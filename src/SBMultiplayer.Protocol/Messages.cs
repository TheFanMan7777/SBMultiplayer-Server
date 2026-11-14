using LiteNetLib.Utils;

namespace SBMultiplayer.Protocol;

public static class ProtocolInfo
{
    /// <summary>À incrémenter à chaque changement incompatible du format des messages.</summary>
    public const int Version = 10;

    public const int DefaultPort = 24800;

    public const int MaxNameLength = 24;

    public const int MaxChatLength = 200;

    /// <summary>Clé de connexion LiteNetLib : un client d'une autre version est refusé dès la poignée de main.</summary>
    public static string ConnectionKey => $"SBMultiplayer/{Version}";

    /// <summary>Données de la demande de connexion : clé de version puis mot de passe du serveur ("" si aucun).</summary>
    public static NetDataWriter ConnectionData(string password)
    {
        var w = new NetDataWriter();
        w.Put(ConnectionKey);
        w.Put(password);
        return w;
    }
}

/// <summary>Codes de refus de connexion envoyés par le serveur, traduits par le client.</summary>
public static class ServerReasons
{
    public const string Full = "SERVER_FULL";

    /// <summary>Suivi de ":&lt;version du protocole du serveur&gt;".</summary>
    public const string VersionMismatch = "VERSION_MISMATCH";

    public const string BadPassword = "BAD_PASSWORD";

    public const string Kicked = "KICKED";

    /// <summary>The website has no room with this code (it closed, or the site restarted).</summary>
    public const string RoomNotFound = "ROOM_NOT_FOUND";

    /// <summary>Too many attempts from this address: wait before trying again.</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Client side only: the website or the game server could not be reached at all.</summary>
    public const string Unreachable = "UNREACHABLE";
}

/// <summary>
/// Événements annoncés par le serveur à tous les joueurs, traduits par le client.
/// Argument : nom de l'admin, du joueur ou nombre de secondes selon l'événement.
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
    /// <summary>Argument : nom du vainqueur de la Cup.</summary>
    public const string CupWon = "CUP_WON";
    /// <summary>Argument : nom du vainqueur de l'élimination.</summary>
    public const string EliminationWon = "ELIMINATION_WON";
    /// <summary>Argument : noms des joueurs éliminés, séparés par des virgules.</summary>
    public const string Eliminated = "ELIMINATED";
    /// <summary>Argument : nom du nouveau finaliste (Cup).</summary>
    public const string Finalist = "FINALIST";
    /// <summary>Argument : nom du vainqueur du match (Rounds : fin de la rotation).</summary>
    public const string MatchWon = "MATCH_WON";
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
    /// <summary>Argument : secondes à ajouter au round.</summary>
    Extend = 4,
    /// <summary>Argument : index de la map dans la rotation.</summary>
    GotoMap = 5,
    /// <summary>Argument : id du joueur.</summary>
    Kick = 6,
    ResetMatch = 7,
}

public enum MessageType : byte
{
    /// <summary>C→S, fiable. Premier message après connexion.</summary>
    Hello = 1,

    /// <summary>S→C, fiable. Réponse à Hello : notre id et la liste des joueurs déjà présents.</summary>
    Welcome = 2,

    /// <summary>S→C, fiable.</summary>
    PlayerJoined = 3,

    /// <summary>S→C, fiable.</summary>
    PlayerLeft = 4,

    /// <summary>C→S (sans id) et S→C (avec id), fiable. Map sur laquelle se trouve le joueur ("" = aucune).</summary>
    MapChanged = 5,

    /// <summary>C→S (sans id) et S→C (avec id), non fiable. Position de la planche.</summary>
    State = 6,

    /// <summary>S→C, fiable. Round en cours : map imposée, phase, temps restant.</summary>
    RoundInfo = 7,

    /// <summary>C→S, fiable. Arrivée franchie pendant un round.</summary>
    Finish = 8,

    /// <summary>S→C, fiable. Classement du round (meilleur temps de chacun).</summary>
    Leaderboard = 9,

    /// <summary>C→S (sans id) et S→C (avec id), fiable. Skin de planche du joueur (id du jeu).</summary>
    SkinChanged = 10,

    /// <summary>C→S, fiable. Mot de passe admin.</summary>
    AdminLogin = 11,

    /// <summary>S→C, fiable. Résultat de la connexion admin, et rotation de maps (pour le panneau admin).</summary>
    AdminStatus = 12,

    /// <summary>C→S, fiable. Commande admin (<see cref="Protocol.AdminCommand"/> + argument).</summary>
    AdminCommand = 13,

    /// <summary>S→C, fiable. Événement à annoncer (<see cref="ServerEvents"/> + argument).</summary>
    ServerEvent = 14,

    /// <summary>S→C, fiable. Classement du match (points cumulés sur les rounds).</summary>
    MatchStandings = 15,

    /// <summary>C→S (texte) et S→C (id, pseudo, texte), fiable. Message de chat ; "/..." = commande.</summary>
    Chat = 16,

    /// <summary>S→C, fiable. Réponse à une commande (<see cref="CommandResults"/> + argument).</summary>
    CommandReply = 17,

    /// <summary>C→S, fiable. Prêt sur la ligne de départ (modes synchronisés).</summary>
    Ready = 18,

    /// <summary>C→S, fiable. Abandon de la manche (modes synchronisés).</summary>
    GiveUp = 19,

    /// <summary>S→C (admins) et C→S (admins), fiable. Réglages du match en JSON.</summary>
    Settings = 20,

    /// <summary>C→S (sans id) et S→C (avec id), fiable. Passage en spectateur ou retour dans la partie.</summary>
    Spectate = 21,
}

public enum RoundPhase : byte
{
    /// <summary>Pas de rounds : chacun roule où il veut (serveur sans liste de maps).</summary>
    Free = 0,

    /// <summary>Round en cours sur la map imposée.</summary>
    Playing = 1,

    /// <summary>Round terminé : affichage des résultats avant la map suivante.</summary>
    Results = 2,

    /// <summary>Modes à départ synchronisé : chargement de la map, en attente que tous les joueurs soient prêts.</summary>
    Preparing = 3,
}

public enum GameMode : byte
{
    /// <summary>Retries illimités pendant le temps du round, meilleur temps.</summary>
    TimeAttack = 0,

    /// <summary>Départ synchronisé, une seule tentative par manche, points selon le temps de course.</summary>
    Rounds = 1,

    /// <summary>Comme Rounds ; à la limite de points on devient finaliste, et un finaliste qui gagne une manche remporte la Cup.</summary>
    Cup = 2,

    /// <summary>Départ synchronisé ; les derniers de chaque manche sont éliminés jusqu'au vainqueur.</summary>
    Elimination = 3,

    /// <summary>Pas de rounds : chacun roule où il veut (même avec une liste de maps).</summary>
    Free = 4,
}

public static class GameModes
{
    /// <summary>Modes à départ synchronisé (une tentative par manche).</summary>
    public static bool IsSynchronized(this GameMode mode) => mode is GameMode.Rounds or GameMode.Cup or GameMode.Elimination;
}

public enum StandingStatus : byte
{
    Normal = 0,
    /// <summary>Cup : a atteint la limite de points.</summary>
    Finalist = 1,
    /// <summary>Élimination : éliminé du match.</summary>
    Eliminated = 2,
    /// <summary>Vainqueur du match (Cup ou Élimination).</summary>
    Winner = 3,
}

/// <summary>
/// Réglages de match modifiables en jeu par un admin. Échangés en JSON (<see cref="Msg.Settings"/>) :
/// ajouter un champ ne casse pas le format.
/// </summary>
public sealed class MatchSettings
{
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

    /// <summary>Serialization metadata generated at compile time (no reflection needed).</summary>
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<MatchSettings> JsonInfo => ProtocolJson.Default.MatchSettings;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(MatchSettings))]
internal partial class ProtocolJson : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <param name="MapKey">Map imposée ("o:&lt;id&gt;" ou "o:&lt;id&gt;:&lt;version&gt;"), "" en phase Free.</param>
/// <param name="RemainingSeconds">Temps restant dans la phase, au moment de l'envoi.</param>
/// <param name="StartIn">Modes synchronisés, phase Playing : secondes avant le départ au moment de l'envoi (≤ 0 = déjà parti).</param>
/// <param name="RoundInMap">Numéro de la manche sur cette map (à partir de 1), et nombre de manches par map.</param>
/// <param name="PointsLimit">Cup : limite de points pour devenir finaliste.</param>
/// <param name="MatchEnded">Phase Results : la partie est terminée (fin du round en Time Attack, fin de la rotation en Rounds,
/// vainqueur en Cup / Élimination) : les clients affichent l'écran de fin de partie.</param>
public readonly record struct RoundInfo(int RoundId, string MapKey, RoundPhase Phase, float RemainingSeconds, int MapIndex, int MapCount, bool Paused,
    GameMode Mode = GameMode.TimeAttack, float StartIn = 0, int RoundInMap = 1, int RoundsPerMap = 1, int ReadyCount = 0, int PlayerCount = 0,
    int PointsLimit = 0, bool MatchEnded = false);

public readonly record struct LeaderboardEntry(int PlayerId, string Name, uint BestMs);

/// <param name="RoundsPlayed">Nombre de rounds où le joueur a réalisé un temps.</param>
public readonly record struct StandingEntry(string Name, int Points, int RoundsPlayed, StandingStatus Status = StandingStatus.Normal);

public readonly record struct Vec3(float X, float Y, float Z);

/// <param name="Time">Horloge de l'émetteur, en secondes. Seuls les écarts entre deux états comptent.</param>
/// <param name="Segment">Incrémenté à chaque téléportation (restart, respawn) : pas d'interpolation entre deux segments.</param>
/// <param name="CamPosition">Caméra du joueur (position et pitch/yaw/roll) : les spectateurs voient ce qu'il voit.</param>
public readonly record struct PlayerState(double Time, Vec3 Position, Vec3 Orientation, ushort Segment, Vec3 CamPosition = default, Vec3 CamOrientation = default);

public readonly record struct PlayerInfo(int Id, string Name, string MapKey, int Skin, bool IsSpectator = false);

/// <summary>Sérialisation des messages. Chaque message commence par son <see cref="MessageType"/>.</summary>
public static class Msg
{
    public static NetDataWriter Hello(NetDataWriter w, string name, int skin)
    {
        w.Reset();
        w.Put((byte)MessageType.Hello);
        w.Put(name);
        w.Put(skin);
        return w;
    }

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

    public static NetDataWriter Welcome(NetDataWriter w, int yourId, IReadOnlyCollection<PlayerInfo> players)
    {
        w.Reset();
        w.Put((byte)MessageType.Welcome);
        w.Put(yourId);
        w.Put(players.Count);
        foreach (PlayerInfo p in players)
        {
            Put(w, p);
        }
        return w;
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

    public static NetDataWriter State(NetDataWriter w, int? id, in PlayerState s)
    {
        w.Reset();
        w.Put((byte)MessageType.State);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        Put(w, s);
        return w;
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
        return w;
    }

    public static RoundInfo GetRound(NetDataReader r) =>
        new(r.GetInt(), r.GetString(256), (RoundPhase)r.GetByte(), r.GetFloat(), r.GetInt(), r.GetInt(), r.GetBool(),
            (GameMode)r.GetByte(), r.GetFloat(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetBool());

    /// <summary>S→C (admins) : réglages actuels. C→S : réglages à appliquer (+ enregistrer dans le fichier de config).</summary>
    public static NetDataWriter Settings(NetDataWriter w, MatchSettings settings, bool saveToFile)
    {
        w.Reset();
        w.Put((byte)MessageType.Settings);
        w.Put(System.Text.Json.JsonSerializer.Serialize(settings, MatchSettings.JsonInfo));
        w.Put(saveToFile);
        return w;
    }

    public static (MatchSettings Settings, bool SaveToFile) GetSettings(NetDataReader r)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize(r.GetString(16384), MatchSettings.JsonInfo) ?? new MatchSettings();
        return (settings, r.GetBool());
    }

    /// <summary>C→S : le joueur est sur la ligne de départ de la manche (modes synchronisés).</summary>
    public static NetDataWriter Ready(NetDataWriter w, int roundId)
    {
        w.Reset();
        w.Put((byte)MessageType.Ready);
        w.Put(roundId);
        return w;
    }

    /// <summary>C→S : le joueur abandonne la manche (retour au départ après le top).</summary>
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

    public static NetDataWriter Event(NetDataWriter w, string code, string argument)
    {
        w.Reset();
        w.Put((byte)MessageType.ServerEvent);
        w.Put(code);
        w.Put(argument);
        return w;
    }

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

    public static NetDataWriter Reply(NetDataWriter w, string code, string argument)
    {
        w.Reset();
        w.Put((byte)MessageType.CommandReply);
        w.Put(code);
        w.Put(argument);
        return w;
    }

    /// <summary>Nettoie un message de chat : longueur bornée, sans caractères de contrôle ni de format invisibles.</summary>
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
            w.Put(e.Name);
            w.Put(e.Points);
            w.Put(e.RoundsPlayed);
            w.Put((byte)e.Status);
        }
        return w;
    }

    public static List<StandingEntry> GetStandings(NetDataReader r)
    {
        int count = Math.Clamp(r.GetInt(), 0, 256);
        var list = new List<StandingEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new StandingEntry(r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetInt(), r.GetInt(), (StandingStatus)r.GetByte()));
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
            w.Put(e.PlayerId);
            w.Put(e.Name);
            w.Put(e.BestMs);
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
            list.Add(new LeaderboardEntry(r.GetInt(), r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetUInt()));
        }
        return (roundId, list);
    }

    /// <summary>
    /// Vrai si la map d'un joueur correspond à l'entrée de rotation :
    /// officielle "o:1:2001" pour "o:1" ou "o:1:2001", Workshop "c:M123.&lt;version&gt;" pour "c:M123" ou la clé complète.
    /// </summary>
    public static bool MapMatches(string playerMapKey, string roundMapKey) =>
        roundMapKey.Length > 0 && (playerMapKey == roundMapKey
            || playerMapKey.StartsWith(roundMapKey + ":", StringComparison.Ordinal)
            || playerMapKey.StartsWith(roundMapKey + ".", StringComparison.Ordinal));

    /// <summary>Clés de map acceptées dans une rotation : niveau officiel ou map du Workshop.</summary>
    public static bool IsValidRotationKey(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key, @"^(o:\d+(:\d+)?|c:M[1-9]\d*(\.[0-9a-f]{64})?)$");

    /// <summary>Identifiant Workshop d'une clé "c:M123" ou "c:M123.&lt;version&gt;", sinon null.</summary>
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

    public static void Put(NetDataWriter w, in PlayerState s)
    {
        w.Put(s.Time);
        Put(w, s.Position);
        Put(w, s.Orientation);
        w.Put(s.Segment);
        Put(w, s.CamPosition);
        Put(w, s.CamOrientation);
    }

    public static PlayerState GetState(NetDataReader r) =>
        new(r.GetDouble(), GetVec3(r), GetVec3(r), r.GetUShort(), GetVec3(r), GetVec3(r));

    /// <summary>C→S (sans id) et S→C (avec id), fiable : le joueur passe spectateur, ou revient dans la partie.</summary>
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

    private static void Put(NetDataWriter w, in Vec3 v)
    {
        w.Put(v.X);
        w.Put(v.Y);
        w.Put(v.Z);
    }

    private static Vec3 GetVec3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());

    /// <summary>
    /// Nettoie un pseudo reçu : longueur bornée (en caractères visibles), et suppression des caractères
    /// de contrôle et de format invisibles (contrôles bidi, caractères « tag », espaces de largeur nulle…)
    /// qui permettent d'usurper un pseudo ou de casser l'affichage.
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
