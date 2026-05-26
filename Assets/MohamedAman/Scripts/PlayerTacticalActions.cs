using UnityEngine;

/// <summary>
/// Player Z/X/C tactical collider + ground alignment (mirrors RobustThirdPersonMovement /
/// EnemyTacticalActions timing). Slide/JumpOver share tactical height; Prone has its own
/// pinned capsule + visual offset path so the mesh does not sink.
/// </summary>
[DisallowMultipleComponent]
public class PlayerTacticalActions : MonoBehaviour
{
    [Header("Slide / JumpOver (do not change for prone fix)")]
    [Range(0.2f, 0.9f)] public float tacticalHeightRatio = 0.50f;
    [Tooltip("Lerp speed when returning to standing after slide/jumpover ends.")]
    public float colliderRestoreSpeed = 12f;
    public float slideActionDuration = 0.45f;
    public float jumpOverActionDuration = 0.60f;

    [Header("Prone only (C key)")]
    [Tooltip("Capsule height as fraction of standing height. 0.38 ≈ 0.68m when standing is 1.8m.")]
    [Range(0.22f, 0.55f)] public float proneHeightRatio = 0.38f;
    [Tooltip("Raises the visual body mesh while prone (CharacterController root unchanged). Tune 0.06–0.09 if floating/sinking.")]
    public float proneVisualYOffset = 0.07f;
    [Tooltip("Hard clamp on prone visual lift to prevent negative offsets that bury the mesh.")]
    public float proneVisualYOffsetMax = 0.12f;
    [Tooltip("Lerp speed when standing up from prone.")]
    public float proneRestoreSpeed = 10f;

    [Header("Debug")]
    public bool debugLog = false;

    private CharacterController _controller;
    private float _standingHeight = 1.8f;
    private float _standingRadius = 0.4f;
    private Vector3 _standingCenter = new Vector3(0f, 0.92f, 0f);
    private float _capsuleBottomY;
    private float _tacticalAnimTimer;
    private bool _proneActive;
    private string _activeAction = "";

    public bool IsTacticalAnimActive => _tacticalAnimTimer > 0f;
    public bool IsProneActive => _proneActive;
    public bool IsActionLocked => _tacticalAnimTimer > 0f;
    public float ProneVisualYOffset => Mathf.Clamp(proneVisualYOffset, 0f, proneVisualYOffsetMax);

    public float TacticalHeight => _standingHeight * tacticalHeightRatio;
    public float ProneColliderHeight => GetProneColliderHeight();

    private bool CanMoveController()
    {
        return isActiveAndEnabled
            && gameObject.activeInHierarchy
            && _controller != null
            && _controller.enabled
            && _controller.gameObject.activeInHierarchy;
    }

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        CacheStandingCollider();
    }

    private void OnDisable()
    {
        RestoreStandingColliderImmediate();
        _tacticalAnimTimer = 0f;
        _proneActive = false;
        _activeAction = "";
    }

    private void OnDestroy()
    {
        RestoreStandingColliderImmediate();
    }

    public void CacheStandingCollider()
    {
        if (_controller == null)
            _controller = GetComponent<CharacterController>();
        if (_controller == null) return;

        _standingHeight = _controller.height;
        _standingRadius = _controller.radius;
        _standingCenter = _controller.center;
        _capsuleBottomY = _standingCenter.y - _standingHeight * 0.5f;
    }

    public void BeginSlide()
    {
        CacheStandingCollider();
        _activeAction = "Slide";
        _tacticalAnimTimer = slideActionDuration;
        SnapColliderImmediate(TacticalHeight);
        FlushGroundSnap(zeroMove: true);
        LogAction("Slide START");
    }

    public void BeginJumpOver()
    {
        CacheStandingCollider();
        _activeAction = "JumpOver";
        _tacticalAnimTimer = jumpOverActionDuration;
        SnapColliderImmediate(TacticalHeight);
        FlushGroundSnap(zeroMove: true);
        LogAction("JumpOver START");
    }

    public void SetProne(bool prone)
    {
        CacheStandingCollider();
        _proneActive = prone;
        if (prone)
        {
            _activeAction = "Prone";
            ApplyProneColliderPinned();
            FlushGroundSnap(zeroMove: true);
            EnforceProneGround();
            LogAction("Prone START");
            if (_controller != null)
                Debug.Log($"[Prone] enter height={_controller.height:F3} center={_controller.center.y:F3} stepOffset={_controller.stepOffset:F3}");
        }
        else
        {
            _activeAction = "";
            RestoreStandingColliderImmediate();
            FlushGroundSnap(zeroMove: true);
            LogAction("Prone END");
            if (_controller != null)
                Debug.Log($"[Prone] exit height={_controller.height:F3} center={_controller.center.y:F3} stepOffset={_controller.stepOffset:F3}");
        }
    }

    public void Tick(float deltaTime)
    {
        if (_tacticalAnimTimer > 0f)
        {
            _tacticalAnimTimer -= deltaTime;
            if (_tacticalAnimTimer <= 0f)
                EndTacticalAnim();
        }
    }

    public void EndSlide()
    {
        if (_activeAction == "Slide")
            EndTacticalAnim();
    }

    public void EndTacticalAnim()
    {
        if (string.IsNullOrEmpty(_activeAction)) return;
        string ended = _activeAction;
        _tacticalAnimTimer = 0f;
        if (!_proneActive)
            RestoreStandingColliderImmediate();
        else
            ApplyProneColliderPinned();

        FlushGroundSnap(zeroMove: false);
        _activeAction = "";
        LogAction($"{ended} END");
    }

    /// <summary>Slide/JumpOver collider — unchanged path.</summary>
    public void ApplyColliderState(bool prone, bool sliding, bool crouching, bool jumpOverOrSlideAnim)
    {
        if (_controller == null) return;

        if (prone && !jumpOverOrSlideAnim && _tacticalAnimTimer <= 0f)
        {
            ApplyProneColliderPinned();
            return;
        }

        float targetHeight = _standingHeight;
        if (jumpOverOrSlideAnim || _tacticalAnimTimer > 0f)
            targetHeight = TacticalHeight;
        else if (sliding || crouching)
            targetHeight = TacticalHeight;

        targetHeight = Mathf.Clamp(targetHeight, 0.55f, _standingHeight);
        float targetCenterY = _capsuleBottomY + targetHeight * 0.5f;

        bool snapNow = sliding || _tacticalAnimTimer > 0f;
        if (snapNow)
        {
            _controller.height = targetHeight;
            _controller.center = new Vector3(_standingCenter.x, targetCenterY, _standingCenter.z);
        }
        else
        {
            float speed = colliderRestoreSpeed * Time.deltaTime;
            _controller.height = Mathf.Lerp(_controller.height, _standingHeight, speed);
            Vector3 c = _controller.center;
            float standCenterY = _capsuleBottomY + _standingHeight * 0.5f;
            c.y = Mathf.Lerp(c.y, standCenterY, speed);
            _controller.center = c;
        }

        SetStepOffsetSafe(_controller.height > _standingHeight * 0.85f ? 0.5f : 0.05f);
    }

    /// <summary>
    /// Clamps stepOffset to (height - 0.01) so Unity never logs
    /// "Step offset can't be greater than the height of the character
    /// controller". Logs once when a clamp actually fires.
    /// </summary>
    private void SetStepOffsetSafe(float desired)
    {
        if (_controller == null) return;
        float maxStep = Mathf.Max(0f, _controller.height - 0.01f);
        if (desired > maxStep)
        {
            Debug.Log($"[Prone] clamped stepOffset because it exceeded controller height (desired={desired} height={_controller.height})");
            desired = maxStep;
        }
        _controller.stepOffset = desired;
    }

    /// <summary>Prone-only ground correction (called from LateUpdate while C is held).</summary>
    public void EnforceProneGround()
    {
        if (!_proneActive || !CanMoveController())
            return;

        CacheStandingCollider();
        ApplyProneColliderPinned();
        EnforceGroundContact();
        FlushGroundSnap(zeroMove: false);
    }

    public void EnforceGroundContact()
    {
        if (!CanMoveController()) return;

        Vector3 rayOrigin = transform.position + Vector3.up * 0.5f;
        int mask = BuildGroundMask();
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 2.5f, mask, QueryTriggerInteraction.Ignore))
            return;

        if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
            return;

        CacheStandingCollider();
        float capsuleBottomY = transform.position.y + _controller.center.y - _controller.height * 0.5f;
        float floorY = hit.point.y;
        float tolerance = _controller.skinWidth + 0.004f;
        float delta = floorY - capsuleBottomY;
        if (Mathf.Abs(delta) <= tolerance)
            return;

        float moveY = delta + (delta > 0f ? _controller.skinWidth : -_controller.skinWidth * 0.5f);
        moveY = Mathf.Clamp(moveY, -0.35f, 0.35f);
        if (Mathf.Abs(moveY) > 0.001f && CanMoveController())
            _controller.Move(Vector3.up * moveY);
    }

    public void FlushGroundSnap(bool zeroMove)
    {
        if (!CanMoveController()) return;

        if (zeroMove)
            _controller.Move(Vector3.zero);

        Vector3 rayOrigin = transform.position + Vector3.up * 0.35f;
        int mask = BuildGroundMask();
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f, mask, QueryTriggerInteraction.Ignore))
            return;

        if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
            return;

        CacheStandingCollider();
        float targetRootY = hit.point.y - _controller.center.y + _controller.height * 0.5f + _controller.skinWidth;
        float deltaY = targetRootY - transform.position.y;
        deltaY = Mathf.Clamp(deltaY, -0.35f, 0.35f);
        if (Mathf.Abs(deltaY) > 0.001f && CanMoveController())
            _controller.Move(Vector3.up * deltaY);
    }

    public void DisableRootMotionOnAnimators()
    {
        foreach (Animator a in GetComponentsInChildren<Animator>(true))
        {
            if (a != null)
                a.applyRootMotion = false;
        }
    }

    private float GetProneColliderHeight()
    {
        float h = _standingHeight * proneHeightRatio;
        float minH = _standingHeight * 0.30f;
        float maxH = _standingHeight * 0.48f;
        return Mathf.Clamp(h, minH, maxH);
    }

    private void ApplyProneColliderPinned()
    {
        if (_controller == null) return;

        float targetHeight = GetProneColliderHeight();
        float targetCenterY = _capsuleBottomY + targetHeight * 0.5f;
        // ORDER MATTERS: shrink stepOffset BEFORE shrinking height, so during
        // the same frame the controller never has stepOffset > height (which
        // is what triggers Unity's runtime warning).
        SetStepOffsetSafe(0.0f);
        _controller.height = targetHeight;
        _controller.center = new Vector3(_standingCenter.x, targetCenterY, _standingCenter.z);
        SetStepOffsetSafe(0.05f);
    }

    private void SnapColliderImmediate(float height)
    {
        if (_controller == null) return;
        height = Mathf.Clamp(height, 0.55f, _standingHeight);
        _controller.height = height;
        _controller.center = new Vector3(_standingCenter.x, _capsuleBottomY + height * 0.5f, _standingCenter.z);
    }

    private void RestoreStandingColliderImmediate()
    {
        if (_controller == null) return;
        // Restore order: shrink stepOffset first, raise height/center, then
        // set the final stepOffset value (safe-clamped against the new height).
        SetStepOffsetSafe(0.0f);
        _controller.height = _standingHeight;
        _controller.center = _standingCenter;
        _controller.radius = _standingRadius;
        SetStepOffsetSafe(_standingHeight > 1.6f ? 0.5f : 0.05f);
        LogCollider("RESTORE standing");
    }

    private void LogAction(string msg)
    {
        if (!debugLog) return;
        LogCollider(msg);
    }

    private void LogCollider(string phase)
    {
        if (!debugLog || _controller == null) return;
        Debug.Log(
            $"[PlayerTacticalActions] {phase} on {name} " +
            $"h={_controller.height:F2} centerY={_controller.center.y:F2} radius={_controller.radius:F2} " +
            $"proneRatio={proneHeightRatio:F2} visualY={proneVisualYOffset:F2}",
            this);
    }

    private static int BuildGroundMask()
    {
        int mask = Physics.DefaultRaycastLayers;
        int playerLayer = LayerMask.NameToLayer("Player");
        int characterLayer = LayerMask.NameToLayer("Character");
        int hittableLayer = LayerMask.NameToLayer("Hittable");
        if (playerLayer >= 0) mask &= ~(1 << playerLayer);
        if (characterLayer >= 0) mask &= ~(1 << characterLayer);
        if (hittableLayer >= 0) mask &= ~(1 << hittableLayer);
        return mask;
    }
}
