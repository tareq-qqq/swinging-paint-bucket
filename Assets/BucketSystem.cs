using UnityEngine;

public class BucketSystem : MonoBehaviour
{
    [Header("Bucket Properties")]
    [SerializeField]
    private float bucketMass = 0.5f;

    [Header("Bucket Geometry (for math containment)")]
    [SerializeField]
    private float bucketRadius = 2.5f;

    [SerializeField]
    private float bucketHeight = 4f;

    [SerializeField]
    private float bucketFloorY = -2f;

    [Tooltip(
        "Radius of the paint-exit hole in the bucket FLOOR (paint streams out here as it swings). 0 = sealed floor."
    )]
    [SerializeField]
    private float holeRadius = 0.4f;

    [Header("References")]
    [SerializeField]
    private PendulumPhysics pendulum;

    public float TotalMass => bucketMass;
    public Vector3 AttachPoint { get; private set; }

    // Geometry exposed so the fluid solver (FluidSimGPU) can use the SAME bucket as its
    // container — one authoritative set of inputs, no duplicated radius/height to keep in sync.
    public float Radius => bucketRadius;
    public float Height => bucketHeight;
    public float FloorY => bucketFloorY;
    public float HoleRadius => holeRadius;

    // Set from the setup UI (SimulationBootstrapper) before the sim starts. Floor tracks the height.
    public void Configure(float mass, float radius, float height, float hole)
    {
        bucketMass = mass;
        bucketRadius = radius;
        bucketHeight = height;
        bucketFloorY = -height * 0.5f;
        holeRadius = hole;
    }

    void Start() { }

    void FixedUpdate()
    {
        if (pendulum == null)
            return;

        transform.rotation = pendulum.BucketRotation;

        transform.position = pendulum.BucketPosition - transform.up * (bucketHeight * 0.5f);

        AttachPoint = pendulum.BucketPosition;
    }

    // Wireframe of the bucket the fluid actually collides with (cylinder + floor spill-hole),
    // drawn in the bucket's frame so you can see and position it. Visible in the Scene view always;
    // in the Game view, enable the "Gizmos" toggle (top-right of the Game view).
    void OnDrawGizmos()
    {
        // Match the collision: rotation+translation only (no scale).
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        float top = bucketFloorY + bucketHeight;
        Gizmos.color = new Color(0.3f, 0.8f, 1f); // cyan = the walls
        DrawCircleXZ(bucketFloorY, bucketRadius, 40);
        DrawCircleXZ(top, bucketRadius, 40);
        for (int i = 0; i < 12; i++)
        {
            float a = i / 12f * Mathf.PI * 2f;
            Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * bucketRadius;
            Gizmos.DrawLine(
                new Vector3(dir.x, bucketFloorY, dir.z),
                new Vector3(dir.x, top, dir.z)
            );
        }

        Gizmos.color = new Color(1f, 0.5f, 0.1f); // orange = the spill-hole in the floor
        DrawCircleXZ(bucketFloorY, holeRadius, 24);
    }

    static void DrawCircleXZ(float y, float radius, int segments)
    {
        Vector3 prev = new Vector3(radius, y, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            Vector3 cur = new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, cur);
            prev = cur;
        }
    }
}
