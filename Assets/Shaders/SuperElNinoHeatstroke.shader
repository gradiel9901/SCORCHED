Shader "Scorched/PostProcessing/SuperElNinoHeatstroke"
{
    Properties
    {
        _MainTex ("Source Texture", 2D) = "white" {}
        _ExposureNormalized ("Exposure (0 - 1)", Range(0, 1)) = 0
        _AmbientHeatHaze ("Ambient Heat Haze", Range(0, 1)) = 0
        _VignetteIntensity ("Vignette Intensity", Range(0, 1)) = 0
        _VignetteRadius ("Vignette Radius", Range(0.1, 0.9)) = 0.55
        _VignetteColor ("Vignette Color", Color) = (0.75, 0.12, 0.05, 1)
        _HeatHazeStrength ("Heat Haze Strength", Float) = 0
        _HeatHazeSpeed ("Heat Haze Speed", Float) = 2.8
        _HeatHazeFrequency ("Heat Haze Frequency", Float) = 12.0
        _DoubleVisionIntensity ("Double Vision Intensity", Float) = 0
        _DoubleVisionSpeed ("Double Vision Speed", Float) = 3.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "SuperElNinoHeatstrokePass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _ExposureNormalized;
                float _AmbientHeatHaze;
                float _VignetteIntensity;
                float _VignetteRadius;
                half4 _VignetteColor;
                float _HeatHazeStrength;
                float _HeatHazeSpeed;
                float _HeatHazeFrequency;
                float _DoubleVisionIntensity;
                float _DoubleVisionSpeed;
            CBUFFER_END

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;

                // Combine Stage 2/3 heatstroke shimmer with subtle ambient outdoor Heatwave mirage
                float effectiveHaze = max(_HeatHazeStrength, _AmbientHeatHaze * 0.0006);
                float hazeSpeed = max(_HeatHazeSpeed, 1.8);
                float hazeFreq = max(_HeatHazeFrequency, 1.0);

                // Stage 2 & 3 Rising Heat Shimmer / Heat Haze Refraction (Gentle & Center-Protected)
                float2 distortedUV = uv;
                if (effectiveHaze > 0.00005)
                {
                    float2 waveUV = uv * hazeFreq + float2(0.0, -_Time.y * hazeSpeed);
                    float n1 = sin(waveUV.y * 6.2831 + sin(waveUV.x * 4.5 + _Time.y * 2.0));
                    float n2 = cos(waveUV.x * 9.4247 - _Time.y * (hazeSpeed * 1.3) + cos(waveUV.y * 8.0));
                    // Keep crosshair and center vision clear and sharp; confine shimmer to periphery
                    float edgeFactor = smoothstep(0.18, 0.65, length(uv - 0.5) * 1.4);
                    float2 distortion = float2(n1, n2) * effectiveHaze * edgeFactor;
                    distortedUV = clamp(uv + distortion, 0.001, 0.999);
                }

                half4 centerSample = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, distortedUV);
                half3 finalRGB = centerSample.rgb;

                // Stage 3 Subtle Diplopia Ghosting (Non-disorienting)
                if (_DoubleVisionIntensity > 0.0005)
                {
                    float pulse = 0.5 + 0.5 * sin(_Time.y * _DoubleVisionSpeed);
                    float2 ghostOffset = float2(cos(_Time.y * _DoubleVisionSpeed), sin(_Time.y * _DoubleVisionSpeed * 1.5) * 0.4)
                                         * _DoubleVisionIntensity * pulse;

                    float2 chromaShift = ghostOffset * 0.20;
                    float2 uvA = clamp(distortedUV + ghostOffset, 0.001, 0.999);
                    float2 uvB = clamp(distortedUV - ghostOffset, 0.001, 0.999);

                    half3 colGhostA;
                    colGhostA.r = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, clamp(uvA + chromaShift, 0.001, 0.999)).r;
                    colGhostA.g = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uvA).g;
                    colGhostA.b = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, clamp(uvA - chromaShift, 0.001, 0.999)).b;

                    half3 colGhostB;
                    colGhostB.r = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, clamp(uvB - chromaShift, 0.001, 0.999)).r;
                    colGhostB.g = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uvB).g;
                    colGhostB.b = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, clamp(uvB + chromaShift, 0.001, 0.999)).b;

                    finalRGB = finalRGB * 0.75h + colGhostA * 0.15h + colGhostB * 0.10h;
                }

                // Stage 1, 2 & 3 Soft Peripheral Warning Vignette (Never Blinds Center View)
                if (_VignetteIntensity > 0.001)
                {
                    float dist = length((uv - 0.5) * float2(1.25, 1.0));
                    float heartbeat = sin(_Time.y * 3.5) * 0.02 * saturate(_ExposureNormalized);
                    float vigMask = saturate(smoothstep(_VignetteRadius, _VignetteRadius + 0.38, dist + heartbeat) * _VignetteIntensity);

                    half3 deepCrimson = half3(0.55h, 0.08h, 0.05h);
                    half3 fieryOrange = half3(0.85h, 0.35h, 0.12h);
                    half3 rimColor = lerp(fieryOrange, deepCrimson, saturate(vigMask * 1.1h));
                    rimColor = lerp(rimColor, _VignetteColor.rgb, 0.25h);

                    finalRGB = lerp(finalRGB, rimColor, (half)(vigMask * 0.60h));
                }

                return half4(finalRGB, centerSample.a);
            }
            ENDHLSL
        }
    }
}
