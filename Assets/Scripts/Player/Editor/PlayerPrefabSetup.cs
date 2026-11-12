#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Netcode;
using Scorched.Player;

namespace Scorched.Editor
{
    /// <summary>
    /// Utility to ensure that a standalone ScorchedPlayer prefab exists in Assets/Prefabs/
    /// and is registered as the default PlayerPrefab on NetworkManager in Level 0 - Test Environment.unity.
    /// This enables Netcode for GameObjects to automatically instantiate and spawn
    /// player avatars when multiple clients join a hosted match.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayerPrefabSetup
    {
        private const string PrefabPath = "Assets/Prefabs/ScorchedPlayer.prefab";
        private const string MainScenePath = "Assets/Scenes/Level 0 - Test Environment.unity";

        static PlayerPrefabSetup()
        {
            EditorApplication.delayCall += EnsurePlayerPrefab;
        }

        [MenuItem("Scorched/Setup/Ensure Player Prefab & NetworkManager", false, 100)]
        public static void EnsurePlayerPrefab()
        {
            // 1. Verify or create the prefab
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (playerPrefab == null)
            {
                var scenePlayer = Object.FindAnyObjectByType<PlayerMovement>();
                if (scenePlayer != null)
                {
                    if (!Directory.Exists("Assets/Prefabs"))
                    {
                        Directory.CreateDirectory("Assets/Prefabs");
                    }

                    playerPrefab = PrefabUtility.SaveAsPrefabAssetAndConnect(scenePlayer.gameObject, PrefabPath, InteractionMode.AutomatedAction);
                    Debug.Log($"[PlayerPrefabSetup] Created '{PrefabPath}' from scene player.");
                }
            }

            // 2. Ensure NetworkManager registers the PlayerPrefab
            var netManager = Object.FindAnyObjectByType<NetworkManager>();
            if (netManager != null && playerPrefab != null)
            {
                if (netManager.NetworkConfig != null && netManager.NetworkConfig.PlayerPrefab == null)
                {
                    netManager.NetworkConfig.PlayerPrefab = playerPrefab;
                    EditorUtility.SetDirty(netManager);
                    var activeScene = EditorSceneManager.GetActiveScene();
                    if (activeScene.isDirty)
                    {
                        EditorSceneManager.SaveScene(activeScene);
                    }
                    Debug.Log($"[PlayerPrefabSetup] Assigned '{PrefabPath}' as default PlayerPrefab on NetworkManager.");
                }
            }
        }
    }
}
#endif

