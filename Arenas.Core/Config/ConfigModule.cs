using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arenas.Plugins;
using Microsoft.Extensions.Logging;

namespace Arenas.Config;

internal sealed class ConfigModule : IModule
{
    private readonly InterfaceBridge       _bridge;
    private readonly ILogger<ConfigModule> _logger;

    public ArenasConfig Config { get; private set; } = new();

    public ConfigModule(InterfaceBridge bridge, ILogger<ConfigModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        var path = Path.Combine(_bridge.ConfigPath, "arenas.json");
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters    = { new JsonStringEnumConverter() },
        };

        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Config, opts));
        }
        else
        {
            var text = File.ReadAllText(path);
            Config = JsonSerializer.Deserialize<ArenasConfig>(text, opts) ?? new ArenasConfig();
        }

        _logger.LogInformation("[Arenas] Config loaded from {Path}", path);
        return true;
    }

    public void OnPostInit()              { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown()                { }
}
