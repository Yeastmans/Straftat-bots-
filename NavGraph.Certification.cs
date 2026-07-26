using System;
using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    public sealed class MapCertificationReport
    {
        public string MapName;
        public float Score;
        public int ActiveNodes;
        public int PlayerNodes;
        public int ActiveEdges;
        public int CandidateEdges;
        public int PlayerProvenEdges;
        public int BotValidatedEdges;
        public int NeedsDemoEdges;
        public int BlockedEdges;
        public int SpecialEdges;
        public int TrustedSpecialEdges;
        public int NeedsDemoSpecialEdges;   // special edges bots gave up on — resolved, not pending
        public int InProgressSpecialEdges;  // ≥1 success, not yet trusted — half credit in stage 3
        public int AnchorCount;
        public int ConnectedAnchors;
        public int ValidatedAnchorRoutes;
        public int WeaponAnchorCount;
        public int ValidatedWeaponRoutes;
        public int BadNodeCount;
        public int WorstBadNodeStrikes;
        public bool NeedsTraining;
        public int StageNumber;
        public float CoverageProgress;
        public float ReachableNodeFraction;   // fraction of player-walked space bots can reach from spawn
        public int ConnectedWeaponCount;      // weapons actually path-connected from spawn
        public float ConnectionProgress;
        public float WeaponProgress;
        public float TrustProgress;
        public float CleanupProgress;
        public float StageProgress;
        // True when the stage metric hasn't moved for a while with bots actively
        // training — the honest "nothing more is coming, advance when ready" signal
        // instead of a bar that silently stalls at an arbitrary percentage.
        public bool StageSettled;
        public string StageName;
        public string StageInstruction;
        public string DemoHint;
        public string PrimaryAction;
        public string NextButtonLabel;
        public string RecommendedBehavior;
        public string TargetLabel;
        public string BadNodeReason;
        public bool HasTargetPosition;
        public Vector3 TargetPosition;
        public bool HasRouteTarget;
        public Vector3 RouteStartPosition;
        public Vector3 RouteEndPosition;
        public List<Vector3> UnconnectedWeaponPositions;
        public string Summary;
    }

    public partial class NavGraph
    {
        // 2 clean traversals trust an edge (was 3 — the single biggest reason the
        // Confirmation bar crawled). Learn-in-play keeps counting traversals in Play
        // mode, so marginal edges still accumulate evidence after training ends.
        private const int BOT_VALIDATION_SUCCESSES_TO_TRUST = 2;
        private const int BOT_VALIDATION_FAILURES_TO_DEMO = 3;
        private const int MIN_PLAY_NODES = 200;
        private MapCertificationReport _cachedCertification;
        private float _lastCertificationTime = -999f;
        private float _nextAutoPruneTime;
        private readonly Dictionary<string, float> _validationLabelCooldowns = new Dictionary<string, float>();

        private static EdgeTrustState NormalizeTrustState(byte value)
        {
            return value <= (byte)EdgeTrustState.Blocked
                ? (EdgeTrustState)value
                : EdgeTrustState.Candidate;
        }

        // ---- Single-stage training (per map, session-persistent) ----
        // ONE stage: bots walk every area (coverage), every weapon, and every path
        // the player has recorded; pending special edges get validated throughout.
        // The bar blends those terms; the one button finishes and switches to Play.
        // TrainingStage is kept for save-data compat but no behavior branches on it.
        private static readonly Dictionary<string, int> _stageByMap = new Dictionary<string, int>();

        public int TrainingStage
        {
            get
            {
                return CurrentMap != null && _stageByMap.TryGetValue(CurrentMap, out int s)
                    ? s : 1;
            }
            set
            {
                if (CurrentMap != null) _stageByMap[CurrentMap] = Mathf.Clamp(value, 1, 3);
                _cachedCertification = null;
            }
        }

        /// <summary>The one training button: finish training, hand off to Play.
        /// (The Play mode setter kills bots, starts a fresh round, saves.)</summary>
        public void AdvanceTrainingStage()
        {
            if (Plugin.NavGraphMode != null)
            {
                Plugin.NavGraphMode.Value = "Play";
                Plugin.Log.LogInfo("[NavGraph] Training finished — switched to Play");
            }
            _cachedCertification = null;
        }

        /// <summary>Stage-2 entry cleanup: drop every node the map can't actually reach —
        /// not connected to a spawn by graph edges AND not sitting on the baked ground
        /// mesh. Map-location nodes (weapons/spawns/patrol) always survive: unlinked
        /// weapons are exactly what stage 2 is about.</summary>
        public void PruneDisconnectedFromSpawn()
        {
            if (IsLocked || !HasData) return;

            var spawnIds = new List<int>();
            foreach (var loc in MapLocations)
                if (loc.label == "Spawn")
                {
                    var sn = GetNodeById(loc.nodeId);
                    if (sn != null && sn.Confidence > 0f) spawnIds.Add(sn.Id);
                }
            if (spawnIds.Count == 0)
                foreach (var loc in MapLocations)
                {
                    var sn = GetNodeById(loc.nodeId);
                    if (sn != null && sn.Confidence > 0f) spawnIds.Add(sn.Id);
                }

            var keep = new HashSet<int>();
            foreach (int s in spawnIds)
                foreach (int id in FloodReachable(s, trustedOnly: false))
                    keep.Add(id);

            var locIds = new HashSet<int>();
            foreach (var loc in MapLocations) locIds.Add(loc.nodeId);

            // Spawn WORLD positions for mesh-route checks. Merely sitting on the baked
            // mesh is not enough to survive: the downward bake scan leaves isolated
            // mesh islands (roof tops, wall interiors) that sample fine but route
            // nowhere — nodes there are exactly the junk this prune is for.
            var spawnPositions = new List<Vector3>();
            foreach (var loc in MapLocations)
                if (loc.label == "Spawn") spawnPositions.Add(loc.pos);
            if (spawnPositions.Count == 0)
                foreach (int sid in spawnIds)
                {
                    var sn = GetNodeById(sid);
                    if (sn != null) spawnPositions.Add(sn.Position);
                }

            int removed = 0;
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0f) continue;
                if (keep.Contains(node.Id) || locIds.Contains(node.Id)) continue;
                if (BotNavMesh.Ready
                    && UnityEngine.AI.NavMesh.SamplePosition(
                        node.Position, out _, 0.8f, UnityEngine.AI.NavMesh.AllAreas)
                    && MeshRoutedFromAnySpawn(spawnPositions, node.Position))
                    continue; // mesh can genuinely WALK there from a spawn
                node.Confidence = 0f;
                removed++;
            }
            if (removed > 0)
            {
                Compact();
                _dirty = true;
            }
            Plugin.Log.LogInfo($"[NavGraph] Cleanup: removed {removed} disconnected nodes/paths");
            _cachedCertification = null;
        }

        public MapCertificationReport GetCertificationReport(bool force = false)
        {
            if (!force && _cachedCertification != null && Time.time - _lastCertificationTime < 3f)
                return _cachedCertification;

            _lastCertificationTime = Time.time;
            var report = new MapCertificationReport { MapName = CurrentMap ?? "?" };

            var anchorIds = new List<int>();
            var weaponIds = new List<int>();
            var seenAnchors = new HashSet<int>();
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0f) continue;
                report.ActiveNodes++;
                if (node.PlayerSourced) report.PlayerNodes++;
            }

            foreach (var loc in MapLocations)
            {
                if (loc.label == "PatrolPoint") continue;
                var node = GetNodeById(loc.nodeId);
                if (node != null && node.Confidence > 0f && seenAnchors.Add(node.Id))
                {
                    anchorIds.Add(node.Id);
                    if (IsWeaponLocationLabel(loc.label))
                        weaponIds.Add(node.Id);
                }
            }
            report.AnchorCount = anchorIds.Count;
            report.WeaponAnchorCount = weaponIds.Count;

            foreach (var edge in Edges)
            {
                if (edge == null)
                {
                    continue;
                }
                if (edge.Confidence <= 0f || edge.TrustState == EdgeTrustState.Blocked)
                {
                    report.BlockedEdges++;
                    continue;
                }

                report.ActiveEdges++;
                if (IsSpecialTraversal(edge)) report.SpecialEdges++;

                switch (edge.TrustState)
                {
                    case EdgeTrustState.PlayerProven:
                        report.PlayerProvenEdges++;
                        break;
                    case EdgeTrustState.BotValidated:
                        report.BotValidatedEdges++;
                        break;
                    case EdgeTrustState.NeedsDemo:
                        report.NeedsDemoEdges++;
                        break;
                    default:
                        report.CandidateEdges++;
                        break;
                }

                if (IsTrustedForPlay(edge) && IsSpecialTraversal(edge))
                    report.TrustedSpecialEdges++;
                if (edge.TrustState == EdgeTrustState.NeedsDemo && IsSpecialTraversal(edge))
                    report.NeedsDemoSpecialEdges++;
                if (IsSpecialTraversal(edge) && !IsTrustedForPlay(edge)
                    && edge.TrustState != EdgeTrustState.NeedsDemo && edge.BotValidationSuccesses > 0)
                    report.InProgressSpecialEdges++;
            }

            report.ConnectedAnchors = CountConnectedAnchors(anchorIds, trustedOnly: false);
            report.ValidatedAnchorRoutes = CountValidatedAnchorRoutes(anchorIds);
            report.ValidatedWeaponRoutes = CountValidatedWeapons(weaponIds, anchorIds);
            report.BadNodeCount = CountBadNodes(out Vector3 badPos, out string badReason, out int badStrikes);
            report.WorstBadNodeStrikes = badStrikes;
            report.BadNodeReason = badReason;
            if (report.BadNodeCount > 0)
            {
                report.HasTargetPosition = true;
                report.TargetPosition = badPos;
                report.TargetLabel = $"Bad route point ({badStrikes}/{3})";
            }

            // ---- Reachability is the REAL coverage metric (not raw node count): what fraction
            // of the space the player has walked can bots actually reach from a spawn? This is
            // the "bots reach most nodes players can" test. We also find which weapons are
            // path-connected at all, so genuinely-unreachable weapons don't block certification. ----
            var spawnSources = new List<int>();
            foreach (var loc in MapLocations)
                if (loc.label == "Spawn")
                {
                    var sn = GetNodeById(loc.nodeId);
                    if (sn != null && sn.Confidence > 0f) spawnSources.Add(sn.Id);
                }
            if (spawnSources.Count == 0) spawnSources.AddRange(anchorIds);
            var reachableFromSpawn = new HashSet<int>();
            foreach (int s in spawnSources)
                foreach (int id in FloodReachable(s, trustedOnly: false))
                    reachableFromSpawn.Add(id);

            int reachablePlayer = 0, totalPlayer = 0, reachableAll = 0;
            foreach (var node in Nodes)
            {
                if (node == null || node.Confidence <= 0f) continue;
                bool r = reachableFromSpawn.Contains(node.Id);
                if (r) reachableAll++;
                if (node.PlayerSourced) { totalPlayer++; if (r) reachablePlayer++; }
            }
            // Reference the player-walked space once we have enough of it; before that
            // (sparse / bot-only), fall back to overall graph connectivity from spawn.
            report.ReachableNodeFraction = totalPlayer >= 20
                ? (totalPlayer > 0 ? reachablePlayer / (float)totalPlayer : 0f)
                : (report.ActiveNodes > 0 ? reachableAll / (float)report.ActiveNodes : 0f);

            var connectedWeaponSet = new HashSet<int>();
            foreach (int wid in weaponIds) if (reachableFromSpawn.Contains(wid)) connectedWeaponSet.Add(wid);

            // ---- NavMesh augmentation: a complete baked-mesh route from spawn IS a working
            // route — it needs no bot-validation grind. Without this, weapon/route progress
            // stalls forever once bots walk the navmesh instead of grinding graph edges. ----
            bool navmeshReady = BotNavMesh.Ready;
            if (navmeshReady)
            {
                Vector3 spawnPos = Vector3.zero;
                bool haveSpawn = false;
                foreach (var loc in MapLocations)
                    if (loc.label == "Spawn") { spawnPos = loc.pos; haveSpawn = true; break; }
                if (!haveSpawn && spawnSources.Count > 0)
                {
                    var sn = GetNodeById(spawnSources[0]);
                    if (sn != null) { spawnPos = sn.Position; haveSpawn = true; }
                }
                if (haveSpawn)
                {
                    var nmRouted = new HashSet<int>();
                    foreach (int aid in anchorIds)
                    {
                        var an = GetNodeById(aid);
                        if (an != null && NavMeshRouteExistsCached(aid, spawnPos, an.Position))
                            nmRouted.Add(aid);
                    }
                    int nmWeapons = 0;
                    foreach (int wid in weaponIds)
                        if (nmRouted.Contains(wid)) { nmWeapons++; connectedWeaponSet.Add(wid); }

                    report.ConnectedAnchors = Mathf.Max(report.ConnectedAnchors, nmRouted.Count);
                    report.ValidatedAnchorRoutes = Mathf.Max(report.ValidatedAnchorRoutes, nmRouted.Count);
                    report.ValidatedWeaponRoutes = Mathf.Max(report.ValidatedWeaponRoutes, nmWeapons);
                    // Ground coverage is MEASURED against the baked mesh: every
                    // spawn-reachable scan cell starts unwalked and fills in as bots and
                    // players actually walk it. Out-of-bounds bake islands were excluded
                    // by the bake-time flood fill, so 100% is genuinely attainable.
                    report.ReachableNodeFraction = BotNavMesh.WalkedCoverage;
                }
            }
            int connectedWeapons = connectedWeaponSet.Count;
            report.ConnectedWeaponCount = connectedWeapons;

            // Weapons NOT yet connected — stage 2 marks these in the world as targets.
            report.UnconnectedWeaponPositions = new List<Vector3>();
            foreach (int wid in weaponIds)
            {
                if (connectedWeaponSet.Contains(wid)) continue;
                var wn = GetNodeById(wid);
                if (wn != null && wn.Confidence > 0f)
                    report.UnconnectedWeaponPositions.Add(wn.Position);
            }

            report.CoverageProgress = report.ReachableNodeFraction;
            report.ConnectionProgress = report.AnchorCount <= 1
                ? 1f
                : Mathf.Clamp01(report.ConnectedAnchors / (float)report.AnchorCount);
            // Only weapons that are actually connected to the graph count — unreachable
            // weapons (some maps have them) neither help nor block the score.
            report.WeaponProgress = connectedWeapons > 0
                ? Mathf.Clamp01(report.ValidatedWeaponRoutes / (float)connectedWeapons)
                : 1f;
            report.TrustProgress = report.ActiveEdges > 0
                ? Mathf.Clamp01((report.PlayerProvenEdges + report.BotValidatedEdges) / (float)report.ActiveEdges)
                : 0f;
            report.CleanupProgress = report.BadNodeCount <= 0
                ? 1f
                : Mathf.Clamp01(1f - report.BadNodeCount / 12f);

            float nodeScore = report.CoverageProgress * 22f;        // reachability is the biggest single factor now
            float edgeDensity = report.ActiveNodes > 0 ? report.ActiveEdges / Mathf.Max(1f, report.ActiveNodes * 1.15f) : 0f;
            float edgeScore = Mathf.Clamp01(edgeDensity) * 10f;
            float anchorScore = report.AnchorCount <= 1 ? 0f : report.ConnectionProgress * 22f;
            float validatedRoutePossible = report.AnchorCount <= 1 ? 1f : Mathf.Clamp01(report.ValidatedAnchorRoutes / Mathf.Max(1f, report.AnchorCount));
            float routeScore = validatedRoutePossible * 18f;
            float weaponScore = report.WeaponProgress * 10f;
            float trustScore = report.TrustProgress * 18f;
            // NO FREE CREDIT: a map with no special edges gets ZERO special points (it used to
            // get the full 12, inflating flat / under-explored maps over the certified line).
            float specialScore = report.SpecialEdges > 0
                ? (report.TrustedSpecialEdges / (float)report.SpecialEdges) * 8f
                : 0f;
            float candidatePenalty = Mathf.Min(8f, Mathf.Max(0, report.CandidateEdges - report.BotValidatedEdges) * 0.05f);
            float badNodePenalty = Mathf.Min(16f, report.BadNodeCount * 2f);

            if (navmeshReady)
            {
                // Ground navigation is solved by the baked mesh; the score only reflects what
                // is actually left: linking areas/weapons (jumps, ladders) and bad-data cleanup.
                report.Score = Mathf.Clamp(40f
                    + report.ConnectionProgress * 20f
                    + report.WeaponProgress * 25f
                    + report.CleanupProgress * 15f
                    - badNodePenalty, 0f, 100f);
                report.NeedsTraining = report.BadNodeCount > 2
                    || (report.ConnectedWeaponCount > 0 && report.WeaponProgress < 0.5f)
                    || (report.AnchorCount > 2 && report.ConnectionProgress < 0.5f);
            }
            else
            {
                report.Score = Mathf.Clamp(nodeScore + edgeScore + anchorScore + routeScore + weaponScore + trustScore + specialScore
                    - candidatePenalty - badNodePenalty, 0f, 100f);
                report.NeedsTraining = report.ActiveNodes < MIN_PLAY_NODES
                    || report.ReachableNodeFraction < 0.6f
                    || report.Score < 70f
                    || report.BadNodeCount > 2
                    || (connectedWeapons > 0 && report.ValidatedWeaponRoutes < Mathf.Max(1, connectedWeapons / 2))
                    || (report.AnchorCount > 2 && report.ConnectedAnchors < Mathf.Max(2, report.AnchorCount / 2));
            }

            ApplyCertificationStage(report, weaponIds, anchorIds);

            report.Summary =
                $"{report.Score:F0}% certified, {report.StageName}, {report.BadNodeCount} bad route points, " +
                $"{report.NeedsDemoEdges} needs-demo edges";

            _cachedCertification = report;
            return report;
        }

        public string GetPlayModeWarning()
        {
            var r = GetCertificationReport(force: true);
            if (!r.NeedsTraining) return null;
            return $"Map undertrained: {r.Summary}. Switch to Training/Validate before Play.";
        }

        // ---- Fall-death heatmap (session): where bots died falling into the void.
        // Path candidates near recent death spots score down and stage-1 explore avoids
        // them, so the same lip doesn't claim bot after bot. Entries decay (4 min). ----
        private readonly List<Vector4> _fallDeaths = new List<Vector4>(32); // xyz + timestamp

        public void ReportFallDeath(Vector3 lastGroundedPos)
        {
            _fallDeaths.Add(new Vector4(lastGroundedPos.x, lastGroundedPos.y, lastGroundedPos.z, Time.time));
            if (_fallDeaths.Count > 64) _fallDeaths.RemoveAt(0);
            Plugin.Log.LogInfo($"[NavGraph] Fall death recorded near {lastGroundedPos} ({_fallDeaths.Count} hot spots)");
        }

        /// <summary>Accumulated penalty for a position near recent fall deaths.
        /// ~1 per fresh death within 4m, fading to ~0.15 over 4 minutes.</summary>
        public float FallDeathPenalty(Vector3 pos)
        {
            if (_fallDeaths.Count == 0) return 0f;
            float penalty = 0f;
            float now = Time.time;
            for (int i = _fallDeaths.Count - 1; i >= 0; i--)
            {
                Vector4 d = _fallDeaths[i];
                float age = now - d.w;
                if (age > 240f) { _fallDeaths.RemoveAt(i); continue; }
                float dx = pos.x - d.x, dz = pos.z - d.z;
                if (dx * dx + dz * dz < 16f && Mathf.Abs(pos.y - d.y) < 4f)
                    penalty += Mathf.Lerp(1f, 0.15f, age / 240f);
            }
            return penalty;
        }

        /// <summary>Training events worth surfacing. The UI hint panel was removed in the
        /// stage redesign, so these go to the log now (grep "[TrainingHint]").</summary>
        public void SetTrainingHint(string message, float seconds = 12f)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Plugin.Log.LogInfo($"[TrainingHint] {message}");
        }

        public void SuppressValidationLabel(string label, float seconds, string message = null)
        {
            if (!string.IsNullOrWhiteSpace(label))
                _validationLabelCooldowns[label] = Time.time + Mathf.Max(1f, seconds);
            if (!string.IsNullOrWhiteSpace(message))
                SetTrainingHint(message, seconds);
        }

        public bool IsTrustedForPlay(NavEdge edge)
        {
            if (edge == null || edge.Confidence <= 0f) return false;
            return edge.TrustState == EdgeTrustState.BotValidated
                || edge.TrustState == EdgeTrustState.PlayerProven;
        }

        public bool IsBadForPlay(NavEdge edge)
        {
            if (edge == null || edge.Confidence <= 0f) return true;
            return edge.TrustState == EdgeTrustState.Blocked
                || edge.TrustState == EdgeTrustState.NeedsDemo;
        }

        public void RebuildTrustStatesFromLegacy()
        {
            foreach (var edge in Edges)
            {
                if (edge == null) continue;
                if (edge.Confidence <= 0f)
                {
                    edge.TrustState = EdgeTrustState.Blocked;
                    continue;
                }

                if (edge.BotValidationSuccesses >= BOT_VALIDATION_SUCCESSES_TO_TRUST)
                {
                    edge.TrustState = EdgeTrustState.BotValidated;
                    continue;
                }

                var from = GetNodeById(edge.From);
                var to = GetNodeById(edge.To);
                bool playerEdge = from != null && to != null && from.PlayerSourced && to.PlayerSourced;
                if (playerEdge || IsProvenEdge(edge.From, edge.To))
                {
                    if (edge.TrustState == EdgeTrustState.Candidate
                        || edge.TrustState == EdgeTrustState.BotTesting)
                        edge.TrustState = EdgeTrustState.PlayerProven;
                    continue;
                }

                if (edge.BotValidationFailures >= BOT_VALIDATION_FAILURES_TO_DEMO && edge.BotValidationSuccesses == 0)
                    edge.TrustState = EdgeTrustState.NeedsDemo;
            }
        }

        public void ReportRouteValidation(List<int> nodeIds, bool success, string reason = null)
        {
            if (nodeIds == null || nodeIds.Count < 2 || IsLocked) return;
            for (int i = 0; i + 1 < nodeIds.Count; i++)
            {
                var edge = GetEdgeBetween(nodeIds[i], nodeIds[i + 1]);
                if (edge == null) continue;
                ReportEdgeValidation(edge, success);
            }
            _dirty = true;
            _cachedCertification = null;
            if (!success && !string.IsNullOrWhiteSpace(reason))
                Plugin.Log.LogInfo($"[NavGraph] Route validation failed: {reason}");
        }

        public void ReportEdgeValidation(NavEdge edge, bool success)
        {
            if (edge == null || edge.Confidence <= 0f) return;
            edge.LastValidationTime = Time.time;
            if (success)
            {
                edge.BotValidationSuccesses++;
                edge.BotValidationFailures = Mathf.Max(0, edge.BotValidationFailures - 1);
                if (edge.TrustState == EdgeTrustState.Candidate)
                    edge.TrustState = EdgeTrustState.BotTesting;
                if (edge.BotValidationSuccesses >= BOT_VALIDATION_SUCCESSES_TO_TRUST)
                    edge.TrustState = EdgeTrustState.BotValidated;
                long packed = ((long)edge.From << 32) | (uint)edge.To;
                _demoNeededEdges.Remove(packed);
            }
            else
            {
                edge.BotValidationFailures++;
                if (edge.TrustState == EdgeTrustState.Candidate)
                    edge.TrustState = EdgeTrustState.BotTesting;
                if (edge.BotValidationFailures >= BOT_VALIDATION_FAILURES_TO_DEMO
                    && edge.BotValidationSuccesses < 2)
                {
                    bool newlyNeedsDemo = edge.TrustState != EdgeTrustState.NeedsDemo;
                    edge.TrustState = EdgeTrustState.NeedsDemo;
                    _demoNeededEdges.Add(((long)edge.From << 32) | (uint)edge.To);
                    if (newlyNeedsDemo)
                        SetTrainingHint("Bots keep failing one route — if you happen to walk that area yourself, they learn it instantly.", 15f);
                }
            }
        }

        public bool TryGetValidationRoute(Vector3 startPos, int botId, out Vector3 target,
            out List<NavNode> path, out string label)
        {
            target = Vector3.zero;
            path = null;
            label = null;
            if (!HasData) return false;

            float bestScore = float.MinValue;
            Vector3 bestTarget = Vector3.zero;
            List<NavNode> bestPath = null;
            string bestLabel = null;

            void Consider(Vector3 pos, string routeLabel, bool requireTargetClose)
            {
                if (IsValidationLabelSuppressed(routeLabel)) return;
                bool wantsHeight = Mathf.Abs(pos.y - startPos.y) > 2.25f;
                var candidate = FindPath(startPos, pos, jitter: 0.02f, searchRadius: 100f, preferHeight: wantsHeight);
                if (candidate == null || candidate.Count <= 1) return;
                if (PathHasBadEdges(candidate)) return;
                float score = ScoreValidationPath(candidate, pos);
                if (routeLabel == "PendingEdge") score += 30f; // stage-3 work itself — beats everything while any remain
                else if (IsWeaponLocationLabel(routeLabel)) score += 25f;
                else if (routeLabel == "Spawn") score += 4f;
                float endDist = Vector3.Distance(candidate[candidate.Count - 1].Position, pos);
                if (requireTargetClose && endDist > 6f) return;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = candidate[candidate.Count - 1].Position;
                    bestPath = candidate;
                    bestLabel = routeLabel;
                }
            }

            // PERFORMANCE: every Consider() is a full graph pathfind. Only try the few
            // nearest non-suppressed locations instead of scanning every location on the
            // map (with callers throttled, this brings stage 3 from a pathfind storm to
            // a handful of searches per bot every couple of seconds).
            var nearLocs = new List<KeyValuePair<float, int>>();
            for (int li = 0; li < MapLocations.Count; li++)
            {
                var loc = MapLocations[li];
                if (loc.label == "PatrolPoint") continue;
                if (IsValidationLabelSuppressed(loc.label)) continue;
                var node = GetNodeById(loc.nodeId);
                if (node == null || node.Confidence <= 0f) continue;
                nearLocs.Add(new KeyValuePair<float, int>(
                    (node.Position - startPos).sqrMagnitude, li));
            }
            nearLocs.Sort((a, b) => a.Key.CompareTo(b.Key));
            int considered = 0;
            for (int i = 0; i < nearLocs.Count && considered < 5; i++)
            {
                var loc = MapLocations[nearLocs[i].Value];
                var node = GetNodeById(loc.nodeId);
                if (node == null) continue;
                Consider(node.Position, loc.label, requireTargetClose: true);
                considered++;
            }

            // Target PENDING SPECIAL EDGES directly — they are the actual stage-3 work.
            // The old flow only routed to weapons/spawns and hoped the path happened to
            // cross candidates, which is why Confirmation crawled on maps whose pending
            // jumps sit off the weapon routes. Nearest few, start offset by botId so
            // eight bots spread across different edges instead of piling onto one.
            var pending = new List<KeyValuePair<float, int>>();
            for (int ei = 0; ei < Edges.Count; ei++)
            {
                var edge = Edges[ei];
                if (edge == null || edge.Confidence <= 0f) continue;
                if (!IsSpecialTraversal(edge) || IsTrustedForPlay(edge) || IsBadForPlay(edge)) continue;
                var toNode = GetNodeById(edge.To);
                if (toNode == null || toNode.Confidence <= 0f) continue;
                float d2 = (toNode.Position - startPos).sqrMagnitude;
                if (d2 > 100f * 100f) continue;
                pending.Add(new KeyValuePair<float, int>(d2, ei));
            }
            if (pending.Count > 0)
            {
                pending.Sort((a, b) => a.Key.CompareTo(b.Key));
                int spread = Mathf.Min(pending.Count, 4);
                int start = Mathf.Abs(botId) % spread;
                int tried = 0;
                for (int i = start; i < pending.Count && tried < 3; i++, tried++)
                {
                    var toNode = GetNodeById(Edges[pending[i].Value].To);
                    if (toNode != null)
                        Consider(toNode.Position, "PendingEdge", requireTargetClose: true);
                }
            }

            var frontier = FindFrontierNode(startPos, 8f);
            if (frontier != null) Consider(frontier.Position, "Frontier", requireTargetClose: true);

            var coverage = GetLowestVisitReachableCell(startPos, 70f);
            if (coverage.HasValue) Consider(coverage.Value, "Coverage", requireTargetClose: false);

            target = bestTarget;
            path = bestPath;
            label = bestLabel;
            return path != null;
        }

        private float ScoreValidationPath(List<NavNode> path, Vector3 target)
        {
            float score = Mathf.Max(0f, Vector3.Distance(path[0].Position, target)
                - Vector3.Distance(path[path.Count - 1].Position, target));
            score -= path.Count * 0.08f;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                var edge = GetEdgeBetween(path[i].Id, path[i + 1].Id);
                if (edge == null) continue;
                if (edge.TrustState == EdgeTrustState.Candidate) score += 4f;
                else if (edge.TrustState == EdgeTrustState.BotTesting) score += 3f;
                else if (edge.TrustState == EdgeTrustState.PlayerProven) score += 2f;
                else if (edge.TrustState == EdgeTrustState.BotValidated) score -= 0.75f;
                // Untrusted SPECIAL edges (jumps/falls/ladders/teleporters) are what
                // stage 3 exists to confirm — the mesh already covers plain ground.
                if (IsSpecialTraversal(edge))
                    score += IsTrustedForPlay(edge) ? 2.5f : 12f;
            }

            return score;
        }

        private bool IsValidationLabelSuppressed(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;
            if (_validationLabelCooldowns.TryGetValue(label, out float until))
            {
                if (Time.time < until) return true;
                _validationLabelCooldowns.Remove(label);
            }
            return false;
        }

        private static bool IsWeaponLocationLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;
            return label != "Spawn" && label != "PatrolPoint" && label != "Teleporter";
        }

        // ---- Bot ground-truth location visits (per map, session-only) ----
        // Stage bars used to read from graph/mesh CONNECTIVITY, which the bake
        // satisfies instantly — stage 2 opened at 100% and stage 3's anchor term was
        // pre-filled, so training visibly "did nothing". These sets record locations
        // bots have PHYSICALLY stood at this session; stages 2-3 measure them instead.
        private string _botVisitMap;
        private readonly HashSet<int> _botVisitedWeaponLocs = new HashSet<int>();
        private readonly HashSet<int> _botWalkedAnchorLocs = new HashSet<int>();
        private readonly HashSet<int> _botWalkedPlayerNodeIds = new HashSet<int>();

        private void EnsureBotVisitMap()
        {
            if (_botVisitMap == CurrentMap) return;
            _botVisitMap = CurrentMap;
            _botVisitedWeaponLocs.Clear();
            _botWalkedAnchorLocs.Clear();
            _botWalkedPlayerNodeIds.Clear();
        }

        /// <summary>Called ~2Hz per bot (and for the player recorder) during Training.
        /// Marks any map location within 2.5m as physically visited. Bots additionally
        /// mark player-recorded nodes as re-walked — that is the "walk every path the
        /// player has made" training objective; the player walking their OWN fresh
        /// trail must not self-complete it, hence isPlayer.</summary>
        public void MarkBotAtPosition(Vector3 pos, bool isPlayer = false)
        {
            EnsureBotVisitMap();
            for (int i = 0; i < MapLocations.Count; i++)
            {
                var (lpos, label, _) = MapLocations[i];
                float dx = pos.x - lpos.x, dz = pos.z - lpos.z;
                if (dx * dx + dz * dz > 6.25f || Mathf.Abs(pos.y - lpos.y) > 2.5f) continue;
                if (label == "PatrolPoint") continue;
                _botWalkedAnchorLocs.Add(i);
                if (IsWeaponLocationLabel(label)) _botVisitedWeaponLocs.Add(i);
            }
            if (isPlayer) return;
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (n == null || !n.PlayerSourced || n.Confidence <= 0f) continue;
                if (_botWalkedPlayerNodeIds.Contains(n.Id)) continue;
                float dx = pos.x - n.Position.x, dz = pos.z - n.Position.z;
                if (dx * dx + dz * dz > 6.25f || Mathf.Abs(pos.y - n.Position.y) > 2.5f) continue;
                _botWalkedPlayerNodeIds.Add(n.Id);
            }
        }

        /// <summary>Fraction of player-recorded nodes a BOT has re-walked this session.
        /// 1 when the player hasn't recorded anything on this map.</summary>
        public float PlayerPathWalkProgress
        {
            get
            {
                EnsureBotVisitMap();
                int total = 0, walked = 0;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    var n = Nodes[i];
                    if (n == null || !n.PlayerSourced || n.Confidence <= 0f) continue;
                    total++;
                    if (_botWalkedPlayerNodeIds.Contains(n.Id)) walked++;
                }
                return total > 0 ? Mathf.Clamp01(walked / (float)total) : 1f;
            }
        }

        /// <summary>Nearest player-recorded node no bot has re-walked this session.</summary>
        public Vector3 FindNearestUnwalkedPlayerNode(Vector3 fromPos, System.Func<Vector3, bool> reject = null)
        {
            EnsureBotVisitMap();
            float best = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (n == null || !n.PlayerSourced || n.Confidence <= 0f) continue;
                if (_botWalkedPlayerNodeIds.Contains(n.Id)) continue;
                if (IsBlacklisted(n.Id)) continue;
                if (reject != null && reject(n.Position)) continue;
                float d = Vector3.Distance(fromPos, n.Position);
                if (d < best) { best = d; bestPos = n.Position; }
            }
            return bestPos;
        }

        public float BotWeaponVisitProgress
        {
            get
            {
                EnsureBotVisitMap();
                int total = 0;
                for (int i = 0; i < MapLocations.Count; i++)
                    if (IsWeaponLocationLabel(MapLocations[i].label)) total++;
                return total > 0 ? Mathf.Clamp01(_botVisitedWeaponLocs.Count / (float)total) : 1f;
            }
        }

        public float BotAnchorCircuitProgress
        {
            get
            {
                EnsureBotVisitMap();
                int total = 0;
                for (int i = 0; i < MapLocations.Count; i++)
                    if (MapLocations[i].label != "PatrolPoint") total++;
                return total > 0 ? Mathf.Clamp01(_botWalkedAnchorLocs.Count / (float)total) : 1f;
            }
        }

        public List<Vector3> GetUnvisitedWeaponPositions()
        {
            EnsureBotVisitMap();
            var outList = new List<Vector3>();
            for (int i = 0; i < MapLocations.Count; i++)
            {
                if (!IsWeaponLocationLabel(MapLocations[i].label)) continue;
                if (_botVisitedWeaponLocs.Contains(i)) continue;
                outList.Add(MapLocations[i].pos);
            }
            return outList;
        }

        /// <summary>Nearest weapon location no bot has stood at yet this session.</summary>
        public (Vector3 pos, string label) FindNearestUnvisitedWeapon(Vector3 fromPos, System.Func<Vector3, bool> reject = null)
        {
            EnsureBotVisitMap();
            float bestDist = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            string bestLabel = "";
            for (int i = 0; i < MapLocations.Count; i++)
            {
                var (lpos, label, _) = MapLocations[i];
                if (!IsWeaponLocationLabel(label)) continue;
                if (_botVisitedWeaponLocs.Contains(i)) continue;
                if (reject != null && reject(lpos)) continue;
                float dist = Vector3.Distance(fromPos, lpos);
                if (dist < bestDist) { bestDist = dist; bestPos = lpos; bestLabel = label; }
            }
            return (bestPos, bestLabel);
        }

        /// <summary>Next unwalked anchor for a stage-3 circuit; botId offsets the pick
        /// so multiple bots fan out over different anchors instead of converging.</summary>
        public Vector3 FindNextCircuitAnchor(Vector3 fromPos, int botId, System.Func<Vector3, bool> reject = null)
        {
            EnsureBotVisitMap();
            var candidates = new List<(float dist, Vector3 pos)>();
            for (int i = 0; i < MapLocations.Count; i++)
            {
                var (lpos, label, _) = MapLocations[i];
                if (label == "PatrolPoint") continue;
                if (_botWalkedAnchorLocs.Contains(i)) continue;
                if (reject != null && reject(lpos)) continue;
                candidates.Add((Vector3.Distance(fromPos, lpos), lpos));
            }
            if (candidates.Count == 0) return Vector3.zero;
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            return candidates[Mathf.Abs(botId) % candidates.Count].pos;
        }

        private void ApplyCertificationStage(MapCertificationReport report, List<int> weaponIds, List<int> anchorIds)
        {
            // Background hygiene (never shown as a stage): bad route points self-prune.
            if (report.BadNodeCount > 2 && !IsLocked && Time.time >= _nextAutoPruneTime)
            {
                _nextAutoPruneTime = Time.time + 20f;
                try
                {
                    PruneBadNodes(50);
                    Plugin.Log.LogInfo($"[NavGraph] Auto-pruned bad route points ({report.BadNodeCount} flagged)");
                }
                catch { }
            }

            // SINGLE-STAGE training. The bar blends everything bots must physically
            // do before the map counts as trained:
            //   coverage      — every reachable area walked
            //   weapons       — a bot stood at every weapon
            //   player paths  — bots re-walked every node the player recorded
            //   special edges — pending jumps/falls/ladders confirmed (NeedsDemo counts
            //                   as resolved; half-credit for one banked traversal)
            // Terms with nothing to do read 1, so empty maps don't stall the bar.
            report.StageNumber = 1;
            report.StageName = "TRAINING";
            float specialProgress = report.SpecialEdges > 0
                ? Mathf.Clamp01((report.TrustedSpecialEdges + report.NeedsDemoSpecialEdges
                    + 0.5f * report.InProgressSpecialEdges) / (float)report.SpecialEdges)
                : 1f;
            float coverage = report.CoverageProgress;
            float weapons = BotWeaponVisitProgress;
            float playerPaths = PlayerPathWalkProgress;
            float rawProgress = coverage * 0.35f + weapons * 0.25f
                + playerPaths * 0.25f + specialProgress * 0.15f;
            report.UnconnectedWeaponPositions = GetUnvisitedWeaponPositions();
            report.StageInstruction = "Bots walk every area, every weapon, and every path you've made. Finish when the bar fills.";
            report.NextButtonLabel = "Finish: Switch To Play";
            report.RecommendedBehavior = "Validate";
            TrackStageProgress(report, 1, rawProgress);
            report.PrimaryAction = null;
            report.NeedsTraining = report.StageProgress < 0.95f;
        }

        // ---- Stage progress high-water + settle detection ----
        // The displayed bar never regresses within a stage (denominators legitimately
        // grow as bots discover more map, which used to make bars run BACKWARD), and
        // when the metric stops moving while bots are actively training we say so
        // instead of stalling silently.
        private string _stageHwMap;
        private int _stageHwStage = -1;
        private float _stageHwValue;
        private float _stageLastGainTime;
        private const float STAGE_SETTLE_SECONDS = 40f;

        private void TrackStageProgress(MapCertificationReport report, int stage, float rawProgress)
        {
            rawProgress = Mathf.Clamp01(rawProgress);
            if (_stageHwMap != CurrentMap || _stageHwStage != stage)
            {
                _stageHwMap = CurrentMap;
                _stageHwStage = stage;
                _stageHwValue = rawProgress;
                _stageLastGainTime = Time.time;
            }
            if (rawProgress > _stageHwValue + 0.004f)
            {
                _stageHwValue = rawProgress;
                _stageLastGainTime = Time.time;
            }
            // Paused bots (or Play mode) can't make progress — don't count that as a stall.
            if (Plugin.TrainingPaused || Mode != NavMode.Training)
                _stageLastGainTime = Time.time;

            report.StageProgress = Mathf.Max(_stageHwValue, rawProgress);
            report.StageSettled = report.StageProgress < 0.99f
                && Time.time - _stageLastGainTime > STAGE_SETTLE_SECONDS;
        }

        private static bool NavMeshRouteExists(Vector3 from, Vector3 to)
        {
            if (!BotNavMesh.Ready) return false;
            var path = BotNavMesh.FindCornerPath(from, to, out bool complete);
            return path != null && complete;
        }

        // Per-anchor mesh-route results, 20s TTL. The report build ran a native
        // navmesh path query per anchor every 3s — 30+ queries in one frame on
        // weapon-heavy maps for answers that essentially never change mid-round.
        private readonly Dictionary<int, KeyValuePair<float, bool>> _nmRouteCache
            = new Dictionary<int, KeyValuePair<float, bool>>();
        private string _nmRouteCacheMap;

        private bool NavMeshRouteExistsCached(int anchorId, Vector3 from, Vector3 to)
        {
            if (_nmRouteCacheMap != CurrentMap)
            {
                _nmRouteCache.Clear();
                _nmRouteCacheMap = CurrentMap;
            }
            if (_nmRouteCache.TryGetValue(anchorId, out var e) && Time.time - e.Key < 20f)
                return e.Value;
            bool ok = NavMeshRouteExists(from, to);
            _nmRouteCache[anchorId] = new KeyValuePair<float, bool>(Time.time, ok);
            return ok;
        }

        /// <summary>Complete baked-mesh route from any spawn to pos — distinguishes
        /// genuinely walkable spots from isolated bake islands.</summary>
        private static bool MeshRoutedFromAnySpawn(List<Vector3> spawnPositions, Vector3 pos)
        {
            for (int i = 0; i < spawnPositions.Count; i++)
                if (NavMeshRouteExists(spawnPositions[i], pos)) return true;
            return false;
        }

        private int CountBadNodes(out Vector3 worstPos, out string worstReason, out int worstStrikes)
        {
            int count = 0;
            worstPos = Vector3.zero;
            worstReason = null;
            worstStrikes = 0;
            foreach (var item in BadNodePositions())
            {
                count++;
                if (item.strikes > worstStrikes)
                {
                    worstStrikes = item.strikes;
                    worstPos = item.pos;
                    worstReason = item.reason;
                }
            }
            return count;
        }

        private bool PathHasBadEdges(List<NavNode> path)
        {
            for (int i = 0; i + 1 < path.Count; i++)
            {
                if (IsBadForPlay(GetEdgeBetween(path[i].Id, path[i + 1].Id)))
                    return true;
            }
            return false;
        }

        /// <summary>Trusted special edge whose endpoints match a from→to hop (used to
        /// execute mesh-link crossings with the recorded trajectory instead of a blind
        /// reactive jump).</summary>
        public NavEdge FindTrustedSpecialEdgeNear(Vector3 from, Vector3 to, float tolerance = 2f)
        {
            float tolSqr = tolerance * tolerance;
            NavEdge best = null;
            float bestScore = float.MaxValue;
            foreach (var e in Edges)
            {
                if (e == null || e.Confidence <= 0f) continue;
                if (!IsSpecialTraversal(e) || !IsTrustedForPlay(e)) continue;
                var fn = GetNodeById(e.From);
                var tn = GetNodeById(e.To);
                if (fn == null || tn == null) continue;
                float dF = (fn.Position - from).sqrMagnitude;
                float dT = (tn.Position - to).sqrMagnitude;
                if (dF > tolSqr || dT > tolSqr) continue;
                float s = dF + dT;
                if (s < bestScore) { bestScore = s; best = e; }
            }
            return best;
        }

        private static bool IsSpecialTraversal(NavEdge edge)
        {
            if (edge == null) return false;
            return edge.Type == EdgeType.Jump || edge.Type == EdgeType.WallJump
                || edge.Type == EdgeType.Fall || edge.Type == EdgeType.Ladder
                || edge.Type == EdgeType.Slide || edge.Type == EdgeType.Teleporter;
        }

        private int CountConnectedAnchors(List<int> anchorIds, bool trustedOnly)
        {
            if (anchorIds.Count == 0) return 0;
            int connected = 0;
            foreach (int anchor in anchorIds)
            {
                var reachable = FloodReachable(anchor, trustedOnly);
                bool hasOther = false;
                foreach (int other in anchorIds)
                {
                    if (other != anchor && reachable.Contains(other))
                    {
                        hasOther = true;
                        break;
                    }
                }
                if (hasOther) connected++;
            }
            return connected;
        }

        private int CountValidatedAnchorRoutes(List<int> anchorIds)
        {
            if (anchorIds.Count <= 1) return 0;
            int count = 0;
            for (int i = 0; i < anchorIds.Count; i++)
            {
                var reachable = FloodReachable(anchorIds[i], trustedOnly: true);
                for (int j = i + 1; j < anchorIds.Count; j++)
                    if (reachable.Contains(anchorIds[j])) count++;
            }
            return count;
        }

        private int CountValidatedWeapons(List<int> weaponIds, List<int> anchorIds)
        {
            if (weaponIds.Count == 0 || anchorIds.Count == 0) return 0;
            int count = 0;
            foreach (int weaponId in weaponIds)
            {
                var reachable = FloodReachable(weaponId, trustedOnly: true);
                bool linked = false;
                foreach (int anchor in anchorIds)
                {
                    if (anchor == weaponId) continue;
                    if (reachable.Contains(anchor))
                    {
                        linked = true;
                        break;
                    }
                }
                if (linked) count++;
            }
            return count;
        }

        private HashSet<int> FloodReachable(int startId, bool trustedOnly)
        {
            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            visited.Add(startId);
            queue.Enqueue(startId);

            int guard = 0;
            while (queue.Count > 0 && guard++ < 5000)
            {
                int id = queue.Dequeue();
                if (!_edgesByFrom.TryGetValue(id, out var edges)) continue;
                foreach (int ei in edges)
                {
                    if (ei < 0 || ei >= Edges.Count) continue;
                    var edge = Edges[ei];
                    if (edge == null || edge.Confidence <= 0f) continue;
                    if (trustedOnly && !IsTrustedForPlay(edge)) continue;
                    if (IsBadForPlay(edge)) continue;
                    var to = GetNodeById(edge.To);
                    if (to == null || to.Confidence <= 0f) continue;
                    if (visited.Add(edge.To)) queue.Enqueue(edge.To);
                }
            }
            return visited;
        }
    }
}
