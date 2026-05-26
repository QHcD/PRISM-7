using UnityEngine;

public interface ICharacterMover
{
    float CurrentSpeed { get; }
    bool IsGrounded { get; }
    void Move(Vector3 worldDirection, float speed);
}
