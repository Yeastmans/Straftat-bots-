using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace StraftatBots
{
    public enum BotState
    {
        FindWeapon,
        GoToWeapon,
        PickUpWeapon,
        Hunt,
        Dead
    }

    /// <summary>
    /// Priority-ranked jump reasons. Higher value = higher priority.
    /// A jump in progress can only be overridden by an equal or higher priority.
    /// </summary>
    public enum JumpReason
    {
        None = 0,
        CombatStrafe = 10,      // Stuck against wall during combat
        StuckRecovery = 20,     // CheckStuck escalation 0
        ExploreStuck = 30,      // Stuck while wandering/exploring
        Obstacle = 40,          // Reactive: feet blocked, waist/head clear
        WallJump = 50,          // Wall jump (80% force, horizontal push)
        GapDetection = 60,      // Ground gap detected ahead on walk edge
        EdgeAhead = 70,         // IsEdgeAhead + target across gap
        GraphJump = 80,         // Nav graph Jump/Fall/WallJump edge execution
        Vault = 90,             // FPC-style vault (short pop, not a real jump)
    }

    public enum ExploreState
    {
        None,
        HeightSeek,     // Find ladders, ramps, ledges when target is above/below
        PlatformProbe,  // Detect platforms across gaps and attempt jumps
        EdgeWalk,       // Walk along gap edges to find crossing points
        FrontierWalk    // Walk to frontier nodes at boundary of explored territory
    }

    public partial class BotController : MonoBehaviour
    {
        // Const layer masks — avoid recomputation every frame
        private const int WALL_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);
        private const int GROUND_MASK = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 14);
        private const int EXPLOSIVE_MASK = (1 << 0) | (1 << 7) | (1 << 14);

        // Pre-allocated physics buffers — shared across all bots (Unity is single-threaded)
        private static readonly Collider[] _overlapBuffer = new Collider[32];
        private static readonly Collider[] _zoneOverlapBuffer = new Collider[128];

        // Bot instance tracking — avoid GetComponent<BotController> in hot paths
        private static readonly HashSet<int> _botInstanceIds = new HashSet<int>();

        public int BotId;
        public string BotName;
        public int PlayerId;

        public bool IsDead = false;

        public BotState State = BotState.FindWeapon;

        private CharacterController _cc;
        private PlayerHealth _playerHealth;
        private PlayerPickup _playerPickup;
        private PlayerValues _playerValues;
        private FirstPersonController _fpc;

        // Animators
        private Animator _bodyAnimator;
        private Animator _globalAnimator;

        // Weapon
        private GameObject _heldWeaponObj;
        private Weapon _heldWeapon;
        private ItemBehaviour _heldBehaviour;
        private Transform _weaponTarget;
        private Transform _playerTarget;
        private ItemBehaviour _targetItem;
        private GameObject _weaponSource; // Original weapon (for reading prefab refs that don't survive clone)
        private bool _isShooting; // true when standing still to shoot
        private float _combatStaleTimer;  // Time spent shooting without hitting
        private float _lastHitTime;       // Last time we hit an enemy
        private int _lastNodeRepeatedId = -1; // Track if we keep hitting the same node
        private int _nodeRepeatCount;          // How many times we've reached this node without progress
        private int[] _recentNodeIds = new int[8]; // Circular buffer of recently visited node IDs
        private int _recentNodeIdx;
        private int _recentNodeCount;

        // Nodeless lock: when bouncing between nodes or stuck, temporarily force MoveTowardNodeless
        // so the bot chases targets directly instead of re-pathing through the same bad edges.
        // In Play mode with sparse graphs, the graph can't be retrained — direct movement is the
        // only way out.
        private float _nodelessLockTimer;
        private int _nodelessBounceCount;    // Escalate lock duration on repeated bounces
        private float _lastBounceTime;       // Track recency so the escalation decays over time

        // Projectile weapon cache
        private bool _isProjectileWeapon;
        private Component _projectilePrefab;
        private float _launchForce = 12f;

        // Cached weapon type flags (set on pickup, avoids GetComponent every frame)
        private bool _cachedIsMelee;
        private bool _cachedIsPlaceable;
        private bool _cachedIsDualLauncher;
        private bool _cachedIsBubbleLauncher;
        private bool _cachedIsGrenade;
        private bool _cachedIsShotgun;
        private bool _cachedIsMinigun;
        private bool _cachedIsChargeGun;
        private bool _cachedIsBeamGun;
        private bool _cachedIsLargeRaycast;
        private bool _cachedIsRepulsive;
        private bool _cachedIsPropeller;
        private Propeller _cachedPropeller;

        // Cached weapon component references — avoid GetComponent every shot
        private Shotgun _cachedShotgun;
        private Minigun _cachedMinigun;
        private ChargeGun _cachedChargeGun;
        private BeamGun _cachedBeamGun;
        private MeleeWeapon _cachedMeleeWeapon;
        private LargeRaycastGun _cachedLargeRaycast;
        private bool _cachedIsExplosiveWeapon;


        // Cached AllHumansDead result
        private bool _cachedAllHumansDead;
        private float _allHumansDeadTimer;

        // Weapon validity cache — avoids per-item GetComponent in FindNearestWeapon
        private static Dictionary<int, bool> _weaponValidCache = new Dictionary<int, bool>();
        private static float _weaponValidCacheTime;

        // Static caches — shared across all bots, refreshed periodically
        private static SpawnPoint[] _cachedSpawns;
        private static float _cachedSpawnsTime;
        private static PlayerHealth[] _cachedPlayers;
        private static float _cachedPlayersTime;
        private static ItemBehaviour[] _cachedItems;
        private static float _cachedItemsTime;
        private static Teleporter[] _cachedTeleporters;
        private static float _cachedTeleportersTime;

        /// <summary>Check if a GameObject is a bot without GetComponent.</summary>
        public static bool IsBot(GameObject go) => go != null && _botInstanceIds.Contains(go.GetInstanceID());
        public static bool IsBot(Component c) => c != null && _botInstanceIds.Contains(c.gameObject.GetInstanceID());

        /// <summary>Clear all static caches. Call on scene change.</summary>
        public static void ClearStaticCaches()
        {
            _cachedSpawns = null; _cachedSpawnsTime = 0f;
            _cachedPlayers = null; _cachedPlayersTime = 0f;
            _cachedItems = null; _cachedItemsTime = 0f;
            _cachedTeleporters = null; _cachedTeleportersTime = 0f;
            _fieldCache.Clear();
            _botInstanceIds.Clear();
            _weaponValidCache.Clear();
        }

        private static SpawnPoint[] GetCachedSpawns()
        {
            if (_cachedSpawns == null || Time.time - _cachedSpawnsTime > 10f)
            {
                _cachedSpawns = Object.FindObjectsOfType<SpawnPoint>();
                _cachedSpawnsTime = Time.time;
            }
            return _cachedSpawns;
        }
        private static PlayerHealth[] GetCachedPlayers()
        {
            if (_cachedPlayers == null || Time.time - _cachedPlayersTime > 0.5f)
            {
                _cachedPlayers = Object.FindObjectsOfType<PlayerHealth>();
                _cachedPlayersTime = Time.time;
            }
            return _cachedPlayers;
        }
        private static ItemBehaviour[] GetCachedItems()
        {
            if (_cachedItems == null || Time.time - _cachedItemsTime > 1f)
            {
                _cachedItems = Object.FindObjectsOfType<ItemBehaviour>();
                _cachedItemsTime = Time.time;
            }
            return _cachedItems;
        }
        private static Teleporter[] GetCachedTeleporters()
        {
            if (_cachedTeleporters == null || Time.time - _cachedTeleportersTime > 2f)
            {
                _cachedTeleporters = Object.FindObjectsOfType<Teleporter>();
                _cachedTeleportersTime = Time.time;
            }
            return _cachedTeleporters;
        }

        // Pre-allocated arrays (avoid GC in hot paths)
        private Vector3[] _claymoreAimDirs = new Vector3[8];

        // Intentional jump — skip edge detection briefly after committing to a jump
        private float _intentionalJumpTimer;

        // Weapon equip cooldown — prevents instant use after pickup
        private float _equipTimer;

        // Online hand positions
        private Transform[] _onlinePositions;

        // Fake camera
        private Camera _botCam;

        // Audio for weapon sounds (on the bot's NetworkObject so it syncs)
        private AudioSource _botAudio;


        // ---- Movement (exact FPC values) ----
        private float _walkSpeed = 7f;       // Exact FPC: walkSpeed = 7
        private float _sprintSpeed = 12f;    // Exact FPC: sprintSpeed = 12
        private float _crouchSpeed = 5f;     // Exact FPC: crouchSpeed = 5
        private float _airSpeed = 10f;       // Exact FPC: airSpeed = 10
        private float _sprintAirSpeed = 14f; // Exact FPC: sprintAirSpeed = 14
        private float _acceleration = 15f;
        private float _verticalVelocity;
        private float _pickupRange = 2.5f;
        private float _currentHorizInput; // smoothed 0-1

        // Gravity (exact FPC values)
        private float _gravityNormal = 30f;
        private float _gravityJump = 20f;
        private float _gravityCrouch = 40f;
        private float _maxFallSpeed = -40f;

        // Combat
        private float _detectionRange = 40f;
        private float _attackRange = 30f;
        private float _meleeRange = 3f;
        private float _minRangedDist = 4f;
        private float _fireTimer;
        private float _turnSpeed = 6f;
        private float _aimInaccuracy = 2.5f;

        // Weapon state machine
        private bool _isBurstFiring;
        private int _burstShotsRemaining;
        private float _burstShotTimer;

        // Full-auto burst pause — bots fire in short bursts, not continuous spray
        private int _autoShotsFired;
        private float _autoPauseTimer;
        private float _burstShotDelay;
        private bool _isReloading;
        private float _reloadTimer;
        private bool _isChargingWeapon;
        private float _chargeTimer;
        private float _chargeTimeRequired;
        private bool _isSpinningUp;
        private float _spinUpTimer;
        private float _spinUpTimeRequired;
        private bool _minigunSpunUp;
        private float _recoilAccumulated; // Builds with sustained fire, decays over time
        private float _shotsSinceRest; // Tracks sustained fire for recoil bloom

        // Timers
        private float _searchTimer;
        private float _searchInterval = 0.2f;
        private float _stuckTimer;
        private float _logTimer;

        // Wander
        private Vector3 _wanderTarget;
        private bool _hasWanderTarget;

        // Per-bot exploration memory — tracks which areas this bot has thoroughly explored
        private HashSet<long> _exploredCells = new HashSet<long>();
        private float _exploredCellTimer;
        private int _exploredStaleCount; // How many times we've revisited explored areas

        // NavGraph pathfinding
        private List<NavNode> _graphPath = new List<NavNode>();
        private int _graphPathIndex;
        private float _repathTimer;
        private Vector3 _lastPathTarget;
        private NavNode _lastReachedNode;       // Last graph node we successfully reached
        private NavNode _prevReachedNode;       // Node before _lastReachedNode — used for shortcut (A-B-C → A-C)
        private Vector3 _lastGroundedPos;       // For recording to graph + death tracking
        private bool _justJumped;               // For recording jump edges
        private float _nextTeleportAttemptTime; // Debounce manual/path-driven teleporter use
        // Sprint slide
        private float _sprintSlideChance = 3f;

        // Ladder climbing
        private bool _onLadder;
        private float _ladderSpeed = 2f;
        private LayerMask _ladderLayer;
        private bool _ladderLayerLoaded;
        private Vector3 _lastLadderPos;       // Center of closest ladder collider
        private Vector3 _ladderFaceDir;       // Direction INTO the ladder surface
        private bool _wasOnLadder;            // Previous frame state — for dismount detection
        private float _ladderDismountTimer;   // Forward push timer after reaching ladder top
        private float _ladderStuckTimer;      // Time spent on ladder — force dismount after 5s
        private float _ladderClimbTimer;     // Total climb time — safety ceiling cap
        private float _lastLadderTouchTime = -999f; // Time.time of last actual ladder touch — watchdog

        // Launch/force/gravity zones — mirrors player trigger-zone behavior.
        private Vector3 _zoneForce;              // Accumulated external force from zones
        private float _zoneForceDuration;         // Time remaining for zone force (suppresses stuck/steering)
        private bool _zoneLaunchInAir;            // True once an impulse launched us — cleared on landing
        private float _gravityZoneMultiplier = 1f;
        private readonly System.Collections.Generic.HashSet<ImpulseZone> _activeImpulseZones
            = new System.Collections.Generic.HashSet<ImpulseZone>();
        private readonly System.Collections.Generic.Dictionary<GravityZone, float> _activeGravityZones
            = new System.Collections.Generic.Dictionary<GravityZone, float>(4);
        // Active ForceZones the bot is inside. Mirrors ForceZone's own player HashSet architecture —
        // we iterate these each frame and apply force ourselves, because Unity's OnTriggerStay fires
        // unreliably on CharacterController-only bots (no Rigidbody), causing the "barely launches" bug.
        private readonly System.Collections.Generic.List<ForceZone> _activeForceZones
            = new System.Collections.Generic.List<ForceZone>(4);
        private readonly System.Collections.Generic.HashSet<ImpulseZone> _scannedImpulseZones
            = new System.Collections.Generic.HashSet<ImpulseZone>();
        private readonly System.Collections.Generic.HashSet<ForceZone> _scannedForceZones
            = new System.Collections.Generic.HashSet<ForceZone>();
        private readonly System.Collections.Generic.HashSet<GravityZone> _scannedGravityZones
            = new System.Collections.Generic.HashSet<GravityZone>();
        private readonly System.Collections.Generic.List<ImpulseZone> _impulseZoneExitBuffer
            = new System.Collections.Generic.List<ImpulseZone>(4);
        private readonly System.Collections.Generic.List<GravityZone> _gravityZoneExitBuffer
            = new System.Collections.Generic.List<GravityZone>(4);

        // Reactive steering (fallback when no graph path)
        private int _avoidDir;

        // Debug: last horizontal movement direction (set at _cc.Move calls)
        private Vector3 _lastMoveDir;

        // Aim
        private Vector3 _aimOffset;
        private float _aimOffsetTimer;

        // Jumping (exact FPC values)
        private float _jumpForce = 8f;
        private float _coyoteTimer;
        private Vector3 _jumpDir;            // Locked direction during jump arc — prevents mid-air steering
        private float _landingFollowTimer;   // Forward push after landing to clear railings/edges
        private JumpReason _activeJumpReason; // What triggered the current jump (for priority gating)
        private float _vaultKillTimer;       // FPC vault: kill vertical velocity after 0.15s
        private bool _movedThisFrame;        // True if a movement method already called cc.Move this frame

        // SMOOTHNESS: jump charge window.
        // When TryJump is called, vertical velocity is held at ~0 for this many seconds before
        // the actual upward force applies. Gives the bot a moment to commit direction and full
        // horizontal speed, turning coin-flip ledge jumps into consistent ones.
        private float _jumpChargeTimer;
        private float _pendingJumpForce;

        // AIR STRAFE: mid-air horizontal micro-correction toward the intended landing point.
        // Seeded by the jump trigger (GraphJump uses landing node, Obstacle uses box-top,
        // EdgeAhead/GapDetection uses picked target). Gets applied every frame while airborne
        // via a small _cc.Move nudge toward the target — keeps repeat jumps landing on spot.
        private Vector3 _airStrafeTarget;
        private bool _airStrafeActive;

        // LADDER: position-delta watchdog for mid-ladder freeze.
        // If a bot claims _onLadder but climbs < 0.2m in > 1.2s, we force-dismount with a push.
        private float _ladderLastYSample;
        private float _ladderYSampleTime;
        // LADDER: stable face-direction pin. The per-frame ladder normal can flip at corners
        // when the bot drifts; once we've locked a good face-dir we keep it for the climb.
        private Vector3 _ladderPinnedFaceDir;
        private bool _ladderFaceDirPinned;
        // LADDER: re-path watchdog. Every ~2 sec while on a ladder, re-check whether the
        // current path is still valid; if not, dismount gracefully.
        private float _ladderRepathTimer;

        // Weapon pursuit
        private float _weaponPursuitTimer;
        private System.Collections.Generic.Dictionary<ItemBehaviour, float> _blacklistedWeapons = new System.Collections.Generic.Dictionary<ItemBehaviour, float>();

        // Sliding (exact FPC slide)
        private float _slideTimer;
        private bool _isSliding;
        private float _slideResetTimer;
        private Vector3 _slideForce;
        private float _slideForceFactor;
        private Vector3 _slideLockedDir;  // Direction locked at slide start — no turning during slide

        // Walk/run transition
        private float _speedChangeCooldown;

        // Leaning
        private bool _isLeaning;
        private float _leanDir;

        // Combat strafing / dodge
        private int _strafeDir = 1;
        private float _strafeSwitchTimer;
        private float _dodgeTimer;
        private bool _isDodging;
        private Vector3 _dodgeDir;
        // Hunt-mode smoothing state
        private Vector3 _smoothedStrafeDir;
        private float _smoothedApproach;
        private float _huntSubState; // -1 = backing up, 0 = strafing, 1 = advancing; smoothed to avoid boundary flicker
        private float _huntSubStateHold; // debounce timer for sub-state switch


        // Crouch
        private bool _isCrouching;
        private float _crouchTimer;

        // Freeze until round starts
        private bool _frozen = true;

        // Head blocked timer (for auto-slide under low ceilings)

        // Stun (taser)
        private float _stunTimer;

        // Explosive avoidance
        private float _explosiveCheckTimer;
        private float _explosiveFleeTimer;

        // Placed claymores — bots avoid these positions (with timestamp for expiry)
        private static System.Collections.Generic.List<(Vector3 pos, float time)> _placedClaymorePositions = new System.Collections.Generic.List<(Vector3, float)>();



        private bool _loggedError;

        private void Awake()
        {
            _botInstanceIds.Add(gameObject.GetInstanceID());
            _cc = GetComponent<CharacterController>();
            if (_cc != null)
            {
                _cc.stepOffset = 0.6f;  // Higher step — handles uneven terrain better
                _cc.slopeLimit = 65f;   // Match FPC: slides at 65°+, walks up anything under
                _cc.skinWidth = 0.08f;  // Slightly thicker skin for smoother collisions
            }
            _playerHealth = GetComponent<PlayerHealth>();

            _playerValues = GetComponent<PlayerValues>();
            _fpc = GetComponent<FirstPersonController>();
            _playerPickup = GetComponent<PlayerPickup>();

            FindAnimators();
            FindOnlinePositions();
            _lastAnimPos = transform.position;

            GameObject camObj = new GameObject("BotAimCam");
            camObj.transform.SetParent(transform);
            camObj.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            _botCam = camObj.AddComponent<Camera>();
            _botCam.enabled = false;

            // Get or create AudioSource on the bot for weapon sounds
            _botAudio = GetComponent<AudioSource>();
            if (_botAudio == null) _botAudio = gameObject.AddComponent<AudioSource>();
            _botAudio.spatialBlend = 1f; // 3D sound
            _botAudio.maxDistance = 50f;
            _botAudio.rolloffMode = AudioRolloffMode.Linear;

            _avoidDir = Random.value > 0.5f ? 1 : -1;
            _slideTimer = Random.Range(10f, 20f);
        }

        private void FindAnimators()
        {
            foreach (var anim in GetComponentsInChildren<Animator>(true))
            {
                string name = anim.gameObject.name.ToLower();
                if (name.Contains("armature"))
                    _bodyAnimator = anim;
                else if (name.Contains("aboubi") || name.Contains("sk_"))
                    _globalAnimator = anim;
            }
            if (_bodyAnimator == null)
                _bodyAnimator = GetComponentInChildren<Animator>();

            Plugin.Log.LogInfo($"[{BotName}] Body animator: {(_bodyAnimator != null ? _bodyAnimator.gameObject.name : "null")}, " +
                               $"Global animator: {(_globalAnimator != null ? _globalAnimator.gameObject.name : "null")}");
        }

        private void FindOnlinePositions()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.ToLower().Contains("onlinepositions"))
                {
                    var positions = t.GetComponentsInChildren<ItemPosition>(true);
                    _onlinePositions = new Transform[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                        _onlinePositions[i] = positions[i].transform;
                    Plugin.Log.LogInfo($"[{BotName}] Found {_onlinePositions.Length} online positions");
                    break;
                }
            }
        }

        // Track wall collisions for anti-stuck
        private Vector3 _lastCollisionNormal;
        private float _collisionTimer;
        private Vector3 _commitDir;          // Direction we're committed to after wall redirect
        private float _commitTimer;          // Time remaining to hold committed direction

        private float _wallHitTimer;
        private int _wallRepathCount;
        private Vector3 _pendingSep; // bot-to-bot separation accumulated in OnControllerColliderHit, applied in Update
        private int _wallJumpCount;          // Wall jumps used this airtime
        private bool _canWallJump;           // Valid wall-jump surface detected
        private Vector3 _wallJumpNormal;     // Normal of the wall to jump off
        private bool _vaultCooldown;          // Prevent vault spam — resets on ground
        private Vector3 _vaultTakeoffPos;    // Position before vault (for edge creation)
        private NavEdge _currentJumpEdge;    // The jump edge being followed (for trajectory replay)
        private float _jumpStartTime;        // Time.time when current jump started
        private Vector3 _lastLandingDir;     // Direction of last jump landing — bias next path
        private bool _jumpMidCorrected;      // True after single mid-air correction applied

        // Trajectory replay — bot steers CC.Move toward recorded air positions
        private int _trajIndex;              // Current waypoint index in AirPositions
        private bool _trajActive;            // True while replaying a recorded trajectory
        // _jumpAlignTimer removed — alignment pause replaced with speed-matching approach
        private bool _inJumpChain;           // True during consecutive jump edge execution
        private int _chainJumpCount;         // Number of jumps completed in current chain

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            float wallAngle = Vector3.Angle(hit.normal, Vector3.up);

            // Wall jump detection — only when NOT in an intentional jump arc
            if (!_cc.isGrounded && !_onLadder && _wallJumpCount < 1
                && _intentionalJumpTimer <= 0f // Don't trigger during normal jumps
                && (hit.gameObject.layer == 0 || hit.gameObject.layer == 14)
                && wallAngle > 88f && wallAngle < 100f)
            {
                _canWallJump = true;
                _wallJumpNormal = hit.normal;
            }

            // Vault detection — matches FPC CheckForVault() exactly
            // Conditions: airborne, feet blocked, chest+head clear, wall nearly vertical
            // DON'T vault during intentional jumps — would derail the planned trajectory
            if (!_cc.isGrounded && !_onLadder && !_vaultCooldown
                && _intentionalJumpTimer <= 0f // Don't vault during jump edges
                && wallAngle > 80f && wallAngle < 130f
                && _verticalVelocity > -5f) // Not falling too fast
            {
                Vector3 fwd = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
                // Match FPC raycasts: feet hits, chest+head clear
                bool rayFeet = Physics.Raycast(transform.position + Vector3.up * 0.3f, fwd, 1.4f,
                    WALL_MASK, QueryTriggerInteraction.Ignore);
                bool rayChest = Physics.Raycast(transform.position + Vector3.up * 1.2f, fwd, 1.5f,
                    WALL_MASK, QueryTriggerInteraction.Ignore);
                bool rayHead = Physics.Raycast(transform.position + Vector3.up * 1.8f, fwd, 2f,
                    WALL_MASK, QueryTriggerInteraction.Ignore);

                if (rayFeet && !rayChest && !rayHead)
                {
                    // Check there's ground on top to land on (don't vault into void)
                    Vector3 topCheck = transform.position + fwd * 1f + Vector3.up * 2.5f;
                    bool groundOnTop = Physics.Raycast(topCheck, Vector3.down, 3f,
                        GROUND_MASK, QueryTriggerInteraction.Ignore);
                    if (groundOnTop)
                    {
                        if (TryJump(JumpReason.Vault, fwd, force: 9f))
                        {
                            _vaultTakeoffPos = transform.position;
                            _vaultCooldown = true;
                            _landingFollowTimer = 0.5f;
                            // Gentle forward push (FPC BForce decays at rate 3, so ~0.5s of push)
                            if (_cc != null && _cc.enabled)
                                _cc.Move(fwd * 0.5f * Time.deltaTime);
                        }
                    }
                }
            }

            // Reset vault cooldown when grounded — record vault edge
            if (_cc.isGrounded)
            {
                if (_vaultCooldown && _vaultTakeoffPos.sqrMagnitude > 0.01f && NavGraph.Instance != null)
                {
                    float vaultDist = Vector3.Distance(_vaultTakeoffPos, transform.position);
                    if (vaultDist > 1f && vaultDist < 10f)
                    {
                        NavGraph.Instance.AddSpecialEdge(_vaultTakeoffPos, transform.position,
                            EdgeType.Jump, isPlayer: false);
                    }
                    _vaultTakeoffPos = Vector3.zero;
                }
                _vaultCooldown = false;
            }

            // During landing follow-through — don't deflect or jump, just push forward
            if (_landingFollowTimer > 0f) return;

            // Hit something at railing/waist height — try jumping over before deflecting
            // Only for actual walls (>65°), not slopes the CC can walk up
            if (wallAngle > 65f && _cc.isGrounded && _intentionalJumpTimer <= 0f && !_onLadder)
            {
                float hitHeight = hit.point.y - transform.position.y;
                // Railing/low wall: hit between knee and chest height, head is clear above
                if (hitHeight > 0.3f && hitHeight < 1.3f)
                {
                    bool headClear = !Physics.Raycast(transform.position + Vector3.up * 1.7f,
                        transform.forward, 1.5f, WALL_MASK, QueryTriggerInteraction.Ignore);
                    if (headClear)
                    {
                        if (TryJump(JumpReason.Obstacle, transform.forward))
                            return; // Jump over — don't deflect
                    }
                }
            }

            // Track wall collisions — only actual walls, not walkable slopes
            if (wallAngle > 65f)
            {
                _lastCollisionNormal = hit.normal;
                _collisionTimer = 0.5f;

                // Continuous wall hits = stuck — boost stuck timer even without graph path
                _wallHitTimer += Time.deltaTime;
                if (_wallHitTimer > 1f && _graphPath.Count == 0)
                {
                    _stuckTimer += 1f; // Force stuck detection in nodeless mode
                    _wallHitTimer = 0f;
                }

                if (_wallHitTimer > 0.5f && _graphPath.Count > 0)
                {
                    _wallHitTimer = 0f;
                    _wallRepathCount++;

                    // Report bad edge
                    if (NavGraph.Instance != null && _lastReachedNode != null
                        && _graphPathIndex < _graphPath.Count)
                    {
                        NavGraph.Instance.ReportWallEdge(
                            _lastReachedNode.Id, _graphPath[_graphPathIndex].Id);
                    }

                    _graphPath.Clear();
                    _graphPathIndex = 0;
                    _repathTimer = 0f;

                    // Push away from wall — horizontal only
                    Vector3 pushAway = hit.normal; pushAway.y = 0;
                    if (pushAway.sqrMagnitude > 0.01f && _cc != null && _cc.enabled)
                        _cc.Move(pushAway.normalized * 0.3f);

                    // After 3 wall repatches, give up on current target entirely
                    if (_wallRepathCount >= 3)
                    {
                        _wallRepathCount = 0;
                        _weaponTarget = null;
                        _targetItem = null;
                        _playerTarget = null;
                        _hasWanderTarget = false;
                        _wanderChangeTimer = 0f;
                        State = _heldWeapon != null ? BotState.Hunt : BotState.FindWeapon;
                    }
                }
            }
            else
            {
                _wallHitTimer = 0f;
            }

            // Bot-to-bot separation — accumulate into _pendingSep, applied in Update.
            // Never call _cc.Move here: it can re-trigger OnControllerColliderHit → stack overflow.
            var otherBot = hit.gameObject.GetComponentInParent<BotController>();
            if (otherBot != null && otherBot != this && !IsDead && !otherBot.IsDead)
            {
                Vector3 sep = transform.position - otherBot.transform.position;
                sep.y = 0f;
                if (sep.sqrMagnitude < 0.0001f)
                    sep = new Vector3(Mathf.Sin(GetInstanceID() * 0.618f), 0f, Mathf.Cos(GetInstanceID() * 0.618f));
                _pendingSep += sep.normalized * 0.05f;
                if (_repathTimer > 0.4f) _repathTimer = 0.4f;
            }
        }

        // Handle damage zones, teleporters, and trigger-zone movement.
        // Harmony also patches StraftatTriggerZone, but bots keep this local path as a fallback
        // because CharacterController trigger callbacks can be inconsistent on modded maps.
        private void OnTriggerEnter(Collider col)
        {
            if (IsDead || _playerHealth == null || _playerHealth.isKilled) return;
            HandleTriggerZoneEnter(col);
            TryEnvironmentKill(col);
            TryTeleport(col);
        }

        private void OnTriggerStay(Collider col)
        {
            if (IsDead || _playerHealth == null || _playerHealth.isKilled) return;
            HandleTriggerZoneStay(col);
            TryEnvironmentKill(col);
        }

        private void OnTriggerExit(Collider col)
        {
            HandleTriggerZoneExit(col);
        }

        private void TryEnvironmentKill(Collider col)
        {
            bool isKillZone = col.CompareTag("Killz");
            bool isDamageZone = col.CompareTag("DamageZone");
            if (!isKillZone && !isDamageZone) return;

            // Use game's RPCs so all clients see the death (same as real player)
            try { _playerHealth.RemoveHealth(_playerHealth.health + 10f); } catch { }
            try { _playerHealth.ChangeKilledState(true); } catch { }
            // Explode BEFORE disabling physics — ragdoll reads bone positions from graphics
            try { _playerHealth.ExplodeServer(false, false, "", -transform.forward, 30f, transform.position + Vector3.up * 2f); } catch { }
            DisableBotPhysics(gameObject);
            try { _playerHealth.DisablePlayerObjectWhenKilled(); } catch { }
            Die(null);
            try { if (PauseManager.Instance != null) PauseManager.Instance.WriteLog($"<b><color=orange>{BotName}</color></b> died to the environment"); } catch { }
        }

        private void TryTeleport(Collider col)
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            var teleporter = col.GetComponent<Teleporter>();
            if (teleporter == null) teleporter = col.GetComponentInParent<Teleporter>();
            if (!col.CompareTag("Teleport") && teleporter == null) return;
            if (teleporter == null || teleporter.teleportPoint == null) return;
            if (Time.time < _nextTeleportAttemptTime) return;
            _nextTeleportAttemptTime = Time.time + 0.25f;
            // Defer to next frame — modifying CC.enabled inside OnTriggerEnter causes FishNet sync errors
            StartCoroutine(DoTeleport(teleporter));
        }

        private System.Collections.IEnumerator DoTeleport(Teleporter tp)
        {
            yield return null; // wait one frame — avoids FishNet sync errors from CC toggle in trigger callback
            if (IsDead) yield break;
            try
            {
                Transform exit = tp.teleportPoint;
                Vector3 dest = exit.position;
                if (Physics.Raycast(dest + Vector3.up * 2f, Vector3.down, out RaycastHit snapHit, 5f))
                    dest = snapHit.point + Vector3.up * 0.05f;

                if (_cc != null && _cc.enabled)
                {
                    _cc.enabled = false;
                    transform.position = dest;
                    _cc.enabled = true;
                }
                else
                {
                    transform.position = dest;
                }

                if (!tp.dontTranslateRotation)
                    transform.eulerAngles -= new Vector3(0, tp.anglesDifference - 180f, 0);

                if (tp.propulsionPower > 0f)
                    ApplyZoneImpulse(exit.forward * tp.propulsionPower);

                _graphPath.Clear();
                _graphPathIndex = 0;
                _repathTimer = 0f;
                _stuckTimer = 0f;
                _nodelessLockTimer = 0f;
                _nextTeleportAttemptTime = Time.time + 0.4f;

                Plugin.Log.LogInfo($"[{BotName}] Teleported to {dest} power={tp.propulsionPower}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] Teleport error: {e.Message}");
            }
        }

        // Mine/claymore detection removed — game's own trigger system handles it.
        // Explosion_Postfix in BotPatches catches bots missed by IsOwner checks,
        // calls Die() + DisableBotPhysics for full death handling.

        private void LateUpdate()
        {
            if (_heldWeaponObj != null && !IsDead)
                PositionWeaponAtHand();
        }

        public void PositionWeaponAtHandPublic() => PositionWeaponAtHand();
        private void PositionWeaponAtHand()
        {
            if (_heldWeaponObj == null) return;
            if (_onlinePositions != null && _onlinePositions.Length > 0 && _onlinePositions[0] != null)
            {
                // Parent to hand bone so weapon follows arm animations (melee swing etc.)
                if (_heldWeaponObj.transform.parent != _onlinePositions[0])
                {
                    _heldWeaponObj.transform.SetParent(_onlinePositions[0]);
                    _heldWeaponObj.transform.localPosition = Vector3.zero;
                    _heldWeaponObj.transform.localRotation = Quaternion.identity;
                    _heldWeaponObj.transform.localScale = Vector3.one;
                }
            }
        }

        private void Update()
        {
            try { UpdateInternal(); }
            catch (System.Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.Log.LogError($"[{BotName}] Update error: {e.Message}\n{e.StackTrace}");
                }
            }
        }

        private void UpdateInternal()
        {
            _movedThisFrame = false;
            if (IsDead) return;

            // Death detection
            if (_playerHealth != null && (_playerHealth.isKilled || _playerHealth.health <= 0f))
            {
                Die(_playerHealth.killer);
                return;
            }

            // Void death — same flow as real player (FPC checks y < -300, we check -50 since maps vary)
            if (transform.position.y < -50f)
            {
                if (_playerHealth != null)
                {
                    // Use the game's RPCs so all clients see the death properly
                    try { _playerHealth.RemoveHealth(_playerHealth.health + 10f); } catch { }
                    try { _playerHealth.ChangeKilledState(true); } catch { }
                    try { _playerHealth.ExplodeServer(false, false, "", -transform.forward, 30f, transform.position + Vector3.up * 2f); } catch { }
                    DisableBotPhysics(gameObject);
                    try { _playerHealth.DisablePlayerObjectWhenKilled(); } catch { }
                }
                else
                {
                    DisableBotPhysics(gameObject);
                }

                try { if (PauseManager.Instance != null) PauseManager.Instance.WriteLog($"<b><color=orange>{BotName}</color></b> fell into the void"); } catch { }
                Die(null);
                return;
            }
            // Hard kill — if somehow still alive past -100, force disable
            if (transform.position.y < -100f && State != BotState.Dead)
            {
                try { DisableBotPhysics(gameObject); } catch { }
                Die(null);
                return;
            }

            // Freeze until human players can move
            if (_frozen)
            {
                if (!AnyHumanCanMove()) return;
                _frozen = false;
                _stunTimer = 0f;
                if (_fpc != null) _fpc.canMove = true;
            }

            // Stun (taser) — detect canMove being set to false by taser RPC
            if (_fpc != null && !_fpc.canMove && !_frozen)
            {
                if (_stunTimer <= 0f)
                    _stunTimer = 3f; // Default stun duration if not set

                _stunTimer -= Time.deltaTime;
                if (_stunTimer <= 0f)
                {
                    // Unfreeze — taser TargetRpc can't reach bots so we handle it
                    _fpc.canMove = true;
                    _stunTimer = 0f;
                }
                return; // Frozen — no movement, no shooting
            }

            HandleLadder();
            ApplyGravity();
            ScanTriggerZones();        // Fallback when zone trigger callbacks miss CharacterController bots.
            ApplyActiveForceZones();   // Continuous ForceZone force (mirrors game's own ForceZone.Update)

            // Apply bot-to-bot separation accumulated this frame from OnControllerColliderHit
            if (_pendingSep.sqrMagnitude > 0.0001f && _cc != null && _cc.enabled && !IsDead)
            {
                _cc.Move(_pendingSep);
                _pendingSep = Vector3.zero;
            }
            else
            {
                _pendingSep = Vector3.zero;
            }
            HandlePropeller();
            HandleWallJump();
            UpdateOverheadSlide();
            UpdateSlide();
            UpdateAnimator();
            UpdateFootsteps();

            if (TryApplyZoneMovement())
            {
                CheckStuck();
                return;
            }

            // Prune placement tracking — only needed for the 10s own-mine grace window.
            // Anything older than 12s is irrelevant (OverlapSphere handles avoidance after that).
            for (int i = _placedClaymorePositions.Count - 1; i >= 0; i--)
            {
                if (Time.time - _placedClaymorePositions[i].time > 12f)
                    _placedClaymorePositions.RemoveAt(i);
            }

            // Avoid nearby explosives (grenades, rockets, obus in flight, mines/claymores).
            // Blast radius is ~3m — 4m gives a tight safety margin without creating
            // huge no-go zones that cause bots to run into walls forever.
            _explosiveCheckTimer -= Time.deltaTime;
            if (_explosiveCheckTimer <= 0f)
            {
                _explosiveCheckTimer = 0.3f;
                float fleeRadius = 4f;
                Vector3 nearestExplosive = Vector3.zero;
                float nearestDist = float.MaxValue;

                // Use OverlapSphere instead of FindObjectsOfType (much cheaper).
                // Already catches own-placed mines too — no need for the separate
                // _placedClaymorePositions loop that used to live below.
                int nearCount = Physics.OverlapSphereNonAlloc(transform.position, fleeRadius, _overlapBuffer, EXPLOSIVE_MASK, QueryTriggerInteraction.Collide);
                for (int ci = 0; ci < nearCount; ci++)
                {
                    var col = _overlapBuffer[ci];
                    // Check explosive types directly — includes grenades, rockets, mines
                    MonoBehaviour mb = col.GetComponent<PhysicsGrenade>() as MonoBehaviour
                        ?? col.GetComponent<HandGrenade>() as MonoBehaviour
                        ?? col.GetComponent<HandGrenadeTwo>() as MonoBehaviour
                        ?? col.GetComponent<Obus>() as MonoBehaviour
                        ?? col.GetComponent<Bubble>() as MonoBehaviour
                        ?? col.GetComponent<ProximityMine>() as MonoBehaviour
                        ?? col.GetComponent<Claymore>() as MonoBehaviour;
                    if (mb == null) continue;

                    bool isMine = mb is ProximityMine || mb is Claymore;
                    if (!isMine)
                    {
                        // Grenades/rockets: skip if we threw it
                        var rootField = GetCachedField(mb.GetType(), "_rootObject");
                        if (rootField != null)
                        {
                            var root = rootField.GetValue(mb) as GameObject;
                            if (root == gameObject) continue;
                        }
                    }
                    else
                    {
                        // Own mines: 10s grace window after placing. Without this the placer
                        // steps on its own mine after respawn and freezes trying to flee from
                        // a mine sitting at the only clear direction.
                        Vector3 mpos = mb.transform.position;
                        bool isOwnRecent = false;
                        for (int pi = 0; pi < _placedClaymorePositions.Count; pi++)
                        {
                            var (ppos, ptime) = _placedClaymorePositions[pi];
                            if (Time.time - ptime < 10f && (ppos - mpos).sqrMagnitude < 1f)
                            {
                                isOwnRecent = true;
                                break;
                            }
                        }
                        if (isOwnRecent) continue;
                    }

                    float d = Vector3.Distance(transform.position, mb.transform.position);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearestExplosive = mb.transform.position;
                    }
                }

                if (nearestDist < fleeRadius)
                {
                    Vector3 away = (transform.position - nearestExplosive);
                    away.y = 0; if (away.sqrMagnitude > 0.01f) away.Normalize(); else away = transform.forward;

                    // Skip flee if the escape path is walled within 1.5m. Otherwise the bot
                    // sprints into a wall forever while the 0.3s retimer re-issues the flee,
                    // pinning it in place until it dies anyway. Accept the damage and keep
                    // doing normal AI — at least the bot stays useful.
                    Vector3 rayOrigin = transform.position + Vector3.up * 0.8f;
                    bool pathBlocked = Physics.Raycast(rayOrigin, away, 1.5f, WALL_MASK, QueryTriggerInteraction.Ignore);
                    if (!pathBlocked)
                    {
                        MoveToward(transform.position + away * 10f, _sprintSpeed);
                        _explosiveFleeTimer = 0.5f;
                    }
                }
            }

            if (_explosiveFleeTimer > 0f)
            {
                _explosiveFleeTimer -= Time.deltaTime;
                return; // Skip normal AI while fleeing
            }

            _logTimer += Time.deltaTime;
            if (_logTimer > 5f)
            {
                _logTimer = 0f;
                float hp = _playerHealth != null ? _playerHealth.health : 0;
                bool graphReady = NavGraph.Instance != null && NavGraph.Instance.HasData;
                float vel = _cc != null ? Mathf.Sqrt(_cc.velocity.x * _cc.velocity.x + _cc.velocity.z * _cc.velocity.z) : 0f;
                string nlTag = _nodelessLockTimer > 0f ? $" NL={_nodelessLockTimer:F1}" : "";
                Plugin.Log.LogInfo($"[{BotName}] State={State} hp={hp} weapon={(_heldWeapon != null ? _heldWeapon.name : "none")} vel={vel:F1} graph={graphReady}({NavGraph.Instance?.Nodes.Count ?? 0}n) grounded={(_cc != null && _cc.isGrounded)} stuck={_stuckTimer:F1}{nlTag} pos={transform.position} path={_graphPath.Count}");
            }

            // Mode selection
            bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;

            // Training None = freeze bots in place.
            // EXCEPTION: if the graph is empty, auto-kickstart — bots explore anyway so
            // a fresh map gets trained by bots alone without requiring the user to flip a toggle.
            if (trainingMode && Plugin.IsTrainingNone)
            {
                bool graphEmpty = NavGraph.Instance == null || NavGraph.Instance.NodeCount < 5;
                if (!graphEmpty)
                {
                    _currentHorizInput = 0f;
                    if (_cc != null && _cc.isGrounded) _verticalVelocity = -1f;
                    return;
                }
                // Fall through — graph is empty, let bot wander to seed initial data.
            }

            if (trainingMode)
            {
                Wander();
            }
            else
            {
                // Opportunistic behaviors — run BEFORE state dispatch
                HandleOpportunistic();

                switch (State)
                {
                    case BotState.FindWeapon: HandleFindWeapon(); break;
                    case BotState.GoToWeapon: HandleGoToWeapon(); break;
                    case BotState.PickUpWeapon: HandlePickUpWeapon(); break;
                    case BotState.Hunt: HandleHunt(); break;
                }
            }

            // Apply gravity for frames where no movement method ran its own cc.Move
            // This prevents floating in non-moving states without double-applying gravity
            if (!_movedThisFrame && _cc != null && _cc.enabled)
                _cc.Move(new Vector3(0, _verticalVelocity * Time.deltaTime, 0));

            CheckStuck();

            // Periodic graph maintenance — only first bot runs it
            if (NavGraph.Instance != null && BotId == 0)
                NavGraph.Instance.PeriodicMaintenance();

            // Record bot movement into NavGraph
            if (_cc != null)
            {
                bool grounded = _cc.isGrounded;

                // Detect unintentional falls while following a path — upgrade edge to Jump
                if (_wasGroundedLastFrame && !grounded && !_justJumped && !_onLadder
                    && _intentionalJumpTimer <= 0f && _lastReachedNode != null
                    && NavGraph.Instance != null && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                {
                    NavGraph.Instance.ReportFallOnEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id, BotId);
                }
                _wasGroundedLastFrame = grounded;

                PlayerRecorder.RecordBot(transform.position, grounded, _onLadder, BotId,
                    _justJumped, _lastGroundedPos, _isSliding);
                _justJumped = false;
                if (grounded) _lastGroundedPos = transform.position;
            }
        }

        // ===================== OVERHEAD SLIDE =====================

        private float _slideStartTime; // When the current slide started — for hard timeout

        private void UpdateOverheadSlide()
        {
            // Extend slide under ceiling — keep sliding if still under low ceiling
            if (_isSliding)
            {
                Vector3 headTop = transform.position + Vector3.up * 1.8f;
                bool ceilingAbove = Physics.Raycast(headTop, Vector3.up, 0.3f, WALL_MASK, QueryTriggerInteraction.Ignore);

                if (Time.time - _slideStartTime > 3f)
                {
                    if (ceilingAbove)
                    {
                        // Still under low ceiling — restart slide instead of standing up into it
                        _slideStartTime = Time.time;
                        _slideTimer = 1.5f;
                        _slideResetTimer = 0f; // Allow immediate re-slide
                    }
                    else
                    {
                        // Clear above — end slide
                        EndSlide();
                    }
                    return;
                }

                if (ceilingAbove && _slideTimer < 0.3f)
                    _slideTimer = 0.3f;
            }
        }

        // ===================== SLIDING =====================

        private bool _wasSliding; // Track slide state transitions for start time

        private void UpdateSlide()
        {
            _slideTimer -= Time.deltaTime;
            _slideResetTimer -= Time.deltaTime;

            // Track when slide starts for hard timeout
            if (_isSliding && !_wasSliding)
                _slideStartTime = Time.time;
            _wasSliding = _isSliding;

            if (!_isSliding)
            {
                // Only slide when needed (overhead obstacle or melee rush) — no random slides
            }
            else
            {
                if (_slideTimer <= 0f)
                {
                    // Check if there's room to stand — above AND ahead at head height
                    bool ceilingBlocked = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up, 1.5f, WALL_MASK, QueryTriggerInteraction.Ignore);
                    bool headAheadBlocked = Physics.Raycast(transform.position + Vector3.up * 1.6f, transform.forward, 1f, WALL_MASK, QueryTriggerInteraction.Ignore);
                    bool canStand = !ceilingBlocked && !headAheadBlocked;

                    if (canStand)
                    {
                        EndSlide();
                    }
                    else
                    {
                        // Can't stand yet — keep sliding, reset start time so hard timeout doesn't kill it
                        _slideTimer = 0.5f;
                        _slideStartTime = Time.time; // Reset hard timeout — still need to slide
                        _slideResetTimer = 0f; // Allow immediate re-slide after this one
                    }
                }
            }
        }

        // ===================== ANIMATOR =====================

        private float _animMoveSpeed; // Smoothed for blend tree
        private float _lastGrounded;
        private Vector3 _lastAnimPos; // Track position for speed calculation

        private void UpdateAnimator()
        {
            // Calculate actual horizontal speed from position delta
            // CC.velocity is unreliable with CharacterController.Move()
            Vector3 posDelta = transform.position - _lastAnimPos;
            _lastAnimPos = transform.position;
            posDelta.y = 0f;
            float horizSpeed = posDelta.magnitude / Mathf.Max(Time.deltaTime, 0.001f);

            // Consider bot "moving" if actually displacing OR actively trying to move
            // (strafing/leaning may have small displacement but legs should still animate)
            bool isMoving = horizSpeed > 0.3f || _currentHorizInput > 0.5f;

            float targetMoveSpeed;
            if (_isSliding)
                targetMoveSpeed = 0f; // Slide animation handles this
            else if (horizSpeed > 5f)
                targetMoveSpeed = 1f;  // Sprint
            else if (isMoving)
                targetMoveSpeed = 0.5f; // Walk
            else
                targetMoveSpeed = 0f;  // Idle

            _animMoveSpeed = Mathf.Lerp(_animMoveSpeed, targetMoveSpeed, 10f * Time.deltaTime);

            // Grounded check for jump trigger only
            bool grounded;
            if (_cc != null)
            {
                grounded = _cc.isGrounded || Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, 0.3f,
                    GROUND_MASK, QueryTriggerInteraction.Ignore);
            }
            else grounded = true;

            // Jump trigger — fire once when leaving ground
            if (_lastGrounded > 0f && !grounded && _verticalVelocity > 0f)
            {
                if (_bodyAnimator != null) try { _bodyAnimator.SetTrigger("Jump"); } catch { }
                if (_globalAnimator != null) try { _globalAnimator.SetTrigger("Jump"); } catch { }
            }
            _lastGrounded = grounded ? 1f : 0f;

            // Vertical aim (pitch toward target)
            // Game uses: -((rotationX) / 90) where rotationX is negative when looking up
            // So looking up = positive Vertical, looking down = negative Vertical
            float vertical = 0f;
            if (_playerTarget != null)
            {
                Vector3 toTarget = (_playerTarget.position + Vector3.up) - (transform.position + Vector3.up * 1.5f);
                float pitch = Mathf.Asin(Mathf.Clamp(toTarget.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                vertical = pitch / 90f; // Positive = looking up, negative = looking down
            }

            // Safety: force end slide/crouch if stuck
            if (_isSliding)
            {
                // Hard timeout or stuck = force end slide
                if (Time.time - _slideStartTime > 3f || _stuckTimer > 1.5f)
                {
                    EndSlide();
                }
            }
            else if (_isCrouching)
            {
                // Force uncrouch: not sliding, crouch timer expired or CC height wrong
                if (_crouchTimer <= 0f || (_cc != null && _cc.height < 1.5f && _stuckTimer > 1f))
                {
                    _isCrouching = false;
                    _crouchTimer = 0f;
                    if (_cc != null) { _cc.height = STAND_HEIGHT; _cc.center = new Vector3(0, STAND_CENTER_Y, 0); }
                    if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", false);
                    if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", false);
                }
            }

            bool hasWeapon = _heldWeapon != null;
            bool twoHanded = hasWeapon && _heldWeapon.requireBothHands;
            bool oneHanded = hasWeapon && !twoHanded;

            // crouchMove — movement magnitude while crouching (used by crouch blend tree)
            float crouchMove = (_isCrouching && isMoving) ? 1f : 0f;

            if (_bodyAnimator != null)
            {
                TrySet(_bodyAnimator, "MovementSpeed", _animMoveSpeed);
                TrySet(_bodyAnimator, "Grounded", grounded);
                TrySet(_bodyAnimator, "Vertical", vertical);
                TrySet(_bodyAnimator, "Crouch", _isCrouching);
                TrySet(_bodyAnimator, "crouchMove", crouchMove);
                TrySet(_bodyAnimator, "Slide", _isSliding);
                TrySet(_bodyAnimator, "RightHanded", oneHanded);
                TrySet(_bodyAnimator, "TwoHanded", twoHanded);
                TrySet(_bodyAnimator, "DoubleHanded", false);
            }

            if (_globalAnimator != null)
            {
                TrySet(_globalAnimator, "MovementSpeed", _animMoveSpeed);
                TrySet(_globalAnimator, "Grounded", grounded);
                TrySet(_globalAnimator, "Vertical", vertical);
                TrySet(_globalAnimator, "Crouch", _isCrouching);
                TrySet(_globalAnimator, "crouchMove", crouchMove);
                TrySet(_globalAnimator, "Slide", _isSliding);
                TrySet(_globalAnimator, "TwoHanded", twoHanded);
                TrySet(_globalAnimator, "SingleHanded", oneHanded);
                TrySet(_globalAnimator, "DoubleSingle", false);
                TrySet(_globalAnimator, "LeftHanded", false);
            }
        }

        private float _footstepTimer;
        private AudioClip[] _footstepClips;
        private bool _footstepClipsLoaded;
        private Vector3 _lastFootstepPos;

        private void LoadFootstepClips()
        {
            if (_footstepClipsLoaded) return;
            _footstepClipsLoaded = true;
            try
            {
                var field = typeof(FirstPersonController).GetField("concreteClips",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) { Plugin.Log.LogWarning($"[{BotName}] concreteClips field not found"); return; }

                // Try our own FPC first
                if (_fpc != null)
                {
                    _footstepClips = field.GetValue(_fpc) as AudioClip[];
                    if (_footstepClips != null && _footstepClips.Length > 0)
                    {
                        Plugin.Log.LogInfo($"[{BotName}] Loaded {_footstepClips.Length} footstep clips from own FPC");
                        return;
                    }
                }

                // Fallback: find clips from ANY FPC in the scene (real player's)
                foreach (var fpc in Object.FindObjectsOfType<FirstPersonController>(true))
                {
                    var clips = field.GetValue(fpc) as AudioClip[];
                    if (clips != null && clips.Length > 0)
                    {
                        _footstepClips = clips;
                        Plugin.Log.LogInfo($"[{BotName}] Loaded {clips.Length} footstep clips from scene FPC");
                        return;
                    }
                }
                Plugin.Log.LogWarning($"[{BotName}] No footstep clips found anywhere");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[{BotName}] Footstep load error: {e.Message}"); }
        }

        private void UpdateFootsteps()
        {
            if (_cc == null || !_cc.isGrounded || _botAudio == null) return;

            Vector3 delta = transform.position - _lastFootstepPos;
            delta.y = 0;
            _lastFootstepPos = transform.position;
            float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 0.001f);
            if (speed < 1f) return;

            float interval = speed > 9.5f ? 0.28f : 0.4f;
            _footstepTimer -= Time.deltaTime;
            if (_footstepTimer > 0f) return;
            _footstepTimer = interval;

            LoadFootstepClips();
            if (_footstepClips == null || _footstepClips.Length == 0) return;

            AudioClip clip = _footstepClips[Random.Range(0, _footstepClips.Length)];
            _botAudio.PlayOneShot(clip, speed > 9.5f ? 0.7f : 0.4f);
        }

        private void TrySet(Animator a, string p, float v)
        { try { a.SetFloat(p, v); } catch { } }
        private void TrySet(Animator a, string p, bool v)
        { try { a.SetBool(p, v); } catch { } }

        // >>> Weapon state machine methods moved to BotController.Weapons.cs

        // >>> Combat methods moved to BotController.Combat.cs

        // ===================== DEATH =====================

        public void Die(Transform killer)
        {
            if (IsDead) return;
            IsDead = true;
            State = BotState.Dead;
            _playerTarget = null;

            Plugin.Log.LogInfo($"[{BotName}] Died! Killer: {(killer != null ? killer.name : "unknown")}");

            // Report death to PlayerRecorder for fall-death tracking
            PlayerRecorder.ReportDeath(BotId, transform.position);

            // Report environmental death to NavGraph (null killer = environment, self-kill = environment)
            if (NavGraph.Instance != null)
            {
                bool isEnvironmental = killer == null || killer.root == transform;
                // Don't penalize nav edges when bot was in combat — strafing off edges
                // or explosive knockback deaths shouldn't degrade navigation data
                bool wasInCombat = State == BotState.Hunt && _playerTarget != null;
                if (isEnvironmental && !wasInCombat)
                {
                    NavGraph.Instance.ReportEnvironmentalDeath(transform.position, _lastGroundedPos);

                    // Report fall death — penalize Fall/Walk edges near the takeoff point
                    float fallHeight = _lastGroundedPos.y - transform.position.y;
                    if (fallHeight > 2f && _lastGroundedPos != Vector3.zero)
                        NavGraph.Instance.ReportFallDeath(_lastGroundedPos, transform.position);

                    // Report the ACTUAL path edge that led to death — always, not just during jumps
                    // This penalizes the specific edge the bot was following when it died
                    if (_lastReachedNode != null && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
                    {
                        NavGraph.Instance.ReportFallOnEdge(_lastReachedNode.Id, _graphPath[_graphPathIndex].Id, BotId);
                        Plugin.Log.LogInfo($"[{BotName}] Death: penalized edge {_lastReachedNode.Id}->{_graphPath[_graphPathIndex].Id}");
                    }

                    // DON'T create new jump edges on death — they often point into void.
                    // Let the bot learn through exploration and successful traversals instead.

                    Plugin.Log.LogInfo($"[{BotName}] Environmental death reported to NavGraph at {transform.position}");
                }
            }

            // Self-kill message only — all other kill paths (RegisterKill, Explosion_Postfix,
            // MeleeHitServer_Prefix, KillServer_Prefix) write their own feed entries.
            if (killer != null)
            {
                try
                {
                    bool isSelfKill = killer.root == transform;
                    if (isSelfKill)
                    {
                        string weaponName = _heldBehaviour != null ? _heldBehaviour.weaponName : "explosive";
                        BotKillFeed.Write(_playerHealth, gameObject, BotName, weaponName, "killed", true);
                    }
                }
                catch { }
            }

            // Don't disable component — coroutines need it active. IsDead blocks all AI logic.
            // Delayed graphics hide — lets ExplodeServer read bone positions for ragdoll
            StartCoroutine(HideGraphicsDelayed());

            // Training mode: auto-respawn after short delay to keep exploring
            if (NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training)
            {
                StartCoroutine(TrainingRespawnDelayed());
            }

            // Despawn weapon (destroy it, don't drop for others)
            if (_heldWeaponObj != null)
            {
                try
                {
                    _heldWeaponObj.transform.SetParent(null);
                    var nob = _heldWeaponObj.GetComponent<FishNet.Object.NetworkObject>();
                    if (nob != null && nob.IsSpawned)
                        FishNet.InstanceFinder.ServerManager.Despawn(nob);
                    else
                        Object.Destroy(_heldWeaponObj);
                }
                catch { try { Object.Destroy(_heldWeaponObj); } catch { } }
                _heldWeaponObj = null;
                _heldWeapon = null;
                _heldBehaviour = null;
            }

            // Destroy bot camera
            if (_botCam != null)
            {
                Object.Destroy(_botCam.gameObject);
                _botCam = null;
            }

            // Always try ragdoll — game's Explode() may have NRE'd on bot data
            if (_playerHealth != null)
            {
                try
                {
                    Vector3 ejectDir = killer != null ? (transform.position - killer.position).normalized : -transform.forward;
                    _playerHealth.ExplodeServer(false, true, "Torso", ejectDir, 30f, transform.position);
                }
                catch { }
            }

            // Full physics/visual disable (stops animators, hides model, disables CC/colliders)
            DisableBotPhysics(gameObject);

            // PlayerDied removes from alivePlayers AND triggers round-end check
            if (GameManager.Instance != null)
            {
                Plugin.Log.LogInfo($"[{BotName}] Calling PlayerDied({PlayerId}), alivePlayers before: [{string.Join(",", GameManager.Instance.alivePlayers)}]");
                try { GameManager.Instance.PlayerDied(PlayerId); }
                catch { GameManager.Instance.alivePlayers.Remove(PlayerId); }
                Plugin.Log.LogInfo($"[{BotName}] alivePlayers after: [{string.Join(",", GameManager.Instance.alivePlayers)}]");
            }

            // Sync death to non-host clients via Mycelium — they need to hide the bot model + spawn ragdoll
            try
            {
                string killerName = BotName; // default self-kill
                if (killer != null)
                {
                    var kb = killer.GetComponent<BotController>();
                    if (kb == null) kb = killer.GetComponentInParent<BotController>();
                    if (kb != null) killerName = kb.BotName;
                    else if (killer.GetComponent<PlayerValues>()?.playerClient != null)
                        killerName = killer.GetComponent<PlayerValues>().playerClient.PlayerNameTag;
                }
                string weaponName = _heldBehaviour != null ? _heldBehaviour.weaponName : "weapon";
                Vector3 ejectDir = killer != null ? (transform.position - killer.position).normalized : -transform.forward;
                // Use NetworkObject ID — PlayerId-based lookup fails on non-host (playerClient is null)
                int netId = -1;
                var nob = GetComponent<FishNet.Object.NetworkObject>();
                if (nob != null) netId = (int)nob.ObjectId;
                BotDamageSync.SyncKill(netId, killerName, weaponName, false,
                    ejectDir, 30f, transform.position, "Torso");
            }
            catch { }
        }

        // Called by BotManager on round reset only
        public void Respawn(Vector3 position)
        {
            // Check if spawn position is inside a wall — nudge out if so
            Vector3 safePos = FindSafeSpawnPosition(position);
            transform.position = safePos;
            IsDead = false;
            _frozen = true;
            State = BotState.FindWeapon;
            _playerTarget = null;
            _weaponTarget = null;
            _isShooting = false;
            _blacklistedWeapons.Clear();
            _placedClaymorePositions.Clear();
            EndSlide();
            _slideTimer = Random.Range(4f, 8f); // Random delay before first slide
            _slideResetTimer = 0f;
            _weaponPursuitTimer = 0f;
            _currentHorizInput = 0f;
            _isLeaning = false;
            _leanDir = 0f;
            _isDodging = false;
            _isCrouching = false;
            _crouchTimer = Random.Range(3f, 6f);
            _coyoteTimer = 0f;
            _strafeDir = Random.value > 0.5f ? 1 : -1;
            _strafeSwitchTimer = Random.Range(1f, 2.5f);
            _dodgeTimer = 0f;
            _smoothedStrafeDir = Vector3.zero;
            _smoothedApproach = 0.3f;
            _huntSubState = 0f;
            _huntSubStateHold = 0f;

            // Reset movement/pathfinding state from previous life
            _stuckTimer = 0f;
            _didStuckNudge = false;
            _didStuckRepath = false;
            _stuckCheckPos = safePos;
            _graphPath.Clear();
            _graphPathIndex = 0;
            _lastReachedNode = null;
            _prevReachedNode = null;
            _lastPathTarget = Vector3.zero;
            _repathTimer = 0f;
            _lastGroundedPos = safePos;
            _lastMoveDir = Vector3.zero;
            _lastAnimPos = safePos;
            _justJumped = false;
            _jumpDir = Vector3.zero;
            _landingFollowTimer = 0f;
            _commitDir = Vector3.zero;
            _commitTimer = 0f;
            _wallHitTimer = 0f;
            _wallRepathCount = 0;
            _combatStaleTimer = 0f;
            _lastHitTime = 0f;
            _lastNodeRepeatedId = -1;
            _nodeRepeatCount = 0;
            _recentNodeCount = 0;
            _nodelessLockTimer = 0f;
            _nodelessBounceCount = 0;
            _recentNodeIdx = 0;
            _wallJumpCount = 0;
            _canWallJump = false;
            _wasGroundedLastFrame = true;
            _verticalVelocity = 0f;
            _intentionalJumpTimer = 0f;
            _trajActive = false;
            _trajIndex = 0;
            _currentJumpEdge = null;
            _activeJumpReason = JumpReason.None;
            _vaultKillTimer = 0f;
            _equipTimer = 0f;
            _hasWanderTarget = false;
            _wanderTarget = Vector3.zero;
            _wanderChangeTimer = 0f;
            _explosiveFleeTimer = 0f;
            _explosiveCheckTimer = 0f;
            _zoneForce = Vector3.zero;
            _zoneForceDuration = 0f;
            _zoneLaunchInAir = false;
            _gravityZoneMultiplier = 1f;
            _activeImpulseZones.Clear();
            _activeGravityZones.Clear();
            _activeForceZones.Clear();

            _onLadder = false;
            _wasOnLadder = false;
            _ladderDismountTimer = 0f;
            _ladderStuckTimer = 0f;
            _nearLadder = false;
            _searchTimer = 0f;
            _targetItem = null;
            _loggedError = false;

            // Re-enable component and physics (disabled in Die())
            enabled = true;
            if (_cc != null)
            {
                _cc.enabled = true;
                _cc.height = STAND_HEIGHT;
                _cc.center = new Vector3(0, STAND_CENTER_Y, 0);
            }

            // Re-enable all child colliders (disabled by DisableBotPhysics)
            foreach (var col in GetComponentsInChildren<Collider>(true))
                col.enabled = true;

            // Recreate bot camera (destroyed in Die())
            if (_botCam == null)
            {
                GameObject camObj = new GameObject("BotAimCam");
                camObj.transform.SetParent(transform);
                camObj.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                _botCam = camObj.AddComponent<Camera>();
                _botCam.enabled = false;
            }

            // Reset all PlayerHealth fields
            if (_playerHealth != null)
            {
                _playerHealth.health = _playerHealth.fullHealth;
                _playerHealth.isKilled = false;
                _playerHealth.isShot = false;
                _playerHealth.killer = null;
                _playerHealth.suicide = false;
                _playerHealth.fellVoid = false;
                _playerHealth.shouldDropWeapon = false;
                _playerHealth.shouldBounce = false;
                if (_playerHealth.graphics != null)
                    _playerHealth.graphics.SetActive(true);
            }

            SetVisible(true);

            if (GameManager.Instance != null)
                GameManager.Instance.alivePlayers.Add(PlayerId);

            Plugin.Log.LogInfo($"[{BotName}] Respawned at {position}");
        }

        /// <summary>
        /// Check if a position is inside geometry and find a safe nearby spot.
        /// </summary>
        private Vector3 FindSafeSpawnPosition(Vector3 pos)
        {
            // Snap to ground first — prevent floating or feet-in-ground
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit groundHit, 5f,
                GROUND_MASK, QueryTriggerInteraction.Ignore))
            {
                pos = new Vector3(pos.x, groundHit.point.y + 0.15f, pos.z);
            }

            // Check if overlapping any colliders at the spawn point
            int wallMask = WALL_MASK;
            Collider[] overlaps = Physics.OverlapCapsule(
                pos + Vector3.up * 0.5f, pos + Vector3.up * 1.8f, 0.4f, wallMask, QueryTriggerInteraction.Ignore);

            if (overlaps.Length == 0) return pos; // Clear spawn

            // Stuck in wall — try 8 directions at increasing distances
            Vector3[] dirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.right).normalized, (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized, (Vector3.back + Vector3.left).normalized };

            for (float dist = 1f; dist <= 4f; dist += 1f)
            {
                foreach (var dir in dirs)
                {
                    Vector3 test = pos + dir * dist;
                    var testOverlaps = Physics.OverlapCapsule(
                        test + Vector3.up * 0.5f, test + Vector3.up * 1.8f, 0.4f, wallMask, QueryTriggerInteraction.Ignore);
                    if (testOverlaps.Length == 0)
                    {
                        // Verify ground exists
                        if (Physics.Raycast(test + Vector3.up * 0.5f, Vector3.down, 3f))
                        {
                            Plugin.Log.LogInfo($"[{BotName}] Spawn in wall at {pos}, nudged to {test}");
                            return test;
                        }
                    }
                }
            }

            Plugin.Log.LogWarning($"[{BotName}] Could not find safe spawn near {pos}, using original");
            return pos + Vector3.up * 2f; // Last resort: push up
        }

        private IEnumerator TrainingRespawnDelayed()
        {
            yield return new WaitForSeconds(2f);

            // Pick a spawn point, prefer one near player-sourced nodes for path learning
            SpawnPoint[] spawns = GetCachedSpawns();
            if (spawns.Length == 0) yield break;

            SpawnPoint best = spawns[Random.Range(0, spawns.Length)];
            if (NavGraph.Instance != null)
            {
                // Try to spawn near a player-sourced node to continue following player paths
                float bestScore = float.MinValue;
                foreach (var sp in spawns)
                {
                    var nearNode = NavGraph.Instance.FindNearestPlayerNode(sp.transform.position, 15f);
                    float score = Random.Range(0f, 5f); // Base randomness
                    if (nearNode != null)
                        score += 10f; // Strong preference for spawns near player paths
                    score += Vector3.Distance(transform.position, sp.transform.position) * 0.1f; // Spread out
                    if (score > bestScore) { bestScore = score; best = sp; }
                }
            }

            Respawn(best.transform.position + Vector3.up * 1.5f);
            Plugin.Log.LogInfo($"[{BotName}] Training auto-respawn at {best.transform.position}");
        }

        // >>> DropWeapon/DestroyHeldWeapon moved to BotController.Weapons.cs

        // >>> Movement methods moved to BotController.Movement.cs
        // >>> Mode methods moved to BotController.Modes.cs

        private float _ladderNearCheckTimer;
        // >>> Ladder/Jump/Gravity/Look methods moved to BotController.Movement.cs
        // ===================== DETECTION =====================

        private ItemBehaviour FindNearestWeapon()
        {
            ItemBehaviour[] items = GetCachedItems();
            ItemBehaviour closest = null;

            Vector3 myPos = transform.position;
            float closestSqr = float.MaxValue;

            // Refresh weapon validity cache every 2s
            bool refreshCache = Time.time - _weaponValidCacheTime > 2f;
            if (refreshCache) _weaponValidCacheTime = Time.time;

            // If the bot already holds a propeller, skip other propellers — we need a
            // REAL combat weapon to replace the propeller, not another one of the same.
            bool haveAnyPropeller = _cachedIsPropeller;

            foreach (var item in items)
            {
                if (item == null || item.isTaken) continue;
                if (item.rootObject != null || item.gameObject.layer != 7) continue;

                // Skip propellers when we already have one
                if (haveAnyPropeller && item.GetComponent<Propeller>() != null) continue;

                // Skip blacklisted weapons
                if (NavGraph.Instance != null)
                {
                    var nearNode = NavGraph.Instance.FindNearestNode(item.transform.position, 3f);
                    if (nearNode != null && Plugin.BlacklistedWeaponNodes.Contains(nearNode.Id)) continue;
                }

                float dx = item.transform.position.x - myPos.x;
                float dz = item.transform.position.z - myPos.z;
                float sqrDist = dx * dx + dz * dz;
                if (sqrDist >= closestSqr) continue;

                // Check weapon validity via cache (avoids 4x GetComponent per item)
                int iid = item.GetInstanceID();
                if (!refreshCache && _weaponValidCache.TryGetValue(iid, out bool cached))
                {
                    if (!cached) continue;
                }
                else
                {
                    Weapon w = item.GetComponent<Weapon>();
                    bool valid = w != null && (!w.needsAmmo || w.currentAmmo > 0)
                        && item.GetComponent<Taser>() == null
                        && item.GetComponent<FlashLight>() == null;
                    _weaponValidCache[iid] = valid;
                    if (!valid) continue;
                }

                closestSqr = sqrDist;
                closest = item;
            }
            return closest;
        }

        private float _targetDebugTimer;
        private Transform FindNearestPlayer()
        {
            bool humansAlive = !AllHumansDead();
            Transform closest = null;
            float closestDist = float.MaxValue;

            // Search ALL active PlayerHealth in scene — includes host AND non-host spawned characters
            PlayerHealth[] allPlayers = GetCachedPlayers();

            // Debug: log player search periodically
            _targetDebugTimer += Time.deltaTime;
            if (_targetDebugTimer > 10f)
            {
                _targetDebugTimer = 0f;
                int total = allPlayers.Length;
                int valid = 0;
                foreach (var p in allPlayers)
                {
                    if (p != null && p.gameObject.activeInHierarchy && !p.isKilled && p.health > 0f && p.gameObject != gameObject)
                        valid++;
                }
                Plugin.Log.LogInfo($"[{BotName}] FindNearestPlayer: {total} PlayerHealth found, {valid} valid targets, humansAlive={humansAlive}");
            }

            foreach (var ph in allPlayers)
            {
                if (ph == null || ph.gameObject == null) continue;
                if (!ph.gameObject.activeInHierarchy) continue;
                if (ph.gameObject == gameObject) continue;
                if (ph.isKilled || ph.health <= 0f) continue;

                bool isOtherBot = IsBot(ph);

                // Targeting priority: always prioritize humans — only target other bots when all humans dead.
                // Removed BotsTargetAll toggle; this is the sensible default.
                if (humansAlive && isOtherBot) continue;
                if (!humansAlive && !isOtherBot) continue;
                BotController otherBot = isOtherBot ? ph.GetComponent<BotController>() : null;
                if (otherBot != null && otherBot.IsDead) continue;

                float dist = HorizontalDist(transform.position, ph.transform.position);
                if (dist < _detectionRange && dist < closestDist)
                {
                    closestDist = dist;
                    closest = ph.transform;
                }
            }
            return closest;
        }

        private bool AllHumansDead()
        {
            _allHumansDeadTimer -= Time.deltaTime;
            if (_allHumansDeadTimer > 0f) return _cachedAllHumansDead;
            _allHumansDeadTimer = 0.5f;

            _cachedAllHumansDead = true;
            foreach (var ph in GetCachedPlayers())
            {
                if (ph == null || !ph.gameObject.activeInHierarchy) continue;
                if (IsBot(ph)) continue;
                if (!ph.isKilled && ph.health > 0f) { _cachedAllHumansDead = false; break; }
            }
            return _cachedAllHumansDead;
        }

        private bool _cachedAnyHumanCanMove;
        private float _anyHumanCanMoveTimer;

        private bool AnyHumanCanMove()
        {
            _anyHumanCanMoveTimer -= Time.deltaTime;
            if (_anyHumanCanMoveTimer > 0f) return _cachedAnyHumanCanMove;
            _anyHumanCanMoveTimer = 0.5f;

            _cachedAnyHumanCanMove = false;
            foreach (var ph in GetCachedPlayers())
            {
                if (ph == null || IsBot(ph)) continue;
                var fpc = ph.GetComponent<FirstPersonController>();
                if (fpc != null && fpc.canMove) { _cachedAnyHumanCanMove = true; break; }
            }
            return _cachedAnyHumanCanMove;
        }

        // ===================== MULTI-LEVEL NAVIGATION =====================

        // _levelPathTarget/_levelPathTimer removed — graph handles multi-level navigation

        /// <summary>
        // ===================== UTILITY =====================

        /// <summary>
        /// Disable all physics on a bot before spawning ragdoll to prevent collisions.
        /// </summary>
        public static void DisableBotPhysicsPublic(GameObject botObj) => DisableBotPhysics(botObj);
        private static void DisableBotPhysics(GameObject botObj)
        {
            // Disable CharacterController
            var cc = botObj.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            // Disable all colliders
            foreach (var col in botObj.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            // NOTE: Do NOT hide graphics or stop animators here.
            // ExplodeServer needs bone positions from the model to spawn ragdoll.
            // Graphics are hidden by HideGraphicsDelayed() after a short delay.
        }

        /// <summary>
        /// Hide the bot model after a delay so ExplodeServer has time to read bone positions for ragdoll.
        /// </summary>
        private System.Collections.IEnumerator HideGraphicsDelayed()
        {
            yield return null; // Wait 1 frame
            yield return null; // Wait another frame for ragdoll to read bones

            // Stop ALL animators including NetworkAnimator
            foreach (var anim in GetComponentsInChildren<Animator>(true))
                anim.enabled = false;
            foreach (var netAnim in GetComponentsInChildren<FishNet.Component.Animating.NetworkAnimator>(true))
                netAnim.enabled = false;

            // Hide all visuals
            if (_playerHealth != null && _playerHealth.graphics != null)
                _playerHealth.graphics.SetActive(false);

            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                r.enabled = false;
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
                r.enabled = false;
        }

        private void SetVisible(bool visible)
        {
            // Only toggle SkinnedMeshRenderers (character model), not MeshRenderers (collision shapes)
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                r.enabled = visible;
            if (_playerHealth != null && _playerHealth.graphics != null)
                _playerHealth.graphics.SetActive(visible);
            if (_cc != null) _cc.enabled = visible;
        }

        private float _stuckCheckInterval = 0.5f;
        private float _stuckCheckTimer;
        private Vector3 _stuckCheckPos;
        private bool _wasGroundedLastFrame = true;
        // Simple stuck system: detect -> nudge once -> repath -> give up
        private bool _didStuckNudge;             // True after one jump/slide nudge this stuck episode
        private bool _didStuckRepath;            // True after one full repath this stuck episode

        /// <summary>
        /// Simple stuck detection: moved < 0.5m in 0.5s while trying to move -> accumulate _stuckTimer.
        /// Stage 1 (0.8s): one jump/slide nudge to clear small geometry snags.
        /// Stage 2 (2.0s): full A* repath to current target; fallback to distant spawn if no path.
        /// Stage 3 (4.0s): give up, swap target entirely.
        /// Progress (moved >= 0.5m) decays the timer and clears both flags once below 0.1s.
        /// </summary>
        private void CheckStuck()
        {
            if (State == BotState.Dead) return;
            if (_onLadder || _ladderDismountTimer > 0f) return;
            if (_zoneForceDuration > 0f) return;

            _stuckCheckTimer -= Time.deltaTime;
            if (_stuckCheckTimer > 0f) return;
            _stuckCheckTimer = _stuckCheckInterval;

            float movedSqr = HorizontalDistSqr(transform.position, _stuckCheckPos);
            _stuckCheckPos = transform.position;

            bool tryingToMove = State == BotState.GoToWeapon || State == BotState.Hunt ||
                                State == BotState.FindWeapon || _hasWanderTarget;

            if (movedSqr < 0.25f && tryingToMove) // 0.25 = 0.5^2
            {
                _stuckTimer += _stuckCheckInterval;
                Vector3 moveDir = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;

                // ---- Stage 1 (0.8s): one jump/slide nudge ----
                if (_stuckTimer >= 0.8f && !_didStuckNudge && _cc != null && _cc.isGrounded)
                {
                    _didStuckNudge = true;
                    var obs = CheckObstructions(moveDir);
                    if (obs.CrouchClear && (obs.FeetBlocked || obs.HeadBlocked) && !_isSliding)
                        InitSlide(moveDir, duration: 1.0f);
                    else
                        TryJump(JumpReason.StuckRecovery, moveDir);
                }

                // ---- Stage 2 (2.0s): full repath ----
                if (_stuckTimer >= 2f && !_didStuckRepath)
                {
                    _didStuckRepath = true;
                    if (NavGraph.Instance != null)
                    {
                        Vector3 target = _wanderTarget;
                        if (_playerTarget != null) target = _playerTarget.position;
                        else if (_weaponTarget != null) target = _weaponTarget.position;

                        var path = NavGraph.Instance.FindPath(transform.position, target, searchRadius: 40f);
                        if (path.Count > 0)
                        {
                            _graphPath = path;
                            _graphPathIndex = 0;
                            _lastReachedNode = null;
                            _prevReachedNode = null;
                            _repathTimer = 0f;
                            Plugin.Log.LogInfo($"[{BotName}] Stuck -> repath ({path.Count} nodes)");
                        }
                        else
                        {
                            // No path available -> engage nodeless mode and pursue the target directly.
                            // Previously we headed to a distant spawn via the same (broken) graph,
                            // which kept the bot bouncing. Now we actually go straight at the target.
                            NavGraph.Instance.ReportStuck(transform.position, moveDir);
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                            _lastBounceTime = Time.time;
                            _nodelessLockTimer = Mathf.Min(14f, 4f + 2f * _nodelessBounceCount);
                            // If we have an enemy/weapon target, chase it directly. Otherwise pick a
                            // distant spawn as the nodeless destination.
                            if (_playerTarget == null && _weaponTarget == null)
                            {
                                _wanderTarget = PickDistantSpawn();
                                _hasWanderTarget = true;
                            }
                            Plugin.Log.LogInfo($"[{BotName}] Stuck -> no path, nodeless lock {_nodelessLockTimer:F1}s");
                        }
                    }
                }

                // ---- Stage 3 (4.0s): give up, swap target ----
                if (_stuckTimer >= 4f)
                {
                    _stuckTimer = 0f;
                    _didStuckNudge = false;
                    _didStuckRepath = false;

                    if (NavGraph.Instance != null)
                        NavGraph.Instance.ReportStuck(transform.position, moveDir);

                    _weaponTarget = null;
                    _targetItem = null;
                    _playerTarget = null;
                    _wanderTarget = PickDistantSpawn();
                    _hasWanderTarget = true;
                    _graphPath.Clear();
                    _graphPathIndex = 0;
                    _repathTimer = 0f;
                    _avoidDir = -_avoidDir;
                    State = _heldWeapon != null ? BotState.Hunt : BotState.FindWeapon;

                    // Force nodeless for the new target — a full-on 4s stuck means the local
                    // graph is unusable for this bot right now. Give it a clean nodeless window
                    // to actually escape the tangle before we trust the graph again.
                    _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                    _lastBounceTime = Time.time;
                    _nodelessLockTimer = Mathf.Max(_nodelessLockTimer, 6f);

                    Plugin.Log.LogInfo($"[{BotName}] Stuck -> giving up, nodeless lock {_nodelessLockTimer:F1}s");
                }
            }
            else
            {
                // Making progress (or not trying to move) — decay timer, clear flags once safely below threshold
                _stuckTimer = Mathf.Max(0f, _stuckTimer - _stuckCheckInterval);
                if (_stuckTimer < 0.1f)
                {
                    _didStuckNudge = false;
                    _didStuckRepath = false;
                }
            }
        }

        // Old edge/hazard detection methods removed — replaced by NavGraph confidence system
        // Edge detection is now handled by IsEdgeAhead() in MoveToward
        // Hazard detection handled by OnTriggerEnter/Stay + graph death penalty

        /// <summary>Pick a random spawn point, biased toward further ones.</summary>
        private Vector3 PickDistantSpawn()
        {
            SpawnPoint[] spawns = GetCachedSpawns();
            if (spawns.Length == 0)
            {
                Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                return transform.position + randomDir * Random.Range(10f, 25f);
            }
            SpawnPoint best = null;
            float bestDist = 0f;
            for (int i = 0; i < Mathf.Min(5, spawns.Length); i++)
            {
                var sp = spawns[Random.Range(0, spawns.Length)];
                float d = HorizontalDistSqr(transform.position, sp.transform.position);
                if (d > bestDist) { bestDist = d; best = sp; }
            }
            return best != null ? best.transform.position : spawns[Random.Range(0, spawns.Length)].transform.position;
        }

        private float HorizontalDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private float HorizontalDistSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private void SetField(object obj, string fieldName, object value)
        {
            var field = GetCachedField(obj.GetType(), fieldName);
            if (field != null) field.SetValue(obj, value);
        }

        // ===================== DEBUG VISUALIZER ACCESS =====================
        // NavGraph path info
        public List<NavNode> DbgGraphPath => _graphPath;
        public int DbgGraphPathIndex => _graphPathIndex;
        public bool DbgOnLadder => _onLadder;
        public bool DbgNearLadder => _nearLadder;
        public float DbgStuckTimer => _stuckTimer;
        public bool DbgIsSliding => _isSliding;
        public bool DbgIsCrouching => _isCrouching;
        public float DbgLadderDismountTimer => _ladderDismountTimer;
        public Vector3 DbgLadderFaceDir => _ladderFaceDir;
        public Transform DbgPlayerTarget => _playerTarget;
        public Transform DbgWeaponTarget => _weaponTarget;
        public Vector3 DbgWanderTarget => _wanderTarget;
        public bool DbgHasWanderTarget => _hasWanderTarget;
        public bool DbgHasWeapon => _heldWeapon != null;
        public int DbgStuckEscalation => (_didStuckRepath ? 2 : (_didStuckNudge ? 1 : 0));

        /// <summary>
        /// Human-readable description of what the bot is currently doing.
        /// </summary>
        public string DbgActivity
        {
            get
            {
                bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
                if (trainingMode)
                {
                    if (_hasWanderTarget) return "TRAIN EXPLORE";
                    return "TRAIN IDLE";
                }
                return State.ToString();
            }
        }

        private static T FindTriggerZone<T>(Collider col) where T : Component
        {
            if (col == null) return null;
            var zone = col.GetComponent<T>();
            return zone != null ? zone : col.GetComponentInParent<T>();
        }

        private static float ReadGravityZoneMultiplier(GravityZone zone)
        {
            if (zone == null) return 1f;
            try
            {
                var field = GetCachedField(typeof(GravityZone), "gravityMultiplier");
                object value = field != null ? field.GetValue(zone) : null;
                return value is float f ? f : 1f;
            }
            catch { return 1f; }
        }

        private void HandleTriggerZoneEnter(Collider col)
        {
            var impulse = FindTriggerZone<ImpulseZone>(col);
            if (impulse != null)
            {
                EnterImpulseZone(impulse);
                return;
            }

            var forceZone = FindTriggerZone<ForceZone>(col);
            if (forceZone != null)
            {
                RegisterForceZone(forceZone);
                return;
            }

            var gravityZone = FindTriggerZone<GravityZone>(col);
            if (gravityZone != null)
                RegisterGravityZone(gravityZone, ReadGravityZoneMultiplier(gravityZone));
        }

        private void HandleTriggerZoneStay(Collider col)
        {
            // Recover missed enter callbacks. EnterImpulseZone is idempotent until exit.
            HandleTriggerZoneEnter(col);
        }

        private void HandleTriggerZoneExit(Collider col)
        {
            var impulse = FindTriggerZone<ImpulseZone>(col);
            if (impulse != null)
            {
                ExitImpulseZone(impulse);
                return;
            }

            var forceZone = FindTriggerZone<ForceZone>(col);
            if (forceZone != null)
            {
                UnregisterForceZone(forceZone);
                return;
            }

            var gravityZone = FindTriggerZone<GravityZone>(col);
            if (gravityZone != null)
                UnregisterGravityZone(gravityZone);
        }

        private void ScanTriggerZones()
        {
            if (_cc == null || !_cc.enabled || IsDead) return;

            _scannedImpulseZones.Clear();
            _scannedForceZones.Clear();
            _scannedGravityZones.Clear();

            Vector3 up = transform.up;
            Vector3 center = transform.TransformPoint(_cc.center);
            float radius = Mathf.Max(0.05f, _cc.radius + 0.08f);
            float halfHeight = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius);
            Vector3 bottom = center - up * halfHeight;
            Vector3 top = center + up * halfHeight;

            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, _zoneOverlapBuffer, -1, QueryTriggerInteraction.Collide);
            bool saturated = count >= _zoneOverlapBuffer.Length;

            for (int i = 0; i < count; i++)
            {
                Collider col = _zoneOverlapBuffer[i];
                if (col == null || !col.isTrigger) continue;

                var impulse = FindTriggerZone<ImpulseZone>(col);
                if (impulse != null)
                {
                    _scannedImpulseZones.Add(impulse);
                    EnterImpulseZone(impulse);
                    continue;
                }

                var forceZone = FindTriggerZone<ForceZone>(col);
                if (forceZone != null)
                {
                    _scannedForceZones.Add(forceZone);
                    RegisterForceZone(forceZone);
                    continue;
                }

                var gravityZone = FindTriggerZone<GravityZone>(col);
                if (gravityZone != null)
                {
                    _scannedGravityZones.Add(gravityZone);
                    RegisterGravityZone(gravityZone, ReadGravityZoneMultiplier(gravityZone));
                }
            }

            if (saturated) return;

            _impulseZoneExitBuffer.Clear();
            foreach (var impulse in _activeImpulseZones)
            {
                if (impulse == null || !_scannedImpulseZones.Contains(impulse))
                    _impulseZoneExitBuffer.Add(impulse);
            }
            for (int i = 0; i < _impulseZoneExitBuffer.Count; i++)
                _activeImpulseZones.Remove(_impulseZoneExitBuffer[i]);

            for (int i = _activeForceZones.Count - 1; i >= 0; i--)
            {
                var forceZone = _activeForceZones[i];
                if (forceZone == null || !_scannedForceZones.Contains(forceZone))
                    _activeForceZones.RemoveAt(i);
            }

            _gravityZoneExitBuffer.Clear();
            foreach (var kv in _activeGravityZones)
            {
                if (kv.Key == null || !_scannedGravityZones.Contains(kv.Key))
                    _gravityZoneExitBuffer.Add(kv.Key);
            }
            if (_gravityZoneExitBuffer.Count > 0)
            {
                for (int i = 0; i < _gravityZoneExitBuffer.Count; i++)
                    _activeGravityZones.Remove(_gravityZoneExitBuffer[i]);
                RecomputeGravityZoneMultiplier();
            }
        }

        private bool TryApplyZoneMovement()
        {
            if (_cc == null || !_cc.enabled || _zoneForceDuration <= 0f) return false;

            bool landedAfterLaunch = _zoneLaunchInAir && _cc.isGrounded && _verticalVelocity <= 0f;
            if (landedAfterLaunch)
            {
                _zoneForceDuration = 0f;
                _zoneForce = Vector3.zero;
                _zoneLaunchInAir = false;
                return false;
            }

            if (!_cc.isGrounded) _zoneLaunchInAir = true;

            Vector3 zoneMove = _zoneForce;
            zoneMove.y = _verticalVelocity;
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
                _lastMoveDir.x = zoneMove.x * inv;
                _lastMoveDir.y = 0f;
                _lastMoveDir.z = zoneMove.z * inv;
            }

            DoMove(zoneMove * Time.deltaTime);
            return true;
        }

        /// <summary>
        /// Apply an impulse force from a launch zone (ImpulseZone/FlingTrigger).
        /// Called from BotPatches when the game's trigger zones detect a bot.
        /// </summary>
        public void ApplyZoneImpulse(Vector3 force)
        {
            if (IsDead) return;
            // Match player exactly: FirstPersonController does `moveDirection += force` on ImpulseZone.
            // For vertical, accumulate onto existing velocity so entering mid-jump preserves momentum.
            _zoneForce += new Vector3(force.x, 0, force.z);
            if (Mathf.Abs(force.y) > 0.001f)
            {
                _verticalVelocity += force.y;
                if (force.y > 0f)
                {
                    // Upward launches ride until landing, like FPC's moveDirection arc.
                    float launchWindow = force.y > 3f ? 2.5f : 0.5f;
                    _zoneForceDuration = Mathf.Max(_zoneForceDuration, launchWindow);
                    _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, launchWindow);
                    _coyoteTimer = 0f;
                    _zoneLaunchInAir = true;
                }
                else
                {
                    _zoneForceDuration = Mathf.Max(_zoneForceDuration, 0.5f);
                }
            }
            if (new Vector3(force.x, 0f, force.z).sqrMagnitude > 1f)
            {
                _zoneForceDuration = Mathf.Max(_zoneForceDuration, 0.5f);
            }
            _stuckTimer = 0f;
            _didStuckNudge = false;
            _didStuckRepath = false;
            Plugin.Log.LogInfo($"[{BotName}] Zone impulse: {force} (vert={_verticalVelocity})");
        }

        /// <summary>Apply an ImpulseZone once per enter, matching the player's OnPlayerEnter behavior.</summary>
        public void EnterImpulseZone(ImpulseZone impulse)
        {
            if (impulse == null || IsDead) return;
            if (!_activeImpulseZones.Add(impulse)) return;
            ApplyZoneImpulse(impulse.force);
        }

        public void ExitImpulseZone(ImpulseZone impulse)
        {
            if (impulse == null) return;
            _activeImpulseZones.Remove(impulse);
        }

        /// <summary>
        /// Register a ForceZone we've entered — we'll apply its force every frame from Update,
        /// rather than relying on OnTriggerStay (which fires unreliably on CharacterController bots).
        /// </summary>
        public void RegisterForceZone(ForceZone fz)
        {
            if (fz == null) return;
            if (!_activeForceZones.Contains(fz))
                _activeForceZones.Add(fz);
        }

        /// <summary>Bot left a ForceZone — stop applying its force.</summary>
        public void UnregisterForceZone(ForceZone fz)
        {
            if (fz == null) return;
            _activeForceZones.Remove(fz);
        }

        /// <summary>Bot entered a GravityZone; apply its multiplier to bot gravity while active.</summary>
        public void RegisterGravityZone(GravityZone zone, float multiplier)
        {
            if (zone == null || IsDead) return;
            if (_activeGravityZones.ContainsKey(zone)) return;
            _activeGravityZones[zone] = multiplier;
            _gravityZoneMultiplier *= multiplier;
            Plugin.Log.LogInfo($"[{BotName}] Gravity zone enter: x{multiplier} (total={_gravityZoneMultiplier})");
        }

        /// <summary>Bot left a GravityZone; rebuild the active multiplier product.</summary>
        public void UnregisterGravityZone(GravityZone zone)
        {
            if (zone == null) return;
            if (!_activeGravityZones.TryGetValue(zone, out float multiplier)) return;
            _activeGravityZones.Remove(zone);
            RecomputeGravityZoneMultiplier();
            Plugin.Log.LogInfo($"[{BotName}] Gravity zone exit: /{multiplier} (total={_gravityZoneMultiplier})");
        }

        private void RecomputeGravityZoneMultiplier()
        {
            _gravityZoneMultiplier = 1f;
            foreach (var kv in _activeGravityZones)
            {
                if (kv.Key != null) _gravityZoneMultiplier *= kv.Value;
            }
        }

        private void PruneDestroyedGravityZones()
        {
            if (_activeGravityZones.Count == 0) return;
            System.Collections.Generic.List<GravityZone> dead = null;
            foreach (var kv in _activeGravityZones)
            {
                if (kv.Key != null) continue;
                if (dead == null) dead = new System.Collections.Generic.List<GravityZone>();
                dead.Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
                _activeGravityZones.Remove(dead[i]);
            RecomputeGravityZoneMultiplier();
        }

        /// <summary>
        /// Apply force from every ForceZone we're currently inside. Called from Update().
        /// Mirrors the game's own ForceZone.Update loop architecture.
        /// </summary>
        private void ApplyActiveForceZones()
        {
            if (IsDead) return;
            // Prune destroyed zones
            for (int i = _activeForceZones.Count - 1; i >= 0; i--)
            {
                if (_activeForceZones[i] == null) _activeForceZones.RemoveAt(i);
            }
            if (_activeForceZones.Count == 0) return;

            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.2f);
            for (int i = 0; i < _activeForceZones.Count; i++)
            {
                var fz = _activeForceZones[i];
                Vector3 force = fz.force;
                Vector3 frameForce = force * dt;
                _zoneForce += new Vector3(frameForce.x, 0f, frameForce.z);
                if (Mathf.Abs(frameForce.y) > 0.0001f)
                {
                    _verticalVelocity += frameForce.y;
                    if (frameForce.y > 0f)
                    {
                        _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.5f);
                        _zoneLaunchInAir = true;
                    }
                }
            }
            _zoneForceDuration = Mathf.Max(_zoneForceDuration, 0.25f);
            _stuckTimer = 0f;
            _didStuckNudge = false;
            _didStuckRepath = false;
        }

        public float DbgIntentionalJumpTimer => _intentionalJumpTimer;
        public JumpReason DbgActiveJumpReason => _activeJumpReason;
        public Vector3 DbgMoveDir => _lastMoveDir;
        public NavNode DbgLastReachedNode => _lastReachedNode;
    }
}
