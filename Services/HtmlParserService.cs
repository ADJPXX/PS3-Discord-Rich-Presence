using System.Text.RegularExpressions;
using HtmlAgilityPack; //dotnet add package HtmlAgilityPack

using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public class HtmlParserService
{
    public GameInfo Parse(string html)
    {
        var document = new HtmlDocument();

        document.LoadHtml(html);

        var game = new GameInfo();

        ParseTemperatures(document, game);

        ParseGameType(document, game);

        ParseGame(document, game);

        return game;
    }


    private void ParseTemperatures(HtmlDocument? document, GameInfo game)
    {
        var thermalData = document?.DocumentNode.SelectSingleNode("//a[@href='/cpursx.ps3?up']");

        if (thermalData == null)
        {
            return;
        }

        var match = Regex.Match(thermalData.InnerText, @"CPU[: ]*(\d+).*?RSX[: ]*(\d+)", RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return;
        }

        game.CpuTemperature = $"CPU {match.Groups[1].Value}°C";

        game.RsxTemperature = $"RSX {match.Groups[2].Value}°C";
    }


    private void ParseGame(HtmlDocument? document, GameInfo game)
    {
        if (game.IsOnXmb)
        {
            game.Name = "PlayStation 3";
            game.TitleId = null;
            return;
        }

        var titleNode = document?.DocumentNode.SelectSingleNode("//a[@target='_blank']");

        if (titleNode == null)
        {
            game.Name = "Desconhecido";
            game.TitleId = null;
            return;
        }

        game.TitleId = titleNode.InnerText.Trim();

        var node = titleNode.NextSibling;

        while (node != null && string.IsNullOrWhiteSpace(node.InnerText))
        {
            node = node.NextSibling;
        }

        game.Name = node?.InnerText.Trim() ?? "Desconhecido";
    }


    private void ParseGameType(HtmlDocument? document, GameInfo game)
    {
        if (document?.DocumentNode.SelectSingleNode("//a[@target='_blank']") != null)
        {
            game.IsRetro = false;
            game.IsOnXmb = false;

            return;
        }

        var retro = document?.DocumentNode.SelectSingleNode("//a[contains(@href,'PSXISO') or contains(@href,'PS2ISO')]");

        if (retro != null)
        {
            game.IsRetro = true;
            game.IsOnXmb = false;
        }

        game.IsRetro = false;
        game.IsOnXmb = true;
    }


    private void ParseImage(HtmlDocument document, GameInfo game)
    {

    }
}