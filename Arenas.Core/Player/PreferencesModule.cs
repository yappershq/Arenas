using System.Collections.Generic;
using Arenas.Arena;
using Arenas.Plugins;
using Arenas.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Units;

namespace Arenas.Player;

/// <summary>
/// Weapon + round-type preferences via IClientPreference cookies (SteamID64-keyed — the ONLY
/// cross-map state, replacing K4's MySQL prefs table entirely). Degrades gracefully (no persistence,
/// in-memory defaults only) when ClientPreferences isn't installed.
///
/// Cookie keys:
///   Arenas_WeaponPref_&lt;WeaponType&gt;  -> weapon classname string ("" = random/no preference)
///   Arenas_RoundPref_&lt;RoundTypeName&gt; -> "1"/"0" (stable NAME key, not a process-lifetime int id)
/// </summary>
internal sealed class PreferencesModule : IModule
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<PreferencesModule> _logger;
    private readonly ArenasApi                _arenasApi;

    private IClientPreference? _prefs;

    public PreferencesModule(InterfaceBridge bridge, ILogger<PreferencesModule> logger, ArenasApi arenasApi)
    {
        _bridge    = bridge;
        _logger    = logger;
        _arenasApi = arenasApi;
    }

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        _prefs = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity)?.Instance;

        _logger.LogInformation(_prefs is null
            ? "[Arenas] ClientPreferences not available — weapon/round prefs will not persist."
            : "[Arenas] ClientPreferences resolved for weapon/round prefs.");

        _arenasApi.WeaponPrefResolver = GetWeaponPreference;
    }

    public void Shutdown() { }

    // ── weapon prefs ──────────────────────────────────────────────────────

    public string? GetWeaponPreference(SteamID steamId, WeaponType type)
    {
        if (_prefs is null || type == WeaponType.Unknown) return null;
        var cookie = _prefs.GetCookie(steamId, WeaponKey(type));
        var value  = cookie?.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SetWeaponPreference(SteamID steamId, WeaponType type, string? classname)
        => _prefs?.SetCookie(steamId, WeaponKey(type), classname ?? string.Empty);

    private static string WeaponKey(WeaponType type) => $"Arenas_WeaponPref_{type}";

    // ── round prefs ───────────────────────────────────────────────────────

    /// <summary>Enabled round-type ids for this player, sourced from cookies keyed by stable round name.
    /// Unset entries default to RoundType.EnabledByDefault.</summary>
    public HashSet<int> GetEnabledRoundTypeIds(SteamID steamId, IReadOnlyList<RoundType> allRoundTypes)
    {
        var result = new HashSet<int>();
        foreach (var rt in allRoundTypes)
        {
            var enabled = rt.EnabledByDefault;
            if (_prefs is not null)
            {
                var cookie = _prefs.GetCookie(steamId, RoundKey(rt.Name));
                if (cookie is not null)
                    enabled = cookie.GetNumber() != 0;
            }
            if (enabled) result.Add(rt.Id);
        }
        return result;
    }

    public void SetRoundTypeEnabled(SteamID steamId, string roundTypeName, bool enabled)
        => _prefs?.SetCookie(steamId, RoundKey(roundTypeName), enabled ? 1L : 0L);

    private static string RoundKey(string roundTypeName) => $"Arenas_RoundPref_{roundTypeName}";
}
