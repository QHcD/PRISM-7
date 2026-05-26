using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

#if PHOTON_UNITY_NETWORKING || PUN_2_OR_NEWER
using Photon.Pun;
#endif

/// <summary>
/// Production-ready 360° third-person orbit camera for PRISM-7.
///
/// SETUP:
///   1. Create a new Camera GameObject (separate from the player — do NOT parent it to the player).
///   2. Attach this script to that Camera GameObject.
///   3. Set the [Target] field to the player's root Transform.
///   4. In multiplayer: the camera automatically disables itself on remote players
///      by checking the Photon PhotonView on the player's root.
///
/// MOVEMENT INTEGRATION:
///   In PlayerController, replace the cameraYaw-based movement direction with:
///       Vector3 forward = ThirdPersonOrbitCamera.GetMovementForward();
///       Vector3 right   = ThirdPersonOrbitCamera.GetMovementRight();
///   These are always horizontal (Y = 0) and normalised — safe to multiply
///   directly by input axes without further normalisation.
///
/// LAYERMASK (Wall Collision):
///   The camera collision uses a layermask that includes Default, Environment,
///   Wall, and other geometry layers. It excludes Player / Enemy / Character
///   so characters never push the camera. Adjust [collisionMask] in the
///   Inspector if your project uses different layer names.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
[DefaultExecutionOrder(-50)]
public class ThirdPersonOrbitCamera : MonoBehaviour
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    /// <summary>
    /// The active local-player camera. Null until Start() has run.
    /// Safe to poll from PlayerController and other scripts.
    /// </summary>
    public static ThirdPersonOrbitCamera Instance { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR FIELDS
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Target")]
    [Tooltip("The player root Transform. Assign via Inspector or let the camera " +
             "auto-find the GameObject tagged 'Player' at startup.")]
    public Transform target;

    // ── Orbit ─────────────────────────────────────────────────────────────────
    [Header("Mouse Sensitivity")]
    [Tooltip("Horizontal (left/right) rotation speed. " +
             "Typical range: 1 (slow) – 6 (fast). Default: 3.")]
    [Range(0.1f, 10f)]
    public float sensitivityX = 3.0f;

    [Tooltip("Vertical (up/down) rotation speed. Slightly lower than X feels natural.")]
    [Range(0.1f, 10f)]
    public float sensitivityY = 2.5f;

    [Tooltip("Invert vertical look (some players prefer this for gamepad style).")]
    public bool invertY = false;

    // ── Pitch clamp ───────────────────────────────────────────────────────────
    [Header("Vertical Angle Clamp")]
    [Tooltip("Minimum downward pitch (negative = looking down). " +
             "-30° prevents orbiting under the floor.")]
    [Range(-89f, 0f)]
    public float pitchMin = -30f;

    [Tooltip("Maximum upward pitch (positive = looking up). " +
             "60° lets the player look at tall enemies/buildings.")]
    [Range(0f, 89f)]
    public float pitchMax = 60f;

    // ── Distance ──────────────────────────────────────────────────────────────
    [Header("Camera Distance")]
    [Tooltip("Default distance from the orbit pivot to the camera (metres).")]
    [Range(1f, 15f)]
    public float defaultDistance = 4.5f;

    [Tooltip("Closest the camera can get (used as collision floor and for cramped spaces).")]
    [Range(0.1f, 3f)]
    // Raised from 0.4 → 1.2 so the camera can never sit inside the player's
    // head/body/weapon even if SphereCast incorrectly registers a hit on the
    // rig. 1.2 m keeps the third-person silhouette intact in tight corners.
    public float minDistance = 1.25f;

    // ── Smoothing ─────────────────────────────────────────────────────────────
    [Header("Smooth Follow")]
    [Tooltip("SmoothDamp time for the pivot following the player. " +
             "Smaller = snappier; larger = floatier. Recommended: 0.05 – 0.15.")]
    [Range(0.01f, 0.5f)]
    public float pivotSmoothTime = 0.08f;

    [Tooltip("SmoothDamp time for distance changes (wall pull-in recovery). " +
             "Smaller recovers faster after clearing a corner.")]
    [Range(0.01f, 0.5f)]
    public float distanceSmoothTime = 0.10f;

    // ── Pivot offset ──────────────────────────────────────────────────────────
    [Header("Pivot / Look Target")]
    [Tooltip("Upward offset from the player root so the camera looks at the chest/head " +
             "rather than the feet. 1.4 works for most humanoid characters.")]
    [Range(0.5f, 2.5f)]
    public float pivotHeightOffset = 1.45f;

    [Tooltip("Horizontal shoulder offset (positive = right shoulder, negative = left). " +
             "0 = centred behind the player.")]
    [Range(-1f, 1f)]
    public float shoulderOffset = 0.48f;

    // ── Collision ─────────────────────────────────────────────────────────────
    [Header("Wall Collision")]
    [Tooltip("Enable SphereCast so the camera never clips through walls.")]
    public bool enableCollision = true;

    [Tooltip("Radius of the SphereCast used to detect walls. Larger values keep " +
             "the camera further from surfaces.")]
    [Range(0.05f, 0.6f)]
    public float collisionRadius = 0.25f;

    [Tooltip("Extra gap between the camera and the wall surface (prevents z-fighting / edge clipping).")]
    [Range(0.01f, 0.3f)]
    public float wallPadding = 0.15f;

    [Tooltip("How quickly the camera is pulled toward the player when a wall appears (SmoothDamp time). " +
             "Smaller = snappier pull-in. 0.03 is a good default.")]
    [Range(0.005f, 0.2f)]
    public float collisionPullInTime = 0.03f;

    [Tooltip("Layers that can push the camera in. Automatically built in Start() from " +
             "Environment / Default / Wall layers, minus player / enemy bodies.")]
    public LayerMask collisionMask = ~0;

    // ── Cursor ────────────────────────────────────────────────────────────────
    [Header("Cursor")]
    [Tooltip("Lock and hide the cursor while the game is running. " +
             "The cursor is released when the application loses focus.")]
    public bool lockCursor = true;

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("Debug")]
    [Tooltip("Draw the collision SphereCast as a Gizmo in the Scene view.")]
    public bool debugDrawCollision = false;

    // ─────────────────────────────────────────────────────────────────────────
    // PRIVATE STATE
    // ─────────────────────────────────────────────────────────────────────────

    // Current orbit angles (degrees)
    private float _yaw;
    private float _pitch;

    // SmoothDamp state for pivot follow
    private Vector3 _smoothedPivot;
    private Vector3 _pivotVelocity;
    private bool    _pivotInitialized;
    private Vector3 _physicsPivot;
    private bool    _physicsPivotValid;

    // SmoothDamp state for collision distance
    private float _currentDistance;
    private float _distanceVelocity;

    // Cached component
    private Camera _cam;
    private Coroutine _frameZeroBindRoutine;
    private const float SafeNearClipPlane = 0.06f;
    private const float AbsoluteCollisionMinDistance = 0.08f;
    private const float MinimumWallPadding = 0.50f;
    private const float MinimumCollisionRadius = 0.42f;

    // ─────────────────────────────────────────────────────────────────────────
    // PROPERTIES  (read by PlayerController for camera-relative movement)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Current orbit yaw in degrees (horizontal angle around the player).</summary>
    public float Yaw => _yaw;

    /// <summary>Current orbit pitch in degrees (vertical angle).</summary>
    public float Pitch => _pitch;

    /// <summary>
    /// Horizontal forward direction the player should move when pressing W.
    /// Always flat (Y = 0, normalised). Use this in PlayerController instead of
    /// computing from a stale cameraYaw variable.
    /// </summary>
    public static Vector3 GetMovementForward()
    {
        if (Instance == null)
            return Vector3.forward;

        // Strip pitch from the camera's forward: project onto XZ plane.
        Vector3 fwd = Quaternion.Euler(0f, Instance._yaw, 0f) * Vector3.forward;
        return fwd.normalized;
    }

    /// <summary>
    /// Horizontal right direction for strafing (A/D input).
    /// Always flat (Y = 0, normalised).
    /// </summary>
    public static Vector3 GetMovementRight()
    {
        if (Instance == null)
            return Vector3.right;

        Vector3 right = Quaternion.Euler(0f, Instance._yaw, 0f) * Vector3.right;
        return right.normalized;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        _cam = GetComponent<Camera>();

        // ── Multiplayer: disable on remote players ────────────────────────────
        // We walk up to the player root (up to 6 levels) and check if the
        // PhotonView belongs to the local machine. Remote cameras must be
        // disabled so their mouses inputs don't drive remote player screens.
        if (!IsLocalPlayer())
        {
            enabled = false;
            _cam.enabled = false;
            return;
        }

        // ── Singleton registration ────────────────────────────────────────────
        if (Instance != null && Instance != this)
        {
            // A duplicate from a previous scene load — destroy only this component,
            // not the whole GameObject (the Camera component must survive).
            Destroy(this);
            return;
        }
        Instance = this;

        // ── Disable legacy CameraController on the same GameObject ────────────
        // PlayerController creates both for API compatibility; only one should
        // position the camera each frame to avoid fighting.
        CameraController legacy = GetComponent<CameraController>();
        if (legacy != null)
            legacy.enabled = false;
    }

    private void Start()
    {
        if (target == null)
        {
            target = GameplayCameraBootstrap.ResolveAuthoritativePlayerTarget();
        }
        else
        {
            Debug.Log($"[CameraTarget] Orbit camera using pre-assigned target transform: {target.name}.");
        }

        collisionMask = BuildCollisionMask();
        EnforceCollisionSafetySettings();

        _cam.nearClipPlane = SafeNearClipPlane;

        if (target != null)
        {
            BindAuthoritativeTarget(target);
        }
        else
        {
            _yaw = transform.eulerAngles.y;
            _pitch = Mathf.Clamp(WrapAngle(transform.eulerAngles.x), pitchMin, pitchMax);
            _currentDistance = defaultDistance;
            _pivotInitialized = false;
        }

        if (lockCursor)
            ApplyCursorLock(true);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
        if (_frameZeroBindRoutine != null)
            StopCoroutine(_frameZeroBindRoutine);
        _frameZeroBindRoutine = StartCoroutine(FrameZeroBindRoutine());
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        if (_frameZeroBindRoutine != null)
        {
            StopCoroutine(_frameZeroBindRoutine);
            _frameZeroBindRoutine = null;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ClearTargetCache();
        if (!GameplayCameraBootstrap.TryBindActiveGameplayCamera() && isActiveAndEnabled)
        {
            if (_frameZeroBindRoutine != null)
                StopCoroutine(_frameZeroBindRoutine);
            _frameZeroBindRoutine = StartCoroutine(FrameZeroBindRoutine());
        }
    }

    private IEnumerator FrameZeroBindRoutine()
    {
        for (int i = 0; i < 120; i++)
        {
            if (GameplayCameraBootstrap.TryBindActiveGameplayCamera())
            {
                _frameZeroBindRoutine = null;
                yield break;
            }
            yield return null;
        }

        _frameZeroBindRoutine = null;
    }

    private void OnPreCull()
    {
        if (target == null)
            GameplayCameraBootstrap.TryBindActiveGameplayCamera();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // Release cursor when the window loses focus; re-lock when it returns.
        if (lockCursor)
            ApplyCursorLock(hasFocus);
    }

    private void FixedUpdate()
    {
        if (target == null)
        {
            Transform resolved = GameplayCameraBootstrap.ResolveAuthoritativePlayerTarget();
            if (resolved != null)
                BindAuthoritativeTarget(resolved);
        }

        if (target == null)
            return;

        ReadMouseInput();
        _physicsPivot = GetRawPivot();
        _physicsPivotValid = true;
        FeedPlayerControllerYaw();
    }

    private void LateUpdate()
    {
        if (target == null)
        {
            if (!GameplayCameraBootstrap.TryBindActiveGameplayCamera())
                return;
            if (target == null)
                return;
        }
        EnforceCollisionSafetySettings();

        UpdateSmoothedPivot();

        // Step 3: Compute desired camera position from pivot + orbit rotation
        Vector3 desiredPos = ComputeDesiredPosition();

        // Step 4: Resolve wall collision — pull camera in if geometry blocks it
        Vector3 finalPos = enableCollision
            ? ResolveCollision(_smoothedPivot, desiredPos)
            : desiredPos;
        if (enableCollision)
            finalPos = ContainCameraOutsideWalls(finalPos, _smoothedPivot);

        // Step 5: Apply position and orientation
        transform.position = finalPos;
        transform.LookAt(_smoothedPivot + Vector3.up * 0f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 1 — MOUSE INPUT
    // ─────────────────────────────────────────────────────────────────────────

    private void ReadMouseInput()
    {
        float mouseX = 0f;
        float mouseY = 0f;

        if (UnityEngine.InputSystem.Mouse.current != null)
        {
            Vector2 mouseDelta = UnityEngine.InputSystem.Mouse.current.delta.ReadValue();
            // Input.GetAxis("Mouse X") returns values scaled by sensitivity and speed.
            // Under new Input System, Mouse.current.delta.ReadValue() returns raw pixel delta.
            // Multiplying pixel delta by 0.05f provides a comparable scaling factor.
            mouseX = mouseDelta.x * 0.05f;
            mouseY = mouseDelta.y * 0.05f;
        }

        // Apply sensitivity (the GetAxis value is already framerate-independent
        // when Sensitivity is set to 1 in the Input Manager, but multiply it
        // here so the Inspector knob is the single source of truth).
        _yaw   += mouseX * sensitivityX;
        _pitch += (invertY ? mouseY : -mouseY) * sensitivityY;

        // Clamp vertical angle to prevent camera flipping over the top.
        _pitch = Mathf.Clamp(_pitch, pitchMin, pitchMax);

        // Wrap yaw to [0, 360] so it never overflows to infinity over long sessions.
        _yaw = (_yaw % 360f + 360f) % 360f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 2 — SMOOTH PIVOT FOLLOW
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateSmoothedPivot()
    {
        Vector3 rawPivot = _physicsPivotValid ? _physicsPivot : GetRawPivot();

        if (!_pivotInitialized)
        {
            _smoothedPivot    = rawPivot;
            _pivotInitialized = true;
            return;
        }

        float planarSpeed = 0f;
        if (target != null)
        {
            PlayerController pc = target.GetComponent<PlayerController>();
            if (pc != null)
                planarSpeed = pc.PlanarSpeed;
        }

        float smoothTime = Mathf.Max(0.001f, pivotSmoothTime);
        if (planarSpeed > 5f)
            smoothTime *= 0.4f;
        else if (planarSpeed > 2f)
            smoothTime *= 0.65f;

        float delta = Mathf.Abs(rawPivot.x - _smoothedPivot.x)
                    + Mathf.Abs(rawPivot.y - _smoothedPivot.y)
                    + Mathf.Abs(rawPivot.z - _smoothedPivot.z);
        if (delta < 0.02f)
        {
            _smoothedPivot = rawPivot;
            _pivotVelocity = Vector3.zero;
            return;
        }

        _smoothedPivot = Vector3.SmoothDamp(
            _smoothedPivot,
            rawPivot,
            ref _pivotVelocity,
            smoothTime);
    }

    /// <summary>
    /// The raw (unsmoothed) world-space point the camera orbits around.
    /// Positioned at chest height, offset to the shoulder side.
    /// </summary>
    private Vector3 GetRawPivot()
    {
        Vector3 pivot = target.position + Vector3.up * pivotHeightOffset;

        // Shoulder offset is applied in the camera's horizontal orbit plane so
        // it doesn't rotate with the player's body — the camera stays over the
        // right shoulder regardless of the player's facing direction.
        Quaternion horizontalOrbit = Quaternion.Euler(0f, _yaw, 0f);
        pivot += horizontalOrbit * Vector3.right * shoulderOffset;

        return pivot;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 3 — DESIRED CAMERA POSITION
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 ComputeDesiredPosition()
    {
        // Build the full orbit rotation (pitch + yaw).
        // Unity's Quaternion.Euler applies intrinsic rotations: X (pitch) then Y (yaw).
        Quaternion orbitRotation = Quaternion.Euler(_pitch, _yaw, 0f);

        // The camera sits behind the pivot: negate forward (Z) and push back
        // by defaultDistance. Shoulder offset is already baked into the pivot.
        return _smoothedPivot + orbitRotation * (Vector3.back * defaultDistance);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 4 — WALL COLLISION  (SphereCast)
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 ResolveCollision(Vector3 pivot, Vector3 desiredPos)
    {
        // Direction and desired distance from pivot to camera.
        Vector3 castDir       = desiredPos - pivot;
        float   desiredDist   = castDir.magnitude;

        if (desiredDist < 0.001f)
            return desiredPos;

        castDir /= desiredDist;   // normalise without allocating a new vector

        // SphereCast: if any geometry sits between the pivot and the desired
        // camera position, find the nearest hit point.
        float safeDist = FindSafeDistance(pivot, castDir, desiredDist);

        // Smooth pull-in (fast when hitting wall), smooth recover (slow backing out).
        float smoothTime = safeDist < _currentDistance
            ? collisionPullInTime               // pulling in — fast
            : distanceSmoothTime;               // backing out — gradual

        float smoothedDistance = Mathf.SmoothDamp(
            _currentDistance,
            safeDist,
            ref _distanceVelocity,
            Mathf.Max(0.001f, smoothTime));

        if (safeDist < _currentDistance)
            smoothedDistance = Mathf.Min(smoothedDistance, safeDist);

        float minimumAllowedDistance = safeDist < minDistance
            ? Mathf.Max(AbsoluteCollisionMinDistance, Mathf.Min(safeDist, minDistance))
            : minDistance;
        _currentDistance = Mathf.Clamp(smoothedDistance, minimumAllowedDistance, defaultDistance);

        // Debug visualisation — draws a green line in the Scene view while active.
        if (debugDrawCollision)
            Debug.DrawLine(pivot, pivot + castDir * _currentDistance, Color.green);

        Vector3 candidate = pivot + castDir * _currentDistance;
        candidate = PreventInsideSolidGeometry(candidate, pivot, castDir);
        return candidate;
    }

    private float FindSafeDistance(Vector3 origin, Vector3 direction, float maxDist)
    {
        // SphereCastAll instead of SphereCast so we can skip child colliders of
        // the player rig, which may sit on the same Default layer as the environment.
        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            collisionRadius,
            direction,
            maxDist,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        float nearest = maxDist;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (col == null)
                continue;
            // Ignore any collider that belongs to the player hierarchy.
            if (target != null && col.transform.IsChildOf(target))
                continue;
            if (IsExcludedCollisionCollider(col))
                continue;

            float paddedDist = Mathf.Max(hits[i].distance - GetEffectiveWallPadding(), AbsoluteCollisionMinDistance);
            if (paddedDist < nearest)
                nearest = paddedDist;
        }

        return nearest;
    }

    private Vector3 PreventInsideSolidGeometry(Vector3 candidate, Vector3 pivot, Vector3 direction)
    {
        if (!HasSolidOverlap(candidate, GetContainmentRadius()))
            return candidate;

        float currentDist = Vector3.Distance(pivot, candidate);
        float safeDist = FindSafeDistance(
            pivot,
            direction,
            Mathf.Max(currentDist + collisionRadius + GetEffectiveWallPadding(), minDistance));

        _distanceVelocity = 0f;
        _currentDistance = Mathf.Min(_currentDistance, safeDist);
        return pivot + direction * Mathf.Max(AbsoluteCollisionMinDistance, safeDist);
    }

    private Vector3 ContainCameraOutsideWalls(Vector3 candidate, Vector3 pivot)
    {
        Vector3 fromPivot = candidate - pivot;
        float distance = fromPivot.magnitude;
        if (distance <= AbsoluteCollisionMinDistance)
            return candidate;

        Vector3 direction = fromPivot / distance;
        float radius = GetContainmentRadius();
        for (int i = 0; i < 8 && HasSolidOverlap(candidate, radius); i++)
        {
            distance = Mathf.Max(AbsoluteCollisionMinDistance, distance - (radius * 0.5f));
            candidate = pivot + direction * distance;
            _currentDistance = Mathf.Min(_currentDistance, distance);
            _distanceVelocity = 0f;
        }

        return candidate;
    }

    private bool HasSolidOverlap(Vector3 center, float radius)
    {
        Collider[] overlaps = Physics.OverlapSphere(
            center,
            radius,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider col = overlaps[i];
            if (col == null)
                continue;
            if (target != null && col.transform.IsChildOf(target))
                continue;
            if (IsExcludedCollisionCollider(col))
                continue;
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 6 — FEED BACK INTO PLAYERCONTROLLER  (backward compatibility)
    // ─────────────────────────────────────────────────────────────────────────

    private PlayerController _cachedPlayerController;
    private bool             _playerControllerSearched;

    public void ClearTargetCache()
    {
        target = null;
        _pivotInitialized = false;
        _physicsPivotValid = false;
        _pivotVelocity = Vector3.zero;
        _distanceVelocity = 0f;
        _cachedPlayerController = null;
        _playerControllerSearched = false;
    }

    public void BindAuthoritativeTarget(Transform newTarget)
    {
        if (newTarget == null)
            return;

        target = newTarget;
        _cachedPlayerController = null;
        _playerControllerSearched = false;
        _yaw = target.eulerAngles.y;
        _pitch = Mathf.Clamp(8f, pitchMin, pitchMax);
        _currentDistance = defaultDistance;
        _pivotVelocity = Vector3.zero;
        _distanceVelocity = 0f;
        _physicsPivot = GetRawPivot();
        _physicsPivotValid = true;
        _smoothedPivot = _physicsPivot;
        _pivotInitialized = true;
        collisionMask = BuildCollisionMask();
        EnforceCollisionSafetySettings();

        if (_cam == null)
            _cam = GetComponent<Camera>();
        if (_cam != null)
        {
            _cam.enabled = true;
            _cam.nearClipPlane = SafeNearClipPlane;
            if (!_cam.gameObject.CompareTag("MainCamera"))
                _cam.gameObject.tag = "MainCamera";
        }

        Vector3 desiredPos = ComputeDesiredPosition();
        Vector3 finalPos = enableCollision
            ? ResolveCollisionImmediate(_smoothedPivot, desiredPos)
            : desiredPos;

        transform.position = finalPos;
        if ((_smoothedPivot - transform.position).sqrMagnitude > 0.0001f)
            transform.LookAt(_smoothedPivot);

        CameraController legacy = GetComponent<CameraController>();
        if (legacy != null)
        {
            legacy.target = target;
            legacy.pitch = _pitch;
            legacy.externalYaw = _yaw;
        }
    }

    private Vector3 ResolveCollisionImmediate(Vector3 pivot, Vector3 desiredPos)
    {
        Vector3 castDir = desiredPos - pivot;
        float desiredDist = castDir.magnitude;
        if (desiredDist < 0.001f)
            return desiredPos;

        castDir /= desiredDist;
        float safeDist = enableCollision
            ? FindSafeDistance(pivot, castDir, desiredDist)
            : desiredDist;
        _currentDistance = safeDist < minDistance
            ? Mathf.Max(AbsoluteCollisionMinDistance, safeDist)
            : Mathf.Max(safeDist, minDistance);
        return pivot + castDir * _currentDistance;
    }

    private void FeedPlayerControllerYaw()
    {
        if (target == null) return;

        // Cache the search result so we only call GetComponent once.
        if (!_playerControllerSearched)
        {
            _cachedPlayerController = target.GetComponentInChildren<PlayerController>(true)
                                   ?? target.GetComponentInParent<PlayerController>();
            _playerControllerSearched = true;
        }

        if (_cachedPlayerController == null) return;

        // PlayerController.cameraYaw and cameraPitch are private — we expose
        // them via the two public setter methods below.
        // If those methods do not exist yet, add them to PlayerController:
        //
        //   public void SetOrbitYaw(float yaw)   { cameraYaw   = yaw; }
        //   public void SetOrbitPitch(float pitch){ cameraPitch = pitch; }
        //
        _cachedPlayerController.SetOrbitYaw(_yaw);
        _cachedPlayerController.SetOrbitPitch(_pitch);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsLocalPlayer()
    {
#if PHOTON_UNITY_NETWORKING || PUN_2_OR_NEWER
        // Walk up to 8 parent transforms to find the PhotonView that owns this camera.
        Transform t = transform;
        for (int i = 0; i < 8 && t != null; i++)
        {
            PhotonView pv = t.GetComponent<PhotonView>();
            if (pv != null)
                return pv.IsMine;
            t = t.parent;
        }
        // If no PhotonView is found anywhere up the hierarchy, assume single-player.
        return true;
#else
        return true;   // Single-player or non-Photon multiplayer — always local.
#endif
    }

    private static void ApplyCursorLock(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !locked;
    }

    /// <summary>
    /// Builds the camera collision LayerMask at runtime so it always matches
    /// whatever layers exist in this particular project's TagManager.
    /// Includes solid geometry; excludes player / enemy bodies.
    /// </summary>
    private LayerMask BuildCollisionMask()
    {
        int mask = 0;

        // Include solid world geometry.
        AddLayer(ref mask, "Default");
        AddLayer(ref mask, "Environment");
        AddLayer(ref mask, "Map");
        AddLayer(ref mask, "LevelContent");
        AddLayer(ref mask, "Building");
        AddLayer(ref mask, "Buildings");
        AddLayer(ref mask, "StaticObstacle");
        AddLayer(ref mask, "Wall");
        AddLayer(ref mask, "Walls");
        AddLayer(ref mask, "Door");
        AddLayer(ref mask, "Doors");
        AddLayer(ref mask, "Obstacle");
        AddLayer(ref mask, "Prop");
        AddLayer(ref mask, "Props");
        AddLayer(ref mask, "Ground");
        AddLayer(ref mask, "Terrain");

        // Exclude bodies that must never push the camera.
        RemoveLayer(ref mask, "Player");
        RemoveLayer(ref mask, "Character");
        RemoveLayer(ref mask, "Enemy");
        RemoveLayer(ref mask, "Enemies");
        RemoveLayer(ref mask, "Hittable");
        RemoveLayer(ref mask, "UI");
        RemoveLayer(ref mask, "TransparentFX");
        RemoveLayer(ref mask, "Ignore Raycast");

        // Belt-and-suspenders: also strip the actual layer the target is on.
        if (target != null)
            mask &= ~(1 << target.gameObject.layer);

        // Fallback: if NOTHING matched any layer name, use Default only.
        if (mask == 0)
            mask = 1 << 0;

        return mask;
    }

    private void EnforceCollisionSafetySettings()
    {
        collisionRadius = Mathf.Max(collisionRadius, MinimumCollisionRadius);
        wallPadding = Mathf.Max(wallPadding, MinimumWallPadding);
        if (_cam == null)
            _cam = GetComponent<Camera>();
        if (_cam != null && (_cam.nearClipPlane < 0.05f || _cam.nearClipPlane > SafeNearClipPlane))
            _cam.nearClipPlane = SafeNearClipPlane;
    }

    private float GetEffectiveWallPadding()
    {
        float nearClip = _cam != null ? _cam.nearClipPlane : SafeNearClipPlane;
        return Mathf.Max(wallPadding, nearClip + 0.44f);
    }

    private float GetContainmentRadius()
    {
        float nearClip = _cam != null ? _cam.nearClipPlane : SafeNearClipPlane;
        return Mathf.Max(collisionRadius, nearClip + wallPadding);
    }

    private bool IsExcludedCollisionCollider(Collider col)
    {
        if (col == null)
            return true;

        int layer = col.gameObject.layer;
        return LayerMatches(layer, "Player")
            || LayerMatches(layer, "Character")
            || LayerMatches(layer, "Enemy")
            || LayerMatches(layer, "Enemies")
            || LayerMatches(layer, "Hittable")
            || LayerMatches(layer, "UI")
            || LayerMatches(layer, "TransparentFX")
            || LayerMatches(layer, "Ignore Raycast");
    }

    private static bool LayerMatches(int layer, string name)
    {
        int namedLayer = LayerMask.NameToLayer(name);
        return namedLayer >= 0 && layer == namedLayer;
    }

    private static void AddLayer(ref int mask, string name)
    {
        int layer = LayerMask.NameToLayer(name);
        if (layer >= 0) mask |= 1 << layer;
    }

    private static void RemoveLayer(ref int mask, string name)
    {
        int layer = LayerMask.NameToLayer(name);
        if (layer >= 0) mask &= ~(1 << layer);
    }

    /// <summary>
    /// Converts a Unity Euler angle (0–360) to a signed angle (-180 to 180)
    /// so pitch comparisons against pitchMin/pitchMax work correctly.
    /// </summary>
    private static float WrapAngle(float angle)
    {
        angle %= 360f;
        return angle > 180f ? angle - 360f : angle;
    }

#if UNITY_EDITOR
    // ── Scene-view Gizmos ──────────────────────────────────────────────────
    private void OnDrawGizmosSelected()
    {
        if (target == null) return;

        Vector3 pivot = Application.isPlaying
            ? _smoothedPivot
            : target.position + Vector3.up * pivotHeightOffset;

        // Orbit pivot marker
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(pivot, 0.08f);

        // Desired camera position
        Quaternion orbitRot  = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3    desiredPos = pivot + orbitRot * (Vector3.back * defaultDistance);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(desiredPos, collisionRadius);

        // Line from pivot to desired position (shows the SphereCast path)
        Gizmos.color = Color.white;
        Gizmos.DrawLine(pivot, desiredPos);
    }
#endif
}
