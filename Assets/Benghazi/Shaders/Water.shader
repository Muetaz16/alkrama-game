// Mediterranean sea for the Corniche: two scrolling normal maps for the waves, sky reflection with
// fresnel, sun glitter, colour by real depth (turquoise in the shallows, deep blue further out),
// foam where the water meets the rocks and the sea wall, soft edge, fog. URP forward, transparent.
Shader "Benghazi/Water"
{
    Properties
    {
        _ShallowColor ("Shallow", Color) = (0.16, 0.62, 0.62, 1)
        _DeepColor ("Deep", Color) = (0.02, 0.17, 0.28, 1)
        _DepthFade ("Depth to deep (m)", Float) = 6
        [Normal][NoScaleOffset] _WaveNormal ("Wave normals", 2D) = "bump" {}
        _WaveScale ("Wave size (m)", Float) = 9
        _WaveStrength ("Wave strength", Range(0, 2)) = 0.7
        _WaveSpeed ("Wave speed", Float) = 0.35
        _FoamColor ("Foam", Color) = (0.95, 0.97, 0.97, 1)
        _FoamDistance ("Foam width (m)", Float) = 0.9
        _Glitter ("Sun glitter", Float) = 5
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_WaveNormal); SAMPLER(sampler_WaveNormal);
            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor, _DeepColor, _FoamColor;
                float _DepthFade, _WaveScale, _WaveStrength, _WaveSpeed, _FoamDistance, _Glitter;
            CBUFFER_END
            // A man in the water (set by PlayerController): xyz where he is, w how far in he is (0 none .. 1 swimming).
            // The depth buffer sees his body just under the surface and would call it a sand bar - turquoise and
            // ringed with foam. Round him the water keeps its open-sea colour, with only a little wash of foam,
            // and stays clear enough to see him through it.
            float4 _Swimmer;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.screenPos = ComputeScreenPos(o.positionCS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half3 Waves(float2 xz, float t)
            {
                // two layers moving in different directions, a third, larger swell
                float2 uv1 = xz / _WaveScale + float2(0.11, 0.07) * t;
                float2 uv2 = xz / (_WaveScale * 0.63) + float2(-0.06, 0.13) * t;
                float2 uv3 = xz / (_WaveScale * 3.7) + float2(0.03, -0.02) * t;
                half3 a = UnpackNormal(SAMPLE_TEXTURE2D(_WaveNormal, sampler_WaveNormal, uv1));
                half3 b = UnpackNormal(SAMPLE_TEXTURE2D(_WaveNormal, sampler_WaveNormal, uv2));
                half3 c = UnpackNormal(SAMPLE_TEXTURE2D(_WaveNormal, sampler_WaveNormal, uv3));
                half2 d = (a.xy + b.xy) * _WaveStrength + c.xy * (_WaveStrength * 0.8);
                return normalize(half3(d.x, 1.0, d.y));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y * _WaveSpeed;
                half3 n = Waves(i.positionWS.xz, t);
                half3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);

                // how deep the water is here, from the depth buffer (sea floor / rocks / wall below)
                float2 suv = i.screenPos.xy / i.screenPos.w;
                float sceneZ = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                float depth = max(0, sceneZ - i.screenPos.w);

                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half shadow = lerp(0.55, 1.0, sun.shadowAttenuation);

                // body colour: shallow turquoise -> deep blue, lit by sun and sky
                half deep = saturate(depth / _DepthFade);
                half nearMan = saturate(1 - distance(i.positionWS.xz, _Swimmer.xz) / 3.2) * _Swimmer.w;
                nearMan *= nearMan * (3 - 2 * nearMan);
                deep = lerp(deep, max(deep, 0.8), nearMan);
                half3 body = lerp(_ShallowColor.rgb, _DeepColor.rgb, deep * deep * (3 - 2 * deep));
                half3 ambient = SampleSH(half3(0, 1, 0));
                body *= ambient * 0.9 + sun.color * saturate(sun.direction.y) * 0.55 * shadow;

                // sky reflection with fresnel
                half fres = 0.02 + 0.98 * pow(1 - saturate(dot(n, v)), 5);
                half3 refl = GlossyEnvironmentReflection(reflect(-v, n), 0.06, 1);
                half3 col = lerp(body, refl, saturate(fres * 1.1));

                // sun glitter
                half3 h = normalize(sun.direction + v);
                half spec = pow(saturate(dot(n, h)), 700) * _Glitter + pow(saturate(dot(n, h)), 90) * 0.25;
                col += sun.color * spec * shadow;

                // foam at the shore and round the rocks, broken up by the waves
                half foamLine = 1 - saturate(depth / _FoamDistance);
                half breakup = saturate((n.x + n.z) * 3 + 0.55);
                half foam = saturate(foamLine * 1.6 - (1 - breakup) * 0.8);
                foam *= 1 - nearMan * 0.82;                                   // a wash round him, not a white ring
                col = lerp(col, _FoamColor.rgb * (ambient + sun.color * 0.6 * shadow), foam);

                // see-through in the shallows, solid further out
                half alpha = saturate(lerp(0.55, 1.0, deep) + fres * 0.4 + foam);
                alpha = lerp(alpha, saturate(0.8 + fres * 0.3 + foam), nearMan);     // he shows through the water he is in
                alpha *= saturate(depth * 4);
                col = MixFog(col, i.fogFactor);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
