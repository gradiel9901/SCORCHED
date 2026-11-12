using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Combat;
using Scorched.Inventory;
using Scorched.Player;
using Scorched.Survival;

namespace Scorched.Environment.Editor
{
    public struct CollisionScanTelemetry
    {
        public int TotalStructuresScanned;
        public int MissingCollidersFixed;
        public int TrappingBoxCollidersRemoved;
        public int MismatchedProxyMeshesReplaced;
        public int ConvexTrapsFixed;
        public int LodCollidersNormalized;
        public List<string> SampleLogs;
    }

    public static class GloveTightCollisionProcessor
    {
        private const string PersistentColliderPrefix = "_GloveCollider_LOD0";
        private const MeshColliderCookingOptions GloveCookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices;

        public static ScorchedCollisionManager EnsureManagerInScene()
        {
            var manager = UnityEngine.Object.FindAnyObjectByType<ScorchedCollisionManager>();
            if (manager != null) return manager;

            var go = new GameObject("_ScorchedCollisionManager");
            manager = go.AddComponent<ScorchedCollisionManager>();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return manager;
        }

        [MenuItem("SCORCHED/Scan & Apply Glove-Tight Collisions")]
        public static void ScanAndApplyFromMenu()
        {
            var manager = EnsureManagerInScene();
            ScanScene(manager, applyFixes: true);
        }

        public static CollisionScanTelemetry ScanScene(ScorchedCollisionManager manager, bool applyFixes)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            return ScanRoots(manager, roots, applyFixes, updateTelemetry: true);
        }

        public static CollisionScanTelemetry ScanHierarchy(ScorchedCollisionManager manager, GameObject root, bool applyFixes)
        {
            if (root == null) return new CollisionScanTelemetry { SampleLogs = new List<string>() };
            return ScanRoots(manager, new[] { root }, applyFixes, updateTelemetry: false);
        }

        private static CollisionScanTelemetry ScanRoots(
            ScorchedCollisionManager manager,
            IReadOnlyList<GameObject> roots,
            bool applyFixes,
            bool updateTelemetry)
        {
            var stats = new CollisionScanTelemetry
            {
                SampleLogs = new List<string>(64)
            };

            LayerMask ignoreMask = manager != null ? manager.IgnoreLayers : default;
            float minMaxBounds = manager != null ? Mathf.Max(0.005f, manager.MinMaxBoundsSize) : 0.02f;
            int minVerts = manager != null ? Mathf.Max(3, manager.MinVertexCount) : 3;

            var handledObjects = new HashSet<GameObject>();

            for (int r = 0; r < roots.Count; r++)
            {
                GameObject root = roots[r];
                if (root == null || !root.activeInHierarchy) continue;

                // Pass A: LODGroup Structures (LOD 0 Only Glove-Tight Binding)
                var lodGroups = root.GetComponentsInChildren<LODGroup>(true);
                for (int i = 0; i < lodGroups.Length; i++)
                {
                    LODGroup lg = lodGroups[i];
                    if (lg == null || !lg.gameObject.activeInHierarchy) continue;
                    if (ShouldSkipHierarchy(lg.gameObject, ignoreMask)) continue;

                    ProcessLODGroup(lg, minVerts, minMaxBounds, applyFixes, handledObjects, ref stats);
                }

                // Pass B: Standalone Structural MeshRenderers (No parent LODGroup or not handled above)
                var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    MeshRenderer mr = renderers[i];
                    if (mr == null || !mr.gameObject.activeInHierarchy) continue;
                    if (handledObjects.Contains(mr.gameObject)) continue;
                    if (ShouldSkipHierarchy(mr.gameObject, ignoreMask)) continue;

                    // Skip orphan lower-LOD sub-meshes even if disconnected from an LODGroup
                    if (IsLowerLodName(mr.name)) continue;

                    MeshFilter mf = mr.GetComponent<MeshFilter>();
                    if (!IsValidStructuralMesh(mr.gameObject, mf, minVerts, minMaxBounds)) continue;

                    handledObjects.Add(mr.gameObject);
                    stats.TotalStructuresScanned++;
                    NormalizeStructuralObject(mr.gameObject, mf.sharedMesh, applyFixes, ref stats);
                }
            }

            if (manager != null && updateTelemetry)
            {
                manager.TotalStructuresScanned = stats.TotalStructuresScanned;
                manager.MissingCollidersFixed = stats.MissingCollidersFixed;
                manager.TrappingBoxCollidersRemoved = stats.TrappingBoxCollidersRemoved;
                manager.MismatchedProxyMeshesReplaced = stats.MismatchedProxyMeshesReplaced;
                manager.ConvexTrapsFixed = stats.ConvexTrapsFixed;
                manager.LodCollidersNormalized = stats.LodCollidersNormalized;
                manager.RecentFixLog = stats.SampleLogs;
                string modeLabel = applyFixes ? "APPLIED" : "AUDIT ONLY";
                manager.LastScanSummary =
                    $"[{modeLabel} {DateTime.Now:HH:mm:ss}] Scanned={stats.TotalStructuresScanned}, MissingFixed={stats.MissingCollidersFixed}, BoxRemoved={stats.TrappingBoxCollidersRemoved}, ProxyReplaced={stats.MismatchedProxyMeshesReplaced}, ConvexFixed={stats.ConvexTrapsFixed}, LODNormalized={stats.LodCollidersNormalized}";
                EditorUtility.SetDirty(manager);
            }

            if (applyFixes && updateTelemetry)
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }

            return stats;
        }

        private static void ProcessLODGroup(
            LODGroup lg,
            int minVerts,
            float minThickness,
            bool applyFixes,
            HashSet<GameObject> handledObjects,
            ref CollisionScanTelemetry stats)
        {
            LOD[] lods = lg.GetLODs();
            if (lods == null || lods.Length == 0) return;

            var lod0Set = new HashSet<GameObject>();
            var lod0List = new List<MeshRenderer>(4);

            Renderer[] lod0Renderers = lods[0].renderers;
            if (lod0Renderers != null)
            {
                for (int i = 0; i < lod0Renderers.Length; i++)
                {
                    if (lod0Renderers[i] is MeshRenderer mr && mr != null)
                    {
                        lod0Set.Add(mr.gameObject);
                        lod0List.Add(mr);
                    }
                }
            }

            // Fallback if LOD 0 array was empty
            if (lod0List.Count == 0)
            {
                var allMr = lg.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < allMr.Length; i++)
                {
                    if (allMr[i] != null && !IsLowerLodName(allMr[i].name))
                    {
                        lod0Set.Add(allMr[i].gameObject);
                        lod0List.Add(allMr[i]);
                    }
                }
            }

            if (lod0List.Count == 0) return;

            stats.TotalStructuresScanned++;

            // 1. Remove non-trigger Box/Capsule/Sphere colliders on LODGroup root itself, plus root MeshColliders if root is not in LOD0
            if (!lod0Set.Contains(lg.gameObject))
            {
                StripNonTriggerColliders(lg.gameObject, applyFixes, ref stats, countAsBoxIfPrimitive: true, countAsLodNormalized: true);
            }

            // 2. Strip any non-trigger colliders from lower LODs (LOD 1, LOD 2, LOD 3, etc.)
            for (int lodIdx = 1; lodIdx < lods.Length; lodIdx++)
            {
                Renderer[] lowerRenderers = lods[lodIdx].renderers;
                if (lowerRenderers == null) continue;
                for (int r = 0; r < lowerRenderers.Length; r++)
                {
                    if (lowerRenderers[r] == null) continue;
                    GameObject lowerGo = lowerRenderers[r].gameObject;
                    if (lod0Set.Contains(lowerGo)) continue;
                    handledObjects.Add(lowerGo);
                    StripNonTriggerColliders(lowerGo, applyFixes, ref stats, countAsBoxIfPrimitive: true, countAsLodNormalized: true);
                }
            }

            // 3. Apply 1:1 MeshCollider to each valid LOD 0 renderer (persistent child holder if LOD 0 GO is inactive)
            for (int i = 0; i < lod0List.Count; i++)
            {
                MeshRenderer mr = lod0List[i];
                if (mr == null) continue;
                handledObjects.Add(mr.gameObject);

                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (!IsValidStructuralMesh(mr.gameObject, mf, minVerts, minThickness)) continue;

                if (mr.gameObject.activeSelf)
                {
                    // Remove any old persistent proxy holder if LOD0 itself stays active
                    Transform staleHolder = lg.transform.Find($"{PersistentColliderPrefix}_{mr.name}");
                    if (staleHolder != null && applyFixes)
                    {
                        UnityEngine.Object.DestroyImmediate(staleHolder.gameObject);
                    }
                    NormalizeStructuralObject(mr.gameObject, mf.sharedMesh, applyFixes, ref stats);
                }
                else
                {
                    // LOD0 GO is disabled; ensure persistent active collider holder keeps exact LOD0 collision at all distances
                    StripNonTriggerColliders(mr.gameObject, applyFixes, ref stats, countAsBoxIfPrimitive: true, countAsLodNormalized: false);
                    string holderName = $"{PersistentColliderPrefix}_{mr.name}";
                    Transform holderTr = lg.transform.Find(holderName);
                    if (holderTr == null)
                    {
                        if (!applyFixes)
                        {
                            stats.MissingCollidersFixed++;
                            RecordLog(ref stats, $"Missing persistent LOD0 collider holder: {lg.name}/{mr.name}");
                            continue;
                        }
                        var holderGo = new GameObject(holderName);
                        holderGo.layer = mr.gameObject.layer;
                        holderTr = holderGo.transform;
                        holderTr.SetParent(lg.transform, false);
                    }
                    if (applyFixes)
                    {
                        holderTr.position = mr.transform.position;
                        holderTr.rotation = mr.transform.rotation;
                        // Match local lossyScale cleanly via world-to-local on parent
                        Vector3 parentScale = lg.transform.lossyScale;
                        Vector3 mrScale = mr.transform.lossyScale;
                        holderTr.localScale = new Vector3(
                            Mathf.Abs(parentScale.x) > 1e-5f ? mrScale.x / parentScale.x : 1f,
                            Mathf.Abs(parentScale.y) > 1e-5f ? mrScale.y / parentScale.y : 1f,
                            Mathf.Abs(parentScale.z) > 1e-5f ? mrScale.z / parentScale.z : 1f);
                    }
                    NormalizeStructuralObject(holderTr.gameObject, mf.sharedMesh, applyFixes, ref stats);
                }
            }
        }

        private static void NormalizeStructuralObject(GameObject targetGo, Mesh visualMesh, bool applyFixes, ref CollisionScanTelemetry stats)
        {
            // Remove doorway-blocking primitive colliders (BoxCollider / CapsuleCollider / SphereCollider)
            var primitiveCols = targetGo.GetComponents<Collider>();
            MeshCollider existingMc = null;
            var extraMeshCols = new List<MeshCollider>();

            for (int i = 0; i < primitiveCols.Length; i++)
            {
                Collider c = primitiveCols[i];
                if (c == null || c.isTrigger) continue;

                if (c is BoxCollider || c is CapsuleCollider || c is SphereCollider)
                {
                    stats.TrappingBoxCollidersRemoved++;
                    RecordLog(ref stats, $"Removed {c.GetType().Name} on {targetGo.name}");
                    if (applyFixes) UnityEngine.Object.DestroyImmediate(c);
                }
                else if (c is MeshCollider mc)
                {
                    if (existingMc == null) existingMc = mc;
                    else extraMeshCols.Add(mc);
                }
            }

            if (applyFixes)
            {
                for (int i = 0; i < extraMeshCols.Count; i++)
                {
                    UnityEngine.Object.DestroyImmediate(extraMeshCols[i]);
                }
            }

            if (existingMc == null)
            {
                stats.MissingCollidersFixed++;
                RecordLog(ref stats, $"Added 1:1 MeshCollider on {targetGo.name} ({visualMesh.name})");
                if (applyFixes)
                {
                    existingMc = targetGo.AddComponent<MeshCollider>();
                    existingMc.sharedMesh = visualMesh;
                    existingMc.convex = false;
                    existingMc.cookingOptions = GloveCookingOptions;
                    existingMc.isTrigger = false;
                    existingMc.enabled = true;
                }
                return;
            }

            bool meshMismatched = existingMc.sharedMesh != visualMesh;
            bool wasConvexTrap = existingMc.convex;
            bool wrongCooking = existingMc.cookingOptions != GloveCookingOptions;

            if (meshMismatched)
            {
                string prevName = existingMc.sharedMesh != null ? existingMc.sharedMesh.name : "NULL";
                if (existingMc.sharedMesh == null) stats.MissingCollidersFixed++;
                else stats.MismatchedProxyMeshesReplaced++;
                RecordLog(ref stats, $"Fixed MeshCollider on {targetGo.name}: {prevName} -> {visualMesh.name}");
            }

            if (wasConvexTrap)
            {
                stats.ConvexTrapsFixed++;
                RecordLog(ref stats, $"Disabled convex trap on {targetGo.name}");
            }

            if (applyFixes && (meshMismatched || wasConvexTrap || wrongCooking || !existingMc.enabled))
            {
                existingMc.sharedMesh = visualMesh;
                existingMc.convex = false;
                existingMc.cookingOptions = GloveCookingOptions;
                existingMc.isTrigger = false;
                existingMc.enabled = true;
            }
        }

        private static void StripNonTriggerColliders(
            GameObject go,
            bool applyFixes,
            ref CollisionScanTelemetry stats,
            bool countAsBoxIfPrimitive,
            bool countAsLodNormalized)
        {
            var cols = go.GetComponents<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.isTrigger) continue;

                if (countAsBoxIfPrimitive && (c is BoxCollider || c is CapsuleCollider || c is SphereCollider))
                {
                    stats.TrappingBoxCollidersRemoved++;
                }
                else if (countAsLodNormalized)
                {
                    stats.LodCollidersNormalized++;
                }

                if (applyFixes)
                {
                    UnityEngine.Object.DestroyImmediate(c);
                }
            }
        }

        private static bool ShouldSkipHierarchy(GameObject go, LayerMask ignoreMask)
        {
            if (go == null) return true;
            if (((1 << go.layer) & ignoreMask.value) != 0) return true;

            // Skip runtime/gameplay entities, triggers, loot containers, terrain, cameras, lights, UI
            if (go.GetComponentInParent<PlayerHealthController>() != null) return true;
            if (go.GetComponentInParent<PlayerMovement>() != null) return true;
            if (go.GetComponentInParent<CharacterController>() != null) return true;
            if (go.GetComponentInParent<BodyPartHitbox>() != null) return true;
            if (go.GetComponentInParent<LootContainerInteractable>() != null) return true;
            if (go.GetComponentInParent<WorldItemPickup>() != null) return true;
            if (go.GetComponentInParent<ShadeZoneBuilding>() != null && HasOnlyTriggerHierarchy(go)) return true;
            if (go.GetComponentInParent<Terrain>() != null) return true;
            if (go.GetComponentInParent<Camera>() != null) return true;
            if (go.GetComponentInParent<Light>() != null) return true;
            if (go.GetComponentInParent<Canvas>() != null) return true;
            if (go.GetComponent<ParticleSystem>() != null) return true;

            return false;
        }

        private static bool HasOnlyTriggerHierarchy(GameObject go)
        {
            return go.GetComponent<MeshRenderer>() == null && go.GetComponent<MeshFilter>() == null;
        }

        private static bool IsValidStructuralMesh(GameObject go, MeshFilter mf, int minVerts, float minMaxBounds)
        {
            if (mf == null || mf.sharedMesh == null) return false;
            Mesh mesh = mf.sharedMesh;
            if (mesh.vertexCount < minVerts) return false;

            string lower = go.name.ToLowerInvariant();
            if (lower.Contains("decal") || lower.Contains("puddle") || lower.Contains("grass") || lower.Contains("foliage"))
            {
                return false;
            }

            Vector3 size = mesh.bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (maxAxis < minMaxBounds) return false;

            return true;
        }

        private static bool IsLowerLodName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.EndsWith("_LOD1", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD2", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD3", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD4", StringComparison.OrdinalIgnoreCase);
        }

        private static void RecordLog(ref CollisionScanTelemetry stats, string entry)
        {
            if (stats.SampleLogs != null && stats.SampleLogs.Count < 80)
            {
                stats.SampleLogs.Add(entry);
            }
        }
    }

    [CustomEditor(typeof(ScorchedCollisionManager))]
    public class ScorchedCollisionManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var manager = (ScorchedCollisionManager)target;
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("SCORCHED // Glove-Tight Collision Manager", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Enforces 1:1 visual MeshCollider binding on LOD0 structures, removes trapping BoxColliders & convex hulls, and preserves gameplay triggers and containers.",
                MessageType.Info);

            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Only (Audit)", GUILayout.Height(30)))
            {
                GloveTightCollisionProcessor.ScanScene(manager, applyFixes: false);
            }

            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.28f, 0.78f, 0.44f, 1f);
            if (GUILayout.Button("Scan & Apply Glove-Tight Collisions", GUILayout.Height(30)))
            {
                GloveTightCollisionProcessor.ScanScene(manager, applyFixes: true);
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Open Collision Manager Window", GUILayout.Height(24)))
            {
                ScorchedCollisionManagerWindow.OpenWindow();
            }

            if (!string.IsNullOrEmpty(manager.LastScanSummary))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(manager.LastScanSummary, MessageType.None);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }

    public class ScorchedCollisionManagerWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("SCORCHED/Collision Manager Window")]
        public static void OpenWindow()
        {
            var win = GetWindow<ScorchedCollisionManagerWindow>("Collision Manager");
            win.minSize = new Vector2(460f, 380f);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("SCORCHED // Glove-Tight Collision Manager", EditorStyles.boldLabel);

            var manager = UnityEngine.Object.FindAnyObjectByType<ScorchedCollisionManager>();
            if (manager == null)
            {
                EditorGUILayout.HelpBox("No ScorchedCollisionManager found in the active scene.", MessageType.Warning);
                if (GUILayout.Button("Create _ScorchedCollisionManager in Scene", GUILayout.Height(28)))
                {
                    manager = GloveTightCollisionProcessor.EnsureManagerInScene();
                }
                return;
            }

            EditorGUI.BeginChangeCheck();
            bool autoFix = EditorGUILayout.Toggle("Auto-Fix New Objects In Editor", manager.AutoFixNewObjectsInEditor);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(manager, "Toggle AutoFixNewObjectsInEditor");
                manager.AutoFixNewObjectsInEditor = autoFix;
                EditorUtility.SetDirty(manager);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Manager in Hierarchy", GUILayout.Height(24)))
            {
                Selection.activeGameObject = manager.gameObject;
                EditorGUIUtility.PingObject(manager.gameObject);
            }
            if (GUILayout.Button("Scan Only (Audit)", GUILayout.Height(24)))
            {
                GloveTightCollisionProcessor.ScanScene(manager, applyFixes: false);
            }
            EditorGUILayout.EndHorizontal();

            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.28f, 0.78f, 0.44f, 1f);
            if (GUILayout.Button("Scan & Apply Glove-Tight Collisions", GUILayout.Height(34)))
            {
                GloveTightCollisionProcessor.ScanScene(manager, applyFixes: true);
            }
            GUI.backgroundColor = prev;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Last Scan Telemetry", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"Structures Scanned: {manager.TotalStructuresScanned}\n" +
                $"Missing / Null Colliders Fixed: {manager.MissingCollidersFixed}\n" +
                $"Trapping Box/Primitive Colliders Removed: {manager.TrappingBoxCollidersRemoved}\n" +
                $"Mismatched Proxy Meshes Replaced: {manager.MismatchedProxyMeshesReplaced}\n" +
                $"Convex Traps Fixed: {manager.ConvexTrapsFixed}\n" +
                $"Lower LOD Colliders Cleaned: {manager.LodCollidersNormalized}\n\n" +
                manager.LastScanSummary,
                MessageType.None);

            if (manager.RecentFixLog != null && manager.RecentFixLog.Count > 0)
            {
                EditorGUILayout.LabelField($"Sample Audit / Fix Entries ({manager.RecentFixLog.Count})", EditorStyles.boldLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                for (int i = 0; i < manager.RecentFixLog.Count; i++)
                {
                    EditorGUILayout.LabelField("• " + manager.RecentFixLog[i], EditorStyles.miniLabel);
                }
                EditorGUILayout.EndScrollView();
            }
        }
    }

    [InitializeOnLoad]
    public static class ScorchedCollisionWatchdog
    {
        private static readonly HashSet<EntityId> PendingRoots = new HashSet<EntityId>();
        private static bool _scheduled = false;

        static ScorchedCollisionWatchdog()
        {
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
        }

        private static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var manager = UnityEngine.Object.FindAnyObjectByType<ScorchedCollisionManager>();
            if (manager == null || !manager.AutoFixNewObjectsInEditor) return;

            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) == ObjectChangeKind.CreateGameObjectHierarchy)
                {
                    stream.GetCreateGameObjectHierarchyEvent(i, out var evt);
                    PendingRoots.Add(evt.entityId);
                }
            }

            if (PendingRoots.Count > 0 && !_scheduled)
            {
                _scheduled = true;
                EditorApplication.delayCall += ProcessPendingRoots;
            }
        }

        private static void ProcessPendingRoots()
        {
            _scheduled = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                PendingRoots.Clear();
                return;
            }

            var manager = UnityEngine.Object.FindAnyObjectByType<ScorchedCollisionManager>();
            if (manager == null || !manager.AutoFixNewObjectsInEditor)
            {
                PendingRoots.Clear();
                return;
            }

            foreach (EntityId entityId in PendingRoots)
            {
                var obj = EditorUtility.EntityIdToObject(entityId) as GameObject;
                if (obj != null && obj.scene.IsValid())
                {
                    GloveTightCollisionProcessor.ScanHierarchy(manager, obj, applyFixes: true);
                }
            }
            PendingRoots.Clear();
        }
    }
}
