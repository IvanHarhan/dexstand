using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The region map as a minimap (bottom left on a PC, top right on a phone, north up, the player an arrow) and as a
/// full-screen map (M, or a tap on the minimap) showing the whole world and its border: a click there plans a
/// route over the roads (A* on the OSM road graph, one-way streets respected), drawn on both maps until reached.
/// Picture and graph come from Blender's region_data/make_minimap.py into Resources/Minimap; everything there is
/// in the export's local metres, and the map root carries origin and scale: Unity = (Ox - x, z, Oy - y) * scale.
/// Built when the game scene loads, like MapTeleport.
/// </summary>
public class MapMinimap : MonoBehaviour
{
    public static bool FullMapOpen { get; private set; }
    /// <summary>The road graph in scene xz, for MapTraffic: nodes and, per node, where one may drive on to.</summary>
    public static Vector2[] RoadNodes { get; private set; }
    public static List<(int to, float w)>[] RoadNext { get; private set; }
    /// <summary>Per node: on a through road (tertiary and up), where the traffic keeps.</summary>
    public static bool[] RoadThrough { get; private set; }

    [System.Serializable]
    private class Data
    {
        public float res; public int width; public int height; public float utm_w; public float utm_h;
        public float[] origin; public float[] aff; public float[] aff2; public float[] inv;
        public float[] nodes; public int[] edges;
    }

    private const float MiniMetres = 520f;   // what the minimap spans
    private Data data;
    private Texture2D tex;
    private float scale = 1f / 3f;
    private Vector2[] nodeXZ;                // scene units
    private List<(int to, float w)>[] adj;
    private readonly List<int> route = new List<int>();
    private Vector2? destination;

    private RectTransform mini, full, fullImage, miniArrow, fullArrow, destMark;
    private Canvas canvas;
    private float zoom = 1f;

    /// <summary>Full map zoom about a point of the picture (its local coordinates), which stays where it is.</summary>
    private void Zoom(float factor, Vector2 about)
    {
        float z = Mathf.Clamp(zoom * factor, 1f, 8f);
        fullImage.anchoredPosition += about * (zoom - z);
        zoom = z;
        fullImage.localScale = Vector3.one * zoom;
    }
    private RawImage miniImage;
    private UILine miniRoute, fullRoute;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<NetworkLauncher>() == null || FindFirstObjectByType<MapStreamer>() == null) return;
        if (Resources.Load<TextAsset>("Minimap/minimap") == null) return;
        new GameObject("MapMinimap").AddComponent<MapMinimap>();
    }

    private void Awake()
    {
        data = JsonUtility.FromJson<Data>(Resources.Load<TextAsset>("Minimap/minimap").text);
        tex = Resources.Load<Texture2D>("Minimap/minimap");
        var map = GameObject.Find("Map");
        if (map != null) scale = map.transform.lossyScale.x;
        BuildGraph();
        BuildUI();
    }

    // ---------------- coordinates ----------------
    private Vector2 ToLocal(Vector2 xz) => new Vector2(data.origin[0] - xz.x / scale, data.origin[1] - xz.y / scale);
    private Vector2 FromLocal(Vector2 b) => new Vector2((data.origin[0] - b.x) * scale, (data.origin[1] - b.y) * scale);

    /// <summary>scene xz -> map pixel (x right, y down)</summary>
    private Vector2 ToPixel(Vector2 xz)
    {
        var b = ToLocal(xz);
        float bx = b.x - data.aff2[0], by = b.y - data.aff2[1];
        float dx = bx * data.inv[0] + by * data.inv[2], dy = bx * data.inv[1] + by * data.inv[3];
        return new Vector2(dx / data.res, (data.utm_h - dy) / data.res);
    }

    private Vector2 FromPixel(Vector2 px)
    {
        float dx = px.x * data.res, dy = data.utm_h - px.y * data.res;
        var b = new Vector2(dx * data.aff[0] + dy * data.aff[2] + data.aff2[0], dx * data.aff[1] + dy * data.aff[3] + data.aff2[1]);
        return FromLocal(b);
    }

    // ---------------- route ----------------
    private void BuildGraph()
    {
        int n = data.nodes.Length / 2;
        nodeXZ = new Vector2[n];
        adj = new List<(int, float)>[n];
        var through = new bool[n];
        for (int i = 0; i < n; i++)
        {
            nodeXZ[i] = FromLocal(new Vector2(data.nodes[2 * i], data.nodes[2 * i + 1]));
            adj[i] = new List<(int, float)>(3);
        }
        for (int e = 0; e + 2 < data.edges.Length; e += 3)
        {
            int a = data.edges[e], b = data.edges[e + 1];
            float w = Vector2.Distance(nodeXZ[a], nodeXZ[b]);
            int flags = data.edges[e + 2];  // 1 one-way, 2 a through road (tertiary and up)
            adj[a].Add((b, w));
            if ((flags & 1) == 0) adj[b].Add((a, w));
            if ((flags & 2) != 0) through[a] = through[b] = true;
        }
        RoadNodes = nodeXZ;
        RoadNext = adj;
        RoadThrough = through;
    }

    private int Nearest(Vector2 xz)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < nodeXZ.Length; i++)
        {
            float d = (nodeXZ[i] - xz).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private void PlanRoute(Vector2 fromXZ, Vector2 toXZ)
    {
        route.Clear();
        int s = Nearest(fromXZ), g = Nearest(toXZ);
        if (s < 0 || g < 0) return;
        var dist = new Dictionary<int, float> { [s] = 0f };
        var prev = new Dictionary<int, int>();
        var open = new SortedSet<(float f, int id)>();
        open.Add((Vector2.Distance(nodeXZ[s], nodeXZ[g]), s));
        var closed = new HashSet<int>();
        while (open.Count > 0)
        {
            var cur = open.Min; open.Remove(cur);
            int a = cur.id;
            if (a == g) break;
            if (!closed.Add(a)) continue;
            foreach (var (b, w) in adj[a])
            {
                float nd = dist[a] + w;
                if (dist.TryGetValue(b, out float old) && old <= nd) continue;
                dist[b] = nd; prev[b] = a;
                open.Add((nd + Vector2.Distance(nodeXZ[b], nodeXZ[g]), b));
            }
        }
        if (!prev.ContainsKey(g) && s != g) return;
        for (int k = g; ; k = prev[k])
        {
            route.Add(k);
            if (k == s || !prev.ContainsKey(k)) break;
        }
        route.Reverse();
        destination = toXZ;
    }

    // ---------------- UI ----------------
    private void BuildUI()
    {
        var canvasGo = new GameObject("MinimapUI");
        canvasGo.transform.SetParent(transform, false);
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 42;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        bool phone = Application.isMobilePlatform;
        mini = Panel(canvasGo.transform, "Minimap", new Color(0f, 0f, 0f, 0.6f));
        // phone: top left, smaller, under the row of travel buttons (it covered the touch controls);
        // PC: top right under the pause button (bottom left covered the HUD's counters)
        // phone (2026-09-26): the left corner still sat on the touch controls — top, in from the left edge
        var anchor = phone ? new Vector2(0.5f, 1f) : new Vector2(1f, 1f);
        mini.anchorMin = mini.anchorMax = mini.pivot = anchor;
        mini.anchoredPosition = phone ? new Vector2(-300f, -100f) : new Vector2(-20f, -120f);
        mini.sizeDelta = phone ? new Vector2(180f, 180f) : new Vector2(300f, 300f);
        mini.gameObject.AddComponent<RectMask2D>();
        miniImage = Raw(mini, tex, 4f);
        miniRoute = Line(mini, 7f);
        miniArrow = Arrow(mini, 26f);
        mini.gameObject.AddComponent<Button>().onClick.AddListener(() => SetFull(true));
        var north = Label(mini, "N", Vector2.zero);  // the map is north-up
        north.alignment = TextAnchor.UpperCenter;
        north.fontSize = 26;
        north.fontStyle = FontStyle.Bold;

        full = Panel(canvasGo.transform, "FullMap", new Color(0.05f, 0.06f, 0.07f, 0.94f));
        full.anchorMin = Vector2.zero; full.anchorMax = Vector2.one; full.offsetMin = full.offsetMax = Vector2.zero;
        var fi = Raw(full, tex, 0f);
        fullImage = fi.rectTransform;
        fullImage.anchorMin = fullImage.anchorMax = fullImage.pivot = new Vector2(0.5f, 0.5f);
        float h = 1000f, w = h * data.width / data.height;
        fullImage.sizeDelta = new Vector2(w, h);
        fi.gameObject.AddComponent<MapClick>().owner = this;
        fullRoute = Line(fullImage, 5f);
        destMark = Arrow(fullImage, 22f);
        destMark.GetComponent<Image>().color = new Color(1f, 0.3f, 0.6f);
        fullArrow = Arrow(fullImage, 30f);
        Label(full, phone ? "Нажми на карту — маршрут.  X — закрыть" : "Клик — маршрут,  ПКМ — сбросить,  M — закрыть", new Vector2(0f, -30f));
        var close = Panel(full, "Close", new Color(0f, 0f, 0f, 0.6f));
        close.anchorMin = close.anchorMax = close.pivot = new Vector2(1f, 1f);
        close.anchoredPosition = new Vector2(-20f, -20f);
        close.sizeDelta = new Vector2(90f, 90f);
        close.gameObject.AddComponent<Button>().onClick.AddListener(() => SetFull(false));
        Label(close, "X", Vector2.zero).fontSize = 48;
        // zoom buttons for a finger; a mouse wheel zooms too (Update) and a drag pans (MapClick)
        for (int k = 0; k < 2; k++)
        {
            bool zoomIn = k == 0;
            var b = Panel(full, zoomIn ? "ZoomIn" : "ZoomOut", new Color(0f, 0f, 0f, 0.6f));
            b.anchorMin = b.anchorMax = b.pivot = new Vector2(1f, 0.5f);
            b.anchoredPosition = new Vector2(-20f, zoomIn ? 55f : -55f);
            b.sizeDelta = new Vector2(90f, 90f);
            b.gameObject.AddComponent<Button>().onClick.AddListener(() => Zoom(zoomIn ? 1.5f : 1f / 1.5f, Vector2.zero));
            Label(b, zoomIn ? "+" : "-", Vector2.zero).fontSize = 56;
        }
        full.gameObject.SetActive(false);

        if (FindFirstObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }
    }

    private static RectTransform Panel(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = c;
        return (RectTransform)go.transform;
    }

    private static RawImage Raw(RectTransform parent, Texture t, float inset)
    {
        // Not "Map": that name is how the world map is found at runtime.
        var go = new GameObject("MinimapImage", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        var ri = go.AddComponent<RawImage>();
        ri.texture = t;
        return ri;
    }

    private static UILine Line(RectTransform parent, float thickness)
    {
        var go = new GameObject("Route", typeof(RectTransform), typeof(CanvasRenderer));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.zero;
        var l = go.AddComponent<UILine>();
        l.thickness = thickness;
        l.color = new Color(0.2f, 0.75f, 1f, 0.95f);
        l.raycastTarget = false;
        return l;
    }

    /// <summary>A white triangle pointing up (map north) — the player, or the destination pin.</summary>
    private static RectTransform Arrow(RectTransform parent, float size)
    {
        var go = new GameObject("Arrow", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        var img = go.AddComponent<Image>();
        img.sprite = TriangleSprite();
        img.color = new Color(1f, 0.85f, 0.2f);
        img.raycastTarget = false;
        return rt;
    }

    private static Sprite triangle;
    private static Sprite TriangleSprite()
    {
        if (triangle != null) return triangle;
        const int n = 64;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float fx = (x + 0.5f) / n, fy = (y + 0.5f) / n;
                bool inside = fy > 0.08f && Mathf.Abs(fx - 0.5f) < (1f - fy) * 0.45f && !(fy < 0.35f && Mathf.Abs(fx - 0.5f) < (0.35f - fy) * 1.2f);
                t.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        t.Apply();
        return triangle = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    private static Text Label(RectTransform parent, string s, Vector2 pos)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(0f, 60f);
        if (pos == Vector2.zero) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 28;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = s;
        text.raycastTarget = false;
        return text;
    }

    private void SetFull(bool open)
    {
        FullMapOpen = open;
        full.gameObject.SetActive(open);
        mini.gameObject.SetActive(!open);
        if (!Application.isMobilePlatform)
        {
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
        }
    }

    private class MapClick : MonoBehaviour, IPointerClickHandler, IDragHandler
    {
        public MapMinimap owner;

        // a drag pans the zoomed map (and, past the drag threshold, is no click)
        public void OnDrag(PointerEventData e) =>
            owner.fullImage.anchoredPosition += e.delta / Mathf.Max(owner.canvas.scaleFactor, 1e-3f);

        public void OnPointerClick(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var lp)) return;
            if (e.button == PointerEventData.InputButton.Right) { owner.route.Clear(); owner.destination = null; return; }
            var r = rt.rect;
            var px = new Vector2((lp.x - r.xMin) / r.width * owner.data.width, (r.yMax - lp.y) / r.height * owner.data.height);
            var cam = ActiveCamera();
            if (cam == null) return;
            var p = cam.transform.position;
            owner.PlanRoute(new Vector2(p.x, p.z), owner.FromPixel(px));
        }
    }

    private static Camera ActiveCamera()
    {
        Camera best = null;
        foreach (var c in Camera.allCameras)
            if (c.targetTexture == null && (best == null || c.depth > best.depth)) best = c;
        return best;
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.mKey.wasPressedThisFrame) SetFull(!FullMapOpen);
        if (FullMapOpen && kb != null && kb.escapeKey.wasPressedThisFrame) SetFull(false);
        var mouse = Mouse.current;
        if (FullMapOpen && mouse != null)
        {
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(fullImage, mouse.position.ReadValue(), null, out var about))
                Zoom(wheel > 0 ? 1.2f : 1f / 1.2f, about);
        }

        var cam = ActiveCamera();
        if (cam == null) return;
        var p3 = cam.transform.position;
        var p = new Vector2(p3.x, p3.z);
        var f = cam.transform.forward;
        var ppx = ToPixel(p);
        var ahead = ToPixel(p + new Vector2(f.x, f.z).normalized * 10f * scale) - ppx;
        float angle = -Mathf.Atan2(ahead.x, -ahead.y) * Mathf.Rad2Deg;  // pixel y runs down

        if (destination.HasValue && (destination.Value - p).magnitude < 15f * scale) { route.Clear(); destination = null; }

        if (!FullMapOpen)
        {
            float spanPx = MiniMetres / data.res;
            miniImage.uvRect = new Rect((ppx.x - spanPx / 2) / data.width, 1f - (ppx.y + spanPx / 2) / data.height,
                                        spanPx / data.width, spanPx / data.height);
            miniArrow.localEulerAngles = new Vector3(0, 0, angle);
            var size = miniImage.rectTransform.rect.size;
            var pts = new List<Vector2>(route.Count);
            foreach (int k in route)
            {
                var q = ToPixel(nodeXZ[k]) - ppx;
                pts.Add(new Vector2(q.x / spanPx * size.x, -q.y / spanPx * size.y));
            }
            miniRoute.SetPoints(pts);
        }
        else
        {
            var r = fullImage.rect;
            Vector2 ToUi(Vector2 px) => new Vector2(r.xMin + px.x / data.width * r.width, r.yMax - px.y / data.height * r.height);
            fullArrow.anchoredPosition = ToUi(ppx);
            fullArrow.localEulerAngles = new Vector3(0, 0, angle);
            // markers and the route keep their size on screen however far the map is zoomed
            fullArrow.localScale = destMark.localScale = Vector3.one / zoom;
            fullRoute.thickness = 5f / zoom;
            destMark.gameObject.SetActive(destination.HasValue);
            if (destination.HasValue) destMark.anchoredPosition = ToUi(ToPixel(destination.Value));
            var pts = new List<Vector2>(route.Count);
            foreach (int k in route) pts.Add(ToUi(ToPixel(nodeXZ[k])));
            fullRoute.SetPoints(pts);
        }
    }

    private void OnDestroy() => FullMapOpen = false;
}
