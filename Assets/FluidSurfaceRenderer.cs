using UnityEngine;
using UnityEngine.Rendering; // RenderTargetIdentifier, CompareFunction

// =====================================================================================
//  FluidSurfaceRenderer — renders the SPH paint as a continuous fluid SURFACE (screen-space).
// =====================================================================================
//  Put this on the Main Camera and assign the paint FluidSimGPU (set its Render Mode = Surface).
//  It reuses the sim's existing GPU buffers (positions/colours/wetness/…) — no extra sim state —
//  and runs the classic screen-space fluid pipeline (Simon Green / Sebastian Lague), adapted for
//  OPAQUE paint with a wetness-driven gloss:
//
//    1. FluidImpostor  — draw each particle as a sphere impostor -> nearest eye-depth + colour + wetness.
//    2. FluidBlur      — bilateral (edge-preserving) blur of the depth -> a smooth surface.
//    3. FluidComposite — reconstruct normals from the smoothed depth, shade the paint, composite
//                        over the scene where the fluid is in front (via _CameraDepthTexture).
//
//  Built-in render pipeline (OnRenderImage). No libraries — all three shaders are hand-written.
//  See FluidRendering.md.
// =====================================================================================
[RequireComponent(typeof(Camera))]
public class FluidSurfaceRenderer : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The paint sim to render. Auto-found if left empty. Set its Render Mode = Surface.")]
    [SerializeField]
    private FluidSimGPU sim;

    [Tooltip(
        "Impostor radius as a multiple of the particle radius. >1 makes the spheres overlap so they merge into a surface. ~1.5 is a good start."
    )]
    [SerializeField]
    private float radiusScale = 1.5f;

    [Header("Surface smoothing")]
    [Range(1, 32)]
    [SerializeField]
    private int blurRadius = 12;

    [Tooltip(
        "Bilateral edge preservation: higher keeps silhouettes crisper (less bleed across the fluid edge)."
    )]
    [SerializeField]
    private float blurDepthFalloff = 8f;

    [Range(1, 4)]
    [SerializeField]
    private int blurIterations = 1;

    [Header("Paint shading")]
    [Tooltip(
        "Light direction in WORLD space (transformed to view space for the surface lighting)."
    )]
    [SerializeField]
    private Vector3 lightDirection = new Vector3(0.4f, 1f, 0.3f);

    [Range(0f, 1f)]
    [SerializeField]
    private float ambient = 0.35f;

    [Tooltip(
        "Specular strength of the WET sheen (scaled by per-particle wetness → dried paint is matte)."
    )]
    [SerializeField]
    private float glossStrength = 0.6f;

    [Tooltip("Specular exponent — higher = smaller/sharper wet highlight.")]
    [SerializeField]
    private float glossPower = 48f;

    [Range(0f, 1f)]
    [Tooltip("Subtle wet Fresnel rim (also faded out as the paint dries).")]
    [SerializeField]
    private float fresnelStrength = 0.3f;

    public enum DebugView
    {
        Off, // normal composite
        SceneColor, // raw scene passthrough (is src the scene?)
        FluidColor, // the impostor pigment buffer (where is the paint on screen?)
        FluidDepth, // the smoothed depth (white = has fluid; should be BLACK where there's none)
    }

    [Header("Debug")]
    [Tooltip("DIAGNOSTIC view: Off = normal. SceneColor = raw scene. FluidColor = the paint the impostors drew. FluidDepth = the fluid depth mask (should be black except where the paint is; if it's white everywhere, that's the bug).")]
    [SerializeField]
    private DebugView debugView = DebugView.Off;

    Camera cam;
    Material impostorMat,
        blurMat,
        compositeMat;

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        cam.depthTextureMode |= DepthTextureMode.Depth; // scene depth for occlusion
        if (sim == null)
            sim = FindObjectOfType<FluidSimGPU>();

        impostorMat = MakeMat("Hidden/FluidImpostor");
        blurMat = MakeMat("Hidden/FluidBlur");
        compositeMat = MakeMat("Hidden/FluidComposite");
    }

    void OnDisable()
    {
        DestroyMat(ref impostorMat);
        DestroyMat(ref blurMat);
        DestroyMat(ref compositeMat);
    }

    static Material MakeMat(string shader)
    {
        var s = Shader.Find(shader);
        return s != null ? new Material(s) { hideFlags = HideFlags.HideAndDontSave } : null;
    }

    static void DestroyMat(ref Material m)
    {
        if (m != null)
            Destroy(m);
        m = null;
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        // Fall through to the plain scene unless everything is ready and Surface mode is on.
        int count = sim != null ? sim.ActiveParticleCount : 0;
        if (
            sim == null
            || !sim.RenderAsSurface
            || count < 2
            || sim.PositionsBuffer == null
            || impostorMat == null
            || blurMat == null
            || compositeMat == null
        )
        {
            Graphics.Blit(src, dst);
            return;
        }

        if (debugView == DebugView.SceneColor)
        {
            Graphics.Blit(src, dst); // diagnostic: does the raw scene show while the effect is active?
            return;
        }

        int w = src.width,
            h = src.height;

        // --- RTs (temporary; auto-managed). depthRT owns the 24-bit depth buffer both impostor
        // passes share; colorRT needs no depth of its own.
        // All ARGBHalf: RFloat did not clear reliably on the target GPU (garbage depth -> whole
        // screen read as fluid). Depth is stored in the red channel of an ARGBHalf target instead.
        var colorRT = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
        var depthRT = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGBHalf);
        var smoothA = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
        var smoothB = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);

        // --- Sphere impostor uniforms ---
        impostorMat.SetBuffer("Positions", sim.PositionsBuffer);
        impostorMat.SetBuffer("Colors", sim.ColorsBuffer);
        impostorMat.SetBuffer("Absorbed", sim.AbsorbedBuffer);
        impostorMat.SetBuffer("Wetness", sim.WetnessBuffer);
        impostorMat.SetBuffer("MixLut", sim.MixLutBuffer);
        impostorMat.SetInt("MixLutSize", sim.MixLutCount);
        impostorMat.SetFloat("_Radius", Mathf.Max(1e-4f, sim.ParticleWorldRadius * radiusScale));
        // Camera matrices passed explicitly (UNITY_MATRIX_V/P are unreliable for a manual
        // DrawProceduralNow inside OnRenderImage). GetGPUProjectionMatrix(..., true) = the platform
        // projection for rendering INTO a texture (reversed-Z + RT flip), matching how the scene src
        // was rendered, so the fluid lines up with the scene.
        impostorMat.SetMatrix("_CamV", cam.worldToCameraMatrix);
        impostorMat.SetMatrix("_CamP", GL.GetGPUProjectionMatrix(cam.projectionMatrix, true));

        // Nearest-wins depth test matched to the platform Z convention: reversed-Z (D3D/Vulkan/Metal)
        // has far = 0 and "closer = greater" -> clear to 0 and test GEqual; otherwise far = 1, LEqual.
        bool reversed = SystemInfo.usesReversedZBuffer;
        impostorMat.SetInt(
            "_ZTest",
            (int)(reversed ? CompareFunction.GreaterEqual : CompareFunction.LessEqual)
        );

        // Two SEPARATE single-target passes (NOT MRT — old GPUs write the 2nd MRT target even for
        // clipped pixels, which filled the whole depth image and painted the scene black). The RFloat
        // colour is cleared with a black BLIT because GL.Clear on RFloat proved unreliable on this GPU
        // (left garbage -> whole screen read as fluid). GL.Clear is used only for the depth BUFFER.
        // Pass 0 — eye depth (+ hardware depth for nearest-wins).
        Graphics.Blit(Texture2D.blackTexture, depthRT); // eye-depth colour -> 0 (empty)
        Graphics.SetRenderTarget(depthRT);
        GL.Clear(true, false, Color.clear, reversed ? 0f : 1f); // depth buffer -> far (keep colour 0)
        impostorMat.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, 6 * count);

        // Pass 1 — colour + wetness, reusing pass 0's depth buffer so only the NEAREST fragment writes.
        Graphics.Blit(Texture2D.blackTexture, colorRT); // colour -> 0 (empty)
        Graphics.SetRenderTarget(colorRT.colorBuffer, depthRT.depthBuffer);
        impostorMat.SetPass(1);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, 6 * count);

        // --- Pass 2: bilateral blur the eye depth into a smooth surface (separable H then V).
        blurMat.SetInt("_BlurRadius", blurRadius);
        blurMat.SetFloat("_DepthFalloff", blurDepthFalloff);
        RenderTexture depthSrc = depthRT;
        for (int it = 0; it < blurIterations; it++)
        {
            blurMat.SetVector("_BlurDir", new Vector4(1f / w, 0f, 0f, 0f));
            Graphics.Blit(depthSrc, smoothA, blurMat, 0);
            blurMat.SetVector("_BlurDir", new Vector4(0f, 1f / h, 0f, 0f));
            Graphics.Blit(smoothA, smoothB, blurMat, 0);
            depthSrc = smoothB; // feed the result back in for the next iteration
        }

        // Diagnostic buffer views (screenshot these to tell what each pass produced).
        if (debugView == DebugView.FluidColor || debugView == DebugView.FluidDepth)
        {
            Graphics.Blit(debugView == DebugView.FluidColor ? colorRT : smoothB, dst);
            RenderTexture.ReleaseTemporary(colorRT);
            RenderTexture.ReleaseTemporary(depthRT);
            RenderTexture.ReleaseTemporary(smoothA);
            RenderTexture.ReleaseTemporary(smoothB);
            return;
        }

        // --- Pass 3: normals from depth + paint shading + composite over the scene.
        Matrix4x4 proj = cam.projectionMatrix;
        compositeMat.SetVector("_ReconProj", new Vector4(1f / proj.m00, 1f / proj.m11, 0f, 0f));
        compositeMat.SetVector("_TexelSize", new Vector4(1f / w, 1f / h, 0f, 0f));
        Vector3 lView = cam.worldToCameraMatrix.MultiplyVector(lightDirection.normalized);
        compositeMat.SetVector("_LightDirView", lView.normalized);
        compositeMat.SetFloat("_Ambient", ambient);
        compositeMat.SetFloat("_GlossStrength", glossStrength);
        compositeMat.SetFloat("_GlossPower", Mathf.Max(1f, glossPower));
        compositeMat.SetFloat("_FresnelStrength", fresnelStrength);
        compositeMat.SetTexture("_FluidDepth", smoothB);
        compositeMat.SetTexture("_FluidColor", colorRT);
        Graphics.Blit(src, dst, compositeMat, 0);

        RenderTexture.ReleaseTemporary(colorRT);
        RenderTexture.ReleaseTemporary(depthRT);
        RenderTexture.ReleaseTemporary(smoothA);
        RenderTexture.ReleaseTemporary(smoothB);
    }
}
