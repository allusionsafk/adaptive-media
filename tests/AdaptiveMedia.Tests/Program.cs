using AdaptiveMedia;
using System.Globalization;

try
{
// Construction must not touch an unconnected pipe (real GUI shutdown regression).
await using (var pendingIpc = new MpvIpc("not-yet-connected")) { }
string ipcName = "adaptive-test-" + Guid.NewGuid().ToString("N");
await using (var server = new System.IO.Pipes.NamedPipeServerStream(ipcName, System.IO.Pipes.PipeDirection.InOut, 1,
    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Task reply = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var request = System.Text.Json.JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        await writer.WriteLineAsync("{\"event\":\"video-reconfig\"}");
        await writer.WriteLineAsync("{\"request_id\":" + request.RootElement.GetProperty("request_id").GetInt32() + ",\"error\":\"success\",\"data\":\"d3d11va\"}");
    });
    await using var client = new MpvIpc(ipcName);
    await client.ConnectAsync(timeout.Token);
    var result = await client.CommandAsync(["get_property", "hwdec-current"], timeout.Token);
    if (result?.GetString() != "d3d11va") throw new Exception("IPC must skip events and match the command response");
    await reply;
}
int assertions = 0;
void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
var media = new MediaInfo(1920, 1080, 24, "h264", "bt.1886", "bt.709");
var target = new PlaybackTarget(2560, 1600, Fullscreen: true);
var options = new PlaybackOptions("Enhanced", "RtxVsr", "Smooth", true, false);
PlaybackPlan Plan(MediaInfo? source = null, PlaybackTarget? output = null, PlaybackOptions? request = null, PlaybackCapabilities? caps = null, string[]? items = null) =>
    PlaybackPlanBuilder.Build("mpv.exe", "config", items ?? ["movie.mp4"], request ?? options, source ?? media, output ?? target, caps ?? new(true, true, Rtx: true), "test");
var plan = Plan();
Check(plan.RtxSrConstructed, "RTX processing must be constructed");
Check(plan.Arguments.Contains("--gpu-api=d3d11") && plan.Arguments.Contains("--gpu-context=d3d11") && plan.Arguments.Contains("--hwdec=d3d11va"), "RTX renderer pairing");
Check(plan.Arguments.Any(x => x.Contains("scale=1.333333:scaling-mode=nvidia")), "1080p aspect-fit final VPP argv");
Check(!plan.Arguments.Contains("--profile=nvidia") && !plan.Arguments.Contains("--vulkan-swap-mode=fifo"), "No contradictory Vulkan override");
Check(!plan.Arguments.Any(x => x.StartsWith("--scale=")), "No conventional upscale stacked after RTX");
foreach (int h in new[] { 480, 720, 900, 1080, 1200, 1440, 1600, 2160 })
foreach (var size in new[] { (1920,1080), (2560,1600), (3840,2160), (3440,1440), (1280,720) })
{
    var src = new MediaInfo(h * 16 / 9, h);
    var p = Plan(src, new(size.Item1, size.Item2));
    double expected = Math.Min((double)size.Item1 / src.Width, (double)size.Item2 / src.Height);
    Check(Math.Abs(p.Scale - expected) < 0.00001, "Aspect fit resolution matrix");
    Check(p.RtxSrConstructed == (expected > 1.001 && expected <= 8), "No upscale when unnecessary");
}
foreach (string profile in new[] { "Automatic", "Reference", "Enhanced", "Compatibility" })
foreach (bool nvidia in new[] { false, true })
foreach (string motion in new[] { "Off", "Gentle", "Smooth" })
{
    var p = Plan(request: new(profile, "Off", motion, false, false), caps: new(nvidia, true));
    Check(p.Arguments.Contains("--profile=nvidia") == (nvidia && profile != "Compatibility"), "Reference lane matrix");
    Check(!p.RtxSrConstructed, "No implicit RTX enhancement");
    Check(p.Arguments.Contains("--vulkan-swap-mode=fifo") == (motion != "Off" && profile != "Compatibility"), "Motion renderer matrix");
    Check(!p.Arguments.Contains("--video-sync-max-factor=12"), "Valid motion max factor");
}
Check(!Plan(caps: new(false, true)).RtxSrConstructed, "Non-NVIDIA fallback");
Check(Plan(caps: new(true, true, "NVIDIA GTX 1080")).Arguments.Contains("--profile=nvidia"), "GTX retains NVIDIA conventional acceleration");
Check(!Plan(caps: new(true, true, "NVIDIA GTX 1080")).RtxSrConstructed, "GTX never requests RTX");
Check(Plan(items: ["sdr.mp4", "hdr.mp4"], request: options with { AutoHdrSwitch = true }).Reasons.Any(x => x.Contains("switching is unavailable")), "Unavailable HDR switch is explained");
Check(!Plan(caps: new(true, false)).RtxSrConstructed, "Missing VPP fallback");
Check(!Plan(new()).RtxSrConstructed, "Unknown probe fallback");
Check(Plan(output: new(1280,720)).Arguments.Contains("--gpu-context=d3d11"), "RTX-ready lane supports later fullscreen upscale");
Check(!Plan(output: new(1280,720)).Arguments.Any(x=>x.StartsWith("--vf=")), "RTX-ready lane performs no unnecessary filtering");
Check(!Plan(new(720, 480, Aspect: 16.0/9)).RtxSrConstructed, "Anamorphic safe fallback");
Check(!Plan(request: options with { RtxHdr = true }).RtxHdrConstructed, "Do not infer HDR display");
Check(Plan(media with { Transfer = "pq" }).Arguments.Contains("--target-trc=bt.1886"), "HDR10 on an SDR path must tone-map to SDR");
Check(Plan(media with { Transfer = "hlg" }).Arguments.Contains("--target-trc=bt.1886"), "HLG on an SDR path must tone-map to SDR");
Check(Plan().Arguments.Contains("--target-colorspace-hint=auto"), "Renderer must follow the selected display color state");
Check(!Plan().Arguments.Contains("--inverse-tone-mapping=yes"), "Automatic must never synthesize HDR from SDR");
Check(!Plan(output: target with { Display = new(HdrSupported: true, HdrActive: false, ActiveColorMode: "SDR", DxgiColorSpace: 0) },
    request: options with { RtxHdr = true }).RtxHdrConstructed,
    "HDR capability without active HDR cannot authorize RTX HDR");
var verifiedHdr = new DisplayCapability(HdrSupported: true, HdrActive: true, ActiveColorMode: "HDR", DxgiColorSpace: 12);
var g16Flags = AdvancedColorFlags.Parse(0x45, 0);
Check(g16Flags.AdvancedColorSupported && g16Flags.WcgSupported && !g16Flags.HdrSupported &&
    !g16Flags.HdrActive && g16Flags.Mode == "SDR", "Measured G16 Info2 flags decode WCG without HDR");
Check(!new DisplayCapability(HdrSupported: false, HdrActive: false, WcgSupported: true, ActiveColorMode: "SDR", DxgiColorSpace: 0,
    ReportedMaxLuminance: 500).WindowsHdrPathActive, "WCG or luminance descriptor never establishes HDR support");
Check(!(verifiedHdr with { DxgiColorSpace = 0 }).WindowsHdrPathActive, "SDR DXGI descriptor contradicts an HDR mode claim");
Check(!(verifiedHdr with { HdrActive = false }).WindowsHdrPathActive, "HDR support is not active HDR");
Check(!(verifiedHdr with { HdrSupported = null }).WindowsHdrPathActive, "Unknown support is not an HDR grant");
Check(!(verifiedHdr with { ActiveColorMode = "WCG" }).WindowsHdrPathActive, "Wide gamut active is not HDR active");
var qualifiedWcg = new DisplayCapability(PnpId: @"DISPLAY\BOE0C4B\5&39391d08&1&UID4354",
    EdidFingerprint: "1B5EAD2BDE875922DC92150000696F1AC51203F92B57865EBC7C1928F642B39C",
    HdrSupported: false, HdrActive: false, WcgSupported: true, WcgActive: true, AdvancedColorActive: true,
    ActiveColorMode: "WCG", BitsPerColor: 10, DxgiColorSpace: 0,
    ReportedMaxLuminance: 270, DxgiAdapter: "NVIDIA GeForce RTX 4080 Laptop GPU",
    RedPrimary: [0.68066406f, 0.31445312f], GreenPrimary: [0.27148438f, 0.6894531f],
    BluePrimary: [0.15039062f, 0.044921875f], WhitePoint: [0.31347656f, 0.32910156f]);
var brightWcg = Plan(media with { Transfer = "pq", Primaries = "bt.2020" },
    target with { Display = qualifiedWcg });
Check(brightWcg.Arguments.Contains("--hdr-reference-white=270") &&
    brightWcg.Arguments.Contains("--tone-mapping=mobius") &&
    brightWcg.Arguments.Contains("--d3d11-output-format=rgba16f") &&
    brightWcg.Arguments.Contains("--d3d11-output-csp=linear"),
    "WCG plans the tested FP16 scRGB path and does not let mpv's SDR reference white override target peak");
Check(brightWcg.Arguments.Contains("--target-peak=270"),
    "An active 10-bit WCG panel with a matched EDID and OS-reported peak uses its reported luminance");
Check(brightWcg.Arguments.Contains("--target-trc=bt.1886") &&
    !brightWcg.Arguments.Any(x => x.Contains("g2084") || x.Contains("pq")),
    "High-luminance WCG remains an SDR transfer, never invented HDR signalling");
Check((qualifiedWcg with { WcgActive = false }).QualifiedWcgPeakNits is null,
    "A supported but inactive WCG mode cannot authorize a brighter renderer");
Check((qualifiedWcg with { BitsPerColor = 8 }).QualifiedWcgPeakNits is null,
    "An 8-bit path cannot authorize high-luminance WCG");
Check((qualifiedWcg with { EdidFingerprint = null }).QualifiedWcgPeakNits is null,
    "Unknown display identity cannot authorize high-luminance WCG");
Check((qualifiedWcg with { RedPrimary = [0.64f, 0.33f] }).QualifiedWcgPeakNits is null,
    "BT.709 primaries cannot authorize wide-gamut output");
Check((qualifiedWcg with { ReportedMaxLuminance = 500, ReportedMaxFullFrameLuminance = 200 }).QualifiedWcgPeakNits is null,
    "A low sustained luminance descriptor cannot authorize an elevated peak");
Check((qualifiedWcg with { ReportedMaxLuminance = float.NaN }).QualifiedWcgPeakNits is null,
    "Invalid OS luminance cannot authorize an elevated peak");
Check(Plan(media, target with { Display = qualifiedWcg }).Color?.TargetPeakNits is null,
    "Known SDR source remains SDR even on an active WCG path");var displayA = verifiedHdr with { SourceName = @"\\.\DISPLAY1", AdapterLow = 1, TargetId = 11 };
var displayB = verifiedHdr with { SourceName = @"\\.\DISPLAY2", AdapterLow = 2, TargetId = 22 };
Check(DisplayCapabilitySelection.ForScreen([displayA, displayB], @"\\.\DISPLAY2") == displayB,
    "Selected screen maps to its own display path");
Check(DisplayCapabilitySelection.ForScreen([displayA, displayB], @"\\.\DISPLAY3") is null,
    "An unmatched screen cannot inherit another target's HDR state");
Check(DisplayCapabilitySelection.ForScreen([displayA, displayA], @"\\.\DISPLAY1") is null,
    "Ambiguous duplicate paths cannot authorize HDR");
Check(DisplayCapabilitySelection.ForAttempt([displayA with { TargetId = 99 }], displayA) is null,
    "A changed target ID cannot reuse the previous display's HDR observation");
Check(DisplayCapabilitySelection.ForAttempt([displayA with { PnpId = "DISPLAY\\OTHER" }], displayA) is null,
    "A replacement monitor on the same connector cannot reuse the previous HDR observation");
Check(Plan(media with { Transfer = "pq" }, target with { Display = verifiedHdr }).Color?.RendererTarget == ColorDelivery.Pq,
    "PQ transfer is retained on verified HDR path without inferring DV signalling");
Check(Plan(media with { Transfer = "pq" }, target with { Display = verifiedHdr }).Arguments.All(x => !x.StartsWith("--target-trc=")),
    "No forced SDR target on verified HDR path");
Check(!Plan(media with { Transfer = "unknown" }, target with { Display = verifiedHdr }, options with { RtxHdr = true }).RtxHdrConstructed, "Unknown transfer is not known SDR");
Check(Plan(output: target with { Display = verifiedHdr }, request: options with { RtxHdr = true }).RtxHdrConstructed, "Verified SDR RTX HDR");
Check(Plan(output: target with { Display = verifiedHdr }, request: options with { RtxHdr = true }).Color?.RendererTarget == ColorDelivery.Hdr10,
    "RTX Video HDR is only a planned SDR-to-HDR10 renderer request");
foreach (string transfer in new[] { "pq", "hlg" })
    Check(!Plan(media with { Transfer = transfer }, target with { Display = verifiedHdr }, options with { RtxHdr = true }).RtxHdrConstructed, "Never apply SDR conversion to HDR source");
foreach (var strength in new[] { ("Off", ""), ("Gentle", "16"), ("Normal", "32"), ("Strong", "48"), ("Automatic", "16") })
{
    var cleaned = Plan(request: options with { CleanupMode = strength.Item1 });
    Check(cleaned.Arguments.Contains("--deband=yes") == (strength.Item1 != "Off"), "Cleanup strength activation");
    if (strength.Item2.Length > 0) Check(cleaned.Arguments.Contains("--deband-threshold=" + strength.Item2), "Cleanup strength threshold");
}
Check(!Plan(media with { PixelFormat = "yuv420p10le" }, request: options with { CleanupMode = "Automatic" }).Arguments.Contains("--deband=yes"), "Automatic cleanup preserves 10-bit source texture");
var playlistAuto = Plan(request: options with { CleanupMode = "Automatic" }, items: ["a.mp4", "b.mp4"]);
Check(!playlistAuto.Arguments.Contains("--deband=yes"), "Automatic cleanup never applies the first item's policy to a playlist");
Check(playlistAuto.Reasons.Any(x => x.Contains("playlist")), "Playlist cleanup restraint is explained");
Check(Plan(request: options with { CleanupMode = "Automatic" }).Arguments.Contains("--deband-threshold=16"), "Automatic cleanup still judges a single known SDR item");
// A quality request must survive the RTX lane whenever RTX SR is not constructed.
var hdrOnly = Plan(output: target with { Display = verifiedHdr }, request: options with { UpscaleMode = "HighQuality", RtxHdr = true });
Check(hdrOnly.RtxHdrConstructed && !hdrOnly.RtxSrConstructed, "RTX HDR without RTX SR");
Check(hdrOnly.Arguments.Contains("--gpu-api=d3d11") && hdrOnly.Arguments.Any(x => x.Contains("nvidia-true-hdr=yes")), "RTX HDR lane pairing");
Check(hdrOnly.Arguments.Contains("--scale=ewa_lanczossharp") && hdrOnly.Arguments.Contains("--sigmoid-upscaling=yes"), "High quality scaling is honoured on the RTX lane");
var rtxDownscale = Plan(output: new(1280, 720));
Check(!rtxDownscale.RtxSrConstructed && rtxDownscale.Arguments.Contains("--scale=ewa_lanczossharp"), "Quality scaling remains when RTX SR is unnecessary");
Check(!Plan(request: options with { UpscaleMode = "Off" }).Arguments.Any(x => x.StartsWith("--scale=")), "Off never adds conventional scaling");
PlaybackPlan Streamed(string item, bool helper) => PlaybackPlanBuilder.Build("mpv.exe", "config", [item], options, media, target,
    new(true, true, Rtx: true), "test", bitstreamRequested: false, streamHelperAvailable: helper);
Check(Streamed("https://example.invalid/watch?v=1", false).Reasons.Any(x => x.Contains("yt-dlp")), "A missing stream helper is explained for site URLs");
Check(!Streamed("https://example.invalid/watch?v=1", true).Reasons.Any(x => x.Contains("yt-dlp")), "An available stream helper is not mentioned");
Check(!Streamed(@"C:\media\movie.mp4", false).Reasons.Any(x => x.Contains("yt-dlp")), "A drive-letter path is never mistaken for a site URL");
var hostile = new[] { "C:\\media\\space and 日本語's (cut) [1].mp4", "--vf=malicious", "https://example.invalid/a?x=1&y=2" };
var escaped = Plan(items: hostile);
Check(escaped.Arguments.TakeLast(4).SequenceEqual(new[] { "--" }.Concat(hostile)), "Paths follow option terminator unchanged");
var psi = NativeProcess.StartInfo(escaped.Executable, escaped.Arguments);
Check(psi.ArgumentList.SequenceEqual(escaped.Arguments), "Exact plan is the launch vector");
Check(!psi.UseShellExecute && psi.CreateNoWindow && psi.RedirectStandardError && psi.RedirectStandardOutput, "No-console subprocess contract");
CultureInfo.CurrentCulture = new("fr-FR");
Check(Plan().Arguments.Any(x => x.Contains("scale=1.333333:")), "Invariant decimal serialization");
if (args.Length > 0 && args[0] == "--probe-files")
{
    foreach (string path in args.Skip(2))
    {
        var probed = await MediaProbe.ReadAsync(args[1], path);
        Check(probed.Known, "Real codec/path probe: " + Path.GetFileName(path));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { file = Path.GetFileName(path), probe = probed }));
    }
}
else if (args.Length > 0)
{
    var probe = await MediaProbe.ReadAsync(args[0], args[1]);
    Check(probe.Width == 1920 && probe.Height == 1080, "REAL probe must retain dimensions (0.3.2 regression)");
    Check(Plan(probe).RtxSrConstructed, "Real probe feeds canonical RTX plan");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(probe));
}
int healthAssertions = PlaybackHealthTests.Run(Check);
int recoveryAssertions = PlaybackRecoveryTests.Run(Check);
int truthAssertions = PlaybackTruthTests.Run(Check);
int enhancementAssertions = EnhancementPlannerTests.Run(Check);
int deliveryAssertions = EnhancementDeliveryTests.Run(Check);
int samplerAssertions = await PlaybackHealthMonitorTests.RunAsync(Check);
int detailsAssertions = PlayerDetailsPayloadTests.Run(Check);
int presentationAssertions = PlaybackPresentationTests.Run(Check);
int userResumeAssertions = WatchLaterResumeTests.Run(Check);
Console.WriteLine($"PASS: {assertions} assertions ({enhancementAssertions} enhancement planner, {deliveryAssertions} enhancement delivery, {healthAssertions} sustained playback health, {recoveryAssertions} automatic recovery, {truthAssertions} playback truth, {samplerAssertions} health sampler, {detailsAssertions} player details, {presentationAssertions} launcher presentation)");
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = "failed", exception = ex.ToString() }));
    return 1;
}

