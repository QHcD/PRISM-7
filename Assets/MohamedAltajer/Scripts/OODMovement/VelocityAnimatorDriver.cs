using UnityEngine;

[RequireComponent(typeof(Animator))]
public class VelocityAnimatorDriver : MonoBehaviour
{
    [SerializeField] private Transform velocitySource;
    [SerializeField] private float walkSpeed = 2.0f;
    [SerializeField] private float runSpeed = 5.5f;
    [SerializeField] private string speedParam = "Speed";
    [SerializeField] private float damping = 0.1f;
    [SerializeField] private bool useCharacterController = true;
    [SerializeField] private bool useRigidbody = false;

    private Animator _animator;
    private CharacterController _cc;
    private Rigidbody _rb;
    private int _speedHash;
    private bool _hasSpeed;
    private Vector3 _previousPosition;
    private float _lastNormalized;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _speedHash = Animator.StringToHash(speedParam);
        CacheParameterPresence();
        ResolveVelocitySource();
        if (velocitySource != null) _previousPosition = velocitySource.position;
    }

    private void ResolveVelocitySource()
    {
        if (velocitySource == null) velocitySource = transform.root;
        if (useCharacterController) _cc = velocitySource.GetComponent<CharacterController>();
        if (useRigidbody) _rb = velocitySource.GetComponent<Rigidbody>();
    }

    private void CacheParameterPresence()
    {
        _hasSpeed = false;
        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        foreach (AnimatorControllerParameter p in _animator.parameters)
        {
            if (p.nameHash == _speedHash && p.type == AnimatorControllerParameterType.Float)
            {
                _hasSpeed = true;
                break;
            }
        }
    }

    private void Update()
    {
        if (_animator == null || !_hasSpeed || velocitySource == null) return;

        float horizontalSpeed = MeasureHorizontalSpeed();
        float normalized = NormalizeForBlend(horizontalSpeed);
        _animator.SetFloat(_speedHash, normalized, damping, Time.deltaTime);
        _lastNormalized = normalized;
    }

    private float MeasureHorizontalSpeed()
    {
        if (_cc != null && useCharacterController)
        {
            Vector3 v = _cc.velocity;
            v.y = 0f;
            return v.magnitude;
        }

        if (_rb != null && useRigidbody)
        {
#if UNITY_6000_0_OR_NEWER
            Vector3 v = _rb.linearVelocity;
#else
            Vector3 v = _rb.velocity;
#endif
            v.y = 0f;
            return v.magnitude;
        }

        Vector3 delta = velocitySource.position - _previousPosition;
        _previousPosition = velocitySource.position;
        delta.y = 0f;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        return delta.magnitude / dt;
    }

    private float NormalizeForBlend(float worldSpeed)
    {
        if (worldSpeed <= 0.01f) return 0f;
        if (worldSpeed <= walkSpeed)
        {
            float t = worldSpeed / Mathf.Max(0.01f, walkSpeed);
            return Mathf.Lerp(0.05f, 1.0f, t);
        }

        float runT = Mathf.InverseLerp(walkSpeed, Mathf.Max(walkSpeed + 0.01f, runSpeed), worldSpeed);
        return Mathf.Lerp(1.0f, 2.0f, Mathf.Clamp01(runT));
    }

    public float CurrentNormalizedSpeed => _lastNormalized;
}
