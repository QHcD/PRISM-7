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
        StabilizeEnvironment();

        if (!Application.isPlaying) return;
        if (scene.name == MainMenuSceneName) return;

        _spawnWatchdog = StartCoroutine(SpawnSafetyWatchdog());
    }

    private void StopSpawnWatchdog()
    {
        if (_spawnWatchdog != null)
        {
            StopCoroutine(_spawnWatchdog);
            _spawnWatchdog = null;
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

        if (!nanPosition && !belowVoid && HasGroundDirectlyBelow(pos))
            return false;

        Vector3 safeTarget = ResolveSafeSpawnTarget();
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

    private static Vector3 ResolveSafeSpawnTarget()
    {
        Transform marker = LocateActivePlayerSpawnMarker();
        if (marker != null)
            return marker.position + Vector3.up * SafeRespawnLiftY;
        return new Vector3(0f, 1f + SafeRespawnLiftY, 0f);
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
            
            // Check if object belongs to Environment or Map layer, or has Environment tag
            bool isEnvironment = false;
            if (envLayer >= 0 && obj.layer == envLayer) isEnvironment = true;
            if (mapLayer >= 0 && obj.layer == mapLayer) isEnvironment = true;
            if (obj.CompareTag("Environment") || obj.CompareTag("Map")) isEnvironment = true;

            if (isEnvironment)
            {
                // Skip if it is part of a character/damageable entity
                if (obj.GetComponentInParent<IDamageable>() != null) continue;

                // Make object static
                obj.isStatic = true;
                
                // Remove Rigidbody to prevent the floor/walls from falling
                Rigidbody rb = obj.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Destroy(rb);
                }
                
                count++;
            }
        }
        
        Debug.Log($"[LevelManager] Programmatic Scene Cleanup: Stabilized {count} environment objects (isStatic = true, Rigidbody removed).");
    }
}
