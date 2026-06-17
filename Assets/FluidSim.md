# FluidSim — 2D SPH Fluid Simulation (design & approach)

This document explains how [`FluidSim.cs`](FluidSim.cs) works: the physics, the maths, the
optimisation, every tunable parameter, how to run it, and what changes when we move to 3D.

It is the **fluid core** of the _Swinging Paint Bucket_ project. For now it is a standalone,
tunable fluid (one paint, one colour) — the pendulum/bucket and the paint-on-canvas steps come
later.

---

## 1. Hard constraints (and how they are met)

| Constraint                                                                     | How it is satisfied                                                                                                                                                             |
| ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **No Unity physics** (no `Rigidbody`/`Collider`/`Physics2D`, no Unity gravity) | Gravity, density, pressure, viscosity and wall collisions are all written by hand in `FluidSim.cs`. The only Unity APIs used are math types, `Input`, `Camera`, and `Graphics`. |
| **No custom shaders**                                                          | Particles are drawn with `Graphics.RenderMeshInstanced` using a _built-in_ `Standard` material. No shader code is authored.                                                     |
| **One fluid only**                                                             | A single colour, a single set of parameters. No colour mixing.                                                                                                                  |
| **Spatial hashing**                                                            | Neighbour search uses a sorted-array spatial hash grid (O(n) build, ~O(1) query per particle).                                                                                  |
| **Everything in one file**                                                     | All logic lives in `Assets/FluidSim.cs`.                                                                                                                                        |

---

## 2. What is SPH?

**Smoothed Particle Hydrodynamics** represents a fluid as many particles. Any quantity at a point
is estimated by a weighted sum over nearby particles, where the weight comes from a _smoothing
kernel_ `W(distance, radius)` that is large for close particles and falls to zero at
`smoothingRadius`. We follow the classic **Müller et al. 2003** formulation with the
**near-density/near-pressure** trick popularised by **Sebastian Lague** to stop particles clumping.

The fluid is driven by three forces, all derived from the particle neighbourhood:

1. **Gravity** — a constant downward acceleration (and the optional mouse force).
2. **Pressure** — particles in dense regions push outward toward sparse regions. This is what makes
   the fluid behave as a roughly incompressible liquid (it splashes and pools instead of passing
   through itself).
3. **Viscosity** — neighbouring particles drag each other's velocities together. Low viscosity =
   watery; high viscosity = thick, sticky paint.

---

## 3. The per-step algorithm

Physics runs in `FixedUpdate`, sub-stepped `iterationsPerFrame` times with
`dt = fixedDeltaTime / iterationsPerFrame` for stability. Each sub-step (`SimulationStep`) does:

```
1. ApplyGravityAndPredict(dt)        // v += g*dt (+ mouse force); predicted = pos + v*dt
2. UpdateSpatialHash()               // rebuild the neighbour grid from predicted positions
3. ComputeDensities()                // density + nearDensity at each particle
4. ApplyPressureForces(dt)           // pressure gradient -> v
5. ApplyViscosity(dt)                // velocity smoothing -> v
6. IntegrateAndResolveCollisions(dt) // pos += v*dt; bounce off the box walls
```

**Why predicted positions?** Densities (step 3) are sampled at where particles _will be_ after
gravity (`predictedPositions`), not where they are now. This look-ahead damps oscillation and is a
key stability trick from Lague's implementation. The actual move happens later in step 6.

### Density (step 3)

For each particle _i_, sum over neighbours _j_ within `smoothingRadius`:

```
density[i]     = Σ_j  DensityKernel(dist_ij)        // (r - d)^2 kernel
nearDensity[i] = Σ_j  NearDensityKernel(dist_ij)    // (r - d)^3 sharper kernel
```

Particle mass is treated as `1`, so density is just a weighted neighbour count.

### Pressure (step 4)

Convert density to pressure with an equation of state, then apply a symmetric force along the
kernel gradient:

```
P[i]     = (density[i] - restDensity) * pressureMultiplier
Pnear[i] = nearDensity[i] * nearPressureMultiplier      // always repulsive, short range

force += dir * DensityKernelDerivative(d)     * sharedPressure / density[j]
force += dir * NearDensityKernelDerivative(d) * sharedNear     / nearDensity[j]
v[i]  += force / density[i] * dt
```

`sharedPressure = (P[i] + P[j]) / 2` keeps the force symmetric (Newton's 3rd law) so momentum is
conserved. The **near** term only ever pushes (it uses raw `nearDensity`, never goes negative),
which is what prevents particles from collapsing onto the exact same point — the classic SPH
"clumping" failure.

### Viscosity (step 5)

Pull each particle's velocity toward its neighbours' velocities:

```
viscForce += (v[j] - v[i]) * ViscosityKernel(d)
v[i] += viscForce * viscosityStrength * dt
```

### Integration & collisions (step 6)

`pos += v * dt`, then clamp into the box (centred on the GameObject, size `boundsSize`). On hitting
a wall the position is pushed back to the edge and that velocity component is reflected and scaled
by `collisionDamping` (`v *= -collisionDamping`). This is the only "collision" in the project and
it is hand-written — no colliders.

---

## 4. The smoothing kernels

Each kernel is a small standalone function. They use **2D normalisation constants** (derived from
the integral of the kernel over a 2D disc of radius `r`). Shapes follow Lague's spiky/poly6 kernels:

| Function                      | Form                   | Used for                                       |
| ----------------------------- | ---------------------- | ---------------------------------------------- |
| `DensityKernel`               | `(r-d)^2 · 6/(π r⁴)`   | density sum, and (via its derivative) pressure |
| `DensityKernelDerivative`     | `-(r-d) · 12/(π r⁴)`   | pressure gradient                              |
| `NearDensityKernel`           | `(r-d)^3 · 10/(π r⁵)`  | near-density sum                               |
| `NearDensityKernelDerivative` | `-(r-d)^2 · 30/(π r⁵)` | near-pressure gradient                         |
| `ViscosityKernel`             | `(r²-d²)^3 · 4/(π r⁸)` | velocity smoothing                             |

All return `0` for `d ≥ smoothingRadius`.

> **This is the only place the maths differs in 3D** — see §7.

---

## 5. Spatial hashing (the optimisation)

A naïve neighbour search is O(n²) — every particle checks every other. With ~1500 particles that is
~2.25M checks per sub-step. The spatial hash makes it ~O(n).

**Idea:** the grid cell size equals `smoothingRadius`, so a particle can only influence others in
its own cell and the 8 surrounding cells (a 3×3 block). We bucket particles by cell and only look
inside those 9 cells.

**Data structure (sorted-array scheme — no `Dictionary`, no per-frame allocation):**

- `CellCoord(pos)` → integer `(x, y)` cell from `floor(pos / smoothingRadius)`.
- `HashCell(cell)` → mix the two ints with large primes into one `uint` (overflow intended).
- `KeyFromHash(hash)` → wrap into `[0, numParticles)` — the table has one slot per particle.
- `spatialEntries[i] = {particleIndex, cellKey}` for every particle, then `Array.Sort` so particles
  sharing a cell key become a **contiguous run**.
- `cellStart[key]` = index of the first entry with that key (sentinel `int.MaxValue` if empty).

**Query:** for a particle, compute its cell, loop the 9 offsets, hash each neighbour cell to a key,
jump to `cellStart[key]`, and walk forward while `spatialEntries[s].cellKey == key`. Then the exact
distance check (`dst < smoothingRadius`) filters out the false positives that hash collisions and
the square-cell shape let through.

`UpdateSpatialHash` is called once per sub-step (step 2) on the predicted positions.

### Other performance work

The spatial hash gives the right _complexity_, but three more things keep the constant factor low:

1. **Cached kernel constants.** The kernels' normalisation factors (e.g. `6/(π r⁴)`) only depend on
   `smoothingRadius`, so they are computed once per step in `PrecomputeKernelScales` instead of
   calling `Mathf.Pow` inside every kernel evaluation (which happens millions of times per second).
2. **Squared-distance culling.** Neighbour loops compare `sqrMagnitude` against `radiusSq` and only
   take a `sqrt` for particles that are actually in range.
3. **Multithreading.** The three heavy passes — density, pressure, viscosity — each have every
   particle write only its own slot, so they run with `Parallel.For` across all CPU cores. The
   viscosity pass first snapshots velocities into `velocityBuffer` so workers read a stable copy of
   their neighbours' velocities (no data race).

---

## 6. Mouse interaction (optional, isolated)

`GetMouseForceInput` reads the buttons (left = attract, right = repel) and converts the cursor to
world space via `Camera.main`. `MouseForce` applies a distance-falloff pull/push to particles within
`interactionRadius`, minus their current velocity so grabbed fluid settles smoothly. It is called
from exactly one spot in `ApplyGravityAndPredict`; delete that call (and set
`enableMouseInteraction = false`) to remove the feature entirely.

---

## 7. Porting to 3D later

The architecture was built so the 3D move is mechanical. You change:

1. **Vector type:** `Vector2` → `Vector3` in the particle arrays and force loops.
2. **Cell coord:** `CellCoord` returns a `Vector3Int`; `HashCell` mixes a third prime for `z`.
3. **Neighbour offsets:** `cellOffsets` grows from the 9 (3×3) entries to 27 (3×3×3).
4. **Collisions:** add the `z` axis clamp in `IntegrateAndResolveCollisions`.
5. **Kernel constants only:** the kernel _shapes_ stay identical, but the normalisation constants
   change (2D disc integral → 3D sphere integral). Replace the `scale` lines, e.g. the spiky
   density kernel becomes `15/(π r⁶)`, its derivative `-45/(π r⁶)`, etc. Nothing that _calls_ the
   kernels changes.

Everything else — the step order, the spatial hash, pressure/viscosity logic — is untouched.

---

## 8. Parameters (map to the project's required inputs)

| Inspector field                                   | Effect                                     | Project requirement (PDF)      |
| ------------------------------------------------- | ------------------------------------------ | ------------------------------ |
| `viscosityStrength`                               | paint thickness (0 = watery, high = honey) | لزوجة الطلاء (viscosity)       |
| `restDensity`                                     | target/rest density the fluid settles to   | كثافة (density)                |
| `pressureMultiplier`                              | incompressibility / splashiness            | تدفق اللون (flow behaviour)    |
| `nearPressureMultiplier`                          | anti-clumping short-range repulsion        | stability                      |
| `smoothingRadius`                                 | interaction radius / smoothness            | —                              |
| `gravity`                                         | gravity vector (hand-written)              | الجاذبية (gravity)             |
| `collisionDamping`                                | energy kept on wall bounce                 | الاحتكاك (friction-like loss)  |
| `boundsSize`                                      | the container box                          | حدود اللوحة (canvas/container) |
| `particleCount`, `particleSpacing`, `spawnCentre` | amount/placement of paint                  | كمية الطلاء                    |
| `iterationsPerFrame`                              | sub-steps (stability vs speed)             | —                              |
| `interactionRadius`, `interactionStrength`        | mouse force                                | —                              |
| `paintColor`, `particleScale`                     | look of the paint                          | لون الطلاء                     |

**`autoCalibrateRestDensity`** (on by default): at startup it measures the fluid's natural density
in its spawn grid and sets `restDensity` to that. This means the spawn is already in equilibrium, so
the fluid neither explodes nor collapses on frame one regardless of the spacing/radius you chose. If
you want to set `restDensity` by hand, turn this off.

---

## 9. How to run it

1. Open the project in **Unity 6** (`6000.3.15f1`).
2. In `SampleScene`, create an empty GameObject and add the **FluidSim** component (or use one
   already in the scene). Leave it at the origin.
3. Set the **Main Camera** to **Orthographic**. The default camera at `(0, 0, -10)` looking toward
   +Z is fine; raise the camera's _Size_ until the grey bounds gizmo fits the view.
4. Press **Play**. Particles fall, splash and settle into a flat-topped pool at the bottom of the
   box.
5. While playing, drag the inspector sliders to retune. To restart the layout without leaving Play
   mode, right-click the component header → **Respawn**.

### What "working" looks like (verification)

- **Pooling:** particles settle into a level pool without exploding outward or collapsing to a dot.
  This confirms density + pressure + collisions are correct.
- **Viscosity:** raise `viscosityStrength` → fluid visibly thickens and moves as a blob; lower it →
  it splashes freely.
- **Density/pressure:** raising `restDensity` compresses the pool; `pressureMultiplier` too low →
  particles overlap/clump, too high → jitter. Confirms the tuning knobs work.
- **Mouse:** hold left/right mouse → nearby particles are pulled toward / pushed away from the
  cursor.
- **Optimisation:** the spatial hash keeps it interactive at the default count. (To prove it helps,
  you could temporarily replace the 9-cell neighbour loops with a brute-force `for j in all
particles` loop and watch it slow down.)

---

## 10. Planned next steps (not in this version)

- Velocity-based colouring and a metaball/surface **shader** for a liquid look.
- **3D** version (see §7).
- **Multiple paints** + colour mixing.
- Coupling to the **swinging bucket**: emit particles from the bucket's moving spout and let them
  stick to a canvas surface.
