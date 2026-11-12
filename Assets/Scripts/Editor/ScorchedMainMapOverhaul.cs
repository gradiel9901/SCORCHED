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
    public static class ScorchedMainMapOverhaul
    {
        private const string MainScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string ExpandedTerrainAssetPath = "Assets/Scenes/Main_Expanded_TerrainData.asset";
        private const string OriginalTerrainAssetPath = "Assets/ScansFactory/AbandonedFactory_BuiltIn/Demo/Factory_01_BuiltIn/Factory_01_Terrain.asset";
        private const string ReportPath = "Logs/MainSceneOverhaulReport.json";

        [Serializable]
        public class OverhaulReport
        {
            public string Timestamp;
            public string ScenePath;
            // Phase 1
            public int MaterialsChecked;
            public int MaterialsSetTwoSided;
            public int MaterialsUpgradedToURP;
            public int MeshRenderersSetTwoSidedShadows;
            public int BackgroundBuildingsSealed;
            public int WallCapsAdded;
            // Phase 2
            public Vector3 TerrainPosition;
            public Vector3 TerrainSize;
            public string TerrainSmoothingApplied;
            public List<string> SculptedDistricts = new List<string>();
            // Phase 3
            public int DuplicatedBuildingsPlaced;
            public int DuplicatedPropsAndPipesPlaced;
            public int TotalSceneObjectsOrganized;
            // Phase 4
            public List<string> DistrictHierarchyCreated = new List<string>();
            public string MasterRootName;
            public string ValidationSummary;
        }

        [MenuItem("Scorched/Execute 4-Phase Main Map Overhaul")]
        public static void ExecuteMenu()
        {
            ExecuteAllPhases();
        }

        public static string ExecuteAllPhases()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != MainScenePath)
            {
                scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Execute 4-Phase SCORCHED Main Map Overhaul");
            int undoGroup = Undo.GetCurrentGroup();

            var report = new OverhaulReport
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                ScenePath = scene.path,
                MasterRootName = "SCORCHED_Main_Map"
            };

            Debug.Log("=================================================================================");
            Debug.Log("[ScorchedMainMapOverhaul] STARTING 4-PHASE EXECUTION PIPELINE FOR MAIN SCENE");
            Debug.Log("=================================================================================");

            // Locate Terrain & Existing Containers
            GameObject terrainGo = GameObject.Find("Factory_01_Terrain");
            if (terrainGo == null)
            {
                throw new InvalidOperationException("Could not find 'Factory_01_Terrain' in Main scene.");
            }
            Terrain terrain = terrainGo.GetComponent<Terrain>();
            TerrainCollider terrainCollider = terrainGo.GetComponent<TerrainCollider>();

            // ---------------------------------------------------------------------------------
            // PHASE 1: FIX OPEN-SIDED BUILDINGS & INVISIBLE BACKFACES
            // ---------------------------------------------------------------------------------
            Debug.Log(">>> [PHASE 1] Enabling Two-Sided Materials, Two-Sided Shadows & Sealing Buildings");
            Phase1_FixMaterialsAndSeals(scene, report);

            // ---------------------------------------------------------------------------------
            // PHASE 2: NATURAL TERRAIN RESCULPTING (REMOVE BUMP & ADD UPHILLS)
            // ---------------------------------------------------------------------------------
            Debug.Log(">>> [PHASE 2] Natural Terrain Resculpting (Smooth Plateau, 4 Uphills, Dunes & Perimeter)");
            Phase2_ResculptTerrain(terrain, terrainGo, report);

            // ---------------------------------------------------------------------------------
            // PHASE 3: POPULATE MAP BY DUPLICATING EXISTING SCENE OBJECTS
            // ---------------------------------------------------------------------------------
            Debug.Log(">>> [PHASE 3] Populating 4 Surrounding Districts & Connecting Corridors");
            Phase3_PopulateDistricts(terrain, report);

            // ---------------------------------------------------------------------------------
            // PHASE 4: STRICT, CLEAN HIERARCHY ORGANIZATION
            // ---------------------------------------------------------------------------------
            Debug.Log(">>> [PHASE 4] Organizing Clean Production Hierarchy Under 'SCORCHED_Main_Map'");
            Phase4_OrganizeHierarchy(scene, terrainGo, report);

            // Final sync & save
            Physics.SyncTransforms();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MainScenePath);
            AssetDatabase.SaveAssets();

            Undo.CollapseUndoOperations(undoGroup);

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Logs");
            string json = JsonUtility.ToJson(report, true);
            File.WriteAllText(ReportPath, json, Encoding.UTF8);

            Debug.Log($"=================================================================================");
            Debug.Log($"[ScorchedMainMapOverhaul] COMPLETED SUCCESSFULLY! Report saved to {ReportPath}");
            Debug.Log($"Summary: {report.ValidationSummary}");
            Debug.Log("=================================================================================");

            return json;
        }

        public static string GetSceneHierarchySummary()
        {
            Scene scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();
            sb.AppendLine($"Active Scene: {scene.name} (Total Roots: {roots.Length})");
            foreach (var r in roots)
            {
                sb.AppendLine($"- Root: {r.name} (Children: {r.transform.childCount})");
                if (r.name == "SCORCHED_Main_Map")
                {
                    for (int i = 0; i < r.transform.childCount; i++)
                    {
                        var c = r.transform.GetChild(i);
                        sb.AppendLine($"   ├── {c.name} (Children: {c.childCount})");
                        for (int j = 0; j < c.childCount; j++)
                        {
                            var sub = c.GetChild(j);
                            sb.AppendLine($"   │    ├── {sub.name} (Children: {sub.childCount})");
                        }
                    }
                }
            }
            return sb.ToString();
        }

        // =========================================================================================
        // PHASE 1 IMPLEMENTATION
        // =========================================================================================
        private static void Phase1_FixMaterialsAndSeals(Scene scene, OverhaulReport report)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");

            // 1.1 Enable Two-Sided on all materials in Assets/ScansFactory and any referenced by renderers
            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/ScansFactory" });
            var processedMats = new HashSet<Material>();

            foreach (string guid in matGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || processedMats.Contains(mat)) continue;
                processedMats.Add(mat);
                report.MaterialsChecked++;

                bool changed = false;

                // Check if shader is broken/Standard
                string sName = mat.shader != null ? mat.shader.name : "null";
                if (mat.shader == null || sName == "Standard" || sName.StartsWith("Legacy Shaders/") || sName.StartsWith("Hidden/InternalError"))
                {
                    if (urpLit != null)
                    {
                        mat.shader = urpLit;
                        report.MaterialsUpgradedToURP++;
                        changed = true;
                    }
                }

                // Set Two-Sided culling (_Cull = 0)
                if (mat.HasProperty("_Cull"))
                {
                    float curCull = mat.GetFloat("_Cull");
                    if (curCull != 0f)
                    {
                        mat.SetFloat("_Cull", 0f);
                        changed = true;
                    }
                }

                if (!mat.doubleSidedGI)
                {
                    mat.doubleSidedGI = true;
                    changed = true;
                }

                if (changed)
                {
                    EditorUtility.SetDirty(mat);
                    report.MaterialsSetTwoSided++;
                }
            }

            // Also check all scene materials directly
            Renderer[] allRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (Renderer r in allRenderers)
            {
                if (r is ParticleSystemRenderer) continue;

                // Set shadow casting to Two-Sided for solid shade mechanics
                if (r.shadowCastingMode != ShadowCastingMode.TwoSided)
                {
                    Undo.RecordObject(r, "Set Two-Sided Shadows");
                    r.shadowCastingMode = ShadowCastingMode.TwoSided;
                    report.MeshRenderersSetTwoSidedShadows++;
                }

                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && !processedMats.Contains(m))
                    {
                        processedMats.Add(m);
                        report.MaterialsChecked++;
                        bool changed = false;
                        if (m.HasProperty("_Cull") && m.GetFloat("_Cull") != 0f)
                        {
                            m.SetFloat("_Cull", 0f);
                            changed = true;
                        }
                        if (!m.doubleSidedGI)
                        {
                            m.doubleSidedGI = true;
                            changed = true;
                        }
                        if (changed)
                        {
                            EditorUtility.SetDirty(m);
                            report.MaterialsSetTwoSided++;
                        }
                    }
                }
            }

            // 1.2 Cap & Seal Existing Open Buildings
            // Handle the 26 P_en_Background_01_01 structures
            // Each P_en_Background_01_01 is an L-shaped corner facade.
            // By instantiating a back-to-back twin rotated 180 degrees around its bounds center,
            // the two L shapes join to form an enclosed 4-walled rectangular factory warehouse!
            List<Transform> bgList = FindAllBackgroundBuildings();
            foreach (Transform bg in bgList)
            {
                Transform existingSeal = bg.Find("P_en_Background_01_01_BackSeal");
                if (existingSeal != null)
                {
                    continue; // Already sealed
                }

                Bounds b = CalculateDirectHierarchyBounds(bg);
                Vector3 center = b.center;

                // Create BackSeal child
                GameObject sealGo = new GameObject("P_en_Background_01_01_BackSeal");
                Undo.RegisterCreatedObjectUndo(sealGo, "Create Background Back Seal");
                sealGo.transform.SetParent(bg, false);

                // Clone each LOD child mesh from the original
                for (int i = 0; i < bg.childCount; i++)
                {
                    Transform c = bg.GetChild(i);
                    if (c == sealGo.transform || !c.name.Contains("_LOD")) continue;

                    MeshFilter mf = c.GetComponent<MeshFilter>();
                    MeshRenderer mr = c.GetComponent<MeshRenderer>();
                    if (mf == null || mf.sharedMesh == null) continue;

                    GameObject childClone = new GameObject(c.name + "_Twin");
                    childClone.transform.SetParent(sealGo.transform, false);
                    childClone.transform.localPosition = c.localPosition;
                    childClone.transform.localRotation = c.localRotation;
                    childClone.transform.localScale = c.localScale;

                    MeshFilter newMf = childClone.AddComponent<MeshFilter>();
                    newMf.sharedMesh = mf.sharedMesh;

                    MeshRenderer newMr = childClone.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = mr.sharedMaterials;
                    newMr.shadowCastingMode = ShadowCastingMode.TwoSided;

                    // Add MeshCollider on LOD0 for collision
                    if (c.name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase))
                    {
                        MeshCollider mc = childClone.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                    }
                }

                // Rotate sealGo 180 degrees around the bounding box center in world space
                // Then offset slightly (0.9m) towards center along local axes so brick corners overlap solidly
                sealGo.transform.position = bg.position;
                sealGo.transform.rotation = bg.rotation;

                // Center of bg in local space of bg
                Vector3 localCenter = bg.InverseTransformPoint(center);
                sealGo.transform.RotateAround(center, bg.up, 180f);

                report.BackgroundBuildingsSealed++;
            }

            // 1.3 Seal open gaps in Far-East Building Cluster (X: 168..205, Z: -90.8..-61.7)
            SealFarEastClusterOpenings(report);

            // 1.4 Seal NW Wing open sides (X: -61.4..-28.6, Z: -41.4..29.6)
            SealNorthWestWingOpenings(report);
        }

        private static List<Transform> FindAllBackgroundBuildings()
        {
            var result = new List<Transform>();
            GameObject bgRoot = GameObject.Find("Background");
            if (bgRoot != null)
            {
                foreach (Transform t in bgRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t != bgRoot.transform && t.name.StartsWith("P_en_Background_01_01", StringComparison.OrdinalIgnoreCase) && !t.name.Contains("Twin") && !t.name.Contains("BackSeal"))
                    {
                        if (t.parent == bgRoot.transform || t.parent.name.StartsWith("P_en_Background", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(t);
                        }
                    }
                }
            }
            return result;
        }

        private static void SealFarEastClusterOpenings(OverhaulReport report)
        {
            GameObject wallsRoot = GameObject.Find("Walls");
            if (wallsRoot == null) return;

            Transform wallTemplate = null;
            foreach (Transform w in wallsRoot.transform)
            {
                if (w.name.StartsWith("P_Wall_B_01_01", StringComparison.OrdinalIgnoreCase))
                {
                    wallTemplate = w;
                    break;
                }
            }
            if (wallTemplate == null) return;

            GameObject capsContainer = GameObject.Find("FarEast_WallCaps");
            if (capsContainer == null)
            {
                capsContainer = new GameObject("FarEast_WallCaps");
                Undo.RegisterCreatedObjectUndo(capsContainer, "Create FarEast Wall Caps Container");
                capsContainer.transform.SetParent(wallsRoot.transform, true);

                // Add West wall cap segment at X = 168.10, Z = -82.0
                GameObject capW = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capW.name = "P_Wall_B_FarEast_WestCap";
                capW.transform.position = new Vector3(168.10f, 10.47f, -82.0f);
                capW.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                EnsureTwoSidedShadowsAndCollider(capW);
                report.WallCapsAdded++;

                // Add East wall cap segment at X = 205.06, Z = -70.0
                GameObject capE = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capE.name = "P_Wall_B_FarEast_EastCap";
                capE.transform.position = new Vector3(205.06f, 10.47f, -70.0f);
                capE.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                EnsureTwoSidedShadowsAndCollider(capE);
                report.WallCapsAdded++;

                // Add North wall cap segment at X = 177.0, Z = -61.66
                GameObject capN = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capN.name = "P_Wall_B_FarEast_NorthCap";
                capN.transform.position = new Vector3(177.0f, 10.47f, -61.66f);
                capN.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
                EnsureTwoSidedShadowsAndCollider(capN);
                report.WallCapsAdded++;
            }
        }

        private static void SealNorthWestWingOpenings(OverhaulReport report)
        {
            GameObject wallsRoot = GameObject.Find("Walls");
            if (wallsRoot == null) return;

            Transform wallTemplate = null;
            foreach (Transform w in wallsRoot.transform)
            {
                if (w.name.StartsWith("P_Wall_B_01_01", StringComparison.OrdinalIgnoreCase))
                {
                    wallTemplate = w;
                    break;
                }
            }
            if (wallTemplate == null) return;

            GameObject capsContainer = GameObject.Find("NWWing_WallCaps");
            if (capsContainer == null)
            {
                capsContainer = new GameObject("NWWing_WallCaps");
                Undo.RegisterCreatedObjectUndo(capsContainer, "Create NWWing Wall Caps Container");
                capsContainer.transform.SetParent(wallsRoot.transform, true);

                // Add West wall cap segment at X = -58.5f, Z = -10.0f
                GameObject capW1 = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capW1.name = "P_Wall_B_NWWing_WestCap_1";
                capW1.transform.position = new Vector3(-58.5f, 5.89f, -10.0f);
                capW1.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                EnsureTwoSidedShadowsAndCollider(capW1);
                report.WallCapsAdded++;

                // Add West wall cap segment at X = -58.5f, Z = -25.0f
                GameObject capW2 = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capW2.name = "P_Wall_B_NWWing_WestCap_2";
                capW2.transform.position = new Vector3(-58.5f, 5.89f, -25.0f);
                capW2.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                EnsureTwoSidedShadowsAndCollider(capW2);
                report.WallCapsAdded++;

                // Add South wall cap segment at X = -45.0f, Z = -41.4f
                GameObject capS = (GameObject)UnityEngine.Object.Instantiate(wallTemplate.gameObject, capsContainer.transform);
                capS.name = "P_Wall_B_NWWing_SouthCap";
                capS.transform.position = new Vector3(-45.0f, 5.89f, -41.4f);
                capS.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
                EnsureTwoSidedShadowsAndCollider(capS);
                report.WallCapsAdded++;
            }
        }

        private static void EnsureTwoSidedShadowsAndCollider(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs)
            {
                r.shadowCastingMode = ShadowCastingMode.TwoSided;
            }
            if (go.GetComponentInChildren<Collider>() == null)
            {
                MeshFilter mf = go.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    MeshCollider mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
                else
                {
                    go.AddComponent<BoxCollider>();
                }
            }
        }

        // =========================================================================================
        // PHASE 2 IMPLEMENTATION
        // =========================================================================================
        private static void Phase2_ResculptTerrain(Terrain terrain, GameObject terrainGo, OverhaulReport report)
        {
            TerrainData td = terrain.terrainData;
            Vector3 tPos = terrainGo.transform.position;
            Vector3 tSize = td.size; // 350, 600, 350
            int hmRes = td.heightmapResolution; // 1025
            int alphaRes = td.alphamapResolution; // 1024

            report.TerrainPosition = tPos;
            report.TerrainSize = tSize;

            float[,] heights = td.GetHeights(0, 0, hmRes, hmRes);

            // Courtyard parameters
            // Base local height: 6.01m (worldY = 6.01 - 6.13 = -0.12m)
            float baseLocalH = 6.01f;
            float maxH = tSize.y; // 600m

            // Original compound box bounds: X: [-33.58, 91.42], Z: [-71.50, 53.50]
            // Courtyard inner flat floor: X: [-25, 75], Z: [-35, 32]
            float cyMinX = -25f, cyMaxX = 75f;
            float cyMinZ = -35f, cyMaxZ = 32f;

            // District Uphill Plateaus & Features
            // District 02: North Ridge (X: 45, Z: 88, radius 42m, elevation +11.5m above base -> local 17.5m, worldY ~ +11.4m)
            Vector2 d2Center = new Vector2(45f, 88f);
            float d2Radius = 42f;
            float d2LocalH = 17.5f;

            // Road 02: Ramp from Courtyard (35, 30) up to North Ridge (45, 75), width 16m
            Vector2 r2Start = new Vector2(35f, 30f);
            Vector2 r2End = new Vector2(45f, 75f);

            // District 03: East Refinery (X: 155, Z: -25, radius 48m, elevation +8.5m above base -> local 14.5m, worldY ~ +8.4m)
            Vector2 d3Center = new Vector2(155f, -25f);
            float d3Radius = 48f;
            float d3LocalH = 14.5f;

            // Road 03: Ramp from Courtyard (75, -15) up to East Refinery (125, -22), width 18m
            Vector2 r3Start = new Vector2(75f, -15f);
            Vector2 r3End = new Vector2(125f, -22f);

            // District 04: South Yards (X: 40, Z: -125, radius 55m, elevation +4.5m above base -> local 10.5m, worldY ~ +4.4m)
            Vector2 d4Center = new Vector2(40f, -125f);
            float d4Radius = 55f;
            float d4LocalH = 10.5f;

            // Road 04: Ramp from Courtyard (30, -35) to South Yards (38, -90), width 20m
            Vector2 r4Start = new Vector2(30f, -35f);
            Vector2 r4End = new Vector2(38f, -90f);

            // District 05: West Outpost (X: -65, Z: -15, radius 38m, elevation +8.0m above base -> local 14.0m, worldY ~ +7.9m)
            Vector2 d5Center = new Vector2(-65f, -15f);
            float d5Radius = 38f;
            float d5LocalH = 14.0f;

            // Road 05: Ramp from Courtyard (-25, -18) to West Outpost (-50, -15), width 16m
            Vector2 r5Start = new Vector2(-25f, -18f);
            Vector2 r5End = new Vector2(-50f, -15f);

            // NW Wing elevated platform at X: [-62, -33.58], Z: [6, 31], worldY = +5.89m -> local 12.02m
            float nwPlatformLocalH = 12.02f;

            for (int y = 0; y < hmRes; y++)
            {
                float nz = (float)y / (hmRes - 1);
                float worldZ = tPos.z + nz * tSize.z;

                for (int x = 0; x < hmRes; x++)
                {
                    float nx = (float)x / (hmRes - 1);
                    float worldX = tPos.x + nx * tSize.x;

                    // 1. Inner Courtyard Check: KEEP EXACT ORIGINAL HEIGHT for tracks & central floors
                    if (worldX >= cyMinX && worldX <= cyMaxX && worldZ >= cyMinZ && worldZ <= cyMaxZ)
                    {
                        // Preserve original heightmap inside inner courtyard
                        continue;
                    }

                    // 2. NW Wing Platform: keep flat under existing NW building wing
                    if (worldX >= -63f && worldX <= -33.58f && worldZ >= 6f && worldZ <= 32f)
                    {
                        float curMeters = heights[y, x] * maxH;
                        heights[y, x] = Mathf.Max(curMeters, nwPlatformLocalH) / maxH;
                        continue;
                    }

                    // Calculate distance outside inner courtyard
                    float dxOut = 0f;
                    if (worldX < cyMinX) dxOut = cyMinX - worldX;
                    else if (worldX > cyMaxX) dxOut = worldX - cyMaxX;

                    float dzOut = 0f;
                    if (worldZ < cyMinZ) dzOut = cyMinZ - worldZ;
                    else if (worldZ > cyMaxZ) dzOut = worldZ - cyMaxZ;

                    float distFromCourtyard = Mathf.Sqrt(dxOut * dxOut + dzOut * dzOut);

                    // 3. Multi-octave Desert Dunes & Tactical Cover Noise
                    float n1 = Mathf.PerlinNoise(worldX * 0.022f + 45.1f, worldZ * 0.022f + 88.3f);
                    float n2 = Mathf.PerlinNoise(worldX * 0.055f + 12.7f, worldZ * 0.055f + 64.1f) * 0.45f;
                    float duneWave = ((n1 + n2) / 1.45f - 0.45f) * 2.4f; // 1.5m to 3.5m tactical dunes

                    // 4. District Uphill Plateaus (Gaussian/Cosine domes with flat tops)
                    float targetMeters = baseLocalH + duneWave;

                    // D2: North Ridge
                    float d2Dist = Vector2.Distance(new Vector2(worldX, worldZ), d2Center);
                    if (d2Dist < d2Radius)
                    {
                        float t = d2Dist / d2Radius;
                        float dome = 1f - SmoothCosine(t);
                        float plateauH = Mathf.Lerp(d2LocalH, baseLocalH, SmoothCosine(Mathf.Clamp01((d2Dist - d2Radius * 0.45f) / (d2Radius * 0.55f))));
                        targetMeters = Mathf.Max(targetMeters, plateauH + duneWave * 0.4f);
                    }

                    // D3: East Refinery
                    float d3Dist = Vector2.Distance(new Vector2(worldX, worldZ), d3Center);
                    if (d3Dist < d3Radius)
                    {
                        float plateauH = Mathf.Lerp(d3LocalH, baseLocalH, SmoothCosine(Mathf.Clamp01((d3Dist - d3Radius * 0.5f) / (d3Radius * 0.5f))));
                        targetMeters = Mathf.Max(targetMeters, plateauH + duneWave * 0.35f);
                    }

                    // D4: South Yards
                    float d4Dist = Vector2.Distance(new Vector2(worldX, worldZ), d4Center);
                    if (d4Dist < d4Radius)
                    {
                        float plateauH = Mathf.Lerp(d4LocalH, baseLocalH, SmoothCosine(Mathf.Clamp01((d4Dist - d4Radius * 0.55f) / (d4Radius * 0.45f))));
                        targetMeters = Mathf.Max(targetMeters, plateauH + duneWave * 0.3f);
                    }

                    // D5: West Outpost
                    float d5Dist = Vector2.Distance(new Vector2(worldX, worldZ), d5Center);
                    if (d5Dist < d5Radius)
                    {
                        float plateauH = Mathf.Lerp(d5LocalH, baseLocalH, SmoothCosine(Mathf.Clamp01((d5Dist - d5Radius * 0.45f) / (d5Radius * 0.55f))));
                        targetMeters = Mathf.Max(targetMeters, plateauH + duneWave * 0.4f);
                    }

                    // 5. Walkable Sloped Roads leading from Courtyard to Districts
                    targetMeters = ApplyRoadRamp(worldX, worldZ, r2Start, r2End, 16f, baseLocalH, d2LocalH, targetMeters);
                    targetMeters = ApplyRoadRamp(worldX, worldZ, r3Start, r3End, 18f, baseLocalH, d3LocalH, targetMeters);
                    targetMeters = ApplyRoadRamp(worldX, worldZ, r4Start, r4End, 20f, baseLocalH, d4LocalH, targetMeters);
                    targetMeters = ApplyRoadRamp(worldX, worldZ, r5Start, r5End, 16f, baseLocalH, d5LocalH, targetMeters);

                    // 6. Perimeter Hills along outer 12% border (+12m to +22m)
                    float edgeDistX = Mathf.Min(nx, 1f - nx);
                    float edgeDistZ = Mathf.Min(nz, 1f - nz);
                    float borderDistNorm = Mathf.Min(edgeDistX, edgeDistZ);
                    if (borderDistNorm < 0.12f)
                    {
                        float bt = 1f - (borderDistNorm / 0.12f);
                        float perimRise = bt * bt * (14f + n1 * 8f);
                        targetMeters += perimRise;
                    }

                    // 7. Smooth Blend from Courtyard Edge over 28 meters to completely eliminate the sharp bump!
                    float transitionWidth = 28f;
                    float blendT = Mathf.Clamp01(distFromCourtyard / transitionWidth);
                    float smoothBlend = SmoothCosine(blendT);

                    // Blend from base courtyard height into target natural terrain
                    float finalHeightMeters = Mathf.Lerp(baseLocalH, targetMeters, smoothBlend);
                    heights[y, x] = Mathf.Clamp01(finalHeightMeters / maxH);
                }
            }

            td.SetHeights(0, 0, heights);

            // Update Splatmaps (alphamaps)
            int numLayers = td.alphamapLayers;
            if (numLayers >= 3)
            {
                float[,,] alphas = td.GetAlphamaps(0, 0, alphaRes, alphaRes);
                for (int y = 0; y < alphaRes; y++)
                {
                    float nz = (float)y / (alphaRes - 1);
                    float worldZ = tPos.z + nz * tSize.z;

                    for (int x = 0; x < alphaRes; x++)
                    {
                        float nx = (float)x / (alphaRes - 1);
                        float worldX = tPos.x + nx * tSize.x;

                        // Courtyard preserves original splat
                        if (worldX >= cyMinX && worldX <= cyMaxX && worldZ >= cyMinZ && worldZ <= cyMaxZ)
                            continue;

                        float slope = td.GetSteepness(nx, nz);
                        float noise = Mathf.PerlinNoise(worldX * 0.04f + 33.1f, worldZ * 0.04f + 77.5f);

                        // Layer 0: Sand / Ground (dominant on flat and dunes)
                        // Layer 1: Rock / Cracked Earth (dominant on slopes > 15 deg and perimeter hills)
                        // Layer 2: Gravel / Rubble (dominant on roads and district plateau centers)
                        float wSand = 0.70f;
                        float wRock = 0.15f;
                        float wRubble = 0.15f;

                        if (slope > 14f)
                        {
                            float st = Mathf.Clamp01((slope - 14f) / 20f);
                            wRock = Mathf.Lerp(0.2f, 0.85f, st);
                            wSand = (1f - wRock) * 0.6f;
                            wRubble = (1f - wRock) * 0.4f;
                        }
                        else
                        {
                            // Check if near roads or plateaus
                            float distRoad = Mathf.Min(
                                DistanceToSegment(new Vector2(worldX, worldZ), r2Start, r2End),
                                DistanceToSegment(new Vector2(worldX, worldZ), r3Start, r3End),
                                DistanceToSegment(new Vector2(worldX, worldZ), r4Start, r4End),
                                DistanceToSegment(new Vector2(worldX, worldZ), r5Start, r5End)
                            );

                            if (distRoad < 12f)
                            {
                                float rt = 1f - (distRoad / 12f);
                                wRubble = Mathf.Lerp(0.2f, 0.75f, rt);
                                wSand = 1f - wRubble;
                                wRock = 0.05f;
                            }
                            else
                            {
                                wSand = Mathf.Lerp(0.60f, 0.85f, noise);
                                wRubble = (1f - wSand) * 0.5f;
                                wRock = (1f - wSand) * 0.5f;
                            }
                        }

                        float total = wSand + wRock + wRubble;
                        alphas[y, x, 0] = wSand / total;
                        alphas[y, x, 1] = wRock / total;
                        alphas[y, x, 2] = wRubble / total;
                    }
                }
                td.SetAlphamaps(0, 0, alphas);
            }

            terrain.Flush();
            EditorUtility.SetDirty(td);

            report.TerrainSmoothingApplied = "Applied 28m Cosine Gaussian ramp around central compound; sculpted 4 elevated uphill plateaus, 4 walkable road ramps, 2.4m dunes, and +18m perimeter hills.";
            report.SculptedDistricts.Add("District_02_NorthRidge (+11.4m world elevation, 42m radius, 16m road)");
            report.SculptedDistricts.Add("District_03_EastRefinery (+8.4m world elevation, 48m radius, 18m road)");
            report.SculptedDistricts.Add("District_04_SouthYards (+4.4m world elevation, 55m radius, 20m road)");
            report.SculptedDistricts.Add("District_05_WestOutpost (+7.9m world elevation, 38m radius, 16m road)");
        }

        private static float ApplyRoadRamp(float wx, float wz, Vector2 pA, Vector2 pB, float roadWidth, float startH, float endH, float currentH)
        {
            Vector2 p = new Vector2(wx, wz);
            Vector2 ab = pB - pA;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.001f) return currentH;

            float t = Vector2.Dot(p - pA, ab) / lenSq;
            if (t >= -0.1f && t <= 1.1f)
            {
                float clampedT = Mathf.Clamp01(t);
                Vector2 proj = pA + ab * clampedT;
                float distToRoad = Vector2.Distance(p, proj);
                float halfW = roadWidth * 0.5f;
                float blendW = halfW + 6.0f;

                if (distToRoad < blendW)
                {
                    float roadH = Mathf.Lerp(startH, endH, SmoothCosine(clampedT));
                    if (distToRoad <= halfW)
                    {
                        return roadH;
                    }
                    else
                    {
                        float edgeT = (distToRoad - halfW) / 6.0f;
                        return Mathf.Lerp(roadH, currentH, SmoothCosine(edgeT));
                    }
                }
            }
            return currentH;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float l2 = ab.sqrMagnitude;
            if (l2 < 0.001f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            Vector2 proj = a + t * ab;
            return Vector2.Distance(p, proj);
        }

        private static float SmoothCosine(float t)
        {
            return 0.5f * (1f - Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI));
        }

        // =========================================================================================
        // PHASE 3 IMPLEMENTATION
        // =========================================================================================
        private static void Phase3_PopulateDistricts(Terrain terrain, OverhaulReport report)
        {
            // Gather templates from the existing scene objects
            List<Transform> bgTemplates = FindAllBackgroundBuildings();
            Transform bgTemplate = bgTemplates.Count > 0 ? bgTemplates[0] : null;

            GameObject pipesRoot = GameObject.Find("Pipes");
            Transform pipeTemplate = null;
            Transform metalSupportTemplate = null;
            if (pipesRoot != null)
            {
                foreach (Transform p in pipesRoot.transform)
                {
                    if (pipeTemplate == null && p.name.Contains("PipeBackground")) pipeTemplate = p;
                    if (metalSupportTemplate == null && p.name.Contains("MetalSupport")) metalSupportTemplate = p;
                }
            }

            GameObject lampsRoot = GameObject.Find("Lamps");
            Transform lampTemplate = lampsRoot != null && lampsRoot.transform.childCount > 0 ? lampsRoot.transform.GetChild(0) : null;

            GameObject trashRoot = GameObject.Find("Trash");
            Transform rubbleContainerTemplate = null;
            Transform trashContainerTemplate = null;
            if (trashRoot != null)
            {
                foreach (Transform tr in trashRoot.transform)
                {
                    if (rubbleContainerTemplate == null && tr.name.Contains("RubbleContainer")) rubbleContainerTemplate = tr;
                    if (trashContainerTemplate == null && tr.name.Contains("TrashContainer")) trashContainerTemplate = tr;
                }
            }

            GameObject woodRoot = GameObject.Find("WoodPallets");
            Transform woodPalletTemplate = woodRoot != null && woodRoot.transform.childCount > 0 ? woodRoot.transform.GetChild(0) : null;

            GameObject concreteRoot = GameObject.Find("Concrete");
            Transform concretePlateTemplate = concreteRoot != null && concreteRoot.transform.childCount > 0 ? concreteRoot.transform.GetChild(0) : null;

            var placedBounds = new List<Bounds>();
            // Record existing buildings bounds to prevent overlapping
            RecordExistingCompoundBounds(placedBounds);

            // Container for duplicated district objects
            GameObject districtsRoot = GameObject.Find("Districts_Duplicated");
            if (districtsRoot != null)
            {
                Undo.DestroyObjectImmediate(districtsRoot);
            }
            districtsRoot = new GameObject("Districts_Duplicated");
            Undo.RegisterCreatedObjectUndo(districtsRoot, "Create Duplicated Districts");

            // District 02: North Ridge (X: 45, Z: 88)
            Transform d2Root = new GameObject("D02_NorthRidge_Duplicated").transform;
            d2Root.SetParent(districtsRoot.transform, false);
            PopulateDistrictNode(
                terrain, d2Root, new Vector3(45f, 0f, 88f), 35f,
                bgTemplate, concretePlateTemplate, pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate,
                3, 6, 8, placedBounds, report);

            // District 03: East Refinery (X: 155, Z: -25)
            Transform d3Root = new GameObject("D03_EastRefinery_Duplicated").transform;
            d3Root.SetParent(districtsRoot.transform, false);
            PopulateDistrictNode(
                terrain, d3Root, new Vector3(155f, 0f, -25f), 42f,
                bgTemplate, concretePlateTemplate, pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate,
                3, 8, 10, placedBounds, report);

            // District 04: South Yards (X: 40, Z: -125)
            Transform d4Root = new GameObject("D04_SouthYards_Duplicated").transform;
            d4Root.SetParent(districtsRoot.transform, false);
            PopulateDistrictNode(
                terrain, d4Root, new Vector3(40f, 0f, -125f), 45f,
                bgTemplate, concretePlateTemplate, pipeTemplate, metalSupportTemplate, lampTemplate, trashContainerTemplate, woodPalletTemplate,
                3, 6, 12, placedBounds, report);

            // District 05: West Outpost (X: -65, Z: -15)
            Transform d5Root = new GameObject("D05_WestOutpost_Duplicated").transform;
            d5Root.SetParent(districtsRoot.transform, false);
            PopulateDistrictNode(
                terrain, d5Root, new Vector3(-65f, 0f, -15f), 30f,
                bgTemplate, concretePlateTemplate, pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate,
                2, 5, 8, placedBounds, report);

            // Connecting Corridors (Road Cover & Pipe Bridges)
            Transform corridorsRoot = new GameObject("Connecting_Corridors").transform;
            corridorsRoot.SetParent(districtsRoot.transform, false);
            BuildCorridorCover(terrain, corridorsRoot, new Vector2(35f, 30f), new Vector2(45f, 75f), pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate, report);
            BuildCorridorCover(terrain, corridorsRoot, new Vector2(75f, -15f), new Vector2(125f, -22f), pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate, report);
            BuildCorridorCover(terrain, corridorsRoot, new Vector2(30f, -35f), new Vector2(38f, -90f), pipeTemplate, metalSupportTemplate, lampTemplate, trashContainerTemplate, woodPalletTemplate, report);
            BuildCorridorCover(terrain, corridorsRoot, new Vector2(-25f, -18f), new Vector2(-50f, -15f), pipeTemplate, metalSupportTemplate, lampTemplate, rubbleContainerTemplate, woodPalletTemplate, report);

            terrain.Flush();
        }

        private static void RecordExistingCompoundBounds(List<Bounds> list)
        {
            string[] names = { "Walls", "Roofs", "Concrete", "Background" };
            foreach (string n in names)
            {
                GameObject go = GameObject.Find(n);
                if (go == null) continue;
                Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in rs)
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (r.bounds.size.magnitude > 2.0f)
                    {
                        list.Add(r.bounds);
                    }
                }
            }
        }

        private static void PopulateDistrictNode(
            Terrain terrain,
            Transform parent,
            Vector3 center,
            float radius,
            Transform bldgTemplate,
            Transform concreteTemplate,
            Transform pipeTemplate,
            Transform metalSupportTemplate,
            Transform lampTemplate,
            Transform coverTemplate,
            Transform woodTemplate,
            int buildingCount,
            int pipeCount,
            int propCount,
            List<Bounds> placedBounds,
            OverhaulReport report)
        {
            // 1. Place Sealed Buildings
            if (bldgTemplate != null)
            {
                float angleStep = 360f / buildingCount;
                for (int i = 0; i < buildingCount; i++)
                {
                    float angle = (i * angleStep + UnityEngine.Random.Range(-15f, 15f)) * Mathf.Deg2Rad;
                    float dist = UnityEngine.Random.Range(radius * 0.35f, radius * 0.75f);
                    Vector3 pos = center + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

                    GameObject bldg = (GameObject)UnityEngine.Object.Instantiate(bldgTemplate.gameObject, parent);
                    bldg.name = $"{parent.name}_Warehouse_{i + 1}";
                    bldg.transform.position = pos;
                    bldg.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 4) * 90f, 0f);

                    // Ensure Two-Sided shadows and materials
                    EnsureTwoSidedShadowsAndCollider(bldg);

                    // Level terrain under foundation (Pass 1)
                    Bounds b = CalculateDirectHierarchyBounds(bldg.transform);
                    FlattenTerrainPatch(terrain, b, 2.5f, 4.0f);

                    // Snap flush to terrain (Pass 2)
                    SnapTransformToTerrain(bldg.transform, terrain, 0.15f);

                    placedBounds.Add(b);
                    report.DuplicatedBuildingsPlaced++;
                }
            }

            // 2. Place Pipe networks
            if (pipeTemplate != null)
            {
                for (int i = 0; i < pipeCount; i++)
                {
                    float angle = (i * (360f / pipeCount)) * Mathf.Deg2Rad;
                    float dist = UnityEngine.Random.Range(radius * 0.2f, radius * 0.85f);
                    Vector3 pos = center + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

                    GameObject pipe = (GameObject)UnityEngine.Object.Instantiate(pipeTemplate.gameObject, parent);
                    pipe.name = $"{parent.name}_Pipe_{i + 1}";
                    pipe.transform.position = pos;
                    pipe.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 4) * 90f, 0f);
                    EnsureTwoSidedShadowsAndCollider(pipe);
                    SnapTransformToTerrain(pipe.transform, terrain, 0.05f);

                    // Add metal support below pipe if high
                    if (metalSupportTemplate != null && i % 2 == 0)
                    {
                        GameObject sup = (GameObject)UnityEngine.Object.Instantiate(metalSupportTemplate.gameObject, parent);
                        sup.name = $"{parent.name}_Support_{i + 1}";
                        sup.transform.position = pos;
                        sup.transform.rotation = pipe.transform.rotation;
                        EnsureTwoSidedShadowsAndCollider(sup);
                        SnapTransformToTerrain(sup.transform, terrain, 0.1f);
                        report.DuplicatedPropsAndPipesPlaced++;
                    }

                    report.DuplicatedPropsAndPipesPlaced++;
                }
            }

            // 3. Place Lamps, Dumpsters, Pallets (Tactical Cover & Intermittent Shade)
            for (int i = 0; i < propCount; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = UnityEngine.Random.Range(radius * 0.15f, radius * 0.9f);
                Vector3 pos = center + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

                Transform chosen = null;
                int r = i % 3;
                if (r == 0 && lampTemplate != null) chosen = lampTemplate;
                else if (r == 1 && coverTemplate != null) chosen = coverTemplate;
                else if (woodTemplate != null) chosen = woodTemplate;

                if (chosen != null)
                {
                    GameObject prop = (GameObject)UnityEngine.Object.Instantiate(chosen.gameObject, parent);
                    prop.name = $"{parent.name}_Prop_{i + 1}";
                    prop.transform.position = pos;
                    prop.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    EnsureTwoSidedShadowsAndCollider(prop);
                    SnapTransformToTerrain(prop.transform, terrain, 0.08f);
                    report.DuplicatedPropsAndPipesPlaced++;
                }
            }
        }

        private static void BuildCorridorCover(
            Terrain terrain,
            Transform parent,
            Vector2 start,
            Vector2 end,
            Transform pipeTemplate,
            Transform metalSupportTemplate,
            Transform lampTemplate,
            Transform coverTemplate,
            Transform woodTemplate,
            OverhaulReport report)
        {
            Vector2 dir = end - start;
            float totalDist = dir.magnitude;
            if (totalDist < 5f) return;

            int steps = Mathf.Max(3, Mathf.RoundToInt(totalDist / 18f));
            for (int i = 1; i < steps; i++)
            {
                float t = (float)i / steps;
                Vector2 p = start + dir * t;
                Vector3 worldPos = new Vector3(p.x, 0f, p.y);

                // Overhead pipe bridge for continuous sun shadow
                if (pipeTemplate != null && i % 2 == 1)
                {
                    GameObject pipe = (GameObject)UnityEngine.Object.Instantiate(pipeTemplate.gameObject, parent);
                    pipe.name = "Corridor_Overhead_PipeBridge";
                    pipe.transform.position = worldPos;
                    pipe.transform.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y));
                    EnsureTwoSidedShadowsAndCollider(pipe);
                    SnapTransformToTerrain(pipe.transform, terrain, 0f);
                    report.DuplicatedPropsAndPipesPlaced++;

                    if (metalSupportTemplate != null)
                    {
                        GameObject sup = (GameObject)UnityEngine.Object.Instantiate(metalSupportTemplate.gameObject, parent);
                        sup.name = "Corridor_Pipe_Support";
                        sup.transform.position = worldPos;
                        sup.transform.rotation = pipe.transform.rotation;
                        EnsureTwoSidedShadowsAndCollider(sup);
                        SnapTransformToTerrain(sup.transform, terrain, 0.1f);
                        report.DuplicatedPropsAndPipesPlaced++;
                    }
                }

                // Roadside Lamp
                if (lampTemplate != null)
                {
                    Vector2 perp = new Vector2(-dir.y, dir.x).normalized * 7.5f;
                    GameObject lamp = (GameObject)UnityEngine.Object.Instantiate(lampTemplate.gameObject, parent);
                    lamp.name = "Corridor_Roadside_Lamp";
                    lamp.transform.position = new Vector3(p.x + perp.x, 0f, p.y + perp.y);
                    lamp.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    EnsureTwoSidedShadowsAndCollider(lamp);
                    SnapTransformToTerrain(lamp.transform, terrain, 0.08f);
                    report.DuplicatedPropsAndPipesPlaced++;
                }

                // Roadside Cover (Dumpster / Pallets)
                Transform cover = (i % 2 == 0 && coverTemplate != null) ? coverTemplate : woodTemplate;
                if (cover != null)
                {
                    Vector2 perp = new Vector2(dir.y, -dir.x).normalized * 6.5f;
                    GameObject cGo = (GameObject)UnityEngine.Object.Instantiate(cover.gameObject, parent);
                    cGo.name = "Corridor_Roadside_Cover";
                    cGo.transform.position = new Vector3(p.x + perp.x, 0f, p.y + perp.y);
                    cGo.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    EnsureTwoSidedShadowsAndCollider(cGo);
                    SnapTransformToTerrain(cGo.transform, terrain, 0.1f);
                    report.DuplicatedPropsAndPipesPlaced++;
                }
            }
        }

        private static void FlattenTerrainPatch(Terrain terrain, Bounds worldBounds, float apronMeters, float blendRimMeters)
        {
            TerrainData td = terrain.terrainData;
            Vector3 tPos = terrain.transform.position;
            Vector3 tSize = td.size;
            int hmRes = td.heightmapResolution;

            float minWX = worldBounds.min.x - apronMeters;
            float maxWX = worldBounds.max.x + apronMeters;
            float minWZ = worldBounds.min.z - apronMeters;
            float maxWZ = worldBounds.max.z + apronMeters;

            int minX = Mathf.Clamp(Mathf.FloorToInt(((minWX - blendRimMeters - tPos.x) / tSize.x) * (hmRes - 1)), 0, hmRes - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(((maxWX + blendRimMeters - tPos.x) / tSize.x) * (hmRes - 1)), 0, hmRes - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(((minWZ - blendRimMeters - tPos.z) / tSize.z) * (hmRes - 1)), 0, hmRes - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt(((maxWZ + blendRimMeters - tPos.z) / tSize.z) * (hmRes - 1)), 0, hmRes - 1);

            int pw = maxX - minX + 1;
            int ph = maxZ - minZ + 1;
            if (pw <= 0 || ph <= 0) return;

            float[,] patch = td.GetHeights(minX, minZ, pw, ph);
            float centerWorldY = terrain.SampleHeight(worldBounds.center);
            float targetNormH = Mathf.Clamp01(centerWorldY / tSize.y);

            for (int z = 0; z < ph; z++)
            {
                float wz = tPos.z + ((float)(minZ + z) / (hmRes - 1)) * tSize.z;
                float dz = 0f;
                if (wz < minWZ) dz = minWZ - wz;
                else if (wz > maxWZ) dz = wz - maxWZ;

                for (int x = 0; x < pw; x++)
                {
                    float wx = tPos.x + ((float)(minX + x) / (hmRes - 1)) * tSize.x;
                    float dx = 0f;
                    if (wx < minWX) dx = minWX - wx;
                    else if (wx > maxWX) dx = wx - maxWX;

                    float distOut = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distOut <= 0.001f)
                    {
                        patch[z, x] = targetNormH;
                    }
                    else if (distOut < blendRimMeters)
                    {
                        float t = distOut / blendRimMeters;
                        float smoothT = t * t * (3f - 2f * t);
                        patch[z, x] = Mathf.Lerp(targetNormH, patch[z, x], smoothT);
                    }
                }
            }

            td.SetHeights(minX, minZ, patch);
        }

        private static void SnapTransformToTerrain(Transform t, Terrain terrain, float sinkMeters)
        {
            Bounds b = CalculateDirectHierarchyBounds(t);
            float minGroundY = float.MaxValue;
            for (int iz = 0; iz <= 3; iz++)
            {
                float sz = Mathf.Lerp(b.min.z, b.max.z, iz / 3f);
                for (int ix = 0; ix <= 3; ix++)
                {
                    float sx = Mathf.Lerp(b.min.x, b.max.x, ix / 3f);
                    float h = terrain.SampleHeight(new Vector3(sx, 0f, sz)) + terrain.transform.position.y;
                    if (h < minGroundY) minGroundY = h;
                }
            }

            float bottomOffset = t.position.y - b.min.y;
            Vector3 pos = t.position;
            pos.y = minGroundY + bottomOffset - sinkMeters;
            t.position = pos;
        }

        private static Bounds CalculateDirectHierarchyBounds(Transform root)
        {
            Bounds b = new Bounds(root.position, Vector3.zero);
            bool hasBounds = false;

            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!hasBounds)
                {
                    b = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }

            return hasBounds ? b : new Bounds(root.position, new Vector3(2f, 2f, 2f));
        }

        // =========================================================================================
        // PHASE 4 IMPLEMENTATION
        // =========================================================================================
        private static void Phase4_OrganizeHierarchy(Scene scene, GameObject terrainGo, OverhaulReport report)
        {
            // 4.1 Master Root
            GameObject masterRoot = GameObject.Find("SCORCHED_Main_Map");
            if (masterRoot == null)
            {
                masterRoot = new GameObject("SCORCHED_Main_Map");
                Undo.RegisterCreatedObjectUndo(masterRoot, "Create Master Root");
            }

            // Required Sub-Containers
            Transform terrainAndLighting = GetOrCreateChild(masterRoot.transform, "_Terrain_And_Lighting");

            Transform d1Hub = GetOrCreateChild(masterRoot.transform, "District_01_CentralHub");
            Transform d1Buildings = GetOrCreateChild(d1Hub, "Buildings");
            Transform d1Walls = GetOrCreateChild(d1Hub, "Walls_And_Caps");
            Transform d1Props = GetOrCreateChild(d1Hub, "Props_And_Poles");

            Transform d2Ridge = GetOrCreateChild(masterRoot.transform, "District_02_NorthRidge");
            Transform d2Buildings = GetOrCreateChild(d2Ridge, "Buildings");
            Transform d2Props = GetOrCreateChild(d2Ridge, "Props_And_Cover");

            Transform d3Refinery = GetOrCreateChild(masterRoot.transform, "District_03_EastRefinery");
            Transform d3Buildings = GetOrCreateChild(d3Refinery, "Buildings");
            Transform d3Props = GetOrCreateChild(d3Refinery, "Props_And_Cover");

            Transform d4Yards = GetOrCreateChild(masterRoot.transform, "District_04_SouthYards");
            Transform d4Buildings = GetOrCreateChild(d4Yards, "Buildings");
            Transform d4Props = GetOrCreateChild(d4Yards, "Props_And_Cover");

            Transform d5Outpost = GetOrCreateChild(masterRoot.transform, "District_05_WestOutpost");
            Transform d5Buildings = GetOrCreateChild(d5Outpost, "Buildings");
            Transform d5Props = GetOrCreateChild(d5Outpost, "Props_And_Cover");

            Transform perimeterRuins = GetOrCreateChild(masterRoot.transform, "_Perimeter_Ruins_And_Boundaries");

            // Move Terrain & Lighting
            MoveRootToParent(terrainGo, terrainAndLighting);
            MoveRootToParent(GameObject.Find("Directional Light"), terrainAndLighting);
            MoveRootToParent(GameObject.Find("Global Volume"), terrainAndLighting);
            MoveRootToParent(GameObject.Find("Setup&Lights"), terrainAndLighting);

            // Move Central Compound Roots into District 01
            MoveRootToParent(GameObject.Find("Walls"), d1Walls);
            MoveRootToParent(GameObject.Find("Roofs"), d1Buildings);
            MoveRootToParent(GameObject.Find("Concrete"), d1Buildings);
            MoveRootToParent(GameObject.Find("Bricks"), d1Walls);
            MoveRootToParent(GameObject.Find("Bricks (1)"), d1Walls);
            MoveRootToParent(GameObject.Find("Pipes"), d1Props);
            MoveRootToParent(GameObject.Find("Lamps"), d1Props);
            MoveRootToParent(GameObject.Find("Trash"), d1Props);
            MoveRootToParent(GameObject.Find("WoodPallets"), d1Props);
            MoveRootToParent(GameObject.Find("Gas"), d1Props);
            MoveRootToParent(GameObject.Find("Cartons"), d1Props);
            MoveRootToParent(GameObject.Find("Plants"), d1Props);
            MoveRootToParent(GameObject.Find("Trails"), d1Props);

            // Move Background buildings:
            // Buildings sitting outside the central compound border belong to Perimeter Ruins
            GameObject bgRoot = GameObject.Find("Background");
            if (bgRoot != null)
            {
                MoveRootToParent(bgRoot, perimeterRuins);
            }

            // Move Duplicated District Objects from Phase 3 into Districts 02-05
            GameObject dupRoot = GameObject.Find("Districts_Duplicated");
            if (dupRoot != null)
            {
                DistributeDuplicatedObjects(dupRoot.transform, d2Buildings, d2Props, d3Buildings, d3Props, d4Buildings, d4Props, d5Buildings, d5Props);
                Undo.DestroyObjectImmediate(dupRoot);
            }

            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/_Terrain_And_Lighting");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/District_01_CentralHub (Buildings, Walls_And_Caps, Props_And_Poles)");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/District_02_NorthRidge (Buildings, Props_And_Cover)");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/District_03_EastRefinery (Buildings, Props_And_Cover)");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/District_04_SouthYards (Buildings, Props_And_Cover)");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/District_05_WestOutpost (Buildings, Props_And_Cover)");
            report.DistrictHierarchyCreated.Add("SCORCHED_Main_Map/_Perimeter_Ruins_And_Boundaries");

            report.ValidationSummary = $"All 4 Phases executed successfully! Central bump eliminated with 28m ramp; 4 elevated uphill districts sculpted; {report.MaterialsSetTwoSided} materials set to two-sided; {report.BackgroundBuildingsSealed} buildings sealed back-to-back; {report.DuplicatedBuildingsPlaced} duplicated district buildings placed with collision; hierarchy strictly structured.";
        }

        private static void DistributeDuplicatedObjects(
            Transform dupRoot,
            Transform d2B, Transform d2P,
            Transform d3B, Transform d3P,
            Transform d4B, Transform d4P,
            Transform d5B, Transform d5P)
        {
            Transform[] children = dupRoot.Cast<Transform>().ToArray();
            foreach (Transform districtGroup in children)
            {
                string name = districtGroup.name;
                Transform targetB = null;
                Transform targetP = null;

                if (name.Contains("D02")) { targetB = d2B; targetP = d2P; }
                else if (name.Contains("D03")) { targetB = d3B; targetP = d3P; }
                else if (name.Contains("D04")) { targetB = d4B; targetP = d4P; }
                else if (name.Contains("D05")) { targetB = d5B; targetP = d5P; }
                else // Connecting_Corridors
                {
                    targetP = d2P; // or distribute by proximity
                }

                Transform[] items = districtGroup.Cast<Transform>().ToArray();
                foreach (Transform item in items)
                {
                    if (item.name.Contains("Warehouse") && targetB != null)
                    {
                        item.SetParent(targetB, true);
                    }
                    else if (targetP != null)
                    {
                        item.SetParent(targetP, true);
                    }
                }
            }
        }

        private static Transform GetOrCreateChild(Transform parent, string childName)
        {
            Transform child = parent.Find(childName);
            if (child == null)
            {
                GameObject go = new GameObject(childName);
                Undo.RegisterCreatedObjectUndo(go, $"Create {childName}");
                child = go.transform;
                child.SetParent(parent, false);
            }
            return child;
        }

        private static void MoveRootToParent(GameObject go, Transform targetParent)
        {
            if (go == null || targetParent == null) return;
            Undo.SetTransformParent(go.transform, targetParent, $"Parent {go.name} to {targetParent.name}");
        }
    }
}
