using UnityEditor;

/// <summary>
/// The in-game map picture (Assets/Resources/Minimap, from Blender's region_data/make_minimap.py) is 3750 x 4250 px
/// at 4 m a pixel: import it at up to 4096 so it is not halved to a blur, and keep it out of mip streaming.
/// </summary>
public class DexstandMinimapImport : AssetPostprocessor
{
    private void OnPreprocessTexture()
    {
        if (!assetPath.Replace('\\', '/').StartsWith("Assets/Resources/Minimap/")) return;
        var importer = (TextureImporter)assetImporter;
        importer.maxTextureSize = 4096;
        importer.mipmapEnabled = true;
        importer.wrapMode = UnityEngine.TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
    }
}
