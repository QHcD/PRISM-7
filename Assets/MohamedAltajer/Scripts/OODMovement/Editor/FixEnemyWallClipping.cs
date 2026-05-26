using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

public static class FixEnemyWallClipping
{
    private static readonly string[] WallKeywords =
    {
        "wall", "barrier", "fence", "container", "crate",
        "pillar", "column", "beam", "girder", "frame",
        "door", "panel", "structure", "block"
    };

    [MenuItem("Tools/PRISM/Fix Enemy Wall Clipping")]
    public static void Run()
    {
        int wallsTagged = 0;
        int wallsCollidered = 0;
        int agentsEnforced = 0;
        int surfacesBuilt = 0;

        GameObject[] roots = UnityEngine.SceneManagement.SceneManager
            .GetActiveScene().GetRootGameObjects();

        foreach (GameObject root in roots)
        {
            foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null) continue;
                if (!LooksLikeWall(mr.gameObject.name)) continue;

                if (EnsureBoxColliderSolid(mr.gameObject)) wallsCollidered++;
                if (MarkNavigationStatic(mr.gameObject)) wallsTagged++;
            }
        }

        foreach (NavMeshAgent a in Object.FindObjectsByType<NavMeshAgent>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (a == null) continue;
            EnforceAgentSettings(a);
            EnsureAgentSolids(a.gameObject);
            EnsureEnemyEnforcerComponent(a.gameObject);
            agentsEnforced++;
        }

        foreach (NavMeshSurface s in Object.FindObjectsByType<NavMeshSurface>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s == null) continue;
            s.collectObjects = CollectObjects.All;
            s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            s.BuildNavMesh();
            EditorUtility.SetDirty(s);
            surfacesBuilt++;
        }

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[FixWallClip] walls tagged NavigationStatic={wallsTagged}");
        Debug.Log($"[FixWallClip] walls given solid BoxCollider={wallsCollidered}");
        Debug.Log($"[FixWallClip] NavMeshAgents enforced={agentsEnforced}");
        Debug.Log($"[FixWallClip] NavMeshSurfaces rebuilt={surfacesBuilt}");

        if (surfacesBuilt == 0)
        {
            Debug.LogWarning("[FixWallClip] No NavMeshSurface found in the scene. " +
                             "Add a 'NavMeshSurface' component to your arena root and re-run, " +
                             "or use Window → AI → Navigation → Bake.");
        }
    }

    private static bool LooksLikeWall(string nameLower)
    {
        nameLower = nameLower.ToLowerInvariant();
        for (int i = 0; i < WallKeywords.Length; i++)
            if (nameLower.Contains(WallKeywords[i])) return true;
        return false;
    }

    private static bool EnsureBoxColliderSolid(GameObject go)
    {
        bool changed = false;
        Collider any = go.GetComponent<Collider>();
        if (any == null)
        {
            BoxCollider box = Undo.AddComponent<BoxCollider>(go);
            box.isTrigger = false;
            changed = true;
        }
        else
        {
            foreach (Collider c in go.GetComponents<Collider>())
            {
                if (c.isTrigger)
                {
                    Undo.RecordObject(c, "Force wall collider solid");
                    c.isTrigger = false;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool MarkNavigationStatic(GameObject go)
    {
        StaticEditorFlags current = GameObjectUtility.GetStaticEditorFlags(go);
        StaticEditorFlags wanted = current | StaticEditorFlags.NavigationStatic | StaticEditorFlags.BatchingStatic;
        if (current == wanted) return false;
        Undo.RecordObject(go, "Mark wall Navigation Static");
        GameObjectUtility.SetStaticEditorFlags(go, wanted);

        GameObjectUtility.SetNavMeshArea(go, 1);
        return true;
    }

    private static void EnforceAgentSettings(NavMeshAgent a)
    {
        Undo.RecordObject(a, "Enforce NavMeshAgent settings");
        a.radius = Mathf.Max(0.3f, a.radius);
        a.height = Mathf.Max(1.6f, a.height);
        a.stoppingDistance = Mathf.Max(1.2f, a.stoppingDistance);
        a.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        a.autoBraking = true;
        a.autoRepath = true;
        a.updatePosition = true;
        a.updateRotation = true;
    }

    private static void EnsureAgentSolids(GameObject go)
    {
        Collider c = go.GetComponent<Collider>();
        if (c == null)
        {
            CapsuleCollider cap = Undo.AddComponent<CapsuleCollider>(go);
            cap.isTrigger = false;
            cap.radius = 0.4f;
            cap.height = 1.8f;
            cap.center = new Vector3(0f, 0.9f, 0f);
        }

        Rigidbody rb = go.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = Undo.AddComponent<Rigidbody>(go);
        }
        Undo.RecordObject(rb, "Enforce kinematic rigidbody on enemy");
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private static void EnsureEnemyEnforcerComponent(GameObject go)
    {
        if (go.GetComponent<EnemyAgentEnforcer>() == null)
            Undo.AddComponent<EnemyAgentEnforcer>(go);
    }
}
