Shader "OSMSendai/GroundOverlayTransparent"
{
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (0.3, 0.5, 0.85, 0.75)
    }

    // ── URP SubShader ──────────────────────────────────────────────
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+1"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always

            // Prevent double-blending where area water and waterway ribbons overlap:
            // first water fragment sets stencil=2, subsequent fragments at the same pixel are rejected.
            Stencil
            {
                Ref 2
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float4 shadowCoord : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.shadowCoord = TransformWorldToShadowCoord(pos.positionWS);
                OUT.fogFactor  = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 n = normalize(IN.normalWS);
                Light light = GetMainLight(IN.shadowCoord);
                half NdotL = saturate(dot(n, light.direction));
                half direct = NdotL * light.shadowAttenuation;
                half3 ambient = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
                half3 col = _BaseColor.rgb * (light.color * direct + ambient);
                col = MixFog(col, IN.fogFactor);
                return half4(col, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    // ── Built-in pipeline fallback ─────────────────────────────────
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry+1" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 2
                Comp NotEqual
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            fixed4 _BaseColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float NdotL = saturate(dot(n, _WorldSpaceLightPos0.xyz));
                float3 col = _BaseColor.rgb * (_LightColor0.rgb * NdotL + UNITY_LIGHTMODEL_AMBIENT.rgb);
                return fixed4(col, _BaseColor.a);
            }
            ENDCG
        }
    }
}
