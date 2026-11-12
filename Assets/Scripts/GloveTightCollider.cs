using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Combat;
using Scorched.Inventory;
using Scorched.Player;
using Scorched.Survival;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

namespace Scorched.Environment
{
    /// <summary>
    /// Enforces mathematically exact 1:1 LOD0 MeshCollider physics on buildings, walls, and props
    /// in both Editor and Windows Standalone builds by keeping source meshes readable, disabling
    /// Static Batching on attached structures, removing trapping BoxColliders, and locking overrides.
    /// </summary>
    [DisallowMultipleComponent]
    public class GloveTightCollider : MonoBehaviour
    {
        [System.Serializable]
        public struct BoundColliderEntry
        {
            public MeshCollider Collider;
            public Mesh VisualMesh;
        }

        [SerializeField] private List<BoundColliderEntry> _boundColliders = new List<BoundColliderEntry>();

        public int BoundCount => _boundColliders != null ? _boundColliders.Count : 0;

        private void Awake()
        {
            VerifyBindingsRuntime();
        }

        public void VerifyBindingsRuntime()
        {
            if (_boundColliders == null) return;

            for (int i = 0; i < _boundColliders.Count; i++)
            {
                BoundColliderEntry entry = _boundColliders[i];
                if (entry.Collider == null) continue;

                Mesh resolvedMesh = entry.VisualMesh;
                if (resolvedMesh == null)
                {
                    MeshFilter mf = entry.Collider.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && !mf.sharedMesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
                    {
                        resolvedMesh = mf.sharedMesh;
                    }
                }

                if (resolvedMesh != null && entry.Collider.sharedMesh != resolvedMesh)
                {
                    entry.Collider.sharedMesh = resolvedMesh;
                }

                if (entry.Collider.convex) entry.Collider.convex = false;
                if (entry.Collider.isTrigger) entry.Collider.isTrigger = false;
                if (!entry.Collider.enabled) entry.Collider.enabled = true;
            }
        }

        public void SetBindingsEditor(List<BoundColliderEntry> bindings)
        {
            _boundColliders = bindings ?? new List<BoundColliderEntry>();
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(GloveTightCollider))]
    public class GloveTightColliderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            GloveTightCollider comp = (GloveTightCollider)target;
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox($"Glove-Tight Colliders Bound: {comp.BoundCount}", MessageType.Info);

            if (GUILayout.Button("Re-Bake This Structure (1:1 Readable + No Static Batching)", GUILayout.Height(28)))
            {
                var report = GloveTightBuildPreprocessor.ProcessSingleRoot(comp.gameObject, saveScene: true);
                Debug.Log(report);
            }
        }
    }

    public class GloveTightBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private const string TargetScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";
        private const string PersistentColliderPrefix = "_GloveCollider_LOD0";
        private const MeshColliderCookingOptions GloveCookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices;

        public void OnPreprocessBuild(BuildReport report)
        {
            int reimportedModels = EnsureAllSceneAndStructureMeshesReadable();
            Debug.Log($"[GloveTightBuildPreprocessor] Pre-build mesh readability pass complete. ModelsReimported={reimportedModels}");
        }

        [MenuItem("SCORCHED/Attach GloveTightCollider To All Scene Structures & Save")]
        public static void AttachAllFromMenu()
        {
            string summary = AttachAndBakeAllSceneStructures();
            Debug.Log(summary);
        }

        public static string AttachAndBakeAllSceneStructures()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != TargetScenePath && System.IO.File.Exists(TargetScenePath))
            {
                scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }

            // 1. Collect all unique candidate structure roots + ensure all meshes used for collision are readable first.
            int reimportedModels = EnsureAllSceneAndStructureMeshesReadable();

            GameObject[] roots = scene.GetRootGameObjects();
            var handledRenderers = new HashSet<GameObject>();
            var attachedHosts = new HashSet<GloveTightCollider>();

            int structuresAttached = 0;
            int collidersBound = 0;
            int unpackedPrefabs = 0;
            int boxCollidersRemoved = 0;
            int batchingClearedCount = 0;

            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null || ShouldSkipHierarchy(root)) continue;

                // Determine structural sub-roots: children of district/environment containers, or LODGroups/MeshRenderers
                var lodGroups = root.GetComponentsInChildren<LODGroup>(true);
                for (int i = 0; i < lodGroups.Length; i++)
                {
                    LODGroup lg = lodGroups[i];
                    if (lg == null || ShouldSkipHierarchy(lg.gameObject)) continue;

                    GameObject targetHost = ResolveHostObject(lg.gameObject);
                    unpackedPrefabs += UnpackPrefabRootIfTrappingPrimitives(targetHost);

                    var bindings = new List<GloveTightCollider.BoundColliderEntry>();
                    ProcessLODGroup(
                        lg,
                        handledRenderers,
                        bindings,
                        ref boxCollidersRemoved,
                        ref batchingClearedCount);

                    if (bindings.Count > 0)
                    {
                        var gtc = AttachOrMergeGloveTight(targetHost, bindings, attachedHosts, ref structuresAttached);
                        collidersBound += bindings.Count;
                        batchingClearedCount += DisableBatchingStaticRecursive(targetHost);
                        EditorUtility.SetDirty(gtc);
                    }
                }

                // Standalone structural MeshRenderers not inside an LODGroup
                var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < meshRenderers.Length; i++)
                {
                    MeshRenderer mr = meshRenderers[i];
                    if (mr == null || handledRenderers.Contains(mr.gameObject)) continue;
                    if (ShouldSkipHierarchy(mr.gameObject)) continue;
                    if (IsLowerLodName(mr.name)) continue;

                    MeshFilter mf = mr.GetComponent<MeshFilter>();
                    if (!IsValidStructuralMesh(mr.gameObject, mf)) continue;

                    handledRenderers.Add(mr.gameObject);

                    // Attach GloveTightCollider on top-level building/prop container under the scene district root if reasonable, else self
                    GameObject host = ResolveHostObject(mr.gameObject);
                    unpackedPrefabs += UnpackPrefabRootIfTrappingPrimitives(host);

                    var entryList = new List<GloveTightCollider.BoundColliderEntry>(1);
                    var mc = NormalizeStructuralObject(mr.gameObject, mf.sharedMesh, ref boxCollidersRemoved);
                    if (mc != null)
                    {
                        entryList.Add(new GloveTightCollider.BoundColliderEntry { Collider = mc, VisualMesh = mf.sharedMesh });
                        var gtc = AttachOrMergeGloveTight(host, entryList, attachedHosts, ref structuresAttached);
                        collidersBound++;
                        batchingClearedCount += DisableBatchingStatic(mr.gameObject);
                        batchingClearedCount += DisableBatchingStatic(host);
                        EditorUtility.SetDirty(gtc);
                    }
                }
            }

            // Ensure no active MeshCollider host retains BatchingStatic
            foreach (var mc in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include))
            {
                if (mc != null && !mc.isTrigger)
                {
                    batchingClearedCount += DisableBatchingStatic(mc.gameObject);
                }
            }

            // Re-run EnsureAllSceneAndStructureMeshesReadable in case newly linked sharedMeshes were found
            reimportedModels += EnsureAllSceneAndStructureMeshesReadable();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            return $"[GloveTightCollider] Baked & Saved '{scene.path}': ModelsSetReadable={reimportedModels}, StructuresWithGloveTight={structuresAttached}, BoundMeshColliders={collidersBound}, PrefabsUnpacked={unpackedPrefabs}, BoxCollidersRemoved={boxCollidersRemoved}, BatchingStaticDisabled={batchingClearedCount}";
        }

        public static string ProcessSingleRoot(GameObject targetRoot, bool saveScene)
        {
            if (targetRoot == null) return "[GloveTightCollider] Target root is null.";

            int unpacked = UnpackPrefabRootIfTrappingPrimitives(targetRoot);
            var handled = new HashSet<GameObject>();
            var hosts = new HashSet<GloveTightCollider>();
            int attached = 0, boxRemoved = 0, batchCleared = 0;
            var allBindings = new List<GloveTightCollider.BoundColliderEntry>();

            foreach (var lg in targetRoot.GetComponentsInChildren<LODGroup>(true))
            {
                if (lg == null || ShouldSkipHierarchy(lg.gameObject)) continue;
                ProcessLODGroup(lg, handled, allBindings, ref boxRemoved, ref batchCleared);
            }

            foreach (var mr in targetRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null || handled.Contains(mr.gameObject) || ShouldSkipHierarchy(mr.gameObject)) continue;
                if (IsLowerLodName(mr.name)) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (!IsValidStructuralMesh(mr.gameObject, mf)) continue;

                handled.Add(mr.gameObject);
                var mc = NormalizeStructuralObject(mr.gameObject, mf.sharedMesh, ref boxRemoved);
                if (mc != null)
                {
                    allBindings.Add(new GloveTightCollider.BoundColliderEntry { Collider = mc, VisualMesh = mf.sharedMesh });
                }
            }

            var gtc = AttachOrMergeGloveTight(targetRoot, allBindings, hosts, ref attached);
            batchCleared += DisableBatchingStaticRecursive(targetRoot);
            EditorUtility.SetDirty(gtc);

            int modelsReadable = EnsureAllSceneAndStructureMeshesReadable();
            if (saveScene)
            {
                Scene active = SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(active);
                EditorSceneManager.SaveScene(active);
                AssetDatabase.SaveAssets();
            }

            return $"[GloveTightCollider] SingleRoot '{targetRoot.name}': Bound={allBindings.Count}, ModelsReadable={modelsReadable}, Unpacked={unpacked}, BoxRemoved={boxRemoved}, BatchingCleared={batchCleared}";
        }

        private static GameObject ResolveHostObject(GameObject structuralGo)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(structuralGo))
            {
                GameObject pRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(structuralGo);
                if (pRoot != null && !ShouldSkipHierarchy(pRoot)) return pRoot;
            }
            var parentLod = structuralGo.GetComponentInParent<LODGroup>();
            if (parentLod != null && !ShouldSkipHierarchy(parentLod.gameObject))
            {
                return parentLod.gameObject;
            }
            return structuralGo;
        }

        private static int UnpackPrefabRootIfTrappingPrimitives(GameObject host)
        {
            if (host == null || !PrefabUtility.IsPartOfPrefabInstance(host)) return 0;

            bool needsUnpack = false;
            foreach (var col in host.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.isTrigger) continue;
                if (col is BoxCollider || col is CapsuleCollider || col is SphereCollider)
                {
                    if (col.GetComponentInParent<LootContainerInteractable>() == null &&
                        col.GetComponentInParent<WorldItemPickup>() == null &&
                        col.GetComponentInParent<PlayerHealthController>() == null)
                    {
                        needsUnpack = true;
                        break;
                    }
                }
            }

            if (!needsUnpack) return 0;

            GameObject outerRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(host);
            if (outerRoot == null) return 0;

            // Preserve intentional gameplay prefabs intact if somehow nested
            if (outerRoot.GetComponentInChildren<LootContainerInteractable>(true) != null ||
                outerRoot.GetComponent<PlayerHealthController>() != null)
            {
                return 0;
            }

            PrefabUtility.UnpackPrefabInstance(outerRoot, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            return 1;
        }

        private static GloveTightCollider AttachOrMergeGloveTight(
            GameObject host,
            List<GloveTightCollider.BoundColliderEntry> newEntries,
            HashSet<GloveTightCollider> alreadyResetHosts,
            ref int structuresAttached)
        {
            GloveTightCollider gtc = host.GetComponent<GloveTightCollider>();
            if (gtc == null)
            {
                gtc = host.AddComponent<GloveTightCollider>();
                structuresAttached++;
            }

            var merged = new List<GloveTightCollider.BoundColliderEntry>();
            if (!alreadyResetHosts.Contains(gtc))
            {
                alreadyResetHosts.Add(gtc);
            }
            else
            {
                var so = new SerializedObject(gtc);
                var prop = so.FindProperty("_boundColliders");
                if (prop != null && prop.isArray)
                {
                    for (int i = 0; i < prop.arraySize; i++)
                    {
                        var elem = prop.GetArrayElementAtIndex(i);
                        var col = elem.FindPropertyRelative("Collider")?.objectReferenceValue as MeshCollider;
                        var msh = elem.FindPropertyRelative("VisualMesh")?.objectReferenceValue as Mesh;
                        if (col != null)
                        {
                            merged.Add(new GloveTightCollider.BoundColliderEntry { Collider = col, VisualMesh = msh });
                        }
                    }
                }
            }

            var seen = new HashSet<MeshCollider>();
            for (int i = 0; i < merged.Count; i++)
            {
                if (merged[i].Collider != null) seen.Add(merged[i].Collider);
            }
            for (int i = 0; i < newEntries.Count; i++)
            {
                if (newEntries[i].Collider != null && seen.Add(newEntries[i].Collider))
                {
                    merged.Add(newEntries[i]);
                }
            }

            gtc.SetBindingsEditor(merged);
            if (PrefabUtility.IsPartOfPrefabInstance(gtc))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(gtc);
            }
            return gtc;
        }

        private static void ProcessLODGroup(
            LODGroup lg,
            HashSet<GameObject> handledObjects,
            List<GloveTightCollider.BoundColliderEntry> outBindings,
            ref int boxCollidersRemoved,
            ref int batchingClearedCount)
        {
            LOD[] lods = lg.GetLODs();
            if (lods == null || lods.Length == 0) return;

            var lod0Set = new HashSet<GameObject>();
            var lod0List = new List<MeshRenderer>(4);

            if (lods[0].renderers != null)
            {
                for (int i = 0; i < lods[0].renderers.Length; i++)
                {
                    if (lods[0].renderers[i] is MeshRenderer mr && mr != null)
                    {
                        lod0Set.Add(mr.gameObject);
                        lod0List.Add(mr);
                    }
                }
            }

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

            if (!lod0Set.Contains(lg.gameObject))
            {
                StripNonTriggerColliders(lg.gameObject, ref boxCollidersRemoved);
            }

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
                    StripNonTriggerColliders(lowerGo, ref boxCollidersRemoved);
                    batchingClearedCount += DisableBatchingStatic(lowerGo);
                }
            }

            for (int i = 0; i < lod0List.Count; i++)
            {
                MeshRenderer mr = lod0List[i];
                if (mr == null) continue;
                handledObjects.Add(mr.gameObject);

                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (!IsValidStructuralMesh(mr.gameObject, mf)) continue;

                batchingClearedCount += DisableBatchingStatic(mr.gameObject);

                if (mr.gameObject.activeSelf)
                {
                    Transform staleHolder = lg.transform.Find($"{PersistentColliderPrefix}_{mr.name}");
                    if (staleHolder != null && !PrefabUtility.IsPartOfPrefabInstance(staleHolder.gameObject))
                    {
                        UnityEngine.Object.DestroyImmediate(staleHolder.gameObject);
                    }

                    var mc = NormalizeStructuralObject(mr.gameObject, mf.sharedMesh, ref boxCollidersRemoved);
                    if (mc != null)
                    {
                        outBindings.Add(new GloveTightCollider.BoundColliderEntry { Collider = mc, VisualMesh = mf.sharedMesh });
                    }
                }
                else
                {
                    StripNonTriggerColliders(mr.gameObject, ref boxCollidersRemoved);
                    string holderName = $"{PersistentColliderPrefix}_{mr.name}";
                    Transform holderTr = lg.transform.Find(holderName);
                    if (holderTr == null)
                    {
                        var holderGo = new GameObject(holderName);
                        holderGo.layer = mr.gameObject.layer;
                        holderTr = holderGo.transform;
                        holderTr.SetParent(lg.transform, false);
                    }
                    holderTr.position = mr.transform.position;
                    holderTr.rotation = mr.transform.rotation;
                    Vector3 parentScale = lg.transform.lossyScale;
                    Vector3 mrScale = mr.transform.lossyScale;
                    holderTr.localScale = new Vector3(
                        Mathf.Abs(parentScale.x) > 1e-5f ? mrScale.x / parentScale.x : 1f,
                        Mathf.Abs(parentScale.y) > 1e-5f ? mrScale.y / parentScale.y : 1f,
                        Mathf.Abs(parentScale.z) > 1e-5f ? mrScale.z / parentScale.z : 1f);

                    var mc = NormalizeStructuralObject(holderTr.gameObject, mf.sharedMesh, ref boxCollidersRemoved);
                    if (mc != null)
                    {
                        outBindings.Add(new GloveTightCollider.BoundColliderEntry { Collider = mc, VisualMesh = mf.sharedMesh });
                    }
                }
            }
        }

        private static MeshCollider NormalizeStructuralObject(GameObject targetGo, Mesh visualMesh, ref int boxRemoved)
        {
            var cols = targetGo.GetComponents<Collider>();
            MeshCollider existingMc = null;
            var extraMeshCols = new List<MeshCollider>();

            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.isTrigger) continue;

                if (c is BoxCollider || c is CapsuleCollider || c is SphereCollider)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(c))
                    {
                        c.enabled = false;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(c);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                    boxRemoved++;
                }
                else if (c is MeshCollider mc)
                {
                    if (existingMc == null) existingMc = mc;
                    else extraMeshCols.Add(mc);
                }
            }

            for (int i = 0; i < extraMeshCols.Count; i++)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(extraMeshCols[i]))
                {
                    extraMeshCols[i].enabled = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(extraMeshCols[i]);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(extraMeshCols[i]);
                }
            }

            if (existingMc == null)
            {
                existingMc = targetGo.AddComponent<MeshCollider>();
            }

            existingMc.sharedMesh = visualMesh;
            existingMc.convex = false;
            existingMc.cookingOptions = GloveCookingOptions;
            existingMc.isTrigger = false;
            existingMc.enabled = true;

            EditorUtility.SetDirty(existingMc);
            if (PrefabUtility.IsPartOfPrefabInstance(existingMc))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(existingMc);
            }

            return existingMc;
        }

        private static void StripNonTriggerColliders(GameObject go, ref int boxRemoved)
        {
            var cols = go.GetComponents<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.isTrigger) continue;

                if (PrefabUtility.IsPartOfPrefabInstance(c))
                {
                    c.enabled = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(c);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(c);
                }

                if (c is BoxCollider || c is CapsuleCollider || c is SphereCollider)
                {
                    boxRemoved++;
                }
            }
        }

        private static int DisableBatchingStaticRecursive(GameObject root)
        {
            int count = 0;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
            {
                count += DisableBatchingStatic(tr.gameObject);
            }
            return count;
        }

        private static int DisableBatchingStatic(GameObject go)
        {
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
            if ((flags & StaticEditorFlags.BatchingStatic) == 0) return 0;

            flags &= ~StaticEditorFlags.BatchingStatic;
            GameObjectUtility.SetStaticEditorFlags(go, flags);
            EditorUtility.SetDirty(go);
            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }
            return 1;
        }

        public static int EnsureAllSceneAndStructureMeshesReadable()
        {
            var meshPaths = new HashSet<string>();

            foreach (var mc in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include))
            {
                if (mc == null || mc.sharedMesh == null) continue;
                if (!mc.sharedMesh.isReadable)
                {
                    string path = AssetDatabase.GetAssetPath(mc.sharedMesh);
                    if (!string.IsNullOrEmpty(path)) meshPaths.Add(path);
                }
            }

            foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (ShouldSkipHierarchy(mf.gameObject)) continue;
                if (!mf.sharedMesh.isReadable)
                {
                    string path = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (!string.IsNullOrEmpty(path)) meshPaths.Add(path);
                }
            }

            if (meshPaths.Count == 0) return 0;

            var importersToSave = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string assetPath in meshPaths)
                {
                    var modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                    if (modelImporter != null && !modelImporter.isReadable)
                    {
                        modelImporter.isReadable = true;
                        modelImporter.SaveAndReimport();
                        importersToSave.Add(assetPath);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (importersToSave.Count > 0)
            {
                AssetDatabase.Refresh();
            }

            return importersToSave.Count;
        }

        private static bool ShouldSkipHierarchy(GameObject go)
        {
            if (go == null) return true;
            if (go.GetComponentInParent<PlayerHealthController>() != null) return true;
            if (go.GetComponentInParent<PlayerMovement>() != null) return true;
            if (go.GetComponentInParent<CharacterController>() != null) return true;
            if (go.GetComponentInParent<BodyPartHitbox>() != null) return true;
            if (go.GetComponentInParent<LootContainerInteractable>() != null) return true;
            if (go.GetComponentInParent<WorldItemPickup>() != null) return true;
            if (go.GetComponentInParent<ShadeZoneBuilding>() != null && go.GetComponent<MeshRenderer>() == null && go.GetComponent<MeshFilter>() == null) return true;
            if (go.GetComponentInParent<Terrain>() != null) return true;
            if (go.GetComponentInParent<Camera>() != null) return true;
            if (go.GetComponentInParent<Light>() != null) return true;
            if (go.GetComponentInParent<Canvas>() != null) return true;
            if (go.GetComponent<ParticleSystem>() != null) return true;
            return false;
        }

        private static bool IsValidStructuralMesh(GameObject go, MeshFilter mf)
        {
            if (mf == null || mf.sharedMesh == null) return false;
            if (mf.sharedMesh.vertexCount < 3) return false;
            string lower = go.name.ToLowerInvariant();
            if (lower.Contains("decal") || lower.Contains("puddle") || lower.Contains("grass") || lower.Contains("foliage"))
            {
                return false;
            }
            Vector3 size = mf.sharedMesh.bounds.size;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) >= 0.02f;
        }

        private static bool IsLowerLodName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.EndsWith("_LOD1", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD2", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD3", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_LOD4", StringComparison.OrdinalIgnoreCase);
        }
    }
#endif
}

