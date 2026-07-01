using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;

namespace Arenas.Queue;

/// <summary>
/// Owns WaitingArenaPlayers (K4's ranked ladder queue) as a PlayerSlot deque, plus per-slot
/// ephemeral state (AFK / round prefs cache / MVPs). Bots have SteamID64 = 0 — every set here is
/// keyed by PlayerSlot, never SteamID64, per the anti-pattern checklist.
/// </summary>
internal sealed class QueueManager
{
    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();

    private readonly ILogger<QueueManager> _logger;

    /// <summary>Ranked queue order — front = next to be seated. Mirrors K4's Queue&lt;ArenaPlayer&gt;.</summary>
    private readonly LinkedList<PlayerSlot> _waiting = new();

    private readonly PlayerState?[] _state = new PlayerState?[MaxSlots];

    public QueueManager(ILogger<QueueManager> logger) => _logger = logger;

    // ── per-slot state ────────────────────────────────────────────────────

    public PlayerState GetOrCreateState(PlayerSlot slot)
        => _state[slot.AsPrimitive()] ??= new PlayerState();

    public PlayerState? GetState(PlayerSlot slot)
        => _state[slot.AsPrimitive()];

    public void ClearSlot(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        _state[slot.AsPrimitive()] = null;
        _waiting.Remove(slot);
    }

    // ── waiting queue ─────────────────────────────────────────────────────

    public IReadOnlyCollection<PlayerSlot> Waiting => _waiting;

    public bool IsWaiting(PlayerSlot slot) => _waiting.Contains(slot);

    public void Enqueue(PlayerSlot slot)
    {
        if (_waiting.Contains(slot)) return;
        _waiting.AddLast(slot);
    }

    public bool TryDequeue(out PlayerSlot slot)
    {
        if (_waiting.First is null) { slot = default; return false; }
        slot = _waiting.First.Value;
        _waiting.RemoveFirst();
        return true;
    }

    public void RemoveFromWaiting(PlayerSlot slot) => _waiting.Remove(slot);

    /// <summary>K4's MoveQueue<T> — drain `from` into `to`, de-duplicating, preserving order.</summary>
    public static void MoveAll(Queue<PlayerSlot> from, Queue<PlayerSlot> to)
    {
        var seen = new HashSet<PlayerSlot>(to);
        while (from.Count > 0)
        {
            var item = from.Dequeue();
            if (seen.Add(item))
                to.Enqueue(item);
        }
    }

    /// <summary>
    /// K4's ranked-ladder rebuild: winners dequeue 2 first, then alternate winner/loser, then
    /// remaining losers, then the waiting queue tail. Returns the freshly-ranked slot order.
    /// Does not mutate this.Waiting — caller (RoundFlowModule) re-populates it for slots that don't
    /// get seated into an arena this round.
    /// </summary>
    public Queue<PlayerSlot> BuildRankedQueue(Queue<PlayerSlot> arenaWinners, Queue<PlayerSlot> arenaLosers)
    {
        var ranked = new Queue<PlayerSlot>();

        if (arenaWinners.Count > 1)
        {
            ranked.Enqueue(arenaWinners.Dequeue());
            ranked.Enqueue(arenaWinners.Dequeue());
        }

        while (arenaWinners.Count > 0)
        {
            ranked.Enqueue(arenaWinners.Dequeue());
            if (arenaLosers.Count > 0)
                ranked.Enqueue(arenaLosers.Dequeue());
        }

        MoveAll(arenaLosers, ranked);

        var waitingQueue = new Queue<PlayerSlot>(_waiting);
        _waiting.Clear();
        MoveAll(waitingQueue, ranked);

        return ranked;
    }

    /// <summary>Re-enqueue a slot at the tail of the waiting ladder (used for AFK / unseated players).</summary>
    public void RequeueTail(PlayerSlot slot) => Enqueue(slot);
}
