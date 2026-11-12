using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Scorched.Player;

namespace Scorched.Survival
{
    /// <summary>
    /// 3-Stage Super El Nino Heatstroke VFX Controller.
    /// Reads the local player's normalized HeatExposure (0..1) from PlayerThermalController
    /// and drives both the M_SuperElNinoHeatstroke fullscreen HLSL material and the
    /// SuperElNino Global Volume (ChromaticAberration & LensDistortion in Stage 3).
    /// Supports live Inspector preview via TestExposureOverride in Edit and Play modes.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class SuperElNinoVFXController : MonoBehaviour
    {
        private static readonly int IdExposureNormalized = Shader.PropertyToID("_ExposureNormalized");
        private static readonly int IdAmbientHeatHaze = Shader.PropertyToID("_AmbientHeatHaze");
        private static readonly int IdVignetteIntensity = Shader.PropertyToID("_VignetteIntensity");
        private static readonly int IdVignetteRadius = Shader.PropertyToID("_VignetteRadius");
        private static readonly int IdVignetteColor = Shader.PropertyToID("_VignetteColor");
        private static readonly int IdHeatHazeStrength = Shader.PropertyToID("_HeatHazeStrength");
        private static readonly int IdHeatHazeSpeed = Shader.PropertyToID("_HeatHazeSpeed");
        private static readonly int IdHeatHazeFrequency = Shader.PropertyToID("_HeatHazeFrequency");
        private static readonly int IdDoubleVisionIntensity = Shader.PropertyToID("_DoubleVisionIntensity");
        private static readonly int IdDoubleVisionSpeed = Shader.PropertyToID("_DoubleVisionSpeed");

        [Header("Shader & Volume References")]
        [SerializeField] private Material _heatstrokeMaterial;
        [SerializeField] private Shader _heatstrokeShader;
        [SerializeField] private Volume _globalVolume;

        [Header("Heatstroke Vignette Styling")]
        [SerializeField] private Color _vignetteColor = new Color(0.7215686f, 0.07843138f, 0.03137255f, 1f); // #B81408
        [SerializeField] private float _heatHazeFrequency = 12.0f;
        [SerializeField] private float _responseLerpSpeed = 5.0f;

        [Header("Inspector Debug Preview (0% -> 100%)")]
        [Tooltip("Scrub from 0 to 100 in Edit Mode (or > 0 in Play Mode) to preview Stage 1 (25%), Stage 2 (55%), and Stage 3 (90%) heatstroke effects.")]
        [Range(0f, 100f)]
        public float TestExposureOverride = 0f;

        private PlayerThermalController _localThermal;
        private ChromaticAberration _chromaticAberration;
        private LensDistortion _lensDistortion;
        private VolumeProfile _cachedProfile;

        private float _smoothedExposure01;
        private float _smoothedAmbientHaze;
        private float _smoothedVignetteIntensity;
        private float _smoothedVignetteRadius = 0.55f;
        private float _smoothedHazeStrength;
        private float _smoothedHazeSpeed = 2.8f;
        private float _smoothedDoubleVisionIntensity;
        private float _smoothedDoubleVisionSpeed = 3.5f;
        private float _smoothedChromaticAberration;
        private float _smoothedLensDistortion;

        private float _cameraCheckTimer;
        private float _playerSearchTimer;

        private void Awake()
        {
            if (Application.isPlaying)
            {
                // Always start matches/builds free of stale Edit-mode preview values.
                TestExposureOverride = 0f;
                _smoothedExposure01 = 0f;
                _smoothedAmbientHaze = 0f;
                _smoothedVignetteIntensity = 0f;
                _smoothedHazeStrength = 0f;
                _smoothedDoubleVisionIntensity = 0f;
                _smoothedChromaticAberration = 0f;
                _smoothedLensDistortion = 0f;
                ResolveReferences();
                ResetVisualsToSafeState();
            }
        }

        private void OnEnable()
        {
            ResolveReferences();
            EnsureActiveCamerasPostProcessing();
            ApplyImmediatelyFromCurrentExposure();
        }

        private void OnValidate()
        {
            ResolveReferences();
            if (!Application.isPlaying)
            {
                ApplyImmediatelyFromCurrentExposure();
            }
        }

        private void OnDisable()
        {
            ResetVisualsToSafeState();
        }

        private void Update()
        {
            ResolveReferences();

            if (Application.isPlaying)
            {
                _cameraCheckTimer -= Time.deltaTime;
                if (_cameraCheckTimer <= 0f)
                {
                    _cameraCheckTimer = 3.0f;
                    EnsureActiveCamerasPostProcessing();
                }
            }

            float targetExposure01 = SampleTargetExposureNormalized(out float targetAmbientHaze);
            float t = Application.isPlaying
                ? 1f - Mathf.Exp(-_responseLerpSpeed * Time.unscaledDeltaTime)
                : 1f;

            UpdateAndApplyVFX(targetExposure01, targetAmbientHaze, t);
        }

        private void ResolveReferences()
        {
            if (_globalVolume == null)
            {
                _globalVolume = GetComponent<Volume>();
            }

            VolumeProfile activeProfile = _globalVolume != null ? _globalVolume.sharedProfile : null;
            if (activeProfile != null && activeProfile != _cachedProfile)
            {
                _cachedProfile = activeProfile;
                if (!activeProfile.TryGet(out _chromaticAberration))
                {
                    _chromaticAberration = activeProfile.Add<ChromaticAberration>(false);
                }
                if (!activeProfile.TryGet(out _lensDistortion))
                {
                    _lensDistortion = activeProfile.Add<LensDistortion>(false);
                }
            }
        }

        private void EnsureActiveCamerasPostProcessing()
        {
            foreach (var cam in FindObjectsByType<Camera>())
            {
                if (cam == null || cam.cameraType != CameraType.Game) continue;
                var data = cam.GetComponent<UniversalAdditionalCameraData>()
                           ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                if (!data.renderPostProcessing)
                {
                    data.renderPostProcessing = true;
                }
                if (_globalVolume != null && (data.volumeLayerMask & (1 << _globalVolume.gameObject.layer)) == 0)
                {
                    data.volumeLayerMask |= 1 << _globalVolume.gameObject.layer;
                }
            }
        }

        private float SampleTargetExposureNormalized(out float targetAmbientHaze)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying || TestExposureOverride > 0.01f)
            {
                float e = Mathf.Clamp01(TestExposureOverride / 100f);
                targetAmbientHaze = e >= 0.10f ? Mathf.Clamp01(e * 0.5f) : 0f;
                return e;
            }
#else
            if (!Application.isPlaying)
            {
                targetAmbientHaze = 0f;
                return 0f;
            }
#endif

            ResolveLocalPlayerThermal();
            float liveE = _localThermal != null ? Mathf.Clamp01(_localThermal.HeatExposure / 100f) : 0f;

            HeatwaveHazardManager hazard = HeatwaveHazardManager.Instance;
            bool outdoorSurge = hazard != null
                                && hazard.IsHeatwaveSurgeActive
                                && (_localThermal == null || !_localThermal.IsInShade);
            targetAmbientHaze = outdoorSurge ? 0.12f : 0f;

            return liveE;
        }

        private void ResolveLocalPlayerThermal()
        {
            if (_localThermal != null && _localThermal.gameObject.activeInHierarchy)
            {
                if (!_localThermal.IsSpawned || _localThermal.IsOwner)
                {
                    return;
                }
            }

            _localThermal = null;
            _playerSearchTimer -= Time.unscaledDeltaTime;
            if (_playerSearchTimer > 0f) return;
            _playerSearchTimer = 1.0f;

            var thermals = FindObjectsByType<PlayerThermalController>(FindObjectsSortMode.None);
            for (int i = 0; i < thermals.Length; i++)
            {
                var tc = thermals[i];
                if (tc != null && tc.gameObject.activeInHierarchy && (!tc.IsSpawned || tc.IsOwner))
                {
                    _localThermal = tc;
                    return;
                }
            }
        }

        private void ApplyImmediatelyFromCurrentExposure()
        {
            float e = SampleTargetExposureNormalized(out float ambient);
            UpdateAndApplyVFX(e, ambient, 1f);
        }

        private void UpdateAndApplyVFX(float e, float ambientHaze, float blend)
        {
            ComputeStageTargets(
                e,
                out float targetVigIntensity,
                out float targetVigRadius,
                out float targetHazeStrength,
                out float targetHazeSpeed,
                out float targetDoubleVision,
                out float targetDoubleVisionSpeed,
                out float targetChromaticAberration,
                out float targetLensDistortion);

            _smoothedExposure01 = Mathf.Lerp(_smoothedExposure01, e, blend);
            _smoothedAmbientHaze = Mathf.Lerp(_smoothedAmbientHaze, ambientHaze, blend);
            _smoothedVignetteIntensity = Mathf.Lerp(_smoothedVignetteIntensity, targetVigIntensity, blend);
            _smoothedVignetteRadius = Mathf.Lerp(_smoothedVignetteRadius, targetVigRadius, blend);
            _smoothedHazeStrength = Mathf.Lerp(_smoothedHazeStrength, targetHazeStrength, blend);
            _smoothedHazeSpeed = Mathf.Lerp(_smoothedHazeSpeed, targetHazeSpeed, blend);
            _smoothedDoubleVisionIntensity = Mathf.Lerp(_smoothedDoubleVisionIntensity, targetDoubleVision, blend);
            _smoothedDoubleVisionSpeed = Mathf.Lerp(_smoothedDoubleVisionSpeed, targetDoubleVisionSpeed, blend);
            _smoothedChromaticAberration = Mathf.Lerp(_smoothedChromaticAberration, targetChromaticAberration, blend);
            _smoothedLensDistortion = Mathf.Lerp(_smoothedLensDistortion, targetLensDistortion, blend);

            if (_heatstrokeMaterial != null)
            {
                _heatstrokeMaterial.SetFloat(IdExposureNormalized, _smoothedExposure01);
                _heatstrokeMaterial.SetFloat(IdAmbientHeatHaze, _smoothedAmbientHaze);
                _heatstrokeMaterial.SetFloat(IdVignetteIntensity, _smoothedVignetteIntensity);
                _heatstrokeMaterial.SetFloat(IdVignetteRadius, _smoothedVignetteRadius);
                _heatstrokeMaterial.SetColor(IdVignetteColor, _vignetteColor);
                _heatstrokeMaterial.SetFloat(IdHeatHazeStrength, _smoothedHazeStrength);
                _heatstrokeMaterial.SetFloat(IdHeatHazeSpeed, _smoothedHazeSpeed);
                _heatstrokeMaterial.SetFloat(IdHeatHazeFrequency, _heatHazeFrequency);
                _heatstrokeMaterial.SetFloat(IdDoubleVisionIntensity, _smoothedDoubleVisionIntensity);
                _heatstrokeMaterial.SetFloat(IdDoubleVisionSpeed, _smoothedDoubleVisionSpeed);
            }

            if (_chromaticAberration != null)
            {
                _chromaticAberration.intensity.overrideState = true;
                _chromaticAberration.intensity.value = _smoothedChromaticAberration;
            }

            if (_lensDistortion != null)
            {
                _lensDistortion.intensity.overrideState = true;
                _lensDistortion.intensity.value = _smoothedLensDistortion;
            }
        }

        private static void ComputeStageTargets(
            float e,
            out float vigIntensity,
            out float vigRadius,
            out float hazeStrength,
            out float hazeSpeed,
            out float doubleVisionIntensity,
            out float doubleVisionSpeed,
            out float chromaticAberration,
            out float lensDistortion)
        {
            doubleVisionSpeed = 3.5f;

            if (e < 0.10f)
            {
                // Safe / Cool
                vigIntensity = 0f;
                vigRadius = 0.55f;
                hazeStrength = 0f;
                hazeSpeed = 2.8f;
                doubleVisionIntensity = 0f;
                chromaticAberration = 0f;
                lensDistortion = 0f;
            }
            else if (e < 0.40f)
            {
                // Stage 1: Early Exposure — Subtle outer amber-crimson warning vignette
                float t = Mathf.InverseLerp(0.10f, 0.40f, e);
                vigIntensity = Mathf.Lerp(0.0f, 0.18f, t);
                vigRadius = Mathf.Lerp(0.65f, 0.55f, t);
                hazeStrength = 0f;
                hazeSpeed = 2.0f;
                doubleVisionIntensity = 0f;
                chromaticAberration = 0f;
                lensDistortion = 0f;
            }
            else if (e < 0.75f)
            {
                // Stage 2: Mid Exposure — Soft peripheral vignette + subtle realistic heat shimmer
                float t = Mathf.InverseLerp(0.40f, 0.75f, e);
                vigIntensity = Mathf.Lerp(0.18f, 0.32f, t);
                vigRadius = Mathf.Lerp(0.55f, 0.46f, t);
                hazeStrength = Mathf.Lerp(0.0f, 0.0012f, t);
                hazeSpeed = 2.4f;
                doubleVisionIntensity = 0f;
                chromaticAberration = Mathf.Lerp(0f, 0.12f, t);
                lensDistortion = 0f;
            }
            else
            {
                // Stage 3: Critical Exposure — Clear center vision, peripheral alert vignette, gentle diplopia & chromatic fringe
                float t = Mathf.InverseLerp(0.75f, 1.0f, e);
                vigIntensity = Mathf.Lerp(0.32f, 0.46f, t);
                vigRadius = Mathf.Lerp(0.46f, 0.40f, t);
                hazeStrength = Mathf.Lerp(0.0012f, 0.0025f, t);
                hazeSpeed = Mathf.Lerp(2.4f, 3.2f, t);
                doubleVisionIntensity = Mathf.Lerp(0.0f, 0.0035f, t);

                float wobble = Application.isPlaying ? (0.90f + 0.10f * Mathf.Sin(Time.unscaledTime * 2.2f)) : 1f;
                chromaticAberration = Mathf.Lerp(0.12f, 0.28f, t) * wobble;
                lensDistortion = Mathf.Lerp(0f, -0.04f, t) * wobble;
            }
        }

        private void ResetVisualsToSafeState()
        {
            if (_heatstrokeMaterial != null)
            {
                _heatstrokeMaterial.SetFloat(IdExposureNormalized, 0f);
                _heatstrokeMaterial.SetFloat(IdAmbientHeatHaze, 0f);
                _heatstrokeMaterial.SetFloat(IdVignetteIntensity, 0f);
                _heatstrokeMaterial.SetFloat(IdVignetteRadius, 0.55f);
                _heatstrokeMaterial.SetColor(IdVignetteColor, _vignetteColor);
                _heatstrokeMaterial.SetFloat(IdHeatHazeStrength, 0f);
                _heatstrokeMaterial.SetFloat(IdHeatHazeSpeed, 2.8f);
                _heatstrokeMaterial.SetFloat(IdHeatHazeFrequency, _heatHazeFrequency);
                _heatstrokeMaterial.SetFloat(IdDoubleVisionIntensity, 0f);
                _heatstrokeMaterial.SetFloat(IdDoubleVisionSpeed, 3.5f);
            }

            if (_chromaticAberration != null)
            {
                _chromaticAberration.intensity.overrideState = true;
                _chromaticAberration.intensity.value = 0f;
            }

            if (_lensDistortion != null)
            {
                _lensDistortion.intensity.overrideState = true;
                _lensDistortion.intensity.value = 0f;
            }
        }
    }
}

