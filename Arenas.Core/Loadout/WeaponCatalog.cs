using System;
using System.Collections.Generic;
using System.Linq;
using Arenas.Shared;

namespace Arenas.Loadout;

/// <summary>Weapon classname lists + WeaponType classification. Port of K4's WeaponModel.</summary>
internal static class WeaponCatalog
{
    public static readonly List<string> RifleItems =
    [
        "weapon_ak47", "weapon_m4a1_silencer", "weapon_m4a1", "weapon_galilar", "weapon_famas", "weapon_sg556", "weapon_aug",
    ];

    public static readonly List<string> SniperItems =
    [
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
    ];

    public static readonly List<string> ShotgunItems =
    [
        "weapon_xm1014", "weapon_nova", "weapon_mag7", "weapon_sawedoff",
    ];

    public static readonly List<string> SmgItems =
    [
        "weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_p90", "weapon_mp5sd", "weapon_bizon", "weapon_ump45",
    ];

    public static readonly List<string> LmgItems =
    [
        "weapon_m249", "weapon_negev",
    ];

    public static readonly List<string> PistolItems =
    [
        "weapon_deagle", "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_elite",
        "weapon_tec9", "weapon_p250", "weapon_cz75a", "weapon_fiveseven", "weapon_revolver",
    ];

    /// <summary>Free-buy / default pistols (Glock, USP-S, P2000). Used by the pistol-round armor
    /// balance (splewis-style): kevlar only if the player's secondary is one of these — upgraded
    /// pistols (Deagle, Five-SeveN, Tec9, CZ75, Dualies, R8, P250) get no armor.</summary>
    public static readonly List<string> CheapPistols =
    [
        "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000",
    ];

    public static bool IsCheapPistol(string? classname) => classname is not null && CheapPistols.Contains(classname);

    public static List<string> GetWeaponList(WeaponType type) => type switch
    {
        WeaponType.Rifle   => RifleItems,
        WeaponType.Sniper  => SniperItems,
        WeaponType.Shotgun => ShotgunItems,
        WeaponType.Smg     => SmgItems,
        WeaponType.Lmg     => LmgItems,
        WeaponType.Pistol  => PistolItems,
        _                  => GetAllPrimaryWeapons(),
    };

    public static List<string> GetAllPrimaryWeapons()
        => [.. RifleItems, .. SniperItems, .. ShotgunItems, .. SmgItems, .. LmgItems];

    public static string GetRandomWeapon(WeaponType type)
    {
        var items = GetWeaponList(type);
        return items[Random.Shared.Next(items.Count)];
    }

    public static WeaponType GetWeaponType(string? classname)
    {
        if (classname is null) return WeaponType.Unknown;
        if (RifleItems.Contains(classname))   return WeaponType.Rifle;
        if (SniperItems.Contains(classname))  return WeaponType.Sniper;
        if (ShotgunItems.Contains(classname)) return WeaponType.Shotgun;
        if (SmgItems.Contains(classname))     return WeaponType.Smg;
        if (LmgItems.Contains(classname))     return WeaponType.Lmg;
        if (PistolItems.Contains(classname))  return WeaponType.Pistol;
        return WeaponType.Unknown;
    }
}
