// =====================================================================================
//  FluidBlur.shader — screen-space fluid, pass 2: edge-preserving (bilateral) depth blur.
// =====================================================================================
//  Smooths the bumpy per-sphere eye-depth into a continuous fluid SURFACE. Run twice
//  (horizontal then vertical) by FluidSurfaceRenderer. Bilateral: the weight falls off
//  with the depth difference, so the surface smooths but the SILHOUETTE stays crisp (no
//  bleeding across the fluid edge). Empty pixels (eyeDepth ≤ 0) are ignored and stay empty.
// =====================================================================================
Shader "Hidden/FluidBlur"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    SubShader
    {
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float2 _BlurDir;     // one-axis texel step, e.g. (1/width, 0) then (0, 1/height)
            int _BlurRadius;     // samples per side
            float _DepthFalloff; // bilateral sharpness: bigger = preserves edges harder

            float4 frag(v2f_img i) : SV_Target
            {
                float centre = tex2D(_MainTex, i.uv).r;
                if (centre <= 0.0)
                    return (0.0).xxxx; // no fluid here -> stays empty

                float sigma = max(1.0, _BlurRadius * 0.5);
                float sum = centre;
                float wsum = 1.0;

                for (int k = 1; k <= _BlurRadius; k++)
                {
                    float gauss = exp(-(k * k) / (2.0 * sigma * sigma));
                    float2 off = _BlurDir * k;

                    float sp = tex2D(_MainTex, i.uv + off).r;
                    if (sp > 0.0)
                    {
                        float w = gauss * exp(-abs(sp - centre) * _DepthFalloff);
                        sum += sp * w;
                        wsum += w;
                    }
                    float sm = tex2D(_MainTex, i.uv - off).r;
                    if (sm > 0.0)
                    {
                        float w = gauss * exp(-abs(sm - centre) * _DepthFalloff);
                        sum += sm * w;
                        wsum += w;
                    }
                }
                return sum / wsum;
            }
            ENDCG
        }
    }
}
