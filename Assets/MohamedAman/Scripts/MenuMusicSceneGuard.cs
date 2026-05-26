using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// Ensures the DontDestroyOnLoad "LobbyMusic" AudioSource (spawned by
/// RuntimeMenuBuilder.SetupLobbyMusic) is silenced on any non-menu scene
/// and re-enabled when returning to the menu. Also prunes duplicate
/// LobbyMusic GameObjects that can accumulate across scene reloads.
///
/// Pure additive helper — does not modify RuntimeMenuBuilder or
/// AudioSettingsRuntime.
/// </summary>
public static class MenuMusicSceneGuard
{
    private const float FadeDuration = 0.45f;
    private const string MusicObjectName = "LobbyMusic";
    private const string ThemeName = "MainMenu_LobbyTheme";
    private static bool _loadRoutineActive;
    private static MenuMusicSingleton _instance;

    private struct MenuMusicCandidate
    {
        public string Url;
        public AudioType Type;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        // Apply once for the boot scene too.
        ApplyForScene(SceneManager.GetActiveScene());
    }

    private static void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        // Only react to the new active scene becoming the primary scene.
        if (mode != LoadSceneMode.Single) return;
        ApplyForScene(s);
    }

    private static void ApplyForScene(Scene s)
    {
        PruneDuplicateLobbyMusic();

        bool isMenu = IsMenuScene(s.name);
        if (isMenu)
        {
            EnsureMenuMusicPlaying();
            return;
        }

        GameObject host = GameObject.Find(MusicObjectName);
        if (host == null) return;
        AudioSource src = host.GetComponent<AudioSource>();
        if (src == null) return;

        // Fade out so transitions don't pop.
        CoroutineHost.Run(FadeAndStop(src, FadeDuration));
    }

    public static AudioSource EnsureMenuMusicPlaying()
    {
        AudioSource src = EnsureMenuMusicSource();

        ApplyMenuMusicSourceSettings(src);

        if (src.clip != null)
        {
            if (!src.isPlaying)
                src.Play();
            return src;
        }

        AudioClip resourcesClip = Resources.Load<AudioClip>(ThemeName);
        if (resourcesClip != null)
        {
            src.clip = resourcesClip;
            src.Play();
            return src;
        }

        if (!_loadRoutineActive)
            CoroutineHost.Run(LoadMenuMusicFromDisk(src));
        return src;
    }

    public static AudioSource PlayMenuTheme(AudioClip clip)
    {
        if (clip == null)
            return EnsureMenuMusicPlaying();

        AudioSource src = EnsureMenuMusicSource();
        ApplyMenuMusicSourceSettings(src);

        if (src.clip != null && IsSameThemeClip(src.clip, clip))
        {
            if (!src.isPlaying)
                src.Play();
            return src;
        }

        if (src.isPlaying && IsThemeClip(src.clip) && IsThemeClip(clip))
            return src;

        src.clip = clip;
        if (!src.isPlaying)
            src.Play();
        return src;
    }

    private static void ApplyMenuMusicSourceSettings(AudioSource src)
    {
        if (src == null) return;
        src.loop = true;
        src.playOnAwake = false;
        src.mute = false;
        src.spatialBlend = 0f;
        src.ignoreListenerPause = false;
        src.volume = AudioSettingsRuntime.ScaledMusic(AudioSettingsRuntime.MenuLobbyMusicDesignMix);
    }

    private static IEnumerator LoadMenuMusicFromDisk(AudioSource src)
    {
        _loadRoutineActive = true;
        MenuMusicCandidate[] candidates = BuildMenuMusicCandidates();

        for (int i = 0; i < candidates.Length; i++)
        {
            if (src == null || !IsMenuScene(SceneManager.GetActiveScene().name))
                break;

            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(candidates[i].Url, candidates[i].Type))
            {
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                    continue;

                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip == null)
                    continue;

                clip.name = ThemeName;
                if (src.isPlaying && IsThemeClip(src.clip))
                {
                    _loadRoutineActive = false;
                    yield break;
                }

                src.clip = clip;
                ApplyMenuMusicSourceSettings(src);
                if (!src.isPlaying)
                    src.Play();
                _loadRoutineActive = false;
                yield break;
            }
        }

        _loadRoutineActive = false;
    }

    private static MenuMusicCandidate[] BuildMenuMusicCandidates()
    {
        var candidates = new List<MenuMusicCandidate>(12);

        void AddFileIfPresent(string absolutePath, AudioType type)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return;
            candidates.Add(new MenuMusicCandidate
            {
                Url = "file:///" + absolutePath.Replace("\\", "/"),
                Type = type
            });
        }

        void AddFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            AddFileIfPresent(Path.Combine(folder, ThemeName + ".ogg"), AudioType.OGGVORBIS);
            AddFileIfPresent(Path.Combine(folder, ThemeName + ".wav"), AudioType.WAV);
            AddFileIfPresent(Path.Combine(folder, ThemeName + ".mp3"), AudioType.MPEG);
        }

        AddFolder(Path.Combine(Application.dataPath, "MohamedAman", "Resources"));
        AddFolder(Path.Combine(Application.dataPath, "MohamedAman", "StreamingAssets"));
        AddFolder(Path.Combine(Application.dataPath, "Audio"));
        AddFolder(Application.streamingAssetsPath);

        return candidates.ToArray();
    }

    public static bool IsMenuSceneName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return false;
        string n = sceneName.ToLowerInvariant();

        // Gameplay scene names (explicitly NOT menu context)
        if (n.Contains("gamescene") || n.Contains("multiplayergamescene") || n.Contains("gameplay"))
            return false;

        // Treat all menu-related screens and pages as menu context
        return n.Contains("mainmenu") 
            || n.Contains("lobby") 
            || n.Contains("settings") 
            || n.Contains("options") 
            || n.Contains("credits")
            || n.Contains("selectlevel")
            || n.Contains("select_level")
            || n.Contains("multiplayer")
            || n.Contains("challenges")
            || n.Contains("prismstore");
    }

    private static bool IsMenuScene(string sceneName)
    {
        return IsMenuSceneName(sceneName);
    }

    private static IEnumerator FadeAndStop(AudioSource src, float duration)
    {
        if (src == null) yield break;
        float startVol = src.volume;
        float t = 0f;
        while (t < duration && src != null)
        {
            if (IsMenuScene(SceneManager.GetActiveScene().name))
                yield break;

            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            src.volume = Mathf.Lerp(startVol, 0f, k);
            yield return null;
        }
        if (src != null)
        {
            if (IsMenuScene(SceneManager.GetActiveScene().name))
                yield break;

            src.Stop();
            src.volume = 0f;
        }
    }

    private static void PruneDuplicateLobbyMusic()
    {
#if UNITY_2023_1_OR_NEWER
        GameObject[] all = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
#else
        GameObject[] all = Object.FindObjectsOfType<GameObject>();
#endif
        GameObject first = _instance != null ? _instance.gameObject : null;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i].name != MusicObjectName) continue;
            if (first == null) { first = all[i]; continue; }
            if (first == all[i]) continue;
            Object.Destroy(all[i]);
        }
    }

    private static AudioSource EnsureMenuMusicSource()
    {
        PruneDuplicateLobbyMusic();

        GameObject host = _instance != null ? _instance.gameObject : GameObject.Find(MusicObjectName);
        if (host == null)
            host = new GameObject(MusicObjectName);
        if (host.name != MusicObjectName)
            host.name = MusicObjectName;

        MenuMusicSingleton singleton = host.GetComponent<MenuMusicSingleton>();
        if (singleton == null)
            singleton = host.AddComponent<MenuMusicSingleton>();
        if (_instance == null)
            _instance = singleton;

        AudioSource src = host.GetComponent<AudioSource>();
        if (src == null)
            src = host.AddComponent<AudioSource>();
        return src;
    }

    private static bool IsSameThemeClip(AudioClip a, AudioClip b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        return IsThemeClip(a) && IsThemeClip(b);
    }

    private static bool IsThemeClip(AudioClip clip)
    {
        return clip != null && string.Equals(clip.name, ThemeName, System.StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MenuMusicSingleton : MonoBehaviour
    {
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Object.Destroy(gameObject);
                return;
            }

            _instance = this;
            gameObject.name = MusicObjectName;
            Object.DontDestroyOnLoad(gameObject);
        }
    }

    /// <summary>Tiny MonoBehaviour host so static code can run coroutines.</summary>
    private class CoroutineHost : MonoBehaviour
    {
        private static CoroutineHost _instance;
        public static void Run(IEnumerator routine)
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("MenuMusicSceneGuardHost");
                Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<CoroutineHost>();
            }
            _instance.StartCoroutine(routine);
        }
    }
}
