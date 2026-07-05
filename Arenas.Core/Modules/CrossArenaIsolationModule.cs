using System;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Arenas.Modules;

/// <summary>
/// Cross-arena isolation: block damage between players in different arenas
/// (PlayerDispatchTraceAttack pre-hook) and cancel flash blinds between them
/// (player_blind game event + pawn FlashDuration/FlashMaxAlpha reset).
///
/// Both behaviours are gated by config flags:
///   CompatibilitySettings.BlockDamageOfNotOpponent
///   CompatibilitySettings.BlockFlashOfNotOpponent
/// </summary>
internal sealed class CrossArenaIsolationModule : IModule, IEventListener
{
    private readonly InterfaceBridge             _bridge;
    private readonly ILogger<CrossArenaIsolationModule> _logger;
    private readonly ConfigModule                _config;
    private readonly ArenaManagerModule          _arenaManager;

    private Func<IPlayerDispatchTraceAttackHookParams, HookReturnValue<long>, HookReturnValue<long>>? _traceAttackHook;
    private bool _flashListenerInstalled;

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public CrossArenaIsolationModule(
        InterfaceBridge             bridge,
        ILogger<CrossArenaIsolationModule> logger,
        ConfigModule                config,
        ArenaManagerModule          arenaManager)
    {
        _bridge       = bridge;
        _logger       = logger;
        _config       = config;
        _arenaManager = arenaManager;
    }

    public bool Init() => true;

    public void OnPostInit()
    {
        if (_config.Config.CompatibilitySettings.BlockDamageOfNotOpponent)
        {
            _traceAttackHook = OnPlayerDispatchTraceAttackPre;
            _bridge.HookManager.PlayerDispatchTraceAttack.InstallHookPre(_traceAttackHook);
        }

        if (_config.Config.CompatibilitySettings.BlockFlashOfNotOpponent)
        {
            _bridge.EventManager.HookEvent("player_blind");
            _bridge.EventManager.InstallEventListener(this);
            _flashListenerInstalled = true;
        }
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        if (_traceAttackHook is not null)
        {
            _bridge.HookManager.PlayerDispatchTraceAttack.RemoveHookPre(_traceAttackHook);
            _traceAttackHook = null;
        }

        if (_flashListenerInstalled)
        {
            _bridge.EventManager.RemoveEventListener(this);
            _flashListenerInstalled = false;
        }
    }

    // ── Cross-arena damage block ─────────────────────────────────────────────

    private HookReturnValue<long> OnPlayerDispatchTraceAttackPre(
        IPlayerDispatchTraceAttackHookParams @params, HookReturnValue<long> prev)
    {
        // Victim slot from pawn params; attacker slot from TakeDamageInfo.
        var victimSlot   = (PlayerSlot)@params.Client.Slot.AsPrimitive();
        var attackerSlot = @params.AttackerPlayerSlot;

        // -1 means world/non-player attacker — don't block environmental damage.
        if (attackerSlot < 0) return prev;

        var attackerPlayerSlot = (PlayerSlot)attackerSlot;

        // Same player (self-damage)? Always allow.
        if (victimSlot.AsPrimitive() == attackerPlayerSlot.AsPrimitive()) return prev;

        // Both must be in arenas for isolation to apply.
        var victimArena   = _arenaManager.FindArenaForSlot(victimSlot);
        var attackerArena = _arenaManager.FindArenaForSlot(attackerPlayerSlot);

        if (victimArena is null || attackerArena is null) return prev;

        // Same arena — allow.
        if (victimArena.Index == attackerArena.Index) return prev;

        // Different arenas: block.
        return new HookReturnValue<long>(EHookAction.SkipCallReturnOverride, 0L);
    }

    // ── Cross-arena flash block ──────────────────────────────────────────────

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly) => true;

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (!@event.Name.Equals("player_blind", StringComparison.Ordinal)) return;

        var victimCtrl   = @event.GetPlayerController("userid");
        var attackerCtrl = @event.GetPlayerController("attacker");

        if (victimCtrl is null || attackerCtrl is null) return;

        var victimSlot   = victimCtrl.PlayerSlot;
        var attackerSlot = attackerCtrl.PlayerSlot;

        if (victimSlot.AsPrimitive() == attackerSlot.AsPrimitive()) return;

        var victimArena   = _arenaManager.FindArenaForSlot(victimSlot);
        var attackerArena = _arenaManager.FindArenaForSlot(attackerSlot);

        if (victimArena is null || attackerArena is null) return;
        if (victimArena.Index == attackerArena.Index) return;

        // Different arenas: zero the blind on the victim's pawn.
        var pawn = victimCtrl.GetPlayerPawn();
        if (pawn is not { IsAlive: true }) return;

        pawn.FlashDuration  = 0f;
        pawn.FlashMaxAlpha  = 0f;
    }
}
