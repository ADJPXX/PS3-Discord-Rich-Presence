using System.Text.Json.Serialization;

namespace PS3DiscordRichPresence.Models;

public class Config
{
    public const long DefaultClientId = 1528636206638694400;

    [JsonPropertyName("Ip")]
    public string? Ps3Ip { get; set; } = "YOUR_PS3_IP_HERE";

    public long ClientId { get; set; } = DefaultClientId;
    public int UpdateIntervalSeconds { get; set; } = 3;
    public int ReconnectIntervalSeconds { get; set; } = 10;
    public bool ShowTemperature { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
}
