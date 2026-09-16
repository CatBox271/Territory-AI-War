// 容器框：一块 quad + SDF 画「圆角矩形背景 + 墙体环」，替代原来用 5 个 SpriteRenderer 拼出来的边框图像。
// 与 Assets/C#/UI显示/ContainerMesh.cs 配套使用：
// * 物体本地坐标 1 单位 = 世界 1 单位（自身缩放保持 1），原点 = 内容中心（不含墙厚的那块区域中心）；
// * 形状与颜色全部通过 MaterialPropertyBlock 逐物体传入，共享材质不产生材质实例；
// * 整块容器（背景 + 墙环）就在这一个 pass 里画完：内孔边界是同一个像素内的颜色过渡（自带抗锯齿），
//   外轮廓再用外框覆盖率收边，所以不会出现两个渲染器叠加导致的透底接缝。
Shader "Custom/ContainerMesh"
{
    Properties
    {
        [PerRendererData] _WallColor ("墙体颜色", Color) = (1,1,1,1)
        [PerRendererData] _BackColor ("背景颜色", Color) = (0.21960784,0.21960784,0.21960784,1)
        [PerRendererData] _ShapeSize ("外框尺寸 w,h（本地单位）", Vector) = (5,5,0,0)
        [PerRendererData] _ShapeCenter ("外框中心（本地单位，内容中心为原点）", Vector) = (0,0,0,0)
        [PerRendererData] _Radius ("外角半径 左上,右上,右下,左下", Vector) = (0.5,0.5,0.5,0.5)
        [PerRendererData] _RadiusInner ("内角半径 左上,右上,右下,左下", Vector) = (0.4,0.4,0.4,0.4)
        [PerRendererData] _WallWidth ("墙厚 上,右,下,左", Vector) = (0.1,0.1,0.1,0.1)
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
            // 形状是用物体空间顶点坐标（v.vertex.xy）算的 SDF：必须关掉动态合批，
            // 否则合批会把顶点转成世界空间，多个容器共用一个材质时形状就会算错
            // （表现为多个实例只能画出一个 / 画出碎片）。
            "DisableBatching"="True"
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
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 local : TEXCOORD0;
            };

            fixed4 _WallColor;
            fixed4 _BackColor;
            float4 _ShapeSize;
            float4 _ShapeCenter;
            float4 _Radius;
            float4 _RadiusInner;
            float4 _WallWidth;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.vertex.xy;   // 本地单位就是世界单位，形状运算全部在这个空间里做
                return o;
            }

            // 按点所在象限取该角的半径（r = 左上,右上,右下,左下）
            float cornerRadius (float2 p, float4 r)
            {
                float leftCorner  = (p.y > 0.0) ? r.x : r.w;   // 左上 : 左下
                float rightCorner = (p.y > 0.0) ? r.y : r.z;   // 右上 : 右下
                return (p.x > 0.0) ? rightCorner : leftCorner;
            }

            // 圆角矩形有符号距离（<0 在内部），半径按半边长收紧避免自交
            float sdRoundBox (float2 p, float2 halfSize, float radius)
            {
                float rad = min(radius, min(halfSize.x, halfSize.y));
                float2 q = abs(p) - halfSize + rad;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - rad;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.local;

                // 逐像素屏幕导数：一个像素大约有多少本地单位，用来做边缘抗锯齿
                float aa = max(fwidth(p.x) + fwidth(p.y), 1e-5);

                // 外框：尺寸 / 中心 / 四角半径都来自脚本（左右、上下墙厚不同时外框中心会偏离内容中心）
                float2 outerHalf = max(_ShapeSize.xy * 0.5, 1e-5);
                float2 pw = p - _ShapeCenter.xy;
                float dOuter = sdRoundBox(pw, outerHalf, cornerRadius(pw, _Radius));
                float covOuter = saturate(0.5 - dOuter / aa);

                // 内孔：外框减去四边墙厚，中心固定在内容中心（本地原点）
                float2 innerHalf = max(outerHalf - float2((_WallWidth.w + _WallWidth.y) * 0.5, (_WallWidth.x + _WallWidth.z) * 0.5), 0.0);
                float dInner = sdRoundBox(p, innerHalf, cornerRadius(p, _RadiusInner));
                float covInner = saturate(0.5 - dInner / aa);

                // 墙厚为 0 时（舞台卡片就是这种：StageStyle.Wall = 0）没有"墙环"这回事：
                // innerHalf 与内角半径都等于外框，dInner 与 dOuter 逐像素相同，t 会恒等于 1 - covOuter，
                // 整块底板就被画成"墙色"、背景色只剩边缘一圈 AA —— 脚本传进来的 _BackColor 形同失效。
                // 按墙厚总量判断：没有墙 → 整块取背景色。
                float wallSum = _WallWidth.x + _WallWidth.y + _WallWidth.z + _WallWidth.w;
                float noWall = step(wallSum, 1e-5);
                float t = (1.0 - covInner) * (1.0 - noWall);   // 墙环权重：1 = 墙色，0 = 背景色

                // 一个 pass 把整块容器画完：内孔边界只是同一个像素里的颜色过渡（自带抗锯齿，不存在
                // 两个渲染器叠加导致的 1 像素透底），外轮廓再用 covOuter 收边。
                fixed4 c;
                c.rgb = lerp(_BackColor.rgb, _WallColor.rgb, t);
                c.a = lerp(_BackColor.a, _WallColor.a, t) * covOuter;
                return c;
            }
            ENDCG
        }
    }
}
