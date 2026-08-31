using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using PS3DiscordRichPresence.Helpers;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence;

public partial class App
{
    private CancellationTokenSource? _cts;

    private TaskbarIcon? _trayIcon;

    private Config? _config;

    private DiscordService? _discord;

    private WebManService? _webMan;

    private GameInfo? _currentGame;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _cts = new CancellationTokenSource();

        _config = ConfigService.ReadJson();

        if (_config.MinimizeToTray)
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = IconHelper.GetTrayIcon(),
                ToolTipText = "PS3 Discord Rich Presence"
            };

            var menu = new ContextMenu();

            var exit = new MenuItem
            {
                Header = "Sair"
            };

            exit.Click += (_, _) =>
            {
                Shutdown();
            };

            menu.Items.Add(exit);

            _trayIcon.ContextMenu = menu;
        }

        Task.Run(() => MainAsync(_cts.Token));

        Task.Run(() => UpdateTrayTooltip(_cts.Token));
    }


    private async Task MainAsync(CancellationToken token)
    {
        var config = _config!;

        var task = new StartupService();

        _discord = new DiscordService(config.ClientId);

        _webMan = new WebManService(config);

        var imageService = new GameImageService();

        var oldTime = DateTime.UtcNow;

        var activityCleared = false;

        string? oldGame = null;

        string? state = null;

        task.TaskVerification(config);

        _discord.ConnectPipe();

        while (!token.IsCancellationRequested)
        {
            if (!await _discord.IsDiscordOnlineAsync())
            {
                _discord.ConnectPipe();

                await Task.Delay(TimeSpan.FromSeconds(config.ReconnectIntervalSeconds), token);

                continue;
            }

            if (!await _webMan.IsPS3OnlineAsync())
            {
                if (!activityCleared)
                {
                    _discord.ClearActivity();
                    
                    activityCleared = true;

                    oldGame = null;
                    
                    oldTime = DateTime.UtcNow;
                    
                    state = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(config.ReconnectIntervalSeconds), token);

                continue;
            }

            var game = await _webMan.GetGameInfoAsync();

            _currentGame = game;

            var image = await imageService.GetImageAsync(game?.TitleId);

            var (currentGame, currentTime) = await _discord.GetCurrentTime(oldTime, oldGame!, game?.Name);

            if (config.ShowTemperature)
            {
                state = $"{game?.CpuTemperature} | {game?.RsxTemperature}";
            }

            _discord.Update(game!, state, image, currentTime);

            activityCleared = false;

            oldTime = currentTime;

            oldGame = currentGame;

            await Task.Delay(TimeSpan.FromSeconds(config.UpdateIntervalSeconds), token);
        }
    }


    private async Task UpdateTrayTooltip(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_trayIcon != null && _discord != null && _webMan != null)
            {
                var discordOnline = await _discord.IsDiscordOnlineAsync();

                var ps3Online = await _webMan.IsPS3OnlineAsync();

                var tooltip = "PS3 Discord Rich Presence\n\n" + $"Discord: {(discordOnline ? "✅ Online" : "❌ Offline")}\n" + $"PS3: {(ps3Online ? "✅ Online" : "❌ Offline")}";

                if (discordOnline && ps3Online && _config != null)
                {
                    if (_config.ShowTemperature && _currentGame != null)
                    {
                        tooltip += "\n\nTemperatura:\n";

                        tooltip += $"CPU: {_currentGame.CpuTemperature}°C\n";

                        tooltip += $"RSX: {_currentGame.RsxTemperature}°C";
                    }
                }

                Current.Dispatcher.Invoke(() =>
                {
                    _trayIcon.ToolTipText = tooltip;
                });
            }

            await Task.Delay(
                TimeSpan.FromSeconds(5),
                token
            );
        }
    }


    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();

        _trayIcon?.Dispose();

        base.OnExit(e);
    }
}