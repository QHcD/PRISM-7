using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyClipGuard : MonoBehaviour
{
    [SerializeField] private float clampSampleRadius = 0.8f;
    [SerializeField] private float edgePushback = 0.1f;
    [SerializeField] private float pushFromCollisionForce = 2f;
    [SerializeField] private LayerMask wallMask = ~0;
    [SerializeField] private bool disableRootMotion = true;
    [SerializeField] private bool keepColliderEnabled = true;
    [SerializeField] private float capsuleRadius = 0.4f;
    [SerializeField] private float capsuleHeight = 1.8f;

    private NavMeshAgent _agent;
    private Animator _animator;
    private Collider _bodyCollider;
    private Rigidbody _rb;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponentInChildren<Animator>();
        EnsureBodyCollider();
        EnsureKinematicRigidbody();
        EnforceAgentSetup();
        if (disableRootMotion && _animator != null) _animator.applyRootMotion = false;
    }

    private void EnsureBodyCollider()
    {
        _bodyCollider = GetComponent<Collider>();
        if (_bodyCollider == null)
        {
            CapsuleCollider cap = gameObject.AddComponent<CapsuleCollider>();
            cap.radius = capsuleRadius;
            cap.height = capsuleHeight;
            cap.center = new Vector3(0f, capsuleHeight * 0.5f, 0f);
            cap.isTrigger = false;
            _bodyCollider = cap;
        }
        else
        {
            _bodyCollider.isTrigger = false;
            _bodyCollider.enabled = true;
        }
    }

    private void EnsureKinematicRigidbody()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private void EnforceAgentSetup()
    {
        if (_agent == null) return;
        _agent.radius = Mathf.Max(0.35f, _agent.radius);
        _agent.height = Mathf.Max(1.6f, _agent.height);
        _agent.stoppingDistance = Mathf.Max(1.4f, _agent.stoppingDistance);
        _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        _agent.avoidancePriority = 50;
        _agent.autoBraking = true;
        _agent.autoRepath = true;
        _agent.updatePosition = true;
        _agent.updateRotation = true;
    }

    private void LateUpdate()
    {
        if (keepColliderEnabled && _bodyCollider != null && !_bodyCollider.enabled)
            _bodyCollider.enabled = true;

        if (disableRootMotion && _animator != null && _animator.applyRootMotion)
            _animator.applyRootMotion = false;

        ClampPositionToNavMesh();
        PushAwayFromWalls();
    }

    private void ClampPositionToNavMesh()
    {
        Vector3 pos = transform.position;
        if (!NavMesh.SamplePosition(pos, out NavMeshHit hit, clampSampleRadius, NavMesh.AllAreas))
        {
            // Knockback or wall penetration pushed the enemy further off the
            // NavMesh than the tight 0.8 m clamp can find. Widen the search
            // (4 m → 12 m) and warp the agent back so it never gets stranded
            // behind a wall or in void space.
            if (!NavMesh.SamplePosition(pos, out hit, 4f, NavMesh.AllAreas) &&
                !NavMesh.SamplePosition(pos, out hit, 12f, NavMesh.AllAreas))
                return;

            Vector3 recover = new Vector3(hit.position.x, pos.y, hit.position.z);
            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(recover);
            else                                       transform.position = recover;
            return;
        }

        Vector3 clamped = hit.position;

        if (NavMesh.FindClosestEdge(clamped, out NavMeshHit edge, NavMesh.AllAreas))
        {
            if (edge.distance < edgePushback)
            {
                Vector3 inward = clamped - edge.position;
                inward.y = 0f;
                if (inward.sqrMagnitude > 0.0001f)
                {
                    inward.Normalize();
                    clamped += inward * (edgePushback - edge.distance);
                }
            }
        }

        Vector3 delta = clamped - pos;
        delta.y = 0f;
        if (delta.sqrMagnitude > 0.0001f)
        {
            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(new Vector3(clamped.x, pos.y, clamped.z));
            else transform.position = new Vector3(clamped.x, pos.y, clamped.z);
        }
    }

    private void PushAwayFromWalls()
    {
        if (_bodyCollider == null) return;

        Vector3 center = transform.position + Vector3.up * (capsuleHeight * 0.5f);
        float probeRadius = capsuleRadius + 0.02f;

        Collider[] hits = Physics.OverlapSphere(center, probeRadius, wallMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider other = hits[i];
            if (other == null) continue;
            if (other == _bodyCollider) continue;
            if (other.transform.IsChildOf(transform)) continue;
            if (other.GetComponentInParent<NavMeshAgent>() != null) continue;

            if (Physics.ComputePenetration(
                    _bodyCollider, transform.position, transform.rotation,
                    other, other.transform.position, other.transform.rotation,
                    out Vector3 direction, out float distance))
            {
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f) continue;
                direction.Normalize();
                Vector3 push = direction * (distance + 0.01f);
                if (_agent != null && _agent.isOnNavMesh)
                    _agent.Warp(transform.position + push);
                else
                    transform.position += push;
            }
        }
    }
}
