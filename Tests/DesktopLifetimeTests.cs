using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS3DiscordRichPresence.Helpers;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence.Tests;

public sealed class DesktopLifetimeTests
{
    [Fact]
    public async Task WpfSwitchesFromSetupToTrayAndClosesOnlyAfterWorkerAndIconCleanup()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ui = new TaskCompletionSource<App>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trayMode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadedIcon = new TaskCompletionSource<Icon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerClosed = false;
        var sta = new Thread(() =>
        {
            try
            {
                var app = new App(async (publish, token) =>
                {
                    try
                    {
                        publish(new MonitorView(false, "Waiting for configuration"));
                        await trayMode.Task.WaitAsync(token);
                        publish(new MonitorView(true, "Connected") { Details = "webMAN at 192.0.2.1" });
                        await refreshStatus.Task.WaitAsync(token);
                        publish(new MonitorView(true, "Waiting for Discord") { Details = "Discord did not confirm the activity." });
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    finally
                    {
                        Volatile.Write(ref workerClosed, true);
                    }
                }, () =>
                {
                    var icon = IconHelper.GetTrayIcon();
                    loadedIcon.SetResult(icon);
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
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.IsBackground = true;
        sta.Start();
        var application = await ui.Task.WaitAsync(guard.Token);
        try
        {
            while (!await application.Dispatcher.InvokeAsync(
                () => application.MainWindow is MainWindow { IsVisible: true } window
                      && window.StatusMessage.Text == "Waiting for configuration",
                DispatcherPriority.Background, guard.Token))
                await Task.Delay(20, guard.Token);

            trayMode.SetResult();
            var icon = await loadedIcon.Task.WaitAsync(guard.Token);
            // This dispatch runs after the tick which switched the presentation.
            await application.Dispatcher.InvokeAsync(() =>
            {
                var window = Assert.IsType<MainWindow>(application.MainWindow);
                Assert.False(window.IsVisible);
                Assert.NotEqual(IntPtr.Zero, icon.Handle);
                application.ShowStatus();
                Assert.True(window.IsVisible);
                Assert.Contains("webMAN at 192.0.2.1", window.StatusMessage.Text);
                Assert.Equal(Visibility.Visible, window.CloseButton.Visibility);
                refreshStatus.SetResult();
            }, DispatcherPriority.Background, guard.Token);
            while (!await application.Dispatcher.InvokeAsync(
                () => application.MainWindow is MainWindow { IsVisible: true } window
                      && window.StatusMessage.Text.Contains("Discord did not confirm the activity."),
                DispatcherPriority.Background, guard.Token))
                await Task.Delay(20, guard.Token);

            await application.Dispatcher.InvokeAsync(() =>
            {
                var window = Assert.IsType<MainWindow>(application.MainWindow);
                window.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.IsVisible);
                Assert.False(Volatile.Read(ref workerClosed));
                application.ShowStatus();
                Assert.True(window.IsVisible);
                window.ExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.Background, guard.Token);
            await finished.Task.WaitAsync(guard.Token);
            Assert.True(Volatile.Read(ref workerClosed));
            Assert.Throws<ObjectDisposedException>(() => icon.Handle);
        }
        finally
        {
            if (!application.Dispatcher.HasShutdownStarted && !finished.Task.IsCompleted)
                await application.Dispatcher.InvokeAsync(application.Shutdown);
        }
    }
}
