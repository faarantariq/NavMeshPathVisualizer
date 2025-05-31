Shader "Faran's/LineArrowScroll"
{
    Properties
    {
        _MainTex ("Arrow Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Speed ("Scroll Speed", Float) = 1
        _Tiling ("Tiling", Vector) = (1,1,0,0)
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent" "Queue"="Transparent"
        }
        LOD 100

        Pass
        {
            ZWrite Off
            //  Blend One One
            Blend SrcAlpha One
            //Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Speed;
            float4 _Tiling;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                float2 tiledUV = v.uv * _Tiling.xy + _Tiling.zw;
                o.uv = tiledUV;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                uv.x += _Time.y * _Speed;
                fixed4 tex = tex2D(_MainTex, uv);
                return tex * _Color;
            }
            ENDCG
        }
    }
}