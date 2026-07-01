using System.Collections.Generic;
using Arenas.Shared;
using Sharp.Shared.Units;

namespace Arenas;

/// <summary>
/// Implements <see cref="IArenasShared"/> — the public round-type registry + arena-placement lookup
/// published to external plugins (Arenas.SpecialRounds and any third party).
/// Registered via RegisterSharpModuleInterface in <see cref="ArenasPlugin.PostInit"/>.
///
/// Core's round-flow / queue modules populate the placement resolver and invoke the registered
/// round callbacks; external modules only register/unregister and read placement.
/// </summary>
internal sealed class ArenasApi : IArenasShared
{
    internal readonly record struct RoundTypeEntry(
        int Id, string Name, int Weight, bool Enabled, ArenaRoundCallback OnStart, ArenaRoundCallback OnEnd);

    private readonly Dictionary<int, RoundTypeEntry> _roundTypes = new();
    private int _nextId = 1;

    /// <summary>Placement resolver injected by Core's queue module; returns null until wired.</summary>
    internal Func<SteamID, int?>? PlacementResolver { get; set; }

    internal IReadOnlyDictionary<int, RoundTypeEntry> RoundTypes => _roundTypes;

    public int RegisterRoundType(string name, int weight, bool enabled, ArenaRoundCallback onStart, ArenaRoundCallback onEnd)
    {
        var id = _nextId++;
        _roundTypes[id] = new RoundTypeEntry(id, name, weight, enabled, onStart, onEnd);
        return id;
    }

    public void UnregisterRoundType(int id) => _roundTypes.Remove(id);

    public int? GetArenaPlacement(SteamID steamId) => PlacementResolver?.Invoke(steamId);
}
