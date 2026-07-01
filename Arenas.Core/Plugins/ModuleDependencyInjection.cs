using Microsoft.Extensions.DependencyInjection;
using Arenas.Modules;

namespace Arenas.Plugins;

internal static class ModuleDependencyInjection
{
    /// <summary>Register all Core services and modules into the DI container.</summary>
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // BootstrapModule kept as proof-of-life log until real modules land (Phase B).
        services.AddSingleton<BootstrapModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BootstrapModule>());

        return services;
    }
}
