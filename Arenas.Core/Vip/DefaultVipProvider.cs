using Arenas.Plugins;
using Arenas.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;

namespace Arenas.Vip;

/// <summary>
/// Core's VIP provider. By default nobody is VIP (Core stays VIP-agnostic — never admin flags).
/// An external <c>Arenas.Vip</c> plugin publishes its own <see cref="IArenasVipProvider"/> under
/// <see cref="IArenasVipProvider.Identity"/> (bridging the house <c>IVipShared</c>); this default
/// resolves that override in OnAllModulesLoaded and delegates to it when present. Consumers inject
/// this via DI unchanged — the override is transparent to them. Load order is deploy-controlled
/// (both plugins finish PostInit before any OAM), so a single lookup with no retry is sufficient.
/// </summary>
internal sealed class DefaultVipProvider : IArenasVipProvider, IModule
{
    private readonly InterfaceBridge              _bridge;
    private readonly ILogger<DefaultVipProvider>  _logger;
    private IArenasVipProvider?                   _external;

    public DefaultVipProvider(InterfaceBridge bridge, ILogger<DefaultVipProvider> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        // Look up an externally-published provider (Arenas.Vip). Guard against resolving ourselves.
        var candidate = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IArenasVipProvider>(IArenasVipProvider.Identity)?.Instance;

        if (candidate is not null && !ReferenceEquals(candidate, this))
        {
            _external = candidate;
            _logger.LogInformation("[Arenas] VIP provider override adopted from an external plugin.");
        }
    }

    public void Shutdown() { }

    /// <inheritdoc/>
    public bool IsVip(SteamID steamId) => _external?.IsVip(steamId) ?? false;
}
