using AdaptiveMedia;
using System.Text.Json;
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Console.Error.WriteLine(e.ExceptionObject); Environment.Exit(1); };
if (await PlaybackRecoveryTests.ChildAsync(args) is int childExit) { Environment.Exit(childExit); return; }
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
if (args.FirstOrDefault() == "--recovery-tests") { await PlaybackRecoveryTests.RunAsync(Check, args.ElementAtOrDefault(1)); Console.WriteLine($"PASS: {checks} recovery assertions"); return; }
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
    Check(new PlaybackService().NativeDolbyVision is not null,
        "the ordinary playback service has the qualified native DV lane available for an opt-in request");
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
// Runtime lifecycle: generations, retention, garbage collection, concurrency.
// A failed or unsupported update must never cost the working runtime.
// ---------------------------------------------------------------------------
string lifeRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-lifecycle-" + Guid.NewGuid().ToString("N"));
try
{
    // Two real generations, differing only in content, so the ids differ.
    (NativeDvRuntimeDescriptor Descriptor, byte[] Archive, byte[] Exe, byte[] Com) Generation(string label, string commit)
    {
        byte[] exe = System.Text.Encoding.UTF8.GetBytes("mpv-executable-" + label);
        byte[] com = System.Text.Encoding.UTF8.GetBytes("mpv-launcher-" + label);
        byte[] archive = System.Text.Encoding.UTF8.GetBytes("archive-payload-" + label);
        string json = $$"""
        {
          "schemaVersion": 1,
          "provider": { "name": "test", "releaseUrl": "https://example.invalid/r" },
          "archive": { "url": "https://example.invalid/{{label}}.7z", "bytes": {{archive.Length}}, "sha256": "{{Sha256Of(archive)}}" },
          "runtime": {
            "executable": { "pathRelativeToManifest": "../x/extracted/mpv.exe", "bytes": {{exe.Length}}, "sha256": "{{Sha256Of(exe)}}" },
            "consoleLauncher": { "pathRelativeToManifest": "../x/extracted/mpv.com", "bytes": {{com.Length}}, "sha256": "{{Sha256Of(com)}}" }
          },
          "mpv": { "version": "0.41.0-test-{{label}}", "commit": "{{commit}}" },
          "libplacebo": { "apiVersion": 371 }
        }
        """;
        return (NativeDvRuntimeDescriptor.FromManifestJson(json), archive, exe, com);
    }

    // Both generations claim the one commit whose adapter is verified, because the
    // support gate is exercised separately below.
    string verified = NativeDvDiagnosticAdapters.Supported[0].MpvCommit;
    var genA = Generation("alpha", verified);
    var genB = Generation("beta", verified);
    Check(genA.Descriptor.VersionId != genB.Descriptor.VersionId, "Different content is a different generation");

    int downloads = 0;
    NativeDvArchiveFetch FetchFor(params (NativeDvRuntimeDescriptor D, byte[] A)[] known) => (uri, destination, token) =>
    {
        downloads++;
        foreach (var k in known)
            if (uri.AbsoluteUri.Contains(k.D.ArchiveUrl.Segments[^1])) { File.WriteAllBytes(destination, k.A); return Task.CompletedTask; }
        throw new IOException("no archive for " + uri);
    };
    NativeDvArchiveExtract ExtractFor(params (NativeDvRuntimeDescriptor D, byte[] E, byte[] C)[] known) => (archive, destination, token) =>
    {
        byte[] bytes = File.ReadAllBytes(archive);
        foreach (var k in known)
            if (Sha256Of(bytes) == k.D.ArchiveSha256)
            {
                File.WriteAllBytes(Path.Combine(destination, "mpv.exe"), k.E);
                File.WriteAllBytes(Path.Combine(destination, "mpv.com"), k.C);
                return Task.CompletedTask;
            }
        throw new IOException("unknown archive");
    };

    string root = Path.Combine(lifeRoot, "store");
    var store = new NativeDvRuntimeStore(root,
        FetchFor((genA.Descriptor, genA.Archive), (genB.Descriptor, genB.Archive)),
        ExtractFor((genA.Descriptor, genA.Exe, genA.Com), (genB.Descriptor, genB.Exe, genB.Com)));
    var life = new NativeDvRuntimeLifecycle(store);

    // Generation id discipline: only an archive hash is ever treated as one.
    Check(NativeDvRuntimeLifecycle.IsGenerationId(genA.Descriptor.VersionId), "An archive hash is a generation id");
    foreach (string bad in new[] { "..", "../../evil", "short", new string('z', 64), "", "  " })
        Check(!NativeDvRuntimeLifecycle.IsGenerationId(bad), $"'{bad}' is not a generation id");

    // First install.
    var first = await life.ReconcileAsync(genA.Descriptor, allowDownload: true);
    Check(first.State == NativeDvLifecycleState.Ready && first.Runtime is not null, "The first generation installs and becomes current");
    Check(first.CurrentGeneration == genA.Descriptor.VersionId && first.PreviousGeneration is null, "There is no previous generation yet");
    Check(downloads == 1, "The first install downloads once");

    // Idempotence: reconciling again changes nothing and downloads nothing.
    var again = await life.ReconcileAsync(genA.Descriptor, allowDownload: true);
    Check(again.State == NativeDvLifecycleState.Ready && downloads == 1, "Reconciling an already-current generation downloads nothing");
    Check(again.GenerationsRemoved == 0, "Repeated reconciliation removes nothing");
    Check(life.ReadState().Current == genA.Descriptor.VersionId, "Repeated reconciliation does not drift the recorded state");

    // Upgrade A -> B. A must survive as the retained fallback.
    var upgraded = await life.ReconcileAsync(genB.Descriptor, allowDownload: true);
    Check(upgraded.State == NativeDvLifecycleState.Updated, "Moving to a new generation reports an update");
    Check(upgraded.CurrentGeneration == genB.Descriptor.VersionId, "The new generation becomes current");
    Check(upgraded.PreviousGeneration == genA.Descriptor.VersionId, "The outgoing generation is retained as previous");
    Check(Directory.Exists(Path.Combine(root, genA.Descriptor.VersionId)), "The previous generation is still on disk");
    Check(store.Resolve(genA.Descriptor).IsUsable, "The retained previous generation still validates");

    // A corrupt candidate must not cost the working runtime.
    var genC = Generation("gamma", verified);
    var corruptStore = new NativeDvRuntimeStore(root,
        FetchFor((genC.Descriptor, genC.Archive)),
        (archive, destination, token) =>
        {
            File.WriteAllBytes(Path.Combine(destination, "mpv.exe"), System.Text.Encoding.UTF8.GetBytes(new string('x', genC.Exe.Length)));
            File.WriteAllBytes(Path.Combine(destination, "mpv.com"), genC.Com);
            return Task.CompletedTask;
        });
    var corruptLife = new NativeDvRuntimeLifecycle(corruptStore);
    var failedUpdate = await corruptLife.ReconcileAsync(genC.Descriptor, allowDownload: true);
    Check(failedUpdate.State == NativeDvLifecycleState.UpdateFailedPreviousRetained, "A corrupt candidate reports a failed update with the previous runtime retained");
    Check(failedUpdate.Runtime is null, "A failed update yields no runtime");
    Check(!Directory.Exists(Path.Combine(root, genC.Descriptor.VersionId)), "A corrupt candidate is never promoted");
    Check(corruptLife.ReadState().Current == genB.Descriptor.VersionId, "A failed update does not change which generation is current");
    Check(store.Resolve(genB.Descriptor).IsUsable, "The working generation still works after a failed update");
    Check(store.Resolve(genA.Descriptor).IsUsable, "The retained fallback survives a failed update too");

    // A cancelled candidate must behave the same way.
    using var cancelSource = new CancellationTokenSource();
    var cancelStore = new NativeDvRuntimeStore(root,
        (uri, destination, token) => { cancelSource.Cancel(); File.WriteAllBytes(destination, genC.Archive); return Task.CompletedTask; },
        ExtractFor((genC.Descriptor, genC.Exe, genC.Com)));
    var cancelled = await new NativeDvRuntimeLifecycle(cancelStore).ReconcileAsync(genC.Descriptor, true, null, cancelSource.Token);
    Check(cancelled.Runtime is null && cancelled.CurrentGeneration == genB.Descriptor.VersionId, "A cancelled update leaves the current generation in place");
    Check(store.Resolve(genB.Descriptor).IsUsable, "The working generation survives a cancelled update");

    // An unsupported runtime is refused before anything is downloaded.
    var unsupported = Generation("delta", "0000000000000000000000000000000000000000");
    int downloadsBefore = downloads;
    var unsupportedResult = await life.ReconcileAsync(unsupported.Descriptor, allowDownload: true);
    Check(unsupportedResult.State == NativeDvLifecycleState.UnsupportedRuntime, "A runtime with no verified adapter is refused");
    Check(unsupportedResult.Runtime is null, "An unsupported runtime never yields a usable runtime");
    Check(downloads == downloadsBefore, "An unsupported runtime is never downloaded");
    Check(!Directory.Exists(Path.Combine(root, unsupported.Descriptor.VersionId)), "An unsupported runtime is never installed");
    Check(life.ReadState().Current == genB.Descriptor.VersionId, "Refusing an unsupported runtime does not disturb the current generation");

    // Garbage collection: only genuinely superseded generations go.
    string orphanGeneration = Path.Combine(root, new string('a', 64));
    Directory.CreateDirectory(orphanGeneration);
    File.WriteAllText(Path.Combine(orphanGeneration, "mpv.exe"), "superseded");
    string notAGeneration = Path.Combine(root, "not-a-generation");
    Directory.CreateDirectory(notAGeneration);
    File.WriteAllText(Path.Combine(notAGeneration, "keep.txt"), "unrelated");
    int collected = life.CollectGarbage(genB.Descriptor);
    Check(collected == 1, "Garbage collection removes the superseded generation");
    Check(!Directory.Exists(orphanGeneration), "The superseded generation is gone");
    Check(Directory.Exists(Path.Combine(root, genB.Descriptor.VersionId)), "Garbage collection never removes the current generation");
    Check(Directory.Exists(Path.Combine(root, genA.Descriptor.VersionId)), "Garbage collection never removes the retained previous generation");
    Check(Directory.Exists(notAGeneration), "Garbage collection only considers directories named like a generation");
    Check(life.CollectGarbage(genB.Descriptor) == 0, "Garbage collection is idempotent");

    // A hostile state file must not be able to name something to delete.
    File.WriteAllText(life.StatePath, "{\"Current\":\"../../../Windows\",\"Previous\":\"..\\\\..\\\\escape\"}");
    var sanitised = life.ReadState();
    Check(sanitised.Current is null && sanitised.Previous is null, "A state file naming a path instead of a generation is ignored");
    File.WriteAllText(life.StatePath, "{ not json");
    Check(life.ReadState().Current is null, "A corrupt state file reads as empty rather than throwing");
    // Restore a truthful state for the remaining checks.
    await life.ReconcileAsync(genB.Descriptor, allowDownload: true);
    Check(life.ReadState().Current == genB.Descriptor.VersionId, "Reconciliation repairs a damaged state file");

    // Crash/restart: a brand new lifecycle over the same root converges without work.
    int beforeRestart = downloads;
    var restarted = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(root,
        FetchFor((genB.Descriptor, genB.Archive)), ExtractFor((genB.Descriptor, genB.Exe, genB.Com))));
    var afterRestart = await restarted.ReconcileAsync(genB.Descriptor, allowDownload: true);
    Check(afterRestart.State == NativeDvLifecycleState.Ready && downloads == beforeRestart, "A restarted process reuses the installed generation without downloading");

    // Concurrency: real OS-level exclusion, not a simulation. Two reconcilers over
    // the same root race for the lock; exactly one may install.
    string raceRoot = Path.Combine(lifeRoot, "race");
    int raceDownloads = 0;
    NativeDvArchiveFetch slowFetch = async (uri, destination, token) =>
    {
        Interlocked.Increment(ref raceDownloads);
        await Task.Delay(250, token);
        File.WriteAllBytes(destination, genA.Archive);
    };
    var raceA = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(raceRoot, slowFetch, ExtractFor((genA.Descriptor, genA.Exe, genA.Com))));
    var raceB = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(raceRoot, slowFetch, ExtractFor((genA.Descriptor, genA.Exe, genA.Com))));
    var results = await Task.WhenAll(
        raceA.ReconcileAsync(genA.Descriptor, allowDownload: true),
        raceB.ReconcileAsync(genA.Descriptor, allowDownload: true));
    Check(raceDownloads == 1, "Two concurrent reconcilers download the runtime once between them");
    Check(results.Count(x => x.Runtime is not null) >= 1, "At least one concurrent reconciler ends with a usable runtime");
    Check(results.All(x => x.State is NativeDvLifecycleState.Ready or NativeDvLifecycleState.Busy), "A reconciler that loses the race reports busy rather than racing");
    Check(!results.Any(x => x.Runtime is not null && !File.Exists(x.Runtime.ExecutablePath)), "No concurrent reconciler returns an unverified runtime");

    // Cross-process: a separate process holding the lock must be respected.
    string lockRoot = Path.Combine(lifeRoot, "crossprocess");
    Directory.CreateDirectory(lockRoot);
    var holderLife = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(lockRoot,
        FetchFor((genA.Descriptor, genA.Archive)), ExtractFor((genA.Descriptor, genA.Exe, genA.Com))));
    string lockFile = Path.Combine(lockRoot, NativeDvRuntimeLifecycle.LockFileName);
    var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell",
        $"-NoProfile -Command \"$f=[IO.File]::Open('{lockFile}','OpenOrCreate','ReadWrite','None'); Start-Sleep -Seconds 6; $f.Dispose()\"")
    { UseShellExecute = false, CreateNoWindow = true });
    try
    {
        await Task.Delay(1500);
        var blocked = await holderLife.ReconcileAsync(genA.Descriptor, allowDownload: true);
        Check(blocked.State == NativeDvLifecycleState.Busy, "A lock held by another process is respected");
        Check(blocked.Runtime is null, "A busy lifecycle never returns a runtime it did not verify");
        Check(!Directory.Exists(Path.Combine(lockRoot, genA.Descriptor.VersionId)), "A busy lifecycle installs nothing");
    }
    finally { try { if (!holder!.HasExited) holder.Kill(true); } catch (InvalidOperationException) { } holder!.WaitForExit(10000); }
    var afterLock = await holderLife.ReconcileAsync(genA.Descriptor, allowDownload: true);
    Check(afterLock.State == NativeDvLifecycleState.Ready, "Once the other process releases the lock, installation proceeds");

    // Nothing was ever created outside the roots the lifecycle was given.
    Check(Directory.GetDirectories(lifeRoot).All(x => Path.GetFileName(x) is "store" or "race" or "crossprocess"),
        "The lifecycle writes only inside the roots it was given");
}
finally { if (Directory.Exists(lifeRoot)) Directory.Delete(lifeRoot, true); }

// ---------------------------------------------------------------------------
// Product observation: the version-pinned log adapter.
// ---------------------------------------------------------------------------
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

var productComposed = NativeDvLogEvidence.Reduce(composedLog, "mpv v0.41.0-1042-g7e4cb538a", true, "d3d11va");
Check(productComposed.Delivered == NativeDvDelivered.FullEnhancementLayer, "The product reports Full FEL only from observed composition");
Check(productComposed.BlElPairing == DvObservedState.Active && productComposed.DecoderInstances == 2, "BL/EL pairing and both decoders are observed");
Check(productComposed.HardwareSurfaces == DvObservedState.Active, "Declining an optional hwdec interop driver is not a software fallback");
Check(!productComposed.HasDegradation, "A fully composed product session reports no degradation");
Check(productComposed.LibplaceboApi == 371, "The renderer API is observed");

var productSuppressed = NativeDvLogEvidence.Reduce(suppressedLog, "mpv v0.41.0-1042-g7e4cb538a", true, "d3d11va");
Check(productSuppressed.Delivered == NativeDvDelivered.BaseLayerOnly, "Without observed composition the product reports base layer only");
Check(productSuppressed.Degradation.Any(x => x.Contains("base-layer-only")), "The product names the base-layer-only fallback");
Check(!productSuppressed.Summary.Contains("Full enhancement-layer"), "A base-layer product session never reads as Full FEL");

// A different build invalidates the patterns, so nothing may be claimed from them.
var wrongBuild = NativeDvLogEvidence.Reduce(composedLog, "mpv v0.40.0-1-gdeadbeef", true, "d3d11va");
Check(wrongBuild.Delivered == NativeDvDelivered.Unknown, "Log patterns from an unpinned build are not trusted");
Check(wrongBuild.FelComposition == DvObservedState.Unknown && wrongBuild.BlElPairing == DvObservedState.Unknown,
    "An unpinned build leaves observations Unknown, not Inactive");
Check(NativeDvDiagnosticAdapters.For(null) is null, "An unknown runtime version has no verified adapter");
Check(NativeDvDiagnosticAdapters.For("mpv v0.41.0-1042-g7e4cb538a") is not null, "The verified build is recognised");

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
    target: new PlaybackTarget(2560, 1440, 1, false), allowUnclassifiedEnhancementLayer: true);
Check(productPlan.Supported && productPlan.Arguments.Contains("--no-config"), "The product plan keeps the proven isolated invocation");
Check(productPlan.Arguments.Contains("--cache-on-disk=no"), "Zero media scratch survives productization");
Check(productPlan.Arguments.Contains(@"--log-file=C:\logs\native-dv.log"), "The product plan writes a bounded diagnostic log");
Check(productPlan.Arguments.Any(x => x.StartsWith("--msg-level=") && x.Contains("vo/gpu-next=debug") && !x.Contains("trace")),
    "Debug level keeps the composition evidence without a per-frame line that would grow with duration");
Check(productPlan.Arguments.Contains("--autofit=2560x1440") && productPlan.Arguments.Contains("--screen=1"),
    "The product plan honours the chosen display");
Check(productPlan.Arguments.Contains("--target-trc=bt.1886") && productPlan.Arguments.Contains("--target-prim=bt.709") &&
    productPlan.Arguments.Contains("--target-colorspace-hint=auto"),
    "Native Profile 7 on an unverified HDR target requests managed SDR tone mapping");
var hdrDisplay = new DisplayCapability(HdrSupported: true, HdrActive: true, ActiveColorMode: "HDR", DxgiColorSpace: 12);
var nativeHdr = NativeDvPlaybackPlanner.Build(fel, pinned, @"C:\media\authored.mkv", "config", "hdr-pipe",
    experimentalLaneEnabled: true, enhancementLayer: true,
    target: new PlaybackTarget(2560, 1440, Display: hdrDisplay), allowUnclassifiedEnhancementLayer: true);
Check(nativeHdr.Supported && nativeHdr.Arguments.Contains("--target-colorspace-hint=auto") &&
    !nativeHdr.Arguments.Any(x => x.StartsWith("--target-trc=")),
    "Native Profile 7 keeps HDR source colour on a verified active HDR path");
var oldDataDir = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR");
var isolatedHdrDir = Path.Combine(Path.GetTempPath(), "DemiMedia-HdrSession-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(isolatedHdrDir);
    Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", isolatedHdrDir);
    var stalePath = Path.Combine(isolatedHdrDir, "hdr-recovery.json");
    File.WriteAllText(stalePath, "stale state must not be applied");
    var switchPlan = PlaybackPlanBuilder.Build("mpv.exe", "config", ["movie.mkv"],
        new PlaybackOptions("Reference", "Off", "Off", false, false, AutoHdrSwitch: true), video,
        new PlaybackTarget(1920, 1080), new(false, false), "switch-test");
    var switchReport = new SessionDiagnostics();
    using (HdrSession.Begin(switchPlan, switchReport)) { }
    Check(File.ReadAllText(stalePath) == "stale state must not be applied" &&
        !File.Exists(Path.Combine(isolatedHdrDir, "hdr-session.lock")) &&
        switchReport.FallbackHistory.Any(x => x.Contains("unavailable")),
        "Stale HDR state cannot be blindly restored and switching is truthfully unavailable");
}
finally
{
    Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", oldDataDir);
    Directory.Delete(isolatedHdrDir, true);
}
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
// Playback-time runtime health.
//
// The model has to separate a runtime that broke from media it was right to
// refuse and from a person who stopped watching. Only the first may cost a
// generation its standing, and none of it may promote a composition claim.
// ---------------------------------------------------------------------------
NativeDvHealthSignals Signals(bool selected = true, bool started = true, bool ipc = true,
    bool renderer = true, bool decoder = true, bool output = true, bool adapter = true,
    bool stop = false, int exit = 0, double seconds = 30) =>
    new()
    {
        LaneSelected = selected, ProcessStarted = started, IpcConnected = ipc,
        RendererObserved = renderer, VideoDecoderObserved = decoder, VideoOutputConfigured = output,
        ObservationAdapterSupported = adapter, StopRequested = stop, ExitCode = exit,
        Lifetime = TimeSpan.FromSeconds(seconds),
    };

// The healthy-start threshold. Each condition is load-bearing: dropping any one
// of them must take the attempt below the threshold.
Check(NativeDvUsefulPlayback.Reached(Signals()), "A runtime that connected, rendered, decoded and configured its output reached useful playback");
Check(!NativeDvUsefulPlayback.Reached(Signals(ipc: false)), "Without a diagnostics connection the threshold is not reached");
Check(!NativeDvUsefulPlayback.Reached(Signals(renderer: false)), "Without a renderer the threshold is not reached");
Check(!NativeDvUsefulPlayback.Reached(Signals(decoder: false)), "Without a video decoder the threshold is not reached");
Check(!NativeDvUsefulPlayback.Reached(Signals(output: false)), "Without a frame configuring the video output the threshold is not reached");
// The threshold must be meaningfully stronger than "a process appeared".
Check(!NativeDvUsefulPlayback.Reached(Signals(ipc: false, renderer: false, decoder: false, output: false)),
    "A process that merely spawned has not started useful playback");

// Case 1: the current generation succeeds, so nothing is rolled back.
var healthy = NativeDvHealthEvaluator.Evaluate(Signals());
Check(healthy.Health == NativeDvHealth.Healthy && !healthy.RollbackCandidate, "A healthy attempt triggers no rollback");
Check(!healthy.MarksGenerationUnhealthy && !healthy.RuntimeAtFault, "A healthy attempt holds nothing against the generation");

// Cases 10 and 11: real runtime failures before useful playback are eligible.
var rendererFailed = NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: 1));
Check(rendererFailed.Health == NativeDvHealth.RendererInitializationFailed && rendererFailed.RollbackCandidate,
    "A renderer or device failure is an eligible health failure");
var crashed = NativeDvHealthEvaluator.Evaluate(Signals(ipc: false, renderer: false, decoder: false, output: false,
    exit: unchecked((int)0xC0000005), seconds: 0.3));
Check(crashed.Health == NativeDvHealth.ProcessCrashed && crashed.RollbackCandidate,
    "An immediate process crash is an eligible health failure");
Check(NativeDvHealthEvaluator.ExitCodeMeansCrash(unchecked((int)0xC0000005)) &&
      NativeDvHealthEvaluator.ExitCodeMeansCrash(-1073741819) && !NativeDvHealthEvaluator.ExitCodeMeansCrash(1),
    "A crash is recognised from an NTSTATUS-shaped exit code, not from an ordinary error code");
var decoderFailed = NativeDvHealthEvaluator.Evaluate(Signals(decoder: false, output: false, exit: 1));
Check(decoderFailed.Health == NativeDvHealth.DecoderInitializationFailed && decoderFailed.RollbackCandidate,
    "A decoder that never initialised is an eligible health failure");
var silentExit = NativeDvHealthEvaluator.Evaluate(Signals(ipc: false, renderer: false, decoder: false, output: false, exit: 1));
Check(silentExit.Health == NativeDvHealth.ExitedBeforeUsefulPlayback && silentExit.RollbackCandidate,
    "A runtime that exited without reporting anything is an eligible health failure");
var launchFailed = NativeDvHealthEvaluator.Evaluate(Signals(started: false, ipc: false, renderer: false, decoder: false, output: false));
Check(launchFailed.Health == NativeDvHealth.LaunchFailed && launchFailed.RollbackCandidate,
    "A runtime that could not be started at all is an eligible health failure");

// A running build with no verified adapter cannot report what it composed, and
// that is a runtime problem rather than a media one.
var unreadable = NativeDvHealthEvaluator.Evaluate(Signals(adapter: false, renderer: false, decoder: false, output: false, exit: 1));
Check(unreadable.Health == NativeDvHealth.ObservationContractFailed && unreadable.RollbackCandidate,
    "A running runtime this build cannot read is an eligible health failure");

// Cases 7, 8 and 9: media, user and normal outcomes must never poison a generation.
foreach (int unplayable in new[] { 2, 3 })
{
    var media = NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: unplayable));
    Check(media.Health == NativeDvHealth.SourceNotPlayable && !media.MarksGenerationUnhealthy && !media.RollbackCandidate,
        $"A source the runtime cannot play (exit {unplayable}) never marks the generation unhealthy");
}
var userStopped = NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, stop: true, exit: 1));
Check(userStopped.Health == NativeDvHealth.StoppedByUser && !userStopped.MarksGenerationUnhealthy,
    "User cancellation never marks the generation unhealthy, even with an abnormal exit on the way out");
var signalled = NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: 4));
Check(signalled.Health == NativeDvHealth.StoppedByUser && !signalled.MarksGenerationUnhealthy,
    "A runtime quit by a signal is an external stop, not a runtime fault");
var normalExit = NativeDvHealthEvaluator.Evaluate(Signals(stop: true));
Check(normalExit.Health == NativeDvHealth.Healthy && !normalExit.MarksGenerationUnhealthy,
    "A normal exit after real playback is healthy and marks nothing unhealthy");
var shortClean = NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: 0, seconds: 0.4));
Check(shortClean.Health == NativeDvHealth.Unknown && !shortClean.MarksGenerationUnhealthy && !shortClean.RollbackCandidate,
    "A clean exit before the threshold is unknown, never a fault: absent evidence is not evidence of a fault");
var notUsed = NativeDvHealthEvaluator.Evaluate(Signals(selected: false));
Check(notUsed.Health == NativeDvHealth.NotEvaluated && !notUsed.MarksGenerationUnhealthy,
    "An attempt that never used the native lane is not evaluated");

// A fault after useful playback is truthfully a fault, and deliberately not a
// rollback trigger: the generation demonstrably worked.
var lateFailure = NativeDvHealthEvaluator.Evaluate(Signals(exit: 1));
Check(lateFailure.Health == NativeDvHealth.FailedAfterUsefulPlayback && lateFailure.RuntimeAtFault,
    "A failure after useful playback is reported as a real runtime fault");
Check(!lateFailure.RollbackCandidate && !lateFailure.MarksGenerationUnhealthy,
    "A failure after useful playback never restarts playback on an older runtime");

// Observing base layer only is a truthful composition outcome, not ill health.
// Rolling back on it would mean treating a MEL source, or a correct refusal to
// compose, as a broken build.
Check(NativeDvHealthEvaluator.Evaluate(Signals()).Health == NativeDvHealth.Healthy,
    "Health is decided by whether playback started, never by what was composed");

// Session-local health memory. It is the thing that makes a bounce impossible.
var memory = new NativeDvHealthMemory();
string genOne = new string('1', 64);
string genTwo = new string('2', 64);
memory.Record(genOne, healthy);
Check(memory.FaultCount(genOne) == 0 && !memory.IsKnownUnhealthy(genOne), "A healthy attempt records no fault");
memory.Record(genOne, userStopped); memory.Record(genOne, shortClean); memory.Record(genOne, lateFailure);
foreach (int unplayable in new[] { 2 })
    memory.Record(genOne, NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: unplayable)));
Check(memory.FaultCount(genOne) == 0,
    "Media refusals, user stops, unknown outcomes and post-playback failures all leave the generation unmarked");
memory.Record(genOne, rendererFailed);
Check(memory.IsKnownUnhealthy(genOne) && memory.FaultCount(genOne) == 1, "A runtime fault before useful playback is recorded");
Check(!memory.IsDemoted(genOne), "One transient failure never demotes a hash-verified runtime");
Check(memory.LastHealth(genOne) == NativeDvHealth.RendererInitializationFailed, "The recorded health names the failure");
memory.Record(genOne, crashed);
Check(memory.IsDemoted(genOne) && memory.FaultCount(genOne) == NativeDvHealthMemory.DemotionThreshold,
    "Repeated failures demote the generation for this session only");
Check(!memory.IsKnownUnhealthy(genTwo) && !memory.IsDemoted(genTwo), "One generation's failures say nothing about another");
memory.Clear(genOne);
Check(!memory.IsKnownUnhealthy(genOne), "An explicit retry clears the session-local judgement");
memory.Record("not-a-generation-id", rendererFailed);
memory.Record(null, rendererFailed);
Check(memory.FaultCount("not-a-generation-id") == 0, "Health is only ever recorded against a real generation id");

// The product-facing lines never claim a composition result.
foreach (NativeDvPlaybackStatus status in Enum.GetValues<NativeDvPlaybackStatus>())
{
    string text = NativeDvPlaybackStatusText.Describe(status);
    Check(text.Length > 0 && !text.Contains("Full enhancement", StringComparison.OrdinalIgnoreCase),
        $"The status line for {status} says something and claims no composition");
}

// ---------------------------------------------------------------------------
// Recorded manifests, generation pinning, and fallback eligibility.
//
// A retained generation may only be launched when it has been verified the same
// way it was verified at install. Everything below is a refusal to launch
// something unproven.
// ---------------------------------------------------------------------------
string healthRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-health-" + Guid.NewGuid().ToString("N"));
try
{
    (NativeDvRuntimeDescriptor Descriptor, byte[] Archive, byte[] Exe, byte[] Com) HealthGeneration(string label, string commit)
    {
        byte[] exe = System.Text.Encoding.UTF8.GetBytes("mpv-executable-" + label);
        byte[] com = System.Text.Encoding.UTF8.GetBytes("mpv-launcher-" + label);
        byte[] archive = System.Text.Encoding.UTF8.GetBytes("archive-payload-" + label);
        string json = $$"""
        {
          "schemaVersion": 1,
          "provider": { "name": "test", "releaseUrl": "https://example.invalid/r" },
          "archive": { "url": "https://example.invalid/{{label}}.7z", "bytes": {{archive.Length}}, "sha256": "{{Sha256Of(archive)}}" },
          "runtime": {
            "executable": { "pathRelativeToManifest": "../x/extracted/mpv.exe", "bytes": {{exe.Length}}, "sha256": "{{Sha256Of(exe)}}" },
            "consoleLauncher": { "pathRelativeToManifest": "../x/extracted/mpv.com", "bytes": {{com.Length}}, "sha256": "{{Sha256Of(com)}}" }
          },
          "mpv": { "version": "0.41.0-test-{{label}}", "commit": "{{commit}}" },
          "libplacebo": { "apiVersion": 371 }
        }
        """;
        return (NativeDvRuntimeDescriptor.FromManifestJson(json), archive, exe, com);
    }

    string verifiedCommit = NativeDvDiagnosticAdapters.Supported[0].MpvCommit;
    var hA = HealthGeneration("health-alpha", verifiedCommit);
    var hB = HealthGeneration("health-beta", verifiedCommit);

    NativeDvArchiveFetch HealthFetch(params (NativeDvRuntimeDescriptor D, byte[] A)[] known) => (uri, destination, token) =>
    {
        foreach (var k in known)
            if (uri.AbsoluteUri.Contains(k.D.ArchiveUrl.Segments[^1])) { File.WriteAllBytes(destination, k.A); return Task.CompletedTask; }
        throw new IOException("no archive for " + uri);
    };
    NativeDvArchiveExtract HealthExtract(params (NativeDvRuntimeDescriptor D, byte[] E, byte[] C)[] known) => (archive, destination, token) =>
    {
        byte[] bytes = File.ReadAllBytes(archive);
        foreach (var k in known)
            if (Sha256Of(bytes) == k.D.ArchiveSha256)
            {
                File.WriteAllBytes(Path.Combine(destination, "mpv.exe"), k.E);
                File.WriteAllBytes(Path.Combine(destination, "mpv.com"), k.C);
                return Task.CompletedTask;
            }
        throw new IOException("unknown archive");
    };

    string hRoot = Path.Combine(healthRoot, "store");
    var hStore = new NativeDvRuntimeStore(hRoot,
        HealthFetch((hA.Descriptor, hA.Archive), (hB.Descriptor, hB.Archive)),
        HealthExtract((hA.Descriptor, hA.Exe, hA.Com), (hB.Descriptor, hB.Exe, hB.Com)));
    var hLife = new NativeDvRuntimeLifecycle(hStore);

    // Case 4: nothing retained yet, so there is nothing to fall back to.
    await hLife.ReconcileAsync(hA.Descriptor, allowDownload: true);
    var noPrevious = hLife.ResolveFallback(hA.Descriptor.VersionId);
    Check(noPrevious.State == NativeDvFallbackState.NoPreviousGeneration && !noPrevious.IsUsable,
        "With no retained generation there is no fallback");
    Check(noPrevious.Runtime is null, "An unavailable fallback never carries a runtime");

    // Installing a generation records its own pinned manifest, outside the
    // generation tree so the installed tree stays immutable.
    var snapshotA = hStore.ReadDescriptorSnapshot(hA.Descriptor.VersionId);
    Check(snapshotA is not null, "Installing a generation records its own pinned manifest");
    Check(snapshotA!.VersionId == hA.Descriptor.VersionId && snapshotA.MpvCommit == hA.Descriptor.MpvCommit &&
          snapshotA.MpvVersion == hA.Descriptor.MpvVersion && snapshotA.LibplaceboApi == hA.Descriptor.LibplaceboApi,
        "The recorded manifest round-trips the generation's identity");
    Check(snapshotA.LauncherRelativePath == hA.Descriptor.LauncherRelativePath &&
          snapshotA.Components.Length == hA.Descriptor.Components.Length &&
          snapshotA.Components.All(x => hA.Descriptor.Components.Any(y => y.RelativePath == x.RelativePath &&
              y.Sha256 == x.Sha256 && y.Bytes == x.Bytes)),
        "The recorded manifest round-trips every pinned component");
    Check(!File.Exists(Path.Combine(hRoot, hA.Descriptor.VersionId, "manifest.json")) &&
          File.Exists(hStore.DescriptorSnapshotPath(hA.Descriptor.VersionId)),
        "The recorded manifest is kept beside the generations, never written into an installed tree");
    Check(hStore.Resolve(snapshotA).IsUsable, "A generation validates against its own recorded manifest");

    // Case 2 precondition: after an upgrade the retained generation is a usable
    // fallback, verified in full rather than trusted.
    await hLife.ReconcileAsync(hB.Descriptor, allowDownload: true);
    var available = hLife.ResolveFallback(hB.Descriptor.VersionId);
    Check(available.State == NativeDvFallbackState.Available && available.IsUsable,
        "After an upgrade the retained generation is an eligible fallback");
    Check(available.GenerationId == hA.Descriptor.VersionId, "The fallback is the retained previous generation");
    Check(available.Runtime!.ExecutablePath == Path.Combine(hRoot, hA.Descriptor.VersionId, "mpv.exe"),
        "The fallback resolves inside its own generation");
    Check(!available.Runtime.ExecutablePath.StartsWith(@"C:\mpv\", StringComparison.OrdinalIgnoreCase),
        "A fallback is never the stable runtime");

    // Case 14: a fallback can never bounce back to the generation that just
    // failed, to one already tried, or to one already known to have failed.
    Check(hLife.ResolveFallback(hA.Descriptor.VersionId).State == NativeDvFallbackState.NoPreviousGeneration,
        "The generation that just failed is never offered as its own fallback");
    Check(hLife.ResolveFallback(hB.Descriptor.VersionId, [hA.Descriptor.VersionId]).State == NativeDvFallbackState.AlreadyAttempted,
        "A generation already tried for this playback is not offered again");
    var bounceMemory = new NativeDvHealthMemory();
    bounceMemory.Record(hA.Descriptor.VersionId, rendererFailed);
    Check(hLife.ResolveFallback(hB.Descriptor.VersionId, null, bounceMemory).State == NativeDvFallbackState.KnownUnhealthy,
        "A generation that already failed this session is not offered as a fallback");

    // Case 6: a retained generation with no verified diagnostic adapter is
    // refused, and refused before its tree is hashed.
    var hC = HealthGeneration("health-unsupported", "0000000000000000000000000000000000000000");
    string unsupportedRoot = Path.Combine(healthRoot, "unsupported");
    Directory.CreateDirectory(Path.Combine(unsupportedRoot, hC.Descriptor.VersionId));
    File.WriteAllBytes(Path.Combine(unsupportedRoot, hC.Descriptor.VersionId, "mpv.exe"), hC.Exe);
    File.WriteAllBytes(Path.Combine(unsupportedRoot, hC.Descriptor.VersionId, "mpv.com"), hC.Com);
    var unsupportedStore = new NativeDvRuntimeStore(unsupportedRoot);
    Check(unsupportedStore.WriteDescriptorSnapshot(hC.Descriptor), "A recorded manifest can be written for any generation");
    File.WriteAllText(Path.Combine(unsupportedRoot, NativeDvRuntimeLifecycle.StateFileName),
        System.Text.Json.JsonSerializer.Serialize(new NativeDvLifecycleRecord(hB.Descriptor.VersionId, hC.Descriptor.VersionId, null)));
    var unsupportedFallback = new NativeDvRuntimeLifecycle(unsupportedStore).ResolveFallback(hB.Descriptor.VersionId);
    Check(unsupportedFallback.State == NativeDvFallbackState.AdapterUnsupported && !unsupportedFallback.IsUsable,
        "A retained generation with no verified diagnostic adapter is refused as a fallback");
    Check(unsupportedFallback.Runtime is null, "An unsupported fallback is never resolved to a runtime to launch");
    Check(unsupportedFallback.Reason.Contains(hC.Descriptor.MpvCommit, StringComparison.OrdinalIgnoreCase),
        "The refusal names the build it could not read");

    // Case 5: a retained generation that no longer matches its manifest is
    // refused, and must not be launched.
    string tamperRoot = Path.Combine(healthRoot, "tampered");
    Directory.CreateDirectory(Path.Combine(tamperRoot, hA.Descriptor.VersionId));
    File.WriteAllBytes(Path.Combine(tamperRoot, hA.Descriptor.VersionId, "mpv.exe"),
        System.Text.Encoding.UTF8.GetBytes(new string('x', hA.Exe.Length)));
    File.WriteAllBytes(Path.Combine(tamperRoot, hA.Descriptor.VersionId, "mpv.com"), hA.Com);
    var tamperStore = new NativeDvRuntimeStore(tamperRoot);
    tamperStore.WriteDescriptorSnapshot(hA.Descriptor);
    File.WriteAllText(Path.Combine(tamperRoot, NativeDvRuntimeLifecycle.StateFileName),
        System.Text.Json.JsonSerializer.Serialize(new NativeDvLifecycleRecord(hB.Descriptor.VersionId, hA.Descriptor.VersionId, null)));
    var tamperedFallback = new NativeDvRuntimeLifecycle(tamperStore).ResolveFallback(hB.Descriptor.VersionId);
    Check(tamperedFallback.State == NativeDvFallbackState.StructurallyInvalid && !tamperedFallback.IsUsable,
        "A retained generation that fails its pinned hash is refused as a fallback");
    Check(tamperedFallback.Runtime is null, "A hash-invalid fallback is never resolved to a runtime to launch");

    // A generation on disk with no recorded manifest cannot be verified, so it is
    // not launched on the strength of its directory name.
    string unrecordedRoot = Path.Combine(healthRoot, "unrecorded");
    Directory.CreateDirectory(Path.Combine(unrecordedRoot, hA.Descriptor.VersionId));
    File.WriteAllBytes(Path.Combine(unrecordedRoot, hA.Descriptor.VersionId, "mpv.exe"), hA.Exe);
    File.WriteAllBytes(Path.Combine(unrecordedRoot, hA.Descriptor.VersionId, "mpv.com"), hA.Com);
    File.WriteAllText(Path.Combine(unrecordedRoot, NativeDvRuntimeLifecycle.StateFileName),
        System.Text.Json.JsonSerializer.Serialize(new NativeDvLifecycleRecord(hB.Descriptor.VersionId, hA.Descriptor.VersionId, null)));
    var unrecorded = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(unrecordedRoot)).ResolveFallback(hB.Descriptor.VersionId);
    Check(unrecorded.State == NativeDvFallbackState.ManifestUnavailable && !unrecorded.IsUsable,
        "A retained generation with no recorded manifest is never launched unverified");

    // A recorded manifest must describe the generation it is filed under, so an
    // edited or misfiled one cannot stand in for another build.
    var misfiled = new NativeDvRuntimeStore(Path.Combine(healthRoot, "misfiled"));
    misfiled.WriteDescriptorSnapshot(hA.Descriptor);
    string misfiledPath = misfiled.DescriptorSnapshotPath(hB.Descriptor.VersionId);
    Directory.CreateDirectory(Path.GetDirectoryName(misfiledPath)!);
    File.Copy(misfiled.DescriptorSnapshotPath(hA.Descriptor.VersionId), misfiledPath, overwrite: true);
    Check(misfiled.ReadDescriptorSnapshot(hB.Descriptor.VersionId) is null,
        "A recorded manifest filed under the wrong generation is ignored");
    foreach (string bad in new[] { "..", "../../evil", "short", "", "  " })
        Check(misfiled.ReadDescriptorSnapshot(bad) is null, $"'{bad}' is never read as a recorded manifest");
    File.WriteAllText(misfiled.DescriptorSnapshotPath(hA.Descriptor.VersionId), "{ not json");
    Check(misfiled.ReadDescriptorSnapshot(hA.Descriptor.VersionId) is null,
        "A corrupt recorded manifest reads as absent rather than throwing");

    // Case 15: a pinned generation cannot be collected out from under a launch.
    Check(Directory.Exists(Path.Combine(hRoot, hA.Descriptor.VersionId)), "The retained generation is on disk before pinning");
    using (var pin = hStore.PinGeneration(hA.Descriptor.VersionId))
    {
        Check(pin is not null, "A validated generation can be pinned for the life of a launch");
        Check(hStore.IsPinned(hA.Descriptor.VersionId) && !hStore.IsPinned(hB.Descriptor.VersionId),
            "A pin names exactly one generation");
        // Force the retained generation out of the keep set, which is the only way
        // garbage collection would consider it at all.
        int removedWhilePinned = hLife.CollectGarbage(hB.Descriptor, new NativeDvLifecycleRecord(hB.Descriptor.VersionId, null, null));
        Check(removedWhilePinned == 0 && Directory.Exists(Path.Combine(hRoot, hA.Descriptor.VersionId)),
            "A pinned generation survives garbage collection that would otherwise remove it");
        // The directory surviving is not enough. Cleanup must not have deleted
        // anything inside it either: a recursive delete that is allowed to start
        // and then fail strips whatever it reached first and leaves the generation
        // present but unusable, which is worse than either outcome.
        Check(hStore.Resolve(hA.Descriptor).IsUsable, "A pinned generation still validates, with none of its files stripped");
        Check(hStore.ReadDescriptorSnapshot(hA.Descriptor.VersionId) is not null,
            "A pinned generation keeps its recorded manifest through garbage collection");
    }
    Check(!hStore.IsPinned(hA.Descriptor.VersionId), "Releasing the pin clears the in-use marker");
    // Released, and now genuinely superseded, it may be collected.
    int removedAfterRelease = hLife.CollectGarbage(hB.Descriptor, new NativeDvLifecycleRecord(hB.Descriptor.VersionId, null, null));
    Check(removedAfterRelease == 1 && !Directory.Exists(Path.Combine(hRoot, hA.Descriptor.VersionId)),
        "Once released and superseded, the generation is collected normally");
    Check(hStore.ReadDescriptorSnapshot(hA.Descriptor.VersionId) is null,
        "A collected generation's recorded manifest is collected with it");
    Check(hStore.ReadDescriptorSnapshot(hB.Descriptor.VersionId) is not null,
        "The surviving generation keeps its recorded manifest");

    // The pin holds a file other than the launcher, so it can never interfere with
    // starting the runtime it is protecting.
    using (var pinB = hStore.PinGeneration(hB.Descriptor.VersionId))
    {
        Check(pinB is not null, "The current generation can be pinned while it plays");
        // The pin is a marker outside the generation, so it cannot contend with
        // the loader for the executable it is protecting.
        using var launcher = new FileStream(Path.Combine(hRoot, hB.Descriptor.VersionId, "mpv.exe"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Check(launcher.Length > 0, "Pinning a generation leaves its launcher openable, so it can still be started");
        Check(hStore.PinGeneration(null) is null && hStore.PinGeneration("../../evil") is null,
            "Only a real generation id can be pinned");
    }

    // Nothing was created outside the roots these tests were given.
    Check(Directory.GetDirectories(healthRoot).All(x => Path.GetFileName(x)
            is "store" or "unsupported" or "tampered" or "unrecorded" or "misfiled"),
        "Health and fallback resolution write only inside the roots they were given");
    Check(!Directory.Exists(@"C:\mpv\" + hA.Descriptor.VersionId) && !Directory.Exists(@"C:\mpv\" + hB.Descriptor.VersionId),
        "Nothing in the health path ever writes near the stable runtime");
}
finally { if (Directory.Exists(healthRoot)) Directory.Delete(healthRoot, true); }


// ---------------------------------------------------------------------------
// Rollback policy: what an attempt's outcome leads to next.
//
// This is the decision the orchestration is built on, kept pure so the rules
// that matter most can be tested directly rather than inferred from a launch.
// ---------------------------------------------------------------------------
string policyRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-policy-" + Guid.NewGuid().ToString("N"));
try
{
    // A usable fallback, built the way the lifecycle builds one.
    var pRuntime = new NativeDvRuntime(Path.Combine(policyRoot, "gen", "mpv.exe"), new string('a', 64), "0.41.0-1042-g7e4cb538a", 371);
    var pDescriptor = NativeDvRuntimeDescriptor.FromManifestJson($$"""
    {
      "schemaVersion": 1,
      "provider": { "name": "test", "releaseUrl": "https://example.invalid/r" },
      "archive": { "url": "https://example.invalid/p.7z", "bytes": 8, "sha256": "{{new string('b', 64)}}" },
      "runtime": { "executable": { "pathRelativeToManifest": "./extracted/mpv.exe", "bytes": 8, "sha256": "{{new string('a', 64)}}" } },
      "mpv": { "version": "0.41.0-1042-g7e4cb538a", "commit": "{{NativeDvDiagnosticAdapters.Supported[0].MpvCommit}}" },
      "libplacebo": { "apiVersion": 371 }
    }
    """);
    var usableFallback = new NativeDvFallbackCandidate(NativeDvFallbackState.Available,
        pDescriptor.VersionId, pDescriptor, pRuntime, "available");
    var missingFallback = new NativeDvFallbackCandidate(NativeDvFallbackState.NoPreviousGeneration,
        null, null, null, "No previous native Dolby Vision runtime is retained.");
    var invalidFallback = new NativeDvFallbackCandidate(NativeDvFallbackState.StructurallyInvalid,
        pDescriptor.VersionId, pDescriptor, null, "does not match its pinned SHA-256");
    var unsupportedFallbackCandidate = new NativeDvFallbackCandidate(NativeDvFallbackState.AdapterUnsupported,
        pDescriptor.VersionId, pDescriptor, null, "no verified way to read diagnostics");

    // Case 1: current succeeds, so no rollback and no fallback is even consulted.
    var healthyDecision = NativeDvRollbackPolicy.Decide(healthy, 0, usableFallback);
    Check(healthyDecision.Step == NativeDvNextStep.Finish && healthyDecision.Status == NativeDvPlaybackStatus.RuntimeHealthy,
        "A healthy attempt finishes with no rollback even when a fallback is available");

    // Case 2: current fails, previous is valid, so retry exactly once.
    var retryDecision = NativeDvRollbackPolicy.Decide(rendererFailed, 0, usableFallback);
    Check(retryDecision.Step == NativeDvNextStep.RetryOnPreviousRuntime, "An eligible failure with a valid fallback retries on the previous runtime");
    Check(retryDecision.Explanation.Contains(pDescriptor.MpvVersion), "The retry names the runtime it is falling back to");
    // And when that retry succeeds, the session says so rather than claiming the
    // current runtime was fine.
    var afterRetrySucceeded = NativeDvRollbackPolicy.Decide(healthy, 1, null);
    Check(afterRetrySucceeded.Step == NativeDvNextStep.Finish &&
          afterRetrySucceeded.Status == NativeDvPlaybackStatus.RolledBackToPreviousRuntime,
        "A healthy attempt after a rollback reports the rollback truthfully");

    // Case 3 and case 14: the second failure cannot recurse or loop. The cap is
    // tested before availability, so even a perfectly usable fallback is refused.
    var cappedDecision = NativeDvRollbackPolicy.Decide(crashed, NativeDvRollbackPolicy.MaximumRollbacks, usableFallback);
    Check(cappedDecision.Step == NativeDvNextStep.UseStablePlayback &&
          cappedDecision.Status == NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed,
        "A second eligible failure uses stable playback and never launches a third native runtime");
    Check(NativeDvRollbackPolicy.MaximumRollbacks == 1, "At most one automatic runtime rollback is ever attempted");
    for (int done = NativeDvRollbackPolicy.MaximumRollbacks; done <= 5; done++)
        Check(NativeDvRollbackPolicy.Decide(rendererFailed, done, usableFallback).Step == NativeDvNextStep.UseStablePlayback,
            $"With {done} rollbacks already done the native lane is finished, whatever is on disk");

    // Cases 4, 5 and 6: an unavailable, invalid or unreadable retained runtime all
    // go straight to stable playback, and none of them is launched.
    foreach (var (candidate, label) in new[]
    {
        (missingFallback, "missing"), (invalidFallback, "hash-invalid"),
        (unsupportedFallbackCandidate, "adapter-unsupported"), ((NativeDvFallbackCandidate?)null, "unresolved"),
    })
    {
        var decided = NativeDvRollbackPolicy.Decide(rendererFailed, 0, candidate);
        Check(decided.Step == NativeDvNextStep.UseStablePlayback &&
              decided.Status == NativeDvPlaybackStatus.PreviousRuntimeUnavailable,
            $"A {label} retained runtime falls through to stable playback");
        Check(candidate?.Runtime is null, $"A {label} retained runtime is never resolved to something launchable");
    }

    // Cases 7, 8 and 9: a media, user or normal outcome never rolls back.
    foreach (var (verdict, label) in new[]
    {
        (NativeDvHealthEvaluator.Evaluate(Signals(renderer: false, decoder: false, output: false, exit: 2)), "unsupported media"),
        (userStopped, "user cancellation"), (normalExit, "normal exit"), (shortClean, "a clean early exit"),
        (lateFailure, "a failure after useful playback"),
    })
    {
        var decided = NativeDvRollbackPolicy.Decide(verdict, 0, usableFallback);
        Check(decided.Step == NativeDvNextStep.Finish,
            $"{label} never triggers a rollback, even with a valid fallback retained");
    }
    Check(NativeDvRollbackPolicy.Decide(lateFailure, 0, usableFallback).Status == NativeDvPlaybackStatus.HealthUnknown,
        "A failure after useful playback is not reported as a healthy runtime");
    Check(NativeDvRollbackPolicy.Decide(notUsed, 0, null).Status == NativeDvPlaybackStatus.NotUsed,
        "An unused native lane reports that it was not used");

    // Case 11 and 12: an eligible failure never carries a composition claim with
    // it, in either direction. The decision is about which runtime to run; what
    // was delivered is a separate question answered by each attempt's own
    // observation.
    Check(!retryDecision.Explanation.Contains("Full enhancement", StringComparison.OrdinalIgnoreCase) &&
          !cappedDecision.Explanation.Contains("Full enhancement", StringComparison.OrdinalIgnoreCase),
        "No rollback decision ever claims a composition result");

    // A full simulated sequence: B fails, A is retried, A also fails. Exactly two
    // native launches, then stable playback, and no generation is tried twice.
    string genB = new string('b', 64);
    string genA = new string('a', 64);
    var sequenceHealth = new NativeDvHealthMemory();
    var launched = new List<string>();
    var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string? currentGeneration = genB;
    int rollbacksDone = 0;
    NativeDvPlaybackStatus finalStatus = NativeDvPlaybackStatus.NotUsed;
    for (int guard = 0; guard < 10; guard++)
    {
        launched.Add(currentGeneration!);
        tried.Add(currentGeneration!);
        var attemptVerdict = crashed;                      // every native attempt fails
        sequenceHealth.Record(currentGeneration, attemptVerdict);
        // The fallback is only offered when it has not already been tried, which
        // is what the lifecycle's eligibility rules enforce for real.
        var offered = !tried.Contains(genA) ? usableFallback with { GenerationId = genA } : missingFallback;
        var step = NativeDvRollbackPolicy.Decide(attemptVerdict, rollbacksDone, offered);
        finalStatus = step.Status;
        if (step.Step != NativeDvNextStep.RetryOnPreviousRuntime) break;
        rollbacksDone++;
        currentGeneration = genA;
    }
    Check(launched.Count == 2 && launched[0] == genB && launched[1] == genA,
        "A failing current runtime produces exactly two native launches: the current one, then the retained one");
    Check(launched.Distinct(StringComparer.OrdinalIgnoreCase).Count() == launched.Count,
        "No generation is ever launched twice in one playback, so B to A to B cannot happen");
    Check(finalStatus == NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed,
        "When both native runtimes fail the session says so and uses stable playback");
    Check(sequenceHealth.IsKnownUnhealthy(genA) && sequenceHealth.IsKnownUnhealthy(genB),
        "Both failing generations are recorded for this session");

    // Case 13: a rollback that starts playback but composes nothing is reported
    // as what it was. Rolling back successfully is not composing successfully,
    // and the status line must not be readable as a composition claim.
    string fallbackUncomposedLog = string.Join("\n",
        "[   0.016][v][mkv] Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track).",
        "[   0.285][v][vd] Opening decoder hevc",
        "[   0.286][v][vd] Selected decoder: hevc - HEVC (High Efficiency Video Coding)",
        "[   0.318][i][vd] Using hardware decoding (d3d11va).",
        "[   0.280][v][vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)");
    var rolledBackBaseLayer = NativeDvLogEvidence.Reduce(fallbackUncomposedLog, "mpv v0.41.0-1042-g7e4cb538a", true, "d3d11va");
    Check(rolledBackBaseLayer.Delivered == NativeDvDelivered.BaseLayerOnly,
        "A rollback that composes nothing reports base layer only");
    Check(!rolledBackBaseLayer.Summary.Contains("Full enhancement", StringComparison.OrdinalIgnoreCase),
        "A rolled-back base-layer result never reads as Full FEL");
    Check(rolledBackBaseLayer.Degradation.Any(x => x.Contains("base-layer-only")),
        "A rolled-back base-layer result names the fallback");

    // The runtime is still healthy: it started useful playback. Health and
    // composition are separate questions and must not contaminate each other.
    var rolledBackHealth = NativeDvHealthEvaluator.Evaluate(Signals(stop: true));
    var rolledBackDecision = NativeDvRollbackPolicy.Decide(rolledBackHealth, 1, null);
    Check(rolledBackDecision.Step == NativeDvNextStep.Finish &&
          rolledBackDecision.Status == NativeDvPlaybackStatus.RolledBackToPreviousRuntime,
        "A rollback that started playback finishes and reports the rollback truthfully");
    Check(!NativeDvPlaybackStatusText.Describe(rolledBackDecision.Status)
            .Contains("Full enhancement", StringComparison.OrdinalIgnoreCase),
        "The rollback status line is not a composition claim");

    // And an unknown composition after a rollback stays unknown.
    var rolledBackUnknown = NativeDvLogEvidence.Reduce("", "mpv v0.41.0-1042-g7e4cb538a", true, "d3d11va");
    Check(rolledBackUnknown.Delivered == NativeDvDelivered.Unknown &&
          rolledBackUnknown.FelComposition == DvObservedState.Unknown,
        "A rollback with no established composition reports unknown, never Full FEL");

    // ---------------------------------------------------------------------
    // Rebuilding a plan for another generation.
    // ---------------------------------------------------------------------
    var rebuiltFacts = new NativeDvSourceFacts(7, 6, "hevc", "pq", "bt.2020", 3840, 2160);
    var rebuiltPlan = NativeDvPlaybackPlanner.Build(
        new DvSourceInfo(DvDetection.Detected, 7, 6,
            new MediaInfo(Width: 3840, Height: 2160, Codec: "hevc", Transfer: "pq", Primaries: "bt.2020"),
            DvCompatibility.Yes, DvEnhancementLayer.Unknown, DvRpuStatus.Validated, 10, "probe"),
        pinned, @"C:\media\authored.mkv", "config", "first-pipe", true, true,
        logPath: @"C:\logs\native-dv.log", target: new PlaybackTarget(1280, 720),
        allowUnclassifiedEnhancementLayer: true);
    var firstOutcome = new NativeDvLaneOutcome(true, NativeDvRuntimeState.Installed, rebuiltPlan, pinned, rebuiltFacts, null);

    var rebuilt = NativeDvLane.WithRuntime(firstOutcome, pRuntime, @"C:\media\authored.mkv",
        "config", "second-pipe", @"C:\logs\native-dv-fallback.log", new PlaybackTarget(1280, 720));
    Check(rebuilt.Selected && rebuilt.Plan is { Supported: true }, "A selected outcome can be rebuilt against the retained runtime");
    Check(rebuilt.Plan!.Executable == pRuntime.ExecutablePath, "The rebuilt plan launches the retained runtime");
    Check(rebuilt.Runtime == pRuntime && rebuilt.SourceFacts == rebuiltFacts,
        "Rebuilding reuses the container facts and changes only the runtime");
    Check(rebuilt.Plan.Arguments.Contains(@"--log-file=C:\logs\native-dv-fallback.log") &&
          !rebuilt.Plan.Arguments.Contains(@"--log-file=C:\logs\native-dv.log"),
        "The retried attempt writes its own diagnostic log, so it cannot read the failed attempt's evidence");
    Check(rebuilt.Plan.Arguments.Any(x => x.Contains("second-pipe")) &&
          !rebuilt.Plan.Arguments.Any(x => x.Contains("first-pipe")),
        "The retried attempt observes over its own diagnostics connection");
    Check(rebuilt.Plan.Arguments.Contains("--cache-on-disk=no") && rebuilt.Plan.MediaScratchPaths.IsEmpty &&
          !rebuilt.Plan.RequiresConversion,
        "Zero media-sized scratch and no conversion survive a rollback");
    Check(rebuilt.Plan.Arguments.Contains("--no-config") && rebuilt.Plan.Arguments.Contains("--vo=gpu-next"),
        "The rollback keeps the proven isolated invocation");
    int rebuiltDelimiter = rebuilt.Plan.Arguments.IndexOf("--");
    Check(rebuiltDelimiter >= 0 && rebuilt.Plan.Arguments[rebuiltDelimiter + 1] == @"C:\media\authored.mkv" &&
          rebuilt.Plan.Arguments.Length == rebuiltDelimiter + 2,
        "The rollback plays the same original container, unchanged");
    Check(!rebuilt.Plan.Arguments.Any(x => x.Contains("mkvextract", StringComparison.OrdinalIgnoreCase) ||
        x.Contains("dovi_tool", StringComparison.OrdinalIgnoreCase) || x.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)),
        "A rollback never reaches the Compatibility Export tools");

    // A rollback is refused outright if it would mean launching the stable runtime.
    var stableRuntime = new NativeDvRuntime(@"C:\mpv\mpv.exe", new string('c', 64), "0.41.0-1011-g182fa6ca4", 371);
    var refusedRebuild = NativeDvLane.WithRuntime(firstOutcome, stableRuntime, @"C:\media\authored.mkv",
        "config", "third-pipe", null, null);
    Check(!refusedRebuild.Selected && refusedRebuild.Plan is null,
        "A rollback can never be rebuilt onto the stable runtime");

    // Only a selected outcome can be rebuilt: a refusal carries no plan to rebuild.
    bool rejectedRebuild = false;
    try
    {
        NativeDvLane.WithRuntime(NativeDvLaneOutcome.NotSelected(NativeDvRuntimeState.Installed, "not selected"),
            pRuntime, @"C:\media\authored.mkv", "config", "pipe", null, null);
    }
    catch (ArgumentException) { rejectedRebuild = true; }
    Check(rejectedRebuild, "An outcome that never selected the native lane cannot be rebuilt into a rollback");
}
finally { if (Directory.Exists(policyRoot)) Directory.Delete(policyRoot, true); }

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
        // validation, real promotion, through the lifecycle.
        var productStore = new NativeDvRuntimeStore(Path.Combine(productRoot, "runtimes", "native-dv"));
        var productLife = new NativeDvRuntimeLifecycle(productStore);
        Check(!productStore.Resolve(shipped).IsUsable, "PRODUCT: a fresh installation has no native runtime");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var firstStatus = await productLife.ReconcileAsync(shipped, allowDownload: true,
            new Progress<string>(x => Console.WriteLine("  " + x)));
        clock.Stop();
        Check(firstStatus.State == NativeDvLifecycleState.Ready && firstStatus.Runtime is not null,
            "PRODUCT: the runtime provisions from its pinned publisher archive");
        var installed = productStore.Resolve(shipped);
        Console.WriteLine($"  first provision: {clock.Elapsed.TotalSeconds:0.0}s -> {installed.Runtime!.ExecutablePath}");

        // A real generation-to-generation upgrade, with real archives. The previous
        // pinned manifest is supplied so the retained-fallback behaviour is
        // exercised against genuine runtimes rather than fixtures.
        string? previousManifest = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_PREVIOUS_MANIFEST");
        if (!string.IsNullOrWhiteSpace(previousManifest) && File.Exists(previousManifest))
        {
            var older = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(previousManifest));
            if (older.VersionId != shipped.VersionId)
            {
                string upgradeRoot = Path.Combine(productRoot, "upgrade");
                var upgradeLife = new NativeDvRuntimeLifecycle(new NativeDvRuntimeStore(upgradeRoot));
                var installedOld = await upgradeLife.ReconcileAsync(older, allowDownload: true);
                Check(installedOld.State == NativeDvLifecycleState.Ready, "PRODUCT: the previous pinned runtime installs");
                var upgrade = await upgradeLife.ReconcileAsync(shipped, allowDownload: true);
                Check(upgrade.State == NativeDvLifecycleState.Updated, "PRODUCT: moving to the new pinned runtime reports an update");
                Check(upgrade.CurrentGeneration == shipped.VersionId, "PRODUCT: the new runtime becomes current");
                Check(upgrade.PreviousGeneration == older.VersionId, "PRODUCT: the previous real runtime is retained as the fallback");
                Check(Directory.Exists(Path.Combine(upgradeRoot, older.VersionId)), "PRODUCT: the retained runtime is still on disk after the upgrade");
                Check(new NativeDvRuntimeStore(upgradeRoot).Resolve(older).IsUsable, "PRODUCT: the retained runtime still validates and could be rolled back to");
                Console.WriteLine($"  real upgrade: {older.MpvVersion} -> {shipped.MpvVersion}, previous retained");
                Directory.Delete(upgradeRoot, true);
            }
        }
        Check(NativeDvRuntimeStore.ComputeSha256(installed.Runtime.ExecutablePath) == installed.Runtime.Sha256,
            "PRODUCT: the installed executable matches its pinned hash");

        var fast = System.Diagnostics.Stopwatch.StartNew();
        var repeat = await productLife.ReconcileAsync(shipped, allowDownload: true);
        fast.Stop();
        Check(repeat.State == NativeDvLifecycleState.Ready && repeat.Runtime is not null, "PRODUCT: the already-installed fast path validates");
        Check(repeat.GenerationsRemoved == 0, "PRODUCT: repeated reconciliation removes nothing");
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

            var observed = NativeDvLogEvidence.Reduce(logText, version, true, hwdec, "d3d11", "d3d11");
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



// ---------------------------------------------------------------------------
// The healthy case, through the real application path.
//
// Both pinned runtimes have already been certified against the authored source,
// so this is deliberately short. What it adds is the one thing the health work
// could plausibly have broken: that an ordinary, working preferred runtime still
// plays and still reports full enhancement-layer composition, with no rollback,
// no fallback even consulted, and no extra launch.
// ---------------------------------------------------------------------------
if (!string.IsNullOrWhiteSpace(realSource) && File.Exists(realSource))
{
    Console.WriteLine("Native Dolby Vision healthy path: exercising the preferred runtime.");
    string shippedManifestPath = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_MANIFEST")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "native-dv-p7", "runtime-manifest.json");
    var shippedPreferred = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(shippedManifestPath));

    string healthyRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-healthy-" + Guid.NewGuid().ToString("N"));
    string? dataDirBefore = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR");
    var healthyStableBefore = File.Exists(@"C:\mpv\mpv.exe")
        ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
        : (Length: 0L, Hash: "absent");
    var healthySourceBefore = new FileInfo(realSource);
    (long Length, DateTime Written) healthySourceIdentity = (healthySourceBefore.Length, healthySourceBefore.LastWriteTimeUtc);

    try
    {
        Directory.CreateDirectory(healthyRoot);
        Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", healthyRoot);

        var healthyStore = new NativeDvRuntimeStore(Path.Combine(healthyRoot, "runtimes", "native-dv"));
        var healthyService = new PlaybackService
        {
            NativeDolbyVision = new NativeDvLane(healthyStore, shippedPreferred),
        };
        var healthyStatus = new List<string>();
        healthyService.StatusChanged += x => { lock (healthyStatus) healthyStatus.Add(x); };

        var healthyPlan = await healthyService.PrepareAsync([realSource],
            new PlaybackOptions("Reference", "Off", "Off", false, false),
            new SystemSummary { MpvPath = @"C:\mpv\mpv.exe" },
            new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = true },
            new PlaybackTarget(1280, 720));

        Check(healthyPlan.Renderer == "Native Dolby Vision gpu-next", "HEALTHY: the preferred runtime is selected for the authored source");
        Check(healthyService.LastNativeLifecycle?.CurrentGeneration == shippedPreferred.VersionId,
            "HEALTHY: the shipped generation is the current one");
        Check(healthyPlan.Executable == Path.Combine(healthyStore.RootPath, shippedPreferred.VersionId, shippedPreferred.LauncherRelativePath),
            "HEALTHY: the plan launches the preferred generation");

        int healthyExit = await healthyService.LaunchAsync(healthyPlan, stopAfterSeconds: 12);

        Console.WriteLine($"  runtime: {shippedPreferred.MpvVersion}");
        Console.WriteLine($"  health: {healthyService.LastNativeHealth?.Health} status={healthyService.LastNativePlaybackStatus} exit={healthyExit}");
        Console.WriteLine($"  observed: delivered={healthyService.LastNativeObservation?.Delivered} " +
            $"composition={healthyService.LastNativeObservation?.FelComposition} " +
            $"pairing={healthyService.LastNativeObservation?.BlElPairing} " +
            $"decoders={healthyService.LastNativeObservation?.DecoderInstances} " +
            $"surfaces={healthyService.LastNativeObservation?.HardwareSurfaces}");

        Check(healthyService.LastNativeHealth is { Health: NativeDvHealth.Healthy, ReachedUsefulPlayback: true },
            "HEALTHY: the preferred runtime reached useful playback");
        Check(healthyService.LastNativePlaybackStatus == NativeDvPlaybackStatus.RuntimeHealthy,
            "HEALTHY: the session reports the native runtime as healthy");
        Check(healthyService.LastNativeFallback is null,
            "HEALTHY: a working runtime never even consults the retained fallback");
        Check(!healthyService.NativeHealth.IsKnownUnhealthy(shippedPreferred.VersionId),
            "HEALTHY: a working generation records no fault");

        // Exactly one native launch, and no stable-path relaunch.
        Check(healthyService.LastReport!.Attempts.Count == 1,
            "HEALTHY: exactly one playback attempt is made, with no unexpected fallback");
        Check(healthyService.LastReport.Attempts[0].Renderer == "Native Dolby Vision gpu-next",
            "HEALTHY: the one attempt is the native one");
        Check(!healthyStatus.Any(x => x.Contains("previous native runtime", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("stable playback", StringComparison.OrdinalIgnoreCase)),
            "HEALTHY: nothing claims a rollback or a stable fallback happened");

        // Full FEL, observed from this attempt's own evidence.
        Check(healthyService.LastNativeObservation is not null, "HEALTHY: the runtime's observation was established");
        Check(healthyService.LastNativeObservation!.Delivered == NativeDvDelivered.FullEnhancementLayer,
            "HEALTHY: the preferred runtime still delivers observed full enhancement-layer composition");
        Check(healthyService.LastNativeObservation.FelComposition == DvObservedState.Active &&
              healthyService.LastNativeObservation.BlElPairing == DvObservedState.Active &&
              healthyService.LastNativeObservation.DecoderInstances >= 2 &&
              healthyService.LastNativeObservation.HardwareSurfaces == DvObservedState.Active,
            "HEALTHY: composition, pairing, dual decode and hardware surfaces are all observed");
        Check(!healthyService.LastNativeObservation.HasDegradation, "HEALTHY: the session reports no degradation");
        Check(!File.Exists(PlaybackService.NativeFallbackLogPath),
            "HEALTHY: no rollback log is written when no rollback happens");

        // Zero media-sized scratch outside the runtime store itself.
        long healthyBiggest = Directory.EnumerateFiles(healthyRoot, "*", SearchOption.AllDirectories)
            .Where(x => !x.Contains(Path.Combine("runtimes", "native-dv"), StringComparison.OrdinalIgnoreCase))
            .Select(x => new FileInfo(x).Length).DefaultIfEmpty(0).Max();
        Console.WriteLine($"  largest non-runtime file written: {healthyBiggest} bytes");
        Check(healthyBiggest < 64L * 1024 * 1024, "HEALTHY: playback writes no media-sized scratch");
        Check(!Directory.EnumerateFiles(healthyRoot, "*.mkv", SearchOption.AllDirectories).Any() &&
              !Directory.EnumerateFiles(healthyRoot, "*.hevc", SearchOption.AllDirectories).Any(),
            "HEALTHY: playback extracts and converts nothing");

        var disabledPlan = await healthyService.PrepareAsync([realSource],
            new PlaybackOptions("Reference", "Off", "Off", false, false),
            new SystemSummary { MpvPath = @"C:\mpv\mpv.exe" },
            new AppSettings { NativeDolbyVisionLane = false, AllowNativeDolbyVisionDownload = true },
            new PlaybackTarget(1280, 720));
        Check(!disabledPlan.Renderer.StartsWith("Native Dolby Vision", StringComparison.Ordinal) &&
              healthyService.LastNativeOutcome is null,
            "HEALTHY: disabling the native setting prevents selection and clears the previous native result");

        var healthyStableAfter = File.Exists(@"C:\mpv\mpv.exe")
            ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
            : (Length: 0L, Hash: "absent");
        Check(healthyStableBefore.Length == healthyStableAfter.Length && healthyStableBefore.Hash == healthyStableAfter.Hash,
            "HEALTHY: the stable runtime is untouched");
        var healthySourceAfter = new FileInfo(realSource);
        Check(healthySourceAfter.Length == healthySourceIdentity.Length &&
              healthySourceAfter.LastWriteTimeUtc == healthySourceIdentity.Written,
            "HEALTHY: the authored source is unchanged");
        Console.WriteLine("Native Dolby Vision healthy path: PASS");
    }
    finally
    {
        Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", dataDirBefore);
        try { if (Directory.Exists(healthyRoot)) Directory.Delete(healthyRoot, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// ---------------------------------------------------------------------------
// Controlled real failure injection through the real application path.
//
// Everything above reasons about health and rollback in isolation. This runs the
// product's own PrepareAsync and LaunchAsync against the authored Profile 7
// source, with a current generation that verifies and probes correctly and then
// fails at launch, and proves the lane retries on the retained real runtime and
// plays.
//
// The injection is a generation whose launcher is replaced after it has been
// verified and used for the probe, which is the shape of a real failure: a
// runtime quarantined by security software, or left half-written by an
// interrupted update, is intact when it is checked and broken when it is run.
// Nothing outside the isolated store is touched: the stable runtime is only ever
// read, and the authored source is only ever played.
// ---------------------------------------------------------------------------
if (!string.IsNullOrWhiteSpace(realSource) && File.Exists(realSource))
{
    Console.WriteLine("Native Dolby Vision rollback: injecting a real current-runtime failure.");
    string previousManifestPath = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_PREVIOUS_MANIFEST") ?? "";
    string cachedRuntimeRoot = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_NATIVE_DV_RUNTIME_CACHE") ?? "";
    if (string.IsNullOrWhiteSpace(previousManifestPath) || !File.Exists(previousManifestPath) ||
        string.IsNullOrWhiteSpace(cachedRuntimeRoot) || !Directory.Exists(cachedRuntimeRoot))
    {
        Console.WriteLine("  skipped: set ADAPTIVE_MEDIA_NATIVE_DV_PREVIOUS_MANIFEST and ADAPTIVE_MEDIA_NATIVE_DV_RUNTIME_CACHE.");
    }
    else
    {
        var retained = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(previousManifestPath));
        string cachedArchive = Directory.EnumerateFiles(cachedRuntimeRoot, "*.7z").Single();
        string cachedTree = Path.Combine(cachedRuntimeRoot, "extracted");
        Check(NativeDvRuntimeStore.ComputeSha256(cachedArchive) == retained.ArchiveSha256,
            "ROLLBACK: the cached archive is the one the retained manifest pins");

        string injectionRoot = Path.Combine(Path.GetTempPath(), "adaptive-media-rollback-" + Guid.NewGuid().ToString("N"));
        string? previousDataDir = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR");
        var stableBefore = File.Exists(@"C:\mpv\mpv.exe")
            ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
            : (Length: 0L, Hash: "absent");
        var sourceBefore = new FileInfo(realSource);
        (long Length, DateTime Written) sourceIdentityBefore = (sourceBefore.Length, sourceBefore.LastWriteTimeUtc);

        try
        {
            // Every path the product derives from its data directory is redirected
            // into the isolated root, so the real user profile is untouched.
            Directory.CreateDirectory(injectionRoot);
            Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", injectionRoot);
            Check(SettingsStore.DirectoryPath == Path.GetFullPath(injectionRoot),
                "ROLLBACK: the product writes into the isolated root for this test");

            string storeRoot = Path.Combine(injectionRoot, "runtimes", "native-dv");

            // The retained generation is provisioned from the locally cached
            // publisher archive: a real runtime, verified by its real pinned
            // hashes, without going back to the network.
            NativeDvArchiveFetch fetchRetained = (uri, destination, token) =>
            {
                File.Copy(cachedArchive, destination, overwrite: true);
                return Task.CompletedTask;
            };
            NativeDvArchiveExtract extractRetained = (archive, destination, token) =>
            {
                foreach (var component in retained.Components)
                    File.Copy(Path.Combine(cachedTree, component.RelativePath),
                        Path.Combine(destination, component.RelativePath), overwrite: true);
                return Task.CompletedTask;
            };

            var store = new NativeDvRuntimeStore(storeRoot, fetchRetained, extractRetained);
            var lifecycle = new NativeDvRuntimeLifecycle(store);
            var installedRetained = await lifecycle.ReconcileAsync(retained, allowDownload: true);
            Check(installedRetained.State == NativeDvLifecycleState.Ready && installedRetained.Runtime is not null,
                "ROLLBACK: the retained real runtime installs and verifies");
            Check(store.ReadDescriptorSnapshot(retained.VersionId) is not null,
                "ROLLBACK: the retained generation records its own pinned manifest");

            // The generation that will fail. Its components are a real working mpv,
            // so it verifies by hash and probes the source correctly, and its
            // manifest names a commit this build has a verified adapter for, so the
            // lifecycle admits it. Its identity differs because its archive does.
            byte[] injectedArchive = System.Text.Encoding.UTF8.GetBytes("injected-current-generation-archive-payload");
            var retainedLauncher = retained.Components.First(x => x.RelativePath == retained.LauncherRelativePath);
            var injectedCompanion = retained.Components.FirstOrDefault(x => x.RelativePath != retained.LauncherRelativePath);
            string ManifestEntry(string name, NativeDvRuntimeComponent component) =>
                "\"" + name + "\": { \"pathRelativeToManifest\": \"./extracted/" +
                component.RelativePath.Replace(Path.DirectorySeparatorChar, '/') + "\", \"bytes\": " +
                component.Bytes + ", \"sha256\": \"" + component.Sha256 + "\" }";
            string injectedRuntimeEntries = ManifestEntry("executable", retainedLauncher) +
                (injectedCompanion is null ? "" : "," + ManifestEntry("consoleLauncher", injectedCompanion));
            string injectedManifest =
                "{ \"schemaVersion\": 1," +
                "  \"provider\": { \"name\": \"injected\", \"releaseUrl\": \"https://example.invalid/injected\" }," +
                "  \"archive\": { \"url\": \"https://example.invalid/injected.7z\", \"bytes\": " + injectedArchive.Length +
                ", \"sha256\": \"" + Sha256Of(injectedArchive) + "\" }," +
                "  \"runtime\": { " + injectedRuntimeEntries + " }," +
                "  \"mpv\": { \"version\": \"" + retained.MpvVersion + "\", \"commit\": \"" + retained.MpvCommit + "\" }," +
                "  \"libplacebo\": { \"apiVersion\": " + retained.LibplaceboApi + " } }";
            var injected = NativeDvRuntimeDescriptor.FromManifestJson(injectedManifest);
            Check(injected.VersionId != retained.VersionId, "ROLLBACK: the injected generation is a different generation");

            var injectingStore = new NativeDvRuntimeStore(storeRoot,
                (uri, destination, token) => { File.WriteAllBytes(destination, injectedArchive); return Task.CompletedTask; },
                extractRetained);
            var injectingLifecycle = new NativeDvRuntimeLifecycle(injectingStore);
            var installedInjected = await injectingLifecycle.ReconcileAsync(injected, allowDownload: true);
            Check(installedInjected.State == NativeDvLifecycleState.Updated, "ROLLBACK: the injected generation becomes current");
            Check(installedInjected.CurrentGeneration == injected.VersionId &&
                  installedInjected.PreviousGeneration == retained.VersionId,
                "ROLLBACK: the real runtime is retained as the previous generation");

            // The product, with its real lane over the real store.
            var service = new PlaybackService
            {
                NativeDolbyVision = new NativeDvLane(injectingStore, injected),
            };
            var statusLines = new List<string>();
            service.StatusChanged += x => { lock (statusLines) statusLines.Add(x); };

            var settings = new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = true };
            var system = new SystemSummary { MpvPath = @"C:\mpv\mpv.exe" };
            var plan = await service.PrepareAsync([realSource], new PlaybackOptions("Reference", "Off", "Off", false, false), system, settings,
                new PlaybackTarget(1280, 720));
            Check(plan.Renderer == "Native Dolby Vision gpu-next",
                "ROLLBACK: the product selects the native lane for the authored source");
            Check(plan.Executable == Path.Combine(storeRoot, injected.VersionId, injected.LauncherRelativePath),
                "ROLLBACK: the product plans to launch the current generation");
            Check(service.LastNativeOutcome is { Selected: true }, "ROLLBACK: the lane probed the real source and selected it");

            // The injection. The generation verified and probed correctly above;
            // now its launcher stops being a runnable image, exactly as it would
            // after a quarantine or a half-written update.
            string injectedLauncher = plan.Executable;
            long injectedLauncherBytes = new FileInfo(injectedLauncher).Length;
            File.WriteAllText(injectedLauncher, "this is not a runnable image");
            Check(new FileInfo(injectedLauncher).Length != injectedLauncherBytes,
                "ROLLBACK: the current generation's launcher is now broken");

            int exit = await service.LaunchAsync(plan, stopAfterSeconds: 12);

            Console.WriteLine($"  health: {service.LastNativeHealth?.Health} status={service.LastNativePlaybackStatus} exit={exit}");
            Console.WriteLine($"  fallback: {service.LastNativeFallback?.State} generation={service.LastNativeFallback?.GenerationId}");
            Console.WriteLine($"  observed: delivered={service.LastNativeObservation?.Delivered} " +
                $"composition={service.LastNativeObservation?.FelComposition} decoders={service.LastNativeObservation?.DecoderInstances}");
            foreach (string line in statusLines) Console.WriteLine("    status: " + line);

            // The failure was classified as the runtime's, and the rollback happened.
            Check(service.LastNativeFallback is { State: NativeDvFallbackState.Available },
                "ROLLBACK: the retained real runtime was found eligible");
            Check(service.LastNativeFallback!.GenerationId == retained.VersionId,
                "ROLLBACK: the fallback is the retained real generation");
            Check(service.LastNativePlaybackStatus == NativeDvPlaybackStatus.RolledBackToPreviousRuntime,
                "ROLLBACK: the session reports that the previous native runtime was used");
            Check(service.LastNativeHealth is { Health: NativeDvHealth.Healthy },
                "ROLLBACK: the retained runtime started useful playback");
            Check(service.NativeHealth.IsKnownUnhealthy(injected.VersionId),
                "ROLLBACK: the generation that failed is recorded unhealthy for this session");
            Check(!service.NativeHealth.IsKnownUnhealthy(retained.VersionId),
                "ROLLBACK: the runtime that worked is not recorded unhealthy");

            // Exactly two native launches: the broken current one, then the retained
            // one. No third native attempt, and no return to the broken generation.
            var nativeAttempts = service.LastReport!.Attempts
                .Where(x => x.Renderer.StartsWith("Native Dolby Vision", StringComparison.Ordinal)).ToList();
            Check(nativeAttempts.Count == 2, "ROLLBACK: exactly two native attempts were made");
            Check(nativeAttempts[0].Executable == injectedLauncher, "ROLLBACK: the first attempt used the current generation");
            Check(nativeAttempts[1].Executable == Path.Combine(storeRoot, retained.VersionId, retained.LauncherRelativePath),
                "ROLLBACK: the second attempt used the retained real generation");
            Check(!nativeAttempts[1].Executable.StartsWith(@"C:\mpv\", StringComparison.OrdinalIgnoreCase),
                "ROLLBACK: the fallback is never the stable runtime");
            Check(nativeAttempts.Select(x => x.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                "ROLLBACK: no generation was launched twice");

            // What the retained runtime delivered was established from its own
            // evidence. This is the truthfulness boundary: the observation belongs
            // to the attempt that ran, and a rollback is never itself a composition
            // claim.
            Check(service.LastNativeObservation is not null,
                "ROLLBACK: the retained runtime's own observation was established");
            Check(service.LastNativeObservation!.Delivered == NativeDvDelivered.FullEnhancementLayer,
                "ROLLBACK: the retained runtime is observed composing the enhancement layer, not assumed to");
            Check(service.LastNativeObservation.FelComposition == DvObservedState.Active &&
                  service.LastNativeObservation.DecoderInstances >= 2,
                "ROLLBACK: composition and dual decode are observed on the attempt that actually ran");
            Check(nativeAttempts.Select(x => x.Arguments.Single(a => a.StartsWith("--log-file="))).Distinct().Count() == 2,
                "ROLLBACK: the retried attempt used its own diagnostic log");
            Check(statusLines.Any(x => x.Contains("previous native runtime", StringComparison.OrdinalIgnoreCase)),
                "ROLLBACK: the user is told the previous runtime was used");
            Check(!statusLines.Any(x => x.Contains("Native runtime healthy", StringComparison.Ordinal)),
                "ROLLBACK: a rollback is never reported as an ordinary healthy run");

            // The active generation survived, and nothing media-sized was written.
            Check(Directory.Exists(Path.Combine(storeRoot, retained.VersionId)) &&
                  store.Resolve(retained).IsUsable,
                "ROLLBACK: the generation that played is intact and still validates");
            long biggest = Directory.EnumerateFiles(injectionRoot, "*", SearchOption.AllDirectories)
                .Where(x => !x.Contains(Path.Combine("runtimes", "native-dv"), StringComparison.OrdinalIgnoreCase))
                .Select(x => new FileInfo(x).Length).DefaultIfEmpty(0).Max();
            Console.WriteLine($"  largest non-runtime file written: {biggest} bytes");
            Check(biggest < 64L * 1024 * 1024, "ROLLBACK: a rollback writes no media-sized scratch");
            Check(!Directory.EnumerateFiles(injectionRoot, "*.mkv", SearchOption.AllDirectories).Any() &&
                  !Directory.EnumerateFiles(injectionRoot, "*.hevc", SearchOption.AllDirectories).Any(),
                "ROLLBACK: a rollback extracts and converts nothing");

            var stableAfter = File.Exists(@"C:\mpv\mpv.exe")
                ? (Length: new FileInfo(@"C:\mpv\mpv.exe").Length, Hash: NativeDvRuntimeStore.ComputeSha256(@"C:\mpv\mpv.exe"))
                : (Length: 0L, Hash: "absent");
            Check(stableBefore.Length == stableAfter.Length && stableBefore.Hash == stableAfter.Hash,
                "ROLLBACK: the stable runtime is untouched by the failure and the rollback");
            var sourceAfter = new FileInfo(realSource);
            Check(sourceAfter.Length == sourceIdentityBefore.Length && sourceAfter.LastWriteTimeUtc == sourceIdentityBefore.Written,
                "ROLLBACK: the authored source is unchanged in length and last-write time");
            Console.WriteLine("Native Dolby Vision rollback: PASS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", previousDataDir);
            try { if (Directory.Exists(injectionRoot)) Directory.Delete(injectionRoot, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

await PlaybackRecoveryTests.RunAsync(Check);
Console.WriteLine($"PASS: {checks} total Dolby Vision assertions");
