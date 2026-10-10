// Region map sky: blue gradient into the fog colour at the horizon, the sun, and slowly drifting fair-weather
// clouds (fbm value noise on a plane high above). _GroundColor is the fog colour, set by DexstandSceneBuilder.
Shader "Dexstand/CloudSky"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.13, 0.3, 0.68, 1)
        _GroundColor ("Horizon / fog", Color) = (0.72, 0.78, 0.84, 1)
        _Coverage ("Cloud cover", Range(0, 1)) = 0.48
        _CloudScale ("Cloud scale", Float) = 1.3
        _Wind ("Wind", Float) = 0.004
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor;
                float4 _GroundColor;
                float _Coverage;
                float _CloudScale;
                float _Wind;
            CBUFFER_END

            float _Night;  // global, from MapDayNight
            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            V vert(A i)
            {
                V o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.dir = i.positionOS.xyz;
                return o;
            }

            float H(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(H(i), H(i + float2(1, 0)), f.x), lerp(H(i + float2(0, 1)), H(i + 1), f.x), f.y);
            }
            float Fbm(float2 p)
            {
                float s = 0, a = 0.5;
                for (int k = 0; k < 5; k++) { s += a * Noise(p); p = p * 2.03 + 17.1; a *= 0.5; }
                return s;
            }

            half4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = saturate(d.y);
                float3 sky = lerp(_GroundColor.rgb, _ZenithColor.rgb, pow(up, 0.45));
                float3 sunDir = _MainLightPosition.xyz;
                float3 sunCol = _MainLightColor.rgb;
                float sd = saturate(dot(d, sunDir));
                sky += sunCol * (pow(sd, 900) * 6 + pow(sd, 12) * 0.12);

                // clouds on a plane: stretched toward the horizon, faded out there
                float2 uv = d.xz / (d.y + 0.12) * _CloudScale + _Time.y * _Wind * float2(1, 0.4);
                float n = Fbm(uv);
                float c = smoothstep(1 - _Coverage, 1 - _Coverage + 0.22, n);
                float lit = saturate(Fbm(uv + sunDir.xz * 0.08) - n + 0.6);   // thinner towards the sun: brighter
                float3 cloud = lerp(float3(0.55, 0.58, 0.63), float3(1.0, 0.99, 0.97), lit) * (0.35 + 0.65 * length(sunCol) / 1.7);
                float fade = smoothstep(0.0, 0.18, d.y);
                cloud *= lerp(1.0, 0.06, _Night);
                // stars where the clouds leave the night sky open
                float3 sc = floor(d * 260.0);  // cells on the sphere of directions: no grid showing
                float hs = frac(sin(dot(sc, float3(127.1, 311.7, 74.7))) * 43758.5453);
                sky += _Night * step(0.9988, hs) * saturate(d.y * 5.0) * (1 - c) * lerp(0.5, 1.5, frac(hs * 97.3));
                sky = lerp(sky, cloud, c * fade * 0.95);
                sky = lerp(sky, _GroundColor.rgb, step(d.y, 0.0));
                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
