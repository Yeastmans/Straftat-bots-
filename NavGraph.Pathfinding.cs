using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    public partial class NavGraph
    {
        // ========== A* PATHFINDING ==========

        /// <summary>
        /// Find a path from start position to target position.
        /// Returns list of nodes to follow, or empty if no path.
        /// </summary>
        public List<NavNode> FindPath(Vector3 startPos, Vector3 targetPos, float jitter = 0.15f, float searchRadius = 30f, bool playerOnly = false, bool preferHeight = false)
        {
            var startNode = playerOnly ? FindNearestPlayerNode(startPos, searchRadius) : FindNearestNode(startPos, searchRadius);
            var endNode = playerOnly ? FindNearestPlayerNode(targetPos, searchRadius) : FindNearestNode(targetPos, searchRadius);

            if (startNode == null || endNode == null) return new List<NavNode>();
            if (startNode.Id == endNode.Id) return new List<NavNode> { endNode };

            var openSet = new SortedSet<(float f, int id)>(_astarComparer);
            var gScore = new Dictionary<int, float>();
            var cameFrom = new Dictionary<int, int>();

            gScore[startNode.Id] = 0;
            float h = Vector3.Distance(startNode.Position, endNode.Position);
            openSet.Add((h, startNode.Id));

            int iterations = 0;
            int maxIterations = Mathf.Min(Nodes.Count * 4, 30000); // Deeper search for optimal paths

            while (openSet.Count > 0 && iterations++ < maxIterations)
            {
                var current = openSet.Min;
                openSet.Remove(current);
                int currentId = current.id;

                if (currentId == endNode.Id)
                {
                    var path = new List<NavNode>();
                    int c = endNode.Id;
                    while (cameFrom.ContainsKey(c))
                    {
                        var pathNode = GetNodeById(c);
                        if (pathNode != null) path.Add(pathNode);
                        else break; // Stale node ID — truncate path here
                        c = cameFrom[c];
                    }
                    path.Reverse();
                    return StraightenPath(path);
                }

                if (!_edgesByFrom.TryGetValue(currentId, out var edges)) continue;

                float currentG = gScore.ContainsKey(currentId) ? gScore[currentId] : float.MaxValue;

                foreach (int ei in edges)
                {
                    if (ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge.Confidence <= 0) continue;

                    // Skip blacklisted destination nodes
                    if (IsBlacklisted(edge.To)) continue;

                    var toN = GetNodeById(edge.To);
                    if (toN == null) continue;

                    // Player-only mode: skip non-player-sourced destination nodes
                    if (playerOnly && !toN.PlayerSourced) continue;

                    // Base cost = distance / confidence
                    float edgeCost = edge.Cost / Mathf.Max(0.1f, edge.Confidence);

                    // Prefer player-sourced paths
                    var fromN = GetNodeById(edge.From);
                    if (fromN != null && toN != null)
                    {
                        if (fromN.PlayerSourced && toN.PlayerSourced)
                            edgeCost *= 0.5f;  // 50% cheaper to follow player paths
                        else if (fromN.PlayerSourced || toN.PlayerSourced)
                            edgeCost *= 0.7f;  // 30% cheaper if at least one end is player-sourced
                    }

                    // Edge type cost modifiers:
                    // Jump = intentional traversal, prefer it. Ladder/Slide = structured movement, prefer.
                    // Fall = risky, heavy penalty. Walk on ramps (height change) = good, slight preference.
                    switch (edge.Type)
                    {
                        case EdgeType.Jump:
                            if (preferHeight && fromN != null && toN != null && toN.Position.y > fromN.Position.y + 0.5f)
                                edgeCost *= 0.5f;  // 50% cheaper — jump gains height toward target
                            else
                                edgeCost *= 0.9f;
                            if (edge.AirSampleCount > 2)
                                edgeCost *= 0.6f;  // Proven trajectory data — strongly prefer
                            break;
                        case EdgeType.Ladder:
                            if (preferHeight)
                                edgeCost *= 0.3f;  // 70% cheaper — ladders are best for height gain
                            else
                                edgeCost *= 0.6f;
                            break;
                        case EdgeType.Slide:
                            edgeCost *= 0.75f; // 25% cheaper — slides are fast movement
                            break;
                        case EdgeType.WallJump:
                            edgeCost *= 0.85f; // Slightly prefer — trained wall jump paths
                            if (edge.AirSampleCount > 2)
                                edgeCost *= 0.6f;  // Proven trajectory data — strongly prefer
                            break;
                        case EdgeType.Fall:
                            edgeCost *= 3.0f;  // Heavy penalty — one-way drop, can't return
                            break;
                        case EdgeType.Teleporter:
                            edgeCost *= 0.1f;  // Near-free — instant teleport
                            break;
                        case EdgeType.Walk:
                            // Prefer walk edges with height change (ramps/stairs) over flat ground
                            if (fromN != null && toN != null)
                            {
                                float heightGain = toN.Position.y - fromN.Position.y;
                                float heightAbs = Mathf.Abs(heightGain);
                                if (heightAbs > 0.3f && heightAbs < 3f)
                                {
                                    if (preferHeight && heightGain > 0.3f)
                                        edgeCost *= 0.4f; // 60% cheaper — strongly prefer gaining height
                                    else if (preferHeight && heightGain < -0.3f)
                                        edgeCost *= 1.5f; // Penalize losing height when we need to go up
                                    else
                                        edgeCost *= 0.8f; // 20% cheaper for any ramp/stair
                                }
                            }
                            break;
                    }

                    // Penalize NearEdge nodes — prefer safe interior paths
                    if (toN != null && toN.NearEdge)
                        edgeCost *= 3f; // 3x cost to traverse edge nodes — strong avoidance

                    // Boost edges that appear in a demonstrated route ("Watch Me" captures)
                    if (IsProvenEdge(edge.From, edge.To))
                        edgeCost *= 0.5f; // 50% cheaper — player demonstrated this sequence

                    // Penalize sharp turns — prefer straighter paths
                    if (cameFrom.ContainsKey(currentId) && fromN != null && toN != null)
                    {
                        var prevNode = GetNodeById(cameFrom[currentId]);
                        if (prevNode != null)
                        {
                            Vector3 prevDir = fromN.Position - prevNode.Position;
                            Vector3 nextDir = toN.Position - fromN.Position;
                            prevDir.y = 0; nextDir.y = 0;
                            if (prevDir.sqrMagnitude > 0.01f && nextDir.sqrMagnitude > 0.01f)
                            {
                                float dot = Vector3.Dot(prevDir.normalized, nextDir.normalized);
                                // dot=1 straight, dot=0 right angle, dot=-1 reverse
                                if (dot < 0.3f) // Sharp turn (>72°)
                                    edgeCost *= 1.3f;
                                else if (dot < 0.7f) // Moderate turn (45-72°)
                                    edgeCost *= 1.1f;
                                // Straight ahead (>45°) = no penalty
                            }
                        }
                    }

                    if (jitter > 0)
                        edgeCost *= (1f + UnityEngine.Random.Range(-jitter, jitter));

                    float tentativeG = currentG + edgeCost;
                    float neighborG = gScore.ContainsKey(edge.To) ? gScore[edge.To] : float.MaxValue;

                    if (tentativeG < neighborG)
                    {
                        cameFrom[edge.To] = currentId;
                        gScore[edge.To] = tentativeG;

                        var toNode = GetNodeById(edge.To);
                        if (toNode != null)
                        {
                            float fScore = tentativeG + Vector3.Distance(toNode.Position, endNode.Position);
                            openSet.Add((fScore, edge.To));
                        }
                    }
                }
            }

            return new List<NavNode>();
        }

        /// <summary>
        /// Find a path that avoids a specific area. Used for stuck recovery —
        /// temporarily blacklists nodes near avoidPos and pathfinds with wider search radius
        /// to force a detour around the obstacle.
        /// </summary>
        public List<NavNode> FindPathAvoiding(Vector3 startPos, Vector3 targetPos, Vector3 avoidPos, float avoidRadius = 5f, float searchRadius = 40f)
        {
            // Temporarily blacklist nodes in the avoid zone
            var tempAvoided = new List<int>();
            var nearby = FindNodesInRadius(avoidPos, avoidRadius);
            float shortExpiry = Time.time + 10f; // Short blacklist — just for this repath cycle

            foreach (var node in nearby)
            {
                if (!_tempBlacklist.ContainsKey(node.Id))
                {
                    _tempBlacklist[node.Id] = shortExpiry;
                    tempAvoided.Add(node.Id);
                }
            }

            // Now pathfind with wider search radius — forces A* to go around the avoided area
            var path = FindPath(startPos, targetPos, jitter: 0.05f, searchRadius: searchRadius);

            // If direct path failed, try reaching closest reachable node near target
            if (path.Count == 0)
            {
                var reachable = FindClosestReachableNode(startPos, targetPos);
                if (reachable != null)
                    path = FindPath(startPos, reachable.Position, jitter: 0.05f, searchRadius: searchRadius);
            }

            // If still no path, try progress node (gets closer even if can't reach target)
            if (path.Count == 0)
            {
                var progress = FindProgressNode(startPos, targetPos, searchRadius);
                if (progress != null)
                    path = FindPath(startPos, progress.Position, jitter: 0.05f, searchRadius: searchRadius);
            }

            // Clean up temp blacklist entries we added (let normal blacklist persist)
            foreach (int id in tempAvoided)
            {
                if (_tempBlacklist.TryGetValue(id, out float exp) && exp == shortExpiry)
                    _tempBlacklist.Remove(id);
            }

            return path;
        }

        /// <summary>
        /// Find the nearest player-sourced node. Used when bots get stuck —
        /// they path to the closest known-good player path to recover.
        /// </summary>
        /// <summary>
        /// Straighten an A* path by removing intermediate walk nodes where direct line-of-sight exists.
        /// Preserves jump/fall/ladder/slide/teleporter nodes — only skips walk-to-walk shortcuts.
        /// </summary>
        public List<NavNode> StraightenPath(List<NavNode> path)
        {
            if (path.Count <= 2) return path;

            var result = new List<NavNode> { path[0] };
            int i = 0;

            while (i < path.Count - 1)
            {
                // Try to skip as far ahead as possible with direct line-of-sight
                int furthest = i + 1;

                for (int j = path.Count - 1; j > i + 1; j--)
                {
                    // Don't skip over critical nodes:
                    // - Jump/fall/ladder/slide endpoints
                    // - Patrol-protected nodes
                    // - Nodes with significant height change (stairs/ramps)
                    bool hasCriticalBetween = false;
                    for (int k = i + 1; k <= j; k++)
                    {
                        if (HasSpecialEdge(path[k].Id) || IsPatrolProtected(path[k].Id))
                        { hasCriticalBetween = true; break; }
                        // Don't skip nodes with >0.5m height change — they're ramp/stair steps
                        if (k > 0 && Mathf.Abs(path[k].Position.y - path[k-1].Position.y) > 0.5f)
                        { hasCriticalBetween = true; break; }
                    }
                    if (hasCriticalBetween) continue;

                    // Check line-of-sight and ground between i and j
                    if (ValidateLineOfSight(path[i].Position, path[j].Position)
                        && ValidateEdgeGround(path[i].Position, path[j].Position))
                    {
                        furthest = j;
                        break;
                    }
                }

                result.Add(path[furthest]);
                i = furthest;
            }

            return result;
        }

        public NavNode FindNearestPlayerNode(Vector3 pos, float maxDist = 30f)
        {
            // Use spatial grid for faster lookup, then filter PlayerSourced
            float bestSqr = maxDist * maxDist;
            NavNode best = null;

            int cx = Mathf.FloorToInt(pos.x / GRID_CELL);
            int cz = Mathf.FloorToInt(pos.z / GRID_CELL);
            int cy = Mathf.FloorToInt(pos.y / GRID_CELL);
            int range = Mathf.CeilToInt(maxDist / GRID_CELL);

            for (int dx = -range; dx <= range; dx++)
            for (int dy = -range; dy <= range; dy++)
            for (int dz = -range; dz <= range; dz++)
            {
                long key = GridKey(cx + dx, cy + dy, cz + dz);
                if (!_spatialGrid.TryGetValue(key, out var list)) continue;
                foreach (int idx in list)
                {
                    if (idx < 0 || idx >= Nodes.Count) continue;
                    var node = Nodes[idx];
                    if (node == null || node.Confidence <= 0 || !node.PlayerSourced) continue;
                    float sqr = (node.Position.x - pos.x) * (node.Position.x - pos.x)
                        + (node.Position.y - pos.y) * (node.Position.y - pos.y)
                        + (node.Position.z - pos.z) * (node.Position.z - pos.z);
                    if (sqr < bestSqr) { bestSqr = sqr; best = node; }
                }
            }
            return best;
        }

        /// <summary>
        /// Find a path that strongly prefers player-sourced nodes.
        /// Used for stuck recovery — bots navigate to nearest player path.
        /// </summary>
        public List<NavNode> FindPathToPlayerPath(Vector3 startPos)
        {
            var playerNode = FindNearestPlayerNode(startPos);
            if (playerNode == null) return new List<NavNode>();
            // Use low jitter for reliable pathing when stuck
            return FindPath(startPos, playerNode.Position, jitter: 0.05f);
        }

        /// <summary>
        /// Find a node that makes progress toward a target position — higher up, closer horizontally.
        /// Used when direct A* to target fails. Searches outward and upward.
        /// </summary>
        public NavNode FindProgressNode(Vector3 currentPos, Vector3 targetPos, float searchRadius = 20f)
        {
            // BFS from start to find all reachable nodes, then score them
            var startNode = FindNearestNode(currentPos, searchRadius);
            if (startNode == null) return null;

            float targetY = targetPos.y;
            float currentY = currentPos.y;
            bool needsUp = targetY > currentY + 2f;
            float distToTarget = Vector3.Distance(currentPos, targetPos);

            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(startNode.Id);
            visited.Add(startNode.Id);

            NavNode best = null;
            float bestScore = float.MinValue;

            int iterations = 0;
            while (queue.Count > 0 && iterations++ < 2000)
            {
                int currentId = queue.Dequeue();
                var node = GetNodeById(currentId);
                if (node == null) continue;

                float dist = Vector3.Distance(currentPos, node.Position);
                if (dist >= 3f) // Must be far enough to make progress
                {
                    float score = 0f;
                    if (needsUp)
                    {
                        float heightGain = node.Position.y - currentY;
                        score += heightGain * 5f;
                        float hx = node.Position.x - targetPos.x, hz = node.Position.z - targetPos.z;
                        score -= Mathf.Sqrt(hx * hx + hz * hz) * 0.3f;
                    }
                    else
                    {
                        float closer = distToTarget - Vector3.Distance(node.Position, targetPos);
                        score += closer * 3f;
                    }

                    if (node.PlayerSourced) score += 3f;
                    if (_edgesByFrom.TryGetValue(node.Id, out var nodeEdges))
                    {
                        foreach (int ei in nodeEdges)
                        {
                            if (ei < Edges.Count && (Edges[ei].Type == EdgeType.Ladder || Edges[ei].Type == EdgeType.Jump))
                                score += 2f;
                        }
                    }

                    if (score > bestScore) { bestScore = score; best = node; }
                }

                // Expand BFS
                if (_edgesByFrom.TryGetValue(currentId, out var edges))
                {
                    foreach (int ei in edges)
                    {
                        if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                        if (IsBlacklisted(Edges[ei].To)) continue;
                        if (!visited.Add(Edges[ei].To)) continue;
                        queue.Enqueue(Edges[ei].To);
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Find the closest node to targetPos that is actually reachable from startPos via A*.
        /// Searches outward from target, testing connectivity. Prevents bots walking to unreachable spots.
        /// </summary>
        public NavNode FindClosestReachableNode(Vector3 startPos, Vector3 targetPos)
        {
            // BFS from start to flood-fill all reachable nodes, track closest to target
            var startNode = FindNearestNode(startPos, 20f);
            if (startNode == null) return null;

            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(startNode.Id);
            visited.Add(startNode.Id);

            NavNode best = null;
            float bestDist = float.MaxValue;

            int iterations = 0;
            while (queue.Count > 0 && iterations++ < 3000)
            {
                int currentId = queue.Dequeue();
                var current = GetNodeById(currentId);
                if (current == null) continue;

                float dist = Vector3.Distance(current.Position, targetPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = current;
                }

                if (_edgesByFrom.TryGetValue(currentId, out var edges))
                {
                    foreach (int ei in edges)
                    {
                        if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                        if (IsBlacklisted(Edges[ei].To)) continue;
                        if (!visited.Add(Edges[ei].To)) continue;
                        queue.Enqueue(Edges[ei].To);
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Find a frontier node — a node at the edge of explored territory with few outgoing edges.
        /// These are the ends of paths where bots should explore further.
        /// </summary>
        public NavNode FindFrontierNode(Vector3 currentPos, float minDist = 5f, Vector3 avoidPos = default,
            HashSet<long> exploredCells = null)
        {
            if (Nodes.Count == 0) return null;

            // Scale search based on local density — denser area = search further + sample more
            int localDensity = 0;
            int lcx = Mathf.FloorToInt(currentPos.x / GRID_CELL);
            int lcy = Mathf.FloorToInt(currentPos.y / GRID_CELL);
            int lcz = Mathf.FloorToInt(currentPos.z / GRID_CELL);
            for (int dx = -2; dx <= 2; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -2; dz <= 2; dz++)
            {
                long key = GridKey(lcx + dx, lcy + dy, lcz + dz);
                if (_spatialGrid.TryGetValue(key, out var cell))
                    localDensity += cell.Count;
            }
            // Dense area (50+ nodes nearby): search further, sample more
            if (localDensity > 50) minDist = Mathf.Max(minDist, 15f);
            else if (localDensity > 20) minDist = Mathf.Max(minDist, 10f);

            NavNode best = null;
            float bestScore = float.MinValue;

            int samples = localDensity > 30 ? Mathf.Min(Nodes.Count, 80) : Mathf.Min(Nodes.Count, 50);
            for (int i = 0; i < samples; i++)
            {
                var node = Nodes[UnityEngine.Random.Range(0, Nodes.Count)];
                if (node.Confidence <= 0) continue;

                float dist = Vector3.Distance(node.Position, currentPos);
                if (dist < minDist) continue;

                // Count outgoing LIVE edges + check for special edges leading outward
                int edgeCount = 0;
                bool hasJumpOrLadder = false;
                int specialEdgeTargetVisits = 0;
                if (_edgesByFrom.TryGetValue(node.Id, out var edgeList))
                {
                    foreach (int ei in edgeList)
                    {
                        if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                        edgeCount++;
                        var e = Edges[ei];
                        if (e.Type == EdgeType.Jump || e.Type == EdgeType.Ladder
                            || e.Type == EdgeType.WallJump || e.Type == EdgeType.Teleporter)
                        {
                            hasJumpOrLadder = true;
                            // Check if the destination is less visited (leads to new territory)
                            var dest = GetNodeById(e.To);
                            if (dest != null)
                                specialEdgeTargetVisits += dest.VisitCount;
                        }
                    }
                }

                // Sparse area bonus: count nearby nodes in 3x3x3 grid neighborhood
                int nearbyCount = 0;
                int cx = Mathf.FloorToInt(node.Position.x / GRID_CELL);
                int cy = Mathf.FloorToInt(node.Position.y / GRID_CELL);
                int cz = Mathf.FloorToInt(node.Position.z / GRID_CELL);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long key = GridKey(cx + dx, cy + dy, cz + dz);
                    if (_spatialGrid.TryGetValue(key, out var cell))
                        nearbyCount += cell.Count;
                }

                // Score components
                float score = (5f - Mathf.Min(edgeCount, 5)) * 4f  // Fewer edges = frontier
                    + dist * 0.15f                                   // Farther = explore further
                    - node.VisitCount * 0.5f                         // Less visited = higher
                    + node.Confidence * 0.5f;                        // Confident nodes preferred

                // Sparse area: fewer nearby nodes = more worth exploring
                float sparsity = Mathf.Max(0, 20 - nearbyCount);    // 0-20 range
                score += sparsity * 0.8f;                            // Strong sparse bias

                // Jump/ladder edges leading to low-visit areas = gateway to new territory
                if (hasJumpOrLadder)
                {
                    score += 5f; // Bonus for having a jump/ladder
                    if (specialEdgeTargetVisits < 3)
                        score += 8f; // Big bonus if destination is barely visited
                }

                // Per-bot explored area penalty — heavily penalize cells this bot has already visited
                if (exploredCells != null)
                {
                    long nodeCell = GridKey(
                        Mathf.FloorToInt(node.Position.x / GRID_CELL),
                        Mathf.FloorToInt(node.Position.y / GRID_CELL),
                        Mathf.FloorToInt(node.Position.z / GRID_CELL));
                    if (exploredCells.Contains(nodeCell))
                        score -= 20f; // Heavy penalty — this bot already explored here
                }

                // Anti-clustering
                if (avoidPos.sqrMagnitude > 0.01f)
                {
                    float avoidDist = Vector3.Distance(node.Position, avoidPos);
                    if (avoidDist < 15f)
                        score -= (15f - avoidDist) * 0.5f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = node;
                }
            }

            return best;
        }

        /// <summary>
        /// Get a random known node for wandering. Biased toward less-visited nodes.
        /// </summary>
        public NavNode GetRandomWanderNode(Vector3 currentPos, float minDist = 5f)
        {
            if (Nodes.Count == 0) return null;

            NavNode best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < 10; i++)
            {
                var node = Nodes[UnityEngine.Random.Range(0, Nodes.Count)];
                if (node.Confidence <= 0) continue;
                float dist = Vector3.Distance(node.Position, currentPos);
                if (dist < minDist) continue;
                float score = dist * 0.1f - node.VisitCount * 0.5f + node.Confidence;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = node;
                }
            }

            if (best != null) return best;
            // Fallback: pick any live node
            for (int i = 0; i < Mathf.Min(20, Nodes.Count); i++)
            {
                var fallback = Nodes[UnityEngine.Random.Range(0, Nodes.Count)];
                if (fallback.Confidence > 0) return fallback;
            }
            return null;
        }

        public NavNode GetNodeById(int id)
        {
            if (id >= 0 && id < Nodes.Count && Nodes[id].Id == id)
                return Nodes[id];
            foreach (var n in Nodes)
                if (n.Id == id) return n;
            return null;
        }
    }
}
