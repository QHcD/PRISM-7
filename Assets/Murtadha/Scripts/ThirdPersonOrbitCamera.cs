using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

#if PHOTON_UNITY_NETWORKING || PUN_2_OR_NEWER
using Photon.Pun;
#endif

/// <summary>
/// Third-person orbit camera for PRISM-7. Restored to the simpler PRISM-71
/// behaviour: SphereCast → SmoothDamp → minDistance clamp. The previous
/// containment / EnforceCollisionSafetySettings stack inflated wallPadding +
/// collisionRadius and pinched the camera onto the player whenever any wall
/// was nearby. This version is the original logic, kept compatible with the
/// CameraController bridge and the PlayerController.SetOrbitYaw/Pitch bridge.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
[DefaultExecutionOrder(-50)]
public class ThirdPersonOrbitCamera : MonoBehaviour
{
    public static ThirdPersonOrbitCamera Instance { get; private set; }

    [Header("Target")]
    public Transform target;

    [Header("Mouse Sensitivity")]
    [Range(0.1f, 10f)] public float sensitivityX = 3.0f;
    [Range(0.1f, 10f)] public float sensitivityY = 2.5f;
    public bool invertY = false;

    [Header("Vertical Angle Clamp")]
    [Range(-89f, 0f)] public float pitchMin = -30f;
    [Range(0f, 89f)]  public float pitchMax = 60f;

    [Header("Camera Distance")]
    [Range(1f, 15f)] public float defaultDistance = 4.5f;
    [Range(0.1f, 3f)] public float minDistance = 1.25f;

    [Header("Smooth Follow")]
    [Range(0.01f, 0.5f)] public float pivotSmoothTime = 0.08f;
    [Range(0.01f, 0.5f)] public float distanceSmoothTime = 0.10f;

    [Header("Pivot / Look Target")]
    [Range(0.5f, 2.5f)] public float pivotHeightOffset = 1.45f;
    [Range(-1f, 1f)]    public float shoulderOffset = 0.48f;

    [Header("Wall Collision")]
    public bool enableCollision = true;
    [Range(0.05f, 0.6f)] public float collisionRadius = 0.25f;
    [Range(0.01f, 0.3f)] public float wallPadding = 0.15f;
    [Range(0.005f, 0.2f)] public float collisionPullInTime = 0.03f;
    public LayerMask collisionMask = ~0;

    [Header("Cursor")]
    public bool lockCursor = true;

    [Header("Debug")]
    public bool debugDrawCollision = false;

    private float _yaw;
    private float _pitch;

    private Vector3 _smoothedPivot;
    private Vector3 _pivotVelocity;
    private bool    _pivotInitialized;
    private Vector3 _physicsPivot;
    private bool    _physicsPivotValid;

    private float _currentDistance;
    private float _distanceVelocity;

    private Camera _cam;
    private Coroutine _frameZeroBindRoutine;

    public float Yaw => _yaw;
    public float Pitch => _pitch;

    /// <summary>Flat (Y=0) forward direction the player should move when pressing W.</summary>
    public static Vector3 GetMovementForward()
    {
        if (Instance == null) return Vector3.forward;
        Vector3 fwd = Quaternion.Euler(0f, Instance._yaw, 0f) * Vector3.forward;
        return fwd.normalized;
    }

    /// <summary>Flat (Y=0) right direction for strafing (A/D input).</summary>
    public static Vector3 GetMovementRight()
    {
        if (Instance == null) return Vector3.right;
        Vector3 right = Quaternion.Euler(0f, Instance._yaw, 0f) * Vector3.right;
        return right.normalized;
    }

    private void Awake()
    {
        _cam = GetComponent<Camera>();

        if (!IsLocalPlayer())
        {
            enabled = false;
            _cam.enabled = false;
            return;
        }

        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        CameraController legacy = GetComponent<CameraController>();
        if (legacy != null)
            legacy.enabled = false;
    }

    private void Start()
    {
        if (target == null)
            target = GameplayCameraBootstrap.ResolveAuthoritativePlayerTarget();

        collisionMask = BuildCollisionMask();
        _cam.nearClipPlane = 0.08f;

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
        if (target == null) return;

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

        UpdateSmoothedPivot();

        Vector3 desiredPos = ComputeDesiredPosition();
        Vector3 finalPos = enableCollision
            ? ResolveCollision(_smoothedPivot, desiredPos)
            : desiredPos;

        transform.position = finalPos;
        transform.LookAt(_smoothedPivot);
    }

    private void ReadMouseInput()
    {
        float mouseX = 0f;
        float mouseY = 0f;

        if (UnityEngine.InputSystem.Mouse.current != null)
        {
            Vector2 mouseDelta = UnityEngine.InputSystem.Mouse.current.delta.ReadValue();
            mouseX = mouseDelta.x * 0.05f;
            mouseY = mouseDelta.y * 0.05f;
        }

        _yaw   += mouseX * sensitivityX;
        _pitch += (invertY ? mouseY : -mouseY) * sensitivityY;
        _pitch = Mathf.Clamp(_pitch, pitchMin, pitchMax);
        _yaw = (_yaw % 360f + 360f) % 360f;
    }

    private void UpdateSmoothedPivot()
    {
        Vector3 rawPivot = _physicsPivotValid ? _physicsPivot : GetRawPivot();

        if (!_pivotInitialized)
        {
            _smoothedPivot = rawPivot;
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
            _smoothedPivot, rawPivot, ref _pivotVelocity, smoothTime);
    }

    private Vector3 GetRawPivot()
    {
        Vector3 pivot = target.position + Vector3.up * pivotHeightOffset;
        Quaternion horizontalOrbit = Quaternion.Euler(0f, _yaw, 0f);
        pivot += horizontalOrbit * Vector3.right * shoulderOffset;
        return pivot;
    }

    private Vector3 ComputeDesiredPosition()
    {
        Quaternion orbitRotation = Quaternion.Euler(_pitch, _yaw, 0f);
        return _smoothedPivot + orbitRotation * (Vector3.back * defaultDistance);
    }

    private Vector3 ResolveCollision(Vector3 pivot, Vector3 desiredPos)
    {
        Vector3 castDir = desiredPos - pivot;
        float desiredDist = castDir.magnitude;
        if (desiredDist < 0.001f)
            return desiredPos;

        castDir /= desiredDist;

        float safeDist = FindSafeDistance(pivot, castDir, desiredDist);

        float smoothTime = safeDist < _currentDistance
            ? collisionPullInTime
            : distanceSmoothTime;

        _currentDistance = Mathf.SmoothDamp(
            _currentDistance, safeDist, ref _distanceVelocity,
            Mathf.Max(0.001f, smoothTime));

        _currentDistance = Mathf.Max(_currentDistance, minDistance);

        if (debugDrawCollision)
            Debug.DrawLine(pivot, pivot + castDir * _currentDistance, Color.green);

        return pivot + castDir * _currentDistance;
    }

    private float FindSafeDistance(Vector3 origin, Vector3 direction, float maxDist)
    {
        RaycastHit[] hits = Physics.SphereCastAll(
            origin, collisionRadius, direction, maxDist,
            collisionMask, QueryTriggerInteraction.Ignore);

        float nearest = maxDist;
        for (int i = 0; i < hits.Length; i++)
        {
            if (target != null && hits[i].collider != null
                && hits[i].collider.transform.IsChildOf(target))
                continue;

            float paddedDist = Mathf.Max(hits[i].distance - wallPadding, minDistance);
            if (paddedDist < nearest)
                nearest = paddedDist;
        }
        return nearest;
    }

    // ── PlayerController bridge ────────────────────────────────────────────
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
        if (newTarget == null) return;

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

        if (_cam == null)
            _cam = GetComponent<Camera>();
        if (_cam != null)
        {
            _cam.enabled = true;
            _cam.nearClipPlane = 0.08f;
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
        _currentDistance = Mathf.Max(safeDist, minDistance);
        return pivot + castDir * _currentDistance;
    }

    private void FeedPlayerControllerYaw()
    {
        if (target == null) return;

        if (!_playerControllerSearched)
        {
            _cachedPlayerController = target.GetComponentInChildren<PlayerController>(true)
                                   ?? target.GetComponentInParent<PlayerController>();
            _playerControllerSearched = true;
        }

        if (_cachedPlayerController == null) return;

        _cachedPlayerController.SetOrbitYaw(_yaw);
        _cachedPlayerController.SetOrbitPitch(_pitch);
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private bool IsLocalPlayer()
    {
#if PHOTON_UNITY_NETWORKING || PUN_2_OR_NEWER
        Transform t = transform;
        for (int i = 0; i < 8 && t != null; i++)
        {
            PhotonView pv = t.GetComponent<PhotonView>();
            if (pv != null)
                return pv.IsMine;
            t = t.parent;
        }
        return true;
#else
        return true;
#endif
    }

    private static void ApplyCursorLock(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !locked;
    }

    private LayerMask BuildCollisionMask()
    {
        int mask = 0;
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

        RemoveLayer(ref mask, "Player");
        RemoveLayer(ref mask, "Character");
        RemoveLayer(ref mask, "Enemy");
        RemoveLayer(ref mask, "Enemies");
        RemoveLayer(ref mask, "Hittable");
        RemoveLayer(ref mask, "UI");
        RemoveLayer(ref mask, "TransparentFX");
        RemoveLayer(ref mask, "Ignore Raycast");

        if (target != null)
            mask &= ~(1 << target.gameObject.layer);

        if (mask == 0)
            mask = 1 << 0;

        return mask;
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

    private static float WrapAngle(float angle)
    {
        angle %= 360f;
        return angle > 180f ? angle - 360f : angle;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (target == null) return;

        Vector3 pivot = Application.isPlaying
            ? _smoothedPivot
            : target.position + Vector3.up * pivotHeightOffset;

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(pivot, 0.08f);

        Quaternion orbitRot  = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3    desiredPos = pivot + orbitRot * (Vector3.back * defaultDistance);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(desiredPos, collisionRadius);

        Gizmos.color = Color.white;
        Gizmos.DrawLine(pivot, desiredPos);
    }
#endif
}
