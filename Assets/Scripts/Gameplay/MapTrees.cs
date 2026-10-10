using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The region's trees, GPU-instanced: a leafy tree and a spruce (trunk + cards, geometry from Blender's
/// export_region.py in Resources/Map/tree_templates.json) placed at every point of Resources/Map/trees.bytes
/// (Blender metres around the bus station, scale, turn, conifer flag). Baked into the tiles they were a gigabyte
/// of the APK; as points they are a few megabytes. Cells within reach of the camera are drawn, nearest detail
/// first. Sits on the map root; the scene builder adds it and its materials.
/// </summary>
public class MapTrees : MonoBehaviour
{
    public Material bark, leaves, needles;
    public float drawDistance = 110f;         // scene units, like MapStreamer's trees
    public float mobileDrawDistance = 80f;
    public float cell = 60f;

    [System.Serializable] private class Part { public float[] v; public int[] t; public float[] uv; }
    [System.Serializable] private class Templates { public Part trunk; public Part leaves; public Part fir; }

    private class Cell { public Bounds bounds; public readonly List<Matrix4x4>[] trees = { new List<Matrix4x4>(), new List<Matrix4x4>() }; public Matrix4x4[][] arrays; }

    private readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
    private readonly Mesh[] meshes = new Mesh[2];   // 0 leafy, 1 spruce; submesh 0 trunk, 1 foliage
    private readonly MaterialPropertyBlock[] crowns = new MaterialPropertyBlock[2];  // TreeLeaves' _Crown per kind
    private bool shadows = true;

    private void Start()
    {
        var data = Resources.Load<TextAsset>("Map/trees");
        var tpl = Resources.Load<TextAsset>("Map/tree_templates");
        if (data == null || tpl == null || bark == null || leaves == null || needles == null) { enabled = false; return; }
        if (Application.isMobilePlatform) { drawDistance = mobileDrawDistance; shadows = false; }
        var t = JsonUtility.FromJson<Templates>(tpl.text);
        meshes[0] = Combine("TreeLeafy", t.trunk, t.leaves);
        meshes[1] = Combine("TreeSpruce", t.trunk, t.fir);
        for (int k = 0; k < 2; k++)
        {
            var b = meshes[k].GetSubMesh(1).bounds;  // the foliage
            crowns[k] = new MaterialPropertyBlock();
            crowns[k].SetVector("_Crown", new Vector4(b.center.x, b.center.y, b.center.z, Mathf.Max(b.extents.x, b.extents.y, b.extents.z)));
        }

        float s = transform.lossyScale.x;
        var raw = data.bytes;
        var f = new float[raw.Length / 4];
        System.Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        for (int i = 0; i + 7 < f.Length; i += 8)
        {
            var p = new Vector3(-f[i], f[i + 2], -f[i + 1]) * s;
            var m = Matrix4x4.TRS(p, Quaternion.Euler(0f, -f[i + 6] * Mathf.Rad2Deg, 0f),
                                  new Vector3(f[i + 3], f[i + 5], f[i + 4]) * s);
            var key = new Vector2Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.z / cell));
            if (!cells.TryGetValue(key, out var c))
                cells[key] = c = new Cell { bounds = new Bounds(p, Vector3.zero) };
            c.trees[f[i + 7] > 0.5f ? 1 : 0].Add(m);
            c.bounds.Encapsulate(p);
        }
        foreach (var c in cells.Values)
        {
            c.bounds.Expand(new Vector3(12f, 20f, 12f) * s * 3f);
            c.arrays = new[] { c.trees[0].ToArray(), c.trees[1].ToArray() };
        }
    }

    /// <summary>Trunk and foliage as two submeshes, turned from Blender axes into Unity's (the mirror flips winding).</summary>
    private static Mesh Combine(string name, Part trunk, Part foliage)
    {
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var subs = new List<int>[] { new List<int>(), new List<int>() };
        int k = 0;
        foreach (var part in new[] { trunk, foliage })
        {
            int b = v.Count;
            for (int i = 0; i + 2 < part.v.Length; i += 3) v.Add(new Vector3(-part.v[i], part.v[i + 2], -part.v[i + 1]));
            for (int i = 0; i + 1 < part.uv.Length; i += 2) uv.Add(new Vector2(part.uv[i], part.uv[i + 1]));
            for (int i = 0; i + 2 < part.t.Length; i += 3)
            {
                subs[k].Add(b + part.t[i]); subs[k].Add(b + part.t[i + 2]); subs[k].Add(b + part.t[i + 1]);
            }
            k++;
        }
        var m = new Mesh { name = name, subMeshCount = 2 };
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetTriangles(subs[0], 0);
        m.SetTriangles(subs[1], 1);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private void Update()
    {
        Camera cam = null;  // the player's, or the car's chase camera while driving
        foreach (var cc in Camera.allCameras)
            if (cc.targetTexture == null && (cam == null || cc.depth > cam.depth)) cam = cc;
        if (cam == null) return;
        var p = cam.transform.position;
        float d2 = drawDistance * drawDistance;
        var shadowMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        foreach (var c in cells.Values)
        {
            if (c.bounds.SqrDistance(p) > d2) continue;
            for (int kind = 0; kind < 2; kind++)
            {
                var arr = c.arrays[kind];
                if (arr.Length == 0) continue;
                for (int sub = 0; sub < 2; sub++)
                {
                    var rp = new RenderParams(sub == 0 ? bark : kind == 0 ? leaves : needles)
                    {
                        worldBounds = c.bounds, shadowCastingMode = shadowMode, receiveShadows = true, layer = gameObject.layer,
                        matProps = sub == 1 ? crowns[kind] : null
                    };
                    for (int start = 0; start < arr.Length; start += 1023)
                        Graphics.RenderMeshInstanced(rp, meshes[kind], sub, arr, Mathf.Min(1023, arr.Length - start), start);
                }
            }
        }
    }
}
