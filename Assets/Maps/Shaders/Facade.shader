// Walls of every building on the region map, drawn from numbers the Blender export packs into each face
// (export_region.py) instead of baked textures — 17 000 buildings would need a thousand atlases.
//   uv0 wall metres (u along the facade, v up from the building's base)   uv1 wall length, eave height
//   uv2 wall bottom, building height   uv3 door position, door type (1 house, 2 garage, 3 wide glass)
//   uv4 kind (0 house 1 commercial 2 shed 3 church 4 glass front 5 hall 6 school 7 store 8 historic), house style
//       (0 plaster 1 Bergisch slate with green shutters 2 half-timber 3 brick)
//   uv5 flags (1 shop front, 2 red band, 4 white, 8 brick, 16 * frame colour 0-3, 64 shutters, 128 roller shutters),
//       door colour + 8 * sign colour
//   uv6 window width, height (m)   uv7 window bay (m), plinth height (m): per building, from its architecture zone
//   uv3.y also carries 4 * the door sill above the wall bottom in 5 cm steps
//   vertex colour: wall colour, linear
// Mirrors wall_shader.py in Blender, which is the reference for the look.
Shader "Dexstand/Facade"
{
    Properties
    {
        _PlasterTex ("Plaster", 2D) = "white" {}
        _BrickTex ("Brick", 2D) = "white" {}
        _PlasterSize ("Plaster tile (m)", Float) = 2.0
        _BrickSize ("Brick tile (m)", Float) = 1.0
        [Normal] _PlasterNormal ("Plaster normal", 2D) = "bump" {}
        [Normal] _BrickNormal ("Brick normal", 2D) = "bump" {}
        _Relief ("Relief strength", Range(0, 2)) = 1.0
        _Grime ("Grime", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _PlasterTex_ST;
            float4 _BrickTex_ST;
            float _PlasterSize;
            float _BrickSize;
            float4 _PlasterNormal_ST;
            float4 _BrickNormal_ST;
            float _Relief;
            float _Grime;
        CBUFFER_END

        // how far vert() pushes a building out from its wall plane, so two in one plane don't fight for the depth;
        // every pass that writes depth uses it, or the prepass and the colour pass disagree
        float3 Pushed(float3 positionOS, float3 normalOS, float height)
        {
            return positionOS + normalOS * frac(sin(height * 12.9898) * 43758.5453) * 0.03;
        }
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
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_PlasterTex); SAMPLER(sampler_PlasterTex);
            TEXTURE2D(_BrickTex); SAMPLER(sampler_BrickTex);
            float _Night;  // global, 0 by day .. 1 at night (MapDayNight)
            TEXTURE2D(_PlasterNormal); SAMPLER(sampler_PlasterNormal);
            TEXTURE2D(_BrickNormal); SAMPLER(sampler_BrickNormal);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float2 wall : TEXCOORD0;
                float2 geo : TEXCOORD1;
                float2 geo2 : TEXCOORD2;
                float2 door : TEXCOORD3;
                float2 kind : TEXCOORD4;
                float2 flags : TEXCOORD5;
                float2 win : TEXCOORD6;
                float2 win2 : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                // Everything but u, v is one value per wall, and must reach the pixel exactly: interpolated, it came
                // out a few ulps off from pixel to pixel, and Hash() of it (which window columns a house leaves out,
                // its grime, its shutters) turned to noise — the white-speckled "moiré" windows.
                nointerpolation float4 color : TEXCOORD2;
                float2 uvw : TEXCOORD3;                  // u, v
                nointerpolation float4 b : TEXCOORD4;    // bottom, height, door u, door type
                nointerpolation float4 c : TEXCOORD5;    // kind, style, flags, colours
                float fog : TEXCOORD6;
                nointerpolation float4 d : TEXCOORD7;    // window width, height, bay, plinth
                nointerpolation float2 geo : TEXCOORD8;  // wall length, eave
            };

            Varyings vert(Attributes i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                Varyings o;
                // walls of two buildings that lie in one plane fought for the depth (blinking windows): each
                // building sits out from its walls by its own few centimetres (mesh units are metres), keyed by its height
                VertexPositionInputs p = GetVertexPositionInputs(Pushed(i.positionOS.xyz, i.normalOS, i.geo2.y));
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color;
                o.uvw = i.wall;
                o.geo = i.geo;
                o.b = float4(i.geo2, i.door);
                o.c = float4(i.kind, i.flags);
                o.d = float4(i.win, i.win2);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // The window grid, frames, mullions and shutter slats are hard steps repeated every metre or so; a few
            // metres off they fall between pixels and shimmered (moiré). Both are box-filtered over the pixel's
            // footprint w instead (in x's own units), so thin lines fade to their average rather than alias.
            float Band(float x, float lo, float hi, float w)
            {
                w = max(w, 1e-5);
                return saturate((min(x + 0.5 * w, hi) - max(x - 0.5 * w, lo)) / w);
            }
            // 1 where frac(x / p) * p < d; PulseI is its running integral
            float PulseI(float x, float p, float d) { float k = floor(x / p); return k * d + min(x - k * p, d); }
            float Pulse(float x, float p, float d, float w)
            {
                w = max(w, 1e-5);
                return (PulseI(x + 0.5 * w, p, d) - PulseI(x - 0.5 * w, p, d)) / w;
            }
            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            half4 frag(Varyings i) : SV_Target
            {
                float u = i.uvw.x, v = i.uvw.y, wlen = max(i.geo.x, 0.01), weave = i.geo.y;
                float wu = fwidth(u), wv = fwidth(v);  // one pixel along and up the wall, metres
                float wbot = i.b.x, height = i.b.y, doorU = i.b.z, doorRaw = round(i.b.w);
                float doorT = fmod(doorRaw, 4.0), doorBase = floor(doorRaw / 4.0) * 0.05;
                float kind = round(i.c.x), style = round(i.c.y), flags = round(i.c.z);
                float dcol = fmod(round(i.c.w), 8.0), scol = floor(round(i.c.w) / 8.0);
                float shop = fmod(flags, 2.0), bandOn = fmod(floor(flags / 2.0), 2.0), white = fmod(floor(flags / 4.0), 2.0);
                float brick = fmod(floor(flags / 8.0), 2.0) + step(2.5, style) * step(style, 3.5);
                float frameIdx = fmod(floor(flags / 16.0), 4.0), shutF = fmod(floor(flags / 64.0), 2.0);
                float rollF = fmod(floor(flags / 128.0), 2.0), slateMode = fmod(floor(flags / 256.0), 8.0);
                float blank = step(abs(kind - 2), 0.4), church = step(abs(kind - 3), 0.4), glassK = step(abs(kind - 4), 0.4);
                float hall = step(abs(kind - 5), 0.4), school = step(abs(kind - 6), 0.4), store = step(abs(kind - 7), 0.4);
                float historic = step(abs(kind - 8), 0.4);
                float resLike = max(step(kind, 0.5), historic);
                float timber = step(abs(style - 2), 0.4) * resLike;
                // Bergisch slate all over, or only on the gable, the upper floors, the ground floor or the west side
                float3 nW = normalize(i.normalWS);
                float sm0 = step(slateMode, 0.5), sm1 = step(abs(slateMode - 1), 0.4), sm2 = step(abs(slateMode - 2), 0.4);
                float sm3 = step(abs(slateMode - 3), 0.4), sm4 = step(abs(slateMode - 4), 0.4);
                float slatePart = sm0 + sm1 * step(weave - 0.05, v) + sm2 * step(wbot + 3.0, v)
                          + sm3 * step(v, wbot + 3.0) + sm4 * step(0.3, nW.x);
                float slate = step(abs(style - 1), 0.4) * resLike * saturate(slatePart);

                // ---- wall surface ----
                float plasterL = dot(SAMPLE_TEXTURE2D(_PlasterTex, sampler_PlasterTex, float2(u, v) / _PlasterSize).rgb, float3(0.3, 0.59, 0.11));
                float3 wall = i.color.rgb * lerp(0.8, 1.12, plasterL);
                float3 brickC = SAMPLE_TEXTURE2D(_BrickTex, sampler_BrickTex, float2(u, v) / _BrickSize).rgb;
                wall = lerp(wall, brickC * 0.85, brick * (1 - slate) * (1 - timber));
                wall = lerp(wall, float3(0.72, 0.72, 0.7) * lerp(0.9, 1.05, plasterL), saturate(hall + store + white));
                float joints = store * max(Pulse(v, 1.25, 0.015, wv), Pulse(u, 3.0, 0.012, wu));
                wall = lerp(wall, float3(0.45, 0.45, 0.44), joints);
                // Bergisch slate: overlapping rounded scales in offset rows, each a shade off its neighbours
                float row = floor(v / 0.19);
                float2 sc = float2(u / 0.3 + 0.5 * fmod(row, 2.0), v / 0.19);
                float2 cellId = floor(sc);
                float2 f = frac(sc) - float2(0.5, 0.0);
                float edge = step(length(float2(f.x * 1.25, f.y - 0.55)), 0.62);
                float3 slateC = float3(0.035, 0.04, 0.05) * (0.75 + 0.5 * Hash(cellId)) * lerp(0.55, 1.0, f.y) * lerp(0.6, 1.0, edge);
                // scales smaller than a couple of pixels turn into their average shade
                float slateFar = saturate(3.0 * max(wu / 0.3, wv / 0.19) - 0.5);
                slateC = lerp(slateC, float3(0.035, 0.04, 0.05) * 0.62, slateFar);
                wall = lerp(wall, slateC, slate);

                // ---- window grid: whole bays per facade, 3 m floors, full floors below the eave; houses take their
                // zone's window size and bay in metres, windows hang from a common lintel line ----
                float spacing = max(i.d.z, 1.6);
                float nwin = max(floor(wlen / spacing), 1.0);
                float bay = wlen / nwin;
                float fu = frac(u / bay), fv = frac(v / 3.0), fl = floor(v / 3.0) * 3.0;
                float du = abs(fu - 0.5) * bay, y = fv * 3.0;
                float hw = 0.5 * min(max(i.d.x, 0.4), bay - 0.6), y1 = 2.52, y0 = y1 - clamp(i.d.y, 0.6, 2.2), fm = 0.07;
                float okFloor = step(wbot - 0.3, fl) * step(fl + 3.0, weave + 0.3) * step(4.0, height);
                float okChurch = step(wbot + 2.0, v) * step(v, min(weave - 1.0, wbot + 9.0));
                float enabled = (1 - blank) * step(1.8, wlen);
                float rel = v - wbot;
                float groundFloor = step(rel, 3.0);
                float dx = abs(u - doorU);
                float doorBay = step(abs(doorT - 1), 0.4) * step(fl - wbot, doorBase + 2.3) * step(dx, bay * 0.5);
                float plain = (1 - doorBay) * (1 - shop * groundFloor) * (1 - saturate(glassK + hall + school + store));
                // houses leave some window columns out: plain wall between rooms, stairs, bathrooms
                float colSkip = resLike * step(2.5, nwin) * step(Hash(float2(floor(u / bay), wlen * 3.7 + weave)), 0.28);
                float win = enabled * okFloor * (1 - church) * plain * (1 - colSkip);
                // an attic window or two in the gable above the eave
                float gz0 = weave + 0.8, gz1 = gz0 + min(i.d.y * 0.8, 1.1);
                float gdu = wlen < 9.0 ? abs(u - wlen * 0.5) : abs(abs(u - wlen * 0.5) - 1.4);
                float ghw = min(hw, 0.5);
                float gwin = resLike * enabled * (1 - church) * step(3.4, height - weave);
                float cwin = enabled * okChurch * church * plain;
                float wfu = wu / bay, wfv = wv / 3.0;
                float frame = win * Band(du, 0, hw + fm, wu) * Band(y, y0 - fm, y1 + fm, wv) + cwin * Band(fu, 0.38, 0.62, wfu)
                            + gwin * Band(gdu, 0, ghw + fm, wu) * Band(v, gz0 - fm, gz1 + fm, wv);
                float glass = win * Band(du, 0, hw, wu) * Band(y, y0, y1, wv) + cwin * Band(fu, 0.41, 0.59, wfu)
                            + gwin * Band(gdu, 0, ghw, wu) * Band(v, gz0, gz1, wv);
                glass *= 1 - step(1.35, 2 * hw) * Band(du, -1, 0.035, wu) * win;  // wide windows: two sashes with a mullion
                // a roller-shutter box over the window
                float rbox = rollF * resLike * win * Band(du, 0, hw + fm, wu) * Band(y, y1 + fm, y1 + fm + 0.2, wv);
                float shutter = saturate(slate + shutF * resLike) * win * Band(du, hw + fm, hw + fm + 0.9 * hw, wu) * Band(y, y0 - fm, y1 + fm, wv);
                // roller shutters: about half let down part of the way
                float2 winId = float2(floor(u / bay), fl);
                float down = rollF * resLike * step(0.45, Hash(winId + 3.1)) * lerp(0.15, 0.9, Hash(winId + 7.7));
                float roller = win * Band(du, 0, hw, wu) * Band(y, y1 - (y1 - y0) * down, y1, wv);
                // a stone sill under each window, a light render lintel over it (not on slate or timber)
                float sill = win * Band(du, 0, hw + fm + 0.06, wu) * Band(y, y0 - fm - 0.07, y0 - fm, wv) * (1 - frame);
                float sillShade = win * Band(du, 0, hw + fm + 0.06, wu) * Band(y, y0 - fm - 0.1, y0 - fm - 0.07, wv);
                float lintel = win * Band(du, 0, hw + fm + 0.03, wu) * Band(y, y1 + fm, y1 + fm + 0.13, wv) * (1 - frame) * resLike * (1 - slate) * (1 - rbox);
                // half-timber: dark beams at every floor and eave, posts at the bay edges and beside the windows,
                // braces across the corner bays
                float beamH = Pulse(v + 0.105, 3.0, 0.21, wv) + Band(y, y0 - fm - 0.195, y0 - fm - 0.045, wv) * (1 - glass);
                float post = Pulse(u + 0.03 * bay, bay, 0.06 * bay, wu) + Band(du, hw + fm, hw + fm + 0.11, wu);
                float corner = step(u, bay) + step(wlen - bay, u);
                float brace = corner * (Band(fu - fv, -0.035, 0.035, wfu + wfv) + Band(fu + fv - 1, -0.035, 0.035, wfu + wfv)) * (1 - frame);
                float beams = timber * okFloor * enabled * saturate(beamH + post + brace) * (1 - frame);
                wall = lerp(wall, float3(0.05, 0.035, 0.025), beams);

                // plinth, historic stone details, grime near the ground
                float3 STONE = float3(0.8, 0.78, 0.72);
                float abovePlinth = step(0.9, rel);
                float cornice = historic * max(Pulse(v + 0.135, 3.0, 0.135, wv), step(weave - 0.4, v)) * abovePlinth;
                float quoin = historic * max(step(u, 0.7), step(wlen - 0.7, u)) * Pulse(v, 0.7, 0.35, wv) * abovePlinth;
                float3 PLINTH = float3(0.16, 0.147, 0.125) * lerp(0.85, 1.15, plasterL);  // #706B63 stone
                wall = lerp(wall, PLINTH, step(rel, max(i.d.w, 0.2)) * (1 - glassK) * (1 - historic));
                wall = lerp(wall, float3(0.42, 0.4, 0.37), historic * step(rel, 0.9));
                wall = lerp(wall, STONE, saturate(cornice + quoin));
                wall = lerp(wall, float3(0.1, 0.09, 0.08), (1 - glassK) * saturate(1 - rel / 1.6) * 0.12);
                // weathering, each house its own amount: rain splash at the foot, soot under the eave, streaks
                // running down from the sills
                float dirt = _Grime * lerp(0.3, 1.0, Hash(float2(wlen * 1.7, weave * 3.1))) * (1 - glassK) * (1 - hall - store);
                float splash = saturate(1 - rel / 0.9);
                float eaveBand = saturate(1 - (weave - v) / 0.6) * step(v, weave + 0.05) * resLike;
                float streakFade = saturate(1 - (y0 - fm - y) / 1.3) * step(y, y0 - fm - 0.08);
                float streakN = lerp(Hash(float2(floor(u * 11.0), fl + 0.5)), 0.5, saturate(wu * 22.0 - 0.5));  // 9 cm streaks, averaged far off
                float streak = win * Band(du, 0, hw * 0.9, wu) * streakFade * lerp(0.3, 1.0, streakN);
                wall *= 1 - saturate(dirt) * (0.3 * splash + 0.22 * eaveBand + 0.2 * streak);

                // school ribbons, hall clerestory, shop fronts, glass fronts, doors
                float sFrame = school * okFloor * Band(fv, 0.26, 0.86, wfv) * Band(u, 0.4, wlen - 0.4, wu);
                float sGlass = sFrame * Band(fv, 0.3, 0.82, wfv) * (1 - Pulse(u, 1.35, 0.0675, wu));
                float belowEave = weave - v;
                float hFrame = hall * step(5.0, weave - wbot) * Band(belowEave, 1.2, 2.4, wv) * Band(u, 0.5, wlen - 0.5, wu);
                float hGlass = hFrame * Band(belowEave, 1.27, 2.33, wv) * (1 - Pulse(u, 2.0, 0.08, wu));
                float edgeOk = Band(u, 0.25, wlen - 0.25, wu);
                float sbay = wlen / max(floor(wlen / 3.4), 1.0);
                float shFrame = shop * Band(rel, 0.3, 2.95, wv) * edgeOk;
                float shGlass = shFrame * (1 - Pulse(u + 0.06 * sbay, sbay, 0.12 * sbay, wu)) * Band(rel, 0.4, 2.85, wv);
                float sign = shop * Band(rel, 3.05, 3.6, wv) * edgeOk;
                float gMull = glassK * max(Pulse(u, 1.5, 0.06, wu), Pulse(v, 3.0, 0.09, wv));
                float top = glassK * step(weave - 1.1, v);
                float gBase = glassK * step(rel, 0.35);
                float gGlass = glassK * (1 - gMull) * (1 - top) * (1 - gBase);
                float houseT = step(abs(doorT - 1), 0.4), garT = step(abs(doorT - 2), 0.4), wideT = step(2.5, doorT);
                float rd = rel - doorBase;  // doors stand on the ground in front, house doors on their steps
                float houseD = houseT * Band(dx, -1, 0.55, wu) * Band(rd, 0, 2.25, wv);
                float houseFr = houseT * Band(dx, -1, 0.63, wu) * Band(rd, 0, 2.33, wv);
                float houseGl = houseD * Band(dx, -1, 0.12, wu) * Band(rd, 0.9, 1.9, wv);
                float garage = garT * Band(dx, -1, 1.25, wu) * Band(rd, 0, 2.1, wv);
                float garageFr = garT * Band(dx, -1, 1.33, wu) * Band(rd, 0, 2.18, wv);
                float wide = wideT * Band(dx, -1, 1.5, wu) * Band(rd, 0, 2.6, wv);
                float wideFr = wideT * Band(dx, -1, 1.62, wu) * Band(rd, 0, 2.72, wv);
                float wideGl = wide * Band(dx, 0.03, 9, wu);

                static const float3 SIGNS[6] = { float3(0.55, 0.03, 0.03), float3(0.04, 0.1, 0.35), float3(0.06, 0.06, 0.06),
                                                 float3(0.04, 0.25, 0.1), float3(0.65, 0.4, 0.04), float3(0.85, 0.85, 0.85) };
                static const float3 DOORS[5] = { float3(0.8, 0.8, 0.78), float3(0.1, 0.1, 0.11), float3(0.28, 0.16, 0.08),
                                                 float3(0.08, 0.18, 0.12), float3(0.4, 0.07, 0.05) };
                float3 GLASS = float3(0.03, 0.045, 0.055);
                static const float3 FRAMES[4] = { float3(0.86, 0.86, 0.84), float3(0.6, 0.6, 0.58), float3(0.03, 0.03, 0.03),
                                                  float3(0.15, 0.15, 0.14) };
                float glassAll = saturate(glass + sGlass + hGlass + shGlass + houseGl + gGlass + wideGl) * (1 - roller);
                float3 col = wall;
                col = lerp(col, min(wall * 1.18 + 0.03, 1.0), lintel * (1 - beams));
                col = lerp(col, float3(0.62, 0.61, 0.58), sill * (1 - beams));
                col = lerp(col, col * 0.55, sillShade * (1 - frame));
                col = lerp(col, FRAMES[(int)clamp(frameIdx, 0, 3)], frame);
                col = lerp(col, float3(0.031, 0.107, 0.04), shutter * (1 - frame));  // #315C38
                col = lerp(col, float3(0.55, 0.56, 0.58), sFrame);
                col = lerp(col, float3(0.35, 0.36, 0.37), hFrame);
                col = lerp(col, float3(0.12, 0.12, 0.13), shFrame);
                col = lerp(col, SIGNS[(int)clamp(scol, 0, 5)], sign);
                col = lerp(col, float3(0.8, 0.8, 0.78), houseFr);
                col = lerp(col, DOORS[(int)clamp(dcol, 0, 4)], houseD);
                col = lerp(col, float3(0.7, 0.7, 0.7), garageFr);
                col = lerp(col, float3(0.55, 0.56, 0.55) * (0.85 + 0.15 * (1 - Pulse(rd, 0.12, 0.06, wv))), garage);
                col = lerp(col, float3(0.25, 0.25, 0.26), wideFr);
                col = lerp(col, float3(0.18, 0.18, 0.19), gMull);
                col = lerp(col, float3(0.55, 0.01, 0.02), top * bandOn);
                col = lerp(col, float3(0.22, 0.22, 0.23), top * (1 - bandOn));
                col = lerp(col, float3(0.1, 0.1, 0.1), gBase);
                col = lerp(col, GLASS, glassAll);
                col = lerp(col, float3(0.26, 0.26, 0.25) * (0.8 + 0.2 * (1 - Pulse(y, 0.06, 0.03, wv))), roller);
                col = lerp(col, FRAMES[(int)clamp(frameIdx, 0, 3)] * 0.92, rbox);

                // ---- relief: plaster and brick normal maps, slate scales tilting out at their lower edge; the
                // tangent runs level along the wall, the bitangent straight up ----
                float3 n = normalize(i.normalWS);
                float3 T = cross(float3(0, 1, 0), n);
                T = dot(T, T) < 1e-4 ? float3(1, 0, 0) : normalize(T);
                float3 tsP = UnpackNormal(SAMPLE_TEXTURE2D(_PlasterNormal, sampler_PlasterNormal, float2(u, v) / _PlasterSize));
                float3 tsB = UnpackNormal(SAMPLE_TEXTURE2D(_BrickNormal, sampler_BrickNormal, float2(u, v) / _BrickSize));
                float3 ts = lerp(tsP, tsB, saturate(brick * (1 - slate) * (1 - timber)));
                ts = lerp(ts, normalize(float3(0, (0.6 - f.y) * (1 - slateFar), 1)), slate);
                float flatten = saturate(glassAll + frame + shFrame + gMull + houseFr + garageFr + wideFr + sign + rbox);
                ts.xy *= _Relief * (1 - flatten);
                n = normalize(T * ts.x + float3(0, 1, 0) * ts.y + n * ts.z);

                // ---- light: sun with shadows, sky ambient, reflections in the glass ----
                float3 viewDir = GetWorldSpaceNormalizeViewDir(i.positionWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float ndl = saturate(dot(n, sun.direction));
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                float3 lit = col * (sun.color * sun.shadowAttenuation * ndl * ao.directAmbientOcclusion
                                    + SampleSH(n) * ao.indirectAmbientOcclusion);
                float nv = saturate(dot(n, viewDir));
                float fresnel = 0.04 + 0.96 * pow(1 - nv, 5);
                float3 refl = GlossyEnvironmentReflection(reflect(-viewDir, n), i.positionWS, 0.08, 1.0);
                float3 h = normalize(sun.direction + viewDir);
                float spec = pow(saturate(dot(n, h)), 180) * sun.shadowAttenuation;
                lit += glassAll * (refl * lerp(0.25, 1.0, fresnel) + sun.color * spec * 2.0);
                lit += (1 - glassAll) * sun.color * pow(saturate(dot(n, h)), 24) * sun.shadowAttenuation * ndl * 0.04;
                // at night some rooms are lit, and the shops
                float roomLit = step(0.45, Hash(winId + 11.3)) * glass * (1 - roller) + 0.8 * shGlass;
                lit += _Night * roomLit * float3(1.0, 0.72, 0.42) * lerp(0.9, 1.6, Hash(winId + 5.1));
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
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 geo2 : TEXCOORD2; };
            float4 vert(A i) : SV_POSITION
            {
                return GetVertexPositionInputs(Pushed(i.positionOS.xyz, i.normalOS, i.geo2.y)).positionCS;
            }
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
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 geo2 : TEXCOORD2; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            V vert(A i)
            {
                V o;
                o.positionCS = GetVertexPositionInputs(Pushed(i.positionOS.xyz, i.normalOS, i.geo2.y)).positionCS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }
            half4 frag(V i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack Off
}
