using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelManager : MonoBehaviour
{
    private const string MainMenuSceneName = "MainMenu";
    private const float VoidYThreshold = -2f;
    private const float SafeRespawnLiftY = 0.6f;
    private const float WatchdogIntervalSeconds = 0.25f;
    private const float WatchdogDurationSeconds = 12f;
    private const float GroundProbeUpOffset = 0.5f;
    private const float GroundProbeDistance = 12f;

    private static LevelManager _instance;
    private Coroutine _spawnWatchdog;
    private Coroutine _sceneSafetyRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        if (_instance == null)
        {
            GameObject go = new GameObject("LevelManager_Runtime");
            _instance = go.AddComponent<LevelManager>();
            DontDestroyOnLoad(go);
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            DestroyImmediate(this.gameObject);
            return;
        }
        _instance = this;
        if (Application.isPlaying)
        {
            DontDestroyOnLoad(gameObject);
        }
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        StopSpawnWatchdog();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StopSpawnWatchdog();

        if (!Application.isPlaying) return;
        if (scene.name == MainMenuSceneName) return;

        GameplayCameraBootstrap.FlushAllTargetCaches();
        RunFrameZeroRuntimeSync();

        if (_sceneSafetyRoutine != null)
            StopCoroutine(_sceneSafetyRoutine);
        _sceneSafetyRoutine = StartCoroutine(SceneSafetySequence());
    }

    private void StopSpawnWatchdog()
    {
        if (_sceneSafetyRoutine != null)
        {
            StopCoroutine(_sceneSafetyRoutine);
            _sceneSafetyRoutine = null;
        }
        if (_spawnWatchdog != null)
        {
            StopCoroutine(_spawnWatchdog);
            _spawnWatchdog = null;
        }
    }

    private IEnumerator SceneSafetySequence()
    {
        RunFrameZeroRuntimeSync();

        if (LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + WatchdogDurationSeconds;
            while (!LevelBuilder.IsRuntimeLevelReady && Time.realtimeSinceStartup < deadline)
            {
                RunFrameZeroRuntimeSync();
                yield return null;
            }
            if (!LevelBuilder.IsRuntimeLevelReady)
                Debug.LogWarning("[LevelManager] Spawn watchdog continuing after runtime readiness timeout.");
        }

        StabilizeEnvironment();
        RunFrameZeroRuntimeSync();
        _spawnWatchdog = StartCoroutine(SpawnSafetyWatchdog());
        _sceneSafetyRoutine = null;
    }

    public static bool RunFrameZeroRuntimeSync()
    {
        Transform target = GameplayCameraBootstrap.ResolveAuthoritativePlayerTarget();
        if (target == null)
            return false;

        PlayerController player = target.GetComponent<PlayerController>()
            ?? target.GetComponentInChildren<PlayerController>(true)
            ?? target.GetComponentInParent<PlayerController>();

        if (player != null && ShouldProjectPlayer(player))
        {
            if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 spawn))
            {
                LevelInteriorSpawnResolver.ApplyExternalSpawn(player, spawn);
                Physics.SyncTransforms();
            }
        }

        return GameplayCameraBootstrap.BindActiveGameplayCamera(target);
    }

    private static bool ShouldProjectPlayer(PlayerController player)
    {
        if (player == null)
            return false;

        Vector3 pos = player.transform.position;
        bool invalid = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
            || float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z)
            || pos.y < -0.5f;

        if (!invalid && LevelInteriorSpawnResolver.RequiresInteriorSpawn)
            invalid = !LevelInteriorSpawnResolver.IsValidInteriorPosition(pos);
        if (!invalid)
            invalid = !HasGroundDirectlyBelow(pos);

        return invalid;
    }

    private static void ForcePlayerToInterior()
    {
        if (LevelInteriorSpawnResolver.RequiresInteriorSpawn && !LevelBuilder.IsRuntimeLevelReady)
            return;

        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;

        Vector3 pos = player.transform.position;
        bool invalid = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || pos.y < VoidYThreshold;

        if (!invalid && LevelInteriorSpawnResolver.RequiresInteriorSpawn)
            invalid = !LevelInteriorSpawnResolver.IsValidInteriorPosition(pos);

        if (!invalid) return;

        Debug.Log($"[SciFiSpawn] LevelManager: ForcePlayerToInterior from {pos}");
        if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 spawn))
        {
            Debug.Log($"[SciFiSpawn] LevelManager: forced to {spawn}");
            LevelInteriorSpawnResolver.ApplyExternalSpawn(player, spawn);
            Physics.SyncTransforms();
            GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
        }
    }

    private IEnumerator SpawnSafetyWatchdog()
    {
        float deadline = Time.realtimeSinceStartup + WatchdogDurationSeconds;
        WaitForSeconds wait = new WaitForSeconds(WatchdogIntervalSeconds);

        while (Time.realtimeSinceStartup < deadline)
        {
            if (TryRescuePlayerFromVoid())
            {
                _spawnWatchdog = null;
                yield break;
            }
            yield return wait;
        }
        _spawnWatchdog = null;
    }

    private static bool TryRescuePlayerFromVoid()
    {
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return false;

        Vector3 pos = player.transform.position;
        bool nanPosition = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z);
        bool belowVoid = !nanPosition && pos.y < VoidYThreshold;

        if (LevelInteriorSpawnResolver.RequiresInteriorSpawn)
        {
            if (!LevelBuilder.IsRuntimeLevelReady)
                return false;

            if (!nanPosition && !belowVoid && LevelInteriorSpawnResolver.IsValidInteriorPosition(pos))
                return false;

            Debug.Log($"[SciFiSpawn] LevelManager watchdog: rescuing player from {pos} nan={nanPosition} void={belowVoid}");
            if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 interiorTarget))
            {
                Debug.Log($"[SciFiSpawn] LevelManager watchdog: rescued to {interiorTarget}");
                LevelInteriorSpawnResolver.ApplyExternalSpawn(player, interiorTarget);
                Physics.SyncTransforms();
                GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
                return true;
            }

            return false;
        }

        if (!nanPosition && !belowVoid && HasGroundDirectlyBelow(pos))
            return false;

        if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 safeTarget))
        {
            LevelInteriorSpawnResolver.ApplyExternalSpawn(player, safeTarget);
            Physics.SyncTransforms();
            GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
            return true;
        }

        return false;
    }

    private static bool HasGroundDirectlyBelow(Vector3 origin)
    {
        Vector3 castOrigin = origin + Vector3.up * GroundProbeUpOffset;
        return Physics.Raycast(
            castOrigin,
            Vector3.down,
            out RaycastHit _,
            GroundProbeDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
    }

    private void StabilizeEnvironment()
    {
        int envLayer = LayerMask.NameToLayer("Environment");
        int mapLayer = LayerMask.NameToLayer("Map");

        Transform[] allTransforms = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        int count = 0;

        foreach (Transform t in allTransforms)
        {
            GameObject obj = t.gameObject;

            bool isEnvironment = false;
            if (envLayer >= 0 && obj.layer == envLayer) isEnvironment = true;
            if (mapLayer >= 0 && obj.layer == mapLayer) isEnvironment = true;
            if (obj.CompareTag("Environment") || obj.CompareTag("Map")) isEnvironment = true;

            if (!isEnvironment) continue;
            if (obj.GetComponentInParent<IDamageable>() != null) continue;

            obj.isStatic = true;

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null) Destroy(rb);

            Collider[] cols = obj.GetComponents<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                if (!cols[i].enabled) cols[i].enabled = true;
                MeshCollider mc = cols[i] as MeshCollider;
                if (mc != null && mc.sharedMesh != null)
                    Physics.BakeMesh(mc.sharedMesh.GetInstanceID(), false);
            }

            count++;
        }

        Physics.SyncTransforms();
    }
}
