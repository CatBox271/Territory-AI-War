// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Custom/SimpleGrabPassBlur" {
    Properties {
        _Color ("Main Color", Color) = (1,1,1,1)
        _BumpAmt  ("Distortion", Range (0,128)) = 10
        _MainTex ("Tint Color (RGB)", 2D) = "white" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}
        _Size ("Size", Range(0, 20)) = 1
        _Samples ("重影数量", Range(3, 33)) = 9
    }

    Category {

        // We must be transparent, so other objects are drawn before this one.
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }


        SubShader {
            ZWrite Off

            // Horizontal blur
            GrabPass {                    
                Tags { "LightMode" = "Always" }
            }
            Pass {
                Tags { "LightMode" = "Always" }

                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #pragma target 3.5
                #pragma fragmentoption ARB_precision_hint_fastest
                #include "UnityCG.cginc"

                struct appdata_t {
                    float4 vertex : POSITION;
                    float2 texcoord: TEXCOORD0;
                };

                struct v2f {
                    float4 vertex : POSITION;
                    float4 uvgrab : TEXCOORD0;
                };

                v2f vert (appdata_t v) {
                    v2f o;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    #if UNITY_UV_STARTS_AT_TOP
                    float scale = -1.0;
                    #else
                    float scale = 1.0;
                    #endif
                    o.uvgrab.xy = (float2(o.vertex.x, o.vertex.y*scale) + o.vertex.w) * 0.5;
                    o.uvgrab.zw = o.vertex.zw;
                    return o;
                }

                sampler2D _GrabTexture;
                float4 _GrabTexture_TexelSize;
                float _Size;
                float _Samples;

                half4 frag( v2f i ) : COLOR {
//                  half4 col = tex2Dproj( _GrabTexture, UNITY_PROJ_COORD(i.uvgrab));
//                  return col;

                    // offset 为像素偏移；kernel 形状沿用原 9 点权重表，_Samples = 9 时逐点等价
                    #define GRABPIXEL(wgt,off) tex2Dproj( _GrabTexture, UNITY_PROJ_COORD(float4(i.uvgrab.x + _GrabTexture_TexelSize.x * (off), i.uvgrab.y, i.uvgrab.z, i.uvgrab.w))) * (wgt)
                    float size = max(_Size, 0.0001);
                    float n = max(3.0, floor(_Samples + 0.5));
                    float halfExtent = 4.0 * size;
                    float stride = (2.0 * halfExtent) / (n - 1.0);

                    half4 sum = half4(0,0,0,0);
                    float wsum = 0.0;
                    for (int k = 0; k < 33; k++) {
                        if ((float)k >= n) break;
                        float offset = -halfExtent + stride * (float)k;
                        float d = abs(offset) / size;
                        float w = 0.18 - 0.03 * min(d, 3.0) - 0.04 * max(d - 3.0, 0.0);
                        sum += GRABPIXEL(w, offset);
                        wsum += w;
                    }

                    return sum / wsum;
                }
                ENDCG
            }
            // Vertical blur
            GrabPass {                        
                Tags { "LightMode" = "Always" }
            }
            Pass {
                Tags { "LightMode" = "Always" }

                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #pragma target 3.5
                #pragma fragmentoption ARB_precision_hint_fastest
                #include "UnityCG.cginc"

                struct appdata_t {
                    float4 vertex : POSITION;
                    float2 texcoord: TEXCOORD0;
                };

                struct v2f {
                    float4 vertex : POSITION;
                    float4 uvgrab : TEXCOORD0;
                };

                v2f vert (appdata_t v) {
                    v2f o;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    #if UNITY_UV_STARTS_AT_TOP
                    float scale = -1.0;
                    #else
                    float scale = 1.0;
                    #endif
                    o.uvgrab.xy = (float2(o.vertex.x, o.vertex.y*scale) + o.vertex.w) * 0.5;
                    o.uvgrab.zw = o.vertex.zw;
                    return o;
                }

                sampler2D _GrabTexture;
                float4 _GrabTexture_TexelSize;
                float _Size;
                float _Samples;

                half4 frag( v2f i ) : COLOR {
//                  half4 col = tex2Dproj( _GrabTexture, UNITY_PROJ_COORD(i.uvgrab));
//                  return col;

                    // offset 为像素偏移；kernel 形状沿用原 9 点权重表，_Samples = 9 时逐点等价
                    #define GRABPIXEL(wgt,off) tex2Dproj( _GrabTexture, UNITY_PROJ_COORD(float4(i.uvgrab.x, i.uvgrab.y + _GrabTexture_TexelSize.y * (off), i.uvgrab.z, i.uvgrab.w))) * (wgt)
                    float size = max(_Size, 0.0001);
                    float n = max(3.0, floor(_Samples + 0.5));
                    float halfExtent = 4.0 * size;
                    float stride = (2.0 * halfExtent) / (n - 1.0);

                    half4 sum = half4(0,0,0,0);
                    float wsum = 0.0;
                    for (int k = 0; k < 33; k++) {
                        if ((float)k >= n) break;
                        float offset = -halfExtent + stride * (float)k;
                        float d = abs(offset) / size;
                        float w = 0.18 - 0.03 * min(d, 3.0) - 0.04 * max(d - 3.0, 0.0);
                        sum += GRABPIXEL(w, offset);
                        wsum += w;
                    }

                    return sum / wsum;
                }
                ENDCG
            }

            // Distortion
            GrabPass {                        
                Tags { "LightMode" = "Always" }
            }
            Pass {
                Tags { "LightMode" = "Always" }

                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #pragma fragmentoption ARB_precision_hint_fastest
                #include "UnityCG.cginc"

                struct appdata_t {
                    float4 vertex : POSITION;
                    float2 texcoord: TEXCOORD0;
                    fixed4 color : COLOR;
                };

                struct v2f {
                    float4 vertex : POSITION;
                    float4 uvgrab : TEXCOORD0;
                    float2 uvbump : TEXCOORD1;
                    float2 uvmain : TEXCOORD2;
                    fixed4 color : COLOR;
                };

                float _BumpAmt;
                float4 _BumpMap_ST;
                float4 _MainTex_ST;

                v2f vert (appdata_t v) {
                    v2f o;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    #if UNITY_UV_STARTS_AT_TOP
                    float scale = -1.0;
                    #else
                    float scale = 1.0;
                    #endif
                    o.uvgrab.xy = (float2(o.vertex.x, o.vertex.y*scale) + o.vertex.w) * 0.5;
                    o.uvgrab.zw = o.vertex.zw;
                    o.uvbump = TRANSFORM_TEX( v.texcoord, _BumpMap );
                    o.uvmain = TRANSFORM_TEX( v.texcoord, _MainTex );
                    o.color = v.color;
                    return o;
                }

                fixed4 _Color;
                sampler2D _GrabTexture;
                float4 _GrabTexture_TexelSize;
                sampler2D _BumpMap;
                sampler2D _MainTex;

                half4 frag( v2f i ) : COLOR {
                    // calculate perturbed coordinates
                    half2 bump = UnpackNormal(tex2D( _BumpMap, i.uvbump )).rg; // we could optimize this by just reading the x  y without reconstructing the Z
                    float2 offset = bump * _BumpAmt * _GrabTexture_TexelSize.xy;
                    i.uvgrab.xy = offset * i.uvgrab.z + i.uvgrab.xy;

                    half4 bg = tex2Dproj( _GrabTexture, UNITY_PROJ_COORD(i.uvgrab));
                    half4 sprite = tex2D( _MainTex, i.uvmain ) * i.color * _Color;
                    sprite.rgb *= sprite.a;

                    bg.rgb = bg.rgb * (1.0 - sprite.a) + sprite.rgb;
                    bg.a = 1.0;
                    return bg;
                }
                ENDCG
            }
        }
    }
}
