using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Scorched.Survival;

namespace Scorched.Environment
{
    /// <summary>
    /// Synchronizes the Volumetric Skybox (raymarched clouds), Volumetric Height Fog + Sun Shafts pass,
    /// camera DepthTexture generation, and dynamic Heatwave wind/scattering surge responses.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ScorchedAtmosphereController : MonoBehaviour
    {
        private static readonly int IdCloudWindSpeed = Shader.PropertyToID("_CloudWindSpeed");
        private static readonly int IdCloudWindPhase = Shader.PropertyToID("_CloudWindPhase");
        private static readonly int IdHeatwaveSurge = Shader.PropertyToID("_HeatwaveSurge");

        private static readonly int IdWindOffset = Shader.PropertyToID("_WindOffset");
        private static readonly int IdFogDensity = Shader.PropertyToID("_FogDensity");
        private static readonly int IdFogMaxOpacity = Shader.PropertyToID("_FogMaxOpacity");
        private static readonly int IdGodRayIntensity = Shader.PropertyToID("_GodRayIntensity");

        [Header("Material & Shader References (Build-Safe)")]
        [SerializeField] private Material _skyboxMaterial;
        [SerializeField] private Shader _skyboxShader;
        [SerializeField] private Material _atmosphereMaterial;
        [SerializeField] private Shader _atmosphereShader;

        [Header("Base Atmospheric Tuning")]
        [SerializeField] private float _baseCloudWindSpeed = 18.0f;
        [SerializeField] private float _heatwaveWindMultiplier = 2.5f;

        [Space(4)]
        [SerializeField] private float _baseGodRayIntensity = 0.55f;
        [SerializeField] private float _heatwaveGodRayIntensity = 0.85f;

        [Space(4)]
        [SerializeField] private float _baseFogDensity = 0.0085f;
        [SerializeField] private float _heatwaveFogDensity = 0.016f;
        [SerializeField] private float _baseFogMaxOpacity = 0.38f;
        [SerializeField] private float _heatwaveFogMaxOpacity = 0.48f;

        [Header("Dust Fog Wind Speed")]
        [SerializeField] private Vector3 _fogWindVelocity = new Vector3(0.18f, 0.03f, 0.09f);

        private float _cloudPhase = 120f;
        private Vector3 _fogWindAccum;
        private float _surgeBlend;
        private float _cameraCheckTimer;

        private void OnEnable()
        {
            ApplySkyboxAssignment();
            EnsureCamerasGenerateDepth();
            UpdateShaderUniforms(0f);
        }

        private void Update()
        {
            float dt = Application.isPlaying ? Time.deltaTime : 0.016f;

            if (Application.isPlaying)
            {
                _cameraCheckTimer -= dt;
                if (_cameraCheckTimer <= 0f)
                {
                    _cameraCheckTimer = 3.0f;
                    EnsureCamerasGenerateDepth();
                }
            }

            UpdateShaderUniforms(dt);
        }

        private void ApplySkyboxAssignment()
        {
            if (_skyboxMaterial != null && RenderSettings.skybox != _skyboxMaterial)
            {
                RenderSettings.skybox = _skyboxMaterial;
            }
        }

        public void EnsureCamerasGenerateDepth()
        {
            foreach (var cam in FindObjectsByType<Camera>())
            {
                if (cam == null) continue;
                cam.depthTextureMode |= DepthTextureMode.Depth;

                var uacd = cam.GetComponent<UniversalAdditionalCameraData>()
                           ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                if (uacd.requiresDepthOption != CameraOverrideOption.On)
                {
                    uacd.requiresDepthOption = CameraOverrideOption.On;
                }
            }
        }

        private void UpdateShaderUniforms(float dt)
        {
            bool heatwaveActive = Application.isPlaying
                                  && HeatwaveHazardManager.Instance != null
                                  && HeatwaveHazardManager.Instance.IsHeatwaveSurgeActive;

            float targetSurge = heatwaveActive ? 1.0f : 0.0f;
            _surgeBlend = Application.isPlaying
                ? Mathf.MoveTowards(_surgeBlend, targetSurge, dt * 1.4f)
                : 0.0f;

            float windMul = Mathf.Lerp(1.0f, _heatwaveWindMultiplier, _surgeBlend);

            if (Application.isPlaying && dt > 0f)
            {
                _cloudPhase += dt * _baseCloudWindSpeed * windMul;
                _fogWindAccum += _fogWindVelocity * (dt * windMul);
            }
            else if (!Application.isPlaying)
            {
                _cloudPhase = 120f;
                _fogWindAccum = Vector3.zero;
            }

            if (_skyboxMaterial != null)
            {
                _skyboxMaterial.SetFloat(IdCloudWindSpeed, _baseCloudWindSpeed);
                _skyboxMaterial.SetFloat(IdCloudWindPhase, _cloudPhase);
                _skyboxMaterial.SetFloat(IdHeatwaveSurge, _surgeBlend);
            }

            if (_atmosphereMaterial != null)
            {
                _atmosphereMaterial.SetFloat(IdGodRayIntensity, Mathf.Lerp(_baseGodRayIntensity, _heatwaveGodRayIntensity, _surgeBlend));
                _atmosphereMaterial.SetFloat(IdFogDensity, Mathf.Lerp(_baseFogDensity, _heatwaveFogDensity, _surgeBlend));
                _atmosphereMaterial.SetFloat(IdFogMaxOpacity, Mathf.Lerp(_baseFogMaxOpacity, _heatwaveFogMaxOpacity, _surgeBlend));
                _atmosphereMaterial.SetVector(IdWindOffset, new Vector4(_fogWindAccum.x, _fogWindAccum.y, _fogWindAccum.z, 0f));
                _atmosphereMaterial.SetFloat(IdHeatwaveSurge, _surgeBlend);
            }
        }

        public void SetReferences(Material skyMat, Shader skyShader, Material atmoMat, Shader atmoShader)
        {
            _skyboxMaterial = skyMat;
            _skyboxShader = skyShader;
            _atmosphereMaterial = atmoMat;
            _atmosphereShader = atmoShader;
            ApplySkyboxAssignment();
            EnsureCamerasGenerateDepth();
            UpdateShaderUniforms(0f);
        }
    }
}

