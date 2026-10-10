using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Render quality for the big region map. Every quality level used the phone
/// pipeline asset — also on a PC: 80% resolution, no anti-aliasing, one shadow
/// cascade over 50 units, hard shadows. The PC levels (Very High, Ultra: what a
/// Windows build starts in) now use PC_RPAsset, set up for a desktop GPU; the
/// phone asset gets a little more where it is cheap on mobile GPUs.
/// Menu "Dexstand/Качество графики", or a QualityTrigger.txt next to the project.
/// </summary>
[InitializeOnLoad]
public static class DexstandRenderQuality
{
    private const string PcAsset = "Assets/Settings/PC_RPAsset.asset";
    private const string MobileAsset = "Assets/Settings/Mobile_RPAsset.asset";
    private static readonly string TriggerPath =
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "QualityTrigger.txt"));
    private static double nextPoll;

    static DexstandRenderQuality()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 1.0;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
        if (!File.Exists(TriggerPath)) return;
        File.Delete(TriggerPath);
        Apply();
    }

    [MenuItem("Dexstand/Качество графики")]
    public static void Apply()
    {
        var pc = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PcAsset);
        var mobile = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(MobileAsset);
        if (pc == null || mobile == null)
        {
            Debug.LogError("[Quality] pipeline assets not found");
            return;
        }

        // Desktop: full resolution, 4x MSAA, soft shadows in four cascades out to 120 units (360 m).
        Set(pc, ("m_RenderScale", 1f), ("m_MSAA", 4), ("m_ShadowDistance", 120f), ("m_ShadowCascadeCount", 4),
            ("m_MainLightShadowmapResolution", 4096), ("m_SoftShadowsSupported", 1), ("m_SoftShadowQuality", 3));
        // Phone: tile GPUs resolve 2x MSAA almost for free; two cascades and soft shadows nearby.
        Set(mobile, ("m_RenderScale", 0.85f), ("m_MSAA", 2), ("m_ShadowDistance", 60f), ("m_ShadowCascadeCount", 2),
            ("m_MainLightShadowmapResolution", 2048), ("m_SoftShadowsSupported", 1), ("m_SoftShadowQuality", 1));

        // The two top levels, where Windows starts, draw with the desktop asset.
        var qs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var levels = qs.FindProperty("m_QualitySettings");
        for (int i = 0; i < levels.arraySize; i++)
        {
            var level = levels.GetArrayElementAtIndex(i);
            string name = level.FindPropertyRelative("name").stringValue;
            level.FindPropertyRelative("customRenderPipeline").objectReferenceValue =
                name == "Very High" || name == "Ultra" ? pc : mobile;
        }
        qs.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log("[Quality] PC levels on PC_RPAsset (4x MSAA, 4 cascades, 120 u shadows); phones 2x MSAA, 2 cascades, 60 u.");
    }

    private static void Set(Object asset, params (string name, object value)[] fields)
    {
        var so = new SerializedObject(asset);
        foreach (var (name, value) in fields)
        {
            var p = so.FindProperty(name);
            if (p == null)
            {
                Debug.LogWarning($"[Quality] {asset.name} has no {name}");
                continue;
            }
            switch (value)
            {
                case float f: p.floatValue = f; break;
                case int n when p.propertyType == SerializedPropertyType.Boolean: p.boolValue = n != 0; break;
                case int n: p.intValue = n; break;
            }
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
    }
}
