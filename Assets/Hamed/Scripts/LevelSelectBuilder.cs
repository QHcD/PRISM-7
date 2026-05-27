using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class LevelSelectBuilder : MonoBehaviour
{
    public Sprite prismBackground;
    public TMP_FontAsset prismFont;

    // Mirrors GameManager.LevelWeaponNames so this scene works without a live GameManager.
    private static readonly string[] LevelWeaponNamesFallback = {
        "Tactical Knife", "Razor Katana", "Shovel", "Baseball Bat", "Nunchucks",
        "Wrench", "Crowbar", "Hammer", "Axe", "Spear",
        "Nailed Plank", "Saw", "Sickle", "Morgenstern", "L3FTE",
        "Riot Shield"
    };

    // Tracks which map button is "selected" visually
    private Button _map1Btn;
    private Button _map2Btn;

    // Environment cards (Industrial vs SciFi). Independent of Map1/Map2 variant.
    private Button _envIndustrialBtn;
    private Button _envSciFiBtn;

    // Tracks which level button is selected
    private int _selectedLevel = -1;
    private Button[] _levelButtons;
    private Button _playBtn;
    private TextMeshProUGUI _selectionLabel;

    void Start()
    {
        prismFont = ResolvePrismFont();
        GameObject canvasObj = new GameObject("Canvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObj.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasObj.AddComponent<GraphicRaycaster>();

        // Background
        Image bg = new GameObject("Background").AddComponent<Image>();
        bg.transform.SetParent(canvasObj.transform, false);
        Stretch(bg.GetComponent<RectTransform>());
        bg.color = new Color(0.07f, 0.07f, 0.13f, 1f);
        if (prismBackground) { bg.sprite = prismBackground; bg.color = Color.white; }

        // ── Title ─────────────────────────────────────────────────────────────
        MakeText(canvasObj.transform, "SELECT LEVEL", 56, new Color(0.6f, 0.2f, 1f, 1f),
            new Vector2(0f, 0.92f), new Vector2(1f, 0.99f), true);

        // ── Environment Selection (Industrial / SciFi) ───────────────────────
        MakeText(canvasObj.transform, "SELECT MAP", 30, new Color(0.95f, 0.85f, 0.45f, 1f),
            new Vector2(0.15f, 0.86f), new Vector2(0.85f, 0.91f), true);

        Image envPanel = new GameObject("EnvironmentPanel").AddComponent<Image>();
        envPanel.transform.SetParent(canvasObj.transform, false);
        envPanel.color = new Color(0.08f, 0.10f, 0.20f, 0.75f);
        RectTransform epRect = envPanel.GetComponent<RectTransform>();
        epRect.anchorMin = new Vector2(0.15f, 0.76f);
        epRect.anchorMax = new Vector2(0.85f, 0.85f);
        epRect.offsetMin = epRect.offsetMax = Vector2.zero;

        _envIndustrialBtn = MakeMapButton(canvasObj.transform, "INDUSTRIAL  (Recommended)",
            new Vector2(0.17f, 0.77f), new Vector2(0.49f, 0.84f),
            () => SelectEnvironment(GameManager.ArenaEnvironment.Industrial));

        _envSciFiBtn = MakeMapButton(canvasObj.transform, "SCIFI ARENA",
            new Vector2(0.51f, 0.77f), new Vector2(0.83f, 0.84f),
            () => SelectEnvironment(GameManager.ArenaEnvironment.SciFi));

        RefreshEnvironmentHighlight();

        // ── Map Variant Panel (NukeTown / City) ──────────────────────────────
        MakeText(canvasObj.transform, "MAP VARIANT", 22, new Color(0.7f, 0.7f, 0.7f, 1f),
            new Vector2(0.15f, 0.715f), new Vector2(0.85f, 0.75f), false);

        Image mapPanel = new GameObject("MapPanel").AddComponent<Image>();
        mapPanel.transform.SetParent(canvasObj.transform, false);
        mapPanel.color = new Color(0.08f, 0.08f, 0.18f, 0.7f);
        RectTransform mpRect = mapPanel.GetComponent<RectTransform>();
        mpRect.anchorMin = new Vector2(0.15f, 0.65f);
        mpRect.anchorMax = new Vector2(0.85f, 0.72f);
        mpRect.offsetMin = mpRect.offsetMax = Vector2.zero;

        _map1Btn = MakeMapButton(canvasObj.transform, "MAP 1  (NukeTown)",
            new Vector2(0.17f, 0.66f), new Vector2(0.49f, 0.71f),
            () => SelectMap(GameManager.ArenaMap.Map1));

        _map2Btn = MakeMapButton(canvasObj.transform, "MAP 2  (City)",
            new Vector2(0.51f, 0.66f), new Vector2(0.83f, 0.71f),
            () => SelectMap(GameManager.ArenaMap.Map2));

        RefreshMapHighlight();

        // ── Level Grid Panel ──────────────────────────────────────────────────
        Image panel = new GameObject("Panel").AddComponent<Image>();
        panel.transform.SetParent(canvasObj.transform, false);
        panel.color = new Color(0.1f, 0.1f, 0.2f, 0.5f);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.15f, 0.21f);
        panelRect.anchorMax = new Vector2(0.85f, 0.64f);
        panelRect.offsetMin = panelRect.offsetMax = Vector2.zero;

        Outline outl = panel.gameObject.AddComponent<Outline>();
        outl.effectColor = new Color(0.6f, 0.2f, 1f, 1f);
        outl.effectDistance = new Vector2(3, -3);

        GridLayoutGroup grid = panel.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(176, 158);
        grid.spacing = new Vector2(20, 20);
        grid.padding = new RectOffset(34, 34, 26, 26);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 4;

        // 16 level buttons — clicking only SELECTS the level (does NOT start the game)
        _levelButtons = new Button[16];
        for (int i = 1; i <= 16; i++)
        {
            int lvl = i;
            _levelButtons[i - 1] = MakeLevelButton(panel.transform, lvl, () =>
            {
                if (lvl > GetUnlockedLevelCountSafe())
                {
                    if (_levelButtons[lvl - 1] != null)
                        UIAnimationHelper.Shake(_levelButtons[lvl - 1].transform);
                    return;
                }
                _selectedLevel = lvl;
                RefreshLevelHighlight();
                RefreshPlayButton();
            });
        }

        RefreshLevelHighlight();

        // ── Selection Summary Label ──────────────────────────────────────────
        _selectionLabel = MakeText(canvasObj.transform, "Select a level and map, then press PLAY",
            22, new Color(0.9f, 0.85f, 0.5f, 1f),
            new Vector2(0.22f, 0.135f), new Vector2(0.78f, 0.19f), false);

        // ── PLAY Button (disabled until level is selected) ───────────────────
        _playBtn = MakeButton(canvasObj.transform, "PLAY",
            new Vector2(0.35f, 0.045f), new Vector2(0.65f, 0.115f),
            OnPlayClicked,
            new Color(0.2f, 0.75f, 0.2f, 1f));  // green
        _playBtn.interactable = false;
        // Dim the button until ready
        _playBtn.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f, 1f);

        // ── Return Button ────────────────────────────────────────────────────
        MakeButton(canvasObj.transform, "RETURN",
            new Vector2(0.05f, 0.045f), new Vector2(0.22f, 0.115f),
            () => SceneManager.LoadScene("MainMenu"));
    }

    // ── Play clicked ─────────────────────────────────────────────────────────

    private void OnPlayClicked()
    {
        if (_selectedLevel < 1) return;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentLevel = _selectedLevel;
            GameManager.Instance.levelTime = 0;
        }
        SceneManager.LoadScene("GameScene");
    }

    // ── Level highlight ──────────────────────────────────────────────────────

    private void RefreshLevelHighlight()
    {
        Color fillUnlocked = new Color(0.10f, 0.13f, 0.22f, 0.98f);
        Color fillSelected = new Color(0.20f, 0.11f, 0.40f, 0.98f);
        Color fillLocked = new Color(0.06f, 0.07f, 0.10f, 0.98f);
        Color outlineUnlocked = new Color(0.30f, 0.55f, 0.95f, 0.55f);
        Color outlineSelected = new Color(0.62f, 0.38f, 1.00f, 0.95f);
        Color outlineLocked = new Color(0.28f, 0.30f, 0.36f, 0.35f);

        int unlockedMax = GetUnlockedLevelCountSafe();

        for (int i = 0; i < _levelButtons.Length; i++)
        {
            if (_levelButtons[i] == null) continue;
            int level = i + 1;
            bool unlocked = level <= unlockedMax;
            bool selected = level == _selectedLevel;

            Image img = _levelButtons[i].GetComponent<Image>();
            Outline o = _levelButtons[i].GetComponent<Outline>();
            UICardHoverEffect hover = _levelButtons[i].GetComponent<UICardHoverEffect>();

            TextMeshProUGUI numTmp = _levelButtons[i].transform.Find("LevelNum")?.GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI weaponTmp = _levelButtons[i].transform.Find("WeaponLine")?.GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI lockTmp = _levelButtons[i].transform.Find("LockLabel")?.GetComponent<TextMeshProUGUI>();

            if (hover != null)
                hover.state = unlocked ? UICardHoverEffect.CardState.Normal : UICardHoverEffect.CardState.Locked;

            if (!unlocked)
            {
                if (img != null) img.color = fillLocked;
                if (o != null)
                {
                    o.effectColor = outlineLocked;
                    o.effectDistance = new Vector2(2f, -2f);
                }
                if (numTmp != null) numTmp.color = new Color(0.42f, 0.44f, 0.50f, 0.75f);
                if (weaponTmp != null) weaponTmp.color = new Color(0.38f, 0.40f, 0.46f, 0.55f);
                if (lockTmp != null) { lockTmp.gameObject.SetActive(true); lockTmp.text = "LOCKED"; }
            }
            else if (selected)
            {
                if (img != null) img.color = fillSelected;
                if (o != null)
                {
                    o.effectColor = outlineSelected;
                    o.effectDistance = new Vector2(3f, -3f);
                }
                if (numTmp != null) numTmp.color = Color.white;
                if (weaponTmp != null) weaponTmp.color = new Color(0.82f, 0.90f, 1f, 0.95f);
                if (lockTmp != null) lockTmp.gameObject.SetActive(false);
            }
            else
            {
                if (img != null) img.color = fillUnlocked;
                if (o != null)
                {
                    o.effectColor = outlineUnlocked;
                    o.effectDistance = new Vector2(2f, -2f);
                }
                if (numTmp != null) numTmp.color = new Color(0.94f, 0.96f, 1f, 1f);
                if (weaponTmp != null) weaponTmp.color = new Color(0.70f, 0.78f, 0.96f, 0.88f);
                if (lockTmp != null) lockTmp.gameObject.SetActive(false);
            }
        }
    }

    private void RefreshPlayButton()
    {
        bool ready = _selectedLevel >= 1;
        _playBtn.interactable = ready;
        _playBtn.GetComponent<Image>().color = ready
            ? new Color(0.2f, 0.75f, 0.2f, 1f)
            : new Color(0.3f, 0.3f, 0.3f, 1f);

        string mapName = GameManager.Instance != null &&
                         GameManager.Instance.GetSelectedMap() == GameManager.ArenaMap.Map2
            ? "City" : "NukeTown";

        if (_selectionLabel != null)
        {
            _selectionLabel.text = ready
                ? $"Level {_selectedLevel}  |  {GetWeaponLabelForLevel(_selectedLevel)}  |  {mapName}  -  Press PLAY to start"
                : "Select a level and map, then press PLAY";
        }
    }

    // ── Map selection ─────────────────────────────────────────────────────────

    private void SelectMap(GameManager.ArenaMap map)
    {
        if (GameManager.Instance != null)
            GameManager.Instance.SetSelectedMap(map);
        RefreshMapHighlight();
        RefreshPlayButton();
    }

    private void RefreshMapHighlight()
    {
        GameManager.ArenaMap current = GameManager.Instance != null
            ? GameManager.Instance.GetSelectedMap()
            : GameManager.ArenaMap.Map1;

        Color selectedCol  = new Color(0.35f, 0.18f, 0.85f, 1f);
        Color defaultCol   = new Color(0.20f, 0.20f, 0.30f, 1f);

        if (_map1Btn != null)
            _map1Btn.GetComponent<Image>().color = (current == GameManager.ArenaMap.Map1) ? selectedCol : defaultCol;
        if (_map2Btn != null)
            _map2Btn.GetComponent<Image>().color = (current == GameManager.ArenaMap.Map2) ? selectedCol : defaultCol;
    }

    // ── Environment selection (Industrial vs SciFi) ───────────────────────────

    private void SelectEnvironment(GameManager.ArenaEnvironment env)
    {
        if (GameManager.Instance != null)
            GameManager.Instance.SetSelectedEnvironment(env);
        RefreshEnvironmentHighlight();
        RefreshPlayButton();
    }

    private void RefreshEnvironmentHighlight()
    {
        GameManager.ArenaEnvironment current = GameManager.Instance != null
            ? GameManager.Instance.GetSelectedEnvironment()
            : GameManager.ArenaEnvironment.Industrial;

        Color selectedCol = new Color(0.55f, 0.32f, 0.95f, 1f);
        Color defaultCol  = new Color(0.18f, 0.20f, 0.32f, 1f);

        if (_envIndustrialBtn != null)
            _envIndustrialBtn.GetComponent<Image>().color =
                (current == GameManager.ArenaEnvironment.Industrial) ? selectedCol : defaultCol;
        if (_envSciFiBtn != null)
            _envSciFiBtn.GetComponent<Image>().color =
                (current == GameManager.ArenaEnvironment.SciFi) ? selectedCol : defaultCol;
    }

    // ── Widget helpers ────────────────────────────────────────────────────────

    Button MakeMapButton(Transform parent, string label, Vector2 aMin, Vector2 aMax,
        UnityEngine.Events.UnityAction action)
    {
        var obj = new GameObject("MapBtn_" + label);
        obj.transform.SetParent(parent, false);
        var img = obj.AddComponent<Image>();
        img.color = new Color(0.20f, 0.20f, 0.30f, 1f);
        var btn = obj.AddComponent<Button>();
        btn.onClick.AddListener(action);

        var outline = obj.AddComponent<Outline>();
        outline.effectColor = new Color(0.6f, 0.2f, 1f, 0.6f);
        outline.effectDistance = new Vector2(2, -2);

        var txt = new GameObject("Txt");
        txt.transform.SetParent(obj.transform, false);
        var tmp = txt.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 26;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        if (prismFont) tmp.font = prismFont;
        Stretch(txt.GetComponent<RectTransform>());

        var r = obj.GetComponent<RectTransform>();
        r.anchorMin = aMin; r.anchorMax = aMax;
        r.offsetMin = r.offsetMax = Vector2.zero;

        return btn;
    }

    // Level card: neon outline, level number, weapon name (ASCII), LOCKED when needed.
    Button MakeLevelButton(Transform parent, int level,
        UnityEngine.Events.UnityAction action)
    {
        string levelStr = level.ToString();
        bool isUnlocked = level <= GetUnlockedLevelCountSafe();
        string weaponLine = GetWeaponLabelForLevel(level).ToUpperInvariant();

        var obj = new GameObject("Btn_" + levelStr);
        obj.transform.SetParent(parent, false);

        var img = obj.AddComponent<Image>();
        img.color = new Color(0.10f, 0.13f, 0.22f, 0.98f);

        var glow = obj.AddComponent<Outline>();
        glow.effectColor = new Color(0.30f, 0.55f, 0.95f, 0.55f);
        glow.effectDistance = new Vector2(2f, -2f);

        var hover = obj.AddComponent<UICardHoverEffect>();
        hover.glowOutline = glow;
        hover.state = isUnlocked ? UICardHoverEffect.CardState.Normal : UICardHoverEffect.CardState.Locked;

        var btn = obj.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.interactable = true;
        ColorBlock cb = btn.colors;
        cb.normalColor = cb.highlightedColor = cb.selectedColor = new Color(0f, 0f, 0f, 0f);
        cb.pressedColor = new Color(1f, 1f, 1f, 0.08f);
        btn.colors = cb;
        btn.onClick.AddListener(action);

        // Thin top accent (sci-fi strip, not a flat rectangle)
        var accentTop = new GameObject("AccentTop");
        accentTop.transform.SetParent(obj.transform, false);
        var accentTopImg = accentTop.AddComponent<Image>();
        float hue = (level * 37f % 360f) / 360f;
        accentTopImg.color = Color.HSVToRGB(hue, 0.42f, isUnlocked ? 0.62f : 0.28f);
        var topRt = accentTop.GetComponent<RectTransform>();
        topRt.anchorMin = new Vector2(0.04f, 0.88f);
        topRt.anchorMax = new Vector2(0.96f, 0.98f);
        topRt.offsetMin = topRt.offsetMax = Vector2.zero;

        // Left edge glow bar
        var accentSide = new GameObject("AccentSide");
        accentSide.transform.SetParent(obj.transform, false);
        var accentSideImg = accentSide.AddComponent<Image>();
        accentSideImg.color = new Color(0.35f, 0.75f, 1f, isUnlocked ? 0.35f : 0.12f);
        var sideRt = accentSide.GetComponent<RectTransform>();
        sideRt.anchorMin = new Vector2(0f, 0.06f);
        sideRt.anchorMax = new Vector2(0.03f, 0.86f);
        sideRt.offsetMin = sideRt.offsetMax = Vector2.zero;

        // Level number
        var numObj = new GameObject("LevelNum");
        numObj.transform.SetParent(obj.transform, false);
        var numTmp = numObj.AddComponent<TextMeshProUGUI>();
        numTmp.text = levelStr;
        numTmp.fontSize = 38;
        numTmp.color = new Color(0.94f, 0.96f, 1f, 1f);
        numTmp.alignment = TextAlignmentOptions.Center;
        numTmp.fontStyle = FontStyles.Bold;
        numTmp.raycastTarget = false;
        if (prismFont) numTmp.font = prismFont;
        var nrt = numObj.GetComponent<RectTransform>();
        nrt.anchorMin = new Vector2(0.05f, 0.58f);
        nrt.anchorMax = new Vector2(0.95f, 0.88f);
        nrt.offsetMin = nrt.offsetMax = Vector2.zero;

        // Weapon name (plain text, no symbols)
        var weaponObj = new GameObject("WeaponLine");
        weaponObj.transform.SetParent(obj.transform, false);
        var weaponTmp = weaponObj.AddComponent<TextMeshProUGUI>();
        weaponTmp.text = weaponLine;
        weaponTmp.fontSize = 12;
        weaponTmp.color = new Color(0.70f, 0.78f, 0.96f, 0.88f);
        weaponTmp.alignment = TextAlignmentOptions.Center;
        weaponTmp.fontStyle = FontStyles.Bold;
        weaponTmp.textWrappingMode = TextWrappingModes.Normal;
        weaponTmp.overflowMode = TextOverflowModes.Ellipsis;
        weaponTmp.raycastTarget = false;
        if (prismFont) weaponTmp.font = prismFont;
        var wrt = weaponObj.GetComponent<RectTransform>();
        wrt.anchorMin = new Vector2(0.06f, 0.22f);
        wrt.anchorMax = new Vector2(0.94f, 0.56f);
        wrt.offsetMin = wrt.offsetMax = Vector2.zero;

        // LOCKED label (hidden when unlocked; RefreshLevelHighlight toggles)
        var lockObj = new GameObject("LockLabel");
        lockObj.transform.SetParent(obj.transform, false);
        var lockTmp = lockObj.AddComponent<TextMeshProUGUI>();
        lockTmp.text = "LOCKED";
        lockTmp.fontSize = 11;
        lockTmp.color = new Color(0.92f, 0.42f, 0.42f, 0.92f);
        lockTmp.alignment = TextAlignmentOptions.Center;
        lockTmp.fontStyle = FontStyles.Bold;
        lockTmp.raycastTarget = false;
        if (prismFont) lockTmp.font = prismFont;
        var lrt = lockObj.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0.05f, 0.04f);
        lrt.anchorMax = new Vector2(0.95f, 0.18f);
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        lockObj.SetActive(!isUnlocked);

        return btn;
    }

    private static int GetUnlockedLevelCountSafe()
    {
        if (GameManager.Instance != null)
            return GameManager.Instance.GetUnlockedLevelCount();
        return Mathf.Clamp(PlayerPrefs.GetInt("UnlockedLevels", 1), 1, GameManager.TotalLevels);
    }

    private static string GetWeaponLabelForLevel(int level)
    {
        if (GameManager.Instance != null)
            return GameManager.Instance.GetWeaponNameForLevel(level);
        int idx = Mathf.Clamp(level - 1, 0, LevelWeaponNamesFallback.Length - 1);
        return LevelWeaponNamesFallback[idx];
    }

    TextMeshProUGUI MakeText(Transform parent, string text, float size, Color color,
        Vector2 aMin, Vector2 aMax, bool isTitle = false)
    {
        var obj = new GameObject("Txt"); obj.transform.SetParent(parent, false);
        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.fontSize = size; tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        if (prismFont) tmp.font = prismFont;
        if (isTitle) { tmp.fontStyle = FontStyles.Bold; obj.AddComponent<Outline>().effectColor = Color.white; }
        var r = obj.GetComponent<RectTransform>();
        r.anchorMin = aMin; r.anchorMax = aMax; r.offsetMin = r.offsetMax = Vector2.zero;
        return tmp;
    }

    Button MakeButton(Transform parent, string label, Vector2 aMin, Vector2 aMax,
        UnityEngine.Events.UnityAction action, Color? bgColor = null)
    {
        var obj = new GameObject("Btn_" + label); obj.transform.SetParent(parent, false);
        var img = obj.AddComponent<Image>(); img.color = bgColor ?? Color.white;
        var btn = obj.AddComponent<Button>(); btn.onClick.AddListener(action);
        var txt = new GameObject("Txt"); txt.transform.SetParent(obj.transform, false);
        var tmp = txt.AddComponent<TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = 32; tmp.color = Color.white;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        if (prismFont) tmp.font = prismFont;
        Stretch(txt.GetComponent<RectTransform>());
        var r = obj.GetComponent<RectTransform>();
        r.anchorMin = aMin; r.anchorMax = aMax; r.offsetMin = r.offsetMax = Vector2.zero;
        return btn;
    }

    void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = r.offsetMax = Vector2.zero;
    }

    TMP_FontAsset ResolvePrismFont()
    {
        if (prismFont != null)
            return prismFont;

        TMP_FontAsset lib = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (lib != null)
            return lib;

        TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        for (int i = 0; i < fonts.Length; i++)
        {
            TMP_FontAsset font = fonts[i];
            if (font != null && (font.name.Contains("Arizona") || font.name.Contains("Azonix")))
                return font;
        }

        if (TMP_Settings.defaultFontAsset != null)
            return TMP_Settings.defaultFontAsset;

        return null;
    }
}
