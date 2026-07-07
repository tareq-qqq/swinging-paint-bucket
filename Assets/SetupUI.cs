using UnityEngine;
using UnityEngine.UI;

// =====================================================================================
//  SetupUI — the pre-run configuration screen (built entirely in code via UIBuilder).
// =====================================================================================
//  Shows every user input: the assignment's REQUIRED inputs as primary sections, and the
//  many fine-tuning knobs under a collapsible "Advanced" panel. Each control is bound to a
//  SimulationSettings field; the Run button hands off to SimulationBootstrapper. See SimulationUI.md.
// =====================================================================================
public class SetupUI : MonoBehaviour
{
    Canvas canvas;

    public void Build(SimulationBootstrapper boot, SimulationSettings s)
    {
        canvas = UIBuilder.CreateCanvas("SetupCanvas", 100);
        var t = canvas.transform;

        // Full-screen dim background so the empty scene behind is hidden.
        UIBuilder.Fill(UIBuilder.Box(t, UIBuilder.Bg).rectTransform);

        // Title (top band).
        var title = UIBuilder.Label(t, "Swinging Paint Bucket - Setup", 26, TextAnchor.MiddleCenter);
        UIBuilder.Band(title.rectTransform, 44, top: true);

        // Run button (bottom band).
        var run = UIBuilder.Button(t, "RUN SIMULATION", UIBuilder.Accent, boot.Run, 54);
        UIBuilder.Band(run.GetComponent<RectTransform>(), 54, top: false);

        // Scrollable list of inputs — a centred column (readable width) between title and Run button.
        var mid = UIBuilder.Box(t, new Color(0f, 0f, 0f, 0.25f));
        UIBuilder.CenterFill(mid.rectTransform, 1120f, 92f, 78f);
        var content = UIBuilder.ScrollColumn(mid.transform);
        BuildRequired(content, s);
        BuildAdvanced(content, s);
    }

    public void Hide()
    {
        if (canvas != null)
            Destroy(canvas.gameObject);
    }

    // ---- Required (assignment) inputs ----
    static void BuildRequired(Transform c, SimulationSettings s)
    {
        UIBuilder.SectionHeader(c, "Paint");
        UIBuilder.SliderRow(c, "Amount of paint (bucket fill 0–1)", 0f, 1f, false, () => s.fillFraction, v => s.fillFraction = v);
        UIBuilder.SliderRow(c, "Viscosity", 0f, 1f, false, () => s.viscosityStrength, v => s.viscosityStrength = v);
        UIBuilder.SliderRow(c, "Colour-flow / mix speed", 0f, 5f, false, () => s.colorMixRate, v => s.colorMixRate = v);
        UIBuilder.ToggleRow(c, "Multi-colour mixing", () => s.enableColorMixing, v => s.enableColorMixing = v);
        UIBuilder.ColorRow(c, "Paint A", () => s.paintColorA, v => s.paintColorA = v);
        UIBuilder.ColorRow(c, "Paint B", () => s.paintColorB, v => s.paintColorB = v);

        UIBuilder.SectionHeader(c, "Bucket");
        UIBuilder.SliderRow(c, "Mass", 0.05f, 5f, false, () => s.bucketMass, v => s.bucketMass = v);
        UIBuilder.SliderRow(c, "Radius", 0.5f, 5f, false, () => s.bucketRadius, v => s.bucketRadius = v);
        UIBuilder.SliderRow(c, "Height", 1f, 8f, false, () => s.bucketHeight, v => s.bucketHeight = v);
        UIBuilder.SliderRow(c, "Exit-hole radius", 0f, 1.5f, false, () => s.holeRadius, v => s.holeRadius = v);
        UIBuilder.ToggleRow(c, "Open top (paint can spill over rim)", () => s.openTop, v => s.openTop = v);

        UIBuilder.SectionHeader(c, "Rope & Pendulum");
        UIBuilder.SliderRow(c, "Rope length", 1f, 12f, false, () => s.ropeRestLength, v => s.ropeRestLength = v);
        UIBuilder.DropdownRow(c, "Rope material", new[] { "Metal Chain", "Hard Rope", "Soft Rope", "Rubber" }, () => s.ropeMaterial, v => s.ropeMaterial = v);
        UIBuilder.Vector3Row(c, "Pivot position", () => s.pivotWorldPos, v => s.pivotWorldPos = v);
        UIBuilder.SliderRow(c, "Initial angle (rad)", 0f, 1.5f, false, () => s.initialTheta, v => s.initialTheta = v);
        UIBuilder.SliderRow(c, "Initial tilt speed", -3f, 3f, false, () => s.initialThetaDot, v => s.initialThetaDot = v);
        UIBuilder.SliderRow(c, "Swing direction (rad)", 0f, 6.28f, false, () => s.initialPhi, v => s.initialPhi = v);
        UIBuilder.SliderRow(c, "Initial spin speed", -3f, 3f, false, () => s.initialPhiDot, v => s.initialPhiDot = v);
        UIBuilder.SliderRow(c, "Kick strength (tilt)", 0f, 3f, false, () => s.kickThetaStrength, v => s.kickThetaStrength = v);
        UIBuilder.SliderRow(c, "Kick strength (spin)", 0f, 3f, false, () => s.kickPhiStrength, v => s.kickPhiStrength = v);

        UIBuilder.SectionHeader(c, "Environment");
        UIBuilder.SliderRow(c, "Gravity", 0f, 25f, false, () => s.gravity, v => s.gravity = v);
        UIBuilder.SliderRow(c, "Air resistance", 0f, 1f, false, () => s.airResistance, v => s.airResistance = v);
        UIBuilder.SliderRow(c, "Humidity", 0f, 1f, false, () => s.humidity, v => s.humidity = v);
        UIBuilder.SliderRow(c, "Temperature (C)", -20f, 45f, false, () => s.ambientTemperature, v => s.ambientTemperature = v);
        UIBuilder.Vector3Row(c, "Wind", () => s.wind, v => s.wind = v);
        UIBuilder.SliderRow(c, "Surface friction", 0f, 1f, false, () => s.friction, v => s.friction = v);

        UIBuilder.SectionHeader(c, "Canvas");
        UIBuilder.DropdownRow(c, "Surface type", new[] { "Canvas", "Paper", "Wood", "Steel" }, () => s.surfaceType, v => s.surfaceType = v);
        UIBuilder.SliderRow(c, "Width", 2f, 30f, false, () => s.canvasWidth, v => s.canvasWidth = v);
        UIBuilder.SliderRow(c, "Height (depth)", 2f, 30f, false, () => s.canvasHeight, v => s.canvasHeight = v);
        UIBuilder.Vector3Row(c, "Orientation (euler °)", () => s.canvasEuler, v => s.canvasEuler = v);
        UIBuilder.SliderRow(c, "Resolution (deposit map)", 128f, 1024f, true, () => s.canvasResolution, v => s.canvasResolution = (int)v);
    }

    // ---- Advanced (collapsible) ----
    static void BuildAdvanced(Transform c, SimulationSettings s)
    {
        RectTransform adv = null;
        UIBuilder.Button(c, "+ Advanced (fine-tuning)", UIBuilder.Header,
            () => { if (adv != null) adv.gameObject.SetActive(!adv.gameObject.activeSelf); }, 34);

        adv = UIBuilder.Column(c, UIBuilder.Panel, new RectOffset(6, 6, 6, 6), 4);
        adv.gameObject.SetActive(false);
        var a = adv.transform;

        UIBuilder.SectionHeader(a, "SPH internals");
        UIBuilder.SliderRow(a, "Particle count (box demo)", 1000f, 60000f, true, () => s.particleCount, v => s.particleCount = (int)v);
        UIBuilder.SliderRow(a, "Particle spacing", 0.1f, 0.6f, false, () => s.particleSpacing, v => s.particleSpacing = v);
        UIBuilder.SliderRow(a, "Smoothing radius", 0.2f, 1.2f, false, () => s.smoothingRadius, v => s.smoothingRadius = v);
        UIBuilder.SliderRow(a, "Rest density", 1f, 40f, false, () => s.restDensity, v => s.restDensity = v);
        UIBuilder.ToggleRow(a, "Auto-calibrate rest density", () => s.autoCalibrateRestDensity, v => s.autoCalibrateRestDensity = v);
        UIBuilder.SliderRow(a, "Pressure multiplier", 10f, 600f, false, () => s.pressureMultiplier, v => s.pressureMultiplier = v);
        UIBuilder.SliderRow(a, "Near-pressure multiplier", 1f, 60f, false, () => s.nearPressureMultiplier, v => s.nearPressureMultiplier = v);
        UIBuilder.SliderRow(a, "Collision damping", 0f, 1f, false, () => s.collisionDamping, v => s.collisionDamping = v);
        UIBuilder.SliderRow(a, "Max speed clamp", 0f, 80f, false, () => s.maxSpeed, v => s.maxSpeed = v);

        UIBuilder.SectionHeader(a, "Stability");
        UIBuilder.SliderRow(a, "Stability CFL", 0.005f, 0.05f, false, () => s.stabilityCFL, v => s.stabilityCFL = v);
        UIBuilder.SliderRow(a, "Max substeps", 1f, 16f, true, () => s.maxSubsteps, v => s.maxSubsteps = (int)v);

        UIBuilder.SectionHeader(a, "Drying & air");
        UIBuilder.ToggleRow(a, "Enable drying", () => s.enableDrying, v => s.enableDrying = v);
        UIBuilder.SliderRow(a, "Drying rate", 0f, 0.5f, false, () => s.dryingRate, v => s.dryingRate = v);
        UIBuilder.SliderRow(a, "Set-wetness threshold", 0f, 1f, false, () => s.setWetnessThreshold, v => s.setWetnessThreshold = v);
        UIBuilder.SliderRow(a, "Temp-viscosity factor", 0f, 0.1f, false, () => s.tempViscosityFactor, v => s.tempViscosityFactor = v);
        UIBuilder.SliderRow(a, "Air-exposure threshold", 0f, 1f, false, () => s.airExposureThreshold, v => s.airExposureThreshold = v);
        UIBuilder.ToggleRow(a, "Enable air effects (wind/drag)", () => s.enableAirEffects, v => s.enableAirEffects = v);

        UIBuilder.SectionHeader(a, "Colour mixing");
        UIBuilder.SliderRow(a, "Mix vibrance", 0f, 1.5f, false, () => s.mixVibrance, v => s.mixVibrance = v);
        UIBuilder.SliderRow(a, "Shake mix boost", 0f, 2f, false, () => s.shakeMixBoost, v => s.shakeMixBoost = v);

        UIBuilder.SectionHeader(a, "Canvas contact physics");
        UIBuilder.SliderRow(a, "Absorbency", 0f, 1f, false, () => s.canvasAbsorbency, v => s.canvasAbsorbency = v);
        UIBuilder.SliderRow(a, "Wettability", 0f, 1f, false, () => s.canvasWettability, v => s.canvasWettability = v);
        UIBuilder.SliderRow(a, "Adhesion", 0f, 1f, false, () => s.canvasAdhesion, v => s.canvasAdhesion = v);
        UIBuilder.SliderRow(a, "Contact friction", 0f, 1f, false, () => s.canvasFriction, v => s.canvasFriction = v);
        UIBuilder.SliderRow(a, "Capillary strength", 0f, 8f, false, () => s.capillaryStrength, v => s.capillaryStrength = v);
        UIBuilder.SliderRow(a, "Viscous drag (absorb)", 0f, 8f, false, () => s.viscousDrag, v => s.viscousDrag = v);
        UIBuilder.SliderRow(a, "Saturation choke", 0f, 20f, false, () => s.saturationChoke, v => s.saturationChoke = v);
        UIBuilder.SliderRow(a, "Wet-film stain rate", 0f, 3f, false, () => s.filmRate, v => s.filmRate = v);
        UIBuilder.SliderRow(a, "Bounce", 0f, 1f, false, () => s.bounce, v => s.bounce = v);
        UIBuilder.SliderRow(a, "Contact-dry (evaporation)", 0f, 5f, false, () => s.contactDryRate, v => s.contactDryRate = v);
        UIBuilder.SliderRow(a, "Opacity buildup", 0.2f, 8f, false, () => s.opacityBuildup, v => s.opacityBuildup = v);
        UIBuilder.SliderRow(a, "Vividness", 1f, 2.5f, false, () => s.vividness, v => s.vividness = v);
        UIBuilder.SliderRow(a, "Brush radius", 0.05f, 1f, false, () => s.brushRadius, v => s.brushRadius = v);
        UIBuilder.SliderRow(a, "Dried-mark strength", 0f, 4f, false, () => s.driedMarkStrength, v => s.driedMarkStrength = v);
        UIBuilder.SliderRow(a, "Layer dry rate", 0f, 3f, false, () => s.layerDryRate, v => s.layerDryRate = v);
        UIBuilder.SliderRow(a, "Export resolution (PNG)", 256f, 4096f, true, () => s.exportResolution, v => s.exportResolution = (int)v);

        UIBuilder.SectionHeader(a, "Rendering & drag");
        UIBuilder.SliderRow(a, "Particle scale", 0.05f, 0.8f, false, () => s.particleScale, v => s.particleScale = v);
        UIBuilder.SliderRow(a, "Drag grab radius (px)", 40f, 300f, false, () => s.grabPixelRadius, v => s.grabPixelRadius = v);
        UIBuilder.SliderRow(a, "Drag turn speed (°/frame)", 1f, 20f, false, () => s.maxDegreesPerFrame, v => s.maxDegreesPerFrame = v);
        UIBuilder.SliderRow(a, "Throw strength", 0f, 1f, false, () => s.throwStrength, v => s.throwStrength = v);
    }
}
