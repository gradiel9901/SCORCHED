using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Scorched.Combat;

namespace Scorched.Networking
{
    /// <summary>
    /// Represents an active game room discovered over the local area network or local machine.
    /// </summary>
    [Serializable]
    public struct DiscoveredRoom
    {
        public string RoomId;
        public string RoomName;
        public string HostIp;
        public ushort Port;
        public int CurrentPlayers;
        public int MaxPlayers;
        public GameMode Mode;
        public bool IsMatchInProgress;
        public float LastHeardTime;

        public string EndpointString => $"{HostIp}:{Port}";
        public bool IsFull => CurrentPlayers >= MaxPlayers;
    }

    /// <summary>
    /// High-performance, lightweight UDP broadcast and query room discovery system.
    /// Allows hosts to advertise active matches over LAN/loopback without cloud matchmaking services,
    /// and allows clients to automatically discover and list rooms in the tactical Lobby browser.
    /// Deduplicates multiple network interface packets to ensure 1 unique room entry per host session.
    /// </summary>
    [DisallowMultipleComponent]
    public class LANRoomDiscovery : MonoBehaviour
    {
        public const ushort DefaultDiscoveryPort = 47777;
        private const string BeaconHeader = "SCORCHED_BEACON";
        private const string QueryHeader = "SCORCHED_QUERY";
        private const string ResponseHeader = "SCORCHED_RESPONSE";
        private const float RoomTimeoutSeconds = 3.5f;
        private const float BroadcastInterval = 0.75f;
        private const float QueryInterval = 0.75f;

        public static LANRoomDiscovery Instance { get; private set; }

        [Header("Discovery Settings")]
        [SerializeField] private ushort _discoveryPort = DefaultDiscoveryPort;
        [SerializeField] private bool _enableLogging = true;

        // Host state
        private bool _isAdvertising = false;
        private string _hostSessionId = "";
        private string _hostedRoomName = "Scorched Match";
        private ushort _hostedGamePort = 7777;
        private int _hostedMaxPlayers = 8;
        private int _hostedCurrentPlayers = 1;
        private GameMode _hostedGameMode = GameMode.FreeForAll;
        private bool _hostedMatchInProgress = false;
        private Socket _hostSocket;
        private float _lastBroadcastTime = 0f;

        // Client (Lobby) state
        private bool _isListening = false;
        private Socket _clientSocket;
        private float _lastQueryTime = 0f;
        // Keyed by RoomId (unique host session ID) to eliminate duplicate listings
        private readonly Dictionary<string, DiscoveredRoom> _rooms = new Dictionary<string, DiscoveredRoom>();
        private readonly List<DiscoveredRoom> _roomsSnapshot = new List<DiscoveredRoom>();

        /// <summary>
        /// Fires whenever a room is added, updated, or removed from the list.
        /// </summary>
        public event Action<IReadOnlyList<DiscoveredRoom>> OnRoomListChanged;

        /// <summary>
        /// Read-only snapshot of all currently discovered active rooms.
        /// </summary>
        public IReadOnlyList<DiscoveredRoom> ActiveRooms => _roomsSnapshot;

        /// <summary>
        /// Indicates if this node is actively broadcasting as a host.
        /// </summary>
        public bool IsAdvertising => _isAdvertising;

        /// <summary>
        /// Indicates if this node is actively scanning for rooms as a client.
        /// </summary>
        public bool IsListening => _isListening;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (_isAdvertising)
            {
                UpdateHostAdvertising();
            }

            if (_isListening)
            {
                UpdateClientListening();
            }
        }

        private void OnDisable()
        {
            StopAdvertising();
            StopListening();
        }

        private void OnDestroy()
        {
            StopAdvertising();
            StopListening();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            StopAdvertising();
            StopListening();
        }

        #region Host Advertising
        /// <summary>
        /// Begins advertising a hosted game room across the local network and loopback.
        /// </summary>
        public void StartAdvertising(string roomName, ushort gamePort, int maxPlayers, GameMode gameMode = GameMode.FreeForAll, int currentPlayers = 1)
        {
            StopAdvertising();

            _hostSessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _hostedRoomName = string.IsNullOrWhiteSpace(roomName) ? "Scorched Match" : roomName.Trim();
            _hostedGamePort = gamePort > 0 ? gamePort : (ushort)7777;
            _hostedMaxPlayers = Mathf.Max(2, maxPlayers);
            _hostedCurrentPlayers = Mathf.Max(1, currentPlayers);
            _hostedGameMode = gameMode;
            _hostedMatchInProgress = false;

            try
            {
                _hostSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _hostSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _hostSocket.ExclusiveAddressUse = false;
                _hostSocket.EnableBroadcast = true;
                _hostSocket.Blocking = false;
                _hostSocket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

                _isAdvertising = true;
                _lastBroadcastTime = Time.realtimeSinceStartup - BroadcastInterval; // Trigger immediate broadcast

                if (_enableLogging)
                {
                    Debug.Log($"[LANRoomDiscovery] Started advertising room '{_hostedRoomName}' [{_hostedGameMode}] (ID: {_hostSessionId}) on discovery port {_discoveryPort} (Game port: {_hostedGamePort}).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Failed to bind host discovery socket on port {_discoveryPort}: {ex.Message}. Falling back to broadcast-only.");
                _isAdvertising = false;
                CloseSocket(ref _hostSocket);
            }
        }

        /// <summary>
        /// Updates the advertised player count (e.g. when peers connect or disconnect).
        /// </summary>
        public void UpdatePlayerCount(int currentPlayers)
        {
            _hostedCurrentPlayers = Mathf.Clamp(currentPlayers, 1, _hostedMaxPlayers);
        }

        /// <summary>
        /// Updates whether the advertised match is in active combat or lobby stage.
        /// </summary>
        public void UpdateMatchInProgress(bool inProgress)
        {
            _hostedMatchInProgress = inProgress;
        }

        /// <summary>
        /// Stops advertising the hosted room and frees the discovery socket.
        /// </summary>
        public void StopAdvertising()
        {
            if (!_isAdvertising && _hostSocket == null)
            {
                return;
            }

            _isAdvertising = false;
            CloseSocket(ref _hostSocket);

            if (_enableLogging)
            {
                Debug.Log("[LANRoomDiscovery] Stopped advertising room.");
            }
        }

        private void UpdateHostAdvertising()
        {
            // 1. Process any incoming query packets from clients looking for rooms
            ProcessIncomingHostQueries();

            // 2. Periodically broadcast unsolicited beacons
            if (Time.realtimeSinceStartup - _lastBroadcastTime >= BroadcastInterval)
            {
                _lastBroadcastTime = Time.realtimeSinceStartup;
                BroadcastBeacon();
            }
        }

        private void ProcessIncomingHostQueries()
        {
            if (_hostSocket == null) return;

            byte[] buffer = new byte[1024];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (_hostSocket != null && _hostSocket.Poll(0, SelectMode.SelectRead))
                {
                    int received = _hostSocket.ReceiveFrom(buffer, ref remoteEp);
                    if (received > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, received);
                        if (message.StartsWith(QueryHeader))
                        {
                            SendResponseTo(remoteEp);
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock)
                {
                    Debug.LogWarning($"[LANRoomDiscovery] Host socket receive error: {ex.SocketErrorCode}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Unexpected host socket error: {ex.Message}");
            }
        }

        private void BroadcastBeacon()
        {
            if (_hostSocket == null) return;

            string payload = $"{BeaconHeader}|{_hostSessionId}|{_hostedRoomName}|{_hostedGamePort}|{_hostedCurrentPlayers}|{_hostedMaxPlayers}|{(int)_hostedGameMode}|{_hostedMatchInProgress}";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            try
            {
                foreach (IPEndPoint ep in GetDiscoveryBroadcastEndpoints(_discoveryPort))
                {
                    try { _hostSocket.SendTo(data, ep); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Failed to broadcast beacon: {ex.Message}");
            }
        }

        private void SendResponseTo(EndPoint clientEndpoint)
        {
            if (_hostSocket == null) return;

            string payload = $"{ResponseHeader}|{_hostSessionId}|{_hostedRoomName}|{_hostedGamePort}|{_hostedCurrentPlayers}|{_hostedMaxPlayers}|{(int)_hostedGameMode}|{_hostedMatchInProgress}";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            try
            {
                _hostSocket.SendTo(data, clientEndpoint);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Failed to send query response to {clientEndpoint}: {ex.Message}");
            }
        }
        #endregion

        #region Client Listening (Lobby Browser)
        /// <summary>
        /// Begins scanning for active game rooms across the local network and loopback.
        /// </summary>
        public void StartListening()
        {
            StopListening();

            _rooms.Clear();
            _roomsSnapshot.Clear();

            try
            {
                // Bind to port 0 (ephemeral port) so client can never conflict with host or other clients
                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _clientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _clientSocket.ExclusiveAddressUse = false;
                _clientSocket.EnableBroadcast = true;
                _clientSocket.Blocking = false;
                _clientSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

                _isListening = true;
                _lastQueryTime = Time.realtimeSinceStartup - QueryInterval; // Trigger immediate probe

                if (_enableLogging)
                {
                    Debug.Log($"[LANRoomDiscovery] Started listening for rooms on ephemeral port {((IPEndPoint)_clientSocket.LocalEndPoint).Port}.");
                }

                OnRoomListChanged?.Invoke(_roomsSnapshot);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LANRoomDiscovery] Failed to start client listener socket: {ex.Message}");
                _isListening = false;
                CloseSocket(ref _clientSocket);
            }
        }

        /// <summary>
        /// Stops scanning for rooms and frees the client discovery socket.
        /// </summary>
        public void StopListening()
        {
            if (!_isListening && _clientSocket == null)
            {
                return;
            }

            _isListening = false;
            CloseSocket(ref _clientSocket);
            _rooms.Clear();
            _roomsSnapshot.Clear();

            if (_enableLogging)
            {
                Debug.Log("[LANRoomDiscovery] Stopped listening for rooms.");
            }
        }

        /// <summary>
        /// Forces an immediate re-scan and clears the current room cache.
        /// </summary>
        public void RefreshRooms()
        {
            _rooms.Clear();
            _roomsSnapshot.Clear();
            OnRoomListChanged?.Invoke(_roomsSnapshot);

            if (_isListening)
            {
                SendClientQuery();
            }
            else
            {
                StartListening();
            }
        }

        private void UpdateClientListening()
        {
            // 1. Process all received packets
            bool listChanged = ProcessIncomingClientPackets();

            // 2. Periodically send query requests
            if (Time.realtimeSinceStartup - _lastQueryTime >= QueryInterval)
            {
                _lastQueryTime = Time.realtimeSinceStartup;
                SendClientQuery();
            }

            // 3. Prune expired rooms
            if (PruneExpiredRooms())
            {
                listChanged = true;
            }

            // 4. Notify listeners if room roster updated
            if (listChanged)
            {
                RebuildSnapshot();
                OnRoomListChanged?.Invoke(_roomsSnapshot);
            }
        }

        private void SendClientQuery()
        {
            if (_clientSocket == null) return;

            byte[] data = Encoding.UTF8.GetBytes(QueryHeader);
            try
            {
                foreach (IPEndPoint ep in GetDiscoveryBroadcastEndpoints(_discoveryPort))
                {
                    try { _clientSocket.SendTo(data, ep); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Failed to send query probe: {ex.Message}");
            }
        }

        private bool ProcessIncomingClientPackets()
        {
            if (_clientSocket == null) return false;

            byte[] buffer = new byte[1024];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
            bool listChanged = false;

            try
            {
                while (_clientSocket != null && _clientSocket.Poll(0, SelectMode.SelectRead))
                {
                    int received = _clientSocket.ReceiveFrom(buffer, ref remoteEp);
                    if (received > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, received);
                        if (TryParseRoomPacket(message, (IPEndPoint)remoteEp, out DiscoveredRoom room))
                        {
                            // Key by unique RoomId so dual packets (LAN broadcast & loopback) never create duplicate rows
                            string key = room.RoomId;
                            if (!_rooms.TryGetValue(key, out DiscoveredRoom existing))
                            {
                                _rooms[key] = room;
                                listChanged = true;
                                if (_enableLogging)
                                {
                                    Debug.Log($"[LANRoomDiscovery] Discovered new room '{room.RoomName}' [{room.Mode}] (ID: {room.RoomId}) at {room.EndpointString}.");
                                }
                            }
                            else
                            {
                                // Same-machine loopback is always preferred when host is local
                                if (room.HostIp == "127.0.0.1" && existing.HostIp != "127.0.0.1")
                                {
                                    existing.HostIp = "127.0.0.1";
                                    listChanged = true;
                                }

                                if (existing.CurrentPlayers != room.CurrentPlayers ||
                                    existing.IsMatchInProgress != room.IsMatchInProgress ||
                                    existing.Mode != room.Mode ||
                                    existing.RoomName != room.RoomName)
                                {
                                    listChanged = true;
                                }

                                existing.CurrentPlayers = room.CurrentPlayers;
                                existing.MaxPlayers = room.MaxPlayers;
                                existing.Mode = room.Mode;
                                existing.RoomName = room.RoomName;
                                existing.Port = room.Port;
                                existing.IsMatchInProgress = room.IsMatchInProgress;
                                existing.LastHeardTime = Time.realtimeSinceStartup;
                                _rooms[key] = existing;
                            }
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock)
                {
                    Debug.LogWarning($"[LANRoomDiscovery] Client socket receive error: {ex.SocketErrorCode}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LANRoomDiscovery] Client socket exception: {ex.Message}");
            }

            return listChanged;
        }

        private bool TryParseRoomPacket(string message, IPEndPoint senderEp, out DiscoveredRoom room)
        {
            room = default;

            if (string.IsNullOrWhiteSpace(message)) return false;

            string[] tokens = message.Split('|');
            // Format: HEADER|ROOM_ID|ROOM_NAME|PORT|CURRENT_PLAYERS|MAX_PLAYERS|GAME_MODE|MATCH_IN_PROGRESS
            if (tokens.Length < 8) return false;

            string header = tokens[0];
            if (header != BeaconHeader && header != ResponseHeader) return false;

            string roomId = tokens[1];
            string roomName = tokens[2];
            if (!ushort.TryParse(tokens[3], out ushort gamePort)) return false;
            if (!int.TryParse(tokens[4], out int currentPlayers)) return false;
            if (!int.TryParse(tokens[5], out int maxPlayers)) return false;

            GameMode mode = GameMode.FreeForAll;
            if (byte.TryParse(tokens[6], out byte modeByte) && Enum.IsDefined(typeof(GameMode), modeByte))
            {
                mode = (GameMode)modeByte;
            }

            if (!bool.TryParse(tokens[7], out bool isMatchInProgress)) isMatchInProgress = false;

            // Resolve host IP from the actual sender socket endpoint
            string hostIp = senderEp.Address.ToString();
            if (senderEp.Address.IsIPv4MappedToIPv6)
            {
                hostIp = senderEp.Address.MapToIPv4().ToString();
            }

            // If packet came from this same machine (e.g. LAN/virtual adapter), connect via reliable loopback
            if (IsLocalMachineIpv4(hostIp))
            {
                hostIp = "127.0.0.1";
            }

            room = new DiscoveredRoom
            {
                RoomId = roomId,
                RoomName = roomName,
                HostIp = hostIp,
                Port = gamePort,
                CurrentPlayers = currentPlayers,
                MaxPlayers = maxPlayers,
                Mode = mode,
                IsMatchInProgress = isMatchInProgress,
                LastHeardTime = Time.realtimeSinceStartup
            };

            return true;
        }

        private bool PruneExpiredRooms()
        {
            if (_rooms.Count == 0) return false;

            float now = Time.realtimeSinceStartup;
            List<string> expiredKeys = null;

            foreach (var kvp in _rooms)
            {
                if (now - kvp.Value.LastHeardTime > RoomTimeoutSeconds)
                {
                    if (expiredKeys == null) expiredKeys = new List<string>();
                    expiredKeys.Add(kvp.Key);
                }
            }

            if (expiredKeys != null)
            {
                foreach (var key in expiredKeys)
                {
                    if (_enableLogging)
                    {
                        Debug.Log($"[LANRoomDiscovery] Room '{_rooms[key].RoomName}' (ID: {key}) timed out.");
                    }
                    _rooms.Remove(key);
                }
                return true;
            }

            return false;
        }

        private void RebuildSnapshot()
        {
            _roomsSnapshot.Clear();
            foreach (var kvp in _rooms)
            {
                _roomsSnapshot.Add(kvp.Value);
            }
        }
        #endregion

        private static List<IPEndPoint> GetDiscoveryBroadcastEndpoints(ushort port)
        {
            var endpoints = new List<IPEndPoint>();
            var seen = new HashSet<string>();

            void Add(IPAddress addr)
            {
                if (addr == null) return;
                string key = addr.ToString();
                if (seen.Add(key))
                {
                    endpoints.Add(new IPEndPoint(addr, port));
                }
            }

            Add(IPAddress.Loopback);
            Add(IPAddress.Broadcast);

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation uni in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null)
                        {
                            continue;
                        }

                        byte[] ipBytes = uni.Address.GetAddressBytes();
                        byte[] maskBytes = uni.IPv4Mask.GetAddressBytes();
                        byte[] bcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }
                        Add(new IPAddress(bcastBytes));
                    }
                }
            }
            catch
            {
                // Fallback to Loopback + Global Broadcast already added
            }

            return endpoints;
        }

        private static bool IsLocalMachineIpv4(string ipStr)
        {
            if (string.IsNullOrEmpty(ipStr)) return false;
            if (ipStr == "127.0.0.1") return true;

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation uni in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily == AddressFamily.InterNetwork &&
                            string.Equals(uni.Address.ToString(), ipStr, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore NIC enumeration errors
            }

            return false;
        }

        private static void CloseSocket(ref Socket socket)
        {
            if (socket != null)
            {
                try
                {
                    socket.Close();
                    socket.Dispose();
                }
                catch { }
                socket = null;
            }
        }
    }
}
