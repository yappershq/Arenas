using System;
using System.IO;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;

namespace Arenas.Config;

/// <summary>
/// Disables the standard CS round-win economy/scoring so Arenas can drive round ends itself
/// (RoundFlow.TerminateRoundIfPossible picks the winner — team score/money must not matter).
/// Port of the K4 gameconfig convar block.
///
/// Ships arenas.cfg (from .assets/configs/) into csgo/cfg/arenas/ on first run and execs it on every
/// map load (mirrors Retakes' ServerConfigModule). We ALSO set the load-bearing convars directly via
/// IConVarManager as a belt-and-braces fallback, so the economy is neutralised even if the .cfg exec
/// is delayed or the file is missing.
/// </summary>
internal sealed class ServerConfigModule : IModule, IGameListener
{
    private const string CfgExecPath = "arenas/arenas.cfg";

    private readonly InterfaceBridge             _bridge;
    private readonly ILogger<ServerConfigModule> _logger;

    // Load-bearing convars applied directly (name -> value). Mirror of arenas.cfg's core block.
    private static readonly (string Name, string Value)[] CoreConVars =
    [
        ("mp_maxmoney", "0"),
        ("mp_teamcashawards", "0"),
        ("mp_playercashawards", "0"),
        ("mp_maxrounds", "0"),
        ("mp_autoteambalance", "0"),
        ("mp_halftime", "0"),
        ("mp_join_grace_time", "0"),
        ("mp_respawn_immunitytime", "0"),
        ("mp_warmuptime", "0"),
        ("mp_ct_default_primary", ""),
        ("mp_ct_default_secondary", ""),
        ("mp_t_default_primary", ""),
        ("mp_t_default_secondary", ""),
        ("mp_free_armor", "0"),
        ("mp_max_armor", "0"),
        ("mp_equipment_reset_rounds", "0"),
        ("mp_round_restart_delay", "2"),
        ("mp_freezetime", "3"),
    ];

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public ServerConfigModule(InterfaceBridge bridge, ILogger<ServerConfigModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        EnsureCfgExists();
        ApplyCoreConVars();
        return true;
    }

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    void IGameListener.OnServerActivate()
    {
        EnsureCfgExists();
        ApplyCoreConVars();

        _bridge.ModSharp.PushTimer(
            () =>
            {
                _bridge.ModSharp.ServerCommand($"exec {CfgExecPath}");
                _logger.LogInformation("[Arenas] Executed server config: {Cfg}", CfgExecPath);
            },
            1.0,
            GameTimerFlags.StopOnMapEnd);
    }

    private void ApplyCoreConVars()
    {
        foreach (var (name, value) in CoreConVars)
        {
            var cvar = _bridge.ConVarManager.FindConVar(name);
            if (cvar is null)
            {
                _logger.LogDebug("[Arenas] ConVar not found (skipped): {Name}", name);
                continue;
            }
            cvar.SetString(value);
        }
    }

    private void EnsureCfgExists()
    {
        try
        {
            var cfgDir  = Path.Combine(_bridge.ModSharp.GetGamePath(), "csgo", "cfg", "arenas");
            var cfgFile = Path.Combine(cfgDir, "arenas.cfg");

            if (File.Exists(cfgFile)) return;

            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(cfgFile, ArenasCfgContents);
            _logger.LogInformation("[Arenas] Created server config: {File}", cfgFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Arenas] Failed to create arenas.cfg — falling back to direct ConVar application only.");
        }
    }

    // K4 gameconfig convar block. Arenas drive round ends themselves, so team score/money/rounds are neutralised.
    private const string ArenasCfgContents =
        """
        // Arenas server config — disables standard CS round-win economy/scoring.
        // Arenas drive round ends themselves (TerminateRoundIfPossible), so team money/score must not matter.
        mp_maxmoney 0
        mp_teamcashawards 0
        mp_playercashawards 0
        mp_maxrounds 0
        mp_autoteambalance 0
        mp_halftime 0
        mp_join_grace_time 0
        mp_respawn_immunitytime 0
        mp_warmuptime 0
        mp_ct_default_primary ""
        mp_ct_default_secondary ""
        mp_t_default_primary ""
        mp_t_default_secondary ""
        mp_free_armor 0
        mp_max_armor 0
        mp_equipment_reset_rounds 0
        mp_round_restart_delay 2
        mp_freezetime 3

        echo [Arenas] Config loaded!

        """;
}
