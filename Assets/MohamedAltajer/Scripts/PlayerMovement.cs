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

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    private void Update()
    {
        float horizontal = 0f;
        float vertical = 0f;
        
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) horizontal -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) horizontal += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) vertical -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) vertical += 1f;
        }

        Gamepad gp = Gamepad.current;
        if (gp != null)
        {
            Vector2 stick = gp.leftStick.ReadValue();
            horizontal += stick.x;
            vertical += stick.y;
        }

        // ── STEP 2: Build and normalise the movement vector ───────────────────
        // Combine axes into a flat (Y = 0) world-space direction vector.
        // Without Normalize(), pressing W+D would move ~1.41× faster than W alone.
        Vector3 direction = new Vector3(horizontal, 0f, vertical).normalized;

        // ── STEP 3: Gravity ───────────────────────────────────────────────────
        ApplyGravity();

        // ── STEP 4: Rotation + Movement (only when there is input) ───────────
        if (direction.magnitude >= 0.1f)
        {
            // Backward (S / down) moves without a 180° turn; strafe while backing
            // still updates yaw from the lateral axis only.
            if (vertical < -0.01f)
            {
                Vector3 lateralFace = new Vector3(horizontal, 0f, 0f);
                if (lateralFace.sqrMagnitude >= 0.01f)
                    RotateTowards(lateralFace.normalized);
            }
            else
                RotateTowards(direction);

            Vector3 moveDir = direction * moveSpeed;
            moveDir.y = _verticalVelocity;
            _controller.Move(moveDir * Time.deltaTime);
        }
        else
        {
            // No input: apply gravity only so the character doesn't float.
            _controller.Move(new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);
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
