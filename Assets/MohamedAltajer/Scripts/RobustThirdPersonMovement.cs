using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Clean third-person movement controller for melee games.
/// Uses CharacterController because it is more predictable than Rigidbody for
/// close-range combat, root-less animation locomotion, and tight indoor maps.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class RobustThirdPersonMovement : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Animator animator;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 7.0f;
    [SerializeField] private float sprintSpeed = 12.0f;
    [SerializeField] private float rotationSpeed = 720f;
    [SerializeField] private bool instantAcceleration = true;
    [SerializeField] private float acceleration = 9999f;
    [SerializeField] private float deceleration = 9999f;

    [Header("Slide (Warzone-style)")]
    [SerializeField] private bool enableSlide = true;
    [SerializeField] private float slideDuration = 0.45f;
    [SerializeField] private float slideSpeedMultiplier = 1.25f;
    [SerializeField] private float slideCooldown = 0.35f;

    [Header("Tactical Actions (Z/X/C)")]
    [SerializeField] private bool enableTacticalActions = true;
    [SerializeField] private float jumpOverCooldown = 0.9f;
    [SerializeField] private float tacticalSlideCooldown = 0.9f;
    [SerializeField] private float proneSpeedMultiplier = 0.35f;

    [Header("Collider Shape During Animations")]
    [Tooltip("Fraction of standing height used while JumpOver/Slide animations play (0–1). " +
             "Tune until the mesh no longer clips the ground.")]
    [SerializeField] [Range(0.2f, 0.9f)] private float tacticalHeightRatio = 0.50f;
    [Tooltip("Fraction of standing height used while prone.")]
    [SerializeField] [Range(0.1f, 0.6f)] private float proneHeightRatio = 0.28f;
    [Tooltip("Speed at which the collider lerps back to standing after a tactical animation ends.")]
    [SerializeField] private float colliderRestoreSpeed = 12f;

    [Header("Jump / Gravity")]
    [SerializeField] private float maxJumpApexHeight = 1.75f;
    [SerializeField] private float gravity = -24f;
    [SerializeField] private float ascentGravityMultiplier = 0.78f;
    [SerializeField] private float descentGravityMultiplier = 1.62f;
    [SerializeField] private float jumpCutGravityMultiplier = 1.15f;
    [SerializeField] private float coyoteTime = 0.16f;
    [SerializeField] private float jumpBufferTime = 0.14f;
    [SerializeField] private float groundedOffset = 0.1f;
    [SerializeField] private float groundedRadius = 0.28f;
    [SerializeField] private LayerMask groundLayers = ~0;

    private CharacterController _controller;
    private Vector3 _moveVelocity;
    private float _verticalVelocity;
    private bool _isGrounded;
    private bool _isSprinting;
    private float _coyoteTimeCounter;
    private float _jumpBufferCounter;
    private bool _sprintToggleLatch;
    private bool _isSliding;
    private float _slideTimer;
    private float _nextSlideTime;
    private bool _isProne;
    private float _nextJumpOverTime;
    private float _nextTacticalSlideTime;

    // Collider shape tracking
    private float _standingHeight;
    private float _standingCenterY;
    // World-space Y of the capsule bottom in standing pose — held constant so the
    // character never sinks into or lifts off the ground when the collider shrinks.
    private float _capsuleBottom;
    // Counts down the duration of an active tactical animation (JumpOver / Slide).
    private float _tacticalAnimTimer;

    public Vector3 MoveDirection { get; private set; }
    public bool IsGrounded => _isGrounded;
    public bool IsSprinting => _isSprinting;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // Cache the designer-configured standing dimensions from the Inspector.
        // All target heights are derived from these so they stay consistent with
        // whatever the user set on the CharacterController component.
        _standingHeight  = _controller.height;
        _standingCenterY = _controller.center.y;
        // capsuleBottom = center.y - height/2 in local space.
        // Keeping this constant while resizing pins the capsule floor to the ground.
        _capsuleBottom   = _standingCenterY - _standingHeight * 0.5f;
    }

    private void Start()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (cameraTransform == null)
            Debug.LogWarning("[RobustThirdPersonMovement] No camera found — movement will be world-space, not camera-relative.", this);
    }

    private void Update()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        UpdateGroundedState();
        HandleTacticalInput();
        HandleMovement();
        HandleJumpAndGravity();
        ApplyMovement();
        UpdateColliderShape();
        UpdateAnimator();
    }

    private void UpdateGroundedState()
    {
        Vector3 spherePosition = transform.position + Vector3.down * groundedOffset;
        _isGrounded = Physics.CheckSphere(spherePosition, groundedRadius, groundLayers, QueryTriggerInteraction.Ignore);

        if (_isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
    }

    private void HandleMovement()
    {
        Vector2 input = ReadMoveInput();
        bool shiftHeld = Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
        if (Keyboard.current != null && Keyboard.current.leftShiftKey.wasPressedThisFrame && SprintModeRuntime.IsToggleMode)
            _sprintToggleLatch = !_sprintToggleLatch;

        bool kbSprint = SprintModeRuntime.IsToggleMode ? _sprintToggleLatch : shiftHeld;
        bool padSprint = Gamepad.current != null
            && (Gamepad.current.leftShoulder.isPressed || Gamepad.current.leftTrigger.ReadValue() > 0.45f)
            && input.sqrMagnitude > 0.05f;
        _isSprinting = kbSprint || padSprint;

        Vector3 inputDirection = new Vector3(input.x, 0f, input.y);
        inputDirection = Vector3.ClampMagnitude(inputDirection, 1f);

        // Rotate input by camera yaw so W = camera forward, not world +Z
        if (cameraTransform != null && inputDirection.sqrMagnitude > 0.0001f)
            inputDirection = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f) * inputDirection;

        MoveDirection = inputDirection.sqrMagnitude > 0.0001f ? inputDirection.normalized : Vector3.zero;

        // Slide trigger: crouch while sprinting + grounded.
        if (enableSlide && _isGrounded && !_isSliding && Time.time >= _nextSlideTime)
        {
            bool crouchPressed = Keyboard.current != null && Keyboard.current.leftCtrlKey.wasPressedThisFrame;
            if (crouchPressed && _isSprinting && inputDirection.sqrMagnitude > 0.2f)
            {
                _isSliding = true;
                _slideTimer = slideDuration;
                if (PlayerSfx.Instance != null) PlayerSfx.Instance.NotifySlideStart();
            }
        }

        float baseSpeed = _isSprinting ? sprintSpeed : moveSpeed;
        if (_isProne) baseSpeed *= proneSpeedMultiplier;
        float targetSpeed = inputDirection.sqrMagnitude > 0.001f ? baseSpeed : 0f;

        Vector3 targetVelocity = MoveDirection * targetSpeed;
        if (_isSliding)
        {
            // Maintain forward momentum during slide; full air-control is handled elsewhere.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : MoveDirection;
            _moveVelocity = forward * (sprintSpeed * Mathf.Max(1f, slideSpeedMultiplier));
        }
        else if (instantAcceleration)
        {
            // Instant acceleration/deceleration: no ramp-up.
            _moveVelocity = targetVelocity;
        }
        else
        {
            float rate = inputDirection.sqrMagnitude > 0.001f ? acceleration : deceleration;
            _moveVelocity = Vector3.MoveTowards(_moveVelocity, targetVelocity, rate * Time.deltaTime);
        }

        if (MoveDirection.sqrMagnitude > 0.001f && !_isSliding)
        {
            Vector3 faceDirection = GetFacingDirection(input);
            Quaternion targetRotation = Quaternion.LookRotation(faceDirection, Vector3.up);
            // Slerp t = rotationSpeed * deltaTime clamped to 1 keeps it frame-rate
            // independent while producing a smooth organic turn feel.
            float t = Mathf.Clamp01(rotationSpeed / 720f * Time.deltaTime * 10f);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
        }
    }

    private float GetAscentGravityMagnitude()
    {
        return Mathf.Abs(gravity) * Mathf.Max(0.01f, ascentGravityMultiplier);
    }

    private float ComputeLaunchSpeedForApex(float apexHeight)
    {
        return Mathf.Sqrt(2f * GetAscentGravityMagnitude() * Mathf.Max(0f, apexHeight));
    }

    private float ResolveJumpLaunchSpeed()
    {
        return ComputeLaunchSpeedForApex(maxJumpApexHeight);
    }

    private float ResolveJumpCutReleaseSpeed()
    {
        return Mathf.Max(0.5f, ResolveJumpLaunchSpeed() * 0.38f);
    }

    private bool IsAscendingJump()
    {
        return _verticalVelocity > 0.06f;
    }

    private float EvaluateAirborneGravity(bool jumpHeld)
    {
        if (_verticalVelocity > 0f)
        {
            if (!jumpHeld && _verticalVelocity < ResolveJumpCutReleaseSpeed())
                return gravity * jumpCutGravityMultiplier;
            return gravity * ascentGravityMultiplier;
        }

        if (_verticalVelocity < 0f)
            return gravity * descentGravityMultiplier;

        return gravity;
    }

    private void TryLaunchJump()
    {
        if (_coyoteTimeCounter <= 0f || _jumpBufferCounter <= 0f)
            return;
        _verticalVelocity = ResolveJumpLaunchSpeed();
        _coyoteTimeCounter = 0f;
        _jumpBufferCounter = 0f;
        if (PlayerSfx.Instance != null)
            PlayerSfx.Instance.NotifyJump();
    }

    private void HandleJumpAndGravity()
    {
        bool jumpPressed = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
        bool jumpHeld = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;

        if (_isGrounded)
            _coyoteTimeCounter = coyoteTime;
        else
            _coyoteTimeCounter -= Time.deltaTime;

        if (jumpPressed)
        {
            _jumpBufferCounter = jumpBufferTime;
            TryLaunchJump();
        }
        else
            _jumpBufferCounter -= Time.deltaTime;

        if (_jumpBufferCounter > 0f)
            TryLaunchJump();

        if (_isGrounded && _verticalVelocity <= 0f)
            _verticalVelocity = -2f;
        else if (!_isGrounded)
        {
            float appliedGravity = EvaluateAirborneGravity(jumpHeld);
            _verticalVelocity += appliedGravity * Time.deltaTime;
        }

        if (_isGrounded && _tacticalAnimTimer > 0f)
            _verticalVelocity = Mathf.Max(_verticalVelocity, -2f);
    }

    private void ApplyMovement()
    {
        Vector3 horizontal = _moveVelocity;
        horizontal.y = 0f;
        Vector3 vertical = new Vector3(0f, _verticalVelocity * Time.deltaTime, 0f);
        if (IsAscendingJump())
        {
            _controller.Move(vertical);
            _controller.Move(horizontal * Time.deltaTime);
        }
        else
        {
            Vector3 finalVelocity = _moveVelocity;
            finalVelocity.y = _verticalVelocity;
            _controller.Move(finalVelocity * Time.deltaTime);
        }
    }

    private void LateUpdate()
    {
        // Slide timer & cooldown.
        if (_isSliding)
        {
            _slideTimer -= Time.deltaTime;
            if (_slideTimer <= 0f)
            {
                _isSliding = false;
                _nextSlideTime = Time.time + slideCooldown;
            }
        }

        // Ground-contact enforcement — runs after the Animator has evaluated its
        // pose for this frame.  If anything (gravity tunneling, a missed grounded
        // frame, residual root motion) has moved the character below the ground
        // surface, push them back up via the CharacterController so the fix
        // respects slopes and colliders rather than writing transform.position.
        if (_tacticalAnimTimer > 0f || _isProne)
            EnforceGroundContact();
    }

    /// <summary>
    /// Casts a ray downward from hip height to find the ground surface.
    /// If the character has somehow penetrated it, corrects position upward
    /// using CharacterController.Move() so slope/collider logic still applies.
    /// </summary>
    private void EnforceGroundContact()
    {
        // Cast from 0.5 m above the character's feet — enough clearance to hit
        // the ground even if the transform is slightly inside the collider.
        Vector3 rayOrigin = transform.position + Vector3.up * 0.5f;
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit,
                             1.5f, groundLayers, QueryTriggerInteraction.Ignore))
            return;

        float penetration = hit.point.y - transform.position.y;
        if (penetration > 0.005f) // more than 5 mm underground
            _controller.Move(Vector3.up * penetration);
    }

    private void OnAnimatorMove()
    {
        // Block root motion from overriding our rotation/position.
        // We apply movement manually in ApplyMovement(), so root motion
        // would cause the character to drift or rotate incorrectly.
    }

    private void UpdateAnimator()
    {
        if (animator == null)
            return;

        // Prevent the Animator from overriding transform at runtime.
        animator.applyRootMotion = false;

        float horizontalSpeed = new Vector3(_moveVelocity.x, 0f, _moveVelocity.z).magnitude;
        float normalizedSpeed = sprintSpeed > 0.01f ? horizontalSpeed / sprintSpeed : 0f;

        // Damp prevents animation popping on instant start/stop
        animator.SetFloat("Speed", normalizedSpeed, 0.1f, Time.deltaTime);
        animator.SetBool("IsGrounded", _isGrounded);
        animator.SetBool("IsSprinting", _isSprinting && horizontalSpeed > 0.1f);
        animator.SetBool("IsProne", _isProne);

        // Footstep cadence — driven from real horizontal speed so sprint
        // ticks faster than walk and stops while airborne / standing still.
        if (PlayerSfx.Instance != null)
            PlayerSfx.Instance.TickFootsteps(_isGrounded, _isSprinting, horizontalSpeed);
    }

    private void HandleTacticalInput()
    {
        if (!enableTacticalActions) return;
        Keyboard k = Keyboard.current;
        if (k == null) return;

        // Z = JumpOver / vault
        if (k.zKey.wasPressedThisFrame && Time.time >= _nextJumpOverTime && _isGrounded && !_isProne)
        {
            _nextJumpOverTime = Time.time + jumpOverCooldown;
            if (animator != null) animator.SetTrigger("JumpOver");
            // Snap the collider to tactical size immediately — frame 0 of the
            // animation already bends the mesh downward, so we can't afford to
            // wait even one frame before resizing.
            SnapColliderToTactical();
            _tacticalAnimTimer = jumpOverCooldown;
        }

        // X = Slide (tactical)
        if (k.xKey.wasPressedThisFrame && Time.time >= _nextTacticalSlideTime && _isGrounded && !_isProne)
        {
            _nextTacticalSlideTime = Time.time + tacticalSlideCooldown;
            if (animator != null) animator.SetTrigger("Slide");
            if (PlayerSfx.Instance != null) PlayerSfx.Instance.NotifySlideStart();
            SnapColliderToTactical();
            _tacticalAnimTimer = tacticalSlideCooldown;
        }

        // C = toggle Prone
        if (k.cKey.wasPressedThisFrame)
        {
            _isProne = !_isProne;
            if (animator != null) animator.SetBool("IsProne", _isProne);
        }
    }

    /// <summary>
    /// Immediately resizes the CharacterController to the tactical (crouched)
    /// dimensions so the capsule bottom never exceeds the mesh during a JumpOver
    /// or Slide animation.  Called synchronously in the same frame the key fires.
    /// </summary>
    private void SnapColliderToTactical()
    {
        float h = _standingHeight * tacticalHeightRatio;
        _controller.height   = h;
        Vector3 c = _controller.center;
        // Pin the capsule bottom: newCenter.y = capsuleBottom + newHeight/2
        c.y = _capsuleBottom + h * 0.5f;
        _controller.center   = c;
    }

    /// <summary>
    /// Called every Update. Drives the CharacterController height and center
    /// toward the correct target for the current locomotion state, then lerps
    /// back to standing once a tactical animation has finished.
    ///
    /// Priority: tactical animation > prone > standing.
    /// The capsule bottom (_capsuleBottom) is kept constant throughout so the
    /// character never sinks into or lifts off the ground when the shape changes.
    /// </summary>
    private void UpdateColliderShape()
    {
        // Tick the tactical animation timer.
        if (_tacticalAnimTimer > 0f)
            _tacticalAnimTimer -= Time.deltaTime;

        float targetHeight;
        if (_tacticalAnimTimer > 0f)
            targetHeight = _standingHeight * tacticalHeightRatio;
        else if (_isProne)
            targetHeight = _standingHeight * proneHeightRatio;
        else
            targetHeight = _standingHeight;

        // Compute the center that keeps the capsule bottom fixed at ground level.
        float targetCenterY = _capsuleBottom + targetHeight * 0.5f;

        // Lerp smoothly back to standing/prone; we already snapped on entry
        // so the lerp only matters during restoration (avoids a visual pop).
        float speed = (_tacticalAnimTimer > 0f || _isProne) ? colliderRestoreSpeed * 2f : colliderRestoreSpeed;
        _controller.height = Mathf.Lerp(_controller.height, targetHeight, speed * Time.deltaTime);
        Vector3 center = _controller.center;
        center.y = Mathf.Lerp(center.y, targetCenterY, speed * Time.deltaTime);
        _controller.center = center;
    }

    /// <summary>
    /// Yaw target for rotation. Pure backward input walks backward without a 180° turn.
    /// </summary>
    private Vector3 GetFacingDirection(Vector2 input)
    {
        Vector2 facingInput = input;
        if (facingInput.y < -0.01f)
            facingInput.y = 0f;

        if (facingInput.sqrMagnitude < 0.0001f)
            facingInput = Vector2.up;

        Vector3 dir = new Vector3(facingInput.x, 0f, facingInput.y);
        if (cameraTransform != null && dir.sqrMagnitude > 0.0001f)
            dir = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f) * dir;

        return dir.sqrMagnitude > 0.0001f ? dir.normalized : transform.forward;
    }

    private static Vector2 ReadMoveInput()
    {
        if (Keyboard.current == null)
            return Vector2.zero;

        Vector2 input = Vector2.zero;
        Keyboard k = Keyboard.current;
        if (k.wKey.isPressed || k.upArrowKey.isPressed || k.numpad8Key.isPressed) input.y += 1f;
        if (k.sKey.isPressed || k.downArrowKey.isPressed || k.numpad2Key.isPressed) input.y -= 1f;
        if (k.dKey.isPressed || k.rightArrowKey.isPressed || k.numpad6Key.isPressed) input.x += 1f;
        if (k.aKey.isPressed || k.leftArrowKey.isPressed || k.numpad4Key.isPressed) input.x -= 1f;
        return input;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Vector3 spherePosition = transform.position + Vector3.down * groundedOffset;
        Gizmos.DrawWireSphere(spherePosition, groundedRadius);
    }
}
