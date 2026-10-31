using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Scorched.Networking
{
    /// <summary>
    /// Manages player connection lifecycle, security verification, and session capacity.
    /// Implements connection approval to enforce the 8-player (4v4) hard limit and reject
    /// late joins once active combat has begun. Handles graceful peer disconnects and
    /// session shutdowns without memory leaks or dangling state.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager))]
    public class NetworkConnectionHandler : MonoBehaviour
    {
        private const int MaxAllowedPlayers = 8; // 4v4 competitive roster limit

        [Header("Match Capacity & Rules")]
        [Tooltip("Maximum concurrent players allowed in the match (default 8 for 4v4).")]
        [Range(2, MaxAllowedPlayers)]
        [SerializeField] private int _maxPlayers = MaxAllowedPlayers;

        [Tooltip("Whether to automatically spawn the player prefab upon connection approval.")]
        [SerializeField] private bool _autoSpawnPlayerPrefab = true;

        [Header("State")]
        [Tooltip("When true, new connections will be rejected because the match is in active combat.")]
        [SerializeField] private bool _isMatchInProgress = false;

        private NetworkManager _networkManager;
        private readonly HashSet<ulong> _connectedClients = new HashSet<ulong>();

        /// <summary>
        /// Fires when a player successfully connects and joins the session.
        /// </summary>
        public event Action<ulong> OnPlayerJoined;

        /// <summary>
        /// Fires when a player disconnects from the session.
        /// </summary>
        public event Action<ulong> OnPlayerLeft;

        /// <summary>
        /// Fires when the local client is disconnected, providing the server's rejection/disconnect reason.
        /// </summary>
        public event Action<string> OnLocalClientDisconnectedWithReason;

        /// <summary>
        /// Fires when the current network session is terminated or reset.
        /// </summary>
        public event Action OnSessionTerminated;

        /// <summary>
        /// Hard player capacity for the 4v4 match.
        /// </summary>
        public int MaxPlayers => _maxPlayers;

        /// <summary>
        /// Number of currently connected and approved players.
        /// </summary>
        public int ConnectedPlayerCount => _connectedClients.Count;

        /// <summary>
        /// Indicates if the match is currently in active combat (locks out late joiners).
        /// </summary>
        public bool IsMatchInProgress
        {
            get => _isMatchInProgress;
            set
            {
                if (_isMatchInProgress != value)
                {
                    _isMatchInProgress = value;
                    Debug.Log($"[ConnectionHandler] Match In-Progress state changed to: {_isMatchInProgress}. Late joining: {(!_isMatchInProgress ? "Allowed" : "Blocked")}.");
                }
            }
        }

        private void Awake()
        {
            _networkManager = GetComponent<NetworkManager>();
            if (_networkManager == null)
            {
                Debug.LogError("[ConnectionHandler] NetworkManager component is missing!", this);
                return;
            }

            ConfigureConnectionApproval();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            _networkManager = GetComponent<NetworkManager>();
            if (_networkManager != null && _networkManager.NetworkConfig != null)
            {
                _networkManager.NetworkConfig.ConnectionApproval = true;
            }
        }
#endif

        private void OnEnable()
        {
            RegisterCallbacks();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
        }

        private void OnDestroy()
        {
            TerminateSession();
        }

        private void ConfigureConnectionApproval()
        {
            if (_networkManager == null)
            {
                return;
            }

            _networkManager.NetworkConfig.ConnectionApproval = true;
            _networkManager.ConnectionApprovalCallback = ConnectionApprovalCheck;
            Debug.Log($"[ConnectionHandler] Connection approval enabled. Max player capacity: {_maxPlayers}.");
        }

        private void RegisterCallbacks()
        {
            if (_networkManager == null)
            {
                return;
            }

            _networkManager.OnClientConnectedCallback += HandleClientConnected;
            _networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
            _networkManager.OnServerStarted += HandleServerStarted;
            _networkManager.OnServerStopped += HandleServerStopped;
        }

        private void UnregisterCallbacks()
        {
            if (_networkManager == null)
            {
                return;
            }

            _networkManager.OnClientConnectedCallback -= HandleClientConnected;
            _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
            _networkManager.OnServerStarted -= HandleServerStarted;
            _networkManager.OnServerStopped -= HandleServerStopped;

            if (_networkManager.ConnectionApprovalCallback == ConnectionApprovalCheck)
            {
                _networkManager.ConnectionApprovalCallback = null;
            }
        }

        /// <summary>
        /// Evaluates incoming connection requests on the server/host.
        /// Enforces max player limits and locks late joining during active gameplay.
        /// </summary>
        private void ConnectionApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ulong clientId = request.ClientNetworkId;

            // Host player is always approved unconditionally
            if (clientId == NetworkManager.ServerClientId)
            {
                ApproveConnection(response, "Host player approved.");
                return;
            }

            // Reject if maximum player capacity is reached
            if (_connectedClients.Count >= _maxPlayers)
            {
                string reason = $"Session is full ({_connectedClients.Count}/{_maxPlayers} players occupied).";
                RejectConnection(response, reason, clientId);
                return;
            }

            // Reject if the match is already underway
            if (_isMatchInProgress)
            {
                string reason = "Match is currently in active combat. Late joining is locked.";
                RejectConnection(response, reason, clientId);
                return;
            }

            // Connection validated
            ApproveConnection(response, $"Client {clientId} approved.");
        }

        private void ApproveConnection(NetworkManager.ConnectionApprovalResponse response, string logMessage)
        {
            response.Approved = true;
            response.CreatePlayerObject = _autoSpawnPlayerPrefab;
            response.PlayerPrefabHash = null; // Use default registered player prefab
            response.Position = null;         // Let spawn system dictate position
            response.Rotation = null;
            response.Pending = false;

            Debug.Log($"[ConnectionHandler] {logMessage}");
        }

        private void RejectConnection(NetworkManager.ConnectionApprovalResponse response, string reason, ulong clientId)
        {
            response.Approved = false;
            response.Reason = reason;
            response.CreatePlayerObject = false;
            response.Pending = false;

            Debug.LogWarning($"[ConnectionHandler] Rejected connection for client {clientId}. Reason: {reason}");
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (!_connectedClients.Contains(clientId))
            {
                _connectedClients.Add(clientId);
            }

            Debug.Log($"[ConnectionHandler] Client {clientId} successfully joined session. Occupied slots: {_connectedClients.Count}/{_maxPlayers}.");
            OnPlayerJoined?.Invoke(clientId);
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            bool wasTracked = _connectedClients.Remove(clientId);

            // Handle local client disconnection
            if (_networkManager != null && clientId == _networkManager.LocalClientId)
            {
                string disconnectReason = _networkManager.DisconnectReason;
                if (string.IsNullOrWhiteSpace(disconnectReason))
                {
                    disconnectReason = "Disconnected from server (Host closed connection or timed out).";
                }

                Debug.LogWarning($"[ConnectionHandler] Local client disconnected. Reason: {disconnectReason}");
                _connectedClients.Clear();
                OnLocalClientDisconnectedWithReason?.Invoke(disconnectReason);
                return;
            }

            // Server-side tracking for remote peer disconnection
            if (wasTracked)
            {
                Debug.Log($"[ConnectionHandler] Remote client {clientId} disconnected. Remaining players: {_connectedClients.Count}/{_maxPlayers}.");
                OnPlayerLeft?.Invoke(clientId);
            }
        }

        private void HandleServerStarted()
        {
            _connectedClients.Clear();
            _isMatchInProgress = false;
            Debug.Log("[ConnectionHandler] Server started. Lobby open for incoming connections.");
        }

        private void HandleServerStopped(bool wasHost)
        {
            Debug.Log($"[ConnectionHandler] Server stopped (WasHost: {wasHost}). Cleaning up connection state.");
            _connectedClients.Clear();
            _isMatchInProgress = false;
            OnSessionTerminated?.Invoke();
        }

        /// <summary>
        /// Gracefully shuts down the current network session and clears all active player tracking.
        /// </summary>
        public void TerminateSession()
        {
            if (_networkManager != null && _networkManager.IsListening)
            {
                Debug.Log("[ConnectionHandler] Terminating network session...");
                _networkManager.Shutdown();
            }

            _connectedClients.Clear();
            _isMatchInProgress = false;
            OnSessionTerminated?.Invoke();
        }
    }
}

