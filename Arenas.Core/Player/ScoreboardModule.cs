using System.Collections.Generic;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Units;

namespace Arenas.Player;

/// <summary>
/// Groups the scoreboard by arena: writes each seated player's <c>m_iScore</c>
/// (<see cref="Sharp.Shared.GameEntities.IPlayerController.Score"/>) as a function of their arena
/// RANK — <see cref="ArenaSlot.ArenaId"/>, the ladder display order assigned every round_prestart
/// (see RoundFlowModule.OnRoundPreStart/AssignArena) — so CS2's per-team score-descending sort
/// clusters arena #1's two players at the top of the scoreboard, arena #2 next, and so on.
///
/// The engine rewrites m_iScore during a live round (kills etc.), so a single write wouldn't stick —
/// same issue as the premier-scoreboard lesson. This re-applies on a throttled repeating PushTimer
/// (mirrors ClanTagModule's 3s sync) instead of a one-shot write.
///
/// The sync timer is (re)armed from <see cref="IGameListener.OnServerActivate"/> — NOT from
/// OnAllSharpModulesLoaded, which only runs once per plugin load. ModSharp strips StopOnMapEnd timers
/// at map end, so a timer pushed only at plugin load never survives the first map change. Mirrors
/// ServerConfigModule/ArenaManagerModule. OnServerActivate fires once per map (including the boot
/// map), and the previous map's StopOnMapEnd timer is already gone by the time it fires again, so
/// exactly one repeating timer is ever live per module.
/// </summary>
internal sealed class ScoreboardModule : IModule, IGameListener
{
    private const double SyncInterval = 3.0;

    // Arena #1 (best rank) gets this score; each subsequent arena subtracts ArenaSpacing. CS2 adds
    // +2/kill, +1/assist to m_iScore mid-round, so 1-point spacing would let adjacent arenas reorder
    // for up to 3s after every kill — space arenas ≥100 apart so a handful of kills can't collide them.
    // High enough that even a very long ladder never runs into negative territory, low enough to stay
    // inside normal UI range.
    private const int BaseScore    = 100_000;
    private const int ArenaSpacing = 100;

    private readonly InterfaceBridge           _bridge;
    private readonly ILogger<ScoreboardModule> _logger;
    private readonly ConfigModule              _config;
    private readonly ArenaManagerModule        _arenaManager;

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public ScoreboardModule(
        InterfaceBridge           bridge,
        ILogger<ScoreboardModule> logger,
        ConfigModule              config,
        ArenaManagerModule        arenaManager)
    {
        _bridge       = bridge;
        _logger       = logger;
        _config       = config;
        _arenaManager = arenaManager;
    }

    public bool Init() => true;

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    void IGameListener.OnServerActivate()
    {
        if (!_config.Config.CompatibilitySettings.ScoreboardArenaGrouping)
        {
            _logger.LogInformation("[Arenas] Scoreboard arena-grouping disabled by config.");
            return;
        }

        _bridge.ModSharp.PushTimer(SyncScores, SyncInterval,
            GameTimerFlags.StopOnMapEnd | GameTimerFlags.Repeatable);
    }

    private void SyncScores()
    {
        var seated = new HashSet<PlayerSlot>();

        foreach (var arena in _arenaManager.Arenas)
        {
            if (arena.ArenaId <= 0) continue; // unassigned / warmup arena — leave engine default alone

            var score = BaseScore - arena.ArenaId * ArenaSpacing;
            WriteScore(arena.Team1, score, seated);
            WriteScore(arena.Team2, score, seated);
        }

        // Clear stale scores for anyone rotated out of an arena (loser -> waiting, AFK, removed) —
        // otherwise they keep their last ~99xxx score and stay glued to the old arena group.
        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (seated.Contains(client.Slot)) continue;
            if (client.GetPlayerController() is not { Team: > CStrikeTeam.Spectator } controller) continue;

            if (controller.Score != 0)
                controller.Score = 0;
        }
    }

    private void WriteScore(List<PlayerSlot>? team, int score, HashSet<PlayerSlot> seated)
    {
        if (team is null) return;

        foreach (var slot in team)
        {
            seated.Add(slot);

            // Re-resolve every tick — never cache the client/controller across ticks.
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            if (client.GetPlayerController() is not { } controller) continue;

            if (controller.Score != score)
                controller.Score = score;
        }
    }
}
