using Microsoft.Extensions.DependencyInjection;
using Arenas.Api;
using Arenas.Arena;
using Arenas.Loadout;
using Arenas.Modules;
using Arenas.Player;
using Arenas.Queue;
using Arenas.RoundFlow;

namespace Arenas.Plugins;

internal static class ModuleDependencyInjection
{
    /// <summary>Register all Core services and modules into the DI container.</summary>
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // BootstrapModule kept as proof-of-life log.
        services.AddSingleton<BootstrapModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BootstrapModule>());

        // Phase B core spine — order matters only for readability; ModSharp lifecycle fan-out
        // (Init/PostInit/OnAllSharpModulesLoaded) is per-module, DI resolves the dependency graph.
        AddModule<Config.ConfigModule>(services);
        // ServerConfigModule neutralises standard CS economy/round-win scoring (arenas drive round ends
        // themselves + rank via the internal ladder). Registered early so convars apply before round flow.
        AddModule<Config.ServerConfigModule>(services);
        AddModule<ArenaManagerModule>(services);
        AddModule<QueueModule>(services);
        AddModule<PreferencesModule>(services);
        AddModule<LoadoutModule>(services);
        AddModule<ApiModule>(services);
        AddModule<RoundFlowModule>(services);
        AddModule<PlayerLifecycleModule>(services);

        return services;
    }

    private static void AddModule<T>(IServiceCollection services) where T : class, IModule
    {
        services.AddSingleton<T>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<T>());
    }
}
