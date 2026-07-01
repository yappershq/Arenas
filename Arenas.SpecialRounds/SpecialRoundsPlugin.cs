using System;
using System.Collections.Generic;
using System.Linq;
using Arenas.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Arenas.SpecialRounds;

/// <summary>
/// Arenas.SpecialRounds — external addon plugin (references Arenas.Shared only). Registers the special
/// round types from Letaryat/K4-Arenas-Special-Rounds through <see cref="IArenasShared"/>:
///   HeadshotOnly (AK47 / USP / Scout — one behaviour, three weapon variants),
///   NoCrosshair, Nades, OneTap.
///
/// House-convention divergences from the source (see docs/PORT_PLAN.md §6):
///   - HeadshotOnly zeroes non-head damage at a PlayerDispatchTraceAttack PRE-hook. It NEVER adds
///     Health/Armor back (the source's post-hoc anti-pattern).
///   - All per-player tracking is PlayerSlot-indexed and cleared on round end / map change; no stored
///     controllers/pawns — re-resolve by slot every callback.
///   - The broken OneTapTimer variant is intentionally NOT ported.
///   - Round-type display names are locale keys (arenas.specialrounds.json), rendered by Core's localizer.
///
/// Lifecycle: resolve IArenasShared + register in OnAllModulesLoaded (Core publishes it in PostInit);
/// unregister + remove hooks in Shutdown.
/// </summary>
public sealed class SpecialRoundsPlugin : IModSharpModule, IEventListener
{
    public string DisplayName   => "Arenas.SpecialRounds";
    public string DisplayAuthor => "yappershq";

    private const string HeInHand = "weapon_hegrenade";
    private const string OneTapWeapon = "weapon_ak47";

    private readonly ISharedSystem              _shared;
    private readonly ILogger<SpecialRoundsPlugin> _logger;
    private readonly IClientManager             _clientManager;
    private readonly IEventManager              _eventManager;
    private readonly IHookManager               _hookManager;
    private readonly IModSharp                  _modSharp;
    private readonly ISharpModuleManager        _moduleManager;

    private readonly SpecialRoundState _state = new();
    private readonly List<int>         _registeredIds = [];

    private IArenasShared? _arenas;

    private Func<IPlayerDispatchTraceAttackHookParams, HookReturnValue<long>, HookReturnValue<long>>? _traceAttackHook;

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public SpecialRoundsPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);

        _shared        = sharedSystem;
        _logger        = sharedSystem.GetLoggerFactory().CreateLogger<SpecialRoundsPlugin>();
        _clientManager = sharedSystem.GetClientManager();
        _eventManager  = sharedSystem.GetEventManager();
        _hookManager   = sharedSystem.GetHookManager();
        _modSharp      = sharedSystem.GetModSharp();
        _moduleManager = sharedSystem.GetSharpModuleManager();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        _arenas = _moduleManager.GetOptionalSharpModuleInterface<IArenasShared>(IArenasShared.Identity)?.Instance;
        if (_arenas is null)
        {
            _logger.LogWarning("[Arenas.SpecialRounds] IArenasShared not available — no special rounds registered. Is Arenas.Core installed?");
            return;
        }

        // Register the round types. HeadshotOnly: three weapon variants share one behaviour.
        RegisterHeadshotOnly("Arenas_Round_OnlyHS_Ak",    "weapon_ak47");
        RegisterHeadshotOnly("Arenas_Round_OnlyHS_Usp",   "weapon_usp_silencer");
        RegisterHeadshotOnly("Arenas_Round_OnlyHS_Scout", "weapon_ssg08");
        RegisterKind("Arenas_Round_NoCrosshair", SpecialRoundKind.NoCrosshair,
            onStart: (t1, t2) => NoCrosshairStart(t1, t2), onEnd: (t1, t2) => NoCrosshairEnd(t1, t2));
        RegisterKind("Arenas_Round_Nades", SpecialRoundKind.Nades,
            onStart: (t1, t2) => NadesStart(t1, t2), onEnd: (t1, t2) => ClearRound(t1, t2));
        RegisterKind("Arenas_Round_OneTap", SpecialRoundKind.OneTap,
            onStart: (t1, t2) => OneTapStart(t1, t2), onEnd: (t1, t2) => ClearRound(t1, t2));

        // Damage pre-hook (HeadshotOnly) + events (Nades re-give, OneTap top-up).
        _traceAttackHook = OnTraceAttackPre;
        _hookManager.PlayerDispatchTraceAttack.InstallHookPre(_traceAttackHook);

        _eventManager.HookEvent("grenade_thrown");
        _eventManager.HookEvent("weapon_fire");
        _eventManager.HookEvent("round_end");
        _eventManager.InstallEventListener(this);

        _logger.LogInformation("[Arenas.SpecialRounds] Registered {Count} special round types.", _registeredIds.Count);
    }

    public void Shutdown()
    {
        if (_arenas is { } arenas)
            foreach (var id in _registeredIds)
                arenas.UnregisterRoundType(id);
        _registeredIds.Clear();

        if (_traceAttackHook is not null)
        {
            _hookManager.PlayerDispatchTraceAttack.RemoveHookPre(_traceAttackHook);
            _traceAttackHook = null;
        }
        _eventManager.RemoveEventListener(this);
        _state.ClearAll();
    }

    // ── registration ──────────────────────────────────────────────────────────

    private void RegisterHeadshotOnly(string localeKey, string weapon)
        => RegisterKind(localeKey, SpecialRoundKind.HeadshotOnly,
            onStart: (t1, t2) => HeadshotOnlyStart(t1, t2, weapon),
            onEnd:   (t1, t2) => ClearRound(t1, t2));

    private void RegisterKind(string localeKey, SpecialRoundKind kind, ArenaRoundCallback onStart, ArenaRoundCallback onEnd)
    {
        var id = _arenas!.RegisterRoundType(localeKey, teamSize: 1, enabledByDefault: false, onStart, onEnd);
        _registeredIds.Add(id);
    }

    // ── HeadshotOnly ────────────────────────────────────────────────────────────

    private void HeadshotOnlyStart(PlayerSlot[] team1, PlayerSlot[] team2, string weapon)
    {
        // OnStart fires once per PLAYER spawn but receives the whole roster — dedupe so each player is
        // equipped exactly once per round (marking the slot; skip already-marked slots).
        foreach (var (pawn, slot) in FreshPawns(team1, team2, SpecialRoundKind.HeadshotOnly))
            EquipKnifeAnd(pawn, weapon);
    }

    /// <summary>Zero non-head damage between two same-arena HeadshotOnly participants. Never writes Health.</summary>
    private HookReturnValue<long> OnTraceAttackPre(IPlayerDispatchTraceAttackHookParams p, HookReturnValue<long> prev)
    {
        var victimSlot = p.Client.Slot;
        var attackerRaw = p.AttackerPlayerSlot;
        if (attackerRaw < 0) return prev; // world/self-inflicted

        var attackerSlot = (PlayerSlot)attackerRaw;
        if (victimSlot.AsPrimitive() == attackerSlot.AsPrimitive()) return prev;

        // Only intercept when BOTH sides are in an active HeadshotOnly round.
        if (!_state.IsActive(victimSlot, SpecialRoundKind.HeadshotOnly)
            || !_state.IsActive(attackerSlot, SpecialRoundKind.HeadshotOnly))
            return prev;

        if (p.HitGroup == HitGroupType.Head) return prev; // headshots pass through

        // Zero the damage entirely (block), rather than adding health back afterwards.
        return new HookReturnValue<long>(EHookAction.SkipCallReturnOverride, 0L);
    }

    // ── NoCrosshair ──────────────────────────────────────────────────────────────

    private void NoCrosshairStart(PlayerSlot[] team1, PlayerSlot[] team2)
    {
        foreach (var (pawn, _) in FreshPawns(team1, team2, SpecialRoundKind.NoCrosshair))
        {
            EquipKnifeAnd(pawn, "weapon_ak47");
            pawn.SetNetVar("m_iHideHUD", GetHideHud(pawn) | (1u << 8));
        }
    }

    private void NoCrosshairEnd(PlayerSlot[] team1, PlayerSlot[] team2)
    {
        foreach (var (pawn, slot) in ResolvePawns(team1).Concat(ResolvePawns(team2)))
        {
            pawn.SetNetVar("m_iHideHUD", GetHideHud(pawn) & ~(1u << 8));
            _state.Clear(slot);
        }
    }

    // ── Nades ────────────────────────────────────────────────────────────────────

    private void NadesStart(PlayerSlot[] team1, PlayerSlot[] team2)
    {
        foreach (var (pawn, _) in FreshPawns(team1, team2, SpecialRoundKind.Nades))
        {
            pawn.GetItemService()?.RemoveAllItems(true);
            pawn.GiveNamedItem("weapon_knife");
            pawn.GiveNamedItem(HeInHand);
        }
    }

    // ── OneTap ───────────────────────────────────────────────────────────────────

    private void OneTapStart(PlayerSlot[] team1, PlayerSlot[] team2)
    {
        foreach (var (pawn, slot) in FreshPawns(team1, team2, SpecialRoundKind.OneTap))
        {
            EquipKnifeAnd(pawn, OneTapWeapon);
            var capturedSlot = slot;
            _modSharp.InvokeFrameAction(() => SetOneClip(capturedSlot));
        }
    }

    // ── events ───────────────────────────────────────────────────────────────────

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly) => true;

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        switch (@event.Name)
        {
            case "grenade_thrown": OnGrenadeThrown(@event); break;
            case "weapon_fire":    OnWeaponFire(@event);    break;
            case "round_end":      _state.ClearAll();        break;
        }
    }

    private void OnGrenadeThrown(IGameEvent @event)
    {
        if (@event.GetPlayerController("userid") is not { } controller) return;
        var slot = controller.PlayerSlot;
        if (!_state.IsActive(slot, SpecialRoundKind.Nades)) return;

        // Re-give an HE so a Nades-round player is never left empty-handed (source parity: not arena-scoped).
        if (controller.GetPlayerPawn() is { IsAlive: true } pawn)
            pawn.GiveNamedItem(HeInHand);
    }

    private void OnWeaponFire(IGameEvent @event)
    {
        if (_arenas is null) return;
        if (@event.GetPlayerController("userid") is not { } shooter) return;
        var shooterSlot = shooter.PlayerSlot;
        if (!_state.IsActive(shooterSlot, SpecialRoundKind.OneTap)) return;

        // Top up the same-arena opponent's clip back to 1 (keep the duel one-tap). Re-resolve by SteamID.
        foreach (var opponentId in _arenas.FindOpponents(shooter.SteamId))
        {
            if (_clientManager.GetGameClient(opponentId) is not { IsInGame: true } opponent) continue;
            var opponentSlot = opponent.Slot;
            if (!_state.IsActive(opponentSlot, SpecialRoundKind.OneTap)) continue;
            _modSharp.InvokeFrameAction(() => SetOneClip(opponentSlot));
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private void ClearRound(PlayerSlot[] team1, PlayerSlot[] team2)
    {
        foreach (var (_, slot) in ResolvePawns(team1).Concat(ResolvePawns(team2)))
            _state.Clear(slot);
    }

    /// <summary>
    /// Live pawns that are NOT yet marked for this round kind — marks them as a side effect. Dedupes
    /// the once-per-player OnStart fan-out so each player is equipped exactly once per round.
    /// </summary>
    private IEnumerable<(IPlayerPawn Pawn, PlayerSlot Slot)> FreshPawns(PlayerSlot[] team1, PlayerSlot[] team2, SpecialRoundKind kind)
    {
        foreach (var (pawn, slot) in ResolvePawns(team1).Concat(ResolvePawns(team2)))
        {
            if (_state.Get(slot) == kind) continue; // already equipped this round
            _state.Set(slot, kind);
            yield return (pawn, slot);
        }
    }

    /// <summary>Re-resolve live pawns by PlayerSlot → (pawn, slot). Slot-keyed so bots (SteamID=0) are
    /// handled correctly — GetGameClient(PlayerSlot) works for both humans and bots.</summary>
    private IEnumerable<(IPlayerPawn Pawn, PlayerSlot Slot)> ResolvePawns(PlayerSlot[] slots)
    {
        foreach (var slot in slots)
        {
            if (_clientManager.GetGameClient(slot) is not { IsInGame: true } client) continue;
            if (client.GetPlayerController()?.GetPlayerPawn() is not { IsAlive: true } pawn) continue;
            yield return (pawn, slot);
        }
    }

    private static void EquipKnifeAnd(IPlayerPawn pawn, string weapon)
    {
        pawn.GetItemService()?.RemoveAllItems(true);
        pawn.GiveNamedItem("weapon_knife");
        pawn.GiveNamedItem(weapon);
    }

    private static uint GetHideHud(IPlayerPawn pawn)
        => pawn.GetNetVar<uint>("m_iHideHUD");

    /// <summary>Set the player's rifle clip to 1 (OneTap). Re-resolve by slot; weapon isn't stored.</summary>
    private void SetOneClip(PlayerSlot slot)
    {
        if (_clientManager.GetGameClient(slot) is not { IsInGame: true } client) return;
        if (client.GetPlayerController()?.GetPlayerPawn() is not { IsAlive: true } pawn) return;

        var weapon = pawn.GetWeaponBySlot(GearSlot.Rifle) ?? pawn.GetActiveWeapon();
        if (weapon is null || weapon.IsKnife) return;

        weapon.Clip        = 1;
        weapon.ReserveAmmo = 0;
    }
}
