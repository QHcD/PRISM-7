using System.Collections;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

[ExecuteAlways]
public class LevelBuilder : MonoBehaviour
{
    private const string RuntimeObjectName  = "__LevelBuilderRuntime";
    private const string GameplayRootName   = "GENERATED_MAP_RUNTIME";
    private const string LegacyGameplayRootName = "GameplayRoot";
    private const string ArenaRootName      = "UrbanArenaRoot";
    private const string EnemyRootName      = "EnemiesRoot";
    private const string MinimapCameraName  = "MinimapCamera";
    private const string RuntimeThirdPersonCameraName = "RuntimeThirdPersonCamera";
    private static readonly Vector3 SafeFallbackSpawn = new Vector3(0f, 1f, 0f);
    private static LevelBuilder instance;
    private bool _navMeshReady;
    private bool _playerSpawnReady;
    private bool _runtimeBuildInProgress;
    private bool _runtimeInitializationComplete;
    private Coroutine _buildRoutine;
    private Bounds _assembledWarehouseBounds;
    private bool _hasAssembledWarehouseBounds;
    private Bounds _sciFiProxyBounds;
    private bool _hasSciFiProxyBounds;
    private NavSourceDiagnostics _lastNavSourceDiagnostics;
    private static NavMeshDataInstance _sciFiNavMeshDataInstance;
    private static bool _multiplayerBuildComplete;

    private struct NavSourceDiagnostics
    {
        public int sources;
        public int walkableSources;
        public string zeroAreaReason;
    }
#if UNITY_EDITOR
    private static bool _editorPreviewQueued;
    private static double _lastEditorPreviewTime;
    private static string _lastEditorPreviewSignature;
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (!Application.isPlaying) return;
        if (instance != null) return;
        GameObject runtimeObject = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(runtimeObject);
        instance = runtimeObject.AddComponent<LevelBuilder>();
    }

    private void Awake()
    {
        UseSharedSciFiEnvironment();
        if (instance != null && instance != this)
        {
            DestroyObjectSafe(gameObject);
            return;
        }

        instance = this;
        if (Application.isPlaying)
            DontDestroyOnLoad(gameObject);
    }

    // Guard: the last frame on which we built, so we never double-build.
    private int _lastBuiltFrame = -1;

    [Header("Enemy spawn spacing")]
    [Tooltip("Preferred minimum horizontal distance between enemy spawn positions.")]
    public float minEnemySpawnSpacing = 6f;
    [Tooltip("Minimum horizontal distance from the player at spawn time. " +
             "Prevents enemies materialising on top of the player.")]
    public float minEnemyToPlayerDistance = 12f;
    [Tooltip("Logs spawn spacing validation when enabled.")]
    public bool debugSpawnSpacing = false;
    [Tooltip("Logs zone/tier per enemy spawn and draws zone gizmos when enabled.")]
    public bool debugEnemySpawnDistribution = false;

    private const float MinEnemySpawnHardFloor = 5f;
    private const float MaxNavSnapHorizontalDrift = 14f;
    private const int MaxEnemiesPerSpawnZone = 2;
    private const int SpawnZoneCount = 9;

    private sealed class EnemySpawnZone
    {
        public readonly string Name;
        public readonly System.Collections.Generic.List<Vector3> Anchors;

        public EnemySpawnZone(string name)
        {
            Name = name;
            Anchors = new System.Collections.Generic.List<Vector3>(24);
        }
    }

#if UNITY_EDITOR
    private static EnemySpawnZone[] _gizmoSpawnZones;
#endif

    // Public accessor so scene-local fallback can reach us.
    public static LevelBuilder Instance => instance;
    public static bool IsRuntimeBuildInProgress => instance != null && instance._runtimeBuildInProgress;
    public static bool IsRuntimeLevelReady => instance != null && instance._runtimeInitializationComplete;
    public static bool IsRuntimeNavMeshReady => instance != null && instance._navMeshReady;

    private void OnEnable()
    {
        if (Application.isPlaying)
            SceneManager.sceneLoaded += OnSceneLoaded;
#if UNITY_EDITOR
        else
            QueueEditorPreviewBuild();
#endif
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        if (!Application.isPlaying) return;
        HandleScene(SceneManager.GetActiveScene());
    }

#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    private static void RegisterEditorPreviewHooks()
    {
        EditorApplication.delayCall -= QueueEditorPreviewBuild;
        EditorApplication.delayCall += QueueEditorPreviewBuild;
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
        EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
        EditorSceneManager.sceneOpened += OnEditorSceneOpened;
    }

    private static void OnEditorSceneOpened(Scene scene, OpenSceneMode mode)
    {
        QueueEditorPreviewBuild();
        if (scene.IsValid() && scene.name == "MainMenu")
            StripMainMenuAtmosphereInEditor();
    }

    private static void StripMainMenuAtmosphereInEditor()
    {
        if (Application.isPlaying)
            return;

        MainMenuMapPresenter.RunSetup();
    }

    [MenuItem("PRISM/Remove Dust And Smoke From Scene")]
    private static void RemoveDustAndSmokeFromSceneMenu()
    {
        if (Application.isPlaying)
            return;

        if (SceneManager.GetActiveScene().name == "MainMenu")
            MainMenuMapPresenter.RunSetup();
        else
        {
            int removed = MapAtmosphereCleanup.RemoveFromActiveScene();
            Debug.Log($"[LevelBuilder] Removed {removed} dust/smoke object(s) from the active scene.");
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        SceneView.RepaintAll();
    }

    private static void OnEditorUpdate()
    {
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded || activeScene.name != "GameScene")
            return;

        string signature = GetEditorPreviewSignature();
        if (!string.Equals(signature, _lastEditorPreviewSignature, System.StringComparison.Ordinal))
            QueueEditorPreviewBuild(force: true);
    }

    [MenuItem("PRISM/Build Scene Preview (No Play Mode)")]
    private static void BuildScenePreviewFromMenu()
    {
        QueueEditorPreviewBuild(force: true);
    }

    private static void QueueEditorPreviewBuild()
    {
        QueueEditorPreviewBuild(force: false);
    }

    private static void QueueEditorPreviewBuild(bool force)
    {
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (_editorPreviewQueued && !force)
            return;

        _editorPreviewQueued = true;
        EditorApplication.delayCall += () =>
        {
            _editorPreviewQueued = false;
            BuildEditorPreviewIfGameScene(force);
        };
    }

    private static void BuildEditorPreviewIfGameScene(bool force)
    {
        if (Application.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded || activeScene.name != "GameScene")
            return;

        LevelBuilder builder = GetOrCreateEditorBuilder();
        if (builder == null)
            return;

        builder.BuildEditorScenePreview(force);
    }

    private static LevelBuilder GetOrCreateEditorBuilder()
    {
        LevelBuilder builder = Object.FindFirstObjectByType<LevelBuilder>();
        if (builder != null)
            return builder;

        GameObject levelManager = GameObject.Find("LevelManager");
        if (levelManager == null)
            levelManager = new GameObject("LevelManager");

        builder = levelManager.GetComponent<LevelBuilder>();
        if (builder == null)
            builder = levelManager.AddComponent<LevelBuilder>();

        return builder;
    }

    public void BuildEditorScenePreview(bool force = false)
    {
        if (Application.isPlaying)
            return;

        CleanupDuplicateGameManagersInEditor();
        CleanupDuplicateRuntimeThirdPersonCamerasInEditor();

        double now = EditorApplication.timeSinceStartup;
        if (!force && now - _lastEditorPreviewTime < 0.5d)
            return;

        _lastEditorPreviewTime = now;

        if (!force && HasCompleteScenePreview())
        {
            EnsurePreviewObjectsVisible();
            return;
        }

        Debug.Log("[LevelBuilder] Building edit-mode GameScene preview.");
        BuildGameScene();
        EnsurePreviewObjectsVisible();
        _lastEditorPreviewSignature = GetEditorPreviewSignature();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        SceneView.RepaintAll();
    }

    private static string GetEditorPreviewSignature()
    {
        GameManager manager = GameManager.Instance;
        int level = manager != null ? manager.currentLevel : 1;
        int map = manager != null ? (int)manager.GetSelectedMap() : 0;
        return $"{level}:{map}";
    }

    private static bool HasCompleteScenePreview()
    {
        GameObject gameplayRoot = GameObject.Find(GameplayRootName);
        GameObject arenaRoot = GameObject.Find(ArenaRootName);
        GameObject enemyRoot = GameObject.Find(EnemyRootName);
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();

        bool hasArenaVisuals = arenaRoot != null &&
            arenaRoot.GetComponentsInChildren<Renderer>(true).Length > 0;
        bool hasEnemies = enemyRoot != null &&
            enemyRoot.GetComponentsInChildren<EnemyController>(true).Length > 0;

        return gameplayRoot != null && hasArenaVisuals && hasEnemies && player != null;
    }

    private static void EnsurePreviewObjectsVisible()
    {
        SetRootActive(GameplayRootName);
        SetRootActive(ArenaRootName);
        SetRootActive(EnemyRootName);

        GameObject arenaRoot = GameObject.Find(ArenaRootName);
        EnsureSceneGroundVisible(arenaRoot != null ? arenaRoot.transform : null);
    }
#endif

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        HandleScene(scene);
    }

    /// <summary>
    /// Handles scene build DIRECTLY from the callback — no deferral to
    /// Update(), which was proven unreliable on DDOL objects after scene
    /// transitions in certain Unity versions.
    /// </summary>
    private void HandleScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;

        // Prevent double-build in the same frame (Start + OnSceneLoaded both fire).
        if (_lastBuiltFrame == Time.frameCount) return;
        _lastBuiltFrame = Time.frameCount;

        Debug.Log($"[LevelBuilder] HandleScene '{scene.name}' on frame {Time.frameCount}");

        if (scene.name == "MainMenu")
        {
            Debug.Log("[LevelBuilder] MainMenu detected in HandleScene. Returning immediately.");
            return;
        }

        if (scene.name == "GameScene")
        {
            Debug.Log("[LevelBuilder] Building GameScene...");
            if (Application.isPlaying)
                StartRuntimeGameSceneBuild();
            else
                BuildGameScene();
        }
        else if (scene.name == MultiplayerMode.MultiplayerSceneName)
        {
            Debug.Log("[MPBuild] running in build/editor = " + (Application.isEditor ? "editor" : "build"));
            Debug.Log("[LevelBuilder] Building MultiplayerGameScene map (synchronous)...");
            BuildMultiplayerScene();
        }
    }

    /// <summary>
    /// Public entry point so the scene-local fallback trigger can call us.
    /// </summary>
    public void TriggerBuild()
    {
        if (_lastBuiltFrame == Time.frameCount) return;
        Scene active = SceneManager.GetActiveScene();
        if (active.name == "GameScene")
        {
            _lastBuiltFrame = Time.frameCount;
            Debug.Log("[LevelBuilder] TriggerBuild called from scene-local fallback.");
            if (Application.isPlaying)
                StartRuntimeGameSceneBuild();
            else
                BuildGameScene();
        }
        else if (active.name == MultiplayerMode.MultiplayerSceneName)
        {
            _lastBuiltFrame = Time.frameCount;
            Debug.Log("[LevelBuilder] TriggerBuild called for MultiplayerGameScene.");
            BuildMultiplayerScene();
        }
    }

    private void BuildMultiplayerScene()
    {
        UseSharedSciFiEnvironment();
        _multiplayerBuildComplete = false;
        try
        {
            CleanupGeneratedRuntimeObjects();
            Transform gameplayRoot = GetOrCreateRoot(GameplayRootName);
            Transform arenaRoot = GetOrCreateChildRoot(gameplayRoot, ArenaRootName);
            gameplayRoot.gameObject.SetActive(true);
            arenaRoot.gameObject.SetActive(true);
            ClearChildren(arenaRoot);

            BuildArena(arenaRoot);
            if (!useSciFiArena)
                StabilizeGround(arenaRoot);
            EnsureIndustrialDoorsInteractable(arenaRoot);
            EnsureMinimapCamera();
            _navMeshReady = Application.isPlaying && TryBuildNavMesh();

            MapReadinessInfo info;
            if (!TryGetMultiplayerMapReadiness(out info))
            {
                Debug.LogError("[MPBuild] ERROR map missing in build");
                Debug.LogError("[MPBuild] " + info.Message);
                return;
            }

            _multiplayerBuildComplete = true;
            Debug.Log("[MPBuild] map loaded from scene/resource/addressable = resource");
        }
        catch (System.Exception e)
        {
            _multiplayerBuildComplete = false;
            Debug.LogError("[MPBuild] ERROR map missing in build");
            Debug.LogError("[MPBuild] " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
    }

    /// <summary>
    /// Synchronous GameScene build. Replaces the old coroutine approach
    /// that was silently dying during DDOL scene transitions.
    /// </summary>
    private void BuildGameScene()
    {
        UseSharedSciFiEnvironment();
        Debug.Log("[LevelBuilder] ===== BUILD START =====");
        if (Application.isPlaying)
        {
            _runtimeBuildInProgress = true;
            _runtimeInitializationComplete = false;
            _navMeshReady = false;
            _playerSpawnReady = false;
        }
        try
        {
#if UNITY_EDITOR
            if (useSciFiArena && !Application.isPlaying && !EnsureSciFiArenaPrefabReadyInEditor())
            {
                Debug.LogError("[LevelBuilder] SciFiArena prefab validation failed before GameScene build.");
                return;
            }
#endif
            EnsureGameManager();
            Debug.Log("[LevelBuilder] Step 1: GameManager ensured");
            if (GameManager.Instance != null)
                GameManager.Instance.InitializeEnemyCount(0);
            if (Application.isPlaying && useSciFiArena)
                SetExistingPlayersActive(false);

            GameManager manager = GameManager.Instance;
            if (manager != null)
                manager.SetPerspectiveMode(GameManager.PerspectiveMode.ThirdPerson);

            // ── Clear stale tagged level content from the previous level ───────
            // Kills any "Environment"/"LevelContent"/"Map"-tagged objects left
            // over from a previous build so the new map spawns into a clean
            // scene (fixes the "old map still visible in Editor" bug).
            ClearExistingLevel();
            CleanupGeneratedRuntimeObjects();
            DisableSciFiExteriorFallbacks();

            Transform gameplayRoot = GetOrCreateRoot(GameplayRootName);
            Transform arenaRoot    = GetOrCreateChildRoot(gameplayRoot, ArenaRootName);
            Transform enemyRoot    = GetOrCreateChildRoot(gameplayRoot, EnemyRootName);
            ClearChildren(arenaRoot);
            ClearChildren(enemyRoot);
            Debug.Log("[LevelBuilder] Step 2: Roots created");

            GameObject plane = GameObject.Find("Plane");
            if (plane != null)
            {
                if (useSciFiArena)
                {
                    plane.SetActive(false);
                }
                else
                {
                    plane.SetActive(true);
                    plane.transform.position   = Vector3.zero;
                    plane.transform.localScale = new Vector3(6f, 1f, 6f);
                    HideNavOnlySurface(plane);
                }
            }

            BuildArena(arenaRoot);
            DisableSciFiExteriorFallbacks();
            // EnsureSceneGroundVisible skipped — industrial map has its own ground
            if (!useSciFiArena)
                StabilizeGround(arenaRoot);
            EnsureIndustrialDoorsInteractable(arenaRoot);
            bool geometryReady = WakeAndVerifyEnvironmentColliders(arenaRoot);
            Debug.Log(geometryReady
                ? "[LevelBuilder] Step 3: Arena built + colliders verified"
                : "[LevelBuilder] Step 3: Arena collider verification failed");
            if (Application.isPlaying && !geometryReady)
            {
                AbortRuntimeInitialization("[LevelBuilder] Runtime initialization aborted: environment colliders are not valid.");
                return;
            }

            // Environmental props are provided by the RPG/FPS industrial map prefab — skip procedural spawning.
            Debug.Log("[LevelBuilder] Step 4: Props skipped (industrial map provides own environment)");

            EnsureMinimapCamera();
            Debug.Log("[LevelBuilder] Step 5: Minimap camera");

            if (Application.isPlaying)
            {
                _buildRoutine = StartCoroutine(CompleteRuntimeInitialization(enemyRoot));
                return;
            }

            CompleteEnvironmentInitialization(enemyRoot);
        }
        catch (System.Exception e)
        {
            string errorMsg = $"[LevelBuilder] BUILD FAILED: {e.GetType().Name}: {e.Message}\n{e.StackTrace}";
            Debug.LogWarning(errorMsg);
            // Also write to file so we can read it even if console logs are unreachable
            try { System.IO.File.WriteAllText(Application.dataPath + "/../build_error.log", errorMsg); }
            catch { }
            if (Application.isPlaying)
                AbortRuntimeInitialization("[LevelBuilder] Runtime initialization aborted after build exception.");
        }
    }

    private void StartRuntimeGameSceneBuild()
    {
        if (_buildRoutine != null)
        {
            StopCoroutine(_buildRoutine);
            _buildRoutine = null;
        }
        PrepareRuntimeNavMeshRestart();
        BuildGameScene();
    }

    private IEnumerator CompleteRuntimeInitialization(Transform enemyRoot)
    {
        yield return new WaitForFixedUpdate();
        yield return WaitForSciFiArenaCoreReady();
        if (useSciFiArena)
            Debug.Log($"[SciFiRestart] arena rebuilt={IsSciFiArenaCoreReady()}");
        yield return BuildRuntimeNavMeshWhenSettled();
        if (useSciFiArena)
            Debug.Log($"[SciFiRestart] navmesh ready={_navMeshReady}");
        CompleteEnvironmentInitialization(enemyRoot);
        _buildRoutine = null;
    }

    private IEnumerator WaitForSciFiArenaCoreReady()
    {
        if (!useSciFiArena)
            yield break;

        const float TimeoutSeconds = 5f;
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (IsSciFiArenaCoreReady())
                yield break;
            yield return null;
        }

        Debug.LogWarning("[SciFiSpawn] SciFiArena core was not ready before NavMesh build window.");
    }

    private void CompleteEnvironmentInitialization(Transform enemyRoot)
    {
        try
        {
            if (!Application.isPlaying)
                _navMeshReady = false;
            Debug.Log(_navMeshReady
                ? "[LevelBuilder] Step 6: NavMesh built"
                : "[LevelBuilder] Step 6: NavMesh skipped; enemy spawning requires valid NavMesh");

            if (useSciFiArena && !_navMeshReady)
            {
                Debug.LogWarning("[SciFiSpawn] SciFiArena NavMesh is not valid; player spawn will use adaptive floor projection and enemies remain gated by NavMesh readiness.");
            }

            ConfigurePlayer();
            Debug.Log("[LevelBuilder] Step 7: Player configured");

            TryInitializeOptionalAISystems();
            if (_navMeshReady && _playerSpawnReady)
            {
                SpawnEnemies(enemyRoot);
            }
            else if (GameManager.Instance != null)
            {
                GameManager.Instance.InitializeEnemyCount(0);
            }
            Debug.Log("[LevelBuilder] Step 8: Enemies spawned: " +
                (GameManager.Instance != null ? GameManager.Instance.enemiesRemaining.ToString() : "?"));

            EnsureHud();
            ResetRuntimeUiAnchors();
            EnsurePauseMenu();
            int spawnedSummary = GameManager.Instance != null ? GameManager.Instance.enemiesRemaining : 0;
            VerifyPlayerInsideArena();
            _runtimeInitializationComplete = true;
            _runtimeBuildInProgress = false;
            Debug.Log($"[StabilizationSummary] NavMeshRebuilt={_navMeshReady} " +
                      $"NavMeshCoverage={EstimateNavMeshCoverageArea():F1}m2 spawnAnchorsValid={spawnedSummary > 0} " +
                      $"enemiesSpawned={spawnedSummary}");
            Debug.Log("[LevelBuilder] ===== BUILD COMPLETE =====");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelBuilder] Runtime initialization failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            _runtimeInitializationComplete = true;
            _runtimeBuildInProgress = false;
            if (GameManager.Instance != null)
                GameManager.Instance.InitializeEnemyCount(0);
        }
    }

    private bool IsSciFiArenaCoreReady()
    {
        if (!useSciFiArena)
            return true;

        Transform mapRoot = GameObject.Find("FbxMap")?.transform;
        if (mapRoot == null || !mapRoot.gameObject.activeInHierarchy)
            return false;

        Transform floorRoot = FindSciFiFloorRoot(mapRoot);
        if (floorRoot == null)
            return false;

        Collider[] colliders = floorRoot.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled || collider.isTrigger)
                continue;
            if (collider.bounds.size.x < 0.5f || collider.bounds.size.z < 0.5f)
                continue;
            return true;
        }

        Transform proxyRoot = mapRoot.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot == null)
            return false;

        Collider[] proxyColliders = proxyRoot.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < proxyColliders.Length; i++)
        {
            Collider collider = proxyColliders[i];
            if (collider != null && collider.enabled && !collider.isTrigger)
                return true;
        }

        return false;
    }

    private static Transform FindSciFiFloorRoot(Transform mapRoot)
    {
        if (mapRoot == null)
            return null;

        Transform direct = mapRoot.Find("Floors");
        if (direct != null)
            return direct;

        Transform[] all = mapRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            string n = t.name.ToLowerInvariant();
            if (n == "floors" || n.Contains("floors & floor props") || n == "floor tiles")
                return t;
        }

        return null;
    }

    private IEnumerator BuildRuntimeNavMeshWhenSettled()
    {
        _navMeshReady = false;
        _playerSpawnReady = false;
        yield return null;
        yield return new WaitForFixedUpdate();

        const int MaxAttempts = 8;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            _navMeshReady = TryBuildNavMesh();
            if (_navMeshReady && IsRuntimeNavMeshValid())
                yield break;
            yield return new WaitForSecondsRealtime(0.1f);
        }

        _navMeshReady = IsRuntimeNavMeshValid();
        if (!_navMeshReady)
            Debug.LogWarning("[LevelBuilder] Runtime NavMesh is not ready after restart retry window; player and enemy spawning remain blocked.");
    }

    private void PrepareRuntimeNavMeshRestart()
    {
        _navMeshReady = false;
        _playerSpawnReady = false;
        _runtimeInitializationComplete = false;
        _runtimeBuildInProgress = false;
        _lastNavSourceDiagnostics = default;
        LevelInteriorSpawnResolver.ClearSpawnCache();
        if (useSciFiArena)
            SetExistingPlayersActive(false);
        _sciFiNavMeshDataInstance.Remove();
        if (Application.isPlaying)
            NavMesh.RemoveAllNavMeshData();
    }

    private void AbortRuntimeInitialization(string message)
    {
        Debug.LogError(message);
        _navMeshReady = false;
        _runtimeInitializationComplete = false;
        _runtimeBuildInProgress = false;
        _buildRoutine = null;
        if (GameManager.Instance != null)
            GameManager.Instance.InitializeEnemyCount(0);
    }

#if UNITY_EDITOR
    private static bool EnsureSciFiArenaPrefabReadyInEditor()
    {
        try
        {
            System.Type builderType = System.Type.GetType("SciFiArenaBuilder, Assembly-CSharp-Editor")
                ?? System.Type.GetType("SciFiArenaBuilder");
            if (builderType == null)
                return true;
            System.Reflection.MethodInfo method = builderType.GetMethod(
                "EnsurePrefabReady",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
                return true;
            object result = method.Invoke(null, null);
            return result is bool ready && ready;
        }
        catch (System.Exception e)
        {
            Debug.LogError("[LevelBuilder] SciFiArena prefab validation failed: " + e.Message);
            return false;
        }
    }
#endif

    private void CleanupMainMenu()
    {
        GameObject urbanArenaRoot = GameObject.Find(ArenaRootName);
        if (urbanArenaRoot != null) urbanArenaRoot.SetActive(false);
        GameObject enemiesRoot = GameObject.Find(EnemyRootName);
        if (enemiesRoot != null) enemiesRoot.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ARENA BUILDING
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Imported industrial meshes name door pieces "…door…" / "…gate…" but ship without
    /// <see cref="DoorController"/>. Adds interaction on collider objects so
    /// the player can open them with the same raycast used for <see cref="IInteractable"/>.
    /// </summary>
    private static void EnsureIndustrialDoorsInteractable(Transform arenaRoot)
    {
        if (arenaRoot == null) return;

        // Fake-door suppression: bail out unless the project has explicitly
        // opted back in to door interactions. Without this guard the method
        // attaches a DoorController + DoorPassThroughOpen to every fence/gate/
        // shutter-named mesh in the imported industrial map, producing the
        // "[E] OPEN DOOR" prompt on non-doors.
        if (!DoorController.DoorInteractionsEnabled)
        {
            Debug.Log("[DoorFix] Skipped: DoorController.DoorInteractionsEnabled=false (v3 map welded doors are not interactive).");
            return;
        }

        Collider[] colliders = arenaRoot.GetComponentsInChildren<Collider>(true);
        int fixedCount = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || !c.enabled) continue;

            Transform doorRoot = FindNearestGeneratedDoorRoot(c);
            if (doorRoot == null) continue;
            if (doorRoot.GetComponentInParent<DoorController>(true) != null) continue;

            DoorController door = doorRoot.GetComponent<DoorController>();
            if (door == null)
                door = doorRoot.gameObject.AddComponent<DoorController>();

            DoorPassThroughOpen passThrough = doorRoot.GetComponent<DoorPassThroughOpen>();
            if (passThrough == null)
                passThrough = doorRoot.gameObject.AddComponent<DoorPassThroughOpen>();

            door.openOnStart         = false;
            door.openOnPlayerTrigger = false;
            door.interactiveToggle   = false;
            // Don't hide door visuals — keep mesh visible; only disable
            // colliders so characters can pass through.
            passThrough.hideOnOpen    = false;

            int envLayer = LayerMask.NameToLayer("Environment");
            if (envLayer >= 0)
                SetLayerRecursive(doorRoot.gameObject, envLayer);

            fixedCount++;
            Debug.Log($"[DoorFix] generatedDoor={doorRoot.name} collider={c.name} controllerAttached=True");
        }

        if (fixedCount > 0)
            Debug.Log($"[DoorFix] Ensured {fixedCount} generated door collider(s) have DoorController in their hierarchy.");
    }

    private static Transform FindNearestGeneratedDoorRoot(Collider collider)
    {
        if (collider == null) return null;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            if (IsGeneratedDoorName(t.name))
                return t;

            if (t == collider.transform && t.name == "Object084" && IsKnownImportedDoorMesh(t))
                return t;
        }

        return null;
    }

    private static bool IsGeneratedDoorName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return false;

        string lower = objectName.ToLowerInvariant();
        return lower.Contains("door")
            || lower.Contains("gate")
            || lower.Contains("garage")
            || lower.Contains("shutter")
            || lower.Contains("rollup");
    }

    private static bool IsKnownImportedDoorMesh(Transform t)
    {
        if (t == null || t.name != "Object084")
            return false;

        if (t.GetComponent<Collider>() == null ||
            t.GetComponent<Renderer>() == null ||
            t.GetComponent<MeshFilter>() == null)
            return false;

        for (Transform parent = t.parent; parent != null; parent = parent.parent)
        {
            string lower = parent.name.ToLowerInvariant();
            if (lower.Contains("hangar") || lower.Contains("industrial"))
                return true;
        }

        return false;
    }

    private void BuildArena(Transform arenaRoot)
    {
        UseSharedSciFiEnvironment();
        GameManager.ArenaMap map = GameManager.Instance != null
            ? GameManager.Instance.GetSelectedMap()
            : GameManager.ArenaMap.Map1;

        // LoadFbxMap adds MeshColliders to every mesh for NavMesh + physics,
        // dynamically measures the map bounds, and grounds the FBX visuals at Y=0.
        LoadFbxMap(arenaRoot, map);

        // SciFiArena is authored from complete warehouse demo modules; do not
        // add visual closure/backfill/edge blockers over it.
        if (!useSciFiArena)
            CreatePhysicsBounds(arenaRoot);

        Transform fbx = arenaRoot.Find("FbxMap");
        if (!useSciFiArena)
            ArenaVisualBounds.Install(arenaRoot, arenaHalfSize, fbx, debugArenaVisualBounds || debugSpawnValidation);

        if (fbx != null)
        {
            if (!useSciFiArena)
            {
                MapAttachedPropsPreserver.Capture(fbx);
                EnvironmentGroundAnchor.Install(fbx, debugArenaVisualBounds || debugSpawnValidation);
                MapAttachedPropsPreserver.Restore(fbx);
            }
        }

        Transform closure = !useSciFiArena ? arenaRoot.Find("ArenaVisualClosure") : null;
        if (closure != null)
            MapVisibilityStabilizer.Install(closure, debugArenaVisualBounds || debugSpawnValidation);
    }

    // Arena half-size for the RPG/FPS industrial map (larger than the old 44×44 primitive arenas)
    [Header("Stabilization Settings")]
    [Tooltip("Dynamic half size of the arena, computed from the loaded map's bounds.")]
    public float arenaHalfSize = 80f;
    [Tooltip("Logs spawn bounds and reachability checks when enabled.")]
    public bool debugSpawnValidation = false;
    [Tooltip("Draw arena perimeter/floor gizmos and log visual closure build.")]
    public bool debugArenaVisualBounds = false;


    /// <summary>Creates a NavMesh floor and installs arena envelope (walls, fog, kill volume).</summary>
    private void CreatePhysicsBounds(Transform parent)
    {
        float full = arenaHalfSize * 2f;

        // Physics floor — kept invisible; the industrial map provides its own visible ground.
        // It still gives NavMesh a solid surface to bake on.
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Ground_PhysicsFloor";
        floor.transform.SetParent(parent, false);
        floor.transform.localPosition = new Vector3(0f, -0.3f, 0f);  // slightly below map ground
        floor.transform.localScale    = new Vector3(full, 0.1f, full);
        Renderer floorRend = floor.GetComponent<Renderer>();
        if (floorRend != null) floorRend.enabled = false;  // invisible — industrial map ground shows instead
        HideNavOnlySurface(floor);

        WorldArenaStabilizer.Install(parent, arenaHalfSize, debugSpawnValidation);
    }

    private static void EnsureSceneGroundVisible(Transform fallbackParent)
    {
        bool hasFbxMap = GameObject.Find("FbxMap") != null;
        bool foundVisibleGround = false;

        string[] knownGroundNames =
        {
            "Plane", "Ground", "ground", "PhysicsFloor",
            "Ground_PhysicsFloor", "ArenaFloor", "VisibleGround_Fallback"
        };

        for (int i = 0; i < knownGroundNames.Length; i++)
        {
            GameObject ground = GameObject.Find(knownGroundNames[i]);
            if (ground == null)
                continue;

            if (hasFbxMap || IsNavOnlySurfaceName(ground.name))
            {
                HideNavOnlySurface(ground);
                continue;
            }

            foundVisibleGround |= ForceGroundVisible(ground);
        }

        if (hasFbxMap)
            return; // We have FbxMap loaded, so skip fallbacks and do not overwrite its materials

        if (fallbackParent != null)
        {
            Renderer[] renderers = fallbackParent.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer rend = renderers[i];
                if (rend == null || !LooksLikeGround(rend.gameObject.name))
                    continue;

                foundVisibleGround |= ForceGroundVisible(rend.gameObject);
            }
        }

        if (foundVisibleGround || fallbackParent == null)
            return;

        GameObject fallbackGround = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fallbackGround.name = "VisibleGround_Fallback";
        fallbackGround.transform.SetParent(fallbackParent, false);
        fallbackGround.transform.localPosition = new Vector3(0f, -0.05f, 0f);
        fallbackGround.transform.localScale = new Vector3(44f, 0.1f, 44f);
        HideNavOnlySurface(fallbackGround);
        Debug.LogWarning("[LevelBuilder] No visible ground found; created hidden VisibleGround_Fallback for physics/NavMesh only.");
    }

    private static bool ForceGroundVisible(GameObject groundObject)
    {
        if (groundObject == null)
            return false;

        groundObject.SetActive(true);

        bool hasRenderer = false;
        Renderer[] renderers = groundObject.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rend = renderers[i];
            if (rend == null)
                continue;

            rend.enabled = true;
            ApplyGroundMaterial(rend);
            hasRenderer = true;
        }

        return hasRenderer;
    }

    private static void HideNavOnlySurface(GameObject surface)
    {
        if (surface == null)
            return;

        surface.SetActive(true);

        Renderer[] renderers = surface.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rend = renderers[i];
            if (rend == null)
                continue;

            rend.enabled = false;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
        }

        Collider[] colliders = surface.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = true;
        }

        int envLayer = LayerMask.NameToLayer("Environment");
        if (envLayer >= 0)
            SetLayerRecursive(surface, envLayer);
    }

    private static bool IsNavOnlySurfaceName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return false;

        string lower = objectName.ToLowerInvariant();
        return lower == "plane"
            || lower.Contains("physicsfloor")
            || lower.Contains("physics_floor")
            || lower.Contains("visibleground_fallback")
            || lower.Contains("navmesh")
            || lower.Contains("debug");
    }

    private static bool LooksLikeGround(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return false;

        string lower = objectName.ToLowerInvariant();
        if (lower.Contains("wall"))
            return false;

        return lower.Contains("ground")
            || lower.Contains("floor")
            || lower.Contains("plane");
    }

    private static void ApplyGroundMaterial(Renderer rend)
    {
        if (rend == null)
            return;

        Shader litShader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard");
        if (litShader == null)
            return;

        Material mat = new Material(litShader);
        Color groundColor = new Color(0.42f, 0.44f, 0.39f, 1f);
        mat.color = groundColor;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", groundColor);

        rend.material = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        rend.receiveShadows = true;
    }

    private static bool IsRoadOrGroundMesh(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();

        // Exclude meshes that represent vertical or elevated structures, or non-ground props
        if (lower.Contains("wall") || lower.Contains("ceiling") || lower.Contains("roof") ||
            lower.Contains("door") || lower.Contains("gate") || lower.Contains("container") ||
            lower.Contains("building") || lower.Contains("prop") || lower.Contains("crate") ||
            lower.Contains("barrier") || lower.Contains("window") || lower.Contains("pillar") ||
            lower.Contains("column") || lower.Contains("fence") || lower.Contains("pipe") ||
            lower.Contains("ladder") || lower.Contains("stair") || lower.Contains("step") ||
            lower.Contains("skybox") || lower.Contains("reflection") || lower.Contains("invisible") ||
            lower.Contains("hanger") || lower.Contains("hangar") || lower.Contains("beam") ||
            lower.Contains("bridge") || lower.Contains("railing") || lower.Contains("office") ||
            lower.Contains("interior") || lower.Contains("car") || lower.Contains("vehicle") ||
            lower.Contains("light") || lower.Contains("lamp") || lower.Contains("barrel") ||
            lower.Contains("canister") || lower.Contains("chimney") || lower.Contains("wires") ||
            lower.Contains("cable"))
        {
            return false;
        }

        // Include names that specifically match road, ground, asphalt, floor, etc.
        return lower.Contains("road") || lower.Contains("ground") || lower.Contains("asphalt") ||
               lower.Contains("street") || lower.Contains("floor") || lower.Contains("concrete") ||
               lower.Contains("path") || lower.Contains("walkway") || lower.Contains("pavement") ||
               lower.Contains("tarmac") || lower.Contains("sidewalk") || lower.Contains("dirt") ||
               lower.Contains("sand") || lower.Contains("terrain") || lower.Contains("way") ||
               lower.Contains("arena") || lower.Contains("platform");
    }

    [Header("Arena Source")]
    [Tooltip("When true, loads the SciFi warehouse arena instead of the legacy Industrial Map.")]
    public bool useSciFiArena = true;

    /// <summary>Loads the active arena prefab from Resources and places it as visual geometry.</summary>
    private void LoadFbxMap(Transform parent, GameManager.ArenaMap map)
    {
        UseSharedSciFiEnvironment();
        EnemySpawnGeometry.AllowEnclosedArena = useSciFiArena;

        string resourcePath = useSciFiArena
            ? "Maps/SciFiArena/SciFiArena"
            : "Maps/IndustrialMap/IndustrialMap";

        GameObject mapPrefab = Resources.Load<GameObject>(resourcePath);
        Debug.Log("[MPBuild] map loaded from scene/resource/addressable = " + (mapPrefab != null ? "resource" : "missing") + " path=" + resourcePath);

        if (mapPrefab == null && useSciFiArena)
        {
            Debug.LogError("[LevelBuilder] SciFi arena prefab not found at Resources/" + resourcePath +
                ". No procedural cube fallback will be generated. Run Tools > PRISM-7 > Build SciFi Arena Prefab in the Editor, then press Play again.");
            return;
        }

        if (mapPrefab == null)
        {
            Debug.LogWarning("[LevelBuilder] Industrial map prefab not found at Resources/" + resourcePath +
                ".\nRun  PRISM-7 ▸ Setup Industrial Map  in the Editor (exit Play mode first), then press Play again.");
            CreateProceduralFallback(parent, map);
            return;
        }

        GameObject mapInstance = Instantiate(mapPrefab, parent);
        mapInstance.name = "FbxMap";
        mapInstance.transform.localPosition = Vector3.zero;
        mapInstance.transform.localRotation = Quaternion.identity;
        mapInstance.transform.localScale = Vector3.one;
#if UNITY_EDITOR
        RemoveMissingScriptsInHierarchy(mapInstance);
#endif
        TagObjectIfDefined(mapInstance, "Map");
        TagHierarchyByName(mapInstance.transform);

        if (useSciFiArena)
        {
            ConfigureSciFiWarehouseAssembly(mapInstance.transform);
            EnsureSciFiIndoorPlayerSpawnMarkers(mapInstance.transform);
        }
        else
        {

        // Compute overall and road-only bounds of the loaded FBX map renderers
        Bounds overallBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasOverall = false;

        Bounds roadBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasRoad = false;

        foreach (Renderer rend in mapInstance.GetComponentsInChildren<Renderer>(true))
        {
            if (rend is ParticleSystemRenderer) continue;
            string lowerName = rend.gameObject.name.ToLowerInvariant();
            if (lowerName.Contains("skybox") || lowerName.Contains("reflection") || lowerName.Contains("invisible"))
                continue;

            // Encapsulate in overall bounds
            if (!hasOverall)
            {
                overallBounds = rend.bounds;
                hasOverall = true;
            }
            else
            {
                overallBounds.Encapsulate(rend.bounds);
            }

            // Filter for road/ground
            if (IsRoadOrGroundMesh(rend.gameObject.name))
            {
                if (!hasRoad)
                {
                    roadBounds = rend.bounds;
                    hasRoad = true;
                }
                else
                {
                    roadBounds.Encapsulate(rend.bounds);
                }
            }
        }

        if (hasOverall)
        {
            // Align visual geometry's horizontal center to (0, 0, 0)
            Vector3 pos = mapInstance.transform.position;
            pos.x -= overallBounds.center.x;
            
            // Ground vertically using the road bounds if available, otherwise fallback to overall bounds
            if (hasRoad)
            {
                pos.y = -roadBounds.min.y; // sit the bottom of road meshes exactly at Y = 0
                Debug.Log($"[LevelBuilder] Grounding FBX map using road/ground meshes bounds min Y: {roadBounds.min.y}. Shifting mapInstance Y by {-roadBounds.min.y}");
            }
            else
            {
                pos.y = -overallBounds.min.y; // sit the overall bottom boundary exactly at Y = 0
                Debug.LogWarning($"[LevelBuilder] No road/ground meshes found! Grounding FBX map using overall bounds min Y: {overallBounds.min.y}");
            }
            
            pos.z -= overallBounds.center.z;
            mapInstance.transform.position = pos;

            // Recalculate overall bounds after shifting for arenaHalfSize calculation
            overallBounds = new Bounds(Vector3.zero, Vector3.zero);
            hasOverall = false;
            foreach (Renderer rend in mapInstance.GetComponentsInChildren<Renderer>(true))
            {
                if (rend is ParticleSystemRenderer) continue;
                string lowerName = rend.gameObject.name.ToLowerInvariant();
                if (lowerName.Contains("skybox") || lowerName.Contains("reflection") || lowerName.Contains("invisible"))
                    continue;

                if (!hasOverall) { overallBounds = rend.bounds; hasOverall = true; }
                else overallBounds.Encapsulate(rend.bounds);
            }

            if (hasOverall)
            {
                float mapExtentX = Mathf.Abs(overallBounds.extents.x);
                float mapExtentZ = Mathf.Abs(overallBounds.extents.z);
                arenaHalfSize = Mathf.Max(mapExtentX, mapExtentZ) + 1.0f;
                if (!useSciFiArena)
                    arenaHalfSize = Mathf.Max(30f, arenaHalfSize);
                Debug.Log($"[LevelBuilder] DynBounds: Centered and grounded FBX map at {mapInstance.transform.position}, arenaHalfSize dynamically set to {arenaHalfSize}");
            }
        }
        }

        // ── Activate EVERYTHING in the industrial map ──────────────────────
        // The prefab may have been captured with some objects inactive.
        // Force every child object and renderer on so the full map is visible.
        foreach (Transform t in mapInstance.GetComponentsInChildren<Transform>(true))
            t.gameObject.SetActive(true);

        foreach (Renderer rend in mapInstance.GetComponentsInChildren<Renderer>(true))
        {
            rend.enabled = true;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            rend.receiveShadows = true;
        }

        if (useSciFiArena)
            ConfigureSciFiWarehouseAssembly(mapInstance.transform);

        if (!useSciFiArena)
        {
            RemoveUnwantedMapProps(mapInstance.transform);
            MapAtmosphereCleanup.RemoveFromHierarchy(mapInstance.transform);
        }

        // ── Remove cameras / audio listeners that compete with ours ────────
        foreach (Camera embeddedCam in mapInstance.GetComponentsInChildren<Camera>(true))
        {
            Debug.Log($"[LevelBuilder] Removing embedded camera '{embeddedCam.name}' from industrial map.");
            DestroyObjectSafe(embeddedCam.gameObject);
        }
        foreach (AudioListener al in mapInstance.GetComponentsInChildren<AudioListener>(true))
            DestroyObjectSafe(al);

        // ── The industrial map ships at real-world scale — no scaling needed ─
        // ── DO NOT replace its materials — it already has correct URP textures ─
        // The old FBX material-swap logic is intentionally skipped here.
        // Replacing materials would wipe all industrial textures and make the
        // map appear as flat grey geometry.

        // ── Add colliders only where none exist (legacy industrial map only).
        // SciFiArena ships prebuilt proxy colliders from the editor builder.
        if (!useSciFiArena)
        {
            foreach (MeshFilter mf in mapInstance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponent<Collider>() != null) continue;
                if (mf.sharedMesh == null) continue;

                if (mf.sharedMesh.isReadable)
                {
                    MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = false;
                }
                else
                {
                    BoxCollider box = mf.gameObject.AddComponent<BoxCollider>();
                    box.center = mf.sharedMesh.bounds.center;
                    box.size   = mf.sharedMesh.bounds.size;
                }
            }
        }

        EnsureIndustrialDoorsInteractable(mapInstance.transform);

        if (!useSciFiArena)
        {
            MapAttachedPropsPreserver.Capture(mapInstance.transform);
            EnvironmentGroundAnchor.Install(mapInstance.transform, debugArenaVisualBounds || debugSpawnValidation);
            MapAttachedPropsPreserver.Restore(mapInstance.transform);
        }

        MapStructureStabilizer.Install(mapInstance.transform, debugArenaVisualBounds || debugSpawnValidation);
        MapVisibilityStabilizer.Install(mapInstance.transform, debugArenaVisualBounds || debugSpawnValidation);

        if (!useSciFiArena)
        {
            EnemySpawnGeometry.RefreshStreetSpawnAnchors(mapInstance.transform);
            Debug.Log($"[LevelBuilder] Street spawn anchors: {EnemySpawnGeometry.StreetSpawnAnchorCount}");
        }
        LogWarehouseModuleSummary(mapInstance.transform);

        Debug.Log($"[LevelBuilder] Industrial map loaded and fully activated: {resourcePath}");
    }

    private void UseSharedSciFiEnvironment()
    {
        useSciFiArena = true;
        EnemySpawnGeometry.AllowEnclosedArena = true;
    }

    private static void DisableSciFiExteriorFallbacks()
    {
        string[] exteriorNames =
        {
            "Plane",
            "Ground",
            "ground",
            "PhysicsFloor",
            "Ground_PhysicsFloor",
            "ArenaFloor",
            "VisibleGround_Fallback",
            "ArenaVisualClosure",
            "ArenaEdgeBlockers",
            "WorldArenaStabilizer"
        };

        int disabled = 0;
        GameObject[] all = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            GameObject obj = all[i];
            if (obj == null) continue;
            if (!IsNamedGeneratedRoot(obj.name, exteriorNames)) continue;
            if (IsInsideSciFiArenaHierarchy(obj.transform)) continue;
            DestroyObjectSafe(obj);
            disabled++;
        }

        RenderSettings.skybox = null;
        Debug.Log($"[SciFiFix] disabled exterior void objects count={disabled}");
    }

    private static bool IsInsideSciFiArenaHierarchy(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            if (t.name == "FbxMap" || t.name == "SciFiArena" || t.name == "SciFiArena(Clone)")
                return true;
        }
        return false;
    }

    private void ConfigureSciFiWarehouseAssembly(Transform mapRoot)
    {
        _hasAssembledWarehouseBounds = false;
        _hasSciFiProxyBounds = false;
        if (mapRoot == null) return;

        Transform modules = mapRoot.Find("DemoWarehouseModules");
        Transform boundsRoot = modules != null ? modules : mapRoot;

        mapRoot.position = Vector3.zero;
        mapRoot.rotation = Quaternion.identity;
        mapRoot.localScale = Vector3.one;
        if (modules != null)
        {
            modules.localPosition = Vector3.zero;
            modules.localRotation = Quaternion.identity;
            modules.localScale = Vector3.one;
        }

        if (!TryComputeWarehouseGeometryBounds(boundsRoot, out Bounds bounds))
        {
            Debug.LogError("[LevelBuilder] SciFi warehouse bounds failed: no final warehouse geometry found.");
            return;
        }

        Bounds groundBounds = bounds;
        bool hasWalkableGround = TryComputeWarehouseWalkableBounds(boundsRoot, out groundBounds);
        float groundY = hasWalkableGround ? groundBounds.min.y : bounds.min.y;

        // The prefab builder already centers X/Z as one coherent assembly. At
        // runtime only correct vertical drift so the lowest walkable floor is Y=0.
        if (Mathf.Abs(groundY) > 0.02f)
        {
            mapRoot.position += Vector3.up * -groundY;
            TryComputeWarehouseGeometryBounds(boundsRoot, out bounds);
            if (hasWalkableGround)
                TryComputeWarehouseWalkableBounds(boundsRoot, out groundBounds);
        }

        _assembledWarehouseBounds = bounds;
        _hasAssembledWarehouseBounds = true;
        Transform proxyRoot = mapRoot.Find("SciFiNavMeshProxyColliders");
        if (TryComputeColliderBounds(proxyRoot, out _sciFiProxyBounds))
            _hasSciFiProxyBounds = true;
        arenaHalfSize = Mathf.Max(bounds.extents.x, bounds.extents.z) + 1f;

        SnapWarehouseModulesToGrid(boundsRoot);
        BrandWarehouseStaticAndGI(mapRoot);
        AnchorWarehouseLightFixtures(mapRoot);

        Debug.Log($"[LevelBuilder] SciFi warehouse final bounds center={bounds.center} size={bounds.size} walkableGroundY={(hasWalkableGround ? groundBounds.min.y : bounds.min.y):F2} arenaHalfSize={arenaHalfSize:F1}");
    }

    private void EnsureSciFiIndoorPlayerSpawnMarkers(Transform mapRoot)
    {
        if (!useSciFiArena || mapRoot == null)
            return;

        LevelInteriorSpawnResolver.MarkWalkableFloorColliders(mapRoot);
        Vector3[] seeds = BuildSciFiIndoorPlayerSpawnSeeds();
        if (seeds.Length == 0)
            return;

        Transform spawnRoot = mapRoot.Find("SpawnPoints");
        if (spawnRoot == null)
        {
            GameObject root = new GameObject("SpawnPoints");
            spawnRoot = root.transform;
            spawnRoot.SetParent(mapRoot, false);
        }

        for (int i = spawnRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = spawnRoot.GetChild(i);
            if (child != null
                && (child.name.StartsWith("PlayerSpawn", System.StringComparison.OrdinalIgnoreCase)
                    || child.name.StartsWith("InsideSpawn", System.StringComparison.OrdinalIgnoreCase)))
                DestroyObjectSafe(child.gameObject);
        }

        int currentLevel = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        int created = 0;
        for (int i = 0; i < seeds.Length; i++)
        {
            if (!LevelInteriorSpawnResolver.TryProjectToInteriorSpawnAnchor(seeds[i], out Vector3 anchor))
                continue;

            GameObject marker = new GameObject(created == 0 ? $"InsideSpawn_L{currentLevel:00}" : $"InsideSpawn_{created:00}");
            marker.transform.SetParent(spawnRoot, false);
            marker.transform.position = anchor;
            TrySetTag(marker, "InsideSpawn");
            created++;
        }

        if (created == 0)
            Debug.LogWarning("[SciFiFix] no generated indoor spawn markers could be projected; adaptive spawn fallback will scan scene floor geometry.");
        else
            Debug.Log($"[SciFiFix] indoor player spawn markers={created}");
    }

    private static void TrySetTag(GameObject obj, string tagName)
    {
        if (obj == null)
            return;

        try
        {
            obj.tag = tagName;
        }
        catch { }
    }

    private Vector3[] BuildSciFiIndoorPlayerSpawnSeeds()
    {
        Bounds bounds;
        if (_hasSciFiProxyBounds)
            bounds = _sciFiProxyBounds;
        else if (_hasAssembledWarehouseBounds)
            bounds = _assembledWarehouseBounds;
        else
            return System.Array.Empty<Vector3>();

        float y = bounds.min.y + 1f;
        Vector3 c = bounds.center;
        float x = Mathf.Clamp(bounds.extents.x * 0.22f, 4f, 12f);
        float z = Mathf.Clamp(bounds.extents.z * 0.22f, 4f, 12f);
        float x2 = Mathf.Clamp(bounds.extents.x * 0.34f, 6f, 18f);
        float z2 = Mathf.Clamp(bounds.extents.z * 0.34f, 6f, 18f);

        return new[]
        {
            new Vector3(c.x, y, c.z),
            new Vector3(c.x, y, c.z - z),
            new Vector3(c.x, y, c.z + z),
            new Vector3(c.x - x, y, c.z),
            new Vector3(c.x + x, y, c.z),
            new Vector3(c.x - x, y, c.z - z),
            new Vector3(c.x + x, y, c.z - z),
            new Vector3(c.x - x, y, c.z + z),
            new Vector3(c.x + x, y, c.z + z),
            new Vector3(c.x - x2, y, c.z),
            new Vector3(c.x + x2, y, c.z),
            new Vector3(c.x, y, c.z - z2),
            new Vector3(c.x, y, c.z + z2),
            new Vector3(c.x, y + 3f, c.z),
            new Vector3(c.x, y + 3f, c.z - z)
        };
    }

    private bool WakeAndVerifyEnvironmentColliders(Transform root)
    {
        if (root == null) return false;

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
            if (transforms[i] != null)
                transforms[i].gameObject.SetActive(true);

        if (useSciFiArena)
        {
            int disabledDecorative = DisableSciFiDecorativeColliders(root);
            Debug.Log($"[SciFiFix] disabled decorative colliders count={disabledDecorative}");
            EnsureRuntimeWarehouseColliders(root);
            int disabledOversized = DisableOversizedSciFiBlockerColliders(root);
            Debug.Log($"[SciFiFix] disabled oversized blocker colliders count={disabledOversized}");
            EnsureTraversalNavMeshLinks(root);
            EnsureSciFiDoorSystems(root);
        }

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        int activeColliders = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null) continue;
            if (col.GetComponentInParent<IDamageable>() != null) continue;
            if (useSciFiArena && IsDecorativeColliderModule(col.transform))
            {
                col.enabled = false;
                continue;
            }
            if (useSciFiArena && IsOversizedSciFiBlockerCollider(col))
            {
                col.enabled = false;
                continue;
            }
            col.gameObject.SetActive(true);
            col.enabled = true;
            if (IsRuntimeColliderRequiredModule(col.transform))
                col.isTrigger = false;
            MeshCollider meshCollider = col as MeshCollider;
            if (meshCollider != null && meshCollider.sharedMesh != null)
                Physics.BakeMesh(meshCollider.sharedMesh.GetInstanceID(), false);
            if (!col.isTrigger)
                activeColliders++;
        }

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        int required = 0;
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null) continue;
            if (!IsRuntimeColliderRequiredModule(mf.transform)) continue;
            required++;
            if (!HasActiveMeshOrBoxCollider(mf.gameObject))
            {
                Debug.LogError("[LevelBuilder] Required environment collider missing on " + GetHierarchyPath(mf.transform));
                Physics.SyncTransforms();
                return false;
            }
        }

        Physics.SyncTransforms();
        return activeColliders > 0 && (required > 0 || !useSciFiArena);
    }

    private static void EnsureRuntimeWarehouseColliders(Transform root)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null) continue;
            if (IsLocomotionTraversalModule(mf.transform))
            {
                EnsureConvexMeshCollider(mf);
                continue;
            }
            if (!IsRuntimeColliderRequiredModule(mf.transform)) continue;
            EnsureMeshOrBoxCollider(mf);
        }
    }

    private static int DisableSciFiDecorativeColliders(Transform root)
    {
        if (root == null) return 0;
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        int disabled = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger || !collider.enabled)
                continue;
            if (!IsDecorativeColliderModule(collider.transform))
                continue;
            collider.enabled = false;
            disabled++;
        }
        return disabled;
    }

    private static int DisableOversizedSciFiBlockerColliders(Transform root)
    {
        if (root == null) return 0;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        int disabled = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger || !collider.enabled)
                continue;
            if (!IsOversizedSciFiBlockerCollider(collider))
                continue;

            collider.enabled = false;
            disabled++;
        }

        return disabled;
    }

    private static void EnsureSciFiDoorSystems(Transform root)
    {
        if (root == null) return;
        SciFiSlidingDoor[] doors = root.GetComponentsInChildren<SciFiSlidingDoor>(true);
        Debug.Log($"[SciFiDoor] EnsureSciFiDoorSystems found {doors.Length} doors under {root.name}");
        for (int i = 0; i < doors.Length; i++)
        {
            SciFiSlidingDoor door = doors[i];
            if (door == null) continue;
            door.interactiveToggle = false;
            door.gameObject.SetActive(true);

            Rigidbody rb = door.GetComponent<Rigidbody>();
            if (rb == null)
                rb = door.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            Collider[] colliders = door.GetComponents<Collider>();
            bool hasTrigger = false;
            for (int c = 0; c < colliders.Length; c++)
            {
                if (colliders[c] == null) continue;
                colliders[c].isTrigger = true;
                colliders[c].enabled = true;
                hasTrigger = true;
            }
            if (!hasTrigger)
            {
                SphereCollider trigger = door.gameObject.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = door.interactRange;
            }
            Debug.Log($"[SciFiDoor] door={door.name} hasTrigger={hasTrigger} rb={rb != null} pos={door.transform.position}");
        }
    }

    private static void EnsureTraversalNavMeshLinks(Transform root)
    {
        if (root == null) return;
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null) continue;
            if (!IsStairOrRampModule(mf.transform)) continue;

            NavMeshLink link = mf.GetComponentInChildren<NavMeshLink>(true);
            if (link == null)
            {
                GameObject linkObject = new GameObject("SciFiTraversalNavMeshLink");
                linkObject.transform.SetParent(mf.transform, false);
                link = linkObject.AddComponent<NavMeshLink>();
            }

            Bounds b = mf.GetComponent<Renderer>() != null ? mf.GetComponent<Renderer>().bounds : new Bounds(mf.transform.position, mf.sharedMesh.bounds.size);
            Vector3 axis = b.size.x >= b.size.z ? Vector3.right : Vector3.forward;
            Vector3 start = b.center - axis * Mathf.Max(0.5f, Mathf.Max(b.size.x, b.size.z) * 0.45f);
            Vector3 end = b.center + axis * Mathf.Max(0.5f, Mathf.Max(b.size.x, b.size.z) * 0.45f);
            start.y = b.min.y + 0.08f;
            end.y = b.max.y + 0.08f;

            link.startPoint = link.transform.InverseTransformPoint(start);
            link.endPoint = link.transform.InverseTransformPoint(end);
            link.width = Mathf.Max(0.8f, Mathf.Min(b.size.x, b.size.z) * 0.7f);
            link.bidirectional = true;
            link.area = 0;
        }
    }

    private static void EnsureConvexMeshCollider(MeshFilter mf)
    {
        if (mf == null || mf.sharedMesh == null) return;

        MeshCollider[] meshColliders = mf.GetComponents<MeshCollider>();
        MeshCollider target = null;
        for (int i = 0; i < meshColliders.Length; i++)
        {
            MeshCollider collider = meshColliders[i];
            if (collider == null) continue;
            if (target == null)
                target = collider;
            collider.sharedMesh = mf.sharedMesh;
            collider.convex = true;
            collider.enabled = true;
            collider.isTrigger = false;
        }

        if (target == null)
        {
            target = mf.gameObject.AddComponent<MeshCollider>();
            target.sharedMesh = mf.sharedMesh;
            target.convex = true;
            target.enabled = true;
            target.isTrigger = false;
        }

        Physics.BakeMesh(mf.sharedMesh.GetInstanceID(), true);
    }

    private static void EnsureMeshOrBoxCollider(MeshFilter mf)
    {
        if (mf == null || mf.sharedMesh == null) return;

        Collider[] colliders = mf.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null) continue;
            MeshCollider meshCollider = col as MeshCollider;
            if (meshCollider != null)
            {
                if (meshCollider.sharedMesh == null)
                    meshCollider.sharedMesh = mf.sharedMesh;
                meshCollider.convex = false;
                meshCollider.enabled = true;
                meshCollider.isTrigger = false;
                return;
            }
            BoxCollider boxCollider = col as BoxCollider;
            if (boxCollider != null)
            {
                if (boxCollider.size == Vector3.zero)
                    boxCollider.size = mf.sharedMesh.bounds.size;
                boxCollider.enabled = true;
                boxCollider.isTrigger = false;
                return;
            }
        }

        if (mf.sharedMesh.isReadable)
        {
            MeshCollider meshCollider = mf.gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = mf.sharedMesh;
            meshCollider.convex = false;
            meshCollider.enabled = true;
            meshCollider.isTrigger = false;
        }
        else
        {
            BoxCollider boxCollider = mf.gameObject.AddComponent<BoxCollider>();
            boxCollider.center = mf.sharedMesh.bounds.center;
            boxCollider.size = mf.sharedMesh.bounds.size;
            boxCollider.enabled = true;
            boxCollider.isTrigger = false;
        }
    }

    private static bool HasActiveMeshOrBoxCollider(GameObject go)
    {
        if (go == null || !go.activeInHierarchy) return false;
        Collider[] colliders = go.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || !col.enabled || col.isTrigger) continue;
            MeshCollider meshCollider = col as MeshCollider;
            if (meshCollider != null && meshCollider.sharedMesh != null) return true;
            if (col is BoxCollider) return true;
        }
        return false;
    }

    private static bool IsRuntimeColliderRequiredModule(Transform transform)
    {
        if (IsDecorativeColliderModule(transform))
            return false;

        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("light") || n.Contains("decal") || n.Contains("sign") || n.Contains("particle"))
                return false;
            if (n.Contains("floor") || n.Contains("catwalk") || n.Contains("wall"))
                return true;
        }
        return false;
    }

    private static bool IsLocomotionTraversalModule(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("stair") || n.Contains("step") || n.Contains("ramp")
                || n.Contains("walkway") || n.Contains("catwalk"))
                return true;
        }
        return false;
    }

    private static bool IsStairOrRampModule(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("stair") || n.Contains("step") || n.Contains("ramp"))
                return true;
        }
        return false;
    }

    private static bool IsDecorativeColliderModule(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("door") || n.Contains("trigger") || n.Contains("navmeshproxy"))
                return false;
            if (n.Contains("floor") || n.Contains("wall") || n.Contains("stair")
                || n.Contains("step") || n.Contains("ramp") || n.Contains("walkway")
                || n.Contains("catwalk") || n.Contains("platform"))
                return false;
            if (n.Contains("rail") || n.Contains("railing") || n.Contains("pipe")
                || n.Contains("cable") || n.Contains("duct") || n.Contains("vent")
                || n.Contains("light") || n.Contains("beam") || n.Contains("shelf")
                || n.Contains("crate") || n.Contains("barrel") || n.Contains("pallet")
                || n.Contains("sprinkler") || n.Contains("camera") || n.Contains("sign")
                || n.Contains("panel") || n.Contains("fuse") || n.Contains("cart")
                || n.Contains("bin") || n.Contains("ext"))
                return true;
        }
        return false;
    }

    private static bool IsOversizedSciFiBlockerCollider(Collider collider)
    {
        if (collider == null || collider.isTrigger)
            return false;
        if (IsRuntimeColliderRequiredModule(collider.transform))
            return false;
        if (IsSciFiNavMeshProxy(collider.transform))
            return false;
        if (collider.GetComponentInParent<IDamageable>() != null)
            return false;

        BoxCollider box = collider as BoxCollider;
        if (box == null)
            return false;

        Renderer[] renderers = box.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return false;

        Bounds visual = default;
        bool hasVisual = false;
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer)
                continue;
            if (!hasVisual)
            {
                visual = renderer.bounds;
                hasVisual = true;
            }
            else
            {
                visual.Encapsulate(renderer.bounds);
            }
        }

        if (!hasVisual)
            return false;

        Bounds colliderBounds = box.bounds;
        bool tooWide = colliderBounds.size.x > visual.size.x + 1.25f
            || colliderBounds.size.z > visual.size.z + 1.25f;
        bool broadParent = box.transform.childCount > 0
            && Mathf.Max(colliderBounds.size.x, colliderBounds.size.z) > 2.5f
            && (box.transform.GetComponent<MeshFilter>() == null || box.transform.GetComponent<Renderer>() == null);
        return tooWide || broadParent;
    }

    private static bool IsSciFiNavMeshProxy(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            if (t.name.ToLowerInvariant().Contains("navmeshproxy"))
                return true;
        }
        return false;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        if (transform == null) return string.Empty;
        string path = transform.name;
        for (Transform p = transform.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    private static void SnapWarehouseModulesToGrid(Transform root)
    {
        const float GridUnit = 1f;
        if (root == null) return;
        Transform modules = root.Find("DemoWarehouseModules") ?? root;
        for (int i = 0; i < modules.childCount; i++)
        {
            Transform module = modules.GetChild(i);
            if (module == null || module.GetComponentInParent<IDamageable>() != null) continue;
            Vector3 p = module.localPosition;
            p.x = Mathf.Round(p.x / GridUnit) * GridUnit;
            p.y = Mathf.Round(p.y / GridUnit) * GridUnit;
            p.z = Mathf.Round(p.z / GridUnit) * GridUnit;
            module.localPosition = p;
            Vector3 e = module.localEulerAngles;
            e.x = Mathf.Round(e.x / 90f) * 90f;
            e.y = Mathf.Round(e.y / 90f) * 90f;
            e.z = Mathf.Round(e.z / 90f) * 90f;
            module.localEulerAngles = e;
        }
    }

    private static void BrandWarehouseStaticAndGI(Transform root)
    {
        if (root == null) return;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;
            if (renderers[i].GetComponentInParent<IDamageable>() != null) continue;
            renderers[i].gameObject.isStatic = true;
            renderers[i].staticShadowCaster = true;
#if UNITY_EDITOR
            var current = UnityEditor.GameObjectUtility.GetStaticEditorFlags(renderers[i].gameObject);
            UnityEditor.GameObjectUtility.SetStaticEditorFlags(
                renderers[i].gameObject,
                current
                | UnityEditor.StaticEditorFlags.ContributeGI
                | UnityEditor.StaticEditorFlags.BatchingStatic);
#endif
        }
    }

    private static void AnchorWarehouseLightFixtures(Transform root)
    {
        if (root == null) return;
        Light[] lights = root.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
        {
            Light l = lights[i];
            if (l == null) continue;
            l.gameObject.SetActive(true);
            if (l.type != LightType.Point && l.type != LightType.Spot) continue;
            l.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.75f;
            if (l.range < 8f) l.range = 8f;
        }
    }

    private static bool TryComputeColliderBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        if (root == null) return false;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || c.isTrigger || !c.enabled)
                continue;
            if (!hasBounds)
            {
                bounds = c.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(c.bounds);
            }
        }

        return hasBounds;
    }

    private static bool TryComputeWarehouseGeometryBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        if (root == null) return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rend = renderers[i];
            if (rend == null || rend is ParticleSystemRenderer || rend is TrailRenderer || rend is LineRenderer)
                continue;
            if (ShouldIgnoreWarehouseBoundsRenderer(rend))
                continue;

            if (!hasBounds)
            {
                bounds = rend.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(rend.bounds);
            }
        }

        return hasBounds;
    }

    private static bool TryComputeWarehouseWalkableBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        if (root == null) return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rend = renderers[i];
            if (rend == null || rend is ParticleSystemRenderer || rend is TrailRenderer || rend is LineRenderer)
                continue;
            if (!IsSciFiWalkableSurfaceName(rend.name) && !IsLikelyWarehouseFloorRenderer(rend))
                continue;

            if (!hasBounds)
            {
                bounds = rend.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(rend.bounds);
            }
        }

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || col.isTrigger)
                continue;
            if (!IsSciFiWalkableSurfaceName(col.name) && !IsLikelyWarehouseFloorBounds(col.bounds))
                continue;

            if (!hasBounds)
            {
                bounds = col.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(col.bounds);
            }
        }

        return hasBounds;
    }

    private static bool IsLikelyWarehouseFloorRenderer(Renderer renderer)
    {
        return renderer != null && IsLikelyWarehouseFloorBounds(renderer.bounds);
    }

    private static bool IsLikelyWarehouseFloorBounds(Bounds b)
    {
        float horizontal = Mathf.Max(b.size.x, b.size.z);
        return horizontal >= 2.5f && b.size.y <= Mathf.Max(0.6f, horizontal * 0.08f);
    }

    private static bool ShouldIgnoreWarehouseBoundsRenderer(Renderer renderer)
    {
        if (renderer == null) return true;

        for (Transform t = renderer.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("spawnpoints") || n.Contains("player") || n.Contains("camera")
                || n.Contains("audio") || n.Contains("backgroundmusic") || n.Contains("dust")
                || n.Contains("particle") || n.Contains("light source"))
                return true;
        }

        string lower = renderer.name.ToLowerInvariant();
        return lower.Contains("skybox") || lower.Contains("reflection") || lower.Contains("invisible");
    }

    private static void LogWarehouseModuleSummary(Transform mapRoot)
    {
        if (mapRoot == null) return;

        Transform modules = mapRoot.Find("DemoWarehouseModules");
        if (modules == null) return;

        int directModules = modules.childCount;
        int renderers = modules.GetComponentsInChildren<Renderer>(true).Length;
        int colliders = modules.GetComponentsInChildren<Collider>(true).Length;
        Debug.Log($"[LevelBuilder] Warehouse modules reused: {directModules} demo root module(s), renderers={renderers}, colliders={colliders}. Source=Assets/SciFi Warehouse Kit/Demo/Scene/SciFi_Warehouse.unity");
    }

    private static void RemoveUnwantedMapProps(Transform mapRoot)
    {
        if (mapRoot == null) return;

        var toRemove = new System.Collections.Generic.List<GameObject>();
        foreach (Transform child in mapRoot.GetComponentsInChildren<Transform>(true))
        {
            if (child == null || child == mapRoot) continue;
            string n = child.name.ToLowerInvariant();

            bool redBuilding = n.Contains("redbuilding") || n.Contains("red_building") || n.Contains("red building");
            bool car = n == "car" || n.StartsWith("car_") || n.Contains(" car ");
            bool woodenBoxes = n.Contains("woodenboxes") || n.Contains("wooden_boxes") || n.Contains("wooden_box") || n.Contains("wooden boxes");

            if (redBuilding || car || woodenBoxes)
                toRemove.Add(child.gameObject);
        }

        for (int i = 0; i < toRemove.Count; i++)
        {
            if (toRemove[i] != null)
                toRemove[i].SetActive(false);
        }

        if (toRemove.Count > 0)
            Debug.Log($"[LevelBuilder] Hid {toRemove.Count} unwanted map prop(s): Car, WoodenBoxes, and RedBuilding only.");
    }

    /// <summary>Scales the map so its largest horizontal dimension equals targetSize.</summary>
    private void AutoScaleMap(GameObject mapObj, float targetSize)
    {
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasAny = false;

        foreach (Renderer rend in mapObj.GetComponentsInChildren<Renderer>(true))
        {
            if (!hasAny) { bounds = rend.bounds; hasAny = true; }
            else bounds.Encapsulate(rend.bounds);
        }

        if (!hasAny) return;

        float maxDim = Mathf.Max(bounds.size.x, bounds.size.z);
        if (maxDim < 0.01f) return;

        float scale = targetSize / maxDim;
        mapObj.transform.localScale = Vector3.one * scale;

        // Re-centre on arena floor after scaling
        Bounds newBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool reHas = false;
        foreach (Renderer rend in mapObj.GetComponentsInChildren<Renderer>(true))
        {
            if (!reHas) { newBounds = rend.bounds; reHas = true; }
            else newBounds.Encapsulate(rend.bounds);
        }
        if (reHas)
        {
            Vector3 pos = mapObj.transform.position;
            pos.x -= newBounds.center.x;
            pos.y  = -newBounds.min.y; // sit on Y=0
            pos.z -= newBounds.center.z;
            mapObj.transform.position = pos;
        }
    }

    /// <summary>Fallback: simple coloured arena when no FBX is available.</summary>
    private void CreateProceduralFallback(Transform parent, GameManager.ArenaMap map)
    {
        Color floorColor = new Color(0.18f, 0.19f, 0.22f);
        Color wallColor  = new Color(0.23f, 0.25f, 0.29f);

        CreatePrimitive(parent, "ArenaFloor", PrimitiveType.Cube,
            Vector3.zero, new Vector3(44f, 0.5f, 44f), floorColor);
        CreatePrimitive(parent, "NorthWall", PrimitiveType.Cube,
            new Vector3(0f, 2.4f,  22f), new Vector3(44f, 4.8f, 1f), wallColor);
        CreatePrimitive(parent, "SouthWall", PrimitiveType.Cube,
            new Vector3(0f, 2.4f, -22f), new Vector3(44f, 4.8f, 1f), wallColor);
        CreatePrimitive(parent, "EastWall",  PrimitiveType.Cube,
            new Vector3( 22f, 2.4f, 0f), new Vector3(1f, 4.8f, 44f), wallColor);
        CreatePrimitive(parent, "WestWall",  PrimitiveType.Cube,
            new Vector3(-22f, 2.4f, 0f), new Vector3(1f, 4.8f, 44f), wallColor);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ENEMY SPAWNING — 12 enemies plus the player
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Nine arena zones (N/S/E/W, corners, center) with anchors spread across
    /// most of the 160×160 industrial arena — not clustered near origin.
    /// </summary>
    private static EnemySpawnZone[] BuildEnemySpawnZones(out System.Collections.Generic.List<Vector3> flatAnchors)
    {
        if (Instance != null && Instance.useSciFiArena && TryBuildSciFiEnemySpawnZones(out EnemySpawnZone[] sciFiZones, out flatAnchors))
            return sciFiZones;

        const float y = 0.01f;
        float currentHalfSize = Instance != null ? Instance.arenaHalfSize : 80f;
        float arenaR = currentHalfSize * 0.82f;

        var north  = new EnemySpawnZone("North");
        var south  = new EnemySpawnZone("South");
        var east   = new EnemySpawnZone("East");
        var west   = new EnemySpawnZone("West");
        var ne     = new EnemySpawnZone("NE");
        var nw     = new EnemySpawnZone("NW");
        var se     = new EnemySpawnZone("SE");
        var sw     = new EnemySpawnZone("SW");
        var center = new EnemySpawnZone("Center");

        float[] cardinalRadii =
        {
            arenaR * 0.42f,
            arenaR * 0.62f,
            arenaR * 0.82f,
        };
        for (int i = 0; i < cardinalRadii.Length; i++)
        {
            float r = cardinalRadii[i];
            north.Anchors.Add(new Vector3(  0f, y,  r));
            south.Anchors.Add(new Vector3(  0f, y, -r));
            east .Anchors.Add(new Vector3(  r, y,  0f));
            west .Anchors.Add(new Vector3( -r, y,  0f));
            north.Anchors.Add(new Vector3( -r * 0.35f, y, r));
            north.Anchors.Add(new Vector3(  r * 0.35f, y, r));
            south.Anchors.Add(new Vector3( -r * 0.35f, y, -r));
            south.Anchors.Add(new Vector3(  r * 0.35f, y, -r));
            east .Anchors.Add(new Vector3(  r, y, -r * 0.35f));
            east .Anchors.Add(new Vector3(  r, y,  r * 0.35f));
            west .Anchors.Add(new Vector3( -r, y, -r * 0.35f));
            west .Anchors.Add(new Vector3( -r, y,  r * 0.35f));
        }

        float[] cornerRadii = { arenaR * 0.48f, arenaR * 0.68f, arenaR * 0.82f };
        for (int i = 0; i < cornerRadii.Length; i++)
        {
            float r = cornerRadii[i] * 0.7071f;
            ne.Anchors.Add(new Vector3(  r, y,  r));
            nw.Anchors.Add(new Vector3( -r, y,  r));
            se.Anchors.Add(new Vector3(  r, y, -r));
            sw.Anchors.Add(new Vector3( -r, y, -r));
        }

        float[] centerRadii = { arenaR * 0.18f, arenaR * 0.30f };
        const int centerSegments = 8;
        for (int i = 0; i < centerRadii.Length; i++)
        {
            float r = centerRadii[i];
            for (int s = 0; s < centerSegments; s++)
            {
                float ang = (s / (float)centerSegments) * Mathf.PI * 2f;
                center.Anchors.Add(new Vector3(Mathf.Cos(ang) * r, y, Mathf.Sin(ang) * r));
            }
        }

        EnemySpawnZone[] zoneArray =
        {
            north, south, east, west, ne, nw, se, sw, center
        };

        flatAnchors = new System.Collections.Generic.List<Vector3>(160);
        int maxLen = 0;
        for (int i = 0; i < zoneArray.Length; i++)
            maxLen = Mathf.Max(maxLen, zoneArray[i].Anchors.Count);
        for (int row = 0; row < maxLen; row++)
        {
            for (int z = 0; z < zoneArray.Length; z++)
            {
                if (row < zoneArray[z].Anchors.Count)
                    flatAnchors.Add(zoneArray[z].Anchors[row]);
            }
        }

#if UNITY_EDITOR
        _gizmoSpawnZones = zoneArray;
#endif
        return zoneArray;
    }

    private static bool TryBuildSciFiEnemySpawnZones(
        out EnemySpawnZone[] zones,
        out System.Collections.Generic.List<Vector3> flatAnchors)
    {
        zones = null;
        flatAnchors = new System.Collections.Generic.List<Vector3>(32);

        GameObject arena = GameObject.Find("FbxMap") ?? GameObject.Find("SciFiArena") ?? GameObject.Find("SciFiArena(Clone)");
        if (arena == null) return false;

        Transform spawnRoot = arena.transform.Find("SpawnPoints");
        if (spawnRoot == null) return false;

        var all = new System.Collections.Generic.List<Vector3>(32);
        Transform[] transforms = spawnRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform t = transforms[i];
            if (t == null || t == spawnRoot) continue;
            if (!t.name.StartsWith("EnemySpawn_", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (TryFindNearestSciFiNavMeshPoint(t.position, 30f, out Vector3 navPoint))
                all.Add(ProjectSciFiPointToSupport(navPoint));
        }

        if (all.Count == 0) return false;

        zones = new EnemySpawnZone[SpawnZoneCount];
        for (int i = 0; i < zones.Length; i++)
            zones[i] = new EnemySpawnZone("SciFi_" + i);

        for (int i = 0; i < all.Count; i++)
        {
            zones[i % zones.Length].Anchors.Add(all[i]);
            flatAnchors.Add(all[i]);
        }

#if UNITY_EDITOR
        _gizmoSpawnZones = zones;
#endif
        return true;
    }

    private static System.Collections.Generic.List<Vector3> BuildDistributedArenaAnchors()
    {
        BuildEnemySpawnZones(out System.Collections.Generic.List<Vector3> flat);
        return flat;
    }

    private static bool AllSpawnZonesAtCapacity(int[] zoneCounts)
    {
        if (zoneCounts == null || zoneCounts.Length < SpawnZoneCount)
            return false;
        for (int i = 0; i < SpawnZoneCount; i++)
        {
            if (zoneCounts[i] < MaxEnemiesPerSpawnZone)
                return false;
        }
        return true;
    }

    private static bool TrySampleNavMeshPreservingAnchor(Vector3 anchor, float maxHorizontalDrift, out Vector3 result)
    {
        result = default;
        float maxDriftSq = maxHorizontalDrift * maxHorizontalDrift;
        Vector2 anchorXZ = new Vector2(anchor.x, anchor.z);
        float[] sampleRadii = { 1.5f, 4f, 8f, 14f, 22f };

        for (int i = 0; i < sampleRadii.Length; i++)
        {
            Vector3 probe = anchor + Vector3.up * 0.5f;
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, sampleRadii[i], NavMesh.AllAreas))
                continue;

            Vector2 hitXZ = new Vector2(hit.position.x, hit.position.z);
            if ((hitXZ - anchorXZ).sqrMagnitude <= maxDriftSq)
            {
                result = hit.position;
                return true;
            }
        }

        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static bool PassesEnemySeparationSq(Vector3 candidate, System.Collections.Generic.List<Vector3> placed, float minSepSqr)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            if ((placed[i] - candidate).sqrMagnitude < minSepSqr)
                return false;
        }

        return true;
    }

    private static float DistanceToNearestPlaced(Vector3 candidate, System.Collections.Generic.List<Vector3> placed)
    {
        if (placed == null || placed.Count == 0)
            return -1f;

        float best = float.MaxValue;
        for (int i = 0; i < placed.Count; i++)
            best = Mathf.Min(best, Vector3.Distance(candidate, placed[i]));

        return best == float.MaxValue ? -1f : best;
    }

    private static bool HasPathCompleteFromPlayer(Vector3 playerNavPos, Vector3 candidateWorld, NavMeshPath path)
    {
        if (!NavMesh.SamplePosition(candidateWorld, out NavMeshHit toHit, 3f, NavMesh.AllAreas))
            return false;
        if (!NavMesh.CalculatePath(playerNavPos, toHit.position, NavMesh.AllAreas, path))
            return false;
        return path.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>
    /// Maximum Y delta (metres) above the player at which an enemy may spawn.
    /// NavMesh.SamplePosition will happily snap onto rooftops, containers, and
    /// raised walkways if the bake reached up there — producing the "enemy on
    /// the roof spinning" bug. We reject any sample whose Y is more than this
    /// above the player's NavMesh foothold.
    /// </summary>
    public const float MaxSpawnYAbovePlayer = 3.5f;

    /// <summary>True when the candidate is too far above the playable arena floor.</summary>
    private static bool IsAboveArenaFloor(Vector3 candidate, Vector3 playerNavPos)
    {
        return (candidate.y - playerNavPos.y) > MaxSpawnYAbovePlayer;
    }

    private bool TryEvaluateEnemySpawnCandidate(
        Vector3 playerNavPos,
        Vector3 cand,
        System.Collections.Generic.List<Vector3> placedSoFar,
        float minSepSqr,
        float playerMinSq,
        NavMeshPath path,
        bool requirePathFromPlayer,
        bool requirePlayerMinDistance)
    {
        if (!IsInsideAssembledWarehouseBounds(cand))
            return false;

        // Reject rooftops / containers / raised walkways — even if NavMesh said
        // they're reachable, enemies stranded up there spin and never engage.
        if (IsAboveArenaFloor(cand, playerNavPos))
        {
            if (debugEnemySpawnDistribution)
                Debug.Log($"[LevelBuilder] Rejected rooftop spawn cand={cand} dy={(cand.y - playerNavPos.y):F2}");
            return false;
        }
        if (!EnemySpawnGeometry.IsValidOutdoorSpawn(cand, playerNavPos, rejectRooftops: false))
        {
            if (debugSpawnValidation || debugEnemySpawnDistribution)
                Debug.Log($"[LevelBuilder] Rejected spawn (ground/clearance) cand={cand}");
            return false;
        }
        if (requirePlayerMinDistance && playerMinSq > 0f && (cand - playerNavPos).sqrMagnitude < playerMinSq)
            return false;
        if (!PassesEnemySeparationSq(cand, placedSoFar, minSepSqr))
            return false;
        if (requirePathFromPlayer && !HasPathCompleteFromPlayer(playerNavPos, cand, path))
            return false;
        return true;
    }

    private bool TryPickEnemySpawnInZone(
        EnemySpawnZone zone,
        int zoneIndex,
        int[] zoneCounts,
        Vector3 playerNavPos,
        System.Collections.Generic.List<Vector3> placedSoFar,
        NavMeshPath path,
        int enemyIndex,
        float minSepSqr,
        float playerMinSq,
        bool tierA,
        out Vector3 spawnWorld)
    {
        spawnWorld = default;
        bool allZonesFull = AllSpawnZonesAtCapacity(zoneCounts);
        if (!allZonesFull && zoneCounts[zoneIndex] >= MaxEnemiesPerSpawnZone)
            return false;

        bool requirePath = tierA;
        bool requirePlayerDist = true;

        for (int a = 0; a < zone.Anchors.Count; a++)
        {
            Vector3 anchor = zone.Anchors[(enemyIndex + a) % zone.Anchors.Count];
            if (Instance != null && Instance.useSciFiArena)
            {
                if (!TryFindNearestSciFiNavMeshPoint(anchor, 24f, out Vector3 navPoint))
                    continue;
                Vector3 sciFiCand = ProjectSciFiPointToSupport(navPoint);
                if (requirePlayerDist && playerMinSq > 0f && (sciFiCand - playerNavPos).sqrMagnitude < playerMinSq)
                    continue;
                if (!PassesEnemySeparationSq(sciFiCand, placedSoFar, minSepSqr))
                    continue;
                if (requirePath && !HasPathCompleteFromPlayer(playerNavPos, sciFiCand, path))
                    continue;
                spawnWorld = sciFiCand;
                zoneCounts[zoneIndex]++;
                return true;
            }

            Vector3 open = FindOpenEnemySpawnAtPreferred(anchor, enemyIndex + a);
            if (!TrySampleNavMeshPreservingAnchor(open, MaxNavSnapHorizontalDrift, out Vector3 cand))
                continue;

            if (!TryEvaluateEnemySpawnCandidate(playerNavPos, cand, placedSoFar, minSepSqr, playerMinSq,
                    path, requirePath, requirePlayerDist))
                continue;

            spawnWorld = cand;
            zoneCounts[zoneIndex]++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Zone-first spawn: each enemy tries a different arena zone before any zone
    /// gets a third occupant. Tier A enforces spacing + player distance + path.
    /// Tier B relaxes spacing to the hard floor but keeps zone caps.
    /// </summary>
    private bool TryPickEnemySpawnPosition(
        Vector3 playerNavPos,
        Vector3 rawPlayerPos,
        System.Collections.Generic.List<Vector3> placedSoFar,
        EnemySpawnZone[] zones,
        int[] zoneCounts,
        NavMeshPath path,
        int enemyIndex,
        out Vector3 spawnWorld,
        out string usedZone,
        out string spawnTier)
    {
        spawnWorld = default;
        usedZone = "Unknown";
        spawnTier = "None";

        if (zones == null || zones.Length == 0)
            return false;

        float primary = Mathf.Max(MinEnemySpawnHardFloor, minEnemySpawnSpacing);
        float primarySq = primary * primary;
        float playerMinSq = Mathf.Max(0f, minEnemyToPlayerDistance) * Mathf.Max(0f, minEnemyToPlayerDistance);
        float fallbackSq = MinEnemySpawnHardFloor * MinEnemySpawnHardFloor;

        for (int z = 0; z < zones.Length; z++)
        {
            int zoneIndex = (enemyIndex + z) % zones.Length;
            if (TryPickEnemySpawnInZone(zones[zoneIndex], zoneIndex, zoneCounts, playerNavPos, placedSoFar,
                    path, enemyIndex, primarySq, playerMinSq, tierA: true, out spawnWorld))
            {
                usedZone = zones[zoneIndex].Name;
                spawnTier = "TierA";
                return true;
            }
        }

        for (int z = 0; z < zones.Length; z++)
        {
            int zoneIndex = (enemyIndex * 3 + z) % zones.Length;
            if (TryPickEnemySpawnInZone(zones[zoneIndex], zoneIndex, zoneCounts, playerNavPos, placedSoFar,
                    path, enemyIndex, fallbackSq, playerMinSq, tierA: false, out spawnWorld))
            {
                usedZone = zones[zoneIndex].Name;
                spawnTier = "TierB";
                return true;
            }
        }

        return false;
    }

    private bool TryEmergencyEnemySpawn(
        Vector3 playerNavPos,
        Vector3 rawPlayerPos,
        System.Collections.Generic.List<Vector3> placed,
        EnemySpawnZone[] zones,
        System.Collections.Generic.List<Vector3> flatAnchors,
        NavMeshPath path,
        int enemyIndex,
        out Vector3 spawnWorld,
        out string usedZone)
    {
        spawnWorld = default;
        usedZone = "Emergency";
        float fallbackSq = MinEnemySpawnHardFloor * MinEnemySpawnHardFloor;
        float playerMinSq = Mathf.Max(0f, minEnemyToPlayerDistance) * Mathf.Max(0f, minEnemyToPlayerDistance);
        float currentHalfSize = Instance != null ? Instance.arenaHalfSize : 80f;
        float arenaR = currentHalfSize * 0.82f;

        if (Instance != null && Instance.useSciFiArena && flatAnchors != null)
        {
            for (int i = 0; i < flatAnchors.Count; i++)
            {
                Vector3 anchor = flatAnchors[(enemyIndex + i) % flatAnchors.Count];
                if (!TryFindNearestSciFiNavMeshPoint(anchor, 30f, out Vector3 navPoint))
                    continue;
                Vector3 cand = ProjectSciFiPointToSupport(navPoint);
                if (playerMinSq > 0f && (cand - playerNavPos).sqrMagnitude < playerMinSq)
                    continue;
                if (!PassesEnemySeparationSq(cand, placed, fallbackSq))
                    continue;
                if (!HasPathCompleteFromPlayer(playerNavPos, cand, path))
                    continue;
                usedZone = "SciFiNavMesh_Emergency";
                spawnWorld = cand;
                return true;
            }
        }

        if (zones != null)
        {
            for (int attempt = 0; attempt < 120; attempt++)
            {
                int zoneIndex = (enemyIndex + attempt) % zones.Length;
                EnemySpawnZone zone = zones[zoneIndex];
                if (zone.Anchors.Count == 0) continue;

                Vector3 jitter = Random.insideUnitSphere * (4f + attempt * 0.15f);
                jitter.y = 0f;
                Vector3 seed = zone.Anchors[(enemyIndex + attempt * 3) % zone.Anchors.Count] + jitter;
                Vector3 open = FindOpenEnemySpawnAtPreferred(seed, enemyIndex + attempt);
                float drift = Mathf.Min(24f, MaxNavSnapHorizontalDrift + attempt * 0.12f);
                if (!TrySampleNavMeshPreservingAnchor(open, drift, out Vector3 cand))
                    continue;

                float distFromCenter = Mathf.Sqrt(cand.x * cand.x + cand.z * cand.z);
                if (distFromCenter > currentHalfSize * 0.95f)
                    continue;

                if (!PassesEnemySeparationSq(cand, placed, fallbackSq))
                    continue;
                if (playerMinSq > 0f && (cand - playerNavPos).sqrMagnitude < playerMinSq)
                    continue;
                if (!EnemySpawnGeometry.IsValidOutdoorSpawn(cand, playerNavPos, rejectRooftops: true))
                    continue;
                if (!IsInsideAssembledWarehouseBounds(cand))
                    continue;
                if (!HasPathCompleteFromPlayer(playerNavPos, cand, path))
                    continue;

                usedZone = zone.Name + "_Emergency";
                spawnWorld = cand;
                return true;
            }
        }

        for (int attempt = 0; attempt < 80; attempt++)
        {
            float ang = Random.Range(0f, Mathf.PI * 2f);
            float rad = Random.Range(arenaR * 0.35f, arenaR * 0.95f);
            Vector3 probe = new Vector3(Mathf.Cos(ang) * rad, 0.01f, Mathf.Sin(ang) * rad);
            if (!TrySampleNavMeshPreservingAnchor(probe, MaxNavSnapHorizontalDrift, out Vector3 cand))
                continue;

            float distFromCenter = Mathf.Sqrt(cand.x * cand.x + cand.z * cand.z);
            if (distFromCenter > currentHalfSize * 0.95f)
                continue;

            if (!PassesEnemySeparationSq(cand, placed, fallbackSq))
                continue;
            if (playerMinSq > 0f && (cand - playerNavPos).sqrMagnitude < playerMinSq)
                continue;
            if (!EnemySpawnGeometry.IsValidOutdoorSpawn(cand, playerNavPos, rejectRooftops: true))
                continue;
            if (!IsInsideAssembledWarehouseBounds(cand))
                continue;
            if (!HasPathCompleteFromPlayer(playerNavPos, cand, path))
                continue;

            usedZone = "ArenaRing_Emergency";
            spawnWorld = cand;
            return true;
        }

        if (flatAnchors != null && flatAnchors.Count > 0)
        {
            for (int ai = 0; ai < flatAnchors.Count; ai++)
            {
                Vector3 seed = flatAnchors[(enemyIndex + ai) % flatAnchors.Count];
                if (!TrySampleNavMeshPreservingAnchor(seed, MaxNavSnapHorizontalDrift * 1.5f, out Vector3 cand))
                    continue;
                if (!PassesEnemySeparationSq(cand, placed, fallbackSq))
                    continue;
                if (!EnemySpawnGeometry.IsValidOutdoorSpawn(cand, playerNavPos, rejectRooftops: true))
                    continue;
                if (!IsInsideAssembledWarehouseBounds(cand))
                    continue;

                usedZone = "AnchorSnap_Emergency";
                Debug.LogWarning(
                    $"[LevelBuilder] Enemy spawn emergency retry accepted index={enemyIndex} pos={cand}",
                    this);
                spawnWorld = cand;
                return true;
            }
        }

        if (flatAnchors != null && flatAnchors.Count > 0)
        {
            Vector3 anchor = flatAnchors[enemyIndex % flatAnchors.Count];
            if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(anchor, playerNavPos, MaxNavSnapHorizontalDrift * 2f,
                    out Vector3 validated))
            {
                if (!IsInsideAssembledWarehouseBounds(validated))
                    return false;
                usedZone = "AnchorValidated_Retry";
                spawnWorld = validated;
                return true;
            }
            else if (TrySampleNavMeshPreservingAnchor(anchor, MaxNavSnapHorizontalDrift * 2f, out Vector3 snapped) &&
                     EnemySpawnGeometry.IsValidOutdoorSpawn(snapped, playerNavPos, rejectRooftops: true))
            {
                if (!IsInsideAssembledWarehouseBounds(snapped))
                    return false;
                usedZone = "AnchorSnap_Retry";
                spawnWorld = snapped;
                return true;
            }
        }

        Debug.LogError(
            $"[LevelBuilder] Enemy spawn failed index={enemyIndex}. No valid NavMesh point found; enemy will not be spawned.",
            this);
        return false;
    }

    private void SpawnEnemies(Transform enemyRoot)
    {
        int   enemyCount  = GameManager.Instance != null ? GameManager.Instance.GetEnemyCount() : 12;
        float enemyDamage = GameManager.Instance != null ? GameManager.Instance.GetEnemyDamage() : 10f;
        int   currentLvl  = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;

        if (Application.isPlaying && !_navMeshReady)
        {
            Debug.LogError("[LevelBuilder] Enemy spawning skipped because runtime NavMesh is not ready. No origin fallback will be used.");
            if (GameManager.Instance != null)
                GameManager.Instance.InitializeEnemyCount(0);
            return;
        }

        PlayerController playerRef = Object.FindFirstObjectByType<PlayerController>();
        Vector3 playerPos = playerRef != null ? playerRef.transform.position : Vector3.zero;

        Vector3 playerNavPos = playerPos;
        if (NavMesh.SamplePosition(playerPos, out NavMeshHit playerSnap, 10f, NavMesh.AllAreas))
            playerNavPos = playerSnap.position;

        // Captured for the post-spawn reachability validation pass below.
        var spawnedEnemies = new System.Collections.Generic.List<GameObject>(enemyCount);

        // Try loading the Crosby enemy model
        GameObject enemyPrefab = Resources.Load<GameObject>("Enemy/Crosby");
        if (enemyPrefab == null)
        {
            Debug.LogError("[LevelBuilder] Enemy prefab Resources/Enemy/Crosby missing. No primitive fallback enemy will be spawned.");
            if (GameManager.Instance != null)
                GameManager.Instance.InitializeEnemyCount(0);
            return;
        }

        EnemySpawnZone[] spawnZones = BuildEnemySpawnZones(out System.Collections.Generic.List<Vector3> arenaAnchors);
        if (spawnZones == null || spawnZones.Length == 0 || arenaAnchors == null || arenaAnchors.Count == 0)
        {
            Debug.LogError("[LevelBuilder] Enemy spawning skipped: no spawn anchors were generated.");
            if (GameManager.Instance != null)
                GameManager.Instance.InitializeEnemyCount(0);
            return;
        }

        ShuffleAnchors(arenaAnchors);
        for (int zi = 0; zi < spawnZones.Length; zi++)
        {
            if (spawnZones[zi] != null)
                ShuffleAnchors(spawnZones[zi].Anchors);
        }
        ShuffleZones(spawnZones);

        int[] zoneCounts = new int[SpawnZoneCount];
        NavMeshPath spawnPath = new NavMeshPath();
        var placedPositions = new System.Collections.Generic.List<Vector3>(enemyCount);

        for (int i = 0; i < enemyCount; i++)
        {
            int startZone = i % SpawnZoneCount;
            Vector3 spawnPos = spawnZones[startZone].Anchors.Count > 0
                ? spawnZones[startZone].Anchors[0]
                : arenaAnchors[i % arenaAnchors.Count];
            GameObject enemyObject;

            // Instantiate the Crosby character model. Missing enemy art is a
            // hard content error; no primitive capsule fallback is used.
            enemyObject = Instantiate(enemyPrefab);
            enemyObject.transform.SetParent(enemyRoot, false);
            enemyObject.name = "Enemy_" + (i + 1);
            enemyObject.transform.position = spawnPos;
            NormalizeEnemyScale(enemyObject, 1.8f);

            // Assign animator controller so enemies aren't stuck in T-pose
            Animator anim = enemyObject.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                RuntimeAnimatorController animCtrl =
                    Resources.Load<RuntimeAnimatorController>("Enemy/CrosbyAnimator");
                if (animCtrl != null)
                {
                    anim.runtimeAnimatorController = animCtrl;
                }
                else
                {
                    Debug.LogWarning("[LevelBuilder] CrosbyAnimator controller not found in Resources/Enemy/");
                }
            }

            enemyObject.tag = "Enemy";
            SetLayerRecursive(enemyObject, ResolveHittableLayer());

            string spawnZoneName = "Unknown";
            string spawnTier = "Emergency";
            Vector3 agentSpawn;
            if (TryPickEnemySpawnPosition(playerNavPos, playerPos, placedPositions, spawnZones, zoneCounts,
                    spawnPath, i, out agentSpawn, out spawnZoneName, out spawnTier))
            {
                // picked
            }
            else
            {
                if (!TryEmergencyEnemySpawn(playerNavPos, playerPos, placedPositions, spawnZones,
                        arenaAnchors, spawnPath, i, out agentSpawn, out spawnZoneName))
                {
                    DestroyObjectSafe(enemyObject);
                    continue;
                }
                spawnTier = "Emergency";
            }

            enemyObject.transform.position = agentSpawn;

            // NavMeshAgent
            // IMPORTANT: add the agent disabled first, snap onto NavMesh, then enable.
            // This prevents "Failed to create agent because it is not close enough to the NavMesh".
            NavMeshAgent agent = EnsureComponent<NavMeshAgent>(enemyObject);
            if (agent.enabled) agent.enabled = false;
            // Tuned 2026-05-21: punchier closing & turning so enemies don't feel
            // laggy or stiff. EnemyController.UpdateChaseMovement may override
            // these per-frame (sprint chase, stuck recovery) but these are the
            // baseline at spawn so the very first second of contact already feels
            // aggressive instead of dragging up to chaseSpeed.
            agent.speed                  = 5.8f;   // was 5.2 — base chase
            agent.acceleration           = 20f;    // was 14 — snappier accel
            agent.angularSpeed           = 720f;   // was 540 — kills spin / hesitation
            agent.stoppingDistance       = 1.2f;   // was 1.7 — close in tighter for melee
            agent.radius                 = 0.45f;
            agent.height                 = 2f;
            agent.avoidancePriority      = 30 + (i * 3) % 40;
            agent.obstacleAvoidanceType  = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.updateRotation         = false;

            // Main collider — ensure a CapsuleCollider is present for hit detection.
            // Use WORLD-SPACE target dimensions and convert to local space so the
            // collider is always the correct size regardless of the model's scale.
            if (enemyObject.GetComponent<Collider>() == null)
            {
                CapsuleCollider cap = enemyObject.AddComponent<CapsuleCollider>();

                // Convert desired world dimensions into local space of this transform.
                float worldHeight   = 1.8f;
                float worldRadius   = 0.45f;
                float worldCenterY  = worldHeight * 0.5f;   // 0.9 m — mid-body

                Vector3 ls = enemyObject.transform.lossyScale;
                float scaleY = Mathf.Abs(ls.y) > 0.0001f ? ls.y : 1f;
                float scaleXZ = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
                if (scaleXZ < 0.0001f) scaleXZ = 1f;

                cap.height = worldHeight  / scaleY;
                cap.radius = worldRadius  / scaleXZ;
                cap.center = new Vector3(0f, worldCenterY / scaleY, 0f);
            }

            EnemyController controller = EnsureComponent<EnemyController>(enemyObject);
            controller.moveSpeed          = 3.2f;
            controller.chaseSpeed         = 5.2f;
            controller.sprintChaseSpeed   = 6.2f;
            controller.agentAcceleration  = 14f;
            controller.agentAngularSpeed  = 540f;
            controller.attackDamage       = enemyDamage;
            controller.maxHealth          = 55 + Mathf.RoundToInt((currentLvl - 1) * 5f);

            agent.speed            = controller.chaseSpeed;
            agent.stoppingDistance = Mathf.Max(0.05f, controller.meleeAttackRange * 0.08f);

            // Snap the agent onto the nearest NavMesh position before it begins moving.
            // CRITICAL: leave the agent DISABLED until PlaceAgentOnNavMesh has had a
            // chance to snap us to a valid NavMesh point. Enabling first at the raw
            // agentSpawn (which may be ~0.5m off the mesh) is what produced the
            // "Failed to create agent because it is not close enough to the NavMesh"
            // warning at level start.
            enemyObject.transform.position = agentSpawn;
            if (agent.enabled) agent.enabled = false;
            if (!PlaceAgentOnNavMesh(agent, enemyObject.transform, agentSpawn, agentSpawn, playerNavPos))
            {
                Debug.LogError($"[LevelBuilder] Enemy spawn rejected after NavMesh placement failed: {enemyObject.name} anchor={agentSpawn}");
                DestroyObjectSafe(enemyObject);
                continue;
            }
            CorrectEnemySpawnPlacement(enemyObject.transform, agent, playerNavPos);
            if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            {
                Debug.LogError($"[LevelBuilder] Enemy spawn rejected: {enemyObject.name} is not on a valid NavMesh after placement.");
                DestroyObjectSafe(enemyObject);
                continue;
            }

            Vector3 finalPos = enemyObject.transform.position;
            float distanceToNearest = DistanceToNearestPlaced(finalPos, placedPositions);
            placedPositions.Add(finalPos);

            if (debugSpawnSpacing)
            {
                Debug.Log(
                    $"[SpawnSpacing] enemy={enemyObject.name} pos={finalPos} distanceToNearest={distanceToNearest}");
            }

            if (debugEnemySpawnDistribution)
            {
                float distPlayer = HorizontalDistance(finalPos, playerPos);
                Debug.Log(
                    $"[EnemySpawn] enemy={enemyObject.name} zone={spawnZoneName} tier={spawnTier} " +
                    $"pos={finalPos} distPlayer={distPlayer:F1} distNearestEnemy=" +
                    $"{(distanceToNearest < 0f ? -1f : distanceToNearest):F1}");
            }

            // Attach the same melee weapon the player is using
            AttachWeaponToEnemy(enemyObject, currentLvl);

            // ── AI upgrade stack (order matters; later components read earlier) ──
            //   1) EnemyPersonality — derives traits from weapon category + jitter.
            //   2) EnemyTacticalBrain — target scoring, stance, group claims.
            // Tactical roll / slide / prone components are intentionally not
            // attached; those mechanics are player-only.
            EnsureComponent<EnemyPersonality>(enemyObject);
            EnsureComponent<EnemyTacticalBrain>(enemyObject);

            spawnedEnemies.Add(enemyObject);
        }

        // ── Reachability validation ─────────────────────────────────────────
        // Walk every spawned enemy and ensure NavMesh.CalculatePath from the
        // player's position completes. If the agent ended up on a disconnected
        // NavMesh island (sealed room baked separately, locked building) the
        // player can never reach it; relocate it to a candidate point that is
        // reachable, on the NavMesh, and clear of other enemies.
        ValidateEnemyReachability(spawnedEnemies, playerPos, arenaAnchors, spawnZones);

        for (int si = 0; si < spawnedEnemies.Count; si++)
        {
            GameObject spawned = spawnedEnemies[si];
            if (spawned == null)
                continue;

            NavMeshAgent spawnedAgent = spawned.GetComponent<NavMeshAgent>();
            CorrectEnemySpawnPlacement(spawned.transform, spawnedAgent, playerNavPos);
        }

        // ── Issue #5: register the authoritative count with GameManager ──────
        // InitializeEnemyCount() sets BOTH enemiesRemaining AND totalEnemiesSpawned
        // so EnemyKilled() can compare against the real number of spawned enemies.
        if (GameManager.Instance != null)
            GameManager.Instance.InitializeEnemyCount(spawnedEnemies.Count);

        Debug.Log($"[LevelBuilder] Enemy spawn summary: requested={enemyCount}, spawned={spawnedEnemies.Count}, NavMeshArea={EstimateNavMeshCoverageArea():F1}m2");
    }

    /// <summary>
    /// Verifies each spawned enemy is reachable from the player via NavMesh
    /// pathing. Enemies stuck on a disconnected NavMesh island are relocated
    /// to a reachable candidate ≥ 2 m from the player and other enemies.
    /// </summary>
    private void ValidateEnemyReachability(
        System.Collections.Generic.List<GameObject> enemies,
        Vector3 playerPos,
        System.Collections.Generic.List<Vector3> candidatePool,
        EnemySpawnZone[] zones)
    {
        if (enemies == null || enemies.Count == 0) return;

        if (!NavMesh.SamplePosition(playerPos, out NavMeshHit playerHit, 6f, NavMesh.AllAreas))
            return;
        Vector3 fromPos = playerHit.position;

        NavMeshPath path = new NavMeshPath();
        float minSeparation = Mathf.Max(MinEnemySpawnHardFloor, minEnemySpawnSpacing);
        float minSepSqr = minSeparation * minSeparation;

        var sortedCandidates = BuildReachabilityCandidatesFarthestFirst(candidatePool, zones, fromPos);

        for (int i = 0; i < enemies.Count; i++)
        {
            GameObject enemy = enemies[i];
            if (enemy == null) continue;

            Vector3 enemyPos = enemy.transform.position;
            if (IsReachable(fromPos, enemyPos, path)) continue;

            Vector3 newPos;
            if (!FindReachableRelocation(fromPos, enemy, enemies, sortedCandidates,
                    minSepSqr, path, out newPos))
            {
                if (debugEnemySpawnDistribution)
                {
                    Debug.LogWarning(
                        $"[SpawnValidation] enemy={enemy.name} unreachable — kept at {enemyPos} (no player-ring relocation)",
                        this);
                }
                continue;
            }

            NavMeshAgent agent = enemy.GetComponent<NavMeshAgent>();
            PlaceAgentOnNavMesh(agent, enemy.transform, newPos, enemyPos, fromPos);
            CorrectEnemySpawnPlacement(enemy.transform, agent, fromPos);
            if (debugEnemySpawnDistribution)
            {
                Debug.Log(
                    $"[SpawnValidation] enemy={enemy.name} reachable=false action=moved newPos={enemy.transform.position}");
            }
        }
    }

    private static System.Collections.Generic.List<Vector3> BuildReachabilityCandidatesFarthestFirst(
        System.Collections.Generic.List<Vector3> candidatePool,
        EnemySpawnZone[] zones,
        Vector3 playerNavPos)
    {
        var candidates = new System.Collections.Generic.List<Vector3>();
        if (zones != null)
        {
            for (int z = 0; z < zones.Length; z++)
                candidates.AddRange(zones[z].Anchors);
        }
        if (candidatePool != null)
        {
            for (int i = 0; i < candidatePool.Count; i++)
                candidates.Add(candidatePool[i]);
        }

        candidates.Sort((a, b) =>
        {
            float da = HorizontalDistance(a, playerNavPos);
            float db = HorizontalDistance(b, playerNavPos);
            return db.CompareTo(da);
        });
        return candidates;
    }

    private static bool IsReachable(Vector3 from, Vector3 to, NavMeshPath path)
    {
        if (!NavMesh.SamplePosition(to, out NavMeshHit toHit, 2f, NavMesh.AllAreas))
            return false;
        if (!NavMesh.CalculatePath(from, toHit.position, NavMesh.AllAreas, path))
            return false;
        return path.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>
    /// Tries every candidate spawn point + a ring of points around the player
    /// until it finds one that is on the NavMesh, has a complete path from the
    /// player, and is far enough from the player and every other enemy.
    /// </summary>
    private static bool FindReachableRelocation(
        Vector3 fromPos,
        GameObject enemy,
        System.Collections.Generic.List<GameObject> allEnemies,
        System.Collections.Generic.List<Vector3> candidates,
        float minSepSqr,
        NavMeshPath path,
        out Vector3 result)
    {
        if (candidates == null || candidates.Count == 0)
        {
            result = Vector3.zero;
            return false;
        }

        for (int c = 0; c < candidates.Count; c++)
        {
            if (!TrySampleNavMeshPreservingAnchor(candidates[c], MaxNavSnapHorizontalDrift * 1.25f, out Vector3 p))
                continue;

            if ((p - fromPos).sqrMagnitude < minSepSqr) continue;

            bool tooCloseToOther = false;
            for (int j = 0; j < allEnemies.Count; j++)
            {
                GameObject other = allEnemies[j];
                if (other == null || other == enemy) continue;
                Vector3 d = other.transform.position - p;
                d.y = 0f;
                if (d.sqrMagnitude < minSepSqr)
                { tooCloseToOther = true; break; }
            }
            if (tooCloseToOther) continue;

            if (!NavMesh.CalculatePath(fromPos, p, NavMesh.AllAreas, path)) continue;
            if (path.status != NavMeshPathStatus.PathComplete) continue;
            if (!EnemySpawnGeometry.IsValidOutdoorSpawn(p, fromPos, rejectRooftops: true))
                continue;

            result = p;
            return true;
        }

        result = Vector3.zero;
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ENEMY WEAPON ATTACHMENT
    // ════════════════════════════════════════════════════════════════════════

    private void AttachWeaponToEnemy(GameObject enemy, int level)
    {
        WeaponLoadout loadout = WeaponLoadoutCatalog.Get(level);
        float targetSize = loadout.TargetSize;
        GameObject weaponPrefab = loadout.LoadPrefab();
        if (weaponPrefab == null)
            weaponPrefab = WeaponLoadoutCatalog.LoadPrefabWithFallback(level, out targetSize);
        if (weaponPrefab == null)
        {
            Debug.LogWarning($"[LevelBuilder] All weapon sources exhausted for level {level}.");
            return;
        }

        EnemyController controller = enemy.GetComponent<EnemyController>();
        if (controller == null)
        {
            Debug.LogWarning("[LevelBuilder] EnemyController missing; cannot attach enemy weapon.");
            return;
        }

        // Prefer the rig-authored weapon socket so Crosby presents the melee
        // weapon with the same underhand grip silhouette as the player body.
        Transform handBone = FindRightHandBone(enemy.transform);
        if (handBone != null)
            controller.weaponAttachPoint = handBone;

        // Grip pose, socket euler, and stabilisation are now resolved inside
        // AttachWeaponToHand from the catalog — no pre-fill required here.
        controller.AttachWeaponToHand(weaponPrefab, targetSize, level);

        if (controller.equippedWeaponObject != null)
            SetLayerRecursive(controller.equippedWeaponObject, enemy.layer);

        // Surgical port from backup: verify the enemy weapon is actually
        // attached + visible, retry once with the same prefab if not.
        RestoreEnemyWeaponPresence(controller, weaponPrefab, targetSize, level);
    }

    private static void RestorePlayerWeaponPresence(PlayerController player)
    {
        if (player == null)
            return;

        // Mirrors how PlayerController itself chooses a level on Start: MP
        // reads MpRoomConfig, SP uses GameManager.currentLevel. Calling
        // GetEquippedWeaponLevel() returns either the live equipped level
        // (after Start has run) or the canonical SP level — never overriding
        // a multiplayer-assigned level.
        int level = player.GetEquippedWeaponLevel();
        if (level <= 0) level = 1;

        // Force a fresh attach so a stale/destroyed equippedWeaponObject from
        // before the rebuild is never reused. ForceReattachWeapon destroys
        // any previous instance, resets the cache, and re-runs the full
        // backup equip path through EquipWeaponForLevel.
        player.ForceReattachWeapon(level);

        GameObject weapon = player.equippedWeaponObject;
        bool attached = WeaponPresenceIsValid(weapon, out string prefabName, out string socketName, out int rendererCount);

        Debug.Log("[WeaponRestore] backup logic applied");
        Debug.Log($"[WeaponRestore] socket={socketName}");
        Debug.Log($"[WeaponRestore] prefab={prefabName}");
        Debug.Log($"[WeaponRestore] renderer count={rendererCount}");
        Debug.Log($"[WeaponRestore] player weapon attached={attached}");
    }

    private static void RestoreEnemyWeaponPresence(EnemyController controller, GameObject weaponPrefab, float targetSize, int level)
    {
        if (controller == null)
            return;

        GameObject weapon = controller.equippedWeaponObject;
        bool attached = WeaponPresenceIsValid(weapon, out string prefabName, out string socketName, out int rendererCount);

        // Retry the exact backup path once if the first attach left no
        // visible weapon (no GO, zero renderers, or scale collapsed to 0).
        if (!attached && weaponPrefab != null)
        {
            if (weapon != null)
            {
                DestroyObjectSafe(weapon);
                controller.equippedWeaponObject = null;
            }
            controller.AttachWeaponToHand(weaponPrefab, targetSize, level);
            if (controller.equippedWeaponObject != null)
                SetLayerRecursive(controller.equippedWeaponObject, controller.gameObject.layer);
            weapon = controller.equippedWeaponObject;
            attached = WeaponPresenceIsValid(weapon, out prefabName, out socketName, out rendererCount);
        }

        Debug.Log($"[WeaponRestore] socket={socketName}");
        Debug.Log($"[WeaponRestore] prefab={prefabName}");
        Debug.Log($"[WeaponRestore] renderer count={rendererCount}");
        Debug.Log($"[WeaponRestore] enemy weapon attached={attached}");
    }

    private static bool WeaponPresenceIsValid(GameObject weapon, out string prefabName, out string socketName, out int rendererCount)
    {
        prefabName = "<null>";
        socketName = "<none>";
        rendererCount = 0;

        if (weapon == null)
            return false;

        prefabName = weapon.name;
        socketName = weapon.transform.parent != null ? weapon.transform.parent.name : "<none>";

        if (!weapon.activeSelf)
            weapon.SetActive(true);

        Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
        int enabledCount = 0;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            if (!r.enabled) r.enabled = true;
            enabledCount++;
        }
        rendererCount = enabledCount;

        Vector3 ls = weapon.transform.localScale;
        if (Mathf.Approximately(ls.x, 0f) || Mathf.Approximately(ls.y, 0f) || Mathf.Approximately(ls.z, 0f))
            weapon.transform.localScale = Vector3.one;

        return enabledCount > 0;
    }

    /// <summary>
    /// Destroys obvious armature helper nodes that ship inside some weapon FBX
    /// files. We deliberately keep renderer components intact because some
    /// imported weapons use SkinnedMeshRenderer for the visible weapon mesh.
    /// </summary>
    private static void StripWeaponArmature(GameObject weapon)
    {
        // Destroy any child whose name contains arm/rig/armature keywords
        string[] poisonKeywords = { "_ARM", "_Arm", "_arm", "Armature", "armature", "_Rig", "_rig" };
        var toDestroy = new System.Collections.Generic.List<GameObject>();

        foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
        {
            if (child == weapon.transform) continue;
            string n = child.name;
            foreach (string keyword in poisonKeywords)
            {
                if (n.Contains(keyword))
                {
                    toDestroy.Add(child.gameObject);
                    Debug.Log($"[StripWeaponArmature] Removing '{n}' from weapon '{weapon.name}'");
                    break;
                }
            }
        }

        foreach (GameObject obj in toDestroy)
        {
            if (obj != null && obj != weapon)
                Object.DestroyImmediate(obj);
        }
    }

    /// <summary>
    /// Safety net: if the weapon's world-space (lossy) scale exceeds maxWorldSize
    /// in any axis, force localScale down proportionally.
    /// </summary>
    private static void ClampWeaponWorldScale(GameObject weapon, float maxWorldSize)
    {
        Vector3 lossy = weapon.transform.lossyScale;
        float maxAxis = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
        if (maxAxis > maxWorldSize && maxAxis > 0.001f)
        {
            float clampFactor = maxWorldSize / maxAxis;
            weapon.transform.localScale *= clampFactor;
            Debug.LogWarning($"[ClampWeaponWorldScale] Clamped '{weapon.name}' from lossy {lossy} (max={maxAxis:F1}) by {clampFactor:F4}");
        }
    }

    /// <summary>Searches the transform hierarchy for a right-hand bone.</summary>
    private Transform FindRightHandBone(Transform root)
    {
        string[] exactNames = {
            "weapon_bone_R",                      // dedicated weapon socket
            "bip_hand_R",                         // Crosby / BIP rig (primary)
            "Bip01 R Hand", "Bip001 R Hand",     // 3ds Max Biped
            "mixamorig:RightHand", "RightHand",  // Mixamo
            "Hand_R", "right_hand", "R_Hand",
            "HandRight", "Wrist_R",
            "jointItemR", "RIGHT_HAND_COMBAT", "RIGHT_HAND_REST"
        };

        foreach (string name in exactNames)
        {
            Transform t = FindDeepChild(root, name);
            if (t != null) return t;
        }

        // Case-insensitive fallback
        return FindDeepChildContaining(root, "righthand")
            ?? FindDeepChildContaining(root, "hand_r")
            ?? FindDeepChildContaining(root, "wrist_r")
            ?? FindDeepChildContaining(root, "r_hand");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Scales the enemy so it stands ~targetHeight world units tall.</summary>
    private static void NormalizeEnemyScale(GameObject enemy, float targetHeight)
    {
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;

        foreach (Renderer r in enemy.GetComponentsInChildren<Renderer>(true))
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!any || b.size.y < 0.01f) return;

        float scale = targetHeight / b.size.y;
        enemy.transform.localScale = Vector3.one * scale;
    }

    private static void ApplyDesiredLossyScale(Transform target, Vector3 desiredLossyScale)
    {
        if (target == null) return;

        Vector3 parentLossyScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(
            desiredLossyScale.x / Mathf.Max(Mathf.Abs(parentLossyScale.x), 0.0001f),
            desiredLossyScale.y / Mathf.Max(Mathf.Abs(parentLossyScale.y), 0.0001f),
            desiredLossyScale.z / Mathf.Max(Mathf.Abs(parentLossyScale.z), 0.0001f));
    }

    private Transform FindDeepChild(Transform root, string boneName)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.name == boneName) return child;
        return null;
    }

    private Transform FindDeepChildContaining(Transform root, string partial)
    {
        string lower = partial.ToLower();
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.name.ToLower().Contains(lower)) return child;
        return null;
    }

    /// <summary>Sets the layer on a GameObject and all of its children recursively.</summary>
    private static void SetLayerRecursive(GameObject obj, int layer)
    {
        if (obj == null || layer < 0) return;
        obj.layer = layer;
        foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = layer;
    }

    private static int ResolveHittableLayer()
    {
        int layer = LayerMask.NameToLayer("Hittable");
        if (layer < 0) layer = LayerMask.NameToLayer("Character");
        return layer;
    }

    private static void EnsureGameManager()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            CleanupDuplicateGameManagersInEditor();
            CleanupDuplicateRuntimeThirdPersonCamerasInEditor();
            return;
        }
#endif

        if (GameManager.Instance != null) return;

        GameManager existing = Object.FindFirstObjectByType<GameManager>();
        if (existing != null) return;

        GameObject managerObject = new GameObject("GameManager");
        managerObject.AddComponent<GameManager>();
    }

#if UNITY_EDITOR
    private static void CleanupDuplicateGameManagersInEditor()
    {
        if (Application.isPlaying)
            return;

        GameManager[] managers = Object.FindObjectsByType<GameManager>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.InstanceID);

        if (managers.Length <= 1)
            return;

        for (int i = managers.Length - 1; i >= 1; i--)
        {
            if (managers[i] != null)
                DestroyObjectSafe(managers[i].gameObject);
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[LevelBuilder] Removed {managers.Length - 1} duplicate GameManager object(s) from the editor scene.");
    }

    private static void CleanupDuplicateRuntimeThirdPersonCamerasInEditor()
    {
        if (Application.isPlaying)
            return;

        Camera[] cameras = Object.FindObjectsByType<Camera>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.InstanceID);

        int kept = 0;
        int removed = 0;

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera camera = cameras[i];
            if (camera == null || camera.gameObject.name != RuntimeThirdPersonCameraName)
                continue;

            if (kept == 0)
            {
                kept++;
                continue;
            }

            DestroyObjectSafe(camera.gameObject);
            removed++;
        }

        if (removed <= 0)
            return;

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[LevelBuilder] Removed {removed} duplicate RuntimeThirdPersonCamera object(s) from the editor scene.");
    }
#endif

    private static Transform GetOrCreateRoot(string objectName)
    {
        GameObject existing = GameObject.Find(objectName);
        Transform root = existing != null ? existing.transform : new GameObject(objectName).transform;
        root.SetParent(null, false);
        root.position = Vector3.zero;
        root.rotation = Quaternion.identity;
        root.localScale = Vector3.one;
        if (objectName == GameplayRootName)
            TagObjectIfDefined(root.gameObject, "LevelContent");
        return root;
    }

    private static Transform GetOrCreateChildRoot(Transform parent, string objectName)
    {
        Transform existing = parent.Find(objectName);
        Transform child = existing;
        if (child == null)
        {
            GameObject created = new GameObject(objectName);
            child = created.transform;
            child.SetParent(parent, false);
        }
        child.localPosition = Vector3.zero;
        child.localRotation = Quaternion.identity;
        child.localScale = Vector3.one;
        return child;
    }

    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
            DestroyObjectSafe(root.GetChild(i).gameObject);
    }

    private static void DestroyObjectSafe(Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }

    private void CleanupGeneratedRuntimeObjects()
    {
#if UNITY_EDITOR
        RemoveMissingScriptsInGeneratedObjects();
#endif
        UniversalCeilingSealer.CleanupLegacyRuntimePieces();

        string[] staleRootNames =
        {
            LegacyGameplayRootName,
            "FbxMap",
            "SciFiArena",
            "SciFiArena(Clone)",
            "SciFiNavMeshProxyColliders",
            "SpawnPoints",
            "NavMesh Surface",
            "ArenaVisualClosure",
            "WorldArenaStabilizer",
            "ArenaEdgeBlockers",
            "Ground_PhysicsFloor",
            "VisibleGround_Fallback",
            "UNIVERSAL_CEILING_Host",
            "UNIVERSAL_CEILING_TILES",
            "UNIVERSAL_CEILING_LIGHTS",
            "UNIVERSAL_CEILING_SUPPORTS",
            "UNIVERSAL_CEILING_DECOR",
            "UNIVERSAL_CEILING_CAP"
        };

        int removed = 0;
        GameObject[] allObjects = Object.FindObjectsByType<GameObject>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < allObjects.Length; i++)
        {
            GameObject obj = allObjects[i];
            if (obj == null || obj == gameObject || obj.transform.IsChildOf(transform))
                continue;
            if (!IsNamedGeneratedRoot(obj.name, staleRootNames))
                continue;

            DestroyObjectSafe(obj);
            removed++;
        }

        if (removed > 0)
            Debug.Log($"[LevelBuilder] Removed {removed} stale generated root object(s) before rebuild.");
        Debug.Log($"[SciFiFix] cleanup old arena objects count={removed}");
    }

    private static bool IsNamedGeneratedRoot(string objectName, string[] staleRootNames)
    {
        if (string.IsNullOrEmpty(objectName) || staleRootNames == null)
            return false;

        for (int i = 0; i < staleRootNames.Length; i++)
        {
            if (objectName == staleRootNames[i])
                return true;
        }
        return false;
    }

#if UNITY_EDITOR
    private static void RemoveMissingScriptsInGeneratedObjects()
    {
        string[] names =
        {
            GameplayRootName,
            LegacyGameplayRootName,
            ArenaRootName,
            EnemyRootName,
            "FbxMap",
            "SciFiArena",
            "SciFiArena(Clone)"
        };

        int removed = 0;
        for (int i = 0; i < names.Length; i++)
        {
            GameObject root = GameObject.Find(names[i]);
            if (root == null) continue;
            removed += RemoveMissingScriptsInHierarchy(root);
        }

        if (removed > 0)
            Debug.Log($"[LevelBuilder] Removed {removed} missing script component(s) from generated runtime objects.");
    }

    private static int RemoveMissingScriptsInHierarchy(GameObject root)
    {
        if (root == null) return 0;

        int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
        for (int i = 0; i < root.transform.childCount; i++)
            removed += RemoveMissingScriptsInHierarchy(root.transform.GetChild(i).gameObject);
        return removed;
    }
#endif

    private void ConfigurePlayer()
    {
        Transform fbxMap = GameObject.Find("FbxMap")?.transform;
        if (fbxMap != null && !useSciFiArena)
            EnemySpawnGeometry.RefreshStreetSpawnAnchors(fbxMap);

        PlayerController playerController = FindPlayerControllerAny();
        if (playerController == null)
        {
            GameObject playerPrefab = Resources.Load<GameObject>("FirstPersonMelee/Player");
            if (playerPrefab != null)
            {
                GameObject playerObject = Instantiate(playerPrefab);
                playerObject.name = "Player";
                playerController = playerObject.GetComponent<PlayerController>();
            }
        }
        if (playerController == null)
        {
            Debug.LogWarning("[LevelBuilder] PlayerController missing; scene preview continues without player controller.");
            return;
        }

        if (!playerController.gameObject.CompareTag("Player"))
            playerController.gameObject.tag = "Player";

        SetLayerRecursive(playerController.gameObject, ResolveHittableLayer());
        if (useSciFiArena)
        {
            playerController.gameObject.SetActive(false);
            ConfigureSciFiPlayerCollision(playerController);
        }

        Vector3 safeSpawn = default;
        if (useSciFiArena)
        {
            if (!LevelInteriorSpawnResolver.TryResolveInteriorSpawn(playerController, out safeSpawn))
            {
                Debug.LogWarning("[SciFiSpawn] adaptive spawn resolver did not find a projected floor; preserving current player transform.");
                safeSpawn = playerController.transform.position;
            }
        }

        if (useSciFiArena)
        {
            Debug.Log($"[SciFiSpawn] LevelBuilder: scifi spawn={safeSpawn} navMeshReady={_navMeshReady}");
            SetSciFiPlayerSpawnMarker(safeSpawn);
            LevelInteriorSpawnResolver.ApplyExternalSpawn(playerController, safeSpawn);
            playerController.transform.rotation = Quaternion.identity;
            playerController.gameObject.SetActive(true);
            _playerSpawnReady = true;
        }
        else
        {
            safeSpawn = FindValidatedPlayerSpawnPoint(SafeFallbackSpawn, playerController);
            Debug.Log($"[SciFiSpawn] LevelBuilder: outdoor spawn={safeSpawn}");
            playerController.TeleportTo(safeSpawn);
            playerController.transform.rotation = Quaternion.identity;
            playerController.SnapCapsuleToWalkableGround();
            _playerSpawnReady = true;
        }
        Physics.SyncTransforms();

        EnsureComponent<PlayerHealth>(playerController.gameObject);
        MeleeBodyTargeting.EnsureMeleeBodyCollider(playerController.transform);
        playerController.RefreshGameplayPreferences();

        // Surgical port from backup: guarantee the player has a visible
        // weapon attached to the right-hand socket after spawn / restart.
        RestorePlayerWeaponPresence(playerController);

        // ── Force the camera to snap to the new position immediately ────────
        // Without this, the camera lerps from (0,0,0) to the player over
        // several frames → "stuck at a wall" for the first second.
        Camera tpCam = playerController.ActiveCamera;
        if (tpCam != null)
        {
            CameraController camCtrl = tpCam.GetComponent<CameraController>();
            if (camCtrl != null)
            {
                camCtrl.target = playerController.transform;
                camCtrl.SnapToTarget();
            }
        }
        else
        {
            Debug.LogWarning("[LevelBuilder] Third-person camera missing after player setup; PlayerController will retry.");
        }

        Debug.Log($"[LevelBuilder] Player configured at {playerController.transform.position}");
    }

    private void VerifyPlayerInsideArena()
    {
        if (!useSciFiArena) return;
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;

        Vector3 pos = player.transform.position;
        bool invalid = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || pos.y < -1f;

        if (!invalid && _hasSciFiProxyBounds)
        {
            Bounds check = _sciFiProxyBounds;
            check.Expand(new Vector3(3f, 20f, 3f));
            invalid = !check.Contains(pos);
        }

        if (!invalid && _hasAssembledWarehouseBounds)
        {
            Bounds check = _assembledWarehouseBounds;
            check.Expand(new Vector3(3f, 20f, 3f));
            if (!check.Contains(pos))
                invalid = true;
        }

        if (!invalid) return;

        Debug.LogWarning($"[SciFiSpawn] VerifyPlayerInsideArena: player at {pos} is OUTSIDE, forcing respawn");
        if (!LevelInteriorSpawnResolver.TryResolveInteriorSpawn(player, out Vector3 fix))
            return;
        LevelInteriorSpawnResolver.ApplyExternalSpawn(player, fix);

        Camera tpCam = player.ActiveCamera;
        if (tpCam != null)
        {
            CameraController camCtrl = tpCam.GetComponent<CameraController>();
            if (camCtrl != null)
                camCtrl.SnapToTarget();
        }
    }

    private static void ConfigureSciFiPlayerCollision(PlayerController playerController)
    {
        if (playerController == null)
            return;

        CharacterController controller = playerController.GetComponent<CharacterController>();
        if (controller != null)
        {
            controller.radius = Mathf.Min(controller.radius > 0f ? controller.radius : 0.32f, 0.32f);
            controller.height = Mathf.Clamp(controller.height > 0f ? controller.height : 1.82f, 1.72f, 1.88f);
            controller.center = new Vector3(0f, controller.height * 0.5f, 0f);
            controller.skinWidth = Mathf.Clamp(controller.skinWidth, 0.035f, 0.06f);
            controller.stepOffset = Mathf.Clamp(controller.stepOffset, 0.28f, 0.42f);
            controller.slopeLimit = Mathf.Clamp(controller.slopeLimit, 45f, 55f);
        }

        CapsuleCollider[] capsules = playerController.GetComponentsInChildren<CapsuleCollider>(true);
        for (int i = 0; i < capsules.Length; i++)
        {
            CapsuleCollider capsule = capsules[i];
            if (capsule == null || capsule.isTrigger)
                continue;
            if (capsule.GetComponentInParent<PlayerHealth>() != null)
                continue;
            capsule.radius = Mathf.Min(capsule.radius, 0.32f);
            capsule.height = Mathf.Clamp(capsule.height, 1.72f, 1.88f);
            capsule.center = new Vector3(capsule.center.x, capsule.height * 0.5f, capsule.center.z);
        }

        Debug.Log("[SciFiFix] player collision capsule tuned for SciFiArena passages.");
    }

    private static void SetSciFiPlayerSpawnMarker(Vector3 position)
    {
        Transform marker = FindSciFiArenaPlayerSpawnMarker();
        if (marker != null)
            marker.position = position;
    }
    private NavMeshSurface EnsureNavMeshSurface()
    {
        if (useSciFiArena)
            return EnsureSciFiWarehouseNavMeshSurface();

        NavMeshSurface navMeshSurface = Object.FindFirstObjectByType<NavMeshSurface>();
        if (navMeshSurface == null)
        {
            GameObject surfaceObject = new GameObject("NavMesh Surface");
            Transform generatedRoot = GetOrCreateRoot(GameplayRootName);
            surfaceObject.transform.SetParent(generatedRoot, false);
            navMeshSurface = surfaceObject.AddComponent<NavMeshSurface>();
            navMeshSurface.collectObjects = CollectObjects.All;
        }
        else if (navMeshSurface.transform.parent == null)
        {
            Transform generatedRoot = GetOrCreateRoot(GameplayRootName);
            navMeshSurface.transform.SetParent(generatedRoot, true);
        }

        navMeshSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        navMeshSurface.minRegionArea = 2.5f;
        navMeshSurface.layerMask = BuildNavMeshLayerMask();
        return navMeshSurface;
    }

    private NavMeshSurface EnsureSciFiWarehouseNavMeshSurface()
    {
        Transform mapRoot = GameObject.Find("FbxMap")?.transform;
        if (mapRoot == null)
            return null;

        Transform proxyRoot = mapRoot.name == "SciFiNavMeshProxyColliders"
            ? mapRoot
            : mapRoot.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot == null)
        {
            Debug.LogError("[AINav] SciFiNavMeshProxyColliders missing from SciFiArena prefab. Rebuild with Tools > PRISM-7 > Build SciFi Arena Prefab outside Play Mode.");
            return null;
        }

        NavMeshSurface[] surfaces = Object.FindObjectsByType<NavMeshSurface>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        NavMeshSurface surface = null;
        for (int i = 0; i < surfaces.Length; i++)
        {
            NavMeshSurface candidate = surfaces[i];
            if (candidate == null) continue;

            if (candidate.transform == proxyRoot)
            {
                surface = candidate;
            }
            else
            {
                if (candidate.transform == mapRoot || candidate.transform.IsChildOf(mapRoot))
                    DestroyObjectSafe(candidate);
                else
                    DestroyObjectSafe(candidate.gameObject);
            }
        }

        if (surface == null)
            surface = proxyRoot.gameObject.AddComponent<NavMeshSurface>();

        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.minRegionArea = 0.45f;
        surface.overrideVoxelSize = true;
        surface.voxelSize = 0.08f;
        surface.overrideTileSize = true;
        surface.tileSize = 128;
        surface.layerMask = BuildNavMeshLayerMask();
        return surface;
    }

    private NavSourceDiagnostics PrepareSciFiNavMeshSources(Transform mapRoot)
    {
        NavSourceDiagnostics diagnostics = default;
        if (mapRoot == null)
        {
            diagnostics.zeroAreaReason = "missing FbxMap root";
            return diagnostics;
        }

        Transform proxyRoot = mapRoot.name == "SciFiNavMeshProxyColliders"
            ? mapRoot
            : mapRoot.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot == null)
        {
            diagnostics.zeroAreaReason = "missing SciFiNavMeshProxyColliders prefab root";
            return diagnostics;
        }

        if (TryComputeColliderBounds(proxyRoot, out _sciFiProxyBounds))
            _hasSciFiProxyBounds = true;

        CountSciFiProxySources(proxyRoot, ref diagnostics);

        if (diagnostics.sources == 0)
            diagnostics.zeroAreaReason = "no enabled collider sources under SciFiNavMeshProxyColliders";
        else if (diagnostics.walkableSources == 0)
            diagnostics.zeroAreaReason = "proxy colliders exist, but none are enabled walkable sources";
        else if (!_hasAssembledWarehouseBounds)
            diagnostics.zeroAreaReason = "warehouse bounds are invalid";
        else
            diagnostics.zeroAreaReason = "NavMesh builder returned no polygons from valid walkable collider sources";

        Debug.Log($"[AINav] SciFi prefab proxy sources: proxyColliders={diagnostics.walkableSources}");
        return diagnostics;
    }

    private static void CountSciFiProxySources(Transform proxyRoot, ref NavSourceDiagnostics diagnostics)
    {
        Collider[] colliders = proxyRoot.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger || !collider.enabled)
                continue;

            diagnostics.sources++;
            diagnostics.walkableSources++;
        }
    }

    private static LayerMask BuildNavMeshLayerMask()
    {
        int mask = Physics.DefaultRaycastLayers;
        RemoveLayer(ref mask, "Player");
        RemoveLayer(ref mask, "Enemy");
        RemoveLayer(ref mask, "Enemies");
        RemoveLayer(ref mask, "UI");
        RemoveLayer(ref mask, "Ignore Raycast");
        return mask;
    }

    private static void RemoveLayer(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
            mask &= ~(1 << layer);
    }

    private bool TryBuildNavMesh()
    {
        try
        {
            NavMeshSurface navMeshSurface = EnsureNavMeshSurface();
            if (navMeshSurface == null)
            {
                Debug.Log("[AINav] no NavMeshSurface found");
                return false;
            }

            _lastNavSourceDiagnostics = default;
            if (useSciFiArena)
                _lastNavSourceDiagnostics = PrepareSciFiNavMeshSources(navMeshSurface.transform);

            if (!useSciFiArena)
                MarkNavMeshWalkability();
            Debug.Log("[AINav] runtime NavMesh build started");
            if (useSciFiArena)
                _sciFiNavMeshDataInstance.Remove();
            navMeshSurface.BuildNavMesh();
            if (useSciFiArena)
                ClampSciFiSpawnMarkersToNavMesh(navMeshSurface.transform);
            float navArea = EstimateNavMeshCoverageArea();
            bool ready = navArea > 20f && HasAnyNavMeshNearArena();
            if (useSciFiArena && !ready)
            {
                if (BuildSciFiNavMeshFromProxyColliders(navMeshSurface.transform))
                {
                    navArea = EstimateNavMeshCoverageArea();
                    ready = navArea > 20f && HasAnyNavMeshNearArena();
                }
            }
            Debug.Log("[AINav] runtime NavMesh build completed");
            if (useSciFiArena)
            {
                if (!ready || navArea <= 0.01f)
                    _lastNavSourceDiagnostics.zeroAreaReason = DiagnoseZeroNavMeshArea(_lastNavSourceDiagnostics);
                Debug.Log($"[AINav] sources={_lastNavSourceDiagnostics.sources} walkableSources={_lastNavSourceDiagnostics.walkableSources} navMeshArea={navArea:F1} ready={ready.ToString().ToLowerInvariant()}");
                if (navArea <= 0.01f)
                    Debug.LogWarning($"[AINav] NavMesh area is 0.0: {_lastNavSourceDiagnostics.zeroAreaReason}");
            }
            Debug.Log($"[AINav] NavMesh coverage area = {navArea:F1} m2");
            Debug.Log("[AINav] NavMesh ready = " + ready.ToString().ToLowerInvariant());
            if (useSciFiArena)
                Debug.Log($"[SciFiFix] navmesh built={ready.ToString().ToLowerInvariant()} connected rooms={CountConnectedSciFiRoomProbes()}");
            if (ready)
                Debug.Log("[AINav] using baked NavMesh data");
            return ready;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelBuilder] NavMesh build failed; enemy spawning will be skipped. {e.GetType().Name}: {e.Message}");
            Debug.Log("[AINav] NavMesh ready = false");
            return false;
        }
    }

    private bool IsRuntimeNavMeshValid()
    {
        try
        {
            float navArea = EstimateNavMeshCoverageArea();
            return navArea > 20f && HasAnyNavMeshNearArena();
        }
        catch
        {
            return false;
        }
    }

    private int CountConnectedSciFiRoomProbes()
    {
        if (!useSciFiArena)
            return 0;

        Transform playerMarker = FindSciFiArenaPlayerSpawnMarker();
        Vector3 start = playerMarker != null ? playerMarker.position : Vector3.zero;
        if (!NavMesh.SamplePosition(start, out NavMeshHit startHit, 30f, NavMesh.AllAreas))
            return 0;

        Vector3[] probes =
        {
            new Vector3(0f, 1f, 0f),
            new Vector3(18f, 1f, 18f),
            new Vector3(-18f, 1f, 18f),
            new Vector3(18f, 1f, -18f),
            new Vector3(-18f, 1f, -18f)
        };

        int connected = 0;
        NavMeshPath path = new NavMeshPath();
        for (int i = 0; i < probes.Length; i++)
        {
            if (!NavMesh.SamplePosition(probes[i], out NavMeshHit probeHit, 16f, NavMesh.AllAreas))
                continue;
            if (!NavMesh.CalculatePath(startHit.position, probeHit.position, NavMesh.AllAreas, path))
                continue;
            if (path.status == NavMeshPathStatus.PathComplete)
                connected++;
        }
        return connected;
    }

    private string DiagnoseZeroNavMeshArea(NavSourceDiagnostics diagnostics)
    {
        if (!string.IsNullOrEmpty(diagnostics.zeroAreaReason)
            && diagnostics.zeroAreaReason != "NavMesh builder returned no polygons from valid walkable collider sources")
            return diagnostics.zeroAreaReason;

        if (diagnostics.walkableSources == 0)
            return "SciFiNavMeshProxyColliders contains no enabled BoxCollider sources; rebuild the prefab outside Play Mode";
        if (!_hasAssembledWarehouseBounds)
            return "warehouse bounds are invalid";
        if (_assembledWarehouseBounds.size.x <= 0.1f || _assembledWarehouseBounds.size.z <= 0.1f)
            return "warehouse bounds are too small for a NavMesh bake";

        return diagnostics.zeroAreaReason;
    }

    private static bool BuildSciFiNavMeshFromProxyColliders(Transform surfaceRoot)
    {
        Transform proxyRoot = surfaceRoot != null && surfaceRoot.name == "SciFiNavMeshProxyColliders"
            ? surfaceRoot
            : surfaceRoot?.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot == null)
            return false;

        BoxCollider[] boxes = proxyRoot.GetComponentsInChildren<BoxCollider>(true);
        var sources = new System.Collections.Generic.List<NavMeshBuildSource>(boxes.Length);
        Bounds bounds = default;
        bool hasBounds = false;

        for (int i = 0; i < boxes.Length; i++)
        {
            BoxCollider box = boxes[i];
            if (box == null || !box.enabled || box.isTrigger || !box.gameObject.activeInHierarchy)
                continue;

            Matrix4x4 matrix = box.transform.localToWorldMatrix * Matrix4x4.Translate(box.center);
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                transform = matrix,
                size = box.size,
                area = 0
            });

            if (!hasBounds)
            {
                bounds = box.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(box.bounds);
            }
        }

        if (sources.Count == 0 || !hasBounds)
            return false;

        if (Instance != null)
        {
            Instance._sciFiProxyBounds = bounds;
            Instance._hasSciFiProxyBounds = true;
        }

        bounds.Expand(new Vector3(4f, 8f, 4f));

        NavMesh.RemoveAllNavMeshData();
        _sciFiNavMeshDataInstance.Remove();
        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 0.28f;
        settings.agentHeight = 1.85f;
        settings.agentClimb = 0.42f;
        settings.agentSlope = 55f;

        NavMeshData data = NavMeshBuilder.BuildNavMeshData(
            settings,
            sources,
            bounds,
            Vector3.zero,
            Quaternion.identity);

        if (data == null)
            return false;

        _sciFiNavMeshDataInstance = NavMesh.AddNavMeshData(data);
        ClampSciFiSpawnMarkersToNavMesh(proxyRoot);
        Debug.Log($"[AINav] explicit SciFi NavMesh build sources={sources.Count} bounds={bounds}");
        return _sciFiNavMeshDataInstance.valid;
    }

    private static void ClampSciFiSpawnMarkersToNavMesh(Transform proxyRoot)
    {
        GameObject arena = GameObject.Find("FbxMap") ?? GameObject.Find("SciFiArena") ?? GameObject.Find("SciFiArena(Clone)");
        Transform spawnRoot = arena != null ? arena.transform.Find("SpawnPoints") : null;
        if (spawnRoot == null)
            return;

        Transform[] markers = spawnRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < markers.Length; i++)
        {
            Transform marker = markers[i];
            if (marker == null || marker == spawnRoot)
                continue;
            bool playerMarker = marker.name.StartsWith("PlayerSpawn", System.StringComparison.OrdinalIgnoreCase);
            if (!playerMarker && !marker.name.StartsWith("EnemySpawn_", System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryFindNearestSciFiNavMeshPoint(marker.position, playerMarker ? 12f : 24f, out Vector3 navPoint)
                && (!playerMarker || IsInsideSciFiInteriorSpawnBounds(navPoint)))
                marker.position = ProjectSciFiPointToSupport(navPoint);
        }
    }

    private static float EstimateNavMeshCoverageArea()
    {
        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.indices == null || tri.vertices.Length == 0 || tri.indices.Length < 3)
            return 0f;

        float area = 0f;
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            Vector3 a = tri.vertices[tri.indices[i]];
            Vector3 b = tri.vertices[tri.indices[i + 1]];
            Vector3 c = tri.vertices[tri.indices[i + 2]];
            area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }
        return area;
    }

    private bool HasAnyNavMeshNearArena()
    {
        if (useSciFiArena)
        {
            Transform marker = FindSciFiArenaPlayerSpawnMarker();
            if (marker != null && NavMesh.SamplePosition(marker.position, out _, 8f, NavMesh.AllAreas))
                return true;

            if (_hasAssembledWarehouseBounds)
            {
                Vector3 c = _assembledWarehouseBounds.center;
                Vector3 e = _assembledWarehouseBounds.extents * 0.55f;
                Vector3[] warehouseProbes =
                {
                    c,
                    new Vector3(c.x + e.x, c.y + 1f, c.z),
                    new Vector3(c.x - e.x, c.y + 1f, c.z),
                    new Vector3(c.x, c.y + 1f, c.z + e.z),
                    new Vector3(c.x, c.y + 1f, c.z - e.z)
                };
                for (int i = 0; i < warehouseProbes.Length; i++)
                    if (NavMesh.SamplePosition(warehouseProbes[i], out _, 10f, NavMesh.AllAreas))
                        return true;
            }
        }

        Vector3[] probes =
        {
            Vector3.zero,
            new Vector3( 6f, 1f,  0f),
            new Vector3(-6f, 1f,  0f),
            new Vector3( 0f, 1f,  6f),
            new Vector3( 0f, 1f, -6f),
            new Vector3(12f, 1f, 12f),
            new Vector3(-12f, 1f, 12f),
            new Vector3(12f, 1f, -12f),
            new Vector3(-12f, 1f, -12f)
        };

        for (int i = 0; i < probes.Length; i++)
        {
            if (NavMesh.SamplePosition(probes[i], out _, 12f, NavMesh.AllAreas))
                return true;
        }

        Transform arena = GameObject.Find("FbxMap")?.transform;
        if (arena == null) return false;

        Renderer[] renderers = arena.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || r is ParticleSystemRenderer) continue;
            if (NavMesh.SamplePosition(r.bounds.center, out _, 8f, NavMesh.AllAreas))
                return true;
        }
        return false;
    }

    private float GetNavMeshGroundReferenceY()
    {
        PlayerController pc = Object.FindFirstObjectByType<PlayerController>();
        if (pc != null)
            return pc.transform.position.y;

        return SafeFallbackSpawn.y;
    }

    private Transform FindArenaRootForNavMesh()
    {
        Transform arena = GameObject.Find(ArenaRootName)?.transform;
        if (arena == null)
            arena = GameObject.Find(GameplayRootName)?.transform;
        if (arena == null)
            arena = GameObject.Find("SciFiArena")?.transform;
        if (arena == null)
            arena = GameObject.Find("IndustrialMap_v3")?.transform;
        if (arena == null)
            arena = GameObject.Find("IndustrialMap")?.transform;
        if (arena == null)
            arena = GameObject.Find("FbxMap")?.transform;
        return arena;
    }

    private void MarkNavMeshWalkability()
    {
        float groundY = GetNavMeshGroundReferenceY();
        Transform arena = FindArenaRootForNavMesh();
        if (arena == null)
            return;

        Renderer[] renderers = arena.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || r is ParticleSystemRenderer)
                continue;

            bool walkable = ShouldIncludeInNavMesh(r, groundY);
            NavMeshModifier modifier = r.gameObject.GetComponent<NavMeshModifier>();
            if (!walkable)
            {
                if (modifier == null)
                    modifier = r.gameObject.AddComponent<NavMeshModifier>();
                modifier.ignoreFromBuild = true;
            }
            else if (modifier != null)
            {
                modifier.ignoreFromBuild = false;
            }
        }

        Collider[] colliders = arena.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || c.isTrigger || c.GetComponent<Renderer>() != null)
                continue;

            bool walkable = ShouldIncludeColliderInNavMesh(c, groundY);
            NavMeshModifier modifier = c.gameObject.GetComponent<NavMeshModifier>();
            if (!walkable)
            {
                if (modifier == null)
                    modifier = c.gameObject.AddComponent<NavMeshModifier>();
                modifier.ignoreFromBuild = true;
            }
            else if (modifier != null)
            {
                modifier.ignoreFromBuild = false;
            }
        }
    }

    private static bool ShouldIncludeInNavMesh(Renderer renderer, float groundY)
    {
        if (IsSciFiWalkableSurfaceName(renderer.gameObject.name))
            return true;

        if (ShouldExcludeFromNavMesh(renderer, groundY))
            return false;

        if (IsRoadOrGroundMesh(renderer.gameObject.name))
            return true;

        Bounds b = renderer.bounds;
        return IsLowFlatWalkSurface(b, groundY, 0.85f);
    }

    private static bool ShouldIncludeColliderInNavMesh(Collider collider, float groundY)
    {
        if (IsSciFiWalkableSurfaceName(collider.name))
            return true;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            if (IsSciFiWalkableSurfaceName(t.name))
                return true;
            if (EnemySpawnGeometry.IsProceduralSpawnSurface(t.name.ToLowerInvariant()))
                return false;
        }

        string n = collider.name.ToLowerInvariant();
        if (EnemySpawnGeometry.IsProceduralSpawnSurface(n))
            return false;
        if (IsNonWalkableColliderName(n))
            return false;

        if (IsRoadOrGroundMesh(collider.name))
            return true;

        return IsLowFlatWalkSurface(collider.bounds, groundY, 0.85f);
    }

    private static bool IsSciFiWalkableSurfaceName(string objectName)
    {
        if (Instance == null || !Instance.useSciFiArena || string.IsNullOrEmpty(objectName))
            return false;

        string n = objectName.ToLowerInvariant();
        if (n.Contains("rail") || n.Contains("railing") || n.Contains("support")
            || n.Contains("pillar") || n.Contains("beam") || n.Contains("ceiling")
            || n.Contains("wall") || n.Contains("door") || n.Contains("vent"))
            return false;

        return n.Contains("floor") || n.Contains("ground") || n.Contains("catwalk")
            || n.Contains("walkway") || n.Contains("stair") || n.Contains("step")
            || n.Contains("platform");
    }

    private static bool IsNonWalkableColliderName(string lowerName)
    {
        return lowerName.Contains("wall") || lowerName.Contains("roof") || lowerName.Contains("fence")
            || lowerName.Contains("container") || lowerName.Contains("barrel") || lowerName.Contains("pipe")
            || lowerName.Contains("ladder") || lowerName.Contains("door") || lowerName.Contains("pillar")
            || lowerName.Contains("column") || lowerName.Contains("trigger") || lowerName.Contains("player")
            || lowerName.Contains("enemy");
    }

    private static bool IsLowFlatWalkSurface(Bounds bounds, float groundY, float maxHeightAboveGround)
    {
        float horizontal = Mathf.Max(bounds.size.x, bounds.size.z);
        if (horizontal < 0.75f)
            return false;

        if (bounds.max.y > groundY + maxHeightAboveGround)
            return false;

        if (bounds.size.y > 0.45f && bounds.size.y > horizontal * 0.22f)
            return false;

        return bounds.size.y <= 0.45f || bounds.size.y <= horizontal * 0.12f;
    }

    private static bool ShouldExcludeFromNavMesh(Renderer renderer, float groundY)
    {
        for (Transform t = renderer.transform; t != null; t = t.parent)
        {
            if (EnemySpawnGeometry.IsProceduralSpawnSurface(t.name.ToLowerInvariant()))
                return true;
        }

        string n = renderer.name.ToLowerInvariant();
        if (EnemySpawnGeometry.IsProceduralSpawnSurface(n))
            return true;
        if (IsNonWalkableColliderName(n))
            return true;

        if (n.Contains("rooftop") || n.Contains("balcony") || n.Contains("catwalk")
            || n.Contains("parapet") || n.Contains("terrace") || n.Contains("railing")
            || n.Contains("antenna") || n.Contains("sign") || n.Contains("hangar")
            || n.Contains("building") || n.Contains("container") || n.Contains("cargo")
            || n.Contains("tank") || n.Contains("silo") || n.Contains("chimney")
            || n.Contains("generator") || n.Contains("dumpster") || n.Contains("crate")
            || n.Contains("barrier") || n.Contains("prop") || n.Contains("office")
            || n.Contains("interior") || n.Contains("beam") || n.Contains("stair")
            || n.Contains("step") || n.Contains("bridge") || n.Contains("overpass"))
            return true;

        Bounds b = renderer.bounds;
        float horizontal = Mathf.Max(b.size.x, b.size.z);
        if (b.size.y > 1.35f && b.size.y > horizontal * 0.55f)
            return true;

        bool flatSlab = b.size.y < 1.25f && b.size.x > 2.0f && b.size.z > 2.0f;
        if (flatSlab && b.center.y > groundY + 2.0f)
            return true;

        if (!IsRoadOrGroundMesh(renderer.name) && b.max.y > groundY + 1.35f && horizontal < 6f)
            return true;

        return false;
    }

    private void TryInitializeOptionalAISystems()
    {
        try
        {
            // Runtime combat does not depend on editor AI, Sentis, MCP, or npm-backed tooling.
            // Keep this wrapper as the isolation point so package failures cannot block play.
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelBuilder] Optional AI/Sentis setup skipped. Combat remains enabled. {e.GetType().Name}: {e.Message}");
        }
    }

    private void EnsureHud()
    {
        HUDManager hud = Object.FindFirstObjectByType<HUDManager>();
        if (hud == null)
        {
            GameObject hudObject = new GameObject("HUDManager");
            hud = hudObject.AddComponent<HUDManager>();
        }
        if (hud != null && GameManager.Instance != null)
        {
            hud.UpdateEnemyCount(GameManager.Instance.enemiesRemaining);
            hud.UpdateScore(GameManager.Instance.score);
        }

        if (!MultiplayerMode.IsMultiplayer && hud != null)
            hud.InitializeSinglePlayerGameplay();

        // MatchCommentator is attached to the DDOL GameManager in GameManager.Awake (fully programmatic VO).
    }

    private static void ResetRuntimeUiAnchors()
    {
        Canvas.ForceUpdateCanvases();
        Canvas[] canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null) continue;
            RectTransform rect = canvas.GetComponent<RectTransform>();
            if (rect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        MinimapCameraFollow minimap = Object.FindFirstObjectByType<MinimapCameraFollow>();
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (minimap != null && player != null)
            minimap.SetFullMapMode(false, player.transform);
        Canvas.ForceUpdateCanvases();
    }

    private void EnsurePauseMenu()
    {
        PauseMenuController pause = GetComponent<PauseMenuController>();
        if (pause == null)
            pause = gameObject.AddComponent<PauseMenuController>();
        pause.enabled = true;
    }

    private void EnsureMinimapCamera()
    {
        MinimapCameraFollow existing = Object.FindFirstObjectByType<MinimapCameraFollow>();
        if (existing != null)
        {
            existing.ResetArenaCache();
            return;
        }
        GameObject minimapObject = new GameObject(MinimapCameraName);
        Transform generatedRoot = GetOrCreateRoot(GameplayRootName);
        minimapObject.transform.SetParent(generatedRoot, false);
        Camera minimapCamera = minimapObject.AddComponent<Camera>();
        minimapCamera.transform.position = new Vector3(0f, 120f, 0f);
        minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        MinimapCameraFollow follow = minimapObject.AddComponent<MinimapCameraFollow>();
        follow.lockToArenaCenter = false;
        follow.height = 120f;
        follow.viewRadius = 28f;
        follow.ResetArenaCache();
    }

    private static GameObject CreatePrimitive(Transform parent, string objectName,
        PrimitiveType primitiveType, Vector3 position, Vector3 scale, Color color)
    {
        GameObject primitive = GameObject.CreatePrimitive(primitiveType);
        primitive.name = objectName;
        primitive.transform.SetParent(parent, false);
        primitive.transform.localPosition = position;
        primitive.transform.localScale    = scale;

        Renderer rend = primitive.GetComponent<Renderer>();
        if (rend != null)
        {
            Shader litShader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Standard");
            Material mat = new Material(litShader);
            mat.color = color;
            rend.material = mat;
        }
        return primitive;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ENVIRONMENT PROPS — crates, barriers, ramps, vehicles
    // ════════════════════════════════════════════════════════════════════════

    private void SpawnEnvironmentProps(Transform propRoot)
    {
        // ── Crate clusters (scattered cover) ─────────────────────────────
        Vector3[] cratePositions = new Vector3[]
        {
            new Vector3(  5f, 0.5f,   3f),
            new Vector3(  5f, 0.5f,   4.2f),
            new Vector3(  5f, 1.5f,   3.6f),   // stacked on top
            new Vector3( -6f, 0.5f,  -4f),
            new Vector3( -6f, 0.5f,  -2.8f),
            new Vector3( 12f, 0.5f,   9f),
            new Vector3( 12f, 0.5f,  10.2f),
            new Vector3( 12f, 1.5f,   9.6f),   // stacked
            new Vector3(-10f, 0.5f,  12f),
            new Vector3(  0f, 0.5f, -12f),
            new Vector3(  8f, 0.5f, -10f),
            new Vector3( -8f, 0.5f,   8f),
        };

        Color crateColor = new Color(0.55f, 0.35f, 0.15f);  // wood brown
        for (int i = 0; i < cratePositions.Length; i++)
        {
            GameObject crate = CreatePrimitive(propRoot, $"Crate_{i + 1}",
                PrimitiveType.Cube, cratePositions[i], Vector3.one, crateColor);
            crate.isStatic = true;
        }

        // ── Barrier walls (waist-high cover) ─────────────────────────────
        Vector3[] barrierPos   = { new Vector3(3f,0.6f,-6f), new Vector3(-4f,0.6f,6f), new Vector3(10f,0.6f,-2f), new Vector3(-12f,0.6f,-8f), new Vector3(7f,0.6f,14f) };
        Vector3[] barrierScale = { new Vector3(4f,1.2f,0.3f), new Vector3(5f,1.2f,0.3f), new Vector3(3f,1.2f,0.3f), new Vector3(4f,1.2f,0.3f), new Vector3(6f,1.2f,0.3f) };
        float[]   barrierYRot  = { 0f, 30f, 90f, 45f, 0f };

        Color barrierColor = new Color(0.45f, 0.45f, 0.50f);  // concrete grey
        for (int i = 0; i < barrierPos.Length; i++)
        {
            GameObject wall = CreatePrimitive(propRoot, $"Barrier_{i + 1}",
                PrimitiveType.Cube, barrierPos[i], barrierScale[i], barrierColor);
            wall.transform.rotation = Quaternion.Euler(0f, barrierYRot[i], 0f);
            wall.isStatic = true;
        }

        // ── Ramps / stairs (climbable surfaces) ──────────────────────────
        Vector3[] rampPos   = { new Vector3(-2f,0.4f,-10f), new Vector3(14f,0.4f,5f), new Vector3(-9f,0.4f,-2f) };
        Vector3[] rampScale = { new Vector3(2f,0.2f,4f), new Vector3(2f,0.2f,4f), new Vector3(2f,0.2f,4f) };
        Vector3[] rampRot   = { new Vector3(15f,0f,0f), new Vector3(15f,90f,0f), new Vector3(15f,180f,0f) };

        Color rampColor = new Color(0.40f, 0.38f, 0.35f);  // dark stone
        for (int i = 0; i < rampPos.Length; i++)
        {
            GameObject ramp = CreatePrimitive(propRoot, $"Ramp_{i + 1}",
                PrimitiveType.Cube, rampPos[i], rampScale[i], rampColor);
            ramp.transform.rotation = Quaternion.Euler(rampRot[i]);
            ramp.isStatic = true;
        }

        // ── Vehicle husks (large cover, built from grouped cubes) ────────
        SpawnVehicleHusk(propRoot, "Car_1", new Vector3( 8f, 0f, -7f),   0f);
        SpawnVehicleHusk(propRoot, "Car_2", new Vector3(-5f, 0f, 10f),  45f);
        SpawnVehicleHusk(propRoot, "Car_3", new Vector3(15f, 0f,  2f), -30f);

        // ── Barrels (cylindrical cover) ──────────────────────────────────
        Vector3[] barrelPositions = new Vector3[]
        {
            new Vector3(  2f, 0.6f,   7f),
            new Vector3(  2.8f, 0.6f, 7f),
            new Vector3( -7f, 0.6f,  -7f),
            new Vector3( 11f, 0.6f,  12f),
            new Vector3(-14f, 0.6f,   0f),
            new Vector3(  0f, 0.6f,  15f),
        };

        Color barrelColor = new Color(0.25f, 0.30f, 0.20f);  // military green
        for (int i = 0; i < barrelPositions.Length; i++)
        {
            GameObject barrel = CreatePrimitive(propRoot, $"Barrel_{i + 1}",
                PrimitiveType.Cylinder, barrelPositions[i],
                new Vector3(0.5f, 0.6f, 0.5f), barrelColor);
            barrel.isStatic = true;
        }

        Debug.Log($"[LevelBuilder] Environment props placed: " +
            $"{cratePositions.Length} crates, {barrierPos.Length} barriers, " +
            $"{rampPos.Length} ramps, 3 cars, {barrelPositions.Length} barrels");
    }

    /// <summary>
    /// Builds a simple car-shaped husk from box primitives (body + roof + 4 wheels).
    /// </summary>
    private void SpawnVehicleHusk(Transform parent, string name, Vector3 position, float yRotation)
    {
        GameObject car = new GameObject(name);
        car.transform.SetParent(parent, false);
        car.transform.position = position;
        car.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
        car.isStatic = true;

        Color bodyColor  = new Color(0.20f, 0.22f, 0.28f);  // dark steel
        Color roofColor  = new Color(0.15f, 0.15f, 0.20f);
        Color wheelColor = new Color(0.10f, 0.10f, 0.10f);

        // Car body
        CreatePrimitive(car.transform, "Body",
            PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f),
            new Vector3(3.8f, 1.0f, 1.6f), bodyColor).isStatic = true;

        // Roof / cabin
        CreatePrimitive(car.transform, "Roof",
            PrimitiveType.Cube, new Vector3(-0.2f, 1.3f, 0f),
            new Vector3(1.8f, 0.8f, 1.4f), roofColor).isStatic = true;

        // 4 wheels
        string[] wheelNames = { "WheelFL", "WheelFR", "WheelBL", "WheelBR" };
        Vector3[] wheelOffsets = {
            new Vector3( 1.2f, 0.2f,  0.8f),
            new Vector3( 1.2f, 0.2f, -0.8f),
            new Vector3(-1.2f, 0.2f,  0.8f),
            new Vector3(-1.2f, 0.2f, -0.8f),
        };
        for (int w = 0; w < 4; w++)
        {
            CreatePrimitive(car.transform, wheelNames[w],
                PrimitiveType.Cylinder, wheelOffsets[w],
                new Vector3(0.4f, 0.1f, 0.4f), wheelColor).isStatic = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  OPEN-AIR SPAWN LOGIC — avoids spawning inside buildings
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds an outdoor, NavMesh-valid spawn point. Tests many candidates
    /// across the arena and picks the first one that:
    ///   1) Has valid NavMesh below it
    ///   2) Has open sky above it (no roof/geometry)
    ///   3) Has enough horizontal clearance (no walls within 1m)
    /// </summary>
    private static Vector3 FindRandomOpenSpawnPoint(Vector3 fallback)
    {
        if (!NavMesh.SamplePosition(fallback, out _, 6f, NavMesh.AllAreas))
            return SafeFallbackSpawn;

        // Grid of candidate XZ positions spread across the industrial arena (80×80)
        Vector3[] candidates =
        {
            new Vector3(  0f, 0f,   0f),
            new Vector3(  0f, 0f, -15f),
            new Vector3(  0f, 0f,  15f),
            new Vector3(-15f, 0f,   0f),
            new Vector3( 15f, 0f,   0f),
            new Vector3(-10f, 0f, -10f),
            new Vector3( 10f, 0f, -10f),
            new Vector3(-10f, 0f,  10f),
            new Vector3( 10f, 0f,  10f),
            new Vector3(  0f, 0f, -25f),
            new Vector3(  0f, 0f,  25f),
            new Vector3(-25f, 0f,   0f),
            new Vector3( 25f, 0f,   0f),
            new Vector3(-20f, 0f, -20f),
            new Vector3( 20f, 0f, -20f),
            new Vector3(-20f, 0f,  20f),
            new Vector3( 20f, 0f,  20f),
            new Vector3(-35f, 0f,   0f),
            new Vector3( 35f, 0f,   0f),
            new Vector3(  0f, 0f, -35f),
            new Vector3(  0f, 0f,  35f),
        };
        Shuffle(candidates);

        foreach (Vector3 candidate in candidates)
        {
            if (IsOpenSpawnPoint(candidate, out Vector3 groundPos))
            {
                Debug.Log($"[LevelBuilder] Found open spawn at {groundPos} (candidate {candidate})");
                return groundPos;
            }
        }

        // Last resort — try the physics floor at origin
        NavMeshHit lastHit;
        if (NavMesh.SamplePosition(Vector3.zero, out lastHit, 5f, NavMesh.AllAreas))
            return lastHit.position + Vector3.up * 0.1f;

        return SafeFallbackSpawn;
    }

    /// <summary>
    /// Like <see cref="FindRandomOpenSpawnPoint"/> but also guarantees the player capsule
    /// is not intersecting environment colliders and is not too close to enemies/props.
    /// </summary>
    private static Vector3 FindValidatedPlayerSpawnPoint(Vector3 fallback, PlayerController playerController)
    {
        float halfSize = Instance != null ? Instance.arenaHalfSize : 80f;

        CharacterController cc = playerController != null ? playerController.GetComponent<CharacterController>() : null;
        float radius = cc != null ? Mathf.Max(0.2f, cc.radius) : 0.4f;
        float height = cc != null ? Mathf.Max(radius * 2f, cc.height) : 2.0f;
        Vector3 center = cc != null ? cc.center : new Vector3(0f, height * 0.5f, 0f);

        int hittable = ResolveHittableLayer();
        int mask = ~0;
        if (hittable >= 0) mask &= ~(1 << hittable);

        if (Instance != null && Instance.useSciFiArena)
        {
            if (LevelInteriorSpawnResolver.TryResolveInteriorSpawn(playerController, out Vector3 indoorFeet))
            {
                Debug.Log($"[LevelBuilder] SciFiArena indoor player spawn at {indoorFeet}");
                return indoorFeet;
            }

            Debug.LogWarning("[LevelBuilder] SciFiArena adaptive spawn failed; using requested fallback.");
            return fallback;
        }

        if (EnemySpawnGeometry.TryFindStreetPlayerSpawn(halfSize, fallback, out Vector3 streetFeet)
            && PassesPlayerSpawnCapsuleChecks(streetFeet, center, radius, height, mask))
        {
            Debug.Log($"[LevelBuilder] Street player spawn at {streetFeet}");
            return streetFeet + Vector3.up * 0.02f;
        }

        if (EnemySpawnGeometry.TryFindOpenPlayerSpawn(halfSize, fallback, out Vector3 openFeet)
            && PassesPlayerSpawnCapsuleChecks(openFeet, center, radius, height, mask))
        {
            Debug.Log($"[LevelBuilder] Outdoor player spawn at {openFeet}");
            return openFeet + Vector3.up * 0.02f;
        }

        // Secondary sweep around the arena rim — still outdoor-only.
        Vector3[] rimSeeds =
        {
            new Vector3( halfSize * 0.55f, 0f,  0f),
            new Vector3(-halfSize * 0.55f, 0f,  0f),
            new Vector3( 0f, 0f,  halfSize * 0.55f),
            new Vector3( 0f, 0f, -halfSize * 0.55f),
            new Vector3( halfSize * 0.4f, 0f,  halfSize * 0.4f),
            new Vector3(-halfSize * 0.4f, 0f, -halfSize * 0.4f),
            new Vector3( halfSize * 0.4f, 0f, -halfSize * 0.4f),
            new Vector3(-halfSize * 0.4f, 0f,  halfSize * 0.4f),
        };
        Shuffle(rimSeeds);

        for (int i = 0; i < rimSeeds.Length; i++)
        {
            if (!EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(
                    rimSeeds[i] + Vector3.up * 0.5f, default, halfSize * 0.35f, out Vector3 valid))
                continue;

            if (!EnemySpawnGeometry.IsValidPlayerStreetSpawn(valid))
                continue;

            if (!PassesPlayerSpawnCapsuleChecks(valid, center, radius, height, mask))
                continue;

            Debug.Log($"[LevelBuilder] Outdoor player spawn (rim) at {valid}");
            return valid + Vector3.up * 0.02f;
        }

        Debug.LogWarning("[LevelBuilder] No street spawn found; retrying open-air street sweep.");
        Vector3 lastResort = FindRandomOpenSpawnPoint(fallback);
        if (EnemySpawnGeometry.IsValidPlayerStreetSpawn(lastResort)
            && PassesPlayerSpawnCapsuleChecks(lastResort, center, radius, height, mask))
            return lastResort;

        return SafeFallbackSpawn;
    }

    /// <summary>
    /// Indoor-only player spawn finder for the enclosed SciFiArena. Tries the prefab's
    /// PlayerSpawn marker first, then a small set of fallback positions inside the hall.
    /// Bypasses street/outdoor validators that always reject sci-fi floors.
    /// </summary>
    private static bool TryFindSciFiArenaIndoorSpawn(
        Vector3 fallback,
        Vector3 capsuleCenterLocal,
        float radius,
        float height,
        int mask,
        out Vector3 feet)
    {
        feet = default;

        // 1) Use the prefab's authored PlayerSpawn marker if present.
        //    LevelBuilder renames the instantiated arena to "FbxMap"; the marker lives
        //    under FbxMap/SpawnPoints/PlayerSpawn for SciFiArena.
        Transform marker = FindSciFiArenaPlayerSpawnMarker();
        if (marker != null
            && TryIndoorNavMeshSpawn(marker.position, capsuleCenterLocal, radius, height, mask, out feet))
            return true;

        // 2) Try the requested fallback position.
        if (TryIndoorNavMeshSpawn(fallback, capsuleCenterLocal, radius, height, mask, out feet))
            return true;

        // 3) Sweep candidates generated from the final assembled warehouse bounds.
        if (Instance != null && Instance._hasAssembledWarehouseBounds)
        {
            Bounds b = Instance._assembledWarehouseBounds;
            float y = b.min.y + 1f;
            Vector3 c = b.center;
            float x = Mathf.Max(3f, b.extents.x * 0.35f);
            float z = Mathf.Max(3f, b.extents.z * 0.35f);
            Vector3[] boundedSeeds =
            {
                new Vector3(c.x, y, c.z),
                new Vector3(c.x - x, y, c.z),
                new Vector3(c.x + x, y, c.z),
                new Vector3(c.x, y, c.z - z),
                new Vector3(c.x, y, c.z + z),
                new Vector3(c.x - x, y, c.z - z),
                new Vector3(c.x + x, y, c.z - z),
                new Vector3(c.x - x, y, c.z + z),
                new Vector3(c.x + x, y, c.z + z),
            };
            for (int i = 0; i < boundedSeeds.Length; i++)
            {
                if (TryIndoorNavMeshSpawn(boundedSeeds[i], capsuleCenterLocal, radius, height, mask, out feet))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the authored PlayerSpawn marker inside the runtime SciFi arena hierarchy.
    /// The prefab is named "SciFiArena" but the instantiated copy is renamed to
    /// "FbxMap" by LoadFbxMap, so we search both.
    /// </summary>
    private static Transform FindSciFiArenaPlayerSpawnMarker()
    {
        GameObject arena = GameObject.Find("FbxMap");
        if (arena == null) arena = GameObject.Find("SciFiArena");
        if (arena == null) arena = GameObject.Find("SciFiArena(Clone)");
        if (arena == null) return null;

        int currentLevel = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        Transform insideLevel = arena.transform.Find($"SpawnPoints/InsideSpawn_L{currentLevel:00}");
        if (insideLevel != null) return insideLevel;

        Transform inside = arena.transform.Find("SpawnPoints/InsideSpawn");
        if (inside != null) return inside;

        Transform direct = arena.transform.Find("SpawnPoints/PlayerSpawn");
        if (direct != null) return direct;

        Transform[] all = arena.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].name.Equals($"InsideSpawn_L{currentLevel:00}", System.StringComparison.OrdinalIgnoreCase)) return all[i];
            if (all[i].name.Equals("InsideSpawn", System.StringComparison.OrdinalIgnoreCase)) return all[i];
            if (all[i].name == "PlayerSpawn") return all[i];
        }
        return null;
    }

    private static bool TryIndoorNavMeshSpawn(
        Vector3 seed,
        Vector3 capsuleCenterLocal,
        float radius,
        float height,
        int mask,
        out Vector3 feet)
    {
        feet = default;
        if (!TryFindNearestSciFiNavMeshPoint(seed, 80f, out Vector3 navPoint))
            return false;
        if (!IsInsideSciFiInteriorSpawnBounds(navPoint))
            return false;
        Vector3 supported = ProjectSciFiPointToSupport(navPoint);
        if (!IsCapsuleSpawnClear(supported, capsuleCenterLocal, radius, height, mask, minObstacleDistance: 0.55f))
            return false;
        feet = supported;
        return true;
    }

    private static bool TryFindNearestSciFiNavMeshPoint(Vector3 seed, float maxDistance, out Vector3 point)
    {
        point = default;

        if (NavMesh.SamplePosition(seed, out NavMeshHit hit, maxDistance, NavMesh.AllAreas)
            && IsInsideSciFiProxyBounds(hit.position))
        {
            point = hit.position;
            return true;
        }

        if (Instance == null || !Instance._hasSciFiProxyBounds)
            return false;

        Bounds b = Instance._sciFiProxyBounds;
        float y = b.min.y + 1f;
        Vector3 c = b.center;
        Vector3[] seeds =
        {
            new Vector3(seed.x, y, seed.z),
            new Vector3(c.x, y, c.z),
            new Vector3(c.x - b.extents.x * 0.35f, y, c.z),
            new Vector3(c.x + b.extents.x * 0.35f, y, c.z),
            new Vector3(c.x, y, c.z - b.extents.z * 0.35f),
            new Vector3(c.x, y, c.z + b.extents.z * 0.35f),
            new Vector3(c.x - b.extents.x * 0.25f, y, c.z - b.extents.z * 0.25f),
            new Vector3(c.x + b.extents.x * 0.25f, y, c.z - b.extents.z * 0.25f),
            new Vector3(c.x - b.extents.x * 0.25f, y, c.z + b.extents.z * 0.25f),
            new Vector3(c.x + b.extents.x * 0.25f, y, c.z + b.extents.z * 0.25f)
        };

        float best = float.PositiveInfinity;
        Vector3 bestPoint = default;
        bool found = false;
        for (int i = 0; i < seeds.Length; i++)
        {
            if (!NavMesh.SamplePosition(seeds[i], out hit, maxDistance, NavMesh.AllAreas))
                continue;
            if (!IsInsideSciFiProxyBounds(hit.position))
                continue;
            float d = (hit.position - seed).sqrMagnitude;
            if (d >= best)
                continue;
            best = d;
            bestPoint = hit.position;
            found = true;
        }

        point = bestPoint;
        return found;
    }

    private static Vector3 ProjectSciFiPointToSupport(Vector3 navPoint)
    {
        Vector3 result = navPoint;
        int mask = BuildNavMeshLayerMask();
        Vector3 start = navPoint + Vector3.up * 2f;
        if (Physics.Raycast(start, Vector3.down, out RaycastHit hit, 8f, mask, QueryTriggerInteraction.Ignore)
            && IsSciFiWalkableSpawnSupport(hit.collider, hit.point + Vector3.up * 0.05f))
        {
            result = hit.point;
        }

        result.y += 0.05f;
        return result;
    }

    private static bool IsInsideSciFiProxyBounds(Vector3 position)
    {
        if (Instance == null || !Instance.useSciFiArena || !Instance._hasSciFiProxyBounds)
            return true;

        Bounds b = Instance._sciFiProxyBounds;
        b.Expand(new Vector3(1.5f, 6f, 1.5f));
        return b.Contains(position);
    }

    private static bool IsInsideSciFiInteriorSpawnBounds(Vector3 position)
    {
        if (Instance == null || !Instance.useSciFiArena)
            return true;

        Bounds b;
        if (Instance._hasSciFiProxyBounds)
            b = Instance._sciFiProxyBounds;
        else if (Instance._hasAssembledWarehouseBounds)
            b = Instance._assembledWarehouseBounds;
        else
            return false;

        float insetX = Mathf.Min(6f, Mathf.Max(2f, b.extents.x * 0.12f));
        float insetZ = Mathf.Min(6f, Mathf.Max(2f, b.extents.z * 0.12f));
        b.min = new Vector3(b.min.x + insetX, b.min.y - 0.5f, b.min.z + insetZ);
        b.max = new Vector3(b.max.x - insetX, b.max.y + 6f, b.max.z - insetZ);
        return b.Contains(position);
    }

    private static bool IsInsideAssembledWarehouseBounds(Vector3 position)
    {
        if (Instance != null && Instance.useSciFiArena && Instance._hasSciFiProxyBounds)
            return IsInsideSciFiProxyBounds(position);

        if (Instance == null || !Instance.useSciFiArena || !Instance._hasAssembledWarehouseBounds)
            return true;

        Bounds b = Instance._assembledWarehouseBounds;
        b.Expand(new Vector3(-1.5f, 3f, -1.5f));
        return position.x >= b.min.x && position.x <= b.max.x
            && position.z >= b.min.z && position.z <= b.max.z
            && position.y >= b.min.y - 0.35f && position.y <= b.max.y + 0.5f;
    }

    private static bool TryFindBoundedWarehouseFallback(out Vector3 feet)
    {
        feet = default;
        if (Instance == null || !Instance.useSciFiArena || !Instance._hasAssembledWarehouseBounds)
            return false;

        Bounds b = Instance._assembledWarehouseBounds;
        feet = new Vector3(b.center.x, b.min.y + 0.08f, b.center.z);
        return true;
    }

    private static bool PassesPlayerSpawnCapsuleChecks(
        Vector3 feetPos,
        Vector3 capsuleCenterLocal,
        float radius,
        float height,
        int mask)
    {
        Vector3 ground = feetPos + Vector3.up * 0.02f;

        if (!IsCapsuleSpawnClear(ground, capsuleCenterLocal, radius, height, mask, minObstacleDistance: 0.75f))
            return false;

        if (!IsFarFromEnemies(ground, minEnemyDistance: 4.0f))
            return false;

        if (!IsFarFromNamedObstacles(ground, minDistance: 3.0f))
            return false;

        return true;
    }

    private static bool TryProjectToGround(Vector3 origin, out Vector3 ground, float maxDistance, int mask)
    {
        ground = origin;
        Vector3 start = origin + Vector3.up * 2.5f;
        if (Physics.Raycast(start, Vector3.down, out RaycastHit hit, maxDistance, mask, QueryTriggerInteraction.Ignore))
        {
            ground = hit.point;
            return true;
        }
        return false;
    }

    private static readonly Collider[] _spawnOverlapBuffer = new Collider[64];

    private static bool IsCapsuleSpawnClear(
        Vector3 worldPosition,
        Vector3 capsuleCenterLocal,
        float radius,
        float height,
        int mask,
        float minObstacleDistance)
    {
        // Build capsule in world space.
        float half = Mathf.Max(0f, (height * 0.5f) - radius);
        Vector3 center = worldPosition + capsuleCenterLocal;
        Vector3 p1 = center + Vector3.up * half;
        Vector3 p2 = center - Vector3.up * half;

        int hitCount = Physics.OverlapCapsuleNonAlloc(
            p1, p2,
            radius + Mathf.Max(0f, minObstacleDistance),
            _spawnOverlapBuffer,
            mask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider c = _spawnOverlapBuffer[i];
            if (c == null) continue;
            if (!c.enabled) continue;
            if (c.isTrigger) continue;
            if (Instance != null && Instance.useSciFiArena && IsSciFiWalkableSpawnSupport(c, worldPosition))
                continue;
            // Reject any solid overlap.
            return false;
        }

        return true;
    }

    private static bool IsSciFiWalkableSpawnSupport(Collider collider, Vector3 feetPosition)
    {
        if (collider == null)
            return false;

        Bounds b = collider.bounds;
        if (b.max.y > feetPosition.y + 0.25f)
            return false;

        string lower = collider.name.ToLowerInvariant();
        if (lower.Contains("navmeshproxy") || lower.Contains("floor") || lower.Contains("catwalk")
            || lower.Contains("stair") || lower.Contains("step") || lower.Contains("platform"))
            return true;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("navmeshproxycolliders") || n.Contains("floor tiles") || n.Contains("catwalk"))
                return true;
        }

        return false;
    }

    private static bool IsFarFromEnemies(Vector3 pos, float minEnemyDistance)
    {
        EnemyController[] enemies = Object.FindObjectsByType<EnemyController>(FindObjectsSortMode.None);
        float minSq = minEnemyDistance * minEnemyDistance;
        for (int i = 0; i < enemies.Length; i++)
        {
            EnemyController e = enemies[i];
            if (e == null || !e.IsAlive) continue;
            Vector3 d = e.transform.position - pos;
            d.y = 0f;
            if (d.sqrMagnitude < minSq)
                return false;
        }
        return true;
    }

    private static bool IsFarFromNamedObstacles(Vector3 pos, float minDistance)
    {
        string[] keywords = { "Tank", "WaterTank", "Container", "Wall", "Stairs", "Ramp", "Hangar", "Building", "Warehouse" };
        Collider[] all = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
        float minSq = minDistance * minDistance;

        for (int i = 0; i < all.Length; i++)
        {
            Collider c = all[i];
            if (c == null || !c.enabled || c.isTrigger) continue;

            // Skip characters (player/enemies) — handled by separate distance checks.
            if (c.GetComponentInParent<IDamageable>() != null) continue;

            string n = c.gameObject.name;
            bool match = false;
            for (int k = 0; k < keywords.Length; k++)
            {
                if (n.IndexOf(keywords[k], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = true;
                    break;
                }
            }
            if (!match) continue;

            Vector3 closest = c.ClosestPoint(pos);
            Vector3 d = closest - pos;
            d.y = 0f;
            if (d.sqrMagnitude < minSq)
                return false;
        }

        return true;
    }

    private static Vector3 FindOpenSpawnPoint(Vector3 fallback)
    {
        return FindRandomOpenSpawnPoint(fallback);
    }

    /// <summary>
    /// Tests if a candidate XZ position is valid: on NavMesh, outdoors, and clear.
    /// </summary>
    private static bool IsOpenSpawnPoint(Vector3 xzCandidate, out Vector3 groundPos)
    {
        groundPos = Vector3.zero;

        // Dynamic bounds safety: reject any candidate spawn point whose horizontal distance from center exceeds 95% of arenaHalfSize
        float currentHalfSize = Instance != null ? Instance.arenaHalfSize : 80f;
        float distFromCenter = Mathf.Sqrt(xzCandidate.x * xzCandidate.x + xzCandidate.z * xzCandidate.z);
        if (distFromCenter > currentHalfSize * 0.95f)
            return false;

        // 1) Find NavMesh surface near this XZ position
        Vector3 samplePoint = new Vector3(xzCandidate.x, 0.5f, xzCandidate.z);
        NavMeshHit hit;
        if (!NavMesh.SamplePosition(samplePoint, out hit, 3f, NavMesh.AllAreas))
            return false;

        // Reject points that are clearly on rooftops (too high above the arena floor)
        if (hit.position.y > 2.5f)
            return false;

        Vector3 feetPos = hit.position + Vector3.up * 0.1f;
        if (!EnemySpawnGeometry.IsValidOutdoorSpawn(feetPos, default, rejectRooftops: false))
            return false;

        groundPos = feetPos;
        return true;
    }

    /// <summary>
    /// Finds an open spawn point for enemies. Same logic but with a wider set
    /// of candidates around the given position.
    /// </summary>
    /// <summary>
    /// Finds an outdoor spawn near the arena anchor. Does not filter by player
    /// distance (that is enforced later in TryPickEnemySpawnPosition).
    /// </summary>
    private static Vector3 FindOpenEnemySpawnAtPreferred(Vector3 preferred, int index)
    {
        Vector3[] offsets =
        {
            Vector3.zero,
            new Vector3( 2f, 0f,  0f), new Vector3(-2f, 0f,  0f),
            new Vector3( 0f, 0f,  2f), new Vector3( 0f, 0f, -2f),
            new Vector3( 4f, 0f,  0f), new Vector3(-4f, 0f,  0f),
            new Vector3( 0f, 0f,  4f), new Vector3( 0f, 0f, -4f),
            new Vector3( 6f, 0f,  0f), new Vector3(-6f, 0f,  0f),
            new Vector3( 0f, 0f,  6f), new Vector3( 0f, 0f, -6f),
            new Vector3( 3f, 0f,  3f), new Vector3(-3f, 0f, -3f),
            new Vector3( 3f, 0f, -3f), new Vector3(-3f, 0f,  3f),
            new Vector3( 8f, 0f,  0f), new Vector3(-8f, 0f,  0f),
            new Vector3( 0f, 0f,  8f), new Vector3( 0f, 0f, -8f),
        };
        Shuffle(offsets);

        for (int i = 0; i < offsets.Length; i++)
        {
            if (IsOpenSpawnPoint(preferred + offsets[i], out Vector3 offsetPos))
                return offsetPos;
        }

        if (TrySampleNavMeshPreservingAnchor(preferred, MaxNavSnapHorizontalDrift, out Vector3 snapped))
            return snapped;

        return preferred;
    }

    // Progressive search radii — each step doubles the previous.
    // An enemy spawning inside a building or at a map edge will be pulled
    // to the nearest reachable NavMesh surface before the agent is enabled,
    // eliminating "Failed to create agent" warnings entirely.
    private static readonly float[] NavSnapRadii = { 1.5f, 4f, 10f, 25f };

    private static Vector3 ResolveAgentSpawnPosition(Vector3 preferred)
    {
        foreach (float r in NavSnapRadii)
        {
            if (NavMesh.SamplePosition(preferred, out NavMeshHit hit, r, NavMesh.AllAreas))
                return hit.position;
        }

        // Nothing found at any radius — return with a small upward offset so
        // the agent sits just above the terrain.
        return new Vector3(preferred.x, preferred.y + 0.2f, preferred.z);
    }

    private static bool PlaceAgentOnNavMesh(NavMeshAgent agent, Transform target, Vector3 spawnPosition,
        Vector3 snapAnchor = default, Vector3 playerNavPos = default)
    {
        if (target == null) return false;

        Vector3 anchor = snapAnchor == default ? spawnPosition : snapAnchor;
        if (EnemySpawnGeometry.TryPlaceAgentOnValidNavMesh(agent, target, anchor, playerNavPos))
        {
            if (!IsInsideAssembledWarehouseBounds(target.position))
            {
                if (agent != null) agent.enabled = false;
                return false;
            }
            return agent == null || (agent.enabled && agent.isOnNavMesh);
        }

        if (agent == null)
        {
            target.position = spawnPosition;
            return false;
        }

        agent.enabled = false;
        target.position = spawnPosition;

        if (TrySampleNavMeshPreservingAnchor(anchor, MaxNavSnapHorizontalDrift, out Vector3 snapped))
        {
            if (!IsInsideAssembledWarehouseBounds(snapped))
                return false;
            target.position = snapped;
            agent.enabled = true;
            if (agent.isOnNavMesh)
            {
                agent.Warp(snapped);
                return true;
            }
            agent.enabled = false;
            return false;
        }

        Debug.LogError($"[LevelBuilder] Could not snap enemy to NavMesh near {spawnPosition}. Agent left disabled and spawn rejected.");
        return false;
    }

    private static void CorrectEnemySpawnPlacement(Transform target, NavMeshAgent agent, Vector3 playerNavPos)
    {
        if (target == null)
            return;

        Vector3 pos = target.position;
        if (EnemySpawnGeometry.IsValidOutdoorSpawn(pos, playerNavPos, rejectRooftops: true))
            return;

        if (EnemySpawnGeometry.TryFindValidOutdoorSpawnNear(pos, playerNavPos, MaxNavSnapHorizontalDrift, out Vector3 corrected))
        {
            target.position = corrected;
            if (agent != null && agent.enabled && agent.isOnNavMesh)
                agent.Warp(corrected);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!debugEnemySpawnDistribution || _gizmoSpawnZones == null)
            return;

        Color[] colors =
        {
            Color.cyan, Color.green, Color.yellow, Color.magenta,
            new Color(1f, 0.5f, 0f), Color.blue, Color.red, new Color(0.5f, 1f, 0.5f),
            Color.white
        };

        for (int z = 0; z < _gizmoSpawnZones.Length; z++)
        {
            Gizmos.color = colors[z % colors.Length];
            EnemySpawnZone zone = _gizmoSpawnZones[z];
            for (int a = 0; a < zone.Anchors.Count; a++)
            {
                Vector3 p = zone.Anchors[a];
                Gizmos.DrawWireSphere(p + Vector3.up * 0.5f, 1.2f);
            }
        }
    }
#endif

    private static void Shuffle<T>(T[] values)
    {
        if (values == null) return;
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            T temp = values[i];
            values[i] = values[j];
            values[j] = temp;
        }
    }

    private static void ShuffleAnchors(System.Collections.Generic.List<Vector3> list)
    {
        if (list == null) return;
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            Vector3 tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    private static void ShuffleZones(EnemySpawnZone[] zones)
    {
        if (zones == null) return;
        for (int i = zones.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            EnemySpawnZone tmp = zones[i];
            zones[i] = zones[j];
            zones[j] = tmp;
        }
    }

    private static T EnsureComponent<T>(GameObject target) where T : Component
    {
        T component = target.GetComponent<T>();
        return component != null ? component : target.AddComponent<T>();
    }

    private static PlayerController FindPlayerControllerAny()
    {
        PlayerController active = Object.FindFirstObjectByType<PlayerController>();
        if (active != null)
            return active;

        PlayerController[] all = Object.FindObjectsByType<PlayerController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null)
                return all[i];

        return null;
    }

    private static void SetExistingPlayersActive(bool active)
    {
        PlayerController[] players = Object.FindObjectsByType<PlayerController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            PlayerController player = players[i];
            if (player == null) continue;
            player.gameObject.SetActive(active);
        }
    }

    public static bool IsMultiplayerBuildComplete => _multiplayerBuildComplete;

    public static bool IsLocalClientMapReadyForSync()
    {
        if (!Application.isPlaying)
            return false;

        if (SceneManager.GetActiveScene().name != MultiplayerMode.MultiplayerSceneName)
            return true;

        if (!IsMultiplayerBuildComplete)
            return false;

        if (!TryGetMultiplayerMapReadiness(out MapReadinessInfo info))
            return false;

        return info.MeshRendererCount > 0 && info.ColliderCount > 0 && info.GroundRaycastOk;
    }

    public struct MapReadinessInfo
    {
        public Transform Root;
        public int MeshRendererCount;
        public int ColliderCount;
        public bool GroundRaycastOk;
        public string Message;
    }

    public static bool TryGetMultiplayerMapReadiness(out MapReadinessInfo info)
    {
        info = new MapReadinessInfo();
        Transform root = FindMultiplayerMapRoot();
        info.Root = root;

        if (root == null)
        {
            info.Message = "no active map root found";
            return false;
        }

        if (!root.gameObject.activeInHierarchy)
        {
            info.Message = "map root inactive: " + root.name;
            return false;
        }

        MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(false);
        Collider[] colliders = root.GetComponentsInChildren<Collider>(false);
        info.MeshRendererCount = meshRenderers != null ? meshRenderers.Length : 0;
        info.ColliderCount = colliders != null ? colliders.Length : 0;

        if (info.MeshRendererCount <= 0)
        {
            info.Message = "map root has no active MeshRenderer: " + root.name;
            return false;
        }

        if (info.ColliderCount <= 0)
        {
            info.Message = "map root has no active Collider: " + root.name;
            return false;
        }

        info.GroundRaycastOk = TryRaycastMapGround(root, out _);
        if (!info.GroundRaycastOk)
        {
            info.Message = "ground raycast failed under map root: " + root.name;
            return false;
        }

        info.Message = "ready";
        return true;
    }

    public static bool TryValidateMultiplayerNavMesh(out bool ready)
    {
        ready = false;

        MapReadinessInfo info;
        if (!TryGetMultiplayerMapReadiness(out info) || info.Root == null)
        {
            Debug.Log("[AINav] NavMesh ready = false");
            return false;
        }

        Vector3[] probes =
        {
            info.Root.position,
            SafeFallbackSpawn,
            new Vector3(8f, SafeFallbackSpawn.y, 8f),
            new Vector3(-8f, SafeFallbackSpawn.y, 8f),
            new Vector3(8f, SafeFallbackSpawn.y, -8f),
            new Vector3(-8f, SafeFallbackSpawn.y, -8f)
        };

        for (int i = 0; i < probes.Length; i++)
        {
            if (NavMesh.SamplePosition(probes[i], out _, 18f, NavMesh.AllAreas))
            {
                ready = true;
                break;
            }
        }

        if (!ready)
        {
            Collider[] colliders = info.Root.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled)
                    continue;

                if (NavMesh.SamplePosition(c.bounds.center, out _, 18f, NavMesh.AllAreas))
                {
                    ready = true;
                    break;
                }
            }
        }

        Debug.Log("[AINav] NavMesh ready = " + ready.ToString().ToLowerInvariant());
        return ready;
    }

    private static Transform FindMultiplayerMapRoot()
    {
        string[] names = { "FbxMap", "Map", "Environment", "LevelRoot", "GeneratedLevel", "Ground", ArenaRootName, GameplayRootName };
        for (int i = 0; i < names.Length; i++)
        {
            GameObject found = GameObject.Find(names[i]);
            if (found != null && found.activeInHierarchy)
                return found.transform;
        }

        string[] tags = { "Map", "Environment", "LevelContent" };
        for (int i = 0; i < tags.Length; i++)
        {
            try
            {
                GameObject[] tagged = GameObject.FindGameObjectsWithTag(tags[i]);
                for (int j = 0; j < tagged.Length; j++)
                    if (tagged[j] != null && tagged[j].activeInHierarchy)
                        return tagged[j].transform;
            }
            catch { }
        }

        int environmentLayer = LayerMask.NameToLayer("Environment");
        if (environmentLayer >= 0)
        {
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].gameObject.layer == environmentLayer)
                    return renderers[i].transform.root;
        }

        return null;
    }

    private static bool TryRaycastMapGround(Transform root, out RaycastHit hit)
    {
        Vector3 origin = root != null ? root.position : Vector3.zero;
        origin.y += 500f;
        if (Physics.Raycast(origin, Vector3.down, out hit, 1000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return true;

        Collider[] colliders = root != null ? root.GetComponentsInChildren<Collider>(false) : null;
        if (colliders == null)
            return false;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled)
                continue;

            Vector3 above = collider.bounds.center;
            above.y = collider.bounds.max.y + 100f;
            if (Physics.Raycast(above, Vector3.down, out hit, 300f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return true;
        }

        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EDITOR-READY CLEANUP / STABILIZATION (Inspector-callable)
    // ════════════════════════════════════════════════════════════════════════
    //
    // Why these live here: the user can change the level index in the
    // Inspector and immediately see the new map preview in the Scene view
    // (because LevelBuilder is [ExecuteAlways]). But stale prefabs from the
    // previous level — anything tagged "Environment", "LevelContent", or
    // "Map" — would otherwise pile up on top of the new geometry, blocking
    // a clean NavMesh bake. ClearExistingLevel() wipes them. After the new
    // map is instantiated, StabilizeGround() walks the geometry and forces
    // floors to be static, on the Environment layer, and not kinematic-free.
    //
    // PrepareForBake() is the one-shot entry point the user calls from a
    // context-menu before pressing the Navigation window's Bake button.
    // ════════════════════════════════════════════════════════════════════════

    private static readonly string[] LevelContentTags = { "Environment", "LevelContent", "Map" };

    /// <summary>
    /// Destroys every loose GameObject in the active scene that carries one of
    /// the level-content tags. Runs in Editor and at runtime.
    /// </summary>
    [ContextMenu("Level/Clear Existing Level")]
    public void ClearExistingLevel()
    {
        int destroyed = 0;
        foreach (string tag in LevelContentTags)
        {
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tag); }
            catch { continue; } // Tag not defined in this project — skip.

            for (int i = 0; i < tagged.Length; i++)
            {
                if (tagged[i] == null) continue;
                if (tagged[i].transform.IsChildOf(transform)) continue; // never nuke ourselves
                if (IsRuntimeRootObject(tagged[i]))
                    continue;
                DestroyObjectSafe(tagged[i]);
                destroyed++;
            }
        }

        // Also wipe the procedural roots so the next build starts clean.
        Transform arena = GameObject.Find(ArenaRootName)?.transform;
        Transform enemies = GameObject.Find(EnemyRootName)?.transform;
        if (arena != null)   ClearChildren(arena);
        if (enemies != null) ClearChildren(enemies);

        Debug.Log($"[LevelBuilder] ClearExistingLevel: removed {destroyed} tagged objects + procedural roots.");
    }

    private static bool IsRuntimeRootObject(GameObject obj)
    {
        if (obj == null)
            return false;
        return obj.name == GameplayRootName
            || obj.name == ArenaRootName
            || obj.name == EnemyRootName;
    }

    /// <summary>
    /// Walks every renderer under <paramref name="root"/>, snaps anything that
    /// looks like a floor or wall onto the Environment layer, marks it static, and
    /// neutralises stray Rigidbodies that were causing the floor to drift.
    /// </summary>
    public void StabilizeGround(Transform root)
    {
        if (root == null) return;
        if (useSciFiArena) return;

        int envLayer = LayerMask.NameToLayer("Environment");
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            GameObject go = r.gameObject;
            string n = go.name.ToLowerInvariant();
            bool looksLikeEnvironment =
                n.Contains("floor") || n.Contains("ground") ||
                n.Contains("plane") || n.Contains("terrain") ||
                n.Contains("wall") || n.Contains("ceiling") ||
                n.Contains("concrete") || n.Contains("road");

            if (!looksLikeEnvironment) continue;

            // Layer
            if (envLayer >= 0) SetLayerRecursively(go, envLayer);

            // Static flags — batching + NavMesh only (avoid Occluder/Occludee hiding walls at angles).
#if UNITY_EDITOR
            UnityEditor.StaticEditorFlags flags = UnityEditor.StaticEditorFlags.BatchingStatic;
            UnityEditor.GameObjectUtility.SetStaticEditorFlags(go, flags);
#endif
            go.isStatic = true;

            // Collider — guarantee one exists so NavMesh + physics work.
            if (go.GetComponent<Collider>() == null)
            {
                if (go.GetComponent<MeshFilter>() != null)
                    go.AddComponent<MeshCollider>();
                else
                    go.AddComponent<BoxCollider>();
            }

            // Rigidbody — environment must never move. Prefer to remove; if other
            // scripts depend on it, force-kinematic.
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // If you'd rather delete it outright, uncomment:
                // DestroyObjectSafe(rb);
            }
        }
    }

    /// <summary>
    /// Editor-callable: ensures every level-content root is active, geometry
    /// is stabilised, and tags/layers are correct so the Navigation window's
    /// Bake button picks up the new map perfectly.
    /// </summary>
    [ContextMenu("Level/Prepare For Bake")]
    public void PrepareForBake()
    {
        // 1. Force every relevant root active so its renderers contribute.
        SetRootActive(GameplayRootName);
        SetRootActive(ArenaRootName);
        SetRootActive(EnemyRootName);

        Transform gameplay = GameObject.Find(GameplayRootName)?.transform;
        Transform arena = GameObject.Find(ArenaRootName)?.transform;
        Transform enemies = GameObject.Find(EnemyRootName)?.transform;

        if (gameplay != null)
            TagObjectIfDefined(gameplay.gameObject, "LevelContent");

        if (arena != null)
        {
            TagObjectIfDefined(arena.gameObject, "LevelContent");
            EnsureHierarchyActive(arena);
            TagHierarchyByName(arena);
        }

        if (enemies != null)
            EnsureHierarchyActive(enemies);

        // 2. Activate every tagged level-content object.
        foreach (string tag in LevelContentTags)
        {
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tag); }
            catch { continue; }
            foreach (GameObject go in tagged)
                if (go != null && !go.activeSelf) go.SetActive(true);
        }

        // 3. Stabilise the floor across both procedural and tagged content.
        if (arena != null) StabilizeGround(arena);
        MarkNavMeshWalkability();
        MapAtmosphereCleanup.RemoveFromActiveScene();

        foreach (string tag in LevelContentTags)
        {
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tag); }
            catch { continue; }
            foreach (GameObject go in tagged)
            {
                if (go == null) continue;
                EnsureHierarchyActive(go.transform);
                TagHierarchyByName(go.transform);
                StabilizeGround(go.transform);
            }
        }

#if UNITY_EDITOR
        // Mark scene dirty so the bake button sees the static-flag changes.
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
#endif
        Debug.Log("[LevelBuilder] PrepareForBake: scene is ready for NavMesh bake.");
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    private static void SetRootActive(string objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return;
        GameObject obj = GameObject.Find(objectName);
        if (obj != null && !obj.activeSelf)
            obj.SetActive(true);
    }

    private static void EnsureHierarchyActive(Transform root)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            child.gameObject.SetActive(true);
    }

    private static void TagHierarchyByName(Transform root)
    {
        if (root == null) return;

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == null) continue;

            string lowerName = child.gameObject.name.ToLowerInvariant();
            if (lowerName.Contains("floor") || lowerName.Contains("ground") ||
                lowerName.Contains("plane") || lowerName.Contains("terrain"))
                TagObjectIfDefined(child.gameObject, "Environment");
        }
    }

    private static void TagObjectIfDefined(GameObject go, string tag)
    {
        if (go == null || string.IsNullOrWhiteSpace(tag))
            return;

        try
        {
            go.tag = tag;
        }
        catch
        {
            // Tag is not configured in the project yet; skip safely.
        }
    }
}

public class DoorPassThroughOpen : MonoBehaviour
{
    // Default flipped to FALSE: the door VISUAL must stay visible at all
    // times. The previous default hid the entire door GameObject (SetActive
    // false) on first open, producing the "doors missing" report. Pass-through
    // is now handled by simply disabling the colliders; the renderers stay on.
    public bool hideOnOpen = false;
    public Vector3 moveOffset = Vector3.up * 3f;

    public void OpenPassable()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || !c.enabled) continue;
            c.enabled = false;
        }

        if (hideOnOpen)
            gameObject.SetActive(false);
        // No offset move either — the door visual stays exactly where it was.
    }
}
