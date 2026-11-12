using UnityEngine;
using Scorched.Survival;

namespace Scorched.Environment
{
    /// <summary>
    /// Spawns and manages 3D airborne desert dust plumes and sunlit motes around the active camera.
    /// Dynamically scales wind velocity, particle emission, and dust turbulence when HeatwaveHazardManager surges.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedAtmosphericParticles : MonoBehaviour
    {
        public static ScorchedAtmosphericParticles Instance { get; private set; }

        [Header("Particle System References")]
        [SerializeField] private ParticleSystem _groundWispsSystem;
        [SerializeField] private ParticleSystem _airborneMotesSystem;

        [Header("Base Wind Velocity (m/s)")]
        [SerializeField] private Vector3 _baseWindVelocity = new Vector3(4.5f, 0.2f, 2.0f);
        [SerializeField] private float _heatwaveSpeedMultiplier = 2.4f;
        [SerializeField] private float _heatwaveEmissionMultiplier = 2.2f;

        [Header("Follow Volume")]
        [SerializeField] private Vector3 _followOffset = new Vector3(0f, 2.5f, 0f);
        [SerializeField] private float _lerpSpeed = 4.0f;

        private Transform _targetCameraTransform;
        private float _currentHeatwaveBlend = 0f;

        private float _baseWispRate = 18f;
        private float _baseMotesRate = 45f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (_groundWispsSystem != null)
            {
                _baseWispRate = _groundWispsSystem.emission.rateOverTime.constant;
            }
            if (_airborneMotesSystem != null)
            {
                _baseMotesRate = _airborneMotesSystem.emission.rateOverTime.constant;
            }
        }

        private void LateUpdate()
        {
            UpdateTargetCamera();

            if (_targetCameraTransform != null)
            {
                Vector3 targetPos = _targetCameraTransform.position + _followOffset;
                transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * _lerpSpeed);
            }

            UpdateHeatwaveAtmosphereResponse(Time.deltaTime);
        }

        private void UpdateTargetCamera()
        {
            if (_targetCameraTransform != null && _targetCameraTransform.gameObject.activeInHierarchy)
            {
                return;
            }

            Camera cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled)
            {
                _targetCameraTransform = cam.transform;
                return;
            }

            foreach (var c in Camera.allCameras)
            {
                if (c != null && c.isActiveAndEnabled && c.cameraType == CameraType.Game)
                {
                    _targetCameraTransform = c.transform;
                    return;
                }
            }
        }

        private void UpdateHeatwaveAtmosphereResponse(float dt)
        {
            bool isHeatwave = HeatwaveHazardManager.Instance != null && HeatwaveHazardManager.Instance.IsHeatwaveSurgeActive;
            float targetBlend = isHeatwave ? 1f : 0f;
            _currentHeatwaveBlend = Mathf.MoveTowards(_currentHeatwaveBlend, targetBlend, dt * 1.5f);

            float speedMul = Mathf.Lerp(1.0f, _heatwaveSpeedMultiplier, _currentHeatwaveBlend);
            float emissionMul = Mathf.Lerp(1.0f, _heatwaveEmissionMultiplier, _currentHeatwaveBlend);

            if (_groundWispsSystem != null)
            {
                var vol = _groundWispsSystem.velocityOverLifetime;
                vol.x = new ParticleSystem.MinMaxCurve(_baseWindVelocity.x * speedMul * 0.75f, _baseWindVelocity.x * speedMul * 1.25f);
                vol.y = new ParticleSystem.MinMaxCurve(-0.1f, 0.4f * speedMul);
                vol.z = new ParticleSystem.MinMaxCurve(_baseWindVelocity.z * speedMul * 0.75f, _baseWindVelocity.z * speedMul * 1.25f);

                var em = _groundWispsSystem.emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(_baseWispRate * emissionMul);
            }

            if (_airborneMotesSystem != null)
            {
                var vol = _airborneMotesSystem.velocityOverLifetime;
                vol.x = new ParticleSystem.MinMaxCurve(_baseWindVelocity.x * speedMul * 0.4f, _baseWindVelocity.x * speedMul * 0.8f);
                vol.y = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
                vol.z = new ParticleSystem.MinMaxCurve(_baseWindVelocity.z * speedMul * 0.4f, _baseWindVelocity.z * speedMul * 0.8f);

                var em = _airborneMotesSystem.emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(_baseMotesRate * emissionMul);
            }
        }

        public void AssignSystems(ParticleSystem wisps, ParticleSystem motes)
        {
            _groundWispsSystem = wisps;
            _airborneMotesSystem = motes;
            if (wisps != null) _baseWispRate = wisps.emission.rateOverTime.constant;
            if (motes != null) _baseMotesRate = motes.emission.rateOverTime.constant;
        }
    }
}

