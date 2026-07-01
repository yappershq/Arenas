using System.Collections.Generic;
using System.Linq;
using Sharp.Shared.Units;

namespace Arenas.Queue;

/// <summary>
/// A pending or accepted duel challenge between two players. PlayerSlot-keyed only — never store
/// <c>IGameClient</c>/pawn (K4's ChallengeModel cached controllers for the challenge lifetime; we
/// re-resolve by slot every callback). Evicted on disconnect and when consumed into an arena.
/// </summary>
internal sealed record Challenge(PlayerSlot Challenger, PlayerSlot Target)
{
    public bool Accepted { get; set; }
}

/// <summary>
/// Owns the live challenge list (K4's <c>List&lt;ChallengeModel&gt;</c>). Slot-based; a player can be
/// in at most one challenge at a time. Accepted challenges are drained by RoundFlowModule at
/// round_prestart, seating both players into the same arena as a 1v1 duel.
/// </summary>
internal sealed class ChallengeService
{
    private readonly List<Challenge> _challenges = [];

    public IReadOnlyList<Challenge> Challenges => _challenges;

    /// <summary>The challenge this slot is a participant in (either side), or null.</summary>
    public Challenge? FindForSlot(PlayerSlot slot)
        => _challenges.FirstOrDefault(c => c.Challenger == slot || c.Target == slot);

    public bool IsInChallenge(PlayerSlot slot) => FindForSlot(slot) is not null;

    /// <summary>Create a pending challenge. Returns false if either party is already challenged.</summary>
    public Challenge? Add(PlayerSlot challenger, PlayerSlot target)
    {
        if (challenger == target) return null;
        if (IsInChallenge(challenger) || IsInChallenge(target)) return null;

        var challenge = new Challenge(challenger, target);
        _challenges.Add(challenge);
        return challenge;
    }

    public void Remove(Challenge challenge) => _challenges.Remove(challenge);

    /// <summary>Drop any challenge involving this slot (disconnect / consumed / declined).</summary>
    public void RemoveForSlot(PlayerSlot slot)
        => _challenges.RemoveAll(c => c.Challenger == slot || c.Target == slot);

    /// <summary>Accepted challenges, in insertion order — drained one-per-arena at prestart.</summary>
    public List<Challenge> DrainAccepted()
    {
        var accepted = _challenges.Where(c => c.Accepted).ToList();
        foreach (var c in accepted) _challenges.Remove(c);
        return accepted;
    }

    public void Clear() => _challenges.Clear();
}
