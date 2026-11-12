#if UNITY_EDITOR
using System.Collections.Generic;
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
    public static class TestShadeDurabilityVerification
    {
        [MenuItem("Tools/SCORCHED/QA/Verify Shelter Durability & Ash System")]
        public static void RunVerification()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== VERIFY SHELTER DURABILITY, ASH COLLAPSE & OPTIMIZATIONS ===");

            Scene scene = SceneManager.GetActiveScene();
            sb.AppendLine($"[0] Active Scene Path: '{scene.path}'");
            if (scene.path != "Assets/Scenes/Level 0 - Test Environment.unity")
            {
                scene = EditorSceneManager.OpenScene("Assets/Scenes/Level 0 - Test Environment.unity", OpenSceneMode.Single);
                sb.AppendLine($"[0b] Opened Scene: '{scene.path}'");
            }

            var rawFound = Object.FindObjectsByType<ShadeZoneBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"[0c] Raw FindObjectsByType ShadeZoneBuilding: {rawFound.Length}");

            // 1. Check PhysX queriesHitBackfaces
            bool backfaces = Physics.queriesHitBackfaces;
            sb.AppendLine($"[1] Physics.queriesHitBackfaces: {backfaces} (Must be True)");

            // 2. Check ShadeZoneBuilding count
            ShadeZoneBuilding.RefreshRegistryOrder();
            var shades = ShadeZoneBuilding.AllShades;
            sb.AppendLine($"[2] Registered ShadeZoneBuildings: {shades.Count}");
            int validCoverCount = 0;
            for (int i = 0; i < shades.Count; i++)
            {
                var s = shades[i];
                if (s != null && s.TriggerVolume != null)
                {
                    Vector3 center = s.TriggerVolume.transform.TransformPoint(s.TriggerVolume.center);
                    if (s.VerifyOverheadCover(center)) validCoverCount++;
                }
            }
            sb.AppendLine($"[2b] Shades with Verified Overhead Cover: {validCoverCount}/{shades.Count}");

            // 3. Test outdoor sky detection (open sky point must return false)
            Vector3 openSkyPos = new Vector3(0f, 1f, 0f);
            bool openSkyShade = false;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null && shades[i].ContainsWorldPoint(openSkyPos) && shades[i].VerifyOverheadCover(openSkyPos))
                {
                    openSkyShade = true;
                    break;
                }
            }
            sb.AppendLine($"[3] Open Sky Point (0, 1, 0) in Shade: {openSkyShade} (Must be False)");

            // 4. Check HeatwaveHazardManager target selection
            var hazard = Object.FindAnyObjectByType<HeatwaveHazardManager>();
            sb.AppendLine($"[4] HeatwaveHazardManager in Scene: {(hazard != null ? "Found" : "Missing")}");

            // 5. Check ScorchedHUDController
            var hud = Object.FindAnyObjectByType<ScorchedHUDController>();
            sb.AppendLine($"[5] ScorchedHUDController in Scene: {(hud != null ? "Found" : "Missing")}");

            // 6. Check Shaders
            Shader ashShader = Shader.Find("Scorched/VFX/AshDisintegration");
            Shader beaconShader = Shader.Find("Scorched/VFX/SupplyBeacon");
            Shader skyShader = Shader.Find("Scorched/Sky/ScorchedVolumetricCloudsSkybox");
            Shader atmoShader = Shader.Find("Scorched/PostProcessing/ScorchedVolumetricAtmosphere");
            sb.AppendLine($"[6] AshDisintegration Shader: {(ashShader != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[6] SupplyBeacon Shader: {(beaconShader != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[6] VolumetricClouds Sky Shader: {(skyShader != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[6] VolumetricAtmosphere Shader: {(atmoShader != null ? "OK" : "MISSING")}");

            // 7. Check Resources VFX materials
            var smokeMat = Resources.Load<Material>("VFX/M_SupplyDropSmoke");
            var emberMat = Resources.Load<Material>("VFX/M_SupplyDropEmbers");
            var beaconMat = Resources.Load<Material>("VFX/M_SupplyBeacon");
            var dissolveNoise = Resources.Load<Texture2D>("VFX/T_VFX_AshDissolveNoise");
            sb.AppendLine($"[7] Resources VFX Smoke Material: {(smokeMat != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[7] Resources VFX Ember Material: {(emberMat != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[7] Resources VFX Beacon Material: {(beaconMat != null ? "OK" : "MISSING")}");
            sb.AppendLine($"[7] Resources VFX Dissolve Noise: {(dissolveNoise != null ? "OK" : "MISSING")}");

            Debug.Log(sb.ToString());
        }
    }
}
#endif
