using UnityEngine;
using UnityEngine.InputSystem; // new Input System (matches the fluid sim)

public class SimulationController : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField]
    private PendulumPhysics pendulum;

    [SerializeField]
    private RopeSystem rope;

    [SerializeField]
    private BucketSystem bucket;

    [SerializeField]
    private Transform pivotAnchor;

    [Header("Pivot")]
    [SerializeField]
    private Vector3 pivotWorldPos = new Vector3(0f, 12f, 0f);

    [Header("Kick Strength")]
    [SerializeField]
    private float kickThetaStrength = 0.5f;

    [SerializeField]
    private float kickPhiStrength = 1.2f;

    private string _guiText = "Keys: 1=MetalChain 2=HardRope 3=SoftRope 4=Rubber  Space=Kick";

    void Start()
    {
        if (pivotAnchor != null)
            pivotAnchor.position = pivotWorldPos;
        if (pendulum != null)
            pendulum.PivotPosition = pivotWorldPos;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null)
            return;

        if (kb.digit1Key.wasPressedThisFrame)
            SetMaterial(RopeSystem.RopeMaterial.MetalChain);
        if (kb.digit2Key.wasPressedThisFrame)
            SetMaterial(RopeSystem.RopeMaterial.HardRope);
        if (kb.digit3Key.wasPressedThisFrame)
            SetMaterial(RopeSystem.RopeMaterial.SoftRope);
        if (kb.digit4Key.wasPressedThisFrame)
            SetMaterial(RopeSystem.RopeMaterial.Rubber);
        if (kb.spaceKey.wasPressedThisFrame)
            OnKick();
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 120, 500, 40));
        GUILayout.Label(_guiText);
        GUILayout.EndArea();
    }

    void SetMaterial(RopeSystem.RopeMaterial m)
    {
        rope?.SetMaterial(m);
        _guiText = "Rope: " + m + "   Space=Kick  1-4=material";
    }

    void OnKick()
    {
        pendulum?.ApplyInitialKick(kickThetaStrength, kickPhiStrength);
    }

    // Set from the setup UI (SimulationBootstrapper) before the sim starts.
    public void Configure(Vector3 pivot, float kickTheta, float kickPhi)
    {
        pivotWorldPos = pivot;
        kickThetaStrength = kickTheta;
        kickPhiStrength = kickPhi;
    }

    public void SetMaterialIndex(int i) =>
        SetMaterial((RopeSystem.RopeMaterial)Mathf.Clamp(i, 0, 3));
}
