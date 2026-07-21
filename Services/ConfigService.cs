using System.IO;
using System.Text.Json;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public static class ConfigService
{
    public static Config LerJson()
    {
        var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PS3config.json");

        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException("Arquivo PS3Config.json não encontrado.");
        }

        var json = File.ReadAllText(jsonPath);

        var config = JsonSerializer.Deserialize<Config>(json);

        if (config == null)
        {
            throw new Exception("Erro ao carregar as configurações.");
        }

        return config;
    }
}