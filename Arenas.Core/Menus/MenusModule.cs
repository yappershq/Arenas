using System.Collections.Generic;
using System.Linq;
using Arenas.Database;
using Arenas.Loadout;
using Arenas.Plugins;
using Arenas.Rounds;
using Arenas.Shared;
using Arenas.Utils;
using Arenas.Weapons;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;

namespace Arenas.Menus;

/// <summary>
/// Builds and caches the two player-preference menus:
///   <see cref="GunsMenu"/>   — nested SubMenu tree (category → weapon list + Random)
///   <see cref="RoundsMenu"/> — flat toggle list (enabled/disabled per round type)
///
/// Both use factory lambdas so title/state are evaluated lazily per-client at display time.
/// Properties are null before OnAllSharpModulesLoaded. CommandsModule checks
/// <see cref="InterfaceBridge.MenuManager"/> before calling DisplayMenu.
/// Call <see cref="RebuildRoundsMenu"/> after external specials are added/removed.
/// </summary>
internal sealed class MenusModule : IModule
{
    private readonly ILogger<MenusModule> _logger;
    private readonly IArenasStore         _store;
    private readonly InterfaceBridge      _bridge;
    private readonly RoundTypeRegistry    _registry;
    private readonly ArenasApi            _arenasApi;

    /// <summary>Cached weapon-preference menu (root → 6 category sub-menus).</summary>
    public Menu? GunsMenu   { get; private set; }

    /// <summary>Cached round-preference toggle menu.</summary>
    public Menu? RoundsMenu { get; private set; }

    public MenusModule(
        InterfaceBridge        bridge,
        ILogger<MenusModule>   logger,
        IArenasStore           store,
        RoundTypeRegistry      registry,
        ArenasApi              arenasApi)
    {
        _bridge    = bridge;
        _logger    = logger;
        _store     = store;
        _registry  = registry;
        _arenasApi = arenasApi;
    }

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        if (_bridge.MenuManager is null)
            _logger.LogWarning("[Arenas] IMenuManager not installed — gun/rounds preference menus disabled.");

        BuildGunsMenu();
        BuildRoundsMenu();

        // Rebuild the rounds menu when an addon (un)registers a round type (order-independent — see
        // ArenasApi.OnRoundTypesChanged). RoundFlowModule subscribes first (DI order), so the registry
        // is already refreshed by the time this rebuild runs.
        _arenasApi.OnRoundTypesChanged += RebuildRoundsMenu;
    }

    public void Shutdown()
    {
        GunsMenu    = null;
        RoundsMenu  = null;
    }

    // ── GunsMenu ─────────────────────────────────────────────────────────────
    // Root: Weapon Preferences
    //   Rifle / Sniper / SMG / LMG / Shotgun / Pistol   (each is a SubMenu)
    //   Exit

    private void BuildGunsMenu()
    {
        var lm = _bridge.LocalizerManager;

        var rifleMenu   = BuildCategoryMenu(WeaponType.Rifle,   "Arenas_Round_Rifle",   lm);
        var sniperMenu  = BuildCategoryMenu(WeaponType.Sniper,  "Arenas_Round_Sniper",  lm);
        var smgMenu     = BuildCategoryMenu(WeaponType.Smg,     "Arenas_Round_Smg",     lm);
        var lmgMenu     = BuildCategoryMenu(WeaponType.Lmg,     "Arenas_Round_Lmg",     lm);
        var shotgunMenu = BuildCategoryMenu(WeaponType.Shotgun, "Arenas_Round_Shotgun", lm);
        var pistolMenu  = BuildCategoryMenu(WeaponType.Pistol,  "Arenas_Round_Pistol",  lm);

        GunsMenu = Menu.Create()
            .Title(client => Loc.Str(lm, client, "Arenas_Menu_WeaponPref_Title"))
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Rifle"),   rifleMenu)
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Sniper"),  sniperMenu)
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Smg"),     smgMenu)
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Lmg"),     lmgMenu)
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Shotgun"), shotgunMenu)
            .SubMenu(client => Loc.Str(lm, client, "Arenas_Round_Pistol"),  pistolMenu)
            .ExitItem(client => Loc.Str(lm, client, "Arenas_Menu_Exit"))
            .Build();
    }

    /// <summary>
    /// Category submenu: Random at top (clears pref), then all weapons in the category.
    /// Selecting a weapon stores the preference and navigates back.
    /// </summary>
    private Menu BuildCategoryMenu(WeaponType type, string titleKey, ILocalizerManager? lm)
    {
        var weapons      = WeaponCatalog.GetWeaponList(type);
        var capturedType = type;

        var builder = Menu.Create()
            .Title(client => Loc.Str(lm, client, titleKey));

        // "Random" — clear stored preference (LoadoutModule picks random when pref is null).
        builder.Item(
            client => Loc.Str(lm, client, "Arenas_General_Random"),
            ctrl =>
            {
                _store.SetWeaponPreference(ctrl.Client.SteamId, capturedType, null);
                Loc.Chat(lm, ctrl.Client, "Arenas_Chat_WeaponPreferencesRemoved",
                    Loc.Str(lm, ctrl.Client, titleKey));
                ctrl.GoBack();
            });

        foreach (var classname in weapons)
        {
            var cap = classname;
            builder.Item(
                CsItemNames.GetDisplayName(cap),
                ctrl =>
                {
                    _store.SetWeaponPreference(ctrl.Client.SteamId, capturedType, cap);
                    Loc.Chat(lm, ctrl.Client, "Arenas_Chat_WeaponPreferencesAdded",
                        CsItemNames.GetDisplayName(cap));
                    ctrl.GoBack();
                });
        }

        builder.BackItem(client => Loc.Str(lm, client, "Arenas_Menu_Back"));
        return builder.Build();
    }

    // ── RoundsMenu ────────────────────────────────────────────────────────────
    // Flat toggle list. At-least-one guard: the last enabled round type cannot be disabled.
    // Title factory reads pref state fresh each display — cookies are synchronous, no async needed.

    /// <summary>Rebuild the rounds menu after external specials are added/removed.</summary>
    public void RebuildRoundsMenu() => BuildRoundsMenu();

    private void BuildRoundsMenu()
    {
        var lm         = _bridge.LocalizerManager;
        var roundTypes = _registry.All.ToList(); // snapshot at build time

        var builder = Menu.Create()
            .Title(client => Loc.Str(lm, client, "Arenas_Menu_RoundPref_Title"));

        foreach (var rt in roundTypes)
        {
            var capturedRt = rt;
            builder.Item(
                // Title factory — re-reads enabled set per client at each display/refresh.
                client =>
                {
                    var enabledSet = _store.GetEnabledRoundTypeIds(client.SteamId, _registry.All);
                    var enabled    = enabledSet.Contains(capturedRt.Id);
                    var key        = enabled ? "Arenas_Menu_RoundPref_ItemEnabled" : "Arenas_Menu_RoundPref_ItemDisabled";
                    return Loc.Str(lm, client, key, Loc.Str(lm, client, capturedRt.Name));
                },
                ctrl =>
                {
                    var steamId    = ctrl.Client.SteamId;
                    var enabledSet = _store.GetEnabledRoundTypeIds(steamId, _registry.All);
                    var isEnabled  = enabledSet.Contains(capturedRt.Id);

                    if (isEnabled && enabledSet.Count <= 1)
                    {
                        Loc.Chat(lm, ctrl.Client, "Arenas_Chat_RoundPreferencesAtLeastOne");
                        ctrl.Refresh();
                        return;
                    }

                    _store.SetRoundTypeEnabled(steamId, capturedRt.Name, !isEnabled);
                    ctrl.Refresh();
                });
        }

        builder.ExitItem(client => Loc.Str(lm, client, "Arenas_Menu_Exit"));
        RoundsMenu = builder.Build();
    }
}
