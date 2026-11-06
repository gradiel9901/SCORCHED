using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Scorched.Player;

namespace Scorched.UI
{
    /// <summary>
    /// Streamlined Camera & Aim HUD for "Scorched".
    /// Removes all extraneous gameplay UI (health bars, squad portraits, inventory slots, ammo counts)
    /// leaving only the dynamic camera crosshair and an interactive visual guide on how the camera works.
    /// </summary>
    [DisallowMultipleComponent]
    public class L4DHUDManager : MonoBehaviour
    {
        public static L4DHUDManager Instance { get; private set; }

        #region UI Colors
        public static readonly Color L4DGreen = new Color(0.23f, 0.92f, 0.23f, 1.0f);      // Vibrant Green (Active/Locked)
        public static readonly Color L4DYellow = new Color(0.95f, 0.78f, 0.24f, 1.0f);     // Amber Warning (Unlocked)
        public static readonly Color L4DRed = new Color(0.95f, 0.24f, 0.24f, 1.0f);        // Critical Red
        public static readonly Color L4DCyan = new Color(0.22f, 0.83f, 0.96f, 1.0f);       // Tactical Cyan (Stamina)
        public static readonly Color L4DPanelDark = new Color(0.06f, 0.08f, 0.06f, 0.88f); // Translucent Dark Panel
        public static readonly Color L4DBorder = new Color(0.25f, 0.45f, 0.25f, 0.75f);    // Panel Outline
        public static readonly Color L4DTextDim = new Color(0.72f, 0.78f, 0.72f, 0.95f);   // Secondary Text
        #endregion

        #region Serialized Settings
        [Header("Crosshair Dynamics (Camera Focal Reticle)")]
        [SerializeField] private float _crosshairBaseGap = 8.0f;
        [SerializeField] private float _crosshairJogGap = 16.0f;
        [SerializeField] private float _crosshairRunGap = 26.0f;
        [SerializeField] private float _crosshairFireKick = 12.0f;
        [SerializeField] private float _crosshairReturnSpeed = 14.0f;

        [Header("Camera Guide Display")]
        [SerializeField] private bool _showCameraGuide = false;
        [SerializeField] private Key _toggleGuideKey = Key.H;
        #endregion

        #region Runtime References
        private PlayerMovement _localPlayerMovement;
        private Scorched.Weapons.NetworkWeaponController _localWeaponController;
        private Canvas _hudCanvas;

        // Crosshair ticks
        private RectTransform _crosshairTop;
        private RectTransform _crosshairBottom;
        private RectTransform _crosshairLeft;
        private RectTransform _crosshairRight;
        private float _crosshairCurrentSpread;
        private float _recoilPulse = 0f;

        // Camera Guide UI
        private GameObject _guidePanelRoot;
        private Text _cursorStatusText;
        private Text _cameraStatsText;
        private GameObject _centerLockPrompt;

        // Match State & Equal-Teams Banner
        private GameObject _matchBannerRoot;
        private Text _matchBannerTitle;
        private Text _matchBannerSubtitle;

        // Vitals UI (HP & Stamina - Bottom Left)
        private GameObject _vitalsPanelRoot;
        private RectTransform _healthBarFill;
        private Image _healthBarImage;
        private Text _healthValueText;
        private RectTransform _staminaBarFill;
        private Image _staminaBarImage;
        private Text _staminaValueText;

        // Ammo UI (Bottom Right)
        private GameObject _ammoPanelRoot;
        private Text _weaponNameText;
        private Text _ammoMagText;
        private Text _ammoReserveText;
        private Text _ammoStatusText;

        // Runtime Vitals Cache
        private float _cachedHealth = 100f;
        private float _cachedMaxHealth = 100f;
        private float _cachedStamina = 100f;
        private float _cachedMaxStamina = 100f;
        private int _cachedMagAmmo = 30;
        private int _cachedReserveAmmo = 120;
        private bool _cachedIsReloading = false;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _crosshairCurrentSpread = _crosshairBaseGap;
            BuildCameraHUD();
        }

        private void Start()
        {
            LocateLocalPlayer();
        }

        private void Update()
        {
            if (MainMenuUIManager.IsMenuOpen)
            {
                if (_hudCanvas != null && _hudCanvas.enabled)
                {
                    _hudCanvas.enabled = false;
                }
                return;
            }

            if (_hudCanvas != null && !_hudCanvas.enabled)
            {
                _hudCanvas.enabled = true;
            }

            HandleGuideToggleInput();
            UpdateCrosshairDynamics();
            UpdateCameraGuideTelemetry();
            UpdateMatchBannerTelemetry();
        }
        #endregion

        #region Player Linking & Combat Events
        private void LocateLocalPlayer()
        {
            if (_localPlayerMovement == null)
            {
                var players = FindObjectsByType<PlayerMovement>();
                foreach (var p in players)
                {
                    if (!p.IsSpawned || p.IsOwner)
                    {
                        _localPlayerMovement = p;
                        _localWeaponController = p.GetComponent<Scorched.Weapons.NetworkWeaponController>();
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Called when the player triggers weapon fire. Pulses the camera crosshair reticle.
        /// </summary>
        public void OnWeaponFired()
        {
            _recoilPulse = _crosshairFireKick;
        }

        public void UpdateHealth(float currentHealth, float maxHealth)
        {
            _cachedHealth = Mathf.Max(0f, currentHealth);
            _cachedMaxHealth = Mathf.Max(1f, maxHealth);
            RefreshVitalsUI();
        }

        public void UpdateStamina(float currentStamina, float maxStamina)
        {
            _cachedStamina = Mathf.Max(0f, currentStamina);
            _cachedMaxStamina = Mathf.Max(1f, maxStamina);
            RefreshVitalsUI();
        }

        public void UpdateAmmo(int mag, int reserve, bool isReloading, string weaponName = "AK-47 TACTICAL")
        {
            _cachedMagAmmo = mag;
            _cachedReserveAmmo = reserve;
            _cachedIsReloading = isReloading;
            RefreshAmmoUI(weaponName);
        }

        // Safe stubs for compatibility with any legacy callers
        public void SelectSlot(int index) { }
        public void ReloadCurrentWeapon() { }
        public void ApplyDamage(int amount)
        {
            _cachedHealth = Mathf.Max(0f, _cachedHealth - amount);
            RefreshVitalsUI();
        }
        public void Heal(int amount)
        {
            _cachedHealth = Mathf.Min(_cachedMaxHealth, _cachedHealth + amount);
            RefreshVitalsUI();
        }
        #endregion

        #region Input Handling
        private void HandleGuideToggleInput()
        {
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            if (keyboard != null && keyboard[_toggleGuideKey].wasPressedThisFrame)
            {
                _showCameraGuide = !_showCameraGuide;
                if (_guidePanelRoot != null)
                {
                    _guidePanelRoot.SetActive(_showCameraGuide);
                }
            }
        }
        #endregion

        #region Crosshair Dynamics
        private void UpdateCrosshairDynamics()
        {
            if (_localPlayerMovement == null)
            {
                LocateLocalPlayer();
            }

            float targetGap = _crosshairBaseGap;
            if (_localPlayerMovement != null)
            {
                var state = _localPlayerMovement.CurrentWeaponPoseState;
                if (state == WeaponPoseState.Running || state == WeaponPoseState.SprintJump)
                {
                    targetGap = _crosshairRunGap;
                }
                else if (state == WeaponPoseState.Jog || state == WeaponPoseState.Jumping)
                {
                    targetGap = _crosshairJogGap;
                }
                else if (state == WeaponPoseState.FiringRifle)
                {
                    targetGap = _crosshairJogGap + _crosshairFireKick * 0.5f;
                }
            }

            // Apply recoil pulse and decay
            if (_recoilPulse > 0.01f)
            {
                targetGap += _recoilPulse;
                _recoilPulse = Mathf.MoveTowards(_recoilPulse, 0f, Time.deltaTime * _crosshairReturnSpeed * 1.5f);
            }

            _crosshairCurrentSpread = Mathf.Lerp(_crosshairCurrentSpread, targetGap, Time.deltaTime * _crosshairReturnSpeed);

            if (_crosshairTop != null)
            {
                _crosshairTop.anchoredPosition = new Vector2(0f, _crosshairCurrentSpread + 6f);
                _crosshairBottom.anchoredPosition = new Vector2(0f, -(_crosshairCurrentSpread + 6f));
                _crosshairLeft.anchoredPosition = new Vector2(-(_crosshairCurrentSpread + 6f), 0f);
                _crosshairRight.anchoredPosition = new Vector2(_crosshairCurrentSpread + 6f, 0f);
            }
        }
        #endregion

        #region Camera Telemetry & Guide Updates
        private void UpdateCameraGuideTelemetry()
        {
            if (_localPlayerMovement == null)
            {
                LocateLocalPlayer();
            }

            bool isLocked = Cursor.lockState == CursorLockMode.Locked;

            // Update center unlock hint
            if (_centerLockPrompt != null)
            {
                _centerLockPrompt.SetActive(!isLocked);
            }

            // Update guide panel telemetry
            if (_cursorStatusText != null)
            {
                if (isLocked)
                {
                    _cursorStatusText.text = "<b>Status:</b> <color=#3BEB3B>LOCKED (Camera Active)</color>";
                }
                else
                {
                    _cursorStatusText.text = "<b>Status:</b> <color=#E8C73D>UNLOCKED (Click to Aim)</color>";
                }
            }

            if (_cameraStatsText != null)
            {
                float pitch = _localPlayerMovement != null ? _localPlayerMovement.CameraPitch : 0f;
                float fov = _localPlayerMovement != null ? _localPlayerMovement.FieldOfView : 75f;
                float sensX = _localPlayerMovement != null ? _localPlayerMovement.MouseSensitivityX : 2f;
                float sensY = _localPlayerMovement != null ? _localPlayerMovement.MouseSensitivityY : 2f;

                _cameraStatsText.text = $"Pitch: <b>{pitch:+0.0;-0.0;0.0}°</b> (Limits: [-85°, +85°])\n" +
                                        $"FOV: <b>{fov:F0}°</b> | Sensitivity: <b>X:{sensX:F1} Y:{sensY:F1}</b>\n" +
                                        $"Tracking: <b>True First-Person (Eye-Level 1:1)</b>";
            }
        }

        private void UpdateMatchBannerTelemetry()
        {
            if (_matchBannerRoot == null) return;

            var matchMgr = Scorched.Combat.NetworkMatchManager.Instance;
            if (matchMgr == null)
            {
                _matchBannerRoot.SetActive(false);
                return;
            }

            _matchBannerRoot.SetActive(true);

            if (matchMgr.IsFreeForAll)
            {
                _matchBannerTitle.color = L4DGreen;
                _matchBannerTitle.text = "[ FREE FOR ALL // COMBAT ACTIVE ]";
                _matchBannerSubtitle.text = "Solo engagement rules • Neutralize all hostiles";
            }
            else // Team Match
            {
                int alpha = matchMgr.TeamAlphaCount;
                int bravo = matchMgr.TeamBravoCount;
                int myTeam = matchMgr.LocalPlayerTeam;
                string teamStr = (myTeam == 0) ? "<color=#38D4F5>[TEAM ALPHA]</color>" : (myTeam == 1) ? "<color=#F5A623>[TEAM BRAVO]</color>" : "[UNASSIGNED]";

                if (matchMgr.IsCombatActive)
                {
                    _matchBannerTitle.color = L4DGreen;
                    _matchBannerTitle.text = $"[ TEAM MATCH // ALPHA: {alpha} vs BRAVO: {bravo} - COMBAT ACTIVE ]";
                    _matchBannerSubtitle.text = $"Assigned Squad: {teamStr} • Friendly fire disabled";
                }
                else
                {
                    _matchBannerTitle.color = L4DYellow;
                    _matchBannerTitle.text = $"[ WAITING FOR EQUAL TEAMS // ALPHA: {alpha} | BRAVO: {bravo} - WEAPONS SAFE ]";
                    _matchBannerSubtitle.text = $"Assigned Squad: {teamStr} • Combat begins when teams are equal (1v1, 2v2, 3v3, 4v4)";
                }
            }
        }
        #endregion

        #region UI Builder
        private void BuildCameraHUD()
        {
            // 1. Root Canvas
            var canvasGo = new GameObject("ScorchedCamera_HUD_Canvas");
            _hudCanvas = canvasGo.AddComponent<Canvas>();
            _hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _hudCanvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // 2. Center Dynamic Crosshair
            BuildCrosshair(canvasGo.transform);

            // 3. Center Lock Hint (visible when mouse is free)
            BuildCenterLockPrompt(canvasGo.transform);

            // 4. "How the Camera Works" Guide Panel
            BuildCameraGuide(canvasGo.transform);

            // 5. Match State & Equal-Teams Banner
            BuildMatchBanner(canvasGo.transform);

            // 6. Player Vitals (HP & Stamina - Bottom Left)
            BuildVitalsPanel(canvasGo.transform);

            // 7. Tactical Ammo & Weapon Status (Bottom Right)
            BuildAmmoPanel(canvasGo.transform);

            // Initial UI Refresh
            RefreshVitalsUI();
            RefreshAmmoUI("AK-47 TACTICAL");
        }

        private void BuildCrosshair(Transform parent)
        {
            var crosshairRoot = CreateUIObject("CrosshairRoot", parent);
            var rect = crosshairRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(40, 40);

            var lineSprite = ProceduralUISprites.GetSolidWhiteSprite();

            _crosshairTop = CreateCrosshairTick("Tick_Top", crosshairRoot.transform, lineSprite, new Vector2(3f, 12f), new Vector2(0f, 12f));
            _crosshairBottom = CreateCrosshairTick("Tick_Bottom", crosshairRoot.transform, lineSprite, new Vector2(3f, 12f), new Vector2(0f, -12f));
            _crosshairLeft = CreateCrosshairTick("Tick_Left", crosshairRoot.transform, lineSprite, new Vector2(12f, 3f), new Vector2(-12f, 0f));
            _crosshairRight = CreateCrosshairTick("Tick_Right", crosshairRoot.transform, lineSprite, new Vector2(12f, 3f), new Vector2(12f, 0f));
        }

        private RectTransform CreateCrosshairTick(string name, Transform parent, Sprite sprite, Vector2 size, Vector2 pos)
        {
            var tick = CreateUIObject(name, parent);
            var rect = tick.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var img = tick.AddComponent<Image>();
            img.sprite = sprite;
            img.color = new Color(1f, 1f, 1f, 0.95f);
            img.raycastTarget = false;

            var outline = tick.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
            outline.effectDistance = new Vector2(1f, -1f);

            return rect;
        }

        private void BuildCenterLockPrompt(Transform parent)
        {
            _centerLockPrompt = CreateUIObject("CenterLockPrompt", parent);
            var rect = _centerLockPrompt.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -48f);
            rect.sizeDelta = new Vector2(360f, 28f);

            var text = _centerLockPrompt.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 13;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = "[ Click to lock cursor into camera ]";
            text.color = L4DYellow;
            text.raycastTarget = false;

            var outline = _centerLockPrompt.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private void BuildCameraGuide(Transform parent)
        {
            _guidePanelRoot = CreateUIObject("CameraGuidePanel", parent);
            var rect = _guidePanelRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(25f, -25f);
            rect.sizeDelta = new Vector2(340f, 230f);

            // Dark semi-transparent card background
            var bgImg = _guidePanelRoot.AddComponent<Image>();
            bgImg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bgImg.type = Image.Type.Sliced;
            bgImg.color = L4DPanelDark;

            // Border outline
            var borderOutline = _guidePanelRoot.AddComponent<Outline>();
            borderOutline.effectColor = L4DBorder;
            borderOutline.effectDistance = new Vector2(1.5f, -1.5f);

            // Header Title
            var titleGo = CreateUIObject("HeaderTitle", _guidePanelRoot.transform);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(14f, -10f);
            titleRect.sizeDelta = new Vector2(-28f, 24f);

            var titleText = titleGo.AddComponent<Text>();
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText.fontSize = 13;
            titleText.fontStyle = FontStyle.Bold;
            titleText.alignment = TextAnchor.MiddleLeft;
            titleText.text = "HOW THE CAMERA WORKS";
            titleText.color = L4DGreen;

            // Instruction list
            var instructionsGo = CreateUIObject("Instructions", _guidePanelRoot.transform);
            var instRect = instructionsGo.GetComponent<RectTransform>();
            instRect.anchorMin = new Vector2(0f, 1f);
            instRect.anchorMax = new Vector2(1f, 1f);
            instRect.pivot = new Vector2(0f, 1f);
            instRect.anchoredPosition = new Vector2(14f, -36f);
            instRect.sizeDelta = new Vector2(-28f, 75f);

            var instText = instructionsGo.AddComponent<Text>();
            instText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            instText.fontSize = 11;
            instText.lineSpacing = 1.2f;
            instText.alignment = TextAnchor.UpperLeft;
            instText.color = L4DTextDim;
            instText.supportRichText = true;
            instText.text = "• <b>Mouse Look:</b> Move mouse to aim (Yaw turns body, Pitch tilts head)\n" +
                            "• <b>Left-Click:</b> Lock cursor & enable camera look\n" +
                            "• <b>ESC:</b> Unlock cursor & release mouse\n" +
                            "• <b>Reticle:</b> Center crosshair marks aim; spreads during sprint/fire";

            // Cursor Status line
            var statusGo = CreateUIObject("CursorStatus", _guidePanelRoot.transform);
            var statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0f, 1f);
            statusRect.anchoredPosition = new Vector2(14f, -118f);
            statusRect.sizeDelta = new Vector2(-28f, 20f);

            _cursorStatusText = statusGo.AddComponent<Text>();
            _cursorStatusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _cursorStatusText.fontSize = 11;
            _cursorStatusText.fontStyle = FontStyle.Bold;
            _cursorStatusText.supportRichText = true;
            _cursorStatusText.text = "<b>Status:</b> LOCKED (Camera Active)";

            // Live Camera Telemetry
            var statsGo = CreateUIObject("CameraStats", _guidePanelRoot.transform);
            var statsRect = statsGo.GetComponent<RectTransform>();
            statsRect.anchorMin = new Vector2(0f, 1f);
            statsRect.anchorMax = new Vector2(1f, 1f);
            statsRect.pivot = new Vector2(0f, 1f);
            statsRect.anchoredPosition = new Vector2(14f, -142f);
            statsRect.sizeDelta = new Vector2(-28f, 50f);

            _cameraStatsText = statsGo.AddComponent<Text>();
            _cameraStatsText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _cameraStatsText.fontSize = 10;
            _cameraStatsText.lineSpacing = 1.15f;
            _cameraStatsText.color = new Color(0.8f, 0.88f, 0.8f, 0.85f);
            _cameraStatsText.supportRichText = true;
            _cameraStatsText.text = "Pitch: 0.0°\nFOV: 75°\nTracking: True First-Person";

            // Toggle hint footer
            var footerGo = CreateUIObject("FooterHint", _guidePanelRoot.transform);
            var footerRect = footerGo.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0f, 0f);
            footerRect.anchorMax = new Vector2(1f, 0f);
            footerRect.pivot = new Vector2(0f, 0f);
            footerRect.anchoredPosition = new Vector2(14f, 8f);
            footerRect.sizeDelta = new Vector2(-28f, 16f);

            var footerText = footerGo.AddComponent<Text>();
            footerText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            footerText.fontSize = 10;
            footerText.alignment = TextAnchor.MiddleLeft;
            footerText.color = new Color(0.5f, 0.6f, 0.5f, 0.8f);
            footerText.supportRichText = true;
            footerText.text = "Press <b>[H]</b> to Toggle Camera Guide";

            _guidePanelRoot.SetActive(_showCameraGuide);
        }

        private void BuildMatchBanner(Transform parent)
        {
            _matchBannerRoot = CreateUIObject("MatchBannerRoot", parent);
            var rect = _matchBannerRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -16f);
            rect.sizeDelta = new Vector2(580f, 52f);

            var bg = _matchBannerRoot.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = L4DPanelDark;

            var outline = _matchBannerRoot.AddComponent<Outline>();
            outline.effectColor = L4DBorder;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Title
            var titleGo = CreateUIObject("BannerTitle", _matchBannerRoot.transform);
            var titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -6f);
            titleRt.sizeDelta = new Vector2(560f, 22f);

            _matchBannerTitle = titleGo.AddComponent<Text>();
            _matchBannerTitle.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _matchBannerTitle.fontSize = 13;
            _matchBannerTitle.fontStyle = FontStyle.Bold;
            _matchBannerTitle.alignment = TextAnchor.MiddleCenter;
            _matchBannerTitle.color = L4DGreen;
            _matchBannerTitle.raycastTarget = false;

            // Subtitle
            var subGo = CreateUIObject("BannerSubtitle", _matchBannerRoot.transform);
            var subRt = subGo.GetComponent<RectTransform>();
            subRt.anchorMin = new Vector2(0f, 0f);
            subRt.anchorMax = new Vector2(1f, 0f);
            subRt.pivot = new Vector2(0.5f, 0f);
            subRt.anchoredPosition = new Vector2(0f, 6f);
            subRt.sizeDelta = new Vector2(560f, 18f);

            _matchBannerSubtitle = subGo.AddComponent<Text>();
            _matchBannerSubtitle.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _matchBannerSubtitle.fontSize = 11;
            _matchBannerSubtitle.alignment = TextAnchor.MiddleCenter;
            _matchBannerSubtitle.color = L4DTextDim;
            _matchBannerSubtitle.raycastTarget = false;
        }

        private void BuildVitalsPanel(Transform parent)
        {
            _vitalsPanelRoot = CreateUIObject("VitalsPanelRoot", parent);
            var rect = _vitalsPanelRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(30f, 30f);
            rect.sizeDelta = new Vector2(330f, 105f);

            var bg = _vitalsPanelRoot.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = L4DPanelDark;

            var outline = _vitalsPanelRoot.AddComponent<Outline>();
            outline.effectColor = L4DBorder;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // --- 1. HEALTH SECTION ---
            var hpHeaderGo = CreateUIObject("HP_Header", _vitalsPanelRoot.transform);
            var hpHeaderRt = hpHeaderGo.GetComponent<RectTransform>();
            hpHeaderRt.anchorMin = new Vector2(0f, 1f);
            hpHeaderRt.anchorMax = new Vector2(1f, 1f);
            hpHeaderRt.pivot = new Vector2(0f, 1f);
            hpHeaderRt.anchoredPosition = new Vector2(16f, -10f);
            hpHeaderRt.sizeDelta = new Vector2(-32f, 18f);

            var hpLabel = hpHeaderGo.AddComponent<Text>();
            hpLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hpLabel.fontSize = 11;
            hpLabel.fontStyle = FontStyle.Bold;
            hpLabel.alignment = TextAnchor.MiddleLeft;
            hpLabel.text = "+ HEALTH";
            hpLabel.color = L4DGreen;

            var hpValGo = CreateUIObject("HP_Value", hpHeaderGo.transform);
            var hpValRt = hpValGo.GetComponent<RectTransform>();
            hpValRt.anchorMin = new Vector2(0f, 0f);
            hpValRt.anchorMax = new Vector2(1f, 1f);
            hpValRt.pivot = new Vector2(1f, 0.5f);
            hpValRt.anchoredPosition = Vector2.zero;
            hpValRt.sizeDelta = Vector2.zero;

            _healthValueText = hpValGo.AddComponent<Text>();
            _healthValueText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _healthValueText.fontSize = 12;
            _healthValueText.fontStyle = FontStyle.Bold;
            _healthValueText.alignment = TextAnchor.MiddleRight;
            _healthValueText.text = "100 / 100 HP";
            _healthValueText.color = Color.white;

            // Health Bar Background
            var hpBarBgGo = CreateUIObject("HP_Bar_Bg", _vitalsPanelRoot.transform);
            var hpBarBgRt = hpBarBgGo.GetComponent<RectTransform>();
            hpBarBgRt.anchorMin = new Vector2(0f, 1f);
            hpBarBgRt.anchorMax = new Vector2(1f, 1f);
            hpBarBgRt.pivot = new Vector2(0f, 1f);
            hpBarBgRt.anchoredPosition = new Vector2(16f, -30f);
            hpBarBgRt.sizeDelta = new Vector2(-32f, 14f);

            var hpBarBgImg = hpBarBgGo.AddComponent<Image>();
            hpBarBgImg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            hpBarBgImg.color = new Color(0.12f, 0.16f, 0.12f, 0.95f);

            // Health Bar Fill
            var hpBarFillGo = CreateUIObject("HP_Bar_Fill", hpBarBgGo.transform);
            _healthBarFill = hpBarFillGo.GetComponent<RectTransform>();
            _healthBarFill.anchorMin = new Vector2(0f, 0f);
            _healthBarFill.anchorMax = new Vector2(1f, 1f);
            _healthBarFill.pivot = new Vector2(0f, 0.5f);
            _healthBarFill.anchoredPosition = Vector2.zero;
            _healthBarFill.sizeDelta = Vector2.zero;

            _healthBarImage = hpBarFillGo.AddComponent<Image>();
            _healthBarImage.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            _healthBarImage.color = L4DGreen;

            // --- 2. STAMINA SECTION ---
            var staHeaderGo = CreateUIObject("Stamina_Header", _vitalsPanelRoot.transform);
            var staHeaderRt = staHeaderGo.GetComponent<RectTransform>();
            staHeaderRt.anchorMin = new Vector2(0f, 1f);
            staHeaderRt.anchorMax = new Vector2(1f, 1f);
            staHeaderRt.pivot = new Vector2(0f, 1f);
            staHeaderRt.anchoredPosition = new Vector2(16f, -54f);
            staHeaderRt.sizeDelta = new Vector2(-32f, 18f);

            var staLabel = staHeaderGo.AddComponent<Text>();
            staLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            staLabel.fontSize = 11;
            staLabel.fontStyle = FontStyle.Bold;
            staLabel.alignment = TextAnchor.MiddleLeft;
            staLabel.text = "» STAMINA";
            staLabel.color = L4DCyan;

            var staValGo = CreateUIObject("Stamina_Value", staHeaderGo.transform);
            var staValRt = staValGo.GetComponent<RectTransform>();
            staValRt.anchorMin = new Vector2(0f, 0f);
            staValRt.anchorMax = new Vector2(1f, 1f);
            staValRt.pivot = new Vector2(1f, 0.5f);
            staValRt.anchoredPosition = Vector2.zero;
            staValRt.sizeDelta = Vector2.zero;

            _staminaValueText = staValGo.AddComponent<Text>();
            _staminaValueText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _staminaValueText.fontSize = 11;
            _staminaValueText.fontStyle = FontStyle.Bold;
            _staminaValueText.alignment = TextAnchor.MiddleRight;
            _staminaValueText.text = "100%";
            _staminaValueText.color = Color.white;

            // Stamina Bar Background
            var staBarBgGo = CreateUIObject("Stamina_Bar_Bg", _vitalsPanelRoot.transform);
            var staBarBgRt = staBarBgGo.GetComponent<RectTransform>();
            staBarBgRt.anchorMin = new Vector2(0f, 1f);
            staBarBgRt.anchorMax = new Vector2(1f, 1f);
            staBarBgRt.pivot = new Vector2(0f, 1f);
            staBarBgRt.anchoredPosition = new Vector2(16f, -74f);
            staBarBgRt.sizeDelta = new Vector2(-32f, 12f);

            var staBarBgImg = staBarBgGo.AddComponent<Image>();
            staBarBgImg.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            staBarBgImg.color = new Color(0.12f, 0.16f, 0.18f, 0.95f);

            // Stamina Bar Fill
            var staBarFillGo = CreateUIObject("Stamina_Bar_Fill", staBarBgGo.transform);
            _staminaBarFill = staBarFillGo.GetComponent<RectTransform>();
            _staminaBarFill.anchorMin = new Vector2(0f, 0f);
            _staminaBarFill.anchorMax = new Vector2(1f, 1f);
            _staminaBarFill.pivot = new Vector2(0f, 0.5f);
            _staminaBarFill.anchoredPosition = Vector2.zero;
            _staminaBarFill.sizeDelta = Vector2.zero;

            _staminaBarImage = staBarFillGo.AddComponent<Image>();
            _staminaBarImage.sprite = ProceduralUISprites.GetSolidWhiteSprite();
            _staminaBarImage.color = L4DCyan;
        }

        private void BuildAmmoPanel(Transform parent)
        {
            _ammoPanelRoot = CreateUIObject("AmmoPanelRoot", parent);
            var rect = _ammoPanelRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-30f, 30f);
            rect.sizeDelta = new Vector2(250f, 105f);

            var bg = _ammoPanelRoot.AddComponent<Image>();
            bg.sprite = ProceduralUISprites.GetRoundedBoxSprite();
            bg.type = Image.Type.Sliced;
            bg.color = L4DPanelDark;

            var outline = _ammoPanelRoot.AddComponent<Outline>();
            outline.effectColor = L4DBorder;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Weapon Title Header
            var weapGo = CreateUIObject("WeaponName", _ammoPanelRoot.transform);
            var weapRt = weapGo.GetComponent<RectTransform>();
            weapRt.anchorMin = new Vector2(0f, 1f);
            weapRt.anchorMax = new Vector2(1f, 1f);
            weapRt.pivot = new Vector2(0.5f, 1f);
            weapRt.anchoredPosition = new Vector2(0f, -10f);
            weapRt.sizeDelta = new Vector2(-28f, 18f);

            _weaponNameText = weapGo.AddComponent<Text>();
            _weaponNameText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _weaponNameText.fontSize = 11;
            _weaponNameText.fontStyle = FontStyle.Bold;
            _weaponNameText.alignment = TextAnchor.MiddleCenter;
            _weaponNameText.color = L4DYellow;
            _weaponNameText.text = "AK-47 TACTICAL";

            // Ammo Counter Row (Big Mag / Reserve)
            var ammoRowGo = CreateUIObject("AmmoRow", _ammoPanelRoot.transform);
            var ammoRowRt = ammoRowGo.GetComponent<RectTransform>();
            ammoRowRt.anchorMin = new Vector2(0f, 1f);
            ammoRowRt.anchorMax = new Vector2(1f, 1f);
            ammoRowRt.pivot = new Vector2(0.5f, 1f);
            ammoRowRt.anchoredPosition = new Vector2(0f, -32f);
            ammoRowRt.sizeDelta = new Vector2(-28f, 40f);

            // Mag Text (Large 36pt)
            var magGo = CreateUIObject("MagText", ammoRowGo.transform);
            var magRt = magGo.GetComponent<RectTransform>();
            magRt.anchorMin = new Vector2(0f, 0f);
            magRt.anchorMax = new Vector2(0.52f, 1f);
            magRt.pivot = new Vector2(1f, 0.5f);
            magRt.anchoredPosition = Vector2.zero;
            magRt.sizeDelta = Vector2.zero;

            _ammoMagText = magGo.AddComponent<Text>();
            _ammoMagText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _ammoMagText.fontSize = 36;
            _ammoMagText.fontStyle = FontStyle.Bold;
            _ammoMagText.alignment = TextAnchor.MiddleRight;
            _ammoMagText.color = Color.white;
            _ammoMagText.text = "30";

            // Reserve Text (20pt)
            var resGo = CreateUIObject("ReserveText", ammoRowGo.transform);
            var resRt = resGo.GetComponent<RectTransform>();
            resRt.anchorMin = new Vector2(0.54f, 0f);
            resRt.anchorMax = new Vector2(1f, 1f);
            resRt.pivot = new Vector2(0f, 0.5f);
            resRt.anchoredPosition = new Vector2(0f, -4f);
            resRt.sizeDelta = Vector2.zero;

            _ammoReserveText = resGo.AddComponent<Text>();
            _ammoReserveText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _ammoReserveText.fontSize = 20;
            _ammoReserveText.fontStyle = FontStyle.Bold;
            _ammoReserveText.alignment = TextAnchor.MiddleLeft;
            _ammoReserveText.color = L4DTextDim;
            _ammoReserveText.text = "/ 120";

            // Status Line / Reload Prompt
            var statGo = CreateUIObject("AmmoStatus", _ammoPanelRoot.transform);
            var statRt = statGo.GetComponent<RectTransform>();
            statRt.anchorMin = new Vector2(0f, 0f);
            statRt.anchorMax = new Vector2(1f, 0f);
            statRt.pivot = new Vector2(0.5f, 0f);
            statRt.anchoredPosition = new Vector2(0f, 8f);
            statRt.sizeDelta = new Vector2(-28f, 18f);

            _ammoStatusText = statGo.AddComponent<Text>();
            _ammoStatusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _ammoStatusText.fontSize = 11;
            _ammoStatusText.fontStyle = FontStyle.Bold;
            _ammoStatusText.alignment = TextAnchor.MiddleCenter;
            _ammoStatusText.color = L4DGreen;
            _ammoStatusText.text = "[ READY ]";
        }

        private void RefreshVitalsUI()
        {
            if (_healthBarFill != null && _healthBarImage != null)
            {
                float hpPct = Mathf.Clamp01(_cachedHealth / _cachedMaxHealth);
                _healthBarFill.anchorMax = new Vector2(hpPct, 1f);

                if (hpPct > 0.5f)
                {
                    _healthBarImage.color = L4DGreen;
                }
                else if (hpPct > 0.25f)
                {
                    _healthBarImage.color = L4DYellow;
                }
                else
                {
                    _healthBarImage.color = L4DRed;
                }

                if (_healthValueText != null)
                {
                    _healthValueText.text = $"{Mathf.CeilToInt(_cachedHealth)} / {Mathf.CeilToInt(_cachedMaxHealth)} HP";
                }
            }

            if (_staminaBarFill != null && _staminaBarImage != null)
            {
                float staPct = Mathf.Clamp01(_cachedStamina / _cachedMaxStamina);
                _staminaBarFill.anchorMax = new Vector2(staPct, 1f);

                if (staPct > 0.2f)
                {
                    _staminaBarImage.color = L4DCyan;
                }
                else
                {
                    _staminaBarImage.color = L4DYellow;
                }

                if (_staminaValueText != null)
                {
                    _staminaValueText.text = $"{Mathf.CeilToInt(staPct * 100f)}%";
                }
            }
        }

        private void RefreshAmmoUI(string weaponName = "AK-47 TACTICAL")
        {
            if (_weaponNameText != null)
            {
                _weaponNameText.text = weaponName;
            }

            if (_ammoMagText != null)
            {
                _ammoMagText.text = _cachedMagAmmo.ToString();
                if (_cachedMagAmmo <= 5)
                {
                    _ammoMagText.color = _cachedMagAmmo == 0 ? L4DRed : L4DYellow;
                }
                else
                {
                    _ammoMagText.color = Color.white;
                }
            }

            if (_ammoReserveText != null)
            {
                _ammoReserveText.text = $"/ {_cachedReserveAmmo}";
            }

            if (_ammoStatusText != null)
            {
                if (_cachedIsReloading)
                {
                    _ammoStatusText.color = L4DYellow;
                    _ammoStatusText.text = "[ RELOADING... ]";
                }
                else if (_cachedMagAmmo == 0)
                {
                    _ammoStatusText.color = L4DRed;
                    _ammoStatusText.text = "[ PRESS R TO RELOAD ]";
                }
                else
                {
                    _ammoStatusText.color = L4DGreen;
                    _ammoStatusText.text = "[ READY ]";
                }
            }
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }
        #endregion
    }

    /// <summary>
    /// Procedural procedural sprite factory for UI elements.
    /// </summary>
    public static class ProceduralUISprites
    {
        private static Sprite s_SolidWhite;
        private static Sprite s_RoundedBox;
        private static Sprite s_BorderBox;

        public static Sprite GetSolidWhiteSprite()
        {
            if (s_SolidWhite == null)
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                Color[] colors = new Color[16];
                for (int i = 0; i < 16; i++) colors[i] = Color.white;
                tex.SetPixels(colors);
                tex.Apply();
                s_SolidWhite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            }
            return s_SolidWhite;
        }

        public static Sprite GetRoundedBoxSprite()
        {
            if (s_RoundedBox == null)
            {
                int s = 32;
                var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0.95f));
                    }
                }
                tex.Apply();
                s_RoundedBox = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(6, 6, 6, 6));
            }
            return s_RoundedBox;
        }

        public static Sprite GetBorderBoxSprite()
        {
            if (s_BorderBox == null)
            {
                int s = 16;
                var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        bool isBorder = x == 0 || x == s - 1 || y == 0 || y == s - 1;
                        tex.SetPixel(x, y, isBorder ? Color.white : Color.clear);
                    }
                }
                tex.Apply();
                s_BorderBox = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(2, 2, 2, 2));
            }
            return s_BorderBox;
        }
    }
}
