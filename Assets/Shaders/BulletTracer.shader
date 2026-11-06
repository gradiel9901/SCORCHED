Shader "Scorched/Weapons/BulletTracer"
{
    Properties
    {
        [Header(Bullet Casing)]
        _BaseColor ("Casing Base Color", Color) = (0.85, 0.65, 0.32, 1.0)
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.95
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.85

        [Header(Incandescent Heat Gradient)]
        _TipColor ("Incandescent Tip Color (Hot)", Color) = (1.0, 0.98, 0.88, 1.0)
        _HeatColor ("Scorched Heat Color", Color) = (1.0, 0.35, 0.05, 1.0)
        _TipHeatLength ("Tip Heat Range", Range(0.05, 2.0)) = 0.5
        _TipOffset ("Tip Origin Offset", Range(-1.0, 1.0)) = 0.0

        [Header(Tracer Glow and Bloom)]
        [HDR] _EmissionColor ("Tracer Emission (HDR)", Color) = (2.2, 0.85, 0.15, 1.0)
        _EmissionIntensity ("Emission Multiplier", Float) = 4.5
        _TailHeatLength ("Tracer Tail Glow Range", Range(0.05, 2.0)) = 0.7
        _TailOffset ("Tail Origin Offset", Range(-1.0, 1.0)) = 0.0

        [Header(Aerodynamic Plasma Fresnel)]
        [HDR] _FresnelColor ("Fresnel Rim Color", Color) = (1.2, 0.5, 0.1, 1.0)
        _FresnelPower ("Fresnel Power", Range(0.5, 8.0)) = 2.5

        [Header(Animation Pulse)]
        _PulseSpeed ("Tracer Pulse Speed", Float) = 14.0
        _PulseAmount ("Tracer Pulse Amplitude", Range(0.0, 0.5)) = 0.15
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex vert
            #pragma fragment frag

            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _HeatColor;
                float4 _EmissionColor;
                float4 _FresnelColor;
                float _Metallic;
                float _Smoothness;
                float _TipHeatLength;
                float _TipOffset;
                float _TailHeatLength;
                float _TailOffset;
                float _EmissionIntensity;
                float _FresnelPower;
                float _PulseSpeed;
                float _PulseAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewDirWS  : TEXCOORD2;
                float3 positionOS : TEXCOORD3;
                float2 uv         : TEXCOORD4;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(posInputs.positionWS);
                output.positionOS = input.positionOS.xyz;
                output.uv = input.uv;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(input.viewDirWS);

                // High-speed tracer shimmer pulse
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseAmount;

                // Heat gradient along longitudinal Z axis (+Z is forward)
                float z = input.positionOS.z;
                float frontHeat = saturate((z + _TipOffset) / max(0.001, _TipHeatLength));
                float rearHeat = saturate((-z + _TailOffset) / max(0.001, _TailHeatLength));

                // Base metallic copper/brass casing
                half3 bulletAlbedo = _BaseColor.rgb;

                // Incandescent tip blend
                half3 tipBlend = lerp(_HeatColor.rgb, _TipColor.rgb, frontHeat);
                bulletAlbedo = lerp(bulletAlbedo, tipBlend, frontHeat * 0.85);

                // Fresnel plasma rim (aerodynamic friction glow)
                float NdotV = saturate(dot(normalWS, viewDirWS));
                float fresnel = pow(1.0 - NdotV, _FresnelPower);
                half3 fresnelGlow = _FresnelColor.rgb * fresnel;

                // Lighting
                Light mainLight = GetMainLight();
                half3 lightColor = mainLight.color;
                half3 lightDir = mainLight.direction;
                float NdotL = saturate(dot(normalWS, lightDir));

                // Specular reflection
                float3 halfVector = normalize(lightDir + viewDirWS);
                float NdotH = saturate(dot(normalWS, halfVector));
                float specPow = exp2(10.0 * _Smoothness + 1.0);
                float spec = pow(NdotH, specPow) * _Metallic;

                half3 directLit = bulletAlbedo * (NdotL * lightColor + 0.18) + (spec * lightColor);

                // HDR Emissive Core
                half3 tipEmission = _TipColor.rgb * (frontHeat * frontHeat * 3.5);
                half3 coreEmission = _EmissionColor.rgb * (rearHeat * 2.2 + 0.45);
                half3 totalEmission = (tipEmission + coreEmission + fresnelGlow) * _EmissionIntensity * pulse;

                half3 finalColor = directLit + totalEmission;
                return half4(finalColor, 1.0);
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
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz));
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
