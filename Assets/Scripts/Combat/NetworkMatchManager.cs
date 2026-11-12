using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Scorched.Combat
{
    /// <summary>
    /// Match engagement game modes supported in SCORCHED.
    /// Default mode is Free For All (FFA).
    /// Host can optionally select Team Match (4v4 Alpha vs Bravo).
    /// </summary>
    public enum GameMode : byte
    {
        FreeForAll = 0,
        TeamMatch = 1
    }

    /// <summary>
    /// Current combat progression state.
    /// </summary>
    public enum MatchState : byte
    {
        WaitingForPlayers = 0,
        WaitingForEqualTeams = 1,
        CombatActive = 2
    }

    /// <summary>
    /// Authoritative server-side match and game mode controller.
    /// Manages match rules, team assignments (Alpha vs Bravo), equal-team enforcement,
    /// friendly-fire filtering, and synchronized combat lockout states via NGO CustomMessaging.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkMatchManager : MonoBehaviour
    {
        public const string MatchSyncMessageName = "Scorched_MatchSync";

        public static NetworkMatchManager Instance { get; private set; }

        [Header("Match Settings")]
        [SerializeField] private GameMode _defaultGameMode = GameMode.FreeForAll;

        // Current runtime match state
        [SerializeField] private GameMode _currentGameMode = GameMode.FreeForAll;
        [SerializeField] private MatchState _currentMatchState = MatchState.CombatActive;
        [SerializeField] private int _teamAlphaCount = 0;
        [SerializeField] private int _teamBravoCount = 0;
        [SerializeField] private bool _isCombatActive = true;
        [SerializeField] private int _localPlayerTeam = -1; // -1 = FFA, 0 = Alpha, 1 = Bravo

        // Server-authoritative player tracking: ClientId -> Team (0 = Alpha, 1 = Bravo, -1 = FFA)
        private readonly Dictionary<ulong, int> _playerTeams = new Dictionary<ulong, int>();

        public event Action<GameMode, MatchState> OnMatchStateChanged;

        public GameMode CurrentGameMode => _currentGameMode;
        public MatchState CurrentMatchState => _currentMatchState;
        public int TeamAlphaCount => _teamAlphaCount;
        public int TeamBravoCount => _teamBravoCount;
        public bool IsCombatActive => _isCombatActive;
        public int LocalPlayerTeam => _localPlayerTeam;

        public bool IsFreeForAll => _currentGameMode == GameMode.FreeForAll;
        public bool IsTeamMatch => _currentGameMode == GameMode.TeamMatch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            if (Instance == null && FindAnyObjectByType<NetworkMatchManager>() == null)
            {
                var go = new GameObject("NetworkMatchManager");
                go.AddComponent<NetworkMatchManager>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _currentGameMode = _defaultGameMode;
            // By default (offline/standalone), Free For All combat is active
            _isCombatActive = true;
            _currentMatchState = MatchState.CombatActive;
        }

        private void Start()
        {
            RegisterNetworkCallbacks();
        }

        private void OnDestroy()
        {
            UnregisterNetworkCallbacks();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        #region NGO Callbacks & CustomMessaging Setup
        private void RegisterNetworkCallbacks()
        {
            var net = NetworkManager.Singleton;
            if (net != null)
            {
                net.OnServerStarted += HandleServerStarted;
                net.OnServerStopped += HandleServerStopped;
                net.OnClientConnectedCallback += HandleClientConnected;
                net.OnClientDisconnectCallback += HandleClientDisconnected;
            }
        }

        private void UnregisterNetworkCallbacks()
        {
            var net = NetworkManager.Singleton;
            if (net != null)
            {
                net.OnServerStarted -= HandleServerStarted;
                net.OnServerStopped -= HandleServerStopped;
                net.OnClientConnectedCallback -= HandleClientConnected;
                net.OnClientDisconnectCallback -= HandleClientDisconnected;

                if (net.CustomMessagingManager != null)
                {
                    net.CustomMessagingManager.UnregisterNamedMessageHandler(MatchSyncMessageName);
                }
            }
        }

        private void HandleServerStarted()
        {
            _playerTeams.Clear();
            RegisterCustomMessageHandler();

            // Register host player (ClientId 0)
            RegisterPlayer(NetworkManager.Singleton.LocalClientId);
            EvaluateMatchState();
            BroadcastMatchSync();
        }

        private void HandleServerStopped(bool wasHost)
        {
            _playerTeams.Clear();
            _teamAlphaCount = 0;
            _teamBravoCount = 0;
            _currentGameMode = _defaultGameMode;
            _currentMatchState = MatchState.CombatActive;
            _isCombatActive = true;
            _localPlayerTeam = -1;
            OnMatchStateChanged?.Invoke(_currentGameMode, _currentMatchState);
        }

        private void HandleClientConnected(ulong clientId)
        {
            RegisterCustomMessageHandler();

            var net = NetworkManager.Singleton;
            if (net == null) return;

            if (net.IsServer)
            {
                // Sync latest match state to the newly connected client
                SendMatchSyncToClient(clientId);
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            var net = NetworkManager.Singleton;
            if (net != null && net.IsServer)
            {
                UnregisterPlayer(clientId);
            }
        }

        private void RegisterCustomMessageHandler()
        {
            var net = NetworkManager.Singleton;
            if (net != null && net.CustomMessagingManager != null)
            {
                net.CustomMessagingManager.RegisterNamedMessageHandler(MatchSyncMessageName, OnMatchSyncReceived);
            }
        }

        private void OnMatchSyncReceived(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte modeByte);
            reader.ReadValueSafe(out byte stateByte);
            reader.ReadValueSafe(out int alphaCount);
            reader.ReadValueSafe(out int bravoCount);
            reader.ReadValueSafe(out bool combatActive);
            reader.ReadValueSafe(out int assignedTeam);

            _currentGameMode = (GameMode)modeByte;
            _currentMatchState = (MatchState)stateByte;
            _teamAlphaCount = alphaCount;
            _teamBravoCount = bravoCount;
            _isCombatActive = combatActive;
            _localPlayerTeam = assignedTeam;

            Debug.Log($"[MatchManager] Client received match sync: Mode={_currentGameMode}, State={_currentMatchState}, CombatActive={_isCombatActive}, Team={_localPlayerTeam} (Alpha: {_teamAlphaCount}, Bravo: {_teamBravoCount})");

            OnMatchStateChanged?.Invoke(_currentGameMode, _currentMatchState);
        }
        #endregion

        #region Server Rule Enforcement & Team Balancing
        /// <summary>
        /// Configures the game mode when the host creates the room.
        /// </summary>
        public void SetGameMode(GameMode mode)
        {
            _currentGameMode = mode;
            _defaultGameMode = mode;

            var net = NetworkManager.Singleton;
            if (net != null && net.IsServer)
            {
                EvaluateMatchState();
                BroadcastMatchSync();
            }
            else
            {
                // Offline / Standalone
                _isCombatActive = (mode == GameMode.FreeForAll);
                _currentMatchState = _isCombatActive ? MatchState.CombatActive : MatchState.WaitingForEqualTeams;
                OnMatchStateChanged?.Invoke(_currentGameMode, _currentMatchState);
            }

            Debug.Log($"[MatchManager] Configured Game Mode: {mode}");
        }

        /// <summary>
        /// Registers a connecting client and assigns them to a team or FFA roster.
        /// In Team Match: auto-balances between Alpha (0) and Bravo (1).
        /// In Free For All: assigns team -1.
        /// </summary>
        public int RegisterPlayer(ulong clientId)
        {
            int assignedTeam;
            if (_currentGameMode == GameMode.FreeForAll)
            {
                assignedTeam = -1;
            }
            else
            {
                // Auto-balance: Assign to team with fewer members
                int alpha = CountTeamMembers(0);
                int bravo = CountTeamMembers(1);
                assignedTeam = (alpha <= bravo) ? 0 : 1;
            }

            _playerTeams[clientId] = assignedTeam;

            var net = NetworkManager.Singleton;
            if (net != null && clientId == net.LocalClientId)
            {
                _localPlayerTeam = assignedTeam;
            }

            RecalculateTeamCounts();
            EvaluateMatchState();
            BroadcastMatchSync();

            Debug.Log($"[MatchManager] Client {clientId} registered in {_currentGameMode}. Assigned Team: {(assignedTeam == 0 ? "Alpha" : assignedTeam == 1 ? "Bravo" : "FFA")}. Counts: Alpha={_teamAlphaCount}, Bravo={_teamBravoCount}.");
            return assignedTeam;
        }

        /// <summary>
        /// Unregisters a disconnecting client and updates match balance.
        /// </summary>
        public void UnregisterPlayer(ulong clientId)
        {
            if (_playerTeams.Remove(clientId))
            {
                RecalculateTeamCounts();
                EvaluateMatchState();
                BroadcastMatchSync();
                Debug.Log($"[MatchManager] Client {clientId} unregistered. Remaining Counts: Alpha={_teamAlphaCount}, Bravo={_teamBravoCount}.");
            }
        }

        /// <summary>
        /// Retrieves the assigned team index for a given client (0 = Alpha, 1 = Bravo, -1 = FFA).
        /// </summary>
        public int GetPlayerTeam(ulong clientId)
        {
            if (_playerTeams.TryGetValue(clientId, out int team))
            {
                return team;
            }
            return _currentGameMode == GameMode.FreeForAll ? -1 : 0;
        }

        /// <summary>
        /// Evaluates team counts and determines whether combat is unlocked or locked.
        /// In Team Match: requires EQUAL player counts (Alpha == Bravo && Alpha > 0) to start.
        /// In Free For All: combat is immediately active whenever players are present.
        /// </summary>
        public void EvaluateMatchState()
        {
            var net = NetworkManager.Singleton;
            bool isServer = net != null && net.IsServer;

            int totalPlayers = _playerTeams.Count;
            if (!isServer && totalPlayers == 0)
            {
                totalPlayers = 1; // Standalone test mode
            }

            if (_currentGameMode == GameMode.FreeForAll)
            {
                // Free For All starts immediately
                _currentMatchState = totalPlayers > 0 ? MatchState.CombatActive : MatchState.WaitingForPlayers;
                _isCombatActive = totalPlayers > 0;
            }
            else
            {
                // Team Match: Equal players rule (e.g. 1v1, 2v2, 3v3, 4v4)
                int alpha = _teamAlphaCount;
                int bravo = _teamBravoCount;

                if (alpha == 0 && bravo == 0)
                {
                    _currentMatchState = MatchState.WaitingForPlayers;
                    _isCombatActive = false;
                }
                else if (alpha == bravo && alpha > 0)
                {
                    _currentMatchState = MatchState.CombatActive;
                    _isCombatActive = true;
                }
                else
                {
                    // Unequal teams: lockout combat until balanced
                    _currentMatchState = MatchState.WaitingForEqualTeams;
                    _isCombatActive = false;
                }
            }

            Debug.Log($"[MatchManager] Match state evaluated: Mode={_currentGameMode}, State={_currentMatchState}, CombatActive={_isCombatActive} (Alpha: {_teamAlphaCount}, Bravo: {_teamBravoCount}).");
            OnMatchStateChanged?.Invoke(_currentGameMode, _currentMatchState);
        }

        private void RecalculateTeamCounts()
        {
            int alpha = 0;
            int bravo = 0;
            foreach (var kvp in _playerTeams)
            {
                if (kvp.Value == 0) alpha++;
                else if (kvp.Value == 1) bravo++;
            }
            _teamAlphaCount = alpha;
            _teamBravoCount = bravo;
        }

        private int CountTeamMembers(int team)
        {
            int count = 0;
            foreach (var kvp in _playerTeams)
            {
                if (kvp.Value == team) count++;
            }
            return count;
        }

        /// <summary>
        /// Validates whether damage can be dealt between attacker and target.
        /// Blocks combat during team unbalance, and enforces friendly fire immunity in Team Match.
        /// </summary>
        public bool CanDamage(ulong attackerClientId, ulong targetClientId)
        {
            // Combat must be active
            if (!_isCombatActive)
            {
                return false;
            }

            // Free For All: everyone can damage everyone
            if (_currentGameMode == GameMode.FreeForAll)
            {
                return true;
            }

            // Team Match: Friendly fire protection
            int attackerTeam = GetPlayerTeam(attackerClientId);
            int targetTeam = GetPlayerTeam(targetClientId);
            return attackerTeam != targetTeam;
        }
        #endregion

        #region Synchronization Broadcasting
        /// <summary>
        /// Broadcasts authoritative match state to all connected clients.
        /// </summary>
        public void BroadcastMatchSync()
        {
            var net = NetworkManager.Singleton;
            if (net == null || !net.IsServer || net.CustomMessagingManager == null)
            {
                return;
            }

            foreach (var clientId in net.ConnectedClientsIds)
            {
                SendMatchSyncToClient(clientId);
            }
        }

        private void SendMatchSyncToClient(ulong clientId)
        {
            var net = NetworkManager.Singleton;
            if (net == null || !net.IsServer || net.CustomMessagingManager == null)
            {
                return;
            }

            if (!net.ConnectedClients.ContainsKey(clientId))
            {
                return;
            }

            int clientTeam = GetPlayerTeam(clientId);

            using (var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize<byte>() * 2 + FastBufferWriter.GetWriteSize<int>() * 3 + FastBufferWriter.GetWriteSize<bool>(), Allocator.Temp))
            {
                writer.WriteValueSafe((byte)_currentGameMode);
                writer.WriteValueSafe((byte)_currentMatchState);
                writer.WriteValueSafe(_teamAlphaCount);
                writer.WriteValueSafe(_teamBravoCount);
                writer.WriteValueSafe(_isCombatActive);
                writer.WriteValueSafe(clientTeam);

                net.CustomMessagingManager.SendNamedMessage(MatchSyncMessageName, clientId, writer);
            }
        }
        #endregion
    }
}
