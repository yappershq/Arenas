using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arenas.Shared;
using Sharp.Shared;
using Sharp.Shared.Units;
using Vip.Shared;

namespace Arenas.Vip;

/// <summary>
/// Optional third-party module: bridges the house VIP plugin (<see cref="IVipShared"/>) into
/// Arenas' VIP-agnostic <see cref="IArenasVipProvider"/> contract.
///
/// Arenas.Core ships a no-op default (nobody VIP) and, in its OnAllModulesLoaded, looks up an
/// externally-published <see cref="IArenasVipProvider"/> via <see cref="IArenasVipProvider.Identity"/>,
/// adopting it when present. This plugin is that publisher: it resolves <see cref="IVipShared"/>
/// (optional — absent on a VIP-less server) and publishes a bridge. Deploy only alongside the Vip
/// plugin; on a VIP-less server this module still loads and Arenas.Core keeps its default. Both
/// plugins finish PostInit before any OnAllModulesLoaded runs, and load order is deploy-controlled,
/// so publishing eagerly here (no retry) is sufficient.
/// </summary>
public sealed class ArenasVipPlugin : IModSharpModule
{
    public string DisplayName   => "Arenas.Vip";
    public string DisplayAuthor => "yappershq";

    private readonly ISharedSystem             _sharedSystem;
    private readonly ILogger<ArenasVipPlugin>  _logger;

    private IVipShared? _vip;

    public ArenasVipPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        _sharedSystem = sharedSystem;
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<ArenasVipPlugin>();
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        var manager = _sharedSystem.GetSharpModuleManager();

        _vip = manager.GetOptionalSharpModuleInterface<IVipShared>(IVipShared.Identity)?.Instance;

        if (_vip is null)
        {
            _logger.LogWarning(
                "[Arenas.Vip] IVipShared not found — Vip plugin not installed/loaded. " +
                "Arenas.Core keeps its no-op default (nobody VIP).");
            return;
        }

        manager.RegisterSharpModuleInterface<IArenasVipProvider>(
            this, IArenasVipProvider.Identity, new VipBridgeProvider(_vip));

        _logger.LogInformation("[Arenas.Vip] Published IArenasVipProvider bridging IVipShared.");
    }

    public void Shutdown() { }

    /// <summary>Delegates IsVip(SteamID) straight to IVipShared.IsVip(ulong).</summary>
    private sealed class VipBridgeProvider(IVipShared vip) : IArenasVipProvider
    {
        public bool IsVip(SteamID steamId) => vip.IsVip((ulong)steamId);
    }
}
