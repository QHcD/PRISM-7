using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if PUN_2_OR_NEWER
using Photon.Pun;
#endif

/// <summary>
/// STRICT SINGLETON — "Destroyer" Pattern.
/// In Awake(), if an instance already exists, this immediately calls
/// DestroyImmediate(gameObject) and returns, guaranteeing only ONE
/// GameManager exists globally across all 16 levels.
///
/// This eliminates the hierarchy flood of duplicate GameManagers that
/// was causing extreme lag, frozen AI, and broken physics.
/// </summary>
public class GameManager : MonoBehaviour
{
    public enum MenuScreen
    {
        MainMenu,
        LevelComplete,
        GameOver,
        Victory
    }

    public enum ArenaEnvironment
    {
        Industrial,
        SciFi
    }

    public enum ArenaMap
    {
        Map1,
        Map2
    }

    public enum PerspectiveMode
    {
        FirstPerson,
        ThirdPerson
    }

    public enum MovementScheme
    {
        Wasd,
        ArrowKeys
    }

    public enum ControlStyleState
    {
        WasdMouse,
        ArrowsMouse
    }

    public static GameManager Instance { get; private set; }
    public static MenuScreen PendingMenuScreen { get; private set; } = MenuScreen.MainMenu;

    public static void SetPendingMenuScreen(MenuScreen screen)
    {
        PendingMenuScreen = screen;
    }

    public const int TotalLevels = 16;

    public enum WeaponType
    {
        Melee,
        TwoHandedMelee,   // Two-handed swords, spears, axes — enables left-hand IK grip
        UltimateMelee
    }

    private static readonly string[] LevelWeaponNames = {
        "Tactical Knife", "Razor Katana", "Shovel", "Baseball Bat", "Nunchucks",
        "Wrench", "Crowbar", "Hammer", "Axe", "Spear",
        "Nailed Plank", "Saw", "Sickle", "Morgenstern", "L3FTE",
        "Riot Shield"
    };

    private static readonly float[] LevelWeaponDamage = {
        25f,  30f,  34f,  37f,  40f,
        43f,  46f,  50f,  54f,  56f,
        58f,  62f,  65f,  70f,  75f,
        85f
    };

    private static readonly float[] LevelWeaponRange = {
        2.0f, 2.5f, 2.4f, 2.8f, 2.2f,
        2.3f, 2.5f, 2.6f, 2.5f, 3.2f,
        2.0f, 2.3f, 2.4f, 2.5f, 2.5f,
        2.8f
    };

    private static readonly float[] LevelWeaponExplosionRadius = {
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0f
    };

    private static readonly WeaponType[] LevelWeaponTypes = {
        //          Knife              Katana                  Shovel                  Baseball Bat            Nunchucks
        WeaponType.Melee,  WeaponType.TwoHandedMelee, WeaponType.TwoHandedMelee, WeaponType.TwoHandedMelee, WeaponType.Melee,
        //          Wrench             Crowbar                 Hammer                  Axe                     Spear
        WeaponType.Melee,  WeaponType.Melee, WeaponType.TwoHandedMelee, WeaponType.TwoHandedMelee, WeaponType.TwoHandedMelee,
        //          Nailed Plank           Saw                Sickle             Morgenstern              L3FTE
        WeaponType.TwoHandedMelee, WeaponType.Melee, WeaponType.Melee, WeaponType.TwoHandedMelee, WeaponType.Melee,
        //          Riot Shield
        WeaponType.TwoHandedMelee
    };

    private static readonly Color[] LevelWeaponColors = {
        new Color(0.80f, 0.85f, 0.90f), new Color(0.85f, 0.30f, 0.30f), new Color(0.85f, 0.70f, 0.45f), new Color(0.85f, 0.60f, 0.35f), new Color(0.70f, 0.90f, 1.00f),
        new Color(0.70f, 0.80f, 0.90f), new Color(0.60f, 0.65f, 0.75f), new Color(0.55f, 0.62f, 0.72f), new Color(0.85f, 0.45f, 0.30f), new Color(0.80f, 0.72f, 0.50f),
        new Color(0.75f, 0.65f, 0.45f), new Color(0.75f, 0.75f, 0.80f), new Color(0.70f, 0.72f, 0.80f), new Color(0.65f, 0.55f, 0.40f), new Color(0.40f, 0.40f, 0.45f),
        new Color(0.30f, 0.45f, 0.60f)
    };

    // ── Runtime state ────────────────────────────────────────────────────────
    public int   currentLevel        = 1;
    public int   score               = 0;
    public int   killCount           = 0;
    public int   enemiesRemaining    = 0;
    public string difficulty         = "Normal";
    public bool  playerTookDamage    = false;
    public float levelTime           = 0f;
    public ArenaMap        selectedMap      = ArenaMap.Map1;
    public ArenaEnvironment selectedEnvironment = ArenaEnvironment.Industrial;
    public PerspectiveMode perspectiveMode  = PerspectiveMode.ThirdPerson;
    public MovementScheme  movementScheme   = MovementScheme.Wasd;

    public int totalEnemiesSpawned   = 0;
    public int enemiesKilledThisLevel = 0;

    /// <summary>Fired whenever <see cref="enemiesRemaining"/> is updated (spawn init or kill).</summary>
    public event Action<int> OnEnemiesRemainingChanged;

    // ── Custom Match (set by the Custom Match menu) ──────────────────────────
    // When IsCustomMatch is true, GetEnemyCount/LevelTimeLimitSeconds and the
    // active difficulty switch over to these per-session overrides instead of
    // the persisted PlayerPrefs values.
    public bool   isCustomMatch          = false;
    public int    customEnemyCount       = 12;
    public int    customMatchTimeSeconds = 300;
    public string customDifficulty       = "Normal";

    public const int DefaultEnemyCount      = 25;
    public const int CustomEnemyMin         = 1;
    public const int CustomEnemyMax         = 25;
    public const int CustomMatchTimeMin     = 60;   // 1 minute
    public const int CustomMatchTimeMax     = 1800; // 30 minutes (defensive cap)

    [SerializeField] private float victoryDelaySeconds = 2.5f;
    [SerializeField] private float loadingScreenMinSeconds = 5.5f;
    [Tooltip("If multiple cameras keep the MainCamera tag, extras are disabled so only one renders (prefers CameraController).")]
    [SerializeField] private bool enforceSingleMainCameraTag = true;

    private bool _levelCompleteTriggered = false;
    private Coroutine _sceneLoadRoutine;
    private Coroutine _retryFinalizeRoutine;
    private bool _retryLoadPending;

    private static string _cachedRetrySceneName = "GameScene";
    private static int _cachedRetrySceneBuildIndex = -1;
    private static int _cachedRetryLevel = 1;

    private AudioSource _matchVoiceAudio;
    private AudioSource _matchUiAudio;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticsForPlayMode()
    {
        Instance = null;
        PendingMenuScreen = MenuScreen.MainMenu;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void VerifyStartupScene()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        Debug.Log($"[Startup] Active scene on boot: {sceneName}");

#if !UNITY_EDITOR
        if (sceneName == "GameScene")
        {
            Debug.Log("[Startup] Force loading MainMenu scene on standalone build boot.");
            SceneManager.LoadScene("MainMenu");
        }
#endif
    }

    // ════════════════════════════════════════════════════════════════════════
    //  STRICT SINGLETON — "Destroyer" Pattern
    //  CRITICAL: Any duplicate GameManager (prefab leftover, scene copy, or
    //  AddComponent call) is IMMEDIATELY destroyed. This is the one fix that
    //  eliminates the hierarchy flood, the lag, and the frozen AI.
    // ════════════════════════════════════════════════════════════════════════
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Scenes may still contain an extra GameManager. We destroy duplicates to keep gameplay stable.
            // Avoid spamming warnings; the proper fix is removing the extra scene instance.
            DestroyImmediate(gameObject);
            return;
        }

        Instance = this;

        if (Application.isPlaying)
        {
            GameManager[] all = UnityEngine.Object.FindObjectsByType<GameManager>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i] != this)
                {
                    DestroyImmediate(all[i].gameObject);
                }
            }

            DontDestroyOnLoad(gameObject);

            SetupProgrammaticMatchAudio();
            if (GetComponent<MatchCommentator>() == null)
                gameObject.AddComponent<MatchCommentator>();
        }

        LoadPersistedSettings();
    }

    private void Start()
    {
        if (!Application.isPlaying || !enforceSingleMainCameraTag)
            return;
        EnforceSingleMainCameraTag();
    }

    /// <summary>
    /// Keeps a single enabled <c>MainCamera</c> when duplicates exist (common after merges / prefab copies).
    /// Does not remove UI or minimap cameras that use other tags.
    /// </summary>
    private void EnforceSingleMainCameraTag()
    {
        Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        Camera preferred = null;
        if (CameraController.Instance != null)
            preferred = CameraController.Instance.GetComponent<Camera>();

        var mains = new List<Camera>(4);
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera c = cameras[i];
            if (c == null || !c.isActiveAndEnabled) continue;
            if (!c.CompareTag("MainCamera")) continue;
            mains.Add(c);
        }

        if (mains.Count <= 1)
            return;

        Camera keep = preferred != null && mains.Contains(preferred) ? preferred : mains[0];
        for (int i = 0; i < mains.Count; i++)
        {
            if (mains[i] != null && mains[i] != keep)
                mains[i].enabled = false;
        }
    }

    /// <summary>Child <c>__MatchVoice</c> — commentary / VO <see cref="PlayMatchVoiceOneShot"/>.</summary>
    public AudioSource MatchVoiceAudio => _matchVoiceAudio;

    /// <summary>Child <c>__MatchUiSfx</c> — countdown beeps, UI stingers.</summary>
    public AudioSource MatchUiAudio => _matchUiAudio;

    private void SetupProgrammaticMatchAudio()
    {
        _matchVoiceAudio = EnsureChildAudioSource("__MatchVoice", SessionManager.Instance != null
            ? SessionManager.Instance.VoiceOverMixerGroupName
            : "VO");
        _matchUiAudio = EnsureChildAudioSource("__MatchUiSfx", SessionManager.Instance != null
            ? SessionManager.Instance.MatchUiMixerGroupName
            : "UI");
    }

    private AudioSource EnsureChildAudioSource(string childName, string mixerGroupName)
    {
        Transform existing = transform.Find(childName);
        if (existing == null)
        {
            GameObject go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            existing = go.transform;
        }

        AudioSource src = existing.GetComponent<AudioSource>();
        if (src == null)
            src = existing.gameObject.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;
        src.loop = false;

        if (SessionManager.Instance != null && !string.IsNullOrEmpty(mixerGroupName))
            SessionManager.Instance.ConfigureMatchAudioSource(src, mixerGroupName);

        return src;
    }

    /// <summary>VO / commentator lines (overlapping friendly).</summary>
    public void PlayMatchVoiceOneShot(AudioClip clip, float volumeScale = 1f)
    {
        if (!Application.isPlaying || clip == null) return;
        if (_matchVoiceAudio == null)
            SetupProgrammaticMatchAudio();
        _matchVoiceAudio?.PlayOneShot(clip, Mathf.Clamp01(volumeScale));
    }

    /// <summary>Countdown beeps / GO chime and other non-positional UI audio.</summary>
    public void PlayMatchUiOneShot(AudioClip clip, float volumeScale = 1f)
    {
        if (!Application.isPlaying || clip == null) return;
        if (_matchUiAudio == null)
            SetupProgrammaticMatchAudio();
        _matchUiAudio?.PlayOneShot(clip, Mathf.Clamp01(volumeScale));
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void LoadPersistedSettings()
    {
        difficulty      = PlayerPrefs.GetString("Difficulty", difficulty);
        selectedMap     = (ArenaMap)Mathf.Clamp(PlayerPrefs.GetInt("SelectedMap", (int)selectedMap), 0, 1);
        selectedEnvironment = (ArenaEnvironment)Mathf.Clamp(PlayerPrefs.GetInt("SelectedEnvironment", (int)selectedEnvironment), 0, 1);
        // Third-person only: if older saves stored FirstPerson, ignore and overwrite.
        perspectiveMode = PerspectiveMode.ThirdPerson;
        int persistedPerspective = Mathf.Clamp(PlayerPrefs.GetInt("PerspectiveMode", (int)PerspectiveMode.ThirdPerson), 0, 1);
        if (persistedPerspective != (int)PerspectiveMode.ThirdPerson)
        {
            PlayerPrefs.SetInt("PerspectiveMode", (int)PerspectiveMode.ThirdPerson);
            PlayerPrefs.Save();
        }
        movementScheme  = ToMovementScheme(LoadControlStyleState());
        currentLevel    = Mathf.Clamp(PlayerPrefs.GetInt("ContinueLevel", currentLevel), 1, TotalLevels);
    }

    private void OnEnable()
    {
        if (Application.isPlaying)
            SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "GameScene" || scene.name == MultiplayerMode.MultiplayerSceneName)
            CacheActiveGameplaySession(scene);

        if (_retryLoadPending && IsSinglePlayerGameplayScene(scene.name))
        {
            _retryLoadPending = false;
            if (_retryFinalizeRoutine != null)
                StopCoroutine(_retryFinalizeRoutine);
            _retryFinalizeRoutine = StartCoroutine(FinalizeRetryReloadNextFrames());
        }

        if (!scene.name.Equals("GameScene"))
            return;

        StartCoroutine(RefreshEnemyCountNextFrame());

        if (currentLevel == 6)
            StartCoroutine(RefreshLevel6Agents());
    }

    public static void CacheActiveGameplaySession(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        _cachedRetrySceneName = scene.name;
        _cachedRetrySceneBuildIndex = scene.buildIndex;
        if (Instance != null)
            _cachedRetryLevel = Instance.currentLevel;

        Debug.Log($"[Retry] current scene = {_cachedRetrySceneName} index = {_cachedRetrySceneBuildIndex} level = {_cachedRetryLevel}");
    }

    private void CacheActiveGameplaySession()
    {
        CacheActiveGameplaySession(SceneManager.GetActiveScene());
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        Scene active = SceneManager.GetActiveScene();
        if (!active.name.Equals("GameScene")) return;
        if (_levelCompleteTriggered) return;

        float timeLimit = LevelTimeLimitSeconds;
        levelTime += Time.deltaTime;
        if (levelTime >= timeLimit)
        {
            levelTime = timeLimit;
            _levelCompleteTriggered = true;
            ResolveTimedMatchWinner();
        }
    }

    private IEnumerator RefreshEnemyCountNextFrame()
    {
        yield return null;

        EnemyController[] enemies = UnityEngine.Object.FindObjectsByType<EnemyController>(FindObjectsSortMode.None);
        if (enemies != null && enemies.Length > 0 && totalEnemiesSpawned <= 0)
            InitializeEnemyCount(enemies.Length);
    }

    private IEnumerator RefreshLevel6Agents()
    {
        yield return null;
        yield return null;

        EnemyController[] enemies = UnityEngine.Object.FindObjectsByType<EnemyController>(FindObjectsSortMode.None);
        if (enemies == null) yield break;

        for (int i = 0; i < enemies.Length; i++)
        {
            if (enemies[i] == null) continue;
            UnityEngine.AI.NavMeshAgent agent = enemies[i].GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent == null) continue;

            agent.enabled = false;
            agent.enabled = true;

            // Do not warp enemies during normal play. EnemyController only
            // warps after a real out-of-map fall below Y = -5.
        }

        Debug.Log($"[GameManager] Level 6 agent refresh: rebound {enemies.Length} NavMeshAgent(s).");
    }

    // ── Settings setters ─────────────────────────────────────────────────────
    public void SetDifficulty(string diff)
    {
        string normalized = NormalizeDifficulty(diff);
        difficulty = normalized;
        PlayerPrefs.SetString("Difficulty", normalized);
        PlayerPrefs.Save();
    }

    /// <summary>Canonicalises a difficulty string. Accepts Easy/Normal/Hard/Veteran.</summary>
    private static string NormalizeDifficulty(string diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) return "Normal";
        string trimmed = diff.Trim();
        if (trimmed.Equals("easy",    System.StringComparison.OrdinalIgnoreCase)) return "Easy";
        if (trimmed.Equals("hard",    System.StringComparison.OrdinalIgnoreCase)) return "Hard";
        if (trimmed.Equals("veteran", System.StringComparison.OrdinalIgnoreCase)) return "Veteran";
        return "Normal";
    }

    public void SetSelectedMap(ArenaMap map)
    {
        selectedMap = map;
        PlayerPrefs.SetInt("SelectedMap", (int)map);
        PlayerPrefs.Save();
    }

    public ArenaEnvironment GetSelectedEnvironment() => selectedEnvironment;

    public void SetSelectedEnvironment(ArenaEnvironment env)
    {
        selectedEnvironment = env;
        PlayerPrefs.SetInt("SelectedEnvironment", (int)env);
        PlayerPrefs.Save();
    }

    public void SetPerspectiveMode(PerspectiveMode mode)
    {
        // Third-person only.
        perspectiveMode = PerspectiveMode.ThirdPerson;
        PlayerPrefs.SetInt("PerspectiveMode", (int)PerspectiveMode.ThirdPerson);
        PlayerPrefs.Save();
    }

    public PerspectiveMode GetPerspectiveMode() => perspectiveMode;

    public void SetMovementScheme(MovementScheme scheme)
    {
        SetControlStyleState(ToControlStyleState(scheme));
    }

    public MovementScheme GetMovementScheme() => movementScheme;
    public ControlStyleState GetControlStyleState() => ToControlStyleState(movementScheme);

    public void SetControlStyleState(ControlStyleState state)
    {
        movementScheme = ToMovementScheme(state);
        PlayerPrefs.SetInt("MovementScheme", (int)movementScheme);
        PlayerPrefs.Save();
        RefreshPlayerControlStyleState();
    }

    public static ControlStyleState LoadControlStyleState()
    {
        int raw = Mathf.Clamp(PlayerPrefs.GetInt("MovementScheme", 0), 0, 1);
        return raw == (int)MovementScheme.ArrowKeys ? ControlStyleState.ArrowsMouse : ControlStyleState.WasdMouse;
    }

    public static MovementScheme ToMovementScheme(ControlStyleState state)
    {
        switch (state)
        {
            case ControlStyleState.ArrowsMouse:
                return MovementScheme.ArrowKeys;
            case ControlStyleState.WasdMouse:
            default:
                return MovementScheme.Wasd;
        }
    }

    public static ControlStyleState ToControlStyleState(MovementScheme scheme)
    {
        switch (scheme)
        {
            case MovementScheme.ArrowKeys:
                return ControlStyleState.ArrowsMouse;
            case MovementScheme.Wasd:
            default:
                return ControlStyleState.WasdMouse;
        }
    }

    private static void RefreshPlayerControlStyleState()
    {
        PlayerController[] players = UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
            if (players[i] != null)
                players[i].RefreshGameplayPreferences();
    }

    public ArenaMap GetSelectedMap() => selectedMap;

    public int GetContinueLevel()
        => Mathf.Clamp(PlayerPrefs.GetInt("ContinueLevel", currentLevel), 1, TotalLevels);

    public bool IsNewPlayer()
    {
        bool hasContinueLevel = PlayerPrefs.HasKey("ContinueLevel") && PlayerPrefs.GetInt("ContinueLevel", 1) > 1;
        bool hasUnlockedLevels = PlayerPrefs.HasKey("UnlockedLevels") && PlayerPrefs.GetInt("UnlockedLevels", 1) > 1;
        bool hasCredits = SessionManager.Instance != null && SessionManager.Instance.Credits > 0;
        bool hasSkins = SessionManager.Instance != null && SessionManager.Instance.UnlockedSkinIds.Count > 1;
        bool hasWeapons = SessionManager.Instance != null && SessionManager.Instance.UnlockedWeaponIds.Count > 1;

        return !(hasContinueLevel || hasUnlockedLevels || hasCredits || hasSkins || hasWeapons);
    }

    // ── Game flow ─────────────────────────────────────────────���───────────────
    public void StartRun(int level = 1)
    {
        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        score = 0;
        // A "regular" run always clears any pending custom-match overrides
        // — only StartCustomRun re-enables them.
        isCustomMatch = false;
        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.ResetMatch();
        if (SessionManager.Instance != null)
            SessionManager.Instance.BeginMatch();
        SetCurrentLevel(level);
        PendingMenuScreen = MenuScreen.MainMenu;
        BeginSceneLoad("GameScene");
    }

    /// <summary>
    /// Begin a Custom Match using the values picked in the Custom Match menu.
    /// </summary>
    /// <param name="enemyCount">Number of enemies (1..25).</param>
    /// <param name="matchTimeSeconds">Total match length in seconds (e.g. 120, 300, 600).</param>
    /// <param name="difficultyLabel">"Easy", "Normal", or "Veteran".</param>
    /// <param name="level">Which weapon level to play with (defaults to 1).</param>
    public void StartCustomRun(int enemyCount, int matchTimeSeconds, string difficultyLabel, int level = 1)
    {
        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        score = 0;

        isCustomMatch          = true;
        customEnemyCount       = Mathf.Clamp(enemyCount, CustomEnemyMin, CustomEnemyMax);
        customMatchTimeSeconds = Mathf.Clamp(matchTimeSeconds, CustomMatchTimeMin, CustomMatchTimeMax);
        customDifficulty       = NormalizeDifficulty(difficultyLabel);

        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.ResetMatch();
        if (SessionManager.Instance != null)
            SessionManager.Instance.BeginMatch();
        SetCurrentLevel(level);
        PendingMenuScreen = MenuScreen.MainMenu;
        BeginSceneLoad("GameScene");
    }

    public void ReplayCurrentLevel()
    {
        if (MultiplayerMode.IsMultiplayer)
        {
            ReplayMultiplayerMatch();
            return;
        }

        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
        EndMatchCinematic.GameplayLocked = false;
        HealthManager.ReleaseStartupProtection();
        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.ResetMatch();
        if (SessionManager.Instance != null)
            SessionManager.Instance.BeginMatch();

        int level = _cachedRetryLevel > 0 ? _cachedRetryLevel : currentLevel;
        SetCurrentLevel(level);

        ResetLevelState();
        PendingMenuScreen = MenuScreen.MainMenu;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        string sceneName = ResolveRetrySceneName();
        int sceneIndex = ResolveRetrySceneBuildIndex(sceneName);
        Debug.Log($"[Retry] reloading scene = {sceneName} index = {sceneIndex} level = {level}");

        _retryLoadPending = true;
        if (_sceneLoadRoutine != null)
        {
            StopCoroutine(_sceneLoadRoutine);
            _sceneLoadRoutine = null;
        }

        LoadRetryScene(sceneName, sceneIndex);
    }

    public void ReplayMultiplayerMatch()
    {
#if PUN_2_OR_NEWER
        if (!PhotonNetwork.InRoom || !PhotonNetwork.IsConnectedAndReady)
        {
            GoToMainMenu();
            return;
        }

        Time.timeScale = 1f;
        EndMatchCinematic.GameplayLocked = true;
        MatchInitializer.ResetSessionState();
        MatchInitializer.EnsureExists();

        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.ResetMatch();

        ResetLevelState();
        LoadingScreenUI.ShowTimedForMultiplayer(5f);

        if (PhotonNetwork.IsMasterClient)
            PhotonNetwork.LoadLevel(MultiplayerMode.MultiplayerSceneName);
#else
        GoToMainMenu();
#endif
    }

    public void SetCurrentLevel(int level)
    {
        currentLevel = Mathf.Clamp(level, 1, TotalLevels);
        _cachedRetryLevel = currentLevel;
        enemiesRemaining = 0;
        PlayerPrefs.SetInt("ContinueLevel", currentLevel);
        if (MatchStatsManager.Instance != null)
            MatchStatsManager.Instance.ResetMatch();
        ResetLevelState();
    }

    private static bool IsSinglePlayerGameplayScene(string sceneName)
    {
        return string.Equals(sceneName, MultiplayerMode.SinglePlayerSceneName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sceneName, "GameScene", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRetrySceneName()
    {
        Scene active = SceneManager.GetActiveScene();
        if (active.IsValid() && active.isLoaded && IsSinglePlayerGameplayScene(active.name) && SceneExistsInBuild(active.name))
            return active.name;

        if (!string.IsNullOrEmpty(_cachedRetrySceneName)
            && IsSinglePlayerGameplayScene(_cachedRetrySceneName)
            && SceneExistsInBuild(_cachedRetrySceneName))
            return _cachedRetrySceneName;

        if (_cachedRetrySceneBuildIndex >= 0)
        {
            string indexedScene = SceneNameForBuildIndex(_cachedRetrySceneBuildIndex);
            if (IsSinglePlayerGameplayScene(indexedScene))
                return indexedScene;
        }

        return MultiplayerMode.SinglePlayerSceneName;
    }

    private static int ResolveRetrySceneBuildIndex(string sceneName)
    {
        int count = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < count; i++)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            if (string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    sceneName,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string SceneNameForBuildIndex(int buildIndex)
    {
        if (buildIndex < 0 || buildIndex >= SceneManager.sceneCountInBuildSettings)
            return string.Empty;

        string path = SceneUtility.GetScenePathByBuildIndex(buildIndex);
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private void LoadRetryScene(string sceneName, int sceneIndex)
    {
        try
        {
            if (SceneExistsInBuild(sceneName))
            {
                SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                return;
            }

            if (sceneIndex >= 0 && sceneIndex < SceneManager.sceneCountInBuildSettings)
            {
                SceneManager.LoadScene(sceneIndex, LoadSceneMode.Single);
                return;
            }

            Debug.LogWarning($"[Retry] Gameplay scene '{sceneName}' is not in Build Settings; falling back to {MultiplayerMode.SinglePlayerSceneName}.");
            SceneManager.LoadScene(MultiplayerMode.SinglePlayerSceneName, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            _retryLoadPending = false;
            Debug.LogError($"[Retry] failed to reload gameplay scene '{sceneName}': {e.Message}");
            GoToMainMenu();
        }
    }

    private IEnumerator FinalizeRetryReloadNextFrames()
    {
        yield return null;
        yield return null;

        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
        EndMatchCinematic.GameplayLocked = false;
        HealthManager.ReleaseStartupProtection();
        LevelManager.RunFrameZeroRuntimeSync();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        PlayerController pc = FindFirstObjectByType<PlayerController>();
        if (pc != null)
        {
            pc.enabled = true;
            CharacterController cc = pc.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;
        }

        if (HUDManager.Instance != null)
            HUDManager.Instance.ApplySinglePlayerGameplayState();

        _retryFinalizeRoutine = null;
    }

    /// <summary>
    /// Match length in seconds. Default match length is 5 minutes (300s).
    /// In a Custom Match, the player picks 2/5/10 minutes via the menu and
    /// we honour that instead.
    /// </summary>
    public float LevelTimeLimitSeconds
        => isCustomMatch ? Mathf.Clamp(customMatchTimeSeconds, CustomMatchTimeMin, CustomMatchTimeMax) : 300f;

    public int GetEnemyCount()
        => isCustomMatch ? Mathf.Clamp(customEnemyCount, CustomEnemyMin, CustomEnemyMax) : DefaultEnemyCount;

    /// <summary>Active difficulty string — Custom Match overrides the persisted Options value.</summary>
    public string ActiveDifficulty => isCustomMatch ? NormalizeDifficulty(customDifficulty) : difficulty;

    public float GetEnemySpeed()
    {
        float baseSpeed = 1.85f + ((currentLevel - 1) * 0.045f);
        switch (ActiveDifficulty)
        {
            case "Easy":    return baseSpeed * 0.88f;
            case "Hard":    return baseSpeed * 1.05f;
            case "Veteran": return baseSpeed * 1.18f;
            default:        return baseSpeed;
        }
    }

    public float GetEnemyDamage()
    {
        // Base 7.0 per hit on Normal level 1 (user requested), milder scaling so
        // late-game doesn't spike. Easy/Hard/Veteran preserve relative difficulty.
        float baseDamage = 7f + ((currentLevel - 1) * 0.30f);
        switch (ActiveDifficulty)
        {
            case "Easy":    return baseDamage * 0.82f;
            case "Hard":    return baseDamage * 1.15f;
            case "Veteran": return baseDamage * 1.40f;
            default:        return baseDamage;
        }
    }

    /// <summary>Multiplier applied to enemy max-HP. Veteran enemies tank more hits.</summary>
    public float GetEnemyHealthMultiplier()
    {
        switch (ActiveDifficulty)
        {
            case "Easy":    return 0.85f;
            case "Hard":    return 1.15f;
            case "Veteran": return 1.45f;
            default:        return 1.00f;
        }
    }

    public string  GetWeaponNameForLevel(int level)         => LevelWeaponNames[Mathf.Clamp(level - 1, 0, LevelWeaponNames.Length - 1)];
    public float   GetWeaponDamageForLevel(int level)       => LevelWeaponDamage[Mathf.Clamp(level - 1, 0, LevelWeaponDamage.Length - 1)];
    public float   GetWeaponRangeForLevel(int level)        => LevelWeaponRange[Mathf.Clamp(level - 1, 0, LevelWeaponRange.Length - 1)];
    public Color   GetWeaponColorForLevel(int level)        => LevelWeaponColors[Mathf.Clamp(level - 1, 0, LevelWeaponColors.Length - 1)];
    public WeaponType GetWeaponTypeForLevel(int level)      => LevelWeaponTypes[Mathf.Clamp(level - 1, 0, LevelWeaponTypes.Length - 1)];
    public float   GetWeaponExplosionRadiusForLevel(int level) => LevelWeaponExplosionRadius[Mathf.Clamp(level - 1, 0, LevelWeaponExplosionRadius.Length - 1)];

    public void AddScore(int points) { score += points; }

    public void InitializeEnemyCount(int count)
    {
        totalEnemiesSpawned    = count;
        enemiesRemaining       = count;
        enemiesKilledThisLevel = 0;

        OnEnemiesRemainingChanged?.Invoke(enemiesRemaining);

        Debug.Log($"[GameManager] InitializeEnemyCount: {count} enemies registered for this level.");
    }

    /// <summary>Enemy hits to kill the player from full health (with 100 HP, Normal = 10 damage per hit).</summary>
    public int GetPlayerHitsToKill()
    {
        switch (ActiveDifficulty)
        {
            case "Easy":    return 15;
            case "Hard":    return 7;
            case "Veteran": return 6;
            default:        return 10;
        }
    }

    public int GetEnemyHitsToKillByPlayer()
    {
        switch (ActiveDifficulty)
        {
            case "Easy":    return 3;
            case "Hard":    return 7;
            case "Veteran": return 8;
            default:        return 5;
        }
    }

    public void EnemyKilled(bool byPlayer = false, bool assistedByPlayer = false)
    {
        enemiesKilledThisLevel++;
        enemiesRemaining = Mathf.Max(0, enemiesRemaining - 1);
        OnEnemiesRemainingChanged?.Invoke(enemiesRemaining);

        if (byPlayer)
            AddScore(100);
        else if (assistedByPlayer)
            AddScore(50);

        if (HUDManager.Instance != null)
        {
            HUDManager.Instance.UpdateEnemyCount(enemiesRemaining);

            if (byPlayer)
            {
                killCount++;
                HUDManager.Instance.UpdateScore(score);
                HUDManager.Instance.RegisterKill();
                if (CombatUIManager.Instance != null) CombatUIManager.Instance.ShowKill();
                else HUDManager.Instance.ShowXpPopup(100, "KILL");
            }
            else if (assistedByPlayer)
            {
                HUDManager.Instance.UpdateScore(score);
                if (CombatUIManager.Instance != null) CombatUIManager.Instance.ShowAssist();
                else HUDManager.Instance.ShowXpPopup(50, "ASSIST");
            }
        }

        Debug.Log($"[GameManager] EnemyKilled: {enemiesKilledThisLevel}/{totalEnemiesSpawned} killed, {enemiesRemaining} remaining.");

        if (totalEnemiesSpawned > 0
            && enemiesKilledThisLevel >= totalEnemiesSpawned
            && !_levelCompleteTriggered)
        {
            _levelCompleteTriggered = true;
            Debug.Log($"[GameManager] All enemies eliminated — waiting {victoryDelaySeconds}s before level complete.");
            StartCoroutine(LevelCompleteDelayed());
        }
    }

    private IEnumerator LevelCompleteDelayed()
    {
        yield return new WaitForSeconds(victoryDelaySeconds);
        LevelComplete();
    }

    private void LevelComplete()
    {
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        Debug.Log("[EndUI] outcome = Victory reason = level win condition met");

        int unlocked = PlayerPrefs.GetInt("UnlockedLevels", 1);
        if (currentLevel >= unlocked)
            PlayerPrefs.SetInt("UnlockedLevels", Mathf.Min(TotalLevels, currentLevel + 1));

        PlayerPrefs.SetInt("ContinueLevel", Mathf.Min(TotalLevels, currentLevel + 1));
        PlayerPrefs.Save();

        if (SessionManager.Instance != null)
            SessionManager.Instance.EndMatch(playerWon: true);

        PendingMenuScreen = MenuScreen.LevelComplete;
        SceneManager.LoadScene("MainMenu");
    }

    private void ResolveTimedMatchWinner()
    {
        if (MultiplayerMode.IsMultiplayer)
        {
            Debug.Log("[EndUI] blocked stale/invalid outcome in multiplayer");
            return;
        }

        if (totalEnemiesSpawned > 0 && enemiesKilledThisLevel >= totalEnemiesSpawned)
        {
            Debug.Log("[EndUI] outcome = Victory reason = all enemies eliminated before timer");
            LevelComplete();
            return;
        }

        bool playerWon = EvaluateSinglePlayerTimerVictory();

        if (playerWon)
        {
            Debug.Log("[EndUI] outcome = Victory reason = timer leaderboard player won");
            LevelComplete();
        }
        else
        {
            Debug.Log("[EndUI] outcome = MissionFailed reason = timer expired player did not win");
            GameOver();
        }
    }

    private bool EvaluateSinglePlayerTimerVictory()
    {
        MatchStatsManager stats = MatchStatsManager.Instance;
        if (stats == null)
            return killCount > 0;

        int playerKills = killCount;
        int topKills = 0;
        bool topIsPlayer = false;

        var leaders = stats.GetTopCombatants(64);
        if (leaders.Count > 0)
        {
            topKills = leaders[0].Kills;
            topIsPlayer = leaders[0].IsPlayer;
            for (int i = 0; i < leaders.Count; i++)
            {
                if (leaders[i].IsPlayer)
                {
                    playerKills = Mathf.Max(playerKills, leaders[i].Kills);
                    break;
                }
            }
        }

        if (playerKills <= 0 && topKills <= 0)
            return false;

        return topIsPlayer || playerKills >= topKills;
    }

    public void LoadNextLevel()
    {
        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        currentLevel++;
        if (currentLevel > TotalLevels)
        {
            currentLevel = TotalLevels;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
            PendingMenuScreen = MenuScreen.Victory;
            SceneManager.LoadScene("MainMenu");
            return;
        }

        ResetLevelState();
        PendingMenuScreen = MenuScreen.MainMenu;
        BeginSceneLoad("GameScene");
    }

    public void GameOver()
    {
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        Debug.Log("[EndUI] outcome = MissionFailed reason = player eliminated or fail condition");

        if (SessionManager.Instance != null)
            SessionManager.Instance.EndMatch(playerWon: false);

        PendingMenuScreen = MenuScreen.GameOver;
        SceneManager.LoadScene("MainMenu");
    }

    public static MenuScreen ResolveAuthoritativeOutcomeScreen()
    {
        if (MultiplayerMode.IsMultiplayer)
            return MenuScreen.MainMenu;

        if (Instance == null)
            return PendingMenuScreen;

        if (Instance.totalEnemiesSpawned > 0 &&
            Instance.enemiesKilledThisLevel >= Instance.totalEnemiesSpawned)
            return MenuScreen.LevelComplete;

        PlayerHealth localHealth = UnityEngine.Object.FindFirstObjectByType<PlayerHealth>();
        if (localHealth != null && !localHealth.IsAlive)
            return MenuScreen.GameOver;

        return PendingMenuScreen;
    }

    public static bool IsRetrySupported()
    {
        return true;
    }

    public void GoToMainMenu()
    {
        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
        // Custom matches are session-scoped; returning to the menu always
        // ends them. If the player wants another, they re-pick it.
        isCustomMatch     = false;
        PendingMenuScreen = MenuScreen.MainMenu;
        SceneManager.LoadScene("MainMenu");
    }

    public int CalculateStars(float timeLimit)
    {
        if (levelTime <= timeLimit && !playerTookDamage) return 3;
        if (levelTime <= timeLimit) return 2;
        return 1;
    }

    public int GetUnlockedLevelCount()
    {
        return Mathf.Clamp(PlayerPrefs.GetInt("UnlockedLevels", 1), 1, TotalLevels);
    }

    private void ResetLevelState()
    {
        levelTime                = 0f;
        killCount                = 0;
        playerTookDamage         = false;
        totalEnemiesSpawned      = 0;
        enemiesKilledThisLevel   = 0;
        enemiesRemaining         = 0;
        _levelCompleteTriggered  = false;
    }

    private static bool SceneExistsInBuild(string sceneName)
    {
        int count = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < count; i++)
        {
            string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
            if (string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    sceneName,
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void BeginSceneLoad(string sceneName)
    {
        if (!SceneExistsInBuild(sceneName))
        {
            Debug.LogWarning($"[GameManager] Scene '{sceneName}' not found in Build Settings — load aborted.");
            return;
        }

        if (!isActiveAndEnabled)
        {
            try { SceneManager.LoadScene(sceneName); }
            catch (System.Exception e)
            {
                Debug.LogError($"[GameManager] SceneManager.LoadScene('{sceneName}') failed: {e.Message}");
            }
            return;
        }

        if (_sceneLoadRoutine != null)
            StopCoroutine(_sceneLoadRoutine);

        _sceneLoadRoutine = StartCoroutine(LoadSceneWithLoadingScreen(sceneName));
    }

    private IEnumerator LoadSceneWithLoadingScreen(string sceneName)
    {
        LoadingScreenUI loadingUi = LoadingScreenUI.CreateOrGet();

        float minDuration = Mathf.Max(0.1f, loadingScreenMinSeconds);
        float start = Time.unscaledTime;

        AsyncOperation op;
        try { op = SceneManager.LoadSceneAsync(sceneName); }
        catch (System.Exception e)
        {
            Debug.LogError($"[GameManager] LoadSceneAsync('{sceneName}') threw: {e.Message}");
            op = null;
        }

        if (op == null)
        {
            loadingUi.DestroySelf();
            _sceneLoadRoutine = null;
            yield break;
        }

        op.allowSceneActivation = false;

        while (true)
        {
            bool minTimeDone = (Time.unscaledTime - start) >= minDuration;
            bool sceneReady = op.progress >= 0.9f;
            if (minTimeDone && sceneReady)
                break;
            yield return null;
        }

        op.allowSceneActivation = true;
        while (!op.isDone)
            yield return null;

        CacheActiveGameplaySession();

        const float maxReadyWait = 4f;
        float readyStart = Time.unscaledTime;
        while (Time.unscaledTime - readyStart < maxReadyWait)
        {
            if (IsGameplayReady())
                break;
            yield return null;
        }

        yield return null;
        yield return null;
        LevelManager.RunFrameZeroRuntimeSync();

        if (loadingUi != null)
            loadingUi.DestroySelf();

        if (sceneName == "GameScene")
        {
            LevelManager.RunFrameZeroRuntimeSync();
            yield return MatchStartCountdownUI.Play();
            if (!MultiplayerMode.IsMultiplayer)
            {
                Time.timeScale = 1f;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                PlayerController pc = FindFirstObjectByType<PlayerController>();
                if (pc != null)
                {
                    pc.enabled = true;
                    CharacterController cc = pc.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = true;
                }
                if (HUDManager.Instance != null)
                    HUDManager.Instance.ApplySinglePlayerGameplayState();
            }
        }

        _sceneLoadRoutine = null;
    }

    private bool IsGameplayReady()
    {
        if (totalEnemiesSpawned <= 0) return false;

        GameObject player = GameObject.FindWithTag("Player");
        if (player == null) return false;

        if (Physics.Raycast(player.transform.position + Vector3.up * 0.5f,
                            Vector3.down, out RaycastHit groundHit, 3f,
                            ~0, QueryTriggerInteraction.Ignore))
        {
            float airborne = (player.transform.position.y - groundHit.point.y);
            if (airborne > 1.2f) return false;
        }
        else
        {
            return false;
        }

        Camera cam = Camera.main;
        if (cam == null) return false;

        return true;
    }
}
