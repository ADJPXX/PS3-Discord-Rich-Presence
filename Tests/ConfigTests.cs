using System.IO;
using System.Text;
using System.Text.Json;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;
using Xunit;

namespace PS3DiscordRichPresence.Tests;

public sealed class ConfigTests : IDisposable
{
    private const string Valid = """{"Ip":"192.168.1.35"}""";
    private readonly string _directory = Directory.CreateTempSubdirectory("ps3-config-").FullName;
    private string ConfigPath => Path.Combine(_directory, "PS3config.json");
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task MinimalReadmeConfigKeepsClientAndTrayDefaults()
    {
        await File.WriteAllTextAsync(ConfigPath, Valid);
        using var stop = new CancellationTokenSource(Limit);
        var config = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token);
        Assert.Equal("192.168.1.35", config.Ps3Ip);
        Assert.Equal(1528636206638694400L, config.ClientId);
        Assert.True(config.MinimizeToTray);
        Assert.Equal(3, config.UpdateIntervalSeconds);
        Assert.Equal(10, config.ReconnectIntervalSeconds);
    }

    [Theory]
    [InlineData("", "JSON")]
    [InlineData("{", "JSON")]
    [InlineData("null", "Ip")]
    [InlineData("{}", "Ip")]
    [InlineData("""{"Ip":"YOUR_PS3_IP_HERE"}""", "Ip")]
    [InlineData("""{"Ip":null}""", "Ip")]
    [InlineData("""{"Ip":"console"}""", "Ip")]
    [InlineData("""{"Ip":"127.0.0.1","ClientId":0}""", "ClientId")]
    [InlineData("""{"Ip":"127.0.0.1","ClientId":"abc"}""", "ClientId")]
    public async Task InvalidSnapshotIsReportedAndRepaired(string invalid, string field)
    {
        await File.WriteAllTextAsync(ConfigPath, invalid);
        using var stop = new CancellationTokenSource(Limit);
        var diagnostic = "";
        var config = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token, message =>
        {
            diagnostic = message;
            File.WriteAllText(ConfigPath, Valid);
        });
        Assert.Contains(ConfigPath, diagnostic);
        Assert.Contains(field, diagnostic);
        Assert.Equal("192.168.1.35", config.Ps3Ip);
    }

    [Fact]
    public async Task InvalidEncodingCanBeReplacedByExistingUtf16Config()
    {
        await File.WriteAllBytesAsync(ConfigPath, new byte[] { 0xff, 0x32 });
        using var stop = new CancellationTokenSource(Limit);
        var diagnostic = "";
        var config = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token, message =>
        {
            diagnostic = message;
            File.WriteAllText(ConfigPath, Valid, Encoding.Unicode);
        });
        Assert.Contains("UTF-8", diagnostic);
        Assert.Equal("192.168.1.35", config.Ps3Ip);
    }

    [Fact]
    public async Task OversizedConfigCanBeRepairedWithoutDeserializingIt()
    {
        await File.WriteAllTextAsync(ConfigPath, new string(' ', ConfigService.MaximumFileBytes) + Valid);
        using var stop = new CancellationTokenSource(Limit);
        var warning = "";
        var result = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token, message =>
        {
            warning = message;
            File.WriteAllText(ConfigPath, Valid);
        });
        Assert.Contains("64 KiB", warning);
        Assert.Equal("192.168.1.35", result.Ps3Ip);
    }

    [Fact]
    public async Task ValidatedSnapshotIsReturnedWithoutASecondRead()
    {
        using var stop = new CancellationTokenSource(Limit);
        var calls = 0;
        Task<string> ReadEditedFile(string path, CancellationToken token) =>
            Task.FromResult(++calls == 1 ? Valid : "{");
        var config = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token, readText: ReadEditedFile);
        Assert.Equal("192.168.1.35", config.Ps3Ip);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentFirstRunPublishesACompleteTemplateOnceAndPreservesEdits()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(Limit);
        var publishers = Enumerable.Range(0, 24).Select(async _ =>
        {
            await start.Task.WaitAsync(stop.Token);
            await ConfigService.InstallTemplateAsync(ConfigPath, stop.Token);
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(publishers).WaitAsync(stop.Token);
        var template = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(ConfigPath, stop.Token));
        Assert.Equal("YOUR_PS3_IP_HERE", template!.Ps3Ip);
        await File.WriteAllTextAsync(ConfigPath, Valid, stop.Token);
        await ConfigService.InstallTemplateAsync(ConfigPath, stop.Token);
        Assert.Equal(Valid, await File.ReadAllTextAsync(ConfigPath, stop.Token));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingFileWaitsForUserAndCancelsPromptly()
    {
        using var stop = new CancellationTokenSource(Limit);
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = ConfigService.ReadJsonAsync(ConfigPath, TimeSpan.FromMinutes(1), stop.Token,
            message => waiting.TrySetResult(message));
        Assert.Contains(ConfigPath, await waiting.Task.WaitAsync(stop.Token));
        Assert.True(File.Exists(ConfigPath));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        using var exclusive = File.Open(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public async Task LockedFileCanBeReadAsSoonAsEditorReleasesIt()
    {
        await File.WriteAllTextAsync(ConfigPath, Valid);
        using var stop = new CancellationTokenSource(Limit);
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Config> read;
        using (File.Open(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            read = ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token, message => waiting.TrySetResult(message));
            Assert.Contains(ConfigPath, await waiting.Task.WaitAsync(stop.Token));
            Assert.False(read.IsCompleted);
        }
        Assert.Equal("192.168.1.35", (await read).Ps3Ip);
    }

    [Fact]
    public async Task PermanentReadFailureIsReportedToCaller()
    {
        using var stop = new CancellationTokenSource(Limit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token,
                readText: (_, _) => throw new UnauthorizedAccessException()));
        await Assert.ThrowsAsync<IOException>(() =>
            ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token,
                readText: (_, _) => throw new IOException("Permanent drive failure")));
    }

    [Theory]
    [InlineData(int.MinValue, int.MinValue, 3, 10)]
    [InlineData(4, 16, 4, 16)]
    [InlineData(int.MaxValue, int.MaxValue, 4294967, 4294967)]
    public async Task AllIntervalsAreValidForTaskDelay(int update, int reconnect, int expectedUpdate, int expectedReconnect)
    {
        var json = JsonSerializer.Serialize(new Config
        {
            Ps3Ip = "127.0.0.1", UpdateIntervalSeconds = update, ReconnectIntervalSeconds = reconnect
        });
        await File.WriteAllTextAsync(ConfigPath, json);
        using var stop = new CancellationTokenSource(Limit);
        var config = await ConfigService.ReadJsonAsync(ConfigPath, Poll, stop.Token);
        Assert.Equal(expectedUpdate, config.UpdateIntervalSeconds);
        Assert.Equal(expectedReconnect, config.ReconnectIntervalSeconds);
        stop.Cancel();
        Assert.True(Task.Delay(TimeSpan.FromSeconds(config.UpdateIntervalSeconds), stop.Token).IsCanceled);
        Assert.True(Task.Delay(TimeSpan.FromSeconds(config.ReconnectIntervalSeconds), stop.Token).IsCanceled);
    }
}
