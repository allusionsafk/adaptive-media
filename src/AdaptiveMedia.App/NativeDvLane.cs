using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace AdaptiveMedia;

/// <summary>Bounded facts read from the container before planning. The probe
/// decodes a single frame to a null video output: it never extracts, never
/// remuxes, and never writes media-sized scratch.</summary>
public sealed record NativeDvSourceFacts(int? DolbyVisionProfile, int? DolbyVisionLevel,
    string Codec, string Transfer, string Primaries, int Width = 0, int Height = 0)
{
    public bool IsProfile7 => DolbyVisionProfile == 7;
    public static NativeDvSourceFacts Unknown { get; } = new(null, null, "unknown", "unknown", "unknown");
}

public static class NativeDvSourceProbe
{
    internal const string Marker = "AMDV|";

    /// <summary>The exact one-frame probe argument vector. Nothing here can write
    /// media: there is no output file, no encoder, and no extraction.</summary>
    public static string[] BuildArguments(string sourcePath) =>
    [
        "--no-config", "--load-scripts=no", "--frames=1", "--vo=null", "--ao=null",
        "--terminal=yes", "--quiet", "--cache-on-disk=no",
        "--term-playing-msg=" + Marker +
            "${current-tracks/video/dolby-vision-profile}|${current-tracks/video/dolby-vision-level}|" +
            "${current-tracks/video/codec}|${video-params/gamma}|${video-params/primaries}|" +
            "${width}|${height}",
        "--", sourcePath,
    ];

    public static NativeDvSourceFacts Parse(string output)
    {
        string? line = output.Split('\n').LastOrDefault(x => x.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null) return NativeDvSourceFacts.Unknown;
        string[] parts = line.Trim().Split('|');
        if (parts.Length != 8) return NativeDvSourceFacts.Unknown;
        // mpv prints an unavailable property as its raw name, so only a real integer counts.
        int? profile = int.TryParse(parts[1], out int p) ? p : null;
        int? level = int.TryParse(parts[2], out int l) ? l : null;
        int.TryParse(parts[6], out int width);
        int.TryParse(parts[7], out int height);
        return new(profile, level, parts[3], parts[4], parts[5], width, height);
    }

    public static async Task<NativeDvSourceFacts> ReadAsync(string runtimeExecutable, string sourcePath, TimeSpan timeout)
    {
        if (!File.Exists(sourcePath)) return NativeDvSourceFacts.Unknown;
        try
        {
            var result = await NativeProcess.CaptureAsync(runtimeExecutable, BuildArguments(Path.GetFullPath(sourcePath)), timeout);
            return result.ExitCode == 0 ? Parse(result.Output) : NativeDvSourceFacts.Unknown;
        }
        catch (TimeoutException) { return NativeDvSourceFacts.Unknown; }
        catch (IOException) { return NativeDvSourceFacts.Unknown; }
    }
}

/// <summary>One mpv build whose diagnostic parsing contract has actually been
/// verified. Adding a runtime means adding an entry here on purpose, after
/// checking the patterns against that build.</summary>
public sealed record NativeDvDiagnosticAdapter(string MpvCommit, string MpvVersionToken, string VerifiedOn, string Notes);

/// <summary>The set of runtimes whose diagnostic output this application knows how
/// to read.
///
/// Support is decided from the runtime that is actually running, never from the
/// shipped manifest. If it were taken from the manifest, bumping the manifest
/// would silently re-point these patterns at a build nobody had checked, and a
/// requested Full FEL could become a claimed one. A runtime update must not be
/// able to quietly degrade what the application says it observed.</summary>
public static class NativeDvDiagnosticAdapters
{
    public static ImmutableArray<NativeDvDiagnosticAdapter> Supported { get; } =
    [
        new("7e4cb538a3f30d25920ad8e87ba6571540fb729f", "g7e4cb538a", "2026-09-10",
            "Profile 7 splitter, dual HEVC decode, [vf] [el_pair], and sh_dovi_compose_nlq confirmed against the authored source; composition signal verified to discriminate at debug level."),
        new("14f2d48cbc7dda61adb4bd181e107a1f3f76e533", "g14f2d48cb", "2026-09-12",
            "Re-verified against the authored source: identical diagnostic contract, and sh_dovi_compose_nlq present with the enhancement layer on and absent with it off. Retained alongside the previous entry so a rollback stays readable."),
    ];

    /// <summary>The adapter for the build that is actually running, or null when no
    /// verified adapter covers it.</summary>
    public static NativeDvDiagnosticAdapter? For(string? observedMpvVersion)
    {
        if (string.IsNullOrWhiteSpace(observedMpvVersion)) return null;
        foreach (var adapter in Supported)
            if (observedMpvVersion.Contains(adapter.MpvVersionToken, StringComparison.OrdinalIgnoreCase))
                return adapter;
        return null;
    }

    public static bool SupportsCommit(string? commit) =>
        !string.IsNullOrWhiteSpace(commit) &&
        Supported.Any(x => x.MpvCommit.Equals(commit, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads a verified runtime's own diagnostic output into an observation.
///
/// mpv exposes no structured property for composition, BL/EL pairing, or the
/// Profile 7 splitter, so those three come from log text — and log text is only
/// trustworthy for a build whose contract someone checked. An unverified build
/// leaves every value Unknown rather than being guessed at.</summary>
public static class NativeDvLogEvidence
{
    public static NativeDvObservation Reduce(string? logText, string? observedMpvVersion,
        bool requestedEnhancementLayer, string? hwdecCurrent = null, string? graphicsApi = null,
        string? graphicsContext = null)
    {
        if (NativeDvDiagnosticAdapters.For(observedMpvVersion) is null)
            return NativeDvObservationReducer.Reduce(rendererObserved: false, splitterObserved: false,
                decoderInstances: 0, selectedDecoders: null, blElPairObserved: false, compositionObserved: false,
                hardwareDecodingObserved: false, softwareFallbackObserved: false,
                requestedEnhancementLayer: requestedEnhancementLayer,
                hardwareDecoderInUse: hwdecCurrent, graphicsApi: graphicsApi, graphicsContext: graphicsContext);

        string text = logText ?? string.Empty;
        var apiMatch = Regex.Match(text, @"Initialized libplacebo .*\(API v(?<api>\d+)\)", RegexOptions.IgnoreCase);
        var hwdecMatch = Regex.Match(text, @"\[vd\]\s*Using hardware decoding \((?<name>[^)]+)\)", RegexOptions.IgnoreCase);
        var decoders = Regex.Matches(text, @"\[vd\]\s*Selected decoder:\s*(?<name>[^\r\n]+)", RegexOptions.IgnoreCase)
            .Select(x => x.Groups["name"].Value.Trim()).ToArray();

        // libplacebo only generates the NLQ composition shader when it is actually
        // composing the enhancement layer: the suppressed run emits none. The
        // per-frame shader-timing line would corroborate it but is trace level, and
        // trace makes the log grow with playback duration.
        bool composition = text.Contains("sh_dovi_compose_nlq", StringComparison.OrdinalIgnoreCase);

        return NativeDvObservationReducer.Reduce(
            rendererObserved: apiMatch.Success,
            splitterObserved: Regex.IsMatch(text, @"Dolby Vision Profile 7 splitter: BL stream \d+, virtual EL stream \d+ \(dependent_track\)"),
            decoderInstances: Regex.Matches(text, @"\[vd\][^\r\n]*Opening decoder hevc\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count,
            selectedDecoders: decoders,
            blElPairObserved: Regex.IsMatch(text, @"\[vf\]\s*\[el_pair\]", RegexOptions.IgnoreCase),
            compositionObserved: composition,
            hardwareDecodingObserved: hwdecMatch.Success,
            // '[vo/gpu-next] Loading failed.' is gpu-next declining an optional hwdec
            // interop driver after another succeeded; it is not a software fallback
            // and must not be matched here.
            softwareFallbackObserved: Regex.IsMatch(text, @"\[vd\].*Using software decoding\.", RegexOptions.IgnoreCase),
            requestedEnhancementLayer: requestedEnhancementLayer,
            hardwareDecoderInUse: hwdecCurrent ?? (hwdecMatch.Success ? hwdecMatch.Groups["name"].Value.Trim() : null),
            graphicsApi: graphicsApi,
            graphicsContext: graphicsContext,
            libplaceboApi: apiMatch.Success ? int.Parse(apiMatch.Groups["api"].Value) : null);
    }
}

/// <summary>Whether the native lane was selected, and if not, exactly why.</summary>
public sealed record NativeDvLaneOutcome(
    bool Selected,
    NativeDvRuntimeState RuntimeState,
    NativeDvPlaybackPlan? Plan,
    NativeDvRuntime? Runtime,
    NativeDvSourceFacts SourceFacts,
    string? Reason)
{
    public static NativeDvLaneOutcome NotSelected(NativeDvRuntimeState state, string reason,
        NativeDvSourceFacts? facts = null) =>
        new(false, state, null, null, facts ?? NativeDvSourceFacts.Unknown, reason);
}

/// <summary>Decides whether native Profile 7 playback applies, and makes the
/// provisioned runtime available to it.
///
/// Selection is deliberately narrow: the lane is opt-in, applies only to a
/// Profile 7 source, and produces a plan only once a runtime has validated. It
/// never consults the Compatibility Export planner or executor, and never runs the
/// evidence adapter, which extracts an elementary stream and would write
/// media-sized scratch.</summary>
public sealed class NativeDvLane
{
    public const string DescriptorFileName = "native-dv-runtime.json";

    private readonly NativeDvRuntimeStore _store;
    private readonly NativeDvRuntimeLifecycle _lifecycle;
    private readonly NativeDvRuntimeDescriptor? _descriptor;

    public NativeDvLane(NativeDvRuntimeStore store, NativeDvRuntimeDescriptor? descriptor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lifecycle = new NativeDvRuntimeLifecycle(_store);
        _descriptor = descriptor;
    }

    public NativeDvRuntimeDescriptor? Descriptor => _descriptor;
    public NativeDvRuntimeLifecycle Lifecycle => _lifecycle;

    /// <summary>Lifecycle status without preparing playback, for the developer
    /// setting and diagnostics.</summary>
    public Task<NativeDvLifecycleStatus> GetStatusAsync(AppSettings settings, CancellationToken cancellation = default) =>
        _lifecycle.ReconcileAsync(_descriptor, settings.AllowNativeDolbyVisionDownload, null, cancellation);

    /// <summary>Load the descriptor the application ships. It is the very same
    /// pinned manifest the experiment used, staged beside the executable, so the
    /// product and the proof cannot describe different runtimes.</summary>
    public static NativeDvRuntimeDescriptor? LoadDescriptor(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, DescriptorFileName);
        try
        {
            if (!File.Exists(path)) return null;
            return NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (UriFormatException) { return null; }
    }

    public static NativeDvLane CreateDefault() =>
        new(new NativeDvRuntimeStore(NativeDvRuntimeStore.DefaultRootPath), LoadDescriptor());

    /// <summary>The lifecycle result of the most recent preparation.</summary>
    public NativeDvLifecycleStatus? LastStatus { get; private set; }

    private static NativeDvRuntimeState MapRuntimeState(NativeDvLifecycleState state) => state switch
    {
        NativeDvLifecycleState.ProvisioningNotAllowed => NativeDvRuntimeState.ProvisioningNotAllowed,
        NativeDvLifecycleState.UnsupportedRuntime => NativeDvRuntimeState.UnsupportedRuntime,
        NativeDvLifecycleState.DescriptorUnavailable => NativeDvRuntimeState.DescriptorUnavailable,
        NativeDvLifecycleState.VerificationFailed => NativeDvRuntimeState.ComponentHashMismatch,
        _ => NativeDvRuntimeState.NotInstalled,
    };

    public async Task<NativeDvLaneOutcome> PrepareAsync(string sourcePath, AppSettings settings,
        string configDir, string pipeName, string? logPath, PlaybackTarget? target,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.NativeDolbyVisionLane)
            return NativeDvLaneOutcome.NotSelected(NativeDvRuntimeState.NotInstalled,
                "Native Dolby Vision playback is turned off.");
        if (_descriptor is null)
            return NativeDvLaneOutcome.NotSelected(NativeDvRuntimeState.DescriptorUnavailable,
                "This build does not describe a native Dolby Vision runtime.");

        // Reconcile before probing. A probe needs the runtime anyway, and installing
        // for a source that turns out not to be Profile 7 would be wasted work.
        // Reconciliation also retains the outgoing generation and removes only
        // superseded ones.
        var status = await _lifecycle.ReconcileAsync(_descriptor, settings.AllowNativeDolbyVisionDownload, progress, cancellation);
        LastStatus = status;
        if (status.Runtime is null)
            return NativeDvLaneOutcome.NotSelected(MapRuntimeState(status.State), status.Reason ?? status.Summary);
        var resolution = new NativeDvRuntimeResolution(NativeDvRuntimeState.Installed, status.Runtime,
            _store.GenerationPath(_descriptor), null);

        var facts = await NativeDvSourceProbe.ReadAsync(resolution.Runtime!.ExecutablePath, sourcePath, TimeSpan.FromSeconds(30));
        if (!facts.IsProfile7)
            return NativeDvLaneOutcome.NotSelected(NativeDvRuntimeState.Installed,
                "This source is not Dolby Vision Profile 7.", facts);

        // The probe establishes Profile 7 but not MEL versus FEL. Classifying that
        // up front would mean extracting an elementary stream, which is exactly the
        // media-sized scratch this lane exists to avoid. Playback destroys nothing,
        // so the layer is left unclassified and what it contributes is decided by
        // what the run actually observes.
        var source = new DvSourceInfo(DvDetection.Detected, facts.DolbyVisionProfile, 6,
            new MediaInfo(Width: facts.Width, Height: facts.Height, Codec: facts.Codec,
                Transfer: facts.Transfer, Primaries: facts.Primaries),
            DvCompatibility.Yes, DvEnhancementLayer.Unknown, DvRpuStatus.Validated, 10,
            "Bounded one-frame runtime probe: dolby-vision-profile reported by the pinned runtime.");

        var plan = NativeDvPlaybackPlanner.Build(source, resolution.Runtime, Path.GetFullPath(sourcePath),
            configDir, pipeName, experimentalLaneEnabled: true, enhancementLayer: true,
            logPath: logPath, target: target, allowUnclassifiedEnhancementLayer: true);
        if (!plan.Supported)
            return NativeDvLaneOutcome.NotSelected(resolution.State, plan.Explanation, facts);

        return new NativeDvLaneOutcome(true, resolution.State, plan, resolution.Runtime, facts, null);
    }
}
