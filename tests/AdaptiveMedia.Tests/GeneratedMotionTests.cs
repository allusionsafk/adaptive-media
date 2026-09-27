using AdaptiveMedia;
using System.Text.Json;

/// <summary>Experimental Generated Motion: only an explicit Enhanced choice on an
/// eligible SDR source with a validated runtime plans it; Automatic never does; and
/// generated frames are claimed only from the FRUC filter's own report.</summary>
internal static class GeneratedMotionTests
{
    private static readonly MediaInfo Sdr1080 = new(1920, 1080, 24, "h264", "bt.1886", "bt.709", "aac", "yuv420p");
    private static readonly PlaybackTarget Panel = new(2560, 1600, RefreshRateHz: 240);
    private static readonly PlaybackCapabilities Rtx = new(true, true, "NVIDIA GeForce RTX 4080 Laptop GPU", Rtx: true);
    private static readonly GeneratedMotionBackend Backend = new(@"C:\runtime\gm1\mpv.exe", "gm1", "test runtime");
    private static readonly GeneratedMotionPower Ac = new(true, false);

    private static PlaybackOptions Enhanced(MotionIntent motion, string upscale = "Off") =>
        new("Enhanced", upscale, "Off", false, false, Intent: EnhancementIntent.ForEnhanced(DetailIntent.Preserve, motion, CleanupIntent.PreserveTexture));

    private static PlaybackPlan Plan(PlaybackOptions options, MediaInfo? source = null, GeneratedMotionBackend? backend = null,
        GeneratedMotionPower? power = null, string[]? items = null, PlaybackCapabilities? caps = null) =>
        PlaybackPlanBuilder.Build(@"C:\stock\mpv.exe", "config", items ?? ["movie.mkv"], options, source ?? Sdr1080, Panel,
            caps ?? Rtx, "gm-test", generatedMotion: backend, power: power);

    private static Dictionary<string, JsonElement> Props(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static string Fruc(string state, int generated, int repeated = 0, int fallbacks = 0, string? last = null) =>
        "{\"vf-metadata/fruc\":{\"lavfi.nvofruc.state\":\"" + state + "\",\"lavfi.nvofruc.frame\":\"source\",\"lavfi.nvofruc.generated\":\"" + generated +
        "\",\"lavfi.nvofruc.repeated\":\"" + repeated + "\",\"lavfi.nvofruc.errors\":\"0\",\"lavfi.nvofruc.fallbacks\":\"" + fallbacks + "\"" +
        (last is null ? "" : ",\"lavfi.nvofruc.last_fallback\":\"" + last + "\"") +
        "},\"estimated-vf-fps\":48.0,\"display-sync-active\":true,\"video-sync\":\"display-resample\",\"interpolation\":true,\"hwdec-current\":\"d3d11va\"}";

    private static DeliveryVerdict? Verdict(PlaybackPlan plan, string json) =>
        EnhancementDeliveryVerifier.Verify(plan, Props(json), DeliveryLatch.Empty with { FramesFlowing = true }, true, false)
            .SingleOrDefault(v => v.Feature == DeliveryFeature.GeneratedMotion);

    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "GENERATED MOTION: " + m); }

        // Planning
        var planned = Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend, power: Ac);
        var args = planned.Arguments.TakeWhile(a => a != "--").ToArray();
        Check(planned.Renderer == GeneratedMotionPolicy.Renderer && planned.Executable == Backend.Executable &&
              planned.Decision?.Motion == MotionImplementation.GeneratedMotion && planned.Requested.MotionMode == "Generated",
            "an explicit Enhanced request on an eligible source runs the validated experimental runtime");
        Check(args.Contains("--vf=@fruc:lavfi=[nvofruc=fps=48]") && args.Contains("--hwdec=d3d11va") &&
              args.Contains("--gpu-api=d3d11") && args.Contains("--d3d11-adapter=NVIDIA GeForce RTX 4080 Laptop GPU"),
            "the filter doubles 24 fps on the NVIDIA GPU's own D3D11 frames");
        Check(args.Contains("--swapchain-depth=6") && !Plan(Enhanced(MotionIntent.BlendSmooth), backend: Backend, power: Ac)
                  .Arguments.Contains("--swapchain-depth=6"),
            "only the Generated Motion lane deepens the swapchain against FRUC presentation stalls");
        Check(args.Contains("--video-sync=display-resample") && args.Contains("--interpolation=yes") &&
              !args.Contains("--profile=nvidia") && !args.Any(a => a.Contains("d3d11vpp")),
            "Smoother motion timing stays underneath as the in-player fallback, without RTX VPP");
        Check(Plan(Enhanced(MotionIntent.GeneratedMotion), Sdr1080 with { Fps = 23.976 }, Backend, Ac)
              .Arguments.Contains("--vf=@fruc:lavfi=[nvofruc=fps=47.952]"), "23.976 fps doubles to 47.952 fps");

        foreach (var goal in new[] { AutomaticGoal.SmootherMotion, AutomaticGoal.BalancedImprovement })
        {
            var automatic = Plan(new("Automatic", "Off", "Off", false, false,
                Intent: EnhancementIntent.ForAutomatic(goal, EnhancementStrength.Strong)), backend: Backend, power: Ac);
            Check(automatic.Decision?.Motion == MotionImplementation.BlendSmooth && automatic.Executable == @"C:\stock\mpv.exe" &&
                  !automatic.Arguments.Any(a => a.Contains("nvofruc")) &&
                  automatic.Reasons.Any(r => r.Contains("Automatic does not use experimental Generated Motion")),
                "Automatic " + goal + " Strong never selects Generated Motion even when it is available");
        }

        var cases = new (string Name, PlaybackPlan Plan, string Reason)[]
        {
            ("no runtime", Plan(Enhanced(MotionIntent.GeneratedMotion), power: Ac), "not installed or did not validate"),
            ("HDR", Plan(Enhanced(MotionIntent.GeneratedMotion), Sdr1080 with { Transfer = "pq", Primaries = "bt.2020" }, Backend, Ac), "8-bit SDR only"),
            ("10-bit", Plan(Enhanced(MotionIntent.GeneratedMotion), Sdr1080 with { PixelFormat = "yuv420p10" }, Backend, Ac), "8-bit only"),
            ("4K", Plan(Enhanced(MotionIntent.GeneratedMotion), Sdr1080 with { Width = 3840, Height = 2160 }, Backend, Ac), "above 1440p"),
            ("60 fps", Plan(Enhanced(MotionIntent.GeneratedMotion), Sdr1080 with { Fps = 60 }, Backend, Ac), "23.976–30 fps"),
            ("playlist", Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend, power: Ac, items: ["a.mkv", "b.mkv"]), "not a playlist"),
            ("battery", Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend, power: new(false, false)), "AC power"),
            ("Energy Saver", Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend, power: new(true, true)), "Energy Saver off"),
            ("unknown power", Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend), "AC power"),
            ("non-RTX", Plan(Enhanced(MotionIntent.GeneratedMotion), backend: Backend, power: Ac, caps: new(false, false)), "NVIDIA RTX GPU"),
        };
        foreach (var (name, plan, reason) in cases)
            Check(plan.Decision?.Motion == MotionImplementation.BlendSmooth && plan.Executable == @"C:\stock\mpv.exe" &&
                  plan.Renderer != GeneratedMotionPolicy.Renderer && !plan.Arguments.Any(a => a.Contains("nvofruc")) &&
                  plan.Reasons.Any(r => r.StartsWith("Generated Motion is unavailable because", StringComparison.Ordinal) && r.Contains(reason)),
                name + " falls back to temporal blend smoothing on the stock player and says why");

        var legacy = Plan(new("Enhanced", "Off", "Generated", false, false), power: Ac);
        Check(legacy.Requested.MotionMode == "Smooth" && !legacy.Arguments.Any(a => a.Contains("nvofruc")),
            "a bare Generated motion mode without a runtime degrades to Smooth");
        var withRtx = Plan(Enhanced(MotionIntent.GeneratedMotion, "RtxVsr") with { RtxHdr = true }, backend: Backend, power: Ac);
        Check(withRtx.Renderer == GeneratedMotionPolicy.Renderer && !withRtx.RtxSrConstructed && !withRtx.RtxHdrConstructed &&
              withRtx.Reasons.Any(r => r.Contains("not combined with experimental Generated Motion")),
            "RTX Video processing is not stacked on the experimental filter");
        var compat = Plan(new("Compatibility", "Off", "Off", false, false, Intent: EnhancementIntent.ForCompatibility()), backend: Backend, power: Ac);
        Check(compat.Renderer == "Compatibility D3D11" && !compat.Arguments.Any(a => a.Contains("nvofruc")),
            "Compatibility never uses Generated Motion");

        // Delivery: only the filter's own report can establish generated frames.
        var active = Verdict(planned, Fruc("active", 574, 2));
        Check(active is { State: DeliveryState.Verified } && active.Evidence.Contains("synthesized 574 intermediate frames") &&
              active.Evidence.Contains("2 repeated at scene cuts"), "an active backend with synthesized frames is verified with counts");
        var passthrough = Verdict(planned, Fruc("passthrough:FRUC fence timeout", 120, 0, 1, "FRUC fence timeout"));
        Check(passthrough is { State: DeliveryState.FellBack } && passthrough.Evidence.Contains("FRUC fence timeout") &&
              passthrough.Evidence.Contains("temporal blend smoothing continued"), "a backend in passthrough is reported as a fallback with its reason");
        var reactivated = Verdict(planned, Fruc("active", 500, 0, 1, "simulated backend failure"));
        Check(reactivated is { State: DeliveryState.FellBack }, "a fallback earlier in the stream stays visible after a seek re-activates the filter");
        var missing = Verdict(planned, """{"estimated-vf-fps":24.0,"hwdec-current":"d3d11va"}""");
        Check(missing is { State: DeliveryState.FellBack } && missing.Evidence.Contains("no Generated Motion filter output"),
            "a filter mpv disabled is a fallback, not a silent success");
        var idle = Verdict(planned, Fruc("active", 0, 7));
        Check(idle is { State: DeliveryState.Unverified }, "a loaded backend that has only repeated frames is not a delivery claim");
        var pending = EnhancementDeliveryVerifier.Verify(planned, Props(Fruc("active", 10)), DeliveryLatch.Empty, true, false)
            .Single(v => v.Feature == DeliveryFeature.GeneratedMotion);
        Check(pending.State == DeliveryState.Pending, "nothing is judged before frames are flowing");
        var blendPlan = Plan(Enhanced(MotionIntent.BlendSmooth), backend: Backend, power: Ac);
        var blendVerdicts = EnhancementDeliveryVerifier.Verify(blendPlan, Props(Fruc("active", 574)), DeliveryLatch.Empty with { FramesFlowing = true }, true, false);
        Check(blendVerdicts.All(v => v.Feature != DeliveryFeature.GeneratedMotion) &&
              blendVerdicts.All(v => !v.Label.Contains("Generated", StringComparison.OrdinalIgnoreCase)),
            "Blend Smooth is never labelled or credited as generated motion");

        // Truth wording
        var evidence = new PlaybackAttemptEvidence(1, 1, planned, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        var props = Props(Fruc("active", 574, 2));
        props["time-pos"] = JsonSerializer.SerializeToElement(5.0); evidence.Observe(props, null);
        props["time-pos"] = JsonSerializer.SerializeToElement(6.0); evidence.Observe(props, null);
        var truth = PlaybackTruthBuilder.Build(evidence, [], []);
        Check(truth.Planned.Lines.Any(l => l.StartsWith("Generated Motion: NVIDIA optical-flow FRUC to 48 fps", StringComparison.Ordinal)) &&
              truth.Planned.Lines.Any(l => l.Contains("this blending is not frame generation")),
            "Planned names the FRUC request and keeps blending separate from generation");
        Check(truth.Delivery.Any(v => v.Feature == DeliveryFeature.GeneratedMotion && v.State == DeliveryState.Verified),
            "the truth chain carries the observed Generated Motion verdict");
        var blendEvidence = new PlaybackAttemptEvidence(2, 2, blendPlan, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        var blendTruth = PlaybackTruthBuilder.Build(blendEvidence, [], []);
        Check(blendTruth.Sections.SelectMany(s => s.Lines).All(l => !l.Contains("Generated Motion", StringComparison.Ordinal) && !l.Contains("FRUC", StringComparison.Ordinal)),
            "a Smoother motion playback never mentions generated frames");
        return count;
    }
}
