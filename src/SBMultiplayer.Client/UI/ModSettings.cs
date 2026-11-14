using BytingLib;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Réglages modifiables en jeu, sous forme de ValueEvent (le type attendu par les sliders et cases du jeu).
/// Chaque changement s'applique immédiatement et est enregistré dans SBMultiplayer.json.
/// </summary>
internal static class ModSettings
{
    public static ValueEvent<float> GhostOpacity { get; } = Bind(ModConfig.Current.GhostOpacity, v => ModConfig.Current.GhostOpacity = v);

    public static ValueEvent<bool> ShowOtherPlayers { get; } = Bind(ModConfig.Current.ShowOtherPlayers, v => ModConfig.Current.ShowOtherPlayers = v);

    public static ValueEvent<bool> ShowNameTags { get; } = Bind(ModConfig.Current.ShowNameTags, v => ModConfig.Current.ShowNameTags = v);

    public static ValueEvent<float> NameTagMaxDistance { get; } = Bind(ModConfig.Current.NameTagMaxDistance, v => ModConfig.Current.NameTagMaxDistance = v);

    public static ValueEvent<bool> ShowRoundPanel { get; } = Bind(ModConfig.Current.ShowRoundPanel, v => ModConfig.Current.ShowRoundPanel = v);

    public static ValueEvent<float> RoundPanelScale { get; } = Bind(ModConfig.Current.RoundPanelScale, v => ModConfig.Current.RoundPanelScale = v);

    public static ValueEvent<bool> ShowSplits { get; } = Bind(ModConfig.Current.ShowSplits, v => ModConfig.Current.ShowSplits = v);

    public static ValueEvent<bool> ShowChat { get; } = Bind(ModConfig.Current.ShowChat, v => ModConfig.Current.ShowChat = v);

    public static ValueEvent<float> ChatScale { get; } = Bind(ModConfig.Current.ChatScale, v => ModConfig.Current.ChatScale = v);

    public static ValueEvent<bool> NotificationsOnTop { get; } = Bind(ModConfig.Current.NotificationsOnTop, v => ModConfig.Current.NotificationsOnTop = v);

    private static ValueEvent<T> Bind<T>(T initial, Action<T> apply)
    {
        var value = new ValueEvent<T>(initial);
        value.OnChange += v =>
        {
            apply(v);
            ModConfig.Save();
        };
        return value;
    }
}
