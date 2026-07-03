# Surfaces — the canvas: collision, absorption, staining & drying (Phase 5)

The paint that streams out of the bucket has to land on something and **mark** it. This phase adds
a **canvas** the paint collides with, **soaks into**, **stains**, and **dries onto** — and the four
required canvas types (**Canvas / Paper / Wood / Steel**) all come out of **one** contact model with
different per-material coefficients.

Files: [`PaintCanvas.cs`](PaintCanvas.cs) (geometry + material + the deposit map + rendering),
[`PaintCanvasSurface.shader`](PaintCanvasSurface.shader) (draws the artwork), and the
`CanvasContact` kernel in [`FluidCompute.compute`](FluidCompute.compute) (the physics). It plugs into
[`FluidSimGPU.cs`](FluidSimGPU.cs) through **one optional `canvas` slot** — leave it empty and the
box/bucket demos are byte-for-byte unchanged, exactly like the bucket integration.

> **No Unity physics, no libraries.** The contact, the capillary absorption and the deposit are all
> hand-written in the compute kernel; Unity only draws the quad.

---

## The physics laws involved (what the teacher was pointing at)

Paint hitting a porous surface is a **wetting + capillary-absorption** problem. The standard laws:

| Law | What it governs | Where it is in the code |
|---|---|---|
| **Young's equation** (contact angle θ) | Does the paint **wet/spread** or **bead**? `γ_sv = γ_sl + γ_lv·cosθ` | `cosTheta = 2·wettability − 1`; drives the impact restitution |
| **Young–Laplace** (`ΔP = 2γ·cosθ / r`) | The **capillary suction** pulling paint into the pores (fine pores + wetting = strong pull) | `capPressure = canvasCapillary · wetting` |
| **Darcy's law** (`q = (k/μ)·ΔP`) | The **absorption flow rate** through the porous medium: permeability `k`, paint viscosity `μ` | `flux = absorbency · capPressure · viscFactor / (1 + saturation·localSat)` |
| **Lucas–Washburn** (penetration `L ∝ √t`) | Absorption is **fast then slows** as the wetted front deepens/saturates | emerges from Darcy: `flux` drops as `localSat` (accumulated coverage) grows |
| **Impulse / restitution** | The **collision** of the drop with the surface | `lv.y = −lv.y · collisionDamping · (1 − wetting)` |

The nice part: these compose. **Porosity** sets Darcy's permeability `k`; **wetting** (Young) sets the
sign and size of the Young–Laplace suction; the **paint's own viscosity** (the assignment's viscosity
input, `μ`) resists the Darcy flux so thick paint soaks in slowly; and **saturation** feeds back so a
patch fills up and then bleeds/pools — Washburn's `√t` slow-down — with no special-casing.

---

## The four coefficients (per material)

Each `PaintCanvas` carries four physically-named coefficients (0–1), set by a `SurfaceType` preset:

- **`absorbency`** — porosity / permeability `k` (Darcy). How readily paint soaks *in*.
- **`wettability`** — contact angle θ (Young). High = wets, spreads & develops capillary suction;
  low = spreads less (paint still sticks — it no longer *beads/bounces* in this model).
- **`adhesion`** — surface energy. A thin **film** that grips/stains even when nothing is absorbed.
- **`friction`** — roughness. Tangential grip that stops the paint sliding across the surface.

### Presets (the four required canvas types)

| Surface | absorbency | wettability | adhesion | friction | behaviour |
|---|---|---|---|---|---|
| **Canvas** | 0.70 | 0.85 | 0.85 | 0.70 | soaks in, spreads, grips, stains strongly |
| **Paper**  | 0.95 | 0.90 | 0.65 | 0.55 | soaks fastest, strong mark, saturates/bleeds |
| **Wood**   | 0.40 | 0.55 | 0.55 | 0.55 | partial soak, medium grip |
| **Steel**  | 0.02 | 0.60 | 0.35 | 0.45 | no soak — paint sticks as a film on top, dries by evaporation, marks moderately (weaker/removable) |

**Paint wets and sticks to every surface** — it's viscous paint, not water on Teflon. So paint
*splats and clings* on contact everywhere (see *Impact* below); surfaces differ by how much they
**absorb**, how strongly they **stain**, and how fast they **dry**, not by whether paint bounces off.
Steel is the low-absorbency case: `absorbency ≈ 0` so it soaks in ~nothing (paint stays a film on
top), it dries only by **evaporation** (contact-drying), and its lower `adhesion` leaves a moderate,
more *removable* mark. Paper is the opposite corner — soaks fastest and saturates/bleeds.

---

## The contact, step by step (`CanvasContact` kernel)

Runs once per substep **after** `UpdatePositions`, only when a canvas is assigned. Only paint that is
**free of the bucket** interacts (box demo: all paint; bucket demo: only *drained* `Released` paint —
the in-bucket paint is still held by the walls above).

1. **Locate.** World → canvas-local. The quad is the local **XZ** plane (normal = local **+Y**), so an
   un-rotated canvas is a horizontal table the drips fall onto; rotate the GameObject for an easel.
   Outside the quad footprint → the drip misses and keeps falling. Only act within a thin contact band
   around the surface.
2. **Contact angle (Young).** `cosTheta = 2·wettability − 1`; `wetting = saturate(cosTheta)`.
3. **Impact — splat & stick.** Put the particle on the surface. Paint barely bounces (it's viscous):
   the normal restitution is a small global `canvasBounce·(1 − wetting)`, so it splats and clings on
   *any* surface — no marble-ball skittering off steel. Roughness damps the tangential velocity
   (friction) so it doesn't slide off.
4. **Capillary absorption (Young–Laplace → Darcy → Washburn).** Read how saturated this texel already
   is (`localSat`), compute the suction `capPressure = canvasCapillary·wetting`, then the Darcy flux
   `absorbency·capPressure·viscFactor / (1 + saturationChoke·localSat)` where
   `viscFactor = 1/(1 + viscousDrag·effectiveViscosity)`. Subtract the absorbed liquid from the
   particle's **wetness** (reusing the drying field: liquid given up to the substrate = paint that
   sets). Then apply **contact-drying (evaporation)**: `wetness −= contactDryRate·(temp/40)·(1−humidity)·dt`
   — paint spread thin on a surface evaporates far faster than bulk, so even non-absorbent **steel
   sets in a few seconds** (then it stamps its dried mark and adheres). Canvas/paper dry by absorption
   *and* this; steel by evaporation only.
5. **Stain (deposit).** Add pigment to the canvas's **wet layer**: `absorbed` (what soaked in) plus a
   thin `adhesion·wetness·filmRate` film that grips on top even on non-absorbing surfaces. Dry paint
   adds nothing new, so a mark stops darkening once it has set; steel barely marks. The colour
   deposited is the particle's **displayed** colour — its mix fraction `t` through the same spectral
   Kubelka–Munk LUT the particles render with (see [`ColorMixing.md`](ColorMixing.md)), so the artwork
   matches the paint. (See *Layering* below for the wet/dry split.)
6. **Dry & adhere (any surface).** Once the paint dries in contact (`wetness ≤ setWetnessThreshold`)
   it leaves a **solid mark** of its colour on *any* surface (a bit lighter on low-adhesion surfaces
   like steel, scaled by `driedMarkStrength·(0.4 + 0.6·adhesion)`), and the particle is then flagged
   `Absorbed` — removed from the sim and hidden. Its colour now lives in the **deposit map**, which is
   stored in the canvas's **local** frame and drawn at the canvas transform, so the dried mark
   **rides the canvas** when it moves/tilts. **That is the adhesion** — dried paint travels with the
   surface. Still-**wet** paint that hasn't set slides off a moving surface (correct — it isn't stuck
   yet). This is also what makes dried **steel/wood** paint show up in the artwork/screenshot instead
   of only existing as loose 3D particles.

### The deposit map (the artwork)

A flat `resX·resY` grid of **9 uints per texel** — a **dry (committed) layer** `[0..2]=RGB, [3]=opacity`
and a **wet (active) layer** `[4..6]=RGB sum, [7]=weight, [8]=dryness`. Drips accumulate into the wet
layer with `InterlockedAdd` (fixed-point, `DEPOSIT_SCALE = 1024`) so thousands of particles over many
frames sum without races. Each drip stamps a **brush disc** (radius `brushRadius` ≈ the paint's
particle size) with a radial falloff, not a single texel — a particle is many texels wide, so stamping
one texel would leave a speckle of dots; the disc lays down **continuous strokes**.
[`PaintCanvasSurface.shader`](PaintCanvasSurface.shader) reads it back in the fragment stage and
composites **base → dry → wet** (source-over), with a hand-written **bilinear** 4-tap blend so the
texel grid reads smooth and a saturation (`_Vividness`) boost. No render texture / blit pass.

### Layering — wet-on-dry vs wet-on-wet (the `CanvasCommit` pass)

Real paint layers: fresh paint on a **dried** mark *covers* it (a new opaque top layer), but fresh
paint on **still-wet** paint *blends/bleeds*. A plain running average can't do this (it always
blends, which also muddies/darkens overlaps), so the deposit is split into the two layers above and a
per-texel **`CanvasCommit`** kernel (one thread per texel — no atomics needed) manages them:

- New drips always land in the **wet** layer and reset that texel's dryness → they blend with paint
  that's still wet there (**wet-on-wet**).
- Each frame `CanvasCommit` **ages** the wet layer's dryness at an **environment-driven** rate
  `dryBase · (0.3 + temp/40) · (1 − humidity) · (0.5 + absorbency)` — hot, dry, absorbent surfaces dry
  and cover sooner; humid air keeps layers wet and blending longer.
- When a texel's wet layer is **dry**, it is **source-over composited onto the dry layer** (the top
  colour wins, it does *not* average into what's below) and the wet layer is cleared. The next drips
  then start a fresh top layer → **wet-on-dry** covering, and colours stay clean instead of muddying.

---

## Scene setup

1. Add a **GameObject** for the canvas below the bucket (e.g. `y ≈ 0`, a horizontal table). Add a
   **`PaintCanvas`** component; pick a `SurfaceType`; size `width`/`height` to cover where the paint
   falls. An un-rotated canvas is horizontal; rotate it for an easel.
2. On the **paint** `FluidSimGPU`, drag the `PaintCanvas` into the **Canvas** slot. That's it — the
   paint that drains out the bucket hole now lands on it and paints.
3. Press Play, swing the bucket, watch the drips draw. **Right-click the `PaintCanvas` → Clear Canvas**
   to wipe between runs. Swap `SurfaceType` to compare Canvas/Paper/Wood/Steel.
4. **Save the artwork:** press the **Export key** (`P` by default) at run time, or **right-click the
   `PaintCanvas` → Export Canvas PNG**. It reads the deposit map back, renders it exactly like the
   on-screen canvas, and writes a timestamped PNG. The full path is logged to the Console; by default
   it goes to the app's persistent-data folder (set `saveFolder` on the component to choose your own,
   e.g. your Desktop). Works in the editor and in a build.

Works in the **box demo** too (no bucket): assign a canvas and any paint that reaches it marks it.

---

## Honest limitations (for the discussion)

- **Soaked-in particles are hidden, not compacted out** — an `Absorbed` particle still occupies a slot
  in the buffer (skipped by every kernel and collapsed to a zero-size sphere in the render), rather
  than being freed. Costs one early-out per skipped particle; a true free-list/compaction pass would
  reclaim the slot but isn't needed at these counts.
- **Wet-on-wet overlaps blend by weighted RGB average** (only *dried* layers are covered
  subtractively via the source-over commit). The *particles* mix subtractively (Kubelka–Munk) before
  they land, so each drop is already the right colour; only different-coloured drips landing on the
  *same still-wet texel* are the RGB-average approximation.
- The absorption constants are **lumped** (one `capillaryStrength` for `2γ/r`, etc.) rather than
  separate surface-tension and pore-radius inputs — defensible as a phenomenological model, and the
  knobs that matter (porosity, wetting, viscosity, saturation) are all explicit.
- The canvas is a **flat finite quad**; a curved or arbitrary-mesh surface would need an SDF, like the
  non-cylinder bucket note in [`BucketIntegration.md`](BucketIntegration.md).
```
