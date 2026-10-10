// Rivers, lakes and the Rhine (Blender build_water.py, material "Water"; DexstandMapCutout puts this shader on it).
// Opaque: a dark water body, the sky and banks reflected through the reflection probes (MapReflections) by Fresnel,
// a sun glint, and small waves from a few travelling sines plus fine ripples, faded out with distance so they
// don't shimmer. Casts no shadow.
Shader "Dexstand/Water"
{
    Properties
    {
        _Deep ("Deep colour", Color) = (0.015, 0.04, 0.045, 1)
        _Shallow ("Shallow tint", Color) = (0.05, 0.1, 0.09, 1)
        _WaveScale ("Wave strength", Range(0, 2)) = 1
        _MetresPerUnit ("Metres per world unit", Float) = 3
        _Flow ("Flow direction (xz) and speed", Vector) = (1, 0.3, 0.6, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _Deep;
            half4 _Shallow;
            float _WaveScale;
            float _MetresPerUnit;
            float4 _Flow;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float fog : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
            }

            // slope of the water surface (dh/dx, dh/dz) in metres
            float2 Slope(float2 m, float t, float fine)
            {
                float2 s = 0;
                static const float4 W[4] = { float4(0.8, 0.6, 0.55, 1.3), float4(-0.4, 0.9, 0.9, 1.9), float4(0.95, -0.3, 1.7, 2.6), float4(0.2, -1.0, 2.9, 3.4) };
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 d = normalize(W[k].xy);
                    float ph = dot(d, m) * W[k].z + t * W[k].w;
                    s += d * cos(ph) * W[k].z * (0.018 / (1 + k));
                }
                // fine ripples drifting with the flow
                float2 q = m * 2.3 + _Flow.xy * t * _Flow.z;
                float e = 0.05;
                float n0 = Noise(q), nx = Noise(q + float2(e, 0)), nz = Noise(q + float2(0, e));
                s += float2(nx - n0, nz - n0) / e * 0.02 * fine;
                return s * _WaveScale;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 viewDir = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float2 m = i.positionWS.xz * _MetresPerUnit;
                float dist = length(i.positionWS - _WorldSpaceCameraPos) * _MetresPerUnit;
                float fade = saturate(1 - dist / 250.0);            // calm far off: no shimmer
                float2 sl = Slope(m, _Time.y, saturate(1 - dist / 60.0)) * lerp(0.25, 1.0, fade);
                float3 n = normalize(float3(-sl.x, 1, -sl.y));

                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float nv = saturate(dot(n, viewDir));
                float fresnel = 0.02 + 0.98 * pow(1 - nv, 5);
                float3 refl = GlossyEnvironmentReflection(reflect(-viewDir, n), i.positionWS, 0.04, 1.0);
                float3 body = lerp(_Deep.rgb, _Shallow.rgb, 0.35) * (SampleSH(float3(0, 1, 0)) + sun.color * saturate(sun.direction.y) * 0.4);
                float3 h = normalize(sun.direction + viewDir);
                float glint = pow(saturate(dot(n, h)), 600) * 6.0 + pow(saturate(dot(n, h)), 90) * 0.15;
                float3 lit = lerp(body, refl, fresnel) + sun.color * glint * sun.shadowAttenuation;
                return half4(MixFog(lit, i.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return GetVertexPositionInputs(p.xyz).positionCS; }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return GetVertexPositionInputs(p.xyz).positionCS; }
            half4 frag() : SV_Target { return half4(0, 1, 0, 0); }
            ENDHLSL
        }
    }
    FallBack Off
}
