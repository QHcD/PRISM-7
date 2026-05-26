using UnityEngine;

public class WallColliderEnforcer : MonoBehaviour, INavObstacle
{
    [SerializeField] private bool ensureBoxCollider = true;
    [SerializeField] private bool disableIfTrigger = true;
    [SerializeField] private string desiredLayer = "Environment";

    public GameObject GameObject => gameObject;
    public Bounds WorldBounds => ResolveBounds();
    public bool BlocksAgents => true;

    private void OnEnable()
    {
        if (ensureBoxCollider) EnsureCollider();
        if (disableIfTrigger) ForceSolid();
        ApplyLayer();
    }

    private void EnsureCollider()
    {
        Collider any = GetComponent<Collider>();
        if (any != null) return;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null) return;

        BoxCollider box = gameObject.AddComponent<BoxCollider>();
        box.isTrigger = false;
        box.center = mf.sharedMesh != null ? mf.sharedMesh.bounds.center : Vector3.zero;
        box.size   = mf.sharedMesh != null ? mf.sharedMesh.bounds.size   : Vector3.one;
    }

    private void ForceSolid()
    {
        foreach (Collider c in GetComponents<Collider>())
        {
            if (c != null && c.isTrigger) c.isTrigger = false;
        }
    }

    private void ApplyLayer()
    {
        int layer = LayerMask.NameToLayer(desiredLayer);
        if (layer >= 0 && gameObject.layer != layer) gameObject.layer = layer;
    }

    private Bounds ResolveBounds()
    {
        Collider c = GetComponent<Collider>();
        if (c != null) return c.bounds;
        Renderer r = GetComponent<Renderer>();
        if (r != null) return r.bounds;
        return new Bounds(transform.position, Vector3.one);
    }
}
