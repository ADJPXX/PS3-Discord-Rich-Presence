namespace PS3DiscordRichPresence.Models;

public class GameInfo
{
    public string? Name { get; set; }

    public string? TitleId { get; set; }

    public string? CpuTemperature { get; set; }

    public string? RsxTemperature { get; set; }

    public string? Image { get; set; }

    public bool IsRetro { get; set; }

    public bool IsOnXmb { get; set; }

    public bool IsOnline { get; set; }
}