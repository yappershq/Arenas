using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Arenas.Plugins;
using Arenas.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;

namespace Arenas;

/// <summary>
/// Arenas — ModSharp port of KitsuneLab-Development/K4-Arenas + Letaryat/K4-Arenas-Special-Rounds.
///
/// Lifecycle (honours ModSharp's "all PostInits finish before any OAM" guarantee):
///   PostInit           — publish IArenasShared so external plugins (special rounds) can subscribe in their OAM.
///   OnAllModulesLoaded — resolve optional external interfaces (LocalizerManager, CommandCenter, …).
/// </summary>
public sealed class ArenasPlugin : IModSharpModule
{
    public string DisplayName   => "Arenas";
    public string DisplayAuthor => "yappershq";

    private readonly IServiceProvider       _provider;
    private readonly ILogger<ArenasPlugin>  _logger;
    private readonly InterfaceBridge        _bridge;
    private readonly ArenasApi              _api;

    public ArenasPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        ArgumentNullException.ThrowIfNull(sharpPath);

        var loggerFactory = sharedSystem.GetLoggerFactory();
        _bridge = new InterfaceBridge(this, sharedSystem, sharpPath, loggerFactory);
        _api    = new ArenasApi();

        var services = new ServiceCollection();
        services.AddSingleton(sharedSystem);
        services.AddSingleton(_bridge);
        services.AddSingleton(_api);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(LoggerFactoryLogger<>));
        services.AddModules();

        _provider = services.BuildServiceProvider();
        _logger   = _provider.GetRequiredService<ILogger<ArenasPlugin>>();
    }

    public bool Init()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => { m.Init(); }, "Init");
        return true;
    }

    /// <summary>Publish IArenasShared so external plugins can subscribe in their OAM.</summary>
    public void PostInit()
    {
        _bridge.SharpModuleManager
            .RegisterSharpModuleInterface<IArenasShared>(this, IArenasShared.Identity, _api);

        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnPostInit(), "PostInit");

        _logger.LogInformation("[Arenas] Published IArenasShared.");
    }

    public void OnAllModulesLoaded()
    {
        _bridge.LocalizerManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;
        LoadLocaleFiles();

        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnAllSharpModulesLoaded(), "OnAllModulesLoaded");

        _logger.LogInformation("[Arenas] All modules loaded.");
    }

    /// <summary>Load every <c>arenas*.json</c> under <c>{sharp}/locales/</c> (shipped via .assets/locales).</summary>
    private void LoadLocaleFiles()
    {
        if (_bridge.LocalizerManager is not { } lm)
        {
            _logger.LogInformation("[Arenas] ILocalizerManager not available — user-facing text will be silent.");
            return;
        }

        var localesPath = Path.Combine(_bridge.SharpPath, "locales");
        if (!Directory.Exists(localesPath)) return;

        foreach (var file in Directory.GetFiles(localesPath, "arenas*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            lm.LoadLocaleFile(fileName);
            _logger.LogInformation("[Arenas] Loaded locale file: {FileName}", fileName);
        }
    }

    public void Shutdown()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.Shutdown(), "Shutdown");

        if (_provider is IDisposable disposable)
            disposable.Dispose();
    }

    private void CallSafe(IModule module, Action<IModule> action, string phase)
    {
        try   { action(module); }
        catch (Exception ex) { _logger.LogError(ex, "[Arenas] Error in {Phase} for {Module}", phase, module.GetType().Name); }
    }
}

/// <summary>Generic logger adapter bridging ILogger&lt;T&gt; onto ModSharp's factory.</summary>
internal sealed class LoggerFactoryLogger<T>(ILoggerFactory factory) : ILogger<T>
{
    private readonly ILogger _inner = factory.CreateLogger(typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
