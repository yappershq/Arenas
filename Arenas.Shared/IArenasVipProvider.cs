using Sharp.Shared.Units;

namespace Arenas.Shared;

/// <summary>
/// VIP check abstraction. Default implementation (DefaultVipProvider) always returns false —
/// nobody is VIP. An optional Arenas.Vip plugin can register a replacement that wires the
/// house IVipShared interface.
///
/// Registered as a DI singleton; consume via constructor injection. Never use admin flags
/// for VIP-gated arena behaviour — that goes here, not through IAdminManager.
/// </summary>
public interface IArenasVipProvider
{
    /// <summary>
    /// Registry identity. An external Arenas.Vip plugin publishes an implementation under this
    /// identity via <c>RegisterSharpModuleInterface</c>; Core's DefaultVipProvider looks it up in
    /// OnAllModulesLoaded and delegates to it when present (else nobody is VIP).
    /// </summary>
    const string Identity = nameof(IArenasVipProvider);

    /// <summary>Returns true if the player has Arenas VIP status (e.g. queue priority).</summary>
    bool IsVip(SteamID steamId);
}
