using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SciFiSlidingDoor : MonoBehaviour, IInteractable
{
    public Transform leftPanel;
    public Transform rightPanel;
    public float panelSlide = 1.9f;
    public float slideDuration = 0.55f;
    public bool interactiveToggle = false;
    public string playerTag = "Player";
    public float interactRange = 3.5f;
    public LayerMask characterLayers;
    public string interactionPrompt = "OPEN DOOR";

    private static readonly int IsOpenHash = Animator.StringToHash("IsOpen");

    private Vector3 _leftClosedLocal;
    private Vector3 _rightClosedLocal;
    private Vector3 _leftOpenLocal;
    private Vector3 _rightOpenLocal;
    private bool _isOpen;
    private Coroutine _slideRoutine;
    private Animator _animator;
    private readonly Dictionary<Transform, int> _occupants = new Dictionary<Transform, int>();

    private void Awake()
    {
        EnsurePanels();
        CapturePoses();
        _animator = GetComponentInChildren<Animator>(true);
        if (characterLayers.value == 0)
            characterLayers = BuildCharacterLayerMask();
        EnsureTrigger();
    }

    private void OnDisable()
    {
        _occupants.Clear();
        if (_slideRoutine != null)
        {
            StopCoroutine(_slideRoutine);
            _slideRoutine = null;
        }
    }

    private void EnsurePanels()
    {
        if (leftPanel == null)
        {
            Transform t = transform.Find("LeftPanel");
            if (t != null) leftPanel = t;
        }
        if (rightPanel == null)
        {
            Transform t = transform.Find("RightPanel");
            if (t != null) rightPanel = t;
        }
    }

    private void EnsureTrigger()
    {
        SphereCollider trigger = GetComponent<SphereCollider>();
        if (trigger == null)
            trigger = gameObject.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.radius = Mathf.Max(trigger.radius, interactRange);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    private void CapturePoses()
    {
        if (leftPanel != null)
        {
            _leftClosedLocal = leftPanel.localPosition;
            float dir = _leftClosedLocal.z < 0f ? -1f : (_leftClosedLocal.z > 0f ? 1f : -1f);
            _leftOpenLocal = _leftClosedLocal + Vector3.forward * (panelSlide * dir);
        }
        if (rightPanel != null)
        {
            _rightClosedLocal = rightPanel.localPosition;
            float dir = _rightClosedLocal.z > 0f ? 1f : (_rightClosedLocal.z < 0f ? -1f : 1f);
            _rightOpenLocal = _rightClosedLocal + Vector3.forward * (panelSlide * dir);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
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

    public void Toggle()
    {
        SetOpen(!_isOpen);
    }

    public void Open()
    {
        SetOpen(true);
    }

    public void Close()
    {
        SetOpen(false);
    }

    string IInteractable.GetPrompt() => string.Empty;

    void IInteractable.Interact(GameObject by)
    {
    }

    bool IInteractable.CanInteract => false;

    private void SetOpen(bool open)
    {
        EnsurePanels();
        if (_isOpen == open && _slideRoutine == null) return;
        _isOpen = open;

        if (_animator != null)
            _animator.SetBool(IsOpenHash, open);

        if (_slideRoutine != null)
            StopCoroutine(_slideRoutine);

        _slideRoutine = StartCoroutine(SlideRoutine(open));
    }

    private IEnumerator SlideRoutine(bool targetOpen)
    {
        Vector3 leftFrom = leftPanel != null ? leftPanel.localPosition : Vector3.zero;
        Vector3 rightFrom = rightPanel != null ? rightPanel.localPosition : Vector3.zero;
        Vector3 leftTo = targetOpen ? _leftOpenLocal : _leftClosedLocal;
        Vector3 rightTo = targetOpen ? _rightOpenLocal : _rightClosedLocal;
        float duration = Mathf.Max(0.05f, slideDuration);
        float t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float n = Mathf.Clamp01(t / duration);
            float e = n * n * (3f - 2f * n);
            if (leftPanel != null) leftPanel.localPosition = Vector3.Lerp(leftFrom, leftTo, e);
            if (rightPanel != null) rightPanel.localPosition = Vector3.Lerp(rightFrom, rightTo, e);
            yield return null;
        }

        if (leftPanel != null) leftPanel.localPosition = leftTo;
        if (rightPanel != null) rightPanel.localPosition = rightTo;
        _slideRoutine = null;

        if (!targetOpen && _occupants.Count > 0)
            SetOpen(true);
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
            if (t.CompareTag(playerTag)) return t;
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
            if (t.CompareTag(playerTag)) return true;
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
        Debug.Log($"[SciFiDoor] BuildCharacterLayerMask=0x{mask:X}");
        return mask;
    }

    private static void AddLayer(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
            mask |= 1 << layer;
    }
}
