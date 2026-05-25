#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Tools > PRISM-7 > Remove Missing Scripts (Prefabs + Open Scene).
/// Walks every prefab under Assets/ and every GameObject in the currently open
/// scene(s) and removes Behaviour entries whose script reference is null.
/// These were causing the "The referenced script (Unknown) on this Behaviour
/// is missing!" console spam at play time.
///
/// Safe: only removes entries where the script GUID can no longer resolve to a
/// MonoScript. Valid scripts are untouched.
/// </summary>
public static class MissingScriptCleaner
{
    [MenuItem("Tools/PRISM-7/Remove Missing Scripts (Prefabs + Open Scene)")]
    public static void RemoveAll()
    {
        int prefabsTouched = CleanPrefabs(out int prefabRemoved);
        int sceneRemoved   = CleanOpenScenes();

        Debug.Log($"[MissingScriptCleaner] Done. Prefab files cleaned: {prefabsTouched} (entries removed: {prefabRemoved}). Open-scene entries removed: {sceneRemoved}.");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static int CleanPrefabs(out int totalRemoved)
    {
        totalRemoved = 0;
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        int touchedFiles = 0;

        for (int i = 0; i < prefabGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
            if (string.IsNullOrEmpty(path)) continue;

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
            if (prefabRoot == null) continue;

            int removed = RemoveMissingRecursive(prefabRoot);
            if (removed > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                touchedFiles++;
                totalRemoved += removed;
            }

            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        return touchedFiles;
    }

    private static int CleanOpenScenes()
    {
        int total = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.IsValid() || !scene.isLoaded) continue;

            GameObject[] roots = scene.GetRootGameObjects();
            int sceneTotal = 0;
            for (int r = 0; r < roots.Length; r++)
                sceneTotal += RemoveMissingRecursive(roots[r]);

            if (sceneTotal > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                total += sceneTotal;
            }
        }

        if (total > 0)
            EditorSceneManager.SaveOpenScenes();

        return total;
    }

    private static int RemoveMissingRecursive(GameObject root)
    {
        int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
        for (int i = 0; i < root.transform.childCount; i++)
            removed += RemoveMissingRecursive(root.transform.GetChild(i).gameObject);
        return removed;
    }
}
#endif
