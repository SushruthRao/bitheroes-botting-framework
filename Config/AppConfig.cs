using System.Text.Json.Serialization;

namespace BitHeroesClient.Config;

// ── Connection ─────────────────────────────────────────────────────────────────

public sealed class ConnectionConfig
{
    public string Host { get; set; } = "f123.bitheroesgame.com";
    public int    Port { get; set; } = 9933;
}

// ── Automation ─────────────────────────────────────────────────────────────────

/// <summary>
/// What the bot does when it detects the account is in the tutorial
/// (dungeon access flag not yet set by the server).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TutorialHandling
{
    /// <summary>Log a warning and idle; user must complete the tutorial manually.</summary>
    Warn,
    /// <summary>Skip tutorial-related steps silently and attempt dungeon entry anyway.</summary>
    Skip,
    /// <summary>Stop the bot entirely when tutorial is detected.</summary>
    Stop
}

public sealed class AutomationConfig
{
    /// <summary>Send CharacterDALC DAILY_REWARD at session start.</summary>
    public bool AutoClaimDailyReward { get; set; } = true;

    /// <summary>Check and loot all completed daily quests at session start.
    /// NOTE: Disabled by default — CharacterDALC action numbers for quest check/loot
    /// are unverified and the server will disconnect if the wrong action is sent.</summary>
    public bool AutoClaimDailyQuests { get; set; } = false;

    /// <summary>Auto-decline familiar capture prompts during battle.</summary>
    public bool AutoDeclineCaptures { get; set; } = true;

    /// <summary>
    /// Before each dungeon, automatically pick the strongest available teammates
    /// (online friends sorted by Power+Stamina+Agility, then owned familiars),
    /// matching the in-game Auto button logic. When false the saved PVE team is used.
    /// </summary>
    public bool AutoAssignTeammates { get; set; } = true;

    /// <summary>Cancel orphaned dungeon sessions on login rather than resuming them.</summary>
    public bool AbandonOrphanedDungeon { get; set; } = true;

    /// <summary>Behaviour when the account has not yet completed the tutorial.</summary>
    public TutorialHandling TutorialHandling { get; set; } = TutorialHandling.Warn;

    /// <summary>
    /// Minutes to wait before retrying dungeon entry after an out-of-energy error.
    /// Set to 0 to retry immediately (not recommended).
    /// </summary>
    public int EnergyWaitMinutes { get; set; } = 10;

    /// <summary>
    /// Maximum number of retries after a recoverable server error before the bot idles.
    /// Applies per-action (e.g. dungeon entry). Resets on a successful run.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay in ms between error retries (no exponential backoff).</summary>
    public int RetryDelayMs { get; set; } = 5000;
}

// ── Dungeon ────────────────────────────────────────────────────────────────────

/// <summary>One teammate slot serialisable from config.json.</summary>
public sealed class TeammateConfig
{
    /// <summary>Teammate entity ID (familiar ID, player char ID, etc.).</summary>
    public int  Id       { get; set; }

    /// <summary>
    /// Slot type: 1 = player/familiar, 2 = guild familiar.
    /// Matches tmts2 field in the SFS2X packet.
    /// </summary>
    public int  Type     { get; set; } = 1;

    /// <summary>
    /// Armory loadout override ID, or -1 for default.
    /// Sent as tmts3 (float) in the SFS2X packet.
    /// </summary>
    public long ArmoryId { get; set; } = -1;
}

public sealed class DungeonConfig
{
    /// <summary>Run the dungeon loop when in-game.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Zone identifier (zon0). Matches in-game zone list.</summary>
    public int ZoneId { get; set; } = 1;

    /// <summary>Node/room within the zone (zon1).</summary>
    public int NodeId { get; set; } = 1;

    /// <summary>Difficulty tier (zon2): 0=Normal, 1=Hard, 2=Heroic.</summary>
    public int DifficultyId { get; set; } = 0;

    /// <summary>Activate the damage-gain bonus in AUTO (bat57).</summary>
    public bool UseDamageGain { get; set; } = true;

    /// <summary>Milliseconds to wait between consecutive dungeon runs.</summary>
    public int RepeatDelayMs { get; set; } = 3000;

    /// <summary>Maximum runs before stopping. -1 = run indefinitely.</summary>
    public int MaxRuns { get; set; } = -1;

    /// <summary>Optional teammate list. Empty = server uses the character's saved team.</summary>
    public List<TeammateConfig> Teammates { get; set; } = new();
}

// ── GUI ────────────────────────────────────────────────────────────────────────

public sealed class GuiConfig
{
    /// <summary>Show the ANSI TUI dashboard instead of plain console output.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dashboard refresh rate in milliseconds.</summary>
    public int RefreshRateMs { get; set; } = 500;

    /// <summary>Number of recent log lines shown in the activity panel.</summary>
    public int MaxLogLines { get; set; } = 15;
}

// ── Logging ────────────────────────────────────────────────────────────────────

public sealed class LoggingConfig
{
    /// <summary>Write log output to a file in addition to the console/dashboard.</summary>
    public bool   LogToFile    { get; set; } = true;

    /// <summary>File path for the log file. Relative paths are resolved from the exe directory.</summary>
    public string LogFile      { get; set; } = "bot.log";

    /// <summary>Emit debug-level entries (packet dumps, raw responses). Noisy.</summary>
    public bool   VerboseMode  { get; set; } = false;
}

// ── Root ───────────────────────────────────────────────────────────────────────

/// <summary>Root config model — directly maps to config.json.</summary>
public sealed class AppConfig
{
    public ConnectionConfig  Connection  { get; set; } = new();
    public AutomationConfig  Automation  { get; set; } = new();
    public List<DungeonConfig> DungeonQueue { get; set; } = new() { new() };
    public GuiConfig         Gui         { get; set; } = new();
    public LoggingConfig     Logging     { get; set; } = new();
}
