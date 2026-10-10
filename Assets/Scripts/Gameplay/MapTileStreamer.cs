using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loads and unloads the region's 500 m tiles by distance from the camera, instead of keeping all 138
/// resident and just hiding their renderers (MapStreamer): the phone build ran out of memory that way.
/// Tile prefabs live in Resources/Map/Tiles (DexstandSceneBuilder.StreamTiles split them out of the built
/// scene, materials/colliders/static flags already applied); Resources/Map/tiles_manifest.json lists each
/// one's name and world bounds. Sits on the map root; the scene builder adds it.
/// </summary>
public class MapTileStreamer : MonoBehaviour
{
    [Tooltip("Scene units; generous on PC so nothing pops, small on phones to keep memory down")]
    public float distance = 1000f;
    public float mobileDistance = 280f;
    public float unloadFactor = 1.3f;
    private float unloadDistance;
    private const int MaxLoading = 2;

    [System.Serializable] private class Entry { public string name; public float x, y, z, r; }
    [System.Serializable] private class Manifest { public Entry[] tiles; }

    private class Tile
    {
        public string name;
        public Vector3 center;
        public GameObject instance;
        public bool loading, failed;
        public AssetBundle bundle;
    }

    // In a built game the tiles are asset bundles (StreamingAssets/tiles, one per tile plus "shared" for the
    // shaders, common materials and textures; DexstandBuild makes them): all of them in Resources made one
    // resources.assets past 4 GB once Köln came in, and the build crashed writing it (2026-09-28).
    private static string BundleDir => System.IO.Path.Combine(Application.streamingAssetsPath, "tiles");
    private bool useBundles;
    private AssetBundle shared;

    private readonly List<Tile> tiles = new List<Tile>();
    private MapStreamer streamer;
    private float nextUnloadCheck;

    /// <summary>Where ground has to exist: this screen's camera, and on the host everyone it simulates.</summary>
    private readonly List<Vector3> anchors = new List<Vector3>();
    private PlayerController[] players = new PlayerController[0];
    private float nextPlayerScan;

    private void Start()
    {
        var json = Resources.Load<TextAsset>("Map/tiles_manifest");
        if (json == null) { enabled = false; return; }
        if (Application.isMobilePlatform) distance = mobileDistance;
        unloadDistance = distance * unloadFactor;

        var manifest = JsonUtility.FromJson<Manifest>(json.text);
        foreach (var e in manifest.tiles)
            tiles.Add(new Tile { name = e.name, center = new Vector3(e.x, e.y, e.z) });

        streamer = GetComponent<MapStreamer>();
        string sharedPath = System.IO.Path.Combine(BundleDir, "shared");
        useBundles = !Application.isEditor && System.IO.File.Exists(sharedPath);
        if (useBundles) shared = AssetBundle.LoadFromFile(sharedPath);
    }

    private System.Collections.IEnumerator LoadTile(Tile t)
    {
        t.loading = true;
        GameObject prefab = null;
        if (useBundles)
        {
            var br = AssetBundle.LoadFromFileAsync(System.IO.Path.Combine(BundleDir, t.name.ToLowerInvariant()));
            yield return br;
            t.bundle = br.assetBundle;
            if (t.bundle != null)
            {
                var ar = t.bundle.LoadAssetAsync<GameObject>(t.name);
                yield return ar;
                prefab = ar.asset as GameObject;
            }
        }
        else
        {
            var rr = Resources.LoadAsync<GameObject>("Map/Tiles/" + t.name);
            yield return rr;
            prefab = rr.asset as GameObject;
        }
        t.loading = false;
        if (prefab == null) { t.failed = true; yield break; }
        t.instance = Instantiate(prefab, transform, false);
        if (streamer != null) streamer.Rescan();
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
        var cam = ActiveCamera();
        if (cam == null) return;
        CollectAnchors(cam.transform.position);
        float d2 = distance * distance;
        bool changed = false;

        if (Time.unscaledTime >= nextUnloadCheck)
        {
            nextUnloadCheck = Time.unscaledTime + 1f;
            float ud2 = unloadDistance * unloadDistance;
            foreach (var t in tiles)
            {
                if (t.instance == null || t.loading || Near(t.center, ud2)) continue;
                DestroyImmediate(t.instance);  // now, so its bundle can go with it
                t.instance = null;
                if (t.bundle != null) { t.bundle.Unload(true); t.bundle = null; }
                changed = true;
            }
            if (changed)
            {
                if (!useBundles) Resources.UnloadUnusedAssets();
                if (streamer != null) streamer.Rescan();
            }
        }

        // Nearest first, a couple at a time. All of them at once, in the manifest's order, queued a hundred tiles
        // on leaving town and the one under the car came last: the road ahead stayed empty for the whole drive.
        int loading = 0;
        foreach (var t in tiles) if (t.loading) loading++;
        while (loading < MaxLoading)
        {
            Tile best = null;
            float bestD = d2;
            foreach (var t in tiles)
            {
                if (t.instance != null || t.loading || t.failed) continue;
                float d = Nearest(t.center);
                if (d <= bestD) { bestD = d; best = t; }
            }
            if (best == null) break;
            StartCoroutine(LoadTile(best));
            loading++;
        }
    }

    /// <summary>
    /// The host runs every player's and the car's physics, so the ground under them has to be loaded there even
    /// when they are far from its own camera — without it they fell through the map on the host and everyone saw
    /// them jump about or vanish.
    /// </summary>
    private void CollectAnchors(Vector3 camera)
    {
        anchors.Clear();
        anchors.Add(camera);

        if (Time.unscaledTime >= nextPlayerScan)
        {
            nextPlayerScan = Time.unscaledTime + 1f;
            players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        }
        foreach (var pc in players)
            if (pc != null && pc.Object != null && pc.Object.IsValid && pc.Object.HasStateAuthority)
                anchors.Add(pc.transform.position);

        var car = SimpleCar.Current;
        if (car != null && car.Object != null && car.Object.IsValid && car.Object.HasStateAuthority)
            anchors.Add(car.transform.position);
    }

    private bool Near(Vector3 center, float range2) => Nearest(center) <= range2;

    /// <summary>Squared distance to the closest anchor.</summary>
    private float Nearest(Vector3 center)
    {
        float best = float.MaxValue;
        foreach (var a in anchors) best = Mathf.Min(best, (center - a).sqrMagnitude);
        return best;
    }
}
