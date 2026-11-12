using UnityEngine;

namespace Scorched.Weapons
{
    /// <summary>
    /// Configuration asset defining ballistic parameters, hitscan vs projectile behavior,
    /// damage attributes, and survival penalties for weapons in "SCORCHED".
    /// </summary>
    [CreateAssetMenu(fileName = "NewWeaponData", menuName = "Scorched/Weapons/Weapon Data", order = 100)]
    public class WeaponDataSO : ScriptableObject
    {
        [Header("General Identity")]
        [Tooltip("Display name of the weapon.")]
        [SerializeField] private string _weaponName = "AK-47 Tactical";

        [Header("Ballistics & Archetype")]
        [Tooltip("True for immediate hitscan raycast detection; False for stepped kinematic ballistic projectiles.")]
        [SerializeField] private bool _isHitscan = true;

        [Tooltip("Muzzle velocity in meters per second (used for tracer flight speed in hitscan and trajectory velocity in projectiles).")]
        [SerializeField] private float _muzzleVelocity = 715.0f;

        [Tooltip("Maximum effective travel/detection range in meters.")]
        [SerializeField] private float _maxRange = 250.0f;

        [Tooltip("Gravity acceleration multiplier for non-hitscan ballistic drop (1.0 = standard earth gravity).")]
        [Range(0.0f, 5.0f)]
        [SerializeField] private float _gravityMultiplier = 1.0f;

        [Tooltip("Conical spread divergence angle in degrees (0 = pinpoint accuracy).")]
        [Range(0.0f, 15.0f)]
        [SerializeField] private float _spreadAngle = 0.45f;

        [Header("Rate of Fire")]
        [Tooltip("Rate of fire in rounds per minute (RPM).")]
        [Range(30.0f, 1500.0f)]
        [SerializeField] private float _fireRateRPM = 600.0f;

        [Header("Lethality & Survival Penalties")]
        [Tooltip("Base health damage subtracted per projectile hit.")]
        [Range(1.0f, 300.0f)]
        [SerializeField] private float _baseDamage = 22.0f;

        [Tooltip("Tactical hydration percentage/amount drained from the target on hit in Scorched.")]
        [Range(0.0f, 100.0f)]
        [SerializeField] private float _hydrationDrainOnHit = 15.0f;

        #region Public Accessors
        public string WeaponName => _weaponName;
        public bool IsHitscan => _isHitscan;
        public float MuzzleVelocity => _muzzleVelocity;
        public float MaxRange => _maxRange;
        public float GravityMultiplier => _gravityMultiplier;
        public float SpreadAngle => _spreadAngle;
        public float FireRateRPM => _fireRateRPM;
        public float BaseDamage => _baseDamage;
        public float HydrationDrainOnHit => _hydrationDrainOnHit;

        /// <summary>
        /// Delay in seconds between consecutive shots based on FireRateRPM.
        /// </summary>
        public float FireCooldownSeconds => 60.0f / Mathf.Max(1.0f, _fireRateRPM);
        #endregion
    }
}
