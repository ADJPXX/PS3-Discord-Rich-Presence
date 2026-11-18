using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;
using Xunit;

namespace PS3DiscordRichPresence.Tests;

public sealed class NetworkTests
{
    private const string Thermal = "<a href='/cpursx.ps3?up'>CPU: 63°C<br>\r\nRSX: 59°C</a>";
    private const string Game = "<h2><a target='_blank' href='/tpl/np/BCUS98111/BCUS98111-ver.xml'>BCUS98111</a>" +
                                "&nbsp;<a href='/search'>Ratchet &amp; Clank v01.02</a></h2>";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("<html><b>Maintenance</b></html>")]
    [InlineData("<a target='_blank' href='/news'>PORTAL123</a>")]
    public void UnrecognizedPagesAreUnavailable(string? html)
    {
        Assert.Null(new HtmlParserService().Parse(html));
    }

    [Fact]
    public void ParserDistinguishesTitleXmbAndManagerAndDecodesText()
    {
        var parser = new HtmlParserService();
        const string unrelated = "<a target='_blank' href='/news'>NOTICIAS1</a>";
        var game = Assert.IsType<GameInfo>(parser.Parse(unrelated + Game + Thermal));
        Assert.Equal("BCUS98111", game.TitleId);
        Assert.Equal("Ratchet & Clank", game.Name);
        Assert.Equal("CPU 63°C", game.CpuTemperature);
        Assert.Equal("RSX 59°C", game.RsxTemperature);
        Assert.False(game.IsOnXmb);
        Assert.False(game.IsOnMultiman);

        var xmb = Assert.IsType<GameInfo>(parser.Parse(unrelated + Thermal));
        Assert.True(xmb.IsOnXmb);
        Assert.Null(xmb.TitleId);

        var manager = Assert.IsType<GameInfo>(parser.Parse(Game + "<a href='/search/mmCM'>mmCM</a>" + Thermal));
        Assert.True(manager.IsOnMultiman);
        Assert.False(manager.IsOnXmb);
        Assert.Null(manager.TitleId);

        var unknown = Assert.IsType<GameInfo>(parser.Parse("<a href='/xmb.ps3$exit'>Exit</a>" + Thermal));
        Assert.False(unknown.IsOnXmb);
        Assert.Equal("Unknown", unknown.Name);
    }

    [Fact]
    public void OlderTitleLinksNeedAWebManMarkerAndExplicitXmbWins()
    {
        const string legacy = "<h2><a target='_blank' href='/game'>BCUS98111</a> Game title</h2>";
        const string memory = "<a class='s' href='/browser.ps3$slaunch'>MEM: 1,764 KB (XMB)</a>";
        var parser = new HtmlParserService();
        Assert.Null(parser.Parse(legacy));
        var game = Assert.IsType<GameInfo>(parser.Parse(legacy + Thermal));
        Assert.Equal("Game title", game.Name);
        Assert.Equal("BCUS98111", game.TitleId);
        var menu = Assert.IsType<GameInfo>(parser.Parse(legacy + Thermal + memory));
        Assert.True(menu.IsOnXmb);
        Assert.Equal("PlayStation 3", menu.Name);
        Assert.Null(menu.TitleId);

        var unidentified = Assert.IsType<GameInfo>(parser.Parse(
            "<a href='/prefix/xmb.ps3$exit?menu'>Exit</a>" + Thermal));
        Assert.False(unidentified.IsOnXmb);
        var limitedPermissions = Assert.IsType<GameInfo>(parser.Parse(
            "<a href='/games.ps3'>MEM: 2763 KB</a>" + Thermal));
        Assert.False(limitedPermissions.IsOnXmb);
    }

    [Fact]
    public void TemperatureKeepsTheReportedUnits()
    {
        var game = Assert.IsType<GameInfo>(new HtmlParserService().Parse(
            "<a href='/cpursx.ps3?up'>CPU: 143&deg;F (MAX: 150°F)<br>RSX: 136°F</a>"));
        Assert.Equal("CPU 143°F", game.CpuTemperature);
        Assert.Equal("RSX 136°F", game.RsxTemperature);
    }

    [Theory]
    [InlineData("<hr>")]
    [InlineData(" | ")]
    [InlineData("")]
    public void MissingGameNameDoesNotConsumeAStatusControl(string separator)
    {
        const string id = "<a target='_blank' href='/BCUS98111-ver.xml'>BCUS98111</a>";
        var game = Assert.IsType<GameInfo>(new HtmlParserService().Parse(id + separator + Thermal));
        Assert.Equal("Unknown", game.Name);
        Assert.Equal("BCUS98111", game.TitleId);
    }

    [Fact]
    public async Task DeclaredLegacyEncodingAndUnicodeBomPreserveTheTitle()
    {
        var html = Game.Replace("Ratchet &amp; Clank", "Action");
        var declared = new ByteArrayContent(Encoding.Latin1.GetBytes(html));
        declared.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "windows-1252" };
        var bom = new ByteArrayContent([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(html)]);
        var bodies = new Queue<HttpContent>([declared, bom]);
        var handler = new Handler((_, _) => Reply(bodies.Dequeue(), HttpStatusCode.OK));
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        Assert.Equal("Action", (await service.GetGameInfoAsync())?.Name);
        Assert.Equal("Action", (await service.GetGameInfoAsync())?.Name);
    }

    [Fact]
    public async Task PollsStatusDirectlySupportsIPv6AndDisposesEveryResponse()
    {
        var expected = new Uri("http://[::1]/cpursx.ps3");
        var body = new TrackingContent(Game + Thermal);
        var probe = new TrackingContent("");
        var handler = new Handler((request, _) =>
            Reply(request.RequestUri == expected ? body : probe, HttpStatusCode.OK));
        using (var service = new WebManService(new Config { Ps3Ip = "::1" }, handler))
        {
            Assert.Equal("BCUS98111", (await service.GetGameInfoAsync())?.TitleId);
            Assert.True(body.Disposed);
            Assert.Equal(1, handler.Calls);
            Assert.True(await service.IsPS3OnlineAsync());
            Assert.True(probe.Disposed);
        }
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task ReadStatusReportsReachabilityAndDiagnosticForEachFailure()
    {
        var calls = 0;
        var invalidText = new ByteArrayContent([0xff]);
        var handler = new Handler((_, _) => ++calls switch
        {
            1 => throw new HttpRequestException("Connection refused"),
            2 => Reply(new TrackingContent(""), HttpStatusCode.Forbidden),
            3 => Reply(new TrackingContent("<html>Another page</html>"), HttpStatusCode.OK),
            4 => Reply(invalidText, HttpStatusCode.OK),
            5 => Reply(new BrokenContent(), HttpStatusCode.OK),
            _ => Reply(new TrackingContent(Game + Thermal), HttpStatusCode.OK)
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        var connection = await service.ReadStatusAsync();
        Assert.False(connection.Responded);
        Assert.Null(connection.Game);
        Assert.Contains("Connection refused", connection.Detail);
        Assert.Contains("127.0.0.1/cpursx.ps3", connection.Detail);
        foreach (var reason in new[] { "HTTP 403", "recognized", "Encoding", "communication" })
        {
            var reading = await service.ReadStatusAsync();
            Assert.True(reading.Responded);
            Assert.Null(reading.Game);
            Assert.Contains(reason, reading.Detail);
        }
        var valid = await service.ReadStatusAsync();
        Assert.True(valid.Responded);
        Assert.Equal("BCUS98111", valid.Game?.TitleId);
        Assert.Null(valid.Detail);
    }

    [Fact]
    public async Task BodyDeadlinePreservesProofThatTheEndpointResponded()
    {
        var handler = new Handler((_, _) => Reply(new DelayedContent(), HttpStatusCode.OK));
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler, TimeSpan.FromMilliseconds(100));
        var reading = await service.ReadStatusAsync().WaitAsync(Guard);
        Assert.True(reading.Responded);
        Assert.Null(reading.Game);
        Assert.Contains("Timed out", reading.Detail);
    }

    [Fact]
    public async Task NormalPollingAcceptsResponsesDelayedPastFiveSeconds()
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackingContent(Thermal) };
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        var reading = await service.ReadStatusAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(reading.Responded);
        Assert.True(reading.Game?.IsOnXmb);
        Assert.Null(reading.Detail);
    }

    [Fact]
    public async Task BrokenReadsAndNonSuccessResponsesRecoverOnNextPoll()
    {
        var calls = 0;
        var unavailable = new TrackingContent("Maintenance");
        var handler = new Handler((_, _) => Interlocked.Increment(ref calls) switch
        {
            1 => throw new HttpRequestException("No connection"),
            2 => Reply(unavailable, HttpStatusCode.ServiceUnavailable),
            3 => Reply(new BrokenContent(), HttpStatusCode.OK),
            _ => Reply(new TrackingContent(Thermal), HttpStatusCode.OK)
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        Assert.Null(await service.GetGameInfoAsync());
        Assert.Null(await service.GetGameInfoAsync());
        Assert.True(unavailable.Disposed);
        Assert.Null(await service.GetGameInfoAsync());
        Assert.True(Assert.IsType<GameInfo>(await service.GetGameInfoAsync()).IsOnXmb);
    }

    [Fact]
    public async Task InvalidEncodingAndOverBudgetDocumentsAreUnavailable()
    {
        var invalidBytes = new ByteArrayContent([.. Encoding.UTF8.GetBytes(Thermal), 0xff]);
        var unknownEncoding = new TrackingContent(Game);
        unknownEncoding.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "no-such-charset" };
        var bodies = new Queue<HttpContent>([invalidBytes, unknownEncoding,
            new TrackingContent(Thermal + new string(' ', 512 * 1024))]);
        var handler = new Handler((_, _) => Reply(bodies.Dequeue(), HttpStatusCode.OK));
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        Assert.Null(await service.GetGameInfoAsync());
        Assert.Null(await service.GetGameInfoAsync());
        Assert.Null(await service.GetGameInfoAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebManCancellationDoesNotBecomeOffline(bool probe)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage();
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        using var stop = new CancellationTokenSource();
        Task call = probe ? service.IsPS3OnlineAsync(stop.Token) : service.GetGameInfoAsync(stop.Token);
        await started.Task.WaitAsync(Guard);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Guard));
    }

    [Fact]
    public async Task ASlowWebManReturnsUnavailableWithinItsBudget()
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage();
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler, TimeSpan.FromMilliseconds(100));
        var reading = await service.ReadStatusAsync().WaitAsync(Guard);
        Assert.False(reading.Responded);
        Assert.Null(reading.Game);
        Assert.Contains("Timed out", reading.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationWinsAgainstConcurrentTransportFailure(bool probe)
    {
        using var stop = new CancellationTokenSource();
        var handler = new Handler((_, _) =>
        {
            stop.Cancel();
            throw new IOException("The connection closed during cancellation.");
        });
        using var service = new WebManService(new Config { Ps3Ip = "127.0.0.1" }, handler);
        Task call = probe ? service.IsPS3OnlineAsync(stop.Token) : service.GetGameInfoAsync(stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Guard));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("id with spaces")]
    public async Task MissingOrInvalidTitleMakesNoCoverRequest(string? id)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        using var images = new GameImageService(handler);
        Assert.Equal("xmb", await images.GetImageAsync(id));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CoverSkipsMissingOrHtmlResponsesCachesSuccessAndDisposesResources()
    {
        var missing = new TrackingContent("");
        var maintenance = new TrackingContent("Maintenance");
        var cover = new TrackingContent("JPEG");
        cover.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/US/")) return Reply(missing, HttpStatusCode.NotFound);
            if (path.Contains("/EN/")) return Reply(maintenance, HttpStatusCode.OK);
            Assert.Equal("/ps3/cover/PT/BCUS98111.jpg", path);
            return Reply(cover, HttpStatusCode.OK);
        });
        using (var images = new GameImageService(handler))
        {
            var result = await images.GetImageAsync("bcus98111");
            Assert.Equal("https://art.gametdb.com/ps3/cover/PT/BCUS98111.jpg", result);
            Assert.Equal(result, await images.GetImageAsync("BCUS98111"));
            Assert.Equal(3, handler.Calls);
        }
        Assert.True(cover.Disposed);
        Assert.True(missing.Disposed);
        Assert.True(maintenance.Disposed);
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task CoverSkipsEmptySuccessAndRecoversFromBrokenTransport()
    {
        var calls = 0;
        var handler = new Handler((_, _) => ++calls switch
        {
            1 => Reply(new ByteArrayContent([]), HttpStatusCode.NoContent),
            2 => Reply(new ByteArrayContent([]), HttpStatusCode.OK),
            _ => throw new IOException("Connection lost")
        });
        using var images = new GameImageService(handler);
        Assert.Equal("bcus98111", await images.GetImageAsync("BCUS98111"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CoverSearchDeadlineAndTemporaryFailureCacheBoundWorkAndAllowRecovery()
    {
        var clock = new AdjustableClock();
        var attempt = 0;
        var handler = new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff]) };
        });
        using var images = new GameImageService(handler, TimeSpan.FromMilliseconds(100), clock);
        Assert.Equal("bcus98111", await images.GetImageAsync("BCUS98111").WaitAsync(Guard));
        Assert.Equal("bcus98111", await images.GetImageAsync("BCUS98111"));
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.EndsWith("/US/BCUS98111.jpg", await images.GetImageAsync("BCUS98111"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CoverCancellationPropagatesAndDoesNotCacheFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var handler = new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff]) };
        });
        using var images = new GameImageService(handler);
        using var stop = new CancellationTokenSource();
        var pending = images.GetImageAsync("BCUS98111", stop.Token);
        await entered.Task.WaitAsync(Guard);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        Assert.EndsWith("/US/BCUS98111.jpg", await images.GetImageAsync("BCUS98111"));
    }

    [Fact]
    public async Task ConcurrentCoverLookupsKeepEachResultPairedWithItsOwnTitle()
    {
        const int count = 20;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        var handler = new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref waiting) == count)
                arrived.TrySetResult();
            await release.Task.WaitAsync(Guard, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff]) };
        });
        using var images = new GameImageService(handler, Guard + Guard);
        var ids = Enumerable.Range(0, count).Select(i => $"BCUS{i:D5}").ToArray();
        var tasks = ids.Select(id => images.GetImageAsync(id)).ToArray();
        try
        {
            await arrived.Task.WaitAsync(Guard);
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await Task.WhenAll(tasks).WaitAsync(Guard);
        for (int i = 0; i < ids.Length; i++)
            Assert.EndsWith($"/US/{ids[i]}.jpg", results[i]);
    }

    private static Task<HttpResponseMessage> Reply(HttpContent content, HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = content });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return send(request, token);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingContent(string text) : StringContent(text, Encoding.UTF8, "text/html")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("Read interrupted"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DelayedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The body read must receive the cancellation token.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan interval) => _utc += interval;
    }
}
