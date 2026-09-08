Shader "Custom/SpriteRainbowFlow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _RainbowStrength ("着色强度", Range(0, 1)) = 0.55
        _RainbowSpeed ("底色流转", Range(0, 2)) = 0.35
        _RainbowDensity ("波纹密度", Range(1, 30)) = 6
        _ShineStrength ("高光强度", Range(0, 1)) = 0.25
        _PatternSpeed ("图斑变化速度", Range(0, 2)) = 0.6
        _ColorFlow ("颜色流数", Range(0, 20)) = 2.5
        _Angle ("炫光角度", Range(0, 6.2831853)) = 0
        _Seed ("随机种子", Range(0, 10)) = 0
        _Impact ("色彩对撞", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma fragmentoption ARB_precision_hint_fastest
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _RainbowStrength;
            float _RainbowSpeed;
            float _RainbowDensity;
            float _ShineStrength;
            float _PatternSpeed;
            float _ColorFlow;
            float _Angle;
            float _Seed;
            float _Impact;

            // 移植 Balatro 多彩卡的多彩效果：直接对原图做 HSV 色相旋转，
            // 而不是往原图上平铺彩虹条带
            fixed3 RgbToHsv(fixed3 c)
            {
                fixed4 K = fixed4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                fixed4 p = lerp(fixed4(c.bg, K.wz), fixed4(c.gb, K.xy), step(c.b, c.g));
                fixed4 q = lerp(fixed4(p.xyw, c.r), fixed4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1e-10;
                return fixed3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            fixed3 HsvToRgb(fixed3 c)
            {
                fixed3 p = abs(frac(c.xxx + fixed3(0.0, 1.0 / 3.0, 2.0 / 3.0)) * 6.0 - 3.0);
                return c.z * lerp(fixed3(1.0, 1.0, 1.0), saturate(p - 1.0), c.y);
            }

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 baseCol = tex2D(_MainTex, i.uv) * i.color;
                if (baseCol.a <= 0.001) return baseCol;

                float2 p = i.uv - 0.5;
                float radius = length(p);
                float t = _Time.y;

                // Balatro 多彩基础：整张图色相旋转 + 径向脉冲
                float pulse = sin(t * (1.9 + _Seed) + radius * (8.0 + _RainbowDensity)) * 0.5 + 0.5;
                fixed3 hsv = RgbToHsv(baseCol.rgb);
                float hueOffset = t * _RainbowSpeed + _Angle * 0.25 + radius * 0.28 + _Seed * 0.8;
                hsv.x = frac(hsv.x + hueOffset);
                hsv.y = clamp(hsv.y * (1.15 + pulse * 0.25), 0.0, 1.0);
                fixed3 outCol = lerp(baseCol.rgb, HsvToRgb(hsv), _RainbowStrength);

                // 图灵斑图：用三个方向叠加的波动做反应扩散式亮斑，不再是圆环/漩涡
                float tp = t * _PatternSpeed;
                float2 q = p * (3.0 + _RainbowDensity);
                float n1 = sin(q.x * 1.15 + sin(q.y * 1.35 + tp * 0.9));
                float n2 = cos(q.y * 1.25 + sin(q.x * 0.85 - tp * 0.7));
                float n3 = sin((q.x + q.y) * 0.8 + sin(q.x - q.y) * 0.55 + tp * 0.5);
                float n = (n1 + n2 + n3) / 3.0;

                float breathe = 0.12 * sin(tp * 1.3);
                float blob = smoothstep(breathe, breathe + 0.55, n);
                float hue = frac(n * _ColorFlow + _Seed * 0.31);

                // 斑块之间切补色，硬边对撞
                float clash = smoothstep(0.4, 0.6, frac(n * 1.7 + tp * 0.5));
                fixed3 colA = HsvToRgb(fixed3(hue, 0.95, 1.25));
                fixed3 colB = HsvToRgb(fixed3(frac(hue + 0.5), 0.95, 1.25));
                fixed3 cd = lerp(colA, colB, lerp(0.5, clash, _Impact));

                float edge = 1.0 - smoothstep(0.0, 0.1, abs(n - (breathe + 0.28)));
                float bright = smoothstep(0.55, 0.9, n);

                float cdMix = saturate(blob * (0.4 + 0.6 * _ShineStrength));
                outCol = lerp(outCol, cd, cdMix * 0.85);
                outCol += cd * edge * _ShineStrength * 0.5;
                outCol += cd * bright * _ShineStrength * 0.4;

                outCol += fixed3(0.04, 0.03, 0.06) * pulse * (0.2 + _ShineStrength * 0.2);
                return fixed4(outCol, baseCol.a);
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}