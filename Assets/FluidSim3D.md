# FluidSim3D — 3D SPH Fluid (quick reference)

[`FluidSim3D.cs`](FluidSim3D.cs) is the **3D version** of the hand-written SPH fluid. It uses the
same from-scratch physics (gravity, density, pressure, viscosity, wall collisions — no Unity
physics) and the same spatial-hash optimisation as the 2D solver, now in full 3D with a container
box you can move **and** tilt with the transform gizmo.

This file is kept short on purpose:

- For the **SPH theory** (what density/pressure/viscosity/kernels are and why), read
  [`FluidSim.md`](FluidSim.md).
- For **what changed from 2D and why** (the kernel constants, 9→27 cells, local-frame collisions,
  etc.), read [`From2DTo3D.md`](From2DTo3D.md).

---

## How to run it

1. Open the project in **Unity 6** (`6000.3.15f1`).
2. In `SampleScene`, create an empty GameObject and add the **FluidSim3D** component. Leave it at the
   origin (or wherever you like — the container is centred on it).
3. Press **Play**. The existing scene camera is fine; you can also **pan/orbit in the Scene view** —
   the instanced particles render there too.
4. Particles fall under gravity and settle into a level pool at the bottom of the box (shown as a
   grey wire cube).

## Interacting with the fluid

- **Move the box:** select the GameObject and drag its **move handles** while playing — the walls
  follow and the fluid sloshes.
- **Tilt the box:** use the **rotate handles** — the box tilts and the fluid pours into the low
  corner (gravity stays world-down). This is the slosh/pour interaction that replaces the 2D mouse
  force.

## Key parameters (same meanings as 2D)

| Field | Effect |
|---|---|
| `particleCount` | number of particles (main performance lever in 3D) |
| `smoothingRadius` | interaction radius / smoothness |
| `viscosityStrength` | paint thickness (0 = watery, high = honey) |
| `restDensity` | target density (auto-calibrated on start by default) |
| `pressureMultiplier` / `nearPressureMultiplier` | incompressibility / anti-clumping |
| `gravity` | world-space gravity vector |
| `collisionDamping` | energy kept on a wall bounce |
| `boundsSize` | size of the container box (`Vector3`) |
| `iterationsPerFrame` | physics sub-steps (stability vs speed) |
| `paintColor`, `particleScale` | look of the paint |

`autoCalibrateRestDensity` (on) measures the spawn's natural density so the fluid starts in
equilibrium and stays stable out of the box. Right-click the component header → **Respawn** to
restart the layout without leaving Play mode.

## Defaults

`particleCount = 2000`, `smoothingRadius = 0.5`, `boundsSize = (8,8,8)`, `gravity = (0,-10,0)`,
`particleScale = 0.2`. Tune live in the Inspector.

## Performance note

3D scans **27** neighbour cells per particle (vs 9 in 2D), so expect the smooth-framerate ceiling to
be **below** the 2D ~3000. If it gets laggy, lower `particleCount` first, then `smoothingRadius`.

## Verify it works

- Particles settle into a **level 3D pool** without exploding outward or collapsing to a point
  (density + pressure + z-collision correct).
- **Tilt** the box → fluid pours to the low corner; **move** it → fluid sloshes and follows.
- Raise `viscosityStrength` → fluid thickens and moves as a blob; lower it → it splashes freely.

## Next steps (later)

Velocity-based colouring + a liquid surface shader, multiple paints + colour mixing, and coupling to
the swinging bucket (emit particles from the moving spout onto a canvas).
