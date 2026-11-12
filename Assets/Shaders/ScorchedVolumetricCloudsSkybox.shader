Shader "Scorched/Sky/ScorchedVolumetricCloudsSkybox"
{
    Properties
    {
        [Header(Rayleigh and Mie Desert Sky)]
        _ZenithColor ("Zenith Color", Color) = (0.1686, 0.2275, 0.2902, 1)
        _MidSkyColor ("Mid Sky Color", Color) = (0.56, 0.54, 0.49, 1)
        _HorizonColor ("Horizon Dust Color", Color) = (0.9098, 0.7216, 0.4706, 1)
        _GroundColor ("Ground Bounce Color", Color) = (0.48, 0.36, 0.24, 1)

        [Header(Sun and Mie Scattering)]
        _SunColor ("Sun Disk Color", Color) = (1.0, 0.94, 0.80, 1)
        _SunSize ("Sun Angular Radius", Range(0.005, 0.08)) = 0.022
        _SunIntensity ("Sun Disk Intensity", Range(1.0, 20.0)) = 8.0
        _MieIntensity ("Mie Glow Intensity", Range(0.0, 5.0)) = 1.6
        _MieG ("Henyey-Greenstein G", Range(0.1, 0.95)) = 0.76

        [Header(Volumetric Clouds)]
        _CloudNoiseTex ("Cloud Weather & Billow Noise", 2D) = "gray" {}
        _CloudCoverage ("Cloud Coverage", Range(0.0, 1.0)) = 0.50
        _CloudDensity ("Cloud Density", Range(0.1, 3.0)) = 1.35
        _CloudScale ("Cloud Macro Scale", Float) = 0.00022
        _CloudDetailScale ("Cloud Detail Scale", Float) = 0.00075
        _CloudAltitudeMin ("Cloud Base Altitude (m)", Float) = 1200.0
        _CloudAltitudeMax ("Cloud Top Altitude (m)", Float) = 2800.0
        _CloudShadowColor ("Cloud Underbelly Color", Color) = (0.2902, 0.2314, 0.1961, 1)
        _CloudHighlightColor ("Cloud Silver Lining Color", Color) = (1.0, 0.9569, 0.8392, 1)
        _CirrusOpacity ("High Cirrus Opacity", Range(0.0, 0.8)) = 0.35

        [Header(Wind and Heatwave Modulation)]
        _WindDirection ("Wind Direction (XZ)", Vector) = (1.0, 0.0, 0.35, 0.0)
        _CloudWindSpeed ("Base Wind Speed", Float) = 18.0
        _CloudWindPhase ("Runtime Wind Phase (Driven)", Float) = 0.0
        _HeatwaveSurge ("Heatwave Surge (0-1)", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Background"
            "RenderType"="Background"
            "PreviewType"="Skybox"
            "RenderPipeline"="UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "ScorchedVolumetricCloudsSkyboxPass"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 rayDirWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_CloudNoiseTex);
            SAMPLER(sampler_CloudNoiseTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _MidSkyColor;
                half4 _HorizonColor;
                half4 _GroundColor;

                half4 _SunColor;
                float _SunSize;
                float _SunIntensity;
                float _MieIntensity;
                float _MieG;

                float4 _CloudNoiseTex_ST;
                float _CloudCoverage;
                float _CloudDensity;
                float _CloudScale;
                float _CloudDetailScale;
                float _CloudAltitudeMin;
                float _CloudAltitudeMax;
                half4 _CloudShadowColor;
                half4 _CloudHighlightColor;
                float _CirrusOpacity;

                float4 _WindDirection;
                float _CloudWindSpeed;
                float _CloudWindPhase;
                float _HeatwaveSurge;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.rayDirWS = input.positionOS.xyz;
                return output;
            }

            float Hash31(float3 p)
            {
                p = frac(p * float3(443.8975, 397.2973, 491.1871));
                p += dot(p, p.yxz + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = Hash31(i + float3(0, 0, 0));
                float n100 = Hash31(i + float3(1, 0, 0));
                float n010 = Hash31(i + float3(0, 1, 0));
                float n110 = Hash31(i + float3(1, 1, 0));
                float n001 = Hash31(i + float3(0, 0, 1));
                float n101 = Hash31(i + float3(1, 0, 1));
                float n011 = Hash31(i + float3(0, 1, 1));
                float n111 = Hash31(i + float3(1, 1, 1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);

                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);

                return lerp(nxy0, nxy1, f.z);
            }

            float Fbm3D(float3 p)
            {
                float value = 0.0;
                float amp = 0.52;
                float3 shift = float3(17.3, 9.2, 31.7);
                for (int i = 0; i < 4; i++)
                {
                    value += amp * ValueNoise3D(p);
                    p = p * 2.07 + shift;
                    amp *= 0.48;
                }
                return value;
            }

            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = max(0.001, 1.0 + g2 - 2.0 * g * cosTheta);
                return (1.0 - g2) / (4.0 * 3.14159265 * pow(denom, 1.5));
            }

            float SampleCloudDensity(float2 uv, float3 windOffset, float heightFraction)
            {
                // Rounded cumulus base and tapering tops
                float heightProfile = smoothstep(0.0, 0.25, heightFraction) * smoothstep(1.0, 0.60, heightFraction);

                float2 uv1 = (uv + windOffset.xz) * _CloudScale;
                float2 uv2 = (uv + windOffset.xz * 1.35 + float2(127.3, 219.1)) * _CloudDetailScale;

                float baseWeather = SAMPLE_TEXTURE2D_LOD(_CloudNoiseTex, sampler_CloudNoiseTex, uv1, 0).r;
                float detailNoise = SAMPLE_TEXTURE2D_LOD(_CloudNoiseTex, sampler_CloudNoiseTex, uv2, 0).r;

                float weather = baseWeather * 0.70 + detailNoise * 0.30;
                float coverage = saturate(_CloudCoverage + _HeatwaveSurge * 0.08);
                float cloud = saturate((weather - (1.0 - coverage)) / max(0.05, coverage));

                return saturate(cloud * heightProfile) * _CloudDensity;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 rayDir = normalize(input.rayDirWS);
                Light mainLight = GetMainLight();
                float3 sunDir = normalize(mainLight.direction);
                if (length(sunDir) < 0.01) sunDir = normalize(float3(0.45, 0.65, 0.45));

                float cosTheta = dot(rayDir, sunDir);
                float up = rayDir.y;

                // 1. Desert Rayleigh & Horizon Gradient
                half3 horizonCol = lerp(_HorizonColor.rgb, half3(0.95, 0.60, 0.30), _HeatwaveSurge * 0.45);
                half3 midCol = lerp(_MidSkyColor.rgb, half3(0.72, 0.52, 0.36), _HeatwaveSurge * 0.35);
                half3 zenithCol = lerp(_ZenithColor.rgb, half3(0.32, 0.28, 0.28), _HeatwaveSurge * 0.25);

                half3 skyColor;
                if (up >= 0.0)
                {
                    float horizonMix = smoothstep(0.0, 0.28, up);
                    float zenithMix = smoothstep(0.18, 0.88, up);
                    skyColor = lerp(horizonCol, midCol, horizonMix);
                    skyColor = lerp(skyColor, zenithCol, zenithMix);
                }
                else
                {
                    skyColor = lerp(horizonCol, _GroundColor.rgb, smoothstep(0.0, -0.25, up));
                }

                // 2. Solar Disk & Mie Forward Scattering Halo
                float hgPhase = HenyeyGreenstein(cosTheta, _MieG);
                float mieGlow = hgPhase * _MieIntensity * (1.0 + _HeatwaveSurge * 0.4);
                float sunDisk = smoothstep(1.0 - _SunSize * 0.06, 1.0 - _SunSize * 0.015, cosTheta);
                float sunCorona = pow(saturate(cosTheta), 48.0) * 1.4;

                skyColor += _SunColor.rgb * (mieGlow * 0.22 + sunCorona);
                skyColor += _SunColor.rgb * _SunIntensity * sunDisk;

                // 3. Curved Atmospheric Cloud Shell Raymarch (above horizon)
                // 3. Realistic Volumetric Clouds & Horizon Blending
                if (up > 0.02)
                {
                    float phaseTravel = (_CloudWindPhase > 0.001) ? _CloudWindPhase : (_Time.y * _CloudWindSpeed);
                    float3 windDirNorm = float3(_WindDirection.x, 0.0, _WindDirection.z);
                    if (length(windDirNorm) < 0.001) windDirNorm = float3(1.0, 0.0, 0.35);
                    windDirNorm = normalize(windDirNorm);
                    float3 windOffset = windDirNorm * phaseTravel;

                    float hBase = _CloudAltitudeMin;
                    float hTop = _CloudAltitudeMax;
                    float hThickness = max(200.0, hTop - hBase);

                    // Celestial Dome Projection
                    float horizonFade = smoothstep(0.04, 0.26, up);
                    float invUp = 1.0 / max(up, 0.06);

                    const int STEPS = 8;
                    float transmittance = 1.0;
                    half3 accumLight = 0.0;

                    for (int s = 0; s < STEPS; s++)
                    {
                        float hFrac = (float(s) + 0.5) / (float)STEPS;
                        float h = lerp(hBase, hTop, hFrac);
                        float dist = h * invUp;
                        float2 cloudUV = rayDir.xz * dist;

                        float density = SampleCloudDensity(cloudUV, windOffset, hFrac);

                        if (density > 0.005)
                        {
                            // Sun light-march for self-shadowing & silver lining
                            float2 sunOffset = sunDir.xz * (hThickness * 0.35 / max(0.2, sunDir.y));
                            float shadowDensity = SampleCloudDensity(cloudUV + sunOffset, windOffset, min(1.0, hFrac + 0.3));

                            float lightTransmittance = exp(-shadowDensity * 3.2);
                            float silverLining = HenyeyGreenstein(cosTheta, 0.65) * 1.6;

                            half3 litColor = lerp(_CloudShadowColor.rgb, _CloudHighlightColor.rgb, saturate(lightTransmittance * 0.85 + 0.15 * hFrac));
                            litColor += _SunColor.rgb * silverLining * lightTransmittance * 0.55;

                            float stepAlpha = 1.0 - exp(-density * 0.45);
                            accumLight += transmittance * litColor * stepAlpha;
                            transmittance *= (1.0 - stepAlpha);

                            if (transmittance < 0.02) break;
                        }
                    }

                    // High-altitude wispy cirrus layer (~5400m)
                    float2 cirrusUV = rayDir.xz * (5400.0 * invUp) + windOffset.xz * 1.5;
                    float cirrusTex = SAMPLE_TEXTURE2D_LOD(_CloudNoiseTex, sampler_CloudNoiseTex, cirrusUV * (_CloudScale * 1.4), 1).r;
                    float cirrusMask = smoothstep(0.48, 0.80, cirrusTex) * _CirrusOpacity * smoothstep(0.05, 0.35, up);
                    half3 cirrusColor = lerp(_HorizonColor.rgb, _CloudHighlightColor.rgb, 0.65);

                    // Front-to-back compositing smoothly blended with desert horizon haze
                    half3 compositeCloud = accumLight + skyColor * transmittance;
                    skyColor = lerp(skyColor, compositeCloud, horizonFade);
                    skyColor = lerp(skyColor, cirrusColor, cirrusMask * transmittance * horizonFade);
                }

                return half4(skyColor, 1.0);
            }
            ENDHLSL
        }
    }
}

