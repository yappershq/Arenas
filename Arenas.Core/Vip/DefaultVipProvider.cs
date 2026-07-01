using Arenas.Plugins;
using Arenas.Shared;
using Sharp.Shared.Units;

namespace Arenas.Vip;

/// <summary>
/// Default IArenasVipProvider — nobody is VIP. Installed by Core; an external Arenas.Vip
/// plugin can shadow this by re-registering IArenasVipProvider in the DI container.
/// </summary>
internal sealed class DefaultVipProvider : IArenasVipProvider, IModule
{
    public bool Init() => true;
    public void OnPostInit() { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown() { }

    /// <inheritdoc/>
    public bool IsVip(SteamID steamId) => false;
}
