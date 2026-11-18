using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public interface IDiscordPresence
{
    bool IsPipeConnected { get; }
    string? LastError { get; }
    Task<bool> ConnectAsync(CancellationToken token);
    Task<bool> UpdateAsync(GameInfo game, string? state, string image, DateTime? startTime, CancellationToken token);
    Task<bool> ClearActivityAsync(CancellationToken token);
}

internal sealed record PollResult(bool DiscordOnline, WebManReading Reading, string? Temperatures, string? DiscordDetail)
{
    public bool Ps3Online => Reading.Game is not null;
    private string ConsoleStatus => Ps3Online ? "✅ Online" :
        Reading.Responded ? "Responded, no webMAN status" : "No response";

    public string DisplayText =>
        $"Discord: {(DiscordOnline ? "✅ Online" : "❌ Offline")}\n" +
        $"PS3: {ConsoleStatus}" +
        (Temperatures is null ? string.Empty : $"\n{Temperatures}");

    public string Describe(string? ps3Ip)
    {
        var game = Reading.Game;
        var observed = game switch
        {
            { IsOnXmb: true } => "XMB",
            { IsOnMultiman: true } => "multiMAN",
            { TitleId: not null } => $"{game.Name} ({game.TitleId})",
            _ => game?.Name
        };
        var consoleDetail = observed is null ? Reading.Detail : $"Detected on PS3 ({ps3Ip}): {observed}.";
        return string.Join("\n", new[] { consoleDetail, DiscordDetail }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }
}

// One worker calls PollAsync sequentially for the whole lifetime of a session.
internal sealed class PresenceMonitor(
    Config config,
    IDiscordPresence discord,
    Func<CancellationToken, Task<WebManReading>> readStatus,
    Func<string?, CancellationToken, Task<string>> getImage,
    TimeProvider clock)
{
    private const int OfflineThreshold = 5;
    private int _consecutiveFailures;
    private bool _clearConfirmed;
    private string? _gameKey;
    private DateTime? _startedAt;
    private sealed record Activity(string? Name, string? Id, string Image, string? Temperatures, DateTime? Start);
    private Activity? _lastConfirmed;

    internal async Task<PollResult> PollAsync(CancellationToken token)
    {
        var reading = await readStatus(token);
        var game = reading.Game;
        var connected = discord.IsPipeConnected;
        if (!connected)
        {
            _clearConfirmed = false;
            _lastConfirmed = null;
            connected = await discord.ConnectAsync(token);
        }

        if (game is null)
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, OfflineThreshold);
            if (_consecutiveFailures == OfflineThreshold)
            {
                _gameKey = null;
                _startedAt = null;
                _lastConfirmed = null;
                if (connected && !_clearConfirmed)
                {
                    _clearConfirmed = await discord.ClearActivityAsync(token);
                    connected = _clearConfirmed;
                }
            }
            var detail = !connected ? discord.LastError :
                _clearConfirmed ? "Discord confirmed that the activity was removed." :
                "Discord connected. Waiting for a valid PS3 reading.";
            return new PollResult(connected, reading, null, detail);
        }

        _consecutiveFailures = 0;
        _clearConfirmed = false;
        var key = game.TitleId ?? game.Name;
        if (_startedAt is null || !string.Equals(_gameKey, key, StringComparison.Ordinal))
        {
            _gameKey = key;
            _startedAt = clock.GetUtcNow().UtcDateTime;
        }

        var temperatures = config.ShowTemperature
            ? string.Join(" | ", new[] { game.CpuTemperature, game.RsxTemperature }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : null;
        if (temperatures == string.Empty)
            temperatures = null;
        if (connected)
        {
            var image = await getImage(game.TitleId, token);
            var activity = new Activity(game.Name, game.TitleId, image, temperatures, _startedAt);
            if (activity != _lastConfirmed)
            {
                connected = await discord.UpdateAsync(game, temperatures, image, _startedAt, token);
                _lastConfirmed = connected ? activity : null;
            }
        }
        return new PollResult(connected, reading, temperatures,
            connected ? "Discord confirmed the current activity." : discord.LastError);
    }
}
