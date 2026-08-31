using System.IO;
using System.Text.Json;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public static class ConfigService
{
    private static readonly string JsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PS3config.json");

    public static Config ReadJson()
    {
        if (!File.Exists(JsonPath))
        {
            var configs = new Config
            {
                Ip = "YOUR_PS3_IP_HERE",
                ClientId = 1528636206638694400,
                UpdateIntervalSeconds = 15,
                ReconnectIntervalSeconds = 30,
                ShowTemperature = false,
                MinimizeToTray = true,
                StartWithWindows = false
            };

            var jsonWrite = JsonSerializer.Serialize(configs, new JsonSerializerOptions{WriteIndented = true});

            File.WriteAllText(JsonPath, jsonWrite);
        }

        WaitIpChange();

        var json = File.ReadAllText(JsonPath);

        var config = JsonSerializer.Deserialize<Config>(json);

        if (config == null)
        {
            throw new Exception("Erro ao carregar as configurações.");
        }

        config.UpdateIntervalSeconds = Math.Max(config.UpdateIntervalSeconds, 3);
        config.ReconnectIntervalSeconds = Math.Max(config.ReconnectIntervalSeconds, 10);

        return config;
    }


    private static void WaitIpChange()
    {
        while (true)
        {
            try
            {
                var json = File.ReadAllText(JsonPath);

                var config = JsonSerializer.Deserialize<Config>(json);

                if (config != null && config.Ip != "YOUR_PS3_IP_HERE" && System.Net.IPAddress.TryParse(config.Ip, out _))
                {
                    return;
                }
            }

            catch
            {
                //Ignores while user is editing/saving the JSON file.
            }

            Thread.Sleep(1000);
        }
    }
}