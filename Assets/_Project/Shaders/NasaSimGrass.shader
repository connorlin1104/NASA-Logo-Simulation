// Blades of grass for the mowable field (driven by MowableGrass.cs). Hand-written HLSL, like the water
// shader next door, because everything here is a few lines of vertex maths and no texture assets.
//
// THE MOW is a vertex-stage lookup, not a mesh edit: every blade samples the global mow mask
// (_NasaMowMask, a CPU-painted R8 texture covering the field) at ITS OWN BASE, and shrinks toward
// _MownHeight where the tractor has been. That is what makes cutting a 42 m field free — the meshes are
// built once and never touched again, and a mown swath appears the instant the mask texel is painted.
//
// Each vertex therefore carries its blade apart in pieces so the shader can rebuild it at any height:
//   POSITION   the rest-pose vertex (so bounds, the scene view and any fallback shader still work)
//   TEXCOORD1  the STEM offset — base -> this vertex along the blade's centre line (height + lean)
//   TEXCOORD2  the SIDE offset — the horizontal half-width vector (x, z)
// so base = POSITION - stem - side, and a mown blade is base + stem * squash + side: shorter, but still
// as wide, which is what cut grass actually looks like. Bending only the stem also keeps the wind sway
// and the astronaut's push-aside from fanning the blade out sideways.
//
// Animation runs on _NasaGrassTime, fed from Time.realtimeSinceStartup by MowableGrass — Unity's builtin
// _Time is multiplied by Time.timeScale, and the sim fast-forwards the mow to 16x, where a wind that
// tracked it would look like a hurricane. Grass is scenery the player stands in, so it runs in real time.
Shader "NasaSim/Grass"
{
    Properties
    {
        // These three are the colours of the BRIGHTEST blades: every blade carries a per-blade tint in its
        // vertex colour that can only darken (a Color32 cannot hold a multiplier above 1), which is what
        // stops a dense field reading as one flat sheet. Expect the field to average ~80% of the swatch.
        _RootColor("Root Color", Color) = (0.11, 0.24, 0.08, 1)
        _TipColor("Tip Color", Color) = (0.46, 0.76, 0.26, 1)
        _MownColor("Mown Color", Color) = (0.68, 0.82, 0.35, 1)
        _MownHeight("Mown Height (fraction of blade)", Range(0.02, 1)) = 0.16
        _MownBleach("Mown Bleach", Range(0, 1)) = 0.75

        _WindDir("Wind Direction (x, z)", Vector) = (0.85, 0.53, 0, 0)
        _WindStrength("Wind Strength (m)", Float) = 0.06
        _WindSpeed("Wind Speed", Float) = 1.5
        _WindWaveLength("Wind Wave Length (m)", Float) = 3.5

        _PushStrength("Push Aside Strength", Range(0, 2)) = 1.0
        _RootShading("Root Shading", Range(0, 1)) = 0.45
        _Translucency("Backlight", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "GrassForward"
            Tags { "LightMode" = "UniversalForward" }

            // Blades are single quads: both sides must draw. There is deliberately no ShadowCaster pass —
            // 70k blades self-shadowing costs far more than the look is worth, and the field is lit by one
            // directional light. Depth is still written, and the renderer copies depth after opaques, so
            // the water's refraction and any depth effects see the grass correctly.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _RootColor;
                half4  _TipColor;
                half4  _MownColor;
                float  _MownHeight;
                float  _MownBleach;
                float4 _WindDir;
                float  _WindStrength;
                float  _WindSpeed;
                float  _WindWaveLength;
                float  _PushStrength;
                float  _RootShading;
                float  _Translucency;
            CBUFFER_END

            // ---- globals, all fed by MowableGrass (outside UnityPerMaterial: they are not per-material) ----
            TEXTURE2D(_NasaMowMask);
            SAMPLER(sampler_NasaMowMask);
            float4 _NasaMowField;      // xy = field min (world x, z), zw = 1 / field size
            float  _NasaGrassTime;     // seconds, UNSCALED (see the header)
            float4 _NasaGrassPusher;   // xyz = walker's world position, w = radius (0 = nobody about)

            struct Attributes
            {
                float4 positionOS : POSITION;    // rest-pose vertex
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;       // rgb = per-blade tint, a = per-blade wind phase
                float2 uv         : TEXCOORD0;   // x = across the blade, y = 0 at the root, 1 at the tip
                float3 stem       : TEXCOORD1;   // base -> vertex along the centre line
                float2 side       : TEXCOORD2;   // horizontal half-width vector (x, z)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                half4  tintCut    : TEXCOORD1;   // rgb = blade tint, a = how mown this blade is
                float2 uv         : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings o;

                float3 sideOS = float3(IN.side.x, 0.0, IN.side.y);
                float3 baseOS = IN.positionOS.xyz - IN.stem - sideOS;

                // MowableGrass builds every chunk with identity rotation and unit scale, so an object-space
                // DIRECTION is already a world-space direction — only the base point needs transforming,
                // and the stem can be bent below in plain world axes.
                float3 baseWS = TransformObjectToWorld(baseOS);

                float2 maskUV = (baseWS.xz - _NasaMowField.xy) * _NasaMowField.zw;
                half   cut    = saturate(SAMPLE_TEXTURE2D_LOD(_NasaMowMask, sampler_NasaMowMask, maskUV, 0).r);

                float  squash = lerp(1.0, _MownHeight, cut);
                float  v      = IN.uv.y;
                float3 stem   = IN.stem * squash;
                float  restY  = IN.stem.y;              // ~ height * v: how much blade there is under this vertex

                // ---- wind: a wave TRAVELLING across the field, so it ripples instead of shivering ----
                float2 wdir  = normalize(_WindDir.xy + float2(1e-5, 0.0));
                float  phase = IN.color.a * 6.2831853 +
                               dot(baseWS.xz, wdir) * (6.2831853 / max(_WindWaveLength, 0.05));
                float  t     = _NasaGrassTime * _WindSpeed;
                float  sway  = sin(t + phase) * 0.7 + sin(t * 1.63 + phase * 1.7) * 0.3;
                // v*v so the root stays planted and only the top half really moves; a mown stub barely does.
                stem.xz += wdir * (sway * _WindStrength * v * v * squash * (1.0 - cut * 0.75));

                // ---- shove aside whoever is walking through ----
                float2 away = baseWS.xz - _NasaGrassPusher.xz;
                float  dist = length(away);
                float  push = _NasaGrassPusher.w > 1e-3 ? saturate(1.0 - dist / _NasaGrassPusher.w) : 0.0;
                push *= push;                                        // tight around the boots, not a crop circle
                float2 pdir = dist > 1e-4 ? away / dist : float2(1.0, 0.0);
                stem.xz += pdir * (push * _PushStrength * restY * squash);
                stem.y  *= 1.0 - push * 0.6;                         // ...and trodden down as well as outward

                float3 positionOS = baseOS + stem + sideOS;
                float3 positionWS = TransformObjectToWorld(positionOS);

                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                o.tintCut    = half4(IN.color.rgb, cut);
                o.uv         = IN.uv;
                o.fogFactor  = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                half  v   = saturate(i.uv.y);
                half  cut = i.tintCut.a;

                half3 albedo = lerp(_RootColor.rgb, _TipColor.rgb, v) * i.tintCut.rgb;
                albedo = lerp(albedo, _MownColor.rgb * i.tintCut.rgb, cut * _MownBleach);
                // Darken toward the root: the cheapest possible contact shading, and the thing that stops a
                // dense field reading as one flat sheet of green.
                albedo *= lerp(1.0 - _RootShading, 1.0, v);

                float3 N = normalize(i.normalWS);
                N = IS_FRONT_VFACE(facing, N, -N);
                // Blades are flat cards at every yaw; lit purely by their own normals the field turns into a
                // patchwork of bright and black slats. Leaning the normal toward straight up shades the field
                // as the surface it reads as, while keeping enough of the blade's own facing to catch the sun.
                N = normalize(lerp(N, float3(0.0, 1.0, 0.0), 0.55));

                Light mainLight = GetMainLight();
                half  ndl = saturate(dot(N, mainLight.direction));
                half3 lit = albedo * mainLight.color * (ndl * 0.65 + 0.35);   // wrapped: no dead-black backs

                // Sun THROUGH the blade — the glow that sells thin foliage, strongest at the thin tips.
                half back = saturate(dot(-N, mainLight.direction));
                lit += albedo * mainLight.color * (back * back * _Translucency * v);

                lit += albedo * SampleSH(N);

                lit = MixFog(lit, i.fogFactor);
                return half4(lit, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
