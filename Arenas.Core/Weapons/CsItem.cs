using System.Collections.Generic;
using System.Linq;

namespace Arenas.Weapons;

/// <summary>
/// CS2 weapon item identifiers for Arenas. Maps weapon_* classnames to typed enum values.
/// Replaces EnumMember reflection from the K4 source — membership is explicit via CsItemNames.
/// Used in menus (display names) and by special-round modules that reference weapons by name.
/// </summary>
public enum CsItem
{
    // ── Rifles ────────────────────────────────────────────────────────────────
    AK47, M4A1S, M4A4, Galil, Famas, SG556, AUG,

    // ── Snipers ───────────────────────────────────────────────────────────────
    AWP, Scout, AutoT, AutoCT,

    // ── SMGs ─────────────────────────────────────────────────────────────────
    Mac10, MP9, MP7, P90, MP5, Bizon, UMP45,

    // ── LMGs ─────────────────────────────────────────────────────────────────
    M249, Negev,

    // ── Shotguns ─────────────────────────────────────────────────────────────
    XM1014, Nova, MAG7, SawedOff,

    // ── Pistols ───────────────────────────────────────────────────────────────
    Deagle, Glock, USPS, HKP2000, Elite, Tec9, P250, CZ, FiveSeven, Revolver,

    // ── Knives ────────────────────────────────────────────────────────────────
    Knife,
}

/// <summary>Maps CsItem enum values to their CS2 "weapon_*" entity name strings, and back.</summary>
public static class CsItemNames
{
    private static readonly Dictionary<CsItem, string> Names = new()
    {
        // Rifles
        { CsItem.AK47,      "weapon_ak47"           },
        { CsItem.M4A1S,     "weapon_m4a1_silencer"  },
        { CsItem.M4A4,      "weapon_m4a1"           },
        { CsItem.Galil,     "weapon_galilar"        },
        { CsItem.Famas,     "weapon_famas"          },
        { CsItem.SG556,     "weapon_sg556"          },
        { CsItem.AUG,       "weapon_aug"            },
        // Snipers
        { CsItem.AWP,       "weapon_awp"            },
        { CsItem.Scout,     "weapon_ssg08"          },
        { CsItem.AutoT,     "weapon_g3sg1"          },
        { CsItem.AutoCT,    "weapon_scar20"         },
        // SMGs
        { CsItem.Mac10,     "weapon_mac10"          },
        { CsItem.MP9,       "weapon_mp9"            },
        { CsItem.MP7,       "weapon_mp7"            },
        { CsItem.P90,       "weapon_p90"            },
        { CsItem.MP5,       "weapon_mp5sd"          },
        { CsItem.Bizon,     "weapon_bizon"          },
        { CsItem.UMP45,     "weapon_ump45"          },
        // LMGs
        { CsItem.M249,      "weapon_m249"           },
        { CsItem.Negev,     "weapon_negev"          },
        // Shotguns
        { CsItem.XM1014,    "weapon_xm1014"         },
        { CsItem.Nova,      "weapon_nova"           },
        { CsItem.MAG7,      "weapon_mag7"           },
        { CsItem.SawedOff,  "weapon_sawedoff"       },
        // Pistols
        { CsItem.Deagle,    "weapon_deagle"         },
        { CsItem.Glock,     "weapon_glock"          },
        { CsItem.USPS,      "weapon_usp_silencer"   },
        { CsItem.HKP2000,   "weapon_hkp2000"        },
        { CsItem.Elite,     "weapon_elite"          },
        { CsItem.Tec9,      "weapon_tec9"           },
        { CsItem.P250,      "weapon_p250"           },
        { CsItem.CZ,        "weapon_cz75a"          },
        { CsItem.FiveSeven, "weapon_fiveseven"      },
        { CsItem.Revolver,  "weapon_revolver"       },
        // Knives
        { CsItem.Knife,     "weapon_knife"          },
    };

    private static readonly Dictionary<string, CsItem> ByName =
        Names.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Returns the CS2 entity classname for this item (e.g. "weapon_ak47").</summary>
    public static string GetName(this CsItem item)
        => Names.TryGetValue(item, out var name) ? name : item.ToString().ToLower();

    /// <summary>Reverse-lookup: entity classname → CsItem, or null if not mapped.</summary>
    public static CsItem? TryGetFromName(string classname)
        => ByName.TryGetValue(classname, out var item) ? item : null;

    /// <summary>Friendly display name from a classname (e.g. "weapon_ak47" → "AK47").</summary>
    public static string GetDisplayName(string classname)
        => classname.Replace("weapon_", string.Empty).Replace("_", " ").ToUpperInvariant();

    /// <summary>Friendly display name from an enum value (e.g. CsItem.AK47 → "AK47").</summary>
    public static string GetDisplayName(this CsItem item)
        => GetDisplayName(item.GetName());
}
