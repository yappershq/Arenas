using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace Arenas.Utils;

/// <summary>
/// Thin localization helper over <see cref="ILocalizerManager"/>. Every user-facing chat/center
/// line routes through here so a missing LocalizerManager degrades to a silent no-op instead of a
/// hardcoded English string. Server-side renders fix culture to <c>en-US</c>; per-client lines use
/// the client's own locale.
/// </summary>
internal static class Loc
{
    private const string ServerCulture = "en-US";

    /// <summary>Localized chat line to one client.</summary>
    public static void Chat(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm?.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);

    /// <summary>Localized center-text line to one client.</summary>
    public static void Center(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm?.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Center);

    /// <summary>Localized chat line to every in-game human (each rendered in their own locale).</summary>
    public static void ChatAll(ILocalizerManager? lm, IClientManager clients, string key, params object?[] args)
    {
        if (lm is null) return;
        foreach (var client in clients.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue;
            lm.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);
        }
    }

    /// <summary>Per-client localized string (menu titles/items). Falls back to the key if absent.
    /// Color tokens are stripped — MenuManager renders HTML and colors the menu itself, so a chat
    /// token like {lightred} would otherwise show as literal text in the menu.</summary>
    public static string Str(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm is null ? key : ChatFormat.StripColorCodes(lm.For(client).Text(key, args));

    /// <summary>Server-side localized string. Falls back to the key.</summary>
    public static string Format(ILocalizerManager? lm, string key, params object?[] args)
        => lm is null ? key : ChatFormat.ProcessColorCodes(lm.Format(ServerCulture, key, args));
}
