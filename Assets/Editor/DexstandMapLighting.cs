using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Scene light for the imported map, set from a trigger file so it can be tuned
/// without anyone at the editor. MapLightTrigger.txt (next to the project) holds
/// "sun intensity r g b" and/or "ambient intensity" lines; the open scene is
/// changed and saved. There is no tonemapping in the scene, so a sun at the
/// template's intensity 2 burns bright ground and roofs to pure white.
/// </summary>
[InitializeOnLoad]
public static class DexstandMapLighting
{
    private static readonly string TriggerPath =
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "MapLightTrigger.txt"));

    private static double nextPoll;

    static DexstandMapLighting()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 1.0;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        if (!File.Exists(TriggerPath)) return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(TriggerPath);
            File.Delete(TriggerPath);
        }
        catch (IOException)
        {
            return;
        }

        foreach (var line in lines)
        {
            var p = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
            if (p[0] == "sun")
            {
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (light.type != LightType.Directional) continue;
                    light.intensity = F(1);
                    if (p.Length >= 5) light.color = new Color(F(2), F(3), F(4));
                    EditorUtility.SetDirty(light);
                }
            }
            else if (p[0] == "ambient")
            {
                RenderSettings.ambientIntensity = F(1);
            }
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[MapLighting] applied: " + string.Join(" | ", lines));
    }
}
