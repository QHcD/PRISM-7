using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class PauseMenuController : MonoBehaviour
{
    private static readonly bool VerbosePauseDiagnostics = false;

    private GameObject pauseCanvas;
    private GameObject mainPanel;
    private GameObject settingsPanel;
    private bool isPaused;
    private TMP_FontAsset prismFont;

    // The MultiplayerGameScene already has a PauseMenuController on a scene
    // object, but builds without it (or scenes loaded via PhotonNetwork.LoadLevel
    // before the scene object wakes) used to ship without a working ESC. Make
    // sure exactly one controller exists in every gameplay scene.
    // Earliest possible hook — runs before any scene Awake. If this line is
    // not in the console, the entire script failed to compile.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StaticBootstrapLog()
    {
        LogPauseDiag("[MPPauseDiag] static bootstrap loaded");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureExistsInGameplayScene()
    {
        SceneManager.sceneLoaded -= OnGameplaySceneLoaded;
        SceneManager.sceneLoaded += OnGameplaySceneLoaded;
        EnsureControllerForScene(SceneManager.GetActiveScene());
    }

    private static void OnGameplaySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureControllerForScene(scene);
    }

    private static void EnsureControllerForScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        if (scene.name == MultiplayerMode.MultiplayerSceneName)
            LogPauseDiag("[MPPauseDiag] sceneLoaded MultiplayerGameScene");

        if (scene.name != MultiplayerMode.SinglePlayerSceneName &&
            scene.name != MultiplayerMode.MultiplayerSceneName)
            return;

        PauseMenuController[] all = FindObjectsByType<PauseMenuController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        PauseMenuController kept = null;
        for (int i = 0; i < all.Length; i++)
        {
            PauseMenuController candidate = all[i];
            if (candidate == null)
                continue;

            if (kept == null)
            {
                kept = candidate;
                continue;
            }

            if (!kept.isActiveAndEnabled && candidate.isActiveAndEnabled)
            {
                Destroy(kept.gameObject);
                kept = candidate;
            }
            else
            {
                Destroy(candidate.gameObject);
            }
        }

        if (kept == null)
        {
            GameObject go = new GameObject("PauseMenuController_Runtime");
            kept = go.AddComponent<PauseMenuController>();
            LogPauseDiag("[MPPauseDiag] forced runtime controller created");
        }

        if (!kept.gameObject.activeSelf)
            kept.gameObject.SetActive(true);
        if (!kept.enabled)
            kept.enabled = true;

        string backend = DetectInputBackend();
        LogPauseDiag("[MPPauseDiag] PauseMenuController exists = true");
        LogPauseDiag("[MPPauseDiag] enabled = " + kept.enabled);
        LogPauseDiag("[MPPauseDiag] GameObject active = " + kept.gameObject.activeInHierarchy);
        LogPauseDiag("[MPPauseDiag] current scene = " + scene.name);
        LogPauseDiag("[MPPauseDiag] input backend detected = " + backend);
    }

    private static string DetectInputBackend()
    {
        return "New";
    }

    private static void LogPauseDiag(string message)
    {
        if (VerbosePauseDiagnostics)
            Debug.Log(message);
    }

    private readonly string[] graphicsLabels = { "LOW", "MEDIUM", "HIGH" };

    private Slider masterVolumeSlider;
    private Slider musicVolumeSlider;
    private Slider sfxVolumeSlider;
    private Slider mouseSensitivitySlider;
    private ScrollRect pauseSettingsScrollRect;

    private void Awake()
    {
        LogPauseDiag("[MPPauseDiag] Awake");
        try { SettingsManager.ApplyDisplayPreferences(); } catch (System.Exception e) { Debug.LogWarning("[MPPauseDiag] Awake settings err: " + e.Message); }
        try { AudioSettingsRuntime.ApplyListenerVolume(); } catch (System.Exception e) { Debug.LogWarning("[MPPauseDiag] Awake audio err: " + e.Message); }
    }

    private float _nextEscAllowedTime;
    private bool _firstKeyLogged;

    private void OnEnable()
    {
        if (!Application.isPlaying)
            return;

        InputSystem.onAfterUpdate -= HandleEscapeAfterInput;
        InputSystem.onAfterUpdate += HandleEscapeAfterInput;
    }

    private void OnDisable()
    {
        InputSystem.onAfterUpdate -= HandleEscapeAfterInput;

        if (!MultiplayerMode.IsMultiplayer)
            Time.timeScale = 1f;
        isPaused = false;
    }

    private void Update()
    {
        bool isGameplayScene = SceneManager.GetActiveScene().name == MultiplayerMode.SinglePlayerSceneName ||
                               SceneManager.GetActiveScene().name == MultiplayerMode.MultiplayerSceneName;
        if (!isGameplayScene)
        {
            if (isPaused)
                ResumeGame();
            return;
        }

        // Periodic "controller alive" log removed — it produced unnecessary
        // background spam during gameplay. The first-frame ping above is enough.
    }

    private void HandleEscapeAfterInput()
    {
        if (!Application.isPlaying || !isActiveAndEnabled)
            return;

        if (MultiplayerMode.IsMultiplayer)
            return;

        bool isGameplayScene = SceneManager.GetActiveScene().name == MultiplayerMode.SinglePlayerSceneName ||
                               SceneManager.GetActiveScene().name == MultiplayerMode.MultiplayerSceneName;
        if (!isGameplayScene)
            return;

        if (isPaused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        HUDManager hudManager = HUDManager.Instance;
        if (!MultiplayerMode.IsMultiplayer && hudManager != null && hudManager.IsMatchFinished)
            return;

        if (!_firstKeyLogged)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame)
            {
                _firstKeyLogged = true;
                LogPauseDiag("[MPPauseDiag] first key detected (InputSystem Keyboard.current)");
            }
        }

        if (Time.unscaledTime < _nextEscAllowedTime)
            return;

        if (!WasEscapePressedThisFrame())
            return;

        _nextEscAllowedTime = Time.unscaledTime + 0.2f;

        if (MultiplayerMode.IsMultiplayer && hudManager != null && hudManager.CloseFullMapFromEscape())
            return;

        if (isPaused)
        {
            ResumeGame();
            LogPauseDiag("[MPPauseDiag] normal pause menu visible = false");
        }
        else
        {
            LogPauseDiag("[MPPauseDiag] calling normal ShowPauseMenu");
            ShowPauseMenu();
            LogPauseDiag("[MPPauseDiag] normal pause menu visible = " + (pauseCanvas != null));
        }
    }

    private static bool WasEscapePressedThisFrame()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            LogPauseDiag("[MPPauseDiag] ESC detected by InputSystem");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Public toggle so external systems (HUDManager) can drive the SAME
    /// pause menu used by single-player ESC. Returns true if the menu is now
    /// open, false if it was closed.
    /// </summary>
    public bool TogglePauseExternal()
    {
        if (isPaused) { ResumeGame(); return false; }
        ShowPauseMenu();
        return true;
    }

    public bool IsPauseMenuOpen => isPaused;

    private void ShowPauseMenu()
    {
        LogPauseDiag("[MPPause] using normal pause menu");
        EnsureEventSystem();
        BuildPauseMenu();
        ShowPanel(mainPanel);

        // Clear any stale UI selection so the first stray Enter/Space press
        // never auto-fires the "currently selected" button (used to be Restart).
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        // Multiplayer keeps Time.timeScale = 1 — pausing time would freeze
        // Photon serialization, RPCs, and remote players. SP behaviour is
        // unchanged.
        if (!MultiplayerMode.IsMultiplayer)
            Time.timeScale = 0f;
        isPaused = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        LogPauseDiag("[MPPause] normal menu opened");
    }

    private void LateUpdate()
    {
        if (!isPaused)
            return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void ResumeGame()
    {
        if (!MultiplayerMode.IsMultiplayer)
            Time.timeScale = 1f;
        isPaused = false;
        LogPauseDiag("[MPPause] normal menu closed");

        if (pauseCanvas != null)
        {
            Destroy(pauseCanvas);
            pauseCanvas = null;
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void RestartGame()
    {
        Time.timeScale = 1f;
        isPaused = false;
        GameManager.Instance?.ReplayCurrentLevel();
    }

    private void QuitGame()
    {
        Time.timeScale = 1f;
        isPaused = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // In MP, the player is in a Photon room — leave it cleanly before
        // returning to the main menu. Wrapped in try so any Photon error
        // never blocks the menu transition.
        if (MultiplayerMode.IsMultiplayer)
        {
            LogPauseDiag("[MPPause] leaving Photon room then main menu");
            // Latch the guard BEFORE LeaveRoom so any late property-write call
            // (SetReadyState, MpRoomConfig, MpMatchController timer ticks)
            // that fires during the unload window early-outs cleanly.
            MultiplayerShutdownGuard.BeginLeave();
#if PUN_2_OR_NEWER
            try
            {
                if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsConnectedAndReady)
                    Photon.Pun.PhotonNetwork.LeaveRoom();
            }
            catch { /* never block menu return */ }
#endif
            MultiplayerMode.SetSinglePlayer();
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
            LogPauseDiag("[MPLeave] loaded MainMenu");
            return;
        }

        GameManager.Instance?.GoToMainMenu();
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
            return;

        GameObject eventSystemObject = new GameObject("EventSystem");
        eventSystemObject.AddComponent<EventSystem>();
        eventSystemObject.AddComponent<InputSystemUIInputModule>();
    }

    private void BuildPauseMenu()
    {
        if (pauseCanvas != null)
            Destroy(pauseCanvas);

        prismFont = ResolvePrismFont();

        pauseCanvas = new GameObject("PauseCanvas");
        Canvas canvas = pauseCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;

        CanvasScaler scaler = pauseCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        pauseCanvas.AddComponent<GraphicRaycaster>();

        Image overlay = new GameObject("PauseOverlay").AddComponent<Image>();
        overlay.transform.SetParent(pauseCanvas.transform, false);
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.02f, 0.02f, 0.06f, 0.72f);
        overlay.raycastTarget = false;

        mainPanel = CreatePausePanel("PausePanel_Main", new Vector2(760f, 700f));
        settingsPanel = CreatePausePanel("PausePanel_Settings", new Vector2(900f, 920f));

        BuildMainPanel(mainPanel.transform);
        BuildSettingsPanel(settingsPanel.transform);
    }

    private GameObject CreatePausePanel(string name, Vector2 size)
    {
        Image panel = new GameObject(name).AddComponent<Image>();
        panel.transform.SetParent(pauseCanvas.transform, false);
        panel.color = new Color(0.16f, 0.20f, 0.30f, 0.36f);
        // CRITICAL: panel background must not receive clicks. Without this, the
        // EventSystem treated the blue panel as a clickable surface; combined
        // with last-selected-button defaulting to RESTART, ambient mouse clicks
        // on the empty panel area fired RestartGame(). raycastTarget=false
        // means only the actual Resume/Restart/Settings/Quit buttons receive
        // clicks now.
        panel.raycastTarget = false;

        RectTransform panelRect = panel.rectTransform;
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = size;
        panelRect.anchoredPosition = new Vector2(0f, -10f);

        Outline panelOutline = panel.gameObject.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.26f, 0.42f, 0.68f, 0.22f);
        panelOutline.effectDistance = new Vector2(2f, -2f);
        return panel.gameObject;
    }

    private void BuildMainPanel(Transform parent)
    {
        CreateLabel(parent, "PAUSED", 62f, new Color(0.78f, 0.84f, 1f, 1f), new Vector2(0f, 250f), new Vector2(420f, 80f), true);
        CreateLabel(parent, "TAKE A BREATH. JUMP BACK IN WHEN YOU'RE READY.", 22f, new Color(0.72f, 0.84f, 1f, 0.88f), new Vector2(0f, 198f), new Vector2(620f, 36f), false);

        CreateButton(parent, "RESUME", new Vector2(0f, 92f), ResumeGame);
        CreateButton(parent, "RESTART", new Vector2(0f, 2f), RestartGame);
        CreateButton(parent, "SETTINGS", new Vector2(0f, -88f), () => ShowPanel(settingsPanel));
        CreateButton(parent, "QUIT", new Vector2(0f, -178f), QuitGame);
    }

    private void BuildSettingsPanel(Transform parent)
    {
        // Slightly tighter header so the content reads more centered.
        CreateLabel(parent, "SETTINGS", 58f, new Color(0.78f, 0.84f, 1f, 1f), new Vector2(0f, 392f), new Vector2(500f, 76f), true);
        CreateLabel(parent, "AUDIO, VIDEO, CONTROLS AND GAMEPLAY IN ONE PLACE.", 20f, new Color(0.72f, 0.84f, 1f, 0.88f), new Vector2(0f, 346f), new Vector2(720f, 34f), false);

        RectTransform content = CreatePauseSettingsScrollArea(parent);

        CreateSectionLabel(content, "AUDIO");

        masterVolumeSlider = CreateSliderRow(content, "MASTER VOL", Vector2.zero, AudioSettingsRuntime.MasterKey, value =>
        {
            AudioSettingsRuntime.ApplyListenerVolume();
            AudioSettingsRuntime.RefreshMenuLobbyMusicIfPresent();
        });

        musicVolumeSlider = CreateSliderRow(content, "MUSIC VOL", Vector2.zero, AudioSettingsRuntime.MusicKey,
            _ => { AudioSettingsRuntime.RefreshMenuLobbyMusicIfPresent(); });
        sfxVolumeSlider = CreateSliderRow(content, "SFX VOL", Vector2.zero, AudioSettingsRuntime.SfxKey, null);

        CreateSectionLabel(content, "VIDEO");

        CreateCycleRow(content, "GRAPHICS", Vector2.zero, GetGraphicsLabel, CycleGraphics);
        CreateCycleRow(content, "FULLSCREEN", Vector2.zero, GetFullscreenLabel, ToggleFullscreen);

        CreateSectionLabel(content, "CONTROLS");

        CreateCycleRow(content, "MOVE STYLE", Vector2.zero, GetMovementLabel, CycleMovement);
        mouseSensitivitySlider = CreateSensitivityRow(content, "MOUSE SENS", Vector2.zero);

        CreateSectionLabel(content, "GAMEPLAY");

        CreateCycleRow(content, "DIFFICULTY", Vector2.zero, GetDifficultyLabel, CycleDifficulty);
        CreateCycleRow(content, "CAMERA VIEW", Vector2.zero, GetPerspectiveLabel, CyclePerspective);

        CreateButton(parent, "RETURN", new Vector2(0f, -390f), () => ShowPanel(mainPanel));
    }

    private RectTransform CreatePauseSettingsScrollArea(Transform parent)
    {
        GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer));
        viewportObj.transform.SetParent(parent, false);
        Image viewportImage = viewportObj.AddComponent<Image>();
        viewportImage.color = new Color(0.02f, 0.04f, 0.10f, 0.18f);
        viewportObj.AddComponent<RectMask2D>();
        pauseSettingsScrollRect = viewportObj.AddComponent<ScrollRect>();

        RectTransform viewportRect = viewportObj.GetComponent<RectTransform>();
        viewportRect.anchorMin = new Vector2(0.5f, 0.5f);
        viewportRect.anchorMax = new Vector2(0.5f, 0.5f);
        viewportRect.pivot = new Vector2(0.5f, 0.5f);
        viewportRect.sizeDelta = new Vector2(780f, 620f);
        viewportRect.anchoredPosition = new Vector2(0f, 6f);

        GameObject contentObj = new GameObject("Content", typeof(RectTransform), typeof(CanvasRenderer));
        contentObj.transform.SetParent(viewportObj.transform, false);
        RectTransform contentRect = contentObj.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, 0f); // driven by ContentSizeFitter

        VerticalLayoutGroup layout = contentObj.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 10, 22);
        layout.spacing = 16f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = contentObj.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Scrollbar scrollbar = CreatePauseSettingsScrollbar(parent);

        pauseSettingsScrollRect.content = contentRect;
        pauseSettingsScrollRect.viewport = viewportRect;
        pauseSettingsScrollRect.vertical = true;
        pauseSettingsScrollRect.horizontal = false;
        pauseSettingsScrollRect.scrollSensitivity = 35f;
        pauseSettingsScrollRect.movementType = ScrollRect.MovementType.Clamped;
        pauseSettingsScrollRect.verticalScrollbar = scrollbar;
        pauseSettingsScrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

        return contentRect;
    }

    private Scrollbar CreatePauseSettingsScrollbar(Transform parent)
    {
        GameObject scrollbarObj = new GameObject("ScrollbarRight", typeof(RectTransform), typeof(CanvasRenderer));
        scrollbarObj.transform.SetParent(parent, false);
        Image track = scrollbarObj.AddComponent<Image>();
        track.color = new Color(0.07f, 0.12f, 0.24f, 0.72f);
        RectTransform trackRect = scrollbarObj.GetComponent<RectTransform>();
        trackRect.anchorMin = new Vector2(0.5f, 0.5f);
        trackRect.anchorMax = new Vector2(0.5f, 0.5f);
        trackRect.pivot = new Vector2(0.5f, 0.5f);
        trackRect.sizeDelta = new Vector2(18f, 620f);
        trackRect.anchoredPosition = new Vector2(408f, 6f);

        GameObject handleObj = new GameObject("Handle");
        handleObj.transform.SetParent(scrollbarObj.transform, false);
        Image handleImage = handleObj.AddComponent<Image>();
        handleImage.color = new Color(0.36f, 0.74f, 1f, 0.95f);
        RectTransform handleRect = handleObj.GetComponent<RectTransform>();
        Stretch(handleRect);

        Scrollbar scrollbar = scrollbarObj.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.targetGraphic = handleImage;
        scrollbar.handleRect = handleRect;
        scrollbar.size = 0.65f;
        return scrollbar;
    }

    private void CreateSectionLabel(Transform parent, string text)
    {
        TextMeshProUGUI label = new GameObject("Section_" + text).AddComponent<TextMeshProUGUI>();
        label.transform.SetParent(parent, false);
        label.text = text;
        label.fontSize = 26f;
        label.fontStyle = FontStyles.Bold;
        label.color = new Color(0.45f, 0.72f, 1f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        if (prismFont != null)
            label.font = prismFont;

        RectTransform rect = label.rectTransform;
        rect.sizeDelta = new Vector2(720f, 36f);

        LayoutElement layoutElement = label.gameObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 36f;
        layoutElement.minHeight = 32f;
    }

    private void CreateCycleRow(Transform parent, string label, Vector2 position, System.Func<string> getValue, UnityEngine.Events.UnityAction onCycle)
    {
        GameObject row = new GameObject("Row_" + label);
        row.transform.SetParent(parent, false);
        RectTransform rowRect = row.AddComponent<RectTransform>();
        rowRect.anchorMin = new Vector2(0.5f, 0.5f);
        rowRect.anchorMax = new Vector2(0.5f, 0.5f);
        rowRect.pivot = new Vector2(0.5f, 0.5f);
        rowRect.sizeDelta = new Vector2(720f, 66f);
        rowRect.anchoredPosition = position;
        LayoutElement rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.preferredHeight = 66f;
        rowLayout.minHeight = 62f;

        CreateLabel(row.transform, label, 28f, Color.white, new Vector2(-205f, 0f), new Vector2(330f, 44f), false, TextAlignmentOptions.Right);
        CreateButton(row.transform, getValue(), new Vector2(128f, 0f), onCycle, new Vector2(330f, 62f), getValue);
    }

    private Slider CreateSliderRow(
        Transform parent,
        string label,
        Vector2 position,
        string prefKey,
        UnityEngine.Events.UnityAction<float> onChanged)
    {
        GameObject row = new GameObject("Row_" + label);
        row.transform.SetParent(parent, false);
        RectTransform rowRect = row.AddComponent<RectTransform>();
        rowRect.anchorMin = new Vector2(0.5f, 0.5f);
        rowRect.anchorMax = new Vector2(0.5f, 0.5f);
        rowRect.pivot = new Vector2(0.5f, 0.5f);
        rowRect.sizeDelta = new Vector2(720f, 62f);
        rowRect.anchoredPosition = position;
        LayoutElement rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.preferredHeight = 62f;
        rowLayout.minHeight = 58f;

        CreateLabel(row.transform, label, 26f, Color.white, new Vector2(-205f, 0f), new Vector2(330f, 44f), false, TextAlignmentOptions.Right);

        GameObject sliderObj = new GameObject("Slider_" + label);
        sliderObj.transform.SetParent(row.transform, false);
        Image sliderBackground = sliderObj.AddComponent<Image>();
        sliderBackground.color = new Color(0.94f, 0.94f, 0.96f, 1f);
        RectTransform sliderRect = sliderObj.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, 0.5f);
        sliderRect.anchorMax = new Vector2(0.5f, 0.5f);
        sliderRect.pivot = new Vector2(0.5f, 0.5f);
        sliderRect.sizeDelta = new Vector2(330f, 20f);
        sliderRect.anchoredPosition = new Vector2(128f, 0f);

        Slider slider = sliderObj.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;

        GameObject fillArea = new GameObject("FillArea");
        fillArea.transform.SetParent(sliderObj.transform, false);
        RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
        fillAreaRect.anchorMin = Vector2.zero;
        fillAreaRect.anchorMax = Vector2.one;
        fillAreaRect.offsetMin = Vector2.zero;
        fillAreaRect.offsetMax = new Vector2(-18f, 0f);

        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(fillArea.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.58f, 0.32f, 0.94f, 1f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        GameObject handle = new GameObject("Handle");
        handle.transform.SetParent(sliderObj.transform, false);
        Image handleImage = handle.AddComponent<Image>();
        handleImage.color = Color.white;
        RectTransform handleRect = handle.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(18f, 28f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.targetGraphic = handleImage;
        slider.SetValueWithoutNotify(Mathf.Clamp01(PlayerPrefs.GetFloat(prefKey, 0.8f)));
        slider.onValueChanged.AddListener(value =>
        {
            PlayerPrefs.SetFloat(prefKey, value);
            PlayerPrefs.Save();
            onChanged?.Invoke(value);
        });

        TextMeshProUGUI valueLabel = new GameObject("Val_" + label).AddComponent<TextMeshProUGUI>();
        valueLabel.transform.SetParent(row.transform, false);
        valueLabel.fontSize = 22f;
        valueLabel.color = new Color(0.82f, 0.90f, 1f, 0.92f);
        valueLabel.alignment = TextAlignmentOptions.Left;
        if (prismFont != null)
            valueLabel.font = prismFont;
        RectTransform valueRect = valueLabel.rectTransform;
        valueRect.anchorMin = new Vector2(0.5f, 0.5f);
        valueRect.anchorMax = new Vector2(0.5f, 0.5f);
        valueRect.pivot = new Vector2(0f, 0.5f);
        valueRect.sizeDelta = new Vector2(90f, 30f);
        valueRect.anchoredPosition = new Vector2(306f, 0f);
        valueLabel.text = Mathf.RoundToInt(slider.value * 100f) + "%";
        slider.onValueChanged.AddListener(value =>
        {
            valueLabel.text = Mathf.RoundToInt(value * 100f) + "%";
        });

        return slider;
    }

    private Slider CreateSensitivityRow(Transform parent, string label, Vector2 position)
    {
        GameObject row = new GameObject("Row_" + label);
        row.transform.SetParent(parent, false);
        RectTransform rowRect = row.AddComponent<RectTransform>();
        rowRect.anchorMin = new Vector2(0.5f, 0.5f);
        rowRect.anchorMax = new Vector2(0.5f, 0.5f);
        rowRect.pivot = new Vector2(0.5f, 0.5f);
        rowRect.sizeDelta = new Vector2(720f, 62f);
        rowRect.anchoredPosition = position;
        LayoutElement rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.preferredHeight = 62f;
        rowLayout.minHeight = 58f;

        CreateLabel(row.transform, label, 26f, Color.white, new Vector2(-205f, 0f), new Vector2(330f, 44f), false, TextAlignmentOptions.Right);

        GameObject sliderObj = new GameObject("Slider_" + label);
        sliderObj.transform.SetParent(row.transform, false);
        Image sliderBackground = sliderObj.AddComponent<Image>();
        sliderBackground.color = new Color(0.94f, 0.94f, 0.96f, 1f);
        RectTransform sliderRect = sliderObj.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, 0.5f);
        sliderRect.anchorMax = new Vector2(0.5f, 0.5f);
        sliderRect.pivot = new Vector2(0.5f, 0.5f);
        sliderRect.sizeDelta = new Vector2(330f, 20f);
        sliderRect.anchoredPosition = new Vector2(128f, 0f);

        Slider slider = sliderObj.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = LookSensitivityRuntime.MinSlider;
        slider.maxValue = LookSensitivityRuntime.MaxSlider;
        slider.wholeNumbers = false;

        GameObject fillArea = new GameObject("FillArea");
        fillArea.transform.SetParent(sliderObj.transform, false);
        RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
        fillAreaRect.anchorMin = Vector2.zero;
        fillAreaRect.anchorMax = Vector2.one;
        fillAreaRect.offsetMin = Vector2.zero;
        fillAreaRect.offsetMax = new Vector2(-18f, 0f);

        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(fillArea.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.30f, 0.55f, 1f, 1f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        GameObject handle = new GameObject("Handle");
        handle.transform.SetParent(sliderObj.transform, false);
        Image handleImage = handle.AddComponent<Image>();
        handleImage.color = Color.white;
        RectTransform handleRect = handle.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(18f, 28f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.targetGraphic = handleImage;

        TextMeshProUGUI valueLabel = new GameObject("Val_" + label).AddComponent<TextMeshProUGUI>();
        valueLabel.transform.SetParent(row.transform, false);
        valueLabel.fontSize = 22f;
        valueLabel.color = new Color(0.82f, 0.90f, 1f, 0.92f);
        valueLabel.alignment = TextAlignmentOptions.Left;
        if (prismFont != null)
            valueLabel.font = prismFont;
        RectTransform valueRect = valueLabel.rectTransform;
        valueRect.anchorMin = new Vector2(0.5f, 0.5f);
        valueRect.anchorMax = new Vector2(0.5f, 0.5f);
        valueRect.pivot = new Vector2(0f, 0.5f);
        valueRect.sizeDelta = new Vector2(90f, 30f);
        valueRect.anchoredPosition = new Vector2(306f, 0f);

        LookSensitivityRuntime.LoadFromPrefs();
        slider.SetValueWithoutNotify(LookSensitivityRuntime.SliderValue);
        valueLabel.text = LookSensitivityRuntime.SliderValue.ToString("0.0");
        slider.onValueChanged.AddListener(value =>
        {
            LookSensitivityRuntime.SetSliderValue(value, persist: true);
            valueLabel.text = value.ToString("0.0");
        });

        return slider;
    }

    private void CreateButton(Transform parent, string text, Vector2 position, UnityEngine.Events.UnityAction action)
    {
        CreateButton(parent, text, position, action, new Vector2(300f, 72f), null);
    }

    private void CreateButton(Transform parent, string text, Vector2 position, UnityEngine.Events.UnityAction action, Vector2 size, System.Func<string> dynamicText)
    {
        Image buttonImage = new GameObject("Btn_" + text).AddComponent<Image>();
        buttonImage.transform.SetParent(parent, false);
        buttonImage.color = Color.white;

        RectTransform rect = buttonImage.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;

        Outline outline = buttonImage.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.20f, 0.24f, 0.38f, 0.30f);
        outline.effectDistance = new Vector2(2f, -2f);

        Button button = buttonImage.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        button.onClick.AddListener(action);
        // Disable EventSystem navigation/auto-select. Without this, the last
        // pressed button (often Settings → returned to main → Resume etc.)
        // remained the "selected" UI target, and a stray Enter/Space press
        // or pointer-click on the panel area was forwarded to it.
        Navigation nav = button.navigation;
        nav.mode = Navigation.Mode.None;
        button.navigation = nav;

        TextMeshProUGUI label = new GameObject("Label").AddComponent<TextMeshProUGUI>();
        label.transform.SetParent(buttonImage.transform, false);
        label.text = dynamicText != null ? dynamicText() : text;
        label.fontSize = 28f;
        label.fontStyle = FontStyles.Bold;
        label.color = new Color(0.10f, 0.10f, 0.14f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        if (prismFont != null)
            label.font = prismFont;

        Stretch(label.rectTransform);

        if (dynamicText != null)
        {
            PauseDynamicLabel dynamicLabel = buttonImage.gameObject.AddComponent<PauseDynamicLabel>();
            dynamicLabel.label = label;
            dynamicLabel.getText = dynamicText;
        }
    }

    private void CreateLabel(Transform parent, string text, float fontSize, Color color, Vector2 position, Vector2 size, bool bold)
    {
        CreateLabel(parent, text, fontSize, color, position, size, bold, TextAlignmentOptions.Center);
    }

    private void CreateLabel(Transform parent, string text, float fontSize, Color color, Vector2 position, Vector2 size, bool bold, TextAlignmentOptions alignment)
    {
        TextMeshProUGUI label = new GameObject("Txt_" + text).AddComponent<TextMeshProUGUI>();
        label.transform.SetParent(parent, false);
        label.text = text;
        label.raycastTarget = false; // labels never receive clicks
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = alignment;
        if (prismFont != null)
            label.font = prismFont;
        if (bold)
            label.fontStyle = FontStyles.Bold;

        RectTransform rect = label.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
    }

    private void ShowPanel(GameObject targetPanel)
    {
        if (mainPanel != null) mainPanel.SetActive(targetPanel == mainPanel);
        if (settingsPanel != null) settingsPanel.SetActive(targetPanel == settingsPanel);
        if (targetPanel == settingsPanel && pauseSettingsScrollRect != null)
            pauseSettingsScrollRect.verticalNormalizedPosition = 1f;
    }

    private string GetDifficultyLabel()
    {
        return (GameManager.Instance != null ? GameManager.Instance.difficulty : PlayerPrefs.GetString("Difficulty", "Normal")).ToUpperInvariant();
    }

    private void CycleDifficulty()
    {
        string current = GameManager.Instance != null ? GameManager.Instance.difficulty : PlayerPrefs.GetString("Difficulty", "Normal");
        string next = current == "Easy" ? "Normal" : current == "Normal" ? "Hard" : "Easy";
        if (GameManager.Instance != null)
        {
            GameManager.Instance.SetDifficulty(next);
        }
        else
        {
            PlayerPrefs.SetString("Difficulty", next);
            PlayerPrefs.Save();
        }
    }

    private string GetPerspectiveLabel()
    {
        // Third-person only.
        return "THIRD PERSON";
    }

    private void CyclePerspective()
    {
        // Third-person only: keep the button functional (re-applies runtime prefs),
        // but do not allow toggling to first-person.
        if (GameManager.Instance != null)
            GameManager.Instance.SetPerspectiveMode(GameManager.PerspectiveMode.ThirdPerson);
        else
        {
            PlayerPrefs.SetInt("PerspectiveMode", (int)GameManager.PerspectiveMode.ThirdPerson);
            PlayerPrefs.Save();
        }

        PlayerController player = FindFirstObjectByType<PlayerController>();
        if (player != null)
            player.RefreshGameplayPreferences();
    }

    private string GetMovementLabel()
    {
        GameManager.ControlStyleState state = GameManager.Instance != null
            ? GameManager.Instance.GetControlStyleState()
            : GameManager.LoadControlStyleState();
        return state == GameManager.ControlStyleState.ArrowsMouse ? "ARROWS + MOUSE" : "WASD + MOUSE";
    }

    private void CycleMovement()
    {
        GameManager.ControlStyleState current = GameManager.Instance != null
            ? GameManager.Instance.GetControlStyleState()
            : GameManager.LoadControlStyleState();
        GameManager.ControlStyleState next = current == GameManager.ControlStyleState.WasdMouse
            ? GameManager.ControlStyleState.ArrowsMouse
            : GameManager.ControlStyleState.WasdMouse;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.SetControlStyleState(next);
        }
        else
        {
            PlayerPrefs.SetInt("MovementScheme", (int)GameManager.ToMovementScheme(next));
            PlayerPrefs.Save();
        }

        PlayerController player = FindFirstObjectByType<PlayerController>();
        if (player != null)
            player.RefreshGameplayPreferences();
    }

    private string GetGraphicsLabel()
    {
        int tier = Mathf.Clamp(PlayerPrefs.GetInt("GraphicsTier", 2), 0, graphicsLabels.Length - 1);
        return graphicsLabels[tier];
    }

    private void CycleGraphics()
    {
        int tier = Mathf.Clamp(PlayerPrefs.GetInt("GraphicsTier", 2), 0, graphicsLabels.Length - 1);
        int nextTier = (tier + 1) % graphicsLabels.Length;
        PlayerPrefs.SetInt("GraphicsTier", nextTier);
        PlayerPrefs.Save();

        int qualityLevel = nextTier == 0 ? 0 :
            nextTier == 1 ? Mathf.Max(0, (QualitySettings.names.Length - 1) / 2) :
            Mathf.Max(0, QualitySettings.names.Length - 1);
        QualitySettings.SetQualityLevel(qualityLevel);
    }

    private string GetFullscreenLabel()
    {
        bool fullscreen = PlayerPrefs.GetInt("Fullscreen", Screen.fullScreen ? 1 : 0) == 1;
        return fullscreen ? "ON" : "OFF";
    }

    private void ToggleFullscreen()
    {
        bool fullscreen = PlayerPrefs.GetInt("Fullscreen", Screen.fullScreen ? 1 : 0) == 1;
        fullscreen = !fullscreen;
        PlayerPrefs.SetInt("Fullscreen", fullscreen ? 1 : 0);
        PlayerPrefs.Save();
        Screen.fullScreen = fullscreen;
    }

    private TMP_FontAsset ResolvePrismFont()
    {
        TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        for (int i = 0; i < fonts.Length; i++)
        {
            TMP_FontAsset font = fonts[i];
            if (font != null && font.name.Contains("Azonix"))
                return font;
        }

        return TMP_Settings.defaultFontAsset;
    }

    private void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}

public class PauseDynamicLabel : MonoBehaviour
{
    public TextMeshProUGUI label;
    public System.Func<string> getText;

    private void Update()
    {
        if (label != null && getText != null)
            label.text = getText();
    }
}
