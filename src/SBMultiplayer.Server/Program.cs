using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

const string Help = """
    SBMultiplayer.Server - room host

      --listen <port>     HTTP and WebSocket port, for the website's gateway (default 9000)
      --max-rooms <n>     most rooms running at once (default 10)

    The gateway secret is read from the SBM_GATEWAY_SECRET environment variable (32+ characters).
    Rooms are created by the website. This process only runs them.
    """;

int port = 9000;
int maxRooms = 10;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {arg}");
    try
    {
        switch (arg)
        {
            case "--listen": port = int.Parse(Next()); break;
            case "--max-rooms": maxRooms = int.Parse(Next()); break;
            case "-h":
            case "--help":
                Console.WriteLine(Help);
                return 0;
            default:
                Console.Error.WriteLine($"Unknown argument: {arg} (see --help)");
                return 1;
        }
    }
    catch (Exception e) when (e is FormatException or ArgumentException)
    {
        Console.Error.WriteLine($"Invalid value for {arg}: {e.Message}");
        return 1;
    }
}

string? secret = Environment.GetEnvironmentVariable("SBM_GATEWAY_SECRET");
if (string.IsNullOrEmpty(secret) || secret.Length < 32)
{
    Console.Error.WriteLine("Set SBM_GATEWAY_SECRET to the gateway secret (32+ characters).");
    return 1;
}

var host = new RoomHost(port, secret, maxRooms);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

host.Start();
Console.WriteLine($"SBMultiplayer room host on port {port} (protocol v{ProtocolInfo.Version}, up to {maxRooms} rooms)");
host.Run(cts.Token);
host.Stop();
Console.WriteLine("Room host stopped.");
return 0;
