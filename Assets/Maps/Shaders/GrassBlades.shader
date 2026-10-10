// Grass blades for MapGrass: one clump mesh drawn per instance from _Blades (xyz + yaw, photo uv + size + tint).
// The clump folds away where the orthophoto under it is not green (roads, yards, fields of stubble) and near the
// edge of MapGrass's radius, and its tips sway in the wind.
Shader "Dexstand/GrassBlades"
{
    Properties
    {
        _BaseMap ("Orthophoto (per draw)", 2D) = "white" {}
        _Root ("Root colour", Color) = (0.035, 0.07, 0.02, 1)
        _Tip ("Tip colour", Color) = (0.2, 0.3, 0.07, 1)
        _Wind ("Wind strength (m)", Float) = 0.06
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Blade { float4 pos; float4 data; };
            StructuredBuffer<Blade> _Blades;
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            float4 _Root, _Tip;
            float _Wind, _MetresPerUnit, _FadeDistance;

            struct A { float3 positionOS : POSITION; float2 uv : TEXCOORD0; uint iid : SV_InstanceID; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 col : TEXCOORD1; float fog : TEXCOORD2; };

            V vert(A i)
            {
                Blade b = _Blades[i.iid];
                float3 c = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, b.data.xy, 0).rgb;
                float green = saturate((c.g - max(c.r, c.b)) * 22.0 - 0.1);
                float dist = distance(b.pos.xyz, _WorldSpaceCameraPos);
                float fade = saturate((_FadeDistance - dist) / (_FadeDistance * 0.3));
                float s = b.data.z * green * fade;
                float cy = cos(b.pos.w), sy = sin(b.pos.w);
                float3 p = float3(i.positionOS.x * cy - i.positionOS.z * sy, i.positionOS.y, i.positionOS.x * sy + i.positionOS.z * cy) * s;
                float h = i.uv.y;
                float t = _Time.y * 1.7 + b.pos.x * 0.9 + b.pos.z * 0.6;
                p.xz += float2(sin(t), cos(t * 0.7)) * _Wind / _MetresPerUnit * h * h * green;
                float3 w = b.pos.xyz + p;
                V o;
                o.positionWS = w;
                o.positionCS = TransformWorldToHClip(w);
                float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
                o.col = lerp(_Root.rgb, _Tip.rgb * lerp(0.8, 1.2, b.data.w), h) * clamp(lum / 0.1, 0.6, 1.4);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 n = float3(0, 1, 0);
                float3 lit = i.col * (sun.color * sun.shadowAttenuation * (0.35 + 0.65 * saturate(sun.direction.y)) + SampleSH(n));
                return half4(MixFog(lit, i.fog), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
