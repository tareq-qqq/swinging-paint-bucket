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
        _Color ("Paint Color", Color) = (0.2, 0.55, 1, 1)
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
            float _Scale;
            fixed4 _Color;

            struct appdata
            {
                float3 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float3 centre = Positions[instanceID];
                float3 worldPos = centre + v.vertex * _Scale; // sphere is not rotated per-instance
                o.pos = UnityWorldToClipPos(worldPos);
                o.normal = v.normal;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Baked directional light + ambient — independent of scene lights.
                float3 n = normalize(i.normal);
                float3 lightDir = normalize(float3(0.4, 1.0, 0.3));
                float ndl = saturate(dot(n, lightDir));
                float3 col = _Color.rgb * (0.35 + 0.65 * ndl);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
