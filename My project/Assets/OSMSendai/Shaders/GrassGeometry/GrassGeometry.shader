// Grass Blade Shader — CPU-generated blade quads with vertex-shader wind animation.
// Works on all platforms (Metal, Vulkan, D3D11, etc.) — no geometry shader needed.
//
// Mesh convention (set by BuildGrassBlades in StreamingTileGenerator):
//   UV.y = 0 at blade base, 1 at blade tip
//   Vertex color = landcover tint (forest / grass)

Shader "OSMSendai/GrassGeometry" {
    Properties {
        _Color ("Base Color", Color) = (0.25, 0.45, 0.15, 1)
        _Color2 ("Tip Color", Color) = (0.45, 0.70, 0.25, 1)
        _WindStrength ("Wind Strength", Float) = 0.05
    }
    SubShader {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+1" }
        LOD 200

        Cull Off

        Pass {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _Color2;
                float _WindStrength;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float2 uv          : TEXCOORD0;
                float4 color       : TEXCOORD3;
            };

            Varyings vert(Attributes input) {
                Varyings o = (Varyings)0;

                float3 posOS = input.positionOS.xyz;

                // Wind displacement: only affects upper portion of blade (uv.y > 0)
                float windFactor = input.uv.y * input.uv.y; // quadratic ease
                float3 posWS = TransformObjectToWorld(posOS);
                float windX = sin(_Time.y * 1.5 + posWS.x * 0.4 + posWS.z * 0.3) * _WindStrength * windFactor;
                float windZ = cos(_Time.y * 1.2 + posWS.z * 0.5 + posWS.x * 0.2) * _WindStrength * windFactor * 0.6;
                posOS.x += windX;
                posOS.z += windZ;

                VertexPositionInputs vpi = GetVertexPositionInputs(posOS);
                o.positionCS = vpi.positionCS;
                o.positionWS = vpi.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                o.color = input.color;
                return o;
            }

            float4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                float3 normalWS = isFrontFace ? input.normalWS : -input.normalWS;

                #if SHADOWS_SCREEN
                    float4 clipPos = TransformWorldToHClip(input.positionWS);
                    float4 shadowCoord = ComputeScreenPos(clipPos);
                #else
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif

                float3 ambient = SampleSH(normalWS);
                Light mainLight = GetMainLight(shadowCoord);

                float NdotL = saturate(saturate(dot(normalWS, mainLight.direction)) + 0.8);
                float up = saturate(dot(float3(0, 1, 0), mainLight.direction) + 0.5);
                float3 shading = NdotL * up * mainLight.shadowAttenuation * mainLight.color + ambient;

                // Lerp base->tip color by UV.y, then tint by vertex color
                float4 grassColor = lerp(_Color, _Color2, input.uv.y);
                float3 vcTint = lerp(float3(1, 1, 1), input.color.rgb * 2.0, 0.3);

                return float4(grassColor.rgb * shading * vcTint, 1);
            }

            ENDHLSL
        }

        Pass {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _Color2;
                float _WindStrength;
            CBUFFER_END

            float3 _LightDirection;

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertShadow(Attributes input) {
                Varyings o;

                float3 posOS = input.positionOS.xyz;
                float windFactor = input.uv.y * input.uv.y;
                float3 posWS = TransformObjectToWorld(posOS);
                float windX = sin(_Time.y * 1.5 + posWS.x * 0.4 + posWS.z * 0.3) * _WindStrength * windFactor;
                float windZ = cos(_Time.y * 1.2 + posWS.z * 0.5 + posWS.x * 0.2) * _WindStrength * windFactor * 0.6;
                posOS.x += windX;
                posOS.z += windZ;

                float3 posWSFinal = TransformObjectToWorld(posOS);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(posWSFinal, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, o.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return o;
            }

            half4 fragShadow(Varyings input) : SV_TARGET {
                return 0;
            }

            ENDHLSL
        }

        Pass {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _Color2;
                float _WindStrength;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertDepth(Attributes input) {
                Varyings o;

                float3 posOS = input.positionOS.xyz;
                float windFactor = input.uv.y * input.uv.y;
                float3 posWS = TransformObjectToWorld(posOS);
                float windX = sin(_Time.y * 1.5 + posWS.x * 0.4 + posWS.z * 0.3) * _WindStrength * windFactor;
                float windZ = cos(_Time.y * 1.2 + posWS.z * 0.5 + posWS.x * 0.2) * _WindStrength * windFactor * 0.6;
                posOS.x += windX;
                posOS.z += windZ;

                o.positionCS = TransformObjectToHClip(posOS);
                return o;
            }

            half4 fragDepth(Varyings input) : SV_TARGET {
                return 0;
            }

            ENDHLSL
        }
    }
}
