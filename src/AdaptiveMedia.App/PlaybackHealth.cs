namespace AdaptiveMedia;

/// <summary>Sustained playback health: whether playback that has started is still
/// healthy.
///
/// This is deliberately separate from <see cref="NativeDvHealth"/>. That enum
/// answers a startup question for one native runtime generation ("did this
/// runtime reach useful playback?") and feeds rollback. This answers a temporal
/// question for any player session ("is it still presenting, and how well?")
/// and, in this version, feeds only reporting. Neither is derived from the
/// other.
///
/// Stable diagnostics identifiers; append rather than renumber.</summary>
public enum SustainedPlaybackHealth
{
    /// <summary>No forward progress has been observed yet. Startup is the
    /// startup-health model's question, so nothing here is judged.</summary>
    Starting = 0,
    /// <summary>Playback is advancing and presentation counters are quiet.</summary>
    Healthy = 1,
    /// <summary>A recent window showed dropped or mistimed frames. Playback is
    /// still advancing; this is not a failure and can clear on its own.</summary>
    TransientPressure = 2,
    /// <summary>Presentation pressure repeated across several windows.</summary>
    SustainedDegradation = 3,
    /// <summary>The player answers, is expected to play, and is neither paused,
    /// buffering, seeking nor at its end, yet position stopped advancing.</summary>
    Stalled = 4,
    /// <summary>The player process is alive but has stopped answering its
    /// diagnostics connection.</summary>
    Frozen = 5,
    /// <summary>Playback reached the end of the media.</summary>
    EndedNormally = 6,
    /// <summary>The user or the application stopped playback.</summary>
    UserStopped = 7,
    /// <summary>The player reported that it could not play the media.</summary>
    SourceFailure = 8,
    /// <summary>The player itself failed: an abnormal exit or a crash.</summary>
    RuntimeFailure = 9,
    /// <summary>Playback ended without enough evidence to say how.</summary>
    Unknown = 10,
    /// <summary>DemiMedia itself stopped the player to recover from a hard
    /// failure. Never a user stop; the failure that caused it is the attempt's
    /// worst condition.</summary>
    RecoveryStopped = 11,
}

/// <summary>What the player was doing at the latest sample. Context, not health:
/// a paused player is not unhealthy, it is paused.</summary>
public enum PlaybackActivity
{
    Unknown = 0,
    Playing = 1,
    Paused = 2,
    Buffering = 3,
    Seeking = 4,
    AtEnd = 5,
    Unresponsive = 6,
}

/// <summary>Every threshold the classifier uses, in one place. The defaults are
/// deliberately expressed as durations and fractions of frames, not as counts
/// tuned to one film, GPU or refresh rate.</summary>
public sealed record PlaybackHealthPolicy
{
    /// <summary>How often the application samples the player.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Length of one pressure evaluation window, in playing time.</summary>
    public TimeSpan WindowLength { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Position must advance by at least this much to count as progress.</summary>
    public double ProgressEpsilonSeconds { get; init; } = 0.05;
    /// <summary>No progress for this long while expected to play is a stall.
    /// Several samples, so one missed poll can never produce it.</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>Before the first progress, startup settling is allowed this long
    /// before a lack of progress is called a stall.</summary>
    public TimeSpan StartupStallAfter { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Unanswered diagnostics for this long while the process lives is a freeze.</summary>
    public TimeSpan FrozenAfter { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>A window is pressured when dropped frames (decoder plus output)
    /// reach this fraction of the frames it should have shown…</summary>
    public double DropFractionForPressure { get; init; } = 0.02;
    /// <summary>…and at least this many. One isolated drop is never pressure.</summary>
    public long MinimumDropsForPressure { get; init; } = 2;
    /// <summary>Mistimed or delayed frames at this fraction of expected frames
    /// also make a window pressured…</summary>
    public double TimingFractionForPressure { get; init; } = 0.10;
    /// <summary>…with at least this many events.</summary>
    public long MinimumTimingEventsForPressure { get; init; } = 3;
    /// <summary>Assumed video frame rate when the player does not report one.</summary>
    public double FallbackFrameRate { get; init; } = 24;
    /// <summary>Degradation needs this many pressured windows…</summary>
    public int DegradationWindows { get; init; } = 3;
    /// <summary>…among the most recent this many evaluated windows.</summary>
    public int DegradationLookback { get; init; } = 4;
    /// <summary>Consecutive clean windows that clear transient pressure.</summary>
    public int CleanWindowsToClearPressure { get; init; } = 2;
    /// <summary>Consecutive clean windows that clear sustained degradation.</summary>
    public int CleanWindowsToClearDegradation { get; init; } = 3;
    /// <summary>Continuous progress needed before a stall or freeze is cleared.</summary>
    public TimeSpan ProgressToClearStall { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>A last observed position this close to the duration counts as the end.</summary>
    public double EndToleranceSeconds { get; init; } = 2.5;
    /// <summary>A recovery resumes this far before the last confirmed position:
    /// replaying a little is better than skipping anything.</summary>
    public double ResumeRewindSeconds { get; init; } = 3;
    /// <summary>How long a player being stopped for recovery gets to exit on its
    /// own before only that exact process is terminated.</summary>
    public TimeSpan RecoveryStopGrace { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Largest believable forward jump per second of elapsed time; a
    /// larger one is a discontinuity (seek or file change), not progress.</summary>
    public double MaximumPlausibleSpeed { get; init; } = 16;
    /// <summary>Transitions retained for diagnostics.</summary>
    public int TransitionHistory { get; init; } = 16;

    public static PlaybackHealthPolicy Default { get; } = new();
}

/// <summary>One observation of a running player, as read. Every field is
/// nullable because the player may not report it; null is "not observed", never
/// zero or false.</summary>
public sealed record PlaybackHealthSample
{
    /// <summary>The attempt this sample was taken from.</summary>
    public required long AttemptId { get; init; }
    /// <summary>Monotonic time since the attempt began.</summary>
    public required TimeSpan At { get; init; }
    /// <summary>The diagnostics connection answered this poll.</summary>
    public bool Responsive { get; init; } = true;
    public double? Position { get; init; }
    public double? Duration { get; init; }
    public bool? Paused { get; init; }
    public bool? PausedForCache { get; init; }
    public bool? Seeking { get; init; }
    public bool? EofReached { get; init; }
    public double? FrameRate { get; init; }
    public long? OutputDrops { get; init; }
    public long? DecoderDrops { get; init; }
    public long? MistimedFrames { get; init; }
    public long? DelayedFrames { get; init; }
}

/// <summary>How the player process ended, as far as it was observed.</summary>
public sealed record PlaybackEnd
{
    public required long AttemptId { get; init; }
    public required TimeSpan At { get; init; }
    public bool ProcessStarted { get; init; } = true;
    public int ExitCode { get; init; }
    /// <summary>This application asked the player to stop.</summary>
    public bool StopRequested { get; init; }
    /// <summary>The reason from the player's last end-file event, when one was
    /// received (eof, stop, quit, error, redirect).</summary>
    public string? EndFileReason { get; init; }
    /// <summary>DemiMedia stopped this player to recover from a hard failure.
    /// Decided before everything else, and never read as a user stop.</summary>
    public bool RecoveryStop { get; init; }
}

public sealed record PlaybackHealthTransition(TimeSpan At, SustainedPlaybackHealth From,
    SustainedPlaybackHealth To, string Reason);

/// <summary>The current sustained-health result for one attempt.</summary>
public sealed record PlaybackHealthSnapshot
{
    public required long AttemptId { get; init; }
    public required SustainedPlaybackHealth State { get; init; }
    public PlaybackActivity Activity { get; init; }
    public string Explanation { get; init; } = "";
    /// <summary>The worst non-terminal condition this attempt was ever in, so a
    /// user stop after a stall still reports that the stall happened.</summary>
    public SustainedPlaybackHealth WorstCondition { get; init; }
    public bool PlaybackProgressed { get; init; }
    public int PressureEpisodes { get; init; }
    public int DegradationEpisodes { get; init; }
    public int StallEpisodes { get; init; }
    public int FreezeEpisodes { get; init; }
    public long OutputDrops { get; init; }
    public long DecoderDrops { get; init; }
    public long TimingEvents { get; init; }
    public int WindowsEvaluated { get; init; }
    public int SamplesAccepted { get; init; }
    public int SamplesRejected { get; init; }
    public int Discontinuities { get; init; }
    /// <summary>Where a replacement attempt may resume: the last position this
    /// attempt confirmed by forward progress, rewound slightly and clamped to the
    /// duration. Null when no progress was ever confirmed.</summary>
    public double? ResumePosition { get; init; }
    public IReadOnlyList<PlaybackHealthTransition> Transitions { get; init; } = [];
    public bool IsTerminal => PlaybackHealthClassifier.IsTerminal(State);
}

/// <summary>Deterministic temporal classifier for one playback attempt.
///
/// sample → normalise → compare with the previous sample → update a bounded
/// window → classify → record a transition only when the evidence changes the
/// state. No clocks are read here and nothing is launched; time arrives with each
/// sample, so every rule is testable without a player.
///
/// A classifier belongs to exactly one attempt. Samples or an end from any other
/// attempt are rejected, so no attempt can donate health to another.</summary>
public sealed class PlaybackHealthClassifier
{
    private readonly PlaybackHealthPolicy _policy;
    private readonly Queue<PlaybackHealthTransition> _transitions = new();
    private readonly Queue<bool> _recentWindows = new();
    private PlaybackHealthTransition? _latest;
    private long _serial;

    private SustainedPlaybackHealth _state = SustainedPlaybackHealth.Starting;
    private SustainedPlaybackHealth _worst = SustainedPlaybackHealth.Healthy;
    private PlaybackActivity _activity;
    private string _explanation = "Waiting for playback to start.";
    /// <summary>The condition a stall or freeze interrupted, restored when it clears.</summary>
    private SustainedPlaybackHealth _presentationState = SustainedPlaybackHealth.Healthy;

    private PlaybackHealthSample? _last;
    /// <summary>The latest sample the player answered. A player shutting down at
    /// the end of the media stops answering first, so the end is judged from here.</summary>
    private PlaybackHealthSample? _lastAnswered;
    private TimeSpan? _firstPlayingAt;
    private double? _anchorPosition;
    private TimeSpan _lastProgressAt;
    private TimeSpan? _progressRunSince;
    private TimeSpan? _unresponsiveSince;
    private bool _progressed;

    // Counter baselines. Null means "rebase on the next reading".
    private long? _baseOutput, _baseDecoder, _baseMistimed, _baseDelayed;

    // The window being accumulated.
    private TimeSpan _windowPlaying;
    private double _windowExpectedFrames;
    private long _windowDrops, _windowTiming;
    private bool _windowHasCounters;
    private int _cleanStreak;

    private int _pressureEpisodes, _degradationEpisodes, _stallEpisodes, _freezeEpisodes;
    private long _outputDrops, _decoderDrops, _timingEvents;
    private int _windows, _accepted, _rejected, _discontinuities;
    private double? _confirmedPosition;

    public PlaybackHealthClassifier(long attemptId, PlaybackHealthPolicy? policy = null)
    {
        AttemptId = attemptId;
        _policy = policy ?? PlaybackHealthPolicy.Default;
    }

    public long AttemptId { get; }
    public SustainedPlaybackHealth State => _state;

    public static bool IsTerminal(SustainedPlaybackHealth state) => state is SustainedPlaybackHealth.EndedNormally or
        SustainedPlaybackHealth.UserStopped or SustainedPlaybackHealth.SourceFailure or
        SustainedPlaybackHealth.RuntimeFailure or SustainedPlaybackHealth.Unknown or SustainedPlaybackHealth.RecoveryStopped;

    /// <summary>Feed one sample. Returns the latest transition it caused, if any.</summary>
    public PlaybackHealthTransition? Observe(PlaybackHealthSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        long serial = _serial;
        ObserveCore(sample);
        return _serial != serial ? _latest : null;
    }

    private PlaybackHealthTransition? ObserveCore(PlaybackHealthSample sample)
    {
        // Another attempt's sample, a sample after the attempt ended, or one that
        // does not move monotonic time forward is not evidence about this attempt.
        if (sample.AttemptId != AttemptId || IsTerminal(_state) || (_last is not null && sample.At <= _last.At))
        {
            _rejected++;
            return null;
        }
        _accepted++;
        var previous = _last;
        _last = sample;

        if (!sample.Responsive)
        {
            _activity = PlaybackActivity.Unresponsive;
            _unresponsiveSince ??= sample.At;
            // Nothing read this poll: counters and position must be rebased once
            // the player answers again, or the gap would read as one huge delta.
            Rebase();
            _anchorPosition = null;
            _progressRunSince = null;
            if (_progressed && _state != SustainedPlaybackHealth.Frozen &&
                sample.At - _unresponsiveSince.Value >= _policy.FrozenAfter)
            {
                _freezeEpisodes++;
                return Move(SustainedPlaybackHealth.Frozen, sample.At,
                    $"The player stopped answering for {(sample.At - _unresponsiveSince.Value).TotalSeconds:0} s while its process was still running.");
            }
            return null;
        }
        _unresponsiveSince = null;
        _lastAnswered = sample;

        _activity = ActivityOf(sample);
        // Answering again ends a freeze only when nothing more is expected of the
        // player; a playing one must also show progress, exactly like a stall.
        if (_state == SustainedPlaybackHealth.Frozen && _activity != PlaybackActivity.Playing)
            Move(_presentationState, sample.At, "The player is answering again.");
        bool discontinuity = IsDiscontinuity(previous, sample);
        if (discontinuity) _discontinuities++;

        // Not expected to advance: pause, buffering, seeking and the end of the
        // media each suspend stall reasoning, and progress is re-anchored when
        // playing resumes so the suspended time is never counted as a stall.
        if (_activity != PlaybackActivity.Playing || discontinuity)
        {
            _anchorPosition = _activity == PlaybackActivity.Playing ? sample.Position : null;
            _lastProgressAt = sample.At;
            _progressRunSince = null;
            if (discontinuity || _activity == PlaybackActivity.Seeking) Rebase();
            else ReadCounters(sample, accumulate: false);
            if (_state == SustainedPlaybackHealth.Stalled && _activity is PlaybackActivity.Paused or PlaybackActivity.AtEnd)
                return Move(_presentationState, sample.At, "Playback is no longer expected to advance; the stall is no longer current.");
            return null;
        }

        _firstPlayingAt ??= sample.At;
        var transition = UpdateProgress(sample);
        if (transition is not null) return transition;
        return UpdatePressure(sample, previous);
    }

    /// <summary>Classify how the attempt ended. The end must belong to this attempt.</summary>
    public PlaybackHealthTransition? Finish(PlaybackEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (end.AttemptId != AttemptId || IsTerminal(_state)) { _rejected++; return null; }
        var (state, reason) = ClassifyEnd(end);
        return Move(state, end.At, reason);
    }

    public PlaybackHealthSnapshot Snapshot() => new()
    {
        AttemptId = AttemptId,
        State = _state,
        Activity = _activity,
        Explanation = _explanation,
        WorstCondition = _progressed ? _worst : SustainedPlaybackHealth.Starting,
        PlaybackProgressed = _progressed,
        PressureEpisodes = _pressureEpisodes,
        DegradationEpisodes = _degradationEpisodes,
        StallEpisodes = _stallEpisodes,
        FreezeEpisodes = _freezeEpisodes,
        OutputDrops = _outputDrops,
        DecoderDrops = _decoderDrops,
        TimingEvents = _timingEvents,
        WindowsEvaluated = _windows,
        SamplesAccepted = _accepted,
        SamplesRejected = _rejected,
        Discontinuities = _discontinuities,
        ResumePosition = ResumePosition(),
        Transitions = _transitions.ToArray(),
    };

    private (SustainedPlaybackHealth, string) ClassifyEnd(PlaybackEnd end)
    {
        if (!end.ProcessStarted)
            return (SustainedPlaybackHealth.RuntimeFailure, "The player could not be started.");
        // A recovery stop may end in a forced termination; that exit code says
        // nothing new, and it must never read as the user having stopped.
        if (end.RecoveryStop)
            return (SustainedPlaybackHealth.RecoveryStopped, "DemiMedia stopped the player to recover playback.");
        // A stop this application asked for is decided before the exit code: a
        // player told to quit may still exit untidily.
        if (end.StopRequested)
            return (SustainedPlaybackHealth.UserStopped, "Playback was stopped by the application.");
        if (MpvExit.MeansCrash(end.ExitCode))
            return (SustainedPlaybackHealth.RuntimeFailure, $"The player terminated abnormally ({end.ExitCode}).");
        if (MpvExit.MeansSourceNotPlayable(end.ExitCode) || end.EndFileReason == "error")
            return (SustainedPlaybackHealth.SourceFailure, "The player reported that it could not play this media.");
        if (MpvExit.MeansExternalStop(end.ExitCode))
            return (SustainedPlaybackHealth.UserStopped, "The player was stopped from outside.");
        if (end.ExitCode != 0)
            return (SustainedPlaybackHealth.RuntimeFailure, $"The player exited with an error ({end.ExitCode}).");
        // A clean exit is either the end of the media or someone closing the player.
        if (end.EndFileReason == "eof" || ReachedEnd())
            return (SustainedPlaybackHealth.EndedNormally, "Playback reached the end of the media.");
        if (end.EndFileReason is "quit" or "stop")
            return (SustainedPlaybackHealth.UserStopped, "The player was closed before the end of the media.");
        if (!_progressed || _lastAnswered is null)
            return (SustainedPlaybackHealth.Unknown, "Playback ended before enough was observed to say how.");
        return (SustainedPlaybackHealth.UserStopped, "The player was closed before the end of the media.");
    }

    /// <summary>Only a position confirmed by forward progress while playing can be
    /// a resume point: never a seek target, a jump, or a value read from another
    /// attempt (those samples are rejected before they get here).</summary>
    private double? ResumePosition()
    {
        if (_confirmedPosition is not double confirmed) return null;
        double resume = Math.Max(0, confirmed - _policy.ResumeRewindSeconds);
        if (_lastAnswered?.Duration is double d && d > 0) resume = Math.Min(resume, Math.Max(0, d - _policy.ResumeRewindSeconds));
        return resume;
    }

    private bool ReachedEnd()
    {
        if (_lastAnswered is null) return false;
        if (_lastAnswered.EofReached == true) return true;
        return _lastAnswered.Position is double p && _lastAnswered.Duration is double d && d > 0 &&
               d - p <= _policy.EndToleranceSeconds;
    }

    private static PlaybackActivity ActivityOf(PlaybackHealthSample s)
    {
        if (s.EofReached == true) return PlaybackActivity.AtEnd;
        if (s.Seeking == true) return PlaybackActivity.Seeking;
        if (s.Paused == true) return PlaybackActivity.Paused;
        if (s.PausedForCache == true) return PlaybackActivity.Buffering;
        return PlaybackActivity.Playing;
    }

    /// <summary>A position that moved backwards, or forwards faster than playback
    /// can, is a seek, a loop or a new file. It is re-anchored, never scored.</summary>
    private bool IsDiscontinuity(PlaybackHealthSample? previous, PlaybackHealthSample current)
    {
        if (previous?.Position is not double before || current.Position is not double now) return false;
        double elapsed = (current.At - previous.At).TotalSeconds;
        return now < before - _policy.ProgressEpsilonSeconds ||
               now - before > elapsed * _policy.MaximumPlausibleSpeed + 1;
    }

    private PlaybackHealthTransition? UpdateProgress(PlaybackHealthSample sample)
    {
        if (sample.Position is not double position) return null;
        if (_anchorPosition is null)
        {
            _anchorPosition = position;
            _lastProgressAt = sample.At;
            return null;
        }
        if (position - _anchorPosition.Value >= _policy.ProgressEpsilonSeconds)
        {
            _anchorPosition = position;
            _confirmedPosition = position;
            _lastProgressAt = sample.At;
            _progressRunSince ??= sample.At;
            if (!_progressed)
            {
                _progressed = true;
                return Move(SustainedPlaybackHealth.Healthy, sample.At, "Playback is advancing.");
            }
            if (_state is SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen &&
                sample.At - _progressRunSince.Value >= _policy.ProgressToClearStall)
                return Move(_presentationState, sample.At, "Playback is advancing again.");
            return null;
        }

        _progressRunSince = null;
        TimeSpan limit = _progressed ? _policy.StallAfter : _policy.StartupStallAfter;
        TimeSpan since = sample.At - (_progressed ? _lastProgressAt : Max(_lastProgressAt, _firstPlayingAt!.Value));
        if (_state != SustainedPlaybackHealth.Stalled && since >= limit)
        {
            _stallEpisodes++;
            return Move(SustainedPlaybackHealth.Stalled, sample.At,
                $"Playback stopped making progress for {since.TotalSeconds:0} s while it was expected to play.");
        }
        return null;
    }

    private PlaybackHealthTransition? UpdatePressure(PlaybackHealthSample sample, PlaybackHealthSample? previous)
    {
        bool counted = ReadCounters(sample, accumulate: true);
        if (!_progressed || previous is null) return null;
        TimeSpan elapsed = sample.At - previous.At;
        // A long gap means samples were missed; it is not playing time we saw.
        if (elapsed > _policy.SampleInterval * 4) return null;
        _windowPlaying += elapsed;
        _windowExpectedFrames += elapsed.TotalSeconds * (sample.FrameRate is > 0 and < 1000 ? sample.FrameRate.Value : _policy.FallbackFrameRate);
        _windowHasCounters |= counted;
        if (_windowPlaying < _policy.WindowLength) return null;
        return CloseWindow(sample.At);
    }

    private PlaybackHealthTransition? CloseWindow(TimeSpan at)
    {
        double frames = Math.Max(1, _windowExpectedFrames);
        bool? pressured = !_windowHasCounters ? null :
            (_windowDrops >= _policy.MinimumDropsForPressure && _windowDrops >= frames * _policy.DropFractionForPressure) ||
            (_windowTiming >= _policy.MinimumTimingEventsForPressure && _windowTiming >= frames * _policy.TimingFractionForPressure);
        long drops = _windowDrops, timing = _windowTiming;
        _windowPlaying = TimeSpan.Zero; _windowExpectedFrames = 0; _windowDrops = 0; _windowTiming = 0; _windowHasCounters = false;
        // A window with no counters is absent evidence: it neither raises pressure
        // nor counts towards clearing it.
        if (pressured is null) return null;
        _windows++;
        _recentWindows.Enqueue(pressured.Value);
        while (_recentWindows.Count > _policy.DegradationLookback) _recentWindows.Dequeue();
        _cleanStreak = pressured.Value ? 0 : _cleanStreak + 1;

        var next = _presentationState;
        string reason;
        if (pressured.Value)
        {
            int recent = _recentWindows.Count(x => x);
            if (recent >= _policy.DegradationWindows)
            {
                next = SustainedPlaybackHealth.SustainedDegradation;
                reason = $"Dropped or mistimed frames in {recent} of the last {_recentWindows.Count} windows ({drops} dropped, {timing} mistimed or delayed in the latest).";
            }
            else
            {
                next = _presentationState == SustainedPlaybackHealth.SustainedDegradation
                    ? SustainedPlaybackHealth.SustainedDegradation : SustainedPlaybackHealth.TransientPressure;
                reason = $"Brief presentation pressure: {drops} dropped and {timing} mistimed or delayed frames in the latest window.";
            }
        }
        else
        {
            reason = "Presentation has been clean for several windows.";
            if (_presentationState == SustainedPlaybackHealth.TransientPressure && _cleanStreak >= _policy.CleanWindowsToClearPressure)
                next = SustainedPlaybackHealth.Healthy;
            else if (_presentationState == SustainedPlaybackHealth.SustainedDegradation && _cleanStreak >= _policy.CleanWindowsToClearDegradation)
                next = SustainedPlaybackHealth.Healthy;
        }
        if (next == _presentationState) return null;
        if (next == SustainedPlaybackHealth.TransientPressure) _pressureEpisodes++;
        if (next == SustainedPlaybackHealth.SustainedDegradation) _degradationEpisodes++;
        _presentationState = next;
        // While stalled or frozen the presentation state is tracked but not shown;
        // it is what the attempt returns to when progress resumes.
        if (_state is SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen) return null;
        return Move(next, at, reason);
    }

    /// <summary>Read cumulative counters as deltas against the baseline. A counter
    /// that went down was reset by the player (a new file), so it is rebased
    /// rather than read as a negative delta.</summary>
    private bool ReadCounters(PlaybackHealthSample s, bool accumulate)
    {
        long output = Delta(ref _baseOutput, s.OutputDrops);
        long decoder = Delta(ref _baseDecoder, s.DecoderDrops);
        long mistimed = Delta(ref _baseMistimed, s.MistimedFrames);
        long delayed = Delta(ref _baseDelayed, s.DelayedFrames);
        bool any = s.OutputDrops.HasValue || s.DecoderDrops.HasValue || s.MistimedFrames.HasValue || s.DelayedFrames.HasValue;
        if (!accumulate || !_progressed) return any;
        _outputDrops += output; _decoderDrops += decoder; _timingEvents += mistimed + delayed;
        _windowDrops += output + decoder;
        _windowTiming += mistimed + delayed;
        return any;
    }

    private static long Delta(ref long? baseline, long? value)
    {
        if (value is not long v) return 0;
        long delta = baseline is long b && v >= b ? v - b : 0;
        baseline = v;
        return delta;
    }

    private void Rebase() => _baseOutput = _baseDecoder = _baseMistimed = _baseDelayed = null;

    private PlaybackHealthTransition? Move(SustainedPlaybackHealth to, TimeSpan at, string reason)
    {
        _explanation = reason;
        if (to == _state) return null;
        var transition = new PlaybackHealthTransition(at, _state, to, reason);
        if (!IsTerminal(to) && Severity(to) > Severity(_worst)) _worst = to;
        if (to is not (SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen) && !IsTerminal(to))
            _presentationState = to;
        _state = to;
        _transitions.Enqueue(transition);
        _latest = transition;
        _serial++;
        while (_transitions.Count > _policy.TransitionHistory) _transitions.Dequeue();
        return transition;
    }

    private static int Severity(SustainedPlaybackHealth s) => s switch
    {
        SustainedPlaybackHealth.TransientPressure => 1,
        SustainedPlaybackHealth.SustainedDegradation => 2,
        SustainedPlaybackHealth.Stalled => 3,
        SustainedPlaybackHealth.Frozen => 4,
        _ => 0,
    };

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>mpv's documented exit codes. The same meanings the native startup
/// evaluator uses; kept here so the general layer does not depend on the native
/// Dolby Vision lane.</summary>
public static class MpvExit
{
    public static bool MeansSourceNotPlayable(int code) => code is 2 or 3;
    public static bool MeansExternalStop(int code) => code == 4;
    public static bool MeansCrash(int code) => code < 0 || (uint)code >= 0xC0000000u;
}

public static class PlaybackHealthText
{
    /// <summary>One concise product-facing line. Per-poll telemetry belongs in
    /// diagnostics, not here.</summary>
    public static string Describe(SustainedPlaybackHealth state) => "Playback health: " + state switch
    {
        SustainedPlaybackHealth.Starting => "Starting",
        SustainedPlaybackHealth.Healthy => "Healthy",
        SustainedPlaybackHealth.TransientPressure => "Temporary presentation pressure",
        SustainedPlaybackHealth.SustainedDegradation => "Sustained presentation pressure",
        SustainedPlaybackHealth.Stalled => "Playback stopped making progress",
        SustainedPlaybackHealth.Frozen => "Player stopped responding",
        SustainedPlaybackHealth.EndedNormally => "Ended normally",
        SustainedPlaybackHealth.UserStopped => "Stopped by user",
        SustainedPlaybackHealth.SourceFailure => "Source could not be played",
        SustainedPlaybackHealth.RuntimeFailure => "Runtime failure",
        SustainedPlaybackHealth.RecoveryStopped => "Stopped for automatic recovery",
        _ => "Unknown",
    };
}
