using System.Windows;

namespace PS3DiscordRichPresence;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
