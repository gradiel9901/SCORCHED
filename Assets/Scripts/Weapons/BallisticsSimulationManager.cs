using System;
using System.Collections.Generic;
using UnityEngine;
using Scorched.Combat;

namespace Scorched.Weapons
{
    /// <summary>
    /// Stepped kinematic ballistic simulation manager for non-hitscan projectile weapons.
    /// Uses pure C# structs in a pre-allocated list to simulate multi-tick ballistic physics,
    /// gravity curves, and continuous linecast collision detection without Unity Rigidbodies
    /// or heap allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public class BallisticsSimulationManager : MonoBehaviour
    {
        private static BallisticsSimulationManager s_Instance;
        public static BallisticsSimulationManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindAnyObjectByType<BallisticsSimulationManager>();
                    if (s_Instance == null)
                    {
                        var go = new GameObject("BallisticsSimulationManager");
                        s_Instance = go.AddComponent<BallisticsSimulationManager>();
                    }
                }
                return s_Instance;
            }
            private set => s_Instance = value;
        }

        #region Active Projectile Struct
        public struct ActiveProjectile
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Damage;
            public float HydrationDrain;
            public float GravityMultiplier;
            public float RemainingLifetime;
            public ulong ShooterClientId;
            public bool IsServerAuthoritative;
            public LayerMask HitMask;
            public TrailRenderer VisualTrail;
            public Transform IgnoredRoot;
            public int ShotId;
        }
        #endregion

        #region Serialized Settings
        [Header("Simulation Capacity")]
        [SerializeField] private int _initialCapacity = 256;
        [SerializeField] private float _defaultLifetime = 5.0f;
        #endregion

        #region Runtime State
        private readonly List<ActiveProjectile> _projectiles = new List<ActiveProjectile>(256);
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_Instance = this;

            if (_initialCapacity > 0)
            {
                _projectiles.Capacity = _initialCapacity;
            }
        }

        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }

            // Return any remaining visual trails to pool
            for (int i = 0; i < _projectiles.Count; i++)
            {
                if (_projectiles[i].VisualTrail != null && BulletVisualPoolManager.Instance != null)
                {
                    BulletVisualPoolManager.Instance.ReleaseTrail(_projectiles[i].VisualTrail);
                }
            }
            _projectiles.Clear();
        }

        private void Update()
        {
            SimulateStep(Time.deltaTime);
        }
        #endregion

        #region Public Registration API
        /// <summary>
        /// Registers a new active ballistic projectile into the stepped simulation.
        /// Zero heap allocation using value-type struct copy.
        /// </summary>
        public void RegisterProjectile(
            Vector3 origin,
            Vector3 initialVelocity,
            float damage,
            float hydrationDrain,
            float gravityMultiplier,
            ulong shooterClientId,
            bool isServerAuthoritative,
            LayerMask hitMask,
            bool attachVisualTrail = false,
            float maxLifetime = 5.0f,
            Transform ignoredRoot = null,
            int shotId = -1)
        {
            TrailRenderer trail = null;
            if (attachVisualTrail && BulletVisualPoolManager.Instance != null)
            {
                trail = BulletVisualPoolManager.Instance.GetPooledTrail(origin);
            }

            _projectiles.Add(new ActiveProjectile
            {
                Position = origin,
                Velocity = initialVelocity,
                Damage = damage,
                HydrationDrain = hydrationDrain,
                GravityMultiplier = gravityMultiplier,
                RemainingLifetime = maxLifetime > 0f ? maxLifetime : _defaultLifetime,
                ShooterClientId = shooterClientId,
                IsServerAuthoritative = isServerAuthoritative,
                HitMask = hitMask,
                VisualTrail = trail,
                IgnoredRoot = ignoredRoot,
                ShotId = shotId
            });
        }
        #endregion

        #region Stepped Simulation (Zero GC Backwards Loop)
        private void SimulateStep(float dt)
        {
            Vector3 gravity = Physics.gravity;

            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                ActiveProjectile p = _projectiles[i];
                p.RemainingLifetime -= dt;

                // 1. Expire lifetime
                if (p.RemainingLifetime <= 0f)
                {
                    CleanupProjectileVisual(ref p);
                    RemoveProjectileAtSwapBack(i);
                    continue;
                }

                // 2. Ballistic acceleration
                p.Velocity += gravity * (p.GravityMultiplier * dt);

                // 3. Step forward
                Vector3 currentPos = p.Position;
                Vector3 nextPos = currentPos + (p.Velocity * dt);

                // 4. Continuous collision linecast via anatomical combat resolver
                if (CombatHitResolver.TryLinecastFirstCombatHit(
                    currentPos,
                    nextPos,
                    p.HitMask,
                    p.IgnoredRoot,
                    out RaycastHit hit,
                    out IDamageable damageable,
                    out BodyPartHitbox hitbox))
                {
                    // Server-authoritative damage resolution
                    if (p.IsServerAuthoritative)
                    {
                        ResolveDamage(hit, damageable, hitbox, p);
                    }

                    // Client-side visual impact
                    if (BulletVisualPoolManager.Instance != null)
                    {
                        BulletVisualPoolManager.Instance.SpawnImpact(hit.point, hit.normal, hit.collider);
                    }

                    // Release visual trail & remove
                    CleanupProjectileVisual(ref p);
                    RemoveProjectileAtSwapBack(i);
                    continue;
                }

                // 5. Advance position and update visual trail
                p.Position = nextPos;
                if (p.VisualTrail != null)
                {
                    p.VisualTrail.transform.position = nextPos;
                }

                // Update struct in place
                _projectiles[i] = p;
            }
        }

        private static void ResolveDamage(RaycastHit hit, IDamageable damageable, BodyPartHitbox hitbox, ActiveProjectile p)
        {
            CombatHitResolver.ApplyResolvedHit(
                damageable,
                hitbox,
                p.Damage,
                p.HydrationDrain,
                p.ShooterClientId,
                hit.point,
                p.ShotId);

            if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
            {
                hit.rigidbody.AddForceAtPosition(p.Velocity.normalized * (p.Damage * 0.5f), hit.point, ForceMode.Impulse);
            }
        }

        private static void CleanupProjectileVisual(ref ActiveProjectile p)
        {
            if (p.VisualTrail != null && BulletVisualPoolManager.Instance != null)
            {
                BulletVisualPoolManager.Instance.ReleaseTrail(p.VisualTrail);
                p.VisualTrail = null;
            }
        }

        private void RemoveProjectileAtSwapBack(int index)
        {
            int lastIndex = _projectiles.Count - 1;
            _projectiles[index] = _projectiles[lastIndex];
            _projectiles.RemoveAt(lastIndex);
        }
        #endregion
    }
}
