using Sharp.Shared.Units;

namespace Arenas.SpecialRounds;

/// <summary>
/// Per-player special-round tracking, PlayerSlot-indexed (bots have SteamID64=0 — never key by SteamID).
/// Cleared on round end / map change. Never stores a controller or pawn; the hooks re-resolve by slot.
/// </summary>
internal sealed class SpecialRoundState
{
    private static readonly int MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();

    // Which special-round behaviour is active for the player in this slot, or null (not in one).
    private readonly SpecialRoundKind?[] _active = new SpecialRoundKind?[MaxSlots];

    public void Set(PlayerSlot slot, SpecialRoundKind kind)
    {
        if (slot.IsValid()) _active[slot.AsPrimitive()] = kind;
    }

    public SpecialRoundKind? Get(PlayerSlot slot)
        => slot.IsValid() ? _active[slot.AsPrimitive()] : null;

    public bool IsActive(PlayerSlot slot, SpecialRoundKind kind)
        => slot.IsValid() && _active[slot.AsPrimitive()] == kind;

    public void Clear(PlayerSlot slot)
    {
        if (slot.IsValid()) _active[slot.AsPrimitive()] = null;
    }

    public void ClearAll()
    {
        for (var i = 0; i < _active.Length; i++) _active[i] = null;
    }
}
