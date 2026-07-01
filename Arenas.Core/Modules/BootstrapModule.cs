using Microsoft.Extensions.Logging;
using Arenas.Plugins;

namespace Arenas.Modules;

/// <summary>
/// Trivial placeholder module — proves the IModule wiring compiles and logs a proof-of-life message.
/// </summary>
internal sealed class BootstrapModule : IModule
{
    private readonly ILogger<BootstrapModule> _logger;

    public BootstrapModule(ILogger<BootstrapModule> logger) => _logger = logger;

    public bool Init()
    {
        _logger.LogInformation("[Arenas] Arenas loaded.");
        return true;
    }

    public void OnPostInit()              { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown()                { }
}
