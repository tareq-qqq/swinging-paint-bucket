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
    [Tooltip(
        "Amount of paint = number of particles. (~20k for the box demo; a bucket holds far fewer.)"
    )]
    public int particleCount = 20000;
    public float particleSpacing = 0.25f;

    [Tooltip(
        "Box mode: where the paint block spawns. In bucket mode the paint spawns inside the bucket."
    )]
    public Vector3 spawnCentre = new Vector3(0f, 1.5f, 0f);
    public float spawnJitter = 0.02f;

    [Header("Fluid (paint properties)")]
    public float smoothingRadius = 0.5f;
    public float restDensity = 12f;
    public float pressureMultiplier = 200f;
    public float nearPressureMultiplier = 12f;
    public float viscosityStrength = 0.12f;
    public bool autoCalibrateRestDensity = true;

    // NOTE: the ambient environment INPUTS — gravity, air resistance, ambient temperature,
    // humidity and wind — now live on the shared EnvironmentConfig component (one source for the
    // paint, the bucket pendulum and the rope). The fields below are only how THIS paint RESPONDS
    // to them, plus sim/collision tuning.

    [Header("Collision")]
    [Range(0f, 1f)]
    [Tooltip("Bounce energy kept on a wall hit (0 = no bounce, 1 = perfectly elastic).")]
    public float collisionDamping = 0.45f;

    [Tooltip(
        "Velocity safety clamp (world units/sec). Stops a bad spawn or a fast bucket move from blowing the sim up. 0 = off. ~25 suits the bucket scale."
    )]
    public float maxSpeed = 25f;

    [Header("Paint — drying / curing (responds to EnvironmentConfig temperature & humidity)")]
    [Tooltip("Strength of temperature → viscosity. 0 = off.")]
    public float tempViscosityFactor = 0.03f;
    public bool enableDrying = true;
    public float dryingRate = 0.05f;

    [Range(0f, 1f)]
    public float setWetnessThreshold = 0.15f;

    [Range(0f, 1f)]
    [Tooltip(
        "Density fraction below which a particle counts as air-exposed (surface). Higher = thicker exposed skin; too low and nothing registers as surface."
    )]
    public float airExposureThreshold = 0.95f;

    [Header("Paint — air response (wind & air drag are set on EnvironmentConfig)")]
    [Tooltip(
        "Whether the air-exposed paint responds to the EnvironmentConfig wind and air resistance."
    )]
    public bool enableAirEffects = true;

    [Header("Bounds — transparent box (move/rotate this GameObject to slosh the fluid)")]
    public Vector3 boundsSize = new Vector3(10f, 10f, 10f);

    [Header("Container override (optional)")]
    [Tooltip(
        "OPTIONAL. Leave empty for the standalone box demo. Assign a BucketContainer to instead contain the paint in the swinging bucket (cylinder + floor spill-hole). This is the only switch between the two demos."
    )]
    public BucketContainer container;

    [Tooltip(
        "OPTIONAL. A transparent-box visual GameObject for the box demo; it is auto-hidden whenever a Container (bucket) is assigned, so the box doesn't show in bucket mode."
    )]
    public GameObject boundsVisual;

    [Header("Canvas (optional)")]
    [Tooltip(
        "OPTIONAL. Assign a PaintCanvas and the paint that leaves the bucket (or, in the box demo, any paint) lands on it, stains it, soaks in and dries onto it (Phase 5 — surfaces). Leave empty and nothing changes."
    )]
    public PaintCanvas canvas;

    [Header("Simulation (stability)")]
    [Tooltip(
        "Stability: the physics timestep is capped at this × Smoothing Radius. LOWER it if paint explodes (at the rim/hole, on a slow machine, or with small particles) — the sim runs more substeps at a smaller, stable step. 0.02 is a good start."
    )]
    public float stabilityCFL = 0.02f;

    [Range(1, 16)]
    [Tooltip(
        "Max physics substeps per frame (a performance cap). If the machine can't keep up at the stable timestep, the sim runs in SLOW-MOTION instead of exploding."
    )]
    public int maxSubsteps = 8;

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

    public enum RenderMode
    {
        Balls, // the original per-particle shaded spheres
        Surface, // screen-space fluid surface (needs a FluidSurfaceRenderer on the camera — Phase 6)
    }

    [Tooltip(
        "Balls = draw each particle as a sphere (default). Surface = let a FluidSurfaceRenderer on the camera render the paint as a continuous fluid surface (this component then skips the spheres)."
    )]
    public RenderMode renderMode = RenderMode.Balls;

    // --- GPU buffers ---
    ComputeBuffer positionsBuf,
        predictedBuf,
        velocitiesBuf; // float3
    ComputeBuffer densitiesBuf; // float2
    ComputeBuffer wetnessBuf; // float (1 = wet, 0 = cured)
    ComputeBuffer colorsBuf; // float3: .x = mix fraction t (0=paint A, 1=paint B)
    ComputeBuffer releasedBuf; // uint: 1 once a particle has drained out the bucket hole
    ComputeBuffer absorbedBuf; // uint: 1 once a particle has soaked into an absorbent canvas (removed)
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
        kUpdatePositions,
        kCanvasContact,
        kCanvasCommit;

    int numParticles;
    int paddedCount; // next power of two >= numParticles (for the bitonic sort)
    bool warnedNoEnvironment; // so the "no EnvironmentConfig" warning fires only once

    Vector3[] bucketLocalSpawn; // bucket-local paint positions (count = volume × fillFraction)
    bool ready; // false until the (deferred) spawn + calibration is done; the sim is frozen until then

    // --- rendering ---
    Mesh particleMesh;
    Material particleMaterial;
    Bounds drawBounds;

    const int GROUP = 64; // numthreads for the per-particle kernels
    const int SORT_GROUP = 128; // numthreads for the bitonic sort kernel

    // =================================================================================
    System.Collections.IEnumerator Start()
    {
        if (compute == null)
        {
            Debug.LogError("FluidSimGPU: assign FluidCompute.compute to the 'Compute' slot.");
            enabled = false;
            yield break;
        }

        // Confirm which GPU/backend Unity is actually using (Intel integrated vs NVIDIA dGPU).
        Debug.Log(
            $"[FluidSimGPU] GPU: {SystemInfo.graphicsDeviceName} | API: {SystemInfo.graphicsDeviceType} | compute: {SystemInfo.supportsComputeShaders}"
        );

        CacheKernels();
        InitBuffers();
        BindBuffers();
        BuildRenderResources();

        // BUCKET: wait until the pendulum + rope have actually moved the bucket to its starting
        // swing pose (a couple of FixedUpdates) BEFORE placing the paint and calibrating — otherwise
        // the paint is placed at the bucket's editor pose, the bucket then jumps, and the collision
        // sees all the paint outside the cylinder and blows it off the walls. The sim is frozen
        // (see Update's !ready guard) until this is done.
        if (container != null && container.Active)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            PlaceBucketPaint();
        }

        if (autoCalibrateRestDensity)
            CalibrateRestDensity();

        ready = true;
    }

    // Simulate in Update (not FixedUpdate) to avoid FixedUpdate's "catch-up" death spiral. The
    // number of substeps is chosen so each physics step is <= a STABLE size (see below).
    void Update()
    {
        // Sim is frozen until the (deferred) spawn + calibration finishes — see Start. Still render
        // so the paint is visible while it gets placed.
        if (!ready)
        {
            RenderParticles();
            return;
        }

        if (numParticles >= 2)
        {
            // Cap the physics timestep for STABILITY, independent of frame rate. A big dt (low FPS,
            // or small particles) makes the pressure forces overshoot in one step, so the paint
            // explodes at thin regions (rim / hole / free surface). Split the frame into enough
            // substeps that each is <= dtCap; if that needs more than maxSubsteps the sim runs in
            // slow-motion instead of exploding. (A faster machine had a smaller dt = didn't explode.)
            float dtCap = Mathf.Max(1e-4f, stabilityCFL * smoothingRadius);
            float frameDt = Mathf.Min(Time.deltaTime, maxSubsteps * dtCap);
            int n = Mathf.Clamp(Mathf.CeilToInt(frameDt / dtCap), 1, maxSubsteps);
            float dt = frameDt / n; // guaranteed <= dtCap
            for (int i = 0; i < n; i++)
                SimulationStep(dt);
        }

        // Live inspector tuning: rebuild the t -> colour LUT only when a paint colour changes.
        if (mixLutBuf != null && (paintColorA != lutColorA || paintColorB != lutColorB))
            BuildMixLut();

        // The transparent box belongs to the box demo only — hide it whenever a bucket is in use.
        if (boundsVisual != null)
        {
            bool showBox = !(container != null && container.Active);
            if (boundsVisual.activeSelf != showBox)
                boundsVisual.SetActive(showBox);
        }

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
        kCanvasContact = compute.FindKernel("CanvasContact");
        kCanvasCommit = compute.FindKernel("CanvasCommit");
    }

    void InitBuffers()
    {
        // --- Decide the spawn + the particle count ---
        // BUCKET: build a CYLINDER of paint in the bucket's LOCAL frame; the count comes from the
        //   bucket volume × fillFraction (so "amount of paint" is just a slider). The local points
        //   are transformed to world on the first frame (deferred), once the pendulum has placed
        //   the bucket, so the paint always lands INSIDE it.
        // BOX: the unchanged centred cube grid in world; count = particleCount.
        bool useBucket = container != null && container.Active;
        Vector3[] colors;

        if (useBucket)
        {
            bucketLocalSpawn = container.BuildLocalSpawn(particleSpacing, spawnJitter);
            numParticles = Mathf.Max(2, bucketLocalSpawn.Length);
            Debug.Log(
                $"[FluidSimGPU] Bucket fill: {bucketLocalSpawn.Length} particles "
                    + $"(capacity {container.Capacity(particleSpacing)} at spacing {particleSpacing})."
            );
        }
        else
        {
            numParticles = Mathf.Max(0, particleCount);
        }
        paddedCount = Mathf.NextPowerOfTwo(Mathf.Max(2, numParticles));

        positionsBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        predictedBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        velocitiesBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        densitiesBuf = new ComputeBuffer(numParticles, sizeof(float) * 2);
        wetnessBuf = new ComputeBuffer(numParticles, sizeof(float));
        colorsBuf = new ComputeBuffer(numParticles, sizeof(float) * 3);
        releasedBuf = new ComputeBuffer(numParticles, sizeof(uint)); // all 0 (captured) by default
        absorbedBuf = new ComputeBuffer(numParticles, sizeof(uint)); // all 0 (active) by default
        spatialKeysBuf = new ComputeBuffer(paddedCount, sizeof(uint));
        spatialIndicesBuf = new ComputeBuffer(paddedCount, sizeof(uint));
        cellStartBuf = new ComputeBuffer(numParticles, sizeof(uint));

        var positions = new Vector3[numParticles];
        var velocities = new Vector3[numParticles]; // start at rest
        colors = new Vector3[numParticles];

        if (useBucket)
        {
            // Provisional world placement (re-done correctly by PlaceBucketPaint once the bucket settles).
            Matrix4x4 toWorld = container.Pose.localToWorldMatrix;
            for (int i = 0; i < numParticles; i++)
            {
                positions[i] = toWorld.MultiplyPoint3x4(bucketLocalSpawn[i]);
                // Two paints split across the bucket's LOCAL x (pose-independent): left=A, right=B.
                colors[i] = new Vector3(bucketLocalSpawn[i].x < 0f ? 0f : 1f, 0f, 0f);
            }
        }
        else
        {
            SpawnInGrid(positions, velocities);
            for (int i = 0; i < numParticles; i++)
                colors[i] = new Vector3(positions[i].x < spawnCentre.x ? 0f : 1f, 0f, 0f);
        }

        positionsBuf.SetData(positions);
        predictedBuf.SetData(positions); // predicted = positions until the first step
        velocitiesBuf.SetData(velocities);

        // Colors[i].x = MIX FRACTION t (0 = paint A, 1 = paint B); the shader maps t -> displayed
        // colour through the spectral Kubelka-Munk LUT (see BuildMixLut). Wetness starts fully wet.
        var wet = new float[numParticles];
        for (int i = 0; i < numParticles; i++)
            wet[i] = 1f;
        wetnessBuf.SetData(wet);
        colorsBuf.SetData(colors);
        releasedBuf.SetData(new uint[numParticles]); // all 0 = all paint starts captured by the bucket
        absorbedBuf.SetData(new uint[numParticles]); // all 0 = no paint has soaked into the canvas yet

        BuildMixLut();
    }

    // Re-place the bucket paint at the bucket's REAL pose (called on the first frame, after the
    // pendulum has positioned the bucket) so it spawns inside instead of where the bucket sat at Start.
    void PlaceBucketPaint()
    {
        if (bucketLocalSpawn == null || container == null || !container.Active)
            return;
        Matrix4x4 toWorld = container.Pose.localToWorldMatrix;
        var positions = new Vector3[numParticles];
        for (int i = 0; i < numParticles; i++)
            positions[i] = toWorld.MultiplyPoint3x4(bucketLocalSpawn[i]);
        positionsBuf.SetData(positions);
        predictedBuf.SetData(positions);
        velocitiesBuf.SetData(new Vector3[numParticles]); // reset to rest
        releasedBuf.SetData(new uint[numParticles]); // freshly placed paint is captured again
        absorbedBuf.SetData(new uint[numParticles]); // freshly placed paint is active again
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
        compute.SetBuffer(kDensities, "Released", releasedBuf);

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
        compute.SetBuffer(kPressure, "Released", releasedBuf);

        // CalculateViscosity
        compute.SetBuffer(kViscosity, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kViscosity, "Velocities", velocitiesBuf);
        compute.SetBuffer(kViscosity, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kViscosity, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kViscosity, "CellStart", cellStartBuf);
        compute.SetBuffer(kViscosity, "Released", releasedBuf);

        // MixColors
        compute.SetBuffer(kMixColors, "PredictedPositions", predictedBuf);
        compute.SetBuffer(kMixColors, "Velocities", velocitiesBuf);
        compute.SetBuffer(kMixColors, "Colors", colorsBuf);
        compute.SetBuffer(kMixColors, "Wetness", wetnessBuf);
        compute.SetBuffer(kMixColors, "SpatialKeys", spatialKeysBuf);
        compute.SetBuffer(kMixColors, "SpatialIndices", spatialIndicesBuf);
        compute.SetBuffer(kMixColors, "CellStart", cellStartBuf);
        compute.SetBuffer(kMixColors, "Released", releasedBuf);

        // UpdatePositions
        compute.SetBuffer(kUpdatePositions, "Positions", positionsBuf);
        compute.SetBuffer(kUpdatePositions, "Velocities", velocitiesBuf);
        compute.SetBuffer(kUpdatePositions, "Released", releasedBuf);

        // Absorbed — soaked-into-canvas paint is skipped by every per-particle kernel and excluded
        // from the neighbour search (via UpdateSpatialHash), so it's removed from the sim entirely.
        compute.SetBuffer(kExternalForces, "Absorbed", absorbedBuf);
        compute.SetBuffer(kUpdateHash, "Absorbed", absorbedBuf);
        compute.SetBuffer(kDensities, "Absorbed", absorbedBuf);
        compute.SetBuffer(kPressure, "Absorbed", absorbedBuf);
        compute.SetBuffer(kViscosity, "Absorbed", absorbedBuf);
        compute.SetBuffer(kMixColors, "Absorbed", absorbedBuf);
        compute.SetBuffer(kUpdatePositions, "Absorbed", absorbedBuf);

        // CanvasContact + CanvasCommit (optional) — only bound when a PaintCanvas is assigned, so the
        // box/bucket demos never touch them. Contact needs the paint state + deposit map + colour LUT;
        // commit only touches the deposit map.
        if (canvas != null && canvas.Active)
        {
            canvas.EnsureResources(); // make sure the deposit buffer exists before we bind it
            compute.SetBuffer(kCanvasContact, "Positions", positionsBuf);
            compute.SetBuffer(kCanvasContact, "Velocities", velocitiesBuf);
            compute.SetBuffer(kCanvasContact, "Colors", colorsBuf);
            compute.SetBuffer(kCanvasContact, "Wetness", wetnessBuf);
            compute.SetBuffer(kCanvasContact, "Released", releasedBuf);
            compute.SetBuffer(kCanvasContact, "Absorbed", absorbedBuf);
            compute.SetBuffer(kCanvasContact, "MixLut", mixLutBuf);
            compute.SetBuffer(kCanvasContact, "CanvasDeposit", canvas.DepositBuffer);

            compute.SetBuffer(kCanvasCommit, "CanvasDeposit", canvas.DepositBuffer);
        }
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

        // Canvas contact runs last (needs final positions/velocities) and only when a canvas exists;
        // the commit pass then dries the wet layer and composites it (over one thread per texel).
        if (canvas != null && canvas.Active)
        {
            Dispatch(kCanvasContact, numParticles);
            Dispatch(kCanvasCommit, canvas.ResX * canvas.ResY);
        }
    }

    void SetUniforms(float dt)
    {
        float r = smoothingRadius;
        float r5 = r * r * r * r * r;
        float r6 = r5 * r;
        float r9 = r6 * r * r * r;

        // ALL ambient environment inputs (gravity / temperature / humidity / wind / air resistance)
        // come from the one shared EnvironmentConfig, so they're entered ONCE and the paint, the
        // bucket pendulum and the rope all agree. If none is in the scene we use safe defaults and
        // warn once (add an EnvironmentConfig to control them).
        var env = EnvironmentConfig.Instance;
        if (env == null && !warnedNoEnvironment)
        {
            Debug.LogWarning(
                "[FluidSimGPU] No EnvironmentConfig in the scene — using default gravity/temperature/"
                    + "humidity/wind/air resistance. Add one EnvironmentConfig to set them (shared with the bucket & rope)."
            );
            warnedNoEnvironment = true;
        }
        Vector3 gravityV = env != null ? env.GravityVector : new Vector3(0f, -9.81f, 0f);
        float temperatureV = env != null ? env.ambientTemperature : 20f;
        float humidityV = env != null ? env.humidity : 0.5f;
        Vector3 windV = env != null ? env.wind : Vector3.zero;
        float airDragV = env != null ? env.airResistance : 0.1f;

        compute.SetInt("numParticles", numParticles);
        compute.SetInt("numEntries", paddedCount);
        compute.SetFloat("smoothingRadius", r);
        compute.SetFloat("radiusSq", r * r);
        compute.SetFloat("deltaTime", dt);

        compute.SetFloat("restDensity", restDensity);
        compute.SetFloat("pressureMultiplier", pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);

        // Temperature thins/thickens ALL the paint (global) -> fold into effective viscosity.
        float tempViscMul = Mathf.Max(0.05f, 1f - tempViscosityFactor * (temperatureV - 20f));
        compute.SetFloat("effectiveViscosity", viscosityStrength * tempViscMul);

        // Environment uniforms.
        compute.SetInt("enableDrying", enableDrying ? 1 : 0);
        compute.SetInt("enableAirEffects", enableAirEffects ? 1 : 0);
        compute.SetFloat("ambientTemperature", temperatureV);
        compute.SetFloat("humidity", humidityV);
        compute.SetFloat("dryingRate", dryingRate);
        compute.SetFloat("setWetnessThreshold", setWetnessThreshold);
        compute.SetFloat("airExposureThreshold", airExposureThreshold);
        compute.SetVector("windForce", windV);
        compute.SetFloat("airDrag", airDragV);

        compute.SetFloat("colorMixRate", colorMixRate);
        compute.SetFloat("shakeMixBoost", shakeMixBoost);

        compute.SetFloat("densityScale", 15f / (2f * Mathf.PI * r5));
        compute.SetFloat("densityDerivScale", 15f / (Mathf.PI * r5));
        compute.SetFloat("nearDensityScale", 15f / (Mathf.PI * r6));
        compute.SetFloat("nearDensityDerivScale", 45f / (Mathf.PI * r6));
        compute.SetFloat("viscScale", 315f / (64f * Mathf.PI * r9));

        compute.SetVector("gravity", gravityV);
        compute.SetFloat("collisionDamping", collisionDamping);
        compute.SetFloat("maxSpeed", maxSpeed);
        compute.SetVector("boundsSize", boundsSize);

        // Container pose + shape. With a bucket assigned the paint is contained by the bucket
        // CYLINDER (radius, height, a floor spill-hole, open top) in the bucket's local frame,
        // so it sloshes/tilts and drains exactly as the bucket swings. With no bucket we fall
        // back to the rotated BOX defined by this GameObject + boundsSize (the old behaviour).
        bool useCylinder = container != null && container.Active;
        Transform c = useCylinder ? container.Pose : transform;
        Matrix4x4 localToWorld = Matrix4x4.TRS(c.position, c.rotation, Vector3.one);
        compute.SetMatrix("localToWorld", localToWorld);
        compute.SetMatrix("worldToLocal", localToWorld.inverse);

        compute.SetInt("containerIsCylinder", useCylinder ? 1 : 0);
        if (useCylinder)
        {
            compute.SetFloat("cylinderRadius", container.Radius);
            compute.SetFloat("cylinderHeight", container.Height);
            compute.SetFloat("cylinderFloorY", container.FloorY);
            compute.SetFloat("holeRadius", container.HoleRadius);
            compute.SetInt("openTop", container.OpenTop ? 1 : 0);
        }

        // Canvas (Phase 5). Pose + finite quad + per-material coefficients + the wetting/absorption
        // physics constants, consumed by the CanvasContact kernel (only dispatched when a canvas is
        // assigned). The quad lies in the canvas's local XZ plane (normal = local +Y).
        if (canvas != null && canvas.Active)
        {
            Transform cv = canvas.Pose;
            Matrix4x4 cToWorld = Matrix4x4.TRS(cv.position, cv.rotation, Vector3.one);
            compute.SetMatrix("canvasLocalToWorld", cToWorld);
            compute.SetMatrix("canvasWorldToLocal", cToWorld.inverse);
            compute.SetFloat("canvasWidth", canvas.Width);
            compute.SetFloat("canvasHeight", canvas.Height);
            compute.SetFloat("canvasContactDist", canvas.ContactThickness);
            // The sphere mesh has radius 0.5, drawn at particleScale — so the paint's world radius is
            // 0.5*particleScale. Rest the particle centre that far above the canvas so it sits on top.
            compute.SetFloat("canvasSurfaceOffset", 0.5f * particleScale);
            compute.SetFloat("canvasBrushRadius", canvas.BrushRadius);
            compute.SetInt("canvasResX", canvas.ResX);
            compute.SetInt("canvasResY", canvas.ResY);
            compute.SetInt("mixLutSize", MixLutSize);

            compute.SetFloat("canvasAbsorbency", canvas.absorbency);
            compute.SetFloat("canvasWettability", canvas.wettability);
            compute.SetFloat("canvasAdhesion", canvas.adhesion);
            compute.SetFloat("canvasFriction", canvas.friction);
            compute.SetFloat("canvasCapillary", canvas.capillaryStrength);
            compute.SetFloat("canvasViscousDrag", canvas.viscousDrag);
            compute.SetFloat("canvasSaturation", canvas.saturationChoke);
            compute.SetFloat("canvasFilmRate", canvas.filmRate);
            compute.SetFloat("canvasFrictionRate", canvas.frictionRate);
            // Layering (CanvasCommit): drying/commit rate + the opacity-buildup the shader uses, so
            // the commit computes wet opacity the same way the display does. Temperature/humidity are
            // already set above (shared with the paint drying).
            compute.SetFloat("canvasDryBase", canvas.layerDryRate);
            compute.SetFloat("canvasOpacityBuildup", Mathf.Max(0.01f, canvas.opacityBuildup));
            compute.SetFloat("canvasSetStamp", Mathf.Max(0f, canvas.driedMarkStrength));
            compute.SetFloat("canvasBounce", Mathf.Clamp01(canvas.bounce));
            compute.SetFloat("canvasContactDry", Mathf.Max(0f, canvas.contactDryRate));
        }
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
        // In Surface mode the FluidSurfaceRenderer (on the camera) draws the fluid instead — skip the
        // spheres so they don't render on top of / behind the surface.
        if (renderMode == RenderMode.Surface)
            return;
        if (numParticles < 2 || particleMaterial == null)
            return;

        particleMaterial.SetBuffer("Positions", positionsBuf);
        particleMaterial.SetBuffer("Colors", colorsBuf); // .x = per-particle mix fraction t
        particleMaterial.SetBuffer("MixLut", mixLutBuf); // t -> spectral Kubelka-Munk colour
        particleMaterial.SetBuffer("Absorbed", absorbedBuf); // soaked-in paint is collapsed/hidden
        particleMaterial.SetInt("MixLutSize", MixLutSize);
        particleMaterial.SetFloat("_Scale", particleScale);

        var rp = new RenderParams(particleMaterial) { worldBounds = drawBounds };
        Graphics.RenderMeshPrimitives(rp, particleMesh, 0, numParticles);
    }

    // --- Public read-only accessors for the screen-space FluidSurfaceRenderer (Phase 6). It reuses
    // these existing buffers directly — no extra sim state. Valid once the buffers are allocated. ---
    public ComputeBuffer PositionsBuffer => positionsBuf;
    public ComputeBuffer ColorsBuffer => colorsBuf; // .x = mix fraction t
    public ComputeBuffer MixLutBuffer => mixLutBuf; // t -> spectral paint colour
    public ComputeBuffer AbsorbedBuffer => absorbedBuf; // 1 = soaked in (hide)
    public ComputeBuffer WetnessBuffer => wetnessBuf; // 1 = wet (glossy) -> 0 = dry (matte)
    public int MixLutCount => MixLutSize;
    public int ActiveParticleCount => positionsBuf != null ? numParticles : 0;
    public float ParticleWorldRadius => particleScale * 0.5f; // sphere mesh radius 0.5 × scale
    public bool RenderAsSurface => renderMode == RenderMode.Surface;

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
        releasedBuf?.Release();
        absorbedBuf?.Release();
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
        // In bucket mode the box is irrelevant — don't draw its wireframe (the bucket has its own).
        if (container != null && container.Active)
            return;
        Gizmos.color = new Color(0.4f, 0.4f, 0.4f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boundsSize);
    }
}
