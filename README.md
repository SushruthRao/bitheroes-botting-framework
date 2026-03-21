# BitHeroesBot

[![Star on GitHub](https://img.shields.io/github/stars/SushruthRao/bitheroes-botting-framework?style=social)](https://github.com/SushruthRao/bitheroes-botting-framework)

A clientless automation bot for **Bit Heroes** (Steam). BitHeroesBot connects directly to the game's SmartFoxServer 2X backend, runs dungeon loops automatically, and tracks your loot — all without opening the game client.

---

## Features

- Clientless — runs entirely without the game window open
- Dungeon queue — rotate through multiple zones/nodes automatically
- Auto-battle — sends AUTO each wave; calls RESULTS when combat is done
- Rarity-colored loot log — item names match in-game rarity colors (common → legendary → mythic)
- Live session stats — runs, wins, gold, exp, items, energy/ticket timers
- Daily reward auto-claim at session start
- Auto-assign teammates from online friends and guild members
- Orphaned dungeon recovery — abandons or reconnects stale sessions
- Per-run loot history and full-session item breakdown tab
- All config in a single `config.json` — no GUI required to configure

---

## Requirements

| Requirement | Version |
|---|---|
| Windows | 10 or later (64-bit) |
| .NET | 8.0 SDK |
| Bit Heroes | Installed via Steam (latest) |
| Steam | Running and logged in |

> Steam must be running when Bot starts — it uses the Steam auth ticket for login.

---

## Setup

### 1. Clone / download

```
git clone https://github.com/SushruthRao/bitheroes-botting-framework.git
cd bitheroes-botting-framework
```

### 2. Verify game DLLs are present

The project references two DLLs from your Bit Heroes installation:

```
C:\Program Files (x86)\Steam\steamapps\common\Bit Heroes\Bit Heroes_Data\Managed\SmartFox2X.dll
C:\Program Files (x86)\Steam\steamapps\common\Bit Heroes\Bit Heroes_Data\Managed\com.rlabrecque.steamworks.net.dll
```

If Bit Heroes is installed to a non-default path, update the `<HintPath>` entries in `BitHeroesClient.csproj`.

### 3. Build

```
dotnet build -c Release
```

The output lands in `bin\Release\net8.0-windows\`.

### 4. Configure

Copy or edit `config.json` next to the executable. A default config is written automatically on first run if none exists.

At minimum, set the dungeon(s) you want to farm:

```json
{
  "dungeonQueue": [
    { "zoneId": 5, "nodeId": 3, "difficultyId": 1, "maxRuns": -1 }
  ]
}
```

### 5. Run

```
BitHeroesClient.exe
```

Or from the build directory:

```
dotnet run
```

Make sure Steam is running before you launch.

---

## Configuration Reference

`config.json` uses camelCase JSON. All fields are optional — missing fields fall back to defaults.

### `connection`

| Field | Default | Description |
|---|---|---|
| `host` | `f123.bitheroesgame.com` | Game server hostname |
| `port` | `9933` | SmartFoxServer port |

You should never need to change these.

### `automation`

| Field | Default | Description |
|---|---|---|
| `autoClaimDailyReward` | `false` | Claim daily login reward at session start |
| `autoClaimDailyQuests` | `false` | Loot completed daily quests (experimental — keep false) |
| `autoDeclineCaptures` | `true` | Auto-decline familiar capture prompts in battle |
| `autoAssignTeammates` | `true` | Pick the strongest online friends/guildmates automatically |
| `abandonOrphanedDungeon` | `false` | Cancel leftover dungeon sessions on login |
| `tutorialHandling` | `"Warn"` | What to do if account is in tutorial: `"Warn"` / `"Skip"` / `"Stop"` |
| `energyWaitMinutes` | `10` | Minutes between energy checks when out of energy |
| `maxRetries` | `3` | Retry attempts per action before idling |
| `retryDelayMs` | `5000` | Milliseconds between retries |

### `dungeonQueue`

A list of dungeon entries run in order. When the last entry finishes its `maxRuns`, the queue wraps back to the first.

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Include this entry in the rotation |
| `zoneId` | `1` | Zone number (matches in-game zone list) |
| `nodeId` | `1` | Node/room within the zone |
| `difficultyId` | `0` | `0` = Normal, `1` = Hard, `2` = Heroic |
| `useDamageGain` | `true` | Activate the damage-gain battle bonus |
| `repeatDelayMs` | `3000` | Wait between consecutive runs (milliseconds) |
| `maxRuns` | `-1` | Runs before moving to next entry; `-1` = unlimited |
| `teammates` | `[]` | Fixed teammate list (overrides auto-assign for this entry) |

**Teammate object:**

| Field | Default | Description |
|---|---|---|
| `id` | — | Familiar or player character ID |
| `type` | `1` | `1` = player/familiar, `2` = guild familiar |
| `armoryId` | `-1` | Armory loadout override; `-1` = use default |

**Example — two-zone rotation:**

```json
"dungeonQueue": [
  { "zoneId": 8, "nodeId": 2, "difficultyId": 2, "maxRuns": 20 },
  { "zoneId": 3, "nodeId": 1, "difficultyId": 1, "maxRuns": -1 }
]
```

### `gui`

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Show the WinForms GUI window |
| `refreshRateMs` | `500` | How often the stats panel refreshes |
| `maxLogLines` | `15` | Activity log line buffer (GUI only) |

### `logging`

| Field | Default | Description |
|---|---|---|
| `logToFile` | `true` | Write log output to a file |
| `logFile` | `"bot.log"` | Log file path (relative to exe) |
| `verboseMode` | `false` | Emit debug-level packet traces (very noisy) |

---

## GUI Overview

The GUI has three tabs on the right panel:

| Tab | Contents |
|---|---|
| **Loot** | Per-encounter loot feed — item names colored by rarity |
| **Summary** | Full-session item breakdown (name, count, rarity color) |
| **Queue** | Edit the dungeon queue without restarting |

The left panel shows live counters: state, zone/node, wave, runs, encounters, gold, exp, energy, tickets, level, and the active team.

The activity log at the bottom streams all bot events. Loot lines are colored to match the game's rarity palette — common items in their usual gray, legendaries in orange, mythics brighter, etc.

---

## Loot Color Reference

Colors come directly from `RarityBook.xml` (parsed from the game server's xml0 packet), so they match the game exactly. Approximate mapping:

| Rarity | Color |
|---|---|
| Generic | Gray |
| Common | White/light gray |
| Rare | Blue |
| Epic | Purple |
| Legendary | Orange/gold |
| Set | Teal/green |
| Mythic | Bright pink/red |
| Ancient | Deep gold |

---

## Troubleshooting

**Steam auth fails / bot disconnects immediately**
Steam must be running and logged into the same account that owns Bit Heroes. The bot calls `SteamAPI.Init()` at startup — check the log for `[STEAM]` lines.

**"Zone locked" error**
The selected zone/node is not yet unlocked on your character. Use a lower zone or complete the required content in-game first.

**Item names show as `Equipment(id=…)` instead of real names**
The bot pulls item names from two sources: the server's `xml0` packet (sent after login) and the local Unity bundle cache. If both are unavailable, it falls back to type+id. Wait for a successful login cycle — names populate automatically.

**Language strings not loading**
Bot looks for the DLC bundle cache at `%LocalLow%\Unity\Ultrabit_Bit Heroes\xml\`. Play the game at least once so Steam downloads and caches the bundles.

**`autoClaimDailyQuests` causes disconnect**
Leave this `false`. The action numbers for the daily quest flow are unverified and sending the wrong packet causes the server to disconnect.
