using UnityEngine;
using UnityEngine.InputSystem;

public sealed class HoldTabMinimapCamera : MonoBehaviour
{
    [SerializeField] private Camera minimapCamera;

    private void Reset()
    {
        GameObject obj = GameObject.Find("MinimapCamera");
        minimapCamera = obj != null ? obj.GetComponent<Camera>() : null;
    }

    private void Awake()
    {
        if (minimapCamera != null)
            minimapCamera.enabled = false;
    }

    private void Update()
    {
        if (minimapCamera == null) return;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.tabKey.wasPressedThisFrame)
                minimapCamera.enabled = true;

            if (kb.tabKey.wasReleasedThisFrame)
                minimapCamera.enabled = false;
        }
    }
}

