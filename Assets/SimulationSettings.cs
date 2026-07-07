using UnityEngine;

// =====================================================================================
//  SimulationSettings — the single source of truth for every user-facing input.
// =====================================================================================
//  The setup UI edits ONE of these; SimulationBootstrapper copies the values into the sim
//  components when the user hits Run. It's a plain class with a STATIC `Current` instance so
//  the values survive a Restart (which reloads the scene) within a play session.
//
//  Defaults mirror the components' current Inspector defaults, so "Run" with nothing changed
//  reproduces today's behaviour. No Unity physics here — it's just numbers.
// =====================================================================================
[System.Serializable]
public class SimulationSettings
{
    // Persists across a Restart (scene reload); statics live until the play session ends.
    static SimulationSettings s_current;
    public static SimulationSettings Current => s_current ??= new SimulationSettings();

    public static void Reset() => s_current = new SimulationSettings();

    // ---- Paint (FluidSimGPU) — required ----
    public float fillFraction = 0.5f;       // BucketContainer.fillFraction (amount of paint)
    public float viscosityStrength = 0.12f;
    public bool enableColorMixing = true;
    public Color paintColorA = new Color(0.10f, 0.20f, 0.85f);
    public Color paintColorB = new Color(0.95f, 0.85f, 0.10f);
    public float colorMixRate = 1f;          // colour-flow speed

    // ---- Bucket (BucketSystem / BucketContainer) — required ----
    public float bucketMass = 0.5f;
    public float bucketRadius = 2.5f;
    public float bucketHeight = 4f;
    public float holeRadius = 0.4f;          // paint-exit hole (diameter/2)
    public bool openTop = true;

    // ---- Rope & Pendulum (RopeSystem / PendulumPhysics / SimulationController) — required ----
    public float ropeRestLength = 5f;
    public int ropeMaterial = 1;             // 0 MetalChain, 1 HardRope, 2 SoftRope, 3 Rubber
    public Vector3 pivotWorldPos = new Vector3(0f, 12f, 0f);
    public float initialTheta = 0.25f;       // initial swing angle
    public float initialThetaDot = 0f;       // initial speed (tilt)
    public float initialPhi = 0f;            // swing direction
    public float initialPhiDot = 0f;         // initial spin speed
    public float kickThetaStrength = 0.5f;
    public float kickPhiStrength = 1.2f;

    // ---- Environment (EnvironmentConfig) — required ----
    public float gravity = 9.81f;
    public float airResistance = 0.08f;
    public float humidity = 0.5f;
    public float ambientTemperature = 20f;
    public Vector3 wind = Vector3.zero;
    public float friction = 0.5f;

    // ---- Canvas (PaintCanvas) — required ----
    public int surfaceType = 0;              // 0 Canvas, 1 Paper, 2 Wood, 3 Steel
    public float canvasWidth = 12f;
    public float canvasHeight = 12f;
    public Vector3 canvasEuler = Vector3.zero; // orientation (the canvas GameObject's rotation)
    public int canvasResolution = 384;

    // ---- Advanced: SPH internals (FluidSimGPU) ----
    public int particleCount = 20000;        // box demo only (bucket uses fillFraction)
    public float particleSpacing = 0.25f;
    public float smoothingRadius = 0.5f;
    public float restDensity = 12f;
    public bool autoCalibrateRestDensity = true;
    public float pressureMultiplier = 200f;
    public float nearPressureMultiplier = 12f;
    public float collisionDamping = 0.45f;
    public float maxSpeed = 25f;

    // ---- Advanced: stability ----
    public float stabilityCFL = 0.02f;
    public int maxSubsteps = 8;

    // ---- Advanced: drying / air ----
    public bool enableDrying = true;
    public float dryingRate = 0.05f;
    public float setWetnessThreshold = 0.15f;
    public float tempViscosityFactor = 0.03f;
    public float airExposureThreshold = 0.95f;
    public bool enableAirEffects = true;

    // ---- Advanced: colour mixing ----
    public float mixVibrance = 0.45f;
    public float shakeMixBoost = 0.5f;

    // ---- Advanced: canvas contact physics (PaintCanvas) ----
    public float canvasAbsorbency = 0.7f;
    public float canvasWettability = 0.85f;
    public float canvasAdhesion = 0.85f;
    public float canvasFriction = 0.7f;
    public float capillaryStrength = 3f;
    public float viscousDrag = 2f;
    public float saturationChoke = 6f;
    public float filmRate = 1f;
    public float bounce = 0.1f;
    public float contactDryRate = 1.5f;
    public float opacityBuildup = 1.5f;
    public float vividness = 1.6f;
    public float brushRadius = 0.35f;
    public float driedMarkStrength = 1.5f;
    public float layerDryRate = 0.6f;
    public int exportResolution = 2048;

    // ---- Advanced: rendering + drag feel ----
    public float particleScale = 0.2f;
    public float grabPixelRadius = 120f;
    public float maxDegreesPerFrame = 6f;
    public float throwStrength = 0.5f;
}
