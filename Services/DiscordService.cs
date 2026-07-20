using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using DiscordRPC;

namespace PS3DiscordRichPresence.Services;

public class DiscordService
{
    private readonly DiscordRpcClient _client;

    private static NamedPipeClientStream? _pipe;

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


    public bool ConnectPipe()
    {
        for (int i = 0; i < 10; i++)
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
                    client_id = 1528636206638694400
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


    public void SetActivity(string game, string? details="", string state="")
    {
        var payload = new
        {
            cmd = "SET_ACTIVITY",

            args = new
            {
                pid = Environment.ProcessId,

                activity = new
                {
                    name = game,
                    //details = game,
                    //state = details,

                    timestamps = new
                    {
                        start = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                }
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
}