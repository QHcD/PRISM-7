using System.Collections;
using UnityEngine;

/// <summary>
/// Scene-level safety net. Attach this to LevelManager if the generated
/// LevelBuilder path is noisy or package tooling fails during startup.
/// </summary>
public class LevelSetup : MonoBehaviour
{
    public PlayerController player;
    public Camera gameplayCamera;

    private IEnumerator Start()
    {
        if (Application.isPlaying && LevelBuilder.Instance != null)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (!LevelBuilder.IsRuntimeLevelReady && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!LevelBuilder.IsRuntimeLevelReady)
            {
                Debug.LogError("[LevelSetup] Setup halted until LevelBuilder reports a valid runtime NavMesh.");
                yield break;
            }
        }

        RunSetup();
    }

    private void RunSetup()
    {
        try
        {
            EnsurePlayer();
            EnsureCamera();
            EnsureGroundVisible();
            StabilizeEnvironment();
            StabilizeSceneStructures();
            DestroyHeavyLevelProps();
            ForceFallbackSpawnIfNeeded();
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
        }
    }

    private void ForceFallbackSpawnIfNeeded()
    {
        if (player == null)
            return;

        Vector3 position = player.transform.position;
        bool unsafePosition = float.IsNaN(position.x)
            || float.IsNaN(position.y)
            || float.IsNaN(position.z)
            || position.y < -0.5f;

        if (!unsafePosition && (!LevelInteriorSpawnResolver.RequiresInteriorSpawn || LevelInteriorSpawnResolver.IsValidInteriorPosition(position)))
            return;

        if (LevelInteriorSpawnResolver.TryResolveSceneSpawn(player, out Vector3 spawn))
            LevelInteriorSpawnResolver.ApplyExternalSpawn(player, spawn);
    }

    private void EnsureGroundVisible()
    {
        bool hasFbxMap = GameObject.Find("FbxMap") != null;
        bool foundGround = false;
        string[] names = { "Plane", "Ground", "ground", "PhysicsFloor", "Ground_PhysicsFloor", "ArenaFloor" };

        for (int i = 0; i < names.Length; i++)
        {
            GameObject ground = GameObject.Find(names[i]);
            if (ground == null)
                continue;

            if (hasFbxMap)
            {
                // Hide procedural/fallback ground meshes since FbxMap is successfully loaded
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

        // Fix: if this fallback exists in a real level, it should not visually block the scene.
        // We only disable the MeshRenderer as requested (collider stays intact).
        if (fallbackRenderer != null)
            fallbackRenderer.enabled = false;

        Debug.LogWarning("[LevelSetup] No ground renderer found; created VisibleGround_Fallback.");
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

    /// <summary>
    /// Walk every "Map" / "Environment" / "Level" root and:
    ///   • Destroy non-kinematic Rigidbodies on geometry (floors and walls
    ///     must not fall under gravity once the scene starts).
    ///   • Re-assign every collidable child to the "Environment" layer so the
    ///     camera SphereCast mask in CameraController catches them as
    ///     wall/floor blockers.
    /// IDamageable actors (player, enemies) parented under these roots are
    /// preserved untouched so their physics aren't broken.
    /// </summary>
    private void StabilizeEnvironment()
    {
        string[] rootNames = { "Map", "Environment", "Level", "World", "Geometry", "Arena" };
        int envLayer = LayerMask.NameToLayer("Environment");
        int mapLayer = LayerMask.NameToLayer("Map");

        // Pass 1: by-name root scan — catches scenes where geometry is grouped
        // under a recognisable parent but not yet on a dedicated layer.
        for (int i = 0; i < rootNames.Length; i++)
        {
            GameObject root = GameObject.Find(rootNames[i]);
            if (root == null) continue;
            StabilizeHierarchy(root.transform, envLayer);
        }

        // Pass 2: by-layer scan — catches loose geometry that isn't grouped
        // under any known root but is already on Map/Environment.
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

            // Never touch characters — they own their own physics setup.
            if (col.GetComponentInParent<IDamageable>() != null) continue;

            // Strip the body so the floor / walls don't fall when the scene
            // starts. Kinematic bodies (moving platforms etc.) are preserved.
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

    /// <summary>
    /// Performance pass: destroy known-heavy props that are not gameplay-critical.
    /// Concrete Barriers are preserved; everything else on the hit list is removed.
    /// </summary>
    private void DestroyHeavyLevelProps()
    {
        // Name fragments to destroy (case-insensitive substring match).
        string[] destroyPatterns = { "car", "wooden box", "woodenbox", "red building", "redbuilding" };
        // Prefixes that must be preserved even if they contain a destroy pattern.
        string[] preservePatterns = { "concrete barrier", "concretebarrier" };

        GameObject[] all = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        foreach (GameObject go in all)
        {
            if (go == null || !go.scene.IsValid()) continue;
            // Never destroy the player or enemies.
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
            // Optional AI/Sentis/npm-backed editor tooling is intentionally not
            // required for movement, camera, or melee combat.
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LevelSetup] Optional AI/Sentis initialization skipped. {e.GetType().Name}: {e.Message}");
        }
    }
}
