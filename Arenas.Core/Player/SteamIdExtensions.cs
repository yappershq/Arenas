namespace Arenas.Player;

/// <summary>Shared SteamID validation helper.</summary>
internal static class SteamIdExtensions
{
    internal static bool IsValidSteamId(this ulong steamId)
        => steamId > 76561197960265728UL;
}
