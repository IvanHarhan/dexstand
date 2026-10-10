using UnityEngine;

/// <summary>
/// The asphalt that OrthoDetail lays over dark paved ground near the camera. Handed to the shader as globals: the
/// ground materials live inside the tiles' FBX imports, and adding a texture to them would mean reimporting all 138.
/// </summary>
public static class MapGroundDetail
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Apply()
    {
        var tex = Resources.Load<Texture2D>("Map/Detail/asphalt_02_Diffuse");
        var nor = Resources.Load<Texture2D>("Map/Detail/asphalt_02_nor_gl");
        if (tex == null || nor == null) return;
        Shader.SetGlobalTexture("_AsphaltTex", tex);
        Shader.SetGlobalTexture("_AsphaltNormal", nor);
        Shader.SetGlobalFloat("_AsphaltSize", 3f);
    }
}
