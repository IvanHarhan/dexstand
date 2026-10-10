// Leaf and needle cards of MapTrees' instanced trees. Lit as URP Lit lit them, every card was flat: its own normal,
// half of them black from behind. Here a card takes the normal of the crown it sits in (out from the crown's centre,
// MapTrees hands in _Crown per tree kind), so a tree shades like one rounded mass, darker inside and underneath; the
// sun shines through the leaves seen against it; each tree gets its own shade of green and sways in the wind.
Shader "Dexstand/TreeLeaves"
{
    Properties
    {
        _BaseMap ("Leaves", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (0.42, 0.48, 0.34, 1)
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _Crown ("Crown centre (object) + radius", Vector) = (0, 1, 0, 1)
        _Wind ("Wind sway (object units)", Float) = 0.03
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            float4 _Crown;
            float _Wind;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

        // the whole crown leans with the gusts, each card flutters on its own; the trunk (below the crown) stays put
        float3 Sway(float3 posOS)
        {
            float3 root = TransformObjectToWorld(float3(0, 0, 0));
            float phase = dot(root.xz, float2(0.13, 0.071));
            float lift = saturate((posOS.y - (_Crown.y - _Crown.w)) / max(2.0 * _Crown.w, 1e-3));
            float t = _Time.y;
            float gust = sin(t * 0.9 + phase) * 0.6 + sin(t * 2.3 + phase * 1.7) * 0.4;
            float flutter = sin(t * 7.0 + dot(posOS, float3(9.1, 7.3, 8.7)) + phase) * 0.35;
            return posOS + float3(gust + flutter, 0, 0.5 * gust - flutter) * _Wind * _Crown.w * lift;
        }

        void Clip(float2 uv)
        {
            clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a - _Cutoff);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 crownWS : TEXCOORD2;   // out from the crown's centre, world space
                float2 uv : TEXCOORD3;
                float2 shade : TEXCOORD4;     // x how far out in the crown (0 centre, 1 rim), y height in it
                float tint : TEXCOORD5;
                float fog : TEXCOORD6;
            };

            Varyings vert(Attributes i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                Varyings o;
                float3 pos = Sway(i.positionOS.xyz);
                VertexPositionInputs p = GetVertexPositionInputs(pos);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                float3 d = i.positionOS.xyz - _Crown.xyz;
                o.crownWS = TransformObjectToWorldNormal(d * float3(1, 0.7, 1));
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                o.shade = float2(saturate(length(d) / max(_Crown.w, 1e-3)),
                                 saturate((i.positionOS.y - (_Crown.y - _Crown.w)) / max(2.0 * _Crown.w, 1e-3)));
                float3 root = TransformObjectToWorld(float3(0, 0, 0));
                o.tint = frac(sin(dot(root.xz, float2(12.9898, 78.233))) * 43758.5453);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                clip(tex.a - _Cutoff);
                // each tree a little yellower or bluer, lighter or darker than the next
                float3 albedo = tex.rgb * _BaseColor.rgb * lerp(float3(0.85, 0.9, 1.0), float3(1.12, 1.05, 0.8), i.tint)
                              * lerp(0.85, 1.1, frac(i.tint * 7.3));

                float3 card = normalize(i.normalWS) * (IS_FRONT_VFACE(face, 1.0, -1.0));
                float3 n = normalize(lerp(card, normalize(i.crownWS), 0.75));
                float3 viewDir = GetWorldSpaceNormalizeViewDir(i.positionWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float shadow = sun.shadowAttenuation;
                // leaves deep in the crown and under it see little sky and less sun
                float inner = lerp(0.45, 1.0, i.shade.x) * lerp(0.7, 1.0, i.shade.y);
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                float wrap = saturate(dot(n, sun.direction) * 0.6 + 0.4);
                float3 lit = albedo * (sun.color * shadow * wrap * lerp(0.6, 1.0, i.shade.x) * ao.directAmbientOcclusion
                                       + SampleSH(n) * inner * ao.indirectAmbientOcclusion);
                // sunlight through the leaves, when looking towards the sun
                float through = pow(saturate(dot(viewDir, -sun.direction)), 4.0) * shadow * lerp(0.3, 1.0, i.shade.x);
                lit += albedo * float3(1.1, 1.2, 0.55) * sun.color * through * 0.6;
                return half4(MixFog(lit, i.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                V o;
                float3 p = TransformObjectToWorld(Sway(i.positionOS.xyz));
                float3 n = TransformObjectToWorldNormal(i.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 dir = normalize(_LightPosition - p);
            #else
                float3 dir = _LightDirection;
            #endif
                float4 c = TransformWorldToHClip(ApplyShadowBias(p, n, dir));
            #if UNITY_REVERSED_Z
                c.z = min(c.z, UNITY_NEAR_CLIP_VALUE);
            #else
                c.z = max(c.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = c;
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }
            half4 frag(V i) : SV_Target { Clip(i.uv); return 0; }
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
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                V o;
                o.positionCS = TransformObjectToHClip(Sway(i.positionOS.xyz));
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }
            half frag(V i) : SV_Target { Clip(i.uv); return 0; }
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
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; };
            V vert(A i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                V o;
                o.positionCS = TransformObjectToHClip(Sway(i.positionOS.xyz));
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                o.normalWS = TransformObjectToWorldNormal((i.positionOS.xyz - _Crown.xyz) * float3(1, 0.7, 1));
                return o;
            }
            half4 frag(V i) : SV_Target { Clip(i.uv); return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack Off
}
