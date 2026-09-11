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


// ---------------------------------------------------------------------------
// Native Profile 7 runtime provisioning. A runtime is only usable when every
// pinned component validated; a partially provisioned tree must never be
// exposed, and nothing may install outside the application's own root.
// ---------------------------------------------------------------------------
string Sha256Of(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
byte[] exeBytes = System.Text.Encoding.UTF8.GetBytes("pinned-mpv-executable-payload");
byte[] comBytes = System.Text.Encoding.UTF8.GetBytes("pinned-mpv-console-launcher");
byte[] archiveBytes = System.Text.Encoding.UTF8.GetBytes("pinned-archive-payload-bytes");

string ManifestJson(int placeboApi = 371) => $$"""
{
  "schemaVersion": 1,
  "provider": { "name": "test-provider", "releaseUrl": "https://example.invalid/release" },
  "archive": { "name": "runtime.7z", "url": "https://example.invalid/runtime.7z",
               "bytes": {{archiveBytes.Length}}, "sha256": "{{Sha256Of(archiveBytes)}}" },
  "runtime": {
    "executable": { "pathRelativeToManifest": "../../.artifacts/x/extracted/mpv.exe",
                    "bytes": {{exeBytes.Length}}, "sha256": "{{Sha256Of(exeBytes)}}" },
    "consoleLauncher": { "pathRelativeToManifest": "../../.artifacts/x/extracted/mpv.com",
                         "bytes": {{comBytes.Length}}, "sha256": "{{Sha256Of(comBytes)}}" }
  },
  "mpv": { "version": "0.41.0-1042-g7e4cb538a", "commit": "7e4cb538a3f30d25920ad8e87ba6571540fb729f" },
  "libplacebo": { "apiVersion": {{placeboApi}} }
}
""";

var descriptor = NativeDvRuntimeDescriptor.FromManifestJson(ManifestJson());
Check(descriptor.VersionId == Sha256Of(archiveBytes), "The runtime generation is identified by its archive content, not its name");
Check(descriptor.LauncherRelativePath == "mpv.exe", "The windowed executable is what the product launches");
Check(descriptor.Components.Length == 2, "Every pinned component is carried");
Check(descriptor.LibplaceboApi == 371 && descriptor.MpvCommit.StartsWith("7e4cb538a"), "Runtime identity is parsed");

// The shipped descriptor is the same pinned manifest the proof used, so parsing
// the real committed file is the anti-drift regression.
string realManifest = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "native-dv-p7", "runtime-manifest.json");
if (File.Exists(realManifest))
{
    var real = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(realManifest));
    Check(real.LauncherRelativePath == "mpv.exe", "The shipped manifest resolves a launcher inside the extracted tree");
    Check(real.Components.Length == 2 && real.LibplaceboApi >= NativeDvRuntime.RequiredLibplaceboApi,
        "The shipped manifest pins components and a composing libplacebo API");
    Check(real.ArchiveUrl.Scheme == "https" && real.ArchiveSha256.Length == 64, "The shipped manifest pins an https archive by SHA-256");
}

Check(Throws(() => NativeDvRuntimeDescriptor.FromManifestJson(ManifestJson().Replace(Sha256Of(exeBytes), "not-a-hash"))),
    "A component without a pinned SHA-256 is refused");
Check(Throws(() => NativeDvRuntimeDescriptor.FromManifestJson(ManifestJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"))),
    "An unknown manifest schema is refused");

static bool Throws(Action action) { try { action(); return false; } catch { return true; } }

string testRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-native-dv-" + Guid.NewGuid().ToString("N"));
try
{
    string root = Path.Combine(testRoot, "runtimes", "native-dv");
    int fetchCount = 0, extractCount = 0;

    NativeDvArchiveFetch Fetch(byte[] payload) => (uri, destination, token) =>
    { fetchCount++; File.WriteAllBytes(destination, payload); return Task.CompletedTask; };
    NativeDvArchiveExtract Extract(bool writeExe = true, bool writeCom = true, byte[]? exeOverride = null) => (archive, destination, token) =>
    {
        extractCount++;
        if (writeExe) File.WriteAllBytes(Path.Combine(destination, "mpv.exe"), exeOverride ?? exeBytes);
        if (writeCom) File.WriteAllBytes(Path.Combine(destination, "mpv.com"), comBytes);
        return Task.CompletedTask;
    };

    var store = new NativeDvRuntimeStore(root, Fetch(archiveBytes), Extract());
    Check(store.Resolve(descriptor).State == NativeDvRuntimeState.NotInstalled, "An empty store reports the runtime as not installed");
    Check(!store.Resolve(descriptor).IsUsable, "A missing runtime is never usable");

    // Provisioning is refused outright when the user has not allowed a download.
    var refused = await store.ProvisionAsync(descriptor, allowDownload: false);
    Check(refused.State == NativeDvRuntimeState.ProvisioningNotAllowed, "Provisioning does not happen without permission");
    Check(fetchCount == 0, "A refused provision never reaches the network");

    // First provision.
    var provisioned = await store.ProvisionAsync(descriptor, allowDownload: true);
    Check(provisioned.State == NativeDvRuntimeState.Installed && provisioned.IsUsable, "A complete, verified runtime installs");
    Check(provisioned.Runtime!.ExecutablePath == Path.Combine(root, descriptor.VersionId, "mpv.exe"), "The launcher resolves inside its own generation");
    Check(File.Exists(provisioned.Runtime.ExecutablePath), "The promoted runtime is really on disk");
    Check(fetchCount == 1 && extractCount == 1, "Provisioning downloads and extracts exactly once");

    // Already-installed fast path: no second download.
    var reused = await store.ProvisionAsync(descriptor, allowDownload: true);
    Check(reused.IsUsable && fetchCount == 1 && extractCount == 1, "An installed runtime is reused without downloading again");

    // Reuse still validates: corrupting an installed component must be detected.
    string installedExe = provisioned.Runtime.ExecutablePath;
    byte[] good = File.ReadAllBytes(installedExe);
    File.WriteAllBytes(installedExe, System.Text.Encoding.UTF8.GetBytes(new string('x', good.Length)));
    var corrupted = store.Resolve(descriptor);
    Check(corrupted.State == NativeDvRuntimeState.ComponentHashMismatch && !corrupted.IsUsable,
        "A tampered installed component is detected on reuse, not trusted because the file exists");
    File.Delete(installedExe);
    Check(store.Resolve(descriptor).State == NativeDvRuntimeState.Incomplete, "A missing component makes the runtime incomplete");
    File.WriteAllBytes(installedExe, good);
    Check(store.Resolve(descriptor).IsUsable, "Restoring the component restores the runtime");

    // A wrong archive is never opened.
    string root2 = Path.Combine(testRoot, "store2");
    int extract2 = 0;
    var badArchive = new NativeDvRuntimeStore(root2, Fetch(System.Text.Encoding.UTF8.GetBytes("wrong-archive-payload-xx")),
        (a, d, t) => { extract2++; return Task.CompletedTask; });
    var archiveResult = await badArchive.ProvisionAsync(descriptor, allowDownload: true);
    Check(archiveResult.State == NativeDvRuntimeState.ArchiveHashMismatch, "An archive that fails its pinned hash is rejected");
    Check(extract2 == 0, "A failed archive is never extracted");
    Check(!Directory.Exists(Path.Combine(root2, descriptor.VersionId)), "A failed provision promotes nothing");

    // An incomplete extraction must not be promoted.
    string root3 = Path.Combine(testRoot, "store3");
    var incomplete = new NativeDvRuntimeStore(root3, Fetch(archiveBytes), Extract(writeCom: false));
    var incompleteResult = await incomplete.ProvisionAsync(descriptor, allowDownload: true);
    Check(incompleteResult.State == NativeDvRuntimeState.Incomplete, "An extraction missing a component is incomplete");
    Check(!Directory.Exists(Path.Combine(root3, descriptor.VersionId)), "An incomplete runtime is never promoted");
    Check(!incomplete.Resolve(descriptor).IsUsable, "A partially provisioned runtime is never exposed as valid");

    // A component of the right size but wrong content must not be promoted.
    string root4 = Path.Combine(testRoot, "store4");
    var tampered = new NativeDvRuntimeStore(root4, Fetch(archiveBytes),
        Extract(exeOverride: System.Text.Encoding.UTF8.GetBytes(new string('y', exeBytes.Length))));
    var tamperedResult = await tampered.ProvisionAsync(descriptor, allowDownload: true);
    Check(tamperedResult.State == NativeDvRuntimeState.ComponentHashMismatch, "Right size and wrong content is still a mismatch");
    Check(!Directory.Exists(Path.Combine(root4, descriptor.VersionId)), "A tampered runtime is never promoted");

    // A download failure is reported, not thrown at the caller.
    string root5 = Path.Combine(testRoot, "store5");
    var offline = new NativeDvRuntimeStore(root5, (u, d, t) => throw new IOException("offline"), Extract());
    Check((await offline.ProvisionAsync(descriptor, allowDownload: true)).State == NativeDvRuntimeState.DownloadUnavailable,
        "An unavailable download is a reported state, not an exception");

    // Cancellation leaves nothing behind.
    string root6 = Path.Combine(testRoot, "store6");
    using var cancelled = new CancellationTokenSource();
    var cancelStore = new NativeDvRuntimeStore(root6, (u, d, t) => { cancelled.Cancel(); File.WriteAllBytes(d, archiveBytes); return Task.CompletedTask; }, Extract());
    var cancelResult = await cancelStore.ProvisionAsync(descriptor, allowDownload: true, null, cancelled.Token);
    Check(cancelResult.State == NativeDvRuntimeState.Cancelled, "Cancellation is reported as cancellation");
    Check(!Directory.Exists(Path.Combine(root6, descriptor.VersionId)), "A cancelled provision promotes nothing");

    // Interrupted staging is recoverable and never mistaken for an install.
    string root7 = Path.Combine(testRoot, "store7");
    string orphan = Path.Combine(root7, NativeDvRuntimeStore.StagingDirectoryName, "interrupted");
    Directory.CreateDirectory(orphan);
    File.WriteAllBytes(Path.Combine(orphan, "mpv.exe"), exeBytes);
    var recovering = new NativeDvRuntimeStore(root7, Fetch(archiveBytes), Extract());
    Check(recovering.Resolve(descriptor).State == NativeDvRuntimeState.NotInstalled, "An abandoned staging tree is not an installed runtime");
    Check(recovering.CleanupStaging() == 1, "Abandoned staging generations are cleaned up");
    var afterRecovery = await recovering.ProvisionAsync(descriptor, allowDownload: true);
    Check(afterRecovery.IsUsable, "Provisioning succeeds after an interrupted attempt");
    Check(recovering.CleanupStaging() == 0 && Directory.Exists(Path.Combine(root7, descriptor.VersionId)),
        "Cleanup removes staging only, never a promoted generation");

    // Promotion conflict: another instance already occupied the generation with
    // something that does not validate. The loser must report the failure rather
    // than overwrite or claim success.
    string rootRace = Path.Combine(testRoot, "race");
    string occupied = Path.Combine(rootRace, descriptor.VersionId);
    Directory.CreateDirectory(occupied);
    File.WriteAllBytes(Path.Combine(occupied, "mpv.exe"), System.Text.Encoding.UTF8.GetBytes("squatter"));
    var racing = new NativeDvRuntimeStore(rootRace, Fetch(archiveBytes), Extract());
    var raceResult = await racing.ProvisionAsync(descriptor, allowDownload: true);
    Check(raceResult.State == NativeDvRuntimeState.PromotionFailed && !raceResult.IsUsable,
        "A generation already occupied by an invalid tree fails promotion instead of claiming success");
    Check(File.ReadAllBytes(Path.Combine(occupied, "mpv.exe")).Length == 8,
        "A failed promotion never overwrites what was already there");

    // A runtime that cannot compose is refused even when every file validates.
    var oldPlacebo = NativeDvRuntimeDescriptor.FromManifestJson(ManifestJson(placeboApi: 369));
    string root8 = Path.Combine(testRoot, "store8");
    var weak = new NativeDvRuntimeStore(root8, Fetch(archiveBytes), Extract());
    var weakResult = await weak.ProvisionAsync(oldPlacebo, allowDownload: true);
    Check(weakResult.State == NativeDvRuntimeState.UnsupportedRuntime && !weakResult.IsUsable,
        "A runtime below the composing libplacebo API is refused rather than silently degraded");

    // Nothing may be installed outside the application's own root.
    Check(Directory.GetDirectories(testRoot).All(x => Path.GetFileName(x) is "runtimes" or "store2" or "store3" or "store4" or "store5" or "store6" or "store7" or "store8" or "race" or "lane" or "blocked"),
        "The store writes only inside the roots it was given");
    Check(!Directory.Exists(@"C:\mpv\" + descriptor.VersionId), "Provisioning never installs into the stable runtime location");

    // A component path that escapes its generation is refused.
    string escaping = ManifestJson().Replace("../../.artifacts/x/extracted/mpv.exe", "../../.artifacts/x/extracted/../../../escape.exe");
    var escapeDescriptor = NativeDvRuntimeDescriptor.FromManifestJson(escaping);
    Check(Throws(() => new NativeDvRuntimeStore(Path.Combine(testRoot, "store9"), Fetch(archiveBytes), Extract())
        .Resolve(escapeDescriptor with { VersionId = descriptor.VersionId })) ||
        !Directory.Exists(Path.Combine(testRoot, "store9")),
        "A component path that escapes the runtime directory is refused");

    // ---------------------------------------------------------------------
    // Lane selection: opt-in, and explicit about every refusal.
    // ---------------------------------------------------------------------
    var laneStore = new NativeDvRuntimeStore(Path.Combine(testRoot, "lane"), Fetch(archiveBytes), Extract());
    var lane = new NativeDvLane(laneStore, descriptor);
    var off = await lane.PrepareAsync("nonexistent.mkv", new AppSettings { NativeDolbyVisionLane = false },
        "config", "pipe", null, null);
    Check(!off.Selected && off.Plan is null, "The native lane is never selected implicitly");
    Check(off.Reason!.Contains("turned off"), "A disabled lane says so");

    var noDescriptor = new NativeDvLane(laneStore, null);
    var missing = await noDescriptor.PrepareAsync("nonexistent.mkv", new AppSettings { NativeDolbyVisionLane = true },
        "config", "pipe", null, null);
    Check(missing.RuntimeState == NativeDvRuntimeState.DescriptorUnavailable && missing.Plan is null,
        "A build with no runtime description cannot select the lane");

    var blockedStore = new NativeDvRuntimeStore(Path.Combine(testRoot, "blocked"), Fetch(archiveBytes), Extract());
    var blocked = await new NativeDvLane(blockedStore, descriptor).PrepareAsync("nonexistent.mkv",
        new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = false }, "config", "pipe", null, null);
    Check(blocked.RuntimeState == NativeDvRuntimeState.ProvisioningNotAllowed && blocked.Plan is null,
        "Without a runtime and without download permission the lane refuses");

    Check(NativeDvLane.LoadDescriptor(Path.Combine(testRoot, "no-such-descriptor.json")) is null,
        "A missing descriptor file is absent, not an exception");
}
finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }

// ---------------------------------------------------------------------------
// Product observation: the version-pinned log adapter.
// ---------------------------------------------------------------------------
const string pinnedCommit = "7e4cb538a3f30d25920ad8e87ba6571540fb729f";
string composedLog = string.Join("\n",
    "[   0.016][v][mkv] Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track).",
    "[   0.285][v][vd] Opening decoder hevc",
    "[   0.286][v][vd] Selected decoder: hevc - HEVC (High Efficiency Video Coding)",
    "[   0.286][v][vd] Opening decoder hevc",
    "[   0.287][v][vd] Selected decoder: hevc - HEVC (High Efficiency Video Coding)",
    "[   0.286][v][vo/gpu-next] Loading hwdec driver 'd3d11-egl'",
    "[   0.286][v][vo/gpu-next] Loading failed.",
    "[   0.318][i][vd] Using hardware decoding (d3d11va).",
    "[   0.346][v][vf] [el_pair] 3840x2160 d3d11[p010] dolbyvision/bt.2020/pq/limited/display",
    "[   0.280][v][vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)",
    "[   0.534][d][vo/gpu-next/libplacebo] [233] /* sh_dovi_compose_nlq */");
string suppressedLog = composedLog.Replace("[   0.534][d][vo/gpu-next/libplacebo] [233] /* sh_dovi_compose_nlq */", "");

var productComposed = NativeDvLogEvidence.Reduce(composedLog, "mpv v0.41.0-1042-g7e4cb538a", pinnedCommit, true, "d3d11va");
Check(productComposed.Delivered == NativeDvDelivered.FullEnhancementLayer, "The product reports Full FEL only from observed composition");
Check(productComposed.BlElPairing == DvObservedState.Active && productComposed.DecoderInstances == 2, "BL/EL pairing and both decoders are observed");
Check(productComposed.HardwareSurfaces == DvObservedState.Active, "Declining an optional hwdec interop driver is not a software fallback");
Check(!productComposed.HasDegradation, "A fully composed product session reports no degradation");
Check(productComposed.LibplaceboApi == 371, "The renderer API is observed");

var productSuppressed = NativeDvLogEvidence.Reduce(suppressedLog, "mpv v0.41.0-1042-g7e4cb538a", pinnedCommit, true, "d3d11va");
Check(productSuppressed.Delivered == NativeDvDelivered.BaseLayerOnly, "Without observed composition the product reports base layer only");
Check(productSuppressed.Degradation.Any(x => x.Contains("base-layer-only")), "The product names the base-layer-only fallback");
Check(!productSuppressed.Summary.Contains("Full enhancement-layer"), "A base-layer product session never reads as Full FEL");

// A different build invalidates the patterns, so nothing may be claimed from them.
var wrongBuild = NativeDvLogEvidence.Reduce(composedLog, "mpv v0.40.0-1-gdeadbeef", pinnedCommit, true, "d3d11va");
Check(wrongBuild.Delivered == NativeDvDelivered.Unknown, "Log patterns from an unpinned build are not trusted");
Check(wrongBuild.FelComposition == DvObservedState.Unknown && wrongBuild.BlElPairing == DvObservedState.Unknown,
    "An unpinned build leaves observations Unknown, not Inactive");
Check(!NativeDvLogEvidence.VersionMatches(null, pinnedCommit), "An unknown runtime version never matches the pinned commit");
Check(NativeDvLogEvidence.VersionMatches("mpv v0.41.0-1042-g7e4cb538a", pinnedCommit), "The pinned build is recognised");

// The bounded source probe.
var probed = NativeDvSourceProbe.Parse("AMDV|7|6|hevc|pq|bt.2020|3840|2160");
Check(probed.IsProfile7 && probed.Width == 3840 && probed.Height == 2160, "The bounded probe reads Profile 7 and real dimensions");
Check(!NativeDvSourceProbe.Parse("AMDV|(unavailable)|x|hevc|pq|bt.2020|0|0").IsProfile7, "An unavailable profile property is not Profile 7");
Check(!NativeDvSourceProbe.Parse("nothing here").IsProfile7, "Unparseable probe output is not Profile 7");
string[] probeArgv = NativeDvSourceProbe.BuildArguments(@"C:\media\authored.mkv");
Check(probeArgv.Contains("--vo=null") && probeArgv.Contains("--frames=1") && probeArgv.Contains("--cache-on-disk=no"),
    "The probe decodes one frame to nothing and caches nothing on disk");
Check(!probeArgv.Any(x => x.Contains("-o=") || x.Contains("extract", StringComparison.OrdinalIgnoreCase) || x.Contains("encode", StringComparison.OrdinalIgnoreCase)),
    "The probe cannot extract, encode, or write media");
Check(probeArgv[^1] == @"C:\media\authored.mkv" && probeArgv[^2] == "--", "The probe reads the original container unchanged");

// The product plan keeps the zero-scratch and isolation invariants.
var productPlan = NativeDvPlaybackPlanner.Build(fel, pinned, @"C:\media\authored.mkv", "config", "pipe",
    experimentalLaneEnabled: true, enhancementLayer: true, logPath: @"C:\logs\native-dv.log",
    target: new PlaybackTarget(2560, 1440, 1, false, true), allowUnclassifiedEnhancementLayer: true);
Check(productPlan.Supported && productPlan.Arguments.Contains("--no-config"), "The product plan keeps the proven isolated invocation");
Check(productPlan.Arguments.Contains("--cache-on-disk=no"), "Zero media scratch survives productization");
Check(productPlan.Arguments.Contains(@"--log-file=C:\logs\native-dv.log"), "The product plan writes a bounded diagnostic log");
Check(productPlan.Arguments.Any(x => x.StartsWith("--msg-level=") && x.Contains("vo/gpu-next=debug") && !x.Contains("trace")),
    "Debug level keeps the composition evidence without a per-frame line that would grow with duration");
Check(productPlan.Arguments.Contains("--autofit=2560x1440") && productPlan.Arguments.Contains("--screen=1"),
    "The product plan honours the chosen display");
Check(!productPlan.Arguments.Any(x => x.Contains("mkvextract", StringComparison.OrdinalIgnoreCase) ||
    x.Contains("dovi_tool", StringComparison.OrdinalIgnoreCase) || x.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)),
    "Native playback never invokes the Dolby Vision export tools");

var unclassified = fel with { EnhancementLayer = DvEnhancementLayer.Unknown };
Check(NativeDvPlaybackPlanner.Build(unclassified, pinned, @"C:\m.mkv", "c", "p", true, true,
    allowUnclassifiedEnhancementLayer: true).Supported,
    "Playback may proceed with an unclassified layer because it destroys nothing and claims nothing");
Check(NativeDvPlaybackPlanner.Build(unclassified, pinned, @"C:\m.mkv", "c", "p", true, true).Rejection ==
    NativeDvRejection.EnhancementLayerUnclassified,
    "The strict default still refuses an unclassified layer");
Check(NativeDvPlaybackPlanner.Build(unclassified, pinned, @"C:\m.mkv", "c", "p", true, true,
    allowUnclassifiedEnhancementLayer: true).Explanation.Contains("unclassified"),
    "An unclassified source does not inherit the FEL wording");


// ---------------------------------------------------------------------------
// Opt-in product-path check against a real authored Profile 7 source.
// Set ADAPTIVE_MEDIA_NATIVE_DV_SOURCE to the file to exercise real provisioning,
// the real bounded probe, the real launch, and real observation. Skipped when
// unset so the ordinary gate stays hermetic.
// ---------------------------------------------------------------------------
string? realSource = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_SOURCE");
if (!string.IsNullOrWhiteSpace(realSource) && File.Exists(realSource))
{
    Console.WriteLine("Native Dolby Vision product path: exercising the real runtime.");
    string manifestPath = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_MANIFEST")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "native-dv-p7", "runtime-manifest.json");
    var shipped = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(manifestPath));

    string productRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-product-" + Guid.NewGuid().ToString("N"));
    var before = File.Exists(@"C:\mpv\mpv.exe")
        ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
        : (Length: 0L, Hash: "absent");
    try
    {
        // First run: nothing installed. Real download, real extraction, real
        // validation, real promotion.
        var productStore = new NativeDvRuntimeStore(Path.Combine(productRoot, "runtimes", "native-dv"));
        Check(!productStore.Resolve(shipped).IsUsable, "PRODUCT: a fresh installation has no native runtime");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var installed = await productStore.ProvisionAsync(shipped, allowDownload: true,
            new Progress<string>(x => Console.WriteLine("  " + x)));
        clock.Stop();
        Check(installed.IsUsable, "PRODUCT: the runtime provisions from its pinned publisher archive");
        Console.WriteLine($"  first provision: {clock.Elapsed.TotalSeconds:0.0}s -> {installed.Runtime!.ExecutablePath}");
        Check(NativeDvRuntimeStore.ComputeSha256(installed.Runtime.ExecutablePath) == installed.Runtime.Sha256,
            "PRODUCT: the installed executable matches its pinned hash");

        var fast = System.Diagnostics.Stopwatch.StartNew();
        Check(productStore.Resolve(shipped).IsUsable, "PRODUCT: the already-installed fast path validates");
        fast.Stop();
        Console.WriteLine($"  already-installed revalidation: {fast.Elapsed.TotalMilliseconds:0} ms");

        // The bounded source probe on the authored file.
        var facts = await NativeDvSourceProbe.ReadAsync(installed.Runtime.ExecutablePath, realSource, TimeSpan.FromSeconds(60));
        Check(facts.IsProfile7, "PRODUCT: the bounded probe identifies Profile 7 without extracting anything");
        Console.WriteLine($"  probe: profile={facts.DolbyVisionProfile} level={facts.DolbyVisionLevel} {facts.Width}x{facts.Height} {facts.Codec}/{facts.Transfer}");

        // The lane builds the plan the application would launch.
        var lane = new NativeDvLane(productStore, shipped);
        string pipe = "adaptive-media-product-" + Guid.NewGuid().ToString("N");
        string logPath = Path.Combine(productRoot, "native-dv.log");
        var outcome = await lane.PrepareAsync(realSource,
            new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = true },
            Path.Combine(productRoot, "config"), pipe, logPath, new PlaybackTarget(1280, 720));
        Check(outcome.Selected && outcome.Plan is { Supported: true }, "PRODUCT: the lane selects native playback for the authored source");
        Check(outcome.Plan!.Executable == installed.Runtime.ExecutablePath, "PRODUCT: the plan launches the provisioned runtime");
        Check(!outcome.Plan.Executable!.StartsWith(@"C:\mpv\", StringComparison.OrdinalIgnoreCase), "PRODUCT: the plan never launches the stable runtime");

        // Launch exactly what the application would launch.
        Directory.CreateDirectory(Path.Combine(productRoot, "config"));
        var psi = NativeProcess.StartInfo(outcome.Plan.Executable, outcome.Plan.Arguments, capture: false);
        using var player = System.Diagnostics.Process.Start(psi) ?? throw new IOException("player did not start");
        try
        {
            await using var ipc = new MpvIpc(pipe);
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ipc.ConnectAsync(connect.Token);
            string? version = null;
            for (int i = 0; i < 40 && version is null; i++)
            {
                using var q = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var v = await ipc.CommandAsync(["get_property", "mpv-version"], q.Token);
                version = v?.GetString();
                if (version is null) await Task.Delay(250);
            }
            await Task.Delay(4000);
            using var props = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? hwdec = (await ipc.CommandAsync(["get_property", "hwdec-current"], props.Token))?.GetString();
            var trackList = await ipc.CommandAsync(["get_property", "track-list"], props.Token);
            var aid = await ipc.CommandAsync(["get_property", "aid"], props.Token);
            var sid = await ipc.CommandAsync(["get_property", "sid"], props.Token);
            var chapters = await ipc.CommandAsync(["get_property", "chapters"], props.Token);

            string logText;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) logText = reader.ReadToEnd();

            var observed = NativeDvLogEvidence.Reduce(logText, version, shipped.MpvCommit, true, hwdec, "d3d11", "d3d11");
            Console.WriteLine($"  observed: delivered={observed.Delivered} composition={observed.FelComposition} pairing={observed.BlElPairing} " +
                $"decoders={observed.DecoderInstances} surfaces={observed.HardwareSurfaces} hwdec={observed.HardwareDecoderInUse} api={observed.LibplaceboApi}");
            Check(observed.Renderer == DvObservedState.Active, "PRODUCT: the renderer is observed through the product adapter");
            Check(observed.Delivered == NativeDvDelivered.FullEnhancementLayer, "PRODUCT: full enhancement-layer composition is observed, not assumed");
            Check(observed.BlElPairing == DvObservedState.Active && observed.DecoderInstances >= 2, "PRODUCT: BL and EL are separately decoded and paired");
            Check(observed.HardwareSurfaces == DvObservedState.Active, "PRODUCT: decoding stays on hardware surfaces");
            Check(!observed.HasDegradation, "PRODUCT: the product session reports no degradation");

            // The container survives: the laboratory harness muted audio and
            // subtitles, the product must not. The contract is that the original
            // tracks are present and selectable. Which one mpv auto-selects is its
            // own default behaviour and is not something this lane should force.
            int audioTracks = 0, subtitleTracks = 0, videoTracks = 0;
            foreach (var track in trackList!.Value.EnumerateArray())
            {
                string kind = track.GetProperty("type").GetString() ?? "";
                if (kind == "audio") audioTracks++;
                else if (kind == "sub") subtitleTracks++;
                else if (kind == "video") videoTracks++;
            }
            Check(audioTracks > 0, "PRODUCT: the original container's audio tracks are present");
            Check(subtitleTracks > 0, "PRODUCT: the original container's subtitle tracks are present");
            Check(videoTracks > 0, "PRODUCT: the original container's video track is present");
            Check(chapters is not null && chapters.Value.GetInt32() > 1, "PRODUCT: the original chapters are present");
            Console.WriteLine($"  container: {videoTracks} video, {audioTracks} audio, {subtitleTracks} subtitle tracks; chapters={chapters}; selected aid={aid} sid={sid}");

            // Bounded diagnostic state: lower the level and confirm growth stops.
            long beforeLower = new FileInfo(logPath).Length;
            using var lower = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ipc.CommandAsync(["set_property", "msg-level", "all=no"], lower.Token);
            long atLower = new FileInfo(logPath).Length;
            await Task.Delay(6000);
            long afterLower = new FileInfo(logPath).Length;
            Console.WriteLine($"  log: {beforeLower} bytes at observation, {afterLower} bytes six seconds later");
            Check(afterLower - atLower == 0, "PRODUCT: the diagnostic log stops growing once composition has been observed");

            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ipc.CommandAsync(["quit"], quit.Token);
        }
        finally
        {
            if (!player.HasExited) { try { player.Kill(true); } catch (InvalidOperationException) { } }
            player.WaitForExit(10000);
        }

        // Zero media-sized scratch, and nothing installed outside the store.
        long scratch = Directory.Exists(Path.Combine(productRoot, "config"))
            ? Directory.EnumerateFiles(Path.Combine(productRoot, "config"), "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) : 0;
        long logBytes = File.Exists(Path.Combine(productRoot, "native-dv.log")) ? new FileInfo(Path.Combine(productRoot, "native-dv.log")).Length : 0;
        Console.WriteLine($"  playback state: config={scratch} bytes, log={logBytes} bytes");
        Check(scratch < 8 * 1024 * 1024 && logBytes < 8 * 1024 * 1024, "PRODUCT: playback state stays bounded, with no media-sized artifact");
        Check(!Directory.EnumerateFiles(productRoot, "*.hevc", SearchOption.AllDirectories).Any() &&
              !Directory.EnumerateFiles(productRoot, "*.mkv", SearchOption.AllDirectories).Any(),
            "PRODUCT: native playback creates no extracted or converted media");

        var after = File.Exists(@"C:\mpv\mpv.exe")
            ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
            : (Length: 0L, Hash: "absent");
        Check(before.Length == after.Length && before.Hash == after.Hash, "PRODUCT: the stable runtime is untouched by native playback");
        Check(new FileInfo(realSource).Length > 0, "PRODUCT: the authored source is still readable and unmodified in length");
    }
    finally { if (Directory.Exists(productRoot)) Directory.Delete(productRoot, true); }
    Console.WriteLine("Native Dolby Vision product path: PASS");
}

Console.WriteLine($"PASS: {checks} total Dolby Vision assertions");
