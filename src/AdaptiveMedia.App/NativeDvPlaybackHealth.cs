namespace AdaptiveMedia;

/// <summary>Playback-time health of one native runtime generation.
///
/// This is deliberately not a boolean. The product has to tell three different
/// things apart: a runtime that broke, media the runtime was right to refuse, and
/// a person who simply stopped watching. Only the first may ever cost a
/// generation its standing.
///
/// Stable diagnostics identifiers; append rather than renumber.</summary>
public enum NativeDvHealth
{
    /// <summary>No native attempt was made, so there is nothing to judge.</summary>
    NotEvaluated = 0,
    /// <summary>The runtime started useful playback. See <see cref="NativeDvUsefulPlayback"/>.</summary>
    Healthy = 1,
    /// <summary>The process could not be started at all.</summary>
    LaunchFailed = 2,
    /// <summary>Abnormal termination before useful playback.</summary>
    ProcessCrashed = 3,
    /// <summary>A non-zero exit before useful playback that is not crash-shaped.</summary>
    ExitedBeforeUsefulPlayback = 4,
    /// <summary>The runtime answered but its renderer never initialised.</summary>
    RendererInitializationFailed = 5,
    /// <summary>The renderer came up but no video decoder was ever selected.</summary>
    DecoderInitializationFailed = 6,
    /// <summary>The running build has no verified diagnostic adapter, so this
    /// application cannot say what it composed. Reported separately from the
    /// lifecycle refusal because here the build is already running.</summary>
    ObservationContractFailed = 7,
    /// <summary>The runtime failed, but only after it had started useful playback.
    /// Truthfully a runtime fault; deliberately not a rollback trigger.</summary>
    FailedAfterUsefulPlayback = 8,
    /// <summary>The runtime reported that it could not play this source. A
    /// statement about the media, never about the runtime.</summary>
    SourceNotPlayable = 9,
    /// <summary>The user, or the application on the user's behalf, stopped
    /// playback.</summary>
    StoppedByUser = 10,
    /// <summary>Not enough was established to make a claim either way. Never a
    /// rollback trigger: absent evidence is not evidence of a fault.</summary>
    Unknown = 11,
}

/// <summary>What a single native playback attempt actually established.
///
/// Every member is a fact about that one attempt. None is derived from what the
/// plan requested, and none is carried over from an earlier attempt.</summary>
public sealed record NativeDvHealthSignals
{
    /// <summary>The native lane was selected and a native launch was attempted.</summary>
    public bool LaneSelected { get; init; }
    /// <summary>The player process really started.</summary>
    public bool ProcessStarted { get; init; }
    /// <summary>The runtime's own diagnostics connection answered.</summary>
    public bool IpcConnected { get; init; }
    /// <summary>The renderer initialised, as reported by the runtime itself.</summary>
    public bool RendererObserved { get; init; }
    /// <summary>A video decoder was selected.</summary>
    public bool VideoDecoderObserved { get; init; }
    /// <summary>A decoded frame configured the video output chain.</summary>
    public bool VideoOutputConfigured { get; init; }
    /// <summary>A verified diagnostic adapter covers the build that actually ran.</summary>
    public bool ObservationAdapterSupported { get; init; }
    /// <summary>The application asked playback to stop.</summary>
    public bool StopRequested { get; init; }
    public int ExitCode { get; init; }
    public TimeSpan Lifetime { get; init; }
}

/// <summary>The threshold for "this runtime successfully started useful playback."
///
/// It is deliberately stronger than "a process appeared" and deliberately weaker
/// than "the film played through": every condition is reached within the first
/// seconds of an ordinary launch, and all of them come from signals the session
/// already collects.</summary>
public static class NativeDvUsefulPlayback
{
    /// <summary>Useful playback means the runtime answered its diagnostics
    /// connection, brought up its renderer, selected a video decoder, and had a
    /// decoded frame configure the video output. A process that merely spawned
    /// satisfies none of these.</summary>
    public static bool Reached(NativeDvHealthSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        return signals.ProcessStarted && signals.IpcConnected && signals.RendererObserved &&
               signals.VideoDecoderObserved && signals.VideoOutputConfigured;
    }
}

/// <summary>The health of one attempt, and whether it may cost the generation.</summary>
public sealed record NativeDvHealthVerdict(
    NativeDvHealth Health,
    bool RuntimeAtFault,
    bool ReachedUsefulPlayback,
    string Explanation)
{
    /// <summary>Whether this attempt may trigger an automatic runtime rollback.
    ///
    /// A fault that appeared only after useful playback is excluded: the runtime
    /// demonstrably worked, so relaunching on an older one would restart a
    /// session that had already started, for no established reason.</summary>
    public bool RollbackCandidate => RuntimeAtFault && !ReachedUsefulPlayback;

    /// <summary>Whether this attempt counts against the generation for the rest of
    /// the session. The same test as <see cref="RollbackCandidate"/>: only a fault
    /// before useful playback is ever held against a generation.</summary>
    public bool MarksGenerationUnhealthy => RollbackCandidate;
}

/// <summary>Turns the signals from one attempt into a health verdict.
///
/// The governing rule is that a clean exit never marks a generation unhealthy,
/// and every ambiguous case resolves to <see cref="NativeDvHealth.Unknown"/>
/// rather than to a fault. A transient driver or display problem must not be
/// able to cost a hash-verified runtime its standing.</summary>
public static class NativeDvHealthEvaluator
{
    /// <summary>mpv exits 2 when it could not play a file and 3 when there was
    /// nothing to play. Both describe the media or the invocation, not the build.</summary>
    public static bool ExitCodeMeansSourceNotPlayable(int code) => code is 2 or 3;

    /// <summary>mpv exits 4 when it was quit by a signal, which means something
    /// outside the runtime ended it.</summary>
    public static bool ExitCodeMeansExternalStop(int code) => code == 4;

    /// <summary>Windows reports an unhandled exception as the NTSTATUS itself, so a
    /// crash arrives as a negative or very large exit code rather than as one of
    /// the small documented error codes.</summary>
    public static bool ExitCodeMeansCrash(int code) => code < 0 || (uint)code >= 0xC0000000u;

    public static NativeDvHealthVerdict Evaluate(NativeDvHealthSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        if (!signals.LaneSelected)
            return new(NativeDvHealth.NotEvaluated, false, false,
                "The native Dolby Vision lane was not used, so its health was not evaluated.");

        if (!signals.ProcessStarted)
            return new(NativeDvHealth.LaunchFailed, true, false,
                "The native Dolby Vision runtime could not be started.");

        bool useful = NativeDvUsefulPlayback.Reached(signals);
        bool crashed = ExitCodeMeansCrash(signals.ExitCode);
        bool clean = signals.ExitCode == 0;

        // A stop this application asked for is an outcome, not a fault, and it is
        // decided before any exit code: a player that is told to quit may still
        // terminate abnormally on the way out.
        if (signals.StopRequested || ExitCodeMeansExternalStop(signals.ExitCode))
            return useful
                ? new(NativeDvHealth.Healthy, false, true,
                    "The native Dolby Vision runtime started playback and was then stopped.")
                : new(NativeDvHealth.StoppedByUser, false, false,
                    "Native Dolby Vision playback was stopped before it had started.");

        // The runtime's own verdict on the media. Never the generation's fault,
        // whichever generation happened to report it.
        if (ExitCodeMeansSourceNotPlayable(signals.ExitCode))
            return new(NativeDvHealth.SourceNotPlayable, false, useful,
                "The native Dolby Vision runtime reported that it could not play this source.");

        if (useful)
            return clean
                ? new(NativeDvHealth.Healthy, false, true,
                    "The native Dolby Vision runtime started useful playback and ended normally.")
                // A real fault, and deliberately not a rollback trigger: this
                // generation demonstrably works.
                : new(NativeDvHealth.FailedAfterUsefulPlayback, true, true,
                    $"The native Dolby Vision runtime failed ({signals.ExitCode}) after it had started useful playback.");

        // Below here the attempt never reached useful playback. A clean exit is
        // still not a fault: a very short file, or a window closed before the
        // renderer settled, both land here and neither says anything about the
        // build.
        if (clean)
            return new(NativeDvHealth.Unknown, false, false,
                "Native Dolby Vision playback ended before its state could be established; nothing is claimed about the runtime.");

        if (!signals.IpcConnected)
            return crashed
                ? new(NativeDvHealth.ProcessCrashed, true, false,
                    $"The native Dolby Vision runtime terminated abnormally ({signals.ExitCode}) before it reported anything.")
                : new(NativeDvHealth.ExitedBeforeUsefulPlayback, true, false,
                    $"The native Dolby Vision runtime exited ({signals.ExitCode}) before it reported anything.");

        // The runtime answered, so its own identity is known and can be judged.
        if (!signals.ObservationAdapterSupported)
            return new(NativeDvHealth.ObservationContractFailed, true, false,
                "The running native Dolby Vision runtime is not one this build knows how to read, so it could not report what it composed.");

        if (crashed)
            return new(NativeDvHealth.ProcessCrashed, true, false,
                $"The native Dolby Vision runtime terminated abnormally ({signals.ExitCode}) before useful playback.");
        if (!signals.RendererObserved)
            return new(NativeDvHealth.RendererInitializationFailed, true, false,
                $"The native Dolby Vision renderer never initialised and the runtime exited ({signals.ExitCode}).");
        if (!signals.VideoDecoderObserved)
            return new(NativeDvHealth.DecoderInitializationFailed, true, false,
                $"The native Dolby Vision runtime selected no video decoder and exited ({signals.ExitCode}).");
        return new(NativeDvHealth.ExitedBeforeUsefulPlayback, true, false,
            $"The native Dolby Vision runtime exited ({signals.ExitCode}) before a frame reached the display.");
    }
}

/// <summary>Session-local memory of which generations failed during playback.
///
/// It is session-local on purpose. A generation is a hash-verified tree of files;
/// one bad launch is far more likely to be a driver, a display, or a GPU reset
/// than a broken build, and persisting that judgement would let a single
/// transient failure cost a valid runtime its standing until somebody found the
/// file that said so. Restarting the application clears every entry, and nothing
/// here is ever written to disk.
///
/// It is also never a source of runtime identity. Which generation is current
/// remains entirely the lifecycle's answer; this only records what happened when
/// one was run.</summary>
public sealed class NativeDvHealthMemory
{
    /// <summary>How many playback-time faults a generation may record in one
    /// session before later attempts start on the retained previous generation
    /// instead of failing on this one first. Two, so a single transient failure
    /// never demotes anything.</summary>
    public const int DemotionThreshold = 2;

    private readonly Dictionary<string, (int Faults, NativeDvHealth Last)> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Record a verdict. Only a fault before useful playback counts;
    /// everything else is ignored, including a media refusal, a user stop, and a
    /// failure that followed real playback.</summary>
    public void Record(string? generationId, NativeDvHealthVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (!NativeDvRuntimeLifecycle.IsGenerationId(generationId) || !verdict.MarksGenerationUnhealthy) return;
        lock (_gate)
        {
            _faults.TryGetValue(generationId!, out var existing);
            _faults[generationId!] = (existing.Faults + 1, verdict.Health);
        }
    }

    public int FaultCount(string? generationId)
    {
        if (!NativeDvRuntimeLifecycle.IsGenerationId(generationId)) return 0;
        lock (_gate) return _faults.TryGetValue(generationId!, out var existing) ? existing.Faults : 0;
    }

    /// <summary>Whether this generation failed at least once during this session.
    /// Enough to rule it out as a rollback target, so a fallback can never bounce
    /// back to something already known to have failed.</summary>
    public bool IsKnownUnhealthy(string? generationId) => FaultCount(generationId) > 0;

    /// <summary>Whether this generation has failed often enough that a later
    /// attempt in this session should start on the retained previous generation
    /// rather than fail on this one first.</summary>
    public bool IsDemoted(string? generationId) => FaultCount(generationId) >= DemotionThreshold;

    public NativeDvHealth LastHealth(string? generationId)
    {
        if (!NativeDvRuntimeLifecycle.IsGenerationId(generationId)) return NativeDvHealth.NotEvaluated;
        lock (_gate) return _faults.TryGetValue(generationId!, out var existing) ? existing.Last : NativeDvHealth.NotEvaluated;
    }

    /// <summary>Clear a generation's recorded faults, for an explicit retry.</summary>
    public void Clear(string? generationId)
    {
        if (generationId is null) return;
        lock (_gate) _faults.Remove(generationId);
    }
}

/// <summary>What the user is told about the native runtime after playback.
/// Stable diagnostics identifiers; append rather than renumber.</summary>
public enum NativeDvPlaybackStatus
{
    NotUsed = 0,
    RuntimeHealthy = 1,
    RolledBackToPreviousRuntime = 2,
    PreviousRuntimeAlsoFailed = 3,
    PreviousRuntimeUnavailable = 4,
    HealthUnknown = 5,
    MediaOrUserOutcome = 6,
    RuntimeUnavailable = 7,
}

/// <summary>What a native attempt's outcome leads to next.</summary>
public enum NativeDvNextStep
{
    /// <summary>Nothing further: this attempt is the result.</summary>
    Finish = 0,
    /// <summary>Retry once on the retained previous generation.</summary>
    RetryOnPreviousRuntime = 1,
    /// <summary>The native lane is finished for this playback; use the established
    /// stable path.</summary>
    UseStablePlayback = 2,
}

public sealed record NativeDvRollbackDecision(NativeDvNextStep Step, NativeDvPlaybackStatus Status, string Explanation);

/// <summary>What happens after a native attempt, as a pure decision.
///
/// This is separate from the code that launches players so the rules that matter
/// most can be tested directly: that a healthy or merely unlucky attempt is never
/// rolled back, that at most one rollback ever happens, and that once the cap is
/// reached no fallback is even looked for.</summary>
public static class NativeDvRollbackPolicy
{
    /// <summary>Maximum automatic native runtime rollbacks per playback. One.
    ///
    /// This is the whole loop prevention. After a single rollback the native lane
    /// is finished for this playback, so no sequence of failures can produce a
    /// third native launch.</summary>
    public const int MaximumRollbacks = 1;

    public static NativeDvRollbackDecision Decide(NativeDvHealthVerdict verdict, int rollbacksAlreadyDone,
        NativeDvFallbackCandidate? fallback)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (!verdict.RollbackCandidate)
        {
            var status = verdict.Health switch
            {
                NativeDvHealth.Healthy => rollbacksAlreadyDone > 0
                    ? NativeDvPlaybackStatus.RolledBackToPreviousRuntime
                    : NativeDvPlaybackStatus.RuntimeHealthy,
                NativeDvHealth.Unknown or NativeDvHealth.FailedAfterUsefulPlayback => NativeDvPlaybackStatus.HealthUnknown,
                NativeDvHealth.NotEvaluated => NativeDvPlaybackStatus.NotUsed,
                _ => NativeDvPlaybackStatus.MediaOrUserOutcome,
            };
            return new(NativeDvNextStep.Finish, status, verdict.Explanation);
        }

        // The cap is tested before a fallback is even considered. Checking
        // availability first would make the limit depend on what happens to be on
        // disk, which is exactly how a bounce becomes possible.
        if (rollbacksAlreadyDone >= MaximumRollbacks)
            return new(NativeDvNextStep.UseStablePlayback, NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed,
                "The retained native Dolby Vision runtime has already had its one attempt; using stable playback.");

        if (fallback is null || !fallback.IsUsable)
            return new(NativeDvNextStep.UseStablePlayback, NativeDvPlaybackStatus.PreviousRuntimeUnavailable,
                fallback?.Reason ?? "No retained native Dolby Vision runtime is available.");

        return new(NativeDvNextStep.RetryOnPreviousRuntime, NativeDvPlaybackStatus.RolledBackToPreviousRuntime,
            $"The current native Dolby Vision runtime failed; retrying once on the retained runtime {fallback.Descriptor!.MpvVersion}.");
    }
}

public static class NativeDvPlaybackStatusText
{
    /// <summary>One product-facing line. None of these claims a composition
    /// result: what was delivered is reported separately, from the observation of
    /// whichever attempt actually ran.</summary>
    public static string Describe(NativeDvPlaybackStatus status) => status switch
    {
        NativeDvPlaybackStatus.RuntimeHealthy => "Native runtime healthy.",
        NativeDvPlaybackStatus.RolledBackToPreviousRuntime => "Native runtime failed; the previous native runtime was used.",
        NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed => "The native runtime failed and so did the previous one; stable playback was used.",
        NativeDvPlaybackStatus.PreviousRuntimeUnavailable => "The native runtime failed and no previous native runtime was available; stable playback was used.",
        NativeDvPlaybackStatus.HealthUnknown => "Native runtime health unknown.",
        NativeDvPlaybackStatus.MediaOrUserOutcome => "Native playback ended without a runtime problem.",
        NativeDvPlaybackStatus.RuntimeUnavailable => "Native runtime unavailable; using stable playback.",
        _ => "Native playback was not used.",
    };
}
