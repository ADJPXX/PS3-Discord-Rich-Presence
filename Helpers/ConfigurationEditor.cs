using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence.Helpers;

internal static class ConfigurationEditor
{
    internal static string? TryOpen()
    {
        try
        {
            var path = ConfigService.JsonPath;
            if (!File.Exists(path))
                return "The configuration file is unavailable. Reload the configuration to create or read it.";
            var editor = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
            editor.ArgumentList.Add(path);
            using var process = Process.Start(editor);
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"Could not open the configuration. {error.Message}";
        }
    }
}
