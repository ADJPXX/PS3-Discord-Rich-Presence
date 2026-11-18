using System.Diagnostics;
using System.IO;
using System.Security;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

internal enum MonitorPhase { Setup, Connecting, Monitoring, Stopped }

internal sealed record MonitorView(bool UseTray, string Message, string? Warning = null, string? Details = null)
{
    internal MonitorPhase Phase { get; init; } = MonitorPhase.Connecting;
    internal PollResult? Sample { get; init; }
    internal string? ConsoleAddress { get; init; }
    internal DateTimeOffset? CheckedAt { get; init; }
}

internal sealed class MonitorSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _stopLock = new();
    private readonly Task _worker;
    private Task? _stopping;
    private MonitorView? _latest;

    internal MonitorView? Latest => Volatile.Read(ref _latest);
    internal Task Completion => _worker;

    internal MonitorSession(Func<Action<MonitorView>, CancellationToken, Task> run)
    {
        _worker = Task.Run(async () =>
        {
            try
            {
                await run(view => Volatile.Write(ref _latest, view), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Expected when the UI or Windows closes the session.
            }
            catch (Exception error)
            {
                Trace.TraceError(error.ToString());
                Volatile.Write(ref _latest, new MonitorView(Latest?.UseTray ?? true, $"Monitoring stopped.\n{error.Message}")
                {
                    Phase = MonitorPhase.Stopped
                });
            }
        });
    }

    internal Task StopAsync()
    {
        lock (_stopLock)
            return _stopping ??= DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await _worker.ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    internal static async Task RunAsync(Action<MonitorView> publish, CancellationToken token)
    {
        var config = await ConfigService.ReadJsonAsync(token, (message, hideToTray) =>
            publish(new MonitorView(hideToTray, message) { Phase = MonitorPhase.Setup }));
        token.ThrowIfCancellationRequested();
        // Remember the preference even if a service constructor fails before the first poll.
        publish(new MonitorView(config.HideToTray, "Connecting to the PS3 and Discord.") { ConsoleAddress = config.Ps3Ip });
        using var webMan = new WebManService(config);
        // The built-in Discord application registers "xmb". Custom applications may have per-title assets.
        using var images = new GameImageService(fallbackAsset: config.ClientId == Config.DefaultClientId ? "xmb" : null);
        await using var discord = new DiscordService(config.ClientId);
        var monitor = new PresenceMonitor(config, discord, webMan.ReadStatusAsync, images.GetImageAsync, TimeProvider.System);
        await RunPollingAsync(config, new StartupService().ApplyStartupSetting, monitor.PollAsync, publish, token);
    }

    internal static async Task RunPollingAsync(Config config, Action<Config> applyStartup,
        Func<CancellationToken, Task<PollResult>> poll, Action<MonitorView> publish, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        wait ??= Task.Delay;
        var failures = 0;
        var warning = ConfigureStartup(config, applyStartup);
        publish(new MonitorView(config.HideToTray, "Connecting to the PS3 and Discord.", warning)
        {
            ConsoleAddress = config.Ps3Ip
        });
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = await poll(token);
            publish(new MonitorView(config.HideToTray, result.DisplayText, warning, result.Describe(config.Ps3Ip))
            {
                Phase = MonitorPhase.Monitoring,
                Sample = result,
                CheckedAt = DateTimeOffset.UtcNow,
                ConsoleAddress = config.Ps3Ip
            });
            var seconds = config.UpdateIntervalSeconds;
            if (result.DiscordOnline && result.Ps3Online)
            {
                failures = 0;
            }
            else if (result.Ps3Online)
            {
                failures = 0;
                seconds = config.ReconnectIntervalSeconds;
            }
            else
            {
                // Preserve the first five probes. Back off during sustained outages.
                failures = Math.Min(8, failures + 1);
                var multiplier = 1 << Math.Max(0, failures - 5);
                var ceiling = Math.Max(60, config.ReconnectIntervalSeconds);
                seconds = (int)Math.Min(ceiling, (long)config.ReconnectIntervalSeconds * multiplier);
            }
            await wait(TimeSpan.FromSeconds(seconds), token);
        }
    }

    private static string? ConfigureStartup(Config config, Action<Config> apply)
    {
        try
        {
            apply(config);
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException or InvalidOperationException)
        {
            Trace.TraceWarning(error.ToString());
            return $"Windows startup: {error.Message}";
        }
    }
}
