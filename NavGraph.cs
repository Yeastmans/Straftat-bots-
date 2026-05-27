using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StraftatBots
{
    public enum EdgeType : byte
    {
        Walk = 0,
        Jump = 1,
        Ladder = 2,
        Fall = 3,
        Slide = 4,
        WallJump = 5,
        Teleporter = 6
    }

    public enum NavMode : byte
    {
        Training = 0,   // Records new nodes from players + bots, builds graph
        Play = 1        // No new nodes, uses existing graph, still does confidence feedback
    }

    public class NavNode
    {
        public int Id;
        public Vector3 Position;
        public float Confidence;    // 0-1, decays on bot death, restored by player traversal
        public int VisitCount;
        public bool PlayerSourced;  // true if a player contributed to this node
        public bool NearEdge;       // true if within 1.5m of a ledge/drop — pathfinding penalizes
        public float LastVisitTime; // Time.time of last successful visit — used by unreachable-pruner

        public NavNode(int id, Vector3 pos)
        {
            Id = id;
            Position = pos;
            Confidence = 1f;
            VisitCount = 0;
            PlayerSourced = false;
            NearEdge = false;
            LastVisitTime = Time.time;
        }
    }

    public class NavEdge
    {
        public int From;
        public int To;
        public EdgeType Type;
        public float Confidence;    // 0-1, decays on failure
        public int SuccessCount;
        public int FailCount;
        public float Cost;          // base cost = distance, modified by confidence

        // Jump trajectory data — recorded from successful jumps/falls
        public Vector3 TakeoffDir;              // Horizontal move direction at takeoff
        public float TakeoffSpeed;              // Horizontal speed at takeoff
        public Vector3[] AirPositions;          // Absolute positions sampled every TRAJECTORY_INTERVAL during air phase
        public float[] AirTimestamps;           // Time offset from takeoff for each position sample
        public int AirSampleCount;              // Number of valid samples in AirPositions/AirTimestamps
        public float LockedSpeed;               // Exact speed that worked (0 = not locked)
        public float LockedAirTime;             // Exact air time that worked (0 = not locked)

        // Legacy — kept for loading old data, not used in new system
        public Vector3[] AirWaypoints;

        // Retry-with-variance: when a Jump edge fails, the variant bit for the approach
        // the bot just used is marked. 4 bits = 4 variants (fast+straight, slow+straight,
        // fast+angled, slow+angled). Only after all 4 have been tried and still fail does
        // the edge get soft-pruned. Lets bots learn jumps by genuinely trying different things.
        public byte TriedVariants;          // bitmask over 4 approach variants
        public byte CurrentVariant;         // 0-3, what the next traversal should try

        // Multi-bot fail consensus: bit i is set if BotId i has failed this edge.
        // An edge only dies from falls if ≥2 distinct bots have failed it, so a single
        // bot stuck in a loop can't murder an otherwise-good edge. Fallback path keeps
        // single-bot testing viable (10-fail override kicks in regardless).
        public uint FailedBotMask;

        public NavEdge(int from, int to, EdgeType type, float dist)
        {
            From = from;
            To = to;
            Type = type;
            Confidence = 1f;
            SuccessCount = 1;
            FailCount = 0;
            Cost = dist;
            TriedVariants = 0;
            CurrentVariant = 0;
            FailedBotMask = 0u;
        }
    }

    /// <summary>
    /// A named ordered sequence of node IDs captured during a "Watch Me" demo.
    /// Bots prefer edges that appear in any ProvenRoute (discount applied in A*).
    /// </summary>
    public class ProvenRoute
    {
        public string Name;
        public int[] NodeIds;
        public float TotalTime;
        public int UseCount;

        public ProvenRoute(string name, int[] nodeIds, float totalTime)
        {
            Name = name ?? string.Empty;
            NodeIds = nodeIds ?? System.Array.Empty<int>();
            TotalTime = totalTime;
            UseCount = 0;
        }
    }

    public partial class NavGraph
    {
        public static NavGraph Instance { get; private set; }

        // v4: adds ProvenRoutes section at end of file (after patrol IDs).
        private const int FILE_VERSION = 4;
        private const float BASE_MERGE_RADIUS = 1.0f;         // Min spacing between nodes (scaled by density)
        private const float BASE_CLUSTER_MERGE = 1.5f;      // Cluster merge radius (scaled by density)
        private const float BASE_NeighborRadius = 2.0f;    // Connection range (scaled by density)
        private const int MAX_EDGES_PER_NODE = 4;            // Cap outgoing walk edges per node

        /// <summary>Fixed merge radii — density slider removed (always = 1.0x).</summary>
        private const float MergeRadius = BASE_MERGE_RADIUS;
        private const float ClusterMergeRadius = BASE_CLUSTER_MERGE;
        private const float NeighborRadius = BASE_NeighborRadius;
        private const float CONFIDENCE_DEATH_PENALTY = 0.35f;
        private const float CONFIDENCE_STUCK_PENALTY = 0.15f;
        private const float CONFIDENCE_SUCCESS_BOOST = 0.15f;
        private const float CONFIDENCE_PLAYER_BOOST = 0.15f;  // Extra boost for player-sourced data
        private const float CONFIDENCE_DELETE_THRESHOLD = 0.05f;
        // Fixed cap — was Plugin.MaxNodes slider. Bot nodes pruned first when over capacity.
        private const int MaxNodes = Plugin.MAX_NODES;
        private static readonly int MaxPlayerNodes = (int)(Plugin.MAX_NODES * 0.6f);
        private static readonly int MaxBotNodesCap = (int)(Plugin.MAX_NODES * 0.4f);
        private static readonly int PruneTarget = (int)(Plugin.MAX_NODES * 0.8f);
        private const float PRUNE_CHECK_INTERVAL = 30f;     // Don't prune more than once per 30s

        public List<NavNode> Nodes = new List<NavNode>();
        public List<NavEdge> Edges = new List<NavEdge>();
        private int _playerNodeCount;
        private int _botNodeCount;

        // Spatial lookup: grid cell -> list of node indices
        private Dictionary<long, List<int>> _spatialGrid = new Dictionary<long, List<int>>();
        private const float GRID_CELL = 2f;
        private static readonly Vector3[] _widthCheckDirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
        // Pre-built A* comparer — avoid allocating a new one per FindPath call
        private static readonly Comparer<(float f, int id)> _astarComparer = Comparer<(float f, int id)>.Create(
            (a, b) => a.f != b.f ? a.f.CompareTo(b.f) : a.id.CompareTo(b.id));

        // Edge lookup: from node id -> list of edge indices
        private Dictionary<int, List<int>> _edgesByFrom = new Dictionary<int, List<int>>();
        private Dictionary<int, List<int>> _edgesByTo = new Dictionary<int, List<int>>();

        private string _currentMap;
        private int _nextNodeId;
        private bool _dirty;
        private float _lastPruneTime;
        private float _lastDeclutterTime;

        // Temporary blacklist: node ID -> expiry time. Blacklisted nodes are avoided in pathfinding.
        // If they stay blacklisted long enough with no success, they get permanently deleted.
        private Dictionary<int, float> _tempBlacklist = new Dictionary<int, float>();
        private const float BL_DURATION_NORMAL = 30f;
        private static float BlacklistDuration => BL_DURATION_NORMAL;
        private const int BLACKLIST_STRIKES_TO_DELETE = 3;     // 3 blacklists = permanent delete
        private Dictionary<int, int> _blacklistStrikes = new Dictionary<int, int>();

        public NavMode Mode { get; set; } = NavMode.Training;
        public string CurrentMap => _currentMap;
        public bool HasData => Nodes.Count > 0;
        /// <summary>When true, no nodes/edges are created, deleted, modified, or pruned.</summary>
        public static bool IsLocked => Plugin.LockGraph?.Value ?? false;
        public int NodeCount => Nodes.Count;
        public int EdgeCount => Edges.Count;

        // Cached routes between key locations (spawns, weapons)
        private Dictionary<(int from, int to), List<NavNode>> _routeCache = new Dictionary<(int, int), List<NavNode>>();

        // Fixed map locations — spawns and weapon spawners, added on load
        public List<(Vector3 pos, string label, int nodeId)> MapLocations = new List<(Vector3, string, int)>();

        // v4: demonstrated player routes (from "Watch Me" button). Pathfinder discounts edges that lie on any of these.
        public List<ProvenRoute> ProvenRoutes = new List<ProvenRoute>();
        private HashSet<long> _provenEdgeSet = new HashSet<long>();

        /// <summary>Pack two node IDs into a single long for fast edge lookups.</summary>
        private static long ProvenKey(int from, int to) => ((long)(uint)from << 32) | (uint)to;

        /// <summary>Rebuild the proven-edge lookup set from the ProvenRoutes list. O(total route length).</summary>
        public void RebuildProvenEdgeSet()
        {
            _provenEdgeSet.Clear();
            foreach (var r in ProvenRoutes)
            {
                if (r?.NodeIds == null || r.NodeIds.Length < 2) continue;
                for (int i = 0; i < r.NodeIds.Length - 1; i++)
                    _provenEdgeSet.Add(ProvenKey(r.NodeIds[i], r.NodeIds[i + 1]));
            }
        }

        /// <summary>True if the edge (from -> to) appears in any saved ProvenRoute.</summary>
        public bool IsProvenEdge(int from, int to) => _provenEdgeSet.Contains(ProvenKey(from, to));

        /// <summary>
        /// Add a demonstrated route. Rejects empty/degenerate input.
        /// Marks the graph dirty so it saves on the next autosave cycle.
        /// </summary>
        public void AddProvenRoute(string name, List<int> nodeIds, float totalTime)
        {
            if (nodeIds == null || nodeIds.Count < 2) return;
            // Collapse consecutive duplicates.
            var cleaned = new List<int>(nodeIds.Count);
            int prev = -1;
            foreach (int id in nodeIds)
            {
                if (id == prev) continue;
                cleaned.Add(id);
                prev = id;
            }
            if (cleaned.Count < 2) return;

            var route = new ProvenRoute(name ?? $"Route_{ProvenRoutes.Count + 1}", cleaned.ToArray(), totalTime);
            ProvenRoutes.Add(route);
            RebuildProvenEdgeSet();
            _dirty = true;
            Plugin.Log.LogInfo($"[NavGraph] Added ProvenRoute '{route.Name}' ({route.NodeIds.Length} nodes, {totalTime:F1}s)");
        }

        /// <summary>Remove all saved ProvenRoutes. UI hook.</summary>
        public void ClearProvenRoutes()
        {
            int n = ProvenRoutes.Count;
            ProvenRoutes.Clear();
            _provenEdgeSet.Clear();
            if (n > 0) _dirty = true;
            Plugin.Log.LogInfo($"[NavGraph] Cleared {n} ProvenRoutes");
        }

        /// <summary>Check if a node is a weapon/spawn map location — NEVER delete these.
        /// PatrolPoint nodes are excluded — they should be clearable.</summary>
        public bool IsMapLocation(int nodeId)
        {
            foreach (var (pos, label, id) in MapLocations)
                if (id == nodeId && label != "PatrolPoint") return true;
            return false;
        }

        public static void Init()
        {
            if (Instance == null)
                Instance = new NavGraph();
        }

        /// <summary>
        /// Load graph for a map. Call on scene load.
        /// </summary>
        public void LoadForMap(string mapName)
        {
            _currentMap = mapName;
            Nodes.Clear();
            Edges.Clear();
            _spatialGrid.Clear();
            _edgesByFrom.Clear();
            _edgesByTo.Clear();
            _nextNodeId = 0;
            _dirty = false;
            _lastPruneTime = 0f;
            _lastDeclutterTime = 0f;
            _playerNodeCount = 0;
            _botNodeCount = 0;
            _tempBlacklist.Clear();
            _blacklistStrikes.Clear();
            _routeCache.Clear();
            MapLocations.Clear();
            ProvenRoutes.Clear();
            _provenEdgeSet.Clear();

            string path = GetFilePath(mapName);
            if (File.Exists(path))
            {
                try
                {
                    LoadFromFile(path);
                    Plugin.Log.LogInfo($"[NavGraph] Loaded {Nodes.Count} nodes, {Edges.Count} edges for {mapName} (mode: {Mode})");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[NavGraph] Failed to load {path}: {ex.Message}");
                    Nodes.Clear();
                    Edges.Clear();
                    _spatialGrid.Clear();
                    _edgesByFrom.Clear();
            _edgesByTo.Clear();
                    _nextNodeId = 0;
                    _playerNodeCount = 0;
                    _botNodeCount = 0;
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[NavGraph] No data for {mapName}, starting fresh (mode: {Mode})");
            }
        }

        /// <summary>
        /// Save current graph to disk. Compacts dead data first.
        /// </summary>
        /// <summary>Force save even if not marked dirty.</summary>
        public void ForceSave() { _dirty = true; Save(); }

        public void Save()
        {
            if (!_dirty || string.IsNullOrEmpty(_currentMap)) return;

            // Only optimize graph during training — Play preserves all data
            if (Mode == NavMode.Training)
            {
                AutoDeclutter();
                SimplifyPaths();
                BuildZones();
            }
            Compact(); // Always compact dead nodes on save

            string path = GetFilePath(_currentMap);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                SaveToFile(path);
                _dirty = false;
                Plugin.Log.LogInfo($"[NavGraph] Saved {Nodes.Count} nodes, {Edges.Count} edges for {_currentMap}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NavGraph] Failed to save: {ex.Message}");
            }
        }

        // ========== NODE OPERATIONS ==========

        /// <summary>
        /// Add a position to the graph. Merges with nearby existing node if within MERGE_RADIUS.
        /// isPlayer=true gives higher confidence weight. Bot nodes start at very low confidence
        /// and converge toward player paths over time.
        /// Validates ground beneath position — rejects mid-air nodes.
        /// In Play mode, only reinforces existing nodes (no new nodes created).
        /// </summary>
        /// <summary>
        /// Add a node without auto-connecting walk edges. Used by NavGraphGenerator
        /// which handles connectivity separately after all nodes are placed.
        /// </summary>
        public NavNode AddNodeRaw(Vector3 pos)
        {
            pos = SnapToGround(pos);

            // Nudge out of geometry — if capsule overlaps a wall, push away from it
            Vector3 capBot = pos + Vector3.up * 0.45f;
            Vector3 capTop = pos + Vector3.up * 1.6f;
            if (Physics.CheckCapsule(capBot, capTop, 0.35f, VALID_WALL_MASK, QueryTriggerInteraction.Ignore))
            {
                // Find which direction is clear by testing 8 directions
                bool nudged = false;
                for (float dist = 0.3f; dist <= 1.2f; dist += 0.3f)
                {
                    if (nudged) break;
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * 45f * Mathf.Deg2Rad;
                        Vector3 nudge = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * dist;
                        Vector3 testPos = pos + nudge;
                        testPos = SnapToGround(testPos);
                        Vector3 tBot = testPos + Vector3.up * 0.45f;
                        Vector3 tTop = testPos + Vector3.up * 1.6f;
                        if (!Physics.CheckCapsule(tBot, tTop, 0.35f, VALID_WALL_MASK, QueryTriggerInteraction.Ignore))
                        {
                            pos = testPos;
                            nudged = true;
                            break;
                        }
                    }
                }
                if (!nudged) return null; // Can't find clear position — skip this node
            }

            var existing = FindNearestNode(pos, NODE_MERGE_RADIUS);
            if (existing != null) return existing; // Already exists

            var node = new NavNode(_nextNodeId++, pos);
            node.Confidence = 1f;
            node.PlayerSourced = true;
            node.NearEdge = false;
            Nodes.Add(node);
            AddToSpatialGrid(node);
            _playerNodeCount++;
            _dirty = true;
            return node;
        }
        private const float NODE_MERGE_RADIUS = 1.5f;

        public NavNode AddPosition(Vector3 pos, bool isPlayer = false, bool force = false)
        {
            // Locked (Freeze Map Data) blocks learned path edits, but forced map
            // infrastructure such as spawn/weapon/teleporter nodes must still exist.
            if (IsLocked && !force)
                return FindNearestNode(pos, 5f);

            // Bots use wider merge radius — consolidate bot data, don't create redundant nodes
            float mergeRadius = isPlayer ? BASE_MERGE_RADIUS : (BASE_MERGE_RADIUS * 1.5f);

            // Find existing nearby node
            NavNode existing = FindNearestNode(pos, mergeRadius);
            if (existing != null)
            {
                existing.VisitCount++;
                float w = 1f / (existing.VisitCount + 1);
                existing.Position = Vector3.Lerp(existing.Position, pos, w);
                existing.Position = SnapToGround(existing.Position); // Keep flush with ground
                float boost = isPlayer ? CONFIDENCE_PLAYER_BOOST : 0.005f;
                existing.Confidence = Mathf.Min(1f, existing.Confidence + boost);
                if (isPlayer) existing.PlayerSourced = true;
                _dirty = true;
                return existing;
            }

            // Play mode: read-only. force=true bypasses this (used by RegisterMapLocations for weapon nodes)
            if (Mode == NavMode.Play && !force)
                return FindNearestNode(pos, NeighborRadius * 2);

            // Bot node creation: only if no player path nearby
            // Bots should follow player paths first, only create nodes far from player data
            if (!isPlayer)
            {
                var nearPlayerNode = FindNearestNode(pos, 3f);
                if (nearPlayerNode != null && nearPlayerNode.PlayerSourced)
                {
                    // Close to a player path — just reinforce the player node, don't create new
                    nearPlayerNode.VisitCount++;
                    nearPlayerNode.Confidence = Mathf.Min(1f, nearPlayerNode.Confidence + 0.005f);
                    return nearPlayerNode;
                }
            }

            // Ground validation + snap to ground surface
            if (!ValidateGround(pos))
                return FindNearestNode(pos, NeighborRadius * 2);
            pos = SnapToGround(pos);

            // Check cached caps — skip entirely when graph is frozen
            if (!IsLocked && Mode == NavMode.Training)
            {
                if (isPlayer && _playerNodeCount >= MaxPlayerNodes)
                {
                    if (Time.time - _lastPruneTime > PRUNE_CHECK_INTERVAL) Prune();
                    if (_playerNodeCount >= MaxPlayerNodes) return FindNearestNode(pos, NeighborRadius * 2);
                }
                else if (!isPlayer && _botNodeCount >= MaxBotNodesCap)
                {
                    if (Time.time - _lastPruneTime > PRUNE_CHECK_INTERVAL) Prune();
                    if (_botNodeCount >= MaxBotNodesCap) return FindNearestNode(pos, NeighborRadius * 2);
                }
            }

            // Create new node
            var node = new NavNode(_nextNodeId++, pos);
            node.Confidence = isPlayer ? 1f : 0.2f;
            node.PlayerSourced = isPlayer;
            node.NearEdge = CheckNearEdge(pos);
            Nodes.Add(node);
            AddToSpatialGrid(node);
            if (isPlayer) _playerNodeCount++; else _botNodeCount++;
            _dirty = true;

            // Re-check nearby NearEdge nodes — this new node may be the "walkable below" they were missing
            RevalidateNearEdgeNodes(pos, 8f);

            // Auto-connect to nearby nodes with walk edges
            // Cap edges per node to prevent dense webbing
            var nearby = FindNodesInRadius(pos, NeighborRadius);
            int edgesAdded = 0;
            foreach (var n in nearby)
            {
                if (n.Id == node.Id) continue;
                if (edgesAdded >= MAX_EDGES_PER_NODE) break;

                // Skip if target node already has too many connections
                int targetEdgeCount = 0;
                if (_edgesByFrom.TryGetValue(n.Id, out var existingEdges))
                    targetEdgeCount = existingEdges.Count;
                if (targetEdgeCount >= MAX_EDGES_PER_NODE) continue;

                float dist = Vector3.Distance(n.Position, pos);
                float heightDiff = Mathf.Abs(n.Position.y - pos.y);
                float hdx = n.Position.x - pos.x, hdz = n.Position.z - pos.z;
                float horizDist = Mathf.Sqrt(hdx * hdx + hdz * hdz);

                // Walk edges: gentle slopes only. Steep climbs/drops need Jump/Fall edges.
                bool isWalkable = heightDiff < 0.8f ||
                    (horizDist > 0.5f && heightDiff / horizDist < 1.0f && heightDiff < 1.5f);

                if (isWalkable && ValidateEdgeGround(pos, n.Position) && ValidateLineOfSight(pos, n.Position))
                {
                    AddEdge(node.Id, n.Id, EdgeType.Walk, dist);
                    AddEdge(n.Id, node.Id, EdgeType.Walk, dist);
                    edgesAdded++;
                }
                // No auto-jump creation here — jump edges only come from:
                // 1. Player/bot actually jumping (PlayerRecorder)
                // 2. ReportFallOnEdge (bot fell here twice)
                // 3. DetectJumpEdges (gap scan on load)
            }

            return node;
        }

        /// <summary>
        /// Check that there's solid ground beneath a position. Rejects mid-air positions.
        /// Uses multiple raycasts for robustness.
        /// </summary>
        private static readonly int VALID_WALL_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);
        private static readonly int VALID_GROUND_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 14);
        private static readonly Collider[] _validateBuffer = new Collider[4];

        /// <summary>
        /// Check if position is within 1.5m of a dangerous ledge/drop.
        /// Returns true only for DANGEROUS edges (void, killzone, unknown terrain below).
        /// Safe drops (walkable nodes below) are NOT flagged — bots can use them intentionally.
        /// </summary>
        private bool CheckNearEdge(Vector3 pos)
        {
            float checkDist = 0.6f;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                Vector3 checkPos = pos + dir * checkDist + Vector3.up * 3f;
                if (Physics.Raycast(checkPos, Vector3.down, out RaycastHit hit, 103f, VALID_GROUND_MASK, QueryTriggerInteraction.Ignore))
                {
                    float dropHeight = pos.y - hit.point.y;
                    if (dropHeight > 3f)
                    {
                        // Big drop — check wide area below for any nav nodes
                        // Cast a wide net: 5m radius at landing point catches sparse node areas
                        var nodesBelow = FindNodesInRadius(hit.point, 5f);
                        bool safeLanding = false;
                        foreach (var n in nodesBelow)
                        {
                            if (n.Confidence <= 0.1f) continue;
                            // Node must be near the landing height (not way above/below)
                            float heightFromLanding = Mathf.Abs(n.Position.y - hit.point.y);
                            if (heightFromLanding < 3f)
                            {
                                safeLanding = true;
                                break;
                            }
                        }
                        if (safeLanding)
                            continue; // Known walkable area below — safe edge

                        // Also check: is the landing on a killzone?
                        if (hit.collider.CompareTag("Killz") || hit.collider.CompareTag("DamageZone"))
                            return true; // Lethal below

                        return true; // No nodes below = dangerous unknown
                    }
                }
                else
                {
                    return true; // No ground at all = void
                }
            }
            return false;
        }

        /// <summary>
        /// Re-check NearEdge on nodes near a newly created node.
        /// The new node may provide the "walkable below" evidence that clears their edge flag.
        /// </summary>
        private void RevalidateNearEdgeNodes(Vector3 newNodePos, float radius)
        {
            var nearby = FindNodesInRadius(newNodePos, radius);
            foreach (var n in nearby)
            {
                if (!n.NearEdge || n.Confidence <= 0f) continue;
                bool wasEdge = n.NearEdge;
                n.NearEdge = CheckNearEdge(n.Position);
                // Also clear if proven jump takeoff
                if (n.NearEdge)
                    n.NearEdge = !HasSuccessfulJumpFrom(n.Id);
                if (wasEdge && !n.NearEdge)
                    Plugin.Log.LogInfo($"[NavGraph] Node {n.Id} cleared NearEdge after new node discovered nearby");
            }
        }

        /// <summary>
        /// Returns true if this node is the origin of a successful jump/fall/walljump edge.
        /// Successful = confidence > 0 and at least 1 success.
        /// </summary>
        private bool HasSuccessfulJumpFrom(int nodeId)
        {
            var edges = GetEdgesFrom(nodeId);
            if (edges == null) return false;
            foreach (var e in edges)
            {
                if ((e.Type == EdgeType.Jump || e.Type == EdgeType.Fall || e.Type == EdgeType.WallJump)
                    && e.Confidence > 0f && e.SuccessCount >= 1)
                    return true;
            }
            return false;
        }

        private static bool ValidateGround(Vector3 pos)
        {
            // Primary: raycast down — ground must be within 1.5m (not floating)
            if (!Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 1.5f))
                return false;

            // Surface must be walkable — CC slopeLimit is 65°, cos(65°) ≈ 0.42
            if (hit.normal.y < 0.42f) return false;

            // Secondary: not inside a wall
            int n = Physics.OverlapSphereNonAlloc(pos + Vector3.up * 1f, 0.3f, _validateBuffer, VALID_WALL_MASK, QueryTriggerInteraction.Ignore);
            if (n > 0) return false;

            return true;
        }

        /// <summary>
        /// Snap a position to the ground surface directly below it.
        /// Returns original position if no ground found (shouldn't happen after ValidateGround).
        /// Adds a tiny offset (0.05m) so the node sits just above the surface, not inside it.
        /// </summary>
        private static Vector3 SnapToGround(Vector3 pos)
        {
            if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f,
                VALID_GROUND_MASK, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(pos.x, hit.point.y + 0.05f, pos.z);
            }
            return pos;
        }

        /// <summary>
        /// Check that a bot can physically walk between two nodes.
        /// Uses CapsuleCast (sweeps the bot's body shape) + midpoint ground check.
        /// </summary>
        private static bool ValidateEdgeGround(Vector3 a, Vector3 b)
        {
            // Check ground exists at 3 points along the edge (25%, 50%, 75%)
            // and that ground is near the expected height (not a chasm below)
            for (float t = 0.25f; t <= 0.75f; t += 0.25f)
            {
                float x = a.x + (b.x - a.x) * t;
                float y = a.y + (b.y - a.y) * t;
                float z = a.z + (b.z - a.z) * t;

                if (!Physics.Raycast(new Vector3(x, y + 0.5f, z), Vector3.down, out RaycastHit hit, 3f,
                    VALID_GROUND_MASK, QueryTriggerInteraction.Ignore))
                    return false; // No ground at all

                if (y - hit.point.y > 1.5f)
                    return false; // Ground way below expected height = gap
            }
            return true;
        }

        // Public wrappers for PlayerRecorder
        public bool ValidateEdgeGroundPublic(Vector3 a, Vector3 b) => ValidateEdgeGround(a, b);
        public bool ValidateLineOfSightPublic(Vector3 a, Vector3 b) => ValidateLineOfSight(a, b);
        public bool CheckNearEdgePublic(Vector3 pos) => CheckNearEdge(pos);

        /// <summary>
        /// Check that there's no solid wall between two positions.
        /// Raycasts at foot and waist height to catch walls the bot can't walk through.
        /// </summary>
        private static bool ValidateLineOfSight(Vector3 a, Vector3 b)
        {
            Vector3 dir = b - a;
            float distSqr = dir.sqrMagnitude;
            if (distSqr < 0.01f) return true;
            float dist = Mathf.Sqrt(distSqr);
            float invDist = 1f / dist;
            dir *= invDist;

            // Check at 3 heights — feet, waist, head — catches thin walls and door frames
            if (Physics.Raycast(a + Vector3.up * 0.3f, dir, dist, VALID_WALL_MASK, QueryTriggerInteraction.Ignore))
                return false;
            if (Physics.Raycast(a + Vector3.up * 0.8f, dir, dist, VALID_WALL_MASK, QueryTriggerInteraction.Ignore))
                return false;
            if (Physics.Raycast(a + Vector3.up * 1.5f, dir, dist, VALID_WALL_MASK, QueryTriggerInteraction.Ignore))
                return false;

            return true;
        }

        /// <summary>
        /// Scan all nodes and delete any that are floating (no solid ground beneath).
        /// Call after scene geometry is loaded. Also validates walk edges.
        /// </summary>
        public void ValidateAllNodes()
        {
            int removedNodes = 0;
            int removedEdges = 0;

            // Build protected set — patrol route nodes + visited patrol nodes must survive
            var protectedIds = new HashSet<int>(_patrolVisitedNodes);
            foreach (var route in _patrolRoutes.Values)
                foreach (var n in route)
                    protectedIds.Add(n.Id);
            foreach (var (_, _, nodeId) in MapLocations)
                protectedIds.Add(nodeId);

            // Phase 1: Remove floating nodes + snap survivors to ground
            int snappedNodes = 0;
            foreach (var node in Nodes)
            {
                if (node.Confidence <= 0) continue;
                if (!ValidateGround(node.Position) && !protectedIds.Contains(node.Id))
                {
                    node.Confidence = -1f;
                    removedNodes++;
                }
                else
                {
                    // Snap to ground surface — fix nodes drifted above geometry
                    Vector3 snapped = SnapToGround(node.Position);
                    if (Mathf.Abs(snapped.y - node.Position.y) > 0.1f)
                    {
                        node.Position = snapped;
                        snappedNodes++;
                    }
                }
            }

            // Phase 2: Validate ALL edges
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0) continue;
                var fromNode = GetNodeById(edge.From);
                var toNode = GetNodeById(edge.To);
                if (fromNode == null || toNode == null || fromNode.Confidence <= 0 || toNode.Confidence <= 0)
                {
                    edge.Confidence = -1f;
                    removedEdges++;
                    continue;
                }

                float heightDiff = Mathf.Abs(fromNode.Position.y - toNode.Position.y);
                float horizDist = new Vector3(fromNode.Position.x - toNode.Position.x, 0,
                    fromNode.Position.z - toNode.Position.z).magnitude;

                // Remove impossible jump edges — too far, too high, or through walls
                if (edge.Type == EdgeType.Jump || edge.Type == EdgeType.Fall)
                {
                    float totalDist = Vector3.Distance(fromNode.Position, toNode.Position);
                    float maxJump = Plugin.GetMaxJumpDist();
                    if (totalDist > maxJump || heightDiff > maxJump * 0.5f || horizDist > maxJump
                        || (heightDiff > 3f && horizDist < 1f))
                    {
                        edge.Confidence = -1f;
                        removedEdges++;
                        continue;
                    }
                }

                // Walk edges: check ground + no walls between
                if (edge.Type == EdgeType.Walk)
                {
                    if (!ValidateEdgeGround(fromNode.Position, toNode.Position) ||
                        !ValidateLineOfSight(fromNode.Position, toNode.Position))
                    {
                        edge.Confidence = -1f;
                        removedEdges++;
                    }
                }
            }

            // Phase 3: Remove Walk edges that duplicate a Jump/Fall/Ladder edge between same nodes
            var specialPairs = new HashSet<long>();
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0) continue;
                if (edge.Type != EdgeType.Walk)
                    specialPairs.Add((long)edge.From << 32 | (uint)edge.To);
            }
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0 || edge.Type != EdgeType.Walk) continue;
                long key = (long)edge.From << 32 | (uint)edge.To;
                if (specialPairs.Contains(key))
                {
                    edge.Confidence = -1f;
                    removedEdges++;
                }
            }

            if (removedNodes > 0 || removedEdges > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Validation: removed {removedNodes} floating nodes, {removedEdges} bad edges");
                _dirty = true;
            }

            // Phase 4: Recalculate NearEdge flags for all nodes
            int edgeNodes = 0;
            foreach (var node in Nodes)
            {
                if (node.Confidence <= 0) continue;
                node.NearEdge = CheckNearEdge(node.Position);
                // Clear NearEdge if this node is the takeoff of a proven jump/fall/walljump
                if (node.NearEdge)
                    node.NearEdge = !HasSuccessfulJumpFrom(node.Id);
                if (node.NearEdge) edgeNodes++;
            }
            if (edgeNodes > 0)
                Plugin.Log.LogInfo($"[NavGraph] {edgeNodes} nodes marked near edges");
        }

        /// <summary>
        /// <summary>
        /// Lightweight edge creation for NavGraphGenerator. Uses AddNodeRaw instead of AddPosition
        /// to avoid triggering RevalidateNearEdgeNodes and auto-connect during scan.
        /// </summary>
        public NavEdge AddSpecialEdgeRaw(Vector3 fromPos, Vector3 toPos, EdgeType type)
        {
            var fromNode = AddNodeRaw(fromPos);
            var toNode = AddNodeRaw(toPos);
            if (fromNode == null || toNode == null) return null;
            if (fromNode.Id == toNode.Id) return null;

            // Check existing
            var existing = GetEdgeBetween(fromNode.Id, toNode.Id);
            if (existing != null) return existing;

            float dist = Vector3.Distance(fromPos, toPos);
            var edge = new NavEdge(fromNode.Id, toNode.Id, type, dist);
            edge.Confidence = 1f;
            edge.SuccessCount = 5;
            Edges.Add(edge);
            int idx = Edges.Count - 1;
            if (!_edgesByFrom.ContainsKey(fromNode.Id))
                _edgesByFrom[fromNode.Id] = new List<int>();
            _edgesByFrom[fromNode.Id].Add(idx);
            _dirty = true;
            return edge;
        }

        /// Add a directional edge (jump, ladder, fall, slide). Walk edges are auto-created.
        /// Jump/fall origins and landings must be on solid ground.
        /// In Play mode, only reinforces existing edges.
        /// </summary>
        public NavEdge AddSpecialEdge(Vector3 fromPos, Vector3 toPos, EdgeType type, bool isPlayer = false)
        {
            if (IsLocked) return null;

            // WALKABILITY GATE: if both points are connected by walkable ground,
            // don't create any special edge — walk edges handle it
            if (type == EdgeType.Jump || type == EdgeType.Fall || type == EdgeType.WallJump)
            {
                if (ValidateEdgeGround(fromPos, toPos) && ValidateLineOfSight(fromPos, toPos))
                    return null; // Walkable — no special edge needed
            }

            // Validate ground for bot-created jump/fall edges only
            // Player edges skip validation — the player was physically there
            if (!isPlayer && (type == EdgeType.Jump || type == EdgeType.Fall))
            {
                if (!ValidateGround(fromPos) || !ValidateGround(toPos))
                    return null;
            }

            // Reject impossible jumps — max distance and height limits
            if (type == EdgeType.Jump)
            {
                float heightDiff = Mathf.Abs(toPos.y - fromPos.y);
                float adx = toPos.x - fromPos.x, adz = toPos.z - fromPos.z;
                float horizDist = Mathf.Sqrt(adx * adx + adz * adz);
                float totalDist = Vector3.Distance(fromPos, toPos);

                // Hard limits — configurable max jump distance
                float maxJump = Plugin.GetMaxJumpDist();
                if (totalDist > maxJump) return null;
                if (heightDiff > maxJump * 0.5f) return null;  // Max height = half of max distance
                if (horizDist > maxJump) return null;

                // Bot-only: tighter limits
                if (!isPlayer)
                {
                    if (heightDiff > 3f && horizDist < 1f) return null;
                    if (heightDiff > 5f) return null;
                }
            }

            var fromNode = AddPosition(fromPos, isPlayer);
            var toNode = AddPosition(toPos, isPlayer);
            if (fromNode == null || toNode == null) return null;
            // Force=true for player special edges — these are always critical
            var edge = AddEdge(fromNode.Id, toNode.Id, type, Vector3.Distance(fromPos, toPos), force: isPlayer);

            // Clear NearEdge on takeoff node — proven jump origin should be accessible
            if (edge != null && fromNode.NearEdge &&
                (type == EdgeType.Jump || type == EdgeType.Fall || type == EdgeType.WallJump))
            {
                fromNode.NearEdge = false;
            }

            return edge;
        }

        /// <summary>
        /// Find the nearest node to a position within maxDist.
        /// </summary>
        public NavNode FindNearestNode(Vector3 pos, float maxDist)
        {
            float bestSqr = maxDist * maxDist;
            NavNode best = null;

            int cx = Mathf.FloorToInt(pos.x / GRID_CELL);
            int cz = Mathf.FloorToInt(pos.z / GRID_CELL);
            int cy = Mathf.FloorToInt(pos.y / GRID_CELL);

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long key = GridKey(cx + dx, cy + dy, cz + dz);
                if (!_spatialGrid.TryGetValue(key, out var list)) continue;
                foreach (int idx in list)
                {
                    if (idx < 0 || idx >= Nodes.Count) continue;
                    var node = Nodes[idx];
                    if (node == null || node.Confidence <= 0) continue;
                    float sqr = (node.Position.x - pos.x) * (node.Position.x - pos.x)
                        + (node.Position.y - pos.y) * (node.Position.y - pos.y)
                        + (node.Position.z - pos.z) * (node.Position.z - pos.z);
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = node;
                    }
                }
            }
            return best;
        }

        public List<NavNode> FindNodesInRadius(Vector3 pos, float radius)
        {
            var result = new List<NavNode>();
            float radiusSqr = radius * radius;
            int cx = Mathf.FloorToInt(pos.x / GRID_CELL);
            int cz = Mathf.FloorToInt(pos.z / GRID_CELL);
            int cy = Mathf.FloorToInt(pos.y / GRID_CELL);
            int range = Mathf.CeilToInt(radius / GRID_CELL);

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
                    if (node == null || node.Confidence <= 0) continue;
                    float sqr = (node.Position.x - pos.x) * (node.Position.x - pos.x)
                        + (node.Position.y - pos.y) * (node.Position.y - pos.y)
                        + (node.Position.z - pos.z) * (node.Position.z - pos.z);
                    if (sqr <= radiusSqr)
                        result.Add(node);
                }
            }
            return result;
        }

        /// <summary>
        /// Remove a node by ID. Clears all edges referencing it.
        /// Used by NavGraphGenerator flood fill to prune unreachable nodes.
        /// </summary>
        public void RemoveNode(int nodeId)
        {
            if (nodeId < 0 || nodeId >= Nodes.Count) return;
            var node = Nodes[nodeId];
            if (node == null) return;

            // Collect edge indices to remove (edges from or to this node)
            HashSet<int> toRemove = new HashSet<int>();
            for (int i = 0; i < Edges.Count; i++)
            {
                if (Edges[i].From == nodeId || Edges[i].To == nodeId)
                    toRemove.Add(i);
            }

            // Remove edges in reverse order to preserve indices
            var sorted = new List<int>(toRemove);
            sorted.Sort();
            for (int i = sorted.Count - 1; i >= 0; i--)
                Edges.RemoveAt(sorted[i]);

            // Rebuild edge lookup for affected nodes
            _edgesByFrom.Remove(nodeId);
            _edgesByTo.Remove(nodeId);

            // Null out the node (preserves indices for other nodes)
            Nodes[nodeId] = null;
            _dirty = true;
        }

        // ========== EDGE OPERATIONS ==========

        public NavEdge AddEdge(int from, int to, EdgeType type, float dist, bool force = false)
        {
            if (IsLocked && !force) return null;
            // Check for existing edge
            if (_edgesByFrom.TryGetValue(from, out var fromEdges))
            {
                foreach (int ei in fromEdges)
                {
                    if (ei < Edges.Count && Edges[ei].To == to && Edges[ei].Type == type)
                    {
                        Edges[ei].SuccessCount++;
                        Edges[ei].Confidence = Mathf.Min(1f, Edges[ei].Confidence + CONFIDENCE_SUCCESS_BOOST);
                        _dirty = true;
                        return Edges[ei];
                    }
                }
            }

            // Play mode: read-only (unless forced by RegisterMapLocations etc.)
            if (Mode == NavMode.Play && !force) return null;

            // Reject jump edges that reverse a fall edge — the fall is one-way down,
            // jumping back up from the landing is usually impossible
            if (type == EdgeType.Jump || type == EdgeType.WallJump)
            {
                if (_edgesByFrom.TryGetValue(to, out var reverseEdges))
                {
                    foreach (int ri in reverseEdges)
                    {
                        if (ri < Edges.Count && Edges[ri].To == from
                            && Edges[ri].Type == EdgeType.Fall && Edges[ri].Confidence > 0f)
                            return null; // Reverse of a fall — don't create impossible jump
                    }
                }
            }

            var edge = new NavEdge(from, to, type, dist);
            int edgeIdx = Edges.Count;
            Edges.Add(edge);

            if (!_edgesByFrom.ContainsKey(from))
                _edgesByFrom[from] = new List<int>();
            _edgesByFrom[from].Add(edgeIdx);

            if (!_edgesByTo.ContainsKey(to))
                _edgesByTo[to] = new List<int>();
            _edgesByTo[to].Add(edgeIdx);

            // Coverage: bump the edges-added counter + stamp last-edge-time for the
            // cell holding the 'from' node. Feeds novelty scoring and saturation.
            var fromNodeForCoverage = GetNodeById(from);
            if (fromNodeForCoverage != null) TouchCellEdgeAdded(fromNodeForCoverage.Position);

            _dirty = true;
            return edge;
        }

        public List<NavEdge> GetEdgesFrom(int nodeId)
        {
            var result = new List<NavEdge>();
            if (_edgesByFrom.TryGetValue(nodeId, out var indices))
            {
                foreach (int i in indices)
                    if (i < Edges.Count && Edges[i].Confidence > 0) result.Add(Edges[i]);
            }
            return result;
        }

        public NavEdge GetEdgeBetween(int fromId, int toId)
        {
            if (fromId < 0 || toId < 0) return null;
            if (_edgesByFrom.TryGetValue(fromId, out var indices))
            {
                foreach (int i in indices)
                    if (i < Edges.Count && Edges[i].To == toId && Edges[i].Confidence > 0)
                        return Edges[i];
            }
            return null;
        }

        // >>> Confidence/feedback/blacklist methods moved to NavGraph.Maintenance.cs

        // ========== TROUBLE ZONE DETECTION ==========

        /// <summary>
        /// <summary>
        /// Remove all player-sourced nodes and their edges.
        /// </summary>
        public void ClearPlayerNodes()
        {
            foreach (var node in Nodes)
                if (node.PlayerSourced && !IsMapLocation(node.Id)) node.Confidence = -1f;
            Compact();
            _dirty = true;
            Plugin.Log.LogInfo($"[NavGraph] Cleared player nodes. {Nodes.Count} nodes remain.");
        }

        /// <summary>
        /// Remove all bot-sourced (non-player) nodes and their edges.
        /// </summary>
        public void ClearBotNodes()
        {
            foreach (var node in Nodes)
                if (!node.PlayerSourced && !IsMapLocation(node.Id)) node.Confidence = -1f;
            Compact();
            _dirty = true;
            Plugin.Log.LogInfo($"[NavGraph] Cleared bot nodes. {Nodes.Count} nodes remain.");
        }

        /// <summary>
        /// Remove all special edges (Jump, Fall, Slide, WallJump, Ladder) and nodes that are ONLY
        /// connected by special edges (no walk edges). Walk nodes/edges preserved.
        /// </summary>
        public void ClearSpecialEdges()
        {
            int removedEdges = 0;
            int removedNodes = 0;

            // Kill all special edges (except Teleporter — those are map infrastructure)
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0f) continue;
                if (edge.Type != EdgeType.Walk && edge.Type != EdgeType.Teleporter)
                {
                    edge.Confidence = -1f;
                    removedEdges++;
                }
            }

            // Kill nodes that have NO remaining walk edges (orphaned special-only nodes)
            foreach (var node in Nodes)
            {
                if (node.Confidence <= 0f) continue;
                // Check if this node has any surviving walk edge
                bool hasWalk = false;
                if (_edgesByFrom.TryGetValue(node.Id, out var fromEdges))
                    foreach (int ei in fromEdges)
                        if (ei < Edges.Count && Edges[ei].Confidence > 0f) { hasWalk = true; break; }
                if (!hasWalk && _edgesByTo.TryGetValue(node.Id, out var toEdges))
                    foreach (int ei in toEdges)
                        if (ei < Edges.Count && Edges[ei].Confidence > 0f) { hasWalk = true; break; }

                // Also keep map locations and patrol nodes
                if (!hasWalk && !IsMapLocation(node.Id) && !IsPatrolProtected(node.Id))
                {
                    node.Confidence = -1f;
                    removedNodes++;
                }
            }

            // Clear patrol visited set since special edges are gone
            _patrolVisitedNodes.Clear();

            Compact();
            _dirty = true;
            Plugin.Log.LogInfo($"[NavGraph] Cleared special edges: {removedEdges} edges, {removedNodes} orphan nodes removed. {Nodes.Count} nodes, {Edges.Count} edges remain.");
        }

        // ========== GROUND SCAN — SEED NODES FROM RAYCASTS ==========

        /// <summary>
        /// Scan the area around a position with downward raycasts to find walkable ground.
        /// Creates seed nodes that bots can then try to connect by walking between them.
        /// Call on map load or around POI targets.
        /// </summary>
        public int ScanGroundArea(Vector3 center, float radius, float spacing = 2f)
        {
            // ScanGroundArea is now only called manually, not on map load

            int created = 0;
            int gMask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 14);

            // Scan a grid from above
            float scanHeight = center.y + 30f; // Start high above
            int steps = Mathf.CeilToInt(radius / spacing);

            for (int x = -steps; x <= steps; x++)
            for (int z = -steps; z <= steps; z++)
            {
                Vector3 scanPos = center + new Vector3(x * spacing, 0, z * spacing);
                float horizDist = new Vector2(x * spacing, z * spacing).magnitude;
                if (horizDist > radius) continue;

                // Raycast down from high up — find ALL floors (multiple levels)
                Vector3 rayStart = new Vector3(scanPos.x, scanHeight, scanPos.z);
                float rayDist = 60f;
                Vector3 current = rayStart;

                for (int level = 0; level < 5; level++) // Up to 5 floors
                {
                    if (Physics.Raycast(current, Vector3.down, out RaycastHit hit, rayDist, gMask))
                    {
                        Vector3 groundPos = hit.point;

                        // Skip if tagged as hazard
                        if (hit.collider.CompareTag("Killz") || hit.collider.CompareTag("DamageZone"))
                        {
                            current = groundPos + Vector3.down * 0.5f;
                            rayDist = current.y - (center.y - 30f);
                            if (rayDist <= 0) break;
                            continue;
                        }

                        // Validate walkability:
                        // 1. Surface must be roughly flat (normal pointing up)
                        // 2. Must have clearance above (2m ceiling check)
                        // 3. Must have solid ground around it (not a thin railing/edge)
                        bool flatEnough = hit.normal.y > 0.7f; // ~45 degree slope max
                        bool hasCeiling = Physics.Raycast(groundPos + Vector3.up * 0.1f, Vector3.up, 0.5f, gMask);

                        // Check 4 directions for nearby ground — must have ground within 0.5m
                        // This rejects thin edges, railings, and narrow geometry
                        bool wideEnough = true;
                        // Static to avoid per-hit allocation
                        Vector3[] checks = _widthCheckDirs;
                        int solidSides = 0;
                        foreach (var checkDir in checks)
                        {
                            Vector3 sidePos = groundPos + checkDir * 0.5f + Vector3.up * 0.3f;
                            if (Physics.Raycast(sidePos, Vector3.down, 1f, gMask))
                                solidSides++;
                        }
                        wideEnough = solidSides >= 3; // At least 3 of 4 sides have ground

                        if (flatEnough && !hasCeiling && wideEnough)
                        {
                            var existing = FindNearestNode(groundPos, MergeRadius * 1.5f);
                            if (existing == null)
                            {
                                var node = AddPosition(groundPos + Vector3.up * 0.1f, isPlayer: false);
                                if (node != null) created++;
                            }
                        }

                        // Continue scanning below for more floors
                        current = groundPos + Vector3.down * 1f;
                        rayDist = current.y - (center.y - 30f);
                        if (rayDist <= 0) break;
                    }
                    else break;
                }
            }

            if (created > 0)
            {
                Plugin.Log.LogInfo($"[NavGraph] Ground scan created {created} seed nodes around {center} (r={radius})");
                _dirty = true;
            }
            return created;
        }

        // ========== MAP LOCATIONS ==========

        /// <summary>
        /// Register all spawn points and weapon spawners as fixed map locations.
        /// Creates a node at each location. Call on map load.
        /// </summary>
        public void RegisterMapLocations()
        {
            MapLocations.Clear();

            // Register spawn positions for reference (no nodes created)
            var spawns = UnityEngine.Object.FindObjectsOfType<SpawnPoint>();
            foreach (var sp in spawns)
                MapLocations.Add((sp.transform.position, "Spawn", -1));

            // Weapons only — 1 node each
            // First check if a node already exists nearby (from loaded graph), reuse it.
            // Only force-create if nothing exists within 3m.
            var items = UnityEngine.Object.FindObjectsOfType<ItemBehaviour>();
            foreach (var item in items)
            {
                if (item == null) continue;
                if (!item.gameObject.activeInHierarchy) continue;
                Vector3 wpos = item.transform.position + Vector3.up * 0.1f;
                var node = FindNearestNode(wpos, 3f);
                if (node == null)
                    node = AddPosition(wpos, isPlayer: true, force: true);
                if (node != null)
                {
                    node.Confidence = 1f;
                    node.VisitCount = Mathf.Max(node.VisitCount, 10);
                    MapLocations.Add((item.transform.position, item.weaponName ?? item.name, node.Id));
                }
            }

            var spawners = UnityEngine.Object.FindObjectsOfType<ItemSpawner>();
            foreach (var spawner in spawners)
            {
                if (spawner == null) continue;
                if (!spawner.gameObject.activeInHierarchy) continue;
                Vector3 spos = spawner.transform.position + Vector3.up * 0.1f;
                var node = FindNearestNode(spos, 3f);
                if (node == null)
                    node = AddPosition(spos, isPlayer: true, force: true);
                if (node != null)
                {
                    node.Confidence = 1f;
                    node.VisitCount = Mathf.Max(node.VisitCount, 10);
                    MapLocations.Add((spawner.transform.position, "Spawner", node.Id));
                }
            }

            // Item dispensers (slot machines) — count as weapon spawns
            var dispensers = UnityEngine.Object.FindObjectsOfType<ItemDispenser>();
            int dispenserCount = 0;
            foreach (var disp in dispensers)
            {
                if (disp == null) continue;
                if (!disp.gameObject.activeInHierarchy) continue;
                // Place node in front of the dispenser (where player stands to interact)
                Vector3 dpos = disp.transform.position + disp.transform.forward * 1.2f + Vector3.up * 0.1f;
                var node = FindNearestNode(dpos, 3f);
                if (node == null)
                    node = AddPosition(dpos, isPlayer: true, force: true);
                if (node != null)
                {
                    node.Confidence = 1f;
                    node.VisitCount = Mathf.Max(node.VisitCount, 10);
                    MapLocations.Add((dpos, "SlotMachine", node.Id));
                    dispenserCount++;
                }
            }

            // Ladders — place nodes at front side base and top
            int ladderNodeCount = 0;
            var allColliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            foreach (var col in allColliders)
            {
                if (col == null) continue;
                string tag = "";
                try { tag = col.tag; } catch { continue; }
                bool isLadder = tag == "Ladder/Metal" || tag == "Ladder/Chain"
                    || col.gameObject.name.ToLower().Contains("ladder");
                if (!isLadder) continue;

                Bounds b = col.bounds;
                // Find front face via raycast from center outward in 4 directions
                Vector3 center = b.center;
                Vector3 frontDir = Vector3.forward;
                float bestDot = -1f;

                for (int d = 0; d < 4; d++)
                {
                    Vector3 testDir = Quaternion.Euler(0, d * 90f, 0) * Vector3.forward;
                    Vector3 rayStart = center + testDir * (b.extents.magnitude + 0.5f);
                    if (Physics.Raycast(rayStart, -testDir, out RaycastHit lHit, b.extents.magnitude + 1f))
                    {
                        // Front face = the face with the most outward normal (thinnest side)
                        float dot = Vector3.Dot(lHit.normal, testDir);
                        if (dot > bestDot) { bestDot = dot; frontDir = lHit.normal; }
                    }
                }

                // Base node: at bottom of ladder, offset toward front
                Vector3 basePos = new Vector3(center.x, b.min.y + 0.1f, center.z) + frontDir * 0.8f;
                basePos = SnapToGround(basePos);
                var baseNode = FindNearestNode(basePos, 2f);
                if (baseNode == null)
                    baseNode = AddPosition(basePos, isPlayer: true, force: true);

                // Top node: at top of ladder, offset toward front
                Vector3 topPos = new Vector3(center.x, b.max.y + 0.2f, center.z) + frontDir * 0.8f;
                topPos = SnapToGround(topPos);
                var topNode = FindNearestNode(topPos, 2f);
                if (topNode == null)
                    topNode = AddPosition(topPos, isPlayer: true, force: true);

                // Create ladder edges between base and top
                if (baseNode != null && topNode != null && baseNode.Id != topNode.Id)
                {
                    float dist = Vector3.Distance(baseNode.Position, topNode.Position);
                    AddEdge(baseNode.Id, topNode.Id, EdgeType.Ladder, dist, force: true);
                    AddEdge(topNode.Id, baseNode.Id, EdgeType.Ladder, dist, force: true);
                    baseNode.Confidence = 1f;
                    topNode.Confidence = 1f;
                    ladderNodeCount++;
                }
            }

            // Teleporters: each Teleporter component is directional, so only add its real entry->exit link.
            int teleporterCount = 0;
            int staleTeleporterCount = 0;
            var validTeleporterEdges = new HashSet<long>();
            var teleporters = UnityEngine.Object.FindObjectsOfType<Teleporter>();
            foreach (var tp in teleporters)
            {
                if (tp == null || tp.teleportPoint == null) continue;

                Vector3 entryPos = tp.transform.position;
                Vector3 exitPos = tp.teleportPoint.position;

                // Snap to ground
                entryPos = SnapToGround(entryPos);
                exitPos = SnapToGround(exitPos);

                var entryNode = FindNearestNode(entryPos, 2f);
                if (entryNode == null)
                    entryNode = AddPosition(entryPos, isPlayer: true, force: true);

                var exitNode = FindNearestNode(exitPos, 2f);
                if (exitNode == null)
                    exitNode = AddPosition(exitPos, isPlayer: true, force: true);

                if (entryNode != null && exitNode != null && entryNode.Id != exitNode.Id)
                {
                    // Teleporter edges are near-zero cost, but must stay directional.
                    // If a map has a return portal it will have its own Teleporter component.
                    AddEdge(entryNode.Id, exitNode.Id, EdgeType.Teleporter, 0.1f, force: true);
                    validTeleporterEdges.Add(ProvenKey(entryNode.Id, exitNode.Id));
                    entryNode.Confidence = 1f;
                    exitNode.Confidence = 1f;
                    MapLocations.Add((entryPos, "Teleporter", entryNode.Id));
                    MapLocations.Add((exitPos, "Teleporter", exitNode.Id));
                    teleporterCount++;
                }
            }

            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0f || edge.Type != EdgeType.Teleporter) continue;
                if (validTeleporterEdges.Contains(ProvenKey(edge.From, edge.To))) continue;
                edge.Confidence = -1f;
                staleTeleporterCount++;
            }
            if (staleTeleporterCount > 0) _dirty = true;

            Plugin.Log.LogInfo($"[NavGraph] Registered {MapLocations.Count} map locations ({spawns.Length} spawns, {items.Length + spawners.Length} weapons, {dispenserCount} dispensers, {ladderNodeCount} ladders, {teleporterCount} teleporters, {staleTeleporterCount} stale teleporter edges removed)");
        }


        /// <summary>
        /// Remove all nodes that have zero connections (no edges in or out).
        /// </summary>
        public void RemoveOrphanNodes()
        {
            int removed = 0;
            foreach (var node in Nodes)
            {
                if (node.Confidence <= 0) continue;

                bool hasOutgoing = _edgesByFrom.TryGetValue(node.Id, out var outEdges)
                    && outEdges.Count > 0;
                bool hasIncoming = _edgesByTo.TryGetValue(node.Id, out var inEdges)
                    && inEdges.Count > 0;

                // Check if any of those edges are actually alive
                if (hasOutgoing)
                {
                    bool anyAlive = false;
                    foreach (int ei in outEdges)
                        if (ei < Edges.Count && Edges[ei].Confidence > 0) { anyAlive = true; break; }
                    hasOutgoing = anyAlive;
                }
                if (hasIncoming)
                {
                    bool anyAlive = false;
                    foreach (int ei in inEdges)
                        if (ei < Edges.Count && Edges[ei].Confidence > 0) { anyAlive = true; break; }
                    hasIncoming = anyAlive;
                }

                if (!hasOutgoing && !hasIncoming && !IsMapLocation(node.Id))
                {
                    node.Confidence = -1f;
                    removed++;
                }
            }

            if (removed > 0)
            {
                Compact();
                _dirty = true;
                Plugin.Log.LogInfo($"[NavGraph] Removed {removed} orphan nodes (no connections)");
            }
        }

        /// <summary>
        /// Find a map location that has an unbroken path from the given position.
        /// Returns the closest reachable location, prioritizing weapons over spawns.
        /// </summary>
        public (Vector3 pos, string label, List<NavNode> path) FindReachableMapLocation(Vector3 fromPos)
        {
            NavNode bestNode = null;
            string bestLabel = "";
            List<NavNode> bestPath = null;
            float bestScore = float.MinValue;

            foreach (var (pos, label, nodeId) in MapLocations)
            {
                // Try cached route first
                var fromNode = FindNearestNode(fromPos, 10f);
                if (fromNode == null) continue;

                var key = (fromNode.Id, nodeId);
                List<NavNode> path = null;

                if (_routeCache.TryGetValue(key, out var cached))
                    path = new List<NavNode>(cached);
                else
                    path = FindPath(fromPos, pos, jitter: 0f);

                if (path.Count == 0) continue;

                // Score: prefer weapons over spawns, closer is better
                float dist = Vector3.Distance(fromPos, pos);
                float score = -dist;
                if (label != "Spawn" && label != "Spawner") score += 20f; // Weapons preferred

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNode = GetNodeById(nodeId);
                    bestLabel = label;
                    bestPath = path;
                }
            }

            if (bestPath != null && bestNode != null)
                return (bestNode.Position, bestLabel, bestPath);
            return (Vector3.zero, "", new List<NavNode>());
        }

        /// <summary>
        /// Find a map location (weapon/patrol) that has no ROUND-TRIP path from the given position.
        /// A location reachable only via one-way falls (no return path) counts as unreachable.
        /// Returns the nearest disconnected location, or Vector3.zero if all are properly connected.
        /// </summary>
        public (Vector3 pos, string label) FindUnreachableMapLocation(Vector3 fromPos)
        {
            float bestDist = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            string bestLabel = "";

            var fromNode = FindNearestNode(fromPos, 10f);

            foreach (var (pos, label, nodeId) in MapLocations)
            {
                if (label == "Spawn") continue;
                if (Plugin.BlacklistedWeaponNodes.Contains(nodeId)) continue;

                bool connected = false;
                if (fromNode != null)
                {
                    // Check path TO the location
                    var pathTo = FindPath(fromPos, pos, jitter: 0f, searchRadius: 50f);
                    if (pathTo.Count > 0)
                    {
                        // Also check path BACK — if we can't return, it's a one-way trap
                        var pathBack = FindPath(pos, fromPos, jitter: 0f, searchRadius: 50f);
                        connected = pathBack.Count > 0;
                    }
                }

                if (!connected)
                {
                    float dist = Vector3.Distance(fromPos, pos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPos = pos;
                        bestLabel = label;
                    }
                }
            }

            return (bestPos, bestLabel);
        }

        // ========== ROUTE CACHE ==========

        /// <summary>
        /// Pre-compute routes between all spawn points and weapon locations.
        /// Call on round start after graph is loaded.
        /// </summary>
        public void CacheKeyRoutes()
        {
            _routeCache.Clear();

            // Collect key locations
            var keyPositions = new List<(Vector3 pos, string label)>();

            var spawns = UnityEngine.Object.FindObjectsOfType<SpawnPoint>();
            foreach (var sp in spawns)
                keyPositions.Add((sp.transform.position, "spawn"));

            var items = UnityEngine.Object.FindObjectsOfType<ItemBehaviour>();
            foreach (var item in items)
            {
                if (item == null || item.isTaken) continue;
                keyPositions.Add((item.transform.position, item.name));
            }

            if (keyPositions.Count < 2) return;

            // Find nearest graph node for each key position
            var keyNodes = new List<(NavNode node, string label)>();
            foreach (var (pos, label) in keyPositions)
            {
                var node = FindNearestNode(pos, 15f);
                if (node != null)
                    keyNodes.Add((node, label));
            }

            // Pre-compute routes between all pairs (limit to avoid lag)
            int computed = 0;
            for (int i = 0; i < keyNodes.Count && computed < 100; i++)
            {
                for (int j = i + 1; j < keyNodes.Count && computed < 100; j++)
                {
                    var from = keyNodes[i].node;
                    var to = keyNodes[j].node;
                    if (from.Id == to.Id) continue;

                    var key = (from.Id, to.Id);
                    if (_routeCache.ContainsKey(key)) continue;

                    var path = FindPath(from.Position, to.Position, jitter: 0f);
                    if (path.Count > 0)
                    {
                        if (IsCachedPathUsable(from.Id, path))
                            _routeCache[key] = path;

                        var reversePath = new List<NavNode>(path);
                        reversePath.Reverse();
                        if (IsCachedPathUsable(to.Id, reversePath))
                            _routeCache[(to.Id, from.Id)] = reversePath;
                        computed++;
                    }
                }
            }

            Plugin.Log.LogInfo($"[NavGraph] Cached {_routeCache.Count} routes between {keyNodes.Count} key locations");
        }

        /// <summary>
        /// Get a cached route between two positions. Returns empty if not cached.
        /// Falls back to FindPath if cache miss.
        /// </summary>
        public List<NavNode> GetCachedRoute(Vector3 fromPos, Vector3 toPos)
        {
            if (_routeCache.Count == 0) return new List<NavNode>();

            var fromNode = FindNearestNode(fromPos, 5f);
            var toNode = FindNearestNode(toPos, 5f);
            if (fromNode == null || toNode == null) return new List<NavNode>();

            var key = (fromNode.Id, toNode.Id);
            if (_routeCache.TryGetValue(key, out var cached))
            {
                if (IsCachedPathUsable(fromNode.Id, cached))
                    return new List<NavNode>(cached);
                _routeCache.Remove(key);
            }

            // Try finding a cached route that passes near our start and end
            foreach (var kv in _routeCache)
            {
                if (kv.Value.Count < 2) continue;
                float startDist = Vector3.Distance(kv.Value[0].Position, fromPos);
                float endDist = Vector3.Distance(kv.Value[kv.Value.Count - 1].Position, toPos);
                if (startDist < 5f && endDist < 5f && IsCachedPathUsable(kv.Key.from, kv.Value))
                    return new List<NavNode>(kv.Value);
            }

            return new List<NavNode>();
        }

        private bool IsCachedPathUsable(int startNodeId, List<NavNode> path)
        {
            if (path == null || path.Count == 0) return false;

            NavNode previous = GetNodeById(startNodeId);
            if (previous == null || previous.Confidence <= 0f) return false;

            for (int i = 0; i < path.Count; i++)
            {
                NavNode next = path[i];
                if (next == null || next.Confidence <= 0f) return false;
                if (!IsPathSegmentUsable(previous, next)) return false;
                previous = next;
            }

            return true;
        }

        private bool IsPathSegmentUsable(NavNode from, NavNode to)
        {
            if (from == null || to == null) return false;
            if (from.Id == to.Id) return true;

            NavEdge edge = GetEdgeBetween(from.Id, to.Id);
            if (edge != null) return true;

            return ValidateEdgeGround(from.Position, to.Position)
                && ValidateLineOfSight(from.Position, to.Position);
        }

        // >>> Declutter/zones/periodic methods moved to NavGraph.Maintenance.cs
        /// Called automatically when hitting MaxNodes, or manually.
        /// </summary>
        public void Prune()
        {
            if (IsLocked) return;
            // Never prune in Play mode — Play is read-only
            if (Mode == NavMode.Play) return;

            _lastPruneTime = Time.time;
            int beforeNodes = Nodes.Count;
            int beforeEdges = Edges.Count;

            // Build protected set — nodes in patrol routes and map locations MUST NOT be deleted
            var protectedIds = new HashSet<int>();
            foreach (var route in _patrolRoutes.Values)
                foreach (var n in route)
                    protectedIds.Add(n.Id);
            foreach (var (_, _, nodeId) in MapLocations)
                protectedIds.Add(nodeId);

            // Phase 1: Kill nodes below threshold (except protected)
            foreach (var node in Nodes)
            {
                if (node.Confidence <= CONFIDENCE_DELETE_THRESHOLD && !protectedIds.Contains(node.Id))
                    node.Confidence = -1f;
            }

            // Phase 2: Merge clusters of nearby confident nodes
            // Sort by confidence descending — high-confidence nodes absorb their neighbors
            var sorted = new List<NavNode>(Nodes);
            sorted.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            var merged = new HashSet<int>(); // IDs that got absorbed

            foreach (var anchor in sorted)
            {
                if (anchor.Confidence <= 0 || merged.Contains(anchor.Id)) continue;

                var cluster = FindNodesInRadius(anchor.Position, ClusterMergeRadius);
                foreach (var neighbor in cluster)
                {
                    if (neighbor.Id == anchor.Id || merged.Contains(neighbor.Id)) continue;
                    if (neighbor.Confidence <= 0) continue;
                    if (protectedIds.Contains(neighbor.Id)) continue; // Never merge patrol/map nodes

                    // Check xyz proximity — merge if close on ALL axes
                    // Different floors (>1.5m vertical) should NOT merge
                    float dx = Mathf.Abs(neighbor.Position.x - anchor.Position.x);
                    float dy = Mathf.Abs(neighbor.Position.y - anchor.Position.y);
                    float dz = Mathf.Abs(neighbor.Position.z - anchor.Position.z);
                    if (dy > 1.5f) continue; // Different floor
                    // Must be close in horizontal too (not just within sphere radius)
                    if (dx > ClusterMergeRadius || dz > ClusterMergeRadius) continue;

                    // Absorb neighbor into anchor — player-sourced nodes have more weight
                    float anchorW = (anchor.VisitCount + 1f) * (anchor.PlayerSourced ? 2f : 1f);
                    float neighborW = (neighbor.VisitCount + 1f) * (neighbor.PlayerSourced ? 2f : 1f);
                    float totalW = anchorW + neighborW;
                    anchor.Position = Vector3.Lerp(neighbor.Position, anchor.Position, anchorW / totalW);
                    anchor.VisitCount += neighbor.VisitCount;
                    anchor.Confidence = Mathf.Max(anchor.Confidence, neighbor.Confidence);
                    if (neighbor.PlayerSourced) anchor.PlayerSourced = true;

                    RedirectEdges(neighbor.Id, anchor.Id);
                    neighbor.Confidence = -1f;
                    merged.Add(neighbor.Id);
                }
            }

            // Phase 3: If still over target, prune aggressively
            // Priority: never-traversed bot nodes > low-confidence bot nodes > low-visit bot nodes
            // Player nodes are strongly protected
            if (Nodes.Count - merged.Count > PruneTarget)
            {
                var candidates = new List<NavNode>();
                foreach (var node in Nodes)
                {
                    if (node.Confidence <= 0 || merged.Contains(node.Id)) continue;
                    candidates.Add(node);
                }
                candidates.Sort((a, b) =>
                {
                    // Score: higher = keep, lower = prune first
                    // Player nodes: massive bonus (+2.0)
                    // Visit count: small bonus (traveled = useful)
                    // Confidence: direct contribution
                    // Never-visited bot nodes: score near 0 = prune first
                    float scoreA = a.Confidence
                        + (a.PlayerSourced ? 2.0f : 0f)
                        + Mathf.Min(a.VisitCount * 0.02f, 0.5f);
                    float scoreB = b.Confidence
                        + (b.PlayerSourced ? 2.0f : 0f)
                        + Mathf.Min(b.VisitCount * 0.02f, 0.5f);
                    return scoreA.CompareTo(scoreB);
                });

                int toRemove = candidates.Count - PruneTarget;
                for (int i = 0; i < toRemove && i < candidates.Count; i++)
                {
                    // Never prune player-sourced or patrol-protected nodes
                    if (candidates[i].PlayerSourced) continue;
                    if (protectedIds.Contains(candidates[i].Id)) continue;
                    candidates[i].Confidence = -1f;
                }
            }

            // Phase 4: Kill edges pointing to/from dead nodes, or dead edges
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0) continue;
                var fromNode = GetNodeById(edge.From);
                var toNode = GetNodeById(edge.To);
                if (fromNode == null || toNode == null || fromNode.Confidence <= 0 || toNode.Confidence <= 0)
                    edge.Confidence = -1f;
            }

            // Phase 5: Compact — rebuild lists without dead entries
            Compact();

            Plugin.Log.LogInfo($"[NavGraph] Pruned: {beforeNodes} -> {Nodes.Count} nodes, {beforeEdges} -> {Edges.Count} edges");
            _dirty = true;
        }

        /// <summary>
        /// Redirect all edges from oldNodeId to newNodeId (for cluster merging).
        /// </summary>
        private void RedirectEdges(int oldNodeId, int newNodeId)
        {
            // Redirect edges FROM old -> make them FROM new
            if (_edgesByFrom.TryGetValue(oldNodeId, out var fromList))
            {
                foreach (int ei in fromList)
                {
                    if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                    var edge = Edges[ei];
                    if (edge.To == newNodeId)
                    {
                        edge.Confidence = -1f; // Self-loop after redirect, kill it
                        continue;
                    }
                    edge.From = newNodeId;
                    // Add to new node's edge list
                    if (!_edgesByFrom.ContainsKey(newNodeId))
                        _edgesByFrom[newNodeId] = new List<int>();
                    _edgesByFrom[newNodeId].Add(ei);
                }
            }

            // Redirect edges TO old -> make them TO new
            if (_edgesByTo.TryGetValue(oldNodeId, out var toList))
            {
                foreach (int ei in toList)
                {
                    if (ei >= Edges.Count || Edges[ei].Confidence <= 0) continue;
                    var edge = Edges[ei];
                    if (edge.From == newNodeId)
                    {
                        edge.Confidence = -1f; // Would become self-loop
                        continue;
                    }
                    edge.To = newNodeId;
                    // Add to new node's incoming list
                    if (!_edgesByTo.ContainsKey(newNodeId))
                        _edgesByTo[newNodeId] = new List<int>();
                    _edgesByTo[newNodeId].Add(ei);
                }
            }
        }

        /// <summary>
        /// Remove all dead nodes and edges, rebuild lookup structures.
        /// </summary>
        private void Compact()
        {
            // Build new node list, mapping old IDs to new indices
            var liveNodes = new List<NavNode>();
            var idMap = new Dictionary<int, int>(); // old id -> new id
            int newId = 0;

            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0) continue;
                int oldId = node.Id;
                node.Id = newId;
                idMap[oldId] = newId;
                liveNodes.Add(node);
                newId++;
            }

            // Build new edge list with remapped IDs
            var liveEdges = new List<NavEdge>();
            foreach (var edge in Edges)
            {
                if (edge.Confidence <= 0) continue;
                if (!idMap.ContainsKey(edge.From) || !idMap.ContainsKey(edge.To)) continue;
                edge.From = idMap[edge.From];
                edge.To = idMap[edge.To];
                if (edge.From == edge.To) continue; // Remove self-loops
                liveEdges.Add(edge);
            }

            // Deduplicate edges — O(E) with Dictionary lookup instead of O(E^2) nested loop
            var edgeMap = new Dictionary<(int, int, EdgeType), NavEdge>();
            var dedupedEdges = new List<NavEdge>();
            foreach (var edge in liveEdges)
            {
                var key = (edge.From, edge.To, edge.Type);
                if (edgeMap.TryGetValue(key, out var existing))
                {
                    existing.Confidence = Mathf.Max(existing.Confidence, edge.Confidence);
                    existing.SuccessCount += edge.SuccessCount;
                    existing.FailCount += edge.FailCount;
                }
                else
                {
                    edgeMap[key] = edge;
                    dedupedEdges.Add(edge);
                }
            }

            // Replace lists
            Nodes = liveNodes;
            Edges = dedupedEdges;
            _nextNodeId = newId;

            // Recalculate cached counters
            _playerNodeCount = 0; _botNodeCount = 0;
            foreach (var n in Nodes)
            {
                if (n.PlayerSourced) _playerNodeCount++; else _botNodeCount++;
            }

            // Rebuild spatial grid
            _spatialGrid.Clear();
            for (int i = 0; i < Nodes.Count; i++)
                AddToSpatialGrid(Nodes[i]);

            // Rebuild edge lookups
            _edgesByFrom.Clear();
            _edgesByTo.Clear();
            _edgesByTo.Clear();
            for (int i = 0; i < Edges.Count; i++)
            {
                int from = Edges[i].From;
                int to = Edges[i].To;
                if (!_edgesByFrom.ContainsKey(from))
                    _edgesByFrom[from] = new List<int>();
                _edgesByFrom[from].Add(i);
                if (!_edgesByTo.ContainsKey(to))
                    _edgesByTo[to] = new List<int>();
                _edgesByTo[to].Add(i);
            }
        }

        // >>> A* pathfinding methods moved to NavGraph.Pathfinding.cs

        // ========== SPATIAL GRID ==========

        private void AddToSpatialGrid(NavNode node)
        {
            long key = GridKey(
                Mathf.FloorToInt(node.Position.x / GRID_CELL),
                Mathf.FloorToInt(node.Position.y / GRID_CELL),
                Mathf.FloorToInt(node.Position.z / GRID_CELL));
            if (!_spatialGrid.ContainsKey(key))
                _spatialGrid[key] = new List<int>();
            // Use node.Id as index — after Compact, Id == list index
            _spatialGrid[key].Add(node.Id);
        }

        private static long GridKey(int x, int y, int z)
        {
            return ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);
        }

        public static long GridKeyPublic(Vector3 pos)
        {
            return GridKey(Mathf.FloorToInt(pos.x / GRID_CELL), Mathf.FloorToInt(pos.y / GRID_CELL), Mathf.FloorToInt(pos.z / GRID_CELL));
        }

        // >>> Serialization methods moved to NavGraph.Serialization.cs
    }
}
