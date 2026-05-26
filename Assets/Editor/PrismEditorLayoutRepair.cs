#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class PrismEditorLayoutRepair
{
    private const string MaximizeBackupFileName = "CurrentMaximizeLayout.dwlt";

    [InitializeOnLoadMethod]
    private static void OnEditorLoad()
    {
        EditorApplication.delayCall += EnsureMaximizeLayoutBackup;
    }

    [MenuItem("Tools/PRISM/Repair Editor Layout (Maximize Error)")]
    public static void RepairFromMenu()
    {
        EnsureMaximizeLayoutBackup();
        EditorApplication.ExecuteMenuItem("Window/Layouts/Default");
        Debug.Log("[PRISM] Editor layout repaired: maximize backup restored and default layout loaded.");
    }

    private static void EnsureMaximizeLayoutBackup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
            return;

        string libraryDir = Path.Combine(projectRoot, "Library");
        string backupPath = Path.Combine(libraryDir, MaximizeBackupFileName);
        if (File.Exists(backupPath))
            return;

        string sourceLayout = FindLayoutSource(projectRoot);
        if (string.IsNullOrEmpty(sourceLayout) || !File.Exists(sourceLayout))
            return;

        if (!Directory.Exists(libraryDir))
            Directory.CreateDirectory(libraryDir);

        try
        {
            File.Copy(sourceLayout, backupPath, false);
        }
        catch (IOException ex)
        {
            Debug.LogWarning($"[PRISM] Could not create {MaximizeBackupFileName}: {ex.Message}");
        }
    }

    private static string FindLayoutSource(string projectRoot)
    {
        string layoutsDir = Path.Combine(projectRoot, "UserSettings", "Layouts");
        if (!Directory.Exists(layoutsDir))
            return null;

        string[] dwltFiles = Directory.GetFiles(layoutsDir, "*.dwlt");
        if (dwltFiles.Length == 0)
            return null;

        for (int i = 0; i < dwltFiles.Length; i++)
        {
            string name = Path.GetFileName(dwltFiles[i]);
            if (name.StartsWith("default", System.StringComparison.OrdinalIgnoreCase))
                return dwltFiles[i];
        }

        return dwltFiles[0];
    }
}
#endif
