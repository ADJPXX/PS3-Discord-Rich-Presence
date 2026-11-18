using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public sealed class DiscordService : IDiscordPresence, IAsyncDisposable
{
    private readonly string _clientId;
    private readonly Func<int, CancellationToken, Task<Stream>> _open;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _operation = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopToken;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private volatile Session? _session;
    private volatile string? _lastError;

    public DiscordService(long clientId) : this(clientId, OpenPipeAsync, TimeSpan.FromSeconds(5)) { }

    internal DiscordService(long clientId, Func<int, CancellationToken, Task<Stream>> open, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientId);
        _clientId = clientId.ToString(CultureInfo.InvariantCulture);
        _open = open;
        _timeout = timeout;
        _stopToken = _stopping.Token;
    }

    public bool IsPipeConnected => !_stopToken.IsCancellationRequested && _session?.IsAlive == true;
    public string? LastError => _session?.ReadError ?? _lastError;

    public Task<bool> ConnectAsync(CancellationToken token) =>
        RunAsync(ConnectCoreAsync, token, Timeout.InfiniteTimeSpan);

    public Task<bool> ClearActivityAsync(CancellationToken token) => SetActivityAsync(null, token);

    public Task<bool> UpdateAsync(GameInfo game, string? state, string image, DateTime? startTime, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(game);
        var title = LimitText(string.IsNullOrWhiteSpace(game.Name) ? "PlayStation 3" : game.Name);
        var activity = new Dictionary<string, object?>
        {
            ["name"] = title,
            ["type"] = 0,
            ["details"] = title,
            ["assets"] = new { large_image = image, large_text = title }
        };

        if (!string.IsNullOrWhiteSpace(state))
            activity["state"] = LimitText(state);
        if (startTime.HasValue)
            activity["timestamps"] = new { start = new DateTimeOffset(startTime.Value).ToUnixTimeSeconds() };

        return SetActivityAsync(activity, token);
    }

    public static (string?, DateTime) GetCurrentTime(DateTime oldTime, string? oldGame, string? currentGame) =>
        (currentGame, currentGame == oldGame ? oldTime : DateTime.UtcNow);

    private static string LimitText(string text)
    {
        const int maxBytes = 128;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes)
                break;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars];
    }

    private Task<bool> SetActivityAsync(object? activity, CancellationToken token) =>
        RunAsync(ct => _session?.SetActivityAsync(activity, ct) ??
            throw new IOException("No active Discord IPC session."), token, _timeout);

    // Keep the entire exchange together. A caller canceled while queuing does not own the session.
    private async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> operation, CancellationToken caller, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_stopToken.IsCancellationRequested, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, _stopToken);
        await _operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            deadline.CancelAfter(timeout);
            try
            {
                if (await operation(deadline.Token).ConfigureAwait(false))
                {
                    _lastError = null;
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                await DisconnectAsync().ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                _stopToken.ThrowIfCancellationRequested();
                _lastError = "Timed out while waiting for Discord to confirm the activity.";
                Trace.TraceWarning(_lastError);
                return false;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                _lastError = ex.Message;
                Trace.TraceWarning($"Discord IPC: {ex.Message}");
            }

            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<bool> ConnectCoreAsync(CancellationToken token)
    {
        if (IsPipeConnected)
            return true;
        await DisconnectAsync().ConfigureAwait(false);

        string? handshakeError = null;
        string? openError = null;
        for (var index = 0; index < 10; index++)
        {
            token.ThrowIfCancellationRequested();
            Session? candidate = null;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                // Bound discovery independently of the time a connected Discord needs to reply.
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                candidate = new Session(await _open(index, attempt.Token).ConfigureAwait(false), _timeout);
                attempt.CancelAfter(_timeout);
                await candidate.HandshakeAsync(_clientId, attempt.Token).ConfigureAwait(false);
                if (!candidate.IsAlive)
                    throw new IOException(candidate.ReadError ?? "Discord closed the session immediately after READY.");
                _session = candidate;
                candidate = null;
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (candidate != null)
                    handshakeError ??= $"discord-ipc-{index} opened, but Discord did not send READY before the deadline.";
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                if (candidate != null)
                    handshakeError ??= $"discord-ipc-{index}: {ex.Message}";
                else if (ex is UnauthorizedAccessException)
                    openError = $"Access denied to discord-ipc-{index} in this Windows session.";
                else if (ex is not (TimeoutException or FileNotFoundException))
                    openError ??= $"discord-ipc-{index}: {ex.Message}";
            }
            finally
            {
                if (candidate != null)
                    await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
        _lastError = handshakeError ?? openError ?? "No Discord Desktop pipe is available in this Windows session.";
        Trace.TraceWarning(_lastError);
        return false;
    }

    private static async Task<Stream> OpenPipeAsync(int index, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(200, token).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException or JsonException;

    private async Task DisconnectAsync()
    {
        var previous = _session;
        _session = null;
        if (previous != null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
            return new ValueTask(_disposeTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _stopping.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }
        finally
        {
            // Callers already queued at shutdown may still be unwinding.
            _operation.Release();
        }
    }

    private sealed class Session(Stream pipe, TimeSpan timeout) : IAsyncDisposable
    {
        private const int MaxPayload = 64 * 1024;
        private readonly SemaphoreSlim _writer = new(1);
        private readonly CancellationTokenSource _end = new();
        private Task _reader = Task.CompletedTask;
        private volatile PendingCommand? _pending;
        private volatile string? _readError;
        public string? ReadError => _readError;

        private sealed record PendingCommand(string Nonce)
        {
            public TaskCompletionSource<string?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool IsAlive => !_reader.IsCompleted;

        public async Task HandshakeAsync(string clientId, CancellationToken token)
        {
            var hello = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = clientId });
            await WriteAsync(0, hello, token).ConfigureAwait(false);
            using var message = await ReadMessageAsync(token).ConfigureAwait(false);
            var root = message.RootElement;
            if (!Matches(root, "cmd", "DISPATCH") || !Matches(root, "evt", "READY"))
                throw new InvalidDataException(DescribeError("Discord did not confirm READY.", root));
            _reader = PumpAsync();
        }

        public async Task<bool> SetActivityAsync(object? activity, CancellationToken token)
        {
            if (!IsAlive)
                throw new IOException(ReadError ?? "Discord IPC reader stopped.");
            var command = new PendingCommand(Guid.NewGuid().ToString());
            _pending = command;
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    cmd = "SET_ACTIVITY",
                    args = new { pid = Environment.ProcessId, activity },
                    nonce = command.Nonce
                });
                await WriteAsync(1, payload, token).ConfigureAwait(false);
                await Task.WhenAny(command.Reply.Task, _reader).WaitAsync(token).ConfigureAwait(false);
                if (!command.Reply.Task.IsCompletedSuccessfully)
                    throw new IOException(ReadError ?? "Discord closed the pipe before confirming SET_ACTIVITY.");
                var error = await command.Reply.Task.ConfigureAwait(false);
                if (error != null)
                    throw new InvalidDataException(error);
                return true;
            }
            finally
            {
                _pending = null;
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    using var message = await ReadMessageAsync(_end.Token).ConfigureAwait(false);
                    var root = message.RootElement;
                    var pending = _pending;
                    if (pending == null || !Matches(root, "nonce", pending.Nonce))
                        continue;
                    var accepted = Matches(root, "cmd", "SET_ACTIVITY") &&
                        (!root.TryGetProperty("evt", out var evt) || evt.ValueKind == JsonValueKind.Null);
                    pending.Reply.TrySetResult(accepted ? null : DescribeError("SET_ACTIVITY rejeitado.", root));
                }
            }
            catch (OperationCanceledException) when (_end.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_end.IsCancellationRequested) { }
            catch (Exception ex) when (IsTransportFailure(ex) || ex is OperationCanceledException)
            {
                _readError = ex.Message;
                Trace.TraceWarning($"Discord IPC read ended: {ex.Message}");
            }
        }

        private static string DescribeError(string context, JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return context;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;
            var code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number &&
                       value.TryGetInt32(out var number) ? $" Code {number}." : "";
            var message = root.TryGetProperty("message", out value) && value.ValueKind == JsonValueKind.String
                ? LimitText(value.GetString()!).Replace('\r', ' ').Replace('\n', ' ').Trim()
                : "";
            return message.Length == 0 ? context + code : $"{context}{code} {message}";
        }

        private static bool Matches(JsonElement root, string name, string expected) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

        private async Task<JsonDocument> ReadMessageAsync(CancellationToken token)
        {
            var header = new byte[8];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (length < 0 || length > MaxPayload)
                    throw new InvalidDataException("Invalid IPC frame length.");
                if (opcode is not (1 or 2 or 3 or 4))
                    throw new InvalidDataException("Unknown IPC opcode.");
                var data = new byte[length];
                await pipe.ReadExactlyAsync(data, token).ConfigureAwait(false);
                if (opcode == 2)
                {
                    const string closed = "Discord closed the session (CLOSE).";
                    if (length == 0)
                        throw new EndOfStreamException(closed);
                    using var reason = JsonDocument.Parse(data);
                    throw new EndOfStreamException(DescribeError(closed, reason.RootElement));
                }
                if (opcode == 3)
                {
                    using var pongDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pongDeadline.CancelAfter(timeout);
                    await WriteAsync(4, data, pongDeadline.Token).ConfigureAwait(false);
                }
                else if (opcode == 1)
                {
                    var message = JsonDocument.Parse(data);
                    if (message.RootElement.ValueKind == JsonValueKind.Object)
                        return message;
                    message.Dispose();
                    throw new InvalidDataException("The IPC frame must contain a JSON object.");
                }
            }
        }

        private async Task WriteAsync(int opcode, byte[] data, CancellationToken token)
        {
            if (data.Length > MaxPayload)
                throw new InvalidDataException("IPC message exceeds the maximum size.");
            var frame = new byte[8 + data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), data.Length);
            data.CopyTo(frame, 8);

            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await pipe.WriteAsync(frame, token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _writer.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _end.Cancel();
            pipe.Dispose();
            try
            {
                await _reader.ConfigureAwait(false);
            }
            finally
            {
                _end.Dispose();
                _writer.Dispose();
            }
        }
    }
}
