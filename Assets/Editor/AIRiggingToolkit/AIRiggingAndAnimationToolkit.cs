#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Scorched.EditorTools
{
    public static class AIRiggingAndAnimationToolkit
    {
        /// <summary>
        /// Lists all 95 standardized Unity HumanTrait Muscle names and their indices to the Console.
        /// </summary>
        [MenuItem("SCORCHED/AI Rigging Toolkit/Log All 95 Humanoid Muscle Names")]
        public static void LogAllMuscleNames()
        {
            string[] muscles = HumanTrait.MuscleName;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== UNITY HUMANOID MUSCLES ({muscles.Length}) ===");
            for (int i = 0; i < muscles.Length; i++)
            {
                sb.AppendLine($"[{i}] \"{muscles[i]}\"");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Safely poses a Humanoid character using normalized [-1.0f to +1.0f] muscle values instead of raw Euler angles.
        /// </summary>
        public static void ApplyHumanoidMusclePose(Animator animator, Dictionary<string, float> muscleTargets)
        {
            if (animator == null || !animator.isHuman || animator.avatar == null)
            {
                Debug.LogWarning("[AIRiggingToolkit] Animator is null or not a valid Humanoid Avatar.");
                return;
            }

            Undo.RecordObjects(animator.GetComponentsInChildren<Transform>(), "AI Muscle Pose");
            using (HumanPoseHandler handler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                HumanPose pose = new HumanPose();
                handler.GetHumanPose(ref pose);

                string[] muscleNames = HumanTrait.MuscleName;
                for (int i = 0; i < muscleNames.Length; i++)
                {
                    if (muscleTargets.TryGetValue(muscleNames[i], out float val))
                    {
                        pose.muscles[i] = Mathf.Clamp(val, -1f, 1f);
                    }
                }

                handler.SetHumanPose(ref pose);
            }
            EditorUtility.SetDirty(animator.gameObject);
        }

        /// <summary>
        /// Analytical Two-Bone IK Solver (Works on ANY rig: Humanoid or Generic) to lock a Hand onto a Weapon Grip/Magazine target.
        /// </summary>
        public static void SolveTwoBoneIK(Transform rootBone, Transform midBone, Transform tipBone, Vector3 targetPos, Quaternion targetRot, Vector3 polePos)
        {
            if (rootBone == null || midBone == null || tipBone == null) return;

            float upperLen = Vector3.Distance(rootBone.position, midBone.position);
            float lowerLen = Vector3.Distance(midBone.position, tipBone.position);
            float totalLen = upperLen + lowerLen;

            Vector3 toTarget = targetPos - rootBone.position;
            float dist = Mathf.Clamp(toTarget.magnitude, 0.001f, totalLen - 0.001f);

            // Law of cosines for interior angles
            float cosAngleRoot = Mathf.Clamp(((upperLen * upperLen) + (dist * dist) - (lowerLen * lowerLen)) / (2f * upperLen * dist), -1f, 1f);
            float angleRoot = Mathf.Acos(cosAngleRoot) * Mathf.Rad2Deg;

            Vector3 planeNormal = Vector3.Cross(toTarget.normalized, (polePos - rootBone.position).normalized).normalized;
            if (planeNormal.sqrMagnitude < 0.001f) planeNormal = Vector3.Cross(toTarget.normalized, Vector3.up).normalized;

            // Rotate root bone toward target + pole bend (+angleRoot bends elbow toward polePos)
            Vector3 currentUpperDir = (midBone.position - rootBone.position).normalized;
            Quaternion aimRot = Quaternion.FromToRotation(currentUpperDir, toTarget.normalized) * rootBone.rotation;
            rootBone.rotation = Quaternion.AngleAxis(angleRoot, planeNormal) * aimRot;

            // Align rootBone's outer-elbow axis toward polePos so the elbow bends as a clean hinge (zero elbow twist)
            Vector3 finalUpperDir = (midBone.position - rootBone.position).normalized;
            Vector3 desiredElbowTipDir = Vector3.ProjectOnPlane(polePos - rootBone.position, finalUpperDir).normalized;
            if (desiredElbowTipDir.sqrMagnitude > 1e-4f)
            {
                bool isRightSide = rootBone.name.EndsWith(".r", StringComparison.OrdinalIgnoreCase)
                    || rootBone.name.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 currentElbowAxis = Vector3.ProjectOnPlane(isRightSide ? -rootBone.right : rootBone.right, finalUpperDir).normalized;
                if (currentElbowAxis.sqrMagnitude > 1e-4f)
                {
                    rootBone.rotation = Quaternion.FromToRotation(currentElbowAxis, desiredElbowTipDir) * rootBone.rotation;
                }
            }

            // Rotate mid bone (forearm) so tip bone (hand) reaches exact targetPos
            Vector3 currentLowerDir = (tipBone.position - midBone.position).normalized;
            Vector3 desiredLowerDir = (targetPos - midBone.position).normalized;
            midBone.rotation = Quaternion.FromToRotation(currentLowerDir, desiredLowerDir) * midBone.rotation;

            // Align midBone's outer-elbow axis with rootBone's hinge plane before distributing wrist twist
            if (desiredElbowTipDir.sqrMagnitude > 1e-4f)
            {
                bool isRightSide = rootBone.name.EndsWith(".r", StringComparison.OrdinalIgnoreCase)
                    || rootBone.name.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 targetMidElbow = Vector3.ProjectOnPlane(desiredElbowTipDir, desiredLowerDir).normalized;
                Vector3 currentMidElbow = Vector3.ProjectOnPlane(isRightSide ? -midBone.right : midBone.right, desiredLowerDir).normalized;
                if (targetMidElbow.sqrMagnitude > 1e-4f && currentMidElbow.sqrMagnitude > 1e-4f)
                {
                    midBone.rotation = Quaternion.FromToRotation(currentMidElbow, targetMidElbow) * midBone.rotation;
                }
            }

            // Distribute axial pronation/supination twist: 60% forearm, <=25 deg wrist cap
            Vector3 boneAxis = tipBone.localPosition.sqrMagnitude > 1e-6f
                ? tipBone.localPosition.normalized
                : Vector3.up;
            Quaternion rel = Quaternion.Inverse(midBone.rotation) * targetRot;
            if (rel.w < 0f) rel = new Quaternion(-rel.x, -rel.y, -rel.z, -rel.w);

            Vector3 relVec = new Vector3(rel.x, rel.y, rel.z);
            Vector3 twistProj = Vector3.Project(relVec, boneAxis);
            Quaternion twist = new Quaternion(twistProj.x, twistProj.y, twistProj.z, rel.w);
            if (Quaternion.Dot(twist, twist) < 1e-6f) twist = Quaternion.identity;
            else twist.Normalize();
            if (twist.w < 0f) twist = new Quaternion(-twist.x, -twist.y, -twist.z, -twist.w);

            Quaternion swing = Quaternion.Inverse(twist) * rel;
            twist.ToAngleAxis(out float twistAngle, out Vector3 twistAxis);
            if (twistAngle > 180f) twistAngle -= 360f;
            float signedTwist = twistAngle * (Vector3.Dot(twistAxis, boneAxis) >= 0f ? 1f : -1f);

            float remTwist = Mathf.Clamp(signedTwist * 0.40f, -25f, 25f);
            float midTwist = Mathf.Clamp(signedTwist - remTwist, -75f, 75f);
            remTwist = Mathf.Clamp(signedTwist - midTwist, -25f, 25f);

            midBone.rotation = midBone.rotation * Quaternion.AngleAxis(midTwist, boneAxis);

            swing.ToAngleAxis(out float swingAngle, out Vector3 swingAxis);
            if (swingAngle > 180f) swingAngle -= 360f;
            if (Mathf.Abs(swingAngle) > 75f && swingAxis.sqrMagnitude > 1e-6f)
            {
                swing = Quaternion.AngleAxis(Mathf.Clamp(swingAngle, -75f, 75f), swingAxis.normalized);
            }

            tipBone.rotation = midBone.rotation * Quaternion.AngleAxis(remTwist, boneAxis) * swing;
        }

        /// <summary>
        /// Records a Transform's current localPosition and localRotation into AnimationCurves for baking .anim clips.
        /// </summary>
        public static void RecordBoneKeyframe(Transform root, Transform bone, float time, Dictionary<string, AnimationCurve> curveMap)
        {
            string relativePath = AnimationUtility.CalculateTransformPath(bone, root);
            AddKey(curveMap, relativePath, "m_LocalPosition.x", time, bone.localPosition.x);
            AddKey(curveMap, relativePath, "m_LocalPosition.y", time, bone.localPosition.y);
            AddKey(curveMap, relativePath, "m_LocalPosition.z", time, bone.localPosition.z);

            AddKey(curveMap, relativePath, "m_LocalRotation.x", time, bone.localRotation.x);
            AddKey(curveMap, relativePath, "m_LocalRotation.y", time, bone.localRotation.y);
            AddKey(curveMap, relativePath, "m_LocalRotation.z", time, bone.localRotation.z);
            AddKey(curveMap, relativePath, "m_LocalRotation.w", time, bone.localRotation.w);
        }

        private static void AddKey(Dictionary<string, AnimationCurve> map, string path, string prop, float time, float val)
        {
            string key = $"{path}|{prop}";
            if (!map.TryGetValue(key, out AnimationCurve curve))
            {
                curve = new AnimationCurve();
                map[key] = curve;
            }
            curve.AddKey(new Keyframe(time, val));
        }

        /// <summary>
        /// Flushes recorded bone curves into a saved Unity AnimationClip (.anim) asset.
        /// </summary>
        public static AnimationClip BakeCurvesToClip(string assetPath, Dictionary<string, AnimationCurve> curveMap, bool loopTime = false)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60f };
            foreach (var kvp in curveMap)
            {
                string[] parts = kvp.Key.Split('|');
                EditorCurveBinding binding = EditorCurveBinding.FloatCurve(parts[0], typeof(Transform), parts[1]);
                AnimationUtility.SetEditorCurve(clip, binding, kvp.Value);
            }

            clip.EnsureQuaternionContinuity();

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loopTime;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(assetPath));
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AIRiggingToolkit] Baked AnimationClip to: {assetPath}");
            return clip;
        }
    }
}
#endif

