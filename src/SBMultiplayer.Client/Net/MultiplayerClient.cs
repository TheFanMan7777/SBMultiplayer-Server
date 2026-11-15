using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Connection to the server. Global singleton: it survives scene changes.
/// LiteNetLib events arrive on the network thread (UnsyncedEvents); the game has nothing to poll.
/// Everything that touches the game (map loading, skin, round notifications) happens in GlobalTick.
/// </summary>
internal sealed class MultiplayerClient : INetEventListener
{
    private const double ReconnectDelaySeconds = 5;

    public static MultiplayerClient Instance { get; } = new();

    private readonly object sendGate = new();
    private readonly NetDataWriter writer = new();
    private NetManager? net;
    private volatile NetPeer? server;

    /// <summary>The WebSocket to the room we're in (website rooms). Null when we connect over UDP.</summary>
    private volatile WsConnection? webServer;

    /// <summary>Room the player is in: its WebSocket address and password. Room mode is on while roomUrl is set.</summary>
    private string? roomUrl;
    private string? roomPassword;
    private bool roomExitHooked;

    /// <summary>A room created on the website, waiting to be joined on the game thread (see <see cref="EnsureConnected"/>).</summary>
    private sealed record PendingRoom(string Code, string Password, string AdminPassword);
    private volatile PendingRoom? pendingRoom;
    private volatile string? pendingRoomError;
    private double lastConnectAttempt = double.NegativeInfinity;
    private string mapKey = "";
    private string playerName = "Joueur";
    private string serverAddress = "";

    /// <summary>The room the player is in (its code, shown in the menu). Null when not in a room.</summary>
    public string? RoomCode { get; private set; }

    /// <summary>Disconnection requested from the menu: no automatic reconnection.</summary>
    private bool userDisconnected;

    /// <summary>Last disconnection reason notified (so the same one isn't repeated every 5 s).</summary>
    private string lastDisconnectReason = "";

    private bool wasReady;

    public ConcurrentDictionary<int, RemotePlayer> Players { get; } = new();

    public RoundState Round { get; } = new();

    /// <summary>Round whose map was already loaded (so it's loaded only once per round).</summary>
    private int loadedRoundId;

    /// <summary>Last (round, phase) announced by a notification.</summary>
    private (int, RoundPhase) notifiedRound;

    /// <summary>The game has finished starting (menu or level shown): a map can be imposed on it.</summary>
    private bool gameReady;

    private GlobalOverlay? overlay;
    private int localSkin;
    private int skinCheckTick;
    public string CurrentMapKey => mapKey;

    public string ServerAddress => serverAddress;

    public string PlayerName => playerName;

    /// <summary>True if we're on the current round's map (standings, automatic retry…).</summary>
    public bool IsOnRoundMap
    {
        get
        {
            var r = Round.Current;
            return IsReady && r.IsRound && Msg.MapMatches(mapKey, r.Info.MapKey);
        }
    }

    public int? LocalId { get; private set; }

    private bool HasLink => server != null || webServer != null;

    public bool IsReady => HasLink && LocalId.HasValue;

    public bool IsConnecting => HasLink && !LocalId.HasValue;

    public bool IsUserDisconnected => userDisconnected;

    /// <summary>Spectator by choice (F8): invisible to the others, not counted in rounds.</summary>
    public bool IsSpectating { get; private set; }

    public void SetSpectating(bool spectating)
    {
        if (spectating == IsSpectating)
        {
            return;
        }
        IsSpectating = spectating;
        if (IsReady)
        {
            Send(w => Msg.Spectate(w, null, spectating), DeliveryMethod.ReliableOrdered);
        }
        Notifications.Info(spectating ? Tr.YouAreSpectating : Tr.YouAreBackInGame);
        // Back to the start line: frozen there while spectating, fresh run when coming back.
        if (GameAccess.IsIngameScene(GameAccess.CurrentScene))
        {
            GameAccess.RetryCurrentLevel();
        }
    }

    /// <summary>Logged in as admin on this server.</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>The server's map rotation (received with the admin status).</summary>
    public IReadOnlyList<string> AdminMaps { get; private set; } = Array.Empty<string>();

    /// <summary>The server's match settings (received when admin).</summary>
    public MatchSettings? ServerSettings { get; private set; }

    /// <summary>Sends what the admin changed (<paramref name="edited"/> compared to the <paramref name="baseline"/> the screen started from).</summary>
    public void SendSettings(MatchSettings edited, MatchSettings baseline, bool saveToFile)
    {
        if (IsReady && IsAdmin)
        {
            string patch = SettingsPatch.Diff(edited, baseline);
            Send(w => Msg.SettingsJson(w, patch, saveToFile), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>What the server supports (mod version, game modes, optional features), received in Welcome.</summary>
    public Capabilities ServerCapabilities { get; private set; } = Capabilities.Unknown;

    /// <summary>Spectating because the server runs a game mode this version doesn't know.</summary>
    private bool spectatingUnknownMode;

    /// <summary>The unknown mode is what made us spectate (to undo it afterwards).</summary>
    private bool autoSpectated;

    /// <summary>Incremented when the admin status changes (the menu rebuilds itself).</summary>
    public int AdminVersion { get; private set; }

    public void AdminLogin(string password)
    {
        if (IsReady)
        {
            Send(w => Msg.AdminLogin(w, password), DeliveryMethod.ReliableOrdered);
        }
    }

    public void Admin(AdminCommand command, int argument = 0)
    {
        if (IsReady && IsAdmin)
        {
            Send(w => Msg.Admin(w, command, argument), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Object (scene) that declared the current map; only it can reset it to "" when leaving.</summary>
    public object? MapOwner { get; private set; }

    // ---------- Global tick (game thread) ----------

    /// <summary>Called on every game tick, whatever the scene (see GlobalTickPatch).</summary>
    public void GlobalTick(BytingLib.Scene? currentScene)
    {
        if (overlay == null && GameAccess.Game != null)
        {
            overlay = new GlobalOverlay();
            GameAccess.GlobalStuff.Add(overlay);
        }

        // A newer release on GitHub: said once in the chat, as soon as the overlay can show it.
        if (!updateAnnounced && UpdateCheck.NewerVersion is string newer && overlay != null)
        {
            updateAnnounced = true;
            Notifications.Warn(Tr.UpdateAvailable(newer));
        }

        // Menus, chat and connection work as soon as the game runs (main menu included).
        ModInput.Update();
        HandleHotkeys();
        PopupMenu.Tick();
        KeyBindings.Tick();
        StartGate.Tick(this);
        HideSeek.Tick();
        if (SteamP2P.Available)
        {
            SteamLobby.Tick();
        }
        TrackLocalSkin();
        EnsureConnected();
        FlushPendingFinish();
        AnnounceConnection();

        var r = Round.Current;
        AnnounceRound(r);

        // Loading the round's map automatically waits until the game has really started (level selection or a
        // level): never during the first-launch save creation. F7 / "Join round" also count as ready.
        if (!gameReady)
        {
            gameReady = currentScene?.GetType().Name == "LevelSelectionScene" || GameAccess.IsIngameScene(currentScene);
            if (!gameReady)
            {
                return;
            }
            ModLog.Info($"Game ready ({currentScene?.GetType().Name})");
        }

        // New round: load the imposed map (once per round). In Rounds mode, every round
        // reloads the map (same map): everyone starts again from the start line.
        if (IsReady && r.Info.Phase is RoundPhase.Playing or RoundPhase.Preparing && r.Info.RoundId != loadedRoundId)
        {
            loadedRoundId = r.Info.RoundId;
            LoadRoundMap(r.Info.MapKey);
        }
    }

    private bool updateAnnounced;

    private void HandleHotkeys()
    {
        if (KeyBindings.IsCapturing)
        {
            KeyBindings.TickCapture(); // F8 > Keys: the next key pressed is the new binding
            ScoreboardHeld = false;
            return;
        }
        if (GameAccess.IsTyping() || ChatInput.IsOpen)
        {
            ScoreboardHeld = false; // no hotkeys while typing text
            return;
        }
        ScoreboardHeld = ModInput.Down(ModConfig.Current.ScoreboardKey) && !PopupMenu.AnyOpen;
        if (ModInput.Pressed(ModConfig.Current.ChatKey) && CanOpenChat())
        {
            ChatInput.Open();
        }
        if (ModInput.Pressed(ModConfig.Current.MenuKey))
        {
            MultiplayerMenu.Open();
        }
        ModConfig cfg = ModConfig.Current;
        if (ModInput.Pressed(cfg.CloseEndScreenKey) && GlobalOverlay.EndScreenVisible && !PopupMenu.AnyOpen && !KeyBindings.BlocksEscape)
        {
            GlobalOverlay.DismissEndScreen();
        }
        if (ModInput.Pressed(ModConfig.Current.RejoinKey))
        {
            RejoinRound();
        }
        bool next = ModInput.Pressed(cfg.SpectateNextKey);
        bool previous = ModInput.Pressed(cfg.SpectatePreviousKey);
        if (Spectator.IsWatching)
        {
            if (next)
            {
                Spectator.Cycle(+1);
            }
            if (previous)
            {
                Spectator.Cycle(-1);
            }
        }
        else if (HideSeek.KeepFrozen)
        {
            // Frozen mouse: follow a player (the hunter first) or go back to the free camera.
            if (next || previous)
            {
                if (HideSeek.FollowRequested)
                {
                    Spectator.Cycle(next ? +1 : -1);
                }
                HideSeek.FollowRequested = true;
            }
            if (ModInput.Pressed(cfg.StopFollowingKey))
            {
                HideSeek.FollowRequested = false;
            }
        }
    }

    /// <summary>The player list key is held (see <see cref="GlobalOverlay"/>).</summary>
    public static bool ScoreboardHeld { get; private set; }

    private static readonly Type? ClickToStartSceneType = HarmonyLib.AccessTools.TypeByName("SE.ClickToStartScene");

    /// <summary>
    /// Enter is also the game's confirm key: with it, the chat only opens in a level, with no menu on top
    /// (the start-line popup and the frozen Hide and Seek view are fine). Any other key works everywhere.
    /// </summary>
    private static bool CanOpenChat()
    {
        if (!ModConfig.Current.ChatKey.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        BytingLib.Scene? scene = GameAccess.CurrentScene;
        if (PopupMenu.AnyOpen || !GameAccess.IsIngameScene(scene))
        {
            return false;
        }
        BytingLib.Scene top = scene!.GetTopmostScene();
        return ReferenceEquals(top, scene) || (ClickToStartSceneType?.IsInstanceOfType(top) ?? false);
    }

    /// <summary>Loads a map ("o:&lt;id&gt;" or "c:M&lt;id&gt;"), e.g. to join a player with /goto.</summary>
    public void GoToMap(string key)
    {
        gameReady = true; // explicit request
        LoadRoundMap(key);
    }

    /// <summary>The player can change skin in the game's menu: tell the others.</summary>
    private void TrackLocalSkin()
    {
        if (++skinCheckTick % 60 != 0 || GameAccess.Game == null)
        {
            return;
        }
        int skin;
        try
        {
            skin = SkinCache.LocalSkinId(GameAccess.Game);
        }
        catch (Exception)
        {
            return; // no save yet (first launch): try again later
        }
        if (skin != localSkin)
        {
            localSkin = skin;
            if (IsReady)
            {
                Send(w => Msg.SkinChanged(w, null, skin), DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void AnnounceConnection()
    {
        bool ready = IsReady;
        if (ready && !wasReady)
        {
            int others = Players.Count;
            Notifications.Good(Tr.Connected(serverAddress, others, Tr.KeyName(ModConfig.Current.MenuKey)));
            lastDisconnectReason = "";
        }
        wasReady = ready;
    }

    private void AnnounceRound(RoundState.Snapshot r)
    {
        var key = (r.Info.RoundId, r.Info.Phase);
        if (!IsReady || !r.IsRound || key == notifiedRound)
        {
            return;
        }
        notifiedRound = key;
        if (r.Info.Phase == RoundPhase.Playing)
        {
            Notifications.Info(Tr.RoundStarted(r.Info.RoundId, GameAccess.DescribeMap(r.Info.MapKey)));
        }
        else if (r.Info.Phase == RoundPhase.Results)
        {
            var winner = r.Leaderboard.FirstOrDefault();
            Notifications.Show(winner.Name == null
                ? Tr.RoundEndNoTimes
                : Tr.RoundEndWinner(winner.Name, Msg.FormatTime(winner.BestMs)), new Microsoft.Xna.Framework.Color(255, 210, 90));
        }
    }

    // ---------- Actions (menu, touches) ----------

    /// <summary>
    /// Address used instead of the configured one: our hosted game (127.0.0.1:port) or the local Steam P2P proxy
    /// towards a friend's game; with its display name and the admin password of our hosted game.
    /// </summary>
    private string? hostAddress;
    private string? hostAddressLabel;
    private string? hostAdminPassword;

    /// <summary>Hosting from F8: starts the in-game server, then connects to it as admin.</summary>
    public void HostGame(int port)
    {
        string? error = HostedServer.Start(port);
        if (error != null)
        {
            Notifications.Warn(Tr.HostFailed(error));
            return;
        }
        ClearRoom();
        SteamP2P.StopJoin();
        hostAddress = $"127.0.0.1:{port}";
        hostAddressLabel = null;
        hostAdminPassword = HostedServer.AdminPassword;
        ConnectNow();
        Notifications.Good(Tr.HostStarted(port));
        if (ModConfig.Current.SteamP2PHost && SteamP2P.Available)
        {
            try
            {
                SteamP2P.StartHost(port);
                Notifications.Good(Tr.SteamHostVisible);
            }
            catch (Exception e)
            {
                ModLog.Error("Steam P2P hosting failed", e);
                Notifications.Warn(Tr.SteamUnavailable);
            }
        }
    }

    /// <summary>Joins a Steam friend's hosted game through Steam P2P (no port to open).</summary>
    public void JoinFriend(ulong steamId, string name)
    {
        if (HostedServer.IsRunning)
        {
            StopHosting();
        }
        try
        {
            ClearRoom();
            int localPort = SteamP2P.StartJoin(steamId);
            hostAddress = $"127.0.0.1:{localPort}";
            hostAddressLabel = $"Steam: {name}";
            hostAdminPassword = null;
            ConnectNow();
            Notifications.Info(Tr.JoiningFriend(name));
        }
        catch (Exception e)
        {
            ModLog.Error("Steam P2P join failed", e);
            Notifications.Warn(Tr.SteamUnavailable);
        }
    }

    public void StopHosting()
    {
        if (!HostedServer.IsRunning)
        {
            return;
        }
        hostAddress = null;
        hostAdminPassword = null;
        Disconnect();
        SteamP2P.StopHost();
        HostedServer.Stop();
        Notifications.Info(Tr.HostStopped);
    }

    // ---------- Rooms (website) ----------

    /// <summary>Creates a room on the website, then joins it as its admin. The web request runs off the game thread.</summary>
    public void CreateRoom(string password)
    {
        string api = ModConfig.Current.RoomSite.Trim().TrimEnd('/') + "/multiplayer/api/rooms";
        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string json = JsonSerializer.Serialize(new { password });
                using HttpResponseMessage response = await http.PostAsync(api, new StringContent(json, Encoding.UTF8, "application/json"));
                string text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    pendingRoomError = ((int)response.StatusCode).ToString();
                    return;
                }
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                // Only the code is used: the address is rebuilt from our own site setting.
                string code = root.GetProperty("code").GetString() ?? "";
                string admin = root.GetProperty("adminPassword").GetString() ?? "";
                pendingRoom = new PendingRoom(code, password, admin);
            }
            catch (Exception e)
            {
                ModLog.Error("Room creation failed", e);
                pendingRoomError = e.GetType().Name;
            }
        });
    }

    /// <summary>Joins a room by its code, with its password if it has one.</summary>
    public void JoinRoom(string code, string? password)
    {
        code = code.Trim().ToUpperInvariant();
        JoinRoomNow(code, ModConfig.Current.RoomSocketUri(code).ToString(), password, null);
    }

    private void JoinRoomNow(string code, string url, string? password, string? adminPassword)
    {
        if (HostedServer.IsRunning)
        {
            StopHosting();
        }
        SteamP2P.StopJoin();
        hostAddress = null;
        hostAddressLabel = null;
        RoomCode = code;
        roomUrl = url;
        roomPassword = string.IsNullOrEmpty(password) ? null : password;
        hostAdminPassword = adminPassword; // the creator of a room is its admin
        ConnectNow();
    }

    /// <summary>Leaves room mode, so the next connection goes to the configured server or to a hosted or friend game.</summary>
    private void ClearRoom()
    {
        RoomCode = null;
        roomUrl = null;
        roomPassword = null;
        hostAdminPassword = null;
    }

    /// <summary>(Re)connects with the configured address and name.</summary>
    public void Connect()
    {
        if (HostedServer.IsRunning)
        {
            StopHosting(); // connecting elsewhere ends the hosted game
        }
        if (SteamP2P.IsJoining)
        {
            SteamP2P.StopJoin(); // back to the configured server address
            hostAddress = null;
            hostAddressLabel = null;
        }
        ClearRoom();
        ConnectNow();
    }

    private void ConnectNow()
    {
        userDisconnected = false;
        if (HasLink)
        {
            DropConnection("reconnecting");
        }
        lastConnectAttempt = double.NegativeInfinity;
        lastDisconnectReason = "";
        EnsureConnected();
    }

    public void Disconnect()
    {
        userDisconnected = true;
        if (HasLink)
        {
            DropConnection("manual disconnection");
            Notifications.Info(Tr.DisconnectedByUser);
        }
    }

    private void DropConnection(string why)
    {
        NetPeer? peer = server;
        WsConnection? web = webServer;
        server = null; // this peer's disconnection event will be ignored
        webServer = null;
        peer?.Disconnect();
        web?.Disconnect(why);
        ModLog.Info($"Connection closed ({why})");
        ResetSessionState();
    }

    private void ResetSessionState()
    {
        LocalId = null;
        Players.Clear();
        Round.Reset();
        loadedRoundId = 0;
        notifiedRound = default;
        ServerCapabilities = Capabilities.Unknown;
        if (spectatingUnknownMode && autoSpectated)
        {
            IsSpectating = false; // spectating was only for that server's unknown mode
        }
        spectatingUnknownMode = false;
        if (IsAdmin)
        {
            IsAdmin = false;
            AdminVersion++;
        }
    }

    public void RejoinRound()
    {
        var r = Round.Current;
        if (!IsReady || r.Info.Phase == RoundPhase.Free)
        {
            Notifications.Warn(Tr.NoRound);
        }
        else if (IsOnRoundMap)
        {
            Notifications.Info(Tr.AlreadyOnRoundMap);
        }
        else
        {
            gameReady = true; // explicit request: the player is ready to play
            loadedRoundId = r.Info.RoundId;
            LoadRoundMap(r.Info.MapKey);
        }
    }

    private static void LoadRoundMap(string key)
    {
        // Workshop map: "c:M<id>" (latest version) or "c:M<id>.<version>" (exact version).
        if (Msg.WorkshopId(key) is ulong workshopId)
        {
            try
            {
                GameAccess.StartWorkshopMap(workshopId, key.Contains('.') ? key[2..] : null);
            }
            catch (Exception e)
            {
                ModLog.Error($"Could not load map {key}", e);
                Notifications.Warn(Tr.MapLoadFailed(key));
            }
            return;
        }
        // Official level: "o:<id>" or "o:<id>:<version>".
        string[] parts = key.Split(':');
        if (parts.Length >= 2 && parts[0] == "o" && uint.TryParse(parts[1], out uint id))
        {
            uint? version = parts.Length >= 3 && uint.TryParse(parts[2], out uint v) ? v : null;
            try
            {
                if (!GameAccess.StartOfficialLevel(id, version, quickRetry: false))
                {
                    Notifications.Warn(Tr.MapLoadFailedVersion(key));
                }
            }
            catch (Exception e)
            {
                ModLog.Error($"Could not load map {key}", e);
                Notifications.Warn(Tr.MapLoadFailed(key));
            }
        }
        else
        {
            ModLog.Warn($"Unsupported round map: {key}");
            Notifications.Warn(Tr.MapNotSupported(key));
        }
    }

    // ---------- Times ----------

    private (int RoundId, SE.LevelTimer Timer, SE.TimeDiffOnCheckpoints? Display, bool SendToServer)? pendingFinish;

    /// <summary>Finish line crossed: the time is read (and sent if needed) at the end of the tick, once the timer is final.</summary>
    public void QueueFinish(int roundId, SE.LevelTimer timer, SE.TimeDiffOnCheckpoints? display, bool sendToServer) =>
        pendingFinish = (roundId, timer, display, sendToServer);

    private void FlushPendingFinish()
    {
        if (pendingFinish is not { } finish)
        {
            return;
        }
        pendingFinish = null;
        uint ms = finish.Timer.Counter.MS;
        int? delta = SplitTracker.Finished(mapKey, finish.RoundId, finish.Timer, finish.Display);
        if (!finish.SendToServer)
        {
            return; // outside a round: only shown by the splits
        }
        if (Round.Current.Info.Mode.IsSynchronized() && StartGate.LostMs > 0)
        {
            // One-attempt round: the time lost by respawning at the start (the game restarted its timer) counts.
            ms += StartGate.LostMs;
            delta += (int)StartGate.LostMs;
        }
        ModLog.Info($"Finished in {Msg.FormatTime(ms)} (round {finish.RoundId}) → sent to the server");
        if (Round.Current.Info.Mode.IsSynchronized())
        {
            StartGate.MarkFinished();
        }
        if (delta is not int d || d < 0)
        {
            Notifications.Good(Tr.NewBest(Msg.FormatTime(ms)));
        }
        else
        {
            Notifications.Info(Tr.TimeVsBest(Msg.FormatTime(ms), Msg.FormatTime((uint)(ms - d)), d / 1000f));
        }
        SendFinish(finish.RoundId, ms);
    }

    public void SendReady(int roundId) => Send(w => Msg.Ready(w, roundId), DeliveryMethod.ReliableOrdered);

    public void SendGiveUp(int roundId) => Send(w => Msg.GiveUp(w, roundId), DeliveryMethod.ReliableOrdered);

    /// <summary>Hide and Seek: we found this mouse (or, with our own id, we left our hiding place).</summary>
    public void SendTag(int roundId, int targetId) => Send(w => Msg.Tag(w, roundId, targetId), DeliveryMethod.ReliableOrdered);

    public void SendChat(string text)
    {
        if (IsReady)
        {
            Send(w => Msg.ChatToServer(w, text), DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendFinish(int roundId, uint timeMs)
    {
        if (IsReady)
        {
            Send(w => Msg.Finish(w, roundId, timeMs), DeliveryMethod.ReliableOrdered);
        }
    }

    // ---------- Connection ----------

    /// <summary>Call regularly from the game thread: (re)connects if needed.</summary>
    public void EnsureConnected()
    {
        if (pendingRoom is { } created)
        {
            pendingRoom = null;
            JoinRoomNow(created.Code, ModConfig.Current.RoomSocketUri(created.Code).ToString(), created.Password, created.AdminPassword);
            Notifications.Good(Tr.RoomCreated(created.Code));
            return;
        }
        if (pendingRoomError is string createError)
        {
            pendingRoomError = null;
            Notifications.Warn(Tr.RoomCreateFailed(createError));
        }
        if (!ModConfig.Current.AutoConnect && lastConnectAttempt == double.NegativeInfinity && !userDisconnected)
        {
            // AutoConnect = false: wait for the player to connect from the menu.
            userDisconnected = true;
        }
        if (userDisconnected || HasLink || NetClock.Now - lastConnectAttempt < ReconnectDelaySeconds)
        {
            return;
        }
        lastConnectAttempt = NetClock.Now;

        if (roomUrl != null)
        {
            ConnectRoom();
            return;
        }

        try
        {
            if (net == null)
            {
                net = new NetManager(this)
                {
                    UnsyncedEvents = true,
                    AutoRecycle = true,
                    UpdateTime = 5,
                    DisconnectTimeout = 10_000,
                };
                net.Start();
                // Game closing: tell the server instead of letting it wait for the timeout.
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    net.Stop(sendDisconnectMessages: true);
                    SteamLobby.Shutdown();
                    SteamP2P.StopJoin();
                    SteamP2P.StopHost();
                    HostedServer.Stop();
                };
            }

            PrepareIdentity();
            var (host, port) = hostAddress != null
                ? (hostAddress[..hostAddress.LastIndexOf(':')], int.Parse(hostAddress[(hostAddress.LastIndexOf(':') + 1)..]))
                : ModConfig.Current.ParseServer();
            serverAddress = hostAddressLabel ?? $"{host}:{port}";
            ModLog.Info($"Connecting to {serverAddress} as \"{playerName}\"…");
            server = net.Connect(host, port, ProtocolInfo.ConnectionData(ModConfig.Current.ServerPassword));
        }
        catch (Exception e)
        {
            ModLog.Error("Connection attempt failed", e);
            NotifyDisconnect(Tr.CannotConnect(ModConfig.Current.Server, e.GetType().Name));
            server = null;
        }
    }

    /// <summary>Joins a room over WebSocket. Runs on the game thread; the connection itself runs on its own thread.</summary>
    private void ConnectRoom()
    {
        try
        {
            if (!roomExitHooked)
            {
                roomExitHooked = true;
                // Game closing: tell the room instead of letting it wait for the timeout.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => webServer?.Disconnect("client closed");
            }

            PrepareIdentity();
            serverAddress = RoomCode ?? roomUrl!;
            ModLog.Info($"Connecting to room {serverAddress} as \"{playerName}\"…");
            var headers = new Dictionary<string, string> { ["x-sbm-version"] = ProtocolInfo.ConnectionKey };
            if (roomPassword != null)
            {
                headers["x-room-password"] = roomPassword;
            }
            var connection = new WsConnection(new Uri(roomUrl!), headers, OnWebOpen, OnWebMessage, OnWebClosed);
            webServer = connection;
            connection.Start();
        }
        catch (Exception e)
        {
            ModLog.Error("Connection attempt failed", e);
            NotifyDisconnect(Tr.CannotConnect(serverAddress, e.GetType().Name));
            webServer = null;
        }
    }

    /// <summary>Our name and skin, read from the game save when available.</summary>
    private void PrepareIdentity()
    {
        playerName = ResolvePlayerName();
        try
        {
            if (GameAccess.Game != null)
            {
                localSkin = SkinCache.LocalSkinId(GameAccess.Game);
            }
        }
        catch (Exception)
        {
            // no save yet (first launch): the skin is sent later by TrackLocalSkin
        }
    }

    private void NotifyDisconnect(string reason)
    {
        if (reason != lastDisconnectReason)
        {
            lastDisconnectReason = reason;
            Notifications.Warn(reason);
        }
    }

    public void SetMap(object owner, string key)
    {
        if (key == "" && !ReferenceEquals(owner, MapOwner))
        {
            return; // a more recent scene already declared its map
        }
        MapOwner = key == "" ? null : owner;
        if (key == mapKey)
        {
            return;
        }
        mapKey = key;
        ModLog.Info($"Current map: {(key == "" ? "(menu)" : key)}");
        if (IsReady)
        {
            Send(w => Msg.MapChanged(w, null, key), DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendState(in PlayerState state)
    {
        if (IsReady && !IsSpectating)
        {
            PlayerState s = state;
            Send(w => Msg.State(w, s), DeliveryMethod.Unreliable);
        }
    }

    private void Send(Func<NetDataWriter, NetDataWriter> build, DeliveryMethod method)
    {
        lock (sendGate)
        {
            if (webServer != null)
            {
                webServer.Send(build(writer), method);
            }
            else
            {
                server?.Send(build(writer), method);
            }
        }
    }

    private static string ResolvePlayerName()
    {
        if (!string.IsNullOrWhiteSpace(ModConfig.Current.PlayerName))
        {
            return Msg.SanitizeName(ModConfig.Current.PlayerName);
        }
        try
        {
            return Msg.SanitizeName(Steamworks.SteamFriends.GetPersonaName());
        }
        catch (Exception e)
        {
            ModLog.Warn($"Steam name unavailable ({e.GetType().Name}), using the default name");
            return "Player";
        }
    }

    // ---------- LiteNetLib events (network thread) ----------

    public void OnPeerConnected(NetPeer peer)
    {
        ModLog.Info($"Connected to server {peer}");
        server = peer; // in case the event arrives before Connect() returns
        int skin = localSkin;
        Send(w => Msg.Hello(w, playerName, skin, ProtocolInfo.OwnCapabilities), DeliveryMethod.ReliableOrdered);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!ReferenceEquals(peer, server))
        {
            return; // old connection closed on purpose (Connect/Disconnect from the menu)
        }
        // Code sent by the server (rejection, kick…): read it only once, reading consumes the bytes.
        string code = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : "";
        string reason = code != "" ? TranslateReasonCode(code) : info.Reason switch
        {
            DisconnectReason.ConnectionFailed => Tr.ServerUnreachable(serverAddress),
            DisconnectReason.Timeout => Tr.ConnectionLost,
            DisconnectReason.RemoteConnectionClose => Tr.ServerClosed,
            DisconnectReason.ConnectionRejected => Tr.ConnectionRejected,
            _ => info.Reason.ToString(),
        };
        if (code is ServerReasons.BadPassword or ServerReasons.Kicked)
        {
            // Retrying would be pointless: wait for the player to act (F8 menu).
            userDisconnected = true;
            ModLog.Warn($"Disconnected from the server: {reason}");
            NotifyDisconnect(reason);
        }
        else
        {
            ModLog.Warn($"Disconnected from the server: {reason}. Retrying in {ReconnectDelaySeconds} s.");
            NotifyDisconnect(Tr.RetryingEvery(reason, (int)ReconnectDelaySeconds));
        }
        server = null;
        ResetSessionState();
    }

    /// <summary>The WebSocket layer reports HTTP refusals as "HTTP_STATUS:n"; these are the codes the server means by them.</summary>
    private static string NormalizeCloseReason(string reason) => reason switch
    {
        "HTTP_STATUS:401" => ServerReasons.BadPassword,
        "HTTP_STATUS:404" => ServerReasons.RoomNotFound,
        "HTTP_STATUS:426" => ServerReasons.VersionMismatch,
        "HTTP_STATUS:429" => ServerReasons.RateLimited,
        "HTTP_STATUS:503" => ServerReasons.Full,
        _ when reason.StartsWith("HTTP_STATUS:", StringComparison.Ordinal) => ServerReasons.Unreachable,
        _ => reason,
    };

    /// <summary>The server sends codes (SERVER_FULL, VERSION_MISMATCH:4, KICKED…) that the client translates.</summary>
    private static string TranslateReasonCode(string code) => code switch
    {
        ServerReasons.Full => Tr.ServerFull,
        ServerReasons.BadPassword => Tr.BadPassword,
        ServerReasons.Kicked => Tr.Kicked,
        ServerReasons.RoomNotFound => Tr.RoomNotFound,
        ServerReasons.RateLimited => Tr.RateLimited,
        _ when code.StartsWith(ServerReasons.VersionMismatch + ":") => Tr.VersionMismatch(code[(ServerReasons.VersionMismatch.Length + 1)..]),
        _ => code, // older server: plain-text message
    };

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!ReferenceEquals(peer, server))
        {
            return;
        }
        HandleMessage(reader, peer.Ping);
    }

    /// <summary>One message from the server, over UDP or a room's WebSocket. Runs on the network thread.</summary>
    private void HandleMessage(NetDataReader reader, int pingMs)
    {
        try
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Welcome:
                {
                    var (yourId, others, serverCapabilities) = Msg.GetWelcome(reader);
                    LocalId = yourId;
                    ServerCapabilities = serverCapabilities;
                    if (hostAdminPassword != null)
                    {
                        string pw = hostAdminPassword;
                        Send(w => Msg.AdminLogin(w, pw), DeliveryMethod.ReliableOrdered); // the host is the admin of its game
                    }
                    Players.Clear();
                    foreach (PlayerInfo other in others)
                    {
                        AddPlayer(other, notify: false);
                    }
                    ModLog.Info($"Welcome! Our id: #{LocalId}, {others.Count} other player(s), server mod {serverCapabilities.Version}");
                    // The server doesn't know our map yet (joining mid-game, or reconnecting).
                    if (mapKey != "")
                    {
                        Send(w => Msg.MapChanged(w, null, mapKey), DeliveryMethod.ReliableOrdered);
                    }
                    if (IsSpectating)
                    {
                        Send(w => Msg.Spectate(w, null, true), DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
                case MessageType.PlayerJoined:
                    AddPlayer(Msg.GetPlayerInfo(reader), notify: true);
                    break;
                case MessageType.PlayerLeft:
                {
                    int id = reader.GetInt();
                    if (Players.TryRemove(id, out RemotePlayer? p))
                    {
                        ModLog.Info($"{p.Name} (#{id}) left");
                        Notifications.Info(Tr.PlayerLeft(p.Name));
                    }
                    break;
                }
                case MessageType.MapChanged:
                {
                    int id = reader.GetInt();
                    string key = reader.GetString(256);
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.SetMap(key);
                        ModLog.Info($"{p.Name} (#{id}) → {(key == "" ? "menu" : key)}");
                    }
                    break;
                }
                case MessageType.Spectate:
                {
                    int id = reader.GetInt();
                    bool spectating = reader.GetBool();
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.IsSpectator = spectating;
                        Notifications.Info(spectating ? Tr.PlayerSpectating(p.Name) : Tr.PlayerBackInGame(p.Name));
                    }
                    break;
                }
                case MessageType.SkinChanged:
                {
                    int id = reader.GetInt();
                    int skin = reader.GetInt();
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.Skin = skin;
                    }
                    break;
                }
                case MessageType.RoundInfo:
                {
                    RoundInfo info = Msg.GetRound(reader);
                    WatchUnknownMode(info);
                    Round.SetInfo(info, pingMs / 1000.0);
                    ModLog.Info($"Round {info.RoundId}: {info.Phase} on {(info.MapKey == "" ? "-" : info.MapKey)}, {info.RemainingSeconds:0} s");
                    break;
                }
                case MessageType.Leaderboard:
                {
                    var (roundId, entries) = Msg.GetLeaderboard(reader);
                    Round.SetLeaderboard(roundId, entries);
                    break;
                }
                case MessageType.AdminStatus:
                {
                    var (isAdmin, maps) = Msg.GetAdminStatus(reader);
                    if (!isAdmin)
                    {
                        Notifications.Warn(Tr.AdminLoginFailed);
                    }
                    IsAdmin = isAdmin;
                    AdminMaps = maps;
                    AdminVersion++;
                    break;
                }
                case MessageType.ServerEvent:
                {
                    var (code, arg, fallback) = Msg.GetCodedText(reader);
                    if ((Tr.ServerEvent(code, arg) ?? NonEmpty(fallback)) is { } text)
                    {
                        Notifications.Show(text, new Microsoft.Xna.Framework.Color(255, 210, 90));
                    }
                    else
                    {
                        ModLog.Info($"Unknown server event {code} ({arg})");
                    }
                    break;
                }
                case MessageType.Chat:
                {
                    reader.GetInt(); // sender id (reserved: mute, colors…)
                    string name = reader.GetString(ProtocolInfo.MaxNameLength * 4);
                    string text = reader.GetString(ProtocolInfo.MaxChatLength * 4);
                    ChatLog.AddPlayer(name, text);
                    break;
                }
                case MessageType.CommandReply:
                {
                    var (code, arg, fallback) = Msg.GetCodedText(reader);
                    string text = Tr.CommandReply(code, arg) ?? NonEmpty(fallback) ?? $"{code} {arg}";
                    Notifications.Show(text, new Microsoft.Xna.Framework.Color(255, 170, 80));
                    break;
                }
                case MessageType.Settings:
                    try
                    {
                        ServerSettings = Msg.GetSettings(reader).Settings;
                    }
                    catch (System.Text.Json.JsonException e)
                    {
                        ModLog.Error("Unreadable server settings", e);
                    }
                    break;
                case MessageType.MatchStandings:
                    Round.Standings = Msg.GetStandings(reader);
                    break;
                case MessageType.HideSeek:
                    Round.SetHideSeek(Msg.GetHideSeek(reader));
                    break;
                case MessageType.States:
                    foreach (var (id, s) in Msg.GetStates(reader))
                    {
                        if (Players.TryGetValue(id, out RemotePlayer? p))
                        {
                            p.AddState(s);
                        }
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Invalid server packet", e);
        }
    }

    private static string? NonEmpty(string s) => s.Length > 0 ? s : null;

    /// <summary>
    /// A newer server can run a game mode this version doesn't know: watch as a spectator (rather than riding
    /// with rules we can't follow) until it switches back to a known mode.
    /// </summary>
    private void WatchUnknownMode(in RoundInfo info)
    {
        bool unknown = info.Phase != RoundPhase.Free && !info.Mode.IsKnown();
        if (unknown && !spectatingUnknownMode)
        {
            spectatingUnknownMode = true;
            Notifications.Warn(Tr.UnknownModeSpectating);
            autoSpectated = !IsSpectating;
            SetSpectating(true);
        }
        else if (!unknown && spectatingUnknownMode)
        {
            spectatingUnknownMode = false;
            if (autoSpectated)
            {
                SetSpectating(false); // a player who was already spectating by choice stays so
            }
        }
    }

    private void AddPlayer(PlayerInfo info, bool notify)
    {
        Players[info.Id] = new RemotePlayer(info);
        ModLog.Info($"{info.Name} (#{info.Id}) joined{(info.MapKey == "" ? "" : $", on {info.MapKey}")}");
        if (notify)
        {
            Notifications.Info(Tr.PlayerJoined(info.Name));
        }
    }

    // ---------- WebSocket events (website rooms, network thread) ----------

    private void OnWebOpen(WsConnection connection)
    {
        if (!ReferenceEquals(connection, webServer))
        {
            return;
        }
        ModLog.Info($"Connected to room {serverAddress}");
        int skin = localSkin;
        Send(w => Msg.Hello(w, playerName, skin, ProtocolInfo.OwnCapabilities), DeliveryMethod.ReliableOrdered);
    }

    private void OnWebMessage(WsConnection connection, byte[] data)
    {
        if (ReferenceEquals(connection, webServer))
        {
            HandleMessage(new NetDataReader(data), connection.Ping);
        }
    }

    private void OnWebClosed(WsConnection connection, string closeReason)
    {
        if (!ReferenceEquals(connection, webServer))
        {
            return; // closed on purpose (Connect/Disconnect from the menu)
        }
        string code = NormalizeCloseReason(closeReason);
        string reason = code switch
        {
            "" => Tr.ConnectionLost,
            ServerReasons.Unreachable => Tr.ServerUnreachable(serverAddress),
            _ => TranslateReasonCode(code),
        };
        if (code is ServerReasons.BadPassword or ServerReasons.Kicked or ServerReasons.RoomNotFound
            || code.StartsWith(ServerReasons.VersionMismatch, StringComparison.Ordinal))
        {
            // Retrying would be pointless: wait for the player to act (F8 menu).
            userDisconnected = true;
            ModLog.Warn($"Disconnected from the room: {reason}");
            NotifyDisconnect(reason);
        }
        else
        {
            ModLog.Warn($"Disconnected from the room: {reason}. Retrying in {ReconnectDelaySeconds} s.");
            NotifyDisconnect(Tr.RetryingEvery(reason, (int)ReconnectDelaySeconds));
        }
        webServer = null;
        ResetSessionState();
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        ModLog.Warn($"Network error {endPoint}: {socketError}");

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
}
