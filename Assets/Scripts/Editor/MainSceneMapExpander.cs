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
    public static class MainSceneMapExpander
    {
        private const string MainScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string ExpandedTerrainAssetPath = "Assets/Scenes/Main_Expanded_TerrainData.asset";
        private const string ReportPath = "Logs/MainSceneExpansionReport.json";

        [Serializable]
        public class ObjectDimensionRecord
        {
            public string Name;
            public Vector3 BeforePosition;
            public Vector3 BeforeBoundsCenter;
            public Vector3 BeforeBoundsSize;
            public float BeforeMinY;
            public Vector3 AfterPosition;
            public Vector3 AfterBoundsCenter;
            public Vector3 AfterBoundsSize;
            public float AfterMinY;
        }

        [Serializable]
        public class ExpansionReport
        {
            public string ScenePath;
            public string ScalingModeSelected;
            public string ScalingModeReason;
            public float PlayerHeightMeters;
            public Vector3 SingleStoryWallSizeMeters;
            public Vector3 TwoStoryWallSizeMeters;
            public Vector3 BeforeTerrainPosition;
            public Vector3 BeforeTerrainSize;
            public Vector3 AfterTerrainPosition;
            public Vector3 AfterTerrainSize;
            public Vector3 BeforeTotalMapBoundsCenter;
            public Vector3 BeforeTotalMapBoundsSize;
            public Vector3 AfterTotalMapBoundsCenter;
            public Vector3 AfterTotalMapBoundsSize;
            public int OuterBackgroundRuinsSnapped;
            public int FarEastBuildingPiecesSnapped;
            public int OuterCollidersAdded;
            public int NegativeBoxCollidersFixed;
            public float BeforeShadowDistance;
            public float AfterShadowDistance;
            public List<ObjectDimensionRecord> RuinSnapRecords = new List<ObjectDimensionRecord>();
        }

        [MenuItem("Scorched/Expand Main Scene Custom Map (Mode B 25x25+)")]
        public static void ExpandMainSceneMenu()
        {
            ExpandMainScene();
        }

        public static string ExpandMainScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != MainScenePath)
            {
                scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Expand Main Scene Custom Map to 25x25+");
            int undoGroup = Undo.GetCurrentGroup();

            var report = new ExpansionReport
            {
                ScenePath = scene.path,
                ScalingModeSelected = "Mode B (Arena Expansion to 25x25x25+ & Outer Ruin Ground Snapping)",
                ScalingModeReason = "Player CharacterController height is 1.80m (visual bounds 2.06m tall) and placed factory buildings are ALREADY at 1:1 human scale (1-story walls = 3.86m tall, 2-story walls = 7.95m tall, doorways = 2.4m-3.2m tall). Only the Factory_01_Terrain base (125m x 125m) was too small, leaving 26 outer Background ruined buildings and the Far-East building cluster floating outside the terrain patch across a 292.3m x 240.6m area.",
                PlayerHeightMeters = 1.80f,
                SingleStoryWallSizeMeters = new Vector3(0.66f, 3.86f, 1.02f),
                TwoStoryWallSizeMeters = new Vector3(0.54f, 7.95f, 7.06f)
            };

            // Measure overall map bounds BEFORE
            Bounds beforeTotalBounds = CalculateSceneEnvironmentBounds(scene);
            report.BeforeTotalMapBoundsCenter = beforeTotalBounds.center;
            report.BeforeTotalMapBoundsSize = beforeTotalBounds.size;

            // Locate Factory_01_Terrain
            GameObject terrainGo = GameObject.Find("Factory_01_Terrain");
            if (terrainGo == null)
            {
                throw new InvalidOperationException("Could not find 'Factory_01_Terrain' in Main scene.");
            }

            Terrain terrain = terrainGo.GetComponent<Terrain>();
            TerrainCollider terrainCollider = terrainGo.GetComponent<TerrainCollider>();
            TerrainData origTd = AssetDatabase.LoadAssetAtPath<TerrainData>("Assets/ScansFactory/AbandonedFactory_BuiltIn/Demo/Factory_01_BuiltIn/Factory_01_Terrain.asset");
            if (origTd == null)
            {
                origTd = terrain.terrainData;
            }

            Vector3 origTerrainPos = new Vector3(-33.579f, -6.13f, -71.501f);
            Vector3 origTerrainSize = origTd.size;
            report.BeforeTerrainPosition = origTerrainPos;
            report.BeforeTerrainSize = origTerrainSize;

            // =========================================================================================
            // STEP 1 & 2A: EXPAND TERRAIN BASE TO 350m x 350m (35x35 Plane Equivalent, > 25x25x25)
            //              WHILE STRICTLY PRESERVING THE CENTRAL 125m x 125m HEIGHTMAP & SPLATMAP
            // =========================================================================================
            // The outer ruins span X: [-78.45, +213.84] (292.29m) and Z: [-154.72, +85.91] (240.63m).
            // A 350m x 350m terrain positioned at (-107.30, origTerrainPos.y, -209.41) covers:
            //   X: [-107.30, +242.70] (350m)
            //   Z: [-209.41, +140.59] (350m)
            // This gives a generous 28m-55m sandy wasteland margin beyond the furthest outer ruins on all 4 sides,
            // while preserving 100% of the exact heightmap and splatmap inside the original [-33.58, 91.42] x [-71.50, 53.50] compound.
            float newWidth = 350f;
            float newLength = 350f;
            float newMaxHeight = origTerrainSize.y; // 600f
            Vector3 newTerrainPos = new Vector3(-107.30f, origTerrainPos.y, -209.41f);

            TerrainData expandedTd = BuildExpandedTerrainData(origTd, origTerrainPos, origTerrainSize, newTerrainPos, new Vector3(newWidth, newMaxHeight, newLength));

            Undo.RecordObject(terrainGo.transform, "Move Expanded Terrain");
            Undo.RecordObject(terrain, "Assign Expanded TerrainData");
            if (terrainCollider != null)
            {
                Undo.RecordObject(terrainCollider, "Assign Expanded TerrainCollider Data");
            }

            terrainGo.transform.position = newTerrainPos;
            terrain.terrainData = expandedTd;
            if (terrainCollider != null)
            {
                terrainCollider.terrainData = expandedTd;
            }

            report.AfterTerrainPosition = newTerrainPos;
            report.AfterTerrainSize = expandedTd.size;

            // =========================================================================================
            // STEP 2B: SNAP ALL FLOATING OUTER RUINS & OUTER BUILDING CLUSTERS TO EXPANDED GROUND
            // =========================================================================================
            // 1. Handle the 26 P_en_Background_01_01 instances under 'Background'
            GameObject bgRoot = GameObject.Find("Background");
            if (bgRoot != null && bgRoot.transform.childCount > 0)
            {
                Transform firstBg = bgRoot.transform.GetChild(0);
                // Collect the 25 child P_en_Background_01_01 instances that are parented under firstBg
                List<Transform> nestedBgChildren = new List<Transform>();
                foreach (Transform child in firstBg)
                {
                    if (child.name.StartsWith("P_en_Background", StringComparison.OrdinalIgnoreCase))
                    {
                        nestedBgChildren.Add(child);
                    }
                }

                // Record all transforms for Undo and cache world positions of nested children so moving firstBg doesn't displace them
                Undo.RecordObject(firstBg, "Snap Outer Background Ruin");
                var cachedWorldPos = new Dictionary<Transform, Vector3>();
                var cachedWorldRot = new Dictionary<Transform, Quaternion>();
                foreach (Transform nc in nestedBgChildren)
                {
                    Undo.RecordObject(nc, "Snap Outer Background Ruin");
                    cachedWorldPos[nc] = nc.position;
                    cachedWorldRot[nc] = nc.rotation;
                }

                // PASS 1: Flatten terrain footprints under all outer Background ruins first
                Bounds firstBgBounds = GetObjectBounds(firstBg, true);
                if (IsOutsideInnerCourtyard(firstBgBounds.center))
                {
                    FlattenTerrainPatchUnderFootprint(terrain, firstBgBounds, 2.0f, 4.0f);
                }
                foreach (Transform nc in nestedBgChildren)
                {
                    Bounds ncBounds = GetObjectBounds(nc, true);
                    if (IsOutsideInnerCourtyard(ncBounds.center))
                    {
                        FlattenTerrainPatchUnderFootprint(terrain, ncBounds, 2.0f, 4.0f);
                    }
                }

                // PASS 2: Snap firstBg and all 25 nested children to the finalized terrain surface
                SnapSingleRuinToTerrain(firstBg, terrain, true, report);

                foreach (Transform nc in nestedBgChildren)
                {
                    nc.position = cachedWorldPos[nc];
                    nc.rotation = cachedWorldRot[nc];
                    SnapSingleRuinToTerrain(nc, terrain, true, report);
                }
            }

            // 2. Snap the floating Far-East building cluster in 'Walls' + 'Roofs' (X > 160, Z in [-92, -60])
            //    Its 16 wall/column pieces and 1 roof piece (P_en_Roof_01_01) were floating together at min.y = 10.4725m.
            SnapFarEastOuterBuildingCluster(terrain, report);

            // Flush terrain modifications
            EditorUtility.SetDirty(expandedTd);
            terrain.Flush();

            // =========================================================================================
            // STEP 3: PHYSICS COLLIDERS & SHADOW DISTANCE ADJUSTMENT
            // =========================================================================================
            // 3.1 Add MeshColliders to the 26 outer Background ruins so bullets & players collide with them
            if (bgRoot != null)
            {
                Transform[] allBgTransforms = bgRoot.GetComponentsInChildren<Transform>(true);
                foreach (Transform t in allBgTransforms)
                {
                    if (t.name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase))
                    {
                        MeshFilter mf = t.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null && t.GetComponent<Collider>() == null)
                        {
                            MeshCollider mc = Undo.AddComponent<MeshCollider>(t.gameObject);
                            mc.sharedMesh = mf.sharedMesh;
                            report.OuterCollidersAdded++;
                        }
                    }
                }
            }

            // 3.2 Fix negative BoxCollider scales/sizes in 'Walls' that caused PhysX warnings
            GameObject wallsRoot = GameObject.Find("Walls");
            if (wallsRoot != null)
            {
                BoxCollider[] boxCols = wallsRoot.GetComponentsInChildren<BoxCollider>(true);
                foreach (BoxCollider bc in boxCols)
                {
                    Vector3 ls = bc.transform.localScale;
                    if (ls.x < 0f || ls.y < 0f || ls.z < 0f)
                    {
                        // Replace negative BoxCollider on mirrored child or normalize size
                        if (bc.size.x < 0f || bc.size.y < 0f || bc.size.z < 0f)
                        {
                            Undo.RecordObject(bc, "Fix BoxCollider Size");
                            bc.size = new Vector3(Mathf.Abs(bc.size.x), Mathf.Abs(bc.size.y), Mathf.Abs(bc.size.z));
                            report.NegativeBoxCollidersFixed++;
                        }
                    }
                }
            }

            // 3.3 Increase QualitySettings & URP Asset shadowDistance to 300m so shadows render clearly across the 350m map
            report.BeforeShadowDistance = QualitySettings.shadowDistance;
            float targetShadowDist = 300f;
            QualitySettings.shadowDistance = targetShadowDist;

            UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                urpAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;

            if (urpAsset != null)
            {
                report.BeforeShadowDistance = urpAsset.shadowDistance;
                Undo.RecordObject(urpAsset, "Increase URP Shadow Distance");
                urpAsset.shadowDistance = targetShadowDist;
                EditorUtility.SetDirty(urpAsset);
            }
            report.AfterShadowDistance = targetShadowDist;

            // Measure overall map bounds AFTER
            Physics.SyncTransforms();
            Bounds afterTotalBounds = CalculateSceneEnvironmentBounds(scene);
            report.AfterTotalMapBoundsCenter = afterTotalBounds.center;
            report.AfterTotalMapBoundsSize = afterTotalBounds.size;

            Undo.CollapseUndoOperations(undoGroup);

            // Save Main.unity and assets
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MainScenePath);
            AssetDatabase.SaveAssets();

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Logs");
            string json = JsonUtility.ToJson(report, true);
            File.WriteAllText(ReportPath, json, Encoding.UTF8);

            Debug.Log($"[MainSceneMapExpander] Completed Mode B expansion of '{MainScenePath}'. Terrain expanded from {origTerrainSize.x}x{origTerrainSize.z}m to {newWidth}x{newLength}m; {report.OuterBackgroundRuinsSnapped} outer background ruins and {report.FarEastBuildingPiecesSnapped} Far-East building pieces snapped to ground.");
            return json;
        }

        private static TerrainData BuildExpandedTerrainData(
            TerrainData origTd,
            Vector3 origPos,
            Vector3 origSize,
            Vector3 newPos,
            Vector3 newSize)
        {
            TerrainData existing = AssetDatabase.LoadAssetAtPath<TerrainData>(ExpandedTerrainAssetPath);
            TerrainData newTd;
            if (existing != null && existing != origTd)
            {
                newTd = existing;
            }
            else
            {
                newTd = new TerrainData();
                AssetDatabase.CreateAsset(newTd, ExpandedTerrainAssetPath);
            }

            int hmRes = origTd.heightmapResolution; // 1025
            int alphaRes = origTd.alphamapResolution; // 1024

            newTd.heightmapResolution = hmRes;
            newTd.alphamapResolution = alphaRes;
            newTd.baseMapResolution = origTd.baseMapResolution;
            newTd.SetDetailResolution(512, origTd.detailResolutionPerPatch);
            newTd.size = newSize;
            newTd.terrainLayers = origTd.terrainLayers;
            newTd.wavingGrassStrength = origTd.wavingGrassStrength;
            newTd.wavingGrassSpeed = origTd.wavingGrassSpeed;
            newTd.wavingGrassAmount = origTd.wavingGrassAmount;
            newTd.wavingGrassTint = origTd.wavingGrassTint;

            float[,] origHeights = origTd.GetHeights(0, 0, hmRes, hmRes);
            float[,] newHeights = new float[hmRes, hmRes];

            // Base courtyard normalized height (~6.01m / 600m = 0.01001f -> worldY ~ -0.12m)
            float baseLocalHeightMeters = 6.01f;
            float nwPlateauLocalHeightMeters = 12.02f; // worldY = 12.02 - 6.13 = +5.89m (matches NW building min.y = 5.89m)

            for (int y = 0; y < hmRes; y++)
            {
                float nz = (float)y / (hmRes - 1);
                float worldZ = newPos.z + nz * newSize.z;

                for (int x = 0; x < hmRes; x++)
                {
                    float nx = (float)x / (hmRes - 1);
                    float worldX = newPos.x + nx * newSize.x;

                    // Convert (worldX, worldZ) into normalized coordinates (u, v) of the original 125m x 125m terrain
                    float u = (worldX - origPos.x) / origSize.x;
                    float v = (worldZ - origPos.z) / origSize.z;

                    if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
                    {
                        // Inside original 125m x 125m compound: preserve EXACT original height via bilinear interpolation
                        newHeights[y, x] = SampleBilinearHeight(origHeights, hmRes, u, v);
                    }
                    else
                    {
                        // Clamped edge height on the boundary of the original 125m x 125m terrain
                        float cu = Mathf.Clamp01(u);
                        float cv = Mathf.Clamp01(v);
                        float edgeNormH = SampleBilinearHeight(origHeights, hmRes, cu, cv);
                        float edgeMeters = edgeNormH * newSize.y;

                        // Distance in meters outside the original 125m x 125m box
                        float dxOut = 0f;
                        if (worldX < origPos.x) dxOut = origPos.x - worldX;
                        else if (worldX > origPos.x + origSize.x) dxOut = worldX - (origPos.x + origSize.x);

                        float dzOut = 0f;
                        if (worldZ < origPos.z) dzOut = origPos.z - worldZ;
                        else if (worldZ > origPos.z + origSize.z) dzOut = worldZ - (origPos.z + origSize.z);

                        float distOutside = Mathf.Sqrt(dxOut * dxOut + dzOut * dzOut);

                        // Special support for the NW building wing at X: [-62, -33.58], Z: [6, 31] where walls sit at worldY = 5.89m
                        float targetOuterMeters = baseLocalHeightMeters;
                        if (worldX >= -63f && worldX <= -33.58f && worldZ >= 6f && worldZ <= 32f)
                        {
                            float wxBlend = 1f - Mathf.SmoothStep(-56f, -63f, worldX);
                            float wzBlend = Mathf.SmoothStep(6f, 11f, worldZ) * (1f - Mathf.SmoothStep(29f, 33f, worldZ));
                            targetOuterMeters = Mathf.Lerp(baseLocalHeightMeters, nwPlateauLocalHeightMeters, wxBlend * wzBlend);
                        }
                        else
                        {
                            // Subtle arid desert wasteland undulations outside the central compound
                            float n1 = Mathf.PerlinNoise(worldX * 0.018f + 31.4f, worldZ * 0.018f + 73.2f);
                            float n2 = Mathf.PerlinNoise(worldX * 0.045f + 19.8f, worldZ * 0.045f + 51.6f) * 0.35f;
                            float wastelandWave = ((n1 + n2) / 1.35f - 0.45f) * 0.9f;

                            // Slight natural perimeter dune rise near the outer 20m border of the 350m x 350m arena
                            float borderDist = Mathf.Min(Mathf.Min(nx, 1f - nx), Mathf.Min(nz, 1f - nz));
                            float borderRise = 0f;
                            if (borderDist < 0.06f)
                            {
                                float bt = 1f - (borderDist / 0.06f);
                                borderRise = bt * bt * (3.5f + n1 * 3.0f);
                            }

                            targetOuterMeters = baseLocalHeightMeters + wastelandWave + borderRise;
                        }

                        // Smoothly blend from the original 125x125 boundary height into the expanded wasteland over 14 meters
                        float blendT = Mathf.SmoothStep(0f, 14f, distOutside);
                        float finalMeters = Mathf.Lerp(edgeMeters, targetOuterMeters, blendT);
                        newHeights[y, x] = Mathf.Clamp01(finalMeters / newSize.y);
                    }
                }
            }

            newTd.SetHeights(0, 0, newHeights);

            // Copy & extend splatmap (alphamaps)
            int numLayers = origTd.alphamapLayers;
            if (numLayers > 0)
            {
                float[,,] origAlpha = origTd.GetAlphamaps(0, 0, alphaRes, alphaRes);
                float[,,] newAlpha = new float[alphaRes, alphaRes, numLayers];

                for (int y = 0; y < alphaRes; y++)
                {
                    float nz = (float)y / (alphaRes - 1);
                    float worldZ = newPos.z + nz * newSize.z;

                    for (int x = 0; x < alphaRes; x++)
                    {
                        float nx = (float)x / (alphaRes - 1);
                        float worldX = newPos.x + nx * newSize.x;

                        float u = (worldX - origPos.x) / origSize.x;
                        float v = (worldZ - origPos.z) / origSize.z;

                        if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
                        {
                            int ox = Mathf.Clamp(Mathf.RoundToInt(u * (alphaRes - 1)), 0, alphaRes - 1);
                            int oy = Mathf.Clamp(Mathf.RoundToInt(v * (alphaRes - 1)), 0, alphaRes - 1);
                            for (int l = 0; l < numLayers; l++)
                            {
                                newAlpha[y, x, l] = origAlpha[oy, ox, l];
                            }
                        }
                        else
                        {
                            // Natural sandy/earth blend across the expanded outer wasteland using the existing TerrainLayers
                            float noise = Mathf.PerlinNoise(worldX * 0.05f + 11.3f, worldZ * 0.05f + 27.9f);
                            if (numLayers >= 3)
                            {
                                float w0 = Mathf.Lerp(0.55f, 0.85f, noise);
                                float w1 = (1f - w0) * 0.75f;
                                float w2 = (1f - w0) * 0.25f;
                                newAlpha[y, x, 0] = w0;
                                newAlpha[y, x, 1] = w1;
                                newAlpha[y, x, 2] = w2;
                            }
                            else
                            {
                                newAlpha[y, x, 0] = 1f;
                            }
                        }
                    }
                }

                newTd.SetAlphamaps(0, 0, newAlpha);
            }

            return newTd;
        }

        private static float SampleBilinearHeight(float[,] heights, int res, float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (res - 1);
            float fy = Mathf.Clamp01(v) * (res - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int y1 = Mathf.Min(y0 + 1, res - 1);

            float tx = fx - x0;
            float ty = fy - y0;

            float h00 = heights[y0, x0];
            float h10 = heights[y0, x1];
            float h01 = heights[y1, x0];
            float h11 = heights[y1, x1];

            float h0 = Mathf.Lerp(h00, h10, tx);
            float h1 = Mathf.Lerp(h01, h11, tx);
            return Mathf.Lerp(h0, h1, ty);
        }

        private static bool IsOutsideInnerCourtyard(Vector3 center)
        {
            return center.x < -30f || center.x > 85f || center.z < -50f || center.z > 40f;
        }

        private static void SnapSingleRuinToTerrain(
            Transform ruinTransform,
            Terrain terrain,
            bool onlyDirectMeshChildren,
            ExpansionReport report)
        {
            Bounds beforeBounds = GetObjectBounds(ruinTransform, onlyDirectMeshChildren);
            Vector3 beforePos = ruinTransform.position;

            // Sample a 5x5 grid across the ruin's horizontal footprint and use the minimum terrain height
            // so even ruins straddling a hillside slope have zero floating corners anywhere along their base.
            float minGroundWorldY = float.MaxValue;
            for (int iz = 0; iz <= 4; iz++)
            {
                float sz = Mathf.Lerp(beforeBounds.min.z, beforeBounds.max.z, iz * 0.25f);
                for (int ix = 0; ix <= 4; ix++)
                {
                    float sx = Mathf.Lerp(beforeBounds.min.x, beforeBounds.max.x, ix * 0.25f);
                    float h = terrain.SampleHeight(new Vector3(sx, 0f, sz)) + terrain.transform.position.y;
                    if (h < minGroundWorldY)
                        minGroundWorldY = h;
                }
            }

            // Calculate exact vertical offset using bottom Renderer.bounds.min.y (with 0.15m sink to prevent any micro-gap)
            float bottomOffset = beforePos.y - beforeBounds.min.y;
            Vector3 newPos = beforePos;
            newPos.y = minGroundWorldY + bottomOffset - 0.15f;
            ruinTransform.position = newPos;

            Bounds afterBounds = GetObjectBounds(ruinTransform, onlyDirectMeshChildren);

            report.OuterBackgroundRuinsSnapped++;
            report.RuinSnapRecords.Add(new ObjectDimensionRecord
            {
                Name = ruinTransform.name,
                BeforePosition = beforePos,
                BeforeBoundsCenter = beforeBounds.center,
                BeforeBoundsSize = beforeBounds.size,
                BeforeMinY = beforeBounds.min.y,
                AfterPosition = newPos,
                AfterBoundsCenter = afterBounds.center,
                AfterBoundsSize = afterBounds.size,
                AfterMinY = afterBounds.min.y
            });
        }

        private static void SnapFarEastOuterBuildingCluster(Terrain terrain, ExpansionReport report)
        {
            // Collect all Walls and Roofs belonging to the Far-East building cluster (X > 160)
            List<Transform> clusterPieces = new List<Transform>();
            string[] containers = { "Walls", "Roofs" };

            foreach (string cName in containers)
            {
                GameObject cGo = GameObject.Find(cName);
                if (cGo == null) continue;

                foreach (Transform child in cGo.transform)
                {
                    if (child.position.x > 160f)
                    {
                        clusterPieces.Add(child);
                    }
                }
            }

            if (clusterPieces.Count == 0) return;

            Bounds combinedBefore = new Bounds(clusterPieces[0].position, Vector3.zero);
            bool hasBounds = false;

            foreach (Transform piece in clusterPieces)
            {
                Undo.RecordObject(piece, "Snap Far-East Outer Building Cluster");
                Renderer[] rs = piece.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in rs)
                {
                    if (!hasBounds)
                    {
                        combinedBefore = r.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        combinedBefore.Encapsulate(r.bounds);
                    }
                }
            }

            if (!hasBounds) return;

            // Flatten terrain under the Far-East building cluster footprint
            FlattenTerrainPatchUnderFootprint(terrain, combinedBefore, 3.0f, 5.0f);

            float groundWorldY = terrain.SampleHeight(combinedBefore.center) + terrain.transform.position.y;
            float deltaY = (groundWorldY - combinedBefore.min.y) - 0.08f;

            foreach (Transform piece in clusterPieces)
            {
                Vector3 p = piece.position;
                p.y += deltaY;
                piece.position = p;
                report.FarEastBuildingPiecesSnapped++;
            }

            Bounds combinedAfter = new Bounds(clusterPieces[0].position, Vector3.zero);
            bool hasAfter = false;
            foreach (Transform piece in clusterPieces)
            {
                Renderer[] rs = piece.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in rs)
                {
                    if (!hasAfter)
                    {
                        combinedAfter = r.bounds;
                        hasAfter = true;
                    }
                    else
                    {
                        combinedAfter.Encapsulate(r.bounds);
                    }
                }
            }

            report.RuinSnapRecords.Add(new ObjectDimensionRecord
            {
                Name = "FarEast_OuterBuilding_Cluster (16 Walls + 1 Roof)",
                BeforePosition = combinedBefore.center,
                BeforeBoundsCenter = combinedBefore.center,
                BeforeBoundsSize = combinedBefore.size,
                BeforeMinY = combinedBefore.min.y,
                AfterPosition = combinedAfter.center,
                AfterBoundsCenter = combinedAfter.center,
                AfterBoundsSize = combinedAfter.size,
                AfterMinY = combinedAfter.min.y
            });
        }

        private static void FlattenTerrainPatchUnderFootprint(Terrain terrain, Bounds worldBounds, float apronMeters, float blendRimMeters)
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

            // Target normalized height at the center of the footprint
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

                    // Do not alter heightmap inside the inner central courtyard/tracks [-28, 82] x [-45, 35]
                    if (wx > -28f && wx < 82f && wz > -45f && wz < 35f)
                        continue;

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

        private static Bounds GetObjectBounds(Transform root, bool onlyDirectMeshChildren)
        {
            Bounds b = new Bounds(root.position, Vector3.zero);
            bool hasB = false;

            if (onlyDirectMeshChildren)
            {
                foreach (Transform child in root)
                {
                    if (child.name.StartsWith("P_en_Background", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Renderer r = child.GetComponent<Renderer>();
                    if (r != null)
                    {
                        if (!hasB) { b = r.bounds; hasB = true; }
                        else b.Encapsulate(r.bounds);
                    }
                }
            }

            if (!hasB)
            {
                Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in rs)
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!hasB) { b = r.bounds; hasB = true; }
                    else b.Encapsulate(r.bounds);
                }
            }

            return b;
        }

        private static Bounds CalculateSceneEnvironmentBounds(Scene scene)
        {
            Bounds total = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "Player" || root.name == "Network Manager" || root.name == "Directional Light" || root.name == "Global Volume")
                    continue;

                Terrain tr = root.GetComponentInChildren<Terrain>(true);
                if (tr != null && tr.terrainData != null)
                {
                    Bounds tb = tr.terrainData.bounds;
                    tb.center += tr.transform.position;
                    if (!hasBounds) { total = tb; hasBounds = true; }
                    else total.Encapsulate(tb);
                }

                Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in rs)
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!hasBounds) { total = r.bounds; hasBounds = true; }
                    else total.Encapsulate(r.bounds);
                }
            }

            return total;
        }
    }
}
