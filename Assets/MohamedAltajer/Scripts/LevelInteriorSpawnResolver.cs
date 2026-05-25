using UnityEngine;
using UnityEngine.AI;

public static class LevelInteriorSpawnResolver
{
    private const string SpawnPointTag = "PlayerSpawn";
    private const string PlayerSpawnName = "PlayerSpawn";
    private const float NavSampleRadius = 10f;
    private const float FallbackNavSampleRadius = 28f;
    private const float SpawnLift = 0.5f;

    // Hard rule: valid SciFiArena player spawns must sit near the playable floor,
    // never on roof / top of walls. Y is measured relative to the playable floor.
    private const float FloorBandMinOffset = -0.5f;
    private const float FloorBandMaxOffset = 3.0f;

    private static bool _loggedSpawnFailure;
    private static Vector3? _cachedSpawn;

    public static void ResetDiagnostics()
    {
        _loggedSpawnFailure = false;
        _cachedSpawn = null;
    }

    public static void ClearSpawnCache()
    {
        _loggedSpawnFailure = false;
        _cachedSpawn = null;
        Debug.Log("[SciFiRestart] clearing old spawn cache");
    }

    public static bool RequiresInteriorSpawn
    {
        get
        {
            return GameManager.Instance != null
                && LevelBuilder.Instance != null
                && LevelBuilder.Instance.useSciFiArena;
        }
    }

    public static bool TryResolveSceneSpawn(PlayerController player, out Vector3 spawn)
    {
        if (RequiresInteriorSpawn)
            return TryResolveInteriorSpawn(player, out spawn);

        Transform marker = ResolveSpawnReference();
        if (marker != null)
        {
            spawn = marker.position + Vector3.up * SpawnLift;
            return true;
        }

        spawn = default;
        return false;
    }

    public static bool TryResolveInteriorSpawn(PlayerController player, out Vector3 spawn)
    {
        // Never reuse a position from before a rebuild. Each call selects fresh.
        _cachedSpawn = null;

        Transform[] markers = ResolveSpawnReferences();
        if (markers.Length > 0)
        {
            int start = markers.Length > 1 ? Random.Range(0, markers.Length) : 0;
            for (int i = 0; i < markers.Length; i++)
            {
                Transform marker = markers[(start + i) % markers.Length];
                if (marker == null)
                    continue;

                bool navmesh = false;
                bool clearance = false;
                if (TryResolveCandidate(marker.position, NavSampleRadius, player, out spawn, out navmesh, out clearance))
                {
                    _cachedSpawn = spawn;
                    Debug.Log($"[SciFiRestart] selected indoor spawn={spawn} marker={marker.name}");
                    Debug.Log($"[SciFiSpawn] spawn via marker {marker.name} pos={spawn}");
                    Debug.Log("[SciFiRestart] player spawned inside=true");
                    return true;
                }
                else
                {
                    Debug.Log($"[SciFiRestart] rejected roof/outside spawn={marker.position} marker={marker.name}");
                }
            }
        }

        if (TryBuildFallbackSeeds(out Vector3[] fallbackSeeds))
        {
            int start = fallbackSeeds.Length > 1 ? Random.Range(0, fallbackSeeds.Length) : 0;
            for (int i = 0; i < fallbackSeeds.Length; i++)
            {
                bool navmesh2 = false;
                bool clearance2 = false;
                Vector3 seed = fallbackSeeds[(start + i) % fallbackSeeds.Length];
                if (TryResolveCandidate(seed, FallbackNavSampleRadius, player, out spawn, out navmesh2, out clearance2))
                {
                    _cachedSpawn = spawn;
                    Debug.Log($"[SciFiRestart] selected indoor spawn={spawn} via fallback#{i}");
                    Debug.Log($"[SciFiSpawn] spawn via fallback#{i} pos={spawn}");
                    Debug.Log("[SciFiRestart] player spawned inside=true");
                    return true;
                }
                else
                {
                    Debug.Log($"[SciFiRestart] rejected roof/outside spawn={seed} fallback#{i}");
                }
            }
        }

        spawn = default;
        if (!_loggedSpawnFailure)
        {
            _loggedSpawnFailure = true;
            Debug.LogError("[SciFiSpawn] no valid indoor NavMesh spawn found; player spawn blocked.");
        }
        return false;
    }

    public static bool IsValidInteriorPosition(Vector3 position)
    {
        if (!RequiresInteriorSpawn)
            return true;

        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
            return false;

        if (position.y < -1f)
            return false;

        return IsInsideArenaBounds(position);
    }

    public static void ApplyExternalSpawn(PlayerController player, Vector3 spawn)
    {
        if (player == null)
            return;

        CharacterController controller = player.GetComponent<CharacterController>();
        bool restoreController = controller != null && controller.enabled;
        if (restoreController)
            controller.enabled = false;

        player.transform.position = spawn;
        Physics.SyncTransforms();

        if (restoreController)
            controller.enabled = true;

        Physics.SyncTransforms();
        Debug.Log($"[SciFiSpawn] ApplyExternalSpawn completed pos={spawn}");
    }

    private static bool TryResolveCandidate(
        Vector3 seed,
        float sampleRadius,
        PlayerController player,
        out Vector3 spawn,
        out bool navmesh,
        out bool clearance)
    {
        spawn = default;
        navmesh = false;
        clearance = false;

        if (!NavMesh.SamplePosition(seed, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
            return false;

        navmesh = true;
        Vector3 candidate = hit.position + Vector3.up * SpawnLift;
        if (!IsInsideArenaBounds(candidate))
            return false;

        if (!IsOnPlayableFloor(candidate))
            return false;

        clearance = HasCapsuleClearance(candidate, player);
        if (!clearance)
            return false;

        spawn = candidate;
        return true;
    }

    // Hard Y-band rule for SciFiArena: reject anything above floor + ~3m so
    // roofs and tops of walls can never be picked as a player spawn.
    private static bool IsOnPlayableFloor(Vector3 position)
    {
        if (!TryGetPlayableBounds(out Bounds bounds))
            return false;

        float floorY = bounds.min.y;
        float minY = floorY + FloorBandMinOffset;
        float maxY = floorY + FloorBandMaxOffset;
        return position.y >= minY && position.y <= maxY;
    }

    private static bool TryBuildFallbackSeeds(out Vector3[] seeds)
    {
        seeds = System.Array.Empty<Vector3>();
        if (!TryGetPlayableBounds(out Bounds bounds))
            return false;

        Vector3 center = bounds.center;
        float y = bounds.min.y + 1f;
        float x = Mathf.Clamp(bounds.extents.x * 0.22f, 4f, 12f);
        float z = Mathf.Clamp(bounds.extents.z * 0.22f, 4f, 12f);
        float x2 = Mathf.Clamp(bounds.extents.x * 0.34f, 6f, 18f);
        float z2 = Mathf.Clamp(bounds.extents.z * 0.34f, 6f, 18f);

        seeds = new[]
        {
            new Vector3(center.x, y, center.z),
            new Vector3(center.x, y, center.z - z),
            new Vector3(center.x, y, center.z + z),
            new Vector3(center.x - x, y, center.z),
            new Vector3(center.x + x, y, center.z),
            new Vector3(center.x - x, y, center.z - z),
            new Vector3(center.x + x, y, center.z - z),
            new Vector3(center.x - x, y, center.z + z),
            new Vector3(center.x + x, y, center.z + z),
            new Vector3(center.x - x2, y, center.z),
            new Vector3(center.x + x2, y, center.z),
            new Vector3(center.x, y, center.z - z2),
            new Vector3(center.x, y, center.z + z2),
            new Vector3(center.x, y + 3f, center.z),
            new Vector3(center.x, y + 3f, center.z - z)
        };
        return true;
    }

    private static bool HasCapsuleClearance(Vector3 feet, PlayerController player)
    {
        CharacterController controller = player != null ? player.GetComponent<CharacterController>() : null;
        float radius = controller != null ? Mathf.Max(0.25f, controller.radius) : 0.4f;
        float height = controller != null ? Mathf.Max(radius * 2.2f, controller.height) : 2f;
        Vector3 bottom = feet + Vector3.up * (radius + 0.08f);
        Vector3 top = feet + Vector3.up * Mathf.Max(radius + 0.12f, height - radius);
        return !Physics.CheckCapsule(bottom, top, radius * 0.92f, BuildSpawnBlockerMask(), QueryTriggerInteraction.Ignore);
    }

    private static Transform ResolveSpawnReference()
    {
        Transform[] markers = ResolveSpawnReferences();
        if (markers.Length > 0)
            return markers[0];
        return null;
    }

    private static Transform[] ResolveSpawnReferences()
    {
        System.Collections.Generic.List<Transform> markers = new System.Collections.Generic.List<Transform>();
        GameObject arena = FindArenaRoot();
        if (arena != null)
        {
            Transform direct = arena.transform.Find("SpawnPoints/" + PlayerSpawnName);
            if (direct != null)
            {
                Debug.Log($"[SciFiSpawn] ResolveSpawnReference: found via path SpawnPoints/{PlayerSpawnName} pos={direct.position}");
                markers.Add(direct);
            }

            Transform[] all = arena.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t != null
                    && t != direct
                    && t.name.StartsWith(PlayerSpawnName, System.StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[SciFiSpawn] ResolveSpawnReference: found via deep search pos={t.position}");
                    markers.Add(t);
                }
            }
        }

        GameObject tagged = null;
        try
        {
            tagged = GameObject.FindWithTag(SpawnPointTag);
        }
        catch { }

        if (tagged != null)
        {
            Debug.Log($"[SciFiSpawn] ResolveSpawnReference: found via tag pos={tagged.transform.position}");
            if (!markers.Contains(tagged.transform))
                markers.Add(tagged.transform);
        }

        if (markers.Count == 0)
            Debug.Log($"[SciFiSpawn] ResolveSpawnReference: NO spawn marker found arena={(arena != null ? arena.name : "NULL")}");
        return markers.ToArray();
    }

    private static bool IsInsideArenaBounds(Vector3 position)
    {
        if (!TryGetPlayableBounds(out Bounds bounds))
            return false;

        float insetX = Mathf.Min(6f, Mathf.Max(2f, bounds.extents.x * 0.12f));
        float insetZ = Mathf.Min(6f, Mathf.Max(2f, bounds.extents.z * 0.12f));
        bounds.min = new Vector3(bounds.min.x + insetX, bounds.min.y - 0.5f, bounds.min.z + insetZ);
        bounds.max = new Vector3(bounds.max.x - insetX, bounds.max.y + 8f, bounds.max.z - insetZ);
        return bounds.Contains(position);
    }

    private static bool TryGetPlayableBounds(out Bounds bounds)
    {
        bounds = default;
        GameObject arena = FindArenaRoot();
        if (arena == null)
            return false;

        Transform proxyRoot = arena.transform.Find("SciFiNavMeshProxyColliders");
        Collider[] colliders = proxyRoot != null
            ? proxyRoot.GetComponentsInChildren<Collider>(false)
            : arena.GetComponentsInChildren<Collider>(false);

        bool hasBounds = false;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled || collider.isTrigger)
                continue;
            if (!hasBounds)
            {
                bounds = collider.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        return hasBounds;
    }

    private static GameObject FindArenaRoot()
    {
        GameObject arena = GameObject.Find("FbxMap");
        if (arena == null) arena = GameObject.Find("SciFiArena");
        if (arena == null) arena = GameObject.Find("SciFiArena(Clone)");
        return arena;
    }

    private static int BuildSpawnBlockerMask()
    {
        int mask = Physics.DefaultRaycastLayers;
        RemoveLayer(ref mask, "Player");
        RemoveLayer(ref mask, "Enemy");
        RemoveLayer(ref mask, "Enemies");
        RemoveLayer(ref mask, "UI");
        RemoveLayer(ref mask, "Ignore Raycast");
        return mask;
    }

    private static void RemoveLayer(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
            mask &= ~(1 << layer);
    }
}
