using System.Diagnostics;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>Reads sustained-health evidence from one running player and feeds it
/// to that attempt's classifier.
///
/// It uses its own diagnostics connection, separate from the startup monitor, for
/// two reasons: a query that times out leaves a connection in an unknown state,
/// so an unresponsive player is detected by dropping and reconnecting this one
/// rather than by corrupting the startup monitor's; and the startup-health path
/// stays exactly as it was. It connects only after the startup monitor has, so it
/// can never take the player's first connection.
///
/// One sample per <see cref="PlaybackHealthPolicy.SampleInterval"/>, a fixed
/// handful of properties, no per-frame work, and nothing retained beyond the
/// classifier's bounded state.</summary>
public sealed class PlaybackHealthMonitor
{
    /// <summary>mpv properties read per sample. Each exists in mpv 0.35+; any the
    /// running build does not report is simply absent from the sample.</summary>
    public static readonly string[] Properties =
    [
        "time-pos", "duration", "pause", "paused-for-cache", "seeking", "eof-reached",
        "estimated-vf-fps", "container-fps",
        "frame-drop-count", "decoder-frame-drop-count", "mistimed-frame-count", "vo-delayed-frame-count",
    ];

    private readonly string _pipeName;
    private readonly PlaybackHealthPolicy _policy;
    private readonly PlaybackHealthClassifier _classifier;
    private readonly object _gate = new();
    private volatile string? _endFileReason;

    public PlaybackHealthMonitor(long attemptId, string pipeName, string runtime, PlaybackHealthPolicy? policy = null)
    {
        _pipeName = pipeName;
        _policy = policy ?? PlaybackHealthPolicy.Default;
        _classifier = new(attemptId, _policy);
        Runtime = runtime;
    }

    public long AttemptId => _classifier.AttemptId;
    /// <summary>Which runtime this attempt ran on, for diagnostics only.</summary>
    public string Runtime { get; }
    public bool StopRequested { get; private set; }
    public event Action<PlaybackHealthTransition>? Transitioned;

    public bool RecoveryStop { get; private set; }
    public void MarkStopRequested() => StopRequested = true;
    /// <summary>Record that DemiMedia is stopping this player to recover, before the
    /// stop is attempted, so the exit it causes can never be read as a user stop.</summary>
    public void MarkRecoveryStop() => RecoveryStop = true;

    public PlaybackHealthSnapshot Snapshot() { lock (_gate) return _classifier.Snapshot(); }

    public void Finish(TimeSpan at, bool processStarted, int exitCode)
    {
        PlaybackHealthTransition? transition;
        lock (_gate)
            transition = _classifier.Finish(new PlaybackEnd { AttemptId = AttemptId, At = at,
                ProcessStarted = processStarted, ExitCode = exitCode, StopRequested = StopRequested, RecoveryStop = RecoveryStop,
                EndFileReason = _endFileReason });
        if (transition is not null) Transitioned?.Invoke(transition);
    }

    public async Task RunAsync(Task startupMonitorConnected, Stopwatch clock, CancellationToken token)
    {
        await Task.WhenAny(startupMonitorConnected, Task.Delay(Timeout.Infinite, token));
        MpvIpc? ipc = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                (var sample, ipc) = await ReadAsync(ipc, clock, token);
                if (token.IsCancellationRequested) return;
                PlaybackHealthTransition? transition;
                lock (_gate) transition = _classifier.Observe(sample);
                if (transition is not null) Transitioned?.Invoke(transition);
                await Task.Delay(_policy.SampleInterval, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ipc is not null) await ipc.DisposeAsync(); }
    }

    private async Task<(PlaybackHealthSample Sample, MpvIpc? Ipc)> ReadAsync(MpvIpc? ipc, Stopwatch clock, CancellationToken token)
    {
        var values = new Dictionary<string, JsonElement>(Properties.Length);
        // An unanswered poll has been unanswered since it was sent, not since its
        // timeout expired; stamping it at the send keeps freeze timing honest.
        TimeSpan sent = clock.Elapsed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_policy.SampleInterval * 3);
        try
        {
            if (ipc is null)
            {
                ipc = new MpvIpc(_pipeName) { EventReceived = OnEvent };
                await ipc.ConnectAsync(timeout.Token);
            }
            foreach (string name in Properties)
                if (await ipc.CommandAsync(["get_property", name], timeout.Token) is { } value) values[name] = value;
        }
        // Including while the attempt is being cancelled: a player closing breaks
        // this connection as a matter of course, and observing health must never
        // be able to fail the playback it is only watching.
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or
            JsonException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // No answer inside the budget. The connection is discarded, never
            // reused mid-reply, and this poll is recorded as unanswered.
            if (ipc is not null) await ipc.DisposeAsync();
            return (new PlaybackHealthSample { AttemptId = AttemptId, At = sent, Responsive = false }, null);
        }
        return (Normalize(AttemptId, clock.Elapsed, values), ipc);
    }

    private void OnEvent(JsonElement message)
    {
        if (message.TryGetProperty("event", out var name) && name.GetString() == "end-file" &&
            message.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            _endFileReason = reason.GetString();
    }

    /// <summary>Turn raw property replies into a sample. Only values of the
    /// expected JSON type are used; anything else is "not observed".</summary>
    public static PlaybackHealthSample Normalize(long attemptId, TimeSpan at, IReadOnlyDictionary<string, JsonElement> values)
    {
        double? Number(string name) => values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
        long? Count(string name) => values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out long l) && l >= 0 ? l : null;
        bool? Flag(string name) => values.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
        return new PlaybackHealthSample
        {
            AttemptId = attemptId, At = at, Responsive = true,
            Position = Number("time-pos"), Duration = Number("duration"),
            Paused = Flag("pause"), PausedForCache = Flag("paused-for-cache"), Seeking = Flag("seeking"),
            EofReached = Flag("eof-reached"),
            FrameRate = Number("estimated-vf-fps") ?? Number("container-fps"),
            OutputDrops = Count("frame-drop-count"), DecoderDrops = Count("decoder-frame-drop-count"),
            MistimedFrames = Count("mistimed-frame-count"), DelayedFrames = Count("vo-delayed-frame-count"),
        };
    }
}
