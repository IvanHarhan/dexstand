using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders the first-person view straight to PNG so viewmodel placement can be
/// checked without entering play mode. Writes one image per yaw variant.
/// </summary>
public static class DexstandPreview
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string OutDir = @"C:\Users\harha\AppData\Local\Temp\claude\C--Users-harha-Desktop\78fb8230-9342-460f-afd3-2590c1d2052c\scratchpad\preview";

    private const int Width = 960;
    private const int Height = 540;

    private static readonly Vector3 ArmsViewOffset = new Vector3(0.16f, -0.26f, 0.55f);
    private static readonly float[] Yaws = { };

    /// <summary>Rebuild then render, so the image matches what the builder saved.</summary>
    public static void BuildAndRender()
    {
        DexstandPlayerBuilder.BuildPlayer();
        RenderVariants();
    }

    /// <summary>
    /// One first-person frame, written where the caller asks. The sweep uses it
    /// to photograph a size and a reach per file name, so a dozen candidates can
    /// be compared side by side instead of one play session at a time.
    /// </summary>
    public static void RenderTo(string fullPath)
    {
        outputOverride = fullPath;
        try { RenderVariants(); }
        finally { outputOverride = null; }
    }

    private static string outputOverride;

    public static void RenderVariants()
    {
        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[Preview] Player prefab missing.");
            return;
        }

        var player = Object.Instantiate(prefab);
        player.name = "PreviewPlayer";
        player.transform.position = new Vector3(0f, 0.1f, 0f);
        player.transform.rotation = Quaternion.identity;

        var all = player.GetComponentsInChildren<Transform>(true);
        var cameraHolder = all.FirstOrDefault(t => t.name == "CameraHolder");
        var arms = all.FirstOrDefault(t => t.name == "Arms");
        var body = all.FirstOrDefault(t => t.name == "Body");
        var cam = player.GetComponentInChildren<Camera>(true);

        if (cameraHolder == null || arms == null || cam == null)
        {
            Debug.LogError("[Preview] Player prefab is missing CameraHolder / Arms / Camera.");
            Object.DestroyImmediate(player);
            return;
        }

        // The body is only shown to other players; in here it would fill the lens.
        if (body != null) body.gameObject.SetActive(false);
        arms.gameObject.SetActive(true);
        cam.gameObject.SetActive(true);

        var model = arms.Find("Model");
        if (model == null)
        {
            Debug.LogError("[Preview] No Model under Arms — run BuildPlayer first.");
            Object.DestroyImmediate(player);
            return;
        }

        // Render exactly what the builder saved — no rotation override — so the
        // image is the real thing rather than another hypothesis.
        var finalPath = outputOverride ?? Path.Combine(OutDir, "view_final.png");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
        Capture(cam, finalPath);
        Debug.Log($"[Preview] built-in placement -> {finalPath}");

        foreach (var yaw in Yaws)
        {
            model.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // Re-centre after rotating: the mesh sits off its own origin.
            var b = WorldBounds(model.gameObject);
            model.position += (cameraHolder.position + ArmsViewOffset) - b.center;

            var path = Path.Combine(OutDir, $"view_yaw{(int)yaw}.png");
            Capture(cam, path);
            Debug.Log($"[Preview] yaw {yaw} -> {path}");
        }

        Object.DestroyImmediate(player);
    }

    private const string BodyOutDir = @"C:\Users\harha\AppData\Local\Temp\claude\C--Users-harha-Desktop\f669d839-b12d-495d-beaa-b2226832d4b6\scratchpad\character";

    /// <summary>
    /// Photographs the third-person body from three sides.
    ///
    /// RenderVariants deliberately hides the body — it is looking down the
    /// player's own sights — so a new character could be swapped in and nothing
    /// in the preview would change. This one does the opposite: body on, arms
    /// off, and its own camera walked around the model, which is the only way to
    /// see whether the skin actually landed.
    /// </summary>
    public static void RenderBody()
    {
        Directory.CreateDirectory(BodyOutDir);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[Preview] Player prefab missing.");
            return;
        }

        var player = Object.Instantiate(prefab);
        player.name = "BodyPreviewPlayer";
        player.transform.position = new Vector3(0f, 0.1f, 0f);

        var all = player.GetComponentsInChildren<Transform>(true);
        var arms = all.FirstOrDefault(t => t.name == "Arms");
        var body = all.FirstOrDefault(t => t.name == "Body");
        if (body == null)
        {
            Debug.LogError("[Preview] No Body in the player prefab.");
            Object.DestroyImmediate(player);
            return;
        }

        body.gameObject.SetActive(true);
        if (arms != null) arms.gameObject.SetActive(false);

        // The stance other players see, so the gun in the fist is judged where it will be held.
        var model = body.Find("Model");
        var idle = DexstandBodyAnimations.Downloaded("Idle");
        if (model != null && idle != null) idle.SampleAnimation(model.gameObject, 0f);
        foreach (var c in player.GetComponentsInChildren<Camera>(true)) c.gameObject.SetActive(false);

        var bounds = WorldBounds(body.gameObject);
        var camGo = new GameObject("BodyPreviewCam");
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.01f;

        // Framing in model heights, not metres: the player is 0.6 m tall in this
        // project, so anything expressed in metres puts the lens either inside
        // the mesh or out in the next field.
        float distance = Mathf.Max(bounds.size.y, 0.01f) * 1.6f;

        foreach (var yaw in new[] { 0f, 40f, 180f })
        {
            var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            camGo.transform.position = bounds.center + dir * distance + Vector3.up * bounds.size.y * 0.08f;
            camGo.transform.rotation = Quaternion.LookRotation(bounds.center - camGo.transform.position);

            var path = Path.Combine(BodyOutDir, $"body_{(int)yaw}.png");
            Capture(cam, path);
            Debug.Log($"[Preview] body yaw {yaw} -> {path}");
        }

        Debug.Log($"[Preview] Body size {bounds.size}, centre {bounds.center}.");

        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(player);
    }

    private static void Capture(Camera cam, string path)
    {
        var rt = new RenderTexture(Width, Height, 24);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;

        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();

        File.WriteAllBytes(path, tex.EncodeToPNG());

        cam.targetTexture = prevTarget;
        RenderTexture.active = prevActive;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    private static Bounds WorldBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
