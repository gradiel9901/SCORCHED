#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Transforms 'Assets/Scenes/Level 0 - Test Environment.unity' from a flat plane/terrain
    /// into a multi-tiered post-apocalyptic desert town with sunken streets, uphill plateaus,
    /// mid-tier ridges, rolling dunes, canyon perimeter banking, flush trimmed perimeter walls,
    /// and flat terraced building pads with walkable dirt skirts.
    /// </summary>
    public static class ScorchedTerrainVerticalityBuilder
    {
        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string TerrainDataAssetPath = "Assets/Scenes/Level0_ScorchedTerrainData.asset";
        private const int TargetHeightmapResolution = 513;
        private const float MinTerrainHeightCapacity = 60f;
        private const float InnerPadMarginMeters = 1.0f;
        private const float OuterSkirtMeters = 12.5f;
        private const float ClusterMergeMarginMeters = 1.0f;

        private sealed class BuildingFootprint
        {
            public Transform Transform;
            public Bounds RenderBounds;
            public float YOffsetAboveGround;
            public int CompoundId;
        }

        private sealed class TerraceCompound
        {
            public int Id;
            public Bounds CombinedFootprint;
            public float TerraceHeightMeters;
            public readonly List<BuildingFootprint> Members = new List<BuildingFootprint>();
        }

        [MenuItem("SCORCHED/Sculpt Level 0 Elevated Terrain")]
        public static void SculptLevel0ElevatedTerrainMenu()
        {
            SculptLevel0ElevatedTerrain();
        }

        public static string SculptLevel0ElevatedTerrain()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != TargetScenePath)
            {
                scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Sculpt Level 0 Elevated Terrain & Terrace Buildings");
            int undoGroup = Undo.GetCurrentGroup();

            // Step 1: Locate or create sculptable Terrain (513x513, Y >= 60f)
            Terrain terrain = EnsureSculptableTerrain(scene, out bool createdNewTerrain);
            TerrainData td = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = td.size;
            int hmRes = td.heightmapResolution;

            // Collect all active buildings/props (excluding terrain, _Hidden, _Walls, Player)
            List<BuildingFootprint> buildings = CollectBuildings(scene, terrain);

            // Step 2: Generate base 513x513 macro-elevation + dunes + outer basin banking
            float[,] baseHeightsMeters = GenerateBaseElevationMeters(terrainPos, terrainSize, hmRes);

            // Step 3: Cluster overlapping buildings into unified compounds & carve terraced pads
            List<TerraceCompound> compounds = BuildTerraceCompounds(buildings);
            ComputeCompoundTerraceHeights(compounds, baseHeightsMeters, terrainPos, terrainSize, hmRes);

            float[,] finalHeightsNormalized = ApplyBuildingTerracesAndSkirts(
                baseHeightsMeters, compounds, terrainPos, terrainSize, hmRes);

            Undo.RecordObject(td, "Apply Elevated Terrain Heightmap");
            td.SetHeights(0, 0, finalHeightsNormalized);
            td.SyncHeightmap();

            TerrainCollider terrainCollider = terrain.GetComponent<TerrainCollider>();
            if (terrainCollider != null)
            {
                Undo.RecordObject(terrainCollider, "Refresh TerrainCollider");
                terrainCollider.terrainData = null;
                terrainCollider.terrainData = td;
            }
            Physics.SyncTransforms();

            // Refresh terrain splatmap gently so outer dunes/steep slopes read as sand/cracked earth
            UpdateSplatmapForSlopeAndDunes(td, finalHeightsNormalized, terrainSize);

            // Snap all buildings & canopies onto their new terrace heights
            SnapBuildingsToTerrain(buildings, compounds, terrain);

            // Trim protruding outer perimeter wall corners and snap wall segments along terrain rim
            int wallSegmentsKept = 0;
            int wallSegmentsTrimmed = 0;
            TrimAndSnapOuterWalls(scene, terrain, ref wallSegmentsKept, ref wallSegmentsTrimmed);

            // Snap scene Player onto terrain so it never sits underground in the sunken valley
            SnapScenePlayerToTerrain(scene, terrain);

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, TargetScenePath);

            // Diagnostic summary
            float minM = float.MaxValue, maxM = float.MinValue;
            for (int z = 0; z < hmRes; z++)
            {
                for (int x = 0; x < hmRes; x++)
                {
                    float m = finalHeightsNormalized[z, x] * terrainSize.y;
                    if (m < minM) minM = m;
                    if (m > maxM) maxM = m;
                }
            }

            float minBuildingY = buildings.Count > 0 ? buildings.Min(b => b.Transform.position.y) : 0f;
            float maxBuildingY = buildings.Count > 0 ? buildings.Max(b => b.Transform.position.y) : 0f;

            var sb = new StringBuilder();
            sb.AppendLine($"[ScorchedTerrainVerticalityBuilder] Completed on '{TargetScenePath}' (saved={saved}, createdNewTerrain={createdNewTerrain}).");
            sb.AppendLine($"Terrain: '{terrain.name}' res={hmRes}x{hmRes}, size={terrainSize}, heightRange=[{minM:F2}m, {maxM:F2}m]");
            sb.AppendLine($"Buildings snapped: {buildings.Count} across {compounds.Count} terrace compounds, Y range=[{minBuildingY:F2}m, {maxBuildingY:F2}m]");
            sb.AppendLine($"Outer walls: {wallSegmentsKept} segments snapped/kept, {wallSegmentsTrimmed} protruding corner segments removed.");
            string result = sb.ToString();
            Debug.Log(result);
            return result;
        }

        private static Terrain EnsureSculptableTerrain(Scene scene, out bool createdNewTerrain)
        {
            createdNewTerrain = false;
            Terrain existing = UnityEngine.Object.FindAnyObjectByType<Terrain>();
            if (existing != null && existing.terrainData != null)
            {
                TerrainData sourceTd = existing.terrainData;
                TerrainData dedicatedTd = LoadOrCloneTerrainData(sourceTd);

                if (dedicatedTd.heightmapResolution != TargetHeightmapResolution)
                {
                    dedicatedTd.heightmapResolution = TargetHeightmapResolution;
                }

                Vector3 size = dedicatedTd.size;
                if (size.y < MinTerrainHeightCapacity)
                {
                    dedicatedTd.size = new Vector3(size.x, MinTerrainHeightCapacity, size.z);
                }

                Undo.RecordObject(existing, "Assign Level 0 TerrainData");
                existing.terrainData = dedicatedTd;

                Material terrainLit = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.unity.render-pipelines.universal/Runtime/Materials/TerrainLit.mat");
                if (terrainLit != null && existing.materialTemplate != terrainLit)
                {
                    Undo.RecordObject(existing, "Ensure URP Terrain Material");
                    existing.materialTemplate = terrainLit;
                }

                TerrainCollider col = existing.GetComponent<TerrainCollider>();
                if (col == null) col = Undo.AddComponent<TerrainCollider>(existing.gameObject);
                Undo.RecordObject(col, "Assign Level 0 TerrainCollider");
                col.terrainData = dedicatedTd;
                return existing;
            }

            // Fallback: convert flat MeshRenderer ground into a 513x513 Unity Terrain
            GameObject flatGround = FindFlatGroundObject(scene);
            Bounds groundBounds = flatGround != null && flatGround.TryGetComponent<Renderer>(out var rend)
                ? rend.bounds
                : new Bounds(Vector3.zero, new Vector3(150f, 0f, 180f));

            TerrainData td = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataAssetPath);
            if (td == null)
            {
                td = new TerrainData();
                AssetDatabase.CreateAsset(td, TerrainDataAssetPath);
            }
            td.heightmapResolution = TargetHeightmapResolution;
            td.size = new Vector3(Mathf.Max(50f, groundBounds.size.x), MinTerrainHeightCapacity, Mathf.Max(50f, groundBounds.size.z));

            if (flatGround != null && flatGround.TryGetComponent<Renderer>(out var groundRend) && groundRend.sharedMaterial != null)
            {
                Texture2D diff = groundRend.sharedMaterial.mainTexture as Texture2D;
                TerrainLayer layer = new TerrainLayer
                {
                    name = "Layer_ConvertedGround",
                    diffuseTexture = diff,
                    tileSize = new Vector2(8f, 8f)
                };
                string layerPath = "Assets/Scenes/Level0_GroundLayer.terrainlayer";
                AssetDatabase.CreateAsset(layer, layerPath);
                td.terrainLayers = new[] { layer };
                Undo.RecordObject(flatGround, "Disable Old Flat Ground");
                flatGround.SetActive(false);
            }

            GameObject terrainGo = Terrain.CreateTerrainGameObject(td);
            terrainGo.name = "Level0_Desert_Terrain";
            Undo.RegisterCreatedObjectUndo(terrainGo, "Create Level 0 Terrain");
            terrainGo.transform.position = new Vector3(
                groundBounds.center.x - td.size.x * 0.5f,
                groundBounds.min.y,
                groundBounds.center.z - td.size.z * 0.5f);

            createdNewTerrain = true;
            return terrainGo.GetComponent<Terrain>();
        }

        private static TerrainData LoadOrCloneTerrainData(TerrainData sourceTd)
        {
            string currentPath = AssetDatabase.GetAssetPath(sourceTd);
            if (currentPath == TerrainDataAssetPath)
            {
                return sourceTd;
            }

            TerrainData existingTarget = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataAssetPath);
            if (existingTarget != null)
            {
                return existingTarget;
            }

            TerrainData clone = UnityEngine.Object.Instantiate(sourceTd);
            clone.name = "Level0_ScorchedTerrainData";
            AssetDatabase.CreateAsset(clone, TerrainDataAssetPath);
            AssetDatabase.SaveAssets();
            return clone;
        }

        private static GameObject FindFlatGroundObject(Scene scene)
        {
            string[] candidateNames = { "Ground", "Floor", "SandGround", "TerrainPlane", "Plane" };
            foreach (var r in scene.GetRootGameObjects())
            {
                foreach (var tr in r.GetComponentsInChildren<Transform>(true))
                {
                    if (candidateNames.Any(c => tr.name.Equals(c, StringComparison.OrdinalIgnoreCase)) &&
                        tr.GetComponent<Renderer>() != null)
                    {
                        return tr.gameObject;
                    }
                }
            }
            return null;
        }

        private static List<BuildingFootprint> CollectBuildings(Scene scene, Terrain terrain)
        {
            var results = new List<BuildingFootprint>();
            var candidateContainers = new List<Transform>();

            GameObject townMap = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "SCORCHED_Town_Map");
            if (townMap != null)
            {
                for (int i = 0; i < townMap.transform.childCount; i++)
                {
                    Transform grp = townMap.transform.GetChild(i);
                    string n = grp.name;
                    if (!grp.gameObject.activeInHierarchy) continue;
                    if (n.StartsWith("_Hidden", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("_Terrain", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("_Walls", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    candidateContainers.Add(grp);
                }

                foreach (Transform grp in candidateContainers)
                {
                    for (int i = 0; i < grp.childCount; i++)
                    {
                        Transform b = grp.GetChild(i);
                        if (!b.gameObject.activeInHierarchy) continue;
                        AddBuildingFootprint(b, terrain, results);
                    }
                }
            }
            else
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (!root.activeInHierarchy) continue;
                    if (root == terrain.gameObject || root.name == "Player" ||
                        root.name == "Directional Light" || root.name == "Global Volume" ||
                        root.name.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
                        root.name.Contains("Wall", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    AddBuildingFootprint(root.transform, terrain, results);
                }
            }

            return results;
        }

        private static void AddBuildingFootprint(Transform t, Terrain terrain, List<BuildingFootprint> results)
        {
            Renderer[] rends = t.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) return;

            Bounds wb = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++)
            {
                wb.Encapsulate(rends[i].bounds);
            }

            // Sample existing ground under the building before editing heightmap so repeated runs are idempotent
            float currentGroundY = terrain.SampleHeight(new Vector3(wb.center.x, 0f, wb.center.z)) + terrain.transform.position.y;
            float offsetAboveGround = t.position.y - currentGroundY;

            results.Add(new BuildingFootprint
            {
                Transform = t,
                RenderBounds = wb,
                YOffsetAboveGround = offsetAboveGround,
                CompoundId = -1
            });
        }

        private static float[,] GenerateBaseElevationMeters(Vector3 terrainPos, Vector3 terrainSize, int hmRes)
        {
            var heightsMeters = new float[hmRes, hmRes];

            for (int z = 0; z < hmRes; z++)
            {
                float v = (float)z / (hmRes - 1);
                float nz = v * 2f - 1f; // -1 (South) to +1 (North)
                float worldZ = terrainPos.z + v * terrainSize.z;

                for (int x = 0; x < hmRes; x++)
                {
                    float u = (float)x / (hmRes - 1);
                    float nx = u * 2f - 1f; // -1 (West) to +1 (East)
                    float worldX = terrainPos.x + u * terrainSize.x;

                    // 1. Sunken Main Street corridor (|nx| < 0.15): valley floor ~2.0m sloping gently S -> N
                    // Center road runs cleanly along X = +1.8m (between West and East building blocks)
                    float nxRoad = (worldX - 1.8f) / (terrainSize.x * 0.5f);
                    float southToNorthSlope = Mathf.SmoothStep(0f, 1f, (nz + 0.85f) / 1.70f);
                    float valleyFloor = 2.0f + southToNorthSlope * 1.4f;

                    // Wide, walkable directional quadrant ramps (~26m transition span so slopes stay <= 30 deg)
                    float wWest = Mathf.SmoothStep(0f, 1f, (-nxRoad - 0.09f) / 0.34f);
                    float wEast = Mathf.SmoothStep(0f, 1f, (nxRoad - 0.09f) / 0.34f);
                    float wNorth = Mathf.SmoothStep(0f, 1f, (nz - 0.02f) / 0.22f);
                    float wSouth = Mathf.SmoothStep(0f, 1f, (-nz - 0.02f) / 0.22f);

                    float wNW = wWest * wNorth; // High ground plateau (+10m to +14m)
                    float wSE = wEast * wSouth; // High ground plateau (+10m to +14m)
                    float wNE = wEast * wNorth; // Mid-tier ridge (+5m to +8m)
                    float wSW = wWest * wSouth; // Mid-tier ridge (+5m to +8m)

                    // Subtle variation across each plateau so buildings don't sit identically flat globally
                    float plateauTilt = Mathf.PerlinNoise(worldX * 0.014f + 4.1f, worldZ * 0.014f + 8.3f) * 2.0f;
                    float nwTarget = 12.4f + plateauTilt;        // ~12.4m - 14.4m
                    float seTarget = 12.4f + plateauTilt;        // ~12.4m - 14.4m
                    float neTarget = 6.2f + plateauTilt * 0.80f; // ~6.2m - 7.8m
                    float swTarget = 6.0f + plateauTilt * 0.80f; // ~6.0m - 7.6m

                    // Gentle E-W cross-canyon saddle between NW/SW and NE/SE
                    float sideWeight = Mathf.Max(wWest, wEast);
                    float crossSaddle = Mathf.Lerp(valleyFloor, valleyFloor + 1.6f, sideWeight);

                    float macroMeters = crossSaddle
                        + wNW * (nwTarget - crossSaddle)
                        + wSE * (seTarget - crossSaddle)
                        + wNE * (neTarget - crossSaddle)
                        + wSW * (swTarget - crossSaddle);

                    // 2. Domain-warped 3-octave rolling desert dunes & tactical berms
                    float warpX = (Mathf.PerlinNoise(worldX * 0.022f + 17.3f, worldZ * 0.022f + 5.9f) - 0.5f) * 14f;
                    float warpZ = (Mathf.PerlinNoise(worldX * 0.022f + 41.1f, worldZ * 0.022f + 23.7f) - 0.5f) * 14f;
                    float wx = worldX + warpX;
                    float wz = worldZ + warpZ;

                    float oct1 = Mathf.PerlinNoise(wx * 0.032f + 10.5f, wz * 0.032f + 3.2f);
                    float oct2 = Mathf.PerlinNoise(wx * 0.075f + 22.8f, wz * 0.075f + 14.4f);
                    float oct3 = Mathf.PerlinNoise(wx * 0.160f + 51.3f, wz * 0.160f + 37.9f);
                    float fbmSigned = (oct1 * 0.62f + oct2 * 0.27f + oct3 * 0.11f) - 0.45f; // slightly positive dune bias

                    // Keep main street walkable with tactical cover berms (~0.8m), full 2.6m-3.5m dunes in open areas
                    float corridorSuppress = Mathf.Lerp(0.30f, 1.0f, Mathf.SmoothStep(0.06f, 0.24f, Mathf.Abs(nxRoad)));
                    float duneMeters = fbmSigned * 3.8f * corridorSuppress;
                    duneMeters += Mathf.Abs(oct1 - 0.5f) * 1.6f * corridorSuppress;

                    float hMeters = Mathf.Max(0.4f, macroMeters + duneMeters);

                    // 3. Outer 12% border canyon / desert dune banking (+8m to +15m)
                    float edgeFactor = Mathf.Max(Mathf.Abs(nx), Mathf.Abs(nz));
                    if (edgeFactor > 0.85f)
                    {
                        float rimT = Mathf.SmoothStep(0.85f, 1.00f, edgeFactor);
                        float duneWave = Mathf.PerlinNoise(worldX * 0.040f + 13.7f, worldZ * 0.040f + 29.1f);
                        float duneDetail = Mathf.PerlinNoise(worldX * 0.095f + 7.2f, worldZ * 0.095f + 19.4f);
                        float rimDuneMeters = 8.5f + duneWave * 5.0f + duneDetail * 1.5f; // ~8.5m to 15.0m
                        float targetRim = Mathf.Max(hMeters, rimDuneMeters);
                        hMeters = Mathf.Lerp(hMeters, targetRim, rimT);
                    }

                    heightsMeters[z, x] = hMeters;
                }
            }

            return heightsMeters;
        }

        private static List<TerraceCompound> BuildTerraceCompounds(List<BuildingFootprint> buildings)
        {
            var compounds = new List<TerraceCompound>();
            int n = buildings.Count;
            var visited = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                visited[i] = true;

                var comp = new TerraceCompound { Id = compounds.Count };
                var queue = new Queue<int>();
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    BuildingFootprint bf = buildings[cur];
                    bf.CompoundId = comp.Id;
                    comp.Members.Add(bf);

                    if (comp.Members.Count == 1) comp.CombinedFootprint = bf.RenderBounds;
                    else comp.CombinedFootprint.Encapsulate(bf.RenderBounds);

                    Bounds expandedA = ExpandXZ(bf.RenderBounds, ClusterMergeMarginMeters);
                    for (int j = 0; j < n; j++)
                    {
                        if (visited[j]) continue;
                        Bounds expandedB = ExpandXZ(buildings[j].RenderBounds, ClusterMergeMarginMeters);
                        if (IntersectsXZ(expandedA, expandedB))
                        {
                            visited[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }

                compounds.Add(comp);
            }

            return compounds;
        }

        private static void ComputeCompoundTerraceHeights(
            List<TerraceCompound> compounds,
            float[,] baseHeightsMeters,
            Vector3 terrainPos,
            Vector3 terrainSize,
            int hmRes)
        {
            foreach (TerraceCompound comp in compounds)
            {
                // Average macro height across compound members so connected buildings sit level at natural hill tier
                float sumH = 0f;
                foreach (BuildingFootprint m in comp.Members)
                {
                    Vector3 c = m.RenderBounds.center;
                    sumH += SampleMetersBilinear(baseHeightsMeters, terrainPos, terrainSize, hmRes, c.x, c.z);
                }
                comp.TerraceHeightMeters = sumH / Mathf.Max(1, comp.Members.Count);
            }

            // Enforce walkable height-step limits between adjacent compounds so nearby pads never form cliffs
            const float maxInterPadTangent = 0.65f; // ~33 deg max slope between adjacent locked pads
            for (int iter = 0; iter < 24; iter++)
            {
                for (int i = 0; i < compounds.Count; i++)
                {
                    for (int j = i + 1; j < compounds.Count; j++)
                    {
                        float bGap = DistanceBetweenCompoundsXZ(compounds[i], compounds[j]);
                        float padGap = Mathf.Max(0.25f, bGap - 2f * InnerPadMarginMeters);
                        float maxDiff = padGap * maxInterPadTangent;
                        float diff = compounds[i].TerraceHeightMeters - compounds[j].TerraceHeightMeters;
                        float absDiff = Mathf.Abs(diff);
                        if (absDiff <= maxDiff) continue;

                        float excess = (absDiff - maxDiff) * 0.5f;
                        float sign = Mathf.Sign(diff);
                        compounds[i].TerraceHeightMeters -= sign * excess;
                        compounds[j].TerraceHeightMeters += sign * excess;
                    }
                }
            }
        }

        private static float[,] ApplyBuildingTerracesAndSkirts(
            float[,] baseHeightsMeters,
            List<TerraceCompound> compounds,
            Vector3 terrainPos,
            Vector3 terrainSize,
            int hmRes)
        {
            var metersGrid = new float[hmRes, hmRes];
            var lockedMask = new bool[hmRes, hmRes];

            for (int z = 0; z < hmRes; z++)
            {
                float worldZ = terrainPos.z + ((float)z / (hmRes - 1)) * terrainSize.z;

                for (int x = 0; x < hmRes; x++)
                {
                    float worldX = terrainPos.x + ((float)x / (hmRes - 1)) * terrainSize.x;
                    float baseH = baseHeightsMeters[z, x];

                    float totalWeight = 0f;
                    float weightedTerraceH = 0f;
                    float maxBlend = 0f;
                    bool lockedInner = false;
                    float lockedHeight = baseH;

                    // Continuous Shepard blend (s / (1 - s + eps)) eliminates vertical step discontinuities at pad edges
                    foreach (TerraceCompound comp in compounds)
                    {
                        float minDist = float.MaxValue;
                        for (int m = 0; m < comp.Members.Count; m++)
                        {
                            float d = DistanceXZToBounds(worldX, worldZ, comp.Members[m].RenderBounds);
                            if (d < minDist) minDist = d;
                        }

                        if (minDist <= InnerPadMarginMeters)
                        {
                            lockedInner = true;
                            lockedHeight = comp.TerraceHeightMeters;
                            break;
                        }

                        if (minDist < OuterSkirtMeters)
                        {
                            float t = (minDist - InnerPadMarginMeters) / (OuterSkirtMeters - InnerPadMarginMeters);
                            float s = 1f - Mathf.SmoothStep(0f, 1f, t);
                            float w = s / (1f - s + 0.0005f);
                            weightedTerraceH += comp.TerraceHeightMeters * w;
                            totalWeight += w;
                            if (s > maxBlend) maxBlend = s;
                        }
                    }

                    if (lockedInner)
                    {
                        metersGrid[z, x] = lockedHeight;
                        lockedMask[z, x] = true;
                    }
                    else if (totalWeight > 0.0001f)
                    {
                        float avgTerrace = weightedTerraceH / totalWeight;
                        metersGrid[z, x] = Mathf.Lerp(baseH, avgTerrace, maxBlend);
                    }
                    else
                    {
                        metersGrid[z, x] = baseH;
                    }
                }
            }

            // Soften non-building cells with diffusion + thermal talus angle clamping (~30 deg max) while keeping inner pads locked flat
            ApplyTalusReposeSmoothing(metersGrid, lockedMask, terrainSize, hmRes, maxSlopeDeg: 30.0f, talusPasses: 64, smoothPasses: 6);

            var outNormalized = new float[hmRes, hmRes];
            float invMaxH = 1f / terrainSize.y;
            for (int z = 0; z < hmRes; z++)
            {
                for (int x = 0; x < hmRes; x++)
                {
                    outNormalized[z, x] = Mathf.Clamp01(metersGrid[z, x] * invMaxH);
                }
            }

            return outNormalized;
        }

        private static void UpdateSplatmapForSlopeAndDunes(TerrainData td, float[,] heightsNormalized, Vector3 terrainSize)
        {
            if (td.terrainLayers == null || td.terrainLayers.Length < 3) return;

            int alphaRes = td.alphamapResolution;
            float[,,] alphamaps = td.GetAlphamaps(0, 0, alphaRes, alphaRes);
            int layers = td.terrainLayers.Length;

            for (int y = 0; y < alphaRes; y++)
            {
                float v = (float)y / (alphaRes - 1);
                float nz = v * 2f - 1f;

                for (int x = 0; x < alphaRes; x++)
                {
                    float u = (float)x / (alphaRes - 1);
                    float nx = u * 2f - 1f;

                    float slopeDeg = td.GetSteepness(u, v);
                    float edgeFactor = Mathf.Max(Mathf.Abs(nx), Mathf.Abs(nz));
                    float rimT = Mathf.SmoothStep(0.82f, 0.95f, edgeFactor);
                    float slopeT = Mathf.SmoothStep(4f, 12f, slopeDeg);

                    float wSand = alphamaps[y, x, 0];
                    float wCracked = alphamaps[y, x, 1];
                    float wConcrete = alphamaps[y, x, 2];

                    // Steep hillside ramps and outer basin dunes blend cleanly to desert sand (avoid stretched dark mud)
                    float hillOverride = Mathf.Max(rimT, slopeT * 0.90f);
                    wConcrete *= (1f - hillOverride);
                    wSand = Mathf.Lerp(wSand, 0.92f, hillOverride);
                    wCracked = Mathf.Lerp(wCracked, 0.08f, hillOverride);

                    float sum = wSand + wCracked + wConcrete;
                    if (sum <= 0.0001f) { wSand = 1f; sum = 1f; }

                    alphamaps[y, x, 0] = wSand / sum;
                    alphamaps[y, x, 1] = wCracked / sum;
                    alphamaps[y, x, 2] = wConcrete / sum;
                    for (int l = 3; l < layers; l++) alphamaps[y, x, l] = 0f;
                }
            }

            td.SetAlphamaps(0, 0, alphamaps);
        }

        private static void SnapBuildingsToTerrain(
            List<BuildingFootprint> buildings,
            List<TerraceCompound> compounds,
            Terrain terrain)
        {
            foreach (BuildingFootprint bf in buildings)
            {
                Undo.RecordObject(bf.Transform, "Snap Building to Elevated Terrace");
                float groundY = terrain.SampleHeight(bf.RenderBounds.center) + terrain.transform.position.y;
                Vector3 pos = bf.Transform.position;
                bf.Transform.position = new Vector3(pos.x, groundY + bf.YOffsetAboveGround, pos.z);
            }
            Physics.SyncTransforms();
        }

        private static void TrimAndSnapOuterWalls(Scene scene, Terrain terrain, ref int keptCount, ref int trimmedCount)
        {
            GameObject wallsRoot = GameObject.Find("_Walls");
            if (wallsRoot == null) return;

            Transform northWall = wallsRoot.transform.Find("_North_Wall");
            Transform southWall = wallsRoot.transform.Find("_South_Wall");
            Transform eastWall = wallsRoot.transform.Find("_East_Wall");
            Transform westWall = wallsRoot.transform.Find("_West_Wall");
            if (northWall == null || southWall == null || eastWall == null || westWall == null) return;

            // Compute exact enclosed rectangle defined by the 4 wall centerlines
            float wallZNorth = northWall.position.z;
            float wallZSouth = southWall.position.z;
            float wallXWestLine = Mathf.Min(eastWall.position.x, westWall.position.x);
            float wallXEastLine = Mathf.Max(eastWall.position.x, westWall.position.x);
            float minZ = Mathf.Min(wallZSouth, wallZNorth);
            float maxZ = Mathf.Max(wallZSouth, wallZNorth);
            float halfThick = 0.32f;

            // Trim & snap North/South walls cleanly inside [wallXWestLine - halfThick, wallXEastLine + halfThick]
            ProcessWallLine(northWall, true, wallXWestLine - halfThick, wallXEastLine + halfThick, terrain, ref keptCount, ref trimmedCount);
            ProcessWallLine(southWall, true, wallXWestLine - halfThick, wallXEastLine + halfThick, terrain, ref keptCount, ref trimmedCount);

            // Trim & snap East/West walls cleanly inside [minZ - halfThick, maxZ + halfThick]
            ProcessWallLine(eastWall, false, minZ - halfThick, maxZ + halfThick, terrain, ref keptCount, ref trimmedCount);
            ProcessWallLine(westWall, false, minZ - halfThick, maxZ + halfThick, terrain, ref keptCount, ref trimmedCount);
        }

        private static void ProcessWallLine(
            Transform wallParent,
            bool alongX,
            float clipMin,
            float clipMax,
            Terrain terrain,
            ref int keptCount,
            ref int trimmedCount)
        {
            var children = new List<Transform>();
            for (int i = 0; i < wallParent.childCount; i++)
                children.Add(wallParent.GetChild(i));

            foreach (Transform seg in children)
            {
                Renderer[] rends = seg.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0) continue;

                Bounds b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

                float segMin = alongX ? b.min.x : b.min.z;
                float segMax = alongX ? b.max.x : b.max.z;
                float segLen = segMax - segMin;

                // Remove protruding corner segments completely outside the flush box
                if (segMax <= clipMin + 0.05f || segMin >= clipMax - 0.05f || segLen <= 0.01f)
                {
                    Undo.DestroyObjectImmediate(seg.gameObject);
                    trimmedCount++;
                    continue;
                }

                // Scale & shift edge-straddling segment so it terminates flush at the corner
                if (segMin < clipMin || segMax > clipMax)
                {
                    float newMin = Mathf.Max(segMin, clipMin);
                    float newMax = Mathf.Min(segMax, clipMax);
                    float ratio = Mathf.Clamp01((newMax - newMin) / segLen);
                    float targetCenterAxis = (newMin + newMax) * 0.5f;

                    Undo.RecordObject(seg, "Trim Corner Wall Segment");
                    // Wall segments may have end pivots and +/-90 rotation; scale first, then align bounds center
                    Vector3 worldAxis = alongX ? Vector3.right : Vector3.forward;
                    Vector3 localScale = seg.localScale;
                    if (Mathf.Abs(Vector3.Dot(seg.right, worldAxis)) > 0.7f)
                        localScale.x *= ratio;
                    else
                        localScale.z *= ratio;
                    seg.localScale = localScale;
                    Physics.SyncTransforms();

                    Renderer[] postScaleRends = seg.GetComponentsInChildren<Renderer>(true);
                    Bounds postB = postScaleRends[0].bounds;
                    for (int i = 1; i < postScaleRends.Length; i++) postB.Encapsulate(postScaleRends[i].bounds);
                    float postCenterAxis = alongX ? postB.center.x : postB.center.z;

                    Vector3 delta = alongX
                        ? new Vector3(targetCenterAxis - postCenterAxis, 0f, 0f)
                        : new Vector3(0f, 0f, targetCenterAxis - postCenterAxis);
                    seg.position += delta;
                }

                // Snap each wall segment onto the rim terrain with 0.55m embed so slanted dunes never leave gaps
                Renderer[] updatedRends = seg.GetComponentsInChildren<Renderer>(true);
                Bounds ub = updatedRends[0].bounds;
                for (int i = 1; i < updatedRends.Length; i++) ub.Encapsulate(updatedRends[i].bounds);

                float groundY = terrain.SampleHeight(ub.center) + terrain.transform.position.y;
                float bottomOffset = seg.position.y - ub.min.y;
                Undo.RecordObject(seg, "Snap Wall Segment to Terrain Border");
                seg.position = new Vector3(seg.position.x, (groundY - 0.55f) + bottomOffset, seg.position.z);
                keptCount++;
            }
        }

        private static void SnapScenePlayerToTerrain(Scene scene, Terrain terrain)
        {
            GameObject player = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Player");
            if (player == null) return;

            Undo.RecordObject(player.transform, "Snap Player to Elevated Valley Floor");
            Vector3 pos = player.transform.position;
            float groundY = terrain.SampleHeight(pos) + terrain.transform.position.y;
            player.transform.position = new Vector3(pos.x, groundY + 0.08f, pos.z);
        }

        private static Bounds ExpandXZ(Bounds b, float margin)
        {
            return new Bounds(b.center, new Vector3(b.size.x + margin * 2f, b.size.y, b.size.z + margin * 2f));
        }

        private static bool IntersectsXZ(Bounds a, Bounds b)
        {
            return a.min.x <= b.max.x && a.max.x >= b.min.x &&
                   a.min.z <= b.max.z && a.max.z >= b.min.z;
        }

        private static float DistanceXZToBounds(float x, float z, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(b.min.x - x, x - b.max.x));
            float dz = Mathf.Max(0f, Mathf.Max(b.min.z - z, z - b.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float DistanceBetweenBoundsXZ(Bounds a, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
            float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float DistanceBetweenCompoundsXZ(TerraceCompound a, TerraceCompound b)
        {
            float minDist = float.MaxValue;
            for (int i = 0; i < a.Members.Count; i++)
            {
                for (int j = 0; j < b.Members.Count; j++)
                {
                    float d = DistanceBetweenBoundsXZ(a.Members[i].RenderBounds, b.Members[j].RenderBounds);
                    if (d < minDist) minDist = d;
                }
            }
            return minDist;
        }

        private static void ApplyTalusReposeSmoothing(
            float[,] metersGrid,
            bool[,] lockedMask,
            Vector3 terrainSize,
            int hmRes,
            float maxSlopeDeg,
            int talusPasses,
            int smoothPasses)
        {
            float stepX = terrainSize.x / (hmRes - 1);
            float stepZ = terrainSize.z / (hmRes - 1);
            float stepDiag = Mathf.Sqrt(stepX * stepX + stepZ * stepZ);
            float maxTan = Mathf.Tan(maxSlopeDeg * Mathf.Deg2Rad);

            float maxDeltaX = stepX * maxTan;
            float maxDeltaZ = stepZ * maxTan;
            float maxDeltaDiag = stepDiag * maxTan;

            var buf = new float[hmRes, hmRes];

            // Pre-smooth transition skirts gently
            for (int pass = 0; pass < 4; pass++)
            {
                Array.Copy(metersGrid, buf, metersGrid.Length);
                for (int z = 1; z < hmRes - 1; z++)
                {
                    for (int x = 1; x < hmRes - 1; x++)
                    {
                        if (lockedMask[z, x]) continue;
                        metersGrid[z, x] = (
                            buf[z - 1, x] + buf[z + 1, x] +
                            buf[z, x - 1] + buf[z, x + 1] +
                            buf[z, x] * 2f) / 6f;
                    }
                }
            }

            // Talus erosion: pile sand cleanly at <= maxSlopeDeg below steep plateaus/pads
            for (int pass = 0; pass < talusPasses; pass++)
            {
                Array.Copy(metersGrid, buf, metersGrid.Length);
                for (int z = 1; z < hmRes - 1; z++)
                {
                    for (int x = 1; x < hmRes - 1; x++)
                    {
                        if (lockedMask[z, x]) continue;
                        float h = buf[z, x];
                        float req = h;

                        req = Mathf.Max(req, buf[z, x - 1] - maxDeltaX);
                        req = Mathf.Max(req, buf[z, x + 1] - maxDeltaX);
                        req = Mathf.Max(req, buf[z - 1, x] - maxDeltaZ);
                        req = Mathf.Max(req, buf[z + 1, x] - maxDeltaZ);
                        req = Mathf.Max(req, buf[z - 1, x - 1] - maxDeltaDiag);
                        req = Mathf.Max(req, buf[z - 1, x + 1] - maxDeltaDiag);
                        req = Mathf.Max(req, buf[z + 1, x - 1] - maxDeltaDiag);
                        req = Mathf.Max(req, buf[z + 1, x + 1] - maxDeltaDiag);

                        metersGrid[z, x] = req;
                    }
                }
            }

            // Final bevel smoothing passes outside locked inner pads
            for (int pass = 0; pass < smoothPasses; pass++)
            {
                Array.Copy(metersGrid, buf, metersGrid.Length);
                for (int z = 1; z < hmRes - 1; z++)
                {
                    for (int x = 1; x < hmRes - 1; x++)
                    {
                        if (lockedMask[z, x]) continue;
                        metersGrid[z, x] = (
                            buf[z - 1, x] + buf[z + 1, x] +
                            buf[z, x - 1] + buf[z, x + 1] +
                            buf[z, x] * 2f) / 6f;
                    }
                }
            }
        }

        private static float SampleMetersBilinear(
            float[,] metersGrid,
            Vector3 terrainPos,
            Vector3 terrainSize,
            int hmRes,
            float worldX,
            float worldZ)
        {
            float fx = Mathf.Clamp01((worldX - terrainPos.x) / terrainSize.x) * (hmRes - 1);
            float fz = Mathf.Clamp01((worldZ - terrainPos.z) / terrainSize.z) * (hmRes - 1);
            int x0 = Mathf.Clamp((int)fx, 0, hmRes - 2);
            int z0 = Mathf.Clamp((int)fz, 0, hmRes - 2);
            float tx = fx - x0;
            float tz = fz - z0;

            float h00 = metersGrid[z0, x0];
            float h10 = metersGrid[z0, x0 + 1];
            float h01 = metersGrid[z0 + 1, x0];
            float h11 = metersGrid[z0 + 1, x0 + 1];
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }
    }
}
#endif
