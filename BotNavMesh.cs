using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StraftatBots
{
    /// <summary>
    /// Runtime-baked Unity NavMesh: ground navigation that works on every map with zero
    /// training. STRAFTAT's map colliders live on arbitrary layers and mostly use
    /// non-readable meshes, so collider-based CollectSources CANNOT work (verified: 0
    /// triangles). Instead the world is sampled with a physics voxel scan — raycasts hit
    /// every collider regardless of layer or mesh readability — and each detected walkable
    /// surface cell becomes a small box build-source. Ladders (found by tag/name, same as
    /// bot ladder logic) are added as NavMesh links so paths route up/down levels.
    /// The learned NavGraph stays authoritative for jumps/teleporters; paths come back as
    /// synthetic NavNodes with NEGATIVE ids so graph-mutation code can skip them.
    /// </summary>
    public static class BotNavMesh
    {
        // World geometry layers only (game TagManager): Default, TransparentFX, Water,
        // Interactable, Ladder, ShootThrough, InteractEnvironment, Glass. Excludes all
        // actor/weapon/ragdoll layers AND InvisibleWall (its tops must not become floors —
        // it is added separately as carve geometry instead).
        private const int SCAN_MASK = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 7) | (1 << 10)
            | (1 << 14) | (1 << 19) | (1 << 24);
        private const float MAX_SLOPE_NORMAL_Y = 0.573f; // cos(55°) — game slope limit
        private const float MIN_HEADROOM = 1.05f;        // crouch height + margin

        private static NavMeshDataInstance _instance;
        private static readonly List<NavMeshLinkInstance> _links = new List<NavMeshLinkInstance>();
        // Trusted learned jump/fall/teleporter edges mirrored as NavMesh links — ONE
        // CalculatePath then plans ground→jump→ladder chains natively instead of the
        // mesh and graph being stitched together at route-commit time.
        private static readonly List<NavMeshLinkInstance> _graphLinks = new List<NavMeshLinkInstance>();
        private static int _graphLinkSignature = -1;
        private static bool _baked;
        private static string _bakedScene;
        private static int _nextSyntheticId = -10;
        private static readonly NavMeshPath _queryPath = new NavMeshPath();

        // ---- Walked-coverage tracking (training stage 1) ----
        // Every baked scan cell starts UNWALKED and fills in as bots/players actually
        // walk over it. "Reachable" is decided once at bake time by a flood fill from
        // the spawn/actor positions (bridged through ladder links), so out-of-bounds
        // bake islands — roof tops, interior raycast leftovers — never count against
        // coverage. Session-only, like the training stage itself.
        private const float Y_BAND = 0.5f;
        private static readonly HashSet<long> _allCells = new HashSet<long>();
        private static readonly HashSet<long> _reachableCells = new HashSet<long>();
        private static readonly HashSet<long> _walkedCells = new HashSet<long>(); // walked ∩ reachable
        private static readonly List<long> _reachableList = new List<long>();     // indexed copy for random sampling
        private static readonly List<KeyValuePair<Vector3, Vector3>> _linkEnds = new List<KeyValuePair<Vector3, Vector3>>();
        private static Vector3 _gridOrigin;
        private static float _cellSize = 0.5f;
        private static bool _seedDegraded; // bake-time flood had no valid seed

        // Per-cell build sources kept so "Clear Unwalked Areas" can REBUILD the actual
        // NavMesh from surviving cells — pruning only the coverage sets left the mesh
        // (and its cyan wireframe) still covering out-of-bounds areas.
        private static readonly Dictionary<long, NavMeshBuildSource> _cellSources = new Dictionary<long, NavMeshBuildSource>();
        private static readonly List<NavMeshBuildSource> _carveSources = new List<NavMeshBuildSource>();
        private static Bounds _bakeBounds;
        private static NavMeshBuildSettings _bakeSettings;

        /// <summary>0..1 — fraction of the spawn-reachable baked ground actually walked this session.</summary>
        public static float WalkedCoverage
            => _reachableCells.Count > 0 ? (float)_walkedCells.Count / _reachableCells.Count : 0f;

        public static int ReachableCellCount => _reachableCells.Count;
        public static int WalkedCellCount => _walkedCells.Count;

        public static bool Ready => _baked && (Plugin.UseNavMesh == null || Plugin.UseNavMesh.Value);
        public static string BakedScene => _bakedScene;
        public static string Status { get; private set; } = "not baked";

        // Cached triangulation — drawn by BotDebugVisualizer so the mesh is visible in game.
        public static Vector3[] TriVertices { get; private set; }
        public static int[] TriIndices { get; private set; }

        public static void Clear()
        {
            if (_instance.valid) _instance.Remove();
            _instance = default;
            foreach (var link in _links)
                if (link.valid) link.Remove();
            _links.Clear();
            foreach (var link in _graphLinks)
                if (link.valid) link.Remove();
            _graphLinks.Clear();
            _graphLinkSignature = -1;
            _baked = false;
            _bakedScene = null;
            TriVertices = null;
            TriIndices = null;
            _allCells.Clear();
            _reachableCells.Clear();
            _walkedCells.Clear();
            _reachableList.Clear();
            _linkEnds.Clear();
            _cellSources.Clear();
            _carveSources.Clear();
            _healedCellCount = 0;
            _nextHealRebuildTime = 0f;
            Status = "not baked";
        }

        public static void Bake(string sceneName)
        {
            Clear();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // World bounds from every non-trigger collider (any layer — map geometry
                // is scattered across many).
                var colliders = Object.FindObjectsOfType<Collider>();
                bool first = true;
                Bounds bounds = new Bounds(Vector3.zero, Vector3.one);
                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    int layer = col.gameObject.layer;
                    if (layer == 3 || layer == 6 || layer == 11 || layer == 18) continue;
                    if (first) { bounds = col.bounds; first = false; }
                    else bounds.Encapsulate(col.bounds);
                }
                if (first)
                {
                    Status = "no geometry";
                    Plugin.Log.LogWarning($"[NavMesh] {sceneName}: no colliders found");
                    return;
                }
                bounds.Expand(2f);
                // Sanity clamp — a stray far-away collider must not explode the scan grid.
                Vector3 c = bounds.center, s = bounds.size;
                s.x = Mathf.Min(s.x, 600f); s.y = Mathf.Min(s.y, 400f); s.z = Mathf.Min(s.z, 600f);
                bounds = new Bounds(c, s);

                // Live actor positions — skip surface hits on/next to them (their body
                // colliders would otherwise become phantom floor patches).
                var actorPositions = new List<Vector3>();
                foreach (var fpc in Object.FindObjectsOfType<FirstPersonController>())
                    if (fpc != null) actorPositions.Add(fpc.transform.position);

                // Cell size: 0.5m preferred, coarser only on truly huge maps. (1.0m cells made
                // corridor mesh too patchy for routing — bots fell back to the learned graph.)
                float area = bounds.size.x * bounds.size.z;
                float cell = Mathf.Clamp(Mathf.Sqrt(area / 400000f), 0.5f, 1.0f);
                int nx = Mathf.CeilToInt(bounds.size.x / cell);
                int nz = Mathf.CeilToInt(bounds.size.z / cell);

                _gridOrigin = bounds.min;
                _cellSize = cell;

                var sources = new List<NavMeshBuildSource>(16384);
                Vector3 boxSize = new Vector3(cell * 1.15f, 0.15f, cell * 1.15f);
                float topY = bounds.max.y + 1f;
                float bottomY = bounds.min.y - 1f;
                int rayCount = 0;

                for (int ix = 0; ix < nx; ix++)
                {
                    float x = bounds.min.x + (ix + 0.5f) * cell;
                    for (int iz = 0; iz < nz; iz++)
                    {
                        float z = bounds.min.z + (iz + 0.5f) * cell;
                        float y = topY;
                        bool firstHitInColumn = true;
                        float prevHitY = topY;
                        // Walk down through every stacked surface in this column.
                        for (int guard = 0; guard < 10 && y > bottomY; guard++)
                        {
                            rayCount++;
                            if (!Physics.Raycast(new Vector3(x, y, z), Vector3.down, out var hit,
                                    y - bottomY, SCAN_MASK, QueryTriggerInteraction.Ignore))
                                break;
                            y = hit.point.y - 0.12f;
                            bool wasFirst = firstHitInColumn;
                            float roofY = prevHitY;
                            firstHitInColumn = false;
                            prevHitY = hit.point.y;

                            if (hit.normal.y < MAX_SLOPE_NORMAL_Y) continue;   // too steep to stand on

                            if (wasFirst)
                            {
                                // Open-sky surface: plain headroom test.
                                rayCount++;
                                if (Physics.Raycast(hit.point + Vector3.up * 0.1f, Vector3.up,
                                        MIN_HEADROOM - 0.1f, SCAN_MASK, QueryTriggerInteraction.Ignore))
                                    continue;
                            }
                            else
                            {
                                // Any hit BELOW another surface must see a real ceiling above it
                                // (a downward-facing front face). Raycasts pass through backfaces,
                                // so a "floor" seen through the inside of a solid wall sees nothing
                                // above — that phantom cell would let paths tunnel THROUGH walls.
                                rayCount++;
                                float maxUp = Mathf.Max(0.5f, roofY - hit.point.y);
                                if (!Physics.Raycast(hit.point + Vector3.up * 0.05f, Vector3.up,
                                        out var ceil, maxUp, SCAN_MASK, QueryTriggerInteraction.Ignore))
                                    continue;                                   // no ceiling = inside solid geometry
                                if (ceil.distance < MIN_HEADROOM - 0.05f)
                                    continue;                                   // ceiling too low even to crouch
                            }

                            bool nearActor = false;
                            for (int a = 0; a < actorPositions.Count; a++)
                            {
                                Vector3 d = hit.point - actorPositions[a];
                                if (Mathf.Abs(d.y) < 2.5f && d.x * d.x + d.z * d.z < 2.25f) { nearActor = true; break; }
                            }
                            if (nearActor) continue;

                            var cellSource = new NavMeshBuildSource
                            {
                                shape = NavMeshBuildSourceShape.Box,
                                size = boxSize,
                                transform = Matrix4x4.TRS(hit.point - hit.normal * 0.08f,
                                    Quaternion.FromToRotation(Vector3.up, hit.normal), Vector3.one),
                                area = 0
                            };
                            sources.Add(cellSource);
                            long cellKey = CellKey(ix, Mathf.RoundToInt(hit.point.y / Y_BAND), iz);
                            _allCells.Add(cellKey);
                            _cellSources[cellKey] = cellSource;
                        }
                    }
                }

                if (sources.Count < 16)
                {
                    Status = $"scan found no walkable ground ({sources.Count} cells)";
                    Plugin.Log.LogWarning($"[NavMesh] {sceneName}: {Status}");
                    return;
                }

                // InvisibleWall colliders (layer 27) carve the mesh as not-walkable volumes —
                // paths must respect boundaries players can't cross either.
                int carveCount = 0;
                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    if (col.gameObject.layer != 27) continue;
                    var wb = col.bounds;
                    var carveSource = new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Box,
                        size = wb.size + new Vector3(0.2f, 0.2f, 0.2f),
                        transform = Matrix4x4.TRS(wb.center, Quaternion.identity, Vector3.one),
                        area = 1 // Not Walkable
                    };
                    sources.Add(carveSource);
                    _carveSources.Add(carveSource);
                    carveCount++;
                }

                // Keep agentTypeID 0 so plain NavMesh.CalculatePath queries hit this surface.
                var settings = NavMesh.GetSettingsByID(0);
                settings.agentRadius = 0.4f;  // game CC radius
                settings.agentHeight = 2.0f;  // game CC height
                settings.agentClimb = 0.58f;  // game step offset 0.6
                settings.agentSlope = 55f;    // game slope limit

                _bakeBounds = bounds;
                _bakeSettings = settings;
                var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds,
                    Vector3.zero, Quaternion.identity);
                if (data == null)
                {
                    Status = "bake failed";
                    Plugin.Log.LogWarning($"[NavMesh] {sceneName}: BuildNavMeshData returned null");
                    return;
                }

                _instance = NavMesh.AddNavMeshData(data);
                _baked = true;
                _bakedScene = sceneName;
                _nextSyntheticId = -10;

                var tri = NavMesh.CalculateTriangulation();
                TriVertices = tri.vertices;
                TriIndices = tri.indices;
                int triCount = TriIndices != null ? TriIndices.Length / 3 : 0;

                if (triCount < 8)
                {
                    Status = $"UNUSABLE: only {triCount} triangles from {sources.Count} scan cells";
                    Plugin.Log.LogWarning($"[NavMesh] {sceneName}: {Status} — falling back to learned graph only");
                    _baked = false;
                    return;
                }

                int ladderLinks = AddLadderLinks(colliders);

                // Coverage flood fill: seed from live actor positions (everyone stands at
                // spawn when the bake runs) plus any learned Spawn locations. Anything the
                // flood can't reach is an out-of-bounds bake island and never counts
                // against stage-1 coverage.
                var seeds = new List<Vector3>(actorPositions);
                try
                {
                    // The game's own spawn points are the most reliable seed — on a fresh
                    // map there are no learned Spawn locations and the player/bots may not
                    // have spawned yet when the bake runs.
                    foreach (var sp in Object.FindObjectsOfType<SpawnPoint>())
                        if (sp != null) seeds.Add(sp.transform.position);
                    if (NavGraph.Instance != null)
                        foreach (var loc in NavGraph.Instance.MapLocations)
                            if (loc.label == "Spawn") seeds.Add(loc.pos);
                }
                catch { }
                ComputeReachability(seeds);

                SyncGraphLinks(force: true);

                // Saved walked coverage (NavData v6) can only be applied now that the
                // cell grid exists.
                try { NavGraph.Instance?.ApplyPendingWalkedRestore(); } catch { }

                Status = $"baked in {sw.ElapsedMilliseconds}ms ({sources.Count} cells, {triCount} tris, {ladderLinks} ladders)";
                Plugin.Log.LogInfo($"[NavMesh] {sceneName}: {Status} — {rayCount} rays, cell {cell:F2}m, {carveCount} invisible walls carved");
            }
            catch (System.Exception ex)
            {
                Clear();
                Status = "bake error";
                Plugin.Log.LogWarning($"[NavMesh] Bake failed for {sceneName}: {ex}");
            }
        }

        /// <summary>Ladders become NavMesh links so CalculatePath routes up/down levels.
        /// Same identification the bots use: ladder tag or "ladder" in the object name.</summary>
        private static int AddLadderLinks(Collider[] colliders)
        {
            int added = 0;
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;
                string tag = "";
                try { tag = col.tag; } catch { }
                // Layer 10 IS the game's Ladder layer (TagManager) — the most reliable signal.
                bool isLadder = col.gameObject.layer == 10
                    || tag == "Ladder/Metal" || tag == "Ladder/Chain"
                    || col.gameObject.name.ToLower().Contains("ladder");
                if (!isLadder) continue;

                var b = col.bounds;
                if (b.size.y < 1.5f) continue; // too short to be a climbable ladder

                if (!TrySampleAround(new Vector3(b.center.x, b.min.y, b.center.z), 1.2f, out Vector3 start))
                    continue;
                if (!TrySampleAround(new Vector3(b.center.x, b.max.y, b.center.z), 1.6f, out Vector3 end))
                    continue;
                if (end.y - start.y < 1f) continue;

                var inst = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = start,
                    endPosition = end,
                    width = 0.6f,
                    bidirectional = true,
                    area = 0,
                    agentTypeID = 0,
                    costModifier = -1
                });
                if (inst.valid)
                {
                    _links.Add(inst);
                    _linkEnds.Add(new KeyValuePair<Vector3, Vector3>(start, end));
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// Mirror trusted learned special edges (jump/wall-jump/fall/teleporter) as
        /// one-way NavMesh links. Ladders were already links; with these, a single
        /// CalculatePath produces complete multi-level routes and the route stitching
        /// between mesh and graph disappears for trusted traversal. Cheap no-op unless
        /// the trusted-edge set changed since the last sync.
        /// </summary>
        public static void SyncGraphLinks(bool force = false)
        {
            if (!_baked || NavGraph.Instance == null || !NavGraph.Instance.HasData) return;

            int signature = 17;
            foreach (var e in NavGraph.Instance.Edges)
            {
                if (!IsLinkableEdge(e)) continue;
                unchecked { signature = signature * 31 + e.From * 7 + e.To; }
            }
            if (!force && signature == _graphLinkSignature) return;
            _graphLinkSignature = signature;

            foreach (var link in _graphLinks)
                if (link.valid) link.Remove();
            _graphLinks.Clear();

            int added = 0;
            foreach (var e in NavGraph.Instance.Edges)
            {
                if (!IsLinkableEdge(e)) continue;
                var from = NavGraph.Instance.GetNodeById(e.From);
                var to = NavGraph.Instance.GetNodeById(e.To);
                if (from == null || to == null) continue;
                if ((from.Position - to.Position).sqrMagnitude > 900f) continue; // sanity: no 30m+ links

                if (!TrySampleAround(from.Position, 1.2f, out Vector3 start)) continue;
                if (!TrySampleAround(to.Position, 1.6f, out Vector3 end)) continue;

                var inst = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = start,
                    endPosition = end,
                    width = 0.5f,
                    bidirectional = false, // recorded traversals are directional
                    area = 0,
                    agentTypeID = 0,
                    costModifier = -1
                });
                if (inst.valid)
                {
                    _graphLinks.Add(inst);
                    added++;
                }
            }
            if (added > 0)
                Plugin.Log.LogInfo($"[NavMesh] Graph links: {added} trusted special edges mirrored as mesh links");
        }

        private static bool IsLinkableEdge(NavEdge e)
        {
            if (e == null || e.Confidence <= 0f) return false;
            if (e.Type != EdgeType.Jump && e.Type != EdgeType.WallJump
                && e.Type != EdgeType.Fall && e.Type != EdgeType.Teleporter) return false;
            return NavGraph.Instance.IsTrustedForPlay(e);
        }

        private static bool TrySampleAround(Vector3 pos, float radius, out Vector3 result)
        {
            Vector3[] offsets =
            {
                Vector3.zero,
                new Vector3(radius, 0, 0), new Vector3(-radius, 0, 0),
                new Vector3(0, 0, radius), new Vector3(0, 0, -radius)
            };
            foreach (var off in offsets)
            {
                if (NavMesh.SamplePosition(pos + off, out var hit, 1.5f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = default;
            return false;
        }

        /// <summary>
        /// Corner path between two world positions. Returns null when either end is off the
        /// mesh or no route exists. <paramref name="complete"/> is true only when the route
        /// really arrives AT the target — a path whose end got sampled to a floor below or
        /// beside the requested point reports as partial so learned jump/ladder routes can
        /// take over the last stretch.
        /// </summary>
        public static List<NavNode> FindCornerPath(Vector3 start, Vector3 end, out bool complete)
        {
            complete = false;
            if (!Ready) return null;
            if (!NavMesh.SamplePosition(start, out var startHit, 3f, NavMesh.AllAreas)) return null;
            if (!NavMesh.SamplePosition(end, out var endHit, 4f, NavMesh.AllAreas)) return null;

            if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, _queryPath))
                return null;
            if (_queryPath.status == NavMeshPathStatus.PathInvalid) return null;
            var corners = _queryPath.corners;
            if (corners == null || corners.Length < 2) return null;

            if (_queryPath.status == NavMeshPathStatus.PathComplete)
            {
                // "Complete" must mean the SAMPLED end is actually the requested target —
                // SamplePosition happily snaps to the floor 3m BELOW a ledge target, which
                // sent bots circling underneath points they should climb to.
                Vector3 delta = endHit.position - end;
                float horiz = new Vector2(delta.x, delta.z).magnitude;
                complete = Mathf.Abs(delta.y) <= 1.5f && horiz <= 2.0f;
            }

            // Smooth the corner chain: the grid-scanned mesh produces micro-zigzags around
            // cell edges that make bots visibly weave. Drop corners closer than 1m to the
            // previous kept one and merge near-collinear corners; keep first and last.
            var kept = new List<Vector3>(corners.Length) { corners[0] };
            for (int i = 1; i < corners.Length - 1; i++)
            {
                Vector3 prev = kept[kept.Count - 1];
                if ((corners[i] - prev).sqrMagnitude < 1.0f) continue;
                Vector3 inDir = corners[i] - prev;
                Vector3 outDir = corners[i + 1] - corners[i];
                inDir.y = 0f; outDir.y = 0f;
                if (inDir.sqrMagnitude > 0.01f && outDir.sqrMagnitude > 0.01f
                    && Vector3.Dot(inDir.normalized, outDir.normalized) > 0.99f
                    && Mathf.Abs(corners[i + 1].y - corners[i].y) < 0.4f)
                    continue;
                kept.Add(corners[i]);
            }
            kept.Add(corners[corners.Length - 1]);

            var path = new List<NavNode>(kept.Count);
            for (int i = 0; i < kept.Count; i++)
                path.Add(new NavNode(_nextSyntheticId--, SnapToGround(kept[i])));
            if (_nextSyntheticId < -1000000000) _nextSyntheticId = -10;
            return path;
        }

        // ==================== WALKED COVERAGE ====================

        private static long CellKey(int ix, int iy, int iz)
            => ((long)(ix + 4096) << 42) | ((long)(iy + 4096) << 21) | (long)(iz + 4096);

        private static void UnpackKey(long key, out int ix, out int iy, out int iz)
        {
            iz = (int)(key & 0x1FFFFF) - 4096;
            iy = (int)((key >> 21) & 0x1FFFFF) - 4096;
            ix = (int)((key >> 42) & 0x1FFFFF) - 4096;
        }

        /// <summary>Candidate cell keys in a box around a world position.</summary>
        private static IEnumerable<long> CellsNear(Vector3 pos, int xzRadius, int bandRadius)
        {
            int ix = Mathf.FloorToInt((pos.x - _gridOrigin.x) / _cellSize);
            int iz = Mathf.FloorToInt((pos.z - _gridOrigin.z) / _cellSize);
            int iy = Mathf.RoundToInt(pos.y / Y_BAND);
            for (int dx = -xzRadius; dx <= xzRadius; dx++)
                for (int dz = -xzRadius; dz <= xzRadius; dz++)
                    for (int dy = -bandRadius; dy <= bandRadius; dy++)
                        yield return CellKey(ix + dx, iy + dy, iz + dz);
        }

        /// <summary>Mark the ground under an actor as walked. Called from the position
        /// recorders every frame; cheap (a few hash lookups).</summary>
        public static void MarkWalked(Vector3 pos)
        {
            if (!_baked || _reachableCells.Count == 0) return;
            // Bake-time seeding failed (nobody had spawned yet)? This is a REAL grounded
            // actor position — re-flood from here so island exclusion works after all.
            if (_seedDegraded)
            {
                _seedDegraded = false;
                Plugin.Log.LogInfo("[NavMesh] Coverage: re-seeding reachability from first walked position");
                ComputeReachability(new List<Vector3> { pos });
            }
            // ~±1m of ground counts as walked regardless of cell resolution.
            int r = Mathf.Max(1, Mathf.RoundToInt(1.0f / _cellSize));
            foreach (long k in CellsNear(pos, r, 2))
            {
                if (_reachableCells.Contains(k))
                {
                    _walkedCells.Add(k);
                }
                else if (_cellSources.ContainsKey(k))
                {
                    // SELF-HEAL: someone is standing on ground a prune removed — it was
                    // reachable after all. Restore the cells; the mesh rebuilds shortly.
                    _reachableCells.Add(k);
                    _reachableList.Add(k);
                    _walkedCells.Add(k);
                    _healedCellCount++;
                }
            }
            if (_healedCellCount > 0 && Time.unscaledTime >= _nextHealRebuildTime)
            {
                _nextHealRebuildTime = Time.unscaledTime + 15f; // batch heals, ~100ms hitch
                Plugin.Log.LogInfo($"[NavMesh] Self-heal: restored {_healedCellCount} pruned cells that got walked — rebuilding mesh");
                _healedCellCount = 0;
                RebuildMeshFromSurvivingCells();
            }
        }

        private static int _healedCellCount;
        private static float _nextHealRebuildTime;

        /// <summary>Walked-cell world positions for NavData persistence (v6).</summary>
        public static List<Vector3> GetWalkedCellPositionsForSave()
        {
            var list = new List<Vector3>(_walkedCells.Count);
            foreach (long k in _walkedCells)
                list.Add(CellToWorld(k));
            return list;
        }

        /// <summary>Re-apply saved walked coverage after a fresh bake. Exact-cell match
        /// with ±1 y-band tolerance and a single-neighbor fallback — deliberately adds
        /// at most ONE cell per saved position so coverage can't inflate across
        /// save/load cycles.</summary>
        public static void RestoreWalked(List<Vector3> positions)
        {
            if (!_baked || positions == null || positions.Count == 0) return;
            int restored = 0;
            foreach (var p in positions)
            {
                int ix = Mathf.FloorToInt((p.x - _gridOrigin.x) / _cellSize);
                int iz = Mathf.FloorToInt((p.z - _gridOrigin.z) / _cellSize);
                int iy = Mathf.RoundToInt(p.y / Y_BAND);
                bool added = false;
                for (int dy = -1; dy <= 1 && !added; dy++)
                {
                    long k = CellKey(ix, iy + dy, iz);
                    if (_reachableCells.Contains(k))
                    {
                        if (_walkedCells.Add(k)) restored++;
                        added = true;
                    }
                }
                if (!added)
                {
                    // Grid shifted slightly between sessions — take the nearest existing
                    // neighbor, but only one.
                    foreach (long k in CellsNear(p, 1, 1))
                    {
                        if (_reachableCells.Contains(k))
                        {
                            if (_walkedCells.Add(k)) restored++;
                            break;
                        }
                    }
                }
            }
            if (restored > 0)
                Plugin.Log.LogInfo($"[NavMesh] Coverage restored from save: {restored} walked cells "
                    + $"({_walkedCells.Count}/{_reachableCells.Count})");
        }

        /// <summary>Grid flood fill from seed positions over the baked cells: 8-connected
        /// in XZ with a ±2 height-band tolerance (handles slopes/steps), plus ladder-link
        /// bridges between floors. Cells the flood never reaches are bake islands.</summary>
        private static void ComputeReachability(List<Vector3> seeds)
        {
            _reachableCells.Clear();
            _walkedCells.Clear();
            if (_allCells.Count == 0) return;

            var queue = new Queue<long>();
            foreach (var pos in seeds)
                foreach (long k in CellsNear(pos, 4, 4)) // actors excluded nearby scan hits at bake — search a bit wide
                    if (_allCells.Contains(k) && _reachableCells.Add(k))
                        queue.Enqueue(k);

            if (queue.Count == 0)
            {
                // No seed landed on the mesh — degrade gracefully: count everything so
                // coverage still fills rather than sitting at 0/0. The first grounded
                // actor position that comes through MarkWalked re-floods properly.
                foreach (long k in _allCells) _reachableCells.Add(k);
                _reachableList.Clear();
                _reachableList.AddRange(_reachableCells);
                _seedDegraded = true;
                Plugin.Log.LogWarning($"[NavMesh] Coverage: no spawn seed found on mesh — counting all {_allCells.Count} cells as reachable");
                return;
            }
            _seedDegraded = false;

            // Ladder links bridge floors the grid adjacency can't cross.
            var bridges = BuildLadderBridges(_allCells);

            while (queue.Count > 0)
            {
                long k = queue.Dequeue();
                UnpackKey(k, out int ix, out int iy, out int iz);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            long nk = CellKey(ix + dx, iy + dy, iz + dz);
                            if (_allCells.Contains(nk) && _reachableCells.Add(nk))
                                queue.Enqueue(nk);
                        }
                    }
                if (bridges.TryGetValue(k, out var far))
                    foreach (long fk in far)
                        if (_reachableCells.Add(fk))
                            queue.Enqueue(fk);
            }

            _reachableList.Clear();
            _reachableList.AddRange(_reachableCells);
            Plugin.Log.LogInfo($"[NavMesh] Coverage: {_reachableCells.Count}/{_allCells.Count} cells reachable from spawn "
                + $"({_allCells.Count - _reachableCells.Count} out-of-bounds island cells excluded)");
        }

        /// <summary>
        /// Manual stage-1 cleanup: drop EVERY still-unwalked scan cell. What has been
        /// walked defines the playable area — everything else (out-of-bounds terrain,
        /// roof tops, unreachable ledges the flood fill or a route check would keep)
        /// stops counting against coverage and stops being targeted by stage-1 explore.
        /// A NavMesh-route check was tried first and still kept out-of-bounds ground
        /// that happened to be mesh-connected; unwalked-means-gone is unambiguous.
        /// </summary>
        public static int PruneAllUnwalked()
        {
            if (!_baked || _reachableCells.Count == 0) return 0;

            // Flood from everything the map is KNOWN to reach: walked ground, learned
            // graph nodes (jump/ladder landings included — so areas connected by jumps
            // survive even if unwalked), ladder bridges, and spawn points. Only cells in
            // components nobody can reach by any known means die — the orphaned islands
            // whose cyan wireframe floats over out-of-bounds areas.
            var connected = new HashSet<long>();
            var queue = new Queue<long>();

            foreach (long k in _walkedCells)
                if (_reachableCells.Contains(k) && connected.Add(k)) queue.Enqueue(k);

            var seedPositions = new List<Vector3>();
            try
            {
                if (NavGraph.Instance != null)
                    foreach (var node in NavGraph.Instance.Nodes)
                        if (node != null && node.Confidence > 0f)
                            seedPositions.Add(node.Position);
                foreach (var sp in Object.FindObjectsOfType<SpawnPoint>())
                    if (sp != null) seedPositions.Add(sp.transform.position);
            }
            catch { }
            foreach (var pos in seedPositions)
                foreach (long k in CellsNear(pos, 3, 4))
                    if (_reachableCells.Contains(k) && connected.Add(k)) queue.Enqueue(k);

            if (connected.Count == 0) return 0;

            var bridges = BuildLadderBridges(_reachableCells);
            while (queue.Count > 0)
            {
                long k = queue.Dequeue();
                UnpackKey(k, out int ix, out int iy, out int iz);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            long nk = CellKey(ix + dx, iy + dy, iz + dz);
                            if (_reachableCells.Contains(nk) && connected.Add(nk))
                                queue.Enqueue(nk);
                        }
                    }
                if (bridges.TryGetValue(k, out var far))
                    foreach (long fk in far)
                        if (_reachableCells.Contains(fk) && connected.Add(fk))
                            queue.Enqueue(fk);
            }

            int before = _reachableCells.Count;
            _reachableCells.RemoveWhere(k => !connected.Contains(k));
            _reachableList.Clear();
            _reachableList.AddRange(_reachableCells);
            int dropped = before - _reachableCells.Count;
            if (dropped > 0)
            {
                Plugin.Log.LogInfo($"[NavMesh] Coverage cleanup: dropped {dropped} unreachable island cells — "
                    + $"coverage now {_walkedCells.Count}/{_reachableCells.Count}");
                RebuildMeshFromSurvivingCells();
            }
            return dropped;
        }

        /// <summary>
        /// Rebuild the actual Unity NavMesh from the surviving (walked) cells + carve
        /// volumes. Without this, pruning only fixed the coverage numbers — the mesh
        /// itself (routing + cyan wireframe) still covered out-of-bounds areas.
        /// </summary>
        private static void RebuildMeshFromSurvivingCells()
        {
            if (_cellSources.Count == 0) return;
            try
            {
                var sources = new List<NavMeshBuildSource>(_reachableCells.Count + _carveSources.Count);
                foreach (long k in _reachableCells)
                    if (_cellSources.TryGetValue(k, out var src))
                        sources.Add(src);
                sources.AddRange(_carveSources);

                var data = NavMeshBuilder.BuildNavMeshData(_bakeSettings, sources, _bakeBounds,
                    Vector3.zero, Quaternion.identity);
                if (data == null)
                {
                    Plugin.Log.LogWarning("[NavMesh] Rebuild after prune failed — keeping old mesh");
                    return;
                }

                if (_instance.valid) _instance.Remove();
                _instance = NavMesh.AddNavMeshData(data);

                var tri = NavMesh.CalculateTriangulation();
                TriVertices = tri.vertices;
                TriIndices = tri.indices;
                int triCount = TriIndices != null ? TriIndices.Length / 3 : 0;
                Plugin.Log.LogInfo($"[NavMesh] Rebuilt from walked ground: {sources.Count - _carveSources.Count} cells, {triCount} tris");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[NavMesh] Rebuild after prune error: {e.Message}");
            }
        }

        private static Vector3 CellToWorld(long key)
        {
            UnpackKey(key, out int ix, out int iy, out int iz);
            return new Vector3(
                _gridOrigin.x + (ix + 0.5f) * _cellSize,
                iy * Y_BAND,
                _gridOrigin.z + (iz + 0.5f) * _cellSize);
        }

        /// <summary>Ladder-link cell bridges over a given cell universe — lets floods
        /// cross between floors the grid adjacency can't connect.</summary>
        private static Dictionary<long, List<long>> BuildLadderBridges(HashSet<long> universe)
        {
            var bridges = new Dictionary<long, List<long>>();
            foreach (var le in _linkEnds)
            {
                var aCells = new List<long>();
                var bCells = new List<long>();
                foreach (long k in CellsNear(le.Key, 2, 3)) if (universe.Contains(k)) aCells.Add(k);
                foreach (long k in CellsNear(le.Value, 2, 3)) if (universe.Contains(k)) bCells.Add(k);
                foreach (long a in aCells)
                {
                    if (!bridges.TryGetValue(a, out var list)) bridges[a] = list = new List<long>();
                    list.AddRange(bCells);
                }
                foreach (long b in bCells)
                {
                    if (!bridges.TryGetValue(b, out var list)) bridges[b] = list = new List<long>();
                    list.AddRange(aCells);
                }
            }
            return bridges;
        }

        /// <summary>
        /// Random-sampled UNWALKED reachable cell for stage-1 explore targeting. Returns
        /// the nearest of the sampled batch (aggressive lawnmower-style coverage) while
        /// skipping ground underfoot and anything the caller rejects (recently visited).
        /// A walked cell can never be returned again, so bots physically cannot loop
        /// over ground they already covered. False when coverage is (near-)complete.
        /// </summary>
        public static bool TryGetUnwalkedCellTarget(Vector3 nearPos, System.Func<Vector3, bool> reject, out Vector3 target)
        {
            target = Vector3.zero;
            if (!_baked || _reachableList.Count == 0) return false;
            if (_walkedCells.Count >= _reachableCells.Count) return false;

            Vector3 best = Vector3.zero;
            float bestDistSqr = float.MaxValue;
            int found = 0;
            int attempts = Mathf.Min(150, _reachableList.Count);
            for (int i = 0; i < attempts && found < 16; i++)
            {
                long k = _reachableList[Random.Range(0, _reachableList.Count)];
                if (_walkedCells.Contains(k)) continue;
                Vector3 pos = CellToWorld(k);
                Vector3 d = pos - nearPos;
                float distSqr = d.x * d.x + d.z * d.z;
                if (distSqr < 16f) continue; // underfoot — walking already covers it
                if (reject != null && reject(pos)) continue;
                found++;
                if (distSqr < bestDistSqr) { bestDistSqr = distSqr; best = pos; }
            }
            if (found == 0) return false;
            target = best;
            return true;
        }

        /// <summary>Scan surfaces sit slightly above the real floor — snap waypoints down so
        /// drawn paths hug the ground and reach checks measure true distance.</summary>
        private static Vector3 SnapToGround(Vector3 pos)
        {
            if (Physics.Raycast(pos + Vector3.up * 0.6f, Vector3.down, out var hit, 2.2f,
                    SCAN_MASK, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.05f;
            return pos + Vector3.up * 0.05f;
        }
    }
}
