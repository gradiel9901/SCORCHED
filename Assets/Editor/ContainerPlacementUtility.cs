using System.Collections.Generic;
using System.IO;
using Scorched.Inventory;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Automates Phase 6 container setup and scene/prefab wiring:
    /// - Repairs Drop Supply Box offset pivot and configures NetworkObject + LootContainerInteractable on all container prefabs.
    /// - Places Inside_Chest exclusively inside open/enterable building interiors (using upward roof + downward floor + clearance raycasts).
    /// - Places outside_Chest outside the town perimeter walls flushed cleanly to Terrain.
    /// - Registers containers and all world pickup prefabs into Assets/DefaultNetworkPrefabs.asset.
    /// - Wires ContainerSpawnManager, HUD sprites, and Player prefabs cleanly.
    /// </summary>
    public static class ContainerPlacementUtility
    {
        private const string InsideChestPath = "Assets/Prefabs/Container/Inside_Chest.prefab";
        private const string OutsideChestPath = "Assets/Prefabs/Container/outside_Chest.prefab";
        private const string DropSupplyBoxPath = "Assets/Prefabs/Container/Drop Supply Box.prefab";
        private const string DeathBagPath = "Assets/Prefabs/Container/Death Bag.prefab";
        private const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";
        private const string ItemPrefabsDir = "Assets/Prefabs/Items";
        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        [MenuItem("SCORCHED/Place Containers & Wire Inventory System")]
        public static void ExecuteAllWiring()
        {
            // 0. Ensure Survival & HUD Systems are wired and target scene is open first
            ScorchedHUDSceneWirer.WireAll();
            var scene = EditorSceneManager.GetActiveScene();

            // 1. Prepare container prefabs with NetworkObject + LootContainerInteractable
            var insidePrefab = ConfigureContainerPrefab(InsideChestPath, LootContainerType.InsideChest, recenterLocalPivot: false, addNetworkTransform: false);
            var outsidePrefab = ConfigureContainerPrefab(OutsideChestPath, LootContainerType.OutsideChest, recenterLocalPivot: false, addNetworkTransform: false);
            var dropPrefab = ConfigureContainerPrefab(DropSupplyBoxPath, LootContainerType.DropSupplyBox, recenterLocalPivot: true, addNetworkTransform: true);
            var deathBagPrefab = ConfigureContainerPrefab(DeathBagPath, LootContainerType.DeathBag, recenterLocalPivot: true, addNetworkTransform: false);

            // 2. Register containers & all item pickups into DefaultNetworkPrefabs
            RegisterNetworkPrefabs(insidePrefab, outsidePrefab, dropPrefab, deathBagPrefab);
            EnsureWeaponItemInCatalog();

            // 3. Place Inside_Chest & outside_Chest cleanly into the scene
            int insideCount = PlaceInsideChests(insidePrefab);
            int outsideCount = PlaceOutsideChests(outsidePrefab);

            // 4. Wire ContainerSpawnManager in scene
            EnsureContainerSpawnManager(dropPrefab);

            // 5. Wire HUD sprites & ScorchedHUDSceneWirer
            WireHUDInventorySprites();

            // 6. Wire Player Prefabs with PlayerInventoryController + PlayerScavengeInteractor + DeathBag prefab
            int playerPrefabsWired = WirePlayerPrefabs(deathBagPrefab);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ContainerPlacementUtility] Completed: {insideCount} Inside_Chest, {outsideCount} outside_Chest, {playerPrefabsWired} Player prefabs wired, DefaultNetworkPrefabs updated.");
        }

        [MenuItem("SCORCHED/Wire Death Bag & Weapon Catalog")]
        public static void WireDeathBagAndWeaponCatalog()
        {
            var deathBagPrefab = ConfigureContainerPrefab(DeathBagPath, LootContainerType.DeathBag, recenterLocalPivot: true, addNetworkTransform: false);
            RegisterNetworkPrefabs(deathBagPrefab);
            EnsureWeaponItemInCatalog();
            int wiredPlayers = WirePlayerPrefabs(deathBagPrefab);

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[ContainerPlacementUtility] Wired Death Bag prefab ({(deathBagPrefab != null ? deathBagPrefab.name : "null")}), catalog Weapon_AK47, and {wiredPlayers} Player prefabs.");
        }

        [MenuItem("SCORCHED/Repair Container Prefab Colliders Only")]
        public static void RepairContainerPrefabCollidersOnly()
        {
            ConfigureContainerPrefab(InsideChestPath, LootContainerType.InsideChest, recenterLocalPivot: false, addNetworkTransform: false);
            ConfigureContainerPrefab(OutsideChestPath, LootContainerType.OutsideChest, recenterLocalPivot: false, addNetworkTransform: false);
            ConfigureContainerPrefab(DropSupplyBoxPath, LootContainerType.DropSupplyBox, recenterLocalPivot: false, addNetworkTransform: true);

            // Clean up any stale null-mesh collider overrides on already-placed scene containers without moving them
            var sceneChests = Object.FindObjectsByType<LootContainerInteractable>(FindObjectsInactive.Include);
            int cleaned = 0;
            foreach (var chest in sceneChests)
            {
                if (chest == null) continue;
                foreach (var mc in chest.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (mc != null && mc.sharedMesh == null)
                    {
                        Object.DestroyImmediate(mc, true);
                        cleaned++;
                    }
                }
                foreach (var col in chest.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null && !col.isTrigger && !col.enabled)
                    {
                        col.enabled = true;
                        cleaned++;
                    }
                }
            }

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[ContainerPlacementUtility] Repaired container prefab colliders cleanly (preserved all {sceneChests.Length} scene positions, cleaned {cleaned} overrides).");
        }

        [MenuItem("SCORCHED/Randomize Inside Boxes Only (Max 3 Per Building)")]
        public static void RandomizeInsideBoxesOnly()
        {
            var insidePrefab = ConfigureContainerPrefab(InsideChestPath, LootContainerType.InsideChest, recenterLocalPivot: false, addNetworkTransform: false);
            int insideCount = PlaceInsideChests(insidePrefab);

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[ContainerPlacementUtility] Placed {insideCount} Inside_Chest boxes (1-3 per building, rotations in {{0, 90, 180}}) while preserving all Outside_Chest placements.");
        }

        private static GameObject ConfigureContainerPrefab(string prefabPath, LootContainerType containerType, bool recenterLocalPivot, bool addNetworkTransform)
        {
            if (!File.Exists(prefabPath))
            {
                Debug.LogWarning($"[ContainerPlacementUtility] Missing container prefab: {prefabPath}");
                return null;
            }

            using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
            {
                var root = scope.prefabContentsRoot;

                if (recenterLocalPivot)
                {
                    root.transform.localPosition = Vector3.zero;
                    Bounds localBounds = new Bounds(Vector3.zero, Vector3.zero);
                    bool any = false;
                    var renderers = root.GetComponentsInChildren<Renderer>();
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Bounds worldB = renderers[i].bounds;
                        // Convert relative to root transform
                        Vector3 relMin = root.transform.InverseTransformPoint(worldB.min);
                        Vector3 relMax = root.transform.InverseTransformPoint(worldB.max);
                        Bounds rb = new Bounds((relMin + relMax) * 0.5f, Vector3.Max(Vector3.zero, relMax - relMin));
                        if (!any) { localBounds = rb; any = true; }
                        else localBounds.Encapsulate(rb);
                    }

                    if (any && (Mathf.Abs(localBounds.center.x) > 0.25f || Mathf.Abs(localBounds.min.y) > 0.15f || Mathf.Abs(localBounds.center.z) > 0.25f))
                    {
                        Vector3 shift = new Vector3(-localBounds.center.x, -localBounds.min.y, -localBounds.center.z);
                        foreach (Transform child in root.transform)
                        {
                            child.localPosition += shift;
                        }
                        var existingBox = root.GetComponent<BoxCollider>();
                        if (existingBox != null)
                        {
                            existingBox.center = new Vector3(0f, localBounds.extents.y, 0f);
                            existingBox.size = localBounds.size;
                        }
                    }
                }

                // Remove any broken MeshColliders with null meshes (e.g., Inside_Chest LOD0 missing collision mesh)
                var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);
                for (int i = 0; i < meshColliders.Length; i++)
                {
                    if (meshColliders[i] != null && meshColliders[i].sharedMesh == null)
                    {
                        Object.DestroyImmediate(meshColliders[i], true);
                    }
                }

                // Guarantee an easy-to-raycast solid collider with valid bounds
                bool hasValidCollider = false;
                var existingColliders = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < existingColliders.Length; i++)
                {
                    var c = existingColliders[i];
                    if (c == null || c.isTrigger || !c.enabled) continue;
                    if (c is MeshCollider mc && mc.sharedMesh == null) continue;
                    hasValidCollider = true;
                    break;
                }

                if (!hasValidCollider)
                {
                    var b = CalculateLocalRendererBounds(root);
                    var box = root.GetComponent<BoxCollider>();
                    if (box == null)
                    {
                        box = root.AddComponent<BoxCollider>();
                    }
                    Vector3 clampedSize = Vector3.Max(b.size, new Vector3(0.85f, 0.85f, 0.85f));
                    Vector3 boxCenter = b.center;
                    boxCenter.y = Mathf.Max(boxCenter.y, clampedSize.y * 0.5f);
                    box.center = boxCenter;
                    box.size = clampedSize;
                    box.isTrigger = false;
                }

                if (root.GetComponent<NetworkObject>() == null)
                {
                    root.AddComponent<NetworkObject>();
                }
                if (addNetworkTransform && root.GetComponent<NetworkTransform>() == null)
                {
                    root.AddComponent<NetworkTransform>();
                }

                var interactable = root.GetComponent<LootContainerInteractable>() ?? root.AddComponent<LootContainerInteractable>();
                interactable.ConfigureType(containerType);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }

        private static Bounds CalculateLocalRendererBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(new Vector3(0f, 0.4f, 0f), Vector3.one * 0.8f);
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            Vector3 c = root.transform.InverseTransformPoint(b.center);
            return new Bounds(c, b.size);
        }

        private static void RegisterNetworkPrefabs(params GameObject[] containerPrefabs)
        {
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                Debug.LogWarning($"[ContainerPlacementUtility] {NetworkPrefabsListPath} not found.");
                return;
            }

            var toAdd = new List<GameObject>();
            foreach (var c in containerPrefabs)
            {
                if (c != null) toAdd.Add(c);
            }

            if (Directory.Exists(ItemPrefabsDir))
            {
                foreach (var guids in AssetDatabase.FindAssets("t:Prefab", new[] { ItemPrefabsDir }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids);
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go != null) toAdd.Add(go);
                }
            }

            int added = 0;
            foreach (var pf in toAdd)
            {
                if (pf == null || pf.GetComponent<NetworkObject>() == null) continue;
                bool exists = false;
                foreach (var entry in list.PrefabList)
                {
                    if (entry.Prefab == pf) { exists = true; break; }
                }
                if (!exists)
                {
                    list.Add(new NetworkPrefab { Prefab = pf });
                    added++;
                }
            }

            if (added > 0)
            {
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[ContainerPlacementUtility] Registered {added} new NetworkPrefabs (total={list.PrefabList.Count}).");
        }

        private static int PlaceInsideChests(GameObject insidePrefab)
        {
            if (insidePrefab == null) return 0;

            var old = GameObject.Find("_LootContainers_Inside");
            if (old != null) Object.DestroyImmediate(old);

            var root = new GameObject("_LootContainers_Inside");
            var map = GameObject.Find("SCORCHED_Town_Map");
            Transform mapTransform = map != null ? map.transform : null;

            // Collect enterable buildings across _ShadeCanopies_And_Cover and all district groups (support both _Manufacting_District_TL and _Manufacturing_District_TL)
            var candidates = new List<Transform>();
            var seenBuildings = new HashSet<Transform>();
            var districtGroups = new List<Transform>();
            var seenGroups = new HashSet<Transform>();
            string[] districtNames =
            {
                "_ShadeCanopies_And_Cover",
                "_Manufacting_District_TL",
                "_Manufacturing_District_TL",
                "_Manufacturing_District_BL",
                "_Administrative_District",
                "_Storage_District"
            };

            foreach (var dName in districtNames)
            {
                Transform grp = mapTransform != null ? mapTransform.Find(dName) : null;
                if (grp == null)
                {
                    var go = GameObject.Find(dName);
                    grp = go != null ? go.transform : null;
                }
                if (grp == null || !seenGroups.Add(grp)) continue;

                districtGroups.Add(grp);
                foreach (Transform bldg in grp)
                {
                    if (!seenBuildings.Add(bldg)) continue;
                    if (IsSealedStructure(bldg.name)) continue;
                    candidates.Add(bldg);
                }
            }

            int placed = 0;
            float[] allowedRotations = { 0f, 90f, 180f };
            var offsetFactors = new List<Vector2>
            {
                new Vector2(0.36f, 0.36f),
                new Vector2(-0.36f, -0.36f),
                new Vector2(0.36f, -0.36f),
                new Vector2(-0.36f, 0.36f),
                new Vector2(0.24f, 0.24f),
                new Vector2(-0.24f, -0.24f),
                new Vector2(0.24f, -0.24f),
                new Vector2(-0.24f, 0.24f),
                new Vector2(0.0f, 0.26f),
                new Vector2(0.0f, -0.26f),
                new Vector2(0.26f, 0.0f),
                new Vector2(-0.26f, 0.0f),
                new Vector2(0.14f, 0.14f),
                new Vector2(-0.14f, -0.14f),
                Vector2.zero
            };

            var placedPerGroup = new Dictionary<Transform, int>();

            // Place 1 to 3 (random, at most 3) chests per enterable building with random 0/90/180 Y rotation
            for (int i = 0; i < candidates.Count; i++)
            {
                var bldg = candidates[i];
                var bRenderers = bldg.GetComponentsInChildren<Renderer>();
                if (bRenderers.Length == 0) continue;

                Bounds b = bRenderers[0].bounds;
                for (int r = 1; r < bRenderers.Length; r++) b.Encapsulate(bRenderers[r].bounds);

                // Shuffle interior offsets so box placement varies naturally per building
                var shuffledOffsets = new List<Vector2>(offsetFactors);
                for (int s = shuffledOffsets.Count - 1; s > 0; s--)
                {
                    int swapIdx = UnityEngine.Random.Range(0, s + 1);
                    (shuffledOffsets[s], shuffledOffsets[swapIdx]) = (shuffledOffsets[swapIdx], shuffledOffsets[s]);
                }

                bool isKnownIndoorCover = bldg.parent != null && bldg.parent.name == "_ShadeCanopies_And_Cover";
                bool isNamedIndoor = bldg.name.Contains("Indoor");

                int desiredCountForBuilding = UnityEngine.Random.Range(1, 4); // 1, 2, or 3 (at most 3)
                int placedInBuilding = 0;
                var buildingBoxPositions = new List<Vector3>(3);

                bool prevHitBackfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;
                try
                {
                    for (int o = 0; o < shuffledOffsets.Count && placedInBuilding < desiredCountForBuilding; o++)
                    {
                        Vector2 factor = shuffledOffsets[o];
                        Vector3 interiorSample = new Vector3(b.center.x + b.extents.x * factor.x, b.min.y + 3.0f, b.center.z + b.extents.z * factor.y);

                        // 1. Raycast down from interior head/mid height to find walkable floor or terrain
                        if (!Physics.Raycast(interiorSample, Vector3.down, out RaycastHit floorHit, 8.0f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }

                        // Skip if too close to another box already placed in this building
                        bool tooClose = false;
                        for (int bp = 0; bp < buildingBoxPositions.Count; bp++)
                        {
                            if (Vector3.Distance(floorHit.point, buildingBoxPositions[bp]) < 2.1f)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                        if (tooClose) continue;

                        // 2. Confirm building actually covers overhead (known shade indoor structures OR upward ray hits building ceiling)
                        bool hasCeiling = isKnownIndoorCover || isNamedIndoor;
                        if (!hasCeiling && Physics.Raycast(floorHit.point + Vector3.up * 0.6f, Vector3.up, out RaycastHit roofHit, 20.0f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            hasCeiling = roofHit.collider != null && roofHit.collider.transform.IsChildOf(bldg);
                        }
                        if (!hasCeiling) continue;

                        // 3. Verify player can walk to/from this interior point through an open doorway (avoid trapping crates inside sealed buildings)
                        if (!isKnownIndoorCover && !IsInteriorSpotReachable(floorHit.point, bldg, b))
                        {
                            continue;
                        }

                        // 4. Ensure chest bounding box does not clip into interior walls or pillars
                        Vector3 chestBoxCenter = floorHit.point + Vector3.up * 0.42f;
                        Collider[] overlaps = Physics.OverlapBox(chestBoxCenter, new Vector3(0.32f, 0.24f, 0.32f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                        bool blocked = false;
                        for (int k = 0; k < overlaps.Length; k++)
                        {
                            if (!(overlaps[k] is TerrainCollider) && overlaps[k] != floorHit.collider)
                            {
                                blocked = true;
                                break;
                            }
                        }
                        if (blocked) continue;

                        float rotY = allowedRotations[UnityEngine.Random.Range(0, allowedRotations.Length)];
                        var chestObj = (GameObject)PrefabUtility.InstantiatePrefab(insidePrefab, root.transform);
                        chestObj.name = $"Inside_Chest_{placed + 1:00}_{bldg.name}";
                        chestObj.transform.SetPositionAndRotation(floorHit.point, Quaternion.Euler(0f, rotY, 0f));
                        placed++;
                        placedInBuilding++;
                        buildingBoxPositions.Add(floorHit.point);
                    }
                }
                finally
                {
                    Physics.queriesHitBackfaces = prevHitBackfaces;
                }

                if (bldg.parent != null && placedInBuilding > 0)
                {
                    placedPerGroup.TryGetValue(bldg.parent, out int prevGrpCount);
                    placedPerGroup[bldg.parent] = prevGrpCount + placedInBuilding;
                }
            }

            // Fallback for districts whose warehouses are physically shut (e.g., _Storage_District):
            // place 1-3 accessible crates tucked cleanly outside loading-bay walls so the district still yields loot.
            foreach (var grp in districtGroups)
            {
                if (grp.name == "_ShadeCanopies_And_Cover") continue;
                placedPerGroup.TryGetValue(grp, out int grpCount);
                if (grpCount > 0) continue;

                PlaceDistrictExteriorBayChests(insidePrefab, root.transform, grp, ref placed, allowedRotations);
            }

            return placed;
        }

        private static bool IsSealedStructure(string buildingName)
        {
            if (string.IsNullOrEmpty(buildingName)) return false;
            string n = buildingName.ToLowerInvariant();
            if (n.Contains("closed")) return true;
            // Exterior warehouse/garage shells with shut doors in this map
            return n.StartsWith("factory_02_03") || n.StartsWith("factory_02_04");
        }

        private static bool IsInteriorSpotReachable(Vector3 floorPoint, Transform bldg, Bounds b)
        {
            float maxDist = Mathf.Sqrt(b.extents.x * b.extents.x + b.extents.z * b.extents.z) + 2.0f;
            Vector3[] testSeeds =
            {
                floorPoint,
                floorPoint + new Vector3(1.5f, 0f, 0f),
                floorPoint + new Vector3(-1.5f, 0f, 0f),
                floorPoint + new Vector3(0f, 0f, 1.5f),
                floorPoint + new Vector3(0f, 0f, -1.5f)
            };

            for (int s = 0; s < testSeeds.Length; s++)
            {
                Vector3 seed = testSeeds[s];
                if (s > 0)
                {
                    if (!Physics.Raycast(seed + Vector3.up * 1.2f, Vector3.down, out RaycastHit stepHit, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    seed = stepHit.point;
                    // Keep the hop strictly inside the building footprint and unobstructed by building walls
                    if (Mathf.Abs(seed.x - b.center.x) > Mathf.Max(0.2f, b.extents.x - 0.5f) ||
                        Mathf.Abs(seed.z - b.center.z) > Mathf.Max(0.2f, b.extents.z - 0.5f))
                    {
                        continue;
                    }

                    Vector3 hopVec = seed - floorPoint;
                    float hopDist = hopVec.magnitude;
                    if (hopDist > 0.05f && HitsBuildingCollider(
                        floorPoint + Vector3.up * 0.55f, floorPoint + Vector3.up * 1.45f, 0.28f, hopVec / hopDist, hopDist, bldg))
                    {
                        continue;
                    }
                }

                Vector3 p1 = seed + Vector3.up * 0.55f;
                Vector3 p2 = seed + Vector3.up * 1.45f;
                Collider[] insideCols = Physics.OverlapCapsule(p1, p2, 0.28f, ~0, QueryTriggerInteraction.Ignore);
                bool blockedAtSeed = false;
                for (int c = 0; c < insideCols.Length; c++)
                {
                    if (insideCols[c] != null && insideCols[c].transform.IsChildOf(bldg))
                    {
                        blockedAtSeed = true;
                        break;
                    }
                }
                if (blockedAtSeed) continue;

                for (int a = 0; a < 24; a++)
                {
                    float ang = a * Mathf.PI * 2f / 24f;
                    Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                    if (!HitsBuildingCollider(p1, p2, 0.28f, dir, maxDist, bldg))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HitsBuildingCollider(Vector3 p1, Vector3 p2, float radius, Vector3 dir, float maxDist, Transform bldg)
        {
            var hits = Physics.CapsuleCastAll(p1, p2, radius, dir, maxDist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider != null && hits[i].collider.transform.IsChildOf(bldg))
                {
                    return true;
                }
            }
            return false;
        }

        private static void PlaceDistrictExteriorBayChests(GameObject insidePrefab, Transform rootTransform, Transform districtGroup, ref int placed, float[] allowedRotations)
        {
            Vector3[] outwardDirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
            foreach (Transform bldg in districtGroup)
            {
                var rends = bldg.GetComponentsInChildren<Renderer>();
                if (rends.Length == 0) continue;

                Bounds b = rends[0].bounds;
                for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);

                int desired = UnityEngine.Random.Range(1, 3); // 1-2 per warehouse exterior bay
                int countInBldg = 0;

                for (int d = 0; d < outwardDirs.Length && countInBldg < desired; d++)
                {
                    Vector3 dir = outwardDirs[d];
                    float edgeDist = Mathf.Abs(dir.x) > 0.5f ? b.extents.x : b.extents.z;
                    Vector3 sampleXZ = b.center + dir * (edgeDist + 1.15f);
                    Vector3 rayStart = new Vector3(sampleXZ.x, b.max.y + 1.0f, sampleXZ.z);

                    if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit groundHit, b.size.y + 8.0f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    if (!(groundHit.collider is TerrainCollider))
                    {
                        continue; // Must sit cleanly on walkable ground/terrain, never on roofs or props
                    }

                    Vector3 chestCenter = groundHit.point + Vector3.up * 0.45f;
                    Collider[] overlaps = Physics.OverlapBox(chestCenter, new Vector3(0.45f, 0.32f, 0.45f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                    bool blocked = false;
                    for (int k = 0; k < overlaps.Length; k++)
                    {
                        if (!(overlaps[k] is TerrainCollider) && overlaps[k] != groundHit.collider)
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) continue;

                    float rotY = allowedRotations[UnityEngine.Random.Range(0, allowedRotations.Length)];
                    var chestObj = (GameObject)PrefabUtility.InstantiatePrefab(insidePrefab, rootTransform);
                    chestObj.name = $"Inside_Chest_{placed + 1:00}_{bldg.name}";
                    chestObj.transform.SetPositionAndRotation(groundHit.point, Quaternion.Euler(0f, rotY, 0f));
                    placed++;
                    countInBldg++;
                }
            }
        }

        private static int PlaceOutsideChests(GameObject outsidePrefab)
        {
            if (outsidePrefab == null) return 0;

            var old = GameObject.Find("_LootContainers_Outside");
            if (old != null) Object.DestroyImmediate(old);

            var root = new GameObject("_LootContainers_Outside");
            Terrain terrain = Terrain.activeTerrain;

            // Perimeter walls span roughly X in [-65.3, +68.0], Z in [-84.9, +85.1].
            // Pick exterior outposts strictly outside walls while staying within terrain bounds.
            // Perimeter walls sit at North=+85.1, South=-84.9, East=-65.3, West=+68.0.
            // Terrain bounds span X:[-75,+75], Z:[-90,+90]. Place outside_Chest strictly OUTSIDE walls while staying inside Terrain!
            (Vector2 xz, float yaw)[] candidates =
            {
                (new Vector2(-32f, 87.6f), 180f), (new Vector2(8f, 87.6f), 180f), (new Vector2(44f, 87.6f), 180f),
                (new Vector2(-36f, -87.4f), 0f),  (new Vector2(12f, -87.4f), 0f), (new Vector2(48f, -87.4f), 0f),
                (new Vector2(-70.2f, -45f), 90f), (new Vector2(-70.2f, 12f), 90f), (new Vector2(-70.2f, 52f), 90f),
                (new Vector2(71.5f, -42f), -90f), (new Vector2(71.5f, 14f), -90f), (new Vector2(71.5f, 54f), -90f)
            };

            float tMinX = -73f, tMaxX = 73f, tMinZ = -88.2f, tMaxZ = 88.2f;
            if (terrain != null && terrain.terrainData != null)
            {
                Vector3 tp = terrain.transform.position;
                Vector3 ts = terrain.terrainData.size;
                tMinX = tp.x + 2.0f;
                tMaxX = tp.x + ts.x - 2.0f;
                tMinZ = tp.z + 1.8f;
                tMaxZ = tp.z + ts.z - 1.8f;
            }

            int placed = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                float x = Mathf.Clamp(candidates[i].xz.x, tMinX, tMaxX);
                float z = Mathf.Clamp(candidates[i].xz.y, tMinZ, tMaxZ);
                float y = 0f;

                if (terrain != null)
                {
                    y = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
                }
                else if (Physics.Raycast(new Vector3(x, 120f, z), Vector3.down, out RaycastHit gh, 220f, ~0, QueryTriggerInteraction.Ignore))
                {
                    y = gh.point.y;
                }

                if (Physics.Raycast(new Vector3(x, y + 1.0f, z), Vector3.up, 30f, ~0, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                Quaternion rot = Quaternion.Euler(0f, candidates[i].yaw, 0f);
                Vector3 center = new Vector3(x, y + 1.15f, z);
                var cols = Physics.OverlapBox(center, new Vector3(2.2f, 0.85f, 1.1f), rot, ~0, QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int c = 0; c < cols.Length; c++)
                {
                    if (!(cols[c] is TerrainCollider)) { blocked = true; break; }
                }
                if (blocked) continue;

                var chestObj = (GameObject)PrefabUtility.InstantiatePrefab(outsidePrefab, root.transform);
                chestObj.name = $"Outside_Chest_{placed + 1:00}";
                chestObj.transform.SetPositionAndRotation(new Vector3(x, y, z), rot);
                placed++;
            }

            return placed;
        }

        private static void EnsureContainerSpawnManager(GameObject dropPrefab)
        {
            var mgr = Object.FindAnyObjectByType<ContainerSpawnManager>();
            GameObject mgrGo = mgr != null
                ? mgr.gameObject
                : (GameObject.Find("[SCORCHED_Managers]") ?? new GameObject("[SCORCHED_Managers]"));

            if (mgrGo.GetComponent<NetworkObject>() == null) mgrGo.AddComponent<NetworkObject>();
            if (mgr == null) mgr = mgrGo.AddComponent<ContainerSpawnManager>();
            if (Object.FindAnyObjectByType<ContainerRestockManager>() == null &&
                mgrGo.GetComponent<ContainerRestockManager>() == null)
            {
                mgrGo.AddComponent<ContainerRestockManager>();
            }

            var so = new SerializedObject(mgr);
            var prop = so.FindProperty("_dropSupplyBoxPrefab");
            if (prop != null && dropPrefab != null)
            {
                prop.objectReferenceValue = dropPrefab;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(mgrGo);
        }

        private static void WireHUDInventorySprites()
        {
            var hud = Object.FindAnyObjectByType<Scorched.UI.ScorchedHUDController>();
            var catalog = ScorchedItemCatalog.Instance;
            if (hud != null && catalog != null)
            {
                var so = new SerializedObject(hud);
                var pRing = so.FindProperty("_radialProgressRingSprite");
                var pCell = so.FindProperty("_cabalSlotCellSprite");
                if (pRing != null) pRing.objectReferenceValue = catalog.RadialProgressRingSprite;
                if (pCell != null) pCell.objectReferenceValue = catalog.CabalSlotCellSprite;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(hud);
            }
        }

        private static void EnsureWeaponItemInCatalog()
        {
            var catalog = Resources.Load<ScorchedItemCatalog>("ScorchedItemCatalog");
            if (catalog == null)
            {
                Debug.LogWarning("[ContainerPlacementUtility] Resources/ScorchedItemCatalog not found.");
                return;
            }

            Sprite icon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/UI/ScorchedInventory/ak47_part_receiver.png");
            var existing = catalog.GetDefinition(ItemId.Weapon_AK47);
            if (existing == null)
            {
                existing = new ItemDefinition();
                catalog.Items.Add(existing);
            }

            existing.Id = ItemId.Weapon_AK47;
            existing.DisplayName = "AK-47 Tactical";
            existing.ShortTag = "AK-47";
            existing.Category = ItemCategory.Weapon;
            existing.Grade = ItemGrade.Great;
            existing.GridWidth = 2;
            existing.GridHeight = 1;
            existing.IsStackable = false;
            existing.MaxStack = 1;
            existing.RestoreAmount = 0f;
            existing.UpgradesToId = ItemId.None;
            if (icon != null) existing.Icon = icon;

            catalog.RebuildLookup();
            EditorUtility.SetDirty(catalog);
        }

        private static int WirePlayerPrefabs(GameObject deathBagPrefab = null)
        {
            if (deathBagPrefab == null)
            {
                deathBagPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DeathBagPath);
            }

            string[] candidatePaths =
            {
                "Assets/Prefabs/Player.prefab",
                "Assets/Prefabs/Player/Player.prefab",
                "Assets/Prefabs/ScorchedPlayer.prefab",
                "Assets/Prefabs/Player/ScorchedPlayer.prefab"
            };

            int count = 0;
            foreach (var p in candidatePaths)
            {
                if (!File.Exists(p)) continue;
                using (var scope = new PrefabUtility.EditPrefabContentsScope(p))
                {
                    var root = scope.prefabContentsRoot;
                    if (root.GetComponent<PlayerInventoryController>() == null)
                        root.AddComponent<PlayerInventoryController>();
                    if (root.GetComponent<PlayerScavengeInteractor>() == null)
                        root.AddComponent<PlayerScavengeInteractor>();

                    var phc = root.GetComponent<Scorched.Player.PlayerHealthController>();
                    if (phc != null && deathBagPrefab != null)
                    {
                        var so = new SerializedObject(phc);
                        var prop = so.FindProperty("_deathBagPrefab");
                        if (prop != null)
                        {
                            prop.objectReferenceValue = deathBagPrefab;
                            so.ApplyModifiedPropertiesWithoutUndo();
                        }
                    }
                }
                count++;
            }

            // Also wire any live Player in scene if present
            foreach (var pm in Object.FindObjectsByType<Scorched.Player.PlayerMovement>(FindObjectsSortMode.None))
            {
                if (pm.gameObject.GetComponent<PlayerInventoryController>() == null)
                    pm.gameObject.AddComponent<PlayerInventoryController>();
                if (pm.gameObject.GetComponent<PlayerScavengeInteractor>() == null)
                    pm.gameObject.AddComponent<PlayerScavengeInteractor>();

                var phc = pm.gameObject.GetComponent<Scorched.Player.PlayerHealthController>();
                if (phc != null && deathBagPrefab != null)
                {
                    var so = new SerializedObject(phc);
                    var prop = so.FindProperty("_deathBagPrefab");
                    if (prop != null)
                    {
                        prop.objectReferenceValue = deathBagPrefab;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                EditorUtility.SetDirty(pm.gameObject);
            }

            return count;
        }
    }
}
