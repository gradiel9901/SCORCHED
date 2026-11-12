using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Scorched.Player;

namespace Scorched.UI
{
    /// <summary>
    /// Renders up to 4 directional orange footstep indicator arcs around the center crosshair
    /// whenever nearby enemy or remote players move within acoustic detection radii.
    /// </summary>
    [DisallowMultipleComponent]
    public class FootstepRadarUI : MonoBehaviour
    {
        #region Serialized Configuration
        [Header("Radar Assets & Layout")]
        [SerializeField] private RectTransform _radarRingRect;
        [SerializeField] private Sprite _footstepArcSprite;

        [Header("Acoustic Detection Ranges (Meters)")]
        [SerializeField] private int _poolSize = 4;
        [SerializeField] private float _sprintRange = 25f;
        [SerializeField] private float _walkRange = 14f;
        [SerializeField] private float _crouchCrawlRange = 5f;
        [SerializeField] private float _minMoveSpeedSq = 0.2f;

        [Header("Visual Tuning")]
        [SerializeField] private float _minAlpha = 0.25f;
        [SerializeField] private float _maxAlpha = 0.95f;
        [SerializeField] private float _pulseSpeed = 9f;
        [SerializeField] private float _pulseScaleAmount = 0.055f;
        #endregion

        #region Runtime State
        private struct ThreatSample
        {
            public float Distance;
            public float Proximity01;
            public float SignedAngleDeg;
            public bool IsSprinting;
        }

        private readonly List<Image> _arcPool = new List<Image>(4);
        private readonly List<ThreatSample> _threats = new List<ThreatSample>(8);
        private PlayerMovement _localPlayer;
        private Camera _cachedCamera;
        #endregion

        #region Initialization
        public void Configure(RectTransform radarRingRect, Sprite footstepArcSprite)
        {
            if (radarRingRect != null)
            {
                _radarRingRect = radarRingRect;
            }
            if (footstepArcSprite != null)
            {
                _footstepArcSprite = footstepArcSprite;
            }
            RebuildPoolIfNeeded();
        }

        private void Awake()
        {
            if (_radarRingRect == null)
            {
                _radarRingRect = GetComponent<RectTransform>();
            }
            RebuildPoolIfNeeded();
        }

        private void RebuildPoolIfNeeded()
        {
            if (_radarRingRect == null)
            {
                return;
            }

            if (_arcPool.Count == _poolSize)
            {
                for (int i = 0; i < _arcPool.Count; i++)
                {
                    if (_arcPool[i] != null && _footstepArcSprite != null && _arcPool[i].sprite != _footstepArcSprite)
                    {
                        _arcPool[i].sprite = _footstepArcSprite;
                    }
                }
                return;
            }

            for (int i = _arcPool.Count - 1; i >= 0; i--)
            {
                if (_arcPool[i] != null)
                {
                    Destroy(_arcPool[i].gameObject);
                }
            }
            _arcPool.Clear();

            for (int i = 0; i < _poolSize; i++)
            {
                var arcGo = new GameObject($"FootstepArc_{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                arcGo.transform.SetParent(_radarRingRect, false);

                var rect = arcGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = _radarRingRect.sizeDelta;

                var img = arcGo.GetComponent<Image>();
                img.sprite = _footstepArcSprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                img.color = Color.white;

                arcGo.SetActive(false);
                _arcPool.Add(img);
            }
        }
        #endregion

        #region Per-Frame Acoustic Scan
        private void LateUpdate()
        {
            if (_radarRingRect == null || _footstepArcSprite == null)
            {
                return;
            }

            RebuildPoolIfNeeded();

            if (!TryResolveLocalReference(out Vector3 localFlatPos, out Vector3 forwardFlat, out PlayerHealthController localHealth))
            {
                HideAllArcs();
                return;
            }

            _threats.Clear();
            var activePlayers = PlayerHealthController.ActiveInstances;
            for (int i = 0; i < activePlayers.Count; i++)
            {
                PlayerHealthController candidate = activePlayers[i];
                if (candidate == null || candidate == localHealth || candidate.IsDead)
                {
                    continue;
                }

                // Never trigger radar from local owner or disabled scene duplicate
                if (!candidate.IsSpawned || candidate.IsOwner || !candidate.gameObject.activeInHierarchy)
                {
                    continue;
                }

                PlayerMovement pm = candidate.GetComponent<PlayerMovement>();
                if (pm == null)
                {
                    continue;
                }

                Vector3 vel = pm.WorldVelocity;
                vel.y = 0f;
                bool isCrawling = candidate.LifeState == PlayerLifeState.Downed && candidate.DownedMoving;
                if (vel.sqrMagnitude <= _minMoveSpeedSq && !isCrawling)
                {
                    continue;
                }

                float detectionRange = DetermineDetectionRange(candidate, pm);
                Vector3 candidatePos = candidate.transform.position;
                Vector3 delta = new Vector3(candidatePos.x - localFlatPos.x, 0f, candidatePos.z - localFlatPos.z);
                float dist = delta.magnitude;

                if (dist > detectionRange || dist < 0.1f)
                {
                    continue;
                }

                float angle = Vector3.SignedAngle(forwardFlat, delta / dist, Vector3.up);
                float proximity = 1f - Mathf.Clamp01(dist / detectionRange);

                _threats.Add(new ThreatSample
                {
                    Distance = dist,
                    Proximity01 = proximity,
                    SignedAngleDeg = angle,
                    IsSprinting = pm.IsSprinting
                });
            }

            if (_threats.Count > 1)
            {
                _threats.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            }

            int activeCount = Mathf.Min(_poolSize, _threats.Count);
            for (int i = 0; i < _poolSize; i++)
            {
                Image arc = _arcPool[i];
                if (arc == null)
                {
                    continue;
                }

                if (i >= activeCount)
                {
                    if (arc.gameObject.activeSelf)
                    {
                        arc.gameObject.SetActive(false);
                    }
                    continue;
                }

                if (!arc.gameObject.activeSelf)
                {
                    arc.gameObject.SetActive(true);
                }

                ThreatSample sample = _threats[i];
                RectTransform arcRect = arc.rectTransform;
                arcRect.localEulerAngles = new Vector3(0f, 0f, -sample.SignedAngleDeg);

                float pulseWave = 0.5f + 0.5f * Mathf.Sin(Time.time * _pulseSpeed + i * 1.1f);
                float pulseAmount = sample.Proximity01 * (sample.IsSprinting ? 1f : 0.45f);
                float scale = 1f + (_pulseScaleAmount * pulseAmount * pulseWave);
                arcRect.localScale = new Vector3(scale, scale, 1f);

                float alpha = Mathf.Lerp(_minAlpha, _maxAlpha, sample.Proximity01);
                if (sample.IsSprinting && sample.Proximity01 > 0.35f)
                {
                    alpha = Mathf.Clamp(alpha + (pulseWave * 0.08f), _minAlpha, 1f);
                }

                arc.color = new Color(1f, 1f, 1f, alpha);
            }
        }

        private float DetermineDetectionRange(PlayerHealthController health, PlayerMovement pm)
        {
            if (health.LifeState == PlayerLifeState.Downed || pm.IsCrouching)
            {
                return _crouchCrawlRange;
            }

            if (pm.IsSprinting || pm.CurrentLocomotion == LocomotionState.Running)
            {
                return _sprintRange;
            }

            return _walkRange;
        }

        private bool TryResolveLocalReference(out Vector3 flatPos, out Vector3 flatForward, out PlayerHealthController localHealth)
        {
            flatPos = Vector3.zero;
            flatForward = Vector3.forward;
            localHealth = null;

            if (_localPlayer == null || !_localPlayer.gameObject.activeInHierarchy)
            {
                _localPlayer = null;
                var all = PlayerHealthController.ActiveInstances;
                for (int i = 0; i < all.Count; i++)
                {
                    PlayerHealthController hc = all[i];
                    if (hc != null && hc.gameObject.activeInHierarchy && (!hc.IsSpawned || hc.IsOwner))
                    {
                        _localPlayer = hc.GetComponent<PlayerMovement>();
                        localHealth = hc;
                        break;
                    }
                }
            }
            else
            {
                localHealth = _localPlayer.GetComponent<PlayerHealthController>();
            }

            if (_localPlayer == null || (localHealth != null && localHealth.IsDead))
            {
                return false;
            }

            flatPos = _localPlayer.transform.position;
            flatPos.y = 0f;

            if (_cachedCamera == null || !_cachedCamera.isActiveAndEnabled)
            {
                _cachedCamera = Camera.main;
            }

            Vector3 fwd = _cachedCamera != null ? _cachedCamera.transform.forward : _localPlayer.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f)
            {
                fwd = _localPlayer.transform.forward;
                fwd.y = 0f;
            }
            flatForward = fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
            return true;
        }

        private void HideAllArcs()
        {
            for (int i = 0; i < _arcPool.Count; i++)
            {
                if (_arcPool[i] != null && _arcPool[i].gameObject.activeSelf)
                {
                    _arcPool[i].gameObject.SetActive(false);
                }
            }
        }
        #endregion
    }
}
