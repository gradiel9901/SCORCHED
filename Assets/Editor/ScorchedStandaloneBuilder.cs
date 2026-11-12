#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Scorched.EditorTools;
using Scorched.Environment;
using Scorched.Survival;
using Scorched.UI;
using Scorched.Player;

namespace Scorched.EditorTools
{
    public static class ScorchedStandaloneBuilder
    {
        public const string BuildOutputDirectory = "Builds/Windows";
        public const string ExecutableName = "SCORCHED.exe";
        public const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        public static string BuildAndReturnReport()
        {
            BuildReport rep = BuildStandaloneWindows64();
            return "RESULT=" + rep.summary.result + ", ERRORS=" + rep.summary.totalErrors + ", SIZE=" + rep.summary.totalSize;
        }

        [MenuItem("Tools/SCORCHED/Build/Build Windows Standalone (Auto-Prepared)")]
        public static void BuildStandaloneFromMenu()
        {
            BuildReport report = BuildStandaloneWindows64();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.DisplayDialog("Build Succeeded", 
                    $"SCORCHED Windows 64-bit build completed successfully!\nPath: {Path.GetFullPath(Path.Combine(BuildOutputDirectory, ExecutableName))}\nSize: {report.summary.totalSize / (1024 * 1024):F1} MB", 
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Build Failed", 
                    $"Build failed or had errors. Check the Unity Console for details.", 
                    "OK");
            }
        }

        public static BuildReport BuildStandaloneWindows64()
        {
            var log = new StringBuilder();
            log.AppendLine("==================================================================");
            log.AppendLine("         SCORCHED WINDOWS STANDALONE RELEASE BUILD PIPELINE       ");
            log.AppendLine("==================================================================");

            // Step 1: Open Target Scene
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.path != TargetScenePath)
            {
                log.AppendLine($"[Build Pipeline] Opening target scene: {TargetScenePath}");
                activeScene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            // Step 2: Release Preparer (Always-Included Shaders, NGO Prefabs, Settings)
            log.AppendLine("[Build Pipeline] Executing ScorchedWindowsReleasePreparer...");
            string preparerReport = ScorchedWindowsReleasePreparer.PrepareAndAudit();
            log.AppendLine(preparerReport);

            // Step 3: Glove-Tight 1:1 Mesh Collider & Static Batching Audit
            log.AppendLine("[Build Pipeline] Executing GloveTightBuildPreprocessor & Attach...");
            string gloveReport = GloveTightBuildPreprocessor.AttachAndBakeAllSceneStructures();
            log.AppendLine(gloveReport);

            // Step 4: Verification of Gameplay Systems
            log.AppendLine("[Build Pipeline] Verifying Gameplay Systems...");
            VerifySceneGameplayIntegrations(log);

            // Step 5: Save Scene and AssetDatabase
            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Step 6: Configure Output Directory
            string projectRoot = Directory.GetCurrentDirectory();
            string outputDir = Path.Combine(projectRoot, BuildOutputDirectory);
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                log.AppendLine($"[Build Pipeline] Created output directory: {outputDir}");
            }
            string executablePath = Path.Combine(outputDir, ExecutableName);

            // Step 7: Build Player Options
            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = new[] { TargetScenePath },
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            log.AppendLine($"[Build Pipeline] Starting BuildPlayer: Target={buildPlayerOptions.target}, Output={executablePath}");
            Debug.Log(log.ToString());

            // Step 8: Execute Build
            DateTime startTime = DateTime.Now;
            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            TimeSpan duration = DateTime.Now - startTime;

            // Step 9: Report Analysis
            var reportSummary = new StringBuilder();
            reportSummary.AppendLine("==================================================================");
            reportSummary.AppendLine($"   BUILD RESULT: {report.summary.result} in {duration.TotalSeconds:F1}s");
            reportSummary.AppendLine("==================================================================");
            reportSummary.AppendLine($"Executable Path: {executablePath}");
            reportSummary.AppendLine($"Total Output Size: {report.summary.totalSize / (1024 * 1024):F1} MB");
            reportSummary.AppendLine($"Total Errors: {report.summary.totalErrors}");
            reportSummary.AppendLine($"Total Warnings: {report.summary.totalWarnings}");

            if (report.summary.result == BuildResult.Succeeded)
            {
                reportSummary.AppendLine(">>> BUILD STATUS: SUCCESS - Ready for Windows distribution!");
                Debug.Log(reportSummary.ToString());
            }
            else
            {
                reportSummary.AppendLine($">>> BUILD STATUS: FAILED with {report.summary.totalErrors} errors.");
                Debug.LogError(reportSummary.ToString());
            }

            return report;
        }

        private static void VerifySceneGameplayIntegrations(StringBuilder log)
        {
            // 1. Shade safe-zones
            var shadeBuildings = UnityEngine.Object.FindObjectsByType<ShadeZoneBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            log.AppendLine($"[Verify] ShadeZoneBuilding instances in scene: {shadeBuildings.Length} (Expect 33)");

            // 2. Thermal & Heatwave Controller
            var thermal = UnityEngine.Object.FindAnyObjectByType<PlayerThermalController>();
            var vfx = UnityEngine.Object.FindAnyObjectByType<SuperElNinoVFXController>();
            var hazard = UnityEngine.Object.FindAnyObjectByType<HeatwaveHazardManager>();
            var atmo = UnityEngine.Object.FindAnyObjectByType<ScorchedAtmosphereController>();

            log.AppendLine($"[Verify] Thermal: {(thermal != null ? "Found" : "Missing")}, " +
                           $"VFX: {(vfx != null ? "Found" : "Missing")}, " +
                           $"Hazard: {(hazard != null ? "Found" : "Missing")}, " +
                           $"Atmo: {(atmo != null ? "Found" : "Missing")}");

            // 3. HUD Controller & Canvas
            var hud = UnityEngine.Object.FindAnyObjectByType<ScorchedHUDController>();
            log.AppendLine($"[Verify] ScorchedHUDController: {(hud != null ? "Found" : "Missing")}");

            // 4. GloveTightColliders count
            var gloveColliders = UnityEngine.Object.FindObjectsByType<GloveTightCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int totalBound = 0;
            foreach (var g in gloveColliders) totalBound += g.BoundCount;
            log.AppendLine($"[Verify] GloveTightCollider hosts: {gloveColliders.Length}, Total bound colliders: {totalBound}");
        }
    }
}
#endif
