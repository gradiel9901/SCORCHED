using Unity.Netcode;
using UnityEngine;

namespace Scorched.Networking
{
    /// <summary>
    /// High-performance, lightweight IMGUI debug HUD for rapid multiplayer testing.
    /// Displays active role, live RTT/ping, connection limits, and match progress.
    /// Automatically minimizes into an unobtrusive status pill once connected to prevent
    /// obscuring gameplay viewports.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkDebugBootstrapUI : MonoBehaviour
    {
        [Header("HUD Configuration")]
        [SerializeField] private bool _enableDebugUI = true;
        [SerializeField] private bool _autoMinimizeOnConnect = true;
        [SerializeField] private KeyCode _toggleVisibilityKey = KeyCode.BackQuote; // Tilde (~) key

        [Header("Default Connection Fields")]
        [SerializeField] private string _inputIp = "127.0.0.1";
        [SerializeField] private string _inputPort = "7777";

        private NetworkManager _networkManager;
        private UnityTransportOptimizer _transportOptimizer;
        private NetworkConnectionHandler _connectionHandler;

        private bool _isMinimized = false;
        private bool _hasConnectedBefore = false;
        private string _statusMessage = "";

        private GUIStyle _boxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _buttonStyle;
        private bool _stylesInitialized = false;

        private void Start()
        {
            _networkManager = NetworkManager.Singleton;
            if (_networkManager != null)
            {
                _transportOptimizer = _networkManager.GetComponent<UnityTransportOptimizer>();
                _connectionHandler = _networkManager.GetComponent<NetworkConnectionHandler>();
            }

            if (_transportOptimizer != null)
            {
                _inputIp = _transportOptimizer.ServerIp;
                _inputPort = _transportOptimizer.ServerPort.ToString();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleVisibilityKey))
            {
                _enableDebugUI = !_enableDebugUI;
            }

            // Auto-minimize when transitioning from offline to online
            if (_autoMinimizeOnConnect && _networkManager != null && _networkManager.IsListening)
            {
                if (!_hasConnectedBefore)
                {
                    _hasConnectedBefore = true;
                    _isMinimized = true;
                }
            }
            else if (_networkManager != null && !_networkManager.IsListening)
            {
                _hasConnectedBefore = false;
            }
        }

        private void OnGUI()
        {
            if (!_enableDebugUI)
            {
                return;
            }

            EnsureStyles();

            bool isOnline = _networkManager != null && _networkManager.IsListening;

            if (isOnline && _isMinimized)
            {
                DrawMinimizedPill();
            }
            else
            {
                DrawExpandedPanel(isOnline);
            }
        }

        private void EnsureStyles()
        {
            if (_stylesInitialized)
            {
                return;
            }

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10)
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _headerStyle.normal.textColor = new Color(0.95f, 0.65f, 0.1f); // Scorched Amber

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11
            };
            _labelStyle.normal.textColor = Color.white;

            _statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Italic
            };
            _statusStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };

            _stylesInitialized = true;
        }

        /// <summary>
        /// Renders an ultra-compact status pill during active gameplay.
        /// </summary>
        private void DrawMinimizedPill()
        {
            string role = GetCurrentRoleString();
            ulong ping = GetPing();
            int currentPlayers = _connectionHandler != null ? _connectionHandler.ConnectedPlayerCount : (_networkManager != null ? _networkManager.ConnectedClientsIds.Count : 1);
            int maxPlayers = _connectionHandler != null ? _connectionHandler.MaxPlayers : 8;

            GUILayout.BeginArea(new Rect(10, 10, 360, 36), _boxStyle);
            GUILayout.BeginHorizontal();

            GUILayout.Label($"<b>{role}</b> | RTT: <b>{ping}ms</b> | Players: <b>{currentPlayers}/{maxPlayers}</b>", _labelStyle);

            if (GUILayout.Button("Expand", _buttonStyle, GUILayout.Width(60), GUILayout.Height(20)))
            {
                _isMinimized = false;
            }

            if (GUILayout.Button("Quit", _buttonStyle, GUILayout.Width(45), GUILayout.Height(20)))
            {
                Disconnect();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Renders the full interactive panel with IP/Port controls, diagnostic metrics, and connection triggers.
        /// </summary>
        private void DrawExpandedPanel(bool isOnline)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, isOnline ? 240 : 280), _boxStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("SCORCHED // NETWORK BOOTSTRAP", _headerStyle);
            if (isOnline && GUILayout.Button("-", _buttonStyle, GUILayout.Width(24), GUILayout.Height(20)))
            {
                _isMinimized = true;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Network telemetry
            string role = GetCurrentRoleString();
            ulong ping = GetPing();
            int currentPlayers = _connectionHandler != null ? _connectionHandler.ConnectedPlayerCount : (_networkManager != null ? _networkManager.ConnectedClientsIds.Count : 0);
            int maxPlayers = _connectionHandler != null ? _connectionHandler.MaxPlayers : 8;

            GUILayout.Label($"State: <b>{role}</b>", _labelStyle);
            if (isOnline)
            {
                GUILayout.Label($"Ping (RTT): <b>{ping} ms</b>", _labelStyle);
                GUILayout.Label($"Players: <b>{currentPlayers} / {maxPlayers}</b> (4v4)", _labelStyle);

                if (_connectionHandler != null)
                {
                    bool matchInProgress = _connectionHandler.IsMatchInProgress;
                    GUILayout.Label($"Match Status: <b>{(matchInProgress ? "In Combat (Locked)" : "Lobby (Open)")}</b>", _labelStyle);

                    if (_networkManager != null && _networkManager.IsServer)
                    {
                        if (GUILayout.Button(matchInProgress ? "Open Lobby (Allow Joins)" : "Lock Match (Start Combat)", _buttonStyle, GUILayout.Height(24)))
                        {
                            _connectionHandler.IsMatchInProgress = !_connectionHandler.IsMatchInProgress;
                        }
                    }
                }

                GUILayout.Space(6);
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Disconnect Session", _buttonStyle, GUILayout.Height(26)))
                {
                    Disconnect();
                }
                GUI.backgroundColor = Color.white;
            }
            else
            {
                // Offline configuration
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", _labelStyle, GUILayout.Width(35));
                _inputIp = GUILayout.TextField(_inputIp);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Port:", _labelStyle, GUILayout.Width(35));
                _inputPort = GUILayout.TextField(_inputPort);
                GUILayout.EndHorizontal();

                GUILayout.Space(6);

                GUI.backgroundColor = new Color(0.2f, 0.8f, 0.4f);
                if (GUILayout.Button("Start Host (4v4)", _buttonStyle, GUILayout.Height(26)))
                {
                    ApplyConnectionData();
                    if (_networkManager != null)
                    {
                        _statusMessage = "Starting Host...";
                        _networkManager.StartHost();
                    }
                }

                GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);
                if (GUILayout.Button("Start Client", _buttonStyle, GUILayout.Height(26)))
                {
                    ApplyConnectionData();
                    if (_networkManager != null)
                    {
                        _statusMessage = "Connecting to Server...";
                        _networkManager.StartClient();
                    }
                }

                GUI.backgroundColor = new Color(1f, 0.6f, 0.2f);
                if (GUILayout.Button("Start Dedicated Server", _buttonStyle, GUILayout.Height(24)))
                {
                    ApplyConnectionData();
                    if (_networkManager != null)
                    {
                        _statusMessage = "Starting Dedicated Server...";
                        _networkManager.StartServer();
                    }
                }
                GUI.backgroundColor = Color.white;
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Space(4);
                GUILayout.Label(_statusMessage, _statusStyle);
            }

            GUILayout.EndArea();
        }

        private void ApplyConnectionData()
        {
            if (_transportOptimizer == null)
            {
                return;
            }

            if (ushort.TryParse(_inputPort, out ushort port))
            {
                _transportOptimizer.SetConnectionData(_inputIp, port);
            }
            else
            {
                _transportOptimizer.SetConnectionData(_inputIp, 7777);
            }
        }

        private void Disconnect()
        {
            if (_connectionHandler != null)
            {
                _connectionHandler.TerminateSession();
            }
            else if (_networkManager != null && _networkManager.IsListening)
            {
                _networkManager.Shutdown();
            }

            _statusMessage = "Disconnected.";
            _isMinimized = false;
        }

        private string GetCurrentRoleString()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                return "Offline";
            }

            if (_networkManager.IsHost)
            {
                return "Host (Listen Server)";
            }

            if (_networkManager.IsServer)
            {
                return "Dedicated Server";
            }

            if (_networkManager.IsClient)
            {
                return "Client";
            }

            return "Unknown";
        }

        private ulong GetPing()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                return 0;
            }

            if (_transportOptimizer != null)
            {
                return _transportOptimizer.GetCurrentRtt();
            }

            return 0;
        }
    }
}

