using UnityEngine;

public class KeyboardMovementInput : MonoBehaviour, IMovementInput
{
    [SerializeField] private string horizontalAxis = "Horizontal";
    [SerializeField] private string verticalAxis = "Vertical";
    [SerializeField] private bool normalizeDiagonalInput = true;

    public Vector2 ReadAxis()
    {
        Vector2 axis = new Vector2(
            Input.GetAxisRaw(horizontalAxis),
            Input.GetAxisRaw(verticalAxis));

        if (normalizeDiagonalInput && axis.sqrMagnitude > 1f)
            axis.Normalize();

        return axis;
    }
}
