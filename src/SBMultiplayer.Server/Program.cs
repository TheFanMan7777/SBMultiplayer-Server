using System.Globalization;
using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

const string Help = """
    SBMultiplayer.Server - dedicated server

      --config <file>       load settings from a JSON file (see --init-config)
      --init-config <file>  write a commented configuration template and exit

    Command-line options override the config file:
      --port <n>            UDP port (default 24800)
      --max-players <n>     max players (default 32)
      --password <text>     password required to join
      --admin-password <t>  password for the in-game admin panel (F8)
      --maps <list>         rotation, e.g. o:1,o:2,o:57 (official level IDs; empty = free mode)
      --mode <mode>         TimeAttack (default) or Rounds (synchronized start, one attempt per round)
      --rounds-per-map <n>  Rounds mode: rounds played on each map (default 3)
      --round <s>           round duration in seconds (default 300)
      --results <s>         results screen duration (default 15)
      --echo                sends each player their own delayed board back (solo testing)
      --echo-delay <s>      echo delay in seconds (default 2)

    Web rooms (for the website; no UDP port per room):
      --rooms <port>        serve web rooms over HTTP on this port (needs SBM_GATEWAY_SECRET, 32+ characters)
      --max-rooms <n>       most rooms open at once (default 20)

    Console commands:
      list, round, standings, next, restart, pause, extend <s>, map <index>, kick <id>, resetmatch, quit
    """;

// ---------- Arguments ----------
var overrides = new List<Action<ServerOptions>>();
string? configPath = null;
int? roomsPort = null;
int maxRooms = 20;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {arg}");
    double Seconds() => double.Parse(Next(), CultureInfo.InvariantCulture);
    try
    {
        switch (arg)
        {
            case "--config": configPath = Next(); break;
            case "--init-config":
            {
                string path = Next();
                if (File.Exists(path))
                {
                    Console.Error.WriteLine($"{path} already exists, not overwriting it.");
                    return 1;
                }
                File.WriteAllText(path, ServerOptions.Template);
                Console.WriteLine($"Configuration template written to {path}. Edit it, then start with --config {path}");
                return 0;
            }
            case "--port": { int v = int.Parse(Next()); overrides.Add(o => o.Port = v); break; }
            case "--max-players": { int v = int.Parse(Next()); overrides.Add(o => o.MaxPlayers = v); break; }
            case "--password": { string v = Next(); overrides.Add(o => o.Password = v); break; }
            case "--admin-password": { string v = Next(); overrides.Add(o => o.AdminPassword = v); break; }
            case "--maps":
            {
                var v = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                overrides.Add(o => o.Maps = v);
                break;
            }
            case "--round": { double v = Seconds(); overrides.Add(o => o.RoundSeconds = v); break; }
            case "--mode":
            {
                if (!Enum.TryParse(Next(), ignoreCase: true, out GameMode v))
                {
                    throw new ArgumentException("expected TimeAttack or Rounds");
                }
                overrides.Add(o => o.Mode = v);
                break;
            }
            case "--rounds-per-map": { int v = int.Parse(Next()); overrides.Add(o => o.RoundsPerMap = v); break; }
            case "--results": { double v = Seconds(); overrides.Add(o => o.ResultsSeconds = v); break; }
            case "--echo": overrides.Add(o => o.Echo = true); break;
            case "--echo-delay": { double v = Seconds(); overrides.Add(o => o.EchoDelaySeconds = v); break; }
            case "--rooms": roomsPort = int.Parse(Next()); break;
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

ServerOptions options;
try
{
    options = configPath != null ? ServerOptions.Load(configPath) : new ServerOptions();
}
catch (Exception e)
{
    Console.Error.WriteLine($"Could not read {configPath}: {e.Message}");
    return 1;
}
foreach (var apply in overrides)
{
    apply(options);
}

if (roomsPort is int webPort)
{
    return RunRooms(webPort, maxRooms);
}

var server = new GameServer(options, configPath);
server.Start();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Console commands on a separate thread; the server logic stays on the main thread.
var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
var inputThread = new Thread(() =>
{
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        commands.Enqueue(line.Trim());
    }
})
{ IsBackground = true };
inputThread.Start();

while (!cts.IsCancellationRequested)
{
    server.Poll();
    while (commands.TryDequeue(out string? line))
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }
        int Arg() => parts.Length > 1 && int.TryParse(parts[1], out int v) ? v : -1;
        string? error = null;
        switch (parts[0].ToLowerInvariant())
        {
            case "list":
                Console.WriteLine($"{server.PlayerCount} player(s) connected");
                foreach (string l in server.DescribePlayers())
                {
                    Console.WriteLine("  " + l);
                }
                break;
            case "round":
                Console.WriteLine(server.DescribeRound());
                break;
            case "standings":
                foreach (string l in server.DescribeStandings())
                {
                    Console.WriteLine("  " + l);
                }
                break;
            case "next": error = server.RunConsoleCommand(AdminCommand.NextPhase, 0); break;
            case "restart": error = server.RunConsoleCommand(AdminCommand.RestartRound, 0); break;
            case "pause": error = server.RunConsoleCommand(AdminCommand.TogglePause, 0); break;
            case "extend": error = server.RunConsoleCommand(AdminCommand.Extend, Arg()); break;
            case "map": error = server.RunConsoleCommand(AdminCommand.GotoMap, Arg()); break;
            case "kick": error = server.RunConsoleCommand(AdminCommand.Kick, Arg()); break;
            case "resetmatch": error = server.RunConsoleCommand(AdminCommand.ResetMatch, 0); break;
            case "quit":
            case "exit":
                cts.Cancel();
                break;
            default:
                Console.WriteLine("Commands: list, round, standings, next, restart, pause, extend <s>, map <index>, kick <id>, resetmatch, quit");
                break;
        }
        if (error != null)
        {
            Console.WriteLine(error);
        }
    }
    Thread.Sleep(2);
}

server.Stop();
Console.WriteLine("Server stopped.");
return 0;

// Serves many rooms behind one HTTP port for the website. The website creates and deletes rooms and bridges players' WebSockets.
static int RunRooms(int port, int maxRooms)
{
    string? secret = Environment.GetEnvironmentVariable("SBM_GATEWAY_SECRET");
    if (secret == null || secret.Length < 32)
    {
        Console.Error.WriteLine("Set SBM_GATEWAY_SECRET to a secret of at least 32 characters. The website must use the same value.");
        return 1;
    }

    var rooms = new RoomHost(port, secret, maxRooms);
    rooms.Start();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    Console.WriteLine($"Web room host on HTTP port {port}, {maxRooms} rooms max. Press Ctrl+C to stop.");
    rooms.Run(cts.Token);
    rooms.Stop();
    Console.WriteLine("Server stopped.");
    return 0;
}
