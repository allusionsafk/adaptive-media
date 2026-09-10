using AdaptiveMedia;
using System.Text.Json;
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
var video = new MediaInfo(3840, 2160, 23.976, "hevc", "pq", "bt.2020", PixelFormat: "yuv420p10le");
var mel = new DvSourceInfo(DvDetection.Detected, 7, 6, video, DvCompatibility.Yes, DvEnhancementLayer.Mel, DvRpuStatus.Validated, 10, "Validated fixture facts");
var fel = mel with { EnhancementLayer = DvEnhancementLayer.Fel };
var p81 = mel with { Profile = 8, CompatibilityId = 1, EnhancementLayer = DvEnhancementLayer.None };
var p5 = mel with { Profile = 5, CompatibilityId = 0, Hdr10Base = DvCompatibility.No, EnhancementLayer = DvEnhancementLayer.None };
var m = DvConversionPlanner.Build(mel, DvConversionTarget.Profile81);
Check(m.Supported && m.BaseVideoCopied && !m.PixelsReencoded, "MEL copies base");
Check(m.RpuAction == DvRpuAction.RewriteToProfile81 && m.EnhancementLayerAction == DvEnhancementLayerAction.Discard, "MEL rewrite/discard");
Check(m.ExpectedOutput is { Profile: 8, CompatibilityId: 1, EnhancementLayer: DvEnhancementLayer.None }, "P81 output contract");
var f = DvConversionPlanner.Build(fel, DvConversionTarget.Profile81);
Check(f.Supported && f.Losses.HasFlag(DvLossClassification.FelPictureContributionLost), "FEL explicit loss");
Check(f.Codes.Contains(DvReasonCode.FelPictureContributionNotRetained), "FEL warning mandatory");
var h = DvConversionPlanner.Build(p81, DvConversionTarget.Hdr10);
Check(h.Supported && h.BaseVideoCopied && h.RpuAction == DvRpuAction.Remove, "HDR10 metadata removal");
Check(h.Losses.HasFlag(DvLossClassification.DolbyVisionMetadataLost), "DV metadata loss");
Check(h.ExpectedOutput is { DolbyVision: false, Profile: null, RpuPresent: false }, "HDR10 output contract");
foreach (var target in Enum.GetValues<DvConversionTarget>()) {
    var p = DvConversionPlanner.Build(p5, target);
    Check(!p.Supported && !p.Executable && p.Method == DvConversionMethod.DecodeProcessReencode, "P5 future pixel pipeline");
    Check(p.PixelsReencoded && !p.BaseVideoCopied && p.Acceleration == DvAccelerationRelevance.PotentiallyUsefulForPixelPipeline, "P5 pixel semantics");
}
foreach (var source in new[] {
    mel with { EnhancementLayer = DvEnhancementLayer.Unknown },
    mel with { Hdr10Base = DvCompatibility.Unknown },
    mel with { Hdr10Base = DvCompatibility.No },
    mel with { Rpu = DvRpuStatus.Unknown }, mel with { Rpu = DvRpuStatus.Malformed },
    mel with { Profile = null }, mel with { CompatibilityId = 1 },
    mel with { Detection = DvDetection.NotDetected },
    mel with { Detection = DvDetection.Unknown }, mel with { BitDepth = null },
    mel with { BaseVideo = video with { Transfer = "hlg" } },
    mel with { BaseVideo = video with { Fps = double.NaN } },
    p81 with { CompatibilityId = null }, p81 with { EnhancementLayer = DvEnhancementLayer.Fel }
}) Check(!DvConversionPlanner.Build(source, DvConversionTarget.Profile81).Supported, "Insufficient/conflicting source rejected");
foreach (var source in new[] { mel, fel, p81, p5, mel with { Profile = null } })
foreach (var target in Enum.GetValues<DvConversionTarget>()) {
    var p = DvConversionPlanner.Build(source, target);
    Check(JsonSerializer.Serialize(p) == JsonSerializer.Serialize(DvConversionPlanner.Build(source with { }, target)), "Deterministic value-equivalent input");
    bool p7To81 = source.Profile == 7 && target == DvConversionTarget.Profile81 && p.Supported;
    Check(p.Executable == p7To81 && p.Executable == !p.Codes.Contains(DvReasonCode.ExecutorNotImplemented), "Only implemented P7 to P8.1 plans are executable");
    if (p.Method is DvConversionMethod.StreamCopyMetadataRewrite or DvConversionMethod.StreamCopyEnhancementLayerDiscard)
        Check(p.BaseVideoCopied && !p.PixelsReencoded && p.Acceleration == DvAccelerationRelevance.NotUseful, "Stream-copy invariant");
    if (p.EnhancementLayerAction == DvEnhancementLayerAction.Discard && source.EnhancementLayer == DvEnhancementLayer.Fel)
        Check(p.Losses.HasFlag(DvLossClassification.FelPictureContributionLost) && p.Codes.Contains(DvReasonCode.FelPictureContributionNotRetained), "All FEL discard plans warn");
}
Check(!DvConversionPlanner.Build(mel, (DvConversionTarget)99).Supported, "Unknown target rejected");
Console.WriteLine($"PASS: {checks} Dolby Vision assertions");

// Stable reasons are part of the contract, not just explanatory text.
Check(m.Codes.Contains(DvReasonCode.P7MelToP81StreamCopy), "MEL reason");
Check(f.Codes.Contains(DvReasonCode.P7FelToP81FelDiscarded) && f.BaseVideoCopied && !f.PixelsReencoded, "FEL copy reason");
Check(!m.Losses.HasFlag(DvLossClassification.FelPictureContributionLost), "MEL is not guessed FEL");
Check(DvConversionPlanner.Build(mel with { Hdr10Base = DvCompatibility.Unknown }, DvConversionTarget.Profile81).Codes.Contains(DvReasonCode.BaseHdr10CompatibilityUnknown), "Unknown base reason");
Check(DvConversionPlanner.Build(mel with { EnhancementLayer = DvEnhancementLayer.Unknown }, DvConversionTarget.Profile81).Codes.Contains(DvReasonCode.EnhancementLayerUnknown), "Unknown EL reason");
var nonDv = new DvSourceInfo(DvDetection.NotDetected, null, null, video, DvCompatibility.Yes, DvEnhancementLayer.None, DvRpuStatus.Absent, 10);
Check(!DvConversionPlanner.Build(nonDv, DvConversionTarget.Hdr10).Supported, "Ordinary HDR10 never becomes DV");
Check(typeof(DvConversionPlan).GetConstructors().Length == 0 && typeof(DvConversionPlan).GetProperties().All(p => p.SetMethod is null), "Plans cannot be externally constructed/mutated to erase warnings");

// ---------------------------------------------------------------------------
// Native Profile 7 playback lane: requested state and observed state must stay
// separate, and the lane must never reach the Compatibility Export path.
// ---------------------------------------------------------------------------
var pinned = new NativeDvRuntime(@"D:\pinned\extracted\mpv.com", "b3c7e71e", "0.41.0-1042-g7e4cb538a", 371);
NativeDvPlaybackPlan Native(DvSourceInfo src, NativeDvRuntime? rt = null, bool enabled = true, bool el = true) =>
    NativeDvPlaybackPlanner.Build(src, rt ?? pinned, @"C:\media\authored.mkv", "config", "pipe", enabled, el);

var nativeFel = Native(fel);
Check(nativeFel.Supported && nativeFel.Rejection == NativeDvRejection.None, "Classified P7 FEL is playable natively");
Check(nativeFel.Request is { SourceProfile: 7, EnhancementLayer: true, Renderer: "gpu-next" }, "Native request records what was asked for");
Check(!nativeFel.RequiresConversion && nativeFel.MediaScratchPaths.IsEmpty, "Native playback declares no conversion and no media scratch");
Check(nativeFel.Arguments.Contains("--cache-on-disk=no"), "Zero media scratch is a lane invariant, not a lab setting");
Check(nativeFel.Arguments.Contains("--vf=format=enhancement-layer=yes"), "FEL request must be explicit");
Check(nativeFel.Arguments.Contains("--vo=gpu-next"), "Native lane renders through gpu-next");
int delimiter = nativeFel.Arguments.IndexOf("--");
Check(delimiter >= 0 && nativeFel.Arguments[delimiter + 1] == @"C:\media\authored.mkv" && nativeFel.Arguments.Length == delimiter + 2,
    "The authored container is played unchanged as the final argument");
Check(!nativeFel.Arguments.Any(x => x.Contains("extract", StringComparison.OrdinalIgnoreCase) ||
    x.Contains("encode", StringComparison.OrdinalIgnoreCase) || x.Contains("record", StringComparison.OrdinalIgnoreCase) ||
    x.StartsWith("--o=") || x.StartsWith("--output=")), "Native playback never carries extraction or output arguments");
// The laboratory harness disables audio and subtitles for measurement isolation.
// A media player must not: the original container's tracks are the product.
Check(!nativeFel.Arguments.Contains("--audio=no") && !nativeFel.Arguments.Contains("--sub=no"),
    "Native playback preserves the original container's audio and subtitle tracks");
Check(Native(mel).Supported && Native(mel).Explanation.Contains("MEL"), "MEL plays natively but claims no picture contribution");

// The lane is opt-in and refuses everything it cannot establish.
Check(Native(fel, enabled: false).Rejection == NativeDvRejection.ExperimentalLaneNotEnabled, "Native lane is never selected implicitly");
Check(Native(fel with { EnhancementLayer = DvEnhancementLayer.Unknown }).Rejection == NativeDvRejection.EnhancementLayerUnclassified,
    "MEL/FEL classification is required; profile 7 alone does not imply it");
Check(Native(fel with { Rpu = DvRpuStatus.PresentUnvalidated }).Rejection == NativeDvRejection.RpuNotValidated, "Unvalidated RPU rejected");
Check(Native(p81).Rejection == NativeDvRejection.UnsupportedProfile, "Native lane covers profile 7 only");
Check(Native(p5).Rejection == NativeDvRejection.UnsupportedProfile, "Profile 5 is not in the native lane");
Check(Native(nonDv).Rejection == NativeDvRejection.NotDolbyVision, "Ordinary HDR10 is not routed to the native lane");
Check(Native(fel with { Hdr10Base = DvCompatibility.Unknown }).Rejection == NativeDvRejection.BaseNotHdr10Compatible, "Unknown base rejected");
Check(Native(fel, new NativeDvRuntime(@"C:\mpv\mpv.exe", "x", "v", 371)).Rejection == NativeDvRejection.ExperimentalRuntimeUnavailable,
    "The stable runtime is never repurposed for the experimental lane");
Check(Native(fel, new NativeDvRuntime(@"relative\mpv.com", "x", "v", 371)).Rejection == NativeDvRejection.ExperimentalRuntimeUnavailable,
    "The experimental runtime must be absolute");
Check(Native(fel, pinned with { LibplaceboApi = 369 }).Rejection == NativeDvRejection.ExperimentalRuntimeUnavailable,
    "A runtime below the composing libplacebo API is refused rather than silently degraded");
foreach (var rejected in new[] { Native(fel, enabled: false), Native(p81), Native(nonDv) })
    Check(!rejected.Supported && rejected.Request is null && rejected.Arguments.IsEmpty, "A rejected native plan carries no request and no argv");

// Observation: nothing here may be inferred from the request.
string[] two = ["hevc - HEVC (High Efficiency Video Coding)", "hevc - HEVC (High Efficiency Video Coding)"];
var composed = NativeDvObservationReducer.Reduce(rendererObserved: true, splitterObserved: true, decoderInstances: 2,
    selectedDecoders: two, blElPairObserved: true, compositionObserved: true, hardwareDecodingObserved: true,
    softwareFallbackObserved: false, requestedEnhancementLayer: true, hardwareDecoderInUse: "d3d11va",
    graphicsApi: "d3d11", graphicsContext: "d3d11", libplaceboApi: 371);
Check(composed.Delivered == NativeDvDelivered.FullEnhancementLayer, "Observed composition delivers Full FEL");
Check(composed.FelComposition == DvObservedState.Active && composed.BlElPairing == DvObservedState.Active, "Composition and pairing observed");
Check(composed.EnhancementLayerDecoder is not null && composed.HardwareSurfaces == DvObservedState.Active, "EL decoder and hardware surfaces observed");
Check(!composed.HasDegradation, "A fully composed run reports no degradation");

// Requested but not composed: a base-layer-only result must say so.
var blOnly = NativeDvObservationReducer.Reduce(true, true, 1, ["hevc - HEVC"], false, false, true, false, true);
Check(blOnly.Delivered == NativeDvDelivered.BaseLayerOnly, "Requesting FEL cannot deliver FEL");
Check(blOnly.FelComposition == DvObservedState.Inactive && blOnly.BlElPairing == DvObservedState.Inactive, "Uncomposed run reports Inactive, not Active");
Check(blOnly.EnhancementLayerDecoder is null, "No enhancement-layer decoder is invented");
Check(blOnly.Degradation.Any(x => x.Contains("base-layer-only")), "Base-layer-only fallback is named");
Check(blOnly.Degradation.Any(x => x.Contains("Fewer than two")), "A missing second decoder is named");
Check(!blOnly.Summary.Contains("Full enhancement-layer"), "A base-layer result never reads as Full FEL");

// A renderer that never reported cannot support a negative claim.
var silent = NativeDvObservationReducer.Reduce(false, false, 0, null, false, false, false, false, true);
Check(silent.Delivered == NativeDvDelivered.Unknown, "Absent evidence stays Unknown, never Inactive");
Check(silent.FelComposition == DvObservedState.Unknown && silent.BlElPairing == DvObservedState.Unknown, "Unknown is not promoted");
Check(silent.Rpu == DvObservedState.Unknown && silent.HardwareSurfaces == DvObservedState.Unknown, "Unestablished state stays Unknown");
Check(silent.Degradation.Any(x => x.Contains("could not be established")), "An unknown composition state is not a Full FEL claim");

// Observation is independent of the request in both directions.
var composedUnrequested = NativeDvObservationReducer.Reduce(true, true, 2, two, true, true, true, false, requestedEnhancementLayer: false);
Check(composedUnrequested.Delivered == NativeDvDelivered.FullEnhancementLayer, "Observed composition is reported even when not requested");
var softwarePath = NativeDvObservationReducer.Reduce(true, true, 2, two, true, true, false, true, true);
Check(softwarePath.HardwareSurfaces == DvObservedState.Inactive, "Software fallback is visible");
Check(softwarePath.Degradation.Any(x => x.Contains("Software decoding fallback")), "Software fallback is named");
Check(typeof(NativeDvPlaybackPlan).GetConstructors().Length == 0 && typeof(NativeDvPlaybackPlan).GetProperties().All(x => x.SetMethod is null),
    "Native plans cannot be externally constructed or mutated to erase a fallback");

Console.WriteLine($"PASS: {checks} total Dolby Vision assertions");
