using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Professional one-click setup script to configure Player Controller with natural 2D Locomotion (walk forward, run forward, walk backward, strafe left/right).
/// Menu: Tools > PRISM-7 > Setup Player FBX Animations
/// </summary>
public static class PlayerFBXAnimatorSetup
{
    private const string PlayerControllerPath = "Assets/Murtadha/Prefabs/FirstPersonMelee/Controllers/Player Controller.controller";
    private const string PlayerPrefabPath = "Assets/Murtadha/Prefabs/FirstPersonMelee/Resources/FirstPersonMelee/Player.prefab";
    private const string MaterialsDir = "Assets/AliAlhawaj/Materials";

    [MenuItem("Tools/PRISM-7/Setup Player FBX Animations")]
    public static void RunSetup()
    {
        // 1. Load and Backup Animator Controller
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(PlayerControllerPath);
        if (controller == null)
        {
            EditorUtility.DisplayDialog("Player Animator Setup",
                "Could not find Player Controller at:\n" + PlayerControllerPath, "OK");
            return;
        }

        string backup = BackupController(PlayerControllerPath);
        Debug.Log("[PlayerFBXAnimatorSetup] Backup written: " + backup);

        // 2. Add necessary parameters
        AddParameter(controller, "Speed", AnimatorControllerParameterType.Float);
        AddParameter(controller, "VelocityX", AnimatorControllerParameterType.Float);
        AddParameter(controller, "VelocityZ", AnimatorControllerParameterType.Float);
        AddParameter(controller, "IsGrounded", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "IsSprinting", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "IsProne", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "IsSliding", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "IsAttacking", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "Attack", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "Hit", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "Dead", AnimatorControllerParameterType.Trigger);

        if (controller.layers == null || controller.layers.Length == 0)
        {
            Debug.LogError("[PlayerFBXAnimatorSetup] Controller has no layers!");
            return;
        }

        var rootSM = controller.layers[0].stateMachine;

        // 3. Load Clips (prefer natural Running.fbx for player over aggressive run)
        var idleClip = LoadFirstClip(MaterialsDir + "/Idle.fbx");
        var walkClip = LoadFirstClip(MaterialsDir + "/Walking.fbx");
        var walkBackClip = LoadFirstClip(MaterialsDir + "/Walking Backwards.fbx");
        var runClip = LoadFirstClip(MaterialsDir + "/Running.fbx") ?? LoadFirstClip(MaterialsDir + "/Sword And Shield Run.fbx");
        var strafeLeftClip = LoadFirstClip(MaterialsDir + "/Left Strafe.fbx") ?? LoadFirstClip(MaterialsDir + "/Strafe Left.fbx");
        var strafeRightClip = LoadFirstClip(MaterialsDir + "/Right Strafe.fbx") ?? LoadFirstClip(MaterialsDir + "/Strafe Right.fbx");
        var attackClip = LoadFirstClip(MaterialsDir + "/Sword And Shield Slash.fbx") ?? LoadFirstClip(MaterialsDir + "/Punching.fbx");
        var hitClip = LoadFirstClip(MaterialsDir + "/Hit Reaction.fbx");
        var deathClip = LoadFirstClip(MaterialsDir + "/Dying.fbx") ?? LoadFirstClip(MaterialsDir + "/Falling Back Death.fbx");

        if (idleClip == null) Debug.LogWarning("[PlayerFBXAnimatorSetup] Missing Idle.fbx");
        if (walkClip == null) Debug.LogWarning("[PlayerFBXAnimatorSetup] Missing Walking.fbx");
        if (runClip == null) Debug.LogWarning("[PlayerFBXAnimatorSetup] Missing Running.fbx");
        if (attackClip == null) Debug.LogWarning("[PlayerFBXAnimatorSetup] Missing Attack clip");

        // 4. Create or Get Locomotion Blend Tree State
        var locomotionState = GetOrCreateLocomotionState(rootSM, controller, idleClip, walkClip, walkBackClip, runClip, strafeLeftClip, strafeRightClip);

        // 5. Create Combat / Hit / Death States
        var attackState = GetOrCreateState(rootSM, "Attack", attackClip, new Vector3(500, 0, 0));
        var hitState = GetOrCreateState(rootSM, "Hit", hitClip, new Vector3(500, 100, 0));
        var deathState = GetOrCreateState(rootSM, "Death", deathClip, new Vector3(500, 200, 0));

        // 6. Wire Transitions
        EnsureAnyStateTransition(rootSM, attackState, "Attack", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);
        EnsureReturnToLocomotionTransition(attackState, locomotionState, exitTimeNormalized: 0.85f, duration: 0.15f);

        EnsureAnyStateTransition(rootSM, hitState, "Hit", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);
        EnsureReturnToLocomotionTransition(hitState, locomotionState, exitTimeNormalized: 0.85f, duration: 0.15f);

        EnsureAnyStateTransition(rootSM, deathState, "Dead", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);

        ForcePlayerPrefabAnimatorSettings(controller);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Player Animator Setup",
            "Player Animator Controller successfully configured with 2D Locomotion and FBX animations!", "OK");
    }

    private static string BackupController(string controllerPath)
    {
        string dir = Path.GetDirectoryName(controllerPath).Replace('\\', '/');
        string name = Path.GetFileNameWithoutExtension(controllerPath);
        string backupDir = dir + "/Backups";
        if (!AssetDatabase.IsValidFolder(backupDir))
            AssetDatabase.CreateFolder(dir, "Backups");
        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string target = backupDir + "/" + name + "_backup_" + stamp + ".controller";
        AssetDatabase.CopyAsset(controllerPath, target);
        return target;
    }

    private static void AddParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
    {
        foreach (var p in controller.parameters)
            if (p.name == name) return;
        controller.AddParameter(name, type);
    }

    private static AnimationClip LoadFirstClip(string assetPath)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        foreach (var a in assets)
        {
            var clip = a as AnimationClip;
            if (clip != null && !clip.name.StartsWith("__preview__"))
                return clip;
        }
        return null;
    }

    private static AnimatorState GetOrCreateLocomotionState(AnimatorStateMachine sm, AnimatorController controller,
        AnimationClip idle, AnimationClip walk, AnimationClip walkBack, AnimationClip run, AnimationClip strafeLeft, AnimationClip strafeRight)
    {
        // Try to find existing Locomotion state
        foreach (var cs in sm.states)
        {
            if (cs.state.name == "Locomotion")
            {
                cs.state.motion = CreateLocomotionBlendTree(controller, idle, walk, walkBack, run, strafeLeft, strafeRight);
                sm.defaultState = cs.state;
                return cs.state;
            }
        }

        var state = sm.AddState("Locomotion", new Vector3(200, 0, 0));
        state.motion = CreateLocomotionBlendTree(controller, idle, walk, walkBack, run, strafeLeft, strafeRight);
        sm.defaultState = state;
        return state;
    }

    private static BlendTree CreateLocomotionBlendTree(AnimatorController controller,
        AnimationClip idle, AnimationClip walk, AnimationClip walkBack, AnimationClip run, AnimationClip strafeLeft, AnimationClip strafeRight)
    {
        var bt = new BlendTree();
        bt.name = "PlayerLocomotionBlendTree";
        bt.blendType = BlendTreeType.FreeformDirectional2D;
        bt.blendParameter = "VelocityX";
        bt.blendParameterY = "VelocityZ";

        // Add to asset so it persists
        AssetDatabase.AddObjectToAsset(bt, controller);

        // Center Idle
        if (idle != null) bt.AddChild(idle, new Vector2(0, 0));

        // Forward Walk and Run
        if (walk != null) bt.AddChild(walk, new Vector2(0, 0.4f));
        if (run != null) bt.AddChild(run, new Vector2(0, 1.0f));

        // Backward Walk
        if (walkBack != null) bt.AddChild(walkBack, new Vector2(0, -0.6f));

        // Strafe Left and Right
        if (strafeLeft != null) bt.AddChild(strafeLeft, new Vector2(-1.0f, 0));
        if (strafeRight != null) bt.AddChild(strafeRight, new Vector2(1.0f, 0));

        return bt;
    }

    private static AnimatorState GetOrCreateState(AnimatorStateMachine sm, string stateName, AnimationClip clip, Vector3 pos)
    {
        foreach (var cs in sm.states)
            if (cs.state.name == stateName)
            {
                if (clip != null)
                    cs.state.motion = clip;
                cs.state.speed = 1f;
                return cs.state;
            }
        var state = sm.AddState(stateName, pos);
        state.motion = clip;
        state.speed = 1f;
        return state;
    }

    private static void EnsureAnyStateTransition(AnimatorStateMachine sm, AnimatorState dest,
        string param, AnimatorConditionMode mode, float threshold, bool hasExitTime, float duration)
    {
        foreach (var t in sm.anyStateTransitions)
        {
            if (t.destinationState == dest && HasCondition(t, param, mode))
                return;
        }
        var trans = sm.AddAnyStateTransition(dest);
        trans.hasExitTime = hasExitTime;
        trans.duration = duration;
        trans.canTransitionToSelf = false;
        trans.AddCondition(mode, threshold, param);
    }

    private static void EnsureReturnToLocomotionTransition(AnimatorState state, AnimatorState locomotionState, float exitTimeNormalized, float duration)
    {
        if (state == null || locomotionState == null)
            return;

        for (int i = state.transitions.Length - 1; i >= 0; i--)
        {
            AnimatorStateTransition existing = state.transitions[i];
            if (existing == null)
                continue;

            if (existing.isExit && existing.conditions.Length == 0)
                state.RemoveTransition(existing);
        }

        foreach (var t in state.transitions)
        {
            if (t.destinationState != locomotionState || t.conditions.Length != 0)
                continue;

            t.hasExitTime = true;
            t.exitTime = exitTimeNormalized;
            t.duration = duration;
            t.canTransitionToSelf = false;
            return;
        }

        var trans = state.AddTransition(locomotionState);
        trans.hasExitTime = true;
        trans.exitTime = exitTimeNormalized;
        trans.duration = duration;
        trans.canTransitionToSelf = false;
    }

    private static bool HasCondition(AnimatorTransitionBase t, string param, AnimatorConditionMode mode)
    {
        foreach (var c in t.conditions)
            if (c.parameter == param && c.mode == mode) return true;
        return false;
    }

    private static void ForcePlayerPrefabAnimatorSettings(RuntimeAnimatorController controller)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning("[PlayerFBXAnimatorSetup] Could not find Player prefab at " + PlayerPrefabPath);
            return;
        }

        PlayerController player = prefab.GetComponent<PlayerController>();
        if (player != null)
        {
            player.playerAnimatorController = controller;
            EditorUtility.SetDirty(player);
        }

        foreach (Animator animator in prefab.GetComponentsInChildren<Animator>(true))
        {
            animator.applyRootMotion = false;
            animator.enabled = true;
            animator.speed = 1f;
            if (animator.runtimeAnimatorController == null ||
                animator.runtimeAnimatorController.name.IndexOf("CrosbyAnimator", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                animator.runtimeAnimatorController = controller;
            }
            EditorUtility.SetDirty(animator);
        }

        PrefabUtility.SavePrefabAsset(prefab);
    }
}
