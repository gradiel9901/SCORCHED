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
        [SerializeField] private int _currentAmmoInMag = 30;

        [Tooltip("Reserve ammunition available for reloading.")]
        [SerializeField] private int _reserveAmmo = 120;

        [Tooltip("Time required to reload magazine in seconds.")]
        [SerializeField] private float _reloadDuration = 2.2f;
        #endregion

        #region Runtime State
        private float _nextLocalFireTime = 0f;
        private double _lastServerValidatedFireTime = 0.0;
        private Collider _ownerCollider;
        private Scorched.Player.PlayerWeaponHandler _weaponHandler;
        private bool _isReloading = false;
        private float _reloadEndTime = 0f;
        #endregion

        #region Public Accessors
        public WeaponDataSO CurrentWeapon
        {
            get => _currentWeapon;
            set => _currentWeapon = value;
        }

        public Transform MuzzlePoint
        {
            get
            {
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
                return _aimCameraTransform.position + _aimCameraTransform.forward * 0.6f - _aimCameraTransform.up * 0.12f + _aimCameraTransform.right * 0.1f;
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
            ResolveDependencies();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ResolveDependencies();
            if (IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        private void Start()
        {
            if (!IsSpawned || IsOwner)
            {
                PushAmmoToHUD();
            }
        }

        private void Update()
        {
            if (IsSpawned && !IsOwner) return;

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
            if (_isReloading) return false;
            if (_currentAmmoInMag >= _magCapacity) return false;
            if (_reserveAmmo <= 0) return false;

            _isReloading = true;
            _reloadEndTime = Time.time + _reloadDuration;
            PushAmmoToHUD();
            return true;
        }

        private void FinishReload()
        {
            int needed = _magCapacity - _currentAmmoInMag;
            int take = Mathf.Min(needed, _reserveAmmo);
            _currentAmmoInMag += take;
            _reserveAmmo -= take;
            _isReloading = false;
            PushAmmoToHUD();
        }

        public void PushAmmoToHUD()
        {
            string name = _currentWeapon != null ? _currentWeapon.WeaponName : "AK-47 TACTICAL";
            Scorched.UI.L4DHUDManager.Instance?.UpdateAmmo(_currentAmmoInMag, _reserveAmmo, _isReloading, name);
        }

        private void ResolveDependencies()
        {
            _weaponHandler = GetComponent<Scorched.Player.PlayerWeaponHandler>();

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
            if (_currentWeapon == null) return false;

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
            Vector3 muzzleOrigin = GetMuzzlePosition();

            // Calculate aim target point via camera center raycast
            Transform aimTrans = _aimCameraTransform != null ? _aimCameraTransform : transform;

            // Start ray slightly forward from camera lens to avoid intersecting own colliders
            Vector3 rayStart = aimTrans.position + (aimTrans.forward * 0.45f);
            Ray aimRay = new Ray(rayStart, aimTrans.forward);

            Vector3 targetPoint = rayStart + (aimTrans.forward * _currentWeapon.MaxRange);

            // Filter out own player and any child colliders
            RaycastHit[] hits = Physics.RaycastAll(aimRay, _currentWeapon.MaxRange, _hitMask, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                float nearestDist = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (h.collider != null && h.collider.transform.root != transform)
                    {
                        if (h.distance < nearestDist)
                        {
                            nearestDist = h.distance;
                            targetPoint = h.point;
                        }
                    }
                }
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
                // Standalone / offline test mode: execute immediate damage resolution
                if (_currentWeapon.IsHitscan)
                {
                    if (Physics.Raycast(muzzleOrigin, fireDirection, out RaycastHit hit, _currentWeapon.MaxRange, _hitMask, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider.transform.root != transform)
                        {
                            hit.collider.GetComponentInParent<IDamageable>()?.ApplyCombatHit(_currentWeapon.BaseDamage, _currentWeapon.HydrationDrainOnHit, 0, hit.point);
                        }
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

            // Trigger muzzle flash at gun barrel tip
            if (BulletVisualPoolManager.Instance != null)
            {
                BulletVisualPoolManager.Instance.SpawnMuzzleFlash(origin, direction, MuzzlePoint);
            }

            if (_currentWeapon.IsHitscan)
            {
                // Hitscan: determine endpoint via instantaneous raycast
                Vector3 endPoint;
                if (Physics.Raycast(origin, direction, out RaycastHit hit, _currentWeapon.MaxRange, _hitMask, QueryTriggerInteraction.Ignore))
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
                        attachVisualTrail: true
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
            if (_currentWeapon == null) return;

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

            // 2. Anti-Cheat: Validate muzzle origin distance from player transform
            if (Vector3.Distance(origin, transform.position) > MaxOriginToleranceMeters)
            {
                // Tele-fire / position desync exploit: snap origin to server position
                origin = transform.position + Vector3.up * 1.5f + transform.forward * 0.5f;
            }

            // 3. Authoritative damage resolution
            if (_currentWeapon.IsHitscan)
            {
                RaycastHit[] hits = Physics.RaycastAll(origin, direction, _currentWeapon.MaxRange, _hitMask, QueryTriggerInteraction.Ignore);
                if (hits != null && hits.Length > 0)
                {
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var hit = hits[i];
                        if (hit.collider != null && hit.collider.transform.root != transform)
                        {
                            // Apply health damage and tactical hydration drain
                            var damageable = hit.collider.GetComponentInParent<IDamageable>();
                            if (damageable != null)
                            {
                                damageable.ApplyCombatHit(_currentWeapon.BaseDamage, _currentWeapon.HydrationDrainOnHit, OwnerClientId, hit.point);
                            }

                            // Physical impulse on rigidbodies
                            if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                            {
                                hit.rigidbody.AddForceAtPosition(direction * (_currentWeapon.BaseDamage * 0.5f), hit.point, ForceMode.Impulse);
                            }
                            break;
                        }
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
                        attachVisualTrail: false // Server does not need visual trail rendering
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
