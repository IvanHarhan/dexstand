using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Parked cars on the region map: in the stalls of the car parks and now and then at the kerb of residential
/// streets (spots from Blender's build_streets.py, Resources/Map/parked.json: Blender metres around the bus
/// station + heading, Unity = (-x, z, -y) * map scale). Only the ones near the camera exist; they come from a pool,
/// a random model of Resources/Cars each (turned to face +Z, sized to a real car, wheels on the ground, a box
/// collider). Built when the game scene loads, like MapTeleport.
/// </summary>
public class MapParkedCars : MonoBehaviour
{
    public float radius = 90f;          // scene units
    public float bucket = 40f;
    public float checkInterval = 0.5f;

    private struct Spot { public Vector3 pos; public float yaw; public int model; public float length; }

    private readonly List<GameObject> templates = new List<GameObject>();
    private readonly Dictionary<Vector2Int, List<int>> buckets = new Dictionary<Vector2Int, List<int>>();
    private readonly Dictionary<int, GameObject> live = new Dictionary<int, GameObject>();
    private readonly List<Stack<GameObject>> pools = new List<Stack<GameObject>>();
    private Spot[] spots;
    private float next;
    private int taken = -1;   // the spot whose car the player's car has become (SimpleCar.RPC_Take)

    public static MapParkedCars Instance { get; private set; }

    /// <summary>The parked car within reach of p, if any: its spot, pose and model name (a Resources/Cars file).</summary>
    public bool Nearest(Vector3 p, float reach, out int spot, out Vector3 pos, out Quaternion rot, out string model)
    {
        spot = -1; pos = default; rot = default; model = null;
        float best = reach * reach;
        foreach (var kv in live)
        {
            float d = (kv.Value.transform.position - p).sqrMagnitude;
            if (d < best) { best = d; spot = kv.Key; }
        }
        if (spot < 0) return false;
        // where it is now: a rammed car is no longer in its stall
        var car = live[spot].transform;
        pos = car.position;
        rot = Quaternion.Euler(0f, car.eulerAngles.y, 0f);
        model = templates[spots[spot].model].name.Substring("Car_".Length);
        return true;
    }

    /// <summary>That spot's car has been taken over (and the one taken before is back); -1 frees it.</summary>
    public void SetTaken(int spot)
    {
        if (spot == taken) return;
        if (spots != null && spot >= 0 && spot < spots.Length && live.TryGetValue(spot, out var go))
        {
            go.SetActive(false);
            pools[spots[spot].model].Push(go);
            live.Remove(spot);
        }
        taken = spot;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<NetworkLauncher>() == null || FindFirstObjectByType<MapStreamer>() == null) return;
        if (Resources.Load<TextAsset>("Map/parked") == null) return;
        new GameObject("MapParkedCars").AddComponent<MapParkedCars>();
    }

    [System.Serializable] private class Data { public float[] cars; }

    private void Awake()
    {
        Instance = this;
        var map = GameObject.Find("Map");
        float s = map != null ? map.transform.lossyScale.x : 1f / 3f;
        foreach (var prefab in Resources.LoadAll<GameObject>("Cars"))
        {
            var t = Template(prefab);
            if (t == null) continue;
            templates.Add(t);
            pools.Add(new Stack<GameObject>());
        }
        if (templates.Count == 0) { enabled = false; return; }

        // car parks and kerbs (build_streets), and cars on the drives of houses (export_region, one file per part)
        var all = new List<float>();
        foreach (var file in new[] { "Map/parked", "Map/parked_drive_0", "Map/parked_drive_1", "Map/parked_drive_2" })
        {
            var ta = Resources.Load<TextAsset>(file);
            if (ta != null) all.AddRange(JsonUtility.FromJson<Data>(ta.text).cars);
        }
        var d = all.ToArray();
        var rnd = new System.Random(11);
        spots = new Spot[d.Length / 4];
        for (int i = 0; i < spots.Length; i++)
        {
            float x = d[4 * i], y = d[4 * i + 1], z = d[4 * i + 2], h = d[4 * i + 3];
            var dir = new Vector2(-Mathf.Cos(h), -Mathf.Sin(h));   // Blender xy -> Unity xz
            spots[i] = new Spot
            {
                pos = new Vector3(-x, z, -y) * s,
                yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg,
                model = rnd.Next(templates.Count),
            };
            spots[i].length = CarSpecs.Get(templates[spots[i].model].name.Substring("Car_".Length)).length * s;
            var key = Key(spots[i].pos);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
            list.Add(i);
        }
    }

    private Vector2Int Key(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / bucket), Mathf.FloorToInt(p.z / bucket));

    private GameObject Template(GameObject prefab) => Template(prefab, transform);

    /// <summary>An inactive copy of the model facing +Z, 1 unit long, wheels on y = 0, with a box collider.</summary>
    public static GameObject Template(GameObject prefab, Transform parent)
    {
        var root = new GameObject("Car_" + prefab.name);
        root.SetActive(false);
        root.transform.SetParent(parent, false);
        var model = Object.Instantiate(prefab, root.transform);
        foreach (var c in model.GetComponentsInChildren<Collider>()) Object.Destroy(c);
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("spoiler")) t.gameObject.SetActive(false);

        var b = CarBounds(root, model);
        if (b.size == Vector3.zero) { Object.Destroy(root); return null; }
        // the front wheels (Kenney names them) say which way is forward; else the longer horizontal side
        Vector3 front = Vector3.zero, back = Vector3.zero; int nf = 0, nb = 0;
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("wheel")) continue;
            var c = root.transform.InverseTransformPoint(CarBounds(root, t.gameObject).center);
            if (t.name.Contains("front")) { front += c; nf++; } else { back += c; nb++; }
        }
        Vector3 ahead = nf > 0 && nb > 0 ? Vector3.ProjectOnPlane(front / nf - back / nb, Vector3.up)
                      : (b.size.x > b.size.z ? Vector3.right : Vector3.forward);
        if (ahead.sqrMagnitude > 1e-6f)
            model.transform.localRotation = Quaternion.FromToRotation(ahead.normalized, Vector3.forward) * model.transform.localRotation;
        b = CarBounds(root, model);
        model.transform.localScale /= Mathf.Max(b.size.z, 1e-3f);
        b = CarBounds(root, model);
        model.transform.localPosition -= new Vector3(b.center.x, b.min.y, b.center.z);
        b = CarBounds(root, model);
        var box = root.AddComponent<BoxCollider>();
        box.center = b.center;
        box.size = new Vector3(b.size.x * 0.88f, b.size.y, b.size.z);  // without the mirrors
        root.AddComponent<LooseCar>();
        return root;
    }

    private static Bounds CarBounds(GameObject root, GameObject part)
    {
        var rs = part.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds();
        Bounds b = default; bool any = false;
        foreach (var r in rs)
        {
            if (r is ParticleSystemRenderer) continue;
            var lb = r.localBounds;
            for (int k = 0; k < 8; k++)
            {
                var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) * 2 - 1, ((k >> 1) & 1) * 2 - 1, ((k >> 2) & 1) * 2 - 1));
                var p = root.transform.InverseTransformPoint(r.transform.TransformPoint(corner));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        return b;
    }

    private void Update()
    {
        if (Time.unscaledTime < next || spots == null) return;
        next = Time.unscaledTime + checkInterval;
        Camera cam = null;
        foreach (var c in Camera.allCameras)
            if (c.targetTexture == null && (cam == null || c.depth > cam.depth)) cam = c;
        if (cam == null) return;
        var p = cam.transform.position;
        float r2 = radius * radius;

        var gone = new List<int>();
        foreach (var kv in live)
            if ((spots[kv.Key].pos - p).sqrMagnitude > r2 * 1.2f) gone.Add(kv.Key);
        foreach (int i in gone)
        {
            var go = live[i];
            go.SetActive(false);
            pools[spots[i].model].Push(go);
            live.Remove(i);
        }

        var k0 = Key(p - new Vector3(radius, 0, radius));
        var k1 = Key(p + new Vector3(radius, 0, radius));
        for (int kx = k0.x; kx <= k1.x; kx++)
            for (int kz = k0.y; kz <= k1.y; kz++)
            {
                if (!buckets.TryGetValue(new Vector2Int(kx, kz), out var list)) continue;
                foreach (int i in list)
                {
                    if (i == taken || live.ContainsKey(i) || (spots[i].pos - p).sqrMagnitude > r2) continue;
                    var sp = spots[i];
                    var pool = pools[sp.model];
                    var go = pool.Count > 0 ? pool.Pop() : Instantiate(templates[sp.model], transform);
                    go.GetComponent<LooseCar>().Settle();
                    go.transform.SetPositionAndRotation(sp.pos, Quaternion.Euler(0f, sp.yaw, 0f));
                    go.transform.localScale = Vector3.one * sp.length;
                    go.SetActive(true);
                    live[i] = go;
                }
            }
    }
}
