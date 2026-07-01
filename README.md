<div align="center">
  <h1><strong>Arenas</strong></h1>
  <p>Winners-stay 1v1 / NvN arena plugin for CS2 — a from-scratch ModSharp port of K4-Arenas. No CounterStrikeSharp runtime.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/Arenas?style=flat&logo=github" alt="Stars">
</p>

---

**Arenas** discovers spawn points on map load and clusters them into paired arenas, then runs a **winners-stay queue**: every round it drains each arena's result into ranked winner/loser queues, folds in players waiting in queue plus any pending duel challenges, re-pairs everyone across the arenas by their common **round-type** preference, gives each side a loadout, and isolates arenas from each other (no cross-arena damage or flash). Players persist per-weapon and per-round-type **preferences**, can go **AFK**, and can **challenge** each other to a duel. There is **no ELO/points/rank persistence** — the only "ranking" is the transient winners-stay ordering, recomputed each round. Special round-types (Headshot-only, No-crosshair, Nades, One-tap) ship as a separate addon that registers through the public API.

Port of [KitsuneLab-Development/K4-Arenas](https://github.com/KitsuneLab-Development/K4-Arenas) + [Letaryat/K4-Arenas-Special-Rounds](https://github.com/Letaryat/K4-Arenas-Special-Rounds), rebuilt in ModSharp house idioms.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/Arenas.Core/` | `<sharp>/modules/Arenas.Core/` |
| `.build/shared/Arenas.Shared/` | `<sharp>/shared/Arenas.Shared/` |
| `.assets/locales/arenas.json` | `<sharp>/locales/arenas.json` |
| `.assets/configs/arenas.cfg` | `<sharp>/../csgo/cfg/arenas/arenas.cfg` *(auto-created on first run)* |

Optional special rounds:

| From | To |
|------|----|
| `.build/modules/Arenas.SpecialRounds/` | `<sharp>/modules/Arenas.SpecialRounds/` |
| `Arenas.SpecialRounds/.assets/locales/arenas.specialrounds.json` | `<sharp>/locales/arenas.specialrounds.json` |

Restart the server (or change map) to load. Config `arenas.json` is auto-generated in `<sharp>/configs/arenas/` on first run.

## 🧩 Dependencies

Uses the **ModSharp first-party modules** (ship with ModSharp): **CommandCenter** (chat commands), **MenuManager** (preference menus), **LocalizerManager** (all user-facing text), **ClientPreferences** (persist weapon/round prefs per SteamID), **TargetingManager** (resolve challenge target by name). Each is resolved optionally — the plugin degrades gracefully (menus/commands/persistence simply become unavailable) if a module is absent.

External plugins:

| Plugin | Required? | Why |
|--------|-----------|-----|
| **Arenas.SpecialRounds** | ⚪ Optional | Adds the Headshot-only / No-crosshair / Nades / One-tap round types. Registers through `IArenasShared`; the base game works without it. |

**LevelRanks** (if installed) runs alongside Arenas out of the box, awarding its own global points from kills — Arenas needs zero integration.

## ⌨️ Commands

All commands are open (no permission). Chat triggers: `!`, `/`, or `.`; console prefix `ms_`. Aliases are configurable in `arenas.json`.

| Command | Aliases | Description | Permission |
|---------|---------|-------------|------------|
| `!queue` | — | Show your position in the waiting queue. | — |
| `!guns` | `!gunpref`, `!weaponpref` | Open the weapon-preference menu (per category). | — |
| `!rounds` | `!roundpref` | Open the round-type-preference toggle menu. | — |
| `!afk` | — | Toggle AFK (move to spectator, re-queue at the tail). | — |
| `!challenge <name>` | `!duel <name>` | Challenge another player in an arena to a 1v1 duel. | — |
| `!caccept` | `!capprove` | Accept a pending challenge. | — |
| `!cdecline` | `!cdeny` | Decline a pending challenge. | — |
| `!arenas` | — | Print current arena ladder assignments in chat (admin debug). | `admin-permission` |

## ⚙️ Configuration

`configs/arenas/arenas.json` (auto-generated on first run). Key sections:

| Setting | Default | Meaning |
|---------|---------|---------|
| `command-settings.*-commands` | see above | Command name/alias lists. |
| `command-settings.center-menu-mode` | `true` | Prefer center-HUD menus over chat menus. |
| `command-settings.center-announce-mode` | `true` | Show round/arena announcements as center-HUD text (falls back to chat). |
| `command-settings.freeze-in-center-menu` | `true` | Freeze players while a center-HUD preference menu is open. |
| `command-settings.admin-permission` | `"@css/generic"` | AdminManager permission flag required for the `!arenas` admin command. |
| `compatibility-settings.force-arena-clantags` | `false` | Reflect the arena tag (ARENA n / WAITING / AFK / CHALLENGE) into the scoreboard clan tag. |
| `compatibility-settings.disable-clantags` | `false` | Never touch clan tags. |
| `compatibility-settings.block-damage-of-not-opponent` | `false` | Block damage between players in different arenas. |
| `compatibility-settings.block-flash-of-not-opponent` | `false` | Cancel cross-arena flash blinds. |
| `compatibility-settings.give-knife-by-default` | `true` | Give a knife in every loadout. |
| `compatibility-settings.prevent-draw-rounds` | `true` | Force a winner instead of a draw when teams tie. |
| `allowed-weapon-prefs.*` | all `true` | Which weapon categories appear in the guns menu. |
| `default-weapon-settings.default-*` | `null` | Optional fixed weapon per category (overrides preference). |
| `default-weapon-settings.default-round` | `Arenas_Round_Rifle` | Fallback round type when two players share none. |
| `round-settings` | `[]` | Custom round-type list; empty = the 12 built-ins. |

Economy/round scoring (`mp_maxmoney`, `mp_teamcashawards`, `mp_maxrounds`, …) is neutralised automatically via `arenas.cfg` and direct convar sets — arenas drive round ends themselves.

## 🔧 How it works

`ArenaFinder` clusters `info_player_*` spawns into arena pairs by median enemy-distance. Each `round_prestart` the ladder is rebuilt (winners up, losers down), accepted duel challenges are seated first, then the queue is re-paired across arenas by common round-type preference. Cross-arena isolation blocks damage at a `PlayerDispatchTraceAttack` pre-hook and cancels flash at `player_blind`. Preferences persist as `IClientPreference` cookies keyed by SteamID (weapon classnames + enabled round names) — no database required. See [docs/PORT_PLAN.md](docs/PORT_PLAN.md) for the full architecture.

## 🧩 Public API

Other plugins add custom round types + query arena state through `IArenasShared` (resolve in `OnAllModulesLoaded`):

```csharp
var api = sharpModuleManager
    .GetOptionalSharpModuleInterface<IArenasShared>(IArenasShared.Identity)?.Instance;

int id = api.RegisterRoundType("My_Round_Key", teamSize: 1, enabledByDefault: false, OnStart, OnEnd);
// ... api.GetArenaPlacement(steamId), api.FindOpponents(steamId), api.IsAfk(steamId), ...
api.UnregisterRoundType(id); // in Shutdown
```

`Arenas.SpecialRounds` is a reference consumer — see its source for the HeadshotOnly / NoCrosshair / Nades / OneTap implementations.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs:
- `.build/modules/Arenas.Core/Arenas.dll`
- `.build/shared/Arenas.Shared/Arenas.Shared.dll`
- `.build/modules/Arenas.SpecialRounds/Arenas.SpecialRounds.dll`
- `.build/modules/Arenas.Vip/Arenas.Vip.dll`

## 🔗 Integrations

### Ranking — use LevelRank
Arenas ships **no ranking of its own** — pair it with the house **LevelRank** plugin, which persists
points (MySQL) and scores by kills/deaths. On an arena server set the round win/loss scores to **0**
in `levelrank.jsonc` (`RoundWins` / `RoundLosses` → 0): global round win/loss is meaningless when
teams are re-paired every round, whereas per-kill scoring already ranks players correctly. Arenas
does not read LevelRank scores (pairing is a winner-ladder, not skill-seeded), so no wiring is needed
— just run both. Note: if LevelRank's scoreboard module is on, it and Arenas both write `m_iScore`;
prefer disabling LevelRank's scoreboard spoof on arena servers so the arena ladder display wins.

### VIP — optional `Arenas.Vip` module
Core is **VIP-agnostic** (never admin flags): VIP goes through the `IArenasVipProvider` contract,
whose default says nobody is VIP. Deploy the optional **`Arenas.Vip`** module (bridges the house
`Vip.Shared` `IVipShared`) on servers that run the Vip plugin — Core adopts it automatically via the
module registry. VIP-less servers just omit the module.

## 🙏 Credits

Port of [KitsuneLab-Development/K4-Arenas](https://github.com/KitsuneLab-Development/K4-Arenas) and [Letaryat/K4-Arenas-Special-Rounds](https://github.com/Letaryat/K4-Arenas-Special-Rounds). Ranking via [LevelRank](https://github.com/yappershq); VIP via the house Vip plugin.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
