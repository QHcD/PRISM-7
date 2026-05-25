using UnityEngine;

public static class LevelInteriorSpawnResolver
{
    private const int WrenchLevelIndex = 6;
    private const float RaycastLift = 6f;
    private const float RaycastDistance = 40f;
    private const float MinInteriorInset = 2.5f;
    private const float InteriorInsetRatio = 0.18f;
    private const string SpawnPointTag = "SpawnPoint";
    private const string PlayerSpawnName = "PlayerSpawn";
    private const string SpawnPointName = "SpawnPoint";

    public static bool RequiresInteriorSpawn
    {
        get
        {
            GameManager manager = GameManager.Instance;
            return manager != null
                && manager.currentLevel == WrenchLevelIndex
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
            spawn = marker.position;
            return true;
        }

        spawn = default;
        return false;
    }

    public static bool TryResolveInteriorSpawn(PlayerController player, out Vector3 spawn)
    {
        spawn = default;
        if (!TryGetInteriorFloorBounds(out Bounds bounds))
            return false;

        Transform reference = ResolveTaggedSpawnReference();
        if (reference != null && TryProjectToInteriorFloor(reference.position, bounds, player, out spawn))
            return true;

        Vector3 center = bounds.center;
        center.y = bounds.max.y + RaycastLift;
        if (TryProjectToInteriorFloor(center, bounds, player, out spawn))
            return true;

        reference = ResolveNamedSpawnReference();
        if (reference != null && TryProjectToInteriorFloor(reference.position, bounds, player, out spawn))
            return true;

        Collider[] colliders = GetMapRootColliders();
        float best = float.PositiveInfinity;
        Vector3 bestPoint = default;
        bool found = false;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (!IsInteriorFloorCollider(collider))
                continue;
            Bounds b = collider.bounds;
            Vector3 probe = new Vector3(b.center.x, b.max.y + RaycastLift, b.center.z);
            if (!TryProjectToInteriorFloor(probe, bounds, player, out Vector3 candidate))
                continue;
            float score = (new Vector2(candidate.x, candidate.z) - new Vector2(bounds.center.x, bounds.center.z)).sqrMagnitude;
            if (score >= best)
                continue;
            best = score;
            bestPoint = candidate;
            found = true;
        }

        if (!found)
            return false;

        spawn = bestPoint;
        return true;
    }

    public static bool IsValidInteriorPosition(Vector3 position)
    {
        if (!TryGetInteriorFloorBounds(out Bounds bounds))
            return false;
        return TryProjectToInteriorFloor(position, bounds, null, out _);
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
    }

    private static bool TryProjectToInteriorFloor(Vector3 source, Bounds bounds, PlayerController player, out Vector3 spawn)
    {
        spawn = default;
        Vector3 origin = source + Vector3.up * RaycastLift;
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            RaycastDistance + bounds.size.y,
            BuildInteriorRaycastMask(),
            QueryTriggerInteraction.Ignore);

        if (hits == null || hits.Length == 0)
            return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (!IsInteriorFloorCollider(hit.collider))
                continue;
            if (!IsInsideInteriorBounds(hit.point, bounds))
                continue;
            spawn = hit.point + Vector3.up * ResolveSpawnLift(player);
            return true;
        }

        return false;
    }

    private static float ResolveSpawnLift(PlayerController player)
    {
        CharacterController controller = player != null ? player.GetComponent<CharacterController>() : null;
        if (controller == null)
            return 0.08f;
        return Mathf.Max(0.08f, controller.skinWidth + 0.02f);
    }

    private static Transform ResolveSpawnReference()
    {
        Transform tagged = ResolveTaggedSpawnReference();
        return tagged != null ? tagged : ResolveNamedSpawnReference();
    }

    private static Transform ResolveTaggedSpawnReference()
    {
        GameObject tagged = null;
        try
        {
            tagged = GameObject.FindWithTag(SpawnPointTag);
        }
        catch { }

        if (tagged != null && tagged.activeInHierarchy)
            return tagged.transform;

        return null;
    }

    private static Transform ResolveNamedSpawnReference()
    {
        GameObject named = GameObject.Find(SpawnPointName);
        if (named != null && named.activeInHierarchy)
            return named.transform;

        GameObject arena = FindArenaRoot();
        if (arena == null)
            return null;

        Transform direct = arena.transform.Find("SpawnPoints/" + PlayerSpawnName);
        if (direct != null && direct.gameObject.activeInHierarchy)
            return direct;

        Transform[] all = arena.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t != null && t.name == PlayerSpawnName && t.gameObject.activeInHierarchy)
                return t;
        }

        return null;
    }

    private static bool TryGetInteriorFloorBounds(out Bounds bounds)
    {
        bounds = default;
        Collider[] colliders = GetMapRootColliders();
        bool hasBounds = false;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (!IsInteriorFloorCollider(collider))
                continue;

            Bounds b = collider.bounds;
            if (!hasBounds)
            {
                bounds = b;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(b);
            }
        }

        return hasBounds;
    }

    private static Collider[] GetMapRootColliders()
    {
        GameObject arena = FindArenaRoot();
        if (arena == null)
            return new Collider[0];

        Transform proxyRoot = arena.transform.Find("SciFiNavMeshProxyColliders");
        if (proxyRoot != null)
            return proxyRoot.GetComponentsInChildren<Collider>(false);

        return arena.GetComponentsInChildren<Collider>(false);
    }

    private static GameObject FindArenaRoot()
    {
        GameObject arena = GameObject.Find("FbxMap");
        if (arena == null) arena = GameObject.Find("SciFiArena");
        if (arena == null) arena = GameObject.Find("SciFiArena(Clone)");
        return arena;
    }

    private static bool IsInteriorFloorCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
            return false;
        if (collider.GetComponentInParent<IDamageable>() != null)
            return false;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("wall") || n.Contains("roof") || n.Contains("ceiling") || n.Contains("door")
                || n.Contains("rail") || n.Contains("railing") || n.Contains("pillar") || n.Contains("beam"))
                return false;
            if (n.Contains("scifinavmeshproxycolliders") || n.Contains("floor") || n.Contains("ground")
                || n.Contains("catwalk") || n.Contains("corridor") || n.Contains("platform") || n.Contains("walkway"))
                return true;
        }

        Bounds b = collider.bounds;
        float horizontal = Mathf.Max(b.size.x, b.size.z);
        return horizontal >= 2f && b.size.y <= Mathf.Max(0.35f, horizontal * 0.08f);
    }

    private static bool IsInsideInteriorBounds(Vector3 point, Bounds bounds)
    {
        float insetX = Mathf.Min(bounds.extents.x * 0.75f, Mathf.Max(MinInteriorInset, bounds.size.x * InteriorInsetRatio));
        float insetZ = Mathf.Min(bounds.extents.z * 0.75f, Mathf.Max(MinInteriorInset, bounds.size.z * InteriorInsetRatio));
        return point.x >= bounds.min.x + insetX
            && point.x <= bounds.max.x - insetX
            && point.z >= bounds.min.z + insetZ
            && point.z <= bounds.max.z - insetZ
            && point.y >= bounds.min.y - 0.5f
            && point.y <= bounds.max.y + 2f;
    }

    private static int BuildInteriorRaycastMask()
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
