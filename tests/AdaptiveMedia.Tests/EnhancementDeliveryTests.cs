using AdaptiveMedia;
using System.Text.Json;

/// <summary>Enhancement delivery verification: planned paths are judged only from
/// the player's own runtime reports, never from the plan or from option values
/// that merely echo it; NVIDIA driver-side processing is never verified; and
/// evidence stays with the attempt and the source that produced it.</summary>
internal static class EnhancementDeliveryTests
{
    // Compact polls captured from mpv v0.41.0-1011 (libplacebo v7.371) on an RTX
    // 4080 Laptop GPU, driver 32.0.16.1692, playing a 960×540 25 fps H.264 source
    // into a 2048×1152 window on a 240 Hz panel. Only timing samples were dropped.
    private const string RealRtxVpp = """
        {"hwdec-current":"d3d11va","vf":[{"name":"d3d11vpp","label":"adaptive-vpp","enabled":true,"params":{"scale":"2.133333","scaling-mode":"nvidia"}}],"display-sync-active":false,"video-sync":"audio","interpolation":false,"video-speed-correction":1.0,"user-data/adaptive/state":"RTX VPP configured for the observed video rectangle (2048 × 1152). Driver activation is unverified.","video-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"d3d11"},"video-out-params":{"w":2048,"h":1152,"dw":2048,"dh":1152,"pixelformat":"d3d11"},"osd-dimensions":{"w":2048,"h":1152,"mt":0,"mb":0,"ml":0,"mr":0},"vo-passes":{"fresh":[{"desc":"map frame (hwdec)","count":165},{"desc":"ortho (vert) upscaling (spline36)","count":167},{"desc":"ortho (horiz) upscaling (spline36), color decoding, color encoding, dithering (10 bits)","count":166}],"redraw":[{"desc":"frame mixing (1 frame), color encoding, dithering (10 bits)","count":0}]},"user-data/adaptive/rtx-sr":"accepted"}
        """;
    private const string RealVulkanEnhanced = """
        {"hwdec-current":"nvdec","display-sync-active":true,"video-sync":"display-resample","interpolation":true,"video-speed-correction":0.993103,"video-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"cuda"},"video-out-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"cuda"},"osd-dimensions":{"w":2048,"h":1152,"mt":0,"mb":0,"ml":0,"mr":0},"vo-passes":{"fresh":[{"desc":"debanding","count":172},{"desc":"ortho (vert) upscaling (spline36)","count":172},{"desc":"debanding, ortho (horiz) upscaling (spline36), color decoding","count":172},{"desc":"polar upscaling (ewa_lanczossharp)","count":171}],"redraw":[{"desc":"frame mixing (2 frames), color encoding, dithering (10 bits)","count":256}]}}
        """;
    private const string RealCadence = """
        {"hwdec-current":"nvdec","display-sync-active":true,"video-sync":"display-resample","interpolation":false,"video-speed-correction":0.993103,"video-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"cuda"},"video-out-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"cuda"},"vo-passes":{"fresh":[{"desc":"ortho (vert) upscaling (spline36)","count":97},{"desc":"ortho (horiz) upscaling (spline36), color decoding","count":97}],"redraw":[{"desc":"frame mixing (1 frame), color encoding, dithering (10 bits)","count":256}]}}
        """;
    // D3D11VA was requested but the Vulkan renderer could not use it: software decoding.
    private const string RealSoftwareVpp = """
        {"hwdec-current":"no","vf":[{"name":"d3d11vpp","label":"adaptive-vpp","enabled":true,"params":{"scaling-mode":"nvidia","scale":"2.133333"}}],"display-sync-active":false,"video-sync":"audio","interpolation":false,"video-params":{"w":960,"h":540,"dw":960,"dh":540,"pixelformat":"yuv420p"},"video-out-params":{"w":2048,"h":1152,"dw":2048,"dh":1152,"pixelformat":"d3d11"},"vo-passes":{"fresh":[{"desc":"map frame (hwdec)","count":115},{"desc":"ortho (vert) upscaling (spline36)","count":115}],"redraw":[{"desc":"frame mixing (1 frame), color encoding, dithering (10 bits)","count":1}]}}
        """;

    private static readonly MediaInfo Source540 = new(960, 540, 25, "h264", "bt.1886", "bt.709", "aac", "yuv420p");
    private static readonly PlaybackTarget Window = new(2048, 1152, RefreshRateHz: 240);
    private static readonly PlaybackCapabilities Rtx = new(true, true, "NVIDIA GeForce RTX 4080 Laptop GPU", Rtx: true);

    private static PlaybackPlan Plan(PlaybackOptions options, PlaybackCapabilities caps, string[]? items = null, PlaybackTarget? target = null) =>
        PlaybackPlanBuilder.Build("mpv.exe", "config", items ?? ["movie.mkv"], options, Source540, target ?? Window, caps, "delivery-test");

    private static Dictionary<string, JsonElement> Props(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static Dictionary<string, JsonElement> With(Dictionary<string, JsonElement> props, string name, object value) =>
        new(props) { [name] = JsonSerializer.SerializeToElement(value) };

    /// <summary>A started attempt polled twice while its position advances.</summary>
    private static PlaybackAttemptEvidence Playing(long id, PlaybackPlan plan, string json, int epoch = 1, double from = 5)
    {
        var e = new PlaybackAttemptEvidence(id, (int)id, plan, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        var props = With(Props(json), "user-data/adaptive/source-epoch", epoch);
        e.Observe(With(props, "time-pos", from), null);
        e.Observe(With(props, "time-pos", from + 1), null);
        return e;
    }

    private static DeliveryState State(IReadOnlyList<DeliveryVerdict> verdicts, DeliveryFeature feature) =>
        verdicts.Single(v => v.Feature == feature).State;

    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "DELIVERY: " + m); }

        var rtxPlan = Plan(new("Enhanced", "RtxVsr", "Off", false, false), Rtx);
        var vulkanPlan = Plan(new("Enhanced", "HighQuality", "Smooth", true, false, CleanupMode: "Normal"), new(true, false));
        var cadencePlan = Plan(new("Enhanced", "Off", "Cadence", false, false), new(true, false));

        // Real RTX 4080 evidence: what is proven, what cannot be, and nothing more.
        {
            var d = Playing(1, rtxPlan, RealRtxVpp).Delivery();
            Check(State(d, DeliveryFeature.HardwareDecoding) == DeliveryState.Verified && State(d, DeliveryFeature.NvidiaVpp) == DeliveryState.Verified,
                "real D3D11VA decoding and VPP scaling (960×540 → 2048×1152 D3D11 surfaces) are verified");
            Check(d.Single(v => v.Feature == DeliveryFeature.NvidiaVpp).Evidence == "960×540 → 2048×1152 on D3D11 video surfaces",
                "VPP evidence names the real scaling the player reported");
            var sr = d.Single(v => v.Feature == DeliveryFeature.RtxSuperResolution);
            Check(sr.State == DeliveryState.Unverified && sr.Evidence.Contains("driver accepted") && sr.Evidence.Contains("does not report"),
                "real NVIDIA VPP proof plus driver acceptance is still only unverified RTX Video Super Resolution");
            Check(d.All(v => v.Feature is not (DeliveryFeature.ConventionalScaling or DeliveryFeature.BlendSmooth or DeliveryFeature.Cleanup)),
                "paths the RTX plan never asked for are not reported at all");

            var v = Playing(2, vulkanPlan, RealVulkanEnhanced).Delivery();
            Check(State(v, DeliveryFeature.HardwareDecoding) == DeliveryState.Verified && State(v, DeliveryFeature.ConventionalScaling) == DeliveryState.Verified &&
                  State(v, DeliveryFeature.BlendSmooth) == DeliveryState.Verified && State(v, DeliveryFeature.Cleanup) == DeliveryState.Verified,
                "real NVDEC, ewa_lanczossharp polar upscaling, 2-frame blending with display-resample, and debanding are verified");
            Check(!v.Any(x => x.Feature is DeliveryFeature.NvidiaVpp or DeliveryFeature.RtxSuperResolution), "no NVIDIA processing on the Vulkan path");

            var c = Playing(3, cadencePlan, RealCadence).Delivery();
            Check(State(c, DeliveryFeature.CadenceCorrection) == DeliveryState.Verified && c.Single(x => x.Feature == DeliveryFeature.CadenceCorrection).Evidence.Contains("×0.9931"),
                "real display-resample without interpolation is verified cadence correction, with its speed correction");
            Check(!c.Any(x => x.Feature == DeliveryFeature.BlendSmooth), "cadence correction is not reported as smoothing");

            var sw = Playing(4, rtxPlan, RealSoftwareVpp).Delivery();
            Check(State(sw, DeliveryFeature.HardwareDecoding) == DeliveryState.FellBack && State(sw, DeliveryFeature.NvidiaVpp) == DeliveryState.Verified,
                "a real software-decoding fallback is reported, while VPP scaling that still happened stays verified");
            Check(sw.Single(x => x.Feature == DeliveryFeature.RtxSuperResolution) is { State: DeliveryState.Unverified } nack && nack.Evidence.Contains("no driver acknowledgement"),
                "without the driver's acknowledgement even the acceptance is not claimed");

            // The same real polls against plans that asked for the other paths.
            var wrongDecoder = Playing(5, vulkanPlan, RealRtxVpp).Delivery();
            Check(wrongDecoder.Single(x => x.Feature == DeliveryFeature.HardwareDecoding) is { State: DeliveryState.FellBack } w && w.Evidence.StartsWith("d3d11va decoding observed instead"),
                "a different hardware decoder than planned is a fallback, not a verification");
            Check(State(Playing(6, vulkanPlan, RealCadence).Delivery(), DeliveryFeature.BlendSmooth) == DeliveryState.FellBack,
                "interpolation switched off (real cadence-only poll) is a fallback of planned smoothing");
            Check(State(Playing(7, vulkanPlan, RealCadence).Delivery(), DeliveryFeature.Cleanup) == DeliveryState.FellBack,
                "a renderer that ran no debanding pass fails planned cleanup");
            Check(State(Playing(8, vulkanPlan, RealRtxVpp).Delivery(), DeliveryFeature.BlendSmooth) == DeliveryState.FellBack,
                "audio-clock timing fails planned smoothing");
        }

        // Falsification: an echo of every launch argument as a property value, with the
        // player visibly playing, never produces a single verified path.
        {
            var options = new[]
            {
                new PlaybackOptions("Enhanced", "RtxVsr", "Smooth", true, true, CleanupMode: "Strong"),
                new PlaybackOptions("Enhanced", "HighQuality", "Cadence", true, false, CleanupMode: "Gentle"),
                new PlaybackOptions("Enhanced", "HighQuality", "Gentle", false, false),
                new PlaybackOptions("Compatibility", "HighQuality", "Off", false, false),
                new PlaybackOptions("Reference", "Off", "Off", false, false),
            };
            var caps = new PlaybackCapabilities[] { Rtx, new(true, false), new(false, false) };
            int plans = 0;
            foreach (var o in options)
            foreach (var cap in caps)
            foreach (var target in new[] { Window, Window with { Display = new(HdrSupported: true, HdrActive: true, ActiveColorMode: "HDR", DxgiColorSpace: 12) }, new PlaybackTarget(640, 360) })
            {
                var plan = Plan(o, cap, target: target);
                var echo = new Dictionary<string, JsonElement>();
                foreach (string arg in plan.Arguments.TakeWhile(x => x != "--").Where(x => x.StartsWith("--") && x.Contains('=')))
                {
                    string name = arg[2..arg.IndexOf('=')], value = arg[(arg.IndexOf('=') + 1)..];
                    echo[name] = value is "yes" or "no" ? JsonSerializer.SerializeToElement(value == "yes") : JsonSerializer.SerializeToElement(value);
                }
                var e = new PlaybackAttemptEvidence(plans, 1, plan, PlaybackAttemptKind.Stable, "stable player") { Started = true };
                e.Observe(With(echo, "time-pos", 1.0), null);
                e.Observe(With(echo, "time-pos", 3.0), null);
                var live = e.Delivery();
                Check(live.All(x => x.State != DeliveryState.Verified), $"argv echo never verifies ({o.Profile}/{o.UpscaleMode}/{o.MotionMode}/{cap.Rtx}/{target.Width})");
                e.Finish(null);
                Check(e.Delivery().All(x => x.State is not (DeliveryState.Verified or DeliveryState.Pending)),
                    $"a finished attempt leaves nothing pending and still verifies nothing ({o.Profile}/{o.MotionMode})");
                plans++;
            }
            Check(plans == 45, "the echo matrix covered every plan");
        }

        // RTX Video Super Resolution is never verified, whatever the evidence.
        {
            int combinations = 0;
            foreach (string? ack in new[] { null, "pending", "accepted", "rejected: Failed to enable NVIDIA RTX Super Resolution: E_FAIL" })
            foreach (string output in new[] { "{\"w\":2048,\"h\":1152,\"pixelformat\":\"d3d11\"}", "{\"w\":960,\"h\":540,\"pixelformat\":\"d3d11\"}", "{\"w\":2048,\"h\":1152,\"pixelformat\":\"yuv420p\"}" })
            foreach (string mode in new[] { "nvidia", "default" })
            {
                string json = RealRtxVpp.Replace("\"accepted\"", ack is null ? "null" : JsonSerializer.Serialize(ack))
                    .Replace("\"video-out-params\":{\"w\":2048,\"h\":1152,\"dw\":2048,\"dh\":1152,\"pixelformat\":\"d3d11\"}", "\"video-out-params\":" + output)
                    .Replace("\"scaling-mode\":\"nvidia\"", "\"scaling-mode\":\"" + mode + "\"");
                var sr = Playing(10 + combinations, rtxPlan, json).Delivery().Single(x => x.Feature == DeliveryFeature.RtxSuperResolution);
                Check(sr.State != DeliveryState.Verified, $"RTX VSR never verified (ack={ack ?? "none"}, mode={mode})");
                if (ack?.StartsWith("rejected") == true)
                    Check(sr.State == DeliveryState.FellBack && sr.Evidence.Contains("E_FAIL"), "a driver refusal is a fallback with the driver's reason");
                combinations++;
            }
        }

        // Evidence waits for the source to play, and never outlives it.
        {
            var e = new PlaybackAttemptEvidence(1, 1, vulkanPlan, PlaybackAttemptKind.Stable, "stable player") { Started = true };
            var first = With(Props(RealVulkanEnhanced), "user-data/adaptive/source-epoch", 1);
            e.Observe(With(first, "time-pos", 3.0), null);
            Check(e.Delivery().All(x => x.State == DeliveryState.Pending), "one poll before playback advances verifies nothing");
            e.Observe(With(first, "time-pos", 3.1), null);
            Check(e.Delivery().All(x => x.State == DeliveryState.Pending), "an advance below the flowing threshold verifies nothing");
            e.Observe(With(first, "time-pos", 4.0), null);
            Check(e.Delivery().All(x => x.State == DeliveryState.Verified), "the first source is verified once it plays");

            // The next playlist item: the renderer still describes the old frames.
            var next = With(With(Props(RealVulkanEnhanced), "user-data/adaptive/source-epoch", 2), "playlist-pos", 1);
            e.Observe(With(next, "time-pos", 0.0), null);
            Check(e.Delivery().All(x => x.State == DeliveryState.Pending), "a source change discards every verification made for the previous source");
            // The new source plays, but its redraws never blend: the old blend is not carried over.
            var noBlend = With(With(Props(RealCadence.Replace("\"interpolation\":false", "\"interpolation\":true")), "user-data/adaptive/source-epoch", 2), "playlist-pos", 1);
            e.Observe(With(noBlend, "time-pos", 1.0), null);
            var d = e.Delivery();
            Check(State(d, DeliveryFeature.BlendSmooth) == DeliveryState.Unverified && State(d, DeliveryFeature.Cleanup) == DeliveryState.FellBack,
                "the new source is judged only from its own frames");
            // An audio-only item: the video properties disappear from the poll.
            var audio = new Dictionary<string, JsonElement> { ["user-data/adaptive/source-epoch"] = JsonSerializer.SerializeToElement(3) };
            e.Observe(With(audio, "time-pos", 0.0), null);
            e.Observe(With(audio, "time-pos", 2.0), null);
            Check(e.Delivery().All(x => x.State != DeliveryState.Verified), "a source without video inherits no video verification");
        }

        // Evidence belongs to one attempt: the replacement starts from nothing, a
        // finished attempt accepts nothing more, and history keeps its own record.
        {
            var failed = Playing(1, vulkanPlan, RealVulkanEnhanced);
            failed.Finish(new PlaybackHealthSnapshot { AttemptId = 1, State = SustainedPlaybackHealth.RuntimeFailure, WorstCondition = SustainedPlaybackHealth.RuntimeFailure });
            var compatibility = Plan(new("Compatibility", "HighQuality", "Off", false, false), new(false, false));
            var replacement = new PlaybackAttemptEvidence(2, 2, compatibility, PlaybackAttemptKind.Stable, "stable player") { Started = true };
            Check(replacement.Delivery().All(x => x.State == DeliveryState.Pending), "a replacement attempt starts with no delivery evidence");
            var t = PlaybackTruthBuilder.Build(replacement, [failed], []);
            Check(!t.Observed.Lines.Any(l => l.Contains(": verified")) && t.Observed.Lines.SequenceEqual(["Nothing observed yet"]),
                "the earlier attempt's verifications never reach the replacement's Observed");
            Check(t.History.Single().Lines.Contains("Verified: NVDEC hardware decoding, High-quality conventional scaling (ewa_lanczossharp), Temporal blend smoothing, Banding reduction"),
                "the earlier attempt's verifications stay in its own history");

            failed.Observe(With(Props("""{"hwdec-current":"no","vo-passes":{"fresh":[],"redraw":[]}}"""), "time-pos", 99.0), null);
            Check(failed.Delivery().All(x => x.State == DeliveryState.Verified), "a finished attempt refuses late evidence");

            var notStarted = new PlaybackAttemptEvidence(3, 3, vulkanPlan, PlaybackAttemptKind.NativePrevious, "native generation b");
            notStarted.Finish(null);
            Check(notStarted.Delivery().All(x => x.State == DeliveryState.Unverified && x.Evidence == "the player did not start"),
                "a player that never started delivered nothing and fell back from nothing");
        }

        // What counts as planned comes from the launched argv and its managed profiles.
        {
            var compat = PlannedDelivery.From(Plan(new("Compatibility", "HighQuality", "Off", false, false), new(false, false)));
            Check(compat.Decoder == "d3d11va-copy" && compat.Scaler == "ewa_lanczossharp" && !compat.VppScaling && !compat.DisplayResample,
                "the compatibility profile plans D3D11VA copy-back decoding and conventional scaling");
            Check(PlannedDelivery.From(Plan(new("Reference", "Off", "Off", false, false), new(false, false))) is { Decoder: "auto-safe", Scaler: null, Deband: false },
                "the managed default decodes automatically and plans no enhancement");
            var small = PlannedDelivery.From(Plan(new("Enhanced", "RtxVsr", "Off", false, false), Rtx, target: new(640, 360)));
            Check(small.VppScaling && small.RtxSuperResolution && small.Decoder == "d3d11va",
                "an armed RTX lane plans VPP scaling even before the window needs it");
            var native = new PlaybackPlan("mpv.exe", ["--no-config", "--vo=gpu-next", "--hwdec=d3d11va", "--gpu-api=d3d11", "--vf=format=enhancement-layer=yes", "--", "movie.mkv"],
                new("Reference", "Off", "Off", false, false), Source540, Window, "Native Dolby Vision gpu-next", false, false, 1, [], "native");
            Check(PlannedDelivery.From(native) is { Decoder: "d3d11va", VppScaling: false, Scaler: null, DisplayResample: false, Deband: false },
                "a native Dolby Vision plan is held to its own decoder and nothing discretionary");
            var intent = EnhancementIntent.ForEnhanced(DetailIntent.Maximum, MotionIntent.BlendSmooth, CleanupIntent.Clean, PerformanceIntent.MaximumQuality);
            var planned = PlannedDelivery.From(Plan(new("Enhanced", "Off", "Off", false, false, Intent: intent), Rtx));
            Check(planned.RtxSuperResolution && planned.Interpolation && planned.Deband && planned.Scaler is null,
                "a planner decision for RTX SR, blend smoothing and cleanup is what gets verified");

            // Downscaled RTX lane: the script removed VPP, the renderer does not upscale.
            var downscaled = Playing(1, rtxPlan, """{"hwdec-current":"d3d11va","vf":[],"video-params":{"w":960,"h":540},"video-out-params":{"w":960,"h":540,"pixelformat":"d3d11"},"osd-dimensions":{"w":640,"h":360},"user-data/adaptive/state":"RTX SR unnecessary at the current window size.","user-data/adaptive/rtx-sr":"accepted","vo-passes":{"fresh":[{"desc":"downscaling (mitchell)","count":10}],"redraw":[]}}""").Delivery();
            Check(State(downscaled, DeliveryFeature.NvidiaVpp) == DeliveryState.NotNeeded && State(downscaled, DeliveryFeature.RtxSuperResolution) == DeliveryState.NotNeeded,
                "VPP and RTX VSR idle at a smaller window are not needed, not delivered and not failed");
            var hq = Playing(2, vulkanPlan, RealVulkanEnhanced.Replace("\"osd-dimensions\":{\"w\":2048,\"h\":1152", "\"osd-dimensions\":{\"w\":640,\"h\":360")
                .Replace("{\"desc\":\"polar upscaling (ewa_lanczossharp)\",\"count\":171}", "{\"desc\":\"downscaling (mitchell)\",\"count\":171}")).Delivery();
            Check(State(hq, DeliveryFeature.ConventionalScaling) == DeliveryState.NotNeeded, "conventional upscaling into a smaller window is not needed");
            var other = Playing(3, vulkanPlan, RealVulkanEnhanced.Replace("polar upscaling (ewa_lanczossharp)", "ortho upscaling (bilinear)")).Delivery();
            Check(State(other, DeliveryFeature.ConventionalScaling) == DeliveryState.FellBack, "upscaling with another scaler is a fallback of the planned one");
        }

        // The session report keeps pass names and whether they ran, not per-frame timings.
        {
            var full = JsonSerializer.SerializeToElement(new { fresh = new[] { new { desc = "debanding", last = 1, avg = 2, peak = 3, count = 5, samples = new[] { 1, 2, 3 } } }, redraw = Array.Empty<object>() });
            string compact = DeliveryEvidence.CompactPasses(full).GetRawText();
            Check(compact.Contains("\"desc\":\"debanding\"") && compact.Contains("\"count\":5") && !compact.Contains("samples") && !compact.Contains("peak"),
                "vo-passes is reduced to what delivery needs");
            Check(DeliveryEvidence.Passes(Props("{\"vo-passes\":" + compact + "}"), "fresh").SequenceEqual(["debanding"]), "the compact form is still read as evidence");
        }
        return count;
    }
}
