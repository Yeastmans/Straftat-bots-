# Straftat Bots — Current Handoff (2026-05-27)

## Workspace + deploy target

- Working folder: `C:\Users\kiran\GUNGAME LATEST BUILD\bots combinations\Straftat Bots`
- Game plugin folder: `C:\Steam\steamapps\common\STRAFTAT\BepInEx\plugins`
- Build command: `dotnet build StraftatBots.csproj -c Release`
- Output DLL: `bin\Release\net48\StraftatBots.dll`
- Deploy step: copy that DLL into the game plugin folder above.

## What we are actively working on

The user is playtesting aggressively and reports these as still broken / not matching base game:

1. Hats on bots still do not work correctly.
2. Bots path poorly through teleporters in hunt cases (target on far side of teleporter).
3. Impulse zones still not behaving correctly for bots.
4. Stun grenades thrown by bots do not explode reliably.
5. Kill-feed still has mismatches (weapon names/colors/format vs base game).
6. Freeze when player dies was reported during one test run and needs focused repro.
7. Occasional combat/pathing stall just outside melee range.

## Important prior findings to keep in view

1. GravityZone handling for bots looked missing in trigger-zone patch path.
2. Bot vertical zone-force logic was not fully mirroring player behavior.
3. Combat movement had separate zone-force flow that could skip landing-aware cleanup.
4. Projectile owner registry risked stale references if not cleaned up.

## Current GitHub state

- User provided repo: `https://github.com/Yeastmans/Straftat-bots-`
- From this folder, `git` resolves to parent repo root: `C:\Users\kiran\GUNGAME LATEST BUILD`
- `Straftat Bots` currently appears as untracked in that parent repo context.
- No remote was configured in the active repo context when checked.
- We paused to avoid committing unrelated root-level files by accident.

## Safe next steps

1. Confirm repo strategy:
   - either initialize/use Git directly in `Straftat Bots`,
   - or keep using parent repo but stage only this project path deliberately.
2. Fix hats + teleporter pathing first (highest repeated user pain).
3. Re-verify impulse/gravity/force zone parity against decompiled player behavior.
4. Re-test stun grenade lifecycle and kill-feed text/color mapping against base-game outputs.
5. Build + deploy DLL to plugin folder and verify timestamp/hash after copy.

## Notes for whoever picks this up

- User wants practical fixes and deploys quickly; they are testing in live matches and reporting regressions fast.
- Prefer minimal, targeted changes and immediate in-game verification loops.
