# BitReaper — Developer Documentation

Complete reference for the project structure, internal architecture, protocols, data models, and extension points.

---

## Table of Contents

1. [Project Structure](#1-project-structure)
2. [Architecture Overview](#2-architecture-overview)
3. [BHBot — State Machine](#3-bhbot--state-machine)
4. [Network Protocol](#4-network-protocol)
5. [Configuration System](#5-configuration-system)
6. [Models](#6-models)
7. [Logger](#7-logger)
8. [GUI (MainForm)](#8-gui-mainform)
9. [Item Name Resolution](#9-item-name-resolution)
10. [Rarity Color System](#10-rarity-color-system)
11. [Unity Bundle Extraction](#11-unity-bundle-extraction)
12. [Session Statistics](#12-session-statistics)
13. [Adding New DALC Handlers](#13-adding-new-dalc-handlers)
14. [Known Limitations](#14-known-limitations)

---

## 1. Project Structure

```
BitHeroesClient/
├── Bot/
│   └── BHBot.cs            — Core bot: SFS2X connection, state machine, all DALC send/recv
├── Config/
│   ├── AppConfig.cs        — Config model classes (ConnectionConfig, AutomationConfig, …)
│   └── ConfigLoader.cs     — JSON load/save/validate
├── Gui/
│   └── MainForm.cs         — WinForms UI: stats panel, dungeon queue editor, loot tabs, log
├── Logging/
│   └── Logger.cs           — Thread-safe logger: file + in-memory buffer + LineWritten event
├── Models/
│   └── Models.cs           — Shared data models and static lookup tables
├── Program.cs              — Entry point
├── config.json             — Runtime configuration (not committed)
├── steam_appid.txt         — Steamworks app ID (1358440)
└── BitHeroesClient.csproj  — Project file with DLL references to game-installed assemblies
```

---

## 2. Architecture Overview

```
Program.Main()
    └── MainForm (WinForms, STAThread)
            ├── BHBot.Start()          — connects to SFS2X, begins state machine
            ├── BHBot.Tick()           — pumps SFS2X events (called by _botTimer every 50ms)
            └── _uiTimer (500ms)       — reads SessionStats, flushes log queue, refreshes GUI

BHBot (static)
    └── SmartFox (_sfs)
            ├── OnConnection           — SFS2X connected → GuestLogin → LoginPlatform
            ├── OnLogin                → SetupExtension, LoadXMLs, GetCharacterList
            ├── OnExtensionResponse    → dispatched by dal0 field → handler methods
            └── OnConnectionLost       — cleanup, reconnect not attempted
```

Everything in `BHBot` is **static**. There is one connection per process. The bot thread is the SFS2X socket-reader thread; all `OnExtension*` callbacks fire on it. The UI timer reads `SessionStats` (volatile fields, no locking needed) and drains the thread-safe log queue.

---

## 3. BHBot — State Machine

### Login states (`LoginState`)

| State | Meaning |
|---|---|
| `Disconnected` | Not connected to SFS2X |
| `SfsLoggedIn` | SFS2X guest login complete; platform login in flight |
| `AwaitingPlatformResp` | `PlayerDALC.LOGIN_PLATFORM` sent; waiting for character list |
| `LoadingXmls` | `UserDALC.LOAD_XMLS` sent; waiting for xml0 packet |
| `InGame` | Character selected; `OnInGame` fired; dungeon loop active |

### Auto states (`AutoState`)

| State | Transition in | Transition out |
|---|---|---|
| `Idle` | Start, stop, energy wait resolved | `EnteringDungeon` on LoopTick |
| `ClaimingDailyReward` | `InGame` + config enabled | `CheckingDailyQuests` or `Idle` |
| `CheckingDailyQuests` | After daily reward | `ClaimingDailyQuest` or `Idle` |
| `ClaimingDailyQuest` | Quest found completed | `CheckingDailyQuests` |
| `EnteringDungeon` | LoopTick | `WaitingForBattle` on ENTER_DUNGEON resp |
| `WaitingForBattle` | Dungeon entered | `AutoBattleActive` on ENTER_BATTLE |
| `AutoBattleActive` | Enemy activated, AUTO sent | `WaitingForResults` on wave done |
| `WaitingForResults` | RESULTS sent | `WaitingForBattle` (more waves) or `CooldownBeforeRepeat` |
| `CooldownBeforeRepeat` | Run complete | `EnteringDungeon` after repeatDelayMs |
| `EnergyWait` | NO_ENERGY / NO_TICKETS error | `Idle` on re-check |
| `Stopped` | MaxRuns reached, ZONE_LOCKED, tutorial Stop | Terminal |

### Key timer fields

| Field | Interval | Purpose |
|---|---|---|
| `_lastHeartbeatMs` | 90,000 ms | Sends `GameDALC IDLE_RESPONSE` to keep session alive |
| `BATTLE_DONE_TIMEOUT_MS` | 15,000 ms | Fires RESULTS if no new ENTER_BATTLE arrives after a wave |

---

## 4. Network Protocol

### SmartFoxServer 2X

The game uses SFS2X (`Sfs2X` namespace, `SmartFox2X.dll`). All packets are `SFSObject` (binary protocol). Thread-safe mode is enabled; events are processed by `sfs.ProcessEvents()` which is called from `BHBot.Tick()`.

### Standard packet envelope

Every outgoing packet sent via `BHBot.Send()` includes:

| Key | Type | Value |
|---|---|---|
| `dal0` | Int | DALC router ID (see table below) |
| `act0` | Int | Action within the DALC |
| `cli1` | UtfString | App version (`"2.5.6"`) |
| `cli2` | UtfString | SFS2X lib version (`"1.7.5"`) |
| `cli3` | UtfString | DLC/asset-bundle version (`"StandaloneWindows64_…"`) |

### DALC routing

Incoming packets are routed by reading `dal0` from the `SFSObject`. The `OnExtensionResponse` handler dispatches to per-DALC methods.

| `dal0` | DALC | Handled in |
|---|---|---|
| 0 | GameDALC | `HandleGame()` |
| 1 | CharacterDALC | `HandleCharacter()` |
| 4 | UserDALC | `HandleUser()` |
| 10 | BattleDALC | `HandleBattle()` |
| 21 | PlayerDALC | `HandlePlayer()` |

Other DALC IDs are received but not explicitly handled (loot from non-dungeon sources, guild events, etc.).

### Key action constants

**PlayerDALC (dal0=21)**

| act0 | Action | Method |
|---|---|---|
| 1 | LOGIN_PLATFORM | `DoLoginPlatform()` |
| 4 | GET_CHARACTER_LIST | `DoGetCharacterList()` |
| 6 | SELECT_CHARACTER | `DoConfirmCharacter()` |

**UserDALC (dal0=4)**

| act0 | Action | Method |
|---|---|---|
| 5 | LOAD_XMLS | `DoLoadXMLs()` |

**GameDALC (dal0=0)**

| act0 | Action | Direction |
|---|---|---|
| 1 | ENTER_DUNGEON | Server → Client (response to enter request) |
| 12 | IDLE_RESPONSE | Client → Server (heartbeat) |
| 14 | RECONNECT_DUNGEON | Client → Server (cancel orphan) |

**BattleDALC (dal0=10)**

| act0 | Action | Method |
|---|---|---|
| 2 | ABILITY | `SendAbility()` |
| 4 | RESULTS | `SendResults()` |
| 5 | AUTO | `SendAuto()` |
| 7 | QUIT | `SendQuit()` |
| 9 | CAPTURE_DECLINE | `SendCaptureDecline()` |

### Password hashing

```
hash = MD5(plaintext_password + "k5iw3la0")
```

Used in `PlayerDALC.doLoginEmail` (`pla1` field). Steam login uses the auth ticket instead and does not need password hashing.

### Steam authentication

1. `SteamAPI.Init()` — must succeed before connecting
2. `SteamUser.RequestEncryptedAppTicket()` — async; bot polls until ready
3. Ticket bytes are hex-encoded and sent as `use4`; Steam ID goes in `use3`

---

## 5. Configuration System

### Files

- **`Config/AppConfig.cs`** — Model classes. Root type: `AppConfig`.
- **`Config/ConfigLoader.cs`** — `Load(path)` / `Save(cfg, path)`. Uses `System.Text.Json` with camelCase policy.

### AppConfig tree

```
AppConfig
├── ConnectionConfig   connection
├── AutomationConfig   automation
├── List<DungeonConfig> dungeonQueue
├── GuiConfig          gui
└── LoggingConfig      logging
```

See `AppConfig.cs` for XML doc on every field.

### Validation

`ConfigLoader.Validate()` runs after deserialization. It clamps negative delays, warns on empty queue, and prints to stderr. The bot receives the (possibly partially corrected) config regardless.

### Runtime config reload

The GUI's Queue tab edits `AppConfig.DungeonQueue` in-memory via `BHBot.UpdateConfig(cfg)`. Config is **not** auto-saved; the user must click Save in the Queue tab.

---

## 6. Models

All models live in `Models/Models.cs`. No persistence — all state is in-memory per session.

### `LootItem`

Represents one item stack from a loot packet.

| Property | Type | Notes |
|---|---|---|
| `ItemId` | int | Server item ID |
| `ItemType` | int | 1=Equipment, 2=Material, 3=Currency, 4=Consumable, 6=Familiar, 8=Mount, 9=Rune, 11=Enchant, 15=Augment |
| `Qty` | int | Stack size |
| `TypeLabel` | string | Human-readable type name; currency IDs resolve to Gold/Credits/etc. |
| `IsCurrency` | bool | `ItemType == 3` |

`ToString()` resolves the item name via `ItemNameLookup.Resolve()`, falling back to `TypeLabel(id=N)`.

### `LootEntry`

One battle's rewards — written to `SessionStats.RecentLoot` and the GUI's loot list.

| Property | Notes |
|---|---|
| `Time` | UTC timestamp |
| `ZoneId` / `NodeId` | Dungeon location |
| `GoldDelta` / `ExpDelta` | Resources earned this encounter |
| `NewLevel` | > 0 if a level-up occurred |
| `Items` | `List<LootItem>` |

`Summary()` formats a one-line display string for the loot list row.

### `DungeonResult`

Full run result after all waves complete. Carries `Victory`, the full loot list, `RunNumber`, zone/node/difficulty, and wave count. Passed to `BHBot.OnDungeonComplete`.

### `LanguageLookup`

Static dictionary mapping localization key → English string. Populated from the English language XML extracted from the Unity bundle cache. Case-insensitive, trimmed keys.

```csharp
LanguageLookup.Register(key, value);
string? text = LanguageLookup.Resolve(key);
```

### `ItemNameLookup`

Static dictionary mapping `(itemId, itemType)` → `(name, rarityLink)`. Populated from the `xml0` packet's item books (EquipmentBook, MaterialBook, etc.).

```csharp
ItemNameLookup.Register(id, type, name, rarityLink);
string? name   = ItemNameLookup.Resolve(id, type);
string? rarity = ItemNameLookup.ResolveRarity(id, type);
```

### `RarityColorLookup`

Static dictionary mapping rarity link string (e.g. `"legendary"`) → `(argbColor uint, rank int)`. Populated from `RarityBook.xml` in the `xml0` packet. The rank is the `id` attribute from RarityBook and increases with rarity tier.

```csharp
RarityColorLookup.Register(link, hexColor, rank); // hexColor = "RRGGBB"
uint  argb = RarityColorLookup.GetArgb(link);      // 0 if unknown
int   rank = RarityColorLookup.GetRank(link);      // -1 if unknown
```

---

## 7. Logger

**File:** `Logging/Logger.cs`

Thread-safe. Never blocks callers. Writes to a circular in-memory buffer and an optional file, then fires `LineWritten` for the GUI.

### Methods

```csharp
Logger.Configure(logToFile, logFilePath, verboseMode, bufferLines);
Logger.Info(msg);
Logger.Warn(msg);
Logger.Error(msg);
Logger.Debug(msg);       // no-op unless verboseMode=true
Logger.Loot(msg, argb); // Info-level with explicit ARGB color override for the GUI
Logger.Shutdown();       // flushes and closes the file writer
```

### Event

```csharp
public static event Action<LogLevel, string, uint>? LineWritten;
// LogLevel: Debug | Info | Warn | Error
// string:   formatted line "[HH:mm:ss.fff] [INF] message"
// uint:     ARGB color override — 0 means use level-based default
```

The GUI subscribes to `LineWritten` on the main thread, enqueues entries into a `ConcurrentQueue<(LogLevel, string, uint)>`, and drains the queue on the UI timer to avoid cross-thread WinForms calls.

### Color rules (GUI)

| Condition | Color |
|---|---|
| `argbColor != 0` | The explicit ARGB value |
| `LogLevel.Warn` | `RGB(255, 200, 60)` — amber |
| `LogLevel.Error` | `RGB(230, 90, 90)` — red |
| `LogLevel.Debug` | `RGB(120, 120, 120)` — gray |
| `LogLevel.Info` (default) | `RGB(200, 200, 200)` — light gray |

---

## 8. GUI (MainForm)

**File:** `Gui/MainForm.cs`

Single WinForms form. Dark theme (`RGB(16-22, 18-28, 28-40)` backgrounds, Consolas/Segoe UI fonts).

### Layout

```
MainForm
├── Header bar           — title + Start/Stop buttons
├── SplitContainer
│   ├── Left panel       — config fields (read-only display + Queue tab)
│   └── Right panel (TabControl)
│       ├── Loot tab     — owner-draw ListBox of LootEntry, rarity-colored rows
│       ├── Summary tab  — owner-draw ListView: item name (rarity color) + count
│       └── Queue tab    — edit DungeonQueue entries
└── Log panel            — owner-draw ListBox of (Color, string) tuples
```

### Timers

| Timer | Interval | Purpose |
|---|---|---|
| `_botTimer` | 50 ms | Calls `BHBot.Tick()` (pumps SFS2X events) |
| `_uiTimer` | 500 ms | Calls `RefreshStats()` — updates all labels and drains log queue |

### Loot tab coloring

`DrawLootItem` computes the highest-rarity item in the `LootEntry` using `RarityColorLookup.GetRank()`, then draws the summary line in that rarity's ARGB color. If no rarity data is available it falls back to:
- Level-up row → green
- Named item row → gold
- Gold-only row → yellow-gray
- Loss row → red

### Summary tab coloring

`DrawSubItem` reads `(int type, string? rarity)` from `ListViewItem.Tag`. The item name text in column 0 uses `RarityColorLookup.GetArgb(rarity)`; the small type-chip on the left uses `ItemTypeColor(type)` so type and rarity are both visible at a glance.

### ItemTypeColor

```csharp
private static Color ItemTypeColor(int type) => type switch
{
    1  => RGB(220, 170, 40),   // Equipment  — gold
    2  => RGB(100, 190, 100),  // Material   — green
    4  => RGB(60,  200, 220),  // Consumable — cyan
    6  => RGB(180, 80,  220),  // Familiar   — purple
    8  => RGB(200, 130, 60),   // Mount      — orange
    9  => RGB(80,  160, 220),  // Rune       — blue
    11 => RGB(220, 80,  200),  // Enchant    — pink
    15 => RGB(200, 220, 60),   // Augment    — yellow-green
    _  => RGB(130, 130, 130),  // Unknown    — gray
};
```

---

## 9. Item Name Resolution

Item names are resolved via a two-source pipeline:

### Source 1 — Language XML (from Unity bundle cache)

`BHBot.LoadLanguageStrings()` runs at startup. It looks for the Unity AssetBundle cache at:

```
%LocalLow%\Unity\Ultrabit_Bit Heroes\xml\{hash}\__data
```

The `__data` file is a UnityFS bundle (format v8, LZ4-compressed blocks). The bot decompresses it and extracts the English language XML (`<data>` document containing `language_english">English<`). The XML maps localization key → English string and is loaded into `LanguageLookup`.

A cached copy is saved to `lang_cache.xml` next to the exe for faster subsequent starts.

### Source 2 — xml0 item books (from server)

`ParseItemXmlBooks()` runs when the `UserDALC LOAD_XMLS` response arrives. It iterates the `xml0` SFSArray and processes known book files:

| Book file | Item type |
|---|---|
| `EquipmentBook.xml` | 1 |
| `MaterialBook.xml` | 2 |
| `ConsumableBook.xml` | 4 |
| `FamiliarBook.xml` | 6 |
| `MountBook.xml` | 8 |
| `RuneBook.xml` | 9 |
| `EnchantBook.xml` | 11 |
| `AugmentBook.xml` | 15 |
| `RarityBook.xml` | *(rarity color registry)* |

For each item element in a book, the name is resolved in this priority order:

1. `LanguageLookup.Resolve(nameKey)` — language XML lookup on the `name` attribute
2. PascalCase split of the `icon` attribute filename (e.g. `"OffhandShieldStrongarm"` → `"Offhand Shield Strongarm"`)
3. AssetsSource chain walk — upgrade items inherit name from their base item
4. `CleanItemKey(nameKey)` — strips prefix/suffix noise from the raw localization key

The resolved name is capitalized (`Util.FirstCharToUpper` equivalent) and registered via `ItemNameLookup.Register(id, type, name, rarityLink)`.

Raw XML is also dumped to `xml_dump/{filename}.txt` in the exe directory for inspection.

---

## 10. Rarity Color System

### Data flow

```
xml0 packet arrives
    → ParseItemXmlBooks detects "RarityBook.xml"
    → Parses each <rarity id="N" link="legendary" textColor="FF8800" …/>
    → RarityColorLookup.Register("legendary", "FF8800", 4)

Item books parsed
    → ItemNameLookup.Register(id, type, name, "legendary")

Battle loot received
    → rarity  = ItemNameLookup.ResolveRarity(id, type)   → "legendary"
    → argb    = RarityColorLookup.GetArgb("legendary")   → 0xFFFF8800
    → Logger.Loot($"  [LOOT] {item}", argb)

GUI draws log line with Color.FromArgb((int)argb)
```

### `RarityColorLookup` API

```csharp
// Register from RarityBook.xml during xml0 parse
RarityColorLookup.Register(string link, string hexColor, int rank);

// Resolve during render / logging
uint  argb = RarityColorLookup.GetArgb(string? link);  // 0 = no data
int   rank = RarityColorLookup.GetRank(string? link);  // -1 = no data
```

The rank (from the `id` attribute in RarityBook.xml) is used to find the "best" (highest-rarity) item in a `LootEntry` for row-level coloring:

```csharp
string? bestRarity = entry.Items
    .Where(i => !i.IsCurrency)
    .Select(i => ItemNameLookup.ResolveRarity(i.ItemId, i.ItemType))
    .Where(r => r != null)
    .OrderByDescending(r => RarityColorLookup.GetRank(r))
    .FirstOrDefault();
```

---

## 11. Unity Bundle Extraction

### Format

The DLC `xml` bundle is a **UnityFS v8** file (magic `"UnityFS\0"`). Key header fields (all big-endian):

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | Magic `"UnityFS\0"` |
| 8 | 4 | Format version (uint32) |
| 12 | var | Engine version string (null-terminated, `"5.x.x"`) |
| var | var | Unity version string (null-terminated, e.g. `"2022.3.62f2"`) |
| var | 8 | Total file size (uint64) |
| var | 4 | Compressed blocks-info size |
| var | 4 | Uncompressed blocks-info size |
| var | 4 | Flags |

**Flags:**
- Bit 0–5: compression type for blocks-info (0=none, 2/3=LZ4)
- Bit 6 (`0x40`): blocks-info stored at end of file (not immediately after header)
- Bit 7 (`0x80`): data section padded to 16-byte boundary after header

### Blocks-info structure (after decompression)

```
Hash128  (16 bytes, ignored)
blockCount (uint32 BE)
per block:
  uncompressedSize (uint32 BE)
  compressedSize   (uint32 BE)
  blockFlags       (uint16 BE)   bits 0-5 = compression type
```

### LZ4 block decompression

The bot implements a raw LZ4 block decompressor in `DecompressLz4Into(ReadOnlySpan<byte> src, Span<byte> dst)`. This is the standard LZ4 sequence format: token byte → literals → (offset + match) pairs. No frame header.

### English XML identification

After decompressing all blocks into a flat byte array, `FindEnglishLanguageXml()` scans for `<data xmlns:xsd=` start tags and checks that the document contains the marker `language_english">English<` before accepting it.

### Methods involved

| Method | Purpose |
|---|---|
| `LoadLanguageStrings()` | Entry point — finds cache dir, decompresses, parses |
| `ExtractEnglishXmlFromBundleCache(localLow)` | Walks `xml\*\__data` files |
| `DecompressUnityFsBundle(path)` | Full UnityFS reader → raw byte[] |
| `DecompressLz4Into(src, dst)` | LZ4 block decompressor |
| `FindEnglishLanguageXml(data)` | Scans decompressed blob for English doc |
| `ParseLanguageXml(bytes)` | Parses XML → `LanguageLookup.Register()` |

---

## 12. Session Statistics

**Class:** `SessionStats` in `Models/Models.cs`

All numeric fields are `volatile` — safe to read from the UI thread without locking. The item breakdown dictionary and recent loot queue use explicit locks.

### Counters

| Property | Type | Notes |
|---|---|---|
| `TotalRuns` | int | Full dungeon runs (all waves complete) |
| `Wins` / `Losses` | int | Run outcomes |
| `EncountersWon` / `EncountersLost` | int | Individual battle outcomes |
| `TotalItems` | int | Non-currency items looted |
| `GoldGained` / `ExpGained` | long | Session totals |
| `Level` | int | Current character level (updated from server) |
| `DailiesClaimed` | int | Daily rewards claimed this session |

### Currency

| Property | Notes |
|---|---|
| `Gold` / `Credits` / `Shards` / `Energy` / `Tickets` | Live values from server |
| `NextEnergyAt` / `NextTicketAt` | `DateTime` of next regen tick |
| `HighestZone` | Highest zone unlocked (from `cha94`) |

### Item breakdown

`GetItemBreakdown()` returns a list of `(name, type, qty, rarity)` sorted by qty descending. Populated by `RecordItemDrop()` which is called from `RecordEncounter()`.

### Recent loot feed

`RecentLoot` — a `Queue<LootEntry>` capped at 50 entries. Read by the GUI loot list. Thread-safe (lock on `_lootLock`).

---

## 13. Adding New DALC Handlers

1. **Identify the DALC ID** (`dal0`) and action (`act0`) from the server response. Use `verboseMode: true` in config to log raw packets.

2. **Add a branch in `OnExtensionResponse`:**

```csharp
case DALC_YOURDALC:
    HandleYourDalc(r);
    break;
```

3. **Implement the handler:**

```csharp
static void HandleYourDalc(ISFSObject r)
{
    int act = r.GetInt("act0");
    switch (act)
    {
        case 1: // YOUR_ACTION_NAME
            // read fields from r
            // update state / SessionStats
            // call Logger.Info(...)
            break;
    }
}
```

4. **Add a send method if needed:**

```csharp
static void SendYourAction(int param)
{
    var o = NewPacket(DALC_YOURDALC, YOUR_ACTION_CONST);
    o.PutInt("field0", param);
    Send(o);
}
```

`NewPacket(dal, act)` creates an `SFSObject` with the standard envelope fields pre-filled.

---

## 14. Known Limitations

| Area | Detail |
|---|---|
| `autoClaimDailyQuests` | Action numbers unverified — leave disabled |
| Reconnect | No automatic reconnect after disconnect; restart the bot |
| Multiple accounts | Not supported — one SFS2X connection per process |
| Non-Steam login | Email/password auth path exists in the protocol but is not wired to the UI or config |
| Dungeon type | Only standard zone dungeons (GameDALC ENTER_ZONE_NODE). Raids, GvG, Invasions, Gauntlets, Rifts, Brawls, Fishing not automated |
| Item name coverage | Names without a language key and without a usable icon filename fall back to `TypeLabel(id=N)` |
| Bundle cache freshness | Language strings are cached to `lang_cache.xml`. If the game updates, delete the cache to force re-extraction |
