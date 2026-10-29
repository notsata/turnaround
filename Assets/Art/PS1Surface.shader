Shader "Turnaround/PS1Surface"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Snap ("Vertex Snap", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Back
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Snap;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed3 light : COLOR0;
                float4 screenPos : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 clip = UnityObjectToClipPos(v.vertex);
                // Preserve the sign behind the camera. Clamping w positive makes a
                // large triangle straddling the near plane explode across the frame.
                float safeW = abs(clip.w) < 0.0001 ? (clip.w < 0.0 ? -0.0001 : 0.0001) : clip.w;
                float2 ndc = clip.xy / safeW;
                float2 snapped = round(ndc * float2(213.0, 120.0)) / float2(213.0, 120.0);
                clip.xy = lerp(ndc, snapped, _Snap) * clip.w;
                o.pos = clip;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 ambient = ShadeSH9(float4(worldNormal, 1.0));
                float ndl = saturate(dot(worldNormal, normalize(_WorldSpaceLightPos0.xyz)));
                o.light = saturate(ambient + _LightColor0.rgb * (0.22 + ndl * 0.78));
                o.screenPos = ComputeScreenPos(clip);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                c.rgb *= i.light;
                float2 pixel = floor((i.screenPos.xy / i.screenPos.w) * _ScreenParams.xy);
                float dither = fmod(pixel.x + pixel.y * 2.0, 4.0) / 4.0 - 0.375;
                c.rgb = floor(saturate(c.rgb + dither / 31.0) * 31.0 + 0.5) / 31.0;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    Fallback "VertexLit"
}
