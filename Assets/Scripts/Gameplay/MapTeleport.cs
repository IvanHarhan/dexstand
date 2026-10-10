using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Quick travel on the region map: buttons in the top-left corner (F2-F6, F8-F10 on
/// a keyboard) put the local player on the main road of a town of the map, and
/// "Машину сюда" (F7) parks the car beside them. Built when the game scene loads,
/// like the test car, so no scene or prefab carries it. Points are scene units of
/// the region export (the bus station is the origin).
/// </summary>
public class MapTeleport : MonoBehaviour
{
    // on the main road of each town (OSM), in scene units; keys for a keyboard
    private static readonly (string label, Vector2 xz, Key key)[] Places =
    {
        ("Виль", new Vector2(2f, 3f), Key.F8),
        ("Брюхермюле", new Vector2(-2061.9f, 752.6f), Key.F9),
        ("Гуммерсбах (Forum)", new Vector2(-374f, -2812f), Key.F2),
        ("Диринггаузен", new Vector2(145f, -1398f), Key.F3),
        ("Альферцхаген", new Vector2(-619.2f, -1494.7f), Key.F4),
        ("Бильштайн", new Vector2(1054.7f, -570.9f), Key.F5),
        // on the A4 itself (a4/ corridor chunks, 2026-09-27)
        ("A4 Энгельскирхен", new Vector2(4434.3f, -715.1f), Key.F6),
        ("A4 Кёльн", new Vector2(10734.1f, -177.9f), Key.F10),
        // 2026-09-28: the places worth a look, in the T menu only
        ("Дом", new Vector2(153.2f, -412.5f), Key.None),
        ("Kaufland", new Vector2(480.2f, -1332.8f), Key.None),
        ("Spielhalle (вокзал)", new Vector2(167.1f, -1414.3f), Key.None),
        ("Merkur Spielothek", new Vector2(-528.7f, -2479.4f), Key.None),
        ("A4 Оверат", new Vector2(6933f, -14.5f), Key.None),
        ("Кёльн Messe", new Vector2(13245.2f, -40.2f), Key.None),
        ("Кёльн Dom / Hbf", new Vector2(13831.1f, 129.6f), Key.None),
        ("Кёльн Neumarkt", new Vector2(14066.7f, 343f), Key.None),
    };

    private GameObject menu;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        // Only the game scene with the region map gets it.
        if (FindFirstObjectByType<NetworkLauncher>() == null || FindFirstObjectByType<MapStreamer>() == null) return;
        new GameObject("MapTeleport").AddComponent<MapTeleport>();
    }

    private void Awake()
    {
        var canvasGo = new GameObject("TeleportUI");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 41;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

        // one button in the corner (for touch), the list itself in a menu that T opens and closes
        AddButton(canvasGo.transform, "Телепорт (T)", new Vector2(16f, -16f), new Vector2(200f, 64f), ToggleMenu);
        menu = new GameObject("TeleportMenu", typeof(RectTransform));
        menu.transform.SetParent(canvasGo.transform, false);
        var mrt = (RectTransform)menu.transform;
        mrt.anchorMin = mrt.anchorMax = mrt.pivot = new Vector2(0.5f, 0.5f);
        int rows = (Places.Length + 2) / 3 + 1;
        mrt.sizeDelta = new Vector2(3 * 300f + 40f, rows * 74f + 40f);
        menu.AddComponent<Image>().color = new Color(0.03f, 0.04f, 0.06f, 0.85f);
        for (int i = 0; i <= Places.Length; i++)
        {
            int index = i;
            var pos = new Vector2(20f + (i % 3) * 300f, -20f - (i / 3) * 74f);
            if (i < Places.Length)
                AddButton(menu.transform, Places[i].label, pos, new Vector2(290f, 64f), () => { Go(index); ToggleMenu(); });
            else
                AddButton(menu.transform, "Машину сюда (F7)", pos, new Vector2(290f, 64f), () => { SummonCar(); ToggleMenu(); });
        }
        menu.SetActive(false);

        if (FindFirstObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }
    }

    private void ToggleMenu()
    {
        menu.SetActive(!menu.activeSelf);
        // the mouse has to reach the list; picking a place (or T again) gives it back to the camera
        Cursor.lockState = menu.activeSelf || Application.isMobilePlatform ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = menu.activeSelf;
    }

    private static void AddButton(Transform parent, string label, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        go.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
        go.AddComponent<Button>().onClick.AddListener(onClick);

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(go.transform, false);
        var lrt = (RectTransform)labelGo.transform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        var text = labelGo.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = label;
        text.raycastTarget = false;
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        for (int i = 0; i < Places.Length; i++)
            if (Places[i].key != Key.None && kb[Places[i].key].wasPressedThisFrame) Go(i);
        if (kb.f7Key.wasPressedThisFrame) SummonCar();
        if (kb.tKey.wasPressedThisFrame) ToggleMenu();
    }

    private static PlayerController LocalPlayer()
    {
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            if (pc.Object != null && pc.Object.IsValid && pc.Object.HasInputAuthority) return pc;
        return null;
    }

    /// <summary>The car, parked beside the player and facing where they look (the host moves it).</summary>
    private static void SummonCar()
    {
        var me = LocalPlayer();
        var car = SimpleCar.Current;
        if (me == null || car == null || car.Object == null || !car.Object.IsValid) return;
        var cam = me.GetComponentInChildren<Camera>();
        car.RPC_Summon(me.transform.position, cam != null ? cam.transform.forward : me.transform.forward);
    }

    private static void Go(int index)
    {
        var me = LocalPlayer();
        if (me == null) return;

        var xz = Places[index].xz * DexstandScale();
        float ground = float.MinValue;
        foreach (var hit in Physics.RaycastAll(new Vector3(xz.x, 2000f, xz.y), Vector3.down, 4000f, ~0,
                                               QueryTriggerInteraction.Ignore))
            if (hit.collider.gameObject.name != "SafetyFloor") ground = Mathf.Max(ground, hit.point.y);
        me.Teleport(new Vector3(xz.x, (ground == float.MinValue ? 0f : ground) + 0.3f, xz.y));
    }

    /// <summary>The points above are for the map at its usual third of real size; the map root carries the scale.</summary>
    private static float DexstandScale()
    {
        var map = GameObject.Find("Map");
        return map != null ? map.transform.localScale.x * 3f : 1f;
    }
}
