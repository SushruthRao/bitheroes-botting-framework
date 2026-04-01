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

// ── Language string lookup (populated from DLC bundle cache) ──────────────────

public static class LanguageLookup
{
    static readonly Dictionary<string, string> _strings = new();

    public static void Register(string key, string value) =>
        _strings[key.ToLowerInvariant().Trim()] = value;

    public static string? Resolve(string? key) =>
        key != null && _strings.TryGetValue(key.ToLowerInvariant().Trim(), out var v) ? v : null;

    public static int Count => _strings.Count;
}

// ── Rarity color lookup ───────────────────────────────────────────────────────

public static class RarityColorLookup
{
    static readonly Dictionary<string, (uint argb, int rank)> _colors = new();

    public static void Register(string link, string hexColor, int rank)
    {
        if (hexColor.Length == 6 &&
            uint.TryParse(hexColor, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            _colors[link.ToLower()] = (0xFF000000u | rgb, rank);
    }

    public static uint GetArgb(string? link) =>
        link != null && _colors.TryGetValue(link.ToLower(), out var c) ? c.argb : 0;

    public static int GetRank(string? link) =>
        link != null && _colors.TryGetValue(link.ToLower(), out var c) ? c.rank : -1;
}

// ── Item name lookup ──────────────────────────────────────────────────────────

public static class ItemNameLookup
{
    static readonly Dictionary<(int id, int type), (string name, string? rarity)> _items = new();

    public static void Register(int id, int type, string name, string? rarityLink = null) =>
        _items[(id, type)] = (name, rarityLink);

    public static string? Resolve(int id, int type) =>
        _items.TryGetValue((id, type), out var v) ? v.name : null;

    public static string? ResolveRarity(int id, int type) =>
        _items.TryGetValue((id, type), out var v) ? v.rarity : null;

    public static int Count => _items.Count;
}

// ── Loot entry ────────────────────────────────────────────────────────────────

public sealed class LootEntry
{
    public DateTime         Time      { get; init; }
    public int              ZoneId    { get; init; }
    public int              NodeId    { get; init; }
    public long             GoldDelta { get; init; }
    public long             ExpDelta  { get; init; }
    public int              NewLevel  { get; init; }
    public List<LootItem>   Items     { get; init; } = new();

    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[{Time.ToLocalTime():HH:mm:ss}]  Z{ZoneId}/N{NodeId}");

        if (GoldDelta > 0) sb.Append($"  +{GoldDelta:N0}g");
        if (ExpDelta  > 0) sb.Append($"  +{ExpDelta:N0}xp");
        if (NewLevel  > 0) sb.Append($"  ★Lv{NewLevel}");

        if (Items.Count > 0)
        {
            var labels = new List<string>();
            var unknownBuckets = new Dictionary<string, int>();

            foreach (var item in Items)
            {
                if (item.IsCurrency) continue;
                var name = ItemNameLookup.Resolve(item.ItemId, item.ItemType);
                if (name != null)
                    labels.Add(item.Qty > 1 ? $"{name}×{item.Qty}" : name);
                else
                {
                    unknownBuckets.TryGetValue(item.TypeLabel, out int c);
                    unknownBuckets[item.TypeLabel] = c + item.Qty;
                }
            }
            foreach (var kv in unknownBuckets)
                labels.Add($"{kv.Key}×{kv.Value}");

            if (labels.Count > 0)
                sb.Append($"  [{string.Join(", ", labels)}]");
        }
        return sb.ToString();
    }
}

// ── Dungeon run result ────────────────────────────────────────────────────────

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

// ── Teammate display info ─────────────────────────────────────────────────────

public sealed record TeammateInfo(int Id, int Type, int Power, int Stamina, int Agility, bool Online = true)
{
    public int    Total    => Power + Stamina + Agility;
    public string TypeName => Type == 2 ? "Familiar" : "Player";
}

// ── Daily quests ──────────────────────────────────────────────────────────────

public sealed class DailyQuestEntry
{
    public int  QuestId   { get; init; }
    public int  Progress  { get; init; }
    public bool Completed { get; init; }
    public bool Looted    { get; init; }
}

// ── Regen timer ───────────────────────────────────────────────────────────────

/// <summary>
/// Tracks a single regenerating resource's regen state.
///
/// Server sends two fields per resource:
///   remainingMs  (e.g. cha28) = ms remaining until the NEXT +1 tick fires
///   cooldownMs   (e.g. cha97) = fixed interval between consecutive ticks
///
/// Local simulation: ConsumeElapsedTicks() is called from the UI thread every
/// 300 ms. When NextTickAt has passed it advances the timer and returns the
/// number of ticks that fired so SessionStats can increment the local count.
/// Server packets are always authoritative and overwrite local state.
/// </summary>
public sealed class RegenTimer
{
    // UTC instant when the next +1 tick will fire.
    // Set as: DateTime.UtcNow + remainingMs each time a server packet arrives.
    private DateTime _nextTickAt = DateTime.MinValue;

    // Fixed interval between ticks (e.g. 360_000 ms = 6 min per unit).
    private long _cooldownMs;

    // ── Server update ─────────────────────────────────────────────────────────

    /// <summary>
    /// Record updated regen state from a server packet.
    /// Pass remainingMs = -1 to update only the cooldown (rate badge) without
    /// resetting the tick timer.
    /// </summary>
    public void Update(long remainingMs, long cooldownMs)
    {
        if (remainingMs >= 0)
            _nextTickAt = DateTime.UtcNow.AddMilliseconds(remainingMs);
        if (cooldownMs > 0)
            _cooldownMs = cooldownMs;
    }

    // ── Local simulation ──────────────────────────────────────────────────────

    /// <summary>
    /// Advance the timer past DateTime.UtcNow and return how many ticks elapsed.
    /// Called by the UI thread every ~300 ms to simulate regen locally between
    /// server confirmations. Returns 0 if the timer hasn't expired yet, or if
    /// regen data has not been received from the server yet.
    /// </summary>
    public int ConsumeElapsedTicks()
    {
        if (_cooldownMs <= 0 || _nextTickAt == DateTime.MinValue) return 0;
        var now = DateTime.UtcNow;
        if (_nextTickAt > now) return 0;

        // How many full intervals have passed since NextTickAt?
        long behind = (long)(now - _nextTickAt).TotalMilliseconds;
        int ticks = (int)(behind / _cooldownMs) + 1;
        _nextTickAt = _nextTickAt.AddMilliseconds((long)ticks * _cooldownMs);
        return ticks;
    }

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>UTC time of the next +1 tick. DateTime.MinValue if unknown.</summary>
    public DateTime NextTickAt => _nextTickAt;

    /// <summary>Time remaining until the next +1. Zero if already past or unknown.</summary>
    public TimeSpan TimeToNext
    {
        get
        {
            if (_nextTickAt == DateTime.MinValue) return TimeSpan.Zero;
            var r = _nextTickAt - DateTime.UtcNow;
            return r > TimeSpan.Zero ? r : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Total wall-clock time until <paramref name="current"/> reaches
    /// <paramref name="max"/> via regeneration.
    /// Returns TimeSpan.Zero when already full, or when data is unavailable.
    /// </summary>
    public TimeSpan TimeUntilFull(int current, int max)
    {
        int needed = max - current;
        if (needed <= 0 || _cooldownMs <= 0 || _nextTickAt == DateTime.MinValue)
            return TimeSpan.Zero;

        // Time to the upcoming tick, then (needed-1) more full intervals after that.
        double totalMs = TimeToNext.TotalMilliseconds + (needed - 1) * _cooldownMs;
        return TimeSpan.FromMilliseconds(Math.Max(0, totalMs));
    }

    /// <summary>Human-readable regen rate badge, e.g. "+1/6m" or "+1/2h30m".</summary>
    public string RegenRate
    {
        get
        {
            if (_cooldownMs <= 0) return "";
            var t = TimeSpan.FromMilliseconds(_cooldownMs);
            if (t.TotalHours >= 1)
            {
                string mins = t.Minutes > 0 ? $"{t.Minutes}m" : "";
                return $"+1/{(int)t.TotalHours}h{mins}";
            }
            if (t.TotalMinutes >= 1)
            {
                string secs = t.Seconds > 0 ? $"{t.Seconds}s" : "";
                return $"+1/{(int)t.TotalMinutes}m{secs}";
            }
            return $"+1/{(int)t.TotalSeconds}s";
        }
    }

    public void Reset()
    {
        _nextTickAt = DateTime.MinValue;
        _cooldownMs = 0;
    }
}

// ── Session statistics ────────────────────────────────────────────────────────

public static class SessionStats
{
    // ── Timing ───────────────────────────────────────────────────────────────

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

    // ── Encounter counters ────────────────────────────────────────────────────

    private static volatile int _encountersWon;
    private static volatile int _encountersLost;

    public static int EncountersWon  => _encountersWon;
    public static int EncountersLost => _encountersLost;

    // ── Session earnings ──────────────────────────────────────────────────────

    private static long _goldGained;
    private static long _expGained;

    public static long GoldGained => _goldGained;
    public static long ExpGained  => _expGained;

    // ── Currency / resources ──────────────────────────────────────────────────
    // Current values — server-authoritative, also locally simulated between packets.
    // Field names from Character.cs decompile:
    //   chal9=gold, chal10=credits, cha5=exp, cha4=level
    //   cha27=energy, cha29=tickets, cha67=shards, cha71=tokens, cha83=badges

    private static long _gold;
    private static long _credits;
    private static long _exp;
    private static volatile int _level;
    private static volatile int _energy;
    private static volatile int _tickets;
    private static volatile int _shards;
    private static volatile int _tokens;
    private static volatile int _badges;

    public static long Gold    => _gold;
    public static long Credits => _credits;
    public static long Exp     => _exp;
    public static int  Level   => _level;
    public static int  Energy  => _energy;
    public static int  Tickets => _tickets;
    public static int  Shards  => _shards;
    public static int  Tokens  => _tokens;
    public static int  Badges  => _badges;

    // ── Max capacity ──────────────────────────────────────────────────────────
    // Two sources, used in priority order:
    //   1. VariableBook values parsed from xml0 — gives base max (+ level scaling for energy).
    //      We ignore per-character GameModifier bonuses since we don't track equipment.
    //   2. Observed max — inferred when the server sends a resource value WITHOUT a regen
    //      timer (cha28 absent), which the game only does when the resource is at cap.

    // VariableBook base values (populated once from ParseVariableBook)
    private static volatile int _vbEnergyMax;       // "energyMax" XML element
    private static volatile int _vbEnergyIncrease;  // "energyIncrease" XML element (per level)
    private static volatile int _vbTicketsMax;       // "ticketsMax"
    private static volatile int _vbShardsMax;        // "shardsMax"
    private static volatile int _vbTokensMax;        // "tokensMax"
    private static volatile int _vbBadgesMax;        // "badgesMax"

    // Observed maximums — updated when server confirms resource is at cap
    private static volatile int _obsEnergyMax;
    private static volatile int _obsTicketsMax;
    private static volatile int _obsShardsMax;
    private static volatile int _obsTokensMax;
    private static volatile int _obsBadgesMax;

    /// <summary>
    /// Effective energy cap: VariableBook formula takes priority, observed max as fallback.
    /// Formula from Character.cs: energyMax + (level-1) * energyIncrease + modifiers.
    /// We omit the modifier bonus (requires full equipment parsing).
    /// </summary>
    public static int EnergyMax =>
        _vbEnergyMax > 0
            ? _vbEnergyMax + Math.Max(0, _level - 1) * _vbEnergyIncrease
            : _obsEnergyMax;

    public static int TicketsMax => _vbTicketsMax > 0 ? _vbTicketsMax : _obsTicketsMax;
    public static int ShardsMax  => _vbShardsMax  > 0 ? _vbShardsMax  : _obsShardsMax;
    public static int TokensMax  => _vbTokensMax  > 0 ? _vbTokensMax  : _obsTokensMax;
    public static int BadgesMax  => _vbBadgesMax  > 0 ? _vbBadgesMax  : _obsBadgesMax;

    // ── Regen timers ──────────────────────────────────────────────────────────

    public static readonly RegenTimer EnergyRegen  = new();
    public static readonly RegenTimer TicketsRegen = new();
    public static readonly RegenTimer ShardsRegen  = new();
    public static readonly RegenTimer TokensRegen  = new();
    public static readonly RegenTimer BadgesRegen  = new();

    // ── Highest unlocked zone (cha94) ─────────────────────────────────────────

    private static volatile int _highestZone;
    public static int HighestZone => _highestZone;

    // ── Bot state ─────────────────────────────────────────────────────────────

    private static volatile string _currentState  = "Starting...";
    private static volatile string _currentAction = "";
    private static volatile int    _currentZone;
    private static volatile int    _currentNode;
    private static volatile int    _currentWave;
    private static volatile int    _retryCount;
    private static volatile int    _enemiesTotal;
    private static volatile int    _enemiesCleared;

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

    // ── Item drop breakdown ───────────────────────────────────────────────────

    private static readonly Dictionary<string, (int type, int qty, string? rarity)> _itemBreakdown = new();
    private static readonly object _itemLock = new();

    public static void RecordItemDrop(int itemType, string displayName, int qty, string? rarityLink = null)
    {
        lock (_itemLock)
        {
            if (_itemBreakdown.TryGetValue(displayName, out var existing))
                _itemBreakdown[displayName] = (existing.type, existing.qty + qty, existing.rarity ?? rarityLink);
            else
                _itemBreakdown[displayName] = (itemType, qty, rarityLink);
        }
    }

    public static IReadOnlyList<(string name, int type, int qty, string? rarity)> GetItemBreakdown()
    {
        lock (_itemLock)
            return _itemBreakdown
                .Select(kv => (kv.Key, kv.Value.type, kv.Value.qty, kv.Value.rarity))
                .OrderByDescending(t => t.qty)
                .ToList();
    }

    // ── Recent loot feed ──────────────────────────────────────────────────────

    private static readonly object           _lootLock   = new();
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

    public static void RecordEncounter(bool win, LootEntry loot)
    {
        if (win) _encountersWon++; else _encountersLost++;
        if (loot.GoldDelta > 0) _goldGained += loot.GoldDelta;
        if (loot.ExpDelta  > 0) _expGained  += loot.ExpDelta;
        var nonCurrency = loot.Items.Where(i => !i.IsCurrency).ToList();
        _totalItems += nonCurrency.Count;
        foreach (var item in nonCurrency)
        {
            var name   = ItemNameLookup.Resolve(item.ItemId, item.ItemType) ?? item.TypeLabel;
            var rarity = ItemNameLookup.ResolveRarity(item.ItemId, item.ItemType);
            RecordItemDrop(item.ItemType, name, item.Qty, rarity);
        }
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

    public static void SetWave(int wave)    => _currentWave   = wave;
    public static void SetRetryCount(int n) => _retryCount    = n;

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

    /// <summary>Update currency values. Pass -1 to leave a field unchanged.</summary>
    public static void UpdateCurrency(long gold = -1, long credits = -1,
                                      int energy = -1, int tickets = -1, int shards = -1,
                                      int tokens = -1, int badges = -1)
    {
        if (gold    >= 0) _gold    = gold;
        if (credits >= 0) _credits = credits;
        if (energy  >= 0) _energy  = energy;
        if (tickets >= 0) _tickets = tickets;
        if (shards  >= 0) _shards  = shards;
        if (tokens  >= 0) _tokens  = tokens;
        if (badges  >= 0) _badges  = badges;
    }

    /// <summary>
    /// Mark a resource as currently at its maximum (server sent value without a regen timer).
    /// Stored as the observed max so TimeUntilFull() can work before VariableBook is parsed.
    /// Pass -1 to leave a field unchanged.
    /// </summary>
    public static void MarkAtCap(int energy = -1, int tickets = -1, int shards = -1,
                                 int tokens = -1, int badges = -1)
    {
        if (energy  > 0 && energy  > _obsEnergyMax)  _obsEnergyMax  = energy;
        if (tickets > 0 && tickets > _obsTicketsMax) _obsTicketsMax = tickets;
        if (shards  > 0 && shards  > _obsShardsMax)  _obsShardsMax  = shards;
        if (tokens  > 0 && tokens  > _obsTokensMax)  _obsTokensMax  = tokens;
        if (badges  > 0 && badges  > _obsBadgesMax)  _obsBadgesMax  = badges;
    }

    /// <summary>Update exp (cha5) and level (cha4).</summary>
    public static void UpdateExpAndLevel(long exp = -1, int level = -1)
    {
        if (exp   >= 0) _exp   = exp;
        if (level >= 0) _level = level;
    }

    /// <summary>
    /// Store VariableBook base values parsed from the xml0 VariableBook.xml entry.
    /// These give the server-configured resource caps (before per-character modifiers).
    /// </summary>
    public static void SetVariableBookMaxes(int energyMax, int energyIncrease,
                                            int ticketsMax, int shardsMax,
                                            int tokensMax,  int badgesMax)
    {
        if (energyMax      > 0) _vbEnergyMax      = energyMax;
        if (energyIncrease > 0) _vbEnergyIncrease = energyIncrease;
        if (ticketsMax     > 0) _vbTicketsMax      = ticketsMax;
        if (shardsMax      > 0) _vbShardsMax       = shardsMax;
        if (tokensMax      > 0) _vbTokensMax       = tokensMax;
        if (badgesMax      > 0) _vbBadgesMax       = badgesMax;
    }

    // Regen delegation
    public static void UpdateEnergyRegen (long remainingMs, long cooldownMs) => EnergyRegen .Update(remainingMs, cooldownMs);
    public static void UpdateTicketRegen (long remainingMs, long cooldownMs) => TicketsRegen.Update(remainingMs, cooldownMs);
    public static void UpdateShardsRegen (long remainingMs, long cooldownMs) => ShardsRegen .Update(remainingMs, cooldownMs);
    public static void UpdateTokensRegen (long remainingMs, long cooldownMs) => TokensRegen .Update(remainingMs, cooldownMs);
    public static void UpdateBadgesRegen (long remainingMs, long cooldownMs) => BadgesRegen .Update(remainingMs, cooldownMs);

    /// <summary>
    /// Simulate local resource regeneration. Called from the UI thread every ~300 ms.
    /// Advances each regen timer and increments the local resource count by the number
    /// of ticks that have elapsed since the last call. Stops at the known cap.
    /// Server packets always overwrite these locally-simulated values.
    /// </summary>
    public static void SimulateRegen()
    {
        int eMax = EnergyMax;
        if (eMax > 0 && _energy < eMax)
        {
            int t = EnergyRegen.ConsumeElapsedTicks();
            if (t > 0) _energy = Math.Min(_energy + t, eMax);
        }

        int tkMax = TicketsMax;
        if (tkMax > 0 && _tickets < tkMax)
        {
            int t = TicketsRegen.ConsumeElapsedTicks();
            if (t > 0) _tickets = Math.Min(_tickets + t, tkMax);
        }

        int shMax = ShardsMax;
        if (shMax > 0 && _shards < shMax)
        {
            int t = ShardsRegen.ConsumeElapsedTicks();
            if (t > 0) _shards = Math.Min(_shards + t, shMax);
        }

        int tokMax = TokensMax;
        if (tokMax > 0 && _tokens < tokMax)
        {
            int t = TokensRegen.ConsumeElapsedTicks();
            if (t > 0) _tokens = Math.Min(_tokens + t, tokMax);
        }

        int bdMax = BadgesMax;
        if (bdMax > 0 && _badges < bdMax)
        {
            int t = BadgesRegen.ConsumeElapsedTicks();
            if (t > 0) _badges = Math.Min(_badges + t, bdMax);
        }
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
        _level = _energy = _tickets = _shards = _tokens = _badges = 0;
        _vbEnergyMax = _vbEnergyIncrease = _vbTicketsMax = 0;
        _vbShardsMax = _vbTokensMax = _vbBadgesMax = 0;
        _obsEnergyMax = _obsTicketsMax = _obsShardsMax = _obsTokensMax = _obsBadgesMax = 0;
        EnergyRegen.Reset();
        TicketsRegen.Reset();
        ShardsRegen.Reset();
        TokensRegen.Reset();
        BadgesRegen.Reset();
        _highestZone = 0;
        _currentState = "Starting...";
        _currentAction = "";
        _currentZone = _currentNode = _currentWave = _retryCount = 0;
        _currentQueueIndex = _queueTotal = 0;
        _enemiesTotal = _enemiesCleared = 0;
        _currentTeam = Array.Empty<TeammateInfo>();
        lock (_lootLock) _recentLoot.Clear();
        lock (_itemLock) _itemBreakdown.Clear();
    }
}
