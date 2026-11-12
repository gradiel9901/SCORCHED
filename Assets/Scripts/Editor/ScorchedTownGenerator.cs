using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Scorched.EditorTools
{
    public static class ScorchedTownGenerator
    {
        private const string TargetScenePath = "Assets/Scenes/SandBox/Map.unity";
        private const string TerrainDataAssetPath = "Assets/Scenes/SandBox/ScorchedTerrainData.asset";
        private const string TerrainLayersFolder = "Assets/Scenes/SandBox/TerrainLayers";
        private const string FactoryPrefabsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/Factory";
        private const string PropsPrefabsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/Props";
        private const string ChunksPrefabsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/FactoryChunks";
        private const string BackgroundsPrefabsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/Backgrounds";
        private const string FoliagePrefabsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/Foliage";
        private const string BuiltInMaterialsFolder = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Materials";
        private const string CommonTexturesFolder = "Assets/ScansFactory/AbandonedFactory_Common/Textures";
        private const string ReportFilePath = "Logs/ScorchedTownBuildReport.json";

        [Serializable]
        public class PrefabDiagnosticRecord
        {
            public string AssetPath;
            public string PrefabName;
            public Vector3 WorldSize;
            public float BottomYOffset;
            public Vector3 CenterOffset;
            public string Category;
        }

        [Serializable]
        public class BuildSummaryReport
        {
            public string Timestamp;
            public string ScenePath;
            public string RenderPipelineAsset;
            public int MaterialsChecked;
            public int MaterialsUpgradedToURP;
            public string GroundPlaneName;
            public Vector3 GroundPlaneCenter;
            public Vector3 GroundPlaneSize;
            public Vector3 TerrainSize;
            public Vector3 TerrainPosition;
            public int TerrainHeightmapResolution;
            public int TotalFactoryPrefabsMeasured;
            public int LargeBuildingsPlaced;
            public int MediumBuildingsPlaced;
            public int SmallStructuresPlaced;
            public int TotalBuildingsPlaced;
            public int IndoorInteriorsPaired;
            public int ShadeCanopiesPlaced;
            public int TacticalCoverPiecesPlaced;
            public int LootNodesGenerated;
            public int SpawnPointsGenerated;
            public List<PrefabDiagnosticRecord> FactoryPrefabDiagnostics = new List<PrefabDiagnosticRecord>();
            public List<string> PlacedBuildingLog = new List<string>();
            public List<string> SpawnPointPositions = new List<string>();
        }

        [MenuItem("Scorched/Build Post-Apocalyptic Town Map")]
        public static void BuildMapMenu()
        {
            BuildMap();
        }

        public static string BuildMap()
        {
            var report = new BuildSummaryReport
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                ScenePath = TargetScenePath
            };

            try
            {
                // =================================================================================
                // PHASE 1: SCENE, RENDER PIPELINE & ASSET BOUNDS DIAGNOSTIC
                // =================================================================================
                Debug.Log("=== [SCORCHED PHASE 1] Scene, Render Pipeline & Asset Bounds Diagnostic ===");

                Scene activeScene = SceneManager.GetActiveScene();
                if (activeScene.path != TargetScenePath)
                {
                    if (File.Exists(TargetScenePath))
                    {
                        activeScene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(TargetScenePath) ?? "Assets/Scenes/SandBox");
                        activeScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                        EditorSceneManager.SaveScene(activeScene, TargetScenePath);
                    }
                }

                // 1.1 Find or create the (30, 30, 30) ground reference plane
                GameObject groundPlane = FindOrCreateReferenceGroundPlane();
                Renderer planeRenderer = groundPlane.GetComponent<Renderer>();
                Vector3 planeCenter = planeRenderer != null ? planeRenderer.bounds.center : groundPlane.transform.position;
                Vector3 planeSize = planeRenderer != null ? planeRenderer.bounds.size : new Vector3(300f, 0f, 300f);

                // Ensure valid 300x300 footprint if plane was already disabled in a prior run
                if (planeSize.x < 10f || planeSize.z < 10f)
                {
                    bool wasActive = groundPlane.activeSelf;
                    groundPlane.SetActive(true);
                    planeRenderer = groundPlane.GetComponent<Renderer>();
                    if (planeRenderer != null)
                    {
                        planeCenter = planeRenderer.bounds.center;
                        planeSize = planeRenderer.bounds.size;
                    }
                    groundPlane.SetActive(wasActive);
                }

                report.GroundPlaneName = groundPlane.name;
                report.GroundPlaneCenter = planeCenter;
                report.GroundPlaneSize = planeSize;
                Debug.Log($"[Phase 1.1] Ground Plane '{groundPlane.name}' located | Scale: {groundPlane.transform.localScale} | World Bounds Size: {planeSize} | Center: {planeCenter}");

                // 1.2 Render Pipeline & Material/Shader Check
                RenderPipelineAsset rpAsset = GraphicsSettings.currentRenderPipeline;
                if (rpAsset == null)
                    rpAsset = QualitySettings.renderPipeline;

                report.RenderPipelineAsset = rpAsset != null ? $"{rpAsset.name} ({rpAsset.GetType().Name})" : "Built-In Render Pipeline";
                Debug.Log($"[Phase 1.2] Active Render Pipeline: {report.RenderPipelineAsset}");

                UpgradeBrokenOrStandardMaterialsToURP(report);

                // 1.3 Measure all prefabs inside Assets/ScansFactory/AbandonedFactory_BuiltIn/Prefabs/Factory
                Dictionary<string, PrefabDiagnosticRecord> factoryDiagnostics = MeasureAllFactoryPrefabs(report);

                // =================================================================================
                // PHASE 2: PROCEDURAL POST-APOCALYPTIC TERRAIN GENERATION
                // =================================================================================
                Debug.Log("=== [SCORCHED PHASE 2] Procedural Post-Apocalyptic Terrain Generation ===");

                // Clean up any previous SCORCHED_Town_Map root for idempotent rebuilds
                GameObject existingRoot = GameObject.Find("SCORCHED_Town_Map");
                if (existingRoot != null)
                {
                    Undo.DestroyObjectImmediate(existingRoot);
                }
                GameObject strayTerrain = GameObject.Find("Scorched_Wasteland_Terrain");
                if (strayTerrain != null)
                {
                    Undo.DestroyObjectImmediate(strayTerrain);
                }

                // Create Root & Sub-containers required by Phase 3
                GameObject rootGo = new GameObject("SCORCHED_Town_Map");
                Transform terrainContainer = CreateSubContainer(rootGo.transform, "_Terrain");
                Transform districtCore = CreateSubContainer(rootGo.transform, "_District_Core");
                Transform districtPerimeter = CreateSubContainer(rootGo.transform, "_District_Perimeter");
                Transform shadeAndCover = CreateSubContainer(rootGo.transform, "_ShadeCanopies_And_Cover");
                Transform lootNodes = CreateSubContainer(rootGo.transform, "_LootNodes");
                Transform spawnPoints = CreateSubContainer(rootGo.transform, "_SpawnPoints");

                Terrain terrain = GenerateScorchedTerrain(planeCenter, planeSize, terrainContainer, report);

                // 2.4 Disable the original flat (30, 30, 30) plane once Terrain is aligned and active
                groundPlane.SetActive(false);
                Debug.Log($"[Phase 2.4] Original ground plane '{groundPlane.name}' disabled; 'Scorched_Wasteland_Terrain' is active.");

                // =================================================================================
                // PHASE 3 & 4: TOWN LAYOUT, DISTRICT ARCHITECTURE & COLLISION-FREE PLACEMENT
                // =================================================================================
                Debug.Log("=== [SCORCHED PHASE 3 & 4] Town Layout, Zero-Overlap Placement & Terrain Footprint Flattening ===");

                PopulateTownDistrictsAndCover(
                    terrain,
                    planeCenter,
                    districtCore,
                    districtPerimeter,
                    shadeAndCover,
                    lootNodes,
                    spawnPoints,
                    factoryDiagnostics,
                    report);

                // =================================================================================
                // PHASE 5: SCORCHED ATMOSPHERE & HARSH SUN LIGHTING
                // =================================================================================
                Debug.Log("=== [SCORCHED PHASE 5] Scorched Atmosphere & Harsh Sun Lighting ===");
                ConfigureScorchedLightingAndAtmosphere(planeCenter);

                // Save Scene and Assets
                EditorSceneManager.MarkSceneDirty(activeScene);
                EditorSceneManager.SaveScene(activeScene, TargetScenePath);
                AssetDatabase.SaveAssets();

                // Write JSON Report
                Directory.CreateDirectory(Path.GetDirectoryName(ReportFilePath) ?? "Logs");
                string json = JsonUtility.ToJson(report, true);
                File.WriteAllText(ReportFilePath, json, Encoding.UTF8);

                Debug.Log($"=== [SCORCHED COMPLETE] Saved '{TargetScenePath}' & Report '{ReportFilePath}' ===");
                Debug.Log($"Summary: {report.TotalBuildingsPlaced} Buildings ({report.LargeBuildingsPlaced} Large, {report.MediumBuildingsPlaced} Medium, {report.SmallStructuresPlaced} Small, +{report.IndoorInteriorsPaired} Indoor shells), {report.ShadeCanopiesPlaced} Shade Canopies, {report.TacticalCoverPiecesPlaced} Cover Pieces, {report.LootNodesGenerated} Loot Nodes, {report.SpawnPointsGenerated} Spawn Points.");
                return json;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ScorchedTownGenerator] Error during map generation: {ex}");
                throw;
            }
        }

        private static Transform CreateSubContainer(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        private static GameObject FindOrCreateReferenceGroundPlane()
        {
            // Look for existing plane with scale ~(30, 30, 30) or named Plane / Ground
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == "SCORCHED_Town_Map" || root.name == "Scorched_Wasteland_Terrain")
                    continue;

                Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform t in allTransforms)
                {
                    Vector3 s = t.lossyScale;
                    if (Mathf.Abs(s.x - 30f) < 1f && Mathf.Abs(s.z - 30f) < 1f)
                    {
                        return t.gameObject;
                    }
                    if (t.name.Equals("Plane", StringComparison.OrdinalIgnoreCase) ||
                        t.name.Equals("Ground", StringComparison.OrdinalIgnoreCase))
                    {
                        if (t.localScale != new Vector3(30f, 30f, 30f))
                        {
                            t.localScale = new Vector3(30f, 30f, 30f);
                        }
                        return t.gameObject;
                    }
                }
            }

            // Create the (30, 30, 30) reference plane at origin if the scene was not saved after adding it
            GameObject createdPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            createdPlane.name = "Plane";
            createdPlane.transform.position = Vector3.zero;
            createdPlane.transform.rotation = Quaternion.identity;
            createdPlane.transform.localScale = new Vector3(30f, 30f, 30f);
            return createdPlane;
        }

        private static void UpgradeBrokenOrStandardMaterialsToURP(BuildSummaryReport report)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (urpLit == null)
            {
                Debug.LogWarning("[Phase 1.2] 'Universal Render Pipeline/Lit' shader not found.");
                return;
            }

            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/ScansFactory" });
            int checkedCount = 0;
            int upgradedCount = 0;

            foreach (string guid in matGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                checkedCount++;

                string shaderName = mat.shader != null ? mat.shader.name : "null";
                bool needsUpgrade = mat.shader == null ||
                                    shaderName == "Standard" ||
                                    shaderName == "Standard (Specular setup)" ||
                                    shaderName.StartsWith("Hidden/InternalErrorShader") ||
                                    shaderName.StartsWith("Legacy Shaders/") ||
                                    (shaderName.Contains("Custom/Terrain") && !shaderName.Contains("Universal"));

                if (needsUpgrade)
                {
                    Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                    if (mainTex == null && mat.HasProperty("_BaseMap")) mainTex = mat.GetTexture("_BaseMap");
                    Texture bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
                    Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") :
                                  (mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white);

                    if (path.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0 && urpParticlesUnlit != null)
                    {
                        mat.shader = urpParticlesUnlit;
                    }
                    else
                    {
                        mat.shader = urpLit;
                    }

                    if (mainTex != null && mat.HasProperty("_BaseMap"))
                        mat.SetTexture("_BaseMap", mainTex);
                    if (bumpMap != null && mat.HasProperty("_BumpMap"))
                        mat.SetTexture("_BumpMap", bumpMap);
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", color);

                    EditorUtility.SetDirty(mat);
                    upgradedCount++;
                }
            }

            report.MaterialsChecked = checkedCount;
            report.MaterialsUpgradedToURP = upgradedCount;
            Debug.Log($"[Phase 1.2] Checked {checkedCount} materials in Assets/ScansFactory; upgraded {upgradedCount} non-URP materials to URP.");
        }

        private static Dictionary<string, PrefabDiagnosticRecord> MeasureAllFactoryPrefabs(BuildSummaryReport report)
        {
            var dict = new Dictionary<string, PrefabDiagnosticRecord>(StringComparer.OrdinalIgnoreCase);
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[]
            {
                FactoryPrefabsFolder,
                PropsPrefabsFolder,
                ChunksPrefabsFolder,
                BackgroundsPrefabsFolder,
                FoliagePrefabsFolder
            });

            foreach (string guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefabAsset == null) continue;

                PrefabDiagnosticRecord rec = MeasurePrefabBounds(prefabAsset, path);
                dict[rec.PrefabName] = rec;

                if (path.StartsWith(FactoryPrefabsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    report.FactoryPrefabDiagnostics.Add(rec);
                    Debug.Log($"[Phase 1.3 Bounds] {rec.PrefabName} | Category: {rec.Category} | Size: ({rec.WorldSize.x:F2}, {rec.WorldSize.y:F2}, {rec.WorldSize.z:F2}) | BottomYOffset: {rec.BottomYOffset:F3}");
                }
            }

            report.TotalFactoryPrefabsMeasured = report.FactoryPrefabDiagnostics.Count;
            return dict;
        }

        private static PrefabDiagnosticRecord MeasurePrefabBounds(GameObject prefabAsset, string assetPath)
        {
            GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            temp.transform.position = Vector3.zero;
            temp.transform.rotation = Quaternion.identity;
            temp.transform.localScale = Vector3.one;

            Renderer[] renderers = temp.GetComponentsInChildren<Renderer>(true);
            Bounds combined = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            foreach (Renderer r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!hasBounds)
                {
                    combined = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(r.bounds);
                }
            }

            if (!hasBounds)
            {
                combined = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
            }

            float bottomYOffset = temp.transform.position.y - combined.min.y;
            Vector3 centerOffset = combined.center - temp.transform.position;
            Vector3 size = combined.size;

            UnityEngine.Object.DestroyImmediate(temp);

            string category;
            float maxHoriz = Mathf.Max(size.x, size.z);
            if (size.y >= 8.0f || maxHoriz >= 21.0f)
            {
                category = "Large/Tall Structure";
            }
            else if (size.y >= 3.5f && maxHoriz >= 10.0f)
            {
                category = "Medium Building";
            }
            else
            {
                category = "Small Structure/Ruin/Prop";
            }

            return new PrefabDiagnosticRecord
            {
                AssetPath = assetPath,
                PrefabName = prefabAsset.name,
                WorldSize = size,
                BottomYOffset = bottomYOffset,
                CenterOffset = centerOffset,
                Category = category
            };
        }

        private static Terrain GenerateScorchedTerrain(
            Vector3 planeCenter,
            Vector3 planeSize,
            Transform parentContainer,
            BuildSummaryReport report)
        {
            float width = Mathf.Max(300f, planeSize.x);
            float length = Mathf.Max(300f, planeSize.z);
            float maxHeight = 36f;
            int hmRes = 513;

            TerrainData terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataAssetPath);
            if (terrainData == null)
            {
                terrainData = new TerrainData();
                AssetDatabase.CreateAsset(terrainData, TerrainDataAssetPath);
            }

            terrainData.heightmapResolution = hmRes;
            terrainData.alphamapResolution = 512;
            terrainData.baseMapResolution = 512;
            terrainData.SetDetailResolution(512, 16);
            terrainData.size = new Vector3(width, maxHeight, length);

            // Disable wind/grass tinting so the land looks completely sun-scorched
            terrainData.wavingGrassStrength = 0f;
            terrainData.wavingGrassSpeed = 0f;
            terrainData.wavingGrassAmount = 0f;
            terrainData.wavingGrassTint = new Color(0.76f, 0.64f, 0.46f, 0f);

            // Sculpt procedural heightmap
            float[,] heights = new float[hmRes, hmRes];
            float baseElevationMeters = 2.6f; // Base town height in meters (allows craters/dried riverbeds to dip down)

            // Crater definitions (normalized 0..1 coordinates and radius/depth)
            Vector4[] craters = new Vector4[]
            {
                new Vector4(0.50f, 0.50f, 0.042f, 1.35f), // Central Town Square sun-scorched basin crater
                new Vector4(0.32f, 0.68f, 0.035f, 1.80f), // NW open dune crater
                new Vector4(0.71f, 0.30f, 0.038f, 1.90f), // SE wasteland impact crater
                new Vector4(0.26f, 0.31f, 0.032f, 1.65f), // SW blast crater
                new Vector4(0.74f, 0.72f, 0.036f, 1.75f), // NE blast crater
                new Vector4(0.50f, 0.24f, 0.028f, 1.50f), // South approach crater
                new Vector4(0.50f, 0.77f, 0.028f, 1.50f)  // North approach crater
            };

            for (int y = 0; y < hmRes; y++)
            {
                float ny = (float)y / (hmRes - 1); // Z axis in world space (0..1)
                for (int x = 0; x < hmRes; x++)
                {
                    float nx = (float)x / (hmRes - 1); // X axis in world space (0..1)

                    float dx = nx - 0.5f;
                    float dy = ny - 0.5f;
                    float distFromCenter = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 0 at center, 1 at edge midpoint
                    float edgeDist = Mathf.Min(Mathf.Min(nx, 1f - nx), Mathf.Min(ny, 1f - ny)); // 0 at border, 0.5 at center

                    // Multi-octave Perlin noise for dunes & wasteland erosion
                    float n1 = Mathf.PerlinNoise(nx * 4.5f + 13.7f, ny * 4.5f + 29.1f);
                    float n2 = Mathf.PerlinNoise(nx * 11.0f + 53.2f, ny * 11.0f + 17.4f) * 0.5f;
                    float n3 = Mathf.PerlinNoise(nx * 24.0f + 91.5f, ny * 24.0f + 63.8f) * 0.25f;
                    float ridgeNoise = 1f - Mathf.Abs(Mathf.PerlinNoise(nx * 6.5f + 7.1f, ny * 6.5f + 41.3f) * 2f - 1f);

                    // Town Foundation Factor: 0 inside core streets, 1 in open wasteland
                    float townCoreBlend = Mathf.SmoothStep(0.18f, 0.68f, distFromCenter);

                    // Street network flattening along N-S (nx ~ 0.5) and E-W (ny ~ 0.5) avenues
                    float avenueDist = Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dy));
                    float avenueMask = Mathf.SmoothStep(0.025f, 0.09f, avenueDist);

                    // Tactical terrain cover in open zones (1.5m to 3.5m height variation)
                    float duneVariation = ((n1 + n2 + n3) / 1.75f - 0.45f) * 3.4f * townCoreBlend * (0.35f + 0.65f * avenueMask);

                    //Gentle elevation tiers for High-Ground Overlook quadrants (NE, NW, SE, SW)
                    float quadrantBoost = 0f;
                    if (Mathf.Abs(dx) > 0.14f && Mathf.Abs(dy) > 0.14f && distFromCenter < 0.72f)
                    {
                        float qMask = Mathf.SmoothStep(0.14f, 0.26f, Mathf.Abs(dx)) *
                                      Mathf.SmoothStep(0.14f, 0.26f, Mathf.Abs(dy)) *
                                      (1f - Mathf.SmoothStep(0.55f, 0.75f, distFromCenter));
                        quadrantBoost = qMask * 1.65f;
                    }

                    // Dried-up riverbed trench meandering across outer SE and NW open zones
                    float riverCurve = ny - (0.28f + 0.12f * Mathf.Sin(nx * Mathf.PI * 2.5f));
                    float riverDepth = 0f;
                    if (distFromCenter > 0.36f && distFromCenter < 0.84f)
                    {
                        float rFactor = Mathf.Exp(-(riverCurve * riverCurve) / 0.00085f);
                        riverDepth = rFactor * 1.85f * townCoreBlend;
                    }

                    // Shallow craters for sun-exposed gunfight cover
                    float craterDepression = 0f;
                    for (int c = 0; c < craters.Length; c++)
                    {
                        Vector4 cr = craters[c];
                        float cdx = nx - cr.x;
                        float cdy = ny - cr.y;
                        float cDist = Mathf.Sqrt(cdx * cdx + cdy * cdy);
                        if (cDist < cr.z * 1.35f)
                        {
                            float t = cDist / cr.z;
                            if (t < 1.0f)
                            {
                                // Bowl depression
                                craterDepression += (1f - t * t) * cr.w;
                            }
                            else
                            {
                                // Raised ejecta rim
                                float rimT = (t - 1.0f) / 0.35f;
                                craterDepression -= Mathf.Sin(rimT * Mathf.PI) * (cr.w * 0.28f);
                            }
                        }
                    }

                    // Outer 15% Perimeter Wasteland: rugged eroded desert ridges/dunes boundary
                    float perimeterHeight = 0f;
                    if (edgeDist < 0.16f)
                    {
                        float borderT = 1f - (edgeDist / 0.16f); // 0 at inner boundary of 15% zone, 1 at map edge
                        float smoothBorder = borderT * borderT * (3f - 2f * borderT);
                        float ruggedness = 0.55f * ridgeNoise + 0.30f * n1 + 0.15f * n2;
                        perimeterHeight = smoothBorder * (9.5f + ruggedness * 13.5f);
                    }

                    float hMeters = baseElevationMeters + duneVariation + quadrantBoost - riverDepth - craterDepression + perimeterHeight;
                    hMeters = Mathf.Clamp(hMeters, 0.35f, maxHeight - 1f);
                    heights[y, x] = hMeters / maxHeight;
                }
            }

            terrainData.SetHeights(0, 0, heights);

            // Configure Arid Surface TerrainLayers & Splatmaps
            ConfigureTerrainLayersAndSplatmap(terrainData, heights, maxHeight);

            // Instantiate Terrain GameObject matching exact world footprint and center of the reference plane
            GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "Scorched_Wasteland_Terrain";
            terrainGo.transform.SetParent(parentContainer, false);

            float terrainBottomY = planeCenter.y - baseElevationMeters;
            Vector3 terrainPos = new Vector3(planeCenter.x - width * 0.5f, terrainBottomY, planeCenter.z - length * 0.5f);
            terrainGo.transform.position = terrainPos;
            terrainGo.isStatic = true;

            Terrain terrainComp = terrainGo.GetComponent<Terrain>();
            terrainComp.drawTreesAndFoliage = false;
            terrainComp.shadowCastingMode = ShadowCastingMode.On;

            // Ensure URP Terrain Lit material is assigned if available
            Shader urpTerrainLit = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            if (urpTerrainLit != null)
            {
                string terrainMatPath = "Assets/Scenes/SandBox/ScorchedTerrainMaterial.mat";
                Material terrainMat = AssetDatabase.LoadAssetAtPath<Material>(terrainMatPath);
                if (terrainMat == null)
                {
                    terrainMat = new Material(urpTerrainLit);
                    AssetDatabase.CreateAsset(terrainMat, terrainMatPath);
                }
                else
                {
                    terrainMat.shader = urpTerrainLit;
                }
                terrainComp.materialTemplate = terrainMat;
            }

            report.TerrainSize = terrainData.size;
            report.TerrainPosition = terrainPos;
            report.TerrainHeightmapResolution = hmRes;

            EditorUtility.SetDirty(terrainData);
            Debug.Log($"[Phase 2] Created 'Scorched_Wasteland_Terrain' ({hmRes}x{hmRes}) at {terrainPos} covering {width}m x {length}m.");
            return terrainComp;
        }

        private static void ConfigureTerrainLayersAndSplatmap(TerrainData terrainData, float[,] heights, float maxHeight)
        {
            if (!AssetDatabase.IsValidFolder(TerrainLayersFolder))
            {
                Directory.CreateDirectory(TerrainLayersFolder);
                AssetDatabase.Refresh();
            }

            // Load project textures from ScansFactory or create procedural arid textures as fallback
            Texture2D sandDiffuse = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_landscape_01_01.TGA");
            Texture2D sandNormal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_landscape_01_01_n.TGA");

            Texture2D crackedMudDiffuse = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_Landscape_01_02_d.TGA");
            Texture2D crackedMudNormal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_Landscape_01_02_n.TGA");

            Texture2D concreteDiffuse = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_GroundPavestone_01_01_bc.TGA");
            Texture2D concreteNormal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommonTexturesFolder}/t_en_GroundPavestone_01_01_n.TGA");

            if (sandDiffuse == null)
                sandDiffuse = GetOrCreateProceduralAridTexture($"{TerrainLayersFolder}/AridSand_Generated.png", new Color(0.76f, 0.63f, 0.45f), new Color(0.68f, 0.55f, 0.38f));
            if (crackedMudDiffuse == null)
                crackedMudDiffuse = GetOrCreateProceduralAridTexture($"{TerrainLayersFolder}/ScorchedMud_Generated.png", new Color(0.58f, 0.47f, 0.35f), new Color(0.45f, 0.36f, 0.27f));
            if (concreteDiffuse == null)
                concreteDiffuse = GetOrCreateProceduralAridTexture($"{TerrainLayersFolder}/DustyConcrete_Generated.png", new Color(0.62f, 0.58f, 0.52f), new Color(0.52f, 0.48f, 0.43f));

            TerrainLayer layerSand = CreateOrUpdateTerrainLayer(
                $"{TerrainLayersFolder}/Layer_ScorchedSand.terrainlayer",
                sandDiffuse,
                sandNormal,
                new Vector2(12f, 12f));

            TerrainLayer layerCrackedEarth = CreateOrUpdateTerrainLayer(
                $"{TerrainLayersFolder}/Layer_CrackedEarth.terrainlayer",
                crackedMudDiffuse,
                crackedMudNormal,
                new Vector2(10f, 10f));

            TerrainLayer layerTownConcrete = CreateOrUpdateTerrainLayer(
                $"{TerrainLayersFolder}/Layer_TownConcrete.terrainlayer",
                concreteDiffuse,
                concreteNormal,
                new Vector2(8f, 8f));

            terrainData.terrainLayers = new[] { layerSand, layerCrackedEarth, layerTownConcrete };

            int alphaRes = terrainData.alphamapResolution;
            int hmRes = terrainData.heightmapResolution;
            float[,,] splatmap = new float[alphaRes, alphaRes, 3];

            for (int y = 0; y < alphaRes; y++)
            {
                float ny = (float)y / (alphaRes - 1);
                int hy = Mathf.Clamp(Mathf.RoundToInt(ny * (hmRes - 1)), 0, hmRes - 1);

                for (int x = 0; x < alphaRes; x++)
                {
                    float nx = (float)x / (alphaRes - 1);
                    int hx = Mathf.Clamp(Mathf.RoundToInt(nx * (hmRes - 1)), 0, hmRes - 1);

                    float hMeters = heights[hy, hx] * maxHeight;
                    float dx = nx - 0.5f;
                    float dy = ny - 0.5f;
                    float distFromCenter = Mathf.Sqrt(dx * dx + dy * dy) * 2f;

                    // Weight 2: Town Concrete along central square and N-S / E-W main streets
                    float onMainStreet = 1f - Mathf.SmoothStep(0.025f, 0.065f, Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dy)));
                    float inTownSquare = 1f - Mathf.SmoothStep(0.09f, 0.16f, distFromCenter);
                    float townRoadMask = Mathf.Clamp01(Mathf.Max(inTownSquare, onMainStreet * (1f - Mathf.SmoothStep(0.45f, 0.72f, distFromCenter))));

                    float patchNoise = Mathf.PerlinNoise(nx * 18f + 5.3f, ny * 18f + 11.9f);
                    float wConcrete = townRoadMask * Mathf.Lerp(0.45f, 0.88f, patchNoise);

                    // Weight 1: Cracked Earth / Rubble on slopes, craters, and building district zones
                    float wCracked = Mathf.Clamp01((1f - distFromCenter) * 0.45f + (hMeters > 5.5f ? 0.55f : 0.15f) * patchNoise);

                    // Weight 0: Sun-scorched Arid Desert Sand
                    float wSand = Mathf.Max(0.1f, 1f - (wConcrete * 0.8f));

                    float sum = wSand + wCracked + wConcrete;
                    splatmap[y, x, 0] = wSand / sum;
                    splatmap[y, x, 1] = wCracked / sum;
                    splatmap[y, x, 2] = wConcrete / sum;
                }
            }

            terrainData.SetAlphamaps(0, 0, splatmap);
        }

        private static TerrainLayer CreateOrUpdateTerrainLayer(string assetPath, Texture2D diffuse, Texture2D normal, Vector2 tileSize)
        {
            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(assetPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, assetPath);
            }

            layer.diffuseTexture = diffuse;
            layer.normalMapTexture = normal;
            layer.tileSize = tileSize;
            layer.metallic = 0f;
            layer.smoothness = 0.05f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        private static Texture2D GetOrCreateProceduralAridTexture(string assetPath, Color colorA, Color colorB)
        {
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null) return existing;

            int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = Mathf.PerlinNoise(x * 0.12f, y * 0.12f) * 0.7f +
                              Mathf.PerlinNoise(x * 0.35f + 17f, y * 0.35f + 31f) * 0.3f;
                    tex.SetPixel(x, y, Color.Lerp(colorA, colorB, n));
                }
            }
            tex.Apply();
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(assetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private struct BuildingSpec
        {
            public string PrefabName;
            public string IndoorMateName;
            public Vector2 OffsetXZ;
            public float RotationY;
            public bool IsCoreDistrict;
            public string ZoneLabel;

            public BuildingSpec(string prefabName, string indoorMateName, float x, float z, float rotY, bool isCore, string zoneLabel)
            {
                PrefabName = prefabName;
                IndoorMateName = indoorMateName;
                OffsetXZ = new Vector2(x, z);
                RotationY = rotY;
                IsCoreDistrict = isCore;
                ZoneLabel = zoneLabel;
            }
        }

        private static void PopulateTownDistrictsAndCover(
            Terrain terrain,
            Vector3 center,
            Transform districtCore,
            Transform districtPerimeter,
            Transform shadeAndCover,
            Transform lootNodes,
            Transform spawnPoints,
            Dictionary<string, PrefabDiagnosticRecord> diagnostics,
            BuildSummaryReport report)
        {
            // Phase 4.1: Maintain List<Bounds> placedBuildingBounds with +2.5f meter alleyway padding
            List<Bounds> placedBuildingBounds = new List<Bounds>();

            // Reserve the central Sun-Scorched Town Square (34m x 34m open central basin) so no building overlaps the plaza
            Bounds townSquareReserve = new Bounds(new Vector3(center.x, center.y + 5f, center.z), new Vector3(34f, 40f, 34f));
            placedBuildingBounds.Add(townSquareReserve);

            // =========================================================================================
            // 1. PLACE ENTERABLE INNER RING & HIGH-GROUND OVERLOOK FACTORY BUILDINGS
            // Arranged along North-South (X=0) and East-West (Z=0) avenues (14m-16m wide)
            // and narrow 4m-6m shaded alleys between adjacent buildings.
            // =========================================================================================
            BuildingSpec[] plannedBuildings = new BuildingSpec[]
            {
                // --- INNER RING: MAIN STREET & SHADED ALLEYWAYS (Accessible Interiors + Street Shade) ---
                // North-West Block
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01", -24f,  24f,  90f, true, "InnerRing_NW_Corner"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04", -22f,  46f,  90f, true, "InnerRing_NorthAvenue_W"),
                new BuildingSpec("Factory_02_02", "FactoryIndoor_02_02", -45f,  21f,   0f, true, "InnerRing_WestAvenue_N"),

                // North-East Block
                new BuildingSpec("Factory_03_01", null,                   24f,  24f, 180f, true, "InnerRing_NE_Corner"),
                new BuildingSpec("Factory_02_02", "FactoryIndoor_02_02",  22f,  46f, -90f, true, "InnerRing_NorthAvenue_E"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04",  46f,  21f, 180f, true, "InnerRing_EastAvenue_N"),

                // South-East Block
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01",  24f, -24f, -90f, true, "InnerRing_SE_Corner"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04",  22f, -46f, -90f, true, "InnerRing_SouthAvenue_E"),
                new BuildingSpec("Factory_02_04", null,                   46f, -20f,   0f, true, "InnerRing_EastAvenue_S"),

                // South-West Block
                new BuildingSpec("Factory_01_02", null,                  -21f, -24f,   0f, true, "InnerRing_SW_Corner"),
                new BuildingSpec("Factory_02_02", "FactoryIndoor_02_02", -23f, -45f,  90f, true, "InnerRing_SouthAvenue_W"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04", -46f, -21f,   0f, true, "InnerRing_WestAvenue_S"),

                // --- VERTICALITY QUADRANTS: HIGH-GROUND OVERLOOKS (Tallest Skyscrapers/Warehouses) ---
                new BuildingSpec("Factory_04_01", null,                  -52f,  52f,  90f, true, "Overlook_NW_HighGround"),
                new BuildingSpec("Factory_01_03", null,                   52f,  52f, 180f, true, "Overlook_NE_HighGround"),
                new BuildingSpec("Factory_04_01", null,                   52f, -52f, -90f, true, "Overlook_SE_HighGround"),
                new BuildingSpec("Factory_01_03", null,                  -52f, -52f,   0f, true, "Overlook_SW_HighGround"),

                // --- SECONDARY ALLEY CORRIDORS (Creating 4m-6m Shaded Alleyways) ---
                new BuildingSpec("Factory_01_01", null,                  -25f,  72f,   5f, false, "AlleyBlock_North_W"),
                new BuildingSpec("Factory_01_01", null,                   25f,  72f, 175f, false, "AlleyBlock_North_E"),
                new BuildingSpec("Factory_01_01", null,                  -25f, -72f,  -5f, false, "AlleyBlock_South_W"),
                new BuildingSpec("Factory_01_01", null,                   25f, -72f, 185f, false, "AlleyBlock_South_E"),
                new BuildingSpec("Factory_03_01_closed", null,           -74f,  24f,  90f, false, "AlleyBlock_West_N"),
                new BuildingSpec("Factory_02_01_closed", null,           -74f, -24f,  85f, false, "AlleyBlock_West_S"),
                new BuildingSpec("Factory_03_01_closed", null,            74f,  24f, -90f, false, "AlleyBlock_East_N"),
                new BuildingSpec("Factory_02_01_closed", null,            74f, -24f, -85f, false, "AlleyBlock_East_S"),

                // --- PERIMETER SPAWN BASTIONS & OUTER INDUSTRIAL RING ---
                // 8 Perimeter Structures providing interior/structural shade for the 8 Spawn Points
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01",   0f,  96f, 180f, false, "Perimeter_North_SpawnBastion"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04",  68f,  82f, 180f, false, "Perimeter_NE_SpawnBastion"),
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01",  98f,   0f, -90f, false, "Perimeter_East_SpawnBastion"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04",  68f, -82f,   0f, false, "Perimeter_SE_SpawnBastion"),
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01",   0f, -96f,   0f, false, "Perimeter_South_SpawnBastion"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04", -68f, -82f,   0f, false, "Perimeter_SW_SpawnBastion"),
                new BuildingSpec("Factory_02_01", "FactoryIndoor_02_01", -98f,   0f,  90f, false, "Perimeter_West_SpawnBastion"),
                new BuildingSpec("Factory_01_04", "FactoryIndoor_01_04", -68f,  82f, 180f, false, "Perimeter_NW_SpawnBastion"),

                // Additional Outer Industrial Ruins &Flank Cover
                new BuildingSpec("Factory_04_01_closed", null,           -92f,  56f,  90f, false, "Perimeter_Outer_WNW"),
                new BuildingSpec("Factory_04_01_closed", null,            92f,  56f, -90f, false, "Perimeter_Outer_ENE"),
                new BuildingSpec("Factory_04_01_closed", null,            92f, -56f, -90f, false, "Perimeter_Outer_ESE"),
                new BuildingSpec("Factory_04_01_closed", null,           -92f, -56f,  90f, false, "Perimeter_Outer_WSW"),
                new BuildingSpec("Factory_02_03", null,                  -48f,  88f,   0f, false, "Perimeter_Ruin_NNW"),
                new BuildingSpec("Factory_02_03", null,                   48f,  88f, 180f, false, "Perimeter_Ruin_NNE"),
                new BuildingSpec("Factory_02_03", null,                   48f, -88f,   0f, false, "Perimeter_Ruin_SSE"),
                new BuildingSpec("Factory_02_03", null,                  -48f, -88f, 180f, false, "Perimeter_Ruin_SSW"),
                new BuildingSpec("Factory_02_04", null,                  -88f,  88f,  45f, false, "Perimeter_Corner_NW"),
                new BuildingSpec("Factory_02_04", null,                   88f,  88f, -45f, false, "Perimeter_Corner_NE"),
                new BuildingSpec("Factory_02_04", null,                   88f, -88f, 135f, false, "Perimeter_Corner_SE"),
                new BuildingSpec("Factory_02_04", null,                  -88f, -88f,-135f, false, "Perimeter_Corner_SW")
            };

            List<Transform> placedPerimeterBastions = new List<Transform>();

            foreach (BuildingSpec spec in plannedBuildings)
            {
                if (!diagnostics.TryGetValue(spec.PrefabName, out PrefabDiagnosticRecord diag))
                {
                    Debug.LogWarning($"[Phase 4] Prefab diagnostic not found for '{spec.PrefabName}'");
                    continue;
                }

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(diag.AssetPath);
                if (prefabAsset == null) continue;

                Quaternion rot = Quaternion.Euler(0f, spec.RotationY, 0f);
                Vector3 candidatePos = new Vector3(center.x + spec.OffsetXZ.x, center.y, center.z + spec.OffsetXZ.y);

                // Search candidate position (or slight outward nudge if needed to strictly satisfy +2.5m zero-overlap guarantee)
                Vector3 finalPos = candidatePos;
                Bounds paddedCandidateBounds = CalculateRotatedWorldBounds(candidatePos, rot, diag, 2.5f);
                bool foundNonOverlapping = !IntersectsAny(placedBuildingBounds, paddedCandidateBounds);

                if (!foundNonOverlapping)
                {
                    Vector2 radialDir = spec.OffsetXZ.sqrMagnitude > 0.01f ? spec.OffsetXZ.normalized : Vector2.up;
                    for (int step = 1; step <= 12; step++)
                    {
                        Vector3 nudged = new Vector3(
                            candidatePos.x + radialDir.x * (step * 2.5f),
                            candidatePos.y,
                            candidatePos.z + radialDir.y * (step * 2.5f));
                        Bounds testBounds = CalculateRotatedWorldBounds(nudged, rot, diag, 2.5f);
                        if (!IntersectsAny(placedBuildingBounds, testBounds))
                        {
                            finalPos = nudged;
                            paddedCandidateBounds = testBounds;
                            foundNonOverlapping = true;
                            break;
                        }
                    }
                }

                if (!foundNonOverlapping)
                {
                    Debug.LogWarning($"[Phase 4.1 Zero-Overlap] Skipped '{spec.PrefabName}' at {candidatePos} due to overlap constraint.");
                    continue;
                }

                // Phase 4.2: Automatic Under-Building Terrain Flattening (+2.0m entrance apron)
                Bounds unpaddedFootprint = CalculateRotatedWorldBounds(finalPos, rot, diag, 0f);
                FlattenTerrainUnderBuilding(terrain, unpaddedFootprint, 2.0f);

                // Phase 4.3: Exact Ground Snapping using terrain.SampleHeight(position) + terrain.transform.position.y + BottomYOffset
                float groundY = terrain.SampleHeight(finalPos) + terrain.transform.position.y;
                finalPos.y = groundY + diag.BottomYOffset;

                Transform parentContainer = spec.IsCoreDistrict ? districtCore : districtPerimeter;
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, parentContainer);
                instance.name = $"{spec.ZoneLabel}_{spec.PrefabName}";
                instance.transform.position = finalPos;
                instance.transform.rotation = rot;

                // Pair matching indoor interior shell if available
                if (!string.IsNullOrEmpty(spec.IndoorMateName) && diagnostics.TryGetValue(spec.IndoorMateName, out PrefabDiagnosticRecord indoorDiag))
                {
                    GameObject indoorAsset = AssetDatabase.LoadAssetAtPath<GameObject>(indoorDiag.AssetPath);
                    if (indoorAsset != null)
                    {
                        GameObject indoorInstance = (GameObject)PrefabUtility.InstantiatePrefab(indoorAsset, instance.transform);
                        indoorInstance.name = spec.IndoorMateName;
                        indoorInstance.transform.localPosition = Vector3.zero;
                        indoorInstance.transform.localRotation = Quaternion.identity;
                        indoorInstance.transform.localScale = Vector3.one;
                        report.IndoorInteriorsPaired++;
                    }
                }

                // Phase 4.4: Ensure Colliders & Static Flags on building and all child meshes
                EnsureCollidersAndStaticFlags(instance, true);

                // Record final padded bounds (+2.5m alleyway padding)
                Bounds finalPaddedBounds = CalculateRotatedWorldBounds(finalPos, rot, diag, 2.5f);
                placedBuildingBounds.Add(finalPaddedBounds);

                if (spec.ZoneLabel.Contains("SpawnBastion"))
                {
                    placedPerimeterBastions.Add(instance.transform);
                }

                // Update Category Counters
                report.TotalBuildingsPlaced++;
                if (diag.Category == "Large/Tall Structure") report.LargeBuildingsPlaced++;
                else if (diag.Category == "Medium Building") report.MediumBuildingsPlaced++;
                else report.SmallStructuresPlaced++;

                report.PlacedBuildingLog.Add($"{instance.name} at ({finalPos.x:F1}, {finalPos.y:F2}, {finalPos.z:F1}) [Size: {diag.WorldSize.x:F1}x{diag.WorldSize.y:F1}x{diag.WorldSize.z:F1}]");
            }

            // =========================================================================================
            // 2. SUN-SCORCHED TOWN SQUARE (CENTER) & SHADE CANOPIES + STREET COVER
            // =========================================================================================
            PlaceShadeCanopiesAndTacticalCover(terrain, center, shadeAndCover, diagnostics, placedBuildingBounds, report);

            // =========================================================================================
            // 3. LOOT NODES (High-Tier Sun-Exposed Town Square + Shaded Alley & Interior Nodes)
            // =========================================================================================
            GenerateLootNodes(terrain, center, lootNodes, diagnostics, report);

            // =========================================================================================
            // 4. 8 BALANCED PLAYER SPAWN POINTS (Evenly Spaced Perimeter, Shaded, Facing Inward)
            // =========================================================================================
            GenerateBalancedSpawnPoints(terrain, center, spawnPoints, placedPerimeterBastions, report);
        }

        private static Bounds CalculateRotatedWorldBounds(Vector3 pos, Quaternion rot, PrefabDiagnosticRecord diag, float horizontalPadding)
        {
            Vector3 rotatedCenterOffset = rot * diag.CenterOffset;
            Vector3 worldCenter = pos + rotatedCenterOffset;

            Vector3 right = rot * Vector3.right;
            Vector3 up = rot * Vector3.up;
            Vector3 forward = rot * Vector3.forward;

            Vector3 halfExtents = diag.WorldSize * 0.5f;
            float rx = Mathf.Abs(right.x) * halfExtents.x + Mathf.Abs(up.x) * halfExtents.y + Mathf.Abs(forward.x) * halfExtents.z;
            float ry = Mathf.Abs(right.y) * halfExtents.x + Mathf.Abs(up.y) * halfExtents.y + Mathf.Abs(forward.y) * halfExtents.z;
            float rz = Mathf.Abs(right.z) * halfExtents.x + Mathf.Abs(up.z) * halfExtents.y + Mathf.Abs(forward.z) * halfExtents.z;

            Vector3 paddedSize = new Vector3(
                (rx + horizontalPadding) * 2f,
                ry * 2f + 10f,
                (rz + horizontalPadding) * 2f);

            return new Bounds(worldCenter, paddedSize);
        }

        private static bool IntersectsAny(List<Bounds> existingBounds, Bounds candidate)
        {
            for (int i = 0; i < existingBounds.Count; i++)
            {
                if (existingBounds[i].Intersects(candidate))
                    return true;
            }
            return false;
        }

        private static void FlattenTerrainUnderBuilding(Terrain terrain, Bounds buildingBounds, float apronMeters)
        {
            TerrainData td = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = td.size;
            int hmRes = td.heightmapResolution;

            float minWorldX = buildingBounds.min.x - apronMeters;
            float maxWorldX = buildingBounds.max.x + apronMeters;
            float minWorldZ = buildingBounds.min.z - apronMeters;
            float maxWorldZ = buildingBounds.max.z + apronMeters;

            float blendRimMeters = 3.0f;

            int minX = Mathf.Clamp(Mathf.FloorToInt(((minWorldX - blendRimMeters - terrainPos.x) / terrainSize.x) * (hmRes - 1)), 0, hmRes - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(((maxWorldX + blendRimMeters - terrainPos.x) / terrainSize.x) * (hmRes - 1)), 0, hmRes - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(((minWorldZ - blendRimMeters - terrainPos.z) / terrainSize.z) * (hmRes - 1)), 0, hmRes - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt(((maxWorldZ + blendRimMeters - terrainPos.z) / terrainSize.z) * (hmRes - 1)), 0, hmRes - 1);

            int patchWidth = maxX - minX + 1;
            int patchHeight = maxZ - minZ + 1;
            if (patchWidth <= 0 || patchHeight <= 0) return;

            float[,] patch = td.GetHeights(minX, minZ, patchWidth, patchHeight);

            // Compute uniform average height across the core building + apron footprint
            float sumH = 0f;
            int countH = 0;
            for (int z = 0; z < patchHeight; z++)
            {
                float worldZ = terrainPos.z + ((float)(minZ + z) / (hmRes - 1)) * terrainSize.z;
                if (worldZ < minWorldZ || worldZ > maxWorldZ) continue;

                for (int x = 0; x < patchWidth; x++)
                {
                    float worldX = terrainPos.x + ((float)(minX + x) / (hmRes - 1)) * terrainSize.x;
                    if (worldX < minWorldX || worldX > maxWorldX) continue;

                    sumH += patch[z, x];
                    countH++;
                }
            }

            float targetNormalizedHeight = countH > 0
                ? (sumH / countH)
                : ((terrain.SampleHeight(buildingBounds.center) / terrainSize.y));

            // Flatten core + apron to uniform target height and smoothly blend outer rim
            for (int z = 0; z < patchHeight; z++)
            {
                float worldZ = terrainPos.z + ((float)(minZ + z) / (hmRes - 1)) * terrainSize.z;
                float dz = 0f;
                if (worldZ < minWorldZ) dz = minWorldZ - worldZ;
                else if (worldZ > maxWorldZ) dz = worldZ - maxWorldZ;

                for (int x = 0; x < patchWidth; x++)
                {
                    float worldX = terrainPos.x + ((float)(minX + x) / (hmRes - 1)) * terrainSize.x;
                    float dx = 0f;
                    if (worldX < minWorldX) dx = minWorldX - worldX;
                    else if (worldX > maxWorldX) dx = worldX - maxWorldX;

                    float distOutsideApron = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distOutsideApron <= 0.001f)
                    {
                        patch[z, x] = targetNormalizedHeight;
                    }
                    else if (distOutsideApron < blendRimMeters)
                    {
                        float t = distOutsideApron / blendRimMeters;
                        float smoothT = t * t * (3f - 2f * t);
                        patch[z, x] = Mathf.Lerp(targetNormalizedHeight, patch[z, x], smoothT);
                    }
                }
            }

            td.SetHeights(minX, minZ, patch);
        }

        private static void PlaceShadeCanopiesAndTacticalCover(
            Terrain terrain,
            Vector3 center,
            Transform shadeAndCover,
            Dictionary<string, PrefabDiagnosticRecord> diagnostics,
            List<Bounds> placedBuildingBounds,
            BuildSummaryReport report)
        {
            // 1. Overhead Industrial Pipe Bridges & Canopies casting transitional street shade across Main Streets
            (string prefab, Vector2 pos, float rotY)[] shadeCanopies = new[]
            {
                ("P_en_PipeBackground_02_01", new Vector2(  0f,  28f),  90f),
                ("P_en_PipeBackground_02_01", new Vector2(  0f, -28f),  90f),
                ("P_en_PipeBackground_02_01", new Vector2( 28f,   0f),   0f),
                ("P_en_PipeBackground_02_01", new Vector2(-28f,   0f),   0f),
                ("P_en_PipeBackground_02_02", new Vector2(  0f,  58f),  90f),
                ("P_en_PipeBackground_02_02", new Vector2(  0f, -58f),  90f),
                ("P_en_PipeBackground_02_02", new Vector2( 58f,   0f),   0f),
                ("P_en_PipeBackground_02_02", new Vector2(-58f,   0f),   0f),
                ("P_en_MetalSupport_01_01",   new Vector2(-13f,  15f),   0f),
                ("P_en_MetalSupport_01_01",   new Vector2( 13f,  15f),  90f),
                ("P_en_MetalSupport_01_01",   new Vector2( 13f, -15f), 180f),
                ("P_en_MetalSupport_01_01",   new Vector2(-13f, -15f), -90f)
            };

            foreach (var canopy in shadeCanopies)
            {
                GameObject placed = SpawnPropSnapToGround(canopy.prefab, center, canopy.pos, canopy.rotY, terrain, shadeAndCover, diagnostics);
                if (placed != null)
                {
                    placed.name = $"ShadeCanopy_{canopy.prefab}";
                    report.ShadeCanopiesPlaced++;
                }
            }

            // 2. Concrete Ground Slabs in the Central Town Square & Avenue Intersections (for clean concrete footing)
            Vector2[] plazaPlates = new[]
            {
                new Vector2( 0f,  0f),
                new Vector2( 8f,  0f),
                new Vector2(-8f,  0f),
                new Vector2( 0f,  8f),
                new Vector2( 0f, -8f)
            };
            foreach (Vector2 p in plazaPlates)
            {
                GameObject slab = SpawnPropSnapToGround("P_en_ConcretePlates_01", center, p, 0f, terrain, shadeAndCover, diagnostics);
                if (slab != null)
                {
                    slab.name = "TownSquare_ConcreteSlab";
                    report.TacticalCoverPiecesPlaced++;
                }
            }

            // 3. Tactical Barricades, Rubble Containers, Dumpsters, Ramps, Brick Piles & Spools
            //    Ring around Sun-Scorched Town Square + Main Streets + Alleyways
            (string prefab, Vector2 pos, float rotY)[] coverPieces = new[]
            {
                // Central Sun-Scorched Town Square Barricade Ring (100% sun exposed high-risk gunfight cover)
                ("P_en_RubbleContainer_01", new Vector2(  0f,  11f),  10f),
                ("P_en_RubbleContainer_01", new Vector2(  0f, -11f), 190f),
                ("P_en_TrashContainer_01",  new Vector2( 11f,   0f),  85f),
                ("P_en_TrashContainer_01",  new Vector2(-11f,   0f), -85f),
                ("P_en_WoodSpool_01_01",    new Vector2(  7f,   7f),  35f),
                ("P_en_WoodSpool_01_01",    new Vector2( -7f,  -7f), 125f),
                ("P_en_WoodSpool_01_01",    new Vector2(  7f,  -7f), -45f),
                ("P_en_WoodSpool_01_01",    new Vector2( -7f,   7f),  60f),
                ("P_en_Brick_01_01",        new Vector2(  4f,   9f),  15f),
                ("P_en_Brick_01_02",        new Vector2( -4f,  -9f), 140f),
                ("P_en_WoodPallet_01_01",   new Vector2(  9f,   4f),  25f),
                ("P_en_WoodPallet_01_02",   new Vector2( -9f,  -4f), -30f),

                // North, South, East, West Avenue Tactical Cover
                ("P_en_RubbleContainer_01", new Vector2( -4f,  36f),   5f),
                ("P_en_TrashContainer_01",  new Vector2(  4f,  50f), -15f),
                ("P_en_LoadingRamp_01_01",  new Vector2(  0f,  66f),   0f),
                ("P_en_RubbleContainer_01", new Vector2(  4f, -36f), 185f),
                ("P_en_TrashContainer_01",  new Vector2( -4f, -50f), 165f),
                ("P_en_LoadingRamp_01_01",  new Vector2(  0f, -66f), 180f),
                ("P_en_RubbleContainer_01", new Vector2( 36f,   4f),  95f),
                ("P_en_TrashContainer_01",  new Vector2( 50f,  -4f),  75f),
                ("P_en_LoadingRamp_01_01",  new Vector2( 66f,   0f),  90f),
                ("P_en_RubbleContainer_01", new Vector2(-36f,  -4f), -85f),
                ("P_en_TrashContainer_01",  new Vector2(-50f,   4f), -75f),
                ("P_en_LoadingRamp_01_01",  new Vector2(-66f,   0f), -90f),

                // Street Lamps & Industrial Details along Main Streets
                ("P_en_Lamp_01_01",         new Vector2( -8f,  18f),  90f),
                ("P_en_Lamp_01_01",         new Vector2(  8f,  38f), -90f),
                ("P_en_Lamp_01_01",         new Vector2( -8f, -18f),  90f),
                ("P_en_Lamp_01_01",         new Vector2(  8f, -38f), -90f),
                ("P_en_Lamp_01_01",         new Vector2( 18f,   8f), 180f),
                ("P_en_Lamp_01_01",         new Vector2( 38f,  -8f),   0f),
                ("P_en_Lamp_01_01",         new Vector2(-18f,  -8f),   0f),
                ("P_en_Lamp_01_01",         new Vector2(-38f,   8f), 180f),

                // Alleyway & Outer Wasteland Cover + Dry Desert Scrub
                ("P_WallC_05",              new Vector2(-36f,  62f),  45f),
                ("P_WallC_05",              new Vector2( 36f,  62f), -45f),
                ("P_WallC_05",              new Vector2( 36f, -62f), 135f),
                ("P_WallC_05",              new Vector2(-36f, -62f),-135f),
                ("P_DryPlants_01_01",       new Vector2(-16f,  12f),   0f),
                ("P_DryPlants_01_02",       new Vector2( 16f, -12f),  90f),
                ("P_DryPlants_01_01",       new Vector2( 62f,  35f),  45f),
                ("P_DryPlants_01_02",       new Vector2(-62f, -35f), 135f)
            };

            foreach (var item in coverPieces)
            {
                GameObject placed = SpawnPropSnapToGround(item.prefab, center, item.pos, item.rotY, terrain, shadeAndCover, diagnostics);
                if (placed != null)
                {
                    placed.name = $"Cover_{item.prefab}";
                    report.TacticalCoverPiecesPlaced++;
                    report.SmallStructuresPlaced++;
                }
            }
        }

        private static GameObject SpawnPropSnapToGround(
            string prefabName,
            Vector3 mapCenter,
            Vector2 offsetXZ,
            float rotY,
            Terrain terrain,
            Transform parent,
            Dictionary<string, PrefabDiagnosticRecord> diagnostics)
        {
            if (!diagnostics.TryGetValue(prefabName, out PrefabDiagnosticRecord diag))
                return null;

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(diag.AssetPath);
            if (asset == null) return null;

            Vector3 pos = new Vector3(mapCenter.x + offsetXZ.x, mapCenter.y, mapCenter.z + offsetXZ.y);
            float groundY = terrain.SampleHeight(pos) + terrain.transform.position.y;
            pos.y = groundY + diag.BottomYOffset;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            instance.transform.position = pos;
            instance.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
            EnsureCollidersAndStaticFlags(instance, true);
            return instance;
        }

        private static void GenerateLootNodes(
            Terrain terrain,
            Vector3 center,
            Transform lootContainer,
            Dictionary<string, PrefabDiagnosticRecord> diagnostics,
            BuildSummaryReport report)
        {
            (string name, Vector2 pos, string visualProp)[] nodes = new[]
            {
                // 6 High-Tier Sun-Exposed Town Square Loot Nodes (High Risk / High Reward)
                ("LootNode_HighTier_SquareCenter_01", new Vector2(  0.0f,   0.0f), "P_en_GasCylinder_01_01"),
                ("LootNode_HighTier_SquareNorth_02",  new Vector2(  0.0f,   5.5f), "P_en_CartonBox_01_01"),
                ("LootNode_HighTier_SquareSouth_03",  new Vector2(  0.0f,  -5.5f), "P_en_CartonBox_01_01"),
                ("LootNode_HighTier_SquareEast_04",   new Vector2(  5.5f,   0.0f), "P_en_GasCylinder_01_01"),
                ("LootNode_HighTier_SquareWest_05",   new Vector2( -5.5f,   0.0f), "P_en_GasCylinder_01_01"),
                ("LootNode_HighTier_SquareNE_06",     new Vector2(  4.0f,   4.0f), "P_en_WoodPallet_01_04"),

                // 8 Mid-Tier Shaded Alley & Overlook Loot Nodes
                ("LootNode_MidTier_NW_Interior_07",   new Vector2(-24.0f,  24.0f), "P_en_CartonBox_01_01"),
                ("LootNode_MidTier_NE_Alley_08",      new Vector2( 34.0f,  22.0f), "P_en_GasCylinder_01_01"),
                ("LootNode_MidTier_SE_Interior_09",   new Vector2( 24.0f, -24.0f), "P_en_CartonBox_01_01"),
                ("LootNode_MidTier_SW_Alley_10",      new Vector2(-34.0f, -22.0f), "P_en_GasCylinder_01_01"),
                ("LootNode_MidTier_OverlookNW_11",    new Vector2(-42.0f,  42.0f), "P_en_WoodPallet_01_05"),
                ("LootNode_MidTier_OverlookNE_12",    new Vector2( 42.0f,  42.0f), "P_en_WoodPallet_01_05"),
                ("LootNode_MidTier_OverlookSE_13",    new Vector2( 42.0f, -42.0f), "P_en_WoodPallet_01_05"),
                ("LootNode_MidTier_OverlookSW_14",    new Vector2(-42.0f, -42.0f), "P_en_WoodPallet_01_05"),

                // 4 Perimeter Supply Caches
                ("LootNode_Standard_NorthApproach_15",new Vector2(  0.0f,  44.0f), "P_en_CartonBox_01_01"),
                ("LootNode_Standard_SouthApproach_16",new Vector2(  0.0f, -44.0f), "P_en_CartonBox_01_01"),
                ("LootNode_Standard_EastApproach_17", new Vector2( 44.0f,   0.0f), "P_en_CartonBox_01_01"),
                ("LootNode_Standard_WestApproach_18", new Vector2(-44.0f,   0.0f), "P_en_CartonBox_01_01")
            };

            foreach (var n in nodes)
            {
                Vector3 pos = new Vector3(center.x + n.pos.x, center.y, center.z + n.pos.y);
                pos.y = terrain.SampleHeight(pos) + terrain.transform.position.y;

                GameObject nodeGo = new GameObject(n.name);
                nodeGo.transform.SetParent(lootContainer, false);
                nodeGo.transform.position = pos;

                // Attach visual supply cache prop so designers & players can see the loot marker immediately
                SpawnPropSnapToGround(n.visualProp, pos, Vector2.zero, 0f, terrain, nodeGo.transform, diagnostics);
                report.LootNodesGenerated++;
            }
        }

        private static void GenerateBalancedSpawnPoints(
            Terrain terrain,
            Vector3 center,
            Transform spawnPointsContainer,
            List<Transform> perimeterBastions,
            BuildSummaryReport report)
        {
            // Place 8 balanced spawn points anchored to the 8 shaded Perimeter Spawn Bastion buildings
            // (settled on their flattened terrain footprints and tucked into their shaded interior/courtyard side facing inward)
            Vector2[] fallbackOffsets = new Vector2[]
            {
                new Vector2(  0f,  96f),
                new Vector2( 68f,  82f),
                new Vector2( 98f,   0f),
                new Vector2( 68f, -82f),
                new Vector2(  0f, -96f),
                new Vector2(-68f, -82f),
                new Vector2(-98f,   0f),
                new Vector2(-68f,  82f)
            };

            for (int i = 0; i < 8; i++)
            {
                string spName = $"SpawnPoint_{i + 1:00}";
                Vector3 pos;

                if (i < perimeterBastions.Count && perimeterBastions[i] != null)
                {
                    Transform bastion = perimeterBastions[i];
                    Vector3 bPos = bastion.position;
                    Vector3 toCenter = new Vector3(center.x - bPos.x, 0f, center.z - bPos.z).normalized;
                    // Position inside the shaded building footprint / entrance apron (3.5m toward town center from building origin)
                    pos = bPos + toCenter * 3.5f;
                }
                else
                {
                    pos = new Vector3(center.x + fallbackOffsets[i].x, center.y, center.z + fallbackOffsets[i].y);
                }

                float groundY = terrain.SampleHeight(pos) + terrain.transform.position.y;
                pos.y = groundY + 0.20f; // Clean floor clearance on the flattened building apron

                // Face inward with slight tactical angle so line-of-sight is broken by inner-ring structures
                Vector3 inwardDir = new Vector3(center.x - pos.x, 0f, center.z - pos.z).normalized;
                Quaternion lookInward = inwardDir.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(inwardDir, Vector3.up) * Quaternion.Euler(0f, (i % 2 == 0 ? 12f : -12f), 0f)
                    : Quaternion.identity;

                GameObject spGo = new GameObject(spName);
                spGo.transform.SetParent(spawnPointsContainer, false);
                spGo.transform.position = pos;
                spGo.transform.rotation = lookInward;

                report.SpawnPointsGenerated++;
                Vector3 fwd = spGo.transform.forward;
                report.SpawnPointPositions.Add($"{spName}: Pos=({pos.x:F1}, {pos.y:F2}, {pos.z:F1}), Forward=({fwd.x:F2}, {fwd.z:F2})");
            }
        }

        private static void EnsureCollidersAndStaticFlags(GameObject root, bool isStatic)
        {
            StaticEditorFlags flags = StaticEditorFlags.BatchingStatic |
                                      StaticEditorFlags.ContributeGI |
                                      StaticEditorFlags.NavigationStatic |
                                      StaticEditorFlags.OccludeeStatic |
                                      StaticEditorFlags.OccluderStatic |
                                      StaticEditorFlags.ReflectionProbeStatic;

            Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in allTransforms)
            {
                if (isStatic)
                {
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
                }

                MeshFilter mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    Collider existingCol = t.GetComponent<Collider>();
                    if (existingCol == null)
                    {
                        MeshCollider mc = t.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                    }
                    else if (!existingCol.enabled)
                    {
                        existingCol.enabled = true;
                    }
                }
            }
        }

        private static void ConfigureScorchedLightingAndAtmosphere(Vector3 planeCenter)
        {
            // Find or create Directional Light
            Light dirLight = null;
            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light l in lights)
            {
                if (l.type == LightType.Directional)
                {
                    dirLight = l;
                    break;
                }
            }

            if (dirLight == null)
            {
                GameObject lightGo = new GameObject("Directional Light");
                dirLight = lightGo.AddComponent<Light>();
                dirLight.type = LightType.Directional;
            }

            // Phase 5.1: Dramatic afternoon angle (Rotation: X = 35, Y = -40, Z = 0)
            dirLight.transform.rotation = Quaternion.Euler(35f, -40f, 0f);

            // Phase 5.2: Harsh warm solar tint (#FFF1D6), Intensity 1.8, Soft Shadows with High Resolution
            if (ColorUtility.TryParseHtmlString("#FFF1D6", out Color sunColor))
            {
                dirLight.color = sunColor;
            }
            dirLight.intensity = 1.8f;
            dirLight.shadows = LightShadows.Soft;
            dirLight.shadowResolution = LightShadowResolution.VeryHigh;
            dirLight.shadowStrength = 0.92f;
            dirLight.shadowBias = 0.05f;
            dirLight.shadowNormalBias = 0.4f;

            UniversalAdditionalLightData urpLightData = dirLight.GetComponent<UniversalAdditionalLightData>();
            if (urpLightData == null)
            {
                urpLightData = dirLight.gameObject.AddComponent<UniversalAdditionalLightData>();
            }
            urpLightData.usePipelineSettings = false;

            // Ensure shadow distance covers the arena in URP / QualitySettings
            QualitySettings.shadowDistance = 260f;
            UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                urpAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;

            if (urpAsset != null)
            {
                urpAsset.shadowDistance = 260f;
                urpAsset.cascade4Split = new Vector3(0.06f, 0.18f, 0.45f);
                EditorUtility.SetDirty(urpAsset);
            }

            // Phase 5.3: Exponential atmospheric fog (#D9B382, density 0.004)
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            if (ColorUtility.TryParseHtmlString("#D9B382", out Color fogColor))
            {
                RenderSettings.fogColor = fogColor;
            }
            RenderSettings.fogDensity = 0.004f;

            // Warm arid ambient lighting
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.85f, 0.74f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.68f, 0.54f, 0.40f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.30f, 0.24f);

            RenderSettings.sun = dirLight;

            // Frame Main Camera and SceneView to overlook the Sun-Scorched Town Square and Main Street avenues
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                Vector3 camPos = planeCenter + new Vector3(-32f, 22f, -38f);
                mainCam.transform.position = camPos;
                mainCam.transform.rotation = Quaternion.LookRotation((planeCenter + new Vector3(0f, 3f, 0f)) - camPos, Vector3.up);
            }

            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.LookAt(planeCenter + new Vector3(0f, 4f, 0f), Quaternion.Euler(28f, 38f, 0f), 85f);
                SceneView.lastActiveSceneView.Repaint();
            }

            Debug.Log("[Phase 5] Configured Directional Light (Rot: 35, -40, 0 | Color: #FFF1D6 | Intensity: 1.8 | Soft Shadows) & Exponential Fog (#D9B382, Density: 0.004).");
        }
    }
}
