using AdaptiveMedia;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

if (args.Length != 7)
{
    Console.Error.WriteLine("usage: <mpv> <media> <output.json> <width> <height> <RtxVsr|Blend|Original|SmartFill> <adapter>");
    return 2;
}
string mpv = Path.GetFullPath(args[0]), media = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
int width = int.Parse(args[3], CultureInfo.InvariantCulture), height = int.Parse(args[4], CultureInfo.InvariantCulture);
string mode = args[5], adapter = args[6];
if (mode is not ("RtxVsr" or "Blend" or "Original" or "SmartFill")) throw new ArgumentException("Unknown mode");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", Path.Combine(Path.GetDirectoryName(output)!, "data-" + mode));
var system = new SystemSummary { MpvPath = mpv, HasNvidia = true, NvidiaAdapter = adapter };
var settings = new AppSettings { NativeDolbyVisionLane = false, PreferExternalDisplay = false };
var target = new PlaybackTarget(width, height, 0, false, 240);
var intent = EnhancementIntent.ForEnhanced(mode == "RtxVsr" ? DetailIntent.Maximum : DetailIntent.Preserve,
    mode == "Blend" ? MotionIntent.BlendSmooth : MotionIntent.Original,
    CleanupIntent.PreserveTexture, PerformanceIntent.MaximumQuality);
var options = new PlaybackOptions("Enhanced", "Off", "Off", false, false, Intent: intent,
    FitMode: mode == "SmartFill" ? "SmartFill" : "Original");
var service = new PlaybackService();
var plan = await service.PrepareAsync([media], options, system, settings, target);
// Benchmark a known window shape; mpv autofit otherwise follows the source aspect.
if (!plan.Arguments.Any(argument => argument.StartsWith("--geometry=", StringComparison.Ordinal)))
{
    int mediaIndex = plan.Arguments.IndexOf("--");
    plan = plan with { Arguments = plan.Arguments.Insert(mediaIndex, "--keepaspect-window=no")
        .Insert(mediaIndex, $"--geometry={width}x{height}") };
}

static (double MemoryMiB, double UtilPercent)? Gpu()
{
    try
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("nvidia-smi")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = "--query-gpu=memory.used,utilization.gpu --format=csv,noheader,nounits",
        } };
        process.Start();
        string line = process.StandardOutput.ReadLine() ?? "";
        if (!process.WaitForExit(2000) || process.ExitCode != 0) return null;
        var fields = line.Split(',');
        return fields.Length >= 2 && double.TryParse(fields[0].Trim(), CultureInfo.InvariantCulture, out double memory) &&
            double.TryParse(fields[1].Trim(), CultureInfo.InvariantCulture, out double util) ? (memory, util) : null;
    }
    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return null; }
}
static double? Number(JsonElement? element) => element is { ValueKind: JsonValueKind.Number } value &&
    value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
static JsonElement? Property(IReadOnlyDictionary<string, JsonElement>? observed, string name) =>
    observed is not null && observed.TryGetValue(name, out var value) ? value : null;

var gpuBefore = Gpu();
var clock = Stopwatch.StartNew();
var run = service.LaunchAsync(plan, stopAfterSeconds: 8);
var samples = new List<object>();
var positions = new List<(double Wall, double Position)>();
JsonElement? finalPasses = null;
double maxMemory = gpuBefore?.MemoryMiB ?? 0, totalUtil = 0;
int gpuSamples = 0;
while (!run.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(13))
{
    var gpu = Gpu();
    if (gpu is { } g) { maxMemory = Math.Max(maxMemory, g.MemoryMiB); totalUtil += g.UtilPercent; gpuSamples++; }
    // The production monitor owns mpv IPC and publishes a complete property snapshot.
    var observed = service.LastReport?.Observed;
    var position = Number(Property(observed, "time-pos"));
    var drops = Number(Property(observed, "frame-drop-count"));
    var delayed = Number(Property(observed, "vo-delayed-frame-count"));
    var decoderDrops = Number(Property(observed, "decoder-frame-drop-count"));
    if (Property(observed, "vo-passes") is { } passes) finalPasses = passes;
    if (position is double at && (positions.Count == 0 || Math.Abs(at - positions[^1].Position) > 0.01))
        positions.Add((clock.Elapsed.TotalSeconds, at));
    samples.Add(new { wall = clock.Elapsed.TotalSeconds, position, drops, delayed, decoderDrops,
        gpuMemoryMiB = gpu?.MemoryMiB, gpuUtilizationPercent = gpu?.UtilPercent });
    await Task.Delay(450);
}
int exitCode = await run;
var truth = service.CurrentTruth();
var finalObserved = service.LastReport?.Observed;
var window = Property(finalObserved, "osd-dimensions");
bool windowMatches = window is { ValueKind: JsonValueKind.Object } size &&
    size.TryGetProperty("w", out var windowWidth) && windowWidth.TryGetInt32(out int actualWidth) &&
    size.TryGetProperty("h", out var windowHeight) && windowHeight.TryGetInt32(out int actualHeight) &&
    Math.Abs(actualWidth - width) <= 8 && Math.Abs(actualHeight - height) <= 8 &&
    Math.Abs((double)actualWidth / actualHeight - (double)width / height) < 0.005;
double? throughput = positions.Count >= 2 && positions[^1].Wall > positions[0].Wall
    ? (positions[^1].Position - positions[0].Position) / (positions[^1].Wall - positions[0].Wall) * plan.Source.Fps : null;
var report = new
{
    media = Path.GetFileName(media), mode, target = $"{width}x{height}", source = $"{plan.Source.Width}x{plan.Source.Height}",
    sourceFps = plan.Source.Fps, observedWindow = window, windowMatches,
    fitState = Property(finalObserved, "user-data/adaptive/fit"), plan.Renderer, plan.FitPlanned, plan.RtxSrConstructed, exitCode,
    durationSeconds = clock.Elapsed.TotalSeconds, startupObservationSeconds = positions.Count > 0 ? (double?)positions[0].Wall : null, playbackAdvanceSourceFps = throughput,
    gpuBaselineMemoryMiB = gpuBefore?.MemoryMiB, peakGpuMemoryMiB = gpuSamples > 0 ? (double?)maxMemory : null,
    peakMemoryDeltaMiB = gpuBefore is { } before ? (double?)(maxMemory - before.MemoryMiB) : null,
    meanTotalGpuUtilizationPercent = gpuSamples > 0 ? (double?)(totalUtil / gpuSamples) : null,
    samples, voPasses = finalPasses, requested = truth?.Requested.Lines, planned = truth?.Planned.Lines,
    observed = truth?.Observed.Lines, fallback = truth?.Fallback.Lines,
    delivery = truth?.Delivery.Select(d => new { feature = d.Feature.ToString(), state = d.State.ToString(), d.Evidence }),
};
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{mode}: {report.source} -> {report.target}, playback advance ~{throughput:0.0} source fps, " +
    $"GPU memory delta {report.peakMemoryDeltaMiB:0} MiB, exit {exitCode}; {output}");
bool fitObserved = mode != "SmartFill" || truth?.Observed.Lines.Any(line => line.StartsWith("Smart Fill active:", StringComparison.Ordinal)) == true;
if (positions.Count < 2) Console.Error.WriteLine("Benchmark did not collect two playback-position snapshots.");
if (!windowMatches) Console.Error.WriteLine("Actual mpv window did not match the requested benchmark geometry.");
if (!fitObserved) Console.Error.WriteLine("Smart Fill was not observed active in the requested benchmark window.");
return exitCode == 0 && positions.Count >= 2 && windowMatches && fitObserved ? 0 : 1;
