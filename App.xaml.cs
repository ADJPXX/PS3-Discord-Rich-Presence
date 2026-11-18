using System.Drawing;
using System.ComponentModel;
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
    private string? _trayError;
    private bool _statusRequested;
    private string? _dismissedWarning;
    private bool _closing;

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

    private void RefreshView(object? sender, EventArgs e)
    {
        var view = _session?.Latest;
        if (_closing || view is null || view == _displayed)
            return;
        _displayed = view;

        if (view.UseTray && _tray is null && _trayError is null)
        {
            try
            {
                var menu = new ContextMenu();
                var status = new MenuItem { Header = "View status" };
                status.Click += (_, _) => ShowStatus();
                menu.Items.Add(status);
                var exit = new MenuItem { Header = "Exit" };
                exit.Click += RequestExit;
                menu.Items.Add(exit);
                _icon = _loadIcon();
                _tray = new TaskbarIcon();
                _tray.Icon = _icon;
                _tray.ContextMenu = menu;
            }
            catch (Exception error) when (error is IOException or ArgumentException or ExternalException)
            {
                DisposeTray();
                _trayError = $"Notification area icon is unavailable. {error.Message}";
            }
        }

        if (_tray is not null)
            _tray.ToolTipText = view.UseTray ? "PS3 Discord Rich Presence\n" + view.Message : "PS3 Discord Rich Presence";

        if (view.Warning is null)
            _dismissedWarning = null;
        if (view.UseTray && _tray is not null && !_statusRequested && view.Warning == _dismissedWarning)
        {
            _statusWindow?.Hide();
            return;
        }

        RenderStatus(view);
    }

    internal void ShowStatus()
    {
        if (_closing || _displayed is null)
            return;
        _statusRequested = true;
        RenderStatus(_displayed);
        if (_statusWindow!.WindowState == WindowState.Minimized)
            _statusWindow.WindowState = WindowState.Normal;
        _statusWindow.Activate();
    }

    private void RenderStatus(MonitorView view)
    {
        if (_statusWindow is null)
        {
            _statusWindow = new MainWindow();
            _statusWindow.Closing += OnStatusClosing;
            _statusWindow.Closed += RequestExit;
            _statusWindow.ExitButton.Click += RequestExit;
        }
        _statusWindow.CloseButton.Visibility = view.UseTray && _tray is not null
            ? Visibility.Visible : Visibility.Collapsed;
        _statusWindow.StatusMessage.Text = string.Join("\n\n",
            new[] { view.Message, view.Details, view.Warning, _trayError }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (!_statusWindow.IsVisible)
            _statusWindow.Show();
    }

    private void OnStatusClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || _tray is null || _displayed?.UseTray != true)
            return;
        e.Cancel = true;
        _statusRequested = false;
        _dismissedWarning = _displayed.Warning;
        _statusWindow!.Hide();
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
