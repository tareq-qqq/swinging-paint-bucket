// =====================================================================================
//  BucketGlass — a simple transparent, double-sided, unlit shader for the bucket walls.
// =====================================================================================
//  Used by BucketVisual.cs to draw the open-top cylinder so you can see the paint inside.
//  Cull Off = both faces visible (you see the far wall through the near wall); ZWrite Off +
//  alpha blend = see-through. Built-in render pipeline.
// =====================================================================================
Shader "Custom/BucketGlass"
{
    Properties
    {
        _Color ("Color", Color) = (0.6, 0.8, 1.0, 0.15)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
