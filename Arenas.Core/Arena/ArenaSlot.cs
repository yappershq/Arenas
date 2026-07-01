using System.Collections.Generic;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Arenas.Arena;

public enum ArenaResultType { Win, Tie, NoOpponent, Empty }

public readonly record struct ArenaResult(ArenaResultType ResultType, IReadOnlyList<PlayerSlot>? Winners, IReadOnlyList<PlayerSlot>? Losers);

/// <summary>
/// One physical arena instance (a clustered spawn pair) plus the two teams currently occupying it.
/// ArenaId semantics (ported from K4): -1 = warmup, -2 = challenge, >=0 = ranked ladder slot (display index).
/// Team lists are PlayerSlot — never store IGameClient/pawn/controller here, re-resolve every callback.
/// </summary>
internal sealed class ArenaSlot
{
    public required int Index { get; init; } // stable identity — position in ArenaManagerModule.Arenas
    public required List<Vector> CtSpawns { get; init; }
    public required List<Vector> TSpawns  { get; init; }

    public int ArenaId = -1;
    public RoundType? CurrentRoundType;
    public ArenaResult Result = new(ArenaResultType.Empty, null, null);

    public List<PlayerSlot>? Team1; // Terrorist side
    public List<PlayerSlot>? Team2; // CT side

    /// <summary>Per-player spawn assignment for this round (slot -> spawn origin).</summary>
    public readonly Dictionary<PlayerSlot, Vector> SpawnAssignment = new();

    public void Reset()
    {
        ArenaId          = -1;
        CurrentRoundType = null;
        Result           = new ArenaResult(ArenaResultType.Empty, null, null);
        Team1             = null;
        Team2             = null;
        SpawnAssignment.Clear();
    }
}
