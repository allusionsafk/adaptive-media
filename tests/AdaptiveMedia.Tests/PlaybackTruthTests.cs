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

    /// <summary>One attempt's evidence. With <paramref name="playing"/>, the player is
    /// polled twice with its position advancing, as a real player that is showing
    /// frames would be; without it, the single poll is from before playback moved.</summary>
    private static PlaybackAttemptEvidence Attempt(long id, PlaybackPlan plan, PlaybackAttemptKind kind = PlaybackAttemptKind.Stable,
        string json = "{}", NativeCompositionFacts? native = null, SustainedPlaybackHealth? health = null, bool fel = false, bool playing = false)
    {
        var e = new PlaybackAttemptEvidence(id, (int)id, plan, kind, kind == PlaybackAttemptKind.Stable ? "stable player" : "native generation x",
            kind == PlaybackAttemptKind.Stable ? null : "0.41.0-1044-g14f2d48cb", fel) { Started = true };
        var props = Props(json);
        if (playing)
        {
            e.Observe(new Dictionary<string, JsonElement>(props) { ["time-pos"] = JsonSerializer.SerializeToElement(10.0) }, native);
            props["time-pos"] = JsonSerializer.SerializeToElement(11.0);
        }
        e.Observe(props, native);
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

        var audioPlan = PlaybackPlanBuilder.Build("mpv.exe", "config", ["film.mkv"],
            new("Reference", "Off", "Off", false, false), Source1080 with { AudioCodec = "eac3" },
            new(1920, 1080), new(false, false), "audio-truth", bitstreamRequested: true);
        var audioPending = PlaybackTruthBuilder.Build(Attempt(1, audioPlan), [], []);
        Check(Has(audioPending.Requested, "Compressed audio passthrough") &&
              Has(audioPending.Planned, "Compressed audio passthrough") &&
              !Has(audioPending.Observed, "Compressed audio"),
            "bitstream request and plan never become observed without runtime output");
        var audioObserved = PlaybackTruthBuilder.Build(Attempt(1, audioPlan,
            json: """{"audio-out-params":{"format":"spdif-eac3"},"current-ao":"wasapi"}"""), [], []);
        Check(Has(audioObserved.Observed, "Compressed audio output") &&
              Has(audioObserved.Observed, "HDMI transport and Atmos unverified") &&
              !Has(audioObserved.Observed, "HDMI active") && !Has(audioObserved.Observed, "Atmos active"),
            "compressed player output is observed without claiming HDMI or Atmos");
        var userStart = audioPlan with { UserResumeAt = 42 };
        var userTruth = PlaybackTruthBuilder.Build(Attempt(1, userStart), [], []);
        Check(Has(userTruth.Requested, "User resume") && Has(userTruth.Planned, "User resume") &&
              !Has(userTruth.Planned, "Automatic recovery resume"),
            "a user resume is labelled as the user's choice");
        var recoveryStart = userStart with { UserResumeAt = null, RecoveryResumeAt = 38 };
        var recoveryTruth = PlaybackTruthBuilder.Build(Attempt(2, recoveryStart), [], []);
        Check(Has(recoveryTruth.Planned, "Automatic recovery resume") && !Has(recoveryTruth.Planned, "User resume"),
            "an automatic recovery point cannot masquerade as a user resume");

        // 1. RTX requested, conventional fallback planned: Requested says RTX; Planned and Observed do not.
        {
            var t = PlaybackTruthBuilder.Build(Attempt(1, noRtxPlan, json: """{"hwdec-current":"nvdec","gpu-api":"vulkan","current-vo":"gpu-next","vf":[]}""", playing: true), [], []);
            Check(Has(t.Requested, "RTX Super Resolution"), "requested RTX is shown as requested");
            Check(!Has(t.Planned, "RTX Super Resolution") && !Has(t.Planned, "VPP"), "a conventional plan does not claim RTX");
            Check(!Has(t.Observed, "RTX") && !Has(t.Observed, "VPP") && Has(t.Observed, "NVDEC hardware decoding: verified (hwdec-current = nvdec)") &&
                  Has(t.Observed, "gpu-next · Vulkan active"), "the ordinary Vulkan path is reported from observation");
            var early = PlaybackTruthBuilder.Build(Attempt(1, noRtxPlan, json: """{"hwdec-current":"nvdec"}"""), [], []);
            Check(!Has(early.Observed, ": verified") && Has(early.Observed, "Not yet observed: NVDEC hardware decoding"),
                "a decoder reported before playback has moved is not yet evidence of delivery");
            Check(Has(t.Why, "No compatible RTX processing path is available; using conventional scaling."), "why comes from the plan's recorded reason");
        }

        // 4. The D3D11 / NVIDIA VPP path, reported only from what the player said.
        {
            // The shape of a real RTX 4080 poll (mpv 0.41, see EnhancementDeliveryTests).
            string real = """
                {"hwdec-current":"d3d11va","gpu-api":"d3d11","current-vo":"gpu-next",
                 "vf":[{"name":"d3d11vpp","label":"adaptive-vpp","enabled":true,"params":{"scale":"1.333333","scaling-mode":"nvidia"}}],
                 "video-params":{"w":1920,"h":1080,"pixelformat":"d3d11"},"video-out-params":{"w":2560,"h":1440,"pixelformat":"d3d11"},
                 "osd-dimensions":{"w":2560,"h":1440},"video-sync":"display-resample","interpolation":true,"display-sync-active":true,
                 "video-speed-correction":0.999,"estimated-display-fps":239.9,"video-target-params":{"gamma":"bt.1886"},
                 "user-data/adaptive/rtx-sr":"accepted",
                 "vo-passes":{"fresh":[{"desc":"map frame (hwdec)","count":90},{"desc":"debanding","count":90}],
                              "redraw":[{"desc":"frame mixing (2 frames), color encoding","count":256}]}}
                """;
            var t = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: real, health: SustainedPlaybackHealth.Healthy, playing: true), [], []);
            Check(Has(t.Planned, "D3D11VA hardware decoding") && Has(t.Planned, "NVIDIA D3D11 VPP") && Has(t.Planned, "RTX Super Resolution at 1.333×") &&
                  Has(t.Planned, "gpu-next · Direct3D 11") && Has(t.Planned, "Smooth motion (display-resample)"), "RTX plan is described from its arguments");
            Check(Has(t.Observed, "D3D11VA hardware decoding: verified (hwdec-current = d3d11va)") &&
                  Has(t.Observed, "NVIDIA VPP scaling: verified (1920×1080 → 2560×1440 on D3D11 video surfaces)") &&
                  Has(t.Observed, "Temporal blend smoothing: verified (display-resample active") && Has(t.Observed, "Banding reduction: verified") &&
                  Has(t.Observed, "gpu-next · Direct3D 11 active") && Has(t.Observed, "2560×1440 output") &&
                  Has(t.Observed, "Display ~240 Hz") && Has(t.Observed, "SDR output"),
                "the real-good path is reported from observation");
            Check(Has(t.Observed, "RTX Video Super Resolution: unverified (") && !Has(t.Observed, "Super Resolution: verified") &&
                  t.Delivery.Single(v => v.Feature == DeliveryFeature.RtxSuperResolution).State == DeliveryState.Unverified,
                "proven NVIDIA VPP scaling and an accepted driver request are still not proof of RTX Video Super Resolution");
            Check(t.Fallback.Lines.SequenceEqual(["None observed"]), "nothing fell back on the real-good path");
            Check(Has(t.Why, "RTX Super Resolution selected because a 1080p source is presented at 1440p (1.333×) on compatible NVIDIA hardware."),
                "why the RTX path was chosen, from recorded plan facts");
            Check(t.Health.Lines[0] == "Healthy" && t.Recovery.Lines.SequenceEqual(["None"]), "healthy ordinary playback, no recovery");

            // Option values echo the launch vector; they are never delivery evidence.
            var echoed = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, playing: true, json:
                """{"video-sync":"display-resample","interpolation":true,"deband":true,"hwdec":"d3d11va","scale":"ewa_lanczossharp"}"""), [], []);
            Check(echoed.Delivery.All(v => v.State != DeliveryState.Verified) && !Has(echoed.Observed, ": verified") && !Has(echoed.Observed, "Smooth motion active"),
                "option readbacks that merely repeat the plan never populate Observed");

            // The filter configured but no scaled output observed: say exactly that.
            var noScale = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, playing: true, json:
                """{"vf":[{"name":"d3d11vpp","enabled":true,"params":{"scale":"1.333333","scaling-mode":"nvidia"}}],"video-params":{"w":1920,"h":1080},"video-out-params":{"w":1920,"h":1080,"pixelformat":"d3d11"},"user-data/adaptive/rtx-sr":"accepted"}"""), [], []);
            Check(Has(noScale.Fallback, "NVIDIA VPP scaling → the filter is configured, but the player reported 1920×1080 d3d11 output") &&
                  Has(noScale.Fallback, "RTX Video Super Resolution → ") && !Has(noScale.Observed, "VPP scaling: verified"),
                "a configured VPP filter without scaled output is a fallback, and so is the RTX processing that depended on it");
            var failed = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, json: """{"vf":[],"user-data/adaptive/state":"RTX filter could not be applied; continuing with standard scaling."}"""), [], []);
            Check(Has(failed.Fallback, "NVIDIA VPP scaling → the VPP filter could not be applied") && !Has(failed.Observed, "VPP scaling: verified"),
                "a failed RTX filter is a fallback, reported as soon as the player says so");
            var refused = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, playing: true, json: real.Replace("\"accepted\"", "\"rejected: Failed to enable NVIDIA RTX Super Resolution: E_INVALIDARG\"")), [], []);
            Check(Has(refused.Fallback, "RTX Video Super Resolution → the NVIDIA driver refused the request (Failed to enable NVIDIA RTX Super Resolution: E_INVALIDARG)") &&
                  Has(refused.Observed, "NVIDIA VPP scaling: verified"), "a driver refusal is a fallback while VPP scaling itself stays verified");
            var hdr = PlaybackTruthBuilder.Build(Attempt(1, Plan(new(true, true, "RTX", Rtx: true), RtxSmooth with { RtxHdr = true }),
                json: """{"video-target-params":{"gamma":"bt.1886"}}"""), [], []);
            Check(Has(hdr.Requested, "RTX Video HDR") && Has(hdr.Observed, "SDR output") && !Has(hdr.Observed, "HDR output"),
                "HDR requested is not HDR output observed");
            var fallback = PlaybackTruthBuilder.Build(Attempt(1, rtxPlan, playing: true, json: """{"video-sync":"audio","interpolation":false,"display-sync-active":false}"""), [], []);
            Check(Has(fallback.Fallback, "Temporal blend smoothing → the player returned to audio-clock timing because display timing was unstable") &&
                  !Has(fallback.Observed, "Temporal blend smoothing: verified"), "a motion fallback is reported as a fallback");
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
            Check(Has(none.Requested, "enhancement-layer composition requested") && !Has(none.Requested, "FEL") &&
                  Has(none.Planned, "Enhancement-layer composition requested"),
                "an enabled native lane requests composition without classifying the source as FEL");
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
            var stable = Attempt(2, Plan(new(false, false)), PlaybackAttemptKind.Stable, json: """{"hwdec-current":"nvdec"}""", health: SustainedPlaybackHealth.Healthy, playing: true);
            var redirected = toPrevious with { LaunchedAttempt = 2, LaunchedKind = PlaybackAttemptKind.Stable, LaunchedRuntime = "stable player", LaunchedResumeAt = 2537 };
            var s = PlaybackTruthBuilder.Build(stable, [failed], [redirected]);
            Check(s.Recovery.Lines.Single().EndsWith("→ Stable playback · resumed near 00:42:17") && !s.Recovery.Lines.Single().Contains("Previous"),
                "a selected previous runtime that never launched is never reported; stable is");
            Check(Has(s.Why, "Stable playback selected after") && !Has(s.Why, "Previous verified runtime selected"), "why names stable");
            // 8. The stale FEL attempt cannot populate the stable attempt's Observed.
            Check(!Has(s.Observed, "FEL") && !Has(s.Observed, "Composition") && Has(s.Observed, "Automatic hardware decoding: verified (hwdec-current = nvdec)"), "stale attempt evidence cannot populate Observed");

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
