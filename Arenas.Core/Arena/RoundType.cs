using Arenas.Shared;

namespace Arenas.Arena;

/// <summary>
/// A weapon-set / team-size configuration a round can use. Port of K4's RoundType struct.
/// Built-ins come from RoundTypeCatalog; special rounds (Phase D, Arenas.SpecialRounds) register
/// via ArenasApi.RegisterRoundType and are folded into RoundTypeCatalog.All at runtime by RoundFlowModule.
/// </summary>
public sealed class RoundType
{
    public required int    Id               { get; init; }
    public required string Name             { get; init; } // locale key
    public          int    TeamSize         { get; init; } = 1;
    public          string? PrimaryWeapon   { get; init; }
    public          string? SecondaryWeapon { get; init; }
    public          bool   UsePreferredPrimary   { get; init; }
    public          WeaponType? PrimaryPreference { get; init; }
    public          bool   UsePreferredSecondary { get; init; }
    public          bool   Armor            { get; init; } = true;
    public          bool   Helmet           { get; init; } = true;
    public          bool   EnabledByDefault { get; init; } = true;

    /// <summary>Non-null for special rounds registered via IArenasShared — Core gives no weapons itself.</summary>
    public ArenaRoundCallback? OnStart { get; init; }
    public ArenaRoundCallback? OnEnd   { get; init; }
}
