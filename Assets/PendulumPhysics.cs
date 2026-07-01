using UnityEngine;

public class PendulumPhysics : MonoBehaviour
{
    [Header("Pendulum State")]
    public float theta;
    public float thetaDot;
    public float phi;
    public float phiDot;

    [Header("Initial Conditions")]
    [SerializeField]
    private float initialTheta = 0.25f;

    [SerializeField]
    private float initialThetaDot = 0f;

    [SerializeField]
    private float initialPhi = 0f;

    [SerializeField]
    private float initialPhiDot = 0f;

    [Header("Physics Parameters")]
    [SerializeField]
    private float gravity = 9.81f;

    [SerializeField]
    private float airResistanceB = 0.08f;

    [Header("References")]
    [SerializeField]
    private RopeSystem ropeSystem;

    [SerializeField]
    private BucketSystem bucketSystem;

    // Use the rope's REST length (fixed) for the swing, not its live Verlet-measured length —
    // the measured length jitters every frame and, fed back into the dynamics, made the bucket
    // visibly judder. The Verlet rope stays purely visual; the pendulum is a fixed-length swing.
    public float RopeLength => ropeSystem != null ? ropeSystem.RestLength : 2f;
    public float TotalEnergy { get; private set; }
    public float AngularMomentum { get; private set; }
    public Vector3 PivotPosition { get; set; }
    public Vector3 BucketPosition { get; private set; }
    public Quaternion BucketRotation { get; private set; }

    // When held (dragged by the mouse), the swing dynamics pause — theta/phi are set externally
    // by BucketDragHandler and the bucket simply follows the cursor; releasing resumes the swing.
    public bool IsHeld { get; set; }

    void Start()
    {
        theta = initialTheta;
        thetaDot = initialThetaDot;
        phi = initialPhi;
        phiDot = initialPhiDot;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float L = RopeLength; // fixed rest length (see RopeLength) — no jittery feedback
        // Skip the swing dynamics while the bucket is being dragged — theta/phi are driven by the
        // mouse instead. The pose below still runs, so the bucket follows the cursor.
        if (!IsHeld)
        {
            float m = bucketSystem != null ? bucketSystem.TotalMass : 1f;
            // Gravity + air resistance come from the shared EnvironmentConfig if one is present, so
            // the bucket, rope and paint all use the SAME values; otherwise fall back to local fields.
            var env = EnvironmentConfig.Instance;
            float b = env != null ? env.airResistance : airResistanceB;
            float g = env != null ? env.gravity : gravity;

            float sinT = Mathf.Sin(theta);
            float cosT = Mathf.Cos(theta);
            float sin2T = sinT * sinT;

            //  THETA
            float thetaDotDot = -(g / L) * sinT + sinT * cosT * phiDot * phiDot - b * thetaDot;

            //  PHI
            float phiDotDot = 0f;
            if (sin2T > 0.01f)
            {
                phiDotDot = (-b * phiDot - 2f * sinT * cosT * thetaDot * phiDot) / sin2T;
            }

            thetaDot += thetaDotDot * dt;
            theta += thetaDot * dt;
            phiDot += phiDotDot * dt;
            phi += phiDot * dt;

            //so when the theta pass 0 we flip the sign
            if (theta < 0f)
            {
                theta = -theta;
                thetaDot = -thetaDot;
                phi += Mathf.PI;
            }
            theta = Mathf.Min(theta, Mathf.PI - 0.01f);

            sin2T = Mathf.Sin(theta) * Mathf.Sin(theta);
            AngularMomentum = sin2T * phiDot;

            float tiltKE = 0.5f * m * L * L * thetaDot * thetaDot;
            float spinKE = 0.5f * m * L * L * sin2T * phiDot * phiDot;
            float PE = -m * g * L * cosT;
            TotalEnergy = tiltKE + spinKE + PE;
        }

        float sT = Mathf.Sin(theta);
        float cT = Mathf.Cos(theta);
        Vector3 offset = new Vector3(L * sT * Mathf.Cos(phi), -L * cT, L * sT * Mathf.Sin(phi));
        BucketPosition = PivotPosition + offset;

        Vector3 swingDir = new Vector3(Mathf.Cos(phi), 0f, Mathf.Sin(phi));
        if (swingDir.sqrMagnitude < 0.001f)
            swingDir = Vector3.forward;
        BucketRotation =
            Quaternion.LookRotation(-swingDir, Vector3.up)
            * Quaternion.Euler(theta * Mathf.Rad2Deg, 0f, 0f);
    }

    public void ApplyInitialKick(float kickTheta, float kickPhi)
    {
        thetaDot = kickTheta;
        phiDot = kickPhi;
    }
}
