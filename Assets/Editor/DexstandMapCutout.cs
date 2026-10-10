using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Material fixes for a map in <see cref="DexstandMapImport.Folder"/>, applied
/// as each material comes out of the FBX.
///
/// The FBX path turns anything with an alpha texture into a blended
/// transparent, which sorts wrongly across thousands of leaf cards: materials
/// named "Cutout_*" (leaves, bushes, fences, sign cut-outs) become alpha-tested
/// and double-sided instead. And Blender's roughness does not come through, so
/// the rest are set matte, with glass and metal given their shine back by name.
/// </summary>
public class DexstandMapCutout : AssetPostprocessor
{
    // URP builds each material from the FBX's description in its own
    // preprocessor; running after it keeps it from resetting what is set here.
    public override int GetPostprocessOrder() => 1000;

    private bool InFolder => assetPath.Replace('\\', '/').StartsWith(DexstandMapImport.Folder + "/");

    private void OnPreprocessMaterialDescription(MaterialDescription description, Material material,
                                                 AnimationClip[] animations)
    {
        if (InFolder) Apply(material);
    }

    private void OnPostprocessMaterial(Material material)
    {
        if (InFolder) Apply(material);
    }

    internal static void Apply(Material material)
    {
        string name = material.name;
        float smoothness = name.StartsWith("Glass_") ? 0.9f
                         : name.StartsWith("Metal") || name.StartsWith("RailSteel") || name.StartsWith("LampHead") ? 0.5f
                         : 0.1f;
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);

        // Region map: light concrete textures came out nearly white in the sun; tone them down
        float tone = name.StartsWith("Pavers") || name.StartsWith("Curb") || name.StartsWith("Gutter") ? 0.7f
                   : name.StartsWith("Ortho_") ? 0.95f : 1f;
        if (tone < 1f && material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", new Color(tone, tone, tone, 1f));
        if (name.StartsWith("Ortho_") && material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.02f);

        // Region map: the blurry orthophoto on ground and roofs gets sharp grass / paving / roof-tile detail.
        // The region tiles' materials are named after their texture ("ortho_E_N_k", one for the ground and roofs
        // of a photo alike) — "Ortho_" never matched them, so until 2026-09-27 the region had no detail at all
        bool roof = name.StartsWith("RoofOrtho_"), shared = name.StartsWith("ortho_");
        if (roof || shared || name.StartsWith("Ortho_"))
        {
            var shader = Shader.Find("Dexstand/OrthoDetail");
            if (shader != null)
            {
                var ortho = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
                material.shader = shader;
                if (ortho != null) material.SetTexture("_BaseMap", ortho);
                material.SetFloat("_Mode", roof ? 1f : shared ? 2f : 0f);
                material.SetFloat("_MetresPerUnit", 1f / DexstandMapImport.Scale);
                material.SetTexture("_GrassTex", Detail("leafy_grass_Diffuse.jpg"));
                material.SetTexture("_PaveTex", Detail("concrete_pavers_02_Diffuse.jpg"));
                material.SetTexture("_TileTex", Detail("clay_roof_tiles_02_Diffuse.jpg"));
                material.SetTexture("_TileNormal", Detail("clay_roof_tiles_02_nor_gl.jpg"));
                material.SetTexture("_GrassNormal", Detail("leafy_grass_nor_gl.jpg"));
                material.SetTexture("_PaveNormal", Detail("concrete_pavers_02_nor_gl.jpg"));
            }
            return;
        }

        // rivers, lakes, the Rhine (build_water.py, 2026-09-28)
        if (name == "Water")
        {
            var water = Shader.Find("Dexstand/Water");
            if (water != null) material.shader = water;
            if (material.HasProperty("_MetresPerUnit")) material.SetFloat("_MetresPerUnit", 1f / DexstandMapImport.Scale);
            return;
        }

        if (!name.StartsWith("Cutout_")) return;

        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", 0.5f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_SrcBlend", (float)BlendMode.One);
        material.SetFloat("_DstBlend", (float)BlendMode.Zero);
        material.SetFloat("_ZWrite", 1f);
        material.EnableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.AlphaTest;
        material.doubleSidedGI = true;

        // A transparency factor from the FBX would otherwise clip the whole card.
        if (material.HasProperty("_BaseColor"))
        {
            var c = material.GetColor("_BaseColor");
            material.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1f));
        }

        // URP lights the back of a double-sided card as if it faced away from the
        // sun, so half the leaves of every tree came out black. A little of the
        // leaf texture as emission keeps the shaded side green.
        var baseMap = material.GetTexture("_BaseMap");
        if (baseMap != null && material.HasProperty("_EmissionColor"))
        {
            material.SetTexture("_EmissionMap", baseMap);
            material.SetColor("_EmissionColor", new Color(0.35f, 0.35f, 0.35f));
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
    }

    private static Texture2D Detail(string file) =>
        AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Maps/Shaders/Detail/" + file);

    /// <summary>Normal maps in the map folder ("*_nor*") are flagged as such, or URP reads them as colour.</summary>
    private void OnPreprocessTexture()
    {
        string p = assetPath.Replace('\\', '/');
        bool detail = p.StartsWith("Assets/Maps/Shaders/Detail/") || p.StartsWith("Assets/Resources/Map/Detail/");
        if (detail && System.IO.Path.GetFileNameWithoutExtension(assetPath).Contains("_nor"))
            ((TextureImporter)assetImporter).textureType = TextureImporterType.NormalMap;
        if (!InFolder) return;
        // the orthophotos are most of the map's texture weight: on a phone a quarter of the pixels, ASTC 8x8
        if (System.IO.Path.GetFileName(assetPath).StartsWith("ortho_"))
        {
            var ti = (TextureImporter)assetImporter;
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 1024;
            android.format = TextureImporterFormat.ASTC_8x8;
            ti.SetPlatformTextureSettings(android);
        }
        if (!System.IO.Path.GetFileNameWithoutExtension(assetPath).Contains("_nor")) return;
        ((TextureImporter)assetImporter).textureType = TextureImporterType.NormalMap;
    }
}
