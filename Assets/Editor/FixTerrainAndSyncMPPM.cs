#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Scorched.EditorTools
{
    public static class FixTerrainAndSyncMPPM
    {
        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        [MenuItem("SCORCHED/Fix Terrain Material and Sync Assets")]
        public static void FixTerrainAndSync()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.path != TargetScenePath)
            {
                activeScene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            Terrain terrain = Object.FindAnyObjectByType<Terrain>();
            if (terrain == null)
            {
                Debug.LogError("[FixTerrain] No Terrain found in active scene!");
                return;
            }

            Debug.Log($"[FixTerrain] Found Terrain: {terrain.name}, Current materialTemplate: {(terrain.materialTemplate != null ? terrain.materialTemplate.name : "null")}");

            // Load official URP TerrainLit material
            Material urpTerrainLitMat = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.unity.render-pipelines.universal/Runtime/Materials/TerrainLit.mat");
            if (urpTerrainLitMat == null)
            {
                var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (rp != null && rp.defaultTerrainMaterial != null)
                {
                    urpTerrainLitMat = rp.defaultTerrainMaterial;
                }
            }

            if (urpTerrainLitMat != null)
            {
                Undo.RecordObject(terrain, "Fix Terrain Material Template");
                terrain.materialTemplate = urpTerrainLitMat;
                EditorUtility.SetDirty(terrain);
                Debug.Log($"[FixTerrain] Assigned official URP TerrainLit material: {urpTerrainLitMat.name} ({AssetDatabase.GetAssetPath(urpTerrainLitMat)})");
            }
            else
            {
                Debug.LogWarning("[FixTerrain] Could not load URP TerrainLit material, clearing materialTemplate to use pipeline default.");
                Undo.RecordObject(terrain, "Clear Terrain Material Template");
                terrain.materialTemplate = null;
                EditorUtility.SetDirty(terrain);
            }

            // Verify TerrainData and Layers
            TerrainData td = terrain.terrainData;
            if (td != null)
            {
                Debug.Log($"[FixTerrain] TerrainData: {td.name}, Size: {td.size}, HeightmapRes: {td.heightmapResolution}, SplatRes: {td.alphamapResolution}, Layers count: {(td.terrainLayers != null ? td.terrainLayers.Length : 0)}");
                if (td.terrainLayers != null)
                {
                    for (int i = 0; i < td.terrainLayers.Length; i++)
                    {
                        var layer = td.terrainLayers[i];
                        Debug.Log($"[FixTerrain] Layer[{i}]: {(layer != null ? layer.name : "null")} - Diffuse: {(layer != null && layer.diffuseTexture != null ? layer.diffuseTexture.name : "null")}");
                    }
                }
                EditorUtility.SetDirty(td);
            }

            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            Debug.Log("[FixTerrain] Scene and assets saved successfully!");
        }
    }
}
#endif

