using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

#if UNITY_EDITOR
[ExecuteAlways]
#endif
public class RuntimeMenuBuilder : MonoBehaviour
{
    public Sprite backgroundImage;
    public TMP_FontAsset customFont;
    public AudioClip lobbyMusicClip;

    // Matches GameManager.LevelWeaponNames for UI when Instance is briefly unavailable.
    private static readonly string[] LevelWeaponNamesFallback = {
        "Tactical Knife", "Razor Katana", "Shovel", "Baseball Bat", "Nunchucks",
        "Wrench", "Crowbar", "Hammer", "Axe", "Spear",
        "Nailed Plank", "Saw", "Sickle", "Morgenstern", "L3FTE",
        "Riot Shield"
    };

    private AudioSource lobbyAudioSource;
    bool _lobbyMusicLoadRoutineActive;

    static string GetWeaponNameForUiTile(int level)
    {
        if (GameManager.Instance != null)
            return GameManager.Instance.GetWeaponNameForLevel(level);
        int idx = Mathf.Clamp(level - 1, 0, LevelWeaponNamesFallback.Length - 1);
        return LevelWeaponNamesFallback[idx];
    }

    void Start()
    {
        MultiplayerMode.SetSinglePlayer();
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        SettingsManager.ApplyDisplayPreferences();
        AudioSettingsRuntime.ApplyListenerVolume();
        customFont = ResolveMenuFont();
        EnsureEventSystem();
        BuildCurrentScreen();
    }

#if UNITY_EDITOR
    private static bool _editorPreviewQueued;

    private void OnEnable()
    {
        if (!Application.isPlaying)
            QueueEditorMenuPreview();
    }

    [InitializeOnLoadMethod]
    private static void RegisterEditorMenuPreview()
    {
        EditorApplication.delayCall -= QueueGlobalEditorMenuPreview;
        EditorApplication.delayCall += QueueGlobalEditorMenuPreview;
        EditorSceneManager.sceneOpened -= OnEditorSceneOpenedForMenu;
        EditorSceneManager.sceneOpened += OnEditorSceneOpenedForMenu;
    }

    private static void OnEditorSceneOpenedForMenu(Scene scene, OpenSceneMode mode)
    {
        if (Application.isPlaying || !scene.IsValid() || scene.name != "MainMenu")
            return;

        QueueGlobalEditorMenuPreview();
    }

    private static void QueueGlobalEditorMenuPreview()
    {
        if (Application.isPlaying)
            return;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.name != "MainMenu")
            return;

        RuntimeMenuBuilder builder = Object.FindFirstObjectByType<RuntimeMenuBuilder>();
        if (builder != null)
            builder.QueueEditorMenuPreview();
    }

    private void QueueEditorMenuPreview()
    {
        if (Application.isPlaying || _editorPreviewQueued)
            return;

        _editorPreviewQueued = true;
        EditorApplication.delayCall += RunEditorMenuPreviewDeferred;
    }

    private void RunEditorMenuPreviewDeferred()
    {
        _editorPreviewQueued = false;
        if (this == null || !isActiveAndEnabled || Application.isPlaying)
            return;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.name != "MainMenu")
            return;

        customFont = ResolveMenuFont();
        EnsureEventSystem();

        var rootsBefore = new System.Collections.Generic.HashSet<int>(
            System.Array.ConvertAll(scene.GetRootGameObjects(), go => go.GetInstanceID()));

        BuildCurrentScreen();
        DisableLegacyEmptyCanvas();

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (!rootsBefore.Contains(root.GetInstanceID()))
                SetDontSaveRecursive(root);
        }
    }

    private static void SetDontSaveRecursive(GameObject go)
    {
        go.hideFlags |= HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        foreach (Transform child in go.transform)
            SetDontSaveRecursive(child.gameObject);
    }

    private static void DisableLegacyEmptyCanvas()
    {
        GameObject canvas = GameObject.Find("Canvas");
        if (canvas == null || canvas.name == "NeonCanvas")
            return;

        Canvas c = canvas.GetComponent<Canvas>();
        if (c != null && canvas.transform.childCount == 0)
            canvas.SetActive(false);
    }

    [MenuItem("PRISM/Rebuild Main Menu Preview")]
    private static void RebuildMainMenuPreviewMenu()
    {
        if (Application.isPlaying)
            return;

        RuntimeMenuBuilder builder = Object.FindFirstObjectByType<RuntimeMenuBuilder>();
        if (builder == null)
        {
            Debug.LogWarning("[RuntimeMenuBuilder] No RuntimeMenuBuilder found in the active scene.");
            return;
        }

        builder.customFont = builder.ResolveMenuFont();
        builder.EnsureEventSystem();
        builder.BuildCurrentScreen();
        DisableLegacyEmptyCanvas();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
    }
#endif

    private void DestroyUiObject(Object obj)
    {
        if (obj == null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(obj);
        else
#endif
            Destroy(obj);
    }

    void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject es = new GameObject("EventSystem");
            eventSystem = es.AddComponent<EventSystem>();
        }

        GameObject eventSystemObject = eventSystem.gameObject;
        StandaloneInputModule legacyModule = eventSystemObject.GetComponent<StandaloneInputModule>();
        if (legacyModule != null)
            DestroyUiObject(legacyModule);

        InputSystemUIInputModule inputModule = eventSystemObject.GetComponent<InputSystemUIInputModule>();
        if (inputModule == null)
            inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();

        inputModule.enabled = true;
        eventSystemObject.SetActive(true);
    }

    public void BuildCurrentScreen()
    {
        EnsureGameManager();
        GameObject existingCanvas = GameObject.Find("NeonCanvas");
        if (existingCanvas != null)
            DestroyUiObject(existingCanvas);

        bool isMainMenu = (GameManager.Instance == null || GameManager.PendingMenuScreen == GameManager.MenuScreen.MainMenu);
        if (isMainMenu)
        {
            RemoveLegacyCinematicStage();
            GameObject strayMenuAnim = GameObject.Find("BackgroundAnimationOverlay");
            if (strayMenuAnim != null)
                DestroyUiObject(strayMenuAnim);
        }

        GameObject canvasObj = new GameObject("NeonCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObj.AddComponent<GraphicRaycaster>();

        Image bg = new GameObject("Background").AddComponent<Image>();
        bg.transform.SetParent(canvasObj.transform, false);
        Stretch(bg.GetComponent<RectTransform>());
        bg.color = new Color(0.03f, 0.04f, 0.08f, 1f);
        Sprite resolvedBackground = ResolveMainMenuBackground();
        if (resolvedBackground != null)
        {
            bg.sprite = resolvedBackground;
            bg.color = Color.white;
            bg.preserveAspect = false;
        }

        // ─── LOBBY MUSIC ─────────────────────────────────────────────────────
        if (isMainMenu && Application.isPlaying)
        {
            SetupLobbyMusic();
        }

        Image overlay = new GameObject("Overlay").AddComponent<Image>();
        overlay.transform.SetParent(canvasObj.transform, false);
        Stretch(overlay.GetComponent<RectTransform>());
        // Neutral dark scrim (no blue tint) so the landscape background reads clearly.
        overlay.color = isMainMenu
            ? new Color(0f, 0f, 0f, 0.26f)
            : new Color(0.01f, 0.02f, 0.05f, 0.48f);

        if (isMainMenu)
        {
            // Loading / menu motion graphics disabled — flat scrim + static background only (performance).
            // BuildAnimatedBackgroundOverlay(canvasObj.transform);
            BuildMainMenu(canvasObj.transform);
            return;
        }
        BuildResultsMenu(canvasObj.transform);
    }

    void RemoveLegacyCinematicStage()
    {
        GameObject existing = GameObject.Find("CinematicStage");
        if (existing != null)
            DestroyUiObject(existing);
    }

    Sprite ResolveMainMenuBackground()
    {
        if (backgroundImage != null)
            return backgroundImage;

        Sprite fromResources = Resources.Load<Sprite>("MainMenuBackground");
        if (fromResources != null)
        {
            backgroundImage = fromResources;
            return fromResources;
        }

#if UNITY_EDITOR
        // In Play Mode the scene instance is sometimes created without the
        // inspector reference. Restore the user's original background asset
        // by path instead of falling back to the procedural stage.
        Sprite editorSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Hamed/Resources/MainMenuBackground.jpg");
        if (editorSprite == null)
            editorSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Hamed/Materials/Images/MainMenuBackground.jpg");
        if (editorSprite != null)
        {
            backgroundImage = editorSprite;
            return editorSprite;
        }
#endif

        return null;
    }

    void BuildAnimatedBackgroundOverlay(Transform root)
    {
        // Intentionally empty: streak / pulse menu animations removed (flat UI only).
        if (root == null) { }
    }

    // ─── MAIN MENU ────────────────────────────────────────────────────────────────
    void BuildMainMenu(Transform root)
    {
        // Titles higher so the menu panel (below) never overlaps subtitle text.
        MakeText(root, "PRISM-7", 120, new Color(0.92f, 0.92f, 1f, 1f),
            new Vector2(0.18f, 0.82f), new Vector2(0.82f, 0.97f), true);
        MakeText(root, "WEAPON TRIALS", 74, new Color(0.62f, 0.48f, 0.92f, 0.78f),
            new Vector2(0.14f, 0.70f), new Vector2(0.86f, 0.82f), true);

        MenuUIManager.BuildProfileHeader(root, customFont);

        bool isNew         = GameManager.Instance == null || GameManager.Instance.IsNewPlayer();
        int  continueLevel = GameManager.Instance != null ? GameManager.Instance.GetContinueLevel() : 1;
        string startLabel  = isNew ? "START" : "CONTINUE";

        Transform menuPanel = CreateMainMenuPanel(root);

        // Single centered column (reference): even spacing, pills hug text, top-to-bottom order.
        var navList = new System.Collections.Generic.List<Selectable>(8);

        // Layout order: Continue → Multiplayer → Select Level → Prism Store → Challenges → Settings → Credits → Quit
        UnityAction startAction = () => GameManager.Instance?.StartRun(continueLevel);
        UnityAction multiplayerAction = () => ToggleMultiplayer(root);
        UnityAction selectLevelAction = () => ToggleLevelSelect(root);
        UnityAction storeAction = () => ToggleStore(root);
        UnityAction challengesAction = () => ToggleChallenges(root);
        UnityAction settingsAction = () => UnityEngine.SceneManagement.SceneManager.LoadScene("Settings");
        UnityAction creditsAction = () => UnityEngine.SceneManagement.SceneManager.LoadScene("Credits");
        UnityAction quitAction = QuitFromMainMenu;

        Button startBtn = MakeCenteredPillMenuButton(menuPanel, startLabel, startAction);
        Button multiplayerBtn = MakeCenteredPillMenuButton(menuPanel, "MULTIPLAYER", multiplayerAction);
        Button selectLevelBtn = MakeCenteredPillMenuButton(menuPanel, "SELECT LEVEL", selectLevelAction);
        Button storeBtn = MakeCenteredPillMenuButton(menuPanel, "PRISM STORE", storeAction);
        Button challengesBtn = MakeCenteredPillMenuButton(menuPanel, "CHALLENGES", challengesAction);
        Button settingsBtn = MakeCenteredPillMenuButton(menuPanel, "SETTINGS", settingsAction);
        Button creditsBtn = MakeCenteredPillMenuButton(menuPanel, "CREDITS", creditsAction);
        Button quitBtn = MakeCenteredPillMenuButton(menuPanel, "QUIT", quitAction);

        RebindMainMenuButton(startBtn, startAction);
        RebindMainMenuButton(multiplayerBtn, multiplayerAction);
        RebindMainMenuButton(selectLevelBtn, selectLevelAction);
        RebindMainMenuButton(storeBtn, storeAction);
        RebindMainMenuButton(challengesBtn, challengesAction);
        RebindMainMenuButton(settingsBtn, settingsAction);
        RebindMainMenuButton(creditsBtn, creditsAction);
        RebindMainMenuButton(quitBtn, quitAction);

        navList.Add(startBtn);
        navList.Add(multiplayerBtn);
        navList.Add(selectLevelBtn);
        navList.Add(storeBtn);
        navList.Add(challengesBtn);
        navList.Add(settingsBtn);
        navList.Add(creditsBtn);
        navList.Add(quitBtn);

        MenuNavigationManager.AttachLinear(root.gameObject, navList);

        if (!PlayerProfile.HasUsername)
            ShowNameEntryOverlayEnhanced(root);
    }

    void ToggleMultiplayer(Transform root)
    {
        Transform existing = root.Find("MultiplayerOverlay");
        if (existing != null)
        {
            DestroyUiObject(existing.gameObject);
            SetMainMenuElementsVisible(root, true);
            return;
        }

        DestroyOverlay(root, "LevelSelectOverlay");
        DestroyOverlay(root, "CustomMatchOverlay");
        DestroyOverlay(root, "StoreOverlay");
        DestroyOverlay(root, "ChallengesOverlay");
        SetMainMenuElementsVisible(root, false);

        GameObject overlayObj = new GameObject("MultiplayerOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.14f);

        // ── Panel ─────────────────────────────────────────────────────────────
        GameObject panelObj = new GameObject("MultiplayerPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        panel.color = new Color(0.06f, 0.10f, 0.22f, 0.96f);
        Outline panelOutline = panelObj.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.30f, 0.55f, 1f, 0.90f);
        panelOutline.effectDistance = new Vector2(3f, -3f);
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(1040f, 820f), new Vector2(0f, -8f));

        // Fade the panel in on open
        CanvasGroup panelCG = panelObj.AddComponent<CanvasGroup>();
        UIAnimationHelper.FadeIn(panelCG, 0.22f);

        // ── Title ─────────────────────────────────────────────────────────────
        MakeText(panelObj.transform, "MULTIPLAYER", 64, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.05f, 0.85f), new Vector2(0.95f, 0.95f), true);

        // Subtitle — premium / COD-style (ASCII only).
        MakeText(panelObj.transform, "DEPLOY ONLINE - UP TO 8 OPERATORS", 21f,
            new Color(0.48f, 0.66f, 1f, 0.82f),
            new Vector2(0.08f, 0.79f), new Vector2(0.92f, 0.84f), false);

        // ── Name input ────────────────────────────────────────────────────────
        TMP_InputField nameInput = CreateMultiplayerNameInput(panelObj.transform);

        // ── Game Mode Selector Widget (Side-by-Side) ──────────────────────────
        System.Action updateModeUI = null;
        Image coopBg = null, chaosBg = null, pvpBg = null;
        Outline coopOutline = null, chaosOutline = null, pvpOutline = null;
        TextMeshProUGUI coopText = null, chaosText = null, pvpText = null;

        Button modeCoopBtn = MakeGameModeButton(panelObj.transform, "CO-OP SURVIVAL",
            new Vector2(0.15f, 0.57f), new Vector2(0.37f, 0.65f),
            () => { MultiplayerMode.SetMode(MpGameMode.CoopSurvival); updateModeUI(); },
            out coopBg, out coopOutline, out coopText);

        Button modeChaosBtn = MakeGameModeButton(panelObj.transform, "HYBRID CHAOS",
            new Vector2(0.39f, 0.57f), new Vector2(0.61f, 0.65f),
            () => { MultiplayerMode.SetMode(MpGameMode.HybridChaos); updateModeUI(); },
            out chaosBg, out chaosOutline, out chaosText);

        Button modePvpBtn = MakeGameModeButton(panelObj.transform, "PURE PVP",
            new Vector2(0.63f, 0.57f), new Vector2(0.85f, 0.65f),
            () => { MultiplayerMode.SetMode(MpGameMode.PurePvP); updateModeUI(); },
            out pvpBg, out pvpOutline, out pvpText);

        updateModeUI = () => {
            MpGameMode cur = MultiplayerMode.ActiveMode;

            // Harmonious premium palettes (vibrant blues and sleek outlines)
            Color activeBg = new Color(0.12f, 0.36f, 0.90f, 0.85f);
            Color activeHoverBg = new Color(0.18f, 0.48f, 1f, 0.95f);
            Color activeOutline = new Color(0.35f, 0.75f, 1f, 0.90f);
            Color activeText = new Color(0.95f, 0.98f, 1f, 1f);

            Color inactiveBg = new Color(0.03f, 0.05f, 0.12f, 0.45f);
            Color inactiveHoverBg = new Color(0.06f, 0.10f, 0.22f, 0.70f);
            Color inactiveOutline = new Color(0.20f, 0.35f, 0.60f, 0.40f);
            Color inactiveText = new Color(0.55f, 0.70f, 0.90f, 0.75f);

            // Co-op
            bool isCoop = cur == MpGameMode.CoopSurvival;
            coopBg.color = isCoop ? activeBg : inactiveBg;
            coopOutline.effectColor = isCoop ? activeOutline : inactiveOutline;
            coopText.color = isCoop ? activeText : inactiveText;
            var hCoop = modeCoopBtn.GetComponent<MenuButtonHoverEffect>();
            if (hCoop != null) {
                hCoop.normalBackgroundColor = isCoop ? activeBg : inactiveBg;
                hCoop.hoverBackgroundColor = isCoop ? activeHoverBg : inactiveHoverBg;
                hCoop.normalOutlineColor = isCoop ? activeOutline : inactiveOutline;
                hCoop.hoverOutlineColor = isCoop ? activeOutline : inactiveOutline;
                hCoop.normalTextColor = isCoop ? activeText : inactiveText;
            }

            // Chaos
            bool isChaos = cur == MpGameMode.HybridChaos;
            chaosBg.color = isChaos ? activeBg : inactiveBg;
            chaosOutline.effectColor = isChaos ? activeOutline : inactiveOutline;
            chaosText.color = isChaos ? activeText : inactiveText;
            var hChaos = modeChaosBtn.GetComponent<MenuButtonHoverEffect>();
            if (hChaos != null) {
                hChaos.normalBackgroundColor = isChaos ? activeBg : inactiveBg;
                hChaos.hoverBackgroundColor = isChaos ? activeHoverBg : inactiveHoverBg;
                hChaos.normalOutlineColor = isChaos ? activeOutline : inactiveOutline;
                hChaos.hoverOutlineColor = isChaos ? activeOutline : inactiveOutline;
                hChaos.normalTextColor = isChaos ? activeText : inactiveText;
            }

            // PvP
            bool isPvp = cur == MpGameMode.PurePvP;
            pvpBg.color = isPvp ? activeBg : inactiveBg;
            pvpOutline.effectColor = isPvp ? activeOutline : inactiveOutline;
            pvpText.color = isPvp ? activeText : inactiveText;
            var hPvp = modePvpBtn.GetComponent<MenuButtonHoverEffect>();
            if (hPvp != null) {
                hPvp.normalBackgroundColor = isPvp ? activeBg : inactiveBg;
                hPvp.hoverBackgroundColor = isPvp ? activeHoverBg : inactiveHoverBg;
                hPvp.normalOutlineColor = isPvp ? activeOutline : inactiveOutline;
                hPvp.hoverOutlineColor = isPvp ? activeOutline : inactiveOutline;
                hPvp.normalTextColor = isPvp ? activeText : inactiveText;
            }
        };

        updateModeUI();

        // ── Status area: connection text + animated dots (no instructional copy).
        TextMeshProUGUI status = MakeText(panelObj.transform,
            "",
            24f, new Color(0.78f, 0.88f, 1f, 0.95f),
            new Vector2(0.08f, 0.118f), new Vector2(0.71f, 0.182f), false);
        status.alignment = TextAlignmentOptions.Left;
        status.margin = new Vector4(10f, 0f, 0f, 0f);

        TextMeshProUGUI dotsLabel = MakeText(panelObj.transform, "",
            26f, new Color(0.55f, 0.80f, 1f, 0.95f),
            new Vector2(0.73f, 0.118f), new Vector2(0.94f, 0.182f), false);
        dotsLabel.alignment = TextAlignmentOptions.Left;

        // ── Photon launcher ───────────────────────────────────────────────────
        GameObject launcherObj = new GameObject("PhotonLauncher");
        launcherObj.transform.SetParent(overlayObj.transform, false);
        PhotonLauncher launcher = launcherObj.AddComponent<PhotonLauncher>();
        launcher.playerNameInput = nameInput;
        launcher.statusText = status;

        // ── Buttons (neon gradient tiers + rim glow hover) ─────────────────────
        // Capture the button reference up-front so the open-level-selector
        // callback can mutate its label text after the user picks a level.
        Button selectLevelBtn = null;
        TextMeshProUGUI selectLevelBtnLabel = null;

        System.Func<int, string> buildLevelButtonText = (lvl) =>
        {
            int safe = Mathf.Clamp(lvl, 1, GameManager.TotalLevels);
            string weapon = GameManager.Instance != null
                ? GameManager.Instance.GetWeaponNameForLevel(safe)
                : (safe == 2 ? "Razor Katana" : "Tactical Knife");
            return $"SELECT LEVEL  ({safe} - {weapon.ToUpperInvariant()})";
        };

        // MakeMultiplayerNeonButton expects UnityAction (UnityEngine.Events),
        // not System.Action — must declare the local as UnityAction.
        UnityAction openLevelSelector = () =>
        {
            BuildMultiplayerLevelSelectOverlay(overlayObj.transform, () =>
            {
                int curr = Mathf.Clamp(MultiplayerRuntimeConfig.MultiplayerSelectedLevel, 1, GameManager.TotalLevels);
                string weapon = GameManager.Instance != null
                    ? GameManager.Instance.GetWeaponNameForLevel(curr)
                    : (curr == 2 ? "Razor Katana" : "Tactical Knife");
                if (selectLevelBtnLabel != null)
                    selectLevelBtnLabel.text = buildLevelButtonText(curr);
                Debug.Log($"[MPMenu] selected level = {curr} weapon = {weapon}");
            });
        };

        selectLevelBtn = MakeMultiplayerNeonButton(panelObj.transform, buildLevelButtonText(MultiplayerRuntimeConfig.MultiplayerSelectedLevel),
            new Vector2(0.15f, 0.465f), new Vector2(0.85f, 0.535f),
            openLevelSelector, 24f, true,
            new Color(0.05f, 0.38f, 0.95f, 1f), new Color(0.10f, 0.55f, 1f, 1f),
            new Color(0.35f, 0.85f, 1f, 1f),
            new Color(0.25f, 0.72f, 1f, 0.95f), new Color(0.55f, 0.95f, 1f, 1f));
        selectLevelBtnLabel = selectLevelBtn.GetComponentInChildren<TextMeshProUGUI>();

        Button createBtn = MakeMultiplayerNeonButton(panelObj.transform, "CREATE ROOM",
            new Vector2(0.15f, 0.375f), new Vector2(0.85f, 0.445f),
            launcher.CreateRoomWithCode, 28f, true,
            new Color(0.16f, 0.08f, 0.58f, 1f), new Color(0.28f, 0.14f, 0.78f, 1f),
            new Color(0.62f, 0.38f, 1f, 1f),
            new Color(0.48f, 0.30f, 1f, 0.75f), new Color(0.78f, 0.55f, 1f, 1f));

        TMP_InputField roomCodeInput = CreateMultiplayerRoomCodeInput(panelObj.transform);

        Button joinCodeBtn = MakeMultiplayerNeonButton(panelObj.transform, "JOIN BY CODE",
            new Vector2(0.52f, 0.285f), new Vector2(0.85f, 0.355f),
            launcher.JoinRoomWithCode, 28f, true,
            new Color(0.05f, 0.24f, 0.68f, 1f), new Color(0.08f, 0.36f, 0.88f, 1f),
            new Color(0.22f, 0.62f, 0.98f, 1f),
            new Color(0.18f, 0.58f, 0.92f, 0.55f), new Color(0.42f, 0.78f, 1f, 0.95f));

        // JOIN RANDOM ROOM button intentionally removed (host-pick-level flow).

        Button backBtn = MakePanelButton(panelObj.transform, "BACK",
            new Vector2(0.38f, 0.038f), new Vector2(0.62f, 0.108f),
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
            }, 30f, false, true);

        launcher.connectButton = selectLevelBtn;
        launcher.joinRandomButton = null;
        launcher.createRoomButton = createBtn;
        launcher.roomCodeInput = roomCodeInput;
        launcher.joinByCodeButton = joinCodeBtn;

        // Animated dots: watch the CREATE button now (the Connect/Play-Online
        // button was repurposed into SELECT LEVEL, which doesn't talk to
        // Photon). Dots cycle when Create Room becomes non-interactable.
        MultiplayerLoadingDots dots = launcherObj.AddComponent<MultiplayerLoadingDots>();
        dots.dotsLabel = dotsLabel;
        dots.watchButton = createBtn;

        var nav = new System.Collections.Generic.List<Selectable>(10);
        nav.Add(nameInput);
        nav.Add(modeCoopBtn);
        nav.Add(modeChaosBtn);
        nav.Add(modePvpBtn);
        nav.Add(selectLevelBtn);
        nav.Add(createBtn);
        nav.Add(roomCodeInput);
        nav.Add(joinCodeBtn);
        nav.Add(backBtn);
        MenuNavigationManager.AttachLinear(overlayObj, nav);
    }

    static void RebindMainMenuButton(Button button, UnityAction action)
    {
        if (button == null || action == null)
            return;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);

        Image image = button.GetComponent<Image>();
        if (image != null)
            button.targetGraphic = image;

        button.interactable = true;
    }

    // ─── Multiplayer Level Selector overlay ───────────────────────────────────
    // Builds a scrollable list of Level 1..16 with the weapon name from
    // GameManager. Selecting a row writes
    // MultiplayerRuntimeConfig.MultiplayerSelectedLevel, fires the supplied
    // callback (which refreshes the SELECT LEVEL button label), and closes
    // the overlay. Pure UI — no Photon / matchmaking changes.
    void BuildMultiplayerLevelSelectOverlay(Transform parent, System.Action onSelected)
    {
        // Replace any prior selector so re-opening doesn't stack overlays.
        Transform existing = parent.Find("MpLevelSelectOverlay");
        if (existing != null) DestroyUiObject(existing.gameObject);

        GameObject overlayObj = new GameObject("MpLevelSelectOverlay");
        overlayObj.transform.SetParent(parent, false);
        Image dim = overlayObj.AddComponent<Image>();
        Stretch(dim.rectTransform);
        dim.color = new Color(0.01f, 0.02f, 0.05f, 0.78f);
        // Close on dim click for quick dismiss.
        Button dimBtn = overlayObj.AddComponent<Button>();
        dimBtn.targetGraphic = dim;
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener(() => DestroyUiObject(overlayObj));

        GameObject panel = new GameObject("MpLevelSelectPanel");
        panel.transform.SetParent(overlayObj.transform, false);
        Image panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.06f, 0.10f, 0.22f, 0.98f);
        Outline panelOutline = panel.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.30f, 0.55f, 1f, 0.90f);
        panelOutline.effectDistance = new Vector2(3f, -3f);
        SetCenteredRect(panel.GetComponent<RectTransform>(), new Vector2(720f, 700f), Vector2.zero);

        MakeText(panel.transform, "SELECT MULTIPLAYER LEVEL", 36f, new Color(0.94f, 0.96f, 1f, 1f),
            new Vector2(0.06f, 0.88f), new Vector2(0.94f, 0.97f), true);

        // ScrollRect content (vertical).
        GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer));
        viewportObj.transform.SetParent(panel.transform, false);
        Image viewportImg = viewportObj.AddComponent<Image>();
        viewportImg.color = new Color(0.02f, 0.04f, 0.10f, 0.40f);
        viewportObj.AddComponent<RectMask2D>();
        ScrollRect scroll = viewportObj.AddComponent<ScrollRect>();
        RectTransform vpRect = viewportObj.GetComponent<RectTransform>();
        vpRect.anchorMin = new Vector2(0.06f, 0.14f);
        vpRect.anchorMax = new Vector2(0.94f, 0.85f);
        vpRect.offsetMin = vpRect.offsetMax = Vector2.zero;

        GameObject content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewportObj.transform, false);
        RectTransform contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, 0f);
        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(14, 14, 12, 12);
        vlg.spacing = 8f;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true;
        vlg.childControlHeight = false;
        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = contentRect;
        scroll.viewport = vpRect;
        scroll.vertical = true;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        int currentSelected = Mathf.Clamp(MultiplayerRuntimeConfig.MultiplayerSelectedLevel, 1, GameManager.TotalLevels);

        for (int i = 1; i <= GameManager.TotalLevels; i++)
        {
            int captured = i;
            string weapon = GameManager.Instance != null
                ? GameManager.Instance.GetWeaponNameForLevel(i)
                : (i == 2 ? "Razor Katana" : "Tactical Knife");
            string rowLabel = $"LEVEL {i} - {weapon.ToUpperInvariant()}";

            GameObject row = new GameObject($"LvlRow_{i}");
            row.transform.SetParent(content.transform, false);
            Image rowImg = row.AddComponent<Image>();
            bool isSelected = i == currentSelected;
            rowImg.color = isSelected
                ? new Color(0.18f, 0.48f, 1f, 0.85f)
                : new Color(0.04f, 0.08f, 0.18f, 0.85f);
            Outline rowOutline = row.AddComponent<Outline>();
            rowOutline.effectColor = isSelected
                ? new Color(0.55f, 0.95f, 1f, 0.95f)
                : new Color(0.25f, 0.45f, 0.85f, 0.45f);
            rowOutline.effectDistance = new Vector2(1.5f, -1.5f);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 58f;
            le.minHeight = 54f;

            TextMeshProUGUI rowText = CreateCenteredLabel(row.transform, rowLabel, 24f, Color.white, true);
            rowText.alignment = TextAlignmentOptions.Center;

            Button rowBtn = row.AddComponent<Button>();
            rowBtn.targetGraphic = rowImg;
            rowBtn.onClick.AddListener(() =>
            {
                MultiplayerRuntimeConfig.MultiplayerSelectedLevel = captured;
                onSelected?.Invoke();
                DestroyUiObject(overlayObj);
            });
        }

        Button closeBtn = MakePanelButton(panel.transform, "CLOSE",
            new Vector2(0.35f, 0.03f), new Vector2(0.65f, 0.12f),
            () => DestroyUiObject(overlayObj), 26f, false, true);
    }

    Button MakeGameModeButton(Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax, System.Action action, out Image bgImage, out Outline outline, out TextMeshProUGUI textComp)
    {
        GameObject obj = new GameObject(label + "_ModeBtn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        bgImage = obj.AddComponent<Image>();
        outline = obj.AddComponent<Outline>();
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = bgImage;
        btn.onClick.AddListener(() => action());

        textComp = CreateCenteredLabel(obj.transform, label, 18f, Color.white, true);
        textComp.fontStyle = FontStyles.Bold;

        MenuButtonHoverEffect hover = obj.AddComponent<MenuButtonHoverEffect>();
        hover.label = textComp;
        hover.background = bgImage;
        hover.hoverScale = new Vector3(1.05f, 1.05f, 1f);
        hover.normalScale = Vector3.one;

        return btn;
    }

    TMP_InputField CreateMultiplayerNameInput(Transform parent)
    {
        GameObject inputObj = new GameObject("PlayerNameInput");
        inputObj.transform.SetParent(parent, false);
        Image image = inputObj.AddComponent<Image>();
        image.color = new Color(0.028f, 0.042f, 0.088f, 0.94f);
        Outline outline = inputObj.AddComponent<Outline>();
        outline.effectColor = new Color(0.28f, 0.78f, 1f, 0.72f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        RectTransform rect = inputObj.GetComponent<RectTransform>();
        // Narrower name field — was 0.16–0.84 which felt visually too wide on
        // the 1040 px panel. Centred, only slightly wider than the inner
        // button column for a cleaner look.
        rect.anchorMin = new Vector2(0.30f, 0.68f);
        rect.anchorMax = new Vector2(0.70f, 0.76f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        TMP_InputField input = inputObj.AddComponent<TMP_InputField>();
        TextMeshProUGUI text = CreateCenteredLabel(inputObj.transform, PlayerProfile.HasUsername ? PlayerProfile.Username : string.Empty, 26f, new Color(0.94f, 0.98f, 1f, 1f), true);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.margin = new Vector4(18f, 10f, 18f, 10f);
        TextMeshProUGUI placeholder = CreateCenteredLabel(inputObj.transform, "PLAYER NAME", 24f, new Color(0.55f, 0.72f, 1f, 0.48f), false);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.margin = new Vector4(18f, 10f, 18f, 10f);

        input.textComponent = text;
        input.placeholder = placeholder;
        input.text = PlayerProfile.HasUsername ? PlayerProfile.Username : string.Empty;
        input.characterLimit = PlayerProfile.MaxNameLength;
        return input;
    }

    TMP_InputField CreateMultiplayerRoomCodeInput(Transform parent)
    {
        GameObject inputObj = new GameObject("RoomCodeInput");
        inputObj.transform.SetParent(parent, false);
        Image image = inputObj.AddComponent<Image>();
        image.color = new Color(0.028f, 0.042f, 0.088f, 0.94f);
        Outline outline = inputObj.AddComponent<Outline>();
        outline.effectColor = new Color(0.28f, 0.78f, 1f, 0.72f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        RectTransform rect = inputObj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.15f, 0.285f);
        rect.anchorMax = new Vector2(0.48f, 0.355f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        TMP_InputField input = inputObj.AddComponent<TMP_InputField>();
        TextMeshProUGUI text = CreateCenteredLabel(inputObj.transform, string.Empty, 26f, new Color(0.94f, 0.98f, 1f, 1f), true);
        text.alignment = TextAlignmentOptions.Center;
        
        TextMeshProUGUI placeholder = CreateCenteredLabel(inputObj.transform, "ENTER CODE", 24f, new Color(0.55f, 0.72f, 1f, 0.48f), false);
        placeholder.alignment = TextAlignmentOptions.Center;

        input.textComponent = text;
        input.placeholder = placeholder;
        input.characterLimit = 6;
        
        // Force uppercase input securely
        input.onValueChanged.AddListener((val) => {
            string upper = val.ToUpper();
            if (val != upper)
            {
                input.text = upper;
            }
        });
        
        return input;
    }

    // ─── RESULTS MENU ─────────────────────────────────────────────────────────────
    void BuildResultsMenu(Transform root)
    {
        EnsureGameManager();

        if (MultiplayerMode.IsMultiplayer)
        {
            Debug.Log("[EndUI] blocked stale/invalid outcome in multiplayer");
            return;
        }

        GameManager.MenuScreen outcome = GameManager.ResolveAuthoritativeOutcomeScreen();
        GameManager.SetPendingMenuScreen(outcome);

        string title = "MISSION RESULT", subtitle = "Back to the Prism.", primaryButton = "MAIN MENU";
        UnityEngine.Events.UnityAction primaryAction = GoToMainMenuSafe;
        bool allowRetry = GameManager.IsRetrySupported();

        string winnerName  = "—";
        int    winnerKills = 0;
        bool   winnerIsPlayer = false;
        if (MatchStatsManager.Instance != null)
        {
            var leaders = MatchStatsManager.Instance.GetTopCombatants(1);
            if (leaders != null && leaders.Count > 0)
            {
                winnerName     = leaders[0].DisplayName;
                winnerKills    = leaders[0].Kills;
                winnerIsPlayer = leaders[0].IsPlayer;
            }
        }
        int liveCredits = SessionManager.Instance != null ? SessionManager.Instance.Credits : 0;

        if (outcome == GameManager.MenuScreen.LevelComplete)
        {
            Debug.Log("[EndUI] outcome = Victory reason = results menu level complete");
            int stars = GameManager.Instance != null ? GameManager.Instance.CalculateStars(120f) : 1;
            int score  = GameManager.Instance != null ? GameManager.Instance.score : 0;
            int level  = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
            title = "LEVEL COMPLETE";
            subtitle = "TOP OPERATIVE:  " + winnerName.ToUpperInvariant() + "  (" + winnerKills + " KILLS)"
                     + (winnerIsPlayer ? "   +500 CR BONUS" : "")
                     + "\nSTARS  " + stars + " / 3"
                     + "\nSCORE  " + score
                     + "\nLEVEL  " + level
                     + "\nCREDITS  " + liveCredits.ToString("N0");
            primaryButton = "NEXT LEVEL";
            primaryAction = () => { if (GameManager.Instance != null) GameManager.Instance.LoadNextLevel(); else GoToMainMenuSafe(); };
        }
        else if (outcome == GameManager.MenuScreen.GameOver)
        {
            Debug.Log("[EndUI] outcome = MissionFailed reason = results menu game over");
            int level = GameManager.Instance != null ? GameManager.Instance.currentLevel : 1;
            int score = GameManager.Instance != null ? GameManager.Instance.score : 0;
            title = "MISSION FAILED";
            subtitle = "WINNER  " + winnerName.ToUpperInvariant() + "  (" + winnerKills + " KILLS)"
                     + "\nLEVEL  " + level
                     + "\nSCORE  " + score
                     + "\nCREDITS  " + liveCredits.ToString("N0");
            if (allowRetry)
            {
                primaryButton = "RETRY";
                primaryAction = () => { if (GameManager.Instance != null) GameManager.Instance.ReplayCurrentLevel(); else UnityEngine.SceneManagement.SceneManager.LoadScene("GameScene"); };
            }
            else
            {
                primaryButton = "MAIN MENU";
                primaryAction = GoToMainMenuSafe;
            }
        }
        else if (outcome == GameManager.MenuScreen.Victory)
        {
            Debug.Log("[EndUI] outcome = Victory reason = results menu campaign victory");
            int score = GameManager.Instance != null ? GameManager.Instance.score : 0;
            title = "PRISM CONQUERED";
            subtitle = "YOU ARE #1 — EVERY TRIAL FALLS.\nAll 20 missions completed.\nFinal Score: " + score
                     + "\nCREDITS  " + liveCredits.ToString("N0");
            primaryButton = "PLAY AGAIN";
            primaryAction = () => { if (GameManager.Instance != null) GameManager.Instance.StartRun(1); else UnityEngine.SceneManagement.SceneManager.LoadScene("GameScene"); };
        }

        bool celebrate = outcome == GameManager.MenuScreen.LevelComplete
            || outcome == GameManager.MenuScreen.Victory;

        string displayTitle = title;
        if (outcome == GameManager.MenuScreen.LevelComplete)
            displayTitle = "VICTORY";

        Color titleColor = celebrate
            ? new Color(1f, 0.86f, 0.2f, 1f)
            : new Color(0.92f, 0.92f, 1f, 1f);
        float titleSize = celebrate ? 112f : 92f;
        Vector2 titleMin = celebrate ? new Vector2(0.08f, 0.70f) : new Vector2(0.20f, 0.62f);
        Vector2 titleMax = celebrate ? new Vector2(0.92f, 0.90f) : new Vector2(0.80f, 0.82f);

        TextMeshProUGUI titleTmp = MakeText(root, displayTitle, titleSize, titleColor,
            titleMin, titleMax, true);

        TextMeshProUGUI bannerTmp = null;
        if (celebrate)
        {
            string bannerLine = outcome == GameManager.MenuScreen.Victory
                ? "#1 ON THE BOARD — LEGEND STATUS"
                : "#1 PLACEMENT — CHAMPION";
            bannerTmp = MakeText(root, bannerLine, 46, new Color(1f, 0.42f, 0.95f, 1f),
                new Vector2(0.06f, 0.58f), new Vector2(0.94f, 0.68f), true);
        }

        float subSize = celebrate ? 34f : 42f;
        Vector2 subMin = celebrate ? new Vector2(0.20f, 0.28f) : new Vector2(0.25f, 0.40f);
        Vector2 subMax = celebrate ? new Vector2(0.80f, 0.56f) : new Vector2(0.75f, 0.60f);
        MakeText(root, subtitle, subSize, new Color(0.94f, 0.94f, 1f, 1f), subMin, subMax);

        // Primary action button (NEXT LEVEL / RETRY / PLAY AGAIN) — large, bright
        MakeActiveButton(root, primaryButton, new Vector2(0.32f, 0.24f), new Vector2(0.68f, 0.33f),
            primaryAction, new Color(0.60f, 0.22f, 0.88f, 1f), Color.white);

        // MAIN MENU button — slightly smaller, secondary style
        MakeActiveButton(root, "MAIN MENU", new Vector2(0.35f, 0.11f), new Vector2(0.65f, 0.20f),
            GoToMainMenuSafe, new Color(0.18f, 0.18f, 0.28f, 1f), new Color(0.88f, 0.88f, 1f, 1f));

        if (celebrate)
        {
            WinScreenCelebration fanfare = root.gameObject.AddComponent<WinScreenCelebration>();
            fanfare.Configure(titleTmp.rectTransform, bannerTmp, root);
        }
    }

    void EnsureGameManager()
    {
        if (GameManager.Instance != null) return;
        GameObject gm = new GameObject("GameManager");
        gm.AddComponent<GameManager>();
    }

    void GoToMainMenuSafe()
    {
        if (GameManager.Instance != null)
            GameManager.Instance.GoToMainMenu();
        else
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
    }

    // A clearly active, prominent button with solid background
    void MakeActiveButton(Transform parent, string label,
        Vector2 anchorMin, Vector2 anchorMax,
        UnityEngine.Events.UnityAction action,
        Color bgColor, Color textColor)
    {
        GameObject obj = new GameObject("ActiveBtn_" + label);
        obj.transform.SetParent(parent, false);

        Image img = obj.AddComponent<Image>();
        img.color = bgColor;

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.interactable = true;
        btn.onClick.AddListener(action);

        // Ensure button is unblocked
        btn.onClick.AddListener(action); // double-register is harmless but let's remove dupe
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(action);

        RectTransform rect = obj.GetComponent<RectTransform>();
        float cx = (anchorMin.x + anchorMax.x) * 0.5f;
        rect.anchorMin = new Vector2(cx, anchorMin.y);
        rect.anchorMax = new Vector2(cx, anchorMax.y);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = new Color(textColor.r * 0.6f, textColor.g * 0.6f, textColor.b * 0.6f, 0.5f);
        outline.effectDistance = new Vector2(2f, -2f);

        TextMeshProUGUI lbl = CreateCenteredLabel(obj.transform, label, 32, textColor, true);
        lbl.fontStyle = FontStyles.Bold;
        lbl.ForceMeshUpdate();
        float w = Mathf.Clamp(lbl.GetPreferredValues().x + 72f, 220f, 960f);
        rect.sizeDelta = new Vector2(w, 0f);

        AttachHoverEffect(obj, lbl, img, bgColor,
            new Color(Mathf.Min(1f, bgColor.r + 0.18f), Mathf.Min(1f, bgColor.g + 0.08f), Mathf.Min(1f, bgColor.b + 0.18f), 1f),
            textColor);
    }

    void ToggleLevelSelect(Transform root)
    {
        Transform existing = root.Find("LevelSelectOverlay");
        if (existing != null)
        {
            DestroyUiObject(existing.gameObject);
            SetMainMenuElementsVisible(root, true);
            return;
        }

        // Tear down any other overlay (e.g. Custom Match) before showing this one.
        DestroyOverlay(root, "CustomMatchOverlay");
        SetMainMenuElementsVisible(root, false);

        GameObject overlayObj = new GameObject("LevelSelectOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.10f);

        GameObject panelObj = new GameObject("LevelSelectPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        // Closer to the older "clean blue" card look (more solid than the newer translucent panel).
        panel.color = new Color(0.14f, 0.20f, 0.36f, 0.62f);
        Outline panelOutline = panelObj.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.12f, 0.20f, 0.40f, 0.75f);
        panelOutline.effectDistance = new Vector2(2f, -2f);

        // Tight panel: grid uses a fixed 4x4 size; avoid excess empty chrome.
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(1020f, 798f), new Vector2(0f, -6f));

        MakeText(panelObj.transform, "SELECT LEVEL", 64, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.04f, 0.84f), new Vector2(0.96f, 0.98f), true);

        // Unity: never use AddComponent<RectTransform>() on a plain GameObject — it is invalid.
        // Grid must be created with a RectTransform so GridLayoutGroup lays out all 16 cells.
        GameObject gridObj = new GameObject("Grid", typeof(RectTransform));
        gridObj.transform.SetParent(panelObj.transform, false);
        RectTransform gridRT = gridObj.GetComponent<RectTransform>();
        gridRT.anchorMin = new Vector2(0.5f, 0.5f);
        gridRT.anchorMax = new Vector2(0.5f, 0.5f);
        gridRT.pivot = new Vector2(0.5f, 0.5f);
        // 4 * 160 + 3 * 18 + 40 padding = 734; 4 * 130 + 3 * 18 + 40 = 614
        gridRT.sizeDelta = new Vector2(736f, 614f);
        gridRT.anchoredPosition = new Vector2(0f, -20f);

        GridLayoutGroup grid = gridObj.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(160f, 130f);
        grid.spacing = new Vector2(18f, 18f);
        grid.padding = new RectOffset(20, 20, 20, 20);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 4;
        grid.childAlignment = TextAnchor.MiddleCenter;
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;

        System.Collections.Generic.List<Selectable> tileSelectables =
            new System.Collections.Generic.List<Selectable>(GameManager.TotalLevels + 1);

        int unlockedLevels = GameManager.Instance != null ? GameManager.Instance.GetUnlockedLevelCount() : 1;
        for (int i = 1; i <= GameManager.TotalLevels; i++)
        {
            int level = i;
            bool isUnlocked = level <= unlockedLevels;
            bool isCurrent = GameManager.Instance != null && level == GameManager.Instance.currentLevel;
            Button tile = MakeLevelTile(gridObj.transform, level, isUnlocked, isCurrent,
                () =>
                {
                    // Hide the level select grid BEFORE the loading screen
                    // takes over so it can never ghost-render through the
                    // semi-transparent loading overlay.
                    HideLevelSelectGrid(root);
                    GameManager.Instance?.StartRun(level);
                });
            if (tile != null) tileSelectables.Add(tile);
        }

        // Return button — also added to the keyboard nav set.
        Button returnBtn = MakePanelButton(panelObj.transform, "RETURN",
            new Vector2(0.395f, 0.006f), new Vector2(0.605f, 0.085f),
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
            }, 31f, false, true);
        if (returnBtn != null) tileSelectables.Add(returnBtn);

        MenuNavigationManager.AttachLinear(overlayObj, tileSelectables);
    }

    /// <summary>
    /// Hides the level select overlay and the main-menu buttons. Called the
    /// instant a level is picked so the grid never bleeds through the
    /// loading screen that fades in over the top of it.
    /// </summary>
    void HideLevelSelectGrid(Transform root)
    {
        Transform overlay = root.Find("LevelSelectOverlay");
        if (overlay != null) overlay.gameObject.SetActive(false);
        SetMainMenuElementsVisible(root, false);
    }

    void DestroyOverlay(Transform root, string overlayName)
    {
        if (root == null) return;
        Transform existing = root.Find(overlayName);
        if (existing != null) DestroyUiObject(existing.gameObject);
    }

    // ─── LEVEL TILE ─────────────────────────────────────────────────────────
    // Level number, weapon name from catalog (ASCII), status line LOCKED/CURRENT/CLEARED.
    // Locked tiles stay clickable — shake on click. No star Unicode (font-safe).
    Button MakeLevelTile(Transform parent, int level, bool isUnlocked, bool isCurrent,
        UnityEngine.Events.UnityAction action)
    {
        bool isCompleted = isUnlocked && !isCurrent &&
                           level < (GameManager.Instance != null ? GameManager.Instance.currentLevel : 1);

        Color cardFill = isCompleted ? new Color(0.10f, 0.18f, 0.13f, 0.98f)
                       : isCurrent   ? new Color(0.20f, 0.11f, 0.38f, 0.98f)
                       : isUnlocked  ? new Color(0.10f, 0.13f, 0.22f, 0.98f)
                                     : new Color(0.06f, 0.07f, 0.10f, 0.98f);

        Color borderCol = isCompleted ? new Color(0.32f, 0.82f, 0.42f, 0.72f)
                        : isCurrent   ? new Color(0.62f, 0.38f, 1.00f, 0.95f)
                        : isUnlocked  ? new Color(0.30f, 0.52f, 0.94f, 0.56f)
                                      : new Color(0.26f, 0.28f, 0.34f, 0.38f);

        GameObject obj = new GameObject("Level_" + level);
        obj.transform.SetParent(parent, false);

        Image img = obj.AddComponent<Image>();
        img.color = cardFill;

        Outline border = obj.AddComponent<Outline>();
        border.effectColor = borderCol;
        border.effectDistance = (isCurrent || isCompleted)
            ? new Vector2(3f, -3f)
            : new Vector2(2f, -2f);

        UICardHoverEffect hover = obj.AddComponent<UICardHoverEffect>();
        hover.glowOutline = border;
        hover.state = !isUnlocked ? UICardHoverEffect.CardState.Locked
                    : isCompleted ? UICardHoverEffect.CardState.Owned
                    : UICardHoverEffect.CardState.Normal;

        // Allow clicks on locked tiles so the shake fires
        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.interactable = true;
        ColorBlock cb = btn.colors;
        cb.normalColor = cb.highlightedColor = cb.selectedColor = new Color(0f, 0f, 0f, 0f);
        cb.pressedColor = new Color(1f, 1f, 1f, 0.08f);
        btn.colors = cb;
        if (isUnlocked)
            btn.onClick.AddListener(action);
        else
            btn.onClick.AddListener(() => UIAnimationHelper.Shake(obj.transform));

        float hueBar = (level * 37f % 360f) / 360f;

        GameObject accentTop = new GameObject("AccentTop");
        accentTop.transform.SetParent(obj.transform, false);
        Image accentTopImg = accentTop.AddComponent<Image>();
        accentTopImg.color = Color.HSVToRGB(hueBar, 0.40f, isUnlocked ? 0.62f : 0.26f);
        RectTransform accentTopRt = accentTop.GetComponent<RectTransform>();
        accentTopRt.anchorMin = new Vector2(0.04f, 0.88f);
        accentTopRt.anchorMax = new Vector2(0.96f, 0.97f);
        accentTopRt.offsetMin = accentTopRt.offsetMax = Vector2.zero;

        GameObject accentSide = new GameObject("AccentSide");
        accentSide.transform.SetParent(obj.transform, false);
        Image accentSideImg = accentSide.AddComponent<Image>();
        accentSideImg.color = new Color(0.35f, 0.75f, 1f, isUnlocked ? 0.34f : 0.11f);
        RectTransform accentSideRt = accentSide.GetComponent<RectTransform>();
        accentSideRt.anchorMin = new Vector2(0f, 0.07f);
        accentSideRt.anchorMax = new Vector2(0.028f, 0.84f);
        accentSideRt.offsetMin = accentSideRt.offsetMax = Vector2.zero;

        Color numCol = isUnlocked ? new Color(0.94f, 0.96f, 1f, 1f)
                                  : new Color(0.42f, 0.44f, 0.50f, 0.72f);
        TextMeshProUGUI lbl = CreateCenteredLabel(obj.transform, level.ToString(), 37, numCol, true);
        lbl.rectTransform.anchorMin = new Vector2(0.06f, 0.54f);
        lbl.rectTransform.anchorMax = new Vector2(0.94f, 0.82f);
        lbl.rectTransform.offsetMin = lbl.rectTransform.offsetMax = Vector2.zero;

        string weaponUpper = GetWeaponNameForUiTile(level).ToUpperInvariant();
        TextMeshProUGUI weaponLbl = CreateCenteredLabel(obj.transform, weaponUpper, 12,
            isUnlocked
                ? new Color(0.72f, 0.82f, 0.98f, 0.92f)
                : new Color(0.38f, 0.41f, 0.48f, 0.52f),
            false);
        weaponLbl.fontStyle = FontStyles.Bold;
        weaponLbl.textWrappingMode = TextWrappingModes.Normal;
        weaponLbl.overflowMode = TextOverflowModes.Ellipsis;
        weaponLbl.raycastTarget = false;
        weaponLbl.rectTransform.anchorMin = new Vector2(0.07f, 0.26f);
        weaponLbl.rectTransform.anchorMax = new Vector2(0.93f, 0.52f);
        weaponLbl.rectTransform.offsetMin = weaponLbl.rectTransform.offsetMax = Vector2.zero;

        string statusText = "";
        Color statusCol = Color.white;
        if (!isUnlocked) { statusText = "LOCKED"; statusCol = new Color(0.92f, 0.45f, 0.45f, 0.92f); }
        else if (isCurrent) { statusText = "CURRENT"; statusCol = new Color(0.45f, 0.95f, 1f, 0.95f); }
        else if (isCompleted) { statusText = "CLEARED"; statusCol = new Color(0.45f, 0.95f, 0.55f, 0.88f); }

        // Plain new GameObject() only has Transform — no RectTransform (throws MissingComponentException).
        GameObject statusGo = new GameObject("StatusLine", typeof(RectTransform));
        statusGo.transform.SetParent(obj.transform, false);
        RectTransform statusRt = statusGo.GetComponent<RectTransform>();
        if (statusRt != null)
        {
            statusRt.anchorMin = new Vector2(0.05f, 0.03f);
            statusRt.anchorMax = new Vector2(0.95f, 0.17f);
            statusRt.offsetMin = statusRt.offsetMax = Vector2.zero;
        }
        TextMeshProUGUI statusLbl = CreateCenteredLabel(statusGo.transform, statusText, 11, statusCol, true);
        statusLbl.raycastTarget = false;
        statusGo.SetActive(!string.IsNullOrEmpty(statusText));

        return btn;
    }

    // ─── SHARED HELPERS ───────────────────────────────────────────────────────────

    TextMeshProUGUI MakeText(Transform parent, string text, float size, Color color,
        Vector2 anchorMin, Vector2 anchorMax, bool isTitle = false)
    {
        GameObject obj = new GameObject("Text_" + text.Substring(0, Mathf.Min(text.Length, 5)));
        obj.transform.SetParent(parent, false);
        TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        if (customFont != null) tmp.font = customFont;

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        if (isTitle)
        {
            tmp.fontStyle = FontStyles.Bold;
            Outline outline = obj.AddComponent<Outline>();
            outline.effectColor = new Color(0.30f, 0.12f, 0.62f, 1f);
            outline.effectDistance = new Vector2(2f, -2f);
        }
        return tmp;
    }

    /// <param name="centerX">0–1 normalized X anchor for a width-sized pill (not full-row stretch).</param>
    Button MakeMenuButton(Transform parent, string label,
        Vector2 rowAnchorMin, Vector2 rowAnchorMax, float centerX,
        UnityEngine.Events.UnityAction action, float labelFontSize = 40f)
    {
        GameObject obj = new GameObject(label + "_Btn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0f);
        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(centerX, rowAnchorMin.y);
        rect.anchorMax = new Vector2(centerX, rowAnchorMax.y);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        TextMeshProUGUI labelText = CreateCenteredLabel(obj.transform, label, labelFontSize, new Color(0.95f, 0.95f, 1f, 1f), true);
        labelText.ForceMeshUpdate();
        float w = Mathf.Clamp(labelText.GetPreferredValues().x + 80f, 160f, 920f);
        rect.sizeDelta = new Vector2(w, 0f);

        AttachHoverEffect(obj, labelText, img,
            new Color(1f, 1f, 1f, 0f),
            new Color(0.18f, 0.42f, 0.92f, 0.32f),
            Color.white);
        return btn;
    }

    Transform CreateMainMenuPanel(Transform parent)
    {
        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(parent, false);

        RectTransform rect = panelObj.AddComponent<RectTransform>();
        // Tall band under titles: keep ALL 9 rows visible (including QUIT) across 16:9 / 16:10 / Free Aspect.
        rect.anchorMin = new Vector2(0.12f, 0.06f);
        rect.anchorMax = new Vector2(0.88f, 0.74f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // No background plate — layout only.
        VerticalLayoutGroup layout = panelObj.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 6f;
        layout.padding = new RectOffset(20, 20, 8, 8);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        return panelObj.transform;
    }

    /// <summary>
    /// Full-width row that centers a pill-sized button (hover panel hugs the label, not the screen).
    /// </summary>
    Button MakeCenteredPillMenuButton(Transform parent, string label,
        UnityEngine.Events.UnityAction action, float labelFontSize = 36f)
    {
        GameObject row = new GameObject(label + "_Row", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        HorizontalLayoutGroup h = row.AddComponent<HorizontalLayoutGroup>();
        h.childAlignment = TextAnchor.MiddleCenter;
        h.spacing = 0f;
        h.padding = new RectOffset(0, 0, 0, 0);
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = false;
        LayoutElement rowLe = row.AddComponent<LayoutElement>();
        rowLe.preferredHeight = 66f;
        rowLe.minHeight = 54f;
        rowLe.flexibleHeight = 0f;
        rowLe.flexibleWidth = 1f;

        return MakePillMenuButton(row.transform, label, action, labelFontSize);
    }

    Button MakePillMenuButton(Transform parent, string label,
        UnityEngine.Events.UnityAction action, float labelFontSize = 40f)
    {
        GameObject obj = new GameObject(label + "_Btn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;

        Image img = obj.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0f);

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);

        TextMeshProUGUI labelText = CreateCenteredLabel(obj.transform, label, labelFontSize, new Color(0.95f, 0.95f, 1f, 1f), true);
        labelText.textWrappingMode = TextWrappingModes.NoWrap;
        labelText.overflowMode = TextOverflowModes.Overflow;
        labelText.enableAutoSizing = false;
        labelText.ForceMeshUpdate();
        float textW = labelText.GetPreferredValues().x;
        float pillW = Mathf.Clamp(textW + 72f, 148f, 880f);

        LayoutElement element = obj.AddComponent<LayoutElement>();
        element.preferredWidth = pillW;
        element.preferredHeight = 66f;
        element.minHeight = 54f;
        element.flexibleWidth = 0f;
        element.flexibleHeight = 0f;

        AttachHoverEffect(obj, labelText, img,
            new Color(1f, 1f, 1f, 0f),
            new Color(0.18f, 0.42f, 0.92f, 0.32f),
            Color.white);
        return btn;
    }

    Button MakeStackMenuButton(Transform parent, string label,
        UnityEngine.Events.UnityAction action, float labelFontSize = 40f)
    {
        return MakeCenteredPillMenuButton(parent, label, action, labelFontSize);
    }

    Button MakePanelButton(Transform parent, string label,
        Vector2 anchorMin, Vector2 anchorMax,
        UnityEngine.Events.UnityAction action,
        float labelFontSize = 31f,
        bool staticMenuBlue = false,
        bool autoSizeSingleLine = false)
    {
        GameObject obj = new GameObject(label + "_Btn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = staticMenuBlue ? SettingsManager.MenuBlue : Color.white;
        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = staticMenuBlue
            ? new Color(0.90f, 0.94f, 1f, 0.55f)
            : new Color(0.20f, 0.24f, 0.38f, 0.30f);
        outline.effectDistance = new Vector2(2f, -2f);

        Color textCol = staticMenuBlue ? Color.white : new Color(0.05f, 0.08f, 0.32f, 1f);
        TextMeshProUGUI labelText = CreateCenteredLabel(obj.transform, label, labelFontSize, textCol, true);
        labelText.fontStyle = FontStyles.Bold;
        labelText.fontSize = labelFontSize;
        labelText.color = textCol;
        if (autoSizeSingleLine)
        {
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.enableAutoSizing = true;
            labelText.fontSizeMin = 16;
            labelText.fontSizeMax = Mathf.RoundToInt(labelFontSize);
            labelText.overflowMode = TextOverflowModes.Truncate;
        }

        if (staticMenuBlue)
        {
            AttachHoverEffect(obj, labelText, img,
                SettingsManager.MenuBlue,
                new Color(0.38f, 0.62f, 1f, 1f),
                Color.white);
        }
        else
        {
            AttachHoverEffect(obj, labelText, img,
                Color.white,
                new Color(0.98f, 0.98f, 1f, 1f),
                new Color(0.05f, 0.08f, 0.32f, 1f));
        }

        if (autoSizeSingleLine)
        {
            labelText.ForceMeshUpdate();
            float w = Mathf.Clamp(labelText.GetPreferredValues().x + 52f, 120f, 760f);
            float cx = (anchorMin.x + anchorMax.x) * 0.5f;
            rect.anchorMin = new Vector2(cx, anchorMin.y);
            rect.anchorMax = new Vector2(cx, anchorMax.y);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.sizeDelta = new Vector2(w, 0f);
        }
        return btn;
    }

    /// <summary>Multiplayer panel: two-tone fill, thin neon outline, hover scale + brighter rim (see MenuButtonHoverEffect).</summary>
    Button MakeMultiplayerNeonButton(Transform parent, string label,
        Vector2 anchorMin, Vector2 anchorMax,
        UnityEngine.Events.UnityAction action,
        float labelFontSize,
        bool autoSizeSingleLine,
        Color normalFill, Color hoverFill,
        Color topSheenRgb,
        Color normalOutline, Color hoverOutline)
    {
        GameObject obj = new GameObject(label + "_Btn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);

        Image img = obj.AddComponent<Image>();
        img.color = normalFill;

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        GameObject sheenObj = new GameObject("TopSheen", typeof(RectTransform));
        sheenObj.transform.SetParent(obj.transform, false);
        RectTransform sheenRt = sheenObj.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0.52f);
        sheenRt.anchorMax = new Vector2(1f, 1f);
        sheenRt.offsetMin = Vector2.zero;
        sheenRt.offsetMax = Vector2.zero;
        Image sheen = sheenObj.AddComponent<Image>();
        sheen.color = new Color(topSheenRgb.r, topSheenRgb.g, topSheenRgb.b, 0.26f);
        sheen.raycastTarget = false;

        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = normalOutline;
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);

        Color textCol = new Color(0.93f, 0.98f, 1f, 1f);
        TextMeshProUGUI labelText = CreateCenteredLabel(obj.transform, label, labelFontSize, textCol, true);
        labelText.fontStyle = FontStyles.Bold;
        if (autoSizeSingleLine)
        {
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.enableAutoSizing = true;
            labelText.fontSizeMin = 14;
            labelText.fontSizeMax = Mathf.RoundToInt(labelFontSize);
            labelText.overflowMode = TextOverflowModes.Truncate;
        }

        MenuButtonHoverEffect hover = obj.AddComponent<MenuButtonHoverEffect>();
        hover.label = labelText;
        hover.background = img;
        hover.normalTextColor = textCol;
        hover.hoverTextColor = new Color(1f, 1f, 1f, 1f);
        hover.normalBackgroundColor = normalFill;
        hover.hoverBackgroundColor = hoverFill;
        hover.hoverScale = new Vector3(1.05f, 1.05f, 1f);
        hover.normalScale = Vector3.one;
        hover.neonOutline = outline;
        hover.normalOutlineColor = normalOutline;
        hover.hoverOutlineColor = hoverOutline;

        if (autoSizeSingleLine)
        {
            labelText.ForceMeshUpdate();
            float w = Mathf.Clamp(labelText.GetPreferredValues().x + 56f, 120f, 820f);
            float cx = (anchorMin.x + anchorMax.x) * 0.5f;
            rect.anchorMin = new Vector2(cx, anchorMin.y);
            rect.anchorMax = new Vector2(cx, anchorMax.y);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.sizeDelta = new Vector2(w, 0f);
        }

        return btn;
    }

    TextMeshProUGUI CreateCenteredLabel(Transform parent, string text,
        float size, Color color, bool bold)
    {
        GameObject textObj = new GameObject("Label");
        textObj.transform.SetParent(parent, false);
        TextMeshProUGUI label = textObj.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        if (bold) label.fontStyle = FontStyles.Bold;
        if (customFont != null) label.font = customFont;
        label.raycastTarget = false;
        Stretch(label.GetComponent<RectTransform>());
        return label;
    }

    void AttachHoverEffect(GameObject target, TextMeshProUGUI label, Image image,
        Color normalBackground, Color hoverBackground, Color hoverTextColor)
    {
        MenuButtonHoverEffect hover = target.AddComponent<MenuButtonHoverEffect>();
        hover.label = label;
        hover.background = image;
        hover.normalTextColor = label.color;
        hover.hoverTextColor = hoverTextColor;
        hover.normalBackgroundColor = normalBackground;
        hover.hoverBackgroundColor = hoverBackground;
    }

    void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    void SetCenteredRect(RectTransform rect, Vector2 size, Vector2 anchoredPosition)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPosition;
    }

    void SetMainMenuElementsVisible(Transform root, bool isVisible)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            string childName = child.name;
            if (childName == "Background" || childName == "Overlay"
                || childName == "LevelSelectOverlay" || childName == "CustomMatchOverlay"
                || childName == "StoreOverlay" || childName == "ChallengesOverlay"
                || childName == "NameEntryOverlay")
                continue;

            child.gameObject.SetActive(isVisible);
        }
    }

    void QuitFromMainMenu()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ─── LOBBY MUSIC ──────────────────────────────────────────────────────────────
    private struct LobbyLoadCandidate
    {
        public string Url;
        public AudioType Type;
    }

    void SetupLobbyMusic()
    {
        AudioClip clip = lobbyMusicClip;
        if (clip == null)
            clip = Resources.Load<AudioClip>("MainMenu_LobbyTheme");

        if (clip != null)
        {
            PlayLobbyClip(clip);
            return;
        }

        if (!TryDispatchLobbyMusicFromDiskCoroutine())
        {
            Debug.LogWarning(
                "[RuntimeMenuBuilder] MainMenu_LobbyTheme: no clip. Assign lobbyMusicClip, add Resources/MainMenu_LobbyTheme (audio), "
                + "or place MainMenu_LobbyTheme (.ogg|.wav|.mp3) under StreamingAssets or Assets/Audio/.");
            AudioSettingsRuntime.RefreshMenuLobbyMusicIfPresent();
        }
    }

    bool TryDispatchLobbyMusicFromDiskCoroutine()
    {
        if (_lobbyMusicLoadRoutineActive)
            return true;

        LobbyLoadCandidate[] tries = BuildLobbyLoadCandidatesFromDiskOrStreaming();
        if (tries == null || tries.Length == 0)
            return false;

        StartCoroutine(LoadLobbyMusicCandidatesCoroutine(tries));
        return true;
    }

    LobbyLoadCandidate[] BuildLobbyLoadCandidatesFromDiskOrStreaming()
    {
        var list = new List<LobbyLoadCandidate>(8);

#if UNITY_ANDROID && !UNITY_EDITOR || UNITY_WEBGL && !UNITY_EDITOR
        AddStreamingUrlCandidates(list);
#else
        void AddFileIfPresent(string absolutePath, AudioType t)
        {
            if (!File.Exists(absolutePath)) return;
            list.Add(new LobbyLoadCandidate { Url = FileUrl(absolutePath), Type = t });
        }

        void AddVariantsInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            AddFileIfPresent(Path.Combine(folder, "MainMenu_LobbyTheme.ogg"), AudioType.OGGVORBIS);
            AddFileIfPresent(Path.Combine(folder, "MainMenu_LobbyTheme.wav"), AudioType.WAV);
            AddFileIfPresent(Path.Combine(folder, "MainMenu_LobbyTheme.mp3"), AudioType.MPEG);
            AddFileIfPresent(Path.Combine(folder, "MainMenu_LobbyTheme.mp4"), AudioType.MPEG);
        }

        AddVariantsInFolder(Path.Combine(Application.dataPath, "Audio"));
        AddVariantsInFolder(Application.streamingAssetsPath);
#endif
        return list.Count == 0 ? null : list.ToArray();
    }

#if UNITY_ANDROID && !UNITY_EDITOR || UNITY_WEBGL && !UNITY_EDITOR
    void AddStreamingUrlCandidates(List<LobbyLoadCandidate> list)
    {
        string streamingRoot = Application.streamingAssetsPath.TrimEnd('/').Replace("\\", "/");
        list.Add(new LobbyLoadCandidate { Url = $"{streamingRoot}/MainMenu_LobbyTheme.ogg", Type = AudioType.OGGVORBIS });
        list.Add(new LobbyLoadCandidate { Url = $"{streamingRoot}/MainMenu_LobbyTheme.wav", Type = AudioType.WAV });
        list.Add(new LobbyLoadCandidate { Url = $"{streamingRoot}/MainMenu_LobbyTheme.mp3", Type = AudioType.MPEG });
        list.Add(new LobbyLoadCandidate { Url = $"{streamingRoot}/MainMenu_LobbyTheme.mp4", Type = AudioType.MPEG });
    }
#endif

    static string FileUrl(string absolutePath)
    {
        return "file:///" + absolutePath.Replace("\\", "/");
    }

    IEnumerator LoadLobbyMusicCandidatesCoroutine(LobbyLoadCandidate[] tries)
    {
        _lobbyMusicLoadRoutineActive = true;

        for (int i = 0; i < tries.Length; i++)
        {
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(tries[i].Url, tries[i].Type))
            {
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                    continue;

                AudioClip loaded = DownloadHandlerAudioClip.GetContent(www);
                if (loaded == null)
                    continue;

                loaded.name = "MainMenu_LobbyTheme";
                PlayLobbyClip(loaded);
                _lobbyMusicLoadRoutineActive = false;
                yield break;
            }
        }

        _lobbyMusicLoadRoutineActive = false;
        Debug.LogWarning(
            "[RuntimeMenuBuilder] Could not load MainMenu_LobbyTheme from disk/StreamingAssets. "
            + "Add the audio file or assign lobbyMusicClip on RuntimeMenuBuilder in the Main Menu scene.");

        AudioSettingsRuntime.RefreshMenuLobbyMusicIfPresent();
    }

    void ApplyLobbyAudioSourceSettings(AudioClip clip)
    {
        lobbyAudioSource.clip = clip;
        lobbyAudioSource.loop = true;
        lobbyAudioSource.playOnAwake = false;
        lobbyAudioSource.mute = false;
        lobbyAudioSource.spatialBlend = 0f;
        lobbyAudioSource.ignoreListenerPause = false;
        lobbyAudioSource.volume = AudioSettingsRuntime.ScaledMusic(AudioSettingsRuntime.MenuLobbyMusicDesignMix);
    }

    void LogMenuMusicStarted(AudioClip clip)
    {
        float effectiveApprox = lobbyAudioSource != null ? lobbyAudioSource.volume * AudioListener.volume : 0f;
        Debug.Log(
            $"[RuntimeMenuBuilder] Menu music started: clip={clip.name}, sourceVolume={lobbyAudioSource.volume:F3}, "
            + $"musicPref={AudioSettingsRuntime.MusicVolume:F3}, masterListener={AudioListener.volume:F3}, effective~={effectiveApprox:F3}, loop=true");
    }

    void PlayLobbyClip(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogWarning("[RuntimeMenuBuilder] Menu music skipped: AudioClip reference is null.");
            return;
        }

        lobbyAudioSource = MenuMusicSceneGuard.PlayMenuTheme(clip);
        if (lobbyAudioSource != null && lobbyAudioSource.clip != null)
            LogMenuMusicStarted(lobbyAudioSource.clip);
    }

    void OnDestroy()
    {
        _lobbyMusicLoadRoutineActive = false;
    }

    public TMP_FontAsset ResolveMenuFont()
    {
        if (customFont != null)
            return customFont;

        // Try sleek/premium Azonix font first!
        TMP_FontAsset azonix = Resources.Load<TMP_FontAsset>("Fonts/Azonix SDF");
        if (azonix != null)
            return azonix;

        TMP_FontAsset lib = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (lib != null)
            return lib;

        TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        for (int i = 0; i < fonts.Length; i++)
        {
            TMP_FontAsset font = fonts[i];
            if (font != null && (font.name.Contains("Azonix") || font.name.Contains("Arizona")))
                return font;
        }

        if (TMP_Settings.defaultFontAsset != null)
            return TMP_Settings.defaultFontAsset;

        return null;
    }

    // ─── CINEMATIC 3D STAGE ───────────────────────────────────────────────────────
    // Builds a small 3D set behind the menu UI: a circular podium, a stylized
    // ronin silhouette holding a katana at the centre, drifting particles, a
    // colourful key/rim light, and a camera that orbits the centre slowly.
    // Everything is procedural so it works without any prefab dependencies.
    void BuildCinematicStage()
    {
        // Tear down any leftover stage from a previous menu reload.
        GameObject existing = GameObject.Find("CinematicStage");
        if (existing != null) DestroyUiObject(existing);

        GameObject stage = new GameObject("CinematicStage");

        // Camera — gets the orbit driver attached.
        GameObject camObj = new GameObject("CinematicCamera");
        camObj.transform.SetParent(stage.transform, false);
        Camera cam = camObj.AddComponent<Camera>();
        cam.clearFlags      = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.018f, 0.025f, 0.06f, 1f);
        cam.fieldOfView     = 38f;
        cam.nearClipPlane   = 0.05f;
        cam.farClipPlane    = 80f;
        cam.depth           = -10; // render behind UI overlay
        camObj.tag          = "MainCamera";

        // Lighting rig — warm key + cool rim for a "cinematic" key/fill look.
        GameObject keyObj = new GameObject("KeyLight");
        keyObj.transform.SetParent(stage.transform, false);
        keyObj.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
        Light key      = keyObj.AddComponent<Light>();
        key.type       = LightType.Directional;
        key.color      = new Color(1f, 0.92f, 0.78f, 1f);
        key.intensity  = 1.05f;

        GameObject rimObj = new GameObject("RimLight");
        rimObj.transform.SetParent(stage.transform, false);
        rimObj.transform.rotation = Quaternion.Euler(15f, 165f, 0f);
        Light rim      = rimObj.AddComponent<Light>();
        rim.type       = LightType.Directional;
        rim.color      = new Color(0.42f, 0.62f, 1f, 1f);
        rim.intensity  = 0.85f;

        // Podium — short cylinder under the character. Disable colliders so
        // the player input never hits stage geometry.
        GameObject podium = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        podium.name = "Podium";
        podium.transform.SetParent(stage.transform, false);
        podium.transform.localScale    = new Vector3(2.3f, 0.12f, 2.3f);
        podium.transform.localPosition = new Vector3(0f, -0.14f, 0f);
        ApplyMatColor(podium, new Color(0.18f, 0.22f, 0.32f, 1f), 0.45f, 0.85f);
        Collider podiumCol = podium.GetComponent<Collider>();
        if (podiumCol != null) DestroyUiObject(podiumCol);

        // Glowing inner ring on top of the podium.
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "PodiumRing";
        ring.transform.SetParent(stage.transform, false);
        ring.transform.localScale    = new Vector3(1.95f, 0.022f, 1.95f);
        ring.transform.localPosition = new Vector3(0f, -0.06f, 0f);
        ApplyMatColor(ring, new Color(0.30f, 0.55f, 1f, 1f), 0.0f, 0.0f, emissive: true);
        Collider ringCol = ring.GetComponent<Collider>();
        if (ringCol != null) DestroyUiObject(ringCol);

        // Procedural ronin silhouette + katana.
        BuildRoninSilhouette(stage.transform);

        // Floating particle field that drifts upward — adds the "alive" feel.
        GameObject particles = new GameObject("FloatingDust");
        particles.transform.SetParent(stage.transform, false);
        particles.transform.localPosition = new Vector3(0f, -1.2f, 0f);
        ParticleSystem ps = particles.AddComponent<ParticleSystem>();
        // Unity starts newly-created ParticleSystems immediately when
        // playOnAwake is true. Stop it before editing immutable runtime
        // properties like duration, then restart after every module is
        // configured. This prevents "Setting the duration while system is
        // still playing is not supported" in the console.
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var psr = particles.GetComponent<ParticleSystemRenderer>();
        if (psr != null)
        {
            // Use a built-in default sprite material when possible.
            Material partMat = new Material(Shader.Find("Sprites/Default"));
            partMat.color    = new Color(0.55f, 0.78f, 1f, 0.85f);
            psr.material     = partMat;
            psr.renderMode   = ParticleSystemRenderMode.Billboard;
        }
        var psMain = ps.main;
        psMain.playOnAwake            = false;
        psMain.duration               = 8f;
        psMain.loop                   = true;
        psMain.startLifetime          = 8.5f;
        psMain.startSpeed             = 0.18f;
        psMain.startSize              = new ParticleSystem.MinMaxCurve(0.025f, 0.06f);
        psMain.startColor             = new Color(0.55f, 0.78f, 1f, 0.65f);
        psMain.simulationSpace        = ParticleSystemSimulationSpace.World;
        psMain.maxParticles           = 220;

        var emission = ps.emission;
        emission.rateOverTime = 22f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale     = new Vector3(11f, 0.5f, 11f);

        var velOver = ps.velocityOverLifetime;
        velOver.enabled = true;
        // Unity requires X/Y/Z velocity curves to use the same MinMaxCurve
        // mode. Use TwoConstants for all axes (zero drift on X/Z, randomized
        // upward drift on Y) to avoid "Particle Velocity curves must all be
        // in the same mode".
        velOver.x       = new ParticleSystem.MinMaxCurve(0f, 0f);
        velOver.y       = new ParticleSystem.MinMaxCurve(0.05f, 0.22f);
        velOver.z       = new ParticleSystem.MinMaxCurve(0f, 0f);

        var sizeOver = ps.sizeOverLifetime;
        sizeOver.enabled = true;
        AnimationCurve sizeCurve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.2f, 1f),
            new Keyframe(0.8f, 1f),
            new Keyframe(1f, 0f));
        sizeOver.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        var colOver = ps.colorOverLifetime;
        colOver.enabled = true;
        Gradient grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.40f, 0.65f, 1f), 0f),
                new GradientColorKey(new Color(0.85f, 0.92f, 1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.85f, 0.25f),
                new GradientAlphaKey(0.55f, 0.7f),
                new GradientAlphaKey(0f, 1f)
            });
        colOver.color = grad;

        ps.Play(true);

        // Background pillars — three soft, dark columns to give parallax.
        for (int i = 0; i < 6; i++)
        {
            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pillar.name = "BgPillar_" + i;
            pillar.transform.SetParent(stage.transform, false);
            float angle = (360f / 6f) * i;
            float rad   = angle * Mathf.Deg2Rad;
            pillar.transform.localPosition = new Vector3(Mathf.Cos(rad) * 6.6f, 1.2f, Mathf.Sin(rad) * 6.6f);
            pillar.transform.localScale    = new Vector3(0.55f, 4.4f + (i % 2) * 0.6f, 0.55f);
            pillar.transform.localRotation = Quaternion.Euler(0f, -angle, 0f);
            ApplyMatColor(pillar, new Color(0.10f, 0.12f, 0.18f, 1f), 0.6f, 0.05f);
            Collider pc = pillar.GetComponent<Collider>();
            if (pc != null) DestroyUiObject(pc);
        }

        // Camera orbit driver — rotates around the centre slowly.
        var orbit = camObj.AddComponent<CinematicCameraOrbit>();
        orbit.target          = stage.transform;
        orbit.heightOffset    = 1.55f;
        orbit.radius          = 4.6f;
        orbit.degreesPerSecond = 7.5f;
    }

    /// <summary>
    /// Builds a stylized humanoid silhouette (head + torso + arms + legs) that
    /// holds a katana. Procedural — no prefab needed.
    /// </summary>
    void BuildRoninSilhouette(Transform parent)
    {
        // Materials for body and weapon.
        Material bodyMat = MakeMaterial(new Color(0.06f, 0.08f, 0.14f, 1f), 0.25f, 0.6f);
        Material trimMat = MakeMaterial(new Color(0.30f, 0.55f, 1f, 1f), 0.0f, 0.0f, emissive: true);
        Material bladeMat = MakeMaterial(new Color(0.85f, 0.92f, 1f, 1f), 1.0f, 0.05f, emissive: true);
        Material hiltMat  = MakeMaterial(new Color(0.18f, 0.10f, 0.06f, 1f), 0.3f, 0.6f);

        GameObject root = new GameObject("Ronin");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0f, 0f, 0f);

        // Torso (slightly tapered cube).
        GameObject torso = GameObject.CreatePrimitive(PrimitiveType.Cube);
        torso.transform.SetParent(root.transform, false);
        torso.transform.localPosition = new Vector3(0f, 0.95f, 0f);
        torso.transform.localScale    = new Vector3(0.55f, 0.85f, 0.32f);
        torso.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(torso.GetComponent<Collider>());

        // Chest accent.
        GameObject chestTrim = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chestTrim.transform.SetParent(root.transform, false);
        chestTrim.transform.localPosition = new Vector3(0f, 1.25f, 0.165f);
        chestTrim.transform.localScale    = new Vector3(0.40f, 0.07f, 0.02f);
        chestTrim.GetComponent<MeshRenderer>().sharedMaterial = trimMat;
        DestroyUiObject(chestTrim.GetComponent<Collider>());

        // Head.
        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0f, 1.65f, 0f);
        head.transform.localScale    = new Vector3(0.30f, 0.30f, 0.30f);
        head.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(head.GetComponent<Collider>());

        // Visor / glow band.
        GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visor.transform.SetParent(root.transform, false);
        visor.transform.localPosition = new Vector3(0f, 1.66f, 0.13f);
        visor.transform.localScale    = new Vector3(0.22f, 0.05f, 0.04f);
        visor.GetComponent<MeshRenderer>().sharedMaterial = trimMat;
        DestroyUiObject(visor.GetComponent<Collider>());

        // Arms — left rests at side, right is forward (sword hand).
        GameObject leftArm = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftArm.transform.SetParent(root.transform, false);
        leftArm.transform.localPosition = new Vector3(-0.42f, 0.95f, 0f);
        leftArm.transform.localScale    = new Vector3(0.16f, 0.78f, 0.18f);
        leftArm.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(leftArm.GetComponent<Collider>());

        GameObject rightArm = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightArm.transform.SetParent(root.transform, false);
        rightArm.transform.localPosition = new Vector3(0.40f, 1.15f, 0.18f);
        rightArm.transform.localRotation = Quaternion.Euler(60f, -10f, 0f);
        rightArm.transform.localScale    = new Vector3(0.16f, 0.78f, 0.18f);
        rightArm.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(rightArm.GetComponent<Collider>());

        // Legs.
        GameObject leftLeg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftLeg.transform.SetParent(root.transform, false);
        leftLeg.transform.localPosition = new Vector3(-0.16f, 0.30f, 0f);
        leftLeg.transform.localScale    = new Vector3(0.20f, 0.60f, 0.22f);
        leftLeg.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(leftLeg.GetComponent<Collider>());

        GameObject rightLeg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightLeg.transform.SetParent(root.transform, false);
        rightLeg.transform.localPosition = new Vector3(0.16f, 0.30f, 0f);
        rightLeg.transform.localScale    = new Vector3(0.20f, 0.60f, 0.22f);
        rightLeg.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        DestroyUiObject(rightLeg.GetComponent<Collider>());

        // Katana — held in the right hand, angled forward.
        GameObject katana = new GameObject("Katana");
        katana.transform.SetParent(root.transform, false);
        katana.transform.localPosition = new Vector3(0.55f, 1.80f, 0.55f);
        katana.transform.localRotation = Quaternion.Euler(15f, -25f, -30f);

        GameObject blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
        blade.transform.SetParent(katana.transform, false);
        blade.transform.localPosition = new Vector3(0f, 0.55f, 0f);
        blade.transform.localScale    = new Vector3(0.045f, 1.10f, 0.012f);
        blade.GetComponent<MeshRenderer>().sharedMaterial = bladeMat;
        DestroyUiObject(blade.GetComponent<Collider>());

        GameObject guard = GameObject.CreatePrimitive(PrimitiveType.Cube);
        guard.transform.SetParent(katana.transform, false);
        guard.transform.localPosition = new Vector3(0f, 0f, 0f);
        guard.transform.localScale    = new Vector3(0.16f, 0.04f, 0.08f);
        guard.GetComponent<MeshRenderer>().sharedMaterial = trimMat;
        DestroyUiObject(guard.GetComponent<Collider>());

        GameObject hilt = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hilt.transform.SetParent(katana.transform, false);
        hilt.transform.localPosition = new Vector3(0f, -0.18f, 0f);
        hilt.transform.localScale    = new Vector3(0.05f, 0.32f, 0.05f);
        hilt.GetComponent<MeshRenderer>().sharedMaterial = hiltMat;
        DestroyUiObject(hilt.GetComponent<Collider>());
    }

    /// <summary>Apply a fresh material with the given tint to a primitive.</summary>
    static void ApplyMatColor(GameObject obj, Color color, float smoothness, float metallic, bool emissive = false)
    {
        MeshRenderer mr = obj.GetComponent<MeshRenderer>();
        if (mr == null) return;
        mr.sharedMaterial = MakeMaterial(color, smoothness, metallic, emissive);
    }

    /// <summary>
    /// Builds a Material that works in both the URP "Lit" pipeline and the
    /// built-in Standard pipeline — set up for the cinematic stage props.
    /// </summary>
    static Material MakeMaterial(Color color, float smoothness, float metallic, bool emissive = false)
    {
        Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");
        Shader shader    = urpShader != null ? urpShader : Shader.Find("Standard");
        Material mat     = new Material(shader);

        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        else if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);

        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", metallic);

        if (emissive)
        {
            mat.EnableKeyword("_EMISSION");
            Color emissionColor = color * 1.6f;
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", emissionColor);
        }

        return mat;
    }

    // ─── CUSTOM MATCH OVERLAY ─────────────────────────────────────────────────────
    /// <summary>
    /// Toggles the Custom Match panel. Lets the player choose enemy count
    /// (1–25), match time (2 / 5 / 10 minutes) and difficulty (Easy / Normal /
    /// Veteran) before launching a custom run via <see cref="GameManager.StartCustomRun"/>.
    /// </summary>
    void ToggleCustomMatch(Transform root)
    {
        Transform existing = root.Find("CustomMatchOverlay");
        if (existing != null)
        {
            DestroyUiObject(existing.gameObject);
            SetMainMenuElementsVisible(root, true);
            return;
        }

        // Tear down any other overlay (e.g. Level Select) first.
        DestroyOverlay(root, "LevelSelectOverlay");
        SetMainMenuElementsVisible(root, false);

        // Default values — pulled from GameManager when one is already running.
        int defaultEnemies = GameManager.Instance != null ? GameManager.Instance.customEnemyCount : 12;
        int defaultMinutes = GameManager.Instance != null ? Mathf.Max(2, GameManager.Instance.customMatchTimeSeconds / 60) : 5;
        string defaultDiff = GameManager.Instance != null ? GameManager.Instance.customDifficulty : "Normal";
        defaultEnemies     = Mathf.Clamp(defaultEnemies, 1, 25);
        if (defaultMinutes != 2 && defaultMinutes != 5 && defaultMinutes != 10) defaultMinutes = 5;

        GameObject overlayObj = new GameObject("CustomMatchOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.10f);

        GameObject panelObj = new GameObject("CustomMatchPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        // Match Options/Settings panel vibe (shadowy slate).
        panel.color = new Color(0.16f, 0.20f, 0.30f, 0.68f);
        Outline panelOutline = panelObj.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.30f, 0.55f, 1f, 0.45f);
        panelOutline.effectDistance = new Vector2(2f, -2f);
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(960f, 700f), Vector2.zero);

        MakeText(panelObj.transform, "CUSTOM MATCH", 64, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.04f, 0.88f), new Vector2(0.96f, 0.98f), true);
        // Subheader (adds polish + explains).
        MakeText(panelObj.transform, "FAST SETUP — ENEMIES, TIME, DIFFICULTY + MAP", 22,
            new Color(0.72f, 0.84f, 1f, 0.88f),
            new Vector2(0.04f, 0.82f), new Vector2(0.96f, 0.88f), true);

        // ─── Enemy Count Slider ──────────────────────────────────────────────
        TextMeshProUGUI enemyValueLabel;
        Slider enemySlider = MakeIntSliderRow(panelObj.transform, "ENEMIES", 1, 25,
            defaultEnemies, 0.72f, out enemyValueLabel);

        // ─── Match Time Buttons (2 / 5 / 10) ─────────────────────────────────
        int[] minuteOptions  = new[] { 2, 5, 10 };
        int   selectedTimeIx = System.Array.IndexOf(minuteOptions, defaultMinutes);
        if (selectedTimeIx < 0) selectedTimeIx = 1;
        Button[] timeButtons = new Button[minuteOptions.Length];
        MakeText(panelObj.transform, "MATCH TIME", 30, new Color(0.85f, 0.88f, 0.96f, 1f),
            new Vector2(0.07f, 0.56f), new Vector2(0.40f, 0.62f), false);
        for (int i = 0; i < minuteOptions.Length; i++)
        {
            int idx = i;
            int minutes = minuteOptions[i];
            float xMin = 0.40f + idx * 0.18f;
            float xMax = xMin + 0.16f;
            Button btn = MakeChoiceButton(panelObj.transform, minutes + " MIN",
                new Vector2(xMin, 0.56f), new Vector2(xMax, 0.62f),
                () => SelectChoice(timeButtons, idx));
            timeButtons[i] = btn;
        }
        SelectChoice(timeButtons, selectedTimeIx);

        // ─── Difficulty Buttons (Easy / Normal / Veteran) ────────────────────
        string[] difficulties = new[] { "Easy", "Normal", "Veteran" };
        int selectedDiffIx    = System.Array.IndexOf(difficulties, defaultDiff);
        if (selectedDiffIx < 0) selectedDiffIx = 1;
        Button[] diffButtons  = new Button[difficulties.Length];
        MakeText(panelObj.transform, "DIFFICULTY", 30, new Color(0.85f, 0.88f, 0.96f, 1f),
            new Vector2(0.07f, 0.40f), new Vector2(0.40f, 0.46f), false);
        for (int i = 0; i < difficulties.Length; i++)
        {
            int idx = i;
            float xMin = 0.40f + idx * 0.18f;
            float xMax = xMin + 0.16f;
            Button btn = MakeChoiceButton(panelObj.transform, difficulties[i].ToUpperInvariant(),
                new Vector2(xMin, 0.40f), new Vector2(xMax, 0.46f),
                () => SelectChoice(diffButtons, idx));
            diffButtons[i] = btn;
        }
        SelectChoice(diffButtons, selectedDiffIx);

        MakeText(panelObj.transform, "Veteran enemies hit harder, soak more damage and chase faster.",
            22, new Color(0.66f, 0.74f, 0.92f, 0.9f),
            new Vector2(0.08f, 0.26f), new Vector2(0.92f, 0.33f), false);

        // ─── Map toggle (small but useful “feature”) ─────────────────────────
        MakeText(panelObj.transform, "MAP", 30, new Color(0.85f, 0.88f, 0.96f, 1f),
            new Vector2(0.07f, 0.32f), new Vector2(0.30f, 0.38f), false);
        string mapName = GameManager.Instance != null ? GameManager.Instance.GetSelectedMap().ToString().ToUpperInvariant() : "MAP1";
        TextMeshProUGUI mapLabel = MakeText(panelObj.transform, mapName, 28, new Color(0.05f, 0.08f, 0.32f, 1f),
            new Vector2(0.40f, 0.32f), new Vector2(0.56f, 0.38f), false);
        Button mapBtn = MakePanelButton(panelObj.transform, mapName,
            new Vector2(0.40f, 0.32f), new Vector2(0.56f, 0.38f),
            () =>
            {
                if (GameManager.Instance == null) return;
                GameManager.ArenaMap next = GameManager.Instance.GetSelectedMap() == GameManager.ArenaMap.Map1
                    ? GameManager.ArenaMap.Map2
                    : GameManager.ArenaMap.Map1;
                GameManager.Instance.SetSelectedMap(next);
                if (mapLabel != null) mapLabel.text = next.ToString().ToUpperInvariant();
            }, 28f, false, true);
        if (mapBtn != null && mapBtn.targetGraphic is Image mImg) mImg.color = Color.white;

        // Randomize button (quick fun).
        Button randomBtn = MakePanelButton(panelObj.transform, "RANDOMIZE",
            new Vector2(0.58f, 0.32f), new Vector2(0.78f, 0.38f),
            () =>
            {
                if (enemySlider != null) enemySlider.value = Random.Range(1, 26);
                SelectChoice(timeButtons, Random.Range(0, timeButtons.Length));
                SelectChoice(diffButtons, Random.Range(0, diffButtons.Length));
            }, 24f, false, true);

        Button startBtn = MakePanelButton(panelObj.transform, "START MATCH",
            new Vector2(0.22f, 0.07f), new Vector2(0.58f, 0.14f),
            () =>
            {
                int enemyCount = enemySlider != null ? Mathf.RoundToInt(enemySlider.value) : defaultEnemies;
                int tIx = FindSelectedIndex(timeButtons, selectedTimeIx);
                int dIx = FindSelectedIndex(diffButtons, selectedDiffIx);
                int chosenMin  = minuteOptions[Mathf.Clamp(tIx, 0, minuteOptions.Length - 1)];
                string diff    = difficulties[Mathf.Clamp(dIx, 0, difficulties.Length - 1)];
                if (GameManager.Instance != null)
                    GameManager.Instance.StartCustomRun(enemyCount, chosenMin * 60, diff);
            }, 28f, true, true);
        Button returnBtn = MakePanelButton(panelObj.transform, "RETURN",
            new Vector2(0.62f, 0.07f), new Vector2(0.78f, 0.14f),
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
            }, 26f, true, true);

        System.Collections.Generic.List<Selectable> nav =
            new System.Collections.Generic.List<Selectable>();
        if (enemySlider != null) nav.Add(enemySlider);
        for (int i = 0; i < timeButtons.Length; i++) if (timeButtons[i] != null) nav.Add(timeButtons[i]);
        for (int i = 0; i < diffButtons.Length; i++) if (diffButtons[i] != null) nav.Add(diffButtons[i]);
        if (startBtn  != null) nav.Add(startBtn);
        if (returnBtn != null) nav.Add(returnBtn);
        MenuNavigationManager.AttachLinear(overlayObj, nav);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  NAME ENTRY OVERLAY — first-run username prompt
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Forces the first-run name entry. Once the user types a valid name and
    /// presses CONFIRM (or Enter) the username is saved via PlayerProfile and
    /// the main menu is restored.
    /// </summary>
    void ShowNameEntryOverlay(Transform root)
    {
        if (root.Find("NameEntryOverlay") != null) return;

        // Hide everything else under the canvas while the prompt is up so the
        // player can't dodge entering a name.
        SetMainMenuElementsVisible(root, false);
        DestroyOverlay(root, "LevelSelectOverlay");
        DestroyOverlay(root, "CustomMatchOverlay");
        DestroyOverlay(root, "StoreOverlay");
        DestroyOverlay(root, "ChallengesOverlay");

        GameObject overlayObj = new GameObject("NameEntryOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.55f);

        GameObject panelObj = new GameObject("NameEntryPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        panel.color = new Color(0.10f, 0.13f, 0.22f, 0.92f);
        Outline panelOutline = panelObj.AddComponent<Outline>();
        panelOutline.effectColor    = new Color(0.30f, 0.55f, 1f, 0.55f);
        panelOutline.effectDistance = new Vector2(2f, -2f);
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(720f, 360f), Vector2.zero);

        MakeText(panelObj.transform, "WELCOME TO PRISM-7", 52, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.04f, 0.74f), new Vector2(0.96f, 0.96f), true);
        MakeText(panelObj.transform, "Enter the operative name shown to your enemies.", 22,
            new Color(0.66f, 0.74f, 0.92f, 0.95f),
            new Vector2(0.06f, 0.58f), new Vector2(0.94f, 0.70f), true);

        // ─── Input field ────────────────────────────────────────────────────
        GameObject inputObj = new GameObject("NameInput");
        inputObj.transform.SetParent(panelObj.transform, false);
        Image inputBg = inputObj.AddComponent<Image>();
        inputBg.color = new Color(0.06f, 0.08f, 0.13f, 1f);
        Outline inputOutline = inputObj.AddComponent<Outline>();
        inputOutline.effectColor    = new Color(0.30f, 0.55f, 1f, 0.45f);
        inputOutline.effectDistance = new Vector2(2f, -2f);
        RectTransform inputRT = inputObj.GetComponent<RectTransform>();
        inputRT.anchorMin = new Vector2(0.10f, 0.34f);
        inputRT.anchorMax = new Vector2(0.90f, 0.50f);
        inputRT.offsetMin = inputRT.offsetMax = Vector2.zero;

        TMP_InputField field = inputObj.AddComponent<TMP_InputField>();
        field.lineType        = TMP_InputField.LineType.SingleLine;
        field.characterLimit  = PlayerProfile.MaxNameLength;
        field.shouldHideMobileInput = false;

        GameObject textArea = new GameObject("Text Area");
        textArea.transform.SetParent(inputObj.transform, false);
        RectTransform taRT = textArea.AddComponent<RectTransform>();
        taRT.anchorMin = new Vector2(0f, 0f);
        taRT.anchorMax = new Vector2(1f, 1f);
        taRT.offsetMin = new Vector2(14f, 6f);
        taRT.offsetMax = new Vector2(-14f, -6f);
        textArea.AddComponent<RectMask2D>();

        GameObject placeholderObj = new GameObject("Placeholder");
        placeholderObj.transform.SetParent(textArea.transform, false);
        TextMeshProUGUI placeholder = placeholderObj.AddComponent<TextMeshProUGUI>();
        placeholder.text       = "TYPE NAME...";
        placeholder.fontSize   = 30;
        placeholder.color      = new Color(0.55f, 0.62f, 0.80f, 0.65f);
        placeholder.alignment  = TextAlignmentOptions.Left;
        Stretch(placeholder.rectTransform);

        GameObject textComp = new GameObject("Text");
        textComp.transform.SetParent(textArea.transform, false);
        TextMeshProUGUI textTMP = textComp.AddComponent<TextMeshProUGUI>();
        textTMP.fontSize  = 30;
        textTMP.color     = new Color(0.94f, 0.96f, 1f, 1f);
        textTMP.alignment = TextAlignmentOptions.Left;
        Stretch(textTMP.rectTransform);

        field.textViewport       = taRT;
        field.textComponent      = textTMP;
        field.placeholder        = placeholder;
        field.text               = string.Empty;

        // ─── Confirm / Skip buttons ────────────────────────────────────────
        TextMeshProUGUI hintLabel = MakeText(panelObj.transform,
            "Min " + PlayerProfile.MinNameLength + " characters, max " + PlayerProfile.MaxNameLength + ".",
            18, new Color(0.66f, 0.74f, 0.92f, 0.85f),
            new Vector2(0.06f, 0.22f), new Vector2(0.94f, 0.30f), true);

        Button confirmBtn = MakePanelButton(panelObj.transform, "CONFIRM",
            new Vector2(0.18f, 0.06f), new Vector2(0.46f, 0.18f),
            () =>
            {
                string sanitized = PlayerProfile.Sanitize(field.text);
                if (string.IsNullOrEmpty(sanitized))
                {
                    if (hintLabel != null)
                    {
                        hintLabel.text  = "Name too short — at least " + PlayerProfile.MinNameLength + " characters.";
                        hintLabel.color = new Color(1f, 0.45f, 0.45f, 1f);
                    }
                    return;
                }
                PlayerProfile.SetUsername(sanitized);
                DestroyUiObject(overlayObj);
                // Refresh the main menu so the profile header re-reads the
                // newly stored username.
                ClearAndRebuildMainMenu(root);
            });
        Button skipBtn = MakePanelButton(panelObj.transform, "SKIP",
            new Vector2(0.54f, 0.06f), new Vector2(0.82f, 0.18f),
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
            });

        System.Collections.Generic.List<Selectable> nav =
            new System.Collections.Generic.List<Selectable> { field, confirmBtn, skipBtn };
        MenuNavigationManager.AttachLinear(overlayObj, nav);

        EventSystem.current?.SetSelectedGameObject(field.gameObject);
        field.ActivateInputField();
    }

    void ShowNameEntryOverlayEnhanced(Transform root)
    {
        if (root.Find("NameEntryOverlay") != null) return;

        SetMainMenuElementsVisible(root, false);
        DestroyOverlay(root, "LevelSelectOverlay");
        DestroyOverlay(root, "CustomMatchOverlay");
        DestroyOverlay(root, "StoreOverlay");
        DestroyOverlay(root, "ChallengesOverlay");

        GameObject overlayObj = new GameObject("NameEntryOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.005f, 0.006f, 0.018f, 0.34f);

        GameObject panelObj = new GameObject("NameEntryPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        panel.color = new Color(0.015f, 0.025f, 0.070f, 0.84f);
        Outline panelOutline = panelObj.AddComponent<Outline>();
        panelOutline.effectColor = new Color(0.42f, 0.72f, 1f, 0.88f);
        panelOutline.effectDistance = new Vector2(3f, -3f);
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(1100f, 640f), new Vector2(0f, -6f));

        AddNameEntryChrome(panelObj.transform);

        TextMeshProUGUI welcome = MakeText(panelObj.transform, "WELCOME TO", 56, new Color(0.92f, 0.96f, 1f, 1f),
            new Vector2(0.10f, 0.77f), new Vector2(0.90f, 0.92f), true);
        welcome.characterSpacing = 8f;
        AddTextGlow(welcome.gameObject, new Color(0.18f, 0.55f, 1f, 0.85f), new Vector2(0f, -3f));

        TextMeshProUGUI title = MakeText(panelObj.transform, "PRISM-7", 82, new Color(0.98f, 0.99f, 1f, 1f),
            new Vector2(0.10f, 0.64f), new Vector2(0.90f, 0.80f), true);
        title.characterSpacing = 6f;
        AddTextGlow(title.gameObject, new Color(0.82f, 0.20f, 1f, 0.95f), new Vector2(0f, -4f));

        AddNameEntryWing(panelObj.transform, true);
        AddNameEntryWing(panelObj.transform, false);
        AddNameEntryDiamond(panelObj.transform);

        TextMeshProUGUI subtitle = MakeText(panelObj.transform,
            "ENTER THE OPERATIVE NAME SHOWN TO\nYOUR ENEMIES.",
            25, new Color(0.62f, 0.76f, 1f, 0.96f),
            new Vector2(0.12f, 0.47f), new Vector2(0.88f, 0.60f), true);
        subtitle.characterSpacing = 6f;

        GameObject inputObj = new GameObject("NameInput");
        inputObj.transform.SetParent(panelObj.transform, false);
        Image inputBg = inputObj.AddComponent<Image>();
        inputBg.color = new Color(0.010f, 0.018f, 0.036f, 0.96f);
        Outline inputOutline = inputObj.AddComponent<Outline>();
        inputOutline.effectColor = new Color(0.40f, 0.78f, 1f, 0.92f);
        inputOutline.effectDistance = new Vector2(3f, -3f);
        RectTransform inputRT = inputObj.GetComponent<RectTransform>();
        inputRT.anchorMin = new Vector2(0.13f, 0.31f);
        inputRT.anchorMax = new Vector2(0.87f, 0.48f);
        inputRT.offsetMin = inputRT.offsetMax = Vector2.zero;
        AddNameInputChrome(inputObj.transform);

        TMP_InputField field = inputObj.AddComponent<TMP_InputField>();
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.characterLimit = PlayerProfile.MaxNameLength;
        field.shouldHideMobileInput = false;

        GameObject textArea = new GameObject("Text Area");
        textArea.transform.SetParent(inputObj.transform, false);
        RectTransform taRT = textArea.AddComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(34f, 10f);
        taRT.offsetMax = new Vector2(-34f, -10f);
        textArea.AddComponent<RectMask2D>();

        GameObject placeholderObj = new GameObject("Placeholder");
        placeholderObj.transform.SetParent(textArea.transform, false);
        TextMeshProUGUI placeholder = placeholderObj.AddComponent<TextMeshProUGUI>();
        placeholder.text = "TYPE NAME...";
        placeholder.fontSize = 42;
        placeholder.color = new Color(0.60f, 0.70f, 0.92f, 0.72f);
        placeholder.alignment = TextAlignmentOptions.Left;
        placeholder.characterSpacing = 2f;
        if (customFont != null) placeholder.font = customFont;
        Stretch(placeholder.rectTransform);

        GameObject textComp = new GameObject("Text");
        textComp.transform.SetParent(textArea.transform, false);
        TextMeshProUGUI textTMP = textComp.AddComponent<TextMeshProUGUI>();
        textTMP.fontSize = 42;
        textTMP.color = new Color(0.94f, 0.96f, 1f, 1f);
        textTMP.alignment = TextAlignmentOptions.Left;
        textTMP.characterSpacing = 2f;
        if (customFont != null) textTMP.font = customFont;
        Stretch(textTMP.rectTransform);

        field.textViewport = taRT;
        field.textComponent = textTMP;
        field.placeholder = placeholder;
        field.text = string.Empty;

        TextMeshProUGUI hintLabel = MakeText(panelObj.transform,
            "MIN " + PlayerProfile.MinNameLength + " CHARACTERS, MAX " + PlayerProfile.MaxNameLength + ".",
            22, new Color(0.66f, 0.78f, 1f, 0.92f),
            new Vector2(0.08f, 0.20f), new Vector2(0.92f, 0.27f), true);
        hintLabel.characterSpacing = 5f;

        Button confirmBtn = MakeNeonActionButton(panelObj.transform, "CONFIRM", "V",
            new Vector2(0.16f, 0.065f), new Vector2(0.45f, 0.165f),
            new Color(0.20f, 0.70f, 1f, 1f), new Color(0.02f, 0.16f, 0.30f, 0.88f),
            () =>
            {
                string sanitized = PlayerProfile.Sanitize(field.text);
                if (string.IsNullOrEmpty(sanitized))
                {
                    if (hintLabel != null)
                    {
                        hintLabel.text = "NAME TOO SHORT - AT LEAST " + PlayerProfile.MinNameLength + " CHARACTERS.";
                        hintLabel.color = new Color(1f, 0.45f, 0.45f, 1f);
                    }
                    return;
                }
                PlayerProfile.SetUsername(sanitized);
                DestroyUiObject(overlayObj);
                ClearAndRebuildMainMenu(root);
            });

        Button skipBtn = MakeNeonActionButton(panelObj.transform, "SKIP", ">>",
            new Vector2(0.55f, 0.065f), new Vector2(0.84f, 0.165f),
            new Color(1f, 0.25f, 0.95f, 1f), new Color(0.20f, 0.02f, 0.26f, 0.86f),
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
            });

        System.Collections.Generic.List<Selectable> nav =
            new System.Collections.Generic.List<Selectable> { field, confirmBtn, skipBtn };
        MenuNavigationManager.AttachLinear(overlayObj, nav);

        EventSystem.current?.SetSelectedGameObject(field.gameObject);
        field.ActivateInputField();
    }

    void AddNameEntryChrome(Transform panel)
    {
        AddPanelGlow(panel, "OuterCyanGlow", new Vector2(1.012f, 1.020f), new Color(0.06f, 0.45f, 1f, 0.12f));
        AddPanelGlow(panel, "OuterMagentaGlow", new Vector2(1.032f, 1.045f), new Color(1f, 0.12f, 0.90f, 0.10f));
        AddNeonLine(panel, "TopRail", new Vector2(0.14f, 0.955f), new Vector2(0.86f, 0.963f), new Color(0.34f, 0.74f, 1f, 0.95f));
        AddNeonLine(panel, "TopRailHot", new Vector2(0.50f, 0.952f), new Vector2(0.86f, 0.960f), new Color(1f, 0.22f, 0.95f, 0.80f));
        AddNeonLine(panel, "BottomRail", new Vector2(0.18f, 0.036f), new Vector2(0.82f, 0.044f), new Color(0.30f, 0.66f, 1f, 0.60f));
        AddPanelCorner(panel, "TL", new Vector2(0f, 1f), new Vector2(1f, -1f), new Color(0.32f, 0.78f, 1f, 1f));
        AddPanelCorner(panel, "TR", new Vector2(1f, 1f), new Vector2(-1f, -1f), new Color(1f, 0.30f, 0.92f, 1f));
        AddPanelCorner(panel, "BL", new Vector2(0f, 0f), new Vector2(1f, 1f), new Color(0.24f, 0.64f, 1f, 0.85f));
        AddPanelCorner(panel, "BR", new Vector2(1f, 0f), new Vector2(-1f, 1f), new Color(1f, 0.28f, 0.88f, 0.85f));
    }

    void AddPanelGlow(Transform parent, string name, Vector2 scale, Color color)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = color;
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(1100f * scale.x, 640f * scale.y);
        rt.anchoredPosition = Vector2.zero;
        obj.transform.SetAsFirstSibling();
    }

    void AddPanelCorner(Transform parent, string name, Vector2 anchor, Vector2 dir, Color color)
    {
        AddCornerLine(parent, name + "_H", anchor, new Vector2(116f * dir.x, 0f), color);
        AddCornerLine(parent, name + "_V", anchor, new Vector2(0f, 88f * dir.y), color);
    }

    void AddCornerLine(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = color;
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.sizeDelta = new Vector2(Mathf.Abs(size.x) > 0f ? Mathf.Abs(size.x) : 4f, Mathf.Abs(size.y) > 0f ? Mathf.Abs(size.y) : 4f);
        rt.anchoredPosition = new Vector2(size.x < 0f ? -18f : 18f, size.y < 0f ? -18f : 18f);
    }

    void AddNeonLine(Transform parent, string name, Vector2 min, Vector2 max, Color color)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = color;
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    void AddTextGlow(GameObject textObject, Color color, Vector2 distance)
    {
        Outline glow = textObject.AddComponent<Outline>();
        glow.effectColor = color;
        glow.effectDistance = distance;
    }

    void AddNameEntryWing(Transform parent, bool left)
    {
        float x0 = left ? 0.145f : 0.855f;
        float sign = left ? 1f : -1f;
        Color color = left ? new Color(0.16f, 0.55f, 1f, 0.92f) : new Color(1f, 0.22f, 0.94f, 0.92f);
        for (int i = 0; i < 4; i++)
        {
            GameObject bar = new GameObject((left ? "Left" : "Right") + "Wing_" + i);
            bar.transform.SetParent(parent, false);
            Image img = bar.AddComponent<Image>();
            img.color = color;
            RectTransform rt = bar.GetComponent<RectTransform>();
            float y = 0.765f - i * 0.032f;
            float x1 = x0 + sign * (0.090f - i * 0.012f);
            rt.anchorMin = new Vector2(Mathf.Min(x0, x1), y);
            rt.anchorMax = new Vector2(Mathf.Max(x0, x1), y + 0.018f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }

    void AddNameEntryDiamond(Transform parent)
    {
        GameObject obj = new GameObject("PrismDiamond");
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = new Color(0.76f, 0.34f, 1f, 0.88f);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.605f);
        rt.anchorMax = new Vector2(0.5f, 0.605f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(30f, 30f);
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = new Color(0.30f, 0.72f, 1f, 0.95f);
        outline.effectDistance = new Vector2(3f, -3f);
    }

    void AddNameInputChrome(Transform input)
    {
        AddNeonLine(input, "InputHotEdge", new Vector2(0.56f, 0.00f), new Vector2(1.00f, 0.04f), new Color(1f, 0.20f, 0.94f, 0.85f));
        AddNeonLine(input, "InputCoolEdge", new Vector2(0.00f, 0.96f), new Vector2(0.42f, 1.00f), new Color(0.30f, 0.78f, 1f, 0.85f));
        AddNeonLine(input, "InputLeftCut", new Vector2(0.00f, 0.00f), new Vector2(0.020f, 1.00f), new Color(0.32f, 0.78f, 1f, 0.45f));
        AddNeonLine(input, "InputRightCut", new Vector2(0.980f, 0.00f), new Vector2(1.00f, 1.00f), new Color(1f, 0.24f, 0.92f, 0.45f));
    }

    Button MakeNeonActionButton(Transform parent, string label, string icon,
        Vector2 anchorMin, Vector2 anchorMax, Color accent, Color fill,
        UnityEngine.Events.UnityAction action)
    {
        GameObject obj = new GameObject(label + "_NeonBtn", typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = fill;
        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = accent;
        outline.effectDistance = new Vector2(3f, -3f);

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        AddNeonLine(obj.transform, "ButtonTop", new Vector2(0f, 0.92f), new Vector2(1f, 1f), accent);
        AddNeonLine(obj.transform, "ButtonBottom", new Vector2(0f, 0f), new Vector2(1f, 0.06f), new Color(accent.r, accent.g, accent.b, 0.48f));

        GameObject iconObj = new GameObject("IconPlate");
        iconObj.transform.SetParent(obj.transform, false);
        Image iconPlate = iconObj.AddComponent<Image>();
        iconPlate.color = accent;
        RectTransform iconRt = iconObj.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0.08f, 0.18f);
        iconRt.anchorMax = new Vector2(0.26f, 0.82f);
        iconRt.offsetMin = iconRt.offsetMax = Vector2.zero;
        TextMeshProUGUI iconLabel = CreateCenteredLabel(iconObj.transform, icon, 28, new Color(0.02f, 0.04f, 0.12f, 1f), true);
        iconLabel.textWrappingMode = TextWrappingModes.NoWrap;

        TextMeshProUGUI labelText = CreateCenteredLabel(obj.transform, label, 31, new Color(0.96f, 0.98f, 1f, 1f), true);
        labelText.rectTransform.anchorMin = new Vector2(0.26f, 0f);
        labelText.rectTransform.anchorMax = new Vector2(1f, 1f);
        labelText.characterSpacing = 3f;

        AttachHoverEffect(obj, labelText, img, fill,
            new Color(Mathf.Min(1f, fill.r + 0.10f), Mathf.Min(1f, fill.g + 0.12f), Mathf.Min(1f, fill.b + 0.16f), 0.96f),
            Color.white);
        return btn;
    }

    /// <summary>
    /// Tears down every direct child of <paramref name="root"/> except the
    /// background plate and rebuilds the main menu UI. Used after the name
    /// entry / store / challenges flow needs to refresh the live profile
    /// header values.
    /// </summary>
    void ClearAndRebuildMainMenu(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
            if (child.name == "Background" || child.name == "Overlay") continue;
            DestroyUiObject(child.gameObject);
        }
        BuildMainMenu(root);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PRISM STORE OVERLAY — buy / equip katana skins
    // ════════════════════════════════════════════════════════════════════════

    // Track the currently selected store tab so we can rebuild the right
    // column without recreating the panel chrome on every switch.
    private enum StoreTab { Weapons, Skins }
    private StoreTab _activeStoreTab = StoreTab.Weapons;

    /// <summary>
    /// Toggles the PRISM Store overlay. Two tabs:
    ///   • WEAPONS — buy / equip Nunchucks, Hammer, etc. (changes the 3D
    ///     mesh + combat stats applied at match start).
    ///   • SKINS   — re-tint the equipped weapon's material at runtime.
    /// Buying spends credits via SessionManager; the panel rebuilds the rows
    /// after every transaction so credit balance + status flags update live.
    /// </summary>
    void ToggleStore(Transform root)
    {
        Transform existing = root.Find("StoreOverlay");
        if (existing != null)
        {
            DestroyUiObject(existing.gameObject);
            SetMainMenuElementsVisible(root, true);
            return;
        }

        DestroyOverlay(root, "LevelSelectOverlay");
        DestroyOverlay(root, "CustomMatchOverlay");
        DestroyOverlay(root, "ChallengesOverlay");
        SetMainMenuElementsVisible(root, false);

        GameObject overlayObj = new GameObject("StoreOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.10f);

        GameObject panelObj = new GameObject("StorePanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        Outline panelOutline = panelObj.AddComponent<Outline>();
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(1180f, 800f), Vector2.zero);
        PrismOrganizedMenuChrome.ApplyPanelSurface(panel, panelOutline);

        // Title centered (matches the other panels).
        MakeText(panelObj.transform, "PRISM STORE", 64, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.04f, 0.88f), new Vector2(0.96f, 0.98f), true);

        // Live credits readout in the corner.
        TextMeshProUGUI creditsLabel = MakeText(panelObj.transform,
            "CREDITS: " + (SessionManager.Instance != null ? SessionManager.Instance.Credits.ToString("N0") : "0"),
            24, new Color(0.30f, 0.85f, 1f, 1f),
            new Vector2(0.70f, 0.805f), new Vector2(0.96f, 0.855f), false);
        creditsLabel.alignment = TextAlignmentOptions.Right;

        // ── Tabs ────────────────────────────────────────────────────────────
        Button weaponTabBtn = null;
        Button skinTabBtn   = null;
        weaponTabBtn = MakePanelButton(panelObj.transform, "WEAPONS",
            new Vector2(0.29f, 0.795f), new Vector2(0.485f, 0.865f),
            () =>
            {
                _activeStoreTab = StoreTab.Weapons;
                RebuildStoreContent(panelObj.transform, creditsLabel);
                HighlightTab(weaponTabBtn, skinTabBtn, true);
            });
        skinTabBtn = MakePanelButton(panelObj.transform, "SKINS",
            new Vector2(0.515f, 0.795f), new Vector2(0.71f, 0.865f),
            () =>
            {
                _activeStoreTab = StoreTab.Skins;
                RebuildStoreContent(panelObj.transform, creditsLabel);
                HighlightTab(weaponTabBtn, skinTabBtn, false);
            });
        HighlightTab(weaponTabBtn, skinTabBtn, _activeStoreTab == StoreTab.Weapons);

        GameObject sectionHeaderGo = new GameObject("StoreSectionHeader");
        sectionHeaderGo.transform.SetParent(panelObj.transform, false);
        TextMeshProUGUI sectionHeader = sectionHeaderGo.AddComponent<TextMeshProUGUI>();
        sectionHeader.text = _activeStoreTab == StoreTab.Weapons ? "WEAPONS" : "SKINS";
        sectionHeader.fontSize = 30f;
        sectionHeader.fontStyle = FontStyles.Bold;
        sectionHeader.alignment = TextAlignmentOptions.Center;
        sectionHeader.color = new Color(0.92f, 0.94f, 1f, 1f);
        if (customFont != null) sectionHeader.font = customFont;
        RectTransform shRT = sectionHeaderGo.GetComponent<RectTransform>();
        shRT.anchorMin = new Vector2(0.04f, 0.720f);
        shRT.anchorMax = new Vector2(0.96f, 0.765f);
        shRT.offsetMin = shRT.offsetMax = Vector2.zero;

        RebuildStoreContent(panelObj.transform, creditsLabel);

        RectTransform storeFooter = PrismOrganizedMenuChrome.CreateFooterRow(panelObj.transform, 64f, 14f, 10f);
        Button returnBtn = PrismOrganizedMenuChrome.AddFooterChipButton(
            storeFooter, "RETURN",
            new Color(0.12f, 0.20f, 0.42f, 0.92f), PrismOrganizedMenuChrome.ButtonOutlineBlue,
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
                ClearAndRebuildMainMenu(root);
            }, customFont);

        System.Collections.Generic.List<Selectable> nav =
            new System.Collections.Generic.List<Selectable> { weaponTabBtn, skinTabBtn };
        Button[] live = panelObj.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < live.Length; i++)
        {
            Button b = live[i];
            if (b == null || b == weaponTabBtn || b == skinTabBtn || b == returnBtn) continue;
            nav.Add(b);
        }
        if (returnBtn != null) nav.Add(returnBtn);
        MenuNavigationManager.AttachLinear(overlayObj, nav);
    }

    void HighlightTab(Button weaponsBtn, Button skinsBtn, bool weaponsActive)
    {
        if (weaponsBtn != null && weaponsBtn.targetGraphic is Image wImg)
            wImg.color = weaponsActive ? new Color(0.32f, 0.56f, 0.96f, 1f) : new Color(0.18f, 0.22f, 0.32f, 1f);
        if (skinsBtn != null && skinsBtn.targetGraphic is Image sImg)
            sImg.color = !weaponsActive ? new Color(0.32f, 0.56f, 0.96f, 1f) : new Color(0.18f, 0.22f, 0.32f, 1f);
    }

    /// <summary>
    /// Tears down the previously-built tab content (preview + rows) and
    /// rebuilds it for the currently active tab. Both tabs share the same
    /// layout — left preview pane + right scrollable rows — so users can
    /// switch instantly without panel chrome flicker.
    /// </summary>
    void RebuildStoreContent(Transform panel, TextMeshProUGUI creditsLabel)
    {
        for (int i = panel.childCount - 1; i >= 0; i--)
        {
            Transform child = panel.GetChild(i);
            if (child.name.StartsWith("StoreContent_"))
                DestroyUiObject(child.gameObject);
        }

        Transform hdr = panel.Find("StoreSectionHeader");
        if (hdr != null)
        {
            TextMeshProUGUI t = hdr.GetComponent<TextMeshProUGUI>();
            if (t != null)
                t.text = _activeStoreTab == StoreTab.Weapons ? "WEAPONS" : "SKINS";
        }

        GameObject contentRoot = new GameObject("StoreContent_" + _activeStoreTab);
        contentRoot.transform.SetParent(panel, false);
        RectTransform crRT = contentRoot.AddComponent<RectTransform>();
        crRT.anchorMin = new Vector2(0f, 0.165f);
        crRT.anchorMax = new Vector2(1f, 0.695f);
        crRT.offsetMin = crRT.offsetMax = Vector2.zero;

        // ─── Preview pane ────────────────────────────────────────────────
        GameObject preview = new GameObject("Preview");
        preview.transform.SetParent(contentRoot.transform, false);
        Image previewImg = preview.AddComponent<Image>();
        Outline previewOutline = preview.AddComponent<Outline>();
        previewOutline.effectColor    = new Color(0.30f, 0.55f, 1f, 0.55f);
        previewOutline.effectDistance = new Vector2(2f, -2f);
        RectTransform previewRT = preview.GetComponent<RectTransform>();
        previewRT.anchorMin = new Vector2(0.06f, 0.06f);
        previewRT.anchorMax = new Vector2(0.32f, 0.94f);
        previewRT.offsetMin = previewRT.offsetMax = Vector2.zero;

        TextMeshProUGUI previewName = MakeText(preview.transform, "", 28,
            new Color(0.94f, 0.96f, 1f, 1f),
            new Vector2(0f, 0.04f), new Vector2(1f, 0.20f), true);
        TextMeshProUGUI previewSubtitle = MakeText(preview.transform,
            _activeStoreTab == StoreTab.Skins ? "EQUIPPED SKIN" : "EQUIPPED WEAPON",
            18, new Color(0.66f, 0.74f, 0.92f, 0.95f),
            new Vector2(0f, 0.86f), new Vector2(1f, 0.96f), true);
        TextMeshProUGUI previewGlyph = MakeText(preview.transform, "",
            96, new Color(1f, 1f, 1f, 0.95f),
            new Vector2(0f, 0.32f), new Vector2(1f, 0.78f), true);

        // ─── Rows ────────────────────────────────────────────────────────
        if (_activeStoreTab == StoreTab.Weapons)
            BuildWeaponCards(contentRoot.transform, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);
        else
            BuildSkinCards(contentRoot.transform, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);

        if (hdr != null)
            hdr.SetAsLastSibling();
    }

    void BuildWeaponRows(Transform parent, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName, TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        TextMeshProUGUI[] statusLabels = new TextMeshProUGUI[SessionManager.Weapons.Length];

        const float topY    = 0.95f;
        const float rowH    = 0.118f;
        const float rowGap  = 0.008f;
        for (int i = 0; i < SessionManager.Weapons.Length; i++)
        {
            int idx = i;
            WeaponDefinition w = SessionManager.Weapons[i];
            float yMax = topY - i * (rowH + rowGap);
            float yMin = yMax - rowH;

            GameObject row = new GameObject("WeaponRow_" + w.Id);
            row.transform.SetParent(parent, false);
            Image rowImg = row.AddComponent<Image>();
            rowImg.color = new Color(0.16f, 0.20f, 0.30f, 0.95f);
            RectTransform rowRT = row.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0.38f, yMin);
            rowRT.anchorMax = new Vector2(0.94f, yMax);
            rowRT.offsetMin = rowRT.offsetMax = Vector2.zero;

            // Icon — coloured swatch with the weapon's glyph letter inside.
            GameObject icon = new GameObject("Icon");
            icon.transform.SetParent(row.transform, false);
            Image iconImg = icon.AddComponent<Image>();
            iconImg.color = w.Tint;
            RectTransform iconRT = icon.GetComponent<RectTransform>();
            iconRT.anchorMin = new Vector2(0.02f, 0.18f);
            iconRT.anchorMax = new Vector2(0.14f, 0.82f);
            iconRT.offsetMin = iconRT.offsetMax = Vector2.zero;
            MakeText(icon.transform, w.Glyph, 38, new Color(0f, 0f, 0f, 0.9f),
                new Vector2(0f, 0f), new Vector2(1f, 1f), true);

            MakeText(row.transform, w.Name, 23, new Color(0.94f, 0.96f, 1f, 1f),
                new Vector2(0.17f, 0.55f), new Vector2(0.66f, 0.95f), false);

            string statLine = "DMG ×" + w.DamageMul.ToString("0.00") + "   SPD ×" + w.AttackSpeedMul.ToString("0.00");
            MakeText(row.transform, statLine, 16, new Color(0.66f, 0.74f, 0.92f, 0.95f),
                new Vector2(0.17f, 0.18f), new Vector2(0.62f, 0.45f), false);

            string priceLabel = w.Price <= 0 ? "FREE" : w.Price.ToString("N0") + " CR";
            MakeText(row.transform, priceLabel, 19, new Color(0.30f, 0.85f, 1f, 1f),
                new Vector2(0.66f, 0.55f), new Vector2(0.84f, 0.95f), false);

            TextMeshProUGUI statusLabel = MakeText(row.transform, "",
                18, new Color(0.66f, 0.74f, 0.92f, 0.95f),
                new Vector2(0.66f, 0.05f), new Vector2(0.96f, 0.50f), false);
            statusLabels[idx] = statusLabel;

            Button rowBtn = row.AddComponent<Button>();
            rowBtn.targetGraphic = rowImg;
            ColorBlock cb = rowBtn.colors;
            cb.normalColor      = new Color(0.16f, 0.20f, 0.30f, 0.95f);
            cb.highlightedColor = new Color(0.32f, 0.56f, 0.96f, 0.95f);
            cb.pressedColor     = new Color(0.22f, 0.36f, 0.66f, 1f);
            cb.selectedColor    = new Color(0.32f, 0.56f, 0.96f, 0.95f);
            cb.disabledColor    = new Color(0.12f, 0.14f, 0.20f, 0.85f);
            rowBtn.colors = cb;

            rowBtn.onClick.AddListener(() =>
            {
                SessionManager s = SessionManager.Instance;
                if (s == null) return;
                if (!s.IsWeaponUnlocked(w.Id))
                {
                    if (s.TryBuyWeapon(w.Id))
                        s.EquipWeapon(w.Id);
                }
                else
                {
                    s.EquipWeapon(w.Id);
                }
                RefreshWeaponRows(statusLabels, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);
            });
        }

        RefreshWeaponRows(statusLabels, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);
    }

    void RefreshWeaponRows(TextMeshProUGUI[] statusLabels, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName, TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        SessionManager s = SessionManager.Instance;
        if (s == null) return;

        if (creditsLabel != null)
            creditsLabel.text = "CREDITS: " + s.Credits.ToString("N0");

        for (int i = 0; i < SessionManager.Weapons.Length && i < statusLabels.Length; i++)
        {
            WeaponDefinition w = SessionManager.Weapons[i];
            string label;
            Color color;
            if (s.EquippedWeaponId == w.Id)        { label = "EQUIPPED";              color = new Color(0.30f, 0.95f, 0.55f, 1f); }
            else if (s.IsWeaponUnlocked(w.Id))     { label = "OWNED — TAP TO EQUIP";  color = new Color(0.30f, 0.85f, 1f, 1f); }
            else if (s.Credits >= w.Price)         { label = "TAP TO BUY";            color = new Color(0.95f, 0.85f, 0.30f, 1f); }
            else                                    { label = "LOCKED";                color = new Color(0.90f, 0.45f, 0.45f, 1f); }
            if (statusLabels[i] != null)
            {
                statusLabels[i].text  = label;
                statusLabels[i].color = color;
            }
        }

        WeaponDefinition equipped = s.EquippedWeapon;
        if (previewImg != null) previewImg.color = equipped.Tint;
        if (previewName != null) previewName.text = equipped.Name.ToUpperInvariant();
        if (previewSubtitle != null) previewSubtitle.text = "EQUIPPED WEAPON";
        if (previewGlyph != null)
        {
            previewGlyph.text  = equipped.Glyph;
            previewGlyph.color = new Color(0f, 0f, 0f, 0.85f);
        }
    }

    void BuildSkinRows(Transform parent, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName, TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        TextMeshProUGUI[] statusLabels = new TextMeshProUGUI[SessionManager.Skins.Length];

        const float topY    = 0.95f;
        const float rowH    = 0.115f;
        const float rowGap  = 0.010f;
        for (int i = 0; i < SessionManager.Skins.Length; i++)
        {
            int idx = i;
            KatanaSkin skin = SessionManager.Skins[i];
            float yMax = topY - i * (rowH + rowGap);
            float yMin = yMax - rowH;

            GameObject row = new GameObject("SkinRow_" + skin.Id);
            row.transform.SetParent(parent, false);
            Image rowImg = row.AddComponent<Image>();
            rowImg.color = new Color(0.16f, 0.20f, 0.30f, 0.95f);
            RectTransform rowRT = row.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0.38f, yMin);
            rowRT.anchorMax = new Vector2(0.94f, yMax);
            rowRT.offsetMin = rowRT.offsetMax = Vector2.zero;

            GameObject swatch = new GameObject("Swatch");
            swatch.transform.SetParent(row.transform, false);
            Image swatchImg = swatch.AddComponent<Image>();
            swatchImg.color = skin.Color;
            RectTransform swatchRT = swatch.GetComponent<RectTransform>();
            swatchRT.anchorMin = new Vector2(0.02f, 0.20f);
            swatchRT.anchorMax = new Vector2(0.10f, 0.80f);
            swatchRT.offsetMin = swatchRT.offsetMax = Vector2.zero;

            MakeText(row.transform, skin.Name, 26, new Color(0.94f, 0.96f, 1f, 1f),
                new Vector2(0.13f, 0.45f), new Vector2(0.62f, 0.95f), false);

            string priceLabel = skin.Price <= 0 ? "FREE" : skin.Price.ToString("N0") + " CR";
            MakeText(row.transform, priceLabel, 22, new Color(0.30f, 0.85f, 1f, 1f),
                new Vector2(0.13f, 0.05f), new Vector2(0.45f, 0.45f), false);

            TextMeshProUGUI statusLabel = MakeText(row.transform, "",
                20, new Color(0.66f, 0.74f, 0.92f, 0.95f),
                new Vector2(0.45f, 0.05f), new Vector2(0.68f, 0.45f), false);
            statusLabels[idx] = statusLabel;

            Button rowBtn = row.AddComponent<Button>();
            rowBtn.targetGraphic = rowImg;
            ColorBlock cb = rowBtn.colors;
            cb.normalColor      = new Color(0.16f, 0.20f, 0.30f, 0.95f);
            cb.highlightedColor = new Color(0.32f, 0.56f, 0.96f, 0.95f);
            cb.pressedColor     = new Color(0.22f, 0.36f, 0.66f, 1f);
            cb.selectedColor    = new Color(0.32f, 0.56f, 0.96f, 0.95f);
            cb.disabledColor    = new Color(0.12f, 0.14f, 0.20f, 0.85f);
            rowBtn.colors = cb;

            rowBtn.onClick.AddListener(() =>
            {
                SessionManager s = SessionManager.Instance;
                if (s == null) return;
                if (!s.IsSkinUnlocked(skin.Id))
                {
                    if (s.TryBuySkin(skin.Id))
                        s.EquipSkin(skin.Id);
                }
                else
                {
                    s.EquipSkin(skin.Id);
                }
                RefreshSkinRows(statusLabels, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);
            });
        }

        RefreshSkinRows(statusLabels, creditsLabel, previewImg, previewName, previewSubtitle, previewGlyph);
    }

    void RefreshSkinRows(TextMeshProUGUI[] statusLabels, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName, TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        SessionManager s = SessionManager.Instance;
        if (s == null) return;

        if (creditsLabel != null)
            creditsLabel.text = "CREDITS: " + s.Credits.ToString("N0");

        for (int i = 0; i < SessionManager.Skins.Length && i < statusLabels.Length; i++)
        {
            KatanaSkin skin = SessionManager.Skins[i];
            string label;
            Color color;
            if (s.EquippedSkinId == skin.Id)         { label = "EQUIPPED";              color = new Color(0.30f, 0.95f, 0.55f, 1f); }
            else if (s.IsSkinUnlocked(skin.Id))      { label = "OWNED — TAP TO EQUIP";  color = new Color(0.30f, 0.85f, 1f, 1f); }
            else if (s.Credits >= skin.Price)        { label = "TAP TO BUY";            color = new Color(0.95f, 0.85f, 0.30f, 1f); }
            else                                      { label = "LOCKED";                color = new Color(0.90f, 0.45f, 0.45f, 1f); }
            if (statusLabels[i] != null)
            {
                statusLabels[i].text  = label;
                statusLabels[i].color = color;
            }
        }

        KatanaSkin equipped = s.FindSkin(s.EquippedSkinId) ?? SessionManager.Skins[0];
        if (previewImg != null) previewImg.color = equipped.Color;
        if (previewName != null) previewName.text = equipped.Name.ToUpperInvariant();
        if (previewSubtitle != null) previewSubtitle.text = "EQUIPPED SKIN";
        if (previewGlyph != null)
        {
            // Skin preview just uses the colour swatch (no glyph).
            previewGlyph.text = "";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  STORE — CARD-BASED GRID (AAA upgrade)
    //  Replaces the old flat-row layout with 2-column scrollable cards.
    //  Each card: colour-strip icon | name | stats | price | status badge.
    //  UICardHoverEffect drives glow + scale; state colour = equipped/locked/owned.
    // ════════════════════════════════════════════════════════════════════════

    void BuildWeaponCards(Transform parent, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName,
        TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        UICardHoverEffect[] effects = new UICardHoverEffect[SessionManager.Weapons.Length];
        TextMeshProUGUI[]   status  = new TextMeshProUGUI[SessionManager.Weapons.Length];
        Image[]             bgImgs  = new Image[SessionManager.Weapons.Length];
        GameObject[]        cards   = new GameObject[SessionManager.Weapons.Length];

        ScrollRect scroll = BuildCardScroll(parent, new Vector2(0.36f, 0f), new Vector2(1f, 1f));
        Transform grid = scroll.content;

        for (int i = 0; i < SessionManager.Weapons.Length; i++)
        {
            int idx = i;
            WeaponDefinition w = SessionManager.Weapons[i];

            GameObject card = new GameObject("WCard_" + w.Id);
            card.transform.SetParent(grid, false);
            cards[idx] = card;

            Image cardImg = card.AddComponent<Image>();
            cardImg.color = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            bgImgs[idx] = cardImg;

            Outline glow = card.AddComponent<Outline>();
            glow.effectColor = new Color(0.22f, 0.28f, 0.42f, 0.50f);
            glow.effectDistance = new Vector2(3f, -3f);

            UICardHoverEffect hover = card.AddComponent<UICardHoverEffect>();
            hover.glowOutline = glow;
            effects[idx] = hover;

            Button btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImg;
            ColorBlock cb = btn.colors;
            cb.normalColor = cb.highlightedColor = cb.selectedColor = new Color(0f, 0f, 0f, 0f);
            cb.pressedColor = new Color(1f, 1f, 1f, 0.08f);
            btn.colors = cb;

            // Left colour strip with glyph
            BuildCardStrip(card.transform, w.Tint, w.Glyph);

            // Name
            TextMeshProUGUI nameLbl = MakeCardLabel(card.transform, w.Name.ToUpperInvariant(),
                19, new Color(0.94f, 0.96f, 1f, 1f), TextAlignmentOptions.Left, true);
            SetCardRect(nameLbl.rectTransform, 0.13f, 0.56f, 0.74f, 0.96f);

            // Stats
            string stats = "DMG ×" + w.DamageMul.ToString("0.00") +
                           "  SPD ×" + w.AttackSpeedMul.ToString("0.00");
            TextMeshProUGUI statsLbl = MakeCardLabel(card.transform, stats,
                13, new Color(0.58f, 0.72f, 0.92f, 0.95f), TextAlignmentOptions.Left, false);
            SetCardRect(statsLbl.rectTransform, 0.13f, 0.28f, 0.74f, 0.54f);

            // Price
            string priceStr = w.Price <= 0 ? "FREE" : w.Price.ToString("N0") + " CR";
            TextMeshProUGUI priceLbl = MakeCardLabel(card.transform, priceStr,
                14, new Color(0.30f, 0.85f, 1f, 1f), TextAlignmentOptions.Left, false);
            SetCardRect(priceLbl.rectTransform, 0.13f, 0.05f, 0.55f, 0.26f);

            // Status badge (right side)
            TextMeshProUGUI statusLbl = MakeCardLabel(card.transform, "",
                13, Color.white, TextAlignmentOptions.Right, true);
            SetCardRect(statusLbl.rectTransform, 0.55f, 0.05f, 0.97f, 0.96f);
            status[idx] = statusLbl;

            btn.onClick.AddListener(() =>
            {
                SessionManager s = SessionManager.Instance;
                if (s == null) return;
                if (!s.IsWeaponUnlocked(w.Id)) { if (s.TryBuyWeapon(w.Id)) s.EquipWeapon(w.Id); }
                else                           { s.EquipWeapon(w.Id); }
                UIAnimationHelper.PunchScale(card.transform);
                RefreshWeaponCards(status, effects, bgImgs, creditsLabel,
                    previewImg, previewName, previewSubtitle, previewGlyph);
            });
        }

        RefreshWeaponCards(status, effects, bgImgs, creditsLabel,
            previewImg, previewName, previewSubtitle, previewGlyph);
    }

    void RefreshWeaponCards(TextMeshProUGUI[] status, UICardHoverEffect[] effects,
        Image[] bgImgs, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName,
        TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        SessionManager s = SessionManager.Instance;
        if (s == null) return;
        if (creditsLabel != null)
            creditsLabel.text = "CREDITS: " + s.Credits.ToString("N0");

        for (int i = 0; i < SessionManager.Weapons.Length && i < status.Length; i++)
        {
            WeaponDefinition w = SessionManager.Weapons[i];
            string badge; Color badgeCol; UICardHoverEffect.CardState state; Color bg;
            if (s.EquippedWeaponId == w.Id)
            {
                badge = "EQUIPPED"; badgeCol = new Color(0.30f, 0.95f, 0.55f, 1f);
                state = UICardHoverEffect.CardState.Equipped;
                bg = new Color(0.12f, 0.22f, 0.15f, 0.97f);
            }
            else if (s.IsWeaponUnlocked(w.Id))
            {
                badge = "OWNED"; badgeCol = new Color(0.30f, 0.85f, 1f, 1f);
                state = UICardHoverEffect.CardState.Owned;
                bg = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            }
            else if (s.Credits >= w.Price)
            {
                badge = "BUY"; badgeCol = new Color(0.95f, 0.85f, 0.30f, 1f);
                state = UICardHoverEffect.CardState.Normal;
                bg = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            }
            else
            {
                badge = "LOCKED"; badgeCol = new Color(0.90f, 0.45f, 0.45f, 1f);
                state = UICardHoverEffect.CardState.Locked;
                bg = new Color(0.09f, 0.10f, 0.14f, 0.97f);
            }
            if (status[i]  != null) { status[i].text  = badge; status[i].color = badgeCol; }
            if (effects[i] != null) effects[i].state   = state;
            if (bgImgs[i]  != null) bgImgs[i].color    = bg;
        }
        ApplyWeaponPreview(s.EquippedWeapon, previewImg, previewName, previewSubtitle, previewGlyph);
    }

    void ApplyWeaponPreview(WeaponDefinition w, Image img, TextMeshProUGUI name,
        TextMeshProUGUI subtitle, TextMeshProUGUI glyph)
    {
        if (img     != null) img.color     = w.Tint;
        if (name    != null) name.text     = w.Name.ToUpperInvariant();
        if (subtitle!= null) subtitle.text = "EQUIPPED WEAPON";
        if (glyph   != null) { glyph.text  = w.Glyph; glyph.color = new Color(0f, 0f, 0f, 0.85f); }
    }

    void BuildSkinCards(Transform parent, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName,
        TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        UICardHoverEffect[] effects = new UICardHoverEffect[SessionManager.Skins.Length];
        TextMeshProUGUI[]   status  = new TextMeshProUGUI[SessionManager.Skins.Length];
        Image[]             bgImgs  = new Image[SessionManager.Skins.Length];
        GameObject[]        cards   = new GameObject[SessionManager.Skins.Length];

        ScrollRect scroll = BuildCardScroll(parent, new Vector2(0.36f, 0f), new Vector2(1f, 1f));
        Transform grid = scroll.content;

        for (int i = 0; i < SessionManager.Skins.Length; i++)
        {
            int idx = i;
            KatanaSkin skin = SessionManager.Skins[i];

            GameObject card = new GameObject("SCard_" + skin.Id);
            card.transform.SetParent(grid, false);
            cards[idx] = card;

            Image cardImg = card.AddComponent<Image>();
            cardImg.color = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            bgImgs[idx] = cardImg;

            Outline glow = card.AddComponent<Outline>();
            glow.effectColor = new Color(0.22f, 0.28f, 0.42f, 0.50f);
            glow.effectDistance = new Vector2(3f, -3f);

            UICardHoverEffect hover = card.AddComponent<UICardHoverEffect>();
            hover.glowOutline = glow;
            effects[idx] = hover;

            Button btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImg;
            ColorBlock cb = btn.colors;
            cb.normalColor = cb.highlightedColor = cb.selectedColor = new Color(0f, 0f, 0f, 0f);
            cb.pressedColor = new Color(1f, 1f, 1f, 0.08f);
            btn.colors = cb;

            // Colour swatch strip (no glyph for skins)
            BuildCardStrip(card.transform, skin.Color, string.Empty);

            // Name
            TextMeshProUGUI nameLbl = MakeCardLabel(card.transform, skin.Name.ToUpperInvariant(),
                19, new Color(0.94f, 0.96f, 1f, 1f), TextAlignmentOptions.Left, true);
            SetCardRect(nameLbl.rectTransform, 0.13f, 0.52f, 0.80f, 0.95f);

            // Price
            string priceStr = skin.Price <= 0 ? "FREE" : skin.Price.ToString("N0") + " CR";
            TextMeshProUGUI priceLbl = MakeCardLabel(card.transform, priceStr,
                14, new Color(0.30f, 0.85f, 1f, 1f), TextAlignmentOptions.Left, false);
            SetCardRect(priceLbl.rectTransform, 0.13f, 0.06f, 0.55f, 0.38f);

            // Status badge
            TextMeshProUGUI statusLbl = MakeCardLabel(card.transform, "",
                13, Color.white, TextAlignmentOptions.Right, true);
            SetCardRect(statusLbl.rectTransform, 0.55f, 0.06f, 0.97f, 0.95f);
            status[idx] = statusLbl;

            btn.onClick.AddListener(() =>
            {
                SessionManager s = SessionManager.Instance;
                if (s == null) return;
                if (!s.IsSkinUnlocked(skin.Id)) { if (s.TryBuySkin(skin.Id)) s.EquipSkin(skin.Id); }
                else                            { s.EquipSkin(skin.Id); }
                UIAnimationHelper.PunchScale(card.transform);
                RefreshSkinCards(status, effects, bgImgs, creditsLabel,
                    previewImg, previewName, previewSubtitle, previewGlyph);
            });
        }

        RefreshSkinCards(status, effects, bgImgs, creditsLabel,
            previewImg, previewName, previewSubtitle, previewGlyph);
    }

    void RefreshSkinCards(TextMeshProUGUI[] status, UICardHoverEffect[] effects,
        Image[] bgImgs, TextMeshProUGUI creditsLabel,
        Image previewImg, TextMeshProUGUI previewName,
        TextMeshProUGUI previewSubtitle, TextMeshProUGUI previewGlyph)
    {
        SessionManager s = SessionManager.Instance;
        if (s == null) return;
        if (creditsLabel != null)
            creditsLabel.text = "CREDITS: " + s.Credits.ToString("N0");

        for (int i = 0; i < SessionManager.Skins.Length && i < status.Length; i++)
        {
            KatanaSkin skin = SessionManager.Skins[i];
            string badge; Color badgeCol; UICardHoverEffect.CardState state; Color bg;
            if (s.EquippedSkinId == skin.Id)
            {
                badge = "EQUIPPED"; badgeCol = new Color(0.30f, 0.95f, 0.55f, 1f);
                state = UICardHoverEffect.CardState.Equipped;
                bg = new Color(0.12f, 0.22f, 0.15f, 0.97f);
            }
            else if (s.IsSkinUnlocked(skin.Id))
            {
                badge = "OWNED"; badgeCol = new Color(0.30f, 0.85f, 1f, 1f);
                state = UICardHoverEffect.CardState.Owned;
                bg = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            }
            else if (s.Credits >= skin.Price)
            {
                badge = "BUY"; badgeCol = new Color(0.95f, 0.85f, 0.30f, 1f);
                state = UICardHoverEffect.CardState.Normal;
                bg = new Color(0.13f, 0.17f, 0.26f, 0.97f);
            }
            else
            {
                badge = "LOCKED"; badgeCol = new Color(0.90f, 0.45f, 0.45f, 1f);
                state = UICardHoverEffect.CardState.Locked;
                bg = new Color(0.09f, 0.10f, 0.14f, 0.97f);
            }
            if (status[i]  != null) { status[i].text  = badge; status[i].color = badgeCol; }
            if (effects[i] != null) effects[i].state   = state;
            if (bgImgs[i]  != null) bgImgs[i].color    = bg;
        }
        KatanaSkin eq = s.FindSkin(s.EquippedSkinId) ?? SessionManager.Skins[0];
        if (previewImg     != null) previewImg.color     = eq.Color;
        if (previewName    != null) previewName.text     = eq.Name.ToUpperInvariant();
        if (previewSubtitle!= null) previewSubtitle.text = "EQUIPPED SKIN";
        if (previewGlyph   != null) previewGlyph.text    = string.Empty;
    }

    // ── Card builder helpers ──────────────────────────────────────────────────

    ScrollRect BuildCardScroll(Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        GameObject scrollGo = new GameObject("CardScroll");
        scrollGo.transform.SetParent(parent, false);
        RectTransform scrollRT = scrollGo.AddComponent<RectTransform>();
        scrollRT.anchorMin = anchorMin;
        scrollRT.anchorMax = anchorMax;
        scrollRT.offsetMin = new Vector2(6f, 4f);
        scrollRT.offsetMax = new Vector2(-6f, -4f);

        ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 34f;

        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        RectTransform vpRT = viewport.AddComponent<RectTransform>();
        Stretch(vpRT);
        viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        viewport.AddComponent<RectMask2D>();

        GameObject gridGo = new GameObject("CardGrid");
        gridGo.transform.SetParent(viewport.transform, false);
        RectTransform gridRT = gridGo.AddComponent<RectTransform>();
        gridRT.anchorMin = new Vector2(0f, 1f);
        gridRT.anchorMax = new Vector2(1f, 1f);
        gridRT.pivot = new Vector2(0.5f, 1f);
        gridRT.anchoredPosition = Vector2.zero;
        gridRT.sizeDelta = Vector2.zero;

        GridLayoutGroup grid = gridGo.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(268f, 126f);
        grid.spacing = new Vector2(10f, 10f);
        grid.padding = new RectOffset(10, 10, 10, 10);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 2;
        grid.childAlignment = TextAnchor.UpperLeft;

        ContentSizeFitter csf = gridGo.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        scroll.viewport = vpRT;
        scroll.content = gridRT;
        return scroll;
    }

    void BuildCardStrip(Transform card, Color stripColor, string glyphText)
    {
        GameObject strip = new GameObject("Strip");
        strip.transform.SetParent(card, false);
        Image stripImg = strip.AddComponent<Image>();
        stripImg.color = stripColor;
        RectTransform rt = strip.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0.11f, 1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        if (!string.IsNullOrEmpty(glyphText))
        {
            TextMeshProUGUI g = MakeCardLabel(strip.transform, glyphText,
                28, new Color(0f, 0f, 0f, 0.85f), TextAlignmentOptions.Center, true);
            Stretch(g.rectTransform);
        }
    }

    TextMeshProUGUI MakeCardLabel(Transform parent, string text, float size,
        Color color, TextAlignmentOptions align, bool bold)
    {
        GameObject obj = new GameObject("CL_" + text.Substring(0, Mathf.Min(text.Length, 6)));
        obj.transform.SetParent(parent, false);
        TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;
        if (bold) tmp.fontStyle = FontStyles.Bold;
        if (customFont != null) tmp.font = customFont;
        return tmp;
    }

    void SetCardRect(RectTransform rt, float x0, float y0, float x1, float y1)
    {
        rt.anchorMin = new Vector2(x0, y0);
        rt.anchorMax = new Vector2(x1, y1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CHALLENGES OVERLAY — track lifetime objectives + bonuses
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Toggles the Challenges overlay. Each row shows the challenge title,
    /// progress, target and whether the lifetime bonus has been claimed.
    /// </summary>
    void ToggleChallenges(Transform root)
    {
        Transform existing = root.Find("ChallengesOverlay");
        if (existing != null)
        {
            DestroyUiObject(existing.gameObject);
            SetMainMenuElementsVisible(root, true);
            return;
        }

        DestroyOverlay(root, "LevelSelectOverlay");
        DestroyOverlay(root, "CustomMatchOverlay");
        DestroyOverlay(root, "StoreOverlay");
        SetMainMenuElementsVisible(root, false);

        GameObject overlayObj = new GameObject("ChallengesOverlay");
        overlayObj.transform.SetParent(root, false);
        Image overlay = overlayObj.AddComponent<Image>();
        Stretch(overlay.rectTransform);
        overlay.color = new Color(0.01f, 0.02f, 0.05f, 0.10f);

        GameObject panelObj = new GameObject("ChallengesPanel");
        panelObj.transform.SetParent(overlayObj.transform, false);
        Image panel = panelObj.AddComponent<Image>();
        Outline panelOutline = panelObj.AddComponent<Outline>();
        SetCenteredRect(panelObj.GetComponent<RectTransform>(), new Vector2(960f, 720f), Vector2.zero);
        PrismOrganizedMenuChrome.ApplyPanelSurface(panel, panelOutline);

        MakeText(panelObj.transform, "CHALLENGES", 64, new Color(0.94f, 0.94f, 1f, 1f),
            new Vector2(0.04f, 0.88f), new Vector2(0.96f, 0.98f), true);
        MakeText(panelObj.transform,
            "Earn +" + SessionManager.CreditsPerChallenge + " PRISM CREDITS per completed challenge.",
            22, new Color(0.66f, 0.74f, 0.92f, 0.95f),
            new Vector2(0.04f, 0.805f), new Vector2(0.96f, 0.865f), true);

        SessionManager session = SessionManager.Instance;

        GameObject scrollGo = new GameObject("ChallengesScroll");
        scrollGo.transform.SetParent(panelObj.transform, false);
        RectTransform scrollRT = scrollGo.AddComponent<RectTransform>();
        scrollRT.anchorMin = new Vector2(0.04f, 0.14f);
        scrollRT.anchorMax = new Vector2(0.96f, 0.76f);
        scrollRT.offsetMin = scrollRT.offsetMax = Vector2.zero;

        ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;

        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        RectTransform vpRT = viewport.AddComponent<RectTransform>();
        Stretch(vpRT);
        Image vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0.04f, 0.05f, 0.08f, 0.25f);
        viewport.AddComponent<RectMask2D>();

        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRT = content.AddComponent<RectTransform>();
        contentRT.anchorMin = new Vector2(0f, 1f);
        contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.pivot = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        contentRT.sizeDelta = new Vector2(0f, 0f);

        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 7f;
        vlg.padding = new RectOffset(2, 2, 2, 8);
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlHeight = true;
        vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;

        ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        scroll.viewport = vpRT;
        scroll.content = contentRT;

        for (int i = 0; i < SessionManager.Challenges.Length; i++)
        {
            ChallengeDefinition def = SessionManager.Challenges[i];

            GameObject row = new GameObject("ChallengeRow_" + def.Id);
            row.transform.SetParent(content.transform, false);
            Image rowImg = row.AddComponent<Image>();
            LayoutElement rowLayout = row.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 92f;
            rowLayout.minHeight = 92f;
            rowLayout.flexibleWidth = 1f;

            bool completed = session != null && session.IsChallengeCompleted(def.Id);
            rowImg.color = completed
                ? new Color(0.16f, 0.32f, 0.22f, 0.95f)
                : new Color(0.16f, 0.20f, 0.30f, 0.95f);

            RectTransform rowRT = row.GetComponent<RectTransform>();
            rowRT.localScale = Vector3.one;

            TextMeshProUGUI titleTmp = MakeText(row.transform, def.Title, 20, new Color(0.94f, 0.96f, 1f, 1f),
                new Vector2(0.04f, 0.38f), new Vector2(0.62f, 0.96f), false);
            titleTmp.textWrappingMode = TextWrappingModes.Normal;
            titleTmp.overflowMode = TextOverflowModes.Truncate;
            titleTmp.alignment = TextAlignmentOptions.TopLeft;

            int progress = session != null ? Mathf.Min(session.GetChallengeProgress(def.Id), def.Target) : 0;
            string progressText = completed ? "COMPLETE" : progress + " / " + def.Target;
            MakeText(row.transform, progressText, 22,
                completed ? new Color(0.30f, 0.95f, 0.55f, 1f) : new Color(0.30f, 0.85f, 1f, 1f),
                new Vector2(0.66f, 0.45f), new Vector2(0.96f, 0.95f), false);

            MakeText(row.transform, "+" + SessionManager.CreditsPerChallenge + " CREDITS", 18,
                new Color(0.85f, 0.88f, 0.96f, 0.85f),
                new Vector2(0.04f, 0.05f), new Vector2(0.65f, 0.40f), false);

            if (def.Target > 1 && !completed)
            {
                GameObject bar = new GameObject("Bar");
                bar.transform.SetParent(row.transform, false);
                Image barBg = bar.AddComponent<Image>();
                barBg.color = new Color(0.06f, 0.08f, 0.13f, 1f);
                RectTransform barRT = bar.GetComponent<RectTransform>();
                barRT.anchorMin = new Vector2(0.66f, 0.10f);
                barRT.anchorMax = new Vector2(0.96f, 0.30f);
                barRT.offsetMin = barRT.offsetMax = Vector2.zero;

                GameObject fill = new GameObject("Fill");
                fill.transform.SetParent(bar.transform, false);
                Image fillImg = fill.AddComponent<Image>();
                fillImg.color = new Color(0.30f, 0.55f, 1f, 1f);
                RectTransform fillRT = fill.GetComponent<RectTransform>();
                fillRT.anchorMin = new Vector2(0f, 0f);
                float pct = def.Target > 0 ? Mathf.Clamp01((float)progress / def.Target) : 0f;
                fillRT.anchorMax = new Vector2(pct, 1f);
                fillRT.offsetMin = fillRT.offsetMax = Vector2.zero;
            }
        }

        RectTransform chFooter = PrismOrganizedMenuChrome.CreateFooterRow(panelObj.transform, 64f, 14f, 36f);
        Button returnBtn = PrismOrganizedMenuChrome.AddFooterChipButton(
            chFooter, "RETURN",
            new Color(0.12f, 0.20f, 0.42f, 0.92f), PrismOrganizedMenuChrome.ButtonOutlineBlue,
            () =>
            {
                DestroyUiObject(overlayObj);
                SetMainMenuElementsVisible(root, true);
                ClearAndRebuildMainMenu(root);
            }, customFont);

        System.Collections.Generic.List<Selectable> nav =
            new System.Collections.Generic.List<Selectable> { returnBtn };
        MenuNavigationManager.AttachLinear(overlayObj, nav);
    }

    /// <summary>
    /// Builds a labeled integer slider row (label on the left, slider in the
    /// middle, current value on the right) anchored vertically at
    /// <paramref name="yAnchor"/> on the parent panel.
    /// </summary>
    Slider MakeIntSliderRow(Transform parent, string label, int min, int max, int initial,
        float yAnchor, out TextMeshProUGUI valueLabel)
    {
        // Label on the left.
        MakeText(parent, label, 30, new Color(0.85f, 0.88f, 0.96f, 1f),
            new Vector2(0.07f, yAnchor - 0.06f), new Vector2(0.30f, yAnchor), false);

        // Slider track.
        GameObject sliderObj = new GameObject("EnemySlider");
        sliderObj.transform.SetParent(parent, false);
        RectTransform sRect = sliderObj.AddComponent<RectTransform>();
        sRect.anchorMin = new Vector2(0.30f, yAnchor - 0.05f);
        sRect.anchorMax = new Vector2(0.86f, yAnchor - 0.005f);
        sRect.offsetMin = sRect.offsetMax = Vector2.zero;

        Image bg = sliderObj.AddComponent<Image>();
        bg.color = new Color(0.18f, 0.22f, 0.32f, 1f);

        Slider slider = sliderObj.AddComponent<Slider>();
        slider.minValue     = min;
        slider.maxValue     = max;
        slider.wholeNumbers = true;
        slider.value        = Mathf.Clamp(initial, min, max);

        // Fill area.
        GameObject fillArea = new GameObject("Fill Area");
        fillArea.transform.SetParent(sliderObj.transform, false);
        RectTransform faRT = fillArea.AddComponent<RectTransform>();
        faRT.anchorMin = new Vector2(0f, 0f);
        faRT.anchorMax = new Vector2(1f, 1f);
        faRT.offsetMin = new Vector2(8f, 4f);
        faRT.offsetMax = new Vector2(-8f, -4f);

        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(fillArea.transform, false);
        Image fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.32f, 0.56f, 0.96f, 1f);
        RectTransform fillRT = fill.GetComponent<RectTransform>();
        fillRT.anchorMin = new Vector2(0f, 0f);
        fillRT.anchorMax = new Vector2(1f, 1f);
        fillRT.offsetMin = fillRT.offsetMax = Vector2.zero;
        slider.fillRect = fillRT;

        // Handle.
        GameObject handleArea = new GameObject("Handle Slide Area");
        handleArea.transform.SetParent(sliderObj.transform, false);
        RectTransform haRT = handleArea.AddComponent<RectTransform>();
        haRT.anchorMin = new Vector2(0f, 0f);
        haRT.anchorMax = new Vector2(1f, 1f);
        haRT.offsetMin = new Vector2(10f, 0f);
        haRT.offsetMax = new Vector2(-10f, 0f);

        GameObject handle = new GameObject("Handle");
        handle.transform.SetParent(handleArea.transform, false);
        Image handleImg = handle.AddComponent<Image>();
        handleImg.color = new Color(0.92f, 0.95f, 1f, 1f);
        RectTransform handleRT = handle.GetComponent<RectTransform>();
        handleRT.sizeDelta = new Vector2(20f, 28f);
        slider.handleRect    = handleRT;
        slider.targetGraphic = handleImg;
        slider.direction     = Slider.Direction.LeftToRight;

        // Value label on the right.
        GameObject valueObj = new GameObject("ValueLabel");
        valueObj.transform.SetParent(parent, false);
        valueLabel = valueObj.AddComponent<TextMeshProUGUI>();
        valueLabel.font          = customFont;
        valueLabel.fontSize      = 32f;
        valueLabel.alignment     = TextAlignmentOptions.Center;
        valueLabel.color         = Color.white;
        valueLabel.fontStyle     = FontStyles.Bold;
        valueLabel.text          = slider.value.ToString();
        RectTransform valueRT    = valueObj.GetComponent<RectTransform>();
        valueRT.anchorMin = new Vector2(0.86f, yAnchor - 0.06f);
        valueRT.anchorMax = new Vector2(0.94f, yAnchor);
        valueRT.offsetMin = valueRT.offsetMax = Vector2.zero;

        TextMeshProUGUI capturedLabel = valueLabel;
        slider.onValueChanged.AddListener(v => capturedLabel.text = Mathf.RoundToInt(v).ToString());
        return slider;
    }

    /// <summary>
    /// Builds a small pill-shaped choice button used for the
    /// time/difficulty pickers in the Custom Match overlay.
    /// </summary>
    Button MakeChoiceButton(Transform parent, string label,
        Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction action)
    {
        GameObject obj = new GameObject(label + "_Choice", typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.color = SettingsManager.MenuBlue;

        Outline outline = obj.AddComponent<Outline>();
        outline.effectColor = new Color(0.30f, 0.55f, 1f, 0.40f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        Button btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(action);
        obj.AddComponent<MenuChoiceTag>();

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        TextMeshProUGUI lbl = CreateCenteredLabel(obj.transform, label, 28,
            new Color(0.92f, 0.95f, 1f, 1f), true);
        lbl.fontStyle = FontStyles.Bold;
        return btn;
    }

    /// <summary>
    /// Visually marks <paramref name="selectedIndex"/> as the active choice
    /// in the supplied button group. Selected = bright blue, others = dim.
    /// </summary>
    static void SelectChoice(Button[] buttons, int selectedIndex)
    {
        if (buttons == null) return;
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            MenuChoiceTag tag = b.GetComponent<MenuChoiceTag>();
            if (tag != null) tag.Selected = i == selectedIndex;

            Image img = b.targetGraphic as Image;
            if (img == null) continue;
            img.color = SettingsManager.MenuBlue;
            Outline o = b.GetComponent<Outline>();
            if (o == null) continue;
            if (i == selectedIndex)
            {
                o.effectColor = new Color(0.95f, 0.97f, 1f, 0.95f);
                o.effectDistance = new Vector2(3f, -3f);
            }
            else
            {
                o.effectColor = new Color(0.22f, 0.40f, 0.72f, 0.55f);
                o.effectDistance = new Vector2(1.5f, -1.5f);
            }
        }
    }

    /// <summary>
    /// Returns the currently visually selected button index (highlighted in
    /// blue) or <paramref name="fallback"/> if no buttons are visibly selected.
    /// </summary>
    static int FindSelectedIndex(Button[] buttons, int fallback)
    {
        if (buttons == null) return fallback;
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            MenuChoiceTag tag = b.GetComponent<MenuChoiceTag>();
            if (tag != null && tag.Selected) return i;
        }
        return fallback;
    }

}

/// <summary>Tracks which option is active in a <see cref="RuntimeMenuBuilder"/> choice-button group.</summary>
public sealed class MenuChoiceTag : MonoBehaviour
{
    public bool Selected;
}

// ─── CINEMATIC CAMERA ORBIT ───────────────────────────────────────────────────
// Rotates the camera around a target position at a constant angular speed,
// keeping the camera looking at the target. Used by the cinematic main menu
// so the player model and katana drift around the screen smoothly.
public class CinematicCameraOrbit : MonoBehaviour
{
    public Transform target;
    public float radius           = 4.5f;
    public float heightOffset     = 1.5f;
    public float degreesPerSecond = 8f;

    private float angleDeg;

    void Start()
    {
        // Start at a flattering 3/4 angle.
        angleDeg = 35f;
        UpdateTransform();
    }

    void Update()
    {
        angleDeg += degreesPerSecond * Time.deltaTime;
        if (angleDeg > 360f) angleDeg -= 360f;
        UpdateTransform();
    }

    void UpdateTransform()
    {
        Vector3 origin = target != null ? target.position : Vector3.zero;
        float rad      = angleDeg * Mathf.Deg2Rad;
        Vector3 pos    = origin + new Vector3(Mathf.Cos(rad) * radius, heightOffset, Mathf.Sin(rad) * radius);
        transform.position = pos;
        Vector3 lookAt = origin + new Vector3(0f, heightOffset * 0.6f, 0f);
        transform.LookAt(lookAt);
    }
}

public class BackgroundStreakAnimator : MonoBehaviour
{
    public float speed = 24f;
    public float verticalAmplitude = 16f;
    public float phase = 0f;

    private RectTransform rect;
    private Vector2 basePos;
    private Image image;

    void Awake()
    {
        rect = GetComponent<RectTransform>();
        image = GetComponent<Image>();
        if (rect != null) basePos = rect.anchoredPosition;
    }

    void Update()
    {
        if (rect == null) return;

        Vector2 pos = basePos;
        pos.x += Mathf.Repeat(Time.unscaledTime * speed + phase * 120f, 2200f) - 1100f;
        pos.y += Mathf.Sin(Time.unscaledTime * 0.9f + phase) * verticalAmplitude;
        rect.anchoredPosition = pos;

        if (image != null)
        {
            Color c = image.color;
            c.a = 0.05f + Mathf.Abs(Mathf.Sin(Time.unscaledTime * 1.4f + phase)) * 0.11f;
            image.color = c;
        }
    }
}

public class BackgroundPulseAnimator : MonoBehaviour
{
    public float minAlpha = 0.015f;
    public float maxAlpha = 0.055f;
    public float speed = 0.75f;

    private Image image;

    void Awake()
    {
        image = GetComponent<Image>();
    }

    void Update()
    {
        if (image == null) return;
        Color c = image.color;
        c.a = Mathf.Lerp(minAlpha, maxAlpha, (Mathf.Sin(Time.unscaledTime * speed) + 1f) * 0.5f);
        image.color = c;
    }
}
