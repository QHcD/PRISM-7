using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public class LocomotionManager : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private Transform cameraTransform;

    [SerializeField] private float walkSpeed = 2.2f;
    [SerializeField] private float runSpeed = 5.5f;
    [SerializeField] private float rotationSmoothTime = 0.08f;
    [SerializeField] private float inputSmoothTime = 0.08f;
    [SerializeField] private float gravity = 25f;
    [SerializeField] private float stickToGroundForce = 4f;
    [SerializeField] private float maxFallSpeed = 30f;
    [SerializeField] private float inputDeadzone = 0.12f;

    [SerializeField] private bool cameraRelativeInput = true;
    [SerializeField] private bool driveDirectional = true;
    [SerializeField] private bool runWhenSprintHeld = true;

    [SerializeField] private string speedParam = "MoveSpeed";
    [SerializeField] private string directionParam = "Direction";
    [SerializeField] private string velocityXParam = "VelocityX";
    [SerializeField] private string velocityZParam = "VelocityZ";
    [SerializeField] private string groundedParam = "IsGrounded";

    [SerializeField] private bool enableFootIK = true;
    [SerializeField] private LayerMask footIKMask = ~0;
    [SerializeField] private float footIKRayDistance = 1.2f;
    [SerializeField] private float footIKVerticalOffset = 0.08f;
    [SerializeField] private float footIKWeightLerp = 12f;

    private CharacterController _controller;
    private IMovementInput _input;
    private IGroundProbe _groundProbe;

    private Vector2 _smoothedInput;
    private Vector2 _inputVelocity;
    private float _turnVelocity;
    private float _verticalVelocity;
    private float _currentLeftFootWeight;
    private float _currentRightFootWeight;

    private int _hSpeed;
    private int _hDirection;
    private int _hVelX;
    private int _hVelZ;
    private int _hGrounded;
    private bool _hasSpeed;
    private bool _hasDirection;
    private bool _hasVelX;
    private bool _hasVelZ;
    private bool _hasGrounded;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        _input = GetComponent<IMovementInput>();
        _groundProbe = GetComponent<IGroundProbe>();
        ResolveCamera();
        CacheParameters();
        if (animator != null)
        {
            animator.applyRootMotion = false;
        }
    }

    private void ResolveCamera()
    {
        if (cameraTransform != null) return;
        Camera cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();
        cameraTransform = cam != null ? cam.transform : null;
    }

    private void CacheParameters()
    {
        _hSpeed = Animator.StringToHash(speedParam);
        _hDirection = Animator.StringToHash(directionParam);
        _hVelX = Animator.StringToHash(velocityXParam);
        _hVelZ = Animator.StringToHash(velocityZParam);
        _hGrounded = Animator.StringToHash(groundedParam);

        _hasSpeed = false;
        _hasDirection = false;
        _hasVelX = false;
        _hasVelZ = false;
        _hasGrounded = false;
        if (animator == null || animator.runtimeAnimatorController == null) return;

        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.nameHash == _hSpeed     && p.type == AnimatorControllerParameterType.Float) _hasSpeed = true;
            if (p.nameHash == _hDirection && p.type == AnimatorControllerParameterType.Float) _hasDirection = true;
            if (p.nameHash == _hVelX      && p.type == AnimatorControllerParameterType.Float) _hasVelX = true;
            if (p.nameHash == _hVelZ      && p.type == AnimatorControllerParameterType.Float) _hasVelZ = true;
            if (p.nameHash == _hGrounded  && p.type == AnimatorControllerParameterType.Bool)  _hasGrounded = true;
        }
    }

    private void Update()
    {
        if (_controller == null || !_controller.enabled) return;

        Vector2 raw = ReadInput();
        if (raw.magnitude < inputDeadzone) raw = Vector2.zero;
        if (raw.magnitude > 1f) raw = raw.normalized;

        _smoothedInput = Vector2.SmoothDamp(_smoothedInput, raw, ref _inputVelocity, inputSmoothTime);

        bool hasInput = _smoothedInput.magnitude > inputDeadzone;
        bool sprintHeld = runWhenSprintHeld && IsSprintHeld();
        float inputMagnitude = Mathf.Clamp01(_smoothedInput.magnitude);
        float targetSpeed = sprintHeld ? Mathf.Lerp(walkSpeed, runSpeed, inputMagnitude) : walkSpeed * inputMagnitude;

        Vector3 worldDir = BuildWorldDirection(_smoothedInput);
        ApplyFacing(worldDir);
        ApplyGravity();

        bool groundedBeforeMove = _controller.isGrounded;
        Vector3 horizontalMotion = (hasInput && groundedBeforeMove) ? worldDir * targetSpeed : Vector3.zero;
        Vector3 motion = horizontalMotion;
        motion.y = _verticalVelocity;
        _controller.Move(motion * Time.deltaTime);

        Vector3 appliedHorizontalVelocity = _controller.velocity;
        appliedHorizontalVelocity.y = 0f;
        if (!_controller.isGrounded) appliedHorizontalVelocity = Vector3.zero;
        FeedAnimator(appliedHorizontalVelocity);
    }

    private void OnAnimatorMove()
    {
        return;
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (!enableFootIK || animator == null) return;
        DriveFootIK(AvatarIKGoal.LeftFoot, ref _currentLeftFootWeight);
        DriveFootIK(AvatarIKGoal.RightFoot, ref _currentRightFootWeight);
    }

    private void DriveFootIK(AvatarIKGoal goal, ref float currentWeight)
    {
        Vector3 bonePos = animator.GetIKPosition(goal);
        Vector3 origin = bonePos + Vector3.up * 0.5f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, footIKRayDistance, footIKMask, QueryTriggerInteraction.Ignore))
        {
            float targetWeight = DetectGrounded() ? 1f : 0f;
            currentWeight = Mathf.Lerp(currentWeight, targetWeight, Time.deltaTime * footIKWeightLerp);

            Vector3 ikPos = hit.point + Vector3.up * footIKVerticalOffset;
            Quaternion ikRot = Quaternion.FromToRotation(Vector3.up, hit.normal) * animator.GetIKRotation(goal);

            animator.SetIKPositionWeight(goal, currentWeight);
            animator.SetIKRotationWeight(goal, currentWeight);
            animator.SetIKPosition(goal, ikPos);
            animator.SetIKRotation(goal, ikRot);
        }
        else
        {
            currentWeight = Mathf.Lerp(currentWeight, 0f, Time.deltaTime * footIKWeightLerp);
            animator.SetIKPositionWeight(goal, currentWeight);
            animator.SetIKRotationWeight(goal, currentWeight);
        }
    }

    private Vector2 ReadInput()
    {
        if (_input != null) return _input.ReadAxis();
        return Vector2.zero;
    }

    private bool IsSprintHeld()
    {
        UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.leftShiftKey.isPressed) return true;
        UnityEngine.InputSystem.Gamepad gp = UnityEngine.InputSystem.Gamepad.current;
        if (gp != null && gp.leftStickButton.isPressed) return true;
        return false;
    }

    private Vector3 BuildWorldDirection(Vector2 axis)
    {
        if (!cameraRelativeInput || cameraTransform == null)
        {
            Vector3 flat = new Vector3(axis.x, 0f, axis.y);
            return flat.sqrMagnitude > 1f ? flat.normalized : flat;
        }

        Vector3 fwd = cameraTransform.forward; fwd.y = 0f;
        Vector3 rgt = cameraTransform.right;   rgt.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward; else fwd.Normalize();
        if (rgt.sqrMagnitude < 0.0001f) rgt = Vector3.right;   else rgt.Normalize();

        Vector3 dir = (fwd * axis.y) + (rgt * axis.x);
        dir.y = 0f;
        if (dir.sqrMagnitude > 1f) dir = dir.normalized;
        return dir;
    }

    private void ApplyFacing(Vector3 worldDir)
    {
        if (worldDir.sqrMagnitude < 0.0001f) return;
        float targetAngle = Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
        float smoothed = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetAngle, ref _turnVelocity, rotationSmoothTime);
        transform.rotation = Quaternion.Euler(0f, smoothed, 0f);
    }

    private void ApplyGravity()
    {
        if (DetectGrounded())
        {
            if (_verticalVelocity < 0f) _verticalVelocity = -stickToGroundForce;
        }
        else
        {
            _verticalVelocity -= gravity * Time.deltaTime;
            if (_verticalVelocity < -maxFallSpeed) _verticalVelocity = -maxFallSpeed;
        }
    }

    private bool DetectGrounded()
    {
        if (_groundProbe != null) return _groundProbe.IsGrounded;
        return _controller != null && _controller.isGrounded;
    }

    private void FeedAnimator(Vector3 appliedHorizontalVelocity)
    {
        if (animator == null) return;

        float horizontalSpeed = appliedHorizontalVelocity.magnitude;
        float moveSpeedValue = walkSpeed > 0.0001f ? horizontalSpeed / walkSpeed : 0f;

        ResolveInputBasis(out Vector3 fwd, out Vector3 rgt);

        float invRunSpeed = runSpeed > 0.0001f ? 1f / runSpeed : 0f;
        float velocityX = horizontalSpeed > 0.0001f ? Vector3.Dot(appliedHorizontalVelocity, rgt) * invRunSpeed : 0f;
        float velocityZ = horizontalSpeed > 0.0001f ? Vector3.Dot(appliedHorizontalVelocity, fwd) * invRunSpeed : 0f;

        velocityX = Mathf.Clamp(velocityX, -1f, 1f);
        velocityZ = Mathf.Clamp(velocityZ, -1f, 1f);

        if (_hasSpeed) animator.SetFloat(_hSpeed, moveSpeedValue);
        if (_hasGrounded) animator.SetBool(_hGrounded, _controller != null && _controller.isGrounded);
        if (_hasVelX) animator.SetFloat(_hVelX, velocityX);
        if (_hasVelZ) animator.SetFloat(_hVelZ, velocityZ);

        if (driveDirectional && _hasDirection)
        {
            float dirAngle = ComputeDirectionalAngle(appliedHorizontalVelocity);
            animator.SetFloat(_hDirection, dirAngle);
        }
    }

    private float ComputeDirectionalAngle(Vector3 worldDir)
    {
        if (worldDir.sqrMagnitude < 0.0001f) return 0f;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return 0f;
        fwd.Normalize();
        Vector3 dir = worldDir; dir.y = 0f; dir.Normalize();
        float signed = Vector3.SignedAngle(fwd, dir, Vector3.up);
        return signed;
    }

    private void ResolveInputBasis(out Vector3 forward, out Vector3 right)
    {
        if (!cameraRelativeInput || cameraTransform == null)
        {
            forward = Vector3.forward;
            right = Vector3.right;
            return;
        }

        forward = cameraTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        else forward.Normalize();

        right = cameraTransform.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
        else right.Normalize();
    }
}
