using System.IO;
using System.Text.Json;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence.Tests;

public sealed class MonitorSessionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentStopsWaitForWorkerCleanupAndReturnTheSameCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = 0;
        var session = new MonitorSession(async (publish, token) =>
        {
            publish(new MonitorView(true, "Connected"));
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cleaning.SetResult();
                await finish.Task.WaitAsync(Guard);
                Interlocked.Increment(ref finished);
            }
        });
        await entered.Task.WaitAsync(Guard);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            await session.StopAsync();
            Assert.Equal(1, Volatile.Read(ref finished));
        }).ToArray();
        start.SetResult();
        try
        {
            await cleaning.Task.WaitAsync(Guard);
            Assert.All(callers, caller => Assert.False(caller.IsCompleted));
            Assert.Same(session.StopAsync(), session.StopAsync());
        }
        finally
        {
            finish.TrySetResult();
        }
        await Task.WhenAll(callers).WaitAsync(Guard);
        Assert.True(session.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task FatalWorkerErrorPreservesVisibilityAndWaitsForCleanup(bool? lastPreference, bool expectedUseTray)
    {
        var cleaned = false;
        var session = new MonitorSession(async (publish, _) =>
        {
            try
            {
                await Task.Yield();
                if (lastPreference is bool useTray)
                    publish(new MonitorView(useTray, "Connecting"));
                throw new IOException("file inaccessible");
            }
            finally
            {
                cleaned = true;
            }
        });
        await session.Completion.WaitAsync(Guard);
        Assert.True(cleaned);
        Assert.Equal(expectedUseTray, session.Latest!.UseTray);
        Assert.Contains("file inaccessible", session.Latest.Message);
        await session.StopAsync().WaitAsync(Guard);
    }

    [Fact]
    public async Task RegistryDeniedStillPollsAndShowsActionableWarning()
    {
        using var stop = new CancellationTokenSource(Guard);
        var calls = 0;
        var views = new List<MonitorView>();
        var game = new GameInfo { Name = "PlayStation 3", IsOnXmb = true };
        var reading = new WebManReading(game, true, null);
        var run = MonitorSession.RunPollingAsync(new Config(), _ => throw new UnauthorizedAccessException("Run"),
            _ =>
            {
                calls++;
                return Task.FromResult(new PollResult(true, reading, null, "Discord confirmed the current activity."));
            }, view =>
            {
                views.Add(view);
                if (view.Message.Contains("PS3:"))
                    stop.Cancel();
            }, stop.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        Assert.Equal(1, calls);
        Assert.Contains("Run", views.Last().Warning);
        Assert.Contains("Discord: ✅ Online", views.Last().Message);
    }

    [Fact]
    public async Task SustainedOutageBacksOffAndRecoveryRestoresConfiguredCadence()
    {
        using var stop = new CancellationTokenSource(Guard);
        var delays = new List<double>();
        var run = MonitorSession.RunPollingAsync(new Config(), _ => { },
            _ =>
            {
                var connected = delays.Count == 8;
                var reading = connected
                    ? new WebManReading(new GameInfo { Name = "PlayStation 3" }, true, null)
                    : new WebManReading(null, false, "No response.");
                return Task.FromResult(new PollResult(true, reading, null, null));
            }, _ => { }, stop.Token,
            (delay, _) =>
            {
                delays.Add(delay.TotalSeconds);
                if (delays.Count == 10)
                    stop.Cancel();
                return Task.CompletedTask;
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        Assert.Equal(new double[] { 10, 10, 10, 10, 10, 20, 40, 60, 3, 10 }, delays);
    }

    [Fact]
    public async Task MissingDiscordDoesNotPushConsoleRefreshesToOneMinute()
    {
        using var stop = new CancellationTokenSource(Guard);
        var delays = new List<double>();
        var reading = new WebManReading(new GameInfo { Name = "PlayStation 3", IsOnXmb = true }, true, null);
        var run = MonitorSession.RunPollingAsync(new Config(), _ => { },
            _ => Task.FromResult(new PollResult(false, reading, null, "Discord is offline.")),
            _ => { }, stop.Token,
            (delay, _) =>
            {
                delays.Add(delay.TotalSeconds);
                if (delays.Count == 7)
                    stop.Cancel();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        Assert.Equal(Enumerable.Repeat(10d, 7), delays);
    }

    [Fact]
    public void ApplicationManifestDoesNotShipUnusedSqliteLibraries()
    {
        var manifest = Path.ChangeExtension(typeof(App).Assembly.Location, ".deps.json");
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var names = json.RootElement.GetProperty("libraries").EnumerateObject().Select(property => property.Name);
        Assert.DoesNotContain(names, name => name.StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase));
    }
}
