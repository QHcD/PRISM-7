using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#if PUN_2_OR_NEWER
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

public sealed class MatchInitializer : MonoBehaviour
#if PUN_2_OR_NEWER
    , IOnEventCallback
#endif
{
    public const byte CountdownStartEventCode = 71;

    private static MatchInitializer _instance;
    private static bool _countdownRunning;
    private static bool _countdownFinished;

    public static bool CountdownComplete => _countdownFinished;

    public static void EnsureExists()
    {
        if (_instance != null)
            return;

        GameObject host = new GameObject("MatchInitializer");
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<MatchInitializer>();
    }

    public static void ResetSessionState()
    {
        _countdownRunning = false;
        _countdownFinished = false;
        EndMatchCinematic.GameplayLocked = false;
    }

    public static void HandleMultiplayerLoadingFinished()
    {
        EnsureExists();
#if PUN_2_OR_NEWER
        if (!PhotonNetwork.InRoom)
        {
            _instance.StartCoroutine(_instance.RunLocalCountdown());
            return;
        }

        if (PhotonNetwork.IsMasterClient)
            _instance.PublishCountdownStart();
#else
        _instance.StartCoroutine(_instance.RunLocalCountdown());
#endif
    }

#if PUN_2_OR_NEWER
    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent == null || photonEvent.Code != CountdownStartEventCode)
            return;

        if (_countdownRunning || _countdownFinished)
            return;

        StartCoroutine(RunLocalCountdown());
    }

    private void PublishCountdownStart()
    {
        if (_countdownRunning || _countdownFinished)
            return;

        var options = new RaiseEventOptions { Receivers = ReceiverGroup.All };
        PhotonNetwork.RaiseEvent(CountdownStartEventCode, null, options, SendOptions.SendReliable);
    }
#endif

    private void OnEnable()
    {
#if PUN_2_OR_NEWER
        PhotonNetwork.AddCallbackTarget(this);
#endif
    }

    private void OnDisable()
    {
#if PUN_2_OR_NEWER
        PhotonNetwork.RemoveCallbackTarget(this);
#endif
    }

    private IEnumerator RunLocalCountdown()
    {
        if (_countdownRunning)
            yield break;

        _countdownRunning = true;
        _countdownFinished = false;
        HealthManager.BeginStartupProtection();
        EndMatchCinematic.GameplayLocked = true;
        ZeroLocalMovementVectors();

        GameObject existing = GameObject.Find("RuntimeStartCountdown");
        if (existing != null)
            Destroy(existing);

        GameObject root = new GameObject("RuntimeStartCountdown");
        DontDestroyOnLoad(root);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9998;
        root.AddComponent<GraphicRaycaster>();
        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject dim = new GameObject("Dim");
        dim.transform.SetParent(root.transform, false);
        Image dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.35f);
        RectTransform dimRect = dimImg.rectTransform;
        dimRect.anchorMin = Vector2.zero;
        dimRect.anchorMax = Vector2.one;
        dimRect.offsetMin = Vector2.zero;
        dimRect.offsetMax = Vector2.zero;

        GameObject labelObj = new GameObject("CountdownLabel");
        labelObj.transform.SetParent(root.transform, false);
        TextMeshProUGUI label = labelObj.AddComponent<TextMeshProUGUI>();
        label.fontSize = 220f;
        label.alignment = TextAlignmentOptions.Center;
        PrismaticHudTypography.ApplyCountdownStyle(label, false);
        RectTransform lr = label.rectTransform;
        lr.anchorMin = new Vector2(0.5f, 0.5f);
        lr.anchorMax = new Vector2(0.5f, 0.5f);
        lr.pivot = new Vector2(0.5f, 0.5f);
        lr.sizeDelta = new Vector2(800f, 400f);
        lr.anchoredPosition = Vector2.zero;

        string[] steps = { "3", "2", "1", "GO!" };
        for (int i = 0; i < steps.Length; i++)
        {
            bool isGo = steps[i] == "GO!";
            if (isGo)
                HealthManager.ReleaseStartupProtection();
            if (label != null)
                PrismaticHudTypography.ApplyCountdownStyle(label, isGo);
            yield return new WaitForSecondsRealtime(isGo ? 0.6f : 0.8f);
        }

        EndMatchCinematic.GameplayLocked = false;
        _countdownFinished = true;
        _countdownRunning = false;
        ZeroLocalMovementVectors();

        if (root != null)
            Destroy(root);
    }

    private static void ZeroLocalMovementVectors()
    {
        PlayerController[] players = FindObjectsByType<PlayerController>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            PlayerController pc = players[i];
            if (pc == null)
                continue;
#if PUN_2_OR_NEWER
            PhotonView view = pc.GetComponent<PhotonView>();
            if (view != null && !view.IsMine)
                continue;
#endif
            pc.ClearMovementForMatchLock();
        }
    }
}
