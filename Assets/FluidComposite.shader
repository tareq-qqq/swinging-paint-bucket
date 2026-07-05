// =====================================================================================
//  FluidComposite.shader — screen-space fluid, pass 3: normals + paint shading + composite.
// =====================================================================================
//  Fullscreen pass (Blit scene -> screen). Reconstructs eye-space position and NORMALS
//  from the smoothed fluid depth, shades the paint as an OPAQUE coloured surface with a
//  WETNESS-driven gloss (glossy while wet -> matte as it dries), and composites it over the
//  scene where the fluid is in front of scene geometry (via _CameraDepthTexture). Adapted
//  from the water version (Green / Lague) — no refraction/transparency; it's paint.
// =====================================================================================
Shader "Hidden/FluidComposite"
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

            sampler2D _MainTex;         // scene colour
            sampler2D _FluidDepth;      // smoothed fluid eye depth (RFloat; 0 = no fluid)
            sampler2D _FluidColor;      // rgb pigment, a = wetness
            sampler2D_float _CameraDepthTexture;

            float4 _ReconProj;          // (1/P00, 1/P11, 0, 0) — view-ray reconstruction
            float2 _TexelSize;          // (1/width, 1/height) of the fluid RTs
            float3 _LightDirView;       // light direction in VIEW space (normalized)
            float _Ambient;             // ambient floor for the diffuse term
            float _GlossStrength;       // specular strength (× wetness)
            float _GlossPower;          // specular exponent (sharpness of the wet highlight)
            float _FresnelStrength;     // rim strength (× wetness)

            // Eye-space position from a screen uv + its eye depth (perspective).
            float3 ViewPos(float2 uv, float eye)
            {
                float2 ndc = uv * 2.0 - 1.0;
                return float3(ndc.x * _ReconProj.x, ndc.y * _ReconProj.y, -1.0) * eye;
            }

            float FluidEye(float2 uv) { return tex2D(_FluidDepth, uv).r; }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float3 sceneCol = tex2D(_MainTex, uv).rgb;

                float eyeC = FluidEye(uv);
                if (eyeC <= 0.0)
                    return fixed4(sceneCol, 1); // no fluid here

                // Occlude against scene geometry (the canvas, bucket walls, …).
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                if (eyeC >= sceneEye)
                    return fixed4(sceneCol, 1); // fluid is behind opaque scene

                // --- Reconstruct the surface normal from the smoothed depth (Green's neighbour-min:
                // pick the smaller delta per axis so edges don't halo). Fall back across invalid sides.
                float3 P = ViewPos(uv, eyeC);
                float eL = FluidEye(uv - float2(_TexelSize.x, 0));
                float eR = FluidEye(uv + float2(_TexelSize.x, 0));
                float eD = FluidEye(uv - float2(0, _TexelSize.y));
                float eU = FluidEye(uv + float2(0, _TexelSize.y));

                float3 dx = (eR > 0.0 && (eL <= 0.0 || abs(eR - eyeC) < abs(eyeC - eL)))
                    ? (ViewPos(uv + float2(_TexelSize.x, 0), eR) - P)
                    : (P - ViewPos(uv - float2(_TexelSize.x, 0), eL));
                float3 dy = (eU > 0.0 && (eD <= 0.0 || abs(eU - eyeC) < abs(eyeC - eD)))
                    ? (ViewPos(uv + float2(0, _TexelSize.y), eU) - P)
                    : (P - ViewPos(uv - float2(0, _TexelSize.y), eD));

                float3 N = normalize(cross(dx, dy));
                if (N.z < 0.0) N = -N; // face the camera (view +Z)

                // --- Paint shading (opaque) ---
                float4 cw = tex2D(_FluidColor, uv);
                float3 pigment = cw.rgb;
                float wetness = cw.a;

                float3 L = normalize(_LightDirView);
                float3 V = normalize(-P);          // toward the camera
                float3 H = normalize(L + V);

                float ndl = saturate(dot(N, L));
                float3 diffuse = pigment * (_Ambient + (1.0 - _Ambient) * ndl);

                // Gloss coupled to wetness: wet paint has a sharp highlight, dried paint is matte.
                float spec = wetness * _GlossStrength * pow(saturate(dot(N, H)), _GlossPower);
                // Subtle wet rim (Fresnel), also faded out as it dries.
                float fres = wetness * _FresnelStrength * pow(1.0 - saturate(dot(N, V)), 5.0);

                float3 paint = diffuse + spec + fres;

                // Edge anti-alias: interior pixels have all 4 neighbours filled (coverage 1); at the
                // silhouette some neighbours are empty, so the fluid feathers into the scene.
                float valid = 1.0;
                valid += (eL > 0.0) ? 1.0 : 0.0;
                valid += (eR > 0.0) ? 1.0 : 0.0;
                valid += (eD > 0.0) ? 1.0 : 0.0;
                valid += (eU > 0.0) ? 1.0 : 0.0;
                float coverage = valid / 5.0;

                float3 outCol = lerp(sceneCol, paint, coverage);
                return fixed4(outCol, 1);
            }
            ENDCG
        }
    }
}
