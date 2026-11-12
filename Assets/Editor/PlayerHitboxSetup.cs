#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Combat;
using Scorched.Player;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Attaches fitted anatomical BodyPartHitbox colliders and PlayerHealthController
    /// strictly to the third-person bandit skeleton (excluding FPS_ViewmodelRig) on both
    /// Assets/Prefabs/ScorchedPlayer.prefab and any active scene Player.
    /// </summary>
    public static class PlayerHitboxSetup
    {
        private const string PlayerPrefabPath = "Assets/Prefabs/ScorchedPlayer.prefab";

        [MenuItem("Scorched/Setup Anatomical Player Hitboxes")]
        public static string SetupAllPlayerHitboxes()
        {
            int count = 0;

            // 1. Update ScorchedPlayer.prefab
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (prefabAsset != null)
            {
                GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
                try
                {
                    int attached = ConfigureHitboxesOnRoot(contents);
                    PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath);
                    count += attached;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            // 2. Update scene Player(s) cleanly and mark scene dirty/saved
            Scene activeScene = SceneManager.GetActiveScene();
            bool updatedScene = false;
            PlayerMovement[] scenePlayers = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (PlayerMovement pm in scenePlayers)
            {
                if (pm == null || !pm.gameObject.scene.IsValid()) continue;
                int attached = ConfigureHitboxesOnRoot(pm.gameObject);
                EditorUtility.SetDirty(pm.gameObject);
                count += attached;
                updatedScene = true;
            }

            if (updatedScene && activeScene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(activeScene);
                EditorSceneManager.SaveScene(activeScene);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string summary = $"[PlayerHitboxSetup] SUCCESS: Configured PlayerHealthController + {count} anatomical BodyPartHitbox colliders on prefab & scene.";
            Debug.Log(summary);
            return summary;
        }

        public static int ConfigureHitboxesOnRoot(GameObject playerRoot)
        {
            if (playerRoot == null) return 0;

            // Ensure PlayerHealthController exists cleanly on root
            PlayerHealthController health = playerRoot.GetComponent<PlayerHealthController>();
            if (health == null)
            {
                health = playerRoot.AddComponent<PlayerHealthController>();
            }

            // Remove any prior BodyPartHitbox (and its bone collider) for idempotent rebuild,
            // strictly preserving the root CharacterController.
            BodyPartHitbox[] existing = playerRoot.GetComponentsInChildren<BodyPartHitbox>(true);
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == null) continue;
                Collider col = existing[i].GetComponent<Collider>();
                UnityEngine.Object.DestroyImmediate(existing[i], true);
                if (col != null && !(col is CharacterController) && col.gameObject != playerRoot)
                {
                    UnityEngine.Object.DestroyImmediate(col, true);
                }
            }

            // Strictly scope hitbox mounting under the third-person armature skeleton ONLY
            Transform skeletonSearchRoot = playerRoot.transform.Find("root") ?? playerRoot.transform;
            int added = 0;

            // 1. Head (2.0x)
            Transform head = FindBoneExcludingViewmodel(skeletonSearchRoot, "head.x");
            if (head != null)
            {
                CapsuleCollider cap = head.gameObject.AddComponent<CapsuleCollider>();
                cap.isTrigger = true;
                cap.direction = 1; // +Y
                cap.radius = 0.115f;
                cap.height = 0.25f;
                cap.center = new Vector3(0f, 0.065f, 0.01f);
                AttachHitbox(head.gameObject, BodyPartType.Head, cap);
                added++;
            }

            // 2. UpperTorso (1.0x) - chest cage & tactical rig
            Transform spine03 = FindBoneExcludingViewmodel(skeletonSearchRoot, "spine_03.x");
            if (spine03 != null)
            {
                BoxCollider box = spine03.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = new Vector3(0f, 0.015f, 0.025f);
                box.size = new Vector3(0.35f, 0.30f, 0.26f);
                AttachHitbox(spine03.gameObject, BodyPartType.UpperTorso, box);
                added++;
            }

            // 3. LowerTorso (0.90x) - abdomen & pelvis
            Transform spine01 = FindBoneExcludingViewmodel(skeletonSearchRoot, "spine_01.x");
            if (spine01 != null)
            {
                BoxCollider box = spine01.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = new Vector3(0f, -0.025f, 0.020f);
                box.size = new Vector3(0.31f, 0.30f, 0.25f);
                AttachHitbox(spine01.gameObject, BodyPartType.LowerTorso, box);
                added++;
            }

            // 4. Arms (0.75x) - Upper Arms & Forearms L/R
            added += AttachSegmentCapsule(skeletonSearchRoot, "arm_stretch.l", "forearm_stretch.l", BodyPartType.Arms, 0.068f, 0.24f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "forearm_stretch.l", "hand.l", BodyPartType.Arms, 0.056f, 0.26f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "arm_stretch.r", "forearm_stretch.r", BodyPartType.Arms, 0.068f, 0.24f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "forearm_stretch.r", "hand.r", BodyPartType.Arms, 0.056f, 0.26f);

            // 5. Legs (0.70x) - Thighs & Shins L/R
            added += AttachSegmentCapsule(skeletonSearchRoot, "thigh_stretch.l", "leg_stretch.l", BodyPartType.Legs, 0.088f, 0.43f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "leg_stretch.l", "foot.l", BodyPartType.Legs, 0.068f, 0.39f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "thigh_stretch.r", "leg_stretch.r", BodyPartType.Legs, 0.088f, 0.43f);
            added += AttachSegmentCapsule(skeletonSearchRoot, "leg_stretch.r", "foot.r", BodyPartType.Legs, 0.068f, 0.39f);

            health.RefreshHitboxes();
            return added;
        }

        private static int AttachSegmentCapsule(
            Transform searchRoot,
            string boneName,
            string childBoneName,
            BodyPartType part,
            float radius,
            float fallbackLength)
        {
            Transform bone = FindBoneExcludingViewmodel(searchRoot, boneName);
            if (bone == null) return 0;

            Transform child = FindBoneExcludingViewmodel(bone, childBoneName);
            Vector3 localEnd = child != null
                ? bone.InverseTransformPoint(child.position)
                : new Vector3(0f, fallbackLength, 0f);

            float segmentLength = localEnd.magnitude;
            if (segmentLength < 0.05f) segmentLength = fallbackLength;

            int dirAxis = 1;
            float ax = Mathf.Abs(localEnd.x);
            float ay = Mathf.Abs(localEnd.y);
            float az = Mathf.Abs(localEnd.z);
            if (ax >= ay && ax >= az) dirAxis = 0;
            else if (az >= ay && az >= ax) dirAxis = 2;

            CapsuleCollider cap = bone.gameObject.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.direction = dirAxis;
            cap.radius = radius;
            cap.height = Mathf.Max(radius * 2f, segmentLength + radius * 0.5f);
            cap.center = localEnd * 0.5f;

            AttachHitbox(bone.gameObject, part, cap);
            return 1;
        }

        private static void AttachHitbox(GameObject go, BodyPartType part, Collider col)
        {
            BodyPartHitbox hb = go.AddComponent<BodyPartHitbox>();
            hb.Configure(part, col);
        }

        private static Transform FindBoneExcludingViewmodel(Transform root, string exactName)
        {
            if (root == null) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!string.Equals(t.name, exactName, StringComparison.Ordinal)) continue;
                if (IsUnderViewmodel(t)) continue;
                return t;
            }
            return null;
        }

        private static bool IsUnderViewmodel(Transform t)
        {
            while (t != null)
            {
                if (t.name.IndexOf("Viewmodel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }
    }
}
#endif
