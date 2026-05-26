using UnityEngine;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(KeyboardMovementInput))]
public class GroundedHorizontalMover : MonoBehaviour, ICharacterMover
{
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float gravity = 25f;
    [SerializeField] private float stickToGroundForce = 4f;
    [SerializeField] private float rotationSmoothTime = 0.1f;
    [SerializeField] private float maxFallSpeed = 30f;
    [SerializeField] private bool cameraRelative = true;
    [SerializeField] private float inputStopThreshold = 0.1f;

    private CharacterController _controller;
    private IMovementInput _input;
    private IGroundProbe _groundProbe;
    private Transform _cameraTransform;
    private float _verticalVelocity;
    private float _turnVelocity;
    private float _lastHorizontalSpeed;

    public float CurrentSpeed => _lastHorizontalSpeed;
    public bool IsGrounded => DetectGrounded();

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _input = GetComponent<IMovementInput>();
        _groundProbe = GetComponent<IGroundProbe>();
        ResolveCamera();
    }

    private void ResolveCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();
        _cameraTransform = cam != null ? cam.transform : null;
    }

    private void Update()
    {
        if (_controller == null || !_controller.enabled) return;
        if (_input == null) return;

        Vector2 axis = _input.ReadAxis();
        bool hasInput = axis.magnitude > inputStopThreshold;

        Vector3 horizontal = BuildHorizontalDirection(axis);

        ApplyRotation(horizontal);
        ApplyVerticalVelocity();

        float speed = hasInput ? walkSpeed : 0f;
        Vector3 horizontalMotion = horizontal * speed;
        horizontalMotion.y = 0f;
        _lastHorizontalSpeed = horizontalMotion.magnitude;

        Vector3 finalMotion = horizontalMotion;
        finalMotion.y = _verticalVelocity;

        _controller.Move(finalMotion * Time.deltaTime);
    }

    public void Move(Vector3 worldDirection, float speed)
    {
        if (_controller == null || !_controller.enabled) return;

        Vector3 horizontal = worldDirection;
        horizontal.y = 0f;
        if (horizontal.sqrMagnitude >= 0.0001f) horizontal.Normalize();

        ApplyRotation(horizontal);
        ApplyVerticalVelocity();

        Vector3 motion = horizontal * speed;
        motion.y = 0f;
        _lastHorizontalSpeed = motion.magnitude;
        motion.y = _verticalVelocity;
        _controller.Move(motion * Time.deltaTime);
    }

    private Vector3 BuildHorizontalDirection(Vector2 axis)
    {
        if (!cameraRelative || _cameraTransform == null)
        {
            Vector3 worldFlat = new Vector3(axis.x, 0f, axis.y);
            return worldFlat.sqrMagnitude > 1f ? worldFlat.normalized : worldFlat;
        }

        Vector3 forward = _cameraTransform.forward;
        Vector3 right   = _cameraTransform.right;
        forward.y = 0f;
        right.y   = 0f;

        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward; else forward.Normalize();
        if (right.sqrMagnitude   < 0.0001f) right   = Vector3.right;   else right.Normalize();

        Vector3 dir = (forward * axis.y) + (right * axis.x);
        dir.y = 0f;
        if (dir.sqrMagnitude > 1f) dir = dir.normalized;
        return dir;
    }

    private void ApplyRotation(Vector3 horizontal)
    {
        if (horizontal.sqrMagnitude < 0.0001f) return;
        float targetAngle = Mathf.Atan2(horizontal.x, horizontal.z) * Mathf.Rad2Deg;
        float smoothed = Mathf.SmoothDampAngle(
            transform.eulerAngles.y, targetAngle, ref _turnVelocity, rotationSmoothTime);
        transform.rotation = Quaternion.Euler(0f, smoothed, 0f);
    }

    private void ApplyVerticalVelocity()
    {
        if (DetectGrounded())
        {
            if (_verticalVelocity < 0f)
                _verticalVelocity = -stickToGroundForce;
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
}
