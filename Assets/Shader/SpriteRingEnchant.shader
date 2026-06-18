Shader "Custom/SpriteRingEnchant"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0

        [Header(Enchant)]
        _EffectColor ("Effect Color", Color) = (0.6, 0.2, 1, 1)
        _EffectOpacity ("Effect Opacity", Range(0, 2)) = 0.5
        _EffectSpeed ("Effect Speed", Range(0, 10)) = 4
        _EffectScale ("Effect Scale", Range(1, 30)) = 8
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

            fixed4 _EffectColor;
            float _EffectOpacity;
            float _EffectSpeed;
            float _EffectScale;

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

                float wave = sin((uv.x + uv.y) * _EffectScale + _Time.y * _EffectSpeed);
                wave = wave * 0.5 + 0.5;
                wave = pow(wave, 4);

                float wave2 = sin((uv.x - uv.y) * _EffectScale * 0.6 + _Time.y * _EffectSpeed * 1.7);
                wave2 = wave2 * 0.5 + 0.5;
                wave2 = pow(wave2, 4);

                float shimmer = max(wave, wave2);
                float intensity = (shimmer - 0.5) * 2;

                c.rgb += intensity * _EffectOpacity * _EffectColor.rgb * _EffectColor.a;
                c.a = alpha;

                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
