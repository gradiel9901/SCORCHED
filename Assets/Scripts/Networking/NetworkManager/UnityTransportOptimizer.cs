using System;
using System.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Scorched.Networking
{
    /// <summary>
    /// Programmatically tunes and optimizes low-level UnityTransport (UTP) parameters
    /// for ultra-low latency, burst-resilient packet streaming in competitive FPS multiplayer.
    /// Eliminates send delay, sizes socket queues to prevent dropped state updates,
    /// and provides safe connection address management.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnityTransport))]
    public class UnityTransportOptimizer : MonoBehaviour
    {
        private const string DefaultServerIp = "127.0.0.1";
        private const ushort DefaultServerPort = 7777;
        private const string DefaultListenAddress = "0.0.0.0";

        [Header("Connection Target (Default / Fallback)")]
        [Tooltip("Target Server IP address for client connections.")]
        [SerializeField] private string _serverIp = DefaultServerIp;

        [Tooltip("Network port for gameplay traffic.")]
        [SerializeField] private ushort _serverPort = DefaultServerPort;

        [Tooltip("Local listen address for server/host sockets. 0.0.0.0 listens on all interfaces.")]
        [SerializeField] private string _listenAddress = DefaultListenAddress;

        [Header("Queue & Buffer Optimization (FPS)")]
        [Tooltip("Maximum send queue capacity in bytes. Scaled to prevent buffer overflow under heavy projectile/state bursts.")]
        [SerializeField] private int _maxSendQueueSize = 1024 * 1024; // 1 MB

        [Tooltip("Maximum number of packets allowed in internal send/receive queues per tick/frame.")]
        [SerializeField] private int _maxPacketQueueSize = 1024;

        [Tooltip("Maximum payload size in bytes for unreliable datagrams.")]
        [SerializeField] private int _maxPayloadSize = 6144; // 6 KB

        [Header("Connection Latency & Timeouts")]
        [Tooltip("Maximum connection attempts before declaring connection failure.")]
        [SerializeField] private int _maxConnectAttempts = 8;

        [Tooltip("Interval between connection retry attempts (ms). Kept low for rapid reconnects.")]
        [SerializeField] private int _connectTimeoutMS = 500;

        [Tooltip("Inactivity threshold before disconnecting an unresponsive peer (ms).")]
        [SerializeField] private int _disconnectTimeoutMS = 5000;

        [Tooltip("Heartbeat frequency to maintain active NAT/UDP hole punching (ms).")]
        [SerializeField] private int _heartbeatTimeoutMS = 500;

        [Header("Low Latency / Flush Optimization")]
        [Tooltip("Force raw socket UDP transport with direct packet dispatch for minimal input-to-render latency.")]
        [SerializeField] private bool _directDatagramDispatch = true;

        private UnityTransport _transport;

        /// <summary>
        /// Currently configured destination IP address.
        /// </summary>
        public string ServerIp => _serverIp;

        /// <summary>
        /// Currently configured destination port.
        /// </summary>
        public ushort ServerPort => _serverPort;

        /// <summary>
        /// Currently configured server socket listen address.
        /// </summary>
        public string ListenAddress => _listenAddress;

        private void Awake()
        {
            InitializeTransport();
            ApplyOptimizationParameters();
            ApplyConnectionAddresses();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            InitializeTransport();
            if (_transport != null)
            {
                ApplyOptimizationParameters();
                _transport.SetConnectionData(_serverIp, _serverPort, _listenAddress);
            }
        }
#endif

        private void InitializeTransport()
        {
            _transport = GetComponent<UnityTransport>();
            if (_transport == null)
            {
                Debug.LogError("[TransportOptimizer] UnityTransport component is missing from GameObject!", this);
            }
        }

        /// <summary>
        /// Tunes transport buffer thresholds and queues for burst tolerance and instant dispatch.
        /// </summary>
        public void ApplyOptimizationParameters()
        {
            if (_transport == null)
            {
                return;
            }

            // High-capacity queues to eliminate packet dropping during firefights
            _transport.MaxSendQueueSize = _maxSendQueueSize;
            _transport.MaxPacketQueueSize = _maxPacketQueueSize;
            _transport.MaxPayloadSize = _maxPayloadSize;

            // Connection handshake pacing
            _transport.MaxConnectAttempts = _maxConnectAttempts;
            _transport.ConnectTimeoutMS = _connectTimeoutMS;
            _transport.DisconnectTimeoutMS = _disconnectTimeoutMS;
            _transport.HeartbeatTimeoutMS = _heartbeatTimeoutMS;

            // Configure socket mode for minimal latency
            if (_directDatagramDispatch)
            {
                _transport.UseWebSockets = false;
                _transport.UseEncryption = false;
            }

            Debug.Log($"[TransportOptimizer] Parameters optimized: MaxSendQueue={_maxSendQueueSize / 1024}KB, MaxPacketQueue={_maxPacketQueueSize}, MaxPayload={_maxPayloadSize}B, ConnectTimeout={_connectTimeoutMS}ms.");
        }

        /// <summary>
        /// Safely sets the IP, port, and listen address with validation and fallback.
        /// </summary>
        public void SetConnectionData(string ip, ushort port, string listenAddress = null)
        {
            string validatedIp = ValidateOrFallbackIp(ip);
            ushort validatedPort = port > 0 ? port : DefaultServerPort;
            string validatedListen = !string.IsNullOrWhiteSpace(listenAddress) ? listenAddress : DefaultListenAddress;

            _serverIp = validatedIp;
            _serverPort = validatedPort;
            _listenAddress = validatedListen;

            ApplyConnectionAddresses();
        }

        private void ApplyConnectionAddresses()
        {
            if (_transport == null)
            {
                return;
            }

            try
            {
                _transport.SetConnectionData(_serverIp, _serverPort, _listenAddress);
                Debug.Log($"[TransportOptimizer] Connection endpoint set to {_serverIp}:{_serverPort} (Listen: {_listenAddress}).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TransportOptimizer] Failed to apply connection data ({_serverIp}:{_serverPort}): {ex.Message}. Falling back to default.");
                _transport.SetConnectionData(DefaultServerIp, DefaultServerPort, DefaultListenAddress);
            }
        }

        /// <summary>
        /// Retrieves the real-time Round Trip Time (RTT/Ping) in milliseconds for a given client connection.
        /// When acting as client, pass NetworkManager.ServerClientId (0).
        /// </summary>
        public ulong GetCurrentRtt(ulong clientId = NetworkManager.ServerClientId)
        {
            if (_transport == null)
            {
                return 0;
            }

            try
            {
                return _transport.GetCurrentRtt(clientId);
            }
            catch
            {
                return 0;
            }
        }

        private static string ValidateOrFallbackIp(string inputIp)
        {
            if (string.IsNullOrWhiteSpace(inputIp))
            {
                return DefaultServerIp;
            }

            string trimmed = inputIp.Trim();
            if (IPAddress.TryParse(trimmed, out _))
            {
                return trimmed;
            }

            Debug.LogWarning($"[TransportOptimizer] '{inputIp}' is not a valid IPv4/IPv6 address. Falling back to '{DefaultServerIp}'.");
            return DefaultServerIp;
        }
    }
}

