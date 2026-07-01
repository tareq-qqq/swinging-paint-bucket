using UnityEngine;

public class EnergyVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private PendulumPhysics pendulum;

    [SerializeField]
    private BucketSystem bucket;

    [SerializeField]
    private RopeSystem rope;

    [Header("Phase Thresholds")]
    [SerializeField]
    private float spinThreshold = 0.05f;

    [SerializeField]
    private float stopThreshold = 0.01f;

    private float peakEnergy;
    private const float g = 9.81f;

    private string _energyText = "";
    private string _momentumText = "";
    private string _materialText = "";
    private string _phaseText = "";

    void Update()
    {
        if (pendulum == null)
            return;

        float L = rope != null ? rope.CurrentLength : 2f;
        float m = bucket != null ? bucket.TotalMass : 1f;
        float sinT = Mathf.Sin(pendulum.theta);
        float cosT = Mathf.Cos(pendulum.theta);
        float sin2T = sinT * sinT;

        float tiltKE = 0.5f * m * L * L * pendulum.thetaDot * pendulum.thetaDot;
        float spinKE = 0.5f * m * L * L * sin2T * pendulum.phiDot * pendulum.phiDot;
        float PE = -m * g * L * cosT + m * g * L;
        float total = tiltKE + spinKE + PE;

        if (total > peakEnergy)
            peakEnergy = total;

        _energyText = string.Format(
            "E={0:F2}J  tiltKE={1:F2}  spinKE={2:F2}  PE={3:F2}",
            total,
            tiltKE,
            spinKE,
            PE
        );
        _momentumText = string.Format("Angular momentum = {0:F3}", pendulum.AngularMomentum);
        _materialText = rope != null ? "Rope: " + rope.MaterialLabel : "";

        float absPhiDot = Mathf.Abs(pendulum.phiDot);
        float absThetaDot = Mathf.Abs(pendulum.thetaDot);
        if (absPhiDot > spinThreshold)
            _phaseText = "Phase: 3D Spinning";
        else if (absThetaDot > stopThreshold)
            _phaseText = "Phase: 2D Swinging";
        else
            _phaseText = "Phase: Stopped";

        // Debug lines visible in Scene view
        Vector3 o = pendulum.BucketPosition + Vector3.up * 0.5f;
        float norm = peakEnergy > 0f ? peakEnergy : 1f;
        float s = 2f;
        Debug.DrawLine(o, o + Vector3.up * (tiltKE / norm * s), Color.red);
        Debug.DrawLine(
            o + Vector3.right * 0.1f,
            o + Vector3.right * 0.1f + Vector3.up * (spinKE / norm * s),
            Color.green
        );
        Debug.DrawLine(
            o + Vector3.right * 0.2f,
            o + Vector3.right * 0.2f + Vector3.up * (PE / norm * s),
            Color.cyan
        );
        Debug.DrawLine(
            o + Vector3.right * 0.3f,
            o + Vector3.right * 0.3f + Vector3.up * (total / norm * s),
            Color.white
        );
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 400, 100));
        GUILayout.Label(_energyText);
        GUILayout.Label(_momentumText);
        GUILayout.Label(_materialText);
        GUILayout.Label(_phaseText);
        GUILayout.EndArea();
    }
}
