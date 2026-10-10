using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bringing in a map made in Blender — here a Google Earth capture pulled in
/// with Blosm and reworked by hand.
///
/// Drop the FBX (and its textures) into <see cref="Folder"/>. The importer below
/// sets it up the way a level needs on first import; "mapaudit" reports what
/// will go wrong before anyone walks into it; "scene" then builds the level
/// from it instead of the old map.
/// </summary>
public class DexstandMapImport : AssetPostprocessor
{
    public const string Folder = "Assets/Maps/Import";

    /// <summary>
    /// How much the imported map is scaled in the scene. Blender exports real
    /// metres. The player is DexstandPlayerBuilder.PlayerHeight tall, so this
    /// ratio keeps real-world proportions: a 3 m doorway stays one and a half
    /// players high. Set it to 1 for a world three times larger than life.
    /// </summary>
    public static float Scale => DexstandPlayerBuilder.PlayerHeight / 1.8f;

    /// <summary>Meshes whose name ends like this are collision only: never drawn.</summary>
    private static readonly string[] CollisionSuffixes = { "_col", "_collider", "_collision" };

    private bool InFolder => assetPath.Replace('\\', '/').StartsWith(Folder + "/");

    // --- Import settings, applied the first time a file lands in the folder.

    private void OnPreprocessModel()
    {
        if (!InFolder) return;

        var importer = (ModelImporter)assetImporter;

        // Only on first import, so anything changed by hand in the inspector
        // afterwards sticks.
        if (!importer.importSettingsMissing) return;

        importer.globalScale = 1f;
        importer.useFileScale = true;

        // MeshColliders need the vertices on the CPU.
        importer.isReadable = true;

        // Blender scenes carry these; a level does not want them.
        importer.importCameras = false;
        importer.importLights = false;
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.None;

        // The pipeline-aware material path: it writes URP Lit materials from
        // what the FBX says each one is. The older path writes Standard-shader
        // materials, which render magenta under URP.
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        importer.materialLocation = ModelImporterMaterialLocation.InPrefab;

        // Geometry exactly as modelled: compression shifts vertices and opens
        // cracks between tiles of a photogrammetry capture.
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.optimizeMeshPolygons = true;
        importer.optimizeMeshVertices = true;

        // Blosm meshes easily pass 65k vertices; 16-bit indices would cut them.
        importer.indexFormat = ModelImporterIndexFormat.Auto;

        importer.importNormals = ModelImporterNormals.Import;
        importer.generateSecondaryUV = false;
    }

    private void OnPreprocessTexture()
    {
        if (!InFolder) return;

        var importer = (TextureImporter)assetImporter;
        if (!importer.importSettingsMissing) return;

        // Phones: 2048 is plenty for a building facade and a quarter of the
        // memory of 4096. Raise it per texture if a close-up needs it.
        importer.maxTextureSize = 2048;
        importer.mipmapEnabled = true;
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.anisoLevel = 4;
    }

    // --- The map in the folder.

    /// <summary>The newest model in the import folder, or null.</summary>
    public static string CurrentPath()
    {
        if (!AssetDatabase.IsValidFolder(Folder)) return null;

        return AssetDatabase.FindAssets("t:Model", new[] { Folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
            .FirstOrDefault();
    }

    public static bool IsCollision(Transform t)
    {
        string n = t.name.ToLowerInvariant();
        return CollisionSuffixes.Any(n.EndsWith);
    }

    /// <summary>
    /// Collision for the level. If the file carries meshes marked as collision
    /// (name ending "_col"), only those collide and they are hidden; otherwise
    /// every visible mesh gets a MeshCollider of its own shape.
    /// </summary>
    public static int AddColliders(GameObject map)
    {
        var filters = map.GetComponentsInChildren<MeshFilter>(true)
                         .Where(f => f.sharedMesh != null).ToArray();
        bool dedicated = filters.Any(f => IsCollision(f.transform));

        int added = 0;
        foreach (var f in filters)
        {
            bool collision = IsCollision(f.transform);
            if (dedicated && !collision) continue;

            if (collision)
            {
                var r = f.GetComponent<Renderer>();
                if (r != null) r.enabled = false;
            }

            if (f.GetComponent<Collider>() != null) continue;
            var mc = f.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = f.sharedMesh;
            added++;
        }

        return added;
    }

    // --- The report.

    [MenuItem("Dexstand/Проверить карту")]
    public static void Audit()
    {
        var log = new StringBuilder();
        var path = CurrentPath();
        if (path == null)
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            Report($"No map yet. Put the FBX and its textures into {Folder}.");
            return;
        }

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var map = Object.Instantiate(asset);
        try
        {
            log.AppendLine($"MAP: {path} ({new FileInfo(path).Length / 1024 / 1024} MB)");

            var transforms = map.GetComponentsInChildren<Transform>(true);
            var renderers = map.GetComponentsInChildren<Renderer>(true);
            var filters = map.GetComponentsInChildren<MeshFilter>(true)
                             .Where(f => f.sharedMesh != null).ToArray();

            // Size and place, in metres as exported.
            var bounds = Bounds(renderers);
            log.AppendLine($"SIZE: {bounds.size.x:F0} x {bounds.size.z:F0} m, " +
                           $"{bounds.size.y:F0} m high; centre {bounds.center.x:F0}, " +
                           $"{bounds.center.y:F0}, {bounds.center.z:F0}");

            if (new Vector2(bounds.center.x, bounds.center.z).magnitude > 500f)
                log.AppendLine("  PROBLEM: the map sits far from the origin. Floating point " +
                               "loses precision out there and the view jitters. Move it to " +
                               "0,0,0 in Blender (the scene build recentres it, but the file " +
                               "should be right too).");

            if (bounds.size.y < 1f || bounds.size.x > 20000f)
                log.AppendLine("  PROBLEM: that size is not a city block in metres — check " +
                               "Blender's unit scale and the FBX export's Apply Scalings.");

            float scale = Scale;
            log.AppendLine($"SCALE: placed at {scale:F3}x, the player is " +
                           $"{DexstandPlayerBuilder.PlayerHeight:F2} m — real proportions. " +
                           $"The map will be {bounds.size.x * scale:F0} x {bounds.size.z * scale:F0} " +
                           "scene units.");

            // Geometry.
            long tris = 0, verts = 0;
            var heavy = new List<(string name, long tris)>();
            int noUv = 0;
            foreach (var f in filters)
            {
                var m = f.sharedMesh;
                long t = 0;
                for (int s = 0; s < m.subMeshCount; s++) t += m.GetIndexCount(s) / 3;
                tris += t;
                verts += m.vertexCount;
                heavy.Add((f.name, t));
                if (m.uv == null || m.uv.Length == 0) noUv++;
            }

            log.AppendLine($"GEOMETRY: {filters.Length} meshes, {tris:N0} triangles, " +
                           $"{verts:N0} vertices, {transforms.Length} objects.");
            if (tris > 1_500_000)
                log.AppendLine("  PROBLEM: too heavy for a phone. Decimate in Blender " +
                               "(Decimate modifier, ratio 0.2-0.5 on the far buildings) — " +
                               "a mobile scene wants well under a million triangles.");
            else if (tris > 500_000)
                log.AppendLine("  WARNING: heavy for a phone. Fine on PC.");

            foreach (var h in heavy.OrderByDescending(x => x.tris).Take(8))
                log.AppendLine($"  {h.tris,10:N0}  {h.name}");

            if (noUv > 0)
                log.AppendLine($"  WARNING: {noUv} mesh(es) have no UVs — their textures " +
                               "will show as one flat colour.");

            // Materials and textures.
            var materials = renderers.SelectMany(r => r.sharedMaterials)
                                     .Where(m => m != null).Distinct().ToArray();
            int missing = renderers.Sum(r => r.sharedMaterials.Count(m => m == null));
            var broken = materials.Where(m => m.shader == null ||
                                              m.shader.name.Contains("InternalErrorShader") ||
                                              !m.shader.name.StartsWith("Universal Render Pipeline"))
                                  .ToArray();
            var untextured = materials.Where(m => m.HasProperty("_BaseMap") &&
                                                  m.GetTexture("_BaseMap") == null).ToArray();

            log.AppendLine($"MATERIALS: {materials.Length}" +
                           (materials.Length > 64 ? " — WARNING: each one is a draw call; " +
                                                    "merge them into atlases in Blender" : ""));
            if (missing > 0)
                log.AppendLine($"  PROBLEM: {missing} empty material slot(s) — they render magenta.");
            foreach (var m in broken.Take(5))
                log.AppendLine($"  PROBLEM: '{m.name}' uses '{m.shader?.name}' — magenta under URP.");
            if (untextured.Length > 0)
                log.AppendLine($"  WARNING: {untextured.Length} material(s) have no texture — " +
                               "exported without 'Path Mode: Copy' + 'Embed Textures', or the " +
                               "texture files were not copied into the folder. " +
                               $"e.g. '{untextured[0].name}'.");

            var textures = materials.SelectMany(m => m.GetTexturePropertyNameIDs()
                                                      .Select(m.GetTexture))
                                    .OfType<Texture2D>().Distinct().ToArray();
            var huge = textures.Where(t => t.width > 2048 || t.height > 2048).ToArray();
            log.AppendLine($"TEXTURES: {textures.Length}" +
                           (textures.Length > 0 ? $", largest {textures.Max(t => t.width)} px" : ""));
            if (huge.Length > 0)
                log.AppendLine($"  NOTE: {huge.Length} texture(s) over 2048 are imported at " +
                               "2048 for phones.");

            // Transforms that cause trouble.
            int mirrored = transforms.Count(t => t.localScale.x * t.localScale.y * t.localScale.z < 0f);
            if (mirrored > 0)
                log.AppendLine($"  PROBLEM: {mirrored} object(s) have negative scale — their " +
                               "faces turn inside out and collide wrong. Apply Scale in Blender " +
                               "(Ctrl+A → Scale) and recalculate normals.");

            int collision = filters.Count(f => IsCollision(f.transform));
            log.AppendLine(collision > 0
                ? $"COLLISION: {collision} dedicated '_col' mesh(es) — only those collide."
                : "COLLISION: none marked; every visible mesh will collide with its own shape.");

            log.AppendLine("Build the level from it with the 'scene' command, then 'gameplay'.");
        }
        finally
        {
            Object.DestroyImmediate(map);
        }

        Report(log.ToString());
    }

    private static void Report(string text)
    {
        Debug.Log("[Map] " + text);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "MapReport.txt"), text);
    }

    private static Bounds Bounds(Renderer[] renderers)
    {
        if (renderers.Length == 0) return new Bounds();
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
