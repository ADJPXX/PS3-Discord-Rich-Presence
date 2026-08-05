using System.IO;
using System.Text.Json;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public static class ConfigService
{
    public static Config ReadJson()
    {
        var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PS3config.json");

        if (!File.Exists(jsonPath))
        {
            var configs = new Config
            {
                Ip = "YOUR_PS3_IP_HERE",
                ClientId = 1528636206638694400,
                UpdateIntervalSeconds = 15,
                ReconnectIntervalSeconds = 30,
                ShowTemperature = false,
                ShowRetroCovers = true,
                MinimizeToTray = true,
                StartWithWindows = false
            };

            var jsonWrite = JsonSerializer.Serialize(configs, new JsonSerializerOptions{WriteIndented = true});

            File.WriteAllText(jsonPath, jsonWrite);
        }

        var json = File.ReadAllText(jsonPath);

        var config = JsonSerializer.Deserialize<Config>(json);

        if (config == null)
        {
            throw new Exception("Erro ao carregar as configurações.");
        }

        config.UpdateIntervalSeconds = Math.Max(config.UpdateIntervalSeconds, 3);
        config.ReconnectIntervalSeconds = Math.Max(config.ReconnectIntervalSeconds, 10);

        return config;
    }
}