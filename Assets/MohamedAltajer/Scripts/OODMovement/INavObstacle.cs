using UnityEngine;

public interface INavObstacle
{
    GameObject GameObject { get; }
    Bounds WorldBounds { get; }
    bool BlocksAgents { get; }
}
