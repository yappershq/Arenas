using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Arenas.Shared;

/// <summary>
/// The rule a special round type enforces. Special-round modules pick one of these when
/// registering a round type; Core applies the base weapon loadout, the module applies the rule.
/// </summary>
public enum SpecialRoundRule
{
    /// <summary>No special rule — plain weapon loadout (normal round type).</summary>
    None = 0,

    /// <summary>Only headshots deal damage; body/limb damage is cancelled pre-application.</summary>
    HeadshotOnly,

    /// <summary>HE-grenade-only duel; thrown grenades are replenished.</summary>
    NadesOnly,

    /// <summary>Crosshair HUD element is hidden for the round.</summary>
    NoCrosshair,

    /// <summary>Single-bullet ammo economy; opponent's clip is topped up when the shooter fires.</summary>
    OneTap,
}

/// <summary>
/// Callback signature for per-arena round start / end. Fired once per concurrent arena instance
/// when a round of the registered type begins/ends in that arena, scoped to that arena's two teams.
/// SteamID lists are the human/bot occupants of each side — re-resolve <c>IGameClient</c> by id in
/// the callback; never store clients/pawns.
/// </summary>
public delegate void ArenaRoundCallback(SteamID[] team1, SteamID[] team2);

/// <summary>
/// Public contract published by Arenas.Core in <c>PostInit</c> via
/// <c>RegisterSharpModuleInterface&lt;IArenasShared&gt;(this, Identity, impl)</c>.
///
/// External plugins (e.g. Arenas.SpecialRounds) resolve it in their own <c>OnAllModulesLoaded</c>
/// (ModSharp finishes all PostInits before any OAM) and register custom round types.
/// NO ORM / 3rd-party types here — this assembly crosses the ALC boundary.
/// </summary>
public interface IArenasShared
{
    static string Identity => typeof(IArenasShared).FullName!;

    /// <summary>
    /// Register a special round type. Returns a handle used only for
    /// <see cref="UnregisterRoundType"/> on module teardown.
    /// </summary>
    /// <param name="name">Round-type display/locale key (e.g. "OnlyHS-AK47").</param>
    /// <param name="weight">Random-selection weight (1 = default even weight).</param>
    /// <param name="enabled">Whether the round type participates in selection.</param>
    /// <param name="onStart">Fired per-arena when a round of this type starts.</param>
    /// <param name="onEnd">Fired per-arena when a round of this type ends.</param>
    int RegisterRoundType(string name, int weight, bool enabled, ArenaRoundCallback onStart, ArenaRoundCallback onEnd);

    /// <summary>Remove a previously registered round type. Call in module teardown.</summary>
    void UnregisterRoundType(int id);

    /// <summary>
    /// Resolve which physical arena instance a player currently occupies, so cross-team
    /// round logic can pair opponents within the same arena. Null if the player is not in an arena.
    /// </summary>
    int? GetArenaPlacement(SteamID steamId);
}
