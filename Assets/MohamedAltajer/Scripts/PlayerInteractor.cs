using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Raycast-based interaction controller for the player. Each frame it casts
/// a ray forward from the active camera; if the ray hits a collider whose
/// parent chain contains an <see cref="IInteractable"/>, a "[E] PROMPT"
/// reticle is shown and pressing E fires <see cref="IInteractable.Interact"/>.
///
/// The reticle UI is built procedurally on first use — no scene wiring
/// required. The component auto-attaches to the player on Start so any
/// existing scene works without prefab edits.
/// </summary>
[DisallowMultipleComponent]
public class PlayerInteractor : MonoBehaviour
{
    [Header("Raycast")]
    [Tooltip("Maximum interaction distance in meters.")]
    public float maxDistance = 3f;

    [Tooltip("Interact key.")]
    public KeyCode interactKey = KeyCode.E;

    [Tooltip("Also use primary mouse button when the pointer is not over UI.")]
    public bool useMouseClick = true;

    [Tooltip("When the cursor is visible/unlocked, cast from the mouse instead of screen center.")]
    public bool rayFromMouseWhenCursorUnlocked = true;

    [Tooltip("Layers considered for interaction. Default = everything except UI/IgnoreRaycast.")]
    public LayerMask interactionMask = ~0;

    [Header("Debug")]
    [Tooltip("Temporary debug logs for door interaction issues.")]
    public bool debugDoorInteraction = false;
    [Tooltip("When true and E is pressed, logs all ray hits with layer + distance.")]
    public bool debugLogAllHitsOnPress = false;

    private IInteractable    _currentTarget;
    private GameObject       _reticleRoot;
    private TextMeshProUGUI  _reticleLabel;
    private Image            _reticleBg;

    /// <summary>
    /// Auto-bootstrap so any scene with a player tagged "Player" gets an
    /// interactor without requiring inspector-driven wiring.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoAttach()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, _) =>
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p == null) return;
            if (p.GetComponent<PlayerInteractor>() == null)
                p.AddComponent<PlayerInteractor>();
        };
    }

    private void Update()
    {
        if (EndMatchCinematic.GameplayLocked)
        {
            HideReticle();
            _currentTarget = null;
            return;
        }

        // Block ALL interaction (prompt + input) when gameplay is gated: pause menu
        // open, TAB full map held, time scale frozen, or cursor unlocked over UI.
        // The reticle and Interact() must both stop — otherwise door prompts bleed
        // through the pause menu and clicks register on doors behind UI.
        if (IsInteractionGated())
        {
            HideReticle();
            _currentTarget = null;
            return;
        }

        bool fullMapHeld = IsFullMapHeld();

        Camera cam = ResolveInteractCamera();
        if (cam == null) { HideReticle(); return; }

        Vector2 screenPoint = GetInteractionScreenPoint();
        Ray ray = BuildInteractionRay(cam, screenPoint);
        bool pressedThisFrame = WasInteractPressedThisFrame();
        bool mouseClickedThisFrame = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
        if (debugDoorInteraction && pressedThisFrame)
        {
            Debug.Log($"[PlayerInteractor] E pressed. origin={ray.origin} dir={ray.direction} maxDist={maxDistance} mask=0x{interactionMask.value:X}");
            if (debugLogAllHitsOnPress)
                DebugLogRaycastAll(ray, "PrimaryRay");
        }

        if (TryGetInteractableHit(ray, out RaycastHit hit, out IInteractable interactable))
        {
            if (debugDoorInteraction)
            {
                DoorController dc = FindDoorController(hit.collider);
                Debug.Log($"[PlayerInteractor] InteractableHit: {(hit.collider != null ? hit.collider.name : "<null>")}  Root: {(hit.collider != null ? hit.collider.transform.root.name : "<null>")}  DoorControllerFound={dc != null}  Prompt=\"{interactable.GetPrompt()}\"  CanInteract={interactable.CanInteract}");
            }
            _currentTarget = interactable;
            string clickHint = useMouseClick ? " / CLICK" : string.Empty;
            if (fullMapHeld) HideReticle();
            else ShowReticle("[" + interactKey + "]" + clickHint + "  " + interactable.GetPrompt());

            bool useUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            bool pressed = pressedThisFrame;
            if (useMouseClick && !pressed && !useUi)
            {
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                    pressed = true;
            }

            if (pressed)
                PerformInteraction(hit.collider, interactable);
            return;
        }

        // Fallback from the player body itself. Third-person cameras sit behind
        // the player, so a short 3m interaction ray can otherwise expire before
        // reaching the door in front of the character.
        Ray forwardRay = new Ray(transform.position + Vector3.up * 1.25f, transform.forward);
        if (TryGetInteractableHit(forwardRay, out hit, out interactable))
        {
            if (debugDoorInteraction)
            {
                DoorController dc = FindDoorController(hit.collider);
                Debug.Log($"[PlayerInteractor] InteractableForwardHit: {(hit.collider != null ? hit.collider.name : "<null>")}  Root: {(hit.collider != null ? hit.collider.transform.root.name : "<null>")}  DoorControllerFound={dc != null}  Prompt=\"{interactable.GetPrompt()}\"  CanInteract={interactable.CanInteract}");
            }
            _currentTarget = interactable;
            string clickHint = useMouseClick ? " / CLICK" : string.Empty;
            if (fullMapHeld) HideReticle();
            else ShowReticle("[" + interactKey + "]" + clickHint + "  " + interactable.GetPrompt());

            bool useUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            bool pressed = pressedThisFrame;
            if (useMouseClick && !pressed && !useUi)
            {
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                    pressed = true;
            }

            if (pressed)
                PerformInteraction(hit.collider, interactable);
            return;
        }

        if (debugDoorInteraction && pressedThisFrame)
        {
            Debug.Log("[PlayerInteractor] E pressed but no interactable found within 3m.");
            if (debugLogAllHitsOnPress)
                DebugLogRaycastAll(forwardRay, "ForwardRay");
        }

        _currentTarget = null;
        HideReticle();
    }

    private void DebugLogRaycastAll(Ray ray, string label)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, interactionMask, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            Debug.Log($"[PlayerInteractor] {label}: no hits.");
            return;
        }

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            Collider c = hits[i].collider;
            if (c == null) continue;
            string layerName = LayerMask.LayerToName(c.gameObject.layer);
            DoorController dc = FindDoorController(c);
            IInteractable intr = FindInteractable(c);
            Debug.Log($"[PlayerInteractor] {label} hit#{i}: name={c.name} root={c.transform.root.name} layer={c.gameObject.layer}({layerName}) dist={hits[i].distance:0.00} DoorController={dc != null} IInteractable={intr != null}");
            Debug.Log($"[DoorFix] hit={c.name} root={c.transform.root.name} foundDoor={dc != null} doorObj={(dc != null ? dc.name : "<null>")}");
        }
    }

    private Ray BuildInteractionRay(Camera cam, Vector2 screenPoint)
    {
        bool freeCursor = Cursor.lockState != CursorLockMode.Locked || Cursor.visible;
        if (rayFromMouseWhenCursorUnlocked && freeCursor)
            return cam.ScreenPointToRay(screenPoint);

        Vector3 origin = transform.position + Vector3.up * 1.25f;
        Vector3 direction = cam.transform.forward;
        if (direction.sqrMagnitude < 0.0001f)
            direction = transform.forward;
        return new Ray(origin, direction.normalized);
    }

    private static Camera ResolveInteractCamera()
    {
        if (CameraController.Instance != null)
        {
            Camera c = CameraController.Instance.GetComponent<Camera>();
            if (c != null && c.enabled) return c;
        }

        return Camera.main;
    }

    private Vector2 GetInteractionScreenPoint()
    {
        bool freeCursor = Cursor.lockState != CursorLockMode.Locked || Cursor.visible;
        if (rayFromMouseWhenCursorUnlocked && freeCursor && Mouse.current != null)
            return Mouse.current.position.ReadValue();

        return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
    }

    /// <summary>
    /// Third-person rays from the camera often hit the player mesh first. Walk
    /// sorted hits until we find an <see cref="IInteractable"/> on another body.
    /// </summary>
    private bool TryGetInteractableHit(Ray ray, out RaycastHit bestHit, out IInteractable interactable)
    {
        bestHit      = default;
        interactable = null;

        RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, interactionMask, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        Transform playerRoot = transform;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (col == null) continue;
            if (IsPlayerOwnedCollider(col, playerRoot)) continue;

            IInteractable found = FindInteractable(col);
            if (found != null && found.CanInteract)
            {
                bestHit      = hits[i];
                interactable = found;
                return true;
            }
        }

        return false;
    }

    private static bool IsPlayerOwnedCollider(Collider col, Transform playerRoot)
    {
        if (playerRoot == null || col == null) return false;
        Transform t = col.transform;
        return t == playerRoot || t.IsChildOf(playerRoot);
    }

    /// <summary>
    /// True when gameplay input should be blocked — pause menu open, TAB full map
    /// held, time scale frozen, or cursor unlocked over UI. Door prompt + Interact
    /// both stop while this is true.
    /// </summary>
    private static PauseMenuController _cachedPause;
    private static float _pauseLookupCooldown;
    private bool IsInteractionGated()
    {
        // TAB held — full map active.
        if (Keyboard.current != null && Keyboard.current.tabKey.isPressed)
            return true;

        // Time scale frozen by anything (pause, end-of-match, cinematic).
        if (Time.timeScale <= 0.0001f)
            return true;

        // Pause menu open — cached lookup; PauseMenuController is a singleton-ish
        // per scene and FindFirstObjectByType is too expensive to call every frame.
        if (_cachedPause == null || Time.unscaledTime > _pauseLookupCooldown)
        {
            _cachedPause = Object.FindFirstObjectByType<PauseMenuController>(FindObjectsInactive.Include);
            _pauseLookupCooldown = Time.unscaledTime + 0.5f;
        }
        if (_cachedPause != null && _cachedPause.IsPauseMenuOpen)
            return true;

        // Cursor unlocked over UI = some menu/settings panel is showing. Locked
        // cursor = gameplay mode → not gated by this signal.
        if (Cursor.lockState != CursorLockMode.Locked
            && EventSystem.current != null
            && EventSystem.current.IsPointerOverGameObject())
            return true;

        return false;
    }

    private void PerformInteraction(Collider hitCollider, IInteractable interactable)
    {
        if (interactable == null)
            return;

        interactable.Interact(gameObject);

        Transform doorRoot = FindDoorPassableRoot(hitCollider, interactable);
        if (doorRoot != null)
        {
            if (doorRoot.GetComponentInChildren<SciFiSlidingDoor>(true) != null)
                return;
            MakeDoorPassable(doorRoot);
        }
    }

    /// <summary>
    /// Walks parents and checks each <see cref="MonoBehaviour"/> for <see cref="IInteractable"/>.
    /// More reliable than <c>GetComponentInParent&lt;IInteractable&gt;()</c> across Unity versions.
    /// </summary>
    private static IInteractable FindInteractable(Collider col)
    {
        if (col == null) return null;

        for (Transform tr = col.transform; tr != null; tr = tr.parent)
        {
            string lower = tr.name.ToLowerInvariant();
            if (tr.GetComponent<SciFiSlidingDoor>() != null
                || lower.Contains("door")
                || lower.Contains("gate")
                || lower.Contains("garage")
                || lower.Contains("shutter")
                || lower.Contains("rollup"))
                return null;
        }

        // Walk up parents. At each level also scan that level's SUBTREE for an
        // IInteractable. Critical for the SciFi kit: Wall_Door's BoxColliders live on
        // a child node (LP_Door_Wall_snaps), while SciFiSlidingDoor lives on a sibling
        // child pivot (SlidingDoor). A pure-parent walk misses the sibling; a pure-
        // child walk from the collider misses it too (collider node has no children).
        // Walking parents + subtree-scan at each parent hits the Wall_Door root level
        // and finds SciFiSlidingDoor via the sibling pivot before climbing further.
        for (Transform tr = col.transform; tr != null; tr = tr.parent)
        {
            MonoBehaviour[] subtree = tr.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < subtree.Length; i++)
            {
                MonoBehaviour mb = subtree[i];
                if (mb == null || !mb.enabled) continue;
                if (mb is IInteractable intr && intr.CanInteract)
                    return intr;
            }

            // Stop climbing once we're past a "door-like" or kit-wall parent — prevents
            // adopting an unrelated interactable from a far-away part of the arena.
            string lower = tr.name.ToLowerInvariant();
            if (lower.Contains("door") || lower.Contains("wall_") || lower.StartsWith("wall"))
                break;
        }

        return null;
    }

    private static DoorController FindDoorController(Collider col)
    {
        if (col == null) return null;

        DoorController door = col.GetComponent<DoorController>();
        if (door != null) return door;

        door = col.GetComponentInParent<DoorController>(true);
        if (door != null) return door;

        door = col.GetComponentInChildren<DoorController>(true);
        if (door != null) return door;

        Transform root = col.transform.root;
        door = root != null ? root.GetComponentInChildren<DoorController>(true) : null;
        if (door != null && (col.transform.IsChildOf(door.transform) || IsDoorLikeTransform(col.transform, col.transform)))
            return door;

        return null;
    }

    private static DoorController EnsureRuntimeDoorController(Collider col)
    {
        // Fake-door suppression: when the global DoorController kill-switch is
        // off (default for the v3 industrial map), do NOT promote arbitrary
        // door-/gate-/Object084-named colliders into runtime DoorControllers.
        // This prevents the "[E] OPEN DOOR" prompt from appearing on fences,
        // containers, and welded hangar doors.
        if (!DoorController.DoorInteractionsEnabled)
            return null;

        Transform doorRoot = FindNearestDoorRoot(col);
        if (doorRoot == null)
            return null;

        DoorController door = doorRoot.GetComponent<DoorController>();
        if (door == null)
            door = doorRoot.gameObject.AddComponent<DoorController>();

        DoorPassThroughOpen passThrough = doorRoot.GetComponent<DoorPassThroughOpen>();
        if (passThrough == null)
            passThrough = doorRoot.gameObject.AddComponent<DoorPassThroughOpen>();

        door.openOnStart = false;
        door.openOnPlayerTrigger = false;
        door.interactiveToggle = false;
        passThrough.hideOnOpen = false; // keep door visible — only disable colliders for pass-through
        return door;
    }

    private static Transform FindDoorPassableRoot(Collider col, IInteractable interactable)
    {
        if (col == null)
            return null;

        DoorController door = FindDoorController(col);
        if (door != null)
            return door.transform;

        if (interactable is DoorController interactableDoor)
            return interactableDoor.transform;

        return FindNearestDoorRoot(col);
    }

    private static void MakeDoorPassable(Transform doorRoot)
    {
        if (doorRoot == null)
            return;

        DoorPassThroughOpen passThrough = doorRoot.GetComponent<DoorPassThroughOpen>();
        if (passThrough != null)
        {
            passThrough.OpenPassable();
            return;
        }

        // Disable only colliders so characters can pass through; keep the door
        // mesh visible (no SetActive(false) — previous behaviour was the cause
        // of "doors missing" reports).
        Collider[] colliders = doorRoot.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || !c.enabled) continue;
            c.enabled = false;
        }
    }

    private static Transform FindNearestDoorRoot(Collider col)
    {
        if (col == null) return null;

        for (Transform tr = col.transform; tr != null; tr = tr.parent)
        {
            if (IsDoorLikeTransform(tr, col.transform))
                return tr;
        }

        return null;
    }

    private static bool IsDoorLikeTransform(Transform candidate, Transform hitTransform)
    {
        if (candidate == null)
            return false;

        string lower = candidate.name.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("gate") || lower.Contains("garage") ||
            lower.Contains("shutter") || lower.Contains("rollup"))
            return true;

        if (candidate == hitTransform && candidate.name == "Object084")
            return IsKnownDoorMesh(candidate);

        return false;
    }

    private static bool IsKnownDoorMesh(Transform candidate)
    {
        if (candidate == null)
            return false;

        // Object084 in this project is the big level door. As long as the mesh
        // has the standard collider/renderer trio it is treated as a door —
        // the previous hangar/industrial parent-name requirement excluded
        // door instances whose parent hierarchy used different naming, leaving
        // the door un-interactable when the player pressed E.
        return candidate.GetComponent<Collider>()   != null
            && candidate.GetComponent<Renderer>()   != null
            && candidate.GetComponent<MeshFilter>() != null;
    }

    /// <summary>
    /// True while the TAB full-map key is held. Works for both old and new input systems.
    /// </summary>
    private static bool IsFullMapHeld()
    {
        // Project is on the New Input System exclusively — legacy UnityEngine.Input
        // throws InvalidOperationException, so we never call it from this file.
        if (Keyboard.current == null) return false;
        return Keyboard.current.tabKey.isPressed;
    }

    private bool WasInteractPressedThisFrame()
    {
        if (Keyboard.current == null) return false;
        switch (interactKey)
        {
            case KeyCode.E: return Keyboard.current.eKey.wasPressedThisFrame;
            case KeyCode.F: return Keyboard.current.fKey.wasPressedThisFrame;
            case KeyCode.Q: return Keyboard.current.qKey.wasPressedThisFrame;
            case KeyCode.R: return Keyboard.current.rKey.wasPressedThisFrame;
            case KeyCode.G: return Keyboard.current.gKey.wasPressedThisFrame;
            case KeyCode.Space: return Keyboard.current.spaceKey.wasPressedThisFrame;
            case KeyCode.LeftShift: return Keyboard.current.leftShiftKey.wasPressedThisFrame;
            case KeyCode.RightShift: return Keyboard.current.rightShiftKey.wasPressedThisFrame;
            case KeyCode.Return: return Keyboard.current.enterKey.wasPressedThisFrame;
            default: return Keyboard.current.eKey.wasPressedThisFrame;  // fall back to E
        }
    }

    private void EnsureReticle()
    {
        if (_reticleRoot != null) return;
        _reticleRoot = new GameObject("InteractionReticle");
        DontDestroyOnLoad(_reticleRoot);

        Canvas canvas = _reticleRoot.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 4500;
        _reticleRoot.AddComponent<GraphicRaycaster>();
        CanvasScaler scaler = _reticleRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        GameObject pill = new GameObject("Pill");
        pill.transform.SetParent(_reticleRoot.transform, false);
        _reticleBg = pill.AddComponent<Image>();
        _reticleBg.color = new Color(0.05f, 0.07f, 0.12f, 0.78f);
        Outline outline = pill.AddComponent<Outline>();
        outline.effectColor    = new Color(0.30f, 0.55f, 1f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        RectTransform pillRT = pill.GetComponent<RectTransform>();
        pillRT.anchorMin = new Vector2(0.5f, 0.5f);
        pillRT.anchorMax = new Vector2(0.5f, 0.5f);
        pillRT.pivot     = new Vector2(0.5f, 0.5f);
        pillRT.sizeDelta = new Vector2(360f, 64f);
        // Sit just above the screen center.
        pillRT.anchoredPosition = new Vector2(0f, 75f);

        GameObject lbl = new GameObject("Label");
        lbl.transform.SetParent(pill.transform, false);
        _reticleLabel = lbl.AddComponent<TextMeshProUGUI>();
        _reticleLabel.text       = string.Empty;
        _reticleLabel.fontSize   = 28f;
        _reticleLabel.fontStyle  = FontStyles.Bold;
        _reticleLabel.alignment  = TextAlignmentOptions.Center;
        _reticleLabel.color      = new Color(0.94f, 0.96f, 1f, 1f);
        RectTransform lblRT = lbl.GetComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero;
        lblRT.anchorMax = Vector2.one;
        lblRT.offsetMin = lblRT.offsetMax = Vector2.zero;

        _reticleRoot.SetActive(false);
    }

    private void ShowReticle(string text)
    {
        EnsureReticle();
        if (_reticleLabel != null) _reticleLabel.text = text;
        if (_reticleRoot != null && !_reticleRoot.activeSelf)
            _reticleRoot.SetActive(true);
    }

    private void HideReticle()
    {
        if (_reticleRoot != null && _reticleRoot.activeSelf)
            _reticleRoot.SetActive(false);
    }
}
