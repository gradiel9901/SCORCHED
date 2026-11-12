#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Environment;
using Scorched.Inventory;
using Scorched.Player;
using Scorched.Survival;
using Scorched.UI;

namespace Scorched.Editor
{
    public static class PlayModeShelterAndAshQA
    {
        private const string ScreenshotDir = "C:/Users/JM/.gemini/antigravity/brain/a36b9384-20ca-483a-b0fc-07b4e8403a64";

        [MenuItem("Tools/SCORCHED/QA/Run PlayMode Shelter & Ash QA")]
        public static void RunPlayModeQA()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Level 0 - Test Environment.unity", OpenSceneMode.Single);

            // Create temporary QA runner in scene
            var existing = GameObject.Find("QARunner_ShelterAndAsh");
            if (existing != null) Object.DestroyImmediate(existing);

            var runnerGo = new GameObject("QARunner_ShelterAndAsh");
            runnerGo.AddComponent<QARunnerComponent>();

            EditorApplication.isPlaying = true;
        }

        public class QARunnerComponent : MonoBehaviour
        {
            private void Start()
            {
                StartCoroutine(ExecuteQAFlow());
            }

            public IEnumerator ExecuteQAFlow()
            {
                Debug.Log("[PlayModeQA] === STARTING SHELTER DURABILITY & ASH COLLAPSE VERIFICATION ===");
                yield return new WaitForSeconds(1.5f);

                // 1. Locate local player and HUD
                var hud = ScorchedHUDController.Instance;
                var hazard = HeatwaveHazardManager.Instance;
                var thermal = FindAnyObjectByType<PlayerThermalController>();

                Debug.Log($"[PlayModeQA] Local Thermal: {(thermal != null ? "Found" : "Missing")}");
                Debug.Log($"[PlayModeQA] HUD Controller: {(hud != null ? "Found" : "Missing")}");
                Debug.Log($"[PlayModeQA] Hazard Manager: {(hazard != null ? "Found" : "Missing")}");

                // 2. Teleport player into Building #0 (Safe Zone)
                ShadeZoneBuilding.RefreshRegistryOrder();
                var shades = ShadeZoneBuilding.AllShades;
                if (shades.Count == 0)
                {
                    Debug.LogError("[PlayModeQA] No shades found!");
                    yield break;
                }

                ShadeZoneBuilding testBuilding = shades[0];
                Vector3 insidePos = testBuilding.TriggerVolume != null
                    ? testBuilding.TriggerVolume.transform.TransformPoint(testBuilding.TriggerVolume.center)
                    : testBuilding.transform.position;
                insidePos.y = testBuilding.transform.position.y + 0.15f;

                if (thermal != null)
                {
                    var cc = thermal.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    thermal.transform.position = insidePos;
                    if (cc != null) cc.enabled = true;
                    thermal.RefreshShadeContainment();
                }

                yield return new WaitForSeconds(1.0f);

                // Verify player is in shade and overhead cover is verified
                bool inShadeInside = thermal != null && thermal.IsInShade;
                Debug.Log($"[PlayModeQA] Step 1: Inside Building '{testBuilding.BuildingDisplayName}' -> InShade: {inShadeInside} (Expected: True)");

                // Capture Screenshot inside shelter showing the Shelter Durability Widget
                string path1 = Path.Combine(ScreenshotDir, "shelter_hud_inside_stable.png");
                ScreenCapture.CaptureScreenshot(path1);
                Debug.Log($"[PlayModeQA] Saved screenshot inside shelter to: {path1}");
                yield return new WaitForSeconds(0.8f);

                // 3. Test Heatwave Targeted Decay
                Debug.Log("[PlayModeQA] Step 2: Triggering Heatwave Active...");
                if (hazard != null)
                {
                    hazard.SetPhaseAuthoritative(MatchPhase.HeatwaveActive, 1, 30f);
                }

                // Find the randomly targeted building
                ShadeZoneBuilding targetedBuilding = null;
                for (int i = 0; i < shades.Count; i++)
                {
                    if (shades[i] != null && shades[i].IsTargetedByHeatwave)
                    {
                        targetedBuilding = shades[i];
                        break;
                    }
                }

                Debug.Log($"[PlayModeQA] Step 2: Random targeted building is '{(targetedBuilding != null ? targetedBuilding.BuildingDisplayName : "None")}'");
                float initialDur = targetedBuilding != null ? targetedBuilding.CurrentDurability : 100f;
                yield return new WaitForSeconds(2.0f);
                float durAfter2s = targetedBuilding != null ? targetedBuilding.CurrentDurability : 100f;
                Debug.Log($"[PlayModeQA] Step 2 Result: Targeted Building '{targetedBuilding?.BuildingDisplayName}' decayed from {initialDur:F1} to {durAfter2s:F1}");

                string path2 = Path.Combine(ScreenshotDir, "shelter_hud_heatwave_erosion.png");
                ScreenCapture.CaptureScreenshot(path2);
                Debug.Log($"[PlayModeQA] Saved screenshot during erosion to: {path2}");
                yield return new WaitForSeconds(0.8f);

                // 4. Test Ash Disintegration Collapse at Durability 0
                Debug.Log("[PlayModeQA] Step 3: Simulating Building Durability reaching 0 -> Ash Collapse!");
                // Spawn a test interior loot container inside the building to verify it also gets incinerated into ashes
                var chestGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                chestGo.name = "TestInteriorLootCrate";
                chestGo.transform.position = insidePos + new Vector3(1.5f, 0f, 1.5f);
                var lootInteractable = chestGo.AddComponent<LootContainerInteractable>();
                lootInteractable.ConfigureType(LootContainerType.InsideChest);

                yield return new WaitForSeconds(0.5f);

                // Drive durability to 0
                testBuilding.ApplySyncState(0f, true);

                yield return new WaitForSeconds(1.5f);
                bool buildingDestroyed = testBuilding.IsDestroyed;
                bool chestIncinerated = lootInteractable.IsIncinerated;
                Debug.Log($"[PlayModeQA] Step 3 Result: Building Destroyed: {buildingDestroyed}, Interior Crate Incinerated: {chestIncinerated}");

                string path3 = Path.Combine(ScreenshotDir, "building_ash_collapse_ruins.png");
                ScreenCapture.CaptureScreenshot(path3);
                Debug.Log($"[PlayModeQA] Saved ash collapse screenshot to: {path3}");
                yield return new WaitForSeconds(1.5f);

                // 5. Test Supply Drop Box Visuals (Laser Beacon & Particle VFX)
                Debug.Log("[PlayModeQA] Step 4: Spawning Supply Drop Box with custom VFX...");
                var dropBoxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Container/Drop Supply Box.prefab");
                if (dropBoxPrefab != null)
                {
                    var dropBoxInstance = Instantiate(dropBoxPrefab, insidePos + new Vector3(6f, 0f, 6f), Quaternion.identity);
                    yield return new WaitForSeconds(1.0f);
                    string path4 = Path.Combine(ScreenshotDir, "supply_drop_box_vfx.png");
                    ScreenCapture.CaptureScreenshot(path4);
                    Debug.Log($"[PlayModeQA] Saved supply drop box screenshot to: {path4}");
                }

                yield return new WaitForSeconds(1.0f);
                Debug.Log("[PlayModeQA] === ALL VERIFICATIONS COMPLETE ===");

                // Stop play mode
                EditorApplication.isPlaying = false;
            }
        }
    }
}
#endif
