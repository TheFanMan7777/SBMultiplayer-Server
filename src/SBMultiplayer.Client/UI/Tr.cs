using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Texts of the mod's interface. The language follows the game's (French if the game is in French,
/// English otherwise), unless "Language" is forced to "fr" or "en" in SBMultiplayer.json.
/// Note: the game's font has neither an em dash nor an ellipsis character: stick to ASCII and common accents.
/// </summary>
internal static class Tr
{
    public static bool French
    {
        get
        {
            string forced = ModConfig.Current.Language.Trim().ToLowerInvariant();
            if (forced is "fr" or "en")
            {
                return forced == "fr";
            }
            try
            {
                return SE.Loca.L?.LanguageKey?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string T(string en, string fr) => French ? fr : en;

    private static string Players(int n) => French
        ? $"{n} autre{(n > 1 ? "s" : "")} joueur{(n > 1 ? "s" : "")}"
        : $"{n} other player{(n > 1 ? "s" : "")}";

    // ---------- Menu ----------
    public static string MenuTitle(string version) => T($"Multiplayer  (SBMultiplayer v{version})", $"Multijoueur  (SBMultiplayer v{version})");
    public static string ServerField => T("Server (address:port)", "Serveur (adresse:port)");
    public static string RoomsSection => T("Rooms (on the website)", "Salles (sur le site)");
    public static string RoomCodeField => T("Room code", "Code de la salle");
    public static string RoomPasswordField => T("Room password", "Mot de passe de la salle");
    public static string JoinRoomButton => T("Join room", "Rejoindre la salle");
    public static string CreateRoomButton => T("Create a room", "Créer une salle");
    public static string CreateRoomHint => T("Creates a room on the website with a code to share. You are its admin.", "Crée une salle sur le site avec un code à partager. Vous en êtes l'admin.");
    public static string RoomCreated(string code) => T($"Room created: {code}. Share this code.", $"Salle créée : {code}. Partagez ce code.");
    public static string RoomCreateFailed(string error) => T($"Could not create a room ({error})", $"Impossible de créer une salle ({error})");
    public static string NoRoomChosen => T("Type a room code first", "Entrez d'abord un code de salle");
    public static string RoomNotFound => T("No room has that code", "Aucune salle ne porte ce code");
    public static string RateLimited => T("Too many attempts, wait a minute", "Trop de tentatives, attendez une minute");
    public static string NameField => T("Name (empty = Steam name)", "Pseudo (vide = pseudo Steam)");
    public static string Connect => T("Connect", "Se connecter");
    public static string Disconnect => T("Disconnect", "Se déconnecter");
    public static string JoinRound => T("Join round", "Rejoindre le round");
    public static string ShowOtherPlayers => T("Show other players", "Afficher les autres joueurs");
    public static string OtherPlayersOpacity => T("Other players' opacity", "Opacité des autres joueurs");
    public static string ShowNameTags => T("Show names", "Afficher les pseudos");
    public static string NameTagDistance => T("Name display distance", "Distance des pseudos");
    public static string ShowNameTagDistance => T("Distance next to names", "Distance à côté des pseudos");
    public static string ShowSplits => T("Checkpoint splits", "Temps intermédiaires");
    public static string RoundPanel => T("Round panel", "Panneau du round");
    public static string ShowRoundPanel => T("Show the round panel", "Afficher le panneau du round");
    public static string RoundPanelSize => T("Round panel size", "Taille du panneau du round");
    public static string KeysHint(string menuKey, string rejoinKey, string chatKey, string scoreboardKey) =>
        T($"{menuKey}: this menu    {rejoinKey}: join the round's map    {chatKey}: chat    {scoreboardKey} (hold): players",
          $"{menuKey} : ce menu    {rejoinKey} : rejoindre la map du round    {chatKey} : chat    {scoreboardKey} (maintenir) : joueurs");
    public static string ShowChat => T("Show chat", "Afficher le chat");
    public static string ChatSize => T("Chat size", "Taille du chat");
    public static string NotificationsOnTop => T("Notifications at the top", "Notifications en haut");
    public static string ChatTitle => T("Chat", "Chat");
    public static string ChatHint => T("Enter: send    Escape: cancel    /help: commands", "Entrée : envoyer    Échap : annuler    /help : commandes");

    /// <summary>Translated reply, or null for a code this version doesn't know (the server's English fallback is shown instead).</summary>
    public static string? CommandReply(string code, string arg) => code switch
    {
        SBMultiplayer.Protocol.CommandResults.Help when arg == "admin" => T(
            "Commands: /help, /locate <name|all>, /goto <name>, /ignore [name], /unignore <name>, /login <password>, /next, /restart, /pause, /extend [seconds], /map <number>, /kick <name>, /resetmatch, /hunter [name]",
            "Commandes : /help, /locate <pseudo|all>, /goto <pseudo>, /ignore [pseudo], /unignore <pseudo>, /login <mot de passe>, /next, /restart, /pause, /extend [secondes], /map <numéro>, /kick <pseudo>, /resetmatch, /hunter [pseudo]"),
        SBMultiplayer.Protocol.CommandResults.Help => T(
            "Commands: /help, /locate <name|all>, /goto <name>, /ignore [name], /unignore <name>, /login <admin password> (admin commands: /next, /restart, /pause, /extend, /map, /kick, /resetmatch, /hunter)",
            "Commandes : /help, /locate <pseudo|all>, /goto <pseudo>, /ignore [pseudo], /unignore <pseudo>, /login <mot de passe admin> (commandes admin : /next, /restart, /pause, /extend, /map, /kick, /resetmatch, /hunter)"),
        SBMultiplayer.Protocol.CommandResults.NotAdmin => T("Admin only: log in with /login <password> or F8 > Admin", "Réservé aux admins : connecte-toi avec /login <mot de passe> ou F8 > Admin"),
        SBMultiplayer.Protocol.CommandResults.NoRound => T("No round in progress", "Aucun round en cours"),
        SBMultiplayer.Protocol.CommandResults.Usage => T($"Usage: {arg}", $"Utilisation : {arg}"),
        SBMultiplayer.Protocol.CommandResults.NoPlayer => T($"No player matching \"{arg}\"", $"Aucun joueur ne correspond à \"{arg}\""),
        SBMultiplayer.Protocol.CommandResults.BadMap => T($"Unknown map number (the rotation has {arg} maps)", $"Numéro de map inconnu (la rotation compte {arg} maps)"),
        SBMultiplayer.Protocol.CommandResults.Unknown => T($"Unknown command {arg} (see /help)", $"Commande inconnue {arg} (voir /help)"),
        SBMultiplayer.Protocol.CommandResults.TooFast => T("You're sending messages too fast", "Tu envoies des messages trop vite"),
        SBMultiplayer.Protocol.CommandResults.InvalidSettings => T($"Settings refused: {arg}", $"Paramètres refusés : {arg}"),
        SBMultiplayer.Protocol.CommandResults.NoConfigFile => T("Settings applied, but not saved: the server wasn't started with a config file (--config)", "Paramètres appliqués, mais pas enregistrés : le serveur n'a pas été lancé avec un fichier de config (--config)"),
        _ => null,
    };
    public static string Back => T("Back", "Retour");
    public static string MenuOpenFailed => T("Could not open the multiplayer menu (see SBMultiplayer.log)", "Impossible d'ouvrir le menu multijoueur (voir SBMultiplayer.log)");

    // ---------- Status ----------
    public static string StatusConnected(string server, string name, int others) =>
        T($"Connected to {server} as {name} ({Players(others)})", $"Connecté à {server} en tant que {name} ({Players(others)})");
    public static string StatusConnecting(string server) => T($"Connecting to {server}...", $"Connexion à {server}...");
    public static string StatusDisconnected => T("Disconnected", "Déconnecté");
    public static string StatusOffline => T("Offline - retrying automatically", "Hors ligne - nouvelle tentative automatique");

    // ---------- Round panel ----------
    public static string ResultsHeader(int seconds) => T($"Results - next map in {seconds} s", $"Résultats - map suivante dans {seconds} s");
    public static string NotOnRoundMap(string rejoinKey) =>
        T($"You're not on the round's map ({rejoinKey} to join it)", $"Tu n'es pas sur la map du round ({rejoinKey} pour la rejoindre)");
    public static string NoTimesYet => T("No times yet", "Aucun temps pour l'instant");

    // ---------- Notifications ----------
    public static string Connected(string server, int others, string menuKey) =>
        T($"Connected to {server} ({Players(others)}) - {menuKey}: multiplayer menu", $"Connecté à {server} ({Players(others)}) - {menuKey} : menu multijoueur");
    public static string DisconnectedByUser => T("Disconnected from the server", "Déconnecté du serveur");
    public static string RetryingEvery(string reason, int seconds) =>
        T($"{reason} - retrying every {seconds} s", $"{reason} - nouvelle tentative toutes les {seconds} s");
    public static string CannotConnect(string server, string error) => T($"Cannot connect to {server} ({error})", $"Connexion impossible à {server} ({error})");
    public static string ServerUnreachable(string server) => T($"Server unreachable ({server})", $"Serveur injoignable ({server})");
    public static string ConnectionLost => T("Connection to the server lost", "Connexion au serveur perdue");
    public static string ServerClosed => T("The server closed the connection", "Le serveur a fermé la connexion");
    public static string ConnectionRejected => T("Connection rejected by the server", "Connexion refusée par le serveur");
    public static string ServerFull => T("The server is full", "Le serveur est plein");
    public static string VersionMismatch(string serverProtocol) =>
        T($"Incompatible mod version (server uses protocol v{serverProtocol}). Please update the mod.",
          $"Version du mod incompatible (le serveur utilise le protocole v{serverProtocol}). Mets le mod à jour.");
    public static string UnknownModeSpectating =>
        T("This server plays a game mode your mod version doesn't know: you're watching as a spectator. Update the mod to take part.",
          "Ce serveur joue un mode que ta version du mod ne connaît pas : tu regardes en spectateur. Mets le mod à jour pour participer.");
    public static string PlayerJoined(string name) => T($"{name} joined the game", $"{name} a rejoint la partie");
    public static string PlayerLeft(string name) => T($"{name} left the game", $"{name} a quitté la partie");
    public static string RoundStarted(int id, string map) => T($"Round {id}: {map}", $"Round {id} : {map}");
    public static string RoundEndNoTimes => T("Round over: no times", "Fin du round : aucun temps");
    public static string RoundEndWinner(string name, string time) => T($"Round over: {name} wins with {time}", $"Fin du round : {name} gagne en {time}");
    public static string NoRound => T("No round in progress", "Aucun round en cours");
    public static string AlreadyOnRoundMap => T("You're already on the round's map", "Tu es déjà sur la map du round");
    public static string MapLoadFailedVersion(string key) => T($"Could not load map {key} (different game version?)", $"Impossible de charger la map {key} (version du jeu différente ?)");
    public static string MapLoadFailed(string key) => T($"Could not load map {key}", $"Impossible de charger la map {key}");
    public static string MapNotSupported(string key) => T($"Map not supported by this mod version: {key}", $"Map non supportée par cette version du mod : {key}");
    public static string BadPassword => T("Wrong server password: set it in the F8 menu, then Connect", "Mot de passe du serveur incorrect : renseigne-le dans le menu F8, puis Se connecter");
    public static string Kicked => T("You were kicked from the server", "Tu as été expulsé du serveur");
    public static string AdminLoginFailed => T("Wrong admin password", "Mot de passe admin incorrect");

    /// <summary>Translated event, or null for a code this version doesn't know (the server's English fallback is shown instead).</summary>
    public static string? ServerEvent(string code, string arg) => code switch
    {
        SBMultiplayer.Protocol.ServerEvents.Paused => T($"Round paused by {arg}", $"Round mis en pause par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.Resumed => T($"Round resumed by {arg}", $"Round repris par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.Skipped => T($"Skipped by {arg}", $"Passé par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.Restarted => T($"Round restarted by {arg}", $"Round relancé par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.Extended => T($"Round extended by {arg} s", $"Round prolongé de {arg} s"),
        SBMultiplayer.Protocol.ServerEvents.MapChosen => T($"Map changed by {arg}", $"Map changée par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.PlayerKicked => T($"{arg} was kicked", $"{arg} a été expulsé"),
        SBMultiplayer.Protocol.ServerEvents.MatchReset => T($"Match scores reset by {arg}", $"Scores du match remis à zéro par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.AdminLoggedIn => T($"{arg} is now admin", $"{arg} est maintenant admin"),
        SBMultiplayer.Protocol.ServerEvents.SettingsChanged => T($"Server settings changed by {arg}", $"Paramètres du serveur modifiés par {arg}"),
        SBMultiplayer.Protocol.ServerEvents.CupWon => T($"{arg} wins the cup!", $"{arg} remporte la Cup !"),
        SBMultiplayer.Protocol.ServerEvents.EliminationWon => T($"{arg} wins the elimination!", $"{arg} remporte l'élimination !"),
        SBMultiplayer.Protocol.ServerEvents.Eliminated => T($"Eliminated: {arg}", $"Éliminé(s) : {arg}"),
        SBMultiplayer.Protocol.ServerEvents.Finalist => T($"{arg} is now a finalist", $"{arg} est finaliste"),
        SBMultiplayer.Protocol.ServerEvents.MatchWon => T($"{arg} wins the match!", $"{arg} remporte le match !"),
        SBMultiplayer.Protocol.ServerEvents.HunterChosen => T($"{arg} is the hunter!", $"{arg} est le chasseur !"),
        SBMultiplayer.Protocol.ServerEvents.MouseFound => MouseFound(arg),
        SBMultiplayer.Protocol.ServerEvents.MouseForfeit => T($"{arg} left their hiding place", $"{arg} a quitté sa cachette"),
        SBMultiplayer.Protocol.ServerEvents.HuntersWin => T($"Every mouse was found: {arg} wins the round!", $"Toutes les souris ont été trouvées : victoire de {arg} !"),
        SBMultiplayer.Protocol.ServerEvents.MiceSurvived => T($"Time's up! Surviving mice: {arg}", $"Temps écoulé ! Souris survivantes : {arg}"),
        _ => null,
    };

    // ---------- Rounds mode (synchronized start) ----------
    public static string WaitingForPlayers(int ready, int total) => T($"Waiting for players ({ready}/{total})", $"En attente des joueurs ({ready}/{total})");
    public static string Go => T("GO!", "GO !");
    public static string GaveUp => T("You went back to the start: you gave up this round", "Retour au départ : tu as abandonné cette manche");
    public static string ConfirmGiveUp(string? key) => key == null
        ? T("One attempt per round: press restart again quickly, or hold it, to give up", "Un seul essai par manche : rappuie vite sur recommencer, ou maintiens-le, pour abandonner")
        : T($"One attempt per round: press {key} again quickly, or hold it, to give up", $"Un seul essai par manche : rappuie vite sur {key}, ou maintiens-le, pour abandonner");
    public static string BackToStartPenalty(float seconds) =>
        T($"Back to the start: the run goes on, +{seconds:0.0} s added to your time", $"Retour au départ : la course continue, +{seconds:0.0} s ajoutées à ton temps");
    public static string MissedStart => T("You missed the start: wait for the next round", "Tu as raté le départ : attends la prochaine manche");
    public static string WaitNextRound => T("Waiting for the next round", "En attente de la prochaine manche");
    public static string FinishedWaiting => T("Finished - waiting for the other players", "Arrivé - en attente des autres joueurs");
    public static string LateWaiting => T("Round already started - you'll race in the next one", "Manche déjà commencée - tu joueras la suivante");
    public static string RoundOf(int n, int total) => T($"Round {n}/{total}", $"Manche {n}/{total}");
    public static string EliminatedWaiting => T("Eliminated - watch the others until the next match", "Éliminé - regarde les autres jusqu'au prochain match");
    public static string FinalistTag => T("FINALIST", "FINALISTE");
    public static string EliminatedTag => T("out", "éliminé");
    public static string WinnerTag => T("WINNER", "VAINQUEUR");
    public static string RoundsSurvived(int n) => T($"{n} round{(n > 1 ? "s" : "")}", $"{n} manche{(n > 1 ? "s" : "")}");

    public static string ModeName(SBMultiplayer.Protocol.GameMode mode) => mode switch
    {
        SBMultiplayer.Protocol.GameMode.Free => T("Free", "Libre"),
        SBMultiplayer.Protocol.GameMode.TimeAttack => "Time Attack",
        SBMultiplayer.Protocol.GameMode.Rounds => "Rounds",
        SBMultiplayer.Protocol.GameMode.Cup => "Cup",
        SBMultiplayer.Protocol.GameMode.Elimination => T("Elimination", "Élimination"),
        SBMultiplayer.Protocol.GameMode.HideAndSeek => T("Hide and Seek", "Cache-cache"),
        _ => T("Unknown mode", "Mode inconnu"),
    };

    // ---------- Server settings (admins) ----------
    public static string SettingsModeAndMapsTitle => T("Mode and maps", "Mode et maps");
    public static string SettingsTimingsTitle => T("Timings and points", "Délais et points");
    public static string SettingsModeLabel(string mode) => T($"Game mode: {mode}", $"Mode de jeu : {mode}");
    public static string SettingsMapsField => T("Level codes separated by commas: official levels (A1, A5…) and Workshop maps (M123456…):", "Codes séparés par des virgules : niveaux officiels (A1, A5…) et maps du Workshop (M123456…) :");
    public static string SettingsAddCurrentMap => T("Add the map I'm on", "Ajouter la map où je suis");
    public static string SettingsNoCurrentMap => T("You're not on a level", "Tu n'es pas sur un niveau");
    public static string SettingsLocalMap => T("Local maps can't be used on a server (the others don't have them): publish it on the Workshop first",
        "Les maps locales ne peuvent pas être utilisées sur un serveur (les autres ne les ont pas) : publie-la d'abord sur le Workshop");
    public static string WorkshopVersionMismatch => T("Your version of this Workshop map differs from the server's: your times won't count",
        "Ta version de cette map du Workshop diffère de celle du serveur : tes temps ne compteront pas");
    public static string SettingsUnknownMap(string code) => T($"Unknown level \"{code}\"", $"Niveau inconnu \"{code}\"");
    public static string SettingsRestartWarning => T("Changing the mode or the maps restarts the match.", "Changer le mode ou les maps relance le match.");
    public static string SettingsSaveToFile => T("Save to the config file", "Enregistrer dans la config");
    public static string SettingsApply => T("Apply", "Appliquer");
    public static string SettingsRoundSeconds => T("Round duration", "Durée du round");
    public static string SettingsTimeLimit => T("Time limit after the start", "Limite de temps après le départ");
    public static string SettingsResultsSeconds => T("Results screen", "Écran de résultats");
    public static string SettingsRoundsPerMap => T("Rounds per map", "Manches par map");
    public static string SettingsFinishTimeout => T("Round end after 1st finisher", "Fin après le 1er arrivé");
    public static string SettingsPrepareTimeout => T("Max wait for players", "Attente max des joueurs");
    public static string SettingsCountdown => T("Start countdown", "Compte à rebours du départ");
    public static string SettingsCupLimit => T("Cup: points to become a finalist", "Cup : points pour être finaliste");
    public static string SettingsEliminatedPerRound => T("Elimination: eliminated per round", "Élimination : éliminés par manche");
    public static string SettingsPointsTable => T("Points by rank", "Points par place");
    public static string SettingsBadPoints => T("Points: whole numbers separated by commas", "Points : des nombres entiers séparés par des virgules");

    // ---------- Menus: sections and sub-screens ----------
    public static string ConnectionSection => T("Connection", "Connexion");
    public static string SettingsSection => T("Settings", "Réglages");

    // ---------- Keys ----------
    public static string KeysTitle => T("Keys", "Touches");
    public static string KeysHelp => T("Click an action, then press its new key (Escape: cancel). A key already used by another action is swapped with it.",
        "Clique sur une action, puis appuie sur sa nouvelle touche (Échap : annuler). Une touche déjà utilisée par une autre action est échangée avec elle.");
    public static string PressAKey => T("Press a key...", "Appuie sur une touche...");
    public static string KeysReset => T("Default keys", "Touches par défaut");
    public static string KeyMenu => T("Multiplayer menu", "Menu multijoueur");
    public static string KeyRejoin => T("Join the round's map", "Rejoindre la map du round");
    public static string KeyChat => T("Chat", "Chat");
    public static string KeyScoreboard => T("Player list (hold)", "Liste des joueurs (maintenir)");
    public static string KeyCloseEndScreen => T("Close the end screen", "Fermer l'écran de fin");
    public static string KeySpectatePrevious => T("Spectator: previous player", "Spectateur : joueur précédent");
    public static string KeySpectateNext => T("Spectator: next player", "Spectateur : joueur suivant");
    public static string KeyStopFollowing => T("Frozen mouse: stop following", "Souris gelée : arrêter de suivre");
    public static string KeyCatchMouse => T("Hunter: catch a mouse", "Chasseur : capturer une souris");
    public static string KeySwapped(string action, string key) => T($"\"{action}\" now uses {key}", $"\"{action}\" utilise maintenant {key}");

    /// <summary>Readable name of a MonoGame key ("OemComma" → ",", "Left" → "Gauche" in French).</summary>
    public static string KeyName(string key) => key switch
    {
        "Escape" => T("Escape", "Échap"),
        "LeftButton" => T("Left click", "Clic gauche"),
        "RightButton" => T("Right click", "Clic droit"),
        "MiddleButton" => T("Middle click", "Clic molette"),
        "XButton1" => T("Mouse 4", "Souris 4"),
        "XButton2" => T("Mouse 5", "Souris 5"),
        "Enter" => T("Enter", "Entrée"),
        "Left" => T("Left", "Gauche"),
        "Right" => T("Right", "Droite"),
        "Up" => T("Up", "Haut"),
        "Down" => T("Down", "Bas"),
        "Space" => T("Space", "Espace"),
        "Back" => T("Backspace", "Retour arrière"),
        "Delete" => T("Delete", "Suppr"),
        "Insert" => T("Insert", "Inser"),
        "PageUp" => T("Page Up", "Page préc."),
        "PageDown" => T("Page Down", "Page suiv."),
        "LeftShift" => T("Left Shift", "Maj gauche"),
        "RightShift" => T("Right Shift", "Maj droite"),
        "LeftControl" => T("Left Ctrl", "Ctrl gauche"),
        "RightControl" => T("Right Ctrl", "Ctrl droit"),
        "LeftAlt" => T("Left Alt", "Alt gauche"),
        "RightAlt" => T("Right Alt", "Alt Gr"),
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemMinus" => "-",
        "OemPlus" => "+",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemTilde" => "`",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemPipe" => "\\",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) => T($"Num {key[6..]}", $"Pavé {key[6..]}"),
        _ => key,
    };
    public static string DisplayTitle => T("Display", "Affichage");
    public static string InterfaceTitle => T("Interface", "Interface");
    public static string AdminRoundSection => T("Round", "Round");
    public static string AdminMatchSection => T("Match", "Match");
    public static string AdminServerSection => T("Server settings", "Paramètres du serveur");
    public static string AdminChangeMap => T("Change map", "Changer de map");
    public static string AdminPlayersButton => T("Players", "Joueurs");
    public static string AdminNoOtherPlayers => T("No other player connected", "Aucun autre joueur connecté");
    public static string SettingsModeSection => T("Game mode", "Mode de jeu");
    public static string SettingsMapsSection => T("Map rotation", "Rotation de maps");
    public static string SettingsApplySection => T("Apply", "Appliquer");
    public static string SettingsDurationsSection => T("Durations", "Durées");
    public static string SettingsMatchSection => T("Match", "Match");
    public static string SettingsFreeModeNothing => T("Nothing to set in Free mode: change the mode in \"Mode and maps\".", "Rien à régler en mode Libre : change le mode dans \"Mode et maps\".");

    public static string ModeDescription(SBMultiplayer.Protocol.GameMode mode) => mode switch
    {
        SBMultiplayer.Protocol.GameMode.Free => T("No rounds: everyone rides wherever they want.", "Pas de rounds : chacun roule où il veut."),
        SBMultiplayer.Protocol.GameMode.TimeAttack => T("Unlimited retries during the round, the best time wins.", "Retries illimités pendant le round, le meilleur temps gagne."),
        SBMultiplayer.Protocol.GameMode.Rounds => T("Synchronized start, one attempt per round, points by race time.", "Départ synchronisé, une tentative par manche, points selon le temps."),
        SBMultiplayer.Protocol.GameMode.Cup => T("Like Rounds; reach the points limit to become a finalist, then win a round to win the cup.", "Comme Rounds ; atteins la limite de points pour être finaliste, puis gagne une manche pour remporter la Cup."),
        SBMultiplayer.Protocol.GameMode.Elimination => T("Like Rounds; the slowest players are eliminated each round until one is left.", "Comme Rounds ; les plus lents sont éliminés à chaque manche jusqu'au dernier."),
        SBMultiplayer.Protocol.GameMode.HideAndSeek => T("A hunter is frozen while the mice hide, then the mice are frozen and the hunter has to find them (aim + grind button).",
            "Un chasseur est gelé pendant que les souris se cachent, puis les souris sont gelées et le chasseur doit les trouver (viser + bouton de grind)."),
        _ => "",
    };

    // ---------- Spectator ----------
    public static string SpectateButton => T("Spectate", "Spectateur");
    public static string PlayButton => T("Play again", "Rejouer");
    public static string YouAreSpectating => T("You're spectating: invisible to the others, not counted in rounds", "Tu es spectateur : invisible pour les autres, hors des manches");
    public static string YouAreBackInGame => T("You're back in the game", "Tu es de retour dans la partie");
    public static string PlayerSpectating(string name) => T($"{name} is spectating", $"{name} est spectateur");
    public static string PlayerBackInGame(string name) => T($"{name} is back in the game", $"{name} est de retour dans la partie");
    public static string SpectatorTitle => T("SPECTATOR", "SPECTATEUR");
    public static string Following(string name) => T($"following {name}", $"suit {name}");
    public static string NobodyToFollow => T("nobody to follow on this map", "personne à suivre sur cette map");
    public static string SpectatorKeys(string previous, string next, string fly) =>
        T($"{previous} / {next}: change player    {fly}: free camera", $"{previous} / {next} : changer de joueur    {fly} : caméra libre");

    /// <summary>The player's key for the game's free camera, as text.</summary>
    public static string FlyKey => SBMultiplayer.Client.Ghosts.GameAccess.FlyKeyName ?? T("Free camera key", "Touche caméra libre");

    // ---------- End-of-game screen ----------
    public static string EndTitle(SBMultiplayer.Protocol.GameMode mode) => mode switch
    {
        SBMultiplayer.Protocol.GameMode.TimeAttack => T("End of the round - Time Attack", "Fin du round - Time Attack"),
        SBMultiplayer.Protocol.GameMode.Rounds => T("End of the match - Rounds", "Fin du match - Rounds"),
        SBMultiplayer.Protocol.GameMode.Cup => T("End of the cup", "Fin de la Cup"),
        SBMultiplayer.Protocol.GameMode.Elimination => T("End of the elimination", "Fin de l'élimination"),
        SBMultiplayer.Protocol.GameMode.HideAndSeek => T("End of the match - Hide and Seek", "Fin du match - Cache-cache"),
        _ => T("End of the game", "Fin de partie"),
    };
    public static string WinnerAnnounce(string name) => T($"Winner: {name}", $"Vainqueur : {name}");
    public static string NoWinner => T("No winner", "Pas de vainqueur");
    public static string AndMore(int n) => T($"... and {n} more", $"... et {n} autre{(n > 1 ? "s" : "")}");
    public static string EndScreenClose(string key) => T($"{key}: close", $"{key} : fermer");
    public static string NextGameIn(int seconds) => T($"Next game in {seconds} s", $"Partie suivante dans {seconds} s");
    public static string SettingsMatchEnd => T("End-of-game screen", "Écran de fin de partie");

    // ---------- Updates ----------
    public static string UpdateAvailable(string version) =>
        T($"SBMultiplayer {version} is available: download it from the mod's GitHub page (releases)",
          $"SBMultiplayer {version} est disponible : télécharge-la sur la page GitHub du mod (releases)");

    // ---------- Public games ----------
    public static string PublicGamesButton => T("Public games", "Parties publiques");
    public static string PublicGamesHint => T("Games hosted from the game and listed publicly. Click one to join it through Steam (no port to open). Password: enter it in the F8 Password field first.",
        "Parties hébergées depuis le jeu et rendues publiques. Clique sur l'une d'elles pour la rejoindre via Steam (aucun port à ouvrir). Mot de passe : renseigne-le d'abord dans le champ Mot de passe de F8.");
    public static string PublicGamesSearching => T("Searching...", "Recherche...");
    public static string NoPublicGames => T("No public game right now. Host one: F8 > Host a game, with \"Public game (anyone on Steam)\" ticked.",
        "Aucune partie publique pour le moment. Héberges-en une : F8 > Héberger une partie, avec \"Partie publique (tout Steam)\" coché.");
    public static string PublicGameCheckbox => T("Public game (anyone on Steam)", "Partie publique (tout Steam)");
    public static string PublicListingOn => T("Your game is now listed in F8 > Public games", "Ta partie apparaît maintenant dans F8 > Parties publiques");
    public static string PublicListingFailed => T("Could not list your game publicly (Steam lobbies unavailable)", "Impossible de rendre ta partie publique (lobbies Steam indisponibles)");
    public static string PasswordTag => T("password", "mot de passe");
    public static string OtherVersionWith(string version) => T($"other mod version ({version})", $"autre version du mod ({version})");

    // ---------- Local commands and player list ----------
    public static string Usage(string usage) => T($"Usage: {usage}", $"Utilisation : {usage}");
    public static string NoPlayerMatching(string query) => T($"No player matching \"{query}\"", $"Aucun joueur ne correspond à \"{query}\"");
    public static string LocateMenus => T("in the menus", "dans les menus");
    public static string LocateLocalMap => T("a local map (not on the Workshop)", "une map locale (pas sur le Workshop)");
    public static string GotoInMenus(string name) => T($"{name} is in the menus", $"{name} est dans les menus");
    public static string GotoLocalMap(string name) => T($"{name} is on a local map: you can't join it", $"{name} est sur une map locale : impossible de le rejoindre");
    public static string GotoSameMap(string name) => T($"You're already on {name}'s map", $"Tu es déjà sur la map de {name}");
    public static string GotoLoading(string name, string map) => T($"Joining {name} on {map}", $"Direction la map de {name} : {map}");
    public static string IgnoreListEmpty => T("You're not ignoring anyone (/ignore <name>)", "Tu n'ignores personne (/ignore <pseudo>)");
    public static string IgnoreList(string names) => T($"Ignored: {names}", $"Ignorés : {names}");
    public static string Ignored(string name) => T($"{name}'s messages are now hidden (/unignore {name} to undo)", $"Les messages de {name} sont masqués (/unignore {name} pour annuler)");
    public static string Unignored(string name) => T($"{name}'s messages are shown again", $"Les messages de {name} sont de nouveau affichés");
    public static string ScoreboardTitle(int count) => T($"Players ({count})", $"Joueurs ({count})");
    public static string ScoreName => T("Player", "Joueur");
    public static string ScoreMap => T("Map", "Map");
    public static string ScoreStatus => T("Status", "Statut");
    public static string ScorePoints => T("Points", "Points");
    public static string RoleHunter => T("hunter", "chasseur");
    public static string RoleMouse => T("mouse", "souris");
    public static string RoleFound => T("found", "trouvé");
    public static string YouTag => T("(you)", "(toi)");

    // ---------- Hide and Seek ----------
    private static string MouseFound(string arg)
    {
        string[] names = arg.Split('\n');
        string hunter = names[0];
        string mouse = names.Length > 1 ? names[1] : "?";
        return T($"{hunter} found {mouse}!", $"{hunter} a trouvé {mouse} !");
    }
    public static string HsYouAreHunter => T("You are the HUNTER", "Tu es le CHASSEUR");
    public static string HsYouAreMouse => T("You are a MOUSE", "Tu es une SOURIS");
    public static string HsYouAreMouseHide => T("You are a MOUSE: hide!", "Tu es une SOURIS : cache-toi !");
    public static string HsHidingPhase => T("Hiding phase", "Phase de cachette");
    public static string HsSeekingPhase => T("Seeking phase", "Phase de chasse");
    public static string HsLockedTitle => T("HIDING PLACE LOCKED", "CACHETTE VERROUILLÉE");
    public static string HsLockedKeys(string fly, string previous, string next, string stop) =>
        T($"{fly}: unlock and ride again    {previous} / {next}: follow a player    {stop}: stop following",
          $"{fly} : déverrouiller et repartir    {previous} / {next} : suivre un joueur    {stop} : arrêter de suivre");
    public static string HsUnlocked => T("Hiding place unlocked: you can ride again", "Cachette déverrouillée : tu peux repartir");
    public static string HsMiceHiding => T("The mice are hiding... you'll be released at the end of the hiding phase", "Les souris se cachent... tu seras libéré à la fin de la phase de cachette");
    public static string HsHide => T("Hide!", "Cache-toi !");
    public static string HsHuntBegins => T("The hunt begins!", "La chasse commence !");
    public static string HsAimHint(string key) => T($"Aim at a mouse and press {key} to catch it", $"Vise une souris et appuie sur {key} pour la capturer");
    public static string HsFrozen => T("Frozen in your hiding place", "Gelé dans ta cachette");
    public static string HsFrozenTitle => T("FROZEN", "GELÉ");
    public static string HsFoundTitle => T("FOUND", "TROUVÉ");
    public static string HsFrozenKeys(string previous, string next, string stop, string fly) =>
        T($"{fly}: free camera / your view    {previous} / {next}: follow a player    {stop}: stop following",
          $"{fly} : caméra libre / ta vue    {previous} / {next} : suivre un joueur    {stop} : arrêter de suivre");
    public static string HsLockHint(string fly) => T($"{fly}: lock your hiding place now", $"{fly} : verrouiller ta cachette maintenant");
    public static string HsLandToLock => T("Touch a surface to lock your hiding place", "Touche une surface pour verrouiller ta cachette");
    public static string HsLandToFreeze => T("Hunt started: you'll be frozen as soon as you touch a surface", "La chasse a commencé : tu seras gelé dès que tu touches une surface");
    public static string HsYouAreHunterNow => T("You were found: you're a hunter now!", "Tu as été trouvé : tu es chasseur maintenant !");
    public static string HsHunters(string names) => T($"Hunter: {names}", $"Chasseur : {names}");
    public static string HsMiceLeft(int hidden, int total) => T($"Mice hidden: {hidden}/{total}", $"Souris cachées : {hidden}/{total}");
    public static string HsFoundTag => T("found", "trouvée");
    public static string HsHidingClock => T("Hiding", "Cache");
    public static string SettingsHideSeconds => T("Hiding time", "Temps pour se cacher");
    public static string SettingsHuntSeconds => T("Hunting time", "Temps de recherche");
    public static string SettingsDnfFillsQuota => T("Elimination: DNF fill the quota", "Élimination : abandons dans le quota");
    public static string SettingsDnfFillsQuotaHint => T(
        "Ticked: players who don't finish fill the quota, first to give up first (quota 2, 3 give-ups: only the first two are out). Unticked: all of them are eliminated.",
        "Coché : les joueurs qui ne finissent pas remplissent le quota, premier abandon d'abord (quota 2, 3 abandons : seuls les deux premiers sortent). Décoché : ils sont tous éliminés.");
    public static string HsRespawnedOut => T("You left your hiding place during the hunt: you're out of this round", "Tu as quitté ta cachette pendant la chasse : tu es hors jeu pour cette manche");
    public static string SettingsInfection => T("Infection: found mice hunt too", "Contagion : trouvées = chasseuses");
    public static string AdminNextHunter => T("Next hunter", "Prochain chasseur");
    public static string AdminRandomHunter => T("Random", "Au hasard");
    public static string AdminHunterSet(string name) => T($"{name} will be the next hunter", $"{name} sera le prochain chasseur");
    public static string AdminHunterRandom => T("The next hunter will be random", "Le prochain chasseur sera tiré au hasard");

    public static string IncompatibleNotice(string version) =>
        T($"SBMultiplayer v{version}: incompatible with this game version, update the mod",
          $"SBMultiplayer v{version} : incompatible avec cette version du jeu, mets le mod à jour");

    // ---------- Host a game ----------
    public static string HostSection => T("Host a game", "Héberger une partie");
    public static string HostManageButton => T("My hosted game", "Ma partie hébergée");
    public static string PlayWithFriendsSection => T("Play with friends", "Jouer entre amis");
    public static string HostPortLabel => T("UDP port", "Port UDP");
    public static string HostButton => T("Host", "Héberger");
    public static string HostStop => T("Stop hosting", "Arrêter la partie");
    public static string HostBadPort => T("Invalid port (1-65535)", "Port invalide (1-65535)");
    public static string HostFailed(string error) => T($"Could not host the game: {error}", $"Impossible d'héberger la partie : {error}");
    public static string HostStarted(int port) => T($"Your game is hosted on port {port}: you're its admin (F8 > Admin)", $"Ta partie est hébergée sur le port {port} : tu en es l'admin (F8 > Admin)");
    public static string HostStopped => T("Hosted game stopped", "Partie hébergée arrêtée");
    public static string HostRunning(int port) => T($"You're hosting a game on UDP port {port}, and you're its admin.", $"Tu héberges une partie sur le port UDP {port}, et tu en es l'admin.");
    public static string HostAddresses(string local, int port) => T(
        $"Friends connect to: {local} (local network), or your public IP:{port} (internet, open the port on your router)",
        $"Tes amis se connectent à : {local} (réseau local), ou ton IP publique:{port} (internet, ouvre le port sur ta box)");

    // ---------- Steam P2P ----------
    public static string SteamHostCheckbox => T("Steam friends join via P2P (no port)", "Amis Steam via P2P (sans port)");
    public static string SteamHostVisible => T("Your Steam friends can join you: F8 > Join a Steam friend", "Tes amis Steam peuvent te rejoindre : F8 > Rejoindre un ami Steam");
    public static string SteamUnavailable => T("Steam P2P unavailable (see SBMultiplayer.log)", "P2P Steam indisponible (voir SBMultiplayer.log)");
    public static string JoinFriendSection => T("Join a Steam friend", "Rejoindre un ami Steam");
    public static string JoinFriendButton => T("Friends hosting a game", "Amis qui hébergent une partie");
    public static string JoinFriendHint => T("Friends playing STUNTBOOST and hosting a game with the mod:", "Amis qui jouent à STUNTBOOST et hébergent une partie avec le mod :");
    public static string NoFriendHosting => T("No friend is hosting a game right now", "Aucun ami n'héberge de partie pour le moment");
    public static string OtherVersion => T("other mod version", "autre version du mod");
    public static string Refresh => T("Refresh", "Actualiser");
    public static string JoiningFriend(string name) => T($"Joining {name}'s game through Steam...", $"Connexion à la partie de {name} via Steam...");

    // ---------- Panel: pause and match ----------
    public static string PausedHeader => T("PAUSED", "PAUSE");
    public static string MatchStandings => T("Match", "Match");
    public static string Points(int n) => T($"{n} pts", $"{n} pts");

    // ---------- Admin ----------
    public static string PasswordField => T("Server password", "Mot de passe du serveur");
    public static string AdminTitle => T("Admin", "Admin");
    public static string AdminNeedsConnection => T("Connect to a server to use the admin panel", "Connecte-toi à un serveur pour utiliser le panneau admin");
    public static string AdminPasswordField => T("Admin password", "Mot de passe admin");
    public static string AdminLoginButton => T("Log in as admin", "Connexion admin");
    public static string AdminNext => T("Next (skip)", "Suivant (passer)");
    public static string AdminRestart => T("Restart round", "Relancer le round");
    public static string AdminPause => T("Pause / Resume", "Pause / Reprendre");
    public static string AdminExtend => T("+1 min", "+1 min");
    public static string AdminResetMatch => T("Reset match scores", "Remettre le match à zéro");
    public static string AdminMaps => T("Go to map:", "Aller à la map :");
    public static string AdminKick(string name) => T($"Kick {name}", $"Expulser {name}");
    public static string AdminPlayers => T("Players:", "Joueurs :");
    public static string AdminNoRotation => T("No map rotation on this server (free mode)", "Pas de rotation de maps sur ce serveur (mode libre)");

    public static string NewBest(string time) => T($"{time} - new personal best!", $"{time} - nouveau meilleur temps !");
    public static string TimeVsBest(string time, string best, float gapSeconds) =>
        T($"{time} (best: {best}, +{gapSeconds:0.000})", $"{time} (meilleur : {best}, +{gapSeconds:0.000})");
}
