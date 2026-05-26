using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lightweight “battle royale” win energy for the level-complete / campaign-clear
/// menus: title punch-in, looping confetti shards, and a slow banner glow — no
/// external tween packages required.
/// </summary>
public class WinScreenCelebration : MonoBehaviour
{
    private const string VictorySoundResourcePath = "Audio/Victory_Sound";
    private const float DuplicateSoundGuardSeconds = 1.0f;

    [SerializeField] private RectTransform titleRect;
    [SerializeField] private TextMeshProUGUI bannerTmp;
    [SerializeField] private int confettiCount = 42;
    [SerializeField] private AudioClip victorySoundClip;

    private readonly List<RectTransform> _confetti = new List<RectTransform>();
    private static AudioClip _cachedVictorySoundClip;
    private static float _lastVictorySoundRealtime = -999f;
    private Vector3 _titleBaseScale = Vector3.one;
    private float _time;
    private bool _titleIntroDone;
    private bool _victorySoundPlayed;

    public void Configure(RectTransform title, TextMeshProUGUI banner, Transform canvasRoot)
    {
        titleRect  = title;
        bannerTmp  = banner;
        _titleIntroDone = false;
        _titleBaseScale = title != null ? title.localScale : Vector3.one;
        BuildConfetti(canvasRoot);
        PlayVictorySoundOnce();
        StartCoroutine(PunchTitleIn());
    }

    private void PlayVictorySoundOnce()
    {
        if (!Application.isPlaying || _victorySoundPlayed)
            return;

        float now = Time.realtimeSinceStartup;
        if (now - _lastVictorySoundRealtime < DuplicateSoundGuardSeconds)
            return;

        AudioClip clip = ResolveVictorySoundClip();
        if (clip == null)
            return;

        AudioSource source = ResolveVictoryAudioSource();
        if (source == null)
            return;

        _victorySoundPlayed = true;
        _lastVictorySoundRealtime = now;
        source.PlayOneShot(clip, AudioSettingsRuntime.ScaledSfx(1f));
    }

    private AudioClip ResolveVictorySoundClip()
    {
        if (victorySoundClip != null)
            return victorySoundClip;

        if (_cachedVictorySoundClip == null)
            _cachedVictorySoundClip = Resources.Load<AudioClip>(VictorySoundResourcePath);

        return _cachedVictorySoundClip;
    }

    private AudioSource ResolveVictoryAudioSource()
    {
        AudioSource source = GameManager.Instance != null ? GameManager.Instance.MatchUiAudio : null;
        if (source == null)
            source = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        if (SessionManager.Instance != null)
            SessionManager.Instance.ConfigureMatchAudioSource(source, SessionManager.Instance.MatchUiMixerGroupName);

        return source;
    }

    private void BuildConfetti(Transform canvasRoot)
    {
        if (canvasRoot == null) return;

        GameObject layer = new GameObject("ConfettiLayer");
        layer.transform.SetParent(canvasRoot, false);
        RectTransform layerRT = layer.AddComponent<RectTransform>();
        StretchFull(layerRT);
        int idx = Mathf.Clamp(2, 0, Mathf.Max(0, canvasRoot.childCount - 1));
        layer.transform.SetSiblingIndex(idx);

        CanvasGroup cg = layer.AddComponent<CanvasGroup>();
        cg.blocksRaycasts = false;
        cg.interactable   = false;

        Color[] palette =
        {
            new Color(1f, 0.88f, 0.2f, 0.92f),
            new Color(1f, 0.35f, 0.85f, 0.88f),
            new Color(0.35f, 0.95f, 1f, 0.88f),
            new Color(0.55f, 1f, 0.45f, 0.85f),
            Color.white
        };

        for (int i = 0; i < confettiCount; i++)
        {
            GameObject bit = new GameObject("Confetti");
            bit.transform.SetParent(layer.transform, false);
            Image img = bit.AddComponent<Image>();
            img.color = palette[Random.Range(0, palette.Length)];
            RectTransform rt = bit.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(Random.Range(6f, 14f), Random.Range(10f, 22f));
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(Random.Range(-920f, 920f), Random.Range(80f, 420f));
            rt.localEulerAngles = new Vector3(0f, 0f, Random.Range(0f, 360f));
            _confetti.Add(rt);
        }
    }

    private static void StretchFull(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = r.offsetMax = Vector2.zero;
    }

    private IEnumerator PunchTitleIn()
    {
        if (titleRect == null) yield break;

        float dur = 0.55f;
        float t   = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / dur);
            float k = OutBack(u);
            titleRect.localScale = _titleBaseScale * Mathf.Lerp(0.15f, 1f, k);
            yield return null;
        }

        titleRect.localScale = _titleBaseScale;
        _titleIntroDone = true;
    }

    private void Update()
    {
        _time += Time.unscaledDeltaTime;

        if (titleRect != null && _titleIntroDone)
        {
            float breathe = 1f + Mathf.Sin(_time * 2.1f) * 0.035f;
            titleRect.localScale = _titleBaseScale * breathe;
        }

        if (bannerTmp != null)
        {
            float g = 0.75f + Mathf.Sin(_time * 3.3f) * 0.25f;
            Color c = bannerTmp.color;
            c.a = Mathf.Clamp01(g);
            bannerTmp.color = c;
        }

        float h = Screen.height + 200f;
        for (int i = 0; i < _confetti.Count; i++)
        {
            RectTransform rt = _confetti[i];
            if (rt == null) continue;

            Vector2 p = rt.anchoredPosition;
            p.y -= (180f + (i % 5) * 22f) * Time.unscaledDeltaTime;
            p.x += Mathf.Sin(_time * 2f + i * 0.37f) * 90f * Time.unscaledDeltaTime;
            if (p.y < -h * 0.5f)
            {
                p.y = Random.Range(420f, 720f);
                p.x = Random.Range(-920f, 920f);
            }

            rt.anchoredPosition = p;
            rt.Rotate(0f, 0f, (35f + (i & 3) * 20f) * Time.unscaledDeltaTime);
        }
    }

    private static float OutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
    }
}
