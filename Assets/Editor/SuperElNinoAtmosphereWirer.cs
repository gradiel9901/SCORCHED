using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Scorched.Player;
using Scorched.Survival;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Configures Phase 1-4 "SUPER EL NINO" atmosphere, Global Volume Profile,
    /// M_SuperElNinoHeatstroke material, URP FullScreenPassRendererFeature,
    /// TerrainLayer roughness fix, Directional Light, Fog, and SuperElNinoVFXController
    /// in Assets/Scenes/Level 0 - Test Environment.unity.
    /// </summary>
    public static class SuperElNinoAtmosphereWirer
    {
        private const string ScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string ShaderPath = "Assets/Shaders/SuperElNinoHeatstroke.shader";
        private const string MaterialPath = "Assets/Materials/M_SuperElNinoHeatstroke.mat";
        private const string ProfilePath = "Assets/Settings/SuperElNino_VolumeProfile.asset";
        private const string FeatureName = "SuperElNinoHeatstroke_FullScreenPass";
        private const string VolumeObjectName = "_SuperElNino_GlobalVolume";

        [MenuItem("SCORCHED/Wire Super El Nino Atmosphere & VFX")]
        public static void WireAll()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 1. Shader & Material (Assets/Materials/M_SuperElNinoHeatstroke.mat)
            Shader heatShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (heatShader == null)
            {
                throw new Exception($"Shader not found at '{ShaderPath}'.");
            }

            EnsureFolder("Assets/Materials");
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(heatShader);
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            else if (mat.shader != heatShader)
            {
                mat.shader = heatShader;
            }

            ColorUtility.TryParseHtmlString("#B81408", out Color vigCol);
            mat.SetFloat("_ExposureNormalized", 0f);
            mat.SetFloat("_AmbientHeatHaze", 0f);
            mat.SetFloat("_VignetteIntensity", 0f);
            mat.SetFloat("_VignetteRadius", 0.55f);
            mat.SetColor("_VignetteColor", vigCol);
            mat.SetFloat("_HeatHazeStrength", 0f);
            mat.SetFloat("_HeatHazeSpeed", 2.8f);
            mat.SetFloat("_HeatHazeFrequency", 12.0f);
            mat.SetFloat("_DoubleVisionIntensity", 0f);
            mat.SetFloat("_DoubleVisionSpeed", 3.5f);
            EditorUtility.SetDirty(mat);

            // 2. Global Volume Profile (Assets/Settings/SuperElNino_VolumeProfile.asset)
            EnsureFolder("Assets/Settings");
            VolumeProfile profile = BuildOrUpdateVolumeProfile();

            // 3. Configure URP Renderers (PC_Renderer + Mobile_Renderer) with FullScreenPassRendererFeature
            int rendererCount = WireRenderers(mat);

            // 4. Configure Scene Directional Light & HeatwaveHazardManager
            ColorUtility.TryParseHtmlString("#FFE8B8", out Color sunColor);
            ColorUtility.TryParseHtmlString("#FFD28A", out Color surgeSunColor);
            ColorUtility.TryParseHtmlString("#D8A974", out Color fogColor);
            ColorUtility.TryParseHtmlString("#E09250", out Color surgeFogColor);

            Light sunLight = null;
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l != null && l.type == LightType.Directional)
                {
                    sunLight = l;
                    Undo.RecordObject(l, "Configure Super El Nino Sun");
                    l.color = sunColor;
                    l.intensity = 2.4f;
                    l.shadows = LightShadows.Soft;
                    l.shadowStrength = 0.88f;
                    EditorUtility.SetDirty(l);
                    break;
                }
            }

            if (sunLight != null)
            {
                RenderSettings.sun = sunLight;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = 0.0045f;

            var hazardMgr = UnityEngine.Object.FindFirstObjectByType<HeatwaveHazardManager>();
            if (hazardMgr != null)
            {
                var hSo = new SerializedObject(hazardMgr);
                if (sunLight != null) hSo.FindProperty("_sunLight").objectReferenceValue = sunLight;
                SetFloat(hSo, "_normalSunIntensity", 2.4f);
                SetFloat(hSo, "_heatwaveSunIntensity", 3.4f);
                SetColor(hSo, "_normalSunColor", sunColor);
                SetColor(hSo, "_heatwaveSunColor", surgeSunColor);
                SetBool(hSo, "_modulateAtmosphericFog", true);
                SetColor(hSo, "_normalFogColor", fogColor);
                SetColor(hSo, "_heatwaveFogColor", surgeFogColor);
                SetFloat(hSo, "_normalFogDensity", 0.0045f);
                SetFloat(hSo, "_heatwaveFogDensity", 0.0085f);
                hSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(hazardMgr);
            }

            // 5. Lower TerrainLayer smoothness to 0.05f & metallic to 0.0f
            int fixedLayers = 0;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include))
            {
                if (t == null || t.terrainData == null || t.terrainData.terrainLayers == null) continue;
                foreach (var tl in t.terrainData.terrainLayers)
                {
                    if (tl == null) continue;
                    tl.smoothness = 0.05f;
                    tl.metallic = 0.0f;
                    EditorUtility.SetDirty(tl);
                    fixedLayers++;
                }
            }

            // 6. Create/Configure _SuperElNino_GlobalVolume + SuperElNinoVFXController
            GameObject volGo = GameObject.Find(VolumeObjectName);
            if (volGo == null)
            {
                volGo = new GameObject(VolumeObjectName);
            }
            volGo.layer = 0; // Default layer

            var vol = volGo.GetComponent<Volume>() ?? volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.weight = 1f;
            vol.sharedProfile = profile;
            vol.enabled = true;

            var vfx = volGo.GetComponent<SuperElNinoVFXController>() ?? volGo.AddComponent<SuperElNinoVFXController>();
            var vfxSo = new SerializedObject(vfx);
            vfxSo.FindProperty("_heatstrokeMaterial").objectReferenceValue = mat;
            vfxSo.FindProperty("_globalVolume").objectReferenceValue = vol;
            SetColor(vfxSo, "_vignetteColor", vigCol);
            vfxSo.FindProperty("TestExposureOverride").floatValue = 0f;
            vfxSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(vfx);
            EditorUtility.SetDirty(volGo);

            // 7. Ensure Post Processing enabled on any scene cameras & player prefabs
            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                EnableCamPostProcessing(cam);
            }

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject check = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (check == null || check.GetComponentInChildren<Camera>(true) == null) continue;
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root != null)
                {
                    foreach (var cam in root.GetComponentsInChildren<Camera>(true))
                    {
                        EnableCamPostProcessing(cam);
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[SuperElNinoAtmosphereWirer] Complete: Sun={(sunLight != null ? sunLight.name : "NONE")}, TerrainLayersFixed={fixedLayers}, RenderersWired={rendererCount}, Profile={ProfilePath}, Material={MaterialPath}");
        }

        private static VolumeProfile BuildOrUpdateVolumeProfile()
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            // Tonemapping: ACES
            var tonemapping = GetOrAddComponent<Tonemapping>(profile);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;
            EditorUtility.SetDirty(tonemapping);

            // Color Adjustments
            ColorUtility.TryParseHtmlString("#FFF2D6", out Color filterCol);
            var colorAdj = GetOrAddComponent<ColorAdjustments>(profile);
            colorAdj.postExposure.overrideState = true;
            colorAdj.postExposure.value = 0.35f;
            colorAdj.contrast.overrideState = true;
            colorAdj.contrast.value = 18f;
            colorAdj.colorFilter.overrideState = true;
            colorAdj.colorFilter.value = filterCol;
            colorAdj.saturation.overrideState = true;
            colorAdj.saturation.value = 8f;
            EditorUtility.SetDirty(colorAdj);

            // White Balance
            var whiteBal = GetOrAddComponent<WhiteBalance>(profile);
            whiteBal.temperature.overrideState = true;
            whiteBal.temperature.value = 28f;
            whiteBal.tint.overrideState = true;
            whiteBal.tint.value = 6f;
            EditorUtility.SetDirty(whiteBal);

            // Split Toning
            ColorUtility.TryParseHtmlString("#2E1D14", out Color splitShadow);
            ColorUtility.TryParseHtmlString("#FFE6A3", out Color splitHigh);
            var split = GetOrAddComponent<SplitToning>(profile);
            split.shadows.overrideState = true;
            split.shadows.value = splitShadow;
            split.highlights.overrideState = true;
            split.highlights.value = splitHigh;
            split.balance.overrideState = true;
            split.balance.value = 15f;
            EditorUtility.SetDirty(split);

            // Bloom
            ColorUtility.TryParseHtmlString("#FFE099", out Color bloomTint);
            var bloom = GetOrAddComponent<Bloom>(profile);
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 0.85f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 1.4f;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.72f;
            bloom.tint.overrideState = true;
            bloom.tint.value = bloomTint;
            bloom.highQualityFiltering.overrideState = true;
            bloom.highQualityFiltering.value = true;
            EditorUtility.SetDirty(bloom);

            // Vignette (Base Ambient)
            var vignette = GetOrAddComponent<Vignette>(profile);
            vignette.color.overrideState = true;
            vignette.color.value = splitShadow;
            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.22f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.35f;
            EditorUtility.SetDirty(vignette);

            // Chromatic Aberration & Lens Distortion (driven dynamically in Stage 3)
            var chroma = GetOrAddComponent<ChromaticAberration>(profile);
            chroma.intensity.overrideState = true;
            chroma.intensity.value = 0f;
            EditorUtility.SetDirty(chroma);

            var lens = GetOrAddComponent<LensDistortion>(profile);
            lens.intensity.overrideState = true;
            lens.intensity.value = 0f;
            lens.scale.overrideState = true;
            lens.scale.value = 1.0f;
            EditorUtility.SetDirty(lens);

            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static T GetOrAddComponent<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing) && existing != null)
            {
                return existing;
            }

            T added = profile.Add<T>(true);
            added.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(added, profile);
            return added;
        }

        private static int WireRenderers(Material passMat)
        {
            int count = 0;
            string[] rendererGuids = AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets/Settings" });
            foreach (string guid in rendererGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (rd == null) continue;

                FullScreenPassRendererFeature feature = null;
                foreach (var f in rd.rendererFeatures)
                {
                    if (f is FullScreenPassRendererFeature fs && fs.name == FeatureName)
                    {
                        feature = fs;
                        break;
                    }
                }

                if (feature == null)
                {
                    feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                    feature.name = FeatureName;
                    AssetDatabase.AddObjectToAsset(feature, rd);

                    var rdSo = new SerializedObject(rd);
                    var featuresProp = rdSo.FindProperty("m_RendererFeatures");
                    int idx = featuresProp.arraySize;
                    featuresProp.InsertArrayElementAtIndex(idx);
                    featuresProp.GetArrayElementAtIndex(idx).objectReferenceValue = feature;
                    rdSo.ApplyModifiedPropertiesWithoutUndo();
                }

                feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
                feature.fetchColorBuffer = true;
                feature.requirements = ScriptableRenderPassInput.None;
                feature.passMaterial = passMat;
                feature.passIndex = 0;
                feature.bindDepthStencilAttachment = false;
                feature.SetActive(true);

                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(rd);
                rd.SetDirty();
                count++;
            }
            return count;
        }

        private static void EnableCamPostProcessing(Camera cam)
        {
            if (cam == null) return;
            var cd = cam.GetComponent<UniversalAdditionalCameraData>()
                     ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            cd.renderPostProcessing = true;
            cd.volumeLayerMask = ~0;
            EditorUtility.SetDirty(cd);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            if (slash > 0)
            {
                string parent = folder.Substring(0, slash);
                string child = folder.Substring(slash + 1);
                EnsureFolder(parent);
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static void SetFloat(SerializedObject so, string propName, float val)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.floatValue = val;
        }

        private static void SetBool(SerializedObject so, string propName, bool val)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.boolValue = val;
        }

        private static void SetColor(SerializedObject so, string propName, Color val)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.colorValue = val;
        }
    }
}

