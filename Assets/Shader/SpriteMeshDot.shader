// 网点渐变 shader：用“规则圆孔网点”把透明与渐变做出来（本体 + 网点遮罩）。
// 与描边 shader 分开：这个只负责本体与透明；描边请用 Custom/SpriteMeshOutline。
//
// 透明度场全部按**本地坐标**算（网格本地 1 单位 = 世界 1 单位，物体缩放保持 1）：
//   1) _Alpha：整体可见度 = **可见面积比例**（内容区平均值，孔洞覆盖率 t = 1 - _Alpha）
//   2) 四条边各一个 Vector4（_EdgeTop/_EdgeRight/_EdgeBottom/_EdgeLeft）= (开始比例, 结束比例, 开始透明度, 结束透明度)：
//      比例 = 从这条边到对边的归一化距离（0 = 就在这条边上）。该边可见度在这段区间里从「开始透明度」到「结束透明度」，
//      区间外分别取两端的值。四条边相乘进 vis（想要某条边渐隐/直接切掉都靠它）
//   3) 四条边各一个 Vector2（_EdgeTopMid/_EdgeRightMid/_EdgeBottomMid/_EdgeLeftMid）= (中间点比例, 该点透明度)：
//      中间点的「比例」是它在**当前这条边的渐变区间里**的位置（0 = 区间起点、1 = 区间终点，不是整条边的比例）。
//      于是这条边在区间里就是一条二次贝塞尔 P0=(0,起透明) P1=(中间点) P2=(1,止透明)，曲线可以凹可以凸；
//      中间点的透明度正好落在起止连线上时曲线仍是直线（和原来完全一样）。y < 0 = 不插控制点，直接线性。
// 再把参数变成网点（两种模式都用“面积 = 参数”反解半径，见 frag）：
//   挖孔模式 _KeepDots = 0：画圆孔，孔并集面积 = t = 1 - 可见度 —— t=0 不打孔（不透明），
//                            t=1 半径 = 格/√2，圆正好铺满整格（完全看不见）
//   保留模式 _KeepDots = 1：保留圆点，点并集面积 = 可见度 —— vis=0 没有点（全透），
//                            vis=1 半径 = 格/√2，点铺满整格（= 原图）
Shader "Custom/SpriteMeshDot"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _SpriteRect ("Sprite Rect (uvMin.xy, uvSize.xy)", Vector) = (0,0,1,1)
        [PerRendererData] _ContentRect ("内容区 minX,minY,maxX,maxY（本地单位）", Vector) = (-0.5,-0.5,0.5,0.5)

        _Color ("Tint", Color) = (1,1,1,1)

        _DotDensity ("网点密度", Float) = 12

        [Toggle] _KeepDots ("圆点保留（0 = 圆孔挖空，1 = 保留圆点、其余挖空）", Float) = 0

        _Alpha ("整体可见度 = 可见面积比例 0~1", Range(0,1)) = 1

        // 四条边各一个 Vector4 = (开始比例, 结束比例, 开始透明度, 结束透明度)
        // 比例按「这条边 → 对边」的归一化距离算：0 = 就在这条边上，1 = 到了对边。
        _EdgeTop    ("上 (ratio起,ratio止,alpha起,alpha止)", Vector) = (0,0,1,1)
        _EdgeRight  ("右 (ratio起,ratio止,alpha起,alpha止)", Vector) = (0,0,1,1)
        _EdgeBottom ("下 (ratio起,ratio止,alpha起,alpha止)", Vector) = (0,0,1,1)
        _EdgeLeft   ("左 (ratio起,ratio止,alpha起,alpha止)", Vector) = (0,0,1,1)

        // 四条边各一个 Vector2 = (中间点比例 0~1, 该点的透明度)：在渐变区间里插一个贝塞尔控制点，做非线性渐变。
        // 比例是「在当前渐变区间里的位置」：0 = 区间起点、1 = 区间终点。y < 0 = 不插控制点（线性）。
        _EdgeTopMid    ("上 中间点 (区间比例0~1, 透明度)", Vector) = (0.5,-1,0,0)
        _EdgeRightMid  ("右 中间点 (区间比例0~1, 透明度)", Vector) = (0.5,-1,0,0)
        _EdgeBottomMid ("下 中间点 (区间比例0~1, 透明度)", Vector) = (0.5,-1,0,0)
        _EdgeLeftMid   ("左 中间点 (区间比例0~1, 透明度)", Vector) = (0.5,-1,0,0)
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
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 local : TEXCOORD0;
                float2 uv    : TEXCOORD1;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _SpriteRect;
            float4 _ContentRect;
            fixed4 _Color;
            float _DotDensity;
            float _KeepDots;
            float _Alpha;
            float4 _EdgeTop;
            float4 _EdgeRight;
            float4 _EdgeBottom;
            float4 _EdgeLeft;
            float4 _EdgeTopMid;
            float4 _EdgeRightMid;
            float4 _EdgeBottomMid;
            float4 _EdgeLeftMid;

            // 一条边的渐变：e = (开始比例, 结束比例, 开始透明度, 结束透明度)，t = 归一化距离（0 = 在这条边上）
            // m.xy = (中间点比例 0~1（在当前渐变区间里）, 该点透明度)；m.y < 0 表示不插控制点，纯线性。
            // 中间点把这段渐变变成二次贝塞尔 P0=(0,alpha起) P1=(m.x,m.y) P2=(1,alpha止)：
            //   先由 x(u) = 2(1-u)u·m.x + u² 反解参数 u，再取该处的 y。
            //   写成「弦 + 2u(1-u)·偏移」的形式，中间点正好落在弦上时偏移为 0，结果和原来的线性一模一样。
            float EdgeGrad(float4 e, float4 m, float t)
            {
                float a = e.x, b = e.y;
                float span = b - a;
                float k = saturate(span > 1e-5 ? (t - a) / span : (t >= a ? 1.0 : 0.0));
                float lin = lerp(e.z, e.w, k);
                if (m.y < 0.0) return saturate(lin);   // 没插中间点：线性

                float A = 1.0 - 2.0 * m.x;   // x(u) = A·u² + B·u
                float B = 2.0 * m.x;
                float u;
                if (abs(A) < 1e-4) u = B > 1e-4 ? k / B : k;   // m.x = 0.5：x(u) = u
                else
                {
                    float disc = max(B * B + 4.0 * A * k, 0.0);
                    u = (-B + sqrt(disc)) / (2.0 * A);          // 落在 [0,1] 的那个根
                }
                u = saturate(u);

                // 弦 + 贝塞尔相对弦的偏移（自动保证 u=0/1 处取到起止透明度）
                float bend = 2.0 * u * (1.0 - u) * (m.y - lerp(e.z, e.w, m.x));
                return saturate(lin + bend);
            }

            // 由“孔洞/圆点覆盖率”反解归一化半径 ρ = r/格（方阵网点、单位格边长 1）：
            //   ρ ≤ 0.5        : 覆盖率 f = πρ²                                  （圆不相碰，精确）
            //   0.5 < ρ ≤ 1/√2 : f = πρ² − 2[2ρ²·acos(1/2ρ) − 0.5√(4ρ²−1)]（只扣相邻圆的重叠；
            //                    对角圆相切与三圆重叠都从 ρ=1/√2 才开始，那里 f 恰好 = 1）
            //   t ≤ π/4 走闭式；其上用解析近似（端点 1/2 与 √2/2 精确、衔接处斜率 = 1/π），
            //   全区间覆盖率偏差 ≤ 1.1 个百分点（线性半径版最大约 16 个百分点）。
            float DotRadiusFromCoverage(float t)
            {
                if (t <= 0.7853981634) return sqrt(t * 0.3183098862);              // √(t/π)
                float s = sqrt(1.0 - t);
                return min(0.7071067812 - s * 0.5992292843 + s * s * 0.3284548256, 0.7071067812);
            }

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.vertex.xy;
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.local;
                float4 cr = _ContentRect;   // minX, minY, maxX, maxY（本地单位）
                float aa = max(fwidth(p.x) + fwidth(p.y), 1e-5);

                // 内容区（sprite 实际显示区）与贴图采样
                float inX = saturate((cr.z - p.x) / aa) * saturate((p.x - cr.x) / aa);
                float inY = saturate((cr.w - p.y) / aa) * saturate((p.y - cr.y) / aa);
                float insideContent = inX * inY;

                // 采样本体：用 _Color（MPB 逐个传），不乘顶点色（网格无 COLOR 通道）
                float2 c01 = float2((p.x - cr.x) / max(cr.z - cr.x, 1e-5), (p.y - cr.y) / max(cr.w - cr.y, 1e-5));
                fixed4 tex = tex2D(_MainTex, _SpriteRect.xy + c01 * _SpriteRect.zw) * _Color;

                // 到四条边的距离（本地单位；边缘 = 0，往内容区里为正）
                float rawT = cr.w - p.y;
                float rawR = cr.z - p.x;
                float rawB = p.y - cr.y;
                float rawL = p.x - cr.x;
                float axX = max(cr.z - cr.x, 1e-5);
                float axY = max(cr.w - cr.y, 1e-5);

                // 可见度：整体 × 四条边各自的渐变（每边按 (开始比例, 结束比例, 开始透明度, 结束透明度) + 贝塞尔中间点算）
                float vT = EdgeGrad(_EdgeTop,    _EdgeTopMid,    rawT / axY);
                float vR = EdgeGrad(_EdgeRight,  _EdgeRightMid,  rawR / axX);
                float vB = EdgeGrad(_EdgeBottom, _EdgeBottomMid, rawB / axY);
                float vL = EdgeGrad(_EdgeLeft,   _EdgeLeftMid,   rawL / axX);
                float vis = saturate(_Alpha) * vT * vR * vB * vL;

                // 3) 网点：两种模式，半径都由“覆盖率 = 参数”反解（面积口径）
                //    _KeepDots = 0：圆孔挖空 —— 孔并集面积 = t = 1 - 可见度（t=1 时孔铺满 = 全透）
                //    _KeepDots = 1：圆点保留 —— 点并集面积 = vis（vis=1 时点铺满 = 原图）
                float cell = 1.0 / max(_DotDensity, 1e-4);
                float t = 1.0 - vis;
                float keep = step(0.5, _KeepDots);
                float a = tex.a * insideContent;

                if (_DotDensity > 0.0001)
                {
                    float rho = DotRadiusFromCoverage(lerp(t, vis, keep));
                    float r = cell * rho;
                    float2 center = float2((cr.x + cr.z) * 0.5, (cr.y + cr.w) * 0.5);
                    float2 cellUv = frac((p - center) / cell + 0.5) - 0.5;
                    float d = length(cellUv * cell);
                    float hole = (r > 0.0) ? (1.0 - smoothstep(r - aa, r + aa, d)) : 0.0;

                    // 端点格角残余各自收尾：挖孔看 t（保证 0 = 真的看不见），保留看 vis（保证 1 = 原图）
                    float solidHole = (1.0 - hole) * (1.0 - smoothstep(0.98, 1.0, t));
                    float solidDot = max(hole, smoothstep(0.995, 1.0, vis));
                    a *= lerp(solidHole, solidDot, keep);
                }
                else
                {
                    // 没给密度：退化成平滑遮罩（alpha = 可见度，本身就是面积口径）
                    a *= vis;
                }

                return fixed4(tex.rgb, a);
            }
            ENDCG
        }
    }
}
