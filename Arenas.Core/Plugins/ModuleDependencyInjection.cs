using Microsoft.Extensions.DependencyInjection;
using Arenas.Api;
using Arenas.Arena;
using Arenas.Database;
using Arenas.Loadout;
using Arenas.Modules;
using Arenas.Queue;
using Arenas.RoundFlow;
using Arenas.Shared;
using Arenas.Vip;

namespace Arenas.Plugins;

internal static class ModuleDependencyInjection
{
    /// <summary>Register all Core services and modules into the DI container.</summary>
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // Shared round-type registry (built-ins + config + addon-registered specials). Owned by
        // RoundFlowModule, read by MenusModule — a plain singleton (not an IModule).
        services.AddSingleton<Rounds.RoundTypeRegistry>();

        // DefaultVipProvider — nobody is VIP; an external Arenas.Vip plugin can replace IArenasVipProvider
        // by re-registering the interface before the host is built.
        services.AddSingleton<DefaultVipProvider>();
        services.AddSingleton<IArenasVipProvider>(sp => sp.GetRequiredService<DefaultVipProvider>());
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<DefaultVipProvider>());

        // Phase C — persistence seam: CookiePrefStore is the default IArenasStore (IClientPreference
        // cookies, no DB needed). An optional Arenas.Database project with SqlPrefStore can override
        // IArenasStore by registering before the host is built.
        services.AddSingleton<CookiePrefStore>();
        services.AddSingleton<IArenasStore>(sp => sp.GetRequiredService<CookiePrefStore>());
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<CookiePrefStore>());

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
        AddModule<LoadoutModule>(services);
        AddModule<ApiModule>(services);
        AddModule<RoundFlowModule>(services);
        AddModule<Player.PlayerLifecycleModule>(services);
        AddModule<CrossArenaIsolationModule>(services);

        // Phase C — commands, menus, challenge duels, force-clantags.
        AddModule<Menus.MenusModule>(services);
        AddModule<Commands.CommandsModule>(services);
        AddModule<Player.ClanTagModule>(services);
        AddModule<Player.ScoreboardModule>(services);

        return services;
    }

    private static void AddModule<T>(IServiceCollection services) where T : class, IModule
    {
        services.AddSingleton<T>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<T>());
    }
}
