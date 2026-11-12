using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Scorched.Combat;
using Scorched.Inventory;
using Scorched.Player;
using Scorched.Survival;
using Scorched.Weapons;

namespace Scorched.UI
{
    /// <summary>
    /// Minimalist Dark Tech-Brutalist / Industrial Tactical HUD for SCORCHED.
    /// Features strict RectMask2D container clipping, generous inner padding,
    /// custom multi-layer ScorchedCustomBar widgets (zero native Unity Sliders),
    /// Directional Footstep Radar, Solar Surge Timer, and Heatstroke Vignette.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedHUDController : MonoBehaviour
    {
        #region Singleton
        public static ScorchedHUDController Instance { get; private set; }
        #endregion

        #region Serialized UI Assets
        [Header("GDD Tactical Frame & Custom Bar Sprites")]
        [SerializeField] private Sprite _gddTacticalFrameSprite;
        [SerializeField] private Sprite _customBarTrackSprite;
        [SerializeField] private Sprite _customBarFillSegmentedSprite;
        [SerializeField] private Sprite _customBarFillRazorSprite;
        [SerializeField] private Sprite _iconHealthCrossSprite;

        [Header("Tactical Icons & Overlays")]
        [SerializeField] private Sprite _footstepArcSprite;
        [SerializeField] private Sprite _iconSunSprite;
        [SerializeField] private Sprite _iconShadeSprite;
        [SerializeField] private Sprite _iconHydrationDropSprite;
        [SerializeField] private Sprite _heatstrokeVignetteSprite;
        [SerializeField] private Sprite _radialProgressRingSprite;
        [SerializeField] private Sprite _cabalSlotCellSprite;

        [Header("Crosshair Settings")]
        [SerializeField] private float _crosshairBaseGap = 7f;
        [SerializeField] private float _crosshairMaxGap = 24f;
        [SerializeField] private float _crosshairFireKick = 7f;
        [SerializeField] private float _crosshairTickLength = 8f;
        [SerializeField] private float _crosshairTickThickness = 2f;
        #endregion

        #region Runtime UI References
        private Font _uiFont;
        private Canvas _hudCanvas;
        private FootstepRadarUI _footstepRadar;

        // Zone 1: Bottom-Left Vitals & Thermal Cluster (320x156)
        private RectTransform _vitalsFrameRect;
        private Image _thermalStatusIcon;
        private Text _thermalStatusText;
        private Text _heatPercentLabel;
        private Text _healthBigText;
        private Text _healthMaxSubText;
        private ScorchedCustomBar _healthCustomBar;
        private Text _staminaPercentText;
        private ScorchedCustomBar _staminaCustomBar;
        private Text _hydrationLabelText;
        private ScorchedCustomBar _hydrationCustomBar;
        private Text _exposureLabelText;
        private ScorchedCustomBar _exposureCustomBar;

        // Zone 1B: Shelter Durability Widget (Anchored directly above vitals at 28, 194, 320x50)
        private RectTransform _shelterFrameRect;
        private CanvasGroup _shelterCanvasGroup;
        private Image _shelterIcon;
        private Text _shelterTitleText;
        private Text _shelterStatusText;
        private Text _shelterDurabilityPercentText;
        private ScorchedCustomBar _shelterCustomBar;
        private float _shelterCurrentAlpha = 0f;
        private float _fallbackPlayerSearchTimer = 0f;

        // Zone 2: Bottom-Right Weapon & Ammo Box (200x92) + [1] & Consumable Hotbar ([2][3][4])
        private struct HotbarSlotVisual
        {
            public HotbarSlotType SlotType;
            public ItemId DefaultBaseItemId;
            public RectTransform FrameRect;
            public Image BackgroundImage;
            public Image IconImage;
            public Text KeybindBadgeText;
            public Text CountBadgeText;
            public Image GradePipImage;
            public Image[] SelectionBorders;
        }

        private RectTransform _ammoFrameRect;
        private Text _weaponKeybindBadgeText;
        private Image[] _weaponSelectionBorders;
        private Text _weaponNameText;
        private Text _ammoStatusBadge;
        private Text _ammoMagText;
        private Text _ammoReserveText;
        private GameObject _reloadTrackRoot;
        private Image _reloadProgressBar;
        private RectTransform _consumableHotbarRect;
        private HotbarSlotVisual[] _hotbarSlotVisuals;

        // Zone 3: Top-Center Minimalist Heatwave Timer Box (240x36)
        private RectTransform _solarFrameRect;
        private Image _solarPillBg;
        private Text _solarCycleText;
        private Text _matchSubStatusText;

        // Zone 4: Center Crosshair, Radar & Heatstroke Vignette
        private Image _heatstrokeVignette;
        private GameObject _crosshairRoot;
        private RectTransform _tickTop;
        private RectTransform _tickBottom;
        private RectTransform _tickLeft;
        private RectTransform _tickRight;

        // DBNO / Dead Compact Overlay
        private RectTransform _lifeStateFrameRect;
        private GameObject _lifeStateRoot;
        private Text _lifeStateTitleText;
        private Text _lifeStatePromptText;
        private ScorchedCustomBar _bleedoutCustomBar;
        private GameObject _reviveRoot;
        private ScorchedCustomBar _reviveCustomBar;
        private Text _reviveLabelText;

        // Scavenging Radial Prompt & Alert Banner + Inventory/Crafting Overlays
        private GameObject _interactionRoot;
        private Image _interactionRadialRing;
        private Image _interactionRadialBg;
        private Text _interactionPromptText;
        private GameObject _alertBannerRoot;
        private Text _alertBannerText;
        private float _alertBannerTimer;
        private ScorchedInventoryUI _inventoryUI;
        private ScorchedCraftingUI _craftingUI;
        private ScorchedContainerLootUI _containerLootUI;
        #endregion

        #region Cached Telemetry & String-GC Guards
        private PlayerMovement _localPlayer;
        private PlayerHealthController _localHealth;
        private PlayerThermalController _localThermal;
        private NetworkWeaponController _localWeapon;
        private PlayerInventoryController _localInventory;
        private PlayerHotbarController _localHotbar;

        private float _cachedHealth = 100f;
        private float _cachedMaxHealth = 100f;
        private float _cachedStamina = 100f;
        private float _cachedMaxStamina = 100f;
        private float _cachedDownedHealth = 100f;
        private float _cachedMaxDownedHealth = 100f;
        private float _cachedReviveProgress = 0f;
        private PlayerLifeState _cachedLifeState = PlayerLifeState.Alive;

        private int _cachedMagAmmo = 30;
        private int _cachedReserveAmmo = 120;
        private bool _cachedIsReloading = false;
        private string _cachedWeaponName = "AK-47";

        private int _lastHpBigInt = int.MinValue;
        private int _lastHpMaxInt = int.MinValue;
        private int _lastHeatPct = int.MinValue;
        private int _lastHydrationPct = int.MinValue;
        private int _lastMagShown = int.MinValue;
        private int _lastReserveShown = int.MinValue;
        private int _lastSolarSecShown = int.MinValue;
        private int _lastMatchPhaseShown = -99;
        private int _lastWaveShown = -99;
        private bool? _lastPermanentShown = null;
        private int _lastShadeDurShown = int.MinValue;
        private int _lastThermalModeShown = -99;
        private int _lastDownedHpShown = int.MinValue;
        private int _lastRevivePctShown = int.MinValue;

        private float _crosshairGap;
        private float _recoilPulse;
        private float _vignetteAlpha;
        #endregion

        #region Tech-Brutalist GDD Palette
        private static readonly Color ColFrameTint = Color.white; // uses #141518@82% baked in gdd_tactical_frame
        private static readonly Color ColShadeCyan = new Color(0.361f, 0.882f, 0.902f, 1f);      // #5CE1E6
        private static readonly Color ColSunAmber = new Color(0.961f, 0.651f, 0.137f, 1f);       // #F5A623
        private static readonly Color ColSurgeCrimson = new Color(1.000f, 0.271f, 0.227f, 1f);   // #FF453A
        private static readonly Color ColPureWhite = new Color(1.000f, 1.000f, 1.000f, 1f);      // #FFFFFF
        private static readonly Color ColHpOffWhite = new Color(0.949f, 0.957f, 0.973f, 1f);     // #F2F4F8
        private static readonly Color ColHpGhostRed = new Color(1.000f, 0.333f, 0.267f, 0.533f); // #FF554488
        private static readonly Color ColStaminaSilver = new Color(0.847f, 0.867f, 0.902f, 1f);  // #D8DDE6
        private static readonly Color ColStaminaGhost = new Color(1.000f, 1.000f, 1.000f, 0.28f);
        private static readonly Color ColHydrationAzure = new Color(0.180f, 0.659f, 1.000f, 1f); // #2EA8FF
        private static readonly Color ColHeatAmberRed = new Color(1.000f, 0.420f, 0.102f, 1f);   // #FF6B1A
        private static readonly Color ColTextLight = new Color(0.863f, 0.863f, 0.863f, 1f);      // #DCDCDC
        private static readonly Color ColTimerText = new Color(0.898f, 0.898f, 0.918f, 1f);      // #E5E5EA
        private static readonly Color ColTextMuted = new Color(0.557f, 0.573f, 0.608f, 1f);      // #8E929B
        private static readonly Color ColWeaponMuted = new Color(0.627f, 0.643f, 0.671f, 1f);    // #A0A4AB
        private static readonly Color ColReadyTeal = new Color(0.282f, 0.898f, 0.761f, 1f);      // #48E5C2
        private static readonly Color ColDivider = new Color(0.243f, 0.259f, 0.294f, 0.65f);     // #3E424B
        private static readonly Color ColGradePoor = new Color(0.620f, 0.620f, 0.620f, 1f);      // #9E9E9E
        private static readonly Color ColGradeGood = new Color(0.196f, 0.843f, 0.294f, 1f);      // #32D74B
        private static readonly Color ColGradeGreat = new Color(1.000f, 0.839f, 0.039f, 1f);     // #FFD60A
        private static readonly Color ColBorderUnselected = new Color(0.282f, 0.298f, 0.329f, 0.55f); // #484C54
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
            _crosshairGap = _crosshairBaseGap;

            ResolveFont();
            EnsureFallbackSprites();
            BuildHUDHierarchy();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (_hudCanvas == null)
            {
                return;
            }

            if (MainMenuUIManager.IsMenuOpen)
            {
                if (_hudCanvas.enabled) _hudCanvas.enabled = false;
                return;
            }

            if (!_hudCanvas.enabled) _hudCanvas.enabled = true;

            ResolveLocalPlayer();
            SyncTelemetryFromLocalPlayer();
            UpdateVitalsAndThermalUI();
            UpdateWeaponAndAmmoUI();
            UpdateHotbarUI();
            UpdateSolarCycleBannerUI();
            UpdateCrosshairAndVignette();
            UpdateLifeStateBannerUI();
            UpdateAlertBannerTimer();

            if (_crosshairRoot != null)
            {
                bool showCrosshair = !ScorchedInputGates.IsInventoryOpen && (_cachedLifeState == PlayerLifeState.Alive);
                if (_crosshairRoot.activeSelf != showCrosshair)
                {
                    _crosshairRoot.SetActive(showCrosshair);
                }
            }
        }
        #endregion

        #region External Compatibility API
        public void ShowAlertBanner(string message, float durationSeconds = 3.5f)
        {
            if (_alertBannerRoot == null || _alertBannerText == null) return;
            if (string.IsNullOrEmpty(message))
            {
                _alertBannerRoot.SetActive(false);
                _alertBannerTimer = 0f;
                return;
            }
            _alertBannerText.text = message.ToUpperInvariant();
            _alertBannerTimer = Mathf.Max(0.5f, durationSeconds);
            _alertBannerRoot.SetActive(true);
        }

        public void UpdateInteractionPrompt(string promptLabel, float radialProgress01, bool visible)
        {
            if (_interactionRoot == null) return;
            if (!visible || string.IsNullOrEmpty(promptLabel))
            {
                if (_interactionRoot.activeSelf) _interactionRoot.SetActive(false);
                return;
            }

            if (!_interactionRoot.activeSelf) _interactionRoot.SetActive(true);
            if (_interactionPromptText != null) _interactionPromptText.text = promptLabel;

            float clamped = Mathf.Clamp01(radialProgress01);
            bool showRing = clamped > 0.001f;
            if (_interactionRadialBg != null) _interactionRadialBg.enabled = showRing;
            if (_interactionRadialRing != null)
            {
                _interactionRadialRing.enabled = showRing;
                _interactionRadialRing.fillAmount = clamped;
            }
        }

        private void UpdateAlertBannerTimer()
        {
            if (_alertBannerRoot == null || !_alertBannerRoot.activeSelf) return;
            _alertBannerTimer -= Time.deltaTime;
            if (_alertBannerTimer <= 0f)
            {
                _alertBannerRoot.SetActive(false);
            }
        }
        public void OnWeaponFired()
        {
            _recoilPulse = _crosshairFireKick;
        }

        public void UpdateHealth(float currentHealth, float maxHealth)
        {
            _cachedHealth = Mathf.Max(0f, currentHealth);
            _cachedMaxHealth = Mathf.Max(1f, maxHealth);
        }

        public void UpdateStamina(float currentStamina, float maxStamina)
        {
            _cachedStamina = Mathf.Max(0f, currentStamina);
            _cachedMaxStamina = Mathf.Max(1f, maxStamina);
        }

        public void UpdateLifeState(
            PlayerLifeState state,
            float health,
            float maxHealth,
            float downedHealth,
            float maxDownedHealth,
            float reviveProgress)
        {
            _cachedLifeState = state;
            _cachedHealth = Mathf.Max(0f, health);
            _cachedMaxHealth = Mathf.Max(1f, maxHealth);
            _cachedDownedHealth = Mathf.Max(0f, downedHealth);
            _cachedMaxDownedHealth = Mathf.Max(1f, maxDownedHealth);
            _cachedReviveProgress = Mathf.Clamp01(reviveProgress);
        }

        public void UpdateAmmo(int currentMag, int reserveAmmo, bool isReloading, string weaponName)
        {
            _cachedMagAmmo = Mathf.Max(0, currentMag);
            _cachedReserveAmmo = Mathf.Max(0, reserveAmmo);
            _cachedIsReloading = isReloading;
            if (!string.IsNullOrEmpty(weaponName))
            {
                _cachedWeaponName = FormatCompactWeaponName(weaponName);
            }
        }

        private static string FormatCompactWeaponName(string raw)
        {
            string upper = raw.Trim().ToUpperInvariant();
            // Keep GDD concise label like "AK-47" if "AK-47 Tactical" or similar fits cleanly
            if (upper == "AK-47 TACTICAL")
            {
                return "AK-47";
            }
            return upper;
        }
        #endregion

        #region Player Resolution & Telemetry Sync
        private void ResolveLocalPlayer()
        {
            if (_localPlayer != null && _localPlayer.gameObject.activeInHierarchy)
            {
                if (!_localPlayer.IsSpawned || _localPlayer.IsOwner)
                {
                    if (_localInventory == null) _localInventory = _localPlayer.GetComponent<PlayerInventoryController>();
                    if (_localHotbar == null) _localHotbar = _localPlayer.GetComponent<PlayerHotbarController>();
                    return;
                }
            }

            _localPlayer = null;
            _localHealth = null;
            _localThermal = null;
            _localWeapon = null;
            _localInventory = null;
            _localHotbar = null;

            var activePlayers = PlayerHealthController.ActiveInstances;
            for (int i = 0; i < activePlayers.Count; i++)
            {
                PlayerHealthController hc = activePlayers[i];
                if (hc != null && hc.gameObject.activeInHierarchy && (!hc.IsSpawned || hc.IsOwner))
                {
                    _localHealth = hc;
                    _localPlayer = hc.GetComponent<PlayerMovement>();
                    _localThermal = hc.GetComponent<PlayerThermalController>();
                    _localWeapon = hc.GetComponent<NetworkWeaponController>();
                    _localInventory = hc.GetComponent<PlayerInventoryController>();
                    _localHotbar = hc.GetComponent<PlayerHotbarController>();
                    return;
                }
            }

            _fallbackPlayerSearchTimer -= Time.unscaledDeltaTime;
            if (_fallbackPlayerSearchTimer > 0f) return;
            _fallbackPlayerSearchTimer = 1.0f;

            var movements = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
            for (int i = 0; i < movements.Length; i++)
            {
                PlayerMovement pm = movements[i];
                if (pm != null && pm.gameObject.activeInHierarchy && (!pm.IsSpawned || pm.IsOwner))
                {
                    _localPlayer = pm;
                    _localHealth = pm.GetComponent<PlayerHealthController>();
                    _localThermal = pm.GetComponent<PlayerThermalController>();
                    _localWeapon = pm.GetComponent<NetworkWeaponController>();
                    _localInventory = pm.GetComponent<PlayerInventoryController>();
                    _localHotbar = pm.GetComponent<PlayerHotbarController>();
                    break;
                }
            }
        }

        private void SyncTelemetryFromLocalPlayer()
        {
            if (_localHealth != null)
            {
                _cachedLifeState = _localHealth.LifeState;
                _cachedHealth = _localHealth.CurrentHealthValue;
                _cachedMaxHealth = Mathf.Max(1f, _localHealth.MaxHealth);
                _cachedDownedHealth = _localHealth.CurrentDownedHealthValue;
                _cachedMaxDownedHealth = Mathf.Max(1f, _localHealth.MaxDownedHealth);
                _cachedReviveProgress = _localHealth.CurrentReviveProgress;
            }
            else if (_localPlayer != null)
            {
                _cachedHealth = _localPlayer.CurrentHealth;
            }

            if (_localPlayer != null)
            {
                _cachedStamina = _localPlayer.CurrentStamina;
                _cachedMaxStamina = Mathf.Max(1f, _localPlayer.MaxStamina);
            }

            if (_localWeapon != null)
            {
                _cachedMagAmmo = _localWeapon.CurrentAmmoInMag;
                _cachedReserveAmmo = _localWeapon.CurrentReserveAmmo;
                _cachedIsReloading = _localWeapon.IsReloading;
                _cachedWeaponName = _localWeapon.IsWeaponEquipped
                    ? FormatCompactWeaponName(_localWeapon.CurrentWeaponName)
                    : "UNARMED";
            }
        }
        #endregion

        #region Per-Frame Visual Updates
        private void UpdateVitalsAndThermalUI()
        {
            // HP text & custom razor bar
            int hpInt = Mathf.CeilToInt(Mathf.Max(0f, _cachedHealth));
            int maxHpInt = Mathf.RoundToInt(Mathf.Max(1f, _cachedMaxHealth));
            if (hpInt != _lastHpBigInt && _healthBigText != null)
            {
                _lastHpBigInt = hpInt;
                _healthBigText.text = hpInt.ToString();
            }
            if (maxHpInt != _lastHpMaxInt && _healthMaxSubText != null)
            {
                _lastHpMaxInt = maxHpInt;
                _healthMaxSubText.text = $"/ {maxHpInt} HP";
            }

            float hpRatio = Mathf.Clamp01(_cachedHealth / Mathf.Max(1f, _cachedMaxHealth));
            if (_healthCustomBar != null)
            {
                _healthCustomBar.SetValue(_cachedHealth, _cachedMaxHealth);
                _healthCustomBar.SetFillColor(hpRatio <= 0.30f ? ColSurgeCrimson : ColHpOffWhite);
                _healthCustomBar.SetPipColor(hpRatio <= 0.30f ? ColSurgeCrimson : ColPureWhite);
            }

            // Stamina custom bar
            if (_staminaCustomBar != null)
            {
                _staminaCustomBar.SetValue(_cachedStamina, _cachedMaxStamina, _staminaPercentText, "%", true);
            }

            // Thermal & Hydration
            float hydration = _localThermal != null ? _localThermal.Hydration : 100f;
            float heatExposure = _localThermal != null ? _localThermal.HeatExposure : 0f;
            bool inShade = _localThermal == null || _localThermal.IsInShade;
            ShadeZoneBuilding currentShade = _localThermal != null ? _localThermal.CurrentShadeZone : null;
            HeatwaveHazardManager hazard = HeatwaveHazardManager.Instance;
            bool graceActive = hazard != null && hazard.IsGraceWindowActive;
            bool surgeActive = hazard != null && hazard.IsHeatwaveSurgeActive;
            bool shadeBurning = inShade && currentShade != null && currentShade.IsTargetedByHeatwave && surgeActive;
            int shadePct = currentShade != null ? Mathf.Clamp(Mathf.RoundToInt(currentShade.DurabilityNormalized * 100f), 0, 100) : 100;

            int hydPct = Mathf.RoundToInt(Mathf.Clamp(hydration, 0f, 100f));
            if (hydPct != _lastHydrationPct && _hydrationLabelText != null)
            {
                _lastHydrationPct = hydPct;
                _hydrationLabelText.text = $"HYDRATION {hydPct}%";
                _hydrationLabelText.color = hydPct <= 20 ? ColSurgeCrimson : ColTextLight;
            }
            if (_hydrationCustomBar != null)
            {
                _hydrationCustomBar.SetValue(hydration, 100f);
                _hydrationCustomBar.SetFillColor(hydPct <= 20 ? ColSurgeCrimson : ColHydrationAzure);
            }

            int heatPct = Mathf.RoundToInt(Mathf.Clamp(heatExposure, 0f, 100f));
            if (heatPct != _lastHeatPct && _heatPercentLabel != null)
            {
                _lastHeatPct = heatPct;
                _heatPercentLabel.text = $"HEAT {heatPct}%";
                _heatPercentLabel.color = heatPct >= 80 ? ColSurgeCrimson : ColTextLight;
            }
            if (_exposureCustomBar != null)
            {
                _exposureCustomBar.SetValue(heatExposure, 100f);
                _exposureCustomBar.SetFillColor(heatPct >= 80 ? ColSurgeCrimson : ColHeatAmberRed);
            }

            if (_thermalStatusText != null && _thermalStatusIcon != null)
            {
                if (shadeBurning)
                {
                    if (_lastThermalModeShown != 1 || _lastShadeDurShown != shadePct)
                    {
                        _lastThermalModeShown = 1;
                        _lastShadeDurShown = shadePct;
                        _thermalStatusIcon.sprite = _iconSunSprite;
                        _thermalStatusText.text = $"SHADE BURNING {shadePct}%";
                    }

                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 7f);
                    Color burnCol = Color.Lerp(ColSunAmber, ColSurgeCrimson, 0.35f + 0.65f * pulse);
                    _thermalStatusIcon.color = burnCol;
                    _thermalStatusText.color = burnCol;
                }
                else if (inShade)
                {
                    if (_lastThermalModeShown != 2)
                    {
                        _lastThermalModeShown = 2;
                        _lastShadeDurShown = int.MinValue;
                        _thermalStatusIcon.sprite = _iconShadeSprite;
                        _thermalStatusIcon.color = ColShadeCyan;
                        _thermalStatusText.text = "IN SHADE // SAFE";
                        _thermalStatusText.color = ColShadeCyan;
                    }
                }
                else if (graceActive)
                {
                    if (_lastThermalModeShown != 3)
                    {
                        _lastThermalModeShown = 3;
                        _lastShadeDurShown = int.MinValue;
                        _thermalStatusIcon.sprite = _iconShadeSprite;
                        _thermalStatusIcon.color = ColShadeCyan;
                        _thermalStatusText.text = "SAFE WINDOW";
                        _thermalStatusText.color = ColShadeCyan;
                    }
                }
                else if (surgeActive)
                {
                    if (_lastThermalModeShown != 4)
                    {
                        _lastThermalModeShown = 4;
                        _lastShadeDurShown = int.MinValue;
                        _thermalStatusIcon.sprite = _iconSunSprite;
                        _thermalStatusIcon.color = ColSurgeCrimson;
                        _thermalStatusText.text = "SOLAR SURGE";
                        _thermalStatusText.color = ColSurgeCrimson;
                    }
                }
                else
                {
                    if (_lastThermalModeShown != 5)
                    {
                        _lastThermalModeShown = 5;
                        _lastShadeDurShown = int.MinValue;
                        _thermalStatusIcon.sprite = _iconSunSprite;
                        _thermalStatusIcon.color = ColSunAmber;
                        _thermalStatusText.text = "SUN EXPOSED";
                        _thermalStatusText.color = ColSunAmber;
                    }
                }
            }

            // Shelter Durability Dedicated HUD Panel
            UpdateShelterDurabilityUI(inShade, currentShade, surgeActive);
        }

        private void UpdateShelterDurabilityUI(bool inShade, ShadeZoneBuilding currentShade, bool surgeActive)
        {
            if (_shelterCanvasGroup == null) return;

            bool showShelter = inShade && currentShade != null && !currentShade.IsDestroyed;
            float targetAlpha = showShelter ? 1f : 0f;
            _shelterCurrentAlpha = Mathf.MoveTowards(_shelterCurrentAlpha, targetAlpha, Time.unscaledDeltaTime * 6f);
            _shelterCanvasGroup.alpha = _shelterCurrentAlpha;

            if (_shelterCurrentAlpha <= 0.005f)
            {
                if (_shelterFrameRect != null && _shelterFrameRect.gameObject.activeSelf)
                {
                    _shelterFrameRect.gameObject.SetActive(false);
                }
                return;
            }

            if (_shelterFrameRect != null && !_shelterFrameRect.gameObject.activeSelf)
            {
                _shelterFrameRect.gameObject.SetActive(true);
            }

            if (currentShade == null) return;

            int durPct = Mathf.Clamp(Mathf.RoundToInt(currentShade.DurabilityNormalized * 100f), 0, 100);
            int occupants = Mathf.Max(1, currentShade.OccupantCount);

            if (_shelterTitleText != null)
            {
                _shelterTitleText.text = currentShade.BuildingDisplayName;
            }

            if (_shelterDurabilityPercentText != null)
            {
                _shelterDurabilityPercentText.text = $"{durPct}%";
            }

            if (_shelterCustomBar != null)
            {
                _shelterCustomBar.SetValue(currentShade.CurrentDurability, currentShade.MaxDurability);
            }

            if (currentShade.IsTargetedByHeatwave && surgeActive)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 8f);
                Color warningCol = Color.Lerp(ColSunAmber, ColSurgeCrimson, pulse);

                if (_shelterIcon != null)
                {
                    _shelterIcon.sprite = _iconSunSprite;
                    _shelterIcon.color = warningCol;
                }
                if (_shelterTitleText != null)
                {
                    _shelterTitleText.color = warningCol;
                }
                if (_shelterStatusText != null)
                {
                    _shelterStatusText.text = occupants > 1 ? $"EROSION ACTIVE [{occupants} OCC: +{occupants * 3.5f:0.0}/s]" : "EROSION ACTIVE // DECAYING";
                    _shelterStatusText.color = warningCol;
                }
                if (_shelterCustomBar != null)
                {
                    _shelterCustomBar.SetFillColor(durPct <= 30 ? ColSurgeCrimson : ColSunAmber);
                }
            }
            else
            {
                Color safeCol = durPct >= 50 ? ColShadeCyan : ColSunAmber;

                if (_shelterIcon != null)
                {
                    _shelterIcon.sprite = _iconShadeSprite;
                    _shelterIcon.color = safeCol;
                }
                if (_shelterTitleText != null)
                {
                    _shelterTitleText.color = safeCol;
                }
                if (_shelterStatusText != null)
                {
                    string occStr = occupants > 1 ? $"{occupants} OCCUPANTS" : "1 OCCUPANT";
                    _shelterStatusText.text = surgeActive ? $"SAFE // UNTARGETED [{occStr}]" : $"STABLE // SAFE [{occStr}]";
                    _shelterStatusText.color = safeCol;
                }
                if (_shelterCustomBar != null)
                {
                    _shelterCustomBar.SetFillColor(safeCol);
                }
            }
        }

        private void UpdateWeaponAndAmmoUI()
        {
            bool unarmed = string.Equals(_cachedWeaponName, "UNARMED", System.StringComparison.OrdinalIgnoreCase);

            if (_weaponNameText != null && _weaponNameText.text != _cachedWeaponName)
            {
                _weaponNameText.text = _cachedWeaponName;
            }

            if (unarmed)
            {
                _lastMagShown = int.MinValue;
                if (_ammoMagText != null)
                {
                    _ammoMagText.text = "--";
                    _ammoMagText.color = ColTextMuted;
                }
                if (_ammoReserveText != null && _lastReserveShown != _cachedReserveAmmo)
                {
                    _lastReserveShown = _cachedReserveAmmo;
                    _ammoReserveText.text = $"/ {_cachedReserveAmmo}";
                }
                if (_ammoStatusBadge != null)
                {
                    _ammoStatusBadge.text = "[ NONE ]";
                    _ammoStatusBadge.color = ColTextMuted;
                }
                if (_reloadTrackRoot != null && _reloadTrackRoot.activeSelf)
                {
                    _reloadTrackRoot.SetActive(false);
                }
                return;
            }

            if (_cachedMagAmmo != _lastMagShown && _ammoMagText != null)
            {
                _lastMagShown = _cachedMagAmmo;
                _ammoMagText.text = _cachedMagAmmo.ToString();
                _ammoMagText.color = _cachedMagAmmo <= 5 ? ColSurgeCrimson : ColPureWhite;
            }

            if (_cachedReserveAmmo != _lastReserveShown && _ammoReserveText != null)
            {
                _lastReserveShown = _cachedReserveAmmo;
                _ammoReserveText.text = $"/ {_cachedReserveAmmo}";
            }

            if (_ammoStatusBadge != null)
            {
                if (_cachedIsReloading)
                {
                    _ammoStatusBadge.text = "[ RELOAD ]";
                    _ammoStatusBadge.color = ColSunAmber;
                }
                else if (_cachedMagAmmo <= 0)
                {
                    _ammoStatusBadge.text = "[ EMPTY ]";
                    _ammoStatusBadge.color = ColSurgeCrimson;
                }
                else
                {
                    _ammoStatusBadge.text = "[ READY ]";
                    _ammoStatusBadge.color = ColReadyTeal;
                }
            }

            if (_reloadProgressBar != null && _reloadTrackRoot != null)
            {
                if (_cachedIsReloading && _localWeapon != null)
                {
                    if (!_reloadTrackRoot.activeSelf) _reloadTrackRoot.SetActive(true);
                    _reloadProgressBar.fillAmount = _localWeapon.ReloadProgress01;
                }
                else if (_reloadTrackRoot.activeSelf)
                {
                    _reloadProgressBar.fillAmount = 0f;
                    _reloadTrackRoot.SetActive(false);
                }
            }
        }

        private void UpdateHotbarUI()
        {
            HotbarSlotType activeSlot = _localHotbar != null ? _localHotbar.ActiveSlot : HotbarSlotType.Weapon;
            bool weaponSelected = activeSlot == HotbarSlotType.Weapon;

            if (_weaponKeybindBadgeText != null)
            {
                _weaponKeybindBadgeText.color = weaponSelected ? ColSunAmber : ColPureWhite;
            }
            ApplyHotbarSelectionVisual(_ammoFrameRect, _weaponSelectionBorders, weaponSelected, ColBorderUnselected);

            if (_hotbarSlotVisuals == null)
            {
                return;
            }

            var catalog = ScorchedItemCatalog.Instance;
            for (int i = 0; i < _hotbarSlotVisuals.Length; i++)
            {
                HotbarSlotVisual vis = _hotbarSlotVisuals[i];
                bool isSelected = activeSlot == vis.SlotType;

                HotbarSlotState state = _localHotbar != null
                    ? _localHotbar.GetConsumableSlotState(vis.SlotType)
                    : default;
                bool hasItem = _localHotbar != null && !state.IsEmpty;

                ItemId displayId = hasItem ? state.BoundItemId : vis.DefaultBaseItemId;
                Sprite iconSprite = catalog != null ? catalog.GetDefinition(displayId)?.Icon : null;
                if (iconSprite == null && catalog != null && displayId != vis.DefaultBaseItemId)
                {
                    iconSprite = catalog.GetDefinition(vis.DefaultBaseItemId)?.Icon;
                }

                if (vis.IconImage != null)
                {
                    if (iconSprite != null)
                    {
                        vis.IconImage.sprite = iconSprite;
                    }
                    vis.IconImage.enabled = vis.IconImage.sprite != null;
                    vis.IconImage.color = hasItem
                        ? ColPureWhite
                        : new Color(1f, 1f, 1f, 0.18f);
                }

                Color gradeColor = hasItem ? GetGradeColor(state.HighestGrade) : ColBorderUnselected;

                if (vis.CountBadgeText != null)
                {
                    string countStr = hasItem ? $"x{state.HighestGradeCount}" : string.Empty;
                    if (vis.CountBadgeText.text != countStr)
                    {
                        vis.CountBadgeText.text = countStr;
                    }
                    vis.CountBadgeText.color = hasItem ? gradeColor : ColTextMuted;
                }

                if (vis.GradePipImage != null)
                {
                    vis.GradePipImage.enabled = hasItem;
                    vis.GradePipImage.color = gradeColor;
                }

                if (vis.KeybindBadgeText != null)
                {
                    vis.KeybindBadgeText.color = isSelected ? ColSunAmber : ColPureWhite;
                }

                ApplyHotbarSelectionVisual(
                    vis.FrameRect,
                    vis.SelectionBorders,
                    isSelected,
                    hasItem ? new Color(gradeColor.r, gradeColor.g, gradeColor.b, 0.72f) : ColBorderUnselected);
            }
        }

        private static Color GetGradeColor(ItemGrade grade)
        {
            switch (grade)
            {
                case ItemGrade.Great: return ColGradeGreat;
                case ItemGrade.Good: return ColGradeGood;
                case ItemGrade.Poor: return ColGradePoor;
                default: return ColBorderUnselected;
            }
        }

        private static void ApplyHotbarSelectionVisual(
            RectTransform frameRect,
            Image[] borderEdges,
            bool isSelected,
            Color unselectedBorderColor)
        {
            if (frameRect != null)
            {
                float targetScale = isSelected ? 1.06f : 1.0f;
                float t = Application.isPlaying ? Mathf.Clamp01(Time.unscaledDeltaTime * 14f) : 1f;
                frameRect.localScale = Vector3.Lerp(frameRect.localScale, Vector3.one * targetScale, t);
            }

            if (borderEdges != null)
            {
                Color c = isSelected ? ColSunAmber : unselectedBorderColor;
                for (int i = 0; i < borderEdges.Length; i++)
                {
                    if (borderEdges[i] != null)
                    {
                        borderEdges[i].color = c;
                    }
                }
            }
        }

        private void UpdateSolarCycleBannerUI()
        {
            HeatwaveHazardManager hazard = HeatwaveHazardManager.Instance;
            if (_solarCycleText != null && _solarPillBg != null)
            {
                if (hazard == null)
                {
                    if (_lastMatchPhaseShown != -100 || _lastSolarSecShown != -99)
                    {
                        _lastMatchPhaseShown = -100;
                        _lastWaveShown = -99;
                        _lastPermanentShown = null;
                        _lastSolarSecShown = -99;
                        _solarCycleText.text = "ARID SUN // STANDBY";
                        _solarCycleText.color = ColTextMuted;
                        _solarPillBg.color = ColFrameTint;
                    }
                }
                else
                {
                    MatchPhase phase = hazard.CurrentMatchPhase;
                    int phaseInt = (int)phase;
                    int wave = Mathf.Max(1, hazard.CurrentWaveNumber);
                    bool permanent = hazard.IsPermanentHeatwave;
                    int sec = Mathf.CeilToInt(Mathf.Max(0f, hazard.TimeUntilNextPhase));
                    bool surge = phase == MatchPhase.HeatwaveActive || permanent;

                    if (_lastMatchPhaseShown != phaseInt ||
                        _lastWaveShown != wave ||
                        _lastPermanentShown != permanent ||
                        (!permanent && _lastSolarSecShown != sec))
                    {
                        _lastMatchPhaseShown = phaseInt;
                        _lastWaveShown = wave;
                        _lastPermanentShown = permanent;
                        _lastSolarSecShown = sec;

                        int m = sec / 60;
                        int s = sec % 60;

                        if (permanent)
                        {
                            _solarCycleText.text = "ENDLESS HEATWAVE";
                        }
                        else if (phase == MatchPhase.GraceWindow)
                        {
                            _solarCycleText.text = $"SAFE WINDOW // {m}:{s:00}";
                        }
                        else if (phase == MatchPhase.HeatwaveActive)
                        {
                            _solarCycleText.text = $"HEATWAVE {wave} // {m}:{s:00}";
                        }
                        else
                        {
                            _solarCycleText.text = $"HEATWAVE {wave} IN {m}:{s:00}";
                        }
                    }

                    if (surge)
                    {
                        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5.5f);
                        _solarCycleText.color = Color.Lerp(ColSunAmber, ColSurgeCrimson, 0.35f + 0.65f * pulse);
                        _solarPillBg.color = Color.Lerp(new Color(1.0f, 0.82f, 0.80f, 1f), new Color(1.0f, 0.68f, 0.66f, 1f), pulse);
                    }
                    else if (phase == MatchPhase.GraceWindow)
                    {
                        _solarCycleText.color = ColShadeCyan;
                        _solarPillBg.color = ColFrameTint;
                    }
                    else
                    {
                        _solarCycleText.color = ColTimerText;
                        _solarPillBg.color = ColFrameTint;
                    }
                }
            }

            if (_matchSubStatusText != null)
            {
                NetworkMatchManager mm = NetworkMatchManager.Instance;
                if (mm != null && !mm.IsCombatActive)
                {
                    if (!_matchSubStatusText.gameObject.activeSelf) _matchSubStatusText.gameObject.SetActive(true);
                    _matchSubStatusText.text = mm.IsTeamMatch
                        ? $"TEAM MATCH // ALPHA {mm.TeamAlphaCount} VS BRAVO {mm.TeamBravoCount}"
                        : "FREE FOR ALL // STANDBY";
                }
                else if (_matchSubStatusText.gameObject.activeSelf)
                {
                    _matchSubStatusText.gameObject.SetActive(false);
                }
            }
        }

        private void UpdateCrosshairAndVignette()
        {
            if (_crosshairRoot != null)
            {
                bool showCrosshair = _cachedLifeState == PlayerLifeState.Alive;
                if (_crosshairRoot.activeSelf != showCrosshair)
                {
                    _crosshairRoot.SetActive(showCrosshair);
                }

                if (showCrosshair)
                {
                    float targetGap = _crosshairBaseGap;
                    if (_localPlayer != null)
                    {
                        float flatSpeed = _localPlayer.WorldVelocity.magnitude;
                        if (_localPlayer.IsSprinting) targetGap += 7f;
                        else if (flatSpeed > 0.3f) targetGap += 3.5f;
                        if (_localPlayer.IsAirborne) targetGap += 6f;
                    }

                    _recoilPulse = Mathf.MoveTowards(_recoilPulse, 0f, Time.deltaTime * 38f);
                    targetGap = Mathf.Min(_crosshairMaxGap, targetGap + _recoilPulse);
                    _crosshairGap = Mathf.Lerp(_crosshairGap, targetGap, Time.deltaTime * 18f);

                    if (_tickTop != null) _tickTop.anchoredPosition = new Vector2(0f, _crosshairGap + _crosshairTickLength * 0.5f);
                    if (_tickBottom != null) _tickBottom.anchoredPosition = new Vector2(0f, -(_crosshairGap + _crosshairTickLength * 0.5f));
                    if (_tickLeft != null) _tickLeft.anchoredPosition = new Vector2(-(_crosshairGap + _crosshairTickLength * 0.5f), 0f);
                    if (_tickRight != null) _tickRight.anchoredPosition = new Vector2(_crosshairGap + _crosshairTickLength * 0.5f, 0f);
                }
            }

            if (_heatstrokeVignette != null)
            {
                bool overheating = _cachedLifeState == PlayerLifeState.Alive &&
                                   _localThermal != null &&
                                   (_localThermal.HeatExposure >= 80f || _localThermal.Hydration <= 20f);

                float targetAlpha = 0f;
                if (overheating)
                {
                    float intensity = Mathf.Max(
                        Mathf.InverseLerp(80f, 100f, _localThermal.HeatExposure),
                        Mathf.InverseLerp(20f, 0f, _localThermal.Hydration));
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4.5f);
                    targetAlpha = Mathf.Lerp(0.32f, 0.68f, intensity) + (pulse * 0.10f);
                }

                _vignetteAlpha = Mathf.MoveTowards(_vignetteAlpha, targetAlpha, Time.deltaTime * 1.8f);
                _heatstrokeVignette.color = new Color(1f, 1f, 1f, Mathf.Clamp01(_vignetteAlpha));
                if (_heatstrokeVignette.enabled != (_vignetteAlpha > 0.005f))
                {
                    _heatstrokeVignette.enabled = _vignetteAlpha > 0.005f;
                }
            }
        }

        private void UpdateLifeStateBannerUI()
        {
            if (_lifeStateRoot == null)
            {
                return;
            }

            bool show = _cachedLifeState != PlayerLifeState.Alive;
            if (_lifeStateRoot.activeSelf != show)
            {
                _lifeStateRoot.SetActive(show);
            }
            if (!show)
            {
                return;
            }

            if (_cachedLifeState == PlayerLifeState.Downed)
            {
                int downInt = Mathf.CeilToInt(_cachedDownedHealth);
                if (downInt != _lastDownedHpShown)
                {
                    _lastDownedHpShown = downInt;
                    _lifeStateTitleText.text = $"DOWNED // BLEEDOUT {downInt} HP";
                }
                _lifeStateTitleText.color = ColSunAmber;
                _lifeStatePromptText.text = "HOLD [E] NEAR COVER TO SELF-REVIVE OR CRAWL";
                _bleedoutCustomBar.SetValue(_cachedDownedHealth, _cachedMaxDownedHealth);

                bool showRev = _cachedReviveProgress > 0.01f;
                if (_reviveRoot.activeSelf != showRev) _reviveRoot.SetActive(showRev);
                if (showRev)
                {
                    _reviveCustomBar.SetValue(_cachedReviveProgress, 1f);
                    int revPct = Mathf.RoundToInt(_cachedReviveProgress * 100f);
                    if (revPct != _lastRevivePctShown)
                    {
                        _lastRevivePctShown = revPct;
                        _reviveLabelText.text = $"REVIVING {revPct}%";
                    }
                }
            }
            else
            {
                _lastDownedHpShown = -1;
                int remSec = _localHealth != null ? Mathf.CeilToInt(Mathf.Max(0f, _localHealth.CurrentRespawnTimeRemaining)) : 0;
                int deaths = _localHealth != null ? Mathf.Max(1, _localHealth.CurrentDeathCount) : 1;
                _lifeStateTitleText.text = remSec > 0
                    ? $"INCAPACITATED // RESPAWN IN {remSec}s"
                    : "INCAPACITATED // REDEPLOYING...";
                _lifeStateTitleText.color = ColSurgeCrimson;
                _lifeStatePromptText.text = $"DEATH #{deaths} — NEXT RESPAWN ESCALATES (+10s)";
                _bleedoutCustomBar.SetValue(0f, _cachedMaxDownedHealth);
                if (_reviveRoot.activeSelf) _reviveRoot.SetActive(false);
            }
        }
        #endregion

        #region HUD Construction (Tech-Brutalist GDD Spec)
        private void ResolveFont()
        {
            _uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_uiFont == null)
            {
                _uiFont = Font.CreateDynamicFontFromOSFont("Arial", 14);
            }
        }

        private void EnsureFallbackSprites()
        {
            if (_gddTacticalFrameSprite == null) _gddTacticalFrameSprite = CreateSolidSprite(new Color(0.078f, 0.082f, 0.094f, 0.82f));
            if (_customBarTrackSprite == null) _customBarTrackSprite = CreateSolidSprite(new Color(0.035f, 0.039f, 0.047f, 0.92f));
            if (_customBarFillSegmentedSprite == null) _customBarFillSegmentedSprite = CreateSolidSprite(Color.white);
            if (_customBarFillRazorSprite == null) _customBarFillRazorSprite = CreateSolidSprite(Color.white);
            if (_iconHealthCrossSprite == null) _iconHealthCrossSprite = CreateSolidSprite(Color.white);
            if (_iconSunSprite == null) _iconSunSprite = CreateSolidSprite(ColSunAmber);
            if (_iconShadeSprite == null) _iconShadeSprite = CreateSolidSprite(ColShadeCyan);
            if (_iconHydrationDropSprite == null) _iconHydrationDropSprite = CreateSolidSprite(ColHydrationAzure);
            if (_heatstrokeVignetteSprite == null) _heatstrokeVignetteSprite = CreateVignetteFallbackSprite();

            var catalog = ScorchedItemCatalog.Instance;
            if (_radialProgressRingSprite == null && catalog != null) _radialProgressRingSprite = catalog.RadialProgressRingSprite;
            if (_cabalSlotCellSprite == null && catalog != null) _cabalSlotCellSprite = catalog.CabalSlotCellSprite;
            if (_radialProgressRingSprite == null) _radialProgressRingSprite = CreateSolidSprite(ColSunAmber);
        }

        private static Sprite CreateSolidSprite(Color c)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.SetPixels(new[] { c, c, c, c });
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
        }

        private static Sprite CreateVignetteFallbackSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x / (float)(size - 1)) * 2f - 1f;
                    float v = (y / (float)(size - 1)) * 2f - 1f;
                    float dist = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(Mathf.InverseLerp(0.55f, 1.35f, dist)) * 0.7f;
                    tex.SetPixel(x, y, new Color(1f, 0.35f, 0.08f, a));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private void BuildHUDHierarchy()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            _hudCanvas = gameObject.GetComponent<Canvas>();
            if (_hudCanvas == null) _hudCanvas = gameObject.AddComponent<Canvas>();
            _hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _hudCanvas.sortingOrder = 10;
            _hudCanvas.pixelPerfect = true;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }

            // Layer 0: Heatstroke Vignette
            _heatstrokeVignette = CreateFullScreenImage("HeatstrokeVignette", _heatstrokeVignetteSprite, new Color(1f, 1f, 1f, 0f));
            _heatstrokeVignette.enabled = false;

            // Layer 1: Center Footstep Radar & Crosshair
            BuildCenterRadarAndCrosshair();

            // Layer 2: Zone 1 - Bottom-Left Vitals & Thermal Cluster (320x156 at 28, 28)
            BuildBottomLeftVitalsCluster();
            BuildShelterDurabilityWidget();

            // Layer 3: Zone 2 - Bottom-Right Weapon & Ammo Box (200x92 at -28, 28) + 3-Slot Consumable Hotbar
            BuildBottomRightAmmoBox();
            BuildBottomRightHotbar();

            // Layer 4: Zone 3 - Top-Center Heatwave Timer Box (240x36 at 0, -20)
            BuildTopCenterHeatwaveBox();

            // Layer 5: DBNO / Dead Compact Overlay
            BuildLifeStateOverlay();

            // Layer 6: Scavenge Radial Ring + Supply Drop / Inventory Alert Banner + Inventory/Crafting Overlays
            BuildInteractionAndAlertWidgets();
            BuildInventoryAndCraftingOverlays();
        }

        private void BuildInteractionAndAlertWidgets()
        {
            var promptRect = CreateRect("Center_InteractionPrompt", transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, -108f), new Vector2(360f, 76f));
            _interactionRoot = promptRect.gameObject;

            var bgRingRect = CreateRect("RadialRingTrack", promptRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, 0f), new Vector2(46f, 46f));
            _interactionRadialBg = bgRingRect.gameObject.AddComponent<Image>();
            _interactionRadialBg.sprite = _radialProgressRingSprite;
            _interactionRadialBg.color = new Color(0.15f, 0.16f, 0.18f, 0.72f);
            _interactionRadialBg.raycastTarget = false;
            _interactionRadialBg.enabled = false;

            var fillRingRect = CreateRect("RadialRingFill", promptRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, 0f), new Vector2(46f, 46f));
            _interactionRadialRing = fillRingRect.gameObject.AddComponent<Image>();
            _interactionRadialRing.sprite = _radialProgressRingSprite;
            _interactionRadialRing.type = Image.Type.Filled;
            _interactionRadialRing.fillMethod = Image.FillMethod.Radial360;
            _interactionRadialRing.fillOrigin = (int)Image.Origin360.Top;
            _interactionRadialRing.fillClockwise = true;
            _interactionRadialRing.fillAmount = 0f;
            _interactionRadialRing.color = ColSunAmber;
            _interactionRadialRing.raycastTarget = false;
            _interactionRadialRing.enabled = false;

            var pillRect = CreateTacticalBox(
                "PromptTextPill",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 0f), new Vector2(320f, 26f),
                out _);
            pillRect.SetParent(promptRect, false);
            pillRect.anchoredPosition = new Vector2(0f, 0f);

            var pillInner = CreatePaddedInnerContainer(pillRect, 12f, 12f, 2f, 2f);
            _interactionPromptText = CreateStretchedText("PromptLabel", pillInner, "[HOLD E] Scavenge", 11, FontStyle.Bold, TextAnchor.MiddleCenter,
                ColPureWhite, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            _interactionRoot.SetActive(false);

            var alertRect = CreateTacticalBox(
                "TopCenter_AlertBanner",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -84f), new Vector2(360f, 34f),
                out var alertImg);
            alertImg.color = new Color(1.0f, 0.85f, 0.65f, 1f);
            _alertBannerRoot = alertRect.gameObject;

            var alertInner = CreatePaddedInnerContainer(alertRect, 14f, 14f, 4f, 4f);
            _alertBannerText = CreateStretchedText("AlertBannerText", alertInner, "SUPPLY DROP INBOUND", 12, FontStyle.Bold, TextAnchor.MiddleCenter,
                ColSunAmber, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _alertBannerRoot.SetActive(false);
        }

        private void BuildInventoryAndCraftingOverlays()
        {
            var invGo = new GameObject("ScorchedInventoryOverlay", typeof(RectTransform), typeof(ScorchedInventoryUI));
            invGo.transform.SetParent(transform, false);
            var invRect = invGo.GetComponent<RectTransform>();
            invRect.anchorMin = Vector2.zero;
            invRect.anchorMax = Vector2.one;
            invRect.offsetMin = Vector2.zero;
            invRect.offsetMax = Vector2.zero;
            _inventoryUI = invGo.GetComponent<ScorchedInventoryUI>();
            _inventoryUI.Initialize(_hudCanvas, _uiFont, _gddTacticalFrameSprite, _cabalSlotCellSprite);

            var craftGo = new GameObject("ScorchedCraftingOverlay", typeof(RectTransform), typeof(ScorchedCraftingUI));
            craftGo.transform.SetParent(transform, false);
            var craftRect = craftGo.GetComponent<RectTransform>();
            craftRect.anchorMin = Vector2.zero;
            craftRect.anchorMax = Vector2.one;
            craftRect.offsetMin = Vector2.zero;
            craftRect.offsetMax = Vector2.zero;
            _craftingUI = craftGo.GetComponent<ScorchedCraftingUI>();
            _craftingUI.Initialize(_uiFont, _gddTacticalFrameSprite);

            var lootGo = new GameObject("ScorchedContainerLootOverlay", typeof(RectTransform), typeof(ScorchedContainerLootUI));
            lootGo.transform.SetParent(transform, false);
            var lootRect = lootGo.GetComponent<RectTransform>();
            lootRect.anchorMin = Vector2.zero;
            lootRect.anchorMax = Vector2.one;
            lootRect.offsetMin = Vector2.zero;
            lootRect.offsetMax = Vector2.zero;
            _containerLootUI = lootGo.GetComponent<ScorchedContainerLootUI>();
            _containerLootUI.Initialize(_uiFont, _gddTacticalFrameSprite);
        }

        private void BuildBottomLeftVitalsCluster()
        {
            _vitalsFrameRect = CreateTacticalBox(
                "BottomLeft_VitalsCluster",
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(28f, 28f), new Vector2(320f, 156f),
                out _);

            // InnerContent with explicit padding (Left: 20, Right: 20, Top: 14, Bottom: 14 => 280x128 usable)
            RectTransform inner = CreatePaddedInnerContainer(_vitalsFrameRect, 20f, 20f, 14f, 14f);

            // Row 1 (Height 20px, Y: 0..-20): Thermal Status Icon + Text left, HEAT 0% right
            RectTransform row1 = CreateTopBand("Row1_ThermalStatus", inner, 0f, 20f);
            RectTransform statusIconRect = CreateRect("StatusIcon", row1, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(12f, 12f));
            _thermalStatusIcon = statusIconRect.gameObject.AddComponent<Image>();
            _thermalStatusIcon.sprite = _iconShadeSprite;
            _thermalStatusIcon.preserveAspect = true;
            _thermalStatusIcon.color = ColShadeCyan;
            _thermalStatusIcon.raycastTarget = false;

            _thermalStatusText = CreateStretchedText("StatusLabel", row1, "IN SHADE // SAFE", 10, FontStyle.Bold, TextAnchor.MiddleLeft, ColShadeCyan,
                new Vector2(0f, 0f), new Vector2(0.64f, 1f), new Vector2(17f, 0f), new Vector2(0f, 0f));

            _heatPercentLabel = CreateStretchedText("HeatReadout", row1, "HEAT 0%", 11, FontStyle.Bold, TextAnchor.MiddleRight, ColTextLight,
                new Vector2(0.64f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 0f));

            // Hairline technical divider
            RectTransform divider = CreateTopBand("Divider", inner, -22f, 1f);
            var divImg = divider.gameObject.AddComponent<Image>();
            divImg.color = ColDivider;
            divImg.raycastTarget = false;

            // Row 2 (Height 38px, Y: -26..-64): Heart/Cross Icon + Bold "100" + "/ 100 HP" + 6px Razor Custom Bar
            RectTransform row2 = CreateTopBand("Row2_HPCluster", inner, -26f, 38f);
            RectTransform hpHeader = CreateTopBand("HPHeader", row2, 0f, 28f);

            var hlg = hpHeader.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.LowerLeft;
            hlg.spacing = 6f;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            RectTransform crossRect = CreateRect("HealthCrossIcon", hpHeader, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(13f, 22f));
            var crossContainerImg = CreateRect("CrossGraphic", crossRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -1f), new Vector2(12f, 12f));
            var crossImg = crossContainerImg.gameObject.AddComponent<Image>();
            crossImg.sprite = _iconHealthCrossSprite;
            crossImg.preserveAspect = true;
            crossImg.color = ColHpOffWhite;
            crossImg.raycastTarget = false;

            _healthBigText = CreateLayoutText("HPValue", hpHeader, "100", 24, FontStyle.Bold, TextAnchor.LowerLeft, ColPureWhite, 64f, 28f);
            var hpFitter = _healthBigText.gameObject.AddComponent<ContentSizeFitter>();
            hpFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            _healthMaxSubText = CreateLayoutText("HPMaxLabel", hpHeader, "/ 100 HP", 11, FontStyle.Bold, TextAnchor.LowerLeft, ColTextMuted, 72f, 20f);
            var maxFitter = _healthMaxSubText.gameObject.AddComponent<ContentSizeFitter>();
            maxFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform hpBarRect = CreateBottomBand("HPCustomBar", row2, 0f, 6f);
            _healthCustomBar = hpBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _healthCustomBar.Configure(
                _customBarTrackSprite,
                _customBarFillRazorSprite,
                _customBarFillRazorSprite,
                ColHpOffWhite,
                ColHpGhostRed,
                ColPureWhite,
                showLeadingPip: true);

            // Row 3 (Height 26px, Y: -69..-95): STAMINA + 100% + 4px Razor Custom Bar
            RectTransform row3 = CreateTopBand("Row3_StaminaCluster", inner, -69f, 26f);
            CreateStretchedText("StaminaLabel", row3, "STAMINA", 10, FontStyle.Bold, TextAnchor.UpperLeft, ColTextMuted,
                new Vector2(0f, 0.35f), new Vector2(0.65f, 1f), Vector2.zero, Vector2.zero);
            _staminaPercentText = CreateStretchedText("StaminaPct", row3, "100%", 10, FontStyle.Bold, TextAnchor.UpperRight, ColTextLight,
                new Vector2(0.65f, 0.35f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            RectTransform stamBarRect = CreateBottomBand("StaminaCustomBar", row3, 0f, 4f);
            _staminaCustomBar = stamBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _staminaCustomBar.Configure(
                _customBarTrackSprite,
                _customBarFillRazorSprite,
                _customBarFillRazorSprite,
                ColStaminaSilver,
                ColStaminaGhost,
                ColPureWhite,
                showLeadingPip: true);

            // Row 4 (Height 28px, Y: -100..-128): Side-by-side Hydration (Left 58%) & Exposure (Right 38%, 4% gap)
            RectTransform row4 = CreateTopBand("Row4_ThermalSegmentedBars", inner, -100f, 28f);

            // Hydration (Left 58%)
            RectTransform hydCol = CreateSubColumn("HydrationColumn", row4, 0f, 0.58f);
            RectTransform dropRect = CreateRect("DropIcon", hydCol, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -2f), new Vector2(10f, 10f));
            var dropImg = dropRect.gameObject.AddComponent<Image>();
            dropImg.sprite = _iconHydrationDropSprite;
            dropImg.preserveAspect = true;
            dropImg.color = ColHydrationAzure;
            dropImg.raycastTarget = false;

            _hydrationLabelText = CreateStretchedText("HydrationLabel", hydCol, "HYDRATION 100%", 9, FontStyle.Bold, TextAnchor.UpperLeft, ColTextLight,
                new Vector2(0f, 0.42f), new Vector2(1f, 1f), new Vector2(14f, 0f), Vector2.zero);
            _hydrationLabelText.resizeTextForBestFit = true;
            _hydrationLabelText.resizeTextMinSize = 8;
            _hydrationLabelText.resizeTextMaxSize = 10;

            RectTransform hydBarRect = CreateBottomBand("HydrationCustomBar", hydCol, 0f, 6f);
            _hydrationCustomBar = hydBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _hydrationCustomBar.Configure(
                _customBarTrackSprite,
                _customBarFillSegmentedSprite,
                _customBarFillRazorSprite,
                ColHydrationAzure,
                new Color(ColHydrationAzure.r, ColHydrationAzure.g, ColHydrationAzure.b, 0.35f),
                ColPureWhite,
                showLeadingPip: true);

            // Exposure (Right 38%, anchorMin=0.62..1.0)
            RectTransform expCol = CreateSubColumn("ExposureColumn", row4, 0.62f, 1.0f);
            RectTransform expIconRect = CreateRect("FlameSunIcon", expCol, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -2f), new Vector2(10f, 10f));
            var expIconImg = expIconRect.gameObject.AddComponent<Image>();
            expIconImg.sprite = _iconSunSprite;
            expIconImg.preserveAspect = true;
            expIconImg.color = ColHeatAmberRed;
            expIconImg.raycastTarget = false;

            _exposureLabelText = CreateStretchedText("ExposureLabel", expCol, "EXPOSURE", 9, FontStyle.Bold, TextAnchor.UpperLeft, ColTextLight,
                new Vector2(0f, 0.42f), new Vector2(1f, 1f), new Vector2(14f, 0f), Vector2.zero);
            _exposureLabelText.resizeTextForBestFit = true;
            _exposureLabelText.resizeTextMinSize = 8;
            _exposureLabelText.resizeTextMaxSize = 10;

            RectTransform expBarRect = CreateBottomBand("ExposureCustomBar", expCol, 0f, 6f);
            _exposureCustomBar = expBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _exposureCustomBar.Configure(
                _customBarTrackSprite,
                _customBarFillSegmentedSprite,
                _customBarFillRazorSprite,
                ColHeatAmberRed,
                new Color(ColHeatAmberRed.r, ColHeatAmberRed.g, ColHeatAmberRed.b, 0.35f),
                ColPureWhite,
                showLeadingPip: true,
                initialFill01: 0f);
        }

        private void BuildShelterDurabilityWidget()
        {
            // Positioned directly above vitals (X: 28, Y: 194, Width: 320, Height: 48)
            _shelterFrameRect = CreateTacticalBox(
                "BottomLeft_ShelterDurability",
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(28f, 194f), new Vector2(320f, 48f),
                out _);

            _shelterCanvasGroup = _shelterFrameRect.gameObject.AddComponent<CanvasGroup>();
            _shelterCanvasGroup.alpha = 0f;
            _shelterCanvasGroup.blocksRaycasts = false;
            _shelterCanvasGroup.interactable = false;

            RectTransform inner = CreatePaddedInnerContainer(_shelterFrameRect, 14f, 14f, 6f, 6f);

            // Row 1 (Height 16px): Shelter Icon + Title left, Occupancy/Status right
            RectTransform row1 = CreateTopBand("Row1_ShelterHeader", inner, 0f, 16f);
            RectTransform iconRect = CreateRect("ShelterIcon", row1, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(12f, 12f));
            _shelterIcon = iconRect.gameObject.AddComponent<Image>();
            _shelterIcon.sprite = _iconShadeSprite;
            _shelterIcon.preserveAspect = true;
            _shelterIcon.color = ColShadeCyan;
            _shelterIcon.raycastTarget = false;

            _shelterTitleText = CreateStretchedText("ShelterTitle", row1, "SHELTER // SAFE ZONE", 10, FontStyle.Bold, TextAnchor.MiddleLeft, ColShadeCyan,
                new Vector2(0f, 0f), new Vector2(0.55f, 1f), new Vector2(16f, 0f), Vector2.zero);

            _shelterStatusText = CreateStretchedText("ShelterStatus", row1, "STABLE [1 OCC]", 10, FontStyle.Bold, TextAnchor.MiddleRight, ColTextLight,
                new Vector2(0.55f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            // Row 2 (Height 10px, anchored at bottom): Durability Custom Bar + percentage readout
            RectTransform row2 = CreateBottomBand("Row2_DurabilityBar", inner, 0f, 10f);
            RectTransform barRect = CreateRect("DurabilityCustomBar", row2, new Vector2(0f, 0f), new Vector2(0.74f, 1f), new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
            _shelterCustomBar = barRect.gameObject.AddComponent<ScorchedCustomBar>();
            _shelterCustomBar.Configure(
                _customBarTrackSprite,
                _customBarFillSegmentedSprite,
                _customBarFillRazorSprite,
                ColShadeCyan,
                ColHpGhostRed,
                ColPureWhite,
                false,
                0f);

            _shelterDurabilityPercentText = CreateStretchedText("DurabilityPctLabel", row2, "100%", 10, FontStyle.Bold, TextAnchor.MiddleRight, ColPureWhite,
                new Vector2(0.76f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            _shelterFrameRect.gameObject.SetActive(false);
        }

        private void BuildBottomRightAmmoBox()
        {
            // Center-pivoted at (-128, 74) = exact bounds X: [-228, -28], Y: [28, 120] while allowing centered 1.06x active lerp
            _ammoFrameRect = CreateTacticalBox(
                "BottomRight_WeaponAmmoBox",
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0.5f),
                new Vector2(-128f, 74f), new Vector2(200f, 92f),
                out _);
            _weaponSelectionBorders = CreateSelectionBorderEdges(_ammoFrameRect, ColSunAmber, 2f);

            // Strict 16px padding on all 4 sides => 168x60 inner area
            RectTransform inner = CreatePaddedInnerContainer(_ammoFrameRect, 16f, 16f, 16f, 16f);

            // Top line (Y: 0..-18): [1] badge + AK-47 left + [ READY ] right
            RectTransform topLine = CreateTopBand("Row1_WeaponHeader", inner, 0f, 18f);
            _weaponKeybindBadgeText = CreateStretchedText("Slot1KeyBadge", topLine, "[1]", 10, FontStyle.Bold, TextAnchor.MiddleLeft, ColSunAmber,
                new Vector2(0f, 0f), new Vector2(0.15f, 1f), Vector2.zero, Vector2.zero);

            _weaponNameText = CreateStretchedText("WeaponName", topLine, "AK-47", 11, FontStyle.Bold, TextAnchor.MiddleLeft, ColWeaponMuted,
                new Vector2(0.15f, 0f), new Vector2(0.56f, 1f), Vector2.zero, Vector2.zero);
            _weaponNameText.resizeTextForBestFit = true;
            _weaponNameText.resizeTextMinSize = 8;
            _weaponNameText.resizeTextMaxSize = 11;

            _ammoStatusBadge = CreateStretchedText("AmmoStatusBadge", topLine, "[ READY ]", 10, FontStyle.Bold, TextAnchor.MiddleRight, ColReadyTeal,
                new Vector2(0.56f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            // Center/Bottom line (Y: -20..-54): Centered bold "30" + "/ 120"
            RectTransform ammoRow = CreateTopBand("Row2_AmmoReadout", inner, -20f, 34f);
            var hlg = ammoRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.LowerCenter;
            hlg.spacing = 6f;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            _ammoMagText = CreateLayoutText("MagAmmo", ammoRow, "30", 26, FontStyle.Bold, TextAnchor.LowerRight, ColPureWhite, 54f, 32f);
            var magFit = _ammoMagText.gameObject.AddComponent<ContentSizeFitter>();
            magFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            _ammoReserveText = CreateLayoutText("ReserveAmmo", ammoRow, "/ 120", 15, FontStyle.Bold, TextAnchor.LowerLeft, ColTextMuted, 58f, 24f);
            var resFit = _ammoReserveText.gameObject.AddComponent<ContentSizeFitter>();
            resFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Bottom accent: 3px custom reload progress bar along bottom inner margin
            RectTransform reloadTrack = CreateBottomBand("ReloadProgressTrack", inner, 0f, 3f);
            _reloadTrackRoot = reloadTrack.gameObject;
            var trackImg = reloadTrack.gameObject.AddComponent<Image>();
            trackImg.sprite = _customBarTrackSprite;
            trackImg.color = Color.white;
            trackImg.raycastTarget = false;

            RectTransform reloadFill = CreateRect("ReloadFill", reloadTrack, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            _reloadProgressBar = reloadFill.gameObject.AddComponent<Image>();
            _reloadProgressBar.sprite = _customBarFillRazorSprite;
            _reloadProgressBar.type = Image.Type.Filled;
            _reloadProgressBar.fillMethod = Image.FillMethod.Horizontal;
            _reloadProgressBar.fillOrigin = (int)Image.OriginHorizontal.Left;
            _reloadProgressBar.color = ColSunAmber;
            _reloadProgressBar.fillAmount = 0f;
            _reloadProgressBar.raycastTarget = false;

            _reloadTrackRoot.SetActive(false);
        }

        private void BuildBottomRightHotbar()
        {
            // Placed strictly 10px left of BottomRight_WeaponAmmoBox (-228 - 10 = -238) along bottom margin Y: 28.
            // Total width = 3 * 68 + 2 * 8 = 220px, height = 76px.
            _consumableHotbarRect = CreateRect(
                "ConsumableHotbar_Container",
                transform,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-238f, 28f), new Vector2(220f, 76f));

            _hotbarSlotVisuals = new HotbarSlotVisual[3];
            _hotbarSlotVisuals[0] = CreateHotbarConsumableBox(_consumableHotbarRect, 0, HotbarSlotType.Water, ItemId.Water_Poor, "[2]");
            _hotbarSlotVisuals[1] = CreateHotbarConsumableBox(_consumableHotbarRect, 1, HotbarSlotType.Food, ItemId.Food_Poor, "[3]");
            _hotbarSlotVisuals[2] = CreateHotbarConsumableBox(_consumableHotbarRect, 2, HotbarSlotType.Bandage, ItemId.Bandage_Poor, "[4]");
        }

        private HotbarSlotVisual CreateHotbarConsumableBox(
            RectTransform container,
            int index,
            HotbarSlotType slotType,
            ItemId defaultBaseItemId,
            string keyLabel)
        {
            const float boxW = 68f;
            const float boxH = 76f;
            const float spacing = 8f;
            float centerX = (boxW * 0.5f) + index * (boxW + spacing);

            RectTransform boxRect = CreateRect(
                $"Slot{(int)slotType}_{slotType}",
                container,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0.5f, 0.5f),
                new Vector2(centerX, boxH * 0.5f), new Vector2(boxW, boxH));

            Image bg = boxRect.gameObject.AddComponent<Image>();
            bg.sprite = _gddTacticalFrameSprite;
            bg.type = _gddTacticalFrameSprite != null && _gddTacticalFrameSprite.border != Vector4.zero
                ? Image.Type.Sliced
                : Image.Type.Simple;
            bg.fillCenter = true;
            bg.color = ColFrameTint;
            bg.raycastTarget = false;
            boxRect.gameObject.AddComponent<RectMask2D>();

            Image[] borders = CreateSelectionBorderEdges(boxRect, ColBorderUnselected, 1.5f);

            RectTransform inner = CreatePaddedInnerContainer(boxRect, 6f, 6f, 6f, 6f);
            RectTransform topBand = CreateTopBand("TopBadges", inner, 0f, 14f);

            Text keyBadge = CreateStretchedText(
                "KeyBadge", topBand, keyLabel, 10, FontStyle.Bold, TextAnchor.MiddleLeft, ColPureWhite,
                new Vector2(0f, 0f), new Vector2(0.45f, 1f), Vector2.zero, Vector2.zero);

            Text countBadge = CreateStretchedText(
                "CountBadge", topBand, string.Empty, 9, FontStyle.Bold, TextAnchor.MiddleRight, ColTextLight,
                new Vector2(0.34f, 0f), new Vector2(0.84f, 1f), Vector2.zero, Vector2.zero);

            RectTransform pipRect = CreateRect(
                "GradePip", topBand,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                Vector2.zero, new Vector2(6f, 6f));
            Image gradePip = pipRect.gameObject.AddComponent<Image>();
            gradePip.color = ColBorderUnselected;
            gradePip.raycastTarget = false;
            gradePip.enabled = false;

            // Center 42x42 item icon positioned at y = -6px inside the 68x76 box (clear of TopBadges & edges)
            RectTransform iconRect = CreateRect(
                "ItemIcon", boxRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, -6f), new Vector2(42f, 42f));
            Image iconImg = iconRect.gameObject.AddComponent<Image>();
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            Sprite baseSprite = ScorchedItemCatalog.Instance != null
                ? ScorchedItemCatalog.Instance.GetDefinition(defaultBaseItemId)?.Icon
                : null;
            iconImg.sprite = baseSprite;
            iconImg.enabled = baseSprite != null;
            iconImg.color = new Color(1f, 1f, 1f, 0.18f);

            return new HotbarSlotVisual
            {
                SlotType = slotType,
                DefaultBaseItemId = defaultBaseItemId,
                FrameRect = boxRect,
                BackgroundImage = bg,
                IconImage = iconImg,
                KeybindBadgeText = keyBadge,
                CountBadgeText = countBadge,
                GradePipImage = gradePip,
                SelectionBorders = borders
            };
        }

        private Image[] CreateSelectionBorderEdges(RectTransform parent, Color initialColor, float thickness)
        {
            var edges = new Image[4];
            edges[0] = CreateEdgeLine("Border_Top", parent, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, thickness), initialColor);
            edges[1] = CreateEdgeLine("Border_Bottom", parent, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, thickness), initialColor);
            edges[2] = CreateEdgeLine("Border_Left", parent, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(thickness, 0f), initialColor);
            edges[3] = CreateEdgeLine("Border_Right", parent, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(thickness, 0f), initialColor);
            return edges;
        }

        private static Image CreateEdgeLine(string name, RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Color color)
        {
            RectTransform rect = CreateRect(name, parent, anchorMin, anchorMax, pivot, Vector2.zero, sizeDelta);
            Image img = rect.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private void BuildTopCenterHeatwaveBox()
        {
            _solarFrameRect = CreateTacticalBox(
                "TopCenter_HeatwaveTimerBox",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -20f), new Vector2(240f, 36f),
                out _solarPillBg);

            // 16px horizontal padding so centered label stays >=16px clear of border
            RectTransform inner = CreatePaddedInnerContainer(_solarFrameRect, 16f, 16f, 4f, 4f);
            _solarCycleText = CreateStretchedText("SolarCycleLabel", inner, "HEATWAVE IN 0:45", 11, FontStyle.Bold, TextAnchor.MiddleCenter, ColTimerText,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _solarCycleText.resizeTextForBestFit = true;
            _solarCycleText.resizeTextMinSize = 8;
            _solarCycleText.resizeTextMaxSize = 11;

            _matchSubStatusText = CreateText("MatchSubStatus", transform, string.Empty, 9, FontStyle.Bold, TextAnchor.MiddleCenter, ColTextMuted,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -62f), new Vector2(240f, 16f));
            _matchSubStatusText.gameObject.SetActive(false);
        }

        private void BuildCenterRadarAndCrosshair()
        {
            RectTransform radarRect = CreateRect("Center_FootstepRadar", transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(240f, 240f));
            _footstepRadar = gameObject.GetComponent<FootstepRadarUI>();
            if (_footstepRadar == null)
            {
                _footstepRadar = gameObject.AddComponent<FootstepRadarUI>();
            }
            _footstepRadar.Configure(radarRect, _footstepArcSprite);

            RectTransform crossRect = CreateRect("Center_Crosshair", transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64f, 64f));
            _crosshairRoot = crossRect.gameObject;

            CreateCrosshairSegment("CenterDot", crossRect, Vector2.zero, new Vector2(2f, 2f));
            _tickTop = CreateCrosshairSegment("TickTop", crossRect, new Vector2(0f, _crosshairBaseGap + _crosshairTickLength * 0.5f), new Vector2(_crosshairTickThickness, _crosshairTickLength));
            _tickBottom = CreateCrosshairSegment("TickBottom", crossRect, new Vector2(0f, -(_crosshairBaseGap + _crosshairTickLength * 0.5f)), new Vector2(_crosshairTickThickness, _crosshairTickLength));
            _tickLeft = CreateCrosshairSegment("TickLeft", crossRect, new Vector2(-(_crosshairBaseGap + _crosshairTickLength * 0.5f), 0f), new Vector2(_crosshairTickLength, _crosshairTickThickness));
            _tickRight = CreateCrosshairSegment("TickRight", crossRect, new Vector2(_crosshairBaseGap + _crosshairTickLength * 0.5f, 0f), new Vector2(_crosshairTickLength, _crosshairTickThickness));
        }

        private void BuildLifeStateOverlay()
        {
            _lifeStateFrameRect = CreateTacticalBox(
                "BottomCenter_LifeStateOverlay",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 28f), new Vector2(360f, 88f),
                out _);
            _lifeStateRoot = _lifeStateFrameRect.gameObject;

            RectTransform inner = CreatePaddedInnerContainer(_lifeStateFrameRect, 18f, 18f, 12f, 12f);

            _lifeStateTitleText = CreateTopBand("LifeStateTitle", inner, 0f, 18f).gameObject.AddComponent<Text>();
            ConfigureTextStyle(_lifeStateTitleText, "DOWNED // BLEEDOUT", 12, FontStyle.Bold, TextAnchor.MiddleCenter, ColSunAmber);

            _lifeStatePromptText = CreateTopBand("LifeStatePrompt", inner, -20f, 14f).gameObject.AddComponent<Text>();
            ConfigureTextStyle(_lifeStatePromptText, "HOLD [E] NEAR COVER TO SELF-REVIVE OR CRAWL", 9, FontStyle.Normal, TextAnchor.MiddleCenter, ColTextLight);
            _lifeStatePromptText.resizeTextForBestFit = true;
            _lifeStatePromptText.resizeTextMinSize = 8;
            _lifeStatePromptText.resizeTextMaxSize = 10;

            RectTransform bleedBarRect = CreateTopBand("BleedoutCustomBar", inner, -39f, 7f);
            _bleedoutCustomBar = bleedBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _bleedoutCustomBar.Configure(_customBarTrackSprite, _customBarFillRazorSprite, _customBarFillRazorSprite, ColSurgeCrimson, ColHpGhostRed, ColPureWhite, true);

            RectTransform revRow = CreateTopBand("ReviveRow", inner, -50f, 14f);
            _reviveRoot = revRow.gameObject;
            RectTransform revBarRect = CreateBottomBand("ReviveCustomBar", revRow, 0f, 5f);
            _reviveCustomBar = revBarRect.gameObject.AddComponent<ScorchedCustomBar>();
            _reviveCustomBar.Configure(_customBarTrackSprite, _customBarFillRazorSprite, _customBarFillRazorSprite, ColShadeCyan, ColStaminaGhost, ColPureWhite, false, 0f);

            _reviveLabelText = CreateStretchedText("ReviveLbl", revRow, "REVIVING 0%", 8, FontStyle.Bold, TextAnchor.UpperCenter, ColPureWhite,
                new Vector2(0f, 0.4f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            _lifeStateRoot.SetActive(false);
        }
        #endregion

        #region Bounds & Clipping Verification (Phase 4)
        /// <summary>
        /// Forces canvas layout update and verifies that every label and custom bar
        /// stays cleanly inside its parent gdd_tactical_frame with explicit inner margin.
        /// </summary>
        public bool VerifyHudLayoutBounds(out string report, float minRequiredInnerMarginPx = 14f)
        {
            Canvas.ForceUpdateCanvases();
            var sb = new StringBuilder();
            bool allPassed = true;

            allPassed &= CheckFrameChildren(_vitalsFrameRect, 14f, minRequiredInnerMarginPx, sb);
            allPassed &= CheckFrameChildren(_ammoFrameRect, 16f, 16f, sb);
            allPassed &= CheckFrameChildren(_solarFrameRect, 4f, 16f, sb);

            if (_hotbarSlotVisuals != null)
            {
                for (int i = 0; i < _hotbarSlotVisuals.Length; i++)
                {
                    var slot = _hotbarSlotVisuals[i];
                    allPassed &= CheckFrameChildren(slot.FrameRect, 5f, 5f, sb);
                    if (slot.FrameRect != null && slot.IconImage != null)
                    {
                        allPassed &= CheckRectInsideFrame(slot.FrameRect, slot.IconImage.rectTransform, 5f, 5f, sb);
                    }
                }
            }

            report = sb.ToString();
            return allPassed;
        }

        private static bool CheckRectInsideFrame(RectTransform frame, RectTransform child, float minVerticalMargin, float minHorizontalMargin, StringBuilder sb)
        {
            Vector3[] fc = new Vector3[4];
            Vector3[] cc = new Vector3[4];
            frame.GetWorldCorners(fc);
            child.GetWorldCorners(cc);
            float scale = frame.lossyScale.x > 0.001f ? frame.lossyScale.x : 1f;

            float leftMargin = (cc[0].x - fc[0].x) / scale;
            float rightMargin = (fc[2].x - cc[2].x) / scale;
            float topMargin = (fc[2].y - cc[2].y) / scale;
            float bottomMargin = (cc[0].y - fc[0].y) / scale;

            bool ok = leftMargin >= (minHorizontalMargin - 0.5f) &&
                      rightMargin >= (minHorizontalMargin - 0.5f) &&
                      topMargin >= (minVerticalMargin - 0.5f) &&
                      bottomMargin >= (minVerticalMargin - 0.5f);
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {frame.name}/{child.name}: L={leftMargin:F1} R={rightMargin:F1} T={topMargin:F1} B={bottomMargin:F1}");
            return ok;
        }

        private static bool CheckFrameChildren(RectTransform frame, float minVerticalMargin, float minHorizontalMargin, StringBuilder sb)
        {
            if (frame == null)
            {
                sb.AppendLine("[FAIL] Missing frame reference.");
                return false;
            }

            Vector3[] frameCorners = new Vector3[4];
            frame.GetWorldCorners(frameCorners);
            float fLeft = frameCorners[0].x;
            float fBottom = frameCorners[0].y;
            float fRight = frameCorners[2].x;
            float fTop = frameCorners[2].y;
            float scale = frame.lossyScale.x > 0.001f ? frame.lossyScale.x : 1f;

            bool ok = true;
            var texts = frame.GetComponentsInChildren<Text>(true);
            foreach (var t in texts)
            {
                if (t == null) continue;
                Vector3[] tc = new Vector3[4];
                t.rectTransform.GetWorldCorners(tc);

                float leftMargin = (tc[0].x - fLeft) / scale;
                float rightMargin = (fRight - tc[2].x) / scale;
                float topMargin = (fTop - tc[2].y) / scale;
                float bottomMargin = (tc[0].y - fBottom) / scale;

                // Measure actual glyph extent when Text uses alignment inside a box
                float prefW = LayoutUtility.GetPreferredWidth(t.rectTransform);
                float rectW = t.rectTransform.rect.width;
                bool textFitsWidth = rectW >= prefW - 1.5f;

                bool marginsOk = leftMargin >= (minHorizontalMargin - 0.5f) &&
                                 rightMargin >= (minHorizontalMargin - 0.5f) &&
                                 topMargin >= (minVerticalMargin - 0.5f) &&
                                 bottomMargin >= (minVerticalMargin - 0.5f);

                if (!marginsOk || !textFitsWidth)
                {
                    ok = false;
                }

                sb.AppendLine($"[{(marginsOk && textFitsWidth ? "PASS" : "FAIL")}] {frame.name}/{t.name} ('{t.text}'): L={leftMargin:F1} R={rightMargin:F1} T={topMargin:F1} B={bottomMargin:F1} prefW={prefW:F1}/rectW={rectW:F1}");
            }

            var bars = frame.GetComponentsInChildren<ScorchedCustomBar>(true);
            foreach (var b in bars)
            {
                if (b == null) continue;
                Vector3[] bc = new Vector3[4];
                ((RectTransform)b.transform).GetWorldCorners(bc);
                float leftMargin = (bc[0].x - fLeft) / scale;
                float rightMargin = (fRight - bc[2].x) / scale;
                float topMargin = (fTop - bc[2].y) / scale;
                float bottomMargin = (bc[0].y - fBottom) / scale;
                bool barOk = leftMargin >= (minHorizontalMargin - 0.5f) &&
                             rightMargin >= (minHorizontalMargin - 0.5f) &&
                             topMargin >= (minVerticalMargin - 0.5f) &&
                             bottomMargin >= (minVerticalMargin - 0.5f);
                if (!barOk) ok = false;
                sb.AppendLine($"[{(barOk ? "PASS" : "FAIL")}] {frame.name}/{b.name}: L={leftMargin:F1} R={rightMargin:F1} T={topMargin:F1} B={bottomMargin:F1}");
            }

            return ok;
        }
        #endregion

        #region UI Factory Helpers
        private RectTransform CreateTacticalBox(
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 pos,
            Vector2 size,
            out Image bgImage)
        {
            RectTransform rect = CreateRect(name, transform, anchorMin, anchorMax, pivot, pos, size);
            bgImage = rect.gameObject.AddComponent<Image>();
            bgImage.sprite = _gddTacticalFrameSprite;
            bgImage.type = _gddTacticalFrameSprite != null && _gddTacticalFrameSprite.border != Vector4.zero
                ? Image.Type.Sliced
                : Image.Type.Simple;
            bgImage.fillCenter = true;
            bgImage.color = ColFrameTint;
            bgImage.raycastTarget = false;

            // Strict RectMask2D guarantees nothing can ever overflow or bleed outside frame bounds
            rect.gameObject.AddComponent<RectMask2D>();
            return rect;
        }

        private static RectTransform CreatePaddedInnerContainer(RectTransform parent, float padLeft, float padRight, float padTop, float padBottom)
        {
            var go = new GameObject("InnerContent", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padLeft, padBottom);
            rect.offsetMax = new Vector2(-padRight, -padTop);
            return rect;
        }

        private static RectTransform CreateTopBand(string name, Transform parent, float topOffsetY, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, topOffsetY - height);
            rect.offsetMax = new Vector2(0f, topOffsetY);
            return rect;
        }

        private static RectTransform CreateBottomBand(string name, Transform parent, float bottomOffsetY, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(0f, bottomOffsetY);
            rect.offsetMax = new Vector2(0f, bottomOffsetY + height);
            return rect;
        }

        private static RectTransform CreateSubColumn(string name, Transform parent, float anchorMinX, float anchorMaxX)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(anchorMinX, 0f);
            rect.anchorMax = new Vector2(anchorMaxX, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private Image CreateFullScreenImage(string name, Sprite sprite, Color color)
        {
            RectTransform rect = CreateRect(name, transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Image img = rect.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private RectTransform CreateCrosshairSegment(string name, Transform parent, Vector2 pos, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            Image img = rect.gameObject.AddComponent<Image>();
            img.color = new Color(0.97f, 0.98f, 0.99f, 0.90f);
            img.raycastTarget = false;

            var shadow = rect.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.72f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return rect;
        }

        private Text CreateLayoutText(string name, Transform parent, string content, int fontSize, FontStyle style, TextAnchor alignment, Color color, float width, float height)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(width, height));
            Text txt = rect.gameObject.AddComponent<Text>();
            ConfigureTextStyle(txt, content, fontSize, style, alignment, color);
            return txt;
        }

        private Text CreateText(
            string name,
            Transform parent,
            string content,
            int fontSize,
            FontStyle style,
            TextAnchor alignment,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPos,
            Vector2 sizeDelta)
        {
            RectTransform rect = CreateRect(name, parent, anchorMin, anchorMax, new Vector2(0.5f, 0.5f), anchoredPos, sizeDelta);
            Text txt = rect.gameObject.AddComponent<Text>();
            ConfigureTextStyle(txt, content, fontSize, style, alignment, color);
            return txt;
        }

        private Text CreateStretchedText(
            string name,
            Transform parent,
            string content,
            int fontSize,
            FontStyle style,
            TextAnchor alignment,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            RectTransform rect = CreateRect(name, parent, anchorMin, anchorMax, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            Text txt = rect.gameObject.AddComponent<Text>();
            ConfigureTextStyle(txt, content, fontSize, style, alignment, color);
            return txt;
        }

        private void ConfigureTextStyle(Text txt, string content, int fontSize, FontStyle style, TextAnchor alignment, Color color)
        {
            txt.font = _uiFont;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.alignment = alignment;
            txt.color = color;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            txt.raycastTarget = false;

            var shadow = txt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.78f);
            shadow.effectDistance = new Vector2(1f, -1f);
        }

        private static RectTransform CreateRect(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = sizeDelta;
            return rect;
        }
        #endregion
    }
}
