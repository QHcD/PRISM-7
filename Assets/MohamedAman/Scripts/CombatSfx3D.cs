using UnityEngine;

public static class CombatSfx3D
{
    public const float DefaultMinDistance = 3f;
    public const float DefaultMaxDistance = 24f;

    public static bool debugCombatSfx = false;

    public static void ConfigureCombatSource(
        AudioSource source,
        float minDistance = DefaultMinDistance,
        float maxDistance = DefaultMaxDistance)
    {
        if (source == null)
            return;

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = Mathf.Max(0.1f, minDistance);
        source.maxDistance = Mathf.Max(source.minDistance + 0.1f, maxDistance);
        source.dopplerLevel = 0f;
    }

    public static bool PlayCombatSfx3D(
        AudioClip clip,
        Vector3 position,
        float baseVolume = 1f,
        float pitch = 1f,
        float minDistance = DefaultMinDistance,
        float maxDistance = DefaultMaxDistance)
    {
        if (clip == null || !IsFinite(position))
            return false;

        float distance = DistanceToListener(position);
        if (distance > maxDistance)
        {
            Log("skipped", clip, position, distance, maxDistance);
            return false;
        }

        GameObject host = new GameObject("CombatSfx3D_" + clip.name);
        host.transform.position = position;

        AudioSource source = host.AddComponent<AudioSource>();
        ConfigureCombatSource(source, minDistance, maxDistance);
        source.clip = clip;
        source.volume = AudioSettingsRuntime.ScaledSfx(Mathf.Max(0f, baseVolume));
        source.pitch = Mathf.Clamp(pitch, 0.25f, 3f);
        source.Play();

        float life = Mathf.Max(0.1f, clip.length / Mathf.Max(0.1f, Mathf.Abs(source.pitch))) + 0.1f;
        Object.Destroy(host, life);

        Log("played", clip, position, distance, maxDistance);
        return true;
    }

    private static float DistanceToListener(Vector3 position)
    {
        AudioListener listener = Object.FindFirstObjectByType<AudioListener>();
        if (listener != null)
            return Vector3.Distance(listener.transform.position, position);

        Camera cam = Camera.main;
        return cam != null ? Vector3.Distance(cam.transform.position, position) : 0f;
    }

    private static void Log(string action, AudioClip clip, Vector3 position, float distance, float maxDistance)
    {
        if (!debugCombatSfx)
            return;

        Debug.Log($"[CombatSfx3D] {action} clip={clip.name} pos={position} listenerDistance={distance:F1} max={maxDistance:F1}");
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
