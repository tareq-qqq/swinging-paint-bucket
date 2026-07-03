using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem; // new Input System (matches the rest of the project) for the export key

// =====================================================================================
//  PaintCanvas — the surface the paint lands on, stains, soaks into and dries onto.
// =====================================================================================
//  This is the ONLY file (plus one compute kernel + one shader) that knows about the
//  canvas. Assign it to a FluidSimGPU's "Canvas" slot and the paint that leaves the
//  bucket (or, in the box demo, any paint) collides with this quad, deposits its colour
//  into a per-texel DEPOSIT MAP, soaks in and cures onto it. Leave the slot empty and the
//  box/bucket demos are completely unchanged.
//
//  Four canvas TYPES (Canvas / Paper / Wood / Steel) are one mechanism with different
//  per-material coefficients — absorbency, wettability, adhesion, friction — grounded in
//  wetting/absorption physics (contact angle, porosity/capillarity, surface energy,
//  roughness). See Surfaces.md.
//
//  The quad lies in this GameObject's LOCAL XZ plane (normal = local +Y), so an un-rotated
//  canvas is a horizontal table the drips fall onto; rotate the GameObject for an easel.
//
//  No Unity physics — the contact is hand-written in FluidCompute.compute (CanvasContact),
//  and this component just owns the geometry, the deposit buffer and the canvas rendering.
// =====================================================================================
public class PaintCanvas : MonoBehaviour
{
    public enum SurfaceType
    {
        Canvas, // soaks, spreads, grips, stains strongly
        Paper,  // soaks fastest, can saturate/bleed
        Wood,   // partial soak, medium grip
        Steel,  // repels — paint beads, slides, barely marks
    }

    [Header("Material")]
    [Tooltip("Picks the four required canvas types. Sets the coefficients + base colour below.")]
    [SerializeField]
    private SurfaceType surfaceType = SurfaceType.Canvas;

    [Tooltip("Re-apply the preset's coefficients/colour (tick to reset after hand-tweaking).")]
    [SerializeField]
    private bool applyPreset = true;

    [Header("Coefficients (0..1) — set by the preset, override if you like")]
    [Range(0f, 1f)]
    [Tooltip("Porosity / capillary action: how fast the surface pulls moisture out of the paint.")]
    public float absorbency = 0.7f;

    [Range(0f, 1f)]
    [Tooltip("Wetting (contact angle): high = paint sticks flat & spreads; low = beads and bounces.")]
    public float wettability = 0.8f;

    [Range(0f, 1f)]
    [Tooltip("Surface energy: how strongly paint grips / how much colour it deposits.")]
    public float adhesion = 0.8f;

    [Range(0f, 1f)]
    [Tooltip("Roughness: tangential grip that stops the paint sliding across the surface.")]
    public float friction = 0.7f;

    [Header("Geometry")]
    [Tooltip("Canvas size along the GameObject's local X.")]
    public float width = 12f;

    [Tooltip("Canvas size along the GameObject's local Z.")]
    public float height = 12f;

    [Tooltip("Half-thickness of the contact band around the surface (world units). ~ particle size.")]
    public float contactThickness = 0.3f;

    [Header("Deposit map (the artwork)")]
    [Tooltip("Deposit-map resolution — where the paint is STAMPED. Keep this MODERATE (256–512). Too high and each drip becomes a tiny dot with gaps (the brush can't span the particle spacing) — the paint turns speckly. For a crisp PNG use 'Export Resolution' below, NOT this.")]
    public int resolution = 384;

    [Tooltip("Resolution of the exported PNG. The deposit map is smoothly upscaled to this, so you get a crisp image without breaking the paint coverage. 2048 is a nice print size.")]
    public int exportResolution = 2048;

    [Tooltip("Colour of the blank surface before any paint lands.")]
    public Color baseColor = new Color(0.94f, 0.92f, 0.87f);

    [Tooltip("How much accumulated paint a texel needs to show at full opacity (LOWER = bolder, more opaque marks; higher = fainter/more translucent).")]
    public float opacityBuildup = 1.5f;

    [Tooltip("Saturation of the deposited paint. Overlapping different-coloured drips average toward grey; >1 restores vividness. 1 = physically-honest average.")]
    public float vividness = 1.6f;

    [Tooltip("World radius each drip stamps onto the canvas. ~ the paint's particle size, so drips make CONTINUOUS strokes instead of dots. Too small = speckly, too big = blobby.")]
    public float brushRadius = 0.3f;

    [Header("Contact physics constants (rarely changed)")]
    [Tooltip("Young–Laplace capillary suction (lumped 2·surfaceTension/poreRadius). Higher = paint soaks in faster on a wetting surface.")]
    public float capillaryStrength = 3f;

    [Tooltip("How strongly the paint's own viscosity (Darcy's μ) resists being absorbed. Higher = thick paint soaks in much slower.")]
    public float viscousDrag = 2f;

    [Tooltip("How strongly an already-saturated patch chokes further absorption (Lucas–Washburn slow-down / bleed).")]
    public float saturationChoke = 6f;

    [Tooltip("Rate a thin surface film stains a NON-absorbing surface (grip via adhesion, e.g. paint drying on steel).")]
    public float filmRate = 0.4f;

    [Tooltip("Scales friction into a per-second tangential damping.")]
    public float frictionRate = 12f;

    [Tooltip("How fast a fresh (wet) paint layer dries and 'commits' so later paint covers it instead of blending. Also scaled by the EnvironmentConfig temperature/humidity and the surface absorbency. Higher = paints over sooner.")]
    public float layerDryRate = 0.6f;

    [Header("Export (save the finished artwork as a PNG)")]
    [Tooltip("Press this key at run time to save the canvas as a PNG. None = disabled (still exportable from the component's right-click menu).")]
    public Key exportKey = Key.P;

    [Tooltip("Folder to save canvas PNGs into. Leave empty to use the app's persistent data folder (the path is logged to the Console on save).")]
    public string saveFolder = "";

    const float DepositScale = 1024f; // must match DEPOSIT_SCALE in FluidCompute.compute
    const int DepositStride = 9; // uints per texel — must match CANVAS_STRIDE in the compute + shader

    ComputeBuffer depositBuf; // resolution*resolution*4 uints: R, G, B, coverage (fixed point)
    Mesh quadMesh;
    Material surfaceMaterial;
    int builtResolution;
    float builtWidth,
        builtHeight;
    SurfaceType lastAppliedPreset;

    // FluidSimGPU checks this before binding/dispatching the canvas kernel.
    public bool Active => isActiveAndEnabled;

    // Geometry the solver reads (pose = this transform; quad in local XZ, normal local +Y).
    public Transform Pose => transform;
    public float Width => width;
    public float Height => height;
    public float ContactThickness => contactThickness;
    public float BrushRadius => Mathf.Max(0f, brushRadius);
    public int ResX => Mathf.Max(1, resolution);
    public int ResY => Mathf.Max(1, resolution);
    public ComputeBuffer DepositBuffer
    {
        get
        {
            EnsureResources();
            return depositBuf;
        }
    }

    void OnEnable()
    {
        if (applyPreset)
            ApplyPreset(surfaceType);
        EnsureResources();
    }

    void OnDisable() => ReleaseResources();

    void OnValidate()
    {
        // Live preset swap in the editor.
        if (applyPreset && surfaceType != lastAppliedPreset)
            ApplyPreset(surfaceType);
    }

    // Allocate the deposit buffer (zeroed) + the quad mesh + the surface material on demand.
    // Called both by FluidSimGPU (before it binds the buffer) and here (before rendering), so
    // whichever runs first wins and the other is a no-op.
    public void EnsureResources()
    {
        int res = Mathf.Max(1, resolution);
        if (depositBuf == null || builtResolution != res)
        {
            ReleaseDepositBuffer();
            depositBuf = new ComputeBuffer(res * res * DepositStride, sizeof(uint));
            depositBuf.SetData(new uint[res * res * DepositStride]); // blank canvas
            builtResolution = res;
        }
        if (surfaceMaterial == null)
        {
            Shader sh = Shader.Find("Custom/PaintCanvasSurface");
            surfaceMaterial = new Material(sh);
        }
        if (quadMesh == null || !Mathf.Approximately(builtWidth, width) || !Mathf.Approximately(builtHeight, height))
            RebuildQuad();
    }

    void Update()
    {
        // Press the export key to save the current canvas artwork to a PNG.
        if (exportKey != Key.None && Keyboard.current != null && Keyboard.current[exportKey].wasPressedThisFrame)
            ExportPng();
    }

    void LateUpdate()
    {
        EnsureResources();

        // Bind the deposit map + params and draw the canvas quad at this transform. The shader reads
        // the accumulated pigment per texel and blends it over the blank surface colour.
        surfaceMaterial.SetBuffer("CanvasDeposit", depositBuf);
        surfaceMaterial.SetInt("CanvasResX", ResX);
        surfaceMaterial.SetInt("CanvasResY", ResY);
        surfaceMaterial.SetColor("_BaseColor", baseColor);
        surfaceMaterial.SetFloat("_DepositScale", DepositScale);
        surfaceMaterial.SetFloat("_Coverage", Mathf.Max(0.01f, opacityBuildup));
        surfaceMaterial.SetFloat("_Vividness", Mathf.Max(0f, vividness));

        Matrix4x4 m = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        var rp = new RenderParams(surfaceMaterial)
        {
            worldBounds = new Bounds(transform.position, Vector3.one * 10000f),
        };
        Graphics.RenderMesh(rp, quadMesh, 0, m);
    }

    // The four required canvas types = one contact model, four coefficient sets (see Surfaces.md).
    void ApplyPreset(SurfaceType type)
    {
        switch (type)
        {
            case SurfaceType.Canvas: // cotton canvas: soaks, spreads, grips, stains strongly
                absorbency = 0.70f; wettability = 0.85f; adhesion = 0.85f; friction = 0.70f;
                baseColor = new Color(0.94f, 0.92f, 0.87f);
                break;
            case SurfaceType.Paper: // very porous: soaks fastest, strong mark, can saturate/bleed
                absorbency = 0.95f; wettability = 0.90f; adhesion = 0.65f; friction = 0.55f;
                baseColor = new Color(0.98f, 0.98f, 0.96f);
                break;
            case SurfaceType.Wood: // partial soak, medium grip
                absorbency = 0.40f; wettability = 0.55f; adhesion = 0.55f; friction = 0.55f;
                baseColor = new Color(0.72f, 0.55f, 0.35f);
                break;
            case SurfaceType.Steel: // non-porous, non-wetting: paint beads, slides, barely marks
                absorbency = 0.02f; wettability = 0.10f; adhesion = 0.10f; friction = 0.15f;
                baseColor = new Color(0.55f, 0.57f, 0.60f);
                break;
        }
        lastAppliedPreset = type;
    }

    void RebuildQuad()
    {
        if (quadMesh == null)
            quadMesh = new Mesh { name = "PaintCanvasQuad" };
        else
            quadMesh.Clear();

        float hw = width * 0.5f,
            hh = height * 0.5f;
        // Quad in the local XZ plane, normal = local +Y (an un-rotated canvas is horizontal).
        quadMesh.vertices = new[]
        {
            new Vector3(-hw, 0f, -hh),
            new Vector3(hw, 0f, -hh),
            new Vector3(hw, 0f, hh),
            new Vector3(-hw, 0f, hh),
        };
        quadMesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        // UV matches the deposit map: u along local X, v along local Z (= the CanvasContact kernel).
        quadMesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        quadMesh.RecalculateBounds();
        builtWidth = width;
        builtHeight = height;
    }

    // Save the canvas artwork as a PNG. Rather than re-deriving colours on the CPU (which is easy to
    // get wrong in a Linear colour-space project — the deposit holds LINEAR RGB, so writing it
    // straight to a PNG comes out crushed/black), we render the canvas THROUGH THE SAME SHADER into a
    // RenderTexture and read that back. The GPU does the exact linear->sRGB conversion the on-screen
    // canvas gets, so the PNG matches the view. Returns the full file path.
    [ContextMenu("Export Canvas PNG")]
    public string ExportPng()
    {
        EnsureResources();
        // The PNG is rendered at EXPORT resolution, upscaled from the (moderate) deposit map by the
        // shader's bilinear sampling — so the image is crisp without the deposit map having to be
        // huge (which would turn the paint into dots).
        int outRes = Mathf.Clamp(exportResolution, 16, 8192);

        // Bind the deposit map + params so the material renders the same image as on screen.
        surfaceMaterial.SetBuffer("CanvasDeposit", depositBuf);
        surfaceMaterial.SetInt("CanvasResX", ResX);
        surfaceMaterial.SetInt("CanvasResY", ResY);
        surfaceMaterial.SetColor("_BaseColor", baseColor);
        surfaceMaterial.SetFloat("_DepositScale", DepositScale);
        surfaceMaterial.SetFloat("_Coverage", Mathf.Max(0.01f, opacityBuildup));
        surfaceMaterial.SetFloat("_Vividness", Mathf.Max(0f, vividness));

        // Render the canvas shader over a full RenderTexture and read it back. Use the project's
        // colour space (Default) — NOT a forced sRGB target — so the PNG matches the live canvas in
        // both Linear and Gamma projects (forcing sRGB double-converts in a Gamma project and the
        // screenshot came out too contrasty/opaque versus the view).
        var rt = RenderTexture.GetTemporary(outRes, outRes, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        rt.filterMode = FilterMode.Bilinear;
        Graphics.Blit(Texture2D.whiteTexture, rt, surfaceMaterial); // source unused by the shader

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(outRes, outRes, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, outRes, outRes), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        byte[] png = tex.EncodeToPNG();
        if (Application.isPlaying)
            Destroy(tex);
        else
            DestroyImmediate(tex);

        string dir = string.IsNullOrEmpty(saveFolder) ? Application.persistentDataPath : saveFolder;
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"canvas_{surfaceType}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        File.WriteAllBytes(file, png);
        Debug.Log($"[PaintCanvas] Saved canvas image ({outRes}×{outRes}, from a {builtResolution} deposit map) to: {file}");
        return file;
    }

    // Wipe the artwork back to a blank canvas (handy between test runs).
    [ContextMenu("Clear Canvas")]
    public void ClearCanvas()
    {
        if (depositBuf != null)
            depositBuf.SetData(new uint[builtResolution * builtResolution * DepositStride]);
    }

    void ReleaseDepositBuffer()
    {
        depositBuf?.Release();
        depositBuf = null;
    }

    void ReleaseResources()
    {
        ReleaseDepositBuffer();
        if (surfaceMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(surfaceMaterial);
            else
                DestroyImmediate(surfaceMaterial);
            surfaceMaterial = null;
        }
        if (quadMesh != null)
        {
            if (Application.isPlaying)
                Destroy(quadMesh);
            else
                DestroyImmediate(quadMesh);
            quadMesh = null;
        }
    }

    void OnDrawGizmos()
    {
        // Outline the canvas footprint in the Scene view so it's easy to place under the bucket.
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
        float hw = width * 0.5f,
            hh = height * 0.5f;
        Vector3 a = new Vector3(-hw, 0f, -hh),
            b = new Vector3(hw, 0f, -hh),
            c = new Vector3(hw, 0f, hh),
            d = new Vector3(-hw, 0f, hh);
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);
    }
}
