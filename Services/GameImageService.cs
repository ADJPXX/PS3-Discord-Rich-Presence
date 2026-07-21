using System.Net.Http;

namespace PS3DiscordRichPresence.Services;

public class GameImageService
{
    private readonly HttpClient _httpClient = new();

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