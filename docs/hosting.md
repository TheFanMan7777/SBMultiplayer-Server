# Hosting a server

## The quick way: host from the game

In the game, open **F8 > Host a game**, choose the UDP port (24800 by default) and click **Host**. The server runs inside your game (it keeps running when the game window loses focus), you're connected to it automatically and you're its **admin**: set the mode and the maps in F8 > Admin. The menu shows the addresses your friends should use.

**Steam friends can join without opening any port**: with "Steam friends can join without opening a port (Steam P2P)" checked (default), your game appears in their **F8 > Join a Steam friend** list while they play STUNTBOOST, and one click connects them through Steam (NAT traversal and Steam's relays). Other players can still join with your IP address and an open port.

**Public games**: tick "Public game (anyone on Steam)" (it needs Steam P2P) and your game appears in everyone's **F8 > Public games** list, with its name, mode, map and number of players: anyone on Steam can join it in one click, the same way. It's listed through a Steam lobby, so nothing else has to be hosted. Set a password in `hosted-server.json` if you only want some players to come in. Dedicated servers aren't listed (they don't run Steam): share their address.

Its settings are kept in `SBMultiplayer/hosted-server.json` (same format as below). The game stops when you click "Stop hosting" or quit the game. For a server that runs all the time, or for tournaments, prefer the dedicated server below. The network setup (firewall, port forwarding) is the same.

## The dedicated server

Download `SBMultiplayer-Server-<version>.zip` from the [releases](https://github.com/AsLirith/SBMultiplayerMod/releases/latest) (requires the .NET 8 runtime or newer), or build it from source (`dotnet run --project src/SBMultiplayer.Server -c Release -- <options>`).

Servers from 0.19.0 on accept players with any later mod version (and players with 0.19.0 can join newer servers), so you don't have to update the server and every player at the same time.

The easiest way to start is to generate a fresh config file and start the server :

```sh
dotnet SBMultiplayer.Server.dll --init-config server.json   # writes a commented template
# edit server.json (maps, passwords, durations...) (or don't touch anything, you can edit the config in game when the server is started if you're not comfortable with config files), then:
dotnet SBMultiplayer.Server.dll --config server.json
```

Every setting can also be given (or overridden) on the command line:

| Option | Config key | Default | Description |
|---|---|---|---|
| `--port <n>` | `Port` | 24800 | UDP port |
| `--max-players <n>` | `MaxPlayers` | 32 | Maximum number of players |
| `--password <text>` | `Password` | (none) | Password required to join |
| `--admin-password <text>` | `AdminPassword` | (none) | Password to use the in-game admin panel (F8 > Admin) |
| `--maps <list>` | `Maps` | (none) | Map rotation, e.g. `o:1,o:2,c:M3456789` (see [Maps](#maps)). Without it, the server runs in free mode: no rounds, everyone rides wherever they want |
| `--mode <mode>` | `Mode` | `TimeAttack` | `Free`, `TimeAttack`, `Rounds`, `Cup` or `Elimination` (see [Game modes](#game-modes)) |
| `--round <s>` | `RoundSeconds` | 300 | Time Attack: round duration. Rounds: time limit after the start |
| `--rounds-per-map <n>` | `RoundsPerMap` | 3 | Rounds mode: rounds played on each map |
| | `FinishTimeoutSeconds` | 15 | Rounds mode: the round ends this long after the first finisher |
| | `PrepareTimeoutSeconds` | 20 | Rounds mode: maximum wait for everyone to be on the start line |
| | `CountdownSeconds` | 3 | Rounds mode: countdown before the start |
| | `CupPointsLimit` | 100 | Cup mode: points needed to become a finalist |
| | `EliminatedPerRound` | 1 | Elimination mode: players eliminated each round |
| | `DnfFillsQuota` | false | Elimination mode: players who don't finish fill the quota, first to give up first (otherwise all of them are eliminated) |
| | `MatchEndSeconds` | 20 | Duration of the end-of-game screen |
| `--results <s>` | `ResultsSeconds` | 15 | Results screen duration in seconds |
| | `PointsTable` | 10, 8, 6, 5, 4, 3, 2, 1 | Match points by rank at the end of each round |
| | `ResultsDirectory` | `results` | Where results are exported (empty = no export) |
| `--echo` | `Echo` | off | Sends each player their own board back with a delay, as a fake "Echo" player, to test alone |

## Maps

The rotation can mix official levels and **Workshop maps**:

- official level: `o:<id>` (any version) or `o:<id>:<version>`;
- Workshop map: `c:M<workshop id>` (latest version, recommended: an update by the author doesn't break the rotation) or the full key with its version.

The easiest way is to set the rotation in game (F8 > Admin > Mode and maps) with level codes: `A1, A5` for official levels, and the game's Workshop codes (`M3456789`) for Workshop maps, or "Add the map I'm on". Players who don't have a Workshop map download it automatically (without subscribing to it) when the round starts.

Local maps (not published on the Workshop) can't be used in a rotation, since the other players don't have them.

A player's current map key (e.g. `o:1:2001`) is shown in the server console when they enter a level.

## Game modes

- **Free**: no rounds, everyone rides wherever they want (the map rotation is kept for later).
- **Time Attack** (default): during the round, everyone retries as much as they want; the best time of each player counts.
- **Rounds**: synchronized start, one attempt per round.
  1. Everyone loads the map and waits on the start line ("Waiting for players x/y"). The countdown starts when everyone is ready, or after `PrepareTimeoutSeconds`.
  2. 3, 2, 1, GO: all players are released at the same time.
  3. Respawning at a checkpoint is allowed. Dying or respawning before the first checkpoint sends you back to the start, but the run goes on: the time lost is added to your finish time. Restarting the level means giving up the round: press the game's restart key (Q by default) **twice quickly or hold it** (the pause menu's Restart works too).
  4. The round ends when nobody is racing anymore (everyone finished, gave up or left), `FinishTimeoutSeconds` after the first finisher, or at the time limit. A player whose game sent nothing for 10 seconds (window in the background...) isn't waited for.
  5. Players are ranked by race time (not by arrival order, which depends on latency) and get points from `PointsTable`. Giving up gives no points.
  6. After `RoundsPerMap` rounds, the next map starts.

  Players who arrive after the start wait on the start line until the next round.
- **Cup**: like Rounds, but reaching `CupPointsLimit` points makes you a **finalist** (your points stop there). A finalist who wins a round **wins the cup**. Then a new match starts.
- **Elimination**: like Rounds, but each round the last `EliminatedPerRound` players are **eliminated**, and at least one player always survives. Players who didn't finish count as last: by default all of them are eliminated, even beyond the quota; with `DnfFillsQuota` they fill the quota, first to give up first (quota 2 and 3 players give up: only the first two are out). Players who miss a round's start (arrived late, game in the background) are eliminated too. Eliminated players watch the others from the start line. The last player standing wins, then a new match starts.
- **Hide and Seek**: one player is the **hunter**, the others are **mice**.
  1. Everyone waits on the start line, like in Rounds. Then the hunter is picked: the player chosen by an admin (F8 > Admin > Next hunter, or `/hunter <name>`), otherwise a random player, never the hunter of the previous round.
  2. The mice leave and have `HideSeconds` to hide. Meanwhile the hunter stays frozen on the start line and can't see them. The board never stops by itself, so a mouse can **lock its hiding place** before the end of the countdown with the game's free camera key (F by default), on a surface.
  3. When the hiding time is over, each mouse is **frozen where it is**, as soon as it touches a surface (never in mid-air). A frozen mouse can use the free camera (F) or follow a player (Left / Right, Down to stop) to watch the hunt, but can't move its board. Leaving the hiding place (restarting, dying, leaving the map) counts as being found, and so does being away from the map when the hunt starts. With `Infection`, mice never see each other.
  4. The hunter has `RoundSeconds` to find the mice: **aim at a mouse and press the catch key** (right click by default, F8 > Keys). The hunter's board must be within 15 cm of the mouse (the game's cm: 1 cm = one board length), with the mouse near the crosshair (there is some room around it) and not behind a wall. The hunter doesn't see name tags and can't use the free camera.
  5. Found mice watch the rest of the hunt, or become hunters too with `Infection`. The round ends when every mouse is found or at the time limit.
  6. **Points**: each mouse found gives its finder `RoundSeconds / number of players` points; each mouse gets 1 point per second spent hidden during the hunt. The match lasts `RoundsPerMap` rounds per map, over the rotation.

  Best on big maps with hiding spots, such as freeroam levels (B7, C9, E6).

## End-of-game screen

At the end of a game, every player sees a full-screen summary with the winner and the final standings, for `MatchEndSeconds`:

- **Time Attack**: at the end of each round (end of the timer): best times, gaps and match points;
- **Rounds**: at the end of the rotation (last round of the last map): match points; then a new match starts;
- **Cup** and **Elimination**: when the winner is decided; then a new match starts;
- **Hide and Seek**: at the end of the rotation, like Rounds.

## Spectators

Players can spectate from the F8 menu ("Spectate"): they stay frozen on the start line, are invisible to the others, aren't waited for or counted in rounds, and their times don't count. They watch through the followed player's own camera (Left / Right to switch player) or the game's free camera (F). In Elimination mode, eliminated players spectate automatically. Useful for casters and organizers.

## Changing the settings in game

Admins can change the match settings without restarting the server: **F8 > Admin**, then:

- **Mode and maps**: game mode, and the map rotation as level codes (`A1, A5, B2`…, or "Add the map I'm on");
- **Timings and points**: round duration / time limit, results screen, rounds per map, end of round after the first finisher, wait for players, countdown, Cup points limit, eliminated per round, points by rank.

Durations and points apply from the next round. Changing the mode or the maps restarts the match on the first map. With "Also save to the server's config file", the config file (`--config`) is rewritten with the new values, keeping its comments; values given on the command line are saved too.

## Running a match

At the end of each round, players get **match points** according to their rank (`PointsTable`). The match standings are shown to players on the results screen, and kept until you reset them.

Admins control the match from the game, either with the admin panel (**F8 > Admin**) or with chat commands (**Enter** opens the chat), after logging in with `AdminPassword` (in the panel, or `/login <password>` in the chat). The server console has the same commands:

| Console | Chat | In-game admin panel | Effect |
|---|---|---|---|
| `next` | `/next` | Next (skip) | Ends the round now (results), skips the results screen, or (Rounds mode) starts the countdown without waiting for missing players |
| `restart` | `/restart` | Restart round | Restarts the current round on the same map (times cleared, no points given) |
| `pause` | `/pause` | Pause / Resume | Freezes or resumes the round timer (players can still ride) |
| `extend <s>` | `/extend [s]` | +1 min | Adds time to the running round (chat: 60 s by default) |
| `map <index>` | `/map <number>` | Go to map | Starts a round on that map of the rotation (console: index from 0, chat: number from 1) |
| `kick <id>` | `/kick <name>` | Kick | Disconnects a player (console: id from `list`; chat: name or start of the name) |
| `resetmatch` | `/resetmatch` | Reset match scores | Clears the match standings |

Everyone can use `/help` in the chat. Chat messages are shown in the server console (`[chat]`), with a limit of 5 messages per 5 seconds per player.

Other console commands: `list` (connected players), `round` (current round), `standings` (match standings), `quit`.

### Exported results

When `ResultsDirectory` is set, the server writes after every round:

- `<date>_round<N>_<map>.json`: rank, name, time and points of every player in that round;
- `match_standings.csv`: the current match standings (rank, name, points, rounds played).

## Network

1. **Firewall** (Linux with ufw): `sudo ufw allow 24800/udp`
2. **Router**: forward **UDP** port 24800 to the server's local IP.
3. Players enter `PUBLIC_IP:24800` as the server address (and the password, if any) in the in-game menu (F8).

### ⚠️ PC connected through both Ethernet AND Wi-Fi

The server's replies leave through the interface of the default route (usually Ethernet). If the router forwards the port to the **Wi-Fi** IP, players reach the server but never receive its replies. In the server log, this shows as `Connection from …` lines followed by `Disconnection of  (#n): Reconnect` every ~5 s, without a player name.

Check which interface is used with `ip route get <player IP>`, and forward the port to that `src` address. You can also just turn the Wi-Fi off.

### Without port forwarding

If your router is behind CGNAT, or port forwarding isn't possible: install [Tailscale](https://tailscale.com/) (or ZeroTier) on the server and on the players' machines, then use the server's Tailscale IP.

## Tested

2026-11-14: 3 players (Linux, over the internet), 6 official maps. Smooth, with only the intended 100 ms interpolation delay.

## Web rooms (website, no open port)

Players can also create a room on the website, with a code to share and an optional password. Their game goes through the
website's WebSocket, so the host doesn't open a port. The room host runs the rooms behind one HTTP port:

```sh
export SBM_GATEWAY_SECRET="<at least 32 characters, the same value the website uses>"
dotnet SBMultiplayer.Server.dll --rooms 9000 --max-rooms 20
```

| Option | Default | Description |
|---|---|---|
| `--rooms <port>` | (off) | Serve web rooms over HTTP on this port. Without it, the server runs as a normal UDP server |
| `--max-rooms <n>` | 20 | Most rooms open at once |

Each room is a game server of its own, with the same settings as a UDP server. The room's password is checked by the
website, not by the room host.
