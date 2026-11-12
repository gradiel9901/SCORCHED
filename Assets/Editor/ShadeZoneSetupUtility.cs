#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Scorched.Survival;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Automatically attaches ShadeZoneBuilding and oriented BoxCollider trigger volumes
    /// to every direct building child under _ShadeCanopies_And_Cover and all town district groups.
    /// </summary>
    public static class ShadeZoneSetupUtility
    {
        private const string ScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string TriggerChildName = "ShadeTriggerVolume";
        private const int IgnoreRaycastLayer = 2;

        private static readonly string[] DistrictRoots = new string[]
        {
            "_ShadeCanopies_And_Cover",
            "_Administrative_District",
            "_Manufacturing_District_TL",
            "_Manufacting_District_TL",
            "_Manufacturing_District_BL",
            "_Storage_District"
        };

        [MenuItem("SCORCHED/Setup Shade Safe-Zones (All Districts & Covers)")]
        public static void SetupShadeZonesMenu()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int configuredCount = SetupShadeZonesInActiveScene();
            if (configuredCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
            }
        }

        public static int SetupShadeZonesInActiveScene()
        {
            var map = GameObject.Find("SCORCHED_Town_Map");
            Transform mapTr = map != null ? map.transform : null;

            var buildings = new List<Transform>();
            var seen = new HashSet<Transform>();

            foreach (var dName in DistrictRoots)
            {
                Transform group = mapTr != null ? mapTr.Find(dName) : null;
                if (group == null)
                {
                    var go = GameObject.Find(dName);
                    if (go != null) group = go.transform;
                }
                if (group == null) continue;

                for (int i = 0; i < group.childCount; i++)
                {
                    Transform bldg = group.GetChild(i);
                    if (bldg == null || !seen.Add(bldg)) continue;

                    // Filter out non-building small props
                    var renderers = bldg.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length < 4) continue;

                    if (!TryComputeLocalRendererBounds(bldg, out Bounds b)) continue;
                    if (Mathf.Max(b.size.x, b.size.z) < 5.0f || b.size.y < 2.5f) continue;

                    buildings.Add(bldg);
                }
            }

            int configured = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                Transform building = buildings[i];
                var shadeZone = building.GetComponent<ShadeZoneBuilding>();
                if (shadeZone == null)
                {
                    shadeZone = Undo.AddComponent<ShadeZoneBuilding>(building.gameObject);
                }

                shadeZone.ShadeIndex = i;
                shadeZone.MaxDurability = 100f;

                if (!TryComputeLocalRendererBounds(building, out Bounds localBounds))
                {
                    localBounds = new Bounds(new Vector3(0f, 2.0f, 0f), new Vector3(8f, 4f, 8f));
                }

                // Tight interior coverage strictly inside walls, trimmed 1.0m on X/Z to eliminate outdoor bleeding
                float insetX = 1.0f;
                float insetZ = 1.0f;
                Vector3 triggerSize = new Vector3(
                    Mathf.Max(2.0f, localBounds.size.x - insetX),
                    Mathf.Max(4.5f, localBounds.size.y + 0.5f),
                    Mathf.Max(2.0f, localBounds.size.z - insetZ));
                Vector3 triggerCenter = localBounds.center;

                Transform triggerTransform = building.Find(TriggerChildName);
                GameObject triggerGo;
                if (triggerTransform == null)
                {
                    triggerGo = new GameObject(TriggerChildName);
                    triggerGo.transform.SetParent(building, false);
                    Undo.RegisterCreatedObjectUndo(triggerGo, "Create ShadeTriggerVolume");
                }
                else
                {
                    triggerGo = triggerTransform.gameObject;
                }

                triggerGo.transform.localPosition = Vector3.zero;
                triggerGo.transform.localRotation = Quaternion.identity;
                triggerGo.transform.localScale = Vector3.one;
                triggerGo.layer = IgnoreRaycastLayer;

                var box = triggerGo.GetComponent<BoxCollider>();
                if (box == null)
                {
                    box = triggerGo.AddComponent<BoxCollider>();
                }
                box.isTrigger = true;
                box.center = triggerCenter;
                box.size = triggerSize;

                var proxy = triggerGo.GetComponent<ShadeTriggerProxy>();
                if (proxy == null)
                {
                    proxy = triggerGo.AddComponent<ShadeTriggerProxy>();
                }
                proxy.Bind(shadeZone);
                shadeZone.SetTriggerVolume(box);

                EditorUtility.SetDirty(shadeZone);
                EditorUtility.SetDirty(box);
                EditorUtility.SetDirty(proxy);
                configured++;
            }

            Debug.Log($"[ShadeZoneSetupUtility] Configured {configured} ShadeZoneBuilding safe-zones across all town districts.");
            return configured;
        }

        private static bool TryComputeLocalRendererBounds(Transform buildingRoot, out Bounds localBounds)
        {
            localBounds = new Bounds();
            bool hasBounds = false;

            var renderers = buildingRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r is ParticleSystemRenderer)
                {
                    continue;
                }

                Bounds wb = r.bounds;
                Vector3 ext = wb.extents;
                Vector3 c = wb.center;

                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 worldCorner = c + new Vector3(ext.x * sx, ext.y * sy, ext.z * sz);
                            Vector3 localCorner = buildingRoot.InverseTransformPoint(worldCorner);
                            if (!hasBounds)
                            {
                                localBounds = new Bounds(localCorner, Vector3.zero);
                                hasBounds = true;
                            }
                            else
                            {
                                localBounds.Encapsulate(localCorner);
                            }
                        }
                    }
                }
            }

            return hasBounds;
        }
    }
}
#endif
