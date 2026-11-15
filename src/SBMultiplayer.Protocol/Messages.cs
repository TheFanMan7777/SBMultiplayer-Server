using LiteNetLib.Utils;

namespace SBMultiplayer.Protocol;

public static class ProtocolInfo
{
    /// <summary>
    /// Changes only on a breaking change of the message format (that would be a 2.0). Compatible additions keep it:
    /// new fields appended at the end of a message, new message types, new capabilities (see docs/protocol.md).
    /// </summary>
    public const int Version = 13;

    /// <summary>This build's mod version ("0.19.0"), exchanged at connection for display and logs.</summary>
    public static string ModVersion { get; } = FormatVersion(typeof(ProtocolInfo).Assembly.GetName().Version);

    private static string FormatVersion(System.Version? v) => v == null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>What this build supports, sent by the client in Hello and by the server in Welcome.</summary>
    public static Capabilities OwnCapabilities => new(ModVersion, GameModes.Known, new[] { Features.DnfFillsQuota });

    /// <summary>Optional features (see docs/protocol.md).</summary>
    public static class Features
    {
        /// <summary>The server knows <see cref="MatchSettings.DnfFillsQuota"/>.</summary>
        public const string DnfFillsQuota = "dnf-fills-quota";
    }

    public const int DefaultPort = 24800;

    public const int MaxNameLength = 24;

    public const int MaxChatLength = 200;

    /// <summary>LiteNetLib connection key: a client of another version is rejected during the handshake.</summary>
    public static string ConnectionKey => $"SBMultiplayer/{Version}";

    /// <summary>Connection request data: version key, then the server password ("" if none).</summary>
    public static NetDataWriter ConnectionData(string password)
    {
        var w = new NetDataWriter();
        w.Put(ConnectionKey);
        w.Put(password);
        return w;
    }
}

/// <summary>
/// What a client or a server supports. A newer peer hides or skips what the other side lacks:
/// game modes it can't run, and optional features named by a string (none yet in protocol 13).
/// </summary>
public sealed record Capabilities(string Version, IReadOnlyList<GameMode> Modes, IReadOnlyList<string> Features)
{
    /// <summary>Peer that didn't send its capabilities.</summary>
    public static Capabilities Unknown { get; } = new("?", GameModes.Known, Array.Empty<string>());

    public bool Has(string feature) => Features.Contains(feature);

    public bool Supports(GameMode mode) => Modes.Contains(mode);
}

/// <summary>Connection rejection codes sent by the server, translated by the client.</summary>
public static class ServerReasons
{
    public const string Full = "SERVER_FULL";

    /// <summary>Followed by ":&lt;server protocol version&gt;".</summary>
    public const string VersionMismatch = "VERSION_MISMATCH";

    public const string BadPassword = "BAD_PASSWORD";

    public const string Kicked = "KICKED";

    /// <summary>Web rooms: no room has that code (it was closed or never existed).</summary>
    public const string RoomNotFound = "ROOM_NOT_FOUND";

    /// <summary>Web rooms: too many attempts from this address; wait and try again.</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Web rooms: the website could not reach the room's game server.</summary>
    public const string Unreachable = "UNREACHABLE";
}

/// <summary>
/// Events announced by the server to all players, translated by the client.
/// Argument: admin name, player name or number of seconds depending on the event.
/// </summary>
public static class ServerEvents
{
    public const string Paused = "PAUSED";
    public const string Resumed = "RESUMED";
    public const string Skipped = "SKIPPED";
    public const string Restarted = "RESTARTED";
    public const string Extended = "EXTENDED";
    public const string MapChosen = "MAP_CHOSEN";
    public const string PlayerKicked = "PLAYER_KICKED";
    public const string MatchReset = "MATCH_RESET";
    public const string AdminLoggedIn = "ADMIN_LOGGED_IN";
    public const string SettingsChanged = "SETTINGS_CHANGED";
    /// <summary>Argument: name of the Cup winner.</summary>
    public const string CupWon = "CUP_WON";
    /// <summary>Argument: name of the Elimination winner.</summary>
    public const string EliminationWon = "ELIMINATION_WON";
    /// <summary>Argument: names of the eliminated players, comma-separated.</summary>
    public const string Eliminated = "ELIMINATED";
    /// <summary>Argument: name of the new finalist (Cup).</summary>
    public const string Finalist = "FINALIST";
    /// <summary>Argument: name of the match winner (Rounds: end of the rotation).</summary>
    public const string MatchWon = "MATCH_WON";
    /// <summary>Hide and Seek: argument = names of this round's hunters, comma-separated.</summary>
    public const string HunterChosen = "HUNTER_CHOSEN";
    /// <summary>Hide and Seek: argument = "hunter name\nmouse name".</summary>
    public const string MouseFound = "MOUSE_FOUND";
    /// <summary>Hide and Seek: argument = name of the mouse that left its hiding place (counts as found).</summary>
    public const string MouseForfeit = "MOUSE_FORFEIT";
    /// <summary>Hide and Seek: every mouse was found. Argument = names of the hunters.</summary>
    public const string HuntersWin = "HUNTERS_WIN";
    /// <summary>Hide and Seek: time is up. Argument = names of the surviving mice, comma-separated.</summary>
    public const string MiceSurvived = "MICE_SURVIVED";
}

/// <summary>Replies to chat commands, sent to the issuer only and translated by the client. Argument depends on the code.</summary>
public static class CommandResults
{
    /// <summary>Argument: "admin" or "player" (which commands to list).</summary>
    public const string Help = "HELP";
    public const string Done = "DONE";
    public const string NotAdmin = "NOT_ADMIN";
    public const string NoRound = "NO_ROUND";
    /// <summary>Argument: expected usage, e.g. "/map <number>".</summary>
    public const string Usage = "USAGE";
    /// <summary>Argument: the searched name or id.</summary>
    public const string NoPlayer = "NO_PLAYER";
    /// <summary>Argument: number of maps in the rotation.</summary>
    public const string BadMap = "BAD_MAP";
    /// <summary>Argument: the unknown command.</summary>
    public const string Unknown = "UNKNOWN";
    public const string TooFast = "TOO_FAST";
    /// <summary>Argument: what is wrong.</summary>
    public const string InvalidSettings = "INVALID_SETTINGS";
    public const string NoConfigFile = "NO_CONFIG_FILE";
}

public enum AdminCommand : byte
{
    NextPhase = 1,
    RestartRound = 2,
    TogglePause = 3,
    /// <summary>Argument: seconds to add to the round.</summary>
    Extend = 4,
    /// <summary>Argument: index of the map in the rotation.</summary>
    GotoMap = 5,
    /// <summary>Argument: player id.</summary>
    Kick = 6,
    ResetMatch = 7,
    /// <summary>Hide and Seek: argument = id of the player who will be the next hunter (-1 = random).</summary>
    SetHunter = 8,
}

public enum MessageType : byte
{
    /// <summary>C→S, reliable. First message after connecting.</summary>
    Hello = 1,

    /// <summary>S→C, reliable. Reply to Hello: our id and the players already there.</summary>
    Welcome = 2,

    /// <summary>S→C, reliable.</summary>
    PlayerJoined = 3,

    /// <summary>S→C, reliable.</summary>
    PlayerLeft = 4,

    /// <summary>C→S (without id) and S→C (with id), reliable. Map the player is on ("" = none).</summary>
    MapChanged = 5,

    /// <summary>C→S, unreliable. Board position (compact, see <see cref="Msg.PutCompact"/>).</summary>
    State = 6,

    /// <summary>S→C, reliable. Current round: imposed map, phase, remaining time.</summary>
    RoundInfo = 7,

    /// <summary>C→S, reliable. Finish line crossed during a round.</summary>
    Finish = 8,

    /// <summary>S→C, reliable. Round standings (everyone's best time).</summary>
    Leaderboard = 9,

    /// <summary>C→S (without id) and S→C (with id), reliable. Player's board skin (game id).</summary>
    SkinChanged = 10,

    /// <summary>C→S, reliable. Admin password.</summary>
    AdminLogin = 11,

    /// <summary>S→C, reliable. Admin login result, and the map rotation (for the admin panel).</summary>
    AdminStatus = 12,

    /// <summary>C→S, reliable. Admin command (<see cref="Protocol.AdminCommand"/> + argument).</summary>
    AdminCommand = 13,

    /// <summary>S→C, reliable. Event to announce (<see cref="ServerEvents"/> + argument).</summary>
    ServerEvent = 14,

    /// <summary>S→C, reliable. Match standings (points accumulated over the rounds).</summary>
    MatchStandings = 15,

    /// <summary>C→S (text) and S→C (id, name, text), reliable. Chat message; "/..." = command.</summary>
    Chat = 16,

    /// <summary>S→C, reliable. Reply to a command (<see cref="CommandResults"/> + argument).</summary>
    CommandReply = 17,

    /// <summary>C→S, reliable. Ready on the start line (synchronized modes).</summary>
    Ready = 18,

    /// <summary>C→S, reliable. Gives up the round (synchronized modes).</summary>
    GiveUp = 19,

    /// <summary>S→C (admins) and C→S (admins), reliable. Match settings as JSON.</summary>
    Settings = 20,

    /// <summary>C→S (without id) and S→C (with id), reliable. Switch to spectator or back into the game.</summary>
    Spectate = 21,

    /// <summary>C→S, reliable. Hide and Seek: the hunter found a mouse (round id, mouse id). A mouse sending its own id forfeits.</summary>
    Tag = 22,

    /// <summary>S→C, reliable. Hide and Seek: hunters, mice and found mice of the current round.</summary>
    HideSeek = 23,

    /// <summary>S→C, unreliable. Board positions of the players on the recipient's map, grouped in one packet.</summary>
    States = 24,
}

public enum RoundPhase : byte
{
    /// <summary>No rounds: everyone rides wherever they want (server without a map list).</summary>
    Free = 0,

    /// <summary>Round in progress on the imposed map.</summary>
    Playing = 1,

    /// <summary>Round over: results shown before the next map.</summary>
    Results = 2,

    /// <summary>Synchronized-start modes: map loading, waiting for every player to be ready.</summary>
    Preparing = 3,
}

public enum GameMode : byte
{
    /// <summary>Unlimited retries during the round time, best time counts.</summary>
    TimeAttack = 0,

    /// <summary>Synchronized start, a single attempt per round, points by race time.</summary>
    Rounds = 1,

    /// <summary>Like Rounds; reaching the points limit makes you a finalist, and a finalist who wins a round wins the Cup.</summary>
    Cup = 2,

    /// <summary>Synchronized start; the last players of each round are eliminated until one winner remains.</summary>
    Elimination = 3,

    /// <summary>No rounds: everyone rides wherever they want (even with a map list).</summary>
    Free = 4,

    /// <summary>Hunters look for the hidden mice; the mice are frozen once the hiding time is over.</summary>
    HideAndSeek = 5,
}

public static class GameModes
{
    /// <summary>Modes this build knows. A newer server can run a mode missing here: <see cref="IsKnown"/>.</summary>
    public static IReadOnlyList<GameMode> Known { get; } = new[]
    {
        GameMode.Free, GameMode.TimeAttack, GameMode.Rounds, GameMode.Cup, GameMode.Elimination, GameMode.HideAndSeek,
    };

    public static bool IsKnown(this GameMode mode) => Known.Contains(mode);

    /// <summary>Synchronized-start modes (one attempt per round).</summary>
    public static bool IsSynchronized(this GameMode mode) => mode is GameMode.Rounds or GameMode.Cup or GameMode.Elimination or GameMode.HideAndSeek;
}

public enum StandingStatus : byte
{
    Normal = 0,
    /// <summary>Cup: reached the points limit.</summary>
    Finalist = 1,
    /// <summary>Elimination: eliminated from the match.</summary>
    Eliminated = 2,
    /// <summary>Match winner (Cup or Elimination).</summary>
    Winner = 3,
}

/// <summary>
/// Match settings an admin can change in game. Exchanged as JSON (<see cref="Msg.Settings"/>):
/// adding a field doesn't break the format.
/// </summary>
public sealed class MatchSettings
{
    /// <summary>A mode this build doesn't know (sent by a newer server) is kept as its number.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(TolerantGameModeConverter))]
    public GameMode Mode { get; set; } = GameMode.TimeAttack;
    public List<string> Maps { get; set; } = new();
    public double RoundSeconds { get; set; } = 300;
    public double ResultsSeconds { get; set; } = 15;
    public int RoundsPerMap { get; set; } = 3;
    public double FinishTimeoutSeconds { get; set; } = 15;
    public double PrepareTimeoutSeconds { get; set; } = 20;
    public double CountdownSeconds { get; set; } = 3;
    public List<int> PointsTable { get; set; } = new() { 10, 8, 6, 5, 4, 3, 2, 1 };
    public int CupPointsLimit { get; set; } = 100;
    public int EliminatedPerRound { get; set; } = 1;
    public double MatchEndSeconds { get; set; } = 20;
    /// <summary>Hide and Seek: time the mice get to hide while the hunter is frozen.</summary>
    public double HideSeconds { get; set; } = 30;
    /// <summary>Hide and Seek: found mice become hunters (otherwise they watch).</summary>
    public bool Infection { get; set; }
    /// <summary>
    /// Elimination: players who didn't finish fill the quota of eliminated players, first to give up first
    /// (otherwise every one of them is eliminated).
    /// </summary>
    public bool DnfFillsQuota { get; set; }

    /// <summary>Serialization metadata generated at compile time (no reflection needed).</summary>
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<MatchSettings> JsonInfo => ProtocolJson.Default.MatchSettings;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(MatchSettings))]
internal partial class ProtocolJson : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>Game mode as its name; an unknown name or number doesn't fail the whole message.</summary>
public sealed class TolerantGameModeConverter : System.Text.Json.Serialization.JsonConverter<GameMode>
{
    /// <summary>Stands for a mode name this build doesn't know.</summary>
    public const GameMode UnknownMode = (GameMode)byte.MaxValue;

    public override GameMode Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
        {
            return reader.TryGetByte(out byte b) ? (GameMode)b : UnknownMode;
        }
        string? name = reader.GetString();
        return Enum.TryParse(name, ignoreCase: true, out GameMode mode) && Enum.IsDefined(mode) ? mode : UnknownMode;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, GameMode value, System.Text.Json.JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value))
        {
            writer.WriteStringValue(value.ToString());
        }
        else
        {
            writer.WriteNumberValue((byte)value);
        }
    }
}

/// <summary>
/// Settings sent by an admin are a partial JSON object: only what they changed. The server merges it into its current
/// settings, so an older admin client never resets settings it doesn't know, and two admins don't undo each other.
/// </summary>
public static class SettingsPatch
{
    /// <summary>Properties of <paramref name="edited"/> that differ from <paramref name="baseline"/>, as a JSON object.</summary>
    public static string Diff(MatchSettings edited, MatchSettings baseline)
    {
        var e = (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(edited, MatchSettings.JsonInfo)!;
        var b = (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(baseline, MatchSettings.JsonInfo)!;
        var diff = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in e)
        {
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(value, b[key]))
            {
                diff[key] = value?.DeepClone();
            }
        }
        return diff.ToJsonString();
    }

    /// <summary>Applies a (partial) JSON object to <paramref name="current"/>; unknown properties are ignored.</summary>
    public static MatchSettings Merge(MatchSettings current, string patchJson)
    {
        var merged = (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(current, MatchSettings.JsonInfo)!;
        if (System.Text.Json.Nodes.JsonNode.Parse(patchJson) is not System.Text.Json.Nodes.JsonObject patch)
        {
            throw new System.Text.Json.JsonException("settings: a JSON object was expected");
        }
        foreach (var (key, value) in patch)
        {
            if (merged.ContainsKey(key))
            {
                merged[key] = value?.DeepClone();
            }
        }
        return System.Text.Json.JsonSerializer.Deserialize(merged, MatchSettings.JsonInfo) ?? current;
    }
}

/// <param name="MapKey">Imposed map ("o:&lt;id&gt;" or "o:&lt;id&gt;:&lt;version&gt;"), "" in the Free phase.</param>
/// <param name="RemainingSeconds">Time left in the phase, when sent.</param>
/// <param name="StartIn">Synchronized modes, Playing phase: seconds before the start when sent (≤ 0 = already started).</param>
/// <param name="RoundInMap">Round number on this map (from 1), and number of rounds per map.</param>
/// <param name="PointsLimit">Cup: points limit to become a finalist.</param>
/// <param name="MatchEnded">Results phase: the game is over (end of the round in Time Attack, end of the rotation in Rounds,
/// a winner in Cup / Elimination): clients show the end-of-game screen.</param>
/// <param name="HideSeconds">Hide and Seek: hiding time after the start; the hunt starts at StartIn + HideSeconds.</param>
/// <param name="Infection">Hide and Seek: found mice become hunters.</param>
public readonly record struct RoundInfo(int RoundId, string MapKey, RoundPhase Phase, float RemainingSeconds, int MapIndex, int MapCount, bool Paused,
    GameMode Mode = GameMode.TimeAttack, float StartIn = 0, int RoundInMap = 1, int RoundsPerMap = 1, int ReadyCount = 0, int PlayerCount = 0,
    int PointsLimit = 0, bool MatchEnded = false, float HideSeconds = 0, bool Infection = false);

/// <summary>Hide and Seek roles of a round (player ids). Found mice are also listed in <paramref name="Mice"/>.</summary>
public readonly record struct HideSeekInfo(int RoundId, int[] Hunters, int[] Mice, int[] Found)
{
    public static HideSeekInfo None => new(-1, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
}

/// <summary>Hide and Seek rules shared by the server and the clients.</summary>
public static class HideSeekRules
{
    /// <summary>Maximum distance between the hunter's board and a mouse to find it, in game units (the game's \"cm\": 1 unit = 1 board length, the player is about as tall).</summary>
    public const float TagRange = 15;

    /// <summary>
    /// Aim tolerance around the mouse: a few centimeters, more when it's further from the camera,
    /// so a small board can still be clicked at full speed.
    /// </summary>
    public const float AimTolerance = 2.5f;

    /// <summary>
    /// Server check on the last known positions, with a margin for latency: speeds are a few tens of units
    /// per second (1 unit = 1 board length), so a hunter moves at most a few units between its last state and the click.
    /// </summary>
    public const float ServerTagRange = TagRange + 25;

    /// <summary>A mouse still in the air this long after the hiding time is frozen anyway.</summary>
    public const double MaxAirSecondsBeforeFreeze = 5;
}

public readonly record struct LeaderboardEntry(int PlayerId, string Name, uint BestMs);

/// <param name="RoundsPlayed">Number of rounds in which the player set a time.</param>
public readonly record struct StandingEntry(string Name, int Points, int RoundsPlayed, StandingStatus Status = StandingStatus.Normal);

public readonly record struct Vec3(float X, float Y, float Z);

/// <param name="Time">Sender's clock, in seconds. Only the difference between two states matters.</param>
/// <param name="Segment">Incremented on every teleport (restart, respawn): no interpolation between two segments.</param>
/// <param name="CamPosition">Player's camera (position and pitch/yaw/roll): spectators see what they see.</param>
public readonly record struct PlayerState(double Time, Vec3 Position, Vec3 Orientation, ushort Segment, Vec3 CamPosition = default, Vec3 CamOrientation = default);

public readonly record struct PlayerInfo(int Id, string Name, string MapKey, int Skin, bool IsSpectator = false);

/// <summary>Message serialization. Every message starts with its <see cref="MessageType"/>.</summary>
public static class Msg
{
    public static NetDataWriter Hello(NetDataWriter w, string name, int skin, Capabilities capabilities)
    {
        w.Reset();
        w.Put((byte)MessageType.Hello);
        w.Put(name);
        w.Put(skin);
        Put(w, capabilities);
        return w;
    }

    /// <summary>Reads a Hello (after its type byte).</summary>
    public static (string Name, int Skin, Capabilities Capabilities) GetHello(NetDataReader r) =>
        (r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetInt(), GetCapabilities(r));

    public static NetDataWriter SkinChanged(NetDataWriter w, int? id, int skin)
    {
        w.Reset();
        w.Put((byte)MessageType.SkinChanged);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        w.Put(skin);
        return w;
    }

    public static NetDataWriter Welcome(NetDataWriter w, int yourId, IReadOnlyCollection<PlayerInfo> players, Capabilities serverCapabilities)
    {
        w.Reset();
        w.Put((byte)MessageType.Welcome);
        w.Put(yourId);
        w.Put(players.Count);
        foreach (PlayerInfo p in players)
        {
            int block = BeginBlock(w);
            Put(w, p);
            EndBlock(w, block);
        }
        Put(w, serverCapabilities);
        return w;
    }

    /// <summary>Reads a Welcome (after its type byte).</summary>
    public static (int YourId, List<PlayerInfo> Players, Capabilities ServerCapabilities) GetWelcome(NetDataReader r)
    {
        int yourId = r.GetInt();
        int count = Math.Clamp(r.GetInt(), 0, 1024);
        var players = new List<PlayerInfo>(count);
        for (int i = 0; i < count; i++)
        {
            players.Add(GetPlayerInfo(GetBlock(r)));
        }
        return (yourId, players, GetCapabilities(r));
    }

    public static NetDataWriter PlayerJoined(NetDataWriter w, PlayerInfo player)
    {
        w.Reset();
        w.Put((byte)MessageType.PlayerJoined);
        Put(w, player);
        return w;
    }

    public static NetDataWriter PlayerLeft(NetDataWriter w, int id)
    {
        w.Reset();
        w.Put((byte)MessageType.PlayerLeft);
        w.Put(id);
        return w;
    }

    public static NetDataWriter MapChanged(NetDataWriter w, int? id, string mapKey)
    {
        w.Reset();
        w.Put((byte)MessageType.MapChanged);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        w.Put(mapKey);
        return w;
    }

    /// <summary>C→S: our board state.</summary>
    public static NetDataWriter State(NetDataWriter w, in PlayerState s)
    {
        w.Reset();
        w.Put((byte)MessageType.State);
        PutCompact(w, s);
        return w;
    }

    /// <summary>
    /// Most states in one <see cref="MessageType.States"/> packet: each takes 46 bytes, so 20 stay under 1023 bytes,
    /// the largest unreliable packet LiteNetLib accepts before its MTU discovery (an oversized one would throw).
    /// </summary>
    public const int MaxStatesPerPacket = 20;

    /// <summary>S→C: several players' states (entries [start, start + count) of the list).</summary>
    public static NetDataWriter States(NetDataWriter w, IReadOnlyList<(int Id, PlayerState State)> entries, int start, int count)
    {
        w.Reset();
        w.Put((byte)MessageType.States);
        w.Put((byte)count);
        for (int i = start; i < start + count; i++)
        {
            w.Put(entries[i].Id);
            int block = BeginShortBlock(w);
            PutCompact(w, entries[i].State);
            EndShortBlock(w, block);
        }
        return w;
    }

    /// <summary>Reads a <see cref="MessageType.States"/> packet (after its type byte).</summary>
    public static List<(int Id, PlayerState State)> GetStates(NetDataReader r)
    {
        int count = r.GetByte();
        var list = new List<(int, PlayerState)>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.GetInt();
            list.Add((id, GetCompact(GetShortBlock(r))));
        }
        return list;
    }

    public static NetDataWriter Round(NetDataWriter w, in RoundInfo r)
    {
        w.Reset();
        w.Put((byte)MessageType.RoundInfo);
        w.Put(r.RoundId);
        w.Put(r.MapKey);
        w.Put((byte)r.Phase);
        w.Put(r.RemainingSeconds);
        w.Put(r.MapIndex);
        w.Put(r.MapCount);
        w.Put(r.Paused);
        w.Put((byte)r.Mode);
        w.Put(r.StartIn);
        w.Put(r.RoundInMap);
        w.Put(r.RoundsPerMap);
        w.Put(r.ReadyCount);
        w.Put(r.PlayerCount);
        w.Put(r.PointsLimit);
        w.Put(r.MatchEnded);
        w.Put(r.HideSeconds);
        w.Put(r.Infection);
        return w;
    }

    public static RoundInfo GetRound(NetDataReader r) =>
        new(r.GetInt(), r.GetString(256), (RoundPhase)r.GetByte(), r.GetFloat(), r.GetInt(), r.GetInt(), r.GetBool(),
            (GameMode)r.GetByte(), r.GetFloat(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetBool(), r.GetFloat(), r.GetBool());

    /// <summary>S→C (admins): the server's current settings, complete.</summary>
    public static NetDataWriter Settings(NetDataWriter w, MatchSettings settings, bool saveToFile) =>
        SettingsJson(w, System.Text.Json.JsonSerializer.Serialize(settings, MatchSettings.JsonInfo), saveToFile);

    /// <summary>C→S: settings to change, as a partial JSON object (<see cref="SettingsPatch.Diff"/>), and whether to save them to the config file.</summary>
    public static NetDataWriter SettingsJson(NetDataWriter w, string json, bool saveToFile)
    {
        w.Reset();
        w.Put((byte)MessageType.Settings);
        w.Put(json);
        w.Put(saveToFile);
        return w;
    }

    public static (MatchSettings Settings, bool SaveToFile) GetSettings(NetDataReader r)
    {
        var (json, save) = GetSettingsJson(r);
        return (System.Text.Json.JsonSerializer.Deserialize(json, MatchSettings.JsonInfo) ?? new MatchSettings(), save);
    }

    public static (string Json, bool SaveToFile) GetSettingsJson(NetDataReader r) => (r.GetString(16384), r.GetBool());

    /// <summary>C→S: the player is on the round's start line (synchronized modes).</summary>
    public static NetDataWriter Ready(NetDataWriter w, int roundId)
    {
        w.Reset();
        w.Put((byte)MessageType.Ready);
        w.Put(roundId);
        return w;
    }

    /// <summary>C→S: the player gives up the round (back to the start after the go).</summary>
    public static NetDataWriter GiveUp(NetDataWriter w, int roundId)
    {
        w.Reset();
        w.Put((byte)MessageType.GiveUp);
        w.Put(roundId);
        return w;
    }

    public static NetDataWriter AdminLogin(NetDataWriter w, string password)
    {
        w.Reset();
        w.Put((byte)MessageType.AdminLogin);
        w.Put(password);
        return w;
    }

    public static NetDataWriter AdminStatus(NetDataWriter w, bool isAdmin, IReadOnlyList<string> maps)
    {
        w.Reset();
        w.Put((byte)MessageType.AdminStatus);
        w.Put(isAdmin);
        w.Put(maps.Count);
        foreach (string m in maps)
        {
            w.Put(m);
        }
        return w;
    }

    public static (bool IsAdmin, List<string> Maps) GetAdminStatus(NetDataReader r)
    {
        bool isAdmin = r.GetBool();
        int count = Math.Clamp(r.GetInt(), 0, 1000);
        var maps = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            maps.Add(r.GetString(256));
        }
        return (isAdmin, maps);
    }

    public static NetDataWriter Admin(NetDataWriter w, AdminCommand command, int argument)
    {
        w.Reset();
        w.Put((byte)MessageType.AdminCommand);
        w.Put((byte)command);
        w.Put(argument);
        return w;
    }

    /// <param name="fallback">English text shown by clients that don't know <paramref name="code"/> (events added after protocol 13).</param>
    public static NetDataWriter Event(NetDataWriter w, string code, string argument, string fallback = "")
    {
        w.Reset();
        w.Put((byte)MessageType.ServerEvent);
        w.Put(code);
        w.Put(argument);
        w.Put(fallback);
        return w;
    }

    /// <summary>Reads a ServerEvent or a CommandReply (after its type byte): code, argument, fallback text.</summary>
    public static (string Code, string Argument, string Fallback) GetCodedText(NetDataReader r) =>
        (r.GetString(64), r.GetString(256), r.GetStringOr(512, ""));

    public static NetDataWriter ChatToServer(NetDataWriter w, string text)
    {
        w.Reset();
        w.Put((byte)MessageType.Chat);
        w.Put(text);
        return w;
    }

    public static NetDataWriter ChatToClient(NetDataWriter w, int senderId, string name, string text)
    {
        w.Reset();
        w.Put((byte)MessageType.Chat);
        w.Put(senderId);
        w.Put(name);
        w.Put(text);
        return w;
    }

    /// <param name="fallback">English text shown by clients that don't know <paramref name="code"/> (replies added after protocol 13).</param>
    public static NetDataWriter Reply(NetDataWriter w, string code, string argument, string fallback = "")
    {
        w.Reset();
        w.Put((byte)MessageType.CommandReply);
        w.Put(code);
        w.Put(argument);
        w.Put(fallback);
        return w;
    }

    /// <summary>Sanitizes a chat message: bounded length, no control or invisible format characters.</summary>
    public static string SanitizeChat(string? text)
    {
        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (System.Text.Rune rune in (text ?? "").EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            if (count++ == ProtocolInfo.MaxChatLength)
            {
                break;
            }
            sb.Append(rune.ToString());
        }
        return sb.ToString().Trim();
    }

    public static NetDataWriter Standings(NetDataWriter w, IReadOnlyCollection<StandingEntry> entries)
    {
        w.Reset();
        w.Put((byte)MessageType.MatchStandings);
        w.Put(entries.Count);
        foreach (StandingEntry e in entries)
        {
            int block = BeginBlock(w);
            w.Put(e.Name);
            w.Put(e.Points);
            w.Put(e.RoundsPlayed);
            w.Put((byte)e.Status);
            EndBlock(w, block);
        }
        return w;
    }

    public static List<StandingEntry> GetStandings(NetDataReader r)
    {
        int count = Math.Clamp(r.GetInt(), 0, 256);
        var list = new List<StandingEntry>(count);
        for (int i = 0; i < count; i++)
        {
            NetDataReader e = GetBlock(r);
            list.Add(new StandingEntry(e.GetString(ProtocolInfo.MaxNameLength * 4), e.GetInt(), e.GetInt(), (StandingStatus)e.GetByte()));
        }
        return list;
    }

    public static NetDataWriter Finish(NetDataWriter w, int roundId, uint timeMs)
    {
        w.Reset();
        w.Put((byte)MessageType.Finish);
        w.Put(roundId);
        w.Put(timeMs);
        return w;
    }

    public static NetDataWriter Leaderboard(NetDataWriter w, int roundId, IReadOnlyCollection<LeaderboardEntry> entries)
    {
        w.Reset();
        w.Put((byte)MessageType.Leaderboard);
        w.Put(roundId);
        w.Put(entries.Count);
        foreach (LeaderboardEntry e in entries)
        {
            int block = BeginBlock(w);
            w.Put(e.PlayerId);
            w.Put(e.Name);
            w.Put(e.BestMs);
            EndBlock(w, block);
        }
        return w;
    }

    public static (int RoundId, List<LeaderboardEntry> Entries) GetLeaderboard(NetDataReader r)
    {
        int roundId = r.GetInt();
        int count = Math.Clamp(r.GetInt(), 0, 256);
        var list = new List<LeaderboardEntry>(count);
        for (int i = 0; i < count; i++)
        {
            NetDataReader e = GetBlock(r);
            list.Add(new LeaderboardEntry(e.GetInt(), e.GetString(ProtocolInfo.MaxNameLength * 4), e.GetUInt()));
        }
        return (roundId, list);
    }

    /// <summary>
    /// True if a player's map matches the rotation entry:
    /// official "o:1:2001" for "o:1" or "o:1:2001", Workshop "c:M123.&lt;version&gt;" for "c:M123" or the full key.
    /// </summary>
    public static bool MapMatches(string playerMapKey, string roundMapKey) =>
        roundMapKey.Length > 0 && (playerMapKey == roundMapKey
            || playerMapKey.StartsWith(roundMapKey + ":", StringComparison.Ordinal)
            || playerMapKey.StartsWith(roundMapKey + ".", StringComparison.Ordinal));

    /// <summary>Map keys accepted in a rotation: official level or Workshop map.</summary>
    public static bool IsValidRotationKey(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key, @"^(o:\d+(:\d+)?|c:M[1-9]\d*(\.[0-9a-f]{64})?)$");

    /// <summary>Workshop id of a "c:M123" or "c:M123.&lt;version&gt;" key, otherwise null.</summary>
    public static ulong? WorkshopId(string key)
    {
        if (!key.StartsWith("c:M", StringComparison.Ordinal))
        {
            return null;
        }
        int end = key.IndexOf('.');
        string digits = end < 0 ? key[3..] : key[3..end];
        return ulong.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) ? id : null;
    }

    public static string FormatTime(uint ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";

    // Compact board state, 41 bytes instead of 63:
    // - time in milliseconds of the sender's clock (only differences matter; wraps after 49 days);
    // - position as floats (1 unit = 1 board length, maps are big: full precision kept);
    // - angles on 16 bits each (0.0055° steps; interpolation already goes the short way around);
    // - segment on 8 bits (only compared for equality);
    // - camera position relative to the board, in hundredths of a unit (the camera stays within a few units).
    private const double AngleScale = 65536 / (2 * Math.PI);
    private const float CameraOffsetScale = 100f;

    public static void PutCompact(NetDataWriter w, in PlayerState s)
    {
        w.Put((uint)((long)Math.Round(s.Time * 1000) & 0xFFFFFFFF));
        Put(w, s.Position);
        PutAngles(w, s.Orientation);
        w.Put((byte)s.Segment);
        PutOffset(w, s.CamPosition.X - s.Position.X);
        PutOffset(w, s.CamPosition.Y - s.Position.Y);
        PutOffset(w, s.CamPosition.Z - s.Position.Z);
        PutAngles(w, s.CamOrientation);
    }

    public static PlayerState GetCompact(NetDataReader r)
    {
        double time = r.GetUInt() / 1000.0;
        Vec3 position = GetVec3(r);
        Vec3 orientation = GetAngles(r);
        ushort segment = r.GetByte();
        var camera = new Vec3(position.X + r.GetShort() / CameraOffsetScale, position.Y + r.GetShort() / CameraOffsetScale,
            position.Z + r.GetShort() / CameraOffsetScale);
        return new PlayerState(time, position, orientation, segment, camera, GetAngles(r));
    }

    private static void PutAngles(NetDataWriter w, in Vec3 v)
    {
        w.Put(Angle(v.X));
        w.Put(Angle(v.Y));
        w.Put(Angle(v.Z));
    }

    private static ushort Angle(float radians) => (ushort)((long)Math.Round(radians * AngleScale) & 0xFFFF);

    private static Vec3 GetAngles(NetDataReader r) =>
        new((float)(r.GetUShort() / AngleScale), (float)(r.GetUShort() / AngleScale), (float)(r.GetUShort() / AngleScale));

    private static void PutOffset(NetDataWriter w, float offset) =>
        w.Put((short)Math.Clamp(Math.Round(offset * CameraOffsetScale), short.MinValue, short.MaxValue));

    /// <summary>C→S (without id) and S→C (with id), reliable: the player becomes a spectator, or comes back into the game.</summary>
    public static NetDataWriter Spectate(NetDataWriter w, int? id, bool spectating)
    {
        w.Reset();
        w.Put((byte)MessageType.Spectate);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        w.Put(spectating);
        return w;
    }

    /// <summary>C→S, Hide and Seek: the hunter found this mouse (or a mouse gives up with its own id).</summary>
    public static NetDataWriter Tag(NetDataWriter w, int roundId, int targetId)
    {
        w.Reset();
        w.Put((byte)MessageType.Tag);
        w.Put(roundId);
        w.Put(targetId);
        return w;
    }

    /// <summary>S→C, Hide and Seek: roles of the current round.</summary>
    public static NetDataWriter HideSeek(NetDataWriter w, in HideSeekInfo info)
    {
        w.Reset();
        w.Put((byte)MessageType.HideSeek);
        w.Put(info.RoundId);
        PutIds(w, info.Hunters);
        PutIds(w, info.Mice);
        PutIds(w, info.Found);
        return w;
    }

    public static HideSeekInfo GetHideSeek(NetDataReader r) => new(r.GetInt(), GetIds(r), GetIds(r), GetIds(r));

    private static void PutIds(NetDataWriter w, IReadOnlyCollection<int> ids)
    {
        w.Put(ids.Count);
        foreach (int id in ids)
        {
            w.Put(id);
        }
    }

    private static int[] GetIds(NetDataReader r)
    {
        int count = Math.Clamp(r.GetInt(), 0, 1024);
        var ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = r.GetInt();
        }
        return ids;
    }

    public static void Put(NetDataWriter w, in PlayerInfo p)
    {
        w.Put(p.Id);
        w.Put(p.Name);
        w.Put(p.MapKey);
        w.Put(p.Skin);
        w.Put(p.IsSpectator);
    }

    public static PlayerInfo GetPlayerInfo(NetDataReader r) =>
        new(r.GetInt(), r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetString(256), r.GetInt(), r.GetBool());

    public static void Put(NetDataWriter w, Capabilities c)
    {
        w.Put(c.Version);
        w.Put((byte)c.Modes.Count);
        foreach (GameMode mode in c.Modes)
        {
            w.Put((byte)mode);
        }
        w.Put((byte)c.Features.Count);
        foreach (string feature in c.Features)
        {
            w.Put(feature);
        }
    }

    public static Capabilities GetCapabilities(NetDataReader r)
    {
        if (r.AvailableBytes < 2)
        {
            return Capabilities.Unknown;
        }
        string version = r.GetString(32);
        int modeCount = r.GetByte();
        var modes = new GameMode[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            modes[i] = (GameMode)r.GetByte();
        }
        int featureCount = r.GetByte();
        var features = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
        {
            features[i] = r.GetString(64);
        }
        return new Capabilities(version, modes, features);
    }

    // ---------- Blocks ----------
    // A list element is written as a block (its length, then its fields), so a newer version can append fields
    // to it: an older reader reads the fields it knows and skips the rest of the block.

    /// <summary>Starts a block of up to 65535 bytes; returns the position to give to <see cref="EndBlock"/>.</summary>
    public static int BeginBlock(NetDataWriter w)
    {
        int position = w.Length;
        w.Put((ushort)0);
        return position;
    }

    public static void EndBlock(NetDataWriter w, int position)
    {
        int length = w.Length - position - 2;
        if (length > ushort.MaxValue)
        {
            throw new InvalidOperationException("block too long");
        }
        w.Data[position] = (byte)length;
        w.Data[position + 1] = (byte)(length >> 8);
    }

    /// <summary>Starts a block of up to 255 bytes (frequent small elements).</summary>
    public static int BeginShortBlock(NetDataWriter w)
    {
        int position = w.Length;
        w.Put((byte)0);
        return position;
    }

    public static void EndShortBlock(NetDataWriter w, int position)
    {
        int length = w.Length - position - 1;
        if (length > byte.MaxValue)
        {
            throw new InvalidOperationException("block too long");
        }
        w.Data[position] = (byte)length;
    }

    /// <summary>Reads a block: returns a reader over its content, <paramref name="r"/> moves past it.</summary>
    public static NetDataReader GetBlock(NetDataReader r) => SubReader(r, r.GetUShort());

    public static NetDataReader GetShortBlock(NetDataReader r) => SubReader(r, r.GetByte());

    private static NetDataReader SubReader(NetDataReader r, int length)
    {
        if (length > r.AvailableBytes)
        {
            throw new InvalidDataException("truncated block");
        }
        var block = new NetDataReader(r.RawData, r.Position, r.Position + length);
        r.SkipBytes(length);
        return block;
    }

    private static void Put(NetDataWriter w, in Vec3 v)
    {
        w.Put(v.X);
        w.Put(v.Y);
        w.Put(v.Z);
    }

    private static Vec3 GetVec3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());

    /// <summary>
    /// Sanitizes a received name: bounded length (in visible characters), and removal of control and
    /// invisible format characters (bidi controls, "tag" characters, zero-width spaces…)
    /// that could impersonate a name or break the display.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (System.Text.Rune rune in (name ?? "").EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }
            if (count++ == ProtocolInfo.MaxNameLength)
            {
                break;
            }
            sb.Append(rune.ToString());
        }
        string clean = sb.ToString().Trim();
        return clean.Length == 0 ? "Player" : clean;
    }
}

/// <summary>
/// Optional fields: a field appended to a message after protocol 13 is read with these, so a message from an
/// older peer (without the field) gets the default value. Reading past the end of a packet doesn't fail by itself
/// in LiteNetLib: it would return garbage, hence the explicit checks.
/// </summary>
public static class OptionalFields
{
    public static bool GetBoolOr(this NetDataReader r, bool fallback) => r.AvailableBytes >= 1 ? r.GetBool() : fallback;

    public static byte GetByteOr(this NetDataReader r, byte fallback) => r.AvailableBytes >= 1 ? r.GetByte() : fallback;

    public static int GetIntOr(this NetDataReader r, int fallback) => r.AvailableBytes >= 4 ? r.GetInt() : fallback;

    public static float GetFloatOr(this NetDataReader r, float fallback) => r.AvailableBytes >= 4 ? r.GetFloat() : fallback;

    public static string GetStringOr(this NetDataReader r, int maxLength, string fallback) =>
        r.AvailableBytes >= 2 ? r.GetString(maxLength) : fallback;
}
