using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Runtime safety pass for imported maps: static render meshes without a solid
/// collider get a non-convex MeshCollider so ragdolls and bodies cannot fall
/// through decorative floor or wall pieces.
/// </summary>
public static class MapRuntimeMeshColliderBaker
{
    private const string GeneratedColliderName = "RuntimeMapMeshCollider";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        BakeActiveScene();
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        BakeScene(scene);
    }

    private static void BakeActiveScene()
    {
        BakeScene(SceneManager.GetActiveScene());
    }

    private static void BakeScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null || ShouldSkipHierarchy(root.transform))
                continue;

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int j = 0; j < filters.Length; j++)
                EnsureMeshCollider(filters[j]);
        }
    }

    private static void EnsureMeshCollider(MeshFilter filter)
    {
        if (filter == null || filter.sharedMesh == null)
            return;

        GameObject go = filter.gameObject;
        if (go == null || ShouldSkipHierarchy(go.transform))
            return;

        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        if (renderer == null)
            return;

        if (HasSolidCollider(go))
            return;

        MeshCollider meshCollider = go.AddComponent<MeshCollider>();
        meshCollider.name = GeneratedColliderName;
        meshCollider.sharedMesh = filter.sharedMesh;
        meshCollider.convex = false;
        meshCollider.isTrigger = false;
    }

    private static bool HasSolidCollider(GameObject go)
    {
        Collider[] colliders = go.GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider != null && collider.enabled && !collider.isTrigger)
                return true;
        }

        return false;
    }

    private static bool ShouldSkipHierarchy(Transform transform)
    {
        if (transform == null)
            return true;

        if (transform.GetComponentInParent<Rigidbody>() != null)
            return true;
        if (transform.GetComponentInParent<Animator>() != null)
            return true;
        if (transform.GetComponentInParent<NavMeshAgent>() != null)
            return true;
        if (transform.GetComponentInParent<PlayerController>() != null)
            return true;
        if (transform.GetComponentInParent<EnemyController>() != null)
            return true;
        if (transform.GetComponentInParent<WeaponBase>() != null)
            return true;
        if (transform.GetComponentInParent<WeaponHitbox>() != null)
            return true;
        if (transform.GetComponentInParent<Camera>() != null)
            return true;
        if (transform.GetComponentInParent<Canvas>() != null)
            return true;

        string path = GetLowerHierarchyPath(transform);
        return ContainsPathSegment(path, "player")
            || ContainsPathSegment(path, "enemy")
            || ContainsPathSegment(path, "weapon")
            || ContainsPathSegment(path, "ragdoll")
            || ContainsPathSegment(path, "hitbox")
            || ContainsPathSegment(path, "minimap")
            || ContainsPathSegment(path, "ui")
            || ContainsPathSegment(path, "canvas");
    }

    private static string GetLowerHierarchyPath(Transform transform)
    {
        string path = transform.name.ToLowerInvariant();
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name.ToLowerInvariant() + "/" + path;
            parent = parent.parent;
        }

        return path;
    }

    private static bool ContainsPathSegment(string path, string segment)
    {
        if (path == segment)
            return true;

        string middle = "/" + segment + "/";
        return path.StartsWith(segment + "/")
            || path.EndsWith("/" + segment)
            || path.Contains(middle);
    }
}
