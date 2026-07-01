# K4-Arenas → ModSharp Port Plan

> Target: `yappershq/Arenas` — a from-scratch ModSharp (Source2/CS2) port of the CounterStrikeSharp plugin **K4-Arenas** and its 7 Special-Rounds addons. This is a **faithful behavioural** port in **house idioms**, not a line-by-line translation.

## 1. Overview

K4-Arenas is a 1v1 / 2v2 / NvN **arena** plugin. On map start it analyses the map geometry to discover spawn points and clusters them into paired "arenas", then runs a **winners-stay** queue: every round it drains each arena's result into ranked winner/loser queues (with a winner-ladder interleave), folds in players waiting in queue plus any pending duel challenges, shuffles, and re-pairs everyone across the arenas by their common **round-type** preference (Rifle, Sniper, Pistol, Scout, AWP, Deagle, SMG, LMG, Knife, 2v2, 3v3, plus addon-registered **special rounds**). Each arena gives its two teams a loadout, teleports them to their spawns, isolates them from other arenas (no cross-arena damage/flash), and on death/round-end computes a win/tie/no-opponent result that feeds the next round's pairing. Players get chat/center HUD announcements, per-player weapon + round-type **preferences** persisted per SteamID, an **AFK** system, **duel challenges**, and a public **shared API** that external addon plugins use to register special round-types and query arena placement/opponents/AFK/weapon prefs. Notably there is **no ELO/points/rank persistence** anywhere — the only "ranking" is the transient winners-stay queue ordering recomputed each round.

---

## 2. Target Architecture

### 2.1 Project layout

```
Arenas/
├─ Arenas.Shared/          # contracts only — house types, NO 3rd-party/ORM/CSS types
│   ├─ IArenaShared.cs         # ISharpModuleInterface, Identity const
│   ├─ IArenasVipProvider.cs   # VIP abstraction (default = nobody)
│   ├─ WeaponType.cs           # enum Rifle/Sniper/SMG/LMG/Shotgun/Pistol/Unknown
│   ├─ SpecialRoundDescriptor.cs  # id/name/teamSize/enabledByDefault + start/end delegates
│   └─ ArenaTeamContext.cs     # slot[]-based team rosters handed to special rounds
│
├─ Arenas.Core/            # the plugin: lifecycle, queue engine, arenas, rounds, weapons, menus, commands
│   ├─ ArenasModule.cs         # IModSharpModule entry + DI composition root
│   ├─ ModuleDependencyInjection.cs
│   ├─ InterfaceBridge.cs      # static bridge (managers/services)
│   ├─ Config/                 # ArenasConfig + nested settings records (System.Text.Json)
│   ├─ Lifecycle/              # IGameListener: map + round lifecycle, warmup
│   ├─ Queue/                  # WaitingQueue, PairingEngine, ChallengeService
│   ├─ Arenas/                 # ArenaContainer, Arena, ArenaFinder, ArenaResult
│   ├─ Players/                # ArenaPlayerState (slot-indexed), AfkService
│   ├─ Rounds/                 # RoundTypeRegistry, RoundType, GetCommonRoundType
│   ├─ Weapons/                # CsItem (house), WeaponCatalog, LoadoutService
│   ├─ Menus/                  # cached nested SubMenu trees (guns / rounds)
│   ├─ Commands/               # CommandCenter registrations (bare names)
│   ├─ Api/                    # ArenaSharedApiImpl : IArenaShared
│   └─ Vip/                    # DefaultVipProvider (nobody)
│
├─ Arenas.Database/        # optional relational store — SqlSugar/connection types stay internal
│   ├─ IArenasStore.cs
│   ├─ CookiePrefStore.cs      # DEFAULT: IClientPreference blob per SteamID (recommended)
│   └─ SqlPrefStore.cs         # OPTIONAL: table + lastseen/purge if config points at a real DB
│
└─ .assets/locales/        # key-first per-culture JSON, {{double-brace}} colors (from commit 1)
    ├─ en.json  pl.json  ...

# External, separate repos/plugins — depend ONLY on Arenas.Shared:
Arenas.Vip/               # optional: wires house Vip.Shared.IVipShared → IArenasVipProvider
Arenas.SpecialRounds.OneTap/     Arenas.SpecialRounds.Scout/
Arenas.SpecialRounds.USP/        Arenas.SpecialRounds.AK47/
Arenas.SpecialRounds.NoCrosshair/  Arenas.SpecialRounds.Nades/
Arenas.SpecialRounds.Bots/        # bots addon (bot_quota driver)
```

**Rule:** the 7 special-round modules + the bots/api addons reference **`Arenas.Shared` only** — never `Arenas.Core`. `Arenas.Shared` contains **zero** CSS (`CCSPlayerController`, `CsItem`), zero ORM (SqlSugar/MySqlConnector/Dapper), zero engine (`IBaseEntity`) types — pure house-neutral contracts to avoid single-ALC TypeLoad risk.

### 2.2 DI & lifecycle

- **Entry:** `ArenasModule : IModSharpModule`. Compose a `ServiceCollection` in `ModuleDependencyInjection.AddModules`, build the provider, populate a static `InterfaceBridge` (managers + house services). Services registered: config, `RoundTypeRegistry`, `WeaponCatalog`, `LoadoutService`, `ArenaContainer` factory, `PairingEngine`, `WaitingQueue`, `ChallengeService`, `AfkService`, `ArenaPlayerStateTable`, menu factories, command handlers, `IArenasStore`, `IArenasVipProvider`, `ArenaSharedApiImpl`.
- **Publish (producer):** `RegisterSharpModuleInterface<IArenaShared>(this, IArenaShared.Identity, impl)` in **`OnPostInit`**. Resolve *own* house services (localizer, client-preference, game-rules) that are always present here.
- **Consume (addons):** each special-round module resolves `GetOptionalSharpModuleInterface<IArenaShared>(IArenaShared.Identity)?.Instance` in **`OnAllSharpModulesLoaded`**, caches the wrapper, and registers its round there (degrade gracefully / log-once when Core absent).
- **Shutdown:** unregister special rounds, remove hooks, null out containers.

### 2.3 State ownership: PlayerSlot-transient vs SteamID-persistent

| State | Keyed by | Lifetime | Notes |
|---|---|---|---|
| Weapon preferences (6 slots, `weapon_*` string or "random") | **SteamID64** | **Persistent** (survives map change) | Store in `Arenas.Database` (cookie blob preferred). `null` slot == random, NOT "no weapon". |
| Enabled round-type set | **SteamID64** | **Persistent** | Persist by **stable round key/name**, NOT the runtime auto-increment ID. |
| `ArenaPlayerState`: SpawnPoint (EntityIndex), AFK, PlayerIsSafe, ArenaTag, CenterMessage, MVPs, Loaded | **PlayerSlot** | **Transient** — array reset on disconnect/map change | Never store a controller/pawn; re-resolve each callback. |
| WaitingQueue, Challenges, Team1/Team2 rosters | **PlayerSlot** | **Transient** — torn down `OnMapEnd` | Hold slots (or slot-indexed state objects), evict on disconnect. |
| ArenaContainer / Arena / Spawns (`EntityIndex`) | — | **Transient** — rebuilt per map by `ArenaFinder` | CYBERSHOKE-created spawn entities rebuilt each map. |
| `RoundTypeRegistry` (built-ins + config + special) | — | Process/map — rebuilt per map, addons re-register | IDs reassigned each build → don't persist numeric IDs. |
| gameRules ref, WarmupTimer, IsBetweenRounds | — | Transient | Null-guard `GetGameRules()`; resolve once, null on map end. |

MVPs/scoreboard fields accumulate **within a map only** and are lost on change (native scoreboard spoof, not persisted).

---

## 3. CSS → ModSharp API mapping (aggregated)

| CounterStrikeSharp | ModSharp (house) | Notes |
|---|---|---|
| `BasePlugin` + `IPluginConfig<T>` + `OnConfigParsed` + `[MinimumApiVersion]` | `IModSharpModule` + DI (`ServiceCollection` + `InterfaceBridge`); config via System.Text.Json in Core | Drop MinimumApiVersion; config is not a CSS hook. |
| `Load(bool hotReload)` + re-scan `GetPlayers()` | `Init`/`OnPostInit` + `OnAllSharpModulesLoaded`; re-scan via `IClientManager` | hotReload branch → explicit re-init path. |
| `RegisterListener<OnMapStart/OnMapEnd>` | `IGameListener` map hooks (e.g. `OnServerActivate` + map-end) — **verify member names** | Build/teardown `ArenaContainer` here. |
| `RegisterListener<OnTick>` | **No OnTick.** `IHookManager` `PlayerPreThink`/`PlayerPostThink`, or throttled `PushTimer` | Per-tick center print → throttled push; clear at freeze-end. |
| `AddCommandListener("jointeam", …)` | `IHookManager.HandleCommandJoinTeam.InstallHookPre(...)` → `HookReturnValue<bool>` | Replaces jointeam listener **and** most `EventPlayerTeam(Pre)` AFK logic. |
| `RegisterEventHandler<EventX>` / `HookMode.Pre` | `IEventManager.HookEvent("event_name")` + `IEventListener`; block via `HookFireEvent`→false / `.Silent` | For events whose **data you read**. |
| `EventRoundPrestart / RoundStart / RoundEnd` (field-less flow) | `IGameListener.OnRoundRestart` (prestart re-pairing) / `OnRoundRestarted`; `HookEvent("round_end")` for the field-reading result compute | House convention: field-less lifecycle → IGameListener. |
| `EventPlayerHurt(Pre)` adding Health/Armor back (cross-arena) | Damage pre-hook (`OnTakeDamage`/`DispatchTraceAttack`) → **block** the damage | **Anti-pattern:** never write Health back. |
| `EventPlayerBlind(Post)` writing `BlindUntilTime` | flash pre-hook zeroing blind duration via `SetNetVar` | Keep cross-arena check; no stored-pointer write. |
| `EventRoundMvp(Pre)` → `HookResult.Handled` | `HookEvent("round_mvp")` → `HookFireEvent` false / `.Silent` | Suppress MVP broadcast. |
| `Utilities.GetPlayers().Where(valid && !bot && !hltv)` | `IClientManager` iteration; filter `IsFakeClient`/`IsHltv`/state | Centralize one valid-humans helper (slot-indexed). |
| Stored `CCSPlayerController` on `ArenaPlayer` | Store **PlayerSlot**; re-resolve via `IClientManager` each callback | Source dangling-pointer bug — do not replicate. |
| `player.PlayerPawn.Value` | `controller.GetPlayerPawn()` null-guarded; store `EntityIndex` | Never cache pawn. |
| `FindAllEntitiesByDesignerName<SpawnPoint>` | `IEntityManager.FindEntityByClassname` loop; `CInfoPlayerCounterterrorist`/`CInfoPlayerTerrorist` | Store `EntityIndex`; rebuild per map. |
| `CreateEntityByName<>` + `DispatchSpawn` + `Teleport` | `IEntityManager.CreateEntityByName` + `SpawnEntitySync` with EKV (`.ToEKVString()` for origin/angles), `DispatchSpawn` once | CYBERSHOKE path; don't double-dispatch if origin is in KV. |
| `entity.AcceptInput("SetDisabled")` / `entity.Remove()` | `entity.AcceptInput(...)` / EntityManager remove | Verify signatures. |
| `SetStateChanged(ent, class, "m_szClan"/"m_iScore"/"m_iMVPs")` | schema setter (auto NetworkStateChanged) or `SetNetVar("m_...")` | Clan tag + scoreboard-ordering spoof. |
| `AddTimer(secs, cb, REPEAT\|STOP_ON_MAPCHANGE)` | `PushTimer(cb, secs, GameTimerFlags.StopOnMapEnd \| StopOnRoundEnd \| Repeat)` | warmup populate, clantag loop, delayed respawn/announce/terminate. |
| `Server.NextWorldUpdate(cb)` | `ModSharp.InvokeFrameAction(cb)` | defer armor write, special-round StartFunction, TerminateRound. |
| `Server.ExecuteCommand("mp_*", cvars)` | `IConVarManager.FindConVar(name)?.Set(v)` | `CheckCommonProblems` batch → typed cvar sets. |
| `Server.ExecuteCommand("mp_restartgame"/"mp_warmup_end")` | engine server-command exec API — **verify** | Real commands, not cvars. |
| `gameRules.WarmupPeriod / WarmupPeriodEnd`; `ConVar.Find("mp_warmuptime")` | `GetGameRules()` → `IGameRules.IsWarmupPeriod` (verified); WarmupPeriodEnd via schema; `FindConVar("mp_warmuptime")` | Null-guard game rules. |
| `gameRules.TerminateRound(delay, reason)` | `IGameRules.TerminateRound(float delay, RoundEndReason reason, bool bypassHook=false, TeamRewardInfo[]? info=null)` — verified | `RoundEndReason` enum exists (verify CTsWin/TerroristsWin/RoundDraw). |
| `controller.SwitchTeam / ChangeTeam / Respawn` | `IPlayerController.SwitchTeam(CStrikeTeam)` (keeps score) / `ChangeTeam` (resets) / `Respawn()` — verified | Slay pawn (`AcceptInput("Kill")`) **before** team move. `CsTeam`→`CStrikeTeam`. |
| `GiveNamedItem(CsItem)` / `RemoveWeapons()` | `pawn.GiveNamedItem("weapon_ak47")`; `pawn.RemoveAllItems(removeSuit:true)` | Give on **pawn** not controller. No `CsItem`/`RemoveWeapons` in MS. |
| `pawn.ItemServices.HasHelmet`; `pawn.ArmorValue` | `pawn.GetItemService().HasHelmet/HasDefuser`; `pawn.ArmorValue` (set `mp_max_armor 0`) | Null-guard service; deferred a frame. |
| `weapon.Clip1 = n; ReserveAmmo.Fill; SetStateChanged` | `IBaseWeapon.Clip = n; .ReserveAmmo = n` (single ints, -1/-2 sentinels, auto-network) | Set next frame via `InvokeFrameAction`; no `Clip2`/`Fill`. |
| `pawn.HideHUD = 1<<8` | `pawn.SetNetVar("m_iHideHUD", v)` (uint) | Keep the crosshair bit value. |
| `PrintToChat / PrintToCenterHtml / Localizer.ForPlayer` | `ILocalizerManager`: `localizer.For(client).Localized(key,args).Transform(ChatFormat.ProcessColorCodes).Print()`; center via `ProcessColorCodesToHtml`; server-side `localizer.Format(culture,key,args)` | All `k4.*` keys → `.assets/locales` from commit 1, `{{double-brace}}` colors. |
| `ExecuteClientCommand("play sounds/...")` | EmitSound via recipient filter or client command | jointeam-denied feedback. |
| `PluginCapability<T>` + `RegisterPluginCapability` / `Capability.Get()` | `RegisterSharpModuleInterface<T>` in PostInit; `GetOptionalSharpModuleInterface<T>(T.Identity)?.Instance` in OnAllSharpModulesLoaded | Addons: 1:1 to consumer phase. |
| Dapper + MySqlConnector inline in model | `Arenas.Database` (SqlSugar/connection internal) or `IClientPreference` cookie; atomic upsert | Never in `.Shared`. Apply loaded prefs on the **game thread**. |
| `AdminManager.PlayerHasPermissions` | `IAdminManager.GetAdmin((SteamID)id)?.HasPermission(flag)` + `MountAdminManifest` | Commands only. VIP behaviour → `IArenasVipProvider`, never admin flags. |
| `AddCommand("css_name", …)` / `info.ArgByIndex(0).Replace("css_","")` | `ICommandCenter.GetRegistry("arenas").RegisterClientCommand("name", handler)` — **bare** names | Drop all `css_` handling; alias lists stay in config. |
| `CommandInfo` / `ReplyToCommand` / `GetArgTargetResult` | ModSharp command context (`IGameClient`/`ICommandContext`); reply via localizer print; house target resolver | No drop-in for `@all/#slot` — use house target parsing. |
| `ChatMenu` / `ShowScrollableMenu` (Kitsune) | House `IMenuManager` cached nested `SubMenu` tree (title/item factories) | No linear `.Next()` paging. |
| `typeof(CsItem)` EnumMember reflection (`FindEnumValueByEnumMemberValue`, duplicated) | House `CsItem` + name↔enum dictionary (Retakes `CsItemNames`) | Config defaults already `weapon_*` strings → map directly; collapse the two copies. |

---

## 4. Phased build order

### Phase A — Skeleton + DI + config + locale
**Files:** `Arenas.Core/ArenasModule.cs`, `ModuleDependencyInjection.cs`, `InterfaceBridge.cs`, `Config/ArenasConfig.cs` (+ nested `DatabaseSettings`, `CommandSettings`, `DefaultWeaponSettings`, `AllowedWeaponPreferences`, `CompatibilitySettings`, `RoundSettings`), `Arenas.Shared/*` stubs, `.assets/locales/en.json` (+ `pl.json`).
- [ ] `IModSharpModule` entry; DI `ServiceCollection` + static `InterfaceBridge`; `ModuleDependencyInjection.AddModules`.
- [ ] Config as plain records via System.Text.Json (no `BasePluginConfig`, no CSS versioning); keep section keys for drop-in familiarity but no `{{color}}` tokens in config values.
- [ ] `ILocalizerManager` + `.assets/locales` **from commit 1**, key-first per-culture, `{{double-brace}}` colors — migrate **every** `k4.*` key now.
- [ ] `Arenas.Shared`: `IArenaShared` (`ISharpModuleInterface` w/ `static string Identity => typeof(IArenaShared).FullName!`), `WeaponType` enum, `IArenasVipProvider` (default nobody), `SpecialRoundDescriptor`, `ArenaTeamContext` — **house types only**.
- [ ] Register empty `IArenaShared` impl in `OnPostInit`; resolve services in `OnAllSharpModulesLoaded`.

### Phase B — Core arena / queue / round lifecycle
**Files:** `Arenas/ArenaContainer.cs`, `Arena.cs`, `ArenaFinder.cs`, `ArenaResult.cs`, `Queue/WaitingQueue.cs`, `PairingEngine.cs`, `Players/ArenaPlayerState.cs` (+ slot table), `Lifecycle/MapLifecycleListener.cs`, `RoundLifecycleListener.cs`, `Players/AfkService.cs`.
- [ ] `ArenaFinder` runs once per map inside container construction; store spawn `EntityIndex`; CYBERSHOKE `info_teleport_destination` → delete + `SpawnEntitySync` real spawns (EKV origin, single dispatch).
- [ ] Map build/teardown via `IGameListener` map hooks; container `= null` on map end.
- [ ] Re-pairing on `IGameListener.OnRoundRestart` (prestart): drain results → ranked winner/loser queues, **winner-ladder interleave** (keep top-2 winners adjacent, then winner/loser pairs), fold in waiting + challenges, shuffle, split AFK, humans-over-bots, walk arenas 0..N (challenge duels → NvN → 1v1 by common round-type → empty), overflow back to queue + spectator. `OnRoundRestarted` for reset.
- [ ] All transient per-player state **PlayerSlot-indexed**; queues/rosters hold slots; evict on disconnect.
- [ ] Team setup: randomize CT/T cluster, assign spawn, scoreboard spoof via `SetNetVar` (Score/MVPs/Damage), clan tag, `SwitchTeam` vs `ChangeTeam` (slay pawn first), delayed `Respawn`, center/chat announce.
- [ ] Spawn/teleport on `player_spawn` (`HookEvent`, reads fields): teleport pawn, health 100, then special-round StartFunction (deferred via `InvokeFrameAction`) **or** loadout.
- [ ] Cross-arena isolation: **block** damage at damage pre-hook + zero flash at flash hook (delete source's Health/Armor write-back).
- [ ] AFK via `HandleCommandJoinTeam` pre-hook (house idiom) + `.Silent` `player_team` to suppress broadcast — do **not** port `EventPlayerTeam(Pre)` verbatim.
- [ ] `TerminateRoundIfPossible`: alive T vs CT → `IGameRules.TerminateRound`, draw-prevention; trigger on death (1s `PushTimer`), disconnect, activate, team change. Decide deliberately whether to keep the FlashFix warmup workaround.
- [ ] HUD center message via throttled think/`PushTimer`; clear at `round_freeze_end`.
- [ ] `PushTimer(... StopOnMapEnd | StopOnRoundEnd)` for warmup-populate, clantag, delayed respawn/announce/terminate.

### Phase C — Ranking (there is none) + DB persistence
**Files:** `Arenas.Database/IArenasStore.cs`, `CookiePrefStore.cs`, (optional) `SqlPrefStore.cs`.
- [ ] **Do NOT scaffold any ELO/points/rank/score schema — it does not exist.** Only two pref sets persist.
- [ ] Default store = `IClientPreference` cookie blob per SteamID (weapon strings + enabled-round **keys**) — mirror `Retakes.Core/Allocator/WeaponPrefsStore.cs` (sync cached reads, log-once when service absent).
- [ ] Optional `SqlPrefStore` only if the operator points config at a real DB (keep `lastseen`/purge, atomic upsert). SqlSugar/connection types stay **internal to Arenas.Database**.
- [ ] Preserve "DB left at defaults ⇒ persistence off, still functional" gating (`HasDatabase=false`).
- [ ] Load on `player_activate`; **apply loaded prefs on the game thread** (never write state from a worker). Remove on `player_disconnect`. Save eagerly on menu change.
- [ ] Persist round prefs by **stable name/key**, not runtime IDs. Weapon prefs as `weapon_*` strings; `null` slot == random.
- [ ] Resolve the `Loaded`-flag bug deliberately (source only sets `Loaded=true` inside the non-empty-rounds branch → empty-rounds players never save). Decide intended behaviour.

### Phase D — Round-types + weapons
**Files:** `Rounds/RoundType.cs`, `RoundTypeRegistry.cs`, `GetCommonRoundType.cs`, `Weapons/CsItem.cs`, `WeaponCatalog.cs`, `LoadoutService.cs`, `Config/GameConfig` (cvar baseline), `Rounds/ChallengeModel` support.
- [ ] House `CsItem` + `CsItemNames` (reuse Retakes template); no EnumMember reflection.
- [ ] `WeaponCatalog`: six category string lists + `GetWeaponList/GetAllPrimaryWeapons/GetRandomWeapon/GetWeaponType`. Preserve: `Unknown` = random primary+pistol sentinel; primaries exclude pistols; two-slot logic.
- [ ] `LoadoutService.SetupWeapons`: strip (`RemoveAllItems(removeSuit:true)`) → knife → primary (fixed/preferred/random) → secondary → armor/helmet next frame via `InvokeFrameAction`; `mp_max_armor 0`; item-service null-guard.
- [ ] `RoundTypeRegistry`: built-ins via `ResetRoundTypes`, config rounds via `AddRoundType`, deterministic ordering; specials appended by addons.
- [ ] `GetCommonRoundType(prefs1, prefs2, multi)` with config default + random single-team fallback; challenge arenas = `-2` (multi:false), warmup = `-1` (use explicit enum instead of magic ints where practical).
- [ ] cvar baseline via `IConVarManager` (`CheckCommonProblems` batch); `mp_restartgame`/`mp_warmup_end` via server-command API — be aware it fights server cfgs.
- [ ] Give items on the **pawn**, re-resolved each call.

### Phase E — Shared API + commands + menus
**Files:** `Api/ArenaSharedApiImpl.cs`, `Commands/*`, `Menus/GunsMenu.cs`, `RoundsMenu.cs`.
- [ ] Implement `IArenaShared` (see §5); publish in `OnPostInit`.
- [ ] Commands via `CommandCenter.GetRegistry("arenas").RegisterClientCommand("<bare>", …)`; alias lists from config; queue/rounds/guns/afk/challenge/accept/decline. Most commands **open** (no perms) — keep so.
- [ ] Every handler re-resolves `ArenaPlayerState` by slot; no stored controllers. Challenge/AFK/timers all slot-indexed, evicted on disconnect.
- [ ] Consolidate AFK logic into one `AfkService.PerformAfkAction` (source duplicates it in API + command with subtle diffs).
- [ ] Menus → one cached nested `SubMenu` tree (category→weapon; round toggle list) with bool toggle items; keep "at least one round enabled" guard; collapse chat/center menu modes into the house menu.

### Phase F — Special-round modules (external plugins)
**Files (each its own repo/module, Shared-only ref):** `OneTap`, `Scout`, `USP`, `AK47`, `NoCrosshair`, `Nades`, `Bots`.
- [ ] Each = `IModSharpModule`/`IModule`: hook game events in `Init`/`OnPostInit`, resolve `IArenaShared` + register round in `OnAllSharpModulesLoaded`, unregister + remove hooks in `Shutdown`.
- [ ] Register via `AddSpecialRound(descriptor)`; store returned id; `RemoveSpecialRound(id)` on shutdown.
- [ ] **Preserve invocation contract:** StartFunction called **once per player** (dedupe in callback), inside a deferred frame; a StartFunction round **bypasses Core SetupWeapons** → the addon equips everything (knife + weapons). EndFunction once per arena, cleanup only. Both team rosters may be null → guard.
- [ ] OnlyHS trio (AK47/USP/Scout): implement headshot-only via **damage pre-hook zeroing non-head damage** (`HitGroup != Head`) — **never** add Health/Armor back.
- [ ] Weapon ammo: `IBaseWeapon.Clip/.ReserveAmmo` set next frame via `InvokeFrameAction` (give doesn't ready the weapon same frame).
- [ ] `NoCrosshair`: `SetNetVar("m_iHideHUD", 1<<8)`.
- [ ] `Nades`: re-give HE on `grenade_thrown` for tracked players — **not** arena-scoped (preserve the difference; don't "fix").
- [ ] Port **OneTap's finished behaviour**; do **not** copy OneTapTimer (unfinished debug: dup namespace/round-name, Polish `PrintToChatAll` spam, `css_tap`, double-decrement). Do not ship two rounds under name "OneTap".
- [ ] All per-round tracking **PlayerSlot-indexed**, cleared on round end/map change; re-resolve controller/pawn each callback; cancel timers on round end/disconnect.
- [ ] `Bots`: consume `IArenaShared`; typed `IConVar` for `bot_quota`/`bot_quota_mode`/`bot_prefix`; `IGameRules` for warmup/phase; `IClientManager` player/bot split; suppress bot disconnect via event pre-hook suppression flag; `TerminateRoundIfPossible` when a lone bot remains. Drop the `gameconfig.cfg` deletion hack unless a concrete need survives.
- [ ] All user text via `ILocalizerManager` (drop hardcoded/debug prints).

### Phase G — Review
- [ ] Run `/module-review` over Core + each addon; verify against `Sharp.Shared` + `mcp__modsharp__*` for every API touched.
- [ ] Confirm: no CSS/ORM/engine types in `Arenas.Shared`; no stored controllers/pawns; no direct Health/Armor writes; damage/flash blocked at hooks; timers flagged `StopOnMapEnd|StopOnRoundEnd`; menus cached; locales complete; commands bare-named; VIP via provider.
- [ ] Verify `TerminateRound`, `IsWarmupPeriod`, `SwitchTeam/ChangeTeam/Respawn`, `GiveNamedItem`, `RemoveAllItems`, `IBaseWeapon.Clip`, `HandleCommandJoinTeam` signatures against source.

---

## 5. Shared-API contract sketch (`Arenas.Shared`)

House-neutral redesign of `IK4ArenaSharedApi` — **no `CCSPlayerController`, no `CsItem`**. Players are `PlayerSlot` (int) or `SteamID`; items are `weapon_*` strings (or house `CsItem`). `WeaponType` stays.

```csharp
namespace Arenas.Shared;

public enum WeaponType { Rifle, Sniper, SMG, LMG, Shotgun, Pistol, Unknown }

// Rosters handed to special-round callbacks — slots, re-resolved by the addon each callback.
public sealed record ArenaTeamContext(int ArenaId, IReadOnlyList<int> Team1Slots,
                                      IReadOnlyList<int> Team2Slots);

public delegate void SpecialRoundStart(ArenaTeamContext ctx); // once per player; addon dedupes
public delegate void SpecialRoundEnd(ArenaTeamContext ctx);   // once per arena; cleanup only

public sealed record SpecialRoundDescriptor(
    string Name, int TeamSize, bool EnabledByDefault,
    SpecialRoundStart Start, SpecialRoundEnd End);

public interface IArenaShared
{
    static string Identity => typeof(IArenaShared).FullName!;

    // Special-round registry
    int  AddSpecialRound(SpecialRoundDescriptor descriptor); // returns runtime round id
    void RemoveSpecialRound(int id);

    // Queries (slot-based; -1 / "" / empty when player not placed)
    int             GetArenaPlacement(int playerSlot);
    string          GetArenaName(int playerSlot);            // tag w/o trailing " |"
    bool            IsAFK(int playerSlot);
    IReadOnlyList<int> FindOpponents(int playerSlot);
    string?         GetPlayerWeaponPreference(int playerSlot, WeaponType type); // null == unknown/random (not Loaded)

    // Actions
    void TerminateRoundIfPossible();
    void PerformAFKAction(int playerSlot, bool afk);         // toggle AFK, move to spectator, retag
}

public interface IArenasVipProvider   // default impl = nobody; Arenas.Vip wires house IVipShared
{
    bool IsVip(SteamID steamId);
}
```

**Documented behavioural contract for addon authors:**
1. Resolve `IArenaShared` in `OnAllSharpModulesLoaded`; `AddSpecialRound` there; `RemoveSpecialRound` in `Shutdown`.
2. `Start` fires **once per player** in the arena (dedupe with a slot set); it runs deferred one frame; a round with a `Start` **bypasses Core's default loadout** — you must give knife + weapons yourself.
3. `End` fires once per arena after the result is known — cleanup only.
4. Either team list may be empty — guard.
5. **Never store** controllers/pawns across the round; re-resolve from slot each callback. Round ids are **not** stable across reload — persistence uses stable names.

---

## 6. Anti-pattern / CSS-ism watchlist (port-specific)

- **`.Shared` type leaks (top priority):** original `IK4ArenaSharedApi` passes `CCSPlayerController` + `CsItem` across the boundary — forbidden (single-ALC TypeLoad). Re-type to slots/SteamID + weapon strings. This is a deliberate, breaking divergence from a line-port and changes how all 7 addons are written.
- **Stored controllers/pawns:** `ArenaPlayer` caches a raw `CCSPlayerController` for the whole session; addons keep `Dictionary<CCSPlayerController, PlayerInfo>` + stored pawns + CSS Timer handles across rounds. → PlayerSlot-indexed arrays, re-resolve every callback, cancel timers on round end/disconnect.
- **Direct Health/Armor writes:** cross-arena damage cancel (`Health += DmgHealth`) and the OnlyHS trio both add health back in `player_hurt(Pre)`. → block/zero damage at the damage pre-hook; never write Health. Flash: `SetNetVar` in a flash hook, not a stored-pointer `BlindUntilTime` write.
- **`Listeners.OnTick` / `AddTimer`:** no OnTick in ModSharp (per-tick center print for every human is heavy) → throttled think/`PushTimer`; `AddTimer` → `PushTimer` with `StopOnMapEnd|StopOnRoundEnd`.
- **`Server.NextWorldUpdate`** → `InvokeFrameAction`; weapon isn't ready same frame after `GiveNamedItem` → set Clip/ReserveAmmo next frame.
- **`css_` command prefix + `Replace("css_","")`** → CommandCenter bare names; delete the stripping hack.
- **Reactive `EventPlayerTeam(Pre)` for AFK** → `HandleCommandJoinTeam` pre-hook + `.Silent` `player_team`.
- **Runtime round IDs persisted:** `RoundType.nextID` is a static counter reset by `ClearRoundTypes` and reassigned per map/addon-load-order; prefs stored as CSV of these IDs silently remap. → persist by stable name/key.
- **EnumMember reflection (`FindEnumValueByEnumMemberValue`)** duplicated in two files → house `CsItem`/name map, collapse to one.
- **Dapper/MySqlConnector inline in models + background-thread pref writes** → `Arenas.Database` (types internal), apply loaded state on the game thread; cookie route preferred.
- **CSS `ChatMenu`/`ScrollableMenu` linear paging** → cached nested `SubMenu` tree.
- **Give on controller / `RemoveWeapons()`** → give on **pawn**; `RemoveAllItems(removeSuit:true)`.
- **`Utilities.SetStateChanged` string calls** → schema setters auto-network; explicit only for raw `SetNetVar`.
- **Inventing a ranking system:** the "rank-db" label is a misnomer — there is no ELO/points/score persistence. Do not scaffold one.
- **VIP via admin flags:** none exists in the source lifecycle; any VIP/preferred gating goes through `IArenasVipProvider` (default nobody), never `AdminManager` flags. Command perm-gating (where present) is the only legitimate admin use — and most Arenas commands are intentionally open.
- **Magic placement ints** (`-1` warmup, `-2` challenge) → prefer an explicit enum.
- **Deliberate-drop candidates:** FlashFix `mp_warmup_end; mp_restartgame` folklore, `gameconfig.cfg` deletion hack, OneTapTimer entirely, Polish debug prints, `css_tap` — evaluate each explicitly rather than porting by reflex.