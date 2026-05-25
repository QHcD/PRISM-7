// Loading-screen / full-screen transition animations are not driven from HUDManager
// (see LoadingScreenUI + RuntimeMenuBuilder — motion overlays disabled project-wide).
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#if PUN_2_OR_NEWER
using Photon.Pun;
#endif

public class HUDManager : MonoBehaviour
{
    private sealed class ScoreboardRowUi
    {
        public GameObject Root;
        public TextMeshProUGUI NameText;
        public TextMeshProUGUI KillsText;
        public TextMeshProUGUI StatusText;
    }

    public static HUDManager Instance;

    public TextMeshProUGUI healthText;
    public TextMeshProUGUI scoreText;
    public TextMeshProUGUI levelText;
    public TextMeshProUGUI weaponText;
    public TextMeshProUGUI enemyCountText;
    public TextMeshProUGUI timerText;
    public Slider healthBar;

    private float elapsed;
    private float timeLimit = 300f;
    private PlayerHealth playerHealth;
    private PlayerController playerController;
    private RawImage minimapImage;
    private RectTransform minimapArrow;
    private RectTransform fullMapPlayerArrow;
    private RectTransform minimapRootRect;
    private RectTransform fullMapFrameRect;
    private readonly List<RectTransform> enemyMinimapArrows = new List<RectTransform>();
    private readonly List<RectTransform> enemyFullMapArrows = new List<RectTransform>();
    private Texture2D minimapCircleTexture;
    private GameObject fullMapOverlay;
    private RawImage fullMapImage;
    private TextMeshProUGUI fullMapHintText;
    private GameObject scoreboardOverlay;
    private TextMeshProUGUI scoreboardTitleText;
    private TextMeshProUGUI scoreboardSummaryText;
    private ScoreboardRowUi[] scoreboardRows;
    private GameObject matchFinishedOverlay;
    private bool matchFinished;
    private TMP_FontAsset prismFont;
    private Image healthFillVisual;
    private bool isFullMapVisible;
    private bool isScoreboardVisible;
    private MinimapCameraFollow _cachedMinimap;
    private Camera _cachedMinimapCamera;
    private float _mapArrowTimer;
    private float _scoreboardRefreshTimer;
    private readonly List<EnemyController> _mapEnemyScratch = new List<EnemyController>(48);
    private const float MapArrowRefreshInterval = 0.12f;
    private const float ScoreboardHiddenRefreshInterval = 0.4f;
    private const float ScoreboardVisibleRefreshInterval = 0.15f;
    private int localMultiplayerActorNumber = -1;
    private string localMultiplayerPlayerName = "Player";
    private bool singlePlayerInitComplete;
    private bool _mpTimerStarted;
    private bool _mpTimerZeroLogged;
    private float _mpTimerLogAccum;
    private float _mpPauseHudAliveLogTimer;
    private bool _mpHudModeApplied;
    private bool _mpEnemiesCounterVisible = true;

    // Kill counter (CoD-style)
    private TextMeshProUGUI killCountText;
    private int killCount;
    private CombatUIManager combatUIManager;

    // Damage flash
    private Image damageFlashImage;
    private float damageFlashAlpha;
    private const float DamageFlashDecay = 2.2f;

    // Low-health vignette
    private Image lowHealthImage;
    private float lowHealthPulse;

    public bool IsMatchFinished => matchFinished;

    /// <summary>Elapsed match seconds (drives the on-screen timer). Prefer this over duplicating time state.</summary>
    public float MatchElapsedSeconds => elapsed;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.StatsChanged -= HandleMatchStatsChanged;
    }

    private void Start()
    {
        prismFont = ResolvePrismFont();
        EnsureRuntimeHud();
        EnsureDamageFlashLayer();
        EnsureLowHealthLayer();

        if (GameManager.Instance == null)
        {
            if (!MultiplayerMode.IsMultiplayer)
                InitializeSinglePlayerGameplay();
            return;
        }

        BindMatchHudFromGameManager();

        if (!MultiplayerMode.IsMultiplayer)
            InitializeSinglePlayerGameplay();
        // In multiplayer, player refs are injected by NetworkPlayerSpawner
        // via InitForMultiplayerLocalPlayer() after the local player spawns.
    }

    private void BindMatchHudFromGameManager()
    {
        if (GameManager.Instance == null)
            return;

        // CRITICAL: in multiplayer, do NOT read GameManager.currentLevel. That
        // value is the single-player Continue save (PlayerPref "ContinueLevel")
        // and used to make the MP HUD say "LEVEL 8 / HAMMER" while the actual
        // equipped weapon was the L1 Tactical Knife.
        int level;
        string weaponName;
        if (MultiplayerMode.IsMultiplayer)
        {
            level = MultiplayerRuntimeConfig.GetSelectedLevel();
            weaponName = MultiplayerRuntimeConfig.GetSelectedWeaponName();
            timeLimit = 300f;
            elapsed = 0f;
        }
        else
        {
            level = GameManager.Instance.currentLevel;
            weaponName = playerController != null
                ? playerController.equippedWeaponName
                : GameManager.Instance.GetWeaponNameForLevel(level);
            timeLimit = GameManager.Instance.LevelTimeLimitSeconds;
            elapsed = 0f;
        }

        if (levelText != null)
            levelText.text = "LEVEL " + level;

        if (weaponText != null && !string.IsNullOrWhiteSpace(weaponName))
            weaponText.text = weaponName.ToUpperInvariant();

        if (!MultiplayerMode.IsMultiplayer)
        {
            UpdateScore(GameManager.Instance.score);
            UpdateEnemyCount(GameManager.Instance.enemiesRemaining);
        }
    }

    /// <summary>
    /// Single-player only: bind scene PlayerHealth/PlayerController, refresh HUD,
    /// enable input, lock cursor, and restore time scale.
    /// </summary>
    public void InitializeSinglePlayerGameplay()
    {
        if (MultiplayerMode.IsMultiplayer || singlePlayerInitComplete)
            return;

        playerHealth = FindFirstObjectByType<PlayerHealth>();
        playerController = FindFirstObjectByType<PlayerController>();
        if (playerHealth == null || playerController == null)
            return;

        singlePlayerInitComplete = true;

        if (GameManager.Instance != null)
            BindMatchHudFromGameManager();

        Debug.Log("[SPInit] HUD local player assigned");
        Debug.Log("[SPInit] PlayerController enabled");
        ApplySinglePlayerGameplayState();
        Debug.Log("[SPInit] TimeScale=1");

        if (MatchStatsManager.Instance != null)
        {
            string playerId = MatchStatsManager.BuildCombatantId(playerHealth);
            string playerLabel = PlayerProfile.HasUsername ? PlayerProfile.Username : "YOU";
            MatchStatsManager.Instance.RegisterCombatant(playerId, playerLabel, isPlayer: true, transform: playerHealth.transform);
            MatchStatsManager.Instance.StatsChanged -= HandleMatchStatsChanged;
            MatchStatsManager.Instance.StatsChanged += HandleMatchStatsChanged;
        }

        ApplySinglePlayerGameplayState();
    }

    /// <summary>Re-applies SP cursor, time scale, and HP after loading/countdown.</summary>
    public void ApplySinglePlayerGameplayState()
    {
        if (MultiplayerMode.IsMultiplayer)
            return;

        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (playerController != null)
        {
            playerController.enabled = true;
            CharacterController cc = playerController.GetComponent<CharacterController>();
            if (cc != null)
                cc.enabled = true;
        }

        if (playerHealth != null)
            UpdateHealth(playerHealth.currentHealth, playerHealth.maxHealth);
    }

    private void Update()
    {
        if (SceneManager.GetActiveScene().name == MultiplayerMode.MultiplayerSceneName)
            TickMultiplayerEscPause();

        float remaining;
        if (MultiplayerMode.IsMultiplayer)
        {
            // Local-only display countdown. Independent of MpMatchController so
            // it ticks even with MpMatchRules.Enabled == false. NEVER triggers
            // match end, victory, return-to-menu, or bot win — just clamps at 0.
            if (!_mpTimerStarted)
            {
                _mpTimerStarted = true;
                timeLimit = 300f;
                elapsed = 0f;
                Debug.Log("[MPTimer] started 300");
            }
            elapsed += Time.unscaledDeltaTime;
            remaining = Mathf.Max(0f, timeLimit - elapsed);

            // 30-second "MPTimer tick" log removed — not useful at runtime
            // and added noise to the live console during gameplay.
            if (remaining <= 0f && !_mpTimerZeroLogged)
            {
                _mpTimerZeroLogged = true;
                Debug.Log("[MPTimer] reached 0 no match end");
            }
        }
        else
        {
            elapsed += Time.deltaTime;
            remaining = Mathf.Max(0f, timeLimit - elapsed);
        }

        if (timerText != null)
            timerText.text = "TIME  " + Mathf.CeilToInt(remaining);

        if (GameManager.Instance != null && !MultiplayerMode.IsMultiplayer)
            GameManager.Instance.levelTime = elapsed;

        // Timer-end UI is owned by GameManager.ResolveTimedMatchWinner — it
        // decides Victory vs GameOver from the kill leaderboard and routes
        // directly to the correct MainMenu screen. The HUD must NOT also pop
        // a generic "MATCH FINISHED" overlay; that produced the double UI
        // sequence (MATCH FINISHED → MainMenu → Victory) the user reported.
        // ShowMatchFinishedOverlay remains available for explicit callers
        // (multiplayer drop, abort flows) via TriggerMatchFinishedOverlay.
        if (false && !matchFinished && remaining <= 0.001f && !MultiplayerMode.IsMultiplayer)
            ShowMatchFinishedOverlay();

        TickDamageFlash();
        TickLowHealthVignette();

        if (!MultiplayerMode.IsMultiplayer && !singlePlayerInitComplete)
            InitializeSinglePlayerGameplay();

        // MP self-heal: if InitForMultiplayerLocalPlayer never ran (race with
        // NetworkPlayerSpawner / HUDManager Awake order), find the local
        // Photon-owned player and bind HUD references here so HP text and
        // weapon never stay blank.
        if (MultiplayerMode.IsMultiplayer && (playerHealth == null || playerController == null))
            TryAutoBindLocalMultiplayerPlayer();

        if (playerHealth != null)
        {
            UpdateHealth(
                playerHealth.currentHealth,
                playerHealth.maxHealth,
                MultiplayerMode.IsMultiplayer ? playerHealth : null);
        }

        if (MultiplayerMode.IsMultiplayer)
        {
            // Pin level + weapon text to the MP single source of truth on every
            // frame. Cheap, idempotent, and self-healing — corrects any stale
            // value left over from BindMatchHudFromGameManager / scene-authored
            // text even if InitForMultiplayerLocalPlayer hasn't fired yet.
            int lvl = MultiplayerRuntimeConfig.GetSelectedLevel();
            string wpn = playerController != null && !string.IsNullOrWhiteSpace(playerController.equippedWeaponName)
                ? playerController.equippedWeaponName
                : MultiplayerRuntimeConfig.GetSelectedWeaponName();

            if (levelText != null)
            {
                string desired = "LEVEL " + lvl;
                if (levelText.text != desired) levelText.text = desired;
            }
            if (weaponText != null)
            {
                string desiredWpn = wpn.ToUpperInvariant();
                if (weaponText.text != desiredWpn) weaponText.text = desiredWpn;
            }
        }
        else if (playerController != null && weaponText != null)
        {
            weaponText.text = playerController.equippedWeaponName.ToUpperInvariant();
        }

        HandleOverlayInput();
        UpdateMinimap();
        TryRefreshScoreboardPeriodic();

        if (MultiplayerMode.IsMultiplayer)
            RefreshMultiplayerTopBarCounter();
    }

    private void TickMultiplayerEscPause()
    {
        // Periodic alive log removed — was firing every 5s in MP gameplay and
        // contributed to console spam during combat.
        MultiplayerEscPauseRouter.ProcessEscPauseInput();
    }

    private void EnsureMinimapReferences()
    {
        if (_cachedMinimap == null)
            _cachedMinimap = FindFirstObjectByType<MinimapCameraFollow>();
        if (_cachedMinimap != null && _cachedMinimapCamera == null)
            _cachedMinimapCamera = _cachedMinimap.GetComponent<Camera>();
    }

    private void TryRefreshScoreboardPeriodic()
    {
        float interval = isScoreboardVisible
            ? ScoreboardVisibleRefreshInterval
            : ScoreboardHiddenRefreshInterval;
        _scoreboardRefreshTimer -= Time.deltaTime;
        if (_scoreboardRefreshTimer > 0f)
            return;

        _scoreboardRefreshTimer = interval;
        RefreshScoreboard();
    }

    private void HandleMatchStatsChanged()
    {
        RefreshScoreboard(force: false);
    }

    public void UpdateHealth(float current, float max, PlayerHealth source = null)
    {
        if (MultiplayerMode.IsMultiplayer && playerHealth != null && source != null && source != playerHealth)
            return;

        float ratio = Mathf.Clamp01(current / Mathf.Max(1f, max));

        if (healthText != null)
        {
            healthText.text = "HP  " + Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(max);
            healthText.enabled = true;
            if (healthText.color.a < 0.05f)
                healthText.color = Color.white;
        }

        // Drive the fill image directly — this is the most reliable approach
        if (healthFillVisual != null)
        {
            healthFillVisual.fillAmount = ratio;
            // Shift colour from green → red as health drops
            healthFillVisual.color = Color.Lerp(new Color(0.92f, 0.18f, 0.18f), new Color(0.18f, 0.92f, 0.45f), ratio);
        }

        if (healthBar != null)
        {
            healthBar.value = ratio;
        }
    }

    public bool IsLocalHealthTarget(PlayerHealth ph)
    {
        return ph != null && ph == playerHealth;
    }

    public bool IsFullMapOpen => isFullMapVisible || (fullMapOverlay != null && fullMapOverlay.activeSelf);

    public void UpdateScore(int score)
    {
        if (scoreText != null)
        {
            scoreText.text = "SCORE  " + score;
        }
    }

    public void UpdateEnemyCount(int count)
    {
        if (MultiplayerMode.IsMultiplayer && MultiplayerRuntimeConfig.IsPurePvP())
            return;

        if (enemyCountText != null)
        {
            enemyCountText.text = "ENEMIES  " + count;
        }
    }

    private void RefreshMultiplayerTopBarCounter()
    {
        if (enemyCountText == null)
            return;

        MpGameMode mode = MultiplayerRuntimeConfig.GetSelectedGameMode();
        if (mode == MpGameMode.PurePvP)
        {
            if (_mpEnemiesCounterVisible)
            {
                _mpEnemiesCounterVisible = false;
                Debug.Log("[MPHUD] PurePvP hiding enemies counter");
            }

#if PUN_2_OR_NEWER
            if (PhotonNetwork.InRoom)
            {
                int playerCount = PhotonNetwork.PlayerList.Length;
                int maxPlayers = MpRoomConfig.ReadMaxPlayers();
                enemyCountText.text = $"PLAYERS  {playerCount}/{maxPlayers}";
            }
            else
#endif
            {
                enemyCountText.text = "PLAYERS  1";
            }

            enemyCountText.gameObject.SetActive(true);
            enemyCountText.enabled = true;
            return;
        }

        if (!_mpEnemiesCounterVisible)
        {
            _mpEnemiesCounterVisible = true;
            Debug.Log($"[MPHUD] enemies counter visible for mode = {mode}");
        }

        EnemyController.CopyAliveEnemies(_mapEnemyScratch);
        enemyCountText.text = "ENEMIES  " + _mapEnemyScratch.Count;
        enemyCountText.gameObject.SetActive(true);
        enemyCountText.enabled = true;
    }

    private void ApplyMultiplayerHudMode()
    {
        if (!MultiplayerMode.IsMultiplayer || _mpHudModeApplied)
            return;

        _mpHudModeApplied = true;
        RefreshMultiplayerTopBarCounter();
    }

    public void RegisterKill()
    {
        killCount++;

        if (killCountText != null)
            killCountText.text = "KILLS  " + killCount;

        // Live-refresh the leaderboard row (visible or not) so the sort
        // order updates the moment the kill is confirmed.
        RefreshScoreboard(force: false);
    }

    public void ShowXpPopup(int xpAmount, string eventLabel = "")
    {
        combatUIManager ??= CombatUIManager.CreateOrGet(GameObject.Find("HUDCanvas")?.transform, prismFont);
        combatUIManager?.ShowXpPopup(xpAmount, eventLabel);
    }

    public void ShowDamageFlash(float damageAmount)
    {
        // Scale flash intensity with damage (cap at 1.0)
        float intensity = Mathf.Clamp01(damageAmount / 40f);
        damageFlashAlpha = Mathf.Max(damageFlashAlpha, 0.25f + intensity * 0.55f);
        if (damageFlashImage != null)
        {
            damageFlashImage.color = new Color(0.85f, 0.05f, 0.05f, damageFlashAlpha);
        }
    }

    private void EnsureDamageFlashLayer()
    {
        GameObject canvasObject = GameObject.Find("HUDCanvas");
        if (canvasObject == null) return;

        GameObject existing = canvasObject.transform.Find("DamageFlash")?.gameObject;
        if (existing != null)
        {
            damageFlashImage = existing.GetComponent<Image>();
            return;
        }

        GameObject flashObj = new GameObject("DamageFlash");
        flashObj.transform.SetParent(canvasObject.transform, false);
        damageFlashImage = flashObj.AddComponent<Image>();
        damageFlashImage.color = new Color(0.85f, 0.05f, 0.05f, 0f);
        damageFlashImage.raycastTarget = false;
        Stretch(flashObj.GetComponent<RectTransform>());

        // Put it above HUD but below pause menu
        flashObj.GetComponent<Canvas>();
        flashObj.transform.SetAsLastSibling();
    }

    private void TickDamageFlash()
    {
        if (damageFlashImage == null) return;
        damageFlashAlpha = Mathf.MoveTowards(damageFlashAlpha, 0f, DamageFlashDecay * Time.deltaTime);
        damageFlashImage.color = new Color(0.85f, 0.05f, 0.05f, damageFlashAlpha);
    }

    private void EnsureLowHealthLayer()
    {
        GameObject canvasObject = GameObject.Find("HUDCanvas");
        if (canvasObject == null) return;

        GameObject existing = canvasObject.transform.Find("LowHealthVignette")?.gameObject;
        if (existing != null)
        {
            lowHealthImage = existing.GetComponent<Image>();
            return;
        }

        GameObject vigObj = new GameObject("LowHealthVignette");
        vigObj.transform.SetParent(canvasObject.transform, false);
        lowHealthImage = vigObj.AddComponent<Image>();
        lowHealthImage.color = new Color(0.75f, 0.02f, 0.02f, 0f);
        lowHealthImage.raycastTarget = false;
        Stretch(vigObj.GetComponent<RectTransform>());
        vigObj.transform.SetSiblingIndex(1); // just above bottom
    }

    private void TickLowHealthVignette()
    {
        if (lowHealthImage == null || playerHealth == null) return;

        float ratio = playerHealth.currentHealth / Mathf.Max(1f, playerHealth.maxHealth);
        if (ratio > 0.35f)
        {
            lowHealthImage.color = new Color(0.75f, 0.02f, 0.02f, 0f);
            return;
        }

        // Pulse faster as health drops
        float pulseSpeed = Mathf.Lerp(3.5f, 6.5f, 1f - ratio / 0.35f);
        lowHealthPulse += Time.deltaTime * pulseSpeed;
        float pulse = (Mathf.Sin(lowHealthPulse) + 1f) * 0.5f;
        float maxAlpha = Mathf.Lerp(0.12f, 0.52f, 1f - ratio / 0.35f);
        lowHealthImage.color = new Color(0.75f, 0.02f, 0.02f, pulse * maxAlpha);
    }

    private void EnsureRuntimeHud()
    {
        GameObject canvasObject = GameObject.Find("HUDCanvas");
        ResolveHealthHudWidgets(canvasObject);

        if (healthText != null && scoreText != null && levelText != null &&
            weaponText != null && enemyCountText != null && timerText != null && healthBar != null)
        {
            if (canvasObject != null)
            {
                EnsureCombatUi(canvasObject.transform);
                EnsureMinimap(canvasObject.transform);
                EnsureFullMapOverlay(canvasObject.transform);
                EnsureScoreboardOverlay(canvasObject.transform);
                EnsurePlayerIdentityChip(canvasObject.transform);
        if (canvasObject != null)
            EnsureScoreLabelPresentation(canvasObject.transform);
    }
            return;
        }

        if (canvasObject == null)
        {
            canvasObject = new GameObject("HUDCanvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            CanvasScaler newScaler = canvasObject.AddComponent<CanvasScaler>();
            newScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            newScaler.referenceResolution = new Vector2(1920f, 1080f);
            newScaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();
        }

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        GameObject topBar = CreateImage(canvasObject.transform, "TopBar",
            new Color(0.05f, 0.07f, 0.11f, 0.52f),
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -58f), new Vector2(0f, 116f));

        CreateImage(topBar.transform, "TopBarAccent",
            new Color(0.30f, 0.76f, 1f, 0.35f),
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(0f, 2f));

        // ── Health panel (bottom-left) ──────────────────────────────────────────
        // Outer panel: anchored bottom-left, fixed size
        GameObject bottomPanel = CreateImage(canvasObject.transform, "BottomHealthPanel",
            new Color(0.05f, 0.07f, 0.11f, 0.78f),
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(240f, 62f), new Vector2(440f, 80f));

        // ── Bar track (dark background behind the green bar) ────────────────────
        // Positioned inside the panel, bottom half
        GameObject healthTrack = CreateImage(bottomPanel.transform, "HealthTrack",
            new Color(0.08f, 0.10f, 0.14f, 1f),
            new Vector2(0f, 0f), new Vector2(1f, 0f),   // anchored at bottom of panel
            new Vector2(0f, 10f),                         // 10 px from bottom edge, centred
            new Vector2(-24f, 18f));                      // full width minus 12 px margin each side, 18 px tall

        // ── Green fill — simple Image that we scale via fillAmount ───────────────
        // Anchored LEFT inside the track so width shrinks rightward as HP drops
        GameObject healthFillObj = new GameObject("HealthFill");
        healthFillObj.transform.SetParent(healthTrack.transform, false);
        Image healthFillImage = healthFillObj.AddComponent<Image>();
        healthFillImage.color = new Color(0.18f, 0.92f, 0.45f, 1f);
        healthFillImage.type = Image.Type.Filled;
        healthFillImage.fillMethod = Image.FillMethod.Horizontal;
        healthFillImage.fillOrigin = 0;
        healthFillImage.fillAmount = 1f;
        healthFillVisual = healthFillImage;
        RectTransform fillRect = healthFillObj.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(2f, 2f);
        fillRect.offsetMax = new Vector2(-2f, -2f);

        // Dummy Slider kept for legacy UpdateHealth path
        if (healthBar == null)
        {
            healthBar = healthTrack.AddComponent<Slider>();
            healthBar.fillRect = fillRect;
            healthBar.minValue = 0f;
            healthBar.maxValue = 1f;
            healthBar.value = 1f;
            healthBar.direction = Slider.Direction.LeftToRight;
            healthBar.interactable = false;
            healthBar.targetGraphic = healthFillImage;
        }

        scoreText ??= CreateText(canvasObject.transform, "ScoreText",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(218f, -36f), new Vector2(380f, 44f),
            34f, FontStyles.Bold, TextAlignmentOptions.Left);
        EnsureScoreLabelPresentation(canvasObject.transform);

        levelText ??= CreateText(topBar.transform, "LevelText",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 22f), new Vector2(320f, 40f),
            36f, FontStyles.Bold, TextAlignmentOptions.Center);

        weaponText ??= CreateText(topBar.transform, "WeaponText",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -12f), new Vector2(500f, 30f),
            24f, FontStyles.Normal, TextAlignmentOptions.Center);

        // Restore classic layout: top-right ENEMIES + TIME (pre-regression).
        enemyCountText ??= CreateText(topBar.transform, "EnemyText",
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-215f, 14f), new Vector2(380f, 40f),
            28f, FontStyles.Bold, TextAlignmentOptions.Right);

        timerText ??= CreateText(topBar.transform, "TimerText",
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-215f, -18f), new Vector2(300f, 34f),
            24f, FontStyles.Normal, TextAlignmentOptions.Right);

        healthText ??= CreateText(bottomPanel.transform, "HPText",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(238f, -28f), new Vector2(420f, 30f),
            24f, FontStyles.Bold, TextAlignmentOptions.Left);

        // Kill counter — bottom-left, above health panel
        killCountText ??= CreateText(canvasObject.transform, "KillCountText",
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(240f, 152f), new Vector2(300f, 34f),
            26f, FontStyles.Bold, TextAlignmentOptions.Left);
        killCountText.text = "KILLS  0";
        killCountText.color = new Color(1f, 0.85f, 0.3f, 1f);

        scoreText.color = new Color(0.97f, 0.98f, 1f, 1f);
        levelText.color = new Color(0.97f, 0.98f, 1f, 1f);
        weaponText.color = new Color(0.66f, 0.88f, 1f, 1f);
        enemyCountText.color = new Color(0.97f, 0.98f, 1f, 1f);
        timerText.color = new Color(0.86f, 0.92f, 1f, 1f);
        healthText.color = Color.white;

        EnsureCombatUi(canvasObject.transform);
        EnsureMinimap(canvasObject.transform);
        EnsureFullMapOverlay(canvasObject.transform);
        EnsureScoreboardOverlay(canvasObject.transform);
        EnsurePlayerIdentityChip(canvasObject.transform);
        ResolveHealthHudWidgets(canvasObject);
    }

    /// <summary>
    /// Binds HP label + bar fill from an existing HUD canvas (EXE builds may have
    /// the bar wired in-scene while the TMP reference on HUDManager is still null).
    /// </summary>
    private void ResolveHealthHudWidgets(GameObject canvasObject = null)
    {
        canvasObject ??= GameObject.Find("HUDCanvas");
        if (canvasObject == null)
            return;

        if (healthText == null)
        {
            Transform hpTransform = canvasObject.transform.Find("BottomHealthPanel/HPText");
            if (hpTransform == null)
                hpTransform = FindHudChildRecursive(canvasObject.transform, "HPText");
            if (hpTransform != null)
                healthText = hpTransform.GetComponent<TextMeshProUGUI>();
        }

        if (healthFillVisual == null)
        {
            Transform fillTransform = canvasObject.transform.Find("BottomHealthPanel/HealthTrack/HealthFill");
            if (fillTransform == null)
                fillTransform = FindHudChildRecursive(canvasObject.transform, "HealthFill");
            if (fillTransform != null)
                healthFillVisual = fillTransform.GetComponent<Image>();
        }

        if (healthBar == null)
        {
            Transform trackTransform = canvasObject.transform.Find("BottomHealthPanel/HealthTrack");
            if (trackTransform == null)
                trackTransform = FindHudChildRecursive(canvasObject.transform, "HealthTrack");
            if (trackTransform != null)
                healthBar = trackTransform.GetComponent<Slider>();
        }

        if (healthText != null)
        {
            healthText.gameObject.SetActive(true);
            healthText.enabled = true;
            if (prismFont != null && healthText.font == null)
                healthText.font = prismFont;
            if (healthText.color.a < 0.05f)
                healthText.color = Color.white;
        }
    }

    /// <summary>
    /// SCORE label only: no panel/Image behind the text (TopBar bg must not sit under it).
    /// </summary>
    private void EnsureScoreLabelPresentation(Transform hudCanvas)
    {
        if (scoreText == null || hudCanvas == null)
            return;

        Image labelBg = scoreText.GetComponent<Image>();
        if (labelBg != null)
            Object.Destroy(labelBg);

        Shadow uiShadow = scoreText.GetComponent<Shadow>();
        if (uiShadow != null)
            Object.Destroy(uiShadow);

        Outline uiOutline = scoreText.GetComponent<Outline>();
        if (uiOutline != null)
            Object.Destroy(uiOutline);

        for (int i = scoreText.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = scoreText.transform.GetChild(i);
            if (child == null) continue;
            Image childImg = child.GetComponent<Image>();
            if (childImg != null)
                Object.Destroy(child.gameObject);
        }

        string[] legacyBgNames = { "ScoreTextBackground", "ScoreBackground", "Background", "ScorePanel", "ScoreBg" };
        for (int n = 0; n < legacyBgNames.Length; n++)
        {
            Transform legacy = hudCanvas.Find("TopBar/" + legacyBgNames[n]);
            if (legacy == null)
                legacy = hudCanvas.Find(legacyBgNames[n]);
            if (legacy == null)
                legacy = FindHudChildRecursive(hudCanvas, legacyBgNames[n]);
            if (legacy == null || legacy == scoreText.transform)
                continue;

            Object.Destroy(legacy.gameObject);
        }

        scoreText.raycastTarget = false;

        Transform parent = scoreText.transform.parent;
        if (parent != null && parent.name == "TopBar")
        {
            RectTransform rect = scoreText.rectTransform;
            Vector2 size = rect.sizeDelta;
            int sibling = scoreText.transform.GetSiblingIndex();

            scoreText.transform.SetParent(hudCanvas, false);

            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(218f, -36f);
            rect.sizeDelta = size.sqrMagnitude > 1f ? size : new Vector2(380f, 44f);
            scoreText.transform.SetSiblingIndex(Mathf.Min(sibling, hudCanvas.childCount - 1));
        }
    }

    private static Transform FindHudChildRecursive(Transform root, string childName)
    {
        if (root == null)
            return null;

        if (root.name == childName)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindHudChildRecursive(root.GetChild(i), childName);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>Small top-left name strip — semi-transparent, sized to the handle.</summary>
    private void EnsurePlayerIdentityChip(Transform canvasTransform)
    {
        if (canvasTransform == null) return;

        string hudName = PlayerProfile.HasUsername ? PlayerProfile.Username : "OPERATIVE";
        GameObject chip = CreateImage(canvasTransform, "PlayerIdentityChip",
            new Color(0.12f, 0.12f, 0.14f, 0.5f),
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -68f), new Vector2(268f, 46f));

        TextMeshProUGUI nameTmp = CreateText(chip.transform, "PlayerHudName",
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            20f, FontStyles.Bold, TextAlignmentOptions.Left);
        nameTmp.text = "  " + hudName;
        nameTmp.color = new Color(0.96f, 0.97f, 1f, 1f);
        nameTmp.textWrappingMode = TextWrappingModes.NoWrap;
        if (prismFont != null) nameTmp.font = prismFont;
    }

    private void EnsureCombatUi(Transform canvasTransform)
    {
        combatUIManager = CombatUIManager.CreateOrGet(canvasTransform, prismFont);
    }

    private GameObject CreateImage(Transform parent, string name, Color color,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
    {
        GameObject existing = parent.Find(name)?.gameObject;
        if (existing != null)
        {
            return existing;
        }

        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        Image image = obj.AddComponent<Image>();
        image.color = color;

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return obj;
    }

    private TextMeshProUGUI CreateText(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 position, Vector2 size, float fontSize, FontStyles fontStyle, TextAlignmentOptions alignment)
    {
        GameObject existing = parent.Find(name)?.gameObject;
        if (existing == null)
        {
            existing = new GameObject(name);
            existing.transform.SetParent(parent, false);
        }

        TextMeshProUGUI tmp = existing.GetComponent<TextMeshProUGUI>();
        if (tmp == null)
        {
            tmp = existing.AddComponent<TextMeshProUGUI>();
        }

        tmp.fontSize = fontSize;
        tmp.fontStyle = fontStyle;
        tmp.alignment = alignment;
        tmp.color = Color.white;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        if (prismFont != null)
            tmp.font = prismFont;
        if (tmp.font == null)
        {
            TMP_FontAsset lib = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            if (lib != null)
                tmp.font = lib;
        }

        RectTransform rect = existing.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return tmp;
    }

    private void EnsureMinimap(Transform canvasTransform)
    {
        // Hard-guard every step: a missing scene-authored child (or a child
        // that has the wrong Graphic component) used to throw NRE here and
        // abort the rest of HUD setup (health/timer/level/weapon never bound).
        try
        {
            if (canvasTransform == null)
            {
                Debug.Log("[MPHUD] EnsureMinimap skipped safely because canvasTransform missing");
                return;
            }
            Debug.Log("[MPHUD] EnsureMinimap canvas found");

            GameObject minimapRoot = CreateImage(canvasTransform, "MinimapRoot",
                new Color(0.10f, 0.08f, 0.05f, 0.82f),
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-130f, 128f), new Vector2(188f, 188f));
            if (minimapRoot == null)
            {
                Debug.Log("[MPHUD] EnsureMinimap skipped safely because MinimapRoot missing");
                return;
            }
            minimapRootRect = minimapRoot.GetComponent<RectTransform>();

            Image minimapRootImage = minimapRoot.GetComponent<Image>() ?? minimapRoot.AddComponent<Image>();
            minimapRootImage.sprite = GetOrCreateCircleSprite();
            minimapRootImage.type = Image.Type.Simple;
            minimapRootImage.preserveAspect = true;
            if (minimapRoot.GetComponent<Mask>() == null)
                minimapRoot.AddComponent<Mask>().showMaskGraphic = true;

            GameObject minimapContent = CreateImage(minimapRoot.transform, "MinimapContent",
                Color.white,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(172f, 172f));
            if (minimapContent == null)
            {
                Debug.Log("[MPHUD] EnsureMinimap skipped safely because MinimapContent missing");
                return;
            }

            // Existing scene MinimapContent may already host a RawImage (Unity
            // only allows one Graphic per GO). GetComponent<Image>() returns
            // null in that case → NRE on .sprite. Be defensive.
            Image minimapContentImage = minimapContent.GetComponent<Image>();
            if (minimapContentImage != null)
            {
                minimapContentImage.sprite = GetOrCreateCircleSprite();
                minimapContentImage.color = Color.white;
            }

            minimapImage = minimapContent.GetComponent<RawImage>();
            if (minimapImage == null)
            {
                if (minimapContentImage != null)
                    DestroyImmediate(minimapContentImage);
                minimapImage = minimapContent.AddComponent<RawImage>();
            }
            minimapImage.color = Color.white;

            GameObject arrowObject = CreateImage(minimapRoot.transform, "PlayerArrow",
                new Color(0.25f, 1f, 0.35f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 42f));
            if (arrowObject != null)
            {
                Image playerArrowImage = arrowObject.GetComponent<Image>() ?? arrowObject.AddComponent<Image>();
                playerArrowImage.sprite = GetOrCreateTriangleSprite();
                playerArrowImage.color = new Color(0.25f, 1f, 0.35f, 1f);
                minimapArrow = arrowObject.GetComponent<RectTransform>();
                if (minimapArrow != null)
                    minimapArrow.sizeDelta = new Vector2(40f, 50f);
            }

            EnsureEnemyArrowPool(minimapRoot.transform, enemyMinimapArrows, "EnemyMiniArrow_", 24f);
            EnsureMinimapReferences();
            Debug.Log("[MPHUD] EnsureMinimap created/found minimap");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[MPHUD] EnsureMinimap skipped safely because exception: " + ex.Message);
        }
    }

    private void EnsureFullMapOverlay(Transform canvasTransform)
    {
        fullMapOverlay = canvasTransform.Find("FullMapOverlay")?.gameObject;
        if (fullMapOverlay == null)
        {
            fullMapOverlay = new GameObject("FullMapOverlay");
            fullMapOverlay.transform.SetParent(canvasTransform, false);
            Image backdrop = fullMapOverlay.AddComponent<Image>();
            // Light dim of the world behind the map — was 0.68 (too opaque and
            // contributed to "huge dark panel" feeling). 0.35 keeps focus on
            // the map while still de-emphasising gameplay behind it.
            backdrop.color = new Color(0.02f, 0.04f, 0.08f, 0.35f);
            Stretch(fullMapOverlay.GetComponent<RectTransform>());

            // Frame is just a thin border tint now — the RawImage on top fully
            // covers the centre area, so we don't need the dark slab that used
            // to dominate the screen.
            GameObject frame = CreateImage(fullMapOverlay.transform, "FullMapFrame",
                new Color(0.08f, 0.12f, 0.18f, 0.55f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1040f, 760f));
            fullMapFrameRect = frame.GetComponent<RectTransform>();
            frame.AddComponent<Outline>().effectColor = new Color(0.32f, 0.78f, 1f, 0.24f);

            // Procedural arena background — behind the RenderTexture RawImage.
            // Acts as a readable fallback when the map camera is unable to
            // render geometry (camera off-mesh, layer-mask race, etc.) so the
            // map never reads as a dark slab. Light slate floor + cyan border
            // approximating the arena footprint.
            GameObject floorObject = new GameObject("FullMapFallbackFloor");
            floorObject.transform.SetParent(frame.transform, false);
            Image floorImg = floorObject.AddComponent<Image>();
            floorImg.color = new Color(0.55f, 0.62f, 0.72f, 1f);
            floorImg.raycastTarget = false;
            RectTransform floorRect = floorObject.GetComponent<RectTransform>();
            floorRect.anchorMin = new Vector2(0.5f, 0.5f);
            floorRect.anchorMax = new Vector2(0.5f, 0.5f);
            floorRect.anchoredPosition = new Vector2(0f, 18f);
            floorRect.sizeDelta = new Vector2(920f, 620f);

            GameObject borderObject = new GameObject("FullMapFallbackBorder");
            borderObject.transform.SetParent(floorObject.transform, false);
            Image borderImg = borderObject.AddComponent<Image>();
            borderImg.color = new Color(0f, 0f, 0f, 0f);
            borderImg.raycastTarget = false;
            Outline borderOutline = borderObject.AddComponent<Outline>();
            borderOutline.effectColor = new Color(0.28f, 0.62f, 0.92f, 0.95f);
            borderOutline.effectDistance = new Vector2(4f, -4f);
            RectTransform borderRect = borderObject.GetComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = Vector2.zero;
            borderRect.offsetMax = Vector2.zero;

            GameObject mapImageObject = new GameObject("FullMapImage");
            mapImageObject.transform.SetParent(frame.transform, false);
            fullMapImage = mapImageObject.AddComponent<RawImage>();
            fullMapImage.color = new Color(1f, 1f, 1f, 0.85f); // tint so the floor shows through
            fullMapImage.raycastTarget = false;
            RectTransform mapRect = mapImageObject.GetComponent<RectTransform>();
            mapRect.anchorMin = new Vector2(0.5f, 0.5f);
            mapRect.anchorMax = new Vector2(0.5f, 0.5f);
            mapRect.anchoredPosition = new Vector2(0f, 18f);
            mapRect.sizeDelta = new Vector2(920f, 620f);

            fullMapHintText = CreateText(frame.transform, "FullMapHint",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(760f, 40f),
                24f, FontStyles.Bold, TextAlignmentOptions.Center);
            fullMapHintText.text = "TAB  TOGGLE FULL MAP";
            fullMapHintText.color = new Color(0.76f, 0.90f, 1f, 0.92f);

            GameObject fullPlayerArrow = CreateImage(frame.transform, "FullMapPlayerArrow",
                new Color(0.25f, 1f, 0.35f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(42f, 54f));
            fullPlayerArrow.GetComponent<Image>().sprite = GetOrCreateTriangleSprite();
            fullMapPlayerArrow = fullPlayerArrow.GetComponent<RectTransform>();
        }
        else
        {
            fullMapFrameRect = fullMapOverlay.transform.Find("FullMapFrame")?.GetComponent<RectTransform>();
            fullMapImage = fullMapOverlay.transform.Find("FullMapFrame/FullMapImage")?.GetComponent<RawImage>();
            fullMapHintText = fullMapOverlay.transform.Find("FullMapFrame/FullMapHint")?.GetComponent<TextMeshProUGUI>();
            fullMapPlayerArrow = fullMapOverlay.transform.Find("FullMapFrame/FullMapPlayerArrow")?.GetComponent<RectTransform>();

            // Re-skin a scene-authored overlay so the dark-slab look from older
            // scene assets is harmonised with the lighter runtime build.
            Image cachedBackdrop = fullMapOverlay.GetComponent<Image>();
            if (cachedBackdrop != null) cachedBackdrop.color = new Color(0.02f, 0.04f, 0.08f, 0.35f);
            Image cachedFrame = fullMapFrameRect != null ? fullMapFrameRect.GetComponent<Image>() : null;
            if (cachedFrame != null) cachedFrame.color = new Color(0.08f, 0.12f, 0.18f, 0.55f);
            if (fullMapImage != null) fullMapImage.color = Color.white;
        }

        Transform frameTransform = fullMapFrameRect != null ? fullMapFrameRect.transform : null;
        if (frameTransform != null && fullMapPlayerArrow == null)
        {
            GameObject fullPlayerArrow = CreateImage(frameTransform, "FullMapPlayerArrow",
                new Color(0.25f, 1f, 0.35f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(42f, 54f));
            fullPlayerArrow.GetComponent<Image>().sprite = GetOrCreateTriangleSprite();
            fullMapPlayerArrow = fullPlayerArrow.GetComponent<RectTransform>();
        }
        if (frameTransform != null)
            EnsureEnemyArrowPool(frameTransform, enemyFullMapArrows, "EnemyFullMapArrow_", 34f);

        isFullMapVisible = false;
        fullMapOverlay.SetActive(false);
        EnsureMinimapReferences();
        if (_cachedMinimap != null)
            _cachedMinimap.SetFullMapMode(false, playerController != null ? playerController.transform : null);
    }

    private void EnsureScoreboardOverlay(Transform canvasTransform)
    {
        scoreboardOverlay = canvasTransform.Find("ScoreboardOverlay")?.gameObject;
        if (scoreboardOverlay == null)
        {
            scoreboardOverlay = new GameObject("ScoreboardOverlay");
            scoreboardOverlay.transform.SetParent(canvasTransform, false);
            Image backdrop = scoreboardOverlay.AddComponent<Image>();
            backdrop.color = new Color(0.01f, 0.03f, 0.06f, 0.72f);
            Stretch(scoreboardOverlay.GetComponent<RectTransform>());

            // ── Panel — centred on screen (anchor + pivot both at 0.5,0.5) ─────
            // Previously anchored to the right edge (1,0.5), which caused the
            // panel to be off-centre and resolution-dependent.
            GameObject panel = CreateImage(scoreboardOverlay.transform, "ScoreboardPanel",
                new Color(0.05f, 0.08f, 0.14f, 0.95f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 620f));

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.pivot = new Vector2(0.5f, 0.5f);

            Outline panelOutline = panel.AddComponent<Outline>();
            panelOutline.effectColor = new Color(0.32f, 0.78f, 1f, 0.30f);
            panelOutline.effectDistance = new Vector2(1.5f, -1.5f);

            // ── Title ────────────────────────────────────────────────────────
            scoreboardTitleText = CreateText(panel.transform, "ScoreboardTitle",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(580f, 52f),
                30f, FontStyles.Bold, TextAlignmentOptions.Center);
            scoreboardTitleText.text = "MATCH STATS";
            scoreboardTitleText.color = Color.white;
            scoreboardTitleText.enableAutoSizing = true;
            scoreboardTitleText.fontSizeMin = 22f;
            scoreboardTitleText.fontSizeMax = 30f;
            scoreboardTitleText.overflowMode = TextOverflowModes.Truncate;

            // Thin separator line under title
            GameObject sep = new GameObject("TitleSeparator");
            sep.transform.SetParent(panel.transform, false);
            Image sepImg = sep.AddComponent<Image>();
            sepImg.color = new Color(0.32f, 0.78f, 1f, 0.35f);
            RectTransform sepRect = sep.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0.5f, 1f);
            sepRect.anchorMax = new Vector2(0.5f, 1f);
            sepRect.pivot     = new Vector2(0.5f, 1f);
            sepRect.anchoredPosition = new Vector2(0f, -84f);
            sepRect.sizeDelta        = new Vector2(580f, 2f);

            scoreboardSummaryText = CreateText(panel.transform, "ScoreboardSummary",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(580f, 28f),
                17f, FontStyles.Bold, TextAlignmentOptions.Left);
            scoreboardSummaryText.text = "LIVE MATCH LEADERBOARD";
            scoreboardSummaryText.color = new Color(0.50f, 0.75f, 1f, 0.80f);

            // ── Entry rows — 5 rows, 82 px apart ────────────────────────────
            CreateScoreboardHeader(panel.transform);
            CreateScoreboardRows(panel.transform);
            // Footer intentionally removed — the "CAPS LOCK TOGGLE LEADERBOARD"
            // hint is no longer shown.
        }
        else
        {
            scoreboardTitleText = scoreboardOverlay.transform.Find("ScoreboardPanel/ScoreboardTitle")?.GetComponent<TextMeshProUGUI>();
            scoreboardSummaryText = scoreboardOverlay.transform.Find("ScoreboardPanel/ScoreboardSummary")?.GetComponent<TextMeshProUGUI>();
            CacheScoreboardRows();
            if (scoreboardRows == null || scoreboardRows.Length == 0 || scoreboardRows[0] == null)
            {
                Transform panel = scoreboardOverlay.transform.Find("ScoreboardPanel");
                if (panel != null)
                {
                    CreateScoreboardHeader(panel);
                    CreateScoreboardRows(panel);
                }
            }
        }

        if (scoreboardTitleText != null)
        {
            scoreboardTitleText.text = MultiplayerMode.IsMultiplayer ? "MULTIPLAYER LEADERBOARD" : "MATCH STATS";
            scoreboardTitleText.enableAutoSizing = true;
            scoreboardTitleText.fontSizeMin = 22f;
            scoreboardTitleText.fontSizeMax = 30f;
            RectTransform titleRect = scoreboardTitleText.rectTransform;
            if (titleRect != null)
                titleRect.sizeDelta = new Vector2(580f, 52f);
        }

        Transform existingPanel = scoreboardOverlay != null ? scoreboardOverlay.transform.Find("ScoreboardPanel") : null;
        if (existingPanel != null)
        {
            RectTransform panelRect = existingPanel as RectTransform;
            if (panelRect != null)
                panelRect.sizeDelta = new Vector2(640f, 620f);
        }

        scoreboardOverlay.SetActive(false);
    }

    private void UpdateMinimap()
    {
        if (minimapImage == null)
        {
            return;
        }

        EnsureMinimapReferences();
        MinimapCameraFollow minimap = _cachedMinimap;
        if (minimap != null)
        {
            RenderTexture texture = minimap.EnsureRenderTexture();
            if (minimapImage.texture != texture)
            {
                minimapImage.texture = texture;
            }

            if (fullMapImage != null && fullMapImage.texture != texture)
                fullMapImage.texture = texture;

            if (playerController != null && !minimap.lockToArenaCenter)
            {
                // Camera Y must sit ABOVE the player (and arena ceiling) — using the
                // raw 'height' field as world-Y put the camera below ceilings, so
                // the map rendered as a dark rectangle. Offset from the player's Y
                // ensures we're always overhead regardless of arena elevation.
                minimap.transform.position = new Vector3(
                    playerController.transform.position.x,
                    playerController.transform.position.y + minimap.height,
                    playerController.transform.position.z);
                minimap.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }

            if (_cachedMinimapCamera != null)
                _cachedMinimapCamera.Render();
        }

        if (minimapArrow != null && playerController != null)
        {
            // Camera now follows the player, so player is always at minimap centre
            minimapArrow.anchoredPosition = Vector2.zero;
            minimapArrow.localEulerAngles = new Vector3(0f, 0f, -playerController.transform.eulerAngles.y);
        }

        if (minimapArrow != null)
            minimapArrow.gameObject.SetActive(!isFullMapVisible);

        _mapArrowTimer -= Time.deltaTime;
        if (_mapArrowTimer <= 0f)
        {
            _mapArrowTimer = MapArrowRefreshInterval;
            UpdateMapArrows(minimap);
        }
    }

    private const int MaxEnemyMapArrows = 32;

    private void EnsureEnemyArrowPool(Transform parent, List<RectTransform> pool, string prefix, float size)
    {
        if (parent == null || pool == null) return;

        for (int i = pool.Count; i < MaxEnemyMapArrows; i++)
        {
            GameObject arrow = CreateImage(parent, prefix + i,
                new Color(1f, 0.18f, 0.16f, 0.95f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size * 1.25f));
            arrow.GetComponent<Image>().sprite = GetOrCreateTriangleSprite();
            arrow.SetActive(false);
            pool.Add(arrow.GetComponent<RectTransform>());
        }
    }

    private void UpdateMapArrows(MinimapCameraFollow minimap)
    {
        if (minimap == null || playerController == null)
            return;

        EnemyController.CopyAliveEnemies(_mapEnemyScratch);
        UpdateFullMapPlayerArrow(minimap);
        UpdateEnemyArrowPool(enemyMinimapArrows, _mapEnemyScratch, minimap, minimapRootRect, false);
        UpdateEnemyArrowPool(enemyFullMapArrows, _mapEnemyScratch, minimap, fullMapFrameRect, true);
    }

    private void UpdateFullMapPlayerArrow(MinimapCameraFollow minimap)
    {
        if (fullMapPlayerArrow == null || fullMapFrameRect == null || playerController == null)
            return;

        Camera mapCamera = minimap.GetComponent<Camera>();
        if (mapCamera == null)
            return;

        float halfSize = Mathf.Min(fullMapFrameRect.rect.width, fullMapFrameRect.rect.height) * 0.5f;
        Vector2 pos = WorldToMapPosition(playerController.transform.position, minimap.transform.position, mapCamera.orthographicSize, halfSize);
        fullMapPlayerArrow.anchoredPosition = pos;
        fullMapPlayerArrow.localEulerAngles = new Vector3(0f, 0f, -playerController.transform.eulerAngles.y);
        fullMapPlayerArrow.gameObject.SetActive(isFullMapVisible);
    }

    private void UpdateEnemyArrowPool(List<RectTransform> pool, List<EnemyController> enemies, MinimapCameraFollow minimap, RectTransform mapRect, bool fullMap)
    {
        if (pool == null || mapRect == null || enemies == null)
            return;

        Camera mapCamera = _cachedMinimapCamera != null ? _cachedMinimapCamera : minimap.GetComponent<Camera>();
        if (mapCamera == null)
            return;

        float halfSize = Mathf.Min(mapRect.rect.width, mapRect.rect.height) * 0.5f;
        int used = 0;

        for (int i = 0; i < enemies.Count && used < pool.Count; i++)
        {
            EnemyController enemy = enemies[i];
            if (enemy == null || !enemy.IsAlive)
                continue;

            RectTransform arrow = pool[used++];
            Vector2 pos = WorldToMapPosition(enemy.transform.position, minimap.transform.position, mapCamera.orthographicSize, halfSize);
            bool inside = pos.sqrMagnitude <= halfSize * halfSize;
            arrow.gameObject.SetActive(inside && (fullMap ? isFullMapVisible : !isFullMapVisible));
            if (!inside) continue;

            arrow.anchoredPosition = pos;
            arrow.localEulerAngles = new Vector3(0f, 0f, -enemy.transform.eulerAngles.y);
        }

        for (int i = used; i < pool.Count; i++)
            if (pool[i] != null) pool[i].gameObject.SetActive(false);
    }

    private static Vector2 WorldToMapPosition(Vector3 world, Vector3 mapCenter, float orthographicSize, float halfSize)
    {
        float worldRadius = Mathf.Max(1f, orthographicSize);
        float x = (world.x - mapCenter.x) / worldRadius * halfSize;
        float y = (world.z - mapCenter.z) / worldRadius * halfSize;
        return new Vector2(x, y);
    }

    private void HandleOverlayInput()
    {
        if (Keyboard.current == null)
            return;

        if (MultiplayerMode.IsMultiplayer)
        {
            if (Cursor.visible)
                return;

            if (Keyboard.current.tabKey.wasPressedThisFrame)
                SetFullMapVisible(!IsFullMapOpen);
        }
        else
        {
            SetFullMapVisible(Keyboard.current.tabKey.isPressed);
        }

        if (Keyboard.current.capsLockKey.wasPressedThisFrame)
            ToggleScoreboard();
    }

    public bool CloseFullMapFromEscape()
    {
        if (!IsFullMapOpen)
            return false;

        SetFullMapVisible(false, force: true);
        Debug.Log("[MPUI] ESC closed full map");
        return true;
    }

    public void ForceCloseFullMapOnMultiplayerSpawn()
    {
        SetFullMapVisible(false, force: true);
        Debug.Log("[MPUI] full map forced closed on spawn");
    }

    private void SetFullMapVisible(bool visible, bool force = false)
    {
        if (!force && isFullMapVisible == visible && (fullMapOverlay == null || fullMapOverlay.activeSelf == visible))
            return;

        isFullMapVisible = visible;
        if (fullMapOverlay != null)
            fullMapOverlay.SetActive(isFullMapVisible);

        // Hide the bottom-right minimap panel while the full map is open and
        // restore it on release. Previously only the player-arrow was toggled,
        // so the minimap circle stayed painted under the full map overlay.
        if (minimapRootRect != null)
            minimapRootRect.gameObject.SetActive(!isFullMapVisible);

        // Tab in MP must only toggle the full map — make sure the CapsLock
        // leaderboard isn't accidentally visible at the same time (users
        // reported "leaderboard + full map overlay" together).
        if (isFullMapVisible && scoreboardOverlay != null && scoreboardOverlay.activeSelf)
        {
            scoreboardOverlay.SetActive(false);
            isScoreboardVisible = false;
        }

        EnsureMinimapReferences();
        if (_cachedMinimap != null)
            _cachedMinimap.SetFullMapMode(isFullMapVisible, playerController != null ? playerController.transform : null);
    }

    private void ToggleScoreboard()
    {
        isScoreboardVisible = !isScoreboardVisible;
        if (scoreboardOverlay != null)
            scoreboardOverlay.SetActive(isScoreboardVisible);

        RefreshScoreboard(force: true);
    }

    private void RefreshScoreboard(bool force = false)
    {
        // Always update data even when hidden — kill counts must be live.
        // Only update visibility when explicitly toggled (force = true from ToggleScoreboard).
        if (force && scoreboardOverlay != null)
            scoreboardOverlay.SetActive(isScoreboardVisible);

        if (scoreboardRows == null || scoreboardRows.Length == 0) return;

        MatchStatsManager stats = MatchStatsManager.Instance;
        if (stats == null) return;

        bool showBots = !MultiplayerMode.IsMultiplayer || MultiplayerRuntimeConfig.IsAiEnabled();

        // Header row stays as a fixed column guide.
        if (scoreboardSummaryText != null)
        {
            int registeredCount = !showBots
                ? stats.GetRegisteredPlayerCount()
                : stats.GetRegisteredCombatantCount();
            scoreboardSummaryText.text = !showBots
                ? $"MULTIPLAYER LEADERBOARD  ({registeredCount} PLAYERS)"
                : $"LIVE MATCH LEADERBOARD  ({registeredCount} COMBATANTS)";
        }

        var entries = !showBots
            ? stats.GetTopPlayers(scoreboardRows.Length)
            : stats.GetTopCombatants(scoreboardRows.Length);
        for (int i = 0; i < scoreboardRows.Length; i++)
        {
            ScoreboardRowUi row = scoreboardRows[i];
            if (row == null || row.Root == null) continue;

            if (i >= entries.Count)
            {
                row.Root.SetActive(false);
                ApplyScoreboardRowStyle(row, false, false, true);
                continue;
            }

            row.Root.SetActive(true);
            MatchStatsManager.CombatantSnapshot entry = entries[i];
            row.NameText.text = $"{i + 1}. {entry.DisplayName}";
            row.KillsText.text = entry.Kills.ToString();
            row.StatusText.text = entry.IsAlive ? "ALIVE" : "DEAD";
            ApplyScoreboardRowStyle(row, entry.IsPlayer, entry.IsAlive, false);
        }
    }

    private void CreateScoreboardHeader(Transform panelTransform)
    {
        GameObject headerRow = new GameObject("ScoreboardHeaderRow");
        headerRow.transform.SetParent(panelTransform, false);
        RectTransform headerRect = headerRow.AddComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0.5f, 1f);
        headerRect.anchorMax = new Vector2(0.5f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = new Vector2(0f, -128f);
        headerRect.sizeDelta = new Vector2(480f, 30f);

        HorizontalLayoutGroup headerLayout = headerRow.AddComponent<HorizontalLayoutGroup>();
        headerLayout.childAlignment = TextAnchor.MiddleLeft;
        headerLayout.childControlHeight = true;
        headerLayout.childControlWidth = true;
        headerLayout.childForceExpandHeight = true;
        headerLayout.childForceExpandWidth = false;
        headerLayout.spacing = 12f;

        CreateScoreboardColumn(headerRow.transform, "HeaderName", "NAME", 256f, TextAlignmentOptions.Left, true);
        CreateScoreboardColumn(headerRow.transform, "HeaderKills", "KILLS", 84f, TextAlignmentOptions.Center, true);
        CreateScoreboardColumn(headerRow.transform, "HeaderStatus", "STATUS", 104f, TextAlignmentOptions.Center, true);
    }

    private void CreateScoreboardRows(Transform panelTransform)
    {
        GameObject rowsRoot = new GameObject("ScoreboardRows");
        rowsRoot.transform.SetParent(panelTransform, false);
        RectTransform rowsRect = rowsRoot.AddComponent<RectTransform>();
        rowsRect.anchorMin = new Vector2(0.5f, 1f);
        rowsRect.anchorMax = new Vector2(0.5f, 1f);
        rowsRect.pivot = new Vector2(0.5f, 1f);
        rowsRect.anchoredPosition = new Vector2(0f, -168f);
        rowsRect.sizeDelta = new Vector2(480f, 320f);

        // English comment: VerticalLayoutGroup keeps each leaderboard row aligned automatically.
        VerticalLayoutGroup layout = rowsRoot.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 6f;
        layout.padding = new RectOffset(0, 0, 0, 0);

        scoreboardRows = new ScoreboardRowUi[8];
        for (int i = 0; i < scoreboardRows.Length; i++)
        {
            GameObject rowObject = new GameObject($"ScoreboardRow_{i + 1}");
            rowObject.transform.SetParent(rowsRoot.transform, false);

            RectTransform rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(480f, 42f);

            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 42f;

            Image rowBg = rowObject.AddComponent<Image>();
            rowBg.color = i % 2 == 0 ? new Color(1f, 1f, 1f, 0.035f) : new Color(1f, 1f, 1f, 0.015f);

            HorizontalLayoutGroup rowHorizontal = rowObject.AddComponent<HorizontalLayoutGroup>();
            rowHorizontal.childAlignment = TextAnchor.MiddleLeft;
            rowHorizontal.childControlHeight = true;
            rowHorizontal.childControlWidth = true;
            rowHorizontal.childForceExpandHeight = true;
            rowHorizontal.childForceExpandWidth = false;
            rowHorizontal.spacing = 12f;
            rowHorizontal.padding = new RectOffset(12, 12, 0, 0);

            scoreboardRows[i] = new ScoreboardRowUi
            {
                Root = rowObject,
                NameText = CreateScoreboardColumn(rowObject.transform, $"RowName_{i + 1}", $"{i + 1}. ---", 256f, TextAlignmentOptions.Left, false),
                KillsText = CreateScoreboardColumn(rowObject.transform, $"RowKills_{i + 1}", "0", 84f, TextAlignmentOptions.Center, false),
                StatusText = CreateScoreboardColumn(rowObject.transform, $"RowStatus_{i + 1}", "---", 104f, TextAlignmentOptions.Center, false)
            };
        }
    }

    private void CacheScoreboardRows()
    {
        Transform rowsRoot = scoreboardOverlay != null
            ? scoreboardOverlay.transform.Find("ScoreboardPanel/ScoreboardRows")
            : null;
        if (rowsRoot == null)
            return;

        scoreboardRows = new ScoreboardRowUi[8];
        for (int i = 0; i < scoreboardRows.Length; i++)
        {
            Transform row = rowsRoot.Find($"ScoreboardRow_{i + 1}");
            if (row == null)
                continue;

            scoreboardRows[i] = new ScoreboardRowUi
            {
                Root = row.gameObject,
                NameText = row.Find($"RowName_{i + 1}")?.GetComponent<TextMeshProUGUI>(),
                KillsText = row.Find($"RowKills_{i + 1}")?.GetComponent<TextMeshProUGUI>(),
                StatusText = row.Find($"RowStatus_{i + 1}")?.GetComponent<TextMeshProUGUI>()
            };
        }
    }

    private TextMeshProUGUI CreateScoreboardColumn(Transform parent, string name, string text, float width, TextAlignmentOptions alignment, bool isHeader)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);

        LayoutElement layout = obj.AddComponent<LayoutElement>();
        layout.preferredWidth = width;
        layout.minWidth = width;

        TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = isHeader ? 17f : 22f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = alignment;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.color = isHeader ? new Color(0.50f, 0.75f, 1f, 0.80f) : Color.white;
        if (prismFont != null)
            tmp.font = prismFont;
        return tmp;
    }

    private static void ApplyScoreboardRowStyle(ScoreboardRowUi row, bool isPlayer, bool isAlive, bool isEmpty)
    {
        Color nameColor = isEmpty
            ? new Color(0.45f, 0.55f, 0.68f, 0.55f)
            : isPlayer
                ? new Color(1f, 0.84f, 0.34f, 1f)
                : new Color(0.92f, 0.97f, 1f, isAlive ? 1f : 0.60f);

        Color valueColor = isEmpty
            ? new Color(0.45f, 0.55f, 0.68f, 0.55f)
            : new Color(0.92f, 0.97f, 1f, isAlive ? 0.95f : 0.60f);

        Color statusColor = isEmpty
            ? new Color(0.45f, 0.55f, 0.68f, 0.55f)
            : isAlive
                ? new Color(0.54f, 0.95f, 0.64f, 0.95f)
                : new Color(1f, 0.32f, 0.32f, 0.92f);

        if (row.NameText != null) row.NameText.color = nameColor;
        if (row.KillsText != null) row.KillsText.color = valueColor;
        if (row.StatusText != null) row.StatusText.color = statusColor;
    }

    private void ShowMatchFinishedOverlay()
    {
        if (MultiplayerMode.IsMultiplayer)
            return;

        matchFinished = true;
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        GameObject canvasObject = GameObject.Find("HUDCanvas");
        if (canvasObject == null)
        {
            return;
        }

        if (matchFinishedOverlay != null)
        {
            Destroy(matchFinishedOverlay);
        }

        // Guarantee the canvas can receive pointer events
        if (canvasObject.GetComponent<GraphicRaycaster>() == null)
            canvasObject.AddComponent<GraphicRaycaster>();

        matchFinishedOverlay = new GameObject("MatchFinishedOverlay");
        matchFinishedOverlay.transform.SetParent(canvasObject.transform, false);

        Image overlay = matchFinishedOverlay.AddComponent<Image>();
        overlay.color = new Color(0.02f, 0.03f, 0.06f, 0.76f);
        Stretch(matchFinishedOverlay.GetComponent<RectTransform>());

        GameObject panel = CreateImage(matchFinishedOverlay.transform, "FinishedPanel",
            new Color(0.13f, 0.17f, 0.28f, 0.90f),
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 360f));
        panel.AddComponent<Outline>().effectColor = new Color(0.26f, 0.42f, 0.68f, 0.22f);

        TextMeshProUGUI title = CreateText(panel.transform, "FinishedTitle",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 74f), new Vector2(560f, 88f),
            44f, FontStyles.Bold, TextAlignmentOptions.Center);
        title.text = "MATCH FINISHED";

        TextMeshProUGUI subtitle = CreateText(panel.transform, "FinishedSubtitle",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(620f, 64f),
            24f, FontStyles.Normal, TextAlignmentOptions.Center);
        subtitle.text = "The timer reached zero. Choose what you want to do next.";
        subtitle.color = new Color(0.78f, 0.88f, 1f, 0.92f);
        subtitle.textWrappingMode = TextWrappingModes.Normal;

        GameObject restartBtnObj = CreateActionButton(panel.transform, "RESTART", new Vector2(-116f, -66f), () =>
        {
            Time.timeScale = 1f;
            GameManager.Instance?.ReplayCurrentLevel();
        });

        CreateActionButton(panel.transform, "MAIN MENU", new Vector2(116f, -66f), () =>
        {
            Time.timeScale = 1f;
            GameManager.Instance?.GoToMainMenu();
        });

        // ── Ensure an EventSystem exists with a NEW-INPUT-SYSTEM-compatible module ──
        //
        // The project uses the Input System Package (new). The legacy
        // StandaloneInputModule reads UnityEngine.Input.* internally, which
        // throws InvalidOperationException under the new Input System and
        // prevents any button click from being dispatched. That is exactly
        // what was killing the RESTART / MAIN MENU buttons on the Match
        // Finished overlay (levels 4/7/9 were reproducible because a match
        // would end without the player ever opening the pause menu — so
        // PauseMenuController had never created a correct EventSystem, and
        // this method silently fell back to StandaloneInputModule).
        //
        // Fix:
        //   1. Find or create the EventSystem.
        //   2. Strip any stale StandaloneInputModule that was shipped with
        //      the scene or added by older code.
        //   3. Guarantee exactly one InputSystemUIInputModule is attached.
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject esObj = new GameObject("EventSystem");
            eventSystem = esObj.AddComponent<EventSystem>();
        }

        GameObject eventSystemGO = eventSystem.gameObject;

        // 2. Remove the broken legacy module if present.
        StandaloneInputModule legacyModule = eventSystemGO.GetComponent<StandaloneInputModule>();
        if (legacyModule != null)
            Destroy(legacyModule);

        // 3. Ensure the new-Input-System UI module is attached exactly once.
        InputSystemUIInputModule uiModule = eventSystemGO.GetComponent<InputSystemUIInputModule>();
        if (uiModule == null)
            uiModule = eventSystemGO.AddComponent<InputSystemUIInputModule>();
        uiModule.enabled = true;

        eventSystemGO.SetActive(true);

        // Focus the Restart button so keyboard/gamepad navigation works immediately
        if (restartBtnObj != null)
        {
            Button firstBtn = restartBtnObj.GetComponent<Button>();
            if (firstBtn != null)
                eventSystem.SetSelectedGameObject(firstBtn.gameObject);
        }
    }

    /// <summary>
    /// Public alias for ShowMatchFinishedOverlay so external callers (and the
    /// user's UIManager references) can trigger the end-game screen directly.
    /// Guarantees: timeScale=0, cursor unlocked, EventSystem active.
    /// </summary>
    public void ShowGameFinishedMenu()
    {
        if (MultiplayerMode.IsMultiplayer)
            return;

        ShowMatchFinishedOverlay();
    }

    private GameObject CreateActionButton(Transform parent, string label, Vector2 position, UnityEngine.Events.UnityAction action)
    {
        GameObject buttonObject = CreateImage(parent, "Btn_" + label,
            Color.white,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position, new Vector2(200f, 60f));

        Button button = buttonObject.GetComponent<Button>();
        if (button == null)
            button = buttonObject.AddComponent<Button>();

        // Ensure the Image is set as the button's targetGraphic so it's clickable
        Image btnImage = buttonObject.GetComponent<Image>();
        if (btnImage != null)
        {
            btnImage.raycastTarget = true;
            button.targetGraphic   = btnImage;
        }

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);

        TextMeshProUGUI labelText = CreateText(buttonObject.transform, "Txt_" + label,
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            24f, FontStyles.Bold, TextAlignmentOptions.Center);
        labelText.text  = label;
        labelText.color = new Color(0.10f, 0.10f, 0.14f, 1f);
        labelText.raycastTarget = false;   // let the Image underneath catch the click

        return buttonObject;
    }

    private Sprite GetOrCreateCircleSprite()
    {
        if (minimapCircleTexture == null)
        {
            minimapCircleTexture = new Texture2D(256, 256, TextureFormat.ARGB32, false);
            minimapCircleTexture.wrapMode = TextureWrapMode.Clamp;

            Vector2 center = new Vector2(127.5f, 127.5f);
            float radius = 120f;
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = distance <= radius ? 1f : 0f;
                    minimapCircleTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            minimapCircleTexture.Apply();
        }

        return Sprite.Create(minimapCircleTexture, new Rect(0f, 0f, 256f, 256f), new Vector2(0.5f, 0.5f));
    }

    private Sprite GetOrCreateTriangleSprite()
    {
        Texture2D triangleTexture = new Texture2D(64, 64, TextureFormat.ARGB32, false);
        triangleTexture.wrapMode = TextureWrapMode.Clamp;

        Vector2 a = new Vector2(32f, 60f);
        Vector2 b = new Vector2(10f, 10f);
        Vector2 c = new Vector2(54f, 10f);

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Vector2 p = new Vector2(x, y);
                bool inside = SameSide(p, a, b, c) && SameSide(p, b, a, c) && SameSide(p, c, a, b);
                triangleTexture.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }

        triangleTexture.Apply();
        return Sprite.Create(triangleTexture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f));
    }

    private bool SameSide(Vector2 p1, Vector2 p2, Vector2 a, Vector2 b)
    {
        Vector3 cp1 = Vector3.Cross(b - a, p1 - a);
        Vector3 cp2 = Vector3.Cross(b - a, p2 - a);
        return Vector3.Dot(cp1, cp2) >= 0f;
    }

    private GameObject EnsureRectObject(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
    {
        GameObject existing = parent.Find(name)?.gameObject;
        if (existing != null)
        {
            return existing;
        }

        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return obj;
    }

    private void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    /// <summary>
    /// Called by NetworkPlayerSpawner after the local Photon player is confirmed
    /// spawned. Wires HUD to the correct local player, hides any stale overlays,
    /// locks the cursor, and registers the combatant in MatchStatsManager.
    /// </summary>
    public void InitForMultiplayerLocalPlayer(PlayerController pc, PlayerHealth ph)
    {
        if (!MultiplayerMode.IsMultiplayer)
            return;

        prismFont ??= ResolvePrismFont();
        EnsureRuntimeHud();
        ResolveHealthHudWidgets();

        playerController = pc;
        playerHealth = ph;
        localMultiplayerActorNumber = -1;
        localMultiplayerPlayerName = PlayerProfile.HasUsername ? PlayerProfile.Username : "Player";

        if (GameManager.Instance != null)
            BindMatchHudFromGameManager();

        // Ensure scoreboard is hidden during active gameplay.
        if (scoreboardOverlay != null)
        {
            scoreboardOverlay.SetActive(false);
            isScoreboardVisible = false;
        }

        ForceCloseFullMapOnMultiplayerSpawn();
        HideStrayMultiplayerMenuCanvas();

        // Destroy any stale match-finished overlay that may have carried over.
        if (matchFinishedOverlay != null)
        {
            Destroy(matchFinishedOverlay);
            matchFinishedOverlay = null;
            matchFinished = false;
        }

        // Lock cursor so the player can look around immediately.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Register local player in leaderboard with confirmed identity.
        if (MatchStatsManager.Instance != null && ph != null)
        {
            string playerId = MatchStatsManager.BuildCombatantId(ph);
            string playerLabel = PlayerProfile.HasUsername ? PlayerProfile.Username : "YOU";
#if PUN_2_OR_NEWER
            PhotonView view = ph.GetComponent<PhotonView>();
            if (view != null && view.Owner != null)
            {
                localMultiplayerActorNumber = view.Owner.ActorNumber;
                playerId = $"photon:{view.Owner.ActorNumber}";
                playerLabel = string.IsNullOrWhiteSpace(view.Owner.NickName)
                    ? $"Player_{view.Owner.ActorNumber}"
                    : view.Owner.NickName;
            }
#endif
            localMultiplayerPlayerName = playerLabel;
            MatchStatsManager.Instance.RegisterCombatant(playerId, playerLabel, isPlayer: true, transform: ph.transform);
            Debug.Log($"[MPHUD] registered player actor={localMultiplayerActorNumber} name={playerLabel}");
            MatchStatsManager.Instance.StatsChanged -= HandleMatchStatsChanged;
            MatchStatsManager.Instance.StatsChanged += HandleMatchStatsChanged;
        }

        // Bind level + weapon HUD text from MultiplayerRuntimeConfig — the
        // SAME source PlayerController.ForceEquipLevelWeaponForMultiplayer
        // uses. Previously HUD read GameManager.currentLevel (SP Continue
        // save) and the two diverged → "LEVEL 8 / HAMMER" with L1 knife.
        int mpLevel = MultiplayerRuntimeConfig.GetSelectedLevel();
        string mpWeapon = pc != null && !string.IsNullOrWhiteSpace(pc.equippedWeaponName)
            ? pc.equippedWeaponName
            : MultiplayerRuntimeConfig.GetSelectedWeaponName();

        if (levelText != null)
            levelText.text = "LEVEL " + mpLevel;
        if (weaponText != null)
            weaponText.text = mpWeapon.ToUpperInvariant();
        Debug.Log($"[MPHUD] displaying level={mpLevel} weapon={mpWeapon}");

        // Guarantee an HP text label exists. The scene-authored HUDCanvas may
        // be missing HPText (only the green bar) — in that case build a fresh
        // one over the bottom panel so the player always sees "HP 100 / 100".
        EnsureMultiplayerHpText();
        EnsureMultiplayerTopBarTexts();
        ApplyMultiplayerHudMode();
        RefreshMultiplayerLocalHealth(ph);

        int hpCur = ph != null ? Mathf.CeilToInt(ph.currentHealth) : 0;
        int hpMax = ph != null ? Mathf.CeilToInt(ph.maxHealth) : 0;
        Debug.Log($"[MPHUD] bound health to local actor={localMultiplayerActorNumber} hp={hpCur}/{hpMax}");

        // Bind minimap follow target to the local Photon player. Without this
        // the camera stayed in lockToArenaCenter mode (the default) and the
        // minimap never followed.
        EnsureMinimapReferences();
        if (_cachedMinimap != null && pc != null)
        {
            _cachedMinimap.SetFullMapMode(false, pc.transform);
            Debug.Log($"[MPMinimap] following local player actor={localMultiplayerActorNumber}");
        }

        Debug.Log("[MPFlow] local player ready");
        Debug.Log("[MPFlow] gameplay state active");
        Debug.Log("[MPFlow] hiding match stats");
        Debug.Log("[MPFlow] input enabled");

        // NOTE: the loading overlay is no longer destroyed here. It now
        // auto-destroys after a fixed 5-second timer (LoadingScreenUI
        // .ShowTimedForMultiplayer) so a slow HUD-bind / Photon callback
        // can't strand the overlay for minutes.
    }

    /// <summary>
    /// Guarantees a working HP text label in MP. If the scene's HUDCanvas
    /// shipped without one (or the serialized ref was lost), we synthesize
    /// one over the bottom-left health panel so the user is never stuck with
    /// just a green bar and no numeric readout.
    /// </summary>
    private void TryAutoBindLocalMultiplayerPlayer()
    {
#if PUN_2_OR_NEWER
        PhotonView[] views = FindObjectsByType<PhotonView>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < views.Length; i++)
        {
            PhotonView v = views[i];
            if (v == null || !v.IsMine) continue;
            PlayerController pc = v.GetComponent<PlayerController>();
            PlayerHealth ph = v.GetComponent<PlayerHealth>();
            if (pc == null || ph == null) continue;
            InitForMultiplayerLocalPlayer(pc, ph);
            return;
        }
#endif
    }

    /// <summary>
    /// Fixes MP HUD regression where SCORE was clipped at top-left and ENEMIES
    /// disappeared from top-right. Creates the two TMP labels at known-good
    /// anchored positions if missing, restores text + colour if present.
    /// Does not change LEVEL / WEAPON / HP / TIMER paths.
    /// </summary>
    private void EnsureMultiplayerTopBarTexts()
    {
        GameObject canvasObject = GameObject.Find("HUDCanvas");
        if (canvasObject == null) return;
        Transform canvas = canvasObject.transform;
        Transform topBar = canvas.Find("TopBar");

        // ── SCORE (top-left, parented under the canvas, anchor 0,1) ─────────
        if (scoreText == null)
        {
            scoreText = CreateText(canvas, "ScoreText",
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(300f, -42f), new Vector2(360f, 48f),
                30f, FontStyles.Bold, TextAlignmentOptions.Left);
        }
        scoreText.gameObject.SetActive(true);
        scoreText.enabled = true;
        scoreText.color = new Color(0.97f, 0.98f, 1f, 1f);
        if (string.IsNullOrEmpty(scoreText.text)) scoreText.text = "SCORE  0";
        EnsureScoreLabelPresentation(canvas);
        // Push to front so the top-bar tint never paints over it.
        scoreText.transform.SetAsLastSibling();
        Debug.Log("[MPHUD] score text restored");

        // ── Top-right counter: PLAYERS in PurePvP, ENEMIES in AI modes ──────
        Transform enemyParent = topBar != null ? topBar : canvas;
        if (enemyCountText == null)
        {
            enemyCountText = CreateText(enemyParent, "EnemyText",
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-215f, 14f), new Vector2(380f, 40f),
                28f, FontStyles.Bold, TextAlignmentOptions.Right);
        }

        enemyCountText.enabled = true;
        enemyCountText.color = new Color(0.97f, 0.98f, 1f, 1f);
        enemyCountText.transform.SetAsLastSibling();

        if (MultiplayerRuntimeConfig.IsPurePvP())
        {
            Debug.Log("[MPHUD] PurePvP hiding enemies counter");
#if PUN_2_OR_NEWER
            if (PhotonNetwork.InRoom)
            {
                int playerCount = PhotonNetwork.PlayerList.Length;
                int maxPlayers = MpRoomConfig.ReadMaxPlayers();
                enemyCountText.text = $"PLAYERS  {playerCount}/{maxPlayers}";
            }
            else
#endif
                enemyCountText.text = "PLAYERS  1";
            enemyCountText.gameObject.SetActive(true);
            Debug.Log("[MPHUD] players counter restored");
        }
        else
        {
            MpGameMode mode = MultiplayerRuntimeConfig.GetSelectedGameMode();
            Debug.Log($"[MPHUD] enemies counter visible for mode = {mode}");
            if (string.IsNullOrEmpty(enemyCountText.text))
                enemyCountText.text = "ENEMIES  0";
            enemyCountText.gameObject.SetActive(true);
            Debug.Log("[MPHUD] enemies text restored");
        }
    }

    private void EnsureMultiplayerHpText()
    {
        if (healthText != null)
        {
            healthText.gameObject.SetActive(true);
            healthText.enabled = true;
            if (healthText.color.a < 0.05f) healthText.color = Color.white;
            if (prismFont != null && healthText.font == null) healthText.font = prismFont;
            return;
        }

        GameObject canvasObject = GameObject.Find("HUDCanvas");
        if (canvasObject == null) return;

        Transform parentTransform = canvasObject.transform.Find("BottomHealthPanel");
        if (parentTransform == null) parentTransform = canvasObject.transform;

        healthText = CreateText(parentTransform, "HPText",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(238f, -28f), new Vector2(420f, 30f),
            24f, FontStyles.Bold, TextAlignmentOptions.Left);
        healthText.color = Color.white;
        healthText.text = "HP 100 / 100";
        healthText.gameObject.SetActive(true);
        healthText.enabled = true;
    }

    private void RefreshMultiplayerLocalHealth(PlayerHealth ph)
    {
        if (ph == null)
            return;

        bool hpTextAssigned = healthText != null;
        Debug.Log($"[MPHUD] hp text assigned={hpTextAssigned}");

        UpdateHealth(ph.currentHealth, ph.maxHealth, ph);
        Debug.Log($"[MPHUD] local HP refresh current={ph.currentHealth} max={ph.maxHealth}");

        if (healthText != null)
        {
            healthText.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
        }
    }

    private void HideStrayMultiplayerMenuCanvas()
    {
        GameObject menuCanvas = GameObject.Find("NeonCanvas");
        if (menuCanvas == null || !menuCanvas.activeSelf)
            return;

        menuCanvas.SetActive(false);
        Debug.Log("[MPUI] multiplayer menu hidden");
    }

    private TMP_FontAsset ResolvePrismFont()
    {
        // ── Priority 1: "aizona fx" font (drag it into a Resources/Fonts folder) ──
        // Try common Resource paths for the custom font asset.
        string[] aizonaPaths = {
            "Fonts/aizona fx SDF",
            "Fonts/aizona fx",
            "Fonts & Materials/aizona fx SDF",
            "Fonts & Materials/aizona fx",
        };
        foreach (string path in aizonaPaths)
        {
            TMP_FontAsset aizona = Resources.Load<TMP_FontAsset>(path);
            if (aizona != null) return aizona;
        }

        // ── Priority 2: any loaded font whose name contains "aizona" ──────────
        TMP_FontAsset[] allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        foreach (TMP_FontAsset font in allFonts)
        {
            if (font == null) continue;
            string lower = font.name.ToLowerInvariant();
            if (lower.Contains("aizona") || lower.Contains("arizona") || lower.Contains("azonix"))
                return font;
        }

        // ── Priority 3: TMP default ──────────────────────────────────────────
        if (TMP_Settings.defaultFontAsset != null)
            return TMP_Settings.defaultFontAsset;

        // ── Priority 4: LiberationSans bundled with TMP ──────────────────────
        return Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
    }
}
