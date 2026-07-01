using System.Collections.Generic;
using Arenas.Arena;
using Arenas.Shared;
using Sharp.Shared.Units;

namespace Arenas.Database;

/// <summary>
/// Abstraction for weapon + round-type preference persistence (per SteamID64).
/// Default implementation: <see cref="CookiePrefStore"/> (IClientPreference, no DB required).
///
/// A future SQL implementation would live in a separate Arenas.Database project with
/// SqlSugar types kept internal; swap it in via DI without touching Core.
///
/// Note: there is NO ELO / points / rank schema — the Arenas ladder is purely transient
/// (winners-stay queue rebuilt each round). Do not scaffold any ranking persistence here.
/// </summary>
internal interface IArenasStore
{
    /// <summary>Stored weapon classname for this player, or <c>null</c> = random / no preference.</summary>
    string? GetWeaponPreference(SteamID steamId, WeaponType type);

    /// <summary>Persist a weapon preference. Pass <c>null</c> to clear (random).</summary>
    void SetWeaponPreference(SteamID steamId, WeaponType type, string? classname);

    /// <summary>
    /// Enabled round-type IDs for this player, resolved from cookies keyed by stable round name.
    /// Unset entries fall back to each <see cref="RoundType.EnabledByDefault"/>.
    /// </summary>
    HashSet<int> GetEnabledRoundTypeIds(SteamID steamId, IReadOnlyList<RoundType> allRoundTypes);

    /// <summary>Persist the enabled/disabled flag for a round type (keyed by stable name, NOT runtime ID).</summary>
    void SetRoundTypeEnabled(SteamID steamId, string roundTypeName, bool enabled);
}
