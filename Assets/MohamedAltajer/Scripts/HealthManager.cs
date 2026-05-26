using UnityEngine;
using UnityEngine.SceneManagement;

public class HealthManager : MonoBehaviour
{
    private const string MainMenuSceneName = "MainMenu";
    private const float StartupProtectionMaxSeconds = 8f;
    private static HealthManager _instance;
    private static bool _countdownIsActive;
    private static float _startupProtectionStartedAt = -1f;

    public static bool CountdownIsActive
    {
        get
        {
            ExpireStaleStartupProtection();
            return _countdownIsActive;
        }
        private set
        {
            _countdownIsActive = value;
            _startupProtectionStartedAt = value ? Time.realtimeSinceStartup : -1f;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null)
            return;

        GameObject go = new GameObject("HealthManager_Runtime");
        _instance = go.AddComponent<HealthManager>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Application.isPlaying)
            return;

        if (scene.name == MainMenuSceneName)
        {
            CountdownIsActive = false;
            ClearDamageUi();
            return;
        }

        BeginStartupProtection();
    }

    public static void BeginStartupProtection()
    {
        CountdownIsActive = true;
        ResetPlayerHealthAndUi();
    }

    public static void ReleaseStartupProtection()
    {
        CountdownIsActive = false;
        ResetPlayerHealthAndUi();
    }

    public static bool BlocksPlayerDamage(GameObject target)
    {
        ExpireStaleStartupProtection();
        if (!CountdownIsActive || target == null)
            return false;

        return target.GetComponentInParent<PlayerHealth>() != null
            || target.GetComponentInParent<PlayerController>() != null
            || target.CompareTag("Player");
    }

    public static void ResetPlayerHealthAndUi()
    {
        PlayerHealth[] players = Object.FindObjectsByType<PlayerHealth>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        for (int i = 0; i < players.Length; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;

            player.ResetToFullHealth();
        }

        if (GameManager.Instance != null)
            GameManager.Instance.playerTookDamage = false;

        ClearDamageUi();
    }

    public static void ClearDamageUi()
    {
        if (HUDManager.Instance != null)
            HUDManager.Instance.ClearDamageOverlays();
    }

    private static void ExpireStaleStartupProtection()
    {
        if (!_countdownIsActive)
            return;
        if (_startupProtectionStartedAt < 0f)
            return;
        if (Time.realtimeSinceStartup - _startupProtectionStartedAt <= StartupProtectionMaxSeconds)
            return;

        _countdownIsActive = false;
        _startupProtectionStartedAt = -1f;
        ClearDamageUi();
        Debug.LogWarning("[HealthManager] Startup protection expired automatically after countdown timeout.");
    }
}
