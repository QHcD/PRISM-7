using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UpgradeOODCharacterToGrounded
{
    private const string TargetName = "OOD_TestCharacter";

    [MenuItem("Tools/PRISM/Upgrade OOD Character To Grounded Mover")]
    public static void Run()
    {
        GameObject root = GameObject.Find(TargetName);
        if (root == null)
        {
            EditorUtility.DisplayDialog("Not Found",
                $"No GameObject named '{TargetName}' in the active scene. " +
                "Run 'Spawn OOD Test Character' first, or rename your player to that.",
                "OK");
            return;
        }

        Component oldMover = root.GetComponent("CharacterControllerMover");
        if (oldMover != null)
        {
            Undo.DestroyObjectImmediate(oldMover);
        }

        Component oldCoordinator = root.GetComponent("MovementAnimationCoordinator");
        if (oldCoordinator != null)
        {
            Undo.DestroyObjectImmediate(oldCoordinator);
        }

        if (root.GetComponent<KeyboardMovementInput>() == null)
            Undo.AddComponent<KeyboardMovementInput>(root);

        if (root.GetComponent<SphereGroundProbe>() == null)
            Undo.AddComponent<SphereGroundProbe>(root);

        if (root.GetComponent<GroundedHorizontalMover>() == null)
            Undo.AddComponent<GroundedHorizontalMover>(root);

        CharacterController cc = root.GetComponent<CharacterController>();
        if (cc != null)
        {
            Undo.RecordObject(cc, "Tune CharacterController");
            cc.stepOffset = Mathf.Max(0.4f, cc.stepOffset);
            cc.slopeLimit = Mathf.Max(50f, cc.slopeLimit);
            cc.skinWidth = Mathf.Max(0.02f, cc.skinWidth);
            cc.minMoveDistance = 0f;
        }

        Animator anim = root.GetComponentInChildren<Animator>();
        if (anim != null && anim.GetComponent<VelocityAnimatorDriver>() == null)
        {
            Undo.AddComponent<VelocityAnimatorDriver>(anim.gameObject);
        }

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
        Debug.Log("[OOD] Upgraded character to GroundedHorizontalMover + SphereGroundProbe. " +
                  "Press Play and test forward movement — character should walk, not fly.");
    }
}
