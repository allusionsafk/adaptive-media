using System.Collections.Immutable;

namespace AdaptiveMedia;

/// <summary>Tri-state runtime observation. Unknown means the evidence did not
/// establish the value; it is never promoted to Inactive, and Inactive is only
/// used when the runtime demonstrably ran and demonstrably did not do the thing.</summary>
public enum DvObservedState { Unknown, Inactive, Active }

/// <summary>What the pipeline actually delivered to the display. FullEnhancementLayer
/// requires observed composition, never a request for it.</summary>
public enum NativeDvDelivered { Unknown, BaseLayerOnly, FullEnhancementLayer }

// Stable diagnostics identifiers; append rather than renumber.
public enum NativeDvRejection
{
    None = 0, NotDolbyVision = 1, ClassificationIncomplete = 2, UnsupportedProfile = 3,
    EnhancementLayerUnclassified = 4, RpuNotValidated = 5, BaseNotHdr10Compatible = 6,
    InvalidSourceFacts = 7, ExperimentalRuntimeUnavailable = 8, ExperimentalLaneNotEnabled = 9
}

/// <summary>The pinned side-by-side experimental runtime. It is never the stable
/// runtime and is always addressed by absolute path.</summary>
public sealed record NativeDvRuntime(string ExecutablePath, string Sha256, string MpvVersion, int LibplaceboApi)
{
    public const int RequiredLibplaceboApi = 370;
    public bool ComposesEnhancementLayer => LibplaceboApi >= RequiredLibplaceboApi;
}

/// <summary>What the application asked for. Requested values never appear in
/// <see cref="NativeDvObservation"/> and never influence it.</summary>
public sealed record NativeDvRequest(
    string SourcePath,
    int SourceProfile,
    DvEnhancementLayer SourceClassification,
    bool EnhancementLayer,
    NativeDvRuntime Runtime,
    string Renderer,
    string GpuApi,
    string GpuContext,
    string HardwareDecoder);

/// <summary>What the runtime demonstrably did. Every member is derived from
/// evidence; none is derived from <see cref="NativeDvRequest"/>.</summary>
public sealed record NativeDvObservation(
    DvObservedState Renderer,
    DvObservedState Profile7Splitter,
    int DecoderInstances,
    string? BaseLayerDecoder,
    string? EnhancementLayerDecoder,
    DvObservedState BlElPairing,
    DvObservedState Rpu,
    DvObservedState FelComposition,
    DvObservedState HardwareSurfaces,
    string? HardwareDecoderInUse,
    string? GraphicsApi,
    string? GraphicsContext,
    int? LibplaceboApi,
    NativeDvDelivered Delivered,
    ImmutableArray<string> Degradation)
{
    public bool HasDegradation => !Degradation.IsDefaultOrEmpty && Degradation.Length > 0;

    /// <summary>Never claims Full FEL from a request. Reads Unknown when the
    /// renderer was never observed.</summary>
    public string Summary => Delivered switch
    {
        NativeDvDelivered.FullEnhancementLayer =>
            $"Full enhancement-layer composition active ({DecoderInstances} decoder instances, {HardwareDecoderInUse ?? "decoder unknown"}).",
        NativeDvDelivered.BaseLayerOnly =>
            "Base layer only. The enhancement layer was not composed.",
        _ => "Pipeline state unknown; the runtime did not report enough to make a claim.",
    };
}

/// <summary>Immutable native-playback policy result. Only the planner constructs
/// plans. A supported plan plays the original container directly: it never
/// converts, extracts, remuxes, or writes media-sized scratch.</summary>
public sealed class NativeDvPlaybackPlan
{
    internal NativeDvPlaybackPlan(NativeDvRequest? request, bool supported, NativeDvRejection rejection,
        string explanation, ImmutableArray<string> arguments)
    {
        if (supported && request is null) throw new ArgumentException("A supported plan requires a request.", nameof(request));
        if (supported != (rejection == NativeDvRejection.None))
            throw new ArgumentException("Support and rejection must agree.", nameof(rejection));
        Request = request; Supported = supported; Rejection = rejection;
        Explanation = explanation; Arguments = arguments;
    }

    public NativeDvRequest? Request { get; }
    public bool Supported { get; }
    public NativeDvRejection Rejection { get; }
    public string Explanation { get; }
    public ImmutableArray<string> Arguments { get; }
    public string? Executable => Request?.Runtime.ExecutablePath;

    /// <summary>Structural, not advisory. Native playback has no conversion step,
    /// so the Compatibility Export planner and executor are never consulted.</summary>
    public bool RequiresConversion => false;

    /// <summary>Media-sized scratch this plan may create. Always empty: the plan
    /// reads the authored container directly and disables on-disk caching.</summary>
    public ImmutableArray<string> MediaScratchPaths => [];
}

public static class NativeDvPlaybackPlanner
{
    /// <summary>Build a native Profile 7 playback plan, or an explicit rejection.
    /// This lane is opt-in: it is never selected implicitly for a Profile 7 source.</summary>
    public static NativeDvPlaybackPlan Build(DvSourceInfo source, NativeDvRuntime runtime, string sourcePath,
        string configDir, string pipeName, bool experimentalLaneEnabled, bool enhancementLayer = true,
        string hardwareDecoder = "d3d11va", string gpuApi = "d3d11", string gpuContext = "d3d11",
        string? logPath = null, PlaybackTarget? target = null, bool allowUnclassifiedEnhancementLayer = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runtime);

        static NativeDvPlaybackPlan Reject(NativeDvRejection code, string text) =>
            new(null, false, code, text, []);

        if (!experimentalLaneEnabled)
            return Reject(NativeDvRejection.ExperimentalLaneNotEnabled,
                "Native Profile 7 playback is an explicit experimental lane and was not enabled.");
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.IndexOfAny(['\0', '\r', '\n']) >= 0)
            return Reject(NativeDvRejection.InvalidSourceFacts, "The media path contains an unsupported control character.");

        // The experimental runtime must be the pinned side-by-side build, addressed
        // absolutely. The stable runtime is never repurposed for this lane.
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !Path.IsPathRooted(runtime.ExecutablePath))
            return Reject(NativeDvRejection.ExperimentalRuntimeUnavailable, "The experimental runtime must be an absolute path.");
        if (runtime.ExecutablePath.StartsWith(@"C:\mpv\", StringComparison.OrdinalIgnoreCase))
            return Reject(NativeDvRejection.ExperimentalRuntimeUnavailable, "The stable runtime cannot be used for the experimental native lane.");
        if (string.IsNullOrWhiteSpace(runtime.Sha256))
            return Reject(NativeDvRejection.ExperimentalRuntimeUnavailable, "The experimental runtime is not pinned by hash.");
        if (!runtime.ComposesEnhancementLayer)
            return Reject(NativeDvRejection.ExperimentalRuntimeUnavailable,
                $"The experimental runtime reports libplacebo API {runtime.LibplaceboApi}; enhancement-layer composition requires at least {NativeDvRuntime.RequiredLibplaceboApi}.");

        if (source.Detection == DvDetection.NotDetected)
            return Reject(NativeDvRejection.NotDolbyVision, "Source is not classified as Dolby Vision.");
        if (source.Detection != DvDetection.Detected || source.Profile is null)
            return Reject(NativeDvRejection.ClassificationIncomplete, "Dolby Vision classification is incomplete.");
        if (source.Profile != 7)
            return Reject(NativeDvRejection.UnsupportedProfile, "The native lane covers Profile 7 only.");

        var video = source.BaseVideo;
        if (video is null || !video.Known || video.Codec != "hevc" || source.BitDepth != 10 ||
            source.CompatibilityId is not (null or 6))
            return Reject(NativeDvRejection.InvalidSourceFacts, "Base HEVC characteristics are insufficient or inconsistent.");
        if (source.Rpu != DvRpuStatus.Validated)
            return Reject(NativeDvRejection.RpuNotValidated, "Whole-stream RPU validation is required.");
        // MEL/FEL is never inferred from profile 7 or from el_present_flag. Export
        // must refuse an unclassified layer because it decides what gets destroyed.
        // Playback destroys nothing and never claims composition it did not observe,
        // so the caller may opt into playing an unclassified source.
        if (source.EnhancementLayer is not (DvEnhancementLayer.Mel or DvEnhancementLayer.Fel) &&
            !(allowUnclassifiedEnhancementLayer && source.EnhancementLayer == DvEnhancementLayer.Unknown))
            return Reject(NativeDvRejection.EnhancementLayerUnclassified,
                "MEL/FEL classification is required before native Profile 7 playback; it is not inferred from the profile.");
        if (source.Hdr10Base != DvCompatibility.Yes)
            return Reject(NativeDvRejection.BaseNotHdr10Compatible, "This lane requires an HDR10-compatible base.");

        var request = new NativeDvRequest(sourcePath, 7, source.EnhancementLayer, enhancementLayer,
            runtime, "gpu-next", gpuApi, gpuContext, hardwareDecoder);

        var args = ImmutableArray.CreateBuilder<string>();
        // The proof ran with --no-config. Keep that: neither the stable mpv-config
        // nor a user configuration may influence the experimental runtime.
        args.Add("--no-config");
        args.Add("--config-dir=" + configDir);
        args.Add(@"--input-ipc-server=\\.\pipe\" + pipeName);
        args.Add("--vo=gpu-next");
        args.Add("--target-colorspace-hint=auto");
        args.Add("--inverse-tone-mapping=no");
        if (target?.Display?.WindowsHdrPathActive != true)
        {
            args.Add("--target-trc=bt.1886");
            if (target?.Display?.QualifiedWcgPeakNits is int peak && gpuApi == "d3d11" && gpuContext == "d3d11")
            {
                args.Add("--target-prim=display-p3");
                args.Add("--target-peak=" + peak.ToString(System.Globalization.CultureInfo.InvariantCulture));
                args.Add("--hdr-reference-white=" + peak.ToString(System.Globalization.CultureInfo.InvariantCulture));
                args.Add("--tone-mapping=mobius");
                args.Add("--d3d11-output-format=rgba16f");
                args.Add("--d3d11-output-csp=linear");
            }
            else args.Add("--target-prim=bt.709");
        }
        args.Add("--hwdec=" + hardwareDecoder);
        args.Add("--gpu-api=" + gpuApi);
        args.Add("--gpu-context=" + gpuContext);
        // Zero media-sized scratch is a product invariant of this lane, not a
        // laboratory setting: packets stream through bounded RAM only.
        args.Add("--cache-on-disk=no");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            // Composition, BL/EL pairing and the Profile 7 splitter have no structured
            // IPC property in this runtime, so they are read from its own diagnostic
            // log. Debug level carries all three; trace would add a per-frame shader
            // line and make the log grow with playback duration. The session lowers
            // the level over IPC once it has observed, so the log stays bounded.
            args.Add("--log-file=" + logPath);
            args.Add("--msg-level=all=warn,mkv=v,vd=v,vf=v,vo/gpu-next=debug");
        }
        args.Add("--vf=format=enhancement-layer=" + (enhancementLayer ? "yes" : "no"));
        args.Add("--terminal=no");
        if (target is not null)
        {
            args.Add("--screen=" + target.Screen);
            args.Add("--fs-screen=" + target.Screen);
            if (target.Fullscreen) args.Add("--fullscreen");
            else if (target.Width > 0 && target.Height > 0) args.Add($"--autofit={target.Width}x{target.Height}");
        }
        // Audio, subtitles, chapters and attachments come from the original
        // container and are deliberately left enabled. The laboratory harness
        // disables them for measurement isolation; the player must not.
        args.Add("--");
        args.Add(sourcePath);

        return new NativeDvPlaybackPlan(request, true, NativeDvRejection.None,
            source.EnhancementLayer switch
            {
                DvEnhancementLayer.Fel => "Play the authored Profile 7 container directly and request full enhancement-layer composition. No conversion and no media-sized scratch.",
                DvEnhancementLayer.Mel => "Play the authored Profile 7 container directly. The source is MEL, so no enhancement-layer picture contribution is expected.",
                _ => "Play the authored Profile 7 container directly. The enhancement layer is unclassified, so whether it contributes is decided by what playback observes.",
            },
            args.ToImmutable());
    }
}

/// <summary>Reduces runtime evidence into <see cref="NativeDvObservation"/>.
/// Structured IPC values are preferred; composition, pairing and the Profile 7
/// splitter have no structured property in the pinned runtime and arrive from its
/// version-pinned diagnostic output.</summary>
public static class NativeDvObservationReducer
{
    public static NativeDvObservation Reduce(
        bool rendererObserved,
        bool splitterObserved,
        int decoderInstances,
        IReadOnlyList<string>? selectedDecoders,
        bool blElPairObserved,
        bool compositionObserved,
        bool hardwareDecodingObserved,
        bool softwareFallbackObserved,
        bool requestedEnhancementLayer,
        string? hardwareDecoderInUse = null,
        string? graphicsApi = null,
        string? graphicsContext = null,
        int? libplaceboApi = null)
    {
        var degradation = ImmutableArray.CreateBuilder<string>();

        // A renderer that was never observed cannot support a negative claim about
        // anything it would have done.
        DvObservedState Gated(bool observed) => !rendererObserved ? DvObservedState.Unknown
            : observed ? DvObservedState.Active : DvObservedState.Inactive;

        var composition = Gated(compositionObserved);
        var pairing = Gated(blElPairObserved);

        var hardwareSurfaces = softwareFallbackObserved ? DvObservedState.Inactive
            : hardwareDecodingObserved ? DvObservedState.Active : DvObservedState.Unknown;

        var decoders = selectedDecoders ?? [];
        string? baseDecoder = decoders.Count >= 1 ? decoders[0] : null;
        string? enhancementDecoder = decoders.Count >= 2 ? decoders[1] : null;

        if (rendererObserved && decoderInstances < 2)
            degradation.Add("Fewer than two HEVC decoder instances were observed; the enhancement layer was not separately decoded.");
        if (softwareFallbackObserved)
            degradation.Add("Software decoding fallback was observed.");
        if (requestedEnhancementLayer && composition == DvObservedState.Inactive)
            degradation.Add("Full enhancement-layer composition was requested but the renderer did not compose it; this is a base-layer-only result.");
        if (requestedEnhancementLayer && composition == DvObservedState.Unknown)
            degradation.Add("Composition state could not be established; the result is not a Full FEL claim.");

        var delivered = composition switch
        {
            DvObservedState.Active => NativeDvDelivered.FullEnhancementLayer,
            DvObservedState.Inactive => NativeDvDelivered.BaseLayerOnly,
            _ => NativeDvDelivered.Unknown,
        };

        return new NativeDvObservation(
            Renderer: rendererObserved ? DvObservedState.Active : DvObservedState.Unknown,
            Profile7Splitter: splitterObserved ? DvObservedState.Active : DvObservedState.Unknown,
            DecoderInstances: decoderInstances,
            BaseLayerDecoder: baseDecoder,
            EnhancementLayerDecoder: enhancementDecoder,
            BlElPairing: pairing,
            Rpu: splitterObserved ? DvObservedState.Active : DvObservedState.Unknown,
            FelComposition: composition,
            HardwareSurfaces: hardwareSurfaces,
            HardwareDecoderInUse: hardwareDecoderInUse,
            GraphicsApi: graphicsApi,
            GraphicsContext: graphicsContext,
            LibplaceboApi: libplaceboApi,
            Delivered: delivered,
            Degradation: degradation.ToImmutable());
    }
}
