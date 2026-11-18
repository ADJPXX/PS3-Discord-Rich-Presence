using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using PS3DiscordRichPresence.Helpers;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence;

public partial class App
{
    private readonly Func<Action<MonitorView>, CancellationToken, Task> _run;
    private readonly Func<Icon> _loadIcon;
    private MonitorSession? _session;
    private DispatcherTimer? _timer;
    private TaskbarIcon? _tray;
    private Icon? _icon;
    private MainWindow? _statusWindow;
    private MonitorView? _displayed;
    private long _retryTrayAfter;
    private bool _closing;
    private bool _restarting;

    public App() : this(MonitorSession.RunAsync) { }

    internal App(Func<Action<MonitorView>, CancellationToken, Task> run, Func<Icon>? loadIcon = null)
    {
        _run = run;
        _loadIcon = loadIcon ?? IconHelper.GetTrayIcon;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _session = new MonitorSession(_run);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += RefreshView;
        _timer.Start();
    }

    internal TaskbarIcon? Tray => _tray;

    private void RefreshView(object? sender, EventArgs e)
    {
        var view = _session?.Latest;
        if (_closing || view is null)
            return;

        if (view.UseTray)
        {
            if (_statusWindow?.IsVisible == true)
                _statusWindow.Hide();
            if (_tray is null && Environment.TickCount64 >= _retryTrayAfter)
                CreateTray();
            if (_tray is not null && view != _displayed)
            {
                var tooltip = $"PS3 Discord Rich Presence\n{view.Warning ?? view.Message}";
                _tray.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";
            }
        }
        else if (view != _displayed)
        {
            DisposeTray();
            ShowDashboard(view);
        }
        _displayed = view;
    }

    private void CreateTray()
    {
        try
        {
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit configuration" };
            edit.Click += (_, _) =>
            {
                if (ConfigurationEditor.TryOpen() is { } error)
                    _tray?.ShowBalloonTip("Configuration", error, BalloonIcon.Warning);
            };
            menu.Items.Add(edit);
            var reload = new MenuItem { Header = "Reload configuration" };
            reload.Click += Reconnect;
            menu.Items.Add(reload);
            var exit = new MenuItem { Header = "Exit" };
            exit.Click += RequestExit;
            menu.Items.Add(exit);
            _icon = _loadIcon();
            _tray = new TaskbarIcon();
            _tray.Icon = _icon;
            _tray.ContextMenu = menu;
            _displayed = null;
        }
        catch (Exception error) when (error is IOException or ArgumentException or ExternalException)
        {
            DisposeTray();
            System.Diagnostics.Trace.TraceWarning("Notification area icon is unavailable: {0}", error.Message);
            _retryTrayAfter = Environment.TickCount64 + 10_000;
        }
    }

    private void ShowDashboard(MonitorView view)
    {
        if (_statusWindow is null)
        {
            _statusWindow = new MainWindow();
            _statusWindow.Closed += RequestExit;
            _statusWindow.ExitButton.Click += RequestExit;
            _statusWindow.ReconnectButton.Click += Reconnect;
        }
        _statusWindow.Render(view);
        if (!_statusWindow.IsVisible)
            _statusWindow.Show();
    }

    private async void Reconnect(object sender, RoutedEventArgs e) => await ReloadAsync();

    internal async Task ReloadAsync()
    {
        if (_closing || _restarting || _session is null)
            return;

        _restarting = true;
        _timer?.Stop();
        if (_statusWindow is not null)
        {
            _statusWindow.ReconnectButton.IsEnabled = false;
            _statusWindow.Hide();
        }
        try
        {
            await _session.StopAsync();
            if (_closing)
                return;

            _displayed = null;
            _retryTrayAfter = 0;
            _session = new MonitorSession(_run);
            _timer?.Start();
        }
        finally
        {
            _restarting = false;
            if (_statusWindow is not null)
                _statusWindow.ReconnectButton.IsEnabled = !_closing;
        }
    }

    private async void RequestExit(object? sender, EventArgs e)
    {
        if (_closing)
            return;
        _closing = true;
        _timer?.Stop();
        try
        {
            if (_session is not null)
                await _session.StopAsync();
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _closing = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= RefreshView;
        }
        try
        {
            // Windows can end the session without invoking either UI exit command.
            // The worker runs on the pool and never posts to or waits for the dispatcher.
            _session?.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            DisposeTray();
            base.OnExit(e);
        }
    }

    private void DisposeTray()
    {
        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        finally
        {
            _icon?.Dispose();
            _icon = null;
        }
    }
}
