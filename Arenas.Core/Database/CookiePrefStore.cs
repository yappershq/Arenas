using System.Collections.Generic;
using Arenas.Arena;
using Arenas.Plugins;
using Arenas.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Units;

namespace Arenas.Database;

/// <summary>
/// Default <see cref="IArenasStore"/>: IClientPreference cookie blob per SteamID64.
///
/// Degrades gracefully (log-once) when ClientPreferences isn't installed — prefs are in-memory
/// for the session only (still functional, just not cross-map persistent).
///
/// No async load / no Loaded-flag bug: cookies are synchronously available once the
/// client-preference system processes the connection. The K4 source bug (Loaded never set for
/// players with no saved round prefs → future saves silently dropped) does not apply here.
///
/// Cookie keys:
///   Arenas_WeaponPref_&lt;WeaponType&gt;  → weapon classname ("" = random / no preference)
///   Arenas_RoundPref_&lt;RoundTypeName&gt; → "1" / "0" (stable NAME key, not a process-lifetime int id)
/// </summary>
internal sealed class CookiePrefStore : IModule, IArenasStore
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<CookiePrefStore> _logger;
    private readonly ArenasApi                _arenasApi;

    private IClientPreference? _prefs;

    public CookiePrefStore(InterfaceBridge bridge, ILogger<CookiePrefStore> logger, ArenasApi arenasApi)
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
            ? "[Arenas] CookiePrefStore: IClientPreference not available — weapon/round prefs will not persist across maps."
            : "[Arenas] CookiePrefStore: IClientPreference resolved — weapon/round prefs are persistent.");

        // Wire the public WeaponPrefResolver so external addons can query via IArenasShared.
        // Must happen here (not PostInit) because IClientPreference itself resolves here.
        _arenasApi.WeaponPrefResolver = GetWeaponPreference;
    }

    public void Shutdown() { }

    // ── IArenasStore ──────────────────────────────────────────────────────────

    public string? GetWeaponPreference(SteamID steamId, WeaponType type)
    {
        if (_prefs is null || type == WeaponType.Unknown) return null;
        var value = _prefs.GetCookie(steamId, WeaponKey(type))?.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SetWeaponPreference(SteamID steamId, WeaponType type, string? classname)
        => _prefs?.SetCookie(steamId, WeaponKey(type), classname ?? string.Empty);

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

    // ── key helpers ───────────────────────────────────────────────────────────

    private static string WeaponKey(WeaponType type)     => $"Arenas_WeaponPref_{type}";
    private static string RoundKey(string roundTypeName)  => $"Arenas_RoundPref_{roundTypeName}";
}
