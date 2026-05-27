using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// Records player movement data into NavGraph. Runs on host only.
    /// Tracks all connected players' grounded positions, jumps, and ladder transitions.
    /// Called from BotPatches via Harmony hooks on FirstPersonController.
    /// </summary>
    public static class PlayerRecorder
    {
        private const float SAMPLE_INTERVAL = 0.25f;
        private const float SAMPLE_INTERVAL_SLOPE = 0.12f;
        private const float SAMPLE_INTERVAL_TRAINING = 0.15f;
        private const float MIN_MOVE_DIST = 0.25f;
        private const float MAX_MOVE_DIST = 1.5f;
        private const float SURROUND_SCAN_INTERVAL = 0.3f;     // Scan surroundings every 0.3s — fast enough for sprinting
        private const float SURROUND_SCAN_RADIUS = 6f;        // How far to scan
        private const int SURROUND_SCAN_DIRS = 8;             // 8 directions
        private const float TRAJ_INTERVAL = 0.05f;            // Record position every 50ms during air phase
        private const int TRAJ_MAX_SAMPLES = 60;              // Max 3 seconds of air time (60 * 0.05)

        // Per-player tracking state
        private class PlayerTrack
        {
            public Vector3 LastRecordedPos;
            public Vector3 LastGroundedPos;     // Last position while grounded (for jump landing)
            public float LastSampleTime;
            public bool WasGrounded;
            public bool WasOnLadder;
            public bool WasSliding;
            public Vector3 LadderEntryPos;
            public Vector3 JumpTakeoffPos;
            public Vector3 SlideEntryPos;
            public bool IsJumping;              // Between takeoff and landing
            public int LastWallJumpCount;       // Track FPC wallJumpsCount for wall jump detection
            public Vector3 WallJumpTakeoffPos;  // Position when wall jump started
            public int LastNodeId = -1;
            public float LastNodeCheckTime;
            public float LastSurroundScan;    // Timer for surrounding ground scan
            public float SlideEndTime;        // Time.time when slide ended — for slide-jump detection
            public Vector3 SlideStartPos;     // Position when slide started
            public float JumpStartTime;       // Time.time at takeoff — for trajectory sampling
            public Vector3 JumpTakeoffDir;    // Horizontal direction at takeoff
            public float JumpTakeoffSpeed;    // Horizontal speed at takeoff
            public Vector3[] JumpAirPositions;  // Full trajectory: positions sampled every TRAJ_INTERVAL
            public float[] JumpAirTimestamps;   // Time offsets from takeoff for each sample
            public int JumpAirSampleCount;
            public float JumpLastSampleTime;    // Time.time of last sample — for fixed interval
            public bool WasVaulting;          // FPC vault state last frame
            public Vector3 VaultStartPos;     // Position when vault started
        }

        private static Dictionary<int, PlayerTrack> _tracks = new Dictionary<int, PlayerTrack>();
        private static bool _enabled;
        private static System.Reflection.FieldInfo _ladderFieldCache;
        private static bool _ladderFieldCached;
        private static readonly Vector3[] _scanDirs;
        private static readonly Vector3[] _scanPerps;
        private const int GROUND_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 14);
        private const int WALL_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);

        static PlayerRecorder()
        {
            // Pre-compute scan directions once
            _scanDirs = new Vector3[SURROUND_SCAN_DIRS];
            _scanPerps = new Vector3[SURROUND_SCAN_DIRS];
            float angleStep = 360f / SURROUND_SCAN_DIRS;
            for (int i = 0; i < SURROUND_SCAN_DIRS; i++)
            {
                float angle = i * angleStep * Mathf.Deg2Rad;
                _scanDirs[i] = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                _scanPerps[i] = new Vector3(Mathf.Cos(angle), 0, -Mathf.Sin(angle));
            }
        }

        // Recent player trail — last N node IDs the player walked through, for bots to follow
        private static List<int> _playerTrail = new List<int>();
        private const int MAX_TRAIL = 50;

        /// <summary>
        /// Get the player's recent trail of node IDs (oldest first).
        /// Bots use this to follow the player's exact path.
        /// </summary>
        public static List<int> PlayerTrail => _playerTrail;

        // --- Watch Me recording ---
        // A player-driven demo. Every node added while active is boosted to Confidence=1.0
        // and appended to an ordered sequence; on stop, it's saved as a ProvenRoute.
        private static bool _watchMeActive;
        private static List<int> _watchMeIds = new List<int>();
        private static string _watchMeName;
        private static float _watchMeStartTime;

        public static bool WatchMeActive => _watchMeActive;
        public static int WatchMeSampleCount => _watchMeIds.Count;
        public static string WatchMeName => _watchMeName;

        /// <summary>Begin a Watch Me recording. Player walks the route; bots will prefer it afterward.</summary>
        public static void StartWatchMe(string name = null)
        {
            _watchMeActive = true;
            _watchMeIds.Clear();
            _watchMeName = string.IsNullOrEmpty(name)
                ? $"Route_{System.DateTime.Now:yyyyMMdd_HHmmss}"
                : name;
            _watchMeStartTime = Time.time;
            Plugin.Log.LogInfo($"[Recorder] Watch Me START ({_watchMeName})");
        }

        /// <summary>Stop the active Watch Me recording and commit it as a ProvenRoute.
        /// Returns true if a route was saved.</summary>
        public static bool StopWatchMe()
        {
            if (!_watchMeActive) return false;
            _watchMeActive = false;
            float duration = Mathf.Max(0f, Time.time - _watchMeStartTime);
            int count = _watchMeIds.Count;
            if (NavGraph.Instance != null && count >= 2)
            {
                NavGraph.Instance.AddProvenRoute(_watchMeName, _watchMeIds, duration);
                Plugin.Log.LogInfo($"[Recorder] Watch Me STOP — saved {count} nodes as '{_watchMeName}'");
                _watchMeIds.Clear();
                return true;
            }
            Plugin.Log.LogWarning($"[Recorder] Watch Me STOP — too short ({count} nodes), discarded");
            _watchMeIds.Clear();
            return false;
        }

        /// <summary>Cancel without saving.</summary>
        public static void CancelWatchMe()
        {
            if (!_watchMeActive) return;
            _watchMeActive = false;
            Plugin.Log.LogInfo($"[Recorder] Watch Me CANCELLED ({_watchMeIds.Count} nodes discarded)");
            _watchMeIds.Clear();
        }

        /// <summary>Called from inside RecordPlayer whenever a fresh node ID enters the player trail.
        /// Boosts the node to max confidence and appends it to the current demo.</summary>
        private static void WatchMeOnNode(NavNode node)
        {
            if (!_watchMeActive || node == null) return;
            // Lock the node in as fully trusted player data.
            node.Confidence = 1f;
            node.PlayerSourced = true;
            if (_watchMeIds.Count == 0 || _watchMeIds[_watchMeIds.Count - 1] != node.Id)
                _watchMeIds.Add(node.Id);
        }

        public static void Enable()
        {
            _enabled = true;
            _tracks.Clear();
            _playerTrail.Clear();
        }

        public static void Disable()
        {
            _enabled = false;
            _tracks.Clear();
        }

        /// <summary>
        /// Called every frame from a Harmony postfix on FirstPersonController.Update (host only).
        /// Records player position data into the NavGraph.
        /// </summary>
        public static void RecordPlayer(FirstPersonController fpc)
        {
            if (!_enabled || NavGraph.Instance == null || fpc == null) return;
            if (!FishNet.InstanceFinder.IsServer) return;

            // Feed coverage heatmap from player movement too — player paths compound with bot ones.
            if (fpc.isGrounded) NavGraph.Instance.TouchCoverage(fpc.transform.position);

            // Get unique ID for this player
            int id = fpc.GetInstanceID();

            if (!_tracks.TryGetValue(id, out var track))
            {
                track = new PlayerTrack();
                track.LastRecordedPos = fpc.transform.position;
                track.LastGroundedPos = fpc.transform.position;
                track.LastSampleTime = Time.time;
                track.WasGrounded = true;
                _tracks[id] = track;
            }

            Vector3 pos = fpc.transform.position;
            bool grounded = fpc.isGrounded;
            bool onLadder = false;
            bool isSliding = fpc.isSliding; // Public field on FPC

            // Check ladder state via cached reflection
            if (!_ladderFieldCached)
            {
                _ladderFieldCached = true;
                _ladderFieldCache = typeof(FirstPersonController).GetField("onLadder",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
            if (_ladderFieldCache != null)
            {
                try { onLadder = (bool)_ladderFieldCache.GetValue(fpc); } catch { }
            }

            // --- Position sampling (grounded only) ---
            if (grounded)
            {
                float dist = Vector3.Distance(pos, track.LastRecordedPos);
                float heightChange = Mathf.Abs(pos.y - track.LastRecordedPos.y);
                bool onSlope = heightChange > 0.1f && dist > 0.3f;
                bool training = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
                float densityMul = Plugin.GetPlayerDensityMultiplier();
                float interval = (training ? SAMPLE_INTERVAL_TRAINING :
                    (onSlope ? SAMPLE_INTERVAL_SLOPE : SAMPLE_INTERVAL)) * densityMul;
                bool timeReady = Time.time - track.LastSampleTime >= interval;
                bool distForce = dist >= MAX_MOVE_DIST * densityMul;

                if ((timeReady || distForce) && dist >= MIN_MOVE_DIST * densityMul)
                {
                    var node = NavGraph.Instance.AddPosition(pos, isPlayer: true);
                    track.LastRecordedPos = pos;
                    track.LastSampleTime = Time.time;

                    // Track node traversal — report success when player moves between nodes
                    // This rehabilitates bad nodes that players prove are walkable
                    if (node != null && node.Id != track.LastNodeId)
                    {
                        if (track.LastNodeId >= 0)
                        {
                            // Ensure player nodes are ALWAYS connected — create edge if missing
                            NavGraph.Instance.EnsurePlayerEdge(track.LastNodeId, node.Id);
                            NavGraph.Instance.ReportSuccess(track.LastNodeId, node.Id, isPlayer: true);

                            if (Time.time - track.LastNodeCheckTime > 5f)
                            {
                                NavGraph.Instance.CompressNearby(pos);
                                track.LastNodeCheckTime = Time.time;
                            }
                        }
                        track.LastNodeId = node.Id;

                        // Add to player trail for bots to follow
                        _playerTrail.Add(node.Id);
                        if (_playerTrail.Count > MAX_TRAIL)
                            _playerTrail.RemoveAt(0);

                        // Watch Me demo in progress — capture this node into the named route
                        WatchMeOnNode(node);
                    }
                }
            }

            // --- Jump / fall detection ---
            if (track.WasGrounded && !grounded && !onLadder && fpc.moveDirection.y > 2f)
            {
                track.JumpTakeoffPos = track.LastGroundedPos;
                track.IsJumping = true;
                track.JumpStartTime = Time.time;
                track.JumpLastSampleTime = 0f;
                Vector3 hDir = new Vector3(fpc.moveDirection.x, 0, fpc.moveDirection.z);
                track.JumpTakeoffDir = hDir.sqrMagnitude > 0.01f ? hDir.normalized : fpc.transform.forward;
                track.JumpTakeoffSpeed = new Vector3(fpc.moveDirection.x, 0, fpc.moveDirection.z).magnitude;
                track.JumpAirPositions = new Vector3[TRAJ_MAX_SAMPLES];
                track.JumpAirTimestamps = new float[TRAJ_MAX_SAMPLES];
                track.JumpAirSampleCount = 0;
            }

            if (track.WasGrounded && !grounded && !onLadder && fpc.moveDirection.y <= 2f && !track.IsJumping)
            {
                track.JumpTakeoffPos = track.LastGroundedPos;
                track.IsJumping = true;
                track.JumpStartTime = Time.time;
                track.JumpLastSampleTime = 0f;
                track.JumpTakeoffDir = fpc.transform.forward;
                track.JumpTakeoffSpeed = new Vector3(fpc.moveDirection.x, 0, fpc.moveDirection.z).magnitude;
                track.JumpAirPositions = new Vector3[TRAJ_MAX_SAMPLES];
                track.JumpAirTimestamps = new float[TRAJ_MAX_SAMPLES];
                track.JumpAirSampleCount = 0;
            }

            // Sample positions during air phase
            if (!grounded) SampleAirPosition(track, pos);

            if (track.IsJumping && grounded)
            {
                float jumpDist = Vector3.Distance(track.JumpTakeoffPos, pos);
                float heightDiff = pos.y - track.JumpTakeoffPos.y;

                if (jumpDist > 1.0f || Mathf.Abs(heightDiff) > 0.25f)
                {
                    // Reject absurdly long "jumps" — likely teleports/glitches, not replicable by bots
                    bool absurdLong = jumpDist > 20f || heightDiff > 10f;

                    // Classify the traversal: walkable slope vs jump vs fall.
                    bool groundTrace = NavGraph.Instance.ValidateEdgeGroundPublic(track.JumpTakeoffPos, pos)
                        && NavGraph.Instance.ValidateLineOfSightPublic(track.JumpTakeoffPos, pos);
                    Vector3 horizTrk = pos - track.JumpTakeoffPos; horizTrk.y = 0f;
                    float runTrk = horizTrk.magnitude;
                    float slopeRatio = runTrk > 0.01f ? Mathf.Abs(heightDiff) / runTrk : 99f;

                    // Walkable-slope test is DIRECTION-AWARE:
                    //   * Downhill (heightDiff <= 0.1): if ground traces, it's walkable — gravity handles
                    //     steep descents, so no Fall/Jump edge needed. This fixes the bug where walking
                    //     down a steep ramp produced bogus Fall edges.
                    //   * Uphill: must be shallow ratio AND modest rise. Above that it's a real jump.
                    bool walkableSlope;
                    if (heightDiff <= 0.1f)
                        walkableSlope = groundTrace;
                    else
                        walkableSlope = groundTrace && slopeRatio < 0.84f && heightDiff < 0.8f;

                    if (absurdLong)
                    {
                        // Skip — don't pollute graph with unreachable edges
                    }
                    else if (walkableSlope)
                    {
                        // Walkable slope — just ensure walk nodes exist, no special edge needed
                        NavGraph.Instance.AddPosition(track.JumpTakeoffPos, isPlayer: true);
                        NavGraph.Instance.AddPosition(pos, isPlayer: true);
                    }
                    else if (heightDiff < -1f && !groundTrace)
                    {
                        // Real drop — ground doesn't trace AND we fell > 1m. Create Fall edge.
                        NavGraph.Instance.AddSpecialEdge(track.JumpTakeoffPos, pos, EdgeType.Fall, isPlayer: true);
                        NavGraph.Instance.AddPosition(pos, isPlayer: true);
                    }
                    else
                    {
                        // Not walkable, not a fall — check whether this is a NECESSARY jump.
                        // Two conditions qualify:
                        //   (a) hasGap: midpoint raycast finds no ground (real gap to clear)
                        //   (b) !groundTrace: the ground doesn't walk continuously between takeoff
                        //       and landing (step-up onto a curb/crate — no gap below, but ground
                        //       isn't connected). Previously dropped because hasGap alone was false.
                        // Both conditions together filter out "hopping on flat ground" (hasGap=false,
                        // groundTrace=true) which shouldn't create a Jump edge.
                        bool hasGap = false;
                        Vector3 mid = (track.JumpTakeoffPos + pos) * 0.5f + Vector3.up * 0.5f;
                        if (!Physics.Raycast(mid, Vector3.down, 3f))
                            hasGap = true;
                        if (!hasGap)
                        {
                            for (float t = 0.25f; t <= 0.75f; t += 0.5f)
                            {
                                Vector3 checkPt = Vector3.Lerp(track.JumpTakeoffPos, pos, t) + Vector3.up * 0.5f;
                                if (!Physics.Raycast(checkPt, Vector3.down, 3f))
                                { hasGap = true; break; }
                            }
                        }

                        bool necessaryJump = hasGap || !groundTrace;
                        if (necessaryJump)
                        {
                            var jumpEdge = NavGraph.Instance.AddSpecialEdge(track.JumpTakeoffPos, pos, EdgeType.Jump, isPlayer: true);
                            NavGraph.Instance.AddPosition(track.JumpTakeoffPos, isPlayer: true);
                            NavGraph.Instance.AddPosition(pos, isPlayer: true);

                            if (jumpEdge != null && track.JumpAirSampleCount > 0)
                            {
                                jumpEdge.TakeoffDir = track.JumpTakeoffDir;
                                jumpEdge.TakeoffSpeed = track.JumpTakeoffSpeed;
                                jumpEdge.AirPositions = new Vector3[track.JumpAirSampleCount];
                                jumpEdge.AirTimestamps = new float[track.JumpAirSampleCount];
                                System.Array.Copy(track.JumpAirPositions, jumpEdge.AirPositions, track.JumpAirSampleCount);
                                System.Array.Copy(track.JumpAirTimestamps, jumpEdge.AirTimestamps, track.JumpAirSampleCount);
                                jumpEdge.AirSampleCount = track.JumpAirSampleCount;
                                jumpEdge.LockedAirTime = track.JumpAirTimestamps[track.JumpAirSampleCount - 1];
                            }
                        }
                    }
                }
                track.IsJumping = false;
            }

            // --- Wall jump detection ---
            int curWallJumps = fpc.wallJumpsCount;
            if (curWallJumps > track.LastWallJumpCount && !grounded)
            {
                // Player just wall jumped — record takeoff position
                track.WallJumpTakeoffPos = pos;
            }
            // Wall jump landed — create WallJump edge
            if (track.WallJumpTakeoffPos.sqrMagnitude > 0.01f && grounded)
            {
                float wjDist = Vector3.Distance(track.WallJumpTakeoffPos, pos);
                if (wjDist > 1.5f && NavGraph.Instance != null)
                {
                    NavGraph.Instance.AddSpecialEdge(track.WallJumpTakeoffPos, pos, EdgeType.WallJump, isPlayer: true);
                }
                track.WallJumpTakeoffPos = Vector3.zero;
            }
            track.LastWallJumpCount = curWallJumps;

            // --- Shared transition detection ---
            ProcessLadder(track, pos, onLadder, isPlayer: true);
            if (grounded) ScanSurroundings(pos, track, isPlayer: true);
            ProcessSlide(track, pos, isSliding, isPlayer: true);
            ProcessSlideJump(track, pos, grounded, isPlayer: true);

            // Vault detection — FPC has a public 'vault' bool
            bool isVaulting = false;
            try { isVaulting = fpc.vault; } catch { }
            if (isVaulting && !track.WasVaulting)
                track.VaultStartPos = pos;
            if (!isVaulting && track.WasVaulting && grounded)
            {
                float vaultDist = Vector3.Distance(track.VaultStartPos, pos);
                if (vaultDist > 1f && NavGraph.Instance != null)
                {
                    var edge = NavGraph.Instance.AddSpecialEdge(track.VaultStartPos, pos, EdgeType.Jump, isPlayer: true);
                    if (edge != null) { edge.Confidence = 1f; edge.SuccessCount = Mathf.Max(edge.SuccessCount, 10); }
                    // Create landing node so bots know to stop here
                    var landNode = NavGraph.Instance.AddPosition(pos, isPlayer: true);
                    if (landNode != null) { landNode.Confidence = 1f; landNode.VisitCount = Mathf.Max(landNode.VisitCount, 10); }
                }
            }
            track.WasVaulting = isVaulting;

            UpdateTrackState(track, pos, grounded, onLadder, isSliding);
        }

        /// <summary>
        /// Record bot positions too — they also discover the map.
        /// Called from BotController.Update.
        /// </summary>
        public static void RecordBot(Vector3 pos, bool grounded, bool onLadder, int botId,
            bool jumped, Vector3 lastGroundedPos, bool isSliding = false)
        {
            if (!_enabled || NavGraph.Instance == null) return;

            // Feed the coverage heatmap every bot sample. Cheap — one dict lookup.
            // Powers GetLowestVisitReachableCell so Explore can push into under-visited areas.
            if (grounded) NavGraph.Instance.TouchCoverage(pos);

            if (!_tracks.TryGetValue(botId, out var track))
            {
                track = new PlayerTrack();
                track.LastRecordedPos = pos;
                track.LastGroundedPos = pos;
                track.LastSampleTime = Time.time;
                track.WasGrounded = true;
                _tracks[botId] = track;
            }

            // Position sampling — training mode uses aggressive intervals
            if (grounded)
            {
                float dist = Vector3.Distance(pos, track.LastRecordedPos);
                float heightChange = Mathf.Abs(pos.y - track.LastRecordedPos.y);
                bool onSlope = heightChange > 0.1f && dist > 0.3f;
                bool training = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
                float densityMul = Plugin.GetBotDensityMultiplier();
                float interval = (training ? SAMPLE_INTERVAL_TRAINING :
                    (onSlope ? SAMPLE_INTERVAL_SLOPE : SAMPLE_INTERVAL)) * densityMul;
                bool timeReady = Time.time - track.LastSampleTime >= interval;
                bool distForce = dist >= MAX_MOVE_DIST * densityMul;

                if ((timeReady || distForce) && dist >= MIN_MOVE_DIST * densityMul)
                {
                    var node = NavGraph.Instance.AddPosition(pos, isPlayer: false);
                    track.LastRecordedPos = pos;
                    track.LastSampleTime = Time.time;

                    // Ensure bots build a CONNECTED graph — create walk edge between
                    // consecutive bot nodes. Previously bots dropped isolated nodes that
                    // A* couldn't path through, so nodeless maps stayed un-pathable.
                    if (node != null && node.Id != track.LastNodeId)
                    {
                        if (track.LastNodeId >= 0)
                            NavGraph.Instance.EnsurePlayerEdge(track.LastNodeId, node.Id);
                        track.LastNodeId = node.Id;
                    }
                }
            }

            // Jump/fall landing — only create Jump edge if the path actually requires jumping
            // (gap, no ground at midpoint, or significant height requiring jump)
            if (track.IsJumping && grounded)
            {
                float jumpDist = Vector3.Distance(track.JumpTakeoffPos, pos);
                float heightDiff = pos.y - track.JumpTakeoffPos.y;
                if (jumpDist > 1.0f || Mathf.Abs(heightDiff) > 0.25f)
                {
                    // Reject absurd jump distances — teleports/glitches, not replicable
                    bool absurdLongBot = jumpDist > 20f || heightDiff > 10f;

                    // Direction-aware walkable-slope test (matches player path).
                    bool groundTrace = NavGraph.Instance.ValidateEdgeGroundPublic(track.JumpTakeoffPos, pos)
                        && NavGraph.Instance.ValidateLineOfSightPublic(track.JumpTakeoffPos, pos);
                    Vector3 horizTrk = pos - track.JumpTakeoffPos; horizTrk.y = 0f;
                    float runTrk = horizTrk.magnitude;
                    float slopeRatio = runTrk > 0.01f ? Mathf.Abs(heightDiff) / runTrk : 99f;
                    bool walkableSlope;
                    if (heightDiff <= 0.1f)
                        walkableSlope = groundTrace;      // any downhill with continuous ground is walkable
                    else
                        walkableSlope = groundTrace && slopeRatio < 0.84f && heightDiff < 0.8f;

                    if (absurdLongBot)
                    {
                        // Skip absurd jumps
                    }
                    else if (walkableSlope)
                    {
                        NavGraph.Instance.AddPosition(track.JumpTakeoffPos, isPlayer: false);
                        NavGraph.Instance.AddPosition(pos, isPlayer: false);
                    }
                    else if (heightDiff < -1.5f && !groundTrace)
                    {
                        // Real drop
                        NavGraph.Instance.AddSpecialEdge(track.JumpTakeoffPos, pos, EdgeType.Fall, isPlayer: false);
                        NavGraph.Instance.AddPosition(pos, isPlayer: false);
                    }
                    else
                    {
                        // Necessary-jump check: hasGap OR ground doesn't trace (step-up case).
                        bool hasGap = false;
                        for (float t = 0.25f; t <= 0.75f; t += 0.25f)
                        {
                            Vector3 checkPt = Vector3.Lerp(track.JumpTakeoffPos, pos, t) + Vector3.up * 0.5f;
                            if (!Physics.Raycast(checkPt, Vector3.down, 3f))
                            { hasGap = true; break; }
                        }

                        if (hasGap || !groundTrace)
                        {
                            var botJumpEdge = NavGraph.Instance.AddSpecialEdge(track.JumpTakeoffPos, pos, EdgeType.Jump, isPlayer: false);
                            NavGraph.Instance.AddPosition(pos, isPlayer: false);

                            if (botJumpEdge != null && track.JumpAirSampleCount > 0)
                            {
                                botJumpEdge.AirPositions = new Vector3[track.JumpAirSampleCount];
                                botJumpEdge.AirTimestamps = new float[track.JumpAirSampleCount];
                                System.Array.Copy(track.JumpAirPositions, botJumpEdge.AirPositions, track.JumpAirSampleCount);
                                System.Array.Copy(track.JumpAirTimestamps, botJumpEdge.AirTimestamps, track.JumpAirSampleCount);
                                botJumpEdge.AirSampleCount = track.JumpAirSampleCount;
                                botJumpEdge.LockedAirTime = track.JumpAirTimestamps[track.JumpAirSampleCount - 1];
                            }
                        }
                    }
                }
                track.IsJumping = false;
            }

            // Jump takeoff (explicit jump)
            if (jumped && track.WasGrounded)
            {
                track.JumpTakeoffPos = track.LastGroundedPos;
                track.IsJumping = true;
                track.JumpStartTime = Time.time;
                track.JumpLastSampleTime = 0f;
                track.JumpAirPositions = new Vector3[TRAJ_MAX_SAMPLES];
                track.JumpAirTimestamps = new float[TRAJ_MAX_SAMPLES];
                track.JumpAirSampleCount = 0;
            }

            // Walk-off detection — bot went airborne without jumping (walked off a ledge)
            if (track.WasGrounded && !grounded && !jumped && !onLadder && !track.IsJumping)
            {
                track.JumpTakeoffPos = track.LastGroundedPos;
                track.IsJumping = true;
                track.JumpStartTime = Time.time;
                track.JumpLastSampleTime = 0f;
                track.JumpAirPositions = new Vector3[TRAJ_MAX_SAMPLES];
                track.JumpAirTimestamps = new float[TRAJ_MAX_SAMPLES];
                track.JumpAirSampleCount = 0;
            }

            // Sample bot air positions during jump
            if (!grounded) SampleAirPosition(track, pos);

            // --- Shared transition detection ---
            ProcessLadder(track, pos, onLadder, isPlayer: false);
            if (grounded) ScanSurroundings(pos, track, isPlayer: false);
            ProcessSlide(track, pos, isSliding, isPlayer: false);
            ProcessSlideJump(track, pos, grounded, isPlayer: false);

            UpdateTrackState(track, pos, grounded, onLadder, isSliding);
        }

        // ===================== SHARED HELPERS =====================
        // These deduplicate logic between RecordPlayer and RecordBot.

        /// <summary>Track ladder entry/exit transitions and create bidirectional Ladder edges.</summary>
        private static void ProcessLadder(PlayerTrack track, Vector3 pos, bool onLadder, bool isPlayer)
        {
            if (onLadder && !track.WasOnLadder)
            {
                track.LadderEntryPos = pos;

                // Jump-to-ladder: was jumping, now grabbed ladder — record jump edge to ladder base
                if (track.IsJumping && NavGraph.Instance != null)
                {
                    float jumpDist = Vector3.Distance(track.JumpTakeoffPos, pos);
                    if (jumpDist > 1f)
                    {
                        var jumpEdge = NavGraph.Instance.AddSpecialEdge(
                            track.JumpTakeoffPos, pos, EdgeType.Jump, isPlayer: isPlayer);
                        if (jumpEdge != null && track.JumpAirSampleCount > 0)
                        {
                            jumpEdge.AirPositions = new Vector3[track.JumpAirSampleCount];
                            jumpEdge.AirTimestamps = new float[track.JumpAirSampleCount];
                            System.Array.Copy(track.JumpAirPositions, jumpEdge.AirPositions, track.JumpAirSampleCount);
                            System.Array.Copy(track.JumpAirTimestamps, jumpEdge.AirTimestamps, track.JumpAirSampleCount);
                            jumpEdge.AirSampleCount = track.JumpAirSampleCount;
                            if (isPlayer)
                            {
                                jumpEdge.TakeoffDir = track.JumpTakeoffDir;
                                jumpEdge.TakeoffSpeed = track.JumpTakeoffSpeed;
                            }
                            jumpEdge.LockedAirTime = track.JumpAirTimestamps[track.JumpAirSampleCount - 1];
                        }
                    }
                    track.IsJumping = false;
                }
            }
            if (!onLadder && track.WasOnLadder)
            {
                float ladderDist = Vector3.Distance(track.LadderEntryPos, pos);
                if (ladderDist > 1f && NavGraph.Instance != null)
                {
                    NavGraph.Instance.AddSpecialEdge(track.LadderEntryPos, pos, EdgeType.Ladder, isPlayer: isPlayer);
                    NavGraph.Instance.AddSpecialEdge(pos, track.LadderEntryPos, EdgeType.Ladder, isPlayer: isPlayer);
                }
            }
        }

        /// <summary>Track slide entry/exit and create Slide edges.</summary>
        private static void ProcessSlide(PlayerTrack track, Vector3 pos, bool isSliding, bool isPlayer)
        {
            if (isSliding && !track.WasSliding)
            {
                track.SlideEntryPos = pos;
                track.SlideStartPos = pos;
            }
            if (!isSliding && track.WasSliding)
            {
                float slideDist = Vector3.Distance(track.SlideEntryPos, pos);
                if (slideDist > 1.5f && NavGraph.Instance != null)
                    NavGraph.Instance.AddSpecialEdge(track.SlideEntryPos, pos, EdgeType.Slide, isPlayer: isPlayer);
                track.SlideEndTime = Time.time;
            }
        }

        /// <summary>Detect slide-jump combos and create Jump edges from slide start to landing.</summary>
        private static void ProcessSlideJump(PlayerTrack track, Vector3 pos, bool grounded, bool isPlayer)
        {
            if (!track.IsJumping || !grounded || track.SlideEndTime <= 0f) return;
            if (track.SlideStartPos.sqrMagnitude < 0.01f) return;

            float timeSinceSlide = Time.time - track.SlideEndTime;
            if (timeSinceSlide < 1.5f)
            {
                float sjDist = Vector3.Distance(track.SlideStartPos, pos);
                if (sjDist > 3f && NavGraph.Instance != null)
                {
                    var sjEdge = NavGraph.Instance.AddSpecialEdge(track.SlideStartPos, pos, EdgeType.Jump, isPlayer: isPlayer);
                    if (isPlayer && sjEdge != null)
                    {
                        sjEdge.Confidence = 1f;
                        sjEdge.SuccessCount = Mathf.Max(sjEdge.SuccessCount, 10);
                    }
                }
                track.SlideEndTime = 0f;
                track.SlideStartPos = Vector3.zero;
            }
        }

        /// <summary>Sample air position during jump for trajectory recording.</summary>
        private static void SampleAirPosition(PlayerTrack track, Vector3 pos)
        {
            if (!track.IsJumping || track.JumpAirPositions == null) return;
            float airTime = Time.time - track.JumpStartTime;
            if (airTime - track.JumpLastSampleTime >= TRAJ_INTERVAL && track.JumpAirSampleCount < TRAJ_MAX_SAMPLES)
            {
                track.JumpAirPositions[track.JumpAirSampleCount] = pos;
                track.JumpAirTimestamps[track.JumpAirSampleCount] = airTime;
                track.JumpAirSampleCount++;
                track.JumpLastSampleTime = airTime;
            }
        }

        /// <summary>Update tracking state at end of frame.</summary>
        private static void UpdateTrackState(PlayerTrack track, Vector3 pos, bool grounded, bool onLadder, bool isSliding)
        {
            if (grounded) track.LastGroundedPos = pos;
            track.WasGrounded = grounded;
            track.WasOnLadder = onLadder;
            track.WasSliding = isSliding;
        }

        /// <summary>
        /// Scan surroundings with raycasts to find nearby walkable terrain and add to graph.
        /// Called periodically for both players and bots.
        /// </summary>
        private static void ScanSurroundings(Vector3 pos, PlayerTrack track, bool isPlayer)
        {
            if (NavGraph.Instance == null) return;
            // Scan is on whenever we're actively training the graph. Play mode = read-only.
            bool isPlay = NavGraph.Instance.Mode == NavMode.Play;
            if (isPlay) return;
            if (Time.time - track.LastSurroundScan < SURROUND_SCAN_INTERVAL) return;
            track.LastSurroundScan = Time.time;

            // Scale step size and dedup radius with density
            float densityMul = isPlayer ? Plugin.GetPlayerDensityMultiplier() : Plugin.GetBotDensityMultiplier();
            float stepSize = Mathf.Clamp(1.5f * densityMul, 0.8f, 4f);  // Dense=small steps, Sparse=big steps
            float dedupRadius = Mathf.Clamp(1f * densityMul, 0.5f, 3f); // Match node spacing to density

            for (int i = 0; i < SURROUND_SCAN_DIRS; i++)
            {
                Vector3 dir = _scanDirs[i];
                Vector3 perp = _scanPerps[i];

                float scanRange = Plugin.GetScanRadius();
                for (float d = stepSize; d <= scanRange; d += stepSize)
                {
                    // Cast from high up to catch both uphill and downhill slopes
                    Vector3 scanPos = pos + dir * d + Vector3.up * 4f;

                    if (Physics.Raycast(scanPos, Vector3.down, out RaycastHit hit, 8f, GROUND_MASK))
                    {
                        if (hit.normal.y < 0.42f) continue;
                        if (hit.collider.CompareTag("Killz") || hit.collider.CompareTag("DamageZone")) continue;

                        Vector3 groundPos = hit.point;

                        // Width check with pre-computed perpendicular
                        bool side1 = Physics.Raycast(groundPos + perp * 0.4f + Vector3.up * 0.5f, Vector3.down, 2f, GROUND_MASK);
                        bool side2 = Physics.Raycast(groundPos - perp * 0.4f + Vector3.up * 0.5f, Vector3.down, 2f, GROUND_MASK);
                        if (!side1 || !side2) continue;

                        var existing = NavGraph.Instance.FindNearestNode(groundPos, dedupRadius);
                        if (existing == null)
                        {
                            Vector3 checkDir = groundPos - pos;
                            float checkDist = checkDir.magnitude;
                            if (checkDist > 0.5f)
                            {
                                checkDir.Normalize();
                                bool footBlocked = Physics.Raycast(pos + Vector3.up * 0.3f, checkDir, checkDist, WALL_MASK, QueryTriggerInteraction.Ignore);
                                bool waistBlocked = Physics.Raycast(pos + Vector3.up * 1f, checkDir, checkDist, WALL_MASK, QueryTriggerInteraction.Ignore);
                                if (footBlocked && waistBlocked) continue;
                            }
                            NavGraph.Instance.AddPosition(groundPos + Vector3.up * 0.05f, isPlayer: isPlayer);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Clear tracking for a specific player (on disconnect/death).
        /// </summary>
        public static void ClearPlayer(int id)
        {
            _tracks.Remove(id);
        }

        /// <summary>
        /// Report that a tracked entity (player or bot) died at this position.
        /// If they were airborne (falling), marks the takeoff→death as a lethal fall edge.
        /// </summary>
        public static void ReportDeath(int trackId, Vector3 deathPos)
        {
            if (NavGraph.Instance == null) return;
            if (!_tracks.TryGetValue(trackId, out var track)) return;

            // If they were mid-fall (IsJumping=true, death below takeoff), this drop-off is lethal
            if (track.IsJumping && track.JumpTakeoffPos.y - deathPos.y > 2f)
            {
                NavGraph.Instance.ReportFallDeath(track.JumpTakeoffPos, deathPos);
            }

            track.IsJumping = false;
        }

        /// <summary>
        /// Clear all tracking data (on scene change).
        /// </summary>
        public static void ClearAll()
        {
            _tracks.Clear();
        }
    }
}
