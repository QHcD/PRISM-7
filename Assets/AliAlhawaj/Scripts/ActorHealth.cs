using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Generic health script for both Player and Enemy.
/// Implements the existing IDamageable interface used across the project.
/// </summary>
public class ActorHealth : MonoBehaviour, IDamageable
{
    [Header("Health")]
    public float maxHealth = 100f;
    public float currentHealth = 100f;

    [Header("Death")]
    [SerializeField] private bool destroyOnDeath = false;
    [SerializeField] private float destroyDelay = 3f;
    [SerializeField] private Animator animator;
    [SerializeField] private string deathTrigger = "Die";
    [SerializeField] private Color hitFlashColor = new Color(1f, 0.25f, 0.25f, 1f);
    [SerializeField] private float hitFlashDuration = 0.12f;

    public bool IsAlive => currentHealth > 0f;

    private bool _isDead;
    private Renderer[] _renderers;
    private Color[] _originalColors;
    private Coroutine _flashRoutine;

    private void Awake()
    {
        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        if (currentHealth <= 0f)
            currentHealth = maxHealth;

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        _renderers = GetComponentsInChildren<Renderer>(true);
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            _originalColors[i] = GetMaterialColor(_renderers[i].material);
        }
    }

    public void TakeDamage(float damage)
    {
        if (_isDead)
            return;

        currentHealth = Mathf.Max(0f, currentHealth - Mathf.Abs(damage));
        FlashDamage();

        CombatVoiceSfx sfx = CombatVoiceSfx.GetOrAdd(gameObject);
        if (currentHealth > 0f)
            sfx.PlayHurt();

        if (currentHealth <= 0f)
            Die();
    }

    public void ReceiveDamage(int amount, GameObject attackerRoot)
    {
        TakeDamage(amount);
    }

    public void Die()
    {
        if (_isDead)
            return;

        _isDead = true;
        currentHealth = 0f;

        CombatVoiceSfx.GetOrAdd(gameObject).PlayDeath();

        if (animator != null && !string.IsNullOrWhiteSpace(deathTrigger))
            animator.SetTrigger(deathTrigger);

        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null)
            cc.enabled = false;

        NavMeshAgent agent = GetComponent<NavMeshAgent>();
        if (agent != null)
            agent.enabled = false;

        Collider[] colliders = GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col != null && !col.isTrigger && col.gameObject == gameObject)
                col.enabled = false;
        }

        RagdollController ragdoll = GetComponent<RagdollController>();
        if (ragdoll != null)
            ragdoll.EnableRagdoll(Vector3.back);

        if (CompareTag("Player") && GameManager.Instance != null)
            GameManager.Instance.GameOver();

        if (destroyOnDeath)
            Destroy(gameObject, destroyDelay);
    }

    private void FlashDamage()
    {
        if (!isActiveAndEnabled || _renderers == null || _renderers.Length == 0)
            return;

        if (_flashRoutine != null)
            StopCoroutine(_flashRoutine);
        _flashRoutine = StartCoroutine(FlashRoutine());
    }

    private System.Collections.IEnumerator FlashRoutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            SetMaterialColor(_renderers[i].material, hitFlashColor);
        }

        yield return new WaitForSeconds(hitFlashDuration);

        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null || _renderers[i].material == null) continue;
            if (i < _originalColors.Length)
                SetMaterialColor(_renderers[i].material, _originalColors[i]);
        }

        _flashRoutine = null;
    }

    private static Color GetMaterialColor(Material mat)
    {
        if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
        if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
        return Color.white;
    }

    private static void SetMaterialColor(Material mat, Color color)
    {
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
    }
}
