using System;
using System.Collections.Generic;
using System.Linq;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Database;
using Arenas.Loadout;
using Arenas.Plugins;
using Arenas.Queue;
using Arenas.Rounds;
using Arenas.Shared;
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
    private readonly IArenasStore             _store;
    private readonly Api.ApiModule            _api;
    private readonly ArenasApi                _arenasApi;
    private readonly RoundTypeRegistry        _registry;
    private readonly IArenasVipProvider       _vip;

    private QueueManager     QueueManager     => _queueModule.QueueManager;
    private ChallengeService ChallengeService => _queueModule.ChallengeService;

    private bool _isBetweenRounds;

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public RoundFlowModule(
        ILogger<RoundFlowModule> logger,
        InterfaceBridge          bridge,
        Config.ConfigModule      config,
        QueueModule              queueModule,
        ArenaManagerModule       arenaManager,
        LoadoutModule            loadout,
        IArenasStore             store,
        Api.ApiModule            api,
        ArenasApi                arenasApi,
        RoundTypeRegistry        registry,
        IArenasVipProvider       vip)
    {
        _logger       = logger;
        _bridge       = bridge;
        _config       = config;
        _queueModule  = queueModule;
        _arenaManager = arenaManager;
        _loadout      = loadout;
        _store        = store;
        _api          = api;
        _arenasApi    = arenasApi;
        _registry     = registry;
        _vip          = vip;
    }

    public bool Init()
    {
        var baseTypes = _config.Config.RoundSettings.Count > 0
            ? RoundTypeCatalog.FromConfig(_config.Config.RoundSettings)
            : RoundTypeCatalog.Defaults();
        _registry.Reset(baseTypes);
        return true;
    }

    public void OnPostInit()
    {
        _bridge.EventManager.HookEvent("round_prestart");
        _bridge.EventManager.HookEvent("round_start");
        _bridge.EventManager.HookEvent("round_freeze_end");
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

        // Re-fold whenever an addon (un)registers a round type — OnAllModulesLoaded order across plugins
        // is undefined, so an addon may register AFTER this ran. Chain (+=) so MenusModule can also subscribe.
        _arenasApi.OnRoundTypesChanged += RefreshRoundTypesWithApi;

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

        _registry.Reset(baseTypes);
        foreach (var special in _api.GetRegisteredRoundTypes())
            _registry.AppendSpecial(special);
    }

    public IReadOnlyList<RoundType> RoundTypes => _registry.All;

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
            case "round_prestart":   OnRoundPreStart();    break;
            case "round_start":      _isBetweenRounds = false; break;
            case "round_freeze_end": OnRoundFreezeEnd();   break;
            case "round_end":        OnRoundEnd();         break;
        }
    }

    // ── round_freeze_end: start the duel round-timer (splewis-style hard cap) ──────────────

    private void OnRoundFreezeEnd()
    {
        var seconds = _config.Config.RoundTimerSeconds;
        if (seconds <= 0) return; // 0 = disabled

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        // StopOnRoundEnd/StopOnMapEnd: ModSharp only clears a StopOnRoundEnd timer at the next round
        // RESTART, not at round_end itself — round_end and round_prestart/round_start for the next
        // round are not instantaneous, so this timer can still be pending when it fires. Correctness
        // here actually relies on the _isBetweenRounds guard below (set true in OnRoundEnd, cleared in
        // round_start) — never remove that guard even if this timer's auto-cancel looks sufficient.
        _bridge.ModSharp.PushTimer(ForceTerminateRoundOnTimeout, seconds,
            GameTimerFlags.StopOnRoundEnd | GameTimerFlags.StopOnMapEnd);
    }

    /// <summary>Hard round-timeout: force-ends the round even if no arena has "finished" yet (e.g. a
    /// stalemate where nobody died). Still respects the same active-matchup guard as TerminateRoundIfPossible
    /// (never force-ends a round that never really started — e.g. a lone player with no opponent).</summary>
    private void ForceTerminateRoundOnTimeout()
    {
        if (_isBetweenRounds) return; // already ended naturally in the meantime

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        // Only force-end while at least one arena is a live 2-sided matchup — same guard
        // TerminateRoundIfPossible uses via HasFinished/IsArenaActive.
        if (!_arenaManager.Arenas.Any(IsArenaActive)) return;

        var players = GetActiveRoundPlayers();
        if (players.All(c => c.IsFakeClient)) return;

        DoTerminateRound(rules, players);
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

        // VIP players seat first, then regular humans, bots last (K4: OrderBy(IsBot) + VIP priority).
        notAfk = new Queue<PlayerSlot>(notAfk.OrderBy(slot =>
            IsVipSlot(slot) ? 0 : IsBotSlot(slot) ? 2 : 1));

        // Drain accepted duel challenges — they get seated FIRST (one arena each), removed from the
        // ladder queue, before normal ladder pairing (K4 PluginEvents: challenges handled per arena
        // index ahead of AddTeamsToArena/AddPlayers). Drop stale challenges (a party disconnected).
        var challenges = ChallengeService.DrainAccepted()
            .Where(c => IsSlotConnected(c.Challenger) && IsSlotConnected(c.Target))
            .ToList();
        if (challenges.Count > 0)
        {
            var challengeSlots = challenges
                .SelectMany(c => new[] { c.Challenger, c.Target })
                .ToHashSet();
            notAfk = new Queue<PlayerSlot>(notAfk.Where(s => !challengeSlots.Contains(s)));
        }

        var displayIndex = 1;
        var challengeIndex = 0;
        foreach (var arena in _arenaManager.Arenas)
        {
            arena.Reset();

            if (challengeIndex < challenges.Count)
            {
                var c = challenges[challengeIndex++];
                var roundType = GetCommonRoundType(
                    GetEnabledTypes(c.Challenger), GetEnabledTypes(c.Target));
                AssignArena(arena, [c.Challenger], [c.Target], roundType, displayIndex, isChallenge: true);
                displayIndex++;
            }
            else if (notAfk.Count >= 1)
            {
                var p1 = notAfk.Dequeue();

                // Peek the next queued player to pick a shared round type — which may be an NvN
                // (2v2/3v3) type. TeamSize then decides how many we seat per side.
                var p2peekPrefs = notAfk.Count > 0 ? GetEnabledTypes(notAfk.Peek()) : null;
                var roundType   = GetCommonRoundType(GetEnabledTypes(p1), p2peekPrefs);

                var teamSize = roundType?.TeamSize ?? 1;
                // Not enough queued to fill both sides of the NvN round → downgrade to 1v1.
                if (teamSize > 1 && 1 + notAfk.Count < 2 * teamSize)
                {
                    teamSize  = 1;
                    roundType = GetCommonRoundType(GetEnabledTypes(p1), p2peekPrefs, maxTeamSize: 1);
                }

                var team1 = new List<PlayerSlot>(teamSize) { p1 };
                while (team1.Count < teamSize && notAfk.Count > 0) team1.Add(notAfk.Dequeue());
                var team2 = new List<PlayerSlot>(teamSize);
                while (team2.Count < teamSize && notAfk.Count > 0) team2.Add(notAfk.Dequeue());

                AssignArena(arena, team1, team2.Count > 0 ? team2 : null, roundType, displayIndex);
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

        // Re-queue accepted challenges that didn't fit into an arena (more challenges than arenas).
        // Both slots were pulled out of notAfk but never seated — re-insert them into the waiting
        // queue and re-register the challenge as accepted so it fires next round.
        while (challengeIndex < challenges.Count)
        {
            var c = challenges[challengeIndex++];
            QueueManager.RequeueTail(c.Challenger);
            QueueManager.RequeueTail(c.Target);
            var renewed = ChallengeService.Add(c.Challenger, c.Target);
            if (renewed is not null) renewed.Accepted = true;
        }
    }

    private void AssignArena(
        ArenaSlot arena, List<PlayerSlot>? team1, List<PlayerSlot>? team2, RoundType? roundType, int displayId,
        bool isChallenge = false)
    {
        arena.ArenaId          = displayId;
        arena.Team1             = team1;
        arena.Team2             = team2;
        arena.CurrentRoundType = roundType ?? (_registry.All.Count > 0 ? _registry.All[0] : null);
        arena.Result           = new ArenaResult(ArenaResultType.Empty, null, null);
        arena.IsChallenge      = isChallenge;

        if (team1 is null && team2 is null) return;

        var swapSides = Random.Shared.Next(2) == 1;
        var t1Spawns  = swapSides ? arena.CtSpawns : arena.TSpawns;
        var t2Spawns  = swapSides ? arena.TSpawns  : arena.CtSpawns;

        SeatTeam(arena, team1, t1Spawns, CStrikeTeam.TE, isChallenge);
        SeatTeam(arena, team2, t2Spawns, CStrikeTeam.CT, isChallenge);
    }

    private void SeatTeam(ArenaSlot arena, List<PlayerSlot>? team, List<Vector> spawns, CStrikeTeam switchTo, bool isChallenge = false)
    {
        if (team is null || spawns.Count == 0) return;

        var spawnPool = new List<Vector>(spawns);
        foreach (var slot in team)
        {
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null) continue;

            // NvN with more team members than spawn points in this cluster: refill the pool
            // (players may share a spawn) instead of Next(0) throwing ArgumentOutOfRange.
            if (spawnPool.Count == 0) spawnPool.AddRange(spawns);

            var idx = Random.Shared.Next(spawnPool.Count);
            arena.SpawnAssignment[slot] = spawnPool[idx];
            spawnPool.RemoveAt(idx);

            var state = QueueManager.GetOrCreateState(slot);
            state.ArenaTag = isChallenge
                ? Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Challenge")
                : Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Arena", arena.ArenaId);

            // Slay the LIVE pawn before re-seating, else SwitchTeam/ChangeTeam leaves a ghost body
            // at the old arena position (they never slay). [[feedback_slay_before_team_transfer]]
            if (controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } livePawn)
                livePawn.Slay();

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

    private bool IsVipSlot(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        return client is { IsInGame: true, IsFakeClient: false } && _vip.IsVip(client.SteamId);
    }

    private HashSet<int> GetEnabledTypes(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        if (client is null) return [.. _registry.All.Where(r => r.EnabledByDefault).Select(r => r.Id)];
        return _store.GetEnabledRoundTypeIds(client.SteamId, _registry.All);
    }

    /// <summary>K4's GetCommonRoundType: intersection of both players' enabled 1v1 round types,
    /// random pick; falls back to the configured default round, then to any 1v1 round type.</summary>
    // maxTeamSize caps the selectable round types (int.MaxValue = allow NvN). The caller downgrades
    // to maxTeamSize:1 when there aren't enough queued players to fill both sides of an NvN round.
    private RoundType? GetCommonRoundType(HashSet<int> prefs1, HashSet<int>? prefs2, int maxTeamSize = int.MaxValue)
    {
        var common = prefs2 is null ? prefs1 : new HashSet<int>(prefs1.Intersect(prefs2));
        var usable = _registry.All.Where(r => r.TeamSize <= maxTeamSize && common.Contains(r.Id)).ToList();

        if (usable.Count > 0) return usable[Random.Shared.Next(usable.Count)];

        var defaultName = _config.Config.DefaultWeaponSettings.DefaultRound;
        var defaultType = _registry.All.FirstOrDefault(r => r.Name == defaultName && r.TeamSize <= maxTeamSize);
        if (defaultType is not null) return defaultType;

        var any = _registry.All.Where(r => r.TeamSize <= maxTeamSize).ToList();
        return any.Count > 0 ? any[Random.Shared.Next(any.Count)] : null;
    }

    // ── round_end: compute per-arena results for the NEXT round_prestart ───

    private void OnRoundEnd()
    {
        _isBetweenRounds = true;

        foreach (var arena in _arenaManager.Arenas)
        {
            // Fire the special-round teardown once per arena BEFORE results/teams are recomputed.
            // This is the OnEnd half of the IArenasShared round-type contract — e.g. NoCrosshair
            // restores m_iHideHUD here. Without it every special-round cleanup silently never runs.
            if (arena.CurrentRoundType?.OnEnd is { } onEnd)
                onEnd(ToSlots(arena.Team1), ToSlots(arena.Team2));

            ComputeArenaResult(arena);
        }
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
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsInGame: true } client) return false;
        if (client.GetPlayerController() is not { } controller) return false;
        return controller.GetPlayerPawn() is { IsAlive: true };
    }

    // ── PlayerSpawnPost: teleport + loadout ──────────────────────────────────

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;
        if (client is not { IsInGame: true }) return; // both humans + bots must be in-game before touching the pawn

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
                roundType.OnStart(ToSlots(arena.Team1), ToSlots(arena.Team2));
            }
            else
            {
                _loadout.GiveLoadout(client, pawn, roundType);
            }
        }
    }

    /// <summary>Convert a slot list to a PlayerSlot array for ArenaRoundCallback. Bots are included
    /// by slot so special-round plugins can resolve them (SteamID=0 is ambiguous for bots).</summary>
    private static PlayerSlot[] ToSlots(List<PlayerSlot>? slots)
    {
        if (slots is null) return [];
        return [.. slots];
    }

    // ── PlayerKilledPost: deferred TerminateRoundIfPossible ─────────────────

    private void OnPlayerKilledPost(IPlayerKilledForwardParams @params)
    {
        // Skip scheduling during warmup — TerminateRoundIfPossible no-ops there anyway; avoids per-kill GC churn.
        if (_bridge.ModSharp.GetGameRules() is not { IsWarmupPeriod: false }) return;
        _bridge.ModSharp.PushTimer(TerminateRoundIfPossible, 1.0, GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
    }

    // ── termination ──────────────────────────────────────────────────────

    public void TerminateRoundIfPossible()
    {
        if (_isBetweenRounds) return;

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        var players = GetActiveRoundPlayers();
        if (players.All(c => c.IsFakeClient)) return;

        var allFinished = _arenaManager.Arenas.All(a => !HasRealPlayers(a) || HasFinished(a));
        if (!allFinished) return;

        DoTerminateRound(rules, players);
    }

    /// <summary>In-game, non-HLTV, non-spectator clients — the pool TerminateRoundIfPossible /
    /// ForceTerminateRoundOnTimeout decide the winner from.</summary>
    private List<IGameClient> GetActiveRoundPlayers()
        => _bridge.ClientManager.GetGameClients(inGame: true)
            .Where(c => !c.IsHltv && c.GetPlayerController() is { Team: > CStrikeTeam.Spectator })
            .ToList();

    /// <summary>Shared terminate-round decision (alive-count winner, draw coinflip) used by both the
    /// natural "all arenas finished" path and the forced round-timeout path.</summary>
    private void DoTerminateRound(IGameRules rules, List<IGameClient> players)
    {
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

    // A live 2-sided matchup: both teams have a connected, non-AFK player.
    private bool IsArenaActive(ArenaSlot a)
        => (a.Team1?.Any(s => IsSlotConnected(s) && !QueueManager.GetOrCreateState(s).Afk) ?? false)
        && (a.Team2?.Any(s => IsSlotConnected(s) && !QueueManager.GetOrCreateState(s).Afk) ?? false);

    private bool HasFinished(ArenaSlot a)
    {
        if (!IsArenaActive(a)) return true;

        return (a.Team1?.All(s => !IsSlotAlive(s)) ?? false) || (a.Team2?.All(s => !IsSlotAlive(s)) ?? false);
    }
}
