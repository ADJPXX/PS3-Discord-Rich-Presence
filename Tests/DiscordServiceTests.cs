using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;
using Xunit;

namespace PS3DiscordRichPresence.Tests;

public sealed class DiscordServiceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const long ClientId = 1528636206638694400;

    [Fact]
    public async Task OnlyReadyPublishesConnectionAndHandshakeUsesStringId()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        var connection = service.ConnectAsync(default);
        var handshake = await pipe.SentAsync();
        Assert.Equal(0, handshake.Opcode);
        var hello = handshake.Json;
        Assert.Equal(1, hello.GetProperty("v").GetInt32());
        Assert.Equal("1528636206638694400", hello.GetProperty("client_id").GetString());
        Assert.False(service.IsPipeConnected);
        pipe.Ready();
        Assert.True(await connection.WaitAsync(Guard));
        Assert.True(service.IsPipeConnected);
    }

    [Theory]
    [InlineData(2, "{\"code\":4000,\"message\":\"Invalid Client ID\"}")]
    [InlineData(1, "{\"evt\":\"ERROR\",\"data\":{\"code\":4000,\"message\":\"Invalid Client ID\"}}")]
    public async Task RejectedHandshakePreservesDiscordReasonAcrossEmptyPipes(int opcode, string json)
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        var connect = service.ConnectAsync(default);
        await pipe.SentAsync();
        pipe.Enqueue(opcode, Encoding.UTF8.GetBytes(json));
        Assert.False(await connect.WaitAsync(Guard));
        Assert.Contains("4000", service.LastError);
        Assert.Contains("Invalid Client ID", service.LastError);
        Assert.False(service.IsPipeConnected);
    }

    [Theory]
    [InlineData(true, "Access denied")]
    [InlineData(false, "No Discord Desktop pipe")]
    public async Task MissingPipeAndAccessDeniedHaveSeparateDiagnostics(bool denied, string expected)
    {
        await using var service = new DiscordService(ClientId, (i, _) =>
            Task.FromException<Stream>(denied && i == 2 ? new UnauthorizedAccessException() : new TimeoutException()), Guard);
        Assert.False(await service.ConnectAsync(default));
        Assert.Contains(expected, service.LastError);
    }

    [Fact]
    public async Task ReadyAfterOneSecondIsAcceptedWithinHandshakeDeadline()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        var connect = service.ConnectAsync(default);
        await pipe.SentAsync();
        await Task.Delay(1200);
        pipe.Ready();
        Assert.True(await connect.WaitAsync(Guard));
        Assert.Null(service.LastError);
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(1, "{")]
    [InlineData(1, "null")]
    [InlineData(1, "{\"cmd\":\"DISPATCH\",\"evt\":\"ERROR\"}")]
    [InlineData(2, "{}")]
    [InlineData(9, "{}")]
    public async Task UnrecognizedHandshakeIsClosedAndNextPipeTried(int opcode, string data)
    {
        var first = new ScriptedPipe();
        var next = new ScriptedPipe();
        await using var service = new DiscordService(ClientId, (i, _) =>
            Task.FromResult<Stream>(i == 0 ? first : next), Guard);
        var connecting = service.ConnectAsync(default);
        await first.SentAsync();
        first.Enqueue(opcode, Encoding.UTF8.GetBytes(data));
        await next.SentAsync();
        Assert.True(first.Closed);
        next.Ready();
        Assert.True(await connecting.WaitAsync(Guard));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(65537, false)]
    [InlineData(2, true)]
    public async Task InvalidOrIncompleteFrameReleasesCandidate(int length, bool end)
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        var connecting = service.ConnectAsync(default);
        await pipe.SentAsync();
        pipe.Header(1, length);
        if (end)
            pipe.EndInput();
        Assert.False(await connecting.WaitAsync(Guard));
        Assert.False(service.IsPipeConnected);
        Assert.True(pipe.Closed);
    }

    [Fact]
    public async Task WindowsPipeReadIsCanceledDuringAnIncompleteHandshake()
    {
        var name = $"ps3-presence-test-{Guid.NewGuid():N}";
        using var guard = new CancellationTokenSource(Guard);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(guard.Token);
        await using var service = new DiscordService(ClientId, async (_, token) =>
        {
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(token);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, Guard);
        var opening = service.ConnectAsync(stop.Token);
        await accepted;
        var header = new byte[8];
        await server.ReadExactlyAsync(header, guard.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        Assert.InRange(length, 1, 1024);
        await server.ReadExactlyAsync(new byte[length], guard.Token);
        await server.WriteAsync(new byte[] { 1, 0, 0 }, guard.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(guard.Token));
        Assert.False(service.IsPipeConnected);
        Assert.Equal(0, await server.ReadAsync(header, guard.Token));
    }

    [Fact]
    public async Task SilentHandshakeTimesOutAndDisposesTransport()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe, TimeSpan.FromMilliseconds(300));
        var connecting = service.ConnectAsync(default);
        await pipe.SentAsync();
        Assert.False(await connecting.WaitAsync(Guard));
        Assert.True(pipe.Closed);
        Assert.Contains("READY", service.LastError);
    }

    [Fact]
    public async Task CancellationDuringPartialHandshakePropagates()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        using var cancel = new CancellationTokenSource();
        var connecting = service.ConnectAsync(cancel.Token);
        await pipe.SentAsync();
        pipe.Header(1, 16);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Guard));
        Assert.True(pipe.Closed);
    }

    [Fact]
    public async Task UpdateWaitsForMatchingAcknowledgementAndSerializesTimestampInSeconds()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        var start = DateTime.UnixEpoch.AddSeconds(123456789);
        var update = service.UpdateAsync(new GameInfo { Name = "Gran Turismo" }, "CPU 60°C", "art", start, default);
        var frame = await pipe.SentAsync();
        Assert.Equal(1, frame.Opcode);
        var body = frame.Json;
        Assert.Equal("SET_ACTIVITY", body.GetProperty("cmd").GetString());
        var args = body.GetProperty("args");
        Assert.Equal(Environment.ProcessId, args.GetProperty("pid").GetInt32());
        var activity = args.GetProperty("activity");
        Assert.Equal("Gran Turismo", activity.GetProperty("name").GetString());
        Assert.Equal("Gran Turismo", activity.GetProperty("details").GetString());
        Assert.Equal(0, activity.GetProperty("type").GetInt32());
        Assert.Equal("CPU 60°C", activity.GetProperty("state").GetString());
        Assert.Equal("art", activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal(123456789L, activity.GetProperty("timestamps").GetProperty("start").GetInt64());

        pipe.EnqueueJson(new { cmd = "SET_ACTIVITY", nonce = "outro-comando" });
        pipe.Enqueue(3, [42]);
        var pong = await pipe.SentAsync();
        Assert.Equal(4, pong.Opcode);
        Assert.Equal(new byte[] { 42 }, pong.Data);
        Assert.False(update.IsCompleted);
        pipe.Acknowledge(frame);
        Assert.True(await update.WaitAsync(Guard));
    }

    [Fact]
    public async Task LongUnicodeTitleFitsTheActivityTextWithoutSplittingAnEmoji()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        var prefix = new string('x', 126);
        var update = service.UpdateAsync(new GameInfo { Name = prefix + "🎮" }, null, "art", null, default);
        var frame = await pipe.SentAsync();
        var activity = frame.Json.GetProperty("args").GetProperty("activity");
        Assert.Equal(prefix, activity.GetProperty("name").GetString());
        Assert.Equal(prefix, activity.GetProperty("assets").GetProperty("large_text").GetString());
        pipe.Acknowledge(frame);
        Assert.True(await update.WaitAsync(Guard));
    }

    [Fact]
    public async Task IdleReaderAnswersPingBeforeNextCommand()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        pipe.Enqueue(3, [1, 2, 3]);
        var pong = await pipe.SentAsync();
        Assert.Equal(4, pong.Opcode);
        Assert.Equal(new byte[] { 1, 2, 3 }, pong.Data);
    }

    [Fact]
    public async Task RejectedClearReturnsFalseAndDisconnects()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        var clear = service.ClearActivityAsync(default);
        var request = await pipe.SentAsync();
        Assert.Equal(JsonValueKind.Null, request.Json.GetProperty("args").GetProperty("activity").ValueKind);
        pipe.EnqueueJson(new { cmd = "SET_ACTIVITY", nonce = request.Nonce, evt = "ERROR",
            data = new { code = 4001, message = "Invalid activity" } });
        Assert.False(await clear.WaitAsync(Guard));
        Assert.False(service.IsPipeConnected);
        Assert.Contains("4001", service.LastError);
        Assert.Contains("Invalid activity", service.LastError);
        Assert.True(pipe.Closed);
    }

    [Fact]
    public async Task BrokenWriteIsNotReportedAsDeliveredAndCanReconnect()
    {
        var first = new ScriptedPipe();
        var second = new ScriptedPipe();
        var pipes = new Queue<Stream>([first, second]);
        await using var service = new DiscordService(ClientId, (_, _) => Task.FromResult(pipes.Dequeue()), Guard);
        await ConnectAsync(service, first);
        first.FailWrite = true;
        Assert.False(await service.ClearActivityAsync(default).WaitAsync(Guard));
        Assert.True(first.Closed);
        await ConnectAsync(service, second);
        Assert.Null(service.LastError);
        var clear = service.ClearActivityAsync(default);
        second.Acknowledge(await second.SentAsync());
        Assert.True(await clear.WaitAsync(Guard));
    }

    [Fact]
    public async Task MissingAcknowledgementExpiresAndReleasesSession()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe, TimeSpan.FromMilliseconds(400));
        await ConnectAsync(service, pipe);
        var clear = service.ClearActivityAsync(default);
        await pipe.SentAsync();
        Assert.False(await clear.WaitAsync(Guard));
        Assert.True(pipe.Closed);
    }

    [Fact]
    public async Task BlockedAsyncWriteCanBeCanceled()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        pipe.BlockWrites = true;
        using var cancel = new CancellationTokenSource();
        var clear = service.ClearActivityAsync(cancel.Token);
        await pipe.SentAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear.WaitAsync(Guard));
        Assert.True(pipe.Closed);
    }

    [Fact]
    public async Task CancelingQueuedCommandPreservesOwnersConnection()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        var first = service.ClearActivityAsync(default);
        var frame = await pipe.SentAsync();
        using var cancel = new CancellationTokenSource();
        var queued = service.ClearActivityAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Guard));
        pipe.Acknowledge(frame);
        Assert.True(await first.WaitAsync(Guard));
        Assert.True(service.IsPipeConnected);
    }

    [Fact]
    public async Task ConcurrentCallersCompleteWithUniqueAcknowledgedCommands()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        const int count = 64;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, count).Select(async _ =>
        {
            await barrier.Task;
            return await service.ClearActivityAsync(default);
        }).ToArray();
        barrier.SetResult();
        var nonces = new HashSet<string>();
        for (var index = 0; index < count; index++)
        {
            var frame = await pipe.SentAsync();
            Assert.True(nonces.Add(frame.Nonce));
            pipe.Acknowledge(frame);
        }
        Assert.All(await Task.WhenAll(calls).WaitAsync(Guard), ok => Assert.True(ok));
    }

    [Fact]
    public async Task DisposalCancelsOutstandingReadAndIsIdempotent()
    {
        var pipe = new ScriptedPipe();
        await using var service = Service(pipe);
        await ConnectAsync(service, pipe);
        var clear = service.ClearActivityAsync(default);
        await pipe.SentAsync();
        var closing = service.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear.WaitAsync(Guard));
        await Task.WhenAll(closing, service.DisposeAsync().AsTask()).WaitAsync(Guard);
        Assert.Equal(1, pipe.CloseCount);
        Assert.False(service.IsPipeConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ConnectAsync(default));
    }

    private static DiscordService Service(ScriptedPipe pipe, TimeSpan? timeout = null) =>
        new(ClientId, (i, _) => i == 0 ? Task.FromResult<Stream>(pipe)
            : Task.FromException<Stream>(new TimeoutException()), timeout ?? Guard);

    private static async Task ConnectAsync(DiscordService service, ScriptedPipe pipe)
    {
        var connecting = service.ConnectAsync(default);
        Assert.Equal(0, (await pipe.SentAsync()).Opcode);
        pipe.Ready();
        Assert.True(await connecting.WaitAsync(Guard));
    }

    private sealed record Packet(int Opcode, byte[] Data)
    {
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Data);
        public string Nonce => Json.GetProperty("nonce").GetString()!;
    }

    private sealed class ScriptedPipe : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<Packet> _outgoing = Channel.CreateUnbounded<Packet>();
        private ReadOnlyMemory<byte> _unread;
        private int _closeCount;
        public int CloseCount => Volatile.Read(ref _closeCount);
        public bool Closed => CloseCount > 0;
        public bool BlockWrites { get; set; }
        public bool FailWrite { get; set; }

        public void EndInput() => _incoming.Writer.TryComplete();
        public void Ready() => EnqueueJson(new { cmd = "DISPATCH", evt = "READY" });
        public void Acknowledge(Packet command) => EnqueueJson(new { cmd = "SET_ACTIVITY", nonce = command.Nonce });
        public void EnqueueJson(object value) => Enqueue(1, JsonSerializer.SerializeToUtf8Bytes(value));

        public void Header(int opcode, int length)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);
            _incoming.Writer.TryWrite(header);
        }

        public void Enqueue(int opcode, byte[] data)
        {
            Header(opcode, data.Length);
            if (data.Length > 0)
                _incoming.Writer.TryWrite(data);
        }

        public Task<Packet> SentAsync() => _outgoing.Reader.ReadAsync().AsTask().WaitAsync(Guard);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_unread.IsEmpty)
            {
                if (!await _incoming.Reader.WaitToReadAsync(token) || !_incoming.Reader.TryRead(out var chunk))
                    return 0;
                _unread = chunk;
            }
            // Each read yields at most three bytes, including reads of a frame header.
            var count = Math.Min(3, Math.Min(buffer.Length, _unread.Length));
            _unread[..count].CopyTo(buffer);
            _unread = _unread[count..];
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (FailWrite)
                throw new IOException("Connection interrupted during the test write.");
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(buffer.Span);
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Span[4..]);
            Assert.Equal(length, buffer.Length - 8);
            await _outgoing.Writer.WriteAsync(new Packet(opcode, buffer[8..].ToArray()), token);
            if (BlockWrites)
                await Task.Delay(Timeout.Infinite, token);
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _closeCount);
            EndInput();
            _outgoing.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
