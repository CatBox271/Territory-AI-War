// 默认显示用 shader：直接输出 tex2D，不做任何特效，也不乘顶点色。
// 因为不乘顶点色，所以 SpriteMeshDisplay 生成的网格不需要 COLOR 通道。
Shader "Custom/SpriteMeshDefault"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _ContentRect ("内容区 minX,minY,maxX,maxY（本地单位）", Vector) = (-0.5,-0.5,0.5,0.5)
        _Color ("Tint", Color) = (1,1,1,1)
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
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float2 local : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _ContentRect;
            fixed4 _Color;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.local = v.vertex.xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 直接输出 tex2D（不乘顶点色）
                fixed4 c = tex2D(_MainTex, i.uv);

                // 只画内容区（sprite 实际显示区）：expand 出来的那一圈保持空，
                // 否则外推的网格 UV 会把边缘像素重复/拉出来。
                float2 p = i.local;
                float aa = max(fwidth(p.x) + fwidth(p.y), 1e-5);
                float4 cr = _ContentRect;
                float inX = saturate((cr.z - p.x) / aa) * saturate((p.x - cr.x) / aa);
                float inY = saturate((cr.w - p.y) / aa) * saturate((p.y - cr.y) / aa);
                c.a *= inX * inY;

                c *= _Color;
                return c;
            }
            ENDCG
        }
    }
}
