using Sharp.Shared.Units;

namespace Arenas.Shared;

/// <summary>
/// Callback signature for per-arena special-round start / end. Fired once per concurrent arena
/// instance when a round of the registered type begins/ends in that arena, scoped to that arena's
/// two teams. Slots (not SteamIDs) are passed so bots (SteamID = 0) are resolvable — re-resolve
/// <c>IGameClient</c> by slot in the callback; never store clients/pawns across the callback.
/// </summary>
public delegate void ArenaRoundCallback(PlayerSlot[] team1, PlayerSlot[] team2);

/// <summary>
/// Public contract published by Arenas.Core in <c>PostInit</c> via
/// <c>RegisterSharpModuleInterface&lt;IArenasShared&gt;(this, Identity, impl)</c>.
///
/// External plugins (e.g. Arenas.SpecialRounds) resolve it in their own <c>OnAllModulesLoaded</c>
/// (ModSharp finishes all PostInits before any OAM) and register custom round types / query arena
/// state. This is the ModSharp equivalent of K4-Arenas' <c>IK4ArenaSharedApi</c>.
/// NO ORM / 3rd-party types here — this assembly crosses the ALC boundary. Players are addressed by
/// <see cref="SteamID"/> (never a stored controller/pawn).
/// </summary>
public interface IArenasShared
{
    static string Identity => typeof(IArenasShared).FullName!;

    // ── Special-round registration (== K4 AddSpecialRound / RemoveSpecialRound) ──

    /// <summary>
    /// Register a special round type. Returns a handle used only for
    /// <see cref="UnregisterRoundType"/> on module teardown.
    /// </summary>
    /// <param name="name">Round-type locale key / display name (e.g. "arenas.rounds.onlyhs_ak").</param>
    /// <param name="teamSize">Players per side for this round type (1 = 1v1).</param>
    /// <param name="enabledByDefault">Whether the round type is on in fresh player preferences.</param>
    /// <param name="onStart">Fired per-arena when a round of this type starts.</param>
    /// <param name="onEnd">Fired per-arena when a round of this type ends.</param>
    int RegisterRoundType(string name, int teamSize, bool enabledByDefault, ArenaRoundCallback onStart, ArenaRoundCallback onEnd);

    /// <summary>Remove a previously registered round type. Call in module teardown.</summary>
    void UnregisterRoundType(int id);

    // ── Arena / player state queries (== K4 arena-placement / AFK / opponents) ──

    /// <summary>
    /// Which physical arena instance the player currently occupies, so cross-team round logic can
    /// pair opponents within the same arena. Null if the player is not in an active arena.
    /// </summary>
    int? GetArenaPlacement(SteamID steamId);

    /// <summary>Display name / number of the arena the player is in (empty if none).</summary>
    string GetArenaName(SteamID steamId);

    /// <summary>Whether the player is currently flagged AFK (sitting in spectator queue).</summary>
    bool IsAfk(SteamID steamId);

    /// <summary>The SteamIDs of the player's opponents in their current arena (empty if none).</summary>
    SteamID[] FindOpponents(SteamID steamId);

    /// <summary>Set a player's AFK state (spectate / rejoin queue).</summary>
    void SetAfk(SteamID steamId, bool afk);

    /// <summary>End the current round early if the win condition is already met.</summary>
    void TerminateRoundIfPossible();

    /// <summary>
    /// The player's saved preferred weapon for a category, or null if "random".
    /// Returned as the CS item classname (e.g. "weapon_ak47") to keep engine enums out of .Shared.
    /// </summary>
    string? GetPlayerWeaponPreference(SteamID steamId, WeaponType weaponType);
}
