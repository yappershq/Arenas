# Arenas (ModSharp) — Port Plan & Design

Authoritative design doc. Migrates **KitsuneLab-Development/K4-Arenas** (~3.9k LOC CounterStrikeSharp) +
**Letaryat/K4-Arenas-Special-Rounds** (~950 LOC) into ONE ModSharp plugin at upstream-grade quality (house
conventions from commit 1, no CSS idioms carried over — the Retakes-HARDENING mistakes are pre-corrected here).
**Public repo:** yappershq/Arenas.

## What K4-Arenas actually is (verified from source)
A **1v1 (up to NvN) arena ladder**. All players sit in a ranked queue (`WaitingArenaPlayers`). Each round, teams
are assigned to physical arenas (spawn-point clusters) **top-down by rank**; the top arena is the "best". On
round end, per-arena **winners move up, losers move down** the ladder (re-queued via a winners/losers merge in
`round_prestart`). Each arena gets a **round type** (weapon set) chosen from the intersection of both teams'
enabled round-type preferences. AFK players drop to spectator/queue. Optional **!challenge** duels pair two
players directly. **The only persisted data is per-player weapon/round PREFERENCES** — there is NO elo / points /
leaderboard. "Rank" is purely the live queue position, reset each map.

## Key porting decisions (house idioms, not CSS)
| K4 (CSS) | Arenas (ModSharp) |
|---|---|
| `css_queue/guns/rounds/afk/challenge/...` via `AddCommand("css_"+name)` | **bare** names via `ICommandCenter.GetRegistry("arenas").RegisterClientCommand(name, …)` |
| `IStringLocalizer` + `lang/en.json` | **`ILocalizerManager`** + `.assets/locales/arenas.json` (key-first per-culture, `{{double-brace}}` colors) from commit 1 |
| KitsuneMenu scrollable center menu / ChatMenu | house **`IMenuManager`** cached nested `SubMenu` tree with title/item factories (NO `.Next()` chaining) |
| `RegisterEventHandler<EventXxx>` | `IEventManager.HookEvent(name)` + `IEventListener`, or typed `IHookManager` forwards (PlayerSpawn/Death/Team-join) |
| `EventPlayerTeam` reactive AFK / team gating | **`IHookManager.HandleCommandJoinTeam`** pre-hook (gates before engine acts) |
| Health/Armor add-back for cross-arena damage block + headshot-only | **`IHookManager.PlayerDispatchTraceAttack` pre-hook** → return `SkipCallReturnOverride` to zero the damage (NEVER a Health write — `feedback_no_direct_health`) |
| Dapper + MySQL `k4-arenas` prefs table | **`IClientPreference` cookies** (prefs are light + non-relational; keeps ORM out entirely, no DB creds to ship). SteamID64 keys — the ONLY cross-map state. |
| `Dictionary<CCSPlayerController,…>` / find-by-controller | **PlayerSlot-indexed arrays `[64]`** for ephemeral state, cleared on disconnect; re-resolve `IGameClient` by slot each callback |
| `CreateEntityByName` + `DispatchSpawn` for teleport spawns | reuse existing map spawns; CYBERSHOKE `info_teleport_destination` path via `EntityManager` enumeration + `SpawnEntitySync` |
| `player.SwitchTeam` / `ChangeTeam` / `Respawn` / `RemoveWeapons` / `GiveNamedItem` | ModSharp `IPlayerController.SwitchTeam/ChangeTeam/Respawn`, `pawn.GiveNamedItem`, weapon strip via item-service (verify API each use) |
| `pawn.Slay` implied by Respawn cycling | `pawn.Slay()` never `AcceptInput("Kill")` |
| `PluginCapability<IK4ArenaSharedApi>` | publish **`IArenasShared`** in PostInit; consumers resolve in OAM |

## Target solution structure (mirror Retakes / mmosystem)
```
Arenas.slnx
Directory.Build.props        # net10.0; .build/modules|shared; ModSharp.Sharp.Shared compile-only
.gitignore
Arenas.Shared/              # PUBLIC contract — IArenasShared, WeaponType, ArenaRoundCallback. NO ORM/3rd-party.
Arenas.Core/               # IModSharpModule entry + all internal modules
Arenas.SpecialRounds/      # SEPARATE module plugin: registers HeadshotOnly/Nades/NoCrosshair/OneTap round types
.assets/locales/arenas.json + configs/
docs/
```
No `Arenas.Database` — prefs live in cookies. (If a future stats feature is added it gets its own `.Database`.)

## Arenas.Shared (public contract) — DONE (Phase A)
- `enum WeaponType { Rifle, Sniper, Smg, Lmg, Shotgun, Pistol, Unknown }`
- `delegate void ArenaRoundCallback(SteamID[] team1, SteamID[] team2)`
- `interface IArenasShared` (Identity const) — the ModSharp translation of `IK4ArenaSharedApi`:
  `RegisterRoundType(name, teamSize, enabledByDefault, onStart, onEnd) → int` / `UnregisterRoundType(id)` /
  `GetArenaPlacement(steamId)` / `GetArenaName` / `IsAfk` / `FindOpponents` / `SetAfk` /
  `TerminateRoundIfPossible` / `GetPlayerWeaponPreference(steamId, type) → string? classname`.
  Players addressed by `SteamID`; weapon prefs returned as classname strings (no engine enum in .Shared).

## Arenas.Core modules (house `IModule`: Init / OnPostInit / OnAllSharpModulesLoaded / Shutdown)
1. **ConfigModule** — load `arenas.json` config → typed config (round-type list, command lists, compatibility
   toggles, allowed-weapon-prefs, default weapons). Hot-reload-safe.
2. **PlayerLifecycleModule** — connect/activate/disconnect via client lifecycle + `player_activate`/
   `player_disconnect`. Loads prefs from cookies on connect; PlayerSlot arrays cleared on disconnect.
   Publishes AFK state. Re-resolve clients by slot.
3. **ArenaManagerModule** (ArenaFinder + ArenaSet) — on map load, enumerate `info_player_terrorist` /
   `info_player_counterterrorist` (+ CYBERSHOKE `info_teleport_destination` compat), cluster spawn pairs into
   arenas (union-find enemy-pairing / centroid merge — port the algorithm verbatim). Owns arena list; supplies
   the placement/name/opponents resolvers to `ArenasApi`.
4. **QueueModule** (ranked ladder) — `WaitingArenaPlayers` order; the `round_prestart` winners-up/losers-down
   ladder rebuild; AFK re-queue; challenge insertion; team-size round-type packing (`AddTeamsToArena`);
   warmup populate. **PlayerSlot-keyed active/queue; bots have SteamID64=0 so NEVER key ephemeral sets by
   SteamID64.**
5. **RoundFlowModule** — hook `round_prestart` (pre: ladder rebuild + arena assignment), `round_start`,
   `round_end` (compute per-arena results), `round_freezeend`, `player_spawn` (teleport + weapon setup),
   `player_death` (deferred `TerminateRoundIfPossible`). Selects each arena's round type; invokes special-round
   start/end callbacks registered via `IArenasShared`. Suppress `round_mvp`, block draws (`PreventDrawRounds`).
6. **LoadoutModule** — per-arena weapon setup from round type: strip weapons, give knife (config), primary
   (fixed classname OR preferred-by-`WeaponType`), secondary, armor/helmet via item-service. Uses cookie prefs.
7. **CommandsModule** — bare commands via CommandCenter: `queue`, `guns/gunpref/weaponpref`, `rounds/roundpref`,
   `afk`, `challenge/duel`, `caccept/capprove`, `cdecline/cdeny`. Menus via `IMenuManager`.
8. **MenuModule** (or folded into CommandsModule) — round-preference toggle menu + weapon-preference (category →
   item) menu, cached nested SubMenu. Saves to cookies on change.
9. **ChallengeModule** — `!challenge <target>` duel offers (ITargetManager for target resolve), accept/decline,
   bot auto-accept, arena-slot -2 injection at `round_prestart`.
10. **CompatibilityModule** — cross-arena flash block (`player_blind` → clear `BlindUntilTime`) + cross-arena
    damage block (via the `PlayerDispatchTraceAttack` pre-hook, comparing attacker/victim arena placement) +
    optional force-clantags. All gated by config toggles (default off, matching source).
11. **ApiModule** — wires all resolver delegates into `ArenasApi` and is the publish point (already published in
    `ArenasPlugin.PostInit`); exposes registered special round types to the RoundFlow selection.

## Arenas.SpecialRounds (separate module plugin)
ONE plugin registering N round types via `IArenasShared.RegisterRoundType`, sharing one arena-placement-keyed
state store (`Dictionary<int arenaId, RoundState>`). Consumes `IArenasShared` in OAM; unregisters in Shutdown.
Round types (collapsing the 7 copy-paste CSS plugins):
- **HeadshotOnly** (param: weapon classname — covers OnlyHS-AK47 / Scout / USP) — give knife+weapon; a
  `PlayerDispatchTraceAttack` pre-hook zeroes any non-headshot damage between the arena's two teams. **Pre-damage
  interception, NOT the source's post-hoc Health add-back.** Verify hitgroup field on the hook params.
- **NadesOnly** — give knife+HE; on `grenade_thrown` (or the throw forward) re-give an HE to the thrower.
- **NoCrosshair** — give knife+ak; set the crosshair `HideHUD` bit via schema setter that networks state; reset
  on round end.
- **OneTap** — give knife+ak, clip=1; on `weapon_fire` (or fire forward) top up the same-arena opponent's clip
  to 1. Uses `GetArenaPlacement` to pair within the correct arena. (OneTapTimer is BROKEN upstream — skip; its
  countdown intent can be a later enhancement.)
All strings via localizer (source shipped zero locale files + some Polish — invent clean keys).

## Verified ModSharp APIs (mcp-confirmed; verify the rest per-use)
- Damage intercept: `IHookManager.PlayerDispatchTraceAttack.Register(cb, EHookMode.Pre)` → `EHookAction`;
  `args.Entity` = victim pawn, `args.Info` = TakeDamageInfo (attacker/damage/type; confirm hitgroup); return
  `EHookAction.SkipCallReturnOverride` to zero damage, `Ignored` to allow.
- Weapon give + tweak: `pawn.GiveNamedItem("weapon_ak47")`; next frame `InvokeFrameAction` →
  `pawn.FindWeapon("weapon_ak47")?.Clip1 = 1`.
- Give/armor/helmet/money/teleport/team/respawn/menus/cookies/events per skill `references/api-cookbook.md`.

## Config (arenas.json — port keys 1:1, drop DB block)
command lists (queue/guns/rounds/afk/challenge/caccept/cdecline), `center-menu-mode`, `center-announce-mode`,
`freeze-in-center-menu`, compatibility (`give-knife-by-default`, `disable-clantags`, `prevent-draw-rounds`,
`block-damage-of-not-opponent`, `block-flash-of-not-opponent`, `force-arena-clantags`), allowed-weapon-prefs
(per category bool), default-weapon-settings (per category default classname + default-round), round-settings
(the 12 built-in round types: rifle/sniper/shotgun/pistol/scout/awp/deagle/smg/lmg/knife + 2v2/3v3). NO
database-settings (cookies replace it).

## Anti-pattern checklist (MUST pass review — the Retakes misses, pre-baked)
- [ ] PlayerSlot-indexed `[64]` for ephemeral state, cleared on disconnect. SteamID64 only for cookie prefs.
- [ ] `HookEvent("name")` for EVERY handled event (not `InstallEventListener` alone).
- [ ] Bare command names via CommandCenter — no `css_`.
- [ ] All user text via `ILocalizerManager`, `{{double-brace}}` colors — zero hardcoded chat/HUD strings.
- [ ] Damage via `PlayerDispatchTraceAttack` hook, never Health/Armor writes.
- [ ] `pawn.Slay()` not `AcceptInput("Kill")`.
- [ ] `HandleCommandJoinTeam` pre-hook for team gating (not reactive `player_team`).
- [ ] Menus: cached nested SubMenu, no `.Next()`.
- [ ] No ORM/3rd-party types in Arenas.Shared.
- [ ] Special rounds = separate module; Core integration-agnostic (registers via IArenasShared).
- [ ] Re-resolve IGameClient by slot each callback; never store client/pawn/entity across callbacks/rounds.
- [ ] Publishers PostInit, consumers OAM.

## Phase plan (build GREEN + commit each)
- **A. Scaffold** ✅ — done, committed.
- **B. Core spine** — Config, PlayerLifecycle+cookies, ArenaManager (finder/clustering), Queue (ladder),
  RoundFlow, Loadout. Green.
- **C. Commands + menus + challenge + compatibility** — CommandCenter commands, IMenuManager pref menus,
  challenge duels, cross-arena damage/flash block, force-clantags. Green.
- **D. SpecialRounds module** — Arenas.SpecialRounds project; HeadshotOnly/Nades/NoCrosshair/OneTap. Green.
- **E. Self-review** — convention/anti-pattern reviewer + grep sweep; fix; green; commit.
- **F. Publish** — gh repo create yappershq/Arenas --public; house README.

Status in docs/STATUS.md.
