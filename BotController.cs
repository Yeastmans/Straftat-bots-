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

    public enum PathSource
    {
        GraphRoute,
        DirectTacticalRoute,
        ExploreBuildRoute,
        NavMeshRoute
    }

    public enum ProgressState
    {
        Progressing,
        Stalled,
        HardStuck
    }

    public partial class BotController : MonoBehaviour
    {
        // Const layer masks — avoid recomputation every frame.
        // Game layer table (TagManager): 0 Default, 1 TransparentFX, 4 Water, 7 Interactable,
        // 10 Ladder, 14 ShootThrough, 19 InteractEnvironment, 24 Glass, 27 InvisibleWall.
        // Map geometry is spread across MANY of these — the old masks (0,1,2,4,8,9,14) missed
        // Interactable/InteractEnvironment/Glass entirely and included held-weapon layers,
        // leaving ledge/jump/ceiling raycasts blind on most maps ("complicated maps are bad").
        private const int WORLD_MASK = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 7) | (1 << 14) | (1 << 19) | (1 << 24);
        private const int WALL_MASK = WORLD_MASK | (1 << 27); // InvisibleWall blocks movement
        private const int GROUND_MASK = WORLD_MASK;
        private const int EXPLOSIVE_MASK = (1 << 0) | (1 << 7) | (1 << 14);

        // Pre-allocated physics buffers — shared across all bots (Unity is single-threaded)
        private static readonly Collider[] _overlapBuffer = new Collider[32];
        private static readonly Collider[] _zoneOverlapBuffer = new Collider[128];
        private static float _globalRepathWindowStart;
        private static int _globalRepathCountInWindow;
        private static int _nextVisualSerial = 1;

        // Bot instance tracking — avoid GetComponent<BotController> in hot paths
        private static readonly HashSet<int> _botInstanceIds = new HashSet<int>();

        public int BotId;
        public string BotName;
        public int PlayerId;
        public int VisualSerial { get; private set; }

        public bool IsDead = false;

        public void RefreshVisualSerial()
        {
            VisualSerial = _nextVisualSerial++;
            if (_nextVisualSerial < 0) _nextVisualSerial = 1;
        }

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
        private int _noPathRecoveryStreak;   // Consecutive failed stuck-repaths before nodeless lock

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
        private static Dictionary<int, bool> _weaponReachCache = new Dictionary<int, bool>();
        private static float _weaponReachCacheTime;

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
            _weaponReachCache.Clear();
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

        // Ice surfaces — mirrors the game's SlopeSlide component (tags Footsteps/Ice,
        // Footsteps/SuperIce). Bots drive their own CharacterController so SlopeSlide's
        // output never reaches them; UpdateIceState() recreates it with the shipped
        // PlayerIK.prefab tuning values.
        private bool _onIce;
        private bool _onSuperIce;             // Super ice also blocks jumping (FPC L1076)
        private float _iceSlopeAngle;         // Ground slope under the bot, degrees
        private Vector3 _iceSlideMove;        // Walk-on-ice downhill push (SlopeSlide.steepSlopeSlideMove)
        private Vector3 _iceCrouchSlideMove;  // Crouch slide on ice slopes (SlopeSlide.slopeSlideMove)

        // Combat
        private float _detectionRange = 40f;
        private float _attackRange = 30f;
        private float _meleeRange = 3f;
        private float _minRangedDist = 4f;
        private float _fireTimer;
        private float _turnSpeed = 6f;
        private float _aimInaccuracy = 2.5f;

        // ---- Per-bot skill (1-10, from Plugin.BotSkills; 5 = roughly the old tuning) ----
        public int Difficulty = 5;
        public int SkillSlot = -1;                // stable config slot 0-7 (lobby position)
        private int _appliedDifficulty = -1;
        private float _skillRefreshTimer;
        private float _skillReactionMin = 0.15f;  // reaction delay range on new target
        private float _skillReactionMax = 0.4f;
        private float _skillLockOnRate = 1.5f;    // how fast _aimSmoothing ramps to 1
        private float _skillAimSlerp = 10f;       // aim rotation slerp speed
        private float _skillBurstPauseMult = 1f;  // full-auto pause length multiplier
        private float _skillDodgeChance = 0.02f;  // per-frame dodge roll when hurt
        private float _skillDriftFloor = 0.35f;   // close-range floor on aim drift (low skill = wobbly even point-blank)
        private float _skillSemiAutoFloor = 0.55f;// min seconds between semi-auto shots
        private int _skillBurstMin = 3;           // full-auto shots before a pause
        private int _skillBurstMax = 6;

        // Piecewise map pinned at skill 5 ≈ the pre-skill-system constants, with the
        // extremes pushed hard: 1 should feel like target practice, 10 like an aimbot.
        private static float SkillLerp(int level, float easy, float mid, float hard)
        {
            if (level <= 5) return Mathf.Lerp(easy, mid, (level - 1) / 4f);
            return Mathf.Lerp(mid, hard, (level - 5) / 5f);
        }

        private void ApplyDifficulty(int level)
        {
            level = Mathf.Clamp(level, 1, 10);
            if (level == _appliedDifficulty) return;
            _appliedDifficulty = level;
            Difficulty = level;

            _aimInaccuracy      = SkillLerp(level, 8.0f, 2.5f, 0.3f);
            _skillDriftFloor    = SkillLerp(level, 0.65f, 0.35f, 0.08f);
            _skillReactionMin   = SkillLerp(level, 0.55f, 0.15f, 0.04f);
            _skillReactionMax   = SkillLerp(level, 1.10f, 0.40f, 0.10f);
            _skillLockOnRate    = SkillLerp(level, 0.5f, 1.5f, 4.5f);
            _skillAimSlerp      = SkillLerp(level, 4.5f, 10f, 22f);
            _detectionRange     = SkillLerp(level, 22f, 40f, 60f);
            _skillBurstPauseMult = SkillLerp(level, 2.2f, 1f, 0.35f);
            _skillDodgeChance   = SkillLerp(level, 0.002f, 0.02f, 0.08f);
            _skillSemiAutoFloor = SkillLerp(level, 0.85f, 0.55f, 0.30f);
            _skillBurstMin      = Mathf.RoundToInt(SkillLerp(level, 2f, 3f, 5f));
            _skillBurstMax      = Mathf.RoundToInt(SkillLerp(level, 4f, 6f, 9f));
        }

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
        private List<int> _validationRouteNodeIds = new List<int>();
        private float _validationRouteTimer;
        private Vector3 _validationRouteTarget;
        private string _validationRouteLabel;

        // NavGraph pathfinding
        private List<NavNode> _graphPath = new List<NavNode>();
        private int _graphPathIndex;
        private float _repathTimer;
        private Vector3 _lastPathTarget;
        private float _routeCommitUntil;
        private Vector3 _routeCommitTarget;
        private float _lastAcceptedPathScore = float.MinValue;
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
        // Continuous ForceZone contribution THIS frame (velocity, rebuilt every frame).
        // Mirrors the player: FPC's moveDirection.xz is rebuilt from input each frame, so
        // ForceZone's `moveDirection += force*dt` is a per-frame velocity offset for them,
        // NOT an accumulating impulse. Bots add it on top of normal movement in DoMove.
        private Vector3 _zoneFrameForce;
        private bool _zoneVerticalActive;         // A zone pushed us up this frame (skip vertical cap)
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
        private Vector3 _ladderExitDir;
        // LADDER: re-path watchdog. Every ~2 sec while on a ladder, re-check whether the
        // current path is still valid; if not, dismount gracefully.
        private float _ladderRepathTimer;

        // Weapon pursuit
        private float _weaponPursuitTimer;
        private float _weaponLastDist = float.MaxValue;
        private float _weaponNoProgressTimer;
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
        private float _huntNoLosTimer;
        private bool _huntHasLastSeenTarget;
        private Vector3 _huntLastSeenTargetPos;
        private float _huntNoLosSideSwitchTimer;
        private int _huntNoLosSide = 1;


        // Crouch
        private bool _isCrouching;
        private float _crouchTimer;

        // Freeze until round starts
        private bool _frozen = true;

        // Head blocked timer (for auto-slide under low ceilings)

        // Stun (taser)
        private float _stunTimer;

        // Explosive avoidance

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
                _cc.skinWidth = SKIN_STANDING;  // game CC default (0.2); model offset matches in ApplyStance
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
        private float _jumpBackoffTimer;     // >0: backing off from a lip to build a run-up
        private float _jumpBackoffCooldownUntil; // one run-up attempt per few seconds, never ping-pong
        private Vector3 _smoothedMoveDir;    // low-pass on the commanded move direction (anti-jitter)

        // In-stride reroute tracker: moving but not closing on the objective
        private Vector3 _softObjLastPos;
        private float _softObjBest = float.MaxValue;
        private float _softObjStagnantSince;

        // Hunt: cooldown between attempts to claim a vantage node above the target
        private float _nextHighGroundBidTime;

        // Top of the ladder collider currently being climbed (tapers the face-pull)
        private float _ladderTopY = -1f;

        /// <summary>Display name of the held weapon (kill-feed fallback when a thrown
        /// projectile has lost its ItemBehaviour link — the launcher is still in hand).</summary>
        public string HeldWeaponDisplayName
        {
            get
            {
                try
                {
                    var ib = _heldWeaponObj != null ? _heldWeaponObj.GetComponent<ItemBehaviour>() : null;
                    if (ib != null && !string.IsNullOrWhiteSpace(ib.weaponName))
                        return ib.weaponName.Replace("(Clone)", "").Trim();
                    if (_heldWeaponObj != null)
                        return _heldWeaponObj.name.Replace("(Clone)", "").Trim();
                }
                catch { }
                return null;
            }
        }

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
                    _stuckTimer += 0.35f; // Nudge recovery without instantly escalating hard-stuck
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

        // Mirrors FirstPersonController.dmgZoneTimer — next time a DamageZone may tick us.
        private float _nextDmgZoneTickTime;

        private float _envZonePollTimer;
        private static readonly Collider[] _envZoneHits = new Collider[32];

        /// <summary>Overlap the CC capsule against trigger volumes and feed the same
        /// TryEnvironmentKill the callbacks use. A CharacterController only generates
        /// trigger callbacks while it MOVES, and OnTriggerEnter never fires when the
        /// bot spawns or comes to rest inside a volume — a bot standing in kill water
        /// took no damage. Polling closes that gap on every map.</summary>
        private void PollEnvironmentZones()
        {
            if (IsDead || _playerHealth == null || _playerHealth.isKilled || _cc == null || !_cc.enabled) return;
            Vector3 center = transform.position + _cc.center;
            float half = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius);
            int n = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * half, center - Vector3.up * half,
                _cc.radius, _envZoneHits, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var col = _envZoneHits[i];
                if (col == null || !col.isTrigger) continue;
                TryEnvironmentKill(col);
                if (IsDead || _playerHealth == null || _playerHealth.isKilled) return;
            }
        }

        private void TryEnvironmentKill(Collider col)
        {
            bool isKillZone = col.CompareTag("Killz");
            bool isDamageZone = col.CompareTag("DamageZone");
            if (!isKillZone && !isDamageZone) return;

            if (isDamageZone)
            {
                // Mirror FirstPersonController.OnTriggerStay: a DamageZone is damage
                // over time (shipped prefab: 0.4 HP per 0.1s tick vs 10 max health,
                // ~2.5s to die), NOT an instant kill. Instant-killing on any touch was
                // the "bots randomly die on ice" bug — icy maps carry damage volumes
                // that players survive by crossing quickly.
                var zone = col.GetComponentInParent<DamageZone>();
                float amount = zone != null ? zone.damageAmount : 0.4f;
                float interval = zone != null ? zone.damageInterval : 0.1f;
                if (Time.time < _nextDmgZoneTickTime) return;
                _nextDmgZoneTickTime = Time.time + Mathf.Max(0.05f, interval);
                bool lethalTick = _playerHealth.health - amount <= 0f;
                try { _playerHealth.RemoveHealth(amount); } catch { }
                if (!lethalTick) return; // hurt, keep moving — death only on the lethal tick
            }

            // Use game's RPCs so all clients see the death (same as real player)
            try { _playerHealth.RemoveHealth(_playerHealth.health + 10f); } catch { }
            try { _playerHealth.ChangeKilledState(true); } catch { }
            // Explode BEFORE disabling physics — ragdoll reads bone positions from graphics
            try { _playerHealth.ExplodeServer(false, false, "", -transform.forward, 30f, transform.position + Vector3.up * 2f); } catch { }
            DisableBotPhysics(gameObject);
            try { _playerHealth.DisablePlayerObjectWhenKilled(); } catch { }
            Die(null);
            // Match the game's own wording for these deaths.
            string envLine = isKillZone ? "fell into the void" : "commited suicide";
            try { if (PauseManager.Instance != null) PauseManager.Instance.WriteLog($"<b><color=orange>{BotName}</color></b> {envLine}"); } catch { }
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
                PlayerRecorder.ClearPlayer(BotId);
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

            // Per-bot skill — re-read from config on a slow cadence so mod-menu
            // changes apply live to bots already in the match.
            _skillRefreshTimer -= Time.deltaTime;
            if (_skillRefreshTimer <= 0f)
            {
                _skillRefreshTimer = 2f;
                ApplyDifficulty(Plugin.GetBotSkill(SkillSlot >= 0 ? SkillSlot : BotId));
            }

            // Death detection
            if (_playerHealth != null && (_playerHealth.isKilled || _playerHealth.health <= 0f))
            {
                Die(_playerHealth.killer);
                return;
            }

            // Void death — same flow as real player (FPC checks y < -300, we check -50 since maps vary)
            if (transform.position.y < -50f)
            {
                // Feed the fall-death heatmap: the mistake happened at the last solid
                // ground, not down in the void — path scoring avoids that lip now.
                try { NavGraph.Instance?.ReportFallDeath(_lastGroundedPos); } catch { }
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
            // Kill-zone poll — CC trigger callbacks miss stationary bots (see PollEnvironmentZones)
            _envZonePollTimer -= Time.deltaTime;
            if (_envZonePollTimer <= 0f)
            {
                _envZonePollTimer = 0.15f;
                PollEnvironmentZones();
                if (IsDead) return;
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
            UpdateIceState();
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
            HandleVaultMantle();
            UpdateOverheadSlide();
            UpdateSlide();
            UpdateAnimator();
            UpdateFootsteps();

            if (TryApplyZoneMovement())
            {
                CheckStuck();
                return;
            }

            // Explosive/mine flee logic intentionally disabled.
            // Bots now keep normal pathing/combat behavior around mines and grenades.

            _logTimer += Time.deltaTime;
            if (_logTimer > 5f)
            {
                _logTimer = 0f;
                float hp = _playerHealth != null ? _playerHealth.health : 0;
                bool graphReady = NavGraph.Instance != null && NavGraph.Instance.HasData;
                float vel = _cc != null ? Mathf.Sqrt(_cc.velocity.x * _cc.velocity.x + _cc.velocity.z * _cc.velocity.z) : 0f;
                string nlTag = _nodelessLockTimer > 0f ? $" NL={_nodelessLockTimer:F1}" : "";
                Plugin.Log.LogInfo($"[{BotName}] State={State} hp={hp} weapon={(_heldWeapon != null ? _heldWeapon.name : "none")} vel={vel:F1} graph={graphReady}({NavGraph.Instance?.Nodes.Count ?? 0}n) grounded={(_cc != null && _cc.isGrounded)} stuck={_stuckTimer:F1}{nlTag} prog={_progressState} src={_pathSource} pos={transform.position} path={_graphPath.Count}");
                if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                {
                    Plugin.Log.LogInfo($"[{BotName}] reliability stuck_events={_stuckEvents} stageA={_recoveryStageA} stageB={_recoveryStageB} stageC={_recoveryStageC} stageD={_recoveryStageD} loop_breaks={_loopBreaks} src_switches={_pathSourceSwitches} hat_failures={_hatAttachFailures}");
                }
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
                if (Plugin.IsValidateMode) HandleTrainingValidation();
                else Wander();
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
            // This prevents floating in non-moving states without double-applying gravity.
            // Routed through DoMove so the ice slide still pushes a stationary bot.
            if (!_movedThisFrame && _cc != null && _cc.enabled)
                DoMove(new Vector3(0, _verticalVelocity * Time.deltaTime, 0));

            CheckStuck();

            // Periodic graph maintenance — only first bot runs it
            if (NavGraph.Instance != null && BotId == 0)
                NavGraph.Instance.PeriodicMaintenance();

            // Record bot movement into NavGraph
            if (_cc != null)
            {
                bool grounded = _cc.isGrounded;

                // Detect unintentional falls while following a path — upgrade edge to Jump
                if (_wasGroundedLastFrame && !grounded && !_justJumped && !_onLadder && !_onIce
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

            // Auto-crouch under low ceilings. Two triggers:
            //  1. Overhead: SphereCast up (capsule-width, not a thin center ray — a thin ray
            //     misses partial overhangs and lets the bot stand into a jam).
            //  2. Look-ahead: a head-height bar within 1.1m of travel that is clear at crouch
            //     height — duck BEFORE hitting the doorway instead of jamming into the lintel.
            // Ceiling crouches stand back up the moment clearance exists (no timer), and are
            // tracked separately from combat's timed tactical crouch.
            if (grounded && !_isSliding && _cc != null)
            {
                if (!_isCrouching && (OverheadBlocked() || LowCeilingAhead()))
                {
                    _isCrouching = true;
                    _ceilingCrouch = true;
                    ApplyStance(CROUCH_HEIGHT, SKIN_CROUCHED);
                    if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", true);
                    if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", true);
                }
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
                // Ceiling crouch: stand the moment there is room (checked every frame).
                // Tactical crouch: stand when its timer runs out (old behavior).
                bool wantsStand = _ceilingCrouch
                    || _crouchTimer <= 0f
                    || (_cc != null && _cc.height < 1.5f && _stuckTimer > 1f);
                if (wantsStand && !OverheadBlocked() && !LowCeilingAhead(0.7f))
                {
                    _isCrouching = false;
                    _ceilingCrouch = false;
                    _crouchTimer = 0f;
                    ApplyStance(STAND_HEIGHT, SKIN_STANDING);
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
            PlayerRecorder.ClearPlayer(BotId);

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
            _hideGraphicsCo = StartCoroutine(HideGraphicsDelayed());

            // Training mode: auto-respawn after short delay to keep exploring
            if (NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training)
            {
                StartCoroutine(TrainingRespawnDelayed());
                // Diagnostic: name every renderer near the death spot shortly after death.
                // If a white box ever appears again, the log will say EXACTLY what it is.
                StartCoroutine(DumpDeathSceneRenderers(transform.position));
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

            // Strip any cosmetic that slipped past the ChangeDress postfix (RPC can run
            // before BotController is attached). The game's death code DETACHES the hat
            // into the world with a rigidbody — on bots it renders as a giant white
            // untextured slab frozen at the death spot. Never let it throw one.
            try
            {
                var deathSetup = GetComponent<PlayerSetup>();
                if (deathSetup != null && deathSetup.hat != null)
                {
                    Object.Destroy(deathSetup.hat);
                    deathSetup.hat = null;
                }
                foreach (var hp in GetComponentsInChildren<HatPosition>(true))
                    if (hp != null) Object.Destroy(hp.gameObject);
            }
            catch { }

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
                // Bot IDs (11+) are not always removed by vanilla PlayerDied paths.
                // Ensure the bot is gone from alivePlayers so round winner resolution can proceed.
                while (GameManager.Instance.alivePlayers.Contains(PlayerId))
                    GameManager.Instance.alivePlayers.Remove(PlayerId);
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
                    ejectDir, 30f, transform.position, "Torso", VisualSerial);
            }
            catch { }
        }

        // Called by BotManager on round reset only
        public void Respawn(Vector3 position, bool reapplyCosmetics = true)
        {
            RefreshVisualSerial();

            // Check if spawn position is inside a wall — nudge out if so
            Vector3 safePos = FindSafeSpawnPosition(position);
            PlayerRecorder.ClearPlayer(BotId);
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
            _huntNoLosTimer = 0f;
            _huntHasLastSeenTarget = false;
            _huntLastSeenTargetPos = Vector3.zero;
            _huntNoLosSideSwitchTimer = 0f;
            _huntNoLosSide = Random.value > 0.5f ? 1 : -1;

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
            _routeCommitUntil = 0f;
            _routeCommitTarget = Vector3.zero;
            _lastAcceptedPathScore = float.MinValue;
            _validationRouteNodeIds.Clear();
            _validationRouteTimer = 0f;
            _validationRouteTarget = Vector3.zero;
            _validationRouteLabel = null;
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
            _noPathRecoveryStreak = 0;
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
            _ladderClimbTimer = 0f;
            _lastLadderTouchTime = -999f;
            _ladderFaceDirPinned = false;
            _ladderPinnedFaceDir = Vector3.zero;
            _ladderFaceDir = Vector3.zero;
            _ladderExitDir = Vector3.zero;
            _ladderRepathTimer = 2f;
            _ladderLastYSample = safePos.y;
            _ladderYSampleTime = Time.time;
            _nearLadder = false;
            _searchTimer = 0f;
            _targetItem = null;
            _loggedError = false;

            // Re-enable component and physics (disabled in Die())
            enabled = true;
            if (_cc != null)
            {
                _cc.enabled = true;
                ApplyStance(STAND_HEIGHT, SKIN_STANDING);
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

            // Cancel a still-pending death graphics hide so it can't blank the fresh body
            if (_hideGraphicsCo != null)
            {
                StopCoroutine(_hideGraphicsCo);
                _hideGraphicsCo = null;
            }

            SetVisible(true);

            // Death disabled all animators (HideGraphicsDelayed). Without re-enabling them the
            // respawned bot renders frozen in bind pose — arms straight out, the "big white
            // cross". Rebind + Update(0) snaps the skeleton back to a valid animated pose.
            foreach (var anim in GetComponentsInChildren<Animator>(true))
            {
                anim.enabled = true;
                try { anim.Rebind(); anim.Update(0f); } catch { }
            }
            foreach (var netAnim in GetComponentsInChildren<FishNet.Component.Animating.NetworkAnimator>(true))
                netAnim.enabled = true;

            // The game's PlayerHealth only ever spawns one ragdoll per life (private
            // spawnedRagdoll latch) — reset it so this bot's NEXT death ragdolls too.
            if (_playerHealth != null)
            {
                try
                {
                    var srField = typeof(PlayerHealth).GetField("spawnedRagdoll",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    srField?.SetValue(_playerHealth, false);
                }
                catch { }
            }

            if (GameManager.Instance != null)
                GameManager.Instance.alivePlayers.Add(PlayerId);

            if (reapplyCosmetics)
                BotManager.Instance?.ReapplyCosmeticsForBot(this);

            // The game's stun effect writes a "Float2" toggle into body materials and
            // enables a stun VFX object; bots never run the restore path, so a bot that
            // died stunned respawns with the glitched stun shader baked in. Clear both.
            try
            {
                foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mats = smr.materials;
                    bool changed = false;
                    foreach (var m in mats)
                        if (m != null && m.HasProperty("Float2")) { m.SetFloat("Float2", 0f); changed = true; }
                    if (changed) smr.materials = mats;
                }
                var setup = GetComponent<PlayerSetup>();
                if (setup != null)
                {
                    var vfxField = typeof(PlayerSetup).GetField("stunVFX",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var vfx = vfxField?.GetValue(setup) as GameObject;
                    if (vfx != null) vfx.SetActive(false);
                }
            }
            catch { }

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

        private System.Collections.IEnumerator DumpDeathSceneRenderers(Vector3 deathPos)
        {
            yield return new WaitForSeconds(1.5f);
            try
            {
                int logged = 0;
                foreach (var r in Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var b = r.bounds;
                    if ((b.center - deathPos).sqrMagnitude > 36f) continue; // 6m radius
                    if (b.size.magnitude > 30f) continue;                    // skip giant static world meshes
                    if (r.transform.root == transform.root) continue;        // the (hidden) bot itself
                    string mat = "none";
                    var sm = r.sharedMaterial;
                    if (sm != null) mat = $"{sm.name}|{(sm.shader != null ? sm.shader.name : "noshader")}";
                    string path = r.gameObject.name;
                    var t = r.transform;
                    while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
                    Plugin.Log.LogInfo($"[DeathScene] {BotName}: '{path}' layer={r.gameObject.layer} tag={r.tag} mat={mat} size={b.size:F1} pos={b.center:F1}");
                    if (++logged >= 25) break;
                }
                if (logged == 0)
                    Plugin.Log.LogInfo($"[DeathScene] {BotName}: no dynamic renderers within 6m of death spot");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[DeathScene] dump failed: {e.Message}"); }
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

            Vector3 spawnPos = best.transform.position + Vector3.up * 1.5f;

            // Fresh object replacement — same as the round-start / "Spawn Bots Now" paths,
            // which are the ones known to respawn bots cleanly. Resurrecting this dead
            // object in place kept leaking death state (white slabs). NOTE: this destroys
            // the current GameObject, so nothing may run after the call.
            if (BotManager.Instance != null)
            {
                BotManager.Instance.RespawnBotFresh(this, spawnPos);
                yield break;
            }

            // Fallback if the manager is somehow gone: old in-place respawn
            Respawn(spawnPos);
            Plugin.Log.LogInfo($"[{BotName}] Training auto-respawn (in-place fallback) at {best.transform.position}");
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
            bool refreshReach = Time.time - _weaponReachCacheTime > 2.5f;
            if (refreshReach) _weaponReachCacheTime = Time.time;

            // If the bot already holds a propeller, skip other propellers — we need a
            // REAL combat weapon to replace the propeller, not another one of the same.
            bool haveAnyPropeller = _cachedIsPropeller;

            foreach (var item in items)
            {
                if (item == null || item.isTaken) continue;
                if (item.rootObject != null || item.gameObject.layer != 7) continue;
                if (_blacklistedWeapons.TryGetValue(item, out float blacklistTime))
                {
                    if (Time.time - blacklistTime < 20f) continue;
                    _blacklistedWeapons.Remove(item);
                }

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

                // Reachability gate: avoid tunneling toward weapons with no realistic path.
                // Keep this cheap by caching + only probing occasionally.
                if (NavGraph.Instance != null && NavGraph.Instance.HasData)
                {
                    bool reachable;
                    if (!refreshReach && _weaponReachCache.TryGetValue(iid, out bool cachedReach))
                    {
                        reachable = cachedReach;
                    }
                    else
                    {
                        // Close items are always worth trying; far items need at least one route.
                        reachable = sqrDist < 64f;
                        if (!reachable)
                        {
                            var quick = NavGraph.Instance.GetCachedRoute(myPos, item.transform.position);
                            if (quick != null && quick.Count > 0) reachable = true;
                            else
                            {
                                var path = NavGraph.Instance.FindPath(myPos, item.transform.position, jitter: 0.05f, searchRadius: 45f, playerOnly: true, preferHeight: true);
                                reachable = path != null && path.Count > 0;
                            }
                        }
                        _weaponReachCache[iid] = reachable;
                    }
                    if (!reachable) continue;
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
            float closestScore = float.MaxValue;

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
                BotController otherBot = isOtherBot ? ph.GetComponent<BotController>() : null;
                if (otherBot != null && otherBot.IsDead) continue;

                float dist = HorizontalDist(transform.position, ph.transform.position);
                if (dist >= _detectionRange) continue;

                bool hasWeapon = _heldWeapon != null && _heldWeaponObj != null;
                bool visible = HasTargetVisibility(ph.transform);
                if (!humansAlive && !isOtherBot) continue;
                if (humansAlive && isOtherBot && (!hasWeapon || (!visible && dist > _detectionRange * 0.65f)))
                    continue;

                float score = dist;
                if (humansAlive && isOtherBot)
                    score += visible ? 4f : 14f; // Prefer humans, but take obvious bot fights.
                if (visible)
                    score -= 18f;
                if (hasWeapon && visible && dist < 18f)
                    score -= 8f;

                if (score < closestScore)
                {
                    closestScore = score;
                    closest = ph.transform;
                }
            }
            return closest;
        }

        private bool HasTargetVisibility(Transform target)
        {
            if (target == null) return false;
            Vector3 origin = transform.position + Vector3.up * 1.25f;
            Vector3 dest = target.position + Vector3.up * 1.0f;
            Vector3 toTarget = dest - origin;
            float dist = toTarget.magnitude;
            if (dist < 0.1f) return true;
            return !Physics.Raycast(origin, toTarget / dist, dist, WALL_MASK, QueryTriggerInteraction.Ignore);
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
        private Coroutine _hideGraphicsCo;
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

        // True while the current crouch is forced by geometry (vs combat's tactical crouch)
        private bool _ceilingCrouch;

        /// <summary>Capsule-width overhead clearance test — blocked means standing here jams.</summary>
        private bool OverheadBlocked()
        {
            return Physics.SphereCast(transform.position + Vector3.up * 0.55f, 0.33f, Vector3.up,
                out _, 1.35f, WALL_MASK, QueryTriggerInteraction.Ignore);
        }

        /// <summary>A head-height bar within travel distance that is passable crouched.</summary>
        private bool LowCeilingAhead(float dist = 1.1f)
        {
            Vector3 dir = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();
            Vector3 feet = transform.position;
            // Band roughly 1.25–1.85m up: catches doorway lintels/pipes at bot head height
            if (!Physics.SphereCast(feet + Vector3.up * 1.55f, 0.3f, dir, out _, dist,
                    WALL_MASK, QueryTriggerInteraction.Ignore))
                return false;
            // Only duck if the crouch-height band (0.3–0.9m) is actually clear to pass
            return !Physics.SphereCast(feet + Vector3.up * 0.6f, 0.3f, dir, out _, dist + 0.3f,
                WALL_MASK, QueryTriggerInteraction.Ignore);
        }

        private void SetVisible(bool visible)
        {
            // Toggle both skinned and mesh renderers.
            // Hats/cigs are regular MeshRenderers; if we only re-enable skinned meshes
            // after death, cosmetics stay invisible even when reattached successfully.
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                r.enabled = visible;
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
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
        private bool _didStuckPivot;             // True after one objective pivot this stuck episode
        private bool _didStuckBreakout;          // True after one deterministic breakout this episode

        // Progress controller
        private PathSource _pathSource = PathSource.DirectTacticalRoute;
        private ProgressState _progressState = ProgressState.Progressing;
        private float _progressTimer;
        private float _lastObjectiveDist = float.MaxValue;
        private float _lastObjectiveUpdateTime;
        private float _nextRepathAllowedAt;
        private float _nextPathSourceLogAt;
        private float _lastHuntMoveTime;
        private float _lastCombatRepositionTime;

        // Diagnostics counters
        private int _stuckEvents;
        private int _recoveryStageA;
        private int _recoveryStageB;
        private int _recoveryStageC;
        private int _recoveryStageD;
        private int _loopBreaks;
        private int _pathSourceSwitches;
        private int _hatAttachFailures;

        // Anti-loop memory: node + edge + heading bucket
        private struct LoopSignature
        {
            public int NodeId;
            public EdgeType EdgeType;
            public int HeadingBucket;
        }
        private readonly LoopSignature[] _loopHistory = new LoopSignature[10];
        private int _loopHistoryIdx;
        private int _loopHistoryCount;
        private float _loopBlacklistUntil;

        /// <summary>
        /// Stage thresholds scale by configured recovery aggression.
        /// </summary>
        private (float a, float b, float c, float d) GetRecoveryThresholds()
        {
            // Bias toward repeated repath attempts before hard stuck breakout.
            if (Plugin.IsFastRecovery) return (0.5f, 0.95f, 2.3f, 4.5f);
            // Medium tightened (was 0.7/1.3/3.0/5.2): a bot visibly parked for over a
            // second reads as broken — nudge fast, repath fast, still leave room between
            // the later stages so recoveries don't thrash.
            if (Plugin.IsMediumRecovery) return (0.55f, 1.0f, 2.4f, 4.4f);
            return (0.9f, 1.8f, 3.8f, 6.2f);
        }

        private Vector3 GetCurrentObjective()
        {
            if (_playerTarget != null) return _playerTarget.position;
            if (_weaponTarget != null) return _weaponTarget.position;
            if (_hasWanderTarget) return _wanderTarget;
            return transform.position;
        }

        private bool IsDegeneratePath(Vector3 objective)
        {
            if (_graphPath == null || _graphPath.Count == 0 || _graphPathIndex >= _graphPath.Count) return true;
            if (_graphPath.Count > 1) return false;
            float objectiveDist = HorizontalDist(transform.position, objective);
            float nodeDist = HorizontalDist(transform.position, _graphPath[0].Position);
            return nodeDist < 2f && objectiveDist > 4f;
        }

        // Pacing detector: samples position every 0.5s over a 6s window. Lots of ground
        // covered with almost no net displacement = bouncing/pacing, regardless of which
        // system is steering or how slowly it flips. This is what the player actually sees.
        private readonly Vector3[] _oscSamples = new Vector3[12];
        private int _oscIdx;
        private int _oscCount;
        private float _oscSampleTimer;

        private void UpdatePacingDetector()
        {
            if (IsDead || _frozen || _onLadder) return;
            // Combat strafing legitimately covers ground without displacement.
            if (State == BotState.Hunt || _playerTarget != null) return;

            _oscSampleTimer -= Time.deltaTime;
            if (_oscSampleTimer > 0f) return;
            _oscSampleTimer = 0.5f;

            _oscSamples[_oscIdx] = transform.position;
            _oscIdx = (_oscIdx + 1) % _oscSamples.Length;
            if (_oscCount < _oscSamples.Length) { _oscCount++; return; }

            float walked = 0f;
            for (int k = 0; k + 1 < _oscSamples.Length; k++)
            {
                Vector3 a = _oscSamples[(_oscIdx + k) % _oscSamples.Length];
                Vector3 b = _oscSamples[(_oscIdx + k + 1) % _oscSamples.Length];
                walked += Vector3.Distance(a, b);
            }
            Vector3 oldest = _oscSamples[_oscIdx % _oscSamples.Length];
            float net = Vector3.Distance(oldest, transform.position);

            if (walked > 14f && net < 3f)
            {
                Plugin.Log.LogInfo($"[{BotName}] Pacing detected (walked {walked:F0}m, net {net:F1}m) — dropping objective and route");
                _oscCount = 0;
                _hasWanderTarget = false;
                _wanderChangeTimer = 0f;
                _exploreState = ExploreState.None;
                _graphPath.Clear();
                _graphPathIndex = 0;
                _repathTimer = 0f;
                _lastAcceptedPathScore = float.MinValue;
                _routeCommitUntil = 0f;
                _weaponPursuitTimer = 0f;
                if (_targetItem != null) _blacklistedWeapons[_targetItem] = Time.time;
                _weaponTarget = null;
            }
        }

        private void UpdateProgressController(float movedSqr)
        {
            UpdatePacingDetector();
            Vector3 objective = GetCurrentObjective();
            float objectiveDist = HorizontalDist(transform.position, objective);
            bool objectiveValid = objectiveDist > 0.5f;
            bool madeMoveProgress = movedSqr >= 0.16f;
            bool madeObjectiveProgress = objectiveValid && _lastObjectiveDist < float.MaxValue && objectiveDist < _lastObjectiveDist - 0.25f;

            if (!objectiveValid)
            {
                _progressState = ProgressState.Progressing;
                _progressTimer = 0f;
                _lastObjectiveDist = objectiveDist;
                _lastObjectiveUpdateTime = Time.time;
                return;
            }

            if (madeMoveProgress || madeObjectiveProgress)
            {
                _progressTimer = Mathf.Max(0f, _progressTimer - _stuckCheckInterval);
                _progressState = ProgressState.Progressing;
                _lastObjectiveDist = objectiveDist;
                _lastObjectiveUpdateTime = Time.time;
                _stuckTimer = Mathf.Max(0f, _stuckTimer - _stuckCheckInterval);
                return;
            }

            _progressTimer += _stuckCheckInterval;
            _lastObjectiveDist = objectiveDist;
            if (_progressTimer >= 2.4f) _progressState = ProgressState.HardStuck;
            else if (_progressTimer >= 0.8f) _progressState = ProgressState.Stalled;
        }

        private void SwitchPathSource(PathSource src)
        {
            if (_pathSource == src) return;
            _pathSource = src;
            _pathSourceSwitches++;
            if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value && Time.time >= _nextPathSourceLogAt)
            {
                _nextPathSourceLogAt = Time.time + 1.5f;
                Plugin.Log.LogInfo($"[{BotName}] PathSource -> {src}");
            }
        }

        private void DoRecoveryPivot()
        {
            _didStuckPivot = true;
            _recoveryStageC++;
            _graphPath.Clear();
            _graphPathIndex = 0;
            _repathTimer = 0f;
            _weaponTarget = null;
            _targetItem = null;
            // Prefer player-sourced anchors when available. This helps bots converge on
            // complex jump/ladder corridors humans have already proven.
            if (NavGraph.Instance != null)
            {
                Vector3 objective = GetCurrentObjective();
                var playerNode = NavGraph.Instance.FindNearestPlayerNode(objective, 40f)
                    ?? NavGraph.Instance.FindNearestPlayerNode(transform.position, 40f);
                if (playerNode != null)
                {
                    var path = NavGraph.Instance.FindPath(transform.position, playerNode.Position, jitter: 0.05f, searchRadius: 50f, playerOnly: true, preferHeight: true);
                    if (path != null && path.Count > 0)
                    {
                        _graphPath = path;
                        _graphPathIndex = 0;
                        _hasWanderTarget = false;
                        SwitchPathSource(PathSource.GraphRoute);
                        return;
                    }
                }
            }
            if (_playerTarget != null)
            {
                Vector3 away = (transform.position - _playerTarget.position);
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = transform.right;
                away.Normalize();
                _wanderTarget = transform.position + away * 8f;
            }
            else
            {
                _wanderTarget = PickDistantSpawn();
            }
            _hasWanderTarget = true;
            SwitchPathSource(PathSource.ExploreBuildRoute);
        }

        private bool TryBuildRecoveryPath(Vector3 target, Vector3 moveDir, out List<NavNode> bestPath)
        {
            bestPath = null;
            if (NavGraph.Instance == null) return false;

            List<NavNode> chosenPath = null;
            float bestScore = float.MinValue;
            bool wantsHeight = Mathf.Abs(target.y - transform.position.y) > 2.25f;
            Vector3 avoidPos = transform.position;
            if (moveDir.sqrMagnitude > 0.01f)
            {
                Vector3 flat = moveDir;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    avoidPos += flat.normalized * 1.5f;
            }

            void Consider(List<NavNode> candidate, float bonus = 0f)
            {
                if (candidate == null || candidate.Count <= 1) return;
                if (!IsRouteSafeForPlay(candidate)) return;
                float score = ScorePathCandidate(candidate, target) + bonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    chosenPath = candidate;
                }
            }

            Consider(NavGraph.Instance.FindPathAvoiding(transform.position, target, avoidPos, 3.5f, 70f), 3f);
            Consider(NavGraph.Instance.FindPath(transform.position, target, jitter: 0.02f, searchRadius: 70f, preferHeight: wantsHeight), 1f);
            Consider(NavGraph.Instance.FindPath(transform.position, target, jitter: 0.05f, searchRadius: 80f, playerOnly: true, preferHeight: true), 2f);

            var reachable = NavGraph.Instance.FindClosestReachableNode(transform.position, target);
            if (reachable != null)
                Consider(NavGraph.Instance.FindPath(transform.position, reachable.Position, jitter: 0.03f, searchRadius: 70f, preferHeight: wantsHeight), 0.5f);

            var progress = NavGraph.Instance.FindProgressNode(transform.position, target, 70f);
            if (progress != null)
                Consider(NavGraph.Instance.FindPath(transform.position, progress.Position, jitter: 0.03f, searchRadius: 70f, preferHeight: wantsHeight), 0.25f);

            bestPath = chosenPath;
            return bestPath != null;
        }

        private static int HeadingBucket(Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return 0;
            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.FloorToInt(angle / 45f) % 8;
        }

        private void PushLoopSignature(int nodeId, EdgeType edgeType, Vector3 headingDir)
        {
            _loopHistory[_loopHistoryIdx] = new LoopSignature
            {
                NodeId = nodeId,
                EdgeType = edgeType,
                HeadingBucket = HeadingBucket(headingDir)
            };
            _loopHistoryIdx = (_loopHistoryIdx + 1) % _loopHistory.Length;
            if (_loopHistoryCount < _loopHistory.Length) _loopHistoryCount++;
        }

        private bool HasLoopCycle()
        {
            if (_loopHistoryCount < 6) return false;
            int i0 = (_loopHistoryIdx - 1 + _loopHistory.Length) % _loopHistory.Length;
            int i1 = (_loopHistoryIdx - 2 + _loopHistory.Length) % _loopHistory.Length;
            int i2 = (_loopHistoryIdx - 3 + _loopHistory.Length) % _loopHistory.Length;
            int i3 = (_loopHistoryIdx - 4 + _loopHistory.Length) % _loopHistory.Length;
            int i4 = (_loopHistoryIdx - 5 + _loopHistory.Length) % _loopHistory.Length;
            int i5 = (_loopHistoryIdx - 6 + _loopHistory.Length) % _loopHistory.Length;
            var a = _loopHistory[i0];
            var b = _loopHistory[i1];
            var c = _loopHistory[i2];
            var d = _loopHistory[i3];
            var e = _loopHistory[i4];
            var f = _loopHistory[i5];
            return a.NodeId == c.NodeId && c.NodeId == e.NodeId
                && b.NodeId == d.NodeId && d.NodeId == f.NodeId
                && a.NodeId != b.NodeId
                && a.HeadingBucket == c.HeadingBucket
                && b.HeadingBucket == d.HeadingBucket;
        }

        private void DoDeterministicBreakout(Vector3 moveDir)
        {
            _didStuckBreakout = true;
            _recoveryStageD++;
            _stuckEvents++;
            _loopBlacklistUntil = Time.time + 5f;

            Vector3 side = Vector3.Cross(Vector3.up, moveDir).normalized;
            if (side.sqrMagnitude < 0.01f) side = transform.right;
            if ((BotId & 1) == 0) side = -side;
            _commitDir = side;
            _commitTimer = 1.0f;
            if (_cc != null && _cc.isGrounded)
            {
                var obs = CheckObstructions(side, 1.2f);
                if (obs.CrouchClear && obs.WaistBlocked && !_isSliding) InitSlide(side, duration: 1.0f);
                else TryJump(JumpReason.ExploreStuck, side);
            }
            _nodelessLockTimer = Mathf.Max(_nodelessLockTimer, 4.5f);
            _stuckTimer = 0f;
            _progressTimer = 0f;
            SwitchPathSource(PathSource.DirectTacticalRoute);
        }

        private void CheckStuck()
        {
            if (State == BotState.Dead) return;
            if (_onLadder || _ladderDismountTimer > 0f)
            {
                // Climbing IS progress: bleed the timer so the red-X stuck indicator
                // (and any pending escalation) doesn't fire mid-climb.
                _stuckTimer = Mathf.Max(0f, _stuckTimer - Time.deltaTime);
                _progressTimer = 0f;
                return;
            }
            if (_zoneForceDuration > 0f) return;

            _stuckCheckTimer -= Time.deltaTime;
            if (_stuckCheckTimer > 0f) return;
            _stuckCheckTimer = _stuckCheckInterval;

            float movedSqr = HorizontalDistSqr(transform.position, _stuckCheckPos);
            _stuckCheckPos = transform.position;

            bool tryingToMove = State == BotState.GoToWeapon || State == BotState.Hunt ||
                                State == BotState.FindWeapon || _hasWanderTarget;

            UpdateProgressController(movedSqr);
            var (stageA, stageB, stageC, stageD) = GetRecoveryThresholds();

            if (movedSqr < 0.25f && tryingToMove) // 0.25 = 0.5^2
            {
                _stuckTimer += _stuckCheckInterval;
                Vector3 moveDir = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;

                // ---- Stage A: local steering correction ----
                if (_stuckTimer >= stageA && !_didStuckNudge && _cc != null && _cc.isGrounded)
                {
                    _didStuckNudge = true;
                    _recoveryStageA++;
                    var obs = CheckObstructions(moveDir);
                    // Crouch FIRST when the blocker is at face height with a passable
                    // gap below — the cheapest correct move, and it keeps steering.
                    if (obs.CrouchClear && obs.HeadBlocked && !obs.WaistBlocked && !_isSliding && !_isCrouching)
                        StartCrouch(1.2f);
                    else if (obs.CrouchClear && (obs.FeetBlocked || obs.HeadBlocked) && !_isSliding)
                        InitSlide(moveDir, duration: 1.0f);
                    else
                        TryJump(JumpReason.StuckRecovery, moveDir);
                    _commitDir = TryAngledDirections(moveDir, WALL_MASK);
                    _commitTimer = Mathf.Max(_commitTimer, 0.8f);
                }

                // ---- Stage B: tactical repath ----
                if (_stuckTimer >= stageB && !_didStuckRepath && Time.time >= _nextRepathAllowedAt
                    && TryConsumeGlobalRepathBudget())
                {
                    _didStuckRepath = true;
                    _recoveryStageB++;
                    _nextRepathAllowedAt = Time.time + 0.7f;
                    if (NavGraph.Instance != null)
                    {
                        Vector3 target = GetCurrentObjective();
                        if (IsDegeneratePath(target))
                        {
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                        }

                        NavGraph.Instance.BlacklistNearby(transform.position + moveDir * 0.8f, 2.4f);
                        if (TryBuildRecoveryPath(target, moveDir, out var path))
                        {
                            _graphPath = path;
                            _graphPathIndex = 0;
                            _lastReachedNode = null;
                            _prevReachedNode = null;
                            _repathTimer = 0f;
                            _noPathRecoveryStreak = 0;
                            SwitchPathSource(PathSource.GraphRoute);
                            Plugin.Log.LogInfo($"[{BotName}] Stuck -> repath ({path.Count} nodes)");
                        }
                        else
                        {
                            SwitchPathSource(PathSource.DirectTacticalRoute);
                            NavGraph.Instance.ReportStuck(transform.position, moveDir);
                            _graphPath.Clear();
                            _graphPathIndex = 0;
                            _repathTimer = 0f;
                            _noPathRecoveryStreak++;

                            // First failure: immediately retry repath with a new heading instead
                            // of entering a long nodeless lock. This keeps bots graph-first.
                            if (_noPathRecoveryStreak < 2 && _progressState != ProgressState.HardStuck)
                            {
                                _commitDir = TryAngledDirections(moveDir, WALL_MASK);
                                _commitTimer = Mathf.Max(_commitTimer, 0.75f);
                                _nextRepathAllowedAt = Time.time + 0.35f;
                                Plugin.Log.LogInfo($"[{BotName}] Stuck -> no path, retrying repath");
                            }
                            else
                            {
                                _nodelessBounceCount = Mathf.Min(5, _nodelessBounceCount + 1);
                                _lastBounceTime = Time.time;
                                _nodelessLockTimer = Mathf.Min(7f, 1.75f + 1.25f * _nodelessBounceCount);
                                if (_playerTarget == null && _weaponTarget == null)
                                {
                                    _wanderTarget = PickDistantSpawn();
                                    _hasWanderTarget = true;
                                }
                                Plugin.Log.LogInfo($"[{BotName}] Stuck -> no path, short nodeless lock {_nodelessLockTimer:F1}s");
                            }
                        }
                    }
                }

                // ---- Stage C: objective pivot ----
                if (_stuckTimer >= stageC && !_didStuckPivot)
                {
                    DoRecoveryPivot();
                }

                // ---- Stage D: deterministic physical breakout ----
                if (_stuckTimer >= stageD && !_didStuckBreakout)
                    DoDeterministicBreakout(moveDir);
            }
            else
            {
                // MOVING but not closing on a static objective (circling a waypoint,
                // sliding along a wall): swap to a fresh route in stride — no stop, no
                // breakout, and long before the 6s pacing detector has to fire.
                if (tryingToMove && _playerTarget == null)
                {
                    Vector3 softObj = GetCurrentObjective();
                    float softObjDist = HorizontalDist(transform.position, softObj);
                    if (HorizontalDist(softObj, _softObjLastPos) > 2f || softObjDist < _softObjBest - 0.35f)
                    {
                        _softObjLastPos = softObj;
                        _softObjBest = softObjDist;
                        _softObjStagnantSince = Time.time;
                    }
                    else if (softObjDist > 1.5f && Time.time - _softObjStagnantSince > 3.5f
                        && Time.time >= _nextRepathAllowedAt && TryConsumeGlobalRepathBudget())
                    {
                        _softObjStagnantSince = Time.time;
                        _nextRepathAllowedAt = Time.time + 0.7f;
                        Vector3 headingDir = _lastMoveDir.sqrMagnitude > 0.01f ? _lastMoveDir : transform.forward;
                        if (TryBuildRecoveryPath(softObj, headingDir, out var softPath))
                        {
                            _graphPath = softPath;
                            _graphPathIndex = 0;
                            _lastReachedNode = null;
                            _prevReachedNode = null;
                            _repathTimer = 0f;
                            SwitchPathSource(PathSource.GraphRoute);
                            Plugin.Log.LogInfo($"[{BotName}] No headway -> in-stride reroute ({softPath.Count} nodes)");
                        }
                    }
                }

                // Making progress (or not trying to move) — decay timer, clear flags once safely below threshold
                _stuckTimer = Mathf.Max(0f, _stuckTimer - _stuckCheckInterval);
                if (_stuckTimer < 0.1f)
                {
                    _didStuckNudge = false;
                    _didStuckRepath = false;
                    _didStuckPivot = false;
                    _didStuckBreakout = false;
                    _noPathRecoveryStreak = 0;
                }
            }
        }

        private bool TryConsumeGlobalRepathBudget()
        {
            const float WINDOW = 1.0f;
            const int MAX_REPATHS = 6; // tuned for 8 bots
            if (Time.time - _globalRepathWindowStart > WINDOW)
            {
                _globalRepathWindowStart = Time.time;
                _globalRepathCountInWindow = 0;
            }
            if (_globalRepathCountInWindow >= MAX_REPATHS) return false;
            _globalRepathCountInWindow++;
            return true;
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

            // Air-steer toward the current path target while the pad/zone carries us, so a
            // vertical jump-pad actually moves the bot toward where it's going instead of
            // launching straight up and dropping back onto the pad. Pad force still dominates;
            // this only adds the bot's normal air-control on top.
            Vector3 steer = Vector3.zero;
            if (!_cc.isGrounded && _graphPath != null && _graphPath.Count > 0 && _graphPathIndex < _graphPath.Count)
            {
                Vector3 toTarget = _graphPath[_graphPathIndex].Position - transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.25f)
                    steer = toTarget.normalized * _airSpeed;
            }

            Vector3 zoneMove = _zoneForce + steer;
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
                    // The pad/launch zone is now the sole authority on this arc — cancel any
                    // in-progress trajectory replay or charged self-jump so they don't fight it.
                    _trajActive = false;
                    _currentJumpEdge = null;
                    _trajIndex = 0;
                    _jumpChargeTimer = 0f;
                    _pendingJumpForce = 0f;
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
            _zoneFrameForce = Vector3.zero;
            _zoneVerticalActive = false;
            if (IsDead) return;
            // Prune destroyed zones
            for (int i = _activeForceZones.Count - 1; i >= 0; i--)
            {
                if (_activeForceZones[i] == null) _activeForceZones.RemoveAt(i);
            }
            if (_activeForceZones.Count == 0) return;

            // Match the player EXACTLY: ForceZone adds force*dt to moveDirection each
            // frame while the player keeps full control. The old code fed this into
            // _zoneForce/_zoneForceDuration, which hijacked ALL bot movement while
            // inside — in an updraft "bounce" zone the bot just yo-yoed in place with
            // no self-steering, and in horizontal push zones it drifted helplessly.
            // Now: horizontal is a per-frame additive on top of normal navigation
            // (consumed in DoMove), vertical accumulates into _verticalVelocity like
            // the FPC's moveDirection.y.
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.2f);
            for (int i = 0; i < _activeForceZones.Count; i++)
            {
                var fz = _activeForceZones[i];
                Vector3 frameForce = fz.force * dt;
                _zoneFrameForce += new Vector3(frameForce.x, 0f, frameForce.z);
                if (Mathf.Abs(frameForce.y) > 0.0001f)
                {
                    _verticalVelocity += frameForce.y;
                    if (frameForce.y > 0f)
                    {
                        _zoneVerticalActive = true;
                        // Suppress reactive jump/steer spam while an updraft carries us.
                        _intentionalJumpTimer = Mathf.Max(_intentionalJumpTimer, 0.5f);
                    }
                }
            }
            _stuckTimer = 0f;
            _didStuckNudge = false;
            _didStuckRepath = false;
        }

        public float DbgIntentionalJumpTimer => _intentionalJumpTimer;
        public JumpReason DbgActiveJumpReason => _activeJumpReason;
        public Vector3 DbgMoveDir => _lastMoveDir;
        public NavNode DbgLastReachedNode => _lastReachedNode;
        public void ReportHatAttachFailure() { _hatAttachFailures++; }
        public void ApplyStun(float stunTime)
        {
            if (IsDead) return;
            if (_fpc != null) _fpc.canMove = false;
            _stunTimer = Mathf.Max(_stunTimer, Mathf.Max(0.25f, stunTime));
        }
    }
}
