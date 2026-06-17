using System;
using System.Threading.Tasks; // Parallel.For for the heavy per-particle passes
using UnityEngine;
using UnityEngine.InputSystem; // project uses the new Input System package for the mouse

// =====================================================================================
//  FluidSim — a 2D Smoothed Particle Hydrodynamics (SPH) fluid, written from scratch.
// =====================================================================================
//  Everything is hand-written: gravity, density, pressure, viscosity and wall collisions.
//  NO Unity physics is used (no Rigidbody / Collider / Physics2D) and NO custom shaders.
//  Neighbour lookups are accelerated with a spatial hash grid.
//
//  The code is split into many small, single-responsibility methods so any block
//  (mouse interaction, rendering, a single force) can be deleted or rewritten in
//  isolation. See FluidSim.md for the full write-up and the 2D -> 3D porting notes.
//
//  Attach this component to an empty GameObject. Point an Orthographic camera at the
//  XY plane (the default Unity camera at (0,0,-10) works) and press Play.
// =====================================================================================
public class FluidSim : MonoBehaviour
{
    // ---------------------------------------------------------------------------------
    //  Inspector parameters — tune these to simulate different paints.
    // ---------------------------------------------------------------------------------
    [Header("Spawn")]
    [Tooltip("How many fluid particles to simulate.")]
    public int particleCount = 1500;

    [Tooltip("Distance between particles in the initial grid block.")]
    public float particleSpacing = 0.18f;

    [Tooltip("Centre of the block the particles spawn in.")]
    public Vector2 spawnCentre = new Vector2(0f, 1.5f);

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
    [Tooltip("Gravity vector applied every step (we do NOT use Unity's gravity).")]
    public Vector2 gravity = new Vector2(0f, -10f);

    [Tooltip(
        "Fraction of velocity kept when bouncing off a wall (1 = perfect bounce, 0 = sticks)."
    )]
    [Range(0f, 1f)]
    public float collisionDamping = 0.45f;

    [Header("Bounds (the container)")]
    [Tooltip("Width/height of the box the fluid is trapped in, centred on this GameObject.")]
    public Vector2 boundsSize = new Vector2(14f, 8f);

    [Header("Simulation")]
    [Tooltip("Physics sub-steps per fixed update. More = more stable but slower.")]
    [Range(1, 10)]
    public int iterationsPerFrame = 2;

    [Header("Mouse interaction")]
    [Tooltip("Hold LEFT mouse to pull fluid toward the cursor, RIGHT mouse to push it away.")]
    public bool enableMouseInteraction = true;
    public float interactionRadius = 2.5f;
    public float interactionStrength = 90f;

    [Header("Rendering (instanced, no custom shader)")]
    public Color paintColor = new Color(0.2f, 0.55f, 1f);

    [Tooltip("Visual diameter of a particle in world units.")]
    public float particleScale = 0.18f;

    // ---------------------------------------------------------------------------------
    //  Particle state — plain parallel arrays, no per-frame allocations.
    // ---------------------------------------------------------------------------------
    Vector2[] positions;
    Vector2[] predictedPositions;
    Vector2[] velocities;
    float[] densities; // standard density at each particle
    float[] nearDensities; // "near" density (sharper kernel) for anti-clumping
    Vector2[] velocityBuffer; // snapshot of velocities so viscosity can run in parallel safely

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

    // The 9 neighbouring cells in 2D (this is the only "dimensional" bit besides the cell coord;
    // in 3D it becomes a 27-cell triple loop — see FluidSim.md).
    static readonly Vector2Int[] cellOffsets =
    {
        new Vector2Int(-1, -1),
        new Vector2Int(0, -1),
        new Vector2Int(1, -1),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 0),
        new Vector2Int(1, 0),
        new Vector2Int(-1, 1),
        new Vector2Int(0, 1),
        new Vector2Int(1, 1),
    };

    // Two large primes used to mix the cell coordinates into a hash.
    const uint HashK1 = 15823;
    const uint HashK2 = 9737333;

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
    void InitializeParticles()
    {
        numParticles = Mathf.Max(0, particleCount);

        positions = new Vector2[numParticles];
        predictedPositions = new Vector2[numParticles];
        velocities = new Vector2[numParticles];
        velocityBuffer = new Vector2[numParticles];
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
            // the average of all densities
            restDensity = sum / numParticles;
        }
    }

    // Lay the particles out in a centred square-ish grid block.
    void SpawnInGrid()
    {
        int perRow = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(numParticles)));
        float blockExtent = (perRow - 1) * particleSpacing * 0.5f;

        for (int i = 0; i < numParticles; i++)
        {
            int col = i % perRow;
            int row = i / perRow;
            float x = spawnCentre.x - blockExtent + col * particleSpacing;
            float y = spawnCentre.y - blockExtent + row * particleSpacing;
            Vector2 jitter = UnityEngine.Random.insideUnitCircle * spawnJitter;
            positions[i] = new Vector2(x, y) + jitter;
            velocities[i] = Vector2.zero;
        }
    }

    [ContextMenu("Respawn")]
    void Respawn()
    {
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

    // 1. Apply gravity and the (optional) mouse force, then predict where each particle
    //    will be. Densities are sampled at the predicted positions for extra stability.
    void ApplyGravityAndPredict(float dt)
    {
        Vector2 mousePos = Vector2.zero;
        int mouseDir = 0;
        if (enableMouseInteraction)
            mouseDir = GetMouseForceInput(out mousePos);

        for (int i = 0; i < numParticles; i++)
        {
            velocities[i] += gravity * dt;
            if (mouseDir != 0)
                velocities[i] += MouseForce(positions[i], velocities[i], mousePos, mouseDir) * dt;

            predictedPositions[i] = positions[i] + velocities[i] * dt;
        }
    }

    // 3. Accumulate density and near-density for every particle from its neighbours.
    //    Each particle writes only its own slot, so the loop is run in parallel across cores.
    void ComputeDensities()
    {
        Parallel.For(0, numParticles, ComputeDensity);
    }

    void ComputeDensity(int i)
    {
        Vector2 pos = predictedPositions[i];
        float density = 0f;
        float nearDensity = 0f;

        Vector2Int centre = CellCoord(pos);
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
        Vector2 pos = predictedPositions[i];
        Vector2 force = Vector2.zero;

        Vector2Int centre = CellCoord(pos);
        for (int c = 0; c < cellOffsets.Length; c++)
        {
            uint key = KeyFromHash(HashCell(centre + cellOffsets[c]));
            for (int s = cellStart[key]; s < numParticles && spatialEntries[s].cellKey == key; s++)
            {
                int j = spatialEntries[s].particleIndex;
                if (j == i)
                    continue;

                Vector2 offset = predictedPositions[j] - pos;
                float sqr = offset.sqrMagnitude;
                if (sqr >= radiusSq)
                    continue; // cull before the sqrt
                float dst = Mathf.Sqrt(sqr);

                // Direction away from neighbour; pick an arbitrary dir if exactly overlapping.
                Vector2 dir = dst > 0f ? offset / dst : Vector2.up;

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
        Vector2 pos = predictedPositions[i];
        Vector2 velI = velocityBuffer[i];
        Vector2 viscForce = Vector2.zero;

        Vector2Int centre = CellCoord(pos);
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

    // 6. Move particles by their velocity and keep them inside the box.
    void IntegrateAndResolveCollisions(float dt)
    {
        Vector2 halfSize = boundsSize * 0.5f;
        Vector2 origin = transform.position;

        for (int i = 0; i < numParticles; i++)
        {
            positions[i] += velocities[i] * dt;

            // Work in box-local space so the container can be moved around.
            Vector2 local = positions[i] - origin;
            Vector2 vel = velocities[i];

            if (Mathf.Abs(local.x) > halfSize.x)
            {
                local.x = halfSize.x * Mathf.Sign(local.x);
                vel.x *= -collisionDamping;
            }
            if (Mathf.Abs(local.y) > halfSize.y)
            {
                local.y = halfSize.y * Mathf.Sign(local.y);
                vel.y *= -collisionDamping;
            }

            positions[i] = origin + local;
            velocities[i] = vel;
        }
    }

    // =================================================================================
    //  Pressure equations of state
    // =================================================================================
    float PressureFromDensity(float density) => (density - restDensity) * pressureMultiplier;

    float NearPressureFromDensity(float nearDensity) => nearDensity * nearPressureMultiplier;

    // =================================================================================
    //  Smoothing kernels — 2D normalised. ONLY the scale constants change for 3D.
    //  (Each kernel is its own tiny function so the 2D->3D port is a localised edit.)
    // =================================================================================

    // Precompute the per-radius normalisation constants once per step instead of calling
    // Mathf.Pow inside every kernel evaluation (millions of times per second). This was the
    // single biggest cost.
    void PrecomputeKernelScales()
    {
        float r = smoothingRadius;
        radiusSq = r * r;
        float r4 = r * r * r * r;
        float r5 = r4 * r;
        float r8 = r4 * r4;
        densityScale = 6f / (Mathf.PI * r4);
        densityDerivScale = 12f / (Mathf.PI * r4);
        nearDensityScale = 10f / (Mathf.PI * r5);
        nearDensityDerivScale = 30f / (Mathf.PI * r5);
        viscScale = 4f / (Mathf.PI * r8);
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
    Vector2Int CellCoord(Vector2 pos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(pos.x / smoothingRadius),
            Mathf.FloorToInt(pos.y / smoothingRadius)
        );
    }

    // Mix integer cell coords into a single hash (overflow is intentional).
    uint HashCell(Vector2Int cell)
    {
        unchecked
        {
            uint a = (uint)cell.x * HashK1;
            uint b = (uint)cell.y * HashK2;
            return a + b;
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
    //  Mouse interaction (self-contained — delete the two call sites to remove it)
    // =================================================================================

    // Returns +1 to attract (left button), -1 to repel (right button), 0 for nothing,
    // and outputs the cursor position in world space.
    int GetMouseForceInput(out Vector2 worldPos)
    {
        worldPos = Vector2.zero;
        Camera cam = Camera.main;
        Mouse mouse = Mouse.current;
        if (cam == null || mouse == null)
            return 0;

        bool attract = mouse.leftButton.isPressed;
        bool repel = mouse.rightButton.isPressed;
        if (!attract && !repel)
            return 0;

        Vector2 mouseScreen = mouse.position.ReadValue();
        Vector3 screen = new Vector3(
            mouseScreen.x,
            mouseScreen.y,
            Mathf.Abs(cam.transform.position.z - transform.position.z)
        );
        Vector3 world = cam.ScreenToWorldPoint(screen);
        worldPos = new Vector2(world.x, world.y);
        return attract ? 1 : -1;
    }

    // Force on one particle from the cursor: pulls/pushes within interactionRadius and
    // also damps the existing velocity so grabbed fluid feels controllable.
    Vector2 MouseForce(Vector2 particlePos, Vector2 particleVel, Vector2 mousePos, int dir)
    {
        Vector2 offset = mousePos - particlePos;
        float dst = offset.magnitude;
        if (dst >= interactionRadius || dst <= 0.0001f)
            return Vector2.zero;

        float falloff = 1f - dst / interactionRadius;
        Vector2 dirToMouse = offset / dst;
        // Pull toward / push away, minus the current velocity so it settles smoothly.
        return (dirToMouse * dir * interactionStrength - particleVel) * falloff;
    }

    // =================================================================================
    //  Rendering — GPU-instanced quads via a built-in material (no shader authored)
    // =================================================================================
    void BuildRenderResources()
    {
        particleMesh = CreateDiscMesh(16);

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

    // A small double-sided disc (so it's visible from either side of the camera).
    Mesh CreateDiscMesh(int segments)
    {
        var mesh = new Mesh { name = "ParticleDisc" };
        var verts = new Vector3[segments + 1];
        verts[0] = Vector3.zero;
        for (int i = 0; i < segments; i++)
        {
            float ang = (float)i / segments * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * 0.5f;
        }

        var tris = new int[segments * 3 * 2];
        for (int i = 0; i < segments; i++)
        {
            int a = i + 1;
            int b = (i + 1) % segments + 1;
            // front face
            tris[i * 3 + 0] = 0;
            tris[i * 3 + 1] = a;
            tris[i * 3 + 2] = b;
            // back face (reversed winding)
            tris[segments * 3 + i * 3 + 0] = 0;
            tris[segments * 3 + i * 3 + 1] = b;
            tris[segments * 3 + i * 3 + 2] = a;
        }

        mesh.vertices = verts;
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

    // Draw the container box in the Scene view for orientation.
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.4f, 0.4f, 0.4f);
        Gizmos.DrawWireCube(transform.position, new Vector3(boundsSize.x, boundsSize.y, 0.01f));
    }
}
