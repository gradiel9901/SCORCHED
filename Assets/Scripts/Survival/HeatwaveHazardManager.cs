using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scorched.Player;

namespace Scorched.Survival
{
    public enum MatchPhase : byte
    {
        GraceWindow = 0,
        HeatwaveCountdown = 1,
        HeatwaveActive = 2
    }

    /// <summary>
    /// Escalating Heatwave & Shade Safe-Zone Durability State Machine for SCORCHED.
    /// Flow:
    /// 1) GraceWindow (60s): zero outside hydration drain / exposure gain, all shades at 100%.
    /// 2) Alternating HeatwaveCountdown & HeatwaveActive along _cooldownSchedule {180, 120, 60, 30, 15, 0}.
    /// 3) Wave 6+ reaches 0s cooldown (Permanent Endless Heatwave!).
    /// During HeatwaveActive, selected ShadeZoneBuildings are targeted and decay at 3.5 + (Occupants * 4.0)/s.
    /// </summary>
    [DisallowMultipleComponent]
    public class HeatwaveHazardManager : NetworkBehaviour
    {
        public static HeatwaveHazardManager Instance { get; private set; }

        [Header("Escalating Match Flow Timing (Seconds)")]
        [SerializeField] private float _graceWindowDuration = 60.0f;
        [SerializeField] private float _heatwaveActiveDuration = 30.0f;
        [SerializeField] private int _baseTargetsPerWave = 2;

        // Wave 1..5 cooldowns, Wave 6+ = 0s (Permanent Endless Heatwave)
        private readonly float[] _cooldownSchedule = new float[] { 180f, 120f, 60f, 30f, 15f, 0f };

        [Header("Solar Intensity Multipliers")]
        [SerializeField] private float _graceSolarMultiplier = 0.0f;
        [SerializeField] private float _standardSolarMultiplier = 1.0f;
        [SerializeField] private float _heatwaveSolarMultiplier = 2.2f;

        [Header("Scene Lighting & Atmosphere")]
        [SerializeField] private Light _sunLight;
        [SerializeField] private float _normalSunIntensity = 2.4f;
        [SerializeField] private float _heatwaveSunIntensity = 3.4f;
        [SerializeField] private Color _normalSunColor = new Color(1.0f, 0.9098039f, 0.7215686f, 1f); // #FFE8B8
        [SerializeField] private Color _heatwaveSunColor = new Color(1.0f, 0.8235294f, 0.5411765f, 1f); // #FFD28A
        [SerializeField] private float _visualLerpSpeed = 1.8f;

        [Header("Atmospheric Fog Response")]
        [SerializeField] private bool _modulateAtmosphericFog = true;
        [SerializeField] private Color _normalFogColor = new Color(0.8470588f, 0.6627451f, 0.454902f, 1f); // #D8A974
        [SerializeField] private Color _heatwaveFogColor = new Color(0.8784314f, 0.572549f, 0.3137255f, 1f); // #E09250
        [SerializeField] private float _normalFogDensity = 0.0018f;
        [SerializeField] private float _heatwaveFogDensity = 0.0035f;

        #region Netcode Synchronized State
        public readonly NetworkVariable<byte> NetworkMatchPhase = new NetworkVariable<byte>(
            (byte)MatchPhase.GraceWindow,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> NetworkWaveNumber = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> NetworkPhaseTimeRemaining = new NetworkVariable<float>(
            60f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<uint> NetworkTargetedShadeMask = new NetworkVariable<uint>(
            0u,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> NetworkIsPermanentHeatwave = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkList<float> NetworkShadeDurabilities = new NetworkList<float>();
        #endregion

        #region Runtime State
        private MatchPhase _localPhase = MatchPhase.GraceWindow;
        private int _localWaveNumber = 0; // 0 in GraceWindow, 1..N during waves
        private float _localTimeRemaining = 60f;
        private uint _localTargetedMask = 0u;
        private bool _localPermanentHeatwave = false;

        private float _currentVisualBlend = 0f;
        private float _syncTimer = 0f;
        private bool _hasInitializedOfflineOrServer = false;
        private readonly List<ShadeZoneBuilding> _candidateShades = new List<ShadeZoneBuilding>(16);
        #endregion

        #region Public Telemetry Properties
        public MatchPhase CurrentMatchPhase => IsSpawned ? (MatchPhase)NetworkMatchPhase.Value : _localPhase;
        public int CurrentWaveNumber => IsSpawned ? NetworkWaveNumber.Value : _localWaveNumber;
        public float TimeUntilNextPhase => IsSpawned ? NetworkPhaseTimeRemaining.Value : _localTimeRemaining;
        public bool IsPermanentHeatwave => IsSpawned ? NetworkIsPermanentHeatwave.Value : _localPermanentHeatwave;
        public uint TargetedShadeMask => IsSpawned ? NetworkTargetedShadeMask.Value : _localTargetedMask;

        public bool IsGraceWindowActive => CurrentMatchPhase == MatchPhase.GraceWindow;
        public bool IsHeatwaveCountdownActive => CurrentMatchPhase == MatchPhase.HeatwaveCountdown;
        public bool IsHeatwaveSurgeActive => CurrentMatchPhase == MatchPhase.HeatwaveActive;

        public float CurrentSolarMultiplier
        {
            get
            {
                return CurrentMatchPhase switch
                {
                    MatchPhase.GraceWindow => _graceSolarMultiplier,
                    MatchPhase.HeatwaveActive => _heatwaveSolarMultiplier,
                    _ => _standardSolarMultiplier
                };
            }
        }

        public Vector3 DirectionToSun
        {
            get
            {
                EnsureSunLight();
                return _sunLight != null ? (-_sunLight.transform.forward).normalized : Vector3.up;
            }
        }
        #endregion

        #region Bootstrap & Lifecycle
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureExistsAfterSceneLoad()
        {
            if (Instance == null && FindAnyObjectByType<HeatwaveHazardManager>() == null)
            {
                var go = new GameObject("HeatwaveHazardManager");
                go.AddComponent<HeatwaveHazardManager>();
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
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
            EnsureSunLight();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _sunLight = null;
            EnsureSunLight();
            ShadeZoneBuilding.RefreshRegistryOrder();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ShadeZoneBuilding.RefreshRegistryOrder();

            if (IsServer)
            {
                ResetMatchStateAuthoritative();
            }
            else
            {
                PullSyncedStateToShades();
            }
        }

        private void Start()
        {
            ShadeZoneBuilding.RefreshRegistryOrder();
            if (!IsSpawned && !_hasInitializedOfflineOrServer)
            {
                ResetMatchStateAuthoritative();
            }
        }
        #endregion

        #region State Machine Update
        private void Update()
        {
            bool authoritative = !IsSpawned || IsServer;

            if (authoritative)
            {
                if (!_hasInitializedOfflineOrServer)
                {
                    ResetMatchStateAuthoritative();
                }

                bool waitingInMainMenu = NetworkManager.Singleton != null
                                         && !NetworkManager.Singleton.IsListening
                                         && Scorched.UI.MainMenuUIManager.IsMenuOpen;
                if (!waitingInMainMenu)
                {
                    TickAuthoritativeStateMachine(Time.deltaTime);
                    TickAuthoritativeShadeBuildings(Time.deltaTime);
                }

                _syncTimer += Time.deltaTime;
                if (_syncTimer >= 0.1f)
                {
                    _syncTimer = 0f;
                    PushStateToNetwork();
                }
            }
            else
            {
                PullSyncedStateToShades();
            }

            UpdateAtmosphereAndLighting(Time.deltaTime);
        }

        public void ResetMatchStateAuthoritative()
        {
            _hasInitializedOfflineOrServer = true;
            _localPhase = MatchPhase.GraceWindow;
            _localWaveNumber = 0;
            _localTimeRemaining = _graceWindowDuration;
            _localTargetedMask = 0u;
            _localPermanentHeatwave = false;

            ShadeZoneBuilding.RefreshRegistryOrder();
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null)
                {
                    shades[i].ResetToFullServer();
                }
            }

            if (IsSpawned && IsServer)
            {
                NetworkShadeDurabilities.Clear();
                for (int i = 0; i < shades.Count; i++)
                {
                    NetworkShadeDurabilities.Add(shades[i] != null ? shades[i].CurrentDurability : 100f);
                }
                PushStateToNetwork();
            }
        }

        /// <summary>
        /// Deterministic helper (useful for automated Play Mode verification & QA) to jump cleanly to any phase.
        /// </summary>
        public void SetPhaseAuthoritative(MatchPhase phase, int waveNumber, float durationSeconds)
        {
            if (IsSpawned && !IsServer) return;

            _hasInitializedOfflineOrServer = true;
            _localPhase = phase;
            _localWaveNumber = Mathf.Max(0, waveNumber);
            _localTimeRemaining = Mathf.Max(0f, durationSeconds);
            _localPermanentHeatwave = phase == MatchPhase.HeatwaveActive && waveNumber >= _cooldownSchedule.Length;

            if (phase == MatchPhase.HeatwaveActive)
            {
                SelectTargetsForWave();
            }
            else
            {
                ClearTargets();
            }

            PushStateToNetwork();
        }

        private void TickAuthoritativeStateMachine(float dt)
        {
            if (dt <= 0f) return;

            if (_localPhase == MatchPhase.HeatwaveActive)
            {
                // If targeted shade collapsed to 0, immediately select another random intact shade to continue decay
                if (!HasAnyAliveTargetedShade() && HasAnyIntactShade())
                {
                    SelectTargetsForWave();
                }

                if (_localPermanentHeatwave)
                {
                    _localTimeRemaining = 0f;
                    return;
                }
            }

            _localTimeRemaining -= dt;
            if (_localTimeRemaining > 0f)
            {
                return;
            }

            // Transition to next phase
            if (_localPhase == MatchPhase.GraceWindow)
            {
                EnterWaveCountdown(1);
            }
            else if (_localPhase == MatchPhase.HeatwaveCountdown)
            {
                EnterHeatwaveActive(_localWaveNumber);
            }
            else if (_localPhase == MatchPhase.HeatwaveActive)
            {
                int nextWave = _localWaveNumber + 1;
                EnterWaveCountdown(nextWave);
            }
        }

        private void EnterWaveCountdown(int waveNumber)
        {
            _localWaveNumber = Mathf.Max(1, waveNumber);
            ClearTargets();

            int scheduleIdx = Mathf.Clamp(_localWaveNumber - 1, 0, _cooldownSchedule.Length - 1);
            float cooldownDuration = _cooldownSchedule[scheduleIdx];

            if (cooldownDuration <= 0.01f)
            {
                // Wave 6+: 0s cooldown -> Permanent Endless Heatwave!
                _localPermanentHeatwave = true;
                EnterHeatwaveActive(_localWaveNumber);
                return;
            }

            _localPhase = MatchPhase.HeatwaveCountdown;
            _localTimeRemaining = cooldownDuration;
            _localPermanentHeatwave = false;
            Debug.Log($"[HeatwaveHazardManager] Wave {_localWaveNumber} Countdown started ({cooldownDuration:F0}s).");
        }

        private void EnterHeatwaveActive(int waveNumber)
        {
            _localWaveNumber = Mathf.Max(1, waveNumber);
            _localPhase = MatchPhase.HeatwaveActive;

            int scheduleIdx = Mathf.Clamp(_localWaveNumber - 1, 0, _cooldownSchedule.Length - 1);
            _localPermanentHeatwave = _cooldownSchedule[scheduleIdx] <= 0.01f;
            _localTimeRemaining = _localPermanentHeatwave ? 0f : _heatwaveActiveDuration;

            SelectTargetsForWave();
            Debug.Log($"[HeatwaveHazardManager] Wave {_localWaveNumber} HEATWAVE ACTIVE (Permanent={_localPermanentHeatwave}, TargetMask=0x{_localTargetedMask:X}).");
        }

        private void ClearTargets()
        {
            _localTargetedMask = 0u;
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null)
                {
                    shades[i].ApplySyncState(shades[i].CurrentDurability, false);
                }
            }
        }

        private void SelectTargetsForWave()
        {
            _candidateShades.Clear();
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null && !shades[i].IsDestroyed)
                {
                    _candidateShades.Add(shades[i]);
                }
            }

            _localTargetedMask = 0u;
            if (_candidateShades.Count == 0)
            {
                return;
            }

            // Truly random selection across all intact shade buildings (no occupancy bias upfront)
            for (int i = _candidateShades.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_candidateShades[i], _candidateShades[j]) = (_candidateShades[j], _candidateShades[i]);
            }

            // Exactly 1 random shade targeted at a time (or 2 if endless permanent heatwave with many buildings left)
            int targetCount = (_localPermanentHeatwave && _candidateShades.Count >= 4) ? 2 : 1;
            targetCount = Mathf.Min(targetCount, _candidateShades.Count);

            for (int i = 0; i < _candidateShades.Count; i++)
            {
                ShadeZoneBuilding shade = _candidateShades[i];
                bool targeted = i < targetCount;
                if (targeted && shade.ShadeIndex >= 0 && shade.ShadeIndex < 32)
                {
                    _localTargetedMask |= (1u << shade.ShadeIndex);
                }
                shade.ApplySyncState(shade.CurrentDurability, targeted);
            }
        }

        private bool HasAnyAliveTargetedShade()
        {
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null && shades[i].IsTargetedByHeatwave && !shades[i].IsDestroyed)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasAnyIntactShade()
        {
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                if (shades[i] != null && !shades[i].IsDestroyed)
                {
                    return true;
                }
            }
            return false;
        }

        private void TickAuthoritativeShadeBuildings(float dt)
        {
            if (_localPhase != MatchPhase.HeatwaveActive)
            {
                return;
            }

            RefreshOccupantsAuthoritative();

            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                ShadeZoneBuilding shade = shades[i];
                if (shade != null && shade.IsTargetedByHeatwave && !shade.IsDestroyed)
                {
                    shade.TickDurabilityDecayServer(dt);
                }
            }
        }

        private static void RefreshOccupantsAuthoritative()
        {
            var shades = ShadeZoneBuilding.AllShades;
            for (int i = 0; i < shades.Count; i++)
            {
                shades[i]?.OccupantClientIds.Clear();
            }

            var players = PlayerHealthController.ActiveInstances;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerHealthController hc = players[i];
                if (hc == null || hc.IsDead || !hc.gameObject.activeInHierarchy) continue;

                var thermal = hc.GetComponent<PlayerThermalController>();
                if (thermal == null) continue;

                ShadeZoneBuilding zone = thermal.RefreshShadeContainment();
                if (zone != null && !zone.IsDestroyed)
                {
                    zone.RegisterOccupantId(thermal.GetEffectiveClientId());
                }
            }
        }

        private void PushStateToNetwork()
        {
            if (!IsSpawned || !IsServer) return;

            NetworkMatchPhase.Value = (byte)_localPhase;
            NetworkWaveNumber.Value = _localWaveNumber;
            NetworkPhaseTimeRemaining.Value = Mathf.Max(0f, _localTimeRemaining);
            NetworkTargetedShadeMask.Value = _localTargetedMask;
            NetworkIsPermanentHeatwave.Value = _localPermanentHeatwave;

            var shades = ShadeZoneBuilding.AllShades;
            while (NetworkShadeDurabilities.Count < shades.Count)
            {
                NetworkShadeDurabilities.Add(100f);
            }
            for (int i = 0; i < shades.Count; i++)
            {
                float d = shades[i] != null ? shades[i].CurrentDurability : 0f;
                if (Mathf.Abs(NetworkShadeDurabilities[i] - d) > 0.05f)
                {
                    NetworkShadeDurabilities[i] = d;
                }
            }
        }

        private void PullSyncedStateToShades()
        {
            if (!IsSpawned || IsServer) return;

            uint mask = NetworkTargetedShadeMask.Value;
            bool isHeatwave = NetworkMatchPhase.Value == (byte)MatchPhase.HeatwaveActive;
            var shades = ShadeZoneBuilding.AllShades;

            for (int i = 0; i < shades.Count; i++)
            {
                ShadeZoneBuilding shade = shades[i];
                if (shade == null) continue;

                float dur = i < NetworkShadeDurabilities.Count ? NetworkShadeDurabilities[i] : shade.MaxDurability;
                bool targeted = isHeatwave && shade.ShadeIndex >= 0 && shade.ShadeIndex < 32 && ((mask & (1u << shade.ShadeIndex)) != 0u);
                shade.ApplySyncState(dur, targeted);
            }
        }
        #endregion

        #region Lighting & Atmospheric Heat Haze
        private void UpdateAtmosphereAndLighting(float dt)
        {
            EnsureSunLight();
            float targetBlend = IsHeatwaveSurgeActive ? 1f : 0f;
            _currentVisualBlend = Mathf.MoveTowards(_currentVisualBlend, targetBlend, dt * _visualLerpSpeed);

            if (_sunLight != null)
            {
                _sunLight.intensity = Mathf.Lerp(_normalSunIntensity, _heatwaveSunIntensity, _currentVisualBlend);
                _sunLight.color = Color.Lerp(_normalSunColor, _heatwaveSunColor, _currentVisualBlend);
            }

            if (_modulateAtmosphericFog)
            {
                if (!RenderSettings.fog) RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = Color.Lerp(_normalFogColor, _heatwaveFogColor, _currentVisualBlend);
                RenderSettings.fogDensity = Mathf.Lerp(_normalFogDensity, _heatwaveFogDensity, _currentVisualBlend);
            }
        }

        private void EnsureSunLight()
        {
            if (_sunLight != null && _sunLight.isActiveAndEnabled) return;

            if (RenderSettings.sun != null && RenderSettings.sun.isActiveAndEnabled)
            {
                _sunLight = RenderSettings.sun;
                return;
            }

            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional && lights[i].isActiveAndEnabled)
                {
                    _sunLight = lights[i];
                    return;
                }
            }
        }
        #endregion
    }
}
