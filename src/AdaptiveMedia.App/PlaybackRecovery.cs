using System.Globalization;

namespace AdaptiveMedia;

/// <summary>What kind of runtime a playback attempt ran on, as far as recovery
/// is concerned.</summary>
public enum PlaybackAttemptKind
{
    /// <summary>The ordinary stable player. V1 never relaunches it automatically.</summary>
    Stable = 0,
    /// <summary>The current native generation: may fall back to the retained one.</summary>
    NativeCurrent = 1,
    /// <summary>The retained previous native generation: may only fall back to stable.</summary>
    NativePrevious = 2,
}

/// <summary>What the recovery system does after a hard sustained failure.
/// Stable diagnostics identifiers; append rather than renumber.</summary>
public enum PlaybackRecoveryStep
{
    /// <summary>Not a recovery trigger; health is reported and nothing else happens.</summary>
    None = 0,
    /// <summary>Relaunch once on the retained previous native generation.</summary>
    RetryOnPreviousNative = 1,
    /// <summary>Relaunch on the established stable player.</summary>
    UseStablePlayback = 2,
    /// <summary>A hard failure with nowhere legitimate to go: reported, not relaunched.</summary>
    NoRecoveryAvailable = 3,
}

public sealed record PlaybackRecoveryDecision(PlaybackRecoveryStep Step, SustainedPlaybackHealth Trigger, string Explanation);

/// <summary>When playback that had started later fails hard, where it goes next.
///
/// Pure and deliberately narrow. Only a failure that leaves nothing to watch
/// triggers anything: a stall, a freeze, or the player dying after it had played.
/// Presentation pressure and degradation are reported only; their thresholds
/// have not been calibrated against real content, and restarting a film that is
/// still playing on the strength of them would be worse than the problem.
///
/// There is no budget here. The native rollback budget is the one the startup
/// path already spends (<see cref="NativeDvRollbackPolicy.MaximumRollbacks"/>),
/// passed in as <c>nativeRollbacksUsed</c>, so a playback can never make more than
/// two native attempts whichever path spends the rollback.</summary>
public static class PlaybackRecoveryPolicy
{
    /// <summary>Whether a health result is a hard failure of playback that had
    /// really started. A stall or failure before any progress is startup's
    /// question, answered by the startup health model.</summary>
    public static bool IsTrigger(SustainedPlaybackHealth state, bool playbackProgressed) =>
        playbackProgressed && state is SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen or
            SustainedPlaybackHealth.RuntimeFailure;

    /// <param name="maximumNativeRollbacks">The shared native budget, normally
    /// <c>NativeDvRollbackPolicy.MaximumRollbacks</c>.</param>
    /// <param name="previousUsable">Whether the retained previous generation
    /// validated. Consulted only when the budget allows a rollback at all.</param>
    public static PlaybackRecoveryDecision Decide(SustainedPlaybackHealth state, bool playbackProgressed,
        PlaybackAttemptKind kind, int nativeRollbacksUsed, int maximumNativeRollbacks,
        bool? previousUsable, string? previousReason = null)
    {
        if (!IsTrigger(state, playbackProgressed))
            return new(PlaybackRecoveryStep.None, state, "Not a hard playback failure; nothing is restarted.");

        if (kind == PlaybackAttemptKind.Stable)
            return new(PlaybackRecoveryStep.NoRecoveryAvailable, state,
                "Stable playback failed and there is no other verified player to recover to; it is not relaunched automatically.");

        if (kind == PlaybackAttemptKind.NativePrevious)
            return new(PlaybackRecoveryStep.UseStablePlayback, state,
                "The retained native runtime failed during playback; recovering with stable playback.");

        // The cap is tested before availability, exactly as at startup, so the
        // limit can never depend on what happens to be on disk.
        if (nativeRollbacksUsed >= maximumNativeRollbacks)
            return new(PlaybackRecoveryStep.UseStablePlayback, state,
                "The native rollback for this playback was already used; recovering with stable playback.");

        if (previousUsable != true)
            return new(PlaybackRecoveryStep.UseStablePlayback, state,
                (previousReason ?? "No retained native runtime is available.") + " Recovering with stable playback.");

        return new(PlaybackRecoveryStep.RetryOnPreviousNative, state,
            "The native runtime failed during playback; recovering once on the retained verified runtime.");
    }
}

/// <summary>The one-shot recovery gate for a single playback.
///
/// Every decision carries the attempt identity. Only the active attempt can
/// claim, and it can claim once: a health transition and a process exit racing
/// each other, two hard transitions in a row, or a callback arriving after the
/// attempt was superseded all resolve to at most one recovery.</summary>
public sealed class PlaybackRecoveryGate
{
    private readonly object _gate = new();
    private long _active;
    private readonly HashSet<long> _claimed = [];
    private readonly HashSet<long> _retired = [];

    /// <summary>Make an attempt the active one. Any earlier attempt is superseded
    /// and can never claim again.</summary>
    public void Activate(long attemptId)
    {
        lock (_gate)
        {
            if (_active != 0) _retired.Add(_active);
            _active = attemptId;
        }
    }

    /// <summary>The attempt has ended; nothing it reports later may act.</summary>
    public void Retire(long attemptId)
    {
        lock (_gate)
        {
            _retired.Add(attemptId);
            if (_active == attemptId) _active = 0;
        }
    }

    public bool IsActive(long attemptId) { lock (_gate) return _active == attemptId && !_retired.Contains(attemptId); }

    /// <summary>True exactly once, and only for the active attempt.</summary>
    public bool TryClaim(long attemptId)
    {
        lock (_gate)
            return _active == attemptId && !_retired.Contains(attemptId) && _claimed.Add(attemptId);
    }

    /// <summary>Claim made while the attempt was still running. Readable after the
    /// attempt has been retired, which is when the loop that owns it decides.</summary>
    public bool WasClaimed(long attemptId) { lock (_gate) return _claimed.Contains(attemptId); }

    public int Claims { get { lock (_gate) return _claimed.Count; } }
}

/// <summary>What recovery did for one failed attempt, for the report.</summary>
/// <remarks><see cref="Target"/> and <see cref="ResumeAt"/> are what was selected.
/// The Launched members are bound only when a replacement player really starts,
/// from that attempt's own plan, and are the only ones the product reports as
/// the recovery that happened.</remarks>
public sealed record PlaybackRecoveryRecord(long FailedAttempt, string FailedRuntime, string Trigger, string Step,
    double? ResumeAt, string Target, string Explanation, PlaybackAttemptKind FailedKind = PlaybackAttemptKind.Stable,
    long? LaunchedAttempt = null, PlaybackAttemptKind? LaunchedKind = null, string? LaunchedRuntime = null,
    double? LaunchedResumeAt = null);

public static class PlaybackRecoveryText
{
    public static string Position(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Two calm product lines: what failed, and what recovery did.</summary>
    public static string Describe(PlaybackRecoveryDecision decision, double? resumeAt)
    {
        string health = PlaybackHealthText.Describe(decision.Trigger);
        string where = resumeAt is double r ? " · resumed near " + Position(r) : " · restarted from beginning";
        return decision.Step switch
        {
            PlaybackRecoveryStep.RetryOnPreviousNative => health + "\nRecovery: Previous verified native runtime" + where,
            PlaybackRecoveryStep.UseStablePlayback => health + "\nRecovery: Stable playback" + where,
            PlaybackRecoveryStep.NoRecoveryAvailable => health + "\nRecovery: Unavailable for this player",
            _ => health,
        };
    }

    /// <summary>The mpv argument that starts a fresh launch at a resume point.</summary>
    public static string StartArgument(double seconds) =>
        "--start=" + Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
}
