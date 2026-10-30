using System;
using Unity.Netcode;
using UnityEngine;

namespace Scorched.Networking
{
    /// <summary>
    /// Central bootstrapper for the [NetworkManager] root GameObject.
    /// Enforces persistent singleton behavior, programmatically applies high-tick-rate
    /// network configurations for competitive FPS play, configures display frame pacing,
    /// and parses command-line launch arguments for headless servers and local automation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager))]
    public class NetworkManagerBootstrapper : MonoBehaviour
    {
        private static NetworkManagerBootstrapper s_Instance;

        [Header("Tick Rate & Simulation (FPS Target)")]
        [Tooltip("Network simulation tick rate. 64 ticks/sec for competitive FPS; 128 for LAN/ultra-low RTT.")]
        [SerializeField] private uint _networkTickRate = 64;

        [Tooltip("Ensure that client and server verify variable safety and matching tick rates.")]
        [SerializeField] private bool _ensureNetworkVariableSafety = true;

        [Header("Client Frame Pacing & Latency")]
        [Tooltip("Disable VSync to eliminate driver frame queue buffering and display latency.")]
        [SerializeField] private bool _disableVSync = true;

        [Tooltip("Target frame rate. Set to -1 for uncapped, or e.g. 144, 240, 360.")]
        [SerializeField] private int _targetFrameRate = -1;

        [Header("Command-Line Launch")]
        [Tooltip("Automatically parse command-line arguments (-mode, -port, -ip, -tickrate) on Start.")]
        [SerializeField] private bool _autoParseCommandLine = true;

        private NetworkManager _networkManager;
        private UnityTransportOptimizer _transportOptimizer;
        private NetworkConnectionHandler _connectionHandler;

        /// <summary>
        /// The active network tick rate configured on the NetworkManager.
        /// </summary>
        public uint NetworkTickRate => _networkTickRate;

        /// <summary>
        /// Target frame rate applied to Application.targetFrameRate.
        /// </summary>
        public int TargetFrameRate => _targetFrameRate;

        private void Awake()
        {
            // Enforce persistent root singleton across scene transitions
            if (s_Instance != null && s_Instance != this)
            {
                Debug.LogWarning($"[NetworkBootstrapper] Duplicate [{gameObject.name}] detected in scene '{gameObject.scene.name}'. Destroying duplicate instance.");
                Destroy(gameObject);
                return;
            }

            s_Instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeNetworkComponents();
            ConfigureFramePacing();
            ApplyNetworkConfiguration();
        }

        private void Start()
        {
            if (_autoParseCommandLine)
            {
                ProcessCommandLineArguments();
            }
        }

        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

        /// <summary>
        /// Resolves and verifies necessary component references on the GameObject.
        /// </summary>
        private void InitializeNetworkComponents()
        {
            _networkManager = GetComponent<NetworkManager>();
            if (_networkManager == null)
            {
                Debug.LogError("[NetworkBootstrapper] NetworkManager component is missing from GameObject!", this);
                return;
            }

            _transportOptimizer = GetComponent<UnityTransportOptimizer>();
            _connectionHandler = GetComponent<NetworkConnectionHandler>();
        }

        /// <summary>
        /// Optimizes frame pacing and eliminates driver queue delay for responsive input.
        /// </summary>
        private void ConfigureFramePacing()
        {
            if (_disableVSync)
            {
                QualitySettings.vSyncCount = 0;
            }

            Application.targetFrameRate = _targetFrameRate;
            Debug.Log($"[NetworkBootstrapper] Frame pacing configured. VSync: {(!_disableVSync ? "Enabled" : "Disabled")}, TargetFrameRate: {(_targetFrameRate == -1 ? "Uncapped (-1)" : _targetFrameRate.ToString())}");
        }

        /// <summary>
        /// Applies high-performance FPS simulation settings to NetworkManager.NetworkConfig.
        /// </summary>
        private void ApplyNetworkConfiguration()
        {
            if (_networkManager == null)
            {
                return;
            }

            var config = _networkManager.NetworkConfig;
            if (config == null)
            {
                Debug.LogError("[NetworkBootstrapper] NetworkConfig is null on NetworkManager!", this);
                return;
            }

            // Enforce 64 or 128 tick rate for precision hit registration
            config.TickRate = _networkTickRate;
            config.EnsureNetworkVariableLengthSafety = _ensureNetworkVariableSafety;

            Debug.Log($"[NetworkBootstrapper] NetworkManager initialized. Simulation TickRate: {config.TickRate} ticks/sec.");
        }

        /// <summary>
        /// Parses startup arguments for automated testing, dedicated servers, and multi-instance launching.
        /// Supported args:
        ///   -mode [host|client|server|dedicated]
        ///   -port [7777]
        ///   -ip [127.0.0.1]
        ///   -tickrate [64|128]
        /// </summary>
        private void ProcessCommandLineArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0)
            {
                return;
            }

            string mode = null;
            string ip = null;
            ushort? port = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();

                if (arg == "-mode" && i + 1 < args.Length)
                {
                    mode = args[++i].ToLowerInvariant();
                }
                else if (arg == "-port" && i + 1 < args.Length)
                {
                    if (ushort.TryParse(args[++i], out ushort parsedPort))
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        Debug.LogWarning($"[NetworkBootstrapper] Invalid -port argument: '{args[i]}'");
                    }
                }
                else if (arg == "-ip" && i + 1 < args.Length)
                {
                    ip = args[++i];
                }
                else if (arg == "-tickrate" && i + 1 < args.Length)
                {
                    if (uint.TryParse(args[++i], out uint parsedRate))
                    {
                        _networkTickRate = parsedRate;
                        if (_networkManager != null && _networkManager.NetworkConfig != null)
                        {
                            _networkManager.NetworkConfig.TickRate = parsedRate;
                        }
                        Debug.Log($"[NetworkBootstrapper] Command-line tickrate applied: {parsedRate} ticks/sec.");
                    }
                }
            }

            // Update transport connection details if specified
            if (_transportOptimizer != null && (ip != null || port.HasValue))
            {
                string targetIp = ip ?? "127.0.0.1";
                ushort targetPort = port ?? 7777;
                _transportOptimizer.SetConnectionData(targetIp, targetPort);
            }

            // Execute network startup if -mode was supplied
            if (!string.IsNullOrEmpty(mode))
            {
                ExecuteLaunchMode(mode);
            }
        }

        private void ExecuteLaunchMode(string mode)
        {
            if (_networkManager == null)
            {
                return;
            }

            switch (mode)
            {
                case "server":
                case "dedicated":
                    Debug.Log("[NetworkBootstrapper] Launching as Dedicated Server via CLI...");
                    _networkManager.StartServer();
                    break;

                case "host":
                    Debug.Log("[NetworkBootstrapper] Launching as Host via CLI...");
                    _networkManager.StartHost();
                    break;

                case "client":
                    Debug.Log("[NetworkBootstrapper] Launching as Client via CLI...");
                    _networkManager.StartClient();
                    break;

                default:
                    Debug.LogWarning($"[NetworkBootstrapper] Unknown launch mode specified: '{mode}'. Expected: host, client, server, or dedicated.");
                    break;
            }
        }
    }
}

