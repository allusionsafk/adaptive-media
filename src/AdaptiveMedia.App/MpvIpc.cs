using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace AdaptiveMedia;
public sealed class MpvIpc : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _id;
    public MpvIpc(string pipe)
    {
        _pipe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    }
    /// <summary>Player events that arrive while a reply is awaited. Events are only
    /// read in passing, so this sees some of them, never a guaranteed stream.</summary>
    public Action<JsonElement>? EventReceived { get; set; }
    public async Task ConnectAsync(CancellationToken token)
    {
        await _pipe.ConnectAsync(token);
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }
    public async Task<JsonElement?> CommandAsync(object[] command, CancellationToken token) =>
        (await SendAsync(command, token)).Data;

    public async Task<bool> CommandSucceededAsync(object[] command, CancellationToken token) =>
        (await SendAsync(command, token)).Success;

    private async Task<(bool Success, JsonElement? Data)> SendAsync(object[] command, CancellationToken token)
    {
        int id = ++_id;
        if (_writer is null || _reader is null) throw new InvalidOperationException("Connect IPC before sending commands.");
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = id }).AsMemory(), token);
        while (await _reader.ReadLineAsync(token) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("request_id", out var reply) && reply.GetInt32() == id)
            {
                bool success = root.GetProperty("error").GetString() == "success";
                return (success, success && root.TryGetProperty("data", out var data) ? data.Clone() : null);
            }
            if (EventReceived is not null && root.TryGetProperty("event", out _)) EventReceived(root.Clone());
        }
        throw new EndOfStreamException("Player closed its diagnostics connection.");
    }
    public async ValueTask DisposeAsync()
    {
        try { if (_writer is not null) await _writer.DisposeAsync(); }
        catch (IOException) { /* A player may close its end before quit is acknowledged. */ }
        finally { _reader?.Dispose(); await _pipe.DisposeAsync(); }
    }
}
