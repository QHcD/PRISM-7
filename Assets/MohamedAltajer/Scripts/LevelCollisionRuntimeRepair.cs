using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime helper that adds walkable stair ramp colliders and downgrades
/// small decorative blockers so they don't snag the player.
///
/// Hard rules (do not relax without re-validating gameplay):
///   - Never disable, trigger, or otherwise touch a collider that could
///     plausibly be a wall, floor, ceiling, beam, slab, catwalk support,
///     or any large map structure.
///   - "Decorative" means small AND name-matches an explicit prop keyword
///     AND has a renderer roughly the same size as the collider. If any
///     check is uncertain, KEEP the original collider untouched.
///   - Stair ramps are only created when the candidate bounds have solid
///     ground beneath, clear headroom above, and the new box doesn't
///     intersect any non-stair structure. Failed ramps leave the original
///     stair colliders in place — the player can still walk them, even
///     if bumpily.
/// </summary>
public static class LevelCollisionRuntimeRepair
{
    private const float PlayerRampWidthPadding = 0.9f;
    private const float RampThickness = 0.28f;
    private const int MaxLogs = 80;

    private const float MaxDecorativeAxis = 1.5f;
    private const float RampHeadroom = 2.1f;
    private const float RampGroundProbeDown = 4f;
    private const float RampGroundProbeUp = 0.6f;
    private const float RampIntersectionPadding = 0.05f;

    public static bool debugColliderRepair = false;

    private static int _logCount;
    private static readonly Collider[] _overlapBuffer = new Collider[32];

    public static void RunForCurrentLevel()
    {
        _logCount = 0;

        Transform[] roots = FindLevelRoots();
        int repaired = 0;
        int ramps = 0;
        int rejectedRamps = 0;

        for (int i = 0; i < roots.Length; i++)
        {
            Transform root = roots[i];
            if (root == null)
                continue;

            HashSet<int> rampedBoundsKeys = new HashSet<int>();
            ramps += BuildStairRamps(root, rampedBoundsKeys, out int rejectedHere);
            rejectedRamps += rejectedHere;
            repaired += RepairDecorativeBlockers(root, rampedBoundsKeys);
        }

        Physics.SyncTransforms();
        Log($"[LevelCollisionRepair] complete roots={roots.Length} decorativeRepairs={repaired} stairRampsAdded={ramps} stairRampsRejected={rejectedRamps}");
    }

    private static Transform[] FindLevelRoots()
    {
        string[] names =
        {
            "GENERATED_MAP_RUNTIME",
            "UrbanArenaRoot",
            "FbxMap",
            "SciFiArena",
            "SciFiArena(Clone)"
        };

        List<Transform> roots = new List<Transform>(names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            GameObject go = GameObject.Find(names[i]);
            if (go != null && go.activeInHierarchy && !roots.Contains(go.transform))
                roots.Add(go.transform);
        }

        return roots.ToArray();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DECORATIVE BLOCKER REPAIR — strict, conservative
    // ──────────────────────────────────────────────────────────────────────

    private static int RepairDecorativeBlockers(Transform root, HashSet<int> rampedStairBounds)
    {
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        int repaired = 0;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled || collider.isTrigger)
                continue;
            if (collider.GetComponentInParent<IDamageable>() != null)
                continue;
            if (IsGeneratedRamp(collider.transform))
                continue;

            string path = GetPath(collider.transform);
            string lower = path.ToLowerInvariant();

            // Anything that names itself or any ancestor as structural is OFF LIMITS.
            if (IsStructuralByName(lower) || HasStructuralAncestor(collider.transform))
                continue;

            // Stair detail collider: only disable if there's an accompanying ramp.
            if (LooksLikeStair(lower))
            {
                if (!rampedStairBounds.Contains(BoundsKey(collider.bounds)))
                {
                    Log($"[LevelCollisionRepair] kept stair collider (no validated ramp) path={path}");
                    continue;
                }
                if (MakeNonBlocking(collider, out string mode))
                {
                    repaired++;
                    Log($"[LevelCollisionRepair] stair detail collider made non-blocking path={path} mode={mode}");
                }
                continue;
            }

            // Truly-decorative props only: needs name match + small bounds + renderer evidence.
            if (!IsDecorativeName(lower))
                continue;

            if (!IsSmallProp(collider))
                continue;

            if (!HasMatchingRenderer(collider))
                continue;

            if (MakeNonBlocking(collider, out string propMode))
            {
                repaired++;
                Log($"[LevelCollisionRepair] decorative prop made non-blocking path={path} mode={propMode}");
            }
        }

        return repaired;
    }

    /// <summary>
    /// Converts a collider to non-blocking, preferring isTrigger so queries still
    /// work. Concave MeshColliders can't be triggers so we disable them instead.
    /// Returns false (and changes nothing) for colliders that are already non-blocking.
    /// </summary>
    private static bool MakeNonBlocking(Collider collider, out string mode)
    {
        mode = string.Empty;
        if (collider == null)
            return false;

        MeshCollider mesh = collider as MeshCollider;
        if (mesh != null && !mesh.convex)
        {
            if (!collider.enabled)
                return false;
            collider.enabled = false;
            mode = "disabled-concave-mesh";
            return true;
        }

        if (collider.isTrigger)
            return false;
        collider.isTrigger = true;
        mode = "trigger";
        return true;
    }

    private static bool IsSmallProp(Collider collider)
    {
        Vector3 size = collider.bounds.size;
        float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        return maxAxis <= MaxDecorativeAxis;
    }

    private static bool HasMatchingRenderer(Collider collider)
    {
        // Require at least one enabled renderer on the same GameObject so we
        // know there is something visually small to back this collider — this
        // avoids touching invisible NavMesh proxies, hidden colliders, or
        // structural shells that have no renderer of their own.
        Renderer[] renderers = collider.GetComponents<Renderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r != null && r.enabled)
                return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  STAIR RAMP BUILDING — geometry-validated
    // ──────────────────────────────────────────────────────────────────────

    private static int BuildStairRamps(Transform root, HashSet<int> rampedBoundsKeys, out int rejected)
    {
        rejected = 0;
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        List<(Bounds, Collider)> stairCandidates = new List<(Bounds, Collider)>(8);

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled)
                continue;
            if (IsGeneratedRamp(collider.transform))
                continue;
            if (!LooksLikeStair(GetPath(collider.transform).ToLowerInvariant()))
                continue;
            Bounds b = collider.bounds;
            if (b.size.y < 0.2f || Mathf.Max(b.size.x, b.size.z) < 1.2f)
                continue;
            if (IsDuplicateBounds(stairCandidates, b))
                continue;
            stairCandidates.Add((b, collider));
        }

        int count = 0;
        Transform rampRoot = null;
        for (int i = 0; i < stairCandidates.Count; i++)
        {
            Bounds bounds = stairCandidates[i].Item1;

            if (!ValidateRampSite(bounds, out string rejectReason))
            {
                rejected++;
                Log($"[LevelCollisionRepair] rejected stair ramp idx={i} reason={rejectReason} bounds={bounds}");
                continue;
            }

            if (rampRoot == null)
                rampRoot = GetOrCreateRampRoot(root);

            if (CreateRampForBounds(rampRoot, bounds, count))
            {
                rampedBoundsKeys.Add(BoundsKey(bounds));
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Validates that a stair bounds is safe to wrap with a walkable ramp:
    /// must have solid ground beneath both ends, clear headroom above the
    /// would-be walk surface, and not overlap any non-stair structural collider.
    /// </summary>
    private static bool ValidateRampSite(Bounds bounds, out string reason)
    {
        bool longX = bounds.size.x >= bounds.size.z;
        float run = Mathf.Max(0.1f, longX ? bounds.size.x : bounds.size.z);
        Vector3 axis = longX ? Vector3.right : Vector3.forward;
        Vector3 lowProbe = bounds.center - axis * (run * 0.42f);
        Vector3 highProbe = bounds.center + axis * (run * 0.42f);

        if (!HasSolidGroundBeneath(lowProbe, bounds, out float lowY))
        {
            reason = $"no ground beneath low end at {lowProbe}";
            return false;
        }
        if (!HasSolidGroundBeneath(highProbe, bounds, out float highY))
        {
            reason = $"no ground beneath high end at {highProbe}";
            return false;
        }

        // Sanity: stairs go up a single floor. If the two ends are vertically
        // far apart, this isn't a single staircase — it's likely two stacked
        // stairs the bounds heuristic merged together.
        if (Mathf.Abs(highY - lowY) > 6f)
        {
            reason = $"vertical delta too large lowY={lowY:F2} highY={highY:F2}";
            return false;
        }

        float topY = Mathf.Max(lowY, highY) + 0.15f;
        Vector3 headroomOrigin = new Vector3(bounds.center.x, topY + 0.05f, bounds.center.z);
        float width = Mathf.Max(0.8f, longX ? bounds.size.z : bounds.size.x);
        if (!HasHeadroom(headroomOrigin, width * 0.45f, RampHeadroom))
        {
            reason = $"insufficient headroom above {headroomOrigin}";
            return false;
        }

        // The walkable ramp box itself, slightly shrunk so we don't false-
        // positive against the stair colliders we're replacing.
        Vector3 rampCenter = new Vector3((lowProbe.x + highProbe.x) * 0.5f,
            (lowY + highY) * 0.5f + 0.08f,
            (lowProbe.z + highProbe.z) * 0.5f);
        Vector3 rampHalf = longX
            ? new Vector3(run * 0.5f * 0.95f, RampThickness * 0.5f, width * 0.5f * 0.95f)
            : new Vector3(width * 0.5f * 0.95f, RampThickness * 0.5f, run * 0.5f * 0.95f);

        if (OverlapsNonStairStructure(rampCenter, rampHalf, Quaternion.identity))
        {
            reason = $"ramp box overlaps non-stair structure at {rampCenter}";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool HasSolidGroundBeneath(Vector3 probe, Bounds bounds, out float groundY)
    {
        groundY = bounds.center.y;
        Vector3 origin = new Vector3(probe.x, bounds.max.y + RampGroundProbeUp, probe.z);
        float distance = bounds.size.y + RampGroundProbeUp + RampGroundProbeDown;
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;

        // The hit must be a roughly upward surface — ceilings facing down don't count.
        if (hit.normal.y < 0.4f)
            return false;
        groundY = hit.point.y;
        return true;
    }

    private static bool HasHeadroom(Vector3 origin, float radius, float height)
    {
        Vector3 bottom = origin + Vector3.up * Mathf.Max(0.05f, radius);
        Vector3 top = origin + Vector3.up * Mathf.Max(0.06f, height - radius);
        int hits = Physics.OverlapCapsuleNonAlloc(bottom, top, Mathf.Max(0.1f, radius),
            _overlapBuffer, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider c = _overlapBuffer[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;
            // Stair geometry itself is fine in the headroom check — only foreign blockers count.
            if (LooksLikeStair(GetPath(c.transform).ToLowerInvariant()))
                continue;
            return false;
        }
        return true;
    }

    private static bool OverlapsNonStairStructure(Vector3 center, Vector3 halfExtents, Quaternion orientation)
    {
        Vector3 padded = halfExtents + Vector3.one * RampIntersectionPadding;
        int hits = Physics.OverlapBoxNonAlloc(center, padded, _overlapBuffer, orientation,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider c = _overlapBuffer[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;
            if (c.GetComponentInParent<IDamageable>() != null)
                continue;
            if (IsGeneratedRamp(c.transform))
                continue;
            string lower = GetPath(c.transform).ToLowerInvariant();
            if (LooksLikeStair(lower))
                continue;
            // Walls/ceilings/beams are exactly what we must NOT cross.
            if (IsStructuralByName(lower))
                return true;
            // Any other large collider also blocks ramp placement.
            Vector3 s = c.bounds.size;
            if (Mathf.Max(s.x, Mathf.Max(s.y, s.z)) > 2f)
                return true;
        }
        return false;
    }

    private static Transform GetOrCreateRampRoot(Transform root)
    {
        Transform existing = root.Find("RuntimeStairRampColliders");
        if (existing != null)
            return existing;

        GameObject go = new GameObject("RuntimeStairRampColliders");
        go.transform.SetParent(root, false);
        return go.transform;
    }

    private static bool CreateRampForBounds(Transform parent, Bounds bounds, int index)
    {
        bool longX = bounds.size.x >= bounds.size.z;
        float run = Mathf.Max(0.1f, longX ? bounds.size.x : bounds.size.z);
        float width = Mathf.Max(0.8f, longX ? bounds.size.z : bounds.size.x) + PlayerRampWidthPadding;

        Vector3 axis = longX ? Vector3.right : Vector3.forward;
        Vector3 lowProbe = bounds.center - axis * (run * 0.42f);
        Vector3 highProbe = bounds.center + axis * (run * 0.42f);

        if (!HasSolidGroundBeneath(lowProbe, bounds, out float lowY))
            return false;
        if (!HasSolidGroundBeneath(highProbe, bounds, out float highY))
            return false;

        float deltaY = highY - lowY;
        if (Mathf.Abs(deltaY) < 0.15f)
            deltaY = Mathf.Max(0.35f, bounds.size.y);

        Vector3 center = (lowProbe + highProbe) * 0.5f;
        center.y = (lowY + highY) * 0.5f + 0.08f;

        GameObject ramp = new GameObject($"Runtime_StairRampCollider_{index:00}");
        ramp.transform.SetParent(parent, true);
        ramp.transform.position = center;
        ramp.transform.rotation = longX
            ? Quaternion.Euler(0f, 0f, Mathf.Atan2(deltaY, run) * Mathf.Rad2Deg)
            : Quaternion.Euler(-Mathf.Atan2(deltaY, run) * Mathf.Rad2Deg, 0f, 0f);

        BoxCollider box = ramp.AddComponent<BoxCollider>();
        box.size = longX
            ? new Vector3(run * 1.08f, RampThickness, width)
            : new Vector3(width, RampThickness, run * 1.08f);
        box.center = Vector3.zero;

        int walkable = LayerMask.NameToLayer("Walkable");
        int environment = LayerMask.NameToLayer("Environment");
        if (walkable >= 0)
            ramp.layer = walkable;
        else if (environment >= 0)
            ramp.layer = environment;

        ramp.isStatic = true;
        Log($"[LevelCollisionRepair] stair ramp added name={ramp.name} bounds={bounds} run={run:F1} width={width:F1} y=({lowY:F2}->{highY:F2})");
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  CLASSIFICATION HELPERS
    // ──────────────────────────────────────────────────────────────────────

    private static bool IsDuplicateBounds(List<(Bounds, Collider)> existing, Bounds candidate)
    {
        for (int i = 0; i < existing.Count; i++)
        {
            Bounds b = existing[i].Item1;
            if ((b.center - candidate.center).sqrMagnitude < 4f)
                return true;
        }
        return false;
    }

    private static int BoundsKey(Bounds b)
    {
        // Quantize center to a stable hash so the ramp/disable phases agree on identity.
        int x = Mathf.RoundToInt(b.center.x * 4f);
        int y = Mathf.RoundToInt(b.center.y * 4f);
        int z = Mathf.RoundToInt(b.center.z * 4f);
        unchecked
        {
            int h = 17;
            h = h * 31 + x;
            h = h * 31 + y;
            h = h * 31 + z;
            return h;
        }
    }

    private static bool LooksLikeStair(string lower)
    {
        return lower.Contains("stair") || lower.Contains("stairs") || lower.Contains("staircase")
            || lower.Contains("step_") || lower.EndsWith("/step") || lower.Contains("/steps");
    }

    /// <summary>
    /// Conservative decorative-prop name match. Keep this list small — false
    /// positives here will disable real walls / floors. When in doubt, leave
    /// the keyword off and let the collider survive.
    /// </summary>
    private static bool IsDecorativeName(string lower)
    {
        return lower.Contains("pipe") || lower.Contains("cable") || lower.Contains("wire")
            || lower.Contains("vent") || lower.Contains("decor")
            || lower.Contains("grate") || lower.Contains("grill")
            || lower.Contains("trim") || lower.Contains("debris")
            || lower.Contains("rubble") || lower.Contains("clutter");
    }

    /// <summary>
    /// Anything that *might* be load-bearing. We err on the side of marking
    /// things structural — a collider kept in error is invisible to the player,
    /// a collider disabled in error drops them through the world.
    /// </summary>
    private static bool IsStructuralByName(string lower)
    {
        return lower.Contains("floor") || lower.Contains("ground") || lower.Contains("wall")
            || lower.Contains("ceiling") || lower.Contains("roof") || lower.Contains("platform")
            || lower.Contains("walkway") || lower.Contains("catwalk") || lower.Contains("corridor")
            || lower.Contains("room") || lower.Contains("navmeshproxy") || lower.Contains("doorframe")
            || lower.Contains("beam") || lower.Contains("column") || lower.Contains("pillar")
            || lower.Contains("slab") || lower.Contains("support") || lower.Contains("girder")
            || lower.Contains("truss") || lower.Contains("frame") || lower.Contains("structure")
            || lower.Contains("base") || lower.Contains("foundation")
            || lower.Contains("runtime_stairrampcollider");
    }

    private static bool HasStructuralAncestor(Transform transform)
    {
        for (Transform t = transform != null ? transform.parent : null; t != null; t = t.parent)
        {
            string lower = t.name.ToLowerInvariant();
            if (IsStructuralByName(lower))
                return true;
        }
        return false;
    }

    private static bool IsGeneratedRamp(Transform transform)
    {
        return transform != null && GetPath(transform).ToLowerInvariant().Contains("runtimestairrampcolliders");
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
            return string.Empty;

        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    private static void Log(string message)
    {
        if (!debugColliderRepair || _logCount >= MaxLogs)
            return;
        _logCount++;
        Debug.Log(message);
    }
}
