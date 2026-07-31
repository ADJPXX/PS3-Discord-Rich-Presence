using System.Net.Http;

namespace PS3DiscordRichPresence.Services;

public class GameImageService
{
    private readonly HttpClient _httpClient = new();

    private static readonly string[] Regions =
    [
        "US", // United States
        "EN", // English (Europe)
        "PT", // Portugal
        "ES", // Spain
        "FR", // France
        "DE", // Germany
        "IT", // Italy
        "NL", // Netherlands
        "JA", // Japan
        "KO", // Korea
        "ZH", // China
        "RU", // Russia
        "AU", // Australia
        "DK", // Denmark
        "FI", // Finland
        "NO", // Norway
        "SE", // Sweden
        "PL", // Poland
        "CZ", // Czechia
          // Hungary
    ];

    public async Task<string> GetImageAsync(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return "xmb";
        }

        try
        {
            foreach (var region in Regions)
            {
                var url = $"https://art.gametdb.com/ps3/cover/{region}/{titleId}.jpg";

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    return url;
                }
            }
        }
        catch
        {
            //ignored
        }

        return titleId.ToLower();
    }
}