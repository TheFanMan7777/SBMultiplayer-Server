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
public sealed class GameServer
{
    private const int EchoIdOffset = 1_000_000;

    /// <summary>Accepted margin between a run's duration and the time elapsed since the round started (latency, loading).</summary>
    private const double FinishToleranceSeconds = 3;

    private sealed class Player
    {
        public required GamePeer Peer { get; init; }
        public int Id => Peer.Id;
        public string Name { get; set; } = "";
        public string MapKey { get; set; } = "";
        public bool HasSaidHello { get; set; }
        public int Skin { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSpectator { get; set; }
        public PlayerInfo Info => new(Id, Name, MapKey, Skin, IsSpectator);

        /// <summary>Takes part in rounds: connected, not spectating.</summary>
        public bool IsPlaying => HasSaidHello && !IsSpectator;
        public Queue<double> RecentChat { get; } = new();
    }

    private readonly ServerOptions options;
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

    /// <summary>Best time of each player in the current round (the name is kept if the player leaves).</summary>
    private readonly Dictionary<int, LeaderboardEntry> best = new();

    /// <summary>Match standings, by player name (players get a new id when they reconnect).</summary>
    private readonly Dictionary<string, StandingEntry> standings = new();

    public GameServer(ServerOptions options, string? configPath = null)
    {
        this.options = options;
        this.configPath = configPath;
    }

    public int PlayerCount => players.Count;

    private bool HasRotation => options.Maps.Count > 0 && options.Mode != GameMode.Free;

    private string CurrentMap => phase == RoundPhase.Free ? "" : options.Maps[mapIndex];

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Logs the room's settings. The room's players connect through <see cref="RoomHost"/>.</summary>
    public void Start()
    {
        Log($"\"{options.ServerName}\" room started (protocol v{ProtocolInfo.Version}, {options.MaxPlayers} players max"
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
                  + (options.Mode == GameMode.Elimination ? $", {options.EliminatedPerRound} eliminated per round" : "")
                : $"Time Attack - rotation: {string.Join(", ", options.Maps)} - {options.RoundSeconds:0} s rounds, {options.ResultsSeconds:0} s results")
            : "Free mode: no rounds, everyone rides wherever they want");
    }

    public void Poll()
    {
        if (options.Echo)
        {
            FlushEcho();
        }
        UpdateRound();
    }

    /// <summary>Disconnects everyone in the room.</summary>
    public void Stop()
    {
        foreach (Player p in players.Values.ToList())
        {
            p.Peer.Disconnect("server stopped");
        }
        players.Clear();
    }

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
        else if (phase == RoundPhase.Playing && IsRoundsMode && RoundsRaceIsOver())
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
        startAt = null;
        firstFinishAt = null;
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
    }

    /// <summary>Rounds mode: everyone ready (or timeout). The players ready now are the participants of the race.</summary>
    private void BeginCountdown()
    {
        phase = RoundPhase.Playing;
        participants.UnionWith(ready);
        startAt = Now + options.CountdownSeconds;
        phaseStart = startAt.Value;
        phaseEnd = startAt.Value + options.RoundSeconds;
        Log($"Round {roundId}: start in {options.CountdownSeconds:0} s ({participants.Count} participant(s))");
        BroadcastRound();
    }

    /// <summary>Rounds mode: all participants finished or gave up, or the finish timeout expired.</summary>
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
        var stillRacing = participants.Where(id => players.ContainsKey(id) && !best.ContainsKey(id) && !gaveUp.Contains(id));
        return participants.Count > 0 && !stillRacing.Any();
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
        if (IsRoundsMode && giveUpRoundId == roundId && phase == RoundPhase.Playing && participants.Contains(p.Id)
            && !best.ContainsKey(p.Id) && gaveUp.Add(p.Id))
        {
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
        if (options.Mode == GameMode.Rounds && mapIndex == options.Maps.Count - 1 && roundInMap >= Math.Max(1, options.RoundsPerMap))
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
    /// Elimination: the last EliminatedPerRound players are eliminated (players who gave up count as last),
    /// but at least one player always survives. Points = rounds survived. Last player standing wins.
    /// </summary>
    private void ApplyElimination()
    {
        var finishers = SortedLeaderboard().Where(e => participants.Contains(e.PlayerId)).Select(e => e.Name).ToList();
        var racers = participants.Where(players.ContainsKey).Select(id => players[id].Name).ToList();
        if (finishers.Count == 0)
        {
            return; // nobody finished: nobody is eliminated this round
        }

        // The last N players are eliminated; players who gave up count as last (all of them are eliminated).
        // Slowest finishers complete the quota, but at least one player always survives.
        var out_ = racers.Where(n => !finishers.Contains(n)).ToList();
        int survivorsIfDnfOut = racers.Count - out_.Count;
        int slowest = Math.Min(Math.Max(0, options.EliminatedPerRound - out_.Count), Math.Max(0, survivorsIfDnfOut - 1));
        out_.AddRange(finishers.Skip(finishers.Count - slowest));

        foreach (string name in racers)
        {
            standings.TryGetValue(name, out StandingEntry previous);
            bool isOut = out_.Contains(name);
            if (isOut)
            {
                eliminated.Add(name);
            }
            standings[name] = new StandingEntry(name, previous.Points + (isOut ? 0 : 1), previous.RoundsPlayed + 1,
                isOut ? StandingStatus.Eliminated : StandingStatus.Normal);
        }
        if (out_.Count > 0)
        {
            Log($"Eliminated: {string.Join(", ", out_)}");
            BroadcastEvent(ServerEvents.Eliminated, string.Join(", ", out_));
        }

        var remaining = players.Values.Where(p => p.IsPlaying && !eliminated.Contains(p.Name)).ToList();
        if (remaining.Count <= 1 && !matchOver)
        {
            matchOver = true;
            string winner = remaining.FirstOrDefault()?.Name ?? finishers[0];
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
            MatchEnded: phase == RoundPhase.Results && matchEnded);

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
    private void OnSettings(Player p, MatchSettings settings, bool saveToFile)
    {
        if (!p.IsAdmin)
        {
            Reply(p, CommandResults.NotAdmin, "");
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


    public void OnPeerConnected(GamePeer peer)
    {
        players[peer.Id] = new Player { Peer = peer };
        Log($"Connection from {peer} (#{peer.Id})");
    }

    public void OnPeerDisconnected(GamePeer peer, string reason)
    {
        if (!players.Remove(peer.Id, out Player? p))
        {
            return;
        }
        Log($"Disconnection of {p.Name} (#{p.Id}): {reason}");
        if (p.HasSaidHello)
        {
            Broadcast(Msg.PlayerLeft(writer, p.Id), DeliveryMethod.ReliableOrdered, except: null);
        }
    }

    public void OnNetworkReceive(GamePeer peer, NetDataReader reader)
    {
        if (!players.TryGetValue(peer.Id, out Player? p) || reader.AvailableBytes < 1)
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
                    OnState(p, Msg.GetState(reader));
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
                case MessageType.Settings:
                {
                    var (settings, save) = Msg.GetSettings(reader);
                    OnSettings(p, settings, save);
                    break;
                }
                default:
                    Log($"Unexpected message {type} from #{p.Id}");
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            Log($"Invalid packet from #{p.Id}: {e.Message}");
        }
    }




    // ---------- Players ----------

    private void OnHello(Player p, NetDataReader reader)
    {
        if (p.HasSaidHello)
        {
            return;
        }
        p.Name = Msg.SanitizeName(reader.GetString(ProtocolInfo.MaxNameLength * 4));
        p.Skin = reader.GetInt();
        p.HasSaidHello = true;

        var others = players.Values.Where(o => o.HasSaidHello && o != p).Select(o => o.Info).ToList();
        p.Peer.Send(Msg.Welcome(writer, p.Id, others), DeliveryMethod.ReliableOrdered);
        Broadcast(Msg.PlayerJoined(writer, p.Info), DeliveryMethod.ReliableOrdered, except: p);

        if (options.Echo)
        {
            p.Peer.Send(Msg.PlayerJoined(writer, new PlayerInfo(EchoIdOffset + p.Id, $"Echo {p.Name}", p.MapKey, p.Skin)), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) joined the game ({players.Count} connected)");

        // Round in progress: the newcomer gets the imposed map, the leaderboard and the match standings.
        if (phase != RoundPhase.Free)
        {
            p.Peer.Send(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered);
            p.Peer.Send(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered);
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
    }

    private void OnState(Player p, PlayerState state)
    {
        // Everyone receives every state; clients only show players on their own map.
        if (p.IsSpectator)
        {
            return; // spectators are invisible
        }
        Broadcast(Msg.State(writer, p.Id, state), DeliveryMethod.Unreliable, except: p);
        if (options.Echo)
        {
            echoQueue.Enqueue((Now + options.EchoDelaySeconds, p.Id, state));
        }
    }

    private void FlushEcho()
    {
        while (echoQueue.Count > 0 && echoQueue.Peek().Due <= Now)
        {
            var (_, peerId, state) = echoQueue.Dequeue();
            if (players.TryGetValue(peerId, out Player? p))
            {
                p.Peer.Send(Msg.State(writer, EchoIdOffset + peerId, state), DeliveryMethod.Unreliable);
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
