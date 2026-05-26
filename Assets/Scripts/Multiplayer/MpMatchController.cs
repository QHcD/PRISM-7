using UnityEngine;
using System.Collections;

#if PUN_2_OR_NEWER
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using TMPro;
using UnityEngine.UI;
using Hashtable = ExitGames.Client.Photon.Hashtable;
#endif

/// <summary>
/// Master-client-authoritative match lifecycle manager.
/// Driven authoritatively by the Master Client using Room Custom Properties under key "ms".
/// States:
///   0: WaitingForPlayers (locks input, waits 3 seconds)
///   1: ReadyCheck (auto-verifies player readiness or waits for connection)
///   2: Countdown (5-second countdown with input locked and big countdown overlay)
///   3: InMatch (active gameplay, enables input, spawns bots, ticks down timer)
///   4: MatchEnded (locks input, resolves winner, triggers winners circle overlay)
/// </summary>
#if PUN_2_OR_NEWER
public class MpMatchController : MonoBehaviourPunCallbacks
#else
public class MpMatchController : MonoBehaviour
#endif
{
    public static MpMatchController Instance { get; private set; }

    private bool _matchStarted;
    private byte _currentLocalState = 255;
    private GameObject _countdownOverlayObj;

    // ── Bootstrap ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by NetworkPlayerSpawner on master client after scene load.
    /// Creates the controller if it doesn't exist yet.
    /// </summary>
    public static void EnsureExists()
    {
        // Stable-test mode kill-switch: do not even create the controller
        // when match rules are disabled. Nothing here runs until the user
        // flips MpMatchRules.Enabled back to true.
        if (!MpMatchRules.Enabled) return;
        if (Instance != null) return;
        var go = new GameObject("MpMatchController");
        go.AddComponent<MpMatchController>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

#if PUN_2_OR_NEWER
        // PhotonView required for callbacks.
        if (GetComponent<PhotonView>() == null)
        {
            PhotonView pv = gameObject.AddComponent<PhotonView>();
            pv.ViewID = 999; // reserved ID — safe for a manager-only object
        }
#endif
    }

    private void Start()
    {
        if (!MpMatchRules.Enabled)
        {
            // Belt-and-braces: even if an old scene-placed controller was
            // dragged in, force it inert when rules are disabled.
            EndMatchCinematic.GameplayLocked = false;
            enabled = false;
            return;
        }
#if PUN_2_OR_NEWER
        if (!PhotonNetwork.InRoom) return;

        // Apply room config to local state (mode/level/bots).
        MpRoomConfig.ApplyToLocalState();

        if (PhotonNetwork.IsMasterClient)
        {
            StartCoroutine(MasterLifecycleCoroutine());
        }
        else
        {
            // Sync initial state locally
            ApplyStateLocally(MpRoomConfig.ReadMatchState());
        }
#endif
    }

#if PUN_2_OR_NEWER
    private IEnumerator MasterLifecycleCoroutine()
    {
        Debug.Log("[MpMatch] Starting authoritative MasterLifecycleCoroutine");

        byte currentState = MpRoomConfig.ReadMatchState();

        // 1. WaitingForPlayers
        if (currentState == 0)
        {
            var props = new Hashtable { { MpRoomConfig.KeyMatchState, (byte)0 } };
            WriteRoomProperties(props, "MpMatchController.MasterLifecycle WaitingForPlayers");
            yield return new WaitForSeconds(3.0f);
            currentState = 1;
        }

        // 2. ReadyCheck
        if (currentState == 1)
        {
            var props = new Hashtable { { MpRoomConfig.KeyMatchState, (byte)1 } };
            WriteRoomProperties(props, "MpMatchController.MasterLifecycle ReadyCheck");
            
            bool allReady = false;
            while (!allReady)
            {
                allReady = true;
                foreach (var p in PhotonNetwork.PlayerList)
                {
                    if (p.CustomProperties.TryGetValue("rd", out object rd))
                    {
                        if (!(bool)rd)
                        {
                            allReady = false;
                            break;
                        }
                    }
                    else
                    {
                        allReady = false;
                        break;
                    }
                }
                if (!allReady) yield return new WaitForSeconds(0.5f);
            }
            currentState = 2;
        }

        // 3. Countdown
        if (currentState == 2)
        {
            var props = new Hashtable { { MpRoomConfig.KeyMatchState, (byte)2 } };
            WriteRoomProperties(props, "MpMatchController.MasterLifecycle Countdown");
            
            float startCountdown = MpRoomConfig.ReadTimerDuration();
            if (startCountdown <= 0 || startCountdown > 5) startCountdown = 5;
            
            for (int i = (int)startCountdown; i > 0; i--)
            {
                var propsTimer = new Hashtable { { MpRoomConfig.KeyTimerDuration, (float)i } };
                WriteRoomProperties(propsTimer, "MpMatchController.MasterLifecycle CountdownTimer");
                yield return new WaitForSeconds(1.0f);
            }
            currentState = 3;
        }

        // 4. InMatch
        if (currentState == 3)
        {
            var props = new Hashtable { { MpRoomConfig.KeyMatchState, (byte)3 } };
            WriteRoomProperties(props, "MpMatchController.MasterLifecycle InMatch");

            // Enable bots if active mode is not PurePvP
            BootBotDirector(MultiplayerRuntimeConfig.GetSelectedGameMode());

            float matchTimeRemaining = MpRoomConfig.ReadTimerDuration();
            if (matchTimeRemaining <= 0 || matchTimeRemaining > 300f) matchTimeRemaining = 300f;
            
            string winner = "";
            while (matchTimeRemaining > 0)
            {
                if (CheckMatchEnded(out winner))
                {
                    break;
                }

                yield return new WaitForSeconds(1.0f);
                matchTimeRemaining -= 1.0f;
                if (matchTimeRemaining < 0) matchTimeRemaining = 0;

                var propsTimer = new Hashtable { { MpRoomConfig.KeyTimerDuration, matchTimeRemaining } };
                WriteRoomProperties(propsTimer, "MpMatchController.MasterLifecycle MatchTimer");
            }

            // 5. MatchEnded
            if (string.IsNullOrEmpty(winner))
            {
                if (MultiplayerMode.ActiveMode == MpGameMode.CoopSurvival)
                {
                    winner = "HUMANS";
                }
                else
                {
                    var leaders = MatchStatsManager.Instance.GetTopCombatants(1);
                    if (leaders != null && leaders.Count > 0)
                        winner = leaders[0].DisplayName;
                    else
                        winner = "—";
                }
            }

            {
                var propsEnd = new Hashtable 
                { 
                    { MpRoomConfig.KeyWinnerName, winner },
                    { MpRoomConfig.KeyMatchState, (byte)4 }
                };
                WriteRoomProperties(propsEnd, "MpMatchController.MasterLifecycle MatchEnded");
            }
        }
    }

    private static void WriteRoomProperties(Hashtable props, string context)
    {
        if (!MultiplayerShutdownGuard.CanWriteProperties(context, requireRoom: true))
            return;

        MultiplayerShutdownGuard.LogPropertyWrite(context);
        PhotonNetwork.CurrentRoom.SetCustomProperties(props);
    }

    private bool CheckMatchEnded(out string winner)
    {
        winner = string.Empty;
        if (MatchStatsManager.Instance == null) return false;

        MpGameMode mode = MultiplayerMode.ActiveMode;

        // Get alive and registered counts
        int alivePlayers = 0;
        int registeredPlayers = 0;
        int aliveBots = 0;
        int registeredBots = 0;
        int aliveCombatants = 0;

        var allCombatants = MatchStatsManager.Instance.GetTopCombatants(100);

        foreach (var c in allCombatants)
        {
            if (c.IsPlayer)
            {
                registeredPlayers++;
                if (c.IsAlive)
                {
                    alivePlayers++;
                    aliveCombatants++;
                }
            }
            else
            {
                registeredBots++;
                if (c.IsAlive)
                {
                    aliveBots++;
                    aliveCombatants++;
                }
            }
        }

        if (mode == MpGameMode.CoopSurvival)
        {
            // Lose condition: All human players eliminated
            if (registeredPlayers > 0 && alivePlayers == 0)
            {
                winner = "BOTS";
                return true;
            }
            // Win condition: All bots eliminated
            if (registeredBots > 0 && aliveBots == 0)
            {
                winner = "HUMANS";
                return true;
            }
        }
        else if (mode == MpGameMode.HybridChaos || mode == MpGameMode.PurePvP)
        {
            // FFA Mode win condition: Last Alive wins if there are multiple players
            if (registeredPlayers > 1 && aliveCombatants == 1)
            {
                foreach (var c in allCombatants)
                {
                    if (c.IsAlive)
                    {
                        winner = c.DisplayName;
                        return true;
                    }
                }
            }
        }

        return false;
    }
#endif

    // ── Bot director ─────────────────────────────────────────────────────────

    private void BootBotDirector(MpGameMode mode)
    {
        if (mode == MpGameMode.PurePvP)
        {
            Debug.Log("[MPMode] PurePvP active - skipping bot director");
            return;
        }

        if (mode == MpGameMode.HybridChaos)
            Debug.Log("[MPMode] AI enabled for mode = HybridChaos");
        else if (mode == MpGameMode.CoopSurvival)
            Debug.Log("[MPMode] AI enabled for mode = CoOpSurvival");

        int botCount = MpRoomConfig.ReadBotCount();
        MpBotDirector.EnsureExists(mode, botCount);
    }

    // ── Photon callbacks ─────────────────────────────────────────────────────

#if PUN_2_OR_NEWER
    public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable changedProps)
    {
        if (!MpMatchRules.Enabled) return;
        // Re-apply config whenever master updates room properties.
        if (changedProps.ContainsKey(MpRoomConfig.KeyMode))
            MpRoomConfig.ApplyToLocalState();

        if (changedProps.ContainsKey(MpRoomConfig.KeyMatchState))
        {
            byte state = (byte)changedProps[MpRoomConfig.KeyMatchState];
            ApplyStateLocally(state);
        }
    }

    private void ApplyStateLocally(byte state)
    {
        if (_currentLocalState == state) return;
        _currentLocalState = state;

        Debug.Log($"[MpMatch] Local state transitioned to: {state}");

        switch (state)
        {
            case 0: // WaitingForPlayers
                EndMatchCinematic.GameplayLocked = true;
                break;
            case 1: // ReadyCheck
                EndMatchCinematic.GameplayLocked = true;
                break;
            case 2: // Countdown
                HealthManager.BeginStartupProtection();
                EndMatchCinematic.GameplayLocked = true;
                StartCoroutine(ShowCountdownOverlay());
                break;
            case 3: // InMatch
                HealthManager.ReleaseStartupProtection();
                EndMatchCinematic.GameplayLocked = false;
                break;
            case 4: // MatchEnded
                EndMatchCinematic.GameplayLocked = true;
                TriggerEndCinematic();
                break;
        }
    }

    private IEnumerator ShowCountdownOverlay()
    {
        if (_countdownOverlayObj != null) Destroy(_countdownOverlayObj);

        Canvas canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) yield break;

        _countdownOverlayObj = new GameObject("CountdownOverlay");
        _countdownOverlayObj.transform.SetParent(canvas.transform, false);

        RectTransform rect = _countdownOverlayObj.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        Image bg = _countdownOverlayObj.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.45f);

        GameObject textGo = new GameObject("CountdownText");
        textGo.transform.SetParent(_countdownOverlayObj.transform, false);
        TextMeshProUGUI tmp = textGo.AddComponent<TextMeshProUGUI>();
        
        // Find custom font from RuntimeMenuBuilder if any, or default
        var menu = FindFirstObjectByType<RuntimeMenuBuilder>();
        if (menu != null && menu.customFont != null)
            tmp.font = menu.customFont;

        tmp.fontSize = 74;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = new Color(0.35f, 0.75f, 1f, 1f);

        RectTransform tRect = textGo.GetComponent<RectTransform>();
        tRect.anchorMin = new Vector2(0.1f, 0.4f);
        tRect.anchorMax = new Vector2(0.9f, 0.6f);
        tRect.offsetMin = tRect.offsetMax = Vector2.zero;

        float lastVal = -1f;
        while (_currentLocalState == 2)
        {
            float rem = MpRoomConfig.ReadTimerDuration();
            if (!Mathf.Approximately(rem, lastVal))
            {
                lastVal = rem;
                tmp.text = $"MATCH STARTS IN\n<size=112>{Mathf.CeilToInt(rem)}</size>";
                textGo.transform.localScale = new Vector3(1.2f, 1.2f, 1f);
            }
            textGo.transform.localScale = Vector3.Lerp(textGo.transform.localScale, Vector3.one, Time.deltaTime * 8f);
            yield return null;
        }

        float elapsed = 0f;
        CanvasGroup cg = _countdownOverlayObj.AddComponent<CanvasGroup>();
        if (cg == null) cg = _countdownOverlayObj.AddComponent<CanvasGroup>();
        while (elapsed < 0.22f)
        {
            elapsed += Time.deltaTime;
            cg.alpha = 1f - (elapsed / 0.22f);
            yield return null;
        }

        Destroy(_countdownOverlayObj);
    }

    private void TriggerEndCinematic()
    {
        if (_countdownOverlayObj != null) Destroy(_countdownOverlayObj);

        string winner = MpRoomConfig.ReadWinnerName();
        MpGameMode mode = MultiplayerMode.ActiveMode;

        bool playerWon = false;
        if (mode == MpGameMode.CoopSurvival)
        {
            playerWon = string.Equals(winner, "HUMANS", System.StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            string localName = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.NickName : "";
            if (string.IsNullOrWhiteSpace(localName) && PhotonNetwork.LocalPlayer != null)
                localName = $"Player_{PhotonNetwork.LocalPlayer.ActorNumber}";
            playerWon = string.Equals(localName, winner, System.StringComparison.OrdinalIgnoreCase);
        }

        Debug.Log($"[MpMatch] MatchEnded! Winner={winner}, PlayerWon={playerWon}");

        EndMatchCinematic.Begin(playerWon, () => {
            Debug.Log("[MpMatch] EndMatchCinematic finished — returning to MainMenu");
            if (PhotonNetwork.InRoom && PhotonNetwork.IsConnectedAndReady) PhotonNetwork.LeaveRoom();
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        });
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        if (!MpMatchRules.Enabled) return;
        if (PhotonNetwork.IsMasterClient)
        {
            Debug.Log("[MpMatch] became master — resuming match lifecycle");
            StopAllCoroutines();
            StartCoroutine(MasterLifecycleCoroutine());
        }
    }
#endif

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
