#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a dense, enclosed sci-fi warehouse arena prefab from the kit.
/// Four corner rooms (Storage, Control, Loading Bay, Maintenance) wrap a central
/// combat hall, with sliding doors, broken sightlines, ceiling pipes/ducts, and
/// real Light components for industrial atmosphere.
/// </summary>
[InitializeOnLoad]
public static class SciFiArenaBuilder
{
    private const string KitRoot = "Assets/SciFi Warehouse Kit/Prefabs";
    private const string DemoScenePath = "Assets/SciFi Warehouse Kit/Demo/Scene/SciFi_Warehouse.unity";
    private const string OutputDir = "Assets/MohamedAltajer/Prefabs/Environment/Resources/Maps/SciFiArena";
    private const string OutputPrefab = OutputDir + "/SciFiArena.prefab";
    private const string VersionFile = OutputDir + "/.builder_version";

    // Bump when layout/content meaningfully changes — forces auto-rebuild.
    private const int BuilderVersion = 25;

    private const float ModuleSize = 6f;
    private const int GridSize = 9;            // 54m x 54m arena
    private const float CeilingY = 9f;
    private const float Half = GridSize * ModuleSize * 0.5f;   // 27

    // Interior room boundaries (4 corner rooms of 3x3 cells = 18m square).
    private const float RoomEdge = 9f;         // inner wall coords: +-RoomEdge

    static SciFiArenaBuilder()
    {
        EditorApplication.delayCall -= AutoBuildIfMissing;
        EditorApplication.delayCall += AutoBuildIfMissing;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void AutoBuildIfMissing()
    {
        if (Application.isPlaying) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        if (IsPrefabCurrentAndValid()) return;

        BuildArenaPrefab();
    }

    public static bool EnsurePrefabReady()
    {
        if (Application.isPlaying)
            return IsPrefabCurrentAndValid();
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return IsPrefabCurrentAndValid();
        if (!IsPrefabCurrentAndValid())
            BuildArenaPrefab();
        return IsPrefabCurrentAndValid();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode) return;
        if (IsPrefabCurrentAndValid()) return;

        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () =>
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                EnsurePrefabReady();
        };
        Debug.LogError("[SciFiArenaBuilder] SciFiArena prefab was invalid. Play Mode was stopped while the prefab rebuild runs outside Play Mode.");
    }

    private sealed class SciFiArenaBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!EnsurePrefabReady())
                throw new BuildFailedException("SciFiArena prefab failed validation before player build.");
        }
    }

    private sealed class KitRefs
    {
        public GameObject floor, floor2, floor3, floor4;
        public GameObject ceilingClosed, ceilingSkylight;
        public GameObject wallPlain, wallDoor, wallDoor2, wallWindow, wallCorner, wallCorridor, wallFan, wallBayDoor;
        public GameObject pillar, wallSupport, ceilingSupport;
        public GameObject shelf01, shelf02, shelf03, shelf04, shelf05, shelf06, shelfEmpty;
        public GameObject crateLong, crateShort, barrel, pallet;
        public GameObject[] palletVariants;
        public GameObject ductStraight, ductElbow01, ductElbow02, ductTee, ductVent;
        public GameObject[] pipesAll;
        public GameObject fusebox01, fusebox02;
        public GameObject exitSign, wallLight, hangingLight;
        public GameObject[] sprinklers;
        public GameObject cart, fireExt, garbageBin, securityCam;
        public GameObject catwalkLong, catwalkShort, catwalkCorner, catwalkRailsLong, catwalkRailsShort;
        public GameObject catwalkPillar01, catwalkPillar02, catwalkPillar03, catwalkStairs;
        public Material doorMat;
    }

    [MenuItem("Tools/PRISM-7/Build SciFi Arena Prefab")]
    public static void BuildArenaPrefab()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
        {
            Debug.LogError("[SciFiArenaBuilder] Cannot build while Play Mode is active. Stop Play Mode first.");
            return;
        }

        SciFiKitURPMaterialFixer.Convert();
        EnsureDir(OutputDir);

        if (TryBuildPrefabFromDemoScene(out GameObject demoSaved))
        {
            File.WriteAllText(VersionFile, BuilderVersion.ToString());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[SciFiArena] Built v{BuilderVersion} from complete demo warehouse modules: {OutputPrefab}");
            Selection.activeObject = demoSaved;
            EditorGUIUtility.PingObject(demoSaved);
            return;
        }

        Debug.LogError("[SciFiArena] Build aborted. Low-level procedural warehouse assembly is disabled; fix the demo scene reference instead.");
        bool allowLegacyFallback = false;
        if (!allowLegacyFallback)
            return;

        KitRefs k = LoadKit();
        if (k.floor == null || k.wallPlain == null)
        {
            Debug.LogError("[SciFiArena] Critical prefabs missing (floor/wall). Cannot build.");
            return;
        }

        GameObject root = new GameObject("SciFiArena");
        Transform floors = MakeChild(root.transform, "Floors");
        Transform ceiling = MakeChild(root.transform, "Ceiling");
        Transform ceilingStruct = MakeChild(root.transform, "CeilingStructure");
        Transform perimeter = MakeChild(root.transform, "PerimeterWalls");
        Transform interior = MakeChild(root.transform, "InteriorWalls");
        Transform doors = MakeChild(root.transform, "Doors");
        Transform structure = MakeChild(root.transform, "Structure");
        Transform centralCover = MakeChild(root.transform, "CentralCover");
        Transform roomStorage = MakeChild(root.transform, "Room_NE_Storage");
        Transform roomControl = MakeChild(root.transform, "Room_NW_Control");
        Transform roomLoading = MakeChild(root.transform, "Room_SE_LoadingBay");
        Transform roomMaint = MakeChild(root.transform, "Room_SW_Maintenance");
        Transform ambient = MakeChild(root.transform, "AmbientProps");
        Transform lights = MakeChild(root.transform, "Lights");
        Transform spawns = MakeChild(root.transform, "SpawnPoints");

        BuildFloors(floors, k);
        BuildCeiling(ceiling, k);
        BuildPerimeterWalls(perimeter, doors, k);
        BuildInteriorWalls(interior, doors, k);
        BuildHallPartitions(interior, doors, k);
        BuildStructure(structure, k);
        BuildCeilingStructure(ceilingStruct, k);
        BuildCentralCover(centralCover, k);

        BuildStorageRoom(roomStorage, k);
        BuildControlRoom(roomControl, k);
        BuildLoadingBay(roomLoading, k);
        BuildMaintenanceRoom(roomMaint, k);

        BuildAmbient(ambient, k);
        BuildLighting(lights, k);
        BuildSpawnPoints(spawns);

        AddNavMeshSurface(root);
        ApplyMinimapLayer(centralCover);
        ApplyMinimapLayer(roomStorage);
        ApplyMinimapLayer(roomControl);
        ApplyMinimapLayer(roomLoading);
        ApplyMinimapLayer(roomMaint);
        ApplyMinimapLayer(ambient);
        ApplyMinimapLayer(ceilingStruct);

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefab);
        Object.DestroyImmediate(root);
        File.WriteAllText(VersionFile, BuilderVersion.ToString());
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[SciFiArena] Built v{BuilderVersion} — 4 corner rooms + central combat hall + dense props/lighting: {OutputPrefab}");
        Selection.activeObject = saved;
        EditorGUIUtility.PingObject(saved);
    }

    // ---------------- Kit loading ----------------

    private static KitRefs LoadKit()
    {
        KitRefs k = new KitRefs();
        k.floor   = Load("Structures/Floor/Floor Tile 01.prefab");
        k.floor2  = Load("Structures/Floor/Floor Tile 02.prefab");
        k.floor3  = Load("Structures/Floor/Floor Tile 03.prefab");
        k.floor4  = Load("Structures/Floor/Floor Tile 04.prefab");

        k.ceilingClosed   = Load("Structures/Ceiling/Ceiling Closed.prefab");
        k.ceilingSkylight = Load("Structures/Ceiling/Ceiling Skylight.prefab");

        k.wallPlain    = Load("Structures/Walls/Wall Plain.prefab");
        k.wallDoor     = Load("Structures/Walls/Wall Door.prefab");
        k.wallDoor2    = Load("Structures/Walls/Wall Door 02.prefab");
        k.wallWindow   = Load("Structures/Walls/Wall Window.prefab");
        k.wallCorner   = Load("Structures/Walls/Wall Corner.prefab");
        k.wallCorridor = Load("Structures/Walls/Wall Corridor.prefab");
        k.wallFan      = Load("Structures/Walls/Wall Fan.prefab");
        k.wallBayDoor  = Load("Structures/Walls/Wall BayDoor.prefab");

        k.pillar         = Load("Structures/Pillars and Beams/Pillar.prefab");
        k.wallSupport    = Load("Structures/Pillars and Beams/Wall Support.prefab");
        k.ceilingSupport = Load("Structures/Pillars and Beams/Ceiling Support.prefab");

        k.shelf01    = Load("Props/Shelves/Shelf Variation 01.prefab");
        k.shelf02    = Load("Props/Shelves/Shelf Variation 02.prefab");
        k.shelf03    = Load("Props/Shelves/Shelf Variation 03.prefab");
        k.shelf04    = Load("Props/Shelves/Shelf Variation 04.prefab");
        k.shelf05    = Load("Props/Shelves/Shelf Variation 05.prefab");
        k.shelf06    = Load("Props/Shelves/Shelf Variation 06.prefab");
        k.shelfEmpty = Load("Props/Shelves/Shelves Empty.prefab");

        k.crateLong  = Load("Props/Crates Barrels Pallets/Crate Long.prefab");
        k.crateShort = Load("Props/Crates Barrels Pallets/Crate Short.prefab");
        k.barrel     = Load("Props/Crates Barrels Pallets/Barrel.prefab");
        k.pallet     = Load("Props/Crates Barrels Pallets/Pallet.prefab");
        k.palletVariants = new GameObject[]
        {
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 01.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 02.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 03.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 04.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 05.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 06.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 07.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 08.prefab"),
            Load("Props/Crates Barrels Pallets/Pallet Variations/Pallet Variation 09.prefab"),
        };

        k.ductStraight = Load("Props/Ducts/Duct Straight.prefab");
        k.ductElbow01  = Load("Props/Ducts/Duct Elbow 01.prefab");
        k.ductElbow02  = Load("Props/Ducts/Duct Elbow 02.prefab");
        k.ductTee      = Load("Props/Ducts/Duct Tee.prefab");
        k.ductVent     = Load("Props/Ducts/Duct Vent.prefab");

        k.pipesAll = new GameObject[]
        {
            Load("Structures/Structure Props/Pipes 01.prefab"),
            Load("Structures/Structure Props/Pipes 02.prefab"),
            Load("Structures/Structure Props/Pipes 03.prefab"),
            Load("Structures/Structure Props/Pipes 04.prefab"),
            Load("Structures/Structure Props/Pipes 05.prefab"),
            Load("Structures/Structure Props/Pipes 06.prefab"),
        };

        k.fusebox01 = Load("Structures/Structure Props/Fusebox 01.prefab");
        k.fusebox02 = Load("Structures/Structure Props/Fusebox 02.prefab");
        k.exitSign  = Load("Structures/Structure Props/Exit Sign.prefab");

        k.sprinklers = new GameObject[]
        {
            Load("Structures/Structure Props/Sprinkler 01.prefab"),
            Load("Structures/Structure Props/Sprinkler 02.prefab"),
            Load("Structures/Structure Props/Sprinkler 03.prefab"),
            Load("Structures/Structure Props/Sprinkler 04.prefab"),
            Load("Structures/Structure Props/Sprinkler 05.prefab"),
            Load("Structures/Structure Props/Sprinkler 06.prefab"),
        };

        k.wallLight     = Load("Props/Misc Props/Wall Light.prefab");
        k.hangingLight  = Load("Props/Misc Props/Hanging Light.prefab");
        k.cart          = Load("Props/Misc Props/Cart.prefab");
        k.fireExt       = Load("Props/Misc Props/Fire Extinguisher.prefab");
        k.garbageBin    = Load("Props/Misc Props/Garbage Bin.prefab");
        k.securityCam   = Load("Props/Misc Props/Security Cam.prefab");

        k.catwalkLong       = Load("Structures/Catwalk/Catwalk Long.prefab");
        k.catwalkShort      = Load("Structures/Catwalk/Catwalk Short.prefab");
        k.catwalkCorner     = Load("Structures/Catwalk/Catwalk Corner.prefab");
        k.catwalkRailsLong  = Load("Structures/Catwalk/Catwalk Rails Long.prefab");
        k.catwalkRailsShort = Load("Structures/Catwalk/Catwalk Rails Short.prefab");
        k.catwalkPillar01   = Load("Structures/Catwalk/Catwalk Pillar 01.prefab");
        k.catwalkPillar02   = Load("Structures/Catwalk/Catwalk Pillar 02.prefab");
        k.catwalkPillar03   = Load("Structures/Catwalk/Catwalk Pillar 03.prefab");
        k.catwalkStairs     = Load("Structures/Catwalk/Catwalk Stairs.prefab");

        k.doorMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/SciFi Warehouse Kit/Art/Materials/Bay Door Mat.mat");
        return k;
    }

    private static GameObject Load(string subPath)
    {
        string full = KitRoot + "/" + subPath;
        GameObject p = AssetDatabase.LoadAssetAtPath<GameObject>(full);
        if (p == null) Debug.LogWarning("[SciFiArena] Missing prefab: " + full);
        return p;
    }

    // ---------------- Helpers ----------------

    private static Transform MakeChild(Transform parent, string name)
    {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    private static GameObject Spawn(GameObject prefab, Transform parent, Vector3 localPos, Quaternion localRot, string name = null)
    {
        if (prefab == null) return null;
        GameObject g = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        g.transform.localPosition = localPos;
        g.transform.localRotation = localRot;
        if (!string.IsNullOrEmpty(name)) g.name = name;
        return g;
    }

    private static GameObject SpawnScaled(GameObject prefab, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, string name = null)
    {
        GameObject g = Spawn(prefab, parent, pos, rot, name);
        if (g != null) g.transform.localScale = scale;
        return g;
    }

    /// <summary>
    /// Spawns a kit prop with all colliders disabled. Use for decorative geometry that
    /// must not be walkable (catwalks without stairs) or block movement.
    /// </summary>
    private static GameObject SpawnDeco(GameObject prefab, Transform parent, Vector3 pos, Quaternion rot, string name = null)
    {
        GameObject g = Spawn(prefab, parent, pos, rot, name);
        if (g == null) return null;
        Collider[] cols = g.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] != null) cols[i].enabled = false;
        }
        return g;
    }

    private static void EnsureDir(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private static Quaternion Yaw(float yawDeg) => Quaternion.Euler(0f, yawDeg, 0f);

    private static bool TryBuildPrefabFromDemoScene(out GameObject savedPrefab)
    {
        savedPrefab = null;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
        {
            Debug.LogError("[SciFiArenaBuilder] Cannot build while Play Mode is active. Stop Play Mode first.");
            return false;
        }

        if (!File.Exists(DemoScenePath))
        {
            Debug.LogError("[SciFiArena] Demo warehouse scene missing: " + DemoScenePath);
            return false;
        }

        Scene previousActive = SceneManager.GetActiveScene();
        Scene demoScene = default;
        GameObject root = null;

        try
        {
            demoScene = EditorSceneManager.OpenScene(DemoScenePath, OpenSceneMode.Additive);
            if (!demoScene.IsValid() || !demoScene.isLoaded)
                return false;

            root = new GameObject("SciFiArena");
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            Transform modules = MakeChild(root.transform, "DemoWarehouseModules");
            modules.localPosition = Vector3.zero;
            modules.localRotation = Quaternion.identity;
            modules.localScale = Vector3.one;

            GameObject[] sceneRoots = demoScene.GetRootGameObjects();
            int copiedRoots = 0;
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                GameObject sceneRoot = sceneRoots[i];
                if (sceneRoot == null || !ShouldCopyDemoWarehouseRoot(sceneRoot))
                    continue;

                GameObject copy = Object.Instantiate(sceneRoot);
                copy.name = sceneRoot.name;
                copy.transform.SetParent(modules, worldPositionStays: false);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one;
                copiedRoots++;
            }

            if (copiedRoots == 0)
            {
                Debug.LogError("[SciFiArena] Demo scene opened but no warehouse module roots were copied.");
                return false;
            }

            CleanupCopiedDemoHierarchy(root);
            AlignWarehouseRootToGrid(modules);
            if (!EnforceWarehousePrefabPipeline(root.transform))
                return false;
            MarkWarehouseStatic(root.transform);
            int proxyCount = BuildNavMeshProxyColliders(root.transform, modules);
            if (proxyCount <= 0)
            {
                Debug.LogError("[SciFiArenaBuilder] Build aborted: no walkable NavMesh proxy colliders were generated from the demo warehouse.");
                return false;
            }

            Transform spawns = MakeChild(root.transform, "SpawnPoints");
            BuildSpawnPoints(spawns, modules);
            AddNavMeshSurface(root);
            if (!ValidateWarehousePrefab(root.transform, out string validationError))
            {
                Debug.LogError("[SciFiArenaBuilder] Build aborted: " + validationError);
                return false;
            }
            LogSciFiArenaValidation(root.transform, modules, root.transform.Find("SciFiNavMeshProxyColliders"), spawns.Find("PlayerSpawn"));

            savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefab);
            return savedPrefab != null;
        }
        finally
        {
            if (root != null)
                Object.DestroyImmediate(root);
            if (demoScene.IsValid() && demoScene.isLoaded)
                EditorSceneManager.CloseScene(demoScene, true);
            if (previousActive.IsValid())
                SceneManager.SetActiveScene(previousActive);
        }
    }

    private static bool ShouldCopyDemoWarehouseRoot(GameObject root)
    {
        if (root == null) return false;

        // These are the complete authored environment roots in
        // Assets/SciFi Warehouse Kit/Demo/Scene/SciFi_Warehouse.unity.
        // Do not replace this with selected child folders such as Floor Tiles.
        return root.name == "Floors & Floor Props"
            || root.name == "Walls"
            || root.name == "Ceilings"
            || root.name == "Corridors";
    }

    private static void CleanupCopiedDemoHierarchy(GameObject root)
    {
        if (root == null) return;

        foreach (Camera cam in root.GetComponentsInChildren<Camera>(true))
            if (cam != null) Object.DestroyImmediate(cam.gameObject);
        foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
            if (listener != null) Object.DestroyImmediate(listener);

        MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = behaviours.Length - 1; i >= 0; i--)
        {
            MonoBehaviour b = behaviours[i];
            if (b == null) continue;
            string typeName = b.GetType().Name;
            if (typeName == "PlayerMovement" || typeName == "MouseComponent" || typeName == "PushPhysics")
                Object.DestroyImmediate(b);
        }

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = all.Length - 1; i >= 0; i--)
        {
            Transform t = all[i];
            if (t == null || t == root.transform) continue;
            string n = t.name.ToLowerInvariant();
            if (n.Contains("dust particles") || n.Contains("backgroundmusic") || n.Contains("groundcheck"))
                Object.DestroyImmediate(t.gameObject);
        }

        RemoveMissingScriptsRecursive(root);
    }

    private static int RemoveMissingScriptsRecursive(GameObject root)
    {
        if (root == null) return 0;
        int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
        for (int i = 0; i < root.transform.childCount; i++)
            removed += RemoveMissingScriptsRecursive(root.transform.GetChild(i).gameObject);
        return removed;
    }

    private static void AlignWarehouseRootToGrid(Transform modules)
    {
        if (modules == null) return;
        if (!TryGetRendererBounds(modules, out Bounds b)) return;

        Bounds walkableBounds = b;
        bool hasWalkableBounds = TryGetWalkableRendererBounds(modules, out walkableBounds);
        float groundY = hasWalkableBounds ? walkableBounds.min.y : b.min.y;

        Vector3 shift = new Vector3(-b.center.x, -groundY, -b.center.z);
        ShiftWarehouseContent(modules, shift);

        if (!TryGetRendererBounds(modules, out b)) return;
        hasWalkableBounds = TryGetWalkableRendererBounds(modules, out walkableBounds);
        groundY = hasWalkableBounds ? walkableBounds.min.y : b.min.y;
        float yCorrection = -groundY;
        Vector3 correction = new Vector3(
            Mathf.Round(b.center.x / ModuleSize) * ModuleSize - b.center.x,
            yCorrection,
            Mathf.Round(b.center.z / ModuleSize) * ModuleSize - b.center.z);
        ShiftWarehouseContent(modules, correction);
        modules.localPosition = Vector3.zero;
        modules.localRotation = Quaternion.identity;
        modules.localScale = Vector3.one;

        Debug.Log($"[SciFiArena] Demo warehouse aligned as one root: modulesLocal={modules.localPosition}, boundsSize={b.size}, walkableGroundY={groundY:0.00}, grid={ModuleSize}m");
    }

    private static void ShiftWarehouseContent(Transform modules, Vector3 worldShift)
    {
        if (modules == null || worldShift == Vector3.zero)
            return;

        for (int i = 0; i < modules.childCount; i++)
        {
            Transform root = modules.GetChild(i);
            for (int c = 0; c < root.childCount; c++)
                root.GetChild(c).position += worldShift;
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;
            root.localScale = Vector3.one;
        }
    }

    private static void LogSciFiArenaValidation(Transform root, Transform modules, Transform proxyRoot, Transform playerSpawn)
    {
        TryGetRendererBounds(modules, out Bounds moduleBounds);
        TryGetColliderBounds(proxyRoot, out Bounds proxyBounds);
        Vector3 spawnPosition = playerSpawn != null ? playerSpawn.position : Vector3.positiveInfinity;
        Bounds expandedProxy = proxyBounds;
        expandedProxy.Expand(new Vector3(2f, 4f, 2f));
        bool spawnInsideProxy = playerSpawn != null && expandedProxy.Contains(spawnPosition);
        Debug.Log($"[SciFiArenaValidation] modules center={moduleBounds.center} size={moduleBounds.size}");
        Debug.Log($"[SciFiArenaValidation] proxies center={proxyBounds.center} size={proxyBounds.size}");
        Debug.Log($"[SciFiArenaValidation] PlayerSpawn world={spawnPosition} insideProxyBounds={spawnInsideProxy}");
        if (root != null)
            Debug.Log($"[SciFiArenaValidation] root localPosition={root.localPosition} localRotation={root.localRotation.eulerAngles} localScale={root.localScale}");
    }

    private static bool TryGetColliderBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        if (root == null) return false;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || c.isTrigger) continue;
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

    private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || r is ParticleSystemRenderer) continue;
            if (!hasBounds)
            {
                bounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        return hasBounds;
    }

    private static bool TryGetWalkableRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        if (root == null) return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (!IsNavMeshProxyCandidate(r))
                continue;

            if (!hasBounds)
            {
                bounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        return hasBounds;
    }

    private static bool EnforceWarehousePrefabPipeline(Transform root)
    {
        EnsureHierarchyActive(root);
        EnsureWarehouseColliders(root);
        EnforceDoubleSidedWallAndRoofMaterials(root);
        WakeAndBakeWarehouseColliders(root);
        if (!ValidateWarehouseColliderCoverage(root, out string validationError))
        {
            Debug.LogError("[SciFiArenaBuilder] Collider validation failed: " + validationError);
            return false;
        }
        return true;
    }

    private static void EnsureHierarchyActive(Transform root)
    {
        if (root == null) return;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null)
                all[i].gameObject.SetActive(true);
    }

    private static void EnsureWarehouseColliders(Transform root)
    {
        MeshCollider[] existingMeshColliders = root.GetComponentsInChildren<MeshCollider>(true);
        for (int i = existingMeshColliders.Length - 1; i >= 0; i--)
        {
            MeshCollider mc = existingMeshColliders[i];
            if (mc == null || mc.sharedMesh == null || mc.sharedMesh.isReadable)
                continue;

            GameObject go = mc.gameObject;
            Vector3 center = mc.sharedMesh.bounds.center;
            Vector3 size = mc.sharedMesh.bounds.size;
            Object.DestroyImmediate(mc);

            BoxCollider box = go.GetComponent<BoxCollider>();
            if (box == null)
                box = go.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
            box.enabled = true;
            box.isTrigger = false;
        }

        MeshFilter[] meshes = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshes.Length; i++)
        {
            MeshFilter mf = meshes[i];
            if (mf == null || mf.sharedMesh == null)
                continue;

            Collider existing = mf.GetComponent<Collider>();
            if (existing is MeshCollider existingMesh)
            {
                if (existingMesh.sharedMesh == null)
                    existingMesh.sharedMesh = mf.sharedMesh;
                existingMesh.convex = false;
                existingMesh.enabled = true;
                existingMesh.isTrigger = false;
                continue;
            }

            if (existing is BoxCollider existingBox)
            {
                if (existingBox.size == Vector3.zero)
                    existingBox.size = mf.sharedMesh.bounds.size;
                existingBox.enabled = true;
                existingBox.isTrigger = false;
                continue;
            }

            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            if (mf.sharedMesh.isReadable)
            {
                MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                mc.enabled = true;
                mc.isTrigger = false;
            }
            else
            {
                BoxCollider box = mf.gameObject.AddComponent<BoxCollider>();
                box.center = mf.sharedMesh.bounds.center;
                box.size = mf.sharedMesh.bounds.size;
                box.enabled = true;
                box.isTrigger = false;
            }
        }
    }

    private static void EnforceDoubleSidedWallAndRoofMaterials(Transform root)
    {
        if (root == null) return;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                continue;
            if (!IsWallOrRoofSegment(renderer.transform))
                continue;

            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int m = 0; m < materials.Length; m++)
            {
                Material mat = materials[m];
                if (!CanForceDoubleSided(mat))
                    continue;
                if (mat.HasProperty("_Cull"))
                {
                    mat.SetFloat("_Cull", (float)CullMode.Off);
                    changed = true;
                }
                if (mat.HasProperty("_CullMode"))
                {
                    mat.SetFloat("_CullMode", (float)CullMode.Off);
                    changed = true;
                }
                if (mat.HasProperty("_DoubleSidedEnable"))
                {
                    mat.SetFloat("_DoubleSidedEnable", 1f);
                    changed = true;
                }
                mat.doubleSidedGI = true;
                EditorUtility.SetDirty(mat);
            }
            if (changed)
                EditorUtility.SetDirty(renderer);
        }
    }

    private static bool CanForceDoubleSided(Material mat)
    {
        if (mat == null) return false;
        if (mat.renderQueue >= (int)RenderQueue.AlphaTest) return false;
        if (mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f) return false;
        if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f) return false;
        if (mat.HasProperty("_Mode") && mat.GetFloat("_Mode") >= 1f) return false;
        string name = mat.name.ToLowerInvariant();
        return !name.Contains("glass") && !name.Contains("fence") && !name.Contains("grid") && !name.Contains("wire");
    }

    private static void WakeAndBakeWarehouseColliders(Transform root)
    {
        if (root == null) return;
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null) continue;
            col.gameObject.SetActive(true);
            col.enabled = true;
            col.isTrigger = false;
            MeshCollider meshCollider = col as MeshCollider;
            if (meshCollider != null && meshCollider.sharedMesh != null)
                Physics.BakeMesh(meshCollider.sharedMesh.GetInstanceID(), false);
        }
    }

    private static bool IsPrefabCurrentAndValid()
    {
        if (!File.Exists(OutputPrefab)) return false;
        if (!File.Exists(VersionFile)) return false;
        if (!int.TryParse(File.ReadAllText(VersionFile).Trim(), out int v) || v != BuilderVersion) return false;
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(OutputPrefab);
            return root != null && ValidateWarehousePrefab(root.transform, out _);
        }
        finally
        {
            if (root != null)
                PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static bool ValidateWarehousePrefab(Transform root, out string error)
    {
        error = null;
        if (root == null)
        {
            error = "missing prefab root";
            return false;
        }
        Transform modules = root.Find("DemoWarehouseModules");
        if (modules == null)
        {
            error = "missing DemoWarehouseModules";
            return false;
        }
        if (!ValidateWarehouseColliderCoverage(modules, out error))
            return false;
        Transform proxyRoot = root.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot == null)
        {
            error = "missing SciFiNavMeshProxyColliders";
            return false;
        }
        BoxCollider[] boxes = proxyRoot.GetComponentsInChildren<BoxCollider>(true);
        int activeBoxes = 0;
        for (int i = 0; i < boxes.Length; i++)
            if (boxes[i] != null && boxes[i].enabled && !boxes[i].isTrigger && boxes[i].gameObject.activeInHierarchy)
                activeBoxes++;
        if (activeBoxes <= 0)
        {
            error = "no active NavMesh proxy BoxColliders";
            return false;
        }
        return true;
    }

    private static bool ValidateWarehouseColliderCoverage(Transform root, out string error)
    {
        error = null;
        if (root == null)
        {
            error = "missing warehouse root";
            return false;
        }
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        int required = 0;
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            if (!IsColliderRequiredWarehouseModule(mf.transform))
                continue;
            required++;
            if (!HasActiveMeshOrBoxCollider(mf.gameObject))
            {
                error = "missing active MeshCollider or BoxCollider on " + GetHierarchyPath(mf.transform);
                return false;
            }
        }
        if (required <= 0)
        {
            error = "no floor, catwalk, or wall modules were detected";
            return false;
        }
        return true;
    }

    private static bool HasActiveMeshOrBoxCollider(GameObject go)
    {
        if (go == null || !go.activeInHierarchy) return false;
        Collider[] colliders = go.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || !col.enabled || col.isTrigger)
                continue;
            MeshCollider meshCollider = col as MeshCollider;
            if (meshCollider != null && meshCollider.sharedMesh != null)
                return true;
            if (col is BoxCollider)
                return true;
        }
        return false;
    }

    private static bool IsColliderRequiredWarehouseModule(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("floor") || n.Contains("catwalk") || n.Contains("wall"))
                return !n.Contains("light") && !n.Contains("decal") && !n.Contains("sign");
        }
        return false;
    }

    private static bool IsWallOrRoofSegment(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("wall") || n.Contains("roof") || n.Contains("ceiling"))
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

    private static int BuildNavMeshProxyColliders(Transform root, Transform warehouseRoot)
    {
        if (root == null || warehouseRoot == null)
            return 0;

        Transform existing = root.Find("SciFiNavMeshProxyColliders");
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        Transform proxyRoot = MakeChild(root, "SciFiNavMeshProxyColliders");
        int envLayer = LayerMask.NameToLayer("Environment");
        if (envLayer >= 0) proxyRoot.gameObject.layer = envLayer;
        proxyRoot.gameObject.isStatic = true;
        int proxyCount = 0;

        Renderer[] renderers = warehouseRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsNavMeshProxyCandidate(renderer))
                continue;

            Bounds b = renderer.bounds;
            GameObject proxy = new GameObject("NavMeshProxy_" + renderer.name);
            proxy.transform.SetParent(proxyRoot, false);
            proxy.transform.position = new Vector3(b.center.x, b.max.y - 0.04f, b.center.z);
            proxy.transform.rotation = Quaternion.identity;
            proxy.transform.localScale = Vector3.one;
            if (envLayer >= 0) proxy.layer = envLayer;
            proxy.isStatic = true;

            BoxCollider box = proxy.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = new Vector3(Mathf.Max(0.5f, b.size.x), 0.08f, Mathf.Max(0.5f, b.size.z));

            NavMeshModifier modifier = proxy.AddComponent<NavMeshModifier>();
            modifier.ignoreFromBuild = false;

            proxyCount++;
        }

        Debug.Log($"[SciFiArenaBuilder] Generated {proxyCount} NavMesh proxy collider(s) under SciFiNavMeshProxyColliders.");
        return proxyCount;
    }

    private static bool IsNavMeshProxyCandidate(Renderer renderer)
    {
        if (renderer == null || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
            return false;

        string objectName = renderer.name.ToLowerInvariant();
        bool underFloorTiles = false;
        bool underCatwalks = false;
        bool underCorridors = false;
        for (Transform t = renderer.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n == "floor tiles" || n == "floors & floor props")
                underFloorTiles = true;
            if (n.Contains("catwalk"))
                underCatwalks = true;
            if (n == "corridors")
                underCorridors = true;

            if (n.Contains("rail") || n.Contains("railing") || n.Contains("wall")
                || n.Contains("ceiling") || n.Contains("roof") || n.Contains("pipe")
                || n.Contains("duct") || n.Contains("light") || n.Contains("beam")
                || n.Contains("pillar") || n.Contains("support") || n.Contains("door")
                || n.Contains("shelf") || n.Contains("crate") || n.Contains("barrel")
                || n.Contains("pallet") || n.Contains("misc props")
                || n.Contains("spawnpoints") || n.Contains("camera") || n.Contains("audio")
                || n.Contains("dust") || n.Contains("particle"))
                return false;
        }

        if (underFloorTiles && (objectName.Contains("floor") || objectName.Contains("ground")))
            return true;

        if (underCatwalks && !objectName.Contains("rail") && !objectName.Contains("pillar")
            && (objectName.Contains("catwalk") || objectName.Contains("walkway")
                || objectName.Contains("stair") || objectName.Contains("step")
                || objectName.Contains("platform")))
            return true;

        if (underCorridors && (objectName.Contains("floor") || objectName.Contains("ground")
            || objectName.Contains("corridor") || objectName.Contains("stair")
            || objectName.Contains("step") || objectName.Contains("platform")))
            return true;

        Bounds b = renderer.bounds;
        float horizontal = Mathf.Max(b.size.x, b.size.z);
        return (underFloorTiles || underCatwalks || underCorridors)
            && b.center.y <= 6.5f
            && horizontal >= 2.5f
            && b.size.y <= Mathf.Max(0.6f, horizontal * 0.08f)
            && !objectName.Contains("trim")
            && !objectName.Contains("side");
    }

    private static void MarkWarehouseStatic(Transform root)
    {
        int envLayer = LayerMask.NameToLayer("Environment");
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            GameObject go = all[i].gameObject;
            if (envLayer >= 0) go.layer = envLayer;
            go.isStatic = true;
            StaticEditorFlags flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic;
            GameObjectUtility.SetStaticEditorFlags(go, flags);
        }
    }

    // ---------------- Floors / Ceiling ----------------

    private static void BuildFloors(Transform parent, KitRefs k)
    {
        GameObject[] variants = { k.floor, k.floor2 ?? k.floor, k.floor3 ?? k.floor, k.floor4 ?? k.floor };
        float origin = -Half;
        for (int x = 0; x < GridSize; x++)
        for (int z = 0; z < GridSize; z++)
        {
            // Use a darker / accented tile for room interiors when available so rooms read differently.
            bool inRoom = (Mathf.Abs(origin + x * ModuleSize + ModuleSize * 0.5f) > RoomEdge) &&
                          (Mathf.Abs(origin + z * ModuleSize + ModuleSize * 0.5f) > RoomEdge);
            GameObject prefab = inRoom ? variants[(x + z) % variants.Length] : k.floor;
            Spawn(prefab, parent, new Vector3(origin + x * ModuleSize, 0f, origin + z * ModuleSize),
                Quaternion.identity, $"Floor_{x}_{z}");
        }
    }

    // Visible ceiling Y in world space. Cap occupies y in [SealedCeilingY - 0.15, +0.15].
    // All overhead props are placed BELOW this so they don't get hidden inside the cap.
    public const float SealedCeilingY = 8.7f;
    public const float OverheadPropY  = 8.45f;     // hanging lights / sprinklers / vents
    public const float OverheadDuctY  = 8.0f;      // duct trunks (slightly lower for layering)

    private static void BuildCeiling(Transform parent, KitRefs k)
    {
        float footprint = GridSize * ModuleSize;
        float overhang  = 6f;
        float capThickness = 0.5f;
        float capSize = footprint + overhang * 2f;
        float capY = SealedCeilingY;
        float tileY = 8.35f;

        Material ceilingMat = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/SciFi Warehouse Kit/Art/Materials/Ceiling Mat.mat");
        if (ceilingMat == null)
            ceilingMat = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/SciFi Warehouse Kit/Art/Materials/Floor Tile Mat.mat");

        GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cap.name = "Ceiling_SealedCap";
        cap.transform.SetParent(parent, false);
        cap.transform.localPosition = new Vector3(0f, capY, 0f);
        cap.transform.localScale = new Vector3(capSize, capThickness, capSize);
        if (ceilingMat != null)
            cap.GetComponent<MeshRenderer>().sharedMaterial = ceilingMat;

        GameObject skirtN = GameObject.CreatePrimitive(PrimitiveType.Cube);
        skirtN.name = "Ceiling_SkirtN";
        skirtN.transform.SetParent(parent, false);
        skirtN.transform.localPosition = new Vector3(0f, capY - 0.6f, Half + 0.3f);
        skirtN.transform.localScale = new Vector3(capSize, 1.2f, 0.6f);
        if (ceilingMat != null) skirtN.GetComponent<MeshRenderer>().sharedMaterial = ceilingMat;

        GameObject skirtS = GameObject.CreatePrimitive(PrimitiveType.Cube);
        skirtS.name = "Ceiling_SkirtS";
        skirtS.transform.SetParent(parent, false);
        skirtS.transform.localPosition = new Vector3(0f, capY - 0.6f, -Half - 0.3f);
        skirtS.transform.localScale = new Vector3(capSize, 1.2f, 0.6f);
        if (ceilingMat != null) skirtS.GetComponent<MeshRenderer>().sharedMaterial = ceilingMat;

        GameObject skirtE = GameObject.CreatePrimitive(PrimitiveType.Cube);
        skirtE.name = "Ceiling_SkirtE";
        skirtE.transform.SetParent(parent, false);
        skirtE.transform.localPosition = new Vector3(Half + 0.3f, capY - 0.6f, 0f);
        skirtE.transform.localScale = new Vector3(0.6f, 1.2f, capSize);
        if (ceilingMat != null) skirtE.GetComponent<MeshRenderer>().sharedMaterial = ceilingMat;

        GameObject skirtW = GameObject.CreatePrimitive(PrimitiveType.Cube);
        skirtW.name = "Ceiling_SkirtW";
        skirtW.transform.SetParent(parent, false);
        skirtW.transform.localPosition = new Vector3(-Half - 0.3f, capY - 0.6f, 0f);
        skirtW.transform.localScale = new Vector3(0.6f, 1.2f, capSize);
        if (ceilingMat != null) skirtW.GetComponent<MeshRenderer>().sharedMaterial = ceilingMat;

        if (k.ceilingClosed != null)
        {
            float origin = -Half;
            for (int x = 0; x < GridSize; x++)
            for (int z = 0; z < GridSize; z++)
            {
                GameObject tile = Spawn(k.ceilingClosed, parent,
                    new Vector3(origin + x * ModuleSize, tileY, origin + z * ModuleSize),
                    Quaternion.identity, $"Ceiling_Tile_{x}_{z}");
                if (tile != null)
                {
                    Unity.AI.Navigation.NavMeshModifier mod = tile.GetComponent<Unity.AI.Navigation.NavMeshModifier>();
                    if (mod == null) mod = tile.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
                    mod.ignoreFromBuild = true;
                }
            }
        }

        Vector3 capMin = cap.transform.localPosition - cap.transform.localScale * 0.5f;
        Vector3 capMax = cap.transform.localPosition + cap.transform.localScale * 0.5f;
        Debug.Log($"[SciFiArena] Ceiling built: capY={capY:0.00} size={capSize:0.0}x{capSize:0.0} thickness={capThickness:0.00} " +
                  $"tileY={tileY:0.00} tiles={GridSize * GridSize} skirts=4 capMin={capMin} capMax={capMax}");
    }

    // ---------------- Perimeter Walls ----------------

    private static void BuildPerimeterWalls(Transform wallParent, Transform doorParent, KitRefs k)
    {
        // Doors at center segment of each side (index 4 of 9).
        const int doorIndex = 4;

        for (int i = 0; i < GridSize; i++)
        {
            float along = -Half + i * ModuleSize;
            bool isDoor = i == doorIndex;

            // South wall
            PlaceWallOrDoor(wallParent, doorParent, k,
                new Vector3(along, 0f, -Half), Yaw(90f), isDoor, $"South_{i}");
            // North wall
            PlaceWallOrDoor(wallParent, doorParent, k,
                new Vector3(along + ModuleSize, 0f, Half), Yaw(270f), isDoor, $"North_{i}");
            // East wall
            PlaceWallOrDoor(wallParent, doorParent, k,
                new Vector3(Half, 0f, along), Yaw(0f), isDoor, $"East_{i}");
            // West wall
            PlaceWallOrDoor(wallParent, doorParent, k,
                new Vector3(-Half, 0f, along + ModuleSize), Yaw(180f), isDoor, $"West_{i}");
        }
    }

    private static void PlaceWallOrDoor(Transform wallParent, Transform doorParent, KitRefs k,
        Vector3 pos, Quaternion rot, bool isDoor, string label)
    {
        GameObject prefab = isDoor ? k.wallDoor : k.wallPlain;
        Transform parent = isDoor ? doorParent : wallParent;
        GameObject wall = Spawn(prefab, parent, pos, rot, (isDoor ? "WallDoor_" : "Wall_") + label);
        if (isDoor && wall != null) AttachSlidingDoor(wall, k.doorMat, slide: 1.9f);
    }

    private static void AttachSlidingDoor(GameObject wallDoor, Material doorMat, float slide)
    {
        GameObject pivot = new GameObject("SlidingDoor");
        pivot.transform.SetParent(wallDoor.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 0.05f, ModuleSize * 0.5f);
        pivot.transform.localRotation = Quaternion.identity;

        GameObject left = GameObject.CreatePrimitive(PrimitiveType.Cube);
        left.name = "LeftPanel";
        left.transform.SetParent(pivot.transform, false);
        left.transform.localPosition = new Vector3(0f, 2.5f, -0.7f);
        left.transform.localScale = new Vector3(0.12f, 4.6f, 1.4f);

        GameObject right = GameObject.CreatePrimitive(PrimitiveType.Cube);
        right.name = "RightPanel";
        right.transform.SetParent(pivot.transform, false);
        right.transform.localPosition = new Vector3(0f, 2.5f, 0.7f);
        right.transform.localScale = new Vector3(0.12f, 4.6f, 1.4f);

        if (doorMat != null)
        {
            left.GetComponent<MeshRenderer>().sharedMaterial = doorMat;
            right.GetComponent<MeshRenderer>().sharedMaterial = doorMat;
        }

        Unity.AI.Navigation.NavMeshModifier mLeft = left.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
        mLeft.ignoreFromBuild = true;
        Unity.AI.Navigation.NavMeshModifier mRight = right.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
        mRight.ignoreFromBuild = true;

        Unity.AI.Navigation.NavMeshLink link = wallDoor.AddComponent<Unity.AI.Navigation.NavMeshLink>();
        link.startPoint = new Vector3( 0.85f, 0.05f, 3f);
        link.endPoint   = new Vector3(-0.85f, 0.05f, 3f);
        link.width = 1.2f;
        link.costModifier = -1f;
        link.bidirectional = true;
        link.area = 0;
        link.autoUpdate = false;

        SphereCollider proxSphere = pivot.AddComponent<SphereCollider>();
        proxSphere.isTrigger = true;
        proxSphere.radius = 3.0f;
        proxSphere.center = new Vector3(0f, 1.0f, 0f);

        Rigidbody pivotRb = pivot.AddComponent<Rigidbody>();
        pivotRb.isKinematic = true;
        pivotRb.useGravity = false;

        GameObject canvasGO = new GameObject("DoorPromptCanvas");
        canvasGO.transform.SetParent(pivot.transform, false);
        canvasGO.transform.localPosition = new Vector3(0f, 5.4f, 0f);
        canvasGO.transform.localRotation = Quaternion.identity;
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 4200;
        RectTransform canvasRT = canvasGO.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(400f, 80f);
        canvasRT.localScale = Vector3.one * 0.01f;

        GameObject bgGO = new GameObject("Bg");
        bgGO.transform.SetParent(canvasGO.transform, false);
        UnityEngine.UI.Image bg = bgGO.AddComponent<UnityEngine.UI.Image>();
        bg.color = new Color(0.05f, 0.07f, 0.12f, 0.78f);
        RectTransform bgRT = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        GameObject txtGO = new GameObject("PromptText");
        txtGO.transform.SetParent(canvasGO.transform, false);
        TMPro.TextMeshProUGUI tmp = txtGO.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.text = "[E] / CLICK OPEN DOOR";
        tmp.fontSize = 42f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = new Color(0.94f, 0.96f, 1f, 1f);
        RectTransform tmpRT = txtGO.GetComponent<RectTransform>();
        tmpRT.anchorMin = Vector2.zero;
        tmpRT.anchorMax = Vector2.one;
        tmpRT.offsetMin = Vector2.zero;
        tmpRT.offsetMax = Vector2.zero;

        canvasGO.SetActive(false);

        SciFiSlidingDoor d = pivot.AddComponent<SciFiSlidingDoor>();
        d.leftPanel = left.transform;
        d.rightPanel = right.transform;
        d.panelSlide = slide;
        d.interactiveToggle = true;
    }

    // ---------------- Interior Room Walls ----------------

    // Layout: 4 corner rooms separated from the central hall by L-shaped wall runs.
    // Each room's facing wall has the middle segment as a sliding door for access.
    //
    // Wall coords reference:
    //   Walls aligned along +X: rotation 90°, pos.x is the cell's negative-X corner, pos.z = wall line.
    //   Walls aligned along +Z: rotation 0°,  pos.z is the cell's negative-Z corner, pos.x = wall line.
    //
    // Rooms (cell ranges 0..8):
    //   SW Maintenance: cells x[0..2], z[0..2]  → walls at x=-9 (z:-27..-9) and z=-9 (x:-27..-9)
    //   SE Loading:     cells x[6..8], z[0..2]  → walls at x= 9 (z:-27..-9) and z=-9 (x: 9..27)
    //   NW Control:     cells x[0..2], z[6..8]  → walls at x=-9 (z: 9..27) and z= 9 (x:-27..-9)
    //   NE Storage:     cells x[6..8], z[6..8]  → walls at x= 9 (z: 9..27) and z= 9 (x: 9..27)

    private static void BuildInteriorWalls(Transform wallParent, Transform doorParent, KitRefs k)
    {
        // SW Maintenance — door in north wall, window panel in east wall.
        AddXWallRun(wallParent, doorParent, k, zLine: -RoomEdge, xStart: -Half, segments: 3,
            doorSegment: 1, faceYaw: 90f, label: "SW_North", asDoor: true);
        AddZWallRun(wallParent, doorParent, k, xLine: -RoomEdge, zStart: -Half, segments: 3,
            doorSegment: -1, faceYaw: 0f, label: "SW_East", asDoor: false, windowSegment: 1);

        // SE Loading Bay — wide bay door on north (towards central hall), plain east-interior wall.
        AddXWallRun(wallParent, doorParent, k, zLine: -RoomEdge, xStart: RoomEdge, segments: 3,
            doorSegment: 1, faceYaw: 90f, label: "SE_North", asDoor: true);
        AddZWallRun(wallParent, doorParent, k, xLine: RoomEdge, zStart: -Half, segments: 3,
            doorSegment: -1, faceYaw: 180f, label: "SE_West", asDoor: false, windowSegment: 1);

        // NW Control — door + window for visibility into main hall.
        AddXWallRun(wallParent, doorParent, k, zLine: RoomEdge, xStart: -Half, segments: 3,
            doorSegment: 1, faceYaw: 270f, label: "NW_South", asDoor: true, windowSegment: 0);
        AddZWallRun(wallParent, doorParent, k, xLine: -RoomEdge, zStart: RoomEdge, segments: 3,
            doorSegment: -1, faceYaw: 0f, label: "NW_East", asDoor: false, windowSegment: 2);

        // NE Storage — door + window.
        AddXWallRun(wallParent, doorParent, k, zLine: RoomEdge, xStart: RoomEdge, segments: 3,
            doorSegment: 1, faceYaw: 270f, label: "NE_South", asDoor: true);
        AddZWallRun(wallParent, doorParent, k, xLine: RoomEdge, zStart: RoomEdge, segments: 3,
            doorSegment: -1, faceYaw: 180f, label: "NE_West", asDoor: false, windowSegment: 1);
    }

    private static void AddXWallRun(Transform wallParent, Transform doorParent, KitRefs k,
        float zLine, float xStart, int segments,
        int doorSegment, float faceYaw, string label, bool asDoor, int windowSegment = -99)
    {
        for (int i = 0; i < segments; i++)
        {
            Vector3 pos = new Vector3(xStart + i * ModuleSize, 0f, zLine);
            Quaternion rot = Yaw(faceYaw);
            GameObject prefab;
            Transform parent;
            string nm;
            if (asDoor && i == doorSegment)
            {
                prefab = k.wallDoor; parent = doorParent; nm = "Door_" + label + "_" + i;
            }
            else if (windowSegment >= 0 && i == windowSegment && k.wallWindow != null)
            {
                prefab = k.wallWindow; parent = wallParent; nm = "Window_" + label + "_" + i;
            }
            else
            {
                prefab = k.wallPlain; parent = wallParent; nm = "Wall_" + label + "_" + i;
            }
            GameObject seg = Spawn(prefab, parent, pos, rot, nm);
            if (asDoor && i == doorSegment && seg != null)
                AttachSlidingDoor(seg, k.doorMat, slide: 1.9f);
        }
    }

    private static void AddZWallRun(Transform wallParent, Transform doorParent, KitRefs k,
        float xLine, float zStart, int segments,
        int doorSegment, float faceYaw, string label, bool asDoor, int windowSegment = -99)
    {
        for (int i = 0; i < segments; i++)
        {
            Vector3 pos = new Vector3(xLine, 0f, zStart + i * ModuleSize);
            Quaternion rot = Yaw(faceYaw);
            GameObject prefab;
            Transform parent;
            string nm;
            if (asDoor && i == doorSegment)
            {
                prefab = k.wallDoor; parent = doorParent; nm = "Door_" + label + "_" + i;
            }
            else if (windowSegment >= 0 && i == windowSegment && k.wallWindow != null)
            {
                prefab = k.wallWindow; parent = wallParent; nm = "Window_" + label + "_" + i;
            }
            else
            {
                prefab = k.wallPlain; parent = wallParent; nm = "Wall_" + label + "_" + i;
            }
            GameObject seg = Spawn(prefab, parent, pos, rot, nm);
            if (asDoor && i == doorSegment && seg != null)
                AttachSlidingDoor(seg, k.doorMat, slide: 1.9f);
        }
    }

    /// <summary>
    /// Partitions the central hall from the four corridor strips that lead to the
    /// perimeter doors. Each cardinal side gets two plain wall flanks plus a sliding
    /// door at the center — the hall reads as a discrete combat room connected to
    /// corridors, not one big square.
    /// </summary>
    private static void BuildHallPartitions(Transform wallParent, Transform doorParent, KitRefs k)
    {
        // Each side has 3 segments (each 6m): flank, door (center), flank.
        // Hall edge is at +-RoomEdge (=9). Flanks occupy outer 6m on each end, door is the middle 6m.
        //
        // Convention recap:
        //   X-aligned walls (running along +X): rot = faceYaw, pos.x is segment left edge, pos.z = line.
        //   Z-aligned walls (running along +Z): rot = faceYaw, pos.z is segment near edge,  pos.x = line.

        // South side (faces north into hall, line z = -RoomEdge)
        PlaceSegX(wallParent, doorParent, k, x: -RoomEdge,            z: -RoomEdge, yaw: 90f,  isDoor: false, label: "HallS_W");
        PlaceSegX(wallParent, doorParent, k, x: -ModuleSize * 0.5f,   z: -RoomEdge, yaw: 90f,  isDoor: true,  label: "HallS_Door");
        PlaceSegX(wallParent, doorParent, k, x:  ModuleSize * 0.5f,   z: -RoomEdge, yaw: 90f,  isDoor: false, label: "HallS_E");

        // North side (faces south, line z = +RoomEdge)
        PlaceSegX(wallParent, doorParent, k, x: -RoomEdge + ModuleSize,           z:  RoomEdge, yaw: 270f, isDoor: false, label: "HallN_W");
        PlaceSegX(wallParent, doorParent, k, x:  ModuleSize * 0.5f,               z:  RoomEdge, yaw: 270f, isDoor: true,  label: "HallN_Door");
        PlaceSegX(wallParent, doorParent, k, x:  RoomEdge,                        z:  RoomEdge, yaw: 270f, isDoor: false, label: "HallN_E");

        // East side (faces west, line x = +RoomEdge, rot 180° extends -Z from placement)
        PlaceSegZ(wallParent, doorParent, k, x:  RoomEdge, z: -ModuleSize * 0.5f, yaw: 180f, isDoor: false, label: "HallE_S");
        PlaceSegZ(wallParent, doorParent, k, x:  RoomEdge, z:  ModuleSize * 0.5f, yaw: 180f, isDoor: true,  label: "HallE_Door");
        PlaceSegZ(wallParent, doorParent, k, x:  RoomEdge, z:  RoomEdge,          yaw: 180f, isDoor: false, label: "HallE_N");

        // West side (faces east, line x = -RoomEdge, rot 0° extends +Z from placement)
        PlaceSegZ(wallParent, doorParent, k, x: -RoomEdge, z: -RoomEdge,          yaw: 0f, isDoor: false, label: "HallW_S");
        PlaceSegZ(wallParent, doorParent, k, x: -RoomEdge, z: -ModuleSize * 0.5f, yaw: 0f, isDoor: true,  label: "HallW_Door");
        PlaceSegZ(wallParent, doorParent, k, x: -RoomEdge, z:  ModuleSize * 0.5f, yaw: 0f, isDoor: false, label: "HallW_N");
    }

    private static void PlaceSegX(Transform wallParent, Transform doorParent, KitRefs k,
        float x, float z, float yaw, bool isDoor, string label)
    {
        GameObject prefab = isDoor ? k.wallDoor : k.wallPlain;
        Transform parent = isDoor ? doorParent : wallParent;
        GameObject seg = Spawn(prefab, parent, new Vector3(x, 0f, z), Yaw(yaw),
            (isDoor ? "Door_" : "Wall_") + label);
        if (isDoor && seg != null) AttachSlidingDoor(seg, k.doorMat, slide: 1.9f);
    }

    private static void PlaceSegZ(Transform wallParent, Transform doorParent, KitRefs k,
        float x, float z, float yaw, bool isDoor, string label)
    {
        GameObject prefab = isDoor ? k.wallDoor : k.wallPlain;
        Transform parent = isDoor ? doorParent : wallParent;
        GameObject seg = Spawn(prefab, parent, new Vector3(x, 0f, z), Yaw(yaw),
            (isDoor ? "Door_" : "Wall_") + label);
        if (isDoor && seg != null) AttachSlidingDoor(seg, k.doorMat, slide: 1.9f);
    }

    // ---------------- Structural Pillars ----------------

    private static void BuildStructure(Transform parent, KitRefs k)
    {
        // Corner pillars at the four inner corners where rooms meet the hall.
        if (k.pillar != null)
        {
            Spawn(k.pillar, parent, new Vector3(-RoomEdge, 0f, -RoomEdge), Quaternion.identity, "Pillar_SW");
            Spawn(k.pillar, parent, new Vector3( RoomEdge, 0f, -RoomEdge), Quaternion.identity, "Pillar_SE");
            Spawn(k.pillar, parent, new Vector3(-RoomEdge, 0f,  RoomEdge), Quaternion.identity, "Pillar_NW");
            Spawn(k.pillar, parent, new Vector3( RoomEdge, 0f,  RoomEdge), Quaternion.identity, "Pillar_NE");

            // Outer corners
            Spawn(k.pillar, parent, new Vector3(-Half + 0.6f, 0f, -Half + 0.6f), Quaternion.identity, "Pillar_OuterSW");
            Spawn(k.pillar, parent, new Vector3( Half - 0.6f, 0f, -Half + 0.6f), Quaternion.identity, "Pillar_OuterSE");
            Spawn(k.pillar, parent, new Vector3(-Half + 0.6f, 0f,  Half - 0.6f), Quaternion.identity, "Pillar_OuterNW");
            Spawn(k.pillar, parent, new Vector3( Half - 0.6f, 0f,  Half - 0.6f), Quaternion.identity, "Pillar_OuterNE");
        }

        // Wall supports along long perimeter runs (every other panel) — vertical I-beam look.
        if (k.wallSupport != null)
        {
            for (int i = 1; i < GridSize; i += 2)
            {
                float along = -Half + i * ModuleSize;
                Spawn(k.wallSupport, parent, new Vector3(along, 0f, -Half + 0.2f), Yaw(0f),   $"WS_S_{i}");
                Spawn(k.wallSupport, parent, new Vector3(along, 0f,  Half - 0.2f), Yaw(180f), $"WS_N_{i}");
                Spawn(k.wallSupport, parent, new Vector3(-Half + 0.2f, 0f, along), Yaw(90f),  $"WS_W_{i}");
                Spawn(k.wallSupport, parent, new Vector3( Half - 0.2f, 0f, along), Yaw(270f), $"WS_E_{i}");
            }
        }
    }

    // ---------------- Ceiling Structure (beams, pipes, ducts overhead) ----------------

    private static void BuildCeilingStructure(Transform parent, KitRefs k)
    {
        // Ceiling supports above the central hall and intersections.
        if (k.ceilingSupport != null)
        {
            Vector3[] supportPositions =
            {
                new Vector3(-RoomEdge, 0f, -RoomEdge),
                new Vector3( RoomEdge, 0f, -RoomEdge),
                new Vector3(-RoomEdge, 0f,  RoomEdge),
                new Vector3( RoomEdge, 0f,  RoomEdge),
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 0f, -RoomEdge),
                new Vector3(0f, 0f,  RoomEdge),
                new Vector3(-RoomEdge, 0f, 0f),
                new Vector3( RoomEdge, 0f, 0f),
            };
            for (int i = 0; i < supportPositions.Length; i++)
                Spawn(k.ceilingSupport, parent, supportPositions[i], Quaternion.identity, $"CeilSupport_{i}");
        }

        // Overhead duct trunks running east-west across the central hall.
        if (k.ductStraight != null)
        {
            float y = OverheadDuctY;
            for (int i = 0; i < 5; i++)
            {
                float x = -12f + i * 6f;
                Spawn(k.ductStraight, parent, new Vector3(x, y, -3f),  Yaw(90f), $"Duct_Trunk1_{i}");
                Spawn(k.ductStraight, parent, new Vector3(x, y,  3f),  Yaw(90f), $"Duct_Trunk2_{i}");
            }
            if (k.ductElbow01 != null)
            {
                Spawn(k.ductElbow01, parent, new Vector3(-18f, y, -3f), Yaw(180f), "Duct_Elbow_W1");
                Spawn(k.ductElbow02 ?? k.ductElbow01, parent, new Vector3(18f, y,  3f), Yaw(0f), "Duct_Elbow_E2");
            }
            if (k.ductTee != null)
                Spawn(k.ductTee, parent, new Vector3(0f, y, 0f), Yaw(0f), "Duct_Tee_Center");
            if (k.ductVent != null)
            {
                Spawn(k.ductVent, parent, new Vector3( 6f, y, -3f), Yaw(90f),  "DuctVent_E");
                Spawn(k.ductVent, parent, new Vector3(-6f, y,  3f), Yaw(270f), "DuctVent_W");
            }
        }

        // Pipe runs along the upper perimeter wall faces (gives walls vertical detail).
        if (k.pipesAll != null)
        {
            float pipeY = 0f; // pipes prefabs are wall-mounted, anchor at floor and extend up.
            int idx = 0;
            for (int i = 0; i < GridSize; i++)
            {
                if (i == 4) continue; // skip door segment
                if (i % 2 != 0) continue;
                float along = -Half + i * ModuleSize + ModuleSize * 0.5f;
                GameObject p1 = k.pipesAll[(idx++) % k.pipesAll.Length];
                GameObject p2 = k.pipesAll[(idx++) % k.pipesAll.Length];
                GameObject p3 = k.pipesAll[(idx++) % k.pipesAll.Length];
                GameObject p4 = k.pipesAll[(idx++) % k.pipesAll.Length];
                if (p1 != null) Spawn(p1, parent, new Vector3(along, pipeY, -Half + 0.05f), Yaw(0f),   $"Pipes_S_{i}");
                if (p2 != null) Spawn(p2, parent, new Vector3(along, pipeY,  Half - 0.05f), Yaw(180f), $"Pipes_N_{i}");
                if (p3 != null) Spawn(p3, parent, new Vector3(-Half + 0.05f, pipeY, along), Yaw(90f),  $"Pipes_W_{i}");
                if (p4 != null) Spawn(p4, parent, new Vector3( Half - 0.05f, pipeY, along), Yaw(270f), $"Pipes_E_{i}");
            }
        }

        // Hanging cables (thin black cubes) from ceiling — environmental detail.
        Material cableMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/SciFi Warehouse Kit/Art/Materials/Bay Door Mat.mat");
        Vector3[] cableSpots =
        {
            new Vector3(-3f, 0f, -6f), new Vector3( 4f, 0f, -2f),
            new Vector3( 2f, 0f,  5f), new Vector3(-5f, 0f,  4f),
        };
        for (int i = 0; i < cableSpots.Length; i++)
        {
            GameObject cable = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cable.name = $"Cable_{i}";
            cable.transform.SetParent(parent, false);
            cable.transform.localPosition = new Vector3(cableSpots[i].x, CeilingY - 1.5f, cableSpots[i].z);
            cable.transform.localScale = new Vector3(0.05f, 2.6f, 0.05f);
            if (cableMat != null) cable.GetComponent<MeshRenderer>().sharedMaterial = cableMat;
            Object.DestroyImmediate(cable.GetComponent<Collider>());
        }
    }

    // ---------------- Central Hall Cover (broken sightlines) ----------------

    private static void BuildCentralCover(Transform parent, KitRefs k)
    {
        // L-shaped cover clusters offset from center so the hall isn't an open square.
        // Cluster A — SW of center
        Spawn(k.crateLong,  parent, new Vector3(-4f, 0f, -4f), Yaw(20f),  "C_A_Long");
        Spawn(k.crateShort, parent, new Vector3(-5.5f, 0f, -2.5f), Yaw(0f), "C_A_Short");
        Spawn(k.crateShort, parent, new Vector3(-5.5f, 1.4f, -2.5f), Yaw(15f), "C_A_ShortStack");
        Spawn(k.barrel,     parent, new Vector3(-3f, 0f, -2f), Quaternion.identity, "C_A_Bar1");

        // Cluster B — NE of center, lower silhouette (pallets)
        Spawn(k.pallet,     parent, new Vector3( 4f, 0f, 4f),  Yaw(15f), "C_B_Pal1");
        Spawn(k.pallet,     parent, new Vector3( 5.5f, 0f, 5.5f), Yaw(-25f), "C_B_Pal2");
        Spawn(k.crateShort, parent, new Vector3( 4f, 0.15f, 4f), Yaw(15f), "C_B_Top");
        Spawn(k.barrel,     parent, new Vector3( 3f, 0f, 6f),  Quaternion.identity, "C_B_Bar1");
        Spawn(k.barrel,     parent, new Vector3( 5f, 0f, 7f),  Quaternion.identity, "C_B_Bar2");

        // Cluster C — NW of center (tall cover wall via stacked crates)
        Spawn(k.crateLong, parent, new Vector3(-5f, 0f, 5f), Yaw(90f), "C_C_Long1");
        Spawn(k.crateLong, parent, new Vector3(-5f, 1.6f, 5f), Yaw(90f), "C_C_Long2");
        Spawn(k.crateShort, parent, new Vector3(-3f, 0f, 6f), Yaw(0f),  "C_C_S1");

        // Cluster D — SE of center, "machinery" feel: barrels + cart
        if (k.cart != null) Spawn(k.cart, parent, new Vector3(5.5f, 0f, -5f), Yaw(135f), "C_D_Cart");
        Spawn(k.barrel, parent, new Vector3(4f, 0f, -6f), Quaternion.identity, "C_D_Bar1");
        Spawn(k.barrel, parent, new Vector3(3f, 0f, -7f), Quaternion.identity, "C_D_Bar2");
        Spawn(k.crateShort, parent, new Vector3(2f, 0f, -5.5f), Yaw(20f), "C_D_S1");

        // Catwalk pillars in inner corners to anchor verticality (decorative; the catwalk is overhead).
        if (k.catwalkPillar01 != null)
        {
            Spawn(k.catwalkPillar01, parent, new Vector3(-6f, 0f, -6f), Quaternion.identity, "CatPillar_SW");
            Spawn(k.catwalkPillar02 ?? k.catwalkPillar01, parent, new Vector3( 6f, 0f, -6f), Quaternion.identity, "CatPillar_SE");
            Spawn(k.catwalkPillar03 ?? k.catwalkPillar01, parent, new Vector3(-6f, 0f,  6f), Quaternion.identity, "CatPillar_NW");
            Spawn(k.catwalkPillar01, parent, new Vector3( 6f, 0f,  6f), Quaternion.identity, "CatPillar_NE");
        }

        // Overhead catwalk forming a perimeter ring inside the hall.
        // DECORATIVE ONLY — raised near the ceiling and colliders stripped so the player
        // and NavMesh treat them as background detail (no fake reachable platforms).
        if (k.catwalkLong != null)
        {
            float y = 7.0f;  // near ceiling (9m) — clearly background, not a play space
            SpawnDeco(k.catwalkLong, parent, new Vector3(-3f, y,  6f), Yaw(0f),   "Catwalk_N1");
            SpawnDeco(k.catwalkLong, parent, new Vector3( 3f, y,  6f), Yaw(0f),   "Catwalk_N2");
            SpawnDeco(k.catwalkLong, parent, new Vector3(-3f, y, -6f), Yaw(0f),   "Catwalk_S1");
            SpawnDeco(k.catwalkLong, parent, new Vector3( 3f, y, -6f), Yaw(0f),   "Catwalk_S2");
            SpawnDeco(k.catwalkLong, parent, new Vector3( 6f, y, -3f), Yaw(90f),  "Catwalk_E1");
            SpawnDeco(k.catwalkLong, parent, new Vector3( 6f, y,  3f), Yaw(90f),  "Catwalk_E2");
            SpawnDeco(k.catwalkLong, parent, new Vector3(-6f, y, -3f), Yaw(90f),  "Catwalk_W1");
            SpawnDeco(k.catwalkLong, parent, new Vector3(-6f, y,  3f), Yaw(90f),  "Catwalk_W2");
            if (k.catwalkRailsLong != null)
            {
                SpawnDeco(k.catwalkRailsLong, parent, new Vector3(-3f, y,  6f), Yaw(0f),  "Rails_N1");
                SpawnDeco(k.catwalkRailsLong, parent, new Vector3( 3f, y,  6f), Yaw(0f),  "Rails_N2");
                SpawnDeco(k.catwalkRailsLong, parent, new Vector3(-3f, y, -6f), Yaw(180f),"Rails_S1");
                SpawnDeco(k.catwalkRailsLong, parent, new Vector3( 3f, y, -6f), Yaw(180f),"Rails_S2");
            }
        }
    }

    // ---------------- Rooms ----------------

    private static Vector3 RC(float x, float z) => new Vector3(x, 0f, z); // shorthand for room coord

    private static void BuildStorageRoom(Transform room, KitRefs k)
    {
        // NE room footprint: x in [+9, +27], z in [+9, +27]
        // Two long aisles of shelves running east-west, with crates between.
        Quaternion faceSouth = Yaw(180f);
        Quaternion faceNorth = Yaw(0f);
        Quaternion faceEast  = Yaw(90f);

        // North shelf row (against north wall)
        if (k.shelf01 != null) Spawn(k.shelf01,    room, RC(13f, 25.5f), faceSouth, "Shelf_N1");
        if (k.shelf02 != null) Spawn(k.shelf02,    room, RC(17f, 25.5f), faceSouth, "Shelf_N2");
        if (k.shelf03 != null) Spawn(k.shelf03,    room, RC(21f, 25.5f), faceSouth, "Shelf_N3");
        if (k.shelf04 != null) Spawn(k.shelf04,    room, RC(25f, 25.5f), faceSouth, "Shelf_N4");

        // East shelf column (against east wall)
        if (k.shelf05 != null) Spawn(k.shelf05,    room, RC(25.5f, 13f), faceEast, "Shelf_E1");
        if (k.shelf06 != null) Spawn(k.shelf06,    room, RC(25.5f, 17f), faceEast, "Shelf_E2");
        if (k.shelfEmpty != null) Spawn(k.shelfEmpty, room, RC(25.5f, 21f), faceEast, "Shelf_E3");

        // Center island — back-to-back shelves, cover-friendly
        if (k.shelf01 != null) Spawn(k.shelf01, room, RC(15f, 17f), faceNorth, "ShelfMid_N");
        if (k.shelf02 != null) Spawn(k.shelf02, room, RC(15f, 18.5f), faceSouth, "ShelfMid_S");

        // Crates stacked
        Spawn(k.crateLong,  room, RC(20f, 14f), Yaw(90f), "Crate_Long1");
        Spawn(k.crateLong,  room, RC(20f, 14f) + Vector3.up * 1.5f, Yaw(95f), "Crate_Long2");
        Spawn(k.crateShort, room, RC(22f, 12f), Yaw(15f), "Crate_S1");
        Spawn(k.crateShort, room, RC(13f, 13f), Yaw(-10f), "Crate_S2");
        Spawn(k.crateShort, room, RC(13f, 13f) + Vector3.up * 1.4f, Yaw(20f), "Crate_S2_Stack");
        Spawn(k.barrel,     room, RC(18f, 13f), Quaternion.identity, "Barrel1");
        Spawn(k.barrel,     room, RC(18.7f, 13.5f), Quaternion.identity, "Barrel2");

        // Fusebox & extinguisher on walls
        if (k.fusebox01 != null) Spawn(k.fusebox01, room, RC(11f, 26.6f) + Vector3.up * 2.5f, faceSouth, "Fusebox1");
        if (k.fusebox02 != null) Spawn(k.fusebox02, room, RC(26.6f, 25f) + Vector3.up * 2.5f, faceEast,  "Fusebox2");
        if (k.fireExt != null) Spawn(k.fireExt, room, RC(11f, 26.4f), faceSouth, "FireExt");
        if (k.exitSign != null) Spawn(k.exitSign, room, RC(15f, 9.2f) + Vector3.up * 4.5f, faceNorth, "ExitSign");
    }

    private static void BuildControlRoom(Transform room, KitRefs k)
    {
        // NW room footprint: x in [-27, -9], z in [+9, +27]
        // Maintenance/control feel: machinery, fuseboxes, a workbench (cart), barrels.
        Quaternion faceSouth = Yaw(180f);
        Quaternion faceEast  = Yaw(90f);
        Quaternion faceWest  = Yaw(270f);

        // Workbench cluster
        if (k.cart != null) Spawn(k.cart, room, RC(-20f, 20f), Yaw(15f), "Cart_Workbench");
        Spawn(k.crateShort, room, RC(-22f, 17f), Yaw(20f), "Crate_S1");
        Spawn(k.crateLong,  room, RC(-15f, 11f), Yaw(0f),  "Crate_L1");
        Spawn(k.barrel,     room, RC(-13f, 13f), Quaternion.identity, "Barrel1");
        Spawn(k.barrel,     room, RC(-13f, 15f), Quaternion.identity, "Barrel2");

        // Empty shelves as "stripped console rack"
        if (k.shelfEmpty != null)
        {
            Spawn(k.shelfEmpty, room, RC(-25.5f, 20f), faceEast, "Rack1");
            Spawn(k.shelfEmpty, room, RC(-25.5f, 24f), faceEast, "Rack2");
        }

        // Wall fuseboxes (control panels)
        if (k.fusebox01 != null) Spawn(k.fusebox01, room, RC(-26.6f, 12f) + Vector3.up * 2.5f, faceEast, "Panel1");
        if (k.fusebox02 != null) Spawn(k.fusebox02, room, RC(-26.6f, 16f) + Vector3.up * 2.5f, faceEast, "Panel2");
        if (k.fusebox01 != null) Spawn(k.fusebox01, room, RC(-22f, 26.6f) + Vector3.up * 2.5f, faceSouth, "Panel3");

        // Pipes on west wall
        if (k.pipesAll != null && k.pipesAll[0] != null)
            Spawn(k.pipesAll[0], room, RC(-26.6f, 18f), faceEast, "Pipes_W");
        if (k.pipesAll != null && k.pipesAll[3] != null)
            Spawn(k.pipesAll[3], room, RC(-26.6f, 24f), faceEast, "Pipes_W2");

        // Ceiling vents
        if (k.ductVent != null)
        {
            Spawn(k.ductVent, room, RC(-20f, 22f) + Vector3.up * OverheadPropY, Yaw(0f),  "Vent1");
            Spawn(k.ductVent, room, RC(-15f, 16f) + Vector3.up * OverheadPropY, Yaw(90f), "Vent2");
        }
        if (k.exitSign != null) Spawn(k.exitSign, room, RC(-15f, 9.2f) + Vector3.up * 4.5f, Yaw(0f), "ExitSign");
        if (k.fireExt != null) Spawn(k.fireExt, room, RC(-26.4f, 22f), faceEast, "FireExt");
    }

    private static void BuildLoadingBay(Transform room, KitRefs k)
    {
        // SE room footprint: x in [+9, +27], z in [-27, -9]
        // Open-ish loading bay: lots of stacked pallets, crates, a cart, barrel cluster.
        Quaternion faceNorth = Yaw(0f);
        Quaternion faceEast  = Yaw(90f);

        // Pallet stacks
        GameObject pal = k.pallet;
        if (k.palletVariants != null && k.palletVariants.Length > 0 && k.palletVariants[0] != null) pal = k.palletVariants[0];
        Spawn(pal, room, RC(13f, -25f), Yaw(0f),  "Pal_A1");
        Spawn(pal, room, RC(17f, -25f), Yaw(0f),  "Pal_A2");
        Spawn(pal, room, RC(21f, -25f), Yaw(0f),  "Pal_A3");
        Spawn(pal, room, RC(25f, -25f), Yaw(0f),  "Pal_A4");

        // Stacked crates on pallets
        Spawn(k.crateLong,  room, RC(13f, -25f) + Vector3.up * 0.15f, Yaw(0f),  "Pal_A1_Crate");
        Spawn(k.crateLong,  room, RC(21f, -25f) + Vector3.up * 0.15f, Yaw(0f),  "Pal_A3_Crate");
        Spawn(k.crateShort, room, RC(17f, -25f) + Vector3.up * 0.15f, Yaw(10f), "Pal_A2_Crate");
        Spawn(k.crateShort, room, RC(25f, -25f) + Vector3.up * 0.15f, Yaw(-12f), "Pal_A4_Crate");
        Spawn(k.crateShort, room, RC(25f, -25f) + Vector3.up * 1.55f, Yaw(20f), "Pal_A4_Crate2");

        // Cart with crate
        if (k.cart != null) Spawn(k.cart, room, RC(20f, -14f), Yaw(120f), "Cart");
        Spawn(k.crateShort, room, RC(20f, -14f) + Vector3.up * 0.6f, Yaw(120f), "Cart_Crate");

        // Barrel cluster
        Spawn(k.barrel, room, RC(25f, -12f), Quaternion.identity, "Bar1");
        Spawn(k.barrel, room, RC(25f, -14f), Quaternion.identity, "Bar2");
        Spawn(k.barrel, room, RC(23f, -13f), Quaternion.identity, "Bar3");

        // Loose shelves
        if (k.shelf03 != null) Spawn(k.shelf03, room, RC(11.5f, -15f), faceEast, "Shelf1");
        if (k.shelf04 != null) Spawn(k.shelf04, room, RC(11.5f, -19f), faceEast, "Shelf2");

        // Garbage bin in corner
        if (k.garbageBin != null) Spawn(k.garbageBin, room, RC(14f, -11f), Yaw(35f), "Bin");

        // Wall fixtures
        if (k.fusebox02 != null) Spawn(k.fusebox02, room, RC(26.6f, -18f) + Vector3.up * 2.5f, faceEast, "Fusebox");
        if (k.exitSign != null) Spawn(k.exitSign, room, RC(15f, -9.2f) + Vector3.up * 4.5f, Yaw(180f), "ExitSign");
        if (k.fireExt != null) Spawn(k.fireExt, room, RC(26.4f, -22f), faceEast, "FireExt");
    }

    private static void BuildMaintenanceRoom(Transform room, KitRefs k)
    {
        // SW room footprint: x in [-27, -9], z in [-27, -9]
        // Pipes, ducts, fuseboxes, barrels — utility room.
        Quaternion faceNorth = Yaw(0f);
        Quaternion faceWest  = Yaw(270f);

        // Pipes covering the south + west walls heavily
        if (k.pipesAll != null)
        {
            if (k.pipesAll[0] != null) Spawn(k.pipesAll[0], room, RC(-22f, -26.6f), faceNorth, "Pipes_S1");
            if (k.pipesAll[1] != null) Spawn(k.pipesAll[1], room, RC(-14f, -26.6f), faceNorth, "Pipes_S2");
            if (k.pipesAll[2] != null) Spawn(k.pipesAll[2], room, RC(-26.6f, -22f), Yaw(90f),  "Pipes_W1");
            if (k.pipesAll[3] != null) Spawn(k.pipesAll[3], room, RC(-26.6f, -14f), Yaw(90f),  "Pipes_W2");
        }

        // Ceiling ducts traversing the room
        if (k.ductStraight != null)
        {
            float y = OverheadDuctY;
            Spawn(k.ductStraight, room, RC(-22f, -18f) + Vector3.up * y, Yaw(0f),  "Duct1");
            Spawn(k.ductStraight, room, RC(-22f, -12f) + Vector3.up * y, Yaw(0f),  "Duct2");
            Spawn(k.ductStraight, room, RC(-18f, -22f) + Vector3.up * y, Yaw(90f), "Duct3");
            if (k.ductElbow01 != null)
                Spawn(k.ductElbow01, room, RC(-22f, -24f) + Vector3.up * y, Yaw(0f), "Duct_Elbow");
        }

        // Barrels (lots — this is a chemical/utility area)
        for (int i = 0; i < 6; i++)
        {
            float bx = -24f + (i % 3) * 1.6f;
            float bz = -22f + (i / 3) * 1.7f;
            Spawn(k.barrel, room, RC(bx, bz), Quaternion.Euler(0f, i * 37f, 0f), $"Barrel_{i}");
        }

        // Crates corner
        Spawn(k.crateLong,  room, RC(-13f, -22f), Yaw(0f), "Crate_L1");
        Spawn(k.crateShort, room, RC(-13f, -25f), Yaw(15f), "Crate_S1");
        Spawn(k.crateShort, room, RC(-15f, -13f), Yaw(-20f), "Crate_S2");

        // Empty shelves as tool racks
        if (k.shelfEmpty != null) Spawn(k.shelfEmpty, room, RC(-11.5f, -16f), Yaw(90f), "ToolRack1");
        if (k.shelfEmpty != null) Spawn(k.shelfEmpty, room, RC(-11.5f, -20f), Yaw(90f), "ToolRack2");

        // Wall fuseboxes (the "electrical room" feel)
        if (k.fusebox01 != null) Spawn(k.fusebox01, room, RC(-26.6f, -12f) + Vector3.up * 2.5f, Yaw(90f),  "Fusebox1");
        if (k.fusebox02 != null) Spawn(k.fusebox02, room, RC(-26.6f, -16f) + Vector3.up * 2.5f, Yaw(90f),  "Fusebox2");
        if (k.fusebox01 != null) Spawn(k.fusebox01, room, RC(-18f, -26.6f) + Vector3.up * 2.5f, Yaw(0f),   "Fusebox3");

        if (k.garbageBin != null) Spawn(k.garbageBin, room, RC(-12f, -12f), Yaw(-25f), "Bin");
        if (k.fireExt != null) Spawn(k.fireExt, room, RC(-26.4f, -20f), Yaw(90f), "FireExt");
        if (k.exitSign != null) Spawn(k.exitSign, room, RC(-15f, -9.2f) + Vector3.up * 4.5f, Yaw(180f), "ExitSign");
    }

    // ---------------- Ambient (signs, cams, sprinklers, etc) ----------------

    private static void BuildAmbient(Transform parent, KitRefs k)
    {
        // Exit signs above perimeter doors
        if (k.exitSign != null)
        {
            Spawn(k.exitSign, parent, new Vector3(0f, 4.6f, -Half + 0.1f), Yaw(0f),   "Exit_S");
            Spawn(k.exitSign, parent, new Vector3(0f, 4.6f,  Half - 0.1f), Yaw(180f), "Exit_N");
            Spawn(k.exitSign, parent, new Vector3( Half - 0.1f, 4.6f, 0f), Yaw(-90f), "Exit_E");
            Spawn(k.exitSign, parent, new Vector3(-Half + 0.1f, 4.6f, 0f), Yaw(90f),  "Exit_W");
        }

        // Security cams in upper corners
        if (k.securityCam != null)
        {
            Spawn(k.securityCam, parent, new Vector3(-Half + 1.2f, 7.8f, -Half + 1.2f), Yaw(-45f),  "Cam_SW");
            Spawn(k.securityCam, parent, new Vector3( Half - 1.2f, 7.8f, -Half + 1.2f), Yaw(-135f), "Cam_SE");
            Spawn(k.securityCam, parent, new Vector3(-Half + 1.2f, 7.8f,  Half - 1.2f), Yaw(45f),   "Cam_NW");
            Spawn(k.securityCam, parent, new Vector3( Half - 1.2f, 7.8f,  Half - 1.2f), Yaw(135f),  "Cam_NE");
            // Central hall cams
            Spawn(k.securityCam, parent, new Vector3(-RoomEdge + 0.5f, 7.5f, 0f), Yaw(90f), "Cam_HallW");
            Spawn(k.securityCam, parent, new Vector3( RoomEdge - 0.5f, 7.5f, 0f), Yaw(-90f), "Cam_HallE");
        }

        // Sprinklers — varied prefabs scattered across central hall + rooms
        if (k.sprinklers != null && k.sprinklers.Length > 0)
        {
            Vector3[] spots =
            {
                new Vector3(-6f, 0f, -6f), new Vector3( 6f, 0f, -6f),
                new Vector3(-6f, 0f,  6f), new Vector3( 6f, 0f,  6f),
                new Vector3( 0f, 0f, -3f), new Vector3( 0f, 0f,  3f),
                new Vector3(-18f, 0f, 18f), new Vector3( 18f, 0f, 18f),
                new Vector3(-18f, 0f,-18f), new Vector3( 18f, 0f,-18f),
                new Vector3(-15f, 0f, 14f), new Vector3( 15f, 0f, 14f),
                new Vector3(-15f, 0f,-14f), new Vector3( 15f, 0f,-14f),
            };
            for (int i = 0; i < spots.Length; i++)
            {
                GameObject sp = k.sprinklers[i % k.sprinklers.Length];
                if (sp == null) continue;
                Spawn(sp, parent, spots[i] + Vector3.up * OverheadPropY, Quaternion.identity, $"Sprinkler_{i}");
            }
        }

        // Wall fans for ambient detail near corridor doors
        if (k.wallFan != null)
        {
            Spawn(k.wallFan, parent, new Vector3(-Half + 0.1f, 5.5f, -3f), Yaw(90f),  "Fan_W");
            Spawn(k.wallFan, parent, new Vector3( Half - 0.1f, 5.5f,  3f), Yaw(-90f), "Fan_E");
        }
    }

    // ---------------- Lighting (real Light components for atmosphere) ----------------

    private static void BuildLighting(Transform parent, KitRefs k)
    {
        // Cool overhead hanging lights — wide point lights for the central hall.
        Vector3[] hangPositions =
        {
            new Vector3(  0f, 0f,   0f),
            new Vector3(-12f, 0f,  12f),
            new Vector3( 12f, 0f, -12f),
            new Vector3(-12f, 0f, -12f),
            new Vector3( 12f, 0f,  12f),
            new Vector3(  0f, 0f,  18f),
            new Vector3(  0f, 0f, -18f),
            new Vector3( 18f, 0f,   0f),
            new Vector3(-18f, 0f,   0f),
        };
        Color coolWhite = new Color(0.78f, 0.86f, 1.0f);
        for (int i = 0; i < hangPositions.Length; i++)
        {
            Vector3 p = hangPositions[i];
            if (k.hangingLight != null)
                Spawn(k.hangingLight, parent, new Vector3(p.x, OverheadPropY, p.z), Quaternion.identity, $"HangFixture_{i}");
            CreatePointLight(parent, new Vector3(p.x, OverheadPropY - 0.7f, p.z), coolWhite, intensity: 2.4f, range: 14f, $"HangLight_{i}");
        }

        // Wall lights spaced along the perimeter — give walls vertical brightness gradients.
        if (k.wallLight != null)
        {
            Color warm = new Color(1.0f, 0.86f, 0.65f);
            int[] indices = { 1, 3, 5, 7 };
            for (int n = 0; n < indices.Length; n++)
            {
                int i = indices[n];
                float along = -Half + i * ModuleSize + ModuleSize * 0.5f;
                Spawn(k.wallLight, parent, new Vector3(-Half + 0.1f, 5.0f, along), Yaw(90f),  $"WL_W_{i}");
                Spawn(k.wallLight, parent, new Vector3( Half - 0.1f, 5.0f, along), Yaw(-90f), $"WL_E_{i}");
                Spawn(k.wallLight, parent, new Vector3(along, 5.0f, -Half + 0.1f), Yaw(0f),   $"WL_S_{i}");
                Spawn(k.wallLight, parent, new Vector3(along, 5.0f,  Half - 0.1f), Yaw(180f), $"WL_N_{i}");

                CreatePointLight(parent, new Vector3(-Half + 1.5f, 4.5f, along), warm, 1.4f, 8f, $"WLite_W_{i}");
                CreatePointLight(parent, new Vector3( Half - 1.5f, 4.5f, along), warm, 1.4f, 8f, $"WLite_E_{i}");
                CreatePointLight(parent, new Vector3(along, 4.5f, -Half + 1.5f), warm, 1.4f, 8f, $"WLite_S_{i}");
                CreatePointLight(parent, new Vector3(along, 4.5f,  Half - 1.5f), warm, 1.4f, 8f, $"WLite_N_{i}");
            }
        }

        // Red emergency accents above each perimeter door
        Color emergency = new Color(1.0f, 0.18f, 0.18f);
        CreateSpotLight(parent, new Vector3(0f, 5.5f, -Half + 1.5f), Vector3.down,   emergency, 3f, 7f, 70f, "Emerg_S");
        CreateSpotLight(parent, new Vector3(0f, 5.5f,  Half - 1.5f), Vector3.down,   emergency, 3f, 7f, 70f, "Emerg_N");
        CreateSpotLight(parent, new Vector3( Half - 1.5f, 5.5f, 0f), Vector3.down,   emergency, 3f, 7f, 70f, "Emerg_E");
        CreateSpotLight(parent, new Vector3(-Half + 1.5f, 5.5f, 0f), Vector3.down,   emergency, 3f, 7f, 70f, "Emerg_W");

        // Per-room key light (cool blue-tinted) at room center to differentiate rooms.
        Color roomKey = new Color(0.6f, 0.78f, 1.0f);
        CreatePointLight(parent, new Vector3( 18f, 5.5f,  18f), roomKey, 2.0f, 16f, "Room_NE_Key");
        CreatePointLight(parent, new Vector3(-18f, 5.5f,  18f), roomKey, 2.0f, 16f, "Room_NW_Key");
        CreatePointLight(parent, new Vector3( 18f, 5.5f, -18f), roomKey, 2.0f, 16f, "Room_SE_Key");
        CreatePointLight(parent, new Vector3(-18f, 5.5f, -18f), roomKey, 2.0f, 16f, "Room_SW_Key");

        // Subtle ambient fill from a single low-intensity point in the middle so corners aren't pitch black.
        CreatePointLight(parent, new Vector3(0f, 4.5f, 0f), new Color(0.55f, 0.62f, 0.78f), 1.2f, 35f, "Ambient_Fill");
    }

    private static void CreatePointLight(Transform parent, Vector3 pos, Color color, float intensity, float range, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.intensity = intensity;
        l.range = range;
        l.shadows = LightShadows.None;     // perf-friendly; many lights at runtime
        l.renderMode = LightRenderMode.Auto;
    }

    private static void CreateSpotLight(Transform parent, Vector3 pos, Vector3 dir, Color color, float intensity, float range, float angle, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.LookRotation(dir.sqrMagnitude > 0.001f ? dir : Vector3.down);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Spot;
        l.color = color;
        l.intensity = intensity;
        l.range = range;
        l.spotAngle = angle;
        l.shadows = LightShadows.None;
    }

    // ---------------- Spawn points (distributed across rooms + hall) ----------------

    private static void BuildSpawnPoints(Transform parent)
    {
        // Player spawn — central hall, south end (faces north toward the main fight).
        GameObject player = new GameObject("PlayerSpawn");
        player.transform.SetParent(parent, false);
        player.transform.localPosition = new Vector3(0f, 1f, -6f);

        // 16 enemy spawns spread across hall + each room interior.
        Vector3[] enemyPositions =
        {
            // Central hall ring
            new Vector3(  6f, 1f,   0f),
            new Vector3( -6f, 1f,   0f),
            new Vector3(  0f, 1f,   6f),
            new Vector3(  0f, 1f,  -6f),
            // NE Storage
            new Vector3( 18f, 1f,  20f),
            new Vector3( 22f, 1f,  15f),
            // NW Control
            new Vector3(-18f, 1f,  20f),
            new Vector3(-22f, 1f,  15f),
            // SE Loading
            new Vector3( 18f, 1f, -20f),
            new Vector3( 22f, 1f, -15f),
            // SW Maintenance
            new Vector3(-18f, 1f, -20f),
            new Vector3(-22f, 1f, -15f),
            // Hall corners
            new Vector3(  6f, 1f,   6f),
            new Vector3( -6f, 1f,   6f),
            new Vector3(  6f, 1f,  -6f),
            new Vector3( -6f, 1f,  -6f),
        };

        for (int i = 0; i < enemyPositions.Length; i++)
        {
            GameObject e = new GameObject($"EnemySpawn_{i:00}");
            e.transform.SetParent(parent, false);
            e.transform.localPosition = enemyPositions[i];
        }
    }

    private static void BuildSpawnPoints(Transform parent, Transform warehouseRoot)
    {
        if (warehouseRoot == null || !TryGetRendererBounds(warehouseRoot, out Bounds geometryBounds))
        {
            BuildSpawnPoints(parent);
            return;
        }

        Bounds walkableBounds = geometryBounds;
        bool hasWalkableBounds = TryGetWalkableRendererBounds(warehouseRoot, out walkableBounds);
        Bounds spawnBounds = hasWalkableBounds ? walkableBounds : geometryBounds;

        float insetX = Mathf.Min(6f, Mathf.Max(2f, spawnBounds.extents.x * 0.18f));
        float insetZ = Mathf.Min(6f, Mathf.Max(2f, spawnBounds.extents.z * 0.18f));
        float xMin = SnapToGrid(spawnBounds.min.x + insetX);
        float xMax = SnapToGrid(spawnBounds.max.x - insetX);
        float zMin = SnapToGrid(spawnBounds.min.z + insetZ);
        float zMax = SnapToGrid(spawnBounds.max.z - insetZ);
        float xMid = SnapToGrid(spawnBounds.center.x);
        float zMid = SnapToGrid(spawnBounds.center.z);
        float y = spawnBounds.min.y + 1f;

        GameObject player = new GameObject("PlayerSpawn");
        player.transform.SetParent(parent, false);
        player.transform.localPosition = parent.InverseTransformPoint(new Vector3(xMid, y, SnapToGrid(Mathf.Lerp(zMin, zMid, 0.5f))));

        Vector3[] enemyPositions =
        {
            new Vector3(xMid, y, zMid),
            new Vector3(xMin, y, zMid),
            new Vector3(xMax, y, zMid),
            new Vector3(xMid, y, zMin),
            new Vector3(xMid, y, zMax),
            new Vector3(xMin, y, zMin),
            new Vector3(xMax, y, zMin),
            new Vector3(xMin, y, zMax),
            new Vector3(xMax, y, zMax),
            new Vector3(SnapToGrid(Mathf.Lerp(xMin, xMid, 0.5f)), y, SnapToGrid(Mathf.Lerp(zMin, zMid, 0.5f))),
            new Vector3(SnapToGrid(Mathf.Lerp(xMax, xMid, 0.5f)), y, SnapToGrid(Mathf.Lerp(zMin, zMid, 0.5f))),
            new Vector3(SnapToGrid(Mathf.Lerp(xMin, xMid, 0.5f)), y, SnapToGrid(Mathf.Lerp(zMax, zMid, 0.5f))),
            new Vector3(SnapToGrid(Mathf.Lerp(xMax, xMid, 0.5f)), y, SnapToGrid(Mathf.Lerp(zMax, zMid, 0.5f))),
            new Vector3(xMin, y, SnapToGrid(Mathf.Lerp(zMin, zMax, 0.25f))),
            new Vector3(xMax, y, SnapToGrid(Mathf.Lerp(zMin, zMax, 0.25f))),
            new Vector3(xMid, y, SnapToGrid(Mathf.Lerp(zMin, zMax, 0.75f))),
        };

        for (int i = 0; i < enemyPositions.Length; i++)
        {
            GameObject e = new GameObject($"EnemySpawn_{i:00}");
            e.transform.SetParent(parent, false);
            e.transform.localPosition = parent.InverseTransformPoint(enemyPositions[i]);
        }
    }

    private static float SnapToGrid(float value)
    {
        return Mathf.Round(value / ModuleSize) * ModuleSize;
    }

    // ---------------- NavMesh + Minimap ----------------

    private static void AddNavMeshSurface(GameObject root)
    {
        Transform proxyRoot = root.transform.Find("SciFiNavMeshProxyColliders");
        GameObject surfaceRoot = proxyRoot != null ? proxyRoot.gameObject : root;
        NavMeshSurface surface = surfaceRoot.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = surfaceRoot.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
        surface.defaultArea = 0;
        surface.layerMask = ~0;
        surface.minRegionArea = 2f;
    }

    private static void ApplyMinimapLayer(Transform propsRoot)
    {
        int ignoreLayer = LayerMask.NameToLayer("IgnoreMinimap");
        if (ignoreLayer < 0 || propsRoot == null) return;
        foreach (Transform t in propsRoot.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = ignoreLayer;
    }
}
#endif
