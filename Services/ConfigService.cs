using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public static class ConfigService
{
    internal static string JsonPath { get; } = Path.Combine(AppContext.BaseDirectory, "PS3config.json");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions TemplateOptions = new() { WriteIndented = true };
    // Task.Delay's maximum finite timeout is UInt32.MaxValue - 1 milliseconds.
    internal const int MaximumDelaySeconds = (int)((uint.MaxValue - 1L) / 1000);
    internal const int MaximumFileBytes = 64 * 1024;

    public static Task<Config> ReadJsonAsync(CancellationToken token, Action<string>? onWaiting = null) =>
        Task.Run(() => ReadJsonAsync(JsonPath, TimeSpan.FromSeconds(1), token, onWaiting), token);

    internal static Task<Config> ReadJsonAsync(CancellationToken token, Action<string, bool> onWaiting) =>
        Task.Run(() => ReadJsonAsync(JsonPath, TimeSpan.FromSeconds(1), token, onWaiting), token);

    internal static Task<Config> ReadJsonAsync(
        string path, TimeSpan pollInterval, CancellationToken token, Action<string>? onWaiting = null,
        Func<string, CancellationToken, Task<string>>? readText = null) =>
        ReadJsonAsync(path, pollInterval, token, (message, _) => onWaiting?.Invoke(message), readText);

    internal static async Task<Config> ReadJsonAsync(
        string path, TimeSpan pollInterval, CancellationToken token, Action<string, bool> onWaiting,
        Func<string, CancellationToken, Task<string>>? readText = null)
    {
        readText ??= ReadSnapshotAsync;
        (string Message, bool HideToTray)? lastNotice = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string message;
            bool hideToTray = true;
            try
            {
                try
                {
                    // On Windows this sharing mode excludes an in-place writer for the entire read.
                    var json = await readText(path, token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    hideToTray = ReadTrayPreference(document.RootElement);
                    var config = document.RootElement.Deserialize<Config>();
                    if (config is null || !IPAddress.TryParse(config.Ps3Ip, out var ip))
                        throw new InvalidDataException("Enter a valid IP address in Ip.");
                    if (config.ClientId <= 0)
                        throw new InvalidDataException("ClientId must be a positive integer.");

                    config.Ps3Ip = ip.ToString();
                    config.HideToTray = hideToTray;
                    config.UpdateIntervalSeconds = Math.Clamp(config.UpdateIntervalSeconds, 3, MaximumDelaySeconds);
                    config.ReconnectIntervalSeconds = Math.Clamp(config.ReconnectIntervalSeconds, 10, MaximumDelaySeconds);
                    token.ThrowIfCancellationRequested();
                    return config;
                }
                catch (FileNotFoundException)
                {
                    await InstallTemplateAsync(path, token).ConfigureAwait(false);
                    continue;
                }
            }
            catch (JsonException ex)
            {
                message = $"Invalid JSON at line {ex.LineNumber + 1}, property {ex.Path}.";
            }
            catch (DecoderFallbackException)
            {
                message = "Invalid text encoding. Save the file as UTF-8.";
            }
            catch (InvalidDataException ex)
            {
                message = ex.Message;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION are retryable while editing.
                message = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                onWaiting($"Unable to read configuration at {path}. {ex.Message}", true);
                throw;
            }

            if (lastNotice != (message, hideToTray))
            {
                onWaiting($"Waiting for configuration at {path}. {message}", hideToTray);
                lastNotice = (message, hideToTray);
            }
            await Task.Delay(pollInterval, token).ConfigureAwait(false);
        }
    }

    private static bool ReadTrayPreference(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return true;

        // The canonical key wins regardless of where the old key appears in the file.
        JsonElement? current = null;
        JsonElement? legacy = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("hidetotray", StringComparison.OrdinalIgnoreCase))
                current = property.Value;
            else if (property.Name.Equals("MinimizeToTray", StringComparison.OrdinalIgnoreCase))
                legacy = property.Value;
        }
        return (current ?? legacy)?.ValueKind switch
        {
            null or JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Set hidetotray to true or false.")
        };
    }

    private static async Task<string> ReadSnapshotAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var length = file.Length;
        if (length > MaximumFileBytes)
            throw new InvalidDataException("Configuration exceeds the 64 KiB limit.");
        var bytes = new byte[(int)length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (file.ReadByte() != -1)
            throw new InvalidDataException("The file changed while it was being read.");
        using var reader = new StreamReader(new MemoryStream(bytes), StrictUtf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static async Task InstallTemplateAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        var published = false;
        try
        {
            await using (staging.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(staging, new Config(), TemplateOptions, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(stagingPath, path, overwrite: false);
                published = true;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183)
            {
                // Another instance or the editor published its copy first. The caller will read it.
            }
        }
        finally
        {
            if (!published)
                File.Delete(stagingPath);
        }
    }
}
