using AdaptiveMedia;

/// <summary>Automatic health response: the trigger rule, the shared-budget
/// decision, the one-shot gate, the recovery stop, and the resume point. All
/// pure; the production path is exercised in the Dolby Vision recovery suite.</summary>
internal static class PlaybackRecoveryTests
{
    private const int Max = 1; // The native rollback budget; mirrors NativeDvRollbackPolicy.MaximumRollbacks.

    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "RECOVERY V1: " + m); }
        PlaybackRecoveryStep Step(SustainedPlaybackHealth s, PlaybackAttemptKind k = PlaybackAttemptKind.NativeCurrent,
            int used = 0, bool? previous = true, bool progressed = true) =>
            PlaybackRecoveryPolicy.Decide(s, progressed, k, used, Max, previous).Step;

        // 1-3. Hard failures of playback that had started recover once on the previous runtime.
        foreach (var hard in new[] { SustainedPlaybackHealth.Stalled, SustainedPlaybackHealth.Frozen, SustainedPlaybackHealth.RuntimeFailure })
            Check(Step(hard) == PlaybackRecoveryStep.RetryOnPreviousNative, $"{hard} on current native -> previous");

        // 4-7. Everything else is report-only, including sustained degradation.
        foreach (var soft in Enum.GetValues<SustainedPlaybackHealth>().Except([SustainedPlaybackHealth.Stalled,
            SustainedPlaybackHealth.Frozen, SustainedPlaybackHealth.RuntimeFailure]))
            foreach (var kind in Enum.GetValues<PlaybackAttemptKind>())
                Check(Step(soft, kind) == PlaybackRecoveryStep.None, $"{soft} on {kind} never recovers");
        Check(!PlaybackRecoveryPolicy.IsTrigger(SustainedPlaybackHealth.SustainedDegradation, true), "degradation is deliberately report-only");
        // Pause, buffering and seeking never produce a trigger state at all; the
        // classifier half of that is proven below with real samples.

        // A stall or failure before any confirmed progress is startup's question.
        foreach (var hard in new[] { SustainedPlaybackHealth.Stalled, SustainedPlaybackHealth.Frozen, SustainedPlaybackHealth.RuntimeFailure })
            Check(Step(hard, progressed: false) == PlaybackRecoveryStep.None, $"{hard} before progress is left to startup health");

        // 8. The startup rollback already spent the shared budget.
        Check(Step(SustainedPlaybackHealth.Frozen, used: 1) == PlaybackRecoveryStep.UseStablePlayback, "budget spent at startup -> stable");
        // 9. The previous runtime itself hard-fails.
        Check(Step(SustainedPlaybackHealth.Stalled, PlaybackAttemptKind.NativePrevious) == PlaybackRecoveryStep.UseStablePlayback, "previous hard-fails -> stable");
        Check(Step(SustainedPlaybackHealth.Stalled, PlaybackAttemptKind.NativePrevious, used: 0, previous: true) == PlaybackRecoveryStep.UseStablePlayback,
            "a demoted start on the previous runtime still goes to stable, never back to current");
        // 10. Previous missing or invalid.
        Check(Step(SustainedPlaybackHealth.Frozen, previous: false) == PlaybackRecoveryStep.UseStablePlayback, "invalid previous -> stable");
        Check(Step(SustainedPlaybackHealth.Frozen, previous: null) == PlaybackRecoveryStep.UseStablePlayback, "unresolved previous -> stable");
        // 11-12. The cap is tested before availability: even a usable previous cannot
        // produce a third native attempt, and nothing ever points back at current.
        Check(Step(SustainedPlaybackHealth.RuntimeFailure, used: 1, previous: true) == PlaybackRecoveryStep.UseStablePlayback,
            "a usable previous cannot overrule a spent budget");
        Check(Enum.GetValues<SustainedPlaybackHealth>().All(s => Step(s, PlaybackAttemptKind.NativePrevious) != PlaybackRecoveryStep.RetryOnPreviousNative),
            "no state on the previous runtime ever produces another native retry (no bounce)");
        // 23. Stable hard failure is reported, never relaunched.
        foreach (var hard in new[] { SustainedPlaybackHealth.Stalled, SustainedPlaybackHealth.Frozen, SustainedPlaybackHealth.RuntimeFailure })
            Check(Step(hard, PlaybackAttemptKind.Stable) == PlaybackRecoveryStep.NoRecoveryAvailable, $"stable {hard} is not auto-relaunched");

        // 13-14. The gate: one claim per attempt, only while active, never after supersession.
        {
            var gate = new PlaybackRecoveryGate();
            gate.Activate(1);
            int wins = 0;
            Parallel.For(0, 64, _ => { if (gate.TryClaim(1)) Interlocked.Increment(ref wins); });
            Check(wins == 1, "64 racing hard-failure callbacks produce exactly one recovery");
            Check(!gate.TryClaim(1), "a later exit callback after a health claim cannot claim again");
            gate.Activate(2);
            Check(!gate.TryClaim(1) && !gate.IsActive(1), "a superseded attempt's late callback is ignored");
            Check(gate.TryClaim(2), "the replacement can claim its own recovery");
            gate.Retire(2);
            gate.Activate(3);
            Check(!gate.TryClaim(2), "a retired attempt can never claim");
            Check(!gate.TryClaim(99), "an attempt that was never active cannot claim");
            Check(gate.WasClaimed(1) && gate.Claims == 2, "claims stay recorded for the loop that decides after retirement");
        }

        // 15. A recovery-directed stop is never a user stop, even when forced.
        {
            var (c, feed) = Playing();
            feed(20, null);
            c.Finish(new PlaybackEnd { AttemptId = 1, At = TimeSpan.FromSeconds(30), ExitCode = -1, RecoveryStop = true });
            Check(c.State == SustainedPlaybackHealth.RecoveryStopped, "a forced recovery stop is RecoveryStopped, not RuntimeFailure");
            var (c2, feed2) = Playing();
            feed2(20, null);
            c2.Finish(new PlaybackEnd { AttemptId = 1, At = TimeSpan.FromSeconds(30), ExitCode = 0, RecoveryStop = true, StopRequested = true });
            Check(c2.State == SustainedPlaybackHealth.RecoveryStopped && c2.State != SustainedPlaybackHealth.UserStopped,
                "a graceful recovery quit is RecoveryStopped, never UserStopped");
            Check(PlaybackClassifierIsTerminal(SustainedPlaybackHealth.RecoveryStopped), "RecoveryStopped is terminal");
            Check(!PlaybackRecoveryPolicy.IsTrigger(SustainedPlaybackHealth.RecoveryStopped, true), "a recovery stop can never trigger another recovery");
        }

        // 16. A replacement's classifier starts from nothing.
        {
            var (c, feed) = Playing();
            feed(20, null);
            var replacement = new PlaybackHealthClassifier(2);
            var s = replacement.Snapshot();
            Check(s.State == SustainedPlaybackHealth.Starting && s.ResumePosition is null && !s.PlaybackProgressed && s.SamplesAccepted == 0,
                "replacement health and resume point start empty");
            replacement.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(50), Position = 49 });
            Check(replacement.Snapshot().ResumePosition is null, "the failed attempt's samples cannot donate a resume point");
        }

        // 19-22. Resume point: only confirmed forward progress, rewound, clamped.
        {
            var (c, feed) = Playing();
            Check(c.Snapshot().ResumePosition is null, "no progress, no resume point");
            feed(60, null);
            double? r = c.Snapshot().ResumePosition;
            Check(r is > 55 and < 59, $"resume is the last confirmed position minus the rewind ({r})");
            // A seek forward to 500 that never confirms progress cannot become the resume point.
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(61), Position = 500, Duration = 600, Seeking = true });
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(62), Position = 500, Duration = 600 });
            Check(c.Snapshot().ResumePosition == r, "a seek target without confirmed progress is not a resume point");
            // A jump reported without a seek flag is a discontinuity, not progress.
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(63), Position = 590, Duration = 600 });
            Check(c.Snapshot().ResumePosition == r, "a discontinuity cannot create a bogus resume point");
            // Progress from the new place does confirm it: the user really is there now.
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(64), Position = 591, Duration = 600 });
            Check(c.Snapshot().ResumePosition is > 587 and < 589, "confirmed progress after a seek moves the resume point");
            // Paused time, frozen polls and stale samples do not move it.
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(65), Position = 595, Duration = 600, Paused = true });
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(66), Responsive = false });
            c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(10), Position = 5 });
            Check(c.Snapshot().ResumePosition is > 587 and < 589, "pause, silence and non-monotonic samples leave the resume point alone");
            // Clamped against the duration.
            var (e, feedEnd) = Playing(duration: 30);
            feedEnd(40, null);
            Check(e.Snapshot().ResumePosition is double end && end <= 27, "resume is clamped inside the duration");
        }
        Check(PlaybackRecoveryText.StartArgument(2537.25) == "--start=2537.25", "resume uses mpv --start in invariant culture");
        Check(PlaybackRecoveryText.Position(2537) == "00:42:17", "product time format");
        Check(PlaybackRecoveryText.Describe(new(PlaybackRecoveryStep.UseStablePlayback, SustainedPlaybackHealth.Stalled, ""), 2537)
            == "Playback health: Playback stopped making progress\nRecovery: Stable playback · resumed near 00:42:17", "calm two-line recovery text");
        Check(PlaybackRecoveryText.Describe(new(PlaybackRecoveryStep.RetryOnPreviousNative, SustainedPlaybackHealth.Frozen, ""), null)
            .Contains("restarted from beginning"), "no resume point is reported truthfully");

        // Pause/buffer/seek never reach a trigger state, even for a long time.
        {
            var (c, feed) = Playing();
            feed(20, null);
            feed(120, i => i % 3 == 0 ? "pause" : i % 3 == 1 ? "buffer" : "seek");
            Check(!PlaybackRecoveryPolicy.IsTrigger(c.State, true) && c.Snapshot().StallEpisodes == 0,
                "two minutes of pause, buffering and seeking never produce a recovery trigger");
        }
        Check(PlaybackTransitionRecovery.PowerChanged(
            new(10_000, 10_000, 1, 0), new(14_000, 11_000, 1, 0)),
            "three seconds asleep is a power transition");
        Check(!PlaybackTransitionRecovery.PowerChanged(
            new(10_000, 10_000, 1, 0), new(14_000, 14_000, 1, 0)),
            "ordinary running time is not a sleep transition");
        Check(PlaybackTransitionRecovery.PowerChanged(
            new(10_000, 10_000, 1, 0), new(10_500, 10_500, 0, 0)),
            "AC to battery is a power transition");
        Check(PlaybackTransitionRecovery.Classify("DXGI_ERROR_DEVICE_REMOVED", false) == PlaybackTransitionFailure.GraphicsDeviceLost,
            "an explicit device removal is classified");
        Check(PlaybackTransitionRecovery.Classify("ordinary media error", false) == PlaybackTransitionFailure.None,
            "an unrelated playback error is not classified as device loss");
        return count;
    }

    private static bool PlaybackClassifierIsTerminal(SustainedPlaybackHealth s) => PlaybackHealthClassifier.IsTerminal(s);

    /// <summary>A classifier fed one-second samples of steady playback.</summary>
    private static (PlaybackHealthClassifier, Action<int, Func<int, string>?>) Playing(double duration = 600)
    {
        var c = new PlaybackHealthClassifier(1);
        double t = 0, pos = 0;
        return (c, (seconds, mode) =>
        {
            for (int i = 0; i < seconds; i++)
            {
                string m = mode?.Invoke(i) ?? "play";
                t += 1;
                if (m == "play") pos = Math.Min(duration, pos + 1);
                c.Observe(new PlaybackHealthSample { AttemptId = 1, At = TimeSpan.FromSeconds(t), Position = pos, Duration = duration,
                    Paused = m == "pause", PausedForCache = m == "buffer", Seeking = m == "seek", EofReached = false });
            }
        });
    }
}
