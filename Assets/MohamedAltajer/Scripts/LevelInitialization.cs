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
        GameplayCameraBootstrap.FlushAllTargetCaches();
        LevelManager.RunFrameZeroRuntimeSync();
        StartCoroutine(InitSequence());
    }

    private IEnumerator InitSequence()
    {
        LevelManager.RunFrameZeroRuntimeSync();
        AuditLevelColliders();
        Physics.SyncTransforms();

        yield return null;

        AuditLevelColliders();
        Physics.SyncTransforms();

        yield return new WaitForFixedUpdate();

        if (LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (!LevelBuilder.IsRuntimeLevelReady && Time.realtimeSinceStartup < deadline)
            {
                LevelManager.RunFrameZeroRuntimeSync();
                yield return null;
            }
            if (!LevelBuilder.IsRuntimeLevelReady)
                Debug.LogWarning("[LevelInitialization] Continuing player initialization after runtime readiness timeout.");
        }

        SnapPlayerToSpawn();
        Physics.SyncTransforms();

        BindCameraToPlayer();
        ForceWeaponReattach();
        yield return null;
        ForceEnemyWeaponReattach();
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
        Physics.SyncTransforms();
        GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
    }

    private static void BindCameraToPlayer()
    {
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;
        GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
    }

    private static void ForceWeaponReattach()
    {
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;
        int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        player.ForceReattachWeapon(level);
    }

    private static void ForceEnemyWeaponReattach()
    {
        int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
        WeaponLoadout loadout = WeaponLoadoutCatalog.Get(level);
        float targetSize = loadout.TargetSize;
        GameObject weaponPrefab = loadout.LoadPrefab();
        if (weaponPrefab == null)
            weaponPrefab = WeaponLoadoutCatalog.LoadPrefabWithFallback(level, out targetSize);
        if (weaponPrefab == null)
            return;

        EnemyController[] enemies = Object.FindObjectsByType<EnemyController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        for (int i = 0; i < enemies.Length; i++)
        {
            EnemyController enemy = enemies[i];
            if (enemy == null) continue;

            if (enemy.equippedWeaponObject != null)
            {
                Object.Destroy(enemy.equippedWeaponObject);
                enemy.equippedWeaponObject = null;
            }

            enemy.AttachWeaponToHand(weaponPrefab, targetSize, level);
            string prefabName;
            string socketName;
            int rendererCount;
            WeaponPresenceIsValid(enemy.equippedWeaponObject, out prefabName, out socketName, out rendererCount);
            Debug.Log("[WeaponRestore] backup logic applied");
            Debug.Log($"[WeaponRestore] enemy weapon attached={enemy.equippedWeaponObject != null && rendererCount > 0}");
            Debug.Log($"[WeaponRestore] socket={socketName}");
            Debug.Log($"[WeaponRestore] prefab={(weaponPrefab != null ? weaponPrefab.name : prefabName)}");
            Debug.Log($"[WeaponRestore] renderer count={rendererCount}");
        }
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
        weapon.SetActive(true);

        Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
            rendererCount++;
        }

        Vector3 scale = weapon.transform.localScale;
        if (Mathf.Approximately(scale.x, 0f) || Mathf.Approximately(scale.y, 0f) || Mathf.Approximately(scale.z, 0f))
            return false;

        return weapon.transform.parent != null && rendererCount > 0;
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
