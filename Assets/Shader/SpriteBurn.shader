Shader "Custom/SpriteBurn"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0

        [Header(Burn)]
        _BurnColor1 ("Ember Color", Color) = (0.55, 0.06, 0.01, 1)
        _BurnColor2 ("Flame Color", Color) = (1, 0.32, 0.04, 1)
        _BurnColor3 ("Core Color", Color) = (1, 0.85, 0.4, 1)
        _BurnIntensity ("Burn Intensity", Range(0, 2)) = 1.05
        _BurnSpeed ("Burn Speed", Range(0, 5)) = 0.85
        _BurnScale ("Burn Scale", Range(1, 30)) = 6
        _BurnPower ("Burn Power", Range(0.5, 5)) = 2.2
        _BurnEdgeGlow ("Edge Glow", Range(0, 2)) = 0.55
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

            fixed4 _BurnColor1;
            fixed4 _BurnColor2;
            fixed4 _BurnColor3;
            float _BurnIntensity;
            float _BurnSpeed;
            float _BurnScale;
            float _BurnPower;
            float _BurnEdgeGlow;

            float hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
            }

            float noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(hash(i), hash(i + float2(1.0, 0.0)), f.x),
                    lerp(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), f.x),
                    f.y);
            }

            float fbm(float2 p)
            {
                float v = 0.0;
                float amp = 0.5;
                for (int i = 0; i < 3; i++)
                {
                    v += amp * noise(p);
                    p = p * 2.03 + float2(17.3, 9.1);
                    amp *= 0.5;
                }
                return v;
            }

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
                float alpha = c.a;
                float2 uv = IN.texcoord;

                float t = _Time.y * _BurnSpeed;

                // 从底部向上翻涌的火焰，两层噪声错开方向形成 flicker
                float2 p = uv * _BurnScale + float2(0.0, -t * 1.9);
                float n = fbm(p);
                float n2 = noise(p * 1.7 + float2(0.0, -t * 0.7));

                float rise = 1.0 - uv.y;
                float flame = saturate(rise * 1.25 + (n - 0.5) * 1.35 - 0.12);
                flame = pow(flame, _BurnPower);

                float flicker = 0.62 + 0.38 * n2;
                float burn = flame * flicker;

                // 卡片外圈余烬，做成 Balatro 燃烧牌的边框感
                float2 edge = abs(uv - 0.5);
                float border = 1.0 - smoothstep(0.40, 0.5, max(edge.x, edge.y));
                border *= 0.65 + 0.35 * noise(uv * _BurnScale * 0.7 + float2(0.0, -t));

                float glow = saturate(burn + border * _BurnEdgeGlow) * _BurnIntensity;

                fixed3 fire = lerp(_BurnColor1.rgb, _BurnColor2.rgb, saturate(flame * 1.9));
                fire = lerp(fire, _BurnColor3.rgb, saturate(pow(flame, 1.6) * 1.35));

                c.rgb += fire * glow;
                c.a = alpha;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
