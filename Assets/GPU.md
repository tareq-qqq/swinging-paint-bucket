# GPU port — SPH on compute shaders

This document explains the GPU version of the fluid solver: [`FluidCompute.compute`](FluidCompute.compute)
(the kernels), [`FluidSimGPU.cs`](FluidSimGPU.cs) (the C# driver), and
[`FluidParticle.shader`](FluidParticle.shader) (rendering). It exists to break the ~2000-particle
ceiling of the CPU solver — the GPU runs every particle in parallel and reaches 50k–100k+.

The **algorithm is identical** to the CPU 3D solver ([`FluidSim3D.cs`](FluidSim3D.cs)) — read
[`FluidSim.md`](FluidSim.md) for the SPH theory and [`From2DTo3D.md`](From2DTo3D.md) for the 3D
specifics. Only the *execution model* changes: instead of `Parallel.For` over CPU cores, each GPU
thread handles one particle, and the neighbour grid is sorted on the GPU. The CPU files are kept as
the readable reference; this is the high-count path.

This is **Milestone 1: the core fluid** (gravity, spatial hash, density, pressure, viscosity,
collision, rendering). Temperature/humidity/drying come in Milestone 2.

---

## How to run it

1. Open the project in **Unity 6**.
2. Create an empty GameObject and add the **FluidSimGPU** component.
3. **Drag `FluidCompute.compute` into the component's "Compute" slot** (required — compute shaders
   can't be found by name at runtime the way the render shader can).
4. Press **Play**. Particles fall and pool; move/rotate the GameObject to slosh/tilt the container.
   View in the Game view or pan around in the Scene view.
5. Raise `particleCount` (e.g. 50000) and confirm it stays smooth — that's the point of the port.

---

## CPU pass → GPU kernel (one-to-one)

| CPU method (`FluidSim3D.cs`) | GPU kernel (`FluidCompute.compute`) |
|---|---|
| `ApplyGravityAndPredict` | `ExternalForces` |
| `UpdateSpatialHash` (build keys + `Array.Sort` + `CalculateOffsets`) | `UpdateSpatialHash` + `BitonicSort` + `CalculateOffsets` |
| `ComputeDensities` | `CalculateDensities` |
| `ApplyPressureForces` | `CalculatePressureForce` |
| `ApplyViscosity` | `CalculateViscosity` |
| `IntegrateAndResolveCollisions` | `UpdatePositions` |

Each `FixedUpdate`, the driver sub-steps `iterationsPerFrame`× and dispatches the kernels in that
order. Kernel constants (smoothing radius, the 3D kernel normalisation scales, rest density, etc.)
are set as uniforms every step, so Inspector tuning is live.

---

## The buffers

All particle state lives in `ComputeBuffer`s on the GPU (never copied back per frame):

| Buffer | Type | Size | Holds |
|---|---|---|---|
| `Positions`, `PredictedPositions`, `Velocities` | float3 | N | particle state |
| `Densities` | float2 | N | (density, near-density) |
| `SpatialKeys` | uint | N→pow2 | each particle's hashed cell key |
| `SpatialIndices` | uint | N→pow2 | particle index, sorted alongside the keys |
| `CellStart` | uint | N | first sorted slot for each cell key |

`N` = `particleCount`. The two sort buffers are padded up to the next **power of two** (the bitonic
sort needs that); padded entries get key `0xFFFFFFFF` so they sort to the end and are ignored.

---

## The spatial hash on the GPU (the one genuinely new piece)

The CPU sorts its `(key, index)` entries with `Array.Sort`. The GPU can't call that, so we sort on
the GPU with a **bitonic merge sort** — a sorting network that's perfectly parallel (every compare-
and-swap in a stage is independent, ideal for thousands of threads).

1. `UpdateSpatialHash` writes each particle's cell key into `SpatialKeys` and `i` into
   `SpatialIndices` (and resets `CellStart`). The padded tail gets the sentinel key.
2. `BitonicSort` is dispatched **once per sort stage** from the driver's `DispatchSort()`. Each
   dispatch compares and swaps pairs at a fixed stride (`groupWidth`/`groupHeight`/`stepIndex`
   uniforms). After all `log₂(N)·(log₂(N)+1)/2` stages, `SpatialKeys` is sorted ascending and
   `SpatialIndices` has followed along — particles of the same cell are now contiguous.
3. `CalculateOffsets` walks the sorted keys and records, for each cell, the first slot where its
   key appears (`CellStart[key]`).

Neighbour search (in the density/pressure/viscosity kernels) is then the same as the CPU: for each
of the 27 surrounding cells, hash → `CellStart[key]` → walk forward while the key matches.

---

## Rendering (no CPU readback)

[`FluidParticle.shader`](FluidParticle.shader) declares `StructuredBuffer<float3> Positions;`. The
driver draws `numParticles` instances of a small sphere mesh with `Graphics.RenderMeshPrimitives`;
the vertex stage uses `SV_InstanceID` to look up `Positions[instanceID]` and place the sphere — so
positions go **straight from the compute buffer to the screen**, never round-tripping to the CPU.
Shading is a simple baked directional light so the spheres read as 3D without depending on scene
lights. (Velocity/colour wiring is left as a hook for the colour-mixing phase.)

---

## Things worth knowing (for the prof discussion)

- **Why bitonic sort:** it's a data-independent sorting *network* — the comparisons in each stage
  are fixed and independent, so all threads do useful work with no divergence. That's what makes
  sorting fast on a GPU (a regular quicksort would serialise).
- **Viscosity race:** `CalculateViscosity` reads neighbours' `Velocities[j]` while other threads
  write their own `Velocities[i]`. It's a benign race for a smoothing/relaxation step (results are
  marginally non-deterministic but stable); the standard GPU SPH implementations ship it this way.
  If artefacts ever appear, double-buffer velocities for that pass like the CPU version does.
- **Rest-density calibration** runs the hash→sort→density kernels once at start with `dt = 0` and
  reads back the average density via `AsyncGPUReadback` (the only CPU readback, and only once), so
  the spawn starts in equilibrium — same trick as the CPU solver.
- **Movable/rotatable box:** the container's pose is passed to `UpdatePositions` as
  `worldToLocal`/`localToWorld` matrices; the collision clamps in the box's local frame, so moving
  or rotating the GameObject slosh/tilts the fluid exactly like the CPU version.

---

## Milestone 2 (next): environment on the GPU

Add a `Wetness` buffer and fold the exposure-gated temperature/humidity/drying/wind from
`ApplyEnvironment` (CPU) into an `Environment` kernel (run after `CalculateDensities`, since it
needs density), mirroring [`Environment.md`](Environment.md).
