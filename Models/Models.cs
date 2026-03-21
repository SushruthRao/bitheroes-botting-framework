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

    // ItemType constants from ItemBook.Lookup switch (ite1 wire field):
    //   1=Equipment  2=Material  3=Currency  4=Consumable
    //   6=Familiar   8=Mount     9=Rune      11=Enchant   15=Augment
    // Currency ItemId: 1=Gold  2=Credits  3=EXP  4=Energy  5=Tickets  67=Shards
    public string TypeLabel => ItemType switch
    {
        1  => "Equipment",
        2  => "Material",
        3  => ItemId switch { 1 => "Gold", 2 => "Credits", 3 => "EXP",
                              4 => "Energy", 5 => "Tickets", 67 => "Shards", _ => "Currency" },
        4  => "Consumable",
        6  => "Familiar",
        8  => "Mount",
        9  => "Rune",
        11 => "Enchant",
        15 => "Augment",
        _  => $"Type{ItemType}"
    };

    public bool IsCurrency => ItemType == 3;

    public override string ToString()
    {
        if (IsCurrency) return $"{TypeLabel}×{Qty:N0}";
        var name = ItemNameLookup.Resolve(ItemId, ItemType);
        return name != null ? $"{name}×{Qty}" : $"{TypeLabel}(id={ItemId})×{Qty}";
    }
}

// ── Item name lookup (populated from LOAD_XMLS xml0 books) ────────────────────

public static class ItemNameLookup
{
    static readonly Dictionary<(int id, int type), string> _names = new();

    public static void Register(int id, int type, string name) => _names[(id, type)] = name;

    public static string? Resolve(int id, int type) =>
        _names.TryGetValue((id, type), out var name) ? name : null;

    public static int Count => _names.Count;
}

// ── Loot entry (one battle's rewards) ──────────────────────────────────────────

public sealed class LootEntry
{
    public DateTime         Time      { get; init; }
    public int              ZoneId    { get; init; }
    public int              NodeId    { get; init; }
    public long             GoldDelta { get; init; }   // gold earned this encounter
    public long             ExpDelta  { get; init; }   // exp  earned this encounter
    public int              NewLevel  { get; init; }   // 0 = no level-up / unknown
    public List<LootItem>   Items     { get; init; } = new();

    // One-line summary for the loot feed list
    public string Summary()
    {
        var parts = new System.Text.StringBuilder();
        parts.Append($"[{Time.ToLocalTime():HH:mm:ss}]  Z{ZoneId}/N{NodeId}");

        if (GoldDelta > 0)   parts.Append($"  +{GoldDelta:N0}g");
        if (ExpDelta  > 0)   parts.Append($"  +{ExpDelta:N0}xp");
        if (NewLevel  > 0)   parts.Append($"  ★Lv{NewLevel}");

        // Bucket items by type label, e.g. "Equip×3, Famil×1"
        if (Items.Count > 0)
        {
            var buckets = Items
                .GroupBy(i => i.TypeLabel)
                .Select(g => $"{g.Key}×{g.Sum(i => i.Qty)}");
            parts.Append($"  [{string.Join(", ", buckets)}]");
        }
        return parts.ToString();
    }
}

// ── Dungeon run result ─────────────────────────────────────────────────────────

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

// ── Teammate display info ──────────────────────────────────────────────────────

public sealed record TeammateInfo(int Id, int Type, int Power, int Stamina, int Agility, bool Online = true)
{
    public int    Total    => Power + Stamina + Agility;
    public string TypeName => Type == 2 ? "Familiar" : "Player";
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
/// Live session statistics. All writes are on the bot thread.
/// UI timer reads are also on the UI thread — no locking needed except
/// for the RecentLoot queue which is written from bot context.
/// </summary>
public static class SessionStats
{
    // ── Timing ────────────────────────────────────────────────────────────────

    public static DateTime SessionStart { get; } = DateTime.UtcNow;
    public static TimeSpan Runtime      => DateTime.UtcNow - SessionStart;

    // ── Dungeon run counters ──────────────────────────────────────────────────

    private static volatile int _totalRuns;
    private static volatile int _wins;
    private static volatile int _losses;
    private static volatile int _totalItems;
    private static volatile int _dailiesClaimed;
    private static volatile int _currentQueueIndex;
    private static volatile int _queueTotal;

    public static int TotalRuns         => _totalRuns;
    public static int Wins              => _wins;
    public static int Losses            => _losses;
    public static int TotalItems        => _totalItems;
    public static int DailiesClaimed    => _dailiesClaimed;
    public static int CurrentQueueIndex => _currentQueueIndex;
    public static int QueueTotal        => _queueTotal;

    // ── Individual encounter counters ─────────────────────────────────────────

    private static volatile int _encountersWon;
    private static volatile int _encountersLost;

    public static int EncountersWon  => _encountersWon;
    public static int EncountersLost => _encountersLost;

    // ── Session earnings (running totals since session start) ─────────────────

    private static long _goldGained;    // total gold earned from battles
    private static long _expGained;     // total EXP earned from battles

    public static long GoldGained => _goldGained;
    public static long ExpGained  => _expGained;

    // ── Currency (field names from Character.cs decompile) ────────────────────
    // chal9=gold(long), chal10=credits(long), cha5=exp(long), cha4=level(int)
    // cha27=energy(int), cha29=tickets(int), cha67=shards(int)

    private static long _gold;
    private static long _credits;
    private static long _exp;
    private static volatile int _level;
    private static volatile int _energy;
    private static volatile int _tickets;
    private static volatile int _shards;

    // Energy regeneration: cha28=timestamp(ms), cha97=cooldown per unit(ms)
    private static long _energyUpdatedAt;
    private static long _energyCooldownMs;

    // Tickets regen: cha30=timestamp, cha98=cooldown
    private static long _ticketsUpdatedAt;
    private static long _ticketsCooldownMs;

    public static long Gold     => _gold;
    public static long Credits  => _credits;
    public static long Exp      => _exp;
    public static int  Level    => _level;
    public static int  Energy   => _energy;
    public static int  Tickets  => _tickets;
    public static int  Shards   => _shards;

    public static DateTime NextEnergyAt
    {
        get
        {
            if (_energyCooldownMs <= 0 || _energyUpdatedAt <= 0) return DateTime.MinValue;
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
    private static volatile int    _enemiesTotal;    // enemies in current dungeon
    private static volatile int    _enemiesCleared;  // enemies defeated so far

    public static string CurrentState   => _currentState;
    public static string CurrentAction  => _currentAction;
    public static int    CurrentZone    => _currentZone;
    public static int    CurrentNode    => _currentNode;
    public static int    CurrentWave    => _currentWave;
    public static int    RetryCount     => _retryCount;
    public static int    EnemiesTotal   => _enemiesTotal;
    public static int    EnemiesCleared => _enemiesCleared;

    // ── Active team ───────────────────────────────────────────────────────────

    private static volatile IReadOnlyList<TeammateInfo> _currentTeam = Array.Empty<TeammateInfo>();
    public  static          IReadOnlyList<TeammateInfo> CurrentTeam  => _currentTeam;
    public  static void SetTeam(IReadOnlyList<TeammateInfo> team) => _currentTeam = team;

    // ── Recent loot feed ──────────────────────────────────────────────────────

    private static readonly object          _lootLock   = new();
    private static readonly Queue<LootEntry> _recentLoot = new();

    public static IReadOnlyList<LootEntry> RecentLoot
    {
        get { lock (_lootLock) { return _recentLoot.ToArray(); } }
    }

    // ── Update methods ────────────────────────────────────────────────────────

    public static void RecordRun(DungeonResult result)
    {
        _totalRuns++;
        if (result.Victory) _wins++; else _losses++;
    }

    /// <summary>
    /// Record the result of one individual enemy encounter (battle).
    /// goldDelta / expDelta are the amounts earned from this encounter.
    /// </summary>
    public static void RecordEncounter(bool win, LootEntry loot)
    {
        if (win) _encountersWon++; else _encountersLost++;
        if (loot.GoldDelta > 0) _goldGained += loot.GoldDelta;
        if (loot.ExpDelta  > 0) _expGained  += loot.ExpDelta;
        _totalItems += loot.Items.Count(i => !i.IsCurrency);
        lock (_lootLock)
        {
            if (_recentLoot.Count >= 50) _recentLoot.Dequeue();
            _recentLoot.Enqueue(loot);
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

    public static void SetWave(int wave)        => _currentWave   = wave;
    public static void SetRetryCount(int n)     => _retryCount    = n;
    public static void SetEnemyProgress(int cleared, int total)
    {
        _enemiesCleared = cleared;
        _enemiesTotal   = total;
    }

    public static void SetQueuePosition(int index, int total)
    {
        _currentQueueIndex = index;
        _queueTotal        = total;
    }

    /// <summary>
    /// Update currency values. Pass -1 for any field to leave it unchanged.
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

    /// <summary>Update exp (cha5) and level (cha4).</summary>
    public static void UpdateExpAndLevel(long exp = -1, int level = -1)
    {
        if (exp   >= 0) _exp   = exp;
        if (level >= 0) _level = level;
    }

    public static void UpdateEnergyRegen(long updatedAtMs, long cooldownMs)
    {
        if (updatedAtMs > 0) _energyUpdatedAt  = updatedAtMs;
        if (cooldownMs  > 0) _energyCooldownMs = cooldownMs;
    }

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
        _encountersWon = _encountersLost = 0;
        _goldGained = _expGained = 0;
        _gold = _credits = _exp = 0;
        _level = _energy = _tickets = _shards = 0;
        _energyUpdatedAt = _energyCooldownMs = 0;
        _ticketsUpdatedAt = _ticketsCooldownMs = 0;
        _highestZone = 0;
        _currentState = "Starting...";
        _currentAction = "";
        _currentZone = _currentNode = _currentWave = _retryCount = 0;
        _currentQueueIndex = _queueTotal = 0;
        _enemiesTotal = _enemiesCleared = 0;
        _currentTeam = Array.Empty<TeammateInfo>();
        lock (_lootLock) _recentLoot.Clear();
    }
}
