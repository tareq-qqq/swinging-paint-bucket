// =====================================================================================
//  PaintCanvasSurface.shader — draws the canvas quad with the deposited paint on it.
// =====================================================================================
//  Reads the per-texel DEPOSIT MAP (a StructuredBuffer<uint> the CanvasContact kernel
//  accumulates into with InterlockedAdd: R, G, B, coverage as fixed-point uints) and
//  blends the accumulated pigment over the blank surface colour. No render texture / blit
//  pass — the shader samples the buffer directly. Built-in render pipeline. See Surfaces.md.
// =====================================================================================
Shader "Custom/PaintCanvasSurface"
{
    Properties
    {
        _BaseColor ("Blank surface colour", Color) = (0.94, 0.92, 0.87, 1)
        _Coverage ("Opacity buildup", Float) = 1.5
        _Vividness ("Paint saturation boost", Float) = 1.6
        _DepositScale ("Deposit fixed-point scale", Float) = 1024
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off // visible from both sides (a tilted easel or a table)

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5      // StructuredBuffer access in the fragment stage
            #include "UnityCG.cginc"

            // 9 uints per texel: a DRY layer [0..2]=RGB, [3]=opacity and a WET layer [4..6]=RGB sum,
            // [7]=weight, [8]=dryness. Fresh paint fills the wet layer; CanvasCommit dries it onto the
            // dry layer. Display = base -> dry -> wet (fresh paint on top). See FluidCompute.compute.
            #define CANVAS_STRIDE 9u
            StructuredBuffer<uint> CanvasDeposit;
            int CanvasResX;
            int CanvasResY;
            float4 _BaseColor;
            float _Coverage;
            float _Vividness;
            float _DepositScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Composite one texel's paint (dry layer, then the wet layer on top) and return it
            // PREMULTIPLIED: float4(colour * opacity, opacity). Premultiplied so the bilinear blend
            // below doesn't drag empty (0-opacity) texels in as black fringes at the paint edges.
            float4 TexelPaint(int tx, int ty)
            {
                tx = clamp(tx, 0, CanvasResX - 1);
                ty = clamp(ty, 0, CanvasResY - 1);
                uint b = ((uint)(ty * CanvasResX + tx)) * CANVAS_STRIDE;

                // Dry (committed) layer: colour stored directly (0..1 * scale), opacity 0..1 * scale.
                float3 dryCol = float3(CanvasDeposit[b + 0u], CanvasDeposit[b + 1u], CanvasDeposit[b + 2u]) / _DepositScale;
                float dryOp = CanvasDeposit[b + 3u] / _DepositScale;

                // Wet (active) layer: additive weighted sum -> normalise; opacity from its weight.
                float wetWq = (float)CanvasDeposit[b + 7u];
                float3 wetCol = wetWq > 0.0
                    ? float3(CanvasDeposit[b + 4u], CanvasDeposit[b + 5u], CanvasDeposit[b + 6u]) / wetWq
                    : float3(0, 0, 0);
                float wetOp = saturate((wetWq / _DepositScale) / max(_Coverage, 0.001));

                // Wet paint sits ON TOP of the dry layer (fresh covers old). Source-over in
                // PREMULTIPLIED form: out.rgb = wet.rgb·wetOp + dry.rgb·dryOp·(1-wetOp). Returning it
                // premultiplied also makes the bilinear blend below fringe-free at the paint edges.
                float op = wetOp + dryOp * (1.0 - wetOp);
                float3 pre = wetCol * wetOp + dryCol * dryOp * (1.0 - wetOp);
                return float4(pre, op);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // BILINEAR sample (a StructuredBuffer has no hardware filtering, so we do the 4-tap
                // blend by hand) of the PREMULTIPLIED composite — smooths the texel grid and fills the
                // gaps between drips so the artwork reads as continuous paint, not a mesh.
                float fx = i.uv.x * CanvasResX - 0.5;
                float fy = i.uv.y * CanvasResY - 0.5;
                int x0 = (int)floor(fx);
                int y0 = (int)floor(fy);
                float ax = fx - x0;
                float ay = fy - y0;
                float4 s = lerp(
                    lerp(TexelPaint(x0, y0),     TexelPaint(x0 + 1, y0),     ax),
                    lerp(TexelPaint(x0, y0 + 1), TexelPaint(x0 + 1, y0 + 1), ax),
                    ay);

                float opacity = saturate(s.a);
                float3 col = _BaseColor.rgb;
                if (opacity > 0.0)
                {
                    float3 paint = s.rgb / max(s.a, 1e-4); // un-premultiply back to a straight colour

                    // Overlapping colours can dull toward grey; push the colour back away from its own
                    // brightness to restore saturation (>1 = more vivid, 1 = honest).
                    float luma = dot(paint, float3(0.299, 0.587, 0.114));
                    paint = max(0.0, lerp(float3(luma, luma, luma), paint, _Vividness));

                    col = lerp(_BaseColor.rgb, paint, opacity);
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
