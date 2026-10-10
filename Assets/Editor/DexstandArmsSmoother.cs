using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Rounds off the pack's blocky first-person arms. The mesh is split and
/// smoothed here rather than re-exported from Blender: the skeleton, the bind
/// poses and every clip stay exactly as the pack made them, and only the skin
/// the bones carry changes.
///
/// Each level splits every triangle in four at its edge midpoints, blending the
/// bone weights of the two ends; a Taubin pass then rounds the corners off
/// without shrinking the whole; the sleeves — whatever the upper and lower arm
/// bones carry — are puffed out a little; and the normals are made smooth.
/// Always built from the pack's original mesh, so running it twice does not
/// smooth twice.
/// </summary>
public static class DexstandArmsSmoother
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string SourcePath =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Art/Meshes/Character/SK_FP_CH_Default_Cubic.fbx";
    private const string OutputFolder = "Assets/Models/FPArms";

    private const int Levels = 2;
    private const int SmoothPasses = 8;

    /// <summary>How far the sleeves are pushed out, as a share of the mesh's size.</summary>
    private const float SleeveInflate = 0.006f;

    /// <summary>Share of the rounding the hand and fingers get.</summary>
    private const float HandStrength = 0.15f;

    [MenuItem("Dexstand/Сгладить руки")]
    public static void Smooth()
    {
        var sources = AssetDatabase.LoadAllAssetsAtPath(SourcePath).OfType<Mesh>().ToList();
        if (sources.Count == 0)
        {
            Debug.LogError($"[Arms] No mesh in {SourcePath}.");
            return;
        }

        if (!AssetDatabase.IsValidFolder(OutputFolder)) AssetDatabase.CreateFolder("Assets/Models", "FPArms");

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            int done = 0;
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.sharedMesh == null) continue;

                // The pack's own mesh, or a smoothed one made from it last time.
                string name = skin.sharedMesh.name.Replace("_Smooth", "");
                var source = sources.FirstOrDefault(m => m.name == name);
                if (source == null) continue;

                var boneNames = skin.bones.Select(b => b != null ? b.name.ToLowerInvariant() : "").ToArray();
                var smooth = Build(source, boneNames);

                string path = $"{OutputFolder}/{source.name}_Smooth.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(smooth, existing);
                    smooth = existing;
                }
                else
                {
                    AssetDatabase.CreateAsset(smooth, path);
                }

                skin.sharedMesh = smooth;
                skin.updateWhenOffscreen = true;
                EditorUtility.SetDirty(skin);
                done++;

                Debug.Log($"[Arms] {source.name}: {source.vertexCount} -> {smooth.vertexCount} vertices, " +
                          $"on {skin.name}.");
            }

            if (done == 0) Debug.LogWarning("[Arms] No renderer uses the pack's arms — nothing smoothed.");
            AssetDatabase.SaveAssets();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static Mesh Build(Mesh source, string[] boneNames)
    {
        var positions = new List<Vector3>(source.vertices);
        var uvs = new List<Vector2>(source.uv.Length == source.vertexCount ? source.uv : new Vector2[source.vertexCount]);
        var weights = new List<BoneWeight>(source.boneWeights);

        // Vertices that share a place are one point of the surface, whatever
        // their UVs; the smoothing moves them together so no seam opens.
        var weld = new List<int>();
        var byPlace = new Dictionary<Vector3Int, int>();
        foreach (var p in positions) weld.Add(Place(byPlace, p));

        var submeshes = new List<List<int>>();
        for (int s = 0; s < source.subMeshCount; s++) submeshes.Add(new List<int>(source.GetTriangles(s)));

        for (int level = 0; level < Levels; level++)
        {
            var midpoints = new Dictionary<long, int>();
            var placeMid = new Dictionary<long, int>();
            int placeCount = weld.Max() + 1;

            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midpoints.TryGetValue(key, out int found)) return found;

                int index = positions.Count;
                positions.Add((positions[a] + positions[b]) * 0.5f);
                uvs.Add((uvs[a] + uvs[b]) * 0.5f);
                weights.Add(Blend(weights[a], weights[b]));

                int pa = weld[a], pb = weld[b];
                long placeKey = pa < pb ? ((long)pa << 32) | (uint)pb : ((long)pb << 32) | (uint)pa;
                if (!placeMid.TryGetValue(placeKey, out int place))
                {
                    place = placeCount++;
                    placeMid[placeKey] = place;
                }
                weld.Add(place);

                midpoints[key] = index;
                return index;
            }

            for (int s = 0; s < submeshes.Count; s++)
            {
                var tris = submeshes[s];
                var split = new List<int>(tris.Count * 4);
                for (int t = 0; t < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    split.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
                }
                submeshes[s] = split;
            }
        }

        int places = weld.Max() + 1;
        var place = new Vector3[places];
        for (int i = 0; i < positions.Count; i++) place[weld[i]] = positions[i];

        // Neighbours on the welded surface, and which of them lie along an open edge.
        var neighbours = new HashSet<int>[places];
        for (int i = 0; i < places; i++) neighbours[i] = new HashSet<int>();
        var edgeUse = new Dictionary<long, int>();
        foreach (var tris in submeshes)
        {
            for (int t = 0; t < tris.Count; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[tris[t + e]], b = weld[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    neighbours[a].Add(b);
                    neighbours[b].Add(a);
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edgeUse[key] = edgeUse.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
        }

        var border = new HashSet<int>[places];
        foreach (var pair in edgeUse)
        {
            if (pair.Value != 1) continue;
            int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xFFFFFFFF);
            (border[a] ??= new HashSet<int>()).Add(b);
            (border[b] ??= new HashSet<int>()).Add(a);
        }

        // How hard each point is rounded. The hand is built from a separate
        // block per finger joint, and rounding each block on its own opened
        // gaps between them — the thumb came apart in three — so the hand only
        // softens; the sleeves take the full rounding.
        var dominant = new int[places];
        for (int i = 0; i < positions.Count; i++) dominant[weld[i]] = weights[i].boneIndex0;
        var strength = new float[places];
        for (int i = 0; i < places; i++)
        {
            int bone = dominant[i];
            string bn = bone >= 0 && bone < boneNames.Length ? boneNames[bone] : "";
            strength[i] = IsSleeve(bn) ? 1f : HandStrength;
        }

        // Taubin: a step in, then a slightly bigger step out, so corners round
        // off while the arm keeps its thickness.
        for (int pass = 0; pass < SmoothPasses; pass++)
        {
            Relax(place, neighbours, border, strength, 0.5f);
            Relax(place, neighbours, border, strength, -0.53f);
        }

        var normals = PlaceNormals(place, weld, submeshes);

        // Sleeves: points whose strongest bone is an upper or lower arm.
        var bounds = new Bounds(place[0], Vector3.zero);
        foreach (var p in place) bounds.Encapsulate(p);
        float push = bounds.size.magnitude * SleeveInflate;
        int puffed = 0;
        for (int i = 0; i < places; i++)
        {
            int bone = dominant[i];
            string bn = bone >= 0 && bone < boneNames.Length ? boneNames[bone] : "";
            if (!IsSleeve(bn)) continue;
            place[i] += normals[i] * push;
            puffed++;
        }
        Debug.Log($"[Arms] Sleeves: {puffed} of {places} points pushed out. Bones: " +
                  string.Join(", ", boneNames.Where(IsSleeve)));
        normals = PlaceNormals(place, weld, submeshes);

        var mesh = new Mesh
        {
            name = source.name + "_Smooth",
            indexFormat = positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };
        var finalPositions = new Vector3[positions.Count];
        var finalNormals = new Vector3[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            finalPositions[i] = place[weld[i]];
            finalNormals[i] = normals[weld[i]];
        }

        mesh.vertices = finalPositions;
        mesh.normals = finalNormals;
        mesh.uv = uvs.ToArray();
        mesh.boneWeights = weights.ToArray();
        mesh.bindposes = source.bindposes;
        mesh.subMeshCount = submeshes.Count;
        for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s);
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static bool IsSleeve(string bone) =>
        bone.Contains("arm") && !bone.Contains("hand") && !bone.Contains("finger") &&
        !bone.Contains("thumb") && !bone.Contains("index") && !bone.Contains("middle") &&
        !bone.Contains("ring") && !bone.Contains("pinky") && !bone.Contains("palm");

    private static int Place(Dictionary<Vector3Int, int> byPlace, Vector3 p)
    {
        var key = new Vector3Int(Mathf.RoundToInt(p.x * 100000f), Mathf.RoundToInt(p.y * 100000f),
                                 Mathf.RoundToInt(p.z * 100000f));
        if (!byPlace.TryGetValue(key, out int id))
        {
            id = byPlace.Count;
            byPlace[key] = id;
        }
        return id;
    }

    private static void Relax(Vector3[] place, HashSet<int>[] neighbours, HashSet<int>[] border,
                              float[] strength, float factor)
    {
        var next = (Vector3[])place.Clone();
        for (int i = 0; i < place.Length; i++)
        {
            // An open edge smooths only along itself, so it does not pull inwards.
            var around = border[i] ?? neighbours[i];
            if (around.Count == 0) continue;

            var mean = Vector3.zero;
            foreach (int n in around) mean += place[n];
            mean /= around.Count;
            next[i] = place[i] + (mean - place[i]) * factor * strength[i];
        }
        System.Array.Copy(next, place, place.Length);
    }

    private static Vector3[] PlaceNormals(Vector3[] place, List<int> weld, List<List<int>> submeshes)
    {
        var normals = new Vector3[place.Length];
        foreach (var tris in submeshes)
        {
            for (int t = 0; t < tris.Count; t += 3)
            {
                int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
                var n = Vector3.Cross(place[b] - place[a], place[c] - place[a]);
                normals[a] += n;
                normals[b] += n;
                normals[c] += n;
            }
        }
        for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].normalized;
        return normals;
    }

    /// <summary>Half and half of two vertices' bone weights, the four strongest kept.</summary>
    private static BoneWeight Blend(BoneWeight a, BoneWeight b)
    {
        var sum = new Dictionary<int, float>();
        void Add(int bone, float w)
        {
            if (w <= 0f) return;
            sum[bone] = (sum.TryGetValue(bone, out float s) ? s : 0f) + w * 0.5f;
        }
        Add(a.boneIndex0, a.weight0); Add(a.boneIndex1, a.weight1); Add(a.boneIndex2, a.weight2); Add(a.boneIndex3, a.weight3);
        Add(b.boneIndex0, b.weight0); Add(b.boneIndex1, b.weight1); Add(b.boneIndex2, b.weight2); Add(b.boneIndex3, b.weight3);

        var top = sum.OrderByDescending(p => p.Value).Take(4).ToArray();
        float total = top.Sum(p => p.Value);
        var result = new BoneWeight();
        if (top.Length > 0) { result.boneIndex0 = top[0].Key; result.weight0 = top[0].Value / total; }
        if (top.Length > 1) { result.boneIndex1 = top[1].Key; result.weight1 = top[1].Value / total; }
        if (top.Length > 2) { result.boneIndex2 = top[2].Key; result.weight2 = top[2].Value / total; }
        if (top.Length > 3) { result.boneIndex3 = top[3].Key; result.weight3 = top[3].Value / total; }
        return result;
    }
}
