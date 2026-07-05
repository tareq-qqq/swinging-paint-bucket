// =====================================================================================
//  FluidImpostor.shader — screen-space fluid, pass 1: sphere impostors -> depth + colour.
// =====================================================================================
//  Drawn procedurally (Graphics.DrawProceduralNow, 6 verts × numParticles) by
//  FluidSurfaceRenderer. Each particle becomes a camera-facing quad; the fragment carves
//  the quad into a SPHERE (analytic ray-sphere) and writes the sphere's front-surface
//  eye-depth, so the nearest particle wins via the depth buffer.
//
//  TWO SEPARATE single-target passes (NOT multi-render-target): older GPUs have a driver
//  bug where clip()/discard is honoured for colour target 0 but the 2nd MRT target still
//  gets written for every pixel — which made the whole screen read as fluid. So:
//    Pass 0 (Depth)  -> eye depth (+ SV_Depth so the nearest particle wins the depth test).
//    Pass 1 (Colour) -> pigment RGB + wetness, ZTested against pass 0's depth (nearest only).
//  Built-in RP, hand-written — the screen-space fluid technique (Green / Sebastian Lague).
// =====================================================================================
Shader "Hidden/FluidImpostor"
{
    Properties
    {
        // Set from C# to match the platform's depth convention (LEqual, or GEqual on reversed-Z),
        // so the NEAREST particle wins the depth test on every graphics API.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    CGINCLUDE
    #pragma vertex vert
    #pragma target 4.5
    #include "UnityCG.cginc"

    StructuredBuffer<float3> Positions;
    StructuredBuffer<float3> Colors;   // .x = mix fraction t
    StructuredBuffer<uint>   Absorbed; // 1 = soaked into the canvas -> hide
    StructuredBuffer<float>  Wetness;  // 1 = wet (glossy) .. 0 = dry (matte)
    StructuredBuffer<float3> MixLut;   // t -> spectral paint colour (CPU-baked)
    int MixLutSize;
    float _Radius;                     // particle world radius (= particleScale × 0.5)
    // Camera matrices passed EXPLICITLY from C# — UNITY_MATRIX_V/P are not reliably the camera's
    // during a manual DrawProceduralNow in OnRenderImage (they can be a fullscreen blit matrix).
    float4x4 _CamV;
    float4x4 _CamP;

    static const float2 CORNERS[6] = {
        float2(-1, -1), float2(1, -1), float2(1, 1),
        float2(-1, -1), float2(1, 1), float2(-1, 1)
    };

    float3 SampleMix(float t)
    {
        float f = saturate(t) * (MixLutSize - 1);
        int i0 = (int)floor(f);
        int i1 = min(i0 + 1, MixLutSize - 1);
        return lerp(MixLut[i0], MixLut[i1], f - i0);
    }

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;         // quad coord in [-1,1]
        float3 viewCentre : TEXCOORD1; // particle centre, view space
        float3 color : TEXCOORD2;
        float wetness : TEXCOORD3;
    };

    v2f vert(uint vid : SV_VertexID)
    {
        v2f o;
        uint id = vid / 6;
        float2 corner = CORNERS[vid % 6];

        float3 worldPos = Positions[id];
        float3 viewCentre = mul(_CamV, float4(worldPos, 1)).xyz;

        // Soaked-in paint: radius 0 collapses the quad to a point (no fragments) -> hidden.
        float r = (Absorbed[id] != 0u) ? 0.0 : _Radius;

        float3 viewPos = viewCentre + float3(corner * r, 0);
        o.pos = mul(_CamP, float4(viewPos, 1));
        o.uv = corner;
        o.viewCentre = viewCentre;
        o.color = SampleMix(Colors[id].x);
        o.wetness = Wetness[id];
        return o;
    }

    // Front-surface point of the sphere in view space (toward camera = +Z), from the quad coord.
    float3 SphereViewPos(v2f i, out float r2)
    {
        r2 = dot(i.uv, i.uv);
        float nz = sqrt(max(0.0, 1.0 - r2));
        return i.viewCentre + float3(i.uv * _Radius, nz * _Radius);
    }
    ENDCG

    SubShader
    {
        // --- Pass 0: DEPTH (eye depth to colour, sphere depth to SV_Depth for nearest-wins) ---
        Pass
        {
            ZWrite On
            ZTest [_ZTest]
            Cull Off
            CGPROGRAM
            #pragma fragment fragDepth
            // eyeDepth is written to an ARGBHalf target (RFloat won't clear reliably on old GPUs);
            // replicate into all channels so .r is valid regardless of how the target is sampled.
            struct DepthOut { float4 eyeDepth : SV_Target; float depth : SV_Depth; };
            DepthOut fragDepth(v2f i)
            {
                float r2;
                float3 viewPos = SphereViewPos(i, r2);
                clip(1.0 - r2); // carve the quad into the sphere silhouette

                float4 clip = mul(_CamP, float4(viewPos, 1));
                DepthOut o;
                o.depth = clip.z / clip.w;         // per-fragment sphere depth -> nearest wins
                o.eyeDepth = (-viewPos.z).xxxx;     // view z is negative in front -> positive eye depth
                return o;
            }
            ENDCG
        }

        // --- Pass 1: COLOUR (pigment + wetness, only the nearest fragment via ZTest, no ZWrite) ---
        Pass
        {
            ZWrite Off
            ZTest [_ZTest]
            Cull Off
            CGPROGRAM
            #pragma fragment fragColor
            // Output the SAME sphere SV_Depth as pass 0 so the ZTest compares like-for-like and the
            // nearest fragment passes (ZWrite Off, so the shared depth buffer is untouched).
            struct ColorOut { float4 color : SV_Target; float depth : SV_Depth; };
            ColorOut fragColor(v2f i)
            {
                float r2;
                float3 viewPos = SphereViewPos(i, r2);
                clip(1.0 - r2);
                float4 clip = mul(_CamP, float4(viewPos, 1));
                ColorOut o;
                o.depth = clip.z / clip.w;
                o.color = float4(i.color, i.wetness); // rgb pigment, a = wetness
                return o;
            }
            ENDCG
        }
    }
}
