using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(KeyboardMovementInput))]
public class NavMeshConstrainedMover : MonoBehaviour, ICharacterMover
{
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float gravity = 25f;
    [SerializeField] private float stickToGroundForce = 4f;
    [SerializeField] private float rotationSmoothTime = 0.1f;
    [SerializeField] private float maxFallSpeed = 30f;
    [SerializeField] private float navSampleRadius = 0.6f;
    [SerializeField] private float navEdgePushback = 0.05f;
    [SerializeField] private int navAreaMask = NavMesh.AllAreas;
    [SerializeField] private bool cameraRelative = true;
    [SerializeField] private float inputStopThreshold = 0.1f;
    [SerializeField] private bool clampToNavMesh = true;

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

        Vector3 horizontalDir = BuildHorizontalDirection(axis);
        ApplyRotation(horizontalDir);
        ApplyVerticalVelocity();

        float speed = hasInput ? walkSpeed : 0f;
        Vector3 horizontalMotion = horizontalDir * speed;
        horizontalMotion.y = 0f;
        _lastHorizontalSpeed = horizontalMotion.magnitude;

        Vector3 desired = horizontalMotion;
        desired.y = _verticalVelocity;

        Vector3 desiredDelta = desired * Time.deltaTime;
        Vector3 candidatePosition = transform.position + desiredDelta;

        if (clampToNavMesh && hasInput)
        {
            candidatePosition = ClampToNavMesh(candidatePosition);
            desiredDelta = candidatePosition - transform.position;
        }

        _controller.Move(desiredDelta);
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

        Vector3 delta = motion * Time.deltaTime;
        if (clampToNavMesh && speed > 0.01f)
        {
            Vector3 candidate = transform.position + delta;
            candidate = ClampToNavMesh(candidate);
            delta = candidate - transform.position;
        }
        _controller.Move(delta);
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

    private Vector3 ClampToNavMesh(Vector3 candidate)
    {
        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, navSampleRadius, navAreaMask))
        {
            Vector3 result = hit.position;

            if (NavMesh.FindClosestEdge(result, out NavMeshHit edge, navAreaMask))
            {
                if (edge.distance < navEdgePushback)
                {
                    Vector3 inward = (result - edge.position);
                    inward.y = 0f;
                    if (inward.sqrMagnitude > 0.0001f)
                    {
                        inward.Normalize();
                        result += inward * (navEdgePushback - edge.distance);
                    }
                }
            }

            result.y = candidate.y;
            return result;
        }

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit fallback, navSampleRadius * 2f, navAreaMask))
        {
            Vector3 safe = fallback.position;
            safe.y = candidate.y;
            return safe;
        }

        return candidate;
    }
}
