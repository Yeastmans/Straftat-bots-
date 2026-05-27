using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    public partial class NavGraph
    {
        // Edges flagged as "demo needed" — bots have failed them enough times that a
        // player demonstration would be worth more than continued bot thrashing.
        // Cleared when an edge gets a successful traversal (see ReportSuccess).
        // Keyed as packed (from<<32)|to ints (as long).
        private readonly HashSet<long> _demoNeededEdges = new HashSet<long>();
        private const int DEMO_NEEDED_FAIL_THRESHOLD = 3;
        public int DemoNeededCount => _demoNeededEdges.Count;
        public bool IsDemoNeededEdge(int fromId, int toId)
            => _demoNeededEdges.Contains(((long)fromId << 32) | (uint)toId);

        /// <summary>
        /// For UI: iterate all demo-needed edge endpoints as (fromPos, toPos) pairs.
        /// </summary>
        public IEnumerable<(Vector3 from, Vector3 to)> DemoNeededEdgePositions()
        {
            foreach (long packed in _demoNeededEdges)
            {
                int f = (int)(packed >> 32);
                int t = (int)(packed & 0xFFFFFFFF);
                var fn = GetNodeById(f);
                var tn = GetNodeById(t);
                if (fn != null && tn != null) yield return (fn.Position, tn.Position);
            }
        }

        private static int PopCount(uint x)
        {
            // Hamming-weight — counts distinct bot IDs that failed an edge.
            x = x - ((x >> 1) & 0x55555555u);
            x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
            return (int)((((x + (x >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }

        // ========== CONFIDENCE / FEEDBACK ==========

        /// <summary>
        /// Bot died on this edge from environment (fell off, walked into hazard).
        /// Works in both Training and Play modes.
        /// </summary>
        public void ReportEnvironmentalDeath(Vector3 deathPos, Vector3 lastSafePos)
        {
            if (IsLocked) return;
            var nearDeath = FindNodesInRadius(deathPos, 3f);

            int edgeCount = Edges.Count;
            foreach (var node in nearDeath)
            {
                for (int i = edgeCount - 1; i >= 0; i--)
                {
                    if (Edges[i].To == node.Id && Edges[i].Confidence > 0)
                    {
                        // Check if this edge connects two player-sourced nodes — never penalize
                        var fromNode = GetNodeById(Edges[i].From);
                        if (fromNode != null && fromNode.PlayerSourced && node.PlayerSourced)
                        {
                            Edges[i].FailCount++; // Track failures but don't reduce confidence
                            continue;
                        }

                        Edges[i].FailCount++;

                        // Jump/Slide/WallJump: soft penalty at first, delete after repeated deaths
                        if ((Edges[i].Type == EdgeType.Jump || Edges[i].Type == EdgeType.Slide || Edges[i].Type == EdgeType.WallJump)
                            && Edges[i].FailCount < 4)
                        {
                            Edges[i].Confidence -= CONFIDENCE_DEATH_PENALTY * 0.3f;
                        }
                        else
                        {
                            Edges[i].Confidence -= CONFIDENCE_DEATH_PENALTY;
                            // ALL edge types can be deleted after enough deaths — including jump/slide
                            // Edges with trajectory data get more chances (proven player jumps)
                            int deleteThreshold = (Edges[i].AirSampleCount > 2) ? 8 : 5;
                            if (Edges[i].Confidence <= CONFIDENCE_DELETE_THRESHOLD && Edges[i].FailCount >= deleteThreshold)
                                Edges[i].Confidence = -1f;
                        }
                    }
                }

                // Player-sourced nodes: very tiny penalty, need massive failures to degrade
                if (node.PlayerSourced)
                    node.Confidence = Mathf.Max(0.2f, node.Confidence - CONFIDENCE_DEATH_PENALTY * 0.05f);
                else
                    node.Confidence = Mathf.Max(0.05f, node.Confidence - CONFIDENCE_DEATH_PENALTY);
            }
            _dirty = true;
        }

        /// <summary>
        /// Player or bot died from falling off a ledge. Penalizes Fall edges
        /// near the takeoff position so bots learn this drop-off is lethal.
        /// </summary>
        public void ReportFallDeath(Vector3 takeoffPos, Vector3 deathPos)
        {
            if (IsLocked) return;
            var nearTakeoff = FindNodesInRadius(takeoffPos, 4f);
            bool penalized = false;

            foreach (var node in nearTakeoff)
            {
                if (!_edgesByFrom.TryGetValue(node.Id, out var edgeIndices)) continue;
                foreach (int ei in edgeIndices)
                {
                    if (ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge.Confidence <= 0) continue;

                    // Only target Fall edges and Walk edges that go toward the death position
                    bool isFallEdge = edge.Type == EdgeType.Fall;
                    bool isWalkTowardDeath = false;
                    if (edge.Type == EdgeType.Walk)
                    {
                        var toNode = GetNodeById(edge.To);
                        if (toNode != null)
                        {
                            Vector3 edgeDir = (toNode.Position - node.Position).normalized;
                            Vector3 deathDir = (deathPos - node.Position).normalized;
                            if (Vector3.Dot(edgeDir, deathDir) > 0.6f)
                                isWalkTowardDeath = true;
                        }
                    }

                    if (!isFallEdge && !isWalkTowardDeath) continue;

                    edge.FailCount += 3; // Heavy failure weight for death

                    // Player-sourced edges: gentle penalty
                    var fromN = GetNodeById(edge.From);
                    var toN = GetNodeById(edge.To);
                    bool playerEdge = fromN != null && toN != null && fromN.PlayerSourced && toN.PlayerSourced;

                    if (playerEdge)
                    {
                        if (edge.FailCount > 6)
                            edge.Confidence = Mathf.Max(0.1f, edge.Confidence - CONFIDENCE_DEATH_PENALTY * 0.3f);
                    }
                    else
                    {
                        edge.Confidence -= CONFIDENCE_DEATH_PENALTY * 1.5f;
                        if (edge.Confidence <= CONFIDENCE_DELETE_THRESHOLD)
                            edge.Confidence = -1f; // Mark for deletion
                    }
                    penalized = true;
                }
            }

            if (penalized)
            {
                Plugin.Log.LogInfo($"[NavGraph] Fall death: penalized edges near takeoff {takeoffPos} (death at {deathPos})");
                _dirty = true;
            }
        }

        /// <summary>
        /// Bot detected a wall between two nodes at runtime.
        /// Heavily penalizes the edge — if confirmed through a wall, delete it.
        /// Also revalidates with raycast to be sure.
        /// </summary>
        public void ReportWallEdge(int fromId, int toId)
        {
            if (IsLocked) return;
            // Don't delete patrol-protected edges unless they've failed many times
            if (IsPatrolProtected(fromId) && IsPatrolProtected(toId)) return;
            if (!_edgesByFrom.TryGetValue(fromId, out var edgeIndices)) return;

            var fromNode = GetNodeById(fromId);
            var toNode = GetNodeById(toId);
            if (fromNode == null || toNode == null) return;

            // Double-check with our own raycast validation
            if (ValidateLineOfSight(fromNode.Position, toNode.Position))
                return; // Actually clear — bot may have been at a weird angle

            foreach (int ei in edgeIndices)
            {
                if (ei >= Edges.Count) continue;
                var edge = Edges[ei];
                if (edge.To != toId || edge.Confidence <= 0) continue;

                // Confirmed wall — heavy penalty
                edge.FailCount += 5;
                bool playerEdge = fromNode.PlayerSourced && toNode.PlayerSourced;

                if (playerEdge)
                {
                    // Player edges: reduce confidence but don't delete (player walked here somehow)
                    edge.Confidence = Mathf.Max(0.05f, edge.Confidence - 0.3f);
                }
                else
                {
                    // Bot edge through wall — delete it
                    edge.Confidence = -1f;
                }

                Plugin.Log.LogInfo($"[NavGraph] Wall edge: {fromId}->{toId} (player={playerEdge}, conf={edge.Confidence:F2})");
                _dirty = true;
                break;
            }

            // Also check reverse direction
            if (_edgesByFrom.TryGetValue(toId, out var revEdges))
            {
                foreach (int ei in revEdges)
                {
                    if (ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge.To != fromId || edge.Confidence <= 0) continue;

                    bool playerEdge = fromNode.PlayerSourced && toNode.PlayerSourced;
                    edge.FailCount += 5;
                    if (playerEdge)
                        edge.Confidence = Mathf.Max(0.05f, edge.Confidence - 0.3f);
                    else
                        edge.Confidence = -1f;
                    break;
                }
            }
        }

        /// <summary>
        /// Bot got stuck near this position. Works in both modes.
        /// </summary>
        public void ReportStuck(Vector3 pos, Vector3 intendedDir)
        {
            if (IsLocked) return;
            var nearNode = FindNearestNode(pos, 2f);
            if (nearNode == null) return;

            if (_edgesByFrom.TryGetValue(nearNode.Id, out var edgeIndices))
            {
                var indices = new List<int>(edgeIndices);
                foreach (int ei in indices)
                {
                    if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                    var edge = Edges[ei];
                    var toNode = GetNodeById(edge.To);
                    if (toNode == null) continue;

                    // Player-sourced edges: tiny penalty, need massive failures to degrade
                    bool playerEdge = nearNode.PlayerSourced && toNode.PlayerSourced;

                    Vector3 edgeDir = (toNode.Position - nearNode.Position).normalized;
                    if (Vector3.Dot(edgeDir, intendedDir.normalized) > 0.5f)
                    {
                        edge.FailCount++;

                        // Well-traveled edges (5+ successes) are confirmed working — tiny penalty
                        if (playerEdge || edge.SuccessCount >= 5)
                        {
                            if (edge.FailCount > 10)
                                edge.Confidence -= CONFIDENCE_STUCK_PENALTY * 0.05f;
                        }
                        else if (edge.Type == EdgeType.Jump || edge.Type == EdgeType.Slide || edge.Type == EdgeType.WallJump)
                        {
                            if (edge.FailCount > 5)
                                edge.Confidence -= CONFIDENCE_STUCK_PENALTY * 0.3f;
                        }
                        else
                        {
                            edge.Confidence -= CONFIDENCE_STUCK_PENALTY;
                        }

                        // Never delete confirmed, player, jump/slide edges
                        if (edge.Confidence <= CONFIDENCE_DELETE_THRESHOLD &&
                            !playerEdge && edge.SuccessCount < 5 &&
                            edge.Type != EdgeType.Jump && edge.Type != EdgeType.Slide)
                            edge.Confidence = -1f;
                    }
                }
            }
            _dirty = true;
        }

        /// <summary>
        /// Bot/player successfully traversed this edge. Works in both modes.
        /// Rehabilitates low-confidence nodes/edges — successful traversal proves
        /// the path is viable, so boost confidence and clear blacklist.
        /// isPlayer gives stronger boost.
        /// </summary>
        public void ReportSuccess(int fromNodeId, int toNodeId, bool isPlayer = false)
        {
            if (IsLocked) return;
            float boost = isPlayer ? CONFIDENCE_PLAYER_BOOST : CONFIDENCE_SUCCESS_BOOST;

            if (_edgesByFrom.TryGetValue(fromNodeId, out var edgeIndices))
            {
                var indices = new List<int>(edgeIndices);
                foreach (int ei in indices)
                {
                    if (ei < Edges.Count && Edges[ei].To == toNodeId)
                    {
                        Edges[ei].SuccessCount++;

                        // Jump/Slide edges get massive boost on success — proven traversable
                        float edgeBoost = boost;
                        if (Edges[ei].Type == EdgeType.Jump || Edges[ei].Type == EdgeType.Slide || Edges[ei].Type == EdgeType.WallJump)
                            edgeBoost = Mathf.Max(boost, 0.4f);

                        Edges[ei].Confidence = Mathf.Min(1f, Edges[ei].Confidence + edgeBoost);

                        // Reset fail count on success
                        if (Edges[ei].SuccessCount > Edges[ei].FailCount)
                            Edges[ei].FailCount = 0;

                        // Clear the variant-tried mask on success — next fail round starts fresh
                        // with all 4 approach variants available again. This keeps the retry
                        // system from permanently locking out an edge that's only flaky.
                        Edges[ei].TriedVariants = 0;
                        Edges[ei].CurrentVariant = 0;

                        // Clear multi-bot fail mask + demo-needed marker — proven viable.
                        Edges[ei].FailedBotMask = 0u;
                        _demoNeededEdges.Remove(((long)fromNodeId << 32) | (uint)toNodeId);

                        // Downgrade Jump back to Walk after 5 walk successes with no falls
                        // The jump was a false positive — walking works fine here
                        if (Edges[ei].Type == EdgeType.Jump && Edges[ei].SuccessCount >= 5
                            && Edges[ei].FailCount == 0)
                        {
                            Edges[ei].Type = EdgeType.Walk;
                            Plugin.Log.LogInfo($"[NavGraph] Jump edge {fromNodeId}->{toNodeId} downgraded to Walk (5 walk successes)");
                        }

                        break;
                    }
                }
            }

            var toNode = GetNodeById(toNodeId);
            if (toNode != null)
            {
                toNode.VisitCount++;
                toNode.Confidence = Mathf.Min(1f, toNode.Confidence + boost);
                toNode.LastVisitTime = Time.time;
                if (isPlayer) toNode.PlayerSourced = true;
                _tempBlacklist.Remove(toNodeId);
                _blacklistStrikes.Remove(toNodeId);
            }

            var fromNode = GetNodeById(fromNodeId);
            if (fromNode != null)
            {
                fromNode.Confidence = Mathf.Min(1f, fromNode.Confidence + boost * 0.5f);
                fromNode.LastVisitTime = Time.time;
                if (isPlayer) fromNode.PlayerSourced = true;
            }

            _dirty = true;
        }

        /// <summary>
        /// Check if there's a slide edge near pos pointing roughly in moveDir.
        /// Returns the slide direction if found, or Vector3.zero if not.
        /// </summary>
        public Vector3 FindNearbySlideDirection(Vector3 pos, Vector3 moveDir, float radius = 3f)
        {
            var nearby = FindNodesInRadius(pos, radius);
            foreach (var node in nearby)
            {
                if (_edgesByFrom.TryGetValue(node.Id, out var edgeIndices))
                {
                    foreach (int ei in edgeIndices)
                    {
                        if (ei >= Edges.Count) continue;
                        var edge = Edges[ei];
                        if (edge.Type != EdgeType.Slide || edge.Confidence <= 0) continue;

                        var toNode = GetNodeById(edge.To);
                        if (toNode == null) continue;

                        Vector3 edgeDir = (toNode.Position - node.Position);
                        edgeDir.y = 0;
                        if (edgeDir.sqrMagnitude < 0.1f) continue;
                        edgeDir.Normalize();

                        if (Vector3.Dot(edgeDir, moveDir.normalized) > 0.5f)
                            return edgeDir;
                    }
                }
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Ensure an edge exists between two consecutive traversal nodes (player or bot).
        /// Classifies as Walk vs Jump using slope ratio, NOT raw height delta —
        /// so long ramps with moderate elevation stay Walk instead of bogus Jump edges.
        /// </summary>
        public void EnsurePlayerEdge(int fromId, int toId)
        {
            // Check if edge already exists
            if (_edgesByFrom.TryGetValue(fromId, out var edgeIndices))
            {
                foreach (int ei in edgeIndices)
                {
                    if (ei < Edges.Count && Edges[ei].To == toId && Edges[ei].Confidence > 0)
                        return; // Edge exists
                }
            }

            // Create edge — classify by slope ratio (rise/run), not raw height.
            // A 10m ramp dropping 2m is walkable (ratio 0.2). A 1m horizontal jump
            // to a 1.5m-higher ledge is a real jump (ratio 1.5).
            var fromNode = GetNodeById(fromId);
            var toNode = GetNodeById(toId);
            if (fromNode == null || toNode == null) return;

            float dist = Vector3.Distance(fromNode.Position, toNode.Position);
            float heightDiff = toNode.Position.y - fromNode.Position.y;

            // Horizontal run (ignores vertical)
            Vector3 horiz = toNode.Position - fromNode.Position;
            horiz.y = 0f;
            float run = horiz.magnitude;

            EdgeType type = EdgeType.Walk;
            // Classify as Jump only when slope is steeper than ~40° (ratio 0.84)
            // OR when height gain exceeds 2m AND run is tight (< 2*rise — can't walk up)
            if (run > 0.01f)
            {
                float ratio = Mathf.Abs(heightDiff) / run;
                bool steep = ratio > 0.84f;
                bool bigUpTightRun = heightDiff > 2f && run < heightDiff * 2f;
                if (steep || bigUpTightRun) type = EdgeType.Jump;
            }
            else if (Mathf.Abs(heightDiff) > 0.3f)
            {
                // Pure-vertical stack (shouldn't happen for walked nodes, but be safe)
                type = EdgeType.Jump;
            }

            // Bidirectional — player/bot traversed both ways implicitly. Force = bypass Play mode block.
            var fwd = AddEdge(fromId, toId, type, dist, force: true);
            var rev = AddEdge(toId, fromId, type, dist, force: true);

            // Mark high confidence — traversal proves this works
            if (fwd != null) fwd.Confidence = 1f;
            if (rev != null) rev.Confidence = 1f;

            _dirty = true;
        }

        /// <summary>
        /// Find the longest connected chain of player-sourced nodes that gets closest to target.
        /// Returns a path of connected player nodes, or empty if none.
        /// </summary>
        public List<NavNode> FindPlayerChainToTarget(Vector3 startPos, Vector3 targetPos, float searchRadius = 15f)
        {
            var startNode = FindNearestNode(startPos, searchRadius);
            if (startNode == null) return new List<NavNode>();

            // BFS along player-sourced nodes using parent-chain (no path copying)
            var visited = new HashSet<int>();
            var parent = new Dictionary<int, int>(); // child -> parent
            int bestNodeId = startNode.Id;
            float bestDist = Vector3.Distance(startNode.Position, targetPos);

            var queue = new Queue<int>();
            queue.Enqueue(startNode.Id);
            visited.Add(startNode.Id);

            int iterations = 0;
            while (queue.Count > 0 && iterations++ < 5000)
            {
                int currentId = queue.Dequeue();
                var current = GetNodeById(currentId);
                if (current == null) continue;

                float distToTarget = Vector3.Distance(current.Position, targetPos);
                if (distToTarget < bestDist)
                {
                    bestDist = distToTarget;
                    bestNodeId = currentId;
                }
                if (distToTarget < 3f) break;

                if (_edgesByFrom.TryGetValue(currentId, out var edges))
                {
                    foreach (int ei in edges)
                    {
                        if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                        int toId = Edges[ei].To;
                        if (visited.Contains(toId)) continue;
                        var toNode = GetNodeById(toId);
                        if (toNode == null || !toNode.PlayerSourced) continue;

                        visited.Add(toId);
                        parent[toId] = currentId;
                        queue.Enqueue(toId);
                    }
                }
            }

            // Reconstruct path from best node back to start
            var path = new List<NavNode>();
            int c = bestNodeId;
            while (true)
            {
                var node = GetNodeById(c);
                if (node != null) path.Add(node);
                if (!parent.ContainsKey(c)) break;
                c = parent[c];
            }
            path.Reverse();
            return StraightenPath(path);
        }

        /// <summary>
        /// Force a specific edge to become a Jump edge immediately.
        /// Used in POI mode when bots die — no waiting for 3 falls.
        /// </summary>
        public void ForceJumpEdge(int fromNodeId, int toNodeId)
        {
            if (IsLocked) return;
            var fromNode = GetNodeById(fromNodeId);
            var toNode = GetNodeById(toNodeId);
            if (fromNode != null && toNode != null)
            {
                // Don't upgrade to Jump if the path is walkable
                if (ValidateEdgeGround(fromNode.Position, toNode.Position)
                    && ValidateLineOfSight(fromNode.Position, toNode.Position))
                    return;

                float heightDiff = Mathf.Abs(toNode.Position.y - fromNode.Position.y);
                float horizDist = new Vector3(toNode.Position.x - fromNode.Position.x, 0,
                    toNode.Position.z - fromNode.Position.z).magnitude;
                if ((heightDiff > 3f && horizDist < 1f) || heightDiff > 5f)
                {
                    // Impossible jump — delete the edge instead
                    if (_edgesByFrom.TryGetValue(fromNodeId, out var delEdges))
                    {
                        foreach (int ei in delEdges)
                        {
                            if (ei < Edges.Count && Edges[ei].To == toNodeId)
                            { Edges[ei].Confidence = -1f; _dirty = true; break; }
                        }
                    }
                    return;
                }
            }

            if (_edgesByFrom.TryGetValue(fromNodeId, out var edgeIndices))
            {
                var indices = new List<int>(edgeIndices);
                foreach (int ei in indices)
                {
                    if (ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge.To != toNodeId) continue;
                    if (edge.Type == EdgeType.Walk)
                    {
                        edge.Type = EdgeType.Jump;
                        edge.Confidence = Mathf.Max(edge.Confidence, 0.7f);
                        Plugin.Log.LogInfo($"[NavGraph] Force-upgraded edge {fromNodeId}->{toNodeId} to Jump");
                        _dirty = true;
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Upgrade all walk edges near a position to Jump — bots keep dying here,
        /// clearly need to jump across.
        /// </summary>
        public void UpgradeNearbyWalkEdgesToJump(Vector3 pos, float radius)
        {
            var nearby = FindNodesInRadius(pos, radius);
            int upgraded = 0;
            foreach (var node in nearby)
            {
                if (_edgesByFrom.TryGetValue(node.Id, out var edgeIndices))
                {
                    foreach (int ei in edgeIndices)
                    {
                        if (ei >= Edges.Count) continue;
                        var edge = Edges[ei];
                        if (edge.Type != EdgeType.Walk || edge.Confidence <= 0) continue;

                        // Only upgrade edges that cross a gap (no ground between)
                        var toNode = GetNodeById(edge.To);
                        if (toNode == null) continue;
                        if (!ValidateEdgeGround(node.Position, toNode.Position))
                        {
                            edge.Type = EdgeType.Jump;
                            edge.Confidence = Mathf.Max(edge.Confidence, 0.6f);
                            upgraded++;
                        }
                    }
                }
            }
            if (upgraded > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Upgraded {upgraded} walk edges to jump near {pos}");
                _dirty = true;
            }
        }

        /// <summary>
        /// Bot fell while traversing a walk edge. Track falls and auto-upgrade to Jump after repeated failures.
        /// </summary>
        public void ReportFallOnEdge(int fromNodeId, int toNodeId) => ReportFallOnEdge(fromNodeId, toNodeId, -1);

        /// <summary>
        /// Bot fell while traversing an edge. BotId enables multi-bot fail consensus —
        /// an edge only dies from falls when ≥2 distinct bots have failed it, so a
        /// single looping bot can't murder a legitimate edge. 10-fail override
        /// preserves single-bot training viability. Pass botId = -1 for anonymous
        /// callers (legacy paths / environmental deaths).
        /// </summary>
        public void ReportFallOnEdge(int fromNodeId, int toNodeId, int botId)
        {
            if (IsLocked) return;
            if (_edgesByFrom.TryGetValue(fromNodeId, out var edgeIndices))
            {
                var indices = new List<int>(edgeIndices);
                foreach (int ei in indices)
                {
                    if (ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge.To != toNodeId) continue;

                    edge.FailCount++;
                    if (botId >= 0 && botId < 32) edge.FailedBotMask |= (1u << botId);

                    // Demo-needed marker: at 3 fails, flag this edge for a player demonstration.
                    // Cheaper than continued bot thrashing + enables the collab UI prompt.
                    if (edge.FailCount >= DEMO_NEEDED_FAIL_THRESHOLD && edge.SuccessCount == 0)
                    {
                        long packed = ((long)fromNodeId << 32) | (uint)toNodeId;
                        if (_demoNeededEdges.Add(packed))
                            Plugin.Log.LogInfo($"[NavGraph] Demo needed: edge {fromNodeId}->{toNodeId} ({edge.FailCount} fails across {PopCount(edge.FailedBotMask)} bots)");
                    }

                    // Check if this edge goes through a wall — if so, delete it immediately
                    var fromNode = GetNodeById(fromNodeId);
                    var toNode = GetNodeById(toNodeId);
                    if (fromNode != null && toNode != null &&
                        !ValidateLineOfSight(fromNode.Position, toNode.Position))
                    {
                        edge.Confidence = -1f;
                        Plugin.Log.LogInfo($"[NavGraph] Removed edge {fromNodeId}->{toNodeId} (through wall)");
                        _dirty = true;
                        break;
                    }

                    if (edge.Type == EdgeType.Walk)
                    {
                        // Walk edge: upgrade to Jump after 2 falls, or delete after consensus.
                        // Multi-bot gate: require ≥2 distinct bots have failed, OR ≥10 total
                        // fails (single-bot escape hatch).
                        int walkDistinctBots = PopCount(edge.FailedBotMask);
                        bool walkConsensus = walkDistinctBots >= 2 || edge.FailCount >= 10;
                        if (edge.FailCount >= 5 && walkConsensus)
                        {
                            edge.Confidence = -1f;
                            Plugin.Log.LogInfo($"[NavGraph] Removed walk edge {fromNodeId}->{toNodeId} ({edge.FailCount} falls, {walkDistinctBots} distinct bots)");
                        }
                        else if (edge.FailCount >= 2
                            && fromNode != null && toNode != null
                            && !ValidateEdgeGround(fromNode.Position, toNode.Position))
                        {
                            edge.Type = EdgeType.Jump;
                            edge.Confidence = Mathf.Max(edge.Confidence, 0.6f);
                            Plugin.Log.LogInfo($"[NavGraph] Walk->Jump {fromNodeId}->{toNodeId} ({edge.FailCount} falls)");
                        }
                    }
                    else if (edge.Type == EdgeType.Jump || edge.Type == EdgeType.Fall)
                    {
                        // RETRY-WITH-VARIANCE: mark the approach variant the bot just used as
                        // "tried", then rotate to the next variant. Only when all 4 have failed
                        // do we even consider deletion — each fail here is one out of 4 attempts,
                        // so "really failed" means failed all approach styles.
                        edge.TriedVariants |= (byte)(1 << (edge.CurrentVariant & 0x03));
                        edge.CurrentVariant = (byte)((edge.CurrentVariant + 1) & 0x03);
                        bool allVariantsTried = (edge.TriedVariants & 0x0F) == 0x0F;

                        // Look for a shorter alternative landing — still useful even with variants.
                        if (edge.FailCount >= 4 && fromNode != null && toNode != null)
                        {
                            float origDist = Vector3.Distance(fromNode.Position, toNode.Position);
                            var nearLanding = FindNodesInRadius(toNode.Position, origDist * 0.5f);
                            bool foundBetter = false;
                            foreach (var candidate in nearLanding)
                            {
                                if (candidate.Id == fromNodeId || candidate.Id == toNodeId) continue;
                                float newDist = Vector3.Distance(fromNode.Position, candidate.Position);
                                if (newDist < origDist * 0.7f && newDist > 1f)
                                {
                                    AddEdge(fromNodeId, candidate.Id, EdgeType.Jump, newDist, force: true);
                                    Plugin.Log.LogInfo($"[NavGraph] Created shorter jump {fromNodeId}->{candidate.Id} (dist={newDist:F1} vs {origDist:F1})");
                                    foundBetter = true;
                                    break;
                                }
                            }

                            // Aggressive prune: 5 fails with no successes AND we already have an alternative landing.
                            // Multi-bot consensus gate: require ≥2 distinct bots failed OR ≥10 raw fails
                            // (single-bot override). Player-sourced endpoints are already skipped upstream.
                            int jumpDistinctBots = PopCount(edge.FailedBotMask);
                            bool jumpConsensus = jumpDistinctBots >= 2 || edge.FailCount >= 10;
                            if (!foundBetter && edge.FailCount >= 5 && edge.SuccessCount == 0 && jumpConsensus)
                            {
                                edge.Confidence = -1f;
                                Plugin.Log.LogInfo($"[NavGraph] Removed impossible jump {fromNodeId}->{toNodeId} ({edge.FailCount} fails, {jumpDistinctBots} distinct bots)");
                            }
                        }

                        // Decay confidence gently — proven jumps that just need more practice are preserved.
                        if (edge.SuccessCount == 0)
                            edge.Confidence = Mathf.Max(edge.Confidence, 0f) * 0.7f; // was 0.5f — gentler decay
                        else
                            edge.Confidence = Mathf.Max(edge.Confidence, 0f) * 0.85f; // was 0.8f
                    }
                    _dirty = true;
                    break;
                }
            }
        }

        /// <summary>
        /// Scan all walk edges and auto-upgrade to Jump if there's no ground between the nodes.
        /// Call after loading or periodically.
        /// </summary>
        public void DetectJumpEdges()
        {
            int upgraded = 0;
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0 || edge.Type != EdgeType.Walk) continue;

                var fromNode = GetNodeById(edge.From);
                var toNode = GetNodeById(edge.To);
                if (fromNode == null || toNode == null) continue;

                // Count how many of 5 sample points have no ground — need at least 2 misses
                // to count as a real gap (1 miss could be a crack or thin geometry)
                int misses = 0;
                for (float t = 0.15f; t <= 0.85f; t += 0.175f)
                {
                    Vector3 sample = Vector3.Lerp(fromNode.Position, toNode.Position, t);
                    if (!Physics.Raycast(sample + Vector3.up * 0.3f, Vector3.down, 2.5f))
                        misses++;
                }

                // Only upgrade if there's a real gap (2+ consecutive misses) and both ends solid
                // AND the path is not actually walkable (slopes have ground but are continuous)
                if (misses >= 2)
                {
                    bool fromGround = Physics.Raycast(fromNode.Position + Vector3.up * 0.3f, Vector3.down, 2.5f);
                    bool toGround = Physics.Raycast(toNode.Position + Vector3.up * 0.3f, Vector3.down, 2.5f);
                    bool walkable = ValidateEdgeGround(fromNode.Position, toNode.Position)
                        && ValidateLineOfSight(fromNode.Position, toNode.Position);

                    if (fromGround && toGround && !walkable)
                    {
                        edge.Type = EdgeType.Jump;
                        edge.Confidence = Mathf.Max(edge.Confidence, 0.6f);
                        upgraded++;
                    }
                }
            }

            if (upgraded > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Auto-detected {upgraded} walk edges as jump edges (gaps between nodes)");
                _dirty = true;
            }
        }

        /// <summary>
        /// Check if there's a jump edge near pos pointing roughly in moveDir.
        /// Returns true if a jump should be attempted.
        /// </summary>
        public bool HasNearbyJumpEdge(Vector3 pos, Vector3 moveDir, float radius = 2f)
        {
            var nearby = FindNodesInRadius(pos, radius);
            foreach (var node in nearby)
            {
                if (_edgesByFrom.TryGetValue(node.Id, out var edgeIndices))
                {
                    foreach (int ei in edgeIndices)
                    {
                        if (ei >= Edges.Count) continue;
                        var edge = Edges[ei];
                        if (edge.Type != EdgeType.Jump || edge.Confidence <= 0) continue;

                        var toNode = GetNodeById(edge.To);
                        if (toNode == null) continue;

                        Vector3 edgeDir = (toNode.Position - node.Position);
                        edgeDir.y = 0;
                        if (edgeDir.sqrMagnitude < 0.1f) continue;

                        if (Vector3.Dot(edgeDir.normalized, moveDir.normalized) > 0.5f)
                            return true;
                    }
                }
            }
            return false;
        }

        // ========== TEMPORARY BLACKLIST ==========

        /// <summary>
        /// Temporarily blacklist nodes near a position. Bot was stuck here — avoid these nodes
        /// and try alternate routes. After repeated blacklists, permanently delete bad nodes.
        /// </summary>
        public void BlacklistNearby(Vector3 pos, float radius = 3f)
        {
            var nearby = FindNodesInRadius(pos, radius);
            float expiry = Time.time + BlacklistDuration;

            foreach (var node in nearby)
            {
                // Temporarily blacklist for pathfinding (even player nodes — bot needs alternate route)
                _tempBlacklist[node.Id] = expiry;

                // But never permanently penalize or delete player-sourced nodes
                if (node.PlayerSourced) continue;

                if (!_blacklistStrikes.ContainsKey(node.Id))
                    _blacklistStrikes[node.Id] = 0;
                _blacklistStrikes[node.Id]++;

                if (_blacklistStrikes[node.Id] >= BLACKLIST_STRIKES_TO_DELETE)
                {
                    node.Confidence = -1f;
                    Plugin.Log.LogInfo($"[NavGraph] Permanently deleted bot node {node.Id} at {node.Position} ({_blacklistStrikes[node.Id]} strikes)");
                    _dirty = true;
                }
                else
                {
                    node.Confidence = Mathf.Max(0.1f, node.Confidence - 0.1f);
                }
            }
        }

        /// <summary>
        /// Check if a node is currently blacklisted. Used in pathfinding to avoid bad areas.
        /// </summary>
        public bool IsBlacklisted(int nodeId)
        {
            if (!_tempBlacklist.TryGetValue(nodeId, out float expiry)) return false;
            if (Time.time > expiry)
            {
                _tempBlacklist.Remove(nodeId);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Clear expired blacklist entries. Called periodically.
        /// </summary>
        public void CleanupBlacklist()
        {
            var expired = new List<int>();
            foreach (var kv in _tempBlacklist)
            {
                if (Time.time > kv.Value) expired.Add(kv.Key);
            }
            foreach (int id in expired)
            {
                _tempBlacklist.Remove(id);
                _blacklistStrikes.Remove(id); // Clean up strikes for expired entries
            }
        }

        /// <summary>
        /// Compress clusters near a position. Called after successful traversals
        /// to merge redundant nodes in well-traveled areas.
        /// </summary>
        /// <summary>
        /// Erase all nodes and their edges within radius of position.
        /// Used by Erase Mode — player walks around to delete bad nodes.
        /// </summary>
        public void EraseNearby(Vector3 pos, float radius)
        {
            if (IsLocked) return;
            var nearby = FindNodesInRadius(pos, radius);
            int removed = 0;
            foreach (var node in nearby)
            {
                if (node.Confidence <= 0) continue;
                node.Confidence = -1f;
                removed++;
            }
            if (removed > 0)
            {
                _dirty = true;
                Plugin.Log.LogInfo($"[NavGraph] Erased {removed} nodes near {pos}");
            }
        }

        /// <summary>
        /// Cache a successful patrol route. Locked in after first success, only cleared after repeated failures.
        /// </summary>
        private static long PatrolRouteKey(Vector3 from, Vector3 to)
        {
            // Key from BOTH start and end weapon — different start weapons have separate routes
            int fx = Mathf.RoundToInt(from.x); int fz = Mathf.RoundToInt(from.z);
            int tx = Mathf.RoundToInt(to.x); int tz = Mathf.RoundToInt(to.z);
            return ((long)(fx & 0xFFFF) << 48) | ((long)(fz & 0xFFFF) << 32)
                 | ((long)(tx & 0xFFFF) << 16) | (long)(tz & 0xFFFF);
        }

        public void CachePatrolRoute(Vector3 from, Vector3 to, List<NavNode> path)
        {
            if (path == null || path.Count < 2) return;
            long key = PatrolRouteKey(from, to);

            // Unprotect nodes from old route that aren't in the new one
            if (_patrolRoutes.TryGetValue(key, out var oldRoute))
            {
                // Collect new route node IDs
                var newIds = new HashSet<int>();
                foreach (var n in path) newIds.Add(n.Id);

                // Collect IDs still used by OTHER routes
                var usedElsewhere = new HashSet<int>();
                foreach (var kvp in _patrolRoutes)
                {
                    if (kvp.Key == key) continue;
                    foreach (var n in kvp.Value) usedElsewhere.Add(n.Id);
                }

                // Unprotect old nodes not in new route and not used by other routes
                int unprotected = 0;
                foreach (var n in oldRoute)
                {
                    if (!newIds.Contains(n.Id) && !usedElsewhere.Contains(n.Id))
                    {
                        _patrolVisitedNodes.Remove(n.Id);
                        unprotected++;
                    }
                }
                if (unprotected > 0)
                    Plugin.Log.LogInfo($"[NavGraph] Unprotected {unprotected} old route nodes (replaced by shorter path)");
            }

            _patrolRoutes[key] = new List<NavNode>(path);
            _patrolRouteFailCounts[key] = 0;

            // Protect all nodes in new route
            foreach (var n in path) _patrolVisitedNodes.Add(n.Id);

            Plugin.Log.LogInfo($"[NavGraph] Saved patrol route to ({to.x:F0},{to.z:F0}) — {path.Count} nodes");
        }

        /// <summary>
        /// Report a patrol route failed at the bot's current position.
        /// Trims the route from the failure point onward — keeps the working portion.
        /// After 25 fails at the same point, clears the entire route.
        /// </summary>
        public void ReportPatrolRouteFail(Vector3 from, Vector3 to, Vector3 failPos)
        {
            long key = PatrolRouteKey(from, to);
            if (!_patrolRouteFailCounts.ContainsKey(key))
                _patrolRouteFailCounts[key] = 0;
            _patrolRouteFailCounts[key]++;
            int fails = _patrolRouteFailCounts[key];

            if (fails >= 25)
            {
                // Too many failures — clear entire route
                _patrolRoutes.Remove(key);
                _patrolRouteFailCounts.Remove(key);
                Plugin.Log.LogInfo($"[NavGraph] Patrol route to ({to.x:F0},{to.z:F0}) fully cleared after 25 failures");
            }
            else if (_patrolRoutes.TryGetValue(key, out var route) && route.Count > 2)
            {
                // Trim route: keep everything up to the failure point, remove the rest
                int trimIdx = -1;
                float closestDist = float.MaxValue;
                for (int i = 0; i < route.Count; i++)
                {
                    float d = Vector3.Distance(route[i].Position, failPos);
                    if (d < closestDist) { closestDist = d; trimIdx = i; }
                }

                if (trimIdx > 0 && trimIdx < route.Count - 1)
                {
                    int removed = route.Count - trimIdx;
                    route.RemoveRange(trimIdx, removed);
                    Plugin.Log.LogInfo($"[NavGraph] Trimmed patrol route at fail point — kept {route.Count} nodes, removed {removed} (fail #{fails})");
                }
            }
        }

        /// <summary>Get a cached patrol route if one exists.</summary>
        public List<NavNode> GetPatrolRoute(Vector3 from, Vector3 to)
        {
            long key = PatrolRouteKey(from, to);
            if (_patrolRoutes.TryGetValue(key, out var route))
            {
                foreach (var n in route)
                    if (n.Confidence <= 0) return new List<NavNode>();
                return new List<NavNode>(route);
            }
            return new List<NavNode>();
        }

        private Dictionary<long, List<NavNode>> _patrolRoutes = new Dictionary<long, List<NavNode>>();
        private Dictionary<long, int> _patrolRouteFailCounts = new Dictionary<long, int>();

        /// <summary>
        /// Find a patrol route that gets the bot closest to a target position.
        /// Returns a path from the nearest route node to the bot, through the route, to the node nearest the target.
        /// </summary>
        public List<NavNode> FindNearestPatrolRoute(Vector3 botPos, Vector3 targetPos)
        {
            if (_patrolRoutes.Count == 0) return new List<NavNode>();

            NavNode bestStartNode = null;
            NavNode bestEndNode = null;
            List<NavNode> bestRoute = null;
            float bestScore = float.MaxValue;

            foreach (var route in _patrolRoutes.Values)
            {
                if (route.Count < 2) continue;

                // Find node in this route closest to the bot
                NavNode closestToBot = null;
                float closestBotDist = float.MaxValue;
                int closestBotIdx = 0;

                // Find node in this route closest to the target
                NavNode closestToTarget = null;
                float closestTargetDist = float.MaxValue;
                int closestTargetIdx = 0;

                for (int i = 0; i < route.Count; i++)
                {
                    if (route[i].Confidence <= 0) continue;
                    float botDist = Vector3.Distance(route[i].Position, botPos);
                    float targetDist = Vector3.Distance(route[i].Position, targetPos);

                    if (botDist < closestBotDist) { closestBotDist = botDist; closestToBot = route[i]; closestBotIdx = i; }
                    if (targetDist < closestTargetDist) { closestTargetDist = targetDist; closestToTarget = route[i]; closestTargetIdx = i; }
                }

                if (closestToBot == null || closestToTarget == null) continue;

                // Score: how useful is this route? Lower = better
                float score = closestBotDist + closestTargetDist;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestStartNode = closestToBot;
                    bestEndNode = closestToTarget;
                    bestRoute = route;
                }
            }

            if (bestRoute == null || bestScore > 50f) return new List<NavNode>(); // Too far from any route

            // Build path: A* to route start → follow route → route end is near target
            var pathToRoute = FindPath(botPos, bestStartNode.Position, searchRadius: 30f);
            if (pathToRoute.Count == 0) return new List<NavNode>();

            // Add the portion of the route from start to end
            int startIdx = bestRoute.IndexOf(bestStartNode);
            int endIdx = bestRoute.IndexOf(bestEndNode);
            if (startIdx >= 0 && endIdx >= 0 && startIdx != endIdx)
            {
                int step = startIdx < endIdx ? 1 : -1;
                for (int i = startIdx; i != endIdx; i += step)
                {
                    if (i >= 0 && i < bestRoute.Count && bestRoute[i].Confidence > 0)
                        pathToRoute.Add(bestRoute[i]);
                }
                if (endIdx >= 0 && endIdx < bestRoute.Count)
                    pathToRoute.Add(bestRoute[endIdx]);
            }

            return StraightenPath(pathToRoute);
        }

        /// <summary>Nodes visited by bots during patrol — permanently protected from pruning/merge.</summary>
        private HashSet<int> _patrolVisitedNodes = new HashSet<int>();

        /// <summary>Mark a node as visited during patrol. It will never be pruned or merged.</summary>
        public void ProtectPatrolNode(int nodeId)
        {
            _patrolVisitedNodes.Add(nodeId);
        }

        /// <summary>Check if a node is part of any saved patrol route or was visited during patrol.</summary>
        public bool IsPatrolProtected(int nodeId)
        {
            if (_patrolVisitedNodes.Contains(nodeId)) return true;
            foreach (var route in _patrolRoutes.Values)
                foreach (var n in route)
                    if (n.Id == nodeId) return true;
            return false;
        }

        /// <summary>
        /// Strip graph to ONLY patrol route nodes/edges. Deletes everything else.
        /// Call after patrol is complete to get a minimal weapon-path-only graph.
        /// </summary>
        public void TrimToPatrolRoutes()
        {
            if (_patrolRoutes.Count == 0)
            {
                Plugin.Log.LogWarning("[NavGraph] No patrol routes saved — nothing to trim to");
                return;
            }

            // Collect all node IDs used in any patrol route
            var protectedNodes = new HashSet<int>();
            foreach (var route in _patrolRoutes.Values)
            {
                foreach (var node in route)
                    protectedNodes.Add(node.Id);
            }

            // Also protect map location nodes (weapon spawners, spawns)
            foreach (var (pos, label, nodeId) in MapLocations)
                protectedNodes.Add(nodeId);

            // Delete all unprotected nodes
            int removedNodes = 0;
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0) continue;
                if (!protectedNodes.Contains(node.Id))
                {
                    node.Confidence = -1f;
                    removedNodes++;
                }
            }

            // Delete edges that don't connect two protected nodes
            int removedEdges = 0;
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0) continue;
                if (!protectedNodes.Contains(edge.From) || !protectedNodes.Contains(edge.To))
                {
                    edge.Confidence = -1f;
                    removedEdges++;
                }
            }

            Compact();
            _dirty = true;
            Save();
            Plugin.Log.LogInfo($"[NavGraph] Trimmed to patrol routes: kept {protectedNodes.Count} nodes, removed {removedNodes} nodes + {removedEdges} edges");
        }

        public void CompressNearby(Vector3 pos, float radius = 2f)
        {
            if (IsLocked) return;
            var nearby = FindNodesInRadius(pos, radius);
            if (nearby.Count < 3) return; // Not enough to compress

            // Find the highest-confidence node as anchor
            NavNode anchor = null;
            float bestConf = 0f;
            foreach (var node in nearby)
            {
                float score = node.Confidence + (node.PlayerSourced ? 0.5f : 0f);
                if (score > bestConf) { bestConf = score; anchor = node; }
            }
            if (anchor == null) return;

            bool merged = false;
            foreach (var neighbor in nearby)
            {
                if (neighbor.Id == anchor.Id) continue;
                if (neighbor.Confidence <= 0) continue;

                // Only merge very close nodes on the same level
                float dy = Mathf.Abs(neighbor.Position.y - anchor.Position.y);
                float horizDist = new Vector3(neighbor.Position.x - anchor.Position.x, 0,
                    neighbor.Position.z - anchor.Position.z).magnitude;
                if (dy > 1f || horizDist > ClusterMergeRadius) continue;

                if (HasSpecialEdge(neighbor.Id)) continue;
                if (IsPatrolProtected(neighbor.Id)) continue;
                if (IsMapLocation(neighbor.Id)) continue; // Never merge weapon/spawn nodes

                float anchorW = (anchor.VisitCount + 1f) * (anchor.PlayerSourced ? 2f : 1f);
                float neighborW = (neighbor.VisitCount + 1f) * (neighbor.PlayerSourced ? 2f : 1f);
                anchor.Position = Vector3.Lerp(neighbor.Position, anchor.Position, anchorW / (anchorW + neighborW));
                anchor.VisitCount += neighbor.VisitCount;
                anchor.Confidence = Mathf.Max(anchor.Confidence, neighbor.Confidence);
                if (neighbor.PlayerSourced) anchor.PlayerSourced = true;

                RedirectEdges(neighbor.Id, anchor.Id);
                neighbor.Confidence = -1f;
                merged = true;
            }

            if (merged) _dirty = true;
        }

        /// <summary>Check if a node is an endpoint of any special movement edge. These must not be merged.</summary>
        private bool HasSpecialEdge(int nodeId)
        {
            // Check outgoing edges (fast — indexed)
            if (_edgesByFrom.TryGetValue(nodeId, out var outEdges))
            {
                foreach (int ei in outEdges)
                {
                    if (ei < Edges.Count && Edges[ei].Confidence > 0)
                    {
                        var t = Edges[ei].Type;
                        if (t == EdgeType.Jump || t == EdgeType.Fall || t == EdgeType.Ladder || t == EdgeType.Slide || t == EdgeType.WallJump || t == EdgeType.Teleporter)
                            return true;
                    }
                }
            }
            // Check incoming edges via _edgesByTo (built during Compact)
            if (_edgesByTo.TryGetValue(nodeId, out var inEdges))
            {
                foreach (int ei in inEdges)
                {
                    if (ei < Edges.Count && Edges[ei].Confidence > 0)
                    {
                        var t = Edges[ei].Type;
                        if (t == EdgeType.Jump || t == EdgeType.Fall || t == EdgeType.Ladder || t == EdgeType.Slide || t == EdgeType.WallJump || t == EdgeType.Teleporter)
                            return true;
                    }
                }
            }
            return false;
        }

        // ========== AUTO DECLUTTER ==========

        /// <summary>
        /// Find dense clusters of nodes and merge them into single nodes.
        /// Preserves nodes that have only 1 connection (dead ends/bridges — merging would break pathing).
        /// </summary>
        public void AutoDeclutter(float clusterRadius = 1.5f, int minClusterSize = 3)
        {
            int totalMerged = 0;
            var processed = new HashSet<int>();

            // Sort by visit count descending — well-traveled nodes become anchors
            var sorted = new List<NavNode>(Nodes);
            sorted.RemoveAll(n => n == null);
            sorted.Sort((a, b) =>
            {
                float sa = a.VisitCount + (a.PlayerSourced ? 100 : 0);
                float sb = b.VisitCount + (b.PlayerSourced ? 100 : 0);
                return sb.CompareTo(sa);
            });

            foreach (var anchor in sorted)
            {
                if (anchor.Confidence <= 0 || processed.Contains(anchor.Id)) continue;
                processed.Add(anchor.Id);

                var cluster = FindNodesInRadius(anchor.Position, clusterRadius);
                if (cluster.Count < minClusterSize) continue;

                // Collect merge candidates — skip nodes with only 1 connection
                var toMerge = new List<NavNode>();
                foreach (var neighbor in cluster)
                {
                    if (neighbor.Id == anchor.Id || processed.Contains(neighbor.Id)) continue;
                    if (neighbor.Confidence <= 0) continue;

                    // Different floor check
                    if (Mathf.Abs(neighbor.Position.y - anchor.Position.y) > 1.5f) continue;

                    // Count outgoing connections (bidirectional edges make this sufficient)
                    int connectionCount = 0;
                    if (_edgesByFrom.TryGetValue(neighbor.Id, out var edges))
                    {
                        foreach (int ei in edges)
                            if (ei < Edges.Count && Edges[ei].Confidence > 0) connectionCount++;
                    }

                    // Don't merge nodes with only 1 connection — they're bridges/dead ends
                    if (connectionCount <= 1) continue;

                    // Never merge nodes that are endpoints of jump/fall/ladder/slide edges
                    if (HasSpecialEdge(neighbor.Id)) continue;
                    if (IsPatrolProtected(neighbor.Id)) continue;
                    if (IsMapLocation(neighbor.Id)) continue;

                    toMerge.Add(neighbor);
                }

                if (toMerge.Count == 0) continue;

                // Merge all candidates into anchor
                foreach (var neighbor in toMerge)
                {
                    float anchorW = (anchor.VisitCount + 1f) * (anchor.PlayerSourced ? 2f : 1f);
                    float neighborW = (neighbor.VisitCount + 1f) * (neighbor.PlayerSourced ? 2f : 1f);
                    anchor.Position = Vector3.Lerp(neighbor.Position, anchor.Position, anchorW / (anchorW + neighborW));
                    anchor.VisitCount += neighbor.VisitCount;
                    anchor.Confidence = Mathf.Max(anchor.Confidence, neighbor.Confidence);
                    if (neighbor.PlayerSourced) anchor.PlayerSourced = true;

                    RedirectEdges(neighbor.Id, anchor.Id);
                    neighbor.Confidence = -1f;
                    processed.Add(neighbor.Id);
                    totalMerged++;
                }
            }

            if (totalMerged > 0)
            {
                Compact();
                Plugin.Log.LogInfo($"[NavGraph] Decluttered: merged {totalMerged} nodes into clusters");
                _dirty = true;
            }
        }

        /// <summary>
        /// Remove intermediate nodes on straight paths. If A->B->C are roughly in a line
        /// and B has only 2 connections (A and C), remove B and connect A->C directly.
        /// Only removes nodes with high visit counts (proven path) on flat ground.
        /// </summary>
        public void SimplifyPaths()
        {
            int totalRemoved = 0;

            // Run multiple passes — each pass can expose new simplification opportunities
            for (int pass = 0; pass < 5; pass++)
            {
                int removed = 0;
                var toRemove = new HashSet<int>();

                foreach (var node in Nodes)
                {
                    if (node == null || node.Confidence <= 0 || toRemove.Contains(node.Id)) continue;

                    // Get unique neighbors via outgoing edges only
                    // (bidirectional edges mean outgoing captures both directions)
                    var neighbors = new HashSet<int>();
                    if (_edgesByFrom.TryGetValue(node.Id, out var outEdges))
                    {
                        foreach (int ei in outEdges)
                            if (ei < Edges.Count && Edges[ei].Confidence > 0 && !toRemove.Contains(Edges[ei].To))
                                neighbors.Add(Edges[ei].To);
                    }

                    // Pass-through node: exactly 2 neighbors
                    if (neighbors.Count != 2) continue;

                    // Never simplify away player-sourced nodes — they define the path
                    if (node.PlayerSourced) continue;

                    var ids = new List<int>(neighbors);
                    var nodeA = GetNodeById(ids[0]);
                    var nodeC = GetNodeById(ids[1]);
                    if (nodeA == null || nodeC == null) continue;
                    if (nodeA.Confidence <= 0 || nodeC.Confidence <= 0) continue;

                    // Relaxed collinearity — dot > 0.5 (wider angle tolerance)
                    Vector3 ab = node.Position - nodeA.Position; ab.y = 0;
                    Vector3 bc = nodeC.Position - node.Position; bc.y = 0;
                    if (ab.sqrMagnitude < 0.01f || bc.sqrMagnitude < 0.01f) continue;
                    float dot = Vector3.Dot(ab.normalized, bc.normalized);
                    if (dot < 0.5f) continue;

                    // Allow larger height difference (1.5m for ramps/stairs)
                    float maxHeightDiff = Mathf.Max(
                        Mathf.Abs(nodeA.Position.y - node.Position.y),
                        Mathf.Abs(node.Position.y - nodeC.Position.y));
                    if (maxHeightDiff > 1.5f) continue;

                    // Larger max distance (NeighborRadius * 2.5)
                    float acDist = Vector3.Distance(nodeA.Position, nodeC.Position);
                    if (acDist > NeighborRadius * 2.5f) continue;

                    // Must have line of sight A->C
                    if (!ValidateLineOfSight(nodeA.Position, nodeC.Position)) continue;

                    // Determine edge type — use the more important type
                    EdgeType edgeType = EdgeType.Walk;
                    if (_edgesByFrom.TryGetValue(nodeA.Id, out var aEdges))
                    {
                        foreach (int ei in aEdges)
                        {
                            if (ei < Edges.Count && Edges[ei].To == node.Id && Edges[ei].Confidence > 0)
                            {
                                if (Edges[ei].Type == EdgeType.Jump || Edges[ei].Type == EdgeType.Ladder)
                                    edgeType = Edges[ei].Type;
                                break;
                            }
                        }
                    }

                    AddEdge(nodeA.Id, nodeC.Id, edgeType, acDist, force: true);
                    AddEdge(nodeC.Id, nodeA.Id, edgeType, acDist, force: true);
                    toRemove.Add(node.Id);
                    removed++;
                }

                foreach (int id in toRemove)
                {
                    var n = GetNodeById(id);
                    if (n != null) n.Confidence = -1f;
                }

                totalRemoved += removed;
                if (removed == 0) break; // No more to simplify
                Compact(); // Rebuild for next pass
            }

            if (totalRemoved > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Simplified: removed {totalRemoved} intermediate nodes");
                _dirty = true;
            }
        }

        // ========== ZONE SIMPLIFICATION ==========

        /// <summary>
        /// Find flat areas with many nodes and replace with corner nodes forming a rectangle.
        /// Massively reduces clutter on open floors/platforms.
        /// </summary>
        public void BuildZones(float cellSize = 4f, int minNodesInCell = 5)
        {
            int totalRemoved = 0;
            int zonesCreated = 0;
            var processed = new HashSet<int>();

            // Grid-based: group nodes by cell
            var cells = new Dictionary<(int, int, int), List<NavNode>>();
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0) continue;
                int cx = Mathf.FloorToInt(node.Position.x / cellSize);
                int cy = Mathf.FloorToInt(node.Position.y / cellSize);
                int cz = Mathf.FloorToInt(node.Position.z / cellSize);
                var key = (cx, cy, cz);
                if (!cells.ContainsKey(key)) cells[key] = new List<NavNode>();
                cells[key].Add(node);
            }

            foreach (var kv in cells)
            {
                var cellNodes = kv.Value;
                if (cellNodes.Count < minNodesInCell) continue;

                // Check flatness — all nodes must be within 0.5m height
                float minY = float.MaxValue, maxY = float.MinValue;
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                foreach (var n in cellNodes)
                {
                    if (n.Position.y < minY) minY = n.Position.y;
                    if (n.Position.y > maxY) maxY = n.Position.y;
                    if (n.Position.x < minX) minX = n.Position.x;
                    if (n.Position.x > maxX) maxX = n.Position.x;
                    if (n.Position.z < minZ) minZ = n.Position.z;
                    if (n.Position.z > maxZ) maxZ = n.Position.z;
                }

                if (maxY - minY > 0.5f) continue; // Not flat
                float spanX = maxX - minX;
                float spanZ = maxZ - minZ;
                if (spanX < 1.5f || spanZ < 1.5f) continue; // Too narrow

                float avgY = (minY + maxY) * 0.5f;

                // Create 4 corner nodes + center node
                Vector3[] corners = {
                    new Vector3(minX, avgY + 0.1f, minZ),
                    new Vector3(maxX, avgY + 0.1f, minZ),
                    new Vector3(maxX, avgY + 0.1f, maxZ),
                    new Vector3(minX, avgY + 0.1f, maxZ)
                };
                Vector3 center = new Vector3((minX + maxX) * 0.5f, avgY + 0.1f, (minZ + maxZ) * 0.5f);

                // Validate corners have ground
                bool allValid = true;
                foreach (var c in corners)
                    if (!ValidateGround(c)) { allValid = false; break; }
                if (!allValid) continue;

                // Check line of sight between all corners
                bool allVisible = true;
                for (int i = 0; i < 4 && allVisible; i++)
                    if (!ValidateLineOfSight(corners[i], corners[(i + 1) % 4])) allVisible = false;
                if (!allVisible) continue;

                // Collect outgoing edges from cell nodes to outside nodes (preserve connectivity)
                var externalEdges = new List<(int toId, EdgeType type, float cost)>();
                foreach (var node in cellNodes)
                {
                    if (_edgesByFrom.TryGetValue(node.Id, out var edges))
                    {
                        foreach (int ei in edges)
                        {
                            if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                            bool isInternal = false;
                            foreach (var cn in cellNodes)
                                if (cn.Id == Edges[ei].To) { isInternal = true; break; }
                            if (!isInternal)
                                externalEdges.Add((Edges[ei].To, Edges[ei].Type, Edges[ei].Cost));
                        }
                    }
                }

                // Determine if any cell nodes are player-sourced
                bool hasPlayer = false;
                int totalVisits = 0;
                foreach (var n in cellNodes) { if (n.PlayerSourced) hasPlayer = true; totalVisits += n.VisitCount; }

                // Remove all cell nodes
                foreach (var n in cellNodes)
                {
                    n.Confidence = -1f;
                    totalRemoved++;
                }

                // Create corner + center nodes
                var cornerNodes = new NavNode[4];
                for (int i = 0; i < 4; i++)
                {
                    cornerNodes[i] = new NavNode(_nextNodeId++, corners[i]);
                    cornerNodes[i].Confidence = 1f;
                    cornerNodes[i].PlayerSourced = hasPlayer;
                    cornerNodes[i].VisitCount = totalVisits / 5;
                    Nodes.Add(cornerNodes[i]);
                    AddToSpatialGrid(cornerNodes[i]);
                }

                var centerNode = new NavNode(_nextNodeId++, center);
                centerNode.Confidence = 1f;
                centerNode.PlayerSourced = hasPlayer;
                centerNode.VisitCount = totalVisits / 5;
                Nodes.Add(centerNode);
                AddToSpatialGrid(centerNode);

                // Connect corners in a ring + each corner to center (star pattern)
                for (int i = 0; i < 4; i++)
                {
                    int next = (i + 1) % 4;
                    float edgeDist = Vector3.Distance(corners[i], corners[next]);
                    AddEdge(cornerNodes[i].Id, cornerNodes[next].Id, EdgeType.Walk, edgeDist, force: true);
                    AddEdge(cornerNodes[next].Id, cornerNodes[i].Id, EdgeType.Walk, edgeDist, force: true);

                    float centerDist = Vector3.Distance(corners[i], center);
                    AddEdge(cornerNodes[i].Id, centerNode.Id, EdgeType.Walk, centerDist, force: true);
                    AddEdge(centerNode.Id, cornerNodes[i].Id, EdgeType.Walk, centerDist, force: true);
                }

                // Diagonal connections
                float diagDist = Vector3.Distance(corners[0], corners[2]);
                AddEdge(cornerNodes[0].Id, cornerNodes[2].Id, EdgeType.Walk, diagDist, force: true);
                AddEdge(cornerNodes[2].Id, cornerNodes[0].Id, EdgeType.Walk, diagDist, force: true);
                diagDist = Vector3.Distance(corners[1], corners[3]);
                AddEdge(cornerNodes[1].Id, cornerNodes[3].Id, EdgeType.Walk, diagDist, force: true);
                AddEdge(cornerNodes[3].Id, cornerNodes[1].Id, EdgeType.Walk, diagDist, force: true);

                // Reconnect external edges to nearest corner
                foreach (var (toId, type, cost) in externalEdges)
                {
                    var toNode = GetNodeById(toId);
                    if (toNode == null || toNode.Confidence <= 0) continue;

                    // Find closest corner to external node
                    NavNode closest = centerNode;
                    float closestDist = Vector3.Distance(center, toNode.Position);
                    for (int i = 0; i < 4; i++)
                    {
                        float d = Vector3.Distance(corners[i], toNode.Position);
                        if (d < closestDist) { closestDist = d; closest = cornerNodes[i]; }
                    }

                    AddEdge(closest.Id, toId, type, closestDist, force: true);
                    AddEdge(toId, closest.Id, type, closestDist, force: true);
                }

                zonesCreated++;
            }

            if (zonesCreated > 0)
            {
                Compact();
                Plugin.Log.LogInfo($"[NavGraph] Built {zonesCreated} zones, removed {totalRemoved} nodes");
                _dirty = true;
            }
        }

        /// <summary>
        /// Run periodic maintenance — declutter + simplify every 15s.
        /// Call from Update loop.
        /// </summary>
        private float _lastAutoSaveTime;

        public void PeriodicMaintenance()
        {
            // Auto-save on interval (always runs, even when locked)
            if (_dirty && Time.time - _lastAutoSaveTime > Plugin.GetAutoSaveInterval())
            {
                _lastAutoSaveTime = Time.time;
                Save();
            }

            if (IsLocked) return;
            if (Time.time - _lastDeclutterTime < 15f) return;
            _lastDeclutterTime = Time.time;
            if (Nodes.Count < 20) return;

            // Revalidate walk edges (wall check) — always safe
            RevalidateWalkEdges(50);

            // In Play mode, never trim — Play is read-only
            if (Mode == NavMode.Play) return;

            AutoDeclutter(ClusterMergeRadius);
            PruneExcessEdges();
            SimplifyPaths();
            if (Nodes.Count > 200) BuildZones();
            _routeCache.Clear();
        }

        /// <summary>
        /// For nodes with more than MAX_EDGES_PER_NODE outgoing walk edges,
        /// remove the longest/lowest-confidence ones to reduce webbing.
        /// </summary>
        private void PruneExcessEdges()
        {
            int removed = 0;
            foreach (var kvp in _edgesByFrom)
            {
                var indices = kvp.Value;
                // Count active walk edges
                int walkCount = 0;
                foreach (int ei in indices)
                {
                    if (ei < Edges.Count && Edges[ei].Confidence > 0 && Edges[ei].Type == EdgeType.Walk)
                        walkCount++;
                }

                if (walkCount <= MAX_EDGES_PER_NODE) continue;

                // Collect walk edges, sort by score (low = remove first)
                var walkEdges = new List<(int idx, float score)>();
                foreach (int ei in indices)
                {
                    if (ei >= Edges.Count || Edges[ei].Confidence <= 0 || Edges[ei].Type != EdgeType.Walk) continue;
                    var e = Edges[ei];
                    var toNode = GetNodeById(e.To);
                    bool playerEdge = false;
                    var fromNode = GetNodeById(e.From);
                    if (fromNode != null && toNode != null)
                        playerEdge = fromNode.PlayerSourced && toNode.PlayerSourced;
                    // Score: higher = keep. Player edges get big bonus, short edges preferred, high confidence preferred
                    float score = e.Confidence * 10f
                        - e.Cost * 0.5f
                        + e.SuccessCount * 0.5f
                        + (playerEdge ? 50f : 0f);
                    walkEdges.Add((ei, score));
                }

                walkEdges.Sort((a, b) => a.score.CompareTo(b.score)); // Lowest score first

                // Remove excess (lowest-scored walk edges)
                int toRemove = walkCount - MAX_EDGES_PER_NODE;
                for (int i = 0; i < toRemove && i < walkEdges.Count; i++)
                {
                    Edges[walkEdges[i].idx].Confidence = -1f;
                    removed++;
                }
            }

            if (removed > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Pruned {removed} excess walk edges (>{MAX_EDGES_PER_NODE} per node)");
                _dirty = true;
            }
        }

        private int _revalidateIdx;

        /// <summary>
        /// Check a batch of walk edges for wall obstructions. Removes edges confirmed through walls.
        /// Runs every maintenance cycle, checking batchSize edges round-robin.
        /// </summary>
        private void RevalidateWalkEdges(int batchSize)
        {
            if (Edges.Count == 0) return;
            int removed = 0;

            for (int i = 0; i < batchSize; i++)
            {
                if (_revalidateIdx >= Edges.Count) _revalidateIdx = 0;
                var edge = Edges[_revalidateIdx];
                _revalidateIdx++;

                if (edge.Confidence <= 0 || edge.Type != EdgeType.Walk) continue;

                var fromNode = GetNodeById(edge.From);
                var toNode = GetNodeById(edge.To);
                if (fromNode == null || toNode == null) continue;

                if (!ValidateLineOfSight(fromNode.Position, toNode.Position))
                {
                    bool playerEdge = fromNode.PlayerSourced && toNode.PlayerSourced;
                    if (playerEdge)
                        edge.Confidence = Mathf.Max(0.05f, edge.Confidence - 0.2f);
                    else
                        edge.Confidence = -1f;
                    removed++;
                }
            }

            if (removed > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Revalidation: removed {removed} wall-blocked edges");
                _dirty = true;
            }
        }

        // ========== PRUNING & COMPACTION ==========

        /// <summary>
        /// Prune the graph: remove low-confidence nodes, merge clusters of nearby high-confidence nodes.
    }
}
