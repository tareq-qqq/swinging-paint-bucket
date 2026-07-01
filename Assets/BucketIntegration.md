# Bucket integration — putting the paint inside the swinging bucket

This document covers wiring the GPU paint ([`FluidSimGPU.cs`](FluidSimGPU.cs) / [`FluidCompute.compute`](FluidCompute.compute))
into a teammate's hand-written **pendulum + rope + bucket** rig, and unifying the shared
environment inputs. Read [`GPU.md`](GPU.md) for the solver and [`ColorMixing.md`](ColorMixing.md)
for the paint colour.

The design goal (from the user): **keep the standalone transparent-box demo working unchanged**, and
add the bucket as a *separate, optional* piece — so the professor can be shown the fluid **with or
without** the bucket. One solver, two demos.

---

## Audit of the teammate's rig (the important checks)

| File | What it is | Unity physics? |
|---|---|---|
| [`PendulumPhysics.cs`](PendulumPhysics.cs) | Spherical pendulum (θ/φ), hand-written Euler integration | **No** |
| [`RopeSystem.cs`](RopeSystem.cs) | Verlet particle chain + distance constraints | **No** |
| [`BucketSystem.cs`](BucketSystem.cs) | Kinematic — copies the pendulum pose onto its transform | **No** |

No `Rigidbody`, no colliders, no joints — all hand-written, so it satisfies the assignment's
**no-built-in-physics** rule (good for the interview). The bucket is a **cylinder**
(`radius`, `height`, floor at `-height/2`) whose transform is driven by the pendulum each
`FixedUpdate`.

> **Input System note:** the teammate's `SimulationController` uses the *legacy* `Input` class while
> the fluid uses the *new* Input System. For both to run, set **Project Settings → Player → Active
> Input Handling = Both**.

---

## One shared source for the environment — [`EnvironmentConfig.cs`](EnvironmentConfig.cs)

The assignment lists the environment as adjustable **inputs** (gravity, air resistance, humidity,
friction). `EnvironmentConfig` is the **single source** for all of them, so they live in **one place**
— the paint no longer keeps its own copies:

| Field on `EnvironmentConfig` | Read by |
|---|---|
| `gravity` | pendulum, rope, paint |
| `airResistance` | pendulum (swing damping) **and** paint (air drag) — *one* value for both |
| `ambientTemperature` | paint |
| `humidity` | paint |
| `wind` | paint |
| `friction` | surfaces (Phase 5) |

- Put **one** `EnvironmentConfig` on a GameObject in **every** scene — box demo *or* bucket demo.
- `FluidSimGPU` no longer has its own `gravity` / `windForce` / `airDrag` / `humidity` / temperature
  fields; it reads them from `EnvironmentConfig.Instance`. It only keeps how the paint *responds*
  (drying rate, temperature→viscosity strength, air-effect toggle, collision damping).
- If no `EnvironmentConfig` is found, the solvers fall back to safe defaults (and the paint logs a
  one-time warning), so nothing hard-crashes — but you should add one to control the values.

So gravity / air resistance / temperature / humidity / wind are entered **once** and the bucket, rope
and paint always agree. **`airResistance` is deliberately one number** that damps both the swing and
the air-exposed paint (they're the same physical "air resistance"); split it later only if a demo
needs different feels.

---

## How the paint is contained — [`BucketContainer.cs`](BucketContainer.cs) (the new file)

`BucketContainer` is the **only** file that knows about the bucket. `FluidSimGPU` gained one optional
field — a `Container override` slot — and **nothing else changes the box behaviour**: leave the slot
empty and it's the original box demo (the collision kernel takes its unchanged box branch); assign a
`BucketContainer` and the paint is contained by the bucket instead.

`BucketContainer` exposes the cylinder to the solver (all geometry read from `BucketSystem`, so the
bucket's inputs live in one place):

- **Pose** — the bucket's transform (origin at the bucket centre, driven by the pendulum), so the
  paint sloshes/tilts/drains exactly as the bucket swings.
- **Radius / Height / FloorY / HoleRadius / OpenTop**.
- **`FillSpawn`** — spawns the paint as a grid that fits inside the cylinder, on the floor, in the
  bucket's local frame (then transformed to world), so it starts *inside* the bucket.

### The collision (in `FluidCompute.compute`, `UpdatePositions`)

A `containerIsCylinder` uniform switches the existing box clamp for a **cylinder** clamp in the
bucket's local frame:

1. **Side wall** — clamp the XZ radius to `cylinderRadius`, reflect the radial velocity (×
   `collisionDamping`). Only while the particle is within the bucket's **height band**, so paint that
   has left (below the floor or over the rim) isn't pulled back.
2. **Floor with a spill-hole** — the floor is solid **except** a central circle of radius
   `holeRadius`: paint within it falls straight **through and out the bottom** (the classic
   pendulum-painting stream that will draw on the canvas in the next phase); paint outside it pools.
   The floor only acts within a thin slab just under it, so already-drained paint keeps falling
   instead of being yanked back up.
3. **Open top** — by default paint can be flung out over the rim under hard swings; a sealed-lid
   option clamps the top.

The box demo is byte-for-byte unchanged: with no container, `containerIsCylinder = 0` and the kernel
runs the original box branch.

---

## Inputs exposed (matching the assignment's required variables)

| Input | Where |
|---|---|
| Bucket weight, radius, height, **paint-exit hole diameter** | `BucketSystem` |
| Amount of paint | `FluidSimGPU.particleCount` |
| Rope length, type/elasticity, pivot | `RopeSystem` / `SimulationController` |
| Initial angle, initial speed, direction, swing count | `PendulumPhysics` |
| Gravity, air resistance, humidity, friction | `EnvironmentConfig` |
| Paint colour(s), viscosity, colour-flow speed, multi-colour | `FluidSimGPU` (see `ColorMixing.md`) |

---

## Scale: grow the bucket, don't shrink the fluid

The SPH solver is tuned for its world scale (`particleSpacing 0.25`, `smoothingRadius 0.5`,
`particleScale 0.2`, `pressureMultiplier 200`). Shrinking those to fit a tiny bucket makes the
kernel-scale constants blow up (**the paint explodes**) and the particles invisible. So the bucket is
sized **up** to the fluid instead — defaults now `bucketRadius 2.5`, `bucketHeight 4`, `holeRadius
0.4`, `rope restLength 5`, `pivot (0,12,0)`. The **same** SPH defaults work in the box and the bucket.

**Amount of paint = a fraction of the bucket volume.** In bucket mode you don't set `particleCount` —
you set `BucketContainer.fillFraction` (0–1). The paint is spawned as a **cylinder matching the
bucket** (radius = bucket radius, height = fillFraction × bucket height), sitting on the floor, and
the particle count is computed from the bucket's volume (`≈ π·r²·h·fill / spacing³`). It is placed on
the **first frame** (after the pendulum positions the bucket), so it always lands inside.

**Anti-explosion safety net:** `FluidSimGPU.maxSpeed` clamps particle velocity (default 25). A bad
spawn or a fast bucket move can no longer blow the sim to infinity — it just settles.

## Scene setup

1. Build the teammate's rig (pivot, `PendulumPhysics`, `RopeSystem`, `BucketSystem`) per their setup.
   Make sure a **Camera is tagged MainCamera** and pointed at the bucket (~y 7). For the rope to show
   in the **Game** view, add a `LineRenderer` to the rope object and assign it to `RopeSystem`
   (otherwise only its Scene-view gizmo shows).
2. Add **one** `EnvironmentConfig` to the scene; set gravity/air/temperature/humidity/wind.
3. On the **paint** GameObject (a `FluidSimGPU`): drag `FluidCompute.compute` into Compute; add a
   `BucketContainer`, assign the `BucketSystem` to it, drag that `BucketContainer` into the
   `FluidSimGPU` **Container override** slot, and set the `BucketContainer` **Fill Fraction** slider
   (e.g. 0.5). Leave the SPH fields at their box defaults. The transparent box turns off automatically.
4. **See the bucket:** add a `BucketVisual` component (e.g. on the Bucket) and assign the
   `BucketSystem` — it draws a transparent open-top cylinder in the **Game** view (not just a gizmo).
5. **Drag it:** add a `BucketDragHandler` (it auto-finds the pendulum/bucket/camera). Click the bucket
   and drag — it follows the cursor on its swing arc without snapping; release to throw it. Keys 1–4
   swap rope material, Space kicks.
6. Press Play: paint pools in the bucket, sloshes as it swings, and streams out the floor hole.

**Box demo (no bucket):** a separate `FluidSimGPU` with the Container override **empty** and the
original 20k/box values. Add an `EnvironmentConfig` here too; optionally assign a transparent-box mesh
to the `Bounds Visual` slot (it auto-hides whenever a bucket is used).

> **Input System:** dragging uses the new Input System (`Mouse.current`). Set **Project Settings →
> Player → Active Input Handling = Both** (or Input System Package), or `Mouse.current` is null and
> dragging silently does nothing.

---

## Deferred (not done yet, by design)

- **2-way coupling** — the paint mass pushing back on the pendulum. One-way (bucket → paint) is what
  this phase delivers; two-way depends on how the pendulum integrator accepts an external torque, and
  is scoped later.
- **Canvas + surface staining** — the paint streaming out the hole currently just falls; the next
  phase ([`Surfaces`], Phase 5) catches it on a canvas and deposits colour.
- **Non-cylinder buckets** — the collision assumes a cylinder (matches `BucketSystem`); an arbitrary
  bucket mesh would need an SDF.
