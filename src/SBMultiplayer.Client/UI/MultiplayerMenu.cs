using BytingLib;
using BytingLib.UI;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// F8 menu: connection (server, password, name) and links to the "Display", "Interface" and "Admin" screens.
/// Each screen stays short; settings apply immediately and are saved in SBMultiplayer.json.
/// </summary>
internal static class MultiplayerMenu
{
    public static string Version => typeof(MultiplayerMenu).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static void Open() => Show("main", BuildMain);

    // ---------- Main screen: connection ----------

    private static Element BuildMain(Action close, Scene scene)
    {
        ModConfig cfg = ModConfig.Current;
        var status = new Label(StatusText(), MenuWidth);
        scene.Add(new StatusUpdater(status));

        TextInput server = Input(cfg.Server, 80);
        TextInput password = Input(cfg.ServerPassword, 64);
        TextInput name = Input(cfg.PlayerName, ProtocolInfo.MaxNameLength);
        TextInput roomCode = Input("", 16);
        TextInput roomPassword = Input("", 64);

        void JoinRoom()
        {
            string code = roomCode.Text.Trim();
            if (code == "")
            {
                Notifications.Warn(Tr.NoRoomChosen);
                return;
            }
            cfg.PlayerName = name.Text.Trim();
            ModConfig.ForgetEnvironmentOverride();
            ModConfig.Save();
            MultiplayerClient.Instance.JoinRoom(code, roomPassword.Text);
        }
        roomCode.OnEnter += () => { JoinRoom(); return true; };

        void ApplyAndConnect()
        {
            cfg.Server = server.Text.Trim();
            cfg.ServerPassword = password.Text;
            cfg.PlayerName = name.Text.Trim();
            ModConfig.ForgetEnvironmentOverride();
            ModConfig.Save();
            MultiplayerClient.Instance.Connect();
        }
        foreach (TextInput input in new[] { server, password, name })
        {
            input.OnEnter += () => { ApplyAndConnect(); return true; };
        }

        return Stack(
            Title(Tr.MenuTitle(Version)),
            UpdateCheck.NewerVersion is string newer ? Text(Tr.UpdateAvailable(newer)) : null,
            status,
            Section(Tr.ConnectionSection),
            Row(Tr.ServerField, Field(server)),
            Row(Tr.PasswordField, Field(password)),
            Row(Tr.NameField, Field(name)),
            Buttons(
                Button(Tr.Connect, ApplyAndConnect),
                Button(Tr.Disconnect, () => MultiplayerClient.Instance.Disconnect()),
                Button(Tr.JoinRound, () =>
                {
                    close();
                    MultiplayerClient.Instance.RejoinRound();
                }),
                Button(MultiplayerClient.Instance.IsSpectating ? Tr.PlayButton : Tr.SpectateButton, () =>
                {
                    close();
                    MultiplayerClient.Instance.SetSpectating(!MultiplayerClient.Instance.IsSpectating);
                })),
            Section(Tr.RoomsSection),
            Row(Tr.RoomCodeField, Field(roomCode)),
            Row(Tr.RoomPasswordField, Field(roomPassword)),
            Buttons(
                Button(Tr.JoinRoomButton, JoinRoom),
                Button(Tr.CreateRoomButton, OpenCreateRoom)),
            Section(Tr.PlayWithFriendsSection),
            Buttons(
                Button(HostedServer.IsRunning ? Tr.HostManageButton : Tr.HostSection, OpenHost),
                SteamP2P.Available ? Button(Tr.JoinFriendSection, OpenFriends) : null,
                SteamP2P.Available ? Button(Tr.PublicGamesButton, OpenPublicGames) : null),
            Section(Tr.SettingsSection),
            Buttons(
                Button(Tr.DisplayTitle, OpenDisplay),
                Button(Tr.InterfaceTitle, OpenInterface),
                Button(Tr.KeysTitle, KeyBindings.Open),
                Button(Tr.AdminTitle, AdminMenu.Open)),
            Text(Tr.KeysHint(Tr.KeyName(cfg.MenuKey), Tr.KeyName(cfg.RejoinKey), Tr.KeyName(cfg.ChatKey), Tr.KeyName(cfg.ScoreboardKey))),
            Button(Tr.Back, close));
    }

    // ---------- Create a room on the website ----------

    private static void OpenCreateRoom() => Show("create-room", (close, _) =>
    {
        TextInput password = Input("", 64);
        void Create()
        {
            close();
            MultiplayerClient.Instance.CreateRoom(password.Text);
        }
        password.OnEnter += () => { Create(); return true; };
        return Stack(
            Title(Tr.CreateRoomButton),
            Text(Tr.CreateRoomHint),
            Row(Tr.RoomPasswordField, Field(password)),
            Buttons(Button(Tr.CreateRoomButton, Create), Button(Tr.Back, close)));
    });

    // ---------- Host a game ----------

    private static void OpenHost() => Show("host", (close, _) => Stack(Title(Tr.HostSection), BuildHostRow(close), Button(Tr.Back, close)));

    private static Element BuildHostRow(Action close)
    {
        if (HostedServer.IsRunning)
        {
            var addresses = HostedServer.LocalAddresses().Select(a => $"{a}:{HostedServer.Port}").ToList();
            return Stack(
                Text(Tr.HostRunning(HostedServer.Port)),
                Text(Tr.HostAddresses(addresses.Count > 0 ? string.Join(", ", addresses) : "?", HostedServer.Port)),
                ModConfig.Current.SteamP2PHost && SteamP2P.Available ? Text(Tr.SteamHostVisible) : null,
                SteamP2P.IsHosting ? Check(Tr.PublicGameCheckbox, ModSettings.PublicGame) : null,
                Button(Tr.HostStop, () =>
                {
                    close();
                    MultiplayerClient.Instance.StopHosting();
                }));
        }
        TextInput port = Input(ModConfig.Current.HostPort.ToString(), 5, 300f);
        void Host()
        {
            if (!int.TryParse(port.Text.Trim(), out int p) || p < 1 || p > 65535)
            {
                Notifications.Warn(Tr.HostBadPort);
                return;
            }
            ModConfig.Current.HostPort = p;
            ModConfig.Save();
            close();
            MultiplayerClient.Instance.HostGame(p);
        }
        port.OnEnter += () => { Host(); return true; };
        return Stack(
            Row(Tr.HostPortLabel, Line(Field(port), Button(Tr.HostButton, Host))),
            SteamP2P.Available ? Check(Tr.SteamHostCheckbox, ModSettings.SteamP2PHost) : null,
            SteamP2P.Available ? Check(Tr.PublicGameCheckbox, ModSettings.PublicGame) : null);
    }

    // ---------- Join a Steam friend (P2P) ----------

    private static void OpenFriends() => Show("friends", (close, _) =>
    {
        var friends = SteamP2P.FriendsHosting();
        Element list = friends.Count == 0
            ? Text(Tr.NoFriendHosting)
            : Grid(friends.Select(f => Button(f.SameVersion ? f.Name : $"{f.Name} ({Tr.OtherVersion})", () =>
            {
                close();
                MultiplayerClient.Instance.JoinFriend(f.SteamId, f.Name);
            })).ToList(), 3);
        return Stack(
            Title(Tr.JoinFriendSection),
            Text(Tr.JoinFriendHint),
            list,
            Buttons(Button(Tr.Refresh, () =>
            {
                close();
                OpenFriends();
            }), Button(Tr.Back, close)));
    });

    // ---------- Public games (Steam lobbies) ----------

    private static void OpenPublicGames()
    {
        SteamLobby.Refresh();
        Show("public", (close, _) =>
        {
            var games = SteamLobby.Games;
            Element list = SteamLobby.Searching ? Text(Tr.PublicGamesSearching)
                : games.Count == 0 ? Text(Tr.NoPublicGames)
                : Grid(games.Take(30).Select(g => Button(DescribeGame(g), () =>
                {
                    close();
                    MultiplayerClient.Instance.JoinFriend(g.HostId, g.Name);
                })).ToList(), 1);
            return Stack(
                Title(Tr.PublicGamesButton),
                Text(Tr.PublicGamesHint),
                list,
                Buttons(Button(Tr.Refresh, SteamLobby.Refresh), Button(Tr.Back, close)));
        }, () => SteamLobby.ListVersion);
    }

    /// <summary>"Bob's game - Hide and Seek - C9 Castle - 3/32 - password".</summary>
    private static string DescribeGame(SteamLobby.PublicGame g)
    {
        var parts = new List<string> { g.Name, Tr.ModeName(g.Mode) };
        if (g.MapKey != "")
        {
            parts.Add(GameAccess.DescribeMap(g.MapKey));
        }
        parts.Add($"{g.Players}/{g.MaxPlayers}");
        if (g.HasPassword)
        {
            parts.Add(Tr.PasswordTag);
        }
        if (!g.SameVersion)
        {
            parts.Add(Tr.OtherVersionWith(g.ModVersion));
        }
        return string.Join(" - ", parts);
    }

    // ---------- Display: how other players are shown ----------

    private static void OpenDisplay() => Show("display", (close, _) =>
    {
        return Stack(
            Title(Tr.DisplayTitle),
            Check(Tr.ShowOtherPlayers, ModSettings.ShowOtherPlayers),
            Slider(Tr.OtherPlayersOpacity, ModSettings.GhostOpacity, Percent, 0.1f, 1f, 0.1f),
            Check(Tr.ShowNameTags, ModSettings.ShowNameTags),
            Slider(Tr.NameTagDistance, ModSettings.NameTagMaxDistance, f => MathF.Round(float.Parse(f)) + " cm", 10f, 300f, 10f),
            Check(Tr.ShowNameTagDistance, ModSettings.ShowNameTagDistance),
            Button(Tr.Back, close)
        );
    });

    // ---------- Interface: HUD elements of the mod ----------

    private static void OpenInterface() => Show("interface", (close, _) =>
    {
        return Stack(
            Title(Tr.InterfaceTitle),
            Check(Tr.ShowSplits, ModSettings.ShowSplits),
            Section(Tr.ChatTitle),
            Check(Tr.ShowChat, ModSettings.ShowChat),
            Slider(Tr.ChatSize, ModSettings.ChatScale, Percent, 0.5f, 2f, 0.1f),
            Check(Tr.NotificationsOnTop, ModSettings.NotificationsOnTop),
            Section(Tr.RoundPanel),
            Check(Tr.ShowRoundPanel, ModSettings.ShowRoundPanel),
            Slider(Tr.RoundPanelSize, ModSettings.RoundPanelScale, Percent, 0.3f, 1.2f, 0.1f),
            Button(Tr.Back, close)
        );
    });

    private static string StatusText()
    {
        MultiplayerClient c = MultiplayerClient.Instance;
        if (c.IsReady)
        {
            return Tr.StatusConnected(c.ServerAddress, c.PlayerName, c.Players.Count);
        }
        if (c.IsConnecting)
        {
            return Tr.StatusConnecting(c.ServerAddress);
        }
        return c.IsUserDisconnected ? Tr.StatusDisconnected : Tr.StatusOffline;
    }

    private sealed class StatusUpdater(Label status) : IUpdate
    {
        private int tick;

        public void Update()
        {
            if (++tick % 15 == 0)
            {
                string text = StatusText();
                if (status.Text != text)
                {
                    status.Text = text;
                }
            }
        }
    }
}
