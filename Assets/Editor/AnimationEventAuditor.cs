#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Tools > PRISM-7 > Audit UnarmedLightAttack1 Receivers.
/// Walks every Animator in the active scene, finds the ones whose
/// RuntimeAnimatorController references a clip named "UnarmedLightAttack1",
/// and prints the full hierarchy path of the Animator's GameObject plus the
/// state of receiver components on that exact GameObject. Use this to verify
/// the runtime is wired up (sinks present and reachable on the host that
/// actually plays the clip).
/// </summary>
public static class AnimationEventAuditor
{
    [MenuItem("Tools/PRISM-7/Audit UnarmedLightAttack1 Receivers")]
    public static void AuditOpenScene()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[AnimAudit] Enter Play mode first — runtime-spawned bodies (player Crosby, enemy Crosbys) only exist while the game is running.");
            return;
        }

        Animator[] animators = Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        StringBuilder sb = new StringBuilder();
        int matched = 0;

        for (int i = 0; i < animators.Length; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;
            RuntimeAnimatorController rc = a.runtimeAnimatorController;
            if (rc == null) continue;

            bool hasClip = false;
            AnimationClip[] clips = rc.animationClips;
            for (int c = 0; c < clips.Length; c++)
            {
                if (clips[c] != null && clips[c].name == "UnarmedLightAttack1")
                {
                    hasClip = true;
                    break;
                }
            }
            if (!hasClip) continue;

            matched++;
            GameObject go = a.gameObject;
            string path = GetHierarchyPath(go.transform);
            bool hasSink   = go.GetComponent<AnimationEventSink>()      != null;
            bool hasMelee  = go.GetComponent<MeleeAnimationEventSink>() != null;
            sb.AppendLine($"[AnimAudit] {path}");
            sb.AppendLine($"   AnimationEventSink: {(hasSink ? "YES" : "MISSING")}");
            sb.AppendLine($"   MeleeAnimationEventSink: {(hasMelee ? "YES" : "MISSING")}");
        }

        if (matched == 0)
            Debug.Log("[AnimAudit] No Animator in the scene currently references a clip named 'UnarmedLightAttack1'.");
        else
            Debug.Log("[AnimAudit] Matches: " + matched + "\n" + sb);
    }

    private static string GetHierarchyPath(Transform t)
    {
        if (t == null) return "<null>";
        StringBuilder sb = new StringBuilder(t.name);
        Transform p = t.parent;
        while (p != null)
        {
            sb.Insert(0, p.name + "/");
            p = p.parent;
        }
        return sb.ToString();
    }
}
#endif
