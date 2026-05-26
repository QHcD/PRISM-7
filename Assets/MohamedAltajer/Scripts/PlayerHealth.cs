using UnityEngine;

#if PUN_2_OR_NEWER
using Photon.Pun;
#endif

public class PlayerHealth : MonoBehaviour, IDamageable
{
    [Header("Health")]
    public float maxHealth = 100f;
    public float currentHealth = 100f;

    [Header("Corpse Setup")]
    public GameObject deadPlayerCorpsePrefab;

    [Header("Auto-Regeneration (CoD Style)")]
    public float regenDelay = 5f;
    public float regenRate = 15f;
    public bool autoRegenEnabled = true;

    [Header("Combat Voice SFX")]
    public AudioClip[] hurtSounds;
    public AudioClip[] deathSounds;

    private float timeSinceLastDamage;
    private bool isRegenerating;
    private string _lastAttackerStatsId;
    private bool _deathHandled;

    private void Awake()
    {
        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        if (currentHealth <= 0f)
            currentHealth = maxHealth;

        timeSinceLastDamage = regenDelay + 1f;

        if (!MultiplayerMode.IsMultiplayer && MatchStatsManager.Instance != null)
        {
            string label = PlayerProfile.HasUsername ? PlayerProfile.Username : "PLAYER";
            MatchStatsManager.Instance.RegisterCombatant(MatchStatsManager.BuildCombatantId(this), label, isPlayer: true, transform: transform);
        }
    }

    private void Update()
    {
        if (!autoRegenEnabled) return;
        if (currentHealth >= maxHealth) return;
        if (currentHealth <= 0f) return;

        timeSinceLastDamage += Time.deltaTime;

        if (timeSinceLastDamage >= regenDelay)
        {
            if (!isRegenerating)
                isRegenerating = true;

            float healAmount = regenRate * Time.deltaTime;
            float before = currentHealth;
            currentHealth = Mathf.Min(maxHealth, currentHealth + healAmount);

            if (Mathf.Abs(currentHealth - before) > 0.001f)
                PushHealthToHud(false);

            if (currentHealth >= maxHealth)
            {
                currentHealth = maxHealth;
                isRegenerating = false;
            }
        }
    }

    public void TakeDamage(float amount)
    {
        if (HealthManager.BlocksPlayerDamage(gameObject))
        {
            ResetToFullHealth();
            return;
        }

        if (_deathHandled || amount <= 0f)
        {
            Debug.Log($"[Health] rejected damage reason=dead-or-zero amount={amount}");
            return;
        }

        if (MultiplayerMode.IsMultiplayer && !IsLocalOwnedPlayer())
        {
            Debug.Log($"[Health] rejected damage reason=non-owner actor={GetPhotonActorNumber()}");
            return;
        }

        float before = currentHealth;
        float absorbed = Mathf.Abs(amount);
        currentHealth = Mathf.Max(0f, currentHealth - absorbed);

        Debug.Log($"[Health] TakeDamage amount={absorbed} before={before} after={currentHealth} actor={GetPhotonActorNumber()} local={IsLocalOwnedPlayer()}");

        timeSinceLastDamage = 0f;
        isRegenerating = false;

        CombatVoiceSfx.GetOrAdd(gameObject).PlayHurt();

        if (SessionManager.Instance != null)
            SessionManager.Instance.OnPlayerTookDamage();

        PushHealthToHud(true, absorbed);

        if (currentHealth <= 0f)
            HandleDeath();
        else if (GameManager.Instance != null)
            GameManager.Instance.playerTookDamage = true;
    }

    public void ApplySyncedHealth(float syncedHealth, bool fromNetworkStream = false)
    {
        if (HealthManager.BlocksPlayerDamage(gameObject))
        {
            ResetToFullHealth();
            return;
        }

        if (IsLocalOwnedPlayer())
            return;

        currentHealth = Mathf.Clamp(syncedHealth, 0f, maxHealth);
        if (fromNetworkStream)
            Debug.Log($"[Health] synced hp={Mathf.CeilToInt(currentHealth)}/{Mathf.CeilToInt(maxHealth)} actor={GetPhotonActorNumber()}");
    }

    private void PushHealthToHud(bool showFlash, float flashAmount = 0f)
    {
        if (!ShouldDriveLocalHud())
            return;

        if (HUDManager.Instance != null)
        {
            HUDManager.Instance.UpdateHealth(currentHealth, maxHealth, this);
            Debug.Log($"[Health] HUD updated hp={Mathf.CeilToInt(currentHealth)}/{Mathf.CeilToInt(maxHealth)}");
            if (showFlash && flashAmount > 0f)
                HUDManager.Instance.ShowDamageFlash(flashAmount);
        }
    }

    private void HandleDeath()
    {
        if (_deathHandled) return;
        _deathHandled = true;

        SpawnCorpse();

        CombatVoiceSfx.GetOrAdd(gameObject).PlayDeath();

        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        UnityEngine.AI.NavMeshAgent agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (agent != null) agent.enabled = false;

        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col != null && !col.isTrigger)
                col.enabled = false;
        }

        RagdollController ragdoll = GetComponent<RagdollController>();
        if (ragdoll != null)
            ragdoll.EnableRagdoll(Vector3.back);

        if (MatchStatsManager.Instance != null)
        {
            MatchStatsManager.Instance.MarkEliminated(GetStatsId());
            MatchStatsManager.Instance.RecordKill(_lastAttackerStatsId);
        }

        if (GameManager.Instance != null && !MultiplayerMode.IsMultiplayer)
        {
            GameManager.Instance.playerTookDamage = true;
            GameManager.Instance.GameOver();
        }
    }

    private void SpawnCorpse()
    {
        if (deadPlayerCorpsePrefab != null)
        {
            Instantiate(deadPlayerCorpsePrefab, transform.position, transform.rotation);
            return;
        }

        Transform visualSource = transform.Find("ThirdPersonBody");
        if (visualSource == null)
        {
            SkinnedMeshRenderer smr = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                visualSource = smr.transform;
                while (visualSource.parent != null && visualSource.parent != transform)
                    visualSource = visualSource.parent;
            }
        }

        if (visualSource == null)
            return;

        GameObject corpse = Instantiate(visualSource.gameObject, transform.position, transform.rotation);
        corpse.name = "PlayerCorpse_Fallback";
        corpse.transform.parent = null;
        corpse.SetActive(true);

        Animator animator = corpse.GetComponentInChildren<Animator>();
        if (animator != null)
            animator.enabled = false;

        Rigidbody[] boneRbs = corpse.GetComponentsInChildren<Rigidbody>(true);
        bool hasBonePhysics = false;
        foreach (Rigidbody boneRb in boneRbs)
        {
            if (boneRb == null) continue;
            boneRb.isKinematic = false;
            boneRb.useGravity = true;
            boneRb.constraints = RigidbodyConstraints.None;
            hasBonePhysics = true;
        }

        Collider[] boneCols = corpse.GetComponentsInChildren<Collider>(true);
        foreach (Collider boneCol in boneCols)
        {
            if (boneCol == null) continue;
            boneCol.enabled = true;
            boneCol.isTrigger = false;
        }

        if (!hasBonePhysics)
        {
            Collider col = corpse.GetComponent<Collider>();
            if (col == null)
            {
                CapsuleCollider cap = corpse.AddComponent<CapsuleCollider>();
                cap.center = new Vector3(0f, 0.9f, 0f);
                cap.radius = 0.3f;
                cap.height = 1.8f;
            }
            else
            {
                col.enabled = true;
                col.isTrigger = false;
            }

            Rigidbody rb = corpse.GetComponent<Rigidbody>();
            if (rb == null)
                rb = corpse.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.mass = 70f;
        }

        MonoBehaviour[] scripts = corpse.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] != null && !(scripts[i] is UnityEngine.EventSystems.UIBehaviour))
                Destroy(scripts[i]);
        }

        Camera[] cameras = corpse.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
            if (cameras[i] != null) Destroy(cameras[i]);

#if PUN_2_OR_NEWER
        PhotonView pv = corpse.GetComponentInChildren<PhotonView>(true);
        if (pv != null) Destroy(pv);
#endif
    }

    private void Start()
    {
        MeleeBodyTargeting.EnsureMeleeBodyCollider(transform);

        CombatVoiceSfx voice = CombatVoiceSfx.GetOrAdd(gameObject);
        voice.ApplyInspectorClips(hurtSounds, deathSounds);

        if (MultiplayerMode.IsMultiplayer && MatchStatsManager.Instance != null)
        {
#if PUN_2_OR_NEWER
            PhotonView view = GetComponent<PhotonView>();
            if (view != null && view.Owner != null)
            {
                string label = string.IsNullOrWhiteSpace(view.Owner.NickName) ? $"Player_{view.Owner.ActorNumber}" : view.Owner.NickName;
                MatchStatsManager.Instance.RegisterCombatant(GetStatsId(), label, isPlayer: true, transform: transform);
            }
#endif
        }

        PushHealthToHud(false);
    }

    public bool IsAlive => currentHealth > 0f && !_deathHandled;

    public void ReceiveDamage(int amount, GameObject attackerRoot)
    {
        if (HealthManager.BlocksPlayerDamage(gameObject))
        {
            ResetToFullHealth();
            return;
        }

        bool fromEnemy = attackerRoot != null && attackerRoot.GetComponentInParent<EnemyController>() != null;

#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer)
        {
            PhotonView pv = GetComponent<PhotonView>();
            NetworkPlayerSync sync = GetComponent<NetworkPlayerSync>();
            if (pv != null && sync != null)
            {
                if (!pv.IsMine)
                {
                    if (fromEnemy && PhotonNetwork.IsMasterClient)
                    {
                        sync.ApplyDamageToNetworkPlayer(Mathf.Max(1, amount), attackerRoot);
                        return;
                    }

                    Debug.Log($"[Health] rejected damage reason=non-owner-proxy actor={GetPhotonActorNumber()}");
                    return;
                }
            }
        }
#endif

        if (MultiplayerMode.IsMultiplayer)
        {
            bool isFriendly = attackerRoot != null && attackerRoot.GetComponentInParent<PlayerHealth>() != null && attackerRoot.GetComponentInParent<PlayerHealth>().gameObject != gameObject;
            if (isFriendly)
            {
                bool ff = true;
#if PUN_2_OR_NEWER
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(MpRoomConfig.KeyFriendlyFire, out object ffRaw))
                    ff = (bool)ffRaw;
#endif
                if (MultiplayerMode.ActiveMode == MpGameMode.CoopSurvival)
                    ff = false;

                if (!ff)
                {
                    Debug.Log("[Health] rejected damage reason=friendly-fire-blocked");
                    return;
                }
            }
        }

        if (attackerRoot != null)
        {
            EnemyController attackerEnemy = attackerRoot.GetComponentInParent<EnemyController>();
            if (attackerEnemy != null)
                _lastAttackerStatsId = MatchStatsManager.BuildCombatantId(attackerEnemy);
        }

        bool fromEnemyAttacker = attackerRoot != null && attackerRoot.GetComponentInParent<EnemyController>() != null;
        if (fromEnemyAttacker && GameManager.Instance != null)
        {
            int hitsToKill = Mathf.Max(1, GameManager.Instance.GetPlayerHitsToKill());
            float perHitDamage = maxHealth / hitsToKill;
            TakeDamage(perHitDamage);
            return;
        }

        TakeDamage((float)Mathf.Max(1, amount));
    }

    public void Heal(float amount)
    {
        if (amount <= 0f) return;
        currentHealth = Mathf.Min(maxHealth, currentHealth + Mathf.Abs(amount));
        PushHealthToHud(false);
    }

    public void ResetToFullHealth()
    {
        maxHealth = Mathf.Max(1f, maxHealth);
        currentHealth = maxHealth;
        timeSinceLastDamage = regenDelay + 1f;
        isRegenerating = false;
        _deathHandled = false;
        _lastAttackerStatsId = null;
        PushHealthToHud(false);
    }

    private bool IsLocalOwnedPlayer()
    {
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer)
        {
            PhotonView pv = GetComponent<PhotonView>();
            if (pv != null)
                return pv.IsMine;
        }
#endif
        return true;
    }

    private bool ShouldDriveLocalHud()
    {
        if (HUDManager.Instance == null)
            return false;

        if (!MultiplayerMode.IsMultiplayer)
            return true;

        return IsLocalOwnedPlayer();
    }

    private int GetPhotonActorNumber()
    {
#if PUN_2_OR_NEWER
        PhotonView view = GetComponent<PhotonView>();
        if (view != null && view.Owner != null)
            return view.Owner.ActorNumber;
#endif
        return -1;
    }

    private string GetStatsId()
    {
#if PUN_2_OR_NEWER
        PhotonView view = GetComponent<PhotonView>();
        if (MultiplayerMode.IsMultiplayer && view != null && view.Owner != null)
            return $"photon:{view.Owner.ActorNumber}";
#endif
        return MatchStatsManager.BuildCombatantId(this);
    }
}
