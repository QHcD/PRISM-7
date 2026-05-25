using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class DoorController : MonoBehaviour, IInteractable
{
    public static bool DoorInteractionsEnabled = false;

    [Header("Swing")]
    public float openAngle = 90f;
    public float openDuration = 0.9f;
    public AnimationCurve easing = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Activation")]
    public bool openOnStart = false;
    public bool openOnPlayerTrigger = true;
    public float proximityRadius = 3.5f;
    public LayerMask characterLayers;

    [Header("Interaction")]
    public bool interactiveToggle = false;
    public string interactionPrompt = "OPEN DOOR";

    private static readonly int IsOpenHash = Animator.StringToHash("IsOpen");

    private Quaternion _closedRot;
    private Quaternion _openRot;
    private bool _isOpen;
    private Coroutine _swingRoutine;
    private Animator _animator;
    private readonly Dictionary<Transform, int> _occupants = new Dictionary<Transform, int>();

    private void Awake()
    {
        _closedRot = transform.localRotation;
        _openRot = _closedRot * Quaternion.Euler(0f, openAngle, 0f);
        _animator = GetComponentInChildren<Animator>(true);
        if (characterLayers.value == 0)
            characterLayers = BuildCharacterLayerMask();
        EnsureProximityTrigger();
    }

    private void Start()
    {
        if (openOnStart)
            SetOpen(true);
    }

    private void OnDisable()
    {
        _occupants.Clear();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!openOnPlayerTrigger) return;
        Transform character = ResolveCharacterRoot(other);
        if (character == null) return;
        if (_occupants.TryGetValue(character, out int count))
            _occupants[character] = count + 1;
        else
            _occupants.Add(character, 1);
        SetOpen(true);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!openOnPlayerTrigger) return;
        Transform character = ResolveCharacterRoot(other);
        if (character == null) return;
        if (_occupants.TryGetValue(character, out int count))
        {
            if (count <= 1)
                _occupants.Remove(character);
            else
                _occupants[character] = count - 1;
        }
        PruneOccupants();
        if (_occupants.Count == 0)
            SetOpen(false);
    }

    public void Open()
    {
        SetOpen(true);
    }

    public void Close()
    {
        SetOpen(false);
    }

    public void Toggle()
    {
        SetOpen(!_isOpen);
    }

    string IInteractable.GetPrompt() => (DoorInteractionsEnabled && interactiveToggle)
        ? (_isOpen ? "CLOSE DOOR" : interactionPrompt)
        : string.Empty;

    void IInteractable.Interact(GameObject by)
    {
        if (DoorInteractionsEnabled && interactiveToggle)
            Toggle();
    }

    bool IInteractable.CanInteract => DoorInteractionsEnabled && interactiveToggle;

    private void SetOpen(bool open)
    {
        if (_isOpen == open && _swingRoutine == null) return;
        _isOpen = open;

        if (_animator != null)
            _animator.SetBool(IsOpenHash, open);

        if (_swingRoutine != null)
            StopCoroutine(_swingRoutine);

        _swingRoutine = StartCoroutine(SwingDoor(open ? _openRot : _closedRot));
    }

    private IEnumerator SwingDoor(Quaternion targetRot)
    {
        Quaternion startRot = transform.localRotation;
        float t = 0f;
        float duration = Mathf.Max(0.05f, openDuration);

        while (t < duration)
        {
            t += Time.deltaTime;
            float normalized = Mathf.Clamp01(t / duration);
            float eased = easing != null ? easing.Evaluate(normalized) : normalized;
            transform.localRotation = Quaternion.Slerp(startRot, targetRot, eased);
            yield return null;
        }

        transform.localRotation = targetRot;
        _swingRoutine = null;
    }

    private void EnsureProximityTrigger()
    {
        SphereCollider trigger = GetComponent<SphereCollider>();
        if (trigger == null)
            trigger = gameObject.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.radius = Mathf.Max(trigger.radius, proximityRadius);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    private Transform ResolveCharacterRoot(Collider other)
    {
        if (other == null) return null;
        if (!IsCharacterLayer(other.gameObject.layer) && !HasCharacterIdentity(other.transform))
            return null;

        for (Transform t = other.transform; t != null; t = t.parent)
        {
            if (t.GetComponent<PlayerController>() != null) return t;
            if (t.GetComponent<EnemyController>() != null) return t;
            if (t.CompareTag("Player")) return t;
            if (t.CompareTag("Enemy")) return t;
        }

        return other.transform.root;
    }

    private bool HasCharacterIdentity(Transform source)
    {
        for (Transform t = source; t != null; t = t.parent)
        {
            if (t.GetComponent<PlayerController>() != null) return true;
            if (t.GetComponent<EnemyController>() != null) return true;
            if (t.GetComponent<CharacterController>() != null) return true;
            if (t.CompareTag("Player")) return true;
            if (t.CompareTag("Enemy")) return true;
        }
        return false;
    }

    private bool IsCharacterLayer(int layer)
    {
        return characterLayers.value != 0 && (characterLayers.value & (1 << layer)) != 0;
    }

    private void PruneOccupants()
    {
        s_pruneBuffer.Clear();
        foreach (Transform transform in _occupants.Keys)
            if (transform == null || !transform.gameObject.activeInHierarchy)
                s_pruneBuffer.Add(transform);
        for (int i = 0; i < s_pruneBuffer.Count; i++)
            _occupants.Remove(s_pruneBuffer[i]);
    }

    private static readonly List<Transform> s_pruneBuffer = new List<Transform>(8);

    private static LayerMask BuildCharacterLayerMask()
    {
        int mask = 0;
        AddLayer(ref mask, "Player");
        AddLayer(ref mask, "Character");
        AddLayer(ref mask, "Hittable");
        AddLayer(ref mask, "Enemy");
        AddLayer(ref mask, "Enemies");
        if (mask == 0)
            mask = 1 << 0;
        return mask;
    }

    private static void AddLayer(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
            mask |= 1 << layer;
    }
}
