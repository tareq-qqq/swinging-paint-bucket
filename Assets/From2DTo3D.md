# From 2D to 3D — how `FluidSim.cs` became `FluidSim3D.cs`

This document explains, change by change, how the 2D SPH solver
([`FluidSim.cs`](FluidSim.cs)) was ported to 3D ([`FluidSim3D.cs`](FluidSim3D.cs)). It is meant as
study material: read [`FluidSim.md`](FluidSim.md) first for the SPH theory, then this for the
*delta*. The 2D files were intentionally left untouched as a reference.

The headline: **the SPH algorithm is almost entirely dimension-agnostic.** The simulation loop, the
spatial hash, the pressure/viscosity logic, the parallelisation and the auto-calibration are
identical. Only a handful of clearly-localised spots change. They are listed below in roughly the
order they appear in the file.

---

## 0. Why most of it doesn't change

SPH is written in terms of **distances** and **vectors between particles**, not coordinates. A
distance is a single number whether the particles live on a plane or in space, and a direction is
just a unit vector of whatever dimension. So every formula — density sum, pressure gradient,
viscosity smoothing, the integration `pos += v*dt` — has the exact same form in 2D and 3D. That is
why the port is mechanical: we mostly swap the *type* of the vectors and widen a couple of loops.

The things that genuinely "know" about the dimension are:

1. the vector type (`Vector2` vs `Vector3`),
2. the spatial-hash grid (cells and how many neighbours surround one),
3. the kernel **normalisation constants**,
4. wall collisions (one extra axis),
5. rendering (a disc vs a sphere).

Everything else is copy-paste.

---

## 1. Vector type: `Vector2` → `Vector3`

**What:** the particle state arrays and all force math change type.

```
Vector2[] positions;            ->   Vector3[] positions;
Vector2[] predictedPositions;   ->   Vector3[] predictedPositions;
Vector2[] velocities;           ->   Vector3[] velocities;
Vector2[] velocityBuffer;       ->   Vector3[] velocityBuffer;
Vector2  gravity;               ->   Vector3  gravity;
Vector2  spawnCentre;           ->   Vector3  spawnCentre;
```

**Why:** a 3D particle needs an x, y **and** z. Because `Vector3` supports the same operators
(`+`, `-`, `* float`, `.magnitude`, `.sqrMagnitude`, `.normalized`), the bodies of
`ComputeDensity`, `ComputePressureForce` and `ComputeViscosityForce` are otherwise unchanged.

---

## 2. The spatial hash grid

The hash is the trickiest "dimensional" part, but the changes are still small.

### 2a. Cell coordinate — `Vector2Int` → `Vector3Int`

```
Vector2Int CellCoord(Vector2 pos)      ->   Vector3Int CellCoord(Vector3 pos)
    floor(pos.x / r), floor(pos.y / r)          ... and floor(pos.z / r)
```

A grid cell is now a little **cube** of side `smoothingRadius` instead of a square.

### 2b. Hash function — a third prime

```
2D:  hash = x*K1 + y*K2
3D:  hash = x*K1 + y*K2 + z*K3      (K3 = 440817757)
```

**Why:** we mix each integer coordinate with a distinct large prime so that different cells rarely
collide into the same table slot. Adding a z coordinate means adding a third prime term. Integer
overflow during the multiply is fine and intended — we only care that the bits are well-scrambled.

### 2c. Neighbour offsets — 9 → 27 cells

In 2D a particle can only affect others in its own cell and the 8 around it: a 3×3 block = **9**
cells. In 3D it is a 3×3×3 block = **27** cells.

```
3^2 = 9   (2D)        3^3 = 27   (3D)
```

The reason it is exactly `3^dimension`: the grid cell size equals `smoothingRadius`, so a particle's
influence sphere (radius `r`) can reach at most one cell in each direction (`-1, 0, +1`) along every
axis. Two axes → `3×3`; three axes → `3×3×3`. In the code the 9-entry hand-written list is replaced
by a triple loop that fills 27 offsets once in `BuildCellOffsets`:

```csharp
for z in -1..1: for y in -1..1: for x in -1..1: add (x,y,z)
```

Everything downstream — sorting `spatialEntries`, `cellStart`, walking the run of a cell — is
**identical**; the neighbour loops just iterate `cellOffsets.Length` (now 27) instead of 9.

> Practical consequence: 27/9 = 3× as many candidate cells, so 3D is heavier per particle. That is
> why the default `particleCount` is lower and why the smooth-framerate ceiling is below the 2D one.

---

## 3. The kernel normalisation constants — the only real math change

The smoothing kernels keep the **same shape** in 3D (`(r-d)²`, `(r-d)³`, `(r²-d²)³`). What changes
is the constant in front of each one. Concretely, in `PrecomputeKernelScales`:

| Kernel (shape) | 2D constant | 3D constant |
|---|---|---|
| `DensityKernel` `(r-d)²` | `6 / (π r⁴)` | `15 / (2π r⁵)` |
| `DensityKernelDerivative` `(r-d)` | `12 / (π r⁴)` | `15 / (π r⁵)` |
| `NearDensityKernel` `(r-d)³` | `10 / (π r⁵)` | `15 / (π r⁶)` |
| `NearDensityKernelDerivative` `(r-d)²` | `30 / (π r⁵)` | `45 / (π r⁶)` |
| `ViscosityKernel` `(r²-d²)³` | `4 / (π r⁸)` | `315 / (64π r⁹)` |

### Why the constants differ

A smoothing kernel must **integrate to 1** over its area of influence — it is a weighted average, so
all the weights together have to sum to one. In 2D that area is a **disc** of radius `r`; in 3D it
is a **sphere** of radius `r`. The normalisation constant is exactly what you divide by to make that
integral equal 1, and the integral of the same shape over a disc is a different number than over a
sphere (more volume to cover, and an extra factor of `r` from the third dimension). That is why, for
example, the powers of `r` in the denominator go up by one (`r⁴ → r⁵`, `r⁵ → r⁶`, `r⁸ → r⁹`): the
3D integral has one more length dimension to account for.

These exact constants are the standard SPH spiky/poly6 kernels (Müller 2003) as used in Sebastian
Lague's 3D fluid project.

### Why getting the exact constant isn't critical here

`autoCalibrateRestDensity` (on by default) measures the fluid's **own** average density right after
spawning and sets `restDensity` to it. Pressure then reacts to *relative* deviations from that
measured baseline. So if a constant were off by a scale factor, the baseline simply shifts with it
and the fluid still settles correctly — the constants mostly affect the *units* of the numbers, not
the qualitative behaviour. (They still matter for matching real-world density values and for
cross-comparing setups, which is why we use the correct 3D ones.)

---

## 4. Spawning in a cube

```
2D:  perRow  = ceil(sqrt(N));      grid over (x, y)
3D:  perAxis = ceil(cbrt(N));      grid over (x, y, z)
```

**Why:** to fill a square you take the square root of the count; to fill a cube you take the cube
root. The jitter also changes from `Random.insideUnitCircle` to `Random.insideUnitSphere`.

---

## 5. Wall collisions + the movable / rotatable box

**What changed (two things):**

1. **A third axis.** `IntegrateAndResolveCollisions` now clamps `z` as well as `x` and `y` against
   `±boundsSize/2`.
2. **Local-frame collision** so the container can be **moved and rotated** with the scene gizmo.

In 2D the box was axis-aligned, so the clamp worked directly on world coordinates (only offset by
the GameObject's position). In 3D we want to tilt the box too, so each particle is first converted
into the box's **local frame**, clamped/reflected there, then converted back:

```
local    = inverse(rotation) * (worldPos - boxOrigin)   // undo the box's rotation+position
localVel = inverse(rotation) * worldVel
... clamp local.x/y/z, reflect that velocity component (* -collisionDamping) ...
worldPos = boxOrigin + rotation * local                 // redo it
worldVel = rotation * localVel
```

**Why:** clamping against a box is trivial when the box is axis-aligned. A rotated box is not
axis-aligned in world space, so we rotate the *problem* into a space where it is (the box's own
frame), solve it there, and rotate the answer back. Gravity is deliberately **not** rotated — it
stays world-down — so tilting the box makes the fluid pour into the low corner, which is the
behaviour we want for a tilting canvas / swinging bucket.

The gizmo (`OnDrawGizmos`) similarly sets `Gizmos.matrix` to the GameObject's transl/rotate matrix
so the wire cube tilts with the box.

---

## 6. Rendering: disc → sphere

**What:** `CreateDiscMesh` (a flat fan of triangles) is replaced by `CreateSphereMesh` (a small
low-poly UV sphere with proper normals).

**Why:** a flat disc looks fine head-on in 2D, but in 3D you orbit the camera and a flat disc would
look like a coin edge-on. A real (if low-poly) sphere with normals reads as a round particle from
every angle and catches the scene light. The rendering path is otherwise the same: build the mesh
once, one instanced `Standard` material with emission, `Graphics.RenderMeshInstanced` batched by
1023. (The `Matrix4x4.TRS` now places a `Vector3` position, but it already used `Vector3` there.)

---

## 7. Removed: mouse interaction

The 2D version had `GetMouseForceInput` / `MouseForce` and the `UnityEngine.InputSystem` import.
These were **removed** in 3D because pushing fluid with a 2D cursor in a 3D volume is ambiguous, and
the movable/rotatable box (section 5) is a better and simpler way to disturb the fluid. Fewer moving
parts, and it doubles as the slosh/pour interaction.

---

## Summary table

| Concern | 2D | 3D | Same algorithm? |
|---|---|---|---|
| Vector type | `Vector2` | `Vector3` | yes |
| Cell coordinate | `Vector2Int` | `Vector3Int` | yes |
| Hash mixing | 2 primes | 3 primes | yes |
| Neighbour cells | 9 (3²) | 27 (3³) | yes |
| Kernel shapes | `(r-d)²` etc. | identical | yes |
| Kernel constants | disc integral | sphere integral | constants only |
| Collisions | clamp x,y | clamp x,y,z, local frame | yes |
| Box | movable | movable + rotatable | — |
| Particle mesh | disc | sphere | — |
| Mouse force | yes | removed | — |
| Sim loop / hash / pressure / viscosity / parallelism / auto-calibration | — | unchanged | **yes** |

If you can explain the kernel-normalisation reasoning in section 3 and the 9→27 / `3^d` reasoning in
section 2c, you understand the heart of what makes this "3D" rather than "2D".
