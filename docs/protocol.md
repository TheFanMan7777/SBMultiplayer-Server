# Network protocol and compatibility

Since 0.19.0 (protocol 13), players and servers no longer have to update together: a newer mod plays with an older
server and the other way around. This page lists the rules that keep it that way. Every change to
`src/SBMultiplayer.Protocol/Messages.cs` must follow them.

## The promise

- `ProtocolInfo.Version` (13) only changes for a breaking change. That would be a 2.0, and it's announced as such.
- Every 1.x mod (from 0.19.0) connects to every 1.x server.
- Something one side doesn't know is skipped or hidden. It never cuts the connection.

## Allowed changes (no protocol bump)

**New field in an existing message**: append it **at the end** of the message, after every existing field.
Read it with the `OptionalFields` helpers (`GetIntOr`, `GetStringOr`...). An older peer sends the message without the
field, and LiteNetLib doesn't fail when reading past the end of a packet: it returns garbage. So never read a new
field with a plain `GetInt()`.

**New field in a list element** (players in `Welcome`, `Leaderboard` and `MatchStandings` entries, `States`
entries): every element is written as a block (`BeginBlock`/`EndBlock`, or `BeginShortBlock` for small frequent
elements). Append the field at the end of the block, and read it with the `OptionalFields` helpers on the block's
reader. Older readers skip what they don't know.

**New message type**: add a value to `MessageType`. Older clients and servers ignore unknown types (the server logs
them once per player). If the feature can't work without the other side, add a capability (see below), and check
it before sending or showing the feature.

**New server event or command reply**: add a code to `ServerEvents` / `CommandResults`, and pass an English
`fallback` text to `Msg.Event` / `Msg.Reply`. Older clients show that text instead of the translation.

**New game mode**: add a value to `GameMode` and to `GameModes.Known`. The server announces its modes in `Welcome`, so
admins with a newer mod only see the modes the server can run. A client that doesn't know the server's mode becomes
a spectator automatically and asks the player to update.

**New match setting**: add a property to `MatchSettings` (exchanged as JSON). Admins send only what they changed
(`SettingsPatch.Diff`), and the server merges it into its settings (`SettingsPatch.Merge`). An older admin client
never resets a setting it doesn't know.

**New optional feature**: add a string to the features in `ProtocolInfo.OwnCapabilities`. Clients send theirs in
`Hello`, the server sends its own in `Welcome`. Use `Capabilities.Has("name")` to decide whether to send a new
message or to show a button. Known features: `dnf-fills-quota` (0.21.0, the server knows `MatchSettings.DnfFillsQuota`).

## Web rooms (0.22.0)

A room made on the website is reached over WebSocket instead of UDP. The protocol is the same: each binary WebSocket
message carries one game message, in the same format. Only the connection differs, so this doesn't change
`ProtocolInfo.Version`.

- The client sends `x-sbm-version` (equal to `ProtocolInfo.ConnectionKey`) and, if the room has one, `x-room-password`.
  The website checks the password itself before it bridges the connection to the game server.
- Refusals use the HTTP status of the handshake (401 wrong password, 404 no room, 426 version, 429 rate limit, 503 full),
  or a WebSocket close reason. The client translates them with the same codes as UDP.
- New reason codes: `ROOM_NOT_FOUND`, `RATE_LIMITED`, `UNREACHABLE`. An older client shows the code as plain text.

## Breaking changes (to avoid)

- Removing, reordering or changing the type of an existing field.
- Changing the meaning of an existing value (message type, enum value, event code).
- Changing the connection data (`ProtocolInfo.ConnectionData`).
- Making a States entry bigger than 255 bytes, or a States packet bigger than 1023 bytes (LiteNetLib's largest
  unreliable packet before MTU discovery). Use a new message type instead.

## Configuration files

- `SBMultiplayer.json` (mod): `ConfigVersion` gives the file's format. A new format comes with a migration in
  `ModConfig.Migrate`, so settings are never lost. Settings written by a newer version are kept as they are when an
  older version saves the file. An unreadable file is copied to `SBMultiplayer.json.unreadable` before the defaults
  are used.
- `server.json` and `hosted-server.json` (server): existing keys keep their name and meaning. Unknown keys are
  ignored, and a missing key gets its default value.
