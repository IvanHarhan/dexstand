using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The Resources/Cars models' materials as Unity has them — name, shader, colour, main texture — to find the one
/// drawn in the wrong colour. Trigger "carmats"; the list lands in DexstandResult.txt's log line and Editor.log.
/// </summary>
public static class DexstandCarMaterials
{
    public static void Report()
    {
        var sb = new StringBuilder("[CarMats]\n");
        foreach (var path in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Resources/Cars" }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            sb.AppendLine(path);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.GetComponent<MeshFilter>() is MeshFilter mf ? mf.sharedMesh : null;
                int empty = r.sharedMaterials.Count(m => m == null);
                if (empty > 0 || mesh != null && mesh.subMeshCount != r.sharedMaterials.Length)
                    sb.AppendLine($"  {r.name}: {empty} EMPTY slots of {r.sharedMaterials.Length}, {(mesh != null ? mesh.subMeshCount : 0)} submeshes");
                foreach (var m in r.sharedMaterials.Where(m => m != null).Distinct())
                {
                    var c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.color : Color.clear;
                    var tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
                    sb.AppendLine($"  {r.name}: {m.name} [{m.shader.name}] {c} tex {(tex != null ? tex.name : "-")} at {AssetDatabase.GetAssetPath(m)}");
                }
            }
        }
        Debug.Log(sb.ToString());
    }

    private const string Folder = "Assets/Resources/Cars/Materials";

    /// <summary>
    /// The Audi TT's materials that never made it into Resources/Cars/Materials. The model looks its materials
    /// up there by name; for these seven there was nothing, the slots stayed empty and drew in the error colour —
    /// the whole cockpit, steering wheel included, bright magenta. Made here and the model reimported.
    /// </summary>
    public static void FixAudi()
    {
        void Make(string name, Color colour, float metallic, float smoothness)
        {
            string path = $"{Folder}/{name}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", colour);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[CarMats] made {path}");
        }
        Make("internal", new Color(0.055f, 0.055f, 0.06f), 0f, 0.25f);                     // dash, seats, wheel
        Make("chrome", new Color(0.8f, 0.8f, 0.82f), 1f, 0.9f);
        Make("car_audi_ttcoupe_2003_Meshbadges", new Color(0.8f, 0.8f, 0.82f), 1f, 0.9f);
        Make("grille", new Color(0.02f, 0.02f, 0.02f), 0f, 0.3f);
        Make("lights", new Color(0.92f, 0.92f, 0.95f), 0f, 0.95f);
        Make("light_R", new Color(0.55f, 0.02f, 0.02f), 0f, 0.9f);
        Make("car_audi_ttcoupe_2003_Meshrim", new Color(0.6f, 0.6f, 0.62f), 0.8f, 0.7f);
        Make("car_audi_ttcoupe_2003_MeshDdiaz_DesignRepositorio_3DAudiTT", new Color(0.6f, 0.6f, 0.62f), 0.5f, 0.6f);
        AssetDatabase.SaveAssets();
        // Named by texture, the textured ones (internal, chrome, lights…) looked for Image_N.mat — the .fbm images
        // have no extension, aren't textures, and the slots stayed empty. By material name they find the ones above.
        var importer = (ModelImporter)AssetImporter.GetAtPath("Assets/Resources/Cars/audi-tt.fbx");
        importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        importer.SaveAndReimport();
        Report();
    }
}
