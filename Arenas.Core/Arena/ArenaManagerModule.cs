using System.Collections.Generic;
using System.Linq;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Listeners;
using Sharp.Shared.Units;

namespace Arenas.Arena;

/// <summary>
/// Owns the live arena list for the current map. Rebuilds via ArenaFinder on map change
/// (IGameListener.OnServerActivate — mirrors Retakes SpawnModule). Other modules (Queue, RoundFlow,
/// Loadout) mutate ArenaSlot.Team1/Team2/ArenaId/Result through here; this module only owns
/// creation/clustering + slot-placement lookups.
/// </summary>
internal sealed class ArenaManagerModule : IModule, IGameListener
{
    private readonly InterfaceBridge              _bridge;
    private readonly ILogger<ArenaManagerModule>  _logger;

    public List<ArenaSlot> Arenas { get; private set; } = [];

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public ArenaManagerModule(InterfaceBridge bridge, ILogger<ArenaManagerModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        RebuildArenas();
        return true;
    }

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    void IGameListener.OnServerActivate()
    {
        _logger.LogInformation("[Arenas] Map changed — rebuilding arenas.");
        RebuildArenas();
    }

    private void RebuildArenas()
    {
        var finder = new ArenaFinder(_logger, _bridge.EntityManager);
        var pairs  = finder.GetArenaPairs();

        var arenas = new List<ArenaSlot>(pairs.Count);
        for (var i = 0; i < pairs.Count; i++)
        {
            var (ct, t) = pairs[i];
            arenas.Add(new ArenaSlot
            {
                Index    = i,
                CtSpawns = ct.Select(e => e.GetAbsOrigin()).ToList(),
                TSpawns  = t.Select(e => e.GetAbsOrigin()).ToList(),
            });
        }

        Arenas = arenas;
    }

    /// <summary>Fisher-Yates shuffle of arena display order — K4's Arenas.Shuffle().</summary>
    public void Shuffle()
    {
        var n = Arenas.Count;
        while (n > 1)
        {
            n--;
            var k = System.Random.Shared.Next(n + 1);
            (Arenas[k], Arenas[n]) = (Arenas[n], Arenas[k]);
        }
    }

    // ── placement lookups (slot-keyed; no stored client/pawn) ───────────────

    public ArenaSlot? FindArenaForSlot(PlayerSlot slot)
        => Arenas.FirstOrDefault(a =>
            (a.Team1?.Contains(slot) ?? false) || (a.Team2?.Contains(slot) ?? false));

    public IReadOnlyList<PlayerSlot> FindOpponents(PlayerSlot slot)
    {
        var arena = FindArenaForSlot(slot);
        if (arena is null) return [];

        var onTeam1 = arena.Team1?.Contains(slot) ?? false;
        return (onTeam1 ? arena.Team2 : arena.Team1) ?? [];
    }

    public void RemoveSlotFromAllArenas(PlayerSlot slot)
    {
        foreach (var arena in Arenas)
        {
            arena.Team1?.Remove(slot);
            if (arena.Team1?.Count == 0) arena.Team1 = null;

            arena.Team2?.Remove(slot);
            if (arena.Team2?.Count == 0) arena.Team2 = null;

            arena.SpawnAssignment.Remove(slot);
        }
    }
}
