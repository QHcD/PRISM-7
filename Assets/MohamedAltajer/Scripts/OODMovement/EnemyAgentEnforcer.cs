using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyAgentEnforcer : MonoBehaviour
{
    [SerializeField] private float radius = 0.4f;
    [SerializeField] private float height = 1.8f;
    [SerializeField] private float baseOffset = 0f;
    [SerializeField] private float acceleration = 12f;
    [SerializeField] private float angularSpeed = 360f;
    [SerializeField] private float stoppingDistance = 1.4f;
    [SerializeField] private ObstacleAvoidanceType avoidance = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
    [SerializeField] private bool autoStopWhenOffMesh = true;

    private NavMeshAgent _agent;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        ApplyAgentSettings();
        EnsureCollider();
        EnsureKinematicRigidbody();
    }

    private void ApplyAgentSettings()
    {
        if (_agent == null) return;
        _agent.radius = radius;
        _agent.height = height;
        _agent.baseOffset = baseOffset;
        _agent.acceleration = acceleration;
        _agent.angularSpeed = angularSpeed;
        _agent.stoppingDistance = stoppingDistance;
        _agent.obstacleAvoidanceType = avoidance;
        _agent.autoBraking = true;
        _agent.autoRepath = true;
        _agent.updatePosition = true;
        _agent.updateRotation = true;
    }

    private void EnsureCollider()
    {
        Collider any = GetComponent<Collider>();
        if (any != null) return;

        CapsuleCollider cap = gameObject.AddComponent<CapsuleCollider>();
        cap.isTrigger = false;
        cap.radius = radius;
        cap.height = height;
        cap.center = new Vector3(0f, height * 0.5f, 0f);
    }

    private void EnsureKinematicRigidbody()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private void Update()
    {
        if (_agent == null) return;
        if (autoStopWhenOffMesh && !_agent.isOnNavMesh)
        {
            _agent.ResetPath();
        }
    }

    public bool IsOnNavMesh => _agent != null && _agent.isOnNavMesh;
}
