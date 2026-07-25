// Stylised-realistic water for URP (Forward+). Hand-written HLSL on purpose: authoring .shadergraph
// JSON by hand is fragile, and everything this needs — scene depth, the opaque texture, analytic waves —
// is a few lines each. Requires Depth Texture + Opaque Texture ON in the URP asset (PC_RPAsset has both).
//
// Animation is driven by _WaterTime, fed by WaterSurface.cs from Time.unscaledTime. Unity's builtin
// _Time is scaled by Time.timeScale, and at the sim's 16x fast-forward the moat would froth comically —
// water is scenery the player watches, so it runs in real time.
Shader "NasaSim/Water"
{
    Properties
    {
        _ShallowColor("Shallow Color (a = opacity)", Color) = (0.32, 0.62, 0.68, 0.35)
        _DeepColor("Deep Color (a = opacity)", Color) = (0.03, 0.20, 0.36, 0.92)
        _DepthFade("Depth Fade (m)", Float) = 1.2
        _FoamColor("Foam Color (a = strength)", Color) = (1, 1, 1, 0.65)
        _FoamDepth("Foam Depth (m)", Float) = 0.25
        _WaveAmp("Wave Amplitude (m)", Float) = 0.035
        _WaveFreq("Wave Frequency", Float) = 1.0
        _WaveSpeed("Wave Speed", Float) = 1.0
        _DetailStrength("Ripple Detail", Range(0, 1)) = 0.35
        _RefractionStrength("Refraction", Float) = 0.35
        _Smoothness("Smoothness", Range(0, 1)) = 0.92
        _WaterTime("Water Time (script-fed)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            // Refraction is composited manually from the opaque texture, so alpha is output as 1 —
            // the blend mode is only kept alpha-style for consistency with the transparent queue.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FoamColor;
                float _DepthFade;
                float _FoamDepth;
                float _WaveAmp;
                float _WaveFreq;
                float _WaveSpeed;
                float _DetailStrength;
                float _RefractionStrength;
                float _Smoothness;
                float _WaterTime;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 positionNDC : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
            };

            static const float2 kDir0 = float2( 0.80,  0.60);
            static const float2 kDir1 = float2(-0.62,  0.78);
            static const float2 kDir2 = float2( 0.35, -0.94);

            // Three directional sines summed; the normal comes from the analytic derivatives, so the
            // lighting matches the displacement exactly (no texture assets needed anywhere).
            float WaveHeightAndNormal(float2 xz, out float3 normalWS)
            {
                float t  = _WaterTime * _WaveSpeed;
                float a0 = _WaveAmp, a1 = _WaveAmp * 0.5, a2 = _WaveAmp * 0.25;
                float w0 = 0.9 * _WaveFreq, w1 = 1.6 * _WaveFreq, w2 = 2.6 * _WaveFreq;

                float p0 = dot(kDir0, xz) * w0 + t * 1.00;
                float p1 = dot(kDir1, xz) * w1 + t * 1.35;
                float p2 = dot(kDir2, xz) * w2 + t * 1.70;

                float h    = a0 * sin(p0) + a1 * sin(p1) + a2 * sin(p2);
                float dhdx = a0 * w0 * kDir0.x * cos(p0) + a1 * w1 * kDir1.x * cos(p1) + a2 * w2 * kDir2.x * cos(p2);
                float dhdz = a0 * w0 * kDir0.y * cos(p0) + a1 * w1 * kDir1.y * cos(p1) + a2 * w2 * kDir2.y * cos(p2);
                normalWS = normalize(float3(-dhdx, 1.0, -dhdz));
                return h;
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

                float3 n;
                positionWS.y += WaveHeightAndNormal(positionWS.xz, n);

                o.positionWS = positionWS;
                o.normalWS = n;
                o.positionCS = TransformWorldToHClip(positionWS);

                // Screen-space NDC (same math as GetVertexPositionInputs, but for the displaced position).
                float4 ndc = o.positionCS * 0.5;
                ndc.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
                ndc.zw = o.positionCS.zw;
                o.positionNDC = ndc;

                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.positionNDC.xy / i.positionNDC.w;
                float waterEye = i.positionNDC.w;                 // eye-space depth of this water pixel

                // Fragment-level micro-ripples for sparkle: two more analytic sine gradients, no textures.
                float t = _WaterTime * _WaveSpeed;
                float2 xz = i.positionWS.xz;
                float d0 = cos(dot(xz, float2( 7.3, 5.1)) + t * 2.4);
                float d1 = cos(dot(xz, float2(-5.7, 8.2)) + t * 3.1);
                float3 N = normalize(i.normalWS + float3(d0, 0.0, d1) * (0.06 * _DetailStrength));

                // Refraction: distort the opaque-texture UV by the ripple normal, but reject the
                // distorted sample if what it lands on is IN FRONT of the water (the classic leak fix —
                // otherwise the astronaut's boots smear into the pond edge).
                float2 refrUV = uv + N.xz * (_RefractionStrength / max(waterEye, 1.0));
                float sceneEyeR = LinearEyeDepth(SampleSceneDepth(refrUV), _ZBufferParams);
                float2 finalUV = sceneEyeR > waterEye ? refrUV : uv;

                float sceneEye = LinearEyeDepth(SampleSceneDepth(finalUV), _ZBufferParams);
                float depthDiff = max(sceneEye - waterEye, 0.0);
                half3 sceneCol = SampleSceneColor(finalUV);

                // Depth tint: clear at the banks, deep blue in the middle.
                float depth01 = saturate(depthDiff / max(_DepthFade, 0.01));
                half3 waterTint = lerp(_ShallowColor.rgb, _DeepColor.rgb, depth01);
                float opacity = lerp(_ShallowColor.a, _DeepColor.a, depth01);
                half3 col = lerp(sceneCol, waterTint, opacity);

                // Shoreline foam with a travelling shimmer — the visible "swish" against the banks.
                float foam = 1.0 - saturate(depthDiff / max(_FoamDepth, 0.01));
                foam *= foam;
                float shimmer = 0.65 + 0.35 * sin(t * 3.0 + (xz.x + xz.y) * 9.0);
                col += _FoamColor.rgb * (_FoamColor.a * foam * shimmer);

                // Main-light Blinn specular + fresnel toward the shallow tint (reads as sky reflection).
                Light mainLight = GetMainLight();
                float3 V = normalize(GetWorldSpaceViewDir(i.positionWS));
                float3 H = normalize(mainLight.direction + V);
                float specPow = exp2(_Smoothness * 10.0 + 1.0);
                half3 spec = mainLight.color * (pow(saturate(dot(N, H)), specPow) * _Smoothness);
                float fresnel = pow(1.0 - saturate(dot(N, V)), 4.0);
                col = lerp(col, _ShallowColor.rgb * 1.15 + mainLight.color * 0.05, fresnel * 0.55);
                col += spec;

                col = MixFog(col, i.fogFactor);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
