using AdaptiveMedia;
using System.Text.Json;

/// <summary>Playback truth surface: requested never becomes observed, the path
/// that actually launched is the one reported, and earlier attempts only ever
/// appear as history.</summary>
internal static class PlaybackTruthTests
{
    private static readonly MediaInfo Source1080 = new(1920, 1080, 23.976, "h264", "bt.1886", "bt.709");
    private static readonly PlaybackOptions RtxSmooth = new("Enhanced", "RtxVsr", "Smooth", true, false);

    private static PlaybackPlan Plan(PlaybackCapabilities caps, PlaybackOptions? options = null, PlaybackTarget? target = null) =>
        PlaybackPlanBuilder.Build("mpv.exe", "config", ["movie.mkv"], options ?? RtxSmooth, Source1080,
            target ?? new PlaybackTarget(2560, 1440, Fullscreen: true), caps, "truth-test");

    private static Dictionary<string, JsonElement> Props(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static PlaybackAttemptEvidence Attempt(long id, PlaybackPlan plan, PlaybackAttemptKind kind = PlaybackAttemptKind.Stable,
        string json = "{}", NativeCompositionFacts? native = null, SustainedPlaybackHealth? health = null, bool fel = false)
    {
        var e = new PlaybackAttemptEvidence(id, (int)id, plan, kind, kind == PlaybackAttemptKind.Stable ? "stable player" : "native generation x",
            kind == PlaybackAttemptKind.Stable ? null : "0.41.0-1044-g14f2d48cb", fel) { Started = true };
        e.Observe(Props(json), native);
        if (health is { } h) e.Finish(new PlaybackHealthSnapshot { AttemptId = id, State = h, WorstCondition = h, PlaybackProgressed = true });
        return e;
    }

    private static readonly NativeCompositionFacts Fel = new(true, 2, true, true, "FullEnhancementLayer", "d3d11va");
    private static readonly NativeCompositionFacts BaseOnly = new(true, 1, false, false, "BaseLayerOnly", "d3d11va");

    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "TRUTH: " + m); }
        bool Has(PlaybackTruthSection s, string text) => s.Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

        var rtxPlan = Plan(new(true, true, "NVIDIA GeForce RTX 4080", Rtx: true));
        var noRtxPlan = Plan(new(true, false));

        // 1. RTX requested, conventional fallback planned: Requested says RTX; Planned and Observed do not.
        {
            var t = PlaybackTruthBuilder.Build(Attempt(1, noRtxPlan, json: """{"hwdec-current":"nvdec","gpu-api":"vulkan","current-vo":"gpu-next","vf":[]}"""), [], []);
            Check(Has(t.Requested, "RTX Super Resolution"), "requested RTX is shown as requested");
            Check(!Has(t.Planned, "RTX Super Resolution") && !Has(t.Planned, "VPP"), "a conventional plan does not claim RTX");
            Check(!Has(t.Observed, "RTX") && !Has(t.Observed, "VPP") && Has(t.Observed, "NVDEC active") && Has(t.Observed, "gpu-next · Vulkan active"),
                "the ordinary Vulkan path is reported from observation");
            Check(Has(t.Why, "No compatible RTX processing path is available; using conventional scaling."), "why comes from the plan's recorded reason");
        }

        // 4. The D3D11 / NVIDIA VPP path, reported only from what the player said.
        {
            string real = """
                {"hwdec-current":"d3d11va","gpu-api":"d3d11","current-vo":"gpu-next",
                 "vf":[{"name":"d3d11vpp","label":"adaptive-vpp","enabled":true,"params":{"scale":"1.333333","scaling-mode":"nvidia"}}],
                 "video-params":{"w":1920,"h":1080},"video-out-params":{"w":2560,"h":1440},"osd-dimensions":{"w":2560,"h":1440},
                 "video-sync":"display-resample","interpolation":true,"estimated-display-fps":239.9,
                 "video-target-params":{"gamma":"bt.1886"}}
                """;
            var t = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: real, health: SustainedPlaybackHealth.Healthy), [], []);
            Check(Has(t.Planned, "D3D11VA hardware decoding") && Has(t.Planned, "NVIDIA D3D11 VPP") && Has(t.Planned, "RTX Super Resolution at 1.333×") &&
                  Has(t.Planned, "gpu-next · Direct3D 11") && Has(t.Planned, "Smooth motion (display-resample)"), "RTX plan is described from its arguments");
            Check(Has(t.Observed, "D3D11VA active") && Has(t.Observed, "NVIDIA VPP active · 1920×1080 → 2560×1440") &&
                  Has(t.Observed, "gpu-next · Direct3D 11 active") && Has(t.Observed, "2560×1440 output") &&
                  Has(t.Observed, "Smooth motion active (display-resample)") && Has(t.Observed, "Display ~240 Hz") && Has(t.Observed, "SDR output"),
                "the real-good path is reported from observation");
            Check(!Has(t.Observed, "RTX Super Resolution"), "driver-side RTX processing is never shown as observed");
            Check(Has(t.Why, "RTX Super Resolution selected because a 1080p source is presented at 1440p (1.333×) on compatible NVIDIA hardware."),
                "why the RTX path was chosen, from recorded plan facts");
            Check(t.Health.Lines[0] == "Healthy" && t.Recovery.Lines.SequenceEqual(["None"]), "healthy ordinary playback, no recovery");

            // The filter configured but no scaled output observed: say exactly that.
            var noScale = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: """{"vf":[{"name":"d3d11vpp","enabled":true}],"video-params":{"w":1920,"h":1080},"video-out-params":{"w":1920,"h":1080}}"""), [], []);
            Check(Has(noScale.Observed, "NVIDIA VPP filter present; no scaling observed") && !Has(noScale.Observed, "VPP active"),
                "a configured VPP filter without scaled output is not claimed as active");
            var failed = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: """{"vf":[],"user-data/adaptive/state":"RTX filter could not be applied; continuing with standard scaling."}"""), [], []);
            Check(Has(failed.Observed, "RTX filter could not be applied") && !Has(failed.Observed, "VPP active"), "a failed RTX filter is observed as failed");
            var hdr = PlaybackTruthBuilder.Build(Attempt(1, Plan(new(true, true, "RTX", Rtx: true), RtxSmooth with { RtxHdr = true }),
                json: """{"video-target-params":{"gamma":"bt.1886"}}"""), [], []);
            Check(Has(hdr.Requested, "RTX Video HDR") && Has(hdr.Observed, "SDR output") && !Has(hdr.Observed, "HDR output"),
                "HDR requested is not HDR output observed");
            var fallback = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: """{"video-sync":"audio"}"""), [], []);
            Check(Has(fallback.Observed, "Smooth motion not active (timing fallback to audio)"), "a motion fallback is observed as a fallback");
        }

        // 12. No requested or planned feature becomes observed without evidence, over a small plan matrix.
        foreach (var caps in new PlaybackCapabilities[] { new(true, true, "RTX", Rtx: true), new(true, false), new(false, false) })
        foreach (var options in new[] { RtxSmooth, RtxSmooth with { RtxHdr = true, MotionMode = "Gentle" }, new PlaybackOptions("Compatibility", "HighQuality", "Off", false, false) })
        foreach (var kind in Enum.GetValues<PlaybackAttemptKind>())
        {
            var t = PlaybackTruthBuilder.Build(Attempt(1, Plan(caps, options), kind, fel: kind != PlaybackAttemptKind.Stable), [], []);
            var expected = kind == PlaybackAttemptKind.Stable ? new[] { "Nothing observed yet" } : new[] { "Composition not yet observed" };
            Check(t.Observed.Lines.SequenceEqual(expected), $"no evidence, no observation ({caps.Rtx}/{options.Profile}/{kind})");
        }

        // 2-3. Native FEL: requested and planned, observed only from evidence.
        {
            var nativePlan = Plan(new(false, false));
            var none = PlaybackTruthBuilder.Build(Attempt(1, nativePlan, PlaybackAttemptKind.NativeCurrent, fel: true), [], []);
            Check(Has(none.Requested, "full enhancement layer") && Has(none.Planned, "Full enhancement-layer composition requested"),
                "FEL requested and planned");
            Check(!Has(none.Observed, "FEL") && Has(none.Observed, "Composition not yet observed"), "no evidence: no FEL claim");
            var baseOnly = PlaybackTruthBuilder.Build(Attempt(1, nativePlan, PlaybackAttemptKind.NativeCurrent, native: BaseOnly, fel: true), [], []);
            Check(!Has(baseOnly.Observed, "FEL") && Has(baseOnly.Observed, "Base layer only"), "base-layer evidence: no FEL claim");
            var fel = PlaybackTruthBuilder.Build(Attempt(1, nativePlan, PlaybackAttemptKind.NativeCurrent, native: Fel, fel: true), [], []);
            Check(Has(fel.Observed, "BL + EL decoded (2 decoder instances)") && Has(fel.Observed, "FEL composition observed"), "real FEL evidence is reported");
        }

        // 5-7, 9. Recovery reports what launched; resume only from the launched plan.
        {
            var nativePlan = Plan(new(false, false));
            var failed = Attempt(1, nativePlan, PlaybackAttemptKind.NativeCurrent, native: Fel, health: SustainedPlaybackHealth.Frozen, fel: true);
            var previous = Attempt(2, nativePlan, PlaybackAttemptKind.NativePrevious, native: Fel, health: SustainedPlaybackHealth.Healthy, fel: true);
            var toPrevious = new PlaybackRecoveryRecord(1, "native generation a", "Frozen", "RetryOnPreviousNative", 2537, "native generation b",
                "The native runtime failed during playback; recovering once on the retained verified runtime.", PlaybackAttemptKind.NativeCurrent,
                2, PlaybackAttemptKind.NativePrevious, "native generation b", 2537);
            var t = PlaybackTruthBuilder.Build(previous, [failed], [toPrevious]);
            Check(t.Recovery.Lines.Single() == "Native runtime stopped responding → Previous verified native runtime · resumed near 00:42:17",
                "recovery names the previous runtime that launched, resumed near");
            Check(t.Health.Lines[0] == "Healthy" && t.Headline == "Playback health: Healthy\nRecovery: Previous verified native runtime · resumed near 00:42:17",
                "a recovered attempt is Healthy with concise prior-failure context");
            Check(Has(t.Why, "Previous verified runtime selected after the native runtime stopped responding."), "why names the actual path and trigger");
            Check(t.History.Single().Title == "Attempt 1 · Native runtime" && t.History.Single().Lines.SequenceEqual(
                ["Full FEL observed", "Player stopped responding", "Recovery → Previous verified native runtime · resumed near 00:42:17"]),
                "the failed attempt appears only as history");
            Check(Has(t.Observed, "FEL composition observed"), "the replacement's own FEL evidence is reported for it");

            // 6. Previous was selected but stable is what launched.
            var stable = Attempt(2, Plan(new(false, false)), PlaybackAttemptKind.Stable, json: """{"hwdec-current":"nvdec"}""", health: SustainedPlaybackHealth.Healthy);
            var redirected = toPrevious with { LaunchedAttempt = 2, LaunchedKind = PlaybackAttemptKind.Stable, LaunchedRuntime = "stable player", LaunchedResumeAt = 2537 };
            var s = PlaybackTruthBuilder.Build(stable, [failed], [redirected]);
            Check(s.Recovery.Lines.Single().EndsWith("→ Stable playback · resumed near 00:42:17") && !s.Recovery.Lines.Single().Contains("Previous"),
                "a selected previous runtime that never launched is never reported; stable is");
            Check(Has(s.Why, "Stable playback selected after") && !Has(s.Why, "Previous verified runtime selected"), "why names stable");
            // 8. The stale FEL attempt cannot populate the stable attempt's Observed.
            Check(!Has(s.Observed, "FEL") && !Has(s.Observed, "Composition") && Has(s.Observed, "NVDEC active"), "stale attempt evidence cannot populate Observed");

            // 7. Resume shown only from the launched plan, never from the selected point.
            var noResume = toPrevious with { LaunchedResumeAt = null };
            Check(PlaybackTruthBuilder.RecoveryOutcome(noResume) == "Previous verified native runtime · restarted from beginning",
                "no launched resume point: restarted from beginning, even though one was selected");
            Check(PlaybackTruthBuilder.RecoveryOutcome(toPrevious with { LaunchedAttempt = null, LaunchedKind = null }) == "Failed: no replacement player started",
                "a recovery whose replacement never started is reported as failed");
            Check(PlaybackTruthBuilder.RecoveryOutcome(new(1, "stable player", "Stalled", "NoRecoveryAvailable", null, "none", "")) == "Unavailable for this player",
                "stable playback has no recovery to offer");
        }

        // 10-11. Calm, distinct health words.
        Check(HealthText.Describe(SustainedPlaybackHealth.TransientPressure) == "Temporary presentation pressure" &&
              !new[] { "fail", "error", "stopp", "problem" }.Any(w => HealthText.Describe(SustainedPlaybackHealth.TransientPressure).Contains(w, StringComparison.OrdinalIgnoreCase)),
            "TransientPressure reads calmly");
        Check(HealthText.Describe(SustainedPlaybackHealth.SustainedDegradation) == "Sustained presentation pressure", "degradation wording");
        Check(HealthText.Describe(SustainedPlaybackHealth.SourceFailure) == "Source could not be played" &&
              HealthText.Describe(SustainedPlaybackHealth.RuntimeFailure) == "Runtime failure", "source failure is not runtime failure");
        Check(HealthText.Describe(SustainedPlaybackHealth.UserStopped) == "Stopped by user", "user stop wording");

        // 13. Bounded history.
        {
            var plan = Plan(new(false, false));
            var earlier = Enumerable.Range(1, 12).Select(i => Attempt(i, plan, health: SustainedPlaybackHealth.RuntimeFailure)).ToArray();
            var t = PlaybackTruthBuilder.Build(Attempt(99, plan), earlier, []);
            Check(t.History.Count == PlaybackTruthBuilder.MaximumHistory && t.History[^1].Title.StartsWith("Attempt 12"), "history is bounded to the most recent attempts");
            Check(PlaybackTruthBuilder.Build(earlier[^1], earlier, []).History.All(h => !h.Title.StartsWith("Attempt 12")), "the current attempt is never its own history");
        }

        // mpv 0.41 reports gpu-api as an object list; it is still observed.
        {
            var t = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: """{"current-vo":"gpu-next","gpu-api":[{"name":"d3d11","enabled":true,"params":{}}]}"""), [], []);
            Check(Has(t.Observed, "gpu-next · Direct3D 11 active"), "list-valued gpu-api is observed");
        }
        // Live health comes from this attempt's own monitor until it finishes.
        {
            var e = new PlaybackAttemptEvidence(1, 1, Plan(new(false, false)), PlaybackAttemptKind.Stable, "stable player") { Started = true };
            var state = SustainedPlaybackHealth.Healthy;
            e.TrackHealth(() => new PlaybackHealthSnapshot { AttemptId = 1, State = state, WorstCondition = state });
            Check(PlaybackTruthBuilder.Build(e, [], []).Health.Lines[0] == "Healthy", "live health is read while playing");
            state = SustainedPlaybackHealth.TransientPressure;
            Check(PlaybackTruthBuilder.Build(e, [], []).Health.Lines[0] == "Temporary presentation pressure", "live health follows the attempt");
            e.Finish(new PlaybackHealthSnapshot { AttemptId = 1, State = SustainedPlaybackHealth.EndedNormally, WorstCondition = SustainedPlaybackHealth.Healthy });
            state = SustainedPlaybackHealth.Frozen;
            Check(PlaybackTruthBuilder.Build(e, [], []).Health.Lines[0] == "Ended normally", "after finishing, the final snapshot is authoritative");
        }

        // Evidence is copied: mutating the caller's dictionary cannot change an attempt's observation.
        {
            var props = Props("""{"hwdec-current":"d3d11va"}""");
            var e = new PlaybackAttemptEvidence(1, 1, Plan(new(false, false)), PlaybackAttemptKind.Stable, "stable player") { Started = true };
            e.Observe(props, null);
            props.Clear();
            Check(e.Read().Observed.ContainsKey("hwdec-current"), "attempt evidence is a snapshot, not a live view");
            var notStarted = new PlaybackAttemptEvidence(2, 2, Plan(new(false, false)), PlaybackAttemptKind.NativePrevious, "native generation b");
            Check(PlaybackTruthBuilder.Build(notStarted, [], []).Observed.Lines.Single() == "Nothing observed: the player did not start.",
                "a player that did not start observes nothing");
        }
        return count;
    }
}
