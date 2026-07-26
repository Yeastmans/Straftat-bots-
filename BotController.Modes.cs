using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    public partial class BotController
    {

        // Recently visited/assigned wander spots (xyz + timestamp in w). Stage-1
        // pickers reject anything within 7m of an entry younger than 45s so bots
        // spread onto fresh ground instead of looping familiar routes.
        private readonly List<Vector4> _recentVisits = new List<Vector4>(24);

        private void RememberVisit(Vector3 pos)
        {
            _recentVisits.Add(new Vector4(pos.x, pos.y, pos.z, Time.time));
            if (_recentVisits.Count > 24) _recentVisits.RemoveAt(0);
        }

        private bool IsRecentlyVisited(Vector3 pos)
        {
            float now = Time.time;
            for (int i = _recentVisits.Count - 1; i >= 0; i--)
            {
                Vector4 v = _recentVisits[i];
                if (now - v.w > 45f) { _recentVisits.RemoveAt(i); continue; }
                float dx = pos.x - v.x, dz = pos.z - v.z;
                if (dx * dx + dz * dz < 49f && Mathf.Abs(pos.y - v.y) < 3f) return true;
            }
            return false;
        }

        /// <summary>Another live bot is already heading within 8m of this spot — pick
        /// something else so the pack fans out instead of converging.</summary>
        private bool IsOtherBotTargetingNear(Vector3 pos)
        {
            var bots = BotManager.ActiveBots;
            if (bots == null) return false;
            foreach (var other in bots)
            {
                if (other == null || other == this || other.IsDead) continue;
                if (!other._hasWanderTarget) continue;
                float dx = other._wanderTarget.x - pos.x, dz = other._wanderTarget.z - pos.z;
                if (dx * dx + dz * dz < 64f) return true;
            }
            return false;
        }

        private bool RejectStage1Cell(Vector3 pos)
            => IsRecentlyVisited(pos) || IsOtherBotTargetingNear(pos)
            || (NavGraph.Instance != null && NavGraph.Instance.FallDeathPenalty(pos) >= 1.6f)
            || IsHazardTarget(pos);

        /// <summary>Target sits inside a lethal volume (kill water). The safeties refuse
        /// to walk in, so assigning it guarantees a stall-and-grind at the shoreline.</summary>
        private bool IsHazardTarget(Vector3 pos)
        {
            RefreshKillZoneCache();
            return IsKillZoneAt(pos + Vector3.up * 0.3f);
        }

        // Breadcrumb trail: the GROUND the bot actually walked, sampled every 0.5s.
        // RememberVisit only covers assigned TARGETS — the corridor walked between
        // targets was unprotected, so the picker's nearest-unwalked fallback kept
        // sending bots straight back down the lane they just swept (the visible
        // stage-1 ping-pong). Cells near a fresh breadcrumb are rejected on the
        // first pick pass; a second pass without the trail filter keeps dead-end
        // corridors walkable back out.
        private readonly List<Vector4> _walkTrail = new List<Vector4>(48);
        private float _trailSampleTimer;

        private void SampleWalkTrail()
        {
            _trailSampleTimer -= Time.deltaTime;
            if (_trailSampleTimer > 0f || _cc == null || !_cc.isGrounded) return;
            _trailSampleTimer = 0.5f;
            Vector3 p = transform.position;
            _walkTrail.Add(new Vector4(p.x, p.y, p.z, Time.time));
            if (_walkTrail.Count > 48) _walkTrail.RemoveAt(0);
        }

        private bool IsOnRecentTrail(Vector3 pos)
        {
            float now = Time.time;
            for (int i = _walkTrail.Count - 1; i >= 0; i--)
            {
                Vector4 v = _walkTrail[i];
                if (now - v.w > 18f) { _walkTrail.RemoveAt(i); continue; }
                float dx = pos.x - v.x, dz = pos.z - v.z;
                if (dx * dx + dz * dz < 9f && Mathf.Abs(pos.y - v.y) < 3f) return true;
            }
            return false;
        }

        private bool RejectStage1CellOrTrail(Vector3 pos)
            => RejectStage1Cell(pos) || IsOnRecentTrail(pos);

        // ---- Route anti-repeat (ALL modes) ----
        // Graph edges this bot walked recently cost extra in ITS pathfinding for 25s,
        // so consecutive routes through the same zone land on different corridors.
        // A* went deterministic on purpose (route flip-flop) — without this, a bot
        // whose objectives keep it in one zone re-derives the identical path every
        // repath and visibly grinds the same lane.
        private readonly Dictionary<long, float> _recentEdgeUse = new Dictionary<long, float>(64);
        private System.Func<int, int, float> _routeRepeatPenalty; // cached delegate, no per-call alloc
        private System.Func<int, int, float> RoutePenaltyFunc
            => _routeRepeatPenalty ?? (_routeRepeatPenalty = RouteRepeatPenalty);

        private const float EDGE_REPEAT_MEMORY = 25f;
        private const float EDGE_REPEAT_PENALTY = 3.5f;

        private static long EdgeUseKey(int from, int to) => ((long)(uint)from << 32) | (uint)to;

        private void StampEdgeUse(int from, int to)
        {
            if (from < 0 || to < 0) return; // synthetic navmesh-corner ids
            float now = Time.time;
            _recentEdgeUse[EdgeUseKey(from, to)] = now;
            _recentEdgeUse[EdgeUseKey(to, from)] = now; // reverse = the oscillation we're killing
            if (_recentEdgeUse.Count > 96)
            {
                _edgeUsePruneBuf.Clear();
                foreach (var kv in _recentEdgeUse)
                    if (now - kv.Value > EDGE_REPEAT_MEMORY) _edgeUsePruneBuf.Add(kv.Key);
                foreach (long k in _edgeUsePruneBuf) _recentEdgeUse.Remove(k);
            }
        }
        private static readonly List<long> _edgeUsePruneBuf = new List<long>(96);

        private float RouteRepeatPenalty(int from, int to)
        {
            if (!_recentEdgeUse.TryGetValue(EdgeUseKey(from, to), out float t)) return 1f;
            float age = Time.time - t;
            if (age >= EDGE_REPEAT_MEMORY) return 1f;
            return Mathf.Lerp(EDGE_REPEAT_PENALTY, 1f, age / EDGE_REPEAT_MEMORY);
        }

        // ---- Zone-dwell breaker (ALL non-combat states) ----
        // A bot pinned inside a ~12m circle for 25s without fighting is grinding a
        // zone. Mark the spot visited (45s picker rejection), dump the route, and
        // force the next wander pick to go distant.
        private Vector3 _dwellAnchor;
        private float _dwellTimer;

        private void UpdateZoneDwell()
        {
            Vector3 pos = transform.position;
            if (HorizontalDist(pos, _dwellAnchor) > 12f)
            {
                _dwellAnchor = pos;
                _dwellTimer = 0f;
                return;
            }
            bool validating = Plugin.IsValidateMode && _validationRouteNodeIds.Count > 1;
            if (State == BotState.Hunt || State == BotState.Dead || _onLadder || validating) return;
            _dwellTimer += Time.deltaTime;
            if (_dwellTimer < 25f) return;

            Plugin.Log.LogInfo($"[{BotName}] Zone dwell — 25s inside 12m at {pos}, breaking out");
            RememberVisit(pos);
            _graphPath.Clear();
            _graphPathIndex = 0;
            _hasWanderTarget = false;
            _wanderChangeTimer = 0f;
            _repathTimer = 0f;
            _exploredStaleCount = 11; // training pickers read this as "force distant"
            // A weapon objective pins Play-mode bots to a zone exactly like a wander
            // target does — drop and blacklist it too or the breakout re-acquires it.
            if (_targetItem != null) { _blacklistedWeapons[_targetItem] = Time.time; _targetItem = null; }
            _weaponTarget = null;
            _dwellAnchor = pos;
            _dwellTimer = 0f;
        }

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
        private Vector3 _smartExploreFailPos;      // Last target a full session failed at
        private float _smartExploreFailTime = -999f; // When — gates immediate re-sessions

        private void BeginSmartExplore(Vector3 target)
        {
            _exploreStartPos = transform.position;
            _exploreStateAttempts = 0;
            _edgeWalkFlipped = false;
            _probeJumpAttempted = false;
            _probeTarget = Vector3.zero;

            // Short session — bots retry from a fresh angle instead of brute-forcing
            // the same approach for minutes at a time.
            _exploreTotalTimer = 20f;

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
            // Only seed from a stable grounded stance — a bot mid-air, mid-slide, or
            // falling is NOT proof the spot is a usable standing position. This stops
            // SmartExplore's "got closer" heuristic from planting phantom anchors.
            if (_cc == null || !_cc.isGrounded || _isSliding) return;
            // force:true so Play-mode bots can still seed (we now learn during Play too).
            var node = NavGraph.Instance.AddPosition(pos, isPlayer: false, force: true);
            // Bot-discovered nodes stay low-trust CANDIDATES — they must be validated like
            // any other bot data before bots rely on them. We no longer force high
            // confidence from a distance heuristic. (highConfidence kept for signature
            // compatibility; intentionally a no-op now.)
            if (node != null && node.VisitCount < 1) node.VisitCount = 1;
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

        private bool TryCommitExploreRoute(Vector3 target, out Vector3 routeEnd)
        {
            routeEnd = target;

            // Navmesh first — on baked maps the corner route IS the explore route. This was
            // the missing piece: explore committed graph routes directly, so bots never
            // used the mesh at all in Training (src=NavMeshRoute never appeared in logs).
            if (BotNavMesh.Ready)
            {
                var nm = BotNavMesh.FindCornerPath(transform.position, target, out bool nmComplete);
                if (nm != null && nm.Count > 1)
                {
                    Vector3 nmEnd = nm[nm.Count - 1].Position;
                    bool useful = nmComplete
                        || Vector3.Distance(transform.position, target) - Vector3.Distance(nmEnd, target) > 4f;
                    if (useful)
                    {
                        routeEnd = nmComplete ? target : nmEnd;
                        AcceptGraphRoute(nm, PathSource.NavMeshRoute, routeEnd, ScorePathCandidate(nm, target));
                        _lastPathTarget = routeEnd;
                        _repathTimer = Mathf.Max(_repathTimer, 3.5f);
                        _lastReachedNode = null;
                        return true;
                    }
                }
            }

            if (NavGraph.Instance == null || !NavGraph.Instance.HasData) return false;

            List<NavNode> bestPath = null;
            float bestScore = float.MinValue;
            Vector3 bestEnd = target;

            void Consider(List<NavNode> path, Vector3 scoreTarget)
            {
                if (path == null || path.Count <= 1) return;
                if (IsImmediateBacktrack(path)) return;
                float score = ScorePathCandidate(path, scoreTarget);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = path;
                    bestEnd = scoreTarget;
                }
            }

            bool wantsHeight = Mathf.Abs(target.y - transform.position.y) > 2.25f;
            Consider(NavGraph.Instance.FindPath(transform.position, target,
                jitter: 0.02f, searchRadius: 90f, preferHeight: wantsHeight, edgeCostScale: RoutePenaltyFunc), target);
            Consider(NavGraph.Instance.FindPath(transform.position, target,
                jitter: 0.04f, searchRadius: 90f, playerOnly: true, preferHeight: wantsHeight, edgeCostScale: RoutePenaltyFunc), target);

            // If the exact target is not connected, route to the closest reachable
            // staging node near it instead of walking directly into geometry.
            if (bestPath == null)
            {
                NavNode staging = NavGraph.Instance.FindClosestReachableNode(transform.position, target);
                if (staging != null && HorizontalDist(transform.position, staging.Position) > 2f)
                {
                    Consider(NavGraph.Instance.FindPath(transform.position, staging.Position,
                        jitter: 0.02f, searchRadius: 90f, preferHeight: wantsHeight, edgeCostScale: RoutePenaltyFunc), staging.Position);
                }
            }

            if (bestPath == null) return false;

            routeEnd = bestEnd;
            AcceptGraphRoute(bestPath, PathSource.ExploreBuildRoute, routeEnd, bestScore);
            _lastPathTarget = routeEnd;
            _repathTimer = Mathf.Max(_repathTimer, 1.75f);
            _lastReachedNode = null;
            _prevReachedNode = null;
            return true;
        }

        // Closest the bot has been to its current wander target — lets the change
        // timer extend while genuine progress continues instead of re-picking
        // (and often reversing) mid-route.
        private float _wanderBestDist = float.MaxValue;

        private bool TryAssignExploreTarget(Vector3 target, float commitment, bool requireRoute)
        {
            if (target == Vector3.zero) return false;

            if (TryCommitExploreRoute(target, out Vector3 routeEnd))
            {
                _wanderTarget = routeEnd;
                _hasWanderTarget = true;
                _wanderChangeTimer = commitment;
                _wanderBestDist = HorizontalDist(transform.position, routeEnd);
                return true;
            }

            bool graphReady = NavGraph.Instance != null && NavGraph.Instance.HasData;
            float flatDist = HorizontalDist(transform.position, target);
            float heightDist = Mathf.Abs(target.y - transform.position.y);

            // With graph data present, far/high targets should never be assigned as
            // raw direct movement. Those are exactly the cases that need complex
            // jump/ladder routes, so skip them if no corridor is found.
            if (requireRoute || (graphReady && (flatDist > 18f || heightDist > 3.5f)))
                return false;

            _wanderTarget = target;
            _hasWanderTarget = true;
            _wanderChangeTimer = commitment;
            _wanderBestDist = flatDist;
            SwitchPathSource(PathSource.ExploreBuildRoute);
            return true;
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
                float[] angles = { 0f, 12f, -12f, 25f, -25f, 40f, -40f, 55f, -55f };
                float[] distances = { 3f, 5f, 7f, 9f, 11f, 13f };

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
                            bool jumpable = horizDist < 14f && heightGain < 2.3f && heightGain > -9f;
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
                // Do NOT pre-create a Jump edge here — the bot has not landed yet.
                // If the jump actually succeeds, RecordBot's landing logic creates the
                // edge from a real takeoff->landing. Pre-seeding made jump edges into the void.
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

        private float _validationSearchTimer; // Route search is EXPENSIVE — throttle it

        private void HandleTrainingValidation()
        {
            if (NavGraph.Instance == null || !NavGraph.Instance.HasData)
            {
                Wander();
                return;
            }

            bool routeActive = _validationRouteNodeIds.Count > 1
                && _graphPath.Count > 0
                && _graphPathIndex < _graphPath.Count;

            if (routeActive)
            {
                _validationRouteTimer += Time.deltaTime;
                float distToTarget = HorizontalDist(transform.position, _validationRouteTarget);

                // Only CONFIRM a route the bot actually walked to the end while grounded —
                // not one it drifted toward or fell near. And credit ONLY the edges it
                // genuinely traversed (the reached prefix), never edges it skipped/fell past.
                bool standing = _cc.isGrounded && !_isSliding && _intentionalJumpTimer <= 0f && _verticalVelocity > -2f;
                bool reachedEnd = _graphPathIndex >= _graphPath.Count - 1;
                if (standing && (reachedEnd || distToTarget < 1.5f))
                {
                    int reached = Mathf.Clamp(_graphPathIndex + 1, 0, _validationRouteNodeIds.Count);
                    if (reached >= 2)
                        NavGraph.Instance.ReportRouteValidation(
                            _validationRouteNodeIds.GetRange(0, reached), success: true, _validationRouteLabel);
                    // ANTI-PING-PONG: a just-validated route goes on cooldown for everyone,
                    // and this bot remembers the spot — otherwise the next search instantly
                    // picks the reverse of the same corridor and bots shuttle back and forth.
                    if (!string.IsNullOrWhiteSpace(_validationRouteLabel))
                        NavGraph.Instance.SuppressValidationLabel(_validationRouteLabel, 12f);
                    RememberVisit(_validationRouteTarget);
                    _validationRouteNodeIds.Clear();
                    _validationRouteTimer = 0f;
                    _hasWanderTarget = false;
                    _graphPath.Clear();
                    _graphPathIndex = 0;
                    _validationSearchTimer = 0.6f + Random.value * 0.6f;
                    return;
                }

                if (_validationRouteTimer > 24f || _progressState == ProgressState.HardStuck || _stuckTimer > 2.4f)
                {
                    string routeLabel = string.IsNullOrWhiteSpace(_validationRouteLabel) ? "route" : _validationRouteLabel;
                    NavGraph.Instance.ReportRouteValidation(_validationRouteNodeIds, success: false,
                        $"{BotName} stuck validating {routeLabel}");
                    NavGraph.Instance.SuppressValidationLabel(routeLabel, 18f,
                        $"PATH FOLLOWER FAILED: {routeLabel}. Bots are trying alternate validation routes.");
                    if (_graphPathIndex > 0 && _graphPathIndex < _graphPath.Count)
                    {
                        int fromId = _graphPath[Mathf.Max(0, _graphPathIndex - 1)].Id;
                        int toId = _graphPath[_graphPathIndex].Id;
                        var edge = NavGraph.Instance.GetEdgeBetween(fromId, toId);
                        if (edge != null) NavGraph.Instance.ReportEdgeValidation(edge, success: false);
                    }
                    _validationRouteNodeIds.Clear();
                    _validationRouteTimer = 0f;
                    _hasWanderTarget = false;
                    _graphPath.Clear();
                    _graphPathIndex = 0;
                    _stuckTimer = 0f;
                    _validationSearchTimer = 1.2f + Random.value * 0.8f;
                    return;
                }

                MoveToward(_validationRouteTarget, _sprintSpeed);
                return;
            }

            // PERFORMANCE: TryGetValidationRoute runs a full graph pathfind per candidate.
            // It used to be called EVERY FRAME per bot whenever no route was active (worst
            // when all labels were suppressed — permanent per-frame pathfind storm). Now a
            // bot searches at most every couple of seconds and wanders in between.
            _validationSearchTimer -= Time.deltaTime;
            if (_validationSearchTimer > 0f)
            {
                Wander();
                return;
            }

            if (NavGraph.Instance.TryGetValidationRoute(transform.position, BotId,
                out Vector3 target, out List<NavNode> route, out string label))
            {
                _graphPath = route;
                _graphPathIndex = 0;
                _lastReachedNode = null;
                _prevReachedNode = null;
                _validationRouteNodeIds.Clear();
                for (int i = 0; i < route.Count; i++)
                    _validationRouteNodeIds.Add(route[i].Id);
                _validationRouteTarget = target;
                _validationRouteLabel = label ?? "Route";
                _validationRouteTimer = 0f;
                _wanderTarget = target;
                _hasWanderTarget = true;
                _wanderChangeTimer = 18f;
                SwitchPathSource(PathSource.GraphRoute);
                MoveToward(_validationRouteTarget, _sprintSpeed);
                return;
            }

            // No pending special-edge work (or everything is on cooldown). Run ANCHOR
            // CIRCUITS instead of aimless wandering: physically walk to each key map
            // location once. This is what the stage-3 anchor bar now measures — bots
            // visibly working the map instead of milling around — and reaching the
            // anchor marks it walked via the passive location-visit tracker.
            _validationSearchTimer = 2f + Random.value * 1.5f;
            Vector3 circuit = NavGraph.Instance.FindNextCircuitAnchor(transform.position, BotId, IsRecentlyVisited);
            if (circuit != Vector3.zero)
                TryAssignExploreTarget(circuit, 18f, requireRoute: false);
            Wander();
        }

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
                if (_exploreTotalTimer <= 0f || _exploreStateAttempts >= 7)
                {
                    // Session FAILED (success exits inside SmartExplore before the
                    // timer runs out). Burn the target for 45s across ALL pickers and
                    // drop it NOW — keeping it meant walking back to the same wall and
                    // re-running the whole stuck→SmartExplore loop (visible ping-pong).
                    if (_hasWanderTarget && _wanderTarget != Vector3.zero)
                        RememberVisit(_wanderTarget);
                    _smartExploreFailPos = _wanderTarget;
                    _smartExploreFailTime = Time.time;
                    _hasWanderTarget = false;
                    _wanderChangeTimer = 0f;
                    _exploreState = ExploreState.None;
                    _stuckTimer = 0f;
                    Plugin.Log.LogInfo($"[{BotName}] SmartExplore failed — burning target {_smartExploreFailPos}");
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
                // SmartExplore's ground tactics (EdgeWalk pacing, probing) are what the
                // player SEES as ping-pong. On a baked map its only remaining job is
                // discovering jump/ladder links to spots the mesh can't route to — for a
                // plain stuck-on-walkable-ground case, just pick a fresh target instead.
                bool meshCantRoute = true;
                if (BotNavMesh.Ready)
                {
                    var probe = BotNavMesh.FindCornerPath(transform.position, _wanderTarget, out bool probeComplete);
                    meshCantRoute = probe == null || !probeComplete;
                }
                if (meshCantRoute)
                {
                    // Repeat guard: a SmartExplore session at (nearly) this target just
                    // failed. Running another is the wall ping-pong the player sees —
                    // burn the target and fall through to the re-pick instead.
                    bool repeatFail = Time.time - _smartExploreFailTime < 30f
                        && HorizontalDist(_wanderTarget, _smartExploreFailPos) < 6f;
                    if (!repeatFail)
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
                    RememberVisit(_wanderTarget);
                    _exploredStaleCount = 11; // stage pickers read this as "force distant"
                }

                _hasWanderTarget = false; // routable but stuck — re-pick below, no pacing
                _stuckTimer = 0f;
                _graphPath.Clear();
                _graphPathIndex = 0;
                _repathTimer = 0f;
            }

            // Timer expiry must not abandon a run that is WORKING — re-picking mid-route
            // is a direction flip the player sees as ping-pong. While the bot keeps
            // setting new closest-distance records toward the target, extend.
            if (_hasWanderTarget && _wanderChangeTimer <= 0f && _wanderTarget != Vector3.zero)
            {
                float distNow = HorizontalDist(transform.position, _wanderTarget);
                if (distNow < _wanderBestDist - 2f && _stuckTimer < 1f)
                {
                    _wanderBestDist = distNow;
                    _wanderChangeTimer = 6f;
                }
            }

            if (!_hasWanderTarget || HorizontalDist(transform.position, _wanderTarget) < 3f
                || _wanderChangeTimer <= 0f || _wanderTarget == Vector3.zero)
            {
                // Arriving at a target records it — recently visited spots are rejected
                // by the stage-1 pickers so bots never loop over the same ground.
                if (_hasWanderTarget && _wanderTarget != Vector3.zero
                    && HorizontalDist(transform.position, _wanderTarget) < 3f)
                    RememberVisit(_wanderTarget);

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

                    // STAGE 1 — EXPLORE: unwalked ground IS the objective. Relentlessly
                    // target unwalked coverage cells; a walked cell can never be returned
                    // by the picker again, so bots physically cannot re-cover old ground.
                    // Recently visited/assigned spots are rejected for 45s as retry backoff.
                    // Two passes: first also rejects cells on the bot's own recent walk
                    // trail (no U-turn back down the lane just swept); if that starves the
                    // pick (dead-end corridor), the plain pass lets the bot walk back out.
                    if (NavGraph.Instance != null && NavGraph.Instance.TrainingStage == 1)
                    {
                        Vector3 pickHeading = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
                        if (BotNavMesh.TryGetUnwalkedCellTarget(transform.position, RejectStage1CellOrTrail, out Vector3 unwalked,
                                heading: pickHeading)
                            || BotNavMesh.TryGetUnwalkedCellTarget(transform.position, RejectStage1Cell, out unwalked,
                                heading: pickHeading))
                        {
                            if (TryAssignExploreTarget(unwalked, Random.Range(10f, 16f) * commitmentMultiplier, requireRoute: false))
                            {
                                RememberVisit(unwalked); // backoff even if the run gets abandoned
                                goto doneWanderPick;
                            }
                        }
                    }

                    // STAGE 2 — WEAPONS: every weapon must be PHYSICALLY visited by a
                    // bot this session — that's what the stage bar measures now. (The
                    // old objective, mesh-unlinked weapons, was empty the moment the
                    // bake finished, so stage 2 read 100% and bots had nothing to do.)
                    if (NavGraph.Instance != null && NavGraph.Instance.TrainingStage == 2 && Random.value < 0.75f)
                    {
                        var (wPos, wLabel) = NavGraph.Instance.FindNearestUnvisitedWeapon(transform.position, IsRecentlyVisited);
                        if (wPos != Vector3.zero
                            && TryAssignExploreTarget(wPos, Random.Range(14f, 22f) * commitmentMultiplier, requireRoute: false))
                        {
                            Plugin.Log.LogInfo($"[{BotName}] Stage 2: visiting weapon '{wLabel}'");
                            goto doneWanderPick;
                        }
                    }

                    // PRIORITY 0 — Frontier queue. A previous bot gave up on this cell;
                    // try it again from a different angle (≥45° off their approach).
                    if (NavGraph.Instance != null &&
                        NavGraph.Instance.TryPopFrontier(BotId, out Vector3 frontierPos, out Vector3 avoidDir)
                        && !IsRecentlyVisited(frontierPos))
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
                        if (TryAssignExploreTarget(targetPos, Random.Range(12f, 20f) * commitmentMultiplier, requireRoute: false))
                        {
                            Plugin.Log.LogInfo($"[{BotName}] Popped frontier cell (avoid dir={avoidDir})");
                            goto doneWanderPick;
                        }
                    }

                    float roll = stale ? 0.85f : Random.value; // Stale = skip to distant spawns

                    // PRIORITY 1 — Coverage heatmap: pick the least-visited reachable cell.
                    // This is the backbone of whole-map trial-and-error learning.
                    if (roll < 0.35f && NavGraph.Instance != null)
                    {
                        var cov = NavGraph.Instance.GetLowestVisitReachableCell(transform.position, 60f);
                        // A cell the bot keeps failing to reach stays lowest-visit forever —
                        // the visited check is what breaks that permanent magnet.
                        if (cov.HasValue && !IsRecentlyVisited(cov.Value))
                        {
                            if (TryAssignExploreTarget(cov.Value, Random.Range(10f, 18f) * commitmentMultiplier, requireRoute: true))
                                goto doneWanderPick;
                        }
                        roll = 0.4f; // fall through to map locations
                    }

                    // 1. Disconnected map locations — highest priority, long commitment
                    if (roll < 0.5f && NavGraph.Instance != null)
                    {
                        var (unreachPos, unreachLabel) = NavGraph.Instance.FindUnreachableMapLocation(transform.position);
                        if (unreachPos != Vector3.zero && !IsRecentlyVisited(unreachPos))
                        {
                            if (!TryAssignExploreTarget(unreachPos, Random.Range(15f, 25f) * commitmentMultiplier, requireRoute: false))
                                roll = 0.55f;
                        }
                        else roll = 0.55f;
                    }

                    // 2. Frontier — push outward, avoid other bots + areas this bot already explored
                    if (roll >= 0.5f && roll < 0.8f && NavGraph.Instance != null && NavGraph.Instance.HasData)
                    {
                        var frontier = NavGraph.Instance.FindFrontierNode(transform.position, 5f,
                            avoidPos: otherBotsAvg, exploredCells: _exploredCells);
                        if (frontier != null && !IsRecentlyVisited(frontier.Position))
                        {
                            if (!TryAssignExploreTarget(frontier.Position, Random.Range(10f, 18f) * commitmentMultiplier, requireRoute: true))
                                roll = 0.85f;
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
                            Vector3 spawnTarget = best != null ? best.transform.position :
                                spawns[Random.Range(0, spawns.Length)].transform.position;
                            if (!TryAssignExploreTarget(spawnTarget, Random.Range(12f, 20f) * commitmentMultiplier, requireRoute: false))
                                _hasWanderTarget = false;
                        }
                        else
                        {
                            Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                            TryAssignExploreTarget(transform.position + randomDir * Random.Range(15f, 40f),
                                Random.Range(12f, 20f) * commitmentMultiplier, requireRoute: false);
                        }
                    }
                    if (!_hasWanderTarget)
                    {
                        Vector3 localDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                        if (localDir.sqrMagnitude < 0.01f) localDir = transform.forward;
                        TryAssignExploreTarget(transform.position + localDir * Random.Range(8f, 14f),
                            Random.Range(8f, 12f) * commitmentMultiplier, requireRoute: false);
                    }
                    doneWanderPick: ;
                }
                else
                {
                    // Play mode: still learn aggressively; prefer reachable routes but keep
                    // frontier/coverage pressure so bots continue discovering complex traversals.
                    float roll2 = Random.value;

                    // 45% — follow unbroken path to a reachable map location (weapon/spawn)
                    if (roll2 < 0.45f && NavGraph.Instance != null && NavGraph.Instance.HasData)
                    {
                        var (locPos, locLabel, locPath) = NavGraph.Instance.FindReachableMapLocation(transform.position);
                        if (locPath.Count > 0 && !IsRecentlyVisited(locPos))
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

                    // 25% — push frontier in play mode too (continuous learning)
                    if (roll2 >= 0.45f && roll2 < 0.7f && NavGraph.Instance != null && NavGraph.Instance.HasData)
                    {
                        var frontier = NavGraph.Instance.FindFrontierNode(transform.position, 5f, avoidPos: otherBotsAvg, exploredCells: _exploredCells);
                        if (frontier != null && !IsRecentlyVisited(frontier.Position))
                        {
                            _wanderTarget = frontier.Position;
                            _hasWanderTarget = true;
                            _wanderChangeTimer = Random.Range(9f, 16f) * commitmentMultiplier;
                        }
                        else roll2 = 0.78f;
                    }

                    // 20% — go to nearest weapon (direct)
                    if (roll2 >= 0.7f && roll2 < 0.9f)
                    {
                        ItemBehaviour nearestWeapon = FindNearestWeapon();
                        if (nearestWeapon != null)
                        {
                            _wanderTarget = nearestWeapon.transform.position;
                            _hasWanderTarget = true;
                        }
                        else roll2 = 0.9f;
                    }

                    // 10% — explore spawn points
                    if (roll2 >= 0.9f || !_hasWanderTarget)
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
