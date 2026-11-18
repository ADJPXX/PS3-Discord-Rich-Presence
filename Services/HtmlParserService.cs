using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public sealed class HtmlParserService
{
    internal const int MaxPageLength = 512 * 1024;
    private static readonly Regex Temperatures = new(
        @"CPU[\s:]*([0-9]{1,3})(?:\s*°?\s*([CF]))?.*?RSX[\s:]*([0-9]{1,3})(?:\s*°?\s*([CF]))?",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex VersionSuffix = new(@"\s+v?\d+(\.\d+)+[a-z]?$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsTitleId(string? value) => value is { Length: 9 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public GameInfo? Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaxPageLength)
            return null;

        var document = new HtmlDocument { OptionMaxNestedChildNodes = 128 };
        try
        {
            document.LoadHtml(html);
        }
        // HtmlAgilityPack uses a plain Exception for its nesting limit.
        catch (Exception ex) when (ex.GetType() == typeof(Exception) &&
                                   ex.Message.StartsWith("Document has more than ", StringComparison.Ordinal))
        {
            Trace.TraceWarning("Invalid webMAN HTML structure: {0}", ex.Message);
            return null;
        }

        var root = document.DocumentNode;
        var thermalNode = root.SelectSingleNode("//a[@href='/cpursx.ps3?up']");
        var memory = root.Descendants("a").FirstOrDefault(n =>
            n.GetAttributeValue("href", "") is "/games.ps3" or "/browser.ps3$slaunch" &&
            Text(n).StartsWith("MEM:", StringComparison.OrdinalIgnoreCase));
        var explicitXmb = memory != null && Text(memory).Contains("(XMB)", StringComparison.OrdinalIgnoreCase);
        var inGame = !explicitXmb && (memory != null || root.Descendants("a").Any(IsExitLink));
        var candidates = root.Descendants("a").Where(n =>
            n.GetAttributeValue("target", "").Equals("_blank", StringComparison.OrdinalIgnoreCase));
        var titleNode = candidates.FirstOrDefault(IsGameLink);
        if (titleNode == null && (thermalNode != null || inGame))
            titleNode = candidates.FirstOrDefault(n => IsRetailId(Text(n)));

        var multiMan = !explicitXmb && root.SelectSingleNode("//a[contains(@href, 'mmCM')]") != null;
        if (thermalNode == null && titleNode == null && !multiMan && !inGame && !explicitXmb)
            return null;

        var game = new GameInfo
        {
            IsOnMultiman = multiMan,
            IsOnXmb = explicitXmb || (!multiMan && !inGame && titleNode == null),
            Name = "PlayStation 3"
        };
        if (thermalNode != null)
        {
            var match = Temperatures.Match(Text(thermalNode));
            if (match.Success)
            {
                game.CpuTemperature = FormatTemperature("CPU", match.Groups[1], match.Groups[2]);
                game.RsxTemperature = FormatTemperature("RSX", match.Groups[3], match.Groups[4]);
            }
        }

        if (game.IsOnXmb || multiMan)
            return game;

        game.Name = "Unknown";
        if (titleNode == null)
            return game;

        game.TitleId = Text(titleNode).ToUpperInvariant();
        for (var sibling = titleNode.NextSibling; sibling != null; sibling = sibling.NextSibling)
        {
            if (sibling.NodeType == HtmlNodeType.Comment)
                continue;
            if (sibling.Name is "br" or "hr" || sibling == thermalNode || IsExitLink(sibling))
                break;
            var name = Text(sibling);
            if (name.Length == 0)
                continue;
            if (name.Any(char.IsLetterOrDigit))
                game.Name = VersionSuffix.Replace(name, "");
            break;
        }
        return game;
    }

    private static bool IsGameLink(HtmlNode node)
    {
        var id = Text(node);
        return IsTitleId(id) && node.GetAttributeValue("href", "")
            .EndsWith($"/{id}-ver.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExitLink(HtmlNode node)
    {
        var href = node.GetAttributeValue("href", "");
        var end = href.IndexOfAny(['?', '#']);
        return (end < 0 ? href : href[..end]).EndsWith("/xmb.ps3$exit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetailId(string id) => id.Length == 9 &&
        id[..4].All(char.IsAsciiLetter) && id[4..].All(char.IsAsciiDigit);

    private static string FormatTemperature(string label, Group value, Group unit) =>
        $"{label} {value.Value}°{(unit.Success ? unit.Value.ToUpperInvariant() : "C")}";

    private static string Text(HtmlNode node) => WebUtility.HtmlDecode(node.InnerText).Trim();
}
