Shader "Custom/SpriteLocalInvert"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Mask", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _InvertStrength ("反色叠加", Range(0, 1)) = 0.75
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

        // 抓取这个物体后面的画面
        GrabPass
        {
            "_LocalInvertGrab"
        }

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
                float4 grab : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _InvertStrength;
            sampler2D _LocalInvertGrab;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;

                #if UNITY_UV_STARTS_AT_TOP
                float scale = -1.0;
                #else
                float scale = 1.0;
                #endif
                o.grab.xy = (float2(o.pos.x, o.pos.y * scale) + o.pos.w) * 0.5;
                o.grab.zw = o.pos.zw;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 mask = tex2D(_MainTex, i.uv) * i.color;
                clip(mask.a - 0.003);

                fixed4 behind = tex2Dproj(_LocalInvertGrab, UNITY_PROJ_COORD(i.grab));
                fixed3 inv = 1.0 - behind.rgb;

                // 普通使用和材质父子叠加都能用：原图保留，反色按强度叠上去；
                // 原图透明处继承透明
                fixed3 rgb = lerp(mask.rgb, inv, _InvertStrength);
                return fixed4(rgb, mask.a);
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}