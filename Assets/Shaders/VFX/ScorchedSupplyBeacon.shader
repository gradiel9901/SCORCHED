Shader "Scorched/VFX/SupplyBeacon"
{
    Properties
    {
        [Header(Beacon Color and Emission)]
        [HDR] _BeaconColor ("Beacon Core Color", Color) = (1.0, 0.45, 0.08, 1.0)
        _Intensity ("Emission Multiplier", Range(0.5, 8.0)) = 3.2
        _MainTex ("Beam Gradient (T_VFX_BeaconBeam)", 2D) = "white" {}

        [Header(Animation and Pulse)]
        _ScrollSpeed ("Vertical Energy Scroll Speed", Range(-4.0, 4.0)) = -1.2
        _PulseSpeed ("Radial Pulse Frequency", Range(0.5, 8.0)) = 3.0
        _PulseAmplitude ("Pulse Amplitude", Range(0.0, 0.5)) = 0.18

        [Header(Edge and Depth Softening)]
        _EdgeFresnelPower ("Edge Softness Power", Range(0.5, 6.0)) = 2.0
        _DepthFadeDistance ("Soft Depth Fade Distance", Range(0.05, 5.0)) = 0.8
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Transparent" 
            "Queue" = "Transparent+100" 
            "RenderPipeline" = "UniversalPipeline" 
            "IgnoreProjector" = "True"
        }

        Blend One One // Pure Additive for brilliant solar laser beacon
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

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
                float3 normalWS     : TEXCOORD1;
                float3 viewDirWS    : TEXCOORD2;
                float4 screenPos    : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BeaconColor;
                float _Intensity;
                float4 _MainTex_ST;
                float _ScrollSpeed;
                float _PulseSpeed;
                float _PulseAmplitude;
                float _EdgeFresnelPower;
                float _DepthFadeDistance;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = normalize(_WorldSpaceCameraPos - posWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Animated vertical energy scroll
                float2 uvScroll = input.uv;
                uvScroll.y += _Time.y * _ScrollSpeed;

                half4 beamTex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvScroll);

                // Radial pulse
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseAmplitude;

                // Edge softness via view-normal alignment
                float ndotv = abs(dot(normalize(input.normalWS), normalize(input.viewDirWS)));
                float fresnel = pow(saturate(ndotv), _EdgeFresnelPower);

                // Soft depth fade against terrain / geometry
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 0.0001);
                #if UNITY_REVERSED_Z
                    float rawDepth = SampleSceneDepth(screenUV);
                #else
                    float rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, SampleSceneDepth(screenUV));
                #endif
                float sceneLinearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float pixelLinearDepth = LinearEyeDepth(input.positionCS.z, _ZBufferParams);
                float depthFade = saturate((sceneLinearDepth - pixelLinearDepth) / max(0.01, _DepthFadeDistance));

                // Vertical sky fade (beamTex.a already incorporates bottom-ground to top-sky falloff)
                float alpha = beamTex.a * fresnel * depthFade * pulse;

                half3 finalColor = _BeaconColor.rgb * _Intensity * alpha;
                return half4(finalColor, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}

