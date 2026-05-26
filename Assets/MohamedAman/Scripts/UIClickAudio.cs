using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Global UI click-sound bootstrap. Auto-attaches a click sound effect to
/// every <see cref="Button"/> in every scene loaded for the rest of the
/// session.
///
/// Click clip source (in priority order):
///   1. Runtime override via <see cref="SetClickClip"/> — used by
///      <see cref="PlayerSfx"/> to push the project's
///      Assets/MohamedAman/Materials/UIClickMenuSound.mp3 in at Awake.
///   2. <c>Resources/Audio/UIClickMenuSound</c> as <see cref="AudioClip"/>
///      (drop a .wav/.ogg/.mp3 with that name to override globally).
///   3. A snappy procedural click synthesised at runtime — guaranteed
///      fallback so menus are never silent.
///
/// The legacy VideoClip-based loader (which read a .mov via VideoPlayer)
/// was removed — PlayerSfx + the AudioClip override path are the only
/// supported routes. If a legacy ClickButtonSound.mov is still present in
/// Resources at boot, a one-shot warning is emitted so it can be deleted.
/// </summary>
public class UIClickAudio : MonoBehaviour
{
    public  static UIClickAudio Instance { get; private set; }
    private const string ResourcePath = "Audio/ClickButtonSound";

    public float LastPlayTime { get; private set; } = -100f;

    [Header("Mixing")]
    [Tooltip("Master volume scalar applied to PlayOneShot.")]
    public float volume = 0.55f;

    [Tooltip("Pitch jitter range — gives consecutive clicks a subtle variety.")]
    public Vector2 pitchJitter = new Vector2(0.96f, 1.04f);

    private AudioSource _audioSource;
    private AudioClip   _clickClip;
    private AudioClip   _hoverClip;
    private float       _lastClickTime = -100f;

    // Track the buttons we've already wired so we don't stack listeners on
    // the same Button across multiple scene loads.
    private readonly HashSet<int> _attachedInstanceIds = new HashSet<int>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject host = new GameObject("UIClickAudio");
        DontDestroyOnLoad(host);
        Instance = host.AddComponent<UIClickAudio>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.loop        = false;
        _audioSource.spatialBlend = 0f;

        ResolveClickClip();

        SceneManager.sceneLoaded += OnSceneLoaded;

        // Attach to anything that's already loaded (e.g. the boot menu).
        AttachToActiveScene();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        // Clear to release references to destroyed buttons (prevents slow leak)
        _attachedInstanceIds.Clear();
        // Defer one frame so dynamically-built canvases (RuntimeMenuBuilder
        // creates buttons in Awake/Start) finish spawning their children.
        StartCoroutine(AttachAfterFrame());
    }

    private System.Collections.IEnumerator AttachAfterFrame()
    {
        yield return null;
        AttachToActiveScene();
    }

    /// <summary>
    /// Runtime override for the click clip. Used by PlayerSfx so a single
    /// Inspector field on the Player prefab can route the project's UI click
    /// asset (e.g. UIClickMenuSound.mp3) into this global system without needing
    /// the asset to live under a Resources/ folder.
    /// </summary>
    public void SetClickClip(AudioClip clip)
    {
        if (clip == null) return;
        _clickClip = clip;
    }

    public void SetHoverClip(AudioClip clip)
    {
        if (clip == null) return;
        _hoverClip = clip;
    }

    /// <summary>
    /// Plays the click sound. Safe to call from any UnityEvent listener.
    /// </summary>
    public void PlayClick()
    {
        if (_audioSource == null) return;

        LastPlayTime = Time.unscaledTime;
        _lastClickTime = Time.unscaledTime;

        // Subtle pitch jitter so spamming buttons doesn't sound robotic.
        _audioSource.pitch = Random.Range(pitchJitter.x, pitchJitter.y);

        float vol = Mathf.Clamp01(volume) * AudioSettingsRuntime.ScaledUi(1f);
        if (_clickClip != null && vol > 0f)
            _audioSource.PlayOneShot(_clickClip, vol);
    }

    /// <summary>
    /// Plays the hover/navigation sound. Safe to call from any listener.
    /// </summary>
    public void PlayHover()
    {
        if (_audioSource == null) return;

        // Do not play hover/select sound on the exact click frame
        if (Time.unscaledTime - _lastClickTime < 0.05f) return;

        _audioSource.pitch = Random.Range(pitchJitter.x, pitchJitter.y);

        float vol = Mathf.Clamp01(volume * 0.7f) * AudioSettingsRuntime.ScaledUi(1f);
        if (_hoverClip != null && vol > 0f)
            _audioSource.PlayOneShot(_hoverClip, vol);
    }

    /// <summary>
    /// Walks every loaded scene + every active <see cref="Button"/> and
    /// adds <see cref="PlayClick"/> as an additional onClick listener.
    /// Idempotent — re-runs on scene load are cheap because every wired
    /// button is registered in <see cref="_attachedInstanceIds"/>.
    /// </summary>
    public void AttachToActiveScene()
    {
        // FindObjectsByType is the modern API; FindObjectOfType is deprecated
        // on Unity 6 and emits CS0618 warnings.
#if UNITY_2023_1_OR_NEWER
        Button[] buttons = Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        Button[] buttons = Object.FindObjectsOfType<Button>(true);
#endif
        for (int i = 0; i < buttons.Length; i++) AttachIfNeeded(buttons[i]);
    }

    /// <summary>
    /// Public hook so any script that programmatically spawns a button
    /// can opt-in immediately without waiting for the next scene-load tick.
    /// </summary>
    public void AttachIfNeeded(Button button)
    {
        if (button == null) return;
        int id = button.GetInstanceID();
        if (_attachedInstanceIds.Contains(id)) return;
        _attachedInstanceIds.Add(id);

        button.onClick.RemoveListener(PlayClick);
        button.onClick.AddListener(PlayClick);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Click-clip resolution
    // ────────────────────────────────────────────────────────────────────────

    private void ResolveClickClip()
    {
        if (_clickClip == null && WeaponHitAudioDatabase.Instance != null)
        {
            _clickClip = WeaponHitAudioDatabase.Instance.uiClickClip;
        }
        if (_hoverClip == null && WeaponHitAudioDatabase.Instance != null)
        {
            _hoverClip = WeaponHitAudioDatabase.Instance.uiHoverClip;
        }

        // Support loading from Assets/MohamedAman/Materials/ in editor as secondary fallback
#if UNITY_EDITOR
        if (_clickClip == null)
            _clickClip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MohamedAman/Materials/UIClickMenuSound.mp3");
        if (_hoverClip == null)
            _hoverClip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MohamedAman/Materials/UIClickMenuSound.mp3");
#endif

        if (_clickClip == null)
        {
            _clickClip = Resources.Load<AudioClip>("Audio/UIClickMenuSound");
        }
        if (_clickClip == null)
        {
            AudioClip ac = Resources.Load<AudioClip>(ResourcePath);
            if (ac != null)
            {
                _clickClip = ac;
            }
        }
        if (_clickClip == null)
        {
            _clickClip = SynthesiseClick();
        }

        if (_hoverClip == null)
        {
            _hoverClip = Resources.Load<AudioClip>("Audio/UIClickMenuSound");
        }
    }

    private static bool _legacyAssetWarned;

    /// <summary>
    /// Detect the obsolete ClickButtonSound.mov asset that the old
    /// VideoPlayer-based system relied on. Loaded via Resources to avoid
    /// referencing UnityEditor at runtime.
    /// </summary>
    private static void WarnIfLegacyClickAssetPresent()
    {
        if (_legacyAssetWarned) return;
        Object legacy = Resources.Load(ResourcePath);
        if (legacy == null) return;
        // The new path expects an AudioClip; anything else here is legacy.
        if (legacy is AudioClip) return;
        _legacyAssetWarned = true;
        Debug.LogWarning(
            "[UIClickAudio] Legacy UI click asset detected at Resources/Audio/ClickButtonSound " +
            "(type: " + legacy.GetType().Name + "). The VideoPlayer-based playback path has been removed. " +
            "Delete Assets/MohamedAman/Resources/Audio/ClickButtonSound.mov to silence this warning. " +
            "UI clicks are now driven by PlayerSfx.uiClickOverride → UIClickAudio.SetClickClip.");
    }

    /// <summary>
    /// Produces a short, percussive click — a quick noise burst layered with
    /// a high-frequency sine that decays exponentially. ~70 ms long, mono.
    /// </summary>
    private static AudioClip SynthesiseClick()
    {
        const int   sampleRate = 44100;
        const float duration   = 0.072f;
        int samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data = new float[samples];

        // Use Random.Range with a fixed-ish seed so the click sounds the same
        // every boot — easier on the player's ear when spamming buttons.
        Random.State previous = Random.state;
        Random.InitState(0xC11C);

        for (int i = 0; i < samples; i++)
        {
            float t        = i / (float)sampleRate;
            float envelope = Mathf.Exp(-t * 65f);                  // fast decay
            float noise    = Random.Range(-1f, 1f) * 0.55f;
            float tone     = Mathf.Sin(2f * Mathf.PI * 1500f * t) * 0.75f;
            data[i]        = (noise + tone) * envelope;
        }

        Random.state = previous;

        AudioClip clip = AudioClip.Create("ProceduralUIClick", samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

public class UIHoverClickAudioHelper : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.ISelectHandler
{
    private Button _button;

    public void Init(Button button)
    {
        _button = button;
    }

    public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (_button != null && _button.interactable)
        {
            if (UIClickAudio.Instance != null)
                UIClickAudio.Instance.PlayHover();
        }
    }

    public void OnSelect(UnityEngine.EventSystems.BaseEventData eventData)
    {
        if (_button != null && _button.interactable)
        {
            if (UIClickAudio.Instance != null)
                UIClickAudio.Instance.PlayHover();
        }
    }
}
