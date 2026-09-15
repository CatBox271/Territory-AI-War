// 描边专用 shader（只做描边，不画本体）
// 配合 SpriteMeshDisplay 生成的“比图形大一圈 padding”的四边形：描边就画在图形外那一圈里。
// 形状全部按**本地坐标**算（网格本地 1 单位 = 世界 1 单位，物体缩放保持 1），所以描边等宽、不受拉伸影响。
Shader "Custom/SpriteMeshOutline"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _SpriteRect ("Sprite Rect (uvMin.xy, uvSize.xy)", Vector) = (0,0,1,1)
        [PerRendererData] _ContentRect ("内容区 minX,minY,maxX,maxY（本地单位）", Vector) = (-0.5,-0.5,0.5,0.5)

        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("描边颜色", Color) = (0,0,0,1)
        _OutlineWidth ("描边宽度 top/right/bottom/left（本地单位，全 0 = 关）", Vector) = (0.03,0.03,0.03,0.03)
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
                fixed4 color : COLOR;
            };

            float4 _ContentRect;
            fixed4 _Color;
            fixed4 _OutlineColor;
            float4 _OutlineWidth;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.vertex.xy;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.local;
                float4 cr = _ContentRect;   // minX, minY, maxX, maxY（本地单位）
                float aa = max(fwidth(p.x) + fwidth(p.y), 1e-5);

                // 内容区（sprite 实际显示区）
                float inX = saturate((cr.z - p.x) / aa) * saturate((p.x - cr.x) / aa);
                float inY = saturate((cr.w - p.y) / aa) * saturate((p.y - cr.y) / aa);
                float insideContent = inX * inY;

                // 外扩矩形（内容区 + 每边各自宽度）
                float wT = max(_OutlineWidth.x, 0.0);
                float wR = max(_OutlineWidth.y, 0.0);
                float wB = max(_OutlineWidth.z, 0.0);
                float wL = max(_OutlineWidth.w, 0.0);

                float exMinX = cr.x - wL, exMaxX = cr.z + wR;
                float exMinY = cr.y - wB, exMaxY = cr.w + wT;
                float exX = saturate((exMaxX - p.x) / aa) * saturate((p.x - exMinX) / aa);
                float exY = saturate((exMaxY - p.y) / aa) * saturate((p.y - exMinY) / aa);
                float insideExpanded = exX * exY;

                float a = saturate(insideExpanded - insideContent) * _OutlineColor.a;
                return fixed4(_OutlineColor.rgb, a);
            }
            ENDCG
        }
    }
}
