using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class SciFiSlidingDoor : MonoBehaviour
{
    public Transform leftPanel;
    public Transform rightPanel;
    public float panelSlide = 1.9f;
    public float slideDuration = 0.55f;

    public bool interactiveToggle = true;
    public string playerTag = "Player";
    public float interactRange = 3.5f;

    private const string PromptMessage = "[E] TO INTERACT";

    private Vector3 _leftClosedLocal;
    private Vector3 _rightClosedLocal;
    private Vector3 _leftOpenLocal;
    private Vector3 _rightOpenLocal;
    private bool _isOpen;
    private bool _isTransitioning;
    private bool _playerInRange;
    private Transform _player;

    private static GameObject s_promptRoot;
    private static CanvasGroup s_promptGroup;
    private static TMP_Text s_promptLabel;
    private static SciFiSlidingDoor s_activeOwner;

    private void Awake()
    {
        EnsurePanels();
        CapturePoses();
        StripLegacyPrompt();
        EnsureTrigger();
    }

    private void OnDisable()
    {
        if (s_activeOwner == this) HidePrompt();
    }

    private void EnsurePanels()
    {
        if (leftPanel == null)
        {
            Transform t = transform.Find("LeftPanel");
            if (t != null) leftPanel = t;
        }
        if (rightPanel == null)
        {
            Transform t = transform.Find("RightPanel");
            if (t != null) rightPanel = t;
        }
    }

    private void StripLegacyPrompt()
    {
        Transform legacy = transform.Find("DoorPromptCanvas");
        if (legacy != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(legacy.gameObject);
            else Destroy(legacy.gameObject);
#else
            Destroy(legacy.gameObject);
#endif
        }
    }

    private void EnsureTrigger()
    {
        SphereCollider sc = GetComponent<SphereCollider>();
        if (sc == null) sc = gameObject.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = Mathf.Max(sc.radius, interactRange);
    }

    private void CapturePoses()
    {
        if (leftPanel != null)
        {
            _leftClosedLocal = leftPanel.localPosition;
            float dir = _leftClosedLocal.z < 0f ? -1f : (_leftClosedLocal.z > 0f ? 1f : -1f);
            _leftOpenLocal = _leftClosedLocal + Vector3.forward * (panelSlide * dir);
        }
        if (rightPanel != null)
        {
            _rightClosedLocal = rightPanel.localPosition;
            float dir = _rightClosedLocal.z > 0f ? 1f : (_rightClosedLocal.z < 0f ? -1f : 1f);
            _rightOpenLocal = _rightClosedLocal + Vector3.forward * (panelSlide * dir);
        }
    }

    // Door behaviour: AUTO-OPEN on any character (player OR enemy) entering the
    // trigger, AUTO-CLOSE when none are inside. No "[E] TO INTERACT" prompt — the
    // door is purely proximity-driven now.
    private int _occupants;

    private static bool IsCharacterCollider(Collider other)
    {
        if (other == null) return false;
        for (Transform t = other.transform; t != null; t = t.parent)
        {
            if (t.CompareTag("Player")) return true;
            if (t.CompareTag("Enemy"))  return true;
            if (t.GetComponent<EnemyController>() != null) return true;
            if (t.GetComponent<PlayerController>() != null) return true;
        }
        return false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsCharacterCollider(other)) return;
        _occupants++;
        if (s_activeOwner == this) HidePrompt();
        if (!_isOpen) Open();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsCharacterCollider(other)) return;
        _occupants = Mathf.Max(0, _occupants - 1);
        if (_occupants == 0 && _isOpen && !_isTransitioning)
            Close();
    }

    private void Update()
    {
        // Prompt-driven E-toggle removed. The door is now fully automatic; the
        // E key no longer affects this door.
    }

    private static bool WasInteractPressedThisFrame()
    {
        return Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;
    }

    public void Toggle()
    {
        if (_isTransitioning) return;
        if (_isOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (_isOpen || _isTransitioning) return;
        EnsurePanels();
        StartCoroutine(SlideRoutine(true));
    }

    public void Close()
    {
        if (!_isOpen || _isTransitioning) return;
        EnsurePanels();
        StartCoroutine(SlideRoutine(false));
    }

    private IEnumerator SlideRoutine(bool targetOpen)
    {
        _isTransitioning = true;
        if (s_activeOwner == this) HidePrompt();
        Vector3 leftFrom = leftPanel != null ? leftPanel.localPosition : Vector3.zero;
        Vector3 rightFrom = rightPanel != null ? rightPanel.localPosition : Vector3.zero;
        Vector3 leftTo = targetOpen ? _leftOpenLocal : _leftClosedLocal;
        Vector3 rightTo = targetOpen ? _rightOpenLocal : _rightClosedLocal;
        float dur = Mathf.Max(0.05f, slideDuration);
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float n = Mathf.Clamp01(t / dur);
            float e = n * n * (3f - 2f * n);
            if (leftPanel != null) leftPanel.localPosition = Vector3.Lerp(leftFrom, leftTo, e);
            if (rightPanel != null) rightPanel.localPosition = Vector3.Lerp(rightFrom, rightTo, e);
            yield return null;
        }
        if (leftPanel != null) leftPanel.localPosition = leftTo;
        if (rightPanel != null) rightPanel.localPosition = rightTo;
        _isOpen = targetOpen;
        _isTransitioning = false;
        if (_playerInRange) ShowPrompt();
    }

    private void ShowPrompt()
    {
        EnsurePromptOverlay();
        s_activeOwner = this;
        if (s_promptLabel != null) s_promptLabel.text = PromptMessage;
        if (s_promptRoot != null && !s_promptRoot.activeSelf) s_promptRoot.SetActive(true);
        if (s_promptGroup != null) s_promptGroup.alpha = 1f;
    }

    private void HidePrompt()
    {
        if (s_promptRoot != null) s_promptRoot.SetActive(false);
        if (s_promptGroup != null) s_promptGroup.alpha = 0f;
        s_activeOwner = null;
    }

    private static void EnsurePromptOverlay()
    {
        if (s_promptRoot != null) return;

        GameObject canvasGo = new GameObject("DoorPromptOverlay");
        DontDestroyOnLoad(canvasGo);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        GameObject panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        RectTransform panelRt = panelGo.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.5f, 0f);
        panelRt.anchorMax = new Vector2(0.5f, 0f);
        panelRt.pivot = new Vector2(0.5f, 0f);
        panelRt.anchoredPosition = new Vector2(0f, 140f);
        panelRt.sizeDelta = new Vector2(420f, 84f);

        GameObject shadowGo = new GameObject("Shadow");
        shadowGo.transform.SetParent(panelGo.transform, false);
        RectTransform shadowRt = shadowGo.AddComponent<RectTransform>();
        shadowRt.anchorMin = Vector2.zero;
        shadowRt.anchorMax = Vector2.one;
        shadowRt.offsetMin = new Vector2(-6f, -10f);
        shadowRt.offsetMax = new Vector2(6f, -2f);
        Image shadowImg = shadowGo.AddComponent<Image>();
        shadowImg.color = new Color(0f, 0f, 0f, 0.55f);
        shadowImg.raycastTarget = false;

        GameObject bgGo = new GameObject("Background");
        bgGo.transform.SetParent(panelGo.transform, false);
        RectTransform bgRt = bgGo.AddComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        Image bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.04f, 0.06f, 0.09f, 0.88f);
        bgImg.raycastTarget = false;

        GameObject stripeGo = new GameObject("Accent");
        stripeGo.transform.SetParent(panelGo.transform, false);
        RectTransform stripeRt = stripeGo.AddComponent<RectTransform>();
        stripeRt.anchorMin = new Vector2(0f, 0f);
        stripeRt.anchorMax = new Vector2(1f, 0f);
        stripeRt.pivot = new Vector2(0.5f, 0f);
        stripeRt.sizeDelta = new Vector2(0f, 5f);
        stripeRt.anchoredPosition = Vector2.zero;
        Image stripeImg = stripeGo.AddComponent<Image>();
        stripeImg.color = new Color(1f, 0.78f, 0.05f, 1f);
        stripeImg.raycastTarget = false;

        GameObject textGo = new GameObject("Label");
        textGo.transform.SetParent(panelGo.transform, false);
        RectTransform textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(18f, 8f);
        textRt.offsetMax = new Vector2(-18f, -8f);
        TextMeshProUGUI label = textGo.AddComponent<TextMeshProUGUI>();
        label.text = PromptMessage;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 36f;
        label.fontStyle = FontStyles.Bold;
        label.color = new Color(1f, 0.95f, 0.78f, 1f);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;

        CanvasGroup cg = panelGo.AddComponent<CanvasGroup>();
        cg.interactable = false;
        cg.blocksRaycasts = false;
        cg.alpha = 1f;

        s_promptRoot = canvasGo;
        s_promptGroup = cg;
        s_promptLabel = label;
        s_promptRoot.SetActive(false);
    }
}
