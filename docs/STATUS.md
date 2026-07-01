# Arenas — Port Status

Phase tracker + resume instructions. Update after every phase.

## Build / verify
```
cd /home/claude/Arenas && env -u version dotnet build -c Release
```
Output: `.build/modules/Arenas.Core/Arenas.dll` + `.build/shared/Arenas.Shared/Arenas.Shared.dll`.

## Phases
- [x] **A. Scaffold** — slnx, Directory.Build.props, .gitignore, Core (ArenasPlugin + InterfaceBridge + IModule + DI + BootstrapModule + Utils), Shared (IArenasShared stub: RegisterRoundType / GetArenaPlacement), locale stub. GREEN. Committed.
- [ ] **B. Core spine** — Config, PlayerLifecycle, Arena assignment/ladder/queue, Round flow, weapon loadout per round type.
- [ ] **C. Stats / ranking / DB** (Arenas.Database) + commands (!rank, !top, …) + menus (weapon prefs).
- [ ] **D. Public API + SpecialRounds module** — flesh IArenasShared, Arenas.SpecialRounds (HeadshotOnly/Nades/NoCrosshair/OneTap).
- [ ] **E. Self-review** — convention/anti-pattern pass; fix; green; commit.
- [ ] **F. Publish** — gh repo create yappershq/Arenas --public; house README.

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
