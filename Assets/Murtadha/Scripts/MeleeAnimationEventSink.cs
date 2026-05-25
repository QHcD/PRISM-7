using UnityEngine;

/// <summary>
/// Absorbs AnimationEvents embedded in third-party melee clips (DragonSouls / Explosive RPG pack)
/// and forwards hitbox-enable/disable calls to the WeaponHitbox on the equipped weapon.
///
/// Attach on the same GameObject as the humanoid Animator that plays the clips.
/// The WeaponHitbox is found automatically via the PlayerController's equipped weapon.
///
/// Also forwards the "OnKatanaStrike" Animation Event to KatanaCombatHandler,
/// which lives on the character root (potentially a different GameObject than
/// the Animator, so we search up the hierarchy).
/// </summary>
[DisallowMultipleComponent]
public class MeleeAnimationEventSink : MonoBehaviour
{
    private WeaponHitbox         cachedHitbox;
    private KatanaCombatHandler  cachedKatanaHandler;

    private WeaponHitbox FindWeaponHitbox()
    {
        if (cachedHitbox != null) return cachedHitbox;

        // Search up to the PlayerController and find the equipped weapon's hitbox
        PlayerController pc = GetComponentInParent<PlayerController>();
        if (pc != null && pc.equippedWeaponObject != null)
            cachedHitbox = pc.equippedWeaponObject.GetComponentInChildren<WeaponHitbox>(true);

        return cachedHitbox;
    }

    // Called when the weapon cache should be refreshed (e.g. weapon re-equip)
    public void ClearCache()
    {
        cachedHitbox        = null;
        cachedKatanaHandler = null;
    }

    private KatanaCombatHandler FindKatanaHandler()
    {
        if (cachedKatanaHandler != null) return cachedKatanaHandler;
        cachedKatanaHandler = GetComponentInParent<KatanaCombatHandler>(true);
        return cachedKatanaHandler;
    }

    // ── Katana Animation Event ──────────────────────────────────────────────
    //
    // Place an Animation Event named "OnKatanaStrike" in the katana swing clip
    // at the frame of peak blade contact.  Unity fires it on this GameObject
    // (the Animator host); we forward it to KatanaCombatHandler on the root.

    public void OnKatanaStrike()
    {
        FindKatanaHandler()?.OnKatanaStrike();
    }

    // ── Animation Event Receivers ──
    //
    // IMPORTANT: All Enable* entry points are intentional NO-OPS.
    //
    // The player's melee damage is routed EXCLUSIVELY through
    // PlayerController.AttackMelee(), which performs a strict, single-target
    // overlap/sweep query against real colliders and damages at most ONE
    // enemy per swing. Enabling the physical trigger hitbox (WeaponHitbox) caused
    // massive AoE "Bluetooth" damage on large weapons (Baseball Bat, Axe)
    // on levels 4/7/9 — its collider swept through multiple enemies at once
    // and one-shot them via overlapping logic.
    //
    // These methods are kept as empty stubs so existing DragonSouls /
    // ExplosiveLLC animation clips that embed these AnimationEvent names
    // (EnableRightUnarmedHitboxes, EnableLeftUnarmedHitbox, etc.) continue
    // to resolve at runtime without throwing "No receiver" warnings —
    // they simply do nothing.

    private WeaponHitbox _unarmedHitbox;
    private bool         _unarmedResolved;

    private void Awake()
    {
        ResolveUnarmedHitbox();
    }

    private void ResolveUnarmedHitbox()
    {
        if (_unarmedResolved) return;
        _unarmedResolved = true;

        WeaponHitbox[] hitboxes = GetComponentsInChildren<WeaponHitbox>(true);
        for (int i = 0; i < hitboxes.Length; i++)
        {
            WeaponHitbox h = hitboxes[i];
            if (h == null) continue;
            string lower = h.gameObject.name.ToLowerInvariant();
            if (lower.Contains("unarmed") || lower.Contains("fist") || lower.Contains("hand"))
            {
                _unarmedHitbox = h;
                return;
            }
        }
    }

    // Zero-cost no-op when no unarmed hitbox exists — no per-event allocations,
    // no Debug.Log. The previous version ran GetComponentsInChildren + Debug.Log
    // on every fire which produced major frame stutter during combat.
    public void EnableRightUnarmedHitboxes()  { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableRightUnarmedHitboxes() { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableLeftUnarmedHitboxes()   { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableLeftUnarmedHitboxes()  { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableLeftUnarmedHitbox()     { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableLeftUnarmedHitbox()    { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void EnableRightUnarmedHitbox()    { if (_unarmedHitbox != null) _unarmedHitbox.EnableHitbox(); }
    public void DisableRightUnarmedHitbox()   { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }
    public void DisableUnarmedHitboxes()      { if (_unarmedHitbox != null) _unarmedHitbox.DisableHitbox(); }

    // Generic enable/disable that can be added to any custom animation clip
    public void EnableWeaponHitbox()  { /* disabled — see header */ }
    public void DisableWeaponHitbox() { /* disabled — see header */ }

    // ── SFX hooks (kept as no-ops for compatibility) ──

    public void PlayRandomRunStepSFX() { }

    public void PlayFootStepSFX() { }

    public void PlayRandomWalkStepSFX() { }

    public void PlayGetHitSFX() { }
}
