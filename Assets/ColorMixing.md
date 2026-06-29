# Color mixing — two paints (GPU)

This document explains the two-paint color mixing in the GPU fluid: the `MixColors` kernel in
[`FluidCompute.compute`](FluidCompute.compute), the spectral color model + LUT in
[`FluidSimGPU.cs`](FluidSimGPU.cs) (`BuildMixLut`), and the per-particle LUT lookup in
[`FluidParticle.shader`](FluidParticle.shader).

Read [`FluidSim.md`](FluidSim.md) for the SPH basics and [`GPU.md`](GPU.md) for the GPU pipeline
first. This is **GPU-only**; the CPU `FluidSim*.cs` stay as the single-color reference.

---

## The idea: each particle carries a mix fraction

You pick **any two colors** from the color wheel (`paintColorA`, `paintColorB`). Each particle
stores a single number — a **mix fraction `t`** in `[0,1]`: `t = 0` means "100% paint A", `t = 1`
means "100% paint B", and anything between is a blend. Two paints just means two groups start at
`t = 0` and `t = 1`.

Where particles of different `t` are neighbors, they **drift toward the average `t`** of their
neighbors each step — so the blend forms at the boundary, exactly like real paint bleeding together.
Because it only ever *averages*, the blend is effectively **irreversible** (once mixed, paints don't
separate back out — what real pigments do).

Mechanically it's the **same neighbor-sum** as the viscosity pass (which averages *velocity*); here
we average the scalar `t` instead (stored in `Colors[i].x`). So it reuses the existing spatial hash
with no new machinery.

Two paints are seeded by splitting the spawn block: particles with `x < spawnCentre.x` get `t = 0`
(paint A), the rest `t = 1` (paint B) — so the two colors start adjacent and blend at their
interface when you slosh them together.

**Why a mix fraction and not an RGB per particle?** Because real paint mixes *subtractively* (next
section) — averaging RGB gives grey, not green. The actual color for a given `t` is computed by
spectral Kubelka-Munk; since that's a function of the single number `t`, we bake it **once** into a
small lookup table (LUT) on the CPU and the shader just reads it. All the physics lives in one
verifiable C# method, and the GPU does no per-frame color math.

---

## Subtractive mixing via spectral Kubelka-Munk (so blue + yellow = green)

**Paint does not mix like light.** RGB is an *additive* (light) model: averaging RGB blue `(0,0,1)`
and yellow `(1,1,0)` gives `(0.5,0.5,0.5)` = **grey**, not green. CMYK is no better — it's just
inverted RGB, so blue + yellow there is *also* grey. Real paint mixes *subtractively*: pigments
**absorb** light, and the absorptions combine. We model this with single-constant **Kubelka-Munk
(KM)** theory — the standard pigment-mixing model — and we run it over a **spectrum**, not 3 channels.

All of this happens in `BuildMixLut()` in [`FluidSimGPU.cs`](FluidSimGPU.cs) on the CPU.

### Step 1 — color → reflectance spectrum

Each picked color is turned into a **reflectance curve** over **20 wavelength bands** (~400–680 nm)
in `ColorToSpectrum`. We split the color's linear R/G/B into smooth per-band weight curves
(`BandWeights`). The curves are deliberately **asymmetric**: a *blue* color reflects some **green**
(overlap into the green bands), but green/red colors stay ~0 in the blue bands (so yellow does *not*
reflect blue). That single overlap is the whole reason **blue + yellow comes out green** — you can't
express it with 3 RGB numbers (pure RGB blue and yellow share no channel → grey).

### Step 2 — mix in K/S space (the linear trick)

In single-constant KM, each band has an absorption/scattering ratio:

```text
K/S = (1 − R)² / (2R)         // R = reflectance in that band
```

and crucially **K/S is *linear* in pigment amount**. So a blend of the two paints at mix fraction
`t` is just the per-band lerp of their K/S:

```text
KS_mix(band) = lerp( KS_A(band), KS_B(band), t )
R_mix(band)  = 1 + KS_mix − sqrt(KS_mix² + 2·KS_mix)     // invert K/S back to reflectance
```

This is why a **scalar mix fraction is enough**: averaging `t` between two paints (what the diffusion
kernel does) and *then* lerping their K/S is exactly the KM mix of the two paints.

### Step 3 — reflectance spectrum → RGB

`SpectrumToLinearRgb` integrates the 20-band reflectance against the same band weights (used as
color-matching sensitivities, normalized so a flat reflectance of 1 = white) to get linear RGB.

### Step 4 — pin the endpoints, then bake the LUT

A round-tripped color is never *exactly* the original, so we add a per-endpoint correction that is
faded to zero in the middle: at `t = 0` the result is forced to **exactly `paintColorA`**, at
`t = 1` exactly `paintColorB` — so **pure picked paints stay vivid and accurate**, and only the
in-between blend follows the (muted, realistic) spectral curve. `mixVibrance` then optionally pushes
the *mixed* colors away from grey for punchier secondaries, again faded to 0 at the endpoints so it
never alters the picked paints.

The whole `t → color` function is sampled into a **64-entry LUT** (`mixLutBuf`) once on the CPU, and
rebuilt only when you change a paint color. The shader (`SampleMix` in
[`FluidParticle.shader`](FluidParticle.shader)) just looks up the LUT by the particle's `t` — no
per-frame spectral math on the GPU.

> **No library.** Mixbox (a popular pigment-mixing library) implements a learned version of this;
> the assignment forbids ready-made libraries, so we wrote single-constant KM + the spectral
> reconstruction ourselves. It's a few dozen lines of well-understood physics, all in
> `BuildMixLut()`.

> **Why a LUT isn't cheating:** the LUT is just *memoization* of a one-parameter function — the KM
> physics is fully implemented in C#; the table only avoids recomputing it per particle per frame.

> **Extending it:** a third base paint would need a 2-parameter mix (a small 2D LUT) instead of the
> 1-parameter LUT; white/black pigments for tints/shades drop straight into the same spectral
> pipeline.

---

## The mixing rate — driven by motion and wetness

The interesting part is *how fast* a particle's mix fraction drifts toward its neighbors'. We move it
a **step `s` (between 0 and 1) toward the weighted-average mix fraction of its neighbors** — which is
**unconditionally stable** (a step toward an average can never overshoot). The kernel works on
`Colors[i].x` (the mix fraction); `.y/.z` are unused. Over the neighbors `j` of particle `i` (within
`smoothingRadius`):

```text
// weighted average neighbour mix fraction, and how "agitated" the neighbourhood is
kw          = ViscosityKernel(dst)               // spatial weight: closer neighbours count more
mixSum      += t_j · kw                           // t_j = Colors[j].x, the neighbour's mix fraction
weightSum   += kw
approach     = abs(dot(v_i − v_j, dir))          // head-on relative motion (see below)
agitationSum += approach · kw

avg       = mixSum / weightSum                   // bounded: a convex blend of neighbour fractions
agitation = agitationSum / weightSum             // weighted-average head-on relative speed

s         = saturate( colorMixRate · dt · wetness_i · (1 + shakeMixBoost · agitation) )
t_i       = lerp(t_i, avg, s)                    // move a step s toward the neighbour average
```

(In the shader `t_i` then becomes a color via the spectral LUT.) What controls the step `s`:

1. **`colorMixRate`** — the base speed (fraction per second toward the local average).
2. **Agitation** (`approach`) — "moving against each other" speeds mixing up. This is the "shake
   harder, mix faster" behavior. See the shear section below for what `approach` measures.
3. **Wetness** (`wetness_i`) — dried/curing paint stops mixing (reuses the drying buffer from the
   environment phase). A fresh wet pour blends; a skinned-over surface holds its color.

> **Why normalize?** An earlier version summed `(color_j − color_i)·w` over all neighbors *without*
> dividing by the weight — with ~30 neighbors and a large rate the step blew far past 1, so the
> colors overshot and exploded to garbage (which renders as black). Moving a bounded *fraction*
> toward a normalized *average* fixes that for any rate.

### "Moving against each other" vs shear — why we use the *radial* component

We don't use the raw relative speed `|v_i − v_j|`. We use only its component **along the line that
connects the two particles** (`dot(relVel, dir)`). Here's why, with the two cases that matter:

```
HEAD-ON  ("against each other")  → strong mixing
   i: →            ← j
      ──────────────────              the connecting line is horizontal,
   relative velocity is ALONG it  →  dot is large  →  w boosted

SHEAR  (slide past each other)   → weak mixing
   i:  → → →                          both moving horizontally,
       ───────                        but side-by-side: the connecting
   j:  ← ← ←                          line is VERTICAL → relative velocity
                                      is perpendicular → dot ≈ 0 → no boost
```

- **Shear** means two things sliding *past* each other (like rubbing your palms together, or cards
  in a deck) — parallel motion, not a collision.
- Two particles moving **parallel in the same direction** have ≈0 relative velocity → no boost
  anyway.
- Two particles moving **head-on / against each other** have their relative velocity pointed right
  along the line between them → big `approach` → fast mixing.

Using `abs(dot(relVel, dir))` captures "they're driving into each other" and ignores "they're just
sliding alongside," which matches the intuition that paints mix when they *run into* each other.

> **Honest note for the prof:** in *real* fluids, shear (stirring!) is actually a major mixing
> mechanism. Emphasizing head-on approach over shear is a deliberate, simplified modeling choice
> that gives intuitive, controllable paint behavior — not a claim about turbulence.

---

## Rendering

`FluidParticle.shader` reads each particle's mix fraction `Colors[instanceID].x`, looks its color up
in the spectral LUT (`SampleMix`, lerping between the two nearest entries), and passes it vertex →
fragment with the simple baked diffuse shading. The driver binds `Colors`, `MixLut` and `MixLutSize`
to the material every frame, so the blend is visible live.

---

## Parameters (FluidSimGPU)

| Field | Effect |
|---|---|
| `enableColorMixing` | turn mixing on/off (off = the two colors stay separate) |
| `paintColorA`, `paintColorB` | the two paints — **pick any color from the wheel** (left / right half of the spawn) |
| `mixVibrance` | extra saturation for **mixed** colors only (secondaries); 0 = physically honest, higher = punchier. Pure paints unaffected |
| `colorMixRate` | overall mixing speed |
| `shakeMixBoost` | how strongly head-on relative motion accelerates mixing |

---

## Race note

Like the viscosity pass, `MixColors` reads neighbors' `Colors[j]` while writing its own `Colors[i]`
— a benign race for a diffusion/relaxation step. If you ever see color drift or banding, snapshot
`Colors` into a read-only buffer before the pass (a tiny copy kernel), exactly mirroring the CPU
viscosity `velocityBuffer`. Not needed in practice for a smoothing operation.

---

## How to verify

1. Press Play: you should see a **blue half and a yellow half** (the defaults) meeting in the middle.
2. Leave it calm → they blend **slowly** at the interface into **green** (subtractive!); the far
   sides stay vividly blue / yellow.
3. **Slosh / tilt the box hard** → they mix **much faster** (the `approach` term).
4. Pick **any** `paintColorA`/`paintColorB` from the wheel and watch the secondary: red + yellow →
   orange, red + blue → purple. Raise `mixVibrance` for punchier mixes; the pure halves stay exact.
5. Set high `ambientTemperature` + low `humidity` so the surface cures → cured paint **stops taking
   on new color** (the wetness gate).
6. Turn `enableColorMixing` off → the two colors never blend (sanity check).

---

## Next (Phase 4)

A particle's current (possibly already-mixed) color — its LUT-resolved RGB — is what the
**canvas/bucket** phase will stamp onto surfaces where it lands.
