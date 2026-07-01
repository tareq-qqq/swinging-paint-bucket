using UnityEngine;

/// Rope built from a Verlet particle chain.
public class RopeSystem : MonoBehaviour
{
    public enum RopeMaterial
    {
        MetalChain,
        HardRope,
        SoftRope,
        Rubber,
    }

    [System.Serializable]
    public struct MaterialProps
    {
        public string label;
        public float stiffness;
        public float damping;
    }

    private static readonly MaterialProps[] s_Materials = new MaterialProps[]
    {
        new MaterialProps
        {
            label = "Metal Chain",
            stiffness = 1.0f,
            damping = 0.9f,
        },
        new MaterialProps
        {
            label = "Hard Rope",
            stiffness = 0.8f,
            damping = 0.7f,
        },
        new MaterialProps
        {
            label = "Soft Rope",
            stiffness = 0.5f,
            damping = 0.4f,
        },
        new MaterialProps
        {
            label = "Rubber",
            stiffness = 0.1f,
            damping = 0.2f,
        },
    };

    [Header("Rope Setup")]
    [SerializeField]
    private int segmentCount = 12;

    [SerializeField]
    private float restLength = 5f;

    [SerializeField]
    private RopeMaterial material = RopeMaterial.HardRope;

    [SerializeField]
    private int constraintIter = 10;

    [Header("References")]
    [SerializeField]
    private Transform pivotAnchor;

    [SerializeField]
    private BucketSystem bucket;

    [Header("Visuals")]
    [SerializeField]
    private LineRenderer lineRenderer;

    private Vector3[] positions;
    private Vector3[] prevPositions;
    private float segRestLength;

    public float CurrentLength { get; private set; }
    public MaterialProps ActiveMaterial => s_Materials[(int)material];
    public float RestLength => restLength;

    void Start()
    {
        InitRope();
    }

    public void InitRope()
    {
        int n = segmentCount + 1; //because num of segs +1 = nodes
        positions = new Vector3[n];
        prevPositions = new Vector3[n];
        segRestLength = restLength / segmentCount;

        Vector3 pivot = pivotAnchor != null ? pivotAnchor.position : Vector3.up * restLength;
        //here i ini the rope down mean -1 on y in unity
        for (int i = 0; i < n; i++)
        {
            positions[i] = pivot + Vector3.down * (segRestLength * i);
            prevPositions[i] = positions[i];
        }

        if (lineRenderer != null)
            lineRenderer.positionCount = n;

        CurrentLength = restLength;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        MaterialProps mat = ActiveMaterial;

        //anckor on top
        if (pivotAnchor != null)
            positions[0] = pivotAnchor.position;

        //    x_next = 2*x_cur - x_prev + a*dt²
        // Shared gravity (same value the pendulum and paint use) if an EnvironmentConfig exists.
        Vector3 gravity =
            EnvironmentConfig.Instance != null
                ? EnvironmentConfig.Instance.GravityVector
                : new Vector3(0f, -9.81f, 0f);
        int n = positions.Length;

        for (int i = 1; i < n - 1; i++)
        {
            Vector3 vel = (positions[i] - prevPositions[i]) * mat.damping;
            Vector3 newPos = positions[i] + vel + gravity * (dt * dt);
            prevPositions[i] = positions[i];
            positions[i] = newPos;
        }
        //bucket last node
        if (bucket != null)
            positions[n - 1] = bucket.AttachPoint;

        //    F_stretch = k * (d - L0)

        float k = mat.stiffness;

        for (int iter = 0; iter < constraintIter; iter++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 delta = positions[i + 1] - positions[i];
                float dist = delta.magnitude;
                if (dist < 1e-6f)
                    continue;

                float diff = (dist - segRestLength) / dist;
                float share = 0.5f * k * diff;

                if (i > 0)
                    positions[i] += delta * share;
                positions[i + 1] -= delta * share;
            }

            if (pivotAnchor != null)
                positions[0] = pivotAnchor.position;
            if (bucket != null)
                positions[n - 1] = bucket.AttachPoint;
        }

        //len in real time
        float totalLen = 0f;
        for (int i = 0; i < n - 1; i++)
            totalLen += (positions[i + 1] - positions[i]).magnitude;
        CurrentLength = totalLen;

        if (lineRenderer != null)
            lineRenderer.SetPositions(positions);
        Debug.Log(CurrentLength);
    }

    public Vector3 GetNodePosition(int i) =>
        (positions != null && i < positions.Length) ? positions[i] : Vector3.zero;

    public void SetMaterial(RopeMaterial m)
    {
        material = m;
    }

    public string MaterialLabel => ActiveMaterial.label;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (positions == null)
            return;
        Gizmos.color = Color.yellow;
        for (int i = 0; i < positions.Length - 1; i++)
            Gizmos.DrawLine(positions[i], positions[i + 1]);
    }
#endif
}
