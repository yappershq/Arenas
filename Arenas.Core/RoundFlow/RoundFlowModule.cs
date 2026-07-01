using System;
using System.Collections.Generic;
using System.Linq;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Loadout;
using Arenas.Plugins;
using Arenas.Queue;
using Arenas.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Arenas.RoundFlow;

/// <summary>
/// Orchestrates the arena ladder round loop — the heart of the K4 port:
///   round_prestart (pre) — compute per-arena win/lose from last round, rebuild the ranked ladder
///                           (winners up / losers down), reshuffle arena display order, assign teams
///                           top-down, pick each arena's round type from both teams' prefs intersection.
///   round_start          — clear "between rounds" flag.
///   round_end             — compute arena results (win/tie/no-opponent) for the NEXT prestart.
///   PlayerSpawnPost       — teleport to the player's assigned arena spawn, heal to 100, give loadout.
///   PlayerKilledPost      — deferred TerminateRoundIfPossible (K4 fires it 1s after any death).
///   round_mvp (pre)       — suppressed (K4 always blocks MVP awards; arena "MVP" is tracked separately).
/// </summary>
internal sealed class RoundFlowModule : IModule, IEventListener
{
    private readonly ILogger<RoundFlowModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly Config.ConfigModule      _config;
    private readonly QueueModule              _queueModule;
    private readonly ArenaManagerModule       _arenaManager;
    private readonly LoadoutModule            _loadout;
    private readonly Player.PreferencesModule _preferences;
    private readonly Api.ApiModule            _api;
    private readonly ArenasApi                _arenasApi;

    private QueueManager QueueManager => _queueModule.QueueManager;

    private bool _isBetweenRounds;
    private List<RoundType> _roundTypes = [];

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public RoundFlowModule(
        ILogger<RoundFlowModule> logger,
        InterfaceBridge          bridge,
        Config.ConfigModule      config,
        QueueModule              queueModule,
        ArenaManagerModule       arenaManager,
        LoadoutModule            loadout,
        Player.PreferencesModule preferences,
        Api.ApiModule            api,
        ArenasApi                arenasApi)
    {
        _logger       = logger;
        _bridge       = bridge;
        _config       = config;
        _queueModule  = queueModule;
        _arenaManager = arenaManager;
        _loadout      = loadout;
        _preferences  = preferences;
        _api          = api;
        _arenasApi    = arenasApi;
    }

    public bool Init()
    {
        _roundTypes = _config.Config.RoundSettings.Count > 0
            ? RoundTypeCatalog.FromConfig(_config.Config.RoundSettings)
            : RoundTypeCatalog.Defaults();
        return true;
    }

    public void OnPostInit()
    {
        _bridge.EventManager.HookEvent("round_prestart");
        _bridge.EventManager.HookEvent("round_start");
        _bridge.EventManager.HookEvent("round_end");
        _bridge.EventManager.HookEvent("round_mvp");
        _bridge.EventManager.InstallEventListener(this);

        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _bridge.HookManager.PlayerKilledPost.InstallForward(OnPlayerKilledPost);
    }

    public void OnAllSharpModulesLoaded()
    {
        // Fold in any special round types registered by external plugins (Arenas.SpecialRounds et al).
        RefreshRoundTypesWithApi();

        _arenasApi.TerminateRound = TerminateRoundIfPossible;
    }

    public void Shutdown()
    {
        _bridge.EventManager.RemoveEventListener(this);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _bridge.HookManager.PlayerKilledPost.RemoveForward(OnPlayerKilledPost);
    }

    /// <summary>Called by ApiModule whenever a special round type is (un)registered.</summary>
    public void RefreshRoundTypesWithApi()
    {
        var baseTypes = _config.Config.RoundSettings.Count > 0
            ? RoundTypeCatalog.FromConfig(_config.Config.RoundSettings)
            : RoundTypeCatalog.Defaults();

        var special = _api.GetRegisteredRoundTypes();
        _roundTypes = [.. baseTypes, .. special];
    }

    public IReadOnlyList<RoundType> RoundTypes => _roundTypes;

    // ── IEventListener ────────────────────────────────────────────────────

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly)
    {
        // K4 always blocks round_mvp — arena "MVP" tracking is separate (ArenaSlot/PlayerState.Mvps).
        if (@event.Name.Equals("round_mvp", StringComparison.Ordinal))
            return false;
        return true;
    }

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        switch (@event.Name)
        {
            case "round_prestart": OnRoundPreStart(); break;
            case "round_start":    _isBetweenRounds = false; break;
            case "round_end":      OnRoundEnd();      break;
        }
    }

    // ── round_prestart: ladder rebuild + arena assignment ───────────────────

    private void OnRoundPreStart()
    {
        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        var arenaWinners = new Queue<PlayerSlot>();
        var arenaLosers  = new Queue<PlayerSlot>();

        foreach (var arena in _arenaManager.Arenas)
        {
            switch (arena.Result.ResultType)
            {
                case ArenaResultType.Win:
                    EnqueueAll(arena.Result.Winners, arenaWinners);
                    EnqueueAll(arena.Result.Losers, arenaLosers);
                    break;
                case ArenaResultType.NoOpponent:
                    EnqueueAll(arena.Result.Winners, arenaWinners);
                    break;
                case ArenaResultType.Tie:
                    EnqueueAll(arena.Team1, arenaLosers);
                    EnqueueAll(arena.Team2, arenaLosers);
                    break;
                case ArenaResultType.Empty:
                default:
                    break;
            }
        }

        var ranked = QueueManager.BuildRankedQueue(arenaWinners, arenaLosers);

        // Split off AFK players — they stay queued but never seated.
        var notAfk = new Queue<PlayerSlot>();
        foreach (var slot in ranked)
        {
            if (!IsSlotConnected(slot)) continue;

            var state = QueueManager.GetOrCreateState(slot);
            if (state.Afk)
            {
                QueueManager.RequeueTail(slot);
            }
            else
            {
                notAfk.Enqueue(slot);
            }
        }

        _arenaManager.Shuffle();

        // Prioritize real players over bots for arena seating (K4: OrderBy(IsBot)).
        notAfk = new Queue<PlayerSlot>(notAfk.OrderBy(IsBotSlot));

        var displayIndex = 1;
        foreach (var arena in _arenaManager.Arenas)
        {
            arena.Reset();

            if (notAfk.Count >= 1)
            {
                var p1 = notAfk.Dequeue();
                PlayerSlot? p2 = notAfk.TryDequeue(out var p2Slot) ? p2Slot : null;

                var roundType = GetCommonRoundType(
                    GetEnabledTypes(p1),
                    p2 is { } p2s ? GetEnabledTypes(p2s) : null);

                AssignArena(arena, [p1], p2 is { } p2v ? [p2v] : null, roundType, displayIndex);
                displayIndex++;
            }
            else
            {
                AssignArena(arena, null, null, null, displayIndex);
                displayIndex++;
            }
        }

        // Anyone left over goes back to the tail of the waiting queue.
        while (notAfk.Count > 0)
        {
            var slot  = notAfk.Dequeue();
            var state = QueueManager.GetOrCreateState(slot);
            state.ArenaTag = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Waiting");
            QueueManager.RequeueTail(slot);
        }
    }

    private void AssignArena(
        ArenaSlot arena, List<PlayerSlot>? team1, List<PlayerSlot>? team2, RoundType? roundType, int displayId)
    {
        arena.ArenaId          = displayId;
        arena.Team1             = team1;
        arena.Team2             = team2;
        arena.CurrentRoundType = roundType ?? (_roundTypes.Count > 0 ? _roundTypes[0] : null);
        arena.Result           = new ArenaResult(ArenaResultType.Empty, null, null);

        if (team1 is null && team2 is null) return;

        var swapSides = Random.Shared.Next(2) == 1;
        var t1Spawns  = swapSides ? arena.CtSpawns : arena.TSpawns;
        var t2Spawns  = swapSides ? arena.TSpawns  : arena.CtSpawns;

        SeatTeam(arena, team1, t1Spawns, CStrikeTeam.TE);
        SeatTeam(arena, team2, t2Spawns, CStrikeTeam.CT);
    }

    private void SeatTeam(ArenaSlot arena, List<PlayerSlot>? team, List<Vector> spawns, CStrikeTeam switchTo)
    {
        if (team is null || spawns.Count == 0) return;

        var spawnPool = new List<Vector>(spawns);
        foreach (var slot in team)
        {
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null) continue;

            var idx = Random.Shared.Next(spawnPool.Count);
            arena.SpawnAssignment[slot] = spawnPool[idx];
            spawnPool.RemoveAt(idx);

            var state = QueueManager.GetOrCreateState(slot);
            state.ArenaTag = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Arena", arena.ArenaId);

            if (controller.Team > CStrikeTeam.Spectator)
                controller.SwitchTeam(switchTo);
            else
                controller.ChangeTeam(switchTo);

            if (!client.IsFakeClient)
            {
                var opponents = arena.Team1 == team ? arena.Team2 : arena.Team1;
                var roundName = arena.CurrentRoundType?.Name ?? "Arenas_Round_Rifle";
                Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_ArenaRoundStart",
                    arena.ArenaId, Loc.Format(_bridge.LocalizerManager, roundName), OpponentCount(opponents));
            }
        }
    }

    private static int OpponentCount(List<PlayerSlot>? opponents) => opponents?.Count ?? 0;

    private static void EnqueueAll(IReadOnlyList<PlayerSlot>? source, Queue<PlayerSlot> target)
    {
        if (source is null) return;
        var seen = new HashSet<PlayerSlot>(target);
        foreach (var slot in source)
        {
            if (seen.Add(slot))
                target.Enqueue(slot);
        }
    }

    private bool IsSlotConnected(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { IsInGame: true };

    private bool IsBotSlot(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: true };

    private HashSet<int> GetEnabledTypes(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        if (client is null) return [.. _roundTypes.Where(r => r.EnabledByDefault).Select(r => r.Id)];
        return _preferences.GetEnabledRoundTypeIds(client.SteamId, _roundTypes);
    }

    /// <summary>K4's GetCommonRoundType: intersection of both players' enabled 1v1 round types,
    /// random pick; falls back to the configured default round, then to any 1v1 round type.</summary>
    private RoundType? GetCommonRoundType(HashSet<int> prefs1, HashSet<int>? prefs2)
    {
        var common = prefs2 is null ? prefs1 : new HashSet<int>(prefs1.Intersect(prefs2));
        var usable = _roundTypes.Where(r => r.TeamSize < 2 && common.Contains(r.Id)).ToList();

        if (usable.Count > 0) return usable[Random.Shared.Next(usable.Count)];

        var defaultName = _config.Config.DefaultWeaponSettings.DefaultRound;
        var defaultType = _roundTypes.FirstOrDefault(r => r.Name == defaultName);
        if (defaultType is not null) return defaultType;

        var any = _roundTypes.Where(r => r.TeamSize < 2).ToList();
        return any.Count > 0 ? any[Random.Shared.Next(any.Count)] : null;
    }

    // ── round_end: compute per-arena results for the NEXT round_prestart ───

    private void OnRoundEnd()
    {
        _isBetweenRounds = true;

        foreach (var arena in _arenaManager.Arenas)
            ComputeArenaResult(arena);
    }

    private void ComputeArenaResult(ArenaSlot arena)
    {
        if (arena.ArenaId == -1)
        {
            // warmup arena — no ladder result, just release players back to the waiting queue on next prestart.
            arena.Result = new ArenaResult(ArenaResultType.Empty, null, null);
            return;
        }

        if (arena.Team1 is null && arena.Team2 is null)
        {
            arena.Result = new ArenaResult(ArenaResultType.Empty, null, null);
            return;
        }

        if (arena.Team1 is null || arena.Team2 is null)
        {
            var winners = arena.Team1 ?? arena.Team2!;
            arena.Result = new ArenaResult(ArenaResultType.NoOpponent, winners, null);
            return;
        }

        var team1Alive = arena.Team1.Count(IsSlotAlive);
        var team2Alive = arena.Team2.Count(IsSlotAlive);

        if (team1Alive == team2Alive)
        {
            arena.Result = new ArenaResult(ArenaResultType.Tie, arena.Team1, arena.Team2);
            return;
        }

        var win  = team1Alive > team2Alive ? arena.Team1 : arena.Team2;
        var lose = team1Alive > team2Alive ? arena.Team2 : arena.Team1;

        foreach (var slot in win)
            QueueManager.GetOrCreateState(slot).Mvps++;

        arena.Result = new ArenaResult(ArenaResultType.Win, win, lose);

        // Arena outcome (win/lose slot lists) drives the internal ladder rebuild only — the ladder is
        // self-contained (climb on win, drop on loss). No external ranking dependency: LevelRanks, if
        // installed, awards its own global points from kills out of the box and needs no integration.
    }

    private bool IsSlotAlive(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        var pawn   = client?.GetPlayerController()?.GetPlayerPawn();
        return pawn is { IsAlive: true };
    }

    // ── PlayerSpawnPost: teleport + loadout ──────────────────────────────────

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;
        if (client.IsFakeClient && !client.IsInGame) return;

        var slot  = client.Slot;
        var arena = _arenaManager.FindArenaForSlot(slot);
        if (arena is null) return;

        if (!arena.SpawnAssignment.TryGetValue(slot, out var spawnOrigin)) return;

        var pawn = @params.Pawn;
        pawn.Teleport(spawnOrigin, null, new Vector(0, 0, 0));
        pawn.Health = 100;

        if (arena.CurrentRoundType is { } roundType)
        {
            if (roundType.OnStart is not null)
            {
                var team1Ids = ToSteamIds(arena.Team1);
                var team2Ids = ToSteamIds(arena.Team2);
                roundType.OnStart(team1Ids, team2Ids);
            }
            else
            {
                _loadout.GiveLoadout(client, pawn, roundType);
            }
        }
    }

    private SteamID[] ToSteamIds(List<PlayerSlot>? slots)
    {
        if (slots is null) return [];
        return slots
            .Select(s => _bridge.ClientManager.GetGameClient(s))
            .Where(c => c is not null)
            .Select(c => c!.SteamId)
            .ToArray();
    }

    // ── PlayerKilledPost: deferred TerminateRoundIfPossible ─────────────────

    private void OnPlayerKilledPost(IPlayerKilledForwardParams @params)
    {
        _bridge.ModSharp.PushTimer(TerminateRoundIfPossible, 1.0, GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
    }

    // ── termination ──────────────────────────────────────────────────────

    public void TerminateRoundIfPossible()
    {
        if (_isBetweenRounds) return;

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        var players = _bridge.ClientManager.GetGameClients(inGame: true)
            .Where(c => !c.IsHltv && c.GetPlayerController() is { Team: > CStrikeTeam.Spectator })
            .ToList();

        if (players.All(c => c.IsFakeClient)) return;

        var allFinished = _arenaManager.Arenas.All(a => !HasRealPlayers(a) || HasFinished(a));
        if (!allFinished) return;

        _isBetweenRounds = true;

        var alive = players.Where(c => c.GetPlayerController()?.GetPlayerPawn() is { IsAlive: true }).ToList();
        var tCount  = alive.Count(c => c.GetPlayerController()!.Team == CStrikeTeam.TE);
        var ctCount = alive.Count(c => c.GetPlayerController()!.Team == CStrikeTeam.CT);

        RoundEndReason reason;
        if (tCount > ctCount) reason = RoundEndReason.TerroristsWin;
        else if (ctCount > tCount) reason = RoundEndReason.CTsWin;
        else reason = _config.Config.CompatibilitySettings.PreventDrawRounds
            ? (Random.Shared.Next(2) == 0 ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin)
            : RoundEndReason.RoundDraw;

        rules.TerminateRound(3f, reason);
    }

    private bool HasRealPlayers(ArenaSlot a)
        => (a.Team1?.Any(s => !IsBotSlot(s) && IsSlotConnected(s)) ?? false)
        || (a.Team2?.Any(s => !IsBotSlot(s) && IsSlotConnected(s)) ?? false);

    private bool HasFinished(ArenaSlot a)
    {
        var active = (a.Team1?.Any(s => IsSlotConnected(s) && !QueueManager.GetOrCreateState(s).Afk) ?? false)
                  && (a.Team2?.Any(s => IsSlotConnected(s) && !QueueManager.GetOrCreateState(s).Afk) ?? false);
        if (!active) return true;

        return (a.Team1?.All(s => !IsSlotAlive(s)) ?? false) || (a.Team2?.All(s => !IsSlotAlive(s)) ?? false);
    }
}
