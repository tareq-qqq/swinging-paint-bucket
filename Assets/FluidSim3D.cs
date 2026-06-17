using System;
using System.Threading.Tasks; // Parallel.For for the heavy per-particle passes
using UnityEngine;

// =====================================================================================
//  FluidSim3D — a 3D Smoothed Particle Hydrodynamics (SPH) fluid, written from scratch.
// =====================================================================================
//  This is the 3D port of FluidSim.cs. Everything is hand-written: gravity, density,
//  pressure, viscosity and wall collisions. NO Unity physics (no Rigidbody / Collider)
//  and NO custom shaders. Neighbour lookups use a 3D spatial hash grid.
//
//  Differences from the 2D version are documented in From2DTo3D.md; the SPH theory
//  itself is in FluidSim.md. In short: Vector2 -> Vector3, the neighbour search scans
//  27 cells (3x3x3) instead of 9, the kernel normalisation constants use the 3D
//  (sphere) integral, collisions clamp X/Y/Z, and the container box follows this
//  GameObject's transform so you can MOVE and ROTATE it with the scene gizmo.
//
//  Attach to an empty GameObject and press Play. View/pan in the Scene view (instanced
//  particles render there too). Grab the move/rotate handles to slosh and pour the fluid.
// =====================================================================================
public class FluidSim3D : MonoBehaviour
{
    // ---------------------------------------------------------------------------------
    //  Inspector parameters — tune these to simulate different paints.
    // ---------------------------------------------------------------------------------
    [Header("Spawn")]
    [Tooltip("How many fluid particles to simulate.")]
    public int particleCount = 2000;

    [Tooltip("Distance between particles in the initial grid block.")]
    public float particleSpacing = 0.25f;

    [Tooltip("Centre of the block the particles spawn in (local-ish, in world space).")]
    public Vector3 spawnCentre = new Vector3(0f, 1.5f, 0f);

    [Tooltip("Tiny random offset added to each particle so the grid is not perfectly regular.")]
    public float spawnJitter = 0.02f;

    [Header("Fluid (paint properties)")]
    [Tooltip("Radius of influence of each particle. Larger = smoother but heavier.")]
    public float smoothingRadius = 0.5f;

    [Tooltip(
        "Rest/target density. Pressure pushes the fluid toward this value. Auto-calibrated on start if enabled below."
    )]
    public float restDensity = 12f;

    [Tooltip(
        "How hard the fluid resists being compressed. Too low = clumping, too high = jitter/explosion."
    )]
    public float pressureMultiplier = 200f;

    [Tooltip("Short-range repulsion that stops particles stacking on the exact same spot.")]
    public float nearPressureMultiplier = 12f;

    [Tooltip("Thickness of the paint. 0 = water-like, high = honey/thick paint.")]
    public float viscosityStrength = 0.12f;

    [Tooltip(
        "On start, set restDensity to the fluid's natural density so the spawn is already in equilibrium (much more stable)."
    )]
    public bool autoCalibrateRestDensity = true;

    [Header("Environment")]
    [Tooltip("Gravity vector applied every step (world space; we do NOT use Unity's gravity).")]
    public Vector3 gravity = new Vector3(0f, -10f, 0f);

    [Tooltip(
        "Fraction of velocity kept when bouncing off a wall (1 = perfect bounce, 0 = sticks)."
    )]
    [Range(0f, 1f)]
    public float collisionDamping = 0.45f;

    [Header("Bounds (the container — move/rotate this GameObject to slosh the fluid)")]
    [Tooltip("Width/height/depth of the box the fluid is trapped in, centred on this GameObject.")]
    public Vector3 boundsSize = new Vector3(8f, 8f, 8f);

    [Header("Simulation")]
    [Tooltip("Physics sub-steps per fixed update. More = more stable but slower.")]
    [Range(1, 10)]
    public int iterationsPerFrame = 2;

    [Header("Rendering (instanced, no custom shader)")]
    public Color paintColor = new Color(0.2f, 0.55f, 1f);

    [Tooltip("Visual diameter of a particle in world units.")]
    public float particleScale = 0.2f;

    // ---------------------------------------------------------------------------------
    //  Particle state — plain parallel arrays, no per-frame allocations.
    // ---------------------------------------------------------------------------------
    Vector3[] positions;
    Vector3[] predictedPositions;
    Vector3[] velocities;
    float[] densities; // standard density at each particle
    float[] nearDensities; // "near" density (sharper kernel) for anti-clumping
    Vector3[] velocityBuffer; // snapshot of velocities so viscosity can run in parallel safely

    int numParticles; // actual allocated count

    // Kernel normalisation constants, precomputed once per step (they only depend on
    // smoothingRadius). Recomputing Mathf.Pow inside every kernel call was the main cost.
    float radiusSq;
    float densityScale,
        densityDerivScale,
        nearDensityScale,
        nearDensityDerivScale,
        viscScale;

    // ---------------------------------------------------------------------------------
    //  Spatial hash — sorted-array scheme (no Dictionary, no per-frame GC).
    // ---------------------------------------------------------------------------------
    struct SpatialEntry : IComparable<SpatialEntry>
    {
        public int particleIndex; // which particle
        public uint cellKey; // hashed-and-wrapped cell id

        public int CompareTo(SpatialEntry other) => cellKey.CompareTo(other.cellKey);
    }

    SpatialEntry[] spatialEntries; // one per particle, sorted by cellKey each step
    int[] cellStart; // cellStart[key] = first index in spatialEntries with that key

    // The 27 neighbouring cells in 3D (3x3x3). Built once in Awake instead of hand-listing.
    Vector3Int[] cellOffsets;

    // Three large primes used to mix the cell coordinates into a hash.
    const uint HashK1 = 15823;
    const uint HashK2 = 9737333;
    const uint HashK3 = 440817757;

    // ---------------------------------------------------------------------------------
    //  Rendering resources.
    // ---------------------------------------------------------------------------------
    Mesh particleMesh;
    Material particleMaterial;
    Matrix4x4[] renderMatrices;

    // =================================================================================
    //  Unity lifecycle
    // =================================================================================
    void Start()
    {
        BuildCellOffsets();
        InitializeParticles();
        BuildRenderResources();
    }

    void FixedUpdate()
    {
        if (numParticles == 0)
            return;

        // Sub-step for stability: a few small steps beat one big step.
        float dt = Time.fixedDeltaTime / iterationsPerFrame;
        for (int i = 0; i < iterationsPerFrame; i++)
            SimulationStep(dt);
    }

    void Update()
    {
        RenderParticles();
    }

    void OnDestroy()
    {
        if (particleMaterial != null)
            Destroy(particleMaterial);
        if (particleMesh != null)
            Destroy(particleMesh);
    }

    // =================================================================================
    //  Setup
    // =================================================================================

    // The only "dimensional" lookup table: every (dx,dy,dz) in [-1,1] -> 27 offsets.
    void BuildCellOffsets()
    {
        cellOffsets = new Vector3Int[27];
        int n = 0;
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            cellOffsets[n++] = new Vector3Int(x, y, z);
    }

    void InitializeParticles()
    {
        numParticles = Mathf.Max(0, particleCount);

        positions = new Vector3[numParticles];
        predictedPositions = new Vector3[numParticles];
        velocities = new Vector3[numParticles];
        velocityBuffer = new Vector3[numParticles];
        densities = new float[numParticles];
        nearDensities = new float[numParticles];

        spatialEntries = new SpatialEntry[numParticles];
        cellStart = new int[numParticles];

        PrecomputeKernelScales();
        SpawnInGrid();

        // Make the spawn configuration the equilibrium so the fluid doesn't explode or
        // collapse on frame one regardless of the chosen spacing/radius.
        if (autoCalibrateRestDensity && numParticles > 0)
        {
            Array.Copy(positions, predictedPositions, numParticles);
            UpdateSpatialHash();
            ComputeDensities();
            float sum = 0f;
            for (int i = 0; i < numParticles; i++)
                sum += densities[i];
            restDensity = sum / numParticles;
        }
    }

    // Lay the particles out in a centred cube-ish grid block.
    void SpawnInGrid()
    {
        int perAxis = Mathf.Max(1, Mathf.CeilToInt(Mathf.Pow(numParticles, 1f / 3f)));
        float blockExtent = (perAxis - 1) * particleSpacing * 0.5f;

        for (int i = 0; i < numParticles; i++)
        {
            int x = i % perAxis;
            int y = (i / perAxis) % perAxis;
            int z = i / (perAxis * perAxis);
            Vector3 cell = new Vector3(x, y, z) * particleSpacing;
            Vector3 jitter = UnityEngine.Random.insideUnitSphere * spawnJitter;
            positions[i] = spawnCentre - Vector3.one * blockExtent + cell + jitter;
            velocities[i] = Vector3.zero;
        }
    }

    [ContextMenu("Respawn")]
    void Respawn()
    {
        if (cellOffsets == null)
            BuildCellOffsets();
        InitializeParticles();
        if (renderMatrices == null || renderMatrices.Length != numParticles)
            renderMatrices = new Matrix4x4[numParticles];
    }

    // =================================================================================
    //  One full simulation step (Müller 2003 / Sebastian Lague formulation)
    // =================================================================================
    void SimulationStep(float dt)
    {
        PrecomputeKernelScales(); // 0. refresh kernel constants (cheap; handles live tuning)
        ApplyGravityAndPredict(dt); // 1. external forces + look-ahead positions
        UpdateSpatialHash(); // 2. rebuild neighbour grid on predicted positions
        ComputeDensities(); // 3. density + near-density
        ApplyPressureForces(dt); // 4. pressure pushes from dense to sparse regions
        ApplyViscosity(dt); // 5. velocity smoothing (paint thickness)
        IntegrateAndResolveCollisions(dt); // 6. move + bounce off walls
    }

    // 1. Apply gravity, then predict where each particle will be. Densities are sampled
    //    at the predicted positions for extra stability.
    void ApplyGravityAndPredict(float dt)
    {
        for (int i = 0; i < numParticles; i++)
        {
            velocities[i] += gravity * dt;
            predictedPositions[i] = positions[i] + velocities[i] * dt;
        }
    }

    // 3. Accumulate density and near-density for every particle from its neighbours.
    //    Each particle writes only its own slot, so the loop runs in parallel across cores.
    void ComputeDensities()
    {
        Parallel.For(0, numParticles, ComputeDensity);
    }

    void ComputeDensity(int i)
    {
        Vector3 pos = predictedPositions[i];
        float density = 0f;
        float nearDensity = 0f;

        Vector3Int centre = CellCoord(pos);
        for (int c = 0; c < cellOffsets.Length; c++)
        {
            uint key = KeyFromHash(HashCell(centre + cellOffsets[c]));
            for (int s = cellStart[key]; s < numParticles && spatialEntries[s].cellKey == key; s++)
            {
                int j = spatialEntries[s].particleIndex;
                float sqr = (predictedPositions[j] - pos).sqrMagnitude;
                if (sqr >= radiusSq)
                    continue; // cull before paying for a sqrt
                float dst = Mathf.Sqrt(sqr);
                density += DensityKernel(dst);
                nearDensity += NearDensityKernel(dst);
            }
        }

        densities[i] = density;
        nearDensities[i] = nearDensity;
    }

    // 4. Pressure force: high-density particles push their neighbours away. The "near"
    //    term is a short-range repulsion that prevents particles collapsing onto a point.
    void ApplyPressureForces(float dt)
    {
        Parallel.For(0, numParticles, i => ComputePressureForce(i, dt));
    }

    void ComputePressureForce(int i, float dt)
    {
        float densityI = densities[i];
        if (densityI <= 0f)
            return;

        float pressureI = PressureFromDensity(densityI);
        float nearPressureI = NearPressureFromDensity(nearDensities[i]);
        Vector3 pos = predictedPositions[i];
        Vector3 force = Vector3.zero;

        Vector3Int centre = CellCoord(pos);
        for (int c = 0; c < cellOffsets.Length; c++)
        {
            uint key = KeyFromHash(HashCell(centre + cellOffsets[c]));
            for (int s = cellStart[key]; s < numParticles && spatialEntries[s].cellKey == key; s++)
            {
                int j = spatialEntries[s].particleIndex;
                if (j == i)
                    continue;

                Vector3 offset = predictedPositions[j] - pos;
                float sqr = offset.sqrMagnitude;
                if (sqr >= radiusSq)
                    continue; // cull before the sqrt
                float dst = Mathf.Sqrt(sqr);

                // Direction away from neighbour; pick an arbitrary dir if exactly overlapping.
                Vector3 dir = dst > 0f ? offset / dst : Vector3.up;

                float densityJ = densities[j];
                float nearDensityJ = nearDensities[j];
                if (densityJ <= 0f)
                    continue;

                // Symmetric (Newton's 3rd law) shared pressure between i and j.
                float sharedPressure = (pressureI + PressureFromDensity(densityJ)) * 0.5f;
                float sharedNear = (nearPressureI + NearPressureFromDensity(nearDensityJ)) * 0.5f;

                force += dir * (DensityKernelDerivative(dst) * sharedPressure / densityJ);
                if (nearDensityJ > 0f)
                    force += dir * (NearDensityKernelDerivative(dst) * sharedNear / nearDensityJ);
            }
        }

        // a = F / density  ->  v += a * dt
        velocities[i] += force / densityI * dt;
    }

    // 5. Viscosity: nudge each particle's velocity toward the average of its neighbours.
    //    This is what makes thick paint move as a cohesive blob instead of splashing.
    void ApplyViscosity(float dt)
    {
        if (viscosityStrength <= 0f)
            return;

        // Snapshot velocities so parallel workers read a stable copy of neighbours' velocities
        // (otherwise reading velocities[j] while another thread writes it would be a data race).
        Array.Copy(velocities, velocityBuffer, numParticles);
        Parallel.For(0, numParticles, i => ComputeViscosityForce(i, dt));
    }

    void ComputeViscosityForce(int i, float dt)
    {
        Vector3 pos = predictedPositions[i];
        Vector3 velI = velocityBuffer[i];
        Vector3 viscForce = Vector3.zero;

        Vector3Int centre = CellCoord(pos);
        for (int c = 0; c < cellOffsets.Length; c++)
        {
            uint key = KeyFromHash(HashCell(centre + cellOffsets[c]));
            for (int s = cellStart[key]; s < numParticles && spatialEntries[s].cellKey == key; s++)
            {
                int j = spatialEntries[s].particleIndex;
                if (j == i)
                    continue;

                float sqr = (predictedPositions[j] - pos).sqrMagnitude;
                if (sqr >= radiusSq)
                    continue; // cull before the sqrt
                float dst = Mathf.Sqrt(sqr);

                viscForce += (velocityBuffer[j] - velI) * ViscosityKernel(dst);
            }
        }

        velocities[i] += viscForce * (viscosityStrength * dt);
    }

    // 6. Move particles and keep them inside the box. The clamp is done in the box's LOCAL
    //    frame, so the container can be both moved AND rotated via the transform gizmo.
    //    Gravity stays world-space, so tilting the box pours the fluid into its low corner.
    void IntegrateAndResolveCollisions(float dt)
    {
        Vector3 halfSize = boundsSize * 0.5f;
        Vector3 origin = transform.position;
        Quaternion rot = transform.rotation;
        Quaternion invRot = Quaternion.Inverse(rot);

        for (int i = 0; i < numParticles; i++)
        {
            positions[i] += velocities[i] * dt;

            // World -> box-local (rotation + translation only; scale ignored on purpose).
            Vector3 local = invRot * (positions[i] - origin);
            Vector3 localVel = invRot * velocities[i];

            if (Mathf.Abs(local.x) > halfSize.x)
            {
                local.x = halfSize.x * Mathf.Sign(local.x);
                localVel.x *= -collisionDamping;
            }
            if (Mathf.Abs(local.y) > halfSize.y)
            {
                local.y = halfSize.y * Mathf.Sign(local.y);
                localVel.y *= -collisionDamping;
            }
            if (Mathf.Abs(local.z) > halfSize.z)
            {
                local.z = halfSize.z * Mathf.Sign(local.z);
                localVel.z *= -collisionDamping;
            }

            // box-local -> world.
            positions[i] = origin + rot * local;
            velocities[i] = rot * localVel;
        }
    }

    // =================================================================================
    //  Pressure equations of state
    // =================================================================================
    float PressureFromDensity(float density) => (density - restDensity) * pressureMultiplier;

    float NearPressureFromDensity(float nearDensity) => nearDensity * nearPressureMultiplier;

    // =================================================================================
    //  Smoothing kernels — 3D normalised (sphere integral). The kernel SHAPES are the same
    //  as the 2D version; only the scale constants differ. See From2DTo3D.md.
    // =================================================================================

    // Precompute the per-radius normalisation constants once per step instead of calling
    // Mathf.Pow inside every kernel evaluation (millions of times per second).
    void PrecomputeKernelScales()
    {
        float r = smoothingRadius;
        radiusSq = r * r;
        float r5 = r * r * r * r * r;
        float r6 = r5 * r;
        float r9 = r6 * r * r * r;
        densityScale = 15f / (2f * Mathf.PI * r5);
        densityDerivScale = 15f / (Mathf.PI * r5);
        nearDensityScale = 15f / (Mathf.PI * r6);
        nearDensityDerivScale = 45f / (Mathf.PI * r6);
        viscScale = 315f / (64f * Mathf.PI * r9);
    }

    // Density: (radius - dst)^2, used for the main density sum.
    float DensityKernel(float dst)
    {
        float v = smoothingRadius - dst;
        return v * v * densityScale;
    }

    // Derivative of the density kernel, used for the pressure gradient.
    float DensityKernelDerivative(float dst)
    {
        float v = smoothingRadius - dst;
        return -v * densityDerivScale;
    }

    // Near-density: (radius - dst)^3, a sharper kernel for short-range repulsion.
    float NearDensityKernel(float dst)
    {
        float v = smoothingRadius - dst;
        return v * v * v * nearDensityScale;
    }

    float NearDensityKernelDerivative(float dst)
    {
        float v = smoothingRadius - dst;
        return -v * v * nearDensityDerivScale;
    }

    // Viscosity (Poly6-style): smooth (radius^2 - dst^2)^3 falloff.
    float ViscosityKernel(float dst)
    {
        float v = smoothingRadius * smoothingRadius - dst * dst;
        return v * v * v * viscScale;
    }

    // =================================================================================
    //  Spatial hash
    // =================================================================================

    // Which grid cell a world position falls into (cell size = smoothingRadius).
    Vector3Int CellCoord(Vector3 pos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(pos.x / smoothingRadius),
            Mathf.FloorToInt(pos.y / smoothingRadius),
            Mathf.FloorToInt(pos.z / smoothingRadius)
        );
    }

    // Mix integer cell coords into a single hash (overflow is intentional).
    uint HashCell(Vector3Int cell)
    {
        unchecked
        {
            uint a = (uint)cell.x * HashK1;
            uint b = (uint)cell.y * HashK2;
            uint c = (uint)cell.z * HashK3;
            return a + b + c;
        }
    }

    // Wrap the hash into a table slot in [0, numParticles).
    uint KeyFromHash(uint hash) => hash % (uint)numParticles;

    // Rebuild the sorted hash table from the predicted positions.
    void UpdateSpatialHash()
    {
        for (int i = 0; i < numParticles; i++)
        {
            uint key = KeyFromHash(HashCell(CellCoord(predictedPositions[i])));
            spatialEntries[i] = new SpatialEntry { particleIndex = i, cellKey = key };
            cellStart[i] = int.MaxValue; // sentinel: "no particle in this cell"
        }

        // Group particles of the same cell together.
        Array.Sort(spatialEntries);

        // Record where each cell's run begins in the sorted array.
        for (int i = 0; i < numParticles; i++)
        {
            uint key = spatialEntries[i].cellKey;
            uint prevKey = i == 0 ? uint.MaxValue : spatialEntries[i - 1].cellKey;
            if (key != prevKey)
                cellStart[key] = i;
        }
    }

    // =================================================================================
    //  Rendering — GPU-instanced spheres via a built-in material (no shader authored)
    // =================================================================================
    void BuildRenderResources()
    {
        particleMesh = CreateSphereMesh(8, 6);

        // A built-in shader that supports GPU instancing. Standard is guaranteed to;
        // we add emission so the particles are clearly visible regardless of lighting.
        Shader shader = Shader.Find("Standard");
        particleMaterial = new Material(shader);
        particleMaterial.enableInstancing = true;
        particleMaterial.color = paintColor;
        particleMaterial.EnableKeyword("_EMISSION");
        particleMaterial.SetColor("_EmissionColor", paintColor);

        renderMatrices = new Matrix4x4[numParticles];
    }

    void RenderParticles()
    {
        if (numParticles == 0 || particleMaterial == null || renderMatrices == null)
            return;

        Vector3 scale = Vector3.one * particleScale;
        for (int i = 0; i < numParticles; i++)
            renderMatrices[i] = Matrix4x4.TRS(positions[i], Quaternion.identity, scale);

        // Instanced draw, batched (the API draws up to 1023 instances per call).
        var rp = new RenderParams(particleMaterial);
        const int batch = 1023;
        for (int start = 0; start < numParticles; start += batch)
        {
            int count = Mathf.Min(batch, numParticles - start);
            Graphics.RenderMeshInstanced(rp, particleMesh, 0, renderMatrices, count, start);
        }
    }

    // A small low-poly UV sphere of radius 0.5 (so it scales like a unit-diameter particle).
    Mesh CreateSphereMesh(int sectors, int rings)
    {
        var mesh = new Mesh { name = "ParticleSphere" };

        int vertCount = (rings + 1) * (sectors + 1);
        var verts = new Vector3[vertCount];
        var normals = new Vector3[vertCount];

        int v = 0;
        for (int r = 0; r <= rings; r++)
        {
            float phi = Mathf.PI * r / rings; // 0..π (pole to pole)
            float y = Mathf.Cos(phi);
            float ringRadius = Mathf.Sin(phi);
            for (int s = 0; s <= sectors; s++)
            {
                float theta = 2f * Mathf.PI * s / sectors; // 0..2π around
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
    //  Editor helpers
    // =================================================================================
    void OnValidate()
    {
        particleCount = Mathf.Max(0, particleCount);
        smoothingRadius = Mathf.Max(0.01f, smoothingRadius);
        particleSpacing = Mathf.Max(0.001f, particleSpacing);
    }

    // Draw the container box in the Scene view, honouring the GameObject's rotation.
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.4f, 0.4f, 0.4f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boundsSize);
    }
}
