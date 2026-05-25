// FixPhotonPlayerPrefab.cs
// Runs once on editor load (and via menu) to ensure the networked Player
// prefab has the PhotonView + PhotonTransformViewClassic components Photon
// requires before PhotonNetwork.Instantiate() will accept it.
//
// Touches ONLY the player prefab. Does not touch combat, enemies, doors,
// UI, weapon grip, or level generation.

#if UNITY_EDITOR && PUN_2_OR_NEWER
using Photon.Pun;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class FixPhotonPlayerPrefab
{
    private const string PrefabPath = "Assets/Murtadha/Prefabs/FirstPersonMelee/Resources/FirstPersonMelee/Player.prefab";
    private const string ResourcePath = "FirstPersonMelee/Player";
    private const string MenuPath = "PRISM/Multiplayer/Fix Photon Player Prefab";

    // Run automatically once when the editor finishes loading scripts.
    static FixPhotonPlayerPrefab()
    {
        EditorApplication.delayCall += RunOnce;
    }

    private static void RunOnce()
    {
        EditorApplication.delayCall -= RunOnce;
        // Skip silently during batch mode (CI builds) or if already done.
        if (Application.isBatchMode) return;
        ApplyFix(silent: true);
    }

    [MenuItem(MenuPath)]
    public static void RunFromMenu()
    {
        ApplyFix(silent: false);
    }

    private static void ApplyFix(bool silent)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu")
        {
            Debug.Log("[PhotonSpawn] Blocked in MainMenu.");
            return;
        }

        Debug.Log($"[PhotonSpawn] prefab path={ResourcePath}");

        GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (root == null)
        {
            Debug.LogError($"[FixPhotonPlayerPrefab] Prefab not found at {PrefabPath}. " +
                           "Update PrefabPath constant to match your project layout.");
            return;
        }

        // Open the prefab for editing.
        using (var scope = new PrefabUtility.EditPrefabContentsScope(PrefabPath))
        {
            GameObject prefabRoot = scope.prefabContentsRoot;
            bool dirty = false;

            // ── 1. PhotonView ──────────────────────────────────────────────
            PhotonView pv = prefabRoot.GetComponent<PhotonView>();
            if (pv == null)
            {
                pv = prefabRoot.AddComponent<PhotonView>();
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Added PhotonView to prefab root.");
            }

            // ── 2. PhotonTransformViewClassic ──────────────────────────────
            PhotonTransformViewClassic ptvc = prefabRoot.GetComponent<PhotonTransformViewClassic>();
            if (ptvc == null)
            {
                ptvc = prefabRoot.AddComponent<PhotonTransformViewClassic>();
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Added PhotonTransformViewClassic to prefab root.");
            }

            // ── 3. NetworkPlayerSync ───────────────────────────────────────
            NetworkPlayerSync nps = prefabRoot.GetComponent<NetworkPlayerSync>();
            if (nps == null)
            {
                nps = prefabRoot.AddComponent<NetworkPlayerSync>();
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Added NetworkPlayerSync to prefab root.");
            }

            // ── 4. Wire ObservedComponents ─────────────────────────────────
            // PhotonView.ObservedComponents is a List<Component>.
            SerializedObject pvSo = new SerializedObject(pv);
            SerializedProperty obsProp = pvSo.FindProperty("ObservedComponents");

            bool hasPtvc = false;
            bool hasNps  = false;
            for (int i = 0; i < obsProp.arraySize; i++)
            {
                Object elem = obsProp.GetArrayElementAtIndex(i).objectReferenceValue;
                if (elem is PhotonTransformViewClassic) hasPtvc = true;
                if (elem is NetworkPlayerSync) hasNps = true;
            }

            if (!hasPtvc)
            {
                obsProp.InsertArrayElementAtIndex(obsProp.arraySize);
                obsProp.GetArrayElementAtIndex(obsProp.arraySize - 1).objectReferenceValue = ptvc;
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Added PhotonTransformViewClassic to ObservedComponents.");
            }

            if (!hasNps)
            {
                obsProp.InsertArrayElementAtIndex(obsProp.arraySize);
                obsProp.GetArrayElementAtIndex(obsProp.arraySize - 1).objectReferenceValue = nps;
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Added NetworkPlayerSync to ObservedComponents.");
            }

            // ── 5. Synchronization = UnreliableOnChange ────────────────────
            SerializedProperty syncProp = pvSo.FindProperty("Synchronization");
            // ViewSynchronization.UnreliableOnChange == 3
            if (syncProp != null && syncProp.enumValueIndex != 3)
            {
                syncProp.enumValueIndex = 3;
                dirty = true;
                Debug.Log("[FixPhotonPlayerPrefab] Set PhotonView Synchronization to UnreliableOnChange.");
            }

            if (dirty)
                pvSo.ApplyModifiedPropertiesWithoutUndo();

            // ── 6. Report ──────────────────────────────────────────────────
            bool pvReady = pv != null
                && prefabRoot.GetComponent<PhotonTransformViewClassic>() != null
                && prefabRoot.GetComponent<NetworkPlayerSync>() != null;

            Debug.Log($"[PhotonSpawn] PhotonView ready={pvReady}");

            if (!silent)
            {
                EditorUtility.DisplayDialog(
                    "Fix Photon Player Prefab",
                    dirty
                        ? $"Prefab updated successfully.\nPath: {ResourcePath}"
                        : $"Prefab was already correct.\nPath: {ResourcePath}",
                    "OK");
            }
        }
    }
}
#endif
