# Arenas — Port Status

Phase tracker + resume instructions. Update after every phase.

## Build / verify
```
cd /home/claude/Arenas && env -u version dotnet build -c Release
```
Output: `.build/modules/Arenas.Core/Arenas.dll` + `.build/shared/Arenas.Shared/Arenas.Shared.dll`.

## Phases
- [x] **A. Scaffold** — slnx, Directory.Build.props, .gitignore, Core + Shared (IArenasShared, WeaponType), locale stub. GREEN. Committed.
- [x] **B. Core spine** — Config + ServerConfig (economy-off convars), PlayerLifecycle (+cookie prefs), Arena finder/manager (spawn clustering), Queue (internal self-contained ladder), RoundFlow, Loadout, API resolver wiring. GREEN. Committed. Reviewed.
- [ ] **C. Commands + menus + challenge + compatibility** — CommandCenter bare commands (queue/guns/rounds/afk/challenge/…), IMenuManager pref menus, challenge duels, cross-arena damage/flash block (PlayerDispatchTraceAttack), force-clantags.
- [ ] **D. SpecialRounds module** — Arenas.SpecialRounds (HeadshotOnly/Nades/NoCrosshair/OneTap) via IArenasShared.RegisterRoundType. See docs/PHASE_D_APIS.md.
- [ ] **E. Self-review** — convention/anti-pattern pass; fix; green; commit.
- [ ] **F. Publish** — gh repo create yappershq/Arenas --public; house README.

## Ranking + economy (FINAL coordinator directive — lean)
- **Internal self-contained ladder** — which arena you're in IS your rank; climb by winning your duel, drop by losing (QueueManager). NO external ranking dependency, NO stats/elo DB, NO rank-provider abstraction.
- **LevelRanks runs out of the box** alongside Arenas (awards its own global points from kills); Arenas needs ZERO integration.
- **Optional-only seam:** a lean LevelRanks *read* for arena seeding-by-global-rank is documented in QueueModule.OnAllSharpModulesLoaded (`// ponytail:`) but NOT built (YAGNI + avoids a foreign .Shared build dep).
- **Standard CS round win/loss economy DISABLED** via ServerConfigModule convars (mp_maxmoney 0, mp_teamcashawards 0, mp_playercashawards 0, mp_maxrounds 0, mp_autoteambalance 0, …) on Init + OnServerActivate; shipped as .assets/configs/arenas.cfg.
- REMOVED the speculative IArenaRankProvider + IArenasVipProvider (no consumer; K4 has no VIP/rank features).

## Key design decisions (from scoping — see PORT_PLAN.md for full)
- Special rounds: ONE `Arenas.SpecialRounds` module registering N round types via `IArenasShared.RegisterRoundType`, sharing one arena-placement-keyed state store.
- HeadshotOnly MUST use pre-damage interception (`PlayerDispatchTraceAttack` pre-hook), NOT the source's post-hoc Health add-back.
- `GetArenaPlacement(steamId)` is the critical cross-team pairing surface — Core arena module supplies the resolver.
- All ephemeral per-player state = PlayerSlot-indexed arrays `[64]`, cleared on disconnect. Bots have SteamID64=0 → NEVER key ephemeral sets by SteamID64.
- **NO stats/elo/DB** — K4 only persists weapon/round PREFERENCES. Use `IClientPreference` cookies keyed by SteamID64 (the only cross-map state); no `Arenas.Database`, no MySQL creds.
- Round prefs keyed by **stable round-type name**, NOT K4's process-lifetime RoundType.ID (unstable across restart).
- Bot-fill (odd slots) + `bot_prefix` folded into QueueModule (K4 had a separate bots plugin; consolidate).

## Resume
Read docs/PORT_PLAN.md for architecture. Next unchecked phase above. Build green + commit each phase.
