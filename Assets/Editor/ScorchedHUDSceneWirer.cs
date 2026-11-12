using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Netcode;
using Scorched.Inventory;
using Scorched.Player;
using Scorched.Survival;
using Scorched.UI;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Wires HeatwaveHazardManager, ScorchedHUDController, FootstepRadarUI, and PlayerThermalController
    /// into Level 0 - Test Environment.unity and Assets/Prefabs/Player.prefab with all UI sprites bound.
    /// </summary>
    public static class ScorchedHUDSceneWirer
    {
        private const string ScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
        private const string SpriteFolder = "Assets/UI/ScorchedHUD/";

        [MenuItem("SCORCHED/Wire Survival & HUD Systems")]
        public static void WireAll()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 0. Auto-configure _ShadeCanopies_And_Cover safe-zones in scene
            int shadeZonesCount = ShadeZoneSetupUtility.SetupShadeZonesInActiveScene();

            // 1. Wire HeatwaveHazardManager in scene
            var hazardMgr = Object.FindAnyObjectByType<HeatwaveHazardManager>();
            if (hazardMgr == null)
            {
                var hazardGo = new GameObject("HeatwaveHazardManager");
                hazardGo.AddComponent<NetworkObject>();
                hazardMgr = hazardGo.AddComponent<HeatwaveHazardManager>();
                Debug.Log("[ScorchedHUDSceneWirer] Created HeatwaveHazardManager in scene.");
            }
            else if (hazardMgr.GetComponent<NetworkObject>() == null)
            {
                hazardMgr.gameObject.AddComponent<NetworkObject>();
            }

            // Remove any stray pre-placed L4DHUDManager in scene
            foreach (var oldHud in Object.FindObjectsByType<L4DHUDManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (oldHud != null)
                {
                    Object.DestroyImmediate(oldHud.gameObject);
                }
            }

            // 2. Wire ScorchedHUDController & FootstepRadarUI in scene
            var hudCtrl = Object.FindAnyObjectByType<ScorchedHUDController>();
            if (hudCtrl == null)
            {
                var hudGo = new GameObject("Scorched_HUD");
                hudCtrl = hudGo.AddComponent<ScorchedHUDController>();
                Debug.Log("[ScorchedHUDSceneWirer] Created Scorched_HUD in scene.");
            }

            var radar = hudCtrl.GetComponent<FootstepRadarUI>();
            if (radar == null)
            {
                radar = hudCtrl.gameObject.AddComponent<FootstepRadarUI>();
            }

            // Bind all GDD Tactical HUD sprites via SerializedObject
            Sprite gddFrameSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "gdd_tactical_frame.png");
            Sprite trackSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "custom_bar_track.png");
            Sprite segFillSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "custom_bar_fill_segmented.png");
            Sprite razorFillSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "custom_bar_fill_razor.png");
            Sprite healthCrossSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "icon_health_cross.png");

            Sprite arcSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "footstep_arc_orange.png");
            Sprite sunSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "icon_status_sun.png");
            Sprite shadeSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "icon_status_shade.png");
            Sprite dropSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "icon_hydration_drop.png");
            Sprite vigSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "heatstroke_vignette.png");

            var hudSo = new SerializedObject(hudCtrl);
            SetSpriteProp(hudSo, "_gddTacticalFrameSprite", gddFrameSprite);
            SetSpriteProp(hudSo, "_customBarTrackSprite", trackSprite);
            SetSpriteProp(hudSo, "_customBarFillSegmentedSprite", segFillSprite);
            SetSpriteProp(hudSo, "_customBarFillRazorSprite", razorFillSprite);
            SetSpriteProp(hudSo, "_iconHealthCrossSprite", healthCrossSprite);
            SetSpriteProp(hudSo, "_footstepArcSprite", arcSprite);
            SetSpriteProp(hudSo, "_iconSunSprite", sunSprite);
            SetSpriteProp(hudSo, "_iconShadeSprite", shadeSprite);
            SetSpriteProp(hudSo, "_iconHydrationDropSprite", dropSprite);
            SetSpriteProp(hudSo, "_heatstrokeVignetteSprite", vigSprite);
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            var radarSo = new SerializedObject(radar);
            SetSpriteProp(radarSo, "_footstepArcSprite", arcSprite);
            radarSo.ApplyModifiedPropertiesWithoutUndo();

            bool allBound = gddFrameSprite && trackSprite && segFillSprite && razorFillSprite && healthCrossSprite &&
                            arcSprite && sunSprite && shadeSprite && dropSprite && vigSprite;

            // Disable debug perspective banner on any scene-placed Player too
            foreach (var persp in Object.FindObjectsByType<FPSPerspectiveController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                DisablePerspectiveBanner(persp);
            }
            foreach (var pm in Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                EnsurePlayerHudDependencies(pm.gameObject, "scene");
            }

            // 3. Wire PlayerThermalController & PlayerHotbarController & hide debug banner on ALL player prefabs
            string[] candidatePrefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" });
            int wiredPrefabs = 0;
            foreach (string guid in candidatePrefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject checkAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (checkAsset == null || checkAsset.GetComponent<PlayerMovement>() == null)
                {
                    continue;
                }

                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                if (prefabRoot != null)
                {
                    EnsurePlayerHudDependencies(prefabRoot, path);

                    var persp = prefabRoot.GetComponent<FPSPerspectiveController>();
                    if (persp != null)
                    {
                        DisablePerspectiveBanner(persp);
                    }

                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                    wiredPrefabs++;
                }
            }
            Debug.Log($"[ScorchedHUDSceneWirer] Wired {wiredPrefabs} Player prefabs.");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ScorchedHUDSceneWirer] Complete. ShadeZones={shadeZonesCount}, AllGddSpritesBound={allBound} (frame={gddFrameSprite != null}, track={trackSprite != null}, seg={segFillSprite != null}, razor={razorFillSprite != null}, cross={healthCrossSprite != null}, arc={arcSprite != null})");
        }

        private static void EnsurePlayerHudDependencies(GameObject target, string contextLabel)
        {
            if (target == null) return;
            if (target.GetComponent<PlayerThermalController>() == null)
            {
                target.AddComponent<PlayerThermalController>();
                Debug.Log("[ScorchedHUDSceneWirer] Added PlayerThermalController to " + contextLabel);
            }
            if (target.GetComponent<PlayerInventoryController>() == null)
            {
                target.AddComponent<PlayerInventoryController>();
                Debug.Log("[ScorchedHUDSceneWirer] Added PlayerInventoryController to " + contextLabel);
            }
            if (target.GetComponent<PlayerHotbarController>() == null)
            {
                target.AddComponent<PlayerHotbarController>();
                Debug.Log("[ScorchedHUDSceneWirer] Added PlayerHotbarController to " + contextLabel);
            }
        }

        private static void DisablePerspectiveBanner(FPSPerspectiveController persp)
        {
            if (persp == null) return;
            var so = new SerializedObject(persp);
            var prop = so.FindProperty("_showOnScreenBanner");
            if (prop != null)
            {
                prop.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void SetSpriteProp(SerializedObject so, string propName, Sprite sprite)
        {
            var prop = so.FindProperty(propName);
            if (prop != null)
            {
                prop.objectReferenceValue = sprite;
            }
            else
            {
                Debug.LogWarning($"[ScorchedHUDSceneWirer] Property '{propName}' not found on {so.targetObject.name}");
            }
        }
    }
}
