using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
namespace AdaptiveMedia;

public sealed class PlaybackService
{
    public event Action<string>? StatusChanged;
    public SessionDiagnostics? LastReport { get; private set; }
    private readonly Dictionary<string, (DateTime Stamp, bool Vpp, string Version)> _capabilities = [];
    private readonly Dictionary<string, (DateTime Stamp, long Length, MediaInfo Media)> _mediaCache = [];
    private readonly Dictionary<string, SessionDiagnostics> _preparedReports = [];
    private readonly Dictionary<string, NativeSelection> _nativeSelections = [];
    private readonly Dictionary<string, NativeAttempt> _nativeAttempts = [];
    private sealed record NativeSelection(NativeDvLaneOutcome Outcome, string? GenerationId,
        string StableExecutable, bool Retained);

    /// <summary>What one native playback attempt asked for, and what it went on to
    /// establish. Each attempt owns its own record, so a retry on another
    /// generation starts from nothing and cannot inherit an earlier attempt's
    /// observations.</summary>
    private sealed class NativeAttempt
    {
        public required NativeDvLaneOutcome Outcome { get; init; }
        public required string LogPath { get; init; }
        public required string? GenerationId { get; init; }
        public bool StopRequested { get; set; }
        public bool IpcConnected { get; set; }
        public bool AdapterSupported { get; set; }
        public bool VideoDecoderObserved { get; set; }
        public bool VideoOutputConfigured { get; set; }
        public NativeDvObservation? Observation { get; set; }
        /// <summary>Until when a settled result short of the requested full
        /// composition keeps reading this attempt's own log for a late upgrade.</summary>
        public TimeSpan? ProvisionalUntil { get; set; }
    }

    /// <summary>How long a settled result short of full composition may still be
    /// upgraded from the attempt's own log. A launch that starts mid-file (a
    /// recovery resume) seeks before decoding, so the renderer reports well before
    /// the first composed frame; settling and silencing the log at that moment
    /// would under-report composition that really happens.</summary>
    private static readonly TimeSpan ObservationUpgradeWindow = TimeSpan.FromSeconds(10);

    /// <summary>The native Dolby Vision lane. Injectable so tests can drive
    /// provisioning without reaching the network.</summary>
    public NativeDvLane? NativeDolbyVision { get; init; }

    /// <summary>What the native lane decided for the most recent preparation,
    /// including the exact reason when it was not selected.</summary>
    public NativeDvLaneOutcome? LastNativeOutcome { get; private set; }

    /// <summary>What the most recent native session actually did, as opposed to
    /// what it asked for. Null when the native lane did not run.</summary>
    public NativeDvObservation? LastNativeObservation { get; private set; }

    /// <summary>Runtime lifecycle state from the most recent preparation: which
    /// generation is current, which is retained, and whether an update or a
    /// failure happened.</summary>
    public NativeDvLifecycleStatus? LastNativeLifecycle { get; private set; }

    /// <summary>Session-local record of which generations failed during playback.
    /// Never written to disk, and never a source of runtime identity.</summary>
    public NativeDvHealthMemory NativeHealth { get; } = new();

    /// <summary>The health of the most recent native attempt.</summary>
    public NativeDvHealthVerdict? LastNativeHealth { get; private set; }

    /// <summary>Why the retained previous generation was or was not used, when a
    /// native attempt failed in a way that could have been rolled back.</summary>
    public NativeDvFallbackCandidate? LastNativeFallback { get; private set; }

    /// <summary>The one product-facing line about the native runtime for the most
    /// recent playback.</summary>
    public NativeDvPlaybackStatus LastNativePlaybackStatus { get; private set; }

    /// <summary>Sustained health of the most recent player attempt: whether playback
    /// that started is still healthy. Live while playing, final after it ends.
    /// Separate from <see cref="LastNativeHealth"/>, which is a startup verdict
    /// about one native runtime generation.</summary>
    public PlaybackHealthSnapshot? LastPlaybackHealth => _health?.Snapshot() ?? _finalHealth;
    private volatile PlaybackHealthMonitor? _health;
    private PlaybackHealthSnapshot? _finalHealth;
    private long _healthAttempts;

    /// <summary>Sustained-health thresholds. Injectable so tests can compress time;
    /// the product always uses the defaults.</summary>
    public PlaybackHealthPolicy HealthPolicy { get; init; } = PlaybackHealthPolicy.Default;

    /// <summary>What automatic recovery did during the most recent playback, if anything.</summary>
    public PlaybackRecoveryRecord? LastRecovery { get; private set; }

    /// <summary>Guards the per-playback truth state (attempt evidence and recovery
    /// records), which the playback loop writes and the UI reads.</summary>
    private readonly object _truthGate = new();
    private PlaybackAttemptEvidence? _evidence;
    private readonly List<PlaybackAttemptEvidence> _attemptEvidence = [];
    private bool _playbackFelRequested;
    private IReadOnlyList<string> _playbackNotes = [];
    /// <summary>Why the native lane was not used, per prepared stable plan.</summary>
    private readonly Dictionary<string, string> _preparedNotes = [];

    /// <summary>The truth chain for the current (or most recent) playback: what was
    /// requested, planned and observed, its health, and what recovery really did.
    /// Observed comes only from the current attempt's own evidence.</summary>
    public PlaybackTruthReport? CurrentTruth()
    {
        lock (_truthGate)
        {
            if (_evidence is null) return null;
            return PlaybackTruthBuilder.Build(_evidence, _attemptEvidence.ToArray(),
                LastReport?.Recovery.ToArray() ?? [], _playbackNotes);
        }
    }

    /// <summary>What one attempt established: how it ended, and whether it claimed
    /// the playback's recovery, with the resume point it confirmed itself.</summary>
    private sealed record AttemptRun(int ExitCode, bool Started, TimeSpan Lifetime,
        SustainedPlaybackHealth? RecoveryTrigger, PlaybackHealthSnapshot? Health);

    public static string NativeConfigDirectory => Path.Combine(SettingsStore.DirectoryPath, "native-dv-config");

    /// <summary>Planning placeholder; each actual launch gets its own temporary log.</summary>
    public static string NativeLogPath => Path.Combine(DiagnosticsStore.DirectoryPath, "native-dv.log");

    /// <summary>Rollback planning placeholder, replaced by a unique attempt log at launch.</summary>
    public static string NativeFallbackLogPath => Path.Combine(DiagnosticsStore.DirectoryPath, "native-dv-fallback.log");

    public async Task<PlaybackPlan> PrepareAsync(IReadOnlyList<string> items, PlaybackOptions options,
        SystemSummary system, AppSettings settings, PlaybackTarget? destination = null)
    {
        var expanded = new List<string>();
        string[] extensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".ts", ".m2ts", ".flv", ".wmv", ".mp3", ".flac", ".m4a", ".aac", ".opus", ".wav", ".ogg"];
        foreach (string item in items)
        {
            if (Directory.Exists(item)) expanded.AddRange(Directory.EnumerateFiles(item).Where(x => extensions.Contains(Path.GetExtension(x).ToLowerInvariant()))
                .OrderBy(x => System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(x), "[0-9]+", m => m.Value.PadLeft(16, '0')), StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(item)) expanded.Add(Path.GetFullPath(item));
            else if (Uri.TryCreate(item, UriKind.Absolute, out var uri) && new[] { "https", "http", "rtsp", "rtmp", "ftp" }.Contains(uri.Scheme)) expanded.Add(item);
            else throw new FileNotFoundException("The selected media file could not be found.", item);
        }
        if (expanded.Count == 0) throw new IOException("This folder contains no supported media files.");
        string mpv = ResolveMpv(system.MpvPath);
        if (!_capabilities.TryGetValue(mpv, out var capability) || capability.Stamp != File.GetLastWriteTimeUtc(mpv))
        {
            var filters = await NativeProcess.CaptureAsync(mpv, ["--no-config", "--terminal=yes", "--vf=help"], TimeSpan.FromSeconds(10));
            var version = await NativeProcess.CaptureAsync(mpv, ["--no-config", "--terminal=yes", "--version"], TimeSpan.FromSeconds(10));
            capability = (File.GetLastWriteTimeUtc(mpv), filters.ExitCode == 0 && filters.Output.Contains("d3d11vpp"), version.Output.Split('\n')[0].Trim());
            _capabilities[mpv] = capability;
        }
        MediaInfo source;
        var file = File.Exists(expanded[0]) ? new FileInfo(expanded[0]) : null;
        if (file is { Exists: true } && _mediaCache.TryGetValue(file.FullName, out var cached) && cached.Stamp == file.LastWriteTimeUtc && cached.Length == file.Length) source = cached.Media;
        else
        {
            try { source = await MediaProbe.ReadAsync(mpv, expanded[0]); }
            catch (TimeoutException) { source = new(); }
            if (file is { Exists: true } && source.Known)
            {
                if (_mediaCache.Count >= 32) _mediaCache.Clear();
                _mediaCache[file.FullName] = (file.LastWriteTimeUtc, file.Length, source);
            }
        }
        // Re-read cheap display geometry for every preparation; GPU inventory may be cached.
        system.Screens = MonitorInventory.GetScreens();
        Native.DisplayColorInfo[] displays;
        try { displays = Native.HdrController.GetDisplays(); } catch { displays = []; }
        int screen = Array.FindIndex(system.Screens, x => x.Primary);
        if (screen < 0) screen = 0;
        if (settings.PreferExternalDisplay && system.Screens.Length > 1)
            screen = Enumerable.Range(0, system.Screens.Length).Where(i => !system.Screens[i].Primary)
                .OrderByDescending(i => (long)system.Screens[i].Width * system.Screens[i].Height).FirstOrDefault(screen);
        var selected = system.Screens.ElementAtOrDefault(screen);
        bool hdrEnabled = displays.Length == 1 && displays[0].Supported && displays[0].Enabled && !displays[0].ForceDisabled;
        bool fullscreen = system.Screens.Length > 1 && selected is { Primary: false } && settings.FullscreenExternal;
        double? refreshRate = displays.Length == 1 && displays[0].RefreshRateHz > 0 ? displays[0].RefreshRateHz : null;
        var target = destination ?? new PlaybackTarget(selected is null ? 0 : fullscreen ? selected.Width : (int)(selected.WorkWidth * .8),
            selected is null ? 0 : fullscreen ? selected.Height : (int)(selected.WorkHeight * .8), screen, fullscreen, hdrEnabled, refreshRate);
        var playbackCapabilities = new PlaybackCapabilities(system.HasNvidia, capability.Vpp, system.NvidiaAdapter,
            system.NvidiaAdapter?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true);
        EnhancementDecision? enhancementDecision = options.Intent is null ? null : EnhancementPlanner.Decide(options.Intent,
            new(source, target, playbackCapabilities, expanded.Count));
        PlaybackOptions effectiveOptions = enhancementDecision?.ApplyTo(options) ?? options;
        // The native Dolby Vision lane is opt-in, applies to a single local file, and
        // produces a plan only once a provisioned runtime has validated. Any refusal
        // falls through to the established stable path with the reason recorded.
        string? nativeNote = null;
        if (NativeDolbyVision is not null && settings.NativeDolbyVisionLane && expanded.Count == 1 && File.Exists(expanded[0]))
        {
            string nativePipe = "adaptive-media-native-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(NativeConfigDirectory);
            Directory.CreateDirectory(DiagnosticsStore.DirectoryPath);
            var outcome = await NativeDolbyVision.PrepareAsync(expanded[0], settings, NativeConfigDirectory,
                nativePipe, NativeLogPath, target, new Progress<string>(x => StatusChanged?.Invoke(x)));
            LastNativeOutcome = outcome;
            LastNativeLifecycle = NativeDolbyVision.LastStatus;
            if (LastNativeLifecycle is not null)
            {
                DiagnosticsStore.Event("info", "native-dv-lifecycle",
                    $"{LastNativeLifecycle.State}: {LastNativeLifecycle.Summary} (current={LastNativeLifecycle.CurrentGeneration ?? "none"}, " +
                    $"previous={LastNativeLifecycle.PreviousGeneration ?? "none"}, removed={LastNativeLifecycle.GenerationsRemoved})");
                // Ready is the ordinary case and needs no announcement; anything the
                // user could act on does.
                if (LastNativeLifecycle.State != NativeDvLifecycleState.Ready)
                    StatusChanged?.Invoke(LastNativeLifecycle.Summary);
            }
            if (outcome.Selected && outcome.Plan is { Supported: true, Request: not null })
            {
                string generation = LastNativeLifecycle?.CurrentGeneration ?? "";
                bool retainedSelection = false;

                // A generation that has already failed repeatedly in this session
                // starts on the retained fallback instead of failing again first.
                // This is session-local and bounded: the judgement is cleared by a
                // restart, and it changes only which generation is run, never which
                // one the lifecycle considers current.
                if (NativeHealth.IsDemoted(generation))
                {
                    var demoted = NativeDolbyVision.Lifecycle.ResolveFallback(generation, null, NativeHealth);
                    LastNativeFallback = demoted;
                    if (demoted.IsUsable)
                    {
                        var preferred = NativeDvLane.WithRuntime(outcome, demoted.Runtime!, expanded[0],
                            NativeConfigDirectory, nativePipe, NativeFallbackLogPath, target);
                        if (preferred.Selected && preferred.Plan is { Supported: true, Request: not null })
                        {
                            outcome = preferred;
                            generation = demoted.GenerationId ?? generation;
                            retainedSelection = true;
                            string note = $"The current native Dolby Vision runtime failed {NativeHealth.FaultCount(LastNativeLifecycle?.CurrentGeneration)} times in this session; starting on the retained previous runtime.";
                            StatusChanged?.Invoke(note);
                            DiagnosticsStore.Event("warning", "native-dv-health", note);
                        }
                    }
                }

                DiagnosticsStore.Event("info", "native-dv", "Native Dolby Vision runtime selected.");
                var nativeDecision = enhancementDecision?.SuppressForCorrectnessPath(
                    "The native Dolby Vision correctness path was selected; discretionary detail, motion, and cleanup processing were not added to its separate renderer plan.");
                var nativeOptions = nativeDecision?.ApplyTo(options) ?? effectiveOptions;
                var nativePlan = new PlaybackPlan(outcome.Plan.Executable!, outcome.Plan.Arguments,
                    nativeOptions with { AutoHdrSwitch = settings.AutoHdrSwitch }, source, target,
                    "Native Dolby Vision gpu-next", false, false, 1,
                    [.. nativeDecision?.Reasons ?? [], outcome.Plan.Explanation,
                     "Full enhancement-layer composition is requested; what it actually delivers is reported after playback starts."],
                    nativePipe, options.Intent, nativeDecision);
                if (_preparedReports.Count >= 32) _preparedReports.Clear();
                if (_nativeSelections.Count >= 32) _nativeSelections.Clear();
                _nativeSelections[nativePipe] = new(outcome,
                    NativeDvRuntimeLifecycle.IsGenerationId(generation) ? generation : null, mpv, retainedSelection);
                LastNativeOutcome = outcome;
                _preparedReports[nativePipe] = new() { Plan = nativePlan, MpvVersion = outcome.Runtime!.MpvVersion,
                    Summary = nativePlan.Summary, Hardware = new { system.Gpu, system.Cpu, system.Drivers, system.Screens } };
                return nativePlan;
            }
            DiagnosticsStore.Event("info", "native-dv", "Native Dolby Vision was not used: " + (outcome.Reason ?? "unknown reason"));
            nativeNote = "Native Dolby Vision was not used: " + (outcome.Reason ?? "unknown reason");
        }
        var plan = PlaybackPlanBuilder.Build(mpv, Path.Combine(AppContext.BaseDirectory, "mpv-config"), expanded, options with { AutoHdrSwitch = settings.AutoHdrSwitch }, source, target,
            playbackCapabilities,
            "adaptive-media-" + Guid.NewGuid().ToString("N"), settings.HdmiBitstream, HasStreamHelper(system));
        if (_preparedReports.Count >= 32) { _preparedReports.Clear(); _preparedNotes.Clear(); }
        if (nativeNote is not null) _preparedNotes[plan.PipeName] = nativeNote;
        _preparedReports[plan.PipeName] = new() { Plan = plan, MpvVersion = capability.Version, Summary = plan.Summary,
            Hardware = new { system.Gpu, system.Cpu, system.Drivers, system.Screens, system.Audio, system.Power,
                Topology = "GPU inventory does not establish display ownership. VRR and endpoint bitstream support are unknown." } };
        return plan;
    }

    public async Task<int> LaunchAsync(PlaybackPlan plan, double? stopAfterSeconds = null)
    {
        if (plan.Renderer.StartsWith("Native Dolby Vision", StringComparison.Ordinal) &&
            !_nativeSelections.ContainsKey(plan.PipeName))
            throw new InvalidOperationException("This native playback plan has expired. Prepare playback again.");
        _preparedReports.TryGetValue(plan.PipeName, out var prepared);
        LastNativeObservation = null;
        LastNativeHealth = null;
        LastNativeFallback = null;
        LastNativePlaybackStatus = NativeDvPlaybackStatus.NotUsed;
        _finalHealth = null;
        LastRecovery = null;
        // One gate per playback: however many attempts this playback makes, each
        // may trigger recovery at most once, and only while it is the active one.
        var gate = new PlaybackRecoveryGate();
        lock (_truthGate)
        {
            // A new playback starts with no evidence at all; nothing from the last
            // one can appear in this one's truth chain.
            _evidence = null;
            _attemptEvidence.Clear();
            _playbackFelRequested = _nativeSelections.TryGetValue(plan.PipeName, out var nativeSelection) &&
                nativeSelection.Outcome.Plan?.Request?.EnhancementLayer == true;
            _playbackNotes = _preparedNotes.TryGetValue(plan.PipeName, out var note) ? [note] : [];
        }
        LastReport = new() { Plan = plan, Summary = plan.Summary, MpvVersion = prepared?.MpvVersion ?? "unknown", Hardware = prepared?.Hardware };
        using var hdrSession = HdrSession.Begin(plan, LastReport);
        DiagnosticsStore.Event("info", "playback-plan", plan.Renderer);
        StatusChanged?.Invoke(plan.Summary);

        if (_nativeSelections.ContainsKey(plan.PipeName))
        {
            var native = await RunNativeWithRollbackAsync(plan, stopAfterSeconds, gate);
            if (native.Handled)
            {
                LastReport.ExitCode = native.ExitCode;
                LastReport.Summary = (LastReport.Plan?.Summary ?? "Playback") + "\n" +
                    (native.ExitCode == 0 ? "Playback ended." : $"Playback failed ({native.ExitCode}).") +
                    HealthSummaryLine() + "\n" + string.Join("\n", LastReport.FallbackHistory);
                LastReport.TruthChain = CurrentTruth()?.Format();
                try { DiagnosticsStore.Save(LastReport); }
                catch (IOException) { StatusChanged?.Invoke("Playback ended; diagnostics could not be saved."); }
                return native.ExitCode;
            }
            LastNativeObservation = null;
            plan = native.StablePlan!;
            LastReport.Plan = plan;
            StatusChanged?.Invoke(plan.Summary);
        }

        var stableRun = await RunOnceAsync(plan, stopAfterSeconds, gate, PlaybackAttemptKind.Stable);
        int code = stableRun.ExitCode;
        ReportStableHardFailure(stableRun);
        // The compatibility retry is for a player that never got going. One that
        // played and then failed is a hard playback failure of the stable player,
        // which V1 reports rather than relaunching from the beginning.
        if (code != 0 && plan.Renderer == "RTX D3D11" && stableRun.Health?.PlaybackProgressed != true)
        {
            const string reason = "RTX playback failed. Retrying once with compatibility video and PCM audio.";
            LastReport.FallbackHistory.Add(reason); StatusChanged?.Invoke(reason);
            DiagnosticsStore.Event("warning", "fallback", reason);
            var fallback = CompatibilityRetryPlan(plan, Path.Combine(AppContext.BaseDirectory, "mpv-config"));
            LastReport.Plan = fallback;
            var compatibilityRun = await RunOnceAsync(fallback, stopAfterSeconds, gate, PlaybackAttemptKind.Stable);
            code = compatibilityRun.ExitCode;
            ReportStableHardFailure(compatibilityRun);
        }
        LastReport.ExitCode = code;
        LastReport.Summary = (LastReport.Plan?.Summary ?? "Playback") + "\n" + (code == 0 ? "Playback ended." : $"Playback failed ({code}).") +
            HealthSummaryLine() + "\n" + string.Join("\n", LastReport.FallbackHistory);
        LastReport.TruthChain = CurrentTruth()?.Format();
        try { DiagnosticsStore.Save(LastReport); } catch (IOException) { StatusChanged?.Invoke("Playback ended; diagnostics could not be saved."); }
        return code;
    }

    private readonly record struct NativeRunResult(bool Handled, int ExitCode, PlaybackPlan? StablePlan);

    /// <summary>Run a native attempt, and if the runtime itself failed before
    /// playback was useful, retry once on the retained previous generation before
    /// giving up on the lane.
    ///
    /// Three things are kept strictly apart here. Whether the runtime is healthy is
    /// decided from that attempt's own signals. Whether a rollback is allowed is
    /// decided from eligibility rules that require the retained generation to have
    /// been verified. What was delivered is decided only by the observation of
    /// whichever attempt actually ran, so a rollback that succeeds in starting
    /// playback but composes nothing is still reported as base layer or unknown.
    /// Success at rolling back is never success at composing.</summary>
    private async Task<NativeRunResult> RunNativeWithRollbackAsync(PlaybackPlan plan, double? stopAfterSeconds,
        PlaybackRecoveryGate gate)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rollbacks = 0;
        // The resume point of a recovery still in progress. A replacement that
        // never plays (it fails to start, or fails before useful playback) must not
        // discard it: the failed attempt's confirmed position is still the most
        // trustworthy place to continue from.
        double? pendingResume = null;

        while (true)
        {
            var selection = _nativeSelections[plan.PipeName];
            // A prepared plan is reusable; observed state is not. A unique log
            // also prevents a concurrent session or an undeletable old log from
            // donating evidence to this launch.
            string logPath = Path.Combine(DiagnosticsStore.DirectoryPath, "native-dv-" + Guid.NewGuid().ToString("N") + ".log");
            plan = plan with { Arguments = plan.Arguments.Select(x => x.StartsWith("--log-file=", StringComparison.Ordinal)
                ? "--log-file=" + logPath : x).ToImmutableArray() };
            var attempt = new NativeAttempt { Outcome = selection.Outcome,
                LogPath = logPath, GenerationId = selection.GenerationId };
            _nativeAttempts[plan.PipeName] = attempt;
            LastReport!.Plan = plan;
            LastNativeObservation = null;
            if (attempt.GenerationId is not null) attempted.Add(attempt.GenerationId);

            // Hold the generation for the life of the attempt so cleanup, in this
            // process or another, cannot remove it while it is playing.
            using var pin = NativeDolbyVision?.Lifecycle.Store.PinGeneration(attempt.GenerationId);

            if (pin is null)
            {
                LastNativePlaybackStatus = NativeDvPlaybackStatus.RuntimeUnavailable;
                Announce("The native generation could not be protected from cleanup; using stable playback.");
                _nativeAttempts.Remove(plan.PipeName);
                return new(false, 0, BuildStableFallbackPlan(plan));
            }
            // Validate a retained generation again under its lifetime lease. A
            // prior selection cannot authorize launch after files or identity change.
            if (selection.Retained)
            {
                var store = NativeDolbyVision!.Lifecycle.Store;
                var descriptor = store.ReadDescriptorSnapshot(attempt.GenerationId);
                if (descriptor is null || !NativeDvDiagnosticAdapters.SupportsCommit(descriptor.MpvCommit) ||
                    store.Resolve(descriptor) is not { IsUsable: true, Runtime: not null } resolved ||
                    !string.Equals(resolved.Runtime.ExecutablePath, plan.Executable, StringComparison.OrdinalIgnoreCase))
                {
                    LastNativePlaybackStatus = NativeDvPlaybackStatus.RuntimeUnavailable;
                    Announce("The retained native generation no longer validates; using stable playback.");
                    _nativeAttempts.Remove(plan.PipeName);
                    return new(false, 0, BuildStableFallbackPlan(plan));
                }
            }
            AttemptRun run;
            try
            {
                run = await RunOnceAsync(plan, stopAfterSeconds, gate,
                    selection.Retained ? PlaybackAttemptKind.NativePrevious : PlaybackAttemptKind.NativeCurrent);
            }
            finally
            {
                _nativeAttempts.Remove(plan.PipeName);
                // The reduced evidence is in the session report; temporary logs
                // must not accumulate with the number of playback attempts.
                try { File.Delete(logPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var verdict = NativeDvHealthEvaluator.Evaluate(new NativeDvHealthSignals
            {
                LaneSelected = true,
                ProcessStarted = run.Started,
                IpcConnected = attempt.IpcConnected,
                RendererObserved = attempt.Observation?.Renderer == DvObservedState.Active,
                VideoDecoderObserved = attempt.VideoDecoderObserved,
                VideoOutputConfigured = attempt.VideoOutputConfigured,
                ObservationAdapterSupported = attempt.AdapterSupported,
                StopRequested = attempt.StopRequested,
                ExitCode = run.ExitCode,
                Lifetime = run.Lifetime,
            });
            LastNativeHealth = verdict;
            NativeHealth.Record(attempt.GenerationId, verdict);
            DiagnosticsStore.Event(verdict.RuntimeAtFault ? "warning" : "info", "native-dv-health",
                $"{verdict.Health}: {verdict.Explanation} (generation={attempt.GenerationId ?? "unknown"}, " +
                $"exit={run.ExitCode}, useful={verdict.ReachedUsefulPlayback})");

            // Resolve a fallback only when one could actually be used. The policy
            // tests the retry cap before availability, so this must not run first:
            // hashing a retained runtime we are not allowed to try would be work
            // done to reach an answer already decided.
            NativeDvFallbackCandidate? fallback = null;
            NativeDvRollbackDecision decision;
            PlaybackRecoveryDecision? recovery = null;
            double? resumeAt = pendingResume;
            if (run.RecoveryTrigger is { } trigger)
            {
                // A hard failure after playback had started. It spends the same
                // single native rollback as startup, resolves the same strictly
                // validated fallback (which excludes every generation already tried
                // in this playback), and is decided before the startup policy,
                // which would otherwise call a playing runtime healthy and finish.
                var kind = selection.Retained ? PlaybackAttemptKind.NativePrevious : PlaybackAttemptKind.NativeCurrent;
                if (kind == PlaybackAttemptKind.NativeCurrent && rollbacks < NativeDvRollbackPolicy.MaximumRollbacks)
                {
                    fallback = NativeDolbyVision?.Lifecycle.ResolveFallback(attempt.GenerationId, attempted, NativeHealth);
                    LastNativeFallback = fallback;
                }
                recovery = PlaybackRecoveryPolicy.Decide(trigger, true, kind, rollbacks,
                    NativeDvRollbackPolicy.MaximumRollbacks, fallback?.IsUsable, fallback?.Reason);
                resumeAt = run.Health?.ResumePosition;
                pendingResume = resumeAt;
                decision = recovery.Step == PlaybackRecoveryStep.RetryOnPreviousNative
                    ? new(NativeDvNextStep.RetryOnPreviousRuntime, NativeDvPlaybackStatus.RolledBackToPreviousRuntime, recovery.Explanation)
                    : new(NativeDvNextStep.UseStablePlayback, rollbacks > 0
                        ? NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed : NativeDvPlaybackStatus.PreviousRuntimeUnavailable, recovery.Explanation);
                RecordRecovery(recovery, resumeAt, run, kind, "native generation " + (attempt.GenerationId ?? "unknown"),
                    recovery.Step == PlaybackRecoveryStep.RetryOnPreviousNative
                        ? "native generation " + (fallback?.GenerationId ?? "unknown") : "stable player");
            }
            else
            {
                if (verdict.RollbackCandidate && rollbacks < NativeDvRollbackPolicy.MaximumRollbacks)
                {
                    fallback = NativeDolbyVision?.Lifecycle.ResolveFallback(attempt.GenerationId, attempted, NativeHealth);
                    LastNativeFallback = fallback;
                }
                decision = NativeDvRollbackPolicy.Decide(verdict, rollbacks, fallback);
            }
            LastNativePlaybackStatus = decision.Status;
            if (selection.Retained && decision.Status == NativeDvPlaybackStatus.RuntimeHealthy)
                LastNativePlaybackStatus = NativeDvPlaybackStatus.RolledBackToPreviousRuntime;

            if (decision.Step == NativeDvNextStep.Finish)
            {
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(true, run.ExitCode, null);
            }

            if (attempt.Observation is not null)
            {
                int observedLine = LastReport!.FallbackHistory.IndexOf(attempt.Observation.Summary);
                if (observedLine >= 0) LastReport.FallbackHistory[observedLine] =
                    "Earlier native attempt (ended): " + attempt.Observation.Summary;
            }
            // A recovery is described by what failed during playback; the startup
            // verdict of an attempt that played is not the reason and is left to
            // diagnostics.
            // A recovery is announced when its replacement really starts, as the
            // path that launched; here only the startup path announces its decision.
            if (recovery is null)
            {
                Announce(verdict.Explanation);
                Announce(decision.Explanation);
            }

            if (decision.Step == NativeDvNextStep.UseStablePlayback)
            {
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(false, run.ExitCode, WithResume(BuildStableFallbackPlan(plan), resumeAt));
            }

            // Only a usable fallback can produce a retry, so these are invariants of
            // the policy rather than cases the product expects. Handling them as a
            // stable fallback anyway keeps a future policy change from turning a
            // missed invariant into a crash in the middle of playback.
            var retryRuntime = fallback?.Runtime;
            var retryDescriptor = fallback?.Descriptor;
            if (retryRuntime is null || retryDescriptor is null)
            {
                RedirectRecoveryToStable("The retained native runtime could not be resolved for launch; recovering with stable playback.");
                LastNativePlaybackStatus = NativeDvPlaybackStatus.PreviousRuntimeUnavailable;
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(false, run.ExitCode, WithResume(BuildStableFallbackPlan(plan), resumeAt));
            }

            string retryPipe = "adaptive-media-native-" + Guid.NewGuid().ToString("N");
            string mediaPath = plan.Arguments.SkipWhile(x => x != "--").Skip(1).FirstOrDefault() ?? "";
            var retryOutcome = NativeDvLane.WithRuntime(attempt.Outcome, retryRuntime, mediaPath,
                NativeConfigDirectory, retryPipe, NativeFallbackLogPath, plan.Target);
            if (!retryOutcome.Selected || retryOutcome.Plan is not { Supported: true, Request: not null })
            {
                RedirectRecoveryToStable("The retained native runtime validated, but no playback plan could be built for it (" +
                    (retryOutcome.Reason ?? retryOutcome.Plan?.Explanation ?? "unknown reason") + "); recovering with stable playback.");
                LastNativePlaybackStatus = NativeDvPlaybackStatus.PreviousRuntimeUnavailable;
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(false, run.ExitCode, WithResume(BuildStableFallbackPlan(plan), resumeAt));
            }

            var retryPlan = WithResume(new PlaybackPlan(retryOutcome.Plan.Executable!, retryOutcome.Plan.Arguments,
                plan.Requested, plan.Source, plan.Target, "Native Dolby Vision gpu-next (previous runtime)",
                false, false, 1,
                [retryOutcome.Plan.Explanation,
                 decision.Explanation,
                 "What this attempt delivers is established from its own evidence, not from the attempt it replaces."],
                retryPipe), resumeAt);

            if (_nativeSelections.Count >= 32) _nativeSelections.Clear();
            _nativeSelections[retryPipe] = new(retryOutcome, fallback?.GenerationId, selection.StableExecutable, true);
            Announce($"Retrying on the retained native runtime {retryDescriptor.MpvVersion}.");
            rollbacks++;
            plan = retryPlan;
            LastReport!.Plan = retryPlan;
        }
    }

    /// <summary>The final health of the attempt that ran last, plus the worst
    /// condition it passed through when that differs.</summary>
    private string HealthSummaryLine()
    {
        if (_finalHealth is not { } health) return "";
        string line = "\n" + PlaybackHealthText.Describe(health.State);
        if (health.WorstCondition is not (SustainedPlaybackHealth.Healthy or SustainedPlaybackHealth.Starting) &&
            health.WorstCondition != health.State)
            line += " (during playback: " + PlaybackHealthText.Describe(health.WorstCondition)["Playback health: ".Length..] + ")";
        return line;
    }

    /// <summary>A fresh plan that starts at the resume point. Only ever applied to a
    /// plan that was just built for the replacement attempt.</summary>
    private static PlaybackPlan WithResume(PlaybackPlan plan, double? resumeAt)
    {
        if (resumeAt is not double at) return plan;
        var arguments = plan.Arguments.Where(x => !x.StartsWith("--start=", StringComparison.Ordinal)).ToList();
        int split = arguments.IndexOf("--");
        arguments.Insert(split < 0 ? arguments.Count : split, PlaybackRecoveryText.StartArgument(at));
        return plan with { Arguments = [.. arguments],
            Reasons = plan.Reasons.Add("Resumed near " + PlaybackRecoveryText.Position(at) + " after automatic recovery.") };
    }

    private void RecordRecovery(PlaybackRecoveryDecision decision, double? resumeAt, AttemptRun run, PlaybackAttemptKind failedKind,
        string failed, string target)
    {
        var health = run.Health;
        lock (_truthGate)
        {
            LastRecovery = new(health?.AttemptId ?? 0, failed, decision.Trigger.ToString(), decision.Step.ToString(),
                resumeAt, target, decision.Explanation, failedKind);
            LastReport?.Recovery.Add(LastRecovery);
        }
        DiagnosticsStore.Event("warning", "playback-recovery",
            $"{decision.Step} after {decision.Trigger} (attempt={health?.AttemptId}, failed={failed}, target={target}, " +
            $"resume={(resumeAt is double r ? r.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "none")}, " +
            $"confirmedProgress={health?.PlaybackProgressed}): {decision.Explanation}");
    }

    /// <summary>The selected previous runtime could not be launched after all, so the
    /// pending recovery is now a recovery to stable playback. Recorded before the
    /// stable plan is even built, so no surface can claim the previous runtime.</summary>
    private void RedirectRecoveryToStable(string explanation)
    {
        lock (_truthGate)
        {
            if (LastReport is null) return;
            int index = LastReport.Recovery.FindLastIndex(r => r.LaunchedAttempt is null && r.Step == nameof(PlaybackRecoveryStep.RetryOnPreviousNative));
            if (index < 0) return;
            LastRecovery = LastReport.Recovery[index] = LastReport.Recovery[index] with
            {
                Step = nameof(PlaybackRecoveryStep.UseStablePlayback), Target = "stable player", Explanation = explanation,
            };
        }
        DiagnosticsStore.Event("warning", "playback-recovery", explanation);
    }

    /// <summary>Bind the pending recovery to the attempt that actually started. Only
    /// a replacement whose process really launched is ever reported as the path
    /// recovery took, with the resume point from its own launched plan.</summary>
    private void BindPendingRecovery(PlaybackAttemptEvidence started)
    {
        PlaybackRecoveryRecord? bound = null;
        lock (_truthGate)
        {
            if (LastReport is null) return;
            int index = LastReport.Recovery.FindLastIndex(r => r.LaunchedAttempt is null &&
                r.Step != nameof(PlaybackRecoveryStep.NoRecoveryAvailable) && r.FailedAttempt != started.AttemptId);
            if (index < 0) return;
            double? resume = started.Plan.Arguments.TakeWhile(x => x != "--").LastOrDefault(x => x.StartsWith("--start=", StringComparison.Ordinal)) is { } start &&
                double.TryParse(start[8..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double at) ? at : null;
            var record = LastReport.Recovery[index];
            bound = record with
            {
                // The step follows what launched, whatever was selected.
                Step = started.Kind == PlaybackAttemptKind.NativePrevious ? nameof(PlaybackRecoveryStep.RetryOnPreviousNative) : nameof(PlaybackRecoveryStep.UseStablePlayback),
                LaunchedAttempt = started.AttemptId, LaunchedKind = started.Kind, LaunchedRuntime = started.Runtime, LaunchedResumeAt = resume,
            };
            LastRecovery = LastReport.Recovery[index] = bound;
        }
        Announce(PlaybackHealthText.Describe(Enum.Parse<SustainedPlaybackHealth>(bound.Trigger)) + "\nRecovery: " + PlaybackTruthBuilder.RecoveryOutcome(bound));
    }

    /// <summary>A stable attempt that failed hard is reported, never relaunched.</summary>
    private void ReportStableHardFailure(AttemptRun run)
    {
        if (run.Health is not { PlaybackProgressed: true } health) return;
        var trigger = PlaybackRecoveryPolicy.IsTrigger(health.State, true) ? health.State
            : health.WorstCondition is SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen ? health.WorstCondition
            : (SustainedPlaybackHealth?)null;
        if (trigger is null) return;
        var decision = PlaybackRecoveryPolicy.Decide(trigger.Value, true, PlaybackAttemptKind.Stable, 0, 0, null);
        RecordRecovery(decision, null, run, PlaybackAttemptKind.Stable, "stable player", "none");
        Announce(PlaybackRecoveryText.Describe(decision, null));
    }

    /// <summary>Stop an owned player for recovery: a graceful quit when the player
    /// still answers, a bounded wait, then termination of exactly this process —
    /// never any other player, whatever its name.</summary>
    private async Task StopForRecoveryAsync(Process process, string pipeName, bool graceful)
    {
        if (graceful)
        {
            try
            {
                await using var ipc = new MpvIpc(pipeName);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ipc.ConnectAsync(timeout.Token);
                await ipc.CommandAsync(["quit"], timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or
                System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        try
        {
            using var wait = new CancellationTokenSource(HealthPolicy.RecoveryStopGrace);
            await process.WaitForExitAsync(wait.Token);
            return;
        }
        catch (OperationCanceledException) { }
        try
        {
            process.Kill(entireProcessTree: false);
            DiagnosticsStore.Event("warning", "playback-recovery", "The unresponsive player was terminated for recovery.");
        }
        catch (InvalidOperationException) { /* Already exited. */ }
        catch (System.ComponentModel.Win32Exception) { /* Exiting; nothing more to do. */ }
    }

    private void Announce(string message)
    {
        if (LastReport is not null && !LastReport.FallbackHistory.Contains(message)) LastReport.FallbackHistory.Add(message);
        StatusChanged?.Invoke(message);
    }

    /// <summary>The one retry for an RTX player that failed before playing: the
    /// compatibility path, with no NVIDIA processing. An intent is replaced with the
    /// planner's own Compatibility intent; left in place, the planner would decide
    /// the retry afresh and override the compatibility profile with the very
    /// enhanced path that just failed, while the announcement said compatibility.</summary>
    internal static PlaybackPlan CompatibilityRetryPlan(PlaybackPlan failed, string configDir)
    {
        string[] items = failed.Arguments.SkipWhile(x => x != "--").Skip(1).ToArray();
        var options = failed.Requested with
        {
            Profile = "Compatibility", UpscaleMode = "HighQuality", RtxHdr = false, MotionMode = "Off",
            Intent = failed.Requested.Intent is null ? null : EnhancementIntent.ForCompatibility(),
        };
        var retry = PlaybackPlanBuilder.Build(failed.Executable, configDir, items, options, failed.Source, failed.Target,
            new(false, false), "adaptive-media-" + Guid.NewGuid().ToString("N"));
        // The truth chain explains a path from its plan, so the plan carries why it exists.
        return retry with { Reasons = retry.Reasons.Insert(0, "The RTX player failed before playback started; this is the single retry on the compatibility path, without NVIDIA processing.") };
    }

    /// <summary>The established stable path for this media, used once the native
    /// lane has run out of runtimes to try. Conservative capabilities, exactly as
    /// the existing compatibility retry uses.</summary>
    private PlaybackPlan BuildStableFallbackPlan(PlaybackPlan native)
    {
        string[] items = native.Arguments.SkipWhile(x => x != "--").Skip(1).ToArray();
        return PlaybackPlanBuilder.Build(_nativeSelections[native.PipeName].StableExecutable, Path.Combine(AppContext.BaseDirectory, "mpv-config"), items,
            native.Requested, native.Source, native.Target, new(false, false),
            "adaptive-media-" + Guid.NewGuid().ToString("N"));
    }

    private async Task<AttemptRun> RunOnceAsync(PlaybackPlan plan, double? stopAfterSeconds,
        PlaybackRecoveryGate gate, PlaybackAttemptKind kind)
    {
        LastReport!.Attempts.Add(plan);
        LastReport.Observed.Clear();
        LastReport.MpvVersion = "unknown";
        LastReport.Error = null;
        bool isNative = _nativeAttempts.ContainsKey(plan.PipeName);
        var clock = Stopwatch.StartNew();
        // Every attempt, native or stable, gets a fresh monitor and classifier, so no
        // earlier attempt's samples or verdict can reach this one.
        string runtime = isNative && _nativeAttempts.TryGetValue(plan.PipeName, out var owner)
            ? "native generation " + (owner.GenerationId ?? "unknown") : "stable player";
        var health = new PlaybackHealthMonitor(Interlocked.Increment(ref _healthAttempts), plan.PipeName, runtime, HealthPolicy);
        gate.Activate(health.AttemptId);
        health.Transitioned += transition => ReportHealth(plan, health, transition);
        _health = health;
        PlaybackAttemptEvidence evidence;
        lock (_truthGate)
        {
            // This attempt's own evidence. The previous current attempt becomes
            // history; nothing it observed can be read as this attempt's.
            if (_evidence is not null && !_attemptEvidence.Contains(_evidence)) _attemptEvidence.Add(_evidence);
            while (_attemptEvidence.Count > PlaybackTruthBuilder.MaximumHistory) _attemptEvidence.RemoveAt(0);
            evidence = new PlaybackAttemptEvidence(health.AttemptId, LastReport.Attempts.Count, plan, kind, runtime,
                isNative && _nativeSelections.TryGetValue(plan.PipeName, out var chosen) ? chosen.Outcome.Runtime?.MpvVersion : null,
                isNative && _playbackFelRequested);
            _evidence = evidence;
        }
        evidence.TrackHealth(health.Snapshot);
        // Launch the exact immutable argument vector; do not probe or rebuild it here.
        Process? started;
        try
        {
            started = Process.Start(NativeProcess.StartInfo(plan.Executable, plan.Arguments));
        }
        catch (Exception ex) when (isNative && ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A native runtime that cannot be started is a health signal, not an
            // error for the user: the lane still has a fallback to try.
            DiagnosticsStore.Event("warning", "native-dv-launch", "The native runtime could not be started: " + ex.Message);
            EndHealth(health, clock, false, -1);
            evidence.Finish(health.Snapshot());
            gate.Retire(health.AttemptId);
            return new(-1, false, clock.Elapsed, null, health.Snapshot());
        }
        catch
        {
            evidence.Finish(health.Snapshot());
            EndHealth(health, clock, false, -1);
            gate.Retire(health.AttemptId);
            throw;
        }
        if (started is null)
        {
            EndHealth(health, clock, false, -1);
            evidence.Finish(health.Snapshot());
            gate.Retire(health.AttemptId);
            if (isNative) return new(-1, false, clock.Elapsed, null, health.Snapshot());
            throw new IOException("Could not start the player.");
        }
        using var process = started;
        DiagnosticsStore.Event("info", "player-started", "Player process started.");
        evidence.Started = true;
        BindPendingRecovery(evidence);
        // A stall or freeze of playback that had started claims this playback's
        // recovery for this attempt, once, and stops exactly this process. Stable
        // playback has nowhere to go in V1, so it is only reported.
        SustainedPlaybackHealth? claimed = null;
        Task recoveryStop = Task.CompletedTask;
        if (kind != PlaybackAttemptKind.Stable)
            health.Transitioned += transition =>
            {
                if (transition.To is not (SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen) ||
                    !PlaybackRecoveryPolicy.IsTrigger(transition.To, health.Snapshot().PlaybackProgressed) ||
                    !gate.TryClaim(health.AttemptId))
                    return;
                claimed = transition.To;
                health.MarkRecoveryStop();
                DiagnosticsStore.Event("warning", "playback-recovery",
                    $"Stopping attempt {health.AttemptId} ({health.Runtime}) to recover from {transition.To}.");
                StatusChanged?.Invoke(plan.Summary + "\n" + PlaybackHealthText.Describe(transition.To) + "\nRecovering playback…");
                recoveryStop = StopForRecoveryAsync(process, plan.PipeName, graceful: transition.To == SustainedPlaybackHealth.Stalled);
            };
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task monitor = MonitorAsync(plan, stopAfterSeconds, cancellation.Token, connected, health, evidence, process);
        Task sampling = health.RunAsync(connected.Task, clock, cancellation.Token);
        await process.WaitForExitAsync();
        DiagnosticsStore.Event("info", "player-exited", "Player process exited.");
        cancellation.Cancel();
        try { await monitor; } catch (OperationCanceledException) { }
        try { await sampling; } catch (OperationCanceledException) { }
        await recoveryStop;
        EndHealth(health, clock, true, process.ExitCode);
        evidence.Finish(health.Snapshot());
        LastReport!.Delivery.Add(new(health.AttemptId, runtime, evidence.Delivery().Select(v => v.Describe()).ToArray()));
        var final = health.Snapshot();
        // A player that died after it had played claims recovery at exit — unless a
        // stall or freeze already claimed it, in which case this exit is the one
        // recovery caused and must not trigger a second.
        if (claimed is null && kind != PlaybackAttemptKind.Stable &&
            final.State == SustainedPlaybackHealth.RuntimeFailure && PlaybackRecoveryPolicy.IsTrigger(final.State, final.PlaybackProgressed) &&
            gate.TryClaim(health.AttemptId))
            claimed = final.State;
        gate.Retire(health.AttemptId);
        DiagnosticsStore.Event("info", "monitor-ended", "Player observation ended.");
        await stdout; string error = await stderr;
        if (process.ExitCode != 0) { LastReport!.Error = error; DiagnosticsStore.Event("error", "playback-exit", $"mpv exited with {process.ExitCode}"); }
        return new(process.ExitCode, true, clock.Elapsed, claimed, final);
    }

    /// <summary>Close an attempt's health with how its process ended, and keep the
    /// final snapshot in the report. Only this attempt's monitor is ever finished.</summary>
    private void EndHealth(PlaybackHealthMonitor health, Stopwatch clock, bool started, int exitCode)
    {
        health.Finish(clock.Elapsed, started, exitCode);
        var snapshot = health.Snapshot();
        _finalHealth = snapshot;
        if (ReferenceEquals(_health, health)) _health = null;
        LastReport?.PlaybackHealth.Add(PlaybackHealthReport.From(snapshot, health.Runtime));
    }

    /// <summary>One line per transition, never per poll.</summary>
    private void ReportHealth(PlaybackPlan plan, PlaybackHealthMonitor health, PlaybackHealthTransition transition)
    {
        bool concerning = transition.To is SustainedPlaybackHealth.SustainedDegradation or SustainedPlaybackHealth.Stalled or
            SustainedPlaybackHealth.Frozen or SustainedPlaybackHealth.RuntimeFailure or SustainedPlaybackHealth.SourceFailure;
        DiagnosticsStore.Event(concerning ? "warning" : "info", "playback-health",
            $"{transition.From} -> {transition.To} at {transition.At.TotalSeconds:0.0} s: {transition.Reason} " +
            $"(attempt={health.AttemptId}, runtime={health.Runtime})");
        if (!PlaybackHealthClassifier.IsTerminal(transition.To))
            StatusChanged?.Invoke(plan.Summary + "\n" + PlaybackHealthText.Describe(transition.To));
    }

    private async Task MonitorAsync(PlaybackPlan plan, double? stopAfterSeconds, CancellationToken cancellation,
        TaskCompletionSource connectedSignal, PlaybackHealthMonitor health, PlaybackAttemptEvidence evidence, Process process)
    {
        try
        {
            await using var ipc = new MpvIpc(plan.PipeName);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ipc.ConnectAsync(connectTimeout.Token);
            DiagnosticsStore.Event("info", "ipc-connected", "Player observation connected.");
            if (_nativeAttempts.TryGetValue(plan.PipeName, out var connected)) connected.IpcConnected = true;
            connectedSignal.TrySetResult();
            var timer = Stopwatch.StartNew();
            // This attempt's own property snapshot. A property the player stops
            // reporting is removed rather than left over from an earlier poll, so a
            // later source (an audio-only playlist item, say) cannot inherit it.
            var observed = new Dictionary<string, JsonElement>();
            while (!cancellation.IsCancellationRequested)
            {
                using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); queryTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                foreach (string name in new[] { "mpv-version", "gpu-api", "gpu-context", "hwdec-current", "vf", "video-codec", "video-params", "video-out-params", "osd-dimensions",
                    "display-fps", "estimated-display-fps", "vsync-jitter", "frame-drop-count", "decoder-frame-drop-count", "vo-delayed-frame-count", "video-sync",
                    "interpolation", "audio-out-params", "current-ao", "current-vo", "video-target-params", "user-data/adaptive/state",
                    // Delivery evidence: what the renderer and timing actually did.
                    "time-pos", "playlist-pos", "display-sync-active", "video-speed-correction", "vo-passes",
                    "user-data/adaptive/source-epoch", "user-data/adaptive/rtx-sr", "user-data/adaptive/rtx-hdr" })
                {
                    var data = await ipc.CommandAsync(["get_property", name], queryTimeout.Token);
                    if (data.HasValue)
                    {
                        observed[name] = name == "vo-passes" ? DeliveryEvidence.CompactPasses(data.Value) : data.Value;
                        if (name == "mpv-version" && data.Value.ValueKind == JsonValueKind.String)
                            LastReport!.MpvVersion = data.Value.GetString() ?? "unknown";
                    }
                    else observed.Remove(name);
                }
                LastReport!.Observed = new(observed);
                if (plan.Requested.MotionMode != "Off" && observed.TryGetValue("video-sync", out var sync) && sync.ValueKind == JsonValueKind.String && sync.GetString() == "audio")
                {
                    const string reason = "Smooth motion was disabled because display timing became unstable.";
                    if (!LastReport.FallbackHistory.Contains(reason)) { LastReport.FallbackHistory.Add(reason); StatusChanged?.Invoke(reason); DiagnosticsStore.Event("warning", "motion-fallback", reason); }
                }
                if (_nativeAttempts.TryGetValue(plan.PipeName, out var attempt) &&
                    attempt.Outcome.Plan?.Request is not null && NativeDolbyVision?.Descriptor is not null)
                {
                    string? version = observed.TryGetValue("mpv-version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    // Health signals are refreshed on every poll, not only until the
                    // observation settles. Composition can be established from the
                    // first frames while the output chain is still coming up, so
                    // latching these to the settling moment would lose a signal the
                    // healthy-start threshold depends on.
                    if (version is not null) attempt.AdapterSupported = NativeDvDiagnosticAdapters.For(version) is not null;
                    if (observed.TryGetValue("video-codec", out var codec) && codec.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(codec.GetString()))
                        attempt.VideoDecoderObserved = true;
                    // mpv only reports real video output dimensions once a decoded
                    // frame has configured the output chain, which is the closest
                    // thing to "a picture reached the display" available here.
                    if (observed.TryGetValue("video-out-params", out var vo) && vo.ValueKind == JsonValueKind.Object &&
                        vo.TryGetProperty("w", out var width) && width.TryGetInt32(out int pixels) && pixels > 0)
                        attempt.VideoOutputConfigured = true;
                }
                if (attempt is not null && (attempt.Observation is null || attempt.ProvisionalUntil is not null) &&
                    attempt.Outcome.Plan?.Request is not null && NativeDolbyVision?.Descriptor is not null)
                {
                    string? version = observed.TryGetValue("mpv-version", out var v2) && v2.ValueKind == JsonValueKind.String ? v2.GetString() : null;
                    // This attempt's own log, never a shared one: the file is
                    // removed before launch and belongs to this attempt alone.
                    string log = ReadSharedText(attempt.LogPath);
                    var observation = NativeDvLogEvidence.Reduce(log, version,
                        attempt.Outcome.Plan.Request.EnhancementLayer,
                        observed.TryGetValue("hwdec-current", out var hw) && hw.ValueKind == JsonValueKind.String ? hw.GetString() : null,
                        observed.TryGetValue("gpu-api", out var api) ? api.ToString() : null,
                        observed.TryGetValue("gpu-context", out var ctx) ? ctx.ToString() : null);
                    // Only settle once the renderer has actually reported; before that
                    // an all-Unknown reading would just be "too early", not a result.
                    bool full = observation.Delivered == NativeDvDelivered.FullEnhancementLayer;
                    if (observation.Renderer == DvObservedState.Active && attempt.Observation is null)
                    {
                        attempt.Observation = observation;
                        LastNativeObservation = observation;
                        LastReport.FallbackHistory.Add(observation.Summary);
                        StatusChanged?.Invoke(observation.Summary);
                        DiagnosticsStore.Event("info", "native-dv-observed", observation.Summary);
                        if (!full && attempt.Outcome.Plan.Request.EnhancementLayer)
                            attempt.ProvisionalUntil = timer.Elapsed + ObservationUpgradeWindow;
                    }
                    else if (attempt.Observation is not null && full && ReferenceEquals(LastNativeObservation, attempt.Observation))
                    {
                        // Upgrade only, and only from this attempt's own log: a result
                        // is never downgraded, and never taken from another attempt.
                        int line = LastReport.FallbackHistory.IndexOf(attempt.Observation.Summary);
                        if (line >= 0) LastReport.FallbackHistory[line] = observation.Summary;
                        attempt.Observation = observation;
                        LastNativeObservation = observation;
                        StatusChanged?.Invoke(observation.Summary);
                        DiagnosticsStore.Event("info", "native-dv-observed", observation.Summary + " (established after the first frames)");
                    }
                    if (attempt.Observation is not null && (full || attempt.ProvisionalUntil is null || timer.Elapsed >= attempt.ProvisionalUntil))
                    {
                        attempt.ProvisionalUntil = null;
                        // Composition state does not change mid-file, so stop the
                        // diagnostic log growing for the rest of a feature-length film.
                        await ipc.CommandAsync(["set_property", "msg-level", "all=no"], queryTimeout.Token);
                    }
                }
                // This attempt's evidence only: the property snapshot was cleared when
                // the attempt began, and the native facts are this attempt's own.
                evidence.Observe(observed, attempt?.Observation is { } seen ? CompositionFacts(seen) : null);
                if (observed.TryGetValue("user-data/adaptive/state", out var state) && state.ValueKind == JsonValueKind.String)
                    StatusChanged?.Invoke(plan.Summary + "\n" + state.GetString() + "\n" + PlaybackHealthText.Describe(health.Snapshot().State));
                if (stopAfterSeconds.HasValue && timer.Elapsed.TotalSeconds >= stopAfterSeconds)
                {
                    bool stopped = await ipc.CommandSucceededAsync(["quit"], queryTimeout.Token);
                    if (attempt is not null) attempt.StopRequested = stopped;
                    if (stopped) health.MarkStopRequested();
                    return;
                }
                await Task.Delay(1000, cancellation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // A player that is closing ends this connection as a matter of course;
            // only a live player that stopped answering is worth a line, once.
            // Sustained health keeps its own connection and is unaffected.
            if (!cancellation.IsCancellationRequested && !process.WaitForExit(500))
            {
                const string line = "Detailed live diagnostics stopped; playback health monitoring continues.";
                if (!LastReport!.FallbackHistory.Contains(line)) LastReport.FallbackHistory.Add(line);
                DiagnosticsStore.Event("warning", "ipc-unavailable", ex.ToString());
            }
        }
    }

    private static NativeCompositionFacts CompositionFacts(NativeDvObservation o) => new(
        o.Renderer == DvObservedState.Active, o.DecoderInstances, o.BlElPairing == DvObservedState.Active,
        o.Delivered == NativeDvDelivered.FullEnhancementLayer, o.Delivered.ToString(), o.HardwareDecoderInUse);

    /// <summary>Read a file the player still holds open for writing.</summary>
    private static string ReadSharedText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    // The inventory helper can fail and leave a default summary, so confirm PATH before claiming yt-dlp is absent.
    private static bool HasStreamHelper(SystemSummary system) => system.YtDlpAvailable ||
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(x => !string.IsNullOrWhiteSpace(x) && File.Exists(Path.Combine(x.Trim('"'), "yt-dlp.exe")));

    public static string ResolveMpv(string? hint = null)
    {
        var candidates = new List<string?> { hint, Path.Combine(AppContext.BaseDirectory, "tools", "mpv.exe"), @"C:\mpv\mpv.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mpv", "mpv.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(x => Path.Combine(x.Trim('"'), "mpv.exe")));
        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x)) ?? throw new FileNotFoundException("mpv was not found. Run the installer and select the MPV component.");
    }
}

