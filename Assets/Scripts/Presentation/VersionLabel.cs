using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The build's version (DexstandBuild stamps the build time into it) small in the bottom right corner, from the
/// menu on: whoever reports a bug can say which build it was.
/// </summary>
public static class VersionLabel
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        var go = new GameObject("VersionLabel");
        Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var text = new GameObject("Text").AddComponent<Text>();
        text.transform.SetParent(go.transform, false);
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 20;
        text.alignment = TextAnchor.LowerRight;
        text.color = new Color(1f, 1f, 1f, 0.55f);
        text.raycastTarget = false;
        text.text = "v" + Application.version;
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-12f, 8f);
        rt.sizeDelta = new Vector2(400f, 30f);
    }
}
