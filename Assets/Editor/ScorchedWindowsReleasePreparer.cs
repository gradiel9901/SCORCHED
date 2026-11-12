#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Scorched.Environment;
using Scorched.Inventory;
using Scorched.Survival;
using Scorched.UI;
using Scorched.Weapons;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Idempotent Windows Standalone (StandaloneWindows64) release readiness preparer & auditor.
    /// </summary>
    public static class ScorchedWindowsReleasePreparer
    {
        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        private static readonly string[] RequiredAlwaysIncludedShaders =
        {
            "Scorched/PostProcessing/SuperElNinoHeatstroke",
            "Scorched/Sky/ScorchedVolumetricCloudsSkybox",
            "Scorched/PostProcessing/ScorchedVolumetricAtmosphere",
            "Scorched/Weapons/BulletTracer",
            "Scorched/Particles/GlowingParticle",
            "Scorched/Decals/BulletMark",
            "Scorched/VFX/SupplyBeacon",
            "Scorched/VFX/AshDisintegration",
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Unlit"
        };

        [MenuItem("Tools/SCORCHED/Release/Prepare & Audit Windows Standalone Build")]
        public static void PrepareFromMenu()
        {
            string report = PrepareAndAudit();
            Debug.Log(report);
        }

        public static string PrepareAndAudit()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SCORCHED WINDOWS STANDALONE PREPARE & AUDIT ===");

            // Ensure target scene is open cleanly
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.path != TargetScenePath)
            {
                activeScene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            // ---------------------------------------------------------
            // PHASE 2: GraphicsSettings Always Included Shaders
            // ---------------------------------------------------------
            int shadersAdded = EnsureAlwaysIncludedShaders(sb);

            // ---------------------------------------------------------
            // PHASE 3: Scene Managers & Serialized References
            // ---------------------------------------------------------
            EnsureHeatstrokeShaderSerialized(sb);
            EnsureBallisticsAndBulletPoolInScene(sb);

            // ---------------------------------------------------------
            // PHASE 4: NGO Prefabs & Registration Check
            // ---------------------------------------------------------
            VerifyNetworkPrefabs(sb);

            // ---------------------------------------------------------
            // PHASE 5: PlayerSettings & EditorBuildSettings
            // ---------------------------------------------------------
            ConfigureBuildAndPlayerSettings(sb);

            // Save dirty scene & assets
            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            sb.AppendLine($"Summary: AlwaysIncludedShadersAdded={shadersAdded}, SceneSaved={activeScene.path}");
            return sb.ToString();
        }

        private static int EnsureAlwaysIncludedShaders(System.Text.StringBuilder sb)
        {
            int added = 0;
            var gsObjs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (gsObjs == null || gsObjs.Length == 0)
            {
                sb.AppendLine("[Phase 2] ERROR: GraphicsSettings.asset could not be loaded.");
                return 0;
            }

            var gsSO = new SerializedObject(gsObjs[0]);
            var arrayProp = gsSO.FindProperty("m_AlwaysIncludedShaders");
            var existing = new HashSet<Shader>();
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var sh = arrayProp.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (sh != null) existing.Add(sh);
            }

            foreach (var shaderName in RequiredAlwaysIncludedShaders)
            {
                var sh = Shader.Find(shaderName);
                if (sh == null)
                {
                    sb.AppendLine($"[Phase 2] WARN: Shader not found -> {shaderName}");
                    continue;
                }

                if (!existing.Contains(sh))
                {
                    int idx = arrayProp.arraySize;
                    arrayProp.InsertArrayElementAtIndex(idx);
                    arrayProp.GetArrayElementAtIndex(idx).objectReferenceValue = sh;
                    existing.Add(sh);
                    added++;
                    sb.AppendLine($"[Phase 2] Added AlwaysIncludedShader: {shaderName}");
                }
            }

            if (added > 0)
            {
                gsSO.ApplyModifiedPropertiesWithoutUndo();
            }
            sb.AppendLine($"[Phase 2] AlwaysIncludedShaders total={arrayProp.arraySize}, newlyAdded={added}");
            return added;
        }

        private static void EnsureHeatstrokeShaderSerialized(System.Text.StringBuilder sb)
        {
            var vfx = UnityEngine.Object.FindAnyObjectByType<SuperElNinoVFXController>();
            if (vfx == null)
            {
                sb.AppendLine("[Phase 3] ERROR: SuperElNinoVFXController missing in scene!");
                return;
            }

            var so = new SerializedObject(vfx);
            var shProp = so.FindProperty("_heatstrokeShader");
            var heatShader = Shader.Find("Scorched/PostProcessing/SuperElNinoHeatstroke");
            if (shProp != null && heatShader != null && shProp.objectReferenceValue != heatShader)
            {
                shProp.objectReferenceValue = heatShader;
                so.ApplyModifiedPropertiesWithoutUndo();
                sb.AppendLine("[Phase 3] Serialized _heatstrokeShader on SuperElNinoVFXController.");
            }
        }

        private static void EnsureBallisticsAndBulletPoolInScene(System.Text.StringBuilder sb)
        {
            var poolMgr = UnityEngine.Object.FindAnyObjectByType<BulletVisualPoolManager>();
            if (poolMgr == null)
            {
                var go = new GameObject("BulletVisualPoolManager");
                poolMgr = go.AddComponent<BulletVisualPoolManager>();
                sb.AppendLine("[Phase 3] Created BulletVisualPoolManager in scene.");
            }

            var tracerMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletTracer.mat");
            var sparkMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletImpactSparks.mat");
            var muzzleMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_MuzzleFlash.mat");
            var markMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletMark.mat");

            var pso = new SerializedObject(poolMgr);
            AssignIfNotNull(pso, "_tracerMaterial", tracerMat);
            AssignIfNotNull(pso, "_impactSparkMaterial", sparkMat);
            AssignIfNotNull(pso, "_muzzleFlashMaterial", muzzleMat);
            AssignIfNotNull(pso, "_bulletMarkMaterial", markMat);
            pso.ApplyModifiedPropertiesWithoutUndo();

            var ballistics = UnityEngine.Object.FindAnyObjectByType<BallisticsSimulationManager>();
            if (ballistics == null)
            {
                var go = new GameObject("BallisticsSimulationManager");
                ballistics = go.AddComponent<BallisticsSimulationManager>();
                sb.AppendLine("[Phase 3] Created BallisticsSimulationManager in scene.");
            }

            sb.AppendLine($"[Phase 3] BulletPool materials: tracer={(tracerMat != null)}, spark={(sparkMat != null)}, muzzle={(muzzleMat != null)}, mark={(markMat != null)}");
        }

        private static void AssignIfNotNull(SerializedObject so, string propName, UnityEngine.Object asset)
        {
            if (asset == null) return;
            var prop = so.FindProperty(propName);
            if (prop != null && prop.objectReferenceValue != asset)
            {
                prop.objectReferenceValue = asset;
            }
        }

        private static void VerifyNetworkPrefabs(System.Text.StringBuilder sb)
        {
            var dnp = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/DefaultNetworkPrefabs.asset");
            if (dnp == null)
            {
                sb.AppendLine("[Phase 4] ERROR: Assets/DefaultNetworkPrefabs.asset not found!");
                return;
            }

            var inList = new HashSet<GameObject>();
            foreach (var entry in dnp.PrefabList)
            {
                if (entry.Prefab != null) inList.Add(entry.Prefab);
            }

            int addedToDnp = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                var no = go.GetComponent<NetworkObject>();
                if (no != null && !inList.Contains(go))
                {
                    dnp.Add(new NetworkPrefab { Prefab = go });
                    inList.Add(go);
                    addedToDnp++;
                    sb.AppendLine($"[Phase 4] Added missing prefab to DefaultNetworkPrefabs.asset: {path}");
                }
            }
            if (addedToDnp > 0) EditorUtility.SetDirty(dnp);

            var nm = UnityEngine.Object.FindAnyObjectByType<NetworkManager>();
            if (nm != null && nm.NetworkConfig != null && nm.NetworkConfig.Prefabs != null)
            {
                bool hasList = nm.NetworkConfig.Prefabs.NetworkPrefabsLists != null &&
                               nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(dnp);
                if (!hasList && nm.NetworkConfig.Prefabs.NetworkPrefabsLists != null)
                {
                    nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(dnp);
                    EditorUtility.SetDirty(nm);
                    sb.AppendLine("[Phase 4] Attached DefaultNetworkPrefabs.asset to scene NetworkManager.");
                }
            }
            sb.AppendLine($"[Phase 4] NetworkPrefabs verified: DefaultListCount={dnp.PrefabList.Count}, Added={addedToDnp}");
        }

        private static void ConfigureBuildAndPlayerSettings(System.Text.StringBuilder sb)
        {
            // Ensure TargetScenePath is index 0 and enabled in EditorBuildSettings
            var scenes = new List<EditorBuildSettingsScene>();
            bool foundTarget = false;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (string.IsNullOrEmpty(s.path) || !File.Exists(s.path))
                {
                    sb.AppendLine($"[Phase 5] Removed missing scene from EditorBuildSettings: {s.path}");
                    continue;
                }
                if (s.path == TargetScenePath)
                {
                    foundTarget = true;
                    scenes.Insert(0, new EditorBuildSettingsScene(TargetScenePath, true));
                }
                else
                {
                    scenes.Add(s);
                }
            }
            if (!foundTarget)
            {
                scenes.Insert(0, new EditorBuildSettingsScene(TargetScenePath, true));
                sb.AppendLine($"[Phase 5] Added {TargetScenePath} at index 0 of EditorBuildSettings.");
            }
            EditorBuildSettings.scenes = scenes.ToArray();

            PlayerSettings.productName = "SCORCHED";
            PlayerSettings.companyName = "SCORCHED Studio";
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;

            sb.AppendLine($"[Phase 5] PlayerSettings applied: Product={PlayerSettings.productName}, Company={PlayerSettings.companyName}, FullScreen={PlayerSettings.fullScreenMode}, Resizable={PlayerSettings.resizableWindow}, RunInBackground={PlayerSettings.runInBackground}");
        }
    }
}
#endif
