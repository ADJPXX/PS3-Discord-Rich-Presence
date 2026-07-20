using Microsoft.Win32;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PS3DiscordRichPresence";

    public void TaskVerification(Config config)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);

        var currentPath = $"\"{Environment.ProcessPath}\"";
        var registryPath = key.GetValue(AppName)?.ToString();

        if (config.StartWithWindows)
        {
            if (registryPath != currentPath)
            {
                key.SetValue(AppName, currentPath);
            }
        }
        else
        {
            if (registryPath != null)
            {
                key.DeleteValue(AppName, false);
            }
        }
    }
}