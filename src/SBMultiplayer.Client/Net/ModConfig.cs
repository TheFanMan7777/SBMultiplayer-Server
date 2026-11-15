using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Configuration read from SBMultiplayer.json (next to the DLL), created with default values if missing.
/// Environment variables take precedence (handy to run two instances): SBMULTI_SERVER, SBMULTI_NAME.
/// </summary>
internal sealed class ModConfig
{
    /// <summary>Format of this file. Bumped only with a migration in <see cref="Migrate"/>: settings are never lost on update.</summary>
    public const int CurrentConfigVersion = 1;

    /// <summary>Format of the file that was read (0 = written before 0.19).</summary>
    public int ConfigVersion { get; set; }

    /// <summary>Settings this version doesn't know (written by a newer version): kept as they are when saving.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>Server address, "host:port" or "host".</summary>
    public string Server { get; set; } = $"127.0.0.1:{ProtocolInfo.DefaultPort}";

    /// <summary>Website that hosts rooms ("Create a room" and room codes), e.g. https://playsbm.thefanclub.cc.</summary>
    public string RoomSite { get; set; } = "https://playsbm.thefanclub.cc";

    /// <summary>Displayed name. Empty = Steam name.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>When hosting, let Steam friends join through Steam P2P (no port to open).</summary>
    public bool SteamP2PHost { get; set; } = true;

    /// <summary>UDP port used when hosting a game from the F8 menu.</summary>
    public int HostPort { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>Server password (empty if the server is open).</summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>Admin password, remembered to log in as admin again easily.</summary>
    public string AdminPassword { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>Max distance (in game units, 1 unit = 1 cm) to show a player's name. 0 = never.</summary>
    public float NameTagMaxDistance { get; set; } = 30f;

    /// <summary>Height of the name above the board, in game units.</summary>
    public float NameTagHeight { get; set; } = 0.8f;

    /// <summary>Name text size (1 = the game's font size at 1080p).</summary>
    public float NameTagScale { get; set; } = 0.4f;

    /// <summary>Round panel (time left, standings) at the top right.</summary>
    public bool ShowRoundPanel { get; set; } = true;

    /// <summary>Round panel size (1 = the original v0.4 size, found too big).</summary>
    public float RoundPanelScale { get; set; } = 0.6f;

    /// <summary>Shows the other players' ghosts.</summary>
    public bool ShowOtherPlayers { get; set; } = true;

    /// <summary>Opacity of the other players' ghosts (0 to 1).</summary>
    public float GhostOpacity { get; set; } = 0.5f;

    /// <summary>Shows names above nearby ghosts.</summary>
    public bool ShowNameTags { get; set; } = true;

    /// <summary>Adds the distance to the names ("Bob - 42 cm").</summary>
    public bool ShowNameTagDistance { get; set; } = true;

    /// <summary>Checkpoint splits (the game's display), compared with our best run of the round.</summary>
    public bool ShowSplits { get; set; } = true;

    /// <summary>Language of the mod's interface: "auto" (the game's), "en" or "fr".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Multiplayer menu key (a MonoGame key name: F8, F10, Insert…).</summary>
    public string MenuKey { get; set; } = "F8";

    /// <summary>Key to join the current round's map.</summary>
    public string RejoinKey { get; set; } = "F7";

    /// <summary>Key to write in the chat. With Enter (the game's confirm key), the chat only opens in a level.</summary>
    public string ChatKey { get; set; } = "Enter";

    /// <summary>Checks GitHub at startup for a newer version of the mod (reads the public release page only).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Hosting: list the game publicly in F8 > Public games (anyone on Steam can join it).</summary>
    public bool PublicGame { get; set; }

    /// <summary>Key to hold to show the player list (name, map, role, points).</summary>
    public string ScoreboardKey { get; set; } = "Tab";

    /// <summary>Closes the end-of-game screen until the next game.</summary>
    public string CloseEndScreenKey { get; set; } = "Escape";

    /// <summary>Spectator (and frozen mouse): follow the previous player.</summary>
    public string SpectatePreviousKey { get; set; } = "Left";

    /// <summary>Spectator (and frozen mouse): follow the next player.</summary>
    public string SpectateNextKey { get; set; } = "Right";

    /// <summary>Frozen mouse: stop following a player, back to the free camera.</summary>
    public string StopFollowingKey { get; set; } = "Down";

    /// <summary>Hide and Seek hunter: catch the mouse you're aiming at (a key or a mouse button, independent of the game's grind).</summary>
    public string CatchKey { get; set; } = "RightButton";

    /// <summary>Players whose chat messages are hidden (/ignore).</summary>
    public List<string> IgnoredPlayers { get; set; } = new();

    /// <summary>Shows the chat (messages and events) at the bottom left.</summary>
    public bool ShowChat { get; set; } = true;

    /// <summary>
    /// Notifications also at the top center of the screen. By default they only go to the chat
    /// (they come back to the top if the chat is hidden).
    /// </summary>
    public bool NotificationsOnTop { get; set; }

    /// <summary>Chat text size (1 = original size).</summary>
    public float ChatScale { get; set; } = 1f;

    /// <summary>Debug: logs the position every second.</summary>
    public bool DebugProbe { get; set; }

    /// <summary>Debug: local ghost following the player 1.5 s behind (step 2).</summary>
    public bool DebugDelayedSelfGhost { get; set; }

    public static ModConfig Current { get; private set; } = new();

    // Comments and trailing commas: the file is edited by hand too.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string path = "";

    // File values, when an environment variable overrides them: not overwritten when saving.
    private static (string Server, string Name)? fileValuesBeforeEnv;

    /// <summary>Saves the configuration (called when a setting changes in the menu).</summary>
    public static void Save()
    {
        try
        {
            ModConfig toWrite = Current;
            if (fileValuesBeforeEnv is { } fileValues)
            {
                toWrite = (ModConfig)Current.MemberwiseClone();
                toWrite.Server = fileValues.Server;
                toWrite.PlayerName = fileValues.Name;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(toWrite, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Error($"Could not save the configuration ({path})", e);
        }
    }

    /// <summary>The address or name were changed in the menu: they replace the environment override.</summary>
    public static void ForgetEnvironmentOverride() => fileValuesBeforeEnv = null;

    public static void Load(string modDir)
    {
        path = Path.Combine(modDir, "SBMultiplayer.json");
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions) ?? new ModConfig();
            }
            else
            {
                Current = new ModConfig { ConfigVersion = CurrentConfigVersion };
                File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
                ModLog.Info($"Default configuration created: {path}");
            }
        }
        catch (Exception e)
        {
            // Keep the player's file: the next save would overwrite it with the default values.
            string backup = path + ".unreadable";
            try
            {
                File.Copy(path, backup, overwrite: true);
            }
            catch (Exception)
            {
                backup = "(no copy)";
            }
            ModLog.Error($"Unreadable configuration ({path}), using default values. The file was copied to {backup}", e);
            Current = new ModConfig { ConfigVersion = CurrentConfigVersion };
        }

        if (Current.ConfigVersion < CurrentConfigVersion)
        {
            Migrate(Current);
            Current.ConfigVersion = CurrentConfigVersion;
            Save();
        }

        string envServer = Environment.GetEnvironmentVariable("SBMULTI_SERVER") ?? "";
        string envName = Environment.GetEnvironmentVariable("SBMULTI_NAME") ?? "";
        if (envServer.Length > 0 || envName.Length > 0)
        {
            fileValuesBeforeEnv = (Current.Server, Current.PlayerName);
        }
        if (Environment.GetEnvironmentVariable("SBMULTI_SERVER") is { Length: > 0 } server)
        {
            Current.Server = server;
        }
        if (Environment.GetEnvironmentVariable("SBMULTI_NAME") is { Length: > 0 } name)
        {
            Current.PlayerName = name;
        }
    }

    /// <summary>Upgrades a file written by an older version, one format at a time.</summary>
    private static void Migrate(ModConfig c)
    {
        if (c.ConfigVersion < 1)
        {
            // Before 0.16, Tab opened the chat: it's the player list now, and the chat moves to Enter.
            if (c.ChatKey.Equals("Tab", StringComparison.OrdinalIgnoreCase))
            {
                c.ChatKey = "Enter";
            }
        }
        // Next format: if (c.ConfigVersion < 2) { ... }
    }

    /// <summary>The WebSocket address of a room: the room site with wss:// (or ws://) and /multiplayer/ws/CODE.</summary>
    public Uri RoomSocketUri(string code)
    {
        string site = RoomSite.Trim().TrimEnd('/');
        if (site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) site = "wss://" + site[8..];
        else if (site.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) site = "ws://" + site[7..];
        return new Uri($"{site}/multiplayer/ws/{Uri.EscapeDataString(code)}");
    }

    public (string Host, int Port) ParseServer()
    {
        string s = Server.Trim();
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out int port))
        {
            return (s[..colon], port);
        }
        return (s, ProtocolInfo.DefaultPort);
    }
}
