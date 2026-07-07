using UnityEngine;
using UnityEngine.SceneManagement;

// =====================================================================================
//  SimulationBootstrapper — the hub that ties the setup UI to the simulation.
// =====================================================================================
//  Put this on ONE always-active GameObject (e.g. "SimulationManager") and assign the
//  "Simulation Root" — an INACTIVE parent GameObject holding every sim object (paint,
//  bucket, pendulum, rope, canvas, environment, drag handler, controller). Because the root
//  starts inactive, nothing runs on Play; the setup screen shows first. On Run we copy the
//  SimulationSettings into the components and activate the root (so they initialise with the
//  chosen values). Restart reloads the scene (settings persist via SimulationSettings.Current).
//
//  See SimulationUI.md for the one-time scene setup. No Unity physics — just wiring.
// =====================================================================================
public class SimulationBootstrapper : MonoBehaviour
{
    [Tooltip("INACTIVE parent GameObject holding all the simulation objects. Activated on Run.")]
    [SerializeField]
    private GameObject simulationRoot;

    [Header("References (auto-found under the root if left empty)")]
    [SerializeField] private FluidSimGPU paint;
    [SerializeField] private EnvironmentConfig environment;
    [SerializeField] private BucketSystem bucket;
    [SerializeField] private BucketContainer container;
    [SerializeField] private PendulumPhysics pendulum;
    [SerializeField] private RopeSystem rope;
    [SerializeField] private SimulationController controller;
    [SerializeField] private BucketDragHandler drag;
    [SerializeField] private PaintCanvas canvas;

    SetupUI setupUI;

    void Awake()
    {
        if (simulationRoot != null)
            simulationRoot.SetActive(false); // defer everything until Run

        FindRefs();

        setupUI = gameObject.AddComponent<SetupUI>();
        setupUI.Build(this, SimulationSettings.Current);
    }

    // FindObjectOfType(true) reaches components on the inactive Simulation root.
    void FindRefs()
    {
        if (paint == null) paint = FindObjectOfType<FluidSimGPU>(true);
        if (environment == null) environment = FindObjectOfType<EnvironmentConfig>(true);
        if (bucket == null) bucket = FindObjectOfType<BucketSystem>(true);
        if (container == null) container = FindObjectOfType<BucketContainer>(true);
        if (pendulum == null) pendulum = FindObjectOfType<PendulumPhysics>(true);
        if (rope == null) rope = FindObjectOfType<RopeSystem>(true);
        if (controller == null) controller = FindObjectOfType<SimulationController>(true);
        if (drag == null) drag = FindObjectOfType<BucketDragHandler>(true);
        if (canvas == null) canvas = FindObjectOfType<PaintCanvas>(true);
    }

    // Called by the setup UI's Run button.
    public void Run()
    {
        // Close the setup overlay FIRST so a click always dismisses it, even if applying throws.
        if (setupUI != null)
            setupUI.Hide();

        try
        {
            Apply(SimulationSettings.Current);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
        }

        if (simulationRoot != null)
            simulationRoot.SetActive(true); // components initialise now, with the applied values

        gameObject.AddComponent<RuntimeHUD>().Build(this, canvas);
    }

    // Called by the runtime HUD's Restart button — a clean reset; settings persist statically.
    public void Restart()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // Copy every setting into the components (while the root is still inactive, so their Start reads them).
    void Apply(SimulationSettings s)
    {
        if (environment != null)
        {
            environment.gravity = s.gravity;
            environment.airResistance = s.airResistance;
            environment.humidity = s.humidity;
            environment.ambientTemperature = s.ambientTemperature;
            environment.wind = s.wind;
            environment.friction = s.friction;
        }

        bucket?.Configure(s.bucketMass, s.bucketRadius, s.bucketHeight, s.holeRadius);
        container?.Configure(s.openTop, s.fillFraction);
        pendulum?.ConfigureInitial(s.initialTheta, s.initialThetaDot, s.initialPhi, s.initialPhiDot);
        rope?.SetRestLength(s.ropeRestLength);
        rope?.SetMaterial((RopeSystem.RopeMaterial)Mathf.Clamp(s.ropeMaterial, 0, 3));
        controller?.Configure(s.pivotWorldPos, s.kickThetaStrength, s.kickPhiStrength);
        drag?.Configure(s.grabPixelRadius, s.maxDegreesPerFrame, s.throwStrength);

        if (canvas != null)
        {
            canvas.SetSurfaceType(s.surfaceType); // preset first...
            // ...then the (optional) advanced overrides on top:
            canvas.absorbency = s.canvasAbsorbency;
            canvas.wettability = s.canvasWettability;
            canvas.adhesion = s.canvasAdhesion;
            canvas.friction = s.canvasFriction;
            canvas.width = s.canvasWidth;
            canvas.height = s.canvasHeight;
            canvas.resolution = s.canvasResolution;
            canvas.exportResolution = s.exportResolution;
            canvas.capillaryStrength = s.capillaryStrength;
            canvas.viscousDrag = s.viscousDrag;
            canvas.saturationChoke = s.saturationChoke;
            canvas.filmRate = s.filmRate;
            canvas.bounce = s.bounce;
            canvas.contactDryRate = s.contactDryRate;
            canvas.opacityBuildup = s.opacityBuildup;
            canvas.vividness = s.vividness;
            canvas.brushRadius = s.brushRadius;
            canvas.driedMarkStrength = s.driedMarkStrength;
            canvas.layerDryRate = s.layerDryRate;
            canvas.transform.eulerAngles = s.canvasEuler; // orientation
        }

        if (paint != null)
        {
            paint.particleCount = s.particleCount;
            paint.particleSpacing = s.particleSpacing;
            paint.smoothingRadius = s.smoothingRadius;
            paint.restDensity = s.restDensity;
            paint.autoCalibrateRestDensity = s.autoCalibrateRestDensity;
            paint.pressureMultiplier = s.pressureMultiplier;
            paint.nearPressureMultiplier = s.nearPressureMultiplier;
            paint.viscosityStrength = s.viscosityStrength;
            paint.collisionDamping = s.collisionDamping;
            paint.maxSpeed = s.maxSpeed;
            paint.stabilityCFL = s.stabilityCFL;
            paint.maxSubsteps = s.maxSubsteps;
            paint.enableDrying = s.enableDrying;
            paint.dryingRate = s.dryingRate;
            paint.setWetnessThreshold = s.setWetnessThreshold;
            paint.tempViscosityFactor = s.tempViscosityFactor;
            paint.airExposureThreshold = s.airExposureThreshold;
            paint.enableAirEffects = s.enableAirEffects;
            paint.enableColorMixing = s.enableColorMixing;
            paint.paintColorA = s.paintColorA;
            paint.paintColorB = s.paintColorB;
            paint.mixVibrance = s.mixVibrance;
            paint.colorMixRate = s.colorMixRate;
            paint.shakeMixBoost = s.shakeMixBoost;
            paint.particleScale = s.particleScale;
        }
    }
}
