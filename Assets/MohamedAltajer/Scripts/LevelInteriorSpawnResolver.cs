using UnityEngine;
using UnityEngine.SceneManagement;

public static class LevelInteriorSpawnResolver
{
    private const string InsideSpawnTag = "InsideSpawn";
    private const string SpawnPointTag = "PlayerSpawn";
    private const string InsideSpawnName = "InsideSpawn";
    private const string PlayerSpawnName = "PlayerSpawn";
    private const string WalkableLayerName = "Walkable";
    private const float MarkerProbeUp = 3f;
    private const float MarkerProbeDown = 18f;
    private const float SpawnFloorOffset = 0.5f;
    private const float AnchorHeight = 1.25f;
    private const float FloorBandMinOffset = -0.5f;
    private const float FloorBandMaxOffset = 3.0f;
    private const float MinWalkableNormalY = 0.58f;

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
        return TryResolveAdaptiveSpawn(player, out spawn);
    }

    public static bool TryResolveInteriorSpawn(PlayerController player, out Vector3 spawn)
    {
        return TryResolveAdaptiveSpawn(player, out spawn);
    }

    private static bool TryResolveAdaptiveSpawn(PlayerController player, out Vector3 spawn)
    {
        _cachedSpawn = null;
        EnsureKnownWalkableFloorLayers();

        Transform[] markers = ResolveSpawnReferences();
        for (int i = 0; i < markers.Length; i++)
        {
            Transform marker = markers[i];
            if (marker == null)
                continue;

            if (TryResolveCandidatePosition(marker.position, player, out spawn, out string error))
            {
                _cachedSpawn = spawn;
                Debug.Log($"[SciFiRestart] selected spawn={spawn} marker={marker.name}");
                Debug.Log($"[SciFiSpawn] spawn via marker {marker.name} pos={spawn}");
                return true;
            }

            Debug.LogWarning("[SciFiSpawn] rejected spawn marker: " + error);
        }

        if (TryResolveAutomaticFallback(player, out spawn, out string fallbackReason))
        {
            _cachedSpawn = spawn;
            Debug.LogWarning("[SciFiSpawn] adaptive fallback spawn selected: " + fallbackReason + " pos=" + spawn);
            return true;
        }

        WarnSpawnFailure("No marker, map root, or structural floor projection produced a safe spawn.");
        return false;
    }

    public static bool IsValidInteriorPosition(Vector3 position)
    {
        if (!RequiresInteriorSpawn)
            return true;

        EnsureKnownWalkableFloorLayers();

        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
            return false;

        if (position.y < -1f)
            return false;

        return IsInsideArenaBounds(position) && HasWalkableFloorUnderPosition(position);
    }

    public static void MarkWalkableFloorColliders(Transform root)
    {
        int walkableLayer = LayerMask.NameToLayer(WalkableLayerName);
        if (root == null || walkableLayer < 0)
            return;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger)
                continue;
            if (!LooksLikeWalkableFloor(collider))
                continue;
            collider.gameObject.layer = walkableLayer;
        }
    }

    public static bool TryProjectToInteriorSpawnAnchor(Vector3 seed, out Vector3 anchor)
    {
        anchor = default;
        EnsureKnownWalkableFloorLayers();
        if (!TryFindWalkableFloor(seed, out RaycastHit hit))
            return false;
        Vector3 feet = hit.point + Vector3.up * SpawnFloorOffset;
        if (!IsInsideArenaBounds(feet) || !IsOnPlayableFloor(feet))
            return false;
        anchor = hit.point + Vector3.up * AnchorHeight;
        return true;
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

    private static void WarnSpawnFailure(string message)
    {
        if (!_loggedSpawnFailure)
        {
            _loggedSpawnFailure = true;
            Debug.LogWarning("[SciFiSpawn] " + message);
        }
    }

    private static bool TryResolveCandidatePosition(Vector3 position, PlayerController player, out Vector3 spawn, out string error)
    {
        spawn = default;
        error = null;

        if (!IsFinite(position))
        {
            error = "spawn candidate was not finite";
            return false;
        }

        if (!TryFindWalkableFloor(position, out RaycastHit hit))
        {
            error = $"spawn candidate {position} did not project onto walkable or environment floor geometry";
            return false;
        }

        Vector3 candidate = hit.point + Vector3.up * SpawnFloorOffset;
        if (!IsInsideArenaBounds(candidate))
        {
            error = $"spawn candidate resolved outside playable interior bounds at {candidate}";
            return false;
        }

        if (!IsOnPlayableFloor(candidate))
        {
            error = $"spawn candidate resolved outside the playable floor band at {candidate}";
            return false;
        }

        if (!HasCapsuleClearance(candidate, player))
        {
            error = $"spawn candidate resolved to blocked capsule space at {candidate}";
            return false;
        }

        spawn = candidate;
        return true;
    }

    private static bool TryResolveAutomaticFallback(PlayerController player, out Vector3 spawn, out string reason)
    {
        spawn = default;
        reason = null;

        System.Collections.Generic.List<Vector3> candidates = new System.Collections.Generic.List<Vector3>();
        Vector3 reference = player != null && IsFinite(player.transform.position) ? player.transform.position : Vector3.zero;

        AddGenericSceneCandidates(candidates);

        if (TryGetPlayableBounds(out Bounds playableBounds))
        {
            Vector3 c = playableBounds.center;
            candidates.Add(new Vector3(c.x, playableBounds.max.y + 2f, c.z));
            reference = new Vector3(c.x, playableBounds.max.y + 2f, c.z);
        }

        if (TryGetSceneStructureBounds(out Bounds sceneBounds))
        {
            Vector3 c = sceneBounds.center;
            candidates.Add(new Vector3(c.x, sceneBounds.max.y + 2f, c.z));
            if (!IsFinite(reference) || reference == Vector3.zero)
                reference = new Vector3(c.x, sceneBounds.max.y + 2f, c.z);
        }

        if (player != null && IsFinite(player.transform.position))
            candidates.Add(player.transform.position);

        for (int i = 0; i < candidates.Count; i++)
        {
            if (TryResolveCandidatePosition(candidates[i], player, out spawn, out _))
            {
                reason = "projected candidate #" + i;
                return true;
            }
        }

        if (TryFindClosestStructuralFloorPoint(reference, out Vector3 structuralPoint)
            && TryResolveCandidatePosition(structuralPoint, player, out spawn, out _))
        {
            reason = "closest structural floor collider";
            return true;
        }

        if (TryFindClosestMeshVertexFloorPoint(reference, out Vector3 vertexPoint))
        {
            Vector3 candidate = vertexPoint + Vector3.up * SpawnFloorOffset;
            if (IsInsideArenaBounds(candidate) && IsOnPlayableFloor(candidate) && HasCapsuleClearance(candidate, player))
            {
                spawn = candidate;
                reason = "closest structural mesh vertex";
                return true;
            }
        }

        return false;
    }

    private static void AddGenericSceneCandidates(System.Collections.Generic.List<Vector3> candidates)
    {
        string[] names =
        {
            InsideSpawnName,
            PlayerSpawnName,
            "SpawnPoint",
            "SpawnPoints",
            "PlayerStart",
            "Start",
            "FbxMap",
            "SciFiArena",
            "SciFiArena(Clone)",
            "Map",
            "Environment",
            "Level",
            "World",
            "Geometry",
            "Arena"
        };

        for (int i = 0; i < names.Length; i++)
        {
            GameObject go = GameObject.Find(names[i]);
            if (go != null && IsFinite(go.transform.position))
                candidates.Add(go.transform.position);
        }
    }

    private static bool TryGetSceneStructureBounds(out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return false;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int r = 0; r < roots.Length; r++)
        {
            GameObject root = roots[r];
            if (root == null)
                continue;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (!IsStructuralCollider(collider))
                    continue;
                if (!found)
                {
                    bounds = collider.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }
        }

        return found;
    }

    private static bool TryFindClosestStructuralFloorPoint(Vector3 reference, out Vector3 point)
    {
        point = default;
        Collider[] colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        float best = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (!IsStructuralCollider(collider))
                continue;

            Bounds b = collider.bounds;
            Vector3 candidate = new Vector3(
                Mathf.Clamp(reference.x, b.min.x, b.max.x),
                b.max.y + 2f,
                Mathf.Clamp(reference.z, b.min.z, b.max.z));
            float distance = HorizontalSqrDistance(reference, candidate);
            if (distance >= best)
                continue;

            best = distance;
            point = candidate;
            found = true;
        }

        return found;
    }

    private static bool TryFindClosestMeshVertexFloorPoint(Vector3 reference, out Vector3 point)
    {
        point = default;
        MeshFilter[] filters = Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        float best = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter == null || filter.sharedMesh == null || !LooksLikeWalkableTransform(filter.transform))
                continue;

            EnsureStructuralMeshCollider(filter);

            if (!filter.sharedMesh.isReadable)
            {
                if (!TryGetMeshFilterBoundsPoint(filter, reference, out Vector3 boundsPoint))
                    continue;

                float boundsDistance = HorizontalSqrDistance(reference, boundsPoint);
                if (boundsDistance < best)
                {
                    best = boundsDistance;
                    point = boundsPoint;
                    found = true;
                }
                continue;
            }

            Vector3[] vertices = filter.sharedMesh.vertices;

            if (vertices == null || vertices.Length == 0)
                continue;

            int stride = Mathf.Max(1, vertices.Length / 256);
            for (int v = 0; v < vertices.Length; v += stride)
            {
                Vector3 world = filter.transform.TransformPoint(vertices[v]);
                float distance = HorizontalSqrDistance(reference, world);
                if (distance >= best)
                    continue;

                best = distance;
                point = world;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetMeshFilterBoundsPoint(MeshFilter filter, Vector3 reference, out Vector3 point)
    {
        point = default;
        Bounds bounds = default;
        bool hasBounds = false;

        Collider collider = filter.GetComponent<Collider>();
        if (collider != null && collider.enabled)
        {
            bounds = collider.bounds;
            hasBounds = true;
        }
        else
        {
            Renderer renderer = filter.GetComponent<Renderer>();
            if (renderer != null && renderer.enabled)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
        }

        if (!hasBounds)
            return false;

        point = new Vector3(
            Mathf.Clamp(reference.x, bounds.min.x, bounds.max.x),
            bounds.max.y,
            Mathf.Clamp(reference.z, bounds.min.z, bounds.max.z));
        return true;
    }

    private static void EnsureStructuralMeshCollider(MeshFilter filter)
    {
        if (filter == null || filter.sharedMesh == null)
            return;

        Collider existing = filter.GetComponent<Collider>();
        if (existing != null)
        {
            existing.enabled = true;
            return;
        }

        MeshCollider collider = filter.gameObject.AddComponent<MeshCollider>();
        collider.sharedMesh = filter.sharedMesh;
        collider.convex = false;
        collider.enabled = true;
        int walkableLayer = LayerMask.NameToLayer(WalkableLayerName);
        if (walkableLayer >= 0)
            filter.gameObject.layer = walkableLayer;
    }

    private static bool TryFindWalkableFloor(Vector3 markerPosition, out RaycastHit hit)
    {
        Vector3 origin = markerPosition + Vector3.up * MarkerProbeUp;
        int mask = BuildFloorProjectionMask();
        if (mask == 0)
        {
            hit = default;
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            MarkerProbeUp + MarkerProbeDown,
            mask,
            QueryTriggerInteraction.Ignore);

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            if (!IsValidWalkableFloorHit(hits[i]))
                continue;
            hit = hits[i];
            return true;
        }

        hit = default;
        return false;
    }

    private static bool IsValidWalkableFloorHit(RaycastHit hit)
    {
        return hit.collider != null
            && hit.normal.y >= MinWalkableNormalY
            && IsWalkableFloorCollider(hit.collider);
    }

    private static bool IsWalkableFloorCollider(Collider collider)
    {
        if (collider == null)
            return false;

        int walkableLayer = LayerMask.NameToLayer(WalkableLayerName);
        if (walkableLayer >= 0 && collider.gameObject.layer == walkableLayer)
            return true;

        if (collider.GetComponentInParent<IDamageable>() != null)
            return false;

        string lower = collider.name.ToLowerInvariant();
        if (lower.Contains("wall") || lower.Contains("roof") || lower.Contains("ceiling")
            || lower.Contains("rail") || lower.Contains("railing") || lower.Contains("pillar")
            || lower.Contains("beam") || lower.Contains("door") || lower.Contains("vent")
            || lower.Contains("pipe") || lower.Contains("fence") || lower.Contains("trigger")
            || lower.Contains("player") || lower.Contains("enemy") || lower.Contains("weapon")
            || lower.Contains("hitbox") || lower.Contains("hurtbox"))
            return false;

        int layer = collider.gameObject.layer;
        return layer == 0
            || LayerMatches(layer, "Environment")
            || LayerMatches(layer, "Map")
            || LayerMatches(layer, "Ground")
            || LayerMatches(layer, "Terrain")
            || LooksLikeWalkableFloor(collider);
    }

    private static int BuildFloorProjectionMask()
    {
        int mask = Physics.DefaultRaycastLayers;
        AddLayerIfExists(ref mask, WalkableLayerName);
        AddLayerIfExists(ref mask, "Environment");
        AddLayerIfExists(ref mask, "Map");
        AddLayerIfExists(ref mask, "Ground");
        AddLayerIfExists(ref mask, "Terrain");
        RemoveLayer(ref mask, "Player");
        RemoveLayer(ref mask, "Enemy");
        RemoveLayer(ref mask, "Enemies");
        RemoveLayer(ref mask, "UI");
        RemoveLayer(ref mask, "Ignore Raycast");
        return mask;
    }

    private static bool HasWalkableFloorUnderPosition(Vector3 position)
    {
        return TryFindWalkableFloor(position + Vector3.up * 0.2f, out RaycastHit hit)
            && Mathf.Abs((hit.point + Vector3.up * SpawnFloorOffset).y - position.y) <= 1.25f;
    }

    private static bool IsOnPlayableFloor(Vector3 position)
    {
        if (!TryGetPlayableBounds(out Bounds bounds))
            return true;

        float floorY = bounds.min.y;
        float minY = floorY + FloorBandMinOffset;
        float maxY = floorY + FloorBandMaxOffset;
        return position.y >= minY && position.y <= maxY;
    }

    private static void EnsureKnownWalkableFloorLayers()
    {
        GameObject arena = FindArenaRoot();
        if (arena != null)
            MarkWalkableFloorColliders(arena.transform);
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

    private static bool LooksLikeWalkableFloor(Collider collider)
    {
        if (collider == null)
            return false;

        string lower = collider.name.ToLowerInvariant();
        if (lower.Contains("wall") || lower.Contains("roof") || lower.Contains("ceiling")
            || lower.Contains("rail") || lower.Contains("railing") || lower.Contains("pillar")
            || lower.Contains("beam") || lower.Contains("door") || lower.Contains("vent")
            || lower.Contains("pipe") || lower.Contains("fence") || lower.Contains("trigger")
            || lower.Contains("player") || lower.Contains("enemy"))
            return false;

        if (lower.Contains("navmeshproxy") || lower.Contains("floor") || lower.Contains("ground")
            || lower.Contains("catwalk") || lower.Contains("walkway") || lower.Contains("stair")
            || lower.Contains("step") || lower.Contains("platform"))
            return true;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            string name = t.name.ToLowerInvariant();
            if (name.Contains("navmeshproxycolliders") || name.Contains("floor tiles")
                || name.Contains("catwalk") || name.Contains("walkway"))
                return true;
        }

        return false;
    }

    private static bool IsStructuralCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || collider.isTrigger)
            return false;
        if (collider.GetComponentInParent<IDamageable>() != null)
            return false;

        string lower = collider.name.ToLowerInvariant();
        if (lower.Contains("player") || lower.Contains("enemy") || lower.Contains("weapon")
            || lower.Contains("hitbox") || lower.Contains("hurtbox") || lower.Contains("trigger"))
            return false;

        return IsWalkableFloorCollider(collider) || LooksLikeWalkableFloor(collider);
    }

    private static bool LooksLikeWalkableTransform(Transform transform)
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            string lower = t.name.ToLowerInvariant();
            if (lower.Contains("wall") || lower.Contains("roof") || lower.Contains("ceiling")
                || lower.Contains("rail") || lower.Contains("railing") || lower.Contains("pillar")
                || lower.Contains("beam") || lower.Contains("door") || lower.Contains("vent")
                || lower.Contains("player") || lower.Contains("enemy"))
                return false;

            if (lower.Contains("floor") || lower.Contains("ground") || lower.Contains("catwalk")
                || lower.Contains("walkway") || lower.Contains("stair") || lower.Contains("step")
                || lower.Contains("platform") || lower.Contains("navmeshproxy"))
                return true;
        }

        return false;
    }

    private static float HorizontalSqrDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
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
        int currentLevel = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;

        AddTaggedInsideSpawnMarkers(markers, currentLevel);

        GameObject arena = FindArenaRoot();
        if (arena != null)
        {
            Transform spawnRoot = arena.transform.Find("SpawnPoints");
            AddSpawnMarker(markers, spawnRoot != null ? spawnRoot.Find($"{InsideSpawnName}_L{currentLevel:00}") : null);
            AddSpawnMarker(markers, spawnRoot != null ? spawnRoot.Find($"{InsideSpawnName}_{currentLevel:00}") : null);
            AddSpawnMarker(markers, spawnRoot != null ? spawnRoot.Find($"{InsideSpawnName}{currentLevel:00}") : null);
            AddSpawnMarker(markers, spawnRoot != null ? spawnRoot.Find(InsideSpawnName) : null);
            AddSpawnMarker(markers, arena.transform.Find("SpawnPoints/" + PlayerSpawnName));

            Transform[] all = arena.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                    continue;
                string name = t.name;
                bool insideMatch = name.Equals($"{InsideSpawnName}_L{currentLevel:00}", System.StringComparison.OrdinalIgnoreCase)
                    || name.Equals($"{InsideSpawnName}_{currentLevel:00}", System.StringComparison.OrdinalIgnoreCase)
                    || name.Equals($"{InsideSpawnName}{currentLevel:00}", System.StringComparison.OrdinalIgnoreCase)
                    || name.Equals(InsideSpawnName, System.StringComparison.OrdinalIgnoreCase);
                bool legacyMatch = name.Equals(PlayerSpawnName, System.StringComparison.OrdinalIgnoreCase);
                if (insideMatch || legacyMatch)
                    AddSpawnMarker(markers, t);
            }
        }

        if (markers.Count == 0)
            AddTaggedPlayerSpawnMarker(markers);

        if (markers.Count == 0)
            Debug.Log($"[SciFiSpawn] ResolveSpawnReference: NO inside spawn marker found arena={(arena != null ? arena.name : "NULL")}");

        return markers.ToArray();
    }

    private static void AddTaggedInsideSpawnMarkers(System.Collections.Generic.List<Transform> markers, int currentLevel)
    {
        GameObject[] tagged = System.Array.Empty<GameObject>();
        try
        {
            tagged = GameObject.FindGameObjectsWithTag(InsideSpawnTag);
        }
        catch { }

        if (tagged == null || tagged.Length == 0)
            return;

        string[] preferredNames =
        {
            $"{InsideSpawnName}_L{currentLevel:00}",
            $"{InsideSpawnName}_{currentLevel:00}",
            $"{InsideSpawnName}{currentLevel:00}",
            InsideSpawnName
        };

        for (int p = 0; p < preferredNames.Length; p++)
        {
            for (int i = 0; i < tagged.Length; i++)
            {
                GameObject go = tagged[i];
                if (go != null && go.name.Equals(preferredNames[p], System.StringComparison.OrdinalIgnoreCase))
                    AddSpawnMarker(markers, go.transform);
            }
        }

        for (int i = 0; i < tagged.Length; i++)
        {
            if (tagged[i] != null)
                AddSpawnMarker(markers, tagged[i].transform);
        }
    }

    private static void AddTaggedPlayerSpawnMarker(System.Collections.Generic.List<Transform> markers)
    {
        GameObject tagged = null;
        try
        {
            tagged = GameObject.FindWithTag(SpawnPointTag);
        }
        catch { }

        if (tagged != null)
            AddSpawnMarker(markers, tagged.transform);
    }

    private static void AddSpawnMarker(System.Collections.Generic.List<Transform> markers, Transform marker)
    {
        if (marker == null || markers.Contains(marker))
            return;

        TryTagInsideSpawn(marker.gameObject);
        markers.Add(marker);
        Debug.Log($"[SciFiSpawn] ResolveSpawnReference: found inside marker {marker.name} pos={marker.position}");
    }

    private static void TryTagInsideSpawn(GameObject marker)
    {
        if (marker == null)
            return;

        try
        {
            marker.tag = InsideSpawnTag;
        }
        catch { }
    }

    private static bool IsInsideArenaBounds(Vector3 position)
    {
        if (!TryGetPlayableBounds(out Bounds bounds))
            return true;

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

    private static void AddLayerIfExists(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0)
            mask |= 1 << layer;
    }

    private static bool LayerMatches(int layer, string layerName)
    {
        int namedLayer = LayerMask.NameToLayer(layerName);
        return namedLayer >= 0 && layer == namedLayer;
    }
}
