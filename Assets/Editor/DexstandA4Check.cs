using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Can the A4 be driven from the Wiehl junction to Engelskirchen? Trigger "a4check": every streamed tile near
/// the route is placed in the (unsaved) scene, then a ray goes down every ~10 m along both carriageways
/// (A:\blender_work\a4\a4_check_points.json, OSM, scene units). Reports points with no road under them and height
/// steps between neighbours — the gaps and seams a car would stop at. Tiles are taken out again afterwards.
/// Result in A:\blender_work\a4\a4_check.txt.
/// </summary>
public static class DexstandA4Check
{
    private const string Points = @"A:\blender_work\a4\a4_check_points.json";
    private const string Report = @"A:\blender_work\a4\a4_check.txt";

    [System.Serializable] private class Way { public long id; public string bridge; public float[] xz; }
    [System.Serializable] private class Runs { public Way[] runs; }
    [System.Serializable] private class TileEntry { public string name; public float x, y, z, r; }
    [System.Serializable] private class TileManifest { public TileEntry[] tiles; }

    public static void Run()
    {
        if (GameObject.Find("Map") == null)  // batch mode starts with an empty scene
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var runs = JsonUtility.FromJson<Runs>(File.ReadAllText(Points)).runs;
        var all = runs.SelectMany(r => Enumerable.Range(0, r.xz.Length / 2).Select(i => new Vector2(r.xz[2 * i], r.xz[2 * i + 1]))).ToList();
        var manifest = JsonUtility.FromJson<TileManifest>(Resources.Load<TextAsset>("Map/tiles_manifest").text);
        var map = GameObject.Find("Map");
        var placed = new List<GameObject>();
        foreach (var t in manifest.tiles)
        {
            var c = new Vector2(t.x, t.z);
            if (!all.Any(p => (p - c).sqrMagnitude < (t.r + 20f) * (t.r + 20f))) continue;
            var src = Resources.Load<GameObject>("Map/Tiles/" + t.name);
            if (src != null) placed.Add((GameObject)PrefabUtility.InstantiatePrefab(src, map.transform));
        }
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        int n = 0, gaps = 0, steps = 0;
        foreach (var run in runs)
        {
            float prev = float.NaN;
            Vector2 prevP = default;
            for (int i = 0; i < run.xz.Length / 2; i++)
            {
                var p = new Vector2(run.xz[2 * i], run.xz[2 * i + 1]);
                n++;
                var hits = Physics.RaycastAll(new Vector3(p.x, 3000f, p.y), Vector3.down, 6000f, ~0, QueryTriggerInteraction.Ignore);
                var road = hits.Where(h => h.collider.name.StartsWith("RoadsCol")).OrderByDescending(h => h.point.y).FirstOrDefault();
                if (road.collider == null)
                {
                    gaps++;
                    string under = hits.Length > 0 ? string.Join(",", hits.OrderByDescending(h => h.point.y).Take(2).Select(h => h.collider.name)) : "nothing";
                    sb.AppendLine($"GAP way {run.id} bridge={run.bridge} at ({p.x:F1}, {p.y:F1}): {under}");
                    prev = float.NaN;
                    continue;
                }
                float y = road.point.y;
                // 10 m apart a real gradient moves under 0.2 units (6 %); more is a seam or a step
                if (!float.IsNaN(prev) && Mathf.Abs(y - prev) > 0.35f && (p - prevP).magnitude < 5f)
                {
                    steps++;
                    sb.AppendLine($"STEP way {run.id} at ({p.x:F1}, {p.y:F1}): {prev:F2} -> {y:F2} on {road.collider.name}");
                }
                prev = y;
                prevP = p;
            }
        }
        foreach (var go in placed) Object.DestroyImmediate(go);
        string head = $"A4 check: {n} points, {gaps} without road, {steps} height steps, {placed.Count} tiles";
        File.WriteAllText(Report, head + "\n" + sb);
        Debug.Log("[A4Check] " + head);
    }
}
