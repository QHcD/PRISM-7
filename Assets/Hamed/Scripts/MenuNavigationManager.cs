using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Arrow-key navigation for runtime-built menus: main-menu grid or a simple vertical list.
/// Enter activates the focused <see cref="Selectable"/> via UI submit. Mouse hover syncs focus.
/// Focus uses <see cref="MenuButtonHoverEffect.KeyboardFocus"/> plus a blue outline / slight scale when no outline exists.
/// </summary>
[DisallowMultipleComponent]
public class MenuNavigationManager : MonoBehaviour
{
    private struct OutlineSnap
    {
        public Color Color;
        public Vector2 Distance;
    }

    private enum NavLayout
    {
        MainMenuGrid,
        LinearVertical,
    }

    private static readonly int[][] DefaultMainMenuRows =
    {
        new[] { 0 },
        new[] { 1, 2 },
        new[] { 3, 4 },
        new[] { 5 },
        new[] { 6 },
        new[] { 7 },
        new[] { 8 },
    };

    private static readonly Color FocusOutlineColor = new Color(0.35f, 0.72f, 1f, 1f);
    private static readonly Vector2 FocusOutlineDistance = new Vector2(5f, -5f);
    private const float FocusScale = 1.045f;

    [SerializeField] private List<Selectable> _selectables = new List<Selectable>();
    private readonly Dictionary<Outline, OutlineSnap> _outlineBase = new Dictionary<Outline, OutlineSnap>();
    private readonly Dictionary<Selectable, Vector3> _scaleBase = new Dictionary<Selectable, Vector3>();

    private int[][] _rows = DefaultMainMenuRows;
    private NavLayout _layout = NavLayout.MainMenuGrid;
    private int _focusedIndex;
    private bool _initialized;

    public static MenuNavigationManager AttachMainMenu(GameObject host, IList<Button> buttons)
    {
        if (host == null || buttons == null) return null;

        MenuNavigationManager mgr = host.GetComponent<MenuNavigationManager>();
        if (mgr == null) mgr = host.AddComponent<MenuNavigationManager>();

        mgr._selectables.Clear();
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i] != null) mgr._selectables.Add(buttons[i]);

        mgr._layout = NavLayout.MainMenuGrid;
        mgr._rows = DefaultMainMenuRows;
        mgr._focusedIndex = 0;
        mgr._initialized = true;
        mgr.CacheVisualBaselines();
        mgr.WirePointerSync();
        mgr.ApplyFocusVisuals(false);
        return mgr;
    }

    /// <summary>Single-column order: first item = top. Used for Settings, Options, overlays.</summary>
    public static MenuNavigationManager AttachLinear(GameObject host, IList<Selectable> items)
    {
        if (host == null || items == null) return null;

        MenuNavigationManager mgr = host.GetComponent<MenuNavigationManager>();
        if (mgr == null) mgr = host.AddComponent<MenuNavigationManager>();

        mgr._selectables.Clear();
        for (int i = 0; i < items.Count; i++)
            if (items[i] != null) mgr._selectables.Add(items[i]);

        mgr._layout = NavLayout.LinearVertical;
        mgr._rows = BuildSingleColumnRows(mgr._selectables.Count);
        mgr._focusedIndex = 0;
        mgr._initialized = true;
        mgr.CacheVisualBaselines();
        mgr.WirePointerSync();
        mgr.ApplyFocusVisuals(false);
        return mgr;
    }

    /// <summary>
    /// IMG_6880.mov compact Main Menu: consumes the hierarchy created by
    /// <see cref="UIManager.CompactifyMainMenuCanvas"/> and wires arrow-key
    /// navigation in top-to-bottom visual order (pairs expand to two steps).
    /// </summary>
    public static MenuNavigationManager AttachMainMenuCompact(GameObject host, Transform stackRoot)
    {
        if (host == null || stackRoot == null) return null;

        var ordered = new List<Selectable>(16);
        for (int i = 0; i < stackRoot.childCount; i++)
        {
            Transform row = stackRoot.GetChild(i);
            if (row == null) continue;

            // Pair row: collect buttons left-to-right.
            HorizontalLayoutGroup hlg = row.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                for (int c = 0; c < row.childCount; c++)
                {
                    Selectable s = row.GetChild(c).GetComponent<Selectable>();
                    if (s != null) ordered.Add(s);
                }
                continue;
            }

            // Single button row.
            Selectable single = row.GetComponent<Selectable>();
            if (single != null) ordered.Add(single);
        }

        return AttachLinear(host, ordered);
    }

    private static int[][] BuildSingleColumnRows(int n)
    {
        int[][] r = new int[n][];
        for (int i = 0; i < n; i++)
            r[i] = new[] { i };
        return r;
    }

    private void CacheVisualBaselines()
    {
        _outlineBase.Clear();
        _scaleBase.Clear();
        for (int i = 0; i < _selectables.Count; i++)
        {
            Selectable s = _selectables[i];
            if (s == null) continue;

            Outline o = FindOutline(s);
            if (o != null && !_outlineBase.ContainsKey(o))
                _outlineBase[o] = new OutlineSnap { Color = o.effectColor, Distance = o.effectDistance };

            RectTransform rt = s.transform as RectTransform;
            if (rt != null && !_scaleBase.ContainsKey(s) && (s is Button || s is Toggle || s is TMP_InputField))
                _scaleBase[s] = rt.localScale;
        }
    }

    private static Outline FindOutline(Selectable s)
    {
        if (s == null) return null;
        Outline o = s.GetComponent<Outline>();
        if (o != null) return o;
        if (s.targetGraphic != null)
            return s.targetGraphic.GetComponent<Outline>();
        return null;
    }

    private void OnEnable()
    {
        if (_initialized && _selectables.Count > 0)
            ApplyFocusVisuals(false);
    }

    private void Update()
    {
        if (!_initialized || _selectables.Count == 0) return;
        if (!AnyInteractableVisible()) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.upArrowKey.wasPressedThisFrame) { MoveVertical(-1); return; }
        if (kb.downArrowKey.wasPressedThisFrame) { MoveVertical(1); return; }
        if (_layout == NavLayout.MainMenuGrid)
        {
            if (kb.leftArrowKey.wasPressedThisFrame) { MoveHorizontal(-1); return; }
            if (kb.rightArrowKey.wasPressedThisFrame) { MoveHorizontal(1); return; }
        }

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            ActivateFocused();
    }

    private bool AnyInteractableVisible()
    {
        for (int i = 0; i < _selectables.Count; i++)
        {
            Selectable s = _selectables[i];
            if (s != null && s.gameObject.activeInHierarchy && s.IsInteractable())
                return true;
        }
        return false;
    }

    internal void SyncFocusFromPointer(int index)
    {
        if (index < 0 || index >= _selectables.Count) return;
        Selectable s = _selectables[index];
        if (s == null || !s.IsInteractable()) return;
        _focusedIndex = index;
        ApplyFocusVisuals(true);
    }

    private void MoveVertical(int delta)
    {
        if (_layout == NavLayout.LinearVertical)
        {
            int ni = _focusedIndex + delta;
            if (ni < 0 || ni >= _selectables.Count) return;
            _focusedIndex = ni;
            ApplyFocusVisuals(true);
            return;
        }

        if (!TryGetRowColumn(_focusedIndex, out int row, out int col)) return;
        int newRow = row + delta;
        if (newRow < 0 || newRow >= _rows.Length) return;

        int[] newRowArr = _rows[newRow];
        int newCol = Mathf.Clamp(col, 0, newRowArr.Length - 1);
        _focusedIndex = newRowArr[newCol];
        ApplyFocusVisuals(true);
    }

    private void MoveHorizontal(int delta)
    {
        if (!TryGetRowColumn(_focusedIndex, out int row, out int col)) return;
        int[] rowArr = _rows[row];
        if (rowArr.Length < 2) return;

        int newCol = col + delta;
        if (newCol < 0 || newCol >= rowArr.Length) return;
        _focusedIndex = rowArr[newCol];
        ApplyFocusVisuals(true);
    }

    private bool TryGetRowColumn(int selectableIndex, out int row, out int col)
    {
        for (int r = 0; r < _rows.Length; r++)
        {
            int[] cols = _rows[r];
            for (int c = 0; c < cols.Length; c++)
            {
                if (cols[c] == selectableIndex)
                {
                    row = r;
                    col = c;
                    return true;
                }
            }
        }

        row = col = 0;
        return false;
    }

    private void ApplyFocusVisuals(bool notifyEventSystem)
    {
        for (int i = 0; i < _selectables.Count; i++)
        {
            Selectable s = _selectables[i];
            if (s == null) continue;

            bool focused = i == _focusedIndex;
            MenuButtonHoverEffect fx = s.GetComponent<MenuButtonHoverEffect>();
            if (fx != null)
                fx.KeyboardFocus = focused;

            Outline o = FindOutline(s);
            if (o != null && _outlineBase.TryGetValue(o, out OutlineSnap snap))
            {
                if (focused)
                {
                    o.effectColor = FocusOutlineColor;
                    o.effectDistance = FocusOutlineDistance;
                }
                else
                {
                    o.effectColor = snap.Color;
                    o.effectDistance = snap.Distance;
                }
            }

            if (_scaleBase.TryGetValue(s, out Vector3 baseScale))
            {
                RectTransform rt = s.transform as RectTransform;
                if (rt != null)
                    rt.localScale = focused ? baseScale * FocusScale : baseScale;
            }
        }

        if (notifyEventSystem && EventSystem.current != null)
        {
            Selectable focused = _focusedIndex >= 0 && _focusedIndex < _selectables.Count
                ? _selectables[_focusedIndex]
                : null;
            if (focused != null && focused.gameObject.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(focused.gameObject);
        }
    }

    private void ActivateFocused()
    {
        if (_focusedIndex < 0 || _focusedIndex >= _selectables.Count) return;
        Selectable s = _selectables[_focusedIndex];
        if (s == null || !s.IsInteractable()) return;

        EventSystem es = EventSystem.current;
        if (es != null)
        {
            var data = new BaseEventData(es);
            ExecuteEvents.Execute(s.gameObject, data, ExecuteEvents.submitHandler);
        }
    }

    private void WirePointerSync()
    {
        for (int i = 0; i < _selectables.Count; i++)
        {
            Selectable s = _selectables[i];
            if (s == null) continue;
            MenuNavPointerSync sync = s.GetComponent<MenuNavPointerSync>();
            if (sync == null) sync = s.gameObject.AddComponent<MenuNavPointerSync>();
            sync.Owner = this;
            sync.Index = i;
        }
    }

    private sealed class MenuNavPointerSync : MonoBehaviour, IPointerEnterHandler
    {
        public MenuNavigationManager Owner;
        public int Index;

        public void OnPointerEnter(PointerEventData eventData)
        {
            Owner?.SyncFocusFromPointer(Index);
        }
    }
}
