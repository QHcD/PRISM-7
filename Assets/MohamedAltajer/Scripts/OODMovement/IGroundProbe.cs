using UnityEngine;

public interface IGroundProbe
{
    bool IsGrounded { get; }
    Vector3 GroundNormal { get; }
}
