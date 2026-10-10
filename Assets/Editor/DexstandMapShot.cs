using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Street-level photos of the imported map, for checking it without anyone at
/// the keyboard. Each line of MapShotTrigger.txt (next to the project) is
/// "name x z yaw pitch [height]" in scene units and degrees; it becomes a PNG
/// taken at eye height (or the given height) above the ground at x, z. Works in
/// edit and play mode and leaves the scene untouched.
/// </summary>
[InitializeOnLoad]
public static class DexstandMapShot
{
    private const string OutDir = @"A:\blender_work\unity_export\shots";
    private const int Width = 1280;
    private const int Height = 720;

    private static readonly string TriggerPath =
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "MapShotTrigger.txt"));

    private static double nextPoll;

    static DexstandMapShot()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 1.0;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!File.Exists(TriggerPath)) return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(TriggerPath);
            File.Delete(TriggerPath);
        }
        catch (IOException)
        {
            return; // still being written
        }

        Directory.CreateDirectory(OutDir);
        var go = new GameObject("MapShotCamera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 70f;
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 3000f;
        float eye = DexstandPlayerBuilder.PlayerHeight * 0.9f;
        var log = new StringBuilder();
        try
        {
            Physics.SyncTransforms();
            foreach (var line in lines)
            {
                var p = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 1 && p[0] == "mats")
                {
                    // How the map's cut-out materials actually came out of the import.
                    var seen = new System.Collections.Generic.HashSet<Material>();
                    foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    {
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null || !(m.name.Contains("Cutout") || m.name.Contains("Leaves")) || !seen.Add(m)) continue;
                            log.AppendLine($"{m.name}: surface {m.GetFloat("_Surface")} clip {m.GetFloat("_AlphaClip")} " +
                                           $"cull {m.GetFloat("_Cull")} queue {m.renderQueue} base {(m.GetTexture("_BaseMap") ? m.GetTexture("_BaseMap").name : "none")}");
                        }
                    }
                    var tree = GameObject.Find("Trees_05");
                    var tr = tree != null ? tree.GetComponent<Renderer>() : null;
                    log.AppendLine("Trees_05 materials: " + (tr == null ? "no renderer"
                        : string.Join(", ", System.Array.ConvertAll(tr.sharedMaterials, m => m == null ? "null" : m.name + "/" + m.shader.name))));
                    continue;
                }
                if (p.Length < 5) continue;
                float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                float x = F(1), z = F(2);
                float ground = Physics.Raycast(new Vector3(x, 1000f, z), Vector3.down, out var hit, 3000f) ? hit.point.y : 0f;
                float lift = p.Length > 5 ? F(5) : eye;
                go.transform.SetPositionAndRotation(new Vector3(x, ground + lift, z), Quaternion.Euler(F(4), F(3), 0f));
                Capture(cam, Path.Combine(OutDir, p[0] + ".png"));
                log.AppendLine($"{p[0]} ground {ground:F2} hit {(hit.collider != null ? hit.collider.name : "-")}");
            }
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
        File.WriteAllText(Path.Combine(OutDir, "shots.txt"), log.ToString());
    }

    private static void Capture(Camera cam, string path)
    {
        var rt = new RenderTexture(Width, Height, 24);
        var prevActive = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = prevActive;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }
}
