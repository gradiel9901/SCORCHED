#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Environment;
using Scorched.Inventory;
using Scorched.Survival;

namespace Scorched.Editor
{
    public static class VerifySupplyDropAndVolumetricFog
    {
        private const string ScreenshotDir = "C:/Users/JM/.gemini/antigravity/brain/a36b9384-20ca-483a-b0fc-07b4e8403a64";

        [MenuItem("Tools/SCORCHED/QA/Verify In-Map Drops & Volumetric Fog")]
        public static void RunVerification()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.path != "Assets/Scenes/Level 0 - Test Environment.unity")
            {
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
                EditorSceneManager.OpenScene("Assets/Scenes/Level 0 - Test Environment.unity", OpenSceneMode.Single);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== VERIFY SUPPLY DROP IN-MAP LANDING & VOLUMETRIC FOG ===");

            // 1. Check ContainerSpawnManager settings
            var csm = Object.FindAnyObjectByType<ContainerSpawnManager>();
            sb.AppendLine($"[1] ContainerSpawnManager in Scene: {(csm != null ? "Found" : "Missing")}");

            // 2. Perform 10 simulated drop landings
            int successCount = 0;
            for (int i = 0; i < 10; i++)
            {
                var drop = csm.SpawnSupplyDropAuthoritative();
                if (drop != null)
                {
                    Vector3 landingPos = new Vector3(drop.transform.position.x, drop.TargetGroundY, drop.transform.position.z);
                    // Check bounds: X in [-38, 40], Z in [-52, 52]
                    bool inBoundsX = landingPos.x >= -38f && landingPos.x <= 40f;
                    bool inBoundsZ = landingPos.z >= -52f && landingPos.z <= 52f;
                    bool inBoundsY = landingPos.y >= 0f && landingPos.y <= 16f;

                    // Check shade containment (should be outside)
                    bool insideShade = false;
                    var shades = ShadeZoneBuilding.AllShades;
                    for (int s = 0; s < shades.Count; s++)
                    {
                        if (shades[s] != null && shades[s].ContainsWorldPoint(landingPos))
                        {
                            insideShade = true;
                            break;
                        }
                    }

                    bool valid = inBoundsX && inBoundsZ && inBoundsY && !insideShade;
                    if (valid) successCount++;

                    sb.AppendLine($"  Drop #{i + 1:D2}: Landing=({landingPos.x:F1}, {landingPos.y:F1}, {landingPos.z:F1}) | InTown={inBoundsX && inBoundsZ} | ValidY={inBoundsY} | OutsideShade={!insideShade} -> {(valid ? "PASS" : "FAIL")}");
                    
                    // Clean up test drop instance
                    Object.DestroyImmediate(drop.gameObject);
                }
            }
            sb.AppendLine($"[2] In-Map Drop Landing Verification: {successCount}/10 PASSED");

            // 3. Check Volumetric Fog settings on Atmosphere Material
            var atmoMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_ScorchedVolumetricAtmosphere.mat");
            if (atmoMat != null)
            {
                float fogDist = atmoMat.GetFloat("_FogStartDistance");
                float fogDens = atmoMat.GetFloat("_FogDensity");
                float fogMaxOp = atmoMat.GetFloat("_FogMaxOpacity");
                float godRays = atmoMat.GetFloat("_GodRayIntensity");
                sb.AppendLine($"[3] Material Volumetric Fog: StartDist={fogDist}m, Density={fogDens}, MaxOpacity={fogMaxOp}, GodRays={godRays}");
            }

            // 4. Check RenderSettings Fog
            sb.AppendLine($"[4] RenderSettings.fog: {RenderSettings.fog}, Density={RenderSettings.fogDensity}, Color={RenderSettings.fogColor}");

            Debug.Log(sb.ToString());

            // 5. Setup camera for dramatic volumetric fog street view screenshot
            bool createdCam = false;
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindAnyObjectByType<Camera>();
            if (cam == null)
            {
                var camGo = new GameObject("QA_Volumetric_Fog_Cam");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.fieldOfView = 65f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 1000f;
                createdCam = true;
            }

            var urpCamData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (urpCamData == null)
            {
                urpCamData = cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            }
            if (urpCamData != null)
            {
                urpCamData.renderPostProcessing = true;
            }

            Vector3 origCamPos = cam.transform.position;
            Quaternion origCamRot = cam.transform.rotation;

            // Position camera at south end of central boulevard looking north towards sunrise/atmosphere
            cam.transform.position = new Vector3(1.8f, 6.4f, -32.0f);
            cam.transform.rotation = Quaternion.Euler(0.0f, 0.0f, 0.0f);

            string outShot = Path.Combine(ScreenshotDir, "dramatic_volumetric_fog_street_view.png");
            RenderTexture rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            Object.DestroyImmediate(rt);

            File.WriteAllBytes(outShot, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log($"[5] Captured dramatic volumetric fog screenshot: {outShot}");

            // 6. Spawn a test crate at central crossroads and capture screenshot
            var demoCrate = csm.SpawnSupplyDropAuthoritative(new Vector2(1.8f, -2.0f));
            if (demoCrate != null)
            {
                demoCrate.transform.position = new Vector3(1.8f, demoCrate.TargetGroundY, -2.0f);
                
                cam.transform.position = new Vector3(1.8f, 5.8f, -14.0f);
                cam.transform.LookAt(new Vector3(1.8f, demoCrate.TargetGroundY + 0.8f, -2.0f));

                string crateShot = Path.Combine(ScreenshotDir, "in_map_supply_drop_and_fog.png");
                RenderTexture rtCrate = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rtCrate;
                cam.Render();
                cam.targetTexture = null;

                RenderTexture.active = rtCrate;
                Texture2D texCrate = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
                texCrate.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                texCrate.Apply();
                RenderTexture.active = null;
                Object.DestroyImmediate(rtCrate);

                File.WriteAllBytes(crateShot, texCrate.EncodeToPNG());
                Object.DestroyImmediate(texCrate);
                Debug.Log($"[6] Captured in-map supply drop screenshot: {crateShot}");

                Object.DestroyImmediate(demoCrate.gameObject);
            }

            if (createdCam)
            {
                Object.DestroyImmediate(cam.gameObject);
            }
            else
            {
                cam.transform.position = origCamPos;
                cam.transform.rotation = origCamRot;
            }
        }
    }
}
#endif

