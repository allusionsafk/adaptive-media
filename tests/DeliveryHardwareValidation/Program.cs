using AdaptiveMedia;
using System.Diagnostics;
using System.Text.Json;

// Real-hardware enhancement delivery validation. The production PlaybackService
// prepares and launches a real mpv on the real GPU; this program only chooses the
// intent, reads the truth surface the product would show, and (for the fallback
// scenario) crashes the first player the way a driver or runtime fault would.
//
// usage: DeliveryHardwareValidation <mpv.exe> <media> <output-dir> <nvidia-adapter-name>
if (args.Length < 4) { Console.Error.WriteLine("usage: <mpv.exe> <media> <output-dir> <nvidia-adapter>"); return 2; }
string mpv = Path.GetFullPath(args[0]), media = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]), adapter = args[3];
Directory.CreateDirectory(output);
// Diagnostics go to this run's folder, never to the user's own data directory.
Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", Path.Combine(output, "data"));
var system = new SystemSummary { MpvPath = mpv, HasNvidia = true, NvidiaAdapter = adapter };
var settings = new AppSettings { AutoHdrSwitch = false, NativeDolbyVisionLane = false };
// A windowed 2048×1152 output on the laptop's 240 Hz panel, so a 540p source is
// really upscaled and a 25 fps source really needs cadence correction.
var target = new PlaybackTarget(2048, 1152, 0, false, 240);
var json = new JsonSerializerOptions { WriteIndented = true };
var failures = new List<string>();
var results = new List<object>();

object Snapshot(PlaybackTruthReport? t) => t is null ? new { missing = true } : new
{
    requested = t.Requested.Lines, planned = t.Planned.Lines, observed = t.Observed.Lines, fallback = t.Fallback.Lines,
    health = t.Health.Lines, recovery = t.Recovery.Lines,
    history = t.History.Select(h => new { h.Title, h.Lines }),
    delivery = t.Delivery.Select(v => new { feature = v.Feature.ToString(), state = v.State.ToString(), v.Evidence }),
};

void Expect(string scenario, PlaybackTruthReport? t, DeliveryFeature feature, params DeliveryState[] allowed)
{
    var verdict = t?.Delivery.SingleOrDefault(v => v.Feature == feature);
    if (verdict is null || !allowed.Contains(verdict.State))
        failures.Add($"{scenario}: {feature} expected {string.Join("/", allowed)}, got {verdict?.State.ToString() ?? "not planned"} ({verdict?.Evidence})");
}

void NeverVerifiedRtx(string scenario, PlaybackTruthReport? t)
{
    foreach (var v in t?.Delivery ?? [])
        if (v.Feature is DeliveryFeature.RtxSuperResolution or DeliveryFeature.RtxVideoHdr && v.State == DeliveryState.Verified)
            failures.Add($"{scenario}: {v.Feature} was reported verified");
}

async Task<JsonElement?> Property(string pipe, string name)
{
    await using var ipc = new MpvIpc(pipe);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await ipc.ConnectAsync(timeout.Token);
    return await ipc.CommandAsync(["get_property", name], timeout.Token);
}

async Task Command(string pipe, object[] command)
{
    await using var ipc = new MpvIpc(pipe);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await ipc.ConnectAsync(timeout.Token);
    await ipc.CommandAsync(command, timeout.Token);
}

async Task<PlaybackTruthReport?> WaitFor(PlaybackService service, Func<PlaybackTruthReport, bool> ready, TimeSpan limit)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < limit)
    {
        if (service.CurrentTruth() is { } t && ready(t)) return t;
        await Task.Delay(250);
    }
    return service.CurrentTruth();
}

bool Settled(PlaybackTruthReport t) => t.Delivery.Count > 0 && t.Delivery.All(v => v.State != DeliveryState.Pending);

async Task<PlaybackPlan> Prepare(PlaybackService service, EnhancementIntent intent) =>
    await service.PrepareAsync([media], new PlaybackOptions("Enhanced", "Off", "Off", false, false, Intent: intent), system, settings, target);

// 1. RTX lane: D3D11VA, NVIDIA VPP with RTX VSR requested, blend smoothing, cleanup.
//    Then the window is shrunk while playing: VPP must go idle, not stay verified.
{
    const string name = "rtx-vpp-blend-deband";
    var service = new PlaybackService();
    var plan = await Prepare(service, EnhancementIntent.ForEnhanced(DetailIntent.Maximum, MotionIntent.BlendSmooth, CleanupIntent.Clean, PerformanceIntent.MaximumQuality));
    var run = service.LaunchAsync(plan, 16);
    var full = await WaitFor(service, Settled, TimeSpan.FromSeconds(8));
    await Task.Delay(1500);
    full = service.CurrentTruth();
    try { await Command(plan.PipeName, ["set_property", "window-scale", 0.3]); } catch (Exception ex) { failures.Add(name + ": resize failed: " + ex.Message); }
    var small = await WaitFor(service, t => t.Delivery.Any(v => v.Feature == DeliveryFeature.NvidiaVpp && v.State == DeliveryState.NotNeeded), TimeSpan.FromSeconds(8));
    int code = await run;
    var final = service.CurrentTruth();
    Expect(name, full, DeliveryFeature.HardwareDecoding, DeliveryState.Verified);
    Expect(name, full, DeliveryFeature.NvidiaVpp, DeliveryState.Verified);
    Expect(name, full, DeliveryFeature.RtxSuperResolution, DeliveryState.Unverified);
    Expect(name, full, DeliveryFeature.Cleanup, DeliveryState.Verified);
    Expect(name, full, DeliveryFeature.BlendSmooth, DeliveryState.Verified, DeliveryState.FellBack, DeliveryState.Unverified);
    Expect(name, small, DeliveryFeature.NvidiaVpp, DeliveryState.NotNeeded);
    Expect(name, small, DeliveryFeature.RtxSuperResolution, DeliveryState.NotNeeded);
    foreach (var t in new[] { full, small, final }) NeverVerifiedRtx(name, t);
    results.Add(new { scenario = name, plan.Renderer, exitCode = code, attempts = service.LastReport?.Attempts.Count,
        fullSize = Snapshot(full), shrunk = Snapshot(small), final = Snapshot(final), reportDelivery = service.LastReport?.Delivery });
}

// 2. NVIDIA Vulkan: NVDEC, conventional ewa_lanczossharp scaling, cadence correction, cleanup.
{
    const string name = "vulkan-conventional-cadence-deband";
    var service = new PlaybackService();
    var plan = await Prepare(service, EnhancementIntent.ForEnhanced(DetailIntent.Sharper, MotionIntent.CadenceCorrected, CleanupIntent.Balanced, PerformanceIntent.Balanced));
    var run = service.LaunchAsync(plan, 10);
    var live = await WaitFor(service, Settled, TimeSpan.FromSeconds(8));
    await Task.Delay(1500);
    live = service.CurrentTruth();
    int code = await run;
    Expect(name, live, DeliveryFeature.HardwareDecoding, DeliveryState.Verified);
    Expect(name, live, DeliveryFeature.ConventionalScaling, DeliveryState.Verified);
    Expect(name, live, DeliveryFeature.CadenceCorrection, DeliveryState.Verified);
    Expect(name, live, DeliveryFeature.Cleanup, DeliveryState.Verified);
    if (live?.Delivery.Any(v => v.Feature is DeliveryFeature.NvidiaVpp or DeliveryFeature.RtxSuperResolution) == true)
        failures.Add(name + ": NVIDIA processing reported on the Vulkan path");
    results.Add(new { scenario = name, plan.Renderer, exitCode = code, live = Snapshot(live), reportDelivery = service.LastReport?.Delivery });
}

// 3. NVIDIA Vulkan with temporal blend smoothing.
{
    const string name = "vulkan-blend-smooth";
    var service = new PlaybackService();
    var plan = await Prepare(service, EnhancementIntent.ForEnhanced(DetailIntent.Sharper, MotionIntent.BlendSmooth, CleanupIntent.PreserveTexture, PerformanceIntent.Balanced));
    var run = service.LaunchAsync(plan, 10);
    var live = await WaitFor(service, Settled, TimeSpan.FromSeconds(8));
    await Task.Delay(1500);
    live = service.CurrentTruth();
    int code = await run;
    Expect(name, live, DeliveryFeature.BlendSmooth, DeliveryState.Verified);
    Expect(name, live, DeliveryFeature.ConventionalScaling, DeliveryState.Verified);
    results.Add(new { scenario = name, plan.Renderer, exitCode = code, live = Snapshot(live), reportDelivery = service.LastReport?.Delivery });
}

// 4. Fallback and attempt isolation: the RTX player crashes during startup after
//    it has established its own delivery; the product retries once on the
//    compatibility path. The replacement must show only its own evidence. Health
//    sampling is slowed so the first player has not yet confirmed progress when it
//    is killed, which is the condition the compatibility retry is for.
{
    const string name = "rtx-crash-then-compatibility";
    var service = new PlaybackService { HealthPolicy = new PlaybackHealthPolicy { SampleInterval = TimeSpan.FromSeconds(8), WindowLength = TimeSpan.FromSeconds(40) } };
    var plan = await Prepare(service, EnhancementIntent.ForEnhanced(DetailIntent.Maximum, MotionIntent.BlendSmooth, CleanupIntent.Clean, PerformanceIntent.MaximumQuality));
    var run = service.LaunchAsync(plan, 10);
    var before = await WaitFor(service, t => t.Delivery.Any(v => v.Feature == DeliveryFeature.NvidiaVpp && v.State == DeliveryState.Verified), TimeSpan.FromSeconds(8));
    bool progressedAtCrash = service.LastPlaybackHealth?.PlaybackProgressed == true;
    int? pid = null;
    try
    {
        var value = await Property(plan.PipeName, "pid");
        pid = value?.GetInt32();
        if (pid is int id) Process.GetProcessById(id).Kill();
    }
    catch (Exception ex) { failures.Add(name + ": could not crash the first player: " + ex.Message); }
    // The replacement's own evidence, once it has had time to establish it.
    await Task.Delay(1000);
    var after = await WaitFor(service, t => t.History.Count == 1 && Settled(t), TimeSpan.FromSeconds(10));
    await Task.Delay(1500);
    after = service.CurrentTruth();
    int code = await run;
    var report = service.LastReport!;
    if (before?.Delivery.Any(v => v.Feature == DeliveryFeature.NvidiaVpp && v.State == DeliveryState.Verified) != true)
        failures.Add(name + ": the first attempt never established VPP delivery, so isolation would be vacuous");
    if (progressedAtCrash) failures.Add(name + ": the first attempt had confirmed progress before the crash; the retry path was not exercised");
    if (report.Attempts.Count != 2) failures.Add($"{name}: expected 2 attempts, got {report.Attempts.Count}");
    if (report.Attempts.ElementAtOrDefault(1)?.Renderer != "Compatibility D3D11")
        failures.Add($"{name}: the retry announced as compatibility launched {report.Attempts.ElementAtOrDefault(1)?.Renderer ?? "nothing"}");
    Expect(name, after, DeliveryFeature.HardwareDecoding, DeliveryState.Verified);
    if (after is null || after.History.Count != 1 || !after.History[0].Lines.Any(l => l.StartsWith("Verified:") && l.Contains("NVIDIA VPP scaling")))
        failures.Add(name + ": the crashed attempt's own verification is not in its history");
    if (after?.Observed.Lines.Any(l => l.Contains("NVIDIA VPP") || l.Contains("Super Resolution")) == true ||
        after?.Delivery.Any(v => v.Feature is DeliveryFeature.NvidiaVpp or DeliveryFeature.RtxSuperResolution) == true)
        failures.Add(name + ": the replacement shows NVIDIA evidence it did not plan or produce");
    if (report.Delivery.Count != 2 || report.Delivery[0].Attempt == report.Delivery[1].Attempt)
        failures.Add(name + ": each attempt must have its own delivery record");
    NeverVerifiedRtx(name, before); NeverVerifiedRtx(name, after);
    results.Add(new { scenario = name, firstRenderer = plan.Renderer, retryRenderer = report.Attempts.ElementAtOrDefault(1)?.Renderer,
        retryArguments = report.Attempts.ElementAtOrDefault(1)?.Arguments.TakeWhile(a => a != "--").Where(a => !a.StartsWith("--input-ipc-server") && !a.StartsWith("--config-dir") && !a.StartsWith("--script=")),
        exitCode = code, killedPid = pid, progressedAtCrash, fallbackHistory = report.FallbackHistory,
        firstAttemptLive = Snapshot(before), replacementLive = Snapshot(after), reportDelivery = report.Delivery });
}

File.WriteAllText(Path.Combine(output, "delivery-hardware.json"), JsonSerializer.Serialize(new { mpv, media = Path.GetFileName(media), adapter, results, failures }, json));
Console.WriteLine(JsonSerializer.Serialize(new { results, failures }, json));
Console.WriteLine(failures.Count == 0 ? "PASS: real-hardware enhancement delivery" : $"FAIL: {failures.Count} expectation(s)");
return failures.Count == 0 ? 0 : 1;
