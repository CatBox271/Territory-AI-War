Shader "Custom/BulletGlow"
{
    // 子弹光效：实例化的柔光点（加法混合）。每颗子弹一个 quad，颜色由 C# 按阵营色×强度×残影衰减预乘好，
    // 通过 MaterialPropertyBlock 的实例属性 _InstanceColor 传进来（配合 Graphics.DrawMeshInstanced）。
    Properties
    {
        _Softness ("柔边（1 = 最柔，越小核心越紧）", Range(0.05, 1)) = 0.7
        _Power ("核心集中度", Range(0.5, 8)) = 2.5
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }

        // 加法：光叠加到地图上（同 MapGlowLayer 的做法）
        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            float _Softness;
            float _Power;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 col = UNITY_ACCESS_INSTANCED_PROP(Props, _InstanceColor);

                // quad 的 uv 0~1 → 圆心为 0 的半径
                float2 d = i.uv * 2.0 - 1.0;
                float r = saturate(length(d));

                // 中心 1、边缘 0；柔边越小指数越大 → 核心越紧、外圈越淡
                float core = pow(saturate(1.0 - r), _Power / max(_Softness, 0.02));

                // col.rgb 已经在 C# 侧预乘了强度与残影衰减，这里只乘剖面
                return fixed4(col.rgb * core, core);
            }
            ENDCG
        }
    }

    Fallback Off
}
