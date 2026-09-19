using AdaptiveMedia;

/// <summary>Sustained playback health, driven with synthetic samples on a fake
/// monotonic clock. Nothing here launches a player.</summary>
internal static class PlaybackHealthTests
{
    private const long Attempt = 7;

    /// <summary>A scripted player: one sample per second, position advancing at
    /// real time unless told otherwise, cumulative counters it can bump.</summary>
    private sealed class Player(PlaybackHealthClassifier classifier, long attempt = Attempt)
    {
        public double Seconds, Position, Duration = 600, Fps = 23.976;
        public long Output, Decoder, Mistimed, Delayed;
        public bool Paused, Buffering, Seeking, Eof, Responsive = true, NoCounters;
        public double Speed = 1;
        public readonly List<PlaybackHealthTransition> Seen = [];

        public PlaybackHealthSample Next(double step = 1)
        {
            Seconds += step;
            if (!Paused && !Buffering && !Seeking && !Eof) Position += step * Speed;
            return new PlaybackHealthSample
            {
                AttemptId = attempt, At = TimeSpan.FromSeconds(Seconds), Responsive = Responsive,
                Position = Responsive ? Position : null, Duration = Responsive ? Duration : null,
                Paused = Responsive ? Paused : null, PausedForCache = Responsive ? Buffering : null,
                Seeking = Responsive ? Seeking : null, EofReached = Responsive ? Eof : null,
                FrameRate = Responsive ? Fps : null,
                OutputDrops = Responsive && !NoCounters ? Output : null, DecoderDrops = Responsive && !NoCounters ? Decoder : null,
                MistimedFrames = Responsive && !NoCounters ? Mistimed : null, DelayedFrames = Responsive && !NoCounters ? Delayed : null,
            };
        }

        public SustainedPlaybackHealth Run(int seconds, Action<int>? each = null)
        {
            for (int i = 0; i < seconds; i++)
            {
                each?.Invoke(i);
                if (classifier.Observe(Next()) is { } t) Seen.Add(t);
            }
            return classifier.State;
        }

        public SustainedPlaybackHealth End(int exit = 0, bool stop = false, string? reason = null, bool started = true)
        {
            classifier.Finish(new PlaybackEnd { AttemptId = attempt, At = TimeSpan.FromSeconds(Seconds + 0.5),
                ExitCode = exit, StopRequested = stop, EndFileReason = reason, ProcessStarted = started });
            return classifier.State;
        }
    }

    private static (PlaybackHealthClassifier C, Player P) Fresh(PlaybackHealthPolicy? policy = null)
    {
        var c = new PlaybackHealthClassifier(Attempt, policy);
        return (c, new Player(c));
    }

    private static bool Ever(Player p, SustainedPlaybackHealth s) => p.Seen.Any(t => t.To == s);

    public static int Run(Action<bool, string> check)
    {
        int before = 0;
        void Check(bool v, string m) { before++; check(v, "SUSTAINED HEALTH: " + m); }

        // 1. Healthy continuous progress stays Healthy, with no flapping.
        {
            var (c, p) = Fresh();
            Check(c.State == SustainedPlaybackHealth.Starting, "starts in Starting, not Healthy");
            Check(p.Run(120) == SustainedPlaybackHealth.Healthy, "continuous progress is Healthy");
            Check(p.Seen.Count == 1, "exactly one transition (Starting -> Healthy) across two minutes");
            Check(c.Snapshot().WindowsEvaluated >= 20, "clean windows were really evaluated");
        }

        // 2. One isolated output drop is not degradation — nor even pressure.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Run(60, i => { if (i == 10) p.Output++; });
            Check(c.State == SustainedPlaybackHealth.Healthy, "one output drop leaves playback Healthy");
            Check(!Ever(p, SustainedPlaybackHealth.TransientPressure) && !Ever(p, SustainedPlaybackHealth.SustainedDegradation),
                "one output drop never produces pressure or degradation");
            Check(c.Snapshot().OutputDrops == 1, "the drop is still recorded");
        }

        // Calibration shape from the real 240 Hz run: one output drop that stays
        // at 1, a modest mistimed/delayed rise, decoder drops 0.
        {
            var (c, p) = Fresh();
            p.Run(30);
            p.Run(120, i => { if (i == 5) p.Output = 1; if (i is > 20 and < 60 && i % 7 == 0) { p.Mistimed++; p.Delayed++; } });
            Check(c.State == SustainedPlaybackHealth.Healthy, "calibration run: modest mistiming + a single drop stays Healthy");
            Check(!Ever(p, SustainedPlaybackHealth.SustainedDegradation), "calibration run never degrades");
        }

        // 3. A short burst becomes TransientPressure and recovers.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Run(5, _ => { p.Output += 2; p.Mistimed += 3; });
            Check(c.State == SustainedPlaybackHealth.TransientPressure, "a burst becomes TransientPressure");
            p.Run(4);
            Check(c.State == SustainedPlaybackHealth.TransientPressure, "one clean window is not enough to clear pressure (hysteresis)");
            p.Run(30);
            Check(c.State == SustainedPlaybackHealth.Healthy, "pressure clears after clean windows");
            Check(!Ever(p, SustainedPlaybackHealth.SustainedDegradation), "a single burst never escalates to degradation");
            Check(c.Snapshot().PressureEpisodes == 1, "one pressure episode recorded");
        }

        // 4. Continuously rising pressure becomes SustainedDegradation.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Run(40, _ => { p.Output += 2; p.Decoder += 1; });
            Check(c.State == SustainedPlaybackHealth.SustainedDegradation, "continuous drops become SustainedDegradation");
            Check(Ever(p, SustainedPlaybackHealth.TransientPressure), "degradation is reached through pressure, not in one step");
            // Three pressured windows, the first of which may be partial.
            double secondsToDegrade = p.Seen.First(t => t.To == SustainedPlaybackHealth.SustainedDegradation).At.TotalSeconds - 20;
            Check(secondsToDegrade > 2 * 5, "degradation needs several windows of evidence");
            p.Run(10);
            Check(c.State == SustainedPlaybackHealth.SustainedDegradation, "two clean windows do not clear degradation");
            p.Run(10);
            Check(c.State == SustainedPlaybackHealth.Healthy, "sustained clean evidence clears degradation");
        }

        // Timing-only pressure (mistimed/delayed) is also seen, proportionally.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Run(40, _ => { p.Mistimed += 4; p.Delayed += 2; });
            Check(c.State == SustainedPlaybackHealth.SustainedDegradation, "sustained mistiming alone degrades");
        }

        // 5. No progress while responsive and expected to play becomes Stalled —
        // only after the intended interval.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Speed = 0;
            p.Run(5);
            Check(c.State == SustainedPlaybackHealth.Healthy, "a few seconds without progress is not yet a stall");
            p.Run(2);
            Check(c.State == SustainedPlaybackHealth.Stalled, "no progress past StallAfter is Stalled");
            var stalledAt = p.Seen.Last().At.TotalSeconds;
            Check(stalledAt - 20 >= 6, "stall fired no earlier than the policy interval");
            p.Speed = 1;
            p.Run(2);
            Check(c.State == SustainedPlaybackHealth.Stalled, "a moment of progress does not clear a stall (hysteresis)");
            p.Run(3);
            Check(c.State == SustainedPlaybackHealth.Healthy, "sustained progress clears a stall");
            Check(c.Snapshot().StallEpisodes == 1 && c.Snapshot().WorstCondition == SustainedPlaybackHealth.Stalled,
                "the stall stays on record after recovery");
        }

        // A single missed poll cannot produce a stall.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Speed = 0; p.Run(1); p.Speed = 1;
            p.Run(30);
            Check(!Ever(p, SustainedPlaybackHealth.Stalled), "one frozen poll is not a stall");
        }

        // Startup settling is not a stall.
        {
            var (c, p) = Fresh();
            p.Speed = 0;
            p.Run(20);
            Check(c.State == SustainedPlaybackHealth.Starting, "startup without progress is Starting, not Stalled");
            p.Speed = 1;
            p.Run(5);
            Check(c.State == SustainedPlaybackHealth.Healthy, "startup that begins progressing becomes Healthy");
        }

        // 6. A user pause never becomes a stall.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Paused = true;
            p.Run(300);
            Check(c.State == SustainedPlaybackHealth.Healthy && !Ever(p, SustainedPlaybackHealth.Stalled), "a five-minute pause is not a stall");
            Check(c.Snapshot().Activity == PlaybackActivity.Paused, "pause is reported as activity");
            p.Paused = false;
            p.Run(30);
            Check(c.State == SustainedPlaybackHealth.Healthy, "resume after pause stays Healthy");
        }

        // 7. Cache buffering never becomes a stall.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Buffering = true;
            p.Run(60);
            Check(!Ever(p, SustainedPlaybackHealth.Stalled), "buffering is not a stall");
            Check(c.Snapshot().Activity == PlaybackActivity.Buffering, "buffering is reported as activity");
            p.Buffering = false;
            p.Run(10);
            Check(c.State == SustainedPlaybackHealth.Healthy, "playback after buffering is Healthy");
        }

        // 8. A seek does not create a false stall, and counters reset across it are not deltas.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Seeking = true; p.Run(3); p.Seeking = false;
            p.Position = 10; // Backwards seek.
            p.Run(10);
            p.Position = 500; // Forward jump reported without a seeking flag.
            p.Run(10);
            Check(!Ever(p, SustainedPlaybackHealth.Stalled) && c.State == SustainedPlaybackHealth.Healthy, "seeks do not create stalls");
            Check(c.Snapshot().Discontinuities >= 2, "both jumps were recognised as discontinuities");
            p.Output = 0; p.Decoder = 0; // Player reset its counters (new file).
            p.Run(1); p.Output = 40; p.Run(1); p.Output = 0; p.Run(20);
            Check(c.Snapshot().OutputDrops == 40, "a counter reset is rebased, never read as a negative or double delta");
        }

        // A seek landing between polls with counters bumped by the seek must not score.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Position = 5; p.Output += 30; p.Decoder += 30;
            p.Run(30);
            Check(!Ever(p, SustainedPlaybackHealth.TransientPressure), "drops that coincide with a discontinuity are rebased, not scored");
        }

        // 9. Normal EOF.
        {
            var (c, p) = Fresh();
            p.Duration = 30; p.Run(29);
            Check(p.End(0) == SustainedPlaybackHealth.EndedNormally, "clean exit at the end is EndedNormally");
            var (c2, p2) = Fresh();
            p2.Run(10);
            Check(p2.End(0, reason: "eof") == SustainedPlaybackHealth.EndedNormally, "an eof end-file event is EndedNormally");
            // Real mpv shape: the last poll before exit at EOF goes unanswered.
            var (c1, p1) = Fresh();
            p1.Duration = 30; p1.Run(29); p1.Responsive = false; p1.Run(1);
            Check(p1.End(0) == SustainedPlaybackHealth.EndedNormally, "EOF is judged from the last answered sample, not a shutdown poll");
            var (c3, p3) = Fresh();
            p3.Duration = 30; p3.Run(28); p3.Eof = true; p3.Run(3);
            Check(!Ever(p3, SustainedPlaybackHealth.Stalled), "sitting at EOF is not a stall");
            Check(p3.End(0) == SustainedPlaybackHealth.EndedNormally, "eof-reached is EndedNormally");
        }

        // 10. User stop.
        {
            var (c, p) = Fresh();
            p.Run(30);
            Check(p.End(0, stop: true) == SustainedPlaybackHealth.UserStopped, "an application stop is UserStopped");
            var (c2, p2) = Fresh();
            p2.Run(30);
            Check(p2.End(0) == SustainedPlaybackHealth.UserStopped, "closing the player mid-file is UserStopped, not EndedNormally");
            var (c3, p3) = Fresh();
            p3.Run(30);
            Check(p3.End(unchecked((int)0xC0000005), stop: true) == SustainedPlaybackHealth.UserStopped, "a requested stop that exits untidily is still UserStopped");
            var (c4, p4) = Fresh();
            p4.Run(20); p4.Speed = 0; p4.Run(10);
            Check(p4.End(0, reason: "quit") == SustainedPlaybackHealth.UserStopped &&
                  c4.Snapshot().WorstCondition == SustainedPlaybackHealth.Stalled,
                "a user stop after a stall is UserStopped and still reports the stall");
        }

        // 11. Source failure stays distinct from runtime failure.
        {
            var (c, p) = Fresh(); p.Run(5);
            Check(p.End(2) == SustainedPlaybackHealth.SourceFailure, "exit 2 is SourceFailure");
            var (c2, p2) = Fresh(); p2.Run(5);
            Check(p2.End(0, reason: "error") == SustainedPlaybackHealth.SourceFailure, "an error end-file is SourceFailure");
            var (c3, p3) = Fresh(); p3.Run(30);
            Check(p3.End(1) == SustainedPlaybackHealth.RuntimeFailure, "exit 1 after playback is RuntimeFailure");
            var (c4, p4) = Fresh(); p4.Run(30);
            Check(p4.End(unchecked((int)0xC0000005)) == SustainedPlaybackHealth.RuntimeFailure, "an access violation is RuntimeFailure");
            var (c5, p5) = Fresh();
            Check(p5.End(-1, started: false) == SustainedPlaybackHealth.RuntimeFailure, "a player that never started is RuntimeFailure");
        }

        // 12. IPC death with a live process is Frozen after the interval; process
        // death after it is classified by exit, keeping the freeze on record.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Responsive = false;
            p.Run(5);
            Check(c.State == SustainedPlaybackHealth.Healthy, "a few unanswered polls are not a freeze");
            p.Run(4);
            Check(c.State == SustainedPlaybackHealth.Frozen, "unanswered past FrozenAfter is Frozen");
            Check(p.End(unchecked((int)0xC0000409)) == SustainedPlaybackHealth.RuntimeFailure &&
                  c.Snapshot().WorstCondition == SustainedPlaybackHealth.Frozen,
                "a frozen player that then crashes is RuntimeFailure, freeze retained");
            var (c2, p2) = Fresh();
            p2.Run(20); p2.Responsive = false; p2.Run(10); p2.Responsive = true;
            p2.Run(1);
            Check(c2.State == SustainedPlaybackHealth.Frozen, "one answered poll does not clear a freeze while playing");
            p2.Run(5);
            Check(c2.State == SustainedPlaybackHealth.Healthy, "answering and progressing clears the freeze");
            Check(!Ever(p2, SustainedPlaybackHealth.TransientPressure), "the unanswered gap is not read as a counter delta");
            var (c3, p3) = Fresh();
            p3.Responsive = false; p3.Run(20);
            Check(c3.State == SustainedPlaybackHealth.Starting, "no answer before playback started is not a sustained-health freeze");
        }

        // 13. Stale samples from an earlier attempt cannot affect a new attempt.
        {
            var old = new PlaybackHealthClassifier(1);
            var oldPlayer = new Player(old, 1);
            oldPlayer.Run(20); oldPlayer.Speed = 0; oldPlayer.Run(10);
            Check(old.State == SustainedPlaybackHealth.Stalled, "setup: earlier attempt stalled");
            var fresh = new PlaybackHealthClassifier(2);
            foreach (var t in Enumerable.Range(0, 40)) fresh.Observe(oldPlayer.Next());
            fresh.Finish(new PlaybackEnd { AttemptId = 1, At = TimeSpan.FromSeconds(100), ExitCode = 2 });
            var snap = fresh.Snapshot();
            Check(snap.State == SustainedPlaybackHealth.Starting && snap.SamplesAccepted == 0 && snap.SamplesRejected == 41,
                "foreign samples and a foreign end are rejected wholesale");
            Check(snap.WorstCondition == SustainedPlaybackHealth.Starting && snap.StallEpisodes == 0, "no health is donated");
            var newPlayer = new Player(fresh, 2);
            Check(newPlayer.Run(20) == SustainedPlaybackHealth.Healthy, "the new attempt is judged on its own samples");
            Check(newPlayer.End(0, stop: true) == SustainedPlaybackHealth.UserStopped, "the new attempt ends on its own end");
            fresh.Observe(newPlayer.Next());
            Check(fresh.Snapshot().SamplesRejected == 42, "samples after a terminal state are rejected");
        }

        // 14. Time going backwards or standing still is rejected conservatively.
        {
            var (c, p) = Fresh();
            p.Run(20);
            var sample = p.Next();
            c.Observe(sample);
            c.Observe(sample with { At = sample.At - TimeSpan.FromSeconds(5), Position = 0 });
            c.Observe(sample with { Position = sample.Position + 1000 });
            Check(c.Snapshot().SamplesRejected == 2 && c.State == SustainedPlaybackHealth.Healthy, "non-monotonic samples are rejected");
            p.Run(20);
            Check(c.State == SustainedPlaybackHealth.Healthy && c.Snapshot().Discontinuities == 0, "history is unpoisoned");
        }

        // 15. Transient load followed by healthy progress clears; absent counters are
        // absent evidence, not clean evidence.
        {
            var (c, p) = Fresh();
            p.Run(20);
            p.Run(5, _ => p.Output += 3);
            Check(c.State == SustainedPlaybackHealth.TransientPressure, "load produces pressure");
            p.NoCounters = true;
            p.Run(60);
            Check(c.State == SustainedPlaybackHealth.TransientPressure, "windows without counters do not clear pressure");
            p.NoCounters = false;
            p.Run(15);
            Check(c.State == SustainedPlaybackHealth.Healthy, "real clean windows clear it");
        }

        // Mutation checks: loosening one hysteresis knob changes the verdict, so the
        // tests above are exercising the knobs rather than passing by accident.
        {
            var (c, p) = Fresh(new PlaybackHealthPolicy { MinimumDropsForPressure = 1, DropFractionForPressure = 0 });
            p.Run(20); p.Run(10, i => { if (i == 3) p.Output++; });
            Check(c.State == SustainedPlaybackHealth.TransientPressure, "MUTATION: without the minimum, one drop would be pressure");
            var (c2, p2) = Fresh(new PlaybackHealthPolicy { StallAfter = TimeSpan.FromSeconds(1) });
            p2.Run(20); p2.Speed = 0; p2.Run(1); p2.Speed = 1; p2.Run(1);
            Check(Ever(p2, SustainedPlaybackHealth.Stalled), "MUTATION: a one-second stall window would fire on one missed poll");
            var (c3, p3) = Fresh(new PlaybackHealthPolicy { DegradationWindows = 1 });
            p3.Run(20); p3.Run(5, _ => p3.Output += 3);
            Check(c3.State == SustainedPlaybackHealth.SustainedDegradation, "MUTATION: one-window degradation would skip pressure");
            // Same script, default versus mutant: the default holds pressure through
            // the first clean window; the mutant clears on it.
            SustainedPlaybackHealth AfterOneCleanWindow(PlaybackHealthPolicy? policy)
            {
                var (cx, px) = Fresh(policy);
                px.Run(20); px.Run(5, _ => px.Output += 3); px.Run(7);
                return cx.State;
            }
            Check(AfterOneCleanWindow(null) == SustainedPlaybackHealth.TransientPressure &&
                  AfterOneCleanWindow(new PlaybackHealthPolicy { CleanWindowsToClearPressure = 1 }) == SustainedPlaybackHealth.Healthy,
                "MUTATION: one clean window would clear pressure immediately");
        }

        // Text is concise and one line.
        foreach (var s in Enum.GetValues<SustainedPlaybackHealth>())
            Check(PlaybackHealthText.Describe(s).StartsWith("Playback health: ") && !PlaybackHealthText.Describe(s).Contains('\n'), "one-line health text");
        Check(PlaybackHealthText.Describe(SustainedPlaybackHealth.Stalled) == "Playback health: Playback stopped making progress", "stall wording");

        // Bounded history.
        {
            var (c, p) = Fresh(new PlaybackHealthPolicy { TransitionHistory = 4 });
            p.Run(20);
            for (int k = 0; k < 10; k++) { p.Run(5, _ => p.Output += 3); p.Run(15); }
            Check(c.Snapshot().Transitions.Count == 4, "transition history is bounded");
        }
        return before;
    }
}
