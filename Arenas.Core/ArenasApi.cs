using System;
using System.Collections.Generic;
using Arenas.Shared;
using Sharp.Shared.Units;

namespace Arenas;

/// <summary>
/// Implements <see cref="IArenasShared"/> — the public special-round registry + arena/player-state
/// queries published to external plugins (Arenas.SpecialRounds and any third party).
/// Registered via RegisterSharpModuleInterface in <see cref="ArenasPlugin.PostInit"/>.
///
/// Core's arena / queue modules inject the resolver delegates (they own the live arena state);
/// external modules only register/unregister round types and read state through here. Keeping the
/// resolvers as delegates avoids a Core→Shared type leak and a hard module reference cycle.
/// </summary>
internal sealed class ArenasApi : IArenasShared
{
    internal readonly record struct RoundTypeEntry(
        int Id, string Name, int TeamSize, bool EnabledByDefault, ArenaRoundCallback OnStart, ArenaRoundCallback OnEnd);

    private readonly Dictionary<int, RoundTypeEntry> _roundTypes = new();
    private int _nextId = 1;

    internal IReadOnlyDictionary<int, RoundTypeEntry> RoundTypes => _roundTypes;

    // Resolvers injected by Core once the arena/queue modules are live. Null-safe until wired.
    internal Func<SteamID, int?>?        PlacementResolver     { get; set; }
    internal Func<SteamID, string>?      ArenaNameResolver     { get; set; }
    internal Func<SteamID, bool>?        AfkResolver           { get; set; }
    internal Func<SteamID, SteamID[]>?   OpponentsResolver     { get; set; }
    internal Action<SteamID, bool>?      AfkSetter             { get; set; }
    internal Action?                     TerminateRound        { get; set; }
    internal Func<SteamID, WeaponType, string?>? WeaponPrefResolver { get; set; }

    // ── Registration ──────────────────────────────────────────────────────────

    public int RegisterRoundType(string name, int teamSize, bool enabledByDefault, ArenaRoundCallback onStart, ArenaRoundCallback onEnd)
    {
        var id = _nextId++;
        _roundTypes[id] = new RoundTypeEntry(id, name, teamSize, enabledByDefault, onStart, onEnd);
        return id;
    }

    public void UnregisterRoundType(int id) => _roundTypes.Remove(id);

    // ── State queries ─────────────────────────────────────────────────────────

    public int? GetArenaPlacement(SteamID steamId) => PlacementResolver?.Invoke(steamId);

    public string GetArenaName(SteamID steamId) => ArenaNameResolver?.Invoke(steamId) ?? string.Empty;

    public bool IsAfk(SteamID steamId) => AfkResolver?.Invoke(steamId) ?? false;

    public SteamID[] FindOpponents(SteamID steamId) => OpponentsResolver?.Invoke(steamId) ?? Array.Empty<SteamID>();

    public void SetAfk(SteamID steamId, bool afk) => AfkSetter?.Invoke(steamId, afk);

    public void TerminateRoundIfPossible() => TerminateRound?.Invoke();

    public string? GetPlayerWeaponPreference(SteamID steamId, WeaponType weaponType)
        => WeaponPrefResolver?.Invoke(steamId, weaponType);
}
