# Fluid surface rendering — paint as a continuous surface (screen-space)

By default each particle is drawn as a shaded sphere ([`FluidParticle.shader`](FluidParticle.shader)).
This phase adds an optional **continuous fluid surface** using the standard **screen-space fluid
rendering** pipeline (Simon Green / NVIDIA, the same approach Sebastian Lague uses) — adapted for
**paint, not water**: opaque, with a **wetness-driven gloss** (glossy while wet → matte as it dries).

Files: [`FluidSurfaceRenderer.cs`](FluidSurfaceRenderer.cs) (the camera component that drives it) +
three hand-written shaders [`FluidImpostor.shader`](FluidImpostor.shader),
[`FluidBlur.shader`](FluidBlur.shader), [`FluidComposite.shader`](FluidComposite.shader). It reuses the
sim's existing GPU buffers via getters on [`FluidSimGPU.cs`](FluidSimGPU.cs) — **no new sim state**.

> **No library.** All three passes are hand-written shaders — the professor-friendly, "write it
> yourself" version of the technique. Built-in render pipeline (`OnRenderImage`).

---

## Why screen-space (not metaballs)

A true 3D iso-surface (marching cubes / raymarched metaballs) is expensive and scales with the volume.
**Screen-space** rendering works per *pixel*, so its cost is the screen resolution, not the particle
count — ideal for a weak GPU. The idea: you never build a mesh; you render the particles to a **depth
image**, **smooth** that image into a surface, and **shade** it. Three passes:

### Pass 1 — depth + colour ([`FluidImpostor.shader`](FluidImpostor.shader))
Each particle is drawn as a **sphere impostor**: a camera-facing quad (6 verts, drawn procedurally —
no mesh) whose fragment shader carves the quad into a sphere (`discard` outside the circle) and
computes the sphere's **front-surface eye-depth** analytically. Writing that depth to `SV_Depth` makes
the **nearest** particle win the depth test, so overlapping blobs resolve to the closest surface.
Outputs (MRT): **colour + wetness** and **linear eye-depth**. Colour is the same spectral
Kubelka–Munk LUT the spheres use (`MixLut[Colors[i].x]`); soaked-in (`Absorbed`) particles are hidden.

> The nearest-wins test is matched to the platform's **reversed-Z** convention from C#
> (`SystemInfo.usesReversedZBuffer` → `GEqual` + clear-to-0, else `LEqual` + clear-to-1); otherwise it
> would keep the *farthest* particle on D3D/Vulkan/Metal.

### Pass 2 — smooth the depth ([`FluidBlur.shader`](FluidBlur.shader))
The raw depth is bumpy (individual spheres). A **bilateral** (edge-preserving) blur — run separably,
horizontal then vertical — smooths it into a continuous surface. "Bilateral" = the blur weight falls
off with the **depth difference**, so it smooths *within* a blob but does **not** bleed across the
silhouette (the fluid edge stays crisp). `blurRadius` / `blurDepthFalloff` / `blurIterations` tune it.

### Pass 3 — normals + shading + composite ([`FluidComposite.shader`](FluidComposite.shader))
A fullscreen pass that:
- **Reconstructs eye-space position** from the smoothed depth (`ViewPos`, using the camera projection),
  and the **surface normal** from neighbouring depths (Green's *neighbour-min*: pick the smaller delta
  per axis so edges don't halo).
- **Shades the paint (opaque):** `diffuse = pigment·(ambient + N·L)`; a **specular** highlight scaled
  by **wetness** (`wetness · glossStrength · pow(N·H, glossPower)`) so wet paint is glossy and dried
  paint is matte; a subtle wetness-scaled **Fresnel** rim. No refraction / no transparency — it's paint.
- **Composites over the scene** where the fluid is **in front** of scene geometry (compares the fluid
  eye-depth to the scene's `_CameraDepthTexture`), so the canvas and bucket walls occlude it correctly.
  A 4-neighbour coverage term anti-aliases the silhouette.

---

## Setup

1. On the **Main Camera**, add a **`FluidSurfaceRenderer`** and assign the paint **`FluidSimGPU`**
   (auto-found if left empty).
2. On that `FluidSimGPU`, set **Render Mode = Surface** (it then stops drawing the spheres; the
   renderer draws the surface instead). Switch back to **Balls** any time for the raw-particle view.
3. Press Play. Tune on the renderer: `radiusScale` (bigger = blobs merge more), `blurRadius`
   (smoother surface), `glossStrength`/`glossPower` (wet sheen), `fresnelStrength`, `ambient`,
   `lightDirection`.

Works in the box and bucket demos alike. The camera needs `depthTextureMode = Depth` for scene
occlusion — the component sets this automatically.

---

## Paint vs water (the deliberate differences)

| | Water (Green/Lague) | **Paint (here)** |
|---|---|---|
| Body | transparent, refracts the background | **opaque** — hides what's behind |
| Colour | thin tint via Beer–Lambert over thickness | **pigment** colour (spectral LUT), diffuse-shaded |
| Surface | always glossy/reflective | **gloss coupled to wetness** — wet = glossy, dry = matte |
| Thickness pass | needed (for absorption) | not needed (opaque) |

## Limitations / notes
- **Depth-reconstructed normals** are screen-space, so at grazing silhouettes they can be slightly
  soft; the neighbour-min method keeps edges from haloing but very thin sheets may look faceted.
- The surface is a **single front layer** (nearest depth) — no internal structure or back faces
  (fine for opaque paint).
- Runs in `OnRenderImage` (**built-in RP only**), consistent with the rest of the project's shaders.
- Cost is ~1 geometry pass (cheap depth impostors) + 2 blur blits + 1 composite blit at screen
  resolution — comfortably interactive on the GTX 850M.
