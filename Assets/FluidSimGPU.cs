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

    [Header("Bounds (move/rotate this GameObject to slosh the fluid)")]
    public Vector3 boundsSize = new Vector3(10f, 10f, 10f);

    [Header("Simulation")]
    [Tooltip("Physics sub-steps per frame. 1 = fastest (halves dispatches); higher = more stable under hard shaking.")]
    [Range(1, 10)]
    public int iterationsPerFrame = 1;

    [Tooltip("Below this framerate the sim slows down (clamps its timestep) instead of taking huge unstable steps. Lower = stays real-time longer but risks jitter; higher = goes slow-motion sooner but stays stable.")]
    public float minStableFramerate = 30f;

    [Header("Rendering")]
    public Color paintColor = new Color(0.2f, 0.55f, 1f);
    public float particleScale = 0.2f;

    // --- GPU buffers ---
    ComputeBuffer positionsBuf,
        predictedBuf,
        velocitiesBuf; // float3
    ComputeBuffer densitiesBuf; // float2
    ComputeBuffer spatialKeysBuf,
        spatialIndicesBuf; // uint, padded to pow2
    ComputeBuffer cellStartBuf; // uint, size numParticles

    // --- kernel indices ---
    int kExternalForces,
        kUpdateHash,
        kSort,
        kOffsets,
        kDensities,
        kPressure,
        kViscosity,
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
        kPressure = compute.FindKernel("CalculatePressureForce");
        kViscosity = compute.FindKernel("CalculateViscosity");
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
        Dispatch(kPressure, numParticles);
        Dispatch(kViscosity, numParticles);
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
        compute.SetFloat("viscosityStrength", viscosityStrength);

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
        particleMaterial.SetFloat("_Scale", particleScale);
        particleMaterial.SetColor("_Color", paintColor);

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
