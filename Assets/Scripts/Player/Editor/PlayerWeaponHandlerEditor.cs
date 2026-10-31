#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Scorched.Player
{
    [CustomEditor(typeof(PlayerWeaponHandler))]
    public class PlayerWeaponHandlerEditor : UnityEditor.Editor
    {
        private static readonly string[] s_StateNames =
        {
            "Idle", "Jog", "Running", "Jumping",
            "Sprint Jump", "Crouch Idle", "Crouched Run", "Firing Rifle"
        };

        private static bool s_SubscribedToPlayMode;

        private void OnEnable()
        {
            if (!s_SubscribedToPlayMode)
            {
                s_SubscribedToPlayMode = true;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
            {
                if (AnimationMode.InAnimationMode())
                {
                    AnimationMode.StopAnimationMode();
                }
            }
        }

        private void OnDisable()
        {
            // CRITICAL: Do NOT stop AnimationMode here!
            // When the user clicks on the WeaponSocket or rifle to move it, Unity deselects Player.
            // Keeping AnimationMode active ensures the character stays frozen in the selected animation pose!
        }

        public override void OnInspectorGUI()
        {
            var handler = (PlayerWeaponHandler)target;

            EditorGUILayout.HelpBox(
                "PREVIEW WORKFLOW:\n" +
                "1. Click any animation state (e.g. 'Jog', 'Running', 'Jumping'). The character stays frozen in that pose!\n" +
                "2. You can nudge the gun using the 3D handles right in Scene View (while Player is selected), OR click the WeaponSocket directly.\n" +
                "3. Click 'Save Socket Pose' to lock your adjustments to that state!\n" +
                "4. When finished, click 'Preview: ACTIVE' to turn preview off.",
                MessageType.Info);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Weapon Socket", handler.WeaponSocket != null ? $"{handler.WeaponSocket.name} (Parent: {handler.WeaponSocket.parent?.name})" : "Missing");
            EditorGUILayout.LabelField("Right Hand Bone", handler.RightHandBone != null ? handler.RightHandBone.name : "Missing");
            EditorGUILayout.LabelField("Equipped Weapon", handler.EquippedWeapon != null ? handler.EquippedWeapon.name : "None");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Align Socket to hand.r"))
            {
                handler.EnsureHandAndSocket();
                if (handler.EquippedWeapon != null)
                {
                    handler.AttachWeaponToSocket(handler.EquippedWeapon);
                }
                EditorUtility.SetDirty(handler.gameObject);
            }
            if (GUILayout.Button("Equip Scene Rifle"))
            {
                handler.TryPickupNearbyOrSceneWeapon();
                EditorUtility.SetDirty(handler.gameObject);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool preview = handler.EnablePreviewMode;
            GUI.backgroundColor = preview ? new Color(0.3f, 1f, 0.4f) : Color.white;
            if (GUILayout.Button(preview ? "Preview: ACTIVE (Click to Turn Off)" : "Preview: OFF (Click to Preview Poses)", GUILayout.Height(30)))
            {
                handler.EnablePreviewMode = !preview;
                if (!handler.EnablePreviewMode)
                {
                    if (!Application.isPlaying && AnimationMode.InAnimationMode())
                    {
                        AnimationMode.StopAnimationMode();
                        SceneView.RepaintAll();
                    }
                }
                else
                {
                    SampleStateInEditor(handler, handler.PreviewState, handler.PreviewNormalizedTime);
                }
            }
            GUI.backgroundColor = Color.white;

            if (handler.EnablePreviewMode)
            {
                float t = EditorGUILayout.Slider("Animation Frame", handler.PreviewNormalizedTime, 0f, 1f);
                if (!Mathf.Approximately(t, handler.PreviewNormalizedTime))
                {
                    handler.PreviewNormalizedTime = t;
                    SampleStateInEditor(handler, handler.PreviewState, t);
                }
            }

            int current = (int)handler.PreviewState;
            int selected = GUILayout.SelectionGrid(current, s_StateNames, 2, GUILayout.Height(100));
            if (selected != current)
            {
                handler.EnablePreviewMode = true;
                handler.PreviewState = (WeaponPoseState)selected;
                SampleStateInEditor(handler, (WeaponPoseState)selected, handler.PreviewNormalizedTime);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button($"Snap Socket to [{(WeaponPoseState)selected}] Pose", GUILayout.Height(26)))
            {
                handler.SnapSocketToPose((WeaponPoseState)selected);
                SampleStateInEditor(handler, (WeaponPoseState)selected, handler.PreviewNormalizedTime);
            }
            if (GUILayout.Button("Save Socket Pose", GUILayout.Height(26)))
            {
                handler.SaveCurrentSocketPoseToState((WeaponPoseState)selected);
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Copy C# Pose Line to Clipboard", GUILayout.Height(24)))
            {
                handler.LogCurrentSocketPose();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);
            DrawDefaultInspector();
        }

        private void OnSceneGUI()
        {
            var handler = (PlayerWeaponHandler)target;
            if (handler == null || handler.WeaponSocket == null)
            {
                return;
            }

            // Draw direct 3D handles in Scene View so the user can move/rotate WeaponSocket
            // without needing to deselect Player!
            if (handler.EnablePreviewMode)
            {
                Transform socket = handler.WeaponSocket;

                EditorGUI.BeginChangeCheck();
                Vector3 newPos = Handles.PositionHandle(socket.position, socket.rotation);
                Quaternion newRot = Handles.RotationHandle(socket.rotation, socket.position);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(socket, "Move Weapon Socket");
                    socket.position = newPos;
                    socket.rotation = newRot;
                    EditorUtility.SetDirty(socket);
                }

                GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = Color.yellow }
                };
                Handles.Label(socket.position + Vector3.up * 0.12f, $"[{handler.PreviewState}] Weapon Socket", labelStyle);
            }
        }

        private void SampleStateInEditor(PlayerWeaponHandler handler, WeaponPoseState state, float normalizedTime)
        {
            handler.SnapSocketToPose(state);

            if (Application.isPlaying)
            {
                handler.SamplePreviewAnimation();
                return;
            }

            var animator = handler.GetComponentInChildren<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            string searchKey = state switch
            {
                WeaponPoseState.Jog => "Jog",
                WeaponPoseState.Running => "Running",
                WeaponPoseState.Jumping => "Jumping",
                WeaponPoseState.SprintJump => "Sprint Jump",
                WeaponPoseState.CrouchIdle => "Crouch Idle",
                WeaponPoseState.CrouchedRun => "Crouched Run",
                WeaponPoseState.FiringRifle => "Firing Rifle",
                _ => "Idle"
            };

            AnimationClip targetClip = null;
            foreach (var clip in animator.runtimeAnimatorController.animationClips)
            {
                if (clip.name.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetClip = clip;
                    break;
                }
            }

            if (targetClip != null)
            {
                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                }

                AnimationMode.SampleAnimationClip(handler.gameObject, targetClip, normalizedTime * targetClip.length);
                handler.GetComponent<PlayerMovement>()?.UpdateCameraPositionAndRotation();
                SceneView.RepaintAll();
            }
        }
    }
}
#endif
