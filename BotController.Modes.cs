using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    public partial class BotController
    {

        // SmartExplore state machine — replaces random explore
        private ExploreState _exploreState = ExploreState.None;
        private float _exploreStateTimer;          // Time remaining in current state
        private float _exploreTotalTimer;          // Total explore session timer
        private Vector3 _exploreTarget;            // Current explore movement target
        private Vector3 _exploreStartPos;          // Where explore session started
        private int _exploreStateAttempts;         // How many states cycled this session
        private float _edgeWalkDir;                // Angle for EdgeWalk (perpendicular to gap)
        private bool _edgeWalkFlipped;             // Already tried other direction
        private Vector3 _probeTarget;              // Platform detected by PlatformProbe
        private bool _probeJumpAttempted;          // Already tried jumping this probe cycle

        private void BeginSmartExplore(Vector3 target)
        {
            _exploreStartPos = transform.position;
            _exploreStateAttempts = 0;
            _edgeWalkFlipped = false;
            _probeJumpAttempted = false;
            _probeTarget = Vector3.zero;

            // Short session — bots retry from a fresh angle instead of brute-forcing
            // the same approach for minutes at a time.
            _exploreTotalTimer = 12f;

            // Seed a node at the explore start point — marks this as reachable
            // so future pathfinding has a proven anchor even on sparse maps.
            SeedExploreNode(transform.position, highConfidence: true);

            _exploreState = ExploreState.None; // PickNext will set the first state
            PickNextExploreState(target);
        }

        // Seed a node at a position discovered or reached during exploration.
        // Used on sparse/untrained maps so bots build graph coverage as they wander.
        // Bypasses the Play-mode lock because explore's whole purpose is to extend the graph.
        private void SeedExploreNode(Vector3 pos, bool highConfidence = false)
        {
            if (NavGraph.Instance == null) return;
            // force:true so Play-mode bots can still seed during Explore
            var node = NavGraph.Instance.AddPosition(pos, isPlayer: false, force: true);
            if (node != null && highConfidence)
            {
                node.Confidence = Mathf.Max(node.Confidence, 0.7f);
                node.VisitCount = Mathf.Max(node.VisitCount, 3);
            }
        }

        private void PickNextExploreState(Vector3 target)
        {
            _exploreStateAttempts++;
            float heightDiff = target.y - transform.position.y;
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0;
            Vector3 horizDir = toTarget.sqrMagnitude > 1f ? toTarget.normalized : transform.forward;

            // Priority order — skip states already tried this cycle
            // Per-state timers tightened: give each tactic just long enough to commit,
            // then move on. Prevents bots wasting 6-8s repeatedly nudging into the same wall.
            if (_exploreState < ExploreState.HeightSeek && Mathf.Abs(heightDiff) > 3f)
            {
                _exploreState = ExploreState.HeightSeek;
                _exploreStateTimer = 4f;
                _exploreTarget = target;
                Plugin.Log.LogInfo($"[{BotName}] Explore: HeightSeek (diff={heightDiff:F1}m)");
                return;
            }
            if (_exploreState < ExploreState.PlatformProbe && IsEdgeAhead(horizDir, 1.5f))
            {
                _exploreState = ExploreState.PlatformProbe;
                _exploreStateTimer = 3f;
                _probeJumpAttempted = false;
                _probeTarget = Vector3.zero;
                Plugin.Log.LogInfo($"[{BotName}] Explore: PlatformProbe (gap detected)");
                return;
            }
            if (_exploreState < ExploreState.EdgeWalk)
            {
                _exploreState = ExploreState.EdgeWalk;
                _exploreStateTimer = 4f;
                _edgeWalkFlipped = false;
                Plugin.Log.LogInfo($"[{BotName}] Explore: EdgeWalk");
                return;
            }
            // Final fallback
            _exploreState = ExploreState.FrontierWalk;
            _exploreStateTimer = 3f;
            Plugin.Log.LogInfo($"[{BotName}] Explore: FrontierWalk");
        }

        private void SmartExplore(Vector3 target)
        {
            _exploreStateTimer -= Time.deltaTime;

            // Check for success: significantly closer to target
            float currentDist = Vector3.Distance(transform.position, target);
            float startDist = Vector3.Distance(_exploreStartPos, target);
            if (currentDist < startDist - 5f)
            {
                // Seed a waypoint node at the success point — proven reachable.
                SeedExploreNode(transform.position, highConfidence: true);
                _exploreState = ExploreState.None;
                Plugin.Log.LogInfo($"[{BotName}] Explore success — {(startDist - currentDist):F1}m closer");
                return;
            }

            // State timeout → advance to next
            if (_exploreStateTimer <= 0f)
                PickNextExploreState(target);

            switch (_exploreState)
            {
                case ExploreState.HeightSeek:    ExploreHeightSeek(target); break;
                case ExploreState.PlatformProbe: ExplorePlatformProbe(target); break;
                case ExploreState.EdgeWalk:      ExploreEdgeWalk(target); break;
                case ExploreState.FrontierWalk:  ExploreFrontierWalk(target); break;
            }
        }

        // ---- HeightSeek: find ladders, ramps, ledges, or controlled drops ----
        private void ExploreHeightSeek(Vector3 target)
        {
            float heightDiff = target.y - transform.position.y;
            Vector3 pos = transform.position;

            if (heightDiff > 3f)
            {
                // TARGET ABOVE — try ladder, then ramp, then ledge scan

                // 1. Ladder
                Collider ladder = FindNearbyLadder(25f);
                if (ladder != null)
                {
                    _exploreTarget = ladder.ClosestPoint(pos);
                    MoveTowardNodeless(_exploreTarget, _sprintSpeed);
                    return;
                }

                // 2. Ramp/stair scan — 12 directions, find rising ground
                Vector3 bestDir = Vector3.zero;
                float bestHeight = -999f;
                for (int i = 0; i < 12; i++)
                {
                    Vector3 testDir = Quaternion.Euler(0, i * 30f, 0) * Vector3.forward;
                    if (Physics.Raycast(pos + Vector3.up * 0.8f, testDir, 2f, WALL_MASK, QueryTriggerInteraction.Ignore))
                        continue;
                    Vector3 checkPos = pos + testDir * 4f + Vector3.up * 2.5f;
                    if (Physics.Raycast(checkPos, Vector3.down, out RaycastHit rHit, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    {
                        if (rHit.point.y > bestHeight)
                        { bestHeight = rHit.point.y; bestDir = testDir; }
                    }
                }
                if (bestDir.sqrMagnitude > 0.01f && bestHeight > pos.y + 0.3f)
                {
                    _exploreTarget = pos + bestDir * 8f;
                    MoveTowardNodeless(_exploreTarget, _sprintSpeed);
                    return;
                }

                // 3. Ledge/crate scan — look for jumpable surfaces above.
                // EXPANDED: denser directional sweep (16 dirs) and wider height band
                // (up to 3m above) so bots can find high platforms when no nodes exist.
                Vector3 bestLedge = Vector3.zero;
                float bestLedgeHeight = -999f;
                // Score ledges by how much they close the height gap toward target —
                // prefer ledges *above* the bot, not just any reachable platform.
                float bestLedgeScore = -999f;
                // Close scan: 16 dirs, 1.8m horizontal, 0.3-3.0m above
                for (int i = 0; i < 16; i++)
                {
                    Vector3 scanDir = Quaternion.Euler(0, i * 22.5f, 0) * Vector3.forward;
                    Vector3 scanFrom = pos + scanDir * 1.8f + Vector3.up * 4f;
                    if (Physics.Raycast(scanFrom, Vector3.down, out RaycastHit lHit, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    {
                        float above = lHit.point.y - pos.y;
                        if (above >= 0.3f && above <= 3.0f)
                        {
                            if (!Physics.Raycast(lHit.point + Vector3.up * 0.1f, Vector3.up, 2f, WALL_MASK, QueryTriggerInteraction.Ignore))
                            {
                                // Score: height gain minus a small penalty for overshooting target
                                float score = above - Mathf.Max(0f, above - heightDiff) * 0.5f;
                                if (score > bestLedgeScore)
                                {
                                    bestLedge = lHit.point;
                                    bestLedgeHeight = above;
                                    bestLedgeScore = score;
                                }
                            }
                        }
                    }
                }

                // Far scan: 12 dirs, 3-8m horizontal, 0-2.5m above (sprint-jump range)
                if (bestLedge == Vector3.zero)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        Vector3 scanDir = Quaternion.Euler(0, i * 30f, 0) * Vector3.forward;
                        float[] distances = { 3f, 5f, 7f };
                        foreach (float d in distances)
                        {
                            Vector3 scanFrom = pos + scanDir * d + Vector3.up * 4f;
                            if (Physics.Raycast(scanFrom, Vector3.down, out RaycastHit fHit, 6f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                            {
                                float above = fHit.point.y - pos.y;
                                if (above >= 0f && above <= 2.5f && above > bestLedgeHeight)
                                {
                                    if (!Physics.Raycast(fHit.point + Vector3.up * 0.1f, Vector3.up, 2f, WALL_MASK, QueryTriggerInteraction.Ignore))
                                    {
                                        bestLedge = fHit.point;
                                        bestLedgeHeight = above;
                                    }
                                }
                            }
                        }
                    }
                }

                if (bestLedge != Vector3.zero)
                {
                    Vector3 toLedge = bestLedge - pos;
                    toLedge.y = 0;
                    float horizDist = toLedge.magnitude;

                    // Tall ledge (>1.8m): back up first to get a running start, then sprint-jump.
                    // Without a run-up, bots can't clear tall platforms.
                    if (bestLedgeHeight > 1.8f && horizDist < 2.5f && _cc.isGrounded)
                    {
                        Vector3 ledgeDir = toLedge.sqrMagnitude > 0.01f ? toLedge.normalized : transform.forward;
                        // If we're too close, reverse slightly to build momentum
                        if (horizDist < 1.2f)
                        {
                            _exploreTarget = pos - ledgeDir * 2.0f;
                            MoveTowardNodeless(_exploreTarget, _sprintSpeed);
                            return;
                        }
                        // In the sweet spot — jump toward ledge with sprint built up
                        TryJump(JumpReason.EdgeAhead, ledgeDir, intentionalTime: 1.5f);
                        return;
                    }

                    if (horizDist < 1.5f && _cc.isGrounded)
                    {
                        Vector3 jumpDir = toLedge.sqrMagnitude > 0.01f ? toLedge.normalized : transform.forward;
                        TryJump(JumpReason.EdgeAhead, jumpDir, intentionalTime: 1.0f);
                    }
                    else
                    {
                        _exploreTarget = pos + toLedge.normalized * Mathf.Min(horizDist, 3f);
                        MoveTowardNodeless(_exploreTarget, _sprintSpeed);
                    }
                    return;
                }

                // 4. Wall-jump fallback — if target is above and we're near a tall wall
                // but no reachable ledge was found, try to wall-jump off it toward target.
                // Tight space maps sometimes need this to get up to catwalks / roofs.
                Vector3 targetHoriz = target - pos; targetHoriz.y = 0f;
                Vector3 wallProbeDir = targetHoriz.sqrMagnitude > 0.5f ? targetHoriz.normalized : transform.forward;
                if (Physics.Raycast(pos + Vector3.up * 1.0f, wallProbeDir, out RaycastHit wallHit,
                    1.5f, WALL_MASK, QueryTriggerInteraction.Ignore))
                {
                    // Wall ahead — confirm it's tall (extends past 2.5m, i.e. not a low step)
                    bool tallWall = Physics.Raycast(pos + Vector3.up * 2.5f, wallProbeDir, 1.5f,
                        WALL_MASK, QueryTriggerInteraction.Ignore);
                    if (tallWall)
                    {
                        // Walk into it and jump — a successful wall-jump will be picked up by
                        // PlayerRecorder's WallJump branch and added as a WallJump edge.
                        MoveTowardNodeless(pos + wallProbeDir * 2f, _sprintSpeed);
                        if (_cc.isGrounded)
                            TryJump(JumpReason.EdgeAhead, wallProbeDir, intentionalTime: 1.5f);
                        return;
                    }
                }

                // Nothing found — just walk toward target
                MoveTowardNodeless(target, _sprintSpeed);
            }
            else if (heightDiff < -3f)
            {
                // TARGET BELOW — controlled drop
                Vector3 toTarget = target - pos;
                toTarget.y = 0;
                Vector3 horizDir = toTarget.sqrMagnitude > 0.5f ? toTarget.normalized : transform.forward;

                // Check for safe drop: raycast down from edge position
                Vector3 edgePos = pos + horizDir * 1.0f;
                if (IsEdgeAhead(horizDir, 1.0f))
                {
                    if (Physics.Raycast(edgePos + Vector3.up * 0.5f, Vector3.down, out RaycastHit dropHit,
                        15f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    {
                        if (dropHit.point.y > -45f)
                        {
                            // Safe drop — suppress edge avoidance, walk off
                            _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 1.5f);
                            _jumpDir = horizDir;
                        }
                    }
                }
                MoveTowardNodeless(target, _sprintSpeed);
            }
            else
            {
                // Height diff reduced below threshold — advance state
                _exploreStateTimer = 0f;
            }

            // Check early exit: height improved
            float currentHeightDiff = Mathf.Abs(target.y - pos.y);
            float startHeightDiff = Mathf.Abs(target.y - _exploreStartPos.y);
            if (startHeightDiff - currentHeightDiff > 2f)
            {
                // Seed a node here — this position is a proven height-gain point.
                SeedExploreNode(pos, highConfidence: true);
                _exploreState = ExploreState.None;
                Plugin.Log.LogInfo($"[{BotName}] HeightSeek success — {(startHeightDiff - currentHeightDiff):F1}m closer in height");
            }
        }

        // ---- PlatformProbe: detect platforms across gaps and attempt jumps ----
        private void ExplorePlatformProbe(Vector3 target)
        {
            Vector3 pos = transform.position;
            Vector3 toTarget = target - pos;
            toTarget.y = 0;
            Vector3 horizDir = toTarget.sqrMagnitude > 1f ? toTarget.normalized : transform.forward;

            // One-time scan for platforms
            if (_probeTarget == Vector3.zero && !_probeJumpAttempted)
            {
                float bestScore = float.MaxValue;
                float[] angles = { 0f, 15f, -15f, 30f, -30f };
                float[] distances = { 3f, 5f, 7f, 9f };

                foreach (float angle in angles)
                {
                    Vector3 probeDir = Quaternion.Euler(0, angle, 0) * horizDir;
                    foreach (float dist in distances)
                    {
                        Vector3 scanFrom = pos + probeDir * dist + Vector3.up * 3f;
                        if (Physics.Raycast(scanFrom, Vector3.down, out RaycastHit hit, 6f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                        {
                            float heightGain = hit.point.y - pos.y;
                            float horizDist = new Vector3(hit.point.x - pos.x, 0, hit.point.z - pos.z).magnitude;

                            // Check within jump envelope
                            bool jumpable = horizDist < 12f && heightGain < 1.8f && heightGain > -8f;
                            if (!jumpable) continue;

                            // Score: prefer closer to target
                            float distToTarget = Vector3.Distance(hit.point, target);
                            if (distToTarget < bestScore)
                            {
                                bestScore = distToTarget;
                                _probeTarget = hit.point;
                            }
                        }
                    }
                }

                if (_probeTarget == Vector3.zero)
                {
                    // No platform found — advance to EdgeWalk
                    _exploreStateTimer = 0f;
                    return;
                }
                // Pre-seed a frontier node at the probed target — even if the jump fails
                // this marks the platform for future attempts.
                SeedExploreNode(_probeTarget);
                Plugin.Log.LogInfo($"[{BotName}] PlatformProbe: found target at {_probeTarget}");
            }

            if (_probeJumpAttempted)
            {
                // Already jumped — advance state
                _exploreStateTimer = 0f;
                return;
            }

            // Approach the gap edge and jump
            Vector3 dirToProbe = _probeTarget - pos;
            dirToProbe.y = 0;
            Vector3 jumpDir = dirToProbe.sqrMagnitude > 0.01f ? dirToProbe.normalized : horizDir;

            if (IsEdgeAhead(jumpDir, 0.8f) && _cc.isGrounded)
            {
                // At edge — jump toward platform
                _probeJumpAttempted = true;
                TryJump(JumpReason.EdgeAhead, jumpDir, intentionalTime: 1.5f);
            }
            else
            {
                // Sprint toward edge
                MoveTowardNodeless(pos + jumpDir * 5f, _sprintSpeed);
            }
        }

        // ---- EdgeWalk: walk along gap edges to find crossings ----
        private void ExploreEdgeWalk(Vector3 target)
        {
            Vector3 pos = transform.position;
            Vector3 toTarget = target - pos;
            toTarget.y = 0;
            Vector3 horizDir = toTarget.sqrMagnitude > 1f ? toTarget.normalized : transform.forward;

            // Pick perpendicular direction on first frame
            if (!_edgeWalkFlipped && _edgeWalkDir == 0f)
            {
                // Choose left or right — prefer direction without wall
                Vector3 right = Vector3.Cross(Vector3.up, horizDir).normalized;
                bool rightClear = !Physics.Raycast(pos + Vector3.up * 0.8f, right, 3f, WALL_MASK, QueryTriggerInteraction.Ignore);
                bool leftClear = !Physics.Raycast(pos + Vector3.up * 0.8f, -right, 3f, WALL_MASK, QueryTriggerInteraction.Ignore);

                if (rightClear && !leftClear) _edgeWalkDir = 1f;
                else if (leftClear && !rightClear) _edgeWalkDir = -1f;
                else _edgeWalkDir = Random.value > 0.5f ? 1f : -1f;
            }

            // Walk perpendicular to gap
            Vector3 perpDir = Vector3.Cross(Vector3.up, horizDir).normalized * _edgeWalkDir;
            MoveTowardNodeless(pos + perpDir * 8f, _sprintSpeed);

            // Periodically check if gap still exists
            if (Time.frameCount % 30 == 0) // ~0.5s at 60fps
            {
                if (!IsEdgeAhead(horizDir, 1.5f))
                {
                    // Gap gone — crossing found! Seed this position as a proven waypoint.
                    SeedExploreNode(pos, highConfidence: true);
                    _exploreState = ExploreState.None;
                    Plugin.Log.LogInfo($"[{BotName}] EdgeWalk: found crossing!");
                    return;
                }

                // Check if gap narrowed — quick re-probe
                float[] shortDists = { 2f, 3f, 4f };
                foreach (float d in shortDists)
                {
                    Vector3 scanFrom = pos + horizDir * d + Vector3.up * 3f;
                    if (Physics.Raycast(scanFrom, Vector3.down, out RaycastHit hit, 6f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    {
                        float hDist = new Vector3(hit.point.x - pos.x, 0, hit.point.z - pos.z).magnitude;
                        if (hDist < 5f && hit.point.y - pos.y < 1.8f && hit.point.y - pos.y > -8f)
                        {
                            // Narrower gap — attempt jump
                            Vector3 jumpDir = (hit.point - pos); jumpDir.y = 0; jumpDir.Normalize();
                            if (_cc.isGrounded && IsEdgeAhead(jumpDir, 0.8f))
                            {
                                TryJump(JumpReason.EdgeAhead, jumpDir, intentionalTime: 1.5f);
                                _exploreStateTimer = 0f;
                                return;
                            }
                        }
                    }
                }
            }

            // Stuck or hit wall — flip direction
            if (_stuckTimer > 2f)
            {
                if (!_edgeWalkFlipped)
                {
                    _edgeWalkDir = -_edgeWalkDir;
                    _edgeWalkFlipped = true;
                    _stuckTimer = 0f;
                }
                else
                {
                    // Already flipped — give up on EdgeWalk
                    _exploreStateTimer = 0f;
                }
            }
        }

        // ---- FrontierWalk: walk to boundary of explored territory ----
        private void ExploreFrontierWalk(Vector3 target)
        {
            if (_exploreTarget == Vector3.zero || Vector3.Distance(transform.position, _exploreTarget) < 2f)
            {
                // Pick a new frontier target
                _exploreTarget = Vector3.zero;
                if (NavGraph.Instance != null && NavGraph.Instance.HasData)
                {
                    var frontier = NavGraph.Instance.FindFrontierNode(transform.position, 5f);
                    if (frontier != null)
                    {
                        // Prefer frontiers closer to target's height
                        float frontierHeightDiff = Mathf.Abs(frontier.Position.y - target.y);
                        float currentHeightDiff = Mathf.Abs(transform.position.y - target.y);
                        if (frontierHeightDiff < currentHeightDiff + 5f)
                            _exploreTarget = frontier.Position;
                    }
                }

                if (_exploreTarget == Vector3.zero)
                {
                    // No good frontier — walk toward target with random deviation
                    Vector3 toTarget = target - transform.position;
                    toTarget.y = 0;
                    if (toTarget.sqrMagnitude > 1f)
                    {
                        float deviation = Random.Range(-60f, 60f);
                        Vector3 devDir = Quaternion.Euler(0, deviation, 0) * toTarget.normalized;
                        _exploreTarget = transform.position + devDir * 15f;
                    }
                    else
                    {
                        Vector3 randomDir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
                        _exploreTarget = transform.position + randomDir * 15f;
                    }
                }
            }

            MoveToward(_exploreTarget, _sprintSpeed);
        }

        // ===================== WANDER =====================

        private void Wander()
        {
            _wanderChangeTimer -= Time.deltaTime;

            // Track explored areas — record current grid cell every 2s
            _exploredCellTimer -= Time.deltaTime;
            if (_exploredCellTimer <= 0f)
            {
                _exploredCellTimer = 2f;
                Vector3 pos = transform.position;
                long cellKey = NavGraph.GridKeyPublic(pos);
                if (!_exploredCells.Add(cellKey))
                    _exploredStaleCount++; // Revisiting known area
                else
                    _exploredStaleCount = Mathf.Max(0, _exploredStaleCount - 2); // New area discovered
            }

            // SmartExplore when stuck in Wander — same system as Connect mode
            if (_exploreState != ExploreState.None)
            {
                _exploreTotalTimer -= Time.deltaTime;
                if (_exploreTotalTimer <= 0f || _exploreStateAttempts >= 4)
                {
                    _exploreState = ExploreState.None;
                    _stuckTimer = 0f;
                }
                else
                {
                    SmartExplore(_wanderTarget);
                    return;
                }
            }

            // Trigger SmartExplore when stuck 2s+ and have a target.
            // Also push the current target to the shared frontier queue so another
            // bot can retry it from a different angle — this is half of the
            // trial-and-error map-learning loop. The approach direction we were
            // using when we gave up is attached so the next bot biases away ≥45°.
            if (_stuckTimer > 2f && _hasWanderTarget && _wanderTarget != Vector3.zero)
            {
                if (NavGraph.Instance != null)
                {
                    Vector3 approachDir = _wanderTarget - transform.position;
                    approachDir.y = 0f;
                    if (approachDir.sqrMagnitude > 0.01f) approachDir.Normalize();
                    NavGraph.Instance.PushFrontier(_wanderTarget, approachDir, BotId);
                }
                BeginSmartExplore(_wanderTarget);
                _stuckTimer = 0f;
                return;
            }

            if (!_hasWanderTarget || HorizontalDist(transform.position, _wanderTarget) < 3f
                || _wanderChangeTimer <= 0f || _wanderTarget == Vector3.zero)
            {
                bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;

                // Budget decay — when the graph has plateaued, lengthen commitment to
                // current targets (bots thrash less). Scalar is 1.0 when graph is
                // growing, 0.5 when it's been stable for 2 minutes.
                float commitmentMultiplier = NavGraph.Instance != null
                    ? Mathf.Lerp(1f, 2f, 1f - NavGraph.Instance.ExploreAggression)
                    : 1f;

                // Find average position of other bots for anti-clustering
                Vector3 otherBotsAvg = Vector3.zero;
                if (BotManager.ActiveBots != null)
                {
                    int count = 0;
                    foreach (var other in BotManager.ActiveBots)
                    {
                        if (other != null && other != this && !other.IsDead)
                        { otherBotsAvg += other.transform.position; count++; }
                    }
                    if (count > 0) otherBotsAvg /= count;
                }

                if (trainingMode)
                {
                    // Training explore priorities — aggressive coverage
                    // Stale bots (revisiting explored areas) get forced to distant/unexplored targets
                    bool stale = _exploredStaleCount > 10;
                    if (stale)
                    {
                        _exploredStaleCount = 0; // Reset after forcing new behavior
                        Plugin.Log.LogInfo($"[{BotName}] Explore stale — forcing distant target");
                    }

                    // PRIORITY 0 — Frontier queue. A previous bot gave up on this cell;
                    // try it again from a different angle (≥45° off their approach).
                    if (NavGraph.Instance != null &&
                        NavGraph.Instance.TryPopFrontier(BotId, out Vector3 frontierPos, out Vector3 avoidDir))
                    {
                        // If we have an avoid direction, stage a waypoint 6m away in the
                        // perpendicular (or reverse) direction so our approach to the cell
                        // comes in from a genuinely different angle than last time.
                        Vector3 targetPos = frontierPos;
                        if (avoidDir.sqrMagnitude > 0.1f)
                        {
                            // Perpendicular choice: rotate avoidDir 90° around Y. Coin-flip
                            // which side, so two bots who both pop the cell don't line up.
                            Vector3 perp = Random.value < 0.5f
                                ? new Vector3( avoidDir.z, 0f, -avoidDir.x)
                                : new Vector3(-avoidDir.z, 0f,  avoidDir.x);
                            // Waypoint staged on the opposite side from avoid, 6m offset.
                            Vector3 stage = frontierPos + perp * 6f - avoidDir * 2f;
                            // Only use the stage if it's actually navigable ground (raycast check).
                            if (Physics.Raycast(stage + Vector3.up * 2f, Vector3.down, out var gh, 8f,
                                    GROUND_MASK, QueryTriggerInteraction.Ignore))
                            {
                                targetPos = gh.point;
                            }
                        }
                        _wanderTarget = targetPos;
                        _hasWanderTarget = true;
                        _wanderChangeTimer = Random.Range(12f, 20f) * commitmentMultiplier;
                        Plugin.Log.LogInfo($"[{BotName}] Popped frontier cell (avoid dir={avoidDir})");
                        goto doneWanderPick;
                    }

                    float roll = stale ? 0.85f : Random.value; // Stale = skip to distant spawns

                    // PRIORITY 1 — Coverage heatmap: pick the least-visited reachable cell.
                    // This is the backbone of whole-map trial-and-error learning.
                    if (roll < 0.35f && NavGraph.Instance != null)
                    {
                        var cov = NavGraph.Instance.GetLowestVisitReachableCell(transform.position, 60f);
                        if (cov.HasValue)
                        {
                            _wanderTarget = cov.Value;
                            _hasWanderTarget = true;
                            _wanderChangeTimer = Random.Range(10f, 18f) * commitmentMultiplier;
                            goto doneWanderPick;
                        }
                        roll = 0.4f; // fall through to map locations
                    }

                    // 1. Disconnected map locations — highest priority, long commitment
                    if (roll < 0.5f && NavGraph.Instance != null)
                    {
                        var (unreachPos, unreachLabel) = NavGraph.Instance.FindUnreachableMapLocation(transform.position);
                        if (unreachPos != Vector3.zero)
                        {
                            _wanderTarget = unreachPos;
                            _hasWanderTarget = true;
                            _wanderChangeTimer = Random.Range(15f, 25f) * commitmentMultiplier; // Very long commitment
                        }
                        else roll = 0.55f;
                    }

                    // 2. Frontier — push outward, avoid other bots + areas this bot already explored
                    if (roll >= 0.5f && roll < 0.8f && NavGraph.Instance != null && NavGraph.Instance.HasData)
                    {
                        var frontier = NavGraph.Instance.FindFrontierNode(transform.position, 5f,
                            avoidPos: otherBotsAvg, exploredCells: _exploredCells);
                        if (frontier != null)
                        {
                            _wanderTarget = frontier.Position;
                            _hasWanderTarget = true;
                            _wanderChangeTimer = Random.Range(10f, 18f) * commitmentMultiplier;
                        }
                        else roll = 0.85f;
                    }

                    // 3. Distant spawns — spread out for maximum coverage
                    if (roll >= 0.8f || !_hasWanderTarget)
                    {
                        SpawnPoint[] spawns = GetCachedSpawns();
                        if (spawns.Length > 0)
                        {
                            // Pick spawn far from self AND far from other bots
                            SpawnPoint best = null;
                            float bestScore = float.MinValue;
                            for (int i = 0; i < Mathf.Min(8, spawns.Length); i++)
                            {
                                var sp = spawns[Random.Range(0, spawns.Length)];
                                float selfDist = Vector3.Distance(transform.position, sp.transform.position);
                                float otherDist = otherBotsAvg.sqrMagnitude > 0.01f
                                    ? Vector3.Distance(otherBotsAvg, sp.transform.position) : 0f;
                                float score = selfDist + otherDist * 0.5f; // Prefer far from everyone
                                if (score > bestScore) { bestScore = score; best = sp; }
                            }
                            _wanderTarget = best != null ? best.transform.position :
                                spawns[Random.Range(0, spawns.Length)].transform.position;
                        }
                        else
                        {
                            Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                            _wanderTarget = transform.position + randomDir * Random.Range(15f, 40f);
                        }
                        _hasWanderTarget = true;
                        _wanderChangeTimer = Random.Range(12f, 20f); // Long commitment
                    }
                    doneWanderPick: ;
                }
                else
                {
                    // Play mode: use reachable map locations with unbroken paths first
                    float roll2 = Random.value;

                    // 60% — follow unbroken path to a reachable map location (weapon/spawn)
                    if (roll2 < 0.6f && NavGraph.Instance != null && NavGraph.Instance.HasData)
                    {
                        var (locPos, locLabel, locPath) = NavGraph.Instance.FindReachableMapLocation(transform.position);
                        if (locPath.Count > 0)
                        {
                            _graphPath = locPath;
                            _graphPathIndex = 0;
                            _lastReachedNode = null;
                            _prevReachedNode = null;
                            _wanderTarget = locPos;
                            _hasWanderTarget = true;
                        }
                        else roll2 = 0.7f;
                    }

                    // 20% — go to nearest weapon (direct)
                    if (roll2 >= 0.6f && roll2 < 0.8f)
                    {
                        ItemBehaviour nearestWeapon = FindNearestWeapon();
                        if (nearestWeapon != null)
                        {
                            _wanderTarget = nearestWeapon.transform.position;
                            _hasWanderTarget = true;
                        }
                        else roll2 = 0.9f;
                    }

                    // 20% — explore spawn points
                    if (roll2 >= 0.8f || !_hasWanderTarget)
                    {
                        SpawnPoint[] spawns = GetCachedSpawns();
                        if (spawns.Length > 0)
                        {
                            _wanderTarget = spawns[Random.Range(0, spawns.Length)].transform.position;
                        }
                        else
                        {
                            Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                            _wanderTarget = transform.position + randomDir * Random.Range(10f, 25f);
                        }
                        _hasWanderTarget = true;
                    }
                }

            }
            MoveToward(_wanderTarget, _sprintSpeed);
        }

        // ===================== VALIDATION MODE =====================
    }
}
