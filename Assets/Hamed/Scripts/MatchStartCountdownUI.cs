using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MatchStartCountdownUI : MonoBehaviour
{
    private const float StepHoldSeconds = 0.82f;
    private const float GoHoldSeconds   = 0.72f;
    private const string CountdownSfxResourcePath = "Audio/3 2 1 go sound effect";
    private static AudioClip _countdownSfxClip;
    private static AudioSource _activeCountdownSfxSource;

    public static IEnumerator Play()
    {
        HealthManager.BeginStartupProtection();
        LevelManager.RunFrameZeroRuntimeSync();
        GameObject host = new GameObject("MatchStartCountdown");
        DontDestroyOnLoad(host);
        var runner = host.AddComponent<MatchStartCountdownUI>();
        yield return runner.RunSequence();
        Destroy(host);
    }

    private Canvas _canvas;
    private TextMeshProUGUI _bigLabel;

    private IEnumerator RunSequence()
    {
        float prevScale = Time.timeScale;
        bool pausedForCountdown = !MultiplayerMode.IsMultiplayer;
        if (pausedForCountdown)
            Time.timeScale = 0f;

        BuildOverlay();

        string[] steps = { "3", "2", "1", "GO!" };
        for (int i = 0; i < steps.Length; i++)
        {
            bool isGo = steps[i] == "GO!";
            if (isGo)
                HealthManager.ReleaseStartupProtection();
            SetBigText(steps[i], isGo);
            if (i == 0)
                PlayCountdownSfx();

            yield return new WaitForSecondsRealtime(isGo ? GoHoldSeconds : StepHoldSeconds);
        }

        if (pausedForCountdown)
            Time.timeScale = Mathf.Approximately(prevScale, 0f) ? 1f : prevScale;
    }

    private void SetBigText(string t, bool isGo)
    {
        if (_bigLabel == null) return;
        _bigLabel.text = t;
        PrismaticHudTypography.ApplyCountdownStyle(_bigLabel, isGo);
    }

    private void BuildOverlay()
    {
        GameObject root = new GameObject("CountdownCanvas");
        root.transform.SetParent(transform, false);

        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 9998;
        root.AddComponent<GraphicRaycaster>();

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject dim = new GameObject("Dim");
        dim.transform.SetParent(root.transform, false);
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.35f);
        RectTransform dimRt = dimImg.rectTransform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = dimRt.offsetMax = Vector2.zero;

        GameObject labelGo = new GameObject("Count");
        labelGo.transform.SetParent(root.transform, false);
        _bigLabel = labelGo.AddComponent<TextMeshProUGUI>();
        _bigLabel.text = string.Empty;
        _bigLabel.fontSize = 220f;
        _bigLabel.alignment = TextAlignmentOptions.Center;
        PrismaticHudTypography.ApplyCountdownStyle(_bigLabel, false);

        RectTransform lr = _bigLabel.rectTransform;
        lr.anchorMin = new Vector2(0.5f, 0.5f);
        lr.anchorMax = new Vector2(0.5f, 0.5f);
        lr.pivot = new Vector2(0.5f, 0.5f);
        lr.sizeDelta = new Vector2(800f, 400f);
        lr.anchoredPosition = Vector2.zero;
    }

    private void PlayCountdownSfx()
    {
        AudioClip clip = ResolveCountdownSfxClip();
        if (clip == null)
            return;

        if (_activeCountdownSfxSource != null && _activeCountdownSfxSource.isPlaying)
            _activeCountdownSfxSource.Stop();

        AudioSource source = GameManager.Instance != null ? GameManager.Instance.MatchUiAudio : null;
        if (source == null)
            source = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();

        ConfigureCountdownSource(source);
        source.clip = clip;
        source.Stop();
        source.Play();
        _activeCountdownSfxSource = source;
    }

    private static AudioClip ResolveCountdownSfxClip()
    {
        if (_countdownSfxClip != null)
            return _countdownSfxClip;

        _countdownSfxClip = Resources.Load<AudioClip>(CountdownSfxResourcePath);
        return _countdownSfxClip;
    }

    private static void ConfigureCountdownSource(AudioSource source)
    {
        if (source == null)
            return;

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        if (SessionManager.Instance != null)
            SessionManager.Instance.ConfigureMatchAudioSource(source, SessionManager.Instance.MatchUiMixerGroupName);
    }
}
