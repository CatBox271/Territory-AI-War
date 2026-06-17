Shader "Custom/SpriteInnerStroke"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0

        _StrokeWidth ("Stroke Width", Range(0, 20)) = 3
        _StrokeColor ("Stroke Color", Color) = (1,1,1,1)
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
            #pragma target 2.0
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
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            fixed4 _StrokeColor;
            float _StrokeWidth;

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
                float alphaCenter = c.a;

                // Skip transparent pixels
                if (alphaCenter < 0.1) { c.rgb *= c.a; return c; }

                float2 texel = _MainTex_TexelSize.xy;

                float minNeighbor = 1.0;

                // Sample at 3 distances for a thicker stroke band
                // Distance 1/3 of stroke width
                {
                    float2 off = texel * _StrokeWidth * 0.33;
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, -off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(off.x, 0)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(-off.x, 0)).a);
                }
                // Distance 2/3 of stroke width
                {
                    float2 off = texel * _StrokeWidth * 0.66;
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, -off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(off.x, 0)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(-off.x, 0)).a);
                }
                // Distance 1x of stroke width
                {
                    float2 off = texel * _StrokeWidth;
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(0, -off.y)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(off.x, 0)).a);
                    minNeighbor = min(minNeighbor, tex2D(_MainTex, IN.texcoord + float2(-off.x, 0)).a);
                }

                // If any neighbor is transparent, we're inside the stroke zone
                float strokeFactor = step(0.5, alphaCenter) * (1.0 - step(0.15, minNeighbor));

                // Blend stroke color (independent of sprite tint)
                c.rgb = lerp(c.rgb, _StrokeColor.rgb, strokeFactor * _StrokeColor.a);
                c.a = max(c.a, strokeFactor * _StrokeColor.a);

                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
