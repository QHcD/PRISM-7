using UnityEngine;

/// <summary>
/// Legacy safety shim. The old sealer generated individual ceiling tiles,
/// beams, lights, and duct-like decor at runtime. PRISM-7 now uses complete
/// assembled warehouse modules, so this component only removes stale objects.
/// </summary>
[DisallowMultipleComponent]
public class UniversalCeilingSealer : MonoBehaviour
{
    private const string TilesGroup    = "UNIVERSAL_CEILING_TILES";
    private const string LightsGroup   = "UNIVERSAL_CEILING_LIGHTS";
    private const string SupportsGroup = "UNIVERSAL_CEILING_SUPPORTS";
    private const string LegacyDecorGroup = "UNIVERSAL_CEILING_DECOR";
    private const string LegacyCapName    = "UNIVERSAL_CEILING_CAP";
    private const string LegacyHostName   = "UNIVERSAL_CEILING_Host";

    public static int CleanupLegacyRuntimePieces()
    {
        int removed = 0;
        GameObject[] all = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            GameObject g = all[i];
            if (g == null) continue;
            string n = g.name;
            if (n == LegacyCapName || n.StartsWith(LegacyCapName + "_Skirt")
                || n == TilesGroup || n == LightsGroup || n == SupportsGroup
                || n == LegacyDecorGroup || n == LegacyHostName)
            {
                DestroyObjectSafe(g);
                removed++;
            }
        }

        if (removed > 0)
            Debug.Log($"[UniversalCeilingSealer] Removed {removed} legacy procedural ceiling object(s). Runtime ceiling generation is disabled.");

        return removed;
    }

    public void ScheduleRebuild()
    {
        CleanupLegacyRuntimePieces();
    }

    private void Awake()
    {
        CleanupLegacyRuntimePieces();
        enabled = false;
    }

    private static void DestroyObjectSafe(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
