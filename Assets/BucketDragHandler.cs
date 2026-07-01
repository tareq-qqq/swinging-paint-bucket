using UnityEngine;
using UnityEngine.InputSystem; // new Input System (matches the rest of the project)

// =====================================================================================
//  BucketDragHandler — grab the swinging bucket with the mouse, pull it around, let go.
// =====================================================================================
//  Click near the bucket to grab it; while held the bucket FOLLOWS the cursor along its
//  swing sphere (radius = rope length around the pivot) and the swing dynamics pause.
//  Release to let it swing again — the drag motion becomes an initial angular velocity so
//  you can "throw" it.
//
//  Two things make it feel right (and not explode):
//   - No teleport on grab: we remember the offset between the cursor's sphere point and the
//     bucket's current direction, so clicking the bucket doesn't snap it to the cursor.
//   - Speed-limited: the bucket can only turn so fast per frame, so a flick can't whip the
//     walls through the paint at huge speed (which is what was blowing the sim up).
//
//  Input only (no Unity physics): the new Input System mouse, a camera ray, and a screen-space
//  proximity test for the grab (the bucket has no collider, by design).
// =====================================================================================
public class BucketDragHandler : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private PendulumPhysics pendulum;

    [SerializeField]
    private BucketSystem bucket;

    [Tooltip("Camera used for the grab ray. Defaults to Camera.main.")]
    [SerializeField]
    private Camera cam;

    [Header("Feel")]
    [Tooltip("How close (in pixels) the cursor must be to the bucket to grab it.")]
    [SerializeField]
    private float grabPixelRadius = 120f;

    [Tooltip("Max degrees the bucket can swing toward the cursor per frame — lower = smoother / gentler on the paint.")]
    [SerializeField]
    private float maxDegreesPerFrame = 6f;

    [Tooltip("How much of the drag speed becomes swing speed on release (0 = drop, 1 = full throw).")]
    [Range(0f, 1f)]
    [SerializeField]
    private float throwStrength = 0.5f;

    private bool dragging;
    private Quaternion grabOffset = Quaternion.identity; // cursor-dir -> bucket-dir at grab (no snap)
    private float thetaRate; // rad/s during the drag, for the throw on release
    private float phiRate;

    void Awake()
    {
        // Auto-wire anything left empty so a missing reference doesn't silently disable dragging.
        if (pendulum == null)
            pendulum = FindObjectOfType<PendulumPhysics>();
        if (bucket == null)
            bucket = FindObjectOfType<BucketSystem>();
        if (cam == null)
            cam = Camera.main;
        if (cam == null)
            cam = FindObjectOfType<Camera>(); // no MainCamera tag — the usual "drag not working" cause
        if (cam == null)
            Debug.LogWarning("[BucketDragHandler] No camera found — can't drag the bucket.");
    }

    void Update()
    {
        if (pendulum == null || bucket == null || cam == null)
            return;

        var mouse = Mouse.current;
        if (mouse == null)
            return;

        if (!dragging)
        {
            if (mouse.leftButton.wasPressedThisFrame && CursorOnBucket(mouse))
                BeginDrag(mouse);
            return;
        }

        DragToCursor(mouse);
        if (mouse.leftButton.wasReleasedThisFrame || !mouse.leftButton.isPressed)
            Release();
    }

    // Screen-space proximity grab — no collider needed.
    bool CursorOnBucket(Mouse mouse)
    {
        Vector3 sp = cam.WorldToScreenPoint(bucket.transform.position);
        if (sp.z < 0f)
            return false; // behind the camera
        Vector2 m = mouse.position.ReadValue();
        return Vector2.Distance(new Vector2(sp.x, sp.y), m) <= grabPixelRadius;
    }

    void BeginDrag(Mouse mouse)
    {
        dragging = true;
        pendulum.IsHeld = true;
        thetaRate = 0f;
        phiRate = 0f;

        // Remember where the cursor's sphere point is RELATIVE to the bucket, so the grab doesn't
        // snap the bucket to the cursor — it stays put and then follows relative cursor motion.
        Vector3 hitDir = CursorSphereDir(mouse);
        Vector3 bucketDir = DirFromAngles(pendulum.theta, pendulum.phi);
        grabOffset = Quaternion.FromToRotation(hitDir, bucketDir);
    }

    // Move the bucket toward the cursor (offset-preserved, speed-limited), and set theta/phi.
    void DragToCursor(Mouse mouse)
    {
        Vector3 target = (grabOffset * CursorSphereDir(mouse)).normalized;
        Vector3 curDir = DirFromAngles(pendulum.theta, pendulum.phi);

        // Speed limit: only turn so far toward the target this frame (smooth + protects the paint).
        float maxStep = maxDegreesPerFrame * Mathf.Deg2Rad;
        Vector3 dir = Vector3.RotateTowards(curDir, target, maxStep, 0f).normalized;

        // Invert the pendulum's offset = (sinθ cosφ, -cosθ, sinθ sinφ).
        float newTheta = Mathf.Clamp(Mathf.Acos(Mathf.Clamp(-dir.y, -1f, 1f)), 0.001f, Mathf.PI - 0.01f);
        float newPhi = Mathf.Atan2(dir.z, dir.x);

        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        thetaRate = (newTheta - pendulum.theta) / dt;
        phiRate = WrapPi(newPhi - pendulum.phi) / dt;

        pendulum.theta = newTheta;
        pendulum.phi = newPhi;
        pendulum.thetaDot = 0f; // frozen while held; the throw is applied on release
        pendulum.phiDot = 0f;
    }

    void Release()
    {
        dragging = false;
        pendulum.thetaDot = thetaRate * throwStrength;
        pendulum.phiDot = phiRate * throwStrength;
        pendulum.IsHeld = false;
    }

    // Unit direction (pivot -> cursor's point on the swing sphere).
    Vector3 CursorSphereDir(Mouse mouse)
    {
        Vector3 pivot = pendulum.PivotPosition;
        float L = Mathf.Max(0.01f, pendulum.RopeLength);
        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        Vector3 hit = ClosestPointOnSphere(ray, pivot, L);
        Vector3 d = hit - pivot;
        return d.sqrMagnitude < 1e-8f ? Vector3.down : d.normalized;
    }

    // The pendulum's bucket direction (unit) for given angles: offset = L·(sinθcosφ, -cosθ, sinθsinφ).
    static Vector3 DirFromAngles(float theta, float phi)
    {
        float st = Mathf.Sin(theta);
        return new Vector3(st * Mathf.Cos(phi), -Mathf.Cos(theta), st * Mathf.Sin(phi));
    }

    // Nearest point on the sphere along the ray; if the ray misses, the sphere point closest to it.
    static Vector3 ClosestPointOnSphere(Ray ray, Vector3 centre, float radius)
    {
        Vector3 m = ray.origin - centre;
        float b = Vector3.Dot(m, ray.direction);
        float c = Vector3.Dot(m, m) - radius * radius;
        float disc = b * b - c;

        if (disc >= 0f)
        {
            float s = Mathf.Sqrt(disc);
            float t = -b - s;
            if (t < 0f)
                t = -b + s;
            if (t >= 0f)
                return ray.origin + ray.direction * t;
        }

        Vector3 closest = ray.origin + ray.direction * Mathf.Max(-b, 0f);
        Vector3 dir = closest - centre;
        if (dir.sqrMagnitude < 1e-8f)
            dir = -ray.direction;
        return centre + dir.normalized * radius;
    }

    // Wrap an angle (radians) to [-π, π] so the φ rate is correct across the atan2 seam.
    static float WrapPi(float a)
    {
        while (a > Mathf.PI)
            a -= 2f * Mathf.PI;
        while (a < -Mathf.PI)
            a += 2f * Mathf.PI;
        return a;
    }
}
