#if UNITY_EDITOR
using System;
using System.Linq;

using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scorched.Networking
{
    /// <summary>
    /// Editor-only integration for Unity 6 Multiplayer Play Mode (MPPM).
    /// Detects player tags ("Host" or "Client") on virtual players and automatically starts
    /// the appropriate network role upon entering Play Mode.
    /// </summary>
    [InitializeOnLoad]
    public static class MPPMAutoConnector
    {
        private const double ConnectTimeoutSeconds = 5.0;
        private static double s_PlayStartTime;
        private static string s_TargetRole;

        static MPPMAutoConnector()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += EnsureAnimatorSpeedMultiplier;
            EditorApplication.delayCall += EnsureVirtualPlayerOnValidBuildScene;
        }

        private static void EnsureVirtualPlayerOnValidBuildScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            try
            {
                if (Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            // Sync assets in clone because -DisableDirectoryMonitor stops OS file watching
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            var enabledScene = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled && !string.IsNullOrEmpty(s.path));
            if (enabledScene != null && System.IO.File.Exists(enabledScene.path))
            {
                Scene active = SceneManager.GetActiveScene();
                Terrain terrain = UnityEngine.Object.FindAnyObjectByType<Terrain>();
                bool terrainNeedsReload = terrain == null || terrain.terrainData == null || terrain.terrainData.terrainLayers == null || terrain.terrainData.terrainLayers.Length == 0 || terrain.materialTemplate == null || terrain.materialTemplate.name != "TerrainLit";

                if (active.path != enabledScene.path || UnityEngine.Object.FindAnyObjectByType<NetworkManager>() == null || terrainNeedsReload)
                {
                    Debug.Log($"[MPPMAutoConnector] Restoring/Reloading MPPM virtual player scene to '{enabledScene.path}' (terrainNeedsReload={terrainNeedsReload}).");
                    EditorSceneManager.OpenScene(enabledScene.path, OpenSceneMode.Single);
                }
            }
        }

        private static void EnsureAnimatorSpeedMultiplier()
        {
            try
            {
                if (!Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor)
                {
                    return;
                }
            }
            catch
            {
                // Ignore if MPPM context unavailable
            }

            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/Animations/Player/01_Player.controller");
            if (controller == null)
            {
                return;
            }

            bool modified = false;

            if (!controller.parameters.Any(p => p.name == "MoveSpeedMultiplier"))
            {
                controller.AddParameter(new UnityEngine.AnimatorControllerParameter
                {
                    name = "MoveSpeedMultiplier",
                    type = UnityEngine.AnimatorControllerParameterType.Float,
                    defaultFloat = 1.0f
                });
                modified = true;
            }

            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var childState in layer.stateMachine.states)
                {
                    var s = childState.state;
                    if (s != null && (s.name == "Jog" || s.name == "Running" || s.name == "Crouched Run"))
                    {
                        if (!s.speedParameterActive || s.speedParameter != "MoveSpeedMultiplier")
                        {
                            s.speedParameterActive = true;
                            s.speedParameter = "MoveSpeedMultiplier";
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                Debug.Log("[MPPMAutoConnector] Configured 'MoveSpeedMultiplier' on 01_Player.controller for backward locomotion.");
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EvaluateMPPMTags();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.update -= WaitForNetworkManager;
                s_TargetRole = null;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += EnsureVirtualPlayerOnValidBuildScene;
            }
        }

        private static void EvaluateMPPMTags()
        {
            try
            {
                var tags = Unity.Multiplayer.PlayMode.CurrentPlayer.Tags;
                if (tags == null || !tags.Any())
                {
                    return;
                }

                if (tags.Any(t => string.Equals(t, "Host", StringComparison.OrdinalIgnoreCase)))
                {
                    s_TargetRole = "Host";
                }
                else if (tags.Any(t => string.Equals(t, "Client", StringComparison.OrdinalIgnoreCase)))
                {
                    s_TargetRole = "Client";
                }
                else if (tags.Any(t => string.Equals(t, "Server", StringComparison.OrdinalIgnoreCase)))
                {
                    s_TargetRole = "Server";
                }

                if (!string.IsNullOrEmpty(s_TargetRole))
                {
                    s_PlayStartTime = EditorApplication.timeSinceStartup;
                    EditorApplication.update += WaitForNetworkManager;
                    Debug.Log($"[MPPMAutoConnector] Detected MPPM tag for role '{s_TargetRole}'. Waiting for NetworkManager...");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MPPMAutoConnector] Failed to evaluate MPPM tags: {ex.Message}");
            }
        }

        private static void WaitForNetworkManager()
        {
            if (EditorApplication.timeSinceStartup - s_PlayStartTime > ConnectTimeoutSeconds)
            {
                Debug.LogWarning($"[MPPMAutoConnector] Timed out waiting for NetworkManager to initialize after {ConnectTimeoutSeconds}s.");
                EditorApplication.update -= WaitForNetworkManager;
                return;
            }

            var netManager = NetworkManager.Singleton;
            if (netManager == null)
            {
                return;
            }

            if (netManager.IsListening)
            {
                EditorApplication.update -= WaitForNetworkManager;
                return;
            }

            EditorApplication.update -= WaitForNetworkManager;

            switch (s_TargetRole)
            {
                case "Host":
                    Debug.Log("[MPPMAutoConnector] Auto-starting as Host...");
                    netManager.StartHost();
                    break;
                case "Client":
                    Debug.Log("[MPPMAutoConnector] Auto-starting as Client...");
                    netManager.StartClient();
                    break;
                case "Server":
                    Debug.Log("[MPPMAutoConnector] Auto-starting as Server...");
                    netManager.StartServer();
                    break;
            }
        }
    }
}
#endif

