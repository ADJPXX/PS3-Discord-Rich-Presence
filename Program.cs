using PS3DiscordRichPresence.Services;

namespace PS3DiscordRichPresence;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var config = ConfigService.LerJson();

        var discord = new DiscordService(config.ClientId);

        var webMan = new WebManService(config);

        var imageService = new GameImageService();

        var oldTime = DateTime.UtcNow;

        string? oldGame = null;

        Console.WriteLine("Verificando PS3...");

        while (true)
        {
            if (!discord.ConnectPipe())
            {
                Console.WriteLine("discord esta OFFLINE.");
            }

            discord.Connect();

            var game = await webMan.GetGameInfoAsync();

            var image = await imageService.GetImageAsync(game.TitleId);

            var (currentGame, currentTime) = await webMan.GetCurrentTime(oldTime, oldGame!, game?.Name);

            discord.SetActivity(game.Name);

            discord.Update(game.Name!, $"", image, currentTime);
        }

        while (true)
        {
            if (!await webMan.IsPS3OnlineAsync())
            {
                Console.Clear();

                Console.WriteLine("PS3 OFFLINE.");
                await Task.Delay(TimeSpan.FromSeconds(config.ReconnectIntervalSeconds));

                continue;
            }

            var game = await webMan.GetGameInfoAsync();

            var image = await imageService.GetImageAsync(game.TitleId);

            if (!await discord.IsDiscordOnlineAsync())
            {
                Console.Clear();

                Console.WriteLine("Discord OFFLINE.");
                await Task.Delay(TimeSpan.FromSeconds(config.ReconnectIntervalSeconds));

                discord.Connect();

                continue;
            }

            var (currentGame, currentTime) = await webMan.GetCurrentTime(oldTime, oldGame!, game?.Name);

            discord.Update(game.Name!, $"", image, currentTime);

            if (game != null)
            {
                Console.Clear();

                Console.WriteLine(game.Name);
                Console.WriteLine(game.TitleId);
                Console.WriteLine(game.CpuTemperature);
                Console.WriteLine(game.RsxTemperature);
            }

            oldTime = currentTime;
            oldGame = currentGame;

            await Task.Delay(TimeSpan.FromSeconds(config.UpdateIntervalSeconds));
        }
    }
}