using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Combat;

namespace Scorched.Weapons
{
    /// <summary>
    /// High-performance, zero-allocation multiplayer weapon controller for Unity 6 NGO.
    /// Implements decoupled visual prediction, client-side zero-latency feel,
    /// server-authoritative hit registration, and anti-cheat validation.
    /// Strictly networks events via RPCs without spawning networked bullet GameObjects.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkWeaponController : NetworkBehaviour
    {
        private const float MaxOriginToleranceMeters = 5.0f;
        private const float RateToleranceMultiplier = 0.85f;

        #region Serialized Configuration
        [Header("Weapon Data Asset")]
        [Tooltip("ScriptableObject defining ballistic parameters, RPM, damage, and hydration drain.")]
        [SerializeField] private WeaponDataSO _currentWeapon;

        [Header("Transform Anchors")]
        [Tooltip("The tip of the weapon barrel where visual tracers originate.")]
        [SerializeField] private Transform _muzzlePoint;

        [Tooltip("The player's camera/aim transform for center-screen raycast projection.")]
        [SerializeField] private Transform _aimCameraTransform;

        [Header("Collision Filtering")]
        [Tooltip("Layer mask for ballistic and hitscan obstacle detection.")]
        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("Ammo & Reloading")]
        [Tooltip("Maximum rounds per magazine.")]
        [SerializeField] private int _magCapacity = 30;

        [Tooltip("Current rounds loaded in the active magazine.")]
        [SerializeField] private int _currentAmmoInMag = 0;

        [Tooltip("Reserve ammunition available for reloading.")]
        [SerializeField] private int _reserveAmmo = 0;

        [Tooltip("Time required to reload magazine in seconds.")]
        [SerializeField] private float _reloadDuration = 2.2f;
        #endregion

        #region Network Weapon State
        public readonly NetworkVariable<bool> HasWeaponEquipped = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> ReserveAmmo = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        #endregion

        #region Runtime State
        private bool _localHasWeaponEquipped = false;
        private float _nextLocalFireTime = 0f;
        private double _lastServerValidatedFireTime = 0.0;
        private Collider _ownerCollider;
        private Scorched.Player.PlayerWeaponHandler _weaponHandler;
        private Scorched.Player.FPSPerspectiveController _perspectiveController;
        private Scorched.Player.PlayerHealthController _healthController;
        private Scorched.Inventory.PlayerHotbarController _hotbar;
        private bool _isReloading = false;
        private float _reloadEndTime = 0f;
        private int _shotSequence = 0;
        private int _serverShotSequence = 0;
        #endregion

        #region Public Accessors
        public bool IsWeaponEquipped => IsSpawned ? HasWeaponEquipped.Value : _localHasWeaponEquipped;
        public bool IsWeaponDrawn => IsWeaponEquipped && (_hotbar == null || _hotbar.IsWeaponSlotSelected);
        public int CurrentAmmoInMag => _currentAmmoInMag;
        public int CurrentReserveAmmo => IsSpawned ? ReserveAmmo.Value : _reserveAmmo;
        public bool IsReloading => _isReloading;
        public string CurrentWeaponName => IsWeaponEquipped
            ? (_currentWeapon != null ? _currentWeapon.WeaponName : "AK-47 TACTICAL")
            : "UNARMED";
        public float ReloadProgress01 => _isReloading && _reloadDuration > 0.01f
            ? Mathf.Clamp01(1f - ((_reloadEndTime - Time.time) / _reloadDuration))
            : 0f;

        public WeaponDataSO CurrentWeapon
        {
            get => _currentWeapon;
            set => _currentWeapon = value;
        }

        public Transform MuzzlePoint
        {
            get
            {
                // Prioritize active perspective muzzle (viewmodel or aligned world weapon)
                if (_perspectiveController == null) _perspectiveController = GetComponent<Scorched.Player.FPSPerspectiveController>();
                if (_perspectiveController != null && _perspectiveController.ActiveMuzzlePoint != null)
                {
                    return _perspectiveController.ActiveMuzzlePoint;
                }
                if (_muzzlePoint != null) return _muzzlePoint;
                if (_weaponHandler != null)
                {
                    _muzzlePoint = _weaponHandler.GetOrCreateMuzzlePoint();
                    return _muzzlePoint;
                }
                return null;
            }
            set => _muzzlePoint = value;
        }

        public Vector3 GetMuzzlePosition()
        {
            var muzzle = MuzzlePoint;
            if (muzzle != null) return muzzle.position;
            if (_weaponHandler != null) return _weaponHandler.GetMuzzlePosition();
            if (_aimCameraTransform != null)
            {
                return _aimCameraTransform.position + _aimCameraTransform.forward * 0.6f;
            }
            return transform.position + transform.forward * 0.5f + Vector3.up * 1.5f;
        }

        public Vector3 GetMuzzleForward()
        {
            var muzzle = MuzzlePoint;
            if (muzzle != null) return muzzle.forward;
            if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null) return _weaponHandler.EquippedWeapon.forward;
            if (_aimCameraTransform != null) return _aimCameraTransform.forward;
            return transform.forward;
        }

        public LayerMask HitMask
        {
            get => _hitMask;
            set => _hitMask = value;
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            _ownerCollider = GetComponent<Collider>() ?? GetComponent<CharacterController>();
            // Enforce empty-handed match start even if prefab had legacy ammo serialized
            _localHasWeaponEquipped = false;
            _currentAmmoInMag = 0;
            _reserveAmmo = 0;
            ResolveDependencies();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ResolveDependencies();

            HasWeaponEquipped.OnValueChanged += HandleWeaponEquippedChanged;
            ReserveAmmo.OnValueChanged += HandleReserveAmmoChanged;

            if (IsServer)
            {
                _localHasWeaponEquipped = false;
                _currentAmmoInMag = 0;
                _reserveAmmo = 0;
                HasWeaponEquipped.Value = false;
                ReserveAmmo.Value = 0;
            }
            else
            {
                _localHasWeaponEquipped = HasWeaponEquipped.Value;
                _reserveAmmo = ReserveAmmo.Value;
            }

            _perspectiveController?.RefreshWeaponEquippedState(IsWeaponEquipped);

            if (IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        public override void OnNetworkDespawn()
        {
            HasWeaponEquipped.OnValueChanged -= HandleWeaponEquippedChanged;
            ReserveAmmo.OnValueChanged -= HandleReserveAmmoChanged;
            base.OnNetworkDespawn();
        }

        private void HandleWeaponEquippedChanged(bool previous, bool current)
        {
            _localHasWeaponEquipped = current;
            _perspectiveController?.RefreshWeaponEquippedState(current);
            if (IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        private void HandleReserveAmmoChanged(int previous, int current)
        {
            _reserveAmmo = current;
            if (IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        private void Start()
        {
            _perspectiveController?.RefreshWeaponEquippedState(IsWeaponEquipped);
            if (!IsSpawned || IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        private void Update()
        {
            if (IsSpawned && !IsOwner) return;
            if (!IsWeaponEquipped) return;
            if (Scorched.Inventory.ScorchedInputGates.IsInventoryOpen) return;

            // Handle reload completion
            if (_isReloading)
            {
                if (Time.time >= _reloadEndTime)
                {
                    FinishReload();
                }
                return;
            }

            // Key polling for R (Reload)
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            {
                TryReload();
            }
        }

        public bool TryReload()
        {
            if (!IsWeaponDrawn) return false;
            if (_healthController != null && !_healthController.CanPerformCombatActions) return false;
            if (_isReloading) return false;
            if (_currentAmmoInMag >= _magCapacity) return false;
            if (CurrentReserveAmmo <= 0) return false;

            _isReloading = true;
            _reloadEndTime = Time.time + _reloadDuration;
            PushAmmoToHUD();
            return true;
        }

        public void ResetWeaponAfterRespawn()
        {
            _isReloading = false;
            _reloadEndTime = 0f;
            _nextLocalFireTime = 0f;
            if (!IsWeaponEquipped)
            {
                _currentAmmoInMag = 0;
                _reserveAmmo = 0;
            }
            else if (_currentAmmoInMag <= 0 && CurrentReserveAmmo > 0)
            {
                FinishReload();
            }
            _perspectiveController?.RefreshWeaponEquippedState(IsWeaponEquipped);
            PushAmmoToHUD();
        }

        /// <summary>
        /// Called authoritatively when crafting the AK-47 from 4 scavenged gun parts.
        /// Loads initial ammo into the magazine and remaining rounds into ReserveAmmo.
        /// </summary>
        /// <summary>
        /// Authoritative server-side cleanup when a player dies: captures whether they had the AK-47 equipped
        /// and how many rounds were loaded in its magazine so they can be placed into a Death Bag, then resets
        /// the player's hands to unarmed / 0 ammo before respawn.
        /// </summary>
        public void CollectAndResetWeaponOnDeathAuthoritative(out bool hadWeapon, out int magAmmo)
        {
            if (IsSpawned && !IsServer)
            {
                hadWeapon = false;
                magAmmo = 0;
                return;
            }

            hadWeapon = IsWeaponEquipped;
            magAmmo = hadWeapon ? Mathf.Clamp(_currentAmmoInMag, 0, _magCapacity) : 0;

            _localHasWeaponEquipped = false;
            _currentAmmoInMag = 0;
            _reserveAmmo = 0;
            _isReloading = false;
            _reloadEndTime = 0f;
            _nextLocalFireTime = 0f;

            if (IsSpawned)
            {
                HasWeaponEquipped.Value = false;
                ReserveAmmo.Value = 0;
                NotifyWeaponStrippedOnDeathOwnerRpc();
            }

            _perspectiveController?.RefreshWeaponEquippedState(false);
            PushAmmoToHUD();
        }

        [Rpc(SendTo.Owner)]
        private void NotifyWeaponStrippedOnDeathOwnerRpc()
        {
            _localHasWeaponEquipped = false;
            _currentAmmoInMag = 0;
            _reserveAmmo = 0;
            _isReloading = false;
            _reloadEndTime = 0f;
            _nextLocalFireTime = 0f;
            _perspectiveController?.RefreshWeaponEquippedState(false);
            PushAmmoToHUD();
        }

        public void EquipCraftedAK47Authoritative(int totalAvailableAmmo)
        {
            if (IsSpawned && !IsServer) return;

            _localHasWeaponEquipped = true;
            int loadToMag = Mathf.Min(_magCapacity, Mathf.Max(0, totalAvailableAmmo));
            int remainingReserve = Mathf.Max(0, totalAvailableAmmo - loadToMag);

            _currentAmmoInMag = loadToMag;
            _reserveAmmo = remainingReserve;

            if (IsSpawned)
            {
                HasWeaponEquipped.Value = true;
                ReserveAmmo.Value = remainingReserve;
                NotifyWeaponCraftedOwnerRpc(loadToMag, remainingReserve);
            }

            _perspectiveController?.RefreshWeaponEquippedState(true);
            PushAmmoToHUD();
        }

        public void AddReserveAmmoAuthoritative(int amount)
        {
            if (IsSpawned && !IsServer) return;
            if (amount <= 0) return;

            _reserveAmmo = Mathf.Max(0, CurrentReserveAmmo + amount);
            if (IsSpawned)
            {
                ReserveAmmo.Value = _reserveAmmo;
            }
            if (!IsSpawned || IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        public void SetReserveAmmoAuthoritative(int totalReserve)
        {
            if (IsSpawned && !IsServer) return;
            int clamped = Mathf.Max(0, totalReserve);
            _reserveAmmo = clamped;
            if (IsSpawned)
            {
                ReserveAmmo.Value = clamped;
            }
            if (!IsSpawned || IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        [Rpc(SendTo.Owner)]
        private void NotifyWeaponCraftedOwnerRpc(int magLoaded, int reserveLoaded)
        {
            _localHasWeaponEquipped = true;
            _currentAmmoInMag = magLoaded;
            _reserveAmmo = reserveLoaded;
            _isReloading = false;
            _perspectiveController?.RefreshWeaponEquippedState(true);
            PushAmmoToHUD();
        }

        private void FinishReload()
        {
            int availReserve = CurrentReserveAmmo;
            int needed = _magCapacity - _currentAmmoInMag;
            int take = Mathf.Min(needed, availReserve);
            _currentAmmoInMag += take;
            _reserveAmmo = Mathf.Max(0, availReserve - take);
            _isReloading = false;

            if (IsSpawned)
            {
                if (IsServer)
                {
                    ReserveAmmo.Value = _reserveAmmo;
                    GetComponent<Scorched.Inventory.PlayerInventoryController>()?.ConsumeAmmoForReloadAuthoritative(take);
                }
                else
                {
                    ConsumeReserveAmmoServerRpc(take);
                }
            }
            else
            {
                GetComponent<Scorched.Inventory.PlayerInventoryController>()?.ConsumeAmmoForReloadAuthoritative(take);
            }
            PushAmmoToHUD();
        }

        [Rpc(SendTo.Server)]
        private void ConsumeReserveAmmoServerRpc(int amount)
        {
            if (amount <= 0) return;
            int next = Mathf.Max(0, ReserveAmmo.Value - amount);
            ReserveAmmo.Value = next;
            _reserveAmmo = next;
            if (!IsOwner)
            {
                _currentAmmoInMag = Mathf.Min(_magCapacity, _currentAmmoInMag + amount);
            }
            GetComponent<Scorched.Inventory.PlayerInventoryController>()?.ConsumeAmmoForReloadAuthoritative(amount);
        }

        public void PushAmmoToHUD()
        {
            if (!IsWeaponEquipped)
            {
                Scorched.UI.ScorchedHUDController.Instance?.UpdateAmmo(0, CurrentReserveAmmo, false, "UNARMED");
                Scorched.UI.L4DHUDManager.Instance?.UpdateAmmo(0, CurrentReserveAmmo, false, "UNARMED");
                return;
            }

            string name = _currentWeapon != null ? _currentWeapon.WeaponName : "AK-47 TACTICAL";
            Scorched.UI.ScorchedHUDController.Instance?.UpdateAmmo(_currentAmmoInMag, CurrentReserveAmmo, _isReloading, name);
            Scorched.UI.L4DHUDManager.Instance?.UpdateAmmo(_currentAmmoInMag, CurrentReserveAmmo, _isReloading, name);
        }

        private void ResolveDependencies()
        {
            _weaponHandler = GetComponent<Scorched.Player.PlayerWeaponHandler>();
            _perspectiveController = GetComponent<Scorched.Player.FPSPerspectiveController>();
            _healthController = GetComponent<Scorched.Player.PlayerHealthController>();
            _hotbar = GetComponent<Scorched.Inventory.PlayerHotbarController>();

            // Auto-link aim camera
            if (_aimCameraTransform == null)
            {
                var cam = GetComponentInChildren<Camera>();
                if (cam != null)
                {
                    _aimCameraTransform = cam.transform;
                }
                else if (Camera.main != null)
                {
                    _aimCameraTransform = Camera.main.transform;
                }
            }

            // Clean up any legacy or rogue MuzzlePoint_Auto parented directly to the player body/root
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "MuzzlePoint_Auto")
                {
                    if (_muzzlePoint == child) _muzzlePoint = null;
                    Destroy(child.gameObject);
                }
            }

            if (_muzzlePoint == null && _weaponHandler != null)
            {
                _muzzlePoint = _weaponHandler.GetOrCreateMuzzlePoint();
            }

            if (_muzzlePoint == null)
            {
                _muzzlePoint = FindMuzzleRecursively(transform);
            }

            // Auto-link fallback weapon data
            if (_currentWeapon == null)
            {
#if UNITY_EDITOR
                _currentWeapon = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDataSO>("Assets/Settings/Weapons/AK47_Rifle.asset");
#endif
            }
        }

        private static Transform FindMuzzleRecursively(Transform parent)
        {
            foreach (Transform child in parent)
            {
                if (string.Equals(child.name, "MuzzlePoint", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(child.name, "Muzzle", StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                var found = FindMuzzleRecursively(child);
                if (found != null) return found;
            }
            return null;
        }
        #endregion

        #region Public Fire API
        /// <summary>
        /// Modular firing entry point called by player input or weapon handlers.
        /// </summary>
        /// <param name="isFiring">True while the fire button/trigger is held down.</param>
        /// <returns>True if a shot was successfully discharged.</returns>
        public bool TryFire(bool isFiring)
        {
            if (!isFiring) return false;
            if (!IsWeaponDrawn) return false;
            if (_currentWeapon == null) return false;
            if (_healthController != null && !_healthController.CanPerformCombatActions) return false;

            // Only network owner controls local firing input
            if (IsSpawned && !IsOwner) return false;

            // Enforce match combat lockout (e.g. waiting for equal teams in Team Match)
            if (Scorched.Combat.NetworkMatchManager.Instance != null && !Scorched.Combat.NetworkMatchManager.Instance.IsCombatActive)
            {
                return false;
            }

            // Lock out firing while reloading
            if (_isReloading) return false;

            // Check magazine ammunition
            if (_currentAmmoInMag <= 0)
            {
                if (_reserveAmmo > 0)
                {
                    TryReload();
                }
                return false;
            }

            // Cooldown enforcement
            if (Time.time < _nextLocalFireTime) return false;

            _nextLocalFireTime = Time.time + _currentWeapon.FireCooldownSeconds;

            // Consume bullet from magazine and push to HUD
            _currentAmmoInMag--;
            PushAmmoToHUD();

            ExecuteDischarge();
            return true;
        }
        #endregion

        #region Step 1: Local Client Prediction & Firing
        private void ExecuteDischarge()
        {
            if (_perspectiveController != null)
            {
                _perspectiveController.PrepareViewmodelForFiring();
            }

            Vector3 muzzleOrigin = GetMuzzlePosition();

            // Calculate aim target point via camera center raycast
            Transform aimTrans = _aimCameraTransform != null ? _aimCameraTransform : transform;

            // Start ray slightly forward from camera lens to avoid intersecting own colliders
            Vector3 rayStart = aimTrans.position + (aimTrans.forward * 0.45f);
            Vector3 targetPoint = rayStart + (aimTrans.forward * _currentWeapon.MaxRange);

            if (CombatHitResolver.TryRaycastFirstCombatHit(
                rayStart,
                aimTrans.forward,
                _currentWeapon.MaxRange,
                _hitMask,
                transform,
                out RaycastHit aimHit,
                out _,
                out _))
            {
                targetPoint = aimHit.point;
            }

            // Apply conical spread divergence
            Vector3 baseDirection = (targetPoint - muzzleOrigin).normalized;
            if (baseDirection.sqrMagnitude < 0.001f)
            {
                baseDirection = aimTrans.forward;
            }
            Vector3 fireDirection = ApplyConicalSpread(baseDirection, _currentWeapon.SpreadAngle);

            // 1. Immediate local simulation (0ms perceived latency for the shooter)
            SimulateLocalShot(muzzleOrigin, fireDirection);

            // 2. Transmit firing intent to server
            if (IsSpawned)
            {
                double clientTimestamp = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0.0;
                RequestFireRpc(muzzleOrigin, fireDirection, clientTimestamp);
            }
            else
            {
                // Standalone / offline test mode: execute immediate anatomical damage resolution
                int localShotId = ++_shotSequence;
                if (_currentWeapon.IsHitscan)
                {
                    if (CombatHitResolver.TryRaycastFirstCombatHit(
                        muzzleOrigin,
                        fireDirection,
                        _currentWeapon.MaxRange,
                        _hitMask,
                        transform,
                        out RaycastHit hit,
                        out IDamageable damageable,
                        out BodyPartHitbox hitbox))
                    {
                        CombatHitResolver.ApplyResolvedHit(
                            damageable,
                            hitbox,
                            _currentWeapon.BaseDamage,
                            _currentWeapon.HydrationDrainOnHit,
                            0,
                            hit.point,
                            localShotId);
                    }
                }
            }
        }

        private static Vector3 ApplyConicalSpread(Vector3 forward, float spreadAngleDegrees)
        {
            if (spreadAngleDegrees <= 0.001f) return forward;

            float angleRad = spreadAngleDegrees * Mathf.Deg2Rad;
            float randomRadius = UnityEngine.Random.Range(0f, Mathf.Tan(angleRad));
            float randomAngle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);

            Vector2 circle = new Vector2(Mathf.Cos(randomAngle), Mathf.Sin(randomAngle)) * randomRadius;
            Vector3 right = Vector3.Cross(forward, Vector3.up);
            if (right.sqrMagnitude < 0.001f)
            {
                right = Vector3.Cross(forward, Vector3.right);
            }
            right.Normalize();
            Vector3 up = Vector3.Cross(right, forward).normalized;

            return (forward + right * circle.x + up * circle.y).normalized;
        }

        /// <summary>
        /// Executes client-side visual effects (tracer flight, muzzle flash & impact VFX) without garbage collection.
        /// </summary>
        private void SimulateLocalShot(Vector3 origin, Vector3 direction)
        {
            if (_currentWeapon == null) return;

            // Trigger viewmodel recoil kick if Modern Warfare mode is active
            if (_perspectiveController != null)
            {
                _perspectiveController.ApplyViewmodelRecoilKick();
            }

            // Trigger muzzle flash at gun barrel tip
            if (BulletVisualPoolManager.Instance != null)
            {
                BulletVisualPoolManager.Instance.SpawnMuzzleFlash(origin, direction, MuzzlePoint);
            }

            if (_currentWeapon.IsHitscan)
            {
                // Hitscan: determine visual endpoint cleanly via anatomical combat resolver
                Vector3 endPoint;
                if (CombatHitResolver.TryRaycastFirstCombatHit(
                    origin,
                    direction,
                    _currentWeapon.MaxRange,
                    _hitMask,
                    transform,
                    out RaycastHit hit,
                    out _,
                    out _))
                {
                    endPoint = hit.point;

                    // Trigger pooled impact sparks, flash light and bullet mark decal
                    if (BulletVisualPoolManager.Instance != null)
                    {
                        BulletVisualPoolManager.Instance.SpawnImpact(hit.point, hit.normal, hit.collider);
                    }
                }
                else
                {
                    endPoint = origin + (direction * _currentWeapon.MaxRange);
                }

                // Trigger pooled tracer travel
                if (BulletVisualPoolManager.Instance != null)
                {
                    BulletVisualPoolManager.Instance.SpawnTracer(origin, endPoint, _currentWeapon.MuzzleVelocity);
                }
            }
            else
            {
                // Stepped Kinematic Ballistics: register visual projectile locally
                if (BallisticsSimulationManager.Instance != null)
                {
                    BallisticsSimulationManager.Instance.RegisterProjectile(
                        origin,
                        direction * _currentWeapon.MuzzleVelocity,
                        _currentWeapon.BaseDamage,
                        _currentWeapon.HydrationDrainOnHit,
                        _currentWeapon.GravityMultiplier,
                        IsSpawned ? OwnerClientId : 0,
                        isServerAuthoritative: false,
                        _hitMask,
                        attachVisualTrail: true,
                        ignoredRoot: transform
                    );
                }
            }
        }
        #endregion

        #region Step 2: Server Authoritative Validation & Damage
        /// <summary>
        /// Server RPC: Validates the firing request against anti-cheat tolerances,
        /// performs authoritative damage resolution, and broadcasts the shot to peers.
        /// </summary>
        [Rpc(SendTo.Server)]
        private void RequestFireRpc(Vector3 origin, Vector3 direction, double clientTimestamp)
        {
            if (!IsWeaponEquipped) return;
            if (_currentWeapon == null) return;
            if (_healthController != null && !_healthController.CanPerformCombatActions) return;

            // Reject server firing if combat is locked out (unequal teams)
            if (Scorched.Combat.NetworkMatchManager.Instance != null && !Scorched.Combat.NetworkMatchManager.Instance.IsCombatActive)
            {
                return;
            }

            double currentTime = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0.0;
            float cooldown = _currentWeapon.FireCooldownSeconds * RateToleranceMultiplier;

            // 1. Anti-Cheat: Validate fire rate interval
            if (currentTime - _lastServerValidatedFireTime < cooldown)
            {
                // Rate-limit violation: drop request
                return;
            }
            _lastServerValidatedFireTime = currentTime;

            if (!IsOwner && _currentAmmoInMag > 0)
            {
                _currentAmmoInMag--;
            }

            // 2. Anti-Cheat: Validate muzzle origin distance from player transform
            if (Vector3.Distance(origin, transform.position) > MaxOriginToleranceMeters)
            {
                // Tele-fire / position desync exploit: snap origin to server position
                origin = transform.position + Vector3.up * 1.5f + transform.forward * 0.5f;
            }

            // 3. Authoritative damage resolution (anatomical hitboxes, single-hit per shot)
            int serverShotId = ++_serverShotSequence;
            if (_currentWeapon.IsHitscan)
            {
                if (CombatHitResolver.TryRaycastFirstCombatHit(
                    origin,
                    direction,
                    _currentWeapon.MaxRange,
                    _hitMask,
                    transform,
                    out RaycastHit hit,
                    out IDamageable damageable,
                    out BodyPartHitbox hitbox))
                {
                    CombatHitResolver.ApplyResolvedHit(
                        damageable,
                        hitbox,
                        _currentWeapon.BaseDamage,
                        _currentWeapon.HydrationDrainOnHit,
                        OwnerClientId,
                        hit.point,
                        serverShotId);

                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                    {
                        hit.rigidbody.AddForceAtPosition(direction * (_currentWeapon.BaseDamage * 0.5f), hit.point, ForceMode.Impulse);
                    }
                }
            }
            else
            {
                // Non-hitscan: Register authoritative stepped projectile on server
                if (BallisticsSimulationManager.Instance != null)
                {
                    BallisticsSimulationManager.Instance.RegisterProjectile(
                        origin,
                        direction * _currentWeapon.MuzzleVelocity,
                        _currentWeapon.BaseDamage,
                        _currentWeapon.HydrationDrainOnHit,
                        _currentWeapon.GravityMultiplier,
                        OwnerClientId,
                        isServerAuthoritative: true,
                        _hitMask,
                        attachVisualTrail: false,
                        ignoredRoot: transform,
                        shotId: serverShotId
                    );
                }
            }

            // 4. Replicate shot event to all observers (excluding the local shooter)
            ReplicateShotRpc(origin, direction);
        }
        #endregion

        #region Step 3: Remote Client Replication
        /// <summary>
        /// Client RPC sent only to non-owning clients to spawn local visual tracers and impacts.
        /// </summary>
        [Rpc(SendTo.NotOwner)]
        private void ReplicateShotRpc(Vector3 origin, Vector3 direction)
        {
            // Non-owning remote clients simulate visuals only (0 authority, 0 GC)
            SimulateLocalShot(origin, direction);
        }
        #endregion
    }
}
