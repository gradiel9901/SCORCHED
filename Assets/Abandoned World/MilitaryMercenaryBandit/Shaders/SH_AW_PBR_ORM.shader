Shader "SH_AW_PBR_ORM" // Especially for the store "Abandoned World"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Normal ("Normal Map", 2D) = "bump" {}
        _ORM ("AO,Roughness,Metallic", 2D) = "white" {}
        _Emissive ("Emissive", 2D) = "black" {}
        _NormalStrength ("Normal Map Strength", Float) = 1.0
        _Roughness ("Roughness", Range(0,2)) = 1
        _Metallic ("Metallic", Range(0,2)) = 1
        _AO_Intensity ("AO Intensity", Range(0,10)) = 1
        _Emission ("Emission", float) = 0
        _ColorEmission ("Color Emissive", Color) = (0,0,0,0)
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex ForwardPassVertex
            #pragma fragment ForwardPassFragment

            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _FORWARD_PLUS

            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog

            // GPU Instancing
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);        SAMPLER(sampler_MainTex);
            TEXTURE2D(_Normal);         SAMPLER(sampler_Normal);
            TEXTURE2D(_ORM);            SAMPLER(sampler_ORM);
            TEXTURE2D(_Emissive);       SAMPLER(sampler_Emissive);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _ColorEmission;
                half _NormalStrength;
                half _Roughness;
                half _Metallic;
                half _AO_Intensity;
                half _Emission;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 texcoord     : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD0;
                float2 uv           : TEXCOORD1;
                half3 normalWS      : TEXCOORD2;
                half4 tangentWS     : TEXCOORD3;
                half fogFactor      : TEXCOORD4;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ForwardPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
                output.normalWS = normalInput.normalWS;
                real sign = input.tangentOS.w * GetOddNegativeScale();
                output.tangentWS = half4(normalInput.tangentWS.xyz, sign);

                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);

                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                return output;
            }

            half4 ForwardPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;

                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal, sampler_Normal, input.uv), _NormalStrength);
                float sgn = input.tangentWS.w;
                float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);
                half3 normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
                normalWS = NormalizeNormalPerPixel(normalWS);

                half4 orm = SAMPLE_TEXTURE2D(_ORM, sampler_ORM, input.uv);
                half4 emissive = SAMPLE_TEXTURE2D(_Emissive, sampler_Emissive, input.uv);

                half metallic = saturate(orm.b * _Metallic);
                half smoothness = saturate(abs(1.0 - orm.g) * _Roughness);
                half occlusion = saturate(orm.r * _AO_Intensity);
                half3 emission = emissive.rgb * _Emission * _ColorEmission.rgb;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.metallic = metallic;
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.emission = emission;
                surfaceData.occlusion = occlusion;
                surfaceData.alpha = albedo.a;
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
                inputData.tangentToWorld = tangentToWorld;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
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

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _ColorEmission;
                half _NormalStrength;
                half _Roughness;
                half _Metallic;
                half _AO_Intensity;
                half _Emission;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 texcoord     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS   : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(ShadowAttributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                positionCS = ApplyShadowClamping(positionCS);
                return positionCS;
            }

            ShadowVaryings ShadowPassVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(ShadowVaryings input) : SV_TARGET
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
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _ColorEmission;
                half _NormalStrength;
                half _Roughness;
                half _Metallic;
                half _AO_Intensity;
                half _Emission;
            CBUFFER_END

            struct DepthAttributes
            {
                float4 positionOS   : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS   : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthOnlyVertex(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(DepthVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _ColorEmission;
                half _NormalStrength;
                half _Roughness;
                half _Metallic;
                half _AO_Intensity;
                half _Emission;
            CBUFFER_END

            struct DepthNormalsAttributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthNormalsVaryings
            {
                float4 positionCS   : SV_POSITION;
                float3 normalWS     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthNormalsVaryings DepthNormalsVertex(DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output = (DepthNormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFragment(DepthNormalsVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
