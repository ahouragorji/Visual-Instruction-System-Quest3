Shader "Hidden/DepthToLinearMeters"
{
    Properties
    {
        _MainTex ("Texture", 2DArray) = "white" {} 
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray 
            
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            // Catch the preprocessed bytes safely passed by Graphics.Blit
            UNITY_DECLARE_TEX2DARRAY(_MainTex);

            v2f vert (appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target {
                // Flip the Y-axis
                float2 correctUV = float2(i.uv.x, 1.0 - i.uv.y);
                
                // Sample the safely blitted texture
                float linearDepth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(correctUV, 0.0)).r;
                
                return float4(linearDepth, 0, 0, 1.0);
            }
            ENDCG
        }
    }
}