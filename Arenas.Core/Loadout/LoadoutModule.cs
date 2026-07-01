using Arenas.Arena;
using Arenas.Config;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace Arenas.Loadout;

/// <summary>
/// Gives a player their round-type loadout: strip → knife (config) → primary (fixed classname OR
/// preferred-by-WeaponType OR random) → secondary → armor/helmet. Port of K4's ArenaPlayer.SetupWeapons.
/// </summary>
internal sealed class LoadoutModule : IModule
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<LoadoutModule>   _logger;
    private readonly Config.ConfigModule      _config;
    private readonly Player.PreferencesModule _preferences;

    public LoadoutModule(InterfaceBridge bridge, ILogger<LoadoutModule> logger, Config.ConfigModule config, Player.PreferencesModule preferences)
    {
        _bridge      = bridge;
        _logger      = logger;
        _config      = config;
        _preferences = preferences;
    }

    public bool Init() => true;
    public void OnPostInit() { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown() { }

    public void GiveLoadout(IGameClient client, IPlayerPawn pawn, RoundType roundType)
    {
        if (!pawn.IsAlive) return;

        var itemService = pawn.GetItemService();
        itemService?.RemoveAllItems(true);

        if (_config.Config.CompatibilitySettings.GiveKnifeByDefault)
            pawn.GiveNamedItem("weapon_knife");

        if (roundType.PrimaryPreference == Shared.WeaponType.Unknown)
        {
            // Warmup / random round types: fully random primary + pistol.
            pawn.GiveNamedItem(WeaponCatalog.GetRandomWeapon(Shared.WeaponType.Unknown));
            pawn.GiveNamedItem(WeaponCatalog.GetRandomWeapon(Shared.WeaponType.Pistol));
        }
        else
        {
            if (roundType.PrimaryWeapon is { } fixedPrimary)
            {
                pawn.GiveNamedItem(fixedPrimary);
            }
            else if (roundType.UsePreferredPrimary && roundType.PrimaryPreference is { } primaryType)
            {
                var pref = _preferences.GetWeaponPreference(client.SteamId, primaryType)
                           ?? WeaponCatalog.GetRandomWeapon(primaryType);
                pawn.GiveNamedItem(pref);
            }

            if (roundType.SecondaryWeapon is { } fixedSecondary)
            {
                pawn.GiveNamedItem(fixedSecondary);
            }
            else if (roundType.UsePreferredSecondary)
            {
                var pref = _preferences.GetWeaponPreference(client.SteamId, Shared.WeaponType.Pistol)
                           ?? WeaponCatalog.GetRandomWeapon(Shared.WeaponType.Pistol);
                pawn.GiveNamedItem(pref);
            }
        }

        pawn.ArmorValue = roundType.Armor ? 100 : 0;
        if (itemService is not null)
            itemService.HasHelmet = roundType.Helmet;
    }
}
