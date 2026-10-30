#if UNITY_EDITOR
using System;
using System.Linq;

using Unity.Netcode;
using UnityEditor;
using UnityEngine;

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

