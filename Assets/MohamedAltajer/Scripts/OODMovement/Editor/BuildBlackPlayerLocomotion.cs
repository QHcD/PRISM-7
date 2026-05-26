using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildBlackPlayerLocomotion
{
    private const string ControllerDir = "Assets/MohamedAltajer/Scripts/OODMovement/Generated";
    private const string ControllerPath = ControllerDir + "/BlackPlayerLocomotion.controller";
    private const string TargetName = "BlackPlayer";

    [MenuItem("Tools/PRISM/Build BlackPlayer Locomotion (CC Velocity + 2D Blend + IK)")]
    public static void Run()
    {
        AnimatorController ctrl = BuildController();
        ApplyToBlackPlayer(ctrl);
    }

    private static AnimatorController BuildController()
    {
        if (!Directory.Exists(ControllerDir)) Directory.CreateDirectory(ControllerDir);

        AnimatorController ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ctrl != null) AssetDatabase.DeleteAsset(ControllerPath);

        ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

        ctrl.AddParameter("MoveSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Direction", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("VelocityX", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("VelocityZ", AnimatorControllerParameterType.Float);
        ctrl.AddParameter(new AnimatorControllerParameter
        {
            name = "IsGrounded",
            type = AnimatorControllerParameterType.Bool,
            defaultBool = true
        });

        AnimatorControllerLayer baseLayer = ctrl.layers[0];
        baseLayer.iKPass = true;
        ctrl.layers = new[] { baseLayer };

        AnimatorStateMachine sm = baseLayer.stateMachine;
        sm.name = "Locomotion";

        BlendTree tree;
        AnimatorState locomotion = ctrl.CreateBlendTreeInController("Locomotion", out tree, 0);
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "VelocityX";
        tree.blendParameterY = "VelocityZ";
        tree.useAutomaticThresholds = false;

        AddChild(tree, "Idle",       new Vector2( 0f,  0f));
        AddChild(tree, "WalkFwd",    new Vector2( 0f,  0.5f));
        AddChild(tree, "RunFwd",     new Vector2( 0f,  1f));
        AddChild(tree, "WalkBack",   new Vector2( 0f, -0.6f));
        AddChild(tree, "StrafeL",    new Vector2(-1f,  0f));
        AddChild(tree, "StrafeR",    new Vector2( 1f,  0f));
        AddChild(tree, "WalkFwdL",   new Vector2(-0.7f, 0.7f));
        AddChild(tree, "WalkFwdR",   new Vector2( 0.7f, 0.7f));

        sm.defaultState = locomotion;

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[BlackPlayer] Built AnimatorController at {ControllerPath} (FreeformDirectional2D, IK Pass=ON, params: MoveSpeed/Direction/VelocityX/VelocityZ/IsGrounded)");
        return ctrl;
    }

    private static void AddChild(BlendTree tree, string slotName, Vector2 pos)
    {
        ChildMotion[] children = tree.children;
        System.Array.Resize(ref children, children.Length + 1);
        children[children.Length - 1] = new ChildMotion
        {
            motion = null,
            position = pos,
            threshold = pos.magnitude,
            timeScale = 1f,
            directBlendParameter = "MoveSpeed"
        };
        tree.children = children;
    }

    private static void ApplyToBlackPlayer(AnimatorController ctrl)
    {
        GameObject root = GameObject.Find(TargetName);
        if (root == null)
        {
            EditorUtility.DisplayDialog(
                "BlackPlayer not found",
                $"No GameObject named '{TargetName}' found in the active scene. " +
                "The controller asset was created, but it was not attached. " +
                "Open the scene that contains BlackPlayer and re-run this menu, " +
                "or assign the controller manually to your player's Animator.",
                "OK");
            return;
        }

        if (root.GetComponent<CharacterController>() == null)
        {
            CharacterController cc = Undo.AddComponent<CharacterController>(root);
            cc.center = new Vector3(0f, 1.0f, 0f);
            cc.radius = 0.35f;
            cc.height = 1.8f;
            cc.stepOffset = 0.4f;
            cc.slopeLimit = 50f;
            cc.skinWidth = 0.02f;
            cc.minMoveDistance = 0f;
        }

        if (root.GetComponent<KeyboardMovementInput>() == null)
            Undo.AddComponent<KeyboardMovementInput>(root);
        if (root.GetComponent<SphereGroundProbe>() == null)
            Undo.AddComponent<SphereGroundProbe>(root);

        LocomotionManager lm = root.GetComponent<LocomotionManager>();
        if (lm == null) lm = Undo.AddComponent<LocomotionManager>(root);

        Animator anim = root.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            Undo.RecordObject(anim, "Configure BlackPlayer Animator");
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            SerializedObject so = new SerializedObject(anim);
            SerializedProperty culling = so.FindProperty("m_CullingMode");
            if (culling != null) culling.enumValueIndex = (int)AnimatorCullingMode.AlwaysAnimate;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
        Debug.Log($"[BlackPlayer] Locomotion stack applied: LocomotionManager + KeyboardMovementInput + SphereGroundProbe. " +
                  $"Animator → applyRootMotion=OFF, IK Pass=ON, BlendTree=FreeformDirectional2D. " +
                  $"Assign Idle/Walk/Run/Strafe clips to the blend tree children to activate motion.");
    }
}
