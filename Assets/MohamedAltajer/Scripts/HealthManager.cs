using UnityEngine;
using UnityEngine.SceneManagement;

public class HealthManager : MonoBehaviour
{
    private const string MainMenuSceneName = "MainMenu";
    private static HealthManager _instance;

    public static bool CountdownIsActive { get; private set; }

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
}
