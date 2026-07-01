using UnityEngine;

// =====================================================================================
//  BucketVisual — draws the bucket as a transparent, open-top cylinder so you can see
//  the paint inside it (and where it is while it swings).
// =====================================================================================
//  Procedurally builds a side-wall-only cylinder mesh (no top/bottom caps) sized from the
//  BucketSystem geometry, and renders it each frame at the bucket's pose with a simple
//  transparent shader (Custom/BucketGlass). Nothing to model or scale by hand — it matches
//  the collision cylinder exactly.
//
//  Put this on any GameObject (e.g. the Bucket) and assign the BucketSystem. No Unity
//  physics — it's pure rendering.
// =====================================================================================
public class BucketVisual : MonoBehaviour
{
    [SerializeField]
    private BucketSystem bucket;

    [Tooltip("Wall tint + transparency (low alpha = barely-there glass).")]
    [SerializeField]
    private Color glassColor = new Color(0.55f, 0.8f, 1f, 0.16f);

    [Range(8, 96)]
    [SerializeField]
    private int segments = 48;

    private Mesh mesh;
    private Material material;
    private float builtRadius,
        builtFloorY,
        builtHeight;

    void Awake()
    {
        if (bucket == null)
            bucket = FindObjectOfType<BucketSystem>();
    }

    void OnDestroy()
    {
        if (mesh != null)
            Destroy(mesh);
        if (material != null)
            Destroy(material);
    }

    void LateUpdate()
    {
        if (bucket == null)
            return;

        // (Re)build the mesh when the geometry changes (also covers the first frame).
        if (
            mesh == null
            || !Mathf.Approximately(builtRadius, bucket.Radius)
            || !Mathf.Approximately(builtFloorY, bucket.FloorY)
            || !Mathf.Approximately(builtHeight, bucket.Height)
        )
            Rebuild();

        if (material != null)
            material.color = glassColor;

        // Draw at the bucket's pose (rotation + translation only, matching the collision).
        Matrix4x4 m = Matrix4x4.TRS(
            bucket.transform.position,
            bucket.transform.rotation,
            Vector3.one
        );
        var rp = new RenderParams(material)
        {
            worldBounds = new Bounds(bucket.transform.position, Vector3.one * 1000f),
        };
        Graphics.RenderMesh(rp, mesh, 0, m);
    }

    void Rebuild()
    {
        if (material == null)
        {
            Shader sh = Shader.Find("Custom/BucketGlass");
            material = new Material(sh);
        }

        builtRadius = bucket.Radius;
        builtFloorY = bucket.FloorY;
        builtHeight = bucket.Height;

        if (mesh == null)
            mesh = new Mesh { name = "BucketGlassCylinder" };
        else
            mesh.Clear();

        float topY = builtFloorY + builtHeight;
        int rings = segments;
        var verts = new Vector3[(rings + 1) * 2];
        var tris = new int[rings * 6];

        for (int i = 0; i <= rings; i++)
        {
            float a = i / (float)rings * Mathf.PI * 2f;
            float x = Mathf.Cos(a) * builtRadius;
            float z = Mathf.Sin(a) * builtRadius;
            verts[i * 2] = new Vector3(x, builtFloorY, z);
            verts[i * 2 + 1] = new Vector3(x, topY, z);
        }

        int t = 0;
        for (int i = 0; i < rings; i++)
        {
            int b0 = i * 2,
                t0 = i * 2 + 1,
                b1 = (i + 1) * 2,
                t1 = (i + 1) * 2 + 1;
            // Winding is irrelevant (the shader is Cull Off / double-sided).
            tris[t++] = b0;
            tris[t++] = t0;
            tris[t++] = b1;
            tris[t++] = b1;
            tris[t++] = t0;
            tris[t++] = t1;
        }

        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
    }
}
