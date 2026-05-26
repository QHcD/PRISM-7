// test
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Serialization;
using System.Collections;
using System.Collections.Generic;

#if PUN_2_OR_NEWER
using Photon.Pun;
#endif


/// <summary>
/// Enemy AI with NavMeshAgent pathfinding, jump-over-obstacles, natural animations,
/// immediate ragdoll death, and proper weapon support.
///
/// Fix list (2026):
///   1. Jump — uses Mathf.Sqrt(jumpHeight * -2f * gravity) matching the Player formula.
///              Triggers when agent gets stuck with an obstacle in front.
///   2. Weapon attachment handled by LevelBuilder; EnemyController exposes weaponAttachPoint.
///   3. Animations — Speed parameter is normalised (0-1) and VelocityX/Z are set for blendtrees.
///   4. Death — plays "Dead" animation for deathAnimDuration seconds BEFORE ragdoll activates.
///   5. Victory — notifies GameManager via EnemyKilled() which now tracks totals correctly.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class EnemyController : MonoBehaviour, IDamageable
{
    private static readonly List<EnemyController> s_aliveEnemies = new List<EnemyController>(64);
    public static IReadOnlyList<EnemyController> AliveEnemies => s_aliveEnemies;
    private static PlayerController s_scenePlayer;
    private static float s_scenePlayerRefreshTime = -999f;
    private const float ChaseGateRecheckInterval = 0.35f;
    public const float CombatStoppingDistance = 0.08f;
    private const float StrictMeleeStrikeFactor = 0.88f;

    private const string PrefKeySicklePos = "Grip.Player.L13.Sickle.Pos";
    private const string PrefKeySickleEuler = "Grip.Player.L13.Sickle.Euler";
    private const string PrefKeySawPos = "Grip.Player.L12.Saw.Pos";
    private const string PrefKeySawEuler = "Grip.Player.L12.Saw.Euler";
    // ── Tuning ──────────────────────────────────────────────────────────────
    [Header("Combat")]
    public float detectionRadius  = 30f;
    public float attackRadius     = 2f;
    [Tooltip("Hard distance gate applied at the moment of impact (after windup). " +
             "A hit is cancelled if the attacker root is farther than this from the target. " +
             "Keep at or below attackRadius so the enemy must be truly close to land a hit.")]
    public float meleeAttackRange = 2.0f;
    public float attackDamage     = 10f;
    public float attackCooldown   = 0.65f;
    public int   maxHealth        = 60;
    [Tooltip("Hits with damage at or above this value trigger death-by-ragdoll even if health remains.")]
    public int   ragdollForceThreshold = 50;

    [Header("Movement — mirrors Player feel")]
    [Tooltip("Base walking speed (patrol / evade).")]
    public float moveSpeed        = 3.2f;
    [Tooltip("Default chase speed toward targets.")]
    public float chaseSpeed       = 5.2f;
    [Tooltip("Upper cap when closing on a sprinting target — keeps aggression without rocket speeds.")]
    public float sprintChaseSpeed = 6.2f;
    [Tooltip("Manual rotation Slerp rate when smoothing toward movement / aim vectors.")]
    public float rotationSpeed    = 10f;
    [Tooltip("NavMeshAgent acceleration (units/s²).")]
    public float agentAcceleration = 14f;
    [Tooltip("NavMeshAgent angular speed (deg/sec). Visual turning uses LateUpdate Slerp when updateRotation is off.")]
    public float agentAngularSpeed = 540f;
    [Tooltip("Scales locomotion blend-tree Speed vs actual agent velocity so run cycles match chase cadence.")]
    public float runAnimationMultiplier = 1.15f;
    [Tooltip("Logs chase velocity, agent tuning, and animator sync.")]
    public bool debugEnemyMovement = false;

    [Header("Flow Field Navigation")]
    [Tooltip("When enabled, chase movement follows the shared flow-field vectors instead of constantly pathing to target positions.")]
    public bool useFlowFieldNavigation = false;
    [Tooltip("Distance ahead (in metres) used as a local steering destination from the current flow vector.")]
    public float flowFieldLookAhead = 1.35f;
    [Tooltip("How often to refresh local steering destination from flow field.")]
    public float flowFieldSteerInterval = 0.12f;

    [Header("Jump (Obstacle Avoidance)")]
    [Tooltip("Height the enemy jumps when blocked. Tuned to a natural human-scale hop matching the Player.")]
    public float jumpHeight       = 1.05f;
    [Tooltip("Gravity value (negative). Mirrors PlayerController.gravity for matching arcs.")]
    public float gravity          = -22f;
    [Tooltip("Horizontal distance ahead to check for obstacles.")]
    public float obstacleCheckDist = 1.6f;
    [Tooltip("How long the enemy must be stuck before attempting a jump.")]
    public float stuckTime        = 1.2f;
    [Tooltip("While jumping: ignore stuck-jump if horizontal agent speed exceeds this.")]
    public float obstacleBlockedSpeedThreshold = 0.25f;

    [Header("Black Ops 3 Maneuvers (Enemies)")]
    [Tooltip("How often (seconds) the enemy may roll a combat maneuver (sprint/jump/slide/flip) while chasing.")]
    public float maneuverRollInterval = 2.4f;
    [Tooltip("Chance per roll that the enemy fires off a maneuver during chase (0..1).")]
    [Range(0f, 1f)] public float maneuverChance = 0.32f;
    [Tooltip("Forward boost applied during a closing slide.")]
    public float maneuverSlideBoost = 6.5f;
    [Tooltip("Forward boost applied during an evasive flip.")]
    public float maneuverFlipBoost = 7.0f;

    [Header("Weapon")]
    public WeaponGripSystem weaponGripSystem;
    [Tooltip("Drag the enemy's right-hand bone here in the Inspector (optional). " +
             "If null, EquipmentManager auto-detects bip_hand_R / weapon_bone_R.")]
    public Transform weaponAttachPoint;
    [Tooltip("Local grip position offset applied after the weapon is parented to the right hand socket.")]
    public Vector3 weaponGripLocalPosition = new Vector3(-0.01f, -0.0025f, 0f);
    [Tooltip("Local grip rotation offset in degrees so the blade/head extends forward from the hand.")]
    [FormerlySerializedAs("weaponGripLocalEuler")]
    public Vector3 weaponGripLocalEulerAngles = new Vector3(0f, 0f, 90f);
    [Tooltip("Optional local socket rotation normalization applied before per-weapon grip offsets.")]
    public Vector3 weaponSocketLocalEulerAngles = Vector3.zero;
    [Tooltip("When enabled, continuously removes the animated hand bone basis so the weapon can keep a player-matched pose on Crosby.")]
    public bool stabilizeWeaponSocketAgainstHandPose = false;

    [Header("Level 2 Katana — Hand Grip")]
    [Tooltip("Offset FROM the hand bone TO the katana grip point, in the hand bone's local space.\n" +
             "Default (0,0,0) = weapon pivot sits exactly at the hand bone (wrist/palm).\n" +
             "Tweak in Play Mode: positive Y moves the grip toward the fingers, negative toward the wrist.")]
    public Vector3 katanaHandGripOffset = new Vector3(0f, 0.06f, 0.02f);

    [Tooltip("Extra euler offset applied ON TOP of the player-matched rotation during the carry/idle pose.\n" +
             "X negative = raise blade tip up.  Tweak in Play Mode to match the player's guard stance.\n" +
             "Has no effect during the attack animation.")]
    public Vector3 katanaCarryExtraEuler = new Vector3(-20f, 0f, 0f);
    [Header("Level 12 Saw — Carry Pose")]
    [Tooltip("Offset FROM bip_hand_R TO the saw handle grip point, in the hand bone's local space.\n" +
             "Positions the saw so the handle sits in the palm (not the blade tip).\n" +
             "Tune in Play Mode if the grip point drifts.")]
    public Vector3 sawHandGripOffset = new Vector3(-0.066f, -0.39f, 0.044f);

    [Tooltip("Extra euler offset applied on top of the player-matched rotation during idle/carry.\n" +
             "Has no effect during the attack animation.")]
    public Vector3 sawCarryExtraEuler = new Vector3(0f, 0f, 0f);

    [Header("Level 9 Axe — Attack Swing Correction")]
    [Tooltip("Extra euler rotation applied to the axe ONLY during the Attack state.\n" +
             "Compensates for the bip_hand_R vs j_wrist_ri axis difference that makes\n" +
             "the swing appear reversed compared to the black player.\n" +
             "Default (0,180,0) flips the swing around Y to match the player's direction.\n" +
             "Tune in Play Mode (Inspector) if the result still looks wrong.\n" +
             "Has NO effect on idle grip — only active while State == Attack.")]
    public Vector3 axeAttackSwingCorrection = new Vector3(0f, 180f, 0f);

    [Header("Level 12 Saw — Attack Swing Correction")]
    [Tooltip("Extra euler rotation applied to the saw ONLY during the Attack state.\n" +
             "Same bone-axis mismatch as the Level 9 axe (bip_hand_R vs j_wrist_ri).\n" +
             "Default (0,180,0) flips the swing around Y to match the player's direction.\n" +
             "Tune in Play Mode (Inspector) if the result still looks wrong.\n" +
             "Has NO effect on idle grip — only active while State == Attack.")]
    public Vector3 sawAttackSwingCorrection = new Vector3(0f, 180f, 0f);

    [Header("Runtime Grip Persistence")]
    [Tooltip("When enabled, enemy sickle/saw grip uses the latest saved runtime tune values.")]
    public bool useSavedRuntimeGripValues = true;

    [HideInInspector] public GameObject equippedWeaponObject;

    [Header("FFA Target Detection")]
    [Tooltip("Layer mask for valid targets (set to 'Character' layer).")]
    public LayerMask detectionMask = ~0;
    [Tooltip("Extended emergency scan radius so enemies stay aggressive and do not idle.")]
    public float aggressiveScanRadius = 120f;

    [Tooltip("Seconds between target scans. Lower = snappier target reacquisition.")]
    public float detectionInterval = 0.35f;

    [Header("Target Locking & LoS")]
    [Tooltip("How long (seconds) the AI commits to a target before considering switching.")]
    public float targetLockDuration = 1.6f;
    [Tooltip("A new candidate target must be this many metres closer than the current one to steal focus.")]
    public float targetSwitchHysteresis = 2.0f;
    [Tooltip("Extra metres added to attackRadius when deciding it is time to swing.")]
    public float attackRangePadding = 0.35f;
    [Tooltip("Extra metres beyond attack radius before the attacker breaks off to chase again (hysteresis).")]
    public float breakAttackPadding = 0.9f;
    [Tooltip("Eye height used for line-of-sight raycasts.")]
    public float lineOfSightEyeHeight = 1.2f;
    [Tooltip("Layers that block line of sight. Leave empty to disable LoS checks (always visible).")]
    public LayerMask lineOfSightBlockers = 0;
    [Tooltip("Max seconds the 'last attacker' retaliation priority remains hot.")]
    public float retaliationMemory = 6f;

    [Header("AI / FSM")]
    [Tooltip("Half-angle (degrees) of the horizontal vision cone for spotting threats.")]
    public float fieldOfViewAngle = 120f;
    [Tooltip("Below this fraction of max HP the enemy enters Evade.")]
    [Range(0.05f, 0.95f)]
    public float lowHealthPercent = 0.25f;
    [Tooltip("Nav velocity below this while chasing counts toward stuck recovery.")]
    public float stuckVelocityThreshold = 0.05f;
    [Tooltip("Seconds at low velocity with an active path before StuckRecovery.")]
    public float stuckTimeThreshold = 1.5f;
    [Tooltip("How often to revalidate NavMesh path and refresh chase destination.")]
    public float repathInterval = 0.45f;
    [Tooltip("Seconds in Idle before starting Patrol.")]
    public float idleToPatrolDelay = 0.55f;
    [Tooltip("Max degrees off-forward allowed before swinging at the target.")]
    public float attackFacingAngle = 50f;
    public bool debugAI = false;
    public bool debugAICombat = false;

    [Header("Flocking (group steering on NavMesh)")]
    [Tooltip("Steer away from other enemies / player when closer than this (horizontal).")]
    public float separationRadius = 1.45f;
    [Tooltip("Neighbour enemies within this radius contribute to alignment.")]
    public float alignmentRadius = 3f;
    [Tooltip("Neighbour enemies within this radius contribute to cohesion centroid.")]
    public float cohesionRadius = 4f;
    [Tooltip("Weight for separation steering.")]
    public float separationWeight = 1.6f;
    [Tooltip("Weight for alignment steering (match neighbour velocity).")]
    public float alignmentWeight = 0.4f;
    [Tooltip("Weight for cohesion steering toward group centroid.")]
    public float cohesionWeight = 0.5f;
    [Tooltip("Cap on combined flock steering magnitude (world units per second scale).")]
    public float maxFlockSteering = 1.2f;

    [Header("Spawn Protection")]
    [Tooltip("Seconds after spawn during which the enemy ignores incoming damage. " +
             "Prevents enemies dying instantly to AoE / spawn-overlap stomps. " +
             "0 = disabled. Keep small (1.0–2.0s).")]
    public float spawnProtectionDuration = 1.25f;
    private float _spawnTime = -1f;
    private float _spawnY = float.NaN; // captured in Start for rooftop watchdog

    [Header("Aggression Tuning (2026-05-21)")]
    [Tooltip("Multiplier applied to chaseSpeed at runtime. 1.0 = unchanged, 1.2 = 20% snappier closing.")]
    [Range(0.5f, 2.0f)] public float chaseSpeedMultiplier = 1.10f;
    [Tooltip("Extra multiplier applied when target distance > farTargetBurstDistance, so enemies don't drag at long range.")]
    [Range(1.0f, 2.0f)] public float farTargetBurstMultiplier = 1.25f;
    [Tooltip("Distance (m) beyond which the burst multiplier kicks in.")]
    public float farTargetBurstDistance = 12f;
    [Tooltip("Multiplier on NavMeshAgent.angularSpeed so enemies turn toward the player without spinning.")]
    [Range(0.5f, 3.0f)] public float turnResponsiveness = 1.30f;
    [Tooltip("Multiplier on the Animator.speed so locomotion cycles read snappier without changing transitions.")]
    [Range(0.5f, 1.5f)] public float animatorSpeedMultiplier = 1.08f;

    [Header("Rooftop / Stuck Watchdog (2026-05-21)")]
    [Tooltip("If enemy.Y rises this far above its spawn Y, it is treated as 'on a rooftop' and warped back down.")]
    public float watchdogMaxYAboveSpawn = 3.5f;
    [Tooltip("Seconds the enemy may remain in an invalid high position before being warped back.")]
    public float watchdogInvalidHighSeconds = 2.5f;
    [Tooltip("Seconds with no path progress while a target is present before forcing a repath / relocate.")]
    public float watchdogStuckPathSeconds = 3.5f;
    [Tooltip("Logs watchdog actions (rooftop warps, stuck recoveries). Off in shipped builds.")]
    public bool debugWatchdog = false;
    [Header("Stability Recovery")]
    [Tooltip("Minimum seconds between automatic recovery warps (prevents warp spam).")]
    public float recoveryWarpCooldown = 2.5f;
    [Tooltip("Seconds allowed on a partial NavMesh path before forcing repath/reposition.")]
    public float partialPathMaxSeconds = 4f;
    [Tooltip("Max vertical metres above spawn Y for chase destinations (rooftop guard).")]
    public float maxChaseVerticalDelta = 3.5f;
    [Tooltip("Seconds without meaningful motion before forcing target reacquisition.")]
    public float idleMotionTimeout = 2.2f;
    private float _highSinceTime = -1f;
    private float _lowProgressSinceTime = -1f;
    private float _partialPathSinceTime = -1f;
    private float _idleMotionSinceTime = -1f;
    private float _lastRecoveryWarpTime = -999f;
    private Vector3 _lastValidNavPosition;
    private bool _hasLastValidNavPosition;
    private float _staticClipSinceTime = -1f;
    private float _navValidateTimer;
    private Vector3 _watchdogLastPosition;
    private float _aiBuildLogTimer;
    private float _noPathReacquireSince = -1f;
    private bool _authorityLogged;
    private bool _destinationSuccessLogged;
    private int _staticFrameCount;
    private Vector3 _lastAntiFreezePosCheck;
    private float _enemyMoveDebugTimer;

    [Header("Hit Reaction")]
    public float flinchDuration = 0.25f;
    public Color hitFlashColor = new Color(1f, 0.3f, 0.3f, 1f);
    public AudioClip hitSound;
    public AudioClip deathSound;

    [Header("Combat Voice SFX")]
    public AudioClip[] hurtSounds;
    public AudioClip[] deathSounds;
    private float _lastVoiceTime = -100f;

    [Tooltip("Damage-window start after attack trigger (seconds).")]
    public float attackHitboxWindup = 0.12f;
    [Tooltip("How long the weapon hitbox remains active during attack (seconds).")]
    public float attackHitboxActiveTime = 0.3f;

    [Header("Death")]
    [Tooltip("Seconds the death animation plays before ragdoll activates.")]
    public float deathAnimDuration = 1.5f;
    [Tooltip("Seconds before corpse is cleaned up (0 = never).")]
    public float corpseLifetime    = 15f;
    [Tooltip("Upward force added when ragdolling.")]
    public float deathPopForce     = 2f;
    [Tooltip("How long ragdoll remains before this enemy object is destroyed.")]
    public float ragdollVisibleDuration = 5f;

    [Header("Patrol")]
    [Tooltip("When no target is found, enemy roams inside this radius.")]
    public float patrolRadius = 12f;
    [Tooltip("How often to pick a new random patrol point.")]
    public float patrolRetargetInterval = 2.25f;

    [Header("OffMesh Link Jump")]
    [Tooltip("Duration used to traverse jump links manually.")]
    public float offMeshJumpDuration = 0.45f;
    [Tooltip("Extra arc height while crossing OffMeshLinks.")]
    public float offMeshJumpHeight = 1.2f;

    private AIStateId _state = AIStateId.Idle;
    private AIStateMachine _aiFsm;

    private NavMeshAgent _agent;
    private Animator     _anim;
    private AudioSource  _audio;
    private Rigidbody    _rb;
    private int          _currentHealth;

    /// <summary>Read-only health for UI / combat diagnostics.</summary>
    public int CurrentHealth => _currentHealth;

    /// <summary>Returns the level of the currently equipped weapon, falling back to current game level.</summary>
    public int GetEquippedWeaponLevel() => _equippedWeaponLevel > 0 ? _equippedWeaponLevel : (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1);

    // ── Public hooks for the tactical brain (EnemyTacticalBrain) ────────────
    // Surgical surface only — the brain *reads* these to make decisions and
    // calls SuggestTarget(...) to nudge target selection. It never reaches
    // into the state machine. OnDamaged fires from ReceiveDamage so the brain
    // can react to incoming hits without polling every frame.
    public UnityEngine.AI.NavMeshAgent Agent => _agent;
    public Transform CurrentTarget => _target;
    public bool AttackInProgress => _attackInProgress;
    public float AttackTimer
    {
        get => _attackTimer;
        set => _attackTimer = value;
    }
    public float IdleTimer
    {
        get => _idleTimer;
        set => _idleTimer = value;
    }
    public float PatrolTimer
    {
        get => _patrolTimer;
        set => _patrolTimer = value;
    }
    public float IdleToPatrolDelay => idleToPatrolDelay;
    public bool IsTraversingOffMeshLink => _isTraversingOffMeshLink;
    public event System.Action<GameObject> OnDamaged;
    public void SuggestTarget(Transform t)
    {
        if (t == null || t == transform) return;
        IDamageable d = t.GetComponentInParent<IDamageable>();
        if (d == null || !d.IsAlive) return;
        _target = t;
        _targetLockTimer = Time.time + targetLockDuration;
        if (!CanEngageChase(_target))
            return;
        InterruptPassiveBehavior();
        if (_state != AIStateId.Chase && _state != AIStateId.Approach && _state != AIStateId.Attack
            && _state != AIStateId.Jumping && _state != AIStateId.Dead)
            TransitionTo(AIStateId.Chase);
    }

    // CharacterController is intentionally disabled at runtime — kept as a
    // reference only so the jump system can toggle it during the arc.
    private CharacterController _controller;
    private float        _attackTimer;
    private Transform    _target;
    private bool         _playerDamagedThisLife;
    private float        _noTargetTimer;

    // Multiplayer Co-op: when set, bots prioritise these human players over other bots.
    private System.Collections.Generic.List<Transform> _mpHumanTargets;
    private bool         _killedByPlayer;
    private RagdollController _ragdoll;
    private Vector3      _lastHitDirection;
    private Rigidbody[]  _ragdollBodies;
    private CapsuleCollider _mainCapsuleCollider;

    // Flinch
    private float _flinchTimer;

    // FFA: track whoever last damaged this enemy so we can retaliate
    private Transform _lastAttacker;
    private float     _lastAttackerTime = -999f;

    // Target lock / commitment timer
    private float _targetLockTimer;

    // Coroutine-based target scanning
    private Coroutine _scanCoroutine;

    // Visual feedback
    private Renderer[] _renderers;
    private Color[]    _originalColors;
    private float      _flashTimer;
    private const float FlashDuration = 0.15f;

    // Jump state
    private float _stuckTimer;
    private bool  _isGrounded = true;
    private AIStateId _preJumpState;
    private float _jumpTimer;       // safety: max time allowed in Jumping state
    private const float MaxJumpDuration = 2.5f;

    private HashSet<int> _animParameterHashes;
    private Transform _activeWeaponSocket;
    private Transform _activeWeaponHandBone;
    private GameObject _equippedWeaponPrefab;
    private int _equippedWeaponLevel = -1;
    private Quaternion _weaponBaseLocalRot = Quaternion.identity;
    private PlayerController _cachedPlayer;
    private bool _weaponAttachInProgress;
    private float      _combatReadyBlend;
    private Quaternion _weaponAttachLocalRot;
    private GameObject _weaponAttachCachedObj;
    private float _patrolTimer;
    private bool _isTraversingOffMeshLink;
    private float _flowFieldSteerTimer;
    private Coroutine _attackHitboxRoutine;
    private Coroutine _katanaAttackResolveRoutine;
    private WeaponHitbox _equippedWeaponHitbox;
    private bool _attackInProgress;
    private bool _attackSwingLanded;
    private float _repathTimer;
    private float _chaseGateTimer;
    private Transform _chaseGateTarget;
    private bool _chaseGateResult;
    private float _immediateCombatInterruptTimer;
    private float _idleTimer;
    private float _navStuckTimer;
    private AIStateId _resumeAfterStuck = AIStateId.Patrol;
    private NavMeshPath _pathScratch;
    private Transform _cachedPlayerTransform;

    /// <summary>Duplicate root filtering for OverlapSphere hits (multi-collider rigs).</summary>
    private readonly HashSet<int> _flockOverlapSeenRoots = new HashSet<int>();

    // Position-delta velocity (same technique the PlayerController uses)
    private Vector3 _lastFramePosition;

    // Animator hashes
    private static readonly int HashSpeed     = Animator.StringToHash("Speed");
    private static readonly int HashVelX      = Animator.StringToHash("VelocityX");
    private static readonly int HashVelZ      = Animator.StringToHash("VelocityZ");
    private static readonly int HashAttack    = Animator.StringToHash("Attack");
    private static readonly int HashHit       = Animator.StringToHash("Hit");
    private static readonly int HashGrounded  = Animator.StringToHash("IsGrounded");
    private const string WeaponSocketName     = "__EnemyWeaponSocket";
    private static readonly Color[] EnemyTintPalette =
    {
        new Color(0.75f, 0.22f, 0.20f, 1f),
        new Color(0.20f, 0.58f, 0.28f, 1f),
        new Color(0.24f, 0.34f, 0.78f, 1f),
        new Color(0.82f, 0.56f, 0.18f, 1f),
        new Color(0.55f, 0.24f, 0.72f, 1f),
        new Color(0.18f, 0.62f, 0.68f, 1f),
    };

    // ── Unity lifecycle ──────────────────────────────────────────────────────
    // ── Global one-time setup ─────────────────────────────────────────────────
    // Layer collision matrix is a project-wide setting. Doing this once from
    // any character's Awake guarantees enemies / players collide regardless of
    // whatever the scene was authored with. Calling IgnoreLayerCollision(...,
    // false) for kinematic-vs-kinematic is technically a no-op (kinematic
    // bodies don't push each other apart), but the flag still affects trigger
    // events and OverlapSphere queries we rely on for the manual separation
    // push below.
    private static bool _characterLayerCollisionEnsured;

    internal static void EnsureCharacterLayerCollision()
    {
        if (_characterLayerCollisionEnsured) return;
        _characterLayerCollisionEnsured = true;

        int enemiesLayer  = LayerMask.NameToLayer("Enemies");
        int playerLayer   = LayerMask.NameToLayer("Player");
        int characterLayer = LayerMask.NameToLayer("Character");
        int hittableLayer = LayerMask.NameToLayer("Hittable");

        if (enemiesLayer >= 0)
            Physics.IgnoreLayerCollision(enemiesLayer, enemiesLayer, false);
        if (characterLayer >= 0)
            Physics.IgnoreLayerCollision(characterLayer, characterLayer, false);
        if (playerLayer >= 0 && enemiesLayer >= 0)
            Physics.IgnoreLayerCollision(playerLayer, enemiesLayer, false);
        if (hittableLayer >= 0)
            Physics.IgnoreLayerCollision(hittableLayer, hittableLayer, false);
    }

    private void Awake()
    {
        EnsureCharacterLayerCollision();

        // Hittable is the single deterministic melee query layer.
        int targetLayer = LayerMask.NameToLayer("Hittable");
        if (targetLayer < 0) targetLayer = LayerMask.NameToLayer("Character");
        if (targetLayer >= 0) SetLayerRecursive(gameObject, targetLayer);

        _agent         = GetComponent<NavMeshAgent>();
        _anim          = GetComponentInChildren<Animator>();
        _currentHealth = maxHealth;
        EnsureAnimationEventSink();
        CacheAnimatorParameters();

        // Audio
        _audio = GetComponent<AudioSource>();
        if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
        _audio.spatialBlend = 1f;
        _audio.playOnAwake  = false;

        // ── NavMeshAgent ──────────────────────────────────────────────────────
        // Agent owns position; yaw is smoothed in LateUpdate toward velocity / aim.
        // With autoBraking DISABLED the enemy holds closing speed until melee hysteresis.
        if (_agent != null)
            ConfigureAgent();

        // ── Disable any CharacterController so it never fights the agent ─────
        _controller = GetComponent<CharacterController>();
        if (_controller != null) _controller.enabled = false;

        // ── Animator: disable root motion so NavMeshAgent drives all movement ─
        if (_anim != null)
            _anim.applyRootMotion = false;

        // ── Rigidbody for jump physics ────────────────────────────────────────
        // Kinematic by default so NavMeshAgent controls movement.
        // Becomes non-kinematic only during the jump arc.
        _rb = GetComponent<Rigidbody>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
        _rb.mass              = 70f;
        _rb.linearDamping     = 0.5f;
        _rb.angularDamping    = 4f;
        _rb.interpolation     = RigidbodyInterpolation.Interpolate;
        _rb.constraints       = RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;
        _rb.isKinematic       = true;  // NavMeshAgent owns movement normally

        // Freeze ALL bone rigidbodies before any FixedUpdate fires to prevent ragdoll explosion at spawn.
        foreach (Rigidbody bone in GetComponentsInChildren<Rigidbody>(true))
        {
            if (bone == null || bone == _rb) continue;
            bone.isKinematic = true;
            bone.useGravity = false;
        }

        // Cache renderers for hit flash (URP-compatible: prefer _BaseColor over _Color)
        _renderers      = GetComponentsInChildren<Renderer>(true);
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            _originalColors[i] = GetMaterialBaseColor(_renderers[i].material);
        }

        // ── Detection mask ────────────────────────────────────────────────────
        // Use ALL physics layers so the player is always detectable regardless
        // of which Unity layer they are assigned to.  The IDamageable check in
        // FindFfaTarget() already filters out non-damageable objects, so it is
        // safe to cast against everything.  Restricting to a single named layer
        // was the primary cause of the "enemy stands still / brain-dead" bug —
        // if the player is on Default (layer 0) instead of "Character", the
        // OverlapSphere returned zero hits and the enemy never acquired a target.
        detectionMask = ~0;
        LoadSavedRuntimeGripValues();

        _pathScratch = new NavMeshPath();
        _aiFsm = new AIStateMachine();
        _aiFsm.Bind(this);
    }

    private void Start()
    {
        CombatVoiceSfx voice = CombatVoiceSfx.GetOrAdd(gameObject);
        voice.ApplyInspectorClips(hurtSounds, deathSounds, hitSound, deathSound);

        detectionInterval = Mathf.Clamp(detectionInterval, 0.05f, 2.0f);
        detectionRadius = Mathf.Clamp(detectionRadius, 20f, 500f);
        aggressiveScanRadius = Mathf.Max(aggressiveScanRadius, Mathf.Max(detectionRadius * 2f, 60f));
        attackCooldown = Mathf.Min(attackCooldown, 0.65f);
        // Keep a real commitment window: too short (the old 0.15s cap) and the
        // AI re-snaps to whoever is marginally nearest every scan, which makes
        // every enemy dogpile the player. Clamp to a sane FFA range instead.
        targetLockDuration = Mathf.Clamp(targetLockDuration, 0.6f, 3f);
        targetSwitchHysteresis = Mathf.Max(0f, targetSwitchHysteresis);

        // LevelBuilder assigns maxHealth/chaseSpeed after AddComponent(), so
        // re-apply runtime state here once the spawner has finished tuning us.
        // Veteran/Hard custom matches scale enemy HP to make survivability
        // match the elevated damage/speed.
        if (GameManager.Instance != null)
        {
            float healthMul = Mathf.Max(0.25f, GameManager.Instance.GetEnemyHealthMultiplier());
            maxHealth = Mathf.Max(1, Mathf.RoundToInt(maxHealth * healthMul));
        }
        _currentHealth = maxHealth;
        _spawnTime = Time.time;
        _spawnY = transform.position.y;
        _watchdogLastPosition = transform.position;
        _lastValidNavPosition = transform.position;
        _hasLastValidNavPosition = true;
        ConfigureAgent();
        ApplyAggressionTuning();

        // Minimal runtime safety: ensure agent is enabled and actually sits on the NavMesh.
        EnsureAgentOnNavMesh();

        // Minimal runtime safety: try to acquire a target immediately (don’t wait a full scan tick).
        EvaluateTargets();

        _lastFramePosition = transform.position;
        _lastAntiFreezePosCheck = transform.position;
        EnsureProperMaterial(gameObject);
        AssignMaterial();

        RegisterForMatchStats();

        _ragdoll = GetComponent<RagdollController>();
        _mainCapsuleCollider = GetComponent<CapsuleCollider>();

        // Cache all bone rigidbodies once and keep ragdoll disabled by default.
        _ragdollBodies = GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < _ragdollBodies.Length; i++)
        {
            Rigidbody boneBody = _ragdollBodies[i];
            if (boneBody == null || boneBody == _rb)
                continue;

            boneBody.isKinematic = true;
            boneBody.useGravity = false;
            boneBody.detectCollisions = true;
        }

        // Start the coroutine-based target scanner
        if (isActiveAndEnabled)
            _scanCoroutine = StartCoroutine(TargetScanLoop());

        _patrolTimer = Random.Range(0f, patrolRetargetInterval);
        _repathTimer = Random.Range(0f, repathInterval);
        _idleTimer = 0f;
        CachePlayerTransform();
        _aiFsm.SetState(_state, true);
    }

    public void ClearTarget() => _target = null;

    public void SyncAIStateId(AIStateId id) => _state = id;

    private void CachePlayerTransform()
    {
        PlayerHealth ph = Object.FindFirstObjectByType<PlayerHealth>();
        _cachedPlayerTransform = ph != null ? ph.transform : null;
    }

    private Transform GetCachedPlayerTransform()
    {
        if (_cachedPlayerTransform == null)
            CachePlayerTransform();
        return _cachedPlayerTransform;
    }

    private bool _navMeshErrorLogged;
    private float _nextForceRetargetTime;

    private bool EnsureAgentOnNavMesh()
    {
        if (_agent == null) _agent = GetComponent<NavMeshAgent>();
        if (_agent == null) return false;

        if (!_agent.enabled)
            _agent.enabled = true;

        if (_agent.isOnNavMesh)
            return true;

        // Search downward first (same Y level ±4m) to avoid snapping to elevated
        // surfaces (rooftops, bridges) that happen to be within a large radius.
        Vector3 searchOrigin = transform.position;
        float searchRadius = 4f;
        if (!float.IsNaN(_spawnY))
        {
            // Bias toward spawn Y so the enemy returns to ground level, not a rooftop.
            searchOrigin.y = _spawnY;
            searchRadius = Mathf.Max(4f, Mathf.Abs(transform.position.y - _spawnY) + 2f);
            searchRadius = Mathf.Min(searchRadius, 8f);
        }
        Vector3 playerNav = GetCachedPlayerTransform() != null
            ? GetCachedPlayerTransform().position
            : searchOrigin;
        if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(searchOrigin, playerNav, 8f, out Vector3 valid))
        {
            transform.position = valid;
            _agent.Warp(valid);
            _spawnY = valid.y;
            Debug.Log("[AINav] warped enemy " + name + " to NavMesh position");
        }
        else if (NavMesh.SamplePosition(searchOrigin, out NavMeshHit hit, searchRadius, NavMesh.AllAreas))
        {
            transform.position = hit.position;
            _agent.Warp(hit.position);
            Debug.Log("[AINav] warped enemy " + name + " to NavMesh position");
        }

        if (!_agent.isOnNavMesh && !_navMeshErrorLogged)
        {
            _navMeshErrorLogged = true;
            Debug.LogError($"[EnemyController] {name} is not on NavMesh after SamplePosition+Warp. pos={transform.position}", this);
            Debug.LogError("[AINav] ERROR no NavMesh found near enemy " + name);
        }

        return _agent.isOnNavMesh;
    }

    private void OnEnable()
    {
        if (!s_aliveEnemies.Contains(this))
            s_aliveEnemies.Add(this);
    }

    private void OnDisable()
    {
        s_aliveEnemies.Remove(this);

        if (_scanCoroutine != null)
        {
            StopCoroutine(_scanCoroutine);
            _scanCoroutine = null;
        }
    }

    public static void CopyAliveEnemies(List<EnemyController> buffer)
    {
        if (buffer == null) return;
        buffer.Clear();
        for (int i = 0; i < s_aliveEnemies.Count; i++)
        {
            EnemyController e = s_aliveEnemies[i];
            if (e != null && e.IsAlive)
                buffer.Add(e);
        }
    }

    public static PlayerController ResolveScenePlayer()
    {
        float t = Time.unscaledTime;
        if (s_scenePlayer == null || t - s_scenePlayerRefreshTime > 1.5f)
        {
            s_scenePlayer = Object.FindFirstObjectByType<PlayerController>(FindObjectsInactive.Exclude);
            s_scenePlayerRefreshTime = t;
        }

        return s_scenePlayer;
    }

    private static Transform ResolveScenePlayerTransform()
    {
        PlayerController pc = ResolveScenePlayer();
        return pc != null ? pc.transform : null;
    }

    private bool HasAiDecisionAuthority()
    {
#if PUN_2_OR_NEWER
        return !MultiplayerMode.IsMultiplayer || PhotonNetwork.IsMasterClient;
#else
        return true;
#endif
    }

    private void LogAiAuthorityOnce()
    {
        if (_authorityLogged)
            return;

        _authorityLogged = true;
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer && PhotonNetwork.IsMasterClient)
            Debug.Log("[AIAuth] MasterClient controls enemy " + name);
        else if (MultiplayerMode.IsMultiplayer)
            Debug.Log("[AIAuth] Non-master display-only enemy " + name);
#endif
    }

    private void Update()
    {
        if (_state == AIStateId.Dead) return;
        LogAiAuthorityOnce();

        if (!HasAiDecisionAuthority())
        {
            SyncAnimator();
            return;
        }

        // End-match cinematic — freeze AI: stop the agent in place, drop the
        // animator into idle, but keep the visible mesh updated so the orbit
        // camera sees a clean podium pose.
        if (EndMatchCinematic.GameplayLocked)
        {
            if (_agent != null && _agent.enabled && !_agent.isStopped) _agent.isStopped = true;
            SyncAnimator();
            return;
        }

        // Warp logic removed per user request

        // Rooftop / stuck / NavMesh watchdog — surgical, runs once per frame, no allocations.
        if (ShouldRunWatchdogThisFrame())
        {
            TickWatchdog();
            RecoverOnlyIfOutOfMap();
        }

        TryImmediateCombatInterrupt();
        TickCombatDrive();

        if (_target == null)
        {
            _noTargetTimer += Time.deltaTime;
            if (_noTargetTimer >= 0.05f)
            {
                _noTargetTimer = 0f;
                EvaluateTargets();
            }
        }
        else
        {
            _noTargetTimer = 0f;
        }

        TryGlobalAiTransitions();

        _attackTimer -= Time.deltaTime;

        if (_flashTimer > 0f)
        {
            _flashTimer -= Time.deltaTime;
            if (_flashTimer <= 0f) RestoreOriginalColors();
        }

        if (_state == AIStateId.Jumping)
            UpdateJumping();
        else if (_state != AIStateId.Dead)
            _aiFsm.Tick();

        EnforceCombatMatrix();

        // ─ Safety kick: if we have a live target but the agent has no path
        // (FSM stalled in Idle/Patrol on the very first frames before its
        // ticker schedules a destination), push SetDestination directly. This
        // is what unfreezes the "enemies stand like statues" symptom.
        if (_target != null && IsHostileAlive(_target)
            && _state != AIStateId.Dead && _state != AIStateId.Jumping
            && _state != AIStateId.Attack && _state != AIStateId.Flinch
            && _agent != null && _agent.enabled && _agent.isOnNavMesh)
        {
            if (_agent.isStopped) _agent.isStopped = false;
            if (!_agent.pathPending && (!_agent.hasPath || (_agent.destination - _target.position).sqrMagnitude > 1f))
            {
                Vector3 dest = _target.position;
                if ((_agent.destination - dest).sqrMagnitude > 0.25f)
                    _agent.SetDestination(dest);
            }
            EmitOneShotAiDiagnostic();
        }

        if (ShouldSyncAnimatorThisFrame())
            SyncAnimator();

        TickAntiFreeze();
        if (debugEnemyMovement)
            TickDebugLog();
    }

    private bool _aiDiagEmitted;
    private void EmitOneShotAiDiagnostic()
    {
        if (_aiDiagEmitted) return;
        _aiDiagEmitted = true;
        string targetName = _target != null ? _target.name : "<null>";
        bool hasPath = _agent != null && _agent.hasPath;
        bool onNav   = _agent != null && _agent.isOnNavMesh;
        Debug.Log($"[AI] {name} state={_state} target={targetName} agentOnNavMesh={onNav} hasPath={hasPath}", this);
    }

    private void FixedUpdate()
    {
        if (!HasAiDecisionAuthority())
            return;

        if (_target != null && IsHostileAlive(_target) && _state != AIStateId.Dead && _state != AIStateId.Jumping)
            AIMotor.EnforceSprintPursuit(this);

        ApplyFlockingSteering();
        EnforceStaticGeometryConstraint();
    }

    private void EnforceCombatMatrix()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Jumping || EndMatchCinematic.GameplayLocked)
            return;

        float scanRadius = Mathf.Max(detectionRadius, aggressiveScanRadius);
        bool currentTargetLive = _target != null && IsHostileAlive(_target);
        bool attackLocked = _state == AIStateId.Attack && _attackInProgress && currentTargetLive;
        if (!attackLocked)
        {
            Transform closest = AISensing.FindClosestHostile(this, scanRadius);
            if (closest != null && IsHostileAlive(closest))
                _target = closest;
        }

        if (_target == null || !IsHostileAlive(_target))
            return;

        if (!PrepareAgentForCombatLocomotion())
            return;

        float combatDistance = GetCombatDistanceToTarget();
        float attackRangeGate = Mathf.Max(0.65f, meleeAttackRange);

        if (combatDistance > attackRangeGate)
        {
            AbortActiveAttack();
            if (_state == AIStateId.Attack || _state == AIStateId.Idle || _state == AIStateId.Patrol
                || _state == AIStateId.Evade || _state == AIStateId.StuckRecovery || _state == AIStateId.Flinch)
                TransitionTo(AIStateId.Chase);

            AIMotor.EnforceSprintPursuit(this);
            return;
        }

        if (_state != AIStateId.Attack && CanBeginMeleeAttack() && !_attackInProgress)
            TransitionTo(AIStateId.Attack);
    }

    public bool IsHostileAlive(Transform t)
    {
        if (t == null)
            return false;

        IDamageable dmg = t.GetComponentInParent<IDamageable>();
        return dmg != null && dmg.IsAlive && dmg.gameObject != gameObject;
    }

    public float GetCombatDistanceToTarget()
    {
        if (_target == null)
            return float.MaxValue;

        Vector3 delta = _target.position - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    public bool PrepareAgentForCombatLocomotion()
    {
        if (_agent == null)
            return false;

        if (!_agent.enabled)
            _agent.enabled = true;

        if (!_agent.isOnNavMesh)
            EnsureAgentOnNavMesh();

        if (!_agent.isOnNavMesh)
            return false;

        _agent.isStopped = false;
        _agent.updatePosition = true;
        _agent.updateRotation = false;
        _agent.autoBraking = false;
        AIMotor.SuppressRootMotion(this);
        AIMotor.ApplyBlackPlayerLocomotion(_agent, this);
        return true;
    }

    private bool ShouldRunWatchdogThisFrame()
    {
        if ((Time.frameCount + GetInstanceID()) % 2 == 0)
            return true;

        return _state == AIStateId.Chase || _state == AIStateId.Attack || _target != null;
    }

    private bool ShouldSyncAnimatorThisFrame()
    {
        if (_state == AIStateId.Chase || _state == AIStateId.Attack || _state == AIStateId.Evade)
            return true;

        if ((Time.frameCount + GetInstanceID()) % 2 == 0)
            return true;

        Transform player = ResolveScenePlayerTransform();
        if (player == null)
            return true;

        return (player.position - transform.position).sqrMagnitude < 900f;
    }

    private void TryGlobalAiTransitions()
    {
        if (_state == AIStateId.Dead || EndMatchCinematic.GameplayLocked)
            return;

        if (_target != null && IsHostileAlive(_target) && GetCombatDistanceToTarget() > meleeAttackRange)
            return;

        if (ShouldEnterEvade())
        {
            TransitionTo(AIStateId.Evade);
            return;
        }

        if (ShouldEnterStuckRecovery())
            TransitionTo(AIStateId.StuckRecovery);
    }

    private bool ShouldEnterEvade()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Evade || _state == AIStateId.StuckRecovery ||
            _state == AIStateId.Jumping)
            return false;

        if (maxHealth <= 0)
            return false;

        if (_target != null && IsTargetValid(_target))
        {
            float engageDist = Vector3.Distance(transform.position, _target.position);
            if (engageDist <= detectionRadius)
                return false;
        }

        return (float)_currentHealth / maxHealth <= lowHealthPercent;
    }

    private bool ShouldEnterStuckRecovery()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.StuckRecovery || _state == AIStateId.Jumping ||
            _state == AIStateId.Attack || _state == AIStateId.Flinch || _state == AIStateId.Idle)
            return false;

        if (_target != null && IsHostileAlive(_target) && GetCombatDistanceToTarget() > meleeAttackRange)
            return false;

        if (!IsAgentReady())
            return false;

        if (!_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.08f)
        {
            _navStuckTimer = 0f;
            return false;
        }

        if (_agent.velocity.magnitude > stuckVelocityThreshold)
        {
            _navStuckTimer = 0f;
            return false;
        }

        _navStuckTimer += Time.deltaTime;
        return _navStuckTimer >= stuckTimeThreshold;
    }

    private void LateUpdate()
    {
        ApplyMovementAlignedRotation();

        if (stabilizeWeaponSocketAgainstHandPose &&
            _activeWeaponSocket != null && _activeWeaponHandBone != null)
        {
            _activeWeaponSocket.localRotation =
                Quaternion.Inverse(_activeWeaponHandBone.localRotation) *
                Quaternion.Euler(weaponSocketLocalEulerAngles);
        }

        // ── Level 2 katana grip — carry pose only (not during attack) ───────
        //
        // During the ATTACK state the weapon follows the animated hand bone
        // naturally through the parent chain so the swing plays correctly.
        //
        // During every OTHER state (idle / walk / chase / flinch) we lock the
        // carry pose to match the player's blade angle exactly:
        //
        //   playerCharSpaceRot  = Inverse(player.root) * player.weapon.rotation
        //   enemy.weapon.rotation = enemy.root * playerCharSpaceRot * extraOffset
        //
        // katanaCarryExtraEuler lets designers tweak the raise angle in Play Mode.
        if (_equippedWeaponLevel == 2 &&
            equippedWeaponObject  != null &&
            _activeWeaponHandBone != null &&
            _state != AIStateId.Attack)          // ← let the swing anim run freely
        {
            if (_cachedPlayer == null)
                _cachedPlayer = ResolveScenePlayer();

            // ── POSITION: pin the weapon pivot to the hand bone + grip offset ──
            // This ensures the handle is physically inside the palm regardless
            // of which socket or local-position offset was set at attach-time.
            equippedWeaponObject.transform.position =
                _activeWeaponHandBone.position
                + _activeWeaponHandBone.rotation * katanaHandGripOffset;

            // ── ROTATION: match the player's blade angle in character space ───
            if (_cachedPlayer != null && _cachedPlayer.equippedWeaponObject != null)
            {
                Quaternion playerCharSpaceRot =
                    Quaternion.Inverse(_cachedPlayer.transform.rotation)
                    * _cachedPlayer.equippedWeaponObject.transform.rotation;

                Quaternion extra = Quaternion.Euler(katanaCarryExtraEuler);

                equippedWeaponObject.transform.rotation =
                    transform.rotation * playerCharSpaceRot * extra;
            }
            else
            {
                // Fallback — no player in scene (multiplayer bot-only match).
                equippedWeaponObject.transform.rotation =
                    _activeWeaponHandBone.rotation * Quaternion.Euler(0f, 0f, 160f);
            }
        }

        // ── Level 12 saw — carry pose (idle/walk/chase, NOT during attack) ──────
        //
        // Goal: same orientation as the black player's saw, handle in the palm.
        //
        // Problem: player socket is under j_wrist_ri (Ronin); enemy socket is under
        // bip_hand_R (Crosby). Different skeletons → different hand bone positions
        // in character space, so a plain character-space position copy puts the saw
        // at the correct body-relative position for the PLAYER but not for Crosby's
        // proportions (hand ends up in the wrong place relative to bip_hand_R).
        //
        // Fix (two steps):
        //   1. ROTATION — character-space copy from the player (bone-axis-agnostic).
        //   2. POSITION — anchor the SAW'S HANDLE point to bip_hand_R in world space.
        //
        // The handle's position in the saw's own local space is derived from the
        // player's grip setup (localPosition / localRotation on j_wrist_ri):
        //
        //   Parent-child relation: saw.world = parent.world + parent.rot * localPos
        //   → parent.world = saw.world - parent.rot * localPos
        //   → handleInSawLocal = -(Inverse(localRot) * localPos)
        //
        // This is purely the saw's local geometry — independent of which skeleton
        // holds it — so the same value works for both Ronin and Crosby.
        if (_equippedWeaponLevel == 12 &&
            equippedWeaponObject  != null &&
            _activeWeaponHandBone != null &&
            _state                != AIStateId.Attack)
        {
            if (_cachedPlayer == null)
                _cachedPlayer = ResolveScenePlayer();

            if (_cachedPlayer != null && _cachedPlayer.equippedWeaponObject != null)
            {
                // Step 1 — rotation: character-space copy from player (bone-independent).
                Quaternion playerCharSpaceRot =
                    Quaternion.Inverse(_cachedPlayer.transform.rotation)
                    * _cachedPlayer.equippedWeaponObject.transform.rotation;

                Quaternion sawWorldRot =
                    transform.rotation * playerCharSpaceRot * Quaternion.Euler(sawCarryExtraEuler);

                equippedWeaponObject.transform.rotation = sawWorldRot;

                // Step 2 — position: anchor handle to bip_hand_R.
                // Player setup: localPos=(-0.066,-0.39,0.044), localRot=Euler(-177.177,-175.886,88.481)
                // handleInSawLocal = -(Inverse(localRot) * localPos)
                Vector3 handleInSawLocal =
                    -(Quaternion.Inverse(Quaternion.Euler(-177.177f, -175.886f, 88.481f))
                      * new Vector3(-0.066f, -0.39f, 0.044f));

                equippedWeaponObject.transform.position =
                    _activeWeaponHandBone.position - sawWorldRot * handleInSawLocal;
            }
            else
            {
                // Fallback: no player in scene — anchor to hand bone with catalogue euler.
                equippedWeaponObject.transform.rotation =
                    _activeWeaponHandBone.rotation * Quaternion.Euler(-177.177f, -175.886f, 88.481f);
            }
        }

        // ── Level 9 axe — attack swing direction correction ──────────────────
        //
        // Crosby's bip_hand_R has different local axes than the player's j_wrist_ri.
        // The attack animation rotates the hand bone; on the player this produces a
        // correct forward swing, but on Crosby the same rotation produces a reversed
        // swing because the bone's local forward is mirrored.
        //
        // Fix: after the animator updates the hand bone each frame, multiply the
        // weapon's current localRotation by axeAttackSwingCorrection (default 180° Y)
        // to flip the swing back to the correct direction.
        //
        // This block only runs during the Attack state — idle grip is untouched.
        // Tune axeAttackSwingCorrection in the Inspector during Play Mode if needed.
        if (_equippedWeaponLevel == 9 &&
            equippedWeaponObject  != null &&
            _state                == AIStateId.Attack)
        {
            // SET to a fixed value each frame — never compound/multiply current
            // localRotation or it oscillates every frame (base → corrected → base...).
            // _weaponBaseLocalRot is the idle grip (0,180,90); axeAttackSwingCorrection
            // is applied on top once per frame so the result is always stable.
            equippedWeaponObject.transform.localRotation =
                _weaponBaseLocalRot * Quaternion.Euler(axeAttackSwingCorrection);
        }

        // ── Level 12 saw — attack swing direction correction ─────────────────
        //
        // Same bone-axis mismatch as Level 9 (bip_hand_R vs j_wrist_ri). The attack
        // animation rotates bip_hand_R so the swing appears reversed on Crosby.
        // Apply a stable fixed correction each frame during Attack only.
        //
        // We also correct localPosition so the handle stays in bip_hand_R's palm
        // (the position set at attach-time is tuned for j_wrist_ri and causes the
        // saw to "fly" during the swing if left uncorrected).
        //
        // Derivation — we want: saw.world.pos + saw.world.rot * handleInSawLocal = bip_hand_R
        //   → localPos = attackLocalRot * Inverse(playerLocalRot) * playerLocalPos
        //   where handleInSawLocal = -(Inverse(playerLocalRot) * playerLocalPos)
        if (_equippedWeaponLevel == 12 &&
            equippedWeaponObject  != null &&
            _state                == AIStateId.Attack)
        {
            Quaternion attackLocalRot = _weaponBaseLocalRot * Quaternion.Euler(sawAttackSwingCorrection);
            equippedWeaponObject.transform.localRotation = attackLocalRot;

            // Anchor the handle to bip_hand_R in local space.
            equippedWeaponObject.transform.localPosition =
                attackLocalRot
                * Quaternion.Inverse(Quaternion.Euler(-177.177f, -175.886f, 88.481f))
                * new Vector3(-0.066f, -0.39f, 0.044f);
        }

        // ── General carry-pose match (all levels except 2 & 12 already handled above) ──
        //
        // Goal: enemy weapon looks identical to the player's grip in every frame.
        //
        // Problem: Crosby (bip_hand_R) and Ronin (j_wrist_ri) have different local
        // bone axes. Applying the same localEuler produces a different world-space
        // blade direction on each rig.
        //
        // Fix: convert the player's weapon orientation into the player's character
        // root space, then re-apply that same character-space rotation on the enemy.
        // This is bone-axis-agnostic — it doesn't matter which skeleton either
        // character uses. Position is pinned to the enemy's own hand bone so arm
        // proportions are respected (Crosby's arm != Ronin's arm length).
        //
        // Excluded during Attack: the swing animation must run freely.
        // Excluded for level 2 (katana) and 12 (saw): dedicated blocks above.
        if (equippedWeaponObject  != null &&
            _activeWeaponHandBone != null &&
            _state                != AIStateId.Attack &&
            _equippedWeaponLevel  == 1)
        {
            if (_cachedPlayer == null)
                _cachedPlayer = ResolveScenePlayer();

            if (_cachedPlayer != null && _cachedPlayer.equippedWeaponObject != null)
            {
                // Character-space weapon rotation from the player (rig-independent).
                Quaternion playerCharSpaceRot =
                    Quaternion.Inverse(_cachedPlayer.transform.rotation)
                    * _cachedPlayer.equippedWeaponObject.transform.rotation;

                equippedWeaponObject.transform.rotation =
                    transform.rotation * playerCharSpaceRot;

                // Pin to the enemy's hand bone in world space.
                // Do NOT copy player's hand-relative position — skeletons differ in
                // arm length and palm offset, so the weapon must sit at the enemy's
                // own palm rather than at a position that makes sense only for Ronin.
                equippedWeaponObject.transform.position = _activeWeaponHandBone.position;
            }
        }
    }


    /// <summary>
    /// Smoothly aligns the body with horizontal NavMesh velocity so feet/locomotion match travel direction (no moonwalking).
    /// NavMeshAgent.updateRotation stays false — rotation is driven here via Slerp.
    /// </summary>
    private void ApplyMovementAlignedRotation()
    {
        if (_state == AIStateId.Dead || EndMatchCinematic.GameplayLocked)
            return;

        if (_state == AIStateId.Attack || _state == AIStateId.Flinch || _state == AIStateId.Jumping)
            return;

        if (_agent == null || !_agent.enabled)
            return;

        Vector3 flatVel = _agent.velocity;
        flatVel.y = 0f;

        if (flatVel.sqrMagnitude > 0.07f)
        {
            FaceDirection(flatVel);
            return;
        }

        if (_state == AIStateId.Chase && _target != null)
        {
            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.02f)
                FaceDirection(toTarget);
        }
    }

    // ── State handlers ────────────────────────────────────────────────────────
    //
    //  Philosophy: the NavMeshAgent owns EVERYTHING movement-related.
    //  State handlers are tiny — they only set destinations, toggle
    //  isStopped, and pick targets. No manual gravity, no CharacterController,
    //  no velocity smoothing. The agent's internal acceleration curve drives
    //  the feel (and we tune it aggressively in Awake for snappy reactions).
    //
    private float _noPathLedgeTimer;

    // Called every chase frame: if the agent has no valid path at all and the
    // target is below, try to drop off a ledge to reach the lower NavMesh island.
    public void CheckNoPathLedgeDrop()
    {
        if (!_isGrounded) return;
        if (_state == AIStateId.Jumping) return;
        if (!IsAgentReady()) return;

        // Only act when the agent genuinely has no path to the target
        bool noPath = !_agent.hasPath
                   || _agent.pathStatus == NavMeshPathStatus.PathPartial
                   || _agent.pathStatus == NavMeshPathStatus.PathInvalid;
        if (!noPath) return;

        // Target must be meaningfully below us
        if (_target == null || _target.position.y >= transform.position.y - 0.8f) return;

        _noPathLedgeTimer -= Time.deltaTime;
        if (_noPathLedgeTimer > 0f) return;
        _noPathLedgeTimer = 1.2f; // throttle: check at most once per 1.2 s

        TryLedgeDrop();
    }

    /// <summary>
    /// Throttled NavMesh validation / destination refresh (see <see cref="repathInterval"/>).
    /// </summary>
    public void TryRefreshChaseDestination()
    {
        _repathTimer -= Time.deltaTime;
        if (_repathTimer > 0f)
            return;

        _repathTimer = repathInterval;

        if (_target == null || !IsAgentReady())
            return;

        Vector3 dest = ClampChaseDestination(_target.position);
        if (!_agent.pathPending && _agent.hasPath && _agent.pathStatus != NavMeshPathStatus.PathInvalid)
        {
            float destDelta = (_agent.destination - dest).sqrMagnitude;
            if (destDelta < 2.25f && (_agent.remainingDistance > 0.35f || _agent.velocity.sqrMagnitude > 0.04f))
                return;
        }

        if (useFlowFieldNavigation && FlowFieldManager.Instance != null)
        {
            _flowFieldSteerTimer = 0f;
            DriveChaseWithFlowField();
        }
        else
        {
            NavMesh.CalculatePath(transform.position, _target.position, NavMesh.AllAreas, _pathScratch);

            if (_pathScratch.status != NavMeshPathStatus.PathInvalid)
            {
                _agent.SetDestination(dest);
                LogMoveDestination(dest);
            }
            else
            {
                Debug.Log("[AIMove] no path, reacquiring target");
                RepositionOnInvalidPath();
                if (debugAI)
                    Debug.Log($"[EnemyAI] {name} chase path {_pathScratch.status}, repositioning.", this);
            }

            return;
        }

        NavMesh.CalculatePath(transform.position, _target.position, NavMesh.AllAreas, _pathScratch);
        if (_pathScratch.status == NavMeshPathStatus.PathInvalid)
        {
            RepositionOnInvalidPath();
            if (debugAI)
                Debug.Log($"[EnemyAI] {name} flow chase goal unreachable ({_pathScratch.status}).", this);
        }
    }

    public void TickEvadeState()
    {
        if (HandleOffMeshLinkTraversal())
            return;

        float hpPct = (float)_currentHealth / Mathf.Max(1, maxHealth);
        if (hpPct > lowHealthPercent + 0.02f)
        {
            TransitionTo(IsTargetValid(_target) && CanEngageChase(_target) ? AIStateId.Chase : AIStateId.Patrol);
            return;
        }

        if (!IsAgentReady())
            return;

        _agent.isStopped = false;
        _agent.speed = moveSpeed;
        _agent.stoppingDistance = Mathf.Max(0.3f, attackRadius * 0.4f);

        Transform player = GetCachedPlayerTransform();
        if (player != null)
        {
            Vector3 away = transform.position - player.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.01f)
            {
                Vector3 fleeGoal = transform.position + away.normalized * 12f;
                if (NavMesh.SamplePosition(fleeGoal, out NavMeshHit hit, 14f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
            }
        }
        else
        {
            SetRandomPatrolDestination();
        }
    }

    public void TickStuckRecoveryState()
    {
        if (!IsAgentReady())
        {
            TransitionTo(_resumeAfterStuck);
            return;
        }

        _agent.ResetPath();
        _navStuckTimer = 0f;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit near, 3f, NavMesh.AllAreas))
        {
            if ((near.position - transform.position).sqrMagnitude > 0.04f)
                _agent.Warp(near.position);
        }

        Vector3 lateral = transform.position + Random.insideUnitSphere * 4f;
        lateral.y = transform.position.y;
        if (NavMesh.SamplePosition(lateral, out NavMeshHit dest, 8f, NavMesh.AllAreas))
            _agent.SetDestination(dest.position);

        if (debugAI)
            Debug.Log($"[EnemyAI] {name} stuck recovery → {_resumeAfterStuck}", this);

        TransitionTo(_resumeAfterStuck);
    }

    public void TickFlinchState()
    {
        _flinchTimer -= Time.deltaTime;

        if (IsAgentReady())
        {
            _agent.isStopped = true;
            _agent.velocity  = Vector3.zero;
        }

        if (_flinchTimer <= 0f)
            TransitionTo(IsHostileAlive(_target) ? AIStateId.Chase : AIStateId.Patrol);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FLOCKING — separation / alignment / cohesion as NavMeshAgent.Move offsets
    //  Single NonAlloc overlap per enemy per physics step (radius = max rule).
    //  Does not replace pathfinding; Chase/Patrol still own SetDestination.
    // ════════════════════════════════════════════════════════════════════════

    private static readonly Collider[] _flockColliderBuffer = new Collider[32];

    private void ApplyFlockingSteering()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Attack || _state == AIStateId.Flinch ||
            _state == AIStateId.Jumping || _state == AIStateId.StuckRecovery)
            return;

        if (EndMatchCinematic.GameplayLocked)
            return;

        Transform player = ResolveScenePlayerTransform();
        if (player != null)
        {
            float distSqr = (player.position - transform.position).sqrMagnitude;
            int phase = Time.frameCount + GetInstanceID();
            if (distSqr > 900f && phase % 4 != 0)
                return;
            if (distSqr > 2500f && phase % 2 != 0)
                return;
        }

        if (!IsAgentReady() || !_agent.isOnNavMesh || _agent.isStopped)
            return;

        float queryRadius = Mathf.Max(0.15f, Mathf.Max(separationRadius, Mathf.Max(alignmentRadius, cohesionRadius)));
        Vector3 origin = transform.position + Vector3.up * 0.9f;

        int hitCount = Physics.OverlapSphereNonAlloc(
            origin,
            queryRadius,
            _flockColliderBuffer,
            ResolveHittableMask(),
            QueryTriggerInteraction.Ignore);

        Vector3 myFlat = new Vector3(transform.position.x, 0f, transform.position.z);

        Vector3 separation = Vector3.zero;
        Vector3 alignVelSum = Vector3.zero;
        int alignCount = 0;
        Vector3 cohesionPosSum = Vector3.zero;
        int cohesionCount = 0;

        _flockOverlapSeenRoots.Clear();

        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _flockColliderBuffer[i];
            if (col == null) continue;
            if (col.transform == transform || col.transform.IsChildOf(transform)) continue;

            EnemyController otherEc = col.GetComponentInParent<EnemyController>();
            if (otherEc != null)
            {
                if (otherEc == this || !otherEc.IsAlive)
                    continue;

                int rootId = otherEc.gameObject.GetInstanceID();
                if (!_flockOverlapSeenRoots.Add(rootId))
                    continue;

                Vector3 otherFlat = new Vector3(otherEc.transform.position.x, 0f, otherEc.transform.position.z);
                Vector3 offset = myFlat - otherFlat;
                float dist = offset.magnitude;

                if (dist > 0.001f && dist < separationRadius)
                {
                    float t = (separationRadius - dist) / Mathf.Max(0.001f, separationRadius);
                    separation += offset.normalized * t;
                }
                else if (dist <= 0.001f)
                {
                    float jitter = GetInstanceID() < rootId ? 1f : -1f;
                    separation += new Vector3(jitter, 0f, jitter * 0.35f).normalized;
                }

                if (dist < alignmentRadius && otherEc._agent != null)
                {
                    Vector3 ov = otherEc._agent.velocity;
                    ov.y = 0f;
                    alignVelSum += ov;
                    alignCount++;
                }

                if (dist < cohesionRadius)
                {
                    cohesionPosSum += otherEc.transform.position;
                    cohesionCount++;
                }

                continue;
            }

            PlayerHealth playerHealth = col.GetComponentInParent<PlayerHealth>();
            if (playerHealth != null)
            {
                int rootId = playerHealth.gameObject.GetInstanceID();
                if (!_flockOverlapSeenRoots.Add(rootId))
                    continue;

                Vector3 pFlat = new Vector3(playerHealth.transform.position.x, 0f, playerHealth.transform.position.z);
                Vector3 offset = myFlat - pFlat;
                float dist = offset.magnitude;
                if (dist > 0.001f && dist < separationRadius)
                {
                    float t = (separationRadius - dist) / Mathf.Max(0.001f, separationRadius);
                    separation += offset.normalized * t;
                }
            }
        }

        Vector3 alignment = Vector3.zero;
        if (alignCount > 0)
        {
            Vector3 avgVel = alignVelSum / alignCount;
            Vector3 myVel = _agent.velocity;
            myVel.y = 0f;
            alignment = avgVel - myVel;
        }

        Vector3 cohesion = Vector3.zero;
        if (cohesionCount > 0)
        {
            Vector3 center = cohesionPosSum / cohesionCount;
            Vector3 toCenter = new Vector3(center.x, 0f, center.z) - myFlat;
            if (toCenter.sqrMagnitude > 0.0001f)
                cohesion = toCenter.normalized;
        }

        Vector3 flock = separation * separationWeight + alignment * alignmentWeight + cohesion * cohesionWeight;
        flock.y = 0f;
        flock = Vector3.ClampMagnitude(flock, maxFlockSteering);

        if (flock.sqrMagnitude < 1e-10f)
            return;

        Vector3 delta = flock * Time.fixedDeltaTime;
        delta = ClampAgentMoveAgainstStatics(delta);
        if (delta.sqrMagnitude < 1e-10f)
            return;

        _agent.Move(delta);
    }

    private Vector3 ClampAgentMoveAgainstStatics(Vector3 delta)
    {
        delta.y = 0f;
        if (delta.sqrMagnitude < 1e-8f)
            return delta;

        float radius = _agent != null ? Mathf.Max(0.3f, _agent.radius) : 0.45f;
        float height = _agent != null ? Mathf.Max(1.6f, _agent.height) : 2f;
        Vector3 origin = transform.position;
        Vector3 dir = delta.normalized;
        float dist = delta.magnitude + 0.05f;

        if (!Physics.CapsuleCast(
                origin + Vector3.up * radius,
                origin + Vector3.up * (height - radius),
                radius * 0.9f,
                dir,
                out RaycastHit hit,
                dist,
                EnemySpawnGeometry.StaticGeometryMask,
                QueryTriggerInteraction.Ignore))
            return delta;

        float allowed = Mathf.Max(0f, hit.distance - 0.06f);
        return dir * Mathf.Min(delta.magnitude, allowed);
    }

    private void EnforceStaticGeometryConstraint()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Jumping)
            return;
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
            return;

        float radius = Mathf.Max(0.3f, _agent.radius) * 0.6f;
        float height = Mathf.Max(1.6f, _agent.height) * 0.8f;
        bool clipping = EnemySpawnGeometry.IsCapsuleInsideStaticGeometry(transform.position, radius, height);
        float agentSpeed = _agent.velocity.sqrMagnitude;

        if (!clipping)
        {
            _staticClipSinceTime = -1f;
            _lastValidNavPosition = transform.position;
            _hasLastValidNavPosition = true;
            return;
        }

        if (agentSpeed > 0.04f)
        {
            _staticClipSinceTime = -1f;
            return;
        }

        if (_staticClipSinceTime < 0f)
        {
            _staticClipSinceTime = Time.time;
            return;
        }

        if (Time.time - _staticClipSinceTime < 1.2f)
            return;

        _staticClipSinceTime = -1f;
        if (_hasLastValidNavPosition && (_lastValidNavPosition - transform.position).sqrMagnitude > 0.05f)
        {
            _agent.Warp(_lastValidNavPosition);
            _agent.ResetPath();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  OUT-OF-MAP RECOVERY
    //  Routine teleport recovery is disabled. The only allowed agent warp is
    //  when an enemy has actually fallen below the world.
    // ════════════════════════════════════════════════════════════════════════

    private Vector3 _frozenLastPos;

    private void RecoverOnlyIfOutOfMap()
    {
        if (_state == AIStateId.Dead) return;
        if (_agent == null || !_agent.enabled) return;
        if (transform.position.y < -5f)
        {
            if (WorldArenaStabilizer.Instance != null &&
                WorldArenaStabilizer.Instance.TryRecoverTransform(transform, "fall_below_world"))
            {
                EvaluateTargets();
                return;
            }

            WarpToNearestNavMeshAfterFall();
            EvaluateTargets();
            return;
        }

        _frozenLastPos = transform.position;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  JUMP — Issue #1
    //  Detects when the enemy is stuck against an obstacle and launches it
    //  upward using the same physics formula as the Player:
    //      verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity)
    // ════════════════════════════════════════════════════════════════════════

    public void CheckAndJumpIfStuck()
    {
        if (!IsAgentReady() || !_agent.hasPath) return;

        float speed = _agent.velocity.magnitude;
        if (speed > obstacleBlockedSpeedThreshold)
        {
            _stuckTimer = 0f; // moving fine — reset timer
            return;
        }

        _stuckTimer += Time.deltaTime;
        if (_stuckTimer < stuckTime) return;
        _stuckTimer = 0f;

        // Vertical walls: do not vault with physics jumps — that clips through building meshes.
        Vector3 origin = transform.position + Vector3.up * 0.6f;
        if (Physics.Raycast(origin, transform.forward, out RaycastHit wallHit, obstacleCheckDist,
                EnemySpawnGeometry.StaticGeometryMask, QueryTriggerInteraction.Ignore))
        {
            if (wallHit.normal.y < 0.5f)
            {
                _stuckTimer = 0f;
                if (_state == AIStateId.Chase || _state == AIStateId.Approach)
                {
                    _agent.ResetPath();
                    TrySetChaseDestinationValidated();
                }
                return;
            }
        }

        // No wall — check for a downward ledge drop (target is below, ground drops ahead)
        TryLedgeDrop();
    }

    private void TryLedgeDrop()
    {
        if (!_isGrounded) return;

        // Only drop when target is meaningfully below us
        if (_target != null && _target.position.y >= transform.position.y - 0.8f) return;

        // Determine the best drop direction: toward target (flattened) or forward
        Vector3 dropDir = _target != null
            ? (_target.position - transform.position)
            : transform.forward;
        dropDir.y = 0f;
        if (dropDir.sqrMagnitude < 0.001f) dropDir = transform.forward;
        dropDir.Normalize();

        int groundMask = ~(1 << gameObject.layer);

        // Probe several distances (0.8m, 1.5m, 2.5m) to find ground below the ledge edge
        float[] probeDistances = { 0.8f, 1.5f, 2.5f };
        RaycastHit dropHit = default;
        bool foundDrop = false;
        foreach (float probe in probeDistances)
        {
            Vector3 probePos = transform.position + dropDir * probe + Vector3.up * 0.5f;
            if (Physics.Raycast(probePos, Vector3.down, out RaycastHit h, 8f, groundMask, QueryTriggerInteraction.Ignore))
            {
                float drop = (transform.position.y + 0.5f) - h.point.y;
                if (drop >= 0.6f &&
                    NavMesh.SamplePosition(h.point, out NavMeshHit nv, 2f, NavMesh.AllAreas) &&
                    nv.position.y < transform.position.y - 0.5f)
                {
                    dropHit = h;
                    foundDrop = true;
                    break;
                }
            }
        }

        if (!foundDrop) return;

        // Execute drop: disable NavMeshAgent, push forward, let gravity handle the fall
        _preJumpState = _state;
        _jumpTimer = MaxJumpDuration;
        TransitionTo(AIStateId.Jumping);

        if (_agent != null) _agent.enabled = false;
        if (_controller != null) _controller.enabled = false;
        _rb.isKinematic = false;

        // Forward push only — slight downward nudge so the enemy leaves the ledge edge
        _rb.linearVelocity = new Vector3(dropDir.x * chaseSpeed * 0.7f, -0.5f, dropDir.z * chaseSpeed * 0.7f);
        _isGrounded = false;

        SetAnimatorBool(HashGrounded, false);
    }

    private void TryJump()
    {
        if (!_isGrounded) return;
        if (IsStaticWallAhead(1.75f))
            return;

        _preJumpState = _state;
        _jumpTimer = MaxJumpDuration;
        TransitionTo(AIStateId.Jumping);

        // Hand movement over to physics
        if (_agent != null) _agent.enabled = false;
        if (_controller != null) _controller.enabled = false;
        _rb.isKinematic = false;

        // Same formula the Player uses in Jump():
        //   verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity)
        float jumpVel = Mathf.Sqrt(jumpHeight * -2f * gravity); // gravity is negative

        // Carry current horizontal momentum forward so the enemy clears the obstacle
        Vector3 horizDir = _target != null
            ? ((_target.position - transform.position).normalized)
            : transform.forward;
        horizDir.y = 0f;
        horizDir.Normalize();

        _rb.linearVelocity = new Vector3(
            horizDir.x * chaseSpeed * 0.6f,
            jumpVel,
            horizDir.z * chaseSpeed * 0.6f);

        _isGrounded = false;

        SetAnimatorBool(HashGrounded, false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ENEMY BLACK OPS 3 MANOEUVRES
    //  Adds the same four-button move vocabulary players use (Sprint / Slide /
    //  Jump / Flip) to the AI. We can't actually press buttons on their behalf,
    //  so the chase loop rolls a small chance every <maneuverRollInterval>
    //  seconds and triggers one of these moves. They're cosmetic + tactical:
    //    • Sprint → already handled by the dynamic chase speed up top.
    //    • Slide  → short forward velocity boost when in mid-range distance.
    //    • Jump   → small combat hop (re-uses TryJump).
    //    • Flip   → evasive forward flip with vertical+forward impulse.
    // ════════════════════════════════════════════════════════════════════════

    private float _maneuverTimer;

    public void TickCombatManeuver()
    {
        // Player-only mechanics. Enemies keep standard NavMesh locomotion,
        // chase, rotation, attack, and death states.
    }

    private bool IsStaticWallAhead(float distance)
    {
        Vector3 origin = transform.position + Vector3.up * 0.75f;
        return Physics.Raycast(origin, transform.forward, distance, EnemySpawnGeometry.StaticGeometryMask,
            QueryTriggerInteraction.Ignore);
    }

    private void DoManeuverSlide()
    {
        if (_target == null || _rb == null) return;
        if (!_isGrounded) return;
        if (IsStaticWallAhead(1.25f))
            return;

        Vector3 dir = (_target.position - transform.position);
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
        dir.Normalize();

        // Use the off-mesh physics path so we don't fight the agent.
        _preJumpState = _state;
        _jumpTimer = MaxJumpDuration;
        TransitionTo(AIStateId.Jumping);
        if (_agent != null) _agent.enabled = false;
        if (_controller != null) _controller.enabled = false;
        _rb.isKinematic = false;

        _rb.linearVelocity = new Vector3(dir.x * maneuverSlideBoost, 0.5f, dir.z * maneuverSlideBoost);
        _isGrounded = false;
        SetAnimatorBool(HashGrounded, false);
    }

    private void DoManeuverFlip()
    {
        if (_rb == null) return;
        if (!_isGrounded) return;
        if (IsStaticWallAhead(1.25f))
            return;

        Vector3 dir = transform.forward;
        if (_target != null)
        {
            Vector3 to = _target.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f) dir = to.normalized;
        }

        _preJumpState = _state;
        _jumpTimer = MaxJumpDuration;
        TransitionTo(AIStateId.Jumping);
        if (_agent != null) _agent.enabled = false;
        if (_controller != null) _controller.enabled = false;
        _rb.isKinematic = false;

        float flipUp = Mathf.Sqrt(Mathf.Max(0.01f, jumpHeight) * -2f * gravity);
        _rb.linearVelocity = new Vector3(dir.x * maneuverFlipBoost, flipUp, dir.z * maneuverFlipBoost);
        _isGrounded = false;
        SetAnimatorBool(HashGrounded, false);
    }

    private void ForceEndJump()
    {
        _isGrounded = true;
        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.isKinematic    = true;
        }
        if (_agent != null)
        {
            _agent.enabled = true;
            Vector3 snapOrigin = float.IsNaN(_spawnY)
                ? transform.position
                : new Vector3(transform.position.x, _spawnY, transform.position.z);
            if (NavMesh.SamplePosition(snapOrigin, out NavMeshHit landHit, 6f, NavMesh.AllAreas))
                _agent.Warp(landHit.position);
        }
        SetAnimatorBool(HashGrounded, true);
        TransitionTo(_target != null ? _preJumpState : AIStateId.Idle);
    }

    private void UpdateJumping()
    {
        // Safety timeout: if landing detection fails (sloped terrain, missed raycast),
        // force-end the jump after MaxJumpDuration to prevent permanent floating.
        _jumpTimer -= Time.deltaTime;
        if (_jumpTimer <= 0f)
        {
            ForceEndJump();
            return;
        }

        // Apply manual gravity to the Rigidbody while airborne so the arc
        // matches the Player's CharacterController behaviour.
        if (_rb != null && !_rb.isKinematic)
            _rb.linearVelocity += Vector3.up * gravity * Time.deltaTime;

        // Landing check: raycast downward. Use 2m so sloped/uneven/ramp terrain
        // is detected before the enemy overshoots. Exclude triggers; include all
        // solid layers (environment, default, etc.).
        int groundMask = ~(1 << gameObject.layer); // exclude own layer to avoid self-hit
        bool hitGround = Physics.Raycast(
            transform.position + Vector3.up * 0.3f,
            Vector3.down,
            out RaycastHit groundHit,
            2f,
            groundMask,
            QueryTriggerInteraction.Ignore);

        // Also treat it as landed if we've somehow ended up below the spawn Y
        // (fell through thin geometry on a downward slope).
        bool fellThrough = !float.IsNaN(_spawnY) && transform.position.y < _spawnY - 0.5f;

        if ((hitGround || fellThrough) && _rb != null && _rb.linearVelocity.y <= 0.15f)
        {
            _isGrounded = true;
            _rb.linearVelocity = Vector3.zero;
            _rb.isKinematic    = true;

            // Re-hand control back to NavMeshAgent (which owns movement)
            if (_agent != null)
            {
                _agent.enabled = true;
                // Snap to the actual ground hit point (works for ramps & multi-level terrain).
                // Fall back to _spawnY only when raycast missed (fellThrough case).
                Vector3 snapOrigin = hitGround
                    ? groundHit.point + Vector3.up * 0.05f
                    : new Vector3(transform.position.x, _spawnY, transform.position.z);
                Vector3 playerNav = GetCachedPlayerTransform() != null
                    ? GetCachedPlayerTransform().position
                    : snapOrigin;
                if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(snapOrigin, playerNav, 6f, out Vector3 valid))
                {
                    _agent.Warp(valid);
                    _spawnY = valid.y;
                }
                else if (NavMesh.SamplePosition(snapOrigin, out NavMeshHit landHit, 4f, NavMesh.AllAreas) &&
                         EnemySpawnGeometry.IsValidOutdoorSpawn(landHit.position, playerNav, rejectRooftops: true))
                {
                    _agent.Warp(landHit.position);
                    _spawnY = landHit.position.y;
                }
            }
            // NOTE: do NOT re-enable _controller — it would fight the agent
            // (the same hybrid that caused the original sluggish/jitter bug).
            SetAnimatorBool(HashGrounded, true);

            // Resume what we were doing before the jump
            TransitionTo(_target != null ? _preJumpState : AIStateId.Idle);
        }
    }

    // ── Combat ────────────────────────────────────────────────────────────────
    public float GetStrictMeleeStrikeRange()
    {
        return Mathf.Max(0.65f, meleeAttackRange * StrictMeleeStrikeFactor);
    }

    private float GetStrictMeleeStrikeRangeSqr()
    {
        float strike = GetStrictMeleeStrikeRange();
        return strike * strike;
    }

    public float GetTargetHorizontalDistanceSqr()
    {
        if (_target == null)
            return float.MaxValue;

        Vector3 delta = _target.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude;
    }

    public float GetTargetBodyDistance()
    {
        if (_target == null)
            return float.MaxValue;

        return MeleeBodyTargeting.GetClosestBodyDistance(GetWeaponStrikePoint(), _target);
    }

    private Vector3 GetWeaponStrikePoint()
    {
        if (_equippedWeaponHitbox != null)
            return _equippedWeaponHitbox.transform.position;

        return transform.position + transform.forward * 0.85f + Vector3.up * 1.1f;
    }

    public float GetApproachEnterDistanceSqr()
    {
        float enterDist = meleeAttackRange + Mathf.Max(0.15f, attackRangePadding * 0.5f);
        return enterDist * enterDist;
    }

    public float GetMeleeBreakDistanceSqr()
    {
        float breakDist = meleeAttackRange + Mathf.Max(0.25f, breakAttackPadding * 0.5f);
        return breakDist * breakDist;
    }

    public bool IsWithinMeleeAttackRange()
    {
        return GetTargetBodyDistance() <= GetStrictMeleeStrikeRange();
    }

    public bool CanBeginMeleeAttack()
    {
        return _target != null
            && GetCombatDistanceToTarget() <= Mathf.Max(0.65f, meleeAttackRange)
            && IsWithinMeleeAttackRange()
            && HasCombatVisionTo(_target)
            && IsFacingTarget(_target, attackFacingAngle);
    }

    private float ResolveChaseSpeed()
    {
        float targetSpeed = chaseSpeed;
        PlayerController pc = _target != null ? _target.GetComponentInParent<PlayerController>() : null;
        if (pc != null)
        {
            float playerTopSpeed = pc.moveSpeed * pc.sprintMultiplier;
            float boosted = Mathf.Max(chaseSpeed, playerTopSpeed + 0.35f);
            targetSpeed = Mathf.Min(sprintChaseSpeed, boosted);
        }

        return targetSpeed;
    }

    private void DriveTargetPursuit(float speed, float stoppingDistance)
    {
        AIMotor.DrivePursuit(this, stoppingDistance);
    }

    public void RegisterMeleeHitLanded()
    {
        _attackSwingLanded = true;
    }

    public void NotifyMeleeAttackMissed()
    {
        if (_state != AIStateId.Attack && !_attackInProgress)
            return;

        AbortActiveAttack();
        if (_target != null && IsTargetValid(_target))
            TransitionTo(AIStateId.Chase);
        else
            TransitionTo(AIStateId.Patrol);
    }

    public void AbortActiveAttack()
    {
        if (_attackHitboxRoutine != null)
        {
            StopCoroutine(_attackHitboxRoutine);
            _attackHitboxRoutine = null;
        }

        if (_katanaAttackResolveRoutine != null)
        {
            StopCoroutine(_katanaAttackResolveRoutine);
            _katanaAttackResolveRoutine = null;
        }

        if (_equippedWeaponHitbox != null)
            _equippedWeaponHitbox.DisableHitbox();

        _attackInProgress = false;
        _attackSwingLanded = false;

        if (_anim != null)
            _anim.ResetTrigger(HashAttack);
    }

    private void CompleteAttackSwing(bool resumeChaseOnMiss)
    {
        if (_equippedWeaponHitbox != null)
            _equippedWeaponHitbox.DisableHitbox();

        _attackInProgress = false;
        _attackHitboxRoutine = null;

        if (resumeChaseOnMiss && !_attackSwingLanded && _target != null && IsTargetValid(_target))
            TransitionTo(AIStateId.Chase);

        _attackSwingLanded = false;
    }

    public void ExecuteAttack()
    {
        if (!CanBeginMeleeAttack() || !IsWithinMeleeAttackRange())
        {
            if (_target != null && IsTargetValid(_target))
                TransitionTo(AIStateId.Chase);
            return;
        }

        if (IsAgentReady())
        {
            _agent.isStopped = true;
            _agent.velocity = Vector3.zero;
        }

        _attackInProgress = true;
        _attackSwingLanded = false;

        if (CombatDebug.Enabled)
            CombatDebug.Log($"EnemyAttack started enemy={name}");

        KatanaCombatHandler katanaHandler = GetComponent<KatanaCombatHandler>();
        if (katanaHandler != null)
        {
            if (!katanaHandler.TriggerAttack())
            {
                _attackInProgress = false;
                return;
            }

            _katanaAttackResolveRoutine = StartCoroutine(KatanaAttackResolveRoutine());
            return;
        }

        SetAnimatorTrigger(HashAttack);

        if (_attackHitboxRoutine != null)
        {
            StopCoroutine(_attackHitboxRoutine);
            _attackHitboxRoutine = null;
        }
        if (_equippedWeaponHitbox != null)
            _equippedWeaponHitbox.DisableHitbox();

        _attackHitboxRoutine = StartCoroutine(AttackHitboxWindowRoutine());
    }

    private IEnumerator KatanaAttackResolveRoutine()
    {
        float resolveTime = attackHitboxWindup + attackHitboxActiveTime + 0.05f;
        if (resolveTime > 0f)
            yield return new WaitForSeconds(resolveTime);

        _katanaAttackResolveRoutine = null;
        CompleteAttackSwing(resumeChaseOnMiss: true);
    }

    private void DriveChaseWithFlowField()
    {
        if (!IsAgentReady())
            return;

        _flowFieldSteerTimer -= Time.deltaTime;
        if (_flowFieldSteerTimer > 0f)
            return;

        _flowFieldSteerTimer = Mathf.Max(0.03f, flowFieldSteerInterval);
        Vector3 flowDir = FlowFieldManager.Instance.GetFlowDirection(transform.position);
        if (flowDir.sqrMagnitude < 0.001f)
        {
            if (_target != null)
                _agent.SetDestination(_target.position);
            return;
        }

        Vector3 steerTarget = transform.position + flowDir.normalized * Mathf.Max(0.5f, flowFieldLookAhead);
        if (NavMesh.SamplePosition(steerTarget, out NavMeshHit hit, 1.8f, NavMesh.AllAreas))
            _agent.SetDestination(hit.position);
        else
            _agent.SetDestination(steerTarget);
    }

    private IEnumerator AttackHitboxWindowRoutine()
    {
        if (_equippedWeaponHitbox == null)
        {
            CompleteAttackSwing(resumeChaseOnMiss: true);
            yield break;
        }

        _equippedWeaponHitbox.DisableHitbox();

        float windupElapsed = 0f;
        while (windupElapsed < attackHitboxWindup)
        {
            if (_target == null || !IsTargetValid(_target)
                || GetTargetBodyDistance() > GetStrictMeleeStrikeRange()
                || !HasCombatVisionTo(_target))
            {
                CompleteAttackSwing(resumeChaseOnMiss: true);
                yield break;
            }

            windupElapsed += Time.deltaTime;
            yield return null;
        }

        if (_target == null || !IsTargetValid(_target)
            || GetTargetBodyDistance() > GetStrictMeleeStrikeRange()
            || !HasCombatVisionTo(_target))
        {
            CompleteAttackSwing(resumeChaseOnMiss: true);
            yield break;
        }

        Physics.SyncTransforms();
        _equippedWeaponHitbox.meleeAttackRange = GetStrictMeleeStrikeRange();
        _equippedWeaponHitbox.maxAttackRange = GetStrictMeleeStrikeRange() + 0.15f;
        _equippedWeaponHitbox.damage = Mathf.Max(1, Mathf.RoundToInt(attackDamage));
        _equippedWeaponHitbox.SetAttackTarget(_target);
        _equippedWeaponHitbox.EnableHitbox();

        float activeElapsed = 0f;
        while (activeElapsed < attackHitboxActiveTime)
        {
            if (_target == null || !IsTargetValid(_target)
                || GetTargetBodyDistance() > GetStrictMeleeStrikeRange())
            {
                _equippedWeaponHitbox.DisableHitbox();
                CompleteAttackSwing(resumeChaseOnMiss: true);
                yield break;
            }

            activeElapsed += Time.deltaTime;
            yield return null;
        }

        CompleteAttackSwing(resumeChaseOnMiss: true);
    }

    public void TakeDamage(int amount, bool byPlayer = false)
    {
        if (_state == AIStateId.Dead) return;
        if (byPlayer) _playerDamagedThisLife = true;

        _currentHealth -= amount;

        // Flinch
        if (_state != AIStateId.Flinch)
        {
            _flinchTimer = flinchDuration;
            TransitionTo(AIStateId.Flinch);
            if (IsAgentReady()) _agent.ResetPath();
        }

        ApplyHitFlash();
        SetAnimatorTrigger(HashHit);
        CombatVoiceSfx.GetOrAdd(gameObject).PlayHurt();

        if (_currentHealth <= 0)
        {
            _killedByPlayer = byPlayer;
            DisableMovementAndCollisionForDeath();
            Die();
        }
    }

    // ── IDamageable ─────────────────────────────────────────────────────────
    public bool IsAlive => _state != AIStateId.Dead;

    public void ReceiveDamage(int amount, GameObject attackerRoot)
    {
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer && !PhotonNetwork.IsMasterClient)
            return;
#endif

        if (MultiplayerMode.IsMultiplayer && MultiplayerMode.ActiveMode == MpGameMode.CoopSurvival)
        {
            if (attackerRoot != null && attackerRoot.GetComponentInParent<EnemyController>() != null)
                return;
        }

        // Brief spawn-protection grace window: avoids enemies being killed in
        // the first frames before the AI has a chance to react / spread out.
        // Not invincibility — duration is short and tunable in Inspector.
        if (spawnProtectionDuration > 0f
            && _spawnTime >= 0f
            && Time.time - _spawnTime < spawnProtectionDuration)
        {
            return;
        }

        // Occlusion is already checked by every caller before ReceiveDamage is
        // invoked (WeaponHitbox.TryDamageCollider, PlayerController.TryDamageTargetFromPoint,
        // etc.). A second linecast here doubles the false-block surface area: if the
        // first check passes but an in-between geometry grazes the second origin
        // (attacker.position+1.6m vs the caller's origin), damage is silently eaten.
        // We keep the log for CombatDebug diagnostics but skip the early return.
        if (CombatDebug.Enabled)
        {
            bool blocked = DamageOcclusion.IsBlocked(attackerRoot, gameObject);
            CombatDebug.Log($"[ReceiveDamage] blockedByWall={blocked} (occlusion not re-enforced here)");
        }

        // ── Record attacker & retaliate immediately ─────────────────────────
        if (attackerRoot != null && attackerRoot != gameObject)
        {
            Transform attackerT = attackerRoot.transform;

            // Walk up to the IDamageable root so we lock onto the correct
            // Transform (not a weapon hitbox child).
            IDamageable atkDmg = attackerRoot.GetComponentInParent<IDamageable>();
            if (atkDmg != null) attackerT = atkDmg.transform;

            _lastAttacker     = attackerT;
            _lastAttackerTime = Time.time;

            // Immediate retaliation: if we were idle, chasing someone far, or
            // our current target is dead/invalid, snap onto the attacker.
            if (IsTargetValid(attackerT) &&
                (!IsTargetValid(_target) || _target == null || _target == attackerT ||
                 Vector3.Distance(transform.position, attackerT.position) <
                 Vector3.Distance(transform.position, _target.position) - 0.5f))
            {
                _target = attackerT;
                _targetLockTimer = Time.time + targetLockDuration;
            }

            if (IsTargetValid(_target) && CanEngageChase(_target))
            {
                InterruptPassiveBehavior();
                if (_state != AIStateId.Attack && _state != AIStateId.Jumping && _state != AIStateId.Dead)
                    TransitionTo(AIStateId.Chase);
            }
        }

        // Store hit direction for ragdoll impulse (attacker → enemy)
        if (attackerRoot != null)
            _lastHitDirection = (transform.position - attackerRoot.transform.position).normalized;

        bool fromPlayer = attackerRoot != null
                       && attackerRoot.GetComponentInParent<PlayerHealth>() != null;

        int appliedDamage = Mathf.Max(1, amount);
        if (fromPlayer && GameManager.Instance != null)
        {
            int hitsToKill = Mathf.Max(1, GameManager.Instance.GetEnemyHitsToKillByPlayer());
            appliedDamage = Mathf.Max(1, Mathf.CeilToInt((float)maxHealth / hitsToKill));
        }

        int healthBefore = _currentHealth;
        if (CombatDebug.Enabled)
            CombatDebug.Log($"applyingDamage amount={appliedDamage} target={gameObject.name}");

        if (MultiplayerMode.IsMultiplayer)
            Debug.Log("[AICombat] damage applied by MasterClient");

        if (debugEnemyMovement)
            Debug.Log($"[EnemyDamage] {name} took {appliedDamage} from {(attackerRoot != null ? attackerRoot.name : "unknown")} (hp={_currentHealth}->{_currentHealth - appliedDamage})", this);

        TakeDamage(appliedDamage, byPlayer: fromPlayer);

        if (CombatDebug.Enabled)
            CombatDebug.Log($"healthBefore={healthBefore} healthAfter={_currentHealth}");

        // Notify the tactical brain (subscriber-only; null-safe).
        OnDamaged?.Invoke(attackerRoot);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DEATH — Issue #4
    //  Switches to ragdoll physics immediately so the body falls naturally.
    //  The corpse persists for corpseLifetime seconds before Destroy().
    // ════════════════════════════════════════════════════════════════════════

    private void Die()
    {
        _state = AIStateId.Dead;
        DisableMovementAndCollisionForDeath();
        CancelInvoke(nameof(EnableRagdoll));
        CancelInvoke(nameof(FreezeRagdoll));
        if (_attackHitboxRoutine != null)
        {
            StopCoroutine(_attackHitboxRoutine);
            _attackHitboxRoutine = null;
        }
        if (_equippedWeaponHitbox != null)
            _equippedWeaponHitbox.DisableHitbox();

        // Stop target scan coroutine — no point scanning when dead
        if (_scanCoroutine != null)
        {
            StopCoroutine(_scanCoroutine);
            _scanCoroutine = null;
        }

        // Notify GameManager FIRST so enemy count updates correctly (Issue #5)
        if (GameManager.Instance != null)
            GameManager.Instance.EnemyKilled(
                byPlayer: _killedByPlayer,
                assistedByPlayer: !_killedByPlayer && _playerDamagedThisLife);

        if (MatchStatsManager.Instance != null)
        {
            string combatantId = MatchStatsManager.BuildCombatantId(this);
            MatchStatsManager.Instance.MarkEliminated(combatantId);

            if (_killedByPlayer)
            {
                PlayerHealth player = null;
                if (_lastAttacker != null)
                {
                    player = _lastAttacker.GetComponentInParent<PlayerHealth>();
                }
                if (player == null)
                {
                    player = FindFirstObjectByType<PlayerHealth>(); // Fallback
                }
                
                string playerId = MatchStatsManager.BuildCombatantId(player);
                MatchStatsManager.Instance.RecordKill(playerId);

                // Enforce that single-player challenges and bounties are only advanced
                // if the local player was the one who actually dealt the final blow.
                #if PUN_2_OR_NEWER
                bool isLocalKill = (player != null && player.GetComponent<PhotonView>() != null && player.GetComponent<PhotonView>().IsMine);
                #else
                bool isLocalKill = true;
                #endif

                if (isLocalKill && SessionManager.Instance != null)
                {
                    string wid = SessionManager.Instance.EquippedWeaponId;
                    SessionManager.Instance.OnPlayerKilledEnemy(wid);
                }
            }
            else
            {
                EnemyController enemyKiller = _lastAttacker != null ? _lastAttacker.GetComponentInParent<EnemyController>() : null;
                if (enemyKiller != null && enemyKiller != this)
                    MatchStatsManager.Instance.RecordKill(MatchStatsManager.BuildCombatantId(enemyKiller));
            }
        }

        // Stop all navigation immediately
        if (_rb != null) _rb.isKinematic = true;

        // Disable main collider so living enemies don't trip over the corpse
        Collider mainCol = GetComponent<Collider>();
        if (mainCol != null) mainCol.enabled = false;

        CombatVoiceSfx.GetOrAdd(gameObject).PlayDeath();

        EnableRagdoll();

        // ── Corpse cleanup ────────────────────────────────────────────────────
        if (ragdollVisibleDuration > 0f)
            Destroy(gameObject, ragdollVisibleDuration);
    }

    private void DisableMovementAndCollisionForDeath()
    {
        if (_attackHitboxRoutine != null)
        {
            StopCoroutine(_attackHitboxRoutine);
            _attackHitboxRoutine = null;
        }

        if (_equippedWeaponHitbox != null)
            _equippedWeaponHitbox.DisableHitbox();

        if (_agent != null && _agent.enabled)
        {
            _agent.isStopped = true;
            _agent.ResetPath();
            _agent.enabled = false;
        }

        if (_controller != null)
            _controller.enabled = false;

        Collider mainCol = GetComponent<Collider>();
        if (mainCol != null)
            mainCol.enabled = false;
    }

    private void RegisterForMatchStats()
    {
        if (MatchStatsManager.Instance == null)
            return;

        string combatantId = MatchStatsManager.BuildCombatantId(this);
        string displayName = gameObject.name.ToUpperInvariant();
        // Pass the transform so the end-match cinematic can orbit around
        // the actual enemy mesh (instead of guessing from a name match).
        MatchStatsManager.Instance.RegisterCombatant(combatantId, displayName, isPlayer: false, transform: transform);
    }

    /// <summary>
    /// Converts the enemy to a full ragdoll: disables the Animator and lets
    /// Rigidbody physics take over every bone.
    /// Delegates to RagdollController when present; falls back to inline logic.
    /// </summary>
    private void EnableRagdoll()
    {
        if (_anim != null) _anim.enabled = false;
        if (_agent != null) _agent.enabled = false;
        if (_controller != null) _controller.enabled = false;

        int defaultLayer = LayerMask.NameToLayer("Default");
        int environmentLayer = LayerMask.NameToLayer("Environment");
        if (defaultLayer >= 0)
            Physics.IgnoreLayerCollision(gameObject.layer, defaultLayer, false);
        if (environmentLayer >= 0)
            Physics.IgnoreLayerCollision(gameObject.layer, environmentLayer, false);

        if (_ragdoll == null)
            _ragdoll = GetComponent<RagdollController>();

        if (_ragdollBodies == null || _ragdollBodies.Length == 0)
            _ragdollBodies = GetComponentsInChildren<Rigidbody>(true);

        var boneBodies = new List<Rigidbody>();
        for (int i = 0; i < _ragdollBodies.Length; i++)
        {
            Rigidbody rb = _ragdollBodies[i];
            if (rb == null || rb.gameObject == gameObject)
                continue;
            boneBodies.Add(rb);
        }

        Vector3 hitDir = _lastHitDirection.sqrMagnitude > 0.001f
            ? _lastHitDirection.normalized
            : -transform.forward;

        // 1) Attempt full bone ragdoll first.
        if (_ragdoll != null || boneBodies.Count > 0)
        {
            if (_mainCapsuleCollider != null)
                _mainCapsuleCollider.enabled = false;

            Collider rootCol = GetComponent<Collider>();
            if (rootCol != null && rootCol != _mainCapsuleCollider)
                rootCol.enabled = false;

            if (_ragdoll != null)
            {
                _ragdoll.EnableRagdoll(hitDir);
                return;
            }

            Rigidbody impulseTarget = null;
            for (int i = 0; i < boneBodies.Count; i++)
            {
                Rigidbody boneRb = boneBodies[i];
                boneRb.gameObject.SetActive(true);
                boneRb.isKinematic = false;
                boneRb.useGravity = true;
                boneRb.detectCollisions = true;
                boneRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                boneRb.interpolation = RigidbodyInterpolation.Interpolate;
                if (impulseTarget == null)
                    impulseTarget = boneRb;
            }

            Collider[] allCols = GetComponentsInChildren<Collider>(true);
            PhysicsMaterial highFriction = new PhysicsMaterial("EnemyRagdollFriction")
            {
                dynamicFriction = 1f,
                staticFriction = 1f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            for (int i = 0; i < allCols.Length; i++)
            {
                Collider c = allCols[i];
                if (c == null) continue;
                if (c.gameObject == gameObject) continue;
                c.enabled = true;
                c.material = highFriction;
            }

            if (impulseTarget != null)
            {
                Vector3 ragdollImpulse = hitDir * deathPopForce + Vector3.up * (deathPopForce * 0.75f);
                impulseTarget.AddForce(ragdollImpulse, ForceMode.VelocityChange);
                impulseTarget.AddTorque(Random.insideUnitSphere * 3f, ForceMode.VelocityChange);
                Invoke(nameof(FreezeRagdoll), 3f);
                return;
            }
        }

        // 2) Bulletproof fallback: make the ROOT body physically topple.
        if (_mainCapsuleCollider == null)
            _mainCapsuleCollider = GetComponent<CapsuleCollider>();
        if (_mainCapsuleCollider == null)
            _mainCapsuleCollider = gameObject.AddComponent<CapsuleCollider>();
        _mainCapsuleCollider.enabled = true; // critical: keep root collider active

        if (_rb == null)
            _rb = GetComponent<Rigidbody>();
        if (_rb == null)
            _rb = gameObject.AddComponent<Rigidbody>();

        _rb.isKinematic = false;
        _rb.useGravity = true;
        _rb.constraints = RigidbodyConstraints.None;
        _rb.detectCollisions = true;
        _rb.mass = Mathf.Max(20f, _rb.mass);
        _rb.linearDamping = 0.15f;
        _rb.angularDamping = 0.05f;

        Vector3 launch = (-transform.forward * (deathPopForce * 1.25f)) + (Vector3.up * (deathPopForce * 1.5f));
        _rb.AddForce(launch, ForceMode.VelocityChange);

        Vector3 tumbleAxis = Vector3.Cross(Vector3.up, hitDir);
        if (tumbleAxis.sqrMagnitude < 0.001f)
            tumbleAxis = transform.right;
        _rb.AddTorque((tumbleAxis.normalized * deathPopForce * 6f) + Random.insideUnitSphere * 2f, ForceMode.VelocityChange);
    }

    private void FreezeRagdoll()
    {
        foreach (Rigidbody rb in GetComponentsInChildren<Rigidbody>(true))
            rb.isKinematic = true;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ANIMATIONS — Issue #3
    //  Speed is normalised to 0–1 so it maps cleanly onto animator blend
    //  trees without depending on raw world-speed units.
    //  VelocityX and VelocityZ drive left/right and forward/back separately
    //  so strafe animations play correctly on both legs.
    // ════════════════════════════════════════════════════════════════════════

    private void SyncAnimator()
    {
        if (_anim == null) return;

        // ══════════════════════════════════════════════════════════════════════
        //  NAVMESHAGENT VELOCITY SYNC
        //  ──────────────────────────
        //  Now that the NavMeshAgent owns position AND rotation, its
        //  `velocity` field is the single source of truth for locomotion
        //  animation. This eliminates the desync between the walk-cycle
        //  foot-speed and the visible displacement that the old position-delta
        //  method suffered when the agent was fighting a CharacterController.
        // ══════════════════════════════════════════════════════════════════════

        Vector3 agentVelocity = (_agent != null && _agent.enabled)
            ? _agent.velocity
            : Vector3.zero;
        agentVelocity.y = 0f;

        float actualSpeed = agentVelocity.magnitude;

        // Normalise by sprint cap so full-speed chases register ~1.0 on the blend tree; optional runAnimationMultiplier
        // nudges foot speed to match visible stride when the agent is at top chase velocity.
        float refSpeed = sprintChaseSpeed;
        PlayerController refPlayer = ResolveScenePlayer();
        if (refPlayer != null)
            refSpeed = Mathf.Max(0.01f, refPlayer.moveSpeed * refPlayer.sprintMultiplier);
        float normalizedSpeed = Mathf.Clamp01((actualSpeed * runAnimationMultiplier) / refSpeed);
        if (actualSpeed < 0.1f)
            normalizedSpeed = 0f;

        // Keep _lastFramePosition updated for any legacy callers.
        _lastFramePosition = transform.position;

        float speedDamp = normalizedSpeed < 0.01f ? 0f : 0.05f;
        bool droveSpeed = SetAnimatorFloatChecked(HashSpeed, normalizedSpeed, speedDamp);

        // Fallback: if the animator has no "Speed" parameter, force-crossfade
        // into the correct state directly (same fallback PlayerController uses)
        if (!droveSpeed)
            ForceLocomotionState(normalizedSpeed);

        // ── Directional blend-tree params (VelocityX / VelocityZ) ────────
        if (debugEnemyMovement && _state == AIStateId.Chase && _agent != null && (Time.frameCount % 15) == 0)
        {
            Debug.Log(
                $"[EnemyMovement] enemy={name} velMag={actualSpeed:F2} agentSpeed={_agent.speed:F2} " +
                $"animNorm={normalizedSpeed:F2} state={_state}",
                this);
        }

        if (actualSpeed > 0.05f)
        {
            Vector3 localVel = transform.InverseTransformDirection(
                agentVelocity.normalized * normalizedSpeed);
            SetAnimatorFloat(HashVelX, Mathf.Clamp(localVel.x, -1f, 1f), 0.05f);
            SetAnimatorFloat(HashVelZ, Mathf.Clamp(localVel.z, -1f, 1f), 0.05f);
        }
        else
        {
            SetAnimatorFloat(HashVelX, 0f, 0.05f);
            SetAnimatorFloat(HashVelZ, 0f, 0.05f);
        }

        SetAnimatorBool(HashGrounded, _isGrounded);
    }

    /// <summary>
    /// Fallback for animator controllers that lack a "Speed" float parameter.
    /// Directly CrossFades into "Walk" or "Idle" states — same technique the
    /// PlayerController.ForceLocomotionState() uses.
    /// </summary>
    private void ForceLocomotionState(float normalizedSpeed)
    {
        if (_anim == null || _anim.runtimeAnimatorController == null) return;

        string stateName     = normalizedSpeed > 0.05f ? "Walk" : "Idle";
        string fullStateName = "Base Layer." + stateName;
        int    stateHash     = Animator.StringToHash(fullStateName);

        if (!_anim.HasState(0, stateHash)) return;

        AnimatorStateInfo current = _anim.GetCurrentAnimatorStateInfo(0);
        if (current.fullPathHash == stateHash) return;

        _anim.CrossFadeInFixedTime(fullStateName, 0.15f, 0);
    }

    private void CacheAnimatorParameters()
    {
        _animParameterHashes = new HashSet<int>();

        if (_anim == null)
            return;

        foreach (AnimatorControllerParameter parameter in _anim.parameters)
            _animParameterHashes.Add(parameter.nameHash);
    }

    private void EnsureAnimationEventSink()
    {
        // Animation events are fired on the GameObject that owns the Animator
        // playing the clip. The Crosby enemy prefab can contain multiple
        // Animators (root + skinned-mesh child literally named "Crosby"), so we
        // attach receiver sinks on EVERY animator host AND on any child named
        // "Crosby" — that is the GameObject the engine reports in the
        // "has no receiver" error message and must be covered explicitly.
        Animator[] animators = GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;
            GameObject host = a.gameObject;
            if (host.GetComponent<AnimationEventSink>() == null)
                host.AddComponent<AnimationEventSink>();
            if (host.GetComponent<MeleeAnimationEventSink>() == null)
                host.AddComponent<MeleeAnimationEventSink>();
        }

        // Belt-and-braces: also walk the whole transform tree and cover any GO
        // literally named "Crosby" (case-insensitive). Animation events embed
        // the host GameObject name in the error, so this guarantees a receiver
        // on exactly the object Unity is firing the event on.
        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            string n = t.gameObject.name;
            if (string.IsNullOrEmpty(n)) continue;
            if (n.IndexOf("Crosby", System.StringComparison.OrdinalIgnoreCase) < 0) continue;

            GameObject go = t.gameObject;
            if (go.GetComponent<AnimationEventSink>() == null)
                go.AddComponent<AnimationEventSink>();
            if (go.GetComponent<MeleeAnimationEventSink>() == null)
                go.AddComponent<MeleeAnimationEventSink>();
        }
    }

    private void AssignMaterial()
    {
        // Try explicit paths first, then search all Materials in Resources as a fallback.
        Material material = Resources.Load<Material>("Materials/Enemy");
        if (material == null)
            material = Resources.Load<Material>("Materials/Ronin");
        if (material == null)
        {
            // Broad fallback: find any material whose name contains "Enemy" or "Ronin"
            foreach (Material m in Resources.LoadAll<Material>("Materials"))
            {
                if (m == null) continue;
                string n = m.name.ToLowerInvariant();
                if (n.Contains("enemy") || n.Contains("ronin") || n.Contains("soldier"))
                {
                    material = m;
                    break;
                }
            }
        }

        if (material == null)
        {
            // Last resort: create a visible default so the enemy isn't a white statue
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            material.color = new Color(0.25f, 0.35f, 0.25f); // military green
        }

        // Ensure AnimationEventSink is present to prevent CS0246 event-method errors
        EnsureAnimationEventSink();

        Color tint = EnemyTintPalette[ResolveEnemyPaletteIndex() % EnemyTintPalette.Length];
        foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            Material instance = new Material(material);
            SetMaterialBaseColor(instance, Color.Lerp(GetMaterialBaseColor(instance), tint, 0.55f));
            smr.material = instance;
        }

        _renderers = GetComponentsInChildren<Renderer>(true);
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            _originalColors[i] = GetMaterialBaseColor(_renderers[i].material);
        }
    }

    private int ResolveEnemyPaletteIndex()
    {
        string objectName = gameObject != null ? gameObject.name : string.Empty;
        int parsed = 0;
        bool foundDigit = false;

        for (int i = 0; i < objectName.Length; i++)
        {
            char c = objectName[i];
            if (c < '0' || c > '9')
                continue;

            foundDigit = true;
            parsed = (parsed * 10) + (c - '0');
        }

        return foundDigit ? Mathf.Max(0, parsed - 1) : Mathf.Abs(GetInstanceID());
    }

    private bool HasAnimatorParameter(int hash)
    {
        return _anim != null
            && _animParameterHashes != null
            && _animParameterHashes.Contains(hash);
    }

    private static int ResolveHittableMask()
    {
        int mask = 0;
        void Add(string n)
        {
            int l = LayerMask.NameToLayer(n);
            if (l >= 0) mask |= 1 << l;
        }
        Add("Player");
        Add("Hittable");
        Add("Character");
        return mask != 0 ? mask : ~0;
    }

    private static void SetLayerRecursive(GameObject root, int layer)
    {
        if (root == null || layer < 0) return;
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] != null)
                transforms[i].gameObject.layer = layer;
        }
    }

    private void SetAnimatorFloat(int hash, float value, float dampTime = 0f)
    {
        if (!HasAnimatorParameter(hash))
            return;

        if (dampTime > 0f)
            _anim.SetFloat(hash, value, dampTime, Time.deltaTime);
        else
            _anim.SetFloat(hash, value);
    }

    /// <summary>Same as SetAnimatorFloat but returns true if the parameter existed.</summary>
    private bool SetAnimatorFloatChecked(int hash, float value, float dampTime = 0f)
    {
        if (!HasAnimatorParameter(hash))
            return false;

        if (dampTime > 0f)
            _anim.SetFloat(hash, value, dampTime, Time.deltaTime);
        else
            _anim.SetFloat(hash, value);
        return true;
    }

    private void SetAnimatorBool(int hash, bool value)
    {
        if (HasAnimatorParameter(hash))
            _anim.SetBool(hash, value);
    }

    private void SetAnimatorTrigger(int hash)
    {
        if (HasAnimatorParameter(hash))
            _anim.SetTrigger(hash);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  HIT REACTION EFFECTS
    // ════════════════════════════════════════════════════════════════════════

    // ── URP-safe color helpers ────────────────────────────────────────────────
    /// <summary>
    /// Returns the "main" colour of a material, checking URP's _BaseColor
    /// first and falling back to Standard's _Color.
    /// </summary>
    private static Color GetMaterialBaseColor(Material mat)
    {
        if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
        if (mat.HasProperty("_Color"))     return mat.GetColor("_Color");
        return Color.white;
    }

    /// <summary>Sets the main colour on a material (URP + Standard safe).</summary>
    private static void SetMaterialBaseColor(Material mat, Color c)
    {
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     c);
    }

    private void ApplyHitFlash()
    {
        _flashTimer = FlashDuration;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            SetMaterialBaseColor(_renderers[i].material, hitFlashColor);
        }
    }

    private void RestoreOriginalColors()
    {
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            if (i < _originalColors.Length)
                SetMaterialBaseColor(_renderers[i].material, _originalColors[i]);
        }
    }

    private void PlayProceduralSound(float frequency, float duration)
    {
        int sampleRate  = 44100;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        AudioClip clip  = AudioClip.Create("ProceduralHit", sampleCount, 1, sampleRate, false);

        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t        = (float)i / sampleRate;
            float envelope = 1f - (t / duration);
            samples[i]     = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.3f;
        }
        clip.SetData(samples, 0);
        _audio.PlayOneShot(clip, AudioSettingsRuntime.ScaledSfx(0.5f));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SEPARATION (Anti-Stacking)
    //  NavMeshAgent.obstacleAvoidanceType = HighQualityObstacleAvoidance
    //  + randomized avoidancePriority (set in Awake) handles enemy-vs-enemy
    //  separation natively now that the agent owns movement. No manual
    //  push-vector math required.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True if the transform points at something still alive we can damage.</summary>
    public bool IsTargetValid(Transform t)
    {
        if (t == null) return false;
        IDamageable dmg = t.GetComponentInParent<IDamageable>();
        if (dmg == null || !dmg.IsAlive)
            return false;

        if (!IsInsidePlayableBounds(t.position))
            return false;

        if (!float.IsNaN(_spawnY))
        {
            float vertical = t.position.y - _spawnY;
            if (vertical > maxChaseVerticalDelta + 1.5f || vertical < -6f)
                return false;
        }

        return true;
    }

    private bool IsInsidePlayableBounds(Vector3 worldPos)
    {
        if (WorldArenaStabilizer.Instance != null)
            return WorldArenaStabilizer.Instance.IsInsidePlayableBounds(worldPos);

        if (LevelBuilder.Instance != null)
        {
            float half = LevelBuilder.Instance.arenaHalfSize * 0.98f;
            return Mathf.Abs(worldPos.x) <= half && Mathf.Abs(worldPos.z) <= half;
        }

        return true;
    }

    private bool TryRecoveryWarp(Vector3 desired, float sampleRadius, string reason)
    {
        if (_agent == null || !_agent.enabled)
            return false;

        float now = Time.time;
        if (now - _lastRecoveryWarpTime < recoveryWarpCooldown)
            return false;

        Vector3 playerNav = GetCachedPlayerTransform() != null
            ? GetCachedPlayerTransform().position
            : desired;
        Vector3 warpPos = desired;
        if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(desired, playerNav, sampleRadius, out Vector3 valid))
        {
            warpPos = valid;
        }
        else if (!NavMesh.SamplePosition(desired, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
            return false;
        else
        {
            if (!float.IsNaN(_spawnY) && (hit.position.y - _spawnY) > watchdogMaxYAboveSpawn + 0.5f)
                return false;

            if (!EnemySpawnGeometry.IsValidOutdoorSpawn(hit.position, playerNav, rejectRooftops: true))
                return false;

            warpPos = hit.position;
        }

        _agent.Warp(warpPos);
        _spawnY = warpPos.y;
        _agent.ResetPath();
        _lastRecoveryWarpTime = now;
        _highSinceTime = -1f;
        _lowProgressSinceTime = -1f;
        _partialPathSinceTime = -1f;

        if (debugWatchdog || debugAI)
            Debug.Log($"[EnemyWatchdog] {name} recovery warp ({reason}) -> {warpPos}");

        return true;
    }

    private static int _aiVisionBlockMask;
    private static bool _aiVisionBlockMaskCached;

    /// <summary>
    /// Solid environment layers only — excludes characters so walls/doors block vision.
    /// </summary>
    private static int ResolveAiVisionBlockMask()
    {
        if (_aiVisionBlockMaskCached)
            return _aiVisionBlockMask;

        int mask = 0;
        void Add(string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0)
                mask |= 1 << layer;
        }

        Add("Environment");
        Add("Building");
        Add("StaticObstacle");
        Add("Wall");
        Add("Door");

        _aiVisionBlockMask = mask;
        _aiVisionBlockMaskCached = true;
        return _aiVisionBlockMask;
    }

    /// <summary>Chest-to-chest line test using environment-only layers.</summary>
    public bool HasCombatVisionTo(Transform t)
    {
        if (t == null)
            return true;

        int mask = ResolveAiVisionBlockMask();
        if (mask == 0)
            return true;

        Vector3 from = transform.position + Vector3.up * lineOfSightEyeHeight;
        Vector3 to = t.position + Vector3.up * lineOfSightEyeHeight;
        return !Physics.Linecast(from, to, mask, QueryTriggerInteraction.Ignore);
    }

    private bool IsInCombatFieldOfView(Transform t)
    {
        Vector3 to = t.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f)
            return true;

        return Vector3.Angle(transform.forward, to) <= fieldOfViewAngle * 0.5f;
    }

    private bool IsFacingTarget(Transform t, float maxDegrees)
    {
        Vector3 to = t.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f)
            return true;

        return Vector3.Angle(transform.forward, to) <= maxDegrees;
    }

    /// <summary>
    /// Patrol→Chase gate: range, FOV cone, AI vision, full NavMesh path.
    /// </summary>
    public void ResetChaseRepathClock()
    {
        _repathTimer = 0f;
        _nextForceRetargetTime = 0f;
    }

    public void RefreshChaseDestinationImmediate()
    {
        ResetChaseRepathClock();
        TrySetChaseDestinationValidated();
    }

    public void InterruptPassiveBehavior()
    {
        if (_isTraversingOffMeshLink && _agent != null && _agent.isOnOffMeshLink)
            _agent.CompleteOffMeshLink();
        _isTraversingOffMeshLink = false;
        _patrolTimer = 0f;
        _idleTimer = 0f;
        if (IsAgentReady())
            _agent.isStopped = false;
    }

    private void TryImmediateCombatInterrupt()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Jumping || EndMatchCinematic.GameplayLocked)
            return;

        if (_state == AIStateId.Attack && _attackInProgress && _target != null && IsHostileAlive(_target))
            return;

        _immediateCombatInterruptTimer -= Time.deltaTime;
        if (_immediateCombatInterruptTimer > 0f)
            return;
        _immediateCombatInterruptTimer = 0.12f;

        float scanRadius = Mathf.Max(detectionRadius, aggressiveScanRadius);
        Transform threat = AISensing.FindClosestHostile(this, scanRadius);
        if (threat == null || !IsHostileAlive(threat))
            return;

        _target = threat;
        _targetLockTimer = Time.time + targetLockDuration;
        InterruptPassiveBehavior();

        if (GetCombatDistanceToTarget() > meleeAttackRange)
            TransitionTo(AIStateId.Chase);
    }

    public bool CanEngageChase(Transform t)
    {
        if (!IsTargetValid(t))
            return false;

        float d = Vector3.Distance(transform.position, t.position);
        if (d <= detectionRadius)
            return true;

        float maxRange = Mathf.Max(detectionRadius, aggressiveScanRadius);
        if (d > maxRange)
            return false;

        if (_chaseGateTarget == t && Time.time < _chaseGateTimer)
            return _chaseGateResult;

        NavMesh.CalculatePath(transform.position, t.position, NavMesh.AllAreas, _pathScratch);
        _chaseGateTarget = t;
        _chaseGateTimer = Time.time + ChaseGateRecheckInterval;
        _chaseGateResult = _pathScratch.status == NavMeshPathStatus.PathComplete
            || _pathScratch.status == NavMeshPathStatus.PathPartial;
        return _chaseGateResult;
    }

    /// <summary>
    /// Keeps agents chasing: re-enable movement, refresh stale paths, transition idle→chase when a target exists.
    /// </summary>
    private void TickCombatDrive()
    {
        if (_state == AIStateId.Dead || EndMatchCinematic.GameplayLocked)
            return;

        if (_agent != null && _agent.enabled && !_agent.isOnNavMesh)
            EnsureAgentOnNavMesh();

        if (_target == null || !IsHostileAlive(_target))
            return;

        if (GetCombatDistanceToTarget() > meleeAttackRange)
        {
            if (_state != AIStateId.Chase && _state != AIStateId.Approach && _state != AIStateId.Jumping)
                TransitionTo(AIStateId.Chase);
            AIMotor.EnforceSprintPursuit(this);
        }
    }

    private void TickDebugLog()
    {
        _enemyMoveDebugTimer -= Time.deltaTime;
        if (_enemyMoveDebugTimer > 0f)
            return;
        _enemyMoveDebugTimer = 1f;

        float vel = (_agent != null && _agent.enabled) ? _agent.velocity.magnitude : 0f;
        float dist = _target != null ? Vector3.Distance(transform.position, _target.position) : -1f;
        Debug.Log(
            $"[EnemyMove] {name} state={_state} vel={vel:F2} isStopped={(_agent != null ? _agent.isStopped.ToString() : "N/A")} " +
            $"target={(_target != null ? _target.name : "null")} dist={dist:F1}",
            this);
    }

    private void TickAntiFreeze()
    {
        if (_state == AIStateId.Dead || _state == AIStateId.Jumping || _state == AIStateId.Flinch
            || _state == AIStateId.Attack || _state == AIStateId.StuckRecovery
            || EndMatchCinematic.GameplayLocked)
        {
            _staticFrameCount = 0;
            _lastAntiFreezePosCheck = transform.position;
            return;
        }

        if (_target == null || !IsHostileAlive(_target))
        {
            _staticFrameCount = 0;
            _lastAntiFreezePosCheck = transform.position;
            return;
        }

        if ((transform.position - _lastAntiFreezePosCheck).sqrMagnitude < 0.0004f)
        {
            _staticFrameCount++;
        }
        else
        {
            _staticFrameCount = 0;
            _lastAntiFreezePosCheck = transform.position;
        }

        if (_staticFrameCount < 4)
            return;

        _staticFrameCount = 0;
        _lastAntiFreezePosCheck = transform.position;

        if (!IsAgentReady())
        {
            EnsureAgentOnNavMesh();
            return;
        }

        _agent.isStopped = false;
        _agent.ResetPath();
        _agent.SetDestination(_target.position);

        if (_state != AIStateId.Chase && _state != AIStateId.Approach)
            TransitionTo(AIStateId.Chase);

    }

    public void TrySetChaseDestinationValidated()
    {
        if (_target == null || !IsAgentReady())
            return;

        Vector3 dest = ClampChaseDestination(_target.position);
        if (!_agent.pathPending && _agent.hasPath && _agent.pathStatus != NavMeshPathStatus.PathInvalid)
        {
            float destDelta = (_agent.destination - dest).sqrMagnitude;
            if (destDelta < 2.25f)
                return;
        }

        NavMesh.CalculatePath(transform.position, _target.position, NavMesh.AllAreas, _pathScratch);

        if (_pathScratch.status != NavMeshPathStatus.PathInvalid)
        {
            if (NavMesh.Raycast(transform.position, dest, out NavMeshHit edgeHit, NavMesh.AllAreas))
                dest = edgeHit.position;

            _agent.SetDestination(dest);
            LogMoveDestination(dest);
            return;
        }

        Debug.Log("[AIMove] no path, reacquiring target");
        RepositionOnInvalidPath();
        if (debugAI)
            Debug.Log($"[EnemyAI] {name} initial chase path {_pathScratch.status}, repositioned.", this);
    }

    private void LogMoveDestination(Vector3 dest)
    {
        if (!MultiplayerMode.IsMultiplayer)
            return;

        if (!_destinationSuccessLogged)
        {
            _destinationSuccessLogged = true;
            Debug.Log("[AIMove] SetDestination enemy " + name + " target=" + dest);
        }

        if (Time.time >= _aiBuildLogTimer)
        {
            _aiBuildLogTimer = Time.time + 2.5f;
            Debug.Log("[AIMove] agent velocity=" + (_agent != null ? _agent.velocity.ToString() : "null"));
        }
    }

    private Vector3 ClampChaseDestination(Vector3 raw)
    {
        if (float.IsNaN(_spawnY))
            return raw;

        float maxY = _spawnY + maxChaseVerticalDelta;
        if (raw.y <= maxY)
            return raw;

        Vector3 clamped = raw;
        clamped.y = maxY;
        if (NavMesh.SamplePosition(clamped, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            return hit.position;

        return clamped;
    }

    private void RepositionOnInvalidPath()
    {
        if (!IsAgentReady())
            return;

        Vector3 probe = transform.position + Random.insideUnitSphere * 5f;
        probe.y = float.IsNaN(_spawnY) ? transform.position.y : _spawnY;

        Vector3 playerNav = GetCachedPlayerTransform() != null
            ? GetCachedPlayerTransform().position
            : transform.position;
        if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(probe, playerNav, 10f, out Vector3 valid))
        {
            _agent.ResetPath();
            _agent.Warp(valid);
            _spawnY = valid.y;
            return;
        }

        TryRecoveryWarp(transform.position, 4f, "invalid_path");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════════════════

    private void ConfigureAgent()
    {
        if (_agent == null) return;

        _agent.speed                 = chaseSpeed;
        _agent.stoppingDistance      = CombatStoppingDistance;
        _agent.acceleration          = agentAcceleration;
        _agent.angularSpeed          = agentAngularSpeed;
        _agent.avoidancePriority     = Random.Range(30, 70);
        _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        _agent.radius                = 0.45f;
        _agent.baseOffset            = 0f;   // FIX: zero so enemy root sits on NavMesh, not above it
        _agent.autoBraking           = false;
        _agent.autoRepath            = true;
        _agent.updatePosition        = true;
        // Manual rotation in LateUpdate follows NavMesh velocity — avoids instant agent snap + moonwalk.
        _agent.updateRotation        = false;
        _agent.autoTraverseOffMeshLink = false;
    }

    public void TransitionTo(AIStateId newState)
    {
        if (_state == newState && newState != AIStateId.Chase && newState != AIStateId.Approach)
            return;

        AIStateId prevState = _state;
        if (newState == AIStateId.StuckRecovery && prevState != AIStateId.StuckRecovery)
            _resumeAfterStuck = MapResumeAfterStuck(prevState);

        if (newState == AIStateId.Chase || newState == AIStateId.Approach || newState == AIStateId.Attack)
            InterruptPassiveBehavior();

        if (MultiplayerMode.IsMultiplayer && (newState == AIStateId.Patrol || newState == AIStateId.Chase || newState == AIStateId.Attack))
            Debug.Log("[AIState] enemy " + name + " state = " + newState);

        if (debugAICombat)
            Debug.Log($"[EnemyAI] {name} transitioned from {prevState} to {newState}. Target={(_target != null ? _target.name : "null")}");

        if (newState == AIStateId.Jumping || newState == AIStateId.Dead)
        {
            OnAIStateExit(prevState, newState);
            SyncAIStateId(newState);
            OnAIStateEnter(prevState, newState);
            return;
        }

        if (_aiFsm != null)
            _aiFsm.SetState(newState, true);
        else
            SyncAIStateId(newState);
    }

    public void OnAIStateExit(AIStateId previous, AIStateId next)
    {
        if (previous == AIStateId.Attack && next != AIStateId.Attack)
            AbortActiveAttack();
    }

    public void OnAIStateEnter(AIStateId previous, AIStateId next)
    {
        if (!IsAgentReady())
            return;

        switch (next)
        {
            case AIStateId.Patrol:
                _agent.isStopped = false;
                _agent.speed = moveSpeed;
                break;
            case AIStateId.Chase:
            case AIStateId.Approach:
                _idleTimer = 0f;
                ResetChaseRepathClock();
                break;
            case AIStateId.Evade:
                _agent.isStopped = false;
                _agent.speed = moveSpeed;
                break;
            case AIStateId.StuckRecovery:
                _agent.isStopped = false;
                break;
        }
    }

    private static AIStateId MapResumeAfterStuck(AIStateId previous)
    {
        switch (previous)
        {
            case AIStateId.Attack:
            case AIStateId.Approach:
            case AIStateId.Flinch:
                return AIStateId.Chase;
            default:
                return previous;
        }
    }

    private void FaceTarget()
    {
        if (_target == null) return;
        FaceDirection(_target.position - transform.position);
    }

    public void FaceDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f) return;

        // ★ Exact port of PlayerController's body-rotation Slerp:
        //     rot = Slerp(rot, Euler(0, targetY, 0), rate * dt)
        // Using only the Y-axis of the target rotation keeps the enemy
        // upright even if the target is on uneven ground.
        float targetY = Quaternion.LookRotation(direction.normalized, Vector3.up).eulerAngles.y;
        Quaternion targetRot = Quaternion.Euler(0f, targetY, 0f);

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            targetRot,
            rotationSpeed * Time.deltaTime);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FFA TARGET SCANNING — "All-Out War" logic with Target Locking
    //
    //  Each scan tick (detectionInterval seconds):
    //    1. Decrement the lock timer.
    //    2. If our current target is still valid AND lock > 0 AND still in a
    //       reasonable range → KEEP IT. (No more flip-flopping every tick.)
    //    3. If recent attacker memory is hot and the attacker is valid → that
    //       overrides lock and becomes our new target.
    //    4. Otherwise scan for the closest valid threat (enemy OR player) and
    //       only switch if the new candidate is meaningfully closer (hysteresis).
    //
    //  This creates stable engagements — enemies commit to a fight instead of
    //  constantly re-evaluating targets every frame.
    // ════════════════════════════════════════════════════════════════════════

    private IEnumerator TargetScanLoop()
    {
        // Stagger scan start so all spawned enemies don't all scan on the
        // same frame (tiny perf win + prevents synchronized destination thrash).
        yield return new WaitForSeconds(Random.Range(0f, detectionInterval));

        var wait = new WaitForSeconds(detectionInterval);
        while (true)
        {
            if (_state != AIStateId.Dead && HasAiDecisionAuthority())
                EvaluateTargets();
            yield return wait;
        }
    }

    /// <summary>
    /// Called by MpBotDirector on master client to give bots a priority
    /// human-player target list (Co-op Survival mode).
    /// Pass null or empty list to revert to default FFA targeting.
    /// </summary>
    public void SetMultiplayerTargets(System.Collections.Generic.List<Transform> humanTargets)
    {
        _mpHumanTargets = humanTargets;
    }

    public void PrepareForMultiplayerAi()
    {
        ConfigureAgent();
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer && PhotonNetwork.IsMasterClient)
        {
            PhotonView view = GetComponent<PhotonView>();
            if (view != null && PhotonNetwork.MasterClient != null && view.OwnerActorNr != PhotonNetwork.MasterClient.ActorNumber)
            {
                view.TransferOwnership(PhotonNetwork.MasterClient);
                Debug.Log("[AIAuth] transferred enemy authority to MasterClient");
            }
        }
#endif

        bool onNavMesh = EnsureAgentOnNavMesh();
        Debug.Log("[AINav] enemy " + name + " isOnNavMesh = " + onNavMesh.ToString().ToLowerInvariant());
        if (!onNavMesh)
            return;

        _agent.enabled = true;
        _agent.isStopped = false;
        _agent.updatePosition = true;
        _agent.updateRotation = false;
        _agent.speed = Mathf.Max(0.1f, _agent.speed);
        _agent.acceleration = Mathf.Max(0.1f, _agent.acceleration);
        _agent.angularSpeed = Mathf.Max(30f, _agent.angularSpeed);

        if (_scanCoroutine == null && isActiveAndEnabled)
            _scanCoroutine = StartCoroutine(TargetScanLoop());

        Debug.Log("[AICombat] sensing enabled");
        Debug.Log("[AICombat] attack state enabled");
        EvaluateTargets();
        if (_target != null)
            Debug.Log("[AICombat] target acquired = " + (_target.GetComponentInParent<PlayerController>() != null ? "PLAYER" : "ENEMY"));
    }

    private void EvaluateTargets()
    {
        if (_mpHumanTargets != null && _mpHumanTargets.Count > 0)
        {
            Transform nearest = null;
            float nearestD = float.MaxValue;
            foreach (Transform t in _mpHumanTargets)
            {
                if (t == null)
                    continue;
                IDamageable dmg = t.GetComponentInParent<IDamageable>();
                if (dmg == null || !dmg.IsAlive)
                    continue;
                float d = Vector3.Distance(transform.position, t.position);
                if (d < nearestD)
                {
                    nearestD = d;
                    nearest = t;
                }
            }

            if (nearest != null && nearestD <= Mathf.Max(detectionRadius, aggressiveScanRadius))
            {
                ApplyTargetSelection(nearest);
                return;
            }
        }

        float effectiveScanRadius = Mathf.Max(detectionRadius, aggressiveScanRadius);
        Transform candidate = AISensing.FindClosestHostile(this, effectiveScanRadius);
        if (candidate == null)
            candidate = FindFallbackTarget();

        if (candidate == null)
        {
            _target = null;
            if (_state != AIStateId.Dead && _state != AIStateId.Flinch && _state != AIStateId.Jumping
                && _state != AIStateId.Chase && _state != AIStateId.Approach && _state != AIStateId.Attack)
                TransitionTo(AIStateId.Patrol);
            return;
        }

        if (IsTargetValid(_target) && _target != candidate)
        {
            if (!AISensing.ShouldSwitchTarget(this, _target, candidate, targetSwitchHysteresis))
                candidate = _target;
        }

        ApplyTargetSelection(candidate);
    }

    private void ApplyTargetSelection(Transform candidate)
    {
        if (candidate == null)
            return;

        bool targetChanged = candidate != _target;
        if (targetChanged)
        {
            _target = candidate;
            _targetLockTimer = Time.time + targetLockDuration;
            bool isPlayer = _target.GetComponentInParent<PlayerController>() != null;
            if (MultiplayerMode.IsMultiplayer)
                Debug.Log("[AICombat] target acquired = " + (isPlayer ? "PLAYER" : "ENEMY"));
            if (debugEnemyMovement)
                Debug.Log($"[EnemyTarget] {name} acquired {_target.name} ({(isPlayer ? "PLAYER" : "ENEMY")}) dist={Vector3.Distance(transform.position, _target.position):F1}", this);
        }

        if (!IsHostileAlive(_target))
            return;

        if (GetCombatDistanceToTarget() > meleeAttackRange)
            TransitionTo(AIStateId.Chase);
        else if (_state != AIStateId.Attack && CanBeginMeleeAttack())
            TransitionTo(AIStateId.Attack);
    }

    private Transform FindFallbackTarget()
    {
        Transform player = FindPlayerTarget();
        if (player != null && IsHostileAlive(player))
            return player;

        Transform nearestEnemy = FindNearestLivingEnemy();
        if (nearestEnemy != null && IsHostileAlive(nearestEnemy))
            return nearestEnemy;

        return player;
    }

    public void SetRandomPatrolDestination()
    {
        if (!IsAgentReady())
            return;

        Vector3 random = transform.position + Random.insideUnitSphere * patrolRadius;
        random.y = transform.position.y;

        if (NavMesh.SamplePosition(random, out NavMeshHit hit, patrolRadius, NavMesh.AllAreas))
        {
            _agent.SetDestination(hit.position);
            LogMoveDestination(hit.position);
        }
    }

    public bool HandleOffMeshLinkTraversal()
    {
        if (!IsAgentReady() || _isTraversingOffMeshLink)
            return false;

        if (!_agent.isOnOffMeshLink)
            return false;

        StartCoroutine(TraverseOffMeshLink());
        return true;
    }

    private IEnumerator TraverseOffMeshLink()
    {
        _isTraversingOffMeshLink = true;
        if (!IsAgentReady())
        {
            _isTraversingOffMeshLink = false;
            yield break;
        }

        OffMeshLinkData data = _agent.currentOffMeshLinkData;
        Vector3 startPos = transform.position;
        Vector3 endPos = data.endPos + Vector3.up * _agent.baseOffset;
        float duration = Mathf.Max(0.1f, offMeshJumpDuration);
        float t = 0f;

        SetAnimatorBool(HashGrounded, false);
        _agent.isStopped = true;
        _agent.updatePosition = false;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            float arc = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI) * offMeshJumpHeight;
            Vector3 pos = Vector3.Lerp(startPos, endPos, Mathf.Clamp01(t));
            pos.y += arc;
            transform.position = pos;
            yield return null;
        }

        transform.position = endPos;
        _agent.updatePosition = true;
        _agent.CompleteOffMeshLink();
        _agent.isStopped = false;
        SetAnimatorBool(HashGrounded, true);
        _isTraversingOffMeshLink = false;
    }

    /// <summary>
    /// Free-for-All targeting. Returns the closest living IDamageable in
    /// range, preferring visible targets (LoS) and excluding self.
    /// Both the player and other enemies are fair game.
    /// </summary>
    private static readonly Collider[] _targetScanBuffer = new Collider[32];
    private static readonly HashSet<int> _ffaEvaluatedRoots = new HashSet<int>();

    private Transform FindFfaTarget(float radius)
    {
        return AISensing.FindClosestHostile(this, radius);
    }

    private Transform FindPlayerTarget()
    {
        PlayerController pc = ResolveScenePlayer();
        if (pc == null)
            return null;

        PlayerHealth player = pc.GetComponent<PlayerHealth>();
        if (player == null)
            return null;

        IDamageable dmg = player.GetComponent<IDamageable>();
        if (dmg == null || !dmg.IsAlive)
            return null;

        return player.transform;
    }

    private void TrySetDestinationNearTarget()
    {
        if (!IsAgentReady() || _target == null)
            return;

        Vector3 targetPos = _target.position;
        if (NavMesh.SamplePosition(targetPos, out NavMeshHit hit, 4.5f, NavMesh.AllAreas))
        {
            _agent.SetDestination(hit.position);
            LogMoveDestination(hit.position);
        }
    }

    public bool IsAgentReady()
    {
        return _agent != null && _agent.enabled && _agent.isOnNavMesh;
    }

    private void WarpToNearestNavMeshAfterFall()
    {
        if (_agent == null)
            return;

        if (transform.position.y >= -5f)
            return;

        if (!_agent.enabled)
            _agent.enabled = true;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 30f, NavMesh.AllAreas))
        {
            transform.position = hit.position;
            _agent.Warp(hit.position);
            _frozenLastPos = hit.position;
        }
    }

    private Transform FindNearestLivingEnemy()
    {
        Transform nearest = null;
        float nearestDistanceSqr = float.MaxValue;
        Vector3 pos = transform.position;

        for (int i = 0; i < s_aliveEnemies.Count; i++)
        {
            EnemyController otherEnemy = s_aliveEnemies[i];
            if (otherEnemy == null || otherEnemy == this || !otherEnemy.IsAlive)
                continue;

            float distSqr = (otherEnemy.transform.position - pos).sqrMagnitude;
            if (distSqr < nearestDistanceSqr)
            {
                nearestDistanceSqr = distSqr;
                nearest = otherEnemy.transform;
            }
        }

        return nearest;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WEAPON ATTACHMENT — Ghost Melee Fix
    //  Searches for "bip_hand_R" by name first (Crosby rig), then falls back
    //  to other common bone names. A neutral socket is inserted under the
    //  hand so imported bone compensation scales do not collapse the weapon
    //  transform or distort its final world size.
    // ════════════════════════════════════════════════════════════════════════

    public void AttachWeaponToHand(GameObject weaponPrefab, float targetSize = 0.5f, int level = -1)
    {
        if (_weaponAttachInProgress) return;
        if (weaponPrefab == null) return;
        if (level < 0)
            level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        if (equippedWeaponObject != null && _equippedWeaponPrefab == weaponPrefab && _equippedWeaponLevel == level)
            return;

        _weaponAttachInProgress = true;
        if (equippedWeaponObject != null)
        {
            Destroy(equippedWeaponObject);
            equippedWeaponObject = null;
        }
        _equippedWeaponHitbox = null;
        _activeWeaponSocket   = null;
        _activeWeaponHandBone = null;

        // ── Resolve level and pull ALL grip data from the catalog. ───────────
        // This makes AttachWeaponToHand the single source of truth for enemy
        // weapon socketing — LevelBuilder no longer needs to pre-fill inspector
        // fields for grip pose, socket euler, or stabilization.
        WeaponLoadout loadout            = WeaponLoadoutCatalog.Get(level);
        weaponGripLocalPosition          = loadout.EnemyLocalPosition;
        weaponGripLocalEulerAngles       = loadout.EnemyLocalEuler;
        weaponSocketLocalEulerAngles     = WeaponLoadoutCatalog.GetEnemySocketLocalEuler(level);
        // Level 2 katana / Level 9 axe: use identity socket (mirrors PlayerController)
        // so the grip euler lands in the same humanoid hand-space on every rig.
        // Both are single-handed weapons whose catalog euler already matches the
        // player's grip exactly — socket stabilization via weapon_bone_R would
        // cancel out the hand-bone rotation and produce a flipped/inverted result.
        stabilizeWeaponSocketAgainstHandPose = (level != 2 && level != 9 && level != 12);
        ApplySavedRuntimeGripValuesForLevel(level);

        // ── 1. Find right-hand bone ─────────────────────────────────────────
        Transform handBone = null;
        if (_anim != null && _anim.isHuman)
            handBone = _anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (handBone == null)
            handBone = FindBoneByName(transform, "bip_hand_R");
        if (handBone == null)
            handBone = weaponAttachPoint;
        if (handBone == null)
            handBone = FindHandBone(gameObject);

        if (handBone == null)
        {
            handBone = transform;
            Debug.LogWarning($"[EnemyController] '{name}': Right hand bone not found. " +
                             "Weapon attached to root. Drag the hand bone into 'Weapon Attach Point'.");
        }

        // Level 2 katana / Level 9 axe / Level 12 saw: bypass weapon_bone_R (Crosby's
        // weapon socket which has a baked orientation that varies per-prefab) in favour
        // of bip_hand_R so every enemy uses the exact same bone as the stabilization base.
        // Without this, enemies whose Inspector weaponAttachPoint differs get different
        // stabilized socket frames and the same euler produces different visual results.
        // Priority 1 — Unity humanoid API (normalises world rotation across rigs).
        // Priority 2 — bip_hand_R by name (works even when Avatar is Generic).
        if (level == 2 || level == 9 || level == 12)
        {
            Transform overrideHand = null;

            if (_anim != null && _anim.isHuman)
                overrideHand = _anim.GetBoneTransform(HumanBodyBones.RightHand);

            if (overrideHand == null)
                overrideHand = FindBoneByName(transform, "bip_hand_R");

            if (overrideHand != null)
            {
                handBone = overrideHand;
                Debug.Log($"[EnemyController] '{name}' lvl={level}: handBone overridden to '{handBone.name}' (isHuman={(_anim != null && _anim.isHuman)})");
            }
            else
            {
                Debug.LogWarning($"[EnemyController] '{name}' lvl={level}: could not find humanoid RightHand or bip_hand_R — grip may look wrong.");
            }
        }

        // ── 2. Instantiate unparented (clean world-space bounds) ────────────
        equippedWeaponObject = Instantiate(weaponPrefab);
        equippedWeaponObject.name = "WeaponModel";
        equippedWeaponObject.SetActive(true);

        foreach (Transform t in equippedWeaponObject.GetComponentsInChildren<Transform>(true))
            t.gameObject.SetActive(true);

        // ── 3. Measure bounds at unit scale ─────────────────────────────────
        equippedWeaponObject.transform.localScale = Vector3.one;
        float weaponExtent = GetMaxRendererExtent(equippedWeaponObject);
        if (weaponExtent < 0.001f) weaponExtent = 1f;

        // ── 4. Parent to a neutral socket so imported bone scales do not
        // shrink the weapon transform down to near-zero local values. ───────
        Transform weaponSocket = GetOrCreateWeaponSocket(handBone);
        Quaternion socketRotation = Quaternion.Euler(weaponSocketLocalEulerAngles);
        if (stabilizeWeaponSocketAgainstHandPose)
            socketRotation = Quaternion.Inverse(handBone.localRotation) * socketRotation;
        weaponSocket.localRotation = socketRotation;
        _activeWeaponSocket   = weaponSocket;
        _activeWeaponHandBone = handBone;
        equippedWeaponObject.transform.SetParent(weaponSocket, worldPositionStays: false);
        equippedWeaponObject.transform.localPosition = Vector3.zero;
        equippedWeaponObject.transform.localRotation = Quaternion.identity;
        Transform runtimeGripParent = WeaponLoadoutCatalog.GetOrCreateRuntimeGripAnchor(
            level, weaponPrefab, weaponSocket);
        if (runtimeGripParent != weaponSocket)
        {
            equippedWeaponObject.transform.SetParent(runtimeGripParent, worldPositionStays: false);
            equippedWeaponObject.transform.localPosition = Vector3.zero;
            equippedWeaponObject.transform.localRotation = Quaternion.identity;
        }

        // ── 5. Compute localScale from the ACTUAL inherited world size ─────
        float desiredWorldSize = Mathf.Max(0.01f, targetSize);
        equippedWeaponObject.transform.localScale = Vector3.one;
        float inheritedExtent = GetMaxRendererExtent(equippedWeaponObject);
        if (inheritedExtent < 0.001f) inheritedExtent = weaponExtent;
        float uniformScale = desiredWorldSize / inheritedExtent;
        equippedWeaponObject.transform.localScale = Vector3.one * uniformScale;

        // ── 6. Apply grip pose — catalog EnemyLocalPosition/Euler is the only
        // source of truth; inspector fields were already overwritten above. ──
        if (WeaponLoadoutCatalog.IsChainsawLevel(level, weaponPrefab))
            WeaponLoadoutCatalog.ApplyChainsawEnemyGripPose(equippedWeaponObject.transform);
        else if (!WeaponLoadoutCatalog.ApplyRuntimeGripPose(level, weaponPrefab, equippedWeaponObject.transform))
            ApplyWeaponGripPose();
        WeaponLoadoutCatalog.ApplyRuntimeOverrides(level, weaponPrefab, equippedWeaponObject);

        if (level == 2) ApplyKatanaHumanoidGrip(equippedWeaponObject, _activeWeaponSocket);

        // Level-specific grip fixes by weapon name (player/AI rigs differ).
        if (weaponPrefab != null)
        {
            string wn = weaponPrefab.name.ToLowerInvariant();
            if (wn.Contains("sickle"))
            {
                equippedWeaponObject.transform.localRotation = Quaternion.Euler(weaponGripLocalEulerAngles);
                equippedWeaponObject.transform.localPosition = weaponGripLocalPosition;
            }
        }

        if (WeaponLoadoutCatalog.IsChainsawLevel(level, weaponPrefab))
            WeaponLoadoutCatalog.ApplyChainsawEnemyGripPose(equippedWeaponObject.transform);

        if (level == 14
            && weaponPrefab.name.IndexOf("morgenstern", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            ApplyMorgensternEnemyGripPose(equippedWeaponObject.transform);
        }

        ForceWeaponRenderable(equippedWeaponObject);
        EnsureEnemyWeaponVisibleWorldSize(equippedWeaponObject, desiredWorldSize);
        LogWeaponRestoreState("enemy", equippedWeaponObject, weaponPrefab);

        Debug.Log($"[EnemyController] '{name}' lvl={level} weapon → hand '{handBone.name}' " +
                  $"targetSize={desiredWorldSize} extent={weaponExtent} " +
                  $"localPosition={equippedWeaponObject.transform.localPosition} " +
                  $"localEuler={equippedWeaponObject.transform.localEulerAngles} " +
                  $"localScale={equippedWeaponObject.transform.localScale} " +
                  $"lossyScale={equippedWeaponObject.transform.lossyScale}");

        // ── 6. Disable physics, embedded animators, colliders ───────────────
        foreach (Rigidbody rb in equippedWeaponObject.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic     = true;
            rb.useGravity      = false;
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        foreach (Animator weaponAnimator in equippedWeaponObject.GetComponentsInChildren<Animator>(true))
            weaponAnimator.enabled = false;

        foreach (Collider col in equippedWeaponObject.GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        // ── 7. Wire WeaponBase ──────────────────────────────────────────────
        WeaponBase wb = equippedWeaponObject.GetComponent<WeaponBase>();
        if (wb == null) wb = equippedWeaponObject.AddComponent<WeaponBase>();
        wb.damage      = (int)attackDamage;
        wb.attackRange = attackRadius;
        wb.isRanged    = false;

        // ── 8. Wire WeaponHitbox ────────────────────────────────────────────
        WeaponHitbox hitbox = equippedWeaponObject.GetComponent<WeaponHitbox>();
        if (hitbox == null) hitbox = equippedWeaponObject.AddComponent<WeaponHitbox>();
        hitbox.damage = (int)attackDamage;
        hitbox.meleeAttackRange = GetStrictMeleeStrikeRange();
        hitbox.maxAttackRange = GetStrictMeleeStrikeRange() + 0.35f;
        if (level == 12)
            hitbox.overlapRadius = 0.45f;
        hitbox.DisableHitbox();
        _equippedWeaponHitbox = hitbox;

        // ── 9. Visibility fix (URP) ─────────────────────────────────────────
        if (equippedWeaponObject.GetComponent<WeaponVisibilityFix>() == null)
            equippedWeaponObject.AddComponent<WeaponVisibilityFix>();

        _equippedWeaponPrefab  = weaponPrefab;
        _equippedWeaponLevel   = level;
        _weaponAttachInProgress = false;
        // Cache the idle grip rotation so LateUpdate can use it as a stable
        // base for the attack-swing correction without compounding each frame.
        if (equippedWeaponObject != null)
            _weaponBaseLocalRot = equippedWeaponObject.transform.localRotation;

        // Bind KatanaCombatHandler — it enforces the final grip pose via
        // WeaponGripOffset.Enforce() and routes hit detection on anim events.
        KatanaCombatHandler katanaHandler = GetComponent<KatanaCombatHandler>();
        if (katanaHandler != null)
        {
            WeaponGripOffset grip = equippedWeaponObject.GetComponent<WeaponGripOffset>();
            if (grip != null)
                katanaHandler.BindKatana(grip);
        }
    }

    private static void ForceWeaponRenderable(GameObject weapon)
    {
        if (weapon == null) return;

        weapon.SetActive(true);
        foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
            child.gameObject.SetActive(true);

        Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
        }
    }

    private static void EnsureEnemyWeaponVisibleWorldSize(GameObject weapon, float targetSize)
    {
        if (weapon == null) return;

        float currentWorldSize = GetVisibleWorldSize(weapon);
        float minimumWorldSize = Mathf.Clamp(Mathf.Max(0.01f, targetSize) * 0.75f, 0.22f, 1.0f);
        if (currentWorldSize > 0.001f && currentWorldSize < minimumWorldSize)
        {
            float scaleUp = minimumWorldSize / currentWorldSize;
            weapon.transform.localScale *= scaleUp;
            return;
        }

        if (currentWorldSize <= 0.001f)
        {
            Vector3 scale = weapon.transform.localScale;
            float fallback = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            weapon.transform.localScale = Vector3.one * Mathf.Max(1f, fallback);
        }
    }

    private static float GetVisibleWorldSize(GameObject weapon)
    {
        if (weapon == null) return 0f;

        Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        Bounds bounds = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || renderer.forceRenderingOff)
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
            return 0f;

        Vector3 size = bounds.size;
        return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
    }

    private static void LogWeaponRestoreState(string actor, GameObject weapon, GameObject prefab)
    {
        int rendererCount = CountWeaponRenderers(weapon);
        bool attached = weapon != null
            && weapon.transform.parent != null
            && rendererCount > 0
            && HasNonZeroLocalScale(weapon.transform);

        Debug.Log("[WeaponRestore] backup logic applied");
        Debug.Log($"[WeaponRestore] {actor} weapon attached={attached}");
        Debug.Log($"[WeaponRestore] socket={(weapon != null && weapon.transform.parent != null ? weapon.transform.parent.name : "<none>")}");
        Debug.Log($"[WeaponRestore] prefab={(prefab != null ? prefab.name : (weapon != null ? weapon.name : "<null>"))}");
        Debug.Log($"[WeaponRestore] renderer count={rendererCount}");
    }

    private static int CountWeaponRenderers(GameObject weapon)
    {
        if (weapon == null) return 0;

        int count = 0;
        Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer != null && renderer.enabled && !renderer.forceRenderingOff)
                count++;
        }
        return count;
    }

    private static bool HasNonZeroLocalScale(Transform t)
    {
        if (t == null) return false;
        Vector3 scale = t.localScale;
        return !Mathf.Approximately(scale.x, 0f)
            && !Mathf.Approximately(scale.y, 0f)
            && !Mathf.Approximately(scale.z, 0f);
    }

    private static Transform GetOrCreateWeaponSocket(Transform handBone)
    {
        Transform socket = handBone.Find(WeaponSocketName);
        if (socket == null)
        {
            GameObject socketObject = new GameObject(WeaponSocketName);
            socket = socketObject.transform;
            socket.SetParent(handBone, worldPositionStays: false);
        }

        socket.localPosition = Vector3.zero;
        socket.localRotation = Quaternion.identity;

        Vector3 handLossy = handBone.lossyScale;
        socket.localScale = new Vector3(
            1f / Mathf.Max(Mathf.Abs(handLossy.x), 0.0001f),
            1f / Mathf.Max(Mathf.Abs(handLossy.y), 0.0001f),
            1f / Mathf.Max(Mathf.Abs(handLossy.z), 0.0001f));

        return socket;
    }

    private void ApplyWeaponGripPose()
    {
        if (equippedWeaponObject == null) return;

        equippedWeaponObject.transform.localRotation = Quaternion.Euler(weaponGripLocalEulerAngles);
        equippedWeaponObject.transform.localPosition = weaponGripLocalPosition;
    }

    private static readonly Vector3 MorgensternEnemyGripBasePosition = new Vector3(-0.025f, -0.0025f, 0f);
    private const float MorgensternEnemyHandleGripInsetNormalized = 0.82f;
    private const bool MorgensternEnemyHandleAtDominantMax = true;

    /// <summary>
    /// Attaches the katana so its euler (0,0,160) is applied in humanoid
    /// RightHand space — identical to PlayerController's approach.
    /// If weaponSocket is provided and non-null, the socket localRotation is
    /// reset to identity so no Crosby-basis compensation interferes.
    /// </summary>
    private void ApplyKatanaHumanoidGrip(GameObject weaponObj, Transform weaponSocket)
    {
        if (weaponObj == null) return;

        // Reset socket to identity — mirrors PlayerController.GetOrCreateWeaponSocket
        // which always sets localRotation = Quaternion.identity.
        if (weaponSocket != null)
            weaponSocket.localRotation = Quaternion.identity;

        // Apply the same grip euler the player uses.
        weaponObj.transform.localPosition    = WeaponLoadoutCatalog.Get(2).EnemyLocalPosition;
        weaponObj.transform.localEulerAngles = new Vector3(0f, 0f, 160f);
    }

    private static void ApplyMorgensternEnemyGripPose(Transform weaponRoot)
    {
        if (weaponRoot == null)
            return;

        if (!TryGetCombinedLocalBounds(weaponRoot, out Bounds localBounds))
        {
            weaponRoot.localRotation = Quaternion.Euler(0f, 0f, 90f);
            weaponRoot.localPosition = MorgensternEnemyGripBasePosition;
            return;
        }

        weaponRoot.localRotation = GetMorgensternEnemyLocalRotation(localBounds);
        Vector3 gripPoint = GetMorgensternEnemyGripPoint(localBounds);
        Vector3 scaledGripPoint = Vector3.Scale(gripPoint, weaponRoot.localScale);
        weaponRoot.localPosition = MorgensternEnemyGripBasePosition - (weaponRoot.localRotation * scaledGripPoint);
    }

    private static Quaternion GetMorgensternEnemyLocalRotation(Bounds localBounds)
    {
        int dominantAxis = GetDominantBoundsAxis(localBounds);

        switch (dominantAxis)
        {
            case 0:
                return Quaternion.Euler(0f, MorgensternEnemyHandleAtDominantMax ? 180f : 0f, 0f);
            case 1:
                return Quaternion.Euler(0f, 0f, MorgensternEnemyHandleAtDominantMax ? 90f : -90f);
            default:
                return Quaternion.Euler(0f, MorgensternEnemyHandleAtDominantMax ? -90f : 90f, 0f);
        }
    }

    private static Vector3 GetMorgensternEnemyGripPoint(Bounds localBounds)
    {
        int dominantAxis = GetDominantBoundsAxis(localBounds);
        float min = localBounds.min[dominantAxis];
        float max = localBounds.max[dominantAxis];
        float gripNormalized = MorgensternEnemyHandleAtDominantMax
            ? MorgensternEnemyHandleGripInsetNormalized
            : 1f - MorgensternEnemyHandleGripInsetNormalized;

        Vector3 gripPoint = localBounds.center;
        gripPoint[dominantAxis] = Mathf.Lerp(min, max, gripNormalized);
        return gripPoint;
    }

    private static int GetDominantBoundsAxis(Bounds localBounds)
    {
        int dominantAxis = 0;
        float dominantSize = localBounds.size.x;
        if (localBounds.size.y > dominantSize)
        {
            dominantAxis = 1;
            dominantSize = localBounds.size.y;
        }
        if (localBounds.size.z > dominantSize)
            dominantAxis = 2;

        return dominantAxis;
    }

    private static bool TryGetCombinedLocalBounds(Transform root, out Bounds combinedBounds)
    {
        combinedBounds = new Bounds();
        if (root == null)
            return false;

        bool hasBounds = false;
        Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;

        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            EncapsulateTransformedBounds(
                ref combinedBounds,
                ref hasBounds,
                meshFilter.sharedMesh.bounds,
                rootWorldToLocal * meshFilter.transform.localToWorldMatrix);
        }

        SkinnedMeshRenderer[] skinnedMeshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null)
                continue;

            Bounds sourceBounds = skinnedMesh.localBounds;
            if (sourceBounds.size.sqrMagnitude <= 0f && skinnedMesh.sharedMesh != null)
                sourceBounds = skinnedMesh.sharedMesh.bounds;
            if (sourceBounds.size.sqrMagnitude <= 0f)
                continue;

            EncapsulateTransformedBounds(
                ref combinedBounds,
                ref hasBounds,
                sourceBounds,
                rootWorldToLocal * skinnedMesh.transform.localToWorldMatrix);
        }

        return hasBounds;
    }

    private static void EncapsulateTransformedBounds(
        ref Bounds combinedBounds,
        ref bool hasBounds,
        Bounds sourceBounds,
        Matrix4x4 sourceToRootMatrix)
    {
        Vector3 center = sourceBounds.center;
        Vector3 extents = sourceBounds.extents;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    Vector3 transformedCorner = sourceToRootMatrix.MultiplyPoint3x4(corner);
                    if (!hasBounds)
                    {
                        combinedBounds = new Bounds(transformedCorner, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        combinedBounds.Encapsulate(transformedCorner);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the largest axis of the combined renderer bounds for a weapon GameObject.
    /// Must be called BEFORE parenting (at world scale 1,1,1) for accurate results.
    /// </summary>
    private static float GetMaxRendererExtent(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return 0f;

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        return Mathf.Max(combined.size.x, combined.size.y, combined.size.z);
    }

    // ── Bone search: authored weapon sockets first, then common hand bones ───
    private static readonly string[] HandBoneNames =
    {
        "weapon_bone_R",        // Crosby weapon socket
        "bip_hand_R",           // Crosby primary right hand
        "j_wrist_ri",           // Ronin (player)
        "mixamorig:RightHand",  // Mixamo
        "RightHand",
        "Hand_R", "hand_R", "hand_r",
        "Wrist_R", "wrist_R",
    };

    private static Transform FindHandBone(GameObject body)
    {
        // ── Priority 1: explicit weapon/hand sockets authored on the rig ────
        foreach (string boneName in HandBoneNames)
        {
            Transform found = FindBoneByName(body.transform, boneName);
            if (found != null)
            {
                Debug.Log($"[EnemyController] Hand bone found via name search: '{found.name}'");
                return found;
            }
        }

        // ── Priority 2: Humanoid avatar API fallback ───────────────────────
        Animator anim = body.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform bone = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
            {
                Debug.Log($"[EnemyController] Hand bone found via Animator API: '{bone.name}'");
                return bone;
            }
        }
        return null;
    }

    private static Transform FindBoneByName(Transform root, string boneName)
    {
        if (root.name == boneName) return root;
        foreach (Transform child in root)
        {
            Transform found = FindBoneByName(child, boneName);
            if (found != null) return found;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MATERIAL FIX — "White Statue" prevention
    //  Loads RoninTexture from Resources/Textures.  If the texture is found it
    //  is applied to every blank/missing material slot on the character mesh.
    //  Falls back to a warm skin-tone colour when the file is absent.
    // ════════════════════════════════════════════════════════════════════════

    private static void EnsureProperMaterial(GameObject body)
    {
        if (body == null) return;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard")
                     ?? Shader.Find("Diffuse");
        if (shader == null) return;

        // Folders where Crosby/enemy textures live
        string[] enemyTextureFolders = { "Enemy/textures/" };

        // Fallback: load the best generic enemy texture
        Texture2D fallbackTex = LoadFirstAvailableTexture(
            "Enemy/textures/mtl_c_usa_mp_seal6_gear_wt",
            "Enemy/textures/mtl_c_usa_mp_seal6_ass_vest_green_wt",
            "Enemy/textures/mtl_c_usa_mp_seal6_pants_1_green_wt",
            "Enemy/textures/mtl_c_usa_mp_seal6_helmet_wt",
            "Enemy/textures/mtl_c_usa_mp_seal6_bala_wt");

        foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true))
        {
            Material[] slots = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                if (!IsBlankMaterial(slots[i])) continue;

                // Try to match a texture to this material's name
                string slotName = (slots[i] != null) ? slots[i].name : "";
                Texture2D slotTex = TryLoadTextureForMaterial(slotName, enemyTextureFolders);
                if (slotTex == null) slotTex = fallbackTex;

                Material newMat = new Material(shader)
                {
                    name = string.IsNullOrEmpty(slotName) ? "EnemyMat_Runtime" : slotName + "_Fix"
                };
                if (slotTex != null)
                {
                    if (newMat.HasProperty("_BaseMap")) newMat.SetTexture("_BaseMap", slotTex);
                    if (newMat.HasProperty("_MainTex")) newMat.SetTexture("_MainTex", slotTex);
                }
                else
                {
                    // Warm tan fallback so they don't look white
                    Color tan = new Color(0.68f, 0.52f, 0.38f);
                    if (newMat.HasProperty("_BaseColor")) newMat.SetColor("_BaseColor", tan);
                    if (newMat.HasProperty("_Color"))     newMat.SetColor("_Color",     tan);
                }
                slots[i] = newMat;
                changed = true;
            }
            if (changed) r.sharedMaterials = slots;
        }
    }

    /// <summary>
    /// Given a material name (e.g. "mtl_c_usa_mp_seal6_gear_wt"),
    /// try to find a matching texture in the enemy textures folder.
    /// </summary>
    private static Texture2D TryLoadTextureForMaterial(string matName, string[] folders)
    {
        if (string.IsNullOrEmpty(matName)) return null;
        string clean = matName.Replace(" (Instance)", "").Trim();
        if (string.IsNullOrEmpty(clean)) return null;

        foreach (string folder in folders)
        {
            Texture2D tex;
            tex = Resources.Load<Texture2D>(folder + clean);       if (tex != null) return tex;
            tex = Resources.Load<Texture2D>(folder + clean + "_c"); if (tex != null) return tex;
        }
        return null;
    }

    private static bool IsBlankMaterial(Material m)
    {
        if (m == null) return true;
        if (m.name.StartsWith("Default-")) return true;
        if (m.shader != null && m.shader.name == "Hidden/InternalErrorShader") return true;
        bool hasTexture = (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null)
                       || (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null);
        if (hasTexture) return false;
        Color c = Color.white;
        if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
        else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
        return c.r > 0.95f && c.g > 0.95f && c.b > 0.95f;
    }

    private static Texture2D LoadFirstAvailableTexture(params string[] resourcePaths)
    {
        for (int i = 0; i < resourcePaths.Length; i++)
        {
            Texture2D tex = Resources.Load<Texture2D>(resourcePaths[i]);
            if (tex != null) return tex;
        }
        return null;
    }

    private void LoadSavedRuntimeGripValues()
    {
        if (!useSavedRuntimeGripValues)
            return;

        int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        ApplySavedRuntimeGripValuesForLevel(level);
    }

    private void ApplySavedRuntimeGripValuesForLevel(int level)
    {
        if (!useSavedRuntimeGripValues)
            return;

        if (level == 13)
        {
            weaponGripLocalPosition = LoadVector3Pref(PrefKeySicklePos, weaponGripLocalPosition);
            weaponGripLocalEulerAngles = LoadVector3Pref(PrefKeySickleEuler, weaponGripLocalEulerAngles);
        }
        else if (level == 12)
        {
            PlayerPrefs.DeleteKey(PrefKeySawPos + ".x");
            PlayerPrefs.DeleteKey(PrefKeySawPos + ".y");
            PlayerPrefs.DeleteKey(PrefKeySawPos + ".z");
            PlayerPrefs.DeleteKey(PrefKeySawEuler + ".x");
            PlayerPrefs.DeleteKey(PrefKeySawEuler + ".y");
            PlayerPrefs.DeleteKey(PrefKeySawEuler + ".z");
        }
    }

    private static Vector3 LoadVector3Pref(string key, Vector3 fallback)
    {
        if (!PlayerPrefs.HasKey(key + ".x")
            || !PlayerPrefs.HasKey(key + ".y")
            || !PlayerPrefs.HasKey(key + ".z"))
        {
            return fallback;
        }

        return new Vector3(
            PlayerPrefs.GetFloat(key + ".x", fallback.x),
            PlayerPrefs.GetFloat(key + ".y", fallback.y),
            PlayerPrefs.GetFloat(key + ".z", fallback.z));
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRadius);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, separationRadius);
        Gizmos.color = new Color(0.35f, 0.55f, 1f, 0.85f);
        Gizmos.DrawWireSphere(transform.position, alignmentRadius);
        Gizmos.color = new Color(0.35f, 0.85f, 0.45f, 0.85f);
        Gizmos.DrawWireSphere(transform.position, cohesionRadius);

        // Show obstacle-check ray
        Gizmos.color = Color.magenta;
        Vector3 origin = transform.position + Vector3.up * 0.6f;
        Gizmos.DrawRay(origin, transform.forward * obstacleCheckDist);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  AGGRESSION TUNING (added 2026-05-21)
    //  Pure tuning shim: bakes the new multipliers into the same agent fields
    //  ConfigureAgent already drives. Called once at Start, and live each tick
    //  via the existing chase-update path because UpdateChaseMovement writes
    //  _agent.speed/acceleration/angularSpeed itself — we only need to update
    //  the source-of-truth `chaseSpeed`, `agentAcceleration`, `agentAngularSpeed`
    //  fields so those writes pick up the multipliers.
    // ════════════════════════════════════════════════════════════════════════
    private bool _aggressionApplied;
    private float _baseChaseSpeed;
    private float _baseAgentAccel;
    private float _baseAgentAngular;
    private float _baseAnimatorSpeed = 1f;
    private void ApplyAggressionTuning()
    {
        if (_aggressionApplied) return;
        _aggressionApplied = true;

        _baseChaseSpeed   = chaseSpeed;
        _baseAgentAccel   = agentAcceleration;
        _baseAgentAngular = agentAngularSpeed;

        chaseSpeed         = _baseChaseSpeed   * chaseSpeedMultiplier;
        agentAcceleration  = _baseAgentAccel   * Mathf.Max(1f, chaseSpeedMultiplier);
        agentAngularSpeed  = _baseAgentAngular * Mathf.Max(0.5f, turnResponsiveness);

        // Animator speed is read by the existing SyncAnimator path; bumping the
        // root Animator speed is the cleanest one-shot to make locomotion read
        // less robotic without changing any transition graph.
        Animator a = GetComponentInChildren<Animator>();
        if (a != null)
        {
            _baseAnimatorSpeed = a.speed > 0.01f ? a.speed : 1f;
            a.speed = _baseAnimatorSpeed * Mathf.Max(0.5f, animatorSpeedMultiplier);
        }

        if (_agent != null && _agent.enabled)
        {
            _agent.acceleration = agentAcceleration;
            _agent.angularSpeed = agentAngularSpeed;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WATCHDOG (added 2026-05-21)
    //  Two cheap checks per frame; no allocations, no FindObject calls.
    //   1. Y-above-spawn rooftop trap — warps the agent back down to the
    //      nearest NavMesh sample near the original spawn Y.
    //   2. No-path-progress timeout — resets the path and re-acquires a target
    //      when the enemy is stuck rotating in place without closing distance.
    // ════════════════════════════════════════════════════════════════════════
    private void TickWatchdog()
    {
        if (_state == AIStateId.Dead) return;
        if (_agent == null || !_agent.enabled) return;

        float now = Time.time;

        _navValidateTimer -= Time.deltaTime;
        if (_navValidateTimer <= 0f)
        {
            _navValidateTimer = 0.45f;
            if (!_agent.isOnNavMesh)
                EnsureAgentOnNavMesh();
        }

        if (!_agent.isOnNavMesh)
            return;

        if (!float.IsNaN(_spawnY))
        {
            float yDelta = transform.position.y - _spawnY;

            // ── 1. Rooftop trap ──────────────────────────────────────────────
            if (yDelta > watchdogMaxYAboveSpawn)
            {
                if (_highSinceTime < 0f) _highSinceTime = now;
                else if (now - _highSinceTime >= watchdogInvalidHighSeconds)
                {
                    Vector3 anchor = _target != null
                        ? new Vector3(_target.position.x, _spawnY, _target.position.z)
                        : new Vector3(transform.position.x, _spawnY, transform.position.z);
                    TryRecoveryWarp(anchor, 14f, "rooftop");
                    _highSinceTime = -1f;
                    _lowProgressSinceTime = -1f;
                }
            }
            else
            {
                _highSinceTime = -1f;
            }
        }

        // ── 2. No path progress while chasing ────────────────────────────────
        if (_target != null && _agent.hasPath && !_agent.pathPending)
        {
            float moved = (transform.position - _watchdogLastPosition).sqrMagnitude;
            if (moved < 0.04f)
            {
                if (_lowProgressSinceTime < 0f) _lowProgressSinceTime = now;
                else if (now - _lowProgressSinceTime >= watchdogStuckPathSeconds)
                {
                    _agent.ResetPath();
                    EvaluateTargets();
                    if (_target != null && IsTargetValid(_target))
                        TrySetChaseDestinationValidated();
                    else
                        RepositionOnInvalidPath();

                    if (debugWatchdog)
                        Debug.Log($"[EnemyWatchdog] {name} stuck path reset; reacquiring target.");
                    _lowProgressSinceTime = -1f;
                }
            }
            else
            {
                _lowProgressSinceTime = -1f;
                _watchdogLastPosition = transform.position;
            }

            if (_agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                if (_partialPathSinceTime < 0f) _partialPathSinceTime = now;
                else if (now - _partialPathSinceTime >= partialPathMaxSeconds)
                {
                    RepositionOnInvalidPath();
                    _partialPathSinceTime = -1f;
                }
            }
            else
            {
                _partialPathSinceTime = -1f;
            }
        }
        else
        {
            _lowProgressSinceTime = -1f;
            _partialPathSinceTime = -1f;
            _watchdogLastPosition = transform.position;
        }

        // ── 3. Out-of-bounds horizontal recovery ─────────────────────────────
        if (!IsInsidePlayableBounds(transform.position))
        {
            float halfSize = LevelBuilder.Instance != null
                ? LevelBuilder.Instance.arenaHalfSize
                : 80f;
            Vector3 flat = new Vector3(transform.position.x, 0f, transform.position.z);
            if (flat.sqrMagnitude > 0.01f)
            {
                Vector3 warpTarget = flat.normalized * (halfSize * 0.9f);
                warpTarget.y = float.IsNaN(_spawnY) ? transform.position.y : _spawnY;
                TryRecoveryWarp(warpTarget, 15f, "out_of_bounds");
            }
        }

        // ── 4. Invalid / unreachable target ────────────────────────────────
        if (_target != null && !IsTargetValid(_target))
        {
            _target = null;
            EvaluateTargets();
        }
        else if (_target != null && _state == AIStateId.Chase && !CanEngageChase(_target))
        {
            if (_idleMotionSinceTime < 0f) _idleMotionSinceTime = now;
            else if (now - _idleMotionSinceTime >= idleMotionTimeout)
            {
                _target = null;
                EvaluateTargets();
                _idleMotionSinceTime = -1f;
            }
        }
        else
        {
            _idleMotionSinceTime = -1f;
        }

        // ── 5. Ground alignment ───────────────────────────────────────────────
        // Handles the case where the NavMesh is baked at the wrong height in a
        // section of the map (flat areas with terrain mismatches). The agent is
        // "on" the NavMesh but floating visually because the baked surface sits
        // above the actual terrain geometry.
        //
        // Runs every ~1.5 s per enemy (staggered by instance ID) — not every
        // frame, so there is no per-frame raycast cost for a full squad.
        CheckGroundAlignment();
    }

    private float _groundAlignTimer;

    private void CheckGroundAlignment()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return;
        if (_state == AIStateId.Dead || _state == AIStateId.Jumping || _state == AIStateId.Flinch) return;

        // Stagger per instance so 20 enemies don't all raycast on the same frame.
        _groundAlignTimer -= Time.deltaTime;
        if (_groundAlignTimer > 0f) return;
        _groundAlignTimer = 1.5f + (GetInstanceID() % 7) * 0.07f;

        // Cast downward to find actual solid terrain geometry.
        int groundMask = ~(1 << gameObject.layer); // exclude own colliders
        Vector3 origin = transform.position + Vector3.up * 0.15f;

        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 8f, groundMask, QueryTriggerInteraction.Ignore))
            return; // no solid ground found below — avoid warping over a pit

        float floatHeight = hit.distance - 0.15f; // metres above actual ground

        // Only act when visually floating more than half a metre.
        if (floatHeight <= 0.5f) return;

        // Try to find a NavMesh surface at or near the actual ground hit point.
        Vector3 groundPos = hit.point + Vector3.up * 0.05f;
        if (!NavMesh.SamplePosition(groundPos, out NavMeshHit navHit, 2f, NavMesh.AllAreas))
            return;

        // Only warp if the found surface is meaningfully lower than current position.
        if (transform.position.y - navHit.position.y < 0.35f) return;

        // Update _spawnY so the rooftop watchdog uses the corrected ground reference.
        _spawnY = navHit.position.y;
        _agent.Warp(navHit.position);

        if (debugWatchdog)
            Debug.Log($"[GroundAlign] {name} was floating {floatHeight:F2}m above terrain → warped to ground NavMesh at y={navHit.position.y:F2}", this);
    }
}
