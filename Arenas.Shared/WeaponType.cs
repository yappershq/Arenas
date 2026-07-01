namespace Arenas.Shared;

/// <summary>
/// Weapon category for per-player weapon preferences and round-type primary-weapon selection.
/// Mirrors K4-Arenas' <c>WeaponType</c> so preference semantics port 1:1.
/// </summary>
public enum WeaponType
{
    Rifle,
    Sniper,
    Smg,
    Lmg,
    Shotgun,
    Pistol,

    /// <summary>Sentinel for "random / any" round types (warmup, 2v2, 3v3).</summary>
    Unknown,
}
