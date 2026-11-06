using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using Unity.Netcode;
using Scorched.Networking;
using Scorched.Player;

namespace Scorched.UI
{
    /// <summary>
    /// Comprehensive tactical Main Menu and In-Game Pause UI Manager for "Scorched".
    /// Features:
    /// - Tactical military aesthetics matching the SCORCHED HUD.
    /// - Screens: Main Menu, Create Room, Lobby (LAN/Local Room Browser), Settings, and In-Game Pause.
    /// - Integration with LANRoomDiscovery for real-time room advertising and joining.
    /// - Direct IP Connect fallback for manual LAN/WAN joins.
    /// - Persistent settings for Master Audio, Mouse Sensitivity, and FOV.
    /// - Automatic cursor locking and input routing.
    /// </summary>
    [DisallowMultipleComponent]
    public class MainMenuUIManager : MonoBehaviour
    {
        public static MainMenuUIManager Instance { get; private set; }

        public static bool IsMenuOpen => Instance != null && (Instance._isMainMenuOpen || Instance._isPauseMenuOpen);

        #region Theme Colors
        private static readonly Color ColorBgDark = new Color(0.04f, 0.06f, 0.04f, 0.94f);
        private static readonly Color ColorPanelDark = new Color(0.07f, 0.09f, 0.07f, 0.92f);
        private static readonly Color ColorBorderGreen = new Color(0.24f, 0.44f, 0.24f, 0.85f);
        private static readonly Color ColorAccentAmber = new Color(0.96f, 0.65f, 0.14f, 1.0f);
        private static readonly Color ColorActiveGreen = new Color(0.23f, 0.92f, 0.23f, 1.0f);
        private static readonly Color ColorTextWhite = new Color(0.92f, 0.95f, 0.92f, 1.0f);
        private static readonly Color ColorTextMuted = new Color(0.65f, 0.72f, 0.65f, 0.9f);
        private static readonly Color ColorButtonNormal = new Color(0.10f, 0.14f, 0.10f, 0.95f);
        private static readonly Color ColorButtonHover = new Color(0.16f, 0.24f, 0.16f, 1.0f);
        private static readonly Color ColorButtonPressed = new Color(0.12f, 0.35f, 0.12f, 1.0f);
        private static readonly Color ColorInputBg = new Color(0.03f, 0.04f, 0.03f, 0.95f);
        #endregion

        #region UI State & Roots
        private Canvas _rootCanvas;
        private GameObject _overlayRoot;
        private GameObject _mainMenuPanel;
        private GameObject _createRoomPanel;
        private GameObject _lobbyPanel;
        private GameObject _settingsPanel;
        private GameObject _pauseMenuPanel;

        private bool _isMainMenuOpen = true;
        private bool _isPauseMenuOpen = false;

        // Create Room controls
        private InputField _roomNameInput;
        private InputField _createPortInput;
        private int _selectedMaxPlayers = 8;
        private Text _maxPlayersLabel;
        private Scorched.Combat.GameMode _selectedGameMode = Scorched.Combat.GameMode.FreeForAll;
        private Button _btnModeFFA;
        private Button _btnModeTeam;
        private Text _gameModeInfoText;

        // Lobby controls
        private Transform _roomListContent;
        private Text _lobbyStatusText;
        private InputField _directIpInput;
        private InputField _directPortInput;
        private readonly List<GameObject> _roomRowObjects = new List<GameObject>();

        // Settings controls
        private Slider _volumeSlider;
        private Text _volumeValueText;
        private Slider _sensitivitySlider;
        private Text _sensitivityValueText;
        private Slider _fovSlider;
        private Text _fovValueText;

        // Pause Menu controls
        private Text _pauseMatchInfoText;

        private Font _tacticalFont;
        #endregion

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitialize()
        {
            if (Instance == null && FindAnyObjectByType<MainMenuUIManager>() == null)
            {
                var menuGo = new GameObject("MainMenuUIManager");
                menuGo.AddComponent<MainMenuUIManager>();
            }
        }

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _tacticalFont = GetDefaultFont();
            EnsureEventSystem();
            EnsureLANDiscovery();
            BuildTacticalUI();
            LoadAndApplySavedSettings();
        }

        private void Start()
        {
            // Subscribe to LAN discovery updates
            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.OnRoomListChanged += HandleRoomListUpdated;
            }

            // Hook into NetworkManager lifecycle callbacks
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
                NetworkManager.Singleton.OnServerStopped += HandleServerStopped;
            }

            // Initial check: if already running (e.g. MPPM auto-host), hide menu
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                HideAllMenus();
            }
            else
            {
                ShowMainMenu();
            }
        }

        private void Update()
        {
            HandleKeyboardShortcuts();
            EnforceCursorState();
        }

        private void OnDestroy()
        {
            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.OnRoomListChanged -= HandleRoomListUpdated;
            }

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
                NetworkManager.Singleton.OnServerStopped -= HandleServerStopped;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }
        #endregion

        #region Input & Cursor Routing
        private void HandleKeyboardShortcuts()
        {
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                bool isNetworkActive = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

                if (_isPauseMenuOpen)
                {
                    ResumeGame();
                }
                else if (isNetworkActive && !_isMainMenuOpen)
                {
                    ShowPauseMenu();
                }
                else if (_isMainMenuOpen)
                {
                    // If in a sub-dialog, return to main panel
                    if (_createRoomPanel.activeSelf || _lobbyPanel.activeSelf || _settingsPanel.activeSelf)
                    {
                        ShowPanel(_mainMenuPanel);
                        if (LANRoomDiscovery.Instance != null)
                        {
                            LANRoomDiscovery.Instance.StopListening();
                        }
                    }
                }
            }
        }

        private void EnforceCursorState()
        {
            if (IsMenuOpen)
            {
                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
        }
        #endregion

        #region Screen Navigation & Visibility
        public void ShowMainMenu()
        {
            _isMainMenuOpen = true;
            _isPauseMenuOpen = false;

            if (_overlayRoot != null) _overlayRoot.SetActive(true);
            ShowPanel(_mainMenuPanel);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.StopListening();
            }
        }

        public void OpenCreateRoom()
        {
            ShowPanel(_createRoomPanel);
        }

        public void OpenLobby()
        {
            ShowPanel(_lobbyPanel);
            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.StartListening();
            }
            if (_lobbyStatusText != null)
            {
                _lobbyStatusText.text = "Scanning local broadcast network on UDP 47777...";
            }
        }

        public void OpenSettings()
        {
            ShowPanel(_settingsPanel);
        }

        public void ShowPauseMenu()
        {
            _isPauseMenuOpen = true;
            _isMainMenuOpen = false;

            if (_overlayRoot != null) _overlayRoot.SetActive(true);
            ShowPanel(_pauseMenuPanel);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            UpdatePauseMenuTelemetry();
        }

        public void ResumeGame()
        {
            HideAllMenus();
        }

        public void HideAllMenus()
        {
            _isMainMenuOpen = false;
            _isPauseMenuOpen = false;

            if (_overlayRoot != null) _overlayRoot.SetActive(false);
            if (_mainMenuPanel != null) _mainMenuPanel.SetActive(false);
            if (_createRoomPanel != null) _createRoomPanel.SetActive(false);
            if (_lobbyPanel != null) _lobbyPanel.SetActive(false);
            if (_settingsPanel != null) _settingsPanel.SetActive(false);
            if (_pauseMenuPanel != null) _pauseMenuPanel.SetActive(false);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        public void TogglePauseMenu()
        {
            if (_isPauseMenuOpen)
            {
                ResumeGame();
            }
            else
            {
                ShowPauseMenu();
            }
        }

        private void ShowPanel(GameObject panelToShow)
        {
            if (_mainMenuPanel != null) _mainMenuPanel.SetActive(_mainMenuPanel == panelToShow);
            if (_createRoomPanel != null) _createRoomPanel.SetActive(_createRoomPanel == panelToShow);
            if (_lobbyPanel != null) _lobbyPanel.SetActive(_lobbyPanel == panelToShow);
            if (_settingsPanel != null) _settingsPanel.SetActive(_settingsPanel == panelToShow);
            if (_pauseMenuPanel != null) _pauseMenuPanel.SetActive(_pauseMenuPanel == panelToShow);
        }
        #endregion

        #region Match Actions: Host, Join, Disconnect, Quit
        public void ExecuteHostRoom()
        {
            string roomName = _roomNameInput != null ? _roomNameInput.text : "Scorched Match";
            if (string.IsNullOrWhiteSpace(roomName)) roomName = "Scorched Match #1";

            string portStr = _createPortInput != null ? _createPortInput.text : "7777";
            if (!ushort.TryParse(portStr, out ushort port)) port = 7777;

            int maxPlayers = _selectedMaxPlayers;

            PlayerPrefs.SetString("Scorched_LastRoomName", roomName);
            PlayerPrefs.SetString("Scorched_LastPort", port.ToString());
            PlayerPrefs.Save();

            // 1. Configure transport port & listen address
            var netManager = NetworkManager.Singleton;
            if (netManager != null)
            {
                var optimizer = netManager.GetComponent<UnityTransportOptimizer>();
                if (optimizer != null)
                {
                    optimizer.SetConnectionData("127.0.0.1", port, "0.0.0.0");
                }
                else
                {
                    var transport = netManager.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
                    if (transport != null)
                    {
                        transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
                    }
                }

                var connHandler = netManager.GetComponent<NetworkConnectionHandler>();
                if (connHandler != null)
                {
                    connHandler.SetMaxPlayers(maxPlayers);
                }

                // 2. Configure game mode
                if (Scorched.Combat.NetworkMatchManager.Instance != null)
                {
                    Scorched.Combat.NetworkMatchManager.Instance.SetGameMode(_selectedGameMode);
                }

                // 3. Start advertising room on LAN
                if (LANRoomDiscovery.Instance != null)
                {
                    LANRoomDiscovery.Instance.StartAdvertising(roomName, port, maxPlayers, _selectedGameMode, 1);
                }

                // 4. Start Host
                Debug.Log($"[MainMenu] Hosting room '{roomName}' [{_selectedGameMode}] on port {port} (Max: {maxPlayers})...");
                netManager.StartHost();
                HideAllMenus();
            }
            else
            {
                Debug.LogError("[MainMenu] NetworkManager.Singleton is null! Cannot start host.");
            }
        }

        public void ExecuteJoinRoom(DiscoveredRoom room)
        {
            Debug.Log($"[MainMenu] Joining room '{room.RoomName}' at {room.EndpointString}...");
            ConnectToEndpoint(room.HostIp, room.Port);
        }

        public void ExecuteDirectConnect()
        {
            string ip = _directIpInput != null ? _directIpInput.text : "127.0.0.1";
            if (string.IsNullOrWhiteSpace(ip)) ip = "127.0.0.1";

            string portStr = _directPortInput != null ? _directPortInput.text : "7777";
            if (!ushort.TryParse(portStr, out ushort port)) port = 7777;

            PlayerPrefs.SetString("Scorched_LastDirectIp", ip);
            PlayerPrefs.SetString("Scorched_LastPort", port.ToString());
            PlayerPrefs.Save();

            Debug.Log($"[MainMenu] Direct connecting to {ip}:{port}...");
            ConnectToEndpoint(ip, port);
        }

        private void ConnectToEndpoint(string ip, ushort port)
        {
            var netManager = NetworkManager.Singleton;
            if (netManager == null)
            {
                Debug.LogError("[MainMenu] NetworkManager.Singleton is null! Cannot connect.");
                return;
            }

            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.StopListening();
            }

            var optimizer = netManager.GetComponent<UnityTransportOptimizer>();
            if (optimizer != null)
            {
                optimizer.SetConnectionData(ip, port);
            }
            else
            {
                var transport = netManager.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
                if (transport != null)
                {
                    transport.SetConnectionData(ip, port);
                }
            }

            netManager.StartClient();
            HideAllMenus();
        }

        public void DisconnectSession()
        {
            Debug.Log("[MainMenu] Disconnecting network session...");

            if (LANRoomDiscovery.Instance != null)
            {
                LANRoomDiscovery.Instance.StopAdvertising();
                LANRoomDiscovery.Instance.StopListening();
            }

            var netManager = NetworkManager.Singleton;
            if (netManager != null)
            {
                var connHandler = netManager.GetComponent<NetworkConnectionHandler>();
                if (connHandler != null)
                {
                    connHandler.TerminateSession();
                }
                else if (netManager.IsListening)
                {
                    netManager.Shutdown();
                }
            }

            ShowMainMenu();
        }

        public void ExecuteQuit()
        {
            Debug.Log("[MainMenu] Exiting SCORCHED...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
        #endregion

        #region Network Lifecycle Callbacks
        private void HandleClientConnected(ulong clientId)
        {
            var netManager = NetworkManager.Singleton;
            if (netManager != null && clientId == netManager.LocalClientId)
            {
                Debug.Log("[MainMenu] Local client successfully connected.");
                HideAllMenus();
            }

            if (LANRoomDiscovery.Instance != null && netManager != null && netManager.IsServer)
            {
                int count = netManager.ConnectedClientsIds.Count;
                LANRoomDiscovery.Instance.UpdatePlayerCount(count);
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            var netManager = NetworkManager.Singleton;
            if (netManager != null && clientId == netManager.LocalClientId)
            {
                Debug.LogWarning("[MainMenu] Local client disconnected.");
                DisconnectSession();
            }

            if (LANRoomDiscovery.Instance != null && netManager != null && netManager.IsServer)
            {
                int count = netManager.ConnectedClientsIds.Count;
                LANRoomDiscovery.Instance.UpdatePlayerCount(count);
            }
        }

        private void HandleServerStopped(bool wasHost)
        {
            Debug.Log($"[MainMenu] Server stopped (WasHost: {wasHost}). Returning to Main Menu.");
            DisconnectSession();
        }

        private void UpdatePauseMenuTelemetry()
        {
            if (_pauseMatchInfoText == null) return;

            var netManager = NetworkManager.Singleton;
            if (netManager != null && netManager.IsListening)
            {
                string role = netManager.IsHost ? "Host" : (netManager.IsServer ? "Dedicated Server" : "Client");
                int players = netManager.ConnectedClientsIds.Count;
                _pauseMatchInfoText.text = $"ROLE: <b>{role}</b>  |  PLAYERS: <b>{players}/8</b>  |  STATUS: <b>CONNECTED</b>";
            }
            else
            {
                _pauseMatchInfoText.text = "STATUS: <b>OFFLINE</b>";
            }
        }
        #endregion

        #region Lobby Dynamic Room List
        private void HandleRoomListUpdated(IReadOnlyList<DiscoveredRoom> rooms)
        {
            if (_roomListContent == null) return;

            // Clear old rows
            foreach (var row in _roomRowObjects)
            {
                if (row != null) Destroy(row);
            }
            _roomRowObjects.Clear();

            int count = rooms != null ? rooms.Count : 0;
            if (_lobbyStatusText != null)
            {
                _lobbyStatusText.text = count == 0
                    ? "Scanning local broadcast network on UDP 47777... (No active rooms yet)"
                    : $"Active Local Rooms: <color=#3AEB3A>{count}</color> discovered.";
            }

            if (count == 0)
            {
                var emptyLabel = CreateUIObject("EmptyNotice", _roomListContent);
                var rt = emptyLabel.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(720f, 60f);
                var txt = emptyLabel.AddComponent<Text>();
                txt.font = _tacticalFont;
                txt.fontSize = 13;
                txt.fontStyle = FontStyle.Italic;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = ColorTextMuted;
                txt.text = "NO HOST BEACONS DETECTED ON LOCAL NETWORK\nCreate a room with [CREATE ROOM] or use Direct IP Connect below.";
                _roomRowObjects.Add(emptyLabel);
                return;
            }

            // Populate room table
            for (int i = 0; i < count; i++)
            {
                var room = rooms[i];
                var rowGo = CreateRoomRow(room);
                _roomRowObjects.Add(rowGo);
            }
        }

        private GameObject CreateRoomRow(DiscoveredRoom room)
        {
            var row = CreateUIObject($"RoomRow_{room.EndpointString}", _roomListContent);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(750f, 44f);

            var bg = row.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0.12f, 0.17f, 0.12f, 0.85f);

            var outline = row.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1, -1);

            // Horizontal layout
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(16, 12, 6, 6);
            hlg.spacing = 12;
            hlg.childControlWidth = false;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            // Room Name
            var nameGo = CreateUIObject("Col_Name", row.transform);
            var nameRt = nameGo.GetComponent<RectTransform>();
            nameRt.sizeDelta = new Vector2(210f, 32f);
            var nameTxt = nameGo.AddComponent<Text>();
            nameTxt.font = _tacticalFont;
            nameTxt.fontSize = 13;
            nameTxt.fontStyle = FontStyle.Bold;
            nameTxt.alignment = TextAnchor.MiddleLeft;
            nameTxt.color = ColorTextWhite;
            nameTxt.text = room.RoomName;

            // Mode Badge
            var modeGo = CreateUIObject("Col_Mode", row.transform);
            var modeRt = modeGo.GetComponent<RectTransform>();
            modeRt.sizeDelta = new Vector2(75f, 32f);
            var modeTxt = modeGo.AddComponent<Text>();
            modeTxt.font = _tacticalFont;
            modeTxt.fontSize = 11;
            modeTxt.fontStyle = FontStyle.Bold;
            modeTxt.alignment = TextAnchor.MiddleCenter;
            if (room.Mode == Scorched.Combat.GameMode.TeamMatch)
            {
                modeTxt.color = new Color(0.22f, 0.83f, 0.96f);
                modeTxt.text = "[TEAM]";
            }
            else
            {
                modeTxt.color = ColorAccentAmber;
                modeTxt.text = "[FFA]";
            }

            // Players
            var plyGo = CreateUIObject("Col_Players", row.transform);
            var plyRt = plyGo.GetComponent<RectTransform>();
            plyRt.sizeDelta = new Vector2(75f, 32f);
            var plyTxt = plyGo.AddComponent<Text>();
            plyTxt.font = _tacticalFont;
            plyTxt.fontSize = 12;
            plyTxt.alignment = TextAnchor.MiddleCenter;
            plyTxt.color = room.IsFull ? new Color(0.9f, 0.3f, 0.3f) : ColorActiveGreen;
            plyTxt.text = $"{room.CurrentPlayers} / {room.MaxPlayers}";

            // Endpoint
            var epGo = CreateUIObject("Col_Endpoint", row.transform);
            var epRt = epGo.GetComponent<RectTransform>();
            epRt.sizeDelta = new Vector2(150f, 32f);
            var epTxt = epGo.AddComponent<Text>();
            epTxt.font = _tacticalFont;
            epTxt.fontSize = 11;
            epTxt.alignment = TextAnchor.MiddleLeft;
            epTxt.color = ColorTextMuted;
            epTxt.text = room.EndpointString;

            // Status
            var statGo = CreateUIObject("Col_Status", row.transform);
            var statRt = statGo.GetComponent<RectTransform>();
            statRt.sizeDelta = new Vector2(75f, 32f);
            var statTxt = statGo.AddComponent<Text>();
            statTxt.font = _tacticalFont;
            statTxt.fontSize = 11;
            statTxt.fontStyle = FontStyle.Bold;
            statTxt.alignment = TextAnchor.MiddleCenter;
            if (room.IsFull)
            {
                statTxt.color = new Color(0.9f, 0.2f, 0.2f);
                statTxt.text = "[FULL]";
            }
            else if (room.IsMatchInProgress)
            {
                statTxt.color = ColorAccentAmber;
                statTxt.text = "[COMBAT]";
            }
            else
            {
                statTxt.color = ColorActiveGreen;
                statTxt.text = "[OPEN]";
            }

            // Join Button
            var capturedRoom = room;
            var joinBtn = CreateStyledButton("Btn_Join", row.transform, "JOIN", new Vector2(75f, 32f), () =>
            {
                ExecuteJoinRoom(capturedRoom);
            }, ColorActiveGreen, new Color(0.05f, 0.15f, 0.05f));

            if (room.IsFull || room.IsMatchInProgress)
            {
                joinBtn.interactable = false;
            }

            return row;
        }
        #endregion

        #region Settings Management
        private void LoadAndApplySavedSettings()
        {
            float volume = PlayerPrefs.GetFloat("Scorched_MasterVolume", 1.0f);
            float sens = PlayerPrefs.GetFloat("Scorched_MouseSensitivity", 2.0f);
            float fov = PlayerPrefs.GetFloat("Scorched_FOV", 75.0f);

            AudioListener.volume = volume;
            if (_volumeSlider != null) _volumeSlider.value = volume;
            if (_volumeValueText != null) _volumeValueText.text = $"{Mathf.RoundToInt(volume * 100)}%";

            if (_sensitivitySlider != null) _sensitivitySlider.value = sens;
            if (_sensitivityValueText != null) _sensitivityValueText.text = sens.ToString("F1");

            if (_fovSlider != null) _fovSlider.value = fov;
            if (_fovValueText != null) _fovValueText.text = $"{Mathf.RoundToInt(fov)}°";

            ApplyPlayerSettings(sens, fov);
        }

        private void OnVolumeChanged(float value)
        {
            AudioListener.volume = value;
            if (_volumeValueText != null) _volumeValueText.text = $"{Mathf.RoundToInt(value * 100)}%";
            PlayerPrefs.SetFloat("Scorched_MasterVolume", value);
        }

        private void OnSensitivityChanged(float value)
        {
            if (_sensitivityValueText != null) _sensitivityValueText.text = value.ToString("F1");
            PlayerPrefs.SetFloat("Scorched_MouseSensitivity", value);
            ApplyPlayerSettings(value, _fovSlider != null ? _fovSlider.value : 75f);
        }

        private void OnFovChanged(float value)
        {
            if (_fovValueText != null) _fovValueText.text = $"{Mathf.RoundToInt(value)}°";
            PlayerPrefs.SetFloat("Scorched_FOV", value);
            ApplyPlayerSettings(_sensitivitySlider != null ? _sensitivitySlider.value : 2f, value);
        }

        private void ApplyPlayerSettings(float sensitivity, float fov)
        {
            var players = FindObjectsByType<PlayerMovement>();
            foreach (var p in players)
            {
                p.ApplySettings(sensitivity, fov);
            }
        }
        #endregion

        #region Procedural UI Building
        private void EnsureEventSystem()
        {
            var es = FindAnyObjectByType<EventSystem>();
            if (es == null)
            {
                var esGo = new GameObject("EventSystem");
                es = esGo.AddComponent<EventSystem>();
                esGo.AddComponent<InputSystemUIInputModule>();
                DontDestroyOnLoad(esGo);
            }
            else
            {
                var standalone = es.GetComponent<StandaloneInputModule>();
                if (standalone != null)
                {
                    Destroy(standalone);
                }
                if (es.GetComponent<InputSystemUIInputModule>() == null)
                {
                    es.gameObject.AddComponent<InputSystemUIInputModule>();
                }
            }
        }

        private void EnsureLANDiscovery()
        {
            if (LANRoomDiscovery.Instance == null && FindAnyObjectByType<LANRoomDiscovery>() == null)
            {
                var discGo = new GameObject("LANRoomDiscovery");
                discGo.AddComponent<LANRoomDiscovery>();
            }
        }

        private void BuildTacticalUI()
        {
            // 1. Root Canvas
            var canvasGo = new GameObject("ScorchedMainMenu_Canvas");
            DontDestroyOnLoad(canvasGo);
            _rootCanvas = canvasGo.AddComponent<Canvas>();
            _rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _rootCanvas.sortingOrder = 90; // High sorting order for clean overlay

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // Fullscreen backdrop overlay
            _overlayRoot = CreateUIObject("OverlayRoot", canvasGo.transform);
            var overlayRt = _overlayRoot.GetComponent<RectTransform>();
            overlayRt.anchorMin = Vector2.zero;
            overlayRt.anchorMax = Vector2.one;
            overlayRt.sizeDelta = Vector2.zero;

            var bgImg = _overlayRoot.AddComponent<Image>();
            bgImg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            bgImg.color = ColorBgDark;

            // 2. Build individual screens
            BuildMainMenuScreen(_overlayRoot.transform);
            BuildCreateRoomScreen(_overlayRoot.transform);
            BuildLobbyScreen(_overlayRoot.transform);
            BuildSettingsScreen(_overlayRoot.transform);
            BuildPauseMenuScreen(_overlayRoot.transform);
        }

        private void BuildMainMenuScreen(Transform parent)
        {
            _mainMenuPanel = CreateUIObject("Screen_MainMenu", parent);
            var rt = _mainMenuPanel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(420f, 370f);

            // Panel Background
            var bg = _mainMenuPanel.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = ColorPanelDark;

            var outline = _mainMenuPanel.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Title Header
            var titleGo = CreateUIObject("HeaderTitle", _mainMenuPanel.transform);
            var titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0.5f, 1f);
            titleRt.anchorMax = new Vector2(0.5f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -24f);
            titleRt.sizeDelta = new Vector2(380f, 44f);
            var titleTxt = titleGo.AddComponent<Text>();
            titleTxt.font = _tacticalFont;
            titleTxt.fontSize = 38;
            titleTxt.fontStyle = FontStyle.Bold;
            titleTxt.alignment = TextAnchor.MiddleCenter;
            titleTxt.color = ColorAccentAmber;
            titleTxt.text = "S C O R C H E D";

            // Divider Line
            CreateHorizontalLine("Divider", _mainMenuPanel.transform, new Vector2(0f, -74f), 350f, ColorBorderGreen);

            // Navigation Buttons Container (Top-anchored vertical stack)
            float startY = -96f;
            float btnHeight = 48f;
            float spacing = 14f;

            CreateStyledButton("Btn_CreateRoom", _mainMenuPanel.transform, "CREATE ROOM", new Vector2(340f, btnHeight), OpenCreateRoom)
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY);

            CreateStyledButton("Btn_Lobby", _mainMenuPanel.transform, "LOBBY", new Vector2(340f, btnHeight), OpenLobby)
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY - (btnHeight + spacing));

            CreateStyledButton("Btn_Settings", _mainMenuPanel.transform, "SETTINGS", new Vector2(340f, btnHeight), OpenSettings)
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY - (btnHeight + spacing) * 2);

            CreateStyledButton("Btn_Quit", _mainMenuPanel.transform, "QUIT", new Vector2(340f, btnHeight), ExecuteQuit, new Color(0.95f, 0.35f, 0.35f))
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY - (btnHeight + spacing) * 3);
        }

        private void BuildCreateRoomScreen(Transform parent)
        {
            _createRoomPanel = CreateUIObject("Screen_CreateRoom", parent);
            var rt = _createRoomPanel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(520f, 540f);

            var bg = _createRoomPanel.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = ColorPanelDark;

            var outline = _createRoomPanel.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Header
            CreateModalHeader("CreateRoomHeader", _createRoomPanel.transform, "// CREATE TACTICAL ROOM", 460f);

            // Form Fields
            float curY = -70f;

            // 1. Room Name
            CreateLabel("Lbl_RoomName", _createRoomPanel.transform, "OPERATION / ROOM NAME:", new Vector2(0f, curY), 440f);
            curY -= 24f;
            _roomNameInput = CreateInputField("Inp_RoomName", _createRoomPanel.transform, PlayerPrefs.GetString("Scorched_LastRoomName", "Scorched Match #1"), new Vector2(0f, curY), 440f, 36f);

            // 2. Engagement Game Mode (Default: Free For All vs Team Match)
            curY -= 46f;
            CreateLabel("Lbl_GameMode", _createRoomPanel.transform, "OPERATIONAL ENGAGEMENT MODE:", new Vector2(0f, curY), 440f);
            curY -= 24f;

            var modeSelGo = CreateUIObject("GameModeSel", _createRoomPanel.transform);
            var modeSelRt = modeSelGo.GetComponent<RectTransform>();
            modeSelRt.anchorMin = new Vector2(0.5f, 1f);
            modeSelRt.anchorMax = new Vector2(0.5f, 1f);
            modeSelRt.pivot = new Vector2(0.5f, 1f);
            modeSelRt.anchoredPosition = new Vector2(0f, curY);
            modeSelRt.sizeDelta = new Vector2(440f, 36f);

            var modeHlg = modeSelGo.AddComponent<HorizontalLayoutGroup>();
            modeHlg.spacing = 10;
            modeHlg.childControlWidth = true;
            modeHlg.childControlHeight = true;
            modeHlg.childForceExpandWidth = true;

            _btnModeFFA = CreateStyledButton("Btn_Mode_FFA", modeSelGo.transform, "FREE FOR ALL", new Vector2(215f, 36f), () =>
            {
                SetSelectedGameMode(Scorched.Combat.GameMode.FreeForAll);
            }, ColorActiveGreen, new Color(0.04f, 0.16f, 0.04f));

            _btnModeTeam = CreateStyledButton("Btn_Mode_Team", modeSelGo.transform, "TEAM MATCH (4v4)", new Vector2(215f, 36f), () =>
            {
                SetSelectedGameMode(Scorched.Combat.GameMode.TeamMatch);
            }, new Color(0.25f, 0.85f, 0.98f), ColorButtonNormal);

            curY -= 40f;
            var modeInfoGo = CreateUIObject("ModeInfo", _createRoomPanel.transform);
            var modeInfoRt = modeInfoGo.GetComponent<RectTransform>();
            modeInfoRt.anchorMin = new Vector2(0.5f, 1f);
            modeInfoRt.anchorMax = new Vector2(0.5f, 1f);
            modeInfoRt.pivot = new Vector2(0.5f, 1f);
            modeInfoRt.anchoredPosition = new Vector2(0f, curY);
            modeInfoRt.sizeDelta = new Vector2(440f, 26f);

            _gameModeInfoText = modeInfoGo.AddComponent<Text>();
            _gameModeInfoText.font = _tacticalFont;
            _gameModeInfoText.fontSize = 11;
            _gameModeInfoText.alignment = TextAnchor.MiddleCenter;
            _gameModeInfoText.color = ColorActiveGreen;
            _gameModeInfoText.supportRichText = true;

            // 3. Max Players
            curY -= 36f;
            CreateLabel("Lbl_MaxPlayers", _createRoomPanel.transform, "MAX COMBATANTS:", new Vector2(0f, curY), 440f);
            curY -= 24f;

            // Max player selector container
            var plySelGo = CreateUIObject("MaxPlayerSel", _createRoomPanel.transform);
            var plySelRt = plySelGo.GetComponent<RectTransform>();
            plySelRt.anchorMin = new Vector2(0.5f, 1f);
            plySelRt.anchorMax = new Vector2(0.5f, 1f);
            plySelRt.pivot = new Vector2(0.5f, 1f);
            plySelRt.anchoredPosition = new Vector2(0f, curY);
            plySelRt.sizeDelta = new Vector2(440f, 34f);
            var hlg = plySelGo.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;

            int[] playerOptions = new int[] { 2, 4, 6, 8 };
            foreach (int count in playerOptions)
            {
                int c = count;
                CreateStyledButton($"Btn_Count_{c}", plySelGo.transform, $"{c} Players", new Vector2(95f, 34f), () =>
                {
                    _selectedMaxPlayers = c;
                    UpdateMaxPlayersDisplay();
                });
            }

            curY -= 38f;
            var maxInfoGo = CreateUIObject("MaxInfo", _createRoomPanel.transform);
            var maxInfoRt = maxInfoGo.GetComponent<RectTransform>();
            maxInfoRt.anchorMin = new Vector2(0.5f, 1f);
            maxInfoRt.anchorMax = new Vector2(0.5f, 1f);
            maxInfoRt.pivot = new Vector2(0.5f, 1f);
            maxInfoRt.anchoredPosition = new Vector2(0f, curY);
            maxInfoRt.sizeDelta = new Vector2(440f, 20f);
            _maxPlayersLabel = maxInfoGo.AddComponent<Text>();
            _maxPlayersLabel.font = _tacticalFont;
            _maxPlayersLabel.fontSize = 12;
            _maxPlayersLabel.alignment = TextAnchor.MiddleCenter;
            _maxPlayersLabel.color = ColorActiveGreen;

            // 4. Port
            curY -= 28f;
            CreateLabel("Lbl_Port", _createRoomPanel.transform, "NETWORK PORT:", new Vector2(0f, curY), 440f);
            curY -= 22f;
            _createPortInput = CreateInputField("Inp_Port", _createRoomPanel.transform, PlayerPrefs.GetString("Scorched_LastPort", "7777"), new Vector2(0f, curY), 440f, 34f);

            // 5. Actions: Host & Back
            curY -= 50f;
            CreateStyledButton("Btn_Back", _createRoomPanel.transform, "BACK", new Vector2(130f, 44f), () => ShowPanel(_mainMenuPanel))
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(-115f, curY);

            CreateStyledButton("Btn_HostDeploy", _createRoomPanel.transform, "HOST & DEPLOY", new Vector2(240f, 44f), ExecuteHostRoom, ColorActiveGreen, new Color(0.04f, 0.14f, 0.04f))
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(100f, curY);

            SetSelectedGameMode(Scorched.Combat.GameMode.FreeForAll);
            _createRoomPanel.SetActive(false);
        }

        private void SetSelectedGameMode(Scorched.Combat.GameMode mode)
        {
            _selectedGameMode = mode;
            UpdateGameModeDisplay();
        }

        private void UpdateGameModeDisplay()
        {
            if (_btnModeFFA != null)
            {
                var img = _btnModeFFA.GetComponent<Image>();
                if (img != null)
                {
                    img.color = _selectedGameMode == Scorched.Combat.GameMode.FreeForAll ? new Color(0.18f, 0.32f, 0.18f, 1f) : ColorButtonNormal;
                }
            }

            if (_btnModeTeam != null)
            {
                var img = _btnModeTeam.GetComponent<Image>();
                if (img != null)
                {
                    img.color = _selectedGameMode == Scorched.Combat.GameMode.TeamMatch ? new Color(0.12f, 0.28f, 0.36f, 1f) : ColorButtonNormal;
                }
            }

            if (_gameModeInfoText != null)
            {
                if (_selectedGameMode == Scorched.Combat.GameMode.FreeForAll)
                {
                    _gameModeInfoText.color = ColorActiveGreen;
                    _gameModeInfoText.text = "<b>[FREE FOR ALL]</b> Solo deployment • Instant active combat upon deployment.";
                }
                else
                {
                    _gameModeInfoText.color = new Color(0.25f, 0.85f, 0.98f);
                    _gameModeInfoText.text = "<b>[TEAM MATCH]</b> Alpha vs Bravo • Combat starts only when teams are EQUAL.";
                }
            }

            UpdateMaxPlayersDisplay();
        }

        private void UpdateMaxPlayersDisplay()
        {
            if (_maxPlayersLabel != null)
            {
                if (_selectedGameMode == Scorched.Combat.GameMode.FreeForAll)
                {
                    _maxPlayersLabel.text = $"Selected: <b>{_selectedMaxPlayers} Players</b> (Free For All Battle)";
                }
                else
                {
                    int half = _selectedMaxPlayers / 2;
                    _maxPlayersLabel.text = $"Selected: <b>{_selectedMaxPlayers} Players</b> ({half}v{half} Squads: Team Alpha vs Team Bravo)";
                }
            }
        }

        private void BuildLobbyScreen(Transform parent)
        {
            _lobbyPanel = CreateUIObject("Screen_Lobby", parent);
            var rt = _lobbyPanel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(820f, 620f);

            var bg = _lobbyPanel.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = ColorPanelDark;

            var outline = _lobbyPanel.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Header
            CreateModalHeader("LobbyHeader", _lobbyPanel.transform, "// TACTICAL OPERATIONS BROWSER (LAN / LOCAL)", 760f);

            // Status bar
            var statGo = CreateUIObject("LobbyStatus", _lobbyPanel.transform);
            var statRt = statGo.GetComponent<RectTransform>();
            statRt.anchoredPosition = new Vector2(0f, -74f);
            statRt.sizeDelta = new Vector2(760f, 24f);
            _lobbyStatusText = statGo.AddComponent<Text>();
            _lobbyStatusText.font = _tacticalFont;
            _lobbyStatusText.fontSize = 12;
            _lobbyStatusText.alignment = TextAnchor.MiddleLeft;
            _lobbyStatusText.color = ColorTextMuted;
            _lobbyStatusText.text = "Scanning local broadcast network on UDP 47777...";

            // Table Header Bar
            var tblHdrGo = CreateUIObject("TableHeader", _lobbyPanel.transform);
            var tblHdrRt = tblHdrGo.GetComponent<RectTransform>();
            tblHdrRt.anchorMin = new Vector2(0.5f, 1f);
            tblHdrRt.anchorMax = new Vector2(0.5f, 1f);
            tblHdrRt.pivot = new Vector2(0.5f, 1f);
            tblHdrRt.anchoredPosition = new Vector2(0f, -96f);
            tblHdrRt.sizeDelta = new Vector2(760f, 28f);

            var tblHdrBg = tblHdrGo.AddComponent<Image>();
            tblHdrBg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            tblHdrBg.color = new Color(0.14f, 0.19f, 0.14f, 0.9f);

            var tblHdrHlg = tblHdrGo.AddComponent<HorizontalLayoutGroup>();
            tblHdrHlg.padding = new RectOffset(16, 12, 4, 4);
            tblHdrHlg.spacing = 12;
            tblHdrHlg.childControlWidth = false;
            tblHdrHlg.childControlHeight = true;

            CreateHeaderColumn(tblHdrGo.transform, "OPERATION NAME", 210f);
            CreateHeaderColumn(tblHdrGo.transform, "MODE", 75f);
            CreateHeaderColumn(tblHdrGo.transform, "PLAYERS", 75f);
            CreateHeaderColumn(tblHdrGo.transform, "HOST ENDPOINT", 150f);
            CreateHeaderColumn(tblHdrGo.transform, "STATUS", 75f);
            CreateHeaderColumn(tblHdrGo.transform, "ACTION", 75f);

            // Scroll View for Room List
            var scrollGo = CreateUIObject("RoomScrollView", _lobbyPanel.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0.5f, 1f);
            scrollRt.anchorMax = new Vector2(0.5f, 1f);
            scrollRt.pivot = new Vector2(0.5f, 1f);
            scrollRt.anchoredPosition = new Vector2(0f, -128f);
            scrollRt.sizeDelta = new Vector2(760f, 230f);

            var scrollBg = scrollGo.AddComponent<Image>();
            scrollBg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            scrollBg.color = new Color(0.02f, 0.03f, 0.02f, 0.8f);

            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // Viewport
            var viewGo = CreateUIObject("Viewport", scrollGo.transform);
            var viewRt = viewGo.GetComponent<RectTransform>();
            viewRt.anchorMin = Vector2.zero;
            viewRt.anchorMax = Vector2.one;
            viewRt.sizeDelta = Vector2.zero;
            viewGo.AddComponent<RectMask2D>();
            scrollRect.viewport = viewRt;

            // Content Container
            var contentGo = CreateUIObject("Content", viewGo.transform);
            _roomListContent = contentGo.transform;
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = new Vector2(0f, 0f);

            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.padding = new RectOffset(6, 6, 6, 6);
            contentVlg.spacing = 6;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = false;
            contentVlg.childForceExpandWidth = true;

            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = contentRt;

            // Direct Connect Section
            float curY = -372f;
            CreateHorizontalLine("DividerDirect", _lobbyPanel.transform, new Vector2(0f, curY), 760f, ColorBorderGreen);

            curY -= 24f;
            CreateLabel("Lbl_DirectConnect", _lobbyPanel.transform, "DIRECT IP CONNECT FALLBACK:", new Vector2(-180f, curY), 380f);

            curY -= 28f;
            var directBarGo = CreateUIObject("DirectConnectBar", _lobbyPanel.transform);
            var directBarRt = directBarGo.GetComponent<RectTransform>();
            directBarRt.anchorMin = new Vector2(0.5f, 1f);
            directBarRt.anchorMax = new Vector2(0.5f, 1f);
            directBarRt.pivot = new Vector2(0.5f, 1f);
            directBarRt.anchoredPosition = new Vector2(0f, curY);
            directBarRt.sizeDelta = new Vector2(760f, 38f);

            var directHlg = directBarGo.AddComponent<HorizontalLayoutGroup>();
            directHlg.spacing = 10;
            directHlg.childControlWidth = false;
            directHlg.childControlHeight = true;

            // Direct IP Input
            _directIpInput = CreateInputField("Inp_DirectIp", directBarGo.transform, PlayerPrefs.GetString("Scorched_LastDirectIp", "127.0.0.1"), Vector2.zero, 280f, 38f);
            // Direct Port Input
            _directPortInput = CreateInputField("Inp_DirectPort", directBarGo.transform, PlayerPrefs.GetString("Scorched_LastPort", "7777"), Vector2.zero, 120f, 38f);

            // Connect button
            CreateStyledButton("Btn_DirectJoin", directBarGo.transform, "CONNECT", new Vector2(130f, 38f), ExecuteDirectConnect, ColorActiveGreen, new Color(0.04f, 0.14f, 0.04f));

            // Refresh & Back bottom bar
            curY -= 56f;
            CreateStyledButton("Btn_Refresh", _lobbyPanel.transform, "REFRESH LIST", new Vector2(160f, 44f), () =>
            {
                if (LANRoomDiscovery.Instance != null)
                {
                    LANRoomDiscovery.Instance.RefreshRooms();
                }
            }).GetComponent<RectTransform>().anchoredPosition = new Vector2(180f, curY);

            CreateStyledButton("Btn_LobbyBack", _lobbyPanel.transform, "BACK TO MENU", new Vector2(160f, 44f), () =>
            {
                if (LANRoomDiscovery.Instance != null)
                {
                    LANRoomDiscovery.Instance.StopListening();
                }
                ShowPanel(_mainMenuPanel);
            }).GetComponent<RectTransform>().anchoredPosition = new Vector2(-180f, curY);

            _lobbyPanel.SetActive(false);
        }

        private void CreateHeaderColumn(Transform parent, string title, float width)
        {
            var colGo = CreateUIObject($"ColHeader_{title}", parent);
            var rt = colGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, 24f);
            var txt = colGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 11;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = ColorAccentAmber;
            txt.text = title;
        }

        private void BuildSettingsScreen(Transform parent)
        {
            _settingsPanel = CreateUIObject("Screen_Settings", parent);
            var rt = _settingsPanel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(560f, 480f);

            var bg = _settingsPanel.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = ColorPanelDark;

            var outline = _settingsPanel.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Header
            CreateModalHeader("SettingsHeader", _settingsPanel.transform, "// OPERATIONAL SETTINGS");

            float curY = -90f;

            // Master Volume
            CreateLabel("Lbl_Vol", _settingsPanel.transform, "MASTER AUDIO VOLUME:", new Vector2(-120f, curY), 240f);
            _volumeValueText = CreateValueLabel("Val_Vol", _settingsPanel.transform, "100%", new Vector2(170f, curY));
            curY -= 26f;
            _volumeSlider = CreateSlider("Sld_Volume", _settingsPanel.transform, new Vector2(0f, curY), 440f, 0f, 1f, OnVolumeChanged);

            // Mouse Sensitivity
            curY -= 50f;
            CreateLabel("Lbl_Sens", _settingsPanel.transform, "MOUSE LOOK SENSITIVITY:", new Vector2(-120f, curY), 240f);
            _sensitivityValueText = CreateValueLabel("Val_Sens", _settingsPanel.transform, "2.0", new Vector2(170f, curY));
            curY -= 26f;
            _sensitivitySlider = CreateSlider("Sld_Sensitivity", _settingsPanel.transform, new Vector2(0f, curY), 440f, 0.5f, 5.0f, OnSensitivityChanged);

            // FOV
            curY -= 50f;
            CreateLabel("Lbl_FOV", _settingsPanel.transform, "FIELD OF VIEW (FOV):", new Vector2(-120f, curY), 240f);
            _fovValueText = CreateValueLabel("Val_FOV", _settingsPanel.transform, "75°", new Vector2(170f, curY));
            curY -= 26f;
            _fovSlider = CreateSlider("Sld_FOV", _settingsPanel.transform, new Vector2(0f, curY), 440f, 60f, 90f, OnFovChanged);

            // Back button
            curY -= 70f;
            CreateStyledButton("Btn_SettingsBack", _settingsPanel.transform, "APPLY & CLOSE", new Vector2(220f, 44f), () =>
            {
                PlayerPrefs.Save();
                if (_isPauseMenuOpen)
                {
                    ShowPanel(_pauseMenuPanel);
                }
                else
                {
                    ShowPanel(_mainMenuPanel);
                }
            }).GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, curY);

            _settingsPanel.SetActive(false);
        }

        private void BuildPauseMenuScreen(Transform parent)
        {
            _pauseMenuPanel = CreateUIObject("Screen_PauseMenu", parent);
            var rt = _pauseMenuPanel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(460f, 420f);

            var bg = _pauseMenuPanel.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = ColorPanelDark;

            var outline = _pauseMenuPanel.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Header
            CreateModalHeader("PauseHeader", _pauseMenuPanel.transform, "// MATCH PAUSED");

            // Telemetry / Status
            var infoGo = CreateUIObject("MatchInfo", _pauseMenuPanel.transform);
            var infoRt = infoGo.GetComponent<RectTransform>();
            infoRt.anchorMin = new Vector2(0.5f, 1f);
            infoRt.anchorMax = new Vector2(0.5f, 1f);
            infoRt.pivot = new Vector2(0.5f, 1f);
            infoRt.anchoredPosition = new Vector2(0f, -68f);
            infoRt.sizeDelta = new Vector2(380f, 22f);
            _pauseMatchInfoText = infoGo.AddComponent<Text>();
            _pauseMatchInfoText.font = _tacticalFont;
            _pauseMatchInfoText.fontSize = 11;
            _pauseMatchInfoText.alignment = TextAnchor.MiddleCenter;
            _pauseMatchInfoText.color = ColorTextMuted;
            _pauseMatchInfoText.text = "STATUS: ACTIVE COMBAT";

            CreateHorizontalLine("PauseDivider", _pauseMenuPanel.transform, new Vector2(0f, -94f), 360f, ColorBorderGreen);

            // Buttons
            float startY = -110f;
            float btnHeight = 44f;
            float spacing = 12f;

            CreateStyledButton("Btn_Resume", _pauseMenuPanel.transform, "RESUME MATCH", new Vector2(320f, btnHeight), ResumeGame)
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY);

            CreateStyledButton("Btn_PauseSettings", _pauseMenuPanel.transform, "SETTINGS", new Vector2(320f, btnHeight), OpenSettings)
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY - (btnHeight + spacing));

            CreateStyledButton("Btn_Disconnect", _pauseMenuPanel.transform, "DISCONNECT TO MENU", new Vector2(320f, btnHeight), DisconnectSession, new Color(0.9f, 0.3f, 0.3f))
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, startY - (btnHeight + spacing) * 2);

            _pauseMenuPanel.SetActive(false);
        }
        #endregion

        #region UI Component Helpers
        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Font GetDefaultFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return font;
        }

        private void CreateModalHeader(string name, Transform parent, string title, float width = 500f)
        {
            var hdrGo = CreateUIObject(name, parent);
            var rt = hdrGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -20f);
            rt.sizeDelta = new Vector2(width, 36f);

            var txt = hdrGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 20;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = ColorAccentAmber;
            txt.text = title;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            CreateHorizontalLine($"{name}_Line", parent, new Vector2(0f, -58f), width + 20f, ColorBorderGreen);
        }

        private void CreateHorizontalLine(string name, Transform parent, Vector2 pos, float width, Color color)
        {
            var lineGo = CreateUIObject(name, parent);
            var rt = lineGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 1.5f);

            var img = lineGo.AddComponent<Image>();
            img.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            img.color = color;
        }

        private void CreateLabel(string name, Transform parent, string text, Vector2 pos, float width)
        {
            var lblGo = CreateUIObject(name, parent);
            var rt = lblGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 22f);

            var txt = lblGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 12;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = ColorTextMuted;
            txt.text = text;
        }

        private Text CreateValueLabel(string name, Transform parent, string text, Vector2 pos)
        {
            var lblGo = CreateUIObject(name, parent);
            var rt = lblGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(100f, 22f);

            var txt = lblGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 12;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleRight;
            txt.color = ColorActiveGreen;
            txt.text = text;
            return txt;
        }

        private Button CreateStyledButton(string name, Transform parent, string text, Vector2 size, Action onClick, Color? textColor = null, Color? bgColor = null)
        {
            var btnGo = CreateUIObject(name, parent);
            var rt = btnGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = size;

            var img = btnGo.AddComponent<Image>();
            img.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            img.type = Image.Type.Sliced;
            img.color = bgColor ?? ColorButtonNormal;

            var outline = btnGo.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1f, -1f);

            var btn = btnGo.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = bgColor ?? ColorButtonNormal;
            colors.highlightedColor = ColorButtonHover;
            colors.pressedColor = ColorButtonPressed;
            colors.disabledColor = new Color(0.1f, 0.12f, 0.1f, 0.5f);
            btn.colors = colors;

            if (onClick != null)
            {
                btn.onClick.AddListener(() => onClick());
            }

            var txtGo = CreateUIObject("Text", btnGo.transform);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.sizeDelta = Vector2.zero;

            var txt = txtGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 13;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = textColor ?? ColorTextWhite;
            txt.text = text;

            return btn;
        }

        private InputField CreateInputField(string name, Transform parent, string defaultText, Vector2 pos, float width, float height)
        {
            var inputGo = CreateUIObject(name, parent);
            var rt = inputGo.GetComponent<RectTransform>();
            if (pos != Vector2.zero)
            {
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = pos;
            }
            rt.sizeDelta = new Vector2(width, height);

            var img = inputGo.AddComponent<Image>();
            img.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            img.type = Image.Type.Sliced;
            img.color = ColorInputBg;

            var outline = inputGo.AddComponent<Outline>();
            outline.effectColor = ColorBorderGreen;
            outline.effectDistance = new Vector2(1f, -1f);

            // Text component
            var txtGo = CreateUIObject("Text", inputGo.transform);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(10f, 2f);
            txtRt.offsetMax = new Vector2(-10f, -2f);

            var txt = txtGo.AddComponent<Text>();
            txt.font = _tacticalFont;
            txt.fontSize = 13;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = ColorTextWhite;

            // Placeholder component
            var phGo = CreateUIObject("Placeholder", inputGo.transform);
            var phRt = phGo.GetComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(10f, 2f);
            phRt.offsetMax = new Vector2(-10f, -2f);

            var phTxt = phGo.AddComponent<Text>();
            phTxt.font = _tacticalFont;
            phTxt.fontSize = 13;
            phTxt.fontStyle = FontStyle.Italic;
            phTxt.alignment = TextAnchor.MiddleLeft;
            phTxt.color = new Color(0.4f, 0.5f, 0.4f, 0.7f);
            phTxt.text = "Enter text...";

            var inputField = inputGo.AddComponent<InputField>();
            inputField.textComponent = txt;
            inputField.placeholder = phTxt;
            inputField.text = defaultText;

            return inputField;
        }

        private Slider CreateSlider(string name, Transform parent, Vector2 pos, float width, float min, float max, Action<float> onValueChanged)
        {
            var sliderGo = CreateUIObject(name, parent);
            var rt = sliderGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 22f);

            // Background track
            var bgGo = CreateUIObject("Background", sliderGo.transform);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 0.35f);
            bgRt.anchorMax = new Vector2(1f, 0.65f);
            bgRt.sizeDelta = Vector2.zero;
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            bgImg.color = new Color(0.12f, 0.16f, 0.12f, 1.0f);

            // Fill area
            var fillArea = CreateUIObject("Fill Area", sliderGo.transform);
            var faRt = fillArea.GetComponent<RectTransform>();
            faRt.anchorMin = new Vector2(0f, 0.35f);
            faRt.anchorMax = new Vector2(1f, 0.65f);
            faRt.offsetMin = new Vector2(5f, 0f);
            faRt.offsetMax = new Vector2(-5f, 0f);

            var fillGo = CreateUIObject("Fill", fillArea.transform);
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.sizeDelta = Vector2.zero;
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            fillImg.color = ColorActiveGreen;

            // Handle area
            var handleArea = CreateUIObject("Handle Slide Area", sliderGo.transform);
            var haRt = handleArea.GetComponent<RectTransform>();
            haRt.anchorMin = Vector2.zero;
            haRt.anchorMax = Vector2.one;
            haRt.offsetMin = new Vector2(10f, 0f);
            haRt.offsetMax = new Vector2(-10f, 0f);

            var handleGo = CreateUIObject("Handle", handleArea.transform);
            var handleRt = handleGo.GetComponent<RectTransform>();
            handleRt.sizeDelta = new Vector2(16f, 22f);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            handleImg.type = Image.Type.Sliced;
            handleImg.color = ColorAccentAmber;

            var slider = sliderGo.AddComponent<Slider>();
            slider.fillRect = fillRt;
            slider.handleRect = handleRt;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;

            if (onValueChanged != null)
            {
                slider.onValueChanged.AddListener(v => onValueChanged(v));
            }

            return slider;
        }
        #endregion
    }
}
