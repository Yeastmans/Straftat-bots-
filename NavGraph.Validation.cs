using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// Periodic validation + aggressive pruning of bad graph data.
    ///
    /// Runs on a heartbeat driven by the first active BotController (or manually from the UI).
    /// Each pass does one cheap batch of work so we never stall the frame — incremental
    /// cursors keep us from re-checking the same nodes every tick.
    ///
    /// Protected from all pruning: player-sourced nodes (PlayerSourced = true). Players define
    /// ground truth; bots can re-learn from them.
    /// </summary>
    public partial class NavGraph
    {
        // -------- validation heartbeat --------

        // Rolling cursor — we walk a small slice of Nodes each tick rather than the whole list.
        private int _validationCursor;
        private float _lastValidationTime;
        private const float VALIDATION_INTERVAL = 2f;  // seconds between passes
        private const int   VALIDATION_BATCH   = 40;   // nodes checked per pass

        // Unreachable-node pruning
        private const float UNREACHABLE_AGE_SEC     = 60f;   // no visit in this long
        private const int   UNREACHABLE_MIN_VISITS  = 0;     // AND zero successful visits ever
        private const float UNREACHABLE_MIN_LIFETIME_SEC = 45f; // brand-new nodes get a grace window

        // Supersede-by-closer pruning
        private const float SUPERSEDE_RADIUS = 1f;
        private const float SUPERSEDE_Y_TOL  = 0.6f;

        // -------- public entry point --------

        /// <summary>
        /// Called every frame by any bot's Update; runs a validation batch at most
        /// every VALIDATION_INTERVAL seconds regardless of caller count.
        /// </summary>
        public void TickValidation()
        {
            if (IsLocked) return;
            if (Time.time - _lastValidationTime < VALIDATION_INTERVAL) return;
            _lastValidationTime = Time.time;

            int removedWall = 0;
            int removedUnreach = 0;
            int removedSuper = 0;

            int count = Nodes.Count;
            if (count == 0) return;

            int checkedThis = 0;
            while (checkedThis < VALIDATION_BATCH && checkedThis < count)
            {
                if (_validationCursor >= count) _validationCursor = 0;
                var node = Nodes[_validationCursor];
                _validationCursor++;
                checkedThis++;

                if (node == null || node.Confidence <= 0f) continue;
                if (node.PlayerSourced) continue; // hard protection

                // 1) Wall / no-ground check
                if (IsNodeInWall(node.Position))
                {
                    node.Confidence = -1f;
                    removedWall++;
                    continue;
                }

                // 2) Unreachable-for-too-long: bot-sourced, zero success visits, old last-visit
                float age = Time.time - node.LastVisitTime;
                // Guard: if the timestamp is unset/zero (old save data), treat as "just now"
                // to avoid a global wipe on first pass after load.
                if (node.LastVisitTime <= 0.5f) { node.LastVisitTime = Time.time; continue; }

                if (node.VisitCount <= UNREACHABLE_MIN_VISITS
                    && age > UNREACHABLE_AGE_SEC
                    && HasNoSuccessfulEdges(node.Id))
                {
                    node.Confidence = -1f;
                    removedUnreach++;
                    continue;
                }

                // 3) Superseded-by-closer: a better node within SUPERSEDE_RADIUS subsumes this one
                if (IsSupersededByBetter(node))
                {
                    node.Confidence = -1f;
                    removedSuper++;
                    continue;
                }
            }

            if (removedWall + removedUnreach + removedSuper > 0)
            {
                _dirty = true;
                Plugin.Log.LogInfo(
                    $"[NavGraph] Validation pruned: wall={removedWall} unreachable={removedUnreach} superseded={removedSuper}");
            }
        }

        // -------- checks --------

        /// <summary>
        /// True if the node is embedded in geometry or has no ground beneath.
        /// Reuses the same masks and tolerances as AddPosition.
        /// </summary>
        private static bool IsNodeInWall(Vector3 pos)
        {
            // No ground within 1.5m below
            if (!Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 1.5f,
                VALID_GROUND_MASK, QueryTriggerInteraction.Ignore))
                return true;
            if (hit.normal.y < 0.42f) return true; // on a too-steep surface

            // Body capsule clipped into a wall
            int n = Physics.OverlapSphereNonAlloc(pos + Vector3.up * 1f, 0.3f,
                _validateBuffer, VALID_WALL_MASK, QueryTriggerInteraction.Ignore);
            if (n > 0) return true;

            return false;
        }

        /// <summary>
        /// True if the node has no surviving edges in either direction (graph-island).
        /// We prune islands because they contribute nothing to pathfinding.
        /// </summary>
        private bool HasNoSuccessfulEdges(int nodeId)
        {
            if (_edgesByFrom.TryGetValue(nodeId, out var outs))
            {
                foreach (int ei in outs)
                {
                    if (ei < Edges.Count && Edges[ei].Confidence > 0f && Edges[ei].SuccessCount > 0)
                        return false;
                }
            }
            if (_edgesByTo.TryGetValue(nodeId, out var ins))
            {
                foreach (int ei in ins)
                {
                    if (ei < Edges.Count && Edges[ei].Confidence > 0f && Edges[ei].SuccessCount > 0)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True if another node within 1m on the same level has strictly better score
        /// (higher VisitCount + higher Confidence) AND this node is not a unique landing
        /// point for a special edge. Merges happen in CompressNearby; this just opts the
        /// node out of the graph so A* picks the better node.
        /// </summary>
        private bool IsSupersededByBetter(NavNode node)
        {
            // Never prune special-edge endpoints — those nodes have geometry-specific meaning
            if (HasSpecialEdge(node.Id)) return false;
            if (IsMapLocation(node.Id)) return false;
            if (IsPatrolProtected(node.Id)) return false;

            var nearby = FindNodesInRadius(node.Position, SUPERSEDE_RADIUS);
            float myScore = node.VisitCount + node.Confidence;
            foreach (var other in nearby)
            {
                if (other == null || other.Id == node.Id || other.Confidence <= 0f) continue;
                if (Mathf.Abs(other.Position.y - node.Position.y) > SUPERSEDE_Y_TOL) continue;
                float otherScore = other.VisitCount + other.Confidence + (other.PlayerSourced ? 5f : 0f);
                if (otherScore > myScore + 0.1f)
                {
                    // Redirect this node's edges onto the better one before we yank it,
                    // so we don't create dead-ends in the graph.
                    RedirectEdges(node.Id, other.Id);
                    return true;
                }
            }
            return false;
        }

        // -------- shortcut detection --------

        /// <summary>
        /// Called from BotController when a bot successfully walks grandparent → parent → current.
        /// If grandparent → current is directly walkable (ground + LOS), adds a Walk edge for
        /// that shortcut and decays the detour edges so A* prefers the straight route.
        /// Cheap and safe: only acts on Walk-type middle nodes, never collapses special edges.
        /// </summary>
        public void TryShortcut(int grandparentId, int parentId, int currentId)
        {
            if (IsLocked) return;
            if (grandparentId == parentId || parentId == currentId || grandparentId == currentId) return;

            var gp = GetNodeById(grandparentId);
            var par = GetNodeById(parentId);
            var cur = GetNodeById(currentId);
            if (gp == null || par == null || cur == null) return;
            if (gp.Confidence <= 0f || par.Confidence <= 0f || cur.Confidence <= 0f) return;

            // Only shortcut through a parent that we'd skip cleanly: parent must be a Walk-midstep,
            // not a takeoff/landing node of a Jump/Fall/Ladder/Slide/WallJump.
            if (HasSpecialEdge(parentId)) return;

            // Must not be a map location (weapon/spawn) — those are semantically meaningful.
            if (IsMapLocation(parentId)) return;

            // Already have a direct edge? Nothing to add — but still decay detour if shortcut
            // is actually shorter.
            var direct = GetEdgeBetween(grandparentId, currentId);
            float directDist = Vector3.Distance(gp.Position, cur.Position);
            float detourDist = Vector3.Distance(gp.Position, par.Position)
                             + Vector3.Distance(par.Position, cur.Position);

            // Require a meaningful saving — 15% or 1m, whichever is larger.
            if (directDist > detourDist * 0.85f && directDist > detourDist - 1f) return;

            // Geometry gate: shortcut must be walkable (ground + LOS).
            if (!ValidateEdgeGround(gp.Position, cur.Position)) return;
            if (!ValidateLineOfSight(gp.Position, cur.Position)) return;

            // Create or refresh the shortcut edge.
            if (direct == null)
            {
                AddEdge(grandparentId, currentId, EdgeType.Walk, directDist, force: true);
                Plugin.Log.LogInfo($"[NavGraph] Shortcut {grandparentId}->{currentId} (saved {detourDist - directDist:F1}m)");
            }
            else
            {
                direct.Confidence = Mathf.Min(1f, direct.Confidence + 0.2f);
                direct.SuccessCount++;
            }

            // Decay the detour so A* stops picking it. Skip if the middle parent is player-sourced —
            // player paths stay intact.
            if (!par.PlayerSourced)
            {
                var eA = GetEdgeBetween(grandparentId, parentId);
                var eB = GetEdgeBetween(parentId, currentId);
                if (eA != null) eA.Confidence *= 0.9f;
                if (eB != null) eB.Confidence *= 0.9f;
            }

            _dirty = true;
        }
    }
}
