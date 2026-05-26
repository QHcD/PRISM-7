using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds / refreshes <c>WeaponHitAudioDatabase</c> by scanning
/// <c>Assets/MohamedAman/Materials/</c> for files named <c>Level{N}*.mp3</c>
/// (case- and whitespace-insensitive, typo-tolerant).
///
/// Runs automatically when any audio file under that folder is imported, and
/// can be invoked manually from <c>PRISM-7 ▸ Audio ▸ Rebuild Weapon Hit Audio Database</c>.
///
/// Robust matching rules:
///   • Strip everything that isn't a letter or digit, lowercase.
///   • Extract the leading <c>level(\d{1,2})</c> token — that's the slot.
///   • Files that don't start with <c>level&lt;num&gt;</c> are ignored.
///   • If two files map to the same level the later (lexically larger filename)
///     wins, so designers can drop a "Level3Shovel_v2.mp3" alongside the
///     original without first deleting it.
/// </summary>
public static class WeaponHitAudioDatabaseBuilder
{
    private const string SourceFolder      = "Assets/MohamedAman/Materials";
    private const string ResourcesFolder   = "Assets/MohamedAman/Resources";
    private const string DatabaseAssetPath = "Assets/MohamedAman/Resources/WeaponHitAudioDatabase.asset";
    private static readonly Regex LevelRx  = new Regex(@"^level(\d{1,2})", RegexOptions.IgnoreCase);
    private static readonly Regex VictoryRx = new Regex(@"v[ic]+tro?y|victroy", RegexOptions.IgnoreCase);
    private static readonly Regex UIClickRx  = new Regex(@"tapclick", RegexOptions.IgnoreCase);

    // Editor-load safety net: make sure the database exists the moment the
    // project is opened so the very first Play Mode session has clips ready.
    // Idempotent — BuildOrUpdate no-ops when nothing changed.
    [InitializeOnLoadMethod]
    private static void EnsureDatabaseOnEditorLoad()
    {
        EditorApplication.delayCall += () =>
        {
            // Only build if the asset is missing OR the level0 slot is empty,
            // so we don't thrash on every editor reload.
            var db = AssetDatabase.LoadAssetAtPath<WeaponHitAudioDatabase>(DatabaseAssetPath);
            if (db == null || db.levelClips == null || db.levelClips.Length == 0 ||
                AllSlotsEmpty(db.levelClips) || db.victoryClip == null ||
                db.uiClickClip == null || db.uiHoverClip == null)
            {
                BuildOrUpdate(verbose: false);
            }
        };
    }

    private static bool AllSlotsEmpty(AudioClip[] clips)
    {
        for (int i = 0; i < clips.Length; i++) if (clips[i] != null) return false;
        return true;
    }

    [MenuItem("PRISM-7/Audio/Rebuild Weapon Hit Audio Database")]
    public static void RebuildMenu()
    {
        var db = BuildOrUpdate(verbose: true);
        if (db != null) Selection.activeObject = db;
    }

    public static WeaponHitAudioDatabase BuildOrUpdate(bool verbose)
    {
        if (!AssetDatabase.IsValidFolder(SourceFolder))
        {
            if (verbose) Debug.LogWarning($"[WeaponHitAudio] Source folder missing: {SourceFolder}");
            return null;
        }

        EnsureFolder(ResourcesFolder);

        // Load or create the database asset.
        var db = AssetDatabase.LoadAssetAtPath<WeaponHitAudioDatabase>(DatabaseAssetPath);
        bool wasCreated = false;
        if (db == null)
        {
            db = ScriptableObject.CreateInstance<WeaponHitAudioDatabase>();
            db.levelClips = new AudioClip[WeaponHitAudioDatabase.MaxLevel];
            AssetDatabase.CreateAsset(db, DatabaseAssetPath);
            wasCreated = true;
        }
        if (db.levelClips == null || db.levelClips.Length != WeaponHitAudioDatabase.MaxLevel)
            db.levelClips = new AudioClip[WeaponHitAudioDatabase.MaxLevel];

        // Scan every AudioClip under the source folder.
        var clipGuids = AssetDatabase.FindAssets("t:AudioClip", new[] { SourceFolder });
        // Pair (level, path, clip) so we can pick deterministically when conflicts exist.
        var resolved = new Dictionary<int, (string path, AudioClip clip)>();
        AudioClip victoryHit = null;
        string victoryPath = null;
        AudioClip uiHoverHit = null;
        string uiHoverPath = null;
        int uiHoverPriority = 0;
        AudioClip uiClickHit = null;
        string uiClickPath = null;
        int uiClickPriority = 0;

        foreach (var g in clipGuids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var fileName = Path.GetFileNameWithoutExtension(path);

            // Victory clip (filename matches victory / victroy etc.) — handled
            // independently of level slots so a single misc clip in the same
            // folder can be auto-wired into PlayerSfx without manual assignment.
            if (VictoryRx.IsMatch(fileName))
            {
                if (victoryPath == null || string.CompareOrdinal(path, victoryPath) > 0)
                {
                    var vClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (vClip != null) { victoryHit = vClip; victoryPath = path; }
                }
                continue;
            }

            int hoverPriority = GetUiHoverPriority(fileName);
            if (hoverPriority > 0)
            {
                if (hoverPriority > uiHoverPriority ||
                    (hoverPriority == uiHoverPriority && (uiHoverPath == null || string.CompareOrdinal(path, uiHoverPath) > 0)))
                {
                    var hClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (hClip != null)
                    {
                        uiHoverHit = hClip;
                        uiHoverPath = path;
                        uiHoverPriority = hoverPriority;
                    }
                }
                continue;
            }

            int clickPriority = GetUiClickPriority(fileName);
            if (clickPriority > 0)
            {
                if (clickPriority > uiClickPriority ||
                    (clickPriority == uiClickPriority && (uiClickPath == null || string.CompareOrdinal(path, uiClickPath) > 0)))
                {
                    var cClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (cClip != null)
                    {
                        uiClickHit = cClip;
                        uiClickPath = path;
                        uiClickPriority = clickPriority;
                    }
                }
                continue;
            }

            int level = ExtractLevel(fileName);
            if (level < 1 || level > WeaponHitAudioDatabase.MaxLevel) continue;

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;

            // Later (lexically larger) filename wins, so newer numbered variants
            // override originals without deletion.
            if (!resolved.TryGetValue(level, out var existing) ||
                string.CompareOrdinal(path, existing.path) > 0)
            {
                resolved[level] = (path, clip);
            }
        }

        // Apply.
        bool changed = wasCreated;
        if (db.victoryClip != victoryHit)
        {
            db.victoryClip = victoryHit;
            changed = true;
        }
        if (db.uiClickClip != uiClickHit)
        {
            db.uiClickClip = uiClickHit;
            changed = true;
        }
        if (db.uiHoverClip != uiHoverHit)
        {
            db.uiHoverClip = uiHoverHit;
            changed = true;
        }

        var report = new StringBuilder();
        for (int lvl = 1; lvl <= WeaponHitAudioDatabase.MaxLevel; lvl++)
        {
            AudioClip newClip = resolved.TryGetValue(lvl, out var hit) ? hit.clip : null;
            if (db.levelClips[lvl - 1] != newClip)
            {
                db.levelClips[lvl - 1] = newClip;
                changed = true;
            }
            if (verbose)
            {
                report.Append($"  Lv{lvl,2}: ");
                report.AppendLine(newClip != null ? Path.GetFileName(hit.path) : "(none)");
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();
            WeaponHitAudioDatabase.InvalidateCache();
        }

        if (verbose)
        {
            Debug.Log($"[WeaponHitAudio] {(wasCreated ? "Created" : "Refreshed")} {DatabaseAssetPath}\n{report}");
        }
        return db;
    }

    private static int ExtractLevel(string fileName)
    {
        // Normalise: strip everything not a letter/digit, lowercase.
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        var m = LevelRx.Match(sb.ToString());
        if (!m.Success) return 0;
        return int.TryParse(m.Groups[1].Value, out int lvl) ? lvl : 0;
    }

    private static int GetUiHoverPriority(string fileName)
    {
        string token = NormalizeAssetToken(fileName);
        if (token == "uiclickmenusound" || token == "newuiclickmenusound") return 2;
        if (token == "uiclicksound" || token == "uiclicksounds") return 1;
        return 0;
    }

    private static int GetUiClickPriority(string fileName)
    {
        string token = NormalizeAssetToken(fileName);
        if (token == "uiclickmenusound" || token == "newuiclickmenusound") return 2;
        if (UIClickRx.IsMatch(fileName)) return 1;
        return 0;
    }

    private static string NormalizeAssetToken(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        var leaf = Path.GetFileName(folder);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // ── Auto-rebuild on import ──────────────────────────────────────────────
    private class AudioPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (TouchesSourceFolder(imported) || TouchesSourceFolder(deleted) ||
                TouchesSourceFolder(moved) || TouchesSourceFolder(movedFrom))
            {
                EditorApplication.delayCall += () => BuildOrUpdate(verbose: false);
            }
        }

        private static bool TouchesSourceFolder(string[] paths)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
                if (paths[i] != null && paths[i].StartsWith(SourceFolder, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
