using System.Diagnostics;
using DiscordRPC;

namespace PS3DiscordRichPresence.Services;

public class DiscordService
{
    private readonly DiscordRpcClient _client;

    public DiscordService(long clientId)
    {
        _client = new DiscordRpcClient(clientId.ToString());
    }

    public async Task<bool> IsDiscordOnlineAsync()
    {
        return await Task.Run(() =>
        {
            var processes = Process.GetProcessesByName("Discord");

            return processes.Length > 0;
        });
    }

    public void Connect()
    {
        try
        {
            _client.Initialize();
        }
        catch
        {
            //ignored
        }
    }

    public void Disconnect()
    {
        _client.Dispose();
    }

    public void Clear()
    {
        _client.ClearPresence();
    }

    public void Update(string game, string? details, string? imageKey, DateTime? startTime)
    {


        _client.SetPresence(new RichPresence
        {
            Type = ActivityType.Playing,
            Details = game,
            State = details,

            Assets = new Assets
            {
                LargeImageKey = imageKey,
                LargeImageText = game
            },

            Timestamps = startTime == null ? null : new Timestamps(startTime.Value)
        });
    }
}