using Arenas.Arena;
using Arenas.Config;
using Arenas.Database;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace Arenas.Loadout;

/// <summary>
/// Gives a player their round-type loadout: strip → knife (config) → primary (fixed classname OR
/// preferred-by-WeaponType OR random) → secondary → armor/helmet + clip/ammo next frame via
/// InvokeFrameAction (GiveNamedItem doesn't ready the weapon in the same game frame).
/// Port of K4's ArenaPlayer.SetupWeapons.
/// </summary>
internal sealed class LoadoutModule : IModule
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<LoadoutModule>   _logger;
    private readonly Config.ConfigModule      _config;
    private readonly IArenasStore             _store;

    public LoadoutModule(InterfaceBridge bridge, ILogger<LoadoutModule> logger, Config.ConfigModule config, IArenasStore store)
    {
        _bridge = bridge;
        _logger = logger;
        _config = config;
        _store  = store;
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
                var pref = _store.GetWeaponPreference(client.SteamId, primaryType)
                           ?? WeaponCatalog.GetRandomWeapon(primaryType);
                pawn.GiveNamedItem(pref);
            }

            if (roundType.SecondaryWeapon is { } fixedSecondary)
            {
                pawn.GiveNamedItem(fixedSecondary);
            }
            else if (roundType.UsePreferredSecondary)
            {
                var pref = _store.GetWeaponPreference(client.SteamId, Shared.WeaponType.Pistol)
                           ?? WeaponCatalog.GetRandomWeapon(Shared.WeaponType.Pistol);
                pawn.GiveNamedItem(pref);
            }
        }

        // Armor/helmet and clip/ammo must be applied next frame:
        //  - Armor write can be overridden by the engine unless applied a tick after spawn.
        //  - GiveNamedItem doesn't ready the weapon same frame (clip not yet initialised).
        // Capture EntityIndex only — never hold IPlayerPawn across frames (dangling pointer risk).
        var pawnIndex  = pawn.Index;
        var wantArmor  = roundType.Armor;
        var wantHelmet = roundType.Helmet;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            // Re-resolve from index; pawn may have been freed if player disconnected mid-frame.
            var freshPawn = _bridge.EntityManager.FindEntityByIndex<IPlayerPawn>(pawnIndex);
            if (freshPawn is not { IsValidEntity: true, IsAlive: true }) return;

            freshPawn.ArmorValue = wantArmor ? 100 : 0;
            var svc = freshPawn.GetItemService();
            if (svc is not null) svc.HasHelmet = wantHelmet;

            // Fill clip + reserve ammo to max for every equipped weapon.
            var weaponService = freshPawn.GetWeaponService();
            if (weaponService is null) return;

            foreach (var handle in weaponService.GetMyWeapons())
            {
                var weapon = _bridge.EntityManager.FindEntityByHandle(handle);
                if (weapon is not { IsValidEntity: true }) continue;
                if (weapon.MaxClip > 0)                weapon.Clip        = weapon.MaxClip;
                if (weapon.PrimaryReserveAmmoMax > 0)  weapon.ReserveAmmo = weapon.PrimaryReserveAmmoMax;
            }
        });
    }
}
