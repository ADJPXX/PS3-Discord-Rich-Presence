using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using PS3DiscordRichPresence.Models;

namespace PS3DiscordRichPresence.Services;

public class DiscordService
{
    private readonly long _clientId;

    private NamedPipeClientStream? _pipe;

    public DiscordService(long clientId)
    {
        _clientId = clientId;
    }


    public async Task<bool> IsDiscordOnlineAsync()
    {
        return await Task.Run(() =>
        {
            var processes = Process.GetProcessesByName("Discord");

            return processes.Length > 0;
        });
    }


    public bool ConnectPipe()
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                _pipe = new NamedPipeClientStream(
                    ".",
                    $"discord-ipc-{i}",
                    PipeDirection.InOut
                );

                _pipe.Connect(2000);


                Send(0, new
                {
                    v = 1,
                    client_id = _clientId
                });


                string response = Receive();

                Console.WriteLine(response);


                return true;
            }
            catch
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }

        return false;
    }


    public void Update(GameInfo game, string? state, string image, DateTime? startTime)
    {
        var activity = new Dictionary<string, object?>
        {
            ["name"] = game.Name,
            ["details"] = "PlayStation 3",

            ["assets"] = new
            {
                large_image = image,
                large_text = game.Name
            }
        };

        if (!string.IsNullOrEmpty(state))
        {
            activity["state"] = state;
        }

        if (startTime != null)
        {
            activity["timestamps"] = new
            {
                start = new DateTimeOffset(startTime.Value).ToUnixTimeMilliseconds()
            };
        }

        var payload = new
        {
            cmd = "SET_ACTIVITY",

            args = new
            {
                pid = Environment.ProcessId,
                activity
            },

            nonce = Guid.NewGuid().ToString()
        };

        Send(1, payload);

        Console.WriteLine("Activity enviada.");
    }


    private void Send(int opcode, object data)
    {
        if (_pipe == null)
            return;


        string json = JsonSerializer.Serialize(data);

        byte[] bytes = Encoding.UTF8.GetBytes(json);


        byte[] packet = new byte[8 + bytes.Length];


        Array.Copy(
            BitConverter.GetBytes(opcode),
            0,
            packet,
            0,
            4
        );


        Array.Copy(
            BitConverter.GetBytes(bytes.Length),
            0,
            packet,
            4,
            4
        );


        Array.Copy(
            bytes,
            0,
            packet,
            8,
            bytes.Length
        );


        _pipe.Write(packet);
        _pipe.Flush();
    }


    private string Receive()
    {
        if (_pipe == null)
            return "";


        byte[] header = new byte[8];

        _pipe.ReadExactly(header);


        int length = BitConverter.ToInt32(header, 4);


        byte[] data = new byte[length];

        _pipe.ReadExactly(data);


        return Encoding.UTF8.GetString(data);
    }


    public async Task<(string?, DateTime)> GetCurrentTime(DateTime oldTime, string oldGame, string? currentGame)
    {
        if (currentGame != oldGame)
        {
            var startTime = DateTime.UtcNow;

            return (currentGame, startTime);
        }

        return (currentGame, oldTime);
    }
}