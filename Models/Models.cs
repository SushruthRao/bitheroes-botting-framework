namespace BitHeroesClient.Models;

// ── Dungeon objects ─────────────────────────────────────────────────────────────

public sealed record DungeonObjectInfo(int Row, int Col, int Type, bool Used, bool Empty)
{
    public string TypeName => Type switch
    {
        0 => "ENEMY", 1 => "TREASURE", 2 => "BOSS",
        3 => "SHRINE", 4 => "LOOTABLE", 5 => "MERCHANT",
        6 => "AD",   _ => $"T{Type}"
    };
}

// ── Loot / dungeon ─────────────────────────────────────────────────────────────

public sealed class LootItem
{
    public int ItemId   { get; init; }
    public int ItemType { get; init; }
    public int Qty      { get; init; }
    public override string ToString() => $"id={ItemId} type={ItemType} qty={Qty}";
}

public sealed class DungeonResult
{
    public bool           Victory      { get; init; }
    public List<LootItem> Loot         { get; init; } = new();
    public int            RunNumber    { get; init; }
    public int            ZoneId       { get; init; }
    public int            NodeId       { get; init; }
    public int            DifficultyId { get; init; }
    public int            Waves        { get; init; }
}

// ── Daily quests ───────────────────────────────────────────────────────────────

public sealed class DailyQuestEntry
{
    public int  QuestId   { get; init; }
    public int  Progress  { get; init; }
    public bool Completed { get; init; }
    public bool Looted    { get; init; }
}

// ── Session statistics ─────────────────────────────────────────────────────────

/// <summary>
/// Live session statistics. All writes are on the bot (UI) thread.
/// Reads from the UI refresh timer are also on the UI thread → no locking needed
/// except for the RecentLoot queue which has BeginInvoke calls from logger.
/// </summary>
public static class SessionStats
{
    // ── Timing ────────────────────────────────────────────────────────────────

    public static DateTime SessionStart { get; } = DateTime.UtcNow;
    public static TimeSpan Runtime      => DateTime.UtcNow - SessionStart;

    // ── Run counters ──────────────────────────────────────────────────────────

    private static volatile int _totalRuns;
    private static volatile int _wins;
    private static volatile int _losses;
    private static volatile int _totalItems;
    private static volatile int _dailiesClaimed;
    private static volatile int _currentQueueIndex;  // which dungeon in the queue
    private static volatile int _queueTotal;         // total dungeons in queue

    public static int TotalRuns        => _totalRuns;
    public static int Wins             => _wins;
    public static int Losses           => _losses;
    public static int TotalItems       => _totalItems;
    public static int DailiesClaimed   => _dailiesClaimed;
    public static int CurrentQueueIndex => _currentQueueIndex;
    public static int QueueTotal       => _queueTotal;

    // ── Currency (correct field names from Character.cs decompile) ─────────────
    // chal9=gold(long), chal10=credits(long), cha27=energy(int),
    // cha29=tickets(int), cha67=shards(int)

    private static long _gold;
    private static long _credits;
    private static volatile int _energy;
    private static volatile int _tickets;
    private static volatile int _shards;

    // Energy regeneration tracking: cha28=timestamp(ms), cha97=cooldown per unit(ms)
    private static long _energyUpdatedAt;   // server ms timestamp
    private static long _energyCooldownMs;  // ms between +1 energy

    // Tickets regen: cha30=timestamp, cha98=cooldown
    private static long _ticketsUpdatedAt;
    private static long _ticketsCooldownMs;

    public static long Gold     => _gold;
    public static long Credits  => _credits;
    public static int  Energy   => _energy;
    public static int  Tickets  => _tickets;
    public static int  Shards   => _shards;

    /// <summary>
    /// Estimated UTC time when the next energy unit regenerates.
    /// Returns DateTime.MinValue if regeneration data has not been received yet.
    /// </summary>
    public static DateTime NextEnergyAt
    {
        get
        {
            if (_energyCooldownMs <= 0 || _energyUpdatedAt <= 0) return DateTime.MinValue;
            // energyUpdatedAt is a server-side unix ms timestamp
            var updatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(_energyUpdatedAt).UtcDateTime;
            return updatedUtc.AddMilliseconds(_energyCooldownMs);
        }
    }

    public static DateTime NextTicketAt
    {
        get
        {
            if (_ticketsCooldownMs <= 0 || _ticketsUpdatedAt <= 0) return DateTime.MinValue;
            var updatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(_ticketsUpdatedAt).UtcDateTime;
            return updatedUtc.AddMilliseconds(_ticketsCooldownMs);
        }
    }

    // ── Highest unlocked zone (cha94) ─────────────────────────────────────────

    private static volatile int _highestZone;
    public static int HighestZone => _highestZone;

    // ── Current bot state ─────────────────────────────────────────────────────

    private static volatile string _currentState  = "Starting...";
    private static volatile string _currentAction = "";
    private static volatile int    _currentZone;
    private static volatile int    _currentNode;
    private static volatile int    _currentWave;
    private static volatile int    _retryCount;

    public static string CurrentState  => _currentState;
    public static string CurrentAction => _currentAction;
    public static int    CurrentZone   => _currentZone;
    public static int    CurrentNode   => _currentNode;
    public static int    CurrentWave   => _currentWave;
    public static int    RetryCount    => _retryCount;

    // ── Recent loot ───────────────────────────────────────────────────────────

    private static readonly object        _lootLock   = new();
    private static readonly Queue<string> _recentLoot = new(10);

    public static IReadOnlyList<string> RecentLoot
    {
        get { lock (_lootLock) { return _recentLoot.ToArray(); } }
    }

    // ── Update methods ────────────────────────────────────────────────────────

    public static void RecordRun(DungeonResult result)
    {
        _totalRuns++;
        if (result.Victory) _wins++; else _losses++;
        _totalItems += result.Loot.Count;
        lock (_lootLock)
        {
            foreach (var item in result.Loot)
            {
                if (_recentLoot.Count >= 10) _recentLoot.Dequeue();
                _recentLoot.Enqueue(item.ToString());
            }
        }
    }

    public static void RecordDailyClaimed() => _dailiesClaimed++;

    public static void SetState(string state, string action = "")
    {
        _currentState  = state;
        _currentAction = action;
    }

    public static void SetZone(int zone, int node, int wave = 0)
    {
        _currentZone = zone;
        _currentNode = node;
        _currentWave = wave;
    }

    public static void SetWave(int wave)    => _currentWave = wave;
    public static void SetRetryCount(int n) => _retryCount  = n;

    public static void SetQueuePosition(int index, int total)
    {
        _currentQueueIndex = index;
        _queueTotal        = total;
    }

    /// <summary>
    /// Update currency values. Pass -1 for any field to leave it unchanged.
    /// All long values match the server's actual field types.
    /// </summary>
    public static void UpdateCurrency(long gold = -1, long credits = -1,
                                      int energy = -1, int tickets = -1, int shards = -1)
    {
        if (gold    >= 0) _gold    = gold;
        if (credits >= 0) _credits = credits;
        if (energy  >= 0) _energy  = energy;
        if (tickets >= 0) _tickets = tickets;
        if (shards  >= 0) _shards  = shards;
    }

    /// <summary>
    /// Update energy regeneration timing data from cha28 (last-update timestamp ms)
    /// and cha97 (cooldown ms per energy unit).
    /// </summary>
    public static void UpdateEnergyRegen(long updatedAtMs, long cooldownMs)
    {
        if (updatedAtMs > 0) _energyUpdatedAt  = updatedAtMs;
        if (cooldownMs  > 0) _energyCooldownMs = cooldownMs;
    }

    /// <summary>
    /// Update ticket regeneration timing from cha30 + cha98.
    /// </summary>
    public static void UpdateTicketRegen(long updatedAtMs, long cooldownMs)
    {
        if (updatedAtMs > 0) _ticketsUpdatedAt  = updatedAtMs;
        if (cooldownMs  > 0) _ticketsCooldownMs = cooldownMs;
    }

    public static void UpdateHighestZone(int zone)
    {
        if (zone > _highestZone) _highestZone = zone;
    }

    public static void Reset()
    {
        _totalRuns = _wins = _losses = _totalItems = _dailiesClaimed = 0;
        _gold = _credits = 0;
        _energy = _tickets = _shards = 0;
        _energyUpdatedAt = _energyCooldownMs = 0;
        _ticketsUpdatedAt = _ticketsCooldownMs = 0;
        _highestZone = 0;
        _currentState = "Starting...";
        _currentAction = "";
        _currentZone = _currentNode = _currentWave = _retryCount = 0;
        _currentQueueIndex = _queueTotal = 0;
        lock (_lootLock) _recentLoot.Clear();
    }
}
