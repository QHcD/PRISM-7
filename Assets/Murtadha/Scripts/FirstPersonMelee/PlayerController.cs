using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
#endif

/// <summary>
/// Main player controller — melee-only.
/// Handles movement, camera, combat, weapon management, and animation.
/// Uses CharacterController for precise, non-physics movement.
///
/// Animation is driven entirely by a standard Animator Controller.
/// Parameters fed each frame: Speed, IsGrounded, IsAttacking, IsSprinting.
/// The Animator Controller is responsible for all blending and leg motion.
/// </summary>
[DefaultExecutionOrder(50)]
public class PlayerController : MonoBehaviour, IDamageable
{
    // ── IDamageable bridge ───────────────────────────────────────────────────
    // PlayerHealth is the authoritative health component but may be missing from
    // the prefab. Implementing IDamageable here guarantees enemy weapon hitboxes
    // always find a valid damage receiver on the player GameObject.
    public bool IsAlive => _playerHealth == null || _playerHealth.IsAlive;
    public void ReceiveDamage(int amount, GameObject attackerRoot)
    {
        if (_playerHealth == null)
            _playerHealth = GetComponent<PlayerHealth>() ?? gameObject.AddComponent<PlayerHealth>();
        _playerHealth.ReceiveDamage(amount, attackerRoot);
    }
    private PlayerHealth        _playerHealth;
    private KatanaCombatHandler _katanaCombatHandler;

    private const string WeaponSocketName = "PlayerRightHandWeaponSocket";
    private const string RuntimeThirdPersonCameraName = "RuntimeThirdPersonCamera";
    private const float ThirdPersonMinPitch = -12f;
    private const float ThirdPersonMaxPitch = 45f;
    private const float ThirdPersonInitialPitch = 8f;
    private const float FirstPersonMinPitch = -80f;
    private const float FirstPersonMaxPitch = 80f;
    private const float ForcedSeparationDistance = 1.0f;
    private const float MeleeImpactRadius = 0.78f;
    private const string PrefKeySicklePos = "Grip.Player.L13.Sickle.Pos";
    private const string PrefKeySickleEuler = "Grip.Player.L13.Sickle.Euler";
    private const string PrefKeySawPos = "Grip.Player.L12.Saw.Pos";
    private const string PrefKeySawEuler = "Grip.Player.L12.Saw.Euler";
    private static readonly Vector3 SafeFallbackSpawn = new Vector3(0f, 1f, 0f);
    private static readonly Vector3 ChainsawSocketLocalPosition = Vector3.zero;
    private static readonly Vector3 ChainsawSocketLocalEuler = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 DefaultLevel13SickleGripLocalPosition = new Vector3(0.006f, 0.002f, 0.026f);
    private static readonly Vector3 DefaultLevel13SickleGripLocalEuler = new Vector3(86f, 5f, 98f);
    private static readonly Vector3 DefaultLevel12SawGripLocalPosition = WeaponLoadoutCatalog.ChainsawPlayerLocalPosition;
    private static readonly Vector3 DefaultLevel12SawGripLocalEuler = WeaponLoadoutCatalog.ChainsawPlayerLocalEuler;
    private static readonly Vector3 PlayerChainsawGripLocalPosition = new Vector3(-0.066f, -0.39f, 0.044f);
    private static readonly Vector3 PlayerChainsawGripLocalEuler = new Vector3(-177.177f, -175.886f, 88.481f);
    // Level 2 Razor Katana player grip.
    // IMPORTANT: these are HAND-SOCKET LOCAL values, not scene/world coordinates.
    // Matches the enemy sword basis so the handle stays in the right palm/fingers.
// جرّب هذه بدل القيم الحالية
private static readonly Vector3 PlayerKatanaGripLocalPosition = new Vector3(-0.01f, -0.0025f, 0f);
private static readonly Vector3 PlayerKatanaGripLocalEuler = new Vector3(0f, 0f, 160f);
private static readonly Vector3 PlayerKatanaGripLocalScale = new Vector3(0.2f, 0.3f, 0.2f);

    // ════════════════════════════════════════════════════════════════════════
    //  INSPECTOR FIELDS
    // ════════════════════════════════════════════════════════════════════════

    [Header("Movement")]
    [Tooltip("Base walking speed in units per second.")]
    public float moveSpeed = 8.5f;

    [Tooltip("Multiplier applied when sprint key is held.")]
    public float sprintMultiplier = 2f;

    [Tooltip("Gravity acceleration (negative value). Stronger gravity = snappier, less floaty landings.")]
    public float gravity = -24f;

    public float maxJumpApexHeight = 1.75f;
    public float thrusterApexHeight = 2.15f;

    [Header("Movement Input Debug")]
    [Tooltip("Left stick input below this magnitude is ignored so controller drift cannot bias movement.")]
    [Range(0f, 1f)] public float gamepadMoveDeadzone = 0.2f;

    [Tooltip("Optional movement-input diagnostic for comparing WASD and arrow-key facing.")]
    public bool debugMovementInput = false;

    [Header("Animation")]
    [Tooltip("Animator controller for the spawned third-person body.")]
    public RuntimeAnimatorController playerAnimatorController;

    [Tooltip("Optional humanoid avatar override for the spawned third-person body.")]
    public Avatar playerAvatar;

    [Header("Movement Tuning")]
    [Tooltip("How fast the character reaches full speed (units/s²). High value removes the 'wading through mud' feel.")]
    public float acceleration = 26f;

    [Tooltip("How fast the character brakes to zero (units/s²). High value stops the player from sliding on key release.")]
    public float deceleration = 32f;

    [Tooltip("How quickly the character rotates to face movement direction (deg/sec).")]
    public float turnSpeed = 720f;

    [Tooltip("How strongly the camera yaw drifts toward the character's movement direction while moving (0 = no drift, 3 = snappy follow).")]
    [Range(0f, 6f)]
    public float cameraMovementFollowStrength = 1.8f;

    [Header("Tactical Maneuvers")]
    public float crouchHeight = 1.3f;
    public float proneHeight = 0.7f;
    public float stanceTransitionSpeed = 8f;
    public float crouchSpeedMultiplier = 0.72f;
    public float proneSpeedMultiplier = 0.45f;
    public float slideSpeedMultiplier = 1.6f;
    public float slideDuration = 0.45f;
    public float powerSlideBoost = 6.0f;
    public float powerSlideCooldown = 0.4f;
    public float mantleUpHeight = 1.1f;
    public float mantleForwardDistance = 1.0f;
    public float mantleDuration = 0.25f;
    public float thrusterApexHeightOverride = 0f;
    public float thrusterForwardBoost = 5.8f;
    public float thrusterCooldown = 0.8f;
    public float crouchBodyYOffset = -0.28f;
    // Crawl/prone uses a tiny lift (model rotated 90° so it lies flat) — the
    // big -0.62 offset used to bury the legs in the floor; we keep it here for
    // any legacy callers but the live code now uses proneCrawlBodyYOffset.
    public float proneBodyYOffset = -0.62f;

    [Header("Black Ops 3 Maneuvers")]
    [Tooltip("Legacy serialized flip impulse. The current Z dodge roll is grounded and does not launch upward.")]
    public float flipVerticalImpulse = 1.4f;
    [Tooltip("Forward boost applied at the start of the grounded dodge roll (Z key).")]
    public float flipForwardBoost = 5.5f;
    [Tooltip("How long the dodge roll body animation plays.")]
    public float flipDuration = 0.42f;
    [Tooltip("Cooldown between dodge rolls.")]
    public float flipCooldown = 0.9f;
    [Tooltip("Body Y offset while crawling — tiny because the body lies flat.")]
    public float proneCrawlBodyYOffset = -0.05f;

    [Header("Camera")]
    [Tooltip("First-person camera reference.")]
    public Camera firstPersonCam;

    [Tooltip("Third-person camera reference (auto-created if null).")]
    public Camera thirdPersonCam;

    [Tooltip("Mouse look sensitivity.")]
    public float sensitivity = 100f;

    [Header("Mouse Look Smoothing")]
    [Tooltip("Higher = snappier; lower = smoother. 18–28 is a good FPS range.")]
    public float lookSmoothing = 80f;

    [Tooltip("Optional cap on per-frame mouse delta to prevent spikes on low FPS or focus changes.")]
    public float maxMouseDeltaPerFrame = 60f;

    [Header("Combat")]
    [Tooltip("Maximum distance for melee attacks.")]
    public float attackDistance = 3f;

    [Tooltip("Delay before damage is applied after attack input.")]
    public float attackDelay = 0.05f;

    [Tooltip("Speed multiplier for attack animations.")]
    public float attackSpeed = 1f;

    [Tooltip("Base damage per hit.")]
    public int attackDamage = 25;

    [Tooltip("Physics layer mask for attack raycasts.")]
    public LayerMask attackLayer;

    [Tooltip("Radius for melee overlap detection.")]
    public float attackRadius = 1.25f;

    [Tooltip("Forward lunge distance on attack.")]
    public float attackLungeDistance = 0.45f;

    [Tooltip("Knockback force applied to hit targets.")]
    public float hitKnockbackForce = 4.5f;

    [Header("Weapon References")]
    [Tooltip("Current weapon display name (read by HUD).")]
    public string equippedWeaponName = "Combat Knife";

    [Tooltip("Optional hit-effect prefab spawned at impact point.")]
    public GameObject hitEffect;

    [Tooltip("Sound played on attack swing.")]
    public AudioClip swordSwing;

    [Tooltip("Sound played on successful hit.")]
    public AudioClip hitSound;

    [Header("Weapon Grip Fine Tuning (Player)")]
    [Tooltip("Level 13 sickle grip local position (player).")]
    public Vector3 level13SickleGripLocalPosition = DefaultLevel13SickleGripLocalPosition;
    [Tooltip("Level 13 sickle grip local euler (player).")]
    public Vector3 level13SickleGripLocalEuler = DefaultLevel13SickleGripLocalEuler;
    [Tooltip("Level 12 saw grip local position (player).")]
    public Vector3 level12SawGripLocalPosition = DefaultLevel12SawGripLocalPosition;
    [Tooltip("Level 12 saw grip local euler (player).")]
    public Vector3 level12SawGripLocalEuler = DefaultLevel12SawGripLocalEuler;

    [Header("Level 1 — Combat knife (FBX)")]
    [Tooltip("Drag your imported knife prefab/model here, or leave empty to load from Resources.")]
    public GameObject combatKnifePrefabOverride;

    [Tooltip("Resources path with no extension (file must live under a Resources folder).")]
    public string combatKnifeResourcePath = "Weapons/TacticalKnife/TacticalKnife";

    [Tooltip("Local position on the third-person right hand.")]
    public Vector3 combatKnifeThirdPersonLocalPos = Vector3.zero;

    [Tooltip("Local rotation on the third-person right hand (degrees).")]
    public Vector3 combatKnifeThirdPersonLocalEuler = Vector3.zero;

    [Tooltip("Local scale on the third-person weapon socket.")]
    public Vector3 combatKnifeThirdPersonLocalScale = new Vector3(0.02f, 0.02f, 0.02f);

    [Tooltip("Local position on the first-person Weapon socket.")]
    public Vector3 combatKnifeFirstPersonLocalPos;

    [Tooltip("Local rotation on the first-person Weapon socket (degrees).")]
    public Vector3 combatKnifeFirstPersonLocalEuler;

    [Tooltip("Local scale for the knife in first person.")]
    public Vector3 combatKnifeFirstPersonLocalScale = Vector3.one;

    [Header("Arena Boundaries")]
    public float arenaBoundaryRadius = 22.8f;
    public float arenaBoundaryPadding = 0.35f;
    public float arenaFloorHeight = 0.1f;
    public float floorSnapSpeed = 14f;
    public float maxFloorSnapDistance = 0.45f;
    [Tooltip("Raises the third-person body mesh so boot soles align with the capsule floor contact.")]
    public float standingVisualFootLift = 0.045f;

    [Header("Ground / Terrain")]
    [Tooltip("Downward velocity applied while grounded so the capsule hugs slopes and stairs.")]
    public float groundedStickVelocity = 5f;
    [Tooltip("Walkable surface normal minimum (auto-clamped to CharacterController.slopeLimit).")]
    [Range(0.45f, 0.95f)]
    public float minWalkableSurfaceNormalY = 0.58f;

    [Header("Wall / Static collision (movement)")]
    [Tooltip("Walls, props, containers — used to pre-clamp horizontal motion before CharacterController.Move. Exclude Character/Hittable/Player.")]
    public LayerMask staticObstacleMask;

    [Tooltip("Extra clearance beyond skinWidth so we do not ram the capsule into mesh every frame (reduces jitter).")]
    public float wallCollisionPadding = 0.045f;

    [Tooltip("Minimum gap to leave when a CapsuleCast hits static geometry.")]
    public float minMoveClearance = 0.025f;

    [Tooltip("Max separation push per frame (meters) vs other characters — prevents oscillation with walls.")]
    public float maxSeparationPushPerFrame = 0.08f;

    [Header("Jump")]
    public float jumpGroundProbeExtra = 0.22f;
    public float coyoteTime = 0.16f;
    public float jumpBufferTime = 0.14f;
    public float ascentGravityMultiplier = 0.78f;
    public float descentGravityMultiplier = 1.62f;
    public float jumpCutGravityMultiplier = 1.15f;
    public float jumpCutReleaseSpeed = 2.35f;
    public float maxFallSpeed = 48f;

    [Header("Safe Spawn")]
    [Tooltip("Extra clearance around the player capsule when validating spawn points against walls/buildings/obstacles.")]
    public float safeSpawnObstacleClearance = 0.45f;
    [Tooltip("How far around the requested spawn point to search for a valid NavMesh position.")]
    public float safeSpawnSearchRadius = 18f;
    [Tooltip("Maximum candidate positions to try before falling back to the safest sampled point.")]
    public int safeSpawnMaxAttempts = 72;
    [Tooltip("How far a candidate may be adjusted to the nearest NavMesh surface.")]
    public float safeSpawnNavMeshSampleRadius = 4f;

    // ════════════════════════════════════════════════════════════════════════
    //  PRIVATE STATE
    // ════════════════════════════════════════════════════════════════════════

    // Components
    private CharacterController controller;
    private Animator animator;
    private AudioSource audioSource;

    // Movement
    private Vector3 verticalVelocity;
    private Vector3 horizontalVelocity;
    private Vector3 actualHorizontalVelocity;
    private Vector2 moveInputRaw;
    private Vector2 moveInputSmoothed;
    private Vector2 lookInput;
    private bool isGrounded;
    private bool isSprinting;
    private bool wasMoving;
    private float coyoteTimeCounter;
    private float jumpBufferCounter;
    private float activeJumpApexHeight;

    // ── F9 movement debug overlay ─────────────────────────────────────────────
    private bool    _overlayEnabled;
    private Vector2 _dbgKeyboard;
    private Vector2 _dbgGamepad;
    private float   _dbgGamepadRawMag;
    private string  _dbgBlocked = "";
    private bool isCrouching;
    private bool isProne;
    private bool isSliding;
    private bool isMantling;
    private bool isFlipping;
    private bool sprintToggled; // Legacy sprint latch kept for any existing callers.
    private float slideTimer;
    private float slideCooldownTimer;
    private float thrusterCooldownTimer;
    private float flipCooldownTimer;
    private float jumpOverCooldownTimer;
    private PlayerTacticalActions _tacticalActions;
    private float targetControllerHeight = 1.8f;
    private Vector3 targetControllerCenter = new Vector3(0f, 0.92f, 0f);
    // Standing capsule bottom in local space — held constant during slide/prone
    // so resizing the CharacterController never buries the player (enemy-style pin).
    private float _standingHeight = 1.8f;
    private float _standingCenterY = 0.92f;
    private float _capsuleBottomY;
    private bool _wasInLowProfileStance;
    private Coroutine mantleCoroutine;
    private Coroutine flipCoroutine;
    private Vector3 thirdPersonBodyBaseLocalPosition;
    // Cached so prone-rotation and flip-rotation can return the body to its
    // original facing exactly without drifting after each maneuver.
    private Quaternion thirdPersonBodyBaseLocalRotation = Quaternion.identity;
    private const float InputSmoothing = 10f;

    // Camera
    private float cameraPitch;
    private float cameraYaw;
    private Vector2 lookInputSmoothed;
    private Vector3 firstPersonLocalPos;
    private Quaternion firstPersonLocalRot;
    private Camera runtimeThirdPersonCamera;
    private bool isThirdPersonActive;
    private bool _bodyFeetAligned;          // true once feet have been snapped to ground
    private int _postSpawnGroundSnapFrames; // extra snaps after load/restart
    private Animator _bodyAnimatorCached;   // cached animator from thirdPersonBody
    private GameObject _lastThirdPersonBodyRef; // tracks when to invalidate the cache
    private float nextMovementInputDebugLogTime;

    // Camera kick (recoil feedback)
    private float cameraKickTarget;
    private float cameraKickCurrent;

    // Combat
    private bool isAttacking;
    private float attackCooldownTimer;
    private float attackResetTimer;
    private float attackFailsafeTimer;
    private const float AttackCooldown = 0.15f;
    private const float AttackResetTime = 0.7f;
    private const float AttackFailsafeDuration = 1.0f;
    private const int MaxMeleeOverlapHits = 32;
    private LayerMask resolvedAttackMask;
    private readonly Collider[] meleeOverlapHits = new Collider[MaxMeleeOverlapHits];
    private readonly RaycastHit[] meleeRaycastHits = new RaycastHit[MaxMeleeOverlapHits];

    // Third-person body
    private GameObject thirdPersonBody;
    private Renderer[] firstPersonRenderers;
    [HideInInspector] public GameObject equippedWeaponObject;
    private int equippedWeaponLevel = -1;
    private bool weaponAttachInProgress;
    private float      _combatReadyBlend;
    private Quaternion _weaponAttachLocalRot;
    private GameObject _weaponAttachCachedObj;
    private WeaponHitbox equippedWeaponHitbox;
    private WeaponEquipper playerWeaponEquipper;
    private Coroutine weaponHitboxRoutine;
    private bool gripTuningMode;
#if PHOTON_UNITY_NETWORKING
    private PhotonView photonView;
#endif

    private Transform firstPersonWeaponSlot;
    private MeshRenderer firstPersonWeaponMeshRenderer;
    private GameObject firstPersonKnifeInstance;

    // ════════════════════════════════════════════════════════════════════════
    //  ANIMATOR
    // ════════════════════════════════════════════════════════════════════════

    private static readonly int HashSpeed     = Animator.StringToHash("Speed");
    private static readonly int HashAttack    = Animator.StringToHash("Attack");
    private static readonly int HashDead      = Animator.StringToHash("Dead");
    private static readonly int HashGrounded  = Animator.StringToHash("IsGrounded");
    private static readonly int HashAttacking = Animator.StringToHash("IsAttacking");
    private static readonly int HashSprinting = Animator.StringToHash("IsSprinting");

    private HashSet<int> _animParameterHashes;
    private int _cachedGroundMask;
    private bool _groundMaskReady;
    private Camera _cachedGameplayCamera;
    private static readonly RaycastHit[] _groundProbeHits = new RaycastHit[12];
    private float _probedFloorY;
    private Vector3 _probedGroundNormal = Vector3.up;
    private bool _probedFloorValid;
    private int _probedFloorFrame = -1;

    // ════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    public Camera ActiveCamera => runtimeThirdPersonCamera;
    public float PlanarSpeed => actualHorizontalVelocity.magnitude;
    public GameObject GetThirdPersonBody() => thirdPersonBody;
    public bool IsMeleeWeapon => true;
    public int GetEquippedWeaponLevel() => equippedWeaponLevel > 0 ? equippedWeaponLevel : (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1);

    // ── ThirdPersonOrbitCamera bridge ────────────────────────────────────────
    // Called every LateUpdate by ThirdPersonOrbitCamera to keep the player's
    // cameraYaw / cameraPitch in sync so movement and first-person camera kick
    // both remain camera-relative without any other code changes.
    public void SetOrbitYaw(float yaw)    { cameraYaw   = yaw; }
    public void SetOrbitPitch(float pitch){ cameraPitch = pitch; }

    private int GetGroundCheckMask()
    {
        if (!_groundMaskReady)
        {
            _cachedGroundMask = BuildGroundCheckMask();
            _groundMaskReady = true;
        }

        return _cachedGroundMask;
    }

    private void RefreshCachedGameplayCamera()
    {
        _cachedGameplayCamera = ActiveCamera != null ? ActiveCamera : GetComponentInChildren<Camera>(true);
    }

    private Camera GetGameplayCamera()
    {
        if (_cachedGameplayCamera == null)
            RefreshCachedGameplayCamera();
        return _cachedGameplayCamera;
    }

    private float MovementDeltaTime()
    {
        return Time.inFixedTimeStep ? Time.fixedDeltaTime : Time.deltaTime;
    }

    private float GetMinWalkableSurfaceNormalY()
    {
        float fromSlope = controller != null
            ? Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad)
            : 0.64f;
        return Mathf.Clamp(minWalkableSurfaceNormalY, 0.45f, Mathf.Max(0.45f, fromSlope - 0.02f));
    }

    private bool TryGetWalkableFloorY(out float floorY)
    {
        if (TryProbeWalkableGround(out RaycastHit hit))
        {
            floorY = hit.point.y;
            return true;
        }

        floorY = 0f;
        return false;
    }

    private bool TryProbeWalkableGround(out RaycastHit bestHit)
    {
        bestHit = default;
        int frame = Time.frameCount;
        if (_probedFloorFrame == frame && _probedFloorValid)
        {
            bestHit.point = new Vector3(transform.position.x, _probedFloorY, transform.position.z);
            bestHit.normal = _probedGroundNormal;
            return true;
        }

        _probedFloorFrame = frame;
        _probedFloorValid = false;
        if (controller == null)
            return false;

        GetCapsuleWorldEndpoints(transform.position, out Vector3 bottom, out Vector3 _);
        float capsuleBottomY = bottom.y;
        float radius = Mathf.Max(0.06f, controller.radius * 0.72f);
        Vector3 origin = bottom + Vector3.up * (radius + 0.03f);
        float maxDist = radius + Mathf.Max(controller.stepOffset, controller.skinWidth)
                        + jumpGroundProbeExtra + 0.12f;
        int mask = GetGroundCheckMask();
        float minNormalY = GetMinWalkableSurfaceNormalY();
        // Allow probing ground above the capsule so buried spawns (restart / NavMesh mismatch) can recover.
        float maxSurfaceY = capsuleBottomY + Mathf.Max(3.5f, controller.stepOffset + 0.35f) + 0.1f;
        float minSurfaceY = capsuleBottomY - Mathf.Max(0.12f, maxFloorSnapDistance);

        int count = Physics.SphereCastNonAlloc(
            origin,
            radius,
            Vector3.down,
            _groundProbeHits,
            maxDist,
            mask,
            QueryTriggerInteraction.Ignore);

        float bestDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            ref RaycastHit h = ref _groundProbeHits[i];
            if (h.collider == null || h.collider.transform.IsChildOf(transform))
                continue;
            if (h.normal.y < minNormalY)
                continue;
            if (h.point.y > maxSurfaceY || h.point.y < minSurfaceY)
                continue;

            if (!found || h.distance < bestDist)
            {
                bestDist = h.distance;
                bestHit = h;
                found = true;
            }
        }

        if (!found
            && Physics.Raycast(origin, Vector3.down, out RaycastHit rayHit, maxDist, mask, QueryTriggerInteraction.Ignore)
            && rayHit.collider != null
            && !rayHit.collider.transform.IsChildOf(transform)
            && rayHit.normal.y >= minNormalY
            && rayHit.point.y <= maxSurfaceY
            && rayHit.point.y >= minSurfaceY)
        {
            bestHit = rayHit;
            found = true;
        }

        if (!found)
            return false;

        _probedFloorY = bestHit.point.y;
        _probedGroundNormal = bestHit.normal.sqrMagnitude > 0.0001f
            ? bestHit.normal.normalized
            : Vector3.up;
        _probedFloorValid = true;
        return true;
    }

    private void ResolveGroundContact()
    {
        if (controller == null || isMantling)
            return;
        if (verticalVelocity.y > 0.12f)
            return;

        bool lowProfile = isProne || isSliding
            || (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive);
        bool hasFloor = TryProbeWalkableGround(out RaycastHit floorHit);
        if (!hasFloor)
            return;

        float floorY = floorHit.point.y;
        float capsuleBottomY = transform.position.y + controller.center.y - controller.height * 0.5f;
        float targetBottomY = floorY + controller.skinWidth;
        float gap = capsuleBottomY - targetBottomY;
        float dt = MovementDeltaTime();

        if (gap < -0.001f)
        {
            float lift = -gap;
            controller.enabled = false;
            transform.position += Vector3.up * lift;
            controller.enabled = true;
            if (verticalVelocity.y < 0f)
                verticalVelocity.y = -GetGroundedStickVelocity();
            return;
        }

        if (!controller.isGrounded && !lowProfile && !IsGroundedForJump())
            return;

        if (gap <= 0.001f || gap > maxFloorSnapDistance)
            return;

        // Standing still: only correct upward (sinking). Downward micro-snaps fight
        // the capsule stick force and read as idle vibration.
        if (IsStandingStill())
            return;

        float moveDown = Mathf.Min(gap, floorSnapSpeed * dt);
        if (moveDown > 0.001f)
            controller.Move(Vector3.down * moveDown);

        if (verticalVelocity.y < 0f)
            verticalVelocity.y = -GetGroundedStickVelocity();
    }

    private float GetGroundedStickVelocity()
    {
        return Mathf.Max(2f, groundedStickVelocity);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ════════════════════════════════════════════════════════════════════════

    private void Awake()
    {
#if PHOTON_UNITY_NETWORKING
        photonView = GetComponent<PhotonView>();
#endif
        // ── Near clip — set before any camera becomes active so the weapon
        // viewmodel never disappears on the very first frame.
        if (Camera.main != null)
            Camera.main.nearClipPlane = 0.1f;
        foreach (Camera cam in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (cam != null && cam.nearClipPlane < 0.1f)
                cam.nearClipPlane = 0.1f;

        // Force the project-wide layer collision matrix to allow Player ↔
        // Enemies overlap so the manual separation push below sees them. The
        // helper handles missing layers gracefully (layers are project-level
        // settings and cannot be created at runtime).
        EnemyController.EnsureCharacterLayerCollision();

        // Prefer dedicated Player layer for enemy melee masks; fall back for older scenes.
        int targetLayer = LayerMask.NameToLayer("Player");
        if (targetLayer < 0) targetLayer = LayerMask.NameToLayer("Hittable");
        if (targetLayer < 0) targetLayer = LayerMask.NameToLayer("Character");
        if (targetLayer >= 0) SetLayerRecursive(gameObject, targetLayer);

        controller  = GetComponent<CharacterController>();
        audioSource = GetComponent<AudioSource>();
        if (GetComponent<PlayerInteractor>() == null)
            gameObject.AddComponent<PlayerInteractor>();

        if (controller != null)
        {
            controller.height          = 1.8f;
            controller.radius          = 0.4f;
            controller.center          = new Vector3(0f, 0.92f, 0f);
            controller.skinWidth       = 0.04f;
            controller.stepOffset      = 0.55f;
            controller.slopeLimit      = 50f;
            controller.minMoveDistance  = 0f;
            targetControllerHeight = controller.height;
            targetControllerCenter = controller.center;
            _standingHeight  = controller.height;
            _standingCenterY = controller.center.y;
            _capsuleBottomY  = _standingCenterY - _standingHeight * 0.5f;
        }

        RobustThirdPersonMovement legacyMovement = GetComponent<RobustThirdPersonMovement>();
        if (legacyMovement != null)
        {
            legacyMovement.enabled = false;
            Debug.Log("[PlayerController] Disabled RobustThirdPersonMovement — PlayerController owns locomotion.", this);
        }

        _tacticalActions = GetComponent<PlayerTacticalActions>();
        if (_tacticalActions == null)
            _tacticalActions = gameObject.AddComponent<PlayerTacticalActions>();
        _tacticalActions.CacheStandingCollider();

        // Cache first-person camera transform for restoring when toggling.
        if (firstPersonCam == null)
            firstPersonCam = GetComponentInChildren<Camera>();
        if (firstPersonCam != null)
        {
            firstPersonLocalPos = firstPersonCam.transform.localPosition;
            firstPersonLocalRot = firstPersonCam.transform.localRotation;
            firstPersonCam.gameObject.SetActive(false);
        }

        CacheFirstPersonWeaponSlot();

        firstPersonRenderers = GetComponentsInChildren<Renderer>(true);
        resolvedAttackMask   = ResolveHittableMask();
        if (staticObstacleMask.value == 0)
            staticObstacleMask = BuildDefaultStaticObstacleMask();

        _cachedGroundMask = BuildGroundCheckMask();
        _groundMaskReady = true;
        RefreshCachedGameplayCamera();

        foreach (Animator childAnimator in GetComponentsInChildren<Animator>(true))
            childAnimator.applyRootMotion = false;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
        LoadSavedGripOverrides();

        // Cache KatanaCombatHandler — when present it owns attack input for
        // the katana level so HandleActionInput must not call Attack() too.
        _katanaCombatHandler = GetComponent<KatanaCombatHandler>();

        // Refresh the runtime mouse sensitivity multiplier from PlayerPrefs
        // each time a player spawns. Guarantees the Options-menu slider value
        // applies to the very first frame of mouse-look in GameScene.
        LookSensitivityRuntime.LoadFromPrefs();

        // Any Rigidbody on the player root (legacy prefabs / imported rigs) must
        // not tip the capsule — CharacterController owns translation.
        Rigidbody rootBody = GetComponent<Rigidbody>();
        if (rootBody != null)
        {
            rootBody.isKinematic = true;
            rootBody.useGravity = false;
            rootBody.interpolation = RigidbodyInterpolation.Interpolate;
            rootBody.constraints = RigidbodyConstraints.FreezeRotationX
                                  | RigidbodyConstraints.FreezeRotationZ;
        }
    }

    private void Start()
    {
        _playerHealth = GetComponent<PlayerHealth>() ?? gameObject.AddComponent<PlayerHealth>();
        EnsureCriticalComponents();
        ResetLevelOneWeaponOffsets();

        // ── 1. Snap player above the floor ──
        if (controller != null) controller.enabled = false;
        Vector3 requestedStart = IsUnsafeSpawn(transform.position)
            ? SafeFallbackSpawn
            : transform.position;
        Vector3 startPos = ResolveSafeSpawnPosition(requestedStart);
        if (controller != null) controller.enabled = false;
        transform.position = startPos;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        cameraYaw = transform.eulerAngles.y;
        cameraPitch = ThirdPersonInitialPitch;
        if (controller != null) controller.enabled = true;
        SnapCapsuleToWalkableGround();
        verticalVelocity.y = -GetGroundedStickVelocity();
        _postSpawnGroundSnapFrames = 4;

        // ── 2. Spawn the third-person body ──
        ApplyPerspectivePreference();
        EnsureThirdPersonBody();
        if (thirdPersonBody != null)
        {
            SetLayerRecursive(thirdPersonBody, gameObject.layer);
            LockAvatarRigidbodies(thirdPersonBody);
        }

        DisableRemotePlayerLocalSystems();

#if PUN_2_OR_NEWER
        if (!IsRemoteNetworkPlayer())
        {
            Debug.Log($"[MPDebug] local player spawned ({gameObject.name})");
            if (runtimeThirdPersonCamera != null && runtimeThirdPersonCamera.enabled)
                Debug.Log("[MPDebug] local camera enabled");
            else
                Debug.LogWarning("[MPDebug] local camera NOT enabled after spawn — check EnsureThirdPersonCamera");
        }
#endif

        // ── 3. Acquire the body animator and bind the intended player controller ──
        animator = null;
        if (thirdPersonBody != null)
            animator = thirdPersonBody.GetComponentInChildren<Animator>(true);

        // Fallback: any non-Arms animator on the player hierarchy
        if (animator == null)
        {
            foreach (Animator a in GetComponentsInChildren<Animator>(true))
            {
                if (a.gameObject.name.IndexOf("Arms", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                animator = a;
                break;
            }
        }

        ConfigureAnimatorBinding(animator, forceControllerAssignment: false);

        foreach (Animator childAnimator in GetComponentsInChildren<Animator>(true))
            ConfigureAnimatorBinding(childAnimator, forceControllerAssignment: false);

        // Do NOT call Rebind() here — it resets the state machine before the
        // controller has finished initialising, which can freeze it permanently.
        // The Entry → default-state transition fires automatically on the first Update.
        CacheAnimatorParameters();

        // ── 4. Third-person body colour (weapon subtree excluded in ApplyPlayerBodyBlackTint) ──
        if (thirdPersonBody != null)
            EnsureProperMaterial(thirdPersonBody);
        AssignMaterial();
        ApplyPlayerBodyBlackTint();

        // In multiplayer the canonical level comes from the room's custom
        // properties (defaulting to Level 1 / Tactical Knife). Using
        // GameManager.currentLevel here used to spawn each client with the
        // host's single-player Continue level (often L8 Hammer).
        int level = MultiplayerMode.IsMultiplayer
            ? MpRoomConfig.ReadLevel()
            : (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1);
        EquipWeaponForLevel(level);
    }

    private void EnsureCriticalComponents()
    {
        if (controller == null)
            controller = GetComponent<CharacterController>();
        if (controller == null)
        {
            controller = gameObject.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = new Vector3(0f, 0.92f, 0f);
        }

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (firstPersonCam == null)
            firstPersonCam = GetComponentInChildren<Camera>(true);

        // Remote network players have their camera/audio disabled by
        // DisableRemotePlayerLocalSystems() — do not create a camera for them.
        if (!IsRemoteNetworkPlayer())
        {
            EnsureThirdPersonCamera();
            if (runtimeThirdPersonCamera == null)
                Debug.LogError("[PlayerController] No gameplay camera could be created. Movement is still enabled.");
            EnsureLocalLowProfileTools();
        }
    }

    private void EnsureLocalLowProfileTools()
    {
        if (IsRemoteNetworkPlayer())
            return;

        if (GetComponent<StanceTestController>() == null)
            gameObject.AddComponent<StanceTestController>();

        LowProfileGroundDebugger dbg = GetComponent<LowProfileGroundDebugger>();
        if (dbg == null)
            dbg = gameObject.AddComponent<LowProfileGroundDebugger>();
        dbg.enabled = true;
    }

    private void ResetLevelOneWeaponOffsets()
    {
        int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        if (level != 1)
            return;

        equippedWeaponName = "Tactical Knife";
        combatKnifeThirdPersonLocalPos = Vector3.zero;
        combatKnifeThirdPersonLocalEuler = Vector3.zero;
        combatKnifeThirdPersonLocalScale = Vector3.one;
        combatKnifeFirstPersonLocalPos = Vector3.zero;
        combatKnifeFirstPersonLocalEuler = Vector3.zero;
        combatKnifeFirstPersonLocalScale = Vector3.one;
    }

    private static bool IsUnsafeSpawn(Vector3 position)
    {
        return float.IsNaN(position.x)
            || float.IsNaN(position.y)
            || float.IsNaN(position.z)
            || position.y < -0.5f;
    }

    private Vector3 ResolveSafeSpawnPosition(Vector3 requestedPosition)
    {
        if (controller == null)
            return requestedPosition;

        if (staticObstacleMask.value == 0)
            staticObstacleMask = BuildDefaultStaticObstacleMask();

        float arenaHalf = LevelBuilder.Instance != null ? LevelBuilder.Instance.arenaHalfSize : 80f;
        bool enclosedArena = LevelBuilder.Instance != null && LevelBuilder.Instance.useSciFiArena;

        // Enclosed sci-fi arena: floors aren't named "road/street", so the street-only
        // validator always fails. Prefer the prefab's PlayerSpawn marker, then accept
        // any NavMesh-sampled point with capsule clearance.
        if (enclosedArena)
        {
            if (TryFindSciFiArenaPlayerSpawn(requestedPosition, out Vector3 arenaRoot))
                return arenaRoot;
        }
        else if (EnemySpawnGeometry.TryFindStreetPlayerSpawn(arenaHalf, requestedPosition, out Vector3 streetFeet)
            && TryBuildSafeSpawnCandidate(streetFeet, out Vector3 streetRoot))
        {
            return streetRoot;
        }

        int attempts = Mathf.Max(8, safeSpawnMaxAttempts);
        float maxRadius = Mathf.Max(1f, safeSpawnSearchRadius);

        if (TryBuildSafeSpawnCandidate(requestedPosition, out Vector3 safePosition))
            return safePosition;

        float goldenAngle = 137.50776f * Mathf.Deg2Rad;
        for (int i = 1; i <= attempts; i++)
        {
            float t = i / (float)attempts;
            float radius = Mathf.Lerp(0.75f, maxRadius, Mathf.Sqrt(t));
            float angle = i * goldenAngle;
            Vector3 offset = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            if (TryBuildSafeSpawnCandidate(requestedPosition + offset, out safePosition))
                return safePosition;
        }

        // Enclosed arena: NavMesh sample + capsule clearance is enough — no street-name check.
        if (enclosedArena)
        {
            for (int i = 0; i <= attempts; i++)
            {
                float t = i / Mathf.Max(1f, (float)attempts);
                float radius = Mathf.Lerp(0.75f, maxRadius, Mathf.Sqrt(t));
                float angle = i * goldenAngle;
                Vector3 offset = i == 0 ? Vector3.zero
                    : new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (TryBuildEnclosedSpawnCandidate(requestedPosition + offset, out Vector3 enclosed))
                    return enclosed;
            }
        }

        if (TryBuildSafeSpawnCandidate(SafeFallbackSpawn, out safePosition))
            return safePosition;
        if (enclosedArena && TryBuildEnclosedSpawnCandidate(SafeFallbackSpawn, out Vector3 enclosedFallback))
            return enclosedFallback;

        // SciFi (enclosed) arena: never emit the outdoor warning — it's misleading for
        // indoor maps. Silently use the safe fallback; LevelBuilder will have logged
        // its own SciFi-specific diagnostic upstream.
        if (enclosedArena)
            return SafeFallbackSpawn;

        Debug.LogWarning("[PlayerController] Could not find an outdoor spawn; using fallback.");
        return SafeFallbackSpawn;
    }

    private bool TryFindSciFiArenaPlayerSpawn(Vector3 requestedPosition, out Vector3 rootPosition)
    {
        rootPosition = requestedPosition;

        // LevelBuilder renames the instantiated SciFi arena to "FbxMap".
        GameObject arena = GameObject.Find("FbxMap")
                        ?? GameObject.Find("SciFiArena")
                        ?? GameObject.Find("SciFiArena(Clone)");
        if (arena == null) return false;

        Transform marker = arena.transform.Find("SpawnPoints/PlayerSpawn");
        if (marker == null)
        {
            Transform[] all = arena.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == "PlayerSpawn") { marker = all[i]; break; }
            }
        }
        if (marker == null) return false;

        if (TryBuildEnclosedSpawnCandidate(marker.position, out rootPosition))
            return true;

        // NavMesh may not be ready — root-position the marker directly so we never warn.
        rootPosition = RootPositionFromGroundPoint(marker.position);
        return true;
    }

    private bool TryBuildEnclosedSpawnCandidate(Vector3 requestedPosition, out Vector3 rootPosition)
    {
        rootPosition = requestedPosition;
        if (controller == null) return false;

        float sampleRadius = Mathf.Max(0.5f, safeSpawnNavMeshSampleRadius);
        if (!NavMesh.SamplePosition(requestedPosition, out NavMeshHit navHit, sampleRadius, NavMesh.AllAreas))
            return false;

        rootPosition = RootPositionFromGroundPoint(navHit.position);
        if (TryFindPhysicsGround(rootPosition + Vector3.up * 2f, 10f, out RaycastHit physHit))
            rootPosition = RootPositionFromGroundPoint(physHit.point);

        return IsSpawnCapsuleClear(rootPosition);
    }

    private bool TryBuildSafeSpawnCandidate(Vector3 requestedPosition, out Vector3 rootPosition)
    {
        rootPosition = requestedPosition;
        if (controller == null)
            return false;

        float sampleRadius = Mathf.Max(0.5f, safeSpawnNavMeshSampleRadius);
        if (!NavMesh.SamplePosition(requestedPosition, out NavMeshHit navHit, sampleRadius, NavMesh.AllAreas))
            return false;

        Vector3 feetCheck = navHit.position + Vector3.up * 0.08f;
        if (!EnemySpawnGeometry.IsValidPlayerStreetSpawn(feetCheck))
            return false;

        rootPosition = RootPositionFromGroundPoint(navHit.position);
        if (TryFindPhysicsGround(rootPosition + Vector3.up * 2f, 10f, out RaycastHit physHit))
            rootPosition = RootPositionFromGroundPoint(physHit.point);

        return IsSpawnCapsuleClear(rootPosition);
    }

    private bool TryFindPhysicsGround(Vector3 rayOrigin, float maxDistance, out RaycastHit hit)
    {
        hit = default;
        int mask = GetGroundCheckMask();
        if (Physics.Raycast(rayOrigin, Vector3.down, out hit, maxDistance, mask, QueryTriggerInteraction.Ignore)
            && hit.collider != null
            && !hit.collider.transform.IsChildOf(transform)
            && hit.normal.y >= GetMinWalkableSurfaceNormalY())
            return true;

        return false;
    }

    /// <summary>
    /// Aligns the capsule feet to physics ground (not NavMesh alone). Safe after restart / TeleportTo.
    /// </summary>
    public bool SnapCapsuleToWalkableGround()
    {
        if (controller == null)
            controller = GetComponent<CharacterController>();
        if (controller == null)
            return false;

        Vector3 rayOrigin = transform.position + Vector3.up * 3f;
        if (!TryFindPhysicsGround(rayOrigin, 12f, out RaycastHit hit))
        {
            rayOrigin = transform.position + Vector3.up * 8f;
            if (!TryFindPhysicsGround(rayOrigin, 20f, out hit))
                return false;
        }

        Vector3 rooted = RootPositionFromGroundPoint(hit.point);
        if (Mathf.Abs(rooted.y - transform.position.y) < 0.01f)
            return true;

        bool wasEnabled = controller.enabled;
        controller.enabled = false;
        transform.position = new Vector3(transform.position.x, rooted.y, transform.position.z);
        controller.enabled = wasEnabled;
        verticalVelocity.y = -GetGroundedStickVelocity();
        Physics.SyncTransforms();
        _probedFloorValid = false;
        return true;
    }

    private Vector3 RootPositionFromGroundPoint(Vector3 groundPoint)
    {
        if (controller == null)
            return groundPoint;

        float rootY = groundPoint.y - controller.center.y + controller.height * 0.5f + controller.skinWidth + 0.03f;
        return new Vector3(groundPoint.x, rootY, groundPoint.z);
    }

    private bool IsSpawnCapsuleClear(Vector3 rootPosition)
    {
        if (controller == null)
            return true;

        GetCapsuleWorldEndpoints(rootPosition, out Vector3 bottom, out Vector3 top);
        int mask = staticObstacleMask.value != 0 ? staticObstacleMask.value : BuildDefaultStaticObstacleMask().value;
        if (mask == 0)
            return true;

        float bodyRadius = Mathf.Max(0.05f, controller.radius * 0.9f);
        Vector3 inset = Vector3.up * 0.08f;
        if (Physics.CheckCapsule(bottom + inset, top - inset, bodyRadius, mask, QueryTriggerInteraction.Ignore))
            return false;

        float clearanceRadius = controller.radius + Mathf.Max(0f, safeSpawnObstacleClearance);
        Vector3 clearanceCenter = rootPosition + controller.center + Vector3.up * 0.15f;
        return !Physics.CheckSphere(clearanceCenter, clearanceRadius, mask, QueryTriggerInteraction.Ignore);
    }

    private void Update()
    {
        if (controller == null)
            return;

        // ── ONE-TIME feet alignment (runs every frame until successful) ────────
        if (!_bodyFeetAligned && thirdPersonBody != null)
            _bodyFeetAligned = TryAlignBodyFeet();

        if (IsRemoteNetworkPlayer())
        {
            DisableRemotePlayerLocalSystems();
            UpdateAnimatorParameters();
            return;
        }
        if (IsUnsafeSpawn(transform.position))
            TeleportTo(SafeFallbackSpawn);

        // Hard anti-flip clamp — unconditional. If anything tilted the root
        // (animator, ragdoll, external script) reset to yaw-only this frame.
        if (!EndMatchCinematic.GameplayLocked)
        {
            Vector3 e0 = transform.eulerAngles;
            transform.eulerAngles = new Vector3(0f, e0.y, 0f);

            // Also kill any root-motion on all body animators — belt-and-suspenders
            // so an asset import that ships with root motion can't cause flips.
            if (thirdPersonBody != null)
            {
                foreach (Animator a in thirdPersonBody.GetComponentsInChildren<Animator>(true))
                    if (a != null && a.applyRootMotion) a.applyRootMotion = false;
            }
        }

        isGrounded = controller.isGrounded;

        // End-match cinematic freezes player input — the Winners-Circle
        // sequence runs entirely on its own camera, and any input we sample
        // here would also fight the slow-motion timeScale.
        if (EndMatchCinematic.GameplayLocked)
        {
            UpdateAnimatorParameters();
            return;
        }

        _dbgBlocked = EndMatchCinematic.GameplayLocked ? "GameplayLocked"
                    : IsRemoteNetworkPlayer()          ? "RemotePlayer"
                    : Time.timeScale <= 0.0001f        ? "Paused(timeScale=0)"
                    : "";

        ReadInput();
        HandleActionInput();
        ApplyLook();
        // UpdateCameraZoom removed — zoom is permanently disabled.
        UpdateHeadBob();
        UpdateCameraKick();
        EnforcePerspectiveVisibility();
        UpdateCombatState();
        UpdateAnimatorParameters();
        HandleWeaponGripDebugTuning();

        // Cursor policy:
        // - During gameplay: locked + hidden.
        // - When timeScale is 0 (pause/menus): unlocked + visible (PauseMenuController handles this too).
        if (Time.timeScale <= 0.0001f)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
    }

    // F9 on-screen movement debug overlay — disabled by default (_overlayEnabled = false).
    private void OnGUI()
    {
        if (!_overlayEnabled || IsRemoteNetworkPlayer()) return;

        Vector3 moveDir = GetCameraRelativeMoveDirection(moveInputSmoothed);
        float   spd     = actualHorizontalVelocity.magnitude;

        string txt =
            "[F9] Movement Debug\n" +
            $"Keyboard raw:     ({_dbgKeyboard.x:F3}, {_dbgKeyboard.y:F3})\n" +
            $"Gamepad raw mag:  {_dbgGamepadRawMag:F3}  raw=({_dbgGamepad.x:F3},{_dbgGamepad.y:F3})\n" +
            $"Final raw input:  ({moveInputRaw.x:F3}, {moveInputRaw.y:F3})\n" +
            $"Smoothed input:   ({moveInputSmoothed.x:F3}, {moveInputSmoothed.y:F3})\n" +
            $"Move direction:   ({moveDir.x:F3}, {moveDir.z:F3})\n" +
            $"Speed (actual):   {spd:F2} m/s\n" +
            $"isGrounded:       {isGrounded}\n" +
            $"timeScale:        {Time.timeScale:F3}\n" +
            $"Blocked by:       {(_dbgBlocked.Length > 0 ? _dbgBlocked : "none")}";

        var bg  = new Rect(8, 8, 336, 208);
        var lbl = new Rect(14, 14, 324, 196);
        GUI.Box(bg, GUIContent.none);
        GUI.Label(lbl, txt);
    }

    private void LateUpdate()
    {
        // Winners Circle / end-match cinematic drives the root transform via
        // VictoryPoseDriver — do not fight it with yaw lock or ground snap.
        if (EndMatchCinematic.GameplayLocked)
            return;

        // Strictly lock ROOT rotation to yaw only
        transform.eulerAngles = new Vector3(0f, transform.eulerAngles.y, 0f);

        // Guard the body mesh: if an animator or physics bone drifted it off-vertical
        // outside of a deliberate flip/prone pose, snap it back to its cached base.
        if (!isFlipping && thirdPersonBody != null && !isProne)
        {
            if (IsStandingStill())
                thirdPersonBody.transform.localRotation = thirdPersonBodyBaseLocalRotation;
            else
                NormalizeThirdPersonBodyLocalRotation();
        }

        if (isProne || isSliding || (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive))
            ClampLowProfileVisualMesh();

        AlignStandingVisualToCapsule();
        if (IsStandingStill())
            StabilizeStandingVisualPose();
        else
            MaintainBodyFeetOnWalkableGround();

        if (_tacticalActions != null)
        {
            _tacticalActions.DisableRootMotionOnAnimators();
            if (isProne)
            {
                _tacticalActions.EnforceProneGround();
                AlignProneVisualToGround();
            }
            else if (isSliding || _tacticalActions.IsTacticalAnimActive)
                _tacticalActions.EnforceGroundContact();
        }
        else if (isProne || isSliding)
            FlushLowProfileGroundSnap(forceZeroMove: false);
    }

    private void AlignProneVisualToGround()
    {
        if (!isProne || thirdPersonBody == null || controller == null)
            return;

        if (!TryProbeWalkableGround(out RaycastHit groundHit))
            return;

        float footWorldY = thirdPersonBody.transform.position.y;
        Animator bodyAnim = thirdPersonBody.GetComponentInChildren<Animator>(true);
        if (bodyAnim != null && bodyAnim.isHuman)
        {
            Transform leftFoot = bodyAnim.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = bodyAnim.GetBoneTransform(HumanBodyBones.RightFoot);
            if (leftFoot != null)
                footWorldY = Mathf.Min(footWorldY, leftFoot.position.y);
            if (rightFoot != null)
                footWorldY = Mathf.Min(footWorldY, rightFoot.position.y);
        }

        float soleOffset = 0.06f;
        float deltaWorldY = groundHit.point.y + soleOffset - footWorldY;
        if (Mathf.Abs(deltaWorldY) < 0.001f || Mathf.Abs(deltaWorldY) > 0.28f)
            return;

        Vector3 local = thirdPersonBody.transform.localPosition;
        thirdPersonBody.transform.localPosition = local + new Vector3(0f, deltaWorldY, 0f);
    }

    private void NormalizeThirdPersonBodyLocalRotation()
    {
        if (thirdPersonBody == null)
            return;

        thirdPersonBody.transform.localRotation = Quaternion.Slerp(
            thirdPersonBody.transform.localRotation,
            thirdPersonBodyBaseLocalRotation,
            18f * Time.deltaTime);
    }

    // ── Anti-Sink + Anti-Flip enforcement (runs every physics tick) ────────
    //
    // FixedUpdate is the deterministic place to enforce two absolute rules
    // that must hold regardless of what any animator, coroutine, or external
    // script tries to do to the transform:
    //
    //   1. The capsule bottom is NEVER below the floor surface.
    //   2. The character is NEVER tilted around X (pitch) or Z (roll).
    //
    // These run in addition to the smoother per-frame logic in Update /
    // LateUpdate.  This is the hard backstop — zero tolerance, no smoothing.
    private void FixedUpdate()
    {
        if (controller == null) return;
        if (EndMatchCinematic.GameplayLocked) return;

        Vector3 e = transform.eulerAngles;
        transform.eulerAngles = new Vector3(0f, e.y, 0f);

        if (IsRemoteNetworkPlayer())
            return;

        _probedFloorValid = false;
        _probedFloorFrame = -1;
        isGrounded = controller.isGrounded;
        ApplyMovement();
        ApplyCharacterSeparationPush();
        ResolveGroundContact();

        if (_postSpawnGroundSnapFrames > 0)
        {
            SnapCapsuleToWalkableGround();
            _postSpawnGroundSnapFrames--;
        }
    }

    /// <summary>
    /// Keeps the visible mesh from sinking below the capsule floor while standing/crouch.
    /// </summary>
    private void AlignStandingVisualToCapsule()
    {
        if (thirdPersonBody == null || controller == null)
            return;

        if (isProne || isSliding || (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive))
            return;
        if (IsAscendingJump())
            return;

        float yOff = standingVisualFootLift;
        if (isCrouching)
            yOff = Mathf.Max(yOff, crouchBodyYOffset);

        Vector3 desired = thirdPersonBodyBaseLocalPosition + new Vector3(0f, yOff, 0f);
        Vector3 lp = thirdPersonBody.transform.localPosition;
        if (lp.y < desired.y)
            thirdPersonBody.transform.localPosition = new Vector3(lp.x, desired.y, lp.z);
    }

    /// <summary>
    /// Small per-frame foot correction on uneven floors so the visible mesh stays
    /// planted while the capsule follows stairs/slopes.
    /// </summary>
    private bool IsStandingStill()
    {
        return moveInputRaw.sqrMagnitude < 0.0001f
            && moveInputSmoothed.sqrMagnitude < 0.0004f
            && horizontalVelocity.sqrMagnitude < 0.0004f;
    }

    private void StabilizeStandingVisualPose()
    {
        if (thirdPersonBody == null || controller == null)
            return;
        if (isProne || isSliding || isMantling || IsAscendingJump())
            return;
        if (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive)
            return;

        float yOff = standingVisualFootLift;
        if (isCrouching)
            yOff = Mathf.Max(yOff, crouchBodyYOffset);

        Vector3 desired = thirdPersonBodyBaseLocalPosition + new Vector3(0f, yOff, 0f);
        Vector3 lp = thirdPersonBody.transform.localPosition;
        if ((lp - desired).sqrMagnitude < 1e-8f)
            return;

        float blend = 1f - Mathf.Exp(-24f * Time.deltaTime);
        thirdPersonBody.transform.localPosition = Vector3.Lerp(lp, desired, blend);
    }

    private void MaintainBodyFeetOnWalkableGround()
    {
        if (thirdPersonBody == null || controller == null)
            return;
        if (IsStandingStill())
            return;
        if (isProne || isSliding || isMantling || IsAscendingJump())
            return;
        if (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive)
            return;
        if (!IsGroundedForJump() || !TryProbeWalkableGround(out RaycastHit groundHit))
            return;

        Animator bodyAnim = thirdPersonBody.GetComponentInChildren<Animator>(true);
        if (bodyAnim == null || !bodyAnim.isInitialized)
            return;

        float feetWorldY = float.MaxValue;
        bool hasFeet = false;
        if (bodyAnim.isHuman)
        {
            Transform lf = bodyAnim.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rf = bodyAnim.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf != null) { feetWorldY = Mathf.Min(feetWorldY, lf.position.y); hasFeet = true; }
            if (rf != null) { feetWorldY = Mathf.Min(feetWorldY, rf.position.y); hasFeet = true; }
        }

        if (!hasFeet)
            return;

        const float ankleToSole = 0.08f;
        float groundY = groundHit.point.y;
        float gap = (feetWorldY - ankleToSole) - groundY;
        if (Mathf.Abs(gap) < 0.002f || Mathf.Abs(gap) > 0.12f)
            return;

        float yOff = standingVisualFootLift;
        if (isCrouching)
            yOff = Mathf.Max(yOff, crouchBodyYOffset);

        Vector3 basePos = thirdPersonBodyBaseLocalPosition + new Vector3(0f, yOff, 0f);
        Vector3 lp = thirdPersonBody.transform.localPosition;
        float targetLocalY = basePos.y - gap;
        float blend = Mathf.Clamp01(18f * Time.deltaTime);
        lp.y = Mathf.Lerp(lp.y, targetLocalY, blend);
        thirdPersonBody.transform.localPosition = new Vector3(lp.x, lp.y, lp.z);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  INPUT
    // ════════════════════════════════════════════════════════════════════════

    private void ReadInput()
    {
        // Deselect ALL UI while the cursor is locked so that Space / arrows /
        // Enter are never consumed by the EventSystem as "Submit" or navigation
        // inputs.  Without this, pressing Space jumps the UI (scrolls panels or
        // clicks buttons) instead of triggering the player's jump action.
        if (Cursor.lockState == CursorLockMode.Locked && EventSystem.current != null)
        {
            // Force-clear the selected object every frame — the EventSystem can
            // re-select UI between frames via pointer-enter events.
            if (EventSystem.current.currentSelectedGameObject != null)
                EventSystem.current.SetSelectedGameObject(null);

            // Also disable the EventSystem's keyboard-navigation processing
            // while in gameplay so it cannot intercept Space, Enter, arrows.
            EventSystem.current.sendNavigationEvents = false;
        }
        else if (EventSystem.current != null)
        {
            // Re-enable UI navigation when cursor is NOT locked (menus, etc.).
            EventSystem.current.sendNavigationEvents = true;
        }

        if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
            _overlayEnabled = !_overlayEnabled;

        moveInputRaw = ReadMovementInput();
        moveInputRaw = Vector2.ClampMagnitude(moveInputRaw, 1f);

        // Exponential smoothing is frame-rate independent (matches look input below)
        float moveDt = Mathf.Max(0.0001f, Time.deltaTime);
        float moveK = 1f - Mathf.Exp(-InputSmoothing * moveDt);
        moveInputSmoothed = Vector2.Lerp(moveInputSmoothed, moveInputRaw, moveK);

        // Exponential smoothing only ASYMPTOTES toward zero — it never reaches
        // it. Without this hard snap the tiny residual keeps feeding a non-zero
        // move vector after every key release, so the character glides on
        // forever ("movement continues after releasing keys"). Once raw input
        // is released and the residual is negligible, force a true full stop.
        if (moveInputRaw.sqrMagnitude < 0.0001f && moveInputSmoothed.sqrMagnitude < 0.0004f)
            moveInputSmoothed = Vector2.zero;

        // Read raw mouse delta (Input System gives pixels-per-frame-ish deltas).
        // We clamp it to avoid giant spikes (alt-tab, focus changes, or 10 FPS stutters).
        lookInput = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        lookInput = Vector2.ClampMagnitude(lookInput, Mathf.Max(1f, maxMouseDeltaPerFrame));

        // Smooth mouse input to remove jitter while keeping responsiveness.
        // Exponential smoothing is frame-rate independent.
        float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
        float k = 1f - Mathf.Exp(-lookSmoothing * dt);
        lookInputSmoothed = Vector2.Lerp(lookInputSmoothed, lookInput, k);
    }

    // Losing window focus (alt-tab in an EXE build) can leave the last key
    // state cached on the input device, and the cached velocity keeps the
    // character running when focus returns. Hard-reset movement state on focus
    // loss / pause so input can never "stick" across a focus change.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            ResetMovementInputState();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
            ResetMovementInputState();
    }

    private void ResetMovementInputState()
    {
        moveInputRaw = Vector2.zero;
        moveInputSmoothed = Vector2.zero;
        horizontalVelocity = Vector3.zero;
        actualHorizontalVelocity = Vector3.zero;
        jumpBufferCounter = 0f;
        coyoteTimeCounter = 0f;
    }

    public void ClearMovementForMatchLock()
    {
        ResetMovementInputState();
        verticalVelocity = Vector3.zero;
        isSprinting = false;
        sprintToggled = false;
    }

    private void HandleActionInput()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
                HandleJumpPress();

            // Tactical layout (matches enemy / RobustThirdPersonMovement): Z = JumpOver, X = Slide, C = Prone.
            if (Keyboard.current.zKey.wasPressedThisFrame)
                TryStartJumpOver();

            if (Keyboard.current.xKey.wasPressedThisFrame)
                TryStartSlide();

            if (Keyboard.current.cKey.wasPressedThisFrame)
                ToggleCrawl();

            if (Keyboard.current.vKey.wasPressedThisFrame)
                TriggerTacticalAbilityPlaceholder();

            // Optional auxiliaries kept on Left Control / Left Alt so existing
            // muscle memory still resolves to the right action.
            if (Keyboard.current.leftCtrlKey.wasPressedThisFrame)
                ToggleCrouch();

            if (Keyboard.current.leftAltKey.wasPressedThisFrame)
                TryMantle();
        }

        if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
            HandleJumpPress();

        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            // KatanaCombatHandler handles its own left-click input in Update().
            // Skip Attack() here to avoid a double-trigger on the katana level.
            if (_katanaCombatHandler == null)
                Attack();
        }
    }

    /// <summary>
    /// Keyboard (WASD + arrows + numpad) takes priority when any movement key is held.
    /// Otherwise a drifting gamepad stick can dominate with X-only input and remove forward/back.
    /// </summary>
    private Vector2 ReadMovementInput()
    {
        // ── Keyboard (always wins) ────────────────────────────────────────────
        Vector2 keyboard = BuildNormalizedKeyboardMoveInput(ResolveControlStyleState());
        _dbgKeyboard = keyboard;

        // ── Gamepad ───────────────────────────────────────────────────────────
        _dbgGamepad       = Vector2.zero;
        _dbgGamepadRawMag = 0f;
        Vector2 gamepadOut = Vector2.zero;

        if (Gamepad.current != null)
        {
            Vector2 stick = Gamepad.current.leftStick.ReadValue();
            _dbgGamepadRawMag = stick.magnitude;
            _dbgGamepad       = stick;

            // Hard floor: ignore gamepad entirely if raw magnitude ≤ 0.35.
            // Anything below this is considered drift regardless of the inspector
            // deadzone setting. This prevents a drifting stick from producing a
            // non-zero moveInputRaw that defeats the snap-to-zero guard.
            float threshold = Mathf.Max(0.35f, Mathf.Clamp01(gamepadMoveDeadzone));
            float mag = stick.magnitude;
            if (mag > threshold)
            {
                float scaled = Mathf.Clamp01((mag - threshold) / (1f - threshold));
                gamepadOut = (stick / mag) * scaled;
            }
        }

        // Keyboard always overrides gamepad; when no key is held return gamepad.
        if (keyboard.sqrMagnitude > 0.0001f)
            return keyboard;

        return gamepadOut;
    }

    private static GameManager.ControlStyleState ResolveControlStyleState()
    {
        if (GameManager.Instance != null)
            return GameManager.Instance.GetControlStyleState();

        return GameManager.LoadControlStyleState();
    }

    private static Vector2 BuildNormalizedKeyboardMoveInput(GameManager.ControlStyleState state)
    {
        if (Keyboard.current == null) return Vector2.zero;

        Keyboard k = Keyboard.current;
        Vector2 m = Vector2.zero;

        switch (state)
        {
            case GameManager.ControlStyleState.ArrowsMouse:
                if (k.upArrowKey.isPressed) m.y += 1f;
                if (k.downArrowKey.isPressed) m.y -= 1f;
                if (k.leftArrowKey.isPressed) m.x -= 1f;
                if (k.rightArrowKey.isPressed) m.x += 1f;
                break;
            case GameManager.ControlStyleState.WasdMouse:
            default:
                if (k.wKey.isPressed) m.y += 1f;
                if (k.sKey.isPressed) m.y -= 1f;
                if (k.aKey.isPressed) m.x -= 1f;
                if (k.dKey.isPressed) m.x += 1f;
                break;
        }

        return Vector2.ClampMagnitude(m, 1f);
    }

    private static string GetMovementDebugKeyPressed()
    {
        if (Keyboard.current == null) return "None";

        Keyboard k = Keyboard.current;
        if (k.wKey.isPressed) return "W";
        if (k.upArrowKey.isPressed) return "UpArrow";
        if (k.sKey.isPressed) return "S";
        if (k.downArrowKey.isPressed) return "DownArrow";
        if (k.aKey.isPressed) return "A";
        if (k.leftArrowKey.isPressed) return "LeftArrow";
        if (k.dKey.isPressed) return "D";
        if (k.rightArrowKey.isPressed) return "RightArrow";
        if (k.numpad8Key.isPressed) return "Numpad8";
        if (k.numpad2Key.isPressed) return "Numpad2";
        if (k.numpad4Key.isPressed) return "Numpad4";
        if (k.numpad6Key.isPressed) return "Numpad6";
        return "None";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MOVEMENT
    // ════════════════════════════════════════════════════════════════════════

    // ── Separation (Anti-Stacking) ───────────────────────────────────────────
    // Push the player out of any overlapping IDamageable actor so the
    // CharacterController never merges into a kinematic enemy capsule after
    // spawn overlap, knockback, or simultaneous melee lunges. Walls / props
    // are ignored — we only push against other characters.

    private static readonly Collider[] _separationBuffer = new Collider[24];

    /// <summary>
    /// Soft separation from other damageable actors only. Runs in Update (not FixedUpdate)
    /// so it never fights the main CharacterController move on a mismatched timestep.
    /// </summary>
    private void ApplyCharacterSeparationPush()
    {
        if (controller == null || !controller.enabled) return;
        if (isMantling || isFlipping) return;

        Vector3 worldCenter = transform.TransformPoint(controller.center);
        float   probe       = ForcedSeparationDistance;

        int hitCount = Physics.OverlapSphereNonAlloc(
            worldCenter, probe, _separationBuffer, ResolveHittableMask(), QueryTriggerInteraction.Ignore);
        if (hitCount == 0) return;

        Vector3 push = Vector3.zero;
        for (int i = 0; i < hitCount; i++)
        {
            Collider other = _separationBuffer[i];
            if (other == null) continue;
            if (other.transform == transform) continue;
            if (other.transform.IsChildOf(transform)) continue;

            IDamageable dmg = other.GetComponentInParent<IDamageable>();
            if (dmg == null) continue;

            Vector3 delta = transform.position - other.transform.position;
            delta.y = 0f;
            float d = delta.magnitude;

            if (d < 0.001f)
            {
                delta = new Vector3(transform.position.x - other.transform.position.x + 0.01f,
                                    0f,
                                    transform.position.z - other.transform.position.z + 0.01f);
                delta = delta.sqrMagnitude > 0f ? delta.normalized : Vector3.right;
                d = 0.01f;
            }

            float overlap = Mathf.Max(0f, ForcedSeparationDistance - d);
            if (overlap > 0f)
                push += (delta / d) * overlap;
        }

        if (push.sqrMagnitude > 1e-8f)
        {
            float cap = Mathf.Min(ForcedSeparationDistance, maxSeparationPushPerFrame);
            controller.Move(Vector3.ClampMagnitude(push, cap));
        }
    }

    /// <summary>
    /// Imported humanoid rigs sometimes ship with loose Rigidbodies on bones — they can tug
    /// the mesh and read as "flips". Kinematic + frozen tilt keeps visuals aligned with the capsule.
    /// </summary>
    private static void LockAvatarRigidbodies(GameObject body)
    {
        if (body == null) return;

        foreach (Rigidbody rb in body.GetComponentsInChildren<Rigidbody>(true))
        {
            if (rb == null) continue;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.constraints = RigidbodyConstraints.FreezeRotationX
                            | RigidbodyConstraints.FreezeRotationZ;
        }
    }

    private static LayerMask BuildDefaultStaticObstacleMask()
    {
        int mask = 0;
        void Add(string layerName)
        {
            int l = LayerMask.NameToLayer(layerName);
            if (l >= 0) mask |= 1 << l;
        }
        Add("Environment");
        Add("Default");
        Add("Map");
        Add("LevelContent");
        Add("Building");
        Add("Buildings");
        Add("Wall");
        Add("Walls");
        Add("StaticObstacle");
        return mask == 0 ? (LayerMask)0 : (LayerMask)mask;
    }

    private void GetCapsuleWorldEndpoints(out Vector3 bottom, out Vector3 top)
    {
        GetCapsuleWorldEndpoints(transform.position, out bottom, out top);
    }

    private void GetCapsuleWorldEndpoints(Vector3 rootPosition, out Vector3 bottom, out Vector3 top)
    {
        Vector3 worldCenter = rootPosition + controller.center;
        float halfHeight = Mathf.Max(controller.radius, controller.height * 0.5f - controller.radius);
        bottom = worldCenter - Vector3.up * halfHeight;
        top    = worldCenter + Vector3.up * halfHeight;
    }

    private bool IsGroundedForJump()
    {
        if (controller == null) return false;
        if (controller.isGrounded) return true;
        return TryGetWalkableFloorY(out _);
    }

    private bool IsAscendingJump()
    {
        return verticalVelocity.y > 0.06f;
    }

    private bool IsJumpHeld()
    {
        bool kb = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
        bool pad = Gamepad.current != null && Gamepad.current.buttonSouth.isPressed;
        return kb || pad;
    }

    private void UpdateJumpCoyoteTimers()
    {
        if (IsGroundedForJump())
            coyoteTimeCounter = coyoteTime;
        else
            coyoteTimeCounter -= MovementDeltaTime();

        jumpBufferCounter -= MovementDeltaTime();
    }

    private float GetAscentGravityMagnitude()
    {
        return Mathf.Abs(gravity) * Mathf.Max(0.01f, ascentGravityMultiplier);
    }

    private float GetDescentGravityMagnitude()
    {
        return Mathf.Abs(gravity) * Mathf.Max(0.01f, descentGravityMultiplier);
    }

    private float ComputeLaunchSpeedForApex(float apexHeight)
    {
        return Mathf.Sqrt(2f * GetAscentGravityMagnitude() * Mathf.Max(0f, apexHeight));
    }

    private float ResolveThrusterApexHeight()
    {
        if (thrusterApexHeightOverride > 0.01f)
            return thrusterApexHeightOverride;
        return thrusterApexHeight;
    }

    private float ResolveJumpLaunchSpeed()
    {
        return ComputeLaunchSpeedForApex(maxJumpApexHeight);
    }

    private float ResolveJumpCutReleaseSpeed()
    {
        return Mathf.Max(0.5f, ComputeLaunchSpeedForApex(maxJumpApexHeight) * 0.38f);
    }

    private bool CanPerformJump()
    {
        if (isProne || isSliding || isMantling || isFlipping)
            return false;
        if (_tacticalActions != null && _tacticalActions.IsTacticalAnimActive)
            return false;
        return coyoteTimeCounter > 0f;
    }

    private void ConstrainAscentToActiveApex()
    {
        if (verticalVelocity.y <= 0f) return;
        float cap = ComputeLaunchSpeedForApex(activeJumpApexHeight) * 1.01f;
        if (verticalVelocity.y > cap)
            verticalVelocity.y = cap;
    }

    private void LaunchJump(float apexHeight)
    {
        activeJumpApexHeight = Mathf.Max(0.1f, apexHeight);
        verticalVelocity.y = ComputeLaunchSpeedForApex(activeJumpApexHeight);
        coyoteTimeCounter = 0f;
        jumpBufferCounter = 0f;
        isGrounded = false;
        if (PlayerSfx.Instance != null)
            PlayerSfx.Instance.NotifyJump();
    }

    private bool TryConsumeBufferedJump()
    {
        if (jumpBufferCounter <= 0f || !CanPerformJump())
            return false;
        LaunchJump(maxJumpApexHeight);
        return true;
    }

    private float EvaluateAirborneGravity(bool jumpHeld)
    {
        if (verticalVelocity.y > 0f)
        {
            if (!jumpHeld && verticalVelocity.y < ResolveJumpCutReleaseSpeed())
                return gravity * jumpCutGravityMultiplier;
            return gravity * ascentGravityMultiplier;
        }

        if (verticalVelocity.y < 0f)
            return gravity * descentGravityMultiplier;

        return gravity;
    }

    private void HandleJumpPress()
    {
        if (isProne || isSliding || isMantling || isFlipping)
            return;
        if (_tacticalActions != null && _tacticalActions.IsActionLocked)
            return;

        if (IsGroundedForJump())
            coyoteTimeCounter = coyoteTime;

        bool wantsThruster = Keyboard.current != null
            && Keyboard.current.leftShiftKey.isPressed
            && moveInputSmoothed.sqrMagnitude > 0.05f;

        if (Gamepad.current != null
            && (Gamepad.current.leftShoulder.isPressed || Gamepad.current.leftTrigger.ReadValue() > 0.45f)
            && Gamepad.current.leftStick.ReadValue().sqrMagnitude > 0.05f)
            wantsThruster = true;

        if (wantsThruster && TryStartThrusterJump())
            return;

        jumpBufferCounter = jumpBufferTime;
        TryConsumeBufferedJump();
    }

    /// <summary>
    /// Forward + slightly-down ray: if we're driving velocity into a wall, damp horizontal speed
    /// (extra layer on top of CapsuleCast clamp) to reduce "frozen" feel on box colliders.
    /// </summary>
    /// <summary>
    /// CapsuleCast + one slide iteration so we shed velocity into walls instead of
    /// hammering CharacterController internal depenetration (stutter near crates).
    /// </summary>
    private Vector3 ClampHorizontalMoveAgainstStatics(Vector3 horizontalDelta)
    {
        if (IsAscendingJump()) return horizontalDelta;
        if (horizontalDelta.sqrMagnitude < 1e-12f || controller == null)
            return horizontalDelta;

        horizontalDelta.y = 0f;
        GetCapsuleWorldEndpoints(out Vector3 p0, out Vector3 p1);
        float r = controller.radius;
        float pad = Mathf.Max(controller.skinWidth, wallCollisionPadding) + minMoveClearance;
        float dist = horizontalDelta.magnitude;
        Vector3 dir = horizontalDelta / dist;

        if (!Physics.CapsuleCast(
                p0, p1,
                r * 0.98f,
                dir,
                out RaycastHit hit,
                dist + pad,
                staticObstacleMask,
                QueryTriggerInteraction.Ignore))
            return horizontalDelta;

        if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
            return horizontalDelta;

        float allowed = Mathf.Max(0f, hit.distance - pad);
        Vector3 move1 = dir * Mathf.Min(dist, allowed);
        Vector3 leftover = horizontalDelta - move1;
        if (leftover.sqrMagnitude < 1e-10f)
            return move1;

        Vector3 n = hit.normal;
        n.y = 0f;
        if (n.sqrMagnitude < 0.001f)
            return move1;
        n.Normalize();

        Vector3 slide = Vector3.ProjectOnPlane(leftover, n);
        slide.y = 0f;
        float slideDist = slide.magnitude;
        if (slideDist < 1e-6f)
            return move1;

        Vector3 sdir = slide / slideDist;
        Vector3 p0b = p0 + move1;
        Vector3 p1b = p1 + move1;
        if (!Physics.CapsuleCast(
                p0b, p1b,
                r * 0.98f,
                sdir,
                out RaycastHit hit2,
                slideDist + pad,
                staticObstacleMask,
                QueryTriggerInteraction.Ignore))
            return move1 + slide;

        if (hit2.collider != null && hit2.collider.transform.IsChildOf(transform))
            return move1 + slide;

        float allowed2 = Mathf.Max(0f, hit2.distance - pad);
        return move1 + sdir * Mathf.Min(slideDist, allowed2);
    }

    private void ApplyMovement()
    {
        if (controller == null) return;
        if (isMantling) return;

        Vector3 frameStartPosition = transform.position;

        // Sprint: LeftShift only (Settings → Sprint Mode: HOLD vs TOGGLE).
        // C = prone (tactical). Gamepad: left shoulder / left trigger while moving.
        bool shiftHeld = Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
        if (Keyboard.current != null && Keyboard.current.leftShiftKey.wasPressedThisFrame && SprintModeRuntime.IsToggleMode)
            sprintToggled = !sprintToggled;
        if (Keyboard.current != null && Keyboard.current.leftShiftKey.wasReleasedThisFrame && SprintModeRuntime.IsHoldMode)
            sprintToggled = false;

        bool kbSprint = SprintModeRuntime.IsToggleMode ? sprintToggled : shiftHeld;
        bool padSprint = Gamepad.current != null
            && (Gamepad.current.leftShoulder.isPressed || Gamepad.current.leftTrigger.ReadValue() > 0.45f)
            && moveInputSmoothed.sqrMagnitude > 0.05f;

        bool wantSprint = (kbSprint || padSprint) && moveInputSmoothed.y > 0.1f;
        if (SprintModeRuntime.IsToggleMode && sprintToggled && moveInputSmoothed.y <= 0.05f)
            sprintToggled = false;

        // Sliding / prone / crouch all force sprint off so you can't sprint
        // through a slide or while lying flat.
        if (isSliding || isProne)
            wantSprint = false;

        isSprinting = wantSprint;

        float targetSpeed    = isSprinting ? moveSpeed * sprintMultiplier : moveSpeed;
        if (isSliding) targetSpeed *= slideSpeedMultiplier;
        else if (isProne) targetSpeed *= proneSpeedMultiplier;
        else if (isCrouching) targetSpeed *= crouchSpeedMultiplier;

        // Dodge roll preserves its short boost so WASD cannot over-accelerate it.
        float dt = MovementDeltaTime();

        if (!isFlipping)
        {
            Vector3 moveDirection = GetCameraRelativeMoveDirection(moveInputSmoothed);
            Vector3 targetVelocity = moveDirection * targetSpeed;

            float rate = moveInputSmoothed.sqrMagnitude > 0.01f ? acceleration : deceleration;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, rate * dt);

            float planarSpeed = horizontalVelocity.magnitude;
            if (planarSpeed > targetSpeed + 0.02f)
                horizontalVelocity = horizontalVelocity * (targetSpeed / planarSpeed);

            // Facing is driven by RAW input, not the smoothed move vector.
            // The smoothed vector decays slowly on release, and its
            // .normalized keeps pointing the old way the whole time — so the
            // body used to keep rotating toward a stale facing after the keys
            // were released (the backward / strafe "rotation desync"). Raw
            // input recomputes the desired facing every frame and rotation
            // gates off cleanly the instant all keys are released.
            if (moveInputRaw.sqrMagnitude > 0.02f && !isMantling)
            {
                Vector3 faceDir = GetCameraRelativeFacingDirection(moveInputRaw);
                if (faceDir.sqrMagnitude > 0.0001f)
                {
                    float targetYaw = Quaternion.LookRotation(faceDir.normalized, Vector3.up).eulerAngles.y;
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation,
                        Quaternion.Euler(0f, targetYaw, 0f),
                        Mathf.Max(1f, turnSpeed) * dt);
                }
            }

            DebugMovementInput(moveDirection);
        }

        bool jumpFeetGrounded = IsGroundedForJump();
        bool tacticalAnim = _tacticalActions != null && _tacticalActions.IsTacticalAnimActive;
        bool onGroundContact = isGrounded || (jumpFeetGrounded && verticalVelocity.y <= 0f);
        RaycastHit groundHit = default;
        bool hasWalkableGround = onGroundContact && TryProbeWalkableGround(out groundHit);

        Vector3 horizontalDelta = horizontalVelocity * dt;
        float slopeVerticalMove = 0f;
        if (hasWalkableGround && groundHit.normal.y >= GetMinWalkableSurfaceNormalY())
        {
            Vector3 slopeMove = Vector3.ProjectOnPlane(horizontalVelocity, groundHit.normal) * dt;
            slopeVerticalMove = slopeMove.y;
            horizontalDelta = new Vector3(slopeMove.x, 0f, slopeMove.z);
        }
        else
            horizontalDelta.y = 0f;

        if (staticObstacleMask.value != 0 && !IsAscendingJump())
            horizontalDelta = ClampHorizontalMoveAgainstStatics(horizontalDelta);

        if (hasWalkableGround)
            horizontalDelta.y = slopeVerticalMove;

        UpdateJumpCoyoteTimers();
        TryConsumeBufferedJump();

        if (onGroundContact)
        {
            if (!IsAscendingJump())
                verticalVelocity.y = -GetGroundedStickVelocity();
            if (tacticalAnim && jumpFeetGrounded && !IsAscendingJump())
                verticalVelocity.y = Mathf.Max(verticalVelocity.y, -GetGroundedStickVelocity());
        }
        else
        {
            float appliedGravity = EvaluateAirborneGravity(IsJumpHeld());
            verticalVelocity.y += appliedGravity * dt;
            ConstrainAscentToActiveApex();
            verticalVelocity.y = Mathf.Max(verticalVelocity.y, -maxFallSpeed);
        }

        Vector3 verticalDelta = new Vector3(0f, verticalVelocity.y * dt, 0f);
        if (IsAscendingJump())
        {
            controller.Move(verticalDelta);
            controller.Move(horizontalDelta);
        }
        else
            controller.Move(horizontalDelta + verticalDelta);

        UpdateTacticalManeuverState();
        UpdateStanceCollider();

        if (_tacticalActions != null)
            _tacticalActions.Tick(dt);

        ClampInsideArena();

        if (IsStandingStill())
        {
            horizontalVelocity = Vector3.zero;
            actualHorizontalVelocity = Vector3.zero;
        }
        else
        {
            Vector3 frameDisplacement = transform.position - frameStartPosition;
            frameDisplacement.y = 0f;
            actualHorizontalVelocity = dt > 0f
                ? frameDisplacement / dt
                : Vector3.zero;
        }

        wasMoving = moveInputSmoothed.sqrMagnitude > 0.01f;

        if (PlayerSfx.Instance != null && onGroundContact && !isProne && !isSliding)
            PlayerSfx.Instance.TickFootsteps(true, isSprinting, horizontalVelocity.magnitude);
    }

    private Vector3 GetCameraRelativeMoveDirection(Vector2 input)
    {
        input = Vector2.ClampMagnitude(input, 1f);
        if (input.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        Vector3 forward;
        Vector3 right;
        if (isThirdPersonActive && ThirdPersonOrbitCamera.Instance != null)
        {
            forward = ThirdPersonOrbitCamera.GetMovementForward();
            right = ThirdPersonOrbitCamera.GetMovementRight();
        }
        else
        {
            Quaternion yawOnlyBasis = Quaternion.Euler(0f, cameraYaw, 0f);
            forward = yawOnlyBasis * Vector3.forward;
            right = yawOnlyBasis * Vector3.right;
        }

        Vector3 moveDirection = (right * input.x) + (forward * input.y);
        moveDirection.y = 0f;
        return moveDirection.sqrMagnitude > 1f
            ? moveDirection.normalized
            : moveDirection;
    }

    /// <summary>
    /// Body yaw target. Backward input (S / down arrow) still moves the character
    /// backward but keeps facing camera-forward so the model does not spin 180°.
    /// </summary>
    private Vector3 GetCameraRelativeFacingDirection(Vector2 input)
    {
        input = Vector2.ClampMagnitude(input, 1f);
        if (input.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        Vector2 facingInput = input;
        if (facingInput.y < -0.01f)
            facingInput.y = 0f;

        if (facingInput.sqrMagnitude < 0.0001f)
            facingInput = Vector2.up;

        return GetCameraRelativeMoveDirection(facingInput);
    }

    private void DebugMovementInput(Vector3 moveDirection)
    {
        if (!debugMovementInput || Time.time < nextMovementInputDebugLogTime)
            return;

        float bodyYaw = thirdPersonBody != null ? thirdPersonBody.transform.eulerAngles.y : -1f;
        nextMovementInputDebugLogTime = Time.time + 0.35f;
        Debug.Log($"[PlayerController] keyPressed={GetMovementDebugKeyPressed()}, " +
                  $"rawInput=({moveInputRaw.x:0.###},{moveInputRaw.y:0.###}), " +
                  $"finalMoveInput=({moveInputSmoothed.x:0.###},{moveInputSmoothed.y:0.###}), " +
                  $"finalMoveDirection=({moveDirection.x:0.###},{moveDirection.z:0.###}), " +
                  $"rootYaw={transform.eulerAngles.y:0.###}, bodyYaw={bodyYaw:0.###}");
    }

    private Vector3 GetActualHorizontalVelocity()
        => actualHorizontalVelocity;

    private Vector3 ResolveMoveWorldDirection(Vector2 input)
    {
        Vector2 clamped = Vector2.ClampMagnitude(input, 1f);
        if (clamped.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        return new Vector3(clamped.x, 0f, clamped.y);
    }

    private void EnforcePerspectiveVisibility()
    {
        if (thirdPersonBody == null)
            return;

        if (isThirdPersonActive)
            SetThirdPersonRenderersVisible(true);
        else
            SetThirdPersonRenderersVisible(false);
    }

    // ✅ FIX: GetActiveAnimator always returns the live animator from thirdPersonBody
    //         so it works even if the body was spawned after Start()
    private Animator GetActiveAnimator()
    {
        if (thirdPersonBody != null)
        {
            Animator bodyAnim = thirdPersonBody.GetComponentInChildren<Animator>(true);
            if (bodyAnim != null)
            {
                // Keep the cached field in sync so other code that reads animator directly works
                if (bodyAnim != animator)
                {
                    animator = bodyAnim;
                    ConfigureAnimatorBinding(animator, forceControllerAssignment: false);
                    CacheAnimatorParameters();
                }
                return bodyAnim;
            }
        }
        return animator;
    }

    public void TeleportTo(Vector3 position)
    {
        if (controller == null)
            controller = GetComponent<CharacterController>();

        if (IsUnsafeSpawn(position))
            position = SafeFallbackSpawn;

        position = ResolveSafeSpawnPosition(position);

        if (controller != null) controller.enabled = false;
        transform.position = position;
        if (controller != null) controller.enabled = true;
        SnapCapsuleToWalkableGround();
        verticalVelocity = Vector3.zero;
        verticalVelocity.y = -GetGroundedStickVelocity();
        _postSpawnGroundSnapFrames = 4;
        Physics.SyncTransforms();
        cameraYaw = transform.eulerAngles.y;

        if (runtimeThirdPersonCamera != null)
        {
            CameraController camCtrl = runtimeThirdPersonCamera.GetComponent<CameraController>();
            if (camCtrl != null)
            {
                camCtrl.target = transform;
                camCtrl.SnapToTarget();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CAMERA / LOOK
    // ════════════════════════════════════════════════════════════════════════

    private void ApplyLook()
    {
        Camera cam = ActiveCamera;
        if (cam == null) return;

        // Mouse-look responsiveness is now driven by the camera's
        // RotationSpeed property, which combines the designer-tuned base
        // rotation speed with the live Options-menu sensitivity multiplier
        // (0–7 slider → 0.20×–2.20×). Falls back to the local sensitivity
        // value if the CameraController hasn't initialized yet.
        float rotSpeed = (CameraController.Instance != null)
            ? CameraController.Instance.RotationSpeed
            : sensitivity * 100f * LookSensitivityRuntime.LookMultiplier;
        // Convert deg/sec back into the per-mouse-unit input scale we used
        // before so existing input.lookInput numbers continue to feel right.
        float perUnit = rotSpeed * 0.01f;
        // Use the smoothed input so rotation doesn't jitter on high DPI mice.
        // Use unscaledDeltaTime so pause/menu timeScale changes don't distort sensitivity.
        float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
        float mouseX = lookInputSmoothed.x * perUnit * dt;
        float mouseY = lookInputSmoothed.y * perUnit * dt;

        cameraPitch -= mouseY;
        if (isThirdPersonActive)
        {
            // Keep the third-person camera in a grounded shooter range.
            cameraPitch = Mathf.Clamp(cameraPitch, ThirdPersonMinPitch, ThirdPersonMaxPitch);
        }
        else
        {
            // Call-of-duty style: wider pitch in first person.
            cameraPitch = Mathf.Clamp(cameraPitch, FirstPersonMinPitch, FirstPersonMaxPitch);
        }

        cameraYaw += mouseX;

        // Soft camera follow: drift cameraYaw toward the player's facing direction while moving.
        if (isThirdPersonActive &&
            cameraMovementFollowStrength > 0.001f &&
            moveInputSmoothed.sqrMagnitude > 0.04f)
        {
            float facingYaw  = transform.eulerAngles.y;
            float yawDelta   = Mathf.DeltaAngle(cameraYaw, facingYaw);
            float followRate = cameraMovementFollowStrength * Time.deltaTime;
            float speedFactor = Mathf.Clamp01(moveInputSmoothed.magnitude);
            cameraYaw += yawDelta * followRate * speedFactor;
        }

        if (isThirdPersonActive && runtimeThirdPersonCamera != null)
        {
            CameraController orbitCtrl = runtimeThirdPersonCamera.GetComponent<CameraController>();
            if (orbitCtrl != null)
            {
                // Decouple camera orbit yaw from player facing so the character can
                // smoothly rotate toward movement direction without camera-driven flips.
                orbitCtrl.useExternalYaw = true;
                orbitCtrl.externalYaw = cameraYaw;
                orbitCtrl.pitch = cameraPitch;
            }
        }
        else if (!isThirdPersonActive && firstPersonCam != null)
        {
            // Apply pitch directly to the FPS camera, preserving its base orientation.
            firstPersonCam.transform.localRotation = firstPersonLocalRot * Quaternion.Euler(cameraPitch, 0f, 0f);
        }
    }

    private void UpdateHeadBob()
    {
        if (isThirdPersonActive) return;
        if (firstPersonCam == null) return;

        Vector3 basePos = firstPersonLocalPos;
        if (IsStandingStill() || !isGrounded)
        {
            firstPersonCam.transform.localPosition = basePos;
            return;
        }

        // Bob from locomotion intent — not physics displacement (ground snap noise).
        float speed = horizontalVelocity.magnitude;
        float bobStrength = Mathf.Clamp01(speed / 6f);
        if (bobStrength < 0.05f)
        {
            firstPersonCam.transform.localPosition = basePos;
            return;
        }

        float bobFreq = Mathf.Lerp(0f, 10.5f, bobStrength);
        float y = Mathf.Sin(Time.time * bobFreq) * 0.03f * bobStrength;
        float x = Mathf.Cos(Time.time * bobFreq * 0.5f) * 0.02f * bobStrength;
        firstPersonCam.transform.localPosition = basePos + new Vector3(x, y, 0f);
    }

    private void UpdateCameraZoom()
    {
        // Zoom permanently disabled — camera stays at its default OTS offset.
    }

    private void UpdateCameraKick()
    {
        if (ActiveCamera == null) return;
        cameraKickCurrent = Mathf.Lerp(cameraKickCurrent, cameraKickTarget, 18f * Time.deltaTime);
        cameraKickTarget  = Mathf.Lerp(cameraKickTarget, 0f, 14f * Time.deltaTime);
        cameraPitch += cameraKickCurrent * Time.deltaTime;
        // Keep camera kick inside the same grounded shooter range.
        cameraPitch = Mathf.Clamp(
            cameraPitch,
            isThirdPersonActive ? ThirdPersonMinPitch : FirstPersonMinPitch,
            isThirdPersonActive ? ThirdPersonMaxPitch : FirstPersonMaxPitch);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  COMBAT
    // ════════════════════════════════════════════════════════════════════════

    public void Attack()
    {
        if (IsRemoteNetworkPlayer()) return;
        if (attackCooldownTimer > 0f || isAttacking) return;

        if (CombatDebug.Enabled)
            CombatDebug.Log("PlayerAttack started");

        isAttacking         = true;
        attackCooldownTimer = AttackCooldown;
        attackResetTimer    = AttackResetTime;
        attackFailsafeTimer = AttackFailsafeDuration;

        Animator activeAnimator = GetActiveAnimator();
        if (activeAnimator == null || activeAnimator.runtimeAnimatorController == null)
        {
            Debug.LogWarning(
                "Attack started without a valid RuntimeAnimatorController. " +
                "Combat failsafe will clear the attack lock automatically.",
                this);
        }

        FireAttack();
        SendNetworkAttackVisual();
    }

    private void FireAttack()
    {
        ApplyAttackLunge();

        if (audioSource != null && swordSwing != null)
        {
            audioSource.pitch = Random.Range(0.9f, 1.1f);
            audioSource.PlayOneShot(swordSwing, AudioSettingsRuntime.ScaledSfx(1f));
        }

        // Per-category swing audio + optional swing-trail VFX. No-ops if the
        // WeaponCombatAudio component isn't on the player or has empty slots.
        WeaponCombatAudio.PlaySwingFor(gameObject, GetEquippedWeaponLevel());

        if (weaponHitboxRoutine != null)
            StopCoroutine(weaponHitboxRoutine);

        if (equippedWeaponObject != null)
        {
            WeaponBase weapon = equippedWeaponObject.GetComponent<WeaponBase>();
            if (weapon != null) weapon.Attack();

            // Drive the per-weapon "alive" animation (e.g. Nunchucks open
            // + spin during the swing window). The animator handles its
            // own timing relative to attackSpeed/attackDelay.
            WeaponLiveAnimator live = equippedWeaponObject.GetComponent<WeaponLiveAnimator>();
            if (live != null) live.PlayAttack(attackSpeed, attackDelay);
        }

        bool immediateHit = TryImmediateMeleeStrike();

        if (!immediateHit && equippedWeaponHitbox != null)
        {
            if (weaponHitboxRoutine != null)
                StopCoroutine(weaponHitboxRoutine);
            weaponHitboxRoutine = StartCoroutine(PlayerWeaponHitboxWindowRoutine());
        }
    }

    private System.Collections.IEnumerator PlayerWeaponHitboxWindowRoutine()
    {
        equippedWeaponHitbox.DisableHitbox();

        if (attackDelay > 0f)
            yield return new WaitForSeconds(attackDelay);

        equippedWeaponHitbox.EnableHitbox();

        yield return new WaitForSeconds(0.12f);

        equippedWeaponHitbox.DisableHitbox();
        weaponHitboxRoutine = null;
    }

    private void UpdateCombatState()
    {
        if (attackCooldownTimer > 0f)
            attackCooldownTimer -= Time.deltaTime;

        if (!isAttacking) return;

        if (attackResetTimer > 0f)
            attackResetTimer -= Time.deltaTime;

        if (attackFailsafeTimer > 0f)
            attackFailsafeTimer -= Time.deltaTime;

        bool normalAttackFinished = attackResetTimer <= 0f;
        bool failsafeExpired = attackFailsafeTimer <= 0f;

        if (normalAttackFinished || failsafeExpired)
            ResetAttackState();
    }

    private void ToggleCrouch()
    {
        if (isMantling) return;
        if (isProne)
        {
            isProne = false;
            isCrouching = true;
        }
        else
        {
            isCrouching = !isCrouching;
        }
    }

    /// <summary>
    /// Prone (C). Animator plays Prone Idle; capsule bottom stays pinned; mesh offset on body child only.
    /// </summary>
    private void ToggleCrawl()
    {
        if (isMantling || isSliding || isFlipping) return;
        isProne = !isProne;
        if (isProne)
        {
            isCrouching   = false;
            isSliding     = false;
            sprintToggled = false;
            if (_tacticalActions != null)
                _tacticalActions.SetProne(true);
            else
                EnterLowProfileStance();
            Animator anim = GetActiveAnimator();
            if (anim != null) AnimSetBool(anim, "IsProne", true);
        }
        else
        {
            if (_tacticalActions != null)
                _tacticalActions.SetProne(false);
            else
                FlushLowProfileGroundSnap(forceZeroMove: true);

            RestoreProneVisualPose();

            Animator anim = GetActiveAnimator();
            if (anim != null) AnimSetBool(anim, "IsProne", false);
        }
    }

    // Kept for backwards-compat with any callers that still hit the old name.
    private void ToggleProne() => ToggleCrawl();

    /// <summary>Legacy sprint lock helper kept for any old callers.</summary>
    private void ToggleSprintLock()
    {
        if (isProne || isSliding || isMantling || isFlipping) return;
        sprintToggled = !sprintToggled;
    }

    private void TriggerTacticalAbilityPlaceholder()
    {
        Debug.Log("[PlayerController] Tactical ability placeholder triggered.", this);
    }

    /// <summary>
    /// Performs a grounded dodge roll with a short horizontal boost and visual
    /// body rotation. Cannot chain into another roll until <see cref="flipCooldown"/> elapses.
    /// </summary>
    private void TryStartDodgeRoll()
    {
        if (isMantling || isProne || isSliding || isFlipping) return;
        if (flipCooldownTimer > 0f) return;
        if (controller == null || !controller.enabled) return;
        if (!IsGroundedForJump()) return;

        isCrouching = false;

        // Forward direction in world space — falls back to the player's
        // facing if there's no input on the WASD axes.
        Vector3 inputDir = new Vector3(moveInputSmoothed.x, 0f, moveInputSmoothed.y);
        Vector3 forward  = inputDir.sqrMagnitude > 0.05f
            ? transform.TransformDirection(inputDir.normalized)
            : transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = transform.forward;

        verticalVelocity.y = -GetGroundedStickVelocity();
        horizontalVelocity = forward.normalized * flipForwardBoost;
        flipCooldownTimer  = flipCooldown;

        if (flipCoroutine != null) StopCoroutine(flipCoroutine);
        flipCoroutine = StartCoroutine(DodgeRollRoutine(forward));
    }

    private System.Collections.IEnumerator DodgeRollRoutine(Vector3 worldForward)
    {
        isFlipping = true;
        // Reference axis to rotate around for the roll body pose.
        // We rotate the body's local X axis through 360 degrees over the duration.
        Quaternion baseLocal = thirdPersonBodyBaseLocalRotation;

        float elapsed  = 0f;
        float duration = Mathf.Max(0.15f, flipDuration);
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / duration);
            // Smooth ease so the spin feels weighty rather than linear.
            float ease = Mathf.Sin(t * Mathf.PI * 0.5f);
            float angle = ease * 360f;
            if (thirdPersonBody != null)
                thirdPersonBody.transform.localRotation = baseLocal * Quaternion.AngleAxis(angle, Vector3.right);
            float stick = -GetGroundedStickVelocity();
            if (controller != null && controller.isGrounded && verticalVelocity.y < stick)
                verticalVelocity.y = stick;
            yield return null;
        }

        if (thirdPersonBody != null)
            thirdPersonBody.transform.localRotation = baseLocal;

        isFlipping = false;
        flipCoroutine = null;

        // Keep a grounded downward bias without launching or snapping the player.
        // Drop nearly all residual horizontal boost so WASD input is responsive
        // again the instant the roll ends.
        if (verticalVelocity.y < 0f)
            verticalVelocity.y = -GetGroundedStickVelocity();
        horizontalVelocity *= 0.15f;
        SnapRootAboveFloorImmediate();
    }

    private void SnapRootAboveFloorImmediate()
    {
        if (controller == null) return;

        Vector3 p = transform.position;
        if (!TryGetWalkableFloorY(out float floorY))
            return;

        float restY = floorY - controller.center.y + (controller.height * 0.5f) + controller.skinWidth;
        float gap = p.y - restY;
        if (gap < -0.02f)
        {
            controller.enabled = false;
            transform.position = new Vector3(p.x, restY, p.z);
            controller.enabled = true;
        }
        else if (gap > 0.001f && gap <= maxFloorSnapDistance)
            controller.Move(Vector3.down * gap);
    }

    // Returns a layermask that covers solid ground but excludes the player's
    // own capsule so snap raycasts never self-intersect.
    private static int BuildGroundCheckMask()
    {
        int mask = Physics.DefaultRaycastLayers;
        int playerLayer    = LayerMask.NameToLayer("Player");
        int characterLayer = LayerMask.NameToLayer("Character");
        int hittableLayer  = LayerMask.NameToLayer("Hittable");
        if (playerLayer    >= 0) mask &= ~(1 << playerLayer);
        if (characterLayer >= 0) mask &= ~(1 << characterLayer);
        if (hittableLayer  >= 0) mask &= ~(1 << hittableLayer);
        return mask;
    }

    private void TryStartSlide()
    {
        if (isMantling || isProne || isSliding || isFlipping) return;
        if (!IsGroundedForJump()) return;
        if (slideCooldownTimer > 0f) return;
        if (moveInputSmoothed.sqrMagnitude < 0.1f) return;

        isSliding = true;
        slideTimer = slideDuration;
        slideCooldownTimer = powerSlideCooldown;
        isCrouching = true;
        Vector3 inputDir = new Vector3(moveInputSmoothed.x, 0f, moveInputSmoothed.y);
        Vector3 slideDirection = inputDir.sqrMagnitude > 0.05f
            ? transform.TransformDirection(inputDir.normalized)
            : transform.forward;
        slideDirection.y = 0f;
        if (slideDirection.sqrMagnitude < 0.001f)
            slideDirection = transform.forward;
        horizontalVelocity = slideDirection.normalized * powerSlideBoost;

        Animator slideAnim = GetActiveAnimator();
        if (slideAnim != null) AnimFireTrigger(slideAnim, "Slide");

        if (_tacticalActions != null)
            _tacticalActions.BeginSlide();
        else
            EnterLowProfileStance();
    }

    private void TryStartJumpOver()
    {
        if (isMantling || isProne || isSliding || isFlipping) return;
        if (_tacticalActions != null && _tacticalActions.IsActionLocked) return;
        if (!IsGroundedForJump()) return;
        if (jumpOverCooldownTimer > 0f) return;

        jumpOverCooldownTimer = 0.9f;
        verticalVelocity.y = -GetGroundedStickVelocity();

        Animator anim = GetActiveAnimator();
        if (anim != null) AnimFireTrigger(anim, "JumpOver");

        if (_tacticalActions != null)
            _tacticalActions.BeginJumpOver();
        else
            EnterLowProfileStance();
    }

    private bool TryStartThrusterJump()
    {
        if (!IsGroundedForJump() || thrusterCooldownTimer > 0f)
            return false;

        Vector3 inputDir = new Vector3(moveInputSmoothed.x, 0f, moveInputSmoothed.y);
        Vector3 thrustDirection = inputDir.sqrMagnitude > 0.05f
            ? transform.TransformDirection(inputDir.normalized)
            : transform.forward;
        thrustDirection.y = 0f;
        if (thrustDirection.sqrMagnitude < 0.001f)
            thrustDirection = transform.forward;

        activeJumpApexHeight = ResolveThrusterApexHeight();
        verticalVelocity.y = ComputeLaunchSpeedForApex(activeJumpApexHeight);
        horizontalVelocity += thrustDirection.normalized * thrusterForwardBoost;
        thrusterCooldownTimer = thrusterCooldown;
        isGrounded = false;
        coyoteTimeCounter = 0f;
        jumpBufferCounter = 0f;
        if (PlayerSfx.Instance != null)
            PlayerSfx.Instance.NotifyJump();
        isCrouching = false;
        return true;
    }

    private void TryMantle()
    {
        if (isMantling || isSliding) return;
        if (mantleCoroutine != null) return;

        Vector3 origin = transform.position + Vector3.up * 1.0f;
        int groundMask = GetGroundCheckMask();
        if (!Physics.Raycast(origin, transform.forward, out RaycastHit wallHit, 1.1f, groundMask, QueryTriggerInteraction.Ignore))
        {
            // Fallback mini-vault so V always produces a visible maneuver.
            Vector3 fallbackTarget = transform.position + transform.forward * 0.7f + Vector3.up * 0.25f;
            mantleCoroutine = StartCoroutine(MantleRoutine(fallbackTarget));
            return;
        }

        Vector3 topCheck = wallHit.point + Vector3.up * mantleUpHeight + transform.forward * 0.15f;
        if (Physics.Raycast(topCheck, Vector3.down, out RaycastHit topHit, mantleUpHeight + 1.2f, groundMask, QueryTriggerInteraction.Ignore))
        {
            mantleCoroutine = StartCoroutine(MantleRoutine(topHit.point + transform.forward * mantleForwardDistance));
        }
    }

    private System.Collections.IEnumerator MantleRoutine(Vector3 mantleTarget)
    {
        isMantling = true;
        isSliding = false;
        Vector3 start = transform.position;
        float t = 0f;
        float duration = Mathf.Max(0.05f, mantleDuration);
        if (controller != null) controller.enabled = false;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            transform.position = Vector3.Lerp(start, mantleTarget, Mathf.Clamp01(t));
            yield return null;
        }

        if (controller != null) controller.enabled = true;
        verticalVelocity = Vector3.zero;
        isMantling = false;
        mantleCoroutine = null;
    }

    private void UpdateTacticalManeuverState()
    {
        if (slideCooldownTimer > 0f)
            slideCooldownTimer -= Time.deltaTime;

        if (thrusterCooldownTimer > 0f)
            thrusterCooldownTimer -= Time.deltaTime;

        if (flipCooldownTimer > 0f)
            flipCooldownTimer -= Time.deltaTime;

        if (jumpOverCooldownTimer > 0f)
            jumpOverCooldownTimer -= Time.deltaTime;

        if (isSliding)
        {
            slideTimer -= Time.deltaTime;
            if (slideTimer <= 0f)
            {
                isSliding = false;
                isCrouching = false;
                if (_tacticalActions != null)
                    _tacticalActions.EndSlide();
            }
        }
    }

    private void UpdateStanceCollider()
    {
        if (controller == null) return;

        bool tacticalAnim = _tacticalActions != null && _tacticalActions.IsTacticalAnimActive;
        bool lowProfile = isProne || isSliding || tacticalAnim;
        bool enteredLowProfile = lowProfile && !_wasInLowProfileStance;
        _wasInLowProfileStance = lowProfile;

        if (_tacticalActions != null)
        {
            _tacticalActions.ApplyColliderState(isProne, isSliding, isCrouching, tacticalAnim);
            targetControllerHeight = controller.height;
            targetControllerCenter = controller.center;
            if (enteredLowProfile)
                _tacticalActions.FlushGroundSnap(zeroMove: true);
        }
        else
        {
            float standHeight = _standingHeight > 0.01f ? _standingHeight : 1.8f;
            float desiredHeight = standHeight;
            if (isProne) desiredHeight = proneHeight;
            else if (isCrouching || isSliding) desiredHeight = crouchHeight;

            targetControllerHeight = Mathf.Clamp(desiredHeight, 0.6f, standHeight);
            targetControllerCenter = new Vector3(0f, _capsuleBottomY + targetControllerHeight * 0.5f, 0f);

            if (lowProfile || enteredLowProfile)
            {
                SnapStanceColliderImmediate();
                if (enteredLowProfile)
                    FlushLowProfileGroundSnap(forceZeroMove: true);
            }
            else
            {
                controller.height = Mathf.Lerp(controller.height, targetControllerHeight, stanceTransitionSpeed * Time.deltaTime);
                controller.center = Vector3.Lerp(controller.center, targetControllerCenter, stanceTransitionSpeed * Time.deltaTime);
            }

            float desiredStep = controller.height > 1.6f ? 0.5f : 0.05f;
            float maxStep = Mathf.Max(0f, controller.height - 0.01f);
            if (desiredStep > maxStep)
            {
                Debug.Log($"[Prone] clamped stepOffset because it exceeded controller height (desired={desiredStep} height={controller.height})");
                desiredStep = maxStep;
            }
            controller.stepOffset = desiredStep;
        }

        if (_tacticalActions != null)
            _tacticalActions.DisableRootMotionOnAnimators();
        else
            DisableAnimatorRootMotion();

        if (isFlipping) return;
        UpdateLowProfileBodyPose();
    }

    /// <summary>
    /// Called when entering slide or prone — pins capsule to ground like enemy AI.
    /// </summary>
    private void EnterLowProfileStance()
    {
        SnapStanceColliderImmediate();
        FlushLowProfileGroundSnap(forceZeroMove: true);
        DisableAnimatorRootMotion();
    }

    private void SnapStanceColliderImmediate()
    {
        if (controller == null) return;

        controller.height = targetControllerHeight;
        controller.center = targetControllerCenter;
    }

    /// <summary>
    /// Physics flush: zero-move then lift capsule bottom to floor (CharacterController.Move).
    /// </summary>
    private void FlushLowProfileGroundSnap(bool forceZeroMove)
    {
        if (controller == null || !controller.enabled || isMantling) return;

        if (forceZeroMove)
            controller.Move(Vector3.zero);

        int groundMask = GetGroundCheckMask();
        Vector3 rayOrigin = transform.position + Vector3.up * 0.35f;
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f, groundMask, QueryTriggerInteraction.Ignore))
            return;

        if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
            return;

        float targetRootY = hit.point.y - controller.center.y + controller.height * 0.5f + controller.skinWidth;
        float deltaY = targetRootY - transform.position.y;
        if (deltaY > 0.001f)
            controller.Move(Vector3.up * deltaY);

        if (verticalVelocity.y < 0f)
            verticalVelocity.y = -GetGroundedStickVelocity();
    }

    private void DisableAnimatorRootMotion()
    {
        if (!isProne && !isSliding && !isCrouching) return;

        Animator active = GetActiveAnimator();
        if (active != null)
            active.applyRootMotion = false;

        if (thirdPersonBody != null)
        {
            foreach (Animator a in thirdPersonBody.GetComponentsInChildren<Animator>(true))
            {
                if (a != null)
                    a.applyRootMotion = false;
            }
        }
    }

    private void UpdateLowProfileBodyPose()
    {
        if (thirdPersonBody == null) return;

        if (isProne)
        {
            ApplyProneVisualPose();
            return;
        }

        float desiredOffsetY = 0f;
        if (isCrouching || isSliding) desiredOffsetY = crouchBodyYOffset;

        Vector3 desiredPos = thirdPersonBodyBaseLocalPosition + new Vector3(0f, desiredOffsetY, 0f);
        if (isSliding)
            thirdPersonBody.transform.localPosition = desiredPos;
        else
        {
            thirdPersonBody.transform.localPosition = Vector3.Lerp(
                thirdPersonBody.transform.localPosition,
                desiredPos,
                stanceTransitionSpeed * Time.deltaTime);
        }

        thirdPersonBody.transform.localRotation = isSliding
            ? thirdPersonBodyBaseLocalRotation
            : Quaternion.Slerp(
                thirdPersonBody.transform.localRotation,
                thirdPersonBodyBaseLocalRotation,
                stanceTransitionSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Prone uses the Prone Idle animator pose only — no manual -90° pitch (that buried the mesh).
    /// Visual Y offset is applied on the body child, not the CharacterController root.
    /// </summary>
    private void ApplyProneVisualPose()
    {
        float yOff = _tacticalActions != null
            ? _tacticalActions.ProneVisualYOffset
            : Mathf.Max(0f, proneCrawlBodyYOffset);

        Vector3 desiredPos = thirdPersonBodyBaseLocalPosition + new Vector3(0f, yOff, 0f);
        thirdPersonBody.transform.localPosition = desiredPos;
        thirdPersonBody.transform.localRotation = thirdPersonBodyBaseLocalRotation;
    }

    private void RestoreProneVisualPose()
    {
        if (thirdPersonBody == null) return;
        thirdPersonBody.transform.localPosition = thirdPersonBodyBaseLocalPosition;
        thirdPersonBody.transform.localRotation = thirdPersonBodyBaseLocalRotation;
    }

    private void ClampLowProfileVisualMesh()
    {
        if (thirdPersonBody == null) return;

        if (isProne)
        {
            float floorLocalY = thirdPersonBodyBaseLocalPosition.y
                + (_tacticalActions != null ? _tacticalActions.ProneVisualYOffset : Mathf.Max(0f, proneCrawlBodyYOffset));
            Vector3 lp = thirdPersonBody.transform.localPosition;
            if (lp.y < floorLocalY)
                thirdPersonBody.transform.localPosition = new Vector3(lp.x, floorLocalY, lp.z);
            return;
        }

        float floorY = thirdPersonBodyBaseLocalPosition.y;
        if (isSliding || isCrouching) floorY += crouchBodyYOffset;

        Vector3 pos = thirdPersonBody.transform.localPosition;
        if (pos.y < floorY)
            thirdPersonBody.transform.localPosition = new Vector3(pos.x, floorY, pos.z);
    }

    private void ResetAttackState()
    {
        isAttacking = false;
        attackResetTimer = 0f;
        attackFailsafeTimer = 0f;
    }

    private void ApplyAttackLunge()
    {
        if (controller == null || !controller.enabled) return;
        Vector3 lunge = transform.forward * attackLungeDistance;
        lunge.y = 0f;
        if (staticObstacleMask.value != 0)
            lunge = ClampHorizontalMoveAgainstStatics(lunge);
        controller.Move(lunge);
        ClampInsideArena();
    }

    private void AttackRaycast()
    {
        AttackMelee();
        cameraKickTarget = -1.2f;
    }

    private const float MeleeHitAngle = 60f;   // strict forward-facing cone (half-angle)

    private bool TryImmediateMeleeStrike()
    {
        Physics.SyncTransforms();

        Camera cam = GetGameplayCamera();
        if (cam == null)
            cam = Camera.main;
        Vector3 origin;
        Vector3 forward;

        if (cam != null && !isThirdPersonActive)
        {
            origin = cam.transform.position + cam.transform.forward * 0.12f;
            forward = cam.transform.forward;
        }
        else
        {
            origin = transform.position + Vector3.up * 1.15f + transform.forward * 0.25f;
            forward = transform.forward;
        }

        if (forward.sqrMagnitude < 0.0001f)
            return false;
        forward.Normalize();

        float radius = Mathf.Clamp(attackRadius * 0.42f, 0.25f, 0.65f);
        float reach = Mathf.Max(attackDistance + 0.65f, 2.8f);
        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            radius,
            forward,
            meleeRaycastHits,
            reach,
            ~0,
            QueryTriggerInteraction.Collide);

        Transform bestTarget = null;
        Vector3 bestPoint = origin + forward * reach;
        float bestDistance = float.PositiveInfinity;
        float nearestBlockerDistance = float.PositiveInfinity;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = meleeRaycastHits[i];
            Collider col = hit.collider;
            if (col == null) continue;
            if (col.transform == transform || col.transform.IsChildOf(transform)) continue;

            Transform damageRoot = GetDamageTargetTransform(col.transform);
            if (damageRoot != null)
            {
                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    bestTarget = damageRoot;
                    bestPoint = hit.point.sqrMagnitude > 0.0001f ? hit.point : col.ClosestPoint(origin);
                }
                continue;
            }

            if (!col.isTrigger && IsMeleeBlocker(col))
                nearestBlockerDistance = Mathf.Min(nearestBlockerDistance, hit.distance);
        }

        if (bestTarget == null)
        {
            Vector3 playerCenter = transform.position + Vector3.up * 1.0f;
            Vector3 forwardFlat = forward;
            forwardFlat.y = 0f;
            if (forwardFlat.sqrMagnitude < 0.0001f)
                forwardFlat = transform.forward;
            forwardFlat.y = 0f;
            if (forwardFlat.sqrMagnitude < 0.0001f)
                return false;
            forwardFlat.Normalize();

            if (TryFindBestMeleeTarget(playerCenter, forwardFlat, out bestTarget, out bestPoint))
                bestDistance = Vector3.Distance(origin, bestPoint);
        }

        if (bestTarget == null || bestDistance > nearestBlockerDistance + 0.05f)
            return false;

        if (!TryDamageTargetFromPoint(bestTarget, attackDamage, origin))
            return false;

        Vector3 hitDirection = bestTarget.position - transform.position;
        hitDirection.y = 0f;
        if (hitDirection.sqrMagnitude < 0.001f)
            hitDirection = forward;
        ApplyHitReaction(bestTarget, hitDirection.normalized);
        HitTarget(bestPoint);
        cameraKickTarget = -1.2f;
        return true;
    }

    private bool IsMeleeBlocker(Collider col)
    {
        if (col == null || staticObstacleMask.value == 0)
            return false;

        int layerMask = 1 << col.gameObject.layer;
        return (staticObstacleMask.value & layerMask) != 0;
    }

    private void AttackMelee()
    {
        // Deterministic melee: one non-alloc sphere at the weapon impact point,
        // restricted to the Hittable layer only.

        Vector3 playerCenter = transform.position + Vector3.up * 1.0f;

        // Flatten the player's forward vector to the horizontal plane so the
        // cone check ignores vertical aim and stays consistent on slopes.
        Vector3 forwardFlat = transform.forward;
        forwardFlat.y = 0f;
        if (forwardFlat.sqrMagnitude < 0.0001f) return;
        forwardFlat.Normalize();

        // CharacterController / NavMeshAgent movement can update transforms
        // outside the physics step. Sync once so the overlap query sees the
        // latest positions before resolving the attack.
        Physics.SyncTransforms();

        Vector3 impactPoint = GetMeleeImpactPoint(playerCenter, forwardFlat);
        int hitCount = Physics.OverlapSphereNonAlloc(
            impactPoint,
            MeleeImpactRadius,
            meleeOverlapHits,
            ResolveHittableMask(),
            QueryTriggerInteraction.Ignore);

        Transform bestTarget = null;
        Vector3 bestHitPoint = impactPoint;
        float bestDistanceSqr = float.PositiveInfinity;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = meleeOverlapHits[i];
            if (hit == null) continue;
            if (hit.transform == transform || hit.transform.IsChildOf(transform)) continue;

            Transform damageRoot = GetDamageTargetTransform(hit.transform);
            if (damageRoot == null) continue;

            Vector3 closest = hit.ClosestPoint(impactPoint);
            Vector3 toTarget = closest - playerCenter;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > attackDistance * attackDistance) continue;
            if (toTarget.sqrMagnitude > 0.0001f && Vector3.Angle(forwardFlat, toTarget.normalized) > MeleeHitAngle)
                continue;

            float d2 = (closest - impactPoint).sqrMagnitude;
            if (d2 < bestDistanceSqr)
            {
                bestDistanceSqr = d2;
                bestTarget = damageRoot;
                bestHitPoint = closest;
            }
        }

        if (bestTarget != null && TryDamageTarget(bestTarget, attackDamage))
        {
            ApplyHitReaction(bestTarget, forwardFlat);
            HitTarget(bestHitPoint);
        }
    }

    private Vector3 GetMeleeImpactPoint(Vector3 playerCenter, Vector3 forwardFlat)
    {
        if (equippedWeaponObject != null)
        {
            Renderer[] renderers = equippedWeaponObject.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(equippedWeaponObject.transform.position, Vector3.zero);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!hasBounds) { bounds = renderers[i].bounds; hasBounds = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }

            if (hasBounds)
            {
                Vector3 weaponPoint = bounds.center + forwardFlat * Mathf.Min(attackDistance * 0.45f, bounds.extents.magnitude);
                weaponPoint.y = Mathf.Clamp(weaponPoint.y, playerCenter.y - 0.35f, playerCenter.y + 0.45f);
                return weaponPoint;
            }
        }

        return playerCenter + forwardFlat * Mathf.Clamp(attackDistance * 0.65f, 0.75f, attackDistance);
    }

    private bool TryFindBestMeleeTarget(Vector3 playerCenter, Vector3 forwardFlat,
        out Transform bestTarget, out Vector3 bestHitPoint)
    {
        bestTarget = null;
        bestHitPoint = playerCenter;

        float capsuleRadius = Mathf.Max(0.1f, attackRadius);
        float capsuleReach = Mathf.Max(0f, attackDistance - capsuleRadius);
        Vector3 capsuleEnd = playerCenter + forwardFlat * capsuleReach;

        int hitCount = Physics.OverlapCapsuleNonAlloc(
            playerCenter,
            capsuleEnd,
            capsuleRadius,
            meleeOverlapHits,
            resolvedAttackMask,
            QueryTriggerInteraction.Collide);

        float bestDistanceSqr = float.PositiveInfinity;
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = meleeOverlapHits[i];
            if (hit == null) continue;

            Transform hitTransform = hit.transform;
            if (hitTransform == transform || hitTransform.IsChildOf(transform))
                continue;

            Transform damageRoot = GetDamageTargetTransform(hitTransform);
            if (damageRoot == null) continue;

            Vector3 closestPoint = hit.ClosestPoint(playerCenter);
            Vector3 toTarget = closestPoint - playerCenter;
            toTarget.y = 0f;
            float horizontalDistanceSqr = toTarget.sqrMagnitude;

            if (horizontalDistanceSqr > attackDistance * attackDistance)
                continue;

            if (horizontalDistanceSqr >= 0.0001f)
            {
                float horizontalDistance = Mathf.Sqrt(horizontalDistanceSqr);

                // Let truly point-blank overlaps through even if the target's
                // root pivot is offset or partially beside the player.
                if (horizontalDistance > capsuleRadius * 0.5f)
                {
                    float angle = Vector3.Angle(forwardFlat, toTarget / horizontalDistance);
                    if (angle > MeleeHitAngle) continue;
                }
            }

            if (horizontalDistanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = horizontalDistanceSqr;
                bestTarget = damageRoot;
                bestHitPoint = closestPoint;
            }
        }

        return bestTarget != null;
    }

    // Single, single-target, single-hit precision fallback.
    // • Fires ONE forward sphere cast from the player's chest
    // • Starts just ahead of the player so self-overlap cannot block it
    // • Re-validates the forward-cone constraint
    // • Will damage at most ONE enemy and never chains
    private void TryPrecisionSweepFallback(Vector3 playerCenter, Vector3 forwardFlat)
    {
        float sweepRadius = Mathf.Clamp(attackRadius * 0.35f, 0.12f, 0.45f);
        float sweepDistance = Mathf.Max(0.05f, attackDistance - sweepRadius);
        Vector3 sweepOrigin = playerCenter + forwardFlat * sweepRadius;

        if (!Physics.SphereCast(
                sweepOrigin,
                sweepRadius,
                forwardFlat,
                out RaycastHit rayHit,
                sweepDistance,
                resolvedAttackMask,
                QueryTriggerInteraction.Collide))
            return;

        if (rayHit.transform == transform || rayHit.transform.IsChildOf(transform))
            return;

        Transform damageRoot = GetDamageTargetTransform(rayHit.transform);
        if (damageRoot == null) return;

        Vector3 targetPoint = rayHit.collider != null
            ? rayHit.collider.ClosestPoint(playerCenter)
            : rayHit.point;

        Vector3 toHit = targetPoint - playerCenter;
        toHit.y = 0f;
        if (toHit.sqrMagnitude >= 0.0001f)
        {
            if (Vector3.Angle(forwardFlat, toHit.normalized) > MeleeHitAngle) return;
        }

        if (toHit.sqrMagnitude > attackDistance * attackDistance) return;

        if (TryDamageTarget(damageRoot, attackDamage))
        {
            ApplyHitReaction(damageRoot, forwardFlat);
            HitTarget(rayHit.point);
        }
    }

    private static float HorizontalSqrDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private Transform GetDamageTargetTransform(Transform target)
    {
        if (target == null) return null;

        IDamageable damageable = target.GetComponentInParent<IDamageable>();
        if (damageable != null && damageable.gameObject != gameObject && damageable.IsAlive)
            return damageable.transform;

        Actor actor = target.GetComponentInParent<Actor>();
        if (actor != null && actor.gameObject != gameObject)
            return actor.transform;

        return null;
    }

    private bool TryDamageTarget(Transform target, int damage)
    {
        if (target == null) return false;

        if (TryDamageNetworkPlayer(target, damage))
            return true;

        IDamageable damageable = target.GetComponentInParent<IDamageable>();
        if (damageable != null && damageable.gameObject != gameObject && damageable.IsAlive)
        {
            if (DamageOcclusion.IsBlocked(gameObject, damageable.gameObject))
                return false;
            damageable.ReceiveDamage(damage, gameObject);
            return true;
        }

        Actor actor = target.GetComponentInParent<Actor>();
        if (actor != null && actor.gameObject != gameObject)
        {
            actor.TakeDamage(damage);
            return true;
        }

        return false;
    }

    private bool TryDamageTargetFromPoint(Transform target, int damage, Vector3 attackOriginWorld)
    {
        if (target == null) return false;

        if (TryDamageNetworkPlayer(target, damage))
            return true;

        EnemyController enemy = target.GetComponentInParent<EnemyController>();
        if (enemy != null && enemy.gameObject != gameObject && enemy.IsAlive)
        {
            bool blocked = DamageOcclusion.IsBlockedFromPoint(gameObject, enemy.gameObject, attackOriginWorld);
            if (CombatDebug.Enabled)
                CombatDebug.Log($"blockedByWall={blocked}");

            if (blocked)
                return false;

            int appliedDamage = Mathf.Max(1, damage);
            if (GameManager.Instance != null)
            {
                int hitsToKill = Mathf.Max(1, GameManager.Instance.GetEnemyHitsToKillByPlayer());
                appliedDamage = Mathf.Max(1, Mathf.CeilToInt((float)enemy.maxHealth / hitsToKill));
            }

            int healthBefore = enemy.CurrentHealth;
            if (CombatDebug.Enabled)
                CombatDebug.Log($"hit target={enemy.gameObject.name}");

            enemy.TakeDamage(appliedDamage, byPlayer: true);

            if (CombatDebug.Enabled)
                CombatDebug.Log(
                    $"applyingDamage amount={appliedDamage} target={enemy.gameObject.name} healthBefore={healthBefore} healthAfter={enemy.CurrentHealth}");
            return true;
        }

        IDamageable damageable = target.GetComponentInParent<IDamageable>();
        if (damageable != null && damageable.gameObject != gameObject && damageable.IsAlive)
        {
            if (DamageOcclusion.IsBlockedFromPoint(gameObject, damageable.gameObject, attackOriginWorld))
                return false;
            damageable.ReceiveDamage(damage, gameObject);
            return true;
        }

        Actor actor = target.GetComponentInParent<Actor>();
        if (actor != null && actor.gameObject != gameObject)
        {
            actor.TakeDamage(damage);
            return true;
        }

        return false;
    }

    private void ApplyHitReaction(Transform hitTransform, Vector3 hitDirection)
    {
        if (hitTransform == null) return;

        Vector3 push = new Vector3(hitDirection.x, 0f, hitDirection.z).normalized;
        if (push.sqrMagnitude < 0.001f)
        {
            push = (hitTransform.position - transform.position);
            push.y = 0f;
            push.Normalize();
        }

        Rigidbody rb = hitTransform.GetComponentInParent<Rigidbody>();
        if (rb != null)
        {
            rb.AddForce(push * hitKnockbackForce, ForceMode.VelocityChange);
            return;
        }

        hitTransform.position += push * 0.18f;
    }

    private bool IsRemoteNetworkPlayer()
    {
#if PUN_2_OR_NEWER
        return MultiplayerMode.IsMultiplayer && photonView != null && !photonView.IsMine;
#else
        return false;
#endif
    }

    private void DisableRemotePlayerLocalSystems()
    {
#if PUN_2_OR_NEWER
        if (!IsRemoteNetworkPlayer())
            return;

        StanceTestController stanceTest = GetComponent<StanceTestController>();
        if (stanceTest != null)
            stanceTest.enabled = false;

        LowProfileGroundDebugger groundDbg = GetComponent<LowProfileGroundDebugger>();
        if (groundDbg != null)
            groundDbg.enabled = false;

        if (firstPersonCam != null)
            firstPersonCam.gameObject.SetActive(false);
        if (runtimeThirdPersonCamera != null)
            runtimeThirdPersonCamera.gameObject.SetActive(false);
        if (thirdPersonCam != null)
            thirdPersonCam.gameObject.SetActive(false);

        Camera[] cameras = GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
            if (cameras[i] != null) cameras[i].enabled = false;

        AudioListener[] listeners = GetComponentsInChildren<AudioListener>(true);
        for (int i = 0; i < listeners.Length; i++)
            if (listeners[i] != null) listeners[i].enabled = false;

        Debug.Log($"[MPDebug] remote camera disabled ({gameObject.name})");
#endif
    }

    private bool TryDamageNetworkPlayer(Transform target, int damage)
    {
#if PUN_2_OR_NEWER
        if (!MultiplayerMode.IsMultiplayer || photonView == null || !photonView.IsMine)
            return false;

        PhotonView targetView = target != null ? target.GetComponentInParent<PhotonView>() : null;
        if (targetView == null || targetView == photonView)
            return false;

        NetworkPlayerSync sync = targetView.GetComponent<NetworkPlayerSync>();
        if (sync == null)
            return false;

        sync.ApplyDamageToNetworkPlayer(damage, gameObject);
        return true;
#else
        return false;
#endif
    }

    private void SendNetworkAttackVisual()
    {
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer && photonView != null && photonView.IsMine)
            photonView.RPC(nameof(RpcPlayRemoteAttackVisual), RpcTarget.Others);
#endif
    }

#if PUN_2_OR_NEWER
    [PunRPC]
    private void RpcPlayRemoteAttackVisual()
    {
        isAttacking = true;
        attackResetTimer = AttackResetTime;
        attackFailsafeTimer = AttackFailsafeDuration;

        Animator activeAnimator = GetActiveAnimator();
        if (activeAnimator != null)
            AnimFireTrigger(activeAnimator, "Attack");

        if (audioSource != null && swordSwing != null)
            audioSource.PlayOneShot(swordSwing, AudioSettingsRuntime.ScaledSfx(0.8f));
    }
#endif

    public void ApplyNetworkWeaponState(int weaponLevel, string weaponName)
    {
        if (!MultiplayerMode.IsMultiplayer)
            return;

        if (!string.IsNullOrWhiteSpace(weaponName))
            equippedWeaponName = weaponName;

        if (weaponLevel > 0 && weaponLevel != equippedWeaponLevel)
            EquipWeaponForLevel(weaponLevel);
    }

    /// <summary>
    /// Photon local spawn: same equip path as single-player after the third-person
    /// body and hand socket exist (LevelBuilder does not run in multiplayer scenes).
    /// </summary>
    public void ForceEquipLevelWeaponForMultiplayer()
    {
        if (!MultiplayerMode.IsMultiplayer)
            return;

        // Source of truth: MultiplayerRuntimeConfig.GetSelectedLevel(). Reads
        // the room's KeyLevel custom property, falls back to the configured
        // default. Do NOT read GameManager.currentLevel — that is the SP
        // Continue save and leaked into MP loadouts (HUD said L8 Hammer).
        int level = MultiplayerRuntimeConfig.GetSelectedLevel();
        string weaponName = MultiplayerRuntimeConfig.GetSelectedWeaponName();

        int actor = -1;
#if PUN_2_OR_NEWER
        if (photonView != null && photonView.Owner != null) actor = photonView.Owner.ActorNumber;
#endif
        Debug.Log($"[MPWeapon] equipped level={level} weapon={weaponName} actor={actor} local=true");

        RefreshGameplayPreferences();
        EnsureThirdPersonBody();

        if (equippedWeaponObject != null)
        {
            Destroy(equippedWeaponObject);
            equippedWeaponObject = null;
        }

        equippedWeaponLevel = -1;
        EquipWeaponForLevel(level);

        string socketName = "none";
        bool weaponActive = false;
        if (equippedWeaponObject != null)
        {
            Transform socket = equippedWeaponObject.transform.parent;
            socketName = socket != null ? socket.name : equippedWeaponObject.name;
            weaponActive = equippedWeaponObject.activeInHierarchy;
        }

        Debug.Log($"[MPWeapon] equipped parent={socketName} active={weaponActive}");
    }

    private void HitTarget(Vector3 pos)
    {
        if (audioSource != null && hitSound != null)
        {
            audioSource.pitch = 1f;
            audioSource.PlayOneShot(hitSound, AudioSettingsRuntime.ScaledSfx(1f));
        }

        if (hitEffect != null)
        {
            GameObject fx = Instantiate(hitEffect, pos, Quaternion.identity);
            Destroy(fx, 20f);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ANIMATION
    // ════════════════════════════════════════════════════════════════════════

    private void CacheAnimatorParameters()
    {
        _animParameterHashes = new HashSet<int>();

        // ✅ FIX: Use GetActiveAnimator() instead of the cached field, so we
        //         always cache against the live animator even if called early.
        Animator activeAnim = thirdPersonBody != null
            ? thirdPersonBody.GetComponentInChildren<Animator>(true)
            : animator;

        if (activeAnim == null) return;

        foreach (AnimatorControllerParameter p in activeAnim.parameters)
            _animParameterHashes.Add(p.nameHash);
    }

    private void UpdateAnimatorParameters()
    {
        Animator anim = GetActiveAnimator();
        if (anim == null) return;

        ConfigureAnimatorBinding(anim, forceControllerAssignment: false);
        if (anim.runtimeAnimatorController == null) return;

        // Use input magnitude so Speed snaps to 0 immediately when keys are released,
        // preventing residual physics velocity keeping the Run animation active while standing still.
        float normalizedSpeed = moveInputSmoothed.sqrMagnitude > 0.01f
            ? Mathf.Clamp01(moveInputSmoothed.magnitude)
            : 0f;

        // ── Locomotion (Float) ──────────────────────────────────────────────
        bool droveSpeedParameter = AnimSetFloat(anim, "Speed", normalizedSpeed, 0.1f);
        if (!droveSpeedParameter)
            ForceLocomotionState(anim, normalizedSpeed);

        // ── Bool params (Player Controller.controller style) ────────────────
        AnimSetBool(anim, "IsAttacking", isAttacking);
        AnimSetBool(anim, "IsGrounded",  isGrounded || IsGroundedForJump());
        AnimSetBool(anim, "IsSprinting", isSprinting);
        AnimSetBool(anim, "IsProne", isProne);
        AnimSetBool(anim, "IsSliding", isSliding);

        // ── Trigger params (CrosbyAnimator style) ───────────────────────────
        // Fire Attack trigger on the frame isAttacking becomes true.
        // Also fire the category-specific trigger (Attack_Light, Attack_Sword,
        // ...) when present on the controller. Falls back silently to the
        // generic Attack trigger if no category trigger is wired yet.
        if (isAttacking && !_wasAttackingLastFrame)
        {
            AnimFireTrigger(anim, WeaponAnimationCategories.GenericAttackTrigger);

            int weaponLevel = GetEquippedWeaponLevel();
            WeaponAnimationCategory category = WeaponAnimationCategories.ForLevel(weaponLevel);
            string categoryTrigger = WeaponAnimationCategories.GetAttackTrigger(category);
            if (!string.IsNullOrEmpty(categoryTrigger)
                && categoryTrigger != WeaponAnimationCategories.GenericAttackTrigger)
            {
                AnimFireTrigger(anim, categoryTrigger);
            }
        }
        _wasAttackingLastFrame = isAttacking;
    }
    private bool _wasAttackingLastFrame;

    // ── Direct, timing-safe param helpers ────────────────────────────────────
    // Query animator.parameters every call — no HashSet that can be stale.

    private static bool AnimSetFloat(Animator anim, string name, float value, float damp = 0f)
    {
        foreach (AnimatorControllerParameter p in anim.parameters)
        {
            if (p.name != name || p.type != AnimatorControllerParameterType.Float) continue;
            if (damp > 0f) anim.SetFloat(name, value, damp, Time.deltaTime);
            else           anim.SetFloat(name, value);
            return true;
        }
        return false;
    }

    private static bool AnimSetBool(Animator anim, string name, bool value)
    {
        foreach (AnimatorControllerParameter p in anim.parameters)
        {
            if (p.name != name || p.type != AnimatorControllerParameterType.Bool) continue;
            anim.SetBool(name, value);
            return true;
        }
        return false;
    }

    private static bool AnimFireTrigger(Animator anim, string name)
    {
        foreach (AnimatorControllerParameter p in anim.parameters)
        {
            if (p.name != name || p.type != AnimatorControllerParameterType.Trigger) continue;
            anim.SetTrigger(name);
            return true;
        }
        return false;
    }

    private static void ForceLocomotionState(Animator anim, float normalizedSpeed)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return;

        string stateName = normalizedSpeed > 0.05f ? "Walk" : "Idle";
        string fullStateName = $"Base Layer.{stateName}";
        int stateHash = Animator.StringToHash(fullStateName);

        if (!anim.HasState(0, stateHash)) return;

        AnimatorStateInfo currentState = anim.GetCurrentAnimatorStateInfo(0);
        if (currentState.fullPathHash == stateHash) return;

        anim.CrossFadeInFixedTime(fullStateName, 0.1f, 0);
    }

    // Keep old helpers so no compile errors in code that still calls them
    private bool HasAnimatorParameter(int hash)
        => _animParameterHashes != null && _animParameterHashes.Contains(hash);

    private void SetAnimatorFloat(int hash, float value, float dampTime = 0f)
    {
        Animator activeAnim = GetActiveAnimator();
        if (activeAnim == null || !HasAnimatorParameter(hash)) return;
        if (dampTime > 0f) activeAnim.SetFloat(hash, value, dampTime, Time.deltaTime);
        else               activeAnim.SetFloat(hash, value);
    }

    private void SetAnimatorBool(int hash, bool value)
    {
        Animator activeAnim = GetActiveAnimator();
        if (activeAnim == null || !HasAnimatorParameter(hash)) return;
        activeAnim.SetBool(hash, value);
    }

    private void ConfigureAnimatorBinding(Animator targetAnimator, bool forceControllerAssignment)
    {
        if (targetAnimator == null) return;

        targetAnimator.applyRootMotion = false;

        // ── Resolve controller ────────────────────────────────────────────────
        // Priority 1: Inspector-assigned playerAnimatorController
        // Priority 2: Auto-load CrosbyAnimator (same controller used by all enemies)
        RuntimeAnimatorController resolvedCtrl = playerAnimatorController;
        if (resolvedCtrl == null)
            resolvedCtrl = Resources.Load<RuntimeAnimatorController>("Enemy/CrosbyAnimator");

        if (resolvedCtrl != null &&
            (forceControllerAssignment || targetAnimator.runtimeAnimatorController == null))
        {
            targetAnimator.runtimeAnimatorController = resolvedCtrl;
        }

        // ── Resolve avatar ────────────────────────────────────────────────────
        if (playerAvatar != null &&
            (forceControllerAssignment || targetAnimator.avatar == null) &&
            targetAnimator.avatar != playerAvatar)
        {
            targetAnimator.avatar = playerAvatar;
        }

        // ── Final safety check (only warn once, not every frame) ─────────────
        if (targetAnimator.runtimeAnimatorController == null && !_animatorMissingWarned)
        {
            _animatorMissingWarned = true;
            Debug.LogWarning(
                "[PlayerController] No animator controller could be resolved. " +
                "Assign one to 'playerAnimatorController' in the Inspector, or ensure " +
                "Resources/Enemy/CrosbyAnimator exists.",
                targetAnimator);
        }
    }
    private bool _animatorMissingWarned;

    // ════════════════════════════════════════════════════════════════════════
    //  ARENA BOUNDARIES
    // ════════════════════════════════════════════════════════════════════════

    private void ClampInsideArena()
    {
        // Arena boundary disabled — the map is open for the player to explore
        // freely.  The CharacterController, NavMesh floor, and level geometry
        // are sufficient to keep the player in the world without an artificial
        // invisible circular wall.  The hardcoded arenaFloorHeight Y-clamp
        // that was here also mismatched real map geometry and contributed to
        // the player being snapped underground near the boundary.
    }

    private float GetGroundedRootY()
    {
        if (controller == null)
            return arenaFloorHeight + 1f;

        return arenaFloorHeight - controller.center.y + (controller.height * 0.5f);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PERSPECTIVE MANAGEMENT
    // ════════════════════════════════════════════════════════════════════════

    private void TogglePerspective()
    {
        // Third-person only.
        if (GameManager.Instance != null)
            GameManager.Instance.SetPerspectiveMode(GameManager.PerspectiveMode.ThirdPerson);
        else
        {
            PlayerPrefs.SetInt("PerspectiveMode", (int)GameManager.PerspectiveMode.ThirdPerson);
            PlayerPrefs.Save();
        }

        RefreshGameplayPreferences();
    }

    public void RefreshGameplayPreferences()
    {
        ApplyPerspectivePreference();
        if (thirdPersonBody != null && isThirdPersonActive)
            thirdPersonBody.SetActive(true);
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor Game tab preview — wires third-person camera/body without entering Play Mode.
    /// </summary>
    public void PrepareEditorGameViewPreview()
    {
        if (Application.isPlaying)
            return;

        if (controller == null)
            controller = GetComponent<CharacterController>();

        if (firstPersonCam == null)
            firstPersonCam = GetComponentInChildren<Camera>(true);

        isThirdPersonActive = true;
        EnsureThirdPersonCamera();
        EnsureThirdPersonBody();

        if (firstPersonCam != null)
            firstPersonCam.gameObject.SetActive(false);

        if (runtimeThirdPersonCamera != null)
        {
            runtimeThirdPersonCamera.tag = "MainCamera";
            runtimeThirdPersonCamera.gameObject.SetActive(true);
        }

        if (thirdPersonBody != null)
        {
            thirdPersonBody.SetActive(true);
            SetThirdPersonRenderersVisible(true);
            UpdateBodyBasePosition();
            StabilizeStandingVisualPose();
            thirdPersonBody.transform.localRotation = thirdPersonBodyBaseLocalRotation;
        }

        Vector3 euler = transform.eulerAngles;
        transform.eulerAngles = new Vector3(0f, euler.y, 0f);
    }
#endif

    private void ApplyPerspectivePreference()
    {
        // Third-person only: ignore persisted preference and force the correct camera.
        if (GameManager.Instance != null)
            GameManager.Instance.SetPerspectiveMode(GameManager.PerspectiveMode.ThirdPerson);
        else
        {
            PlayerPrefs.SetInt("PerspectiveMode", (int)GameManager.PerspectiveMode.ThirdPerson);
            PlayerPrefs.Save();
        }

        EnableThirdPersonView();
    }

    private void EnableFirstPersonView()
    {
        isThirdPersonActive = false;
        cameraPitch = 0f;

        if (firstPersonCam == null)
            firstPersonCam = GetComponentInChildren<Camera>(true);

        if (firstPersonCam != null)
        {
            // Snap to a stable head height (prevents "inside floor/body" camera on some prefabs).
            float headY = controller != null ? Mathf.Clamp(controller.height - 0.18f, 1.35f, 1.85f) : 1.65f;
            firstPersonLocalPos = new Vector3(firstPersonLocalPos.x, headY, Mathf.Max(firstPersonLocalPos.z, 0.09f));
            firstPersonCam.transform.localPosition = firstPersonLocalPos;
            firstPersonCam.transform.localRotation = firstPersonLocalRot;
            // Keep a safe near clip to avoid seeing inside meshes.
            firstPersonCam.nearClipPlane = 0.1f;
            firstPersonCam.tag = "MainCamera";
            firstPersonCam.gameObject.SetActive(true);

            // If FPSCameraRig is present, let it take over anchor + culling-mask setup.
            FPSCameraRig rig = firstPersonCam.GetComponent<FPSCameraRig>();
            if (rig == null)
                rig = firstPersonCam.gameObject.AddComponent<FPSCameraRig>();
            rig.playerRoot = transform;
            // Forward the head bone if the body is already spawned.
            if (thirdPersonBody != null)
            {
                Transform headBone = FPSCameraRig_FindHeadBone(thirdPersonBody.transform);
                if (headBone != null) rig.SetHeadBone(headBone);
            }
        }

        if (runtimeThirdPersonCamera != null)
        {
            runtimeThirdPersonCamera.tag = "Untagged";
            runtimeThirdPersonCamera.gameObject.SetActive(false);
        }

        // Keep the third-person rig ACTIVE so weapon scripts/coroutines keep working,
        // but hide its renderers so FPS view stays clean.
        if (thirdPersonBody != null)
        {
            thirdPersonBody.SetActive(true);
            SetThirdPersonRenderersVisible(false);
        }

        SetFirstPersonRenderersVisible(true);
        RefreshFirstPersonWeaponModel(equippedWeaponLevel > 0 ? equippedWeaponLevel : (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1));
    }

    private void EnableThirdPersonView()
    {
        isThirdPersonActive = true;
        EnsureThirdPersonCamera();
        EnsureThirdPersonBody();

        if (firstPersonCam != null)
        {
            firstPersonCam.tag = "Untagged";
            firstPersonCam.gameObject.SetActive(false);
        }

        if (runtimeThirdPersonCamera != null)
        {
            runtimeThirdPersonCamera.tag = "MainCamera";
            runtimeThirdPersonCamera.gameObject.SetActive(true);
        }

        SetFirstPersonRenderersVisible(false);
        if (thirdPersonBody != null)
        {
            thirdPersonBody.SetActive(true);
            SetThirdPersonRenderersVisible(true);
        }

        ClearFirstPersonKnifeVisual();
    }

    private void SetThirdPersonRenderersVisible(bool visible)
    {
        if (thirdPersonBody == null) return;
        Renderer[] rs = thirdPersonBody.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] != null)
                rs[i].enabled = visible;
        }
    }

    private void EnsureThirdPersonCamera()
    {
        // Never create a camera for a remote network player.
        if (IsRemoteNetworkPlayer()) return;

        if (runtimeThirdPersonCamera != null)
        {
            thirdPersonCam = runtimeThirdPersonCamera;
            ApplyThirdPersonCameraSettings(runtimeThirdPersonCamera.GetComponent<CameraController>(), runtimeThirdPersonCamera);
            return;
        }

        CameraController[] existingFollows = Object.FindObjectsByType<CameraController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.InstanceID);

        for (int i = 0; i < existingFollows.Length; i++)
        {
            CameraController existingFollow = existingFollows[i];
            if (existingFollow == null)
                continue;

#if PUN_2_OR_NEWER
            // Skip cameras that belong to a different (remote) Photon player —
            // reusing them would orphan that player's camera and could tag a
            // remote camera as "MainCamera" on the local client.
            Photon.Pun.PhotonView camPv = existingFollow.GetComponentInParent<Photon.Pun.PhotonView>();
            if (camPv != null && !camPv.IsMine) continue;

            // Skip cameras already adopted by a different local PlayerController.
            PlayerController camOwner = existingFollow.GetComponentInParent<PlayerController>();
            if (camOwner != null && camOwner != this) continue;
#endif

            runtimeThirdPersonCamera = existingFollow.GetComponent<Camera>();
            if (runtimeThirdPersonCamera != null)
            {
                thirdPersonCam = runtimeThirdPersonCamera;
                existingFollow.target = transform;
                // Must be MainCamera so Camera.main / interaction rays match gameplay.
                runtimeThirdPersonCamera.tag = "MainCamera";
                ApplyThirdPersonCameraSettings(existingFollow, runtimeThirdPersonCamera);
                RefreshCachedGameplayCamera();
                DestroyDuplicateThirdPersonCameras(existingFollows, i);
                return;
            }
        }

        GameObject camObj = new GameObject(RuntimeThirdPersonCameraName);
        runtimeThirdPersonCamera = camObj.AddComponent<Camera>();

        // Borrow settings from the first-person camera if it still exists,
        // otherwise use sensible defaults (first-person cam may be disabled).
        if (firstPersonCam != null)
        {
            runtimeThirdPersonCamera.fieldOfView     = Mathf.Max(firstPersonCam.fieldOfView, 70f);
            runtimeThirdPersonCamera.nearClipPlane   = firstPersonCam.nearClipPlane;
            runtimeThirdPersonCamera.farClipPlane    = firstPersonCam.farClipPlane;
            runtimeThirdPersonCamera.clearFlags      = firstPersonCam.clearFlags;
            runtimeThirdPersonCamera.backgroundColor = firstPersonCam.backgroundColor;
        }
        else
        {
            runtimeThirdPersonCamera.fieldOfView     = 70f;
            runtimeThirdPersonCamera.nearClipPlane   = 0.08f;
            runtimeThirdPersonCamera.farClipPlane    = 1000f;
            runtimeThirdPersonCamera.clearFlags      = CameraClearFlags.Skybox;
        }
        runtimeThirdPersonCamera.tag = "MainCamera";
        camObj.AddComponent<AudioListener>();

        // ── Orbit camera (self-contained mouse input + wall collision) ────────
        // ThirdPersonOrbitCamera owns mouse reading so the camera never freezes
        // when isThirdPersonActive is toggled or the CameraController link breaks.
        ThirdPersonOrbitCamera orbitCam = camObj.AddComponent<ThirdPersonOrbitCamera>();
        orbitCam.target               = transform;
        orbitCam.sensitivityX         = sensitivity / 30f;   // normalise from PlayerController scale
        orbitCam.sensitivityY         = sensitivity / 30f;
        orbitCam.pivotHeightOffset    = 1.45f;
        orbitCam.shoulderOffset       = 0.48f;
        orbitCam.defaultDistance      = 4.85f;
        orbitCam.minDistance          = 1.25f;
        orbitCam.pitchMin             = ThirdPersonMinPitch;
        orbitCam.pitchMax             = ThirdPersonMaxPitch;
        orbitCam.pivotSmoothTime      = 0.08f;
        orbitCam.distanceSmoothTime   = 0.12f;
        orbitCam.enableCollision      = true;
        orbitCam.collisionRadius      = 0.25f;
        orbitCam.wallPadding          = 0.18f;
        orbitCam.lockCursor           = true;

        // Legacy CameraController kept for API compatibility (SnapToTarget, zoom calls, etc.)
        // but with collision disabled so only ThirdPersonOrbitCamera positions the camera.
        CameraController follow = camObj.AddComponent<CameraController>();
        if (follow != null)
        {
            ApplyThirdPersonCameraSettings(follow, runtimeThirdPersonCamera);
            follow.enabled = false;   // ThirdPersonOrbitCamera drives position instead
        }

        thirdPersonCam = runtimeThirdPersonCamera;
        RefreshCachedGameplayCamera();
    }

    private void ApplyThirdPersonCameraSettings(CameraController follow, Camera cam)
    {
        if (follow == null) return;

        follow.target = transform;
        follow.offset = new Vector3(0.55f, 1.45f, -4.85f);
        follow.smoothSpeed = 60f;
        follow.lookHeight = 1.55f;
        follow.lookTargetLocalOffset = Vector3.zero;
        follow.lookTargetSmoothSpeed = 60f;
        follow.minPitch = ThirdPersonMinPitch;
        follow.maxPitch = ThirdPersonMaxPitch;
        follow.pitch = Mathf.Clamp(cameraPitch, ThirdPersonMinPitch, ThirdPersonMaxPitch);
        follow.enableCollision = true;
        follow.minDistance = 1.25f;
        follow.minHeightAboveGround = 0.55f;
        follow.collisionRadius = 0.25f;
        follow.wallPadding = 0.18f;
        follow.pullInSpeed = 0.035f;
        follow.recoverySmoothTime = 0.12f;
        follow.closeDistanceFailsafe = 0.5f;
        follow.closeSpaceHeightBoost = 0.35f;
        follow.closeSpaceFieldOfView = 76f;
        follow.defaultFieldOfView = 68f;

        if (cam != null)
        {
            cam.fieldOfView = 68f;
            cam.nearClipPlane = 0.08f;
        }
    }

    private static void DestroyDuplicateThirdPersonCameras(CameraController[] controllers, int keepIndex)
    {
        for (int i = controllers.Length - 1; i >= 0; i--)
        {
            if (i == keepIndex || controllers[i] == null)
                continue;

            GameObject cameraObject = controllers[i].gameObject;
            if (cameraObject != null && cameraObject.name == RuntimeThirdPersonCameraName)
                Destroy(cameraObject);
        }
    }

    private void SetFirstPersonRenderersVisible(bool visible)
    {
        if (firstPersonRenderers == null) return;
        for (int i = 0; i < firstPersonRenderers.Length; i++)
        {
            if (firstPersonRenderers[i] != null)
                firstPersonRenderers[i].enabled = visible;
        }

        if (firstPersonWeaponMeshRenderer != null)
            firstPersonWeaponMeshRenderer.enabled = visible && firstPersonKnifeInstance == null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  THIRD-PERSON BODY
    // ════════════════════════════════════════════════════════════════════════

    private void EnsureThirdPersonBody()
    {
        // If the Inspector-assigned (or previously spawned) body is already
        // present we still need to guarantee the AnimationEventSink stubs are
        // attached — without this, the Player prefab's baked-in Crosby body
        // played UnarmedLightAttack1 with no receiver because the sink was only
        // added on the Resources.Load instantiate path below.
        if (thirdPersonBody != null)
        {
            EnsureAnimationEventSink(thirdPersonBody);
            return;
        }

        GameObject roninBodyPrefab = Resources.Load<GameObject>("Player/Ronin/source/Ronin");
        if (roninBodyPrefab != null)
        {
            thirdPersonBody = Instantiate(roninBodyPrefab, transform);
            thirdPersonBody.name = "ThirdPersonBody";
            thirdPersonBody.transform.localPosition = Vector3.zero;
            thirdPersonBody.transform.localRotation = Quaternion.identity;
            thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
            thirdPersonBodyBaseLocalRotation = thirdPersonBody.transform.localRotation;
            NormalizeBodyScale(thirdPersonBody, 1.8f);

            Animator roninAnimator = thirdPersonBody.GetComponentInChildren<Animator>(true);
            if (roninAnimator != null)
            {
                ConfigureAnimatorBinding(roninAnimator, forceControllerAssignment: true);
                EnsureAnimationEventSink(roninAnimator.gameObject);

                // ✅ FIX: Update cached animator field immediately after body spawns
                animator = roninAnimator;
            }

            CacheAnimatorParameters();

            // Notify FPSCameraRig (if present) of the new head bone.
            NotifyFPSCameraRigHeadBone(thirdPersonBody.transform);
            return;
        }

        GameObject dragonSoulsBodyPrefab = Resources.Load<GameObject>("Player/DragonSoulsThirdPersonBody");
        if (dragonSoulsBodyPrefab != null)
        {
            thirdPersonBody = Instantiate(dragonSoulsBodyPrefab, transform);
            thirdPersonBody.name = "ThirdPersonBody";
            thirdPersonBody.transform.localPosition = Vector3.zero;
            thirdPersonBody.transform.localRotation = Quaternion.identity;
            thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
            thirdPersonBodyBaseLocalRotation = thirdPersonBody.transform.localRotation;
            NormalizeBodyScale(thirdPersonBody, 1.8f);

            Animator bodyAnimator = thirdPersonBody.GetComponentInChildren<Animator>(true);
            if (bodyAnimator != null)
            {
                ConfigureAnimatorBinding(bodyAnimator, forceControllerAssignment: true);
                EnsureAnimationEventSink(bodyAnimator.gameObject);

                // ✅ FIX: Update cached animator field immediately after body spawns
                animator = bodyAnimator;
            }

            CacheAnimatorParameters();
            return;
        }

        GameObject knightPrefab = Resources.Load<GameObject>("ThirdPersonKnight/Paladin WProp J Nordstrom");
        if (knightPrefab != null)
        {
            thirdPersonBody = Instantiate(knightPrefab, transform);
            thirdPersonBody.name = "ThirdPersonBody";
            thirdPersonBody.transform.localPosition = Vector3.zero;
            thirdPersonBody.transform.localRotation = Quaternion.identity;
            thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
            thirdPersonBodyBaseLocalRotation = thirdPersonBody.transform.localRotation;
            thirdPersonBody.transform.localScale = Vector3.one;

            HideKnightWeaponProp(thirdPersonBody);

            Animator importedAnimator = thirdPersonBody.GetComponentInChildren<Animator>(true);
            if (importedAnimator != null)
            {
                ConfigureAnimatorBinding(importedAnimator, forceControllerAssignment: true);
                EnsureAnimationEventSink(importedAnimator.gameObject);

                // ✅ FIX: Update cached animator field immediately after body spawns
                animator = importedAnimator;
            }

            CacheAnimatorParameters();
            return;
        }

        thirdPersonBody = new GameObject("ThirdPersonBody");
        thirdPersonBody.transform.SetParent(transform, false);
        thirdPersonBody.transform.localPosition = Vector3.zero;
        thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
        thirdPersonBodyBaseLocalRotation = thirdPersonBody.transform.localRotation;
    }

    private void ApplySkinMaterial(GameObject body)
    {
        // WeaponFix: preserve prefab-authored character materials.
    }

    /// <summary>
    /// Loads "RoninTexture" from Resources/Textures and applies it to any blank/
    /// missing material slot on the character body so the player is never a
    /// "white statue".  Slots that already have a real texture are left alone.
    /// Place your texture at:  Assets/_Shared/Imports/Resources/Player/Ronin/textures/RoninTexture.png
    /// </summary>
    private static void EnsureProperMaterial(GameObject body)
    {
        // Body colour is handled by <see cref="ApplyPlayerBodyBlackTint"/> (runtime-safe; preserves weapon materials).
    }

    /// <summary>
    /// Forces all third-person body mesh materials to black. Skips the equipped weapon subtree
    /// so weapon visuals stay prefab-accurate. Safe to call after weapon swaps / level loads.
    /// </summary>
    private void ApplyPlayerBodyBlackTint()
    {
        if (thirdPersonBody == null) return;

        Renderer[] renderers = thirdPersonBody.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            if (r is ParticleSystemRenderer)
                continue;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                continue;
            if (IsEquippedWeaponRenderer(r))
                continue;

            Material[] mats = r.materials;
            if (mats == null || mats.Length == 0) continue;

            for (int j = 0; j < mats.Length; j++)
            {
                if (mats[j] == null) continue;
                ApplyBlackToMaterialInstance(mats[j]);
            }

            r.materials = mats;
        }
    }

    private bool IsEquippedWeaponRenderer(Renderer r)
    {
        Transform t = r.transform;
        if (equippedWeaponObject != null && t.IsChildOf(equippedWeaponObject.transform))
            return true;

        return t.GetComponentInParent<WeaponBase>() != null;
    }

    private static void ApplyBlackToMaterialInstance(Material m)
    {
        if (m == null) return;

        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", Color.black);
        if (m.HasProperty("_Color"))
            m.SetColor("_Color", Color.black);
        if (m.HasProperty("_EmissionColor"))
            m.SetColor("_EmissionColor", Color.black);
        if (m.HasProperty("_EmissiveColor"))
            m.SetColor("_EmissiveColor", Color.black);
    }

    /// <summary>
    /// Given a material's name (e.g. "mp_ronin_torso"), tries to find a
    /// matching color texture in known folders (e.g. "mp_ronin_torso_c.tga").
    /// </summary>
    private static Texture2D TryLoadTextureForMaterial(string matName, string[] folders)
    {
        if (string.IsNullOrEmpty(matName)) return null;

        // Strip common Unity suffixes
        string clean = matName.Replace(" (Instance)", "").Replace("_Fix", "").Trim();
        if (string.IsNullOrEmpty(clean)) return null;

        // Try "{folder}{name}_c.tga", "{folder}{name}_c", "{folder}{name}"
        foreach (string folder in folders)
        {
            Texture2D tex;
            tex = Resources.Load<Texture2D>(folder + clean + "_c.tga");  if (tex != null) return tex;
            tex = Resources.Load<Texture2D>(folder + clean + "_c");       if (tex != null) return tex;
            tex = Resources.Load<Texture2D>(folder + clean);              if (tex != null) return tex;
        }
        return null;
    }

    private static Material LoadFirstAvailableMaterial(params string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            Material m = Resources.Load<Material>(paths[i]);
            if (m != null) return m;
        }
        return null;
    }

    private static bool IsBlankMaterial(Material m)
    {
        if (m == null) return true;
        if (m.name.StartsWith("Default-")) return true;
        // Check if shader is missing/pink (URP project with Standard-only mat)
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

    private static void NormalizeBodyScale(GameObject body, float targetHeight)
    {
        if (body == null) return;

        body.transform.localScale = Vector3.one;

        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;
        foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true))
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!any || b.size.y < 0.01f) return;

        float scale = targetHeight / b.size.y;
        body.transform.localScale = Vector3.one * scale;
    }

    /// <summary>
    /// Updates the cached base local position used by all body-offset logic.
    /// </summary>
    public void UpdateBodyBasePosition()
    {
        if (thirdPersonBody != null)
            thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
    }

    /// <summary>
    /// Runs every frame in Update until it succeeds.
    /// Measures actual foot bone positions vs physics ground and shifts
    /// thirdPersonBody so feet are flush — no floating gap.
    /// </summary>
    private bool TryAlignBodyFeet()
    {
        if (thirdPersonBody == null) return false;

        Animator bodyAnim = thirdPersonBody.GetComponentInChildren<Animator>(true);
        if (bodyAnim == null || !bodyAnim.isInitialized) return false;

        // ── 1. Find lowest foot world-Y ───────────────────────────────────────
        float feetWorldY = float.MaxValue;
        bool  hasFeet    = false;

        if (bodyAnim.isHuman)
        {
            Transform lf = bodyAnim.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rf = bodyAnim.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf != null) { feetWorldY = Mathf.Min(feetWorldY, lf.position.y); hasFeet = true; }
            if (rf != null) { feetWorldY = Mathf.Min(feetWorldY, rf.position.y); hasFeet = true; }
        }

        // Fallback: lowest renderer bound if no humanoid bones
        if (!hasFeet)
        {
            foreach (Renderer r in thirdPersonBody.GetComponentsInChildren<Renderer>(true))
            { feetWorldY = Mathf.Min(feetWorldY, r.bounds.min.y); hasFeet = true; }
        }

        if (!hasFeet || feetWorldY >= 9999f) return false;

        // ── 2. Find walkable ground under the capsule ─────────────────────────
        float groundY = transform.position.y + controller.center.y - controller.height * 0.5f;
        if (TryProbeWalkableGround(out RaycastHit hit))
            groundY = hit.point.y;

        // ── 3. Shift body so SOLE of foot (not ankle bone) lands on ground ────
        // The foot bone sits at the ankle, ~8 cm above the shoe sole.
        // We keep that offset so the sole (not the bone) touches the ground.
        const float ankleToSole = 0.08f;   // metres from foot bone to bottom of shoe

        float gap = (feetWorldY - ankleToSole) - groundY;
        if (Mathf.Abs(gap) >= 0.003f)
        {
            Vector3 lp = thirdPersonBody.transform.localPosition;
            lp.y -= gap;
            thirdPersonBody.transform.localPosition = lp;
        }

        // Lock as the new base — all crouch/prone/slide offsets build on this.
        thirdPersonBodyBaseLocalPosition = thirdPersonBody.transform.localPosition;
        Debug.Log($"[PlayerController] Feet aligned: gap={gap:+0.000;-0.000} m (ankleToSole={ankleToSole} m).");
        return true;
    }

    private void EnsureAnimationEventSink(GameObject root)
    {
        if (root == null) return;

        // Ensure stubs are present on the root
        if (root.GetComponent<AnimationEventSink>() == null)
            root.AddComponent<AnimationEventSink>();

        if (root.GetComponent<MeleeAnimationEventSink>() == null)
            root.AddComponent<MeleeAnimationEventSink>();

        // Cover EVERY animator host in the hierarchy, not just the first one
        // (rigs like Crosby ship with multiple Animators across child meshes —
        // attaching to only the first one leaves later hosts without a
        // receiver, which is exactly the "no receiver" error pattern).
        Animator[] animators = root.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;
            GameObject target = a.gameObject;
            if (target == root) continue;

            if (target.GetComponent<AnimationEventSink>() == null)
                target.AddComponent<AnimationEventSink>();

            if (target.GetComponent<MeleeAnimationEventSink>() == null)
                target.AddComponent<MeleeAnimationEventSink>();
        }

        // Belt-and-braces: cover any GO literally named "Crosby" — the engine
        // reports the host GameObject name in the "no receiver" warning, so
        // this guarantees a receiver on exactly that object.
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
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
        // Ensure AnimationEventSink is present to prevent CS0246 event-method errors
        if (thirdPersonBody != null)
            EnsureAnimationEventSink(thirdPersonBody);
    }

    private static void ApplyReadableTint(Material mat, Color tint, float strength)
    {
        if (mat == null) return;

        Color baseColor = Color.white;
        if (mat.HasProperty("_BaseColor")) baseColor = mat.GetColor("_BaseColor");
        else if (mat.HasProperty("_Color")) baseColor = mat.GetColor("_Color");

        Color mixed = Color.Lerp(baseColor, tint, Mathf.Clamp01(strength));
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", mixed);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", mixed);
    }

    private void HideKnightWeaponProp(GameObject knight)
    {
        Transform prop = FindBoneContaining(knight.transform, "Weapon");
        if (prop != null) prop.gameObject.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WEAPON SYSTEM
    // ════════════════════════════════════════════════════════════════════════

    public void EquipWeaponForLevel(int level)
    {
        int gameplayLevel = Mathf.Max(1, level);
        if (GameManager.Instance != null)
            gameplayLevel = Mathf.Clamp(gameplayLevel, 1, GameManager.TotalLevels);

        // Store buffs apply only when the equipped store weapon matches this
        // campaign level. Otherwise we always use the level's mesh + base
        // stats (fixes every level showing e.g. Baseball Bat after buying it).
        WeaponDefinition storeEquipped = null;
        if (SessionManager.Instance != null)
            storeEquipped = SessionManager.Instance.EquippedWeapon;
        bool storeBuffsThisLevel = storeEquipped != null && storeEquipped.Id != "default"
            && storeEquipped.LevelIndex == gameplayLevel;

        if (GameManager.Instance != null)
        {
            equippedWeaponName = GameManager.Instance.GetWeaponNameForLevel(gameplayLevel);
            attackDamage       = Mathf.RoundToInt(GameManager.Instance.GetWeaponDamageForLevel(gameplayLevel));
            attackDistance     = GameManager.Instance.GetWeaponRangeForLevel(gameplayLevel);
            attackSpeed = 1.0f; attackDelay = 0.4f; attackRadius = 1.25f;
        }
        else
        {
            equippedWeaponName = "Combat Knife";
        }

        if (storeBuffsThisLevel)
        {
            equippedWeaponName = storeEquipped.Name;
            attackDamage       = Mathf.Max(1, Mathf.RoundToInt(attackDamage * storeEquipped.DamageMul));
            attackSpeed       *= storeEquipped.AttackSpeedMul;
            attackDelay        = Mathf.Clamp(attackDelay / storeEquipped.AttackSpeedMul, 0.12f, 1.0f);
        }

        if (thirdPersonBody != null)
        {
            if (isThirdPersonActive)
                thirdPersonBody.SetActive(true);

            if (equippedWeaponObject == null || equippedWeaponLevel != gameplayLevel)
                AttachWeaponToHand(thirdPersonBody, gameplayLevel);
            SetupWeaponIK();
        }

        if (gameplayLevel == 8)
        {
            // Defensive force-visible pass for the Hammer rig: imported FBXes
            // sometimes ship with renderer.enabled = false on inner mesh nodes,
            // which made the HUD say "LEVEL 8 HAMMER" while the model was
            // present but invisible. Re-enable every renderer + the root GO.
            bool visible = false;
            string prefabName = "<null>";
            string socketName = "<none>";
            if (equippedWeaponObject != null)
            {
                if (!equippedWeaponObject.activeSelf)
                    equippedWeaponObject.SetActive(true);
                Renderer[] hammerRenderers = equippedWeaponObject.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < hammerRenderers.Length; i++)
                {
                    if (hammerRenderers[i] == null) continue;
                    if (!hammerRenderers[i].enabled) hammerRenderers[i].enabled = true;
                    if (hammerRenderers[i].isVisible || hammerRenderers[i].enabled) visible = true;
                }
                prefabName = equippedWeaponObject.name;
                socketName = equippedWeaponObject.transform.parent != null
                    ? equippedWeaponObject.transform.parent.name
                    : "<none>";
            }
            if (equippedWeaponObject == null || !visible)
            {
                Debug.LogError($"[WeaponEquip] Level 8 Hammer missing or invisible: weapon={prefabName} socket={socketName} visible={visible}. " +
                               "Expected Resources/Weapons/Imported/Hammer(level8)l/source/Sledgehammer/Sledge hammer.");
            }
            else
            {
                Debug.Log($"[WeaponEquip] Level 8 Hammer equipped: weapon={prefabName} socket={socketName} visible={visible}");
            }
        }

        // Weapon materials stay on the prefab-authored sharedMaterials. Runtime
        // tinting is intentionally skipped to avoid white/default material swaps.
        ApplyEquippedKatanaSkin();

        WeaponDefinition liveAnimDef = storeBuffsThisLevel
            ? storeEquipped
            : (SessionManager.Instance != null
                ? SessionManager.Instance.FindWeaponForCampaignLevel(gameplayLevel)
                : null);
        ApplyWeaponLiveAnimation(liveAnimDef);

        // Keep the FPS viewmodel in sync when the player swaps weapons.
        if (!isThirdPersonActive)
            RefreshFirstPersonWeaponModel(gameplayLevel);

        ApplyPlayerBodyBlackTint();
    }

    /// <summary>
    /// Attaches a <see cref="WeaponLiveAnimator"/> to the equipped weapon so it
    /// animates in sync with attacks. Different store weapons get different
    /// presets — Nunchucks chain-spin, Hammer gets a heavy windup, etc.
    /// </summary>
    private void ApplyWeaponLiveAnimation(WeaponDefinition def)
    {
        if (equippedWeaponObject == null) return;
        WeaponLiveAnimator live = equippedWeaponObject.GetComponent<WeaponLiveAnimator>();
        if (live == null) live = equippedWeaponObject.AddComponent<WeaponLiveAnimator>();
        live.Configure(def);
    }

    /// <summary>
    /// Re-tints every renderer on the equipped weapon object with the colour
    /// of the PRISM-store skin currently equipped on the player profile.
    /// </summary>
    public void ApplyEquippedKatanaSkin()
    {
        // Disabled by WeaponFix: renderer.material instancing was producing
        // default/white runtime material swaps on some imported prefabs.
    }

    private void SetupWeaponIK()
    {
        if (thirdPersonBody == null) return;

        WeaponIKHandler ikHandler = thirdPersonBody.GetComponent<WeaponIKHandler>();
        if (ikHandler == null)
            ikHandler = thirdPersonBody.AddComponent<WeaponIKHandler>();

        ikHandler.DisableIK();
    }

    // ── Hand bone priority list — melee grip sockets first, firearm tag last ─
    private static readonly string[] PlayerHandBoneNames =
    {
        "Hand.R",
        "Wrist.R",
        "Palm.R",
        "tag_accessory_right",  // Ronin fallback socket on right wrist
        "j_wrist_ri",           // Ronin wrist bone
        "weapon_bone_R",        // Crosby weapon socket
        "bip_hand_R",           // Crosby rig (enemies use same bones as player on some levels)
        "tag_weapon_right",     // Ronin firearm socket (kept as last-resort fallback)
        "mixamorig:RightHand",  // Mixamo
        "RightHand",
        "Hand_R", "hand_R", "hand_r",
        "Wrist_R", "wrist_R",
    };

    private static Transform FindPlayerHandBone(GameObject body)
    {
        // ── Priority 1: Humanoid avatar API (true hand bone) ───────────────
        // Fixes weapons attaching to carry/sheath tags like "tag_weapon_right"
        // that can live on the back for some rigs.
        Animator anim = body.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform bone = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
            {
                Debug.Log($"[PlayerController] Hand bone found via Animator API: '{bone.name}'");
                return bone;
            }
        }

        // ── Priority 2: explicit weapon/hand sockets authored on the rig ────
        foreach (string boneName in PlayerHandBoneNames)
        {
            Transform found = FindBoneExact(body.transform, boneName);
            if (found != null)
            {
                Debug.Log($"[PlayerController] Hand bone found via name search: '{found.name}'");
                return found;
            }
        }

        return null;
    }

    private static Transform FindBoneExact(Transform root, string boneName)
    {
        if (root.name == boneName) return root;
        foreach (Transform child in root)
        {
            Transform found = FindBoneExact(child, boneName);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform FindPlayerChainsawAttachBone(GameObject body)
    {
        if (body == null) return null;

        // Level 12 should attach to the animated wrist itself, not to any
        // firearm/body carry tags, so the rear handle stays in the palm.
        Transform accessoryBone = FindBoneExact(body.transform, "tag_accessory_right");
        if (accessoryBone != null)
            return accessoryBone;

        Transform wristBone = FindBoneExact(body.transform, "j_wrist_ri");
        if (wristBone != null)
            return wristBone;

        Animator anim = body.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
            return anim.GetBoneTransform(HumanBodyBones.RightHand);

        return FindPlayerHandBone(body);
    }

    private void AttachWeaponToHand(GameObject body, int weaponLevel = -1)
    {
        if (body == null) return;
        if (weaponAttachInProgress) return;

        int level = weaponLevel >= 1
            ? weaponLevel
            : (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1);
        if (equippedWeaponObject != null && equippedWeaponLevel == level)
            return;

        weaponAttachInProgress = true;
        if (equippedWeaponObject != null)
        {
            Destroy(equippedWeaponObject);
            equippedWeaponObject = null;
            equippedWeaponLevel = -1;
        }
        equippedWeaponHitbox = null;

        WeaponLoadout loadout = WeaponLoadoutCatalog.Get(level);

        // ── 1. Load prefab with guaranteed fallback chain ───────────────────
        float finalTargetSize;
        GameObject prefab = WeaponLoadoutCatalog.LoadPrefabWithFallback(level, out finalTargetSize);
        if (prefab == null)
        {
            Debug.LogWarning($"[PlayerController] All weapon sources exhausted for level {level}, using primitive.");
            Transform fallbackBone = ResolveHandBone(body, level);
            Transform fallbackSocket = GetOrCreateWeaponSocket(fallbackBone);
            equippedWeaponObject = BuildPrimitiveWeapon(level, fallbackSocket, loadout);
            equippedWeaponLevel = level;
            equippedWeaponHitbox = equippedWeaponObject != null ? equippedWeaponObject.GetComponent<WeaponHitbox>() : null;
            weaponAttachInProgress = false;
            return;
        }

        // ── 2. Bone verification — mirrors EnemyController exactly ──────────
        // Priority 1: known rig bone names
        // Priority 2: Humanoid avatar API (the #1 floating-weapon fix)
        Transform handBone = ResolveHandBone(body, level);
        Debug.Log($"[PlayerController] Hand bone resolved: '{handBone.name}' (level={level})");

        // ── 3. Attach parent: katana / level-2 → RightHand bone directly (palm alignment).
        //     Other weapons keep the scale-normalising socket under the hand bone.
        bool katanaStyle = level == 2
            || (prefab != null && prefab.name.ToLowerInvariant().Contains("katana"));
        Transform weaponParent = GetOrCreateWeaponSocket(handBone);

        // ── 4. Instantiate unparented to measure clean world-space bounds ────
        GameObject weapon = Instantiate(prefab);
        weapon.name = "WeaponModel";
        weapon.SetActive(true);
        SetLayerRecursive(weapon, gameObject.layer);

        foreach (Transform t in weapon.GetComponentsInChildren<Transform>(true))
            t.gameObject.SetActive(true);

        // ── 5. Measure bounds at unit scale ─────────────────────────────────
        weapon.transform.localScale = Vector3.one;
        float weaponExtent = GetMaxRendererExtent(weapon);
        if (weaponExtent < 0.001f) weaponExtent = 1f;

        // ── 6. Parent to hand / socket — MIRROR ENEMY LOGIC ─────────────────
        weapon.transform.SetParent(weaponParent, worldPositionStays: false);
        weapon.transform.localPosition = Vector3.zero;
        weapon.transform.localRotation = Quaternion.identity;
        weapon.transform.localScale    = Vector3.one;

        // ── 7. Scale to target world size ────────────────────────────────────
        float uniformScale = finalTargetSize / weaponExtent;
        Vector3 parentLossy = weaponParent.lossyScale;
        weapon.transform.localScale = new Vector3(
            uniformScale / Mathf.Max(Mathf.Abs(parentLossy.x), 0.0001f),
            uniformScale / Mathf.Max(Mathf.Abs(parentLossy.y), 0.0001f),
            uniformScale / Mathf.Max(Mathf.Abs(parentLossy.z), 0.0001f));

        // ── 8. Grip pose ─────────────────────────────────────────────────────
        if (prefab != null)
        {
            string n = prefab.name.ToLowerInvariant();
            if (n.Contains("sickle"))
            {
                weapon.transform.localPosition = DefaultLevel13SickleGripLocalPosition;
                weapon.transform.localRotation = Quaternion.Euler(DefaultLevel13SickleGripLocalEuler);
                ForceWeaponRenderable(weapon);
                ApplySickleHandPose(handBone, weapon.transform);
            }
            else if (level == 3 && prefab != null &&
                     prefab.name.IndexOf("shovel", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Shovel: hold mid-handle, blade tilted forward-up so it clears the ground.
                weapon.transform.localPosition = new Vector3(-0.06f, 0.08f, 0f);
                weapon.transform.localRotation = Quaternion.Euler(-50f, 0f, 90f);
                ForceWeaponRenderable(weapon);
            }
            else if (ShouldUsePlayerChainsawGrip(level, prefab))
            {
                ApplyPlayerChainsawGrip(weapon.transform);
                ForceWeaponRenderable(weapon);
            }
            else if (katanaStyle)
            {
                ApplyWeaponGripPose(weapon.transform, PlayerKatanaGripLocalPosition, PlayerKatanaGripLocalEuler);
                ForceWeaponRenderable(weapon);
            }
            else
            {
                if (!WeaponLoadoutCatalog.ApplyPlayerRuntimeGripPose(level, prefab, weapon.transform))
                    ApplyWeaponGripPose(weapon.transform, loadout.PlayerLocalPosition, loadout.PlayerLocalEuler);
            }
        }
        else
        {
            ApplyWeaponGripPose(weapon.transform, loadout.PlayerLocalPosition, loadout.PlayerLocalEuler);
        }

        WeaponLoadoutCatalog.ApplyRuntimeOverrides(level, prefab, weapon);
        RestoreWeaponSharedMaterialsFromPrefab(prefab, weapon);
        if (katanaStyle)
        {
            ApplyWeaponGripPose(weapon.transform, PlayerKatanaGripLocalPosition, PlayerKatanaGripLocalEuler);
            ForceWeaponRenderable(weapon);

            Debug.Log($"[KatanaGrip] Player katana applied on '{weaponParent.name}' pos={weapon.transform.localPosition} rot={weapon.transform.localEulerAngles} scale={weapon.transform.localScale}");
        }

        WeaponEquipper equipper = GetOrCreatePlayerWeaponEquipper();
        equipper.weaponSocket = weaponParent;
        equipper.EquipWeapon(
            weapon.transform,
            weapon.transform.localPosition,
            weapon.transform.localEulerAngles,
            weapon.transform.localScale,
            ResolveWeaponEquipLogName(level, prefab, equippedWeaponName));

        // Per-equip [PlayerController] / [WeaponFix] dump logs removed — they
        // fired on every weapon equip/re-equip (multiple times per level) and
        // were a major source of combat-time console spam.

        // ── 9. Disable physics, embedded animators, colliders ────────────────
        foreach (Animator weaponAnimator in weapon.GetComponentsInChildren<Animator>(true))
            weaponAnimator.enabled = false;

        foreach (Rigidbody rb in weapon.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = true;
            rb.useGravity  = false;
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        foreach (Collider col in weapon.GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        // ── 10. Wire WeaponBase ──────────────────────────────────────────────
        WeaponBase wb = weapon.GetComponent<WeaponBase>();
        if (wb == null) wb = weapon.AddComponent<WeaponBase>();
        wb.weaponName  = equippedWeaponName;
        wb.damage      = attackDamage;
        wb.attackRange = attackDistance;
        wb.isRanged    = false;

        // ── 11. Wire WeaponHitbox ────────────────────────────────────────────
        WeaponHitbox hitbox = weapon.GetComponent<WeaponHitbox>();
        if (hitbox == null) hitbox = weapon.AddComponent<WeaponHitbox>();
        hitbox.damage = attackDamage;
        hitbox.DisableHitbox();
        equippedWeaponHitbox = hitbox;

        // ── 12. Visibility fix (URP) ─────────────────────────────────────────
        if (weapon.GetComponent<WeaponVisibilityFix>() == null)
            weapon.AddComponent<WeaponVisibilityFix>();

        MeleeWeaponWallPullback wallPull = weapon.GetComponent<MeleeWeaponWallPullback>();
        if (wallPull == null) wallPull = weapon.AddComponent<MeleeWeaponWallPullback>();
        wallPull.Configure(transform, staticObstacleMask);

        // ── 13. Refresh melee animation event sink cache ─────────────────────
        if (thirdPersonBody != null)
        {
            MeleeAnimationEventSink sink = thirdPersonBody.GetComponentInChildren<MeleeAnimationEventSink>(true);
            if (sink != null) sink.ClearCache();
        }

        weapon.SetActive(true);
        equippedWeaponObject = weapon;
        equippedWeaponLevel = level;
        weaponAttachInProgress = false;

        // Bind KatanaCombatHandler so it can enforce the final grip pose and
        // knows which bladeCenter to cast from during the attack event.
        if (_katanaCombatHandler != null)
        {
            WeaponGripOffset grip = weapon.GetComponent<WeaponGripOffset>();
            if (grip != null)
                _katanaCombatHandler.BindKatana(grip);
        }
    }

    private static bool ShouldUsePlayerChainsawGrip(int level, GameObject prefab)
    {
        if (level != 12)
            return false;

        if (prefab != null)
        {
            string prefabName = prefab.name.ToLowerInvariant();
            if (prefabName.Contains("chainsaw") || prefabName.Contains("chain") || prefabName.Contains("saw"))
                return true;
        }

        return WeaponLoadoutCatalog.IsChainsawLevel(level, prefab);
    }

    private static void ApplyPlayerChainsawGrip(Transform weaponRoot)
    {
        if (weaponRoot == null)
            return;

        weaponRoot.localPosition = PlayerChainsawGripLocalPosition;
        weaponRoot.localRotation = Quaternion.Euler(PlayerChainsawGripLocalEuler);
    }

    private static int ResolveHittableMask()
    {
        int mask = 0;
        void TryAdd(string name) { int l = LayerMask.NameToLayer(name); if (l >= 0) mask |= 1 << l; }
        TryAdd("Hittable");
        TryAdd("Character");
        TryAdd("Enemy");
        TryAdd("Enemies");
        mask |= 1 << 0; // Default layer — enemies/players without explicit layer assignment
        return mask;
    }

    // Resolves the player right-hand bone using explicit names first, then the
    // Humanoid avatar API as the guaranteed fallback (fixes floating weapons).
    private static Transform ResolveHandBone(GameObject body, int weaponLevel = -1)
    {
        if (body == null) return null;

        // Prefer the real humanoid hand bone when available; some rigs include
        // carry/sheath tags (e.g. "tag_weapon_right") that may live on the back.
        Animator humanoid = body.GetComponentInChildren<Animator>(true);
        if (humanoid != null && humanoid.isHuman)
        {
            Transform rh = humanoid.GetBoneTransform(HumanBodyBones.RightHand);
            if (rh != null)
            {
                Debug.Log($"[PlayerController] ResolveHandBone: using humanoid RightHand '{rh.name}'");
                return rh;
            }
        }

        if (weaponLevel == 13)
        {
            string[] meleeNames =
            {
                "Hand.R",
                "hand.R",
                "Wrist.R",
                "Palm.R",
                "j_wrist_ri",
                "j_wrist_r",
                "weapon_bone_R",
                "bip_hand_R",
                "Bip001 R Hand",
                "Bip01 R Hand",
                "RightHand",
                "mixamorig:RightHand",
                "Hand_R",
                "hand_R",
                "hand_r",
                "Wrist_R",
                "wrist_R",
                "tag_accessory_right",
            };

            foreach (string n in meleeNames)
            {
                Transform found = FindBoneExact(body.transform, n);
                if (found != null)
                {
                    Debug.Log($"[PlayerController] ResolveHandBone: found '{found.name}' by Level 13 melee priority");
                    return found;
                }
            }

            Animator meleeAnim = body.GetComponentInChildren<Animator>(true);
            if (meleeAnim != null && meleeAnim.isHuman)
            {
                Transform meleeBone = meleeAnim.GetBoneTransform(HumanBodyBones.RightHand);
                if (meleeBone != null)
                {
                    Debug.Log($"[PlayerController] ResolveHandBone: found '{meleeBone.name}' via HumanBodyBones for Level 13");
                    return meleeBone;
                }
            }
        }

        // Explicit bone name search — same priority list as FindPlayerHandBone
        string[] names = {
            "Hand.R", "hand.R", "Wrist.R", "Palm.R",
            "RightHand", "mixamorig:RightHand", "Hand_R",
            "j_wrist_ri", "j_wrist_r", "weapon_bone_R",
            "bip_hand_R", "Bip001 R Hand", "Bip01 R Hand",
            "tag_accessory_right", "tag_weapon_right",
            "hand_R", "hand_r", "Wrist_R", "wrist_R",
        };
        foreach (string n in names)
        {
            Transform found = FindBoneExact(body.transform, n);
            if (found != null)
            {
                Debug.Log($"[PlayerController] ResolveHandBone: found '{found.name}' by name");
                return found;
            }
        }

        // Humanoid avatar API fallback — most reliable for any standard rig
        Animator anim = body.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform bone = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
            {
                Debug.Log($"[PlayerController] ResolveHandBone: found '{bone.name}' via HumanBodyBones");
                return bone;
            }
        }

        Debug.LogWarning("[PlayerController] ResolveHandBone: no hand bone found, falling back to body root.");
        return body.transform;
    }

    private static Transform GetOrCreateWeaponSocket(Transform handBone)
    {
        if (handBone == null)
            return null;

        Transform socketParent = FindBoneExact(handBone.root, "bip_hand_R") ?? handBone;
        Transform socket = handBone.Find(WeaponSocketName);
        if (socket == null)
            socket = socketParent.Find(WeaponSocketName);

        if (socket == null)
        {
            GameObject socketObject = new GameObject(WeaponSocketName);
            socket = socketObject.transform;
            socket.SetParent(socketParent, worldPositionStays: false);
        }
        else if (socket.parent != socketParent)
        {
            socket.SetParent(socketParent, worldPositionStays: false);
        }

        socket.localPosition = Vector3.zero;
        socket.localRotation = Quaternion.identity;
        socket.localScale = Vector3.one;

        return socket;
    }

    private static void ApplyWeaponGripPose(Transform weaponRoot, Vector3 localPosition, Vector3 localEuler)
    {
        if (weaponRoot == null) return;
        weaponRoot.localPosition = localPosition;
        weaponRoot.localRotation = Quaternion.Euler(localEuler);
    }

    private WeaponEquipper GetOrCreatePlayerWeaponEquipper()
    {
        if (playerWeaponEquipper == null)
        {
            playerWeaponEquipper = GetComponent<WeaponEquipper>();
            if (playerWeaponEquipper == null)
                playerWeaponEquipper = gameObject.AddComponent<WeaponEquipper>();
        }

        return playerWeaponEquipper;
    }

    private static string ResolveWeaponEquipLogName(int level, GameObject prefab, string fallbackName)
    {
        if (level == 2)
            return "Razor Katana";

        if (!string.IsNullOrWhiteSpace(fallbackName))
            return fallbackName;

        return prefab != null ? prefab.name : "Weapon";
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

    private static void ApplySickleHandPose(Transform handBone, Transform weaponRoot)
    {
        if (handBone == null || weaponRoot == null)
            return;

        SickleGripPoseDriver driver = handBone.GetComponent<SickleGripPoseDriver>();
        if (driver != null)
            driver.enabled = false;
    }

    private static void RestoreWeaponSharedMaterialsFromPrefab(GameObject prefab, GameObject weapon)
    {
        if (prefab == null || weapon == null)
            return;

        Renderer[] prefabRenderers = prefab.GetComponentsInChildren<Renderer>(true);
        Renderer[] weaponRenderers = weapon.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < weaponRenderers.Length; i++)
        {
            Renderer target = weaponRenderers[i];
            if (target == null)
                continue;

            Renderer source = FindMatchingPrefabRenderer(prefabRenderers, target.name);
            if (source == null || source.sharedMaterials == null || source.sharedMaterials.Length == 0)
                continue;

            Material[] targetMaterials = target.sharedMaterials;
            bool blank = targetMaterials == null || targetMaterials.Length == 0;
            if (!blank)
            {
                for (int j = 0; j < targetMaterials.Length; j++)
                {
                    Material mat = targetMaterials[j];
                    if (mat == null || IsDefaultWhiteMaterial(mat))
                    {
                        blank = true;
                        break;
                    }
                }
            }

            if (blank)
                target.sharedMaterials = source.sharedMaterials;
        }
    }

    private static Renderer FindMatchingPrefabRenderer(Renderer[] prefabRenderers, string rendererName)
    {
        if (prefabRenderers == null || string.IsNullOrEmpty(rendererName))
            return null;

        for (int i = 0; i < prefabRenderers.Length; i++)
        {
            Renderer renderer = prefabRenderers[i];
            if (renderer != null && renderer.name == rendererName)
                return renderer;
        }

        return prefabRenderers.Length > 0 ? prefabRenderers[0] : null;
    }

    private static bool IsDefaultWhiteMaterial(Material mat)
    {
        if (mat == null)
            return true;

        string lower = mat.name.ToLowerInvariant();
        if (lower.Contains("default") || lower.Contains("white"))
            return true;

        Color color = Color.white;
        if (mat.HasProperty("_BaseColor")) color = mat.GetColor("_BaseColor");
        else if (mat.HasProperty("_Color")) color = mat.GetColor("_Color");
        return color.r > 0.95f && color.g > 0.95f && color.b > 0.95f;
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

    private Transform FindBone(Transform root, string boneName)
    {
        if (root.name == boneName) return root;
        foreach (Transform child in root)
        {
            Transform found = FindBone(child, boneName);
            if (found != null) return found;
        }
        return null;
    }

    private Transform FindBoneContaining(Transform root, string substring)
    {
        if (root.name.ToLowerInvariant().Contains(substring.ToLowerInvariant())) return root;
        foreach (Transform child in root)
        {
            Transform found = FindBoneContaining(child, substring);
            if (found != null) return found;
        }
        return null;
    }

    private static void StripWeaponArmature(GameObject weapon)
    {
        string[] poisonKeywords = { "_ARM", "_Arm", "_arm", "Armature", "armature", "_Rig", "_rig" };
        var toDestroy = new List<GameObject>();

        foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
        {
            if (child == weapon.transform) continue;
            foreach (string keyword in poisonKeywords)
            {
                if (child.name.Contains(keyword))
                {
                    toDestroy.Add(child.gameObject);
                    break;
                }
            }
        }

        foreach (GameObject obj in toDestroy)
        {
            if (obj != null && obj != weapon)
                Object.DestroyImmediate(obj);
        }
    }

    private GameObject BuildWeaponModel(int level, Transform attachPoint, WeaponLoadout loadout)
    {
        GameObject levelPrefab = loadout.LoadPrefab();
        if (levelPrefab != null)
        {
            Transform weaponSocket = attachPoint != null && attachPoint.name == WeaponSocketName
                ? attachPoint
                : GetOrCreateWeaponSocket(attachPoint);
            GameObject weapon = Instantiate(levelPrefab);
            weapon.name = "WeaponModel";
            SetLayerRecursive(weapon, gameObject.layer);

            weapon.SetActive(true);
            foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
                child.gameObject.SetActive(true);

            StripWeaponArmature(weapon);
            EquipmentManager.ApplyAutoScale(weapon, loadout.TargetSize);
            Vector3 desiredLossyScale = weapon.transform.lossyScale;

            weapon.transform.SetParent(weaponSocket, worldPositionStays: false);
            weapon.transform.localPosition = Vector3.zero;
            weapon.transform.localRotation = Quaternion.identity;
            ApplyDesiredLossyScale(weapon.transform, desiredLossyScale);
            if (ShouldUsePlayerChainsawGrip(level, levelPrefab))
                ApplyPlayerChainsawGrip(weapon.transform);
            else if (!WeaponLoadoutCatalog.ApplyPlayerRuntimeGripPose(level, levelPrefab, weapon.transform))
                ApplyWeaponGripPose(weapon.transform, loadout.PlayerLocalPosition, loadout.PlayerLocalEuler);
            WeaponLoadoutCatalog.ApplyRuntimeOverrides(
                GameManager.Instance != null ? GameManager.Instance.currentLevel : 1,
                levelPrefab,
                weapon);

            // Safety: if scale ended up near-zero, force a visible fallback
            Vector3 lossyFinal = weapon.transform.lossyScale;
            float minAxis = Mathf.Min(Mathf.Abs(lossyFinal.x),
                            Mathf.Min(Mathf.Abs(lossyFinal.y), Mathf.Abs(lossyFinal.z)));
            if (minAxis < 0.005f)
            {
                weapon.transform.localScale = Vector3.one * 0.1f;
                Debug.LogWarning($"[PlayerController] Weapon near-zero scale — forced to (0.1,0.1,0.1).");
            }

            Debug.Log($"[PlayerController] Weapon '{weapon.name}' → hand '{weaponSocket.name}' " +
                      $"targetSize={loadout.TargetSize} localScale={weapon.transform.localScale} " +
                      $"lossyScale={weapon.transform.lossyScale}");

            WeaponBase weaponBase = weapon.GetComponent<WeaponBase>();
            if (weaponBase == null)
                weaponBase = weapon.AddComponent<WeaponBase>();

            weaponBase.weaponName  = equippedWeaponName;
            weaponBase.damage      = attackDamage;
            weaponBase.attackRange = attackDistance;
            weaponBase.isRanged    = false;

            if (weapon.GetComponent<WeaponVisibilityFix>() == null)
                weapon.AddComponent<WeaponVisibilityFix>();

            MeleeWeaponWallPullback wallPullBk = weapon.GetComponent<MeleeWeaponWallPullback>();
            if (wallPullBk == null) wallPullBk = weapon.AddComponent<MeleeWeaponWallPullback>();
            wallPullBk.Configure(transform, staticObstacleMask);

            WeaponHitbox hitbox = weapon.GetComponent<WeaponHitbox>();
            if (hitbox == null)
                hitbox = weapon.AddComponent<WeaponHitbox>();
            hitbox.damage = attackDamage;

            if (thirdPersonBody != null)
            {
                MeleeAnimationEventSink sink = thirdPersonBody.GetComponentInChildren<MeleeAnimationEventSink>(true);
                if (sink != null) sink.ClearCache();
            }

            equippedWeaponObject = weapon;
            return weapon;
        }

        return BuildPrimitiveWeapon(level, attachPoint, loadout);
    }

    private void CacheFirstPersonWeaponSlot()
    {
        firstPersonWeaponSlot = null;
        firstPersonWeaponMeshRenderer = null;
        if (firstPersonCam == null) return;

        // Search for an existing "Weapon" child under the FPS camera.
        Transform[] trs = firstPersonCam.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < trs.Length; i++)
        {
            if (trs[i].name != "Weapon") continue;
            firstPersonWeaponSlot = trs[i];
            firstPersonWeaponMeshRenderer = firstPersonWeaponSlot.GetComponent<MeshRenderer>();
            return;
        }

        // No "Weapon" slot found — create one directly under the camera so
        // RefreshFirstPersonWeaponModel never silently exits with a null slot.
        GameObject slotGo = new GameObject("Weapon");
        slotGo.transform.SetParent(firstPersonCam.transform, false);
        slotGo.transform.localPosition = Vector3.zero;
        slotGo.transform.localRotation = Quaternion.identity;
        firstPersonWeaponSlot = slotGo.transform;
    }

    private void ClearFirstPersonKnifeVisual()
    {
        if (firstPersonKnifeInstance != null)
        {
            Destroy(firstPersonKnifeInstance);
            firstPersonKnifeInstance = null;
        }
        if (firstPersonWeaponMeshRenderer != null)
            firstPersonWeaponMeshRenderer.enabled = true;
    }

    private void RefreshFirstPersonWeaponModel(int level)
    {
        if (firstPersonCam == null)
            firstPersonCam = GetComponentInChildren<Camera>(true);
        if (firstPersonCam == null)
            return;

        if (firstPersonWeaponSlot == null)
            CacheFirstPersonWeaponSlot();
        if (firstPersonWeaponSlot == null)
            return;

        ClearFirstPersonKnifeVisual();

        // Spawn a lightweight "viewmodel" weapon under the camera for COD-style FPS feel.
        float _;
        GameObject prefab = WeaponLoadoutCatalog.LoadPrefabWithFallback(Mathf.Clamp(level, 1, 16), out _);
        if (prefab == null)
            return;

        GameObject vm = Instantiate(prefab, firstPersonWeaponSlot, false);
        vm.name = "FP_WeaponModel";
        SetLayerRecursive(vm, gameObject.layer);

        foreach (Animator a in vm.GetComponentsInChildren<Animator>(true))
            a.enabled = false;
        foreach (Rigidbody rb in vm.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }
        foreach (Collider c in vm.GetComponentsInChildren<Collider>(true))
            c.enabled = false;

        // ── Weapon viewmodel offset rules (IMG_6923.mov fix) ────────────────
        // Mandatory offsets keep the hilt close to the visible right hand
        // instead of floating in the center of the FPV.
        vm.transform.localPosition = new Vector3(0.42f, -0.30f, 0.82f);
        vm.transform.localRotation = Quaternion.Euler(5f, 100f, -18f);
        vm.transform.localScale    = Vector3.one * 0.12f;

        // Per-weapon scale tuning so long blades don't fill the whole screen.
        if (level == 2) // Katana — long blade, needs a tighter scale
        {
            vm.transform.localPosition = new Vector3(0.44f, -0.32f, 0.92f);
            vm.transform.localRotation = Quaternion.Euler(7f, 96f, -20f);
            vm.transform.localScale = Vector3.one * 0.095f;
        }

        // Hide the placeholder mesh renderer if the camera has one.
        if (firstPersonWeaponMeshRenderer != null)
            firstPersonWeaponMeshRenderer.enabled = false;

        firstPersonKnifeInstance = vm;
    }

    private static void ApplyDesiredLossyScale(Transform target, Vector3 desiredLossyScale)
    {
        if (target == null) return;

        Vector3 parentLossyScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(
            desiredLossyScale.x / Mathf.Max(Mathf.Abs(parentLossyScale.x), 0.0001f),
            desiredLossyScale.y / Mathf.Max(Mathf.Abs(parentLossyScale.y), 0.0001f),
            desiredLossyScale.z / Mathf.Max(Mathf.Abs(parentLossyScale.z), 0.0001f));
    }

    private void NotifyFPSCameraRigHeadBone(Transform bodyRoot)
    {
        if (firstPersonCam == null) return;
        FPSCameraRig rig = firstPersonCam.GetComponent<FPSCameraRig>();
        if (rig == null) return;
        Transform headBone = FPSCameraRig_FindHeadBone(bodyRoot);
        if (headBone != null) rig.SetHeadBone(headBone);
    }

    // ── FPSCameraRig integration helpers ─────────────────────────────────────

    /// <summary>
    /// Searches the body hierarchy for a bone that looks like a head bone.
    /// Used to automatically anchor the FPS camera in front of the face.
    /// </summary>
    private static Transform FPSCameraRig_FindHeadBone(Transform root)
    {
        string[] candidates = { "Head", "Bip01 Head", "mixamorig:Head", "head", "Bip_Head" };
        foreach (string name in candidates)
        {
            Transform found = FPSCameraRig_SearchByName(root, name);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform FPSCameraRig_SearchByName(Transform t, string target)
    {
        if (t == null) return null;
        if (string.Equals(t.name, target, System.StringComparison.OrdinalIgnoreCase)) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform result = FPSCameraRig_SearchByName(t.GetChild(i), target);
            if (result != null) return result;
        }
        return null;
    }

    private static void SetLayerRecursive(GameObject obj, int layer)
    {
        if (obj == null || layer < 0) return;
        obj.layer = layer;
        foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = layer;
    }

    private void HandleWeaponGripDebugTuning()
    {
        if (Keyboard.current == null || equippedWeaponObject == null)
            return;

        // Toggle live grip tuning mode with F8.
        if (Keyboard.current.f8Key.wasPressedThisFrame)
            gripTuningMode = !gripTuningMode;
        if (!gripTuningMode)
            return;

        Transform w = equippedWeaponObject.transform;
        Vector3 pos = w.localPosition;
        Vector3 eul = w.localEulerAngles;
        float posStep = 0.002f;
        float rotStep = 1f;

        bool changed = false;

        // Position (Alt + I/K, O/L, P/;)
        bool alt = Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed;
        if (alt)
        {
            if (Keyboard.current.iKey.isPressed) { pos.x += posStep; changed = true; }
            if (Keyboard.current.kKey.isPressed) { pos.x -= posStep; changed = true; }
            if (Keyboard.current.oKey.isPressed) { pos.y += posStep; changed = true; }
            if (Keyboard.current.lKey.isPressed) { pos.y -= posStep; changed = true; }
            if (Keyboard.current.pKey.isPressed) { pos.z += posStep; changed = true; }
            if (Keyboard.current.semicolonKey.isPressed) { pos.z -= posStep; changed = true; }

            // Rotation (Alt + 1/2, 3/4, 5/6)
            if (Keyboard.current.digit1Key.isPressed) { eul.x += rotStep; changed = true; }
            if (Keyboard.current.digit2Key.isPressed) { eul.x -= rotStep; changed = true; }
            if (Keyboard.current.digit3Key.isPressed) { eul.y += rotStep; changed = true; }
            if (Keyboard.current.digit4Key.isPressed) { eul.y -= rotStep; changed = true; }
            if (Keyboard.current.digit5Key.isPressed) { eul.z += rotStep; changed = true; }
            if (Keyboard.current.digit6Key.isPressed) { eul.z -= rotStep; changed = true; }
        }

        if (changed)
        {
            w.localPosition = pos;
            w.localRotation = Quaternion.Euler(eul);
        }

        // Print current exact pose to Console (F9) for copy/paste into inspector/code.
        if (Keyboard.current.f9Key.wasPressedThisFrame)
        {
            Debug.Log($"[GripTune] weapon={equippedWeaponObject.name} pos={w.localPosition} euler={w.localEulerAngles}");
        }

        // Save live tuned grip to persistent PlayerPrefs (F10).
        if (Keyboard.current.f10Key.wasPressedThisFrame)
        {
            SaveCurrentWeaponGripToPrefs();
            Debug.Log("[GripTune] Saved persistent grip values (F10).");
        }

        // Clear saved persistent values and revert to inspector defaults (F11).
        if (Keyboard.current.f11Key.wasPressedThisFrame)
        {
            ClearSavedGripOverrides();
            Debug.Log("[GripTune] Cleared persistent grip values (F11).");
        }
    }

    private void SaveCurrentWeaponGripToPrefs()
    {
        if (equippedWeaponObject == null)
            return;

        Vector3 pos = equippedWeaponObject.transform.localPosition;
        Vector3 eul = equippedWeaponObject.transform.localEulerAngles;
        int level = equippedWeaponLevel >= 1
            ? equippedWeaponLevel
            : (GameManager.Instance != null ? GameManager.Instance.currentLevel : -1);

        if (level == 13)
        {
            level13SickleGripLocalPosition = pos;
            level13SickleGripLocalEuler = eul;
            SaveVector3Pref(PrefKeySicklePos, pos);
            SaveVector3Pref(PrefKeySickleEuler, eul);
            PlayerPrefs.Save();
            return;
        }

        if (level == 12)
        {
            level12SawGripLocalPosition = pos;
            level12SawGripLocalEuler = eul;
            SaveVector3Pref(PrefKeySawPos, pos);
            SaveVector3Pref(PrefKeySawEuler, eul);
            PlayerPrefs.Save();
            return;
        }

        Debug.LogWarning($"[GripTune] Save ignored for level {level}. Supported: level 12 (saw), level 13 (sickle).");
    }

    private void LoadSavedGripOverrides()
    {
        level13SickleGripLocalPosition = DefaultLevel13SickleGripLocalPosition;
        level13SickleGripLocalEuler = DefaultLevel13SickleGripLocalEuler;
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".x");
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".y");
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".z");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".x");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".y");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".z");
        level12SawGripLocalPosition = DefaultLevel12SawGripLocalPosition;
        level12SawGripLocalEuler = DefaultLevel12SawGripLocalEuler;
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".x");
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".y");
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".z");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".x");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".y");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".z");
    }

    private void ClearSavedGripOverrides()
    {
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".x");
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".y");
        PlayerPrefs.DeleteKey(PrefKeySicklePos + ".z");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".x");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".y");
        PlayerPrefs.DeleteKey(PrefKeySickleEuler + ".z");
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".x");
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".y");
        PlayerPrefs.DeleteKey(PrefKeySawPos + ".z");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".x");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".y");
        PlayerPrefs.DeleteKey(PrefKeySawEuler + ".z");
        PlayerPrefs.Save();
    }

    private static void SaveVector3Pref(string key, Vector3 value)
    {
        PlayerPrefs.SetFloat(key + ".x", value.x);
        PlayerPrefs.SetFloat(key + ".y", value.y);
        PlayerPrefs.SetFloat(key + ".z", value.z);
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

    private GameObject BuildPrimitiveWeapon(int level, Transform attachPoint, WeaponLoadout loadout)
    {
        Transform weaponSocket = attachPoint != null && attachPoint.name == WeaponSocketName
            ? attachPoint
            : GetOrCreateWeaponSocket(attachPoint);
        GameObject root = new GameObject("WeaponModel_Fallback");
        root.transform.SetParent(weaponSocket, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        ApplyWeaponGripPose(root.transform, loadout.PlayerLocalPosition, loadout.PlayerLocalEuler);
        SetLayerRecursive(root, gameObject.layer);

        BoxCollider weaponCollider = root.AddComponent<BoxCollider>();
        weaponCollider.isTrigger = true;
        weaponCollider.center    = Vector3.zero;
        weaponCollider.size      = Vector3.one * 0.1f;

        WeaponBase weaponBase  = root.AddComponent<WeaponBase>();
        weaponBase.weaponName  = equippedWeaponName;
        weaponBase.damage      = attackDamage;
        weaponBase.attackRange = attackDistance;
        weaponBase.isRanged    = false;

        root.AddComponent<WeaponVisibilityFix>();

        GameObject meshObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        meshObj.transform.SetParent(root.transform);
        meshObj.transform.localPosition = Vector3.zero;
        meshObj.transform.localScale    = new Vector3(0.05f, 0.3f, 0.05f);

        Destroy(meshObj.GetComponent<Collider>());

        MeleeWeaponWallPullback wallPullPm = root.GetComponent<MeleeWeaponWallPullback>();
        if (wallPullPm == null) wallPullPm = root.AddComponent<MeleeWeaponWallPullback>();
        wallPullPm.Configure(transform, staticObstacleMask);

        return root;
    }
}
