// Unlit effects for the HDRP build: muzzle flash, tracers, smoke, dust, sparks, the red dot, glass cracks.
// The same thing as "Sprites/Default" (texture x vertex colour x tint, premultiplied alpha, no depth write),
// with one addition: HDRP keeps the picture in camera-exposed values, and a plain unlit shader does not know
// the exposure - at a bright exposure its colours would come out dim against the scene, at a dark one too
// bright. TimeOfDay publishes the exposure multiplier as _BenghaziFxGain and the colour is scaled by it, so a
// puff of dust or a flash sits in the picture the way it did under URP.
Shader "Benghazi/Fx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color;
            float _BenghaziFxGain;

            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = tex2D(_MainTex, i.uv) * i.color;
                float gain = _BenghaziFxGain > 0.00001 ? _BenghaziFxGain : 1.0;      // not set yet: as Sprites/Default
                c.rgb *= c.a * gain;
                return c;
            }
            ENDCG
        }
    }
}
