using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Lightweight health component for Level 9.
/// Implements IDamageable so it is automatically detected by the existing
/// WeaponHitbox (OverlapSphere) AND by the new WeaponDamage (OnTriggerEnter).
///
/// HOW TO USE
/// ----------
/// 1. Attach to the ROOT of every character that needs HP in Level 9.
/// 2. Tag the root "Player" or "Enemy".
/// 3. Set Max Health in the Inspector (default 100).
/// 4. Optionally wire OnDeath / OnDamageTaken events in the Inspector.
/// </summary>
public class Health : MonoBehaviour, IDamageable
{
    [Header("Settings")]
    [SerializeField] private float maxHealth = 100f;

    [Header("Events  (optional)")]
    public UnityEvent          OnDeath;
    public UnityEvent<float>   OnDamageTaken;   // passes remaining HP

    // ── State ────────────────────────────────────────────────────────────────

    private float _currentHealth;
    private bool  _isDead;

    // ── IDamageable ──────────────────────────────────────────────────────────

    public bool IsAlive => !_isDead;

    /// <summary>
    /// Called by WeaponHitbox (the existing OverlapSphere system).
    /// Bridges the int-based interface to our float-based TakeDamage.
    /// </summary>
    public void ReceiveDamage(int amount, GameObject attackerRoot)
    {
        if (HealthManager.BlocksPlayerDamage(gameObject))
        {
            ResetToFullHealth();
            return;
        }

        TakeDamage(amount);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Deducts <paramref name="damageAmount"/> HP and triggers death when HP reaches 0.</summary>
    public void TakeDamage(float damageAmount)
    {
        if (HealthManager.BlocksPlayerDamage(gameObject))
        {
            ResetToFullHealth();
            return;
        }

        if (_isDead) return;

        _currentHealth = Mathf.Max(0f, _currentHealth - Mathf.Abs(damageAmount));
        OnDamageTaken?.Invoke(_currentHealth);

        Debug.Log($"[Health] {gameObject.name} took {damageAmount} damage → {_currentHealth}/{maxHealth} HP");

        CombatVoiceSfx sfx = CombatVoiceSfx.GetOrAdd(gameObject);
        if (_currentHealth > 0f)
            sfx.PlayHurt();

        if (_currentHealth <= 0f)
            Die();
    }

    public float CurrentHealth => _currentHealth;
    public float MaxHealth     => maxHealth;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        _currentHealth = maxHealth;
        _isDead        = false;
    }

    public void ResetToFullHealth()
    {
        _currentHealth = maxHealth;
        _isDead = false;
    }

    // ── Death ────────────────────────────────────────────────────────────────

    private void Die()
    {
        if (_isDead) return;
        _isDead        = true;
        _currentHealth = 0f;

        Debug.Log($"[Health] {gameObject.name} died.");
        OnDeath?.Invoke();

        CombatVoiceSfx.GetOrAdd(gameObject).PlayDeath();

        // Trigger animator "Die" if one exists.
        Animator anim = GetComponentInChildren<Animator>();
        if (anim != null)
            anim.SetTrigger("Die");

        // Disable CharacterController / NavMeshAgent so the corpse stays put.
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        UnityEngine.AI.NavMeshAgent nav = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (nav != null) nav.enabled = false;
    }
}
