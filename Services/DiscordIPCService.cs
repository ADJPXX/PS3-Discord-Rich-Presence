using System.IO.Pipes;
using System.Text;
using System.Text.Json;

public static class DiscordIpcService
{
    private const string ClientId = "1528636206638694400";

    static NamedPipeClientStream? pipe;


    static void Main()
    {
        Console.WriteLine("Conectando ao Discord...");

        if (!Connect())
        {
            Console.WriteLine("Discord não encontrado.");
            return;
        }


        Console.WriteLine("Discord conectado!");


        SetActivity(
            "PS3",
            "inFAMOUS 2",
            "Collecting Blast Shards"
        );


        Console.WriteLine("Rich Presence enviado!");
        Console.WriteLine("Pressione ENTER para sair.");

        Console.ReadLine();
    }


    static bool Connect()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                pipe = new NamedPipeClientStream(
                    ".",
                    $"discord-ipc-{i}",
                    PipeDirection.InOut
                );

                pipe.Connect(2000);


                Send(0, new
                {
                    v = 1,
                    client_id = ClientId
                });


                string response = Receive();

                Console.WriteLine(response);


                return true;
            }
            catch
            {
                pipe?.Dispose();
                pipe = null;
            }
        }

        return false;
    }


    static void SetActivity(
        string name,
        string details,
        string state)
    {
        var payload = new
        {
            cmd = "SET_ACTIVITY",

            args = new
            {
                pid = Environment.ProcessId,

                activity = new
                {
                    name = name,
                    details = details,
                    state = state,

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


    static void Send(int opcode, object data)
    {
        if (pipe == null)
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


        pipe.Write(packet);
        pipe.Flush();
    }


    static string Receive()
    {
        if (pipe == null)
            return "";


        byte[] header = new byte[8];

        pipe.ReadExactly(header);


        int length = BitConverter.ToInt32(header, 4);


        byte[] data = new byte[length];

        pipe.ReadExactly(data);


        return Encoding.UTF8.GetString(data);
    }
}