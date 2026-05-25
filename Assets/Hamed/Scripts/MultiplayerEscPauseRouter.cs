using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public sealed class MultiplayerEscPauseRouter : MonoBehaviour
{
    private static int _lastProcessedFrame = -1;
    private static int _lastToggleFrame = -1;
    private static float _nextEscAllowedTime;
    private float _aliveLogTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        EnsureRouterForScene(SceneManager.GetActiveScene());
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureRouterForScene(scene);
    }

    private static void EnsureRouterForScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || scene.name != MultiplayerMode.MultiplayerSceneName)
            return;

        if (FindFirstObjectByType<MultiplayerEscPauseRouter>() != null)
            return;

        GameObject go = new GameObject("MultiplayerEscPauseRouter");
        go.AddComponent<MultiplayerEscPauseRouter>();
    }

    private void Update()
    {
        if (SceneManager.GetActiveScene().name != MultiplayerMode.MultiplayerSceneName)
            return;

        bool hudIsRunning = HUDManager.Instance != null && HUDManager.Instance.isActiveAndEnabled;
        if (!hudIsRunning)
        {
            // Periodic alive log removed; ProcessEscPauseInput is the real
            // useful work here and runs unchanged.
            ProcessEscPauseInput();
        }
    }

    public static void ProcessEscPauseInput()
    {
        if (SceneManager.GetActiveScene().name != MultiplayerMode.MultiplayerSceneName)
            return;

        if (_lastProcessedFrame == Time.frameCount)
            return;
        _lastProcessedFrame = Time.frameCount;

        if (!WasEscapePressedThisFrame())
            return;

        Debug.Log("[MPPauseHUD] ESC detected");

        if (_lastToggleFrame == Time.frameCount || Time.unscaledTime < _nextEscAllowedTime)
            return;

        _lastToggleFrame = Time.frameCount;
        _nextEscAllowedTime = Time.unscaledTime + 0.2f;

        PauseMenuController controller = FindPauseMenuController();
        if (controller == null)
        {
            GameObject go = new GameObject("PauseMenuController_Runtime");
            controller = go.AddComponent<PauseMenuController>();
        }

        if (!controller.gameObject.activeSelf)
            controller.gameObject.SetActive(true);
        if (!controller.enabled)
            controller.enabled = true;

        controller.TogglePauseExternal();
    }

    private static bool WasEscapePressedThisFrame()
    {
        Keyboard keyboard = Keyboard.current;
        return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
    }

    private static PauseMenuController FindPauseMenuController()
    {
        PauseMenuController[] controllers = FindObjectsByType<PauseMenuController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        PauseMenuController fallback = null;

        for (int i = 0; i < controllers.Length; i++)
        {
            PauseMenuController controller = controllers[i];
            if (controller == null)
                continue;

            if (controller.isActiveAndEnabled)
                return controller;

            fallback ??= controller;
        }

        return fallback;
    }
}
