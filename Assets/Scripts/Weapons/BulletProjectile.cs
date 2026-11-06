using System.Collections.Generic;
using UnityEngine;

namespace Scorched.Weapons
{
    /// <summary>
    /// High-velocity ballistic bullet projectile for "Scorched".
    /// Features:
    /// - Continuous raycast sweep collision detection (immune to high-speed geometry tunneling).
    /// - Directional ballistic trajectory with realistic micro-gravity curve.
    /// - Renders custom incandescent bullet shader with high-speed tracer trail.
    /// - Spawns directional spark bursts, debris, and point light flash on impact.
    /// - Rigidbody physics impulse delivery on hit surfaces.
    /// </summary>
    [DisallowMultipleComponent]
    public class BulletProjectile : MonoBehaviour
    {
        [Header("Ballistics & Velocity")]
        [Tooltip("Projectile speed in meters per second (7.62x39mm high-speed rifle round).")]
        [SerializeField] private float _speed = 240.0f;

        [Tooltip("Gravity acceleration applied to trajectory.")]
        [SerializeField] private float _gravity = -9.81f;

        [Tooltip("Gravity multiplier (0 for laser flat, 0.25-0.5 for realistic long-range ballistic drop).")]
        [SerializeField] private float _gravityScale = 0.3f;

        [Tooltip("Maximum flight duration before automatic cleanup.")]
        [SerializeField] private float _maxLifetime = 4.0f;

        [Header("Impact & Combat")]
        [Tooltip("Direct damage dealt to hit targets.")]
        [SerializeField] private float _damage = 42.0f;

        [Tooltip("Physical impulse force applied to rigidbodies.")]
        [SerializeField] private float _impactForce = 35.0f;

        [Tooltip("Layer mask for collision detection.")]
        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("Visual Effects")]
        [Tooltip("Prefab for spark burst and impact flash.")]
        [SerializeField] private GameObject _impactEffectPrefab;

        [Tooltip("Optional TrailRenderer reference for custom tracer tail.")]
        [SerializeField] private TrailRenderer _trailRenderer;

        private Vector3 _velocity;
        private Vector3 _currentPosition;
        private float _timeAlive = 0f;
        private Collider _ignoredShooterCollider;
        private bool _hasHit = false;

        public float Speed => _speed;
        public float Damage => _damage;

        /// <summary>
        /// Initializes the projectile with direction and shooter parameters.
        /// </summary>
        public void Initialize(Vector3 direction, Collider shooterCollider = null)
        {
            _ignoredShooterCollider = shooterCollider;
            _velocity = direction.normalized * _speed;
            _currentPosition = transform.position;
            transform.rotation = Quaternion.LookRotation(direction, Vector3.up);

            // Ensure TrailRenderer is configured for blazing tracer trail
            SetupTracerTrail();
        }

        private void Awake()
        {
            _currentPosition = transform.position;
            if (_velocity == Vector3.zero)
            {
                _velocity = transform.forward * _speed;
            }
        }

        private void Start()
        {
            SetupTracerTrail();
        }

        private void Update()
        {
            if (_hasHit) return;

            _timeAlive += Time.deltaTime;
            if (_timeAlive >= _maxLifetime)
            {
                Destroy(gameObject);
                return;
            }

            // Apply slight ballistic gravity drop over distance
            _velocity.y += _gravity * _gravityScale * Time.deltaTime;

            // Continuous raycast sweep from current position to next position
            Vector3 stepMove = _velocity * Time.deltaTime;
            float stepDistance = stepMove.magnitude;
            Vector3 stepDirection = stepMove / Mathf.Max(0.0001f, stepDistance);

            // Raycast forward along this frame's step
            Ray ray = new Ray(_currentPosition, stepDirection);
            if (Physics.Raycast(ray, out RaycastHit hit, stepDistance, _hitMask, QueryTriggerInteraction.Ignore))
            {
                // Check if hit the shooter themselves
                if (_ignoredShooterCollider != null && hit.collider == _ignoredShooterCollider)
                {
                    // Continue flight past shooter's own collider
                    AdvancePosition(stepMove);
                    return;
                }

                HandleImpact(hit);
                return;
            }

            // No obstacle encountered: advance position
            AdvancePosition(stepMove);
        }

        private void AdvancePosition(Vector3 step)
        {
            _currentPosition += step;
            transform.position = _currentPosition;

            if (_velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
            }
        }

        private void HandleImpact(RaycastHit hit)
        {
            _hasHit = true;
            transform.position = hit.point;

            // 1. Physical impulse on rigidbodies
            if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
            {
                hit.rigidbody.AddForceAtPosition(_velocity.normalized * _impactForce, hit.point, ForceMode.Impulse);
            }

            // 2. Spawn spark and flash impact effect and bullet mark
            if (BulletVisualPoolManager.Instance != null)
            {
                BulletVisualPoolManager.Instance.SpawnImpact(hit.point, hit.normal, hit.collider);
            }
            else
            {
                SpawnImpactEffect(hit.point, hit.normal);
            }

            // 3. Detach trail renderer if present so it fades naturally instead of abruptly vanishing
            if (_trailRenderer != null)
            {
                _trailRenderer.transform.SetParent(null, true);
                Destroy(_trailRenderer.gameObject, _trailRenderer.time + 0.1f);
            }

            // 4. Destroy projectile
            Destroy(gameObject);
        }

        private void SpawnImpactEffect(Vector3 position, Vector3 normal)
        {
            if (_impactEffectPrefab != null)
            {
                Instantiate(_impactEffectPrefab, position + normal * 0.02f, Quaternion.LookRotation(normal));
            }
            else
            {
                // Procedural fallback spark burst
                var impactGo = new GameObject("BulletImpact_Auto");
                impactGo.transform.position = position + normal * 0.02f;
                impactGo.transform.rotation = Quaternion.LookRotation(normal);
                impactGo.AddComponent<BulletImpactEffect>();
            }
        }

        private void SetupTracerTrail()
        {
            if (_trailRenderer != null) return;

            _trailRenderer = GetComponentInChildren<TrailRenderer>();
            if (_trailRenderer == null)
            {
                var trailGo = new GameObject("TracerTrail");
                trailGo.transform.SetParent(transform, false);
                trailGo.transform.localPosition = new Vector3(0f, 0f, -0.1f);

                _trailRenderer = trailGo.AddComponent<TrailRenderer>();
                _trailRenderer.time = 0.07f; // High-speed flash trail
                _trailRenderer.startWidth = 0.035f;
                _trailRenderer.endWidth = 0.0f;
                _trailRenderer.minVertexDistance = 0.05f;
                _trailRenderer.autodestruct = false;

                // Attempt to link our custom bullet material
                var mat = Resources.Load<Material>("Materials/M_BulletTracer");
                if (mat == null)
                {
                    mat = GetComponent<MeshRenderer>()?.sharedMaterial;
                }
                if (mat != null)
                {
                    _trailRenderer.sharedMaterial = mat;
                }

                // Vibrant tracer trail color gradient
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(1f, 0.98f, 0.85f), 0f),
                        new GradientColorKey(new Color(1f, 0.45f, 0.05f), 0.6f),
                        new GradientColorKey(new Color(0.8f, 0.2f, 0.0f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.8f, 0.4f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                _trailRenderer.colorGradient = grad;
            }
        }
    }
}
