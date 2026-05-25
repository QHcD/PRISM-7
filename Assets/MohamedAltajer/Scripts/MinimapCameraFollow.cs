using UnityEngine;

/// <summary>
/// Attach this component to the minimap Camera GameObject.
/// HUDManager uses FindFirstObjectByType to locate it and call EnsureRenderTexture().
/// </summary>
[RequireComponent(typeof(Camera))]
public class MinimapCameraFollow : MonoBehaviour
{
    // When true, camera stays fixed above arena centre (full overview).
    // When false, HUDManager repositions this transform every frame to follow the player.
    public bool lockToArenaCenter = true;

    // World-space Y height of the minimap camera above the ground plane.
    // Bumped from 35 → 120 so the orthographic top-down view always sits ABOVE
    // any arena ceiling/catwalk geometry and actually captures the floor/walls
    // layout (previously the camera could end up below ceiling caps and only
    // rendered the dark clear-colour with no level visible).
    public float height = 120f;

    // Resolution of the minimap render texture (square).
    public int textureSize = 256;

    // Orthographic size — controls how much of the arena is visible in the minimap.
    public float viewRadius = 28f;

    [Tooltip("Extra padding added around the calculated arena bounds for the full-map view.")]
    public float fullMapPadding = 12f;

    private RenderTexture _rt;
    private Camera _cam;
    private Bounds _arenaBounds;
    private bool _hasArenaBounds;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
        CacheArenaBounds();
        ConfigureCamera();
    }

    private void ConfigureCamera()
    {
        if (_cam == null) return;

        _cam.orthographic = true;
        _cam.orthographicSize = viewRadius;
        _cam.nearClipPlane = 0.1f;
        // Far clip generously covers full arena depth (was height+10 = 45 which
        // was easy to fall short of when the camera is repositioned high above
        // the arena). 500 is well within ortho-camera precision limits.
        _cam.farClipPlane = 500f;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        // Slate-blue clear so even unrendered slivers read as "map area" rather
        // than the previous near-black rectangle (root cause of "dark panel with
        // markers only" report).
        _cam.backgroundColor = new Color(0.18f, 0.22f, 0.28f, 1f);
        _cam.cullingMask = ~0; // render all layers — explicit catch-all
        _cam.depth = -2;       // render before main cameras
        _cam.enabled = false;  // HUDManager calls Render() manually — disable auto rendering
    }

    public void SetFullMapMode(bool enabled, Transform playerTarget = null)
    {
        CacheArenaBounds();

        if (enabled)
        {
            lockToArenaCenter = true;
            if (_hasArenaBounds)
            {
                Vector3 center = _arenaBounds.center;
                // Always sit ABOVE the arena's top extent + the configured height
                // so ceilings/catwalks don't occlude the floor layout.
                float topY = _arenaBounds.max.y + height;
                transform.position = new Vector3(center.x, topY, center.z);
                _cam.orthographicSize = Mathf.Max(24f, Mathf.Max(_arenaBounds.extents.x, _arenaBounds.extents.z) + fullMapPadding);
                _cam.farClipPlane = Mathf.Max(500f, (topY - _arenaBounds.min.y) + 50f);
            }
            else
            {
                _cam.orthographicSize = Mathf.Max(viewRadius, 42f);
            }

            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            return;
        }

        lockToArenaCenter = false;
        _cam.orthographicSize = viewRadius;
        if (playerTarget != null)
        {
            float topY = _hasArenaBounds ? _arenaBounds.max.y + height : playerTarget.position.y + height;
            transform.position = new Vector3(playerTarget.position.x, topY, playerTarget.position.z);
        }
    }

    /// <summary>
    /// Returns the RenderTexture used by the minimap, creating it on first call.
    /// Called by HUDManager every frame inside UpdateMinimap().
    /// </summary>
    public RenderTexture EnsureRenderTexture()
    {
        if (_rt == null || !_rt.IsCreated())
        {
            // Release any stale texture before creating a fresh one
            if (_rt != null) _rt.Release();

            _rt = new RenderTexture(textureSize, textureSize, 16, RenderTextureFormat.ARGB32);
            _rt.name = "MinimapRenderTexture";
            _rt.antiAliasing = 1;
            _rt.filterMode = FilterMode.Bilinear;
            _rt.Create();

            if (_cam != null)
                _cam.targetTexture = _rt;
        }

        return _rt;
    }

    private void OnDestroy()
    {
        // Clean up the RenderTexture to avoid GPU memory leaks
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }
    }

    private void CacheArenaBounds()
    {
        // Prefer the SciFiArena's Floors group when present — it gives perfectly
        // centered playable bounds without ceiling cap, perimeter overhangs, or
        // decorative props skewing the result.
        if (TryGetSciFiFloorBounds(out Bounds floorBounds))
        {
            _hasArenaBounds = true;
            _arenaBounds = floorBounds;
            return;
        }

        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        bool hasBounds = false;
        Bounds combinedBounds = default;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled)
                continue;

            if (renderer.GetComponentInParent<Canvas>() != null)
                continue;

            string lowerName = renderer.gameObject.name.ToLowerInvariant();
            if (lowerName.Contains("weapon") || lowerName.Contains("player") || lowerName.Contains("enemy"))
                continue;
            // Exclude items that intentionally extend past the playable footprint.
            if (lowerName.Contains("ceiling_sealedcap") || lowerName.Contains("ceiling_opaquecap")
                || lowerName.Contains("ceiling_detail") || lowerName.Contains("catwalk")
                || lowerName.Contains("hanglight") || lowerName.Contains("sprinkler")
                || lowerName.Contains("securitycam") || lowerName.StartsWith("cam_"))
                continue;

            if (!hasBounds)
            {
                combinedBounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }

        _hasArenaBounds = hasBounds;
        _arenaBounds = combinedBounds;
    }

    /// <summary>
    /// Computes bounds from the SciFiArena's Floors group only, giving a clean
    /// centered playable area. Returns false if the arena isn't loaded or the
    /// Floors hierarchy isn't found.
    /// </summary>
    private bool TryGetSciFiFloorBounds(out Bounds bounds)
    {
        bounds = default;

        GameObject arena = GameObject.Find("FbxMap")
                        ?? GameObject.Find("SciFiArena")
                        ?? GameObject.Find("SciFiArena(Clone)");
        if (arena == null) return false;

        Transform floors = arena.transform.Find("Floors");
        if (floors == null) return false;

        Renderer[] floorRenderers = floors.GetComponentsInChildren<Renderer>(true);
        if (floorRenderers == null || floorRenderers.Length == 0) return false;

        bool init = false;
        for (int i = 0; i < floorRenderers.Length; i++)
        {
            Renderer r = floorRenderers[i];
            if (r == null) continue;
            if (!init) { bounds = r.bounds; init = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return init;
    }
}
