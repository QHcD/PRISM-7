using UnityEngine;

public class SphereGroundProbe : MonoBehaviour, IGroundProbe
{
    [SerializeField] private Transform probeOrigin;
    [SerializeField] private float probeRadius = 0.28f;
    [SerializeField] private float probeOffset = 0.12f;
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField] private bool excludeSelf = true;

    private bool _isGrounded;
    private Vector3 _groundNormal = Vector3.up;
    private CharacterController _cc;

    public bool IsGrounded => _isGrounded;
    public Vector3 GroundNormal => _groundNormal;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (probeOrigin == null) probeOrigin = transform;
    }

    private void Update()
    {
        Vector3 origin = probeOrigin.position + Vector3.down * probeOffset;

        if (_cc != null && _cc.isGrounded)
        {
            _isGrounded = true;
        }
        else
        {
            _isGrounded = Physics.CheckSphere(origin, probeRadius, groundMask, QueryTriggerInteraction.Ignore);
        }

        if (_isGrounded && Physics.SphereCast(
                probeOrigin.position + Vector3.up * 0.1f,
                probeRadius,
                Vector3.down,
                out RaycastHit hit,
                probeOffset + 0.3f,
                groundMask,
                QueryTriggerInteraction.Ignore))
        {
            if (!excludeSelf || !hit.collider.transform.IsChildOf(transform))
                _groundNormal = hit.normal;
        }
        else
        {
            _groundNormal = Vector3.up;
        }
    }

    private void OnDrawGizmosSelected()
    {
        Transform origin = probeOrigin != null ? probeOrigin : transform;
        Gizmos.color = _isGrounded ? Color.green : Color.red;
        Gizmos.DrawWireSphere(origin.position + Vector3.down * probeOffset, probeRadius);
    }
}
