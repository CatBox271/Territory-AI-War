Shader "Custom/TowelGlassBlink"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _RimStart ("边缘起点", Range(0, 1)) = 0.5
        _RimEnd ("边缘终点", Range(0, 1)) = 0.9
        _ReflectStrength ("边缘反射强度", Range(0, 1)) = 0.85
        _ReflectOffset ("反射外扩", Range(0, 30)) = 5
        _BlurSize ("地图模糊程度", Range(0, 30)) = 7
        _ReflectBrightness ("反射亮度", Range(0, 3)) = 1.1
        _InvincibleGlow ("无敌闪白强度 0=正常 越大越亮", Range(0, 4)) = 0
        _InvincibleGlowColor ("无敌闪白颜色", Color) = (1,1,1,1)
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

        // 抓取球体背后已经画好的地图（本球自己不在抓取结果里）
        GrabPass
        {
            "_MarbleRimGrab"
        }

        // ===== Pass 1：玻璃本体（和原 MarbleRimMapReflect 逐字一致，不含任何闪白代码）=====
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
            float _RimStart;
            float _RimEnd;
            float _ReflectStrength;
            float _ReflectOffset;
            float _BlurSize;
            float _ReflectBrightness;

            sampler2D _MarbleRimGrab;
            float4 _MarbleRimGrab_TexelSize;

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
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.003);

                // 从球心指向边缘的方向，越靠边反射越强
                float2 dir = i.uv - 0.5;
                float d = length(dir);
                float r = d * 2.0;
                dir = d > 0.0001 ? dir / d : float2(0, 1);

                float rim = smoothstep(_RimStart, _RimEnd, r);
                rim *= _ReflectStrength;

                float2 texel = _MarbleRimGrab_TexelSize.xy;
                float2 outward = dir * _ReflectOffset * texel;
                float2 blur = texel * _BlurSize;

                // 3x3 模糊采样，采样点整体沿径向朝球外偏移，模拟反射球外的地图
                half4 grab = half4(0, 0, 0, 0);
                float wsum = 0.0;
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        float w = (abs(x) + abs(y) == 2) ? 1.0 : ((abs(x) + abs(y) == 1) ? 2.0 : 4.0);
                        float2 p = float2(
                            i.grab.x + outward.x + x * blur.x,
                            i.grab.y + outward.y + y * blur.y);
                        grab += tex2Dproj(_MarbleRimGrab, UNITY_PROJ_COORD(float4(p, i.grab.z, i.grab.w))) * w;
                        wsum += w;
                    }
                }
                grab.rgb /= wsum;
                grab.rgb *= _ReflectBrightness;

                c.rgb = lerp(c.rgb, grab.rgb, rim);
                return c;
            }
            ENDCG
        }

        // ===== Pass 2：无敌闪白（在 Pass 1 处理完玻璃之后，把白光叠加上去）=====
        // Blend One One：rgb 和 alpha 两个通道都直接相加，所以是"加亮"而不是"变淡"。
        // _InvincibleGlow = 0 时整段被 clip 掉，一个像素都不写 → 不无敌时零开销、外观和纯玻璃完全一致。
        Pass
        {
            Blend One One

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
            float _InvincibleGlow;
            fixed4 _InvincibleGlowColor;

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
                clip(_InvincibleGlow - 0.001);

                // 用塔身自己的轮廓（sprite alpha）裁出加亮范围，否则整个 quad 会亮成一块方块
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.003);

                fixed glow = _InvincibleGlow * c.a;

                c.rgb = _InvincibleGlowColor.rgb * glow;
                c.a = _InvincibleGlowColor.a * glow;   // alpha 也一起加
                return c;
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
