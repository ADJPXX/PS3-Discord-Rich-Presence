using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace PS3DiscordRichPresence.Services;

public sealed class GameImageService : IDisposable
{
    private sealed record CachedImage(string TitleId, string Image, DateTimeOffset ExpiresAt);

    private static readonly string[] Regions =
        ["US", "EN", "PT", "ES", "FR", "DE", "IT", "NL", "JA", "KO", "ZH", "RU", "AU", "DK", "FI", "NO", "SE", "PL", "CZ", "HU"];

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _budget;
    private readonly TimeProvider _clock;
    private readonly string? _fallbackAsset;
    private CachedImage? _lastImage;

    public GameImageService(HttpMessageHandler? handler = null, TimeSpan? searchBudget = null,
        TimeProvider? clock = null, string? fallbackAsset = null)
    {
        _budget = searchBudget ?? TimeSpan.FromSeconds(3);
        if (_budget <= TimeSpan.Zero || _budget.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(searchBudget));
        _clock = clock ?? TimeProvider.System;
        _fallbackAsset = fallbackAsset;
        _httpClient = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> GetImageAsync(string? titleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HtmlParserService.IsTitleId(titleId))
            return "xmb";

        var id = titleId!.ToUpperInvariant();
        var cached = Volatile.Read(ref _lastImage);
        if (cached?.TitleId == id && cached.ExpiresAt > _clock.GetUtcNow())
            return cached.Image;

        var image = _fallbackAsset ?? id.ToLowerInvariant();
        var lifetime = TimeSpan.FromMinutes(1);
        using var deadline = new CancellationTokenSource(_budget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            foreach (var region in Regions)
            {
                var url = $"https://art.gametdb.com/ps3/cover/{region}/{id}.jpg";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    continue;

                if (!response.IsSuccessStatusCode)
                {
                    Trace.TraceWarning("Capa GameTDB: HTTP {0}.", (int)response.StatusCode);
                    break;
                }
                if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                    continue;

                var type = response.Content.Headers.ContentType?.MediaType;
                if (type != null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    continue;

                image = url;
                lifetime = TimeSpan.FromHours(1);
                break;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("Timed out while searching GameTDB for the cover.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Trace.TraceWarning("GameTDB request failed: {0}", ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _lastImage, new CachedImage(id, image, _clock.GetUtcNow() + lifetime));
        return image;
    }

    public void Dispose() => _httpClient.Dispose();
}
