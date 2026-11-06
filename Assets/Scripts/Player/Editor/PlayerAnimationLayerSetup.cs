#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Scorched.Player.Editor
{
    [InitializeOnLoad]
    public static class PlayerAnimationLayerSetup
    {
        private const string ControllerPath = "Assets/Animations/Player/01_Player.controller";
        private const string MaskPath = "Assets/Animations/Player/UpperBodyMask.mask";
        private const string FiringClipGuid = "2c19ff8ba32157d40b87e0ce76f68db0";

        static PlayerAnimationLayerSetup()
        {
            EnsureUpperBodyLayerConfigured();
        }

        [MenuItem("Tools/Scorched/Configure Upper Body Firing Layer")]
        public static void EnsureUpperBodyLayerConfigured()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogWarning($"[PlayerAnimationLayerSetup] Could not find AnimatorController at {ControllerPath}");
                return;
            }

            // 1. Create or load UpperBody AvatarMask
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask == null)
            {
                mask = new AvatarMask();
                mask.name = "UpperBodyMask";

                // Configure humanoid body parts:
                // Lower body (Root, Legs, Feet) = false -> preserves locomotion (running, jogging, crouch)
                // Upper body (Body, Head, Arms, Hands) = true -> blends firing recoil onto upper body
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftHandIK, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightHandIK, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);

                AssetDatabase.CreateAsset(mask, MaskPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[PlayerAnimationLayerSetup] Created UpperBody AvatarMask at {MaskPath}");
            }

            // 2. Find or load Firing Rifle motion clip
            Motion firingMotion = null;
            string clipPath = AssetDatabase.GUIDToAssetPath(FiringClipGuid);
            if (!string.IsNullOrEmpty(clipPath))
            {
                firingMotion = AssetDatabase.LoadAssetAtPath<Motion>(clipPath);
            }

            // Fallback: search Base Layer for Firing Rifle motion
            if (firingMotion == null && controller.layers.Length > 0)
            {
                var baseStates = controller.layers[0].stateMachine.states;
                foreach (var childState in baseStates)
                {
                    if (childState.state.name == "Firing Rifle" && childState.state.motion != null)
                    {
                        firingMotion = childState.state.motion;
                        break;
                    }
                }
            }

            // 3. Ensure Layer 1 "Upper Body" exists
            var layers = controller.layers;
            int upperBodyIndex = -1;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].name == "Upper Body")
                {
                    upperBodyIndex = i;
                    break;
                }
            }

            bool layerAdded = false;
            if (upperBodyIndex < 0)
            {
                controller.AddLayer("Upper Body");
                layers = controller.layers;
                upperBodyIndex = layers.Length - 1;
                layerAdded = true;
            }

            // Configure layer properties
            layers[upperBodyIndex].avatarMask = mask;
            layers[upperBodyIndex].defaultWeight = 1.0f;
            layers[upperBodyIndex].blendingMode = AnimatorLayerBlendingMode.Override;

            var sm = layers[upperBodyIndex].stateMachine;

            // Find or create Empty default state
            AnimatorState emptyState = null;
            AnimatorState firingState = null;
            foreach (var childState in sm.states)
            {
                if (childState.state.name == "Empty") emptyState = childState.state;
                if (childState.state.name == "Firing Rifle") firingState = childState.state;
            }

            if (emptyState == null)
            {
                emptyState = sm.AddState("Empty", new Vector3(250, 120, 0));
                emptyState.motion = null;
                emptyState.writeDefaultValues = true;
            }

            if (firingState == null)
            {
                firingState = sm.AddState("Firing Rifle", new Vector3(250, 220, 0));
                firingState.motion = firingMotion;
                firingState.writeDefaultValues = true;

                // Transition back to Empty when fire animation finishes
                var transition = firingState.AddTransition(emptyState);
                transition.hasExitTime = true;
                transition.exitTime = 0.95f;
                transition.duration = 0.12f;
                transition.hasFixedDuration = true;
            }
            else if (firingState.motion == null && firingMotion != null)
            {
                firingState.motion = firingMotion;
            }

            sm.defaultState = emptyState;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            if (layerAdded)
            {
                Debug.Log("[PlayerAnimationLayerSetup] Successfully added and configured 'Upper Body' layer on 01_Player.controller.");
            }
        }
    }
}
#endif

