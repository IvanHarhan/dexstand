using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draw distance for the region map. The map comes in 500 m cells (objects named
/// "Kind_E_N_q" by the Blender export); Unity already skips what is off screen, and
/// this switches off whole cells that are too far to matter: trees and small props
/// early, ground, buildings and roads at the edge of the fog. Colliders stay on.
/// Sits on the map root; the scene builder adds it.
/// </summary>
public class MapStreamer : MonoBehaviour
{
    [Tooltip("Scene units (the world is a third of real size): 500 = 1.5 km")]
    public float drawDistance = 320f;   // 2026-09-26: cut further, PC was lagging
    public float treeDistance = 110f;
    public float detailDistance = 70f;
    public float mobileDrawDistance = 200f;
    public float mobileTreeDistance = 70f;
    public float mobileDetailDistance = 45f;
    public float checkInterval = 0.25f;
    private const float CityFromX = 12000f, CityScale = 0.55f;

    private class Group
    {
        public readonly List<Renderer> renderers = new List<Renderer>();
        public Bounds bounds;
        public float limit;
        public int state = -1;  // -1: not applied yet — a rescan's groups hold renderers already on and off
    }

    private readonly List<Group> groups = new List<Group>();
    private float nextCheck;
    private Camera lastCamera;

    private void Awake()
    {
        if (Application.isMobilePlatform)
        {
            drawDistance = mobileDrawDistance;
            treeDistance = mobileTreeDistance;
            detailDistance = mobileDetailDistance;
            // the fog was baked for the PC draw distance; match it or the cut-off shows before the haze does
            RenderSettings.fogStartDistance = drawDistance * 0.45f;
            RenderSettings.fogEndDistance = drawDistance;
        }

        // the sky's ambient light is not baked (the scene is built in batch mode): take it from the sky now
        DynamicGI.UpdateEnvironment();
        Scan();
    }

    /// <summary>Rebuilds the distance groups from whatever renderers exist now. MapTileStreamer calls this
    /// after loading or unloading a tile, since those renderers did not exist at Awake.</summary>
    public void Rescan() => Scan();

    private void Scan()
    {
        groups.Clear();
        var byKey = new Dictionary<string, Group>();
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            // Collision meshes, hidden by the scene builder — by their name, not by being switched off: this
            // class switches far cells off itself, and a rescan (every tile MapTileStreamer loads) dropped
            // those for good. Past the first ring round the start the map never drew again, only its colliders.
            string lower = r.name.ToLowerInvariant();  // the suffixes DexstandMapImport hides
            if (lower.EndsWith("_col") || lower.EndsWith("_collider") || lower.EndsWith("_collision")) continue;
            string n = r.name;
            int us = n.IndexOf('_');
            if (us < 0) continue;
            string kind = n.Substring(0, us);
            float limit = kind == "Trees" ? treeDistance
                        : kind == "Props" || kind == "Details" || kind == "Marks" || kind == "Barriers" || kind == "Gutters" ? detailDistance
                        : drawDistance;
            string key = limit + "|" + n.Substring(us + 1);
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new Group { limit = limit, bounds = r.bounds };
                byKey[key] = g;
                groups.Add(g);
            }
            g.renderers.Add(r);
            g.bounds.Encapsulate(r.bounds);
        }
        nextCheck = 0f;  // the new groups get their state on the next frame, not a quarter second later
    }

    private void Update()
    {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + checkInterval;

        var cam = ActiveCamera();
        if (cam == null) return;
        if (cam != lastCamera)
        {
            // the fog hides the edge; nothing past it is worth drawing
            cam.farClipPlane = drawDistance * 1.05f;
            lastCamera = cam;
        }

        var p = cam.transform.position;
        // Köln (scene x past ~12 km: the dense centre, 2026-09-28): everything drawn only about half as far
        float k = p.x > CityFromX ? CityScale : 1f;
        foreach (var g in groups)
        {
            var c = g.bounds.center;
            var e = g.bounds.extents;
            float dx = Mathf.Max(0f, Mathf.Abs(p.x - c.x) - e.x);
            float dz = Mathf.Max(0f, Mathf.Abs(p.z - c.z) - e.z);
            bool on = dx * dx + dz * dz < g.limit * g.limit * k * k;
            if ((on ? 1 : 0) == g.state) continue;
            g.state = on ? 1 : 0;
            foreach (var r in g.renderers)
                if (r != null) r.enabled = on;
        }
    }

    /// <summary>Whichever camera draws last: the player's, or the car's chase camera while driving.</summary>
    private static Camera ActiveCamera()
    {
        Camera best = null;
        foreach (var c in Camera.allCameras)
            if (c.targetTexture == null && (best == null || c.depth > best.depth)) best = c;
        return best;
    }
}
