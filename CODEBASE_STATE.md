# STRAFTAT Bot Mod — Current State Summary

## Codebase: 20 files, ~17,300 lines of C#

---

## What Works Well

- **Bot state machine** — Clean flow: FindWeapon → GoToWeapon → PickUpWeapon → Hunt → Dead
- **Combat** — Weapon-specific tactics (melee rush, shotgun close-range, explosive distance-keeping, mine placement). Strafing, leaning, dodging, stun handling, hazard avoidance
- **NavGraph core** — A* pathfinding with confidence system, spatial grid lookups, edge types (Walk/Jump/Fall/Slide/Ladder/WallJump), player-path preference
- **Small map performance** — Bots navigate reliably on simple/small maps using learned paths
- **Weapon handling** — Find nearest weapon, pathfind to it, pick up, manage ammo, swap when empty, dual-wield detection
- **Harmony patches** — 40+ game methods hooked cleanly. Kill feed, explosions, melee, zone effects all working
- **Networking** — Mycelium sync of NavGraph, kills, cosmetics to non-host clients
- **Debug visualizer** — GL rendering of nodes, edges, paths, bot indicators, scan radius

## What Doesn't Work / Is Broken

### Movement (the big problem)
- **Can't reliably repeat complex paths** — Multi-jump chains, platforming sequences, tight jumps fail inconsistently
- **Jump edges store trajectory data but A* doesn't use it** — Bots recalculate jump speed every time instead of replaying the recorded LockedSpeed/trajectory
- **No route recording** — When a player does a 3-jump platforming sequence, the system stores individual jump edges but has no concept of "this is a sequence that must be executed in order"
- **Movement not unified** — Separate `_verticalVelocity` + horizontal instead of FPC's single `moveDirection` vector
- **Stuck recovery incomplete** — 3-escalation system partially implemented, bots still get stuck on geometry
- **Double gravity edge cases** — `_movedThisFrame` guard works mostly but not 100%

### Navigation
- **300 maps, manual training doesn't scale** — Training mode requires a human walking every path on every map
- **Confidence decay is global** — One bot death penalizes ALL learned paths, not just the one that killed it
- **Pruning loses important nodes** — Bridge/jump nodes may be pruned while weapon nodes are protected
- **No reachability set** — Must BFS every time to check if two nodes are connected
- **Walk edge height validation too strict** — 1.5m max rejects valid slopes

### Combat (minor issues)
- **Grenade arc prediction missing** — Bots throw straight, no arc calculation
- **Explosive hazard check too short** — Only looks 5m, grenades fly 20m+
- **No friendly fire handling** — Bots never damage each other in team mode

---

## File Structure

| File | Lines | Purpose |
|------|-------|---------|
| BotController.cs | 2,479 | Core: fields, lifecycle, state dispatch, death/respawn |
| BotController.Movement.cs | 2,376 | MoveToward, jump, gravity, ladder, slide, stuck recovery |
| BotController.Combat.cs | 2,287 | Hunt AI, aiming, shooting, melee, grenades, weapon tactics |
| BotController.Modes.cs | 1,036 | Patrol, FollowPlayer, FollowTrail, Wander |
| BotController.Weapons.cs | 530 | Find/Go/Pickup weapon, ammo management, dual-wield |
| NavGraph.cs | 1,767 | Core data, spatial grid, add/remove nodes/edges, confidence |
| NavGraph.Pathfinding.cs | 537 | A* implementation, path straightening, frontier/wander |
| NavGraph.Maintenance.cs | 1,629 | Pruning, declutter, merge, orphan cleanup |
| NavGraph.Serialization.cs | 399 | Binary save/load, Mycelium chunked sync |
| BotPatches.cs | 607 | Harmony hooks: solo play, training guard, death tracking |
| BotPatches.Weapons.cs | 777 | Kill feed sync, explosion damage, melee patches |
| BotPatches.Lifecycle.cs | 198 | Scene load, bot spawn, player recording |
| BotManager.cs | 809 | Spawning, lifecycle, cosmetics, draw timer |
| BotDebugVisualizer.cs | 808 | GL rendering of graph, bot overlays |
| PlayerRecorder.cs | 681 | Movement recording: jumps, falls, ladders, slides |
| BotDamageSync.cs | 478 | Mycelium RPC: kills, shots, NavGraph sync |
| Plugin.cs | 607 | BepInEx config, settings, training buttons |
| BotUI.cs | 43 | Lobby add/remove bot buttons |
| BotCountUI.cs | 154 | Bot count dropdown UI |
| BotData.cs | 78 | Bot metadata, cosmetic randomization |

---

## Key Game Values (from decompiled FPC)

- jumpForce: 8
- gravity: 30 (falling), 20 (rising), 40 (crouching)
- walkSpeed: 7, sprintSpeed: 12, crouchSpeed: 5
- CC: height=2, radius=0.4, stepOffset=0.6, slopeLimit=55
- No fall damage, only void deaths (y < -50)

## Key NavGraph Details

- **EdgeType enum**: Walk, Jump, Ladder, Fall, Slide, WallJump
- **Jump edges store**: AirPositions[], AirTimestamps[], LockedSpeed, LockedAirTime — but pathfinding ignores these
- **Confidence**: 0-1 float. +0.15 on success, -0.35 on death, -0.15 on stuck. Edges below 0.05 pruned on save
- **A* cost modifiers**: Jump 0.5x-0.9x, Ladder 0.3x-0.6x, Slide 0.75x, Fall 1.5x, NearEdge 3x

## Modes

- **Training**: Player walks map, NavGraph records paths. Bots explore/wander to fill gaps
- **Play**: Bots use learned NavGraph. A* pathfinding with player-path preference

---

## The Core Problem

Bots work great on small, simple maps. On complex maps with multi-level platforming, tight jumps, and chain sequences:

1. **They can't repeat hard paths** — Jump edges exist but bots recalculate physics each time instead of replaying exact recorded trajectories
2. **No sequence concept** — A 5-jump platforming chain is 5 separate edges with no "do these in order without stopping" instruction
3. **300 maps need auto-generation** — Manual training per map is impractical
4. **Learning is noisy** — Confidence system helps but one bad death penalizes unrelated paths

The previous attempt (Bot Validation mode — raycast scan + bot pair-walking) was removed because it produced bad data and had fundamental design issues.
