using System.IO;
using Microsoft.Win32;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PS3DiscordRichPresence";
    private readonly Func<string?> _processPath;
    private readonly Func<string?> _entryAssemblyPath;
    private readonly Func<object?> _read;
    private readonly Action<string?> _write;

    public StartupService() : this(() => Environment.ProcessPath, () => Environment.GetCommandLineArgs().FirstOrDefault(),
        ReadCommand, WriteCommand) { }

    internal StartupService(Func<string?> processPath, Func<string?> entryAssemblyPath, Func<object?> read,
        Action<string?> write)
    {
        _processPath = processPath;
        _entryAssemblyPath = entryAssemblyPath;
        _read = read;
        _write = write;
    }

    public void ApplyStartupSetting(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var command = config.StartWithWindows ? GetCommand() : null;
        if (!Equals(_read(), command))
            _write(command);
    }

    private string GetCommand()
    {
        var executable = _processPath();
        if (string.IsNullOrWhiteSpace(executable))
            throw new IOException("The executable path is unknown.");

        if (!string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return $"\"{executable}\"";

        var assembly = _entryAssemblyPath();
        if (string.IsNullOrWhiteSpace(assembly))
            throw new IOException("The application path for the dotnet host is unknown.");
        return $"\"{executable}\" \"{Path.GetFullPath(assembly)}\"";
    }

    private static object? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(AppName);
    }

    private static void WriteCommand(string? command)
    {
        if (command is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        using var writable = Registry.CurrentUser.CreateSubKey(RegistryPath);
        writable.SetValue(AppName, command);
    }
}
