using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Real grass blades on the region map, only around the camera. The ground is cut into small chunks; each chunk
/// drops clumps onto the terrain collider ("GroundCol_*") by raycast — roads, pavements and buildings have
/// colliders of their own on top, so nothing grows through them — and remembers the orthophoto UV under each.
/// The blade shader (Dexstand/GrassBlades) reads the photo there and folds a clump away where the ground is not
/// green, and sways the tips in the wind. Drawn instanced from a buffer per chunk and photo: no GameObjects.
/// Sits on the map root; the scene builder adds it.
/// </summary>
public class MapGrass : MonoBehaviour
{
    [Tooltip("Scene units (a third of a metre each)")] public float radius = 10f;
    public float chunkSize = 4f;
    [Tooltip("Clumps per square scene unit")] public float density = 11f;
    public float mobileRadius = 8f;
    public float mobileDensity = 8f;
    public int chunksPerFrame = 1;  // each is a few hundred raycasts; two a frame stuttered while driving
    public Material material;

    private struct Blade { public Vector4 pos; public Vector4 data; }  // xyz + yaw, uv + size + tint

    private class Part { public Texture tex; public ComputeBuffer buf; public int count; }

    private class Chunk { public readonly List<Part> parts = new List<Part>(); public Bounds bounds; }

    private readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();

    /// <summary>Clumps standing now (for the map tour's check).</summary>
    public int Clumps { get { int n = 0; foreach (var c in chunks.Values) foreach (var p in c.parts) n += p.count; return n; } }
    private readonly Dictionary<string, Texture> groundTex = new Dictionary<string, Texture>();
    private Mesh clump;
    private MaterialPropertyBlock mpb;
    private float scale;

    private void Start()
    {
        if (material == null || !SystemInfo.supportsComputeShaders) { enabled = false; return; }
        if (Application.isMobilePlatform) { radius = mobileRadius; density = mobileDensity; }
        scale = transform.lossyScale.x;  // metres -> scene units
        foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
            if (r.name.StartsWith("Ground_") && r.sharedMaterial != null && r.sharedMaterial.HasProperty("_BaseMap"))
            {
                string cell = r.name.Substring("Ground_".Length);
                groundTex[cell] = r.sharedMaterial.GetTexture("_BaseMap");
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && FitUV(mf, out var fu, out var fv)) photoUV[cell] = (fu, fv);
            }
        clump = BuildClump();
        mpb = new MaterialPropertyBlock();
    }

    /// <summary>Seven thin blades, each one triangle leaning its own way, 1 m tall before scaling.</summary>
    private static Mesh BuildClump()
    {
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var tri = new List<int>();
        var rnd = new System.Random(7);
        for (int k = 0; k < 7; k++)
        {
            float a = (float)(rnd.NextDouble() * Mathf.PI * 2);
            float r = (float)rnd.NextDouble() * 0.12f;
            var c = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            float yaw = (float)(rnd.NextDouble() * Mathf.PI);
            var side = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw)) * 0.025f;
            var lean = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (0.15f + 0.2f * (float)rnd.NextDouble());
            float h = 0.6f + 0.4f * (float)rnd.NextDouble();
            int b = v.Count;
            v.Add(c - side); v.Add(c + side); v.Add(c + lean + Vector3.up * h);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(0.5f, 1));
            tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
        }
        var m = new Mesh { name = "GrassClump" };
        m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
        m.RecalculateNormals();
        m.bounds = new Bounds(Vector3.up * 0.5f, Vector3.one * 2f);
        return m;
    }

    private void Update()
    {
        Camera cam = null;  // the player's, or the car's chase camera while driving
        foreach (var cc in Camera.allCameras)
            if (cc.targetTexture == null && (cam == null || cc.depth > cam.depth)) cam = cc;
        if (cam == null || clump == null) return;
        var p = cam.transform.position;
        int reach = Mathf.CeilToInt(radius / chunkSize);
        var centre = new Vector2Int(Mathf.FloorToInt(p.x / chunkSize), Mathf.FloorToInt(p.z / chunkSize));

        // drop far chunks, build a few missing near ones, nearest first
        var drop = new List<Vector2Int>();
        foreach (var kv in chunks)
            if ((kv.Key - centre).sqrMagnitude > (reach + 1) * (reach + 1)) drop.Add(kv.Key);
        foreach (var k in drop) { Release(chunks[k]); chunks.Remove(k); }

        int built = 0;
        for (int ring = 0; ring <= reach && built < chunksPerFrame; ring++)
            for (int dx = -ring; dx <= ring && built < chunksPerFrame; dx++)
                for (int dz = -ring; dz <= ring && built < chunksPerFrame; dz++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring || dx * dx + dz * dz > reach * reach) continue;
                    var key = centre + new Vector2Int(dx, dz);
                    if (chunks.ContainsKey(key)) continue;
                    chunks[key] = BuildChunk(key);
                    built++;
                }

        mpb.SetFloat("_MetresPerUnit", 1f / scale);
        mpb.SetFloat("_FadeDistance", radius);
        foreach (var c in chunks.Values)
            foreach (var part in c.parts)
            {
                mpb.SetBuffer("_Blades", part.buf);
                mpb.SetTexture("_BaseMap", part.tex);
                Graphics.DrawMeshInstancedProcedural(clump, 0, material, c.bounds, part.count, mpb,
                    UnityEngine.Rendering.ShadowCastingMode.Off, true, gameObject.layer);
            }
    }

    // The terrain's collision meshes carry no UVs, so the photo UV under a blade comes from the drawn ground mesh
    // of its cell: the orthophoto lies flat on it, so uv is an affine function of world x, z (fitted from three
    // of its vertices far apart).
    private readonly Dictionary<string, (Vector3 u, Vector3 v)> photoUV = new Dictionary<string, (Vector3, Vector3)>();

    private static bool FitUV(MeshFilter mf, out Vector3 fu, out Vector3 fv)
    {
        fu = fv = Vector3.zero;
        var mesh = mf.sharedMesh;
        if (mesh == null || !mesh.isReadable) return false;
        var verts = mesh.vertices;
        var uvs = mesh.uv;
        if (verts.Length < 3 || uvs.Length != verts.Length) return false;
        var w = new Vector2[verts.Length];
        for (int i = 0; i < verts.Length; i++) { var p = mf.transform.TransformPoint(verts[i]); w[i] = new Vector2(p.x, p.z); }
        int a = 0, b = 0, c = 0;
        float best = -1f;
        for (int i = 0; i < w.Length; i++) { float d = (w[i] - w[a]).sqrMagnitude; if (d > best) { best = d; b = i; } }
        best = -1f;
        var ab = w[b] - w[a];
        for (int i = 0; i < w.Length; i++) { float d = Mathf.Abs(ab.x * (w[i].y - w[a].y) - ab.y * (w[i].x - w[a].x)); if (d > best) { best = d; c = i; } }
        // solve [x z 1] * (fu, fv) = (u, v) for the three points
        var m = new Matrix4x4(new Vector4(w[a].x, w[b].x, w[c].x, 0), new Vector4(w[a].y, w[b].y, w[c].y, 0),
                              new Vector4(1, 1, 1, 0), new Vector4(0, 0, 0, 1));
        if (Mathf.Abs(m.determinant) < 1e-6f) return false;
        var inv = m.inverse;
        var u = inv.MultiplyVector(new Vector3(uvs[a].x, uvs[b].x, uvs[c].x));
        var v = inv.MultiplyVector(new Vector3(uvs[a].y, uvs[b].y, uvs[c].y));
        fu = u; fv = v;
        return true;
    }

    private Chunk BuildChunk(Vector2Int key)
    {
        var chunk = new Chunk();
        var byTex = new Dictionary<Texture, List<Blade>>();
        var rnd = new System.Random(key.x * 73856093 ^ key.y * 19349663);
        int n = Mathf.RoundToInt(chunkSize * chunkSize * density);
        float x0 = key.x * chunkSize, z0 = key.y * chunkSize;
        float yMin = float.MaxValue, yMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float x = x0 + (float)rnd.NextDouble() * chunkSize, z = z0 + (float)rnd.NextDouble() * chunkSize;
            if (!Physics.Raycast(new Vector3(x, 10000f, z), Vector3.down, out var hit, 20000f)) continue;
            string name = hit.collider.name;
            if (!name.StartsWith("GroundCol_")) continue;  // a road, pavement, roof... is on top here
            string cell = name.Substring("GroundCol_".Length);
            if (cell.EndsWith("_col")) cell = cell.Substring(0, cell.Length - 4);
            if (!groundTex.TryGetValue(cell, out var tex) || !photoUV.TryGetValue(cell, out var fit)) continue;
            if (!byTex.TryGetValue(tex, out var list)) byTex[tex] = list = new List<Blade>();
            float size = (0.18f + 0.22f * (float)rnd.NextDouble()) * scale;  // 18-40 cm tall, in scene units
            var xz1 = new Vector3(hit.point.x, hit.point.z, 1f);
            list.Add(new Blade
            {
                pos = new Vector4(hit.point.x, hit.point.y, hit.point.z, (float)rnd.NextDouble() * 6.283f),
                data = new Vector4(Vector3.Dot(xz1, fit.u), Vector3.Dot(xz1, fit.v), size, (float)rnd.NextDouble())
            });
            yMin = Mathf.Min(yMin, hit.point.y); yMax = Mathf.Max(yMax, hit.point.y);
        }
        foreach (var kv in byTex)
        {
            var buf = new ComputeBuffer(kv.Value.Count, 32);
            buf.SetData(kv.Value);
            chunk.parts.Add(new Part { tex = kv.Key, buf = buf, count = kv.Value.Count });
        }
        if (yMin > yMax) yMin = yMax = 0;
        chunk.bounds = new Bounds(new Vector3(x0 + chunkSize / 2, (yMin + yMax) / 2, z0 + chunkSize / 2),
                                  new Vector3(chunkSize + 1, yMax - yMin + 2, chunkSize + 1));
        return chunk;
    }

    private static void Release(Chunk c)
    {
        foreach (var p in c.parts) p.buf?.Release();
        c.parts.Clear();
    }

    private void OnDisable()
    {
        foreach (var c in chunks.Values) Release(c);
        chunks.Clear();
    }
}
