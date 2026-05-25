using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class MapVisibilityStabilizer
{
    private static bool _logged;

    public static void Install(Transform mapRoot, bool debugLog = false)
    {
        if (mapRoot == null) return;

        int renderersFixed = 0;
        int lodDisabled = 0;
        int occlusionRemoved = 0;

        Renderer[] renderers = mapRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;

            r.forceRenderingOff = false;
            r.enabled = true;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            renderersFixed++;

            GameObject go = r.gameObject;
            if (go == null) continue;

            // Keep batching-friendly static, but do not mark as occlusion-only static at runtime.
            go.isStatic = true;

            string n = go.name.ToLowerInvariant();
            bool isStructure = n.Contains("wall") || n.Contains("building") || n.Contains("hangar")
                || n.Contains("warehouse") || n.Contains("container") || n.Contains("fence")
                || n.Contains("concrete") || n.Contains("floor") || n.Contains("ground")
                || n.Contains("road") || n.Contains("asphalt");

            if (isStructure)
            {
                MeshRenderer mr = r as MeshRenderer;
                if (mr != null)
                    ExpandRendererBounds(mr, 1.35f);
#if UNITY_EDITOR
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
                flags |= StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic;
                GameObjectUtility.SetStaticEditorFlags(go, flags);
#endif
            }
        }

        LODGroup[] lods = mapRoot.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < lods.Length; i++)
        {
            if (lods[i] == null) continue;
            lods[i].ForceLOD(0);
            lods[i].enabled = false;
            lodDisabled++;
        }

        OcclusionArea[] areas = mapRoot.GetComponentsInChildren<OcclusionArea>(true);
        for (int i = 0; i < areas.Length; i++)
        {
            if (areas[i] != null)
            {
                Object.Destroy(areas[i]);
                occlusionRemoved++;
            }
        }

        OcclusionPortal[] portals = mapRoot.GetComponentsInChildren<OcclusionPortal>(true);
        for (int i = 0; i < portals.Length; i++)
        {
            if (portals[i] != null)
                Object.Destroy(portals[i]);
        }

        DisableGameplayCameraOcclusionCulling();

        if (debugLog || !_logged)
        {
            _logged = true;
            Debug.Log(
                $"[MapVisibility] Stabilized '{mapRoot.name}': renderers={renderersFixed}, " +
                $"lodDisabled={lodDisabled}, occlusionAreasRemoved={occlusionRemoved}");
        }
    }

    private static void ExpandRendererBounds(MeshRenderer renderer, float padding)
    {
        if (renderer == null) return;

        MeshFilter mf = renderer.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Bounds b = mf.sharedMesh.bounds;
        Vector3 ext = b.extents * padding;
        renderer.localBounds = new Bounds(b.center, ext * 2f);
    }

    public static void DisableGameplayCameraOcclusionCulling()
    {
        CameraController cc = CameraController.Instance;
        if (cc != null)
        {
            Camera cam = cc.GetComponent<Camera>();
            if (cam != null)
                cam.useOcclusionCulling = false;
        }

        Camera main = Camera.main;
        if (main != null)
            main.useOcclusionCulling = false;

        Camera[] all = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c == null) continue;
            if (c.name.Contains("Minimap")) continue;
            c.useOcclusionCulling = false;
        }
    }
}
