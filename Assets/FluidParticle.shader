// =====================================================================================
//  FluidParticle.shader — draws GPU particles by reading their positions from a buffer.
// =====================================================================================
//  Used by FluidSimGPU.cs with Graphics.RenderMeshPrimitives: it draws `numParticles`
//  instances of a small sphere mesh, and the vertex stage places each instance at
//  Positions[instanceID] (straight from the compute buffer — no CPU readback, no
//  per-instance matrix array). Built-in render pipeline. Simple baked diffuse shading
//  so the spheres read as 3D regardless of scene lighting.
// =====================================================================================
Shader "Custom/FluidParticle"
{
    Properties
    {
        _Scale ("Particle Scale", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5      // StructuredBuffer access in the vertex stage
            #include "UnityCG.cginc"

            StructuredBuffer<float3> Positions;
            StructuredBuffer<float3> Colors;   // .x = per-particle mix fraction t (0=paint A, 1=paint B)
            StructuredBuffer<float3> MixLut;   // t -> displayed colour, baked on the CPU (FluidSimGPU.BuildMixLut)
            int MixLutSize;
            float _Scale;

            // Map a particle's mix fraction t to its displayed colour. All the subtractive
            // (Kubelka-Munk) spectral mixing happens once on the CPU and is baked into MixLut;
            // here we just look it up and lerp between the two nearest entries. Endpoints of the
            // LUT are the exact picked paint colours, so pure paint stays vivid.
            float3 SampleMix(float t)
            {
                float f = saturate(t) * (MixLutSize - 1);
                int i0 = (int)floor(f);
                int i1 = min(i0 + 1, MixLutSize - 1);
                return lerp(MixLut[i0], MixLut[i1], f - i0);
            }

            struct appdata
            {
                float3 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 color : TEXCOORD1;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float3 centre = Positions[instanceID];
                float3 worldPos = centre + v.vertex * _Scale; // sphere is not rotated per-instance
                o.pos = UnityWorldToClipPos(worldPos);
                o.normal = v.normal;
                o.color = SampleMix(Colors[instanceID].x); // mix fraction -> spectral Kubelka-Munk colour (LUT)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Baked directional light + ambient — independent of scene lights.
                float3 n = normalize(i.normal);
                float3 lightDir = normalize(float3(0.4, 1.0, 0.3));
                float ndl = saturate(dot(n, lightDir));
                float3 col = i.color * (0.35 + 0.65 * ndl);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
