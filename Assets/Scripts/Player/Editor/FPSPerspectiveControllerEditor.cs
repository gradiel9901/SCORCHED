#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Scorched.Player.Editor
{
    [CustomEditor(typeof(FPSPerspectiveController))]
    public class FPSPerspectiveControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var controller = (FPSPerspectiveController)target;

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "FPS PERSPECTIVE & WEAPON PRESENTATION SYSTEM:\n\n" +
                "• Option 1: True First-Person (Eye-Level Tuned) — 100% full-body, relaxed low-ready patrol stance, camera at actual eye level.\n" +
                "• Option 2: Tactical Combat-Ready (Upper Body Layer) — Native mocap upper-body layer raises arms & AK-47 to shoulder height. Zero bone twisting.\n" +
                "• Option 3: Dedicated FPS Viewmodel (Modern Warfare Style) — Camera-anchored viewmodel with prominent framing, smooth ADS, sway & bob.\n\n" +
                "DYNAMIC CENTERING: When firing or aiming (Right-Click), the gun and hands smoothly glide to the center of the screen without clipping inside the head or deforming the body!",
                MessageType.Info);

            EditorGUILayout.Space(6);

            // Prominent Mode Dropdown
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Select FPS View Presentation", EditorStyles.boldLabel);

            var modeProp = serializedObject.FindProperty("_mode");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(modeProp, new GUIContent("Perspective Mode"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                controller.ApplyCurrentMode();
                EditorUtility.SetDirty(controller.gameObject);
            }

            // Quick Selection Buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("1. Eye-Tuned", GUILayout.Height(26)))
            {
                modeProp.enumValueIndex = (int)FPSPerspectiveMode.TrueFirstPerson_EyeTuned;
                serializedObject.ApplyModifiedProperties();
                controller.ApplyCurrentMode();
                EditorUtility.SetDirty(controller.gameObject);
            }
            if (GUILayout.Button("2. Combat-Ready", GUILayout.Height(26)))
            {
                modeProp.enumValueIndex = (int)FPSPerspectiveMode.CombatReady_UpperBody;
                serializedObject.ApplyModifiedProperties();
                controller.ApplyCurrentMode();
                EditorUtility.SetDirty(controller.gameObject);
            }
            if (GUILayout.Button("3. Modern Warfare", GUILayout.Height(26)))
            {
                modeProp.enumValueIndex = (int)FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
                serializedObject.ApplyModifiedProperties();
                controller.ApplyCurrentMode();
                EditorUtility.SetDirty(controller.gameObject);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            // Firing Center Alignment Settings
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Firing / Aiming Center Alignment", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_centerOnFiring"), new GUIContent("Center When Firing/Aiming"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_centerTransitionSpeed"), new GUIContent("Center Transition Speed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_firingSightRightOffset"), new GUIContent("Sightline X Offset (Center)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_firingEyeForwardOffset"), new GUIContent("Sightline Z Offset (Relief)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_firingEyeUpOffset"), new GUIContent("Sightline Y Offset (Height)"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            // Controls & Settings
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_cycleModeKey"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_showOnScreenBanner"));

            EditorGUILayout.Space(6);

            // Fine tuning parameters foldout
            EditorGUILayout.LabelField("Resting Stance Offsets", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt1EyeForward"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt1EyeUp"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt1FOV"));

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt2EyeForward"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt2EyeUp"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_opt2FOV"));

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_vmHipPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_vmHipEuler"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_vmAdsPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_adsTransitionSpeed"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
