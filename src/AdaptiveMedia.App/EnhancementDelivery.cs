using System.Globalization;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>An enhancement path the launched player was asked to run.</summary>
public enum DeliveryFeature { HardwareDecoding, NvidiaVpp, RtxSuperResolution, RtxVideoHdr, ConventionalScaling, CadenceCorrection, BlendSmooth, Cleanup }

/// <summary>What one attempt's own runtime evidence established about a planned
/// path. Only <see cref="Verified"/> is a delivery claim; everything else says why
/// it is not one.</summary>
public enum DeliveryState
{
    /// <summary>The player has not yet played enough of this source to judge.</summary>
    Pending,
    /// <summary>The player itself reported the path doing work on this source.</summary>
    Verified,
    /// <summary>Evidence is consistent with the path, but nothing the player or
    /// driver reports can prove it did the work.</summary>
    Unverified,
    /// <summary>Configured, and correctly idle at the current output size.</summary>
    NotNeeded,
    /// <summary>Planned, and the player reported something else doing the job.</summary>
    FellBack,
}

public sealed record DeliveryVerdict(DeliveryFeature Feature, string Label, DeliveryState State, string Evidence)
{
    public string Describe() => Label + ": " + State switch
    {
        DeliveryState.Verified => "verified",
        DeliveryState.Unverified => "unverified",
        DeliveryState.NotNeeded => "not needed",
        DeliveryState.FellBack => "fell back",
        _ => "not yet observed",
    } + (Evidence.Length == 0 ? "" : " (" + Evidence + ")");
}

/// <summary>What the launched argument vector (and the managed profiles it names)
/// asked the player to do. Read from the exact argv of one attempt, so a
/// replacement attempt is judged against its own plan, never its predecessor's.</summary>
public sealed record PlannedDelivery(string? Decoder, bool VppScaling, bool RtxSuperResolution, bool RtxVideoHdr,
    string? Scaler, bool DisplayResample, bool Interpolation, string? Tscale, bool Deband)
{
    public static PlannedDelivery From(PlaybackPlan plan)
    {
        var args = plan.Arguments.TakeWhile(x => x != "--").ToArray();
        string? Last(string name) => args.LastOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        bool managedConfig = !args.Contains("--no-config");
        string? decoder = Last("--hwdec")
            ?? (args.Contains("--profile=compatibility") ? "d3d11va-copy" : args.Contains("--profile=nvidia") ? "nvdec" : managedConfig ? "auto-safe" : null);
        decoder = decoder?.Split(',')[0].Trim();
        if (decoder is "no" or "") decoder = null;
        string vf = Last("--vf") ?? "";
        string opts = Last("--script-opts") ?? "";
        // The runtime script owns the VPP filter on the RTX lane and adds or removes
        // it as the video rectangle changes, so the lane is planned when armed, not
        // only when the launch vector already carried a filter.
        bool srArmed = opts.Contains("adaptive-playback-sr=yes", StringComparison.Ordinal);
        bool hdrArmed = opts.Contains("adaptive-playback-hdr=yes", StringComparison.Ordinal);
        bool vppScale = vf.Contains("d3d11vpp", StringComparison.Ordinal) && vf.Contains("scale=", StringComparison.Ordinal);
        bool resample = Last("--video-sync") == "display-resample";
        return new(decoder, vppScale || srArmed, srArmed || vf.Contains("scaling-mode=nvidia", StringComparison.Ordinal),
            hdrArmed || vf.Contains("nvidia-true-hdr=yes", StringComparison.Ordinal), Last("--scale"), resample,
            resample && Last("--interpolation") == "yes", Last("--tscale"), Last("--deband") == "yes");
    }
}

/// <summary>Facts about the current source that need more than one poll: when it
/// began, whether it has visibly played, and whether frames were ever blended.
/// Reset whenever the source changes, so nothing established for one playlist
/// item can be credited to the next.</summary>
public sealed record DeliveryLatch(string? SourceKey, double? FirstPosition, bool FramesFlowing, bool BlendedFrames, int SourceChanges)
{
    public static readonly DeliveryLatch Empty = new(null, null, false, false, 0);

    /// <summary>How far this source must advance before pass and decoder evidence
    /// is trusted. Renderer pass lists describe the most recent frame; until the
    /// new source has rendered, they may still describe the previous one.</summary>
    public const double FlowingAfterSeconds = 0.25;

    public DeliveryLatch Fold(IReadOnlyDictionary<string, JsonElement> sample)
    {
        string key = DeliveryEvidence.Number(sample, "user-data/adaptive/source-epoch")?.ToString(CultureInfo.InvariantCulture) + "|" +
                     DeliveryEvidence.Number(sample, "playlist-pos")?.ToString(CultureInfo.InvariantCulture);
        var latch = this;
        if (SourceKey is not null && key != SourceKey)
            latch = Empty with { SourceChanges = SourceChanges + 1 };
        latch = latch with { SourceKey = key };
        double? position = DeliveryEvidence.Number(sample, "time-pos");
        if (position is double at)
        {
            if (latch.FirstPosition is null) latch = latch with { FirstPosition = at };
            else if (at - latch.FirstPosition.Value >= FlowingAfterSeconds) latch = latch with { FramesFlowing = true };
        }
        if (latch.FramesFlowing && DeliveryEvidence.MixedFrames(sample) >= 2) latch = latch with { BlendedFrames = true };
        return latch;
    }
}

/// <summary>Reads mpv runtime properties. Everything here is something the player
/// reports about what it is doing now; option values are only ever used to
/// explain a fallback, never to establish delivery.</summary>
public static class DeliveryEvidence
{
    public static double? Number(IReadOnlyDictionary<string, JsonElement> o, string name) =>
        o.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static string? Text(IReadOnlyDictionary<string, JsonElement> o, string name) =>
        o.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Flag(IReadOnlyDictionary<string, JsonElement> o, string name) =>
        o.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static (int W, int H, string Format)? Picture(IReadOnlyDictionary<string, JsonElement> o, string name)
    {
        if (!o.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        if (!v.TryGetProperty("w", out var w) || !v.TryGetProperty("h", out var h) || !w.TryGetInt32(out int wi) || !h.TryGetInt32(out int hi) || wi <= 0 || hi <= 0) return null;
        // The displayed size, after any aspect correction, is what a scaler targets.
        if (v.TryGetProperty("dw", out var dw) && dw.TryGetInt32(out int dwi) && dwi > 0) wi = dwi;
        if (v.TryGetProperty("dh", out var dh) && dh.TryGetInt32(out int dhi) && dhi > 0) hi = dhi;
        string format = v.TryGetProperty("pixelformat", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
        return (wi, hi, format);
    }

    /// <summary>The video rectangle inside the output, from osd-dimensions margins.</summary>
    public static (int W, int H)? VideoRectangle(IReadOnlyDictionary<string, JsonElement> o)
    {
        if (!o.TryGetValue("osd-dimensions", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        int Get(string p) => v.TryGetProperty(p, out var x) && x.TryGetInt32(out int i) ? i : 0;
        int w = Get("w") - Get("ml") - Get("mr"), h = Get("h") - Get("mt") - Get("mb");
        return w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>Descriptions of the render passes that actually ran for the most
    /// recent frame of the given kind ("fresh" or "redraw").</summary>
    public static IReadOnlyList<string> Passes(IReadOnlyDictionary<string, JsonElement> o, string kind)
    {
        if (!o.TryGetValue("vo-passes", out var v) || v.ValueKind != JsonValueKind.Object ||
            !v.TryGetProperty(kind, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var passes = new List<string>();
        foreach (var pass in list.EnumerateArray())
            if (pass.ValueKind == JsonValueKind.Object && pass.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String &&
                pass.TryGetProperty("count", out var c) && c.TryGetInt64(out long count) && count > 0)
                passes.Add(d.GetString()!);
        return passes;
    }

    /// <summary>How many source frames the most recent redraw blended together.</summary>
    public static int MixedFrames(IReadOnlyDictionary<string, JsonElement> o)
    {
        int most = 0;
        foreach (string desc in Passes(o, "redraw"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(desc, @"frame mixing \((\d+) frames?\)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int n)) most = Math.Max(most, n);
        }
        return most;
    }

    /// <summary>A copy of vo-passes with only what delivery needs: pass descriptions
    /// and whether they ran. The per-frame timing samples are dropped so the
    /// session report stays bounded.</summary>
    public static JsonElement CompactPasses(JsonElement passes)
    {
        if (passes.ValueKind != JsonValueKind.Object) return passes.Clone();
        var compact = new Dictionary<string, object>();
        foreach (var kind in passes.EnumerateObject())
            if (kind.Value.ValueKind == JsonValueKind.Array)
                compact[kind.Name] = kind.Value.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object)
                    .Select(p => new
                    {
                        desc = p.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                        count = p.TryGetProperty("count", out var c) && c.TryGetInt64(out long n) ? n : 0,
                    }).ToArray();
        return JsonSerializer.SerializeToElement(compact);
    }
}

/// <summary>Turns one attempt's plan and its own runtime evidence into a verdict
/// per planned path. Pure. The plan decides what is asked about; only the
/// player's runtime reports can answer. Driver-side NVIDIA processing is never
/// reported as verified: the driver acknowledges a request, it does not report
/// that it processed a frame.</summary>
public static class EnhancementDeliveryVerifier
{
    public static IReadOnlyList<DeliveryVerdict> Verify(PlaybackPlan plan, IReadOnlyDictionary<string, JsonElement> observed,
        DeliveryLatch latch, bool started, bool finished)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var planned = PlannedDelivery.From(plan);
        var verdicts = new List<DeliveryVerdict>();
        // A path that was never judged before the attempt ended stays unproven;
        // it is not quietly promoted, and it is not called a fallback either.
        DeliveryVerdict Waiting(DeliveryFeature feature, string label) => !started
            ? new(feature, label, DeliveryState.Unverified, "the player did not start")
            : finished ? new(feature, label, DeliveryState.Unverified, "playback ended before this source had played long enough to observe")
            : new(feature, label, DeliveryState.Pending, "");
        bool flowing = started && latch.FramesFlowing;

        if (planned.Decoder is { } decoder)
        {
            string label = DecoderLabel(decoder);
            string? current = DeliveryEvidence.Text(observed, "hwdec-current");
            if (!flowing) verdicts.Add(Waiting(DeliveryFeature.HardwareDecoding, label));
            else if (current is null)
                verdicts.Add(new(DeliveryFeature.HardwareDecoding, label, DeliveryState.Unverified, "the player reported no video decoder for this source"));
            else if (current is "no" or "")
                verdicts.Add(new(DeliveryFeature.HardwareDecoding, label, DeliveryState.FellBack, "software decoding observed (hwdec-current = no)"));
            else if (decoder is "auto" or "auto-safe" or "auto-copy" || current == decoder)
                verdicts.Add(new(DeliveryFeature.HardwareDecoding, label, DeliveryState.Verified, "hwdec-current = " + current));
            else
                verdicts.Add(new(DeliveryFeature.HardwareDecoding, label, DeliveryState.FellBack, current + " decoding observed instead (hwdec-current = " + current + ")"));
        }

        DeliveryVerdict? vpp = null;
        if (planned.VppScaling)
        {
            const string label = "NVIDIA VPP scaling";
            string? script = DeliveryEvidence.Text(observed, "user-data/adaptive/state");
            var filter = VppFilter(observed);
            var input = DeliveryEvidence.Picture(observed, "video-params");
            var output = DeliveryEvidence.Picture(observed, "video-out-params");
            if (script is not null && script.Contains("could not be applied", StringComparison.Ordinal))
                vpp = new(DeliveryFeature.NvidiaVpp, label, DeliveryState.FellBack, "the VPP filter could not be applied; conventional scaling is used");
            else if (!flowing) vpp = Waiting(DeliveryFeature.NvidiaVpp, label);
            else if (filter is null || !filter.Value.Scales)
                vpp = input is { } source && DeliveryEvidence.VideoRectangle(observed) is { } rect && rect.W <= source.W && rect.H <= source.H
                    ? new(DeliveryFeature.NvidiaVpp, label, DeliveryState.NotNeeded, $"{source.W}×{source.H} video is not upscaled into the current {rect.W}×{rect.H} rectangle")
                    : new(DeliveryFeature.NvidiaVpp, label, DeliveryState.FellBack, "no VPP scaling filter is active in the player");
            else if (input is { } i && output is { } o && (o.W > i.W || o.H > i.H) && o.Format == "d3d11")
                vpp = new(DeliveryFeature.NvidiaVpp, label, DeliveryState.Verified, $"{i.W}×{i.H} → {o.W}×{o.H} on D3D11 video surfaces");
            else
                vpp = new(DeliveryFeature.NvidiaVpp, label, DeliveryState.FellBack,
                    output is { } unscaled ? $"the filter is configured, but the player reported {unscaled.W}×{unscaled.H} {unscaled.Format} output" : "the filter is configured, but no scaled output was reported");
            verdicts.Add(vpp);
        }

        if (planned.RtxSuperResolution)
        {
            const string label = "RTX Video Super Resolution";
            string? ack = DeliveryEvidence.Text(observed, "user-data/adaptive/rtx-sr");
            var filter = VppFilter(observed);
            if (ack is not null && ack.StartsWith("rejected", StringComparison.Ordinal))
                verdicts.Add(new(DeliveryFeature.RtxSuperResolution, label, DeliveryState.FellBack,
                    "the NVIDIA driver refused the request (" + ack["rejected".Length..].TrimStart(':', ' ') + ")"));
            else if (vpp is null or { State: DeliveryState.Pending })
                verdicts.Add(Waiting(DeliveryFeature.RtxSuperResolution, label));
            else if (vpp.State is DeliveryState.NotNeeded or DeliveryState.FellBack)
                verdicts.Add(vpp with { Feature = DeliveryFeature.RtxSuperResolution, Label = label });
            else if (filter is not { NvidiaScaling: true })
                verdicts.Add(new(DeliveryFeature.RtxSuperResolution, label, DeliveryState.FellBack, "VPP is scaling without the NVIDIA scaling mode"));
            else
                verdicts.Add(new(DeliveryFeature.RtxSuperResolution, label, DeliveryState.Unverified, ack == "accepted"
                    ? "NVIDIA VPP scaling is active and the driver accepted the RTX request, but the driver does not report whether Super Resolution processed frames"
                    : "NVIDIA VPP scaling is active, but no driver acknowledgement of the RTX request was observed for this source"));
        }

        if (planned.RtxVideoHdr)
        {
            const string label = "RTX Video HDR";
            string? ack = DeliveryEvidence.Text(observed, "user-data/adaptive/rtx-hdr");
            bool configured = VppFilter(observed) is { TrueHdr: true };
            if (ack is not null && ack.StartsWith("rejected", StringComparison.Ordinal))
                verdicts.Add(new(DeliveryFeature.RtxVideoHdr, label, DeliveryState.FellBack,
                    "the NVIDIA driver refused the request (" + ack["rejected".Length..].TrimStart(':', ' ') + ")"));
            else if (!flowing) verdicts.Add(Waiting(DeliveryFeature.RtxVideoHdr, label));
            else if (!configured)
                verdicts.Add(new(DeliveryFeature.RtxVideoHdr, label, DeliveryState.FellBack, "no RTX Video HDR filter is active in the player"));
            else
                verdicts.Add(new(DeliveryFeature.RtxVideoHdr, label, DeliveryState.Unverified, ack == "accepted"
                    ? "the driver accepted the request, but it does not report whether HDR conversion processed frames; the HDR output tag is set by the player, not measured"
                    : "the filter is configured, but no driver acknowledgement was observed for this source"));
        }

        if (planned.Scaler is { } scaler)
        {
            string label = "High-quality conventional scaling (" + scaler + ")";
            var passes = DeliveryEvidence.Passes(observed, "fresh");
            var video = DeliveryEvidence.Picture(observed, "video-out-params");
            var rect = DeliveryEvidence.VideoRectangle(observed);
            if (!flowing || passes.Count == 0) verdicts.Add(Waiting(DeliveryFeature.ConventionalScaling, label));
            else if (passes.Any(p => p.Contains("upscaling (" + scaler + ")", StringComparison.Ordinal)))
                verdicts.Add(new(DeliveryFeature.ConventionalScaling, label, DeliveryState.Verified, "the renderer ran an upscaling pass with " + scaler));
            else if (video is { } v && rect is { } r && r.W <= v.W && r.H <= v.H)
                verdicts.Add(new(DeliveryFeature.ConventionalScaling, label, DeliveryState.NotNeeded,
                    $"the renderer does not upscale {v.W}×{v.H} video into a {r.W}×{r.H} rectangle"));
            else if (video is null || rect is null)
                verdicts.Add(new(DeliveryFeature.ConventionalScaling, label, DeliveryState.Unverified, "no " + scaler + " pass was reported and the output geometry is unknown"));
            else
                verdicts.Add(new(DeliveryFeature.ConventionalScaling, label, DeliveryState.FellBack, "the renderer upscaled without a " + scaler + " pass"));
        }

        if (planned.DisplayResample)
        {
            bool blend = planned.Interpolation;
            var feature = blend ? DeliveryFeature.BlendSmooth : DeliveryFeature.CadenceCorrection;
            string label = !blend ? "Cadence-corrected presentation" : planned.Tscale == "oversample" ? "Gentle motion" : "Temporal blend smoothing";
            bool? synced = DeliveryEvidence.Flag(observed, "display-sync-active");
            string? sync = DeliveryEvidence.Text(observed, "video-sync");
            double? speed = DeliveryEvidence.Number(observed, "video-speed-correction");
            string timing = speed is double s ? $"display-resample active, video speed ×{s.ToString("0.####", CultureInfo.InvariantCulture)}" : "display-resample active";
            if (!flowing || synced is null) verdicts.Add(Waiting(feature, label));
            else if (synced == false)
                verdicts.Add(new(feature, label, DeliveryState.FellBack, sync == "audio"
                    ? "the player returned to audio-clock timing because display timing was unstable"
                    : "display-synchronised timing is not active (video-sync = " + (sync ?? "unknown") + ")"));
            else if (!blend)
                verdicts.Add(new(feature, label, DeliveryState.Verified, timing));
            else if (DeliveryEvidence.Flag(observed, "interpolation") == false)
                verdicts.Add(new(feature, label, DeliveryState.FellBack, "interpolation was switched off during playback; " + timing));
            else if (latch.BlendedFrames)
                verdicts.Add(new(feature, label, DeliveryState.Verified, timing + ", redraws blended " + Math.Max(2, DeliveryEvidence.MixedFrames(observed)) + " source frames"));
            else
                verdicts.Add(new(feature, label, DeliveryState.Unverified, timing + ", but no blended frames were reported"));
        }

        if (planned.Deband)
        {
            const string label = "Banding reduction";
            var passes = DeliveryEvidence.Passes(observed, "fresh");
            if (!flowing || passes.Count == 0) verdicts.Add(Waiting(DeliveryFeature.Cleanup, label));
            else if (passes.Any(p => p.Contains("debanding", StringComparison.Ordinal)))
                verdicts.Add(new(DeliveryFeature.Cleanup, label, DeliveryState.Verified, "the renderer ran a debanding pass"));
            else
                verdicts.Add(new(DeliveryFeature.Cleanup, label, DeliveryState.FellBack, "the renderer ran no debanding pass"));
        }
        return verdicts;
    }

    public static string DecoderLabel(string decoder) => decoder switch
    {
        "d3d11va" => "D3D11VA hardware decoding",
        "d3d11va-copy" => "D3D11VA copy-back decoding",
        "nvdec" => "NVDEC hardware decoding",
        "nvdec-copy" => "NVDEC copy-back decoding",
        "auto" or "auto-safe" or "auto-copy" => "Automatic hardware decoding",
        _ => "Hardware decoding (" + decoder + ")",
    };

    private readonly record struct Vpp(bool Scales, bool NvidiaScaling, bool TrueHdr);

    /// <summary>The d3d11vpp filter the player currently runs, if enabled.</summary>
    private static Vpp? VppFilter(IReadOnlyDictionary<string, JsonElement> observed)
    {
        if (!observed.TryGetValue("vf", out var vf)) return null;
        IEnumerable<JsonElement> filters = vf.ValueKind switch
        {
            JsonValueKind.Array => vf.EnumerateArray(),
            JsonValueKind.Object => [vf],
            _ => [],
        };
        foreach (var f in filters)
        {
            if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("name", out var n) || n.GetString() != "d3d11vpp") continue;
            if (f.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False) continue;
            string? Param(string name) => f.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            bool scales = double.TryParse(Param("scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 1.001;
            return new(scales, Param("scaling-mode") == "nvidia", Param("nvidia-true-hdr") == "yes");
        }
        return null;
    }
}
