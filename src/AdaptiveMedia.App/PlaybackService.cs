using System.Diagnostics;
using System.Text.Json;
namespace AdaptiveMedia;

public sealed class PlaybackService
{
    public event Action<string>? StatusChanged;
    public SessionDiagnostics? LastReport { get; private set; }
    private readonly Dictionary<string, (DateTime Stamp, bool Vpp, string Version)> _capabilities = [];
    private readonly Dictionary<string, (DateTime Stamp, long Length, MediaInfo Media)> _mediaCache = [];
    private readonly Dictionary<string, SessionDiagnostics> _preparedReports = [];
    private readonly Dictionary<string, NativeAttempt> _nativeAttempts = [];

    /// <summary>What one native playback attempt asked for, and what it went on to
    /// establish. Each attempt owns its own record, so a retry on another
    /// generation starts from nothing and cannot inherit an earlier attempt's
    /// observations.</summary>
    private sealed class NativeAttempt
    {
        public required NativeDvLaneOutcome Outcome { get; init; }
        public required string LogPath { get; init; }
        public required string? GenerationId { get; init; }
        public bool IpcConnected { get; set; }
        public bool AdapterSupported { get; set; }
        public bool VideoDecoderObserved { get; set; }
        public bool VideoOutputConfigured { get; set; }
        public NativeDvObservation? Observation { get; set; }
    }

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

    public static string NativeConfigDirectory => Path.Combine(SettingsStore.DirectoryPath, "native-dv-config");

    /// <summary>The diagnostic log for the first native attempt of a playback.</summary>
    public static string NativeLogPath => Path.Combine(DiagnosticsStore.DirectoryPath, "native-dv.log");

    /// <summary>The diagnostic log for a rollback attempt. A separate file, and
    /// both are removed before their launch, because composition is read from log
    /// text: if the two attempts shared a file, the failed generation's
    /// composition line could be read back as the fallback's own evidence and a
    /// Full FEL result would be inherited rather than observed.</summary>
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
        var target = destination ?? new PlaybackTarget(selected is null ? 0 : fullscreen ? selected.Width : (int)(selected.WorkWidth * .8),
            selected is null ? 0 : fullscreen ? selected.Height : (int)(selected.WorkHeight * .8), screen, fullscreen, hdrEnabled);
        // The native Dolby Vision lane is opt-in, applies to a single local file, and
        // produces a plan only once a provisioned runtime has validated. Any refusal
        // falls through to the established stable path with the reason recorded.
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
                string nativeLog = NativeLogPath;

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
                            nativeLog = NativeFallbackLogPath;
                            string note = $"The current native Dolby Vision runtime failed {NativeHealth.FaultCount(LastNativeLifecycle?.CurrentGeneration)} times in this session; starting on the retained previous runtime.";
                            StatusChanged?.Invoke(note);
                            DiagnosticsStore.Event("warning", "native-dv-health", note);
                        }
                    }
                }

                DiagnosticsStore.Event("info", "native-dv", "Native Dolby Vision runtime selected.");
                var nativePlan = new PlaybackPlan(outcome.Plan.Executable!, outcome.Plan.Arguments,
                    options with { AutoHdrSwitch = settings.AutoHdrSwitch }, source, target,
                    "Native Dolby Vision gpu-next", false, false, 1,
                    [outcome.Plan.Explanation,
                     "Full enhancement-layer composition is requested; what it actually delivers is reported after playback starts."],
                    nativePipe);
                if (_preparedReports.Count >= 32) _preparedReports.Clear();
                if (_nativeAttempts.Count >= 32) _nativeAttempts.Clear();
                _nativeAttempts[nativePipe] = new NativeAttempt
                {
                    Outcome = outcome,
                    LogPath = nativeLog,
                    GenerationId = NativeDvRuntimeLifecycle.IsGenerationId(generation) ? generation : null,
                };
                _preparedReports[nativePipe] = new() { Plan = nativePlan, MpvVersion = outcome.Runtime!.MpvVersion,
                    Summary = nativePlan.Summary, Hardware = new { system.Gpu, system.Cpu, system.Drivers, system.Screens } };
                return nativePlan;
            }
            DiagnosticsStore.Event("info", "native-dv", "Native Dolby Vision was not used: " + (outcome.Reason ?? "unknown reason"));
        }
        var plan = PlaybackPlanBuilder.Build(mpv, Path.Combine(AppContext.BaseDirectory, "mpv-config"), expanded, options with { AutoHdrSwitch = settings.AutoHdrSwitch }, source, target,
            new(system.HasNvidia, capability.Vpp, system.NvidiaAdapter, system.NvidiaAdapter?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true),
            "adaptive-media-" + Guid.NewGuid().ToString("N"), settings.HdmiBitstream, HasStreamHelper(system));
        if (_preparedReports.Count >= 32) _preparedReports.Clear();
        _preparedReports[plan.PipeName] = new() { Plan = plan, MpvVersion = capability.Version, Summary = plan.Summary,
            Hardware = new { system.Gpu, system.Cpu, system.Drivers, system.Screens, system.Audio, system.Power,
                Topology = "GPU inventory does not establish display ownership. VRR and endpoint bitstream support are unknown." } };
        return plan;
    }

    public async Task<int> LaunchAsync(PlaybackPlan plan, double? stopAfterSeconds = null)
    {
        _preparedReports.TryGetValue(plan.PipeName, out var prepared);
        LastNativeObservation = null;
        LastNativeHealth = null;
        LastNativePlaybackStatus = NativeDvPlaybackStatus.NotUsed;
        LastReport = new() { Plan = plan, Summary = plan.Summary, MpvVersion = prepared?.MpvVersion ?? "unknown", Hardware = prepared?.Hardware };
        using var hdrSession = HdrSession.Begin(plan, LastReport);
        DiagnosticsStore.Event("info", "playback-plan", plan.Renderer);
        StatusChanged?.Invoke(plan.Summary);

        if (_nativeAttempts.ContainsKey(plan.PipeName))
        {
            var native = await RunNativeWithRollbackAsync(plan, stopAfterSeconds);
            if (native.Handled)
            {
                LastReport.ExitCode = native.ExitCode;
                LastReport.Summary = (LastReport.Plan?.Summary ?? "Playback") + "\n" +
                    (native.ExitCode == 0 ? "Playback ended." : $"Playback failed ({native.ExitCode}).") +
                    "\n" + string.Join("\n", LastReport.FallbackHistory);
                try { DiagnosticsStore.Save(LastReport); }
                catch (IOException) { StatusChanged?.Invoke("Playback ended; diagnostics could not be saved."); }
                return native.ExitCode;
            }
            plan = native.StablePlan!;
            LastReport.Plan = plan;
            StatusChanged?.Invoke(plan.Summary);
        }

        int code = (await RunOnceAsync(plan, stopAfterSeconds)).ExitCode;
        if (code != 0 && plan.Renderer == "RTX D3D11")
        {
            const string reason = "RTX playback failed. Retrying once with compatibility video and PCM audio.";
            LastReport.FallbackHistory.Add(reason); StatusChanged?.Invoke(reason);
            DiagnosticsStore.Event("warning", "fallback", reason);
            string[] items = plan.Arguments.SkipWhile(x => x != "--").Skip(1).ToArray();
            var fallback = PlaybackPlanBuilder.Build(plan.Executable, Path.Combine(AppContext.BaseDirectory, "mpv-config"), items,
                plan.Requested with { Profile = "Compatibility", UpscaleMode = "HighQuality", RtxHdr = false, MotionMode = "Off" }, plan.Source, plan.Target,
                new(false, false), "adaptive-media-" + Guid.NewGuid().ToString("N"));
            LastReport.Plan = fallback;
            code = (await RunOnceAsync(fallback, stopAfterSeconds)).ExitCode;
        }
        LastReport.ExitCode = code;
        LastReport.Summary = (LastReport.Plan?.Summary ?? "Playback") + "\n" + (code == 0 ? "Playback ended." : $"Playback failed ({code}).") +
            "\n" + string.Join("\n", LastReport.FallbackHistory);
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
    private async Task<NativeRunResult> RunNativeWithRollbackAsync(PlaybackPlan plan, double? stopAfterSeconds)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rollbacks = 0;

        while (true)
        {
            var attempt = _nativeAttempts[plan.PipeName];
            if (attempt.GenerationId is not null) attempted.Add(attempt.GenerationId);

            // Each attempt reads its own evidence and nothing else. Removing the
            // log before launch is what stops a composition line written by a
            // previous attempt from being read back as this one's own.
            TryDeleteDiagnosticLog(attempt.LogPath);
            LastNativeObservation = null;

            // Hold the generation for the life of the attempt so cleanup, in this
            // process or another, cannot remove it while it is playing.
            using var pin = NativeDolbyVision?.Lifecycle.Store.PinGeneration(attempt.GenerationId);

            var run = await RunOnceAsync(plan, stopAfterSeconds);

            var verdict = NativeDvHealthEvaluator.Evaluate(new NativeDvHealthSignals
            {
                LaneSelected = true,
                ProcessStarted = run.Started,
                IpcConnected = attempt.IpcConnected,
                RendererObserved = attempt.Observation?.Renderer == DvObservedState.Active,
                VideoDecoderObserved = attempt.VideoDecoderObserved,
                VideoOutputConfigured = attempt.VideoOutputConfigured,
                ObservationAdapterSupported = attempt.AdapterSupported,
                StopRequested = stopAfterSeconds.HasValue,
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
            if (verdict.RollbackCandidate && rollbacks < NativeDvRollbackPolicy.MaximumRollbacks)
            {
                fallback = NativeDolbyVision?.Lifecycle.ResolveFallback(attempt.GenerationId, attempted, NativeHealth);
                LastNativeFallback = fallback;
            }

            var decision = NativeDvRollbackPolicy.Decide(verdict, rollbacks, fallback);
            LastNativePlaybackStatus = decision.Status;

            if (decision.Step == NativeDvNextStep.Finish)
            {
                Announce(NativeDvPlaybackStatusText.Describe(decision.Status));
                return new(true, run.ExitCode, null);
            }

            Announce(verdict.Explanation);
            Announce(decision.Explanation);

            if (decision.Step == NativeDvNextStep.UseStablePlayback)
            {
                Announce(NativeDvPlaybackStatusText.Describe(decision.Status));
                return new(false, run.ExitCode, BuildStableFallbackPlan(plan));
            }

            // Only a usable fallback can produce a retry, so these are invariants of
            // the policy rather than cases the product expects. Handling them as a
            // stable fallback anyway keeps a future policy change from turning a
            // missed invariant into a crash in the middle of playback.
            var retryRuntime = fallback?.Runtime;
            var retryDescriptor = fallback?.Descriptor;
            if (retryRuntime is null || retryDescriptor is null)
            {
                LastNativePlaybackStatus = NativeDvPlaybackStatus.PreviousRuntimeUnavailable;
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(false, run.ExitCode, BuildStableFallbackPlan(plan));
            }

            string retryPipe = "adaptive-media-native-" + Guid.NewGuid().ToString("N");
            string mediaPath = plan.Arguments.SkipWhile(x => x != "--").Skip(1).FirstOrDefault() ?? "";
            var retryOutcome = NativeDvLane.WithRuntime(attempt.Outcome, retryRuntime, mediaPath,
                NativeConfigDirectory, retryPipe, NativeFallbackLogPath, plan.Target);
            if (!retryOutcome.Selected || retryOutcome.Plan is not { Supported: true, Request: not null })
            {
                LastNativePlaybackStatus = NativeDvPlaybackStatus.PreviousRuntimeUnavailable;
                Announce(NativeDvPlaybackStatusText.Describe(LastNativePlaybackStatus));
                return new(false, run.ExitCode, BuildStableFallbackPlan(plan));
            }

            var retryPlan = new PlaybackPlan(retryOutcome.Plan.Executable!, retryOutcome.Plan.Arguments,
                plan.Requested, plan.Source, plan.Target, "Native Dolby Vision gpu-next (previous runtime)",
                false, false, 1,
                [retryOutcome.Plan.Explanation,
                 decision.Explanation,
                 "What this attempt delivers is established from its own evidence, not from the attempt it replaces."],
                retryPipe);

            if (_nativeAttempts.Count >= 32) _nativeAttempts.Clear();
            _nativeAttempts[retryPipe] = new NativeAttempt
            {
                Outcome = retryOutcome,
                LogPath = NativeFallbackLogPath,
                GenerationId = fallback?.GenerationId,
            };
            Announce($"Retrying on the retained native runtime {retryDescriptor.MpvVersion}.");
            rollbacks++;
            plan = retryPlan;
            LastReport!.Plan = retryPlan;
        }
    }

    private void Announce(string message)
    {
        if (LastReport is not null && !LastReport.FallbackHistory.Contains(message)) LastReport.FallbackHistory.Add(message);
        StatusChanged?.Invoke(message);
    }

    /// <summary>The established stable path for this media, used once the native
    /// lane has run out of runtimes to try. Conservative capabilities, exactly as
    /// the existing compatibility retry uses.</summary>
    private PlaybackPlan BuildStableFallbackPlan(PlaybackPlan native)
    {
        string[] items = native.Arguments.SkipWhile(x => x != "--").Skip(1).ToArray();
        return PlaybackPlanBuilder.Build(ResolveMpv(), Path.Combine(AppContext.BaseDirectory, "mpv-config"), items,
            native.Requested, native.Source, native.Target, new(false, false),
            "adaptive-media-" + Guid.NewGuid().ToString("N"));
    }

    private static void TryDeleteDiagnosticLog(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<(int ExitCode, bool Started, TimeSpan Lifetime)> RunOnceAsync(PlaybackPlan plan, double? stopAfterSeconds)
    {
        LastReport!.Attempts.Add(plan);
        LastReport.Observed.Clear();
        bool isNative = _nativeAttempts.ContainsKey(plan.PipeName);
        var clock = Stopwatch.StartNew();
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
            return (-1, false, clock.Elapsed);
        }
        if (started is null)
        {
            if (isNative) return (-1, false, clock.Elapsed);
            throw new IOException("Could not start the player.");
        }
        using var process = started;
        DiagnosticsStore.Event("info", "player-started", "Player process started.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource();
        Task monitor = MonitorAsync(plan, stopAfterSeconds, cancellation.Token);
        await process.WaitForExitAsync();
        DiagnosticsStore.Event("info", "player-exited", "Player process exited.");
        cancellation.Cancel();
        try { await monitor; } catch (OperationCanceledException) { }
        DiagnosticsStore.Event("info", "monitor-ended", "Player observation ended.");
        await stdout; string error = await stderr;
        if (process.ExitCode != 0) { LastReport!.Error = error; DiagnosticsStore.Event("error", "playback-exit", $"mpv exited with {process.ExitCode}"); }
        return (process.ExitCode, true, clock.Elapsed);
    }

    private async Task MonitorAsync(PlaybackPlan plan, double? stopAfterSeconds, CancellationToken cancellation)
    {
        try
        {
            await using var ipc = new MpvIpc(plan.PipeName);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ipc.ConnectAsync(connectTimeout.Token);
            DiagnosticsStore.Event("info", "ipc-connected", "Player observation connected.");
            if (_nativeAttempts.TryGetValue(plan.PipeName, out var connected)) connected.IpcConnected = true;
            var timer = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested)
            {
                using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); queryTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                foreach (string name in new[] { "mpv-version", "gpu-api", "gpu-context", "hwdec-current", "vf", "video-codec", "video-params", "video-out-params", "osd-dimensions",
                    "display-fps", "estimated-display-fps", "vsync-jitter", "frame-drop-count", "decoder-frame-drop-count", "vo-delayed-frame-count", "video-sync",
                    "interpolation", "audio-out-params", "current-ao", "user-data/adaptive/state" })
                {
                    var data = await ipc.CommandAsync(["get_property", name], queryTimeout.Token);
                    if (data.HasValue) LastReport!.Observed[name] = data.Value;
                }
                if (plan.Requested.MotionMode != "Off" && LastReport!.Observed.TryGetValue("video-sync", out var sync) && sync.ValueKind == JsonValueKind.String && sync.GetString() == "audio")
                {
                    const string reason = "Smooth motion was disabled because display timing became unstable.";
                    if (!LastReport.FallbackHistory.Contains(reason)) { LastReport.FallbackHistory.Add(reason); StatusChanged?.Invoke(reason); DiagnosticsStore.Event("warning", "motion-fallback", reason); }
                }
                if (_nativeAttempts.TryGetValue(plan.PipeName, out var attempt) &&
                    attempt.Outcome.Plan?.Request is not null && NativeDolbyVision?.Descriptor is not null)
                {
                    string? version = LastReport!.Observed.TryGetValue("mpv-version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    // Health signals are refreshed on every poll, not only until the
                    // observation settles. Composition can be established from the
                    // first frames while the output chain is still coming up, so
                    // latching these to the settling moment would lose a signal the
                    // healthy-start threshold depends on.
                    if (version is not null) attempt.AdapterSupported = NativeDvDiagnosticAdapters.For(version) is not null;
                    if (LastReport.Observed.TryGetValue("video-codec", out var codec) && codec.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(codec.GetString()))
                        attempt.VideoDecoderObserved = true;
                    // mpv only reports real video output dimensions once a decoded
                    // frame has configured the output chain, which is the closest
                    // thing to "a picture reached the display" available here.
                    if (LastReport.Observed.TryGetValue("video-out-params", out var vo) && vo.ValueKind == JsonValueKind.Object &&
                        vo.TryGetProperty("w", out var width) && width.TryGetInt32(out int pixels) && pixels > 0)
                        attempt.VideoOutputConfigured = true;
                }
                if (attempt is not null && attempt.Observation is null &&
                    attempt.Outcome.Plan?.Request is not null && NativeDolbyVision?.Descriptor is not null)
                {
                    string? version = LastReport!.Observed.TryGetValue("mpv-version", out var v2) && v2.ValueKind == JsonValueKind.String ? v2.GetString() : null;
                    // This attempt's own log, never a shared one: the file is
                    // removed before launch and belongs to this attempt alone.
                    string log = ReadSharedText(attempt.LogPath);
                    var observation = NativeDvLogEvidence.Reduce(log, version,
                        attempt.Outcome.Plan.Request.EnhancementLayer,
                        LastReport.Observed.TryGetValue("hwdec-current", out var hw) && hw.ValueKind == JsonValueKind.String ? hw.GetString() : null,
                        LastReport.Observed.TryGetValue("gpu-api", out var api) ? api.ToString() : null,
                        LastReport.Observed.TryGetValue("gpu-context", out var ctx) ? ctx.ToString() : null);
                    // Only settle once the renderer has actually reported; before that
                    // an all-Unknown reading would just be "too early", not a result.
                    if (observation.Renderer == DvObservedState.Active)
                    {
                        attempt.Observation = observation;
                        LastNativeObservation = observation;
                        LastReport.FallbackHistory.Add(observation.Summary);
                        StatusChanged?.Invoke(observation.Summary);
                        DiagnosticsStore.Event("info", "native-dv-observed", observation.Summary);
                        // Composition state does not change mid-file, so stop the
                        // diagnostic log growing for the rest of a feature-length film.
                        await ipc.CommandAsync(["set_property", "msg-level", "all=no"], queryTimeout.Token);
                    }
                }
                if (LastReport!.Observed.TryGetValue("user-data/adaptive/state", out var state) && state.ValueKind == JsonValueKind.String)
                    StatusChanged?.Invoke(plan.Summary + "\n" + state.GetString());
                if (stopAfterSeconds.HasValue && timer.Elapsed.TotalSeconds >= stopAfterSeconds) { await ipc.CommandAsync(["quit"], queryTimeout.Token); return; }
                await Task.Delay(1000, cancellation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                LastReport!.FallbackHistory.Add("Live diagnostics unavailable; playback was not interrupted.");
                DiagnosticsStore.Event("warning", "ipc-unavailable", ex.ToString());
            }
        }
    }

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

