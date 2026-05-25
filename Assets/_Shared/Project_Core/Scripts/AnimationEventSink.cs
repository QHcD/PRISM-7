using UnityEngine;

/// <summary>
/// Absorbs third-party AnimationEvents embedded in imported clips so Unity does
/// not log "has no receiver" warnings. Attach on the same GameObject as the
/// Animator that plays the clips.
///
/// Hitbox lookups are cached at Awake — the per-event path is a zero-allocation
/// no-op when no unarmed hitbox exists, which is the common case (the player
/// usually wields a weapon). Previously every event ran GetComponentsInChildren
/// + Debug.Log, which produced major frame stutter during combat.
/// </summary>
[DisallowMultipleComponent]
public class AnimationEventSink : MonoBehaviour
{
    private WeaponHitbox _unarmedHitbox;
    private bool _hitboxResolved;

    private void Awake()
    {
        ResolveUnarmedHitbox();
    }

    private void ResolveUnarmedHitbox()
    {
        if (_hitboxResolved) return;
        _hitboxResolved = true;

        WeaponHitbox[] hitboxes = GetComponentsInChildren<WeaponHitbox>(true);
        for (int i = 0; i < hitboxes.Length; i++)
        {
            WeaponHitbox h = hitboxes[i];
            if (h == null) continue;
            string n = h.gameObject.name;
            if (string.IsNullOrEmpty(n)) continue;
            string lower = n.ToLowerInvariant();
            if (lower.Contains("unarmed") || lower.Contains("fist") || lower.Contains("hand"))
            {
                _unarmedHitbox = h;
                return;
            }
        }
    }

    // ── SFX stubs (intentional no-ops for third-party clips) ─────────────────
    public void PlayRandomRunStepSFX() { }
    public void PlayFootStepSFX() { }
    public void PlayRandomWalkStepSFX() { }
    public void PlayGetHitSFX() { }

    // ── Unarmed hitbox receivers ─────────────────────────────────────────────
    // Zero-cost no-op when no unarmed hitbox exists. No logging on the per-event
    // path — combat fires these 4+ times per swing per actor and even a single
    // Debug.Log here used to cost frames.
    public void EnableRightUnarmedHitboxes()  { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableRightUnarmedHitboxes() { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableLeftUnarmedHitboxes()   { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableLeftUnarmedHitboxes()  { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableLeftUnarmedHitbox()     { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableLeftUnarmedHitbox()    { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableRightUnarmedHitbox()    { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableRightUnarmedHitbox()   { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void DisableUnarmedHitboxes()      { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
}
