using AdaptiveMedia;

internal static class EnhancementPlannerTests
{
    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool value, string name) { check(value, name); count++; }

        var source1080 = new MediaInfo(1920, 1080, 24, "h264", "bt.1886", "bt.709", PixelFormat: "yuv420p");
        var output1440 = new PlaybackTarget(2560, 1440, RefreshRateHz: 240);
        var rtx = new PlaybackCapabilities(true, true, "NVIDIA RTX", true);
        PlaybackEnvironment Environment(MediaInfo? source = null, PlaybackTarget? target = null,
            PlaybackCapabilities? capabilities = null, int itemCount = 1,
            bool generated = false, bool neural = false) =>
            new(source ?? source1080, target ?? output1440, capabilities ?? rtx, itemCount, generated, neural);
        EnhancementIntent Enhanced(DetailIntent detail = DetailIntent.Maximum,
            MotionIntent motion = MotionIntent.Original, CleanupIntent cleanup = CleanupIntent.Balanced,
            PerformanceIntent performance = PerformanceIntent.MaximumQuality) =>
            EnhancementIntent.ForEnhanced(detail, motion, cleanup, performance);

        var noUpscale = EnhancementPlanner.Decide(Enhanced(), Environment(target: new(1920, 1080, RefreshRateHz: 60)));
        Check(noUpscale.Detail == DetailImplementation.None &&
              noUpscale.Reasons.Any(x => x.Contains("already meets", StringComparison.OrdinalIgnoreCase)),
            "DETAIL: matching output blocks RTX despite compatible hardware");

        var eligible = EnhancementPlanner.Decide(Enhanced(), Environment());
        Check(eligible.Detail == DetailImplementation.NvidiaVpp &&
              eligible.Reasons.Any(x => x.Contains("compatible NVIDIA", StringComparison.OrdinalIgnoreCase)),
            "DETAIL: eligible maximum-quality upscale selects NVIDIA VPP");

        var ineligible = EnhancementPlanner.Decide(Enhanced(), Environment(capabilities: new(false, false)));
        Check(ineligible.Detail == DetailImplementation.Conventional &&
              ineligible.Reasons.Any(x => x.Contains("not available", StringComparison.OrdinalIgnoreCase)),
            "DETAIL: ineligible NVIDIA request falls back to conventional scaling truthfully");

        var efficient = EnhancementPlanner.Decide(Enhanced(performance: PerformanceIntent.Efficient),
            Environment(target: new(2048, 1152, RefreshRateHz: 60)));
        Check(efficient.Detail == DetailImplementation.None &&
              efficient.Reasons.Any(x => x.Contains("marginal", StringComparison.OrdinalIgnoreCase)),
            "DETAIL: Efficient rejects marginal optional enhancement");
        Check(eligible.Detail == DetailImplementation.NvidiaVpp,
            "DETAIL: MaximumQuality selects the strongest eligible detail path");

        var reference = EnhancementPlanner.Decide(EnhancementIntent.ForReference(), Environment(target: new(2560, 1440, RefreshRateHz: 240)));
        Check(reference.Detail == DetailImplementation.None && reference.Cleanup == CleanupImplementation.Off &&
              reference.Motion == MotionImplementation.Original,
            "REFERENCE: clean-cadence reference playback selects no aggressive discretionary enhancement");

        var independent = EnhancementPlanner.Decide(Enhanced(DetailIntent.Maximum, MotionIntent.Original,
            CleanupIntent.Clean, PerformanceIntent.MaximumQuality), Environment());
        Check(independent.Detail == DetailImplementation.NvidiaVpp && independent.Motion == MotionImplementation.Original &&
              independent.Cleanup == CleanupImplementation.Strong,
            "ENHANCED: detail, motion, and cleanup dimensions remain independent");

        var cleanCadence = EnhancementPlanner.Decide(Enhanced(motion: MotionIntent.CadenceCorrected), Environment());
        Check(cleanCadence.Cadence == CadenceQuality.Clean && cleanCadence.Motion == MotionImplementation.Original,
            "MOTION: 24 fps to 240 Hz is recognized as clean cadence");

        var smoothClean = EnhancementPlanner.Decide(Enhanced(motion: MotionIntent.BlendSmooth), Environment());
        Check(smoothClean.Cadence == CadenceQuality.Clean && smoothClean.Motion == MotionImplementation.BlendSmooth,
            "MOTION: clean cadence does not erase a smoother-motion request");

        var poorReference = EnhancementPlanner.Decide(EnhancementIntent.ForReference(),
            Environment(target: new(2560, 1440, RefreshRateHz: 60)));
        Check(poorReference.Cadence == CadenceQuality.Poor && poorReference.Motion == MotionImplementation.CadenceCorrected,
            "MOTION: poor cadence plus cinematic preservation selects correction without interpolation");

        var generatedFallback = EnhancementPlanner.Decide(Enhanced(motion: MotionIntent.GeneratedMotion), Environment());
        Check(generatedFallback.Motion == MotionImplementation.BlendSmooth &&
              generatedFallback.Reasons.Any(x => x.Contains("generated-frame interpolation is not available", StringComparison.OrdinalIgnoreCase)),
            "MOTION: unavailable generated motion falls back truthfully to temporal blending");
        var neuralFallback = EnhancementPlanner.Decide(Enhanced(motion: MotionIntent.NeuralMotion), Environment());
        Check(neuralFallback.Motion == MotionImplementation.BlendSmooth &&
              neuralFallback.Reasons.Any(x => x.Contains("neural motion is not available", StringComparison.OrdinalIgnoreCase)),
            "MOTION: unavailable neural motion never claims execution");

        var unknownRefresh = EnhancementPlanner.Decide(EnhancementIntent.ForReference(),
            Environment(target: new(2560, 1440)));
        Check(unknownRefresh.Cadence == CadenceQuality.Unknown && unknownRefresh.Motion == MotionImplementation.Original &&
              unknownRefresh.Reasons.Any(x => x.Contains("refresh rate is unknown", StringComparison.OrdinalIgnoreCase)),
            "MOTION: missing display information stays conservative and admits uncertainty");

        var preserveTexture = EnhancementPlanner.Decide(Enhanced(cleanup: CleanupIntent.PreserveTexture), Environment());
        Check(preserveTexture.Cleanup == CleanupImplementation.Off,
            "CLEANUP: PreserveTexture blocks strong debanding");
        var clean = EnhancementPlanner.Decide(Enhanced(cleanup: CleanupIntent.Clean), Environment());
        Check(clean.Cleanup == CleanupImplementation.Strong,
            "CLEANUP: Clean selects the stronger currently-supported cleanup");

        var automatic = EnhancementPreferences.IntentFor("Automatic", "BalancedImprovement", "Normal",
            "Balanced", "Original", "Balanced", "Balanced");
        Check(automatic.Mode == EnhancementMode.Automatic && automatic.Goal == AutomaticGoal.BalancedImprovement &&
              automatic.Strength == EnhancementStrength.Normal,
            "AUTOMATIC: result-oriented preferences create semantic intent");
        var explicitIntent = EnhancementPreferences.IntentFor("Enhanced", "ImproveDetail", "Strong",
            "Maximum", "Original", "Clean", "MaximumQuality");
        Check(explicitIntent.Mode == EnhancementMode.Enhanced && explicitIntent.Detail == DetailIntent.Maximum &&
              explicitIntent.Motion == MotionIntent.Original && explicitIntent.Cleanup == CleanupIntent.Clean,
            "ENHANCED: UI preferences remain independent semantic dimensions");
        var staleReference = EnhancementPreferences.IntentFor("Reference", "ImproveDetail", "Strong",
            "Maximum", "NeuralMotion", "Clean", "MaximumQuality");
        Check(staleReference == EnhancementIntent.ForReference(),
            "REFERENCE: stale aggressive saved values cannot leak into reference intent");

        var first = EnhancementPlanner.Decide(Enhanced(), Environment());
        var second = EnhancementPlanner.Decide(Enhanced(), Environment());
        Check(first == second && first.Reasons.SequenceEqual(second.Reasons),
            "DETERMINISM: identical immutable inputs produce identical decisions and reasons");

        PlaybackPlan SemanticPlan(EnhancementIntent intent, PlaybackCapabilities? capabilities = null,
            PlaybackTarget? target = null) => PlaybackPlanBuilder.Build("mpv.exe", "config", ["movie.mp4"],
                new PlaybackOptions(intent.Mode.ToString(), "Off", "Off", false, false, Intent: intent),
                source1080, target ?? output1440, capabilities ?? rtx, "semantic-test");

        var semanticNvidia = SemanticPlan(Enhanced());
        Check(semanticNvidia.Intent == Enhanced() && semanticNvidia.Decision?.Detail == DetailImplementation.NvidiaVpp &&
              semanticNvidia.RtxSrConstructed && semanticNvidia.Arguments.Contains("--hwdec=d3d11va") &&
              semanticNvidia.Arguments.Any(x => x.Contains("d3d11vpp=scale=1.333333:scaling-mode=nvidia", StringComparison.Ordinal)) &&
              semanticNvidia.Arguments.Contains("--vo=gpu-next"),
            "PLAN: maximum-detail intent translates to the established D3D11VA to NVIDIA VPP to gpu-next path");

        var semanticConventional = SemanticPlan(Enhanced(), new(false, false));
        Check(semanticConventional.Decision?.Detail == DetailImplementation.Conventional &&
              !semanticConventional.RtxSrConstructed && semanticConventional.Arguments.Contains("--scale=ewa_lanczossharp"),
            "PLAN: ineligible NVIDIA intent translates to conventional libplacebo scaling");

        var cadencePlan = SemanticPlan(Enhanced(motion: MotionIntent.CadenceCorrected), target: new(2560, 1440, RefreshRateHz: 60));
        Check(cadencePlan.Decision?.Motion == MotionImplementation.CadenceCorrected &&
              cadencePlan.Arguments.Contains("--video-sync=display-resample") &&
              !cadencePlan.Arguments.Contains("--interpolation=yes"),
            "PLAN: CadenceCorrected requests display resampling without temporal blending");

        var blendPlan = SemanticPlan(Enhanced(motion: MotionIntent.BlendSmooth));
        Check(blendPlan.Decision?.Motion == MotionImplementation.BlendSmooth &&
              blendPlan.Arguments.Contains("--interpolation=yes") && blendPlan.Arguments.Any(x => x.StartsWith("--tscale=")),
            "PLAN: BlendSmooth emits the existing mpv temporal interpolation request");

        var noEvidence = new PlaybackAttemptEvidence(501, 1, semanticNvidia, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        var nvidiaTruth = PlaybackTruthBuilder.Build(noEvidence, [], []);
        Check(nvidiaTruth.Intent.Lines.Any(x => x.Contains("Maximum detail", StringComparison.OrdinalIgnoreCase)) &&
              nvidiaTruth.Requested.Lines.Contains("RTX Super Resolution") &&
              nvidiaTruth.Planned.Lines.Contains("NVIDIA D3D11 VPP") &&
              !nvidiaTruth.Observed.Lines.Any(x => x.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                                  x.Contains("RTX", StringComparison.OrdinalIgnoreCase)),
            "TRUTH: intent, requested, and planned NVIDIA state never populate Observed without evidence");

        var futureRequest = SemanticPlan(Enhanced(motion: MotionIntent.NeuralMotion));
        var futureEvidence = new PlaybackAttemptEvidence(502, 1, futureRequest, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        var futureTruth = PlaybackTruthBuilder.Build(futureEvidence, [], []);
        Check(futureRequest.Decision?.Motion == MotionImplementation.BlendSmooth &&
              futureTruth.Requested.Lines.Any(x => x.Contains("Temporal blend", StringComparison.OrdinalIgnoreCase)) &&
              !futureTruth.Planned.Lines.Any(x => x.Contains("Generated", StringComparison.OrdinalIgnoreCase) || x.Contains("Neural", StringComparison.OrdinalIgnoreCase)) &&
              !futureTruth.Observed.Lines.Any(x => x.Contains("Generated", StringComparison.OrdinalIgnoreCase) || x.Contains("Neural", StringComparison.OrdinalIgnoreCase)),
            "TRUTH: unsupported future motion requests report the supported blend tier and never claim execution");

        return count;
    }
}
