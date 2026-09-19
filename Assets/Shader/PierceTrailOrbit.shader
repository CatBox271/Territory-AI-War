// 穿甲弹尾迹：中心一条亮线，旁边几条线绕着它转
//   「螺旋扭曲」为 0 时是刚性圆柱公转；调大就拧成螺旋，整束线像麻花一样绕中央
// 用法：新建材质 → Shader 选 Custom/PierceTrailOrbit → 拖到穿甲 Prefab 里 Trail 的 Materials 槽
// 说明：横向坐标取自 UV（默认 UV.y，若线是横着长的就把「横向UV用U轴」勾上）
//       绕轴是数学上的圆柱投影：sin 决定横向位置，cos 决定前后（前面亮、后面暗、后面被中心线挡住）
//       本材质输出预乘 alpha，所以默认混合是 One / OneMinusSrcAlpha；想要发光叠加就改成 One / One
Shader "Custom/PierceTrailOrbit"
{
    Properties
    {
        _Color ("线条颜色", Color) = (0.55, 0.95, 1.2, 1)
        _CoreColor ("中心线颜色", Color) = (1.8, 2.2, 2.6, 1)

        _Count ("线条数量", Range(1, 12)) = 6
        _Radius ("绕轴半径（占半宽的比例）", Range(0, 1)) = 0.34
        _Width ("线条粗细", Range(0.005, 0.6)) = 0.07
        _Softness ("线条柔和度", Range(0, 4)) = 1

        _Spin ("转速（圈/秒）", Float) = 0.6
        _Phase ("相位偏移（圈）", Float) = 0
        _Twist ("螺旋扭曲（每单位UV的圈数，0=刚性公转）", Float) = 1.0

        _BackDim ("背面亮度", Range(0, 1)) = 0.25
        _BackAlpha ("背面不透明度", Range(0, 1)) = 0.4

        _CoreWidth ("中心线粗细", Range(0.005, 0.6)) = 0.055
        _GlowWidth ("中心线辉光宽度", Range(0.01, 1)) = 0.3
        _GlowStrength ("中心线辉光强度", Range(0, 3)) = 0.5

        _Brightness ("整体亮度", Range(0, 4)) = 1

        [Toggle(_ACROSS_U)] _AcrossU ("横向UV用 U 轴（默认用 V 轴）", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("源混合", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("目标混合", Float) = 10
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
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma shader_feature_local _ACROSS_U
            #include "UnityCG.cginc"

            #define PI2 6.28318530718
            #define MAX_LINES 12

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

            float4 _Color;
            float4 _CoreColor;

            float _Count;
            float _Radius;
            float _Width;
            float _Softness;

            float _Spin;
            float _Phase;
            float _Twist;

            float _BackDim;
            float _BackAlpha;

            float _CoreWidth;
            float _GlowWidth;
            float _GlowStrength;

            float _Brightness;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color;
                return o;
            }

            // 一条线的横截面：d 为到线心的横向距离，中心实、两侧软
            float LineMask(float d, float halfW, float soft, float aa)
            {
                float inner = halfW;
                float outer = halfW * (1.0 + soft) + aa;
                return 1.0 - smoothstep(inner, max(outer, inner + 1e-5), d);
            }

            // 返回 float4（不用 fixed4）：中心线和线条亮度可以超过 1，交给 Bloom 吃
            float4 frag(v2f i) : SV_Target
            {
                #if defined(_ACROSS_U)
                    float across = i.uv.x;
                    float along = i.uv.y;
                #else
                    float across = i.uv.y;
                    float along = i.uv.x;
                #endif

                // w：-1 一边 ~ 0 中心线 ~ 1 另一边
                float w = (across - 0.5) * 2.0;
                float aa = max(fwidth(w), 1e-4);

                float count = max(floor(_Count + 0.5), 1.0);
                float halfW = max(_Width, 1e-4) * 0.5;

                // 相位：时间转 + 手动偏移 + 沿长度扭曲（0 时整条尾迹刚性公转）
                float t = _Time.y * _Spin * PI2 + _Phase * PI2 + _Twist * PI2 * along;

                float3 vRGB = i.color.rgb;
                float vA = i.color.a;
                float3 lineTint = _Color.rgb * vRGB;
                float3 coreTint = _CoreColor.rgb * vRGB;

                // 以下颜色都是预乘 alpha
                float3 frontRGB = 0.0;
                float frontA = 0.0;
                float3 backRGB = 0.0;
                float backA = 0.0;

                for (int k = 0; k < MAX_LINES; k++)
                {
                    if ((float)k >= count)
                    {
                        break;
                    }

                    float phi = t + PI2 * (float)k / count;
                    float dep = cos(phi);          // 1 = 正对镜头（在中心线前面），-1 = 在最后面
                    float xo = sin(phi) * _Radius; // 圆柱投影到横向的位置

                    float d = abs(w - xo);
                    float mask = LineMask(d, halfW, _Softness, aa);

                    float face = saturate(dep * 0.5 + 0.5);
                    float3 lineRGB = lineTint * lerp(_BackDim, 1.0, face);
                    float cov = mask * lerp(_BackAlpha, 1.0, face);

                    if (dep >= 0.0)
                    {
                        frontRGB += lineRGB * cov * (1.0 - frontA);
                        frontA += cov * (1.0 - frontA);
                    }
                    else
                    {
                        backRGB += lineRGB * cov * (1.0 - backA);
                        backA += cov * (1.0 - backA);
                    }
                }

                // 中心线：实心核心 + 柔和辉光（辉光偏加法，颜色比透明度多一点）
                float coreHalf = max(_CoreWidth, 1e-4) * 0.5;
                float coreBody = LineMask(abs(w), coreHalf, 0.35, aa);
                float coreGlow = exp(-abs(w) / max(_GlowWidth, 1e-4)) * _GlowStrength;
                float coreCov = saturate(coreBody + coreGlow * 0.6);
                float3 coreRGB = coreTint * (coreBody + coreGlow);

                // 先后面几条线，再中心线压住它们，最后前面的线压住中心线
                float3 col = backRGB;
                float a = backA;
                col = coreRGB + col * (1.0 - coreCov);
                a = coreCov + a * (1.0 - coreCov);
                col = frontRGB + col * (1.0 - frontA);
                a = frontA + a * (1.0 - frontA);

                a = saturate(a);
                col = max(col, 0.0) * _Brightness * vA;
                a *= vA;

                return float4(col, a);
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
