// GPU-instanced grass blade shader for DrawMeshInstancedIndirect.
// Based on MangoButtermilch/Unity-Grass-Instancer (MIT License).
// Rewritten for full URP compatibility (no UnityCG.cginc).
//
// Each instance reads its TRS matrix from a StructuredBuffer filled by a
// compute-shader visibility pass.  Wind is applied in the vertex shader
// using value noise derived from world-space UVs.

Shader "OSMSendai/GrassIndirect"
{
    Properties
    {
        _PrimaryCol  ("Base Color",     Color)  = (0.25, 0.45, 0.15, 1)
        _SecondaryCol("Mid Color",      Color)  = (0.35, 0.58, 0.20, 1)
        _TipColor    ("Tip Color",      Color)  = (0.50, 0.72, 0.28, 1)
        _AOColor     ("AO Color",       Color)  = (0.15, 0.22, 0.10, 1)
        _WindNoiseScale ("Wind Noise Scale", Float) = 2.25
        _WindStrength   ("Wind Strength",    Float) = 4.8
        _WindSpeed      ("Wind Speed", Vector) = (-4.92, 3, 0, 0)
        _MeshDeformationLimitLow ("Deform Low",  Range(0, 5)) = 0.08
        _MeshDeformationLimitTop ("Deform Top",  Range(0, 5)) = 2.0
        _MinBrightness  ("Min Brightness", Float) = 0.5
        _ShadowBrightness ("Shadow Brightness", Float) = 0.3
        [Toggle] _RecieveShadow("Receive Shadow", Float) = 1
    }

    SubShader
    {
        Tags {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry+1"
        }
        LOD 100
        Cull Off
        ZWrite On

        // ─────────────────────── ForwardLit ───────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "noise.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS       : SV_POSITION;
                float2 uv               : TEXCOORD0;
                float3 normalWS         : TEXCOORD1;
                float4 shadowCoord      : TEXCOORD2;
                float3 positionWS       : TEXCOORD3;
                float  fogFactor        : TEXCOORD4;
            };

            StructuredBuffer<float4x4> visibleList;

            CBUFFER_START(UnityPerMaterial)
                float4 _PrimaryCol;
                float4 _SecondaryCol;
                float4 _TipColor;
                float4 _AOColor;
                float4 _WindSpeed;
                float  _WindNoiseScale;
                float  _WindStrength;
                float  _MeshDeformationLimitLow;
                float  _MeshDeformationLimitTop;
                float  _MinBrightness;
                float  _ShadowBrightness;
                float  _RecieveShadow;
            CBUFFER_END

            Varyings vert(Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings o = (Varyings)0;

                float3 posWS = mul(visibleList[instanceID], float4(input.positionOS.xyz, 1)).xyz;

                // Wind noise
                float2 worldUV = posWS.xz + _WindSpeed.xy * _Time.y;
                float noise = 0;
                Unity_SimpleNoise_float(worldUV, _WindNoiseScale, noise);
                noise -= 0.5;

                o.uv = input.uv;
                float smoothDef = smoothstep(_MeshDeformationLimitLow, _MeshDeformationLimitTop, o.uv.y);
                float distortion = smoothDef * noise;
                posWS.xz += distortion * _WindStrength * normalize(_WindSpeed.xy);

                o.positionWS  = posWS;
                o.positionCS  = TransformWorldToHClip(posWS);
                o.normalWS    = TransformObjectToWorldNormal(input.normalOS);
                o.shadowCoord = TransformWorldToShadowCoord(posWS);
                o.fogFactor   = ComputeFogFactor(o.positionCS.z);

                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float4 col  = lerp(_PrimaryCol, _SecondaryCol, input.uv.y);
                float4 ao   = lerp(_AOColor, 1.0, input.uv.y);
                float4 tip  = lerp(0.0, _TipColor, input.uv.y * input.uv.y);
                float4 grassColor = (col + tip) * ao;

                Light mainLight = GetMainLight(input.shadowCoord);
                float NdotL = clamp(dot(mainLight.direction, input.normalWS), _MinBrightness, 1.0);

                float3 litColor = grassColor.rgb * NdotL;

                if (_RecieveShadow > 0)
                {
                    float shadow = saturate(mainLight.shadowAttenuation + _ShadowBrightness);
                    litColor *= shadow * mainLight.color;
                }

                litColor = MixFog(litColor, input.fogFactor);
                return half4(litColor, 1);
            }
            ENDHLSL
        }

        // ─────────────────────── ShadowCaster ───────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vertShadow
            #pragma fragment fragShadow
            #pragma target   4.5

            #include "noise.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            StructuredBuffer<float4x4> visibleList;
            float3 _LightDirection;

            CBUFFER_START(UnityPerMaterial)
                float4 _PrimaryCol;
                float4 _SecondaryCol;
                float4 _TipColor;
                float4 _AOColor;
                float4 _WindSpeed;
                float  _WindNoiseScale;
                float  _WindStrength;
                float  _MeshDeformationLimitLow;
                float  _MeshDeformationLimitTop;
                float  _MinBrightness;
                float  _ShadowBrightness;
                float  _RecieveShadow;
            CBUFFER_END

            Varyings vertShadow(Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings o;

                float3 posWS = mul(visibleList[instanceID], float4(input.positionOS.xyz, 1)).xyz;

                float2 worldUV = posWS.xz + _WindSpeed.xy * _Time.y;
                float noise = 0;
                Unity_SimpleNoise_float(worldUV, _WindNoiseScale, noise);
                noise -= 0.5;

                float smoothDef = smoothstep(_MeshDeformationLimitLow, _MeshDeformationLimitTop, input.uv.y);
                posWS.xz += smoothDef * noise * _WindStrength * normalize(_WindSpeed.xy);

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return o;
            }

            half4 fragShadow(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ─────────────────────── DepthOnly ───────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vertDepth
            #pragma fragment fragDepth
            #pragma target   4.5

            #include "noise.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            StructuredBuffer<float4x4> visibleList;

            CBUFFER_START(UnityPerMaterial)
                float4 _PrimaryCol;
                float4 _SecondaryCol;
                float4 _TipColor;
                float4 _AOColor;
                float4 _WindSpeed;
                float  _WindNoiseScale;
                float  _WindStrength;
                float  _MeshDeformationLimitLow;
                float  _MeshDeformationLimitTop;
                float  _MinBrightness;
                float  _ShadowBrightness;
                float  _RecieveShadow;
            CBUFFER_END

            Varyings vertDepth(Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings o;

                float3 posWS = mul(visibleList[instanceID], float4(input.positionOS.xyz, 1)).xyz;

                float2 worldUV = posWS.xz + _WindSpeed.xy * _Time.y;
                float noise = 0;
                Unity_SimpleNoise_float(worldUV, _WindNoiseScale, noise);
                noise -= 0.5;

                float smoothDef = smoothstep(_MeshDeformationLimitLow, _MeshDeformationLimitTop, input.uv.y);
                posWS.xz += smoothDef * noise * _WindStrength * normalize(_WindSpeed.xy);

                o.positionCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
