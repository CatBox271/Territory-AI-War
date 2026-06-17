Shader "Custom/SpriteRing"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0

        _RingRadius ("Ring Radius", Range(0, 0.5)) = 0.35
        _RingThickness ("Ring Thickness", Range(0, 0.1)) = 0.02
        _RingColor ("Ring Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment SpriteFrag
            #pragma multi_compile _ PIXELSNAP_ON

            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _RingColor;
            float _RingRadius;
            float _RingThickness;

            v2f SpriteVert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _Color;
#ifdef PIXELSNAP_ON
                OUT.vertex = UnityPixelSnap(OUT.vertex);
#endif
                return OUT;
            }

            fixed4 SpriteFrag(v2f IN) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, IN.texcoord) * IN.color;

                // Distance from sprite center
                float dist = length(IN.texcoord - float2(0.5, 0.5));

                float halfThick = _RingThickness * 0.5;
                float innerR = _RingRadius - halfThick;
                float outerR = _RingRadius + halfThick;

                // Smooth ring mask
                float ring = smoothstep(innerR - 0.005, innerR, dist)
                           * (1.0 - smoothstep(outerR, outerR + 0.005, dist));

                // Blend ring color (independent of sprite tint)
                c.rgb = lerp(c.rgb, _RingColor.rgb, ring * _RingColor.a);
                c.a = max(c.a, ring * _RingColor.a);

                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
