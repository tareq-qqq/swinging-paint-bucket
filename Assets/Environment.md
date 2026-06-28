# Environment factors — Temperature & Humidity (Phase 1)

This document explains the environmental physics added to [`FluidSim3D.cs`](FluidSim3D.cs):
**temperature**, **humidity**, **drying/curing**, and **wind + air resistance** — the environment
factors the assignment PDF asks for. It is written to be explainable to the prof: every effect is a
simple, defensible *physical relationship*, not full thermodynamics.

Read [`FluidSim.md`](FluidSim.md) for the underlying SPH theory first.

---

## The core idea: drying and air motion only touch AIR-EXPOSED paint

The most important design decision (and the most physically correct one):

> Drying and air drag/wind are **surface phenomena**. They only happen where paint actually meets
> the air — not deep inside the body of paint, and not (later) sealed inside the bucket.

Why this matters, with concrete questions it answers:

- *Should paint dry while sitting in the bucket?* Mostly **no** — only the exposed top skins over;
  the bulk stays wet for a long time.
- *Why does a little paint dry faster than a lot?* Because drying is a **surface-to-volume** effect:
  a thin spread is almost all surface, a deep pool is mostly interior.
- *Should wind/air-drag affect paint inside the container?* **No** — moving air only touches paint
  in the open (the surface, and droplets falling toward the canvas).

### How we detect "exposed to air": density

We get air-exposure for free from a quantity SPH already computes — **density**. An interior
particle is surrounded by neighbours on all sides, so its density is high. A particle on the surface
(or an airborne droplet) is missing neighbours on the air side, so its density is low. This is the
standard SPH **free-surface detection**.

```
bulkDensity = airExposureThreshold * restDensity
exposure[i] = clamp01( (bulkDensity - density[i]) / bulkDensity )   // 0 = buried, 1 = airborne
```

`airExposureThreshold` (default 0.85) sets where "bulk" begins. Drying, wind and drag are all
multiplied by `exposure`, which gives us, automatically:

- **No drying in the bulk/bucket** (interior `exposure ≈ 0`).
- **The volume effect for free** (thin spread = mostly exposed = dries fast; deep pool = stays wet).
- **Air only on open-air paint** (bulk and, later, enclosed paint are untouched).

> **Honest caveat:** paint pressed against a solid wall *also* has slightly reduced density (the
> well-known SPH "boundary deficiency"), so it can read as mildly exposed. `airExposureThreshold`
> filters out all but genuinely free-surface particles. Once the real bucket walls exist we can
> treat wall-contact explicitly.

---

## The four effects

All of this lives in one method, `ApplyEnvironment(dt)`, which runs **after** `ComputeDensities`
(it needs density) and is parallelised across cores like the other passes.

### 1. Temperature → viscosity (GLOBAL, not surface-gated)

Warm paint is thinner; cold paint is thicker. Unlike drying, this applies to **all** the paint,
because heat conducts through the whole body — the bulk warms/cools too. Implemented as a global
multiplier on viscosity (`TemperatureViscosityMultiplier`, used in `ApplyViscosity`):

```
multiplier = max(0.05, 1 - tempViscosityFactor * (ambientTemperature - 20))
```

At the 20 °C reference the multiplier is 1. Above 20 °C it drops below 1 (thinner); below 20 °C it
rises above 1 (thicker). `tempViscosityFactor = 0` disables it.

### 2. Humidity + temperature → drying (surface-gated)

Each step, exposed paint loses wetness:

```
wetness[i] -= dryingRate * tempDryFactor * (1 - humidity) * exposure[i] * dt
tempDryFactor = clamp01(ambientTemperature / 40)        // hot air dries faster
```

- **Humid air** (`humidity → 1`) → `(1 - humidity) → 0` → drying nearly stops (paint stays runny).
- **Hot, dry air** → fast drying.
- `wetness` is per-particle, starts at 1 (fully wet), and only the air-exposed paint loses it.

### 3. Setting / curing

When a particle's `wetness` falls below `setWetnessThreshold`, its motion is strongly damped toward
zero — the paint has "set":

```
if (wetness[i] <= setWetnessThreshold) velocity[i] *= max(0, 1 - 20*dt)
```

This is the hook the future **canvas-sticking** step builds on: dried paint stops flowing and stays
where it landed.

### 4. Wind + air drag (surface-gated)

```
velocity[i] += windForce * exposure[i] * dt          // wind pushes exposed paint
velocity[i] -= velocity[i] * airDrag * exposure[i] * dt  // air resistance on exposed paint
```

Bulk paint (`exposure 0`) feels neither. The PDF lists air resistance and wind among the environment
factors, and they cost almost nothing on top of the exposure machinery.

---

## Parameters (Inspector)

| Field | Meaning |
|---|---|
| `ambientTemperature` (°C) | warmer = thinner paint + faster drying |
| `humidity` (0–1) | humid air dries paint slowly; dry air fast |
| `tempViscosityFactor` | strength of the temperature→viscosity effect (0 = off) |
| `enableDrying` | master toggle for drying/curing |
| `dryingRate` | base curing speed for fully-exposed paint in hot, dry air |
| `setWetnessThreshold` | wetness below which paint "sets" and freezes |
| `airExposureThreshold` | density fraction above which paint counts as bulk (shielded) |
| `enableAirEffects` | master toggle for wind + drag |
| `windForce` (Vector3) | constant wind on exposed paint |
| `airDrag` | air resistance on exposed paint |

---

## How to verify (≈2000 particles, CPU)

1. **Temperature → viscosity:** raise `ambientTemperature` → the fluid visibly thins and splashes;
   lower it → it crawls. (Affects the whole body.)
2. **Drying is surface-only:** set low `humidity` and high `ambientTemperature`. The **top surface**
   gradually sets/stops while the **bulk underneath stays wet and mobile**. Tilt/slosh and watch the
   set crust ride on wet paint.
3. **Volume effect:** compare a small puddle vs a deep pool — the small one dries through much
   faster. (No code special-cases this; it emerges from exposure.)
4. **Humidity:** crank `humidity` to ~1 → drying nearly stops even when hot.
5. **Air motion is surface-only:** set a `windForce` → only the surface/airborne particles drift
   with it; the bulk is unaffected. Toggle `enableAirEffects` / `enableDrying` off to A/B compare.

---

## Where this fits

- Implemented entirely on the CPU 3D solver so it's easy to tune and debug (it's count-insensitive).
- It reuses the existing `densities[]` — no new neighbour search, so it stays cheap.
- The "extra per-particle field (`wetness`) + a decay/exposure pass" pattern is the same shape that
  **color mixing** (a later phase) uses, so this doubles as a warm-up.
- These kernels get ported to the GPU compute version alongside the rest of the solver later.
