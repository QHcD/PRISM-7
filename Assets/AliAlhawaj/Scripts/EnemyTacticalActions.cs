using UnityEngine;
using UnityEngine.AI;

#if PUN_2_OR_NEWER
using Photon.Pun;
#endif

/// <summary>
/// Tactical-action driver for enemies — Z (JumpOver), X (Slide), C (Prone)
/// using the SAME Animator parameters as the player. Picks decisions from
/// EnemyPersonality + EnemyController state + recent damage signal.
///
/// Runtime effect (NEW):
///   When a trigger fires, the agent is briefly modulated for the visible
///   duration of the clip — slide / jumpover commit forward at sprint speed,
///   prone halves speed. Cooldowns prevent spam and ensure animations are
///   never cancelled mid-way. The agent is NEVER stopped or had its path
///   reset, so the existing chase/attack state machine keeps working.
///
/// All animator triggers / bools are null-guarded — if the controller does
/// not define a parameter (older enemies before TacticalAnimatorSetup),
/// the action is logged and skipped.
/// </summary>
[DisallowMultipleComponent]
public class EnemyTacticalActions : MonoBehaviour
{
    [Header("Decision interval")]
    public float decisionInterval = 0.45f;

    [Header("Cooldown floors (seconds, before personality scaling)")]
    public float slideCooldownBase    = 6f;
    public float jumpOverCooldownBase = 8f;
    public float proneCooldownBase    = 14f;

    [Header("Slide rules")]
    public float slideChaseMaxDistance = 7.0f;
    public float slideChaseMinDistance = 3.2f;
    public float slideDisengageHpThreshold = 0.35f;
    [Tooltip("How recently the enemy must have been hit to consider a reactive slide-dodge.")]
    public float slideDodgeWindow = 0.45f;

    [Header("JumpOver rules")]
    public float jumpOverBlockedSeconds = 0.5f;
    public float jumpOverGapMinDistance = 5.0f;
    public float jumpOverGapMaxDistance = 9.0f;

    [Header("Prone rules")]
    [Range(0f, 1f)] public float proneHpThreshold = 0.30f;
    public float proneMinTargetDistance = 11f;
    public float proneHoldDuration = 1.6f;

    [Header("Action visible-effect durations (seconds)")]
    [Tooltip("How long the agent is locked into the slide commit window.")]
    public float slideActionDuration = 0.55f;
    [Tooltip("How long the agent is locked into the vault commit window.")]
    public float jumpOverActionDuration = 0.6f;
    [Tooltip("Agent speed multiplier during the slide commit (1.4 = +40% forward push).")]
    public float slideSpeedMultiplier = 1.45f;
    [Tooltip("Agent speed multiplier during the prone hold (lower = slower).")]
    [Range(0.0f, 1.0f)] public float proneSpeedMultiplier = 0.40f;

    [Header("Reaction delay (seconds)")]
    public float reactionDelayMax = 0.35f;

    [Header("Debug")]
    [Tooltip("Logs once-per-enemy attachment summary + each triggered action.")]
    public bool debugLog = false;

    // ── Cached refs ──────────────────────────────────────────────────────────
    private Animator _anim;
    private EnemyController _enemy;
    private NavMeshAgent _agent;
    private EnemyPersonality _personality;

    private float _nextDecisionTime;
    private float _nextSlideTime;
    private float _nextJumpOverTime;
    private float _nextProneTime;
    private float _pathBlockedSince = -1f;
    private float _proneClearTime = -1f;
    private float _reactiveDamageTime = -999f;

    private float _actionLockUntil = -1f;
    private float _baseAgentSpeed = -1f;
    private string _activeAction = "";

    private bool _hasJumpOver;
    private bool _hasSlide;
    private bool _hasIsProne;

    private void Awake()
    {
        // Tactical maneuvers (roll / slide / prone / jumpover / sprint-boost
        // / dodge) are PLAYER-ONLY. Enemies must just navigate, chase, rotate,
        // attack, and die. Disable this component immediately so its Update
        // never decides to trigger any maneuver and any animator triggers it
        // would have set stay clean.
        enabled = false;
        return;
#pragma warning disable CS0162
        _enemy = GetComponent<EnemyController>();
        _agent = GetComponent<NavMeshAgent>();
        _anim = GetComponentInChildren<Animator>();
        _personality = GetComponent<EnemyPersonality>();

        if (_anim != null)
        {
            foreach (AnimatorControllerParameter p in _anim.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == "JumpOver") _hasJumpOver = true;
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == "Slide")    _hasSlide    = true;
                if (p.type == AnimatorControllerParameterType.Bool    && p.name == "IsProne")  _hasIsProne  = true;
            }
        }

        if (debugLog)
        {
            string animName = _anim != null && _anim.runtimeAnimatorController != null
                ? _anim.runtimeAnimatorController.name : "<none>";
            Debug.Log(
                $"[EnemyTacticalActions] {name} attached. anim='{animName}' " +
                $"JumpOver={_hasJumpOver} Slide={_hasSlide} IsProne={_hasIsProne}", this);
            if (!_hasJumpOver || !_hasSlide || !_hasIsProne)
                Debug.LogWarning(
                    $"[EnemyTacticalActions] {name} controller is missing tactical parameters. " +
                    "Run: Tools > PRISM-7 > Setup Tactical Animator (Enemy Crosby).", this);
        }

        _nextDecisionTime = Time.time + Random.Range(0f, decisionInterval);
#pragma warning restore CS0162
    }

    private void OnEnable()
    {
        if (_enemy != null) _enemy.OnDamaged += HandleDamaged;
    }

    private void OnDisable()
    {
        if (_enemy != null) _enemy.OnDamaged -= HandleDamaged;
        // Make absolutely sure we don't leak a modified speed if disabled mid-action.
        RestoreAgentSpeed();
    }

    private void HandleDamaged(GameObject attacker)
    {
        _reactiveDamageTime = Time.time;
    }

    private void Update()
    {
#if PUN_2_OR_NEWER
        if (MultiplayerMode.IsMultiplayer && !PhotonNetwork.IsMasterClient)
            return;
#endif

        if (_enemy == null || !_enemy.IsAlive) { RestoreAgentSpeed(); return; }
        if (_anim == null) return;

        // Auto-clear prone after hold so the AI never stays flat.
        if (_hasIsProne && _proneClearTime > 0f && Time.time >= _proneClearTime)
        {
            _anim.SetBool("IsProne", false);
            _proneClearTime = -1f;
            RestoreAgentSpeed();
            if (debugLog) Debug.Log($"[EnemyTacticalActions] {name} prone cleared", this);
        }

        // End slide / jumpover action lock when the window expires.
        if (_actionLockUntil > 0f && Time.time >= _actionLockUntil)
        {
            RestoreAgentSpeed();
            _actionLockUntil = -1f;
        }

        // Hold off on new decisions while an action is running so animations
        // play through fully instead of being canceled by a follow-up trigger.
        if (Time.time < _actionLockUntil) return;
        if (Time.time < _nextDecisionTime) return;
        _nextDecisionTime = Time.time + decisionInterval;

        EvaluateTactics();
    }

    private void EvaluateTactics()
    {
        Transform target = _enemy.CurrentTarget;
        float targetDist = target != null
            ? Vector3.Distance(transform.position, target.position)
            : float.PositiveInfinity;

        float hpFrac = _enemy.maxHealth > 0
            ? (float)_enemy.CurrentHealth / _enemy.maxHealth
            : 1f;

        float aggression = _personality != null ? _personality.aggression    : 0.55f;
        float bravery    = _personality != null ? _personality.bravery       : 0.55f;
        float mobility   = _personality != null ? _personality.mobility      : 0.55f;
        float reaction   = _personality != null ? _personality.reactionSpeed : 0.55f;

        float slideCd    = slideCooldownBase    * Mathf.Lerp(1.25f, 0.65f, mobility);
        float jumpOverCd = jumpOverCooldownBase * Mathf.Lerp(1.2f,  0.75f, aggression);
        float proneCd    = proneCooldownBase    * Mathf.Lerp(1.0f,  0.7f,  1f - bravery);

        // ── SLIDE ────────────────────────────────────────────────────────────
        if (_hasSlide && Time.time >= _nextSlideTime)
        {
            bool reactiveDodge = Time.time - _reactiveDamageTime <= slideDodgeWindow
                                 && Random.value < Mathf.Lerp(0.2f, 0.85f, reaction);
            bool chaseSlide = targetDist >= slideChaseMinDistance
                              && targetDist <= slideChaseMaxDistance
                              && Random.value < Mathf.Lerp(0.15f, 0.6f, aggression);
            bool disengage = hpFrac < slideDisengageHpThreshold
                             && Time.time - _reactiveDamageTime <= 1.5f
                             && Random.value < Mathf.Lerp(0.55f, 0.15f, bravery);

            if (reactiveDodge || chaseSlide || disengage)
            {
                _nextSlideTime = Time.time + slideCd - Random.Range(0f, reactionDelayMax * reaction);
                FireSlide(reactiveDodge ? "dodge" : (disengage ? "disengage" : "chase"));
                return;
            }
        }

        // ── JUMPOVER ─────────────────────────────────────────────────────────
        if (_hasJumpOver && Time.time >= _nextJumpOverTime && _agent != null && _agent.isOnNavMesh)
        {
            bool pathBlocked = _agent.hasPath
                && (_agent.pathStatus == NavMeshPathStatus.PathPartial
                    || _agent.pathStatus == NavMeshPathStatus.PathInvalid);

            if (pathBlocked)
            {
                if (_pathBlockedSince < 0f) _pathBlockedSince = Time.time;
                if (Time.time - _pathBlockedSince >= jumpOverBlockedSeconds
                    && Random.value < Mathf.Lerp(0.4f, 0.85f, aggression))
                {
                    _nextJumpOverTime = Time.time + jumpOverCd;
                    _pathBlockedSince = -1f;
                    FireJumpOver("blocked");
                    return;
                }
            }
            else
            {
                _pathBlockedSince = -1f;
                bool aggressiveGap = targetDist >= jumpOverGapMinDistance
                                     && targetDist <= jumpOverGapMaxDistance
                                     && Random.value < Mathf.Lerp(0.0f, 0.35f, aggression * bravery);
                if (aggressiveGap)
                {
                    _nextJumpOverTime = Time.time + jumpOverCd;
                    FireJumpOver("gapClose");
                    return;
                }
            }
        }

        // ── PRONE ────────────────────────────────────────────────────────────
        if (_hasIsProne && Time.time >= _nextProneTime && _proneClearTime < 0f
            && targetDist >= proneMinTargetDistance
            && hpFrac <= proneHpThreshold
            && Random.value < Mathf.Lerp(0.05f, 0.30f, 1f - bravery))
        {
            _nextProneTime = Time.time + proneCd;
            _proneClearTime = Time.time + proneHoldDuration;
            FireProne();
        }
    }

    // ── Action firing (animator + agent speed modulation + debug) ───────────

    private void FireSlide(string reason)
    {
        _anim.SetTrigger("Slide");
        BeginActionLock("Slide", slideActionDuration, slideSpeedMultiplier);
        if (debugLog) Debug.Log($"[EnemyTacticalActions] {name} SLIDE ({reason})", this);
    }

    private void FireJumpOver(string reason)
    {
        _anim.SetTrigger("JumpOver");
        BeginActionLock("JumpOver", jumpOverActionDuration, slideSpeedMultiplier);
        if (debugLog) Debug.Log($"[EnemyTacticalActions] {name} JUMPOVER ({reason})", this);
    }

    private void FireProne()
    {
        _anim.SetBool("IsProne", true);
        // Prone uses the same speed-modulation scaffolding but with a much
        // longer window (handled via _proneClearTime) and a slower multiplier.
        ApplyAgentSpeedMultiplier(proneSpeedMultiplier);
        _activeAction = "Prone";
        if (debugLog) Debug.Log($"[EnemyTacticalActions] {name} PRONE", this);
    }

    private void BeginActionLock(string actionName, float duration, float speedMul)
    {
        _activeAction = actionName;
        _actionLockUntil = Time.time + duration;
        ApplyAgentSpeedMultiplier(speedMul);
    }

    private void ApplyAgentSpeedMultiplier(float mul)
    {
        if (_agent == null || !_agent.enabled) return;
        if (_baseAgentSpeed < 0f) _baseAgentSpeed = _agent.speed; // capture once
        _agent.speed = _baseAgentSpeed * Mathf.Max(0.05f, mul);
    }

    private void RestoreAgentSpeed()
    {
        if (_agent != null && _agent.enabled && _baseAgentSpeed >= 0f)
            _agent.speed = _baseAgentSpeed;
        _baseAgentSpeed = -1f;
        _activeAction = "";
    }
}
