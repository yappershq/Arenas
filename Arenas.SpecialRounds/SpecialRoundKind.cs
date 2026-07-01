namespace Arenas.SpecialRounds;

/// <summary>The distinct special-round behaviours this addon registers. HeadshotOnly is parameterized
/// by weapon (three registrations share one behaviour).</summary>
internal enum SpecialRoundKind
{
    HeadshotOnly,
    NoCrosshair,
    Nades,
    OneTap,
}
