using System.IO;
using PS3DiscordRichPresence.Models;
using PS3DiscordRichPresence.Services;
using Xunit;

namespace PS3DiscordRichPresence.Tests;

public sealed class StartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingSettingNeedsNoWriteAccess(bool enabled)
    {
        const string path = @"C:\Program Files\PS3\Activity.exe";
        var command = enabled ? $"\"{path}\"" : null;
        var service = new StartupService(() => path, () => null, () => command,
            _ => throw new UnauthorizedAccessException());
        service.ApplyStartupSetting(new Config { StartWithWindows = enabled });
    }

    [Fact]
    public void DotnetHostRegistersTheEntryAssemblyAndCanBeRemoved()
    {
        const string host = @"C:\Program Files\dotnet\dotnet.exe";
        const string app = @"C:\PS3 Activity\PS3DiscordRichPresence.dll";
        string? stored = null;
        var service = new StartupService(() => host, () => app, () => stored, value => stored = value);
        service.ApplyStartupSetting(new Config { StartWithWindows = true });
        Assert.Equal($"\"{host}\" \"{app}\"", stored);
        service.ApplyStartupSetting(new Config { StartWithWindows = false });
        Assert.Null(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void MissingExecutableOrManagedEntryCannotOverwriteSetting(string? host)
    {
        var writes = 0;
        var service = new StartupService(() => host, () => null, () => "existing", _ => writes++);
        Assert.Throws<IOException>(() => service.ApplyStartupSetting(new Config { StartWithWindows = true }));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void FailedRegistryWriteRemainsObservable()
    {
        var service = new StartupService(() => @"C:\PS3\Activity.exe", () => null, () => null,
            _ => throw new UnauthorizedAccessException("Registro bloqueado"));
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            service.ApplyStartupSetting(new Config { StartWithWindows = true }));
        Assert.Contains("Registro bloqueado", ex.Message);
    }
}
