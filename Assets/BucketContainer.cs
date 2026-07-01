using UnityEngine;

// =====================================================================================
//  BucketContainer — puts the GPU paint inside the swinging BUCKET (the bucket integration).
// =====================================================================================
//  This is the ONLY file that knows about the bucket. Assign it to a FluidSimGPU's
//  "Container override" slot and the paint becomes contained by the bucket — a CYLINDER
//  with solid walls, an OPEN TOP, and a circular SPILL HOLE in the floor that paint
//  streams out of as the bucket swings (the classic pendulum-painting mechanic).
//
//  Leave a FluidSimGPU's container slot EMPTY and it runs as the standalone transparent-box
//  demo, completely unchanged. So one solver gives both demos (with / without the bucket).
//
//  All geometry (radius, height, floor, hole) comes from the BucketSystem so the bucket's
//  inputs are authored in ONE place. No Unity physics — just geometry the solver reads.
// =====================================================================================
public class BucketContainer : MonoBehaviour
{
    [Tooltip("The bucket whose cavity contains the paint. Geometry (radius/height/hole) is read from it.")]
    [SerializeField]
    private BucketSystem bucket;

    [Tooltip("Open top: paint can climb out / be flung over the rim under hard swings. Off = sealed lid.")]
    [SerializeField]
    private bool openTop = true;

    [Range(0f, 1f)]
    [Tooltip(
        "Amount of paint as a FRACTION of the bucket's volume (0 = empty, 1 = full). The actual particle count is computed from the bucket's radius/height, so you never set it by hand."
    )]
    [SerializeField]
    private float fillFraction = 0.5f;

    // True only when this override is enabled AND a bucket is wired — FluidSimGPU checks this
    // before switching out of box mode, so a half-configured component falls back to the box.
    public bool Active => isActiveAndEnabled && bucket != null;

    // The bucket's transform IS the container frame (origin at the bucket centre; the pendulum
    // drives its pose), so the paint sloshes/tilts/drains exactly as the bucket swings.
    public Transform Pose => bucket.transform;

    public float Radius => bucket.Radius;
    public float Height => bucket.Height;
    public float FloorY => bucket.FloorY; // local Y of the floor (= -Height/2)
    public float HoleRadius => bucket.HoleRadius;
    public bool OpenTop => openTop;

    // How many particles fill 100% of the bucket at this spacing (≈ volume / spacing³).
    public int Capacity(float spacing)
    {
        float s = Mathf.Max(1e-4f, spacing);
        float volume = Mathf.PI * Radius * Radius * Height;
        return Mathf.Max(0, Mathf.FloorToInt(volume / (s * s * s)));
    }

    // Build the paint as a CYLINDER matching the bucket — radius = bucket radius, height set by the
    // fill fraction — sitting on the floor and centred on the axis, in the bucket's LOCAL frame.
    // The count comes from the bucket's volume × fillFraction, so the "amount of paint" is just the
    // slider. FluidSimGPU transforms these to world by the bucket pose (deferred to the first frame
    // so they land inside the bucket wherever the pendulum has placed it). Returns LOCAL positions.
    public Vector3[] BuildLocalSpawn(float spacing, float jitter)
    {
        float s = Mathf.Max(1e-3f, spacing);
        float rFill = Mathf.Max(0f, Radius - s * 0.5f); // keep particles off the wall
        float fillHeight = Mathf.Clamp01(fillFraction) * Height;
        float baseY = FloorY + s * 0.5f; // sit just above the floor
        int nr = Mathf.CeilToInt(rFill / s); // grid rings out from the axis
        int ny = Mathf.Max(0, Mathf.CeilToInt(fillHeight / s));
        float rSq = rFill * rFill;

        var pts = new System.Collections.Generic.List<Vector3>();
        for (int yi = 0; yi < ny; yi++)
        {
            float y = baseY + yi * s;
            if (y > FloorY + fillHeight)
                break;
            for (int xi = -nr; xi <= nr; xi++)
            for (int zi = -nr; zi <= nr; zi++)
            {
                float x = xi * s,
                    z = zi * s;
                if (x * x + z * z > rSq) // clip the square grid to the circle
                    continue;
                Vector3 j = Random.insideUnitSphere * jitter;
                pts.Add(new Vector3(x + j.x, y + j.y, z + j.z));
            }
        }
        return pts.ToArray();
    }
}
