#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Generates Assets/Animations/Down_Idle.anim from the Base Layer "Down Walking" clip's
    /// grounded pose with subtle wounded breathing, registers "Down Idle" in 01_Player.controller
    /// without using Animator Parameters, and guarantees "Death" has loopTime = false.
    /// </summary>
    public static class DownIdleClipGenerator
    {
        private const string ControllerPath = "Assets/Animations/Player/01_Player.controller";
        private const string DownIdleClipPath = "Assets/Animations/Down_Idle.anim";
        private const string DownWalkingStateName = "Down Walking";
        private const string DownIdleStateName = "Down Idle";
        private const string DeathStateName = "Death";
        private const float ClipDurationSeconds = 2.0f;
        private const float GroundSampleTime = 0.0f;

        private static readonly Dictionary<string, float> SpineBreathingPitchDegrees = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            { "root/root.x/spine_01.x", -1.2f },
            { "root/root.x/spine_01.x/spine_02.x", -1.8f },
            { "root/root.x/spine_01.x/spine_02.x/spine_03.x", -2.4f },
            { "root/root.x/spine_01.x/spine_02.x/spine_03.x/neck.x", 1.8f }
        };

        [MenuItem("SCORCHED/Animations/Generate Down Idle & Configure Death Clip")]
        public static string GenerateDownIdleAndConfigureDeath()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                return $"ERROR: AnimatorController not found at {ControllerPath}";
            }

            AnimatorStateMachine baseSm = controller.layers[0].stateMachine;
            AnimationClip downWalkingClip = FindStateClip(baseSm, DownWalkingStateName);
            if (downWalkingClip == null)
            {
                return $"ERROR: State '{DownWalkingStateName}' or its AnimationClip was not found on Base Layer.";
            }

            AnimationClip downIdleClip = CreateOrUpdateDownIdleClip(downWalkingClip);
            RegisterDownIdleState(controller, baseSm, downIdleClip);
            bool deathLoopDisabled = EnsureDeathNonLooping(baseSm);

            EditorUtility.SetDirty(downIdleClip);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var summary = $"SUCCESS: Generated '{DownIdleClipPath}' (len={downIdleClip.length:F2}s, loop=true) from '{downWalkingClip.name}', registered '{DownIdleStateName}' on Base Layer, Death loopTime disabled={deathLoopDisabled}.";
            Debug.Log($"[DownIdleClipGenerator] {summary}");
            return summary;
        }

        private static AnimationClip FindStateClip(AnimatorStateMachine sm, string stateName)
        {
            foreach (var childState in sm.states)
            {
                if (string.Equals(childState.state.name, stateName, StringComparison.Ordinal))
                {
                    return childState.state.motion as AnimationClip;
                }
            }
            return null;
        }

        private static AnimationClip CreateOrUpdateDownIdleClip(AnimationClip downWalkingClip)
        {
            AnimationClip newClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(DownIdleClipPath);
            if (newClip == null)
            {
                newClip = new AnimationClip { name = "Down_Idle" };
                AssetDatabase.CreateAsset(newClip, DownIdleClipPath);
            }
            else
            {
                newClip.ClearCurves();
                newClip.name = "Down_Idle";
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(downWalkingClip);

            // Group local rotation XYZW components by bone path so breathing pitch is rotated
            // in quaternion space without gimbal lock or non-unit drift.
            var rotBindingsByPath = new Dictionary<string, EditorCurveBinding[]>(StringComparer.Ordinal);
            var rotValuesByPath = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var scalarBindings = new List<EditorCurveBinding>();

            foreach (var b in bindings)
            {
                int rotIdx = GetRotationComponentIndex(b.propertyName);
                if (rotIdx >= 0)
                {
                    if (!rotBindingsByPath.TryGetValue(b.path, out var arr))
                    {
                        arr = new EditorCurveBinding[4];
                        rotBindingsByPath[b.path] = arr;
                        rotValuesByPath[b.path] = new float[4];
                    }
                    arr[rotIdx] = b;
                    var srcCurve = AnimationUtility.GetEditorCurve(downWalkingClip, b);
                    rotValuesByPath[b.path][rotIdx] = srcCurve != null ? srcCurve.Evaluate(GroundSampleTime) : (rotIdx == 3 ? 1f : 0f);
                }
                else
                {
                    scalarBindings.Add(b);
                }
            }

            float halfTime = ClipDurationSeconds * 0.5f;

            // Freeze root / scalar properties at the grounded pose across [0.0, 1.0, 2.0]
            foreach (var b in scalarBindings)
            {
                var srcCurve = AnimationUtility.GetEditorCurve(downWalkingClip, b);
                float val = srcCurve != null ? srcCurve.Evaluate(GroundSampleTime) : 0f;
                var curve = new AnimationCurve(
                    CreateFlatKeyframe(0f, val),
                    CreateFlatKeyframe(halfTime, val),
                    CreateFlatKeyframe(ClipDurationSeconds, val)
                );
                AnimationUtility.SetEditorCurve(newClip, b, curve);
            }

            // Hold limb quaternions stationary and layer gentle wounded chest breathing on spine/neck
            foreach (var kvp in rotBindingsByPath)
            {
                string path = kvp.Key;
                EditorCurveBinding[] compBindings = kvp.Value;
                float[] v = rotValuesByPath[path];

                Quaternion baseRot = new Quaternion(v[0], v[1], v[2], v[3]).normalized;
                Quaternion midRot = baseRot;

                if (SpineBreathingPitchDegrees.TryGetValue(path, out float pitchDeg))
                {
                    midRot = (baseRot * Quaternion.AngleAxis(pitchDeg, Vector3.right)).normalized;
                    if (Quaternion.Dot(baseRot, midRot) < 0f)
                    {
                        midRot = new Quaternion(-midRot.x, -midRot.y, -midRot.z, -midRot.w);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (string.IsNullOrEmpty(compBindings[i].propertyName)) continue;

                    float q0 = GetQuaternionComponent(baseRot, i);
                    float qMid = GetQuaternionComponent(midRot, i);
                    var curve = new AnimationCurve(
                        CreateFlatKeyframe(0f, q0),
                        CreateFlatKeyframe(halfTime, qMid),
                        CreateFlatKeyframe(ClipDurationSeconds, q0)
                    );
                    AnimationUtility.SetEditorCurve(newClip, compBindings[i], curve);
                }
            }

            newClip.EnsureQuaternionContinuity();

            var settings = AnimationUtility.GetAnimationClipSettings(newClip);
            settings.loopTime = true;
            settings.loopBlend = true;
            settings.cycleOffset = 0f;
            settings.stopTime = ClipDurationSeconds;
            AnimationUtility.SetAnimationClipSettings(newClip, settings);

            return newClip;
        }

        private static void RegisterDownIdleState(AnimatorController controller, AnimatorStateMachine baseSm, AnimationClip downIdleClip)
        {
            AnimatorState existingState = null;
            foreach (var child in baseSm.states)
            {
                if (string.Equals(child.state.name, DownIdleStateName, StringComparison.Ordinal))
                {
                    existingState = child.state;
                    break;
                }
            }

            if (existingState == null)
            {
                existingState = baseSm.AddState(DownIdleStateName);
            }

            existingState.motion = downIdleClip;
            existingState.speed = 1.0f;
            existingState.speedParameterActive = false;
            existingState.writeDefaultValues = true;
            EditorUtility.SetDirty(existingState);
        }

        private static bool EnsureDeathNonLooping(AnimatorStateMachine baseSm)
        {
            AnimationClip deathClip = FindStateClip(baseSm, DeathStateName);
            if (deathClip == null) return false;

            string assetPath = AssetDatabase.GetAssetPath(deathClip);
            if (AssetImporter.GetAtPath(assetPath) is ModelImporter modelImporter)
            {
                var clips = modelImporter.clipAnimations;
                if (clips == null || clips.Length == 0)
                {
                    clips = modelImporter.defaultClipAnimations;
                }

                bool changed = false;
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i].loopTime)
                    {
                        clips[i].loopTime = false;
                        changed = true;
                    }
                }

                if (changed)
                {
                    modelImporter.clipAnimations = clips;
                    modelImporter.SaveAndReimport();
                }

                AnimationClip reloaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (reloaded != null)
                {
                    return !AnimationUtility.GetAnimationClipSettings(reloaded).loopTime;
                }
            }

            var settings = AnimationUtility.GetAnimationClipSettings(deathClip);
            settings.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(deathClip, settings);
            EditorUtility.SetDirty(deathClip);
            return !AnimationUtility.GetAnimationClipSettings(deathClip).loopTime;
        }

        private static Keyframe CreateFlatKeyframe(float time, float value)
        {
            return new Keyframe(time, value, 0f, 0f);
        }

        private static int GetRotationComponentIndex(string propertyName)
        {
            return propertyName switch
            {
                "m_LocalRotation.x" => 0,
                "m_LocalRotation.y" => 1,
                "m_LocalRotation.z" => 2,
                "m_LocalRotation.w" => 3,
                _ => -1
            };
        }

        private static float GetQuaternionComponent(Quaternion q, int index)
        {
            return index switch
            {
                0 => q.x,
                1 => q.y,
                2 => q.z,
                _ => q.w
            };
        }
    }
}
#endif

