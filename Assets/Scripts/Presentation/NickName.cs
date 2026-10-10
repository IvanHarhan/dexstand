using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The player's nickname: typed in the main menu (a field this adds at the top of the screen while the menu is
/// up), remembered between runs, sent to everyone by PlayerController when a match starts and shown over the
/// other players' heads by NameTag.
/// </summary>
public class NickName : MonoBehaviour
{
    private const string Key = "Dexstand.Nick";

    public static string Current
    {
        get
        {
            string n = PlayerPrefs.GetString(Key, "");
            if (string.IsNullOrWhiteSpace(n))
            {
                n = "Игрок" + Random.Range(100, 1000);
                PlayerPrefs.SetString(Key, n);
            }
            return n;
        }
        set => PlayerPrefs.SetString(Key, string.IsNullOrWhiteSpace(value) ? "" : value.Trim());
    }

    private GameMenu menu;
    private GameObject panel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<GameMenu>() == null) return;
        new GameObject("NickName").AddComponent<NickName>();
    }

    private void Start()
    {
        menu = FindFirstObjectByType<GameMenu>();
        var canvasGo = new GameObject("NickUI");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 60;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        panel = new GameObject("NickPanel", typeof(RectTransform));
        panel.transform.SetParent(canvasGo.transform, false);
        var rt = (RectTransform)panel.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -24f);
        rt.sizeDelta = new Vector2(560f, 76f);
        panel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var label = Text(panel.transform, font, "Ник:", TextAnchor.MiddleLeft, new Vector2(16f, 0f), new Vector2(100f, 76f));
        label.color = new Color(1f, 1f, 1f, 0.8f);

        var fieldGo = new GameObject("Field", typeof(RectTransform));
        fieldGo.transform.SetParent(panel.transform, false);
        var frt = (RectTransform)fieldGo.transform;
        frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(1f, 1f);
        frt.offsetMin = new Vector2(110f, 10f); frt.offsetMax = new Vector2(-12f, -10f);
        fieldGo.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
        var field = fieldGo.AddComponent<InputField>();
        var text = Text(fieldGo.transform, font, "", TextAnchor.MiddleLeft, new Vector2(12f, 0f), Vector2.zero, true);
        field.textComponent = text;
        field.characterLimit = 20;
        field.text = Current;
        field.onEndEdit.AddListener(v => { Current = v; field.text = Current; });
    }

    private static Text Text(Transform parent, Font font, string s, TextAnchor align, Vector2 offset, Vector2 size, bool fill = false)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        if (fill) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = offset; rt.offsetMax = -offset; }
        else { rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f); rt.anchoredPosition = offset; rt.sizeDelta = size; }
        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = 34;
        t.alignment = align;
        t.color = Color.white;
        t.text = s;
        t.supportRichText = false;
        return t;
    }

    private void Update()
    {
        // only on the main menu, where the match has not started yet
        bool show = menu != null && menu.InMenu;
        if (panel != null && panel.activeSelf != show) panel.SetActive(show);
    }
}

/// <summary>A nickname floating over another player's head, turned to the camera; gone past a street's length.</summary>
public class NameTag : MonoBehaviour
{
    public PlayerController player;
    private TextMesh text;
    private MeshRenderer mesh;

    public static void Create(PlayerController player)
    {
        var go = new GameObject("NameTag");
        go.transform.SetParent(player.transform, false);
        go.transform.localPosition = Vector3.up * 0.78f;  // over the head of a 0.6-unit player
        var tag = go.AddComponent<NameTag>();
        tag.player = player;
        tag.text = go.AddComponent<TextMesh>();
        tag.text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tag.mesh = go.GetComponent<MeshRenderer>();
        tag.mesh.sharedMaterial = tag.text.font.material;
        // A TextMesh line is fontSize × characterSize / 10 units tall: 0.14 here, a quarter of the player.
        // At 0.0022 it came out 0.014 — two pixels, which is why nobody saw a nick.
        tag.text.fontSize = 64;
        tag.text.characterSize = 0.022f;
        tag.text.anchor = TextAnchor.LowerCenter;
        tag.text.alignment = TextAlignment.Center;
        tag.text.color = Color.white;
    }

    private void LateUpdate()
    {
        if (player == null || !player.Object || !player.Object.IsValid) return;
        string nick = player.Nick.ToString();
        text.text = nick.Length > 0 ? nick : "Игрок " + player.Object.InputAuthority.PlayerId;
        Camera cam = null;
        foreach (var c in Camera.allCameras)
            if (c.targetTexture == null && (cam == null || c.depth > cam.depth)) cam = c;
        if (cam == null) return;
        var d = transform.position - cam.transform.position;
        mesh.enabled = d.sqrMagnitude < 60f * 60f;
        transform.rotation = Quaternion.LookRotation(d, Vector3.up);
        // a little bigger with distance, so it stays readable
        transform.localScale = Vector3.one * Mathf.Clamp(d.magnitude / 6f, 1f, 4f);
    }
}
