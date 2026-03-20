using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitHeroesClient.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string DefaultPath = "config.json";

    /// <summary>
    /// Load config from <paramref name="path"/>.
    /// If the file does not exist, write a default config and return it.
    /// Validation warnings are printed to stderr.
    /// </summary>
    public static AppConfig Load(string path = DefaultPath)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[config] '{path}' not found – writing defaults.");
            var defaults = new AppConfig();
            Save(defaults, path);
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? new AppConfig();
            Validate(cfg);
            return cfg;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[config] Parse error in '{path}': {ex.Message}");
            Console.Error.WriteLine("[config] Using defaults.");
            return new AppConfig();
        }
    }

    /// <summary>Serialise <paramref name="cfg"/> back to <paramref name="path"/>.</summary>
    public static void Save(AppConfig cfg, string path = DefaultPath)
    {
        try
        {
            string json = JsonSerializer.Serialize(cfg, _opts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] Could not save '{path}': {ex.Message}");
        }
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    private static void Validate(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Connection.Host))
            Warn("connection.host is empty; using default.");

        if (cfg.Connection.Port is < 1 or > 65535)
            Warn($"connection.port={cfg.Connection.Port} is invalid; using default 9933.");

        if (cfg.Automation.EnergyWaitMinutes < 0)
        {
            Warn("automation.energyWaitMinutes < 0; clamping to 1.");
            cfg.Automation.EnergyWaitMinutes = 1;
        }

        if (cfg.Automation.RetryDelayMs < 0)
        {
            Warn("automation.retryDelayMs < 0; clamping to 0.");
            cfg.Automation.RetryDelayMs = 0;
        }

        if (cfg.DungeonQueue.Count == 0)
            Warn("dungeonQueue is empty; bot will idle.");

        foreach (var d in cfg.DungeonQueue)
        {
            if (d.ZoneId < 1)
                Warn($"dungeonQueue entry: zoneId={d.ZoneId} looks invalid (should be ≥ 1).");
            if (d.NodeId < 1)
                Warn($"dungeonQueue entry: nodeId={d.NodeId} looks invalid (should be ≥ 1).");
            if (d.RepeatDelayMs < 0)
            {
                Warn("dungeonQueue entry: repeatDelayMs < 0; clamping to 0.");
                d.RepeatDelayMs = 0;
            }
        }

        if (cfg.Gui.RefreshRateMs < 100)
        {
            Warn("gui.refreshRateMs < 100; clamping to 100.");
            cfg.Gui.RefreshRateMs = 100;
        }
    }

    private static void Warn(string msg) =>
        Console.Error.WriteLine($"[config] WARNING: {msg}");
}
