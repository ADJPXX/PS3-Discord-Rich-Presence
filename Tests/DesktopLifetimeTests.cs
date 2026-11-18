using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS3DiscordRichPresence.Helpers;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence.Tests;

public sealed class DesktopLifetimeTests
{
    [Fact]
    public async Task DashboardIsVisibleOnlyInWindowModeAndReloadWaitsForCleanup()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ui = new TaskCompletionSource<App>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var cleanups = 0;
        var overlap = 0;
        var icons = new List<Icon>();

        async Task RunMonitor(Action<MonitorView> publish, CancellationToken token)
        {
            var run = Interlocked.Increment(ref starts);
            if (Volatile.Read(ref cleanups) != run - 1)
                Interlocked.Exchange(ref overlap, 1);

            try
            {
                if (run == 2)
                {
                    var game = new GameInfo { Name = "Journey", TitleId = "BCUS98316" };
                    var sample = new PollResult(false, new WebManReading(game, true, null),
                        "CPU 55°C", "Discord is unavailable.");
                    publish(new MonitorView(false, sample.DisplayText, null, sample.Describe("192.0.2.1"))
                    {
                        Phase = MonitorPhase.Monitoring,
                        Sample = sample,
                        ConsoleAddress = "192.0.2.1",
                        CheckedAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    publish(new MonitorView(true, "Waiting for configuration", "Startup warning")
                        { Phase = MonitorPhase.Setup });
                }
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                if (run == 1)
                {
                    firstStopping.TrySetResult();
                    await allowFirstCleanup.Task.WaitAsync(guard.Token);
                }
                Interlocked.Increment(ref cleanups);
            }
        }

        var sta = new Thread(() =>
        {
            try
            {
                var app = new App(RunMonitor, () =>
                {
                    var icon = IconHelper.GetTrayIcon();
                    icons.Add(icon);
                    return icon;
                });
                ui.SetResult(app);
                app.Run();
                finished.SetResult();
            }
            catch (Exception error)
            {
                ui.TrySetException(error);
                finished.TrySetException(error);
            }
        }) { IsBackground = true };
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        var application = await ui.Task.WaitAsync(guard.Token);

        async Task WaitForPresentation(Func<bool> ready)
        {
            while (!await application.Dispatcher.InvokeAsync(ready, DispatcherPriority.Background, guard.Token))
                await Task.Delay(20, guard.Token);
        }

        try
        {
            await WaitForPresentation(() => application.Tray is not null);
            await application.Dispatcher.InvokeAsync(() =>
            {
                Assert.Empty(application.Windows.OfType<MainWindow>());
                Assert.NotEqual(IntPtr.Zero, Assert.Single(icons).Handle);
                var menu = application.Tray!.ContextMenu.Items.OfType<MenuItem>().ToArray();
                Assert.DoesNotContain(menu, item => Equals(item.Header, "View status"));
                var reload = Assert.Single(menu, item => Equals(item.Header, "Reload configuration"));
                reload.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                reload.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }, DispatcherPriority.Background, guard.Token);

            await firstStopping.Task.WaitAsync(guard.Token);
            Assert.Equal(1, Volatile.Read(ref starts));
            allowFirstCleanup.SetResult();
            await WaitForPresentation(() => application.MainWindow is MainWindow { IsVisible: true } window
                && window.ActivityTitle.Text == "Journey");
            await application.Dispatcher.InvokeAsync(() =>
            {
                Assert.Null(application.Tray);
                Assert.Throws<ObjectDisposedException>(() => icons[0].Handle);
                var window = Assert.IsType<MainWindow>(application.MainWindow);
                window.DiagnosticsTab.IsChecked = true;
                Assert.Equal(Visibility.Visible, window.DiagnosticsPage.Visibility);
                Assert.Equal(Visibility.Collapsed, window.OverviewPage.Visibility);
                Assert.Contains("Discord is unavailable.", window.StatusMessage.Text);
                window.OverviewTab.IsChecked = true;
                Assert.Equal("Online", window.ConsoleState.Text);
                Assert.Equal("Offline", window.DiscordState.Text);
                Assert.Equal("CPU 55°C", window.Temperature.Text);
                Assert.Contains("BCUS98316", window.ActivityDetail.Text);
                window.Width = 375;
                window.UpdateLayout();
                Assert.Equal(1, Grid.GetRow(window.DiscordCard));
                window.Width = 820;
                window.UpdateLayout();
                Assert.Equal(0, Grid.GetRow(window.DiscordCard));
                window.ReconnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.Background, guard.Token);

            await WaitForPresentation(() => application.Tray is not null && icons.Count == 2);
            await application.Dispatcher.InvokeAsync(() =>
            {
                Assert.DoesNotContain(application.Windows.OfType<MainWindow>(), window => window.IsVisible);
                var exit = Assert.Single(application.Tray!.ContextMenu.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "Exit"));
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }, DispatcherPriority.Background, guard.Token);
            await finished.Task.WaitAsync(guard.Token);
            Assert.Equal(3, Volatile.Read(ref starts));
            Assert.Equal(3, Volatile.Read(ref cleanups));
            Assert.Equal(0, Volatile.Read(ref overlap));
            Assert.Throws<ObjectDisposedException>(() => icons[1].Handle);
        }
        finally
        {
            allowFirstCleanup.TrySetResult();
            if (!application.Dispatcher.HasShutdownStarted && !finished.Task.IsCompleted)
                await application.Dispatcher.InvokeAsync(application.Shutdown);
        }
    }
}
