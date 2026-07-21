using System.Net.Http;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public class WebManService
{
    private readonly Config _config;

    private readonly HttpClient _httpClient;

    private readonly HtmlParserService _parser;

    public WebManService(Config config)
    {
        _config = config;

        _httpClient = new HttpClient();

        _parser = new HtmlParserService();

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }


    public async Task<bool> IsPS3OnlineAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"http://{_config.Ip}");

            return response.IsSuccessStatusCode;
        }

        catch
        {
            return false;
        }
    }


    private async Task<string?> GetHtmlAsync()
    {
        try
        {
            return await _httpClient.GetStringAsync($"http://{_config.Ip}/cpursx.ps3?/sman.ps3");
        }

        catch
        {
            return null;
        }
    }


    public async Task<GameInfo?> GetGameInfoAsync()
    {
        var html = await GetHtmlAsync();

        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        return _parser.Parse(html);
    }
}