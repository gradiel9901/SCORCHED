#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Scorched.Environment;
using UnityEditor;
using UnityEditor.Rendering.Universal;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Scorched.EditorTools
{
    public static class ScorchedAtmosphereWirer
    {
        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        private const string SkyboxShaderName = "Scorched/Sky/ScorchedVolumetricCloudsSkybox";
        private const string AtmosphereShaderName = "Scorched/PostProcessing/ScorchedVolumetricAtmosphere";

        private const string SkyboxMatPath = "Assets/Materials/M_ScorchedVolumetricSkybox.mat";
        private const string AtmosphereMatPath = "Assets/Materials/M_ScorchedVolumetricAtmosphere.mat";
        private const string DustParticleMatPath = "Assets/Materials/M_DesertDustPlume_Particle.mat";

        private const string DustPlumeTexPath = "Assets/Textures/Sky/desert_dust_plume.png";
        private const string CloudNoiseTexPath = "Assets/Textures/Sky/cloud_weather_noise.png";

        [MenuItem("SCORCHED/Setup Volumetric Atmosphere & Skybox")]
        public static void WireFromMenu()
        {
            string summary = SetupAndWireAll();
            Debug.Log(summary);
        }

        public static string SetupAndWireAll()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SCORCHED VOLUMETRIC ATMOSPHERE & SKYBOX SETUP ===");

            Scene active = SceneManager.GetActiveScene();
            if (active.path != TargetScenePath && File.Exists(TargetScenePath))
            {
                active = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            // 1. Texture Importers
            ConfigureTextureImporters(sb);

            // 2. Materials
            var skyMat = EnsureSkyboxMaterial(sb);
            var atmoMat = EnsureAtmosphereMaterial(sb);
            var dustMat = EnsureDustParticleMaterial(sb);

            // 3. Renderer Feature in PC_Renderer.asset
            EnsureRendererFeatureInPCRenderer(atmoMat, sb);

            // 4. Always Included Shaders
            EnsureAlwaysIncludedShaders(sb);

            // 5. Scene Objects: Controller & Particle Rig
            EnsureAtmosphereControllerInScene(skyMat, atmoMat, sb);
            EnsureParticleRigInScene(dustMat, sb);

            // 6. Camera Depth & Skybox assignment
            RenderSettings.skybox = skyMat;
            DynamicGI.UpdateEnvironment();

            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                cam.depthTextureMode |= DepthTextureMode.Depth;
                var uacd = cam.GetComponent<UniversalAdditionalCameraData>()
                           ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                uacd.requiresDepthOption = CameraOverrideOption.On;
            }

            // 7. Save Scene and Assets
            EditorSceneManager.MarkSceneDirty(active);
            EditorSceneManager.SaveScene(active);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            sb.AppendLine($"[Finished] SceneSaved='{active.path}', Skybox='{skyMat.name}', AtmoPass='{atmoMat.name}'");
            return sb.ToString();
        }

        private static void ConfigureTextureImporters(System.Text.StringBuilder sb)
        {
            if (File.Exists(DustPlumeTexPath))
            {
                var imp = AssetImporter.GetAtPath(DustPlumeTexPath) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = TextureImporterType.Default;
                    imp.alphaSource = TextureImporterAlphaSource.FromInput;
                    imp.alphaIsTransparency = true;
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.filterMode = FilterMode.Bilinear;
                    imp.sRGBTexture = true;
                    imp.SaveAndReimport();
                    sb.AppendLine("[Texture] Configured desert_dust_plume.png importer (Alpha Transparency).");
                }
            }

            if (File.Exists(CloudNoiseTexPath))
            {
                var imp = AssetImporter.GetAtPath(CloudNoiseTexPath) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = TextureImporterType.Default;
                    imp.alphaSource = TextureImporterAlphaSource.None;
                    imp.wrapMode = TextureWrapMode.Repeat;
                    imp.filterMode = FilterMode.Trilinear;
                    imp.sRGBTexture = false; // Linear FBM noise
                    imp.SaveAndReimport();
                    sb.AppendLine("[Texture] Configured cloud_weather_noise.png importer (Tileable Linear Noise).");
                }
            }
        }

        private static Material EnsureSkyboxMaterial(System.Text.StringBuilder sb)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            var sh = Shader.Find(SkyboxShaderName);
            if (mat == null)
            {
                mat = new Material(sh);
                AssetDatabase.CreateAsset(mat, SkyboxMatPath);
                sb.AppendLine($"[Material] Created {SkyboxMatPath}");
            }
            else if (mat.shader != sh)
            {
                mat.shader = sh;
            }

            var cloudTex = AssetDatabase.LoadAssetAtPath<Texture2D>(CloudNoiseTexPath);
            if (cloudTex != null)
            {
                mat.SetTexture("_CloudNoiseTex", cloudTex);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material EnsureAtmosphereMaterial(System.Text.StringBuilder sb)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AtmosphereMatPath);
            var sh = Shader.Find(AtmosphereShaderName);
            if (mat == null)
            {
                mat = new Material(sh);
                AssetDatabase.CreateAsset(mat, AtmosphereMatPath);
                sb.AppendLine($"[Material] Created {AtmosphereMatPath}");
            }
            else if (mat.shader != sh)
            {
                mat.shader = sh;
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material EnsureDustParticleMaterial(System.Text.StringBuilder sb)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(DustParticleMatPath);
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
            if (mat == null)
            {
                mat = new Material(sh);
                AssetDatabase.CreateAsset(mat, DustParticleMatPath);
                sb.AppendLine($"[Material] Created {DustParticleMatPath}");
            }
            else if (mat.shader != sh)
            {
                mat.shader = sh;
            }

            mat.SetFloat("_Surface", 1.0f); // Transparent
            mat.SetFloat("_Blend", 0.0f);   // Alpha blend
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0.0f);
            mat.SetFloat("_Cull", 0.0f);
            mat.SetColor("_BaseColor", new Color(1.0f, 0.88f, 0.72f, 0.85f));

            var plumeTex = AssetDatabase.LoadAssetAtPath<Texture2D>(DustPlumeTexPath);
            if (plumeTex != null)
            {
                mat.SetTexture("_BaseMap", plumeTex);
                mat.SetTexture("_MainTex", plumeTex);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void EnsureRendererFeatureInPCRenderer(Material atmoMat, System.Text.StringBuilder sb)
        {
            var rd = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>("Assets/Settings/PC_Renderer.asset");
            if (rd == null)
            {
                sb.AppendLine("[Renderer] ERROR: Assets/Settings/PC_Renderer.asset not found.");
                return;
            }

            FullScreenPassRendererFeature targetFeature = null;
            foreach (var rf in rd.rendererFeatures)
            {
                if (rf is FullScreenPassRendererFeature fsp && fsp.name == "ScorchedVolumetricAtmosphere_Pass")
                {
                    targetFeature = fsp;
                    break;
                }
            }

            if (targetFeature == null)
            {
                targetFeature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                targetFeature.name = "ScorchedVolumetricAtmosphere_Pass";
                targetFeature.SetActive(true);
                targetFeature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingTransparents;
                targetFeature.fetchColorBuffer = true;
                targetFeature.requirements = ScriptableRenderPassInput.Depth;
                targetFeature.passMaterial = atmoMat;

                AssetDatabase.AddObjectToAsset(targetFeature, rd);

                var so = new SerializedObject(rd);
                var featuresProp = so.FindProperty("m_RendererFeatures");
                int idx = featuresProp.arraySize;
                featuresProp.InsertArrayElementAtIndex(idx);
                featuresProp.GetArrayElementAtIndex(idx).objectReferenceValue = targetFeature;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(rd);
                sb.AppendLine("[Renderer] Added ScorchedVolumetricAtmosphere_Pass to PC_Renderer.asset.");
            }
            else
            {
                targetFeature.passMaterial = atmoMat;
                targetFeature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingTransparents;
                targetFeature.fetchColorBuffer = true;
                targetFeature.requirements = ScriptableRenderPassInput.Depth;
                targetFeature.SetActive(true);
                EditorUtility.SetDirty(targetFeature);
                EditorUtility.SetDirty(rd);
                sb.AppendLine("[Renderer] Verified ScorchedVolumetricAtmosphere_Pass in PC_Renderer.asset.");
            }
        }

        private static void EnsureAlwaysIncludedShaders(System.Text.StringBuilder sb)
        {
            var gsObjs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (gsObjs == null || gsObjs.Length == 0) return;

            var gsSO = new SerializedObject(gsObjs[0]);
            var arrayProp = gsSO.FindProperty("m_AlwaysIncludedShaders");
            var existing = new HashSet<Shader>();
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var s = arrayProp.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null) existing.Add(s);
            }

            string[] needed = { SkyboxShaderName, AtmosphereShaderName };
            int added = 0;
            foreach (var sname in needed)
            {
                var sh = Shader.Find(sname);
                if (sh != null && !existing.Contains(sh))
                {
                    int idx = arrayProp.arraySize;
                    arrayProp.InsertArrayElementAtIndex(idx);
                    arrayProp.GetArrayElementAtIndex(idx).objectReferenceValue = sh;
                    existing.Add(sh);
                    added++;
                    sb.AppendLine($"[GraphicsSettings] Added AlwaysIncludedShader: {sname}");
                }
            }
            if (added > 0)
            {
                gsSO.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void EnsureAtmosphereControllerInScene(Material skyMat, Material atmoMat, System.Text.StringBuilder sb)
        {
            var ctrl = UnityEngine.Object.FindAnyObjectByType<ScorchedAtmosphereController>();
            if (ctrl == null)
            {
                var go = new GameObject("_ScorchedAtmosphereController");
                ctrl = go.AddComponent<ScorchedAtmosphereController>();
                sb.AppendLine("[Scene] Created _ScorchedAtmosphereController GameObject.");
            }

            var skySh = Shader.Find(SkyboxShaderName);
            var atmoSh = Shader.Find(AtmosphereShaderName);
            ctrl.SetReferences(skyMat, skySh, atmoMat, atmoSh);
            EditorUtility.SetDirty(ctrl);
        }

        private static void EnsureParticleRigInScene(Material dustMat, System.Text.StringBuilder sb)
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<ScorchedAtmosphericParticles>();
            GameObject rootGo;
            if (rig == null)
            {
                rootGo = new GameObject("_VolumetricDesertWind_VFX");
                rig = rootGo.AddComponent<ScorchedAtmosphericParticles>();
                sb.AppendLine("[Scene] Created _VolumetricDesertWind_VFX GameObject.");
            }
            else
            {
                rootGo = rig.gameObject;
            }

            // Layer A: Ground Sand Wisps
            Transform wispsTr = rootGo.transform.Find("GroundSandWisps");
            ParticleSystem psWisps;
            if (wispsTr == null)
            {
                var wispGo = new GameObject("GroundSandWisps");
                wispGo.transform.SetParent(rootGo.transform, false);
                wispGo.transform.localPosition = new Vector3(0f, 1.2f, 0f);
                psWisps = wispGo.AddComponent<ParticleSystem>();
            }
            else
            {
                psWisps = wispsTr.GetComponent<ParticleSystem>();
            }

            var wMain = psWisps.main;
            wMain.startLifetime = new ParticleSystem.MinMaxCurve(7.0f, 12.0f);
            wMain.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            wMain.startSize = new ParticleSystem.MinMaxCurve(7.0f, 13.0f);
            wMain.startColor = new Color(0.92f, 0.76f, 0.52f, 0.11f);
            wMain.maxParticles = 80;
            wMain.loop = true;
            wMain.simulationSpace = ParticleSystemSimulationSpace.World;

            var wEmission = psWisps.emission;
            wEmission.rateOverTime = 14f;

            var wShape = psWisps.shape;
            wShape.shapeType = ParticleSystemShapeType.Box;
            wShape.scale = new Vector3(65f, 3f, 65f);

            var wVol = psWisps.velocityOverLifetime;
            wVol.enabled = true;
            wVol.space = ParticleSystemSimulationSpace.World;
            wVol.x = new ParticleSystem.MinMaxCurve(3.5f, 5.5f);
            wVol.y = new ParticleSystem.MinMaxCurve(-0.1f, 0.35f);
            wVol.z = new ParticleSystem.MinMaxCurve(1.5f, 2.8f);

            var wSol = psWisps.sizeOverLifetime;
            wSol.enabled = true;
            AnimationCurve wCurve = new AnimationCurve();
            wCurve.AddKey(0f, 0.6f);
            wCurve.AddKey(0.5f, 1.0f);
            wCurve.AddKey(1f, 1.35f);
            wSol.size = new ParticleSystem.MinMaxCurve(1f, wCurve);

            var wCol = psWisps.colorOverLifetime;
            wCol.enabled = true;
            Gradient wGrad = new Gradient();
            wGrad.SetKeys(
                new[] { new GradientColorKey(new Color(0.95f, 0.80f, 0.55f), 0f), new GradientColorKey(new Color(0.88f, 0.68f, 0.44f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.12f, 0.25f), new GradientAlphaKey(0.11f, 0.70f), new GradientAlphaKey(0f, 1f) });
            wCol.color = wGrad;

            var wRend = psWisps.GetComponent<ParticleSystemRenderer>();
            wRend.material = dustMat;
            wRend.renderMode = ParticleSystemRenderMode.Billboard;

            // Layer B: Airborne Dust Motes
            Transform motesTr = rootGo.transform.Find("AirborneDustMotes");
            ParticleSystem psMotes;
            if (motesTr == null)
            {
                var moteGo = new GameObject("AirborneDustMotes");
                moteGo.transform.SetParent(rootGo.transform, false);
                moteGo.transform.localPosition = new Vector3(0f, 4.0f, 0f);
                psMotes = moteGo.AddComponent<ParticleSystem>();
            }
            else
            {
                psMotes = motesTr.GetComponent<ParticleSystem>();
            }

            var mMain = psMotes.main;
            mMain.startLifetime = new ParticleSystem.MinMaxCurve(5.0f, 9.0f);
            mMain.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            mMain.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            mMain.startColor = new Color(1.0f, 0.90f, 0.62f, 0.55f);
            mMain.maxParticles = 250;
            mMain.loop = true;
            mMain.simulationSpace = ParticleSystemSimulationSpace.World;

            var mEmission = psMotes.emission;
            mEmission.rateOverTime = 42f;

            var mShape = psMotes.shape;
            mShape.shapeType = ParticleSystemShapeType.Box;
            mShape.scale = new Vector3(45f, 12f, 45f);

            var mVol = psMotes.velocityOverLifetime;
            mVol.enabled = true;
            mVol.space = ParticleSystemSimulationSpace.World;
            mVol.x = new ParticleSystem.MinMaxCurve(2.0f, 3.8f);
            mVol.y = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
            mVol.z = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);

            var mCol = psMotes.colorOverLifetime;
            mCol.enabled = true;
            Gradient mGrad = new Gradient();
            mGrad.SetKeys(
                new[] { new GradientColorKey(new Color(1.0f, 0.92f, 0.68f), 0f), new GradientColorKey(new Color(1.0f, 0.82f, 0.48f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.65f, 0.2f), new GradientAlphaKey(0.60f, 0.8f), new GradientAlphaKey(0f, 1f) });
            mCol.color = mGrad;

            var mRend = psMotes.GetComponent<ParticleSystemRenderer>();
            mRend.material = dustMat;
            mRend.renderMode = ParticleSystemRenderMode.Billboard;

            rig.AssignSystems(psWisps, psMotes);
            EditorUtility.SetDirty(rig);
            sb.AppendLine("[Scene] Configured _VolumetricDesertWind_VFX (Layer A Ground Sand Wisps + Layer B Airborne Motes).");
        }
    }
}
#endif
