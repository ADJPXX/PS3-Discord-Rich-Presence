namespace PS3DiscordRichPresence.Models;

public class Config
{
    public string? Ip { get; set; }

    public long ClientId { get; set; }

    public int UpdateIntervalSeconds { get; set; }

    public int ReconnectIntervalSeconds { get; set; }

    public bool ShowTemperature { get; set; }

    public bool ShowElapsedTime { get; set; }

    public bool ShowRetroCovers { get; set; }

    public bool MinimizeToTray { get; set; }

    public bool StartWithWindows { get; set; }
}