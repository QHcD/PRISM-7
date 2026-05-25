using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelManager : MonoBehaviour
{
    private const string PlayerSpawnMarkerName = "PlayerSpawn";
    private const string SpawnPointsParentName = "SpawnPoints";
    private const string MainMenuSceneName = "MainMenu";
    private const float VoidYThreshold = -2f;
    private const float SafeRespawnLiftY = 0.6f;
    private const float WatchdogIntervalSeconds = 0.25f;
    private const float WatchdogDurationSeconds = 8f;
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
        if (LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + WatchdogDurationSeconds;
            while (!LevelBuilder.IsRuntimeLevelReady && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!LevelBuilder.IsRuntimeLevelReady)
            {
                Debug.LogError("[LevelManager] Spawn watchdog halted until LevelBuilder reports a valid runtime NavMesh.");
                _sceneSafetyRoutine = null;
                yield break;
            }
        }

        StabilizeEnvironment();
        _spawnWatchdog = StartCoroutine(SpawnSafetyWatchdog());
        _sceneSafetyRoutine = null;
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
            if (!nanPosition && LevelInteriorSpawnResolver.IsValidInteriorPosition(pos))
                return false;

            if (LevelInteriorSpawnResolver.TryResolveInteriorSpawn(player, out Vector3 interiorTarget))
            {
                LevelInteriorSpawnResolver.ApplyExternalSpawn(player, interiorTarget);
                return true;
            }

            return false;
        }

        if (!nanPosition && !belowVoid && HasGroundDirectlyBelow(pos))
            return false;

        if (!TryResolveSafeSpawnTarget(out Vector3 safeTarget))
            return false;
        player.TeleportTo(safeTarget);
        Physics.SyncTransforms();
        return true;
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

    private static bool TryResolveSafeSpawnTarget(out Vector3 target)
    {
        target = default;
        Transform marker = LocateActivePlayerSpawnMarker();
        if (marker == null)
            return false;

        target = marker.position + Vector3.up * SafeRespawnLiftY;
        return true;
    }

    private static Transform LocateActivePlayerSpawnMarker()
    {
        GameObject arena = GameObject.Find("FbxMap");
        if (arena == null) arena = GameObject.Find("SciFiArena");
        if (arena == null) arena = GameObject.Find("SciFiArena(Clone)");
        if (arena == null) return null;

        Transform direct = arena.transform.Find(SpawnPointsParentName + "/" + PlayerSpawnMarkerName);
        if (direct != null) return direct;

        Transform[] children = arena.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            Transform t = children[i];
            if (t != null && t.name == PlayerSpawnMarkerName)
                return t;
        }
        return null;
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
        Debug.Log($"[LevelManager] StabilizeEnvironment: {count} environment objects locked and colliders baked.");
    }
}
