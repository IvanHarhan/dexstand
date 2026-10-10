using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Reflections for shop glass, windows and car paint, which otherwise mirrored only the sky. One probe follows the
/// camera and is re-rendered a face per frame every few seconds; box-projected ones sit at the big glass fronts
/// (Rewe, the bus station) and are rendered once the streamed tiles round them are in, then every couple of minutes
/// for the time of day. PC only. Puts itself on the map root, so the scene needs no rebuild.
/// </summary>
public class MapReflections : MonoBehaviour
{
    // scene x, z (as DexstandCarTest's FpsSpots) and box size, in scene units (a unit is 3 m)
    private static readonly (string name, Vector2 at, Vector3 size)[] Spots =
    {
        ("Rewe", new Vector2(18.6f, 17f), new Vector3(30f, 10f, 24f)),
        ("Busbahnhof", new Vector2(2f, 3f), new Vector3(34f, 10f, 24f)),
    };
    private const float FollowEvery = 4f, SpotEvery = 120f, SpotReach = 80f, SpotSettle = 3f;

    private ReflectionProbe follow;
    private float followAt;
    private ReflectionProbe[] spots;
    private float[] spotAt, nearSince;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (Application.isMobilePlatform) return;
        var map = GameObject.Find("Map");
        if (map != null && map.GetComponent<MapReflections>() == null) map.AddComponent<MapReflections>();
    }

    private void Start()
    {
        follow = Probe("ReflectionFollow", Vector3.zero, Vector3.one * 700f, 0, false);
        follow.farClipPlane = 150f;
        spots = new ReflectionProbe[Spots.Length];
        spotAt = new float[Spots.Length];
        nearSince = new float[Spots.Length];
        for (int i = 0; i < Spots.Length; i++)
        {
            var p = new Vector3(Spots[i].at.x, 0f, Spots[i].at.y);
            spots[i] = Probe("Reflection" + Spots[i].name, p, Spots[i].size, 1, true);
            spots[i].farClipPlane = 100f;
            spotAt[i] = float.NegativeInfinity;
            nearSince[i] = -1f;
        }
    }

    private ReflectionProbe Probe(string name, Vector3 at, Vector3 size, int importance, bool box)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.position = at;
        var p = go.AddComponent<ReflectionProbe>();
        p.mode = ReflectionProbeMode.Realtime;
        p.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        p.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
        p.resolution = box ? 256 : 128;
        p.hdr = true;
        p.size = size / transform.lossyScale.x;  // the probe's box is in its own (map-scaled) space
        p.boxProjection = box;
        p.importance = importance;
        p.blendDistance = box ? 4f : 0f;
        p.clearFlags = ReflectionProbeClearFlags.Skybox;
        p.shadowDistance = 0f;
        return p;
    }

    private void Update()
    {
        Camera cam = null;  // the player's, or the car's chase camera while driving (as MapTrees)
        foreach (var c in Camera.allCameras)
            if (c.targetTexture == null && (cam == null || c.depth > cam.depth)) cam = c;
        if (cam == null) return;
        var eye = cam.transform.position;
        if (Time.time - followAt > FollowEvery)
        {
            followAt = Time.time;
            follow.transform.position = eye;
            follow.RenderProbe();
        }
        for (int i = 0; i < spots.Length; i++)
        {
            var d = spots[i].transform.position - eye;
            bool near = d.x * d.x + d.z * d.z < SpotReach * SpotReach;
            if (!near) { nearSince[i] = -1f; continue; }
            if (nearSince[i] < 0f) nearSince[i] = Time.time;
            // wait for the tiles round it to stream in before the first shot
            if (Time.time - nearSince[i] > SpotSettle && Time.time - spotAt[i] > SpotEvery)
            {
                spotAt[i] = Time.time;
                var p = spots[i].transform.position;
                p.y = Ground(p) + 0.7f;  // eye height; the ground wasn't streamed in at Start
                spots[i].transform.position = p;
                spots[i].RenderProbe();
            }
        }
    }

    private static float Ground(Vector3 p)
    {
        float best = float.MinValue;
        foreach (var h in Physics.RaycastAll(new Vector3(p.x, 5000f, p.z), Vector3.down, 10000f, ~0, QueryTriggerInteraction.Ignore))
            if (h.collider.name.EndsWith("_col")) best = Mathf.Max(best, h.point.y);
        return best == float.MinValue ? 0f : best;
    }
}
