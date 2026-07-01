using System.Collections.Generic;
using System.Linq;
using Arenas.Arena;
using Arenas.Config;
using Arenas.Database;
using Arenas.Plugins;
using Arenas.RoundFlow;
using Arenas.Shared;
using Arenas.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Objects;

namespace Arenas.Menus;

/// <summary>
/// Round-type and weapon preference menus. House <see cref="IMenuManager"/> nested SubMenu trees —
/// no CSS linear ChatMenu/ScrollableMenu paging. Menus are built per-open (round types + per-player
/// prefs are runtime state), using per-item title FACTORIES so each item's label reflects the
/// viewing player's current preference and localizes to their culture. Toggling an item flips the
/// stored preference (via <see cref="IArenasStore"/>) and calls <c>controller.Refresh()</c> to redraw.
///
/// Degrades to a no-op (with a chat hint) when IMenuManager isn't installed.
/// </summary>
internal sealed class MenusModule : IModule
{
    private readonly InterfaceBridge     _bridge;
    private readonly ILogger<MenusModule> _logger;
    private readonly ConfigModule        _config;
    private readonly RoundFlowModule     _roundFlow;
    private readonly IArenasStore        _store;

    public MenusModule(
        InterfaceBridge     bridge,
        ILogger<MenusModule> logger,
        ConfigModule        config,
        RoundFlowModule     roundFlow,
        IArenasStore        store)
    {
        _bridge    = bridge;
        _logger    = logger;
        _config    = config;
        _roundFlow = roundFlow;
        _store     = store;
    }

    public bool Init() => true;
    public void OnPostInit() { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown() { }

    // ── round preference menu ─────────────────────────────────────────────────

    public void OpenRoundPreferenceMenu(IGameClient client)
    {
        if (_bridge.MenuManager is not { } mm)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_MenusUnavailable");
            return;
        }

        var lm       = _bridge.LocalizerManager;
        var steamId  = client.SteamId;
        var roundTypes = _roundFlow.RoundTypes;

        var menu = new Menu();
        menu.SetTitle(c => Loc.Str(lm, c, "Arenas_Menu_RoundPref_Title"));

        foreach (var rt in roundTypes)
        {
            var roundType = rt; // capture
            menu.AddItem(
                c =>
                {
                    var enabled = _store.GetEnabledRoundTypeIds(steamId, roundTypes).Contains(roundType.Id);
                    var display = Loc.Str(lm, c, roundType.Name);
                    return Loc.Str(lm, c,
                        enabled ? "Arenas_Menu_RoundPref_ItemEnabled" : "Arenas_Menu_RoundPref_ItemDisabled",
                        display);
                },
                controller =>
                {
                    ToggleRoundPreference(controller.Client, roundTypes, roundType);
                    controller.Refresh();
                });
        }

        menu.AddExitItem(c => Loc.Str(lm, c, "Arenas_Menu_Exit"));
        mm.DisplayMenu(client, menu);
    }

    private void ToggleRoundPreference(IGameClient client, IReadOnlyList<RoundType> roundTypes, RoundType roundType)
    {
        var steamId = client.SteamId;
        var enabledIds = _store.GetEnabledRoundTypeIds(steamId, roundTypes);
        var currentlyEnabled = enabledIds.Contains(roundType.Id);

        if (currentlyEnabled)
        {
            // "At least one round must stay enabled" guard (K4 ToggleRoundPreference).
            if (enabledIds.Count <= 1)
            {
                Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_RoundPreferencesAtLeastOne");
                return;
            }
            _store.SetRoundTypeEnabled(steamId, roundType.Name, false);
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_RoundPreferencesRemoved",
                Loc.Str(_bridge.LocalizerManager, client, roundType.Name));
        }
        else
        {
            _store.SetRoundTypeEnabled(steamId, roundType.Name, true);
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_RoundPreferencesAdded",
                Loc.Str(_bridge.LocalizerManager, client, roundType.Name));
        }
    }

    // ── weapon preference menu (category → weapon submenu) ────────────────────

    public void OpenWeaponPreferenceMenu(IGameClient client)
    {
        if (_bridge.MenuManager is not { } mm)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Arenas_Chat_MenusUnavailable");
            return;
        }

        var lm = _bridge.LocalizerManager;

        var menu = new Menu();
        menu.SetTitle(c => Loc.Str(lm, c, "Arenas_Menu_WeaponPref_Title"));

        foreach (var type in AllowedWeaponTypes())
        {
            var weaponType = type;
            // Sub-menu built per-navigation so it reflects the current preference each time.
            menu.AddSubMenu(
                c => Loc.Str(lm, c, WeaponTypeKey(weaponType)),
                c => BuildWeaponSubMenu(c, weaponType));
        }

        menu.AddExitItem(c => Loc.Str(lm, c, "Arenas_Menu_Exit"));
        mm.DisplayMenu(client, menu);
    }

    private Menu BuildWeaponSubMenu(IGameClient client, WeaponType weaponType)
    {
        var lm      = _bridge.LocalizerManager;
        var steamId = client.SteamId;

        var sub = new Menu();
        sub.SetTitle(c => Loc.Str(lm, c, "Arenas_Menu_WeaponPref_Title"));

        // "Random" resets the preference (null == random).
        sub.AddItem(
            c =>
            {
                var isRandom = _store.GetWeaponPreference(steamId, weaponType) is null;
                var label    = Loc.Str(lm, c, "Arenas_General_Random");
                return Loc.Str(lm, c,
                    isRandom ? "Arenas_Menu_WeaponPref_ItemEnabled" : "Arenas_Menu_WeaponPref_ItemDisabled", label);
            },
            controller =>
            {
                _store.SetWeaponPreference(steamId, weaponType, null);
                Loc.Chat(lm, controller.Client, "Arenas_Chat_WeaponPreferencesAdded",
                    Loc.Str(lm, controller.Client, "Arenas_General_Random"));
                controller.Refresh();
            });

        foreach (var classname in Loadout.WeaponCatalog.GetWeaponList(weaponType))
        {
            var weapon = classname;
            sub.AddItem(
                c =>
                {
                    var selected = _store.GetWeaponPreference(steamId, weaponType) == weapon;
                    var label    = FriendlyWeaponName(weapon);
                    return Loc.Str(lm, c,
                        selected ? "Arenas_Menu_WeaponPref_ItemEnabled" : "Arenas_Menu_WeaponPref_ItemDisabled", label);
                },
                controller =>
                {
                    _store.SetWeaponPreference(steamId, weaponType, weapon);
                    Loc.Chat(lm, controller.Client, "Arenas_Chat_WeaponPreferencesAdded", FriendlyWeaponName(weapon));
                    controller.Refresh();
                });
        }

        sub.AddBackItem(c => Loc.Str(lm, c, "Arenas_Menu_Back"));
        return sub;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private IEnumerable<WeaponType> AllowedWeaponTypes()
    {
        var a = _config.Config.AllowedWeaponPreferences;
        if (a.Rifle)   yield return WeaponType.Rifle;
        if (a.Sniper)  yield return WeaponType.Sniper;
        if (a.Smg)     yield return WeaponType.Smg;
        if (a.Lmg)     yield return WeaponType.Lmg;
        if (a.Shotgun) yield return WeaponType.Shotgun;
        if (a.Pistol)  yield return WeaponType.Pistol;
    }

    private static string WeaponTypeKey(WeaponType type) => type switch
    {
        WeaponType.Rifle   => "Arenas_Round_Rifle",
        WeaponType.Sniper  => "Arenas_Round_Sniper",
        WeaponType.Smg     => "Arenas_Round_Smg",
        WeaponType.Lmg     => "Arenas_Round_Lmg",
        WeaponType.Shotgun => "Arenas_Round_Shotgun",
        WeaponType.Pistol  => "Arenas_Round_Pistol",
        _                  => "Arenas_General_Random",
    };

    /// <summary>"weapon_ak47" → "Ak47" for menu display (no locale key per weapon in K4 either).</summary>
    private static string FriendlyWeaponName(string classname)
    {
        var name = classname.StartsWith("weapon_") ? classname["weapon_".Length..] : classname;
        return name.Length == 0 ? classname : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
