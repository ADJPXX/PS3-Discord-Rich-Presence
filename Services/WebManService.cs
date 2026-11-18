using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public sealed record WebManReading(GameInfo? Game, bool Responded, string? Detail);

public sealed class WebManService : IDisposable
{
    private readonly Uri _root;
    private readonly Uri _status;
    private readonly HttpClient _httpClient;
    private readonly HtmlParserService _parser = new();

    public WebManService(Config config, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IPAddress.TryParse(config.Ps3Ip, out var address))
            throw new ArgumentException("Invalid PS3 IP address.", nameof(config));

        _root = new UriBuilder(Uri.UriSchemeHttp, address.ToString()).Uri;
        _status = new Uri(_root, "/cpursx.ps3");
        _httpClient = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(12),
            MaxResponseContentBufferSize = HtmlParserService.MaxPageLength
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }

    public async Task<bool> IsPS3OnlineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_root, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.TraceWarning("webMAN probe: {0}", ex.Message);
            return false;
        }
    }

    public async Task<GameInfo?> GetGameInfoAsync(CancellationToken cancellationToken = default) =>
        (await ReadStatusAsync(cancellationToken).ConfigureAwait(false)).Game;

    public async Task<WebManReading> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        // ResponseHeadersRead allows us to distinguish a reachable endpoint from an unreadable body.
        // The caller's budget must cover both headers and the bounded content read.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_httpClient.Timeout);
        var responded = false;
        try
        {
            using var response = await _httpClient.GetAsync(_status, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            responded = true;
            deadline.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
                return Unavailable(true, $"HTTP {(int)response.StatusCode} while requesting the status.");

            await response.Content.LoadIntoBufferAsync(HtmlParserService.MaxPageLength, deadline.Token)
                .ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            string html;
            try
            {
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                var encoding = GetEncoding(charset, bytes);
                html = encoding.GetString(bytes).TrimStart('\uFEFF');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                deadline.Token.ThrowIfCancellationRequested();
                return Unavailable(true, $"Invalid encoding in the status page. {ex.Message}");
            }

            var game = _parser.Parse(html);
            deadline.Token.ThrowIfCancellationRequested();
            return game is null
                ? Unavailable(true, "The response does not contain a recognized webMAN status page.")
                : new WebManReading(game, true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(responded, $"Timed out after {_httpClient.Timeout.TotalSeconds:g} s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable(responded, $"Communication failed. {ex.Message}");
        }
    }

    private WebManReading Unavailable(bool responded, string detail)
    {
        var message = $"{_status} — {detail}";
        Trace.TraceWarning(message);
        return new WebManReading(null, responded, message);
    }

    private static Encoding GetEncoding(string? charset, ReadOnlySpan<byte> bytes)
    {
        if (charset == null)
        {
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xfe, 0xff]))
                charset = "utf-32BE";
            else if (bytes.StartsWith(Encoding.UTF32.Preamble))
                charset = "utf-32";
            else if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
                charset = "utf-16BE";
            else if (bytes.StartsWith(Encoding.Unicode.Preamble))
                charset = "utf-16";
            else
                charset = "utf-8";
        }
        return CodePagesEncodingProvider.Instance.GetEncoding(charset, EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback) ?? Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    public void Dispose() => _httpClient.Dispose();
}
