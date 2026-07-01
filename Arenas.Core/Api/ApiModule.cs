using System.Collections.Generic;
using System.Linq;
using Arenas.Arena;
using Arenas.Plugins;
using Arenas.Queue;
using Arenas.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;

namespace Arenas.Api;

/// <summary>
/// Wires resolver delegates into the already-published ArenasApi (ArenasPlugin.PostInit publishes it;
/// this module fills in the state-query delegates once the arena/queue modules are live) and exposes
/// registered special round types to RoundFlowModule's round-type selection.
/// </summary>
internal sealed class ApiModule : IModule
{
    private readonly InterfaceBridge      _bridge;
    private readonly ILogger<ApiModule>   _logger;
    private readonly ArenasApi            _api;
    private readonly ArenaManagerModule   _arenaManager;
    private readonly QueueModule          _queueModule;

    private QueueManager QueueManager => _queueModule.QueueManager;

    public ApiModule(InterfaceBridge bridge, ILogger<ApiModule> logger, ArenasApi api, ArenaManagerModule arenaManager, QueueModule queueModule)
    {
        _bridge       = bridge;
        _logger       = logger;
        _api          = api;
        _arenaManager = arenaManager;
        _queueModule  = queueModule;
    }

    public bool Init() => true;

    public void OnPostInit()
    {
        _api.PlacementResolver = steamId => ResolveSlot(steamId) is { } slot
            ? _arenaManager.FindArenaForSlot(slot)?.ArenaId
            : null;

        _api.ArenaNameResolver = steamId => ResolveSlot(steamId) is { } slot
            ? QueueManager.GetState(slot)?.ArenaTag ?? string.Empty
            : string.Empty;

        _api.AfkResolver = steamId => ResolveSlot(steamId) is { } slot
            && (QueueManager.GetState(slot)?.Afk ?? false);

        _api.OpponentsResolver = steamId =>
        {
            if (ResolveSlot(steamId) is not { } slot) return [];
            return _arenaManager.FindOpponents(slot)
                .Select(s => _bridge.ClientManager.GetGameClient(s))
                .Where(c => c is not null)
                .Select(c => c!.SteamId)
                .ToArray();
        };

        _api.AfkSetter = (steamId, afk) =>
        {
            if (ResolveSlot(steamId) is not { } slot) return;
            QueueManager.GetOrCreateState(slot).Afk = afk;
        };

        _logger.LogInformation("[Arenas] ArenasApi state-query resolvers wired.");
    }

    // TerminateRound / WeaponPrefResolver are wired by RoundFlowModule / PreferencesModule themselves
    // in their own OnAllSharpModulesLoaded (both already depend on ApiModule for round-type wiring,
    // so wiring the reverse delegates here would create a DI cycle).

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown() { }

    private Sharp.Shared.Units.PlayerSlot? ResolveSlot(SteamID steamId)
    {
        var client = _bridge.ClientManager.GetGameClient(steamId);
        return client is { IsInGame: true } ? client.Slot : null;
    }

    /// <summary>Special round types registered by external plugins via IArenasShared.RegisterRoundType.</summary>
    public List<RoundType> GetRegisteredRoundTypes()
        => _api.RoundTypes.Values.Select(e => new RoundType
        {
            Id               = 10000 + e.Id, // offset to avoid clashing with built-in ids
            Name             = e.Name,
            TeamSize         = e.TeamSize,
            EnabledByDefault = e.EnabledByDefault,
            OnStart          = e.OnStart,
            OnEnd            = e.OnEnd,
        }).ToList();
}
