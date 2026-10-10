using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The pedestrians of the map (Assets/Models/NPC, from blender_tools/make_npcs.py: MPFB people on the
/// mixamo_unity rig): Humanoid, so they walk with the patrol target's clips, and hair, brows and lashes
/// alpha-tested and two-sided instead of the grey sheets they import as.
/// </summary>
public class DexstandNpcImport : AssetPostprocessor
{
    public const string Folder = "Assets/Models/NPC";

    private bool InFolder => assetPath.Replace('\\', '/').StartsWith(Folder + "/");

    private void OnPreprocessModel()
    {
        if (!InFolder) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
    }

    /// <summary>Skin and cloth maps are 2-4k; a passer-by is never that close.</summary>
    private void OnPreprocessTexture()
    {
        if (!InFolder) return;
        ((TextureImporter)assetImporter).maxTextureSize = 1024;
    }

    private void OnPostprocessMaterial(Material material)
    {
        if (!InFolder) return;
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.15f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        string n = material.name.ToLowerInvariant();
        if (!(n.Contains("hair") || n.Contains("eyebrow") || n.Contains("eyelash") || n.Contains("short") || n.Contains("bob")
              || n.Contains("long") || n.Contains("ponytail") || n.Contains("braid") || n.Contains("afro"))) return;
        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", 0.45f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.EnableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.AlphaTest;
        if (material.HasProperty("_BaseColor"))
        {
            var c = material.GetColor("_BaseColor");
            material.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1f));
        }
    }
}
