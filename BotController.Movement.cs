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
            _cc.Move(motion);
            _movedThisFrame = true;
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

        private void MoveToward(Vector3 target, float speed)
        {
            if (_cc == null || !_cc.enabled) return;

            // Auto-nodeless: if graph is unusable, bot is in an untrained area, or bot has hit
            // a bounce/stuck lock, use direct movement. Always on — previously toggleable via
            // SmartFallback; no reason to disable.
            {
                // Decay the bounce escalation once the bot has been behaving for 30s
                if (_nodelessBounceCount > 0 && Time.time - _lastBounceTime > 30f)
                    _nodelessBounceCount = Mathf.Max(0, _nodelessBounceCount - 1);

                // Bounce lock — set by ping-pong detection below. While the lock is active,
                // skip graph entirely and path directly to the target. This lets the bot reach
                // enemies even when the local graph cluster is tangled with bad edges.
                if (_nodelessLockTimer > 0f)
                {
                    _nodelessLockTimer -= Time.deltaTime;
                    MoveTowardNodeless(target, speed);
                    return;
                }

                bool graphUsable = NavGraph.Instance != null && NavGraph.Instance.HasData
                    && NavGraph.Instance.NodeCount >= 10;
                if (!graphUsable)
                {
                    MoveTowardNodeless(target, speed);
                    return;
                }

                var nearBot = NavGraph.Instance.FindNearestNode(transform.position, 8f);
                if (nearBot == null)
                {
                    MoveTowardNodeless(target, speed);
                    return;
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
                    // Decay horizontal zone force slightly — matches player's moveDirection friction
                    _zoneForce *= Mathf.Max(0f, 1f - 2f * Time.deltaTime);
                    _zoneForceDuration -= Time.deltaTime;
                    if (_zoneForceDuration <= 0f)
                    {
                        _zoneForce = Vector3.zero;
                        _zoneForceDuration = 0f;
                        _zoneLaunchInAir = false;
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

            if (NavGraph.Instance != null && NavGraph.Instance.HasData)
            {
                _repathTimer -= Time.deltaTime;
                float distToTarget = Vector3.Distance(_lastPathTarget, target);
                // Don't repath while on ladder, dismounting, or mid-jump
                bool suppressRepath = _onLadder || _ladderDismountTimer > 0f
                    || (_intentionalJumpTimer > 0f && !_cc.isGrounded);
                // Don't repath if we have a working path and are making progress
                bool hasWorkingPath = _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count;
                bool targetMoved = distToTarget > 5f; // Only repath if target moved significantly

                if (!suppressRepath && (!hasWorkingPath || _repathTimer <= 0f || targetMoved))
                {
                    // Adaptive repath interval: fast when no path, slow when path is working
                    _repathTimer = hasWorkingPath ? 2f : 1f;
                    _lastPathTarget = target;

                    // Try cached route first (instant), then A*
                    // Prefer height-gaining paths when target is above us
                    bool wantHeight = target.y > transform.position.y + 2f;
                    _graphPath = NavGraph.Instance.GetCachedRoute(transform.position, target);
                    if (_graphPath.Count == 0)
                        _graphPath = NavGraph.Instance.FindPath(transform.position, target, preferHeight: wantHeight);
                    _graphPathIndex = 0;

                    // No connected path to target — try alternatives
                    if (_graphPath.Count == 0)
                    {
                        // Try wider search radius
                        _graphPath = NavGraph.Instance.FindPath(transform.position, target, searchRadius: 40f);
                        _graphPathIndex = 0;
                    }
                    if (_graphPath.Count == 0)
                    {
                        // Try closest reachable node near target
                        var closestReachable = NavGraph.Instance.FindClosestReachableNode(
                            transform.position, target);
                        if (closestReachable != null)
                        {
                            _graphPath = NavGraph.Instance.FindPath(transform.position, closestReachable.Position);
                            _graphPathIndex = 0;
                        }
                    }
                    // Try patrol routes as highways — find a saved route that passes near the target
                    if (_graphPath.Count == 0)
                    {
                        var patrolPath = NavGraph.Instance.FindNearestPatrolRoute(transform.position, target);
                        if (patrolPath.Count > 0)
                        {
                            _graphPath = patrolPath;
                            _graphPathIndex = 0;
                        }
                    }
                    if (_graphPath.Count == 0)
                    {
                        // All graph pathing failed — fall back to nodeless direct movement
                        MoveTowardNodeless(target, speed);
                        return;
                    }
                }

                // Advance past reached nodes
                while (_graphPathIndex < _graphPath.Count)
                {
                    float distToNode = Vector3.Distance(transform.position, _graphPath[_graphPathIndex].Position);
                    if (distToNode < 0.7f)
                    {
                        var reachedNode = _graphPath[_graphPathIndex];

                        // Report success — rehabilitates bad nodes, boosts confidence
                        if (_lastReachedNode != null)
                            NavGraph.Instance.ReportSuccess(_lastReachedNode.Id, reachedNode.Id);

                        // Compress clusters around well-traveled areas (every 5th node to avoid spam)
                        if (reachedNode.VisitCount % 5 == 0)
                            NavGraph.Instance.CompressNearby(reachedNode.Position);

                        // Track recent node history for ping-pong detection
                        _recentNodeIds[_recentNodeIdx] = reachedNode.Id;
                        _recentNodeIdx = (_recentNodeIdx + 1) % _recentNodeIds.Length;
                        if (_recentNodeCount < _recentNodeIds.Length) _recentNodeCount++;

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

                        // Track repeated node visits — delete bad edges
                        if (reachedNode.Id == _lastNodeRepeatedId)
                        {
                            _nodeRepeatCount++;
                        }

                        if ((_nodeRepeatCount >= 10 || pingPong) && NavGraph.Instance != null)
                        {
                            // Bouncing between nodes — penalize the edges causing it
                            Plugin.Log.LogInfo($"[{BotName}] {(pingPong ? "Ping-pong" : "Repeat")} detected at node {reachedNode.Id}");
                            var badEdges = NavGraph.Instance.GetEdgesFrom(reachedNode.Id);
                            foreach (var be in badEdges)
                            {
                                if (be.Type == EdgeType.Jump || be.Type == EdgeType.Fall || be.Type == EdgeType.WallJump)
                                {
                                    // Check if the target is one of our recent nodes (we keep going back to it)
                                    bool isRecent = false;
                                    for (int ri = 0; ri < _recentNodeCount; ri++)
                                        if (_recentNodeIds[ri] == be.To) { isRecent = true; break; }
                                    if (isRecent)
                                    {
                                        be.Confidence = Mathf.Max(be.Confidence - 0.5f, -1f);
                                        if (be.Confidence <= 0f) be.Confidence = -1f;
                                        Plugin.Log.LogInfo($"[{BotName}] Penalized bounce edge {reachedNode.Id}->{be.To}");
                                    }
                                }
                            }
                            _nodeRepeatCount = 0;
                            _recentNodeCount = 0;
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            _stuckTimer = 1f; // Trigger stuck recovery

                            // ENGAGE NODELESS LOCK — bypass the graph entirely for a window so the
                            // bot can actually reach the target. Escalate on repeat bounces so a
                            // bot stuck in a persistent tangle gets longer nodeless windows each time.
                            _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                            _lastBounceTime = Time.time;
                            // 4s base + 2s per escalation, up to 14s
                            _nodelessLockTimer = Mathf.Min(14f, 4f + 2f * _nodelessBounceCount);
                            Plugin.Log.LogInfo($"[{BotName}] Nodeless lock engaged for {_nodelessLockTimer:F1}s (bounce #{_nodelessBounceCount})");
                            break;
                        }

                        _lastNodeRepeatedId = reachedNode.Id;

                        // Shortcut detection: bot walked prev → last → reached cleanly.
                        // If prev → reached is directly walkable, add the shortcut edge and
                        // decay the detour so A* prefers the straight route next time.
                        if (_prevReachedNode != null && _lastReachedNode != null && NavGraph.Instance != null)
                        {
                            NavGraph.Instance.TryShortcut(_prevReachedNode.Id, _lastReachedNode.Id, reachedNode.Id);
                        }

                        // Rotate the 2-deep history BEFORE overwriting _lastReachedNode
                        _prevReachedNode = _lastReachedNode;
                        _lastReachedNode = reachedNode;
                        _wallRepathCount = 0;
                        _graphPathIndex++;

                        // Re-check NearEdge: bot walked here, new nodes may exist below nearby edges
                        if (reachedNode.NearEdge && NavGraph.Instance != null)
                        {
                            reachedNode.NearEdge = NavGraph.Instance.CheckNearEdgePublic(reachedNode.Position);
                        }
                    }
                    else break;
                }

                if (_graphPathIndex < _graphPath.Count)
                {
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
                            return;
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
                        NavEdge jumpEdge = null;
                        if (_lastReachedNode != null && NavGraph.Instance != null)
                            jumpEdge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);

                        Vector3 jumpToNode = nodePos - transform.position;
                        float jumpTotalDist = jumpToNode.magnitude;
                        float jumpHeightDiff = Mathf.Abs(jumpToNode.y);
                        float maxJump = Plugin.GetMaxJumpDist();

                        // DELETE impossible jumps — too far, too many failures, or no ground
                        bool impossible = jumpTotalDist > maxJump || jumpHeightDiff > maxJump * 0.5f;
                        if (jumpEdge != null && jumpEdge.FailCount >= 5) impossible = true;
                        bool destHasGround = Physics.Raycast(nodePos + Vector3.up * 2f, Vector3.down, 6f,
                            GROUND_MASK, QueryTriggerInteraction.Ignore);
                        bool isPlayMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play;
                        if (isPlayMode && !destHasGround) impossible = true;

                        if (impossible)
                        {
                            if (jumpEdge != null && jumpEdge.FailCount >= 5)
                            {
                                jumpEdge.Confidence = -1f; // Delete the edge permanently
                                Plugin.Log.LogInfo($"[{BotName}] Deleted impossible jump edge ({jumpEdge.FailCount} fails)");
                            }
                            if (_lastReachedNode != null)
                                NavGraph.Instance?.ReportFallOnEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id, BotId);
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            nextEdgeType = EdgeType.Walk;
                        }
                    }
                    if (nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.WallJump)
                    {
                        NavEdge jumpEdge = null;
                        if (_lastReachedNode != null && NavGraph.Instance != null)
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
                                    jumpEdge.Confidence = -1f;
                                    NavGraph.Instance.AddEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id,
                                        EdgeType.Walk, Vector3.Distance(fromPos, toPos));
                                    Plugin.Log.LogInfo($"[{BotName}] Converted jump edge to walk — ground is walkable");
                                }
                                nextEdgeType = EdgeType.Walk;
                            }
                        }
                    }
                    if (nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.WallJump)
                    {
                        NavEdge jumpEdge = null;
                        if (_lastReachedNode != null && NavGraph.Instance != null)
                            jumpEdge = NavGraph.Instance.GetEdgeBetween(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id);

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
                            // PHASE 1: Walk to takeoff node
                            float distToTakeoff = _lastReachedNode != null ?
                                Vector3.Distance(transform.position, _lastReachedNode.Position) : 0f;

                            if (distToTakeoff > 0.5f && _lastReachedNode != null)
                            {
                                // Walk to takeoff position — sprint if in a jump chain
                                Vector3 toTakeoff = _lastReachedNode.Position - transform.position;
                                toTakeoff.y = 0;
                                if (toTakeoff.sqrMagnitude > 0.1f) dir = toTakeoff.normalized;
                                speed = _inJumpChain ? _sprintSpeed : _walkSpeed;
                            }
                            // PHASE 2: At takeoff — speed-match and jump with momentum
                            else if (IsEdgeAhead(jumpFaceDir, 0.7f) || jumpHorizDist < 1.5f)
                            {
                                // Determine target takeoff speed from recorded data
                                float targetTakeoffSpeed = speed; // default from physics calc
                                if (jumpEdge != null)
                                {
                                    if (jumpEdge.TakeoffSpeed > 0.1f)
                                        targetTakeoffSpeed = jumpEdge.TakeoffSpeed;
                                    else if (jumpEdge.LockedSpeed > 0f)
                                        targetTakeoffSpeed = jumpEdge.LockedSpeed;
                                }
                                targetTakeoffSpeed = Mathf.Clamp(targetTakeoffSpeed, _walkSpeed, _sprintSpeed);

                                // Use recorded takeoff direction if available
                                Vector3 takeoffDir = jumpFaceDir;
                                if (jumpEdge != null && jumpEdge.TakeoffDir.sqrMagnitude > 0.01f)
                                    takeoffDir = jumpEdge.TakeoffDir;

                                // Maintain speed and direction — no stopping
                                dir = takeoffDir;
                                speed = targetTakeoffSpeed;
                                _currentHorizInput = Mathf.MoveTowards(_currentHorizInput, 1f, 5f * Time.deltaTime);
                                LookAtDirection(takeoffDir);

                                // Jump when facing and speed are adequate
                                float facingDot = Vector3.Dot(transform.forward, takeoffDir);
                                bool facingOk = facingDot > 0.85f;
                                bool speedOk = _currentHorizInput > 0.6f || jumpHorizDist < 1.0f;

                                if (facingOk && speedOk)
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
                            // PHASE 3: Approaching edge — sprint toward it
                            else
                            {
                                // Approaching — run toward edge at calculated speed
                                speed = Mathf.Max(speed, _walkSpeed);
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

                    dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
                    else dir.Normalize();
                }
                else
                {
                    // Path exhausted or empty
                    bool seekingLadder = false;
                    if (_cc.isGrounded && !_onLadder && target.y > transform.position.y + 2f && _stuckTimer > 1f)
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
            }

            // Proactive slide/crouch: detect low ceilings and slide-only passages
            // Two triggers: (1) immediate when head blocked + crouch clear, (2) when stuck 0.5s
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder
                && _intentionalJumpTimer <= 0f)
            {
                var slideObs = CheckObstructions(dir);

                // Trigger 1: head blocked but crouch clear — low ceiling passage, slide immediately
                // Trigger 2: waist blocked + crouch clear + stuck — wall with crawl space
                bool shouldSlide = false;
                if (slideObs.CrouchClear && slideObs.HeadBlocked && !slideObs.WaistBlocked)
                    shouldSlide = true; // Low ceiling — slide right away
                else if (slideObs.CrouchClear && slideObs.WaistBlocked && _stuckTimer > 0.3f)
                    shouldSlide = true; // Wall with crawl space — slide after brief stuck

                if (shouldSlide)
                {
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
                    bool shouldJump = nextEdgeType == EdgeType.Jump || nextEdgeType == EdgeType.Fall
                        || nextEdgeType == EdgeType.WallJump || targetAcrossGap;

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
                        }
                    }
                    else
                    {
                        // No target across — turn away from edge
                        dir = TryAngledDirections(dir, wallMask);
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

                        // Proportional drift correction — engage early, scale with distance
                        if (totalDrift > 0.05f)
                        {
                            float correctionStrength = Mathf.Clamp01(totalDrift / 0.5f);
                            Vector3 correction = toTarget.normalized * correctionStrength
                                * Mathf.Min(totalDrift, 1.0f) * Time.deltaTime * 10f;
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

            // During slide: force impulse handles movement, reduce normal speed to near-zero
            // so the slide force is the primary mover (matches FPC behavior)
            if (_isSliding) targetSpeed = 0.5f;

            // Sprint slide
            if (sprinting && grounded && !_isSliding && !_isShooting && _slideResetTimer <= 0f)
            {
                _sprintSlideChance -= Time.deltaTime;
                if (_sprintSlideChance <= 0f)
                {
                    _sprintSlideChance = Random.Range(3f, 7f);
                    StartSprintSlide(0.6f);
                }
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
                _isCrouching = false;
                if (_cc != null) { _cc.height = STAND_HEIGHT; _cc.center = new Vector3(0, STAND_CENTER_Y, 0); }
                if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", false);
                if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", false);
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

                // Slight pull into ladder surface to stay attached (increased 0.3 → 0.8)
                if (_ladderFaceDir.sqrMagnitude > 0.01f)
                    move += _ladderFaceDir * 0.8f;

                // Face into the ladder
                LookAtDirection(_ladderFaceDir);
            }
            else if (_ladderDismountTimer > 0f && _ladderFaceDir.sqrMagnitude > 0.01f)
            {
                // Push OVER the top of the ladder — forward (into ladder) + up.
                // Stronger forward push (0.7 → 1.0 sprint) + higher lift (3 → 5) so the bot
                // reliably clears the top lip and lands on the platform.
                move = _ladderFaceDir * _sprintSpeed + Vector3.up * 5f;
                _intentionalJumpTimer = 0.35f;
            }
            else
            {
                move = dir * targetSpeed * _currentHorizInput + forceComponent;
                move.y = _verticalVelocity;
            }
            // FINAL SAFETY: if grounded and not in an intentional jump, check for void ahead
            // This is the last line of defense — catches anything the earlier checks missed
            if (grounded && _intentionalJumpTimer <= 0f && !_onLadder && _ladderDismountTimer <= 0f)
            {
                Vector3 horizMove = new Vector3(move.x, 0, move.z);
                if (horizMove.sqrMagnitude > 0.01f)
                {
                    Vector3 nextPos = transform.position + horizMove.normalized * 0.8f;
                    if (!Physics.Raycast(nextPos + Vector3.up * 2f, Vector3.down, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    {
                        // No ground 0.8m ahead — kill horizontal movement
                        move.x = 0f;
                        move.z = 0f;
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
                    // Close in H and V — arrived, settle.
                    if (_cc != null && _cc.enabled && !_movedThisFrame)
                    {
                        _cc.Move(new Vector3(0f, _verticalVelocity * Time.deltaTime, 0f));
                        _movedThisFrame = true;
                    }
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

            // Shared reactive steering — same logic as MoveToward's Phase 2
            ReactiveSteer(ref dir, ref jumped, target, WALL_MASK);

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
            if (_intentionalJumpTimer > 0f && !grounded) _currentHorizInput = 1f;
            else _currentHorizInput = Mathf.Lerp(_currentHorizInput, 1f, _acceleration * Time.deltaTime);

            Vector3 move = dir * targetSpeed * _currentHorizInput;
            move.y = _verticalVelocity;

            // FINAL SAFETY: void check before move
            if (grounded && _intentionalJumpTimer <= 0f && !_onLadder)
            {
                Vector3 hm = new Vector3(move.x, 0, move.z);
                if (hm.sqrMagnitude > 0.01f)
                {
                    Vector3 np = transform.position + hm.normalized * 0.8f;
                    if (!Physics.Raycast(np + Vector3.up * 2f, Vector3.down, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore))
                    { move.x = 0f; move.z = 0f; }
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

        private Collider FindNearbyLadder(float radius)
        {
            // Skip entirely if we already scanned and found no ladders on this map
            if (_mapLadderScanned && !_mapHasLadders) return null;

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

            // Method 2: search by name — ladder objects often have "ladder" in their name
            if (best == null)
            {
                foreach (var col in Object.FindObjectsOfType<Collider>())
                {
                    if (col == null) continue;
                    string name = col.gameObject.name.ToLower();
                    string tag = "";
                    try { tag = col.tag; } catch { }

                    bool isLadder = tag == "Ladder/Metal" || tag == "Ladder/Chain"
                        || name.Contains("ladder");

                    if (!isLadder) continue;

                    float d = Vector3.Distance(transform.position, col.ClosestPoint(transform.position));
                    if (d < radius && d < bestDist) { bestDist = d; best = col; }
                }
            }

            if (best != null)
            {
                _cachedLadder = best;
                _mapHasLadders = true;
                _mapLadderScanned = true;
                Plugin.Log.LogInfo($"[{BotName}] Found ladder: {best.gameObject.name} tag={best.tag} layer={best.gameObject.layer} dist={bestDist:F1}");
            }
            else if (radius >= 50f)
            {
                // Large radius search found nothing — mark map as ladderless
                _mapHasLadders = false;
                _mapLadderScanned = true;
                Plugin.Log.LogInfo($"[{BotName}] No ladders found on this map");
            }

            return best;
        }

        /// <summary>Reset ladder cache on scene change.</summary>
        public static void ResetLadderCache()
        {
            _mapHasLadders = true;
            _mapLadderScanned = false;
            _cachedLadder = null;
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
        private bool IsEdgeAhead(Vector3 dir, float checkDist)
        {
            if (_onLadder || _nearLadder || _ladderDismountTimer > 0f) return false;
            Vector3 checkPos = transform.position + dir * checkDist;
            // Cast from 2.5m above bot — on ramps the ground ahead is HIGHER than the bot,
            // so a low origin (0.2m) misses the ramp surface entirely → false "edge" detection.
            // 2.5m covers slopes up to ~60° at 1.5m check distance.
            return !Physics.Raycast(checkPos + Vector3.up * 2.5f, Vector3.down, 5f, GROUND_MASK, QueryTriggerInteraction.Ignore);
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

            if (_cc != null) { _cc.height = SLIDE_HEIGHT; _cc.center = new Vector3(0, SLIDE_CENTER_Y, 0); }
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
                _cc.height = STAND_HEIGHT;
                _cc.center = new Vector3(0, STAND_CENTER_Y, 0);
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
        private void ReactiveSteer(ref Vector3 dir, ref bool jumped, Vector3 target, int wallMask)
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
            }

            // Proactive slide: detect low ceilings and crawl spaces
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && !_isSliding && !_onLadder
                && _intentionalJumpTimer <= 0f)
            {
                var obs = CheckObstructions(dir);
                bool shouldSlide = false;
                if (obs.CrouchClear && obs.HeadBlocked && !obs.WaistBlocked)
                    shouldSlide = true;
                else if (obs.CrouchClear && obs.WaistBlocked && _stuckTimer > 0.3f)
                    shouldSlide = true;
                if (shouldSlide)
                {
                    InitSlide(dir);
                    _stuckTimer = 0f;
                }
            }

            // Edge detection: check for gaps ahead, jump if target is across
            if (!zoneLaunched && !commitActive && !jumped && _cc.isGrounded && _intentionalJumpTimer <= 0f && !_onLadder)
            {
                if (IsEdgeAhead(dir, 1f))
                {
                    Vector3 toTarget = target - transform.position;
                    float hDist = new Vector3(toTarget.x, 0, toTarget.z).magnitude;
                    if (hDist > 1f && hDist < Plugin.GetMaxJumpDist())
                    {
                        Vector3 mid = transform.position + dir * (hDist * 0.5f) + Vector3.up * 0.5f;
                        if (!Physics.Raycast(mid, Vector3.down, 3f, GROUND_MASK, QueryTriggerInteraction.Ignore))
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
                    dir = -dir;
            }
        }

        // AvoidWalls removed — wall avoidance now handled by CC collision feedback in MoveToward

        private float _wanderChangeTimer;

        private bool _nearLadder; // Ladder within 2m — suppresses jump/wall slide/edge detection

        private void HandleLadder()
        {
            // WATCHDOG: if we claim to be on a ladder but haven't actually touched one in >0.5s,
            // force-clear the state. Without this, a stuck _onLadder=true causes ApplyGravity to
            // early-return and _verticalVelocity stays at _ladderSpeed — bot flies into the sky.
            if (_onLadder && Time.time - _lastLadderTouchTime > 0.5f)
            {
                _onLadder = false;
                _ladderStuckTimer = 0f;
                _ladderClimbTimer = 0f;
                _ladderFaceDirPinned = false;
                _verticalVelocity = Mathf.Min(_verticalVelocity, 0f);
                Plugin.Log.LogInfo($"[{BotName}] Ladder watchdog — no ladder touched >0.5s, force-cleared stuck state");
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
                    if (deltaY < 0.2f)
                    {
                        _onLadder = false;
                        _ladderDismountTimer = 0.7f;
                        _ladderStuckTimer = 0f;
                        _ladderClimbTimer = 0f;
                        _ladderFaceDirPinned = false;
                        _verticalVelocity = _jumpForce * 0.5f; // bump outward
                        Plugin.Log.LogInfo($"[{BotName}] Ladder freeze watchdog — no Y progress, dismounting");
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
                int colCount = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 0.5f, _overlapBuffer, _ladderLayer);
                for (int ci = 0; ci < colCount; ci++)
                {
                    var c = _overlapBuffer[ci];
                    touching = true;
                    float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                    if (d < closestDist) { closestDist = d; closestLadder = c; }
                }
            }

            // Tag fallback (same radius as FPC)
            if (!touching)
            {
                int tagCount = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 0.6f, _overlapBuffer, ~0, QueryTriggerInteraction.Collide);
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
                            frontSide = faceDot < -0.2f || moveDot > 0.5f;
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
                                _ladderFaceDir = rayDir;
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
                    if (_ladderClimbTimer > 10f)
                        ceilingBlocked = true; // Force dismount

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
                                        badNode.Confidence = -1f;
                                        Plugin.Log.LogInfo($"[{BotName}] Removed bad ladder node {badNode.Id}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            _onLadder = false;
                            _verticalVelocity = -2f;
                            _ladderDismountTimer = 0.5f;
                            _ladderClimbTimer = 0f;
                        }
                    }
                    else
                    {
                        _onLadder = true;
                        _verticalVelocity = _ladderSpeed;
                        _coyoteTimer = 0.15f;

                        Vector3 ladderCenter = closestLadder.bounds.center;
                        _lastLadderPos = ladderCenter;

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
                _onLadder = false;
                _ladderStuckTimer = 0f;
                _ladderClimbTimer = 0f;

                // Dismount detection: was climbing, now off ladder — push AWAY from ladder
                if (_wasOnLadder && _ladderFaceDir.sqrMagnitude > 0.01f)
                {
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
            }

            // --- Defaults ---
            if (force <= 0f) force = _jumpForce;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;
            direction.y = 0f;
            direction.Normalize();

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

            // Graph jump edge: set up trajectory replay
            _currentJumpEdge = jumpEdge;
            if (jumpEdge != null && jumpEdge.AirSampleCount > 2)
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
            if (_trajActive && _currentJumpEdge != null) return; // Trajectory replay controls vertical velocity

            // Hard cap on upward velocity — nothing in Straftat should send bots above jump impulse.
            // If external forces push above 2× jumpForce, clamp (zones can still launch normally).
            if (_verticalVelocity > _jumpForce * 2.5f && _zoneForceDuration <= 0f)
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
