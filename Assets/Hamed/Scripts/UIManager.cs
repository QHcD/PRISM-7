using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Central UI bootstrap: Input System–safe EventSystem, end-game menu, and menu keyboard navigation hooks.
/// </summary>
public class UIManager : MonoBehaviour
{
    private static UIManager _runtime;
    private static int _mainMenuApplyFramesRemaining;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsureRuntimeInstance()
    {
        if (_runtime != null) return;
        GameObject host = new GameObject("__UIManagerRuntime");
        DontDestroyOnLoad(host);
        _runtime = host.AddComponent<UIManager>();
    }

    private float _menuFailsafeTimer = 0f;

    private void Start()
    {
        if (SceneManager.GetActiveScene().name == "MainMenu")
        {
            Debug.Log("[MainMenuUI] Start on UIManager boot, invoking EnsureMainMenuUI.");
            EnsureMainMenuUI();
        }
    }

    private void Update()
    {
        if (!Application.isPlaying) return;

        if (SceneManager.GetActiveScene().name == "MainMenu")
        {
            if (GameObject.Find("NeonCanvas") == null)
            {
                _menuFailsafeTimer += Time.unscaledDeltaTime;
                if (_menuFailsafeTimer >= 1.0f)
                {
                    Debug.Log("[MainMenuUI] Failsafe triggered in Update: NeonCanvas not found for >1s. Rebuilding.");
                    EnsureMainMenuUI();
                    _menuFailsafeTimer = 0f;
                }
            }
            else
            {
                _menuFailsafeTimer = 0f;
            }
        }
        else
        {
            _menuFailsafeTimer = 0f;
        }

        if (!IsMainMenuLikeScene()) return;

        // Only a short stabilization window; do not keep reshuffling forever.
        if (_mainMenuApplyFramesRemaining > 0)
        {
            _mainMenuApplyFramesRemaining--;
            CompactifyMainMenuCanvas();
        }
    }

    private sealed class MainMenuCleanupRunner : MonoBehaviour
    {
        internal void Run()
        {
            StopAllCoroutines();
            StartCoroutine(Co());
        }

        private static IEnumerator Co()
        {
            // Menu is built in RuntimeMenuBuilder.Start(); wait a couple frames
            // so ProfileHeader definitely exists, then delete it + compact stack.
            yield return null;
            yield return null;

            EnsureMainMenuUI();

            CompactifyMainMenuCanvas();
            yield return null;
            CompactifyMainMenuCanvas(); // second pass for safety if menu rebuilt mid-frame
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapMainMenuCleanupHook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Application.isPlaying) return;
        // Gameplay / pause overlays need a consistent EventSystem in every scene.
        EnsureInputSystemEventSystem();

        if (scene.name == "MainMenu")
        {
            EnsureMainMenuUI();
        }

        if (!IsMainMenuLikeScene()) return;
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        CleanMainMenuMissingScripts();
        CompactifyMainMenuCanvas();
        _mainMenuApplyFramesRemaining = 60;
 
        // Delayed cleanup so it runs AFTER RuntimeMenuBuilder.Start() as well.
        GameObject host = GameObject.Find("__MainMenuCleanupRunner");
        if (host == null)
        {
            host = new GameObject("__MainMenuCleanupRunner");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
        }
        MainMenuCleanupRunner runner = host.GetComponent<MainMenuCleanupRunner>();
        if (runner == null) runner = host.AddComponent<MainMenuCleanupRunner>();
        runner.Run();
    }

    private static bool IsMainMenuLikeScene()
    {
        if (SceneManager.GetActiveScene().name == "MainMenu") return true;

        // Do not rely on a specific scene name; some projects rename it.
        GameObject canvas = GameObject.Find("NeonCanvas");
        if (canvas == null) return false;

        // Heuristic: main menu has the profile header and/or Continue/Start button.
        if (canvas.transform.Find("ProfileHeader") != null) return true;

        Button[] buttons = canvas.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            var t = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            string txt = t != null ? (t.text ?? string.Empty).Trim().ToUpperInvariant() : string.Empty;
            if (txt == "CONTINUE" || txt == "START") return true;
        }

        return false;
    }

    private static void EnsureMainMenuUI()
    {
        if (SceneManager.GetActiveScene().name != "MainMenu")
            return;

        Debug.Log("[MainMenuUI] Calling EnsureMainMenuUI");

        // 1) EventSystem check
        EventSystem eventSystem = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject es = new GameObject("EventSystem");
            eventSystem = es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
            Debug.Log("[MainMenuUI] EventSystem missing - created.");
        }
        else
        {
            Debug.Log("[MainMenuUI] EventSystem exists.");
        }

        // 2) RuntimeMenuBuilder check
        RuntimeMenuBuilder builder = UnityEngine.Object.FindFirstObjectByType<RuntimeMenuBuilder>();
        if (builder == null)
        {
            GameObject menuBuilderHost = new GameObject("RuntimeMenuBuilderHost");
            builder = menuBuilderHost.AddComponent<RuntimeMenuBuilder>();
            Debug.Log("[MainMenuUI] RuntimeMenuBuilder missing - created.");
        }
        else
        {
            Debug.Log("[MainMenuUI] RuntimeMenuBuilder exists.");
        }

        // 3) NeonCanvas check
        GameObject canvas = GameObject.Find("NeonCanvas");
        if (canvas == null)
        {
            Debug.Log("[MainMenuUI] NeonCanvas missing - rebuilding.");
            if (builder.customFont == null)
                builder.customFont = builder.ResolveMenuFont();
            builder.BuildCurrentScreen();
        }
        else
        {
            Debug.Log("[MainMenuUI] NeonCanvas exists.");
        }

        Debug.Log("[MainMenuUI] Main menu build complete.");
    }

    public void ShowGameFinishedMenu()
    {
        if (MultiplayerMode.IsMultiplayer)
        {
            Debug.Log("[EndUI] blocked stale/invalid outcome in multiplayer");
            return;
        }

        GameManager.MenuScreen outcome = GameManager.ResolveAuthoritativeOutcomeScreen();
        if (outcome == GameManager.MenuScreen.LevelComplete || outcome == GameManager.MenuScreen.Victory)
            Debug.Log("[EndUI] outcome = Victory reason = authoritative revalidation");
        else if (outcome == GameManager.MenuScreen.GameOver)
            Debug.Log("[EndUI] outcome = MissionFailed reason = authoritative revalidation");

        GameManager.SetPendingMenuScreen(outcome);

        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        EnsureInputSystemEventSystem();

        HUDManager hud = HUDManager.Instance;
        if (hud == null)
            hud = FindFirstObjectByType<HUDManager>();

        if (hud != null)
        {
            hud.ShowGameFinishedMenu();
            return;
        }

        Debug.LogWarning("[UIManager] No HUDManager found. Game is paused and cursor is unlocked, but no finish menu was shown.");
    }

    /// <summary>
    /// Ensures an <see cref="EventSystem"/> exists and uses <see cref="InputSystemUIInputModule"/> only
    /// (legacy <see cref="StandaloneInputModule"/> is removed to avoid InvalidOperationException).
    /// </summary>
    public static void EnsureInputSystemEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject eventSystemObject = new GameObject("EventSystem");
            eventSystem = eventSystemObject.AddComponent<EventSystem>();
        }

        GameObject go = eventSystem.gameObject;

        StandaloneInputModule legacy = go.GetComponent<StandaloneInputModule>();
        if (legacy != null)
            Object.Destroy(legacy);

        InputSystemUIInputModule uiModule = go.GetComponent<InputSystemUIInputModule>();
        if (uiModule == null)
            uiModule = go.AddComponent<InputSystemUIInputModule>();
        uiModule.enabled = true;

        go.SetActive(true);
    }

    /// <summary>Vertical list: Arrow Up/Down + Enter / Submit. Blue outline focus ring when applicable.</summary>
    public static MenuNavigationManager AttachLinearMenuNavigation(GameObject host, IList<Selectable> items)
        => MenuNavigationManager.AttachLinear(host, items);

    /// <summary>Main-menu 2D grid (Continue + pairs + column).</summary>
    public static MenuNavigationManager AttachMainMenuGrid(GameObject host, IList<Button> buttons)
        => MenuNavigationManager.AttachMainMenu(host, buttons);

    /// <summary>Forces session JSON + <see cref="PlayerPrefs"/> to disk (scene changes hook this automatically).</summary>
    public static void FlushSessionToDisk()
    {
        if (SessionManager.Instance != null)
            SessionManager.Instance.FlushPersistence();
        else
            PlayerPrefs.Save();
    }

    /// <summary>
    /// IMG_6880.mov final fix:
    /// • COMPLETELY destroys the top-left ProfileHeader panel
    /// • Rebuilds a compact zero-gap button stack in the requested order:
    ///   Continue → [Custom Match | Select Level] → [Prism Store | Challenges]
    ///   → Settings → Credits → Quit
    /// </summary>
    public static void CompactifyMainMenuCanvas()
    {
        GameObject canvas = GameObject.Find("NeonCanvas");
        if (canvas == null) return;

        // 1) Completely destroy the ProfileHeader — no visual remnant.
        Transform profile = canvas.transform.Find("ProfileHeader");
        if (profile != null)
            Object.Destroy(profile.gameObject);

        // If overlays are currently open, do not reshuffle under them.
        if (canvas.transform.Find("LevelSelectOverlay") != null) return;
        if (canvas.transform.Find("CustomMatchOverlay") != null) return;
        if (canvas.transform.Find("StoreOverlay") != null) return;
        if (canvas.transform.Find("ChallengesOverlay") != null) return;
        if (canvas.transform.Find("NameEntryOverlay") != null) return;

        // RuntimeMenuBuilder may own the whole layout under a Panel with a VLG.
        Transform menuPanel = canvas.transform.Find("Panel");
        if (menuPanel != null && menuPanel.GetComponent<VerticalLayoutGroup>() != null)
            return;

        // 2) Collect buttons (RuntimeMenuBuilder names them by label text).
        Button continueBtn     = FindButtonByLabel(canvas.transform, "CONTINUE") ?? FindButtonByLabel(canvas.transform, "START");
        Button selectLevelBtn  = FindButtonByLabel(canvas.transform, "SELECT LEVEL");
        Button storeBtn        = FindButtonByLabel(canvas.transform, "PRISM STORE");
        Button challengesBtn   = FindButtonByLabel(canvas.transform, "CHALLENGES");
        Button settingsBtn     = FindButtonByLabel(canvas.transform, "SETTINGS");
        Button creditsBtn      = FindButtonByLabel(canvas.transform, "CREDITS");
        Button quitBtn         = FindButtonByLabel(canvas.transform, "QUIT");

        // Core buttons required; Custom Match is optional (added by RuntimeMenuBuilder update).
        if (continueBtn == null || selectLevelBtn == null || storeBtn == null ||
            challengesBtn == null || settingsBtn == null ||
            creditsBtn == null || quitBtn == null)
            return;

        // Destroy previous stack if present.
        Transform existingStack = canvas.transform.Find("MainMenuButtonStack");
        if (existingStack != null) Object.Destroy(existingStack.gameObject);

        GameObject stackGo = new GameObject("MainMenuButtonStack", typeof(RectTransform));
        stackGo.transform.SetParent(canvas.transform, false);
        RectTransform stackRT = (RectTransform)stackGo.transform;
        stackRT.anchorMin = new Vector2(0.18f, 0.08f);
        stackRT.anchorMax = new Vector2(0.82f, 0.82f);
        stackRT.offsetMin = stackRT.offsetMax = Vector2.zero;

        VerticalLayoutGroup vlg = stackGo.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.spacing = 0f;                          // zero gap — buttons touch
        vlg.padding = new RectOffset(0, 0, 0, 0);
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        ContentSizeFitter fit = stackGo.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fit.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        // Row 0: Continue (full width)
        ReparentAndLayout(continueBtn, stackRT, preferredHeight: 88f, fullWidth: true);

        ReparentAndLayout(selectLevelBtn, stackRT, preferredHeight: 88f, fullWidth: true);

        // Row 2: Prism Store | Challenges
        Transform pair2 = CreatePairRow(stackRT, "Pair2_Row");
        ReparentAndLayout(storeBtn,      pair2, preferredHeight: 88f, fullWidth: false);
        CreatePairSeparator(pair2);
        ReparentAndLayout(challengesBtn, pair2, preferredHeight: 88f, fullWidth: false);

        // Rows 3-5: Settings, Credits, Quit (all full width, zero gap)
        ReparentAndLayout(settingsBtn, stackRT, preferredHeight: 88f, fullWidth: true);
        ReparentAndLayout(creditsBtn,  stackRT, preferredHeight: 88f, fullWidth: true);
        ReparentAndLayout(quitBtn,     stackRT, preferredHeight: 88f, fullWidth: true);

        // Hide stray duplicate buttons that are still parented outside the stack.
        DisableStrayMenuButtons(canvas.transform, stackRT);

        // 3) Keyboard navigation wired to the new stack order.
        MenuNavigationManager.AttachMainMenuCompact(canvas, stackRT);
    }

    private static void CleanMainMenuMissingScripts()
    {
        GameObject canvas = GameObject.Find("NeonCanvas");
        if (canvas == null)
            return;

        string[] staleNames =
        {
            "QUIT_BTN", "PRISM_STORE_BTN", "PRISM STORE_BTN",
            "START_BTN", "CONTINUE_BTN", "MULTIPLAYER_BTN"
        };

        Transform[] all = canvas.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null)
                continue;

            string normalized = (t.name ?? string.Empty).Trim().Replace(' ', '_').ToUpperInvariant();
            bool isStale = false;
            for (int s = 0; s < staleNames.Length; s++)
            {
                if (normalized == staleNames[s])
                {
                    isStale = true;
                    break;
                }
            }

            if (!isStale)
                continue;

            if (t.GetComponentInParent<Transform>() != null &&
                t.GetComponentInParent<Transform>().name == "MainMenuButtonStack")
                continue;

            if (t.parent != null && t.parent.name == "MainMenuButtonStack")
                continue;

            Button button = t.GetComponent<Button>();
            if (button == null)
                continue;

#if UNITY_EDITOR
            if (UnityEditor.GameObjectUtility.RemoveMonoBehavioursWithMissingScript(button.gameObject) > 0)
                continue;
#endif
            if (button.gameObject != canvas && button.transform.parent == canvas.transform)
                Object.Destroy(button.gameObject);
        }
    }

    private static Transform CreatePairRow(Transform parent, string name)
    {
        GameObject row = new GameObject(name, typeof(RectTransform));
        row.transform.SetParent(parent, false);

        HorizontalLayoutGroup hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.spacing = 0f;
        hlg.padding = new RectOffset(0, 0, 0, 0);
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;

        RectTransform rt = (RectTransform)row.transform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(0f, 88f);
        return row.transform;
    }

    private static void ReparentAndLayout(Button btn, Transform parent, float preferredHeight, bool fullWidth)
    {
        if (btn == null) return;
        RectTransform rt = btn.transform as RectTransform;
        if (rt == null) return;

        btn.transform.SetParent(parent, false);

        LayoutElement le = btn.GetComponent<LayoutElement>();
        if (le == null) le = btn.gameObject.AddComponent<LayoutElement>();
        le.minHeight       = preferredHeight;
        le.preferredHeight = preferredHeight;
        le.flexibleHeight  = 0f;

        // Width: in VerticalLayout full rows fill; in HorizontalLayout split evenly.
        le.minWidth        = 0f;
        le.preferredWidth  = 0f;
        le.flexibleWidth   = 1f;

        // Reset any previous “pill” sizing / anchors so layout groups fully control it.
        rt.localScale = Vector3.one;
        rt.pivot      = new Vector2(0.5f, 0.5f);
        rt.anchorMin  = new Vector2(0.5f, 0.5f);
        rt.anchorMax  = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }

    private static void CreatePairSeparator(Transform pairRow)
    {
        if (pairRow == null) return;
        GameObject sep = new GameObject("PairSeparator", typeof(RectTransform));
        sep.transform.SetParent(pairRow, false);
        LayoutElement le = sep.AddComponent<LayoutElement>();
        le.minWidth = 26f;
        le.preferredWidth = 26f;
        le.flexibleWidth = 0f;
        le.minHeight = 88f;
        le.preferredHeight = 88f;
        le.flexibleHeight = 0f;

        var tmp = sep.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.text = "|";
        tmp.fontSize = 44f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = new Color(0.92f, 0.94f, 1f, 0.65f);
        TMPro.TMP_FontAsset f = Resources.Load<TMPro.TMP_FontAsset>("Fonts/Azonix SDF");
        if (f != null) tmp.font = f;

        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static Button FindButtonByLabel(Transform root, string labelUpper)
    {
        if (root == null || string.IsNullOrEmpty(labelUpper)) return null;
        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            TMPro.TextMeshProUGUI t = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (t == null) continue;
            if (string.Equals(t.text?.Trim(), labelUpper, System.StringComparison.OrdinalIgnoreCase))
                return b;
        }
        return null;
    }

    // Legacy helper kept for external callers that may still reference it,
    // but CompactifyMainMenuCanvas now calls Object.Destroy on the panel directly.
    private static void StripProfilePanelVisuals(Transform profileHeader)
    {
        if (profileHeader != null)
            Object.Destroy(profileHeader.gameObject);
    }

    private static bool ContainsTMPText(Transform root)
    {
        if (root == null) return false;
        if (root.GetComponent<TMPro.TextMeshProUGUI>() != null) return true;
        for (int i = 0; i < root.childCount; i++)
            if (ContainsTMPText(root.GetChild(i))) return true;
        return false;
    }

    private static void DisableStrayMenuButtons(Transform canvasRoot, Transform stackRoot)
    {
        if (canvasRoot == null || stackRoot == null) return;
        Button[] buttons = canvasRoot.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            if (b.transform.IsChildOf(stackRoot)) continue;

            TMPro.TextMeshProUGUI t = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            string txt = t != null ? (t.text ?? string.Empty).Trim().ToUpperInvariant() : string.Empty;
            if (txt == "CONTINUE" || txt == "START" || txt == "CUSTOM MATCH" ||
                txt == "SELECT LEVEL" || txt == "PRISM STORE" || txt == "CHALLENGES" ||
                txt == "SETTINGS" || txt == "CREDITS" || txt == "QUIT")
            {
                b.gameObject.SetActive(false);
            }
        }
    }
}
