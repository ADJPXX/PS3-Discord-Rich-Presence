using System.Net.Http;

namespace PS3DiscordRichPresence.Services;

public class GameImageService
{
    private readonly HttpClient _httpClient = new();

<<<<<<< HEAD
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

=======
>>>>>>> parent of cf9d789 (feat: trim game version from title and search covers across multiple GameTDB regions)
    public async Task<string> GetImageAsync(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return "xmb";
        }

        var regions = new Dictionary<char, string>
        {
            ['A'] = "ZH",
            ['E'] = "EN",
            ['H'] = "US",
            ['J'] = "JA",
            ['K'] = "KO",
            ['U'] = "US"
        };

        if (!regions.TryGetValue(titleId[2], out var region))
        {
            return titleId.ToLower();
        }

        var url = $"https://art.gametdb.com/ps3/cover/{region}/{titleId}.jpg";

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                return url;
            }
        }
        catch
        {
            //ignored
        }

        return titleId.ToLower();
    }
}