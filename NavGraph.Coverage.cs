using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    // Coverage heatmap + frontier queue.
    // The goal is to let bots learn the WHOLE map by trial-and-error:
    //   - Coverage heatmap: bins all visited positions into a world grid.
    //     Bots in Explore ask "what's the least-visited reachable cell?"
    //     and head there, which guarantees they eventually cover the map.
    //   - Frontier queue: when a bot can't reach a target after a watchdog
    //     fires, its last intended target is pushed here. Next idle bot
    //     pops and tries again from a different angle — shared across all
    //     bots so coverage compounds.
    //   - Saturation / novelty: cells with many visits but few new edges
    //     are promoted as "real unexplored"; cells whose graph hasn't grown
    //     in 30s are demoted so bots don't thrash known hubs.
    //   - Budget decay: when the graph node count plateaus, explore
    //     aggression scales down so bots naturally ease off and stabilise.
    public partial class NavGraph
    {
        // Grid cell size — 4m is a good compromise between resolution and noise.
        private const float COVERAGE_CELL = 4f;

        // Map-position -> total bot+player visits in that cell.
        private readonly Dictionary<long, int> _coverage = new Dictionary<long, int>();

        // Cells with at least one reachable node — used to skip unreachable areas.
        private readonly HashSet<long> _coverageHasNode = new HashSet<long>();

        // How many *new* edges have been created from nodes in this cell. Denominator
        // of the novelty score — a cell with many visits and zero edges added is a
        // genuine dead-end worth revisiting from a different angle.
        private readonly Dictionary<long, int> _edgesAddedFromCell = new Dictionary<long, int>();

        // Last time any edge was added in this cell. If no new edges in >30s and
        // the cell already has ≥3 nodes, we consider the cell "saturated" and
        // demote it as an exploration target.
        private readonly Dictionary<long, float> _cellLastEdgeTime = new Dictionary<long, float>();
        private const float CELL_SATURATION_AGE = 30f;
        private const int   CELL_SATURATION_MIN_NODES = 3;

        // Frontier queue — carries the failed target plus the direction the bot
        // was approaching from, so the next bot can pick an opposite approach.
        private struct FrontierEntry
        {
            public Vector3 Pos;
            public Vector3 ApproachDir;  // unit vector from bot -> pos at the moment of giving up (may be zero)
            public int     BotId;        // who gave up, so we can bias away from them (optional)
        }
        private readonly Queue<FrontierEntry> _frontier = new Queue<FrontierEntry>();
        private const int FRONTIER_CAP = 32;

        // Budget decay — explore aggression scalar. 1.0 = full-bore, 0.5 = relaxed.
        // Dropped once the graph node count has been stable for two 60s windows.
        public float ExploreAggression { get; private set; } = 1f;
        private int _budgetLastNodeCount;
        private int _budgetStableWindows;
        private float _budgetLastSample;
        private const float BUDGET_SAMPLE_INTERVAL = 60f;
        private const float BUDGET_STABLE_DELTA_PCT = 0.02f;  // 2% growth per window counts as stable

        private float _coverageLastRebuild = -999f;
        private const float COVERAGE_REBUILD_INTERVAL = 4f;

        private static long CellKey(Vector3 pos)
        {
            int x = Mathf.FloorToInt(pos.x / COVERAGE_CELL);
            int z = Mathf.FloorToInt(pos.z / COVERAGE_CELL);
            return ((long)x << 32) ^ (uint)z;
        }

        private static Vector3 CellCenter(long key)
        {
            int x = (int)(key >> 32);
            int z = (int)(key & 0xFFFFFFFF);
            return new Vector3((x + 0.5f) * COVERAGE_CELL, 0f, (z + 0.5f) * COVERAGE_CELL);
        }

        /// <summary>
        /// Record that a bot or player passed through this world position.
        /// Cheap — a single dict lookup per call.
        /// </summary>
        public void TouchCoverage(Vector3 pos)
        {
            long key = CellKey(pos);
            if (_coverage.TryGetValue(key, out int n)) _coverage[key] = n + 1;
            else _coverage[key] = 1;
        }

        /// <summary>
        /// Bump the edge-added counter for the cell that contains 'pos'. Called from
        /// AddEdge so the novelty picker and saturation check can read it.
        /// </summary>
        internal void TouchCellEdgeAdded(Vector3 pos)
        {
            long key = CellKey(pos);
            if (_edgesAddedFromCell.TryGetValue(key, out int n)) _edgesAddedFromCell[key] = n + 1;
            else _edgesAddedFromCell[key] = 1;
            _cellLastEdgeTime[key] = Time.time;
        }

        /// <summary>
        /// Rebuild the HasNode set + node-per-cell counts from current graph state.
        /// Called lazily from the pickers.
        /// </summary>
        private void RebuildCoverageIfStale()
        {
            if (Time.time - _coverageLastRebuild < COVERAGE_REBUILD_INTERVAL) return;
            _coverageLastRebuild = Time.time;

            _coverageHasNode.Clear();
            _cellNodeCount.Clear();
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence < 0f) continue;
                long key = CellKey(node.Position);
                _coverageHasNode.Add(key);
                if (_cellNodeCount.TryGetValue(key, out int n)) _cellNodeCount[key] = n + 1;
                else _cellNodeCount[key] = 1;
            }
        }
        private readonly Dictionary<long, int> _cellNodeCount = new Dictionary<long, int>();

        private bool IsCellSaturated(long key)
        {
            if (!_cellNodeCount.TryGetValue(key, out int nodes) || nodes < CELL_SATURATION_MIN_NODES)
                return false;
            if (!_cellLastEdgeTime.TryGetValue(key, out float t)) return false;
            return (Time.time - t) > CELL_SATURATION_AGE;
        }

        /// <summary>
        /// Returns the center of the lowest-visit cell that has a reachable node,
        /// skipping saturated cells. If every reachable cell is saturated, falls
        /// back to the highest-novelty cell (visits / (edges_added + 1)) so bots
        /// keep pushing into real dead-ends instead of thrashing hubs.
        /// Returns null if no suitable cell exists.
        /// </summary>
        public Vector3? GetLowestVisitReachableCell(Vector3 nearPos, float maxScanDist = 80f)
        {
            RebuildCoverageIfStale();
            if (_coverageHasNode.Count == 0) return null;

            long bestKey = 0;
            int bestScore = int.MaxValue;
            bool found = false;
            float maxSqr = maxScanDist * maxScanDist;

            // Pass 1: lowest-visit, non-saturated cells.
            foreach (long key in _coverageHasNode)
            {
                if (IsCellSaturated(key)) continue;
                Vector3 c = CellCenter(key);
                float dx = c.x - nearPos.x;
                float dz = c.z - nearPos.z;
                float distSqr = dx * dx + dz * dz;
                if (distSqr > maxSqr) continue;

                int visits = _coverage.TryGetValue(key, out int v) ? v : 0;
                int score = visits * 10 + (int)(Mathf.Sqrt(distSqr));
                if (score < bestScore)
                {
                    bestScore = score;
                    bestKey = key;
                    found = true;
                }
            }

            // Pass 2: every reachable cell is saturated — use novelty score instead.
            if (!found)
            {
                float bestNov = -1f;
                foreach (long key in _coverageHasNode)
                {
                    Vector3 c = CellCenter(key);
                    float dx = c.x - nearPos.x;
                    float dz = c.z - nearPos.z;
                    float distSqr = dx * dx + dz * dz;
                    if (distSqr > maxSqr) continue;

                    int visits = _coverage.TryGetValue(key, out int v) ? v : 0;
                    int edgesAdded = _edgesAddedFromCell.TryGetValue(key, out int e) ? e : 0;
                    // High novelty = many visits per edge created. Add 1 to denominator to avoid /0.
                    float novelty = (visits + 1f) / (edgesAdded + 1f);
                    // Penalise distance lightly so we don't tele across the map.
                    float score = novelty - Mathf.Sqrt(distSqr) * 0.05f;
                    if (score > bestNov)
                    {
                        bestNov = score;
                        bestKey = key;
                        found = true;
                    }
                }
            }

            if (!found) return null;

            // Snap to a real node in the cell so the bot can actually path there.
            Vector3 center = CellCenter(bestKey);
            var snap = FindNearestNode(center, COVERAGE_CELL);
            return snap != null ? (Vector3?)snap.Position : center;
        }

        /// <summary>Legacy push — no approach direction available.</summary>
        public void PushFrontier(Vector3 pos) => PushFrontier(pos, Vector3.zero, -1);

        /// <summary>
        /// Push with the approach direction the bot was on when it gave up.
        /// The next bot to pop will bias its approach ≥45° off this direction.
        /// </summary>
        public void PushFrontier(Vector3 pos, Vector3 approachDir, int botId)
        {
            // Dedupe nearby frontier cells — if one exists, merge the approach dir
            // so we remember multiple attempted angles per cell (union via average).
            foreach (var existing in _frontier)
            {
                if ((existing.Pos - pos).sqrMagnitude < (COVERAGE_CELL * COVERAGE_CELL)) return;
            }

            Vector3 dir = approachDir;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) dir.Normalize();
            else dir = Vector3.zero;

            _frontier.Enqueue(new FrontierEntry { Pos = pos, ApproachDir = dir, BotId = botId });
            while (_frontier.Count > FRONTIER_CAP) _frontier.Dequeue();
        }

        /// <summary>Legacy pop — drops the approach data.</summary>
        public bool TryPopFrontier(out Vector3 pos)
        {
            if (_frontier.Count == 0) { pos = Vector3.zero; return false; }
            var entry = _frontier.Dequeue();
            pos = entry.Pos;
            return true;
        }

        /// <summary>
        /// Pop a frontier cell and report the approach direction to AVOID.
        /// Caller should bias its own approach so the angle between the new path
        /// and avoidDir is ≥45° — this is the heart of multi-bot angle variance.
        /// </summary>
        public bool TryPopFrontier(int callerBotId, out Vector3 pos, out Vector3 avoidDir)
        {
            if (_frontier.Count == 0) { pos = Vector3.zero; avoidDir = Vector3.zero; return false; }
            var entry = _frontier.Dequeue();
            pos = entry.Pos;
            // If the last attempt was by the *same* bot, avoidDir is its own dir; otherwise
            // we still bias away — different-angle attempts compound information fastest.
            avoidDir = entry.ApproachDir;
            return true;
        }

        /// <summary>
        /// Sampled every 60s from the bot update path. Once the graph's node count
        /// has grown less than 2% across two consecutive windows, we halve the
        /// explore aggression — bots spend longer per target and thrash the graph
        /// less. Naturally ramps back up if the graph starts growing again.
        /// </summary>
        public void TickTrainingBudget(int currentNodeCount)
        {
            if (Time.time - _budgetLastSample < BUDGET_SAMPLE_INTERVAL) return;
            _budgetLastSample = Time.time;

            if (_budgetLastNodeCount <= 0)
            {
                _budgetLastNodeCount = currentNodeCount;
                return;
            }

            float growth = (currentNodeCount - _budgetLastNodeCount) / (float)Mathf.Max(1, _budgetLastNodeCount);
            if (growth < BUDGET_STABLE_DELTA_PCT) _budgetStableWindows++;
            else _budgetStableWindows = 0;

            // Two stable windows in a row → cool down.
            if (_budgetStableWindows >= 2 && ExploreAggression > 0.5f)
            {
                ExploreAggression = 0.5f;
                Plugin.Log.LogInfo($"[NavGraph] Training budget: graph stable ({currentNodeCount} nodes) → aggression 0.5");
            }
            // Graph growing again → ramp back.
            else if (growth >= BUDGET_STABLE_DELTA_PCT * 2f && ExploreAggression < 1f)
            {
                ExploreAggression = 1f;
                _budgetStableWindows = 0;
                Plugin.Log.LogInfo($"[NavGraph] Training budget: graph growing again → aggression 1.0");
            }

            _budgetLastNodeCount = currentNodeCount;
        }

        public int CoverageCellCount => _coverageHasNode.Count;
        public int FrontierCount => _frontier.Count;
    }
}
