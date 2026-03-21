namespace BitHeroesClient.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Thread-safe logger. Writes to a file and a circular in-memory buffer.
/// Fires <see cref="LineWritten"/> for every entry so the GUI can subscribe.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();

    private static bool         _verboseMode;
    private static bool         _logToFile;
    private static int          _bufferLimit = 500;
    private static StreamWriter? _writer;

    private static readonly LinkedList<string> _buffer = new();

    // ── Event ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired on every log entry (on whichever thread called Log/Info/Warn/Error).
    /// The GUI subscribes to this to display entries in the activity log.
    /// The uint is an ARGB color override (0 = use level-based default).
    /// </summary>
    public static event Action<LogLevel, string, uint>? LineWritten;

    // ── Initialisation ─────────────────────────────────────────────────────────

    public static void Configure(bool logToFile, string logFilePath,
                                  bool verboseMode, int bufferLines = 500)
    {
        lock (_lock)
        {
            _verboseMode = verboseMode;
            _logToFile   = logToFile;
            _bufferLimit = bufferLines;

            if (logToFile)
            {
                try
                {
                    _writer = new StreamWriter(logFilePath, append: true, System.Text.Encoding.UTF8)
                        { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[logger] Cannot open '{logFilePath}': {ex.Message}");
                    _logToFile = false;
                }
            }
        }
    }

    public static void Shutdown()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }

    // ── Logging ────────────────────────────────────────────────────────────────

    public static void Log(string msg)   => Write(LogLevel.Info,  msg);
    public static void Info(string msg)  => Write(LogLevel.Info,  msg);
    public static void Warn(string msg)  => Write(LogLevel.Warn,  msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void Debug(string msg) { if (_verboseMode) Write(LogLevel.Debug, msg); }

    /// <summary>Log an Info-level message with a specific ARGB color (0 = default).</summary>
    public static void Loot(string msg, uint argbColor) => Write(LogLevel.Info, msg, argbColor);

    // ── Buffer access ──────────────────────────────────────────────────────────

    public static string[] GetRecentLines(int count)
    {
        lock (_lock)
        {
            int take = Math.Min(count, _buffer.Count);
            var result = new string[take];
            var node = _buffer.Last;
            for (int i = take - 1; i >= 0 && node != null; i--, node = node.Previous)
                result[i] = node.Value;
            return result;
        }
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private static void Write(LogLevel level, string msg, uint argbColor = 0)
    {
        string prefix = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Warn  => "WRN",
            LogLevel.Error => "ERR",
            _              => "INF"
        };
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{prefix}] {msg}";

        lock (_lock)
        {
            _buffer.AddLast(line);
            while (_buffer.Count > _bufferLimit)
                _buffer.RemoveFirst();
            _writer?.WriteLine(line);
        }

        // Fire event outside lock
        LineWritten?.Invoke(level, line, argbColor);
    }
}
