// Ground and roofs of the region map. Their colour is the orthophoto (0.2-0.8 m a pixel, so up close it is a blur);
// this lays sharp detail over it in world space, relative to the detail texture's own mean so the photo keeps
// its colours: grass where the photo is green, paving grain elsewhere, and on roofs (_Mode 1) clay tiles running
// across the slope. DexstandMapCutout switches the Ortho_* / RoofOrtho_* materials of the map to it.
Shader "Dexstand/OrthoDetail"
{
    Properties
    {
        _BaseMap ("Orthophoto", 2D) = "white" {}
        _GrassTex ("Grass", 2D) = "grey" {}
        _PaveTex ("Paving", 2D) = "grey" {}
        _TileTex ("Roof tiles", 2D) = "grey" {}
        _GrassSize ("Grass tile (m)", Float) = 2.0
        _PaveSize ("Paving tile (m)", Float) = 2.0
        _TileSize ("Roof tile (m)", Float) = 2.5
        [Normal] _GrassNormal ("Grass normal", 2D) = "bump" {}
        [Normal] _PaveNormal ("Paving normal", 2D) = "bump" {}
        [Normal] _TileNormal ("Roof tile normal", 2D) = "bump" {}
        _Relief ("Relief strength", Range(0, 2)) = 1.0
        _Mode ("0 ground, 1 roof, 2 by slope", Float) = 0
        _MetresPerUnit ("Metres per world unit", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _GrassTex_ST;
            float4 _PaveTex_ST;
            float4 _TileTex_ST;
            float _GrassSize;
            float _PaveSize;
            float _TileSize;
            float _Mode;
            float _MetresPerUnit;
            float4 _GrassNormal_ST;
            float4 _PaveNormal_ST;
            float4 _TileNormal_ST;
            float _Relief;
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
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_GrassTex); SAMPLER(sampler_GrassTex);
            TEXTURE2D(_PaveTex); SAMPLER(sampler_PaveTex);
            TEXTURE2D(_TileTex); SAMPLER(sampler_TileTex);
            TEXTURE2D(_GrassNormal); SAMPLER(sampler_GrassNormal);
            TEXTURE2D(_PaveNormal); SAMPLER(sampler_PaveNormal);
            TEXTURE2D(_TileNormal); SAMPLER(sampler_TileNormal);
            // globals (MapGroundDetail), not material properties: the tiles' materials are baked into the FBX imports
            TEXTURE2D(_AsphaltTex); SAMPLER(sampler_AsphaltTex);
            TEXTURE2D(_AsphaltNormal); SAMPLER(sampler_AsphaltNormal);
            float _AsphaltSize;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = i.uv;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

            // detail relative to the texture's mean colour (its smallest mip): 1 on average, crisp around it
            float3 Rel(TEXTURE2D_PARAM(t, s), float2 uv)
            {
                float3 d = SAMPLE_TEXTURE2D(t, s, uv).rgb;
                float3 m = SAMPLE_TEXTURE2D_LOD(t, s, float2(0.5, 0.5), 12).rgb;
                return clamp(d / max(m, 0.02), 0.0, 2.5);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb;
                float3 n = normalize(i.normalWS);
                float3 pw = i.positionWS * _MetresPerUnit;  // detail sizes are in metres
                float3 col;
                // _Mode 2: the region tiles' ground and roofs share one material per orthophoto; a slope over ~25°
                // that isn't green is a pitched roof, anything flatter is ground (flat roofs take the paving detail)
                // green by hue, not by margin: the photo's late-summer meadows are olive to khaki, barely greener
                // than red but well clear of blue; grey asphalt, sand and brown earth stay out
                float gInv = 1.0 / max(c.g, 0.01);
                float greenness = saturate(min((c.g - 0.9 * c.r) * gInv * 10.0, (c.g - 1.25 * c.b) * gInv * 6.0));
                float greenish = step(0.5, greenness);
                float roof = _Mode > 1.5 ? step(abs(n.y), 0.906) * (1 - greenish) : _Mode;
                if (roof < 0.5)
                {
                    // where the photo is green: plain lawn texture (the user wanted uniform grass, not the blurry
                    // photo), only its brightness nudged by the photo so fields, shade and meadows still differ
                    float green = greenness;
                    float3 grassC = SAMPLE_TEXTURE2D(_GrassTex, sampler_GrassTex, pw.xz / _GrassSize).rgb;
                    float3 grassM = SAMPLE_TEXTURE2D_LOD(_GrassTex, sampler_GrassTex, float2(0.5, 0.5), 12).rgb;
                    float macro = Lum(SAMPLE_TEXTURE2D(_GrassTex, sampler_GrassTex, pw.xz / (_GrassSize * 9.7) + 0.37).rgb)
                                / max(Lum(grassM), 0.02);
                    // leafy_grass is mostly dead leaves (mean colour beige): it gives the pattern, the colour is a
                    // fresh lawn green with a third of the photo's own hue left in
                    static const float3 LAWN = float3(0.076, 0.141, 0.027);
                    float shade = clamp(Lum(c) / Lum(LAWN), 0.6, 1.3);
                    float blade = clamp(Lum(grassC) / max(Lum(grassM), 0.02), 0.4, 1.6);
                    float3 g = lerp(LAWN * shade, c, 0.3) * blade * lerp(0.88, 1.12, saturate(macro - 0.5));
                    // a real surface never is as white as the sun-bleached photo: cap the albedo
                    c *= min(1.0, 0.42 / max(Lum(c), 0.01));
                    // the rest (car parks, yards, track beds, bare earth) keeps the photo's colour — what lies there
                    // isn't known, and paving everywhere read as a brick plaza — and takes the grain of asphalt
                    // aggregate over it: strong near the camera, the photo's own blur by ~90 m, where a pixel covers
                    // more than the grain anyway
                    float asphaltSize = _AsphaltSize > 0 ? _AsphaltSize : 3.0;
                    float3 asphM = SAMPLE_TEXTURE2D_LOD(_AsphaltTex, sampler_AsphaltTex, float2(0.5, 0.5), 12).rgb;
                    float hasAsphalt = step(0.001, Lum(asphM));
                    float grain = hasAsphalt > 0
                        ? Lum(SAMPLE_TEXTURE2D(_AsphaltTex, sampler_AsphaltTex, pw.xz / asphaltSize).rgb) / max(Lum(asphM), 0.02)
                        : Lum(Rel(TEXTURE2D_ARGS(_PaveTex, sampler_PaveTex), pw.xz / _PaveSize));
                    float dist = length(i.positionWS - _WorldSpaceCameraPos) * _MetresPerUnit;
                    float closeUp = saturate(1.0 - (dist - 30.0) / 60.0);
                    col = lerp(c * lerp(1.0, clamp(grain, 0.3, 1.8), lerp(0.35, 0.9, closeUp)), g, green);
                    // relief of the grass or the paving, projected from above
                    float3 tsG = UnpackNormal(SAMPLE_TEXTURE2D(_GrassNormal, sampler_GrassNormal, pw.xz / _GrassSize));
                    float3 tsP = UnpackNormal(SAMPLE_TEXTURE2D(_PaveNormal, sampler_PaveNormal, pw.xz / _PaveSize));
                    float3 tsA = hasAsphalt > 0 ? UnpackNormal(SAMPLE_TEXTURE2D(_AsphaltNormal, sampler_AsphaltNormal, pw.xz / asphaltSize)) : tsP;
                    float3 ts = lerp(tsA, tsG, green);
                    ts.xy *= _Relief * 0.8;
                    float3 T = normalize(float3(1, 0, 0) - n * n.x);
                    float3 B = normalize(float3(0, 0, 1) - n * n.z);
                    n = normalize(T * ts.x + B * ts.y + n * ts.z);
                }
                else
                {
                    // rows of tiles run level along the roof, courses step down the slope
                    float3 t = cross(float3(0, 1, 0), n);
                    t = dot(t, t) < 1e-3 ? float3(1, 0, 0) : normalize(t);
                    float3 b = cross(n, t);
                    float flat = step(0.97, abs(n.y));
                    float2 tuv = float2(dot(pw, t), dot(pw, b)) / _TileSize;
                    float tile = Lum(Rel(TEXTURE2D_ARGS(_TileTex, sampler_TileTex), tuv));
                    col = c * lerp(1.0, tile, lerp(0.85, 0.25, flat));
                    float3 ts = UnpackNormal(SAMPLE_TEXTURE2D(_TileNormal, sampler_TileNormal, tuv));
                    ts.xy *= _Relief * (1 - flat);
                    n = normalize(t * ts.x + b * ts.y + n * ts.z);
                }

                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float ndl = saturate(dot(n, sun.direction));
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                float3 lit = col * (sun.color * sun.shadowAttenuation * ndl * ao.directAmbientOcclusion
                                    + SampleSH(n) * ao.indirectAmbientOcclusion);
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            float4 vert(A i) : SV_POSITION
            {
                float3 p = TransformObjectToWorld(i.positionOS.xyz);
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
                return c;
            }
            half4 frag() : SV_Target { return 0; }
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
            float4 vert(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
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
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.normalWS = TransformObjectToWorldNormal(i.normalOS); return o; }
            half4 frag(V i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack Off
}
