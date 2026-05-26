using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

public static class ApplyEnvironmentParityFix
{
    private const string TestCharacterName = "OOD_TestCharacter";

    [MenuItem("Tools/PRISM/Apply Environment Parity Fix")]
    public static void Run()
    {
        int playersConverted = 0;
        int enemiesGuarded = 0;
        int surfacesBuilt = 0;

        foreach (CharacterController cc in Object.FindObjectsByType<CharacterController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (cc == null) continue;
            GameObject go = cc.gameObject;

            if (go.GetComponent<NavMeshAgent>() != null) continue;

            Component oldGrounded = go.GetComponent("GroundedHorizontalMover");
            if (oldGrounded != null) Undo.DestroyObjectImmediate(oldGrounded);

            Component oldBasic = go.GetComponent("CharacterControllerMover");
            if (oldBasic != null) Undo.DestroyObjectImmediate(oldBasic);

            if (go.GetComponent<KeyboardMovementInput>() == null)
                Undo.AddComponent<KeyboardMovementInput>(go);
            if (go.GetComponent<SphereGroundProbe>() == null)
                Undo.AddComponent<SphereGroundProbe>(go);
            if (go.GetComponent<NavMeshConstrainedMover>() == null)
                Undo.AddComponent<NavMeshConstrainedMover>(go);

            Undo.RecordObject(cc, "Tune CharacterController");
            cc.stepOffset = Mathf.Max(0.4f, cc.stepOffset);
            cc.slopeLimit = Mathf.Max(50f, cc.slopeLimit);
            cc.skinWidth = Mathf.Max(0.02f, cc.skinWidth);
            cc.minMoveDistance = 0f;

            playersConverted++;
        }

        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (agent == null) continue;
            GameObject go = agent.gameObject;

            if (go.name == TestCharacterName) continue;

            if (go.GetComponent<EnemyClipGuard>() == null)
                Undo.AddComponent<EnemyClipGuard>(go);

            enemiesGuarded++;
        }

        foreach (NavMeshSurface s in Object.FindObjectsByType<NavMeshSurface>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s == null) continue;
            s.collectObjects = CollectObjects.All;
            s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            s.BuildNavMesh();
            EditorUtility.SetDirty(s);
            surfacesBuilt++;
        }

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[ParityFix] players converted to NavMeshConstrainedMover={playersConverted}");
        Debug.Log($"[ParityFix] enemies given EnemyClipGuard={enemiesGuarded}");
        Debug.Log($"[ParityFix] NavMeshSurfaces rebuilt={surfacesBuilt}");

        if (surfacesBuilt == 0)
            Debug.LogWarning("[ParityFix] No NavMeshSurface in scene — add one to the arena root and re-run, or bake via Window → AI → Navigation.");
        if (playersConverted == 0)
            Debug.LogWarning("[ParityFix] No player CharacterController found. Make sure your player has a CharacterController component.");
    }
}
