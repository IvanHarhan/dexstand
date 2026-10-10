using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Wanted level and the police that come for it. Killing or hurting passers-by (reported by the client that did it,
/// see Report) puts stars on a player, up to five; more stars bring more patrol cars. The host runs the police: cars
/// come in from the road network out of sight and drive the roads to the player, the two officers in each get out
/// close by, close in and shoot. Out of every unit's sight for forgetSeconds, the player loses a star; dead, all of
/// them. Shooting an officer adds one.
///
/// One networked scene object holds it all (the scene builder's "police" command places it): the stars per player
/// and the pose of every car and officer, so every player sees the same police. Clients only draw them.
/// </summary>
public class Police : NetworkBehaviour
{
    public const int Cars = 6, CopsPerCar = 2, Cops = Cars * CopsPerCar, MaxStars = 5;

    [Header("Wanted")]
    public float forgetSeconds = 25f;   // out of sight this long: one star less
    public float seeDistance = 60f;     // scene units (a unit is 3 m)

    [Header("Units")]
    public float carSpeedKmh = 90f;
    public float chaseSpeedKmh = 150f;  // after a player who is driving
    public float spawnMin = 70f, spawnMax = 140f;
    public float dismountDistance = 10f;
    public float copRange = 25f, copKeep = 3.5f;
    public float copRunSpeed = 5.4f;    // m/s: as fast as a player, or they only ever fell behind
    public float copDamage = 9f, copFireInterval = 0.9f, copHealth = 60f;

    public static Police Instance { get; private set; }

    [Networked, Capacity(16)] public NetworkDictionary<PlayerRef, byte> Stars => default;
    [Networked, Capacity(Cars)] private NetworkArray<Vector3> CarPos => default;
    [Networked, Capacity(Cars)] private NetworkArray<float> CarYaw => default;
    [Networked, Capacity(Cars)] private NetworkArray<PlayerRef> CarTarget => default;
    [Networked, Capacity(Cars)] private NetworkArray<byte> CarState => default;   // 0 none, 1 driving, 2 stopped
    [Networked, Capacity(Cops)] private NetworkArray<Vector3> CopPos => default;
    [Networked, Capacity(Cops)] private NetworkArray<float> CopYaw => default;
    [Networked, Capacity(Cops)] private NetworkArray<byte> CopState => default;   // 0 none, 1 in the car, 2 on foot, 3 down
    [Networked, Capacity(Cops)] private NetworkArray<float> CopHealth => default;
    [Networked, Capacity(Cops)] private NetworkArray<int> CopShots => default;    // counts up with every shot fired
    [Networked, Capacity(Cops)] private NetworkArray<Vector3> CopShotAt => default;

    private const byte None = 0, Driving = 1, Stopped = 2;
    private const byte InCar = 1, OnFoot = 2, Down = 3;

    private float s = 1f / 3f;  // scene units per metre

    // ------------------------------------------------------------------ reporting

    /// <summary>A crime by this player, from the client that saw it happen: one star, and one more per kill.</summary>
    public static void Report(PlayerRef who, bool killed)
    {
        var p = Instance;
        if (p != null && p.Object != null && p.Object.IsValid && who != PlayerRef.None) p.RPC_Crime(who, killed);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Crime(PlayerRef who, NetworkBool killed) => Raise(who, killed ? 1 : 0);

    /// <summary>Host: a wanted player's first crime is one star; after that, `more` more.</summary>
    private void Raise(PlayerRef who, int more)
    {
        int stars = Stars.TryGet(who, out var v) ? v : 0;
        Stars.Set(who, (byte)Mathf.Min(MaxStars, stars == 0 ? 1 : stars + more));
        lastSeen[who] = Runner.SimulationTime;
    }

    /// <summary>For the automated test: what the police are doing about a spot.</summary>
    public string Status(Vector3 spot, out Vector3 nearest)
    {
        int cars = 0, onFoot = 0, down = 0;
        float best = float.MaxValue;
        nearest = spot;
        for (int i = 0; i < Cars; i++)
        {
            if (CarState[i] == None) continue;
            cars++;
            float d = Vector3.Distance(CarPos[i], spot);
            if (d < best) { best = d; nearest = CarPos[i]; }
        }
        for (int j = 0; j < Cops; j++) { if (CopState[j] == OnFoot) onFoot++; if (CopState[j] == Down) down++; }
        int shots = 0;
        for (int j = 0; j < Cops; j++) shots += CopShots[j];
        return $"{cars} cars (nearest {(cars > 0 ? best.ToString("F0") : "-")}), {onFoot} on foot, {down} down, {shots} shots";
    }

    public int StarsOf(PlayerRef who) => Object != null && Object.IsValid && Stars.TryGet(who, out var v) ? v : 0;

    /// <summary>Host: a player's shot hit an officer.</summary>
    public void HitCop(int cop, float damage, PlayerRef shooter)
    {
        if (!HasStateAuthority || cop < 0 || cop >= Cops || CopState[cop] != OnFoot && CopState[cop] != InCar) return;
        CopHealth.Set(cop, CopHealth[cop] - damage);
        if (CopHealth[cop] > 0f) { Raise(shooter, 0); return; }
        CopState.Set(cop, Down);
        Raise(shooter, 1);
    }

    // ------------------------------------------------------------------ host

    private class Plan { public readonly List<int> path = new List<int>(); public int step; public Vector3 goal; public float replanAt, speed, recalledAt, idleSince; }

    private struct Suspect { public PlayerRef who; public PlayerHealth health; public Transform body; }

    private readonly Plan[] plans = new Plan[Cars];
    private readonly Dictionary<PlayerRef, float> lastSeen = new Dictionary<PlayerRef, float>();
    private readonly Dictionary<PlayerRef, Vector3> lastPos = new Dictionary<PlayerRef, Vector3>();
    private readonly Dictionary<PlayerRef, float> speedOf = new Dictionary<PlayerRef, float>();
    private readonly Dictionary<PlayerRef, Vector3> velOf = new Dictionary<PlayerRef, Vector3>();
    private readonly float[] nextShot = new float[Cops];
    private readonly List<Suspect> suspects = new List<Suspect>();
    private float nextScan;

    public override void Spawned()
    {
        Instance = this;
        var map = GameObject.Find("Map");
        if (map != null) s = map.transform.lossyScale.x;
        for (int i = 0; i < Cars; i++) plans[i] = new Plan();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null;
        foreach (var v in carViews) if (v != null) Destroy(v.root);
        foreach (var v in copViews) if (v != null) Destroy(v.root);
        if (hud != null) Destroy(hud);
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;
        float now = Runner.SimulationTime, dt = Runner.DeltaTime;
        ScanSuspects(now);
        foreach (var p in suspects)
        {
            var pos = p.body.position;
            if (lastPos.TryGetValue(p.who, out var was))
            {
                velOf[p.who] = (pos - was) / dt;
                speedOf[p.who] = velOf[p.who].magnitude;
            }
            lastPos[p.who] = pos;
        }
        UpdateWanted(now);
        Allocate();
        for (int i = 0; i < Cars; i++) if (CarState[i] != None) DriveCar(i, now, dt);
        for (int j = 0; j < Cops; j++) if (CopState[j] == OnFoot || CopState[j] == InCar) MoveCop(j, now, dt);
    }

    private void ScanSuspects(float now)
    {
        if (now < nextScan && suspects.TrueForAll(p => p.health != null && p.body != null)) return;
        nextScan = now + 1f;
        suspects.Clear();
        foreach (var h in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (h.Object == null || !h.Object.IsValid) continue;
            var pc = h.GetComponentInChildren<PlayerController>(true);
            if (pc != null) suspects.Add(new Suspect { who = h.Object.InputAuthority, health = h, body = pc.transform });
        }
    }

    private bool Find(PlayerRef who, out Suspect suspect)
    {
        foreach (var p in suspects)
            if (p.who == who && p.health != null && p.body != null) { suspect = p; return true; }
        suspect = default;
        return false;
    }

    private static Vector3 Chest(Transform body, float s) => body.position + Vector3.up * 1.3f * s;

    private void UpdateWanted(float now)
    {
        var wanted = new List<PlayerRef>();
        foreach (var kv in Stars) wanted.Add(kv.Key);
        foreach (var who in wanted)
        {
            // dead or gone: the slate is clean
            if (!Find(who, out var p) || p.health.IsDead) { Stars.Remove(who); continue; }
            if (!lastSeen.ContainsKey(who)) lastSeen[who] = now;
            if (Seen(p.body.position)) lastSeen[who] = now;
            else if (now - lastSeen[who] > forgetSeconds)
            {
                lastSeen[who] = now;
                int left = Stars.Get(who) - 1;
                if (left <= 0) Stars.Remove(who); else Stars.Set(who, (byte)left);
            }
        }
    }

    /// <summary>Whether any car or officer on its feet has the spot in view.</summary>
    private bool Seen(Vector3 spot)
    {
        for (int i = 0; i < Cars; i++)
            if (CarState[i] != None && CanSee(CarPos[i], spot)) return true;
        for (int j = 0; j < Cops; j++)
            if (CopState[j] == OnFoot && CanSee(CopPos[j], spot)) return true;
        return false;
    }

    private static readonly RaycastHit[] SightHits = new RaycastHit[16];

    private bool CanSee(Vector3 from, Vector3 to)
    {
        float d = Vector3.Distance(from, to);
        if (d > seeDistance) return false;
        if (d < 15f) return true;
        var a = from + Vector3.up * 1.5f * s;
        var b = to + Vector3.up * 1.5f * s;
        int n = Physics.RaycastNonAlloc(a, (b - a).normalized, SightHits, Vector3.Distance(a, b), ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (SightHits[i].collider.name.EndsWith("_col")) return false;  // the map in between: a wall, a hill
        return true;
    }

    /// <summary>More stars, more cars: one for one or two, two up to four, three at five.</summary>
    private void Allocate()
    {
        var wanted = new Dictionary<PlayerRef, int>();
        foreach (var kv in Stars) wanted[kv.Key] = kv.Value <= 1 ? 1 : kv.Value <= 3 ? 2 : 3;

        for (int i = 0; i < Cars; i++)
        {
            if (CarState[i] == None) continue;
            var t = CarTarget[i];
            if (t != PlayerRef.None && wanted.TryGetValue(t, out int need) && need > 0)
            {
                wanted[t] = need - 1;
                plans[i].idleSince = 0f;
                continue;
            }
            // nobody to chase (any more), or one car too many: stand down once no player is close, or after a while
            CarTarget.Set(i, PlayerRef.None);
            float now = Runner.SimulationTime;
            if (plans[i].idleSince <= 0f) plans[i].idleSince = now;
            if (!AnyPlayerWithin(CarPos[i], 50f) || now - plans[i].idleSince > 30f) Retire(i);
        }
        foreach (var kv in wanted)
            for (int n = 0; n < kv.Value; n++)
            {
                int slot = -1;
                for (int i = 0; i < Cars && slot < 0; i++) if (CarState[i] == None) slot = i;
                if (slot < 0 || !Find(kv.Key, out var p) || !SpawnCar(slot, kv.Key, p.body.position)) break;
            }
    }

    private bool AnyPlayerWithin(Vector3 pos, float range)
    {
        foreach (var p in suspects)
            if (p.body != null && Vector3.Distance(p.body.position, pos) < range) return true;
        return false;
    }

    private void Retire(int car)
    {
        CarState.Set(car, None);
        CarTarget.Set(car, PlayerRef.None);
        for (int k = 0; k < CopsPerCar; k++) CopState.Set(car * CopsPerCar + k, None);
    }

    private bool SpawnCar(int car, PlayerRef target, Vector3 near)
    {
        var nodes = MapMinimap.RoadNodes;
        if (nodes == null || nodes.Length == 0) return false;
        BuildGrid(nodes);
        // a road node out of sight: between spawnMin and spawnMax, main roads preferred
        int best = -1;
        float bestScore = float.MaxValue;
        for (int tries = 0; tries < 80; tries++)
        {
            var dir = Random.insideUnitCircle.normalized * Random.Range(spawnMin, spawnMax);
            int node = NearestNode(new Vector3(near.x + dir.x, 0f, near.z + dir.y), 3);
            if (node < 0) continue;
            float d = Vector2.Distance(nodes[node], new Vector2(near.x, near.z));
            float score = Mathf.Abs(d - (spawnMin + spawnMax) * 0.5f)
                          + (MapMinimap.RoadThrough != null && !MapMinimap.RoadThrough[node] ? 40f : 0f);
            if (d >= spawnMin * 0.8f && score < bestScore) { bestScore = score; best = node; }
        }
        if (best < 0) return false;
        var pos = Ground(new Vector3(nodes[best].x, 0f, nodes[best].y), float.NaN);
        CarPos.Set(car, pos);
        CarYaw.Set(car, Yaw(near - pos));
        CarTarget.Set(car, target);
        CarState.Set(car, Driving);
        for (int k = 0; k < CopsPerCar; k++)
        {
            int j = car * CopsPerCar + k;
            CopState.Set(j, InCar);
            CopHealth.Set(j, copHealth);
            CopPos.Set(j, pos);
        }
        var plan = plans[car];
        plan.path.Clear();
        plan.step = 0;
        plan.replanAt = 0f;
        plan.speed = 0f;
        plan.idleSince = 0f;
        plan.recalledAt = 0f;
        return true;
    }

    private void DriveCar(int car, float now, float dt)
    {
        var plan = plans[car];
        bool chasing = Find(CarTarget[car], out var p) && !p.health.IsDead;
        var pos = CarPos[car];
        float targetSpeed = chasing && speedOf.TryGetValue(p.who, out var sp) ? sp : 0f;

        if (CarState[car] == Stopped)
        {
            // they drove off (or walked well away): everyone back in, and after them
            if (chasing && (targetSpeed > 3f || Vector3.Distance(pos, p.body.position) > dismountDistance * 2.5f))
            {
                if (plan.recalledAt <= 0f) plan.recalledAt = now;
                bool aboard = true;
                for (int k = 0; k < CopsPerCar; k++) aboard &= CopState[car * CopsPerCar + k] != OnFoot;
                if (aboard || now - plan.recalledAt > 8f)
                {
                    for (int k = 0; k < CopsPerCar; k++)
                        if (CopState[car * CopsPerCar + k] == OnFoot) CopState.Set(car * CopsPerCar + k, InCar);
                    CarState.Set(car, Driving);
                    plan.replanAt = 0f;
                }
            }
            else plan.recalledAt = 0f;
            return;
        }

        if (!chasing) { plan.speed = Mathf.MoveTowards(plan.speed, 0f, 6f * dt); return; }
        var tp = p.body.position;
        if (Vector3.Distance(pos, tp) < dismountDistance && targetSpeed < 3f)
        {
            // pull up and get out on both sides
            CarState.Set(car, Stopped);
            plan.speed = 0f;
            plan.recalledAt = 0f;
            var right = Quaternion.Euler(0f, CarYaw[car], 0f) * Vector3.right;
            for (int k = 0; k < CopsPerCar; k++)
            {
                int j = car * CopsPerCar + k;
                if (CopState[j] != InCar) continue;
                CopState.Set(j, OnFoot);
                CopPos.Set(j, Ground(pos + right * (k == 0 ? -0.5f : 0.5f), pos.y));
                nextShot[j] = now + 1f;
            }
            return;
        }

        var nodes = MapMinimap.RoadNodes;
        bool driving = targetSpeed > 3f;
        if (nodes != null && (now >= plan.replanAt || (tp - plan.goal).sqrMagnitude > 20f * 20f && now >= plan.replanAt - 2f))
        {
            plan.replanAt = now + 3f;
            plan.goal = tp;
            plan.step = 0;
            Path(NearestNode(pos, 2), NearestNode(tp, 3), plan.path);
        }
        // along the roads, then the last stretch straight at them
        Vector3 wp = tp;
        while (nodes != null && plan.step < plan.path.Count)
        {
            var n = nodes[plan.path[plan.step]];
            wp = new Vector3(n.x, pos.y, n.y);
            if (new Vector2(wp.x - pos.x, wp.z - pos.z).sqrMagnitude > 1.5f * 1.5f) break;
            plan.step++;
            wp = tp;
        }
        // Close behind a car on the move: off the route and straight for where it is going — a ram.
        float gap = Vector3.Distance(pos, tp);
        if (driving && gap < 18f) wp = tp + velOf.GetValueOrDefault(p.who) * 0.4f;
        var to = wp - pos;
        to.y = 0f;
        float want = (driving ? chaseSpeedKmh : carSpeedKmh) / 10.8f;
        // slow for a sharp turn at the next node, and for the last stretch to someone on foot
        float turn = Vector3.Angle(Quaternion.Euler(0f, CarYaw[car], 0f) * Vector3.forward, to);
        if (turn > 30f) want *= Mathf.Lerp(1f, 0.3f, (turn - 30f) / 90f);
        if (!driving && gap < 25f) want = Mathf.Min(want, 4f);
        plan.speed = Mathf.MoveTowards(plan.speed, want, (want < plan.speed ? 12f : 4f) * dt);
        if (to.sqrMagnitude > 1e-4f)
        {
            CarYaw.Set(car, Mathf.MoveTowardsAngle(CarYaw[car], Yaw(to), 140f * dt));
            var ahead = Quaternion.Euler(0f, CarYaw[car], 0f) * Vector3.forward;
            pos += ahead * plan.speed * dt;
        }
        CarPos.Set(car, Ground(pos, pos.y));
        for (int k = 0; k < CopsPerCar; k++)
        {
            int j = car * CopsPerCar + k;
            if (CopState[j] == InCar) { CopPos.Set(j, CarPos[car]); CopYaw.Set(j, CarYaw[car]); }
        }
    }

    private void MoveCop(int j, float now, float dt)
    {
        int car = j / CopsPerCar;
        if (CopState[j] == InCar)
        {
            // out of the window in a chase, the passenger only, and badly
            if (j % CopsPerCar == 1 && CarState[car] == Driving && Find(CarTarget[car], out var chased) && !chased.health.IsDead)
                Fire(j, CarPos[car], chased, now, 0.35f);
            return;
        }
        if (CopState[j] != OnFoot) return;
        var pos = CopPos[j];
        Vector3 goal;
        float keep;
        bool chasing = Find(CarTarget[car], out var p) && !p.health.IsDead;
        if (CarState[car] == Driving || !chasing)
        {
            // back to the car
            goal = CarPos[car];
            keep = 0.4f;
            if (Vector3.Distance(pos, goal) < 0.8f && CarState[car] == Driving) { CopState.Set(j, InCar); return; }
        }
        else { goal = p.body.position; keep = copKeep; }

        var to = goal - pos;
        to.y = 0f;
        float d = to.magnitude;
        if (d > keep)
        {
            pos += to / d * Mathf.Min(copRunSpeed * s * dt, d - keep);
            CopPos.Set(j, Ground(pos, pos.y));
        }
        if (d > 1e-3f) CopYaw.Set(j, Yaw(to));

        if (chasing && CarState[car] != Driving) Fire(j, pos, p, now, 1f);
    }

    /// <summary>A shot at the suspect if they are in range and in sight and the gun is ready; aim is 1 on foot.</summary>
    private void Fire(int j, Vector3 pos, Suspect p, float now, float aim)
    {
        if (now < nextShot[j]) return;
        float range = Vector3.Distance(pos, p.body.position);
        if (range > copRange || !CanSee(pos, p.body.position)) return;
        nextShot[j] = now + copFireInterval * Random.Range(0.8f, 1.3f);
        float chance = aim * Mathf.Lerp(0.55f, 0.15f, range / copRange) * (speedOf.TryGetValue(p.who, out var v) && v > 3f ? 0.5f : 1f);
        var chest = Chest(p.body, s);
        bool hit = Random.value < chance;
        if (hit) p.health.ApplyDamage(copDamage, false, PlayerRef.None);
        CopShotAt.Set(j, hit ? chest : chest + Random.insideUnitSphere * 0.4f);
        CopShots.Set(j, CopShots[j] + 1);
    }

    private static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

    private static readonly RaycastHit[] GroundHits = new RaycastHit[16];

    /// <summary>The highest map collider under a point; nearY: a short ray round that height, NaN: from the sky.</summary>
    private static Vector3 Ground(Vector3 p, float nearY)
    {
        bool near = !float.IsNaN(nearY);
        int n = Physics.RaycastNonAlloc(new Vector3(p.x, near ? nearY + 1.5f : 5000f, p.z), Vector3.down, GroundHits,
                                        near ? 4f : 10000f, ~0, QueryTriggerInteraction.Ignore);
        float y = float.MinValue;
        for (int i = 0; i < n; i++)
            if (GroundHits[i].collider.name.EndsWith("_col")) y = Mathf.Max(y, GroundHits[i].point.y);
        return new Vector3(p.x, y == float.MinValue ? (near ? nearY : p.y) : y, p.z);
    }

    // ------------------------------------------------------------------ roads

    private Dictionary<Vector2Int, List<int>> grid;
    private const float GridCell = 30f;

    private void BuildGrid(Vector2[] nodes)
    {
        if (grid != null) return;
        grid = new Dictionary<Vector2Int, List<int>>();
        for (int i = 0; i < nodes.Length; i++)
        {
            var k = new Vector2Int(Mathf.FloorToInt(nodes[i].x / GridCell), Mathf.FloorToInt(nodes[i].y / GridCell));
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
            l.Add(i);
        }
    }

    private int NearestNode(Vector3 p, int reach)
    {
        var nodes = MapMinimap.RoadNodes;
        if (nodes == null) return -1;
        BuildGrid(nodes);
        var c = new Vector2Int(Mathf.FloorToInt(p.x / GridCell), Mathf.FloorToInt(p.z / GridCell));
        int best = -1;
        float bestD = float.MaxValue;
        for (int dx = -reach; dx <= reach; dx++)
            for (int dz = -reach; dz <= reach; dz++)
            {
                if (!grid.TryGetValue(c + new Vector2Int(dx, dz), out var l)) continue;
                foreach (int i in l)
                {
                    float d = (nodes[i] - new Vector2(p.x, p.z)).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
        return best;
    }

    /// <summary>A* over the road graph; leaves the path empty when there is none within reach.</summary>
    private static void Path(int from, int to, List<int> path)
    {
        path.Clear();
        var nodes = MapMinimap.RoadNodes;
        var next = MapMinimap.RoadNext;
        if (nodes == null || next == null || from < 0 || to < 0) return;
        var g = new Dictionary<int, float> { [from] = 0f };
        var prev = new Dictionary<int, int>();
        var open = new SortedSet<(float f, int n)> { (0f, from) };
        int expanded = 0;
        while (open.Count > 0 && expanded++ < 40000)
        {
            var cur = open.Min;
            open.Remove(cur);
            int n = cur.n;
            if (n == to) break;
            float gn = g[n];
            foreach (var (m, _) in next[n])
            {
                float gm = gn + Vector2.Distance(nodes[n], nodes[m]);
                if (g.TryGetValue(m, out var old))
                {
                    if (gm >= old) continue;
                    open.Remove((old + Vector2.Distance(nodes[m], nodes[to]), m));
                }
                g[m] = gm;
                prev[m] = n;
                open.Add((gm + Vector2.Distance(nodes[m], nodes[to]), m));
            }
        }
        if (!prev.ContainsKey(to) && from != to) return;
        for (int n = to; n != from; n = prev[n]) path.Add(n);
        path.Reverse();
    }

    // ------------------------------------------------------------------ drawing (every peer)

    private class CarView { public GameObject root; public Renderer[] lamps; public Light light; }
    private class CopView { public GameObject root; public Animator anim; public bool fallen; public byte state; }
    private readonly int[] seenShots = new int[Cops];

    private readonly CarView[] carViews = new CarView[Cars];
    private readonly CopView[] copViews = new CopView[Cops];
    private ImpactEffects effects;
    private GameObject hud;
    private Image[] hudStars;
    private static readonly int SpeedId = Animator.StringToHash("Speed");

    public override void Render()
    {
        float t = Time.deltaTime;
        for (int i = 0; i < Cars; i++)
        {
            bool on = CarState[i] != None;
            var v = carViews[i];
            if (!on) { if (v != null) v.root.SetActive(false); continue; }
            if (v == null && (v = carViews[i] = MakeCar()) == null) continue;
            bool fresh = !v.root.activeSelf;
            v.root.SetActive(true);
            var rot = Quaternion.Euler(0f, CarYaw[i], 0f);
            var tr = v.root.transform;
            if (fresh) tr.SetPositionAndRotation(CarPos[i], rot);
            else tr.SetPositionAndRotation(Vector3.Lerp(tr.position, CarPos[i], 1f - Mathf.Exp(-15f * t)),
                                           Quaternion.Slerp(tr.rotation, rot, 1f - Mathf.Exp(-10f * t)));
            // the blue lamps take turns
            bool phase = Mathf.Repeat(Time.time * 3f + i * 0.3f, 1f) < 0.5f;
            for (int k = 0; k < v.lamps.Length; k++) v.lamps[k].enabled = (k % 2 == 0) == phase;
            if (v.light != null) v.light.intensity = phase ? 3f : 0.6f;
        }

        if (effects == null) effects = FindFirstObjectByType<ImpactEffects>();
        for (int j = 0; j < Cops; j++)
        {
            byte state = CopState[j];
            var v = copViews[j];
            if (CopShots[j] != seenShots[j])
            {
                // from the officer's chest, or out of the car's window
                seenShots[j] = CopShots[j];
                var car = carViews[j / CopsPerCar];
                Vector3? from = state == OnFoot && v != null && v.root.activeSelf
                    ? v.root.transform.position + Vector3.up * 1.4f * s + v.root.transform.forward * 0.4f * s
                    : state == InCar && car != null && car.root.activeSelf ? car.root.transform.position + Vector3.up * 0.35f : (Vector3?)null;
                if (from.HasValue && effects != null) effects.PlayTracer(from.Value, CopShotAt[j]);
            }
            // a slot handed to a new officer: the old body (maybe lying there) makes way
            if (v != null && v.state == Down && state != Down) { Destroy(v.root); v = copViews[j] = null; }
            if (state == None || state == InCar)
            {
                if (v != null) { v.root.SetActive(false); v.state = state; }
                continue;
            }
            if (v == null && (v = copViews[j] = MakeCop(j)) == null) continue;
            if (state == Down)
            {
                if (!v.fallen)
                {
                    v.fallen = true;
                    v.root.SetActive(true);
                    foreach (var c in v.root.GetComponents<Collider>()) c.enabled = false;
                    if (v.anim != null && v.anim.isHuman)
                        Pedestrian.BuildRagdoll(v.anim, v.root.transform.lossyScale.y, v.root.transform.position + Vector3.up * s,
                                                -v.root.transform.forward * 1.5f * s, false);
                    if (v.anim != null) v.anim.enabled = false;
                }
                v.state = state;
                continue;
            }
            bool fresh = !v.root.activeSelf || v.state != OnFoot;
            v.root.SetActive(true);
            v.state = state;
            var tr = v.root.transform;
            var rot = Quaternion.Euler(0f, CopYaw[j], 0f);
            var before = tr.position;
            if (fresh) tr.SetPositionAndRotation(CopPos[j], rot);
            else tr.SetPositionAndRotation(Vector3.Lerp(tr.position, CopPos[j], 1f - Mathf.Exp(-15f * t)),
                                           Quaternion.Slerp(tr.rotation, rot, 1f - Mathf.Exp(-10f * t)));
            if (v.anim != null && t > 0f) v.anim.SetFloat(SpeedId, (tr.position - before).magnitude / t / s);
        }
        DrawHud();
    }

    private CarView MakeCar()
    {
        var prefab = Resources.Load<GameObject>("Cars/vw-passat");
        if (prefab == null) return null;
        var template = MapParkedCars.Template(prefab, transform);
        if (template == null) return null;
        var go = template;  // the template is made for this one car
        go.name = "PoliceCar";
        Destroy(go.GetComponent<LooseCar>());
        // kinematic: the player's car hits it as it would a moving car, not a wall that teleports
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        if (go.GetComponent<CarEngineSound>() == null) go.AddComponent<CarEngineSound>().volume = 0.45f;
        rb.interpolation = RigidbodyInterpolation.None;
        go.transform.localScale = Vector3.one * CarSpecs.Get("vw-passat").length * s;
        go.transform.SetParent(null, true);
        go.SetActive(true);
        Paint(go, new Color(0.18f, 0.3f, 0.7f));

        // a light bar on the roof: two blue lamps and their glow
        var b = new Bounds(go.transform.position, Vector3.zero);
        foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        var lamp = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
        lamp.color = new Color(0.2f, 0.45f, 1f);
        var lamps = new Renderer[2];
        for (int k = 0; k < 2; k++)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(cube.GetComponent<Collider>());
            cube.transform.SetParent(go.transform, false);
            cube.transform.position = new Vector3(b.center.x, b.max.y + 0.02f, b.center.z) + go.transform.right * (k == 0 ? -0.12f : 0.12f);
            cube.transform.rotation = go.transform.rotation;
            cube.transform.localScale = new Vector3(0.2f, 0.05f, 0.08f) / go.transform.lossyScale.x;
            lamps[k] = cube.GetComponent<Renderer>();
            lamps[k].sharedMaterial = lamp;
        }
        var lightGo = new GameObject("Siren");
        lightGo.transform.SetParent(go.transform, false);
        lightGo.transform.position = new Vector3(b.center.x, b.max.y + 0.2f, b.center.z);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(0.2f, 0.4f, 1f);
        light.range = 4f;
        light.shadows = LightShadows.None;
        return new CarView { root = go, lamps = lamps, light = light };
    }

    /// <summary>The body paint: of the car's colours, the one covering the most of it that is neither glass nor near black.</summary>
    private static void Paint(GameObject car, Color colour)
    {
        Renderer bestR = null;
        int bestSub = -1, bestTris = 0;
        foreach (var r in car.GetComponentsInChildren<Renderer>())
        {
            var mesh = r is SkinnedMeshRenderer sk ? sk.sharedMesh
                     : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
            if (mesh == null) continue;
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length && m < mesh.subMeshCount; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;
                var c = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : mat.color;
                if (c.a < 0.9f || c.maxColorComponent < 0.15f || mat.name.Contains("glass")) continue;
                int tris = (int)mesh.GetIndexCount(m) / 3;
                if (tris > bestTris) { bestTris = tris; bestR = r; bestSub = m; }
            }
        }
        if (bestR == null) return;
        var block = new MaterialPropertyBlock();
        bestR.GetPropertyBlock(block, bestSub);
        block.SetColor("_BaseColor", colour);
        block.SetColor("_Color", colour);
        bestR.SetPropertyBlock(block, bestSub);
    }

    private CopView MakeCop(int j)
    {
        var peds = FindFirstObjectByType<MapPedestrians>();
        if (peds == null || peds.models == null || peds.models.Length == 0) return null;
        var men = System.Array.FindAll(peds.models, m => m != null && m.name.Contains("_m"));
        var model = men.Length > 0 ? men[j % men.Length] : peds.models[j % peds.models.Length];
        var go = Instantiate(model);
        go.name = "PoliceOfficer";
        go.transform.localScale = Vector3.one * s;
        var anim = go.GetComponentInChildren<Animator>() ?? go.AddComponent<Animator>();
        anim.runtimeAnimatorController = peds.controller;
        anim.applyRootMotion = false;
        var cap = go.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 0.9f, 0f);
        cap.height = 1.75f;
        cap.radius = 0.25f;
        go.AddComponent<PoliceCop>().index = j;
        // the uniform: every suit dark blue
        var navy = new Color(0.08f, 0.12f, 0.3f);
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                if (mats[m] == null || !mats[m].name.Contains("suit")) continue;
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block, m);
                block.SetColor("_BaseColor", navy);
                block.SetColor("_Color", navy);
                r.SetPropertyBlock(block, m);
            }
        }
        go.SetActive(false);
        return new CopView { root = go, anim = anim };
    }

    /// <summary>This screen's player's stars, top right: five diamonds, the earned ones lit.</summary>
    private void DrawHud()
    {
        int stars = Runner != null ? StarsOf(Runner.LocalPlayer) : 0;
        if (hud == null)
        {
            if (stars == 0) return;
            hud = new GameObject("WantedHud");
            var canvas = hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 45;
            var scaler = hud.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            // PC: under MapMinimap's 300 px square (top right, 120..420 down); phone: its minimap is top centre-left
            float y = Application.isMobilePlatform ? -150f : -450f;
            hudStars = new Image[MaxStars];
            for (int i = 0; i < MaxStars; i++)
            {
                var star = new GameObject("Star" + i).AddComponent<Image>();
                star.transform.SetParent(hud.transform, false);
                var rt = star.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
                rt.sizeDelta = new Vector2(34f, 34f);
                rt.anchoredPosition = new Vector2(-40f - (MaxStars - 1 - i) * 52f, y);
                rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
                hudStars[i] = star;
            }
        }
        hud.SetActive(stars > 0);
        for (int i = 0; i < MaxStars; i++)
        {
            hudStars[i].color = i < stars ? new Color(1f, 0.82f, 0.2f) : new Color(0f, 0f, 0f, 0.35f);
        }
    }
}
