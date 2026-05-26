using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Professional one-click setup script to configure CrosbyAnimator and import settings for the new enemy animations.
/// Menu: Tools > PRISM-7 > Setup Enemy FBX Animations
/// </summary>
public static class EnemyFBXAnimatorSetup
{
    private const string EnemyControllerPath = "Assets/AliAlhawaj/Prefabs/Enemies/Resources/Enemy/CrosbyAnimator.controller";
    private const string MaterialsDir = "Assets/AliAlhawaj/Materials";

    [MenuItem("Tools/PRISM-7/Setup Enemy FBX Animations")]
    public static void RunSetup()
    {
        // 1. Configure FBX Import Settings (Humanoid, Loops)
        ConfigureFBXImportSettings();

        // 2. Load and Backup Animator Controller
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(EnemyControllerPath);
        if (controller == null)
        {
            EditorUtility.DisplayDialog("Enemy Animator Setup",
                "Could not find CrosbyAnimator Controller at:\n" + EnemyControllerPath, "OK");
            return;
        }

        string backup = BackupController(EnemyControllerPath);
        Debug.Log("[EnemyFBXAnimatorSetup] Backup written: " + backup);

        // 3. Add necessary parameters
        AddParameter(controller, "Speed", AnimatorControllerParameterType.Float);
        AddParameter(controller, "VelocityX", AnimatorControllerParameterType.Float);
        AddParameter(controller, "VelocityZ", AnimatorControllerParameterType.Float);
        AddParameter(controller, "IsGrounded", AnimatorControllerParameterType.Bool);
        AddParameter(controller, "Attack", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "Hit", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "Death", AnimatorControllerParameterType.Trigger);

        if (controller.layers == null || controller.layers.Length == 0)
        {
            Debug.LogError("[EnemyFBXAnimatorSetup] Controller has no layers!");
            return;
        }

        var rootSM = controller.layers[0].stateMachine;

        // 4. Load Clips
        var idleClip = LoadFirstClip(MaterialsDir + "/Idle.fbx");
        var walkClip = LoadFirstClip(MaterialsDir + "/Walking.fbx");
        var walkBackClip = LoadFirstClip(MaterialsDir + "/Walking Backwards.fbx");
        var runClip = LoadFirstClip(MaterialsDir + "/Sword And Shield Run.fbx") ?? LoadFirstClip(MaterialsDir + "/Running.fbx");
        var strafeLeftClip = LoadFirstClip(MaterialsDir + "/Left Strafe.fbx") ?? LoadFirstClip(MaterialsDir + "/Strafe Left.fbx");
        var strafeRightClip = LoadFirstClip(MaterialsDir + "/Right Strafe.fbx") ?? LoadFirstClip(MaterialsDir + "/Strafe Right.fbx");
        var attackClip = LoadFirstClip(MaterialsDir + "/Sword And Shield Slash.fbx") ?? LoadFirstClip(MaterialsDir + "/Punching.fbx");
        var hitClip = LoadFirstClip(MaterialsDir + "/Hit Reaction.fbx");
        var deathClip = LoadFirstClip(MaterialsDir + "/Dying.fbx") ?? LoadFirstClip(MaterialsDir + "/Falling Back Death.fbx");

        if (idleClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Idle.fbx");
        if (walkClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Walking.fbx");
        if (runClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Running/Run.fbx");
        if (attackClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Slash/Punching.fbx");
        if (hitClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Hit Reaction.fbx");
        if (deathClip == null) Debug.LogWarning("[EnemyFBXAnimatorSetup] Missing Dying/Death.fbx");

        // 5. Create or Get Locomotion Blend Tree State
        var locomotionState = GetOrCreateLocomotionState(rootSM, controller, idleClip, walkClip, walkBackClip, runClip, strafeLeftClip, strafeRightClip);

        // 6. Create Combat / Hit / Death States
        var attackState = GetOrCreateState(rootSM, "Attack", attackClip, new Vector3(500, 0, 0));
        var hitState = GetOrCreateState(rootSM, "Hit", hitClip, new Vector3(500, 100, 0));
        var deathState = GetOrCreateState(rootSM, "Death", deathClip, new Vector3(500, 200, 0));

        // Disable loop on action states
        if (attackClip != null) SetClipLoop(attackClip, false);
        if (hitClip != null) SetClipLoop(hitClip, false);
        if (deathClip != null) SetClipLoop(deathClip, false);

        // 7. Wire Transitions
        EnsureAnyStateTransition(rootSM, attackState, "Attack", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);
        EnsureExitTransition(attackState, exitTimeNormalized: 0.85f, duration: 0.15f);

        EnsureAnyStateTransition(rootSM, hitState, "Hit", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);
        EnsureExitTransition(hitState, exitTimeNormalized: 0.85f, duration: 0.15f);

        EnsureAnyStateTransition(rootSM, deathState, "Death", AnimatorConditionMode.If, 0f, hasExitTime: false, duration: 0.05f);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Enemy Animator Setup",
            "Enemy CrosbyAnimator successfully configured with the new animations!", "OK");
    }

    private static void ConfigureFBXImportSettings()
    {
        string[] loopTrueClips = {
            "Idle.fbx", "Walking.fbx", "Walking Backwards.fbx", "Running.fbx", "Sword And Shield Run.fbx",
            "Left Strafe.fbx", "Right Strafe.fbx", "Strafe Left.fbx", "Strafe Right.fbx"
        };

        string[] loopFalseClips = {
            "Sword And Shield Slash.fbx", "Punching.fbx", "Hit Reaction.fbx", "Dying.fbx",
            "Falling Back Death.fbx", "Standing Up.fbx"
        };

        ConfigureClipsInArray(loopTrueClips, loopTime: true);
        ConfigureClipsInArray(loopFalseClips, loopTime: false);

        AssetDatabase.Refresh();
    }

    private static void ConfigureClipsInArray(string[] filenames, bool loopTime)
    {
        for (int i = 0; i < filenames.Length; i++)
        {
            string path = MaterialsDir + "/" + filenames[i];
            if (!File.Exists(path)) continue;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            bool changed = false;

            // Ensure Humanoid Setup
            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                changed = true;
            }

            // Ensure Loop Settings
            var clipAnims = importer.defaultClipAnimations;
            if (clipAnims != null && clipAnims.Length > 0)
            {
                for (int c = 0; c < clipAnims.Length; c++)
                {
                    if (clipAnims[c].loopTime != loopTime)
                    {
                        clipAnims[c].loopTime = loopTime;
                        changed = true;
                    }
                }
                importer.clipAnimations = clipAnims;
            }

            if (changed)
            {
                importer.SaveAndReimport();
                Debug.Log($"[EnemyFBXAnimatorSetup] Reimported {filenames[i]} with Humanoid and loopTime={loopTime}");
            }
        }
    }

    private static void SetClipLoop(AnimationClip clip, bool loop)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        if (settings.loopTime != loop)
        {
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }
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
                // Re-build Blend Tree to ensure it matches precisely
                cs.state.motion = CreateLocomotionBlendTree(controller, idle, walk, walkBack, run, strafeLeft, strafeRight);
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
        bt.name = "LocomotionBlendTree";
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
                if (clip != null && cs.state.motion == null)
                    cs.state.motion = clip;
                return cs.state;
            }
        var state = sm.AddState(stateName, pos);
        state.motion = clip;
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

    private static void EnsureExitTransition(AnimatorState state, float exitTimeNormalized, float duration)
    {
        foreach (var t in state.transitions)
            if (t.isExit && t.conditions.Length == 0) return;
        var trans = state.AddExitTransition();
        trans.hasExitTime = true;
        trans.exitTime = exitTimeNormalized;
        trans.duration = duration;
    }

    private static bool HasCondition(AnimatorTransitionBase t, string param, AnimatorConditionMode mode)
    {
        foreach (var c in t.conditions)
            if (c.parameter == param && c.mode == mode) return true;
        return false;
    }
}
