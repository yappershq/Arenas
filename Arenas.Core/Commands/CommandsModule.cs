using System.Collections.Generic;
using System.Linq;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Menus;
using Arenas.Plugins;
using Arenas.Queue;
using Arenas.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Arenas.Commands;

/// <summary>
/// Registers all Arenas client commands via CommandCenter — BARE names (no css_/ms_ prefix; CommandCenter
/// adds ms_ for console and accepts . / ! / / chat triggers). Alias lists come from config. Most commands
/// are intentionally OPEN (no admin gate) — matches K4. Every handler re-resolves state by slot; nothing
/// stores a controller/pawn.
///
/// Commands: queue, guns, rounds, afk, challenge &lt;name&gt;, caccept, cdecline.
/// </summary>
internal sealed class CommandsModule : IModule
{
    private readonly InterfaceBridge        _bridge;
    private readonly ILogger<CommandsModule> _logger;
    private readonly ConfigModule           _config;
    private readonly QueueModule            _queueModule;
    private readonly ArenaManagerModule     _arenaManager;
    private readonly MenusModule            _menus;
    private readonly RoundFlow.RoundFlowModule _roundFlow;
    private readonly ArenasApi              _arenasApi;

    private QueueManager     QueueManager     => _queueModule.QueueManager;
    private ChallengeService ChallengeService => _queueModule.ChallengeService;

    public CommandsModule(
        InterfaceBridge        bridge,
        ILogger<CommandsModule> logger,
        ConfigModule           config,
        QueueModule            queueModule,
        ArenaManagerModule     arenaManager,
        MenusModule            menus,
        RoundFlow.RoundFlowModule roundFlow,
        ArenasApi              arenasApi)
    {
        _bridge       = bridge;
        _logger       = logger;
        _config       = config;
        _queueModule  = queueModule;
        _arenaManager = arenaManager;
        _menus        = menus;
        _roundFlow    = roundFlow;
        _arenasApi    = arenasApi;
    }

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        if (_bridge.CommandCenter is not { } cc)
        {
            _logger.LogWarning("[Arenas] ICommandCenter not available — chat commands will not be registered.");
            return;
        }

        var reg = cc.GetRegistry("arenas");
        var cmds = _config.Config.CommandSettings;

        Register(reg, cmds.QueueCommands,            OnQueue);
        Register(reg, cmds.GunsCommands,             OnGuns);
        Register(reg, cmds.RoundsCommands,           OnRounds);
        Register(reg, cmds.AfkCommands,              OnAfk);
        Register(reg, cmds.ChallengeCommands,        OnChallenge);
        Register(reg, cmds.ChallengeAcceptCommands,  OnChallengeAccept);
        Register(reg, cmds.ChallengeDeclineCommands, OnChallengeDecline);

        // Route IArenasShared.SetAfk through the full AFK action (spectate + retag + terminate),
        // overriding ApiModule's flag-only default (registered earlier → this wins in OAM order).
        _arenasApi.AfkSetter = (steamId, afk) =>
        {
            if (_bridge.ClientManager.GetGameClient(steamId) is { IsInGame: true } client)
                SetAfk(client, afk);
        };

        _logger.LogInformation("[Arenas] Registered client commands via CommandCenter.");
    }

    public void Shutdown() { } // CommandCenter registrations are scoped by module identity; no manual teardown.

    private static void Register(ICommandRegistry reg, List<string> names, System.Action<IGameClient, StringCommand> handler)
    {
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            reg.RegisterClientCommand(name, handler);
    }

    // ── queue ──────────────────────────────────────────────────────────────────

    private void OnQueue(IGameClient client, StringCommand _)
    {
        var slot = client.Slot;
        var waiting = QueueManager.Waiting.ToList();
        var idx = waiting.IndexOf(slot);
        if (idx < 0)
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_QueueNotInQueue");
        else
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_QueuePosition", idx + 1);
    }

    // ── preference menus ─────────────────────────────────────────────────────
    // MenusModule owns the cached GunsMenu/RoundsMenu (built with per-player title factories);
    // the command just displays the cached instance for the caller.

    private void OnGuns(IGameClient client, StringCommand _)  => ShowMenu(client, _menus.GunsMenu);
    private void OnRounds(IGameClient client, StringCommand _) => ShowMenu(client, _menus.RoundsMenu);

    private void ShowMenu(IGameClient client, Menu? menu)
    {
        if (_bridge.MenuManager is { } mm && menu is not null)
            mm.DisplayMenu(client, menu);
        else
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_MenusUnavailable");
    }

    // ── afk toggle ─────────────────────────────────────────────────────────────

    private void OnAfk(IGameClient client, StringCommand _)
    {
        var slot  = client.Slot;
        var state = QueueManager.GetOrCreateState(slot);
        SetAfk(client, !state.Afk);
    }

    /// <summary>Shared AFK action (also the IArenasShared.SetAfk implementation target). Slot-based.</summary>
    public void SetAfk(IGameClient client, bool afk)
    {
        var slot  = client.Slot;
        var state = QueueManager.GetOrCreateState(slot);
        state.Afk = afk;

        if (afk)
        {
            state.ArenaTag = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Afk");
            // Move to spectator + slay so they don't linger in an arena.
            if (client.GetPlayerController() is { } controller)
            {
                if (controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn)
                    pawn.Slay();
                if (controller.Team > CStrikeTeam.Spectator)
                    controller.SwitchTeam(CStrikeTeam.Spectator);
            }
            _arenaManager.RemoveSlotFromAllArenas(slot);
            ChallengeService.RemoveForSlot(slot);
            QueueManager.RequeueTail(slot);
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_AfkEnabled");
            _roundFlow.TerminateRoundIfPossible();
        }
        else
        {
            state.ArenaTag = Loc.Format(_bridge.LocalizerManager, "Arenas_Tag_Waiting");
            QueueManager.RequeueTail(slot);
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_AfkDisabled");
        }
    }

    // ── challenge / accept / decline ──────────────────────────────────────────

    private void OnChallenge(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;

        if (command.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Command_Help",
                _config.Config.CommandSettings.ChallengeCommands.FirstOrDefault() ?? "challenge", "[name]");
            return;
        }

        if (ChallengeService.IsInChallenge(slot))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_InChallenge");
            return;
        }

        // Both must be in an active arena for a duel to make sense (K4 requires arena placement != -1).
        if (_arenaManager.FindArenaForSlot(slot) is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_NotInArena");
            return;
        }

        var target = ResolveTarget(client, command.GetArg(1));
        if (target is null || target.Slot == slot)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_InvalidTarget");
            return;
        }

        var targetSlot = target.Slot;
        if (ChallengeService.IsInChallenge(targetSlot))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_InChallenge");
            return;
        }
        if (_arenaManager.FindArenaForSlot(targetSlot) is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_NotInArena");
            return;
        }

        var challenge = ChallengeService.Add(slot, targetSlot);
        if (challenge is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_InChallenge");
            return;
        }

        Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_Waiting", target.Name);

        // Bots auto-accept after a beat (K4 behaviour). Re-resolve BOTH sides by slot in the timer —
        // never capture the IGameClient across the frame boundary (it may dangle on disconnect).
        if (target.IsFakeClient)
        {
            _bridge.ModSharp.PushTimer(() =>
            {
                if (!ChallengeService.Challenges.Contains(challenge)) return;
                if (_bridge.ClientManager.GetGameClient(challenge.Challenger) is not { IsInGame: true } challenger) return;
                if (_bridge.ClientManager.GetGameClient(challenge.Target) is not { IsInGame: true } bot) return;

                challenge.Accepted = true;
                Loc.Chat(_bridge.LocalizerManager, challenger, "Arenas_Challenge_AcceptedBy", bot.Name);
            }, 1.0, GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
            return;
        }

        var accept  = _config.Config.CommandSettings.ChallengeAcceptCommands.FirstOrDefault() ?? "caccept";
        var decline = _config.Config.CommandSettings.ChallengeDeclineCommands.FirstOrDefault() ?? "cdecline";
        Loc.Chat(_bridge.LocalizerManager, target, "Arenas_Challenge_Request", client.Name, accept, decline);
    }

    private void OnChallengeAccept(IGameClient client, StringCommand _)
    {
        var challenge = ChallengeService.FindForSlot(client.Slot);
        if (challenge is null || challenge.Target != client.Slot)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_NotChallenged");
            return;
        }

        var challenger = _bridge.ClientManager.GetGameClient(challenge.Challenger);
        if (challenger is not { IsInGame: true })
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_NotAvailable");
            ChallengeService.Remove(challenge);
            return;
        }

        challenge.Accepted = true;
        Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_Accepted", challenger.Name);
        Loc.Chat(_bridge.LocalizerManager, challenger, "Arenas_Challenge_AcceptedBy", client.Name);
    }

    private void OnChallengeDecline(IGameClient client, StringCommand _)
    {
        var challenge = ChallengeService.FindForSlot(client.Slot);
        if (challenge is null || challenge.Target != client.Slot)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_NotChallenged");
            return;
        }

        var challenger = _bridge.ClientManager.GetGameClient(challenge.Challenger);
        ChallengeService.Remove(challenge);

        var challengerName = challenger?.Name ?? Loc.Str(_bridge.LocalizerManager, client, "Arenas_General_Bot");
        Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Challenge_Declined", challengerName);
        if (challenger is { IsInGame: true })
            Loc.Chat(_bridge.LocalizerManager, challenger, "Arenas_Challenge_DeclinedBy", client.Name);
    }

    // (target resolution below)

    /// <summary>Resolve a challenge target by name via TargetingManager (partial-name, unique). Null if none/ambiguous.</summary>
    private IGameClient? ResolveTarget(IGameClient activator, string targetString)
    {
        if (_bridge.TargetingManager is { } tm)
        {
            var matches = tm.GetByTarget(activator, targetString).Where(c => c is { IsInGame: true }).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        // Fallback: manual unique substring match over in-game humans + bots.
        var candidates = _bridge.ClientManager.GetGameClients(inGame: true)
            .Where(c => c.Name.Contains(targetString, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
