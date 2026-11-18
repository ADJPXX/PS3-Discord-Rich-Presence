using System.Windows;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PS3DiscordRichPresence.Services;
using PS3DiscordRichPresence.Helpers;

namespace PS3DiscordRichPresence;

public partial class MainWindow : Window
{
    private string? _monitorWarning;
    private string? _actionNotice;
    private bool _compact;

    public MainWindow()
    {
        InitializeComponent();
        using var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("PS3DiscordRichPresence.Resources.ps.ico");
        if (icon is not null)
        {
            var frames = new IconBitmapDecoder(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            BrandMark.Source = frames.OrderBy(frame => Math.Abs(frame.PixelWidth - 48)).First();
        }
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SizeChanged += (_, _) => ArrangeDashboard(ActualWidth);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal void Render(MonitorView view)
    {
        var sample = view.Sample;
        var game = sample?.Reading.Game;
        var waiting = view.Phase switch
        {
            MonitorPhase.Setup => "Setup required",
            MonitorPhase.Stopped => "Monitor stopped",
            MonitorPhase.Monitoring => "Waiting for activity",
            _ => "Connecting"
        };
        ActivityTitle.Text = game switch
        {
            { IsOnXmb: true } => "XMB",
            { IsOnMultiman: true } => "multiMAN",
            { Name: { Length: > 0 } name } => name,
            _ => waiting
        };
        ActivityDetail.Text = game switch
        {
            { IsOnXmb: true } => "PlayStation 3 home menu",
            { IsOnMultiman: true } => "PlayStation 3 game manager",
            { TitleId: { Length: > 0 } id } => $"Game • {id}",
            not null => "PlayStation 3 game",
            _ when view.Phase == MonitorPhase.Setup => "Choose Edit configuration to set your PS3 IP address.",
            _ when view.Phase == MonitorPhase.Stopped => "Open Diagnostics for the error, then reconnect.",
            _ when sample?.Reading.Responded == true => "The console responded, but webMAN could not identify its activity.",
            _ when sample is not null => "Waiting for a response from the console.",
            _ => "Checking webMAN and Discord Desktop."
        };
        Temperature.Text = sample?.Temperatures ?? string.Empty;
        Temperature.Visibility = string.IsNullOrWhiteSpace(Temperature.Text) ? Visibility.Collapsed : Visibility.Visible;
        ConsoleEndpoint.Text = view.ConsoleAddress ?? "No console address loaded";
        var idle = view.Phase == MonitorPhase.Connecting ? "Checking" : "Waiting";
        SetConnection(ConsoleState, sample is null ? idle : sample.Ps3Online ? "Online" :
            sample.Reading.Responded ? "Status unavailable" : "No response",
            sample is null ? "SoftText" : sample.Ps3Online ? "Mint" : "Amber");
        SetConnection(DiscordState, sample is null ? idle : sample.DiscordOnline ? "Online" : "Offline",
            sample is null ? "SoftText" : sample.DiscordOnline ? "Mint" : "Coral");
        DiscordEndpoint.Text = sample switch
        {
            { DiscordOnline: true, Ps3Online: true } => "Activity confirmed",
            { DiscordOnline: true } => "Connected via local IPC",
            { DiscordOnline: false } => "See Diagnostics for details",
            _ => "Local IPC connection"
        };
        LastCheck.Text = view.CheckedAt is { } checkedAt
            ? $"Checked {checkedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)}" : string.Empty;
        StatusMessage.Text = string.Join("\n\n", new[] { view.Message, view.Details, view.Warning }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        _monitorWarning = view.Warning;
        UpdateNotice();
    }

    private void SetConnection(TextBlock label, string text, string brush)
    {
        label.Text = text;
        label.Foreground = (Brush)FindResource(brush);
    }

    private void ArrangeDashboard(double width)
    {
        bool compact = width < 610;
        if (compact == _compact)
            return;
        _compact = compact;
        Page.Margin = compact ? new Thickness(16,16,16,14) : new Thickness(28,22,28,18);
        BrandText.Visibility = Illustration.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ArtColumn.Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        DiscordColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ConnectionGap.Width = new GridLength(compact ? 0 : 12);
        Grid.SetColumn(DiscordCard, compact ? 0 : 2);
        Grid.SetRow(DiscordCard, compact ? 1 : 0);
        DiscordCard.Margin = compact ? new Thickness(0,8,0,0) : new Thickness(0);
        Grid.SetColumnSpan(ActionBar, compact ? 2 : 1);
        Grid.SetRow(WindowActions, compact ? 1 : 0);
        Grid.SetColumn(WindowActions, compact ? 0 : 1);
        Grid.SetColumnSpan(WindowActions, compact ? 2 : 1);
        WindowActions.Margin = compact ? new Thickness(0,4,6,0) : new Thickness(0);
        PageTitle.FontSize = compact ? 22 : 28;
        ActivityTitle.FontSize = compact ? 25 : 30;
    }

    private void SelectPage(object sender, RoutedEventArgs e)
    {
        // The first Checked event runs before the page content has been created.
        if (OverviewPage is null || DiagnosticsPage is null)
            return;
        bool showDiagnostics = DiagnosticsTab.IsChecked == true;
        OverviewPage.Visibility = showDiagnostics ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsPage.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        _actionNotice = null;
        UpdateNotice();
    }

    private void CopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(StatusMessage.Text);
            _actionNotice = "Report copied to clipboard.";
        }
        catch (ExternalException)
        {
            _actionNotice = "The clipboard is busy. Try copying again.";
        }
        UpdateNotice();
    }

    private void OpenConfig(object sender, RoutedEventArgs e)
    {
        _actionNotice = ConfigurationEditor.TryOpen() ?? "Save your edits, then choose Reconnect to apply them.";
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        Notice.Text = string.Join("\n", new[] { _monitorWarning, _actionNotice }.Where(text => !string.IsNullOrWhiteSpace(text)));
        Notice.Visibility = Notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

}
