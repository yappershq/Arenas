using Arenas.Arena;
using Arenas.Plugins;
using Arenas.Queue;
using Arenas.RoundFlow;
using Arenas.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Arenas.Player;

/// <summary>
/// Connect/disconnect + AFK gating. Port of K4's EventPlayerActivate/EventPlayerDisconnect/EventPlayerTeam.
///
/// AFK is gated via the HandleCommandJoinTeam pre-hook (house convention) instead of reacting to the
/// player_team event: an active player requesting Spectator gets flagged AFK + re-queued at the tail;
/// an AFK player requesting a live team gets un-flagged. player_team is still hooked (Silent = true)
/// only to suppress the join-team chat spam the pre-hook can't touch.
/// </summary>
internal sealed class PlayerLifecycleModule : IModule, IClientListener, IEventListener
{
    private readonly ILogger<PlayerLifecycleModule> _logger;
    private readonly InterfaceBridge                _bridge;
    private readonly QueueModule                    _queueModule;
    private readonly ArenaManagerModule             _arenaManager;
    private readonly PreferencesModule              _preferences;
    private readonly RoundFlowModule                _roundFlow;

    private QueueManager QueueManager => _queueModule.QueueManager;

    private System.Func<IHandleCommandJoinTeamHookParams, HookReturnValue<bool>, HookReturnValue<bool>>? _joinTeamHook;

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    public PlayerLifecycleModule(
        ILogger<PlayerLifecycleModule> logger,
        InterfaceBridge                bridge,
        QueueModule                    queueModule,
        ArenaManagerModule             arenaManager,
        PreferencesModule              preferences,
        RoundFlowModule                roundFlow)
    {
        _logger       = logger;
        _bridge       = bridge;
        _queueModule  = queueModule;
        _arenaManager = arenaManager;
        _preferences  = preferences;
        _roundFlow    = roundFlow;
    }

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.ClientManager.InstallClientListener(this);

        _joinTeamHook = OnHandleCommandJoinTeam;
        _bridge.HookManager.HandleCommandJoinTeam.InstallHookPre(_joinTeamHook);

        _bridge.EventManager.HookEvent("player_team");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        _bridge.EventManager.RemoveEventListener(this);
        if (_joinTeamHook is not null)
            _bridge.HookManager.HandleCommandJoinTeam.RemoveHookPre(_joinTeamHook);
        _bridge.ClientManager.RemoveClientListener(this);
    }

    // ── IClientListener ─────────────────────────────────────────────────────

    void IClientListener.OnClientPutInServer(IGameClient client)
    {
        if (client.IsHltv) return;
        SetupPlayer(client);
    }

    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        QueueManager.ClearSlot(client.Slot);
        _arenaManager.RemoveSlotFromAllArenas(client.Slot);
        _roundFlow.TerminateRoundIfPossible();
    }

    private void SetupPlayer(IGameClient client)
    {
        var slot = client.Slot;
        if (QueueManager.GetState(slot) is not null) return; // already set up

        var state = QueueManager.GetOrCreateState(slot);
        state.ArenaTag = Loc.Format(_bridge.LocalizerManager,
            _bridge.ModSharp.GetGameRules() is { IsWarmupPeriod: true } ? "Arenas_Tag_Warmup" : "Arenas_Tag_Waiting");

        QueueManager.Enqueue(slot);

        if (!client.IsFakeClient)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_QueueAdded", QueueManager.Waiting.Count);
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_ArenaAfkHint");
        }

        if (_bridge.ModSharp.GetGameRules() is { IsWarmupPeriod: false } && !client.IsFakeClient)
            _roundFlow.TerminateRoundIfPossible();
    }

    // ── HandleCommandJoinTeam pre-hook (AFK gating) ─────────────────────────

    private HookReturnValue<bool> OnHandleCommandJoinTeam(
        IHandleCommandJoinTeamHookParams p, HookReturnValue<bool> prev)
    {
        var client = p.Client;
        if (client is null || !client.IsInGame || client.IsFakeClient)
            return new HookReturnValue<bool>(EHookAction.Ignored);

        var slot          = client.Slot;
        var requestedTeam = (CStrikeTeam)p.Team;
        if (requestedTeam == CStrikeTeam.UnAssigned)
            return new HookReturnValue<bool>(EHookAction.Ignored);

        var currentTeam = client.GetPlayerController()?.Team ?? CStrikeTeam.Spectator;
        var state       = QueueManager.GetOrCreateState(slot);

        // active/live -> spectator: mark AFK, requeue at tail.
        if (currentTeam is not CStrikeTeam.Spectator and not CStrikeTeam.UnAssigned
            && requestedTeam == CStrikeTeam.Spectator && !state.Afk)
        {
            state.Afk       = true;
            state.ArenaTag  = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Afk");
            QueueManager.RequeueTail(slot);

            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_AfkEnabled");
            return new HookReturnValue<bool>(EHookAction.Ignored);
        }

        // spectator -> live: clear AFK.
        if (currentTeam == CStrikeTeam.Spectator && requestedTeam != CStrikeTeam.Spectator && state.Afk)
        {
            state.Afk      = false;
            state.ArenaTag = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Waiting");
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_AfkDisabled");
            return new HookReturnValue<bool>(EHookAction.Ignored);
        }

        if (!client.IsFakeClient)
            _roundFlow.TerminateRoundIfPossible();

        return new HookReturnValue<bool>(EHookAction.Ignored);
    }

    // ── IEventListener — suppress team-join chat spam only ──────────────────

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly)
    {
        if (@event.Name.Equals("player_team", System.StringComparison.Ordinal))
            serverOnly = true;
        return true;
    }

    void IEventListener.FireGameEvent(IGameEvent @event) { }
}
