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
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launches.txt"), mode + "\n");
        if (mode == "fail") return 1;
        if (mode == "media") return 2;
        if (mode == "clean") return 0;
        string pipeName = args.Single(x => x.StartsWith("--input-ipc-server="))[19..];
        if (pipeName.StartsWith(@"\\.\pipe\")) pipeName = pipeName[9..];
        string? log = args.FirstOrDefault(x => x.StartsWith("--log-file="))?[11..];
        if (log is not null && mode != "silent")
            File.WriteAllText(log, "Initialized libplacebo test (API v371)\n" + (mode == "full" || mode == "partial" || mode == "reject-stop" ?
                "Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track)\n[vd] Opening decoder hevc\n[vd] Opening decoder hevc\n[vd] Selected decoder: hevc\n[vf] [el_pair]\nsh_dovi_compose_nlq\n[vd] Using hardware decoding (d3d11va).\n" : ""));
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var command = message.RootElement.GetProperty("command");
            string name = command[0].GetString()!;
            string? prop = command.GetArrayLength() > 1 ? command[1].GetString() : null;
            object? data = prop switch
            {
                "mpv-version" => version,
                "video-codec" when mode != "partial" && mode != "silent" && mode != "reject-stop" => "hevc",
                "video-out-params" when mode != "partial" && mode != "silent" && mode != "reject-stop" => new { w = 3840, h = 2160 },
                "hwdec-current" when mode == "full" || mode == "partial" || mode == "reject-stop" => "d3d11va",
                "gpu-api" => "d3d11",
                "gpu-context" => "d3d11",
                _ => null
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = message.RootElement.GetProperty("request_id").GetInt32(), error = name == "quit" && mode == "reject-stop" ? "command failed" : data is null && name == "get_property" ? "property unavailable" : "success", data }));
            if (name == "quit") return mode == "reject-stop" ? 1 : 0;
            // The monitor has consumed a complete poll and, for partial, its log.
            if (name == "set_property" && prop == "msg-level" && mode == "partial") return 1;
        }
        return 0;
    }

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
            var service = new PlaybackService { NativeDolbyVision = new NativeDvLane(store, a) };
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
                }
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
            check(new NativeDvRuntimeLifecycle(store).CollectGarbage(null, new(null, null, null)) == 1, "PINS: GC resumes after the last owner releases");
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
