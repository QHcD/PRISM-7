using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Focused third-person movement controller.
/// Rotation uses Mathf.Atan2 + SmoothDampAngle so the model always faces
/// exactly the direction it is walking — no camera logic included.
///
/// SETUP CHECKLIST (must be correct or the script fights itself):
///   1. This script lives on the PARENT (root) GameObject.
///   2. The 3D model mesh is a CHILD of that parent.
///   3. Animator → "Apply Root Motion" = UNCHECKED.
///   4. No Rigidbody on this object (we use CharacterController).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Movement")]
    [Tooltip("World-units per second while walking.")]
    [SerializeField] private float moveSpeed = 6f;

    [Header("Rotation")]
    [Tooltip("Seconds to smooth from the current angle to the target angle. " +
             "Lower = snappier turn. 0.05–0.15 feels natural for most games.")]
    [SerializeField] private float turnSmoothTime = 0.1f;

    [Header("Gravity")]
    [Tooltip("Downward acceleration when airborne (positive value, applied as negative Y).")]
    [SerializeField] private float gravity = 20f;

    // ── Private state ─────────────────────────────────────────────────────────

    private CharacterController _controller;
    private Transform _cameraTransform;

    /// <summary>
    /// Current Y velocity (negative = falling).
    /// Kept between frames so gravity accelerates naturally.
    /// </summary>
    private float _verticalVelocity;

    /// <summary>
    /// Internal velocity reference required by SmoothDampAngle.
    /// Must persist between frames — do NOT reset it manually.
    /// </summary>
    private float _turnSmoothVelocity;

    /// <summary>Gamepad stick deadzone to suppress controller drift.</summary>
    private const float GamepadDeadzone = 0.2f;

    /// <summary>Enable temporary diagnostic logs (toggle off once verified).</summary>
    [SerializeField] private bool debugMoveLogs = true;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        CacheCameraTransform();
    }

    private void CacheCameraTransform()
    {
        Camera cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();
        _cameraTransform = cam != null ? cam.transform : null;
    }

    private void Update()
    {
        // ── STEP 1: Read input (keyboard takes priority; gamepad with deadzone) ─
        float horizontal = 0f;
        float vertical = 0f;
        bool keyboardActive = false;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  { horizontal -= 1f; keyboardActive = true; }
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) { horizontal += 1f; keyboardActive = true; }
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  { vertical   -= 1f; keyboardActive = true; }
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    { vertical   += 1f; keyboardActive = true; }
        }

        // Only sample gamepad when keyboard is silent — prevents stick drift from
        // overriding intentional WASD/arrow input.
        if (!keyboardActive)
        {
            Gamepad gp = Gamepad.current;
            if (gp != null)
            {
                Vector2 stick = gp.leftStick.ReadValue();
                if (stick.magnitude < GamepadDeadzone) stick = Vector2.zero;
                horizontal = stick.x;
                vertical   = stick.y;
            }
        }

        Vector2 rawInput = new Vector2(horizontal, vertical);
        if (rawInput.magnitude > 1f) rawInput = rawInput.normalized;

        // ── STEP 2: Build camera-relative movement basis (yaw-only) ───────────
        // Project camera forward & right onto the XZ plane so vertical pitch
        // doesn't scale movement when the camera tilts up/down.
        Vector3 camForward = Vector3.forward;
        Vector3 camRight   = Vector3.right;
        if (_cameraTransform == null) CacheCameraTransform();
        if (_cameraTransform != null)
        {
            camForward = _cameraTransform.forward; camForward.y = 0f; camForward.Normalize();
            camRight   = _cameraTransform.right;   camRight.y   = 0f; camRight.Normalize();
            if (camForward.sqrMagnitude < 0.001f) camForward = Vector3.forward;
            if (camRight.sqrMagnitude   < 0.001f) camRight   = Vector3.right;
        }

        Vector3 moveWorld = (camForward * rawInput.y) + (camRight * rawInput.x);
        if (moveWorld.magnitude > 1f) moveWorld = moveWorld.normalized;

        // ── STEP 3: Gravity ───────────────────────────────────────────────────
        ApplyGravity();

        // ── STEP 4: Rotation + Movement (only when there is input) ───────────
        if (moveWorld.sqrMagnitude >= 0.01f)
        {
            // Believable backward movement: when pressing S/Down (vertical < 0),
            // do NOT spin the player 180°. Instead, only update facing from the
            // lateral (strafe) component if any — so the player walks backward
            // while still showing their front. Pure forward/strafe → face motion.
            if (rawInput.y < -0.05f)
            {
                // Build a "lateral only" world vector (strafe component on camera basis).
                Vector3 lateralWorld = camRight * rawInput.x;
                if (lateralWorld.sqrMagnitude >= 0.01f)
                    RotateTowards(lateralWorld.normalized);
                // else: pure backward — keep current facing (no rotation).
            }
            else
            {
                RotateTowards(moveWorld);
            }

            Vector3 step = moveWorld * moveSpeed;
            step.y = _verticalVelocity;
            _controller.Move(step * Time.deltaTime);
        }
        else
        {
            // No input: apply gravity only so the character doesn't float.
            _controller.Move(new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);
        }

        if (debugMoveLogs && rawInput.sqrMagnitude > 0.0001f)
        {
            Debug.Log($"[MoveFix] input=({rawInput.x:F2},{rawInput.y:F2}) " +
                      $"moveWorld=({moveWorld.x:F2},{moveWorld.y:F2},{moveWorld.z:F2}) " +
                      $"facing={transform.eulerAngles.y:F1}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Calculates the exact world-space yaw angle from the input vector using
    /// Atan2, then uses SmoothDampAngle to rotate the transform toward it.
    ///
    /// WHY Atan2 instead of Quaternion.LookRotation?
    ///   Atan2 works in degree-space so SmoothDampAngle can interpolate the
    ///   shortest path between any two angles — including the 359° → 1° wrap.
    ///   LookRotation + Slerp can sometimes take the long way around.
    /// </summary>
    private void RotateTowards(Vector3 direction)
    {
        // Atan2(x, z) gives the signed angle (degrees) from world +Z to the
        // direction vector, which is the yaw we want the character to face.
        float targetAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        // SmoothDampAngle eases the current Y-euler toward targetAngle,
        // correctly wrapping across the 0/360 boundary.
        // _turnSmoothVelocity is the internal damper state — keep it as a field.
        float smoothedAngle = Mathf.SmoothDampAngle(
            transform.eulerAngles.y, // current yaw
            targetAngle,             // desired yaw
            ref _turnSmoothVelocity, // internal velocity (mutated by Unity)
            turnSmoothTime           // time to reach target (seconds)
        );

        // Apply only yaw rotation; X and Z stay at 0 so the model stays upright.
        transform.rotation = Quaternion.Euler(0f, smoothedAngle, 0f);
    }

    /// <summary>
    /// Accumulates downward velocity when airborne.
    /// The small constant while grounded keeps CharacterController.isGrounded
    /// reliable — without it the controller briefly reports "not grounded" each frame.
    /// </summary>
    private void ApplyGravity()
    {
        if (_controller.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;          // small sticking force, not 0
        else
            _verticalVelocity -= gravity * Time.deltaTime; // accelerate downward
    }
}
