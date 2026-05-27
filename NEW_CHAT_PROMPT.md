# Prompt for New Chat

Paste everything below the line into the new chat.

---

I'm building a STRAFTAT (FPS game) bot mod using BepInEx + Harmony. The mod is ~17,300 lines across 20 C# files. Combat, weapons, networking, and patching all work well. The core unsolved problem is **navigation on complex maps**.

## What Works

- Bot state machine: FindWeapon → GoToWeapon → PickUpWeapon → Hunt → Dead
- Combat AI with weapon-specific tactics (melee rush, shotgun close-range, explosive keep-distance, mine placement)
- NavGraph: A* pathfinding, spatial grid, 6 edge types (Walk/Jump/Fall/Slide/Ladder/WallJump), confidence system
- PlayerRecorder: records player movement, jumps, falls, slides, ladders into NavGraph
- Jump edges already store trajectory data: AirPositions[], AirTimestamps[], LockedSpeed, LockedAirTime
- Bots work great on small/simple maps
- 40+ Harmony patches for kill feed, explosions, melee, zone effects
- Mycelium networking syncs NavGraph to non-host clients

## What's Broken

**Bots can't reliably repeat complex paths.** This is THE problem. Specifically:

1. **Jump edges store trajectory data but A* and movement don't use it** — bots recalculate jump physics every traversal instead of replaying the recorded LockedSpeed/AirPositions. They overshoot, undershoot, or miss entirely.

2. **No route/sequence concept** — a 5-jump platforming chain is stored as 5 separate edges. Bots pathfind to each node independently, stop, reorient, then start the next jump. A player would do these fluidly without stopping. There's no "execute this sequence as one continuous movement."

3. **300 maps, manual training doesn't scale** — Training mode requires a human walking every path. Need automatic nav generation that works on any map geometry.

4. **Movement system isn't unified** — separate `_verticalVelocity` + horizontal calculation instead of FPC's single `moveDirection` vector. Causes edge cases with double gravity, inconsistent jump execution.

5. **Confidence decay is global** — one bot death penalizes ALL learned paths, not just the edge that killed it.

## Game Physics (from decompiled FirstPersonController)

- jumpForce: 8, gravity: 30 (falling) / 20 (rising) / 40 (crouching)
- walkSpeed: 7, sprintSpeed: 12, crouchSpeed: 5
- CharacterController: height=2, radius=0.4, stepOffset=0.6, slopeLimit=55
- No fall damage, only void deaths (y < -50)

## File Structure

| File | Lines | Purpose |
|------|-------|---------|
| BotController.cs | 2,479 | Core: fields, lifecycle, state dispatch, death/respawn |
| BotController.Movement.cs | 2,376 | MoveToward, jump, gravity, ladder, slide, stuck recovery |
| BotController.Combat.cs | 2,287 | Hunt AI, aiming, shooting, melee, weapon tactics |
| BotController.Modes.cs | 1,036 | Patrol, FollowPlayer, FollowTrail, Wander |
| BotController.Weapons.cs | 530 | Find/Go/Pickup weapon, ammo, dual-wield |
| NavGraph.cs | 1,767 | Core data, spatial grid, nodes/edges, confidence |
| NavGraph.Pathfinding.cs | 537 | A* with player-path preference, path straightening |
| NavGraph.Maintenance.cs | 1,629 | Pruning, declutter, merge, orphan cleanup |
| NavGraph.Serialization.cs | 399 | Binary save/load (FILE_VERSION=3), Mycelium sync |
| BotPatches.cs | 607 | Harmony hooks, training guard transpilers |
| BotPatches.Weapons.cs | 777 | Kill feed, explosion damage, melee patches |
| BotPatches.Lifecycle.cs | 198 | Scene load, bot spawn, recording hook |
| BotManager.cs | 809 | Spawning, cosmetics, draw timer |
| BotDebugVisualizer.cs | 808 | GL rendering of graph and bot overlays |
| PlayerRecorder.cs | 681 | Movement recording: jumps, falls, ladders, slides |
| BotDamageSync.cs | 478 | Mycelium RPC networking |
| Plugin.cs | 607 | BepInEx config and settings |

## What Was Already Tried and Failed

**Bot Validation mode** — raycast scan to place nodes on all walkable surfaces, then spawn 8 real bots to walk between node pairs and validate connections. Problems:
- Bots walked directly between random node pairs (MoveTowardNodeless) which doesn't understand level geometry like stairs/ramps
- Created jump edges on flat walkable ground because reactive steering jumped over small bumps
- Got stuck spinning at nodes directly above/below them
- Only created 1 connection per walk instead of building dense graphs
- Bots couldn't respawn properly after void deaths
- Produced garbage edge data that was worse than training mode
- The entire system was removed

## What I Need

I need you to read the full codebase, understand the existing systems deeply, then propose and implement a solution for reliable bot navigation on complex maps. The solution needs to handle:

- **Trajectory replay** — bots must execute recorded jump sequences exactly, not recalculate
- **Route sequences** — chain multiple edges into one continuous movement instruction
- **Auto-generation** — some way to build nav data for untrained maps without a human player
- **Reliable multi-level navigation** — stairs, ramps, platforms, multi-jump chains

Before proposing anything, read every file, ask me at least 5 questions about things you're unsure of, and don't make assumptions about the game's systems. The working directory is `c:\Users\kiran\GUNGAME LATEST BUILD\BOTS CURRENT WORKING ISH\`. Deploy builds to `C:\Steam\steamapps\common\STRAFTAT\BepInEx\plugins\StraftatBots.dll`.
