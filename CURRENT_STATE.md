# Straftat Bots — Current State (2026-04-21)

## Latest session work

### 1. Kill feed fix for bot explosions (`BotPatches.Weapons.cs`)

**Problem:** Bot-fired Obus/Bubble/grenades dealt damage and killed, but no kill feed entries appeared. The `SendKillLog` prefix patches never fired because the game's `HandleExplosion` aborts early on bots due to an `isOwner` gate, so `SendKillLog` is never reached.

**Fix:** Moved the kill feed `PauseManager.Instance.WriteLog(...)` call into the postfixes that already bypass the `isOwner` gate:

- `ObusExplosion_Postfix` — now writes `"<victim> was killed with a Serac by <killer>"` inside the lethal branch.
- `Explosion_Postfix` non-instant-kill branch (grenades, Bubblee, HandGrenade, HandGrenadeTwo, PhysicsGrenade) — now writes kill feed when bot's health hits zero. Instant-kill branch (Claymore/ProxMine) already had this.
- New helper `ResolveExplosiveWeaponName(MonoBehaviour)` maps types → user-facing weapon names: Serac, Bublee, grenade, Claymore, AP Mine, Proximity Mine.
- Self-kills suppressed (the `Die()` path handles those).

### 2. Hunt-mode movement jitter (`BotController.Combat.cs` + `BotController.cs`)

**Problem:** Bots oscillated/jittered while fighting. Four root causes identified.

**Fixes:**

1. **Dead `threshold`/`exitRange` variable in `HandleHunt`** — it was computed but never used, so bots flipped between advance/strafe every frame near `enterRange`. Replaced with a proper hysteresis sub-state (`_huntSubState`: -1 back-up, 0 strafe, +1 advance) with a 0.35 s debounce (`_huntSubStateHold`), so sub-state can't switch more than ~3×/sec.

2. **Discrete `approach` factor in `CombatMovement`** — old code was `dist > 15f ? 0.7f : (dist < 8f ? -0.3f : 0.3f)`, which sign-flipped the toward/away component at 8 m. Replaced with a monotonic piecewise-linear curve, then low-pass filtered via `_smoothedApproach` (~0.2 s TC).

3. **No final-motion smoothing** — added `_smoothedStrafeDir` Vector3 field, Lerp'd per frame (~0.1 s TC) on the composite strafe+approach direction. Kills per-frame direction jumps from edge/wall/strafe-flip checks. Snaps to zero on stop.

4. **Strafe switch cadence** — random interval increased from 2.5–5 s to 3.5–6.5 s so visible weave is less twitchy.

New fields added in `BotController.cs` near other combat-strafing state:
```csharp
private Vector3 _smoothedStrafeDir;
private float _smoothedApproach;
private float _huntSubState;
private float _huntSubStateHold;
```
All reset on respawn alongside existing strafe/dodge resets.

## Still pending / not yet built

- **#44** Dedupe + validate jump edges (merge similar)
- **#45** Restrict Slide edges to forced-slide-only
- **#46** Improve crouch-walking + air-strafing

## Unverified / needs in-game test

- The kill-feed fix is plausible but not confirmed in-game. The prior `SendKillLog` prefixes are still registered and mostly harmless but now effectively dead code. Leave in for safety until verified.
- The hunt-mode smoothing changes may feel **too smooth** (sluggish) in real combat. The time constants (10 Hz motion, 5 Hz approach) are first pass — if bots feel unresponsive, raise the coefficients (multiply by ~1.5).
- `_smoothedStrafeDir` starts at zero on spawn, so there's a ~100 ms motion ramp-up on entering combat for the first time. Acceptable but can be addressed by snapping to target direction on first non-zero frame.

## Architectural landmines (from this session)

- **Bot `isOwner` == false on server.** The game's `OnCollisionEnter`, `HandleExplosion`, `SendKillLog` paths all early-out when `isOwner == false`, and server-spawned bots fail that check. Any weapon/projectile fix must route through the server-side `*_Postfix` that bypasses the gate, not the original code path.
- **`KillShockWave` and similar VFX methods NRE on bots** because `_rootObject.FirstPersonController.lensDistortion` etc. don't exist. Always prefix-skip for bot rootObjects.
- **`ClientInstance.Instance.PlayerNameTag`** is always the **local human**, never a bot. Don't use it to name killer bots in kill feeds — pull from `BotController.BotName` or `PlayerValues.playerClient.PlayerNameTag`.
- **Nodeless-lock window** (`_nodelessLockTimer`) is engaged on ping-pong detection + stuck recovery stages 2/3. 4 s base + 2 s per escalation, max 14 s. Decays bounce count after 30 s idle.

## Files touched this session

- `BotController.cs` — added hunt-smoothing fields + reset block (near line ~376 and ~1465)
- `BotController.Combat.cs` — rewrote the `else` branch of `HandleHunt` (ranged weapon path, ~line 403-469); rewrote strafe direction + smoothing in `CombatMovement` (~line 519-602)
- `BotPatches.Weapons.cs` — added `ResolveExplosiveWeaponName` helper; added kill feed to `Explosion_Postfix` non-instant branch and to `ObusExplosion_Postfix`

## Build + deploy

Sandbox has no `dotnet`/`msbuild`. Build on Windows:
```
dotnet build StraftatBots.csproj -c Release
```
Output: `bin\Release\net48\StraftatBots.dll` → drop into `BepInEx\plugins\`.
