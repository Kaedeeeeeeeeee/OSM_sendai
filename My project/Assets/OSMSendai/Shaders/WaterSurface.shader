Shader "OSMSendai/WaterSurface"
{
    Properties
    {
        [Header(Color)]
        _ShallowColor("Water Color", Color) = (0.25, 0.60, 0.75, 0.85)

        [Header(Waves)]
        _WaveSpeed("Wave Speed", Float) = 0.8
        _WaveScale("Wave UV Scale", Float) = 0.04
        _WaveStrength("Wave Height", Float) = 0.12
        _WaveDir1("Wave Direction 1 (xy)", Vector) = (1.0, 0.6, 0, 0)
        _WaveDir2("Wave Direction 2 (xy)", Vector) = (-0.4, 1.0, 0, 0)

        [Header(Ripple Normals)]
        _NoiseTex("Noise Texture", 2D) = "bump" {}
        _NormalScale("Normal Scale (UV tiling)", Float) = 0.1
        _NormalSpeed1("Normal Scroll Speed 1 (xy)", Vector) = (0.02, 0.03, 0, 0)
        _NormalSpeed2("Normal Scroll Speed 2 (xy)", Vector) = (-0.03, 0.01, 0, 0)
        _NormalStrength("Normal Strength", Range(0, 2)) = 0.5

        [Header(Specular)]
        _WaterSpecColor("Specular Color", Color) = (1, 1, 1, 1)
        _SpecPower("Specular Power", Float) = 128
        _SpecIntensity("Specular Intensity", Float) = 1.0

        [Header(Fresnel)]
        _FresnelPower("Fresnel Power", Float) = 2.0
        _FresnelBias("Fresnel Bias (min opacity)", Range(0, 1)) = 0.65
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Cull Back

            // Prevent double-blending where area water and waterway ribbons overlap.
            Stencil
            {
                Ref 2
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_NoiseTex);  SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;

                float  _WaveSpeed;
                float  _WaveScale;
                float  _WaveStrength;
                float4 _WaveDir1;
                float4 _WaveDir2;

                float4 _NoiseTex_ST;
                float  _NormalScale;
                float4 _NormalSpeed1;
                float4 _NormalSpeed2;
                float  _NormalStrength;

                half4  _WaterSpecColor;
                float  _SpecPower;
                float  _SpecIntensity;

                float  _FresnelPower;
                float  _FresnelBias;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD0;
                float3 normalWS     : TEXCOORD1;
                float  fogFactor    : TEXCOORD2;
            };

            // ── Gerstner wave helper ───────────────────────────────
            float3 GerstnerWave(float2 dir, float2 worldXZ, float t,
                                float amplitude, float frequency, float steepness)
            {
                float2 d = normalize(dir);
                float phase = dot(d, worldXZ) * frequency + t;
                float s, c;
                sincos(phase, s, c);
                return float3(d.x * steepness * amplitude * c,
                              amplitude * s,
                              d.y * steepness * amplitude * c);
            }

            // ── Vertex ─────────────────────────────────────────────
            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                float3 posOS = IN.positionOS.xyz;
                float3 posWS = TransformObjectToWorld(posOS);

                // Apply Gerstner wave displacement in world space.
                float t = _Time.y * _WaveSpeed;
                float freq = _WaveScale * 6.2831853; // 2*PI * scale

                float3 wave1 = GerstnerWave(_WaveDir1.xy, posWS.xz, t,
                                            _WaveStrength, freq, 0.6);
                float3 wave2 = GerstnerWave(_WaveDir2.xy, posWS.xz, t * 0.8,
                                            _WaveStrength * 0.5, freq * 1.3, 0.4);

                posWS += wave1 + wave2;

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            // ── Unpack a grayscale noise texture into a faux normal ─
            half3 SampleNoiseNormal(float2 uv)
            {
                half h = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv).r;
                float d = 1.0 / 256.0;
                half hR = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv + float2(d, 0)).r;
                half hU = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv + float2(0, d)).r;
                half3 n = half3(h - hR, h - hU, 1.0);
                return normalize(n);
            }

            // ── Fragment ───────────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                float3 posWS = IN.positionWS;
                float2 worldUV = posWS.xz;

                // ── Scrolling normal perturbation ──
                float t = _Time.y;
                float2 uv1 = worldUV * _NormalScale + _NormalSpeed1.xy * t;
                float2 uv2 = worldUV * _NormalScale * 1.4 + _NormalSpeed2.xy * t;
                half3 n1 = SampleNoiseNormal(uv1);
                half3 n2 = SampleNoiseNormal(uv2);
                half3 perturbedN = normalize(half3(
                    (n1.x + n2.x) * _NormalStrength,
                    (n1.y + n2.y) * _NormalStrength,
                    1.0));

                // Blend with geometric normal.
                half3 N = normalize(IN.normalWS + half3(perturbedN.x, 0, perturbedN.y));

                // ── Water color (no depth texture dependency) ──
                half4 waterColor = _ShallowColor;

                // ── Fresnel ──
                float3 viewDir = normalize(_WorldSpaceCameraPos - posWS);
                float fresnel = _FresnelBias + (1.0 - _FresnelBias) * pow(1.0 - saturate(dot(viewDir, N)), _FresnelPower);
                waterColor.a *= fresnel;

                // ── Specular highlight ──
                Light mainLight = GetMainLight();
                float3 H = normalize(viewDir + mainLight.direction);
                float spec = pow(saturate(dot(N, H)), _SpecPower) * _SpecIntensity;
                half3 specular = _WaterSpecColor.rgb * mainLight.color * spec;

                // ── Simple lighting ──
                float NdotL = saturate(dot(N, mainLight.direction));
                half3 ambient = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
                half3 lit = waterColor.rgb * (mainLight.color * NdotL + ambient) + specular;

                // ── Fog ──
                lit = MixFog(lit, IN.fogFactor);

                return half4(lit, waterColor.a);
            }
            ENDHLSL
        }
    }

    // ── Built-in pipeline fallback (simple transparent) ──────────
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

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

            fixed4 _ShallowColor;
            float _WaveSpeed;
            float _WaveStrength;
            float4 _WaveDir1;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 posWS = mul(unity_ObjectToWorld, v.vertex).xyz;
                float phase = dot(normalize(_WaveDir1.xy), posWS.xz) * 0.5 + _Time.y * _WaveSpeed;
                posWS.y += sin(phase) * _WaveStrength;
                o.pos = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                o.worldPos = posWS;
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float NdotL = saturate(dot(n, _WorldSpaceLightPos0.xyz));
                float3 col = _ShallowColor.rgb * (_LightColor0.rgb * NdotL + UNITY_LIGHTMODEL_AMBIENT.rgb);
                return fixed4(col, _ShallowColor.a);
            }
            ENDCG
        }
    }
}
