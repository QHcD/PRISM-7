using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-5000)]
public class LevelInitialization : MonoBehaviour
{
    private static LevelInitialization _instance;

    private const string MainMenuSceneName = "MainMenu";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        GameObject go = new GameObject("LevelInitialization_Runtime");
        _instance = go.AddComponent<LevelInitialization>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()  => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Application.isPlaying) return;
        StopLobbyMusicForGameplay(scene.name);
        if (scene.name == MainMenuSceneName) return;
        StartCoroutine(InitSequence());
    }

    private IEnumerator InitSequence()
    {
        AuditLevelColliders();
        Physics.SyncTransforms();

        yield return null;

        AuditLevelColliders();
        Physics.SyncTransforms();

        yield return new WaitForFixedUpdate();

        if (LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while ((!LevelBuilder.IsRuntimeLevelReady
                    || (LevelInteriorSpawnResolver.RequiresInteriorSpawn && !LevelBuilder.IsRuntimeNavMeshReady))
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!LevelBuilder.IsRuntimeLevelReady
                || (LevelInteriorSpawnResolver.RequiresInteriorSpawn && !LevelBuilder.IsRuntimeNavMeshReady))
                Debug.LogWarning("[LevelInitialization] Continuing player initialization after runtime readiness timeout.");
        }

        SnapPlayerToSpawn();
        Physics.SyncTransforms();

        BindCameraToPlayer();
        ForceWeaponReattach();
    }

    private static void AuditLevelColliders()
    {
        Collider[] all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Collider col = all[i];
            if (col == null) continue;
            if (col.GetComponentInParent<IDamageable>() != null) continue;

            if (!col.gameObject.activeInHierarchy)
                col.gameObject.SetActive(true);
            if (!col.enabled)
                col.enabled = true;

            MeshCollider mc = col as MeshCollider;
            if (mc != null && mc.sharedMesh != null)
                Physics.BakeMesh(mc.sharedMesh.GetInstanceID(), false);
        }
    }

    private static void SnapPlayerToSpawn()
    {
        if (LevelInteriorSpawnResolver.RequiresInteriorSpawn && !LevelBuilder.IsRuntimeNavMeshReady)
            return;

        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;

        Vector3 currentPos = player.transform.position;
        bool posInvalid = float.IsNaN(currentPos.x) || float.IsNaN(currentPos.y) || float.IsNaN(currentPos.z)
            || currentPos.y < -0.5f;

        if (!posInvalid && LevelInteriorSpawnResolver.RequiresInteriorSpawn)
            posInvalid = !LevelInteriorSpawnResolver.IsValidInteriorPosition(currentPos);

        if (!posInvalid)
        {
            Debug.Log($"[SciFiSpawn] LevelInitialization: player position valid at {currentPos}");
            return;
        }

        if (!LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 spawn))
        {
            Debug.Log($"[SciFiSpawn] LevelInitialization: no spawn resolved, player stays at {currentPos}");
            return;
        }

        Debug.Log($"[SciFiSpawn] LevelInitialization: spawning player at {spawn} (was {currentPos})");
        LevelInteriorSpawnResolver.ApplyExternalSpawn(player, spawn);
    }

    private static void BindCameraToPlayer()
    {
        CameraController cam = Object.FindFirstObjectByType<CameraController>();
        if (cam == null) return;
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;
        cam.target = player.transform;
        cam.SnapToTarget();
    }

    private static void ForceWeaponReattach()
    {
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;
        int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        player.ForceReattachWeapon(level);
    }

    private static void StopLobbyMusicForGameplay(string loadedSceneName)
    {
        if (loadedSceneName == MainMenuSceneName) return;
        GameObject go = GameObject.Find("LobbyMusic");
        if (go == null) return;
        AudioSource src = go.GetComponent<AudioSource>();
        if (src != null && src.isPlaying) src.Stop();
    }

}
