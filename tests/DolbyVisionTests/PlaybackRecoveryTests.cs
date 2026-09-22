using AdaptiveMedia;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

// The child is only an mpv protocol peer. Preparation, attempt sequencing,
// observation, health, fallback selection, and launch all belong to production.
internal static class PlaybackRecoveryTests
{
    public static async Task<int?> ChildAsync(string[] args)
    {
        if (args.FirstOrDefault() == "--pin-child")
        {
            using var pin = new NativeDvRuntimeStore(args[1]).PinGeneration(args[2]);
            Console.WriteLine(pin is null ? "unprotected" : "protected");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        if (!args.Contains("--no-config") && !args.Any(x => x.StartsWith("--input-ipc-server="))) return null;
        string version = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "version.txt"));
        if (args.Contains("--version")) { Console.WriteLine("mpv " + version); return 0; }
        if (args.Contains("--vf=help")) return 0;
        if (args.Contains("--frames=1"))
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "require-probe-pin")))
            {
                string generation = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
                var store = new NativeDvRuntimeStore(Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName);
                if (!store.IsPinned(generation)) return 2;
            }
            Console.WriteLine("AMDV|7|6|hevc|pq|bt.2020|3840|2160");
            Console.WriteLine("AMPROBE|3840|2160|24|hevc|pq|bt.2020|aac|yuv420p10le|1.777777");
            return 0;
        }
        string mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "behavior.txt"));
        string? startArgument = args.FirstOrDefault(x => x.StartsWith("--start="))?[8..];
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launches.txt"), mode + (startArgument is null ? "" : "|start=" + startArgument) + "\n");
        if (mode == "fail") return 1;
        if (mode == "media") return 2;
        if (mode == "clean") return 0;
        string pipeName = args.Single(x => x.StartsWith("--input-ipc-server="))[19..];
        if (pipeName.StartsWith(@"\\.\pipe\")) pipeName = pipeName[9..];
        string? log = args.FirstOrDefault(x => x.StartsWith("--log-file="))?[11..];
        // Sustained-health modes compose like "full" at startup, then misbehave.
        bool composes = mode is "full" or "partial" or "reject-stop" or "freeze" or "stall" or "crash-late" or "freeze-crash";
        if (log is not null && mode != "silent")
            File.WriteAllText(log, "Initialized libplacebo test (API v371)\n" + (composes ?
                "Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track)\n[vd] Opening decoder hevc\n[vd] Opening decoder hevc\n[vd] Selected decoder: hevc\n[vf] [el_pair]\nsh_dovi_compose_nlq\n[vd] Using hardware decoding (d3d11va).\n" : ""));

        // Playback position for the sustained-health modes: ten media seconds per
        // real second from --start, so a short test covers a meaningful span.
        // late-compose: the renderer reports first and the composed frames follow,
        // as when a real launch starts mid-file.
        if (mode == "late-compose" && log is not null)
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                File.AppendAllText(log, "Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track)\n[vd] Opening decoder hevc\n[vd] Opening decoder hevc\n[vd] Selected decoder: hevc\n[vf] [el_pair]\nsh_dovi_compose_nlq\n[vd] Using hardware decoding (d3d11va).\n");
            });
        bool progresses = mode is "play" or "late-compose" or "pause" or "freeze" or "stall" or "crash-late" or "freeze-crash";
        double from = double.TryParse(startArgument, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        var gate = new object();
        Stopwatch? playing = null;
        double Elapsed() { lock (gate) return playing?.Elapsed.TotalSeconds ?? 0; }
        double Position() => from + 10 * (mode switch { "stall" or "pause" => Math.Min(Elapsed(), 0.6), _ => Elapsed() });
        bool Frozen() => mode is "freeze" or "freeze-crash" && Elapsed() > 1.0;

        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task Serve(NamedPipeServerStream pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                {
                    lock (gate) playing ??= Stopwatch.StartNew();
                    // A frozen player reads nothing more and answers nothing.
                    if (Frozen()) await Task.Delay(Timeout.Infinite, lifetime.Token);
                    using var message = JsonDocument.Parse(line);
                    var command = message.RootElement.GetProperty("command");
                    string name = command[0].GetString()!;
                    string? prop = command.GetArrayLength() > 1 ? command[1].GetString() : null;
                    object? data = prop switch
                    {
                        "mpv-version" => version,
                        "video-codec" when mode != "partial" && mode != "silent" && mode != "reject-stop" => "hevc",
                        "video-out-params" when mode != "partial" && mode != "silent" && mode != "reject-stop" => new { w = 3840, h = 2160 },
                        "hwdec-current" when composes || mode == "late-compose" => "d3d11va",
                        "gpu-api" => "d3d11",
                        "gpu-context" => "d3d11",
                        "time-pos" when progresses => Position(),
                        "duration" when progresses => 600.0,
                        "pause" when progresses => mode == "pause" && Elapsed() > 0.6,
                        "paused-for-cache" or "seeking" or "eof-reached" when progresses => false,
                        _ => null
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = message.RootElement.GetProperty("request_id").GetInt32(), error = name == "quit" && mode == "reject-stop" ? "command failed" : data is null && name == "get_property" ? "property unavailable" : "success", data }));
                    if (name == "quit") { exit.TrySetResult(mode == "reject-stop" ? 1 : 0); return; }
                    // The monitor has consumed a complete poll and, for partial, its log.
                    if (name == "set_property" && prop == "msg-level" && mode == "partial") { exit.TrySetResult(1); return; }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { await pipe.DisposeAsync(); }
        }
        // Like mpv, every client gets its own pipe instance, so the startup monitor,
        // the health sampler and a recovery stop can all connect.
        _ = Task.Run(async () =>
        {
            while (!exit.Task.IsCompleted)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(lifetime.Token); }
                catch (OperationCanceledException) { await pipe.DisposeAsync(); return; }
                _ = Serve(pipe);
            }
        });
        if (mode is "crash-late" or "freeze-crash")
            _ = Task.Run(async () =>
            {
                while (Elapsed() < (mode == "crash-late" ? 1.2 : 1.6)) await Task.Delay(20);
                Environment.Exit(unchecked((int)0xC0000005));
            });
        return await exit.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The product policy with time compressed so a sustained failure is
    /// reached in about a second. Every rule is the same; only durations shrink.</summary>
    private static readonly PlaybackHealthPolicy FastHealth = new()
    {
        SampleInterval = TimeSpan.FromMilliseconds(100), WindowLength = TimeSpan.FromMilliseconds(500),
        StallAfter = TimeSpan.FromMilliseconds(600), StartupStallAfter = TimeSpan.FromSeconds(3),
        FrozenAfter = TimeSpan.FromMilliseconds(600), ProgressToClearStall = TimeSpan.FromMilliseconds(300),
        ResumeRewindSeconds = 0.25, RecoveryStopGrace = TimeSpan.FromMilliseconds(500),
    };

    public static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "adaptive-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? prior = Environment.GetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR");
        Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", root);
        try
        {
            if (only is null or "pins") await PinsAsync(root, check);
            if (only == "pins") return;
            string storeRoot = Path.Combine(root, "runtimes", "native-dv");
            var store = new NativeDvRuntimeStore(storeRoot);
            NativeDvRuntimeDescriptor Install(char id, string mode)
            {
                string generation = new(id, 64);
                string folder = Path.Combine(storeRoot, generation);
                CopyPeer(folder);
                File.WriteAllText(Path.Combine(folder, "behavior.txt"), mode);
                if (only is null or "probe") File.WriteAllText(Path.Combine(folder, "require-probe-pin"), "");
                string exe = Path.Combine(folder, "DolbyVisionTests.exe");
                string version = id == 'a' ? "0.41.0-1044-g14f2d48cb" : "0.41.0-1042-g7e4cb538a";
                File.WriteAllText(Path.Combine(folder, "version.txt"), version);
                var descriptor = new NativeDvRuntimeDescriptor(generation, version,
                    id == 'a' ? "14f2d48cbc7dda61adb4bd181e107a1f3f76e533" : "7e4cb538a3f30d25920ad8e87ba6571540fb729f", 371, "test", "https://example.invalid", new Uri("https://example.invalid/a.7z"), generation, 1,
                    "DolbyVisionTests.exe", [new("DolbyVisionTests.exe", NativeDvRuntimeStore.ComputeSha256(exe), new FileInfo(exe).Length)]);
                store.WriteDescriptorSnapshot(descriptor);
                return descriptor;
            }
            var b = Install('b', "base");
            var a = Install('a', "full");
            File.WriteAllText(Path.Combine(storeRoot, NativeDvRuntimeLifecycle.StateFileName), JsonSerializer.Serialize(new NativeDvLifecycleRecord(a.VersionId, b.VersionId, null)));
            string stableDir = Path.Combine(root, "stable");
            CopyPeer(stableDir);
            File.WriteAllText(Path.Combine(stableDir, "behavior.txt"), "base");
            File.WriteAllText(Path.Combine(stableDir, "version.txt"), "stable-protocol-peer");
            string stableExe = Path.Combine(stableDir, "DolbyVisionTests.exe");
            string media = Path.Combine(root, "source.mkv"); File.WriteAllText(media, "protocol fixture only");
            var service = new PlaybackService { NativeDolbyVision = new NativeDvLane(store, a), HealthPolicy = FastHealth };
            var plan = await service.PrepareAsync([media], new("Reference", "Off", "Off", false, false),
                new SystemSummary { MpvPath = stableExe }, new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = false, AutoHdrSwitch = false }, new(1280, 720));
            check(plan.Executable == Path.Combine(storeRoot, a.VersionId, "DolbyVisionTests.exe"), "RECOVERY: production PrepareAsync selected current generation");
            if (only == "probe") return;
            void Mode(NativeDvRuntimeDescriptor d, string mode) => File.WriteAllText(Path.Combine(storeRoot, d.VersionId, "behavior.txt"), mode);
            if (only is null or "validation")
            {
                string marker = Path.Combine(storeRoot, NativeDvRuntimeStore.PinDirectoryName, b.VersionId + ".pin");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                using (var deleting = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    check(!new NativeDvRuntimeLifecycle(store).ResolveFallback(a.VersionId).IsUsable,
                        "RECOVERY: retained validation cannot race a generation already owned by GC");
                using (var blocked = new FileStream(Path.Combine(storeRoot, b.VersionId, b.LauncherRelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
                    check(!new NativeDvRuntimeLifecycle(store).ResolveFallback(a.VersionId).IsUsable,
                        "RECOVERY: a temporarily unreadable retained component declines fallback without throwing");
            }
            if (only is null or "replay")
            {
                bool activePinChecked = false;
                void CheckActivePin(string status)
                {
                    if (!status.Contains("composition active") || activePinChecked) return;
                    activePinChecked = true;
                    new NativeDvRuntimeLifecycle(store).CollectGarbage(null, new(null, null, null));
                    check(store.Resolve(a).IsUsable, "RECOVERY: actual playback lease protects the entire active generation during GC");
                }
                // Preserve B while deliberately removing A from GC's keep set.
                using var retainB = store.PinGeneration(b.VersionId);
                service.StatusChanged += CheckActivePin;
                await service.LaunchAsync(plan, 0);
                service.StatusChanged -= CheckActivePin;
                check(activePinChecked, "RECOVERY: active-generation GC ran during real production monitoring");
                check(service.LastNativeHealth?.ReachedUsefulPlayback == true && service.LastNativeObservation?.Delivered == NativeDvDelivered.FullEnhancementLayer, "RECOVERY: first launch establishes its own full evidence");
                string firstLog = service.LastReport!.Attempts[0].Arguments.Single(x => x.StartsWith("--log-file="));
                Mode(a, "fail");
                await service.LaunchAsync(plan, 0);
                check(service.LastReport!.Attempts.All(x => x.Arguments.Single(a => a.StartsWith("--log-file=")) != firstLog), "RECOVERY: replay and rollback use fresh log identities");
                check(service.LastReport!.Attempts.Count == 2, "RECOVERY: replay with a fresh startup failure rolls back instead of inheriting old health");
                check(service.NativeHealth.FaultCount(a.VersionId) == 1, "RECOVERY: only the new failed attempt counts against current generation");
                check(service.LastNativeObservation?.Delivered != NativeDvDelivered.FullEnhancementLayer && service.LastNativeObservation?.HardwareDecoderInUse != "d3d11va", "RECOVERY: base-layer rollback inherits no FEL or hwdec from first launch");
                check(service.LastNativePlaybackStatus == NativeDvPlaybackStatus.RolledBackToPreviousRuntime, "RECOVERY: successful base-layer rollback reports rollback independently of FEL");
            }
            if (only is null or "rejected-stop")
            {
                service.NativeHealth.Clear(a.VersionId); service.NativeHealth.Clear(b.VersionId);
                Mode(a, "reject-stop"); Mode(b, "base");
                await service.LaunchAsync(plan, 0);
                check(service.LastReport!.Attempts.Count == 2 && service.NativeHealth.FaultCount(a.VersionId) == 1,
                    "RECOVERY: rejected quit does not masquerade as user cancellation or suppress rollback");
            }
            if (only is null or "stable")
            {
                // Verify selected stable routing before allowing a synthetic launch.
                var stablePlan = (PlaybackPlan)typeof(PlaybackService).GetMethod("BuildStableFallbackPlan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [plan])!;
                check(stablePlan.Executable == stableExe, "RECOVERY: stable fallback preserves the runtime selected during preparation");
                Mode(a, "fail"); Mode(b, "partial");
                await service.LaunchAsync(plan, 0);
                check(service.LastReport!.Attempts.Count == 3 && service.LastReport.Attempts.Take(2).Select(x => x.Executable).Distinct().Count() == 2, "RECOVERY: exactly A then B then stable, with no third native launch");
                check(service.LastReport.Plan!.Executable == stableExe && service.LastReport.ExitCode == 0, "RECOVERY: stable playback wins after both native attempts fail");
                check(service.LastReport.FallbackHistory.Where(x => x.Contains("composition active")).All(x => x.StartsWith("Earlier native attempt")), "RECOVERY: prior composition is explicitly historical in the final report");
                check(service.LastNativeObservation is null && !service.LastReport.Observed.ContainsKey("hwdec-current"), "RECOVERY: stable result retains no native FEL or hwdec claim");
                check(service.LastReport.MpvVersion == "stable-protocol-peer", "RECOVERY: final runtime identity comes from the active attempt");
                var healths = service.LastReport.PlaybackHealth;
                check(healths.Count == 3 && healths.Select(x => x.Attempt).Distinct().Count() == 3 &&
                      healths[0].Runtime.Contains(a.VersionId) && healths[1].Runtime.Contains(b.VersionId) && healths[2].Runtime == "stable player",
                    "SUSTAINED HEALTH: every attempt, native and stable, has its own health record and runtime identity");
                check(healths[0].State == nameof(SustainedPlaybackHealth.RuntimeFailure) && healths[2].State == nameof(SustainedPlaybackHealth.UserStopped) &&
                      service.LastPlaybackHealth?.State == SustainedPlaybackHealth.UserStopped,
                    "SUSTAINED HEALTH: a failed native attempt's health is not donated to the stable attempt that followed");
                check(healths.All(x => !x.PlaybackProgressed && x.StallEpisodes == 0 && x.FreezeEpisodes == 0),
                    "SUSTAINED HEALTH: a peer that never reports position is never called healthy, stalled or frozen");
            }
            if (only is null or "outcomes")
            {
                service.NativeHealth.Clear(a.VersionId);
                service.NativeHealth.Clear(b.VersionId);
                foreach (string outcome in new[] { "media", "clean", "base" })
                {
                    Mode(a, outcome);
                    await service.LaunchAsync(plan, 0);
                    check(service.LastReport!.Attempts.Count == 1 && service.NativeHealth.FaultCount(a.VersionId) == 0,
                        "RECOVERY: production " + outcome + " outcome never poisons a runtime or retries");
                    if (outcome == "media") check(service.LastPlaybackHealth?.State == SustainedPlaybackHealth.SourceFailure &&
                        service.LastNativeHealth?.Health == NativeDvHealth.SourceNotPlayable, "SUSTAINED HEALTH: a media refusal is SourceFailure alongside the unchanged native verdict");
                }
            }
            if (only is null or "sustained")
            {
                string Launches(NativeDvRuntimeDescriptor d) => Path.Combine(storeRoot, d.VersionId, "launches.txt");
                int Count(string path) => File.Exists(path) ? File.ReadAllLines(path).Length : 0;
                string Last(string path) => File.ReadAllLines(path)[^1];
                double StartOf(PlaybackPlan p) => double.Parse(p.Arguments.Single(x => x.StartsWith("--start="))[8..], System.Globalization.CultureInfo.InvariantCulture);
                string stableLaunches = Path.Combine(stableDir, "launches.txt");
                File.WriteAllText(Path.Combine(stableDir, "behavior.txt"), "play");
                void Reset() { service.NativeHealth.Clear(a.VersionId); service.NativeHealth.Clear(b.VersionId); }

                // 1, 16-18, 21: current freezes after composing FEL -> previous, once, resumed.
                foreach (var (failure, trigger) in new[] { ("freeze", SustainedPlaybackHealth.Frozen), ("stall", SustainedPlaybackHealth.Stalled),
                    ("crash-late", SustainedPlaybackHealth.RuntimeFailure) })
                {
                    Reset(); Mode(a, failure); Mode(b, "play");
                    int aBefore = Count(Launches(a)), bBefore = Count(Launches(b)), stableBefore = Count(stableLaunches);
                    await service.LaunchAsync(plan, 3);
                    var report = service.LastReport!;
                    string tag = "SUSTAINED RECOVERY (" + failure + "): ";
                    check(report.Attempts.Count == 2 && Count(Launches(a)) == aBefore + 1 && Count(Launches(b)) == bBefore + 1 &&
                          Count(stableLaunches) == stableBefore, tag + "exactly one replacement, on the previous runtime");
                    check(report.Recovery.Count == 1 && service.LastRecovery?.Trigger == trigger.ToString() &&
                          service.LastRecovery.Step == nameof(PlaybackRecoveryStep.RetryOnPreviousNative), tag + "one recovery with the right trigger");
                    var failed = report.PlaybackHealth[0];
                    check(failed.State == (failure == "crash-late" ? nameof(SustainedPlaybackHealth.RuntimeFailure) : nameof(SustainedPlaybackHealth.RecoveryStopped)) &&
                          failed.State != nameof(SustainedPlaybackHealth.UserStopped), tag + "the failed attempt is not reported as a user stop");
                    double resume = StartOf(report.Attempts[1]);
                    check(failed.ResumePosition is double confirmed && Math.Abs(resume - confirmed) < 0.001 && resume > 1 &&
                          Last(Launches(b)).EndsWith("|start=" + PlaybackRecoveryText.StartArgument(resume)[8..]),
                        tag + $"the replacement launched at the failed attempt's own confirmed position ({resume:0.00})");
                    check(!report.Attempts[0].Arguments.Any(x => x.StartsWith("--start=")), tag + "the original plan was not mutated with a resume point");
                    var replacement = report.PlaybackHealth[1];
                    check(replacement.Attempt != failed.Attempt && replacement.PlaybackProgressed && replacement.StallEpisodes == 0 &&
                          replacement.FreezeEpisodes == 0 && replacement.State == nameof(SustainedPlaybackHealth.UserStopped),
                        tag + "the replacement established its own fresh health");
                    check(service.LastNativeObservation?.Delivered != NativeDvDelivered.FullEnhancementLayer &&
                          service.LastNativeObservation?.HardwareDecoderInUse != "d3d11va" &&
                          report.FallbackHistory.Where(x => x.Contains("composition active")).All(x => x.StartsWith("Earlier native attempt")),
                        tag + "the replacement inherits no FEL or hwdec evidence; rollback success is not composition success");
                    check(service.LastNativePlaybackStatus == NativeDvPlaybackStatus.RolledBackToPreviousRuntime &&
                          service.NativeHealth.FaultCount(a.VersionId) == 0, tag + "rolled back without poisoning the generation for the session");
                    check(report.FallbackHistory.Any(x => x.StartsWith(PlaybackHealthText.Describe(trigger) + "\nRecovery: Previous verified runtime · resumed at ")),
                        tag + "calm recovery line");
                }

                // The replacement resumes mid-file, so its composed frames arrive after
                // its renderer reports: its own later evidence must still be read.
                Reset(); Mode(a, "freeze"); Mode(b, "late-compose");
                {
                    await service.LaunchAsync(plan, 4);
                    var report = service.LastReport!;
                    check(report.Attempts.Count == 2 && service.LastNativeObservation?.Delivered == NativeDvDelivered.FullEnhancementLayer &&
                          !report.FallbackHistory.Any(x => x.StartsWith("Base layer only")),
                        "SUSTAINED RECOVERY: a resumed replacement's late composition is established from its own log, not under-reported");
                }

                // 9, 11, 12: current and previous both freeze -> stable, never back to current.
                Reset(); Mode(a, "freeze"); Mode(b, "freeze");
                {
                    int aBefore = Count(Launches(a)), bBefore = Count(Launches(b)), stableBefore = Count(stableLaunches);
                    await service.LaunchAsync(plan, 3);
                    var report = service.LastReport!;
                    check(report.Attempts.Count == 3 && Count(Launches(a)) == aBefore + 1 && Count(Launches(b)) == bBefore + 1 &&
                          Count(stableLaunches) == stableBefore + 1 && report.Attempts[2].Executable == stableExe,
                        "SUSTAINED RECOVERY: current then previous freeze -> stable; two native attempts, no bounce");
                    check(report.Recovery.Select(x => x.Step).SequenceEqual([nameof(PlaybackRecoveryStep.RetryOnPreviousNative), nameof(PlaybackRecoveryStep.UseStablePlayback)]),
                        "SUSTAINED RECOVERY: one recovery per failed attempt, each a different step");
                    check(StartOf(report.Attempts[2]) > StartOf(report.Attempts[1]) && Last(stableLaunches).Contains("|start="),
                        "SUSTAINED RECOVERY: stable resumes from the previous runtime's own later position");
                    check(service.LastPlaybackHealth?.State == SustainedPlaybackHealth.UserStopped && service.LastNativeObservation is null,
                        "SUSTAINED RECOVERY: stable result carries its own health and no native evidence");
                }

                // 8: the startup rollback was already spent -> a later freeze goes straight to stable.
                Reset(); Mode(a, "fail"); Mode(b, "freeze");
                {
                    int bBefore = Count(Launches(b)), stableBefore = Count(stableLaunches);
                    await service.LaunchAsync(plan, 3);
                    var report = service.LastReport!;
                    check(report.Attempts.Count == 3 && Count(Launches(b)) == bBefore + 1 && Count(stableLaunches) == stableBefore + 1 &&
                          report.Recovery.Count == 1 && report.Recovery[0].Step == nameof(PlaybackRecoveryStep.UseStablePlayback),
                        "SUSTAINED RECOVERY: startup rollback + later freeze shares one budget -> stable");
                    check(service.LastNativePlaybackStatus == NativeDvPlaybackStatus.PreviousRuntimeAlsoFailed,
                        "SUSTAINED RECOVERY: the status says both native runtimes failed");
                }

                // 10: previous exists but no longer validates -> stable directly.
                Reset(); Mode(a, "freeze"); Mode(b, "play");
                {
                    int bBefore = Count(Launches(b)), stableBefore = Count(stableLaunches);
                    using (var unreadable = new FileStream(Path.Combine(storeRoot, b.VersionId, b.LauncherRelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
                        await service.LaunchAsync(plan, 3);
                    var report = service.LastReport!;
                    check(report.Attempts.Count == 2 && Count(Launches(b)) == bBefore && Count(stableLaunches) == stableBefore + 1 &&
                          report.Recovery.Single().Step == nameof(PlaybackRecoveryStep.UseStablePlayback),
                        "SUSTAINED RECOVERY: an unverifiable previous runtime is never launched; stable instead");
                }

                // 13: a freeze and a crash racing each other still recover once.
                Reset(); Mode(a, "freeze-crash"); Mode(b, "play");
                {
                    await service.LaunchAsync(plan, 3);
                    check(service.LastReport!.Attempts.Count == 2 && service.LastReport.Recovery.Count == 1,
                        "SUSTAINED RECOVERY: racing freeze and crash produce exactly one recovery");
                }

                // E: user pause and user stop never recover.
                foreach (string calm in new[] { "pause", "play" })
                {
                    Reset(); Mode(a, calm);
                    int aBefore = Count(Launches(a));
                    await service.LaunchAsync(plan, 3);
                    check(service.LastReport!.Attempts.Count == 1 && service.LastReport.Recovery.Count == 0 && Count(Launches(a)) == aBefore + 1 &&
                          service.LastPlaybackHealth?.State == SustainedPlaybackHealth.UserStopped && service.LastPlaybackHealth.StallEpisodes == 0,
                        "SUSTAINED RECOVERY: " + calm + " then stop never triggers recovery");
                }

                // 23: stable playback that fails hard is reported, not relaunched.
                var stablePlan = await service.PrepareAsync([media], new("Reference", "Off", "Off", false, false),
                    new SystemSummary { MpvPath = stableExe }, new AppSettings { NativeDolbyVisionLane = false, AutoHdrSwitch = false }, new(1280, 720));
                foreach (var (failure, trigger) in new[] { ("crash-late", SustainedPlaybackHealth.RuntimeFailure), ("stall", SustainedPlaybackHealth.Stalled) })
                {
                    File.WriteAllText(Path.Combine(stableDir, "behavior.txt"), failure);
                    int before = Count(stableLaunches);
                    await service.LaunchAsync(stablePlan, 3);
                    check(Count(stableLaunches) == before + 1 && service.LastReport!.Attempts.Count == 1 &&
                          service.LastRecovery?.Step == nameof(PlaybackRecoveryStep.NoRecoveryAvailable) && service.LastRecovery.Trigger == trigger.ToString(),
                        "SUSTAINED RECOVERY: stable " + failure + " is reported and not relaunched");
                }
                File.WriteAllText(Path.Combine(stableDir, "behavior.txt"), "base");
                Mode(a, "full"); Mode(b, "base"); Reset();
            }
            if (only is null or "unprotected")
            {
                // An exclusive owner models GC winning the race after selection.
                string pins = Path.Combine(storeRoot, NativeDvRuntimeStore.PinDirectoryName); Directory.CreateDirectory(pins);
                using var deletion = new FileStream(Path.Combine(pins, a.VersionId + ".pin"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                int before = File.Exists(Path.Combine(storeRoot, a.VersionId, "launches.txt")) ? File.ReadAllLines(Path.Combine(storeRoot, a.VersionId, "launches.txt")).Length : 0;
                Mode(a, "full"); Mode(b, "base");
                int faultsBefore = service.NativeHealth.FaultCount(a.VersionId);
                await service.LaunchAsync(plan, 0);
                int after = File.Exists(Path.Combine(storeRoot, a.VersionId, "launches.txt")) ? File.ReadAllLines(Path.Combine(storeRoot, a.VersionId, "launches.txt")).Length : 0;
                check(before == after, "RECOVERY: production never launches when generation pin acquisition fails");
                check(service.NativeHealth.FaultCount(a.VersionId) == faultsBefore, "RECOVERY: a cleanup conflict does not poison a valid runtime");
                check(service.LastReport!.Plan!.Executable == stableExe && service.LastNativeObservation is null, "RECOVERY: protection failure falls through truthfully to the selected stable runtime");
            }
            if (only is null or "expired")
            {
                for (int i = 0; i < 32; i++)
                    await service.PrepareAsync([media], new("Reference", "Off", "Off", false, false),
                        new SystemSummary { MpvPath = stableExe }, new AppSettings { NativeDolbyVisionLane = true, AllowNativeDolbyVisionDownload = false, AutoHdrSwitch = false }, new(1280, 720));
                bool expired = false;
                try { await service.LaunchAsync(plan, 0); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("expired")) { expired = true; }
                check(expired, "RECOVERY: expired native planning metadata can never bypass the protected launch path");
            }
        }
        finally { Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", prior); Directory.Delete(root, true); }
    }

    private static void CopyPeer(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "version.txt"), "0.41.0-1044-g14f2d48cb");
        foreach (string name in new[] { "DolbyVisionTests.exe", "DolbyVisionTests.dll", "DolbyVisionTests.deps.json", "DolbyVisionTests.runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(folder, name));
    }

    private static async Task PinsAsync(string root, Action<bool, string> check)
    {
        string storeRoot = Path.Combine(root, "pins-store");
        string id = new('c', 64);
        string generation = Path.Combine(storeRoot, id); Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "runtime"), "intact");
        var store = new NativeDvRuntimeStore(storeRoot);
        Process StartOwner()
        {
            var psi = NativeProcess.StartInfo(Path.Combine(AppContext.BaseDirectory, "DolbyVisionTests.exe"), ["--pin-child", storeRoot, id]);
            psi.RedirectStandardInput = true;
            return Process.Start(psi)!;
        }
        using var first = StartOwner(); using var second = StartOwner();
        try
        {
            string? one = await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string? two = await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(one == "protected" && two == "protected", "PINS: concurrent real playback processes both own protection");
            await first.StandardInput.WriteLineAsync("release"); await first.WaitForExitAsync();
            check(new NativeDvRuntimeLifecycle(store).CollectGarbage(null, new(null, null, null)) == 0 && File.ReadAllText(Path.Combine(generation, "runtime")) == "intact", "PINS: releasing first owner cannot expose second owner to partial GC");
            second.Kill(true); await second.WaitForExitAsync();
            // Windows can release a terminated owner's share lock a few milliseconds
            // after the process is reported exited (measured: up to ~7 ms under
            // contention). GC correctly skips a generation it cannot lease
            // exclusively and collects it on a later pass, so what must hold is
            // eventual collection, bounded, with the files intact until then.
            int collected = 0;
            var releaseDeadline = Stopwatch.StartNew();
            while (collected == 0 && releaseDeadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                collected = new NativeDvRuntimeLifecycle(store).CollectGarbage(null, new(null, null, null));
                if (collected == 0)
                {
                    check(File.ReadAllText(Path.Combine(generation, "runtime")) == "intact", "PINS: a skipped collection never leaves a partial deletion");
                    await Task.Delay(25);
                }
            }
            check(collected == 1 && !Directory.Exists(generation), "PINS: GC resumes after the last owner releases");
            check(store.PinGeneration(id) is null, "PINS: generation deleted between selection and launch cannot be pinned");
            Directory.CreateDirectory(generation);
            using (var deleting = store.TryAcquireGenerationDeletionLock(id))
            {
                check(deleting is not null, "PINS: GC can acquire its exclusive lifetime lease");
                using var contender = StartOwner();
                try
                {
                    check(await contender.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "unprotected",
                        "PINS: playback cannot acquire protection after deletion wins, even while files still exist");
                    Directory.Delete(generation, true);
                }
                finally { if (!contender.HasExited) contender.Kill(true); await contender.WaitForExitAsync(); }
            }
            check(store.PinGeneration(id) is null, "PINS: deletion lock release cannot resurrect a missing generation");
        }
        finally
        {
            foreach (var child in new[] { first, second }) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        }
    }
}
