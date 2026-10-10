using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A little traffic on the region map: a few cars (models of Resources/Cars) driving the OSM road graph that
/// MapMinimap loads, on the right-hand side, one-way streets respected. They appear out of sight around the camera and
/// vanish when it leaves them, or when they are shut in for good.
///
/// Each one steers like a car: it chases a point a little way down its lane (pure pursuit) and turns no tighter than
/// its wheels allow, so it takes a bend in an arc instead of snapping round the road's corner points. It keeps a
/// time gap to whatever is ahead in its lane (the player included) and eases off for it, slows for bends, and at a
/// junction waits until the car already crossing it is through. It sits on the road nose up or down with the slope,
/// and its wheels turn.
/// Kinematic: the road is found by rays down, they never skid. Built when the game scene loads, like MapTeleport.
/// </summary>
public class MapTraffic : MonoBehaviour
{
    public int count = 4;
    public int mobileCount = 2;
    public float spawnMin = 60f, spawnMax = 150f, despawn = 200f;  // scene units
    public float speedKmh = 40f;

    // real metres and seconds; times the map scale where they meet the scene
    private const float LaneOffset = 1.4f;       // from the road's middle to the lane's
    private const float MaxSteer = 35f;           // degrees at the front wheels
    private const float TimeGap = 1.6f;           // seconds behind whatever is ahead
    private const float StandGap = 4f;            // metres left to it when stopped
    private const float JunctionReach = 16f;      // metres from a junction where a car asks for it
    private const float StopLine = 7f;            // metres short of the junction's middle where it waits

    private class Car
    {
        public GameObject go; public Collider body; public LooseCar loose;
        public int from, to; public float t;      // the chased point: on the lane from node `from` to `to`
        public float side;                        // lane offset, metres right of the road's middle (negative: pulled out)
        public Vector3 pos; public float yaw;     // where the car itself is (yaw in radians, 0 = +z)
        public float speed, cruise;               // scene units/s
        public float length, wheelbase;           // scene units
        public float blockedFor, roundUntil;
        public int junction = -1;                 // the junction node it holds, if any
        public float waited;                      // seconds stood at a junction line
        public Transform[] wheels; public Quaternion[] wheelRest; public Vector3[] wheelCentre; public float wheelRadius, wheelTurn;
    }

    private readonly List<Car> cars = new List<Car>();
    private readonly List<GameObject> templates = new List<GameObject>();
    private readonly Dictionary<int, Car> junctions = new Dictionary<int, Car>();
    private float s = 1f / 3f;
    private Transform parked;
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<NetworkLauncher>() == null || FindFirstObjectByType<MapStreamer>() == null) return;
        new GameObject("MapTraffic").AddComponent<MapTraffic>();
    }

    private void Start()
    {
        var map = GameObject.Find("Map");
        if (map != null) s = map.transform.lossyScale.x;
        if (Application.isMobilePlatform) count = mobileCount;
        foreach (var prefab in Resources.LoadAll<GameObject>("Cars"))
        {
            var t = MapParkedCars.Template(prefab, transform);
            if (t != null) templates.Add(t);
        }
        if (templates.Count == 0) enabled = false;
        var pc = FindFirstObjectByType<MapParkedCars>();
        if (pc != null) parked = pc.transform;
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
        if (nodes == null || cam == null) return;
        var cp = cam.transform.position;
        var c2 = new Vector2(cp.x, cp.z);

        // out of range: gone once nobody sees it go, or well past the range whatever
        for (int i = cars.Count - 1; i >= 0; i--)
        {
            float d2 = (cars[i].pos - cp).sqrMagnitude;
            if (d2 > despawn * despawn && (!Visible(cars[i].pos) || d2 > 2.25f * despawn * despawn)) Remove(cars[i]);
        }
        cars.RemoveAll(c => c.go == null);
        if (cars.Count < count) TrySpawn(nodes, next, c2);

        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        foreach (var car in cars) Drive(car, nodes, next, dt);
        cars.RemoveAll(c => c.go == null);
    }

    private void Remove(Car car)
    {
        if (car.junction >= 0 && junctions.TryGetValue(car.junction, out var holder) && holder == car) junctions.Remove(car.junction);
        car.junction = -1;
        if (car.go != null) Destroy(car.go);
        car.go = null;
    }

    /// <summary>For the test drives: junctions entered so far.</summary>
    public static int JunctionsTaken;

    /// <summary>For the test drives: each car's place, heading (degrees), speed (scene units/s) and what holds it up.</summary>
    public List<(Vector3 pos, float yaw, float speed, float length, bool waiting, bool blocked, GameObject go)> Snapshot()
    {
        var list = new List<(Vector3, float, float, float, bool, bool, GameObject)>();
        foreach (var c in cars)
            if (c.go != null) list.Add((c.pos, c.yaw * Mathf.Rad2Deg, c.speed, c.length, c.waited > 0f, c.blockedFor > 0f, c.go));
        return list;
    }

    private static bool Visible(Vector3 p)
    {
        var cam = ActiveCamera();
        if (cam == null) return false;
        var v = cam.WorldToViewportPoint(p);
        return v.z > 0f && v.x > -0.1f && v.x < 1.1f && v.y > -0.1f && v.y < 1.1f;
    }

    private void TrySpawn(Vector2[] nodes, List<(int to, float w)>[] next, Vector2 c2)
    {
        for (int tries = 0; tries < 20; tries++)
        {
            int a = Random.Range(0, nodes.Length);
            float d = Vector2.Distance(nodes[a], c2);
            if (d < spawnMin || d > spawnMax || next[a].Count == 0) continue;
            if (MapMinimap.RoadThrough != null && !MapMinimap.RoadThrough[a]) continue;  // main roads only
            int b = next[a][Random.Range(0, next[a].Count)].to;
            var dir = (nodes[b] - nodes[a]).normalized;
            var start = nodes[a] + new Vector2(dir.y, -dir.x) * LaneOffset * s;
            // not into another car
            bool taken = false;
            foreach (var o in cars)
                if (Vector2.Distance(new Vector2(o.pos.x, o.pos.z), start) < 12f * s) taken = true;
            if (taken) continue;

            var template = templates[Random.Range(0, templates.Count)];
            var spec = CarSpecs.Get(template.name.Substring("Car_".Length));
            var car = new Car
            {
                from = a, to = b, t = 0f, side = LaneOffset,
                yaw = Mathf.Atan2(dir.x, dir.y),
                cruise = speedKmh * Random.Range(0.85f, 1.15f) / 3.6f * s,
                length = spec.length * s, wheelbase = spec.length * 0.6f * s,
            };
            car.go = Instantiate(template, transform);
            car.go.AddComponent<CarEngineSound>().volume = 0.35f;
            car.body = car.go.GetComponent<Collider>();
            car.loose = car.go.GetComponent<LooseCar>();
            car.go.transform.localScale = Vector3.one * car.length;
            car.go.SetActive(true);
            FindWheels(car);
            car.pos = new Vector3(start.x, 5000f, start.y);  // Drive's first ray starts above everything
            cars.Add(car);
            return;
        }
    }

    /// <summary>
    /// The model's wheels (Kenney names them "wheel-…"), to turn them as it rolls: each one's pose and middle in the
    /// car's own frame, taken while the car still stands unturned at the origin.
    /// </summary>
    private static void FindWheels(Car car)
    {
        var root = car.go.transform;
        var list = new List<Transform>();
        foreach (var t in car.go.GetComponentsInChildren<Transform>())
            if (t.name.StartsWith("wheel") && t.GetComponentInChildren<Renderer>() != null) list.Add(t);
        car.wheels = list.ToArray();
        car.wheelRest = new Quaternion[list.Count];
        car.wheelCentre = new Vector3[list.Count];
        car.wheelRadius = 0f;
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i].GetComponentInChildren<Renderer>().bounds;
            car.wheelRest[i] = Quaternion.Inverse(root.rotation) * list[i].rotation;
            car.wheelCentre[i] = root.InverseTransformPoint(b.center);
            car.wheelRadius = Mathf.Max(car.wheelRadius, b.extents.y);
        }
        if (car.wheelRadius <= 0f) car.wheelRadius = 0.1f;
    }

    /// <summary>The chased point: on the lane, `side` metres right of the road between the two nodes.</summary>
    private Vector2 LanePoint(Car car, Vector2[] nodes)
    {
        Vector2 a = nodes[car.from], b = nodes[car.to];
        var dir = (b - a).normalized;
        return Vector2.Lerp(a, b, car.t) + new Vector2(dir.y, -dir.x) * car.side * s;
    }

    /// <summary>Moves the chased point on along the road by `step` scene units, onto the next road at a node.</summary>
    private void Advance(Car car, Vector2[] nodes, List<(int to, float w)>[] next, float step)
    {
        float len = Mathf.Max(Vector2.Distance(nodes[car.from], nodes[car.to]), 0.01f);
        car.t += step / len;
        while (car.t >= 1f)
        {
            var options = next[car.to];
            if (options.Count == 0) { car.t = 1f; return; }
            float over = (car.t - 1f) * len;
            // keep to the through roads where they go on; only a dead end turns back
            var through = MapMinimap.RoadThrough;
            var ahead = options.FindAll(o => o.to != car.from && (through == null || through[o.to]));
            if (ahead.Count == 0) ahead = options.FindAll(o => o.to != car.from);
            int pick = ahead.Count > 0 ? ahead[Random.Range(0, ahead.Count)].to : car.from;
            car.from = car.to;
            car.to = pick;
            len = Mathf.Max(Vector2.Distance(nodes[car.from], nodes[car.to]), 0.01f);
            car.t = over / len;
        }
    }

    private static bool IsJunction(List<(int to, float w)>[] next, int node) => next[node].Count >= 3;

    private void Drive(Car car, Vector2[] nodes, List<(int to, float w)>[] next, float dt)
    {
        // rammed: a wreck now, left to physics where it stopped
        if (car.loose != null && car.loose.Body != null)
        {
            car.pos = car.go.transform.position;
            if (car.junction >= 0) { junctions.Remove(car.junction); car.junction = -1; }
            return;
        }

        var p2 = new Vector2(car.pos.x, car.pos.z);
        var heading = new Vector2(Mathf.Sin(car.yaw), Mathf.Cos(car.yaw));

        // the chased point stays a look-ahead in front: longer the faster it goes, so it rounds bends wider
        float lookAhead = Mathf.Max(6f * s, car.speed * 1.1f, car.wheelbase * 2.5f);
        for (int guard = 0; guard < 64 && Vector2.Distance(LanePoint(car, nodes), p2) < lookAhead; guard++)
        {
            if (car.t >= 1f && next[car.to].Count == 0) break;
            Advance(car, nodes, next, 0.5f * s);
        }
        var target = LanePoint(car, nodes);

        // ---- how fast it may go: its own pace, the bend ahead, the junction, whatever is in its lane
        float want = car.cruise;
        Vector2 a = nodes[car.from], b = nodes[car.to];
        var segDir = (b - a).normalized;
        float toNode = (1f - car.t) * Vector2.Distance(a, b) + Vector2.Distance(p2, target);
        if (next[car.to].Count > 0)
        {
            var nb = nodes[next[car.to][0].to];
            float turn = Vector2.Angle(segDir, (nb - b).normalized);
            if (turn > 25f)
            {
                // a bend's speed (about 0.3 g round it), reached by the time it gets there
                float corner = Mathf.Lerp(car.cruise, 15f / 3.6f * s, Mathf.Clamp01((turn - 25f) / 65f));
                want = Mathf.Min(want, Mathf.Sqrt(corner * corner + 2f * 2f * s * Mathf.Max(0f, toNode - car.length)));
            }
        }
        if (next[car.to].Count == 0) want = Mathf.Min(want, Mathf.Sqrt(2f * 2f * s * Mathf.Max(0f, toNode - car.length)));

        // The junction ahead of the car itself (the chased point may already be past it): whoever is in it first
        // goes, the others wait at the line. One kept waiting too long goes anyway — the one inside may be stuck.
        if (car.junction >= 0)
        {
            var held = nodes[car.junction] - p2;
            if (Vector2.Dot(held, heading) < 0f && held.magnitude > JunctionReach * s * 0.5f)
            {
                if (junctions.TryGetValue(car.junction, out var h) && h == car) junctions.Remove(car.junction);
                car.junction = -1;
            }
        }
        int jNode = -1;
        float jDist = float.MaxValue;
        foreach (int n in new[] { car.from, car.to })
        {
            var to = nodes[n] - p2;
            if (!IsJunction(next, n) || n == car.junction || Vector2.Dot(to, heading) <= 0f || to.magnitude >= jDist) continue;
            jNode = n;
            jDist = to.magnitude;
        }
        if (jNode >= 0 && jDist < JunctionReach * s)
        {
            if (!junctions.TryGetValue(jNode, out var holder) || holder == null || holder.go == null || car.waited > 8f)
            {
                junctions[jNode] = holder = car;
                JunctionsTaken++;
            }
            if (holder == car)
            {
                if (car.junction >= 0 && car.junction != jNode && junctions.TryGetValue(car.junction, out var h) && h == car)
                    junctions.Remove(car.junction);
                car.junction = jNode;
                car.waited = 0f;
            }
            else
            {
                want = Mathf.Min(want, Mathf.Sqrt(2f * 3f * s * Mathf.Max(0f, jDist - StopLine * s)));
                if (car.speed < 0.5f * s) car.waited += dt;
            }
        }
        else car.waited = 0f;

        // ahead, a car's width down its own lane: a parked car is gone round, anything else is followed at a
        // time gap, and stopped behind (the map itself does not count)
        var fwd = new Vector3(heading.x, 0f, heading.y);
        float side = Time.time < car.roundUntil ? -0.4f : LaneOffset;
        bool blocked = false;
        float reach = Mathf.Max(12f * s, car.speed * TimeGap + StandGap * s + car.speed * car.speed / (2f * 4f * s));
        int hitCount = Physics.BoxCastNonAlloc(car.pos + Vector3.up * 0.8f * s, new Vector3(0.9f, 0.5f, 0.5f) * s, fwd, Hits,
                                               Quaternion.LookRotation(fwd), reach, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++)
        {
            var hit = Hits[i];
            if (hit.collider == car.body || hit.collider.name.EndsWith("_col")) continue;
            if (hit.distance <= 0f) continue;   // already overlapping at the start: beside it, not ahead
            if (parked != null && hit.collider.transform.IsChildOf(parked)) { side = -0.4f; continue; }
            float gap = hit.distance - car.length * 0.5f - StandGap * s;
            want = Mathf.Min(want, Mathf.Max(0f, gap) / TimeGap);
            if (gap < 3f * s) blocked = true;
        }

        // Held up for good (a player stopped in its lane, a car dumped across the road): go round it as round a
        // parked one, and when even that is shut, vanish once nobody is looking — a new car comes in elsewhere.
        car.blockedFor = blocked && car.speed < 0.5f * s ? car.blockedFor + dt : 0f;
        if (car.blockedFor > 4f && Time.time >= car.roundUntil) car.roundUntil = Time.time + 6f;
        if (car.blockedFor > 15f && !Visible(car.pos))
        {
            Remove(car);
            return;
        }
        car.side = Mathf.MoveTowards(car.side, side, 1.5f * dt);
        // up gently, down as hard as it has to
        car.speed = Mathf.MoveTowards(car.speed, want, (want < car.speed ? 6f : 1.8f) * s * dt);

        // ---- steering: the arc through the chased point, no tighter than the wheels turn
        var toTarget = target - p2;
        float alpha = Mathf.Atan2(toTarget.x, toTarget.y) - car.yaw;
        alpha = Mathf.Repeat(alpha + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        float curvature = 2f * Mathf.Sin(alpha) / Mathf.Max(toTarget.magnitude, 0.01f);
        float most = Mathf.Tan(MaxSteer * Mathf.Deg2Rad) / Mathf.Max(car.wheelbase, 0.01f);
        curvature = Mathf.Clamp(curvature, -most, most);
        float placed = car.pos.y;
        if (placed > 4000f)
        {
            // a new car: straight onto its lane, facing down it
            car.yaw = Mathf.Atan2(toTarget.x, toTarget.y);
        }
        else
        {
            car.yaw += curvature * car.speed * dt;
            heading = new Vector2(Mathf.Sin(car.yaw), Mathf.Cos(car.yaw));
            p2 += heading * car.speed * dt;
        }

        // ---- on the road: the highest map collider under each axle (never a person or another car)
        heading = new Vector2(Mathf.Sin(car.yaw), Mathf.Cos(car.yaw));
        float half = car.wheelbase * 0.5f;
        float yMid = Ground(p2, car.pos.y, 1.5f, out bool found);
        if (!found) yMid = car.pos.y > 4000f ? 0f : car.pos.y;
        // the axles from just above the road: from higher up a bridge deck or a roof beside it counted as road and
        // stood the car on its tail; and no steeper than a road gets (35 %)
        float steepest = half * 0.35f;
        float yFront = Ground(p2 + heading * half, yMid, 0.4f, out bool ff);
        float yBack = Ground(p2 - heading * half, yMid, 0.4f, out bool fb);
        if (!ff || Mathf.Abs(yFront - yMid) > steepest) yFront = yMid;
        if (!fb || Mathf.Abs(yBack - yMid) > steepest) yBack = yMid;
        car.pos = new Vector3(p2.x, (yFront + yBack) * 0.5f, p2.y);
        float pitch = -Mathf.Atan2(yFront - yBack, Mathf.Max(car.wheelbase, 0.01f)) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(pitch, car.yaw * Mathf.Rad2Deg, 0f);
        car.go.transform.SetPositionAndRotation(car.pos, placed > 4000f ? rot : Quaternion.Slerp(car.go.transform.rotation, rot, 1f - Mathf.Exp(-15f * dt)));

        // the wheels roll, the front ones steer
        if (car.wheels != null && car.wheels.Length > 0)
        {
            car.wheelTurn += car.speed / car.wheelRadius * Mathf.Rad2Deg * dt;
            float steerDeg = Mathf.Atan(curvature * car.wheelbase) * Mathf.Rad2Deg;
            var root = car.go.transform;
            for (int i = 0; i < car.wheels.Length; i++)
            {
                var w = car.wheels[i];
                if (w == null) continue;
                bool front = w.name.Contains("front");
                w.rotation = root.rotation * Quaternion.Euler(0f, front ? steerDeg : 0f, 0f)
                             * Quaternion.AngleAxis(car.wheelTurn, Vector3.right) * car.wheelRest[i];
                // turned about its pivot, which need not be its middle: put the middle back
                var r = w.GetComponentInChildren<Renderer>();
                if (r != null) w.position += root.TransformPoint(car.wheelCentre[i]) - r.bounds.center;
            }
        }
    }

    /// <summary>The highest map collider under p from `rise` above `near` (or anywhere, for a car just placed).</summary>
    private static float Ground(Vector2 p, float near, float rise, out bool found)
    {
        bool fresh = near > 4000f;
        int n = Physics.RaycastNonAlloc(new Vector3(p.x, fresh ? 5000f : near + rise, p.y), Vector3.down, Hits,
                                        fresh ? 10000f : rise + 2.5f, ~0, QueryTriggerInteraction.Ignore);
        float y = float.MinValue;
        for (int i = 0; i < n; i++)
            if (Hits[i].collider.name.EndsWith("_col")) y = Mathf.Max(y, Hits[i].point.y);
        found = y > float.MinValue;
        return found ? y : near;
    }
}
