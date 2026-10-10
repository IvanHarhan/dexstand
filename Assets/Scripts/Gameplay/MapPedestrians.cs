using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// People on the pavements of the region map: a few walkers around the camera, following the OSM road graph that
/// MapMinimap loads, a pavement's width to the side of the carriageway, turning at junctions. They only start
/// where that line lands on a pavement collider ("Walks*"), so they stay in the towns, and they vanish when the
/// camera leaves them. The body is the patrol target's character (walk animation on "Speed") without its target
/// logic. Sits on the map root; the scene builder adds it with the prefab.
/// </summary>
public class MapPedestrians : MonoBehaviour
{
    public GameObject prefab;
    [Tooltip("MPFB people (Assets/Models/NPC); each walker is one of them. Empty: the target's character.")]
    public GameObject[] models;
    public RuntimeAnimatorController controller;
    public int count = 16;
    public int mobileCount = 8;
    public float spawnMin = 8f, spawnMax = 50f, despawn = 70f;    // scene units (24-150 m: close enough to be seen)
    public float walkSpeed = 1.3f;                                   // m/s
    public float side = 4.2f;                                        // m from the road's centre line

    private class Walker { public GameObject go; public Animator anim; public Pedestrian body; public int from, to; public float t, sideSign, speed, side; }

    private readonly List<Walker> walkers = new List<Walker>();
    public int Count => walkers.Count;
    public int Tries { get; private set; }
    public string LastGround { get; private set; } = "";
    private GameObject template;
    private Transform people;
    private float s = 1f / 3f;
    private float nextSpawn;
    private static readonly int SpeedId = Animator.StringToHash("Speed");

    private void Start()
    {
        if (prefab == null) { enabled = false; return; }
        s = transform.lossyScale.x;
        if (Application.isMobilePlatform) count = mobileCount;
        // the walkers live under a root of their own: under the map (scaled to a third) they came out 60 cm tall
        people = new GameObject("Pedestrians").transform;
        // an inactive holder keeps the copy's scripts from waking before the target logic is taken off
        var holder = new GameObject("PedestrianTemplate");
        holder.SetActive(false);
        holder.transform.SetParent(people, false);
        template = Instantiate(prefab, holder.transform);
        foreach (var mb in template.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(mb);
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
        var nodes = MapMinimap.RoadNodes;
        var next = MapMinimap.RoadNext;
        var cam = ActiveCamera();
        if (nodes == null || cam == null || template == null) return;
        var cp = cam.transform.position;

        for (int i = walkers.Count - 1; i >= 0; i--)
        {
            // the dead lie where they fell and clear themselves away (Pedestrian); a new walker takes the place
            if (walkers[i].go == null || walkers[i].body.Dead) walkers.RemoveAt(i);
            else if ((walkers[i].go.transform.position - cp).sqrMagnitude > despawn * despawn) { Destroy(walkers[i].go); walkers.RemoveAt(i); }
        }
        if (walkers.Count < count && Time.time >= nextSpawn)
        {
            nextSpawn = Time.time + 0.2f;
            TrySpawn(nodes, next, new Vector2(cp.x, cp.z));
        }
        foreach (var w in walkers)
            if (!w.body.Dead) Walk(w, nodes, next, Time.deltaTime);
    }

    /// <summary>A walkable spot beside the road: pavement, verge or car park — not the carriageway, not a roof.</summary>
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    /// <param name="nearY">the walker's height now: a short ray round it instead of one down from the sky</param>
    private Vector3 Point(Vector2[] nodes, int a, int b, float t, float sideSign, out bool onPavement, float nearY = float.NaN,
                          float offset = -1f)
    {
        var d = (nodes[b] - nodes[a]).normalized;
        var p2 = Vector2.Lerp(nodes[a], nodes[b], t) + new Vector2(d.y, -d.x) * sideSign * (offset < 0f ? side : offset) * s;
        float y = float.MinValue;
        onPavement = false;
        bool near = !float.IsNaN(nearY);
        float top = near ? nearY + 1.5f : 5000f, length = near ? 4f : 10000f;
        int n = Physics.RaycastNonAlloc(new Vector3(p2.x, top, p2.y), Vector3.down, Hits, length, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = Hits[i];
            if (!h.collider.name.EndsWith("_col") || h.point.y <= y) continue;
            y = h.point.y;
            string cn = h.collider.name;
            onPavement = cn.StartsWith("Walks") || cn.StartsWith("GroundCol") || cn.StartsWith("Parking");
            LastGround = cn;
        }
        return new Vector3(p2.x, y == float.MinValue ? (near ? nearY : 0f) : y, p2.y);
    }

    // road nodes by 30-unit cell, to pick spawn points near the camera rather than anywhere on the map
    private Dictionary<Vector2Int, List<int>> nodeGrid;
    private const float GridCell = 30f;

    private static readonly Collider[] Near = new Collider[16];

    /// <summary>In a town: a building within 25 m.</summary>
    private bool InTown(Vector3 p)
    {
        int n = Physics.OverlapSphereNonAlloc(p, 25f * s, Near, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (Near[i].name.StartsWith("BuildCol")) return true;
        return false;
    }

    private void TrySpawn(Vector2[] nodes, List<(int to, float w)>[] next, Vector2 c2)
    {
        if (nodeGrid == null)
        {
            nodeGrid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < nodes.Length; i++)
            {
                var k = new Vector2Int(Mathf.FloorToInt(nodes[i].x / GridCell), Mathf.FloorToInt(nodes[i].y / GridCell));
                if (!nodeGrid.TryGetValue(k, out var l)) nodeGrid[k] = l = new List<int>();
                l.Add(i);
            }
        }
        int reach = Mathf.CeilToInt(spawnMax / GridCell);
        var ck = new Vector2Int(Mathf.FloorToInt(c2.x / GridCell), Mathf.FloorToInt(c2.y / GridCell));
        for (int tries = 0; tries < 12; tries++)
        {
            var key = ck + new Vector2Int(Random.Range(-reach, reach + 1), Random.Range(-reach, reach + 1));
            if (!nodeGrid.TryGetValue(key, out var cellNodes)) continue;
            int a = cellNodes[Random.Range(0, cellNodes.Count)];
            float d = Vector2.Distance(nodes[a], c2);
            if (d < spawnMin || d > spawnMax || next[a].Count == 0) continue;
            int b = next[a][Random.Range(0, next[a].Count)].to;
            Tries++;
            float sideSign = Random.value < 0.5f ? -1f : 1f;
            var p = Point(nodes, a, b, 0.5f, sideSign, out bool pavement);
            if (!pavement || !InTown(p)) continue;
            GameObject go;
            if (models != null && models.Length > 0 && controller != null)
            {
                // an MPFB person: metres, so a third of a unit per metre here, walking on the target's clips
                go = Instantiate(models[Random.Range(0, models.Length)], people);
                go.transform.localScale = Vector3.one * s * Random.Range(0.96f, 1.04f);
                var an = go.GetComponentInChildren<Animator>() ?? go.AddComponent<Animator>();
                an.runtimeAnimatorController = controller;
                an.applyRootMotion = false;
                an.cullingMode = AnimatorCullingMode.CullCompletely;
                var cap = go.AddComponent<CapsuleCollider>();
                cap.center = new Vector3(0f, 0.9f, 0f);
                cap.height = 1.75f;
                cap.radius = 0.25f;
            }
            else
            {
                go = Instantiate(template, people);
                go.transform.localScale = template.transform.localScale * Random.Range(0.92f, 1.08f);
            }
            go.transform.position = p;
            go.SetActive(true);
            var anim = go.GetComponentInChildren<Animator>();
            walkers.Add(new Walker { go = go, anim = anim, body = go.AddComponent<Pedestrian>(), from = a, to = b, t = 0.5f, sideSign = sideSign, side = side,
                                     speed = walkSpeed * Random.Range(0.8f, 1.2f) });
            return;
        }
    }

    private void Walk(Walker w, Vector2[] nodes, List<(int to, float w)>[] next, float dt)
    {
        float len = Mathf.Max(Vector2.Distance(nodes[w.from], nodes[w.to]), 0.01f);
        w.t += w.speed * s * dt / len;
        if (w.t >= 1f)
        {
            // on at the junction; every walk leads both ways, so a dead end just turns them round
            var options = next[w.to];
            int pick = options.Count > 0 ? options[Random.Range(0, options.Count)].to : w.from;
            if (pick == w.from && options.Count > 1) pick = options[(options.FindIndex(o => o.to == pick) + 1) % options.Count].to;
            w.from = w.to; w.to = pick; w.t = 0f;
        }
        var p = Point(nodes, w.from, w.to, w.t, w.sideSign, out _, w.go.transform.position.y, w.side);
        // A fixed distance from the centre line is still carriageway on a wide street: out onto the pavement
        // step by step while the ground underfoot is road, and back in slowly where the street narrows again.
        if (LastGround.StartsWith("Roads")) w.side = Mathf.Min(w.side + 4f * dt, side * 2.5f);
        else w.side = Mathf.MoveTowards(w.side, side, 0.2f * dt);
        var dir = p - w.go.transform.position;
        dir.y = 0f;
        w.go.transform.position = p;
        if (dir.sqrMagnitude > 1e-6f)
            w.go.transform.rotation = Quaternion.Slerp(w.go.transform.rotation, Quaternion.LookRotation(dir), 1f - Mathf.Exp(-6f * dt));
        if (w.anim != null) w.anim.SetFloat(SpeedId, w.speed);
    }
}
