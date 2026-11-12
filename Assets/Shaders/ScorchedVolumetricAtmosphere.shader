Shader "Scorched/PostProcessing/ScorchedVolumetricAtmosphere"
{
    Properties
    {
        [Header(World Space Valley Dust Fog)]
        _FogColor ("Valley Fog Color", Color) = (0.8784, 0.6902, 0.4314, 1)
        _FogSunColor ("Sunlit Fog Scatter Color", Color) = (1.0, 0.8706, 0.5882, 1)
        _FogDensity ("Fog Density", Range(0.0, 0.08)) = 0.015
        _FogBaseHeight ("Fog Base Height", Float) = 3.5
        _FogHeightFalloff ("Fog Height Falloff", Range(0.01, 0.5)) = 0.075
        _FogMaxOpacity ("Max Fog Opacity", Range(0.0, 0.92)) = 0.56
        _FogStartDistance ("Fog Start Distance", Float) = 6.0

        [Header(Turbulent Wind Dust)]
        _TurbulenceScale ("Noise Scale", Float) = 0.03
        _TurbulenceStrength ("Turbulence Strength", Range(0.0, 1.0)) = 0.42
        _WindOffset ("Runtime Wind Offset", Vector) = (0, 0, 0, 0)

        [Header(Crepuscular Volumetric God Rays)]
        _GodRayColor ("God Ray Color", Color) = (1.0, 0.8588, 0.5490, 1)
        _GodRayIntensity ("God Ray Intensity", Range(0.0, 3.0)) = 1.0
        _GodRayDecay ("God Ray Decay", Range(0.85, 0.995)) = 0.945
        _GodRayDensity ("God Ray Step Density", Range(0.1, 1.6)) = 0.92
        _GodRayMaxExposure ("God Ray Max Clamp", Range(0.05, 1.0)) = 0.42

        [Header(Heatwave Surge State)]
        _HeatwaveSurge ("Heatwave Surge", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "ScorchedVolumetricAtmospherePass"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FogColor;
                half4 _FogSunColor;
                float _FogDensity;
                float _FogBaseHeight;
                float _FogHeightFalloff;
                float _FogMaxOpacity;
                float _FogStartDistance;

                float _TurbulenceScale;
                float _TurbulenceStrength;
                float4 _WindOffset;

                half4 _GodRayColor;
                float _GodRayIntensity;
                float _GodRayDecay;
                float _GodRayDensity;
                float _GodRayMaxExposure;

                float _HeatwaveSurge;
            CBUFFER_END

            float Hash31(float3 p)
            {
                p = frac(p * float3(443.8975, 397.2973, 491.1871));
                p += dot(p, p.yxz + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            float Noise3D(float3 p)
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

            float FbmTurbulence(float3 p)
            {
                float v = 0.0;
                v += 0.65 * Noise3D(p);
                v += 0.35 * Noise3D(p * 2.13 + float3(11.4, 5.7, 23.1));
                return v;
            }

            bool IsSkyDepth(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth <= 0.00001;
                #else
                    return rawDepth >= 0.99999;
                #endif
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                half4 sceneSample = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                half3 sceneRGB = sceneSample.rgb;

                float rawDepth = SampleSceneDepth(uv);
                bool isSky = IsSkyDepth(rawDepth);

                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 camToWorld = worldPos - _WorldSpaceCameraPos;
                float dist = length(camToWorld);
                float3 viewDir = normalize(camToWorld);

                Light mainLight = GetMainLight();
                float3 sunDir = normalize(mainLight.direction);
                if (length(sunDir) < 0.01) sunDir = normalize(float3(0.45, 0.65, 0.45));

                float sunDot = saturate(dot(viewDir, sunDir));
                float mieForward = pow(sunDot, 5.0);

                // 1. Height-Based & 3D Turbulent Valley Dust Fog (keeps upper skies/clouds crisp)
                if (!isSky)
                {
                    float effectiveDist = max(0.0, dist - _FogStartDistance);
                    float distFactor = 1.0 - exp(-effectiveDist * _FogDensity);

                    // Exponential height falloff concentrates heavy sunlit dust in low central valleys
                    float heightDelta = max(0.0, worldPos.y - _FogBaseHeight);
                    float heightFactor = exp(-heightDelta * _FogHeightFalloff);

                    // Midpoint turbulence sample adds drifting 3D dust curtains through streets
                    float3 midWorld = _WorldSpaceCameraPos + viewDir * min(dist * 0.6, 120.0);
                    float turb = FbmTurbulence(midWorld * _TurbulenceScale - _WindOffset.xyz);
                    float turbMod = lerp(1.0, 0.65 + turb * 0.85, _TurbulenceStrength);

                    float fogAmount = saturate(distFactor * heightFactor * turbMod);
                    fogAmount = min(fogAmount, _FogMaxOpacity);

                    half3 baseFogCol = lerp(_FogColor.rgb, half3(0.92, 0.62, 0.34), _HeatwaveSurge * 0.35);
                    half3 dustColor = lerp(baseFogCol, _FogSunColor.rgb, mieForward * 0.72);
                    sceneRGB = lerp(sceneRGB, dustColor, fogAmount);
                }

                // 2. Radial Crepuscular Volumetric God Rays (High-Performance 10-Sample Occlusion March)
                float sunAlignment = dot(viewDir, sunDir);
                if (sunAlignment > 0.05 && _GodRayIntensity > 0.001)
                {
                    float3 farSunWorld = _WorldSpaceCameraPos + sunDir * 1000.0;
                    float4 sunClip = TransformWorldToHClip(farSunWorld);

                    if (sunClip.z > 0.0 && sunClip.w > 0.001)
                    {
                        float2 sunNDC = sunClip.xy / sunClip.w;
                        #if UNITY_UV_STARTS_AT_TOP
                            sunNDC.y = -sunNDC.y;
                        #endif
                        float2 sunUV = sunNDC * 0.5 + 0.5;

                        // Early out if sun is too far outside camera frustum
                        if (sunUV.x >= -0.5 && sunUV.x <= 1.5 && sunUV.y >= -0.5 && sunUV.y <= 1.5)
                        {
                            float2 deltaUV = (uv - sunUV);
                            const int NUM_SAMPLES = 10;
                            float2 stepUV = deltaUV * (_GodRayDensity / (float)NUM_SAMPLES);
                            float2 sampleUV = uv;

                            float illuminationDecay = 1.0;
                            float accumShaft = 0.0;

                            [unroll]
                            for (int i = 0; i < NUM_SAMPLES; i++)
                            {
                                sampleUV -= stepUV;
                                if (sampleUV.x >= 0.0 && sampleUV.x <= 1.0 && sampleUV.y >= 0.0 && sampleUV.y <= 1.0)
                                {
                                    float sRawDepth = SampleSceneDepth(sampleUV);
                                    float isLight = IsSkyDepth(sRawDepth) ? 1.0 : 0.0;
                                    accumShaft += isLight * illuminationDecay;
                                }
                                illuminationDecay *= _GodRayDecay;
                            }

                            accumShaft /= (float)NUM_SAMPLES;

                            // Weight by forward Mie cone and smooth falloff if sun moves near screen periphery
                            float radialSunDist = length(sunUV - 0.5);
                            float screenMask = 1.0 - smoothstep(0.70, 1.55, radialSunDist);
                            float directionWeight = pow(saturate(sunAlignment), 2.2);

                            // Sun shafts illuminate the dust in front of terrain & buildings (0 on sky dome to preserve crisp cloud silhouettes)
                            float airScatterWeight = isSky ? 0.0 : 1.0;
                            float rayContribution = min(_GodRayMaxExposure, accumShaft * _GodRayIntensity * screenMask * directionWeight * airScatterWeight);
                            half3 rayTint = lerp(_GodRayColor.rgb, half3(1.0, 0.72, 0.35), _HeatwaveSurge * 0.45);
                            sceneRGB += rayTint * rayContribution;
                        }
                    }
                }

                return half4(sceneRGB, sceneSample.a);
            }
            ENDHLSL
        }
    }
}

