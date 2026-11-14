using System.Collections.Concurrent;
using System.Net.Http;
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
/// Connexion au serveur. Singleton global : il survit aux changements de scène.
/// Les événements de la connexion WebSocket arrivent sur le thread réseau ; le jeu n'a rien à « poller ».
/// Tout ce qui touche au jeu (chargement de map, skin, notifications de round) se fait dans GlobalTick.
/// </summary>
internal sealed class MultiplayerClient
{
    private const double ReconnectDelaySeconds = 5;

    public static MultiplayerClient Instance { get; } = new();

    private readonly object sendGate = new();
    private readonly NetDataWriter writer = new();
    private volatile WsConnection? server;
    private readonly ConcurrentQueue<Action> gameThreadQueue = new();
    private bool processExitHooked;
    private double lastConnectAttempt = double.NegativeInfinity;
    private string mapKey = "";
    private string playerName = "Joueur";
    private string serverAddress = "";

    /// <summary>Déconnexion demandée depuis le menu : pas de reconnexion automatique.</summary>
    private bool userDisconnected;

    /// <summary>Dernière raison de déconnexion notifiée (pour ne pas répéter la même toutes les 5 s).</summary>
    private string lastDisconnectReason = "";

    private bool wasReady;

    public ConcurrentDictionary<int, RemotePlayer> Players { get; } = new();

    public RoundState Round { get; } = new();

    /// <summary>Round dont on a déjà chargé la map (pour ne la charger qu'une fois par round).</summary>
    private int loadedRoundId;

    /// <summary>Dernier (round, phase) annoncé par une notification.</summary>
    private (int, RoundPhase) notifiedRound;

    /// <summary>Le jeu a fini de démarrer (menu ou niveau affiché) : on peut lui imposer une map.</summary>
    private bool gameReady;

    private GlobalOverlay? overlay;
    private int localSkin;
    private int skinCheckTick;
    private KeyboardState previousKeys;

    public string CurrentMapKey => mapKey;

    public string ServerAddress => serverAddress;

    public string PlayerName => playerName;

    /// <summary>Vrai si on est sur la map du round en cours (classement, retry automatique…).</summary>
    public bool IsOnRoundMap
    {
        get
        {
            var r = Round.Current;
            return IsReady && r.IsRound && Msg.MapMatches(mapKey, r.Info.MapKey);
        }
    }

    public int? LocalId { get; private set; }

    public bool IsReady => server != null && LocalId.HasValue;

    public bool IsConnecting => server != null && !LocalId.HasValue;

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

    /// <summary>Connecté en admin sur ce serveur.</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>Rotation de maps du serveur (reçue avec le statut admin).</summary>
    public IReadOnlyList<string> AdminMaps { get; private set; } = Array.Empty<string>();

    /// <summary>Réglages du match du serveur (reçus quand on est admin).</summary>
    public MatchSettings? ServerSettings { get; private set; }

    public void SendSettings(MatchSettings settings, bool saveToFile)
    {
        if (IsReady && IsAdmin)
        {
            Send(w => Msg.Settings(w, settings, saveToFile), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Incrémenté quand le statut admin change (le menu se reconstruit).</summary>
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

    /// <summary>Objet (scène) qui a déclaré la map courante ; seul lui peut la remettre à "" en partant.</summary>
    public object? MapOwner { get; private set; }

    // ---------- Tick global (thread du jeu) ----------

    /// <summary>Appelé à chaque tick du jeu, quelle que soit la scène (voir GlobalTickPatch).</summary>
    public void GlobalTick(BytingLib.Scene? currentScene)
    {
        if (overlay == null && GameAccess.Game != null)
        {
            overlay = new GlobalOverlay();
            GameAccess.GlobalStuff.Add(overlay);
        }

        // Menus, chat and connection work as soon as the game runs (main menu included).
        HandleHotkeys();
        PopupMenu.Tick();
        StartGate.Tick(this);
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
            ModLog.Info($"Jeu prêt ({currentScene?.GetType().Name})");
        }

        // Nouveau round : on charge la map imposée (une seule fois par round). En mode Rounds, chaque
        // manche recharge la map (même map) : tout le monde repart de la ligne de départ.
        if (IsReady && r.Info.Phase is RoundPhase.Playing or RoundPhase.Preparing && r.Info.RoundId != loadedRoundId)
        {
            loadedRoundId = r.Info.RoundId;
            LoadRoundMap(r.Info.MapKey);
        }
    }

    private void HandleHotkeys()
    {
        KeyboardState keys = Keyboard.GetState();
        if (GameAccess.IsTyping() || ChatInput.IsOpen)
        {
            previousKeys = keys; // pas de raccourcis pendant la saisie de texte
            return;
        }
        if (Pressed(keys, ModConfig.Current.ChatKey))
        {
            ChatInput.Open();
        }
        if (Pressed(keys, ModConfig.Current.MenuKey))
        {
            MultiplayerMenu.Open();
        }
        if (Pressed(keys, ModConfig.Current.RejoinKey))
        {
            RejoinRound();
        }
        if (Spectator.IsWatching)
        {
            if (Pressed(keys, "Right"))
            {
                Spectator.Cycle(+1);
            }
            if (Pressed(keys, "Left"))
            {
                Spectator.Cycle(-1);
            }
        }
        previousKeys = keys;
    }

    private bool Pressed(KeyboardState keys, string keyName) =>
        Enum.TryParse(keyName, ignoreCase: true, out Keys key) && keys.IsKeyDown(key) && !previousKeys.IsKeyDown(key);

    /// <summary>Le joueur peut changer de skin dans le menu du jeu : on le signale aux autres.</summary>
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
            Notifications.Good(Tr.Connected(serverAddress, others, ModConfig.Current.MenuKey));
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

    /// <summary>The room we're in: its code (shown in the menu) and what's needed to rejoin it.</summary>
    public string? RoomCode { get; private set; }
    private string? roomUrl;
    private string? roomPassword;
    private string? hostAdminPassword;

    /// <summary>Creates a room on the website and joins it as admin. The HTTP call runs off the game thread.</summary>
    public void CreateRoom(string password)
    {
        string site = ModConfig.Current.Server;
        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string json = JsonSerializer.Serialize(new { password });
                string api = site.Trim().TrimEnd('/') + "/multiplayer/api/rooms";
                using HttpResponseMessage response = await http.PostAsync(api, new StringContent(json, Encoding.UTF8, "application/json"));
                string text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    string status = ((int)response.StatusCode).ToString();
                    PostToGameThread(() => Notifications.Warn(Tr.RoomCreateFailed(status)));
                    return;
                }
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                string code = root.GetProperty("code").GetString() ?? "";
                string url = root.GetProperty("url").GetString() ?? "";
                string admin = root.GetProperty("adminPassword").GetString() ?? "";
                PostToGameThread(() =>
                {
                    Notifications.Good(Tr.RoomCreated(code));
                    JoinRoomNow(code, url, password, admin);
                });
            }
            catch (Exception e)
            {
                ModLog.Error("Room creation failed", e);
                string error = e.GetType().Name;
                PostToGameThread(() => Notifications.Warn(Tr.RoomCreateFailed(error)));
            }
        });
    }

    /// <summary>Joins a room by its code, with its password if it has one.</summary>
    public void JoinRoom(string code, string? password)
    {
        code = code.Trim().ToUpperInvariant();
        JoinRoomNow(code, ModConfig.Current.RoomSocketUri(code).ToString(), password, null);
    }

    /// <summary>(Re)connects to the room the player last joined.</summary>
    public void Connect()
    {
        if (roomUrl == null)
        {
            Notifications.Info(Tr.NoRoomChosen);
            return;
        }
        ConnectNow();
    }

    private void JoinRoomNow(string code, string url, string? password, string? adminPassword)
    {
        RoomCode = code;
        roomUrl = url;
        roomPassword = string.IsNullOrEmpty(password) ? null : password;
        hostAdminPassword = adminPassword; // the creator of a room is its admin
        ConnectNow();
    }

    private void ConnectNow()
    {
        userDisconnected = false;
        if (server != null)
        {
            DropConnection("reconnexion");
        }
        lastConnectAttempt = double.NegativeInfinity;
        lastDisconnectReason = "";
        EnsureConnected();
    }

    public void Disconnect()
    {
        userDisconnected = true;
        if (server != null)
        {
            DropConnection("déconnexion manuelle");
            Notifications.Info(Tr.DisconnectedByUser);
        }
    }

    private void DropConnection(string why)
    {
        WsConnection? peer = server;
        server = null; // l'événement de déconnexion de ce pair sera ignoré
        peer?.Disconnect();
        ModLog.Info($"Connexion fermée ({why})");
        ResetSessionState();
    }

    private void ResetSessionState()
    {
        LocalId = null;
        Players.Clear();
        Round.Reset();
        loadedRoundId = 0;
        notifiedRound = default;
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
        // Map du Workshop : "c:M<id>" (dernière version) ou "c:M<id>.<version>" (version exacte).
        if (Msg.WorkshopId(key) is ulong workshopId)
        {
            try
            {
                GameAccess.StartWorkshopMap(workshopId, key.Contains('.') ? key[2..] : null);
            }
            catch (Exception e)
            {
                ModLog.Error($"Impossible de charger la map {key}", e);
                Notifications.Warn(Tr.MapLoadFailed(key));
            }
            return;
        }
        // Niveau officiel : "o:<id>" ou "o:<id>:<version>".
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
                ModLog.Error($"Impossible de charger la map {key}", e);
                Notifications.Warn(Tr.MapLoadFailed(key));
            }
        }
        else
        {
            ModLog.Warn($"Map de round non supportée : {key}");
            Notifications.Warn(Tr.MapNotSupported(key));
        }
    }

    // ---------- Temps ----------

    private (int RoundId, SE.LevelTimer Timer, SE.TimeDiffOnCheckpoints? Display, bool SendToServer)? pendingFinish;

    /// <summary>Arrivée franchie : le temps sera lu (et envoyé si besoin) à la fin du tick, chrono alors définitif.</summary>
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
            return; // hors round : affiché par les intermédiaires uniquement
        }
        ModLog.Info($"Arrivée en {Msg.FormatTime(ms)} (round {finish.RoundId}) → envoi au serveur");
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

    // ---------- Connexion ----------

    /// <summary>À appeler régulièrement depuis le thread du jeu : (re)connecte si nécessaire.</summary>
    public void EnsureConnected()
    {
        while (gameThreadQueue.TryDequeue(out Action? queued))
        {
            queued();
        }
        if (!ModConfig.Current.AutoConnect && lastConnectAttempt == double.NegativeInfinity && !userDisconnected)
        {
            // AutoConnect = false : on attend que le joueur se connecte depuis le menu.
            userDisconnected = true;
        }
        if (userDisconnected || server != null || NetClock.Now - lastConnectAttempt < ReconnectDelaySeconds)
        {
            return;
        }
        lastConnectAttempt = NetClock.Now;

        try
        {
            if (roomUrl == null)
            {
                return; // no room chosen yet: the menu says which one to join
            }
            if (!processExitHooked)
            {
                processExitHooked = true;
                // Fermeture du jeu : on prévient le serveur au lieu de le laisser attendre le timeout.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => server?.Disconnect("client closed");
            }

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
            serverAddress = RoomCode ?? roomUrl;
            ModLog.Info($"Connexion à {serverAddress} en tant que « {playerName} »…");
            var headers = new Dictionary<string, string> { ["x-sbm-version"] = ProtocolInfo.ConnectionKey };
            if (roomPassword != null)
            {
                headers["x-room-password"] = roomPassword;
            }
            var connection = new WsConnection(new Uri(roomUrl), headers, OnPeerConnected, OnNetworkReceive, OnPeerDisconnected);
            server = connection;
            connection.Start();
        }
        catch (Exception e)
        {
            ModLog.Error("Échec de la tentative de connexion", e);
            NotifyDisconnect(Tr.CannotConnect(ModConfig.Current.Server, e.GetType().Name));
            server = null;
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
            return; // une scène plus récente a déjà déclaré sa map
        }
        MapOwner = key == "" ? null : owner;
        if (key == mapKey)
        {
            return;
        }
        mapKey = key;
        ModLog.Info($"Map courante : {(key == "" ? "(menu)" : key)}");
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
            Send(w => Msg.State(w, null, s), DeliveryMethod.Unreliable);
        }
    }

    /// <summary>Queues an action for the game thread's next tick. Used for results of web requests.</summary>
    private void PostToGameThread(Action action) => gameThreadQueue.Enqueue(action);

    private void Send(Func<NetDataWriter, NetDataWriter> build, DeliveryMethod method)
    {
        lock (sendGate)
        {
            server?.Send(build(writer), method);
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
            ModLog.Warn($"Pseudo Steam indisponible ({e.GetType().Name}), pseudo par défaut utilisé");
            return "Player";
        }
    }

    // ---------- Événements LiteNetLib (thread réseau) ----------

    private void OnPeerConnected(WsConnection peer)
    {
        ModLog.Info("Connecté au serveur");
        server = peer; // au cas où l'événement arrive avant le retour de Connect()
        int skin = localSkin;
        Send(w => Msg.Hello(w, playerName, skin), DeliveryMethod.ReliableOrdered);
    }

    private void OnPeerDisconnected(WsConnection peer, string closeReason)
    {
        if (!ReferenceEquals(peer, server))
        {
            return; // ancienne connexion fermée volontairement (Connect/Disconnect depuis le menu)
        }
        // Code envoyé par le serveur (refus, kick…), ou raison de l'échec de la connexion.
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
            // Réessayer ne servirait à rien : on attend une action du joueur (menu F8).
            userDisconnected = true;
            ModLog.Warn($"Déconnecté du serveur : {reason}");
            NotifyDisconnect(reason);
        }
        else
        {
            ModLog.Warn($"Déconnecté du serveur : {reason}. Nouvelle tentative dans {ReconnectDelaySeconds} s.");
            NotifyDisconnect(Tr.RetryingEvery(reason, (int)ReconnectDelaySeconds));
        }
        server = null;
        ResetSessionState();
    }

    /// <summary>A refused handshake reports its HTTP status. This maps it to one of the codes above.</summary>
    private static string NormalizeCloseReason(string reason) => reason switch
    {
        "HTTP_STATUS:401" => ServerReasons.BadPassword,
        "HTTP_STATUS:404" => ServerReasons.RoomNotFound,
        "HTTP_STATUS:429" => ServerReasons.RateLimited,
        "HTTP_STATUS:503" => ServerReasons.Full,
        _ when reason.StartsWith("HTTP_STATUS:", StringComparison.Ordinal) => ServerReasons.Unreachable,
        _ => reason,
    };

    /// <summary>Le serveur envoie des codes (SERVER_FULL, VERSION_MISMATCH:4, KICKED…) que le client traduit.</summary>
    private static string TranslateReasonCode(string code) => code switch
    {
        ServerReasons.Full => Tr.ServerFull,
        ServerReasons.BadPassword => Tr.BadPassword,
        ServerReasons.Kicked => Tr.Kicked,
        ServerReasons.RoomNotFound => Tr.RoomNotFound,
        ServerReasons.RateLimited => Tr.RateLimited,
        _ when code.StartsWith(ServerReasons.VersionMismatch + ":") => Tr.VersionMismatch(code[(ServerReasons.VersionMismatch.Length + 1)..]),
        _ => code, // serveur plus ancien : message en clair
    };

    private void OnNetworkReceive(WsConnection peer, byte[] data)
    {
        if (!ReferenceEquals(peer, server))
        {
            return;
        }
        var reader = new NetDataReader(data);
        try
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Welcome:
                {
                    LocalId = reader.GetInt();
                    if (hostAdminPassword != null)
                    {
                        string pw = hostAdminPassword;
                        Send(w => Msg.AdminLogin(w, pw), DeliveryMethod.ReliableOrdered); // the host is the admin of its game
                    }
                    int count = reader.GetInt();
                    Players.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        AddPlayer(Msg.GetPlayerInfo(reader), notify: false);
                    }
                    ModLog.Info($"Bienvenue ! Notre id : #{LocalId}, {count} autre(s) joueur(s)");
                    // Le serveur ne connaît pas encore notre map (connexion en cours de partie, ou reconnexion).
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
                        ModLog.Info($"{p.Name} (#{id}) est parti");
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
                    Round.SetInfo(info, peer.Ping / 1000.0);
                    ModLog.Info($"Round {info.RoundId} : {info.Phase} sur {(info.MapKey == "" ? "-" : info.MapKey)}, {info.RemainingSeconds:0} s");
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
                    string code = reader.GetString(64);
                    string arg = reader.GetString(128);
                    Notifications.Show(Tr.ServerEvent(code, arg), new Microsoft.Xna.Framework.Color(255, 210, 90));
                    break;
                }
                case MessageType.Chat:
                {
                    reader.GetInt(); // id de l'émetteur (réservé : mute, couleurs…)
                    string name = reader.GetString(ProtocolInfo.MaxNameLength * 4);
                    string text = reader.GetString(ProtocolInfo.MaxChatLength * 4);
                    ChatLog.AddPlayer(name, text);
                    break;
                }
                case MessageType.CommandReply:
                {
                    string code = reader.GetString(64);
                    string arg = reader.GetString(256);
                    Notifications.Show(Tr.CommandReply(code, arg), new Microsoft.Xna.Framework.Color(255, 170, 80));
                    break;
                }
                case MessageType.Settings:
                    ServerSettings = Msg.GetSettings(reader).Settings;
                    break;
                case MessageType.MatchStandings:
                    Round.Standings = Msg.GetStandings(reader);
                    break;
                case MessageType.State:
                {
                    int id = reader.GetInt();
                    PlayerState s = Msg.GetState(reader);
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.AddState(s);
                    }
                    break;
                }
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Paquet serveur invalide", e);
        }
    }

    private void AddPlayer(PlayerInfo info, bool notify)
    {
        Players[info.Id] = new RemotePlayer(info);
        ModLog.Info($"{info.Name} (#{info.Id}) a rejoint{(info.MapKey == "" ? "" : $", sur {info.MapKey}")}");
        if (notify)
        {
            Notifications.Info(Tr.PlayerJoined(info.Name));
        }
    }

}
