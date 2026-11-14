using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Runs many rooms in one process behind one HTTP port. The website (the gateway) creates and deletes rooms
/// through /rooms, and bridges each player's WebSocket to /room/{id}. Each room is one GameServer.
/// Everything that touches a room runs on one thread (<see cref="Run"/>), fed by a queue.
/// </summary>
public sealed class RoomHost
{
    private sealed class Room
    {
        public required int Id { get; init; }
        public required GameServer Game { get; init; }
        public required int MaxPlayers { get; init; }
        public HashSet<GamePeer> Peers { get; } = new();
    }

    private sealed record CreateRoomRequest(int Id, string Name, string? Password, string? AdminPassword, int MaxPlayers);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpListener listener = new();
    private readonly string secret;
    private readonly int maxRooms;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
    private readonly Dictionary<int, Room> rooms = new();
    private int nextPeerId;

    public RoomHost(int port, string secret, int maxRooms)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException("the gateway secret must be at least 32 characters", nameof(secret));
        }
        this.secret = secret;
        this.maxRooms = maxRooms;
        listener.Prefixes.Add($"http://*:{port}/");
    }

    public void Start()
    {
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        listener.Stop();
        Enqueue(() =>
        {
            foreach (Room room in rooms.Values.ToList())
            {
                CloseRoom(room, "server stopped");
            }
        });
    }

    /// <summary>Runs the rooms until cancelled. Call from the main thread.</summary>
    public void Run(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            while (queue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Room error: {ex}");
                }
            }
            foreach (Room room in rooms.Values.ToList())
            {
                room.Game.Poll();
            }
            Thread.Sleep(2);
        }
    }

    private void Enqueue(Action action) => queue.Enqueue(action);

    private Task<T> Post<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private async Task AcceptLoop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (!listener.IsListening)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url?.AbsolutePath ?? "/";
            if (path == "/health")
            {
                await ReplyAsync(context, 200, "{\"ok\":true}");
                return;
            }
            if (!Authorized(request))
            {
                await ReplyAsync(context, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            if (path == "/rooms" && request.HttpMethod == "POST")
            {
                await CreateRoomAsync(context);
                return;
            }
            var deleteMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/rooms/(\d+)$");
            if (deleteMatch.Success && request.HttpMethod == "DELETE")
            {
                await DeleteRoomAsync(context, int.Parse(deleteMatch.Groups[1].Value));
                return;
            }
            var joinMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/room/(\d+)$");
            if (joinMatch.Success && request.IsWebSocketRequest)
            {
                await JoinRoomAsync(context, int.Parse(joinMatch.Groups[1].Value));
                return;
            }
            await ReplyAsync(context, 404, "{\"error\":\"not found\"}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request error: {ex.Message}");
            try
            {
                await ReplyAsync(context, 500, "{\"error\":\"server error\"}");
            }
            catch (Exception)
            {
                // The client has gone; nothing more to do.
            }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        string? given = request.Headers["x-gateway-secret"];
        if (string.IsNullOrEmpty(given))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(secret));
    }

    private async Task CreateRoomAsync(HttpListenerContext context)
    {
        CreateRoomRequest? body;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = JsonSerializer.Deserialize<CreateRoomRequest>(await reader.ReadToEndAsync(), JsonOptions);
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body == null || body.Id <= 0 || body.MaxPlayers <= 0)
        {
            await ReplyAsync(context, 400, "{\"error\":\"bad request\"}");
            return;
        }

        (int status, string error) = await Post(() =>
        {
            if (rooms.ContainsKey(body.Id))
            {
                return (409, "room exists");
            }
            if (rooms.Count >= maxRooms)
            {
                return (503, "no free room");
            }
            var options = new ServerOptions
            {
                ServerName = body.Name,
                Password = body.Password ?? "",
                AdminPassword = body.AdminPassword ?? "",
                MaxPlayers = body.MaxPlayers,
                ResultsDirectory = "",
            };
            var game = new GameServer(options);
            game.Start();
            rooms[body.Id] = new Room { Id = body.Id, Game = game, MaxPlayers = body.MaxPlayers };
            return (201, "");
        });
        await ReplyAsync(context, status, status == 201 ? "{\"ok\":true}" : $"{{\"error\":\"{error}\"}}");
    }

    private async Task DeleteRoomAsync(HttpListenerContext context, int id)
    {
        bool removed = await Post(() =>
        {
            if (!rooms.TryGetValue(id, out Room? room))
            {
                return false;
            }
            CloseRoom(room, "room closed");
            return true;
        });
        await ReplyAsync(context, removed ? 204 : 404, removed ? "" : "{\"error\":\"not found\"}");
    }

    private void CloseRoom(Room room, string reason)
    {
        rooms.Remove(room.Id);
        foreach (GamePeer peer in room.Peers)
        {
            peer.Disconnect(reason);
        }
        room.Peers.Clear();
        room.Game.Stop();
    }

    private async Task JoinRoomAsync(HttpListenerContext context, int id)
    {
        string version = context.Request.Headers["x-sbm-version"] ?? "";
        if (version != ProtocolInfo.ConnectionKey)
        {
            await ReplyAsync(context, 426, $"{{\"error\":\"version\",\"version\":\"{ProtocolInfo.Version}\"}}");
            return;
        }

        (int status, Room? room) = await Post(() =>
        {
            if (!rooms.TryGetValue(id, out Room? found))
            {
                return (404, (Room?)null);
            }
            return found.Peers.Count >= found.MaxPlayers ? (503, (Room?)null) : (200, found);
        });
        if (room == null)
        {
            await ReplyAsync(context, status, status == 404 ? "{\"error\":\"not found\"}" : "{\"error\":\"room full\"}");
            return;
        }

        WebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var peer = new GamePeer(Interlocked.Increment(ref nextPeerId), wsContext.WebSocket);
        Enqueue(() =>
        {
            if (rooms.GetValueOrDefault(id) != room)
            {
                peer.Disconnect("room closed");
                return;
            }
            room.Peers.Add(peer);
            room.Game.OnPeerConnected(peer);
        });

        await peer.ReceiveAsync(
            (p, data) => Enqueue(() =>
            {
                if (room.Peers.Contains(p))
                {
                    room.Game.OnNetworkReceive(p, new NetDataReader(data));
                }
            }),
            (p, reason) => Enqueue(() =>
            {
                if (room.Peers.Remove(p))
                {
                    room.Game.OnPeerDisconnected(p, reason);
                }
            }));
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        if (json.Length > 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        context.Response.Close();
    }
}
