using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadback for rest-density calibration

// =====================================================================================
//  FluidSimGPU — GPU SPH fluid: C# driver for FluidCompute.compute
// =====================================================================================
//  The simulation math is identical to the CPU FluidSim3D.cs; this class just owns the
//  GPU buffers, sets the kernel uniforms, dispatches the kernels each FixedUpdate, and
//  draws the particles by reading their positions straight out of the GPU buffer.
//
//  Setup: attach to an empty GameObject, drag FluidCompute.compute into the "Compute"
//  slot, press Play. Move/rotate the GameObject to slosh/tilt the container. The CPU
//  FluidSim3D.cs is left as the readable reference; this is the high-particle-count path.
//
//  No Unity physics, no libraries — all hand-written. See GPU.md.
// =====================================================================================
public class FluidSimGPU : MonoBehaviour
{
    [Header("Compute")]
    [Tooltip("Drag FluidCompute.compute here.")]
    public ComputeShader compute;

    [Header("Spawn")]
    public int particleCount = 20000;
    public float particleSpacing = 0.25f;
    public Vector3 spawnCentre = new Vector3(0f, 1.5f, 0f);
    public float spawnJitter = 0.02f;

    [Header("Fluid (paint properties)")]
    public float smoothingRadius = 0.5f;
    public float restDensity = 12f;
    public float pressureMultiplier = 200f;
    public float nearPressureMultiplier = 12f;
    public float viscosityStrength = 0.12f;
    public bool autoCalibrateRestDensity = true;

    [Header("Environment")]
    public Vector3 gravity = new Vector3(0f, -10f, 0f);

    [Range(0f, 1f)]
    public float collisionDamping = 0.45f;

    [Header("Environment — Temperature & Humidity")]
    [Tooltip("Ambient air temperature (°C). Warmer = thinner paint and faster drying.")]
    public float ambientTemperature = 20f;

    [Range(0f, 1f)]
    [Tooltip("Air humidity. Humid air dries slowly; dry air dries fast.")]
    public float humidity = 0.5f;

    [Tooltip("Strength of temperature → viscosity. 0 = off.")]
    public float tempViscosityFactor = 0.03f;

    [Header("Environment — Drying / Curing")]
    public bool enableDrying = true;
    public float dryingRate = 0.05f;

    [Range(0f, 1f)]
    public float setWetnessThreshold = 0.15f;

    [Range(0f, 1f)]
    [Tooltip(
        "Density fraction below which a particle counts as air-exposed (surface). Higher = thicker exposed skin; too low and nothing registers as surface."
    )]
    public float airExposureThreshold = 0.95f;

    [Header("Environment — Air motion (exposed paint only)")]
    public bool enableAirEffects = true;
    public Vector3 windForce = Vector3.zero;
    public float airDrag = 0.5f;

    [Header("Bounds (move/rotate this GameObject to slosh the fluid)")]
    public Vector3 boundsSize = new Vector3(10f, 10f, 10f);

    [Header("Simulation")]
    [Tooltip(
        "Physics sub-steps per frame. 1 = fastest (halves dispatches); higher = more stable under hard shaking."
    )]
    [Range(1, 10)]
    public int iterationsPerFrame = 1;

    [Tooltip(
        "Below this framerate the sim slows down (clamps its timestep) instead of taking huge unstable steps. Lower = stays real-time longer but risks jitter; higher = goes slow-motion sooner but stays stable."
    )]
    public float minStableFramerate = 30f;

    [Header("Color mixing (2 paints)")]
    public bool enableColorMixing = true;

    [Tooltip(
        "Paint A — pick ANY colour from the wheel. It is converted to a reflectance spectrum and mixed subtractively via Kubelka-Munk, so e.g. blue + yellow = green (not grey)."
    )]
    [ColorUsage(false)]
    public Color paintColorA = new Color(0.10f, 0.20f, 0.85f); // blue (left half of the spawn block)

    [Tooltip("Paint B — pick ANY colour from the wheel (subtractive Kubelka-Munk mixing).")]
    [ColorUsage(false)]
    public Color paintColorB = new Color(0.95f, 0.85f, 0.10f); // yellow (right half)

    [Range(0f, 1.5f)]
    [Tooltip(
        "Extra saturation for MIXED colours (real subtractive mixes are muted; this makes secondaries like green/orange punchier). 0 = physically honest, higher = more vivid. Pure picked paints are unaffected."
    )]
    public float mixVibrance = 0.45f;

    [Tooltip(
        "Overall pigment mixing speed (fraction toward the neighbour-average color per second)."
    )]
    public float colorMixRate = 1f;

    [Tooltip(
        "How much 'moving against each other' (head-on) speeds mixing up vs parallel/shear motion."
    )]
    public float shakeMixBoost = 0.5f;

    [Header("Rendering")]
    public float particleScale = 0.2f;

    // --- GPU buffers ---
    ComputeBuffer positionsBuf,
        predictedBuf,
        velocitiesBuf; // float3
    ComputeBuffer densitiesBuf; // float2
    ComputeBuffer wetnessBuf; // float (1 = wet, 0 = cured)
    ComputeBuffer colorsBuf; // float3: .x = mix fraction t (0=paint A, 1=paint B)
    ComputeBuffer mixLutBuf; // float3 LUT: t -> displayed colour (spectral Kubelka-Munk)
    const int MixLutSize = 64;
    Color lutColorA,
        lutColorB; // last colours the LUT was built for (rebuild on change)
    ComputeBuffer spatialKeysBuf,
        spatialIndicesBuf; // uint, padded to pow2
    ComputeBuffer cellStartBuf; // uint, size numParticles

    // --- kernel indices ---
    int kExternalForces,
        kUpdateHash,
        kSort,
        kOffsets,
        kDensities,
        kEnvironment,
        kPressure,
        kViscosity,
        kMixColors,
        kUpdatePositions;

    int numParticles;
    int paddedCount; // next power of two >= numParticles (for the bitonic sort)

    // --- rendering ---
    Mesh particleMesh;
    Material particleMaterial;
    Bounds drawBounds;

    const int GROUP = 64; // numthreads for the per-particle kernels
    const int SORT_GROUP = 128; // numthreads for the bitonic sort kernel

    // =================================================================================
    void Start()
    {
        if (compute == null)
        {
            Debug.LogError("FluidSimGPU: assign FluidCompute.compute to the 'Compute' slot.");
            enabled = false;
            return;
        }

        // Confirm which GPU/backend Unity is actually using (Intel integrated vs NVIDIA dGPU).
        Debug.Log(
            $"[FluidSimGPU] GPU: {SystemInfo.graphicsDeviceName} | API: {SystemInfo.graphicsDeviceType} | compute: {SystemInfo.supportsComputeShaders}"
        );

        CacheKernels();
        InitBuffers();
        BindBuffers();
        BuildRenderResources();

        if (autoCalibrateRestDensity)
            CalibrateRestDensity();
    }

    // Simulate in Update (not FixedUpdate) so we run EXACTLY iterationsPerFrame sub-steps per
    // rendered frame. FixedUpdate would run extra times to "catch up" when a step is heavy,
    // multiplying the ~200 dispatches/step into a death spiral — the classic GPU-sim lag trap.
    void Update()
    {
        if (numParticles >= 2)
        {
            float frameDt = Mathf.Min(Time.deltaTime, 1f / Mathf.Max(1f, minStableFramerate)); // clamp so a hitch can't explode the sim
            float dt = frameDt / iterationsPerFrame;
            for (int i = 0; i < iterationsPerFrame; i++)
                SimulationStep(dt);
        }

        // Live inspector tuning: rebuild the t -> colour LUT only when a paint colour changes.
        if (mixLutBuf != null && (paintColorA != lutColorA || paintColorB != lutColorB))
            BuildMixLut();

        RenderParticles();
    }

    void OnDestroy()
    {
        ReleaseBuffers();
        if (particleMaterial != null)
            Destroy(particleMaterial);
        if (particleMesh != null)
            Destroy(particleMesh);
    }

    // =================================================================================
    //  Setup
    // =================================================================================
    void CacheKernels()
    {
        kExternalForces = compute.FindKernel("ExternalForces");
        kUpdateHash = compute.FindKernel("UpdateSpatialHash");
        kSort = compute.FindKernel("BitonicSort");
        kOffsets = compute.FindKernel("CalculateOffsets");
        kDensities = compute.FindKernel("CalculateDensities");
        kEnvironment = compute.FindKernel("Environment");
        kPressure = compute.FindKernel("CalculatePressureForce");
        kViscosity = compute.FindKernel("CalculateViscosity");
        kMixColors = compute.FindKernel("MixColors");
        kUpdatePositions = compute.FindKernel("UpdatePositions");
    }

    void InitBuffers()
    {
        numParticles = Mathf.Max(0, particleCount);
        paddedCount = Mathf.NextPowerOfTwo(Mathf.Max(2, numParticles));

        positionsBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        predictedBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        velocitiesBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        densitiesBuf = new ComputeBuffer(numParticles, sizeof(float) * 2);
        wetnessBuf = new ComputeBuffer(numParticles, sizeof(float));
        colorsBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        spatialKeysBuf = new ComputeBuffer(paddedCount, sizeof(uint));
        spatialIndicesBuf = new ComputeBuffer(paddedCount, sizeof(uint));
        cellStartBuf = new ComputeBuffer(numParticles, sizeof(uint));

        // Spawn a centred cube grid on the CPU and upload.
        var positions = new Vector3[numParticles];
        var velocities = new Vector3[numParticles];
        SpawnInGrid(positions, velocities);

        positionsBuf.SetData(positions);
        predictedBuf.SetData(positions); // predicted = positions until the first step
        velocitiesBuf.SetData(velocities);

        // The per-particle colour buffer stores a single MIX FRACTION t in .x (0 = paint A,
        // 1 = paint B); .y/.z are unused. The diffusion kernel averages this scalar, and the
        // shader maps t -> displayed colour through the spectral Kubelka-Munk LUT (see BuildMixLut).
        // Storing t (not RGB) keeps mixing in spectral space with exact, vivid endpoint colours.
        var wet = new float[numParticles];
        var colors = new Vector3[numParticles];
        for (int i = 0; i < numParticles; i++)
        {
            wet[i] = 1f; // fully wet to start
            // Two paints: left half of the spawn block = A (t=0), right half = B (t=1).
            float t = positions[i].x < spawnCentre.x ? 0f : 1f;
            colors[i] = new Vector3(t, 0f, 0f);
        }
        wetnessBuf.SetData(wet);
        colorsBuf.SetData(colors);

        BuildMixLut();
    }

    void SpawnInGrid(Vector3[] positions, Vector3[] velocities)
    {
        int perAxis = Mathf.Max(1, Mathf.CeilToInt(Mathf.Pow(numParticles, 1f / 3f)));
        float blockExtent = (perAxis - 1) * particleSpacing * 0.5f;

        for (int i = 0; i < numParticles; i++)
        {
            int x = i % perAxis;
            int y = (i / perAxis) % perAxis;
            int z = i / (perAxis * perAxis);
            Vector3 cell = new Vector3(x, y, z) * particleSpacing;
            Vector3 jitter = Random.insideUnitSphere * spawnJitter;
            positions[i] = spawnCentre - Vector3.one * blockExtent + cell + jitter;
            velocities[i] = Vector3.zero;
        }
    }

    void BindBuffers()
    {
        // ExternalForces
        compute.SetBuffer(kExternalForces, "Positions", positionsBuf);
        compute.SetBuffer(kExternalForces, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kExternalForces, "Velocities", velocitiesBuf);

        // UpdateSpatialHash
        compute.SetBuffer(kUpdateHash, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kUpdateHash, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kUpdateHash, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kUpdateHash, "CellStart", cellStartBuf);

        // BitonicSort
        compute.SetBuffer(kSort, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kSort, "SpatialIndices", spatialIndicesBuf);

        // CalculateOffsets
        compute.SetBuffer(kOffsets, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kOffsets, "CellStart", cellStartBuf);

        // CalculateDensities
        compute.SetBuffer(kDensities, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kDensities, "Densities", densitiesBuf);
        compute.SetBuffer(kDensities, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kDensities, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kDensities, "CellStart", cellStartBuf);

        // Environment
        compute.SetBuffer(kEnvironment, "Densities", densitiesBuf);
        compute.SetBuffer(kEnvironment, "Velocities", velocitiesBuf);
        compute.SetBuffer(kEnvironment, "Wetness", wetnessBuf);

        // CalculatePressureForce
        compute.SetBuffer(kPressure, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kPressure, "Velocities", velocitiesBuf);
        compute.SetBuffer(kPressure, "Densities", densitiesBuf);
        compute.SetBuffer(kPressure, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kPressure, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kPressure, "CellStart", cellStartBuf);

        // CalculateViscosity
        compute.SetBuffer(kViscosity, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kViscosity, "Velocities", velocitiesBuf);
        compute.SetBuffer(kViscosity, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kViscosity, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kViscosity, "CellStart", cellStartBuf);

        // MixColors
        compute.SetBuffer(kMixColors, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kMixColors, "Velocities", velocitiesBuf);
        compute.SetBuffer(kMixColors, "Colors", colorsBuf);
        compute.SetBuffer(kMixColors, "Wetness", wetnessBuf);
        compute.SetBuffer(kMixColors, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kMixColors, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kMixColors, "CellStart", cellStartBuf);

        // UpdatePositions
        compute.SetBuffer(kUpdatePositions, "Positions", positionsBuf);
        compute.SetBuffer(kUpdatePositions, "Velocities", velocitiesBuf);
    }

    // =================================================================================
    //  Per-step dispatch
    // =================================================================================
    void SimulationStep(float dt)
    {
        SetUniforms(dt);

        Dispatch(kExternalForces, numParticles);
        Dispatch(kUpdateHash, paddedCount); // padded so the tail gets sentinel keys
        DispatchSort();
        Dispatch(kOffsets, numParticles);
        Dispatch(kDensities, numParticles);
        if (enableDrying || enableAirEffects)
            Dispatch(kEnvironment, numParticles);
        Dispatch(kPressure, numParticles);
        Dispatch(kViscosity, numParticles);
        if (enableColorMixing)
            Dispatch(kMixColors, numParticles);
        Dispatch(kUpdatePositions, numParticles);
    }

    void SetUniforms(float dt)
    {
        float r = smoothingRadius;
        float r5 = r * r * r * r * r;
        float r6 = r5 * r;
        float r9 = r6 * r * r * r;

        compute.SetInt("numParticles", numParticles);
        compute.SetInt("numEntries", paddedCount);
        compute.SetFloat("smoothingRadius", r);
        compute.SetFloat("radiusSq", r * r);
        compute.SetFloat("deltaTime", dt);

        compute.SetFloat("restDensity", restDensity);
        compute.SetFloat("pressureMultiplier", pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);

        // Temperature thins/thickens ALL the paint (global) -> fold into effective viscosity.
        float tempViscMul = Mathf.Max(0.05f, 1f - tempViscosityFactor * (ambientTemperature - 20f));
        compute.SetFloat("effectiveViscosity", viscosityStrength * tempViscMul);

        // Environment uniforms.
        compute.SetInt("enableDrying", enableDrying ? 1 : 0);
        compute.SetInt("enableAirEffects", enableAirEffects ? 1 : 0);
        compute.SetFloat("ambientTemperature", ambientTemperature);
        compute.SetFloat("humidity", humidity);
        compute.SetFloat("dryingRate", dryingRate);
        compute.SetFloat("setWetnessThreshold", setWetnessThreshold);
        compute.SetFloat("airExposureThreshold", airExposureThreshold);
        compute.SetVector("windForce", windForce);
        compute.SetFloat("airDrag", airDrag);

        compute.SetFloat("colorMixRate", colorMixRate);
        compute.SetFloat("shakeMixBoost", shakeMixBoost);

        compute.SetFloat("densityScale", 15f / (2f * Mathf.PI * r5));
        compute.SetFloat("densityDerivScale", 15f / (Mathf.PI * r5));
        compute.SetFloat("nearDensityScale", 15f / (Mathf.PI * r6));
        compute.SetFloat("nearDensityDerivScale", 45f / (Mathf.PI * r6));
        compute.SetFloat("viscScale", 315f / (64f * Mathf.PI * r9));

        compute.SetVector("gravity", gravity);
        compute.SetFloat("collisionDamping", collisionDamping);
        compute.SetVector("boundsSize", boundsSize);

        // Box pose (rotation + translation only, no scale) for the rotated-box collision.
        Matrix4x4 localToWorld = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        compute.SetMatrix("localToWorld", localToWorld);
        compute.SetMatrix("worldToLocal", localToWorld.inverse);
    }

    // Standard GPU bitonic merge sort: one dispatch per (stage, step).
    void DispatchSort()
    {
        int numStages = (int)Mathf.Log(paddedCount, 2);
        int threadGroups = Mathf.CeilToInt((paddedCount / 2f) / SORT_GROUP);

        for (int stage = 0; stage < numStages; stage++)
        {
            for (int step = 0; step <= stage; step++)
            {
                int groupWidth = 1 << (stage - step);
                int groupHeight = 2 * groupWidth - 1;
                compute.SetInt("groupWidth", groupWidth);
                compute.SetInt("groupHeight", groupHeight);
                compute.SetInt("stepIndex", step);
                compute.Dispatch(kSort, threadGroups, 1, 1);
            }
        }
    }

    void Dispatch(int kernel, int threadCount)
    {
        int groups = Mathf.CeilToInt(threadCount / (float)GROUP);
        compute.Dispatch(kernel, Mathf.Max(1, groups), 1, 1);
    }

    // Run hash -> sort -> offsets -> densities once at rest and read back the average
    // density, so the spawn configuration is already in equilibrium (matches the CPU trick).
    void CalibrateRestDensity()
    {
        SetUniforms(0f); // dt = 0 → predicted positions = positions
        Dispatch(kExternalForces, numParticles);
        Dispatch(kUpdateHash, paddedCount);
        DispatchSort();
        Dispatch(kOffsets, numParticles);
        Dispatch(kDensities, numParticles);

        var req = AsyncGPUReadback.Request(densitiesBuf);
        req.WaitForCompletion();
        if (req.hasError)
            return;

        var data = req.GetData<Vector2>();
        float sum = 0f;
        for (int i = 0; i < data.Length; i++)
            sum += data[i].x;
        restDensity = sum / data.Length;
    }

    // =================================================================================
    //  Spectral colour — pick ANY two colours; mix them like real PAINT (subtractive),
    //  so blue + yellow = green, not the grey that averaging RGB gives.
    //
    //  Light mixes additively (RGB), but pigments mix SUBTRACTIVELY: each absorbs part of
    //  the spectrum and the absorptions stack. Real paint also needs a *spectrum* (not 3
    //  numbers): a blue pigment reflects some green, so blue+yellow keeps green and drops
    //  red/blue. We model this with single-constant Kubelka-Munk over SPECTRAL_BANDS bands:
    //
    //    1. Turn each picked colour into a reflectance spectrum R(λ)  (ColorToSpectrum).
    //    2. Per band, K/S = (1-R)^2 / (2R)  — the absorption/scatter ratio; it is LINEAR in
    //       pigment amount, so a 50/50 mix is just the average of the two K/S spectra.
    //    3. Mix: KS(t) = lerp(KS_A, KS_B, t); invert back to reflectance, then -> RGB.
    //
    //  Because the whole thing is a function of ONE number t (the mix fraction the particles
    //  carry and diffuse), we bake it once into a small t -> colour LUT on the CPU; the shader
    //  just samples the LUT. No per-frame spectral maths on the GPU, and easy to verify here.
    //  Endpoints are pinned EXACTLY to the picked colours (so pures stay vivid); only the blend
    //  in between follows the spectral curve.  No library — this is a few lines of KM physics.
    // =================================================================================
    const int SPECTRAL_BANDS = 20; // ~400..680 nm, CPU-only cost (LUT is precomputed)

    static float SmoothStep01(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    // Per-band reflectance "weights" for the red/green/blue content of a colour. Asymmetric on
    // purpose: blue reflects into green (overlap -> green survives a blue+yellow mix), but green
    // and red stay ~0 in the blue region (so yellow does NOT reflect blue).
    static void BandWeights(float wl, out float wR, out float wG, out float wB)
    {
        wB = Mathf.Lerp(1f, 0.40f, SmoothStep01(450f, 540f, wl)); // 1 in blue -> 0.4 in green
        wB = Mathf.Lerp(wB, 0.05f, SmoothStep01(540f, 610f, wl)); // 0.4 -> 0.05 into red
        wG = SmoothStep01(470f, 540f, wl) * (1f - 0.85f * SmoothStep01(580f, 660f, wl));
        wR = SmoothStep01(540f, 620f, wl);
    }

    static float BandWavelength(int b) => 400f + (680f - 400f) * b / (SPECTRAL_BANDS - 1);

    // sRGB colour -> reflectance spectrum (linear-light weighted sum of the band weights).
    static void ColorToSpectrum(Color c, float[] spectrum)
    {
        Color lin = c.linear;
        for (int b = 0; b < SPECTRAL_BANDS; b++)
        {
            BandWeights(BandWavelength(b), out float wR, out float wG, out float wB);
            float s = 0.02f + lin.r * wR + lin.g * wG + lin.b * wB; // 0.02 baseline keeps R>0
            spectrum[b] = Mathf.Clamp(s, 0.02f, 0.98f);
        }
    }

    // Reflectance spectrum -> linear RGB (band weights double as colour-matching sensitivities,
    // normalised so a flat reflectance of 1 maps to white).
    static Vector3 SpectrumToLinearRgb(float[] spectrum)
    {
        float r = 0f,
            g = 0f,
            b = 0f,
            nr = 0f,
            ng = 0f,
            nb = 0f;
        for (int i = 0; i < SPECTRAL_BANDS; i++)
        {
            BandWeights(BandWavelength(i), out float wR, out float wG, out float wB);
            r += spectrum[i] * wR;
            g += spectrum[i] * wG;
            b += spectrum[i] * wB;
            nr += wR;
            ng += wG;
            nb += wB;
        }
        return new Vector3(r / nr, g / ng, b / nb);
    }

    static float ReflectanceToKS(float r)
    {
        float v = 1f - r;
        return v * v / (2f * r);
    }

    static float KSToReflectance(float ks)
    {
        return 1f + ks - Mathf.Sqrt(ks * ks + 2f * ks);
    }

    // Bake the t -> displayed-colour LUT for the current paintColorA/paintColorB.
    void BuildMixLut()
    {
        if (mixLutBuf == null)
            mixLutBuf = new ComputeBuffer(MixLutSize, sizeof(float) * 3);

        var specA = new float[SPECTRAL_BANDS];
        var specB = new float[SPECTRAL_BANDS];
        ColorToSpectrum(paintColorA, specA);
        ColorToSpectrum(paintColorB, specB);

        var ksA = new float[SPECTRAL_BANDS];
        var ksB = new float[SPECTRAL_BANDS];
        for (int b = 0; b < SPECTRAL_BANDS; b++)
        {
            ksA[b] = ReflectanceToKS(specA[b]);
            ksB[b] = ReflectanceToKS(specB[b]);
        }

        // Spectral renders of the two endpoints, and the picked colours in linear light — the
        // difference is the per-endpoint correction that pins t=0/t=1 to the EXACT picked colour.
        Vector3 specRgbA = SpectrumToLinearRgb(specA);
        Vector3 specRgbB = SpectrumToLinearRgb(specB);
        Color linA = paintColorA.linear,
            linB = paintColorB.linear;
        Vector3 corrA = new Vector3(linA.r - specRgbA.x, linA.g - specRgbA.y, linA.b - specRgbA.z);
        Vector3 corrB = new Vector3(linB.r - specRgbB.x, linB.g - specRgbB.y, linB.b - specRgbB.z);

        var lut = new Vector3[MixLutSize];
        var ksMix = new float[SPECTRAL_BANDS];
        for (int k = 0; k < MixLutSize; k++)
        {
            float t = k / (float)(MixLutSize - 1);
            for (int b = 0; b < SPECTRAL_BANDS; b++)
                ksMix[b] = KSToReflectance(Mathf.Lerp(ksA[b], ksB[b], t)); // K/S mix -> reflectance
            Vector3 rgb = SpectrumToLinearRgb(ksMix);
            rgb += corrA * (1f - t) + corrB * t; // pin endpoints exactly to the picked colours

            // Optional vividness for the MIX only: push away from grey, faded out to 0 at the two
            // endpoints (1 - |2t-1|) so the picked pure paints are never altered.
            float boost = mixVibrance * (1f - Mathf.Abs(2f * t - 1f));
            if (boost > 0f)
            {
                float luma = 0.299f * rgb.x + 0.587f * rgb.y + 0.114f * rgb.z;
                rgb =
                    new Vector3(luma, luma, luma)
                    + (rgb - new Vector3(luma, luma, luma)) * (1f + boost);
            }
            lut[k] = new Vector3(Mathf.Clamp01(rgb.x), Mathf.Clamp01(rgb.y), Mathf.Clamp01(rgb.z));
        }

        mixLutBuf.SetData(lut);
        lutColorA = paintColorA;
        lutColorB = paintColorB;
    }

    // =================================================================================
    //  Rendering — draw instances reading position from the GPU buffer (no readback)
    // =================================================================================
    void BuildRenderResources()
    {
        particleMesh = CreateSphereMesh(8, 6);

        Shader shader = Shader.Find("Custom/FluidParticle");
        particleMaterial = new Material(shader);
        drawBounds = new Bounds(transform.position, Vector3.one * 10000f);
    }

    void RenderParticles()
    {
        if (numParticles < 2 || particleMaterial == null)
            return;

        particleMaterial.SetBuffer("Positions", positionsBuf);
        particleMaterial.SetBuffer("Colors", colorsBuf); // .x = per-particle mix fraction t
        particleMaterial.SetBuffer("MixLut", mixLutBuf); // t -> spectral Kubelka-Munk colour
        particleMaterial.SetInt("MixLutSize", MixLutSize);
        particleMaterial.SetFloat("_Scale", particleScale);

        var rp = new RenderParams(particleMaterial) { worldBounds = drawBounds };
        Graphics.RenderMeshPrimitives(rp, particleMesh, 0, numParticles);
    }

    // Low-poly UV sphere (radius 0.5) — same as the CPU version.
    Mesh CreateSphereMesh(int sectors, int rings)
    {
        var mesh = new Mesh { name = "ParticleSphere" };

        int vertCount = (rings + 1) * (sectors + 1);
        var verts = new Vector3[vertCount];
        var normals = new Vector3[vertCount];

        int v = 0;
        for (int r = 0; r <= rings; r++)
        {
            float phi = Mathf.PI * r / rings;
            float y = Mathf.Cos(phi);
            float ringRadius = Mathf.Sin(phi);
            for (int s = 0; s <= sectors; s++)
            {
                float theta = 2f * Mathf.PI * s / sectors;
                Vector3 n = new Vector3(
                    ringRadius * Mathf.Cos(theta),
                    y,
                    ringRadius * Mathf.Sin(theta)
                );
                normals[v] = n;
                verts[v] = n * 0.5f;
                v++;
            }
        }

        var tris = new int[rings * sectors * 6];
        int t = 0;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < sectors; s++)
            {
                int a = r * (sectors + 1) + s;
                int b = a + sectors + 1;
                tris[t++] = a;
                tris[t++] = b;
                tris[t++] = a + 1;
                tris[t++] = a + 1;
                tris[t++] = b;
                tris[t++] = b + 1;
            }
        }

        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    // =================================================================================
    void ReleaseBuffers()
    {
        positionsBuf?.Release();
        predictedBuf?.Release();
        velocitiesBuf?.Release();
        densitiesBuf?.Release();
        wetnessBuf?.Release();
        colorsBuf?.Release();
        mixLutBuf?.Release();
        mixLutBuf = null;
        spatialKeysBuf?.Release();
        spatialIndicesBuf?.Release();
        cellStartBuf?.Release();
    }

    [ContextMenu("Respawn")]
    void Respawn()
    {
        if (!Application.isPlaying || compute == null)
            return;
        ReleaseBuffers();
        InitBuffers();
        BindBuffers();
        if (autoCalibrateRestDensity)
            CalibrateRestDensity();
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.4f, 0.4f, 0.4f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boundsSize);
    }
}
