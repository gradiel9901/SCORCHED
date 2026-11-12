using System;
using UnityEngine;
using Scorched.Player;

namespace Scorched.Combat
{
    /// <summary>
    /// Anatomical damage zones attached strictly to third-person skeleton bones.
    /// </summary>
    public enum BodyPartType : byte
    {
        Head = 0,
        UpperTorso = 1,
        LowerTorso = 2,
        Arms = 3,
        Legs = 4
    }

    /// <summary>
    /// Modular per-limb / body-part collider implementing IDamageable.
    /// Scales incoming weapon base damage by the anatomical zone multiplier and routes
    /// hits to the root PlayerHealthController.
    /// </summary>
    [DisallowMultipleComponent]
    public class BodyPartHitbox : MonoBehaviour, IDamageable
    {
        [Header("Hitbox Zone")]
        [Tooltip("Anatomical damage region represented by this bone collider.")]
        [SerializeField] private BodyPartType _bodyPart = BodyPartType.UpperTorso;

        [Tooltip("Optional override multiplier. Leave <= 0 to use canonical zone multiplier.")]
        [SerializeField] private float _damageMultiplierOverride = -1f;

        [Tooltip("Auto-resolved collider attached to this bone.")]
        [SerializeField] private Collider _hitboxCollider;

        private PlayerHealthController _ownerHealthController;

        public BodyPartType BodyPart => _bodyPart;
        public float DamageMultiplier => _damageMultiplierOverride > 0f ? _damageMultiplierOverride : GetDefaultMultiplier(_bodyPart);
        public Collider HitboxCollider => _hitboxCollider;
        public PlayerHealthController OwnerHealthController
        {
            get
            {
                if (_ownerHealthController == null)
                {
                    _ownerHealthController = GetComponentInParent<PlayerHealthController>(true);
                }
                return _ownerHealthController;
            }
        }

        public static float GetDefaultMultiplier(BodyPartType part)
        {
            switch (part)
            {
                case BodyPartType.Head:       return 1.65f;
                case BodyPartType.UpperTorso: return 1.00f;
                case BodyPartType.LowerTorso: return 0.90f;
                case BodyPartType.Arms:       return 0.75f;
                case BodyPartType.Legs:       return 0.70f;
                default:                      return 1.00f;
            }
        }

        public void Configure(BodyPartType part, Collider col = null)
        {
            _bodyPart = part;
            _damageMultiplierOverride = -1f;
            _hitboxCollider = col != null ? col : GetComponent<Collider>();
            _ownerHealthController = GetComponentInParent<PlayerHealthController>(true);
            SyncControllerCollisionIgnore();
        }

        public void SetHitboxActive(bool active)
        {
            if (_hitboxCollider == null)
            {
                _hitboxCollider = GetComponent<Collider>();
            }
            if (_hitboxCollider != null)
            {
                _hitboxCollider.enabled = active;
            }
        }

        private void Reset()
        {
            _hitboxCollider = GetComponent<Collider>();
            _ownerHealthController = GetComponentInParent<PlayerHealthController>(true);
        }

        private void Awake()
        {
            if (_hitboxCollider == null)
            {
                _hitboxCollider = GetComponent<Collider>();
            }
            _ownerHealthController = GetComponentInParent<PlayerHealthController>(true);
            SyncControllerCollisionIgnore();
        }

        private void SyncControllerCollisionIgnore()
        {
            if (_hitboxCollider == null)
            {
                return;
            }

            CharacterController cc = GetComponentInParent<CharacterController>(true);
            if (cc != null && cc != _hitboxCollider)
            {
                Physics.IgnoreCollision(_hitboxCollider, cc, true);
            }
        }

        public void ApplyCombatHit(float healthDamage, float hydrationDamage, ulong attackerClientId, Vector3 hitPoint)
        {
            PlayerHealthController health = OwnerHealthController;
            if (health != null)
            {
                health.ApplyBodyPartHit(healthDamage, hydrationDamage, attackerClientId, hitPoint, _bodyPart, DamageMultiplier, -1);
            }
        }
    }

    /// <summary>
    /// Shared combat raycast / linecast resolution helper so hitscan and ballistic systems
    /// pierce root CharacterControllers cleanly and always strike the closest anatomical BodyPartHitbox
    /// (or solid world geometry) once per shot.
    /// </summary>
    public static class CombatHitResolver
    {
        public static bool TryLinecastFirstCombatHit(
            Vector3 start,
            Vector3 end,
            LayerMask hitMask,
            Transform ignoredRoot,
            out RaycastHit bestHit,
            out IDamageable damageable,
            out BodyPartHitbox hitbox)
        {
            Vector3 delta = end - start;
            float distance = delta.magnitude;
            if (distance <= 1e-5f)
            {
                bestHit = default;
                damageable = null;
                hitbox = null;
                return false;
            }

            return TryRaycastFirstCombatHit(start, delta / distance, distance, hitMask, ignoredRoot, out bestHit, out damageable, out hitbox);
        }

        public static bool TryRaycastFirstCombatHit(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            LayerMask hitMask,
            Transform ignoredRoot,
            out RaycastHit bestHit,
            out IDamageable damageable,
            out BodyPartHitbox hitbox)
        {
            bestHit = default;
            damageable = null;
            hitbox = null;

            RaycastHit[] hits = Physics.RaycastAll(origin, direction, maxDistance, hitMask, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
            {
                return false;
            }

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit candidate = hits[i];
                Collider col = candidate.collider;
                if (col == null)
                {
                    continue;
                }

                if (ignoredRoot != null && col.transform.root == ignoredRoot)
                {
                    continue;
                }

                // Never allow a player's outer movement capsule to intercept or shield limb/head hitboxes
                if (col is CharacterController)
                {
                    continue;
                }

                BodyPartHitbox partHitbox = col.GetComponent<BodyPartHitbox>();
                if (partHitbox != null)
                {
                    if (!partHitbox.enabled || !partHitbox.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    PlayerHealthController ownerHealth = partHitbox.OwnerHealthController;
                    if (ownerHealth != null && ownerHealth.IsDead)
                    {
                        continue;
                    }

                    bestHit = candidate;
                    hitbox = partHitbox;
                    damageable = partHitbox;
                    return true;
                }

                // Ignore unrelated scene trigger volumes (spawn triggers, FX triggers, etc.)
                if (col.isTrigger)
                {
                    continue;
                }

                IDamageable nonHitboxDamageable = col.GetComponentInParent<IDamageable>();
                if (nonHitboxDamageable is PlayerHealthController phc && phc.IsDead)
                {
                    continue;
                }

                bestHit = candidate;
                damageable = nonHitboxDamageable;
                hitbox = null;
                return true;
            }

            return false;
        }

        public static void ApplyResolvedHit(
            IDamageable damageable,
            BodyPartHitbox hitbox,
            float baseDamage,
            float hydrationDamage,
            ulong attackerClientId,
            Vector3 hitPoint,
            int shotId)
        {
            if (hitbox != null && hitbox.OwnerHealthController != null)
            {
                hitbox.OwnerHealthController.ApplyBodyPartHit(
                    baseDamage,
                    hydrationDamage,
                    attackerClientId,
                    hitPoint,
                    hitbox.BodyPart,
                    hitbox.DamageMultiplier,
                    shotId);
                return;
            }

            damageable?.ApplyCombatHit(baseDamage, hydrationDamage, attackerClientId, hitPoint);
        }
    }
}

