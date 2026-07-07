# Simulation UI + cross-platform delivery

A **setup screen** (before the run) where the user sets **every** input we expose in the Inspector,
a **Run** button, and a runtime **HUD** (Screenshot + Restart) that keeps all the existing controls
(Space = kick, 1–4 = rope material, mouse-drag the bucket, `P` = save canvas). The whole UI is built
**in code** (uGUI) — no prefabs to wire — so you only add one manager object.

---

## Cross-platform — you do NOT need Windows Forms or a VM

**Unity itself is the cross-platform layer.** The UI is part of the Unity scene, and Unity compiles
the same project to a native **Windows `.exe`** and a **Linux** build.

- **Develop + test everything (sim *and* UI) in the Unity Editor on Linux.** No VM, ever.
- **Windows build (for the demo / your Windows teammates):** `File → Build Settings → PC, Mac & Linux
  Standalone → Target Platform: Windows`, **Scripting Backend = Mono** (Player Settings) → **Build**.
  Mono lets you build the Windows player *from Linux* — install **"Windows Build Support (Mono)"** in
  Unity Hub (per the editor version). Output is a `.exe` + a `_Data` folder; zip and share.
- Or a **Windows teammate** opens the same git repo and builds Windows natively.
- A **Linux** build is the same menu with the Linux target.

There is no Windows-Forms code and nothing OS-specific — the UI and sim are identical on both.

---

## One-time scene setup (do this once)

1. Create an empty GameObject **`Simulation`**. Drag **every simulation object under it**: the paint
   (`FluidSimGPU`), the bucket, the pendulum, the rope, the canvas (`PaintCanvas`), the
   `EnvironmentConfig`, the `BucketDragHandler`, and the `SimulationController`.
   - **Leave the Main Camera and any lights OUTSIDE** `Simulation` (they must stay active to render
     the setup screen).
   - **Untick the `Simulation` GameObject's active checkbox** (top-left of its Inspector) so it starts
     **inactive** — that's what makes the sim wait for Run.
2. Create an empty GameObject **`SimulationManager`** (leave it **active**). Add the
   **`SimulationBootstrapper`** component. Drag the `Simulation` GameObject into its **Simulation
   Root** slot. (The component refs auto-find under the root; you can also assign them explicitly.)
3. Press **Play**.

That's it — no Canvas or EventSystem to create; the code builds them.

---

## Using it

- **Play** → the **Setup** screen appears; the sim does **not** run yet.
- Set the **required** inputs (Paint, Bucket, Rope & Pendulum, Environment, Canvas). Expand
  **`+ Advanced (fine-tuning)`** for the SPH internals, stability, drying/air, colour-mixing, canvas
  contact-physics, rendering and drag-feel knobs.
- Hit **RUN SIMULATION** → the values are applied and the sim starts.
- During the run: **Space** kicks, **1–4** swap rope material, **drag** the bucket with the mouse,
  **Screenshot** (or `P`) exports the canvas PNG, **Restart** returns to the setup screen with your
  values retained.

---

## How it fits together

| Piece | Role |
|---|---|
| [`SimulationSettings.cs`](SimulationSettings.cs) | Plain data class holding all ~60 inputs; a static `Current` persists across a Restart. Defaults mirror the components, so "Run" unchanged = today's behaviour. |
| [`SimulationBootstrapper.cs`](SimulationBootstrapper.cs) | On one active manager object. Keeps the `Simulation` root inactive, shows the setup UI, and on **Run** copies the settings into the components (via their public setters) then activates the root. **Restart** reloads the scene. |
| [`SetupUI.cs`](SetupUI.cs) | Builds the setup screen — required sections + the Advanced collapsible — each control bound to a settings field. |
| [`RuntimeHUD.cs`](RuntimeHUD.cs) | The in-run overlay: Screenshot + Restart + a controls hint. |
| [`UIBuilder.cs`](UIBuilder.cs) | Code-only uGUI helper (canvas, sliders, toggles, dropdowns, colour rows, vector3 rows, scroll view) used by both screens. |

The components gained small public setters (`BucketSystem.Configure`, `PendulumPhysics.ConfigureInitial`,
`RopeSystem.SetRestLength`, `SimulationController.Configure`, `BucketContainer.Configure`,
`PaintCanvas.SetSurfaceType`, `BucketDragHandler.Configure`) so the bootstrapper can apply the chosen
values before the sim starts. No solver logic changed.

### Required inputs → the assignment's list
Paint amount/viscosity/colours/flow, bucket mass/radius/height/hole, rope length/material/pivot +
initial angle/speed/direction + kick, gravity/air-resistance/humidity/temperature/wind/friction, and
canvas type/size/orientation/resolution are all in the **required** sections; everything else is under
Advanced.

---

## Verify
- Play → setup screen; sim idle. Change bucket radius, paint colours, canvas type, a couple of Advanced
  values → **Run** → the sim starts and those values visibly apply.
- Space/1–4/drag/Screenshot/`P` all work; **Restart** returns to setup with values kept; re-run works.
- `File → Build Settings → Windows` (Mono) → `.exe` runs on Windows with identical UI; a Linux build
  runs too. No VM used.
