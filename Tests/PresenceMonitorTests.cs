using NSubstitute;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence.Tests;

public sealed class PresenceMonitorTests
{
    private readonly IDiscordPresence _discord = Substitute.For<IDiscordPresence>();
    private readonly Func<CancellationToken, Task<WebManReading>> _readStatus =
        Substitute.For<Func<CancellationToken, Task<WebManReading>>>();
    private readonly Func<string?, CancellationToken, Task<string>> _getImage =
        Substitute.For<Func<string?, CancellationToken, Task<string>>>();
    private readonly ManualClock _clock = new();
    private readonly PresenceMonitor _monitor;
    private static readonly CancellationToken Token = CancellationToken.None;

    public PresenceMonitorTests()
    {
        _discord.IsPipeConnected.Returns(true);
        _discord.UpdateAsync(Arg.Any<GameInfo>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Token)
            .Returns(true);
        _discord.ClearActivityAsync(Token).Returns(true);
        _getImage(Arg.Any<string?>(), Token).Returns("cover");
        _monitor = new PresenceMonitor(new Config { ShowTemperature = true }, _discord, _readStatus, _getImage, _clock);
    }

    [Fact]
    public async Task MissingStatusDoesNotPublishNullOrEndMonitoring()
    {
        SetStatus(null, false, "webMAN did not return a readable status.");
        var first = await _monitor.PollAsync(Token);
        Assert.False(first.Ps3Online);
        await _getImage.DidNotReceiveWithAnyArgs().Invoke(default, default);
        await _discord.DidNotReceiveWithAnyArgs().UpdateAsync(null!, null, "", null, default);

        var game = new GameInfo { Name = "Journey", TitleId = "BCUS98316" };
        SetStatus(game);
        var next = await _monitor.PollAsync(Token);

        Assert.True(next.Ps3Online);
        await _discord.Received(1).UpdateAsync(game, null, "cover", _clock.GetUtcNow().UtcDateTime, Token);
    }

    [Fact]
    public async Task FifthConsecutiveFailureClearsOnceAfterConfirmationAndRetriesFailure()
    {
        SetStatus(null, false, "webMAN did not return a readable status.");
        _discord.ClearActivityAsync(Token).Returns(false, true);
        for (var index = 0; index < 4; index++)
            await _monitor.PollAsync(Token);
        await _discord.DidNotReceive().ClearActivityAsync(Token);

        Assert.False((await _monitor.PollAsync(Token)).DiscordOnline);
        Assert.True((await _monitor.PollAsync(Token)).DiscordOnline);
        await _monitor.PollAsync(Token);
        await _discord.Received(2).ClearActivityAsync(Token);

        SetStatus(new GameInfo { Name = "PlayStation 3", IsOnXmb = true });
        await _monitor.PollAsync(Token);
        SetStatus(null, false, "webMAN did not return a readable status.");
        for (var index = 0; index < 5; index++)
            await _monitor.PollAsync(Token);

        await _discord.Received(3).ClearActivityAsync(Token);
    }

    [Fact]
    public async Task TooltipAndPresenceUseOneSampleAndDoNotDuplicateUnits()
    {
        var game = new GameInfo { Name = "PlayStation 3", CpuTemperature = "CPU 52°C", RsxTemperature = "RSX 55°C" };
        SetStatus(game);
        var result = await _monitor.PollAsync(Token);

        Assert.Equal("CPU 52°C | RSX 55°C", result.Temperatures);
        Assert.Equal("Discord: ✅ Online\nPS3: ✅ Online\nCPU 52°C | RSX 55°C", result.DisplayText);
        await _readStatus.Received(1).Invoke(Token);
        await _discord.Received(1).UpdateAsync(game, result.Temperatures, "cover", Arg.Any<DateTime?>(), Token);

        SetStatus(null, false, "webMAN did not return a readable status.");
        Assert.Null((await _monitor.PollAsync(Token)).Temperatures);
    }

    [Fact]
    public async Task Ps3StatusRemainsAvailableWhenDiscordIsOffline()
    {
        _discord.IsPipeConnected.Returns(false);
        _discord.ConnectAsync(Token).Returns(false);
        SetStatus(new GameInfo { Name = "PlayStation 3" });
        var result = await _monitor.PollAsync(Token);

        Assert.True(result.Ps3Online);
        Assert.False(result.DiscordOnline);
        Assert.Contains("PS3: ✅ Online", result.DisplayText);
        await _getImage.DidNotReceiveWithAnyArgs().Invoke(default, default);
    }

    [Fact]
    public async Task TwoTitleIdsWithSameNameStartDistinctSessions()
    {
        GameInfo game = new() { Name = "Demo", TitleId = "BCES12345" };
        SetStatus(game);
        var firstTime = _clock.GetUtcNow().UtcDateTime;
        await _monitor.PollAsync(Token);
        _clock.Now = _clock.Now.AddMinutes(2);
        await _monitor.PollAsync(Token);
        await _discord.Received(1).UpdateAsync(game, null, "cover", firstTime, Token);

        game = new GameInfo { Name = "Demo", TitleId = "BCES98765" };
        SetStatus(game);
        await _monitor.PollAsync(Token);
        await _discord.Received(1).UpdateAsync(game, null, "cover", _clock.GetUtcNow().UtcDateTime, Token);
    }

    [Fact]
    public async Task IdenticalActivityIsRetriedUntilAcknowledgedAndRepublishedOnReconnect()
    {
        var game = new GameInfo { Name = "Journey", TitleId = "BCUS98316" };
        SetStatus(game);
        _discord.UpdateAsync(game, null, "cover", Arg.Any<DateTime?>(), Token).Returns(false, true);

        for (var index = 0; index < 3; index++)
            await _monitor.PollAsync(Token);
        await _discord.Received(2).UpdateAsync(game, null, "cover", Arg.Any<DateTime?>(), Token);

        _discord.IsPipeConnected.Returns(false);
        _discord.ConnectAsync(Token).Returns(true);
        await _monitor.PollAsync(Token);
        await _discord.Received(3).UpdateAsync(game, null, "cover", Arg.Any<DateTime?>(), Token);
    }

    [Fact]
    public async Task AbruptCancellationOfStatusReadSkipsAllOutputs()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _readStatus(stop.Token).Returns(async call =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new WebManReading(null, false, "Canceled.");
        });

        var poll = _monitor.PollAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll.WaitAsync(TimeSpan.FromSeconds(5)));
        await _discord.DidNotReceiveWithAnyArgs().UpdateAsync(null!, null, "", null, default);
    }

    [Fact]
    public async Task RecognizedXmbShowsConfirmedActivityOnlyAfterAcknowledgement()
    {
        SetStatus(new GameInfo { Name = "PlayStation 3", IsOnXmb = true });
        _discord.UpdateAsync(Arg.Any<GameInfo>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Token)
            .Returns(false, true);

        var rejected = await _monitor.PollAsync(Token);
        Assert.False(rejected.DiscordOnline);
        Assert.DoesNotContain("confirmed", rejected.Describe("192.0.2.1"), StringComparison.OrdinalIgnoreCase);

        var accepted = await _monitor.PollAsync(Token);
        Assert.True(accepted.DiscordOnline);
        Assert.Contains("Discord confirmed the current activity.", accepted.Describe("192.0.2.1"));
    }

    [Fact]
    public async Task ReachableWebManAndDiscordDenialHaveIndependentDiagnostics()
    {
        _discord.IsPipeConnected.Returns(false);
        _discord.ConnectAsync(Token).Returns(false);
        _discord.LastError.Returns("Discord rejected the local connection.");
        SetStatus(null, true, "http://192.0.2.1/cpursx.ps3 returned an unrecognized page.");

        var result = await _monitor.PollAsync(Token);

        Assert.True(result.Reading.Responded);
        Assert.False(result.Ps3Online);
        Assert.False(result.DiscordOnline);
        Assert.Contains("Responded, no webMAN status", result.DisplayText);
        Assert.Contains("unrecognized page", result.Describe("192.0.2.1"));
        Assert.Contains("Discord rejected the local connection.", result.Describe("192.0.2.1"));
    }

    private void SetStatus(GameInfo? game, bool responded = true, string? detail = null)
    {
        var reading = new WebManReading(game, responded, detail);
        _readStatus(Token).Returns(Task.FromResult(reading));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
