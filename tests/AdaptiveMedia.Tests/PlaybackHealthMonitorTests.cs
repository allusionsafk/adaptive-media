using AdaptiveMedia;
using System.Diagnostics;
using System.IO.Pipes;

/// <summary>The health sampler watches playback; it must never be able to fail it.
/// A player that closes its diagnostics connection, or one that never answers,
/// ends the sampler quietly.</summary>
internal static class PlaybackHealthMonitorTests
{
    public static async Task<int> RunAsync(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "HEALTH SAMPLER: " + m); }
        var fast = new PlaybackHealthPolicy { SampleInterval = TimeSpan.FromMilliseconds(50) };

        // No player at the other end, cancelled while polling.
        {
            var monitor = new PlaybackHealthMonitor(1, "adaptive-absent-" + Guid.NewGuid().ToString("N"), "stable player", fast);
            using var cancellation = new CancellationTokenSource(250);
            var run = monitor.RunAsync(Task.CompletedTask, Stopwatch.StartNew(), cancellation.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Check(run.IsCompletedSuccessfully, "a player that never answers ends the sampler quietly");
        }

        // A player that answers and then vanishes mid-poll, exactly as one closing does.
        {
            string pipeName = "adaptive-closing-" + Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            var server = Task.Run(async () =>
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await reader.ReadLineAsync(cancellation.Token);
                // Break the connection and cancel the attempt at the same moment.
                pipe.Dispose();
                cancellation.Cancel();
            });
            var monitor = new PlaybackHealthMonitor(2, pipeName, "stable player", fast);
            var run = monitor.RunAsync(Task.CompletedTask, Stopwatch.StartNew(), cancellation.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Check(run.IsCompletedSuccessfully, "a player closing its connection while the attempt is cancelled cannot fault the sampler");
            Check(monitor.Snapshot().State == SustainedPlaybackHealth.Starting, "no health is claimed from a connection that produced nothing");
        }
        return count;
    }
}
