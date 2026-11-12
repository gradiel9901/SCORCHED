Shader "Scorched/VFX/AshDisintegration"
{
    Properties
    {
        [Header(Base Appearance)]
        _BaseMap ("Base Texture", 2D) = "white" {}
        _BaseColor ("Base Color Tint", Color) = (1.0, 1.0, 1.0, 1.0)
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.0
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.2

        [Header(Ash Disintegration and Burn)]
        _DissolveAmount ("Dissolve Progress (0=Intact, 1=Ashes)", Range(0.0, 1.0)) = 0.0
        _DissolveNoise ("Noise Texture (T_VFX_AshDissolveNoise)", 2D) = "white" {}
        _NoiseTiling ("Noise Tiling", Vector) = (4.0, 4.0, 0.0, 0.0)

        [Header(Fiery Embers and Charred Ash)]
        [HDR] _EmberColor ("Ember Glow Color", Color) = (3.5, 1.2, 0.15, 1.0)
        _EmberWidth ("Ember Rim Width", Range(0.01, 0.25)) = 0.08
        _AshColor ("Charred Ash Color", Color) = (0.09, 0.08, 0.07, 1.0)
        _AshWidth ("Ash Gradient Width", Range(0.02, 0.40)) = 0.18

        [Header(Vertical Crumble Bias)]
        _HeightMin ("Object Height Min (Local Y)", Float) = 0.0
        _HeightMax ("Object Height Max (Local Y)", Float) = 10.0
        _VerticalBurnBias ("Top-Down Burn Bias", Range(0.0, 1.0)) = 0.45
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "Queue" = "Geometry" 
            "RenderPipeline" = "UniversalPipeline" 
        }

        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 uv           : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : TEXCOORD2;
                float localY        : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
                float _Metallic;
                float _Smoothness;
                float _DissolveAmount;
                float4 _NoiseTiling;
                half4 _EmberColor;
                float _EmberWidth;
                half4 _AshColor;
                float _AshWidth;
                float _HeightMin;
                float _HeightMax;
                float _VerticalBurnBias;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_DissolveNoise);
            SAMPLER(sampler_DissolveNoise);

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.localY = input.positionOS.y;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Early exit if completely intact
                if (_DissolveAmount <= 0.001)
                {
                    half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                    return col;
                }

                // Sample 2D Noise
                float2 noiseUV = input.uv * _NoiseTiling.xy;
                half noise = SAMPLE_TEXTURE2D(_DissolveNoise, sampler_DissolveNoise, noiseUV).r;

                // Vertical top-down burn gradient: higher local Y burns first
                float heightRange = max(0.1, _HeightMax - _HeightMin);
                float height01 = saturate((input.localY - _HeightMin) / heightRange);
                float verticalBias = (height01 - 0.5) * _VerticalBurnBias;

                // Combined dissolve value
                float dissolveThreshold = saturate(_DissolveAmount * 1.35 - 0.15);
                float dissolveFactor = (noise - verticalBias) - dissolveThreshold;

                // Disintegration pixel clipping (turns into thin air / ash void)
                clip(dissolveFactor);

                // Sample base texture
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Burning front calculations:
                // 1. Glowing Ember edge: pixels closest to clip line (dissolveFactor ~ 0)
                float emberMask = 1.0 - smoothstep(0.0, _EmberWidth, dissolveFactor);
                // 2. Charred Ash gradient: pixels behind the ember rim
                float ashMask = 1.0 - smoothstep(_EmberWidth, _EmberWidth + _AshWidth, dissolveFactor);

                // Surface transitions to charred black ash
                half3 surfaceColor = lerp(baseTex.rgb, _AshColor.rgb, ashMask);

                // Add intense incandescent ember emission
                half3 emberEmission = _EmberColor.rgb * (emberMask * emberMask);

                // Basic directional lighting
                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(input.normalWS, mainLight.direction));
                half3 directLighting = surfaceColor * (mainLight.color * (ndotl * 0.85 + 0.15));

                half3 finalRGB = directLighting + emberEmission;
                return half4(finalRGB, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 uv           : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float localY        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
                float _Metallic;
                float _Smoothness;
                float _DissolveAmount;
                float4 _NoiseTiling;
                half4 _EmberColor;
                float _EmberWidth;
                half4 _AshColor;
                float _AshWidth;
                float _HeightMin;
                float _HeightMax;
                float _VerticalBurnBias;
            CBUFFER_END

            TEXTURE2D(_DissolveNoise);
            SAMPLER(sampler_DissolveNoise);

            Varyings vertShadow(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.localY = input.positionOS.y;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, _MainLightPosition.xyz));
                return output;
            }

            half4 fragShadow(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                if (_DissolveAmount > 0.001)
                {
                    float2 noiseUV = input.uv * _NoiseTiling.xy;
                    half noise = SAMPLE_TEXTURE2D(_DissolveNoise, sampler_DissolveNoise, noiseUV).r;
                    float heightRange = max(0.1, _HeightMax - _HeightMin);
                    float height01 = saturate((input.localY - _HeightMin) / heightRange);
                    float verticalBias = (height01 - 0.5) * _VerticalBurnBias;
                    float dissolveThreshold = saturate(_DissolveAmount * 1.35 - 0.15);
                    clip((noise - verticalBias) - dissolveThreshold);
                }

                return 0;
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
