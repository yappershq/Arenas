using Arenas.Config;
using Arenas.Plugins;
using Arenas.Queue;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;

namespace Arenas.Player;

/// <summary>
/// Force-arena-clantags: reflect each player's arena tag (ARENA n / WAITING / AFK / CHALLENGE) into
/// their scoreboard clan tag via <c>IPlayerController.SetClanTag</c>. K4 wrote m_szClan on every tag
/// change; the house idiom is a single throttled repeating PushTimer that re-syncs connected humans,
/// avoiding per-event writes. Gated by two config flags (opt-in force, opt-out disable) — when both
/// off the module installs no timer at all.
///
/// SetClanTag does nothing on fake clients, so bots are skipped.
///
/// The sync timer is (re)armed from <see cref="IGameListener.OnServerActivate"/> — NOT from
/// OnAllSharpModulesLoaded, which only runs once per plugin load. ModSharp strips StopOnMapEnd timers
/// at map end, so a timer pushed only at plugin load never survives the first map change. Mirrors
/// ServerConfigModule/ArenaManagerModule. OnServerActivate fires once per map (including the boot
/// map), and the previous map's StopOnMapEnd timer is already gone by the time it fires again, so
/// exactly one repeating timer is ever live per module.
/// </summary>
internal sealed class ClanTagModule : IModule, IGameListener
{
    private const double SyncInterval = 3.0;

    private readonly InterfaceBridge      _bridge;
    private readonly ILogger<ClanTagModule> _logger;
    private readonly ConfigModule         _config;
    private readonly QueueModule          _queueModule;

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public ClanTagModule(InterfaceBridge bridge, ILogger<ClanTagModule> logger, ConfigModule config, QueueModule queueModule)
    {
        _bridge      = bridge;
        _logger      = logger;
        _config      = config;
        _queueModule = queueModule;
    }

    public bool Init() => true;

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    void IGameListener.OnServerActivate()
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
