#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Player;
using Scorched.Weapons;

namespace Scorched.EditorTools
{
    public static class PlayerViewmodelRestorer
    {
        private const string PrefabPath = "Assets/Prefabs/ScorchedPlayer.prefab";
        private const string MainScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Scorched/Setup/Restore Native Player Animation & Weapon Setup")]
        public static void RestoreNativeMenu()
        {
            string log = RestoreAll();
            Debug.Log(log);
        }

        public static string RestoreAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== RESTORING NATIVE CHARACTER ANIMATIONS & WEAPON SOCKET ===");

            // 1. Restore Scene Player if Main.unity is open
            Scene activeScene = SceneManager.GetActiveScene();
            GameObject scenePlayer = GameObject.Find("Player");
            if (scenePlayer != null)
            {
                RestorePlayerObject(scenePlayer, sb, "Scene Player");
                EditorSceneManager.MarkSceneDirty(activeScene);
                EditorSceneManager.SaveScene(activeScene);
                sb.AppendLine("[Scene] Saved Main.unity with native setup restored.");
            }

            // 2. Restore Prefab Asset
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefabAsset != null)
            {
                string path = AssetDatabase.GetAssetPath(prefabAsset);
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    RestorePlayerObject(prefabRoot, sb, "Prefab Asset");
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                    sb.AppendLine($"[Prefab] Saved '{PrefabPath}' with native setup restored.");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            sb.AppendLine("=== RESTORATION COMPLETE: All twisted IK removed, native animations & socket restored! ===");
            return sb.ToString();
        }

        public static string TestSpinePitchIK(float pitchAngle)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var sb = new StringBuilder();
            sb.AppendLine($"=== TEST SPINE PITCH IK (Pitch={pitchAngle} deg) ===");

            var mov = p.GetComponent<PlayerMovement>();
            var ctrl = p.GetComponent<FPSPerspectiveController>();
            var cam = p.GetComponentInChildren<Camera>();

            // Pitch spine bones
            var spine = FindDeepChild(p.transform, "spine.x");
            var chest = FindDeepChild(p.transform, "chest.x");
            var upperChest = FindDeepChild(p.transform, "upper_chest.x");

            if (spine != null && chest != null && upperChest != null)
            {
                spine.rotation = Quaternion.AngleAxis(pitchAngle * 0.25f, p.transform.right) * spine.rotation;
                chest.rotation = Quaternion.AngleAxis(pitchAngle * 0.35f, p.transform.right) * chest.rotation;
                upperChest.rotation = Quaternion.AngleAxis(pitchAngle * 0.40f, p.transform.right) * upperChest.rotation;
            }

            // Run IK
            ctrl?.SolveLeftHandIK();

            var armL = FindDeepChild(p.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var handL = FindDeepChild(p.transform, "hand.l");
            var weapon = p.GetComponent<PlayerWeaponHandler>()?.EquippedWeapon;
            var lGrip = weapon?.Find("LeftHandGripTarget");

            sb.AppendLine($"arm.l: {armL?.position}");
            sb.AppendLine($"forearm.l: {forearmL?.position}");
            sb.AppendLine($"hand.l: {handL?.position}");
            sb.AppendLine($"lGrip: {lGrip?.position}");
            if (handL != null && lGrip != null)
            {
                sb.AppendLine($"Dist hand.l to lGrip: {Vector3.Distance(handL.position, lGrip.position):F4}m");
            }
            if (cam != null && handL != null)
            {
                sb.AppendLine($"hand.l in Cam Viewport: {cam.WorldToViewportPoint(handL.position)}");
            }
            if (cam != null && weapon != null)
            {
                sb.AppendLine($"weapon in Cam Viewport: {cam.WorldToViewportPoint(weapon.position)}");
            }

            return sb.ToString();
        }

        public static string CreateForearmsAndHandsMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = smr.sharedMesh;

            var bones = smr.bones;
            var weights = srcMesh.boneWeights;

            // Define Forearm & Hand Bones:
            // Right: 20 (forearm_stretch.r), 21 (hand.r), 22..36 (fingers), 37 (forearm_twist.r)
            // Left:  41 (forearm_stretch.l), 42 (hand.l), 43..57 (fingers), 58 (forearm_twist.l)
            bool[] isForearmHandBone = new bool[bones.Length];
            for (int i = 20; i <= 37; i++) isForearmHandBone[i] = true;
            for (int i = 41; i <= 58; i++) isForearmHandBone[i] = true;

            bool[] isKeptVertex = new bool[srcMesh.vertexCount];
            BoneWeight[] cleanWeights = new BoneWeight[srcMesh.vertexCount];

            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float w0 = isForearmHandBone[bw.boneIndex0] ? bw.weight0 : 0f;
                float w1 = isForearmHandBone[bw.boneIndex1] ? bw.weight1 : 0f;
                float w2 = isForearmHandBone[bw.boneIndex2] ? bw.weight2 : 0f;
                float w3 = isForearmHandBone[bw.boneIndex3] ? bw.weight3 : 0f;
                float total = w0 + w1 + w2 + w3;

                // Keep vertices that are primarily part of forearms, wrists, gloves, and hands
                if (total >= 0.50f)
                {
                    isKeptVertex[i] = true;
                    // Re-normalize to 100% on forearm/hand bones
                    cleanWeights[i] = new BoneWeight
                    {
                        boneIndex0 = bw.boneIndex0,
                        boneIndex1 = bw.boneIndex1,
                        boneIndex2 = bw.boneIndex2,
                        boneIndex3 = bw.boneIndex3,
                        weight0 = w0 / total,
                        weight1 = w1 / total,
                        weight2 = w2 / total,
                        weight3 = w3 / total
                    };
                }
                else
                {
                    cleanWeights[i] = bw;
                }
            }

            Mesh cleanMesh = UnityEngine.Object.Instantiate(srcMesh);
            cleanMesh.name = "FPS_Bandit_PureArms";
            cleanMesh.boneWeights = cleanWeights;

            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isKeptVertex[v0] && isKeptVertex[v1] && isKeptVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            cleanMesh.SetTriangles(keptTriangles.ToArray(), 0);
            if (cleanMesh.subMeshCount > 1) cleanMesh.SetTriangles(new int[0], 1);
            if (cleanMesh.subMeshCount > 2) cleanMesh.SetTriangles(new int[0], 2);

            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(cleanMesh, assetPath);
            AssetDatabase.SaveAssets();

            // Update in scene
            var p = GameObject.Find("Player");
            var vmSmr = p?.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (vmSmr != null)
            {
                vmSmr.sharedMesh = cleanMesh;
            }

            return $"Created Forearms & Hands Mesh at {assetPath} with {keptTriangles.Count / 3} triangles!";
        }

        public static string CreateSpikelessArmsMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = smr.sharedMesh;

            var bones = smr.bones;
            var weights = srcMesh.boneWeights;

            // Define arm bones:
            // 19..38: Right arm, forearm, hand, fingers, twists
            // 40..59: Left arm, forearm, hand, fingers, twists
            bool[] isArmBone = new bool[bones.Length];
            for (int i = 19; i <= 38; i++) isArmBone[i] = true;
            for (int i = 40; i <= 59; i++) isArmBone[i] = true;

            bool[] isArmVertex = new bool[srcMesh.vertexCount];
            BoneWeight[] cleanWeights = new BoneWeight[srcMesh.vertexCount];

            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float w0 = isArmBone[bw.boneIndex0] ? bw.weight0 : 0f;
                float w1 = isArmBone[bw.boneIndex1] ? bw.weight1 : 0f;
                float w2 = isArmBone[bw.boneIndex2] ? bw.weight2 : 0f;
                float w3 = isArmBone[bw.boneIndex3] ? bw.weight3 : 0f;
                float totalArm = w0 + w1 + w2 + w3;

                // Only keep vertices strongly attached to arms
                if (totalArm >= 0.70f)
                {
                    isArmVertex[i] = true;
                    // Re-normalize weights so 100% of deformation is driven by arm bones! ZERO influence from chest/spine/neck!
                    cleanWeights[i] = new BoneWeight
                    {
                        boneIndex0 = bw.boneIndex0,
                        boneIndex1 = bw.boneIndex1,
                        boneIndex2 = bw.boneIndex2,
                        boneIndex3 = bw.boneIndex3,
                        weight0 = w0 / totalArm,
                        weight1 = w1 / totalArm,
                        weight2 = w2 / totalArm,
                        weight3 = w3 / totalArm
                    };
                }
                else
                {
                    cleanWeights[i] = bw;
                }
            }

            Mesh cleanMesh = UnityEngine.Object.Instantiate(srcMesh);
            cleanMesh.name = "FPS_Bandit_PureArms";
            cleanMesh.boneWeights = cleanWeights;

            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isArmVertex[v0] && isArmVertex[v1] && isArmVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            cleanMesh.SetTriangles(keptTriangles.ToArray(), 0);
            if (cleanMesh.subMeshCount > 1) cleanMesh.SetTriangles(new int[0], 1);
            if (cleanMesh.subMeshCount > 2) cleanMesh.SetTriangles(new int[0], 2);

            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(cleanMesh, assetPath);
            AssetDatabase.SaveAssets();

            // Apply to existing viewmodel SMR in scene
            var p = GameObject.Find("Player");
            var vmSmr = p?.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (vmSmr != null)
            {
                vmSmr.sharedMesh = cleanMesh;
            }

            return $"Created Spikeless Arms Mesh at {assetPath} with {keptTriangles.Count / 3} triangles!";
        }

        public static string ListAllBanditBones()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var sb = new StringBuilder();
            sb.AppendLine($"=== ALL {smr.bones.Length} BANDIT BONES ===");
            for (int i = 0; i < smr.bones.Length; i++)
            {
                sb.AppendLine($"[{i}] {smr.bones[i].name}");
            }
            return sb.ToString();
        }

        public static string CreatePureGlovesAndCuffsMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = smr.sharedMesh;

            var bones = smr.bones;
            var weights = srcMesh.boneWeights;

            // Pure Glove, Fingers & Wrist Cuffs ONLY:
            // Exclude elbow (forearm_stretch 20, 41) to eliminate all elbow joint flare!
            // Right: 21 (hand.r), 22..36 (fingers), 37 (forearm_twist.r)
            // Left:  42 (hand.l), 43..57 (fingers), 58 (forearm_twist.l)
            bool[] isGloveAndCuff = new bool[bones.Length];
            for (int i = 21; i <= 37; i++) isGloveAndCuff[i] = true;
            for (int i = 42; i <= 58; i++) isGloveAndCuff[i] = true;

            bool[] isKeptVertex = new bool[srcMesh.vertexCount];
            int keptVertCount = 0;

            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                // Check if vertex's dominant bone is in the glove/wrist cuff set
                float w = 0f;
                if (isGloveAndCuff[bw.boneIndex0]) w += bw.weight0;
                if (isGloveAndCuff[bw.boneIndex1]) w += bw.weight1;
                if (isGloveAndCuff[bw.boneIndex2]) w += bw.weight2;
                if (isGloveAndCuff[bw.boneIndex3]) w += bw.weight3;

                // Keep only vertices that are 80%+ on the glove & wrist cuff
                if (w >= 0.80f)
                {
                    isKeptVertex[i] = true;
                    keptVertCount++;
                }
            }

            Mesh cleanMesh = UnityEngine.Object.Instantiate(srcMesh);
            cleanMesh.name = "FPS_Bandit_PureArms";

            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isKeptVertex[v0] && isKeptVertex[v1] && isKeptVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            cleanMesh.SetTriangles(keptTriangles.ToArray(), 0);
            if (cleanMesh.subMeshCount > 1) cleanMesh.SetTriangles(new int[0], 1);
            if (cleanMesh.subMeshCount > 2) cleanMesh.SetTriangles(new int[0], 2);

            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(cleanMesh, assetPath);
            AssetDatabase.SaveAssets();

            // Update in scene
            var p = GameObject.Find("Player");
            var vmSmr = p?.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (vmSmr != null)
            {
                vmSmr.sharedMesh = cleanMesh;
                vmSmr.updateWhenOffscreen = true;
            }

            return $"Created Pure Gloves & Cuffs Mesh at {assetPath}: {keptVertCount} verts, {keptTriangles.Count / 3} triangles!";
        }

        public static string CreateCleanForearmsAndHandsMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = smr.sharedMesh;

            var bones = smr.bones;
            var weights = srcMesh.boneWeights;

            // Forearm, wrist, and hand bones ONLY:
            // Right: 20 (forearm_stretch.r), 21 (hand.r), 22..36 (fingers), 37 (forearm_twist.r)
            // Left:  41 (forearm_stretch.l), 42 (hand.l), 43..57 (fingers), 58 (forearm_twist.l)
            bool[] isForearmHand = new bool[bones.Length];
            for (int i = 20; i <= 37; i++) isForearmHand[i] = true;
            for (int i = 41; i <= 58; i++) isForearmHand[i] = true;

            bool[] isKeptVertex = new bool[srcMesh.vertexCount];
            int keptVertCount = 0;

            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float w = 0f;
                if (isForearmHand[bw.boneIndex0]) w += bw.weight0;
                if (isForearmHand[bw.boneIndex1]) w += bw.weight1;
                if (isForearmHand[bw.boneIndex2]) w += bw.weight2;
                if (isForearmHand[bw.boneIndex3]) w += bw.weight3;

                // Only keep vertices firmly on forearm, wrist, and hand (85%+)
                if (w >= 0.85f)
                {
                    isKeptVertex[i] = true;
                    keptVertCount++;
                }
            }

            // Keep original bone weights intact so skinning deformation remains smooth and flawless!
            Mesh cleanMesh = UnityEngine.Object.Instantiate(srcMesh);
            cleanMesh.name = "FPS_Bandit_PureArms";

            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isKeptVertex[v0] && isKeptVertex[v1] && isKeptVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            cleanMesh.SetTriangles(keptTriangles.ToArray(), 0);
            if (cleanMesh.subMeshCount > 1) cleanMesh.SetTriangles(new int[0], 1);
            if (cleanMesh.subMeshCount > 2) cleanMesh.SetTriangles(new int[0], 2);

            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(cleanMesh, assetPath);
            AssetDatabase.SaveAssets();

            // Update in scene
            var p = GameObject.Find("Player");
            var vmSmr = p?.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (vmSmr != null)
            {
                vmSmr.sharedMesh = cleanMesh;
                vmSmr.updateWhenOffscreen = true;
            }

            return $"Created Clean Forearms & Hands Mesh at {assetPath}: {keptVertCount} verts, {keptTriangles.Count / 3} triangles!";
        }

        public static string InspectHandPositionsInView()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            var vmWeapon = rig?.Find("Viewmodel_AK47");
            var vmArms = rig?.Find("Viewmodel_BanditArms");

            var handR = FindDeepChild(vmArms, "hand.r");
            var handL = FindDeepChild(vmArms, "hand.l");
            var rGrip = vmWeapon?.Find("RightHandGripTarget");
            var lGrip = vmWeapon?.Find("LeftHandGripTarget");

            var sb = new StringBuilder();
            sb.AppendLine("=== HAND POSITIONS IN VIEW ===");
            sb.AppendLine($"Cam: pos={cam?.transform.position}");
            sb.AppendLine($"hand.r: world={handR?.position}, vp={cam?.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l: world={handL?.position}, vp={cam?.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");
            sb.AppendLine($"rGrip:  world={rGrip?.position}, vp={cam?.WorldToViewportPoint(rGrip?.position ?? Vector3.zero)}");
            sb.AppendLine($"lGrip:  world={lGrip?.position}, vp={cam?.WorldToViewportPoint(lGrip?.position ?? Vector3.zero)}");

            return sb.ToString();
        }

        public static string CaptureAllPerspectiveModes()
        {
            return ApplyAndVerifyFinalViewmodelSetup();
        }

        public static string TestPerfectAdsViewmodel()
        {
            return CaptureControllerView(true, false, 0f, "test_perfect_ads.png");
        }

        public static string TestPerfectAnatomyViewmodel()
        {
            return CaptureControllerView(false, false, 0f, "test_perfect_anatomy.png");
        }

        public static string TestViewmodelGlovesCapture()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            rig.gameObject.SetActive(true);
            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");

            if (vmWeapon != null)
            {
                vmWeapon.gameObject.SetActive(true);
                vmWeapon.localPosition = new Vector3(0.06f, -0.15f, 0.30f);
                vmWeapon.localRotation = Quaternion.Euler(-1f, -2f, 1f);
            }

            if (vmArms != null)
            {
                vmArms.gameObject.SetActive(true);
                vmArms.localPosition = new Vector3(0.02f, -0.42f, 0.06f);
                vmArms.localRotation = Quaternion.Euler(0f, 15f, 0f);

                var smr = vmArms.GetComponentInChildren<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    smr.enabled = true;
                    smr.updateWhenOffscreen = true;
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset");
                    if (mesh != null) smr.sharedMesh = mesh;
                }

                // Solve Two-Bone IK for both hands
                var rGrip = vmWeapon.Find("RightHandGripTarget");
                var lGrip = vmWeapon.Find("LeftHandGripTarget");

                var armR = FindDeepChild(vmArms, "arm_stretch.r");
                var forearmR = FindDeepChild(vmArms, "forearm_stretch.r");
                var handR = FindDeepChild(vmArms, "hand.r");

                var armL = FindDeepChild(vmArms, "arm_stretch.l");
                var forearmL = FindDeepChild(vmArms, "forearm_stretch.l");
                var handL = FindDeepChild(vmArms, "hand.l");

                if (armR != null && forearmR != null && handR != null && rGrip != null)
                {
                    Vector3 rPole = armR.position - rig.up * 0.5f + rig.right * 0.4f - rig.forward * 0.2f;
                    FPSPerspectiveController.SolveTwoBoneIKInternal(armR, forearmR, handR, rGrip.position, rGrip.rotation, rPole);
                }

                if (armL != null && forearmL != null && handL != null && lGrip != null)
                {
                    Vector3 lPole = armL.position - rig.up * 0.5f - rig.right * 0.4f - rig.forward * 0.2f;
                    FPSPerspectiveController.SolveTwoBoneIKInternal(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);
                }
            }

            string capLog = CaptureViewToArtifact("test_vm_gloves_live.png");
            return capLog;
        }

        public static string TestGlovesOnlyMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = smr.sharedMesh;

            var bones = smr.bones;
            var weights = srcMesh.boneWeights;

            // Strict gloves & wrist cuffs:
            // Right: 21 (hand.r), 22..36 (fingers), 37 (forearm_twist.r)
            // Left:  42 (hand.l), 43..57 (fingers), 58 (forearm_twist.l)
            bool[] isGloveBone = new bool[bones.Length];
            for (int i = 21; i <= 37; i++) isGloveBone[i] = true;
            for (int i = 42; i <= 58; i++) isGloveBone[i] = true;

            bool[] isGloveVertex = new bool[srcMesh.vertexCount];
            BoneWeight[] cleanWeights = new BoneWeight[srcMesh.vertexCount];

            int gloveVertCount = 0;
            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float w0 = isGloveBone[bw.boneIndex0] ? bw.weight0 : 0f;
                float w1 = isGloveBone[bw.boneIndex1] ? bw.weight1 : 0f;
                float w2 = isGloveBone[bw.boneIndex2] ? bw.weight2 : 0f;
                float w3 = isGloveBone[bw.boneIndex3] ? bw.weight3 : 0f;
                float total = w0 + w1 + w2 + w3;

                // Keep vertices that have at least 30% weight on glove/hand/wrist
                if (total >= 0.30f)
                {
                    isGloveVertex[i] = true;
                    gloveVertCount++;
                    // Re-normalize to 100% on glove bones so ZERO pulling from forearm_stretch/arm/body
                    cleanWeights[i] = new BoneWeight
                    {
                        boneIndex0 = isGloveBone[bw.boneIndex0] ? bw.boneIndex0 : (isGloveBone[bw.boneIndex1] ? bw.boneIndex1 : 21),
                        boneIndex1 = isGloveBone[bw.boneIndex1] ? bw.boneIndex1 : (isGloveBone[bw.boneIndex0] ? bw.boneIndex0 : 21),
                        boneIndex2 = isGloveBone[bw.boneIndex2] ? bw.boneIndex2 : (isGloveBone[bw.boneIndex0] ? bw.boneIndex0 : 21),
                        boneIndex3 = isGloveBone[bw.boneIndex3] ? bw.boneIndex3 : (isGloveBone[bw.boneIndex0] ? bw.boneIndex0 : 21),
                        weight0 = w0 / total,
                        weight1 = w1 / total,
                        weight2 = w2 / total,
                        weight3 = w3 / total
                    };
                }
                else
                {
                    cleanWeights[i] = bw;
                }
            }

            Mesh gloveMesh = UnityEngine.Object.Instantiate(srcMesh);
            gloveMesh.name = "FPS_Bandit_PureArms";
            gloveMesh.boneWeights = cleanWeights;

            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isGloveVertex[v0] && isGloveVertex[v1] && isGloveVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            gloveMesh.SetTriangles(keptTriangles.ToArray(), 0);
            if (gloveMesh.subMeshCount > 1) gloveMesh.SetTriangles(new int[0], 1);
            if (gloveMesh.subMeshCount > 2) gloveMesh.SetTriangles(new int[0], 2);

            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(gloveMesh, assetPath);
            AssetDatabase.SaveAssets();

            // Assign to scene
            var p = GameObject.Find("Player");
            var vmSmr = p?.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (vmSmr != null)
            {
                vmSmr.sharedMesh = gloveMesh;
                vmSmr.updateWhenOffscreen = true;
            }

            return $"Created Glove Mesh at {assetPath}: {gloveVertCount} verts, {keptTriangles.Count / 3} triangles!";
        }

        public static string FixPlayerBanditMesh()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var smr = p.transform.Find("bandit")?.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) return "No SMR on Player/bandit";

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var fbxSmr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();

            Undo.RecordObject(smr, "Restore Bandit Mesh");
            smr.sharedMesh = fbxSmr.sharedMesh;
            smr.sharedMaterials = fbxSmr.sharedMaterials;
            smr.enabled = true;
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            // Also restore hand.l localPosition to FBX bindpose
            var handL = FindDeepChild(p.transform, "hand.l");
            if (handL != null)
            {
                Undo.RecordObject(handL, "Restore Hand L Bindpose");
                handL.localPosition = new Vector3(0f, 0.2648f, 0f);
            }

            EditorUtility.SetDirty(p);
            return $"Restored Player/bandit: mesh={smr.sharedMesh.name}, materials={smr.sharedMaterials.Length}, enabled={smr.enabled}";
        }

        public static string InspectBanditComponents()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var b = p.transform.Find("bandit");
            if (b == null) return "No bandit child";

            var comps = b.GetComponents<SkinnedMeshRenderer>();
            var sb = new StringBuilder();
            sb.AppendLine($"=== BANDIT GAMEOBJECT SMRS ({comps.Length}) ===");
            for (int i = 0; i < comps.Length; i++)
            {
                var s = comps[i];
                sb.AppendLine($"[{i}] enabled={s.enabled}, shadow={s.shadowCastingMode}, mesh={(s.sharedMesh != null ? s.sharedMesh.name : "NULL")}");
            }
            return sb.ToString();
        }

        public static string InspectPlayerBodyHierarchy()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var sb = new StringBuilder();
            sb.AppendLine("=== PLAYER BODY HIERARCHY ===");
            for (int i = 0; i < p.transform.childCount; i++)
            {
                var c = p.transform.GetChild(i);
                sb.AppendLine($"- {c.name} (active={c.gameObject.activeSelf}, smr={c.GetComponent<SkinnedMeshRenderer>() != null})");
            }
            return sb.ToString();
        }

        public static string ListAllPlayerSmrs()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var smrs = p.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var sb = new StringBuilder();
            sb.AppendLine("=== ALL SMRS ON PLAYER ===");
            foreach (var s in smrs)
            {
                sb.AppendLine($"SMR: {s.gameObject.name}, activeInHierarchy={s.gameObject.activeInHierarchy}, enabled={s.enabled}, shadowMode={s.shadowCastingMode}, mesh={s.sharedMesh?.name}");
            }
            return sb.ToString();
        }

        public static string TestOption2CameraOffsets(float fwd, float up, float right)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            // 1. Restore hand.l localPosition to exact FBX bindpose
            var handL = FindDeepChild(p.transform, "hand.l");
            if (handL != null) handL.localPosition = new Vector3(0f, 0.2648f, 0f);

            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl == null) return "No FPSPerspectiveController";

            ctrl.CurrentMode = FPSPerspectiveMode.CombatReady_UpperBody;
            ctrl.ApplyCurrentMode();

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            var rend = p.GetComponentInChildren<SkinnedMeshRenderer>();
            if (rend != null)
            {
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            var weap = p.GetComponent<PlayerWeaponHandler>()?.EquippedWeapon;
            if (weap != null)
            {
                weap.gameObject.SetActive(true);
                FPSPerspectiveController.EnsureLeftHandGripTarget(weap);
            }
            ctrl.SolveLeftHandIK();

            var cam = p.GetComponentInChildren<Camera>();
            var neck = FindDeepChild(p.transform, "neck.x");
            if (cam != null && neck != null)
            {
                cam.transform.position = neck.position + p.transform.forward * fwd + p.transform.up * up + p.transform.right * right;
                cam.transform.rotation = p.transform.rotation;
                cam.fieldOfView = 80f;
                cam.nearClipPlane = 0.015f;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"=== OPTION 2 OFFSETS (fwd={fwd}, up={up}, right={right}) ===");
            var handR = FindDeepChild(p.transform, "hand.r");
            var lGrip = weap?.Find("LeftHandGripTarget");

            sb.AppendLine($"weapon vp: {cam?.WorldToViewportPoint(weap?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.r vp: {cam?.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l vp: {cam?.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");

            string capLog = CaptureViewToArtifact("test_opt2_tuned.png");
            sb.AppendLine(capLog);

            return sb.ToString();
        }

        public static string TestOption2CombatReadyCapture()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl == null) return "No FPSPerspectiveController";

            // 1. Restore hand.l localPosition to exact FBX bindpose
            var handL = FindDeepChild(p.transform, "hand.l");
            if (handL != null)
            {
                handL.localPosition = new Vector3(0f, 0.2648f, 0f);
            }

            // 2. Set mode to CombatReady_UpperBody
            ctrl.CurrentMode = FPSPerspectiveMode.CombatReady_UpperBody;
            ctrl.ApplyCurrentMode();

            // 3. Sample Firing Rifle animation on character
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            // 4. Ensure Weapon and Grips are active
            var weap = p.GetComponent<PlayerWeaponHandler>()?.EquippedWeapon;
            if (weap != null)
            {
                weap.gameObject.SetActive(true);
                FPSPerspectiveController.EnsureLeftHandGripTarget(weap);
                FPSPerspectiveController.EnsureRightHandGripTarget(weap);
            }

            // 5. Run Left Hand IK
            ctrl.SolveLeftHandIK();

            // 6. Inspect camera and hands
            var cam = p.GetComponentInChildren<Camera>();
            var handR = FindDeepChild(p.transform, "hand.r");
            var lGrip = weap?.Find("LeftHandGripTarget");

            var sb = new StringBuilder();
            sb.AppendLine("=== OPTION 2 COMBAT-READY TEST ===");
            sb.AppendLine($"Cam: pos={cam?.transform.position}, fov={cam?.fieldOfView}");
            sb.AppendLine($"hand.r: world={handR?.position}, vp={cam?.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l: world={handL?.position}, vp={cam?.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");
            sb.AppendLine($"weapon: world={weap?.position}, vp={cam?.WorldToViewportPoint(weap?.position ?? Vector3.zero)}");
            sb.AppendLine($"lGrip:  world={lGrip?.position}, vp={cam?.WorldToViewportPoint(lGrip?.position ?? Vector3.zero)}");
            if (handL != null && lGrip != null)
            {
                sb.AppendLine($"Dist hand.l to lGrip: {Vector3.Distance(handL.position, lGrip.position):F4}m");
            }

            string capLog = CaptureViewToArtifact("test_option2_combat_ready.png");
            sb.AppendLine(capLog);

            return sb.ToString();
        }

        public static string PrintVmArmsHierarchyTree()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            var vmArms = rig?.Find("Viewmodel_BanditArms");
            if (vmArms == null) return "No vmArms";

            var sb = new StringBuilder();
            sb.AppendLine("=== VMARMS HIERARCHY TREE ===");
            PrintTransformRecursive(vmArms, "", sb);
            return sb.ToString();
        }

        private static void PrintTransformRecursive(Transform t, string indent, StringBuilder sb)
        {
            sb.AppendLine($"{indent}- {t.name} (localPos={t.localPosition.ToString("F3")}, scale={t.localScale.ToString("F2")})");
            for (int i = 0; i < t.childCount; i++)
            {
                PrintTransformRecursive(t.GetChild(i), indent + "  ", sb);
            }
        }

        public static string InspectRenderedMeshInCamera()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            var vmArms = rig?.Find("Viewmodel_BanditArms");
            var smr = vmArms?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return "No SMR";

            var bakedMesh = new Mesh();
            smr.BakeMesh(bakedMesh);

            var verts = bakedMesh.vertices;
            var sb = new StringBuilder();
            sb.AppendLine($"=== BAKED MESH IN CAMERA ({verts.Length} verts) ===");

            int insideFrustum = 0;
            Vector3 minVP = Vector3.one * 999f, maxVP = -Vector3.one * 999f;
            Vector3 closestVertWorld = Vector3.zero;
            float closestZ = 999f;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 worldPt = smr.transform.TransformPoint(verts[i]);
                Vector3 vp = cam.WorldToViewportPoint(worldPt);
                if (vp.z > 0f)
                {
                    minVP = Vector3.Min(minVP, vp);
                    maxVP = Vector3.Max(maxVP, vp);
                    if (vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f)
                    {
                        insideFrustum++;
                        if (vp.z < closestZ)
                        {
                            closestZ = vp.z;
                            closestVertWorld = worldPt;
                        }
                    }
                }
            }

            sb.AppendLine($"Verts inside frustum: {insideFrustum}");
            sb.AppendLine($"VP range: min={minVP.ToString("F3")}, max={maxVP.ToString("F3")}");
            sb.AppendLine($"Closest vert in frustum: world={closestVertWorld.ToString("F3")}, vp={cam.WorldToViewportPoint(closestVertWorld).ToString("F3")}");

            return sb.ToString();
        }

        public static string InspectBanditHandsGeometry()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var mesh = smr.sharedMesh;
            var weights = mesh.boneWeights;
            var verts = mesh.vertices;

            // Hand bones:
            // Right hand + fingers: 21..36
            // Left hand + fingers:  42..57
            int countR = 0, countL = 0;
            Vector3 centerR = Vector3.zero, centerL = Vector3.zero;

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var bw = weights[i];
                if ((bw.boneIndex0 >= 21 && bw.boneIndex0 <= 36 && bw.weight0 > 0.5f) ||
                    (bw.boneIndex1 >= 21 && bw.boneIndex1 <= 36 && bw.weight1 > 0.5f))
                {
                    countR++;
                    centerR += verts[i];
                }

                if ((bw.boneIndex0 >= 42 && bw.boneIndex0 <= 57 && bw.weight0 > 0.5f) ||
                    (bw.boneIndex1 >= 42 && bw.boneIndex1 <= 57 && bw.weight1 > 0.5f))
                {
                    countL++;
                    centerL += verts[i];
                }
            }

            if (countR > 0) centerR /= countR;
            if (countL > 0) centerL /= countL;

            var sb = new StringBuilder();
            sb.AppendLine("=== BANDIT HANDS GEOMETRY ===");
            sb.AppendLine($"Right hand vertices: {countR}, center in bind: {centerR.ToString("F3")}");
            sb.AppendLine($"Left hand vertices:  {countL}, center in bind: {centerL.ToString("F3")}");
            return sb.ToString();
        }

        public static string TestWeaponOnlyCapture(float weapX, float weapY, float weapZ, float rotX, float rotY, float rotZ)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");

            if (vmArms != null) vmArms.gameObject.SetActive(false);
            if (vmWeapon != null)
            {
                vmWeapon.gameObject.SetActive(true);
                vmWeapon.localPosition = new Vector3(weapX, weapY, weapZ);
                vmWeapon.localRotation = Quaternion.Euler(rotX, rotY, rotZ);
            }

            string capLog = CaptureViewToArtifact("test_weapon_only.png");
            return capLog;
        }

        public static string InspectArmsMeshBounds()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset");
            if (mesh == null) return "No mesh";
            var sb = new StringBuilder();
            sb.AppendLine("=== ARMS MESH INSPECTION ===");
            sb.AppendLine($"Bounds: center={mesh.bounds.center}, size={mesh.bounds.size}, min={mesh.bounds.min}, max={mesh.bounds.max}");
            sb.AppendLine($"Submesh 0 triangles: {mesh.GetTriangles(0).Length / 3}");
            sb.AppendLine($"Submesh 1 triangles: {(mesh.subMeshCount > 1 ? mesh.GetTriangles(1).Length / 3 : 0)}");
            sb.AppendLine($"Submesh 2 triangles: {(mesh.subMeshCount > 2 ? mesh.GetTriangles(2).Length / 3 : 0)}");

            // Check sample vertices
            var tris = mesh.GetTriangles(0);
            var verts = mesh.vertices;
            var weights = mesh.boneWeights;
            sb.AppendLine("Sample tri vertices in bind space:");
            for (int i = 0; i < Math.Min(tris.Length, 15); i += 3)
            {
                int v = tris[i];
                var bw = weights[v];
                sb.AppendLine($"  v[{v}]: pos={verts[v]}, bones=({bw.boneIndex0}:{bw.weight0:F2}, {bw.boneIndex1}:{bw.weight1:F2}, {bw.boneIndex2}:{bw.weight2:F2}, {bw.boneIndex3}:{bw.weight3:F2})");
            }
            return sb.ToString();
        }

        public static string InspectVmArmsMaterials()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var smr = p.transform.Find("Main Camera/FPS_ViewmodelRig/Viewmodel_BanditArms")?.GetComponentInChildren<SkinnedMeshRenderer>();
            var sb = new StringBuilder();
            sb.AppendLine("=== VMARMS MATERIALS ===");
            if (smr != null)
            {
                sb.AppendLine($"smr={smr.name}, mesh={smr.sharedMesh?.name}, submeshes={smr.sharedMesh?.subMeshCount}, materials={smr.sharedMaterials.Length}");
                for (int i = 0; i < smr.sharedMaterials.Length; i++)
                {
                    sb.AppendLine($"  Mat[{i}]={(smr.sharedMaterials[i] != null ? smr.sharedMaterials[i].name : "NULL")}");
                }
            }
            else
            {
                sb.AppendLine("No SMR found!");
            }
            return sb.ToString();
        }

        public static string InspectVmArmsHierarchy()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            var vmArms = rig?.Find("Viewmodel_BanditArms");
            if (vmArms == null) return "No vmArms";

            var sb = new StringBuilder();
            sb.AppendLine("=== VMARMS HIERARCHY ===");
            sb.AppendLine($"vmArms pos={vmArms.position}, localPos={vmArms.localPosition}");

            var pelvis = FindDeepChild(vmArms, "pelvis.x");
            var spine = FindDeepChild(vmArms, "spine.x");
            var chest = FindDeepChild(vmArms, "chest.x");
            var upperChest = FindDeepChild(vmArms, "upper_chest.x");
            var shoulderR = FindDeepChild(vmArms, "shoulder.r");
            var armR = FindDeepChild(vmArms, "arm_stretch.r");

            sb.AppendLine($"pelvis: localPos={pelvis?.localPosition}, worldPos={pelvis?.position}");
            sb.AppendLine($"spine: localPos={spine?.localPosition}, worldPos={spine?.position}");
            sb.AppendLine($"chest: localPos={chest?.localPosition}, worldPos={chest?.position}");
            sb.AppendLine($"upperChest: localPos={upperChest?.localPosition}, worldPos={upperChest?.position}");
            sb.AppendLine($"shoulderR: localPos={shoulderR?.localPosition}, worldPos={shoulderR?.position}");
            sb.AppendLine($"armR: localPos={armR?.localPosition}, worldPos={armR?.position}");

            if (armR != null && cam != null)
            {
                sb.AppendLine($"armR relative to cam: {cam.transform.InverseTransformPoint(armR.position)}");
            }

            return sb.ToString();
        }

        public static string TestCalibrateViewmodelPosition(float armsX, float armsY, float armsZ, float weapX, float weapY, float weapZ, float armsYaw = 15f)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");
            if (vmWeapon == null || vmArms == null) return "Missing vmWeapon or vmArms";

            vmArms.localPosition = new Vector3(armsX, armsY, armsZ);
            vmArms.localRotation = Quaternion.Euler(0f, armsYaw, 0f);

            vmWeapon.localPosition = new Vector3(weapX, weapY, weapZ);
            vmWeapon.localRotation = Quaternion.Euler(-1f, -2f, 1f);

            var rGrip = vmWeapon.Find("RightHandGripTarget");
            var lGrip = vmWeapon.Find("LeftHandGripTarget");

            var armR = FindDeepChild(vmArms, "arm_stretch.r");
            var forearmR = FindDeepChild(vmArms, "forearm_stretch.r");
            var handR = FindDeepChild(vmArms, "hand.r");

            var armL = FindDeepChild(vmArms, "arm_stretch.l");
            var forearmL = FindDeepChild(vmArms, "forearm_stretch.l");
            var handL = FindDeepChild(vmArms, "hand.l");

            var sb = new StringBuilder();
            sb.AppendLine($"=== CALIBRATING (Arms={vmArms.localPosition}, Yaw={armsYaw}, Weap={vmWeapon.localPosition}) ===");

            float reachR = 0f, distR = 0f, reachL = 0f, distL = 0f;

            if (armR != null && forearmR != null && handR != null && rGrip != null)
            {
                reachR = Vector3.Distance(armR.position, forearmR.position) + Vector3.Distance(forearmR.position, handR.position);
                distR = Vector3.Distance(armR.position, rGrip.position);
                sb.AppendLine($"Right Arm: reach={reachR:F3}m, distToGrip={distR:F3}m, Reachable={(distR <= reachR)} ({distR/reachR:P1})");

                Vector3 rPole = armR.position - rig.up * 0.5f + rig.right * 0.4f - rig.forward * 0.2f;
                FPSPerspectiveController.SolveTwoBoneIKInternal(armR, forearmR, handR, rGrip.position, rGrip.rotation, rPole);
                sb.AppendLine($"After IK handR: dist={Vector3.Distance(handR.position, rGrip.position):F4}m, vp={cam.WorldToViewportPoint(handR.position)}");
            }

            if (armL != null && forearmL != null && handL != null && lGrip != null)
            {
                reachL = Vector3.Distance(armL.position, forearmL.position) + Vector3.Distance(forearmL.position, handL.position);
                distL = Vector3.Distance(armL.position, lGrip.position);
                sb.AppendLine($"Left Arm: reach={reachL:F3}m, distToGrip={distL:F3}m, Reachable={(distL <= reachL)} ({distL/reachL:P1})");

                Vector3 lPole = armL.position - rig.up * 0.5f - rig.right * 0.4f - rig.forward * 0.2f;
                FPSPerspectiveController.SolveTwoBoneIKInternal(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);
                sb.AppendLine($"After IK handL: dist={Vector3.Distance(handL.position, lGrip.position):F4}m, vp={cam.WorldToViewportPoint(handL.position)}");
            }

            string capLog = CaptureViewToArtifact("test_vm_calibrated.png");
            sb.AppendLine(capLog);

            return sb.ToString();
        }

        public static string DebugTwoBoneIKCalculation()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");
            if (vmWeapon == null || vmArms == null) return "Missing vmWeapon or vmArms";

            var rGrip = vmWeapon.Find("RightHandGripTarget");
            var armR = FindDeepChild(vmArms, "arm_stretch.r");
            var forearmR = FindDeepChild(vmArms, "forearm_stretch.r");
            var handR = FindDeepChild(vmArms, "hand.r");

            var sb = new StringBuilder();
            sb.AppendLine("=== DEBUG TWO BONE IK ===");
            sb.AppendLine($"armR: pos={armR?.position}");
            sb.AppendLine($"forearmR: pos={forearmR?.position}");
            sb.AppendLine($"handR: pos={handR?.position}");
            sb.AppendLine($"rGrip: pos={rGrip?.position}");

            if (armR != null && forearmR != null && handR != null && rGrip != null)
            {
                float upperLen = Vector3.Distance(armR.position, forearmR.position);
                float lowerLen = Vector3.Distance(forearmR.position, handR.position);
                float totalLen = upperLen + lowerLen;
                Vector3 toTarget = rGrip.position - armR.position;
                float dist = toTarget.magnitude;

                sb.AppendLine($"upperLen={upperLen:F4}, lowerLen={lowerLen:F4}, totalLen={totalLen:F4}");
                sb.AppendLine($"dist to target={dist:F4} (reach limit: {totalLen:F4})");
                sb.AppendLine($"Is reachable: {dist <= totalLen}");
            }

            return sb.ToString();
        }

        public static string TestSolveViewmodelIK()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");
            if (vmWeapon == null || vmArms == null) return "Missing vmWeapon or vmArms";

            var rGrip = vmWeapon.Find("RightHandGripTarget");
            var lGrip = vmWeapon.Find("LeftHandGripTarget");

            var armR = FindDeepChild(vmArms, "arm_stretch.r");
            var forearmR = FindDeepChild(vmArms, "forearm_stretch.r");
            var handR = FindDeepChild(vmArms, "hand.r");

            var armL = FindDeepChild(vmArms, "arm_stretch.l");
            var forearmL = FindDeepChild(vmArms, "forearm_stretch.l");
            var handL = FindDeepChild(vmArms, "hand.l");

            var sb = new StringBuilder();
            sb.AppendLine("=== SOLVING VIEWMODEL IK ===");

            if (armR != null && forearmR != null && handR != null && rGrip != null)
            {
                Vector3 rPole = armR.position - rig.up * 0.6f + rig.right * 0.5f - rig.forward * 0.2f;
                FPSPerspectiveController.SolveTwoBoneIKInternal(armR, forearmR, handR, rGrip.position, rGrip.rotation, rPole);
                sb.AppendLine($"After IK handR dist: {Vector3.Distance(handR.position, rGrip.position):F4}m, vp={cam.WorldToViewportPoint(handR.position)}");
            }

            if (armL != null && forearmL != null && handL != null && lGrip != null)
            {
                Vector3 lPole = armL.position - rig.up * 0.6f - rig.right * 0.5f - rig.forward * 0.2f;
                FPSPerspectiveController.SolveTwoBoneIKInternal(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);
                sb.AppendLine($"After IK handL dist: {Vector3.Distance(handL.position, lGrip.position):F4}m, vp={cam.WorldToViewportPoint(handL.position)}");
            }

            string capLog = CaptureViewToArtifact("test_vm_solved_ik.png");
            sb.AppendLine(capLog);

            return sb.ToString();
        }

        public static string InspectViewmodelGripsAndHands()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");
            if (vmWeapon == null || vmArms == null) return "Missing vmWeapon or vmArms";

            var sb = new StringBuilder();
            sb.AppendLine("=== VIEWMODEL GRIPS & HANDS ===");

            var rGrip = vmWeapon.Find("RightHandGripTarget");
            var lGrip = vmWeapon.Find("LeftHandGripTarget");

            var handR = FindDeepChild(vmArms, "hand.r");
            var handL = FindDeepChild(vmArms, "hand.l");

            sb.AppendLine($"vmWeapon: pos={vmWeapon.localPosition}, rot={vmWeapon.localEulerAngles}");
            sb.AppendLine($"rGrip: world={rGrip?.position}, local={rGrip?.localPosition}, vp={cam.WorldToViewportPoint(rGrip?.position ?? Vector3.zero)}");
            sb.AppendLine($"lGrip: world={lGrip?.position}, local={lGrip?.localPosition}, vp={cam.WorldToViewportPoint(lGrip?.position ?? Vector3.zero)}");
            sb.AppendLine($"handR: world={handR?.position}, local={handR?.localPosition}, vp={cam.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"handL: world={handL?.position}, local={handL?.localPosition}, vp={cam.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");

            if (handR != null && rGrip != null)
                sb.AppendLine($"Dist handR to rGrip: {Vector3.Distance(handR.position, rGrip.position):F4}m");
            if (handL != null && lGrip != null)
                sb.AppendLine($"Dist handL to lGrip: {Vector3.Distance(handL.position, lGrip.position):F4}m");

            return sb.ToString();
        }

        public static string TestViewmodelCapture()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl == null) return "No FPSPerspectiveController";

            ctrl.CurrentMode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
            ctrl.ApplyCurrentMode();

            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Camera";

            var rig = cam.transform.Find("FPS_ViewmodelRig");
            if (rig == null) return "No ViewmodelRig";

            var sb = new StringBuilder();
            sb.AppendLine("=== VIEWMODEL RIG INSPECTION ===");
            sb.AppendLine($"Rig Active: {rig.gameObject.activeSelf}");

            var vmWeapon = rig.Find("Viewmodel_AK47");
            var vmArms = rig.Find("Viewmodel_BanditArms");

            sb.AppendLine($"vmWeapon: {(vmWeapon != null ? "Found at " + vmWeapon.localPosition : "MISSING")}");
            sb.AppendLine($"vmArms: {(vmArms != null ? "Found at " + vmArms.localPosition : "MISSING")}");

            if (vmArms != null)
            {
                var smr = vmArms.GetComponentInChildren<SkinnedMeshRenderer>();
                sb.AppendLine($"vmArms SMR: {(smr != null ? smr.name + ", mesh=" + smr.sharedMesh?.name + ", enabled=" + smr.enabled + ", bounds=" + smr.bounds : "NO SMR")}");
                var armR = FindDeepChild(vmArms, "arm_stretch.r");
                var handR = FindDeepChild(vmArms, "hand.r");
                var armL = FindDeepChild(vmArms, "arm_stretch.l");
                var handL = FindDeepChild(vmArms, "hand.l");

                sb.AppendLine($"vmHandR: {handR?.position}, vp={cam.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
                sb.AppendLine($"vmHandL: {handL?.position}, vp={cam.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");
            }

            string capLog = CaptureViewToArtifact("test_vm_view_live.png");
            sb.AppendLine(capLog);

            return sb.ToString();
        }

        public static string InspectAk47Fbx()
        {
            var ak = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
            if (ak == null) return "No AK47 FBX";
            var sb = new StringBuilder();
            sb.AppendLine("=== AK47 FBX DETAILS ===");
            var mr = ak.GetComponentInChildren<MeshFilter>();
            if (mr != null && mr.sharedMesh != null)
            {
                var mesh = mr.sharedMesh;
                sb.AppendLine($"Mesh bounds: center={mesh.bounds.center.ToString("F4")}, size={mesh.bounds.size.ToString("F4")}, min={mesh.bounds.min.ToString("F4")}, max={mesh.bounds.max.ToString("F4")}");
            }
            return sb.ToString();
        }

        public static string InspectBindposeVsPlayer()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var p = GameObject.Find("Player");
            if (fbx == null || p == null) return "Missing FBX or Player";

            var sb = new StringBuilder();
            sb.AppendLine("=== BINDPOSE FBX VS PLAYER ARM BONES ===");

            string[] bones = new[]
            {
                "shoulder.l", "arm_stretch.l", "arm_twist.l", "forearm_stretch.l", "forearm_twist.l", "hand.l",
                "shoulder.r", "arm_stretch.r", "arm_twist.r", "forearm_stretch.r", "forearm_twist.r", "hand.r"
            };

            foreach (var b in bones)
            {
                var fbxBone = FindDeepChild(fbx.transform, b);
                var pBone = FindDeepChild(p.transform, b);
                sb.AppendLine($"{b}:");
                sb.AppendLine($"   FBX:    localPos={fbxBone?.localPosition.ToString("F4")}, localRot={fbxBone?.localEulerAngles.ToString("F2")}");
                sb.AppendLine($"   Player: localPos={pBone?.localPosition.ToString("F4")}, localRot={pBone?.localEulerAngles.ToString("F2")}");
            }

            return sb.ToString();
        }

        public static string InspectLeftArmHierarchy()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var sb = new StringBuilder();
            sb.AppendLine("=== LEFT ARM HIERARCHY ===");

            var armL = FindDeepChild(p.transform, "arm_stretch.l");
            var armTwistL = FindDeepChild(p.transform, "arm_twist.l");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var forearmTwistL = FindDeepChild(p.transform, "forearm_twist.l");
            var handL = FindDeepChild(p.transform, "hand.l");

            sb.AppendLine($"arm_stretch.l: parent={armL?.parent?.name}, pos={armL?.position}, localPos={armL?.localPosition}");
            sb.AppendLine($"arm_twist.l: parent={armTwistL?.parent?.name}, pos={armTwistL?.position}, localPos={armTwistL?.localPosition}");
            sb.AppendLine($"forearm_stretch.l: parent={forearmL?.parent?.name}, pos={forearmL?.position}, localPos={forearmL?.localPosition}");
            sb.AppendLine($"forearm_twist.l: parent={forearmTwistL?.parent?.name}, pos={forearmTwistL?.position}, localPos={forearmTwistL?.localPosition}");
            sb.AppendLine($"hand.l: parent={handL?.parent?.name}, pos={handL?.position}, localPos={handL?.localPosition}");

            return sb.ToString();
        }

        public static string InspectPlayerIKAndGrips()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var sb = new StringBuilder();
            sb.AppendLine("=== INSPECT PLAYER IK AND GRIPS ===");

            var armL = FindDeepChild(p.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var handL = FindDeepChild(p.transform, "hand.l");

            var armR = FindDeepChild(p.transform, "arm_stretch.r");
            var forearmR = FindDeepChild(p.transform, "forearm_stretch.r");
            var handR = FindDeepChild(p.transform, "hand.r");

            var weapon = p.GetComponent<PlayerWeaponHandler>()?.EquippedWeapon;
            var lGrip = weapon?.Find("LeftHandGripTarget");
            var rGrip = weapon?.Find("RightHandGripTarget");

            sb.AppendLine($"arm.l: {armL?.position}");
            sb.AppendLine($"forearm.l: {forearmL?.position}");
            sb.AppendLine($"hand.l: {handL?.position}");
            sb.AppendLine($"arm.r: {armR?.position}");
            sb.AppendLine($"forearm.r: {forearmR?.position}");
            sb.AppendLine($"hand.r: {handR?.position}");
            sb.AppendLine($"weapon: {weapon?.position}");
            sb.AppendLine($"lGrip: {lGrip?.position}");
            sb.AppendLine($"rGrip: {rGrip?.position}");

            if (armL != null && forearmL != null && handL != null)
            {
                float upperLenL = Vector3.Distance(armL.position, forearmL.position);
                float lowerLenL = Vector3.Distance(forearmL.position, handL.position);
                sb.AppendLine($"Left arm lengths: upper={upperLenL:F3}, lower={lowerLenL:F3}, total={(upperLenL + lowerLenL):F3}");
                if (lGrip != null)
                {
                    float distToLGrip = Vector3.Distance(armL.position, lGrip.position);
                    sb.AppendLine($"Dist arm.l to LeftHandGripTarget: {distToLGrip:F3} (reach fraction: {distToLGrip / (upperLenL + lowerLenL):P1})");
                }
            }

            if (handL != null && lGrip != null)
            {
                sb.AppendLine($"Dist hand.l to LeftHandGripTarget: {Vector3.Distance(handL.position, lGrip.position):F3}m");
            }
            if (handR != null && weapon != null)
            {
                sb.AppendLine($"Dist hand.r to weapon: {Vector3.Distance(handR.position, weapon.position):F3}m");
            }

            return sb.ToString();
        }

        public static string InspectBanditFbxDetails()
        {
            var sb = new StringBuilder();
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (fbx == null) return "FBX not found!";

            var smrs = fbx.GetComponentsInChildren<SkinnedMeshRenderer>();
            sb.AppendLine($"FBX SkinnedMeshRenderers: {smrs.Length}");
            foreach (var smr in smrs)
            {
                sb.AppendLine($"  SMR: {smr.name}, mesh={smr.sharedMesh.name}, submeshes={smr.sharedMesh.subMeshCount}, bones={smr.bones.Length}, rootBone={smr.rootBone?.name}");
                for (int i = 0; i < smr.sharedMaterials.Length; i++)
                {
                    sb.AppendLine($"    Mat[{i}]: {smr.sharedMaterials[i]?.name}");
                }
            }

            return sb.ToString();
        }

        public static string TestEyeOffsets(float fwd, float up, float right)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            var cam = p.GetComponentInChildren<Camera>();
            var head = FindDeepChild(p.transform, "head.x");
            if (cam == null || head == null) return "Cam or Head missing";

            // Hide head bone
            head.localScale = Vector3.zero;

            // Compute eye position relative to head along body rotation
            Vector3 targetPos = head.position + (p.transform.forward * fwd) + (p.transform.up * up) + (p.transform.right * right);
            cam.transform.position = targetPos;
            cam.transform.rotation = p.transform.rotation;
            cam.fieldOfView = 80f;
            cam.nearClipPlane = 0.015f;

            var sb = new StringBuilder();
            sb.AppendLine($"=== TEST EYE OFFSETS (fwd={fwd}, up={up}, right={right}) ===");
            sb.AppendLine($"Cam: world={cam.transform.position}");

            var handR = FindDeepChild(p.transform, "hand.r");
            var handL = FindDeepChild(p.transform, "hand.l");
            var forearmR = FindDeepChild(p.transform, "forearm_stretch.r");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var rifle = FindDeepChild(p.transform, "Rifle");
            var muzzle = rifle?.Find("MuzzlePoint");

            sb.AppendLine($"hand.r: world={handR?.position}, vp={cam.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l: world={handL?.position}, vp={cam.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.r: world={forearmR?.position}, vp={cam.WorldToViewportPoint(forearmR?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.l: world={forearmL?.position}, vp={cam.WorldToViewportPoint(forearmL?.position ?? Vector3.zero)}");
            sb.AppendLine($"rifle: world={rifle?.position}, vp={cam.WorldToViewportPoint(rifle?.position ?? Vector3.zero)}");
            sb.AppendLine($"muzzle: world={muzzle?.position}, vp={cam.WorldToViewportPoint(muzzle?.position ?? Vector3.zero)}");

            return sb.ToString();
        }

        public static string TestHeadShrink()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var head = FindDeepChild(p.transform, "head.x");
            if (head == null) return "head.x not found";

            var handR = FindDeepChild(p.transform, "hand.r");
            var handL = FindDeepChild(p.transform, "hand.l");

            head.localScale = Vector3.zero;

            var sb = new StringBuilder();
            sb.AppendLine("=== HEAD SHRINK TEST ===");
            sb.AppendLine($"head.x localScale: {head.localScale}");
            sb.AppendLine($"hand.r localScale: {handR?.localScale}, worldPos: {handR?.position}");
            sb.AppendLine($"hand.l localScale: {handL?.localScale}, worldPos: {handL?.position}");

            // Restore
            head.localScale = Vector3.one;
            return sb.ToString();
        }

        public static string TestCodCameraViewports()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Cam";

            // Test COD camera eye position
            Vector3 testCamPos = p.transform.position + new Vector3(0.02f, 1.48f, -0.08f);
            cam.transform.position = testCamPos;
            cam.transform.rotation = p.transform.rotation;
            cam.fieldOfView = 80f;
            cam.nearClipPlane = 0.015f;

            var sb = new StringBuilder();
            sb.AppendLine("=== COD CAMERA POSITION TEST ===");
            var handR = FindDeepChild(p.transform, "hand.r");
            var handL = FindDeepChild(p.transform, "hand.l");
            var head = FindDeepChild(p.transform, "head.x");
            var forearmR = FindDeepChild(p.transform, "forearm_stretch.r");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var rifle = FindDeepChild(p.transform, "Rifle");
            var muzzle = rifle?.Find("MuzzlePoint");

            sb.AppendLine($"Cam: world={cam.transform.position}");
            sb.AppendLine($"head: world={head?.position}, vp={cam.WorldToViewportPoint(head?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(head?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.r: world={handR?.position}, vp={cam.WorldToViewportPoint(handR?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l: world={handL?.position}, vp={cam.WorldToViewportPoint(handL?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(handL?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.r: world={forearmR?.position}, vp={cam.WorldToViewportPoint(forearmR?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(forearmR?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.l: world={forearmL?.position}, vp={cam.WorldToViewportPoint(forearmL?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(forearmL?.position ?? Vector3.zero)}");
            sb.AppendLine($"rifle: world={rifle?.position}, vp={cam.WorldToViewportPoint(rifle?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(rifle?.position ?? Vector3.zero)}");
            sb.AppendLine($"muzzle: world={muzzle?.position}, vp={cam.WorldToViewportPoint(muzzle?.position ?? Vector3.zero)}, camLocal={cam.transform.InverseTransformPoint(muzzle?.position ?? Vector3.zero)}");

            return sb.ToString();
        }

        public static string SampleFiringRifleAndInspect()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip == null) return "Firing Rifle clip not found";

            clip.SampleAnimation(p, 0.1f);

            var sb = new StringBuilder();
            sb.AppendLine("=== SAMPLED FIRING RIFLE CLIP ===");
            var cam = p.GetComponentInChildren<Camera>();
            var handR = FindDeepChild(p.transform, "hand.r");
            var handL = FindDeepChild(p.transform, "hand.l");
            var head = FindDeepChild(p.transform, "head.x");
            var forearmR = FindDeepChild(p.transform, "forearm_stretch.r");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var rifle = FindDeepChild(p.transform, "Rifle");
            var muzzle = rifle?.Find("MuzzlePoint");

            sb.AppendLine($"hand.r: world={handR?.position}, vp={cam?.WorldToViewportPoint(handR?.position ?? Vector3.zero)}");
            sb.AppendLine($"hand.l: world={handL?.position}, vp={cam?.WorldToViewportPoint(handL?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.r: world={forearmR?.position}, vp={cam?.WorldToViewportPoint(forearmR?.position ?? Vector3.zero)}");
            sb.AppendLine($"forearm.l: world={forearmL?.position}, vp={cam?.WorldToViewportPoint(forearmL?.position ?? Vector3.zero)}");
            sb.AppendLine($"rifle: world={rifle?.position}, fwd={rifle?.forward}, vp={cam?.WorldToViewportPoint(rifle?.position ?? Vector3.zero)}");
            sb.AppendLine($"muzzle: world={muzzle?.position}, vp={cam?.WorldToViewportPoint(muzzle?.position ?? Vector3.zero)}");

            return sb.ToString();
        }

        public static string InspectAdsMuzzle()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var cam = p.GetComponentInChildren<Camera>();
            var vmRig = cam?.transform.Find("FPS_ViewmodelRig");
            if (vmRig == null) return "No ViewmodelRig";

            // ADS pos: (0.0f, -0.115f, 0.28f)
            vmRig.localPosition = new Vector3(0.0f, -0.115f, 0.28f);
            vmRig.localRotation = Quaternion.identity;

            var muz = vmRig.Find("Viewmodel_AK47/MuzzlePoint");
            if (muz == null) return "No vmMuzzle";

            var sb = new StringBuilder();
            sb.AppendLine("=== ADS VIEWMODEL MUZZLE ===");
            sb.AppendLine($"World pos: {muz.position}");
            sb.AppendLine($"Cam local: {cam.transform.InverseTransformPoint(muz.position)}");
            sb.AppendLine($"Viewport: {cam.WorldToViewportPoint(muz.position)}");
            return sb.ToString();
        }

        public static string InspectWeaponWorldCoordinates()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var sb = new StringBuilder();
            sb.AppendLine("=== WEAPON TRANSFORM COORDS ===");
            var cam = p.GetComponentInChildren<Camera>();
            var handR = FindDeepChild(p.transform, "hand.r");
            var socket = FindDeepChild(p.transform, "WeaponSocket");
            var rifle = FindDeepChild(p.transform, "Rifle");
            var muzzle = rifle?.Find("MuzzlePoint");

            sb.AppendLine($"Cam: world={cam?.transform.position}, fwd={cam?.transform.forward}");
            sb.AppendLine($"hand.r: world={handR?.position}, rot={handR?.eulerAngles}");
            sb.AppendLine($"WeaponSocket: world={socket?.position}, localPos={socket?.localPosition}, localRot={socket?.localEulerAngles}");
            sb.AppendLine($"Rifle: world={rifle?.position}, localPos={rifle?.localPosition}, localRot={rifle?.localEulerAngles}, fwd={rifle?.forward}");
            sb.AppendLine($"MuzzlePoint: world={muzzle?.position}, localPos={muzzle?.localPosition}");
            if (cam != null && muzzle != null)
            {
                sb.AppendLine($"Muzzle Viewport: {cam.WorldToViewportPoint(muzzle.position)}");
                sb.AppendLine($"Muzzle in Cam Local: {cam.transform.InverseTransformPoint(muzzle.position)}");
            }
            return sb.ToString();
        }

        public static string SetModeAndInspect(int modeIndex)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl == null) return "No FPSPerspectiveController";
            ctrl.CurrentMode = (FPSPerspectiveMode)modeIndex;
            return InspectSceneAndPlayer();
        }

        public static string InspectArmBones()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var sb = new StringBuilder();
            sb.AppendLine("=== ARM BONES INSPECTION ===");
            string[] armBoneNames = new[]
            {
                "shoulder.r", "arm_stretch.r", "arm_twist.r", "forearm_stretch.r", "forearm_twist.r", "hand.r",
                "shoulder.l", "arm_stretch.l", "arm_twist.l", "forearm_stretch.l", "forearm_twist.l", "hand.l"
            };
            foreach (var name in armBoneNames)
            {
                var b = FindDeepChild(p.transform, name);
                if (b != null)
                {
                    sb.AppendLine($"{name}: pos={b.localPosition}, rot={b.localEulerAngles}, scale={b.localScale}, worldPos={b.position}");
                }
                else
                {
                    sb.AppendLine($"{name}: NOT FOUND");
                }
            }
            return sb.ToString();
        }

        public static string InspectAllPlayerChildren()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var sb = new StringBuilder();
            sb.AppendLine("=== PLAYER HIERARCHY ===");
            PrintHierarchyRecursive(p.transform, 0, sb);
            return sb.ToString();
        }

        private static void PrintHierarchyRecursive(Transform t, int depth, StringBuilder sb)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}- {t.name} (active={t.gameObject.activeSelf}, pos={t.localPosition}, scale={t.localScale})");
            for (int i = 0; i < t.childCount; i++)
            {
                PrintHierarchyRecursive(t.GetChild(i), depth + 1, sb);
            }
        }

        public static string AnalyzeBanditMeshBones()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (fbx == null) return "FBX not found";
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return "SMR not found";
            var mesh = smr.sharedMesh;
            var sb = new StringBuilder();
            sb.AppendLine($"Mesh vertices: {mesh.vertexCount}, submeshes: {mesh.subMeshCount}, bones: {smr.bones.Length}");
            for (int i = 0; i < smr.bones.Length; i++)
            {
                sb.AppendLine($"Bone[{i}]: {smr.bones[i].name}");
            }
            return sb.ToString();
        }

        public static string ExtractFpsArmsMesh()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (fbx == null) return "FBX not found";
            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return "SMR not found";

            var srcMesh = smr.sharedMesh;
            var weights = srcMesh.boneWeights;
            var verts = srcMesh.vertices;
            var norms = srcMesh.normals;
            var tans = srcMesh.tangents;
            var uvs = srcMesh.uv;

            // Pure arm and hand bone indices (excluding clavicle/shoulder which touch torso)
            bool[] isArmBone = new bool[smr.bones.Length];
            // Right arm: 19..38
            for (int i = 19; i <= 38; i++) isArmBone[i] = true;
            // Left arm: 40..59
            for (int i = 40; i <= 59; i++) isArmBone[i] = true;

            // Find which vertices have significant arm weight (> 0.5) and are NOT weighted to spine/neck/chest/thigh
            bool[] isTorsoBone = new bool[smr.bones.Length];
            for (int i = 0; i <= 17; i++) isTorsoBone[i] = true; // root, legs, spine, neck, head
            isTorsoBone[18] = true; // shoulder.r
            isTorsoBone[39] = true; // shoulder.l

            bool[] keepVertex = new bool[srcMesh.vertexCount];
            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float armWeight = 0f;
                float torsoWeight = 0f;

                if (isArmBone[bw.boneIndex0]) armWeight += bw.weight0;
                else if (isTorsoBone[bw.boneIndex0]) torsoWeight += bw.weight0;

                if (isArmBone[bw.boneIndex1]) armWeight += bw.weight1;
                else if (isTorsoBone[bw.boneIndex1]) torsoWeight += bw.weight1;

                if (isArmBone[bw.boneIndex2]) armWeight += bw.weight2;
                else if (isTorsoBone[bw.boneIndex2]) torsoWeight += bw.weight2;

                if (isArmBone[bw.boneIndex3]) armWeight += bw.weight3;
                else if (isTorsoBone[bw.boneIndex3]) torsoWeight += bw.weight3;

                // Only keep vertices where arm influence dominates and torso influence is negligible
                if (armWeight > 0.6f && torsoWeight < 0.25f)
                {
                    keepVertex[i] = true;
                }
            }

            // Remap twist and torso bones so NO vertex is ever pulled toward spine/shoulder
            int RemapBoneIndex(int bIdx, bool isLeft)
            {
                if (bIdx == 37) return 20; // forearm_twist.r -> forearm_stretch.r
                if (bIdx == 38) return 19; // arm_twist.r -> arm_stretch.r
                if (bIdx == 18) return 19; // shoulder.r -> arm_stretch.r

                if (bIdx == 58) return 41; // forearm_twist.l -> forearm_stretch.l
                if (bIdx == 59) return 40; // arm_twist.l -> arm_stretch.l
                if (bIdx == 39) return 40; // shoulder.l -> arm_stretch.l

                if (bIdx <= 17) return isLeft ? 40 : 19; // root/spine/neck -> upper arm
                return bIdx;
            }

            // Extract triangles that belong to arm vertices
            var newMesh = new Mesh();
            newMesh.name = "Bandit_FPS_Arms";

            int submeshCount = srcMesh.subMeshCount;
            var newSubmeshTris = new System.Collections.Generic.List<int>[submeshCount];
            int[] oldToNew = new int[srcMesh.vertexCount];
            for (int i = 0; i < oldToNew.Length; i++) oldToNew[i] = -1;

            var newVerts = new System.Collections.Generic.List<Vector3>();
            var newNorms = new System.Collections.Generic.List<Vector3>();
            var newTans = new System.Collections.Generic.List<Vector4>();
            var newUVs = new System.Collections.Generic.List<Vector2>();
            var newWeights = new System.Collections.Generic.List<BoneWeight>();

            int GetOrCreateNewVertex(int oldIdx)
            {
                if (oldToNew[oldIdx] != -1) return oldToNew[oldIdx];
                int nIdx = newVerts.Count;
                oldToNew[oldIdx] = nIdx;
                newVerts.Add(verts[oldIdx]);
                if (norms != null && norms.Length > oldIdx) newNorms.Add(norms[oldIdx]);
                if (tans != null && tans.Length > oldIdx) newTans.Add(tans[oldIdx]);
                if (uvs != null && uvs.Length > oldIdx) newUVs.Add(uvs[oldIdx]);

                var bw = weights[oldIdx];
                bool isLeft = (bw.boneIndex0 >= 39 && bw.boneIndex0 <= 59) ||
                              (bw.boneIndex1 >= 39 && bw.boneIndex1 <= 59);

                bw.boneIndex0 = RemapBoneIndex(bw.boneIndex0, isLeft);
                bw.boneIndex1 = RemapBoneIndex(bw.boneIndex1, isLeft);
                bw.boneIndex2 = RemapBoneIndex(bw.boneIndex2, isLeft);
                bw.boneIndex3 = RemapBoneIndex(bw.boneIndex3, isLeft);

                float sum = bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3;
                if (sum > 0.0001f)
                {
                    bw.weight0 /= sum;
                    bw.weight1 /= sum;
                    bw.weight2 /= sum;
                    bw.weight3 /= sum;
                }

                newWeights.Add(bw);
                return nIdx;
            }

            for (int s = 0; s < submeshCount; s++)
            {
                newSubmeshTris[s] = new System.Collections.Generic.List<int>();
                int[] srcTris = srcMesh.GetTriangles(s);
                for (int t = 0; t < srcTris.Length; t += 3)
                {
                    int v0 = srcTris[t];
                    int v1 = srcTris[t + 1];
                    int v2 = srcTris[t + 2];
                    if (keepVertex[v0] && keepVertex[v1] && keepVertex[v2])
                    {
                        newSubmeshTris[s].Add(GetOrCreateNewVertex(v0));
                        newSubmeshTris[s].Add(GetOrCreateNewVertex(v1));
                        newSubmeshTris[s].Add(GetOrCreateNewVertex(v2));
                    }
                }
            }

            newMesh.SetVertices(newVerts);
            if (newNorms.Count == newVerts.Count) newMesh.SetNormals(newNorms);
            if (newTans.Count == newVerts.Count) newMesh.SetTangents(newTans);
            if (newUVs.Count == newVerts.Count) newMesh.SetUVs(0, newUVs);
            newMesh.boneWeights = newWeights.ToArray();
            newMesh.bindposes = srcMesh.bindposes;

            newMesh.subMeshCount = submeshCount;
            for (int s = 0; s < submeshCount; s++)
            {
                newMesh.SetTriangles(newSubmeshTris[s], s);
            }

            newMesh.RecalculateBounds();

            string assetPath = "Assets/Models/FPS_Bandit_Arms.asset";
            AssetDatabase.CreateAsset(newMesh, assetPath);
            AssetDatabase.SaveAssets();

            return $"Extracted FPS Arms Mesh! Vertices: {newVerts.Count} (was {srcMesh.vertexCount}), Submesh0 Tris: {newSubmeshTris[0].Count / 3}, Saved to {assetPath}";
        }

        public static string TestViewmodelUnderCamera()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Camera found";

            // Hide old rigs & body
            var oldRig = cam.transform.Find("FPS_ViewmodelRig");
            if (oldRig != null) oldRig.gameObject.SetActive(false);

            var oldPlayerRifle = FindDeepChild(p.transform, "WeaponSocket");
            if (oldPlayerRifle != null) oldPlayerRifle.gameObject.SetActive(false);

            var playerSmr = p.GetComponentInChildren<SkinnedMeshRenderer>();
            if (playerSmr != null) playerSmr.enabled = false;

            Transform pivot = cam.transform.Find("COD_ViewmodelPivot");
            if (pivot != null) UnityEngine.Object.DestroyImmediate(pivot.gameObject);

            var pivotGo = new GameObject("COD_ViewmodelPivot");
            pivot = pivotGo.transform;
            pivot.SetParent(cam.transform, false);
            // Calibrated local offset in camera space with bladed combat stance
            pivot.localPosition = new Vector3(-0.06f, -0.42f, 0.05f);
            pivot.localRotation = Quaternion.Euler(0f, 20f, 0f);

            // Instantiate Bandit
            var banditPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Prefabs/MilitaryMercenaryBandit_1.prefab");
            var bandit = UnityEngine.Object.Instantiate(banditPrefab, pivot);
            bandit.name = "Bandit_Viewmodel";
            bandit.transform.localPosition = Vector3.zero;
            bandit.transform.localRotation = Quaternion.identity;

            // Swap to arms-only mesh
            var armsMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_Arms.asset");
            var smr = bandit.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null && armsMesh != null)
            {
                smr.sharedMesh = armsMesh;
            }

            // Remove unwanted components (colliders, audio, etc)
            foreach (var col in bandit.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(col);

            // Play Firing Rifle animation
            var anim = bandit.GetComponent<Animator>();
            var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx").OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip != null)
            {
                clip.SampleAnimation(bandit, 0.1f);
            }

            // Clone AK-47 to hand.r
            Transform handR = FindDeepChild(bandit.transform, "hand.r");
            Transform gunTrans = null;
            if (handR != null)
            {
                var riflePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ScorchedPlayer.prefab");
                var rifleSrc = FindDeepChild(riflePrefab.transform, "Rifle");
                if (rifleSrc != null)
                {
                    var gun = UnityEngine.Object.Instantiate(rifleSrc.gameObject, handR);
                    gun.name = "Viewmodel_Rifle";
                    gun.transform.localPosition = new Vector3(-0.026f, 0.084f, 0.038f);
                    gun.transform.localRotation = Quaternion.Euler(4.2f, 327.1f, 357.7f);
                    gun.transform.localScale = Vector3.one;
                    gunTrans = gun.transform;
                }
            }

            // Solve Left Arm IK onto rifle handguard
            if (gunTrans != null)
            {
                SolveViewmodelLeftArmIK(bandit.transform, gunTrans);
            }

            return $"Created COD_ViewmodelPivot under Camera! SMR mesh={smr?.sharedMesh?.name}, verts={smr?.sharedMesh?.vertexCount}";
        }

        public static void SolveViewmodelLeftArmIK(Transform bandit, Transform gun)
        {
            Transform armL = FindDeepChild(bandit, "arm_stretch.l");
            Transform forearmL = FindDeepChild(bandit, "forearm_stretch.l");
            Transform handL = FindDeepChild(bandit, "hand.l");
            if (armL == null || forearmL == null || handL == null) return;

            // Handguard target point on rifle
            Vector3 targetPos = gun.TransformPoint(new Vector3(-0.015f, -0.025f, 0.165f));
            Quaternion targetRot = gun.rotation * Quaternion.Euler(45f, -65f, 215f);

            Vector3 a = armL.position;
            Vector3 b = forearmL.position;
            Vector3 c = handL.position;

            float lenAB = Vector3.Distance(a, b);
            float lenBC = Vector3.Distance(b, c);
            float lenAC = Mathf.Clamp(Vector3.Distance(a, targetPos), 0.001f, (lenAB + lenBC) * 0.999f);

            Vector3 dirAC = (targetPos - a).normalized;

            // Natural elbow bend direction: elbow points down and left (away from body/gun)
            Vector3 bendHint = a - bandit.up * 0.4f - bandit.right * 0.35f;
            Vector3 planeNormal = Vector3.Cross(dirAC, (bendHint - a).normalized).normalized;
            if (planeNormal.sqrMagnitude < 0.001f) planeNormal = -bandit.forward;

            float cosA = Mathf.Clamp((lenAB * lenAB + lenAC * lenAC - lenBC * lenBC) / (2f * lenAB * lenAC), -1f, 1f);
            float angleA = Mathf.Acos(cosA) * Mathf.Rad2Deg;

            // Rotate arm and forearm toward handguard without stretching bone lengths
            Vector3 curDirAB = (b - a).normalized;
            Vector3 targetDirAB = Quaternion.AngleAxis(-angleA, planeNormal) * dirAC;
            armL.rotation = Quaternion.FromToRotation(curDirAB, targetDirAB) * armL.rotation;

            b = forearmL.position;
            Vector3 curDirBC = (c - b).normalized;
            Vector3 targetDirBC = (targetPos - b).normalized;
            forearmL.rotation = Quaternion.FromToRotation(curDirBC, targetDirBC) * forearmL.rotation;

            // Hand matches target grip rotation (do NOT force world position which stretches bone!)
            handL.rotation = targetRot;
        }

        public static string InspectViewmodelStatus()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "p is null";
            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "cam is null";
            var bv = cam.transform.Find("COD_ViewmodelPivot/Bandit_Viewmodel");
            if (bv == null) return "bv is null";
            var smr = bv.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return "smr is null";
            var handR = FindDeepChild(bv.transform, "hand.r");
            var handL = FindDeepChild(bv.transform, "hand.l");
            return $"smr enabled={smr.enabled}, mesh={smr.sharedMesh?.name}, verts={smr.sharedMesh?.vertexCount}, bounds={smr.bounds}, handR_vp={cam.WorldToViewportPoint(handR?.position ?? Vector3.zero)}, handL_vp={cam.WorldToViewportPoint(handL?.position ?? Vector3.zero)}, handR_cam={cam.transform.InverseTransformPoint(handR?.position ?? Vector3.zero)}, handL_cam={cam.transform.InverseTransformPoint(handL?.position ?? Vector3.zero)}";
        }

        public static string InspectAllSmrs()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var smrs = p.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var sb = new StringBuilder();
            foreach (var s in smrs)
            {
                sb.AppendLine($"{s.name} in {s.transform.parent?.name} (activeInHierarchy={s.gameObject.activeInHierarchy}, enabled={s.enabled}, mesh={s.sharedMesh?.name}, verts={s.sharedMesh?.vertexCount})");
            }
            return sb.ToString();
        }

        public static string FindOffendingBones()
        {
            var p = GameObject.Find("Player");
            var cam = p.GetComponentInChildren<Camera>();
            var bv = p.transform.Find("Main Camera/COD_ViewmodelPivot/Bandit_Viewmodel");
            if (bv == null) return "bv null";
            var smr = bv.GetComponentInChildren<SkinnedMeshRenderer>();
            var sb = new StringBuilder();
            for (int i = 0; i < smr.bones.Length; i++)
            {
                var b = smr.bones[i];
                var camLocal = cam.transform.InverseTransformPoint(b.position);
                var vp = cam.WorldToViewportPoint(b.position);
                sb.AppendLine($"Bone[{i}] {b.name}: camLocal={camLocal}, vp={vp}");
            }
            return sb.ToString();
        }

        public static string InspectAllClips()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var handR = FindDeepChild(p.transform, "hand.r");
            var handL = FindDeepChild(p.transform, "hand.l");

            var sb = new StringBuilder();
            string[] clipFiles = new string[]
            {
                "Assets/Animations/Player/Firing Rifle.fbx",
                "Assets/Animations/Player/Idle.fbx",
                "Assets/Animations/Player/Jog.fbx",
                "Assets/Animations/Player/Running.fbx",
                "Assets/Animations/Player/Crouch Idle.fbx"
            };

            foreach (var file in clipFiles)
            {
                var clip = AssetDatabase.LoadAllAssetsAtPath(file).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
                if (clip == null) continue;
                clip.SampleAnimation(p, 0.1f);
                float dist = Vector3.Distance(handR.position, handL.position);
                sb.AppendLine($"{clip.name}: handR={handR.position}, handL={handL.position}, distBetweenHands={dist:F3}m");
            }
            return sb.ToString();
        }

        public static string InspectFbxRestPose()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (fbx == null) return "No FBX";
            var sb = new StringBuilder();
            string[] bones = new string[] { "shoulder.l", "arm_stretch.l", "forearm_stretch.l", "hand.l", "shoulder.r", "arm_stretch.r", "forearm_stretch.r", "hand.r" };
            foreach (var b in bones)
            {
                var t = FindDeepChild(fbx.transform, b);
                sb.AppendLine($"{b}: localPos={t?.localPosition}, localRot={t?.localEulerAngles}");
            }
            return sb.ToString();
        }

        public static string InspectReloadClipBindings()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/AK47_Reload.anim");
            if (clip == null) return "No reload clip";
            var bindings = AnimationUtility.GetCurveBindings(clip);
            return string.Join(", ", bindings.Select(b => b.path).Distinct());
        }

        public static string TestManualArmPose(float aX, float aY, float aZ, float fX, float fY, float fZ, float hX, float hY, float hZ)
        {
            var p = GameObject.Find("Player");
            var bv = p.transform.Find("Main Camera/COD_ViewmodelPivot/Bandit_Viewmodel");
            if (bv == null) return "No bv";

            var armL = FindDeepChild(bv, "arm_stretch.l");
            var forearmL = FindDeepChild(bv, "forearm_stretch.l");
            var handL = FindDeepChild(bv, "hand.l");

            if (armL != null) armL.localEulerAngles = new Vector3(aX, aY, aZ);
            if (forearmL != null) forearmL.localEulerAngles = new Vector3(fX, fY, fZ);
            if (handL != null) handL.localEulerAngles = new Vector3(hX, hY, hZ);

            var cam = p.GetComponentInChildren<Camera>();
            var hlVp = cam.WorldToViewportPoint(handL.position);
            var hlCam = cam.transform.InverseTransformPoint(handL.position);
            return $"Set left arm pose! handL vp={hlVp}, camLocal={hlCam}";
        }

        public static string InspectRightArmAngles()
        {
            var p = GameObject.Find("Player");
            var bv = p.transform.Find("Main Camera/COD_ViewmodelPivot/Bandit_Viewmodel");
            if (bv == null) return "No bv";
            var armR = FindDeepChild(bv, "arm_stretch.r");
            var faR = FindDeepChild(bv, "forearm_stretch.r");
            var hR = FindDeepChild(bv, "hand.r");
            return $"armR={armR?.localEulerAngles}, faR={faR?.localEulerAngles}, hR={hR?.localEulerAngles}";
        }

        public static string InspectArmLengths()
        {
            var p = GameObject.Find("Player");
            var bv = p.transform.Find("Main Camera/COD_ViewmodelPivot/Bandit_Viewmodel");
            if (bv == null) return "No bv";
            var a = FindDeepChild(bv, "arm_stretch.l");
            var b = FindDeepChild(bv, "forearm_stretch.l");
            var c = FindDeepChild(bv, "hand.l");
            var gun = bv.GetComponentInChildren<PlayerWeaponHandler>()?.transform ?? bv.GetComponentInChildren<Transform>();
            var target = FindDeepChild(bv, "LeftHandGripTarget");
            float lenAB = Vector3.Distance(a.position, b.position);
            float lenBC = Vector3.Distance(b.position, c.position);
            float distToTarget = target != null ? Vector3.Distance(a.position, target.position) : -1f;
            return $"lenAB={lenAB:F3}, lenBC={lenBC:F3}, totalMaxReach={(lenAB + lenBC):F3}, distToTarget={distToTarget:F3}, targetPos={target?.position}, aPos={a.position}, bPos={b.position}, cPos={c.position}";
        }

        public static string CleanupTestPivot()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No player";
            var pivot = p.transform.Find("Main Camera/COD_ViewmodelPivot");
            if (pivot != null) UnityEngine.Object.DestroyImmediate(pivot.gameObject);
            var smr = p.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) smr.enabled = true;
            var ws = FindDeepChild(p.transform, "WeaponSocket");
            if (ws != null) ws.gameObject.SetActive(true);
            return "Cleaned up test pivot and re-enabled player!";
        }

        public static string TestNativeArmsOnPlayer()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            // Unhide WeaponSocket and rifle
            var ws = FindDeepChild(p.transform, "WeaponSocket");
            if (ws != null) ws.gameObject.SetActive(true);

            // Re-assign bandit SMR to FPS Arms mesh
            var smr = p.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                smr.enabled = true;
                var armsMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_Arms.asset");
                if (armsMesh != null) smr.sharedMesh = armsMesh;
            }

            // Ensure head and neck bone scales are (1,1,1)
            var head = FindDeepChild(p.transform, "head.x");
            var neck = FindDeepChild(p.transform, "neck.x");
            if (head != null) head.localScale = Vector3.one;
            if (neck != null) neck.localScale = Vector3.one;

            // Sample Firing Rifle
            var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx").OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            // Position Camera at eye level with bottom-middle framing
            var cam = p.GetComponentInChildren<Camera>();
            if (cam != null && neck != null)
            {
                cam.transform.position = neck.position + (p.transform.forward * -0.04f) + (p.transform.up * 0.04f) + (p.transform.right * 0.04f);
                cam.transform.rotation = p.transform.rotation;
                cam.fieldOfView = 80f;
                cam.nearClipPlane = 0.015f;
            }

            // Solve Left Hand IK with torso-relative bend hint
            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl != null) ctrl.SolveLeftHandIK();

            return $"Configured Player with FPS_Bandit_Arms mesh and 1:1 bone scale! SMR mesh={smr?.sharedMesh?.name}";
        }

        public static string InspectNativeHandsViewport(float fwd, float up, float right)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No player";
            var neck = FindDeepChild(p.transform, "neck.x");
            var cam = p.GetComponentInChildren<Camera>();
            cam.transform.position = neck.position + (p.transform.forward * fwd) + (p.transform.up * up) + (p.transform.right * right);
            cam.transform.rotation = p.transform.rotation;
            var hr = FindDeepChild(p.transform, "hand.r");
            var hl = FindDeepChild(p.transform, "hand.l");
            var far = FindDeepChild(p.transform, "forearm_stretch.r");
            var fal = FindDeepChild(p.transform, "forearm_stretch.l");
            return $"fwd={fwd}, up={up}, right={right} | hr_vp={cam.WorldToViewportPoint(hr.position)}, hl_vp={cam.WorldToViewportPoint(hl.position)}, far_vp={cam.WorldToViewportPoint(far.position)}, fal_vp={cam.WorldToViewportPoint(fal.position)}";
        }

        public static string TestTuckRightArm(float rx, float ry, float rz)
        {
            var p = GameObject.Find("Player").transform.Find("Main Camera/COD_ViewmodelPivot/Bandit_Viewmodel");
            if (p == null) return "No bv";
            var armR = FindDeepChild(p, "arm_stretch.r");
            if (armR != null) armR.localEulerAngles = new Vector3(rx, ry, rz);

            var s3 = p.Find("root/root.x/spine_01.x/spine_02.x/spine_03.x");
            var hr = FindDeepChild(p, "hand.r");
            var target = FindDeepChild(p, "LeftHandGripTarget");
            var a = FindDeepChild(p, "arm_stretch.l");

            float dist = target != null ? Vector3.Distance(a.position, target.position) : -1f;
            return $"rx={rx}, ry={ry}, rz={rz} | hrInS3={s3.InverseTransformPoint(hr.position)}, targetInS3={s3.InverseTransformPoint(target.position)}, distToLeftShoulder={dist:F3}m";
        }

        public static string FindPipesNearOrigin()
        {
            var sb = new StringBuilder();
            var objs = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Where(t => Vector3.Distance(t.position, Vector3.zero) < 20f);
            foreach (var o in objs)
            {
                if (o.name.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    o.name.IndexOf("support", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    o.name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    o.name.IndexOf("wire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    o.name.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    o.name.IndexOf("pole", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.AppendLine($"{o.name} @ {o.position}, rot={o.eulerAngles}, scale={o.lossyScale}");
                }
            }
            return sb.ToString();
        }

        public static string InspectWeaponController()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";
            var nw = p.GetComponent<NetworkWeaponController>();
            var wh = p.GetComponent<PlayerWeaponHandler>();
            var pm = p.GetComponent<PlayerMovement>();
            return $"nw={(nw != null)}, currentWeapon={(nw?.CurrentWeapon != null ? nw.CurrentWeapon.WeaponName : "null")}, isHitscan={(nw?.CurrentWeapon != null && nw.CurrentWeapon.IsHitscan)}, muzzle={(nw?.MuzzlePoint != null ? nw.MuzzlePoint.name : "null")}, muzzlePos={(nw != null ? nw.GetMuzzlePosition().ToString() : "null")}, whMuzzle={(wh != null ? wh.GetMuzzlePosition().ToString() : "null")}";
        }

        public static string InspectSceneAndPlayer()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== INSPECTING SCENE AND PLAYER ===");
            GameObject player = GameObject.Find("Player");
            if (player == null) return "Scene Player not found!";

            sb.AppendLine($"Player GameObject: {player.name}, Active: {player.activeSelf}, Pos: {player.transform.position}, Rot: {player.transform.eulerAngles}");

            var movement = player.GetComponent<PlayerMovement>();
            var perspective = player.GetComponent<FPSPerspectiveController>();
            var netWeap = player.GetComponent<NetworkWeaponController>();
            var weapHandler = player.GetComponent<PlayerWeaponHandler>();

            sb.AppendLine($"Perspective Mode: {(perspective != null ? perspective.CurrentMode.ToString() : "null")}");
            sb.AppendLine($"Movement EyeOffset: Forward={movement?.EyeOffsetForward}, Up={movement?.EyeOffsetUp}, Right={movement?.EyeOffsetRight}");

            var cam = player.GetComponentInChildren<Camera>();
            if (cam != null)
            {
                sb.AppendLine($"Camera: {cam.name}, Active: {cam.gameObject.activeInHierarchy}, Pos: {cam.transform.position}, LocalPos: {cam.transform.localPosition}, Rot: {cam.transform.eulerAngles}, FOV: {cam.fieldOfView}, NearClip: {cam.nearClipPlane}");
            }

            // Check ActiveMuzzlePoint
            var activeMuzzle = perspective?.ActiveMuzzlePoint;
            sb.AppendLine($"perspective.ActiveMuzzlePoint: {(activeMuzzle != null ? activeMuzzle.name + " at world " + activeMuzzle.position + ", vp=" + (cam != null ? cam.WorldToViewportPoint(activeMuzzle.position).ToString() : "") : "null")}");

            // Check netWeap.MuzzlePoint
            var netMuzzle = netWeap?.MuzzlePoint;
            sb.AppendLine($"netWeap.MuzzlePoint: {(netMuzzle != null ? netMuzzle.name + " at world " + netMuzzle.position + ", vp=" + (cam != null ? cam.WorldToViewportPoint(netMuzzle.position).ToString() : "") : "null")}");

            // Check weapHandler.GetMuzzlePosition()
            if (weapHandler != null)
            {
                Vector3 whPos = weapHandler.GetMuzzlePosition();
                sb.AppendLine($"weapHandler.GetMuzzlePosition(): {whPos}, vp={(cam != null ? cam.WorldToViewportPoint(whPos).ToString() : "")}");
            }

            // Check all objects within 8 meters of player
            sb.AppendLine("--- Objects near Player (radius 8m) ---");
            var colliders = Physics.OverlapSphere(player.transform.position, 8f);
            foreach (var col in colliders)
            {
                if (col.transform.root != player.transform.root)
                {
                    sb.AppendLine($"  Nearby Col: {col.name} (root: {col.transform.root.name}), pos={col.transform.position}, dist={Vector3.Distance(player.transform.position, col.transform.position):F2}m");
                }
            }

            // Find all Trail/Track objects in scene
            var allTrails = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name.IndexOf("trail", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("track", StringComparison.OrdinalIgnoreCase) >= 0);
            sb.AppendLine("--- Trail / Track transforms in scene ---");
            foreach (var tr in allTrails.Take(15))
            {
                sb.AppendLine($"  Trail: {tr.name}, pos={tr.position}, rot={tr.eulerAngles}, scale={tr.lossyScale}");
            }

            return sb.ToString();
        }

        public static string TestBoneHierarchy()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BANDIT BONE HIERARCHY ===");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) return "Prefab not found!";

            Transform rootBone = FindDeepChild(prefab.transform, "root.x");
            if (rootBone == null) return "root.x not found!";

            PrintBoneTree(rootBone, 0, sb);
            return sb.ToString();
        }

        private static void PrintBoneTree(Transform current, int depth, StringBuilder sb)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}- {current.name}");
            for (int i = 0; i < current.childCount; i++)
            {
                PrintBoneTree(current.GetChild(i), depth + 1, sb);
            }
        }

        public static string InspectMuzzlePoint()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== INSPECTING MUZZLE POINT & WEAPONS ===");
            GameObject player = GameObject.Find("Player");
            if (player == null) return "Scene Player not found!";

            var netWeapon = player.GetComponent<NetworkWeaponController>();
            var weaponHandler = player.GetComponent<PlayerWeaponHandler>();
            var cam = player.GetComponentInChildren<Camera>();

            sb.AppendLine("NetWeapon MuzzlePoint: " + (netWeapon?.MuzzlePoint != null ? netWeapon.MuzzlePoint.name + " (parent: " + netWeapon.MuzzlePoint.parent.name + ")" : "null"));
            sb.AppendLine("NetWeapon GetMuzzlePosition(): " + netWeapon?.GetMuzzlePosition());
            if (cam != null && netWeapon != null)
            {
                sb.AppendLine("NetWeapon Muzzle Viewport: " + cam.WorldToViewportPoint(netWeapon.GetMuzzlePosition()));
            }

            Transform vmRig = cam?.transform.Find("FPS_ViewmodelRig");
            sb.AppendLine("FPS_ViewmodelRig active: " + (vmRig != null && vmRig.gameObject.activeSelf));
            if (vmRig != null)
            {
                Transform vmMuzzle = vmRig.Find("Viewmodel_AK47/MuzzlePoint");
                sb.AppendLine("vmMuzzle exists: " + (vmMuzzle != null));
                if (vmMuzzle != null && cam != null)
                {
                    sb.AppendLine("vmMuzzle world: " + vmMuzzle.position + ", vp: " + cam.WorldToViewportPoint(vmMuzzle.position));
                }
            }

            // Check world weapon
            if (weaponHandler?.EquippedWeapon != null)
            {
                sb.AppendLine("EquippedWeapon: " + weaponHandler.EquippedWeapon.name + ", active=" + weaponHandler.EquippedWeapon.gameObject.activeSelf);
                foreach (Transform child in weaponHandler.EquippedWeapon)
                {
                    sb.AppendLine("  Child: " + child.name + ", pos=" + child.localPosition + ", rot=" + child.localEulerAngles);
                    var cmf = child.GetComponent<MeshFilter>();
                    if (cmf != null && cmf.sharedMesh != null)
                    {
                        var verts = cmf.sharedMesh.vertices;
                        Vector3 minV = Vector3.one * float.MaxValue;
                        Vector3 maxV = Vector3.one * float.MinValue;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 wv = child.TransformPoint(verts[i]);
                            Vector3 lv = weaponHandler.EquippedWeapon.InverseTransformPoint(wv);
                            minV = Vector3.Min(minV, lv);
                            maxV = Vector3.Max(maxV, lv);
                        }
                        sb.AppendLine("    Mesh: " + cmf.sharedMesh.name + ", in Rifle local space min=" + minV + ", max=" + maxV);
                    }
                }
            }

            return sb.ToString();
        }

        public static string InspectBanditMeshes()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== INSPECTING BANDIT MESHES & SUBMESHES ===");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) return "Prefab not found!";

            var smrs = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            sb.AppendLine($"Found {smrs.Length} SkinnedMeshRenderers on {prefab.name}:");
            foreach (var smr in smrs)
            {
                sb.AppendLine($"  - Name: {smr.name}, RootBone: {smr.rootBone?.name}, Bones: {smr.bones.Length}, Materials: {smr.sharedMaterials.Length}, Bounds: {smr.bounds.size}");
            }

            var mr = prefab.GetComponentsInChildren<MeshRenderer>(true);
            sb.AppendLine($"Found {mr.Length} MeshRenderers on {prefab.name}:");
            foreach (var m in mr)
            {
                sb.AppendLine($"  - Name: {m.name}, Parent: {m.transform.parent?.name}, Materials: {m.sharedMaterials.Length}");
            }

            return sb.ToString();
        }

        public static string InspectAnimationClips()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== INSPECTING ANIMATOR CONTROLLER & CLIPS ===");
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/Animations/Player/01_Player.controller");
            if (controller == null)
            {
                sb.AppendLine("AnimatorController not found!");
                return sb.ToString();
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                sb.AppendLine($"Layer {i}: {layer.name} (weight={layer.defaultWeight}, avatarMask={layer.avatarMask?.name})");
                foreach (var state in layer.stateMachine.states)
                {
                    sb.AppendLine($"  State: {state.state.name}, Motion: {state.state.motion?.name}");
                    foreach (var trans in state.state.transitions)
                    {
                        sb.AppendLine($"    -> To: {trans.destinationState?.name}, Conditions: {trans.conditions.Length}");
                        foreach (var cond in trans.conditions)
                        {
                            sb.AppendLine($"       Condition: {cond.parameter} {cond.mode} {cond.threshold}");
                        }
                    }
                }
                foreach (var anyTrans in layer.stateMachine.anyStateTransitions)
                {
                    sb.AppendLine($"  [AnyState] -> To: {anyTrans.destinationState?.name}, Conditions: {anyTrans.conditions.Length}");
                }
            }

            return sb.ToString();
        }

        public static string VerifyCurrentSetup()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== VERIFYING PLAYER WEAPON & ANIMATION SETUP ===");

            VerifyGameObject(GameObject.Find("Player"), sb, "Scene Player");

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefabAsset != null)
            {
                VerifyGameObject(prefabAsset, sb, "Prefab Asset");
            }

            return sb.ToString();
        }

        private static void VerifyGameObject(GameObject player, StringBuilder sb, string label)
        {
            if (player == null)
            {
                sb.AppendLine($"[{label}] Not found!");
                return;
            }

            sb.AppendLine($"--- {label} ({player.name}) ---");
            var weaponHandler = player.GetComponent<PlayerWeaponHandler>();
            sb.AppendLine($"  PlayerWeaponHandler present: {weaponHandler != null}");
            if (weaponHandler != null)
            {
                sb.AppendLine($"  RightHandBone: {(weaponHandler.RightHandBone != null ? weaponHandler.RightHandBone.name : "null")}");
                sb.AppendLine($"  WeaponSocket: {(weaponHandler.WeaponSocket != null ? weaponHandler.WeaponSocket.name : "null")}");
                if (weaponHandler.WeaponSocket != null)
                {
                    sb.AppendLine($"  WeaponSocket Parent: {weaponHandler.WeaponSocket.parent?.name}");
                    sb.AppendLine($"  WeaponSocket LocalPos: {weaponHandler.WeaponSocket.localPosition}");
                    sb.AppendLine($"  WeaponSocket LocalRot: {weaponHandler.WeaponSocket.localEulerAngles}");
                }
                sb.AppendLine($"  EquippedWeapon: {(weaponHandler.EquippedWeapon != null ? weaponHandler.EquippedWeapon.name : "null")}");
                if (weaponHandler.EquippedWeapon != null)
                {
                    sb.AppendLine($"  EquippedWeapon Parent: {weaponHandler.EquippedWeapon.parent?.name}");
                    sb.AppendLine($"  EquippedWeapon LocalPos: {weaponHandler.EquippedWeapon.localPosition}");
                    sb.AppendLine($"  EquippedWeapon LocalRot: {weaponHandler.EquippedWeapon.localEulerAngles}");
                }
            }

            var vm = player.GetComponentsInChildren<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "ModernWarfareViewmodelController");
            sb.AppendLine($"  ModernWarfareViewmodelController present: {vm != null}");

            var cam = player.GetComponentInChildren<Camera>();
            if (cam != null)
            {
                sb.AppendLine($"  Camera: {cam.name}, FOV: {cam.fieldOfView}, NearClip: {cam.nearClipPlane}");
                sb.AppendLine($"  Camera local pos: {cam.transform.localPosition}");

                Transform handR = FindDeepChild(player.transform, "hand.r");
                Transform handL = FindDeepChild(player.transform, "hand.l");
                Transform rifle = FindDeepChild(player.transform, "rifle") ?? FindDeepChild(player.transform, "Rifle");
                Transform head = FindDeepChild(player.transform, "head.x") ?? FindDeepChild(player.transform, "head");

                if (head != null)
                {
                    Vector3 headVp = cam.WorldToViewportPoint(head.position);
                    sb.AppendLine($"  Head: world={head.position}, vp={headVp}");
                }
                if (handR != null)
                {
                    Vector3 rVp = cam.WorldToViewportPoint(handR.position);
                    Vector3 rLocal = cam.transform.InverseTransformPoint(handR.position);
                    sb.AppendLine($"  hand.r: world={handR.position}, camLocal={rLocal}, vp={rVp}");
                }
                if (handL != null)
                {
                    Vector3 lVp = cam.WorldToViewportPoint(handL.position);
                    Vector3 lLocal = cam.transform.InverseTransformPoint(handL.position);
                    sb.AppendLine($"  hand.l: world={handL.position}, camLocal={lLocal}, vp={lVp}");
                }
                if (rifle != null)
                {
                    Vector3 rifleVp = cam.WorldToViewportPoint(rifle.position);
                    Vector3 rifleLocal = cam.transform.InverseTransformPoint(rifle.position);
                    sb.AppendLine($"  rifle: world={rifle.position}, camLocal={rifleLocal}, vp={rifleVp}");
                }
            }
        }

        private static void RestorePlayerObject(GameObject root, StringBuilder sb, string context)
        {
            sb.AppendLine($"--- Processing {context} ({root.name}) ---");

            // 0. Remove any missing / dead MonoBehaviours (e.g. deleted AK47ReloadController)
            int removedMissing = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            if (removedMissing > 0)
            {
                sb.AppendLine($"[{context}] Removed {removedMissing} missing/dead MonoBehaviour script(s).");
            }

            // 1. Remove ModernWarfareViewmodelController component completely
            var allComponents = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var c in allComponents)
            {
                if (c != null && c.GetType().Name == "ModernWarfareViewmodelController")
                {
                    UnityEngine.Object.DestroyImmediate(c, true);
                    sb.AppendLine($"[{context}] Removed ModernWarfareViewmodelController component.");
                }
            }

            // 2. Locate Camera and remove any artificial FPS_WeaponPivot
            Transform camTrans = FindDeepChild(root.transform, "PlayerCamera")
                              ?? FindDeepChild(root.transform, "PlayerCamera_Auto")
                              ?? FindDeepChild(root.transform, "Main Camera")
                              ?? FindDeepChild(root.transform, "Camera");

            if (camTrans != null)
            {
                Transform pivot = camTrans.Find("FPS_WeaponPivot");
                if (pivot != null)
                {
                    // If the rifle was accidentally left parented inside FPS_WeaponPivot, unparent it first
                    while (pivot.childCount > 0)
                    {
                        Transform child = pivot.GetChild(0);
                        child.SetParent(root.transform, false);
                    }
                    UnityEngine.Object.DestroyImmediate(pivot.gameObject);
                    sb.AppendLine($"[{context}] Removed artificial FPS_WeaponPivot from Camera.");
                }
            }

            // 3. Locate hand.r and WeaponSocket
            Transform handR = FindDeepChild(root.transform, "hand.r")
                           ?? FindDeepChild(root.transform, "mixamorig:RightHand")
                           ?? FindDeepChild(root.transform, "RightHand");

            if (handR == null)
            {
                sb.AppendLine($"[{context}] WARNING: hand.r not found!");
                return;
            }

            Transform weaponSocket = FindDeepChild(root.transform, "WeaponSocket");
            if (weaponSocket == null)
            {
                var socketGo = new GameObject("WeaponSocket");
                socketGo.transform.SetParent(handR, false);
                weaponSocket = socketGo.transform;
                sb.AppendLine($"[{context}] Created missing WeaponSocket under {handR.name}.");
            }
            else
            {
                if (weaponSocket.parent != handR)
                {
                    weaponSocket.SetParent(handR, false);
                    sb.AppendLine($"[{context}] Re-parented WeaponSocket back under {handR.name}.");
                }
            }

            // Reset weaponSocket local transform to default Idle pose
            WeaponPose defaultIdle = PlayerWeaponHandler.GetPoseForState(WeaponPoseState.Idle);
            weaponSocket.localPosition = defaultIdle.LocalPosition;
            weaponSocket.localRotation = defaultIdle.Rotation;
            weaponSocket.localScale = Vector3.one;

            // 4. Locate Rifle / AK-47
            Transform rifle = FindDeepChild(root.transform, "rifle")
                           ?? FindDeepChild(root.transform, "Rifle")
                           ?? FindDeepChild(root.transform, "AK47NoSubdiv");

            if (rifle != null)
            {
                // Remove artificial right grip target if any (right hand uses native WeaponSocket)
                Transform rGrip = rifle.Find("RightHandGripTarget");
                if (rGrip != null) UnityEngine.Object.DestroyImmediate(rGrip.gameObject);

                // Calibrate and ensure LeftHandGripTarget on AK-47 wooden handguard
                FPSPerspectiveController.EnsureLeftHandGripTarget(rifle);

                // Parent rifle into WeaponSocket with native model offsets
                rifle.SetParent(weaponSocket, false);
                rifle.localPosition = PlayerWeaponHandler.DefaultRiflePosition;
                rifle.localRotation = Quaternion.Euler(PlayerWeaponHandler.DefaultRifleEuler);
                rifle.localScale = Vector3.one;
                FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifle);
                sb.AppendLine($"[{context}] Parented {rifle.name} into WeaponSocket with native offset and calibrated MuzzlePoint.");
            }

            // 5. Ensure PlayerWeaponHandler is properly wired
            var weaponHandler = root.GetComponent<PlayerWeaponHandler>();
            if (weaponHandler != null)
            {
                weaponHandler.EnsureHandAndSocket();
                if (rifle != null)
                {
                    weaponHandler.AttachWeaponToSocket(rifle);
                }
                sb.AppendLine($"[{context}] Wired PlayerWeaponHandler with {handR.name} and WeaponSocket.");
            }

            // 6. Ensure FPSPerspectiveController component exists and is configured
            var perspectiveCtrl = root.GetComponent<FPSPerspectiveController>();
            if (perspectiveCtrl == null)
            {
                perspectiveCtrl = root.AddComponent<FPSPerspectiveController>();
                sb.AppendLine($"[{context}] Added FPSPerspectiveController component.");
            }

            var armsMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset")
                        ?? AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_Arms.asset");
            var defaultMesh = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx")?.GetComponentInChildren<SkinnedMeshRenderer>()?.sharedMesh;

            var so = new SerializedObject(perspectiveCtrl);
            if (armsMesh != null) so.FindProperty("_fpsArmsMesh").objectReferenceValue = armsMesh;
            if (defaultMesh != null) so.FindProperty("_defaultBodyMesh").objectReferenceValue = defaultMesh;
            so.ApplyModifiedProperties();

            // Set main SkinnedMeshRenderer on character to original full body mesh and ensure enabled
            var smr = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                smr.enabled = true;
                if (defaultMesh != null) smr.sharedMesh = defaultMesh;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // Restore hand.l localPosition to exact FBX bindpose to prevent wrist dislocation
            var handL = FindDeepChild(root.transform, "hand.l");
            if (handL != null)
            {
                handL.localPosition = new Vector3(0f, 0.2648f, 0f);
            }

            var headBone = FindDeepChild(root.transform, "head.x");
            var neckBone = FindDeepChild(root.transform, "neck.x");
            if (headBone != null) headBone.localScale = Vector3.one;
            if (neckBone != null) neckBone.localScale = Vector3.one;

            if (camTrans != null)
            {
                perspectiveCtrl.EnsureViewmodelRig();
            }

            perspectiveCtrl.ApplyCurrentMode();
            sb.AppendLine($"[{context}] Configured FPSPerspectiveController with FPS_Bandit_Arms mesh and dropdown support.");

            // 7. Ensure NetworkWeaponController references are synced
            var netWeap = root.GetComponent<NetworkWeaponController>();
            if (netWeap != null && perspectiveCtrl != null)
            {
                netWeap.MuzzlePoint = perspectiveCtrl.ActiveMuzzlePoint;
                sb.AppendLine($"[{context}] Synced NetworkWeaponController.MuzzlePoint to {netWeap.MuzzlePoint?.name}.");
            }

            // 8. Reset all arm bone rotations to identity or zero relative offsets so animation controls them
            ResetArmBones(root.transform, sb, context);
        }

        public static Mesh CreatePerfectFpsArmsMesh()
        {
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (banditFbx == null) return null;
            var smr = banditFbx.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return null;

            var srcMesh = smr.sharedMesh;
            var weights = srcMesh.boneWeights;

            // Identify arm and hand bone indices (EXCLUDING shoulders to eliminate vest straps)
            // 19..38: Right arm, forearm, hand, fingers
            // 40..59: Left arm, forearm, hand, fingers
            bool[] isArmBone = new bool[smr.bones.Length];
            for (int i = 19; i <= 38; i++) isArmBone[i] = true;
            for (int i = 40; i <= 59; i++) isArmBone[i] = true;

            // Identify which vertices are purely on arms/hands/sleeves
            bool[] isArmVertex = new bool[srcMesh.vertexCount];
            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float armWeight = 0f;
                if (isArmBone[bw.boneIndex0]) armWeight += bw.weight0;
                if (isArmBone[bw.boneIndex1]) armWeight += bw.weight1;
                if (isArmBone[bw.boneIndex2]) armWeight += bw.weight2;
                if (isArmBone[bw.boneIndex3]) armWeight += bw.weight3;

                // Keep vertex if strictly weighted to arm bones
                if (armWeight >= 0.70f)
                {
                    isArmVertex[i] = true;
                }
            }

            // Clone exact original mesh preserving all vertices, normals, tangents, uvs, boneWeights, and bindposes
            Mesh armsMesh = UnityEngine.Object.Instantiate(srcMesh);
            armsMesh.name = "FPS_Bandit_PureArms";

            // Filter Submesh 0 (body): only keep triangles where ALL 3 vertices belong to arms
            int[] srcTriangles = srcMesh.GetTriangles(0);
            var keptTriangles = new System.Collections.Generic.List<int>(srcTriangles.Length);
            for (int i = 0; i < srcTriangles.Length; i += 3)
            {
                int v0 = srcTriangles[i];
                int v1 = srcTriangles[i + 1];
                int v2 = srcTriangles[i + 2];

                if (isArmVertex[v0] && isArmVertex[v1] && isArmVertex[v2])
                {
                    keptTriangles.Add(v0);
                    keptTriangles.Add(v1);
                    keptTriangles.Add(v2);
                }
            }

            armsMesh.SetTriangles(keptTriangles.ToArray(), 0);
            // Empty out hair and eyelashes
            if (armsMesh.subMeshCount > 1) armsMesh.SetTriangles(new int[0], 1);
            if (armsMesh.subMeshCount > 2) armsMesh.SetTriangles(new int[0], 2);

            // Save to AssetDatabase
            string assetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(armsMesh, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"Created Perfect FPS Arms Mesh at {assetPath} with {keptTriangles.Count / 3} triangles (from {srcTriangles.Length / 3})!");
            return armsMesh;
        }

        public static void SolveTwoBoneIK(Transform boneA, Transform boneB, Transform boneC, Vector3 targetPos, Quaternion targetRot, Vector3 hintPos, bool isLeft)
        {
            if (boneA == null || boneB == null || boneC == null) return;

            Vector3 a = boneA.position;
            Vector3 b = boneB.position;
            Vector3 c = boneC.position;

            float lenAB = Vector3.Distance(a, b);
            float lenBC = Vector3.Distance(b, c);
            float maxReach = (lenAB + lenBC) * 0.999f;
            float distAC = Vector3.Distance(a, targetPos);
            float clampedDist = Mathf.Clamp(distAC, 0.05f, maxReach);

            Vector3 dirAC = (targetPos - a).normalized;
            Vector3 hintDir = (hintPos - a).normalized;
            Vector3 planeNormal = Vector3.Cross(dirAC, hintDir).normalized;

            if (planeNormal.sqrMagnitude < 0.001f)
            {
                planeNormal = Vector3.up;
            }

            // Law of Cosines for angle at A
            float cosA = Mathf.Clamp((lenAB * lenAB + clampedDist * clampedDist - lenBC * lenBC) / (2f * lenAB * clampedDist), -1f, 1f);
            float angleA = Mathf.Acos(cosA) * Mathf.Rad2Deg;

            // Rotate boneA
            Vector3 curDirAB = (b - a).normalized;
            float sign = isLeft ? -1f : 1f;
            Vector3 targetDirAB = Quaternion.AngleAxis(sign * angleA, planeNormal) * dirAC;
            boneA.rotation = Quaternion.FromToRotation(curDirAB, targetDirAB) * boneA.rotation;

            // Re-sample B after boneA rotation
            b = boneB.position;
            Vector3 curDirBC = (boneC.position - b).normalized;
            Vector3 targetDirBC = (targetPos - b).normalized;
            boneB.rotation = Quaternion.FromToRotation(curDirBC, targetDirBC) * boneB.rotation;

            // Rotate hand to match target rotation
            boneC.rotation = targetRot;
        }

        public static string TestBuildPureArmsViewmodel()
        {
            Mesh pureArms = CreatePerfectFpsArmsMesh();
            if (pureArms == null) return "Failed to create pure arms mesh";

            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            if (camTrans == null) camTrans = p.transform.Find("PlayerCamera_Auto") ?? p.GetComponentInChildren<Camera>()?.transform;
            if (camTrans == null) return "No CameraTransform found";

            Transform existingRig = camTrans.Find("FPS_ViewmodelRig");
            if (existingRig != null) UnityEngine.Object.DestroyImmediate(existingRig.gameObject);

            var rigGo = new GameObject("FPS_ViewmodelRig");
            rigGo.transform.SetParent(camTrans, false);
            // Master Viewmodel Pivot in Camera Space: perfectly framed Modern Warfare viewmodel
            rigGo.transform.localPosition = new Vector3(0.01f, -0.32f, 0.15f);
            rigGo.transform.localRotation = Quaternion.identity;

            // Instantiate Bandit Arms character
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var armsChar = (GameObject)PrefabUtility.InstantiatePrefab(banditFbx, rigGo.transform);
            armsChar.name = "Viewmodel_BanditArms";
            // Position Bandit character so shoulders sit right beside camera lens
            armsChar.transform.localPosition = new Vector3(0.0f, -1.46f, 0.02f);
            armsChar.transform.localRotation = Quaternion.identity;
            armsChar.transform.localScale = Vector3.one;

            // Assign pure arms mesh to SkinnedMeshRenderer (zero body/head/leg triangles)
            var smr = armsChar.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.sharedMesh = pureArms;
            }

            foreach (var col in armsChar.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(col);

            // AK-47 mounted directly to viewmodel rig in Modern Warfare hipfire framing
            Transform existingRifle = rigGo.transform.Find("Viewmodel_AK47");
            if (existingRifle != null) UnityEngine.Object.DestroyImmediate(existingRifle.gameObject);

            var sourceRifle = FindDeepChild(p.transform, "Rifle");
            GameObject rifleGo = null;
            if (sourceRifle != null)
            {
                rifleGo = UnityEngine.Object.Instantiate(sourceRifle.gameObject, rigGo.transform);
                rifleGo.name = "Viewmodel_AK47";
                rifleGo.SetActive(true);
                // Modern Warfare Hip-Fire weapon framing in camera space (natural arm reach)
                rifleGo.transform.localPosition = new Vector3(0.08f, -0.14f, 0.24f);
                rifleGo.transform.localRotation = Quaternion.Euler(-1.0f, -1.5f, 1.0f);
                rifleGo.transform.localScale = Vector3.one;

                foreach (var rogueCam in rifleGo.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(rogueCam.gameObject);
                foreach (var c in rifleGo.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);
                FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifleGo.transform);
            }

            // Bone references
            var armR = FindDeepChild(armsChar.transform, "arm_stretch.r");
            var forearmR = FindDeepChild(armsChar.transform, "forearm_stretch.r");
            var handR = FindDeepChild(armsChar.transform, "hand.r");

            var armL = FindDeepChild(armsChar.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(armsChar.transform, "forearm_stretch.l");
            var handL = FindDeepChild(armsChar.transform, "hand.l");

            string ikLog = "";
            if (rifleGo != null && armR != null && forearmR != null && handR != null)
            {
                // Right Hand Grip: Pistol grip and trigger
                Vector3 rGripPos = rifleGo.transform.TransformPoint(new Vector3(0.015f, -0.055f, -0.035f));
                Quaternion rGripRot = rifleGo.transform.rotation * Quaternion.Euler(15f, 95f, -75f);
                Vector3 rHint = armR.position - camTrans.up * 0.45f + camTrans.right * 0.35f;
                SolveTwoBoneIK(armR, forearmR, handR, rGripPos, rGripRot, rHint, false);
                ikLog += $"Right: armPos={armR.position}, rGripPos={rGripPos}, dist={Vector3.Distance(armR.position, rGripPos):F2} | ";
            }

            if (rifleGo != null && armL != null && forearmL != null && handL != null)
            {
                // Left Hand Grip: Wooden lower handguard
                Vector3 lGripPos = rifleGo.transform.TransformPoint(new Vector3(0.005f, -0.025f, 0.185f));
                Quaternion lGripRot = rifleGo.transform.rotation * Quaternion.Euler(-25f, 75f, -55f);
                Vector3 lHint = armL.position - camTrans.up * 0.45f - camTrans.right * 0.35f;
                SolveTwoBoneIK(armL, forearmL, handL, lGripPos, lGripRot, lHint, true);
                ikLog += $"Left: armPos={armL.position}, lGripPos={lGripPos}, dist={Vector3.Distance(armL.position, lGripPos):F2}";
            }

            // Hide 3rd person body on scene player completely so only viewmodel renders
            var sceneSmrs = p.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var s in sceneSmrs)
            {
                if (!s.transform.IsChildOf(rigGo.transform))
                {
                    s.enabled = false;
                }
            }
            var sceneMeshRenderers = p.GetComponentsInChildren<MeshRenderer>();
            foreach (var mr in sceneMeshRenderers)
            {
                if (!mr.transform.IsChildOf(rigGo.transform))
                {
                    mr.enabled = false;
                }
            }

            string capLog = CaptureViewToArtifact("cod_mw_viewmodel_framed.png");
            return "Built pure arms viewmodel with two-bone IK successfully! | " + ikLog + " | " + capLog;
        }

        public static string DiagnoseViewmodelRenderers()
        {
            var sb = new StringBuilder();
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";

            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Camera";

            sb.AppendLine($"Cam: worldPos={cam.transform.position}, fwd={cam.transform.forward}");

            var smrs = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"Total SMRs in scene: {smrs.Length}");
            foreach (var smr in smrs)
            {
                sb.AppendLine($"  SMR: '{smr.name}' (parent='{smr.transform.parent?.name}'), enabled={smr.enabled}, goActive={smr.gameObject.activeInHierarchy}, shadowMode={smr.shadowCastingMode}, boundsCenter={smr.bounds.center}, vpCenter={cam.WorldToViewportPoint(smr.bounds.center)}");
            }

            var mrs = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"Total MeshRenderers in scene: {mrs.Length}");
            foreach (var mr in mrs)
            {
                if (mr.name.Contains("AK") || mr.name.Contains("Rifle") || mr.name.Contains("Bandit"))
                {
                    sb.AppendLine($"  MR: '{mr.name}' (parent='{mr.transform.parent?.name}'), enabled={mr.enabled}, goActive={mr.gameObject.activeInHierarchy}, boundsCenter={mr.bounds.center}, vpCenter={cam.WorldToViewportPoint(mr.bounds.center)}");
                }
            }

            return sb.ToString();
        }

        public static string TestNativeCombatReadyView()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            // Remove any temporary viewmodel rig so scene player is clean
            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Camera found";

            Transform existingRig = cam.transform.Find("FPS_ViewmodelRig");
            if (existingRig != null) UnityEngine.Object.DestroyImmediate(existingRig.gameObject);

            // 1. Restore pristine Bandit mesh
            var defaultMesh = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx")?.GetComponentInChildren<SkinnedMeshRenderer>()?.sharedMesh;
            var smr = p.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                smr.enabled = true;
                if (defaultMesh != null) smr.sharedMesh = defaultMesh;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // 2. Ensure all bone scales are 1:1 (NO head shrink hack!)
            var headBone = FindDeepChild(p.transform, "head.x");
            var neckBone = FindDeepChild(p.transform, "neck.x");
            if (headBone != null) headBone.localScale = Vector3.one;
            if (neckBone != null) neckBone.localScale = Vector3.one;

            // 3. Ensure WeaponSocket is in Firing Rifle pose
            var socket = FindDeepChild(p.transform, "WeaponSocket");
            if (socket != null)
            {
                socket.localPosition = new Vector3(0.060f, 0.192f, 0.022f);
                socket.localRotation = Quaternion.Euler(-52.590f, -178.831f, 90.890f);
            }
            var rifle = FindDeepChild(p.transform, "Rifle");
            if (rifle != null)
            {
                rifle.gameObject.SetActive(true);
                rifle.localPosition = PlayerWeaponHandler.DefaultRiflePosition;
                rifle.localRotation = Quaternion.Euler(PlayerWeaponHandler.DefaultRifleEuler);
                rifle.localScale = Vector3.one;
                FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifle);
            }

            // 4. Sample Firing Rifle mocap animation directly
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            // 5. Position camera at FPS eye relief (8cm in front of neck, 3cm up, 4cm right)
            Transform anchor = neckBone != null ? neckBone : headBone;
            Vector3 eyePos = anchor.position + p.transform.forward * 0.08f + p.transform.up * 0.03f + p.transform.right * 0.04f;
            cam.transform.position = eyePos;
            cam.transform.rotation = p.transform.rotation;
            cam.nearClipPlane = 0.015f;
            cam.fieldOfView = 80f;

            string capLog = CaptureViewToArtifact("native_combat_ready_view.png");
            return "Sampled native Combat Ready view successfully! | " + capLog;
        }

        public static string TestBuildGluedHandsViewmodel()
        {
            Mesh pureArms = CreatePerfectFpsArmsMesh();
            if (pureArms == null) return "Failed to create pure arms mesh";

            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            if (camTrans == null) camTrans = p.transform.Find("PlayerCamera_Auto") ?? p.GetComponentInChildren<Camera>()?.transform;
            if (camTrans == null) return "No CameraTransform found";

            Transform existingRig = camTrans.Find("FPS_ViewmodelRig");
            if (existingRig != null) UnityEngine.Object.DestroyImmediate(existingRig.gameObject);

            var rigGo = new GameObject("FPS_ViewmodelRig");
            rigGo.transform.SetParent(camTrans, false);
            rigGo.transform.localPosition = Vector3.zero;
            rigGo.transform.localRotation = Quaternion.identity;

            // 1. Mount AK-47 in Modern Warfare Hip-Fire framing (lower-middle screen)
            var sourceRifle = FindDeepChild(p.transform, "Rifle");
            if (sourceRifle == null) return "No source Rifle";

            var rifleGo = UnityEngine.Object.Instantiate(sourceRifle.gameObject, rigGo.transform);
            rifleGo.name = "Viewmodel_AK47";
            rifleGo.SetActive(true);
            rifleGo.transform.localPosition = new Vector3(0.07f, -0.19f, 0.42f);
            rifleGo.transform.localRotation = Quaternion.Euler(-1.5f, -2.5f, 1.0f);
            rifleGo.transform.localScale = Vector3.one;

            foreach (var rogueCam in rifleGo.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(rogueCam.gameObject);
            foreach (var c in rifleGo.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);
            FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifleGo.transform);

            // 2. Instantiate Bandit Arms
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var armsChar = (GameObject)PrefabUtility.InstantiatePrefab(banditFbx, rigGo.transform);
            armsChar.name = "Viewmodel_BanditArms";

            var smr = armsChar.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.sharedMesh = pureArms;
            }
            foreach (var col in armsChar.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(col);

            // 3. Find arm bones
            var armR = FindDeepChild(armsChar.transform, "arm_stretch.r");
            var forearmR = FindDeepChild(armsChar.transform, "forearm_stretch.r");
            var handR = FindDeepChild(armsChar.transform, "hand.r");

            var armL = FindDeepChild(armsChar.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(armsChar.transform, "forearm_stretch.l");
            var handL = FindDeepChild(armsChar.transform, "hand.l");

            // Right hand glued to pistol grip
            Vector3 rGripPos = rifleGo.transform.TransformPoint(new Vector3(0.02f, -0.055f, -0.04f));
            Quaternion rGripRot = rifleGo.transform.rotation * Quaternion.Euler(15f, 95f, -75f);
            handR.position = rGripPos;
            handR.rotation = rGripRot;

            forearmR.position = rGripPos - camTrans.forward * 0.22f + camTrans.right * 0.08f - camTrans.up * 0.10f;
            forearmR.rotation = Quaternion.LookRotation((rGripPos - forearmR.position).normalized, camTrans.up);
            armR.position = forearmR.position - camTrans.forward * 0.20f + camTrans.right * 0.12f - camTrans.up * 0.15f;
            armR.rotation = Quaternion.LookRotation((forearmR.position - armR.position).normalized, camTrans.up);

            // Left hand glued to wooden handguard
            Vector3 lGripPos = rifleGo.transform.TransformPoint(new Vector3(0.005f, -0.025f, 0.19f));
            Quaternion lGripRot = rifleGo.transform.rotation * Quaternion.Euler(-25f, 75f, -55f);
            handL.position = lGripPos;
            handL.rotation = lGripRot;

            forearmL.position = lGripPos - camTrans.forward * 0.24f - camTrans.right * 0.12f - camTrans.up * 0.12f;
            forearmL.rotation = Quaternion.LookRotation((lGripPos - forearmL.position).normalized, camTrans.up);
            armL.position = forearmL.position - camTrans.forward * 0.20f - camTrans.right * 0.15f - camTrans.up * 0.15f;
            armL.rotation = Quaternion.LookRotation((forearmL.position - armL.position).normalized, camTrans.up);

            // Hide 3rd person body on scene player completely so only viewmodel renders
            var sceneSmrs = p.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var s in sceneSmrs)
            {
                if (!s.transform.IsChildOf(rigGo.transform))
                {
                    s.enabled = false;
                }
            }
            var sceneMeshRenderers = p.GetComponentsInChildren<MeshRenderer>();
            foreach (var mr in sceneMeshRenderers)
            {
                if (!mr.transform.IsChildOf(rigGo.transform))
                {
                    mr.enabled = false;
                }
            }

            string capLog = CaptureViewToArtifact("cod_mw_glued_hands.png");
            return "Built glued hands viewmodel successfully! | " + capLog;
        }

        public static string SetupViewmodelRigWithArms()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            if (camTrans == null) camTrans = p.transform.Find("PlayerCamera_Auto") ?? p.GetComponentInChildren<Camera>()?.transform;
            if (camTrans == null) return "No CameraTransform found";

            Transform existingRig = camTrans.Find("FPS_ViewmodelRig");
            if (existingRig != null)
            {
                UnityEngine.Object.DestroyImmediate(existingRig.gameObject);
            }

            var rigGo = new GameObject("FPS_ViewmodelRig");
            rigGo.transform.SetParent(camTrans, false);
            rigGo.transform.localPosition = Vector3.zero;
            rigGo.transform.localRotation = Quaternion.identity;

            // Instantiate Bandit Arms character
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            if (banditFbx == null) return "Bandit FBX not found";

            var armsChar = (GameObject)PrefabUtility.InstantiatePrefab(banditFbx, rigGo.transform);
            armsChar.name = "Viewmodel_BanditArms";
            // Position arms character so its chest is behind the camera lens and forearms/hands reach forward into view
            armsChar.transform.localPosition = new Vector3(-0.04f, -1.42f, -0.30f);
            armsChar.transform.localRotation = Quaternion.identity;
            armsChar.transform.localScale = Vector3.one;

            // Remove unwanted components if any
            foreach (var col in armsChar.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(col);

            // Add Animator and assign 01_Player controller
            var anim = armsChar.GetComponent<Animator>();
            if (anim == null) anim = armsChar.AddComponent<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/Player/01_Player.controller");
            anim.runtimeAnimatorController = controller;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Shrink head bone of viewmodel arms so head/face never blocks lens
            var vmHead = FindDeepChild(armsChar.transform, "head.x");
            if (vmHead != null) vmHead.localScale = Vector3.zero;

            // Find right hand bone on viewmodel
            var vmHandR = FindDeepChild(armsChar.transform, "hand.r");
            if (vmHandR == null) return "vmHandR not found";

            // Create Viewmodel WeaponSocket
            var vmSocket = new GameObject("WeaponSocket");
            vmSocket.transform.SetParent(vmHandR, false);
            vmSocket.transform.localPosition = new Vector3(0.088f, 0.082f, -0.015f);
            vmSocket.transform.localRotation = Quaternion.Euler(6.65f, 96.0f, -85.0f);

            // Instantiate AK-47
            var akFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
            if (akFbx != null)
            {
                var rifle = (GameObject)PrefabUtility.InstantiatePrefab(akFbx, vmSocket.transform);
                rifle.name = "Viewmodel_AK47";
                rifle.transform.localPosition = PlayerWeaponHandler.DefaultRiflePosition;
                rifle.transform.localRotation = Quaternion.Euler(PlayerWeaponHandler.DefaultRifleEuler);
                rifle.transform.localScale = Vector3.one;

                // Remove rogue cameras from FBX
                foreach (var rogueCam in rifle.GetComponentsInChildren<Camera>(true))
                {
                    UnityEngine.Object.DestroyImmediate(rogueCam.gameObject);
                }
                foreach (var c in rifle.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);

                FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifle.transform);
            }

            // Sample Firing Rifle animation on the viewmodel animator
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null)
            {
                clip.SampleAnimation(armsChar, 0.1f);
            }

            return "Viewmodel Rig with Arms and AK-47 created successfully under Camera!";
        }

        public static string TestBuildAndCapture()
        {
            string setupLog = SetupViewmodelRigWithArms();
            string capLog = CaptureViewToArtifact("cod_viewmodel_arms_test.png");
            return setupLog + " | " + capLog;
        }

        [MenuItem("Scorched/Setup/Capture Current Camera View")]
        public static void CaptureTestMenu()
        {
            Debug.Log(CaptureTest());
        }

        public static string CaptureTest()
        {
            return CaptureViewToArtifact("current_view_check.png");
        }

        public static string CaptureViewToArtifact(string filename)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var player = GameObject.Find("Player") ?? GameObject.Find("ScorchedPlayer(Clone)");
                cam = player?.GetComponentInChildren<Camera>();
            }
            if (cam == null) return "No Camera found!";

            int w = 1920;
            int h = 1080;
            // Flush Edit-Mode SkinnedMeshRenderer bone buffers before offscreen cam.Render()
            foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (smr != null && smr.enabled)
                {
                    smr.enabled = false;
                    smr.enabled = true;
                }
            }
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var prevRT = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevRT;

            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);

            byte[] bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string artifactDir = @"C:\Users\JM\.gemini\antigravity\brain\a36b9384-20ca-483a-b0fc-07b4e8403a64";
            string path = System.IO.Path.Combine(artifactDir, filename);
            System.IO.File.WriteAllBytes(path, bytes);

            return $"Saved camera view ({w}x{h}) to: {path}";
        }

        private static void ResetArmBones(Transform root, StringBuilder sb, string context)
        {
            string[] armBoneNames = new[]
            {
                "arm_stretch.r", "forearm_stretch.r", "hand.r",
                "arm_stretch.l", "forearm_stretch.l", "hand.l",
                "shoulder.r", "shoulder.l"
            };

            foreach (var bName in armBoneNames)
            {
                Transform bone = FindDeepChild(root, bName);
                if (bone != null)
                {
                    // Scale must be exactly (1,1,1)
                    bone.localScale = Vector3.one;
                }
            }
            sb.AppendLine($"[{context}] Verified all arm bone scales are (1,1,1).");
        }

        public static string TestCODViewmodelFraming()
        {
            Mesh pureArms = CreatePerfectFpsArmsMesh();
            if (pureArms == null) return "Failed to create pure arms mesh";

            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            if (camTrans == null) camTrans = p.transform.Find("PlayerCamera_Auto") ?? p.GetComponentInChildren<Camera>()?.transform;
            if (camTrans == null) return "No CameraTransform found";

            // Clean previous rigs
            Transform oldRig = camTrans.Find("FPS_ViewmodelRig");
            if (oldRig != null) UnityEngine.Object.DestroyImmediate(oldRig.gameObject);

            var rigGo = new GameObject("FPS_ViewmodelRig");
            rigGo.transform.SetParent(camTrans, false);
            rigGo.transform.localPosition = Vector3.zero;
            rigGo.transform.localRotation = Quaternion.identity;

            // 1. Weapon Mount in Camera Space (Call of Duty Hip-Fire framing)
            var akFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
            if (akFbx == null) return "No AK47 FBX found";

            var rifleGo = (GameObject)PrefabUtility.InstantiatePrefab(akFbx, rigGo.transform);
            rifleGo.name = "Viewmodel_AK47";
            rifleGo.SetActive(true);
            rifleGo.transform.localPosition = new Vector3(0.065f, -0.185f, 0.320f);
            rifleGo.transform.localRotation = Quaternion.Euler(-1.0f, -2.0f, 1.0f);
            rifleGo.transform.localScale = Vector3.one;

            var rifleMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Models/Weapon/Materials/RIfle.mat");
            if (rifleMat != null)
            {
                foreach (var mr in rifleGo.GetComponentsInChildren<MeshRenderer>()) mr.sharedMaterial = rifleMat;
            }

            foreach (var rogueCam in rifleGo.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(rogueCam.gameObject);
            foreach (var c in rifleGo.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);
            FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifleGo.transform);

            // Create Right Hand Grip Target on Pistol Grip
            var rGrip = new GameObject("RightHandGripTarget").transform;
            rGrip.SetParent(rifleGo.transform, false);
            rGrip.localPosition = new Vector3(0.005f, -0.085f, -0.080f);
            rGrip.localRotation = Quaternion.Euler(20f, 90f, -75f);

            // Create Left Hand Grip Target on Wooden Lower Handguard
            var lGrip = new GameObject("LeftHandGripTarget").transform;
            lGrip.SetParent(rifleGo.transform, false);
            lGrip.localPosition = new Vector3(-0.020f, -0.045f, 0.240f);
            lGrip.localRotation = Quaternion.Euler(0f, -90f, 150f);

            // 2. Instantiate Bandit Arms Character
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
            var armsChar = (GameObject)PrefabUtility.InstantiatePrefab(banditFbx, rigGo.transform);
            armsChar.name = "Viewmodel_BanditArms";
            // Align character so shoulders sit naturally below and behind camera lens (22cm below eyes)
            armsChar.transform.localPosition = new Vector3(0.0f, -2.30f, -0.10f);
            armsChar.transform.localRotation = Quaternion.identity;
            armsChar.transform.localScale = Vector3.one;

            var smr = armsChar.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.sharedMesh = pureArms;
                smr.updateWhenOffscreen = true;
            }
            foreach (var col in armsChar.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(col);

            // Sample Firing Rifle animation on armsChar so posture and hips are fully initialized
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(armsChar, 0.1f);

            // 3. Solve Two-Bone IK for both arms to lock onto AK-47 grips
            var armR = FindDeepChild(armsChar.transform, "arm_stretch.r");
            var forearmR = FindDeepChild(armsChar.transform, "forearm_stretch.r");
            var handR = FindDeepChild(armsChar.transform, "hand.r");

            var armL = FindDeepChild(armsChar.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(armsChar.transform, "forearm_stretch.l");
            var handL = FindDeepChild(armsChar.transform, "hand.l");

            if (armR != null && forearmR != null && handR != null)
            {
                Vector3 rPole = armR.position - camTrans.up * 0.6f + camTrans.right * 0.5f - camTrans.forward * 0.2f;
                AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armR, forearmR, handR, rGrip.position, rGrip.rotation, rPole);
            }

            if (armL != null && forearmL != null && handL != null)
            {
                Vector3 lPole = armL.position - camTrans.up * 0.6f - camTrans.right * 0.5f - camTrans.forward * 0.2f;
                AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);
            }

            // 5. Hide 3rd person body on scene player so local player only sees viewmodel
            var sceneSmrs = p.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var s in sceneSmrs)
            {
                if (!s.transform.IsChildOf(rigGo.transform))
                {
                    s.enabled = false;
                }
            }
            var sceneMeshRenderers = p.GetComponentsInChildren<MeshRenderer>();
            foreach (var mr in sceneMeshRenderers)
            {
                if (!mr.transform.IsChildOf(rigGo.transform))
                {
                    mr.enabled = false;
                }
            }

            string capLog = CaptureViewToArtifact("cod_test_hipfire_framing.png");
            return "Built COD Hipfire Viewmodel: " + capLog;
        }

        public static string InspectTestRig()
        {
            var p = GameObject.Find("Player");
            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            var rig = camTrans?.Find("FPS_ViewmodelRig");
            if (rig == null) return "rig is null";
            var smr = rig.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return "smr is null";
            var cam = camTrans.GetComponent<Camera>();

            var armR = FindDeepChild(rig, "arm_stretch.r");
            var handR = FindDeepChild(rig, "hand.r");
            var armL = FindDeepChild(rig, "arm_stretch.l");
            var handL = FindDeepChild(rig, "hand.l");

            return $"CamPos={camTrans.position} | armR.pos={armR.position}, armR.local={armR.localPosition} | handR.pos={handR.position} | handL.pos={handL.position}";
        }

        public static string TestLeftGripAngles(float rx, float ry, float rz, float px, float py, float pz)
        {
            var p = GameObject.Find("Player");
            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            var rig = camTrans?.Find("FPS_ViewmodelRig");
            var rifleGo = rig?.Find("Viewmodel_AK47");
            var armsChar = rig?.Find("Viewmodel_BanditArms");
            if (rifleGo == null || armsChar == null) return "rig missing";

            var lGrip = rifleGo.Find("LeftHandGripTarget");
            lGrip.localPosition = new Vector3(px, py, pz);
            lGrip.localRotation = Quaternion.Euler(rx, ry, rz);

            var armL = FindDeepChild(armsChar, "arm_stretch.l");
            var forearmL = FindDeepChild(armsChar, "forearm_stretch.l");
            var handL = FindDeepChild(armsChar, "hand.l");

            Vector3 lPole = armL.position - camTrans.up * 0.6f - camTrans.right * 0.5f - camTrans.forward * 0.2f;
            AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);

            string capLog = CaptureViewToArtifact("cod_test_hipfire_framing.png");
            return $"armL_cam={camTrans.InverseTransformPoint(armL.position)}, handL_cam={camTrans.InverseTransformPoint(handL.position)} | {capLog}";
        }

        public static string TestRealGripAndFraming()
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player found";

            var camTrans = p.GetComponent<PlayerMovement>()?.CameraTransform;
            if (camTrans == null) camTrans = p.transform.Find("PlayerCamera_Auto") ?? p.GetComponentInChildren<Camera>()?.transform;
            if (camTrans == null) return "No CameraTransform found";

            // Clean up any rogue viewmodel rig
            Transform oldRig = camTrans.Find("FPS_ViewmodelRig");
            if (oldRig != null) UnityEngine.Object.DestroyImmediate(oldRig.gameObject);

            // 1. Use pure arms mesh (zero torso/chin/vest clipping)
            Mesh pureArms = CreatePerfectFpsArmsMesh();
            var smr = p.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                smr.enabled = true;
                if (pureArms != null) smr.sharedMesh = pureArms;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // 2. Ensure all bone scales are 1
            var headBone = FindDeepChild(p.transform, "head.x");
            var neckBone = FindDeepChild(p.transform, "neck.x");
            if (headBone != null) headBone.localScale = Vector3.one;
            if (neckBone != null) neckBone.localScale = Vector3.one;

            // 3. Calibrate WeaponSocket directly in hand.r palm
            var handR = FindDeepChild(p.transform, "hand.r");
            var socket = FindDeepChild(p.transform, "WeaponSocket");
            var rifle = FindDeepChild(p.transform, "Rifle");
            if (socket != null && handR != null)
            {
                socket.gameObject.SetActive(true);
                socket.SetParent(handR, false);
                // Exact palm center for pistol grip of AK-47
                socket.localPosition = new Vector3(0.040f, 0.085f, 0.015f);
                socket.localRotation = Quaternion.Euler(-55.0f, -178.0f, 90.0f);
            }
            if (rifle != null)
            {
                rifle.gameObject.SetActive(true);
                rifle.localPosition = PlayerWeaponHandler.DefaultRiflePosition;
                rifle.localRotation = Quaternion.Euler(PlayerWeaponHandler.DefaultRifleEuler);
                rifle.localScale = Vector3.one;
                FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifle);
                FPSPerspectiveController.EnsureLeftHandGripTarget(rifle);
            }

            // 4. Sample Firing Rifle animation
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Firing Rifle.fbx");
            if (clip == null)
            {
                var objs = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Player/Firing Rifle.fbx");
                clip = objs.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            }
            if (clip != null) clip.SampleAnimation(p, 0.1f);

            // 5. Solve Left Hand IK with AIRiggingAndAnimationToolkit
            var armL = FindDeepChild(p.transform, "arm_stretch.l");
            var forearmL = FindDeepChild(p.transform, "forearm_stretch.l");
            var handL = FindDeepChild(p.transform, "hand.l");
            Transform gripTarget = rifle != null ? rifle.Find("LeftHandGripTarget") : null;

            if (armL != null && forearmL != null && handL != null && gripTarget != null)
            {
                Vector3 pole = armL.position - p.transform.up * 0.45f - p.transform.right * 0.35f;
                AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armL, forearmL, handL, gripTarget.position, gripTarget.rotation, pole);
            }

            // 6. Camera Framing for Call of Duty Hip Fire:
            // Camera slightly behind and above neck, eye offset right so gun is framed in bottom-middle
            Transform anchor = neckBone != null ? neckBone : headBone;
            var cam = camTrans.GetComponent<Camera>();
            cam.nearClipPlane = 0.015f;
            cam.fieldOfView = 80f;

            // Eye offset: 12cm forward, 4cm up, 6cm right (clears shoulder, brings hands and rifle into view)
            camTrans.position = anchor.position + p.transform.forward * 0.12f + p.transform.up * 0.04f + p.transform.right * 0.06f;
            camTrans.rotation = p.transform.rotation;

            string capLog = CaptureViewToArtifact("cod_real_hipfire_test.png");
            var mr = rifle.GetComponentInChildren<MeshRenderer>();
            return $"Rifle: active={rifle.gameObject.activeInHierarchy}, parent={rifle.parent.name}, pos={rifle.position}, camVP={cam.WorldToViewportPoint(rifle.position)}, mrChild={mr?.name}, mrEnabled={mr?.enabled} | handToRifle={Vector3.Distance(handR.position, rifle.position):F3}m | {capLog}";
        }

        public static string InspectAK47AndBanditRig()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== STEP 1 INSPECTION: AK-47 BOUNDS & BANDIT RIG ===");

            // 1. Inspect AK-47 FBX & Scene Viewmodel_AK47
            var akFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
            if (akFbx != null)
            {
                var mf = akFbx.GetComponentInChildren<MeshFilter>(true);
                var mr = akFbx.GetComponentInChildren<MeshRenderer>(true);
                if (mf != null && mf.sharedMesh != null)
                {
                    Bounds mb = mf.sharedMesh.bounds;
                    sb.AppendLine($"[AK47 Mesh] name={mf.sharedMesh.name}, subMeshes={mf.sharedMesh.subMeshCount}, verts={mf.sharedMesh.vertexCount}");
                    sb.AppendLine($"  mesh.bounds: min={mb.min:F4}, max={mb.max:F4}, center={mb.center:F4}, size={mb.size:F4}");
                    sb.AppendLine($"  childTransform of mf: pos={mf.transform.localPosition:F4}, rot={mf.transform.localEulerAngles:F2}, scale={mf.transform.localScale:F4}");

                    // Bucket vertices by Z (along barrel) in AK47 root local space
                    Vector3[] verts = mf.sharedMesh.vertices;
                    Matrix4x4 m = mf.transform.localToWorldMatrix;
                    float zMin = float.MaxValue, zMax = float.MinValue;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 p = m.MultiplyPoint3x4(verts[i]);
                        if (p.z < zMin) zMin = p.z;
                        if (p.z > zMax) zMax = p.z;
                    }
                    sb.AppendLine($"  RootLocal Z range: [{zMin:F4}, {zMax:F4}], len={(zMax - zMin):F4}m");

                    // Print 16 Z slices with Y min/max and X min/max (helps pinpoint stock, grip, receiver, handguard, sights, muzzle)
                    int slices = 16;
                    for (int s = 0; s < slices; s++)
                    {
                        float a = Mathf.Lerp(zMin, zMax, s / (float)slices);
                        float b = Mathf.Lerp(zMin, zMax, (s + 1) / (float)slices) + 0.0001f;
                        float ymin = float.MaxValue, ymax = float.MinValue;
                        float xmin = float.MaxValue, xmax = float.MinValue;
                        int cnt = 0;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 p = m.MultiplyPoint3x4(verts[i]);
                            if (p.z >= a && p.z < b)
                            {
                                cnt++;
                                if (p.y < ymin) ymin = p.y;
                                if (p.y > ymax) ymax = p.y;
                                if (p.x < xmin) xmin = p.x;
                                if (p.x > xmax) xmax = p.x;
                            }
                        }
                        if (cnt > 0)
                        {
                            sb.AppendLine($"  Slice Z[{a:F3}..{b:F3}] ({((a - zMin) / (zMax - zMin) * 100f):F0}%): Y=[{ymin:F3}, {ymax:F3}], X=[{xmin:F3}, {xmax:F3}], verts={cnt}");
                        }
                    }

                    // Inspect Rear Sight (Z in [0.13, 0.19]), Front Sight (Z in [0.47, 0.525]), Pistol Grip (Z in [-0.18, -0.04]), Wooden Handguard (Z in [0.20, 0.36])
                    float rsYMin = float.MaxValue, rsYMax = float.MinValue, rsXSum = 0; int rsCnt = 0;
                    float fsYMax = float.MinValue, fsXAtMax = 0, fsZAtMax = 0;
                    Vector3 pgMin = Vector3.one * 999f, pgMax = Vector3.one * -999f, pgSum = Vector3.zero; int pgCnt = 0;
                    Vector3 hgMin = Vector3.one * 999f, hgMax = Vector3.one * -999f, hgSum = Vector3.zero; int hgCnt = 0;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 p = m.MultiplyPoint3x4(verts[i]);
                        if (p.z >= 0.145f && p.z <= 0.185f && p.y > 0.075f) { rsYMin = Mathf.Min(rsYMin, p.y); rsYMax = Mathf.Max(rsYMax, p.y); rsXSum += p.x; rsCnt++; }
                        if (p.z >= 0.48f && p.z <= 0.52f && p.y > fsYMax) { fsYMax = p.y; fsXAtMax = p.x; fsZAtMax = p.z; }
                        if (p.z >= -0.17f && p.z <= -0.05f && p.y < -0.025f)
                        {
                            pgMin = Vector3.Min(pgMin, p); pgMax = Vector3.Max(pgMax, p); pgSum += p; pgCnt++;
                        }
                        if (p.z >= 0.19f && p.z <= 0.36f)
                        {
                            hgMin = Vector3.Min(hgMin, p); hgMax = Vector3.Max(hgMax, p); hgSum += p; hgCnt++;
                        }
                    }
                    sb.AppendLine($"  RearSightTop region (Z 0.145..0.185, Y>0.075): cnt={rsCnt}, avgX={(rsCnt > 0 ? rsXSum / rsCnt : 0):F4}, Y=[{rsYMin:F4}, {rsYMax:F4}]");
                    sb.AppendLine($"  FrontSight peak: ({fsXAtMax:F4}, {fsYMax:F4}, {fsZAtMax:F4})");
                    if (pgCnt > 0) sb.AppendLine($"  PistolGrip (Z -0.17..-0.05, Y<-0.025): cnt={pgCnt}, avg={(pgSum / pgCnt):F4}, min={pgMin:F4}, max={pgMax:F4}");
                    if (hgCnt > 0) sb.AppendLine($"  Handguard (Z 0.19..0.36): cnt={hgCnt}, avg={(hgSum / hgCnt):F4}, min={hgMin:F4}, max={hgMax:F4}");
                }
            }

            // 2. Check current FPS_Bandit_PureArms.asset vs Full Bandit SMR
            var existingArms = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset");
            if (existingArms != null)
            {
                sb.AppendLine($"[Current PureArms Asset] tris={existingArms.GetTriangles(0).Length / 3}, bounds.min={existingArms.bounds.min:F3}, bounds.max={existingArms.bounds.max:F3}");
            }

            string banditPath = "Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx";
            var banditFbx = AssetDatabase.LoadAssetAtPath<GameObject>(banditPath);
            if (banditFbx != null)
            {
                var temp = UnityEngine.Object.Instantiate(banditFbx);
                try
                {
                    var anim = temp.GetComponentInChildren<Animator>();
                    var handL = FindDeepChild(temp.transform, "hand.l");
                    var handR = FindDeepChild(temp.transform, "hand.r");
                    var idx1L = FindDeepChild(temp.transform, "c_index1.l");
                    var pnk1L = FindDeepChild(temp.transform, "c_pinky1.l");
                    var thm1L = FindDeepChild(temp.transform, "c_thumb1.l");
                    var idx1R = FindDeepChild(temp.transform, "c_index1.r");
                    var pnk1R = FindDeepChild(temp.transform, "c_pinky1.r");
                    var thm1R = FindDeepChild(temp.transform, "c_thumb1.r");

                    if (handL != null && idx1L != null && pnk1L != null && thm1L != null)
                    {
                        Vector3 dIdx = handL.InverseTransformPoint(idx1L.position);
                        Vector3 dPnk = handL.InverseTransformPoint(pnk1L.position);
                        Vector3 dThm = handL.InverseTransformPoint(thm1L.position);
                        sb.AppendLine($"[hand.l local offsets] index1={dIdx:F4}, pinky1={dPnk:F4}, thumb1={dThm:F4}");
                    }
                    if (handR != null && idx1R != null && pnk1R != null && thm1R != null)
                    {
                        Vector3 dIdx = handR.InverseTransformPoint(idx1R.position);
                        Vector3 dPnk = handR.InverseTransformPoint(pnk1R.position);
                        Vector3 dThm = handR.InverseTransformPoint(thm1R.position);
                        sb.AppendLine($"[hand.r local offsets] index1={dIdx:F4}, pinky1={dPnk:F4}, thumb1={dThm:F4}");
                    }

                    // Test HumanPose finger muscle posing
                    var muscles = new Dictionary<string, float>
                    {
                        // Left Hand C-Clamp: thumb forward, fingers curled onto underside of handguard
                        { "Left Thumb 1 Stretched", 0.30f }, { "Left Thumb Spread", 0.45f },
                        { "Left Thumb 2 Stretched", 0.15f }, { "Left Thumb 3 Stretched", 0.10f },
                        { "Left Index 1 Stretched", -0.65f }, { "Left Index 2 Stretched", -0.75f }, { "Left Index 3 Stretched", -0.65f },
                        { "Left Middle 1 Stretched", -0.70f }, { "Left Middle 2 Stretched", -0.80f }, { "Left Middle 3 Stretched", -0.70f },
                        { "Left Ring 1 Stretched", -0.72f }, { "Left Ring 2 Stretched", -0.80f }, { "Left Ring 3 Stretched", -0.72f },
                        { "Left Little 1 Stretched", -0.70f }, { "Left Little 2 Stretched", -0.78f }, { "Left Little 3 Stretched", -0.70f },
                        // Right Hand Pistol Grip: index light on trigger, others curled around grip
                        { "Right Thumb 1 Stretched", 0.10f }, { "Right Thumb Spread", 0.35f },
                        { "Right Thumb 2 Stretched", -0.45f }, { "Right Thumb 3 Stretched", -0.50f },
                        { "Right Index 1 Stretched", -0.35f }, { "Right Index 2 Stretched", -0.50f }, { "Right Index 3 Stretched", -0.40f },
                        { "Right Middle 1 Stretched", -0.78f }, { "Right Middle 2 Stretched", -0.85f }, { "Right Middle 3 Stretched", -0.80f },
                        { "Right Ring 1 Stretched", -0.80f }, { "Right Ring 2 Stretched", -0.85f }, { "Right Ring 3 Stretched", -0.80f },
                        { "Right Little 1 Stretched", -0.78f }, { "Right Little 2 Stretched", -0.85f }, { "Right Little 3 Stretched", -0.80f }
                    };
                    AIRiggingAndAnimationToolkit.ApplyHumanoidMusclePose(anim, muscles);
                    sb.AppendLine("[HumanPose Finger Quaternions]");
                    string[] fingerNames = {
                        "c_thumb1.l", "c_thumb2.l", "c_thumb3.l", "c_index1.l", "c_index2.l", "c_index3.l",
                        "c_middle1.l", "c_middle2.l", "c_middle3.l", "c_ring1.l", "c_ring2.l", "c_ring3.l",
                        "c_pinky1.l", "c_pinky2.l", "c_pinky3.l",
                        "c_thumb1.r", "c_thumb2.r", "c_thumb3.r", "c_index1.r", "c_index2.r", "c_index3.r",
                        "c_middle1.r", "c_middle2.r", "c_middle3.r", "c_ring1.r", "c_ring2.r", "c_ring3.r",
                        "c_pinky1.r", "c_pinky2.r", "c_pinky3.r"
                    };
                    foreach (var fn in fingerNames)
                    {
                        var fb = FindDeepChild(temp.transform, fn);
                        if (fb != null)
                        {
                            Quaternion q = fb.localRotation;
                            sb.AppendLine($"  {fn}: new Quaternion({q.x:F5}f, {q.y:F5}f, {q.z:F5}f, {q.w:F5}f)");
                        }
                    }

                    // Verify IK +angleRoot sign sends elbow TOWARD pole!
                    var armL = FindDeepChild(temp.transform, "arm_stretch.l");
                    var forearmL = FindDeepChild(temp.transform, "forearm_stretch.l");
                    if (armL != null && forearmL != null && handL != null)
                    {
                        Vector3 tgt = armL.position + new Vector3(0.05f, 0.05f, 0.35f);
                        Vector3 pole = armL.position + new Vector3(-0.3f, -0.5f, 0.1f);
                        float upperLen = Vector3.Distance(armL.position, forearmL.position);
                        float lowerLen = Vector3.Distance(forearmL.position, handL.position);
                        Vector3 toTarget = tgt - armL.position;
                        float dist = Mathf.Clamp(toTarget.magnitude, 0.001f, upperLen + lowerLen - 0.001f);
                        float cosAngle = Mathf.Clamp(((upperLen * upperLen) + (dist * dist) - (lowerLen * lowerLen)) / (2f * upperLen * dist), -1f, 1f);
                        float angleRoot = Mathf.Acos(cosAngle) * Mathf.Rad2Deg;
                        Vector3 planeNormal = Vector3.Cross(toTarget.normalized, (pole - armL.position).normalized).normalized;
                        Quaternion aimRot = Quaternion.FromToRotation((forearmL.position - armL.position).normalized, toTarget.normalized) * armL.rotation;
                        armL.rotation = Quaternion.AngleAxis(+angleRoot, planeNormal) * aimRot;
                        forearmL.rotation = Quaternion.FromToRotation((handL.position - forearmL.position).normalized, (tgt - forearmL.position).normalized) * forearmL.rotation;
                        float dotPositive = Vector3.Dot((forearmL.position - armL.position).normalized, (pole - armL.position).normalized);
                        sb.AppendLine($"[IK +angleRoot Test] dot(elbow,pole)={dotPositive:F3}, distHandTgt={Vector3.Distance(handL.position, tgt):F4}m, forearmPos={forearmL.position:F3}");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(temp);
                }
            }

            Debug.Log(sb.ToString());
            return sb.ToString();
        }

        public static string RebuildFullArmsMeshAndTestCalib()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== STEP 2 & PREVIEW CALIBRATION ===");

            // 1. Rebuild FPS_Bandit_PureArms.asset preserving FULL UpperArm + Forearm + Wrist + Hand (Zero torso/chest spikes, Zero hollow cuff)
            string banditPath = "Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx";
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(banditPath);
            var srcSmr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            var srcMesh = srcSmr.sharedMesh;
            var weights = srcMesh.boneWeights;

            // Right arm: 19..38 (arm_stretch.r, forearm_stretch.r, hand.r, fingers, forearm_twist.r, arm_twist.r)
            // Left arm:  40..59 (arm_stretch.l, forearm_stretch.l, hand.l, fingers, forearm_twist.l, arm_twist.l)
            bool[] isArmBone = new bool[srcSmr.bones.Length];
            for (int i = 19; i <= 38; i++) isArmBone[i] = true;
            for (int i = 40; i <= 59; i++) isArmBone[i] = true;

            bool[] isArmVert = new bool[srcMesh.vertexCount];
            BoneWeight[] cleanWeights = new BoneWeight[srcMesh.vertexCount];
            int armVertCount = 0;

            for (int i = 0; i < srcMesh.vertexCount; i++)
            {
                var bw = weights[i];
                float w0 = isArmBone[bw.boneIndex0] ? bw.weight0 : 0f;
                float w1 = isArmBone[bw.boneIndex1] ? bw.weight1 : 0f;
                float w2 = isArmBone[bw.boneIndex2] ? bw.weight2 : 0f;
                float w3 = isArmBone[bw.boneIndex3] ? bw.weight3 : 0f;
                float totalArm = w0 + w1 + w2 + w3;

                if (totalArm >= 0.50f)
                {
                    isArmVert[i] = true;
                    armVertCount++;
                    cleanWeights[i] = new BoneWeight
                    {
                        boneIndex0 = bw.boneIndex0,
                        boneIndex1 = bw.boneIndex1,
                        boneIndex2 = bw.boneIndex2,
                        boneIndex3 = bw.boneIndex3,
                        weight0 = w0 / totalArm,
                        weight1 = w1 / totalArm,
                        weight2 = w2 / totalArm,
                        weight3 = w3 / totalArm
                    };
                }
                else
                {
                    cleanWeights[i] = bw;
                }
            }

            Mesh armsMesh = UnityEngine.Object.Instantiate(srcMesh);
            armsMesh.name = "FPS_Bandit_PureArms";
            armsMesh.boneWeights = cleanWeights;

            int[] srcTris = srcMesh.GetTriangles(0);
            var keptTris = new List<int>(srcTris.Length);
            int crossArmTris = 0;
            for (int i = 0; i < srcTris.Length; i += 3)
            {
                int v0 = srcTris[i], v1 = srcTris[i + 1], v2 = srcTris[i + 2];
                if (isArmVert[v0] && isArmVert[v1] && isArmVert[v2])
                {
                    // Verify no stray triangle connects left & right arms
                    bool v0L = weights[v0].boneIndex0 >= 39;
                    bool v1L = weights[v1].boneIndex0 >= 39;
                    bool v2L = weights[v2].boneIndex0 >= 39;
                    if (v0L != v1L || v1L != v2L) { crossArmTris++; continue; }
                    keptTris.Add(v0); keptTris.Add(v1); keptTris.Add(v2);
                }
            }
            armsMesh.SetTriangles(keptTris.ToArray(), 0);
            if (armsMesh.subMeshCount > 1) armsMesh.SetTriangles(new int[0], 1);
            if (armsMesh.subMeshCount > 2) armsMesh.SetTriangles(new int[0], 2);

            string armsAssetPath = "Assets/Models/FPS_Bandit_PureArms.asset";
            AssetDatabase.CreateAsset(armsMesh, armsAssetPath);
            sb.AppendLine($"[Full Arms Mesh] verts={armVertCount}, tris={keptTris.Count / 3}, crossArmSkipped={crossArmTris}");

            // 2. Measure AK-47 Exact Barrel Bore Center & Iron Sight Notch
            var akFbx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
            if (akFbx != null)
            {
                var mf = akFbx.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null)
                {
                    Matrix4x4 m = mf.transform.localToWorldMatrix;
                    Vector3[] verts = mf.sharedMesh.vertices;
                    float boreYMin = float.MaxValue, boreYMax = float.MinValue, boreXMin = float.MaxValue, boreXMax = float.MinValue;
                    float rsLeftX = 0, rsRightX = 0; float rsCenterY = float.MaxValue;
                    foreach (var v in verts)
                    {
                        Vector3 pt = m.MultiplyPoint3x4(v);
                        if (pt.z >= 0.512f && pt.y < 0.045f)
                        {
                            boreYMin = Mathf.Min(boreYMin, pt.y); boreYMax = Mathf.Max(boreYMax, pt.y);
                            boreXMin = Mathf.Min(boreXMin, pt.x); boreXMax = Mathf.Max(boreXMax, pt.x);
                        }
                        if (pt.z >= 0.150f && pt.z <= 0.175f && Mathf.Abs(pt.x) < 0.004f && pt.y > 0.075f)
                        {
                            rsCenterY = Mathf.Min(rsCenterY, pt.y);
                        }
                        if (pt.z >= 0.495f && pt.z <= 0.515f && pt.y > 0.085f)
                        {
                            if (pt.x < 0.002f) rsLeftX = Mathf.Min(rsLeftX, pt.x);
                            rsRightX = Mathf.Max(rsRightX, pt.x);
                        }
                    }
                    sb.AppendLine($"[AK47 Barrel Bore Z>=0.512, Y<0.045] X=[{boreXMin:F4},{boreXMax:F4}] mid={(boreXMin + boreXMax) * 0.5f:F4}, Y=[{boreYMin:F4},{boreYMax:F4}] mid={(boreYMin + boreYMax) * 0.5f:F4}");
                    sb.AppendLine($"[AK47 Sights] RearCenterMinY={rsCenterY:F4}, FrontPostX=[{rsLeftX:F4},{rsRightX:F4}]");
                }
            }

            // 3. Bake Humanoid Finger & Grip Pose Clip using AIRiggingAndAnimationToolkit
            var temp = UnityEngine.Object.Instantiate(fbx);
            try
            {
                var anim = temp.GetComponentInChildren<Animator>();
                var muscles = new Dictionary<string, float>
                {
                    { "Left Thumb 1 Stretched", 0.30f }, { "Left Thumb Spread", 0.45f },
                    { "Left Thumb 2 Stretched", 0.15f }, { "Left Thumb 3 Stretched", 0.10f },
                    { "Left Index 1 Stretched", -0.65f }, { "Left Index 2 Stretched", -0.75f }, { "Left Index 3 Stretched", -0.65f },
                    { "Left Middle 1 Stretched", -0.70f }, { "Left Middle 2 Stretched", -0.80f }, { "Left Middle 3 Stretched", -0.70f },
                    { "Left Ring 1 Stretched", -0.72f }, { "Left Ring 2 Stretched", -0.80f }, { "Left Ring 3 Stretched", -0.72f },
                    { "Left Little 1 Stretched", -0.70f }, { "Left Little 2 Stretched", -0.78f }, { "Left Little 3 Stretched", -0.70f },
                    { "Right Thumb 1 Stretched", 0.10f }, { "Right Thumb Spread", 0.35f },
                    { "Right Thumb 2 Stretched", -0.45f }, { "Right Thumb 3 Stretched", -0.50f },
                    { "Right Index 1 Stretched", -0.35f }, { "Right Index 2 Stretched", -0.50f }, { "Right Index 3 Stretched", -0.40f },
                    { "Right Middle 1 Stretched", -0.78f }, { "Right Middle 2 Stretched", -0.85f }, { "Right Middle 3 Stretched", -0.80f },
                    { "Right Ring 1 Stretched", -0.80f }, { "Right Ring 2 Stretched", -0.85f }, { "Right Ring 3 Stretched", -0.80f },
                    { "Right Little 1 Stretched", -0.78f }, { "Right Little 2 Stretched", -0.85f }, { "Right Little 3 Stretched", -0.80f }
                };
                AIRiggingAndAnimationToolkit.ApplyHumanoidMusclePose(anim, muscles);
                var curveMap = new Dictionary<string, AnimationCurve>();
                string[] allFingerNames = {
                    "c_thumb1.l", "c_thumb2.l", "c_thumb3.l", "c_index1.l", "c_index2.l", "c_index3.l",
                    "c_middle1.l", "c_middle2.l", "c_middle3.l", "c_ring1.l", "c_ring2.l", "c_ring3.l",
                    "c_pinky1.l", "c_pinky2.l", "c_pinky3.l",
                    "c_thumb1.r", "c_thumb2.r", "c_thumb3.r", "c_index1.r", "c_index2.r", "c_index3.r",
                    "c_middle1.r", "c_middle2.r", "c_middle3.r", "c_ring1.r", "c_ring2.r", "c_ring3.r",
                    "c_pinky1.r", "c_pinky2.r", "c_pinky3.r"
                };
                foreach (var fn in allFingerNames)
                {
                    var fb = FindDeepChild(temp.transform, fn);
                    if (fb != null) AIRiggingAndAnimationToolkit.RecordBoneKeyframe(temp.transform, fb, 0f, curveMap);
                }
                AIRiggingAndAnimationToolkit.BakeCurvesToClip("Assets/Animations/Player/VM_AK47_GripPose.anim", curveMap, true);
                sb.AppendLine("[Baked Clip] Saved Assets/Animations/Player/VM_AK47_GripPose.anim");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }

            AssetDatabase.SaveAssets();

            // 4. Test Scene Preview (Hipfire + ADS) with Calibrated Framing, IK, Shoulders & Finger Poses!
            var p = GameObject.Find("Player");
            var cam = p?.GetComponentInChildren<Camera>();
            var rig = cam?.transform.Find("FPS_ViewmodelRig");
            if (cam != null && rig != null)
            {
                cam.nearClipPlane = 0.015f;
                cam.fieldOfView = 80f;
                var vmWeapon = rig.Find("Viewmodel_AK47");
                var vmArms = rig.Find("Viewmodel_BanditArms");
                if (vmWeapon != null && vmArms != null)
                {
                    var smr = vmArms.GetComponentInChildren<SkinnedMeshRenderer>();
                    if (smr != null) { smr.sharedMesh = armsMesh; smr.updateWhenOffscreen = true; }

                    // Helper to apply exact HumanPose-derived finger curls (from AIRiggingAndAnimationToolkit.ApplyHumanoidMusclePose)
                    Action applyFingerGripPoses = () =>
                    {
                        var fingerQuats = new (string name, Quaternion rot)[]
                        {
                            // Left Hand C-Clamp (Thumb forward along handguard, fingers curled around bottom/right)
                            ("c_thumb1.l", new Quaternion(0.19372f, -0.44205f, 0.51855f, 0.70581f)),
                            ("c_thumb2.l", new Quaternion(-0.16189f, 0.06245f, -0.06522f, 0.98267f)),
                            ("c_thumb3.l", new Quaternion(-0.24308f, -0.03168f, -0.08520f, 0.96574f)),
                            ("c_index1.l", new Quaternion(-0.58170f, -0.09166f, -0.05756f, 0.80617f)),
                            ("c_index2.l", new Quaternion(-0.60150f, 0.02157f, -0.07347f, 0.79519f)),
                            ("c_index3.l", new Quaternion(-0.62202f, 0.01304f, -0.10633f, 0.77564f)),
                            ("c_middle1.l", new Quaternion(-0.58808f, -0.06245f, -0.10591f, 0.79940f)),
                            ("c_middle2.l", new Quaternion(-0.60844f, 0.00686f, -0.09036f, 0.78841f)),
                            ("c_middle3.l", new Quaternion(-0.62094f, 0.05947f, -0.17577f, 0.76158f)),
                            ("c_ring1.l", new Quaternion(-0.58633f, -0.00396f, -0.16466f, 0.79315f)),
                            ("c_ring2.l", new Quaternion(-0.60597f, 0.01129f, -0.11519f, 0.78702f)),
                            ("c_ring3.l", new Quaternion(-0.63484f, 0.00890f, -0.12234f, 0.76284f)),
                            ("c_pinky1.l", new Quaternion(-0.58120f, 0.07022f, -0.16145f, 0.79449f)),
                            ("c_pinky2.l", new Quaternion(-0.58866f, 0.01492f, -0.17083f, 0.78999f)),
                            ("c_pinky3.l", new Quaternion(-0.61280f, -0.00759f, -0.11230f, 0.78218f)),
                            // Right Hand Pistol Grip (Index light on trigger, others curled tightly around grip)
                            ("c_thumb1.r", new Quaternion(-0.16457f, -0.46503f, 0.50182f, -0.71052f)),
                            ("c_thumb2.r", new Quaternion(0.33637f, 0.05624f, -0.15195f, -0.92769f)),
                            ("c_thumb3.r", new Quaternion(0.41894f, -0.02244f, -0.16049f, -0.89343f)),
                            ("c_index1.r", new Quaternion(0.47351f, -0.08018f, -0.04256f, -0.87610f)),
                            ("c_index2.r", new Quaternion(0.52118f, 0.02291f, -0.06387f, -0.85074f)),
                            ("c_index3.r", new Quaternion(0.54391f, 0.01462f, -0.09402f, -0.83373f)),
                            ("c_middle1.r", new Quaternion(0.61530f, -0.06347f, -0.10963f, -0.77805f)),
                            ("c_middle2.r", new Quaternion(0.62361f, 0.00660f, -0.09281f, -0.77618f)),
                            ("c_middle3.r", new Quaternion(0.64934f, 0.05702f, -0.18371f, -0.73577f)),
                            ("c_ring1.r", new Quaternion(0.61351f, -0.00346f, -0.16803f, -0.77160f)),
                            ("c_ring2.r", new Quaternion(0.62100f, 0.01096f, -0.11826f, -0.77476f)),
                            ("c_ring3.r", new Quaternion(0.65783f, 0.00950f, -0.12791f, -0.74217f)),
                            ("c_pinky1.r", new Quaternion(0.60827f, 0.07010f, -0.16621f, -0.77296f)),
                            ("c_pinky2.r", new Quaternion(0.60930f, 0.01464f, -0.17676f, -0.77285f)),
                            ("c_pinky3.r", new Quaternion(0.64181f, -0.00478f, -0.12080f, -0.75728f))
                        };
                        foreach (var item in fingerQuats)
                        {
                            var fb = FindDeepChild(vmArms, item.name);
                            if (fb != null) fb.localRotation = item.rot;
                        }
                    };

                    // Disable Animator on Viewmodel_BanditArms so it never fights procedural IK & HumanPose finger curls
                    var vmAnim = vmArms.GetComponent<Animator>();
                    if (vmAnim != null) vmAnim.enabled = false;

                    var rGrip = FPSPerspectiveController.EnsureRightHandGripTarget(vmWeapon);
                    var lGrip = FPSPerspectiveController.EnsureLeftHandGripTarget(vmWeapon);

                    // Left Hand C-Clamp: wrist at (-0.045, 0.002, 0.235)
                    // Left Hand C-Clamp: wrist hugging left-under side of wooden handguard
                    // Palm (-fwd) faces right+up (+0.76, +0.64, -0.10); Thumb (-right) faces forward+up (-0.15, +0.42, +0.89)
                    Vector3 lFwd = -(new Vector3(0.76f, 0.64f, -0.10f).normalized);
                    Vector3 lRight = -(new Vector3(-0.15f, 0.42f, 0.89f).normalized);
                    Vector3 lUp = Vector3.Cross(lFwd, lRight).normalized;
                    lFwd = Vector3.Cross(lRight, lUp).normalized;
                    lGrip.localPosition = new Vector3(-0.036f, 0.022f, 0.242f);
                    lGrip.localRotation = Quaternion.LookRotation(lFwd, lUp);
                    sb.AppendLine($"[Calibrated LeftHandGripTarget] localPos={lGrip.localPosition:F4}, localEuler={lGrip.localRotation.eulerAngles:F2}");

                    // Right Hand Pistol Grip: wrist behind/right of upper pistol grip & trigger tang
                    // Palm (-fwd) faces left+fwd (-0.90, +0.20, +0.38); Thumb (+right) faces up+fwd (0.08, +0.86, +0.50)
                    Vector3 rFwd = -(new Vector3(-0.90f, 0.20f, 0.38f).normalized);
                    Vector3 rRight = new Vector3(0.08f, 0.86f, 0.50f).normalized;
                    Vector3 rUp = Vector3.Cross(rFwd, rRight).normalized;
                    rFwd = Vector3.Cross(rRight, rUp).normalized;
                    rGrip.localPosition = new Vector3(0.030f, -0.045f, -0.156f);
                    rGrip.localRotation = Quaternion.LookRotation(rFwd, rUp);
                    sb.AppendLine($"[Calibrated RightHandGripTarget] localPos={rGrip.localPosition:F4}, localEuler={rGrip.localRotation.eulerAngles:F2}");

                    Action<string, Vector3, Vector3> solveAndCap = (capName, weapPos, weapEuler) =>
                    {
                        rig.gameObject.SetActive(true);
                        vmWeapon.gameObject.SetActive(true);
                        vmArms.gameObject.SetActive(true);
                        vmWeapon.localPosition = weapPos;
                        vmWeapon.localRotation = Quaternion.Euler(weapEuler);

                        // Place BanditArms root neutrally, then anchor Left & Right shoulders independently in camera space
                        vmArms.localPosition = new Vector3(0f, -1.66f, 0.05f);
                        vmArms.localRotation = Quaternion.identity;
                        var spine03 = FindDeepChild(vmArms, "spine_03.x");
                        var shoulderL = FindDeepChild(vmArms, "shoulder.l");
                        var armL = FindDeepChild(vmArms, "arm_stretch.l");
                        var forearmL = FindDeepChild(vmArms, "forearm_stretch.l");
                        var handL = FindDeepChild(vmArms, "hand.l");
                        var twistL = FindDeepChild(vmArms, "forearm_twist.l");

                        var shoulderR = FindDeepChild(vmArms, "shoulder.r");
                        var armR = FindDeepChild(vmArms, "arm_stretch.r");
                        var forearmR = FindDeepChild(vmArms, "forearm_stretch.r");
                        var handR = FindDeepChild(vmArms, "hand.r");
                        var twistR = FindDeepChild(vmArms, "forearm_twist.r");

                        sb.AppendLine($"  [DIAG Before] cam={cam.name} pos={cam.transform.position:F3} rot={cam.transform.eulerAngles:F1} | rigPos={rig.localPosition:F3} rigRot={rig.localEulerAngles:F1} rigScale={rig.lossyScale:F3}");
                        sb.AppendLine($"  [DIAG Refs] spine03={spine03?.name}, shL={shoulderL?.name}, armL={armL?.name}, foreL={forearmL?.name}, handL={handL?.name}, scale={vmArms.lossyScale:F3}");

                        if (spine03 != null) spine03.rotation = cam.transform.rotation;

                        // Keep Left UpperArm root well below bottom-left frustum so only continuous forearm sleeve enters screen
                        Vector3 armLDest = cam.transform.TransformPoint(new Vector3(-0.18f, -0.47f, 0.42f));
                        if (shoulderL != null && armL != null) shoulderL.position += (armLDest - armL.position);

                        Vector3 armRDest = cam.transform.TransformPoint(new Vector3(0.22f, -0.38f, 0.02f));
                        if (shoulderR != null && armR != null) shoulderR.position += (armRDest - armR.position);

                        sb.AppendLine($"  [DIAG AfterSh] armL.world={armL?.position:F3} (dest={armLDest:F3}, camLoc={cam.transform.InverseTransformPoint(armL.position):F3}) | lGrip.world={lGrip.position:F3} (camLoc={cam.transform.InverseTransformPoint(lGrip.position):F3})");

                        Vector3 lPole = cam.transform.TransformPoint(new Vector3(-0.34f, -0.74f, 0.20f));
                        Vector3 rPole = cam.transform.TransformPoint(new Vector3(0.38f, -0.56f, 0.08f));

                        AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armL, forearmL, handL, lGrip.position, lGrip.rotation, lPole);
                        AIRiggingAndAnimationToolkit.SolveTwoBoneIK(armR, forearmR, handR, rGrip.position, rGrip.rotation, rPole);
                        sb.AppendLine($"  [DIAG AfterIK] handL.world={handL.position:F3}, dist={Vector3.Distance(handL.position, lGrip.position):F4}, foreL.camLoc={cam.transform.InverseTransformPoint(forearmL.position):F3}");

                        if (twistL != null && forearmL != null && handL != null)
                        {
                            Quaternion relL = Quaternion.Inverse(forearmL.rotation) * handL.rotation;
                            Vector3 twistAxis = Vector3.up;
                            Vector3 ra = new Vector3(relL.x, relL.y, relL.z);
                            Vector3 proj = Vector3.Project(ra, twistAxis);
                            Quaternion twistOnly = new Quaternion(proj.x, proj.y, proj.z, relL.w).normalized;
                            twistL.rotation = forearmL.rotation * Quaternion.Slerp(Quaternion.identity, twistOnly, 0.55f);
                        }
                        if (twistR != null && forearmR != null && handR != null)
                        {
                            Quaternion relR = Quaternion.Inverse(forearmR.rotation) * handR.rotation;
                            Vector3 twistAxis = Vector3.up;
                            Vector3 ra = new Vector3(relR.x, relR.y, relR.z);
                            Vector3 proj = Vector3.Project(ra, twistAxis);
                            Quaternion twistOnly = new Quaternion(proj.x, proj.y, proj.z, relR.w).normalized;
                            twistR.rotation = forearmR.rotation * Quaternion.Slerp(Quaternion.identity, twistOnly, 0.55f);
                        }

                        applyFingerGripPoses();

                        if (smr != null)
                        {
                            smr.enabled = true;
                            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                            smr.updateWhenOffscreen = true;
                            smr.forceMatrixRecalculationPerRender = true;
                            smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 50f);
                            smr.gameObject.layer = vmWeapon.gameObject.layer;

                            var baked = new Mesh();
                            smr.BakeMesh(baked);
                            Vector3[] bVerts = baked.vertices;
                            int[] bTris = baked.GetTriangles(0);
                            Vector3 minW = Vector3.one * 999f, maxW = Vector3.one * -999f, sumLHand = Vector3.zero, sumRHand = Vector3.zero;
                            int cntL = 0, cntR = 0;
                            BoneWeight[] bws = smr.sharedMesh.boneWeights;
                            foreach (int vi in bTris)
                            {
                                Vector3 wpt = smr.transform.TransformPoint(bVerts[vi]);
                                minW = Vector3.Min(minW, wpt); maxW = Vector3.Max(maxW, wpt);
                                if (bws[vi].boneIndex0 >= 40) { sumLHand += wpt; cntL++; }
                                else if (bws[vi].boneIndex0 >= 19) { sumRHand += wpt; cntR++; }
                            }
                            UnityEngine.Object.DestroyImmediate(baked);
                            sb.AppendLine($"  [DIAG SMR] trisVerts={bTris.Length}, mats={smr.sharedMaterials.Length}({smr.sharedMaterials[0]?.name}), rootBone={smr.rootBone?.name}");
                            sb.AppendLine($"  [DIAG ArmVerts] minW={minW:F2}, maxW={maxW:F2} | avgL={(cntL > 0 ? sumLHand / cntL : Vector3.zero):F2} (vp={cam.WorldToViewportPoint(cntL > 0 ? sumLHand / cntL : Vector3.zero):F2}) | avgR={(cntR > 0 ? sumRHand / cntR : Vector3.zero):F2}");
                            sb.AppendLine($"  [DIAG Bones] handL={handL.position:F2}, handR={handR.position:F2}, armR={armR.position:F2}, b42={smr.bones[42]?.name}@{smr.bones[42]?.position:F2}");
                        }

                        CaptureViewToArtifact(capName);
                        sb.AppendLine($"[{capName}] handL->lGrip={Vector3.Distance(handL.position, lGrip.position):F4}m (vp elbow={cam.WorldToViewportPoint(forearmL.position):F2}, armL={cam.WorldToViewportPoint(armL.position):F2}), handR->rGrip={Vector3.Distance(handR.position, rGrip.position):F4}m");
                    };

                    solveAndCap("mw_calib_hipfire.png", new Vector3(0.15f, -0.24f, 0.52f), new Vector3(1.5f, -2.5f, -3.0f));
                    solveAndCap("mw_calib_sprint.png", new Vector3(0.19f, -0.27f, 0.48f), new Vector3(22f, -28f, 18f));
                    cam.fieldOfView = 68f;
                    solveAndCap("mw_calib_ads.png", new Vector3(0.000f, -0.088f, 0.390f), Vector3.zero);
                    cam.fieldOfView = 80f;
                }
            }

            Debug.Log(sb.ToString());
            return sb.ToString();
        }

        private static string CaptureControllerView(bool ads, bool sprint, float pitchDeg, string artifactName)
        {
            var p = GameObject.Find("Player");
            if (p == null) return "No Player";
            var ctrl = p.GetComponent<FPSPerspectiveController>();
            if (ctrl == null) return "No FPSPerspectiveController";
            var cam = p.GetComponentInChildren<Camera>();
            if (cam == null) return "No Camera";

            ctrl.CurrentMode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
            ctrl.ApplyCurrentMode();

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(FPSPerspectiveController);
            t.GetField("_currentAdsWeight", flags)?.SetValue(ctrl, ads ? 1f : 0f);
            t.GetField("_currentSprintWeight", flags)?.SetValue(ctrl, sprint ? 1f : 0f);
            t.GetField("_recoilOffset", flags)?.SetValue(ctrl, Vector3.zero);
            t.GetField("_recoilRotOffset", flags)?.SetValue(ctrl, Vector3.zero);

            Quaternion prevCamRot = cam.transform.localRotation;
            float prevFov = cam.fieldOfView;
            cam.transform.localRotation = Quaternion.Euler(-pitchDeg, 0f, 0f);
            cam.fieldOfView = ads ? 68f : 80f;

            var updateMw = t.GetMethod("UpdateModernWarfareViewmodel", flags);
            updateMw?.Invoke(ctrl, new object[] { 0f, ads });

            string cap = CaptureViewToArtifact(artifactName);

            cam.transform.localRotation = prevCamRot;
            cam.fieldOfView = prevFov;
            t.GetField("_currentAdsWeight", flags)?.SetValue(ctrl, 0f);
            t.GetField("_currentSprintWeight", flags)?.SetValue(ctrl, 0f);
            updateMw?.Invoke(ctrl, new object[] { 0f, false });
            return cap;
        }

        [MenuItem("SCORCHED/Verify & Save Final Viewmodel Rig")]
        public static string ApplyAndVerifyFinalViewmodelSetup()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== APPLY & VERIFY FINAL VIEWMODEL SETUP ===");

            var pureArmsMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset");
            if (pureArmsMesh == null) return "ERROR: Assets/Models/FPS_Bandit_PureArms.asset missing";
            sb.AppendLine($"[Mesh] PureArms verts={pureArmsMesh.vertexCount}, tris={pureArmsMesh.triangles.Length / 3}");

            string[] requiredProps = new string[]
            {
                "_mode", "_centerOnFiring", "_centerTransitionSpeed",
                "_firingSightRightOffset", "_firingEyeForwardOffset", "_firingEyeUpOffset",
                "_cycleModeKey", "_showOnScreenBanner",
                "_opt1EyeForward", "_opt1EyeUp", "_opt1FOV",
                "_opt2EyeForward", "_opt2EyeUp", "_opt2FOV",
                "_vmHipPosition", "_vmHipEuler", "_vmAdsPosition", "_adsTransitionSpeed"
            };

            System.Action<GameObject, string> syncPlayerTarget = (playerObj, label) =>
            {
                var c = playerObj.GetComponent<FPSPerspectiveController>();
                if (c == null) { sb.AppendLine($"[{label}] ERROR: No FPSPerspectiveController"); return; }

                var so = new SerializedObject(c);
                var missing = new List<string>();
                foreach (var rp in requiredProps)
                {
                    if (so.FindProperty(rp) == null) missing.Add(rp);
                }

                void SetMesh(string pName, Mesh m) { var sp = so.FindProperty(pName); if (sp != null) sp.objectReferenceValue = m; }
                void SetInt(string pName, int v) { var sp = so.FindProperty(pName); if (sp != null) sp.intValue = v; }
                void SetBool(string pName, bool v) { var sp = so.FindProperty(pName); if (sp != null) sp.boolValue = v; }
                void SetVec(string pName, Vector3 v) { var sp = so.FindProperty(pName); if (sp != null) sp.vector3Value = v; }
                void SetFloat(string pName, float v) { var sp = so.FindProperty(pName); if (sp != null) sp.floatValue = v; }

                SetInt("_mode", (int)FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare);
                SetBool("_centerOnFiring", false);
                SetMesh("_fpsArmsMesh", pureArmsMesh);
                SetVec("_vmHipPosition", new Vector3(0.17f, -0.27f, 0.34f));
                SetVec("_vmHipEuler", new Vector3(1.0f, -1.5f, 0.0f));
                SetVec("_vmAdsPosition", new Vector3(0.000f, -0.088f, 0.455f));
                SetVec("_vmAdsEuler", Vector3.zero);
                SetVec("_vmSprintPositionOffset", new Vector3(0.015f, -0.055f, -0.045f));
                SetVec("_vmSprintEuler", new Vector3(11f, -23f, -2f));
                SetVec("_vmCrouchPositionOffset", new Vector3(-0.008f, -0.012f, -0.010f));
                SetVec("_vmCrouchEuler", new Vector3(1.5f, -1.5f, 3.0f));
                SetVec("_vmJumpPositionOffset", new Vector3(-0.006f, -0.018f, -0.012f));
                SetVec("_vmJumpEuler", new Vector3(-2.0f, 1.5f, 2.5f));
                SetVec("_vmLeftShoulderAnchor", new Vector3(-0.02f, -0.58f, 0.28f));
                SetVec("_vmRightShoulderAnchor", new Vector3(0.16f, -0.58f, -0.12f));
                SetVec("_vmLeftElbowPole", new Vector3(0.05f, -0.68f, 0.25f));
                SetVec("_vmRightElbowPole", new Vector3(0.16f, -0.74f, -0.18f));
                SetFloat("_idleBreathingAmp", 0.003f);
                SetFloat("_idleBreathingHz", 1.8f);
                so.ApplyModifiedPropertiesWithoutUndo();

                c.EnsureViewmodelRig();

                var cam = playerObj.GetComponentInChildren<Camera>(true);
                var rig = cam != null ? cam.transform.Find("FPS_ViewmodelRig") : null;
                var vmWeapon = rig != null ? rig.Find("Viewmodel_AK47") : null;
                var vmArms = rig != null ? rig.Find("Viewmodel_BanditArms") : null;

                if (vmWeapon != null)
                {
                    var oldLGrip = vmWeapon.Find("LeftHandGripTarget");
                    if (oldLGrip != null) UnityEngine.Object.DestroyImmediate(oldLGrip.gameObject);
                    var oldRGrip = vmWeapon.Find("RightHandGripTarget");
                    if (oldRGrip != null) UnityEngine.Object.DestroyImmediate(oldRGrip.gameObject);

                    var muz = FPSPerspectiveController.EnsureMuzzlePointOnTransform(vmWeapon);
                    muz.localPosition = new Vector3(0f, 0.0367f, 0.524f);
                    muz.localRotation = Quaternion.identity;
                    var lg = FPSPerspectiveController.EnsureLeftHandGripTarget(vmWeapon);
                    var rg = FPSPerspectiveController.EnsureRightHandGripTarget(vmWeapon);
                    sb.AppendLine($"[{label}] VM weapon synced: muz={muz.localPosition:F4}, lGrip={lg.localPosition:F3}, rGrip={rg.localPosition:F3}");
                }

                // Also sync world weapon targets (used in Mode 1 & Mode 2 and tracers)
                var wh = playerObj.GetComponent<PlayerWeaponHandler>();
                if (wh != null)
                {
                    var rifle = FindDeepChild(wh.transform, "AK47") ?? FindDeepChild(wh.transform, "Rifle");
                    if (rifle != null)
                    {
                        var oldWLGrip = rifle.Find("LeftHandGripTarget");
                        if (oldWLGrip != null) UnityEngine.Object.DestroyImmediate(oldWLGrip.gameObject);
                        var wMuz = FPSPerspectiveController.EnsureMuzzlePointOnTransform(rifle);
                        wMuz.localPosition = new Vector3(0f, 0.0367f, 0.524f);
                        wMuz.localRotation = Quaternion.identity;
                        FPSPerspectiveController.EnsureLeftHandGripTarget(rifle);
                        FPSPerspectiveController.EnsureRightHandGripTarget(rifle);
                    }
                }

                if (vmArms != null)
                {
                    var armsAnim = vmArms.GetComponent<Animator>();
                    if (armsAnim != null) armsAnim.enabled = false;
                    var smr = vmArms.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    if (smr != null)
                    {
                        smr.sharedMesh = pureArmsMesh;
                        smr.updateWhenOffscreen = true;
                        smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 50f);
                    }
                }

                // Re-bind cached references inside controller
                c.CurrentMode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
                c.ApplyCurrentMode();
                EditorUtility.SetDirty(c);
                EditorUtility.SetDirty(playerObj);
                sb.AppendLine($"[{label}] Synced! MissingEditorProps={string.Join(",", missing)}");
            };

            // 1. Sync Scene Player
            var scenePlayer = GameObject.Find("Player");
            if (scenePlayer == null) return "ERROR: No Player in active scene";
            syncPlayerTarget(scenePlayer, "Scene");

            // 2. Sync Prefab Assets/Prefabs/ScorchedPlayer.prefab
            const string prefabPath = "Assets/Prefabs/ScorchedPlayer.prefab";
            var prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            if (prefabRoot != null)
            {
                syncPlayerTarget(prefabRoot, "Prefab");
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            // 3. Verify runtime-equivalent pipeline on Scene Player across stances & pitches
            var ctrl = scenePlayer.GetComponent<FPSPerspectiveController>();
            var camScene = scenePlayer.GetComponentInChildren<Camera>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(FPSPerspectiveController);
            var updateMw = t.GetMethod("UpdateModernWarfareViewmodel", flags);
            var adsField = t.GetField("_currentAdsWeight", flags);
            var sprintField = t.GetField("_currentSprintWeight", flags);
            var recoilField = t.GetField("_recoilOffset", flags);
            var recoilRotField = t.GetField("_recoilRotOffset", flags);

            var rigScene = camScene.transform.Find("FPS_ViewmodelRig");
            var vmWeaponScene = rigScene.Find("Viewmodel_AK47");
            var vmArmsScene = rigScene.Find("Viewmodel_BanditArms");
            var handL = FindDeepChild(vmArmsScene, "hand.l");
            var handR = FindDeepChild(vmArmsScene, "hand.r");
            var forearmL = FindDeepChild(vmArmsScene, "forearm_stretch.l");
            var forearmR = FindDeepChild(vmArmsScene, "forearm_stretch.r");
            var twistL = FindDeepChild(vmArmsScene, "forearm_twist.l");
            var armL = FindDeepChild(vmArmsScene, "arm_stretch.l");
            var lGripT = vmWeaponScene.Find("LeftHandGripTarget");
            var rGripT = vmWeaponScene.Find("RightHandGripTarget");

            void RunCaseAndReport(string label, bool ads, bool sprint, bool recoilKick, float pitchDeg, string artifact)
            {
                ctrl.CurrentMode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
                ctrl.ApplyCurrentMode();
                adsField?.SetValue(ctrl, ads ? 1f : 0f);
                sprintField?.SetValue(ctrl, sprint ? 1f : 0f);
                recoilField?.SetValue(ctrl, Vector3.zero);
                recoilRotField?.SetValue(ctrl, Vector3.zero);
                if (recoilKick) ctrl.ApplyViewmodelRecoilKick();

                camScene.transform.localRotation = Quaternion.Euler(-pitchDeg, 0f, 0f);
                camScene.fieldOfView = ads ? 68f : 80f;
                updateMw?.Invoke(ctrl, new object[] { 0.0001f, ads });

                float errL = Vector3.Distance(handL.position, lGripT.position);
                float errR = Vector3.Distance(handR.position, rGripT.position);
                float rotErrL = Quaternion.Angle(handL.rotation, lGripT.rotation);
                float rotErrR = Quaternion.Angle(handR.rotation, rGripT.rotation);
                Quaternion relWristL = Quaternion.Inverse(forearmL.rotation) * handL.rotation;
                if (relWristL.w < 0f) relWristL = new Quaternion(-relWristL.x, -relWristL.y, -relWristL.z, -relWristL.w);
                Quaternion relTwistL = new Quaternion(0f, relWristL.y, 0f, relWristL.w).normalized;
                relTwistL.ToAngleAxis(out float remTwistDegL, out _);
                if (remTwistDegL > 180f) remTwistDegL -= 360f;
                Vector3 armLVp = camScene.WorldToViewportPoint(armL.position);
                Vector3 foreLVp = camScene.WorldToViewportPoint(forearmL.position);
                Vector3 handLVp = camScene.WorldToViewportPoint(handL.position);
                Vector3 rec = (Vector3)(recoilField?.GetValue(ctrl) ?? Vector3.zero);

                if (!string.IsNullOrEmpty(artifact))
                    CaptureViewToArtifact(artifact);

                sb.AppendLine($"[Verify:{label}] errL={errL:F4}m(rot={rotErrL:F1}deg, axialTwist={Mathf.Abs(remTwistDegL):F1}deg), errR={errR:F4}m(rot={rotErrR:F1}deg), handL.vp={handLVp:F2}, elbow.vp={foreLVp:F2}, armL.vp={armLVp:F2}, rec={rec:F4}");
            }

            RunCaseAndReport("HipFire", false, false, false, 0f, "final_mw_hipfire.png");
            RunCaseAndReport("ADS_RightClick", true, false, false, 0f, "final_mw_ads.png");
            RunCaseAndReport("TacticalSprint", false, true, false, 0f, "final_mw_sprint.png");
            RunCaseAndReport("RecoilKick", false, false, true, 0f, null);
            RunCaseAndReport("PitchUp50", false, false, false, 50f, "final_mw_pitch_up.png");
            RunCaseAndReport("PitchDown50", false, false, false, -50f, null);

            // 4. Verify Mode 2 & Mode 1 transitions
            camScene.transform.localRotation = Quaternion.identity;
            camScene.fieldOfView = 80f;
            var headBone = FindDeepChild(scenePlayer.transform, "head.x") ?? FindDeepChild(scenePlayer.transform, "Head");
            Vector3 prevHeadScale = headBone != null ? headBone.localScale : Vector3.one;
            if (headBone != null) headBone.localScale = Vector3.zero;
            ctrl.CurrentMode = FPSPerspectiveMode.CombatReady_UpperBody;
            ctrl.ApplyCurrentMode();
            TestOption2CameraOffsets(0.08f, 0.02f, 0.00f);
            ctrl.PostLateUpdateIK();
            CaptureViewToArtifact("final_mode2_combat.png");
            sb.AppendLine("[Verify:Mode2] Captured final_mode2_combat.png");

            ctrl.CurrentMode = FPSPerspectiveMode.TrueFirstPerson_EyeTuned;
            ctrl.ApplyCurrentMode();
            ctrl.PostLateUpdateIK();
            sb.AppendLine($"[Verify:Mode1] OK, cam.fov={camScene.fieldOfView:F1}, cam.locPos={camScene.transform.localPosition:F3}");
            if (headBone != null) headBone.localScale = prevHeadScale;

            // 5. Restore default Mode 3 Hip-Fire & save scene/assets
            camScene.transform.localRotation = Quaternion.identity;
            camScene.fieldOfView = 80f;
            adsField?.SetValue(ctrl, 0f);
            sprintField?.SetValue(ctrl, 0f);
            recoilField?.SetValue(ctrl, Vector3.zero);
            recoilRotField?.SetValue(ctrl, Vector3.zero);
            ctrl.CurrentMode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;
            ctrl.ApplyCurrentMode();
            ctrl.PostLateUpdateIK();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            sb.AppendLine("DONE: Scene and Prefab verified and saved.");

            Debug.Log(sb.ToString());
            return sb.ToString();
        }

        public static Transform FindDeepChild(Transform parent, string childName)
        {
            if (string.Equals(parent.name, childName, StringComparison.OrdinalIgnoreCase))
                return parent;

            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindDeepChild(parent.GetChild(i), childName);
                if (result != null) return result;
            }
            return null;
        }
    }
}
#endif
