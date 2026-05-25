using System.Collections;
using UnityEngine;

// Peer to DoorController for doors that slide instead of swing — shutters that
// retract upward, garage doors, side-sliding hangar doors. Shares the same
// IInteractable contract so the player's existing [E] raycast triggers it
// identically. No physics; pure transform interpolation, so it can't explode.
//
// Setup: place this on the door GameObject. The door slides from its current
// local position to (current + slideAxis * slideDistance), in local space.
//   • slideAxis = (0,1,0) for a roller shutter going up
//   • slideAxis = (1,0,0) for a sliding side door
[DisallowMultipleComponent]
public class SlidingDoor : MonoBehaviour, IInteractable
{
    [Header("Slide")]
    [Tooltip("Direction the door slides in LOCAL space. Normalised automatically.")]
    public Vector3 slideAxis = Vector3.up;

    [Tooltip("Distance to slide along slideAxis when opening.")]
    public float slideDistance = 3f;

    [Tooltip("Seconds to complete the slide.")]
    public float slideDuration = 1.2f;

    public AnimationCurve easing = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Interaction")]
    public bool interactiveToggle = false;
    public string interactionPrompt = "OPEN";

    private Vector3 _closedPos;
    private Vector3 _openPos;
    private bool _isOpen;
    private Coroutine _routine;

    private void Awake()
    {
        _closedPos = transform.localPosition;
        var dir = slideAxis.sqrMagnitude > 0.0001f ? slideAxis.normalized : Vector3.up;
        _openPos = _closedPos + dir * slideDistance;
    }

    public void Open()   { if (!_isOpen)  { _isOpen = true;  Restart(_openPos); } }
    public void Close()  { if ( _isOpen)  { _isOpen = false; Restart(_closedPos); } }
    public void Toggle() { if (_isOpen) Close(); else Open(); }

    private void Restart(Vector3 target)
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(Slide(target));
    }

    private IEnumerator Slide(Vector3 target)
    {
        Vector3 start = transform.localPosition;
        float t = 0f;
        float dur = Mathf.Max(0.05f, slideDuration);
        while (t < dur)
        {
            t += Time.deltaTime;
            float n = Mathf.Clamp01(t / dur);
            float e = easing != null ? easing.Evaluate(n) : n;
            transform.localPosition = Vector3.LerpUnclamped(start, target, e);
            yield return null;
        }
        transform.localPosition = target;
        _routine = null;
    }

    string IInteractable.GetPrompt() => string.Empty;
    void   IInteractable.Interact(GameObject by) { }
    bool   IInteractable.CanInteract           => false;
}
