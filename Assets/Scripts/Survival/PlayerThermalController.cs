using System;
using Unity.Netcode;
using UnityEngine;
using Scorched.Player;

namespace Scorched.Survival
{
    /// <summary>
    /// Per-player Shade Safe-Zone Detection, Hydration drain/halt, HeatExposure accumulation/cooling,
    /// stamina regen penalty, heatstroke vignette intensity, and sunburn damage through PlayerHealthController.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerHealthController))]
    public class PlayerThermalController : NetworkBehaviour
    {
        private const float ShadeCheckIntervalSeconds = 0.1f;
        private const float _standardSolarFallbackMultiplier = 1.0f;

        [Header("Survival Vitals (0 - 100)")]
        [SerializeField] private float _maxHydration = 100f;
        [SerializeField] private float _maxHeatExposure = 100f;

        [Header("Thermal Drain & Recovery Rates (/sec)")]
        [SerializeField] private float _heatGainBaseRate = 2.5f;
        [SerializeField] private float _heatCoolShadeRate = 24.0f;
        [SerializeField] private float _hydrationDrainBaseRate = 1.2f;
        [SerializeField] private float _hydrationSprintPenaltyRate = 0.8f;

        [Header("Overheat / Dehydration Penalties")]
        [SerializeField] private float _overheatHeatThreshold = 80f;
        [SerializeField] private float _dehydratedThreshold = 20f;
        [SerializeField] private float _staminaRegenPenalty = 0.40f;
        [SerializeField] private float _heatBurnDamagePerSecond = 3.5f;

        public readonly NetworkVariable<float> NetworkHydration = new NetworkVariable<float>(
            100f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> NetworkHeatExposure = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> NetworkIsInShade = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private PlayerHealthController _healthController;
        private PlayerMovement _playerMovement;
        private ShadeZoneBuilding _currentShadeZone;

        private float _localHydration = 100f;
        private float _localHeatExposure = 0f;
        private bool _localIsInShade = false;
        private float _shadeCheckTimer = 0f;
        private float _networkSyncTimer = 0f;
        private float _burnDamageAccumulator = 0f;
        private bool _wasDead = false;

        public float Hydration => IsSpawned ? (IsServer ? _localHydration : NetworkHydration.Value) : _localHydration;
        public float MaxHydration => _maxHydration;
        public float HeatExposure => IsSpawned ? (IsServer ? _localHeatExposure : NetworkHeatExposure.Value) : _localHeatExposure;
        public float MaxHeatExposure => _maxHeatExposure;

        public ShadeZoneBuilding CurrentShadeZone => (_currentShadeZone != null && !_currentShadeZone.IsDestroyed) ? _currentShadeZone : null;
        public bool IsAliveForShade => _healthController == null || !_healthController.IsDead;

        // Owner evaluates ShadeZoneBuilding containment every 0.1s + trigger callbacks; Server validates authoritatively.
        public bool IsInShade => (IsSpawned && !IsServer && !IsOwner)
            ? NetworkIsInShade.Value
            : (_localIsInShade && (_currentShadeZone == null || !_currentShadeZone.IsDestroyed));

        public bool IsOverheatingOrDehydrated => HeatExposure >= _overheatHeatThreshold || Hydration <= _dehydratedThreshold;

        public float StaminaRegenMultiplier => IsOverheatingOrDehydrated ? Mathf.Clamp01(1f - _staminaRegenPenalty) : 1f;

        public float HeatstrokeSeverity01
        {
            get
            {
                if (!IsOverheatingOrDehydrated) return 0f;
                float heatFactor = Mathf.Clamp01(Mathf.InverseLerp(_overheatHeatThreshold, _maxHeatExposure, HeatExposure));
                float waterFactor = Mathf.Clamp01(Mathf.InverseLerp(_dehydratedThreshold, 0f, Hydration));
                return Mathf.Clamp01(Mathf.Max(0.25f, Mathf.Max(heatFactor, waterFactor)));
            }
        }

        private void Awake()
        {
            _healthController = GetComponent<PlayerHealthController>();
            _playerMovement = GetComponent<PlayerMovement>();
            _localHydration = _maxHydration;
            _localHeatExposure = 0f;
            _localIsInShade = false;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                _localHydration = _maxHydration;
                _localHeatExposure = 0f;
                NetworkHydration.Value = _localHydration;
                NetworkHeatExposure.Value = _localHeatExposure;
                CheckShadeStatus();
                NetworkIsInShade.Value = _localIsInShade;
            }
            else
            {
                _localHydration = NetworkHydration.Value;
                _localHeatExposure = NetworkHeatExposure.Value;
                _localIsInShade = NetworkIsInShade.Value;
            }
        }

        private void Start()
        {
            CheckShadeStatus();
        }

        private void Update()
        {
            bool isDead = _healthController != null && _healthController.IsDead;

            // Detect respawn transition to cleanly reset survival vitals
            if (_wasDead && !isDead)
            {
                ResetThermalVitals();
            }
            _wasDead = isDead;

            if (isDead)
            {
                if (_currentShadeZone != null)
                {
                    _currentShadeZone.UnregisterOccupantId(GetEffectiveClientId());
                    _currentShadeZone = null;
                    _localIsInShade = false;
                }
                return;
            }

            // 1. Safe-zone bounds evaluation every 0.1s (runs on Server & Local Owner)
            if (!IsSpawned || IsServer || IsOwner)
            {
                _shadeCheckTimer += Time.deltaTime;
                if (_shadeCheckTimer >= ShadeCheckIntervalSeconds)
                {
                    _shadeCheckTimer = 0f;
                    CheckShadeStatus();
                }
            }

            // 2. Authoritative simulation (Server in multiplayer, or local when offline/unspawned)
            if (!IsSpawned || IsServer)
            {
                SimulateThermalTick(Time.deltaTime);
            }
        }

        /// <summary>
        /// Dedicated Building Safe-Zone Detection:
        /// Evaluates registered ShadeZoneBuilding trigger volumes under _ShadeCanopies_And_Cover.
        /// Destroyed shades (CurrentDurability &lt;= 0) no longer protect the player.
        /// </summary>
        public bool CheckShadeStatus()
        {
            RefreshShadeContainment();
            return _localIsInShade;
        }

        public ShadeZoneBuilding RefreshShadeContainment()
        {
            ShadeZoneBuilding matched = null;

            if (IsAliveForShade)
            {
                Vector3 probePos = transform.position + Vector3.up * 0.9f;
                var allShades = ShadeZoneBuilding.AllShades;
                for (int i = 0; i < allShades.Count; i++)
                {
                    ShadeZoneBuilding shade = allShades[i];
                    if (shade == null || shade.IsDestroyed)
                    {
                        continue;
                    }

                    if (shade.ContainsWorldPoint(probePos) && shade.VerifyOverheadCover(probePos))
                    {
                        matched = shade;
                        break;
                    }
                }
            }

            ulong clientId = GetEffectiveClientId();
            if (_currentShadeZone != null && _currentShadeZone != matched)
            {
                _currentShadeZone.UnregisterOccupantId(clientId);
            }

            _currentShadeZone = matched;
            if (matched != null)
            {
                matched.RegisterOccupantId(clientId);
            }

            _localIsInShade = matched != null && !matched.IsDestroyed;

            if (IsSpawned && IsServer && NetworkIsInShade.Value != _localIsInShade)
            {
                NetworkIsInShade.Value = _localIsInShade;
            }

            return _currentShadeZone;
        }

        internal void NotifyEnteredShadeZone(ShadeZoneBuilding zone)
        {
            if (zone == null || zone.IsDestroyed || !IsAliveForShade) return;

            Vector3 probePos = transform.position + Vector3.up * 0.9f;
            if (!zone.VerifyOverheadCover(probePos))
            {
                // Trigger entered on outdoor perimeter without solid overhead ceiling/roof - ignore
                return;
            }

            ulong clientId = GetEffectiveClientId();
            if (_currentShadeZone != null && _currentShadeZone != zone)
            {
                _currentShadeZone.UnregisterOccupantId(clientId);
            }

            _currentShadeZone = zone;
            zone.RegisterOccupantId(clientId);
            _localIsInShade = true;
            if (IsSpawned && IsServer && !NetworkIsInShade.Value)
            {
                NetworkIsInShade.Value = true;
            }
        }

        internal void NotifyExitedShadeZone(ShadeZoneBuilding zone)
        {
            if (_currentShadeZone == zone)
            {
                RefreshShadeContainment();
            }
        }

        public ulong GetEffectiveClientId()
        {
            return (NetworkObject != null && NetworkObject.IsSpawned) ? NetworkObject.OwnerClientId : 0UL;
        }

        public void RestoreHydrationAuthoritative(float amount)
        {
            if (IsSpawned && !IsServer) return;
            if (amount <= 0f) return;

            _localHydration = Mathf.Min(_maxHydration, _localHydration + amount);
            _localHeatExposure = Mathf.Max(0f, _localHeatExposure - amount * 0.5f);
            if (IsSpawned && IsServer)
            {
                NetworkHydration.Value = _localHydration;
                NetworkHeatExposure.Value = _localHeatExposure;
            }
        }

        private void SimulateThermalTick(float dt)
        {
            if (dt <= 0f) return;

            HeatwaveHazardManager hazard = HeatwaveHazardManager.Instance;
            bool inGraceWindow = hazard != null && hazard.IsGraceWindowActive;
            float solarMultiplier = hazard != null ? hazard.CurrentSolarMultiplier : _standardSolarFallbackMultiplier;
            bool isSprinting = _playerMovement != null && _playerMovement.IsSprinting;

            // Destroyed buildings immediately lose shade protection
            if (_currentShadeZone != null && _currentShadeZone.IsDestroyed)
            {
                _currentShadeZone = null;
                _localIsInShade = false;
            }

            if (_localIsInShade)
            {
                // Intact Shade Safe-Zone: rapid cooling, zero dehydration drain
                _localHeatExposure = Mathf.Max(0f, _localHeatExposure - _heatCoolShadeRate * dt);
                _burnDamageAccumulator = 0f;
            }
            else if (inGraceWindow)
            {
                // Initial 60s Grace Window: ZERO consequences outdoors, completely cool
                _localHeatExposure = 0f;
                _burnDamageAccumulator = 0f;
            }
            else
            {
                // Exposed to Sun (outside or inside destroyed building): heat rises and water drains
                _localHeatExposure = Mathf.Min(_maxHeatExposure, _localHeatExposure + (_heatGainBaseRate * solarMultiplier) * dt);

                float waterDrain = (_hydrationDrainBaseRate * solarMultiplier) + (isSprinting ? _hydrationSprintPenaltyRate : 0f);
                _localHydration = Mathf.Max(0f, _localHydration - waterDrain * dt);

                // Severe heatstroke / empty hydration applies periodic heat-burn damage while still exposed
                if (_localHydration <= 0f || _localHeatExposure >= _maxHeatExposure)
                {
                    _burnDamageAccumulator += _heatBurnDamagePerSecond * dt;
                    if (_burnDamageAccumulator >= 0.5f)
                    {
                        float dmg = _burnDamageAccumulator;
                        _burnDamageAccumulator = 0f;
                        _healthController?.ApplyEnvironmentalDamage(dmg);
                    }
                }
                else
                {
                    _burnDamageAccumulator = 0f;
                }
            }

            if (IsSpawned && IsServer)
            {
                _networkSyncTimer += dt;
                if (_networkSyncTimer >= 0.1f)
                {
                    _networkSyncTimer = 0f;
                    if (Mathf.Abs(NetworkHydration.Value - _localHydration) > 0.02f)
                    {
                        NetworkHydration.Value = _localHydration;
                    }
                    if (Mathf.Abs(NetworkHeatExposure.Value - _localHeatExposure) > 0.02f)
                    {
                        NetworkHeatExposure.Value = _localHeatExposure;
                    }
                }
            }
        }

        public void ResetThermalVitals()
        {
            _localHydration = _maxHydration;
            _localHeatExposure = 0f;
            _burnDamageAccumulator = 0f;
            RefreshShadeContainment();

            if (IsSpawned && IsServer)
            {
                NetworkHydration.Value = _localHydration;
                NetworkHeatExposure.Value = _localHeatExposure;
                NetworkIsInShade.Value = _localIsInShade;
            }
        }
    }
}

