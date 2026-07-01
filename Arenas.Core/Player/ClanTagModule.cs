using Arenas.Config;
using Arenas.Plugins;
using Arenas.Queue;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;

namespace Arenas.Player;

/// <summary>
/// Force-arena-clantags: reflect each player's arena tag (ARENA n / WAITING / AFK / CHALLENGE) into
/// their scoreboard clan tag via <c>IPlayerController.SetClanTag</c>. K4 wrote m_szClan on every tag
/// change; the house idiom is a single throttled repeating PushTimer that re-syncs connected humans,
/// avoiding per-event writes. Gated by two config flags (opt-in force, opt-out disable) — when both
/// off the module installs no timer at all.
///
/// SetClanTag does nothing on fake clients, so bots are skipped.
/// </summary>
internal sealed class ClanTagModule : IModule
{
    private const double SyncInterval = 3.0;

    private readonly InterfaceBridge      _bridge;
    private readonly ILogger<ClanTagModule> _logger;
    private readonly ConfigModule         _config;
    private readonly QueueModule          _queueModule;

    public ClanTagModule(InterfaceBridge bridge, ILogger<ClanTagModule> logger, ConfigModule config, QueueModule queueModule)
    {
        _bridge      = bridge;
        _logger      = logger;
        _config      = config;
        _queueModule = queueModule;
    }

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        var compat = _config.Config.CompatibilitySettings;
        if (!compat.ForceArenaClantags || compat.DisableClantags)
        {
            _logger.LogInformation("[Arenas] Arena clantags disabled by config — clantag sync not installed.");
            return;
        }

        _bridge.ModSharp.PushTimer(SyncClanTags, SyncInterval,
            GameTimerFlags.StopOnMapEnd | GameTimerFlags.Repeatable);
    }

    public void Shutdown() { }

    private void SyncClanTags()
    {
        var queue = _queueModule.QueueManager;
        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue; // SetClanTag no-ops on bots
            if (client.GetPlayerController() is not { } controller) continue;

            var tag = queue.GetState(client.Slot)?.ArenaTag;
            if (!string.IsNullOrEmpty(tag))
                controller.SetClanTag(tag);
        }
    }
}
