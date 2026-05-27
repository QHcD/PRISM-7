using System.Collections.Generic;
using UnityEngine;

public class CombatVoiceSfx : MonoBehaviour
{
    private static readonly bool VerboseCombatVoiceLogs = false;

    [Header("Clips")]
    public AudioClip[] hurtClips;
    public AudioClip[] deathClips;

    [Header("Audio Settings")]
    public AudioSource source;
    public float minInterval = 0.18f;
    public float minDistance = CombatSfx3D.DefaultMinDistance;
    public float maxDistance = CombatSfx3D.DefaultMaxDistance;

    private float _lastVoiceTime = -100f;
    private bool _hasDied;

    private void Awake()
    {
        EnsureAudioSource();
        EnsureClipsLoaded();
    }

    private void Start()
    {
        EnsureClipsLoaded();
    }

    public static CombatVoiceSfx GetOrAdd(GameObject owner)
    {
        if (owner == null) return null;

        CombatVoiceSfx sfx = owner.GetComponent<CombatVoiceSfx>();
        if (sfx == null)
            sfx = owner.AddComponent<CombatVoiceSfx>();

        sfx.EnsureAudioSource();
        sfx.EnsureClipsLoaded();
        return sfx;
    }

    public void ApplyInspectorClips(AudioClip[] hurt, AudioClip[] death, AudioClip hurtSingle = null, AudioClip deathSingle = null)
    {
        if (hurt != null && hurt.Length > 0)
            hurtClips = hurt;

        if (death != null && death.Length > 0)
            deathClips = death;

        if (hurtSingle != null)
            hurtClips = MergeClips(hurtClips, new[] { hurtSingle });

        if (deathSingle != null)
            deathClips = MergeClips(deathClips, new[] { deathSingle });
    }

    public void EnsureAudioSource()
    {
        if (source == null)
            source = GetComponent<AudioSource>();

        if (source == null)
            source = gameObject.AddComponent<AudioSource>();

        CombatSfx3D.ConfigureCombatSource(source, minDistance, maxDistance);
    }

    public void EnsureClipsLoaded()
    {
        CombatVoiceClipBank.EnsureLoaded();

        if (hurtClips == null || hurtClips.Length == 0)
            hurtClips = ToArray(CombatVoiceClipBank.HurtClips);
        else
            hurtClips = MergeClips(hurtClips, CombatVoiceClipBank.HurtClips);

        if (deathClips == null || deathClips.Length == 0)
            deathClips = ToArray(CombatVoiceClipBank.DeathClips);
        else
            deathClips = MergeClips(deathClips, CombatVoiceClipBank.DeathClips);
    }

    public void PlayHurt()
    {
        if (_hasDied) return;
        if (Time.time - _lastVoiceTime < minInterval) return;

        EnsureAudioSource();
        EnsureClipsLoaded();

        AudioClip clip = PickRandom(hurtClips);
        if (clip == null)
        {
            if (VerboseCombatVoiceLogs)
                Debug.Log($"[CombatVoiceSfx] No hurt/death clips assigned or found on {gameObject.name}");
            return;
        }

        _lastVoiceTime = Time.time;
        float pitch = Random.Range(0.95f, 1.05f);
        PlayVoiceOneShot(clip, pitch, 0.85f);
        if (VerboseCombatVoiceLogs)
            Debug.Log($"[CombatVoiceSfx] Hurt sound played on {gameObject.name}");
    }

    public void PlayDeath()
    {
        if (_hasDied) return;
        _hasDied = true;

        EnsureClipsLoaded();

        AudioClip clip = PickRandom(deathClips);
        if (clip == null)
        {
            if (VerboseCombatVoiceLogs)
                Debug.Log($"[CombatVoiceSfx] No hurt/death clips assigned or found on {gameObject.name}");
            return;
        }

        float pitch = Random.Range(0.95f, 1.05f);
        PlayVoiceOneShot(clip, pitch, 1f);
        if (VerboseCombatVoiceLogs)
            Debug.Log($"[CombatVoiceSfx] Death sound played on {gameObject.name}");
    }

    private void PlayVoiceOneShot(AudioClip clip, float pitch, float baseVolume)
    {
        CombatSfx3D.PlayCombatSfx3D(clip, transform.position, baseVolume, pitch, minDistance, maxDistance);
    }

    private static AudioClip PickRandom(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return null;
        return clips[Random.Range(0, clips.Length)];
    }

    private static AudioClip[] MergeClips(AudioClip[] primary, IReadOnlyList<AudioClip> extra)
    {
        List<AudioClip> merged = new List<AudioClip>(8);
        AddUnique(merged, primary);
        if (extra != null)
        {
            for (int i = 0; i < extra.Count; i++)
                AddUnique(merged, extra[i]);
        }

        return merged.Count > 0 ? merged.ToArray() : null;
    }

    private static AudioClip[] MergeClips(AudioClip[] primary, AudioClip[] extra)
    {
        List<AudioClip> merged = new List<AudioClip>(8);
        AddUnique(merged, primary);
        AddUnique(merged, extra);
        return merged.Count > 0 ? merged.ToArray() : null;
    }

    private static void AddUnique(List<AudioClip> list, AudioClip clip)
    {
        if (clip == null || list.Contains(clip)) return;
        list.Add(clip);
    }

    private static void AddUnique(List<AudioClip> list, AudioClip[] clips)
    {
        if (clips == null) return;
        for (int i = 0; i < clips.Length; i++)
            AddUnique(list, clips[i]);
    }

    private static AudioClip[] ToArray(IReadOnlyList<AudioClip> clips)
    {
        if (clips == null || clips.Count == 0) return null;
        AudioClip[] arr = new AudioClip[clips.Count];
        for (int i = 0; i < clips.Count; i++)
            arr[i] = clips[i];
        return arr;
    }
}
