# Phase D (SpecialRounds) — pre-verified ModSharp APIs

Verified via mcp__modsharp__ so the SpecialRounds port doesn't guess.

## HeadshotOnly (OnlyHS-AK/Scout/USP → one parameterized round)
- Hook: `IHookManager.PlayerDispatchTraceAttack.Register(cb, EHookMode.Pre)` → `EHookAction`.
- Params: `IPlayerDispatchTraceAttackHookParams` exposes:
  - `.Info` (TakeDamageInfo*), and directly: `HitGroup` (enum `HitGroupType`), `AttackerPlayerSlot` (int),
    `IsPawn` (bool), `IsWorld` (bool), `Damage`/`OriginalDamage` (float), `Team` (int).
  - `args.Entity` = victim pawn.
- Rule: if `HitGroup != HitGroupType.Head` AND attacker+victim are both in the SAME arena's two teams for a
  HeadshotOnly round → return `EHookAction.SkipCallReturnOverride` to zero the damage. Allow otherwise
  (`EHookAction.Ignored`). NEVER write Health. (`HitGroupType` — verify exact Head member name via get_type.)

## NoCrosshair
- Field: `CBasePlayerPawn.m_iHideHUD` (uint, networked). Set via `pawn.SetNetVar("m_iHideHUD", value)`.
- Crosshair bit = `1u << 8`. On round start: `SetNetVar("m_iHideHUD", cur | (1u<<8))`; reset on end: clear bit.

## Nades
- Event `grenade_thrown` (fields: userid, weapon). HookEvent + IEventListener. On throw by a tracked player,
  re-give an HE: `pawn.GiveNamedItem("weapon_hegrenade")` (verify classname).

## OneTap
- Event `weapon_fire` (fields: userid, weapon, silenced). On fire by a tracked player, top up the SAME-arena
  opponent's clip next frame via `InvokeFrameAction`. The pawn→active-weapon accessor + `Clip1` setter must be
  VERIFIED via mcp (the pattern DB shows `pawn.FindWeapon(...)` but confirm the real method on IPlayerPawn /
  weapon services before use).
- `HitGroupType.Head` CONFIRMED (enum: Invalid, Generic=0, Head, Chest, Stomach, LeftArm, RightArm, LeftLeg,
  RightLeg, Neck, Unknown9, Gear, Special).
- Use `IArenasShared.GetArenaPlacement(steamId)` + `FindOpponents(steamId)` to pair within the arena.
- OneTapTimer is BROKEN upstream — SKIP.

## Registration
Each round type: `arenasShared.RegisterRoundType(localeKey, teamSize:1, enabledByDefault:false, onStart, onEnd)`
in the SpecialRounds module's OnAllSharpModulesLoaded (after resolving IArenasShared). Unregister in Shutdown.
Share ONE `Dictionary<int arenaId, RoundState>` state store keyed by placement. All strings via localizer.
