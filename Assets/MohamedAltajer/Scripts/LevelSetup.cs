using System.Collections;
using UnityEngine;

public class LevelSetup : MonoBehaviour
{
    public PlayerController player;
    public Camera gameplayCamera;

    private void Awake()
    {
        if (!Application.isPlaying)
            return;

        GameplayCameraBootstrap.FlushAllTargetCaches();
        LevelManager.RunFrameZeroRuntimeSync();
    }

    private IEnumerator Start()
    {
        if (Application.isPlaying && LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (!LevelBuilder.IsRuntimeLevelReady && Time.realtimeSinceStartup < deadline)
            {
                LevelManager.RunFrameZeroRuntimeSync();
                yield return null;
            }
            if (!LevelBuilder.IsRuntimeLevelReady)
                Debug.LogWarning("[LevelSetup] Continuing setup after runtime readiness timeout.");
        }

        RunSetup();
    }

    private void RunSetup()
    {
        try
        {
            EnsurePlayer();
            EnsureGroundVisible();
            StabilizeEnvironment();
            StabilizeSceneStructures();
            DestroyHeavyLevelProps();
            ForceFallbackSpawnIfNeeded();
            EnsureCamera();
            TryInitializeOptionalAISystems();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelSetup] Non-critical setup failed; forcing fallback spawn. {e.GetType().Name}: {e.Message}");
            EnsureGroundVisible();
            ForceFallbackSpawnIfNeeded();
        }
    }

    private void EnsurePlayer()
    {
        if (player == null)
            player = FindFirstObjectByType<PlayerController>();

        if (player == null)
        {
            Debug.LogWarning("[LevelSetup] PlayerController not found; fallback setup will continue.");
            return;
        }

        if (!player.CompareTag("Player"))
            player.tag = "Player";

        if (player.GetComponent<PlayerHealth>() == null)
            player.gameObject.AddComponent<PlayerHealth>();
    }

    private void EnsureCamera()
    {
        if (gameplayCamera == null && player != null)
            gameplayCamera = player.ActiveCamera;

        if (gameplayCamera == null)
            gameplayCamera = Camera.main;

        if (gameplayCamera == null)
        {
            GameObject cameraObject = new GameObject("FallbackGameplayCamera");
            gameplayCamera = cameraObject.AddComponent<Camera>();
            cameraObject.tag = "MainCamera";
        }

        if (player != null)
        {
            CameraController cameraController = gameplayCamera.GetComponent<CameraController>();
            if (cameraController == null)
                cameraController = gameplayCamera.gameObject.AddComponent<CameraController>();

            cameraController.target = player.transform;
            cameraController.SnapToTarget();
            GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);
        }
    }

    private void ForceFallbackSpawnIfNeeded()
    {
        if (player == null)
            return;

        LevelManager.RunFrameZeroRuntimeSync();

        Vector3 position = player.transform.position;
        bool unsafePosition = float.IsNaN(position.x)
            || float.IsNaN(position.y)
            || float.IsNaN(position.z)
            || position.y < -0.5f;

        if (!unsafePosition && LevelInteriorSpawnResolver.RequiresInteriorSpawn)
            unsafePosition = !LevelInteriorSpawnResolver.IsValidInteriorPosition(position);

        if (!unsafePosition)
            return;

        Debug.Log($"[SciFiSpawn] LevelSetup: player position unsafe pos={position}, resolving...");
        if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 spawn))
        {
            Debug.Log($"[SciFiSpawn] LevelSetup: resolved to {spawn}");
            LevelInteriorSpawnResolver.ApplyExternalSpawn(player, spawn);
            Physics.SyncTransforms();
            GameplayCameraBootstrap.BindActiveGameplayCamera(player.transform);

            if (gameplayCamera != null)
            {
                CameraController camCtrl = gameplayCamera.GetComponent<CameraController>();
                if (camCtrl != null)
                    camCtrl.SnapToTarget();
            }
        }
    }

    private void EnsureGroundVisible()
    {
        bool hasFbxMap = GameObject.Find("FbxMap") != null;
        if (LevelBuilder.Instance != null && LevelBuilder.Instance.useSciFiArena && !hasFbxMap)
        {
            Debug.LogWarning("[SciFiSpawn] SciFiArena map root missing; spawn resolver will use generic scene floor projection.");
            return;
        }
        bool foundGround = false;
        string[] names = { "Plane", "Ground", "ground", "PhysicsFloor", "Ground_PhysicsFloor", "ArenaFloor" };

        for (int i = 0; i < names.Length; i++)
        {
            GameObject ground = GameObject.Find(names[i]);
            if (ground == null)
                continue;

            if (hasFbxMap)
            {
                Renderer[] renderers = ground.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] != null)
                        renderers[r].enabled = false;
                }
                continue;
            }

            ground.SetActive(true);
            Renderer[] renderers2 = ground.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers2.Length; r++)
            {
                if (renderers2[r] == null)
                    continue;

                renderers2[r].enabled = true;
                foundGround = true;
            }
        }

        if (hasFbxMap || foundGround)
            return;

        GameObject fallbackGround = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fallbackGround.name = "VisibleGround_Fallback";
        fallbackGround.transform.position = new Vector3(0f, -0.05f, 0f);
        fallbackGround.transform.localScale = new Vector3(44f, 0.1f, 44f);

        Renderer fallbackRenderer = fallbackGround.GetComponent<Renderer>();
        if (fallbackRenderer != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");
            if (shader != null)
            {
                Material material = new Material(shader);
                Color groundColor = new Color(0.42f, 0.44f, 0.39f, 1f);
                material.color = groundColor;
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", groundColor);
                fallbackRenderer.material = material;
            }
        }

        if (fallbackRenderer != null)
            fallbackRenderer.enabled = false;
    }

    private void StabilizeSceneStructures()
    {
        string[] mapRootNames = { "FbxMap", "IndustrialMap", "IndustrialMap_v3", "IndustrialMap_v3_small", "SciFiArena", "SciFiArena(Clone)" };
        for (int i = 0; i < mapRootNames.Length; i++)
        {
            GameObject mapRoot = GameObject.Find(mapRootNames[i]);
            if (mapRoot == null) continue;
            MapStructureStabilizer.Install(mapRoot.transform);
            MapVisibilityStabilizer.Install(mapRoot.transform);
            EnemySpawnGeometry.RefreshStreetSpawnAnchors(mapRoot.transform);
        }
    }

    private void StabilizeEnvironment()
    {
        string[] rootNames = { "Map", "Environment", "Level", "World", "Geometry", "Arena" };
        int envLayer = LayerMask.NameToLayer("Environment");
        int mapLayer = LayerMask.NameToLayer("Map");

        for (int i = 0; i < rootNames.Length; i++)
        {
            GameObject root = GameObject.Find(rootNames[i]);
            if (root == null) continue;
            StabilizeHierarchy(root.transform, envLayer);
        }

        if (envLayer >= 0 || mapLayer >= 0)
            StabilizeByLayer(envLayer, mapLayer);
    }

    private void StabilizeByLayer(int envLayer, int mapLayer)
    {
        Collider[] all = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Collider col = all[i];
            if (col == null) continue;
            int layer = col.gameObject.layer;
            if (layer != envLayer && layer != mapLayer) continue;
            if (col.GetComponentInParent<IDamageable>() != null) continue;

            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic
                && rb.GetComponentInParent<IDamageable>() == null)
            {
                Destroy(rb);
            }

            if (envLayer >= 0)
                col.gameObject.layer = envLayer;
        }
    }

    private void StabilizeHierarchy(Transform root, int envLayer)
    {
        if (root == null) return;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null) continue;

            if (col.GetComponentInParent<IDamageable>() != null) continue;

            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic
                && rb.GetComponentInParent<IDamageable>() == null)
            {
                Destroy(rb);
            }

            if (envLayer >= 0)
                col.gameObject.layer = envLayer;
        }
    }

    private void DestroyHeavyLevelProps()
    {
        string[] destroyPatterns = { "car", "wooden box", "woodenbox", "red building", "redbuilding" };
        string[] preservePatterns = { "concrete barrier", "concretebarrier" };

        GameObject[] all = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        foreach (GameObject go in all)
        {
            if (go == null || !go.scene.IsValid()) continue;
            if (go.GetComponentInParent<IDamageable>() != null) continue;

            string nameLower = go.name.ToLowerInvariant();

            bool shouldPreserve = false;
            foreach (string p in preservePatterns)
                if (nameLower.Contains(p)) { shouldPreserve = true; break; }
            if (shouldPreserve) continue;

            foreach (string pattern in destroyPatterns)
            {
                if (nameLower.Contains(pattern))
                {
                    Destroy(go);
                    break;
                }
            }
        }
    }

    private void TryInitializeOptionalAISystems()
    {
        try
        {
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelSetup] Optional AI/Sentis initialization skipped. {e.GetType().Name}: {e.Message}");
        }
    }
}
