using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Shared outdoor spawn validation and runtime geometry checks for enemies.
/// Keeps spawns on walkable ground (not under the map) and away from wall faces.
/// </summary>
public static class EnemySpawnGeometry
{
    /// <summary>
    /// When true, IsValidOutdoorSpawn bypasses the IsEnclosedOrIndoor rejection.
    /// Set this for indoor maps (e.g. SciFiArena) so spawns inside a roofed,
    /// wall-bounded warehouse aren't all rejected as "indoor".
    /// </summary>
    public static bool AllowEnclosedArena = false;

    private const float MinWallClearance = 0.85f;
    private const float MaxFeetBelowGround = 0.12f;
    private const float MaxFeetAboveGround = 0.35f;
    private const float RequiredHeadroom = 1.95f;
    private const float MaxIndoorCeilingHeight = 13f;
    private const float EnclosureWallProbeRange = 11f;
    private const float EnclosureWallRayCount = 12;
    private const float MinBuildingSpawnClearance = 6f;

    private static int _spawnPhysicsMask = -1;
    private static int _staticGeometryMask = -1;
    private static readonly System.Collections.Generic.List<Vector3> StreetSpawnAnchors =
        new System.Collections.Generic.List<Vector3>(256);

    public static int StreetSpawnAnchorCount => StreetSpawnAnchors.Count;

    public static int SpawnPhysicsMask
    {
        get
        {
            if (_spawnPhysicsMask != -1)
                return _spawnPhysicsMask;

            int mask = Physics.DefaultRaycastLayers;
            ExcludeCharacterLayers(ref mask);
            _spawnPhysicsMask = mask;
            return _spawnPhysicsMask;
        }
    }

    public static int StaticGeometryMask
    {
        get
        {
            if (_staticGeometryMask != -1)
                return _staticGeometryMask;

            int mask = 0;
            void Add(string layerName)
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer >= 0)
                    mask |= 1 << layer;
            }

            Add("Environment");
            Add("Building");
            Add("StaticObstacle");
            Add("Wall");
            Add("Door");
            Add("Default");

            if (mask == 0)
                mask = Physics.DefaultRaycastLayers;

            ExcludeCharacterLayers(ref mask);
            _staticGeometryMask = mask;
            return _staticGeometryMask;
        }
    }

    private static void ExcludeCharacterLayers(ref int mask)
    {
        int playerLayer = LayerMask.NameToLayer("Player");
        int characterLayer = LayerMask.NameToLayer("Character");
        int hittableLayer = LayerMask.NameToLayer("Hittable");
        int enemiesLayer = LayerMask.NameToLayer("Enemies");
        if (playerLayer >= 0) mask &= ~(1 << playerLayer);
        if (characterLayer >= 0) mask &= ~(1 << characterLayer);
        if (hittableLayer >= 0) mask &= ~(1 << hittableLayer);
        if (enemiesLayer >= 0) mask &= ~(1 << enemiesLayer);
    }

    /// <summary>
    /// Outdoor spawn: grounded feet, headroom, not inside geometry, wall clearance, optional rooftop guard.
    /// </summary>
    public static bool IsValidOutdoorSpawn(Vector3 candidate, Vector3 playerNavPos, bool rejectRooftops)
    {
        if (rejectRooftops && playerNavPos != default &&
            (candidate.y - playerNavPos.y) > LevelBuilder.MaxSpawnYAbovePlayer)
            return false;

        if (!TryAlignFeetToGround(candidate, out Vector3 feet))
            return false;

        if (!HasSpawnGroundAndClearance(feet, RequiredHeadroom))
            return false;

        if (!HasHorizontalWallClearance(feet))
            return false;

        if (!AllowEnclosedArena && IsEnclosedOrIndoor(feet))
            return false;

        return true;
    }

    /// <summary>
    /// Player-only spawn: must stand on a real map street/road surface — never backfill or physics floors.
    /// </summary>
    public static bool IsValidPlayerStreetSpawn(Vector3 candidate)
    {
        if (!TryAlignFeetToStreetGround(candidate, out Vector3 feet))
            return false;

        if (!IsValidOutdoorSpawn(feet, default, rejectRooftops: false))
            return false;

        if (IsNearBuildingFootprint(feet, MinBuildingSpawnClearance))
            return false;

        return true;
    }

    /// <summary>
    /// Collects walkable street centroids from the loaded industrial map (FbxMap root).
    /// </summary>
    public static void RefreshStreetSpawnAnchors(Transform mapRoot)
    {
        StreetSpawnAnchors.Clear();
        if (mapRoot == null) return;

        Renderer[] renderers = mapRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer rend = renderers[i];
            if (rend == null || rend is ParticleSystemRenderer) continue;
            if (!IsStreetMeshName(rend.gameObject.name)) continue;

            Bounds b = rend.bounds;
            if (b.size.y > 2.5f) continue;

            AddStreetAnchor(new Vector3(b.center.x, b.max.y, b.center.z));

            float spanX = b.size.x;
            float spanZ = b.size.z;
            if (spanX > 10f || spanZ > 10f)
            {
                float ox = spanX * 0.28f;
                float oz = spanZ * 0.28f;
                AddStreetAnchor(new Vector3(b.center.x + ox, b.max.y, b.center.z + oz));
                AddStreetAnchor(new Vector3(b.center.x - ox, b.max.y, b.center.z + oz));
                AddStreetAnchor(new Vector3(b.center.x + ox, b.max.y, b.center.z - oz));
                AddStreetAnchor(new Vector3(b.center.x - ox, b.max.y, b.center.z - oz));
            }
        }
    }

    /// <summary>
    /// Finds a spawn on an actual street mesh — never on arena backfill / physics floors.
    /// </summary>
    public static bool TryFindStreetPlayerSpawn(float arenaHalfSize, Vector3 preferred, out Vector3 feetPosition)
    {
        feetPosition = default;

        if (StreetSpawnAnchors.Count > 0)
        {
            var ordered = new System.Collections.Generic.List<Vector3>(StreetSpawnAnchors);
            OrderAnchorsByPreferred(ordered, preferred);
            ShuffleTail(ordered, startIndex: 1);

            for (int i = 0; i < ordered.Count; i++)
            {
                Vector3 seed = ordered[i];
                if (TryFindValidStreetSpawnNear(seed, arenaHalfSize * 0.2f, out Vector3 valid))
                {
                    feetPosition = valid;
                    return true;
                }

                if (IsValidPlayerStreetSpawn(seed))
                {
                    feetPosition = seed;
                    return true;
                }
            }
        }

        if (TryFindOpenPlayerSpawnOnStreetsOnly(arenaHalfSize, preferred, out feetPosition))
            return true;

        return false;
    }

    private static bool TryFindValidStreetSpawnNear(Vector3 seed, float maxDrift, out Vector3 validPosition)
    {
        validPosition = default;
        Vector2 seedXZ = new Vector2(seed.x, seed.z);
        float maxDriftSq = maxDrift * maxDrift;

        Vector2[] offsets =
        {
            Vector2.zero,
            new Vector2( 2f,  0f), new Vector2(-2f,  0f),
            new Vector2( 0f,  2f), new Vector2( 0f, -2f),
            new Vector2( 4f,  0f), new Vector2(-4f,  0f),
            new Vector2( 0f,  4f), new Vector2( 0f, -4f),
        };

        for (int o = 0; o < offsets.Length; o++)
        {
            Vector3 probe = seed + new Vector3(offsets[o].x, 1.5f, offsets[o].y);
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, maxDrift, NavMesh.AllAreas))
                continue;

            Vector2 hitXZ = new Vector2(hit.position.x, hit.position.z);
            if ((hitXZ - seedXZ).sqrMagnitude > maxDriftSq)
                continue;

            if (!IsValidPlayerStreetSpawn(hit.position))
                continue;

            validPosition = hit.position;
            return true;
        }

        return false;
    }

    private static bool TryFindOpenPlayerSpawnOnStreetsOnly(float arenaHalfSize, Vector3 preferred, out Vector3 feetPosition)
    {
        feetPosition = default;
        float half = Mathf.Max(30f, arenaHalfSize);
        float step = Mathf.Clamp(half * 0.18f, 8f, 16f);

        var candidates = new System.Collections.Generic.List<Vector3>(64);
        candidates.Add(new Vector3(preferred.x, 0f, preferred.z));

        float limit = half * 0.85f;
        for (float x = -limit; x <= limit + 0.01f; x += step)
        {
            for (float z = -limit; z <= limit + 0.01f; z += step)
                candidates.Add(new Vector3(x, 0f, z));
        }

        ShuffleCandidates(candidates);

        float sampleRadius = Mathf.Max(3f, step * 0.6f);
        for (int i = 0; i < candidates.Count; i++)
        {
            Vector3 seed = candidates[i] + Vector3.up * 1.5f;
            if (NavMesh.SamplePosition(seed, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas)
                && IsValidPlayerStreetSpawn(hit.position))
            {
                feetPosition = hit.position;
                return true;
            }
        }

        return false;
    }

    private static bool TryAlignFeetToStreetGround(Vector3 candidate, out Vector3 feet)
    {
        feet = candidate;
        int mask = SpawnPhysicsMask;
        Vector3 probe = candidate + Vector3.up * 2f;

        if (!Physics.Raycast(probe, Vector3.down, out RaycastHit ground, 8f, mask, QueryTriggerInteraction.Ignore))
            return false;

        if (!IsStreetSurfaceCollider(ground.collider))
            return false;

        float deltaY = candidate.y - ground.point.y;
        if (deltaY < -MaxFeetBelowGround || deltaY > MaxFeetAboveGround + 1.25f)
            return false;

        feet = ground.point + Vector3.up * 0.08f;
        return true;
    }

    public static bool IsStreetSurfaceCollider(Collider collider)
    {
        if (collider == null) return false;

        for (Transform t = collider.transform; t != null; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (IsProceduralSpawnSurface(n))
                return false;
            if (IsStreetMeshName(n))
                return true;
            if (LooksLikeBuildingStructureName(n))
                return false;
        }

        return false;
    }

    public static bool IsProceduralSpawnSurface(string lowerName)
    {
        if (string.IsNullOrEmpty(lowerName)) return true;

        return lowerName.Contains("physicsfloor") || lowerName.Contains("physics_floor")
            || lowerName.Contains("arenafloor") || lowerName.Contains("backfill")
            || lowerName.Contains("visibleground_fallback") || lowerName.Contains("edgeblocker")
            || lowerName.Contains("physicswall") || lowerName.Contains("arenaperimeter")
            || lowerName.Contains("arenavisualclosure") || lowerName.Contains("worldarenastabilizer")
            || lowerName.Contains("foundation_") || lowerName.Contains("perimeter_")
            || lowerName.Contains("killvolume") || lowerName == "plane";
    }

    public static bool IsStreetMeshName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return false;
        string lower = objectName.ToLowerInvariant();
        if (IsProceduralSpawnSurface(lower)) return false;
        if (LooksLikeBuildingStructureName(lower)) return false;

        return lower.Contains("road") || lower.Contains("asphalt") || lower.Contains("street")
            || lower.Contains("pavement") || lower.Contains("tarmac") || lower.Contains("sidewalk")
            || lower.Contains("road_set") || lower.Contains("road_with") || lower.Contains("road_tile")
            || lower.Contains("road_up") || lower.Contains("walkway");
    }

    private static bool LooksLikeBuildingStructureName(string lower)
    {
        return lower.Contains("hangar") || lower.Contains("building") || lower.Contains("warehouse")
            || lower.Contains("office") || lower.Contains("interior") || lower.Contains("roof")
            || lower.Contains("ceiling") || lower.Contains("foundation") || lower.Contains("container")
            || lower.Contains("silo") || lower.Contains("tank") || lower.Contains("barrel");
    }

    private static bool IsNearBuildingFootprint(Vector3 feet, float minDistance)
    {
        int mask = StaticGeometryMask;
        float minSq = minDistance * minDistance;
        Collider[] hits = Physics.OverlapSphere(feet + Vector3.up * 1f, minDistance + 1f, mask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hits.Length; i++)
        {
            Collider c = hits[i];
            if (c == null || !c.enabled || c.isTrigger) continue;

            string n = c.name.ToLowerInvariant();
            Transform root = c.transform.root;
            string rootName = root != null ? root.name.ToLowerInvariant() : n;
            if (!LooksLikeBuildingStructureName(n) && !LooksLikeBuildingStructureName(rootName))
            {
                for (Transform t = c.transform; t != null; t = t.parent)
                {
                    if (LooksLikeBuildingStructureName(t.name.ToLowerInvariant()))
                    {
                        n = t.name.ToLowerInvariant();
                        break;
                    }
                }
                if (!LooksLikeBuildingStructureName(n)) continue;
            }

            Vector3 closest = c.ClosestPoint(feet);
            Vector3 delta = closest - feet;
            delta.y = 0f;
            if (delta.sqrMagnitude < minSq)
                return true;
        }

        return false;
    }

    private static void AddStreetAnchor(Vector3 worldPoint)
    {
        if (StreetSpawnAnchors.Count >= 512) return;
        StreetSpawnAnchors.Add(worldPoint);
    }

    private static void OrderAnchorsByPreferred(System.Collections.Generic.List<Vector3> anchors, Vector3 preferred)
    {
        if (anchors == null || anchors.Count < 2) return;
        anchors.Sort((a, b) =>
        {
            float da = (new Vector2(a.x, a.z) - new Vector2(preferred.x, preferred.z)).sqrMagnitude;
            float db = (new Vector2(b.x, b.z) - new Vector2(preferred.x, preferred.z)).sqrMagnitude;
            return da.CompareTo(db);
        });
    }

    private static void ShuffleTail(System.Collections.Generic.List<Vector3> list, int startIndex)
    {
        if (list == null || list.Count - startIndex < 2) return;
        var rng = new System.Random(unchecked((int)System.DateTime.Now.Ticks));
        for (int i = list.Count - 1; i > startIndex; i--)
        {
            int j = rng.Next(startIndex, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// True when the point sits under a low ceiling and is bounded by nearby walls
    /// (hangars, warehouses, sealed rooms). Open courtyards and streets pass.
    /// </summary>
    public static bool IsEnclosedOrIndoor(Vector3 candidate)
    {
        Vector3 feet = candidate;
        if (!TryAlignFeetToGround(candidate, out Vector3 aligned))
            feet = candidate;
        else
            feet = aligned;

        int mask = StaticGeometryMask;
        Vector3 probe = feet + Vector3.up * 1.35f;

        if (!Physics.Raycast(probe, Vector3.up, out RaycastHit ceiling, MaxIndoorCeilingHeight, mask,
                QueryTriggerInteraction.Ignore))
            return false;

        int wallHits = CountHorizontalWallHits(probe, EnclosureWallProbeRange, mask);
        if (wallHits >= 4)
            return true;

        if (wallHits >= 2 && ceiling.distance < 9f)
            return true;

        if (wallHits >= 3 && ceiling.distance < MaxIndoorCeilingHeight)
            return true;

        return false;
    }

    /// <summary>
    /// Finds an outdoor NavMesh point spread across the arena — never inside buildings.
    /// </summary>
    public static bool TryFindOpenPlayerSpawn(float arenaHalfSize, Vector3 preferred, out Vector3 feetPosition)
    {
        feetPosition = default;
        float half = Mathf.Max(30f, arenaHalfSize);
        float step = Mathf.Clamp(half * 0.22f, 10f, 20f);

        var candidates = new System.Collections.Generic.List<Vector3>(64);
        candidates.Add(new Vector3(preferred.x, 0f, preferred.z));

        float limit = half * 0.88f;
        for (float x = -limit; x <= limit + 0.01f; x += step)
        {
            for (float z = -limit; z <= limit + 0.01f; z += step)
                candidates.Add(new Vector3(x, 0f, z));
        }

        ShuffleCandidates(candidates);

        float sampleRadius = Mathf.Max(4f, step * 0.75f);
        float maxDrift = step * 2.5f;

        for (int i = 0; i < candidates.Count; i++)
        {
            Vector3 seed = candidates[i] + Vector3.up * 0.5f;
            if (TryFindValidOutdoorSpawnNear(seed, default, maxDrift, out Vector3 valid)
                && IsValidOutdoorSpawn(valid, default, rejectRooftops: false))
            {
                feetPosition = valid;
                return true;
            }

            if (NavMesh.SamplePosition(seed, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas)
                && IsValidOutdoorSpawn(hit.position, default, rejectRooftops: false))
            {
                feetPosition = hit.position;
                return true;
            }
        }

        return false;
    }

    private static int CountHorizontalWallHits(Vector3 origin, float range, int mask)
    {
        int hits = 0;
        for (int i = 0; i < EnclosureWallRayCount; i++)
        {
            float ang = i * (Mathf.PI * 2f / EnclosureWallRayCount);
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            if (Physics.Raycast(origin, dir, range, mask, QueryTriggerInteraction.Ignore))
                hits++;
        }

        return hits;
    }

    private static void ShuffleCandidates(System.Collections.Generic.List<Vector3> list)
    {
        if (list == null || list.Count < 2) return;
        var rng = new System.Random(unchecked((int)System.DateTime.Now.Ticks));
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public static bool TryAlignFeetToGround(Vector3 candidate, out Vector3 feet)
    {
        feet = candidate;
        int mask = SpawnPhysicsMask;

        Vector3 probe = candidate + Vector3.up * 0.35f;
        if (!Physics.Raycast(probe, Vector3.down, out RaycastHit ground, 2.75f, mask, QueryTriggerInteraction.Ignore))
            return false;

        float deltaY = candidate.y - ground.point.y;
        if (deltaY < -MaxFeetBelowGround || deltaY > MaxFeetAboveGround + 0.5f)
            return false;

        feet = ground.point + Vector3.up * 0.08f;

        if (Physics.Raycast(feet + Vector3.up * 0.12f, Vector3.up, RequiredHeadroom + 0.25f, mask,
                QueryTriggerInteraction.Ignore))
            return false;

        float bodyRadius = 0.45f;
        Vector3 bodyCenter = feet + Vector3.up * (RequiredHeadroom * 0.5f);
        if (Physics.CheckSphere(bodyCenter, bodyRadius, mask, QueryTriggerInteraction.Ignore))
            return false;

        return true;
    }

    public static bool HasHorizontalWallClearance(Vector3 feet)
    {
        int mask = StaticGeometryMask;
        Vector3 chest = feet + Vector3.up * 1f;

        for (int i = 0; i < 8; i++)
        {
            float ang = i * (Mathf.PI * 2f / 8f);
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            if (Physics.Raycast(chest, dir, MinWallClearance, mask, QueryTriggerInteraction.Ignore))
                return false;
        }

        if (Physics.Raycast(feet + Vector3.up * 0.5f, Vector3.up, 4f, mask, QueryTriggerInteraction.Ignore))
            return false;

        return true;
    }

    /// <summary>
    /// Spiral search for a valid outdoor point near <paramref name="seed"/>.
    /// </summary>
    public static bool TryFindValidOutdoorSpawnNear(
        Vector3 seed,
        Vector3 playerNavPos,
        float maxHorizontalDrift,
        out Vector3 validPosition)
    {
        validPosition = default;
        float maxDriftSq = maxHorizontalDrift * maxHorizontalDrift;
        Vector2 seedXZ = new Vector2(seed.x, seed.z);

        float[] sampleRadii = { 1.5f, 4f, 8f, 14f, 22f };
        Vector2[] offsets =
        {
            Vector2.zero,
            new Vector2( 2f,  0f), new Vector2(-2f,  0f),
            new Vector2( 0f,  2f), new Vector2( 0f, -2f),
            new Vector2( 3f,  3f), new Vector2(-3f, -3f),
            new Vector2( 3f, -3f), new Vector2(-3f,  3f),
        };

        for (int r = 0; r < sampleRadii.Length; r++)
        {
            for (int o = 0; o < offsets.Length; o++)
            {
                Vector3 probe = seed + new Vector3(offsets[o].x, 0.5f, offsets[o].y);
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, sampleRadii[r], NavMesh.AllAreas))
                    continue;

                Vector2 hitXZ = new Vector2(hit.position.x, hit.position.z);
                if ((hitXZ - seedXZ).sqrMagnitude > maxDriftSq)
                    continue;

                if (!IsValidOutdoorSpawn(hit.position, playerNavPos, rejectRooftops: true))
                    continue;

                validPosition = hit.position;
                return true;
            }
        }

        return false;
    }

    public static bool TryPlaceAgentOnValidNavMesh(
        NavMeshAgent agent,
        Transform target,
        Vector3 preferred,
        Vector3 playerNavPos)
    {
        if (target == null)
            return false;

        if (agent != null)
            agent.enabled = false;

        const float maxDrift = 14f;
        if (TryFindValidOutdoorSpawnNear(preferred, playerNavPos, maxDrift, out Vector3 valid))
        {
            target.position = valid;
            if (agent != null)
            {
                agent.enabled = true;
                if (agent.isOnNavMesh)
                    agent.Warp(valid);
            }
            return true;
        }

        target.position = preferred;
        if (agent != null)
            agent.enabled = false;
        return false;
    }

    /// <summary>
    /// Capsule overlap against static geometry — used to stop NavMeshAgent/flocking clipping through walls.
    /// </summary>
    public static bool IsCapsuleInsideStaticGeometry(Vector3 worldPosition, float radius, float height)
    {
        float half = Mathf.Max(radius, height * 0.5f - radius);
        Vector3 bottom = worldPosition + Vector3.up * radius;
        Vector3 top = worldPosition + Vector3.up * (height - radius);
        return Physics.CheckCapsule(bottom, top, radius * 0.92f, StaticGeometryMask, QueryTriggerInteraction.Ignore);
    }

    private static bool HasSpawnGroundAndClearance(Vector3 candidate, float requiredHeight)
    {
        int mask = SpawnPhysicsMask;

        Vector3 feetProbe = candidate + Vector3.up * 0.35f;
        if (!Physics.Raycast(feetProbe, Vector3.down, out RaycastHit ground, 2.5f, mask, QueryTriggerInteraction.Ignore))
            return false;

        if (candidate.y < ground.point.y - MaxFeetBelowGround)
            return false;

        if (Physics.Raycast(candidate + Vector3.up * 0.12f, Vector3.up, requiredHeight + 0.25f, mask,
                QueryTriggerInteraction.Ignore))
            return false;

        float bodyRadius = 0.45f;
        Vector3 bodyCenter = candidate + Vector3.up * (requiredHeight * 0.5f);
        return !Physics.CheckSphere(bodyCenter, bodyRadius, mask, QueryTriggerInteraction.Ignore);
    }
}
