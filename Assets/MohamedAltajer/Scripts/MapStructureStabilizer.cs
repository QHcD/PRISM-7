using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Fixes enclosed industrial building shells at runtime:
///   • Double-sided opaque materials so hangar walls/roofs stay visible from inside.
///   • Mesh-accurate colliders instead of loose prefab box bounds (walk-through walls).
/// Skips fences, lattice panels, and transparent/cutout props — those assets break when forced double-sided.
/// </summary>
public static class MapStructureStabilizer
{
    private static bool _logged;
    private static readonly Dictionary<Material, Material> DoubleSidedCache = new Dictionary<Material, Material>(128);

    public static void Install(Transform mapRoot, bool debugLog = false)
    {
        if (mapRoot == null) return;

        int materialsFixed = 0;
        int collidersUpgraded = 0;
        int collidersAdded = 0;

        MeshFilter[] filters = mapRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null) continue;

            MeshRenderer renderer = mf.GetComponent<MeshRenderer>();
            if (renderer != null && IsEnclosedBuildingShell(mf.gameObject))
            {
                if (ApplyDoubleSidedMaterials(renderer))
                    materialsFixed++;
            }

            if (ShouldEnsureStructureCollider(mf.gameObject)
                && TryEnsureStructureCollider(mf, out bool upgraded, out bool added))
            {
                if (upgraded) collidersUpgraded++;
                if (added) collidersAdded++;
            }
        }

        if (debugLog || !_logged)
        {
            _logged = true;
            Debug.Log(
                $"[MapStructure] Stabilized '{mapRoot.name}': doubleSided={materialsFixed}, " +
                $"collidersUpgraded={collidersUpgraded}, collidersAdded={collidersAdded}");
        }
    }

    private static bool ApplyDoubleSidedMaterials(Renderer renderer)
    {
        if (renderer == null) return false;

        Material[] shared = renderer.sharedMaterials;
        if (shared == null || shared.Length == 0) return false;

        bool changed = false;
        Material[] runtime = new Material[shared.Length];
        for (int i = 0; i < shared.Length; i++)
        {
            Material src = shared[i];
            if (src == null)
            {
                runtime[i] = null;
                continue;
            }

            Material dst = GetOrCreateDoubleSided(src);
            runtime[i] = dst;
            if (dst != src)
                changed = true;
        }

        if (changed)
            renderer.sharedMaterials = runtime;

        return changed;
    }

    private static Material GetOrCreateDoubleSided(Material source)
    {
        if (source == null || !IsSafeForDoubleSided(source))
            return source;

        if (IsDoubleSided(source))
            return source;

        if (DoubleSidedCache.TryGetValue(source, out Material cached) && cached != null)
            return cached;

        Material dup = Object.Instantiate(source);
        dup.name = source.name + "_DoubleSided";
        dup.hideFlags = HideFlags.DontSave;

        if (dup.HasProperty("_Cull"))
            dup.SetFloat("_Cull", (float)CullMode.Off);
        if (dup.HasProperty("_CullMode"))
            dup.SetFloat("_CullMode", (float)CullMode.Off);
        if (dup.HasProperty("_DoubleSidedEnable"))
            dup.SetFloat("_DoubleSidedEnable", 1f);
        dup.doubleSidedGI = true;

        DoubleSidedCache[source] = dup;
        return dup;
    }

    private static bool IsSafeForDoubleSided(Material mat)
    {
        if (mat == null) return false;

        // Transparent / cutout / fade materials shimmer or z-fight when forced double-sided.
        if (mat.renderQueue >= (int)RenderQueue.AlphaTest)
            return false;

        if (mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f)
            return false;

        if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f)
            return false;

        if (mat.HasProperty("_Mode") && mat.GetFloat("_Mode") >= 1f)
            return false;

        string shaderName = mat.shader != null ? mat.shader.name.ToLowerInvariant() : string.Empty;
        if (shaderName.Contains("transparent") || shaderName.Contains("fade"))
            return false;

        string matName = mat.name.ToLowerInvariant();
        if (matName.Contains("lattice") || matName.Contains("fence") || matName.Contains("grid")
            || matName.Contains("wire") || matName.Contains("glass"))
            return false;

        return true;
    }

    private static bool IsDoubleSided(Material mat)
    {
        if (mat == null) return false;
        if (mat.doubleSidedGI) return true;
        if (mat.HasProperty("_Cull") && mat.GetFloat("_Cull") <= 0.5f) return true;
        if (mat.HasProperty("_CullMode") && mat.GetFloat("_CullMode") <= 0.5f) return true;
        return false;
    }

    private static bool TryEnsureStructureCollider(MeshFilter mf, out bool upgraded, out bool added)
    {
        upgraded = false;
        added = false;

        if (mf == null || mf.sharedMesh == null)
            return false;

        if (ShouldSkipColliderObject(mf.gameObject))
            return false;

        Collider existing = mf.GetComponent<Collider>();
        if (existing != null && existing.isTrigger)
            return false;

        if (existing is BoxCollider && mf.sharedMesh.isReadable)
        {
            Object.Destroy(existing);
            existing = null;
            upgraded = true;
        }

        if (existing != null)
            return upgraded;

        if (mf.sharedMesh.isReadable)
        {
            MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;
            added = true;
        }
        else
        {
            BoxCollider box = mf.gameObject.AddComponent<BoxCollider>();
            box.center = mf.sharedMesh.bounds.center;
            box.size = mf.sharedMesh.bounds.size;
            added = true;
        }

        Collider col = mf.GetComponent<Collider>();
        if (col != null)
        {
            col.isTrigger = false;
            col.enabled = true;
            TagStructureLayer(mf.gameObject);
        }

        return upgraded || added;
    }

    private static bool IsEnclosedBuildingShell(GameObject go)
    {
        if (go == null) return false;
        string goLower = go.name.ToLowerInvariant();
        if (goLower.Contains("skybox") || goLower.Contains("reflection")
            || goLower.Contains("invisible") || goLower.Contains("trigger"))
            return false;

        for (Transform t = go.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("hangar") || n.Contains("building") || n.Contains("warehouse")
                || n.Contains("office") || n.Contains("shed") || n.Contains("hall")
                || n.Contains("roof") || n.Contains("ceiling") || n.Contains("module")
                || n.Contains("interior") || n.Contains("construction") || n.Contains("kit"))
                return true;
        }
        return false;
    }

    private static bool ShouldEnsureStructureCollider(GameObject go)
    {
        if (go == null) return false;
        string goLower = go.name.ToLowerInvariant();
        if (goLower.Contains("trigger") || goLower.Contains("skybox") || goLower.Contains("invisible"))
            return false;

        for (Transform t = go.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (IsOutdoorPropName(n)) return false;
            if (n.Contains("hangar") || n.Contains("building") || n.Contains("warehouse")
                || n.Contains("office") || n.Contains("shed") || n.Contains("hall")
                || n.Contains("structure") || n.Contains("roof") || n.Contains("ceiling")
                || n.Contains("wall") || n.Contains("concrete_wall") || n.Contains("module")
                || n.Contains("interior") || n.Contains("kit"))
                return true;
        }
        return false;
    }

    private static bool IsOutdoorPropName(string lowerName)
    {
        if (string.IsNullOrEmpty(lowerName)) return false;

        return lowerName.Contains("fence") || lowerName.Contains("lattice")
            || lowerName.Contains("railing") || lowerName.Contains("barrier")
            || lowerName.Contains("road_block") || lowerName.Contains("roadblock")
            || lowerName.Contains("container") || lowerName.Contains("cargo")
            || lowerName.Contains("silo") || lowerName.Contains("tank")
            || lowerName.Contains("barrel") || lowerName.Contains("pipe")
            || lowerName.Contains("wire") || lowerName.Contains("cable");
    }

    private static bool IsExcludedStructureName(string lowerName)
    {
        if (string.IsNullOrEmpty(lowerName)) return true;

        return lowerName.Contains("road") || lowerName.Contains("asphalt")
            || lowerName.Contains("ground") || lowerName.Contains("floor")
            || lowerName.Contains("pavement") || lowerName.Contains("street")
            || lowerName.Contains("skybox") || lowerName.Contains("reflection")
            || lowerName.Contains("invisible") || lowerName.Contains("trigger");
    }

    private static bool ShouldSkipColliderObject(GameObject go)
    {
        string n = go.name.ToLowerInvariant();
        if (IsOutdoorPropName(n)) return true;
        if (n.Contains("trigger")) return true;
        if (n.Contains("door") && !n.Contains("frame")) return true;
        if (n.Contains("gate") || n.Contains("garage") || n.Contains("shutter")) return true;
        return false;
    }

    private static void TagStructureLayer(GameObject go)
    {
        int env = LayerMask.NameToLayer("Environment");
        if (env < 0) env = LayerMask.NameToLayer("Map");
        if (env < 0) env = LayerMask.NameToLayer("Default");
        if (env < 0) return;

        SetLayerRecursive(go, env);
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursive(child.gameObject, layer);
    }
}
