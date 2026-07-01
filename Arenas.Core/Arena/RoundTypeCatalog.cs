using System.Collections.Generic;
using Arenas.Config;
using Arenas.Shared;

namespace Arenas.Arena;

/// <summary>Builds the 12 built-in round types (port of K4's RoundType static fields / PluginConfig defaults).</summary>
internal static class RoundTypeCatalog
{
    public const string Rifle   = "Arenas_Round_Rifle";
    public const string Sniper  = "Arenas_Round_Sniper";
    public const string Shotgun = "Arenas_Round_Shotgun";
    public const string Pistol  = "Arenas_Round_Pistol";
    public const string Scout   = "Arenas_Round_Scout";
    public const string Awp     = "Arenas_Round_Awp";
    public const string Deagle  = "Arenas_Round_Deagle";
    public const string Smg     = "Arenas_Round_Smg";
    public const string Lmg     = "Arenas_Round_Lmg";
    public const string Knife   = "Arenas_Round_Knife";
    public const string TwoVsTwo   = "Arenas_Round_2v2";
    public const string ThreeVsThree = "Arenas_Round_3v3";

    public static List<RoundType> Defaults()
    {
        var id = 1;
        return
        [
            new RoundType { Id = id++, Name = Rifle,   TeamSize = 1, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Rifle,   UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Sniper,  TeamSize = 1, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Sniper,  UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Shotgun, TeamSize = 1, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Shotgun, UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Pistol,  TeamSize = 1, UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Scout,   TeamSize = 1, PrimaryWeapon = "weapon_ssg08", UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Awp,     TeamSize = 1, PrimaryWeapon = "weapon_awp",   UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Deagle,  TeamSize = 1, SecondaryWeapon = "weapon_deagle", Armor = false, Helmet = false },
            new RoundType { Id = id++, Name = Smg,     TeamSize = 1, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Smg, UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Lmg,     TeamSize = 1, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Lmg, UsePreferredSecondary = true },
            new RoundType { Id = id++, Name = Knife,   TeamSize = 1, Armor = false, Helmet = false },
            new RoundType { Id = id++, Name = TwoVsTwo,   TeamSize = 2, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Unknown, UsePreferredSecondary = true, EnabledByDefault = false },
            new RoundType { Id = id++, Name = ThreeVsThree, TeamSize = 3, UsePreferredPrimary = true, PrimaryPreference = WeaponType.Unknown, UsePreferredSecondary = true, EnabledByDefault = false },
        ];
    }

    /// <summary>Build from config.RoundSettings (non-empty overrides Defaults() entirely, matching K4).</summary>
    public static List<RoundType> FromConfig(List<RoundTypeSettings> settings)
    {
        var list = new List<RoundType>();
        var id   = 1;
        foreach (var s in settings)
        {
            list.Add(new RoundType
            {
                Id                    = id++,
                Name                  = s.TranslationName,
                TeamSize              = s.TeamSize,
                PrimaryWeapon         = s.PrimaryWeapon,
                SecondaryWeapon       = s.SecondaryWeapon,
                UsePreferredPrimary   = s.UsePreferredPrimary,
                PrimaryPreference     = s.PrimaryPreference,
                UsePreferredSecondary = s.UsePreferredSecondary,
                Armor                 = s.Armor,
                Helmet                = s.Helmet,
                EnabledByDefault      = s.EnabledByDefault,
            });
        }
        return list;
    }
}
