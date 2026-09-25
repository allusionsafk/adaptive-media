using System.Globalization;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>What one attempt's native composition evidence established, reduced
/// to plain facts so the truth surface does not depend on the native lane. Built
/// only from that attempt's own observation.</summary>
public sealed record NativeCompositionFacts(bool RendererActive, int DecoderInstances, bool BaseAndEnhancementPaired,
    bool FelComposed, string Delivered, string? HardwareDecoder, bool RpuProcessed = false);

/// <summary>Everything one attempt established, and nothing it did not. Owned by
/// exactly one attempt: a replacement gets a new instance, so evidence cannot
/// travel between attempts.</summary>
public sealed class PlaybackAttemptEvidence
{
    private readonly object _gate = new();
    private Dictionary<string, JsonElement> _observed = [];
    private DeliveryLatch _latch = DeliveryLatch.Empty;
    private bool _finished;
    private NativeCompositionFacts? _native;
    private PlaybackHealthSnapshot? _health;
    private DisplayCapability? _observedDisplay;
    private Func<PlaybackHealthSnapshot>? _liveHealth;

    public PlaybackAttemptEvidence(long attemptId, int number, PlaybackPlan plan, PlaybackAttemptKind kind, string runtime,
        string? nativeRuntimeVersion = null, bool nativeFelRequested = false)
    {
        AttemptId = attemptId; Number = number; Plan = plan; Kind = kind; Runtime = runtime;
        NativeRuntimeVersion = nativeRuntimeVersion; NativeFelRequested = nativeFelRequested;
    }

    public long AttemptId { get; }
    public int Number { get; }
    public PlaybackPlan Plan { get; }
    public PlaybackAttemptKind Kind { get; }
    public string Runtime { get; }
    public string? NativeRuntimeVersion { get; }
    public bool NativeFelRequested { get; }
    public bool Started { get; set; }

    /// <summary>Replace this attempt's property snapshot. Copied, so later
    /// mutation of the caller's dictionary cannot reach it. A finished attempt
    /// accepts nothing more: a late poll cannot rewrite what it established.</summary>
    public void Observe(IReadOnlyDictionary<string, JsonElement> properties, NativeCompositionFacts? native)
    {
        var copy = properties.ToDictionary(x => x.Key, x => x.Value.Clone());
        lock (_gate)
        {
            if (_finished) return;
            _observed = copy;
            _latch = _latch.Fold(copy);
            if (native is not null) _native = native;
        }
    }

    /// <summary>This attempt's own live health, read until the attempt finishes.</summary>
    public void TrackHealth(Func<PlaybackHealthSnapshot> live) { lock (_gate) _liveHealth = live; }

    public void Finish(PlaybackHealthSnapshot? health) { lock (_gate) { _health = health; _liveHealth = null; _finished = true; } }

    public void ObserveDisplay(DisplayCapability? display)
    {
        lock (_gate) if (!_finished) _observedDisplay = display;
    }

    public DisplayCapability? ObservedDisplay { get { lock (_gate) return _observedDisplay; } }

    /// <summary>Whether each path this attempt's own plan asked for was delivered,
    /// judged only from this attempt's own runtime evidence.</summary>
    public IReadOnlyList<DeliveryVerdict> Delivery()
    {
        Dictionary<string, JsonElement> observed; DeliveryLatch latch; bool finished;
        lock (_gate) { observed = _observed; latch = _latch; finished = _finished; }
        return EnhancementDeliveryVerifier.Verify(Plan, observed, latch, Started, finished);
    }

    public (IReadOnlyDictionary<string, JsonElement> Observed, NativeCompositionFacts? Native, PlaybackHealthSnapshot? Health) Read()
    {
        Func<PlaybackHealthSnapshot>? live;
        lock (_gate)
        {
            if (_health is not null || _liveHealth is null) return (_observed, _native, _health);
            live = _liveHealth;
        }
        return (_observed, _native, live());
    }
}

public sealed record PlaybackTruthSection(string Title, IReadOnlyList<string> Lines);

/// <summary>The user-facing truth chain for one playback: requested, planned,
/// observed, health, recovery, and why. Every observed line comes from the
/// current attempt's own evidence; earlier attempts appear only as history.</summary>
public sealed record PlaybackTruthReport(PlaybackTruthSection Intent, PlaybackTruthSection Requested, PlaybackTruthSection Planned,
    PlaybackTruthSection Observed, PlaybackTruthSection Fallback, PlaybackTruthSection Health, PlaybackTruthSection Recovery,
    PlaybackTruthSection Why, IReadOnlyList<PlaybackTruthSection> History, string Headline, IReadOnlyList<DeliveryVerdict> Delivery)
{
    public IEnumerable<PlaybackTruthSection> Sections => [Intent, Requested, Planned, Observed, Fallback, Health, Recovery, Why];

    /// <summary>The expandable detail text.</summary>
    public string Format()
    {
        var text = new System.Text.StringBuilder();
        foreach (var section in Sections)
        {
            text.AppendLine(section.Title);
            foreach (string line in section.Lines) text.AppendLine("  " + line);
            text.AppendLine();
        }
        if (History.Count > 0)
        {
            text.AppendLine("Earlier attempts in this playback");
            foreach (var attempt in History)
            {
                text.AppendLine("  " + attempt.Title);
                foreach (string line in attempt.Lines) text.AppendLine("    " + line);
            }
        }
        return text.ToString().TrimEnd();
    }
}

/// <summary>Builds the truth chain. Pure: it reads recorded plan facts, attempt
/// evidence, health snapshots and recovery records, and invents nothing. A
/// request is never shown as an observation, and something that can only be
/// planned (driver-side RTX processing, for example) stays under Planned.</summary>
public static class PlaybackTruthBuilder
{
    /// <summary>Earlier attempts retained for one playback. A playback makes at most
    /// four (current native, previous native, stable, compatibility retry).</summary>
    public const int MaximumHistory = 6;

    public static PlaybackTruthReport Build(PlaybackAttemptEvidence current, IReadOnlyList<PlaybackAttemptEvidence> earlier,
        IReadOnlyList<PlaybackRecoveryRecord> recovery, IReadOnlyList<string>? nativeLaneNotes = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var (observed, native, health) = current.Read();
        var delivery = current.Delivery();
        var intent = IntentLines(current.Plan);
        var requested = Requested(current);
        var planned = Planned(current);
        var observedLines = Observed(current.Plan, observed, native, current.Kind != PlaybackAttemptKind.Stable, current.Started, delivery,
            current.ObservedDisplay);
        var fallbackLines = delivery.Where(v => v.State == DeliveryState.FellBack).Select(v => v.Label + " → " + v.Evidence).ToList();
        if (current.Plan.FitPlanned && SmartFitEvidence(observed).Fallback is { } fitFailure)
            fallbackLines.Add(SmartFitOriginalGeometry(observed)
                ? "Smart Fill → original framing (" + fitFailure + ")"
                : "Smart Fill unavailable; renderer framing unverified (" + fitFailure + ")");
        if (fallbackLines.Count == 0) fallbackLines.Add("None observed");
        var healthLines = new List<string> { HealthText.Describe(health?.State ?? SustainedPlaybackHealth.Starting) };
        if (health is { } h && h.WorstCondition is not (SustainedPlaybackHealth.Healthy or SustainedPlaybackHealth.Starting) &&
            h.WorstCondition != h.State)
            healthLines.Add("Earlier in this attempt: " + HealthText.Describe(h.WorstCondition));
        var recoveryLines = RecoveryLines(recovery);
        var why = Why(current, recovery, nativeLaneNotes);
        var history = earlier.Where(x => x.AttemptId != current.AttemptId).TakeLast(MaximumHistory)
            .Select(x => History(x, recovery)).ToArray();
        string headline = "Playback health: " + healthLines[0];
        var lastRecovery = recovery.LastOrDefault();
        if (lastRecovery is not null) headline += "\nRecovery: " + RecoveryOutcome(lastRecovery);
        return new(new("Intent", intent), new("Requested", requested), new("Planned", planned), new("Observed", observedLines),
            new("Fallback", fallbackLines), new("Playback health", healthLines), new("Recovery", recoveryLines),
            new("Why this path?", why), history, headline, delivery);
    }

    private static List<string> IntentLines(PlaybackPlan plan)
    {
        if (plan.Intent is not { } intent) return ["Legacy playback choices"];
        return intent.Mode switch
        {
            EnhancementMode.Reference => ["Preserve the original look", "Source-faithful processing"],
            EnhancementMode.Compatibility => ["Compatibility playback"],
            EnhancementMode.Automatic => [intent.Goal switch
            {
                AutomaticGoal.PreserveOriginal => "Preserve the original look",
                AutomaticGoal.ImproveDetail => "Improve detail",
                AutomaticGoal.SmootherMotion => "Make motion smoother",
                AutomaticGoal.CleanImage => "Clean up compression and banding",
                _ => "Balanced improvement",
            }, "Strength: " + intent.Strength, "Performance: " + SplitWords(intent.Performance.ToString())],
            _ => [intent.Detail switch
            {
                DetailIntent.Maximum => "Maximum detail",
                DetailIntent.Sharper => "Sharper detail",
                DetailIntent.Balanced => "Balanced detail",
                _ => "Preserve source detail",
            }, "Motion: " + SplitWords(intent.Motion.ToString()), "Cleanup: " + SplitWords(intent.Cleanup.ToString()),
                "Performance: " + SplitWords(intent.Performance.ToString())],
        };
    }

    private static string SplitWords(string value) => System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");

    private static List<string> Requested(PlaybackAttemptEvidence attempt)
    {
        var o = attempt.Plan.Requested;
        var lines = new List<string>();
        if (attempt.NativeFelRequested) lines.Add("Native Dolby Vision Profile 7 · enhancement-layer composition requested");
        lines.Add(attempt.Plan.Decision?.Detail switch
        {
            DetailImplementation.NvidiaVpp => "RTX Super Resolution",
            DetailImplementation.Conventional => "High-quality conventional scaling",
            DetailImplementation.None => "No discretionary upscaling",
            _ => o.UpscaleMode switch
            {
                "RtxVsr" => "RTX Super Resolution",
                "HighQuality" => "High-quality scaling",
                "Automatic" => "Automatic scaling",
                _ => "Standard scaling",
            },
        });
        lines.Add(attempt.Plan.Decision?.Motion switch
        {
            MotionImplementation.CadenceCorrected => "Cadence-corrected presentation",
            MotionImplementation.BlendSmooth => "Temporal blend smoothing",
            MotionImplementation.GeneratedMotion => "Generated motion",
            MotionImplementation.NeuralMotion => "Neural motion",
            MotionImplementation.Original => "Original motion",
            _ => o.MotionMode switch { "Smooth" => "Smooth motion", "Gentle" => "Gentle motion", _ => "Native cadence" },
        });
        if (o.RtxHdr) lines.Add("RTX Video HDR");
        if (o.FitMode == "SmartFill") lines.Add("Smart Fill");
        string cleanup = attempt.Plan.Decision?.Cleanup switch
        {
            CleanupImplementation.Gentle => "Gentle",
            CleanupImplementation.Balanced => "Balanced",
            CleanupImplementation.Strong => "Clean",
            CleanupImplementation.Off => "Off",
            _ => o.CleanupMode == "Legacy" ? (o.Cleanup ? "Normal" : "Off") : o.CleanupMode,
        };
        if (cleanup != "Off") lines.Add("Banding reduction: " + cleanup);
        if (attempt.Plan.BitstreamRequested) lines.Add("Compressed audio passthrough");
        if (attempt.Plan.UserResumeAt is double requestedAt)
            lines.Add("User resume near " + PlaybackRecoveryText.Position(requestedAt));
        lines.Add("Preset: " + o.Profile);
        return lines;
    }

    private static string? Argument(PlaybackPlan plan, string name) =>
        plan.Arguments.TakeWhile(x => x != "--").LastOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    private static bool HasProfile(PlaybackPlan plan, string profile) =>
        plan.Arguments.TakeWhile(x => x != "--").Contains("--profile=" + profile);

    private static List<string> Planned(PlaybackAttemptEvidence attempt)
    {
        var plan = attempt.Plan;
        var lines = new List<string>();
        if (attempt.Kind != PlaybackAttemptKind.Stable)
        {
            lines.Add("Native Dolby Vision Profile 7 · verified runtime" + (attempt.NativeRuntimeVersion is { } v ? " " + v : "") +
                (attempt.Kind == PlaybackAttemptKind.NativePrevious ? " (previous)" : ""));
            if (attempt.NativeFelRequested) lines.Add("Enhancement-layer composition requested of the runtime; FEL contribution awaits observation");
        }
        string? hwdec = Argument(plan, "--hwdec");
        lines.Add(hwdec switch
        {
            "d3d11va" => "D3D11VA hardware decoding",
            null when HasProfile(plan, "nvidia") => "NVDEC hardware decoding (automatic fallback)",
            null when HasProfile(plan, "compatibility") => "D3D11VA copy-back decoding (automatic fallback)",
            null => "Automatic hardware decoding",
            _ => "Decoding: " + hwdec,
        });
        string? vf = Argument(plan, "--vf");
        if (vf is not null && vf.Contains("d3d11vpp", StringComparison.Ordinal))
        {
            lines.Add("NVIDIA D3D11 VPP");
            if (plan.RtxSrConstructed) lines.Add($"RTX Super Resolution at {plan.Scale.ToString("0.###", CultureInfo.InvariantCulture)}×");
            if (plan.RtxHdrConstructed)
            {
                lines.Add("RTX Video HDR");
                lines.Add("NVIDIA driver support pending runtime negotiation; frame processing is not directly observable");
            }
        }
        string? vo = Argument(plan, "--vo");
        string? api = Argument(plan, "--gpu-api");
        lines.Add((vo ?? "default renderer") + (api == "d3d11" ? " · Direct3D 11" : HasProfile(plan, "compatibility") ? " · Direct3D 11" : " · Vulkan"));
        if (Argument(plan, "--scale") is { } scale) lines.Add("libplacebo scaling (" + scale + ")");
        if (Argument(plan, "--video-sync") == "display-resample")
            lines.Add(Argument(plan, "--interpolation") != "yes"
                ? "Cadence-corrected presentation (display-resample, no interpolation)"
                : plan.Decision?.Motion == MotionImplementation.BlendSmooth
                    ? "Temporal blend smoothing (display-resample)"
                    : (Argument(plan, "--tscale") == "oversample" ? "Gentle" : "Smooth") + " motion (display-resample)");
        if (Argument(plan, "--deband") == "yes") lines.Add("Banding reduction (deband)");
        if (plan.BitstreamPlanned) lines.Add("Compressed audio passthrough for supported source codecs; endpoint unverified");
        else if (plan.BitstreamRequested) lines.Add("PCM audio; compressed passthrough unavailable on this runtime");
        if (plan.FitPlanned) lines.Add("Smart Fill source-only reframing; processing awaits runtime evidence");
        else if (plan.Requested.FitMode == "SmartFill") lines.Add("Original framing; Smart Fill unavailable on this path");
        if (plan.Color is { } color)
            lines.Add(color.ToneMapToSdr ? "HDR source → SDR renderer target (tone mapping planned)" :
                plan.RtxHdrConstructed ? "Known SDR → RTX Video HDR renderer target requested; processing and output await observation" :
                color.RendererTarget is ColorDelivery.Pq or ColorDelivery.Hlg or ColorDelivery.Hdr10 ? "Source HDR preserved on planned HDR renderer path" :
                color.RendererTarget == ColorDelivery.Unknown ? "Renderer color target unclassified; output awaits observation" :
                "SDR renderer target planned");
        if (plan.RecoveryResumeAt is double recoveryAt)
            lines.Add("Automatic recovery resume near " + PlaybackRecoveryText.Position(recoveryAt));
        else if (plan.UserResumeAt is double userAt)
            lines.Add("User resume near " + PlaybackRecoveryText.Position(userAt));
        return lines;
    }

    private static (int W, int H)? Size(IReadOnlyDictionary<string, JsonElement> observed, string name)
    {
        if (!observed.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        if (!v.TryGetProperty("w", out var w) || !v.TryGetProperty("h", out var h) || !w.TryGetInt32(out int wi) || !h.TryGetInt32(out int hi)) return null;
        return wi > 0 && hi > 0 ? (wi, hi) : null;
    }

    /// <summary>A string property, or the first entry's name of a list-valued one
    /// (mpv 0.41 reports gpu-api and gpu-context as object lists).</summary>
    private static string? Text(IReadOnlyDictionary<string, JsonElement> observed, string name)
    {
        if (!observed.TryGetValue(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.Object &&
            v[0].TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) return n.GetString();
        return null;
    }

    private static bool SmartFitOriginalGeometry(IReadOnlyDictionary<string, JsonElement> observed)
    {
        if (Size(observed, "video-out-params") is null) return false;
        foreach (var name in new[] { "video-zoom", "video-align-x", "video-align-y" })
            if (!observed.TryGetValue(name, out var value) || !value.TryGetDouble(out double actual) ||
                !double.IsFinite(actual) || Math.Abs(actual) > 0.01)
                return false;
        return true;
    }

    private static bool SmartFitOriginalEvidence(IReadOnlyDictionary<string, JsonElement> observed) =>
        observed.TryGetValue("user-data/adaptive/fit", out var fit) && fit.ValueKind == JsonValueKind.Object &&
        fit.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String &&
        state.GetString() == "not-needed" && SmartFitOriginalGeometry(observed);
    private static (bool Active, string? Fallback) SmartFitEvidence(IReadOnlyDictionary<string, JsonElement> observed)
    {
        if (!observed.TryGetValue("user-data/adaptive/fit", out var fit) || fit.ValueKind != JsonValueKind.Object ||
            !fit.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            return (false, null);
        if (state.GetString() == "fallback" && fit.TryGetProperty("reason", out var reason) &&
            reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } why)
            return (false, why.Length <= 120 ? why : why[..120]);
        if (state.GetString() != "active" || !fit.TryGetProperty("samples", out var samples) ||
            !samples.TryGetInt32(out int count) || count <= 0 ||
            !fit.TryGetProperty("zoom", out var claimedZoom) || !claimedZoom.TryGetDouble(out double expected) ||
            !observed.TryGetValue("video-zoom", out var actualZoom) || !actualZoom.TryGetDouble(out double actual) ||
            !double.IsFinite(expected) || !double.IsFinite(actual) || expected <= 0 || Math.Abs(expected - actual) > 0.01 ||
            Size(observed, "video-out-params") is null)
            return (false, null);
        return (true, null);
    }

    /// <summary>Only what this attempt's player reported. Anything not reported is
    /// left out rather than filled in from the plan. Planned enhancement paths are
    /// reported through their delivery verdicts, which come from runtime evidence;
    /// the ones that fell back are listed under Fallback instead.</summary>
    public static List<string> Observed(PlaybackPlan plan, IReadOnlyDictionary<string, JsonElement> observed,
        NativeCompositionFacts? native, bool nativeAttempt, bool started, IReadOnlyList<DeliveryVerdict>? delivery = null,
        DisplayCapability? observedDisplay = null)
    {
        var lines = new List<string>();
        if (!started) { lines.Add("Nothing observed: the player did not start."); return lines; }
        delivery ??= [];
        if (nativeAttempt)
        {
            if (native?.RpuProcessed == true) lines.Add("Dolby Vision RPU metadata processing observed");
            if (native is null || !native.RendererActive) lines.Add("Composition not yet observed");
            else if (native.FelComposed)
            {
                if (native.BaseAndEnhancementPaired) lines.Add($"BL + EL decoded ({native.DecoderInstances} decoder instances)");
                lines.Add("FEL composition observed");
            }
            else lines.Add(native.Delivered == "BaseLayerOnly" ? "Base layer only; the enhancement layer was not composed" : "Composition unknown");
            lines.Add("Dolby Vision display signalling unverified; renderer and metadata evidence do not establish proprietary display mode");
        }
        if (observed.Count > 0)
        {
            foreach (var verdict in delivery.Where(v => v.State is DeliveryState.Verified or DeliveryState.Unverified or DeliveryState.NotNeeded))
                lines.Add(verdict.Describe());
            var pending = delivery.Where(v => v.State == DeliveryState.Pending).Select(v => v.Label).ToArray();
            if (pending.Length > 0) lines.Add("Not yet observed: " + string.Join(", ", pending));
        }
        if (plan.FitPlanned && SmartFitEvidence(observed).Active)
            lines.Add("Smart Fill active: source frames sampled and renderer zoom observed; no pixels generated");
        else if (plan.FitPlanned && SmartFitOriginalEvidence(observed))
            lines.Add("Original framing observed: Smart Fill was unnecessary at the current window size");
        // With no decoder planned there is no verdict, but the decoder in use is still a fact.
        if (!delivery.Any(v => v.Feature == DeliveryFeature.HardwareDecoding))
            switch (Text(observed, "hwdec-current"))
            {
                case null: break;
                case "no" or "": lines.Add("Software decoding"); break;
                case "d3d11va": lines.Add("D3D11VA active"); break;
                case "nvdec": lines.Add("NVDEC active"); break;
                case var other: lines.Add(other + " decoding active"); break;
            }
        string? api = Text(observed, "gpu-api");
        string? vo = Text(observed, "current-vo");
        if (vo is not null || api is not null)
            lines.Add((vo ?? "renderer") + (api is null ? "" : " · " + (api == "d3d11" ? "Direct3D 11" : api == "vulkan" ? "Vulkan" : api)) + " active");
        if (Size(observed, "osd-dimensions") is { } osd) lines.Add($"{osd.W}×{osd.H} output");
        if (observed.TryGetValue("video-target-params", out var target) && target.ValueKind == JsonValueKind.Object &&
            target.TryGetProperty("gamma", out var gamma) && gamma.ValueKind == JsonValueKind.String)
        {
            if (gamma.GetString() is "pq" or "hlg")
            {
                string transfer = gamma.GetString()!.ToUpperInvariant();
                lines.Add("Renderer target " + transfer + "; " +
                    (observedDisplay?.WindowsHdrPathActive == true
                        ? "Windows HDR active on the matched target (read-only query); physical display output unmeasured"
                        : "display HDR state unverified; physical display output unmeasured"));
            }
            else lines.Add("SDR output reported by renderer; physical display output unmeasured");
        }
        if (observedDisplay is { } display &&
            (!observed.TryGetValue("video-target-params", out var transferParams) ||
             transferParams.ValueKind != JsonValueKind.Object ||
             !transferParams.TryGetProperty("gamma", out var outputGamma) ||
             outputGamma.ValueKind != JsonValueKind.String ||
             outputGamma.GetString() is not ("pq" or "hlg")))
            lines.Add(display.WindowsHdrPathActive
                ? "Windows HDR active on the matched target (read-only query); physical display output unmeasured"
                : display.HdrActive == false
                    ? "Windows HDR inactive on the matched target (read-only query); physical display output unmeasured"
                    : "Windows HDR state unverified on the matched target; physical display output unmeasured");
        if (observed.TryGetValue("estimated-display-fps", out var fps) && fps.ValueKind == JsonValueKind.Number && fps.GetDouble() > 0)
            lines.Add($"Display ~{fps.GetDouble():0} Hz");
        if (observed.TryGetValue("audio-out-params", out var audio) && audio.ValueKind == JsonValueKind.Object &&
            audio.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
        {
            string? value = format.GetString();
            if (value is not null && value.StartsWith("spdif-", StringComparison.OrdinalIgnoreCase))
                lines.Add("Compressed audio output reported by player (" + value + "); HDMI transport and Atmos unverified");
            else if (!string.IsNullOrWhiteSpace(value))
                lines.Add("Decoded audio output reported by player (" + value + ")");
        }
        if (lines.Count == 0) lines.Add("Nothing observed yet");
        return lines;
    }

    private static string AttemptName(PlaybackAttemptKind kind) => kind switch
    {
        PlaybackAttemptKind.NativeCurrent => "Native runtime",
        PlaybackAttemptKind.NativePrevious => "Previous verified native runtime",
        _ => "Stable playback",
    };

    /// <summary>What recovery actually did: the path that launched, never the one
    /// merely selected.</summary>
    public static string RecoveryOutcome(PlaybackRecoveryRecord record)
    {
        if (record.Step == nameof(PlaybackRecoveryStep.NoRecoveryAvailable)) return "Unavailable for this player";
        if (record.LaunchedKind is not { } kind) return "Failed: no replacement player started";
        string where = record.LaunchedResumeAt is double at ? " · resumed near " + PlaybackRecoveryText.Position(at) : " · restarted from beginning";
        return (kind == PlaybackAttemptKind.NativePrevious ? "Previous verified native runtime" : "Stable playback") + where;
    }

    private static List<string> RecoveryLines(IReadOnlyList<PlaybackRecoveryRecord> recovery)
    {
        if (recovery.Count == 0) return ["None"];
        return recovery.Select(r => $"{AttemptName(r.FailedKind)} {HealthText.FailureVerb(r.Trigger)} → {RecoveryOutcome(r)}").ToList();
    }

    private static List<string> Why(PlaybackAttemptEvidence current, IReadOnlyList<PlaybackRecoveryRecord> recovery, IReadOnlyList<string>? nativeNotes)
    {
        var lines = new List<string>();
        var plan = current.Plan;
        var last = recovery.LastOrDefault(r => r.LaunchedAttempt == current.AttemptId);
        if (last is not null)
        {
            string target = last.LaunchedKind == PlaybackAttemptKind.NativePrevious ? "Previous verified runtime" : "Stable playback";
            lines.Add($"{target} selected after the {AttemptName(last.FailedKind).ToLowerInvariant()} {HealthText.FailureVerb(last.Trigger)}.");
            lines.Add(last.Explanation);
        }
        if (plan.RtxSrConstructed && plan.Source.Known)
            lines.Add($"RTX Super Resolution selected because a {plan.Source.Height}p source is presented at " +
                $"{Math.Round(plan.Source.Height * plan.Scale):0}p ({plan.Scale.ToString("0.###", CultureInfo.InvariantCulture)}×) on compatible NVIDIA hardware.");
        foreach (string reason in plan.Reasons.Where(r => !r.StartsWith("Resumed at", StringComparison.Ordinal)))
            if (!lines.Contains(reason)) lines.Add(reason);
        if (nativeNotes is not null)
            foreach (string note in nativeNotes) if (!lines.Contains(note)) lines.Add(note);
        if (lines.Count == 0) lines.Add("The requested choices were planned as shown; nothing was changed.");
        return lines;
    }

    private static PlaybackTruthSection History(PlaybackAttemptEvidence attempt, IReadOnlyList<PlaybackRecoveryRecord> recovery)
    {
        var (observed, native, health) = attempt.Read();
        var lines = new List<string>();
        if (attempt.Kind != PlaybackAttemptKind.Stable)
            lines.Add(native is null || !native.RendererActive ? "Composition not observed"
                : native.FelComposed ? "Full FEL observed" : native.Delivered == "BaseLayerOnly" ? "Base layer only" : "Composition unknown");
        // What that attempt itself established; it stays with that attempt.
        var delivery = attempt.Delivery();
        string Names(DeliveryState state) => string.Join(", ", delivery.Where(v => v.State == state).Select(v => v.Label));
        if (Names(DeliveryState.Verified) is { Length: > 0 } verified) lines.Add("Verified: " + verified);
        if (Names(DeliveryState.FellBack) is { Length: > 0 } fellBack) lines.Add("Fell back: " + fellBack);
        lines.Add(health is null ? "Health not established" : HealthText.Describe(health.WorstCondition is SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen
            ? health.WorstCondition : health.State));
        if (recovery.FirstOrDefault(r => r.FailedAttempt == attempt.AttemptId) is { } r) lines.Add("Recovery → " + RecoveryOutcome(r));
        return new($"Attempt {attempt.Number} · {AttemptName(attempt.Kind)}", lines);
    }
}

/// <summary>Calm product wording for health, without the "Playback health:" prefix.</summary>
public static class HealthText
{
    public static string Describe(SustainedPlaybackHealth state) =>
        PlaybackHealthText.Describe(state)["Playback health: ".Length..];

    public static string FailureVerb(string trigger) => trigger switch
    {
        nameof(SustainedPlaybackHealth.Frozen) => "stopped responding",
        nameof(SustainedPlaybackHealth.Stalled) => "stopped making progress",
        nameof(SustainedPlaybackHealth.RuntimeFailure) => "failed",
        _ => "failed",
    };
}
