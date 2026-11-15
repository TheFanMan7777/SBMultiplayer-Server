using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Relay server: it does not simulate physics. It keeps the player list and their map, relays
/// board states, and runs the rounds (imposed map, timer, leaderboard, match points, admin commands).
/// Everything runs on a single thread (<see cref="Poll"/>).
/// </summary>
public sealed class GameServer : INetEventListener
{
    private const int EchoIdOffset = 1_000_000;

    /// <summary>Accepted margin between a run's duration and the time elapsed since the round started (latency, loading).</summary>
    private const double FinishToleranceSeconds = 3;

    private sealed class Player
    {
        public required IPeer Peer { get; init; }
        public int Id => Peer.Id;
        public string Name { get; set; } = "";
        public string MapKey { get; set; } = "";
        public bool HasSaidHello { get; set; }
        public int Skin { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSpectator { get; set; }
        /// <summary>The client's mod version, game modes and optional features (from Hello).</summary>
        public Capabilities Capabilities { get; set; } = Capabilities.Unknown;
        public PlayerInfo Info => new(Id, Name, MapKey, Skin, IsSpectator);

        /// <summary>Takes part in rounds: connected, not spectating.</summary>
        public bool IsPlaying => HasSaidHello && !IsSpectator;
        public Queue<double> RecentChat { get; } = new();
        public HashSet<MessageType> UnknownMessages { get; } = new();
    }

    private readonly ServerOptions options;
    /// <summary>The UDP listener. Null for a web room, which has no port of its own.</summary>
    private readonly NetManager? net;
    private readonly NetDataWriter writer = new();
    private readonly Dictionary<int, Player> players = new();
    private readonly Queue<(double Due, int PeerId, PlayerState State)> echoQueue = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    // ---------- Round state ----------
    private RoundPhase phase = RoundPhase.Free;
    private int roundId;
    private int mapIndex = -1;
    private double phaseStart;
    private double phaseEnd;
    private bool paused;
    private double pausedRemaining;

    // ---------- Rounds mode (synchronized start) ----------
    /// <summary>Synchronized-start modes: Rounds, Cup, Elimination.</summary>
    private bool IsRoundsMode => options.Mode.IsSynchronized();

    /// <summary>Elimination mode: eliminated players (by name).</summary>
    private readonly HashSet<string> eliminated = new(StringComparer.Ordinal);

    /// <summary>Cup / Elimination: the match has a winner; a new match starts after the results.</summary>
    private bool matchOver;

    private readonly string? configPath;
    private int roundInMap = 1;
    private double? startAt;
    private double? firstFinishAt;
    private readonly HashSet<int> ready = new();
    private readonly HashSet<int> participants = new();
    private readonly HashSet<int> gaveUp = new();

    /// <summary>Players who gave up this round, first to give up first.</summary>
    private readonly List<int> gaveUpOrder = new();

    /// <summary>When each player's last board state arrived (a game in the background sends nothing).</summary>
    private readonly Dictionary<int, double> lastStateAt = new();

    /// <summary>A participant who sent no board state for this long is no longer waited for (game in the background...).</summary>
    private const double StalledSeconds = 10;

    /// <summary>Hide and Seek: the mice away from the map at the start of the hunt were put out of the round.</summary>
    private bool huntStartChecked;

    /// <summary>Best time of each player in the current round (the name is kept if the player leaves).</summary>
    private readonly Dictionary<int, LeaderboardEntry> best = new();

    /// <summary>Match standings, by player name (players get a new id when they reconnect).</summary>
    private readonly Dictionary<string, StandingEntry> standings = new();

    // ---------- Hide and Seek ----------
    private bool IsHideAndSeek => options.Mode == GameMode.HideAndSeek;

    /// <summary>Hunters of the round (found mice join them in infection mode), by id, with their name.</summary>
    private readonly Dictionary<int, string> hunters = new();

    /// <summary>Mice of the round (found ones included), by id, with their name.</summary>
    private readonly Dictionary<int, string> mice = new();

    private readonly HashSet<int> found = new();

    /// <summary>Hunter(s) of the previous round: never hunter twice in a row (unless chosen by an admin).</summary>
    private readonly HashSet<string> lastHunters = new(StringComparer.Ordinal);

    /// <summary>Next hunter chosen by an admin (by name), or null for a random one.</summary>
    private string? nextHunter;

    /// <summary>Players in the round when it started (hunter included): finding a mouse is worth RoundSeconds / this.</summary>
    private int huntPlayerCount;

    /// <summary>Last board position of each player, to check that a hunter is really close to the mouse they found.</summary>
    private readonly Dictionary<int, Vec3> lastPosition = new();

    /// <summary>Start of the hunt: the mice are frozen, the hunter is released.</summary>
    private double SeekStart => (startAt ?? double.MaxValue / 2) + options.HideSeconds;

    public GameServer(ServerOptions options, string? configPath = null, bool listenUdp = true)
    {
        this.options = options;
        this.configPath = configPath;
        if (listenUdp)
        {
            net = new NetManager(this)
            {
                AutoRecycle = true,
                UpdateTime = 5,
                DisconnectTimeout = 10_000,
            };
        }
    }

    public int PlayerCount => players.Count;

    private bool HasRotation => options.Maps.Count > 0 && options.Mode != GameMode.Free;

    private string CurrentMap => phase == RoundPhase.Free ? "" : options.Maps[mapIndex];

    private double Now => clock.Elapsed.TotalSeconds;

    public void Start()
    {
        if (net != null && !net.Start(options.Port))
        {
            throw new InvalidOperationException($"Cannot listen on UDP port {options.Port}");
        }
        string transport = net != null ? $"on UDP port {options.Port}" : "as a web room";
        Log($"\"{options.ServerName}\" started {transport} (protocol v{ProtocolInfo.Version}, {options.MaxPlayers} players max"
            + (options.Password != "" ? ", password protected" : "")
            + (options.AdminPassword != "" ? ", in-game admin enabled" : "")
            + (options.Echo ? ", echo mode" : "") + ")");
        LogSettings();
    }

    private void LogSettings()
    {
        Log(HasRotation
            ? (IsRoundsMode
                ? $"{options.Mode} mode - rotation: {string.Join(", ", options.Maps)} - {options.RoundsPerMap} rounds per map, {options.RoundSeconds:0} s limit, {options.ResultsSeconds:0} s results"
                  + (options.Mode == GameMode.Cup ? $", {options.CupPointsLimit} points to become a finalist" : "")
                  + (options.Mode == GameMode.Elimination ? $", {options.EliminatedPerRound} eliminated per round{(options.DnfFillsQuota ? " (players who don't finish fill the quota)" : "")}" : "")
                  + (IsHideAndSeek ? $", {options.HideSeconds:0} s to hide{(options.Infection ? ", infection" : "")}" : "")
                : $"Time Attack - rotation: {string.Join(", ", options.Maps)} - {options.RoundSeconds:0} s rounds, {options.ResultsSeconds:0} s results")
            : "Free mode: no rounds, everyone rides wherever they want");
    }

    /// <summary>What a game browser shows about this server. Refreshed every second by <see cref="Poll"/>, readable from any thread.</summary>
    public sealed record ServerSummary(string Name, GameMode Mode, string MapKey, int Players, int MaxPlayers, bool HasPassword);

    private volatile ServerSummary? summary;
    private double nextSummaryAt;

    public ServerSummary? Summary => summary;

    public void Poll()
    {
        net?.PollEvents();
        if (Now - lastStatesFlush >= StatesFlushSeconds)
        {
            FlushStates();
        }
        if (Now >= nextSummaryAt)
        {
            nextSummaryAt = Now + 1;
            summary = new ServerSummary(options.ServerName, HasRotation ? options.Mode : GameMode.Free, phase == RoundPhase.Free ? "" : CurrentMap,
                players.Values.Count(p => p.HasSaidHello), options.MaxPlayers, options.Password != "");
        }
        if (options.Echo)
        {
            FlushEcho();
        }
        UpdateRound();
    }

    public void Stop() => net?.Stop(sendDisconnectMessages: true);

    // ---------- Console ----------

    public IEnumerable<string> DescribePlayers() =>
        players.Values.Select(p => $"#{p.Id} {p.Name}{(p.IsAdmin ? " [admin]" : "")} - map: {(p.MapKey == "" ? "(menu)" : p.MapKey)} - ping {p.Peer.Ping} ms");

    public string DescribeRound() => phase switch
    {
        RoundPhase.Free => "Free mode",
        _ => $"Round {roundId} - map {mapIndex + 1}/{options.Maps.Count} ({CurrentMap}) - {phase}{(paused ? " (paused)" : "")} - {Remaining:0} s left",
    };

    public IEnumerable<string> DescribeStandings() =>
        SortedStandings().Select((e, i) => $"{i + 1}. {e.Name} - {e.Points} pts ({e.RoundsPlayed} rounds)");

    /// <summary>Runs an admin command from the server console (no admin name). Returns an error message, or null.</summary>
    public string? RunConsoleCommand(AdminCommand command, int argument)
    {
        var (code, arg) = ExecuteAdmin(null, command, argument);
        return code == CommandResults.Done ? null : DescribeResult(code, arg);
    }

    /// <summary>English text of a command result, for the server console.</summary>
    private static string DescribeResult(string code, string arg) => code switch
    {
        CommandResults.NoRound => "No round in progress",
        CommandResults.Usage => $"Usage: {arg}",
        CommandResults.NoPlayer => $"No player {arg}",
        CommandResults.BadMap => $"Map index out of range (the rotation has {arg} maps)",
        _ => $"{code} {arg}",
    };

    // ---------- Rounds ----------

    private double Remaining => Math.Max(0, (paused ? pausedRemaining : phaseEnd - Now));

    private void UpdateRound()
    {
        if (!HasRotation)
        {
            if (phase != RoundPhase.Free)
            {
                // Switched to free mode: tell the clients the rounds are over.
                phase = RoundPhase.Free;
                mapIndex = -1;
                paused = false;
                BroadcastRound();
            }
            return;
        }
        if (!players.Values.Any(p => p.HasSaidHello))
        {
            if (phase != RoundPhase.Free)
            {
                Log("Nobody left: rotation paused");
                phase = RoundPhase.Free;
                mapIndex = -1;
                paused = false;
            }
            return;
        }

        if (phase == RoundPhase.Free)
        {
            StartRound((mapIndex + 1) % options.Maps.Count, newMap: true);
        }
        else if (paused)
        {
            phaseEnd = Now + pausedRemaining;
        }
        else if (phase == RoundPhase.Preparing)
        {
            // Everyone on the start line (or waited long enough): countdown, then start.
            int connected = players.Values.Count(p => p.IsPlaying && !eliminated.Contains(p.Name));
            if ((ready.Count >= connected && ready.Count > 0) || Now >= phaseEnd)
            {
                BeginCountdown();
            }
        }
        else if (phase == RoundPhase.Playing && IsHideAndSeek && !huntStartChecked && startAt != null && Now >= SeekStart)
        {
            huntStartChecked = true;
            ForfeitMiceAwayFromMap();
        }
        else if (phase == RoundPhase.Playing && IsRoundsMode && (IsHideAndSeek ? HuntIsOver() : RoundsRaceIsOver()))
        {
            EndRound();
        }
        else if (Now >= phaseEnd)
        {
            if (phase == RoundPhase.Playing)
            {
                EndRound();
            }
            else
            {
                AdvanceAfterResults();
            }
        }
    }

    /// <summary>After the results: next round on the same map (Rounds mode), or next map.</summary>
    private void AdvanceAfterResults()
    {
        if (matchOver)
        {
            // Cup or Elimination won: new match, from the next map.
            ResetMatch();
            StartRound((mapIndex + 1) % options.Maps.Count, newMap: true);
            return;
        }
        if (IsRoundsMode && roundInMap < Math.Max(1, options.RoundsPerMap))
        {
            roundInMap++;
            StartRound(mapIndex, newMap: false);
        }
        else
        {
            StartRound((mapIndex + 1) % options.Maps.Count, newMap: true);
        }
    }

    private void StartRound(int index, bool newMap)
    {
        if (newMap)
        {
            roundInMap = 1;
        }
        mapIndex = index;
        roundId++;
        paused = false;
        matchEnded = false;
        phaseStart = Now;
        best.Clear();
        ready.Clear();
        participants.Clear();
        gaveUp.Clear();
        gaveUpOrder.Clear();
        huntStartChecked = false;
        startAt = null;
        firstFinishAt = null;
        hunters.Clear();
        mice.Clear();
        found.Clear();
        if (IsRoundsMode)
        {
            phase = RoundPhase.Preparing;
            phaseEnd = Now + options.PrepareTimeoutSeconds;
            Log($"Round {roundId}: {CurrentMap} (round {roundInMap}/{options.RoundsPerMap} on this map) - waiting for players");
        }
        else
        {
            phase = RoundPhase.Playing;
            phaseEnd = Now + options.RoundSeconds;
            Log($"Round {roundId}: {CurrentMap} ({options.RoundSeconds:0} s)");
        }
        BroadcastRound();
        BroadcastLeaderboard();
        if (IsHideAndSeek)
        {
            BroadcastHideSeek();
        }
    }

    /// <summary>Rounds mode: everyone ready (or timeout). The players ready now are the participants of the race.</summary>
    private void BeginCountdown()
    {
        phase = RoundPhase.Playing;
        participants.UnionWith(ready);
        startAt = Now + options.CountdownSeconds;
        phaseStart = startAt.Value;
        phaseEnd = startAt.Value + options.RoundSeconds + (IsHideAndSeek ? options.HideSeconds : 0);
        Log($"Round {roundId}: start in {options.CountdownSeconds:0} s ({participants.Count} participant(s))");
        if (IsHideAndSeek)
        {
            ChooseHunters();
        }
        BroadcastRound();
    }

    // ---------- Hide and Seek ----------

    /// <summary>
    /// Picks the hunter among the players ready on the start line: the one chosen by an admin, otherwise a random
    /// player who wasn't the hunter of the previous round. Everyone else is a mouse.
    /// </summary>
    private void ChooseHunters()
    {
        hunters.Clear();
        mice.Clear();
        found.Clear();
        var candidates = participants.Where(players.ContainsKey).Select(id => players[id]).ToList();
        huntPlayerCount = candidates.Count;
        if (candidates.Count == 0)
        {
            return;
        }
        Player? hunter = nextHunter != null ? candidates.FirstOrDefault(c => c.Name == nextHunter) : null;
        nextHunter = null;
        if (hunter == null)
        {
            var pool = candidates.Where(c => !lastHunters.Contains(c.Name)).ToList();
            if (pool.Count == 0)
            {
                pool = candidates;
            }
            hunter = pool[Random.Shared.Next(pool.Count)];
        }
        hunters[hunter.Id] = hunter.Name;
        lastHunters.Clear();
        lastHunters.Add(hunter.Name);
        foreach (Player c in candidates.Where(c => c != hunter))
        {
            mice[c.Id] = c.Name;
        }
        Log($"Round {roundId}: {hunter.Name} is the hunter, {mice.Count} mice");
        BroadcastEvent(ServerEvents.HunterChosen, hunter.Name);
        BroadcastHideSeek();
    }

    /// <summary>Mice not found yet and still connected.</summary>
    private IEnumerable<int> HiddenMice() =>
        mice.Keys.Where(id => !found.Contains(id) && !hunters.ContainsKey(id) && players.ContainsKey(id));

    /// <summary>After the hiding time: every mouse found (or gone), or no hunter left.</summary>
    private bool HuntIsOver()
    {
        if (startAt == null || Now < SeekStart)
        {
            return false;
        }
        return !HiddenMice().Any() || !hunters.Keys.Any(players.ContainsKey);
    }

    private int FinderPoints => huntPlayerCount == 0 ? 0 : (int)Math.Round(options.RoundSeconds / huntPlayerCount);

    /// <summary>One point per second spent hidden during the hunt.</summary>
    private int SurvivalPoints(double until) =>
        (int)Math.Max(0, Math.Floor(Math.Min(until, SeekStart + options.RoundSeconds) - SeekStart));

    private void AddPoints(string name, int points, int rounds = 0)
    {
        standings.TryGetValue(name, out StandingEntry previous);
        standings[name] = new StandingEntry(name, previous.Points + points, previous.RoundsPlayed + rounds, previous.Status);
    }

    private void OnTag(Player p, int tagRoundId, int targetId)
    {
        if (!IsHideAndSeek || phase != RoundPhase.Playing || tagRoundId != roundId || Now < SeekStart
            || !mice.TryGetValue(targetId, out string? mouseName) || found.Contains(targetId) || hunters.ContainsKey(targetId))
        {
            return;
        }
        if (targetId == p.Id)
        {
            // The mouse left its hiding place (restart from the pause menu...): counts as found, by nobody.
            MouseOut(targetId, mouseName);
            Log($"{mouseName} left their hiding place");
            BroadcastEvent(ServerEvents.MouseForfeit, mouseName);
            return;
        }
        if (!hunters.ContainsKey(p.Id))
        {
            return;
        }
        if (lastPosition.TryGetValue(p.Id, out Vec3 a) && lastPosition.TryGetValue(targetId, out Vec3 b))
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > HideSeekRules.ServerTagRange)
            {
                Log($"Tag refused: {p.Name} is {distance:0} cm away from {mouseName}");
                return;
            }
        }
        AddPoints(p.Name, FinderPoints);
        MouseOut(targetId, mouseName);
        Log($"{p.Name} found {mouseName}");
        BroadcastEvent(ServerEvents.MouseFound, $"{p.Name}\n{mouseName}");
    }

    /// <summary>A mouse is out of the hunt (found, gave up or left): survival points, and a hunter in infection mode.</summary>
    private void MouseOut(int id, string name)
    {
        found.Add(id);
        AddPoints(name, SurvivalPoints(Now));
        if (options.Infection && players.ContainsKey(id))
        {
            hunters[id] = name;
        }
        BroadcastHideSeek();
        BroadcastStandings();
    }

    /// <summary>
    /// Start of the hunt: a mouse that left the round's map while hiding can't be found there anymore, so it's out
    /// (like leaving the map during the hunt). Otherwise it would come back to the spawn after the hunt started.
    /// </summary>
    private void ForfeitMiceAwayFromMap()
    {
        foreach (int id in HiddenMice().ToList())
        {
            Player p = players[id];
            if (!Msg.MapMatches(p.MapKey, CurrentMap))
            {
                MouseOut(id, p.Name);
                Log($"{p.Name} wasn't on the map when the hunt started");
                BroadcastEvent(ServerEvents.MouseForfeit, p.Name);
            }
        }
    }

    /// <summary>A mouse disconnects or becomes a spectator during the round: out of the hunt.</summary>
    private void OnLeftHunt(Player p)
    {
        if (IsHideAndSeek && phase == RoundPhase.Playing && mice.ContainsKey(p.Id) && !found.Contains(p.Id))
        {
            MouseOut(p.Id, p.Name);
        }
    }

    /// <summary>End of a Hide and Seek round: the mice still hidden get their survival points.</summary>
    private void AwardHideSeekPoints()
    {
        var survivors = mice.Where(m => !found.Contains(m.Key)).ToList();
        foreach (var (_, name) in survivors)
        {
            AddPoints(name, SurvivalPoints(Now));
        }
        foreach (string name in mice.Values.Concat(hunters.Values).Distinct())
        {
            AddPoints(name, 0, rounds: 1);
        }
        if (startAt == null || Now < SeekStart || mice.Count == 0)
        {
            return; // skipped before the hunt
        }
        if (survivors.Count == 0)
        {
            Log("Every mouse was found: the hunters win the round");
            BroadcastEvent(ServerEvents.HuntersWin, string.Join(", ", hunters.Where(h => !mice.ContainsKey(h.Key)).Select(h => h.Value)));
        }
        else
        {
            string names = string.Join(", ", survivors.Select(m => m.Value));
            Log($"Time is up, surviving mice: {names}");
            BroadcastEvent(ServerEvents.MiceSurvived, names);
        }
    }

    private void BroadcastHideSeek() =>
        Broadcast(Msg.HideSeek(writer, CurrentHideSeek()), DeliveryMethod.ReliableOrdered, except: null);

    private HideSeekInfo CurrentHideSeek() =>
        new(roundId, hunters.Keys.ToArray(), mice.Keys.ToArray(), found.ToArray());

    /// <summary>Rounds mode: nobody is racing anymore (all finished, gave up, left or stalled), or the finish timeout expired.</summary>
    private bool RoundsRaceIsOver()
    {
        if (startAt == null || Now < startAt)
        {
            return false;
        }
        if (firstFinishAt is double first && Now >= first + options.FinishTimeoutSeconds)
        {
            return true;
        }
        return !participants.Any(id => players.ContainsKey(id) && !best.ContainsKey(id) && !gaveUp.Contains(id) && !IsStalled(id));
    }

    /// <summary>
    /// No board state from this participant for <see cref="StalledSeconds"/> since the start: their game is in the
    /// background or frozen. The round doesn't wait for them (a time they send later still counts).
    /// </summary>
    private bool IsStalled(int id)
    {
        double since = Math.Max(startAt ?? Now, lastStateAt.TryGetValue(id, out double t) ? t : double.NegativeInfinity);
        return Now - since > StalledSeconds;
    }

    private void OnReady(Player p, int readyRoundId)
    {
        if (!IsRoundsMode || readyRoundId != roundId || phase != RoundPhase.Preparing || !Msg.MapMatches(p.MapKey, CurrentMap)
            || eliminated.Contains(p.Name) || p.IsSpectator)
        {
            return;
        }
        if (ready.Add(p.Id))
        {
            BroadcastRound(); // updates the "ready x/y" counter
        }
    }

    /// <summary>A player becomes a spectator (or comes back): out of the ready wait and the current race.</summary>
    private void OnSpectate(Player p, bool spectating)
    {
        if (p.IsSpectator == spectating)
        {
            return;
        }
        p.IsSpectator = spectating;
        if (spectating)
        {
            ready.Remove(p.Id);
            participants.Remove(p.Id); // the round doesn't wait for them anymore
            OnLeftHunt(p);
        }
        Log($"{p.Name} {(spectating ? "is now spectating" : "is back in the game")}");
        Broadcast(Msg.Spectate(writer, p.Id, spectating), DeliveryMethod.ReliableOrdered, except: p);
        if (phase != RoundPhase.Free)
        {
            BroadcastRound(); // ready x/y counters
        }
    }

    private void OnGiveUp(Player p, int giveUpRoundId)
    {
        if (IsRoundsMode && !IsHideAndSeek && giveUpRoundId == roundId && phase == RoundPhase.Playing && participants.Contains(p.Id)
            && !best.ContainsKey(p.Id) && gaveUp.Add(p.Id))
        {
            gaveUpOrder.Add(p.Id);
            Log($"{p.Name} gave up");
        }
    }

    /// <summary>The results phase in progress ends a game: clients show the end-of-game screen.</summary>
    private bool matchEnded;

    private void EndRound()
    {
        phase = RoundPhase.Results;
        paused = false;
        phaseStart = Now;
        AwardPoints(); // Cup / Elimination: may decide a winner (matchOver)

        // End of a game: each Time Attack round (end of the timer), the end of the rotation in Rounds mode,
        // or a Cup / Elimination winner.
        if ((options.Mode is GameMode.Rounds or GameMode.HideAndSeek) && mapIndex == options.Maps.Count - 1 && roundInMap >= Math.Max(1, options.RoundsPerMap))
        {
            matchOver = true;
            string? winner = SortedStandings().FirstOrDefault().Name;
            if (winner != null)
            {
                Log($"{winner} wins the match!");
                BroadcastEvent(ServerEvents.MatchWon, winner);
            }
        }
        matchEnded = options.Mode == GameMode.TimeAttack || matchOver;
        phaseEnd = Now + (matchEnded ? Math.Max(options.ResultsSeconds, options.MatchEndSeconds) : options.ResultsSeconds);
        BroadcastRound();
        BroadcastStandings();
        LogResults();
        ExportResults();
    }

    private int PointsForRank(int rank) => rank < options.PointsTable.Count ? options.PointsTable[rank] : 0;

    private void AwardPoints()
    {
        switch (options.Mode)
        {
            case GameMode.Cup:
                AwardCupPoints();
                break;
            case GameMode.Elimination:
                ApplyElimination();
                break;
            case GameMode.HideAndSeek:
                AwardHideSeekPoints();
                break;
            default:
                List<LeaderboardEntry> board = SortedLeaderboard();
                for (int rank = 0; rank < board.Count; rank++)
                {
                    string name = board[rank].Name;
                    standings.TryGetValue(name, out StandingEntry previous);
                    standings[name] = new StandingEntry(name, previous.Points + PointsForRank(rank), previous.RoundsPlayed + 1);
                }
                break;
        }
    }

    /// <summary>
    /// Cup: points by rank; reaching the limit makes you a finalist (points stop there).
    /// A player who was already a finalist and wins the round wins the cup.
    /// </summary>
    private void AwardCupPoints()
    {
        List<LeaderboardEntry> board = SortedLeaderboard();
        for (int rank = 0; rank < board.Count; rank++)
        {
            string name = board[rank].Name;
            standings.TryGetValue(name, out StandingEntry previous);
            if (previous.Status == StandingStatus.Finalist)
            {
                if (rank == 0 && !matchOver)
                {
                    matchOver = true;
                    standings[name] = previous with { Status = StandingStatus.Winner, RoundsPlayed = previous.RoundsPlayed + 1 };
                    Log($"{name} wins the cup!");
                    BroadcastEvent(ServerEvents.CupWon, name);
                }
                else
                {
                    standings[name] = previous with { RoundsPlayed = previous.RoundsPlayed + 1 };
                }
                continue;
            }
            int points = previous.Points + PointsForRank(rank);
            bool finalist = points >= options.CupPointsLimit;
            standings[name] = new StandingEntry(name, finalist ? options.CupPointsLimit : points, previous.RoundsPlayed + 1,
                finalist ? StandingStatus.Finalist : StandingStatus.Normal);
            if (finalist)
            {
                Log($"{name} is now a finalist");
                BroadcastEvent(ServerEvents.Finalist, name);
            }
        }
    }

    /// <summary>
    /// Elimination: the last EliminatedPerRound players are eliminated, but at least one player always survives.
    /// Players who didn't finish count as last: with DnfFillsQuota they fill the quota (first to give up first),
    /// otherwise all of them are eliminated. Players of the match who missed this round's start are eliminated too.
    /// Points = rounds survived. Last player standing wins.
    /// </summary>
    private void ApplyElimination()
    {
        var racers = participants.Where(players.ContainsKey).Select(id => players[id]).ToList();
        if (racers.Count == 0)
        {
            return; // nobody raced: nothing is decided this round
        }
        var finishers = SortedLeaderboard().Where(e => participants.Contains(e.PlayerId)).Select(e => e.Name).ToList();

        // Didn't finish, worst first: stalled (game in the background), then by give-up order, then still racing at the end.
        int Rank(Player p) => IsStalled(p.Id) ? -1 : gaveUpOrder.IndexOf(p.Id) is int i and >= 0 ? i : int.MaxValue;
        var dnf = racers.Where(p => !finishers.Contains(p.Name)).OrderBy(Rank).Select(p => p.Name).ToList();

        var out_ = new List<string>();
        int maxOut = racers.Count - 1; // at least one survivor
        if (options.DnfFillsQuota)
        {
            out_.AddRange(dnf.Concat(Enumerable.Reverse(finishers)).Take(Math.Min(options.EliminatedPerRound, maxOut)));
        }
        else if (finishers.Count > 0) // nobody finished: nobody is eliminated this round
        {
            out_.AddRange(dnf);
            int slowest = Math.Min(Math.Max(0, options.EliminatedPerRound - out_.Count), Math.Max(0, maxOut - out_.Count));
            out_.AddRange(finishers.Skip(finishers.Count - slowest));
        }

        // In the match but not in the race (missed the start, back from spectating...): eliminated, outside the quota.
        var absent = players.Values.Where(p => p.IsPlaying && !eliminated.Contains(p.Name) && !participants.Contains(p.Id))
            .Select(p => p.Name).ToList();

        foreach (string name in racers.Select(r => r.Name).Concat(absent))
        {
            standings.TryGetValue(name, out StandingEntry previous);
            bool isOut = out_.Contains(name) || absent.Contains(name);
            if (isOut)
            {
                eliminated.Add(name);
            }
            standings[name] = new StandingEntry(name, previous.Points + (isOut ? 0 : 1), previous.RoundsPlayed + (absent.Contains(name) ? 0 : 1),
                isOut ? StandingStatus.Eliminated : StandingStatus.Normal);
        }
        var allOut = out_.Concat(absent).ToList();
        if (allOut.Count > 0)
        {
            Log($"Eliminated: {string.Join(", ", out_)}{(absent.Count > 0 ? $" - missed the start: {string.Join(", ", absent)}" : "")}");
            BroadcastEvent(ServerEvents.Eliminated, string.Join(", ", allOut));
        }

        var remaining = players.Values.Where(p => p.IsPlaying && !eliminated.Contains(p.Name)).ToList();
        if (remaining.Count <= 1 && !matchOver)
        {
            matchOver = true;
            string winner = remaining.FirstOrDefault()?.Name ?? racers.Select(r => r.Name).First(n => !out_.Contains(n));
            standings.TryGetValue(winner, out StandingEntry w);
            standings[winner] = w with { Name = winner, Status = StandingStatus.Winner };
            Log($"{winner} wins the elimination!");
            BroadcastEvent(ServerEvents.EliminationWon, winner);
        }
    }

    private void ResetMatch()
    {
        standings.Clear();
        eliminated.Clear();
        matchOver = false;
        BroadcastStandings();
    }

    private RoundInfo CurrentRoundInfo() =>
        new(roundId, CurrentMap, phase, (float)Remaining, Math.Max(0, mapIndex), options.Maps.Count, paused,
            options.Mode,
            StartIn: startAt is double s ? (float)(s - Now) : 0f,
            RoundInMap: roundInMap,
            RoundsPerMap: IsRoundsMode ? Math.Max(1, options.RoundsPerMap) : 1,
            ReadyCount: ready.Count,
            PlayerCount: players.Values.Count(p => p.IsPlaying && !eliminated.Contains(p.Name)),
            PointsLimit: options.Mode == GameMode.Cup ? options.CupPointsLimit : 0,
            MatchEnded: phase == RoundPhase.Results && matchEnded,
            HideSeconds: IsHideAndSeek ? (float)options.HideSeconds : 0f,
            Infection: IsHideAndSeek && options.Infection);

    private void BroadcastRound() => Broadcast(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered, except: null);

    private List<LeaderboardEntry> SortedLeaderboard() => best.Values.OrderBy(e => e.BestMs).ToList();

    private List<StandingEntry> SortedStandings() =>
        standings.Values.OrderByDescending(e => e.Points).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void BroadcastLeaderboard() =>
        Broadcast(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered, except: null);

    private void BroadcastStandings() =>
        Broadcast(Msg.Standings(writer, SortedStandings()), DeliveryMethod.ReliableOrdered, except: null);

    private void BroadcastEvent(string code, string argument) =>
        Broadcast(Msg.Event(writer, code, argument), DeliveryMethod.ReliableOrdered, except: null);

    private void OnFinish(Player p, int finishRoundId, uint timeMs)
    {
        string reject = "";
        double elapsed = Now - phaseStart;
        if (phase != RoundPhase.Playing || finishRoundId != roundId)
        {
            reject = "outside of a round";
        }
        else if (IsHideAndSeek)
        {
            reject = "no times in Hide and Seek";
        }
        else if (p.IsSpectator)
        {
            reject = "spectating";
        }
        else if (IsRoundsMode && (!participants.Contains(p.Id) || gaveUp.Contains(p.Id) || best.ContainsKey(p.Id)))
        {
            reject = "not racing in this round (one attempt per round)";
        }
        else if (!Msg.MapMatches(p.MapKey, CurrentMap))
        {
            reject = $"wrong map ({p.MapKey})";
        }
        else if (timeMs == 0 || timeMs / 1000.0 > elapsed + FinishToleranceSeconds)
        {
            // A run can't last longer than the round has been running.
            reject = $"impossible time ({Msg.FormatTime(timeMs)} while the round started {elapsed:0.0} s ago)";
        }

        if (reject != "")
        {
            Log($"Time rejected for {p.Name}: {reject}");
            return;
        }

        firstFinishAt ??= Now;
        bool improved = !best.TryGetValue(p.Id, out LeaderboardEntry previous) || timeMs < previous.BestMs;
        Log($"{p.Name}: {Msg.FormatTime(timeMs)}{(improved ? " (personal best)" : "")}");
        if (improved)
        {
            best[p.Id] = new LeaderboardEntry(p.Id, p.Name, timeMs);
            BroadcastLeaderboard();
        }
    }

    private void LogResults()
    {
        if (IsHideAndSeek)
        {
            return; // no times: the finds and the surviving mice are already in the log
        }
        var board = SortedLeaderboard();
        Log($"Round {roundId} results ({CurrentMap}):" + (board.Count == 0 ? " no times" : ""));
        for (int i = 0; i < board.Count; i++)
        {
            Log($"  {i + 1}. {board[i].Name} - {Msg.FormatTime(board[i].BestMs)}");
        }
    }

    /// <summary>Writes the round results (JSON) and the match standings (CSV) for tournament organizers.</summary>
    private void ExportResults()
    {
        if (string.IsNullOrWhiteSpace(options.ResultsDirectory))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(options.ResultsDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeMap = string.Concat(CurrentMap.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var board = SortedLeaderboard();
            var round = new
            {
                Round = roundId,
                Map = CurrentMap,
                EndedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                Results = board.Select((e, i) => new
                {
                    Rank = i + 1,
                    e.Name,
                    TimeMs = e.BestMs,
                    Time = Msg.FormatTime(e.BestMs),
                    Points = i < options.PointsTable.Count ? options.PointsTable[i] : 0,
                }),
            };
            File.WriteAllText(Path.Combine(options.ResultsDirectory, $"{stamp}_round{roundId}_{safeMap}.json"),
                JsonSerializer.Serialize(round, ExportJson));

            var csv = new StringBuilder("Rank,Name,Points,RoundsPlayed,Status\n");
            int rank = 1;
            foreach (StandingEntry e in SortedStandings())
            {
                csv.Append(rank++).Append(',').Append(CsvField(e.Name)).Append(',').Append(e.Points).Append(',').Append(e.RoundsPlayed).Append(',').Append(e.Status).Append('\n');
            }
            File.WriteAllText(Path.Combine(options.ResultsDirectory, "match_standings.csv"), csv.ToString());
        }
        catch (Exception e)
        {
            Log($"Could not export results: {e.Message}");
        }
    }

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        // Keep "+01:00" and accented names readable in the exported files.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string CsvField(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    // ---------- Admin ----------

    private void OnAdminLogin(Player p, string password)
    {
        bool ok = options.AdminPassword != "" && password == options.AdminPassword;
        if (ok && !p.IsAdmin)
        {
            p.IsAdmin = true;
            Log($"{p.Name} (#{p.Id}) logged in as admin");
            BroadcastEvent(ServerEvents.AdminLoggedIn, p.Name);
        }
        else if (!ok)
        {
            Log($"Failed admin login from {p.Name} (#{p.Id})");
        }
        p.Peer.Send(Msg.AdminStatus(writer, p.IsAdmin, p.IsAdmin ? options.Maps : Array.Empty<string>()), DeliveryMethod.ReliableOrdered);
        if (p.IsAdmin)
        {
            p.Peer.Send(Msg.Settings(writer, options.ToMatchSettings(), saveToFile: false), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>An admin changed the match settings in game.</summary>
    private void OnSettings(Player p, string patchJson, bool saveToFile)
    {
        if (!p.IsAdmin)
        {
            Reply(p, CommandResults.NotAdmin, "");
            return;
        }
        // Only what the admin changed is sent: settings their mod version doesn't know are kept.
        MatchSettings settings;
        try
        {
            settings = SettingsPatch.Merge(options.ToMatchSettings(), patchJson);
        }
        catch (System.Text.Json.JsonException e)
        {
            Reply(p, CommandResults.InvalidSettings, e.Message);
            return;
        }
        GameMode oldMode = options.Mode;
        var oldMaps = new List<string>(options.Maps);
        string? error = options.Apply(settings);
        if (error != null)
        {
            Reply(p, CommandResults.InvalidSettings, error);
            return;
        }
        Log($"Settings changed by {p.Name}");
        LogSettings();

        if (saveToFile)
        {
            if (configPath == null)
            {
                Reply(p, CommandResults.NoConfigFile, "");
            }
            else
            {
                try
                {
                    options.Save(configPath);
                    Log($"Settings saved to {configPath}");
                }
                catch (Exception e)
                {
                    Log($"Could not save {configPath}: {e.Message}");
                    Reply(p, CommandResults.InvalidSettings, e.Message);
                }
            }
        }

        // A new mode or map list restarts the match; durations and points apply from the next round.
        if (options.Mode != oldMode || !options.Maps.SequenceEqual(oldMaps))
        {
            ResetMatch();
            if (HasRotation)
            {
                StartRound(0, newMap: true);
            }
        }
        BroadcastEvent(ServerEvents.SettingsChanged, p.Name);
        foreach (Player admin in players.Values.Where(o => o.IsAdmin))
        {
            admin.Peer.Send(Msg.AdminStatus(writer, true, options.Maps), DeliveryMethod.ReliableOrdered);
            admin.Peer.Send(Msg.Settings(writer, options.ToMatchSettings(), saveToFile: false), DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnAdminCommand(Player p, AdminCommand command, int argument)
    {
        if (!p.IsAdmin)
        {
            Log($"Admin command {command} refused for {p.Name} (#{p.Id}): not logged in");
            Reply(p, CommandResults.NotAdmin, "");
            return;
        }
        var (code, arg) = ExecuteAdmin(p, command, argument);
        if (code != CommandResults.Done)
        {
            Reply(p, code, arg);
        }
    }

    /// <summary>Runs an admin command. Returns a <see cref="CommandResults"/> code and its argument.</summary>
    private (string Code, string Arg) ExecuteAdmin(Player? admin, AdminCommand command, int argument)
    {
        string by = admin?.Name ?? "server";
        bool inRound = phase != RoundPhase.Free;
        switch (command)
        {
            case AdminCommand.NextPhase:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                if (phase == RoundPhase.Preparing)
                {
                    BeginCountdown(); // don't wait for the missing players
                }
                else if (phase == RoundPhase.Playing)
                {
                    EndRound();
                }
                else
                {
                    AdvanceAfterResults();
                }
                BroadcastEvent(ServerEvents.Skipped, by);
                break;

            case AdminCommand.RestartRound:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                StartRound(mapIndex, newMap: false);
                BroadcastEvent(ServerEvents.Restarted, by);
                break;

            case AdminCommand.TogglePause:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                if (paused)
                {
                    paused = false;
                    phaseEnd = Now + pausedRemaining;
                }
                else
                {
                    paused = true;
                    pausedRemaining = Math.Max(0, phaseEnd - Now);
                }
                BroadcastRound();
                BroadcastEvent(paused ? ServerEvents.Paused : ServerEvents.Resumed, by);
                break;

            case AdminCommand.Extend:
                if (phase != RoundPhase.Playing)
                {
                    return (CommandResults.NoRound, "");
                }
                if (argument <= 0)
                {
                    return (CommandResults.Usage, "/extend <seconds>");
                }
                if (paused)
                {
                    pausedRemaining += argument;
                }
                else
                {
                    phaseEnd += argument;
                }
                BroadcastRound();
                BroadcastEvent(ServerEvents.Extended, argument.ToString(CultureInfo.InvariantCulture));
                break;

            case AdminCommand.GotoMap:
                if (argument < 0 || argument >= options.Maps.Count)
                {
                    return (CommandResults.BadMap, options.Maps.Count.ToString(CultureInfo.InvariantCulture));
                }
                StartRound(argument, newMap: true);
                BroadcastEvent(ServerEvents.MapChosen, by);
                break;

            case AdminCommand.Kick:
                if (!players.TryGetValue(argument, out Player? target))
                {
                    return (CommandResults.NoPlayer, $"#{argument}");
                }
                Log($"{target.Name} (#{target.Id}) kicked by {by}");
                BroadcastEvent(ServerEvents.PlayerKicked, target.Name);
                target.Peer.Disconnect(ServerReasons.Kicked);
                break;

            case AdminCommand.ResetMatch:
                ResetMatch();
                BroadcastEvent(ServerEvents.MatchReset, by);
                break;

            case AdminCommand.SetHunter:
                if (argument < 0)
                {
                    nextHunter = null;
                    break;
                }
                if (!players.TryGetValue(argument, out Player? chosen))
                {
                    return (CommandResults.NoPlayer, $"#{argument}");
                }
                nextHunter = chosen.Name;
                break;

            default:
                return (CommandResults.Unknown, command.ToString());
        }
        Log($"Admin: {command}{(argument != 0 ? $" {argument}" : "")} by {by}");
        return (CommandResults.Done, "");
    }

    // ---------- Chat ----------

    private const int ChatBurst = 5;
    private const double ChatWindowSeconds = 5;

    private void OnChat(Player p, string raw)
    {
        string text = Msg.SanitizeChat(raw);
        if (text.Length == 0)
        {
            return;
        }

        // Anti-spam: at most ChatBurst messages per ChatWindowSeconds.
        while (p.RecentChat.Count > 0 && Now - p.RecentChat.Peek() > ChatWindowSeconds)
        {
            p.RecentChat.Dequeue();
        }
        if (p.RecentChat.Count >= ChatBurst)
        {
            Reply(p, CommandResults.TooFast, "");
            return;
        }
        p.RecentChat.Enqueue(Now);

        if (text.StartsWith('/'))
        {
            OnChatCommand(p, text);
            return;
        }
        Log($"[chat] {p.Name}: {text}");
        Broadcast(Msg.ChatToClient(writer, p.Id, p.Name, text), DeliveryMethod.ReliableOrdered, except: null);
    }

    private void Reply(Player p, string code, string argument) =>
        p.Peer.Send(Msg.Reply(writer, code, argument), DeliveryMethod.ReliableOrdered);

    /// <summary>Chat commands. Map numbers are 1-based for players (the console uses 0-based indexes).</summary>
    private void OnChatCommand(Player p, string text)
    {
        string[] parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (command)
        {
            case "help":
                Reply(p, CommandResults.Help, p.IsAdmin ? "admin" : "player");
                return;
            case "login":
                OnAdminLogin(p, arg);
                return;
        }

        AdminCommand? admin = command switch
        {
            "next" or "skip" => AdminCommand.NextPhase,
            "restart" => AdminCommand.RestartRound,
            "pause" or "resume" => AdminCommand.TogglePause,
            "extend" => AdminCommand.Extend,
            "map" => AdminCommand.GotoMap,
            "kick" => AdminCommand.Kick,
            "resetmatch" => AdminCommand.ResetMatch,
            "hunter" => AdminCommand.SetHunter,
            _ => null,
        };
        if (admin is not AdminCommand cmd)
        {
            Reply(p, CommandResults.Unknown, "/" + command);
            return;
        }
        if (!p.IsAdmin)
        {
            Reply(p, CommandResults.NotAdmin, "");
            return;
        }

        int argument = 0;
        switch (cmd)
        {
            case AdminCommand.Extend:
                argument = arg == "" ? 60 : int.TryParse(arg, out int seconds) ? seconds : -1;
                break;
            case AdminCommand.GotoMap:
                if (!int.TryParse(arg, out int number))
                {
                    Reply(p, CommandResults.Usage, "/map <number>");
                    return;
                }
                argument = number - 1;
                break;
            case AdminCommand.Kick:
                Player? target = FindPlayer(arg);
                if (target == null)
                {
                    Reply(p, arg == "" ? CommandResults.Usage : CommandResults.NoPlayer, arg == "" ? "/kick <name>" : arg);
                    return;
                }
                argument = target.Id;
                break;
            case AdminCommand.SetHunter:
                if (arg == "")
                {
                    argument = -1; // random
                    break;
                }
                Player? hunter = FindPlayer(arg);
                if (hunter == null)
                {
                    Reply(p, CommandResults.NoPlayer, arg);
                    return;
                }
                argument = hunter.Id;
                break;
        }

        var (code, resultArg) = ExecuteAdmin(p, cmd, argument);
        if (code != CommandResults.Done)
        {
            Reply(p, code, resultArg);
        }
    }

    /// <summary>Finds a player by "#id", exact name, or unique name prefix (case-insensitive).</summary>
    private Player? FindPlayer(string query)
    {
        if (query == "")
        {
            return null;
        }
        var named = players.Values.Where(o => o.HasSaidHello).ToList();
        if (query.StartsWith('#') && int.TryParse(query[1..], out int id))
        {
            return named.FirstOrDefault(o => o.Id == id);
        }
        var exact = named.FirstOrDefault(o => string.Equals(o.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }
        var prefix = named.Where(o => o.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    // ---------- LiteNetLib events ----------

    /// <summary>
    /// Why a join must be refused, or null to accept it. Shared by the UDP listener and the web room host,
    /// so both transports give the same answers. The website checks room passwords itself, so web joins skip that step.
    /// </summary>
    public string? RefuseReason(string key, string password, bool checkPassword = true)
    {
        if (players.Count >= options.MaxPlayers)
        {
            return ServerReasons.Full;
        }
        if (key != ProtocolInfo.ConnectionKey)
        {
            return $"{ServerReasons.VersionMismatch}:{ProtocolInfo.Version}";
        }
        if (checkPassword && options.Password != "" && password != options.Password)
        {
            return ServerReasons.BadPassword;
        }
        return null;
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        string key = request.Data.AvailableBytes > 0 ? request.Data.GetString(64) : "";
        string password = request.Data.AvailableBytes > 0 ? request.Data.GetString(128) : "";
        string? refusal = RefuseReason(key, password);
        if (refusal != null)
        {
            // A full server is normal; the other refusals are worth a line in the log.
            if (refusal != ServerReasons.Full)
            {
                Log($"Connection refused from {request.RemoteEndPoint}: {refusal}");
            }
            request.Reject(NetDataWriter.FromString(refusal));
            return;
        }
        request.Accept();
    }

    public void OnPeerConnected(NetPeer peer) => AddPlayer(new UdpPeer(peer));

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) => RemovePlayer(peer.Id, info.Reason.ToString());

    /// <summary>A player joined through a web room. The room host has already called <see cref="RefuseReason"/>.</summary>
    public void OnWebPeerConnected(IPeer peer) => AddPlayer(peer);

    public void OnWebPeerDisconnected(IPeer peer, string reason) => RemovePlayer(peer.Id, reason);

    /// <summary>One message from a web room player. Runs on the game thread.</summary>
    public void OnWebMessage(IPeer peer, byte[] data) => Receive(peer.Id, new NetDataReader(data));

    private void AddPlayer(IPeer peer)
    {
        players[peer.Id] = new Player { Peer = peer };
        Log($"Connection from {peer} (#{peer.Id})");
    }

    private void RemovePlayer(int id, string reason)
    {
        if (!players.Remove(id, out Player? p))
        {
            return;
        }
        Log($"Disconnection of {p.Name} (#{p.Id}): {reason}");
        lastPosition.Remove(p.Id);
        lastStateAt.Remove(p.Id);
        OnLeftHunt(p);
        if (p.HasSaidHello)
        {
            Broadcast(Msg.PlayerLeft(writer, p.Id), DeliveryMethod.ReliableOrdered, except: null);
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method) =>
        Receive(peer.Id, reader);

    private void Receive(int id, NetDataReader reader)
    {
        if (!players.TryGetValue(id, out Player? p) || reader.AvailableBytes < 1)
        {
            return;
        }

        try
        {
            var type = (MessageType)reader.GetByte();
            if (!p.HasSaidHello && type != MessageType.Hello)
            {
                return;
            }

            switch (type)
            {
                case MessageType.Hello:
                    OnHello(p, reader);
                    break;
                case MessageType.MapChanged:
                    OnMapChanged(p, reader.GetString(256));
                    break;
                case MessageType.State:
                    OnState(p, Msg.GetCompact(reader));
                    break;
                case MessageType.SkinChanged:
                    p.Skin = reader.GetInt();
                    Broadcast(Msg.SkinChanged(writer, p.Id, p.Skin), DeliveryMethod.ReliableOrdered, except: p);
                    if (options.Echo)
                    {
                        p.Peer.Send(Msg.SkinChanged(writer, EchoIdOffset + p.Id, p.Skin), DeliveryMethod.ReliableOrdered);
                    }
                    break;
                case MessageType.Finish:
                    OnFinish(p, reader.GetInt(), reader.GetUInt());
                    break;
                case MessageType.AdminLogin:
                    OnAdminLogin(p, reader.GetString(128));
                    break;
                case MessageType.AdminCommand:
                    OnAdminCommand(p, (AdminCommand)reader.GetByte(), reader.GetInt());
                    break;
                case MessageType.Chat:
                    OnChat(p, reader.GetString(ProtocolInfo.MaxChatLength * 4));
                    break;
                case MessageType.Ready:
                    OnReady(p, reader.GetInt());
                    break;
                case MessageType.GiveUp:
                    OnGiveUp(p, reader.GetInt());
                    break;
                case MessageType.Spectate:
                    OnSpectate(p, reader.GetBool());
                    break;
                case MessageType.Tag:
                    OnTag(p, reader.GetInt(), reader.GetInt());
                    break;
                case MessageType.Settings:
                {
                    var (json, save) = Msg.GetSettingsJson(reader);
                    OnSettings(p, json, save);
                    break;
                }
                default:
                    // Probably a newer client: its extra messages are ignored (logged once per type).
                    if (p.UnknownMessages.Add(type))
                    {
                        Log($"Ignored message type {(byte)type} from {p.Name} (#{p.Id}, mod {p.Capabilities.Version})");
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            // A malformed packet (or a bug) must never stop the server for everyone.
            Log($"Invalid packet from #{p.Id}: {e.GetType().Name}: {e.Message}");
        }
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) =>
        Log($"Network error {endPoint}: {socketError}");

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // ---------- Players ----------

    private void OnHello(Player p, NetDataReader reader)
    {
        if (p.HasSaidHello)
        {
            return;
        }
        var (name, skin, capabilities) = Msg.GetHello(reader);
        p.Name = Msg.SanitizeName(name);
        p.Skin = skin;
        p.Capabilities = capabilities;
        p.HasSaidHello = true;

        var others = players.Values.Where(o => o.HasSaidHello && o != p).Select(o => o.Info).ToList();
        p.Peer.Send(Msg.Welcome(writer, p.Id, others, ProtocolInfo.OwnCapabilities), DeliveryMethod.ReliableOrdered);
        Broadcast(Msg.PlayerJoined(writer, p.Info), DeliveryMethod.ReliableOrdered, except: p);

        if (options.Echo)
        {
            p.Peer.Send(Msg.PlayerJoined(writer, new PlayerInfo(EchoIdOffset + p.Id, $"Echo {p.Name}", p.MapKey, p.Skin)), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) joined the game with mod {Msg.SanitizeName(capabilities.Version)} ({players.Count} connected)");

        // Round in progress: the newcomer gets the imposed map, the leaderboard and the match standings.
        if (phase != RoundPhase.Free)
        {
            p.Peer.Send(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered);
            p.Peer.Send(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered);
            if (IsHideAndSeek)
            {
                p.Peer.Send(Msg.HideSeek(writer, CurrentHideSeek()), DeliveryMethod.ReliableOrdered);
            }
        }
        if (standings.Count > 0)
        {
            p.Peer.Send(Msg.Standings(writer, SortedStandings()), DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnMapChanged(Player p, string mapKey)
    {
        p.MapKey = mapKey;
        Broadcast(Msg.MapChanged(writer, p.Id, mapKey), DeliveryMethod.ReliableOrdered, except: p);
        if (options.Echo)
        {
            p.Peer.Send(Msg.MapChanged(writer, EchoIdOffset + p.Id, mapKey), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) → {(mapKey == "" ? "menu" : mapKey)}");

        // Hide and Seek: a mouse leaving the map during the hunt can't be found anymore: it's out.
        if (IsHideAndSeek && phase == RoundPhase.Playing && Now >= SeekStart && mice.ContainsKey(p.Id) && !found.Contains(p.Id)
            && !Msg.MapMatches(mapKey, CurrentMap))
        {
            MouseOut(p.Id, p.Name);
            BroadcastEvent(ServerEvents.MouseForfeit, p.Name);
        }
    }

    // Board states are not relayed one by one: they are grouped every StatesFlushSeconds into one packet per
    // recipient, which only contains the players on the recipient's map (the others wouldn't be shown anyway).
    private const double StatesFlushSeconds = 0.01;
    private readonly List<(Player Sender, PlayerState State)> pendingStates = new();
    private readonly List<(int Id, PlayerState State)> batch = new();
    private double lastStatesFlush;

    private void OnState(Player p, PlayerState state)
    {
        if (p.IsSpectator)
        {
            return; // spectators are invisible
        }
        lastPosition[p.Id] = state.Position;
        lastStateAt[p.Id] = Now;
        pendingStates.Add((p, state));
        if (options.Echo)
        {
            echoQueue.Enqueue((Now + options.EchoDelaySeconds, p.Id, state));
        }
    }

    private void FlushStates()
    {
        lastStatesFlush = Now;
        if (pendingStates.Count == 0)
        {
            return;
        }
        foreach (Player recipient in players.Values)
        {
            if (!recipient.HasSaidHello || recipient.MapKey == "")
            {
                continue;
            }
            batch.Clear();
            foreach (var (sender, state) in pendingStates)
            {
                if (sender != recipient && sender.MapKey == recipient.MapKey && players.ContainsKey(sender.Id))
                {
                    batch.Add((sender.Id, state));
                }
            }
            for (int start = 0; start < batch.Count; start += Msg.MaxStatesPerPacket)
            {
                recipient.Peer.Send(Msg.States(writer, batch, start, Math.Min(Msg.MaxStatesPerPacket, batch.Count - start)), DeliveryMethod.Unreliable);
            }
        }
        pendingStates.Clear();
    }

    private void FlushEcho()
    {
        while (echoQueue.Count > 0 && echoQueue.Peek().Due <= Now)
        {
            var (_, peerId, state) = echoQueue.Dequeue();
            if (players.TryGetValue(peerId, out Player? p))
            {
                batch.Clear();
                batch.Add((EchoIdOffset + peerId, state));
                p.Peer.Send(Msg.States(writer, batch, 0, 1), DeliveryMethod.Unreliable);
            }
        }
    }

    private void Broadcast(NetDataWriter w, DeliveryMethod method, Player? except)
    {
        foreach (Player o in players.Values)
        {
            if (o != except && o.HasSaidHello)
            {
                o.Peer.Send(w, method);
            }
        }
    }

    /// <summary>Where the server writes its log: the console for the dedicated server, the mod's log when hosted in game.</summary>
    public static Action<string> LogSink { get; set; } = line => Console.WriteLine(line);

    private static void Log(string message) => LogSink($"[{DateTime.Now:HH:mm:ss}] {message}");
}
