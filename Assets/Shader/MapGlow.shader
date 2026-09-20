// 地图炫光层：一个大 quad 装下所有炫光，全部炫光一次 draw call 画完
// 由 MapGlowLayer.cs 每帧填数组：位置是"相对地图中心的世界坐标"，半径都是世界单位
// 剖面形状来自每个炫光的 AnimationCurve（CPU 烤成 _GlowLUT，一行一个炫光，按归一化距离采样）
// 金属感四项也在这里：
//   A 地图反射：采样背后的地图（_MapTex = 地图 displayRT）取色，光环"映出"地面颜色
//   B 拉丝条纹：绕圆周的放射状细纹，可缓慢流动
//   C 锐利边缘高光：内缘 / 外缘各一条细亮边
//   D 明暗分层：亮度按地图明暗压，暗处不再无脑糊一层亮
// 循环里带包围盒早退，绝大多数像素一次比较就跳过
Shader "Custom/MapGlow"
{
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest LEqual
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma fragmentoption ARB_precision_hint_fastest
            #include "UnityCG.cginc"

            #define MAX_GLOWS 32

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            uniform float4 _GlowA[MAX_GLOWS];   // xy = 相对地图中心的世界坐标，z = 环半径（0 = 点状），w = 曲线外侧映射半径
            uniform float4 _GlowB[MAX_GLOWS];   // x = 曲线内侧映射半径，yzw = 颜色 × 强度（已预乘）
            uniform float4 _GlowC[MAX_GLOWS];   // x = 地图反射强度，y = 明暗分层强度，z = 拉丝强度，w = 拉丝条数
            uniform float4 _GlowD[MAX_GLOWS];   // x = 拉丝流动速度，y = 边缘高光强度，z = 高光宽度(占映射半径)，w = 反射采样偏移
            uniform int _GlowCount;
            uniform float _QuadSize;            // 这个 quad 的世界边长（地图尺寸 + 两边留白）
            uniform float _MapSize;             // 地图的世界边长（用来把世界坐标换算成地图 uv）
            uniform float _LutRows;
            uniform float _LutCols;

            sampler2D _GlowLUT;                 // 剖面曲线：x = 归一化距离，y = 第几个炫光
            sampler2D _MapTex;                  // 背后的地图
            float4 _MapTex_TexelSize;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float3 SampleMap(float2 uv)
            {
                return tex2Dlod(_MapTex, float4(saturate(uv), 0.0, 0.0)).rgb;
            }

            // 地图是 Point 采样的 RT，模糊一圈免得反射色一块一块的
            float3 SampleMapBlur(float2 uv)
            {
                float2 tx = _MapTex_TexelSize.xy * 2.0;
                float3 c = SampleMap(uv) * 2.0;
                c += SampleMap(uv + float2(tx.x, 0.0));
                c += SampleMap(uv - float2(tx.x, 0.0));
                c += SampleMap(uv + float2(0.0, tx.y));
                c += SampleMap(uv - float2(0.0, tx.y));
                return c / 6.0;
            }

            float4 frag(v2f i) : SV_Target
            {
                // 相对地图中心的世界坐标（地图边长一半 = _QuadSize/2）
                float2 p = (i.uv - 0.5) * _QuadSize;

                float3 col = 0;
                float cov = 0;

                for (int k = 0; k < MAX_GLOWS; k++)
                {
                    if (k >= _GlowCount) break;

                    float4 A = _GlowA[k];
                    float4 B = _GlowB[k];
                    float4 C = _GlowC[k];
                    float4 D = _GlowD[k];

                    float outer = max(A.w, 0.0005);   // 曲线往外的映射半径
                    float inner = max(B.x, 0.0005);   // 曲线往内的映射半径（环形用）
                    float2 d = p - A.xy;

                    // 包围盒早退：不在这个炫光范围内就跳过（省算力）
                    float reach = A.z > 0.0001 ? A.z + max(inner, outer) : outer;
                    float extent = reach * 1.05;
                    if (max(abs(d.x), abs(d.y)) > extent) continue;

                    float r = length(d);

                    // 归一化距离：环形 0.5 = 正好在环半径上（内/外各按 inner/outer 映射）；点状 0 = 中心
                    float t;
                    if (A.z > 0.0001)
                        t = r >= A.z ? 0.5 + 0.5 * (r - A.z) / outer
                                     : 0.5 - 0.5 * (A.z - r) / inner;
                    else
                        t = r / outer;
                    t = saturate(t);

                    float u = (t * (_LutCols - 1.0) + 0.5) / _LutCols;
                    float row = ((float)k + 0.5) / _LutRows;
                    float v = tex2Dlod(_GlowLUT, float4(u, row, 0.0, 0.0)).r;

                    // C 锐利边缘高光：内缘 / 外缘各一条细亮边
                    if (D.y > 0.0001)
                    {
                        float rw = max(D.z, 0.002) * max(inner, outer);
                        float eIn = A.z > 0.0001 ? A.z - inner : 0.0;
                        float eOut = A.z > 0.0001 ? A.z + outer : outer;
                        v += D.y * (exp(-pow((r - eIn) / rw, 2.0)) + exp(-pow((r - eOut) / rw, 2.0)));
                    }

                    // B 拉丝条纹：绕圆周的放射状细纹（整数条才不会在 ±π 处出现接缝）
                    if (C.z > 0.0001)
                    {
                        float n = max(floor(C.w + 0.5), 1.0);
                        float ang = atan2(d.y, d.x);
                        float s = 0.5 + 0.5 * sin(ang * n + _Time.y * D.x);
                        v *= lerp(1.0, 0.35 + 1.3 * s, C.z);
                    }

                    // A / D 地图反射 + 明暗分层（都靠采样背后的地图）
                    float3 tint = float3(1.0, 1.0, 1.0);
                    float shadeMul = 1.0;
                    if (C.x > 0.0001 || C.y > 0.0001)
                    {
                        float2 dir = r > 1e-5 ? d / r : float2(0.0, 1.0);
                        float2 base = (p + dir * D.w) / _MapSize + 0.5;
                        float3 mapCol = SampleMapBlur(base);
                        float lum = dot(mapCol, float3(0.299, 0.587, 0.114));

                        tint = lerp(float3(1.0, 1.0, 1.0), saturate(mapCol * 1.8), C.x);   // A：映出地图颜色
                        shadeMul = lerp(1.0, saturate(0.25 + lum * 1.1), C.y);             // D：按地图明暗压
                    }

                    float w = v * shadeMul;
                    col += B.yzw * tint * w;
                    cov = saturate(cov + w);
                }

                return float4(col, cov);   // Blend One One：直接相加
            }
            ENDCG
        }
    }

    Fallback Off
}
