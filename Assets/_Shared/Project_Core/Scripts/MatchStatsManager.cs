using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Tracks combatant registrations and simple kill leaderboard data for the current match.
/// </summary>
public class MatchStatsManager : MonoBehaviour
{
    public event System.Action StatsChanged;

    public struct CombatantSnapshot
    {
        public string    Id;
        public string    DisplayName;
        public int       Kills;
        public int       Deaths;
        public int       Score;
        public bool      IsPlayer;
        public bool      IsAlive;
        /// <summary>
        /// Live world transform of the combatant (player root or enemy root).
        /// May be null after the entity has been destroyed (e.g. enemy
        /// pooled out of the scene). The end-match cinematic uses this to
        /// orbit the camera around the top three.
        /// </summary>
        public Transform Transform;
    }

    private sealed class CombatantData
    {
        public string    Id;
        public string    DisplayName;
        public int       Kills;
        public int       Deaths;
        public bool      IsPlayer;
        public bool      IsAlive;
        public Transform Transform;

        /// <summary>Score is derived: 100 per kill, -25 per death (floor 0).</summary>
        public int Score => Mathf.Max(0, Kills * 100 - Deaths * 25);
    }

    public static MatchStatsManager Instance { get; private set; }

    private readonly Dictionary<string, CombatantData> _combatants = new Dictionary<string, CombatantData>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null)
            return;

        GameObject managerObject = new GameObject("MatchStatsManager");
        DontDestroyOnLoad(managerObject);
        Instance = managerObject.AddComponent<MatchStatsManager>();
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            return;
        }

        if (Instance != this)
            Destroy(gameObject);
    }

    public static string BuildCombatantId(Component component)
    {
        if (component == null)
            return string.Empty;

        return $"{component.gameObject.scene.name}:{component.GetInstanceID()}";
    }

    public void ResetMatch()
    {
        _combatants.Clear();
        NotifyStatsChanged();
    }

    public void RegisterCombatant(string id, string displayName, bool isPlayer, Transform transform = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        // SP local-player dedupe: HUDManager.InitializeSinglePlayerGameplay,
        // PlayerHealth.Awake, and any future ensure-path can each call this for
        // the same local player. They each pass a different InstanceID-based ID
        // (BuildCombatantId(this) vs BuildCombatantId(playerHealth) etc.) — in
        // single-player there is one and only one local player, so we collapse
        // any existing isPlayer entry onto the incoming id rather than spawning
        // a second "Hamed" row. Multiplayer keeps per-actor IDs (photon:N),
        // so we skip dedupe there.
        if (isPlayer && !MultiplayerMode.IsMultiplayer)
        {
            string existingPlayerKey = null;
            foreach (var kvp in _combatants)
            {
                if (kvp.Value != null && kvp.Value.IsPlayer)
                {
                    existingPlayerKey = kvp.Key;
                    break;
                }
            }

            if (existingPlayerKey != null && existingPlayerKey != id)
            {
                CombatantData previous = _combatants[existingPlayerKey];
                _combatants.Remove(existingPlayerKey);
                previous.Id = id;
                _combatants[id] = previous;
            }
        }

        if (!_combatants.TryGetValue(id, out CombatantData combatant))
        {
            combatant = new CombatantData
            {
                Id = id
            };
            _combatants[id] = combatant;
        }

        combatant.DisplayName = string.IsNullOrWhiteSpace(displayName) ? (isPlayer ? "PLAYER" : "COMBATANT") : displayName.ToUpperInvariant();
        combatant.IsPlayer  = isPlayer;
        combatant.IsAlive   = true;
        if (transform != null) combatant.Transform = transform;
        NotifyStatsChanged();
    }

    public void MarkEliminated(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (_combatants.TryGetValue(id, out CombatantData combatant))
        {
            // Only count the *first* elimination — re-registering the same
            // id (e.g. respawn) would otherwise double-count the death.
            if (combatant.IsAlive)
            {
                combatant.Deaths += 1;
                combatant.IsAlive = false;
                NotifyStatsChanged();
            }
        }
    }

    public void RecordKill(string killerId)
    {
        if (string.IsNullOrWhiteSpace(killerId))
            return;

        if (_combatants.TryGetValue(killerId, out CombatantData combatant))
        {
            combatant.Kills++;
            NotifyStatsChanged();
        }
    }

    public IReadOnlyList<CombatantSnapshot> GetTopCombatants(int count)
    {
        if (count <= 0)
            return System.Array.Empty<CombatantSnapshot>();

        return _combatants.Values
            .OrderByDescending(entry => entry.Kills)
            .ThenBy(entry => entry.Deaths)
            .ThenByDescending(entry => entry.IsPlayer)
            .ThenBy(entry => entry.DisplayName)
            .Take(count)
            .Select(entry => new CombatantSnapshot
            {
                Id          = entry.Id,
                DisplayName = entry.DisplayName,
                Kills       = entry.Kills,
                Deaths      = entry.Deaths,
                Score       = entry.Score,
                IsPlayer    = entry.IsPlayer,
                IsAlive     = entry.IsAlive,
                Transform   = entry.Transform,
            })
            .ToArray();
    }

    public IReadOnlyList<CombatantSnapshot> GetTopPlayers(int count)
    {
        if (count <= 0)
            return System.Array.Empty<CombatantSnapshot>();

        return _combatants.Values
            .Where(entry => entry.IsPlayer)
            .OrderByDescending(entry => entry.Kills)
            .ThenBy(entry => entry.Deaths)
            .ThenBy(entry => entry.DisplayName)
            .Take(count)
            .Select(entry => new CombatantSnapshot
            {
                Id          = entry.Id,
                DisplayName = entry.DisplayName,
                Kills       = entry.Kills,
                Deaths      = entry.Deaths,
                Score       = entry.Score,
                IsPlayer    = entry.IsPlayer,
                IsAlive     = entry.IsAlive,
                Transform   = entry.Transform,
            })
            .ToArray();
    }

    public int GetRegisteredCombatantCount()
    {
        return _combatants.Count;
    }

    public int GetRegisteredPlayerCount()
    {
        return _combatants.Values.Count(entry => entry.IsPlayer);
    }

    private void NotifyStatsChanged()
    {
        StatsChanged?.Invoke();
    }
}
