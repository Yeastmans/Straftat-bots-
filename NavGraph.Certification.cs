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
        public string Summary;
    }

    public partial class NavGraph
    {
        private const int BOT_VALIDATION_SUCCESSES_TO_TRUST = 3;
        private const int BOT_VALIDATION_FAILURES_TO_DEMO = 3;
        private const int MIN_PLAY_NODES = 200;
        private MapCertificationReport _cachedCertification;
        private float _lastCertificationTime = -999f;
        private readonly Dictionary<string, float> _validationLabelCooldowns = new Dictionary<string, float>();
        private string _trainingHint;
        private float _trainingHintUntil;

        private static EdgeTrustState NormalizeTrustState(byte value)
        {
            return value <= (byte)EdgeTrustState.Blocked
                ? (EdgeTrustState)value
                : EdgeTrustState.Candidate;
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

            int connectedWeapons = 0;
            foreach (int wid in weaponIds) if (reachableFromSpawn.Contains(wid)) connectedWeapons++;
            report.ConnectedWeaponCount = connectedWeapons;

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

            report.Score = Mathf.Clamp(nodeScore + edgeScore + anchorScore + routeScore + weaponScore + trustScore + specialScore
                - candidatePenalty - badNodePenalty, 0f, 100f);
            report.NeedsTraining = report.ActiveNodes < MIN_PLAY_NODES
                || report.ReachableNodeFraction < 0.6f
                || report.Score < 70f
                || report.BadNodeCount > 2
                || (connectedWeapons > 0 && report.ValidatedWeaponRoutes < Mathf.Max(1, connectedWeapons / 2))
                || (report.AnchorCount > 2 && report.ConnectedAnchors < Mathf.Max(2, report.AnchorCount / 2));

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

        public void SetTrainingHint(string message, float seconds = 12f)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _trainingHint = message;
            _trainingHintUntil = Time.time + Mathf.Max(1f, seconds);
            _cachedCertification = null;
        }

        public string GetTrainingHint()
        {
            return Time.time <= _trainingHintUntil ? _trainingHint : null;
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
                        SetTrainingHint("WALK THIS YOURSELF: follow the START and END markers and just walk the route once — it becomes trusted instantly.", 30f);
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
                if (IsWeaponLocationLabel(routeLabel)) score += 25f;
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

            foreach (var loc in MapLocations)
            {
                if (loc.label == "PatrolPoint") continue;
                var node = GetNodeById(loc.nodeId);
                if (node == null || node.Confidence <= 0f) continue;
                Consider(node.Position, loc.label, requireTargetClose: true);
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
                if (IsSpecialTraversal(edge)) score += 2.5f;
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

        private void ApplyCertificationStage(MapCertificationReport report, List<int> weaponIds, List<int> anchorIds)
        {
            string liveHint = GetTrainingHint();
            if (!string.IsNullOrWhiteSpace(liveHint))
                report.DemoHint = liveHint;

            // Stage 0 (only when bad data is really piling up): clean it so it stops dragging
            // the mesh down. A couple of bad points are tolerated — Play is non-blocking anyway.
            if (report.BadNodeCount > 2)
            {
                report.StageNumber = 0;
                report.StageName = "Repair: Bad Data";
                report.StageProgress = report.CleanupProgress;
                report.StageInstruction = "Several route points keep failing and are dragging the mesh down.";
                report.PrimaryAction = "Press Clean Bad Nodes, then keep building the mesh.";
                report.NextButtonLabel = "Clean Bad Nodes";
                report.RecommendedBehavior = "Validate";
                return;
            }

            // Stage 1 — BUILD THE MESH (the initial connect pass): the player walks and bots
            // Explore until most of the map AND its weapons are reachable from spawn.
            bool meshThin = report.ActiveNodes < MIN_PLAY_NODES
                || report.ReachableNodeFraction < 0.6f
                || (report.AnchorCount > 2 && report.ConnectedAnchors < Mathf.Max(2, report.AnchorCount / 2));
            if (meshThin)
            {
                report.StageNumber = 1;
                report.StageName = "Stage 1: Build the Mesh";
                report.StageProgress = Mathf.Min(report.CoverageProgress, report.ConnectionProgress);
                report.StageInstruction = "Connect the map: walk it yourself and let bots Explore so most rooms, "
                    + "upper levels, ladders, jumps and weapon spawns link up from a spawn.";
                report.PrimaryAction = "Walk around (you train fastest) and run Explore. Reach every area and weapon.";
                report.NextButtonLabel = "Run Explore";
                report.RecommendedBehavior = "Explore";
                TryAssignUnvalidatedWeaponTarget(report, weaponIds, anchorIds);
                return;
            }

            // Stage 2 — VALIDATE: the mesh is connected; bots prove the routes are reliable.
            // Anything they keep failing is surfaced as a "walk this yourself" route — no
            // recording ceremony, your normal walking is already trusted.
            if (report.Score < 70f || report.TrustProgress < 0.6f
                || (report.ConnectedWeaponCount > 0 && report.ValidatedWeaponRoutes < Mathf.Max(1, report.ConnectedWeaponCount / 2)))
            {
                report.StageNumber = 2;
                report.StageName = "Stage 2: Validate";
                report.StageProgress = Mathf.Clamp01(report.TrustProgress / 0.6f);
                report.StageInstruction = "The mesh is connected; bots now need repetition to trust the routes.";
                report.PrimaryAction = "Run Validate. If a marked route keeps failing, just walk it yourself once — that instantly trusts it.";
                report.NextButtonLabel = "Run Validate";
                report.RecommendedBehavior = "Validate";
                TryAssignUnvalidatedWeaponTarget(report, weaponIds, anchorIds);
                TryAssignNeedsDemoTarget(report);
                return;
            }

            // Stage 3 — READY. A soft finish line: bots keep learning during Play too.
            report.StageNumber = 3;
            report.StageName = "Ready";
            report.StageProgress = 1f;
            report.StageInstruction = "Map is connected and validated. Bots keep improving it during Play.";
            report.PrimaryAction = "Switch to Play whenever you like — or keep playing and bots keep learning.";
            report.NextButtonLabel = "Switch To Play";
            report.RecommendedBehavior = "None";
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

        private bool TryAssignNeedsDemoTarget(MapCertificationReport report)
        {
            foreach (long packed in _demoNeededEdges)
            {
                int from = (int)(packed >> 32);
                int to = (int)(packed & 0xFFFFFFFF);
                var fromNode = GetNodeById(from);
                var toNode = GetNodeById(to);
                if (fromNode == null || toNode == null) continue;
                report.HasTargetPosition = true;
                report.TargetPosition = Vector3.Lerp(fromNode.Position, toNode.Position, 0.5f);
                report.HasRouteTarget = true;
                report.RouteStartPosition = fromNode.Position;
                report.RouteEndPosition = toNode.Position;
                report.TargetLabel = "Walk this route: START -> END";
                return true;
            }

            foreach (var edge in Edges)
            {
                if (edge == null || edge.Confidence <= 0f || edge.TrustState != EdgeTrustState.NeedsDemo) continue;
                var fromNode = GetNodeById(edge.From);
                var toNode = GetNodeById(edge.To);
                if (fromNode == null || toNode == null) continue;
                report.HasTargetPosition = true;
                report.TargetPosition = Vector3.Lerp(fromNode.Position, toNode.Position, 0.5f);
                report.HasRouteTarget = true;
                report.RouteStartPosition = fromNode.Position;
                report.RouteEndPosition = toNode.Position;
                report.TargetLabel = $"Walk this {edge.Type} route: START -> END";
                return true;
            }
            return false;
        }

        private bool TryAssignUnvalidatedWeaponTarget(MapCertificationReport report, List<int> weaponIds, List<int> anchorIds)
        {
            foreach (var loc in MapLocations)
            {
                if (!IsWeaponLocationLabel(loc.label)) continue;
                if (!weaponIds.Contains(loc.nodeId)) continue;
                if (WeaponHasTrustedConnection(loc.nodeId, anchorIds)) continue;
                var node = GetNodeById(loc.nodeId);
                if (node == null || node.Confidence <= 0f) continue;
                report.HasTargetPosition = true;
                report.TargetPosition = node.Position;
                report.TargetLabel = $"Weapon route: {loc.label}";
                return true;
            }
            return false;
        }

        private bool WeaponHasTrustedConnection(int weaponId, List<int> anchorIds)
        {
            var reachable = FloodReachable(weaponId, trustedOnly: true);
            foreach (int anchor in anchorIds)
            {
                if (anchor == weaponId) continue;
                if (reachable.Contains(anchor)) return true;
            }
            return false;
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
