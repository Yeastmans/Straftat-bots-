using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StraftatBots
{
    public partial class BotController
    {
        // ===================== MOVEMENT =====================

        /// <summary>Single cc.Move wrapper — sets _movedThisFrame flag to prevent double gravity.</summary>
        private void DoMove(Vector3 motion)
        {
            if (_cc == null || !_cc.enabled) return;
            if (!_movedThisFrame)
            {
                // Ice slide + continuous ForceZone push ride on top of whatever movement
                // runs this frame — the FPC adds slopeSlideMove/steepSlopeSlideMove and
                // ForceZone force into its single Move the same way.
                Vector3 ice = _iceSlideMove + _iceCrouchSlideMove;
                Vector3 iceHoriz = new Vector3(ice.x, 0f, ice.z);
                // The ice push must never carry a bot over a void lip: the final-safety
                // footprint check runs on the COMMANDED move before DoMove, so a shove
                // added here bypassed it entirely — that's the "slides into the void on
                // ice" death. A human steers/brakes against the push at a lip; the bot
                // kills the push and commits an escape direction instead. Zone forces
                // stay untouched (launch pads are meant to throw you).
                if (iceHoriz.sqrMagnitude > 0.01f && _cc.isGrounded && _intentionalJumpTimer <= 0f)
                {
                    float lookahead = Mathf.Clamp(iceHoriz.magnitude * 0.12f, 0.6f, 1.6f);
                    if (!HasGroundFootprintAhead(iceHoriz, lookahead))
                    {
                        _iceSlideMove = Vector3.zero;
                        _iceCrouchSlideMove = Vector3.zero;
                        ice = Vector3.zero;
                        if (TryGetSafeEdgeEscapeDir(iceHoriz, out Vector3 iceEscape))
                        {
                            _commitDir = iceEscape;
                            _commitTimer = Mathf.Max(_commitTimer, 0.5f);
                        }
                    }
                }
                Vector3 extra = ice + _zoneFrameForce;
                if (extra.sqrMagnitude > 0.0001f)
                    motion += extra * Time.deltaTime;
            }
            _cc.Move(motion);
            _movedThisFrame = true;
        }

        /// <summary>
        /// Ice detection + slide vectors, mirroring the game's SlopeSlide component.
        /// Tag raycast straight down (SlopeSlide uses 8m on landLayer); on Footsteps/Ice
        /// and Footsteps/SuperIce the bot gets the same downhill push players get.
        /// Tuning constants are the shipped PlayerIK.prefab values (iceSpeed=26,
        /// minWalkIceSlideSpeed=4, maxWalkIceSlideSpeed=20, iceAcceleration=2,
        /// iceDeceleration=1, steepSlopeSlideDeceleration=10, clamps 10/30, walk clamps 0/50),
        /// NOT the SlopeSlide.cs field initializers — the prefab overrides them.
        /// </summary>
        private void UpdateIceState()
        {
            if (_cc == null || !_cc.enabled) return;

            RaycastHit hit;
            bool downRay = Physics.Raycast(transform.position, Vector3.down, out hit, 8f, GROUND_MASK);
            if (downRay)
            {
                if (hit.transform.CompareTag("Footsteps/Ice"))           { _onIce = true;  _onSuperIce = false; }
                else if (hit.transform.CompareTag("Footsteps/SuperIce")) { _onIce = true;  _onSuperIce = true;  }
                else                                                     { _onIce = false; _onSuperIce = false; }
                _iceSlopeAngle = Vector3.Angle(hit.normal, Vector3.up);
            }
            // No hit: keep last surface flags like SlopeSlide does — grounded checks below
            // stop the vectors from building while airborne anyway.

            bool grounded = _cc.isGrounded;

            // Walk-on-ice push (SlopeSlide.steepSlopeSlideMove): horizontal component of the
            // surface normal = downhill. Zero on flat ice, ramps hard with slope angle.
            if (downRay && _onIce && grounded)
            {
                float walkFactor = Mathf.Clamp(_iceSlopeAngle, 0f, 50f) / 20f; // divisor is (secondClamp-firstClamp), game's own math
                Vector3 target = new Vector3(hit.normal.x, 0f, hit.normal.z) * Mathf.Lerp(4f, 20f, walkFactor);
                _iceSlideMove = Vector3.Lerp(_iceSlideMove, target, 2f * Time.deltaTime);
            }
            else
            {
                _iceSlideMove = Vector3.Lerp(new Vector3(_iceSlideMove.x, 0f, _iceSlideMove.z),
                    Vector3.zero, 10f * Time.deltaTime);
            }

            // Crouch slide on ice slopes (SlopeSlide.slopeSlideMove, ice branch only —
            // the bot's own slide system covers normal sprint slides).
            if (downRay && _onIce && grounded && _isCrouching && _iceSlopeAngle > 13f)
            {
                float speedFactor = Mathf.Clamp(_iceSlopeAngle, 10f, 30f) / 20f;
                Vector3 target = new Vector3(hit.normal.x, -hit.normal.y, hit.normal.z) * speedFactor * 26f;
                _iceCrouchSlideMove = Vector3.Lerp(_iceCrouchSlideMove, target, 2f * Time.deltaTime);
            }
            else
            {
                // iceDeceleration=1 while still on ice (slide keeps carrying), concrete's 3 once off
                _iceCrouchSlideMove = Vector3.Lerp(new Vector3(_iceCrouchSlideMove.x, 0f, _iceCrouchSlideMove.z),
                    Vector3.zero, (_onIce ? 1f : 3f) * Time.deltaTime);
            }
        }

        private bool TryFollowTeleporterEdge(NavNode fromNode, NavNode toNode, NavEdge edge, float speed)
        {
            if (fromNode == null || toNode == null) return false;

            Teleporter teleporter = FindTeleporterForEdge(fromNode.Position, toNode.Position);
            if (teleporter == null)
            {
                if (edge != null)
                {
                    edge.Confidence = -1f;
                    Plugin.Log.LogInfo($"[{BotName}] Rejected stale teleporter edge {edge.From}->{edge.To}");
                }
                _graphPath.Clear();
                _graphPathIndex = 0;
                _repathTimer = 0f;
                return false;
            }

            Collider trigger = teleporter.GetComponent<Collider>();
            if (trigger == null) trigger = teleporter.GetComponentInChildren<Collider>();

            Vector3 entryPoint = GetTeleporterEntryPoint(teleporter, transform.position);
            Vector3 flatToEntry = entryPoint - transform.position;
            flatToEntry.y = 0f;
            float distToEntry = flatToEntry.magnitude;

            Vector3 moveDir = distToEntry > 0.05f
                ? flatToEntry / distToEntry
                : GetTeleporterPushDir(teleporter, toNode.Position);

            if (trigger != null && trigger.enabled)
            {
                Vector3 bodyPoint = transform.position + Vector3.up * 0.8f;
                Vector3 closest = trigger.ClosestPoint(bodyPoint);
                Vector3 flatToClosest = closest - bodyPoint;
                flatToClosest.y = 0f;
                bool closeEnough = trigger.bounds.Contains(bodyPoint)
                    || flatToClosest.sqrMagnitude < 0.8f * 0.8f
                    || distToEntry < 0.9f;

                if (closeEnough)
                    TryTeleport(trigger);
            }

            _currentHorizInput = 1f;
            _lastMoveDir = moveDir;
            LookAtDirection(moveDir);

            Vector3 move = moveDir * Mathf.Max(speed, _walkSpeed);
            move.y = _verticalVelocity;
            DoMove(move * Time.deltaTime);
            _stuckTimer = 0f;
            return true;
        }

        private Teleporter FindTeleporterForEdge(Vector3 entryNodePos, Vector3 exitNodePos)
        {
            Teleporter[] teleporters = GetCachedTeleporters();
            Teleporter best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < teleporters.Length; i++)
            {
                Teleporter tp = teleporters[i];
                if (tp == null || !tp.enabled || !tp.gameObject.activeInHierarchy || tp.teleportPoint == null)
                    continue;

                float entryFlat = HorizontalSqr(tp.transform.position, entryNodePos);
                float exitFlat = HorizontalSqr(tp.teleportPoint.position, exitNodePos);
                if (entryFlat > 25f || exitFlat > 64f) continue;

                float yScore = Mathf.Abs(tp.transform.position.y - entryNodePos.y) * 0.2f
                    + Mathf.Abs(tp.teleportPoint.position.y - exitNodePos.y) * 0.1f;
                float score = entryFlat + exitFlat + yScore;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = tp;
                }
            }

            return best;
        }

        private Vector3 GetTeleporterEntryPoint(Teleporter teleporter, Vector3 fromPos)
        {
            Collider trigger = teleporter.GetComponent<Collider>();
            if (trigger == null) trigger = teleporter.GetComponentInChildren<Collider>();
            if (trigger != null && trigger.enabled)
            {
                Vector3 center = trigger.bounds.center;
                Vector3 closest = trigger.ClosestPoint(fromPos + Vector3.up * 0.8f);
                closest.y = center.y;
                return Vector3.Lerp(closest, center, 0.5f);
            }
            return teleporter.transform.position;
        }

        private Vector3 GetTeleporterPushDir(Teleporter teleporter, Vector3 exitNodePos)
        {
            Vector3 dir = teleporter.selfOrientation != null
                ? teleporter.selfOrientation.forward
                : teleporter.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
            {
                dir = exitNodePos - transform.position;
                dir.y = 0f;
            }
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            return dir.normalized;
        }

        // Direction-oscillation tracking (visible bouncing the id-based detector misses)
        private int _dirFlipCount;
        private float _lastDirFlipTime;

        /// <summary>Training guard: never accept a route that immediately re-walks the two
        /// nodes just walked through (the seed of every A→B→A→B loop).</summary>
        private bool IsImmediateBacktrack(List<NavNode> path)
        {
            if (path == null || path.Count < 2) return false;
            if (NavGraph.Instance == null || NavGraph.Instance.Mode != NavMode.Training) return false;
            if (_lastReachedNode == null || _prevReachedNode == null) return false;
            int lastId = _lastReachedNode.Id, prevId = _prevReachedNode.Id;
            if (lastId < 0 || prevId < 0) return false;
            if (path[0].Id == lastId && path[1].Id == prevId) return true;
            return path.Count >= 3 && path[1].Id == lastId && path[2].Id == prevId;
        }

        private NavEdge FindBestPathEdge(int fromId, int toId)
        {
            if (NavGraph.Instance == null) return null;

            NavEdge best = null;
            var edges = NavGraph.Instance.GetEdgesFrom(fromId);
            foreach (var e in edges)
            {
                if (e.To != toId) continue;
                if (best == null || EdgePriority(e.Type) > EdgePriority(best.Type)
                    || (e.Type == best.Type && e.Confidence > best.Confidence))
                {
                    best = e;
                }
            }
            return best;
        }

        private static int EdgePriority(EdgeType type)
        {
            switch (type)
            {
                case EdgeType.Teleporter: return 6;
                case EdgeType.Ladder: return 5;
                case EdgeType.Jump:
                case EdgeType.WallJump: return 4;
                case EdgeType.Slide: return 3;
                case EdgeType.Fall: return 2;
                default: return 1;
            }
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private float ScorePathCandidate(List<NavNode> path, Vector3 target)
        {
            if (path == null || path.Count == 0 || NavGraph.Instance == null)
                return float.MinValue;

            float startToTarget = Vector3.Distance(transform.position, target);
            float endToTarget = Vector3.Distance(path[path.Count - 1].Position, target);
            float closure = Mathf.Max(0f, startToTarget - endToTarget);
            float targetHeightDelta = target.y - transform.position.y;
            bool wantsVertical = Mathf.Abs(targetHeightDelta) > 2.25f;
            float endHeightError = Mathf.Abs(path[path.Count - 1].Position.y - target.y);

            float specialBonus = 0f;
            float confidenceBonus = 0f;
            float playerBonus = 0f;
            float turnPenalty = 0f;
            Vector3 prevDir = Vector3.zero;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var edge = NavGraph.Instance.GetEdgeBetween(path[i].Id, path[i + 1].Id);
                if (edge == null) continue;
                var from = NavGraph.Instance.GetNodeById(edge.From);
                var to = NavGraph.Instance.GetNodeById(edge.To);
                float gain = from != null && to != null ? to.Position.y - from.Position.y : 0f;
                bool usefulVertical = wantsVertical && Mathf.Sign(gain) == Mathf.Sign(targetHeightDelta)
                    && Mathf.Abs(gain) > 0.5f;

                if (edge.Type == EdgeType.Jump || edge.Type == EdgeType.WallJump)
                    specialBonus += usefulVertical ? 2.4f : 0.6f;
                else if (edge.Type == EdgeType.Ladder)
                    specialBonus += usefulVertical ? 3.0f : 0.45f;
                else if (edge.Type == EdgeType.Teleporter)
                    specialBonus += wantsVertical ? 2.0f : 0.6f;
                else if (edge.Type == EdgeType.Slide)
                    specialBonus += 0.25f;
                else if (edge.Type == EdgeType.Walk && usefulVertical)
                    specialBonus += 1.0f;

                confidenceBonus += Mathf.Clamp(edge.Confidence, 0f, 2f) * 0.18f;
                if (from != null && from.PlayerSourced) playerBonus += 0.18f;
                if (to != null && to.PlayerSourced) playerBonus += 0.18f;

                if (from != null && to != null)
                {
                    Vector3 segDir = to.Position - from.Position;
                    segDir.y = 0f;
                    if (segDir.sqrMagnitude > 0.01f)
                    {
                        segDir.Normalize();
                        if (prevDir.sqrMagnitude > 0.01f)
                        {
                            float dot = Vector3.Dot(prevDir, segDir);
                            if (dot < 0.25f) turnPenalty += 0.45f;
                            else if (dot < 0.65f) turnPenalty += 0.18f;
                        }
                        prevDir = segDir;
                    }
                }
            }

            // Death heatmap: routes through spots where bots recently fell to their
            // deaths score down hard (~2 per fresh death near a waypoint).
            float deathPenalty = 0f;
            for (int i = 0; i < path.Count; i += 2)
                deathPenalty += NavGraph.Instance.FallDeathPenalty(path[i].Position) * 2f;

            float nodePenalty = path.Count * 0.3f;
            float endPenalty = endToTarget * 0.8f;
            float verticalPenalty = wantsVertical ? endHeightError * 2.2f : 0f;
            return closure * 3f + specialBonus + confidenceBonus + playerBonus
                - nodePenalty - endPenalty - verticalPenalty - turnPenalty - deathPenalty;
        }

        private bool IsRouteSafeForPlay(List<NavNode> path)
        {
            if (path == null || path.Count <= 1 || NavGraph.Instance == null) return true;
            if (NavGraph.Instance.Mode != NavMode.Play) return true;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                var edge = NavGraph.Instance.GetEdgeBetween(path[i].Id, path[i + 1].Id);
                if (edge == null || edge.Confidence <= 0f) return false;
                if (NavGraph.Instance.IsBadForPlay(edge)) return false;

                var from = NavGraph.Instance.GetNodeById(edge.From);
                var to = NavGraph.Instance.GetNodeById(edge.To);
                if (from == null || to == null) return false;

                if ((edge.Type == EdgeType.Jump || edge.Type == EdgeType.WallJump)
                    && !IsPlayerProvenJumpEdge(edge)
                    && !NavGraph.Instance.IsTrustedForPlay(edge))
                    return false;

                if (edge.Type == EdgeType.Fall)
                {
                    float drop = from.Position.y - to.Position.y;
                    if (!NavGraph.Instance.IsTrustedForPlay(edge) || drop > 2.75f || to.NearEdge) return false;
                }

                if (to.NearEdge && edge.Type == EdgeType.Walk)
                    return false;
            }
            return true;
        }

        private int CountRouteSpecialEdges(List<NavNode> path)
        {
            if (path == null || NavGraph.Instance == null) return 0;
            int count = 0;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var edge = NavGraph.Instance.GetEdgeBetween(path[i].Id, path[i + 1].Id);
                if (edge == null) continue;
                if (edge.Type == EdgeType.Jump || edge.Type == EdgeType.WallJump
                    || edge.Type == EdgeType.Ladder || edge.Type == EdgeType.Teleporter
                    || edge.Type == EdgeType.Slide)
                    count++;
            }
            return count;
        }

        private bool ShouldHoldCurrentRoute(Vector3 target, bool hasWorkingPath, bool targetMoved)
        {
            if (!hasWorkingPath || _graphPath == null || _graphPathIndex >= _graphPath.Count) return false;
            if (Time.time >= _routeCommitUntil) return false;

            int specialEdges = CountRouteSpecialEdges(_graphPath);
            // Special-traversal chains (jump→ladder→jump up a tower) legitimately stall
            // for a moment between segments — lining up a jump, mounting a ladder. The
            // old 1.4s stuck limit dumped the whole sequence right there and a mesh
            // repath sent the bot around (or back down). Give chains real slack; only
            // a hard stuck breaks them.
            float stuckLimit = specialEdges > 0 ? 2.8f : 1.4f;
            if (_progressState == ProgressState.HardStuck || _stuckTimer > stuckLimit) return false;

            float movedFromCommit = Vector3.Distance(_routeCommitTarget, target);
            float allowedTargetMove = specialEdges > 0 ? 11f : 7f;
            if (movedFromCommit > allowedTargetMove) return false;

            return !targetMoved || specialEdges > 0;
        }

        private float GetWaypointReachRadius(NavNode node)
        {
            float radius = State == BotState.Hunt ? 1.15f : 0.95f;
            if (node == null) return radius;

            NavEdge edge = null;
            if (_lastReachedNode != null)
                edge = FindBestPathEdge(_lastReachedNode.Id, node.Id);

            if (edge != null)
            {
                switch (edge.Type)
                {
                    case EdgeType.Ladder:
                        radius = 1.55f;
                        break;
                    case EdgeType.Jump:
                    case EdgeType.WallJump:
                    case EdgeType.Fall:
                        radius = 1.25f;
                        break;
                    case EdgeType.Slide:
                        radius = 1.2f;
                        break;
                }
            }

            if (_currentHorizInput > 0.8f) radius += 0.15f;
            if (Mathf.Abs(node.Position.y - transform.position.y) > 1.2f) radius += 0.2f;
            return radius;
        }

        // Pathfinding time spent by ALL bots this frame. With 8 bots, repaths landing on
        // the same frame stack into one long hitch — a bot that still has a workable
        // route defers ~0.15s when the frame's budget is spent. Pathless bots and
        // moved-target repaths always proceed.
        private static int s_repathFrame;
        private static float s_repathMsThisFrame;

        private bool RepathBudgetExhausted(bool hasWorkingPath, bool targetMoved)
        {
            if (Time.frameCount != s_repathFrame)
            {
                s_repathFrame = Time.frameCount;
                s_repathMsThisFrame = 0f;
            }
            if (!hasWorkingPath || targetMoved) return false;
            if (s_repathMsThisFrame <= 12f) return false;
            _repathTimer = Mathf.Max(_repathTimer, 0.15f);
            return true;
        }

        private void AcceptGraphRoute(List<NavNode> path, PathSource source, Vector3 target, float score)
        {
            _graphPath = path ?? new List<NavNode>();
            _graphPathIndex = 0;
            _lastAcceptedPathScore = score;
            _routeCommitTarget = target;

            int specialEdges = CountRouteSpecialEdges(_graphPath);
            // Stickier commits = bots stop re-deciding their route every couple seconds (smoother,
            // less direction-thrashing). Safe now that A* is deterministic, so the committed route
            // is the same one a repath would pick anyway. Navmesh corner routes are the most
            // deterministic of all — hold them longest.
            float commitTime = specialEdges > 0 ? 7.5f : (source == PathSource.NavMeshRoute ? 4.5f : 3.0f);
            if (State == BotState.GoToWeapon) commitTime += 1.5f;
            if (Mathf.Abs(target.y - transform.position.y) > 2.25f) commitTime += 1.5f;
            _routeCommitUntil = Time.time + commitTime;

            SwitchPathSource(source);
        }

        private List<NavNode> FindVerticalConnectorRoute(Vector3 target)
        {
            if (NavGraph.Instance == null || !NavGraph.Instance.HasData) return null;
            float targetHeightDelta = target.y - transform.position.y;
            if (Mathf.Abs(targetHeightDelta) < 2.25f) return null;

            NavNode bestNode = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < NavGraph.Instance.Nodes.Count; i++)
            {
                var node = NavGraph.Instance.Nodes[i];
                if (node == null || node.Confidence <= 0f) continue;
                var edges = NavGraph.Instance.GetEdgesFrom(node.Id);
                if (edges == null || edges.Count == 0) continue;

                foreach (var edge in edges)
                {
                    if (edge == null || edge.Confidence <= 0f) continue;
                    if (edge.Type != EdgeType.Ladder && edge.Type != EdgeType.Jump
                        && edge.Type != EdgeType.WallJump && edge.Type != EdgeType.Teleporter)
                        continue;

                    var to = NavGraph.Instance.GetNodeById(edge.To);
                    if (to == null) continue;
                    float gain = to.Position.y - node.Position.y;
                    if (edge.Type != EdgeType.Teleporter && Mathf.Sign(gain) != Mathf.Sign(targetHeightDelta))
                        continue;
                    if (edge.Type != EdgeType.Teleporter && Mathf.Abs(gain) < 0.5f)
                        continue;

                    float nodeDist = Vector3.Distance(transform.position, node.Position);
                    float exitHeightError = Mathf.Abs(to.Position.y - target.y);
                    float exitTargetDist = Vector3.Distance(to.Position, target);
                    float score = -nodeDist * 0.6f - exitHeightError * 2.8f - exitTargetDist * 0.35f;
                    if (edge.Type == EdgeType.Ladder) score += 8f;
                    if (edge.Type == EdgeType.Teleporter) score += 7f;
                    if ((edge.Type == EdgeType.Jump || edge.Type == EdgeType.WallJump) && IsPlayerProvenJumpEdge(edge))
                        score += 6f;
                    if (node.PlayerSourced) score += 2f;
                    if (to.PlayerSourced) score += 2f;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestNode = node;
                    }
                }
            }

            if (bestNode == null) return null;
            var path = NavGraph.Instance.FindPath(transform.position, bestNode.Position,
                jitter: 0.02f, searchRadius: 80f, playerOnly: true, preferHeight: true);
            if (path == null || path.Count <= 1)
                path = NavGraph.Instance.FindPath(transform.position, bestNode.Position,
                    jitter: 0.02f, searchRadius: 80f, preferHeight: true);
            return path;
        }

        private bool _gapApproachSprint; // This frame is a deliberate sprint-at-edge for a gap jump

        private void MoveToward(Vector3 target, float speed)
        {
            if (_cc == null || !_cc.enabled) return;
            _gapApproachSprint = false;

            // Auto-nodeless: if graph is unusable, bot is in an untrained area, or bot has hit
            // a bounce/stuck lock, use direct movement. Always on — previously toggleable via
            // SmartFallback; no reason to disable.
            {
                // Decay the bounce escalation once the bot has been behaving for 30s
                if (_nodelessBounceCount > 0 && Time.time - _lastBounceTime > 30f)
                    _nodelessBounceCount = Mathf.Max(0, _nodelessBounceCount - 1);

                // Bounce lock — set by ping-pong detection below. While the lock is active,
                // mostly use nodeless movement, but keep probing graph recovery.
                if (_nodelessLockTimer > 0f)
                {
                    _nodelessLockTimer -= Time.deltaTime;

                    // Probe graph recovery during nodeless lock instead of waiting out the full timer.
                    if (BotNavMesh.Ready || (NavGraph.Instance != null && NavGraph.Instance.HasData))
                    {
                        _repathTimer -= Time.deltaTime;
                        if (_repathTimer <= 0f)
                        {
                            _repathTimer = 0.6f;
                            // Navmesh escape route first — it can't ping-pong on bad graph data.
                            List<NavNode> recoverPath = null;
                            if (BotNavMesh.Ready)
                            {
                                recoverPath = BotNavMesh.FindCornerPath(transform.position, target, out bool nmComplete);
                                if (recoverPath != null && !nmComplete) recoverPath = null;
                            }
                            if (recoverPath == null && NavGraph.Instance != null && NavGraph.Instance.HasData)
                                recoverPath = NavGraph.Instance.FindPath(transform.position, target, searchRadius: 45f);
                            if (recoverPath != null && recoverPath.Count > 1)
                            {
                                _graphPath = recoverPath;
                                _graphPathIndex = 0;
                                _nodelessLockTimer = 0f;
                                _noPathRecoveryStreak = 0;
                                SwitchPathSource(recoverPath[0].Id < 0 ? PathSource.NavMeshRoute : PathSource.GraphRoute);
                                Plugin.Log.LogInfo($"[{BotName}] Recovered route during nodeless ({recoverPath.Count} nodes, {(recoverPath[0].Id < 0 ? "navmesh" : "graph")})");
                            }
                        }
                    }

                    if (_nodelessLockTimer <= 0f && _graphPath != null && _graphPath.Count > 1 && _graphPathIndex < _graphPath.Count)
                        return;
                    MoveTowardNodeless(target, speed);
                    return;
                }

                // With a baked navmesh, untrained areas are routable — skip the nodeless
                // offramps that exist to cover a thin/absent learned graph.
                bool navmeshReady = BotNavMesh.Ready;
                bool graphUsable = NavGraph.Instance != null && NavGraph.Instance.HasData
                    && NavGraph.Instance.NodeCount >= 10;
                if (!graphUsable && !navmeshReady)
                {
                    SwitchPathSource(PathSource.ExploreBuildRoute);
                    MoveTowardNodeless(target, speed);
                    return;
                }

                if (!navmeshReady)
                {
                    var nearBot = NavGraph.Instance.FindNearestNode(transform.position, 8f);
                    if (nearBot == null)
                    {
                        SwitchPathSource(PathSource.ExploreBuildRoute);
                        MoveTowardNodeless(target, speed);
                        return;
                    }

                    if (IsDegeneratePath(target))
                    {
                        SwitchPathSource(PathSource.DirectTacticalRoute);
                        MoveTowardNodeless(target, speed);
                        return;
                    }
                }
            }

            // Zone launch override — skip all pathfinding/steering, just ride the force.
            // Gravity already ran in ApplyGravity() earlier this frame, so _verticalVelocity
            // has the correct "falling" component baked in; we just ride whatever it is now.
            if (_zoneForceDuration > 0f)
            {
                // End the launch when the bot has actually landed after going airborne.
                // This matches player behavior: once they land, normal movement resumes.
                bool landedAfterLaunch = _zoneLaunchInAir && _cc.isGrounded && _verticalVelocity <= 0f;
                if (landedAfterLaunch)
                {
                    _zoneForceDuration = 0f;
                    _zoneForce = Vector3.zero;
                    _zoneLaunchInAir = false;
                    // Fall through to normal movement this frame
                }
                else
                {
                    // Track "in air" state so we know when to end on landing
                    if (!_cc.isGrounded) _zoneLaunchInAir = true;

                    Vector3 zoneMove = _zoneForce;
                    zoneMove.y = _verticalVelocity;
                    // FPC's moveDirection has NO air friction — a launched player carries full
                    // horizontal momentum until landing. Decaying mid-flight made bots undershoot
                    // pads, fall back, and re-trigger them. Friction + countdown tick on ground only;
                    // the landedAfterLaunch check above ends the ride.
                    if (_cc.isGrounded)
                    {
                        _zoneForce *= Mathf.Max(0f, 1f - 2f * Time.deltaTime);
                        _zoneForceDuration -= Time.deltaTime;
                        if (_zoneForceDuration <= 0f)
                        {
                            _zoneForce = Vector3.zero;
                            _zoneForceDuration = 0f;
                            _zoneLaunchInAir = false;
                        }
                    }
                    float zmSqr = zoneMove.x * zoneMove.x + zoneMove.z * zoneMove.z;
                    if (zmSqr > 0.0001f)
                    {
                        float inv = 1f / Mathf.Sqrt(zmSqr);
                        _lastMoveDir.x = zoneMove.x * inv; _lastMoveDir.y = 0f; _lastMoveDir.z = zoneMove.z * inv;
                    }
                    DoMove(zoneMove * Time.deltaTime);
                    return;
                }
            }

            _commitTimer -= Time.deltaTime;
            Vector3 dir = (_commitTimer > 0f && _commitDir.sqrMagnitude > 0.01f) ? _commitDir : transform.forward;
            _intentionalJumpTimer -= Time.deltaTime;
            bool jumped = false;

            // ---- Phase 1: Graph path following ----
            EdgeType nextEdgeType = EdgeType.Walk;
            NavEdge nextEdge = null;
            NavNode nextEdgeFromNode = null;

            bool graphHasData = NavGraph.Instance != null && NavGraph.Instance.HasData;
            if (graphHasData || BotNavMesh.Ready)
            {
                _repathTimer -= Time.deltaTime;
                float distToTarget = Vector3.Distance(_lastPathTarget, target);
                // Don't repath while on ladder, dismounting, or mid-jump
                bool suppressRepath = _onLadder || _ladderDismountTimer > 0f
                    || (_intentionalJumpTimer > 0f && !_cc.isGrounded);
                // Don't repath if we have a working path and are making progress
                bool hasWorkingPath = _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count;
                bool routeHasSpecial = hasWorkingPath && CountRouteSpecialEdges(_graphPath) > 0;
                bool targetMoved = distToTarget > (routeHasSpecial ? 10f : 5f);

                if (!suppressRepath && ShouldHoldCurrentRoute(target, hasWorkingPath, targetMoved))
                {
                    // Keep executing the committed corridor. This prevents flicker between
                    // equally plausible routes while climbing, jumping, or chasing weapons.
                }
                else if (!suppressRepath && (!hasWorkingPath || _repathTimer <= 0f || targetMoved)
                    && !RepathBudgetExhausted(hasWorkingPath, targetMoved))
                {
                    // Adaptive repath interval: fast when no path, slow when path is working.
                    // A working navmesh route is deterministic — repathing it just regenerates
                    // the same corners, so hold it even longer between refreshes.
                    bool weaponPath = State == BotState.GoToWeapon || (State == BotState.FindWeapon && _weaponTarget != null);
                    _repathTimer = hasWorkingPath
                        ? (_pathSource == PathSource.NavMeshRoute ? 3.5f : 2f)
                        : (weaponPath ? 1.8f : 1f);
                    _lastPathTarget = target;

                    // Weighted multi-candidate routing. Prefer routes that materially close
                    // objective distance and include useful traversal edges (jump/ladder),
                    // including player-only routes when regular graphing is weak.
                    bool wantHeight = target.y > transform.position.y + 2f;

                    List<NavNode> bestPath = null;
                    float bestScore = float.MinValue;
                    PathSource bestSrc = PathSource.DirectTacticalRoute;

                    void ConsiderCandidate(List<NavNode> candidate, PathSource src, float bonus = 0f, bool skipSafety = false)
                    {
                        if (candidate == null || candidate.Count == 0) return;
                        if (IsImmediateBacktrack(candidate)) return;
                        if (!skipSafety && !IsRouteSafeForPlay(candidate)) return;
                        float score = ScorePathCandidate(candidate, target) + bonus;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPath = candidate;
                            bestSrc = src;
                        }
                    }

                    // NAVMESH FIRST: a complete ground route IS the route — deterministic,
                    // geometrically safe, and immune to learned-data noise, so there is
                    // nothing to score it against (this also kills route flip-flop, the main
                    // ping-pong source). A partial route (a gap/jump blocks the last stretch)
                    // drops into candidate scoring below so learned jump/ladder routes can
                    // win instead. Skipped while executing a validation route — those must
                    // follow the exact graph edges being tested.
                    bool validating = Plugin.IsValidateMode && _validationRouteNodeIds.Count > 1;
                    List<NavNode> nmDirect = null;
                    if (BotNavMesh.Ready && !validating)
                    {
                        var nmPath = BotNavMesh.FindCornerPath(transform.position, target, out bool nmComplete);
                        if (nmPath != null)
                        {
                            // Partial routes still get a solid bonus: graph candidates accrue
                            // per-edge confidence/player bonuses a corner path can never earn,
                            // which starved navmesh routes out of the running entirely.
                            if (nmComplete) nmDirect = nmPath;
                            else ConsiderCandidate(nmPath, PathSource.NavMeshRoute, 5f, skipSafety: true);
                        }
                    }

                    if (nmDirect != null)
                    {
                        AcceptGraphRoute(nmDirect, PathSource.NavMeshRoute, target,
                            ScorePathCandidate(nmDirect, target));
                    }
                    else
                    {
                    bool combatPath = State == BotState.Hunt && _playerTarget != null;
                    if (graphHasData)
                    {
                        // Candidate evaluation used to fire up to 9 full A* searches + a
                        // graph-wide scan + a BFS flood in one frame — the 50-105ms
                        // "[Perf] SLOW bot update" spikes. Now: duplicates are gated on
                        // whether they can actually differ, the expensive candidates run
                        // cheapest-first behind a time budget, and fallbacks only run when
                        // nothing was found. A pathless bot always keeps searching.
                        var swRepath = System.Diagnostics.Stopwatch.StartNew();
                        bool InBudget() => bestPath == null || swRepath.ElapsedMilliseconds < 6;

                        ConsiderCandidate(NavGraph.Instance.GetCachedRoute(transform.position, target), PathSource.GraphRoute);
                        if (!combatPath)
                        {
                            var direct = NavGraph.Instance.FindPath(transform.position, target, preferHeight: wantHeight);
                            ConsiderCandidate(direct, PathSource.GraphRoute);
                            // 40f differs from the default 30f only in endpoint snapping —
                            // rerun only when widening can snap a different endpoint.
                            // Otherwise it repeats the identical search (worst on unreachable
                            // targets, where every call exhausts the whole component).
                            if ((direct == null || direct.Count == 0)
                                && (NavGraph.Instance.FindNearestNode(transform.position, 30f) == null
                                    || NavGraph.Instance.FindNearestNode(target, 30f) == null))
                                ConsiderCandidate(NavGraph.Instance.FindPath(transform.position, target, searchRadius: 40f, preferHeight: wantHeight), PathSource.GraphRoute);
                        }
                        var playerNear = NavGraph.Instance.FindPath(transform.position, target, jitter: 0.05f, searchRadius: 50f, playerOnly: true, preferHeight: true);
                        ConsiderCandidate(playerNear, PathSource.ExploreBuildRoute);
                        if (weaponPath)
                        {
                            if (playerNear == null || playerNear.Count == 0)
                                ConsiderCandidate(NavGraph.Instance.FindPath(transform.position, target, jitter: 0.02f, searchRadius: 75f, playerOnly: true, preferHeight: true), PathSource.ExploreBuildRoute);
                            if (InBudget())
                                ConsiderCandidate(NavGraph.Instance.FindPath(transform.position, target, jitter: 0.02f, searchRadius: 75f, preferHeight: true), PathSource.GraphRoute);
                        }

                        // Full node×edge scan + up to two more A* runs — the most expensive
                        // candidate, so it moved from second to last and respects the budget.
                        if (InBudget())
                            ConsiderCandidate(FindVerticalConnectorRoute(target), PathSource.GraphRoute);

                        // Reachability fallback — a BFS flood is only worth it when no
                        // candidate produced a route at all.
                        if (bestPath == null)
                        {
                            var closestReachable = NavGraph.Instance.FindClosestReachableNode(transform.position, target);
                            if (closestReachable != null)
                                ConsiderCandidate(NavGraph.Instance.FindPath(transform.position, closestReachable.Position, searchRadius: 45f), PathSource.GraphRoute);
                        }

                        ConsiderCandidate(NavGraph.Instance.FindNearestPatrolRoute(transform.position, target), PathSource.ExploreBuildRoute);
                        s_repathMsThisFrame += (float)swRepath.Elapsed.TotalMilliseconds;
                    }

                    // Wider keep-margin: only abandon the current route for a MATERIALLY better one
                    // (was 0.75). Prevents swapping between near-equal routes — a key jitter source.
                    if (hasWorkingPath && bestPath != null && _lastAcceptedPathScore > float.MinValue + 1f
                        && bestScore < _lastAcceptedPathScore + 1.5f && !targetMoved)
                    {
                        _repathTimer = 1.0f;
                        bestPath = _graphPath;
                        bestSrc = PathSource.GraphRoute;
                        bestScore = _lastAcceptedPathScore;
                    }

                    AcceptGraphRoute(bestPath ?? new List<NavNode>(), bestSrc, target, bestScore);
                    }

                    if (_graphPath.Count == 0)
                    {
                        // All pathing failed. In Training, don't blindly push at the old
                        // target — nodeless-walk toward the nearest UNVISITED node so the
                        // bot keeps discovering instead of grinding the same spot.
                        Vector3 fallbackTarget = target;
                        if (NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training)
                        {
                            var unvisited = NavGraph.Instance.FindNearestUnvisitedNode(transform.position, 40f);
                            if (unvisited != null) fallbackTarget = unvisited.Position;
                        }
                        SwitchPathSource(PathSource.DirectTacticalRoute);
                        MoveTowardNodeless(fallbackTarget, speed);
                        return;
                    }
                    _noPathRecoveryStreak = 0;

                    // Single-node "paths" are usually just our current node and do not provide
                    // actionable movement toward the target. Treat them as no-path so bots keep
                    // pushing in nodeless mode instead of bouncing on node-repeat logic.
                    if (_graphPath.Count == 1)
                    {
                        float onlyNodeDist = Vector3.Distance(transform.position, _graphPath[0].Position);
                        float targetDist = Vector3.Distance(transform.position, target);
                        bool verticalGoal = Mathf.Abs(target.y - transform.position.y) > 2.25f;
                        bool keepVerticalConnector = verticalGoal && Mathf.Abs(_graphPath[0].Position.y - transform.position.y) > 0.75f;
                        if (!keepVerticalConnector && onlyNodeDist < 2f && targetDist > 4f)
                        {
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            SwitchPathSource(PathSource.DirectTacticalRoute);
                            MoveTowardNodeless(target, speed);
                            return;
                        }
                    }
                }

                // Advance past reached nodes
                while (_graphPathIndex < _graphPath.Count)
                {
                    float distToNode = Vector3.Distance(transform.position, _graphPath[_graphPathIndex].Position);
                    bool reachedNodeThisFrame = distToNode < GetWaypointReachRadius(_graphPath[_graphPathIndex]);

                    // Pure-pursuit style advancement: if the bot has already crossed the
                    // waypoint plane, do not make it turn back to kiss the exact node.
                    if (!reachedNodeThisFrame && _lastReachedNode != null)
                    {
                        Vector3 seg = _graphPath[_graphPathIndex].Position - _lastReachedNode.Position;
                        seg.y = 0f;
                        Vector3 botAlong = transform.position - _lastReachedNode.Position;
                        botAlong.y = 0f;
                        float segLen = seg.magnitude;
                        if (segLen > 0.3f)
                        {
                            Vector3 segDir = seg / segLen;
                            float along = Vector3.Dot(botAlong, segDir);
                            float lateral = (botAlong - segDir * along).magnitude;
                            if (along >= segLen - 0.25f && lateral < 1.35f)
                                reachedNodeThisFrame = true;
                        }
                    }

                    if (reachedNodeThisFrame)
                    {
                        var reachedNode = _graphPath[_graphPathIndex];

                        // Synthetic navmesh waypoints carry negative ids — they must never
                        // create or reinforce learned graph data.
                        bool trainingGraph = NavGraph.Instance != null
                            && NavGraph.Instance.Mode == NavMode.Training
                            && reachedNode.Id >= 0;

                        // Training success feeds certification; Play stays read-only here.
                        if (trainingGraph && _lastReachedNode != null && _lastReachedNode.Id >= 0)
                            NavGraph.Instance.ReportSuccess(_lastReachedNode.Id, reachedNode.Id);

                        // Compress clusters around well-traveled areas (every 5th node to avoid spam)
                        if (trainingGraph && reachedNode.VisitCount % 5 == 0)
                            NavGraph.Instance.CompressNearby(reachedNode.Position);

                        // Track recent node history for ping-pong detection
                        _recentNodeIds[_recentNodeIdx] = reachedNode.Id;
                        _recentNodeIdx = (_recentNodeIdx + 1) % _recentNodeIds.Length;
                        if (_recentNodeCount < _recentNodeIds.Length) _recentNodeCount++;
                        PushLoopSignature(reachedNode.Id, nextEdgeType, _lastMoveDir);

                        // Detect ping-pong: A→B→A→B pattern in recent history
                        // Detect ping-pong: A→B→A→B→A→B pattern — need 3 full cycles (6 entries)
                        bool pingPong = false;
                        if (_recentNodeCount >= 6)
                        {
                            int n0 = _recentNodeIds[(_recentNodeIdx - 1 + 8) % 8];
                            int n1 = _recentNodeIds[(_recentNodeIdx - 2 + 8) % 8];
                            int n2 = _recentNodeIds[(_recentNodeIdx - 3 + 8) % 8];
                            int n3 = _recentNodeIds[(_recentNodeIdx - 4 + 8) % 8];
                            int n4 = _recentNodeIds[(_recentNodeIdx - 5 + 8) % 8];
                            int n5 = _recentNodeIds[(_recentNodeIdx - 6 + 8) % 8];
                            if (n0 == n2 && n2 == n4 && n1 == n3 && n3 == n5 && n0 != n1)
                                pingPong = true;
                        }
                        if (!pingPong && HasLoopCycle()) pingPong = true;

                        // Track repeated node visits — delete bad edges
                        if (reachedNode.Id == _lastNodeRepeatedId)
                        {
                            _nodeRepeatCount++;
                        }

                        if (_graphPath.Count >= 2 && (_nodeRepeatCount >= 10 || pingPong) && NavGraph.Instance != null)
                        {
                            if (Plugin.IsValidateMode && _validationRouteNodeIds.Count > 1)
                            {
                                string label = string.IsNullOrWhiteSpace(_validationRouteLabel) ? "route" : _validationRouteLabel;
                                NavGraph.Instance.ReportRouteValidation(_validationRouteNodeIds, success: false,
                                    $"{BotName} ping-ponged while executing {label}");
                                NavGraph.Instance.SuppressValidationLabel(label, 18f,
                                    $"PATH FOLLOWER FAILED: {label}. Bots will try other routes; if it repeats, walk it yourself once.");
                                _validationRouteNodeIds.Clear();
                                _validationRouteTimer = 0f;
                                _validationRouteTarget = Vector3.zero;
                                _validationRouteLabel = null;
                            }

                            if (Time.time < _loopBlacklistUntil)
                            {
                                _graphPath.Clear();
                                _graphPathIndex = 0;
                                _repathTimer = 0f;
                                SwitchPathSource(PathSource.DirectTacticalRoute);
                                MoveTowardNodeless(target, speed);
                                return;
                            }
                            // Bouncing between nodes is almost always a STEERING/controller artifact
                            // (equally-scored routes, waypoint twitch), NOT bad graph data. Break the
                            // loop via repath + nodeless recovery below; do NOT destroy the shared
                            // edge confidence — that was wiping out validly-trained jumps for a
                            // movement bug the bot can recover from on its own.
                            Plugin.Log.LogInfo($"[{BotName}] {(pingPong ? "Ping-pong" : "Repeat")} detected at node {reachedNode.Id} — breaking loop (graph data preserved)");
                            _nodeRepeatCount = 0;
                            _recentNodeCount = 0;
                            _loopBreaks++;
                            _loopBlacklistUntil = Time.time + 4f;
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            _stuckTimer = 1f; // Trigger stuck recovery

                            _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                            _lastBounceTime = Time.time;

                            // First bounce: force quick repath retry without entering nodeless lock.
                            // Repeated bounces: short lock window, then probe back into graph.
                            if (_nodelessBounceCount < 2 && _progressState != ProgressState.HardStuck)
                            {
                                _nextRepathAllowedAt = Time.time + 0.2f;
                                _noPathRecoveryStreak = Mathf.Max(_noPathRecoveryStreak, 1);
                                SwitchPathSource(PathSource.GraphRoute);
                                Plugin.Log.LogInfo($"[{BotName}] Repeat detected -> forcing repath retry");
                            }
                            else
                            {
                                _nodelessLockTimer = Mathf.Min(7f, 1.75f + 1.25f * _nodelessBounceCount);
                                SwitchPathSource(PathSource.DirectTacticalRoute);
                                Plugin.Log.LogInfo($"[{BotName}] Nodeless lock engaged for {_nodelessLockTimer:F1}s (bounce #{_nodelessBounceCount})");
                            }
                            break;
                        }

                        _lastNodeRepeatedId = reachedNode.Id;

                        // Shortcut detection: bot walked prev → last → reached cleanly.
                        // If prev → reached is directly walkable, add the shortcut edge and
                        // decay the detour so A* prefers the straight route next time.
                        if (trainingGraph && _prevReachedNode != null && _prevReachedNode.Id >= 0
                            && _lastReachedNode != null && _lastReachedNode.Id >= 0 && NavGraph.Instance != null)
                        {
                            NavGraph.Instance.TryShortcut(_prevReachedNode.Id, _lastReachedNode.Id, reachedNode.Id);
                        }

                        // Rotate the 2-deep history BEFORE overwriting _lastReachedNode
                        _prevReachedNode = _lastReachedNode;
                        _lastReachedNode = reachedNode;
                        _wallRepathCount = 0;
                        _graphPathIndex++;

                        // Re-check NearEdge: bot walked here, new nodes may exist below nearby edges
                        if (trainingGraph && reachedNode.NearEdge && NavGraph.Instance != null)
                        {
                            reachedNode.NearEdge = NavGraph.Instance.CheckNearEdgePublic(reachedNode.Position);
                        }
                    }
                    else break;
                }

                if (_graphPathIndex < _graphPath.Count)
                {
                    SwitchPathSource(PathSource.GraphRoute);
                    Vector3 nodePos = _graphPath[_graphPathIndex].Position;
                    dir = nodePos - transform.position;
                    float distToNext = new Vector3(dir.x, 0, dir.z).magnitude;

                    // If stuck trying to reach this node (wall blocked), skip to next
                    if (_stuckTimer > 2f && distToNext > 2f && _graphPathIndex + 1 < _graphPath.Count)
                    {
                        // Check if we can see the node AFTER this one
                        Vector3 skipPos = _graphPath[_graphPathIndex + 1].Position;
                        Vector3 toSkip = skipPos - transform.position;
                        bool canSeeSkip = !Physics.Raycast(transform.position + Vector3.up * 0.8f,
                            toSkip.normalized, toSkip.magnitude, WALL_MASK, QueryTriggerInteraction.Ignore);
                        if (canSeeSkip)
                        {
                            NavGraph.Instance.ReportBadNode(_graphPath[_graphPathIndex].Id, "skipped blocked waypoint", 1, silent: true);
                            _graphPathIndex++; // Skip blocked node
                            nodePos = _graphPath[_graphPathIndex].Position;
                            dir = nodePos - transform.position;
                            _stuckTimer = 0f;
                        }
                    }

                    // Check edge type to this node — try multiple lookups for robustness
                    if (_lastReachedNode != null)
                    {
                        nextEdge = FindBestPathEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                        if (nextEdge != null)
                        {
                            nextEdgeType = nextEdge.Type;
                            nextEdgeFromNode = _lastReachedNode;
                        }
                    }

                    // LOS SKIP (Play only): if the waypoint AFTER the current one is
                    // directly walkable-visible, advance past the current one. Exact
                    // node-center visits are a training behavior (edge crediting needs
                    // them); in Play they just look robotic.
                    _losSkipTimer -= Time.deltaTime;
                    if (_losSkipTimer <= 0f && NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play
                        && nextEdgeType == EdgeType.Walk && _cc.isGrounded && _intentionalJumpTimer <= 0f && !_onLadder
                        && _graphPathIndex + 1 < _graphPath.Count)
                    {
                        _losSkipTimer = 0.4f;
                        var skipEdge = FindBestPathEdge(_graphPath[_graphPathIndex].Id, _graphPath[_graphPathIndex + 1].Id);
                        if ((skipEdge == null || skipEdge.Type == EdgeType.Walk)
                            && CanWalkStraightTo(_graphPath[_graphPathIndex + 1].Position))
                        {
                            _graphPathIndex++;
                            nodePos = _graphPath[_graphPathIndex].Position;
                            dir = nodePos - transform.position;
                            distToNext = new Vector3(dir.x, 0, dir.z).magnitude;
                            if (_lastReachedNode != null)
                            {
                                nextEdge = FindBestPathEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                                if (nextEdge != null) { nextEdgeType = nextEdge.Type; nextEdgeFromNode = _lastReachedNode; }
                            }
                        }
                    }

                    // PURE PURSUIT on walk legs: steer toward a point ~2.6m along the
                    // remaining path polyline instead of at the next node center — the
                    // classic fix for robotic node-to-node movement. Waypoint ADVANCEMENT
                    // stays distance-based against the real waypoint; only steering
                    // smooths. Special edges are excluded (jump lineups and ladder mounts
                    // must hit their nodes exactly).
                    if (nextEdgeType == EdgeType.Walk && _cc.isGrounded && _intentionalJumpTimer <= 0f)
                    {
                        Vector3 pursuit = ComputePursuitPoint(nodePos);
                        Vector3 toPursuit = pursuit - transform.position;
                        toPursuit.y = 0f;
                        if (toPursuit.sqrMagnitude > 0.25f)
                            dir = pursuit - transform.position;
                    }

                    // MESH-LINK EXECUTION: mesh routes can now cross trusted jump/fall
                    // links (SyncGraphLinks). A corner meaningfully above or across a gap
                    // is one of those crossings — look up the learned edge that matches
                    // this hop and launch its RECORDED trajectory (hard rail included)
                    // instead of leaving it to blind reactive jumping. Falls just need
                    // the void-safety walk-off allowance.
                    if (_pathSource == PathSource.NavMeshRoute && nextEdgeType == EdgeType.Walk
                        && _cc.isGrounded && _intentionalJumpTimer <= 0f && !_onLadder
                        && distToNext < 3.2f && NavGraph.Instance != null)
                    {
                        float hopUp = nodePos.y - transform.position.y;
                        Vector3 flatHop = new Vector3(nodePos.x - transform.position.x, 0f, nodePos.z - transform.position.z);
                        bool needsJump = hopUp > 1.1f
                            || (flatHop.sqrMagnitude > 1.2f && !HasGroundFootprintAhead(flatHop, 1.1f));
                        bool isDrop = hopUp < -1.5f;

                        if (needsJump)
                        {
                            var linkEdge = NavGraph.Instance.FindTrustedSpecialEdgeNear(transform.position, nodePos);
                            if (linkEdge != null && (linkEdge.Type == EdgeType.Jump || linkEdge.Type == EdgeType.WallJump))
                            {
                                if (flatHop.sqrMagnitude > 0.04f
                                    && TryJump(JumpReason.GraphJump, flatHop.normalized, jumpEdge: linkEdge))
                                {
                                    _airStrafeTarget = nodePos;
                                    _airStrafeActive = true;
                                    jumped = true;
                                }
                            }
                        }
                        else if (isDrop && flatHop.sqrMagnitude > 0.04f)
                        {
                            // Trusted fall link: authorize the walk-off (void safety would
                            // otherwise zero the horizontal move right at the lip).
                            var fallEdge = NavGraph.Instance.FindTrustedSpecialEdgeNear(transform.position, nodePos, 2.5f);
                            if (fallEdge != null && fallEdge.Type == EdgeType.Fall)
                            {
                                _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.45f);
                                _jumpDir = flatHop.normalized;
                                dir = flatHop;
                            }
                        }
                    }

                    // Fallback: if no edge found, also check from nearest node to bot's position
                    if (nextEdge == null && _lastReachedNode == null)
                    {
                        var nearBot = NavGraph.Instance.FindNearestNode(transform.position, 3f);
                        if (nearBot != null)
                        {
                            nextEdge = FindBestPathEdge(nearBot.Id, _graphPath[_graphPathIndex].Id);
                            if (nextEdge != null)
                            {
                                nextEdgeType = nextEdge.Type;
                                nextEdgeFromNode = nearBot;
                            }
                        }
                    }

                    if (nextEdgeType == EdgeType.Teleporter)
                    {
                        if (TryFollowTeleporterEdge(nextEdgeFromNode, _graphPath[_graphPathIndex], nextEdge, speed))
                        {
                            SwitchPathSource(PathSource.GraphRoute);
                            return;
                        }
                        SwitchPathSource(PathSource.DirectTacticalRoute);
                        MoveTowardNodeless(target, speed);
                        return;
                    }

                    // Geometry-based gap detection: check if there's a gap between bot and next node
                    // Runs for ALL edge types — catches mistyped edges and missing jump edges
                    if (nextEdgeType == EdgeType.Walk && _cc.isGrounded && !jumped)
                    {
                        Vector3 toNext = nodePos - transform.position;
                        float horizToNext = new Vector3(toNext.x, 0, toNext.z).magnitude;

                        if (horizToNext > 1f)
                        {
                            Vector3 horizDir = new Vector3(toNext.x, 0, toNext.z).normalized;

                            // Check 3 points along the path for gaps (30%, 50%, 70%)
                            bool gapFound = false;
                            for (float t = 0.3f; t <= 0.7f; t += 0.2f)
                            {
                                Vector3 checkPt = transform.position + horizDir * (horizToNext * t) + Vector3.up * 0.5f;
                                if (!Physics.Raycast(checkPt, Vector3.down, 3f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                                { gapFound = true; break; }
                            }

                            // Also check: is there an edge RIGHT ahead of the bot? (within 2m forward)
                            if (!gapFound && IsEdgeAhead(horizDir, 1.5f))
                                gapFound = true;

                            if (gapFound)
                            {
                                nextEdgeType = EdgeType.Jump;
                                if (horizToNext < 4f)
                                {
                                    // Air-strafe: drive toward the actual node on the far side.
                                    _airStrafeTarget = nodePos;
                                    _airStrafeActive = true;
                                    if (TryJump(JumpReason.GapDetection, horizDir, intentionalTime: 1.5f))
                                    {
                                        jumped = true;
                                        dir = horizDir;
                                        speed = _sprintSpeed;
                                    }
                                }
                            }
                        }
                    }

                    // --- Runtime wall check: raycast toward next walk node ---
                    // ONLY for Walk edges — Jump/Fall edges expect gaps/obstacles ahead
                    if (nextEdgeType == EdgeType.Walk && _cc.isGrounded && !_isSliding
                        && _intentionalJumpTimer <= 0f
                        && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                    {
                        Vector3 toNode = nodePos - transform.position;
                        float horizDist = new Vector3(toNode.x, 0, toNode.z).magnitude;
                        if (horizDist > 1f && horizDist < 15f)
                        {
                            Vector3 horizDir = new Vector3(toNode.x, 0, toNode.z).normalized;
                            // Box-jump trigger distance: only fire when the face is within 0.9m
                            // (previously used min(horizDist, 3) which fired up to 3m out and
                            // caused jump-too-early on short boxes).
                            const float BOX_JUMP_TRIGGER_DIST = 0.9f;
                            bool feetBlocked = Physics.Raycast(transform.position + Vector3.up * 0.3f,
                                horizDir, BOX_JUMP_TRIGGER_DIST, WALL_MASK, QueryTriggerInteraction.Ignore);

                            if (feetBlocked)
                            {
                                bool waistClear = !Physics.Raycast(transform.position + Vector3.up * 1f,
                                    horizDir, BOX_JUMP_TRIGGER_DIST, WALL_MASK, QueryTriggerInteraction.Ignore);
                                bool headClear = !Physics.Raycast(transform.position + Vector3.up * 1.7f,
                                    horizDir, BOX_JUMP_TRIGGER_DIST, WALL_MASK, QueryTriggerInteraction.Ignore);

                                if (waistClear || headClear)
                                {
                                    // Low wall — try jumping over it
                                    // Store target landing point for air-strafe to track toward
                                    _airStrafeTarget = transform.position + horizDir * 2.2f;
                                    _airStrafeActive = true;
                                    jumped = TryJump(JumpReason.Obstacle, horizDir);
                                }
                                else
                                {
                                    // Fully blocked — try sliding under
                                    bool slideClear = !Physics.Raycast(
                                        transform.position + Vector3.up * 0.3f,
                                        horizDir, Mathf.Min(horizDist, 3f), WALL_MASK, QueryTriggerInteraction.Ignore);
                                    // Check if crouch height has clearance
                                    bool crouchClear = !Physics.Raycast(
                                        transform.position + Vector3.up * 0.5f,
                                        horizDir, Mathf.Min(horizDist, 3f), WALL_MASK, QueryTriggerInteraction.Ignore);

                                    if (crouchClear)
                                    {
                                        // Can slide/crouch under
                                        InitSlide(horizDir, duration: 0.8f);
                                    }
                                    else
                                    {
                                        // Fully walled off — confirm with waist raycast too
                                        bool waistBlocked = Physics.Raycast(
                                            transform.position + Vector3.up * 0.8f,
                                            horizDir, Mathf.Min(horizDist, 3f), WALL_MASK, QueryTriggerInteraction.Ignore);

                                        if (waistBlocked)
                                        {
                                            // Wall confirmed — mark edge bad, repath
                                            if (_lastReachedNode != null)
                                                NavGraph.Instance.ReportWallEdge(
                                                    _lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                                            _graphPath.Clear();
                                            _graphPathIndex = 0;
                                            _repathTimer = 0f;
                                            dir = transform.forward;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // ===== JUMP/WALLJUMP EDGE HANDLING =====
                    if (nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.WallJump)
                    {
                        // Look up the actual edge for locked data + fail tracking
                        NavEdge jumpEdge = nextEdge;
                        if (jumpEdge == null && _lastReachedNode != null && NavGraph.Instance != null)
                            jumpEdge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);

                        Vector3 jumpToNode = nodePos - transform.position;
                        // Measure feasibility from the TAKEOFF NODE (the recorded edge length),
                        // NOT the bot's transient position. Measuring from transform.position
                        // condemned valid edges whenever the bot reached the waypoint plane a
                        // little short/long — a positioning artifact, not a bad edge.
                        Vector3 takeoffRef = _lastReachedNode != null ? _lastReachedNode.Position : transform.position;
                        float jumpTotalDist = Vector3.Distance(takeoffRef, nodePos);
                        float jumpHeightDiff = Mathf.Abs(nodePos.y - takeoffRef.y);
                        float maxJump = Plugin.GetMaxJumpDist();

                        // Decide whether this jump is unreachable for the bot right now.
                        bool impossible = jumpTotalDist > maxJump || jumpHeightDiff > maxJump * 0.5f;
                        bool destHasGround = Physics.Raycast(nodePos + Vector3.up * 2f, Vector3.down, 6f,
                            GROUND_MASK, QueryTriggerInteraction.Ignore);
                        bool isPlayMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play;
                        if (isPlayMode && !destHasGround) impossible = true;
                        if (isPlayMode && (jumpEdge == null
                            || (!IsPlayerProvenJumpEdge(jumpEdge) && !NavGraph.Instance.IsTrustedForPlay(jumpEdge))))
                            impossible = true;

                        if (impossible)
                        {
                            // The bot has NOT jumped — this is a PATHING decision, not a traversal
                            // failure. Do NOT slam the edge with a death-level fall penalty and do
                            // NOT single-bot-delete it (that destroyed validly-trained jumps). Only
                            // a jump genuinely beyond the envelope FROM ITS TAKEOFF NODE gets one
                            // consensus-gated strike; the play-mode "untrusted/no-ground" cases just repath.
                            bool trulyTooFar = jumpTotalDist > maxJump || jumpHeightDiff > maxJump * 0.5f;
                            if (trulyTooFar && jumpEdge != null && _lastReachedNode != null && NavGraph.Instance != null)
                                NavGraph.Instance.ReportFallOnEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id, BotId);
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            nextEdgeType = EdgeType.Walk;
                        }
                    }
                    if (nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.WallJump)
                    {
                        NavEdge jumpEdge = nextEdge;
                        if (jumpEdge == null && _lastReachedNode != null && NavGraph.Instance != null)
                            jumpEdge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);

                        // Check if we can just walk to the target — no jump needed
                        // (jump edge may have been created from a gap that no longer exists, or on a slope)
                        if (NavGraph.Instance != null && _lastReachedNode != null)
                        {
                            var fromPos = _lastReachedNode.Position;
                            var toPos = _graphPath[_graphPathIndex].Position;
                            bool canWalk = NavGraph.Instance.ValidateEdgeGroundPublic(fromPos, toPos)
                                && NavGraph.Instance.ValidateLineOfSightPublic(fromPos, toPos);
                            float walkHeightDiff = Mathf.Abs(toPos.y - fromPos.y);
                            float walkHorizDist = new Vector3(toPos.x - fromPos.x, 0, toPos.z - fromPos.z).magnitude;

                            // Walkable if ground is continuous AND height change is gentle (slope, not cliff)
                            if (canWalk && walkHeightDiff < 1.5f && walkHorizDist > 0.5f)
                            {
                                // Convert to walk — delete the jump edge, create walk edge
                                if (jumpEdge != null)
                                {
                                    // Add a parallel Walk edge so the bot can stroll this segment,
                                    // but PRESERVE the jump edge + its recorded trajectory. We no
                                    // longer delete demonstrated jumps just because a straight
                                    // ground/LoS sample reads as walkable (it can cross rails/lava).
                                    NavGraph.Instance.AddEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id,
                                        EdgeType.Walk, Vector3.Distance(fromPos, toPos));
                                }
                                nextEdgeType = EdgeType.Walk;
                            }
                        }
                    }
                    if (nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.WallJump)
                    {
                        NavEdge jumpEdge = nextEdge;
                        if (jumpEdge == null && _lastReachedNode != null && NavGraph.Instance != null)
                            jumpEdge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                        NavNode takeoffNode = _lastReachedNode ?? nextEdgeFromNode;

                        Vector3 jumpToNode = nodePos - transform.position;
                        Vector3 jumpFaceDir = new Vector3(jumpToNode.x, 0, jumpToNode.z);
                        float jumpHorizDist = jumpFaceDir.magnitude;
                        if (jumpFaceDir.sqrMagnitude > 0.01f) jumpFaceDir.Normalize();
                        else jumpFaceDir = transform.forward;

                        // PHYSICS CALCULATION — exact speed needed
                        float heightDiffJ = nodePos.y - transform.position.y;
                        float gravJ = heightDiffJ > 0 ? _gravityJump : _gravityNormal;
                        float airTimeEst = (2f * _jumpForce) / gravJ;
                        if (heightDiffJ < -1f) airTimeEst += Mathf.Sqrt(Mathf.Abs(heightDiffJ) * 2f / gravJ);
                        airTimeEst = Mathf.Clamp(airTimeEst, 0.3f, 2f);
                        float requiredSpeed = jumpHorizDist / Mathf.Max(airTimeEst, 0.1f);

                        // Use locked values if available (proven to work)
                        if (jumpEdge != null && jumpEdge.LockedSpeed > 0f)
                            requiredSpeed = jumpEdge.LockedSpeed;
                        if (jumpEdge != null && jumpEdge.LockedAirTime > 0f)
                            airTimeEst = jumpEdge.LockedAirTime;

                        speed = Mathf.Clamp(requiredSpeed, _walkSpeed * 0.5f, _sprintSpeed);
                        dir = jumpFaceDir;
                        _jumpDir = jumpFaceDir;
                        LookAtDirection(jumpFaceDir);

                        if (_cc.isGrounded)
                        {
                            // Resolve target takeoff speed/direction FIRST — every phase
                            // (approach, run-up, commit) needs them, not just the lip.
                            float targetTakeoffSpeed = speed; // default from physics calc
                            if (jumpEdge != null)
                            {
                                if (jumpEdge.TakeoffSpeed > 0.1f)
                                    targetTakeoffSpeed = jumpEdge.TakeoffSpeed;
                                else if (jumpEdge.LockedSpeed > 0f)
                                    targetTakeoffSpeed = jumpEdge.LockedSpeed;
                            }
                            targetTakeoffSpeed = Mathf.Clamp(targetTakeoffSpeed, _walkSpeed, _sprintSpeed);

                            Vector3 takeoffDir = jumpFaceDir;
                            if (jumpEdge != null && jumpEdge.TakeoffDir.sqrMagnitude > 0.01f)
                                takeoffDir = jumpEdge.TakeoffDir;

                            Vector3 takeoffPos = takeoffNode != null ? takeoffNode.Position : transform.position;
                            float distToTakeoff = new Vector3(
                                takeoffPos.x - transform.position.x, 0,
                                takeoffPos.z - transform.position.z).magnitude;

                            // Actual horizontal velocity — the smoothed-input check used
                            // before let bots leave the lip while the CC was still
                            // accelerating, which is why long jumps kept falling short.
                            Vector3 horizVel = _cc.velocity; horizVel.y = 0;
                            float curSpeed = horizVel.magnitude;

                            // Fast jumps need acceleration room: a run-up point behind the
                            // lip along the takeoff line, proportional to required speed.
                            float runupDist = Mathf.Lerp(0.8f, 3.5f,
                                Mathf.InverseLerp(_walkSpeed, _sprintSpeed, targetTakeoffSpeed));
                            Vector3 runupPoint = takeoffPos - takeoffDir * runupDist;
                            bool needsFastTakeoff = targetTakeoffSpeed > _walkSpeed + 0.5f;

                            bool atLip = IsEdgeAhead(jumpFaceDir, 0.7f) || jumpHorizDist < 1.5f;

                            // PHASE 0: Backing off from the lip to build a run-up.
                            if (_jumpBackoffTimer > 0f)
                            {
                                _jumpBackoffTimer -= Time.deltaTime;
                                Vector3 toRunup = runupPoint - transform.position; toRunup.y = 0;
                                if (toRunup.magnitude < 0.4f)
                                {
                                    _jumpBackoffTimer = 0f; // in position — charge this frame
                                }
                                else
                                {
                                    dir = toRunup.normalized;
                                    speed = _sprintSpeed;
                                    LookAtDirection(takeoffDir); // keep eyes on the jump
                                }
                            }

                            if (_jumpBackoffTimer > 0f)
                            {
                                // still repositioning — no jump this frame
                            }
                            // PHASE 1: Walk/sprint to the takeoff node
                            else if (distToTakeoff > 0.5f && takeoffNode != null && !atLip)
                            {
                                // Fast jumps route through the run-up point so the bot
                                // arrives already moving along the takeoff line.
                                Vector3 goal = takeoffPos;
                                if (needsFastTakeoff && distToTakeoff > runupDist * 0.7f)
                                {
                                    Vector3 toTk = takeoffPos - transform.position; toTk.y = 0;
                                    if (toTk.sqrMagnitude > 0.01f
                                        && Vector3.Dot(toTk.normalized, takeoffDir) < 0.7f)
                                        goal = runupPoint; // approaching from the side — swing behind first
                                }
                                Vector3 toGoal = goal - transform.position;
                                toGoal.y = 0;
                                if (toGoal.sqrMagnitude > 0.1f) dir = toGoal.normalized;
                                speed = (needsFastTakeoff || _inJumpChain) ? _sprintSpeed : _walkSpeed;
                            }
                            // PHASE 2: At the lip — commit and jump on REAL speed
                            else if (atLip)
                            {
                                dir = takeoffDir;
                                speed = targetTakeoffSpeed;
                                _currentHorizInput = Mathf.MoveTowards(_currentHorizInput, 1f, 5f * Time.deltaTime);
                                LookAtDirection(takeoffDir);

                                float facingDot = Vector3.Dot(transform.forward, takeoffDir);
                                bool facingOk = facingDot > 0.85f;
                                bool speedOk = jumpHorizDist < 1.0f
                                    || curSpeed >= targetTakeoffSpeed * 0.8f;

                                // Too slow for this jump and standing at the lip: back off
                                // once to build a run-up instead of leaping short into the
                                // gap. One attempt per few seconds — never ping-pong.
                                if (facingOk && !speedOk && needsFastTakeoff
                                    && distToTakeoff < 1.0f && Time.time >= _jumpBackoffCooldownUntil)
                                {
                                    _jumpBackoffTimer = 1.2f;
                                    _jumpBackoffCooldownUntil = Time.time + 4f;
                                }
                                // After a failed/cooling backoff, take the jump anyway —
                                // air-strafe correction beats standing at the lip forever.
                                else if (facingOk && (speedOk || Time.time < _jumpBackoffCooldownUntil))
                                {
                                    // Air-strafe: target the landing node directly so the
                                    // bot nudges itself back onto the node mid-arc.
                                    _airStrafeTarget = nodePos;
                                    _airStrafeActive = true;
                                    if (TryJump(JumpReason.GraphJump, takeoffDir,
                                        intentionalTime: airTimeEst + 0.1f, jumpEdge: jumpEdge))
                                    {
                                        jumped = true;
                                        dir = _jumpDir;
                                    }
                                }
                            }
                            // PHASE 3: Approaching edge — run toward it
                            else
                            {
                                speed = Mathf.Max(speed, needsFastTakeoff ? _sprintSpeed : _walkSpeed);
                            }
                        }
                        else
                        {
                            // Airborne — lock direction, no changes
                            _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.3f);
                        }
                    }

                    // Handle slide edges — match player slide exactly
                    if (nextEdgeType == EdgeType.Slide && _cc.isGrounded && !_isSliding)
                    {
                        Vector3 slideDir = nodePos - transform.position;
                        slideDir.y = 0;
                        if (slideDir.sqrMagnitude > 0.01f) slideDir.Normalize();
                        else slideDir = transform.forward;

                        InitSlide(slideDir, duration: 0.8f);
                    }

                    // Handle ladder edges — approach and climb
                    if (nextEdgeType == EdgeType.Ladder && !_onLadder)
                    {
                        // Move toward the ladder node — HandleLadder will grab onto it
                        dir = (nodePos - transform.position);
                        dir.y = 0;
                        if (dir.sqrMagnitude > 0.01f) dir.Normalize();
                    }

                    // Navmesh ladder links carry no graph edge: the next waypoint just sits
                    // well above us (the ladder top). Steer into the nearby ladder so the
                    // grab logic takes over — the flattened direction alone would circle
                    // beneath the target forever.
                    if (nextEdge == null && !_onLadder && _cc.isGrounded)
                    {
                        Vector3 toWp = nodePos - transform.position;
                        float horizToWp = new Vector2(toWp.x, toWp.z).magnitude;
                        if (toWp.y > 1.5f && horizToWp < 3.5f)
                        {
                            Collider ladder = FindNearbyLadder(6f);
                            if (ladder != null)
                            {
                                Vector3 toLadder = ladder.ClosestPoint(transform.position + Vector3.up) - transform.position;
                                toLadder.y = 0f;
                                if (toLadder.sqrMagnitude > 0.04f) dir = toLadder.normalized;
                            }
                        }
                    }

                    dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
                    else dir.Normalize();
                }
                else
                {
                    // Path exhausted or empty
                    bool seekingLadder = false;
                    if (_cc.isGrounded && !_onLadder && target.y > transform.position.y + 2f && _stuckTimer > 0.5f)
                    {
                        Collider ladder = FindNearbyLadder(8f);
                        if (ladder != null)
                        {
                            Vector3 toLadder = ladder.ClosestPoint(transform.position) - transform.position;
                            toLadder.y = 0;
                            if (toLadder.sqrMagnitude > 0.1f)
                            {
                                dir = toLadder.normalized;
                                seekingLadder = true;
                            }
                        }
                    }
                    if (!seekingLadder)
                    {
                        dir = target - transform.position;
                        dir.y = 0f;
                        if (dir.sqrMagnitude < 0.1f) dir = transform.forward;
                    }
                    dir.Normalize();
                }
            }
            else
            {
                // ---- No graph data — direct line to target ----
                dir = target - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.1f) dir = transform.forward;
                dir.Normalize();
            }

            // ---- Commit direction override ----
            // If we're committed to a direction (from TryAngledDirections wall redirect),
            // honor it instead of the graph path direction. Without this, graph path following
            // overwrites commitDir every frame causing oscillation.
            bool commitActive = _commitTimer > 0f && _commitDir.sqrMagnitude > 0.01f;
            if (commitActive)
                dir = _commitDir;

            // ---- Door interaction: open closed doors in our path ----
            TryOpenDoor(dir);

            // ---- Phase 2: Reactive steering (obstacle avoidance + jump attempts) ----
            // Skip all reactive steering while being launched by a zone — ride the force like a player
            // Also skip while committed to a direction — let the commit play out
            int wallMask = WALL_MASK;
            bool zoneLaunched = _zoneForceDuration > 0f;

            // Obstacle jump: feet blocked, clear above, safe LATERAL landing ahead
            // Skip when on a Jump/Fall edge — the jump handler already dealt with it
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder && !_nearLadder
                && nextEdgeType == EdgeType.Walk && _intentionalJumpTimer <= 0f)
            {
                // Raycast above step offset — check if it's a wall or just a slope
                // Trigger range tightened: 0.9m (was 1.2m) — fires only when the bot is
                // close enough that the jump arc will land ON the box instead of short.
                const float BOX_FACE_DIST = 0.9f;
                bool feetBlocked = false;
                if (Physics.Raycast(transform.position + Vector3.up * 0.7f, dir, out RaycastHit feetHit, BOX_FACE_DIST, wallMask, QueryTriggerInteraction.Ignore))
                {
                    float slopeAngle = Vector3.Angle(feetHit.normal, Vector3.up);
                    feetBlocked = slopeAngle > 65f; // Only treat as wall if steeper than slope limit
                }
                if (feetBlocked)
                {
                    bool waistClear = !Physics.Raycast(transform.position + Vector3.up * 1f, dir, BOX_FACE_DIST, wallMask, QueryTriggerInteraction.Ignore);
                    bool headClear = !Physics.Raycast(transform.position + Vector3.up * 1.7f, dir, BOX_FACE_DIST, wallMask, QueryTriggerInteraction.Ignore);

                    // Check for landing: on top of obstacle (close) OR ahead (far)
                    bool safeLanding = false;
                    if (waistClear || headClear)
                    {
                        int gMask = GROUND_MASK;
                        // Check 1: landing ON TOP of the obstacle (stairs/boxes — close and above)
                        Vector3 closeCheck = transform.position + dir * 0.8f + Vector3.up * 2.5f;
                        if (Physics.Raycast(closeCheck, Vector3.down, out RaycastHit closeHit, 3f, gMask))
                        {
                            if (closeHit.point.y > transform.position.y + 0.3f)
                                safeLanding = true; // Ground above us = box/stair top
                        }
                        // Check 2: landing AHEAD (gap crossing)
                        if (!safeLanding)
                        {
                            Vector3 farCheck = transform.position + dir * 2f + Vector3.up * 2f;
                            if (Physics.Raycast(farCheck, Vector3.down, out RaycastHit farHit, 6f, gMask))
                            {
                                float landHoriz = new Vector3(farHit.point.x - transform.position.x, 0,
                                    farHit.point.z - transform.position.z).magnitude;
                                if (landHoriz > 0.5f) safeLanding = true;
                            }
                        }
                    }

                    if (safeLanding && (waistClear || headClear))
                    {
                        // Safe landing confirmed + space above — jump forward.
                        // Seed air-strafe target 2.2m ahead (typical box-top distance).
                        _airStrafeTarget = transform.position + dir * 2.2f;
                        _airStrafeActive = true;
                        jumped = TryJump(JumpReason.Obstacle, dir);
                    }
                    else
                    {
                        // No safe landing or fully blocked — go around, don't blind jump
                        dir = TryAngledDirections(dir, wallMask);
                    }
                }
                else
                {
                    // Feet ray (0.7m) clear — check for KNEE-HIGH blockers underneath it.
                    jumped = TryKneeHop(dir, wallMask);
                }
            }

            // Proactive low-clearance handling.
            // Trigger 1: immediate crouch when head is blocked but crouch lane is clear.
            // Trigger 2: slide when a crawl-space wall blocks waist movement and we're stuck.
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder
                && _intentionalJumpTimer <= 0f)
            {
                var slideObs = CheckObstructions(dir);

                if (ConfirmLowCeiling(dir))
                {
                    // Low ceiling corridor: crouch and keep moving instead of head-bumping.
                    if (!_isCrouching)
                        StartCrouch(0.8f);
                    else
                        _crouchTimer = Mathf.Max(_crouchTimer, 0.25f);
                    _stuckTimer = 0f;
                }
                else if (slideObs.CrouchClear && slideObs.WaistBlocked && _stuckTimer > 0.7f)
                {
                    // Wall with crawl space — burst through with a slide.
                    InitSlide(dir);
                    _stuckTimer = 0f;
                }
            }

            // Edge detection — check for edges when grounded (skip during commit to prevent oscillation)
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_onLadder && !_nearLadder)
            {
                // Check for path target across gap first
                bool hasPathTarget = _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count;
                Vector3 gapJumpDir = dir;
                bool targetAcrossGap = false;

                if (hasPathTarget)
                {
                    Vector3 toTarget = _graphPath[_graphPathIndex].Position - transform.position;
                    float hDist = new Vector3(toTarget.x, 0, toTarget.z).magnitude;
                    float totalDist = toTarget.magnitude;
                    if (hDist > 0.5f && totalDist < Plugin.GetMaxJumpDist())
                    {
                        gapJumpDir = new Vector3(toTarget.x, 0, toTarget.z).normalized;
                        targetAcrossGap = true;
                    }
                }

                // Also check weapon/player target directly — even without path nodes
                if (!targetAcrossGap)
                {
                    Vector3 directTarget = Vector3.zero;
                    if (_weaponTarget != null) directTarget = _weaponTarget.position;
                    else if (_playerTarget != null) directTarget = _playerTarget.position;

                    if (directTarget != Vector3.zero)
                    {
                        Vector3 toTarget = directTarget - transform.position;
                        float hDist = new Vector3(toTarget.x, 0, toTarget.z).magnitude;
                        float totalDist = toTarget.magnitude;
                        if (hDist > 1f && totalDist < Plugin.GetMaxJumpDist())
                        {
                            gapJumpDir = new Vector3(toTarget.x, 0, toTarget.z).normalized;
                            targetAcrossGap = true;
                        }
                    }
                }

                // Check edges in BOTH movement dir AND path target dir
                bool edgeInMoveDir = IsEdgeAhead(dir, 1.5f);
                bool edgeInTargetDir = targetAcrossGap && IsEdgeAhead(gapJumpDir, 1.5f);
                bool edgeDetected = edgeInMoveDir || edgeInTargetDir;

                if (edgeDetected)
                {
                    bool isPlayMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play;
                    bool provenGraphJump = false;
                    if (_lastReachedNode != null && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count && NavGraph.Instance != null)
                    {
                        var edge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                        provenGraphJump = IsPlayerProvenJumpEdge(edge);
                    }
                    bool shouldJump = nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.Fall
                        || nextEdgeType == EdgeType.WallJump || targetAcrossGap;
                    if (isPlayMode && !provenGraphJump)
                        shouldJump = false;

                    if (shouldJump)
                    {
                        // Check close-range edge in jump direction for timing
                        bool atEdge = IsEdgeAhead(gapJumpDir, 0.7f);

                        if (atEdge)
                        {
                            // At the edge — jump NOW at full sprint
                            // Seed air-strafe: use path target if we have one, else project ~3m ahead.
                            if (targetAcrossGap && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                                _airStrafeTarget = _graphPath[_graphPathIndex].Position;
                            else
                                _airStrafeTarget = transform.position + gapJumpDir * 3f;
                            _airStrafeActive = true;
                            if (TryJump(JumpReason.EdgeAhead, gapJumpDir, intentionalTime: 1.5f))
                            {
                                jumped = true;
                                dir = gapJumpDir;
                                speed = _sprintSpeed; // Max speed for max distance
                            }
                        }
                        else
                        {
                            // Approaching — sprint toward edge, don't turn away
                            dir = gapJumpDir;
                            speed = _sprintSpeed;
                            _gapApproachSprint = true; // exempt from the edge speed governor
                        }
                    }
                    else
                    {
                        // No target across — turn away from edge
                        dir = TryAngledDirections(dir, wallMask);
                        if (!HasGroundFootprintAhead(dir, 0.8f) && TryGetSafeEdgeEscapeDir(gapJumpDir, out Vector3 escapeDir))
                        {
                            dir = escapeDir;
                            _commitDir = escapeDir;
                            _commitTimer = Mathf.Max(_commitTimer, 0.45f);
                        }
                    }
                }
            }

            // Proactive wall check — redirect if walking into a wall
            // Skip when: on jump/fall/ladder edge, near a ladder, committed direction active
            if (!zoneLaunched && !commitActive && !jumped && !_onLadder && !_nearLadder
                && _intentionalJumpTimer <= 0f
                && nextEdgeType == EdgeType.Walk)
            {
                // Check surface angle — slopes under 65° are walkable, not walls
                bool headBlocked = false, bodyBlocked = false;
                if (Physics.Raycast(transform.position + Vector3.up * 1.5f, dir, out RaycastHit headHit, 0.5f, wallMask, QueryTriggerInteraction.Ignore))
                    headBlocked = Vector3.Angle(headHit.normal, Vector3.up) > 65f;
                if (Physics.Raycast(transform.position + Vector3.up * 0.8f, dir, out RaycastHit bodyHit, 0.5f, wallMask, QueryTriggerInteraction.Ignore))
                    bodyBlocked = Vector3.Angle(bodyHit.normal, Vector3.up) > 65f;
                if (headBlocked && bodyBlocked)
                    dir = TryAngledDirections(dir, wallMask);
            }

            // Wall slide via CC collision feedback — NOT during intentional jumps
            if (!zoneLaunched && !jumped && !_onLadder && !_nearLadder && _intentionalJumpTimer <= 0f)
            {
                _collisionTimer -= Time.deltaTime;
                if (_collisionTimer > 0f && _lastCollisionNormal.sqrMagnitude > 0.01f)
                {
                    Vector3 colNormal = _lastCollisionNormal; colNormal.y = 0; colNormal.Normalize();
                    float dot = Vector3.Dot(dir, -colNormal);
                    if (dot > 0.3f) // Deflect on any significant wall contact
                    {
                        Vector3 slideDir = dir - Vector3.Dot(dir, -colNormal) * -colNormal;
                        if (slideDir.sqrMagnitude > 0.01f)
                            dir = slideDir.normalized;
                    }
                }
            }

            // Explore jump: at elevation changes during wander/explore, try jumping
            // Only if actually stuck on a wall (not a walkable slope)
            if (!zoneLaunched && !jumped && _cc.isGrounded && !_onLadder && State == BotState.FindWeapon && _stuckTimer > 1.5f)
            {
                // Check if what's ahead is a slope (walkable) vs a wall
                bool isWall = false;
                if (Physics.Raycast(transform.position + Vector3.up * 0.5f, dir, out RaycastHit slopeCheck, 1.5f, wallMask, QueryTriggerInteraction.Ignore))
                {
                    float sAngle = Vector3.Angle(slopeCheck.normal, Vector3.up);
                    isWall = sAngle > 65f; // Only jump at actual walls
                }

                if (isWall)
                {
                    int gMask = GROUND_MASK;
                    Vector3 aheadCheck = transform.position + dir * 1.5f + Vector3.up * 2f;
                    if (Physics.Raycast(aheadCheck, Vector3.down, out RaycastHit gHit, 6f, gMask, QueryTriggerInteraction.Ignore))
                    {
                        float heightDiff = gHit.point.y - transform.position.y;
                        if (heightDiff > 0.3f && heightDiff < 3f)
                        {
                            if (TryJump(JumpReason.ExploreStuck, dir))
                            {
                                jumped = true;
                                _stuckTimer = 0f;
                            }
                        }
                    }
                }
            }

            // ---- Direction lock during slide/crouch ----
            // While sliding, maintain the direction locked at slide start — no turning
            if (_isSliding && _slideLockedDir.sqrMagnitude > 0.01f)
            {
                dir = _slideLockedDir;
            }

            // ---- Side-edge avoidance: push away from edges perpendicular to movement ----
            if (_cc.isGrounded && !_isSliding && _intentionalJumpTimer <= 0f)
                dir = AvoidSideEdges(dir);

            // ---- Emergency edge stop: very close check (0.5m), reverse if about to walk off ----
            // Skip during commit — the committed direction was already validated
            if (!commitActive && _cc.isGrounded && !jumped && _intentionalJumpTimer <= 0f
                && nextEdgeType != EdgeType.Jump && nextEdgeType != EdgeType.Fall)
            {
                if (IsEdgeAhead(dir, 0.5f))
                    dir = -dir; // Emergency reverse
            }

            // ---- Jump direction: trajectory replay OR fallback direction lock ----
            if (_intentionalJumpTimer > 0f && !_onLadder && _jumpDir.sqrMagnitude > 0.01f)
            {
                if (!_cc.isGrounded)
                {
                    // TRAJECTORY REPLAY: steer CC.Move toward recorded waypoints
                    if (_trajActive && _currentJumpEdge != null && _currentJumpEdge.AirSampleCount > 0)
                    {
                        float airTime = Time.time - _jumpStartTime;

                        // Find the two bracketing samples for current time
                        var positions = _currentJumpEdge.AirPositions;
                        var timestamps = _currentJumpEdge.AirTimestamps;
                        int count = _currentJumpEdge.AirSampleCount;

                        // Advance index to current time
                        while (_trajIndex < count - 1 && timestamps[_trajIndex + 1] < airTime)
                            _trajIndex++;

                        // Interpolate target position between two samples
                        Vector3 targetPos;
                        if (_trajIndex >= count - 1)
                        {
                            targetPos = positions[count - 1];
                        }
                        else
                        {
                            float t0 = timestamps[_trajIndex];
                            float t1 = timestamps[_trajIndex + 1];
                            float lerp = (t1 > t0) ? Mathf.Clamp01((airTime - t0) / (t1 - t0)) : 0f;
                            targetPos = Vector3.Lerp(positions[_trajIndex], positions[_trajIndex + 1], lerp);
                        }

                        // Full 3D correction toward recorded position
                        Vector3 toTarget = targetPos - transform.position;
                        float totalDrift = toTarget.magnitude;

                        // HARD RAIL ("cheat"): the recorded arc is ground truth — a jump a
                        // player demonstrated must land every time. Allow 0.25m of slack
                        // around the recorded position and close the rest IMMEDIATELY
                        // (capped at 4m/frame; CC.Move still respects walls). The old
                        // proportional 10 m/s pull let drift accumulate and bots missed.
                        if (totalDrift > 0.25f)
                        {
                            Vector3 correction = toTarget * (1f - 0.25f / totalDrift);
                            if (correction.sqrMagnitude > 16f)
                                correction = correction.normalized * 4f;
                            _cc.Move(correction);
                        }

                        // Horizontal steering for the normal move
                        Vector3 horizTarget = new Vector3(toTarget.x, 0, toTarget.z);
                        if (horizTarget.sqrMagnitude > 0.01f)
                        {
                            dir = horizTarget.normalized;
                            float horizDist = horizTarget.magnitude;
                            speed = Mathf.Clamp(horizDist / Mathf.Max(Time.deltaTime, 0.001f), 0.5f, _sprintSpeed * 1.5f);
                        }
                        else
                        {
                            dir = _jumpDir;
                            speed = 0.5f;
                        }

                        // Authoritative vertical velocity from trajectory data
                        // With gravity guarded, we are the sole authority on vertical movement
                        // The recorded positions already encode the correct parabolic arc
                        float trajDt = Mathf.Max(Time.deltaTime, 0.001f);
                        float neededVY = (targetPos.y - transform.position.y) / trajDt;
                        _verticalVelocity = Mathf.Clamp(neededVY, _maxFallSpeed, _jumpForce * 1.5f);

                        _currentHorizInput = 1f;
                        _jumpDir = dir;

                        // ARC CONSUMED: past the final sample without a landing means the
                        // recording ends short of (or above) the real ground here. Hand
                        // control back to physics so gravity finishes the landing — the
                        // rail must NOT hold the bot hovering at the last sample.
                        if (_trajIndex >= count - 1 && airTime > timestamps[count - 1] + 0.1f)
                        {
                            _trajActive = false;
                            _currentJumpEdge = null;
                            _verticalVelocity = Mathf.Min(_verticalVelocity, 0f);
                        }
                        // BAD MATCH ABORT: persistently huge drift means this recording
                        // doesn't fit the current takeoff (mesh-link lookups match within
                        // 2m). Stop fighting it — physics + air strafe finish the jump.
                        else if (totalDrift > 3.5f)
                        {
                            _trajDriftTimer += Time.deltaTime;
                            if (_trajDriftTimer > 0.35f)
                            {
                                _trajDriftTimer = 0f;
                                _trajActive = false;
                                _currentJumpEdge = null;
                                Plugin.Log.LogInfo($"[{BotName}] Trajectory replay aborted — recording doesn't fit takeoff (drift {totalDrift:F1}m)");
                            }
                        }
                        else
                        {
                            _trajDriftTimer = 0f;
                        }
                    }
                    // FALLBACK: no trajectory data — lock direction with single mid-air correction
                    else
                    {
                        dir = _jumpDir;
                        // Use locked speed from previous success if available
                        if (_currentJumpEdge != null && _currentJumpEdge.LockedSpeed > 0f)
                            speed = _currentJumpEdge.LockedSpeed;
                        _currentHorizInput = 1f; // Force full speed application

                        if (!_jumpMidCorrected && _verticalVelocity < 1f && _verticalVelocity > -3f)
                        {
                            _jumpMidCorrected = true;
                            if (_graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                            {
                                Vector3 toLand = _graphPath[_graphPathIndex].Position - transform.position;
                                toLand.y = 0;
                                if (toLand.sqrMagnitude > 1f)
                                {
                                    Vector3 landDir = toLand.normalized;
                                    float dot = Vector3.Dot(_jumpDir, landDir);
                                    if (dot < 0.94f)
                                    {
                                        dir = Vector3.Lerp(_jumpDir, landDir, 0.25f).normalized;
                                        _jumpDir = dir;
                                    }
                                }
                            }
                        }
                    }

                    // Wall jumps: slightly stronger continuous steering
                    if (_wallJumpCount > 0)
                    {
                        Vector3 wallTarget = _jumpDir;
                        if (_graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                        {
                            Vector3 toNode = _graphPath[_graphPathIndex].Position - transform.position;
                            toNode.y = 0;
                            if (toNode.sqrMagnitude > 0.5f) wallTarget = toNode.normalized;
                        }
                        dir = Vector3.Lerp(_jumpDir, wallTarget, 0.25f).normalized;
                        _jumpDir = dir;
                    }

                    // Set landing pause when about to land
                    if (_verticalVelocity < -1f)
                        _landingFollowTimer = 0.5f;
                }
                else
                {
                    dir = _jumpDir;
                }
            }

            // ---- Landing pause: brief stop after landing to prevent overshooting ledges ----
            // Skip pause if next edge is a jump/walljump (chain jumps need momentum)
            if (_landingFollowTimer > 0f && _cc.isGrounded)
            {
                bool nextIsJump = false;
                if (_graphPath.Count > 0 && _graphPathIndex < _graphPath.Count && _lastReachedNode != null)
                {
                    var edges = NavGraph.Instance?.GetEdgesFrom(_lastReachedNode.Id);
                    if (edges != null)
                    {
                        foreach (var e in edges)
                        {
                            if (e.To == _graphPath[_graphPathIndex].Id &&
                                (e.Type == EdgeType.Jump || e.Type == EdgeType.WallJump))
                            { nextIsJump = true; break; }
                        }
                    }
                }

                if (nextIsJump)
                {
                    // Chain jump — maintain momentum, record success, pre-orient
                    _landingFollowTimer = 0f;
                    _inJumpChain = true;
                    _chainJumpCount++;

                    // Record success on the edge we just completed
                    if (_currentJumpEdge != null)
                    {
                        _currentJumpEdge.SuccessCount++;
                        if (_currentJumpEdge.LockedAirTime <= 0f)
                            _currentJumpEdge.LockedAirTime = Time.time - _jumpStartTime;
                    }

                    // Keep speed and intentional timer active through the chain
                    _intentionalJumpTimer = 0.3f;
                    _currentHorizInput = 1f;

                    // Pre-orient toward next jump's takeoff direction
                    NavEdge nextJumpEdge = null;
                    if (NavGraph.Instance != null && _lastReachedNode != null
                        && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                        nextJumpEdge = NavGraph.Instance.GetEdgeBetween(
                            _lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                    if (nextJumpEdge != null && nextJumpEdge.TakeoffDir.sqrMagnitude > 0.01f)
                        _jumpDir = nextJumpEdge.TakeoffDir;

                    // Clear current edge but don't full-clear jump state
                    _currentJumpEdge = null;
                    _trajActive = false;
                    _trajIndex = 0;
                }
                else
                {
                    // Save jump direction for post-landing bias BEFORE clearing
                    if (_jumpDir.sqrMagnitude > 0.01f)
                        _lastLandingDir = _jumpDir;

                    // Record successful jump — lock speed/airTime on the edge
                    if (_currentJumpEdge != null && _currentJumpEdge.LockedSpeed <= 0f)
                    {
                        float airTime = Time.time - _jumpStartTime;
                        // Use player-recorded TakeoffSpeed if available (most accurate)
                        // Otherwise estimate from current movement
                        float lockSpeed = _walkSpeed;
                        if (_currentJumpEdge.TakeoffSpeed > 0.1f)
                            lockSpeed = _currentJumpEdge.TakeoffSpeed;
                        else
                            lockSpeed = Mathf.Max(speed * _currentHorizInput, _walkSpeed);
                        _currentJumpEdge.LockedSpeed = lockSpeed;
                        _currentJumpEdge.LockedAirTime = airTime;
                        _currentJumpEdge.SuccessCount++;
                        if (_currentJumpEdge.TakeoffDir.sqrMagnitude < 0.01f && _lastLandingDir.sqrMagnitude > 0.01f)
                            _currentJumpEdge.TakeoffDir = _lastLandingDir;
                    }

                    // Kill jump state
                    ClearJumpState();

                    _landingFollowTimer -= Time.deltaTime;
                    if (_landingFollowTimer <= 0f)
                    {
                        // Repath from new position after landing
                        _graphPath.Clear();
                        _graphPathIndex = 0;
                        _repathTimer = 0f;

                        // Bias: continue in jump direction briefly before new path takes over
                        // Prevents immediate backtrack after landing
                        if (_lastLandingDir.sqrMagnitude > 0.01f)
                        {
                            _commitDir = _lastLandingDir;
                            _commitTimer = 0.4f;
                            _lastLandingDir = Vector3.zero;
                        }
                    }
                    // During pause: ABSOLUTE STOP — kill everything horizontal
                    dir = Vector3.zero;
                    speed = 0f;
                    _currentHorizInput = 0f;
                    _slideForceFactor = 0f;   // Kill slide momentum
                    _slideForce = Vector3.zero;
                    _commitDir = Vector3.zero; // Kill any commit direction
                    _commitTimer = 0f;
                }
            }

            // ---- Phase 3: Facing ----
            if ((_onLadder || _ladderDismountTimer > 0f) && _ladderFaceDir.sqrMagnitude > 0.01f)
                LookAtDirection(_ladderFaceDir);
            else if (_isSliding && _slideLockedDir.sqrMagnitude > 0.01f)
                LookAtDirection(_slideLockedDir); // Face slide direction, not target
            else if (_isShooting && _playerTarget != null)
                LookAtTarget(_playerTarget.position);
            else
                LookAtDirection(dir);

            // ---- Phase 4: Speed & movement ----
            bool grounded = _cc.isGrounded;
            float targetSpeed;

            bool sprinting = speed >= _sprintSpeed || _intentionalJumpTimer > 0f;

            // During intentional jumps: ALWAYS use the calculated speed — never override with sprint/air
            if (_intentionalJumpTimer > 0f && !grounded)
            {
                targetSpeed = speed; // Trajectory replay or LockedSpeed — already set correctly
            }
            else
            {
                if (!grounded && sprinting) targetSpeed = _sprintAirSpeed;
                else if (!grounded) targetSpeed = _airSpeed;
                else if (_isCrouching) targetSpeed = _crouchSpeed;
                else if (sprinting) targetSpeed = _sprintSpeed;
                else targetSpeed = _walkSpeed;
            }

            // Slow down when approaching next walk node — prevents overshooting
            if (grounded && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count
                && _intentionalJumpTimer <= 0f && !_isSliding)
            {
                float distToNext = Vector3.Distance(transform.position, _graphPath[_graphPathIndex].Position);
                if (distToNext < 1.5f)
                {
                    // Scale speed down as we approach: full speed at 1.5m, walk at 0.5m, near-stop at 0.2m
                    float slowFactor = Mathf.Clamp01((distToNext - 0.2f) / 1.3f);
                    targetSpeed = Mathf.Lerp(_walkSpeed * 0.3f, targetSpeed, slowFactor);
                }
            }

            // EDGE GOVERNOR: never sprint blindly at a cliff lip. Approach edges at walk
            // speed unless this frame is a deliberate gap-jump run-up or a jump is
            // already in flight — kills the "walked off at full speed" class of deaths.
            if (grounded && targetSpeed > _walkSpeed && !_gapApproachSprint
                && _intentionalJumpTimer <= 0f && !_isSliding && !_onLadder
                && IsEdgeAhead(dir, 2.2f))
            {
                targetSpeed = _walkSpeed;
            }

            // During slide: force impulse handles movement, reduce normal speed to near-zero
            // so the slide force is the primary mover (matches FPC behavior)
            if (_isSliding) targetSpeed = 0.5f;

            // Sprint slide
            if (sprinting && grounded && !_isSliding && !_isShooting && _slideResetTimer <= 0f)
            {
                // Disable random sprint-slide flavor during normal navigation; looked too spammy.
                _sprintSlideChance = Mathf.Max(_sprintSlideChance, 2f);
            }

            // Don't accelerate during landing pause — keep at zero
            bool inLandingPause = _landingFollowTimer > 0f && grounded;
            if (!inLandingPause)
            {
                // Force full speed during active jumps — don't decelerate mid-arc
                if (_intentionalJumpTimer > 0f && !grounded)
                    _currentHorizInput = 1f;
                else if (dir.sqrMagnitude < 0.001f)
                    _currentHorizInput = Mathf.Lerp(_currentHorizInput, 0f, _acceleration * 2f * Time.deltaTime);
                else
                    _currentHorizInput = Mathf.Lerp(_currentHorizInput, 1f, _acceleration * Time.deltaTime);

                // SMOOTHNESS: waypoint arrival slowdown.
                // Approaching the current path waypoint (not just the final target) damps input
                // so the bot stops lurching through waypoints at sprint speed. Only applies while
                // grounded — in-air motion needs full input. Corners look much cleaner with this.
                if (grounded && _graphPath != null && _graphPathIndex < _graphPath.Count && _intentionalJumpTimer <= 0f)
                {
                    Vector3 toWp = _graphPath[_graphPathIndex].Position - transform.position;
                    toWp.y = 0f;
                    float wpDist = toWp.magnitude;
                    if (wpDist < 1.5f)
                    {
                        // Ease from full-speed at 1.5m down to 0.55x at 0.4m (never to zero —
                        // that causes stutter; let dist-<0.4 be handled by waypoint advance).
                        float slowFactor = Mathf.Lerp(0.55f, 1f, Mathf.InverseLerp(0.4f, 1.5f, wpDist));
                        _currentHorizInput = Mathf.Min(_currentHorizInput, slowFactor);
                    }
                }
            }

            // Safety: if crouching but not sliding and no combat crouch timer, force uncrouch
            // Prevents permanent crouch state from bugs
            if (_isCrouching && !_isSliding && _crouchTimer <= 0f && grounded)
            {
                bool standBlocked = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up, 1.2f,
                    WALL_MASK, QueryTriggerInteraction.Ignore);
                if (!standBlocked)
                {
                    _isCrouching = false;
                    ApplyStance(STAND_HEIGHT, SKIN_STANDING);
                    if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", false);
                    if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", false);
                }
                else
                {
                    // Keep crouch while headroom is still blocked in low-clearance passages.
                    _crouchTimer = 0.2f;
                }
            }

            // Decay slide force
            Vector3 forceComponent = Vector3.zero;
            if (_slideForceFactor > 0f)
            {
                forceComponent = _slideForce.normalized * _slideForceFactor;
                _slideForceFactor -= 3f * Time.deltaTime;
                if (_slideForceFactor < 0f) _slideForceFactor = 0f;
            }

            // ---- Final move ----
            Vector3 move;
            if (_onLadder)
            {
                // Climb up + pull toward ladder center + face into ladder surface
                move = Vector3.up * _ladderSpeed;

                // Pull toward ladder center horizontally (centers bot on the ladder).
                // Stronger pull (3.0 vs 1.5) to resist sideways drift when climbing diagonally.
                Vector3 toCenter = _lastLadderPos - transform.position;
                toCenter.y = 0;
                float centerDist = toCenter.magnitude;
                if (centerDist > 0.15f)
                    move += toCenter.normalized * Mathf.Min(centerDist * 5f, 3f);

                // Slight pull into ladder surface to stay attached — TAPERED near the
                // collider top: grinding full force into the wall at the lip is what
                // pinned bots against it instead of letting them step over.
                if (_ladderFaceDir.sqrMagnitude > 0.01f)
                {
                    float facePull = (_ladderTopY > 0f && _ladderTopY - transform.position.y < 1.2f)
                        ? 0.25f : 0.8f;
                    move += _ladderFaceDir * facePull;
                }

                // Face into the ladder
                LookAtDirection(_ladderFaceDir);
            }
            else if (_ladderDismountTimer > 0f)
            {
                // Push toward the actual exit/platform, not blindly into the ladder face.
                // This stops bots from climbing a bit, colliding with the lip, then dropping.
                Vector3 exitDir = _ladderExitDir.sqrMagnitude > 0.01f
                    ? _ladderExitDir
                    : PickLadderExitDir(GetLadderObjective());
                // Never grind the dismount push into a wall — a bad exit pick used to
                // pin the bot against geometry at full sprint for the whole timer.
                if (Physics.Raycast(transform.position + Vector3.up * 0.9f, exitDir, 0.7f,
                    WALL_MASK, QueryTriggerInteraction.Ignore))
                {
                    Vector3 alt = TryAngledDirections(exitDir, WALL_MASK);
                    if (alt.sqrMagnitude > 0.01f) { exitDir = alt; _ladderExitDir = alt; }
                }
                move = exitDir * _sprintSpeed + Vector3.up * 4.2f;
                _intentionalJumpTimer = 0.35f;
            }
            else
            {
                // Low-pass the commanded direction: kills the single-frame flip-flop
                // jitter from waypoint switches and reactive nudges while still following
                // a genuine turn within ~0.1s. Reversals (>~100°) pass through instantly —
                // edge escapes and breakouts must never be softened.
                if (grounded && _intentionalJumpTimer <= 0f && _jumpBackoffTimer <= 0f
                    && dir.sqrMagnitude > 0.01f)
                {
                    if (_smoothedMoveDir.sqrMagnitude < 0.01f) _smoothedMoveDir = dir;
                    float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
                    _smoothedMoveDir = Vector3.Slerp(_smoothedMoveDir, dir, k);
                    if (Vector3.Dot(_smoothedMoveDir.normalized, dir.normalized) > -0.2f)
                        dir = _smoothedMoveDir.normalized * dir.magnitude;
                    else
                        _smoothedMoveDir = dir;
                }
                move = dir * targetSpeed * _currentHorizInput + forceComponent;
                move.y = _verticalVelocity;
            }
            // FINAL SAFETY: if grounded and not in an intentional jump, check for void ahead
            // This is the last line of defense — catches anything the earlier checks missed.
            // Lookahead scales with actual speed: the old fixed 0.8m gave a sprinting bot
            // ~0.07s of margin, which is why they still ran off map edges.
            if (grounded && _intentionalJumpTimer <= 0f && !_onLadder && _ladderDismountTimer <= 0f)
            {
                Vector3 horizMove = new Vector3(move.x, 0, move.z);
                if (horizMove.sqrMagnitude > 0.01f)
                {
                    float voidLookahead = Mathf.Clamp(horizMove.magnitude * 0.16f, 0.8f, 2.0f);
                    if (!HasGroundFootprintAhead(horizMove, voidLookahead)
                        && !IsImpulseZoneAhead(horizMove, voidLookahead)
                        && !RouteAuthorizesDrop(horizMove))
                    {
                        if (TryGetSafeEdgeEscapeDir(horizMove, out Vector3 escapeDir))
                        {
                            move.x = escapeDir.x * _walkSpeed * 0.75f;
                            move.z = escapeDir.z * _walkSpeed * 0.75f;
                            _commitDir = escapeDir;
                            _commitTimer = Mathf.Max(_commitTimer, 0.35f);
                        }
                        else
                        {
                            move.x = 0f;
                            move.z = 0f;
                        }
                        _slideForceFactor = 0f;
                        if (_graphPath.Count > 0)
                        {
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                        }
                    }
                }
            }

            float moveMagSqr = move.x * move.x + move.z * move.z;
            if (moveMagSqr > 0.0001f)
            {
                float invMag = 1f / Mathf.Sqrt(moveMagSqr);
                _lastMoveDir.x = move.x * invMag; _lastMoveDir.y = 0f; _lastMoveDir.z = move.z * invMag;
            }
            DoMove(move * Time.deltaTime);
        }

        /// <summary>
        /// Try angled directions when the direct path is blocked.
        /// Returns the best unblocked direction, or reverses if all blocked.
        /// </summary>
        /// <summary>
        /// Simple direct movement with no graph data. Walk toward target, avoid walls, jump obstacles.
        /// </summary>
        private void MoveTowardNodeless(Vector3 target, float speed)
        {
            _intentionalJumpTimer -= Time.deltaTime;
            _commitTimer -= Time.deltaTime;

            // ARRIVAL DAMPING: if we're within 0.8m of target horizontally, stop driving
            // toward it. Prevents the spaz-jitter where bots reach a node and oscillate
            // back/forth across it because tiny overshoots flip the direction each frame.
            Vector3 horizToTarget = target - transform.position;
            horizToTarget.y = 0f;
            float horizDistSqr = horizToTarget.sqrMagnitude;
            if (horizDistSqr < 0.64f) // 0.8m
            {
                float heightDiff = Mathf.Abs(target.y - transform.position.y);
                if (heightDiff < 2f)
                {
                    // Close in H and V — arrived, settle. (DoMove so ice slide still applies.)
                    if (_cc != null && _cc.enabled && !_movedThisFrame)
                        DoMove(new Vector3(0f, _verticalVelocity * Time.deltaTime, 0f));
                    _currentHorizInput = 0f;
                    return;
                }
                // Target is directly above or below (stacked bots, different floors).
                // Wander laterally — golden-angle rotation per bot so they spread out.
                float angle = (GetInstanceID() * 137.508f + Time.time * 45f) % 360f * Mathf.Deg2Rad;
                horizToTarget = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 3f;
                horizDistSqr = horizToTarget.sqrMagnitude;
            }

            // If committed to a direction (after wall redirect), hold it
            Vector3 dir;
            if (_commitTimer > 0f && _commitDir.sqrMagnitude > 0.01f)
            {
                dir = _commitDir;
            }
            else
            {
                dir = target - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.1f) dir = transform.forward;
                dir.Normalize();
            }

            bool jumped = false;

            // Shared reactive steering. On untrained maps (no usable graph), run in a
            // relaxed mode so bots don't get trapped in edge-reverse oscillation.
            bool relaxedNoGraph = NavGraph.Instance == null || !NavGraph.Instance.HasData || NavGraph.Instance.NodeCount < 10;
            ReactiveSteer(ref dir, ref jumped, target, WALL_MASK, relaxedNoGraph);

            // Proactive ladder seeking: if target is above us, scan for ladders nearby
            if (_cc.isGrounded && !_onLadder && target.y > transform.position.y + 2f && _stuckTimer > 1f)
            {
                Collider ladder = FindNearbyLadder(8f);
                if (ladder != null)
                {
                    Vector3 toLadder = ladder.ClosestPoint(transform.position) - transform.position;
                    toLadder.y = 0;
                    if (toLadder.sqrMagnitude > 0.1f)
                        dir = toLadder.normalized;
                }
            }

            LookAtDirection(dir);

            // Speed
            bool grounded = _cc.isGrounded;
            float targetSpeed = grounded ? speed : (speed >= _sprintSpeed ? _sprintAirSpeed : _airSpeed);
            // Edge governor (see MoveToward): don't sprint blindly at cliff lips.
            if (grounded && targetSpeed > _walkSpeed && _intentionalJumpTimer <= 0f && !_onLadder
                && IsEdgeAhead(dir, 2.2f))
                targetSpeed = _walkSpeed;
            if (_intentionalJumpTimer > 0f && !grounded) _currentHorizInput = 1f;
            else _currentHorizInput = Mathf.Lerp(_currentHorizInput, 1f, _acceleration * Time.deltaTime);

            Vector3 move = dir * targetSpeed * _currentHorizInput;
            move.y = _verticalVelocity;

            // FINAL SAFETY: void check before move. Lookahead scales with speed like
            // MoveToward's — the old fixed 0.8m gave a sprinting bot ~0.07s of margin,
            // which is how DirectTacticalRoute bots still ran off map edges.
            if (grounded && _intentionalJumpTimer <= 0f && !_onLadder)
            {
                Vector3 hm = new Vector3(move.x, 0, move.z);
                if (hm.sqrMagnitude > 0.01f)
                {
                    float voidLookahead = Mathf.Clamp(hm.magnitude * 0.16f, 0.8f, 2.0f);
                    if (!HasGroundFootprintAhead(hm, voidLookahead) && !IsImpulseZoneAhead(hm, voidLookahead))
                    {
                        if (TryGetSafeEdgeEscapeDir(hm, out Vector3 escapeDir))
                        {
                            move.x = escapeDir.x * _walkSpeed * 0.75f;
                            move.z = escapeDir.z * _walkSpeed * 0.75f;
                            _commitDir = escapeDir;
                            _commitTimer = Mathf.Max(_commitTimer, 0.35f);
                        }
                        else
                        {
                            move.x = 0f;
                            move.z = 0f;
                        }
                        _slideForceFactor = 0f;
                    }
                }
            }

            // Oscillation detector: the id-based ping-pong check can't see navmesh routes
            // (fresh synthetic ids every repath), but the bouncing is plainly visible in
            // game. Several near-reversals of steering direction inside a couple of
            // seconds = bouncing — break out via the existing nodeless machinery and log
            // it so the log finally matches what the player sees.
            if (grounded && _intentionalJumpTimer <= 0f && !_onLadder && !_isSliding)
            {
                if (Time.time - _lastDirFlipTime > 2.5f) _dirFlipCount = 0;
                if (_lastMoveDir.sqrMagnitude > 0.01f && dir.sqrMagnitude > 0.01f
                    && Vector3.Dot(_lastMoveDir, dir) < -0.4f)
                {
                    _dirFlipCount++;
                    _lastDirFlipTime = Time.time;
                    if (_dirFlipCount >= 4)
                    {
                        _dirFlipCount = 0;
                        _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                        _lastBounceTime = Time.time;
                        _graphPath.Clear();
                        _graphPathIndex = 0;
                        _repathTimer = 0f;
                        _nodelessLockTimer = Mathf.Min(7f, 1.75f + 1.25f * _nodelessBounceCount);
                        SwitchPathSource(PathSource.DirectTacticalRoute);
                        Plugin.Log.LogInfo($"[{BotName}] Direction oscillation — breaking out (bounce #{_nodelessBounceCount})");
                        MoveTowardNodeless(target, speed);
                        return;
                    }
                }
            }

            _lastMoveDir = dir;
            DoMove(move * Time.deltaTime);
        }

        /// <summary>Find nearest ladder collider within radius.</summary>
        private static Collider[] _ladderBuffer = new Collider[128];

        private float _ladderSearchTimer;
        private static Collider _cachedLadder;

        private static bool _mapHasLadders = true;  // Assume true until first scan proves otherwise
        private static bool _mapLadderScanned;

        private static Collider[] _mapLadderColliders = System.Array.Empty<Collider>();

        /// <summary>One full-scene collider sweep per map. The old per-call sweep ran
        /// EVERY second for every bot standing far from a ladder ([Perf] showed 12-20
        /// ms/frame windows and a multi-second worst hitch from exactly this).</summary>
        private static void EnsureLadderCache()
        {
            if (_mapLadderScanned) return;
            _mapLadderScanned = true;
            var found = new System.Collections.Generic.List<Collider>();
            foreach (var col in Object.FindObjectsOfType<Collider>())
            {
                if (col == null) continue;
                bool isLadder = col.gameObject.layer == 10; // Ladder layer
                if (!isLadder)
                {
                    string tag = "";
                    try { tag = col.tag; } catch { }
                    isLadder = tag == "Ladder/Metal" || tag == "Ladder/Chain"
                        || col.gameObject.name.IndexOf("ladder", System.StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (isLadder) found.Add(col);
            }
            _mapLadderColliders = found.ToArray();
            _mapHasLadders = _mapLadderColliders.Length > 0;
            Plugin.Log.LogInfo($"[BOT] Ladder cache: {_mapLadderColliders.Length} collider(s) on this map");
        }

        private Collider FindNearbyLadder(float radius)
        {
            EnsureLadderCache();
            if (!_mapHasLadders) return null;

            // Rate limit — don't search every frame
            _ladderSearchTimer -= Time.deltaTime;
            if (_ladderSearchTimer > 0f && _cachedLadder != null)
            {
                float cachedDist = Vector3.Distance(transform.position, _cachedLadder.ClosestPoint(transform.position));
                if (cachedDist < radius) return _cachedLadder;
            }
            _ladderSearchTimer = 1f;

            Collider best = null;
            float bestDist = float.MaxValue;

            // Method 1: search by ladder layer
            if (_ladderLayer.value != 0)
            {
                int c = Physics.OverlapSphereNonAlloc(transform.position, radius,
                    _ladderBuffer, _ladderLayer);
                for (int i = 0; i < c; i++)
                {
                    if (_ladderBuffer[i] == null) continue;
                    float d = Vector3.Distance(transform.position, _ladderBuffer[i].ClosestPoint(transform.position));
                    if (d < bestDist) { bestDist = d; best = _ladderBuffer[i]; }
                }
            }

            // Method 2: nearest from the per-map cache (tag/name ladders off the layer)
            if (best == null)
            {
                for (int i = 0; i < _mapLadderColliders.Length; i++)
                {
                    var col = _mapLadderColliders[i];
                    if (col == null) continue;
                    float d = Vector3.Distance(transform.position, col.ClosestPoint(transform.position));
                    if (d < radius && d < bestDist) { bestDist = d; best = col; }
                }
            }

            if (best != null && best != _cachedLadder)
            {
                _cachedLadder = best;
                Plugin.Log.LogInfo($"[{BotName}] Found ladder: {best.gameObject.name} tag={best.tag} layer={best.gameObject.layer} dist={bestDist:F1}");
            }
            else if (best != null)
            {
                _cachedLadder = best;
            }

            return best;
        }

        /// <summary>Reset ladder cache on scene change.</summary>
        public static void ResetLadderCache()
        {
            _mapHasLadders = true;
            _mapLadderScanned = false;
            _cachedLadder = null;
            _mapLadderColliders = System.Array.Empty<Collider>();
        }

        private Vector3 TryAngledDirections(Vector3 dir, int wallMask)
        {
            float[] angles = { 30, -30, 60, -60, 90, -90 };
            foreach (float angle in angles)
            {
                Vector3 test = Quaternion.Euler(0, angle, 0) * dir;
                if (!Physics.Raycast(transform.position + Vector3.up * 0.5f, test, 1.5f, wallMask, QueryTriggerInteraction.Ignore))
                {
                    // Commit to this direction for 1.5s — prevents oscillation
                    _commitDir = test;
                    _commitTimer = 1.5f;
                    return test;
                }
            }
            // All blocked — commit to reverse for 2s
            _commitDir = -dir;
            _commitTimer = 2f;
            return -dir;
        }

        // ---- Door interaction ----
        private const int DOOR_LAYER_MASK = 1 << 19; // Layer 19 = environment interaction
        private float _doorCheckTimer;

        private void TryOpenDoor(Vector3 moveDir)
        {
            _doorCheckTimer -= Time.deltaTime;
            if (_doorCheckTimer > 0f) return;
            _doorCheckTimer = 0.3f; // Check 3x per second

            // Raycast at waist height in movement direction — layer 19 = doors, dispensers, interactables
            if (Physics.Raycast(transform.position + Vector3.up * 0.8f, moveDir, out RaycastHit hit,
                2f, DOOR_LAYER_MASK, QueryTriggerInteraction.Ignore))
            {
                // Door — open if closed
                var door = hit.collider.GetComponent<Door>();
                if (door == null) door = hit.collider.GetComponentInParent<Door>();
                if (door != null && !door.isOpen)
                {
                    door.OnInteract(transform);
                    return;
                }

                // Slot machine / item dispenser — use if no weapon held
                if (_heldWeapon == null)
                {
                    var dispenser = hit.collider.GetComponent<ItemDispenser>();
                    if (dispenser == null) dispenser = hit.collider.GetComponentInParent<ItemDispenser>();
                    if (dispenser != null)
                    {
                        dispenser.OnInteract(transform);
                    }
                }
            }
        }

        /// <summary>
        /// Lightweight edge detection: raycast down at check position.
        /// Returns true if there's no ground ahead (edge/void).
        /// </summary>
        // Per-frame memo for IsEdgeAhead — steering, the speed governor, gap detection
        // and the emergency stop all probe the same directions every frame. With 8+
        // bots this was one of the biggest recurring raycast costs.
        private int _edgeCacheFrame = -1;
        private int _edgeCacheCount;
        private readonly Vector4[] _edgeCacheEntries = new Vector4[8]; // dir.xz, dist, result(0/1)

        private bool IsEdgeAhead(Vector3 dir, float checkDist)
        {
            if (_onLadder || _nearLadder || _ladderDismountTimer > 0f) return false;

            int frame = Time.frameCount;
            if (frame != _edgeCacheFrame)
            {
                _edgeCacheFrame = frame;
                _edgeCacheCount = 0;
            }
            for (int i = 0; i < _edgeCacheCount; i++)
            {
                Vector4 e = _edgeCacheEntries[i];
                if (Mathf.Abs(e.z - checkDist) < 0.05f
                    && e.x * dir.x + e.y * dir.z > 0.995f * new Vector2(dir.x, dir.z).magnitude)
                    return e.w > 0.5f;
            }

            Vector3 checkPos = transform.position + dir * checkDist;
            // Cast from 2.5m above bot — on ramps the ground ahead is HIGHER than the bot,
            // so a low origin (0.2m) misses the ramp surface entirely → false "edge" detection.
            // 2.5m covers slopes up to ~60° at 1.5m check distance.
            bool result = !Physics.Raycast(checkPos + Vector3.up * 2.5f, Vector3.down, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore);

            Vector2 flat = new Vector2(dir.x, dir.z);
            if (_edgeCacheCount < _edgeCacheEntries.Length && flat.sqrMagnitude > 0.001f)
            {
                flat.Normalize();
                _edgeCacheEntries[_edgeCacheCount++] = new Vector4(flat.x, flat.y, checkDist, result ? 1f : 0f);
            }
            return result;
        }

        private bool HasGroundFootprintAhead(Vector3 dir, float checkDist = 0.9f)
        {
            if (dir.sqrMagnitude < 0.001f) return true;
            dir.y = 0f;
            dir.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, dir).normalized * 0.35f;
            Vector3 center = transform.position + dir * checkDist;
            return Physics.Raycast(center + Vector3.up * 2.5f, Vector3.down, 5.5f, GROUND_MASK, QueryTriggerInteraction.Ignore)
                && Physics.Raycast(center + side + Vector3.up * 2.5f, Vector3.down, 5.5f, GROUND_MASK, QueryTriggerInteraction.Ignore)
                && Physics.Raycast(center - side + Vector3.up * 2.5f, Vector3.down, 5.5f, GROUND_MASK, QueryTriggerInteraction.Ignore);
        }

        private bool IsPlayerProvenJumpEdge(NavEdge edge)
        {
            if (edge == null || NavGraph.Instance == null) return false;
            if (edge.Type != EdgeType.Jump && edge.Type != EdgeType.WallJump) return false;
            var from = NavGraph.Instance.GetNodeById(edge.From);
            var to = NavGraph.Instance.GetNodeById(edge.To);
            return from != null && to != null && from.PlayerSourced && to.PlayerSourced;
        }

        private bool TryGetSafeEdgeEscapeDir(Vector3 unsafeDir, out Vector3 escapeDir)
        {
            escapeDir = Vector3.zero;
            unsafeDir.y = 0f;
            if (unsafeDir.sqrMagnitude < 0.001f) unsafeDir = transform.forward;
            unsafeDir.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, unsafeDir).normalized;
            Vector3 toLastGround = _lastGroundedPos - transform.position;
            toLastGround.y = 0f;

            Vector3[] candidates =
            {
                -unsafeDir,
                right,
                -right,
                (-unsafeDir + right).normalized,
                (-unsafeDir - right).normalized,
                toLastGround.sqrMagnitude > 0.25f ? toLastGround.normalized : -unsafeDir
            };

            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 d = candidates[i];
                if (d.sqrMagnitude < 0.001f) continue;
                d.y = 0f;
                d.Normalize();
                if (!HasGroundFootprintAhead(d, 0.65f)) continue;
                if (Physics.Raycast(transform.position + Vector3.up * 0.9f, d, 0.7f, WALL_MASK, QueryTriggerInteraction.Ignore))
                    continue;

                float score = Vector3.Dot(d, -unsafeDir);
                if (toLastGround.sqrMagnitude > 0.25f)
                    score += Vector3.Dot(d, toLastGround.normalized) * 0.5f;
                if (score > bestScore)
                {
                    bestScore = score;
                    escapeDir = d;
                }
            }

            return escapeDir.sqrMagnitude > 0.001f;
        }

        /// <summary>
        /// Check for edges on both sides perpendicular to movement direction.
        /// Only corrects if the bot is very close to a drop (0.6m).
        /// </summary>
        private Vector3 AvoidSideEdges(Vector3 moveDir)
        {
            if (_onLadder || _nearLadder || _ladderDismountTimer > 0f) return moveDir;
            if (_intentionalJumpTimer > 0f || _zoneForceDuration > 0f) return moveDir;
            if (_graphPath.Count > 0 && _graphPathIndex < _graphPath.Count) return moveDir; // Following a path — trust it

            Vector3 right = new Vector3(moveDir.z, 0, -moveDir.x);
            bool leftEdge = IsEdgeAhead(right, 0.6f);
            bool rightEdge = IsEdgeAhead(-right, 0.6f);

            if (leftEdge && !rightEdge)
                moveDir = (moveDir - right * 0.3f).normalized;
            else if (rightEdge && !leftEdge)
                moveDir = (moveDir + right * 0.3f).normalized;

            return moveDir;
        }

        // ===================== SLIDE HELPERS =====================
        // CC dimensions — match FPC exactly
        private const float STAND_HEIGHT = 2f;
        private const float STAND_CENTER_Y = 1f;
        private const float SLIDE_HEIGHT = 0.8f;
        private const float SLIDE_CENTER_Y = 0.4f;
        private const float CROUCH_HEIGHT = 1.25f;   // game Player.prefab override (script default 1f is wrong)
        private const float SKIN_STANDING = 0.2f;    // game CC default skin width
        private const float SKIN_CROUCHED = 0.07f;   // game's crouch skin width (FPC.Update)

        private Transform _graphicsTf;

        /// <summary>
        /// Mirrors the game's stance handling exactly: CC bottom pinned at the feet
        /// (center = height/2, FPC.AdjustHeight), skin width 0.2 standing / 0.07
        /// crouched, and the body model offset down by exactly -skinWidth
        /// (PlayerSetup.ChangeSkinWidthObservers). The old fixed skinWidth of 0.08
        /// combined with the prefab's inherited -0.2 model offset rendered bot feet
        /// 0.12m inside the floor — worse when crouching.
        /// </summary>
        private void ApplyStance(float height, float skin)
        {
            if (_cc != null)
            {
                _cc.height = height;
                _cc.center = new Vector3(0, height * 0.5f, 0);
                _cc.skinWidth = skin;
            }
            if (_graphicsTf == null)
            {
                try
                {
                    var ps = GetComponent<PlayerSetup>();
                    var f = typeof(PlayerSetup).GetField("graphics",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                    object g = ps != null && f != null ? f.GetValue(ps) : null;
                    if (g is GameObject go) _graphicsTf = go.transform;
                    else if (g is Transform tf) _graphicsTf = tf;
                }
                catch { }
            }
            if (_graphicsTf != null)
                _graphicsTf.localPosition = new Vector3(0, -skin, 0);
        }

        /// <summary>
        /// Single entry point for ALL slide initiation. Handles CC resize, animator,
        /// force application, and state setup. Prevents inconsistent slide starts.
        /// </summary>
        /// <param name="direction">Locked movement direction during slide</param>
        /// <param name="duration">Slide duration in seconds</param>
        /// <param name="force">Slide impulse strength (default 2f matches FPC)</param>
        /// <param name="setResetTimer">If true, sets slideResetTimer (sprint slides only)</param>
        private void InitSlide(Vector3 direction, float duration = 1.5f, float force = 2f, bool setResetTimer = false)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;
            direction.Normalize();

            _isSliding = true;
            _slideTimer = duration;
            _isCrouching = true;
            _slideLockedDir = direction;
            _slideForce = direction * force;
            _slideForceFactor = force;
            _slideStartTime = Time.time;

            ApplyStance(SLIDE_HEIGHT, SKIN_STANDING); // game keeps 0.2 skin while sliding
            if (_bodyAnimator != null) TrySet(_bodyAnimator, "Slide", true);
            if (_globalAnimator != null) TrySet(_globalAnimator, "Slide", true);

            if (setResetTimer)
                _slideResetTimer = 1.5f; // Exact FPC slideResetTime
        }

        /// <summary>
        /// Single exit point for ALL slide termination. Restores CC, clears force, syncs animator.
        /// </summary>
        private void EndSlide()
        {
            _isSliding = false;
            _slideTimer = 0f;
            _isCrouching = false;
            _slideForceFactor = 0f;
            _slideForce = Vector3.zero;
            _slideLockedDir = Vector3.zero;

            // Push up to avoid sinking into floor when restoring full height
            if (_cc != null)
            {
                float heightDiff = STAND_HEIGHT - _cc.height;
                if (heightDiff > 0.1f && _cc.enabled)
                    _cc.Move(Vector3.up * heightDiff * 0.5f);
                ApplyStance(STAND_HEIGHT, SKIN_STANDING);
            }
            if (_bodyAnimator != null) { TrySet(_bodyAnimator, "Slide", false); TrySet(_bodyAnimator, "Crouch", false); }
            if (_globalAnimator != null) { TrySet(_globalAnimator, "Slide", false); TrySet(_globalAnimator, "Crouch", false); }
        }

        /// <summary>
        /// Sprint slide — the voluntary "fun" slide. Respects cooldown.
        /// </summary>
        private void StartSprintSlide(float duration)
        {
            if (_isSliding || _slideResetTimer > 0f) return;
            InitSlide(transform.forward, duration * 2f, force: 2f, setResetTimer: true);
        }

        // ===================== OBSTRUCTION CHECK =====================
        /// <summary>
        /// Consistent wall/obstacle detection at 3 standardized heights.
        /// All movement code should use this instead of ad-hoc raycasts.
        /// Heights match FPC: feet=0.3, waist=1.0, head=1.5, crouch=0.4
        /// </summary>
        private struct ObstructionResult
        {
            public bool FeetBlocked;   // 0.3m — below step offset
            public bool WaistBlocked;  // 1.0m — body center
            public bool HeadBlocked;   // 1.5m — head height
            public bool CrouchClear;   // 0.4m — passable while crouching
        }

        /// <summary>The chosen route wants this descent: the current or next waypoint is
        /// a REAL node meaningfully below the bot, roughly in the step direction. The
        /// void-lip FINAL SAFETY refusing these made bots ping-pong at every lip their
        /// own path crossed (block -> clear path -> repath to the same route -> block).
        /// Waypoints are actual ground positions, so stepping off toward one is not a
        /// void fall — the game has no fall damage.</summary>
        private bool RouteAuthorizesDrop(Vector3 horizMove)
        {
            if (_graphPath == null || _graphPathIndex >= _graphPath.Count) return false;
            Vector3 myPos = transform.position;
            Vector3 dir = new Vector3(horizMove.x, 0f, horizMove.z);
            if (dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            int last = Mathf.Min(_graphPathIndex + 1, _graphPath.Count - 1);
            for (int i = _graphPathIndex; i <= last; i++)
            {
                var node = _graphPath[i];
                if (node == null) continue;
                Vector3 to = node.Position - myPos;
                float drop = -to.y;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (drop > 0.8f && drop < 25f
                    && flat.magnitude < 14f
                    && (flat.sqrMagnitude < 0.25f || Vector3.Dot(flat.normalized, dir) > 0.5f))
                {
                    // Authorize ONLY if stepping off here actually lands on something.
                    // A below-waypoint across a chasm means the route wants a JUMP —
                    // if the jump logic didn't fire, walking off is a void death
                    // (this exact regression killed bots on jump-y paths).
                    Vector3 stepProbe = myPos + dir * 1.1f + Vector3.up * 0.4f;
                    if (Physics.Raycast(stepProbe, Vector3.down, drop + 5f,
                            GROUND_MASK, QueryTriggerInteraction.Ignore))
                        return true;
                }
            }
            return false;
        }

        private float _lowCeilingConfirmTimer;

        /// <summary>True only for a REAL low ceiling just ahead: the head ray blocked
        /// close-in (0.9m, not the default 1.5m probe), a second ray near the actual
        /// head top ALSO blocked (a chest-height railing/bar/prop only blocks one),
        /// and the condition held for ~0.2s (kills one-frame flickers). Bots were
        /// crouch-walking through normal play off railings probed 1.5m out.</summary>
        private bool ConfirmLowCeiling(Vector3 dir)
        {
            var obs = CheckObstructions(dir, 0.9f);
            bool candidate = obs.CrouchClear && obs.HeadBlocked && !obs.WaistBlocked
                && Physics.Raycast(transform.position + Vector3.up * 1.85f, dir, 0.9f,
                    WALL_MASK, QueryTriggerInteraction.Ignore);
            if (!candidate) { _lowCeilingConfirmTimer = 0f; return false; }
            if (_isCrouching) return true; // already committed — keep holding through the passage
            _lowCeilingConfirmTimer += Time.deltaTime;
            return _lowCeilingConfirmTimer >= 0.2f;
        }

        private ObstructionResult CheckObstructions(Vector3 dir, float dist = 1.5f)
        {
            var r = new ObstructionResult();
            r.FeetBlocked = Physics.Raycast(transform.position + Vector3.up * 0.3f, dir, dist,
                WALL_MASK, QueryTriggerInteraction.Ignore);
            r.WaistBlocked = Physics.Raycast(transform.position + Vector3.up * 1f, dir, dist,
                WALL_MASK, QueryTriggerInteraction.Ignore);
            r.HeadBlocked = Physics.Raycast(transform.position + Vector3.up * 1.5f, dir, dist,
                WALL_MASK, QueryTriggerInteraction.Ignore);
            r.CrouchClear = !Physics.Raycast(transform.position + Vector3.up * 0.4f, dir, dist,
                WALL_MASK, QueryTriggerInteraction.Ignore);
            return r;
        }


        // ===================== SHARED REACTIVE STEERING =====================
        /// <summary>
        /// Shared reactive steering used by both MoveToward and MoveTowardNodeless.
        /// Handles: obstacle jump, proactive slide, edge detection, wall redirect,
        /// collision deflection, explore jump, emergency edge stop.
        /// </summary>
        private void ReactiveSteer(ref Vector3 dir, ref bool jumped, Vector3 target, int wallMask, bool relaxedNoGraph = false)
        {
            bool zoneLaunched = _zoneForceDuration > 0f;
            bool commitActive = _commitTimer > 0f && _commitDir.sqrMagnitude > 0.01f;

            // Obstacle jump: feet blocked, clear above, safe landing
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder && !_nearLadder
                && _intentionalJumpTimer <= 0f)
            {
                var obs = CheckObstructions(dir, 1.2f);
                if (obs.FeetBlocked)
                {
                    if (!obs.WaistBlocked || !obs.HeadBlocked)
                    {
                        // Check for safe landing
                        bool safeLanding = false;
                        int gMask = GROUND_MASK;
                        Vector3 closeCheck = transform.position + dir * 0.8f + Vector3.up * 2.5f;
                        if (Physics.Raycast(closeCheck, Vector3.down, out RaycastHit closeHit, 3f, gMask))
                            if (closeHit.point.y > transform.position.y + 0.3f)
                                safeLanding = true;
                        if (!safeLanding)
                        {
                            Vector3 farCheck = transform.position + dir * 2f + Vector3.up * 2f;
                            if (Physics.Raycast(farCheck, Vector3.down, out RaycastHit farHit, 6f, gMask))
                                if (new Vector3(farHit.point.x - transform.position.x, 0, farHit.point.z - transform.position.z).magnitude > 0.5f)
                                    safeLanding = true;
                        }
                        if (safeLanding)
                            jumped = TryJump(JumpReason.Obstacle, dir);
                        else
                            dir = TryAngledDirections(dir, wallMask);
                    }
                    else
                    {
                        dir = TryAngledDirections(dir, wallMask);
                    }
                }
                else
                {
                    // Feet ray clear — check for KNEE-HIGH blockers underneath it.
                    jumped = TryKneeHop(dir, wallMask);
                }
            }

            // Proactive crouch/slide under obstacles:
            //  * Face-height opening (head blocked, waist clear): CROUCH-WALK immediately.
            //    A slide here was wrong — it's momentary, locks direction, and the bot
            //    stood back up into the obstacle over and over.
            //  * True crawl gap (waist blocked too): slide is the only shape that fits.
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder
                && _intentionalJumpTimer <= 0f)
            {
                var obs = CheckObstructions(dir);
                if (ConfirmLowCeiling(dir))
                {
                    if (!_isCrouching) StartCrouch(0.9f);
                    else _crouchTimer = Mathf.Max(_crouchTimer, 0.4f); // keep it held while still blocked
                    _stuckTimer = 0f;
                }
                else if (obs.CrouchClear && obs.WaistBlocked && _stuckTimer > 0.15f)
                {
                    InitSlide(dir);
                    _stuckTimer = 0f;
                }
            }

            // Edge detection: check for gaps ahead, jump if target is across
            if (!relaxedNoGraph && !zoneLaunched && !commitActive && !jumped && _cc.isGrounded && _intentionalJumpTimer <= 0f && !_onLadder)
            {
                if (IsEdgeAhead(dir, 1f))
                {
                    if (NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play)
                    {
                        dir = TryAngledDirections(dir, wallMask);
                        return;
                    }

                    Vector3 toTarget = target - transform.position;
                    float hDist = new Vector3(toTarget.x, 0, toTarget.z).magnitude;
                    if (hDist > 1f && hDist < Plugin.GetMaxJumpDist())
                    {
                        // Far-side landing probe: a gap is only jumpable if ground exists
                        // near where the jump comes down. The old check only confirmed the
                        // MIDDLE was empty, so bots gap-jumped map rims into the void — and
                        // the >5m sprint-approach below suppresses the lip safety while
                        // doing it. Falling short of a real landing stays possible (that's
                        // how training discovers jump edges); pure void dives don't.
                        Vector3 far = transform.position + dir * hDist;
                        bool landingExists =
                            (Physics.Raycast(far + Vector3.up * 1.5f, Vector3.down, out RaycastHit landHit, 28f,
                                GROUND_MASK, QueryTriggerInteraction.Ignore)
                             && landHit.point.y > transform.position.y - 25f)
                            || (Physics.Raycast(target + Vector3.up * 1.5f, Vector3.down, out RaycastHit tgtHit, 28f,
                                GROUND_MASK, QueryTriggerInteraction.Ignore)
                             && tgtHit.point.y > transform.position.y - 25f);

                        Vector3 mid = transform.position + dir * (hDist * 0.5f) + Vector3.up * 0.5f;
                        if (!landingExists)
                        {
                            dir = TryAngledDirections(dir, wallMask);
                        }
                        else if (!Physics.Raycast(mid, Vector3.down, 3f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                        {
                            // Long jumps (>5m): build sprint speed before jumping
                            if (hDist > 5f && _currentHorizInput < 0.8f)
                            {
                                _currentHorizInput = Mathf.MoveTowards(_currentHorizInput, 1f, 5f * Time.deltaTime);
                                _intentionalJumpTimer = 0.5f; // Suppress void safety during approach
                                _jumpDir = dir;
                            }
                            else
                            {
                                _airStrafeTarget = target;
                                _airStrafeActive = true;
                                jumped = TryJump(JumpReason.GapDetection, dir, intentionalTime: 1.5f);
                            }
                        }
                        else
                            dir = TryAngledDirections(dir, wallMask);
                    }
                    else if (hDist <= 1f)
                    {
                        // Target is very close but edge ahead — jump toward it (ladder across gap)
                        _airStrafeTarget = target;
                        _airStrafeActive = true;
                        jumped = TryJump(JumpReason.EdgeAhead, dir, intentionalTime: 1.0f);
                    }
                    else
                    {
                        dir = TryAngledDirections(dir, wallMask);
                    }
                }
            }

            // Proactive wall redirect
            if (!zoneLaunched && !commitActive && !jumped && !_onLadder && !_nearLadder
                && _intentionalJumpTimer <= 0f)
            {
                bool headBlocked = false, bodyBlocked = false;
                if (Physics.Raycast(transform.position + Vector3.up * 1.5f, dir, out RaycastHit headHit, 0.5f, wallMask, QueryTriggerInteraction.Ignore))
                    headBlocked = Vector3.Angle(headHit.normal, Vector3.up) > 65f;
                if (Physics.Raycast(transform.position + Vector3.up * 0.8f, dir, out RaycastHit bodyHit, 0.5f, wallMask, QueryTriggerInteraction.Ignore))
                    bodyBlocked = Vector3.Angle(bodyHit.normal, Vector3.up) > 65f;
                if (headBlocked && bodyBlocked)
                    dir = TryAngledDirections(dir, wallMask);
            }

            // Collision wall slide
            if (!zoneLaunched && !jumped && !_onLadder && !_nearLadder && _intentionalJumpTimer <= 0f)
            {
                _collisionTimer -= Time.deltaTime;
                if (_collisionTimer > 0f && _lastCollisionNormal.sqrMagnitude > 0.01f)
                {
                    Vector3 colNormal = _lastCollisionNormal; colNormal.y = 0; colNormal.Normalize();
                    float dot = Vector3.Dot(dir, -colNormal);
                    if (dot > 0.3f)
                    {
                        Vector3 slideDir = dir - dot * -colNormal;
                        if (slideDir.sqrMagnitude > 0.01f)
                            dir = slideDir.normalized;
                    }
                }
            }

            // Explore jump when stuck against wall
            if (!zoneLaunched && !jumped && _cc.isGrounded && !_onLadder && _stuckTimer > 1.5f)
            {
                var obs = CheckObstructions(dir, 1f);
                if (obs.FeetBlocked && !obs.WaistBlocked)
                {
                    if (TryJump(JumpReason.ExploreStuck, dir))
                    {
                        jumped = true;
                        _stuckTimer = 0f;
                    }
                }
            }

            // Emergency edge stop
            if (!commitActive && _cc.isGrounded && !jumped && _intentionalJumpTimer <= 0f)
            {
                if (IsEdgeAhead(dir, 0.5f))
                {
                    if (relaxedNoGraph)
                    {
                        // On sparse/no-graph maps, hard reverse creates left-right ping-pong.
                        // Side-step and commit briefly instead to keep forward pressure.
                        Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                        if ((BotId & 1) == 0) side = -side;
                        _commitDir = side;
                        _commitTimer = 0.8f;
                        dir = side;
                    }
                    else
                    {
                        dir = -dir;
                    }
                }
            }
        }

        // AvoidWalls removed — wall avoidance now handled by CC collision feedback in MoveToward

        private float _wanderChangeTimer;

        private bool _nearLadder; // Ladder within 2m — suppresses jump/wall slide/edge detection

        private Vector3 GetLadderObjective()
        {
            if (_graphPath != null && _graphPath.Count > 0)
            {
                int idx = Mathf.Clamp(_graphPathIndex, 0, _graphPath.Count - 1);
                return _graphPath[idx].Position;
            }
            if (_weaponTarget != null) return _weaponTarget.position;
            if (_playerTarget != null) return _playerTarget.position;
            if (_hasWanderTarget) return _wanderTarget;
            return transform.position + Vector3.up * 3f;
        }

        private Vector3 PickLadderExitDir(Vector3 objective)
        {
            Vector3 toObjective = objective - transform.position;
            toObjective.y = 0f;
            if (toObjective.sqrMagnitude > 0.25f && IsLadderExitClear(toObjective.normalized))
                return toObjective.normalized;

            if (_lastMoveDir.sqrMagnitude > 0.25f && IsLadderExitClear(_lastMoveDir.normalized))
                return _lastMoveDir.normalized;

            if (_ladderFaceDir.sqrMagnitude > 0.25f)
            {
                Vector3 away = -_ladderFaceDir.normalized;
                if (IsLadderExitClear(away)) return away;
                if (IsLadderExitClear(_ladderFaceDir.normalized)) return _ladderFaceDir.normalized;
            }

            return transform.forward;
        }

        private bool IsLadderExitClear(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.y = 0f;
            dir.Normalize();
            Vector3 chest = transform.position + Vector3.up * 1.2f;
            return !Physics.SphereCast(chest, 0.3f, dir, out _, 1.15f, WALL_MASK, QueryTriggerInteraction.Ignore);
        }

        private void HandleLadder()
        {
            // WATCHDOG: if we claim to be on a ladder but haven't actually touched one in >1.2s,
            // force-clear the state. Without this, a stuck _onLadder=true causes ApplyGravity to
            // early-return and _verticalVelocity stays at _ladderSpeed — bot flies into the sky.
            if (_onLadder && Time.time - _lastLadderTouchTime > 1.2f)
            {
                _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                _onLadder = false;
                _ladderStuckTimer = 0f;
                _ladderClimbTimer = 0f;
                _ladderFaceDirPinned = false;
                _verticalVelocity = Mathf.Min(_verticalVelocity, 0f);
                Plugin.Log.LogInfo($"[{BotName}] Ladder watchdog — no ladder touched >1.2s, force-cleared stuck state");
            }

            // WATCHDOG: mid-ladder freeze. If we're on a ladder but haven't actually climbed
            // more than 0.2m in 1.2s, force-dismount with a push. Catches cases where the
            // bot is geometrically on a ladder but vertical velocity isn't translating to
            // movement (CC stuck against geometry, invisible collider, etc.).
            if (_onLadder)
            {
                if (Time.time - _ladderYSampleTime > 1.2f)
                {
                    float deltaY = transform.position.y - _ladderLastYSample;
                    if (deltaY < 0.05f)
                    {
                        _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                        _onLadder = false;
                        _ladderDismountTimer = 0.35f;
                        _ladderStuckTimer = 0f;
                        _ladderClimbTimer = 0f;
                        _ladderFaceDirPinned = false;
                        _verticalVelocity = _jumpForce * 0.5f; // bump outward
                        if (NavGraph.Instance != null)
                        {
                            var badNode = NavGraph.Instance.FindNearestNode(transform.position, 2f);
                            if (badNode != null)
                                NavGraph.Instance.ReportBadNode(badNode.Id, "ladder climb made no progress", 1, silent: true);
                        }
                        Plugin.Log.LogInfo($"[{BotName}] Ladder freeze watchdog — no Y progress, nudging off");
                    }
                    _ladderLastYSample = transform.position.y;
                    _ladderYSampleTime = Time.time;
                }
            }
            else
            {
                // Reset sampler so the next climb starts clean.
                _ladderLastYSample = transform.position.y;
                _ladderYSampleTime = Time.time;
            }

            // RE-PATH WATCHDOG: every ~2 sec on a ladder, re-confirm path validity.
            // If the graph path is empty or invalid, drop off instead of freezing.
            if (_onLadder)
            {
                _ladderRepathTimer -= Time.deltaTime;
                if (_ladderRepathTimer <= 0f)
                {
                    _ladderRepathTimer = 2f;
                    bool noPath = _graphPath == null || _graphPath.Count == 0
                        || _graphPathIndex >= _graphPath.Count;
                    // Only dismount on no-path when the bot actually has somewhere to be.
                    // Ambient wandering without a target is fine — ladder is progress on its own.
                    bool hasGoal = _weaponTarget != null || _playerTarget != null || _hasWanderTarget;
                    if (noPath && hasGoal)
                    {
                        _onLadder = false;
                        _ladderDismountTimer = 0.4f;
                        _ladderFaceDirPinned = false;
                        _verticalVelocity = -1f;
                        Plugin.Log.LogInfo($"[{BotName}] Ladder re-path watchdog — no valid continuation, dismounting");
                    }
                }
            }
            else
            {
                _ladderRepathTimer = 2f; // reset window on dismount
            }

            if (_cc == null || !_cc.enabled)
            {
                // CC disabled: also clear ladder state so we don't fly when it re-enables
                _onLadder = false;
                return;
            }

            // Load ladder layer once — try own FPC first, then any scene FPC
            if (!_ladderLayerLoaded)
            {
                _ladderLayerLoaded = true;
                try
                {
                    var field = typeof(FirstPersonController).GetField("ladderLayer",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (field != null)
                    {
                        // Try own FPC
                        if (_fpc != null)
                            _ladderLayer = (LayerMask)field.GetValue(_fpc);

                        // If still 0, try any FPC in scene (real player's)
                        if (_ladderLayer.value == 0)
                        {
                            foreach (var fpc in Object.FindObjectsOfType<FirstPersonController>())
                            {
                                LayerMask layer = (LayerMask)field.GetValue(fpc);
                                if (layer.value != 0)
                                {
                                    _ladderLayer = layer;
                                    break;
                                }
                            }
                        }

                        Plugin.Log.LogInfo($"[{BotName}] Ladder layer: {_ladderLayer.value}");
                    }
                }
                catch { }
            }

            // Decrement dismount timer
            if (_ladderDismountTimer > 0f)
                _ladderDismountTimer -= Time.deltaTime;

            _wasOnLadder = _onLadder;

            // ---- "On ladder" detection: every frame, matching FPC exactly ----
            // FPC: OverlapSphere(pos + up*0.5, 0.5, ladderLayer)
            bool touching = false;
            Collider closestLadder = null;
            float closestDist = float.MaxValue;

            if (_ladderLayer.value != 0)
            {
                Vector3 probe0 = transform.position + Vector3.up * 0.55f;
                Vector3 probe1 = transform.position + Vector3.up * 1.15f;
                int colCount = Physics.OverlapSphereNonAlloc(probe0, 0.75f, _overlapBuffer, _ladderLayer);
                for (int ci = 0; ci < colCount; ci++)
                {
                    var c = _overlapBuffer[ci];
                    if (c == null) continue;
                    touching = true;
                    float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                    if (d < closestDist) { closestDist = d; closestLadder = c; }
                }
                int colCount2 = Physics.OverlapSphereNonAlloc(probe1, 0.75f, _overlapBuffer, _ladderLayer);
                for (int ci = 0; ci < colCount2; ci++)
                {
                    var c = _overlapBuffer[ci];
                    if (c == null) continue;
                    touching = true;
                    float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                    if (d < closestDist) { closestDist = d; closestLadder = c; }
                }
            }

            // Tag fallback (same radius as FPC)
            if (!touching)
            {
                int tagCount = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.8f, 0.9f, _overlapBuffer, ~0, QueryTriggerInteraction.Collide);
                for (int ci = 0; ci < tagCount; ci++)
                {
                    var c = _overlapBuffer[ci];
                    if (c.CompareTag("Ladder/Metal") || c.CompareTag("Ladder/Chain"))
                    {
                        touching = true;
                        closestLadder = c;
                        break;
                    }
                }
            }

            if (touching && closestLadder != null)
            {
                // Watchdog: record actual ladder contact
                _lastLadderTouchTime = Time.time;

                // Check we're on the FRONT of the ladder, not the back
                // Raycast toward ladder to get surface normal — bot must be facing INTO the surface
                Vector3 toLadder = closestLadder.ClosestPoint(transform.position + Vector3.up * 0.5f) - (transform.position + Vector3.up * 0.5f);
                toLadder.y = 0;
                bool frontSide = true; // Default to allowing if can't determine

                if (toLadder.sqrMagnitude > 0.01f)
                {
                    Vector3 rayDir = toLadder.normalized;
                    if (Physics.Raycast(transform.position + Vector3.up * 0.5f, rayDir, out RaycastHit ladderHit, 2f))
                    {
                        Vector3 normal = ladderHit.normal; normal.y = 0;
                        if (normal.sqrMagnitude > 0.01f)
                        {
                            // Bot's forward must face INTO the ladder (dot with normal < 0)
                            // Also check movement direction — bot might be walking toward it
                            float faceDot = Vector3.Dot(transform.forward, normal);
                            float moveDot = Vector3.Dot(rayDir, -normal);
                            frontSide = faceDot < -0.2f || moveDot > 0.5f || _onLadder;
                            if (frontSide)
                            {
                                // STABILIZE: pin the face-dir on first good read so the per-frame
                                // normal doesn't flip at corners and drag the bot off sideways.
                                if (!_ladderFaceDirPinned)
                                {
                                    _ladderPinnedFaceDir = -normal.normalized;
                                    _ladderFaceDirPinned = true;
                                }
                                _ladderFaceDir = _ladderPinnedFaceDir;
                            }
                            else
                            {
                                // Many STRAFTAT ladder colliders are effectively double-sided.
                                // Treat the contact as climbable and use the approach direction
                                // instead of dropping the bot back down.
                                frontSide = true;
                                _ladderFaceDir = rayDir;
                            }
                        }
                        else
                            _ladderFaceDir = _ladderFaceDirPinned ? _ladderPinnedFaceDir : rayDir;
                    }
                    else
                        _ladderFaceDir = _ladderFaceDirPinned ? _ladderPinnedFaceDir : rayDir;
                }

                if (frontSide)
                {
                    // Safety: if head is hitting ceiling, dismount — don't climb into the sky
                    bool ceilingBlocked = Physics.Raycast(transform.position + Vector3.up * 1.8f,
                        Vector3.up, 0.3f, WALL_MASK, QueryTriggerInteraction.Ignore);

                    // Safety: max ladder climb time (10s) — no ladder is that tall
                    _ladderClimbTimer += Time.deltaTime;
                    if (_ladderClimbTimer > 4f)
                        ceilingBlocked = true; // Force dismount — bounds sky-flight if top detection misses

                    if (ceilingBlocked)
                    {
                        // Head blocked — might be on wrong side or at top with overhang
                        // Try teleporting to front side of ladder if we can
                        if (_ladderFaceDir.sqrMagnitude > 0.01f && closestLadder != null)
                        {
                            Vector3 frontPos = closestLadder.bounds.center + _ladderFaceDir * 1.2f;
                            frontPos.y = transform.position.y;
                            // Check if front side has head clearance
                            bool frontClear = !Physics.Raycast(frontPos + Vector3.up * 1.8f,
                                Vector3.up, 0.5f, WALL_MASK, QueryTriggerInteraction.Ignore);
                            if (frontClear)
                            {
                                // Teleport to front side and continue climbing
                                if (_cc != null && _cc.enabled)
                                {
                                    _cc.enabled = false;
                                    transform.position = frontPos;
                                    _cc.enabled = true;
                                }
                                _ladderClimbTimer = 0f;
                                Plugin.Log.LogInfo($"[{BotName}] Ladder: teleported to front side");
                                // Don't dismount — try again from correct side
                            }
                            else
                            {
                                // No clearance anywhere — dismount and delete bad ladder node
                                _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                                _onLadder = false;
                                _verticalVelocity = -2f;
                                _ladderDismountTimer = 0.5f;
                                _ladderClimbTimer = 0f;

                                // Remove nearby ladder nodes at this height (wrong side)
                                if (NavGraph.Instance != null)
                                {
                                    var badNode = NavGraph.Instance.FindNearestNode(transform.position, 2f);
                                    if (badNode != null)
                                    {
                                        NavGraph.Instance.ReportBadNode(badNode.Id, "bad ladder-side waypoint", 3, silent: true);
                                        Plugin.Log.LogInfo($"[{BotName}] Removed bad ladder node {badNode.Id}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                            _onLadder = false;
                            _verticalVelocity = -2f;
                            _ladderDismountTimer = 0.5f;
                            _ladderClimbTimer = 0f;
                        }
                    }
                    else
                    {
                        // Fresh-grab intent gate: don't launch up a ladder we have no REAL reason to
                        // climb. GetLadderObjective falls back to "+3 up" when idle, so we require an
                        // actual objective source (path / weapon / target / wander) AND that it's
                        // above us. This stops bots grabbing ladders they walk past and flying skyward.
                        if (!_onLadder && !_wasOnLadder)
                        {
                            bool hasRealObjective = (_graphPath != null && _graphPath.Count > 0)
                                || _weaponTarget != null || _playerTarget != null || _hasWanderTarget;
                            Vector3 intentObj = GetLadderObjective();
                            if (!hasRealObjective || intentObj.y <= transform.position.y + 1.0f)
                            {
                                _onLadder = false;
                                return; // near a ladder but nothing above to reach — keep walking
                            }
                        }

                        _onLadder = true;
                        _verticalVelocity = _ladderSpeed;
                        _coyoteTimer = 0.15f;

                        Vector3 ladderCenter = closestLadder.bounds.center;
                        _lastLadderPos = ladderCenter;
                        _ladderTopY = closestLadder.bounds.max.y;

                        Vector3 ladderObjective = GetLadderObjective();
                        // Dismount at the top of ANY ladder collider (not just "tall" ones) so the bot
                        // never keeps climbing past the top into open sky.
                        bool nearColliderTop = transform.position.y >= closestLadder.bounds.max.y - 1.1f;
                        bool reachedPathExit = ladderObjective.y <= transform.position.y + 0.8f
                            && ladderObjective.y >= transform.position.y - 1.2f
                            && HorizontalDist(transform.position, ladderObjective) < 4f;
                        if (nearColliderTop || reachedPathExit)
                        {
                            _ladderExitDir = PickLadderExitDir(ladderObjective);
                            _onLadder = false;
                            _ladderDismountTimer = 0.85f;
                            _ladderStuckTimer = 0f;
                            _ladderClimbTimer = 0f;
                            _verticalVelocity = Mathf.Max(_verticalVelocity, _jumpForce * 0.45f);
                            _nearLadder = true;
                            return;
                        }

                        // Pull toward ladder center horizontally — prevents side-climbing
                        Vector3 toCenter = ladderCenter - transform.position;
                        toCenter.y = 0;
                        if (toCenter.sqrMagnitude > 0.15f)
                            _cc.Move(toCenter.normalized * 3f * Time.deltaTime);
                    }
                }
                else
                {
                    // On back of ladder — don't grab, treat as wall
                    _onLadder = false;
                }
            }
            else
            {
                if (_wasOnLadder && Time.time - _lastLadderTouchTime < 1.0f)
                {
                    _onLadder = true;
                    _nearLadder = true;
                    return;
                }

                _onLadder = false;
                _ladderStuckTimer = 0f;
                _ladderClimbTimer = 0f;

                // Dismount detection: was climbing, now off ladder — push AWAY from ladder
                if (_wasOnLadder && _ladderFaceDir.sqrMagnitude > 0.01f)
                {
                    _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                    _ladderDismountTimer = 0.9f; // Longer push to clear the top edge
                    _verticalVelocity = _jumpForce * 0.5f; // Stronger upward boost for top step-off
                }
                // Fresh climb next time — let the face-dir re-pin from the new contact.
                _ladderFaceDirPinned = false;
            }

            // Ladder stuck timeout — if on ladder for too long, force dismount
            if (_onLadder)
            {
                _ladderStuckTimer += Time.deltaTime;
                if (_ladderStuckTimer > 5f)
                {
                    _ladderExitDir = PickLadderExitDir(GetLadderObjective());
                    _onLadder = false;
                    _ladderDismountTimer = 0.6f;
                    _ladderStuckTimer = 0f;
                    _verticalVelocity = _jumpForce * 0.5f; // Small upward boost
                    Plugin.Log.LogInfo($"[{BotName}] Ladder stuck timeout — forced dismount");
                }
            }

            // ---- "Near ladder" check: rate-limited for wider radius ----
            _ladderNearCheckTimer -= Time.deltaTime;
            if (_ladderNearCheckTimer <= 0f)
            {
                _ladderNearCheckTimer = 0.15f;
                _nearLadder = _onLadder; // Always near if on

                if (!_nearLadder)
                {
                    Vector3 nearCenter = transform.position + Vector3.up * 0.8f + transform.forward * 0.5f;
                    if (_ladderLayer.value != 0)
                    {
                        int n = Physics.OverlapSphereNonAlloc(nearCenter, 2f, _overlapBuffer, _ladderLayer);
                        _nearLadder = n > 0;
                    }
                    if (!_nearLadder)
                    {
                        int n = Physics.OverlapSphereNonAlloc(nearCenter, 2f, _overlapBuffer, ~0, QueryTriggerInteraction.Collide);
                        for (int ci = 0; ci < n; ci++)
                        {
                            if (_overlapBuffer[ci].CompareTag("Ladder/Metal") || _overlapBuffer[ci].CompareTag("Ladder/Chain"))
                            { _nearLadder = true; break; }
                        }
                    }
                }
            }

            // Also near if dismounting
            if (_ladderDismountTimer > 0f) _nearLadder = true;

            // ---- Ladder approach: when near a ladder and target is above, walk into it ----
            // This makes bots prefer climbing over going around
            if (!_onLadder && _nearLadder && !_isSliding && _ladderDismountTimer <= 0f)
            {
                // Check if our target/wander point is above us
                Vector3 currentTarget = _hasWanderTarget ? _wanderTarget :
                    (_playerTarget != null ? _playerTarget.position :
                    (_weaponTarget != null ? _weaponTarget.position : Vector3.zero));

                bool targetAbove = currentTarget.y > transform.position.y + 1.5f;

                if (targetAbove)
                {
                    // Find the nearest ladder collider and walk toward it
                    Collider nearestLadder = null;
                    float nearestDist = 3f;

                    if (_ladderLayer.value != 0)
                    {
                        int n = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 3f, _overlapBuffer, _ladderLayer);
                        for (int ci = 0; ci < n; ci++)
                        {
                            var c = _overlapBuffer[ci];
                            float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                            if (d < nearestDist) { nearestDist = d; nearestLadder = c; }
                        }
                    }
                    if (nearestLadder == null)
                    {
                        int n = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 3f, _overlapBuffer, ~0, QueryTriggerInteraction.Collide);
                        for (int ci = 0; ci < n; ci++)
                        {
                            var c = _overlapBuffer[ci];
                            if (!c.CompareTag("Ladder/Metal") && !c.CompareTag("Ladder/Chain")) continue;
                            float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                            if (d < nearestDist) { nearestDist = d; nearestLadder = c; }
                        }
                    }

                    if (nearestLadder != null && nearestDist > 0.6f)
                    {
                        // Walk toward the ladder to grab onto it
                        Vector3 toLadder = nearestLadder.ClosestPoint(transform.position) - transform.position;
                        toLadder.y = 0;
                        if (toLadder.sqrMagnitude > 0.01f)
                        {
                            _cc.Move(toLadder.normalized * _walkSpeed * Time.deltaTime);
                            LookAtDirection(toLadder.normalized);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Detect if the bot is embedded inside a wall (pushed by players or physics)
        /// and push it back to valid space. Uses CheckCapsule to detect overlaps.
        /// </summary>
        /// <summary>
        /// Detect if bot is inside solid geometry and push it out horizontally.
        /// </summary>

        /// <summary>
        /// Execute wall jump when conditions are met — matches FPC wall jump exactly.
        /// Only jumps when it would help (target is above or bot is falling).
        /// </summary>
        /// <summary>
        /// Propeller flight — matches FPC exactly: adds flySpeed*dt to verticalVelocity, capped at 7.
        /// Bot uses propeller when target is above, when stuck, or at edges it needs to cross.
        /// Recharges power on ground.
        /// </summary>
        private void HandlePropeller()
        {
            if (!_cachedIsPropeller || _cachedPropeller == null) return;

            // Read propeller fields via reflection (cached)
            float flySpeed = ReadFloatField(_cachedPropeller, "flySpeed", 15f);
            float maxPower = ReadFloatField(_cachedPropeller, "maxPower", 4f);
            float power = ReadFloatField(_cachedPropeller, "power", 4f);

            // Recharge on ground (matches FPC: power goes to maxPower when grounded)
            if (_cc.isGrounded)
            {
                if (power < maxPower)
                {
                    power = maxPower;
                    SetFloatField(_cachedPropeller, "power", power);
                }
            }

            // Decide when to fly
            bool shouldFly = false;
            Vector3 target = Vector3.zero;

            // Get current movement target
            if (_weaponTarget != null) target = _weaponTarget.position;
            else if (_playerTarget != null) target = _playerTarget.position;
            else if (_hasWanderTarget) target = _wanderTarget;
            else if (_graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                target = _graphPath[_graphPathIndex].Position;

            if (target != Vector3.zero)
            {
                float heightDiff = target.y - transform.position.y;
                // Fly when target is above us
                if (heightDiff > 2f && power > 0.5f)
                    shouldFly = true;
                // Fly when stuck and target is above
                if (heightDiff > 1f && _stuckTimer > 1f && power > 0.3f)
                    shouldFly = true;
                // Fly over edges/gaps toward target
                if (!_cc.isGrounded && _intentionalJumpTimer > 0f && heightDiff > 0f && power > 0.2f)
                    shouldFly = true;
            }

            // Also fly when at an edge we need to cross
            if (_cc.isGrounded && power > 1f)
            {
                Vector3 fwd = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
                if (IsEdgeAhead(fwd, 1f))
                    shouldFly = true;
            }

            if (shouldFly && power > 0f)
            {
                // Match FPC Fly() exactly: add flySpeed*dt to verticalVelocity, cap at 7
                if (_verticalVelocity < 7f)
                    _verticalVelocity += flySpeed * Time.deltaTime;
                power -= Time.deltaTime;
                SetFloatField(_cachedPropeller, "power", power);

                // Suppress landing pause while flying
                _landingFollowTimer = 0f;
            }
        }

        private float _afterVaultJumpTimer;

        /// <summary>
        /// Per-frame vault check — mirrors FPC.CheckForVault exactly: while airborne
        /// moving into a near-vertical face whose lip is passable (feet ray hits,
        /// chest + head rays clear), boost straight up (y=9, killed after 0.15s like
        /// the player's). The old collider-hit vault only fired while physically
        /// TOUCHING the wall and never during the bot's own jumps — which is exactly
        /// the player's mantle flow ("jump into the wall, vault"). Also implements the
        /// FPC after-vault jump: within 0.5s of a vault one extra full-force boost if
        /// the lip still isn't cleared — "vault, jump again" for taller ledges.
        /// </summary>
        private void HandleVaultMantle()
        {
            _afterVaultJumpTimer -= Time.deltaTime;
            if (_cc == null || !_cc.enabled || _cc.isGrounded || _onLadder || IsDead) return;
            if (_zoneForceDuration > 0f) return;
            if (_trajActive) return;             // recorded arcs are authoritative
            if (_verticalVelocity < -6f) return; // falling fast — wall jump handles that

            // Allowed during the bot's own close-range jumps; never during long
            // planned arcs (graph jumps / gap crossings have their own landing).
            if (_intentionalJumpTimer > 0f
                && _activeJumpReason != JumpReason.Obstacle
                && _activeJumpReason != JumpReason.StuckRecovery
                && _activeJumpReason != JumpReason.ExploreStuck
                && _activeJumpReason != JumpReason.WallJump
                && _activeJumpReason != JumpReason.Vault
                && _activeJumpReason != JumpReason.None)
                return;

            Vector3 fwd = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();

            if (!Physics.Raycast(transform.position + Vector3.up * 0.3f, fwd, out RaycastHit feetHit, 1.4f,
                    WALL_MASK, QueryTriggerInteraction.Ignore))
                return;
            float wallAngle = Vector3.Angle(feetHit.normal, Vector3.up);
            if (wallAngle < 80f || wallAngle > 130f) return;
            if (Physics.Raycast(transform.position + Vector3.up * 1.2f, fwd, 1.5f, WALL_MASK, QueryTriggerInteraction.Ignore))
                return;
            if (Physics.Raycast(transform.position + Vector3.up * 1.8f, fwd, 2f, WALL_MASK, QueryTriggerInteraction.Ignore))
                return;

            // The lip must have standable ground — never boost into nothing.
            Vector3 topCheck = transform.position + fwd * 1f + Vector3.up * 2.5f;
            if (!Physics.Raycast(topCheck, Vector3.down, out RaycastHit topHit, 3f,
                    GROUND_MASK, QueryTriggerInteraction.Ignore))
                return;

            if (!_vaultCooldown)
            {
                // VAULT — FPC: moveDirection.y = 9 + forward BForce, one per airtime.
                _verticalVelocity = 9f;
                _vaultCooldown = true;
                _vaultTakeoffPos = transform.position;
                _afterVaultJumpTimer = 0.5f;
                _vaultKillTimer = 0.15f;
                _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.4f);
                _jumpDir = fwd;
                _activeJumpReason = JumpReason.Vault;
                _airStrafeTarget = topHit.point + fwd * 0.3f;
                _airStrafeActive = true;
                _cc.Move(fwd * 1.5f * Time.deltaTime);
            }
            else if (_afterVaultJumpTimer > 0f && _verticalVelocity < 3f)
            {
                // AFTER-VAULT JUMP — FPC allows one full mid-air jump within 0.5s of a
                // vault (aftervaultjumpTimer): the "jump again" that mantles taller lips.
                _afterVaultJumpTimer = 0f;
                _verticalVelocity = _jumpForce;
                _vaultKillTimer = 0f;
                _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.5f);
                _airStrafeTarget = topHit.point + fwd * 0.3f;
                _airStrafeActive = true;
                Plugin.Log.LogInfo($"[{BotName}] After-vault jump");
            }
        }

        private void HandleWallJump()
        {
            // Reset wall jump count when grounded — record wall jump edge on landing
            if (_cc != null && _cc.isGrounded)
            {
                // Don't record wall jump edges for bots — they trigger accidentally
                // from brushing geometry. Only player wall jumps are intentional.
                _wallJumpCount = 0;
                _canWallJump = false;
                return;
            }

            if (!_canWallJump || _wallJumpCount >= 1) return;

            // NEVER wall jump during an intentional jump — let it land first
            if (_intentionalJumpTimer > 0f && _verticalVelocity > -2f) return;
            // Don't wall jump during landing follow-through
            if (_landingFollowTimer > 0f) return;

            // Determine target position
            Vector3 targetPos = transform.position;
            bool hasTarget = false;
            if (_weaponTarget != null) { targetPos = _weaponTarget.position; hasTarget = true; }
            else if (_playerTarget != null) { targetPos = _playerTarget.position; hasTarget = true; }

            bool targetAbove = hasTarget && targetPos.y > transform.position.y + 2f;
            bool falling = _verticalVelocity < -5f;

            // Check if wall jump would take us way further from target (allow some slack for going around)
            if (hasTarget && !falling && _stuckTimer < 0.5f)
            {
                Vector3 afterJump = transform.position + _wallJumpNormal * 2f + Vector3.up * 2f;
                float currentDist = Vector3.Distance(transform.position, targetPos);
                float afterDist = Vector3.Distance(afterJump, targetPos);
                if (afterDist > currentDist * 1.5f) return; // Only reject if significantly further
            }

            // Wall jump conditions: target above, falling, OR stuck against a wall
            bool stuck = _stuckTimer > 0.5f;
            if (!targetAbove && !falling && !stuck) return;

            // Calculate push direction: 60% wall normal + 40% toward target
            Vector3 pushDir = _wallJumpNormal;
            pushDir.y = 0;
            if (pushDir.sqrMagnitude > 0.01f)
            {
                pushDir.Normalize();
                if (hasTarget)
                {
                    Vector3 toTarget = targetPos - transform.position;
                    toTarget.y = 0;
                    if (toTarget.sqrMagnitude > 0.5f)
                        pushDir = Vector3.Lerp(pushDir, toTarget.normalized, 0.4f).normalized;
                }
            }
            else pushDir = transform.forward;

            // 80% force like FPC: moveDirection.y = jumpForce * 0.8f * wallJumpFactor
            if (!TryJump(JumpReason.WallJump, pushDir, force: _jumpForce * 0.8f))
                return;

            _wallJumpCount++;
            _canWallJump = false;

            // Horizontal push away from wall (FPC uses BForce, we use direct CC nudge)
            if (_cc != null && _cc.enabled)
                _cc.Move(pushDir * 2f * Time.deltaTime);

            Plugin.Log.LogInfo($"[{BotName}] Wall jump! vel={_verticalVelocity:F1}");
        }

        // ===================== UNIFIED JUMP GATE =====================

        /// <summary>
        /// Single entry point for ALL jump actions. Handles priority gating, state setup,
        /// slide cancellation, and prevents conflicting jumps from overriding each other.
        /// Returns true if the jump was accepted, false if blocked.
        /// </summary>
        /// <param name="reason">Why we're jumping — determines priority</param>
        /// <param name="direction">Horizontal direction to lock during jump (normalized)</param>
        /// <param name="force">Vertical force (default _jumpForce=8). Vault uses 9, wall jump uses 6.4</param>
        /// <param name="intentionalTime">How long to suppress reactive steering. 0 = auto-calculate from distance</param>
        /// <param name="jumpEdge">Optional NavEdge being followed (for trajectory replay + locked speed)</param>
        private bool TryJump(JumpReason reason, Vector3 direction, float force = 0f,
            float intentionalTime = 0f, NavEdge jumpEdge = null)
        {
            if (_cc == null || !_cc.enabled) return false;

            // --- Hard blocks: never jump in these states ---
            if (_onLadder) return false;
            if (_ladderDismountTimer > 0f) return false;
            if (_zoneForceDuration > 0f) return false;
            if (IsDead || State == BotState.Dead) return false;

            // --- Priority gate: active jump can only be overridden by equal or higher priority ---
            if (_intentionalJumpTimer > 0f && !_cc.isGrounded && reason < _activeJumpReason)
                return false;

            // --- Grounded check: most jumps require grounded (or coyote time) ---
            // Exceptions: WallJump (explicitly airborne), Vault (checked by caller)
            if (reason != JumpReason.WallJump && reason != JumpReason.Vault)
            {
                if (!_cc.isGrounded && _coyoteTimer <= 0f) return false;
                if (_onSuperIce) return false; // FPC blocks all jumps on super ice (FirstPersonController L1076)
            }

            // --- Defaults ---
            if (force <= 0f) force = _jumpForce;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;
            direction.y = 0f;
            direction.Normalize();

            // --- Landing gate: reactive jumps must see SOME ground along the arc ---
            // These jumps used to launch on faith ("target is within max jump distance")
            // and bots regularly leapt into the void. Learned graph jumps keep their
            // recorded trust; every reactive/panic jump is gated — including stuck
            // recovery, which could panic-jump a bot straight off a void edge.
            if (reason == JumpReason.EdgeAhead || reason == JumpReason.GapDetection
                || reason == JumpReason.StuckRecovery || reason == JumpReason.ExploreStuck)
            {
                if (!HasJumpLanding(direction)) return false;
            }

            // --- Arc rehearsal: does the first half-second of the parabola even fit? ---
            if (reason == JumpReason.EdgeAhead || reason == JumpReason.GapDetection
                || reason == JumpReason.Obstacle)
            {
                if (!RehearseJumpArc(direction, force)) return false;
            }

            // Default intentional time based on reason if not specified
            if (intentionalTime <= 0f)
            {
                switch (reason)
                {
                    case JumpReason.Vault:          intentionalTime = 0.3f; break;
                    case JumpReason.CombatStrafe:    intentionalTime = 0.5f; break;
                    case JumpReason.StuckRecovery:   intentionalTime = 0.8f; break;
                    case JumpReason.ExploreStuck:    intentionalTime = 0.8f; break;
                    case JumpReason.Obstacle:        intentionalTime = 0.6f; break;
                    case JumpReason.WallJump:        intentionalTime = 1.0f; break;
                    case JumpReason.GapDetection:    intentionalTime = 1.5f; break;
                    case JumpReason.EdgeAhead:       intentionalTime = 1.5f; break;
                    case JumpReason.GraphJump:       intentionalTime = 1.5f; break; // Overridden by airTimeEst if jumpEdge set
                    default:                         intentionalTime = 0.8f; break;
                }
            }

            // --- Cancel slide if active (can't jump while sliding) ---
            if (_isSliding)
            {
                EndSlide();
            }

            // --- Set all jump state atomically ---
            // SMOOTHNESS: for GraphJump / EdgeAhead / GapDetection we use a short charge
            // window — vertical velocity applies 2 frames later so the bot commits direction
            // and full speed first. Reactive/emergency jumps fire immediately.
            bool useCharge =
                reason == JumpReason.GraphJump ||
                reason == JumpReason.EdgeAhead ||
                reason == JumpReason.GapDetection ||
                reason == JumpReason.Obstacle;   // box-up jumps benefit from commit phase too
            if (useCharge)
            {
                _jumpChargeTimer = 0.035f;     // ~2 physics frames at 60Hz
                _pendingJumpForce = force;
                _verticalVelocity = 0f;         // hold — no fall, no rise yet
            }
            else
            {
                _verticalVelocity = force;
                _jumpChargeTimer = 0f;
                _pendingJumpForce = 0f;
            }
            _coyoteTimer = 0f;
            _intentionalJumpTimer = intentionalTime;
            _justJumped = true;
            _jumpDir = direction;
            _activeJumpReason = reason;
            _jumpStartTime = Time.time;
            _jumpMidCorrected = false;

            // Vault-specific: FPC kills vertical velocity after 0.15s
            if (reason == JumpReason.Vault)
                _vaultKillTimer = 0.15f;
            else
                _vaultKillTimer = 0f;

            // Graph jump edge: set up trajectory replay. Replay whenever we have at least a
            // takeoff+landing pair (>=2 samples) — short/fast recorded jumps used to fall back
            // to recomputed physics and miss. (Replay's t=0 is re-stamped at the real launch
            // instant in ApplyGravity's charge-fire so the arc lines up.)
            _currentJumpEdge = jumpEdge;
            if (jumpEdge != null && jumpEdge.AirSampleCount >= 2)
            {
                _trajActive = true;
                _trajIndex = 0;
            }
            else
            {
                _trajActive = false;
            }

            // Use locked takeoff direction from successful previous traversal
            if (jumpEdge != null && jumpEdge.TakeoffDir.sqrMagnitude > 0.01f)
                _jumpDir = jumpEdge.TakeoffDir;

            return true;
        }

        private float _losSkipTimer;
        private float _trajDriftTimer; // Replay drift accumulator — aborts bad-match recordings

        /// <summary>Point ~2.6m along the remaining path polyline (walk edges only) —
        /// the pure-pursuit steering target. Stops at the node BEFORE any special edge
        /// so jump lineups and ladder mounts still hit their nodes exactly.</summary>
        private Vector3 ComputePursuitPoint(Vector3 fallback)
        {
            if (_graphPath == null || _graphPathIndex >= _graphPath.Count) return fallback;
            float remaining = 2.6f;
            Vector3 cur = transform.position;
            for (int i = _graphPathIndex; i < _graphPath.Count; i++)
            {
                if (i > _graphPathIndex)
                {
                    var e = FindBestPathEdge(_graphPath[i - 1].Id, _graphPath[i].Id);
                    if (e != null && e.Type != EdgeType.Walk)
                        return _graphPath[i - 1].Position;
                }
                Vector3 wp = _graphPath[i].Position;
                float segLen = Vector3.Distance(cur, wp);
                if (segLen >= remaining)
                    return Vector3.Lerp(cur, wp, remaining / Mathf.Max(segLen, 0.001f));
                remaining -= segLen;
                cur = wp;
            }
            return _graphPath[_graphPath.Count - 1].Position;
        }

        /// <summary>Straight-line walkability: eye-level line of sight plus ground
        /// continuity samples along the way. Used by the Play-mode waypoint skip.</summary>
        private bool CanWalkStraightTo(Vector3 wp)
        {
            if (Mathf.Abs(wp.y - transform.position.y) > 1.5f) return false;
            Vector3 eye = transform.position + Vector3.up * 1.2f;
            Vector3 to = (wp + Vector3.up * 1.2f) - eye;
            float dist = to.magnitude;
            if (dist > 12f || dist < 0.05f) return false;
            if (Physics.Raycast(eye, to / dist, dist, WALL_MASK, QueryTriggerInteraction.Ignore)) return false;

            Vector3 flat = wp - transform.position;
            flat.y = 0f;
            float fd = flat.magnitude;
            if (fd < 1f) return true;
            Vector3 fdir = flat / fd;
            for (float d = 1.5f; d < fd; d += 2f)
            {
                Vector3 probe = transform.position + fdir * d + Vector3.up * 1f;
                if (!Physics.Raycast(probe, Vector3.down, 3f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Arc rehearsal: sphere-cast the first half-second of the predicted parabola.
        /// Catches "the doorframe/lip overhead clips me right after launch" — the
        /// landing gate only proves ground exists, not that the arc fits. Only refuses
        /// on overhead (ceiling-facing) hits so mantling a ledge still works.
        /// </summary>
        private bool RehearseJumpArc(Vector3 dir, float force)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return true;
            dir.Normalize();
            Vector3 vel = _cc != null ? _cc.velocity : Vector3.zero;
            float h = Mathf.Clamp(new Vector2(vel.x, vel.z).magnitude, 4f, _sprintAirSpeed);
            Vector3 prev = transform.position + Vector3.up * 0.9f; // capsule center
            for (int i = 1; i <= 4; i++)
            {
                float t = 0.12f * i;
                Vector3 p = transform.position + dir * (h * t)
                    + Vector3.up * (0.9f + force * t - 0.5f * _gravityJump * t * t);
                Vector3 seg = p - prev;
                if (seg.sqrMagnitude > 0.0001f)
                {
                    float segLen = seg.magnitude;
                    if (Physics.SphereCast(prev, 0.34f, seg / segLen, out RaycastHit hit, segLen,
                            WALL_MASK, QueryTriggerInteraction.Ignore)
                        && hit.normal.y < -0.35f)
                        return false; // smacks the underside of something overhead
                }
                prev = p;
            }
            return true;
        }

        /// <summary>
        /// Is there ANY real ground along the jump direction? Samples downward rays
        /// along the flight corridor out to max jump range; if every sample falls into
        /// nothing (void), the jump is refused. Depth doesn't matter — Straftat has no
        /// fall damage; only the kill-void does.
        /// </summary>
        private bool HasJumpLanding(Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();
            float maxD = Mathf.Min(Plugin.GetMaxJumpDist(), 10f);
            for (float d = 1.2f; d <= maxD; d += 1f)
            {
                Vector3 probe = transform.position + dir * d + Vector3.up * 1.2f;
                if (Physics.Raycast(probe, Vector3.down, 80f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Knee-high obstacles sit BELOW the 0.7m feet ray and defeat the CC's 0.6 step
        /// offset on sharp edges — bots used to grind against them forever. Detect a low
        /// blocker with a clear top within hop height and jump onto it.
        /// </summary>
        private bool TryKneeHop(Vector3 dir, int wallMask)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();

            if (!Physics.Raycast(transform.position + Vector3.up * 0.3f, dir, out RaycastHit kneeHit, 0.9f,
                    wallMask, QueryTriggerInteraction.Ignore))
                return false;
            if (Vector3.Angle(kneeHit.normal, Vector3.up) < 55f) return false; // walkable ramp — let CC handle it

            // Chest must be clear — otherwise this is a real wall, not a knee blocker.
            if (Physics.Raycast(transform.position + Vector3.up * 1.3f, dir, kneeHit.distance + 0.5f,
                    wallMask, QueryTriggerInteraction.Ignore))
                return false;

            // A top surface within hop height.
            Vector3 topProbe = transform.position + dir * (kneeHit.distance + 0.45f) + Vector3.up * 1.6f;
            if (!Physics.Raycast(topProbe, Vector3.down, out RaycastHit topHit, 1.7f,
                    GROUND_MASK, QueryTriggerInteraction.Ignore))
                return false;
            float rise = topHit.point.y - transform.position.y;
            if (rise < 0.25f || rise > 1.05f) return false;

            _airStrafeTarget = topHit.point + dir * 0.3f;
            _airStrafeActive = true;
            return TryJump(JumpReason.Obstacle, dir, intentionalTime: 0.45f);
        }

        /// <summary>
        /// Reset jump state when landing. Called from landing pause logic.
        /// </summary>
        private void ClearJumpState()
        {
            _intentionalJumpTimer = 0f;
            _jumpDir = Vector3.zero;
            _justJumped = false;
            _currentJumpEdge = null;
            _trajActive = false;
            _trajIndex = 0;
            _activeJumpReason = JumpReason.None;
            _vaultKillTimer = 0f;
            _inJumpChain = false;
            _chainJumpCount = 0;
        }

        // ===================== GRAVITY =====================

        private void ApplyGravity()
        {
            if (_cc == null || !_cc.enabled) return;
            PruneDestroyedGravityZones();

            // SMOOTHNESS: jump charge window.
            // When a graph/edge/gap jump is queued, _jumpChargeTimer holds the bot's
            // vertical velocity at 0 for ~35 ms so it can commit its heading and reach
            // full horizontal speed BEFORE the upward impulse. When the timer elapses,
            // the queued force is applied and gravity resumes normally.
            if (_jumpChargeTimer > 0f)
            {
                _jumpChargeTimer -= Time.deltaTime;
                _verticalVelocity = 0f;
                // Force full horizontal commit during the charge window
                _currentHorizInput = 1f;

                if (_jumpChargeTimer <= 0f)
                {
                    _verticalVelocity = _pendingJumpForce;
                    _pendingJumpForce = 0f;
                    _jumpChargeTimer = 0f;
                    // The impulse fires NOW — this is the real launch instant. Re-stamp the
                    // jump start so trajectory replay indexes recorded AirTimestamps from the
                    // ACTUAL liftoff, not from the ~35ms-earlier TryJump call. That offset made
                    // the replayed arc lead the bot and caused overshoot/undershoot.
                    _jumpStartTime = Time.time;
                    _trajIndex = 0;
                    // Fall through to normal gravity handling this frame so
                    // the impulse takes effect immediately.
                }
                else
                {
                    return; // still charging — skip the rest of gravity this frame
                }
            }

            // Sky-flight safety: even if _onLadder is set, bail to normal gravity
            // unless we've actually touched a ladder recently. Prevents stuck-ladder
            // bots from rocketing upward indefinitely.
            if (_onLadder && Time.time - _lastLadderTouchTime > 0.5f)
            {
                _onLadder = false;
                _verticalVelocity = Mathf.Min(_verticalVelocity, 0f);
            }

            if (_onLadder) return; // Ladder handles vertical velocity
            if (_trajActive && _currentJumpEdge != null)
            {
                // Replay controls vertical velocity — but ONLY while the jump window is
                // live. If the arc ended without a landing (mismatched/stale recording),
                // a blind return here froze vertical velocity forever and bots WALKED IN
                // MID-AIR. Hand back to real gravity instead.
                if (_intentionalJumpTimer > 0f) return;
                _trajActive = false;
                _currentJumpEdge = null;
            }

            // Hard cap on upward velocity — nothing in Straftat should send bots above jump impulse.
            // If external forces push above 2× jumpForce, clamp (zones can still launch normally,
            // including continuous updraft ForceZones which don't set a launch duration).
            if (_verticalVelocity > _jumpForce * 2.5f && _zoneForceDuration <= 0f && !_zoneVerticalActive)
                _verticalVelocity = _jumpForce * 2.5f;

            // Vault velocity kill — FPC kills moveDirection.y after 0.15s via coroutine
            if (_vaultKillTimer > 0f)
            {
                _vaultKillTimer -= Time.deltaTime;
                if (_vaultKillTimer <= 0f)
                {
                    // Match FPC DeactivateVault: if grounded, push down; else zero
                    if (_cc.isGrounded)
                        _verticalVelocity = -5f;
                    else if (!_justJumped)
                        _verticalVelocity = 0f;
                    _vaultKillTimer = 0f;
                }
            }

            // Zone launch protection — don't fight launch forces
            bool zoneLaunch = _zoneForceDuration > 0f && _verticalVelocity > 2f;

            // Track grounded transitions for coyote time
            bool grounded = _cc.isGrounded;
            if (grounded)
            {
                // Don't kill upward velocity during zone launch — the launch zone
                // just set _verticalVelocity and we need to preserve it
                if (zoneLaunch)
                    _coyoteTimer = 0f; // Cancel coyote — we're launching
                else
                {
                    _verticalVelocity = -1f; // Match FPC: moveDirection.y = -1 when grounded
                    // Ice slope: press into the surface like FPC's OnSlopeIce gravity pump,
                    // so the capsule stays grounded while the ice slide pushes it downhill
                    // (otherwise the horizontal push skips the bot off the slope every frame).
                    if (_onIce && _iceSlopeAngle > 10f && _iceSlopeAngle < 65f)
                    {
                        float slideMagSqr = (_iceSlideMove.x + _iceCrouchSlideMove.x) * (_iceSlideMove.x + _iceCrouchSlideMove.x)
                                          + (_iceSlideMove.z + _iceCrouchSlideMove.z) * (_iceSlideMove.z + _iceCrouchSlideMove.z);
                        if (slideMagSqr > 0.25f)
                        {
                            float needed = Mathf.Sqrt(slideMagSqr) * Mathf.Tan(_iceSlopeAngle * Mathf.Deg2Rad) + 2f;
                            _verticalVelocity = Mathf.Max(-needed, _maxFallSpeed);
                        }
                    }
                    _coyoteTimer = 0.15f;
                    _activeJumpReason = JumpReason.None; // Landed — clear priority lock
                    // Air-strafe is only valid while airborne — clear on landing.
                    _airStrafeActive = false;
                }
            }
            else
            {
                _coyoteTimer -= Time.deltaTime;
                // Exact FPC gravity: 20 when rising, 30 when falling, 40 when crouching
                float grav = _isCrouching ? _gravityCrouch
                    : (_verticalVelocity > 0 ? _gravityJump : _gravityNormal);
                _verticalVelocity -= grav * _gravityZoneMultiplier * Time.deltaTime;
                if (_verticalVelocity < _maxFallSpeed) _verticalVelocity = _maxFallSpeed;

                // AIR STRAFE: nudge horizontally toward the intended landing point.
                // Applies whenever an airborne bot has an active strafe target — bridges the
                // gap between open-loop jump physics and the actual target the bot aimed for.
                // Player-like 1:1 trajectory correction, magnitude scales with how far off the
                // ideal approach vector we are.
                if (_airStrafeActive && _intentionalJumpTimer > 0f)
                {
                    Vector3 toTarget = _airStrafeTarget - transform.position;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 0.04f)
                    {
                        Vector3 desiredDir = toTarget.normalized;
                        Vector3 flatFwd = transform.forward;
                        flatFwd.y = 0f;
                        if (flatFwd.sqrMagnitude > 0.01f) flatFwd.Normalize();
                        // Difference between current facing and desired direction.
                        Vector3 lateral = desiredDir - flatFwd;
                        lateral.y = 0f;
                        // Nudge magnitude: 2 m/s horizontal at most, scaled by offset.
                        float nudge = Mathf.Clamp01(lateral.magnitude) * 2f * Time.deltaTime;
                        if (nudge > 0f)
                            _cc.Move(lateral.normalized * nudge);
                    }
                }
            }
        }

        private void LookAtTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                Quaternion rot = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.Slerp(transform.rotation, rot, 8f * Time.deltaTime);
            }
        }

        private void LookAtDirection(Vector3 dir)
        {
            if (dir.sqrMagnitude > 0.01f)
            {
                Quaternion rot = Quaternion.LookRotation(dir);
                // SMOOTHNESS: clamp the per-frame angle change so bots never snap.
                // Slerp alone can produce near-instant turns at high _turnSpeed; RotateTowards
                // caps the max degree-delta. 360 deg/sec is fast enough to track any target but
                // kills the 180°-in-2-frames teleport that happens when targets switch.
                const float MAX_TURN_DEG_PER_SEC = 360f;
                float maxStep = MAX_TURN_DEG_PER_SEC * Time.deltaTime;
                Quaternion slerped = Quaternion.Slerp(transform.rotation, rot, _turnSpeed * Time.deltaTime);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, slerped, maxStep);
            }
        }

    }
}
