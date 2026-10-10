using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unattended test drive of <see cref="SimpleCar"/>. Writing CarTestTrigger.txt
/// next to the project enters play mode, starts single player, puts the player
/// in the car and drives a fixed programme — straight, hard right, brake, left
/// through the kerbs — logging speed, wheels on the ground, height above the
/// road and tilt, with chase-camera frames. Then it leaves play mode.
/// Results: A:\blender_work\unity_export\cartest.
/// </summary>
[InitializeOnLoad]
public static class DexstandCarTest
{
    private const string OutDir = @"A:\blender_work\unity_export\cartest";
    private const string RunningKey = "Dexstand.CarTestRunning";

    private static readonly string TriggerPath =
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "CarTestTrigger.txt"));

    private const string ModeKey = "Dexstand.CarTestMode";

    // Seconds from the moment the player sits in: until, x = steer, y = throttle.
    private static readonly (float until, Vector2 input)[] MapProgramme =
    {
        (4f, new Vector2(0f, 1f)),
        (7f, new Vector2(1f, 1f)),
        (9f, new Vector2(0f, -1f)),
        (14f, new Vector2(-0.6f, 1f)),
        (16f, new Vector2(0f, -1f)),
    };

    // On a flat pad far below the map ("track" in the trigger file): full throttle to top speed,
    // a sweeping turn at speed, then braking to a stop.
    private static readonly (float until, Vector2 input)[] TrackProgramme =
    {
        (16f, new Vector2(0f, 1f)),
        (21f, new Vector2(0.5f, 1f)),
        (25f, new Vector2(0f, -1f)),
    };

    // "kerb": on the pad, square across two pavements — the first at city pace, the second faster.
    private static readonly (float until, Vector2 input)[] KerbProgramme =
    {
        (1f, new Vector2(0f, 0f)),
        (8.5f, new Vector2(0f, 0.6f)),
        (11f, new Vector2(0f, -1f)),
    };

    private static (float until, Vector2 input)[] Programme =>
        EditorPrefs.GetString(ModeKey, "map") switch
        {
            "track" => TrackProgramme,
            "kerb" => KerbProgramme,
            "ped" => KerbProgramme,
            _ => MapProgramme,
        };

    private static float reached80 = -1f, reached120 = -1f;

    private static double nextPoll, stageTime, lastSample;
    private static int stage, shot;
    private static bool asked;
    private const float KerbHeight = 0.10f;
    private static Pedestrian runOver, shotDead, onMap;
    private static readonly System.Collections.Generic.List<GameObject> balls = new System.Collections.Generic.List<GameObject>();
    private static Vector3 runOverStart, shotStart, rammedStart, onMapStart;
    private static LooseCar rammed;
    private static int smoothFrames;

    /// <summary>A walker as MapPedestrians makes one, standing still.</summary>
    private static Pedestrian TestPerson(Vector3 at)
    {
        var peds = Object.FindFirstObjectByType<MapPedestrians>();
        if (peds == null || peds.models == null || peds.models.Length == 0) return null;
        var go = Object.Instantiate(peds.models[0]);
        go.transform.localScale = Vector3.one * peds.transform.lossyScale.x;
        go.transform.position = at;
        var an = go.GetComponentInChildren<Animator>() ?? go.AddComponent<Animator>();
        an.runtimeAnimatorController = peds.controller;
        var cap = go.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 0.9f, 0f);
        cap.height = 1.75f;
        cap.radius = 0.25f;
        return go.AddComponent<Pedestrian>();
    }

    private static string PersonState(Pedestrian p, Vector3 from)
    {
        if (p == null) return "gone";
        var bodies = p.GetComponentsInChildren<Rigidbody>();
        var hips = p.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Hips);
        var head = p.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Head);
        return $"dead {p.Dead}, {bodies.Length} bodies, hips {(hips != null ? (hips.position - from).ToString("F2") : "?")} from start, " +
               $"head scale {(head != null ? head.localScale.x.ToString("F2") : "?")}";
    }
    private static SimpleCar car;
    private static StringBuilder log;
    private static float maxLift, maxVy, minUp = 1f, topSpeed;
    private static int airborneSamples;
    // frame rate over the drive, to see what the map costs
    private static int startFrame, spotFrame, spot;
    private static float startTime, spotTime, worstFrame, spotWorst;
    private static PlayerController player;
    private static StringBuilder fpsLog;

    // "fps" in the trigger file: scene x, z of busy spots of the region map (the bus station is the origin)
    private static readonly (string name, Vector2 pos)[] FpsSpots =
    {
        ("bus station", new Vector2(2f, 3f)),
        ("rewe", new Vector2(18.6f, 17f)),
        ("north", new Vector2(117f, -240f)),
        ("home", new Vector2(154f, -404f)),
        ("gummersbach", new Vector2(-308f, -3004.3f)),
        ("kaufland", new Vector2(447.7f, -1326f)),
    };

    private static readonly StringBuilder hits = new StringBuilder();

    static DexstandCarTest()
    {
        EditorApplication.update += Tick;
        Application.logMessageReceived += (message, _, _) =>
        {
            if (stage == 4 && message.StartsWith("[CarHit]")) hits.AppendLine(message);
        };
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying)
        {
            if (EditorPrefs.GetBool(RunningKey, false) && !EditorApplication.isPlayingOrWillChangePlaymode)
                EditorPrefs.SetBool(RunningKey, false); // play was stopped by hand
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || !File.Exists(TriggerPath)) return;
            string mode = File.ReadAllText(TriggerPath).Trim();
            EditorPrefs.SetString(ModeKey, mode == "track" || mode == "dyn" || mode == "manual" || mode == "traffic" || mode == "fps" || mode == "kerb" || mode == "ped" || mode == "police" ? mode : "map");
            File.Delete(TriggerPath);
            Directory.CreateDirectory(OutDir);
            EditorPrefs.SetBool(RunningKey, true);
            EditorApplication.EnterPlaymode();
            return;
        }
        if (!EditorPrefs.GetBool(RunningKey, false)) return;

        double now = EditorApplication.timeSinceStartup;
        switch (stage)
        {
            case 0: // wait for the scene, then start a match
                stageTime = now;
                stage = 1;
                return;
            case 1:
                if (now - stageTime < 2.0) return;
                var menu = Object.FindFirstObjectByType<GameMenu>();
                if (menu != null) menu.StartSinglePlayer();
                stageTime = now;
                stage = 2;
                return;
            case 2: // wait for our player, then walk them to the car and sit in
                car = Object.FindFirstObjectByType<SimpleCar>();
                PlayerController me = null;
                foreach (var pc in Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
                    if (pc.Object != null && pc.Object.IsValid && pc.Object.HasInputAuthority) me = pc;
                if (me != null && EditorPrefs.GetString(ModeKey, "map") == "police")
                {
                    // two kills' worth of stars on our player, standing at the bus station: then watch them come
                    if (Police.Instance == null) { if (now - stageTime > 30.0) Finish("FAILED: no Police in the scene"); return; }
                    player = me;
                    Police.Report(me.Object.InputAuthority, true);
                    Police.Report(me.Object.InputAuthority, true);
                    fpsLog = new StringBuilder();
                    stageTime = now;
                    spot = 0;
                    stage = 20;
                    return;
                }
                if (me != null && EditorPrefs.GetString(ModeKey, "map") == "traffic")
                {
                    // the town's traffic, three times as much as usual, watched from where the player stands
                    var traffic = Object.FindFirstObjectByType<MapTraffic>();
                    if (traffic == null) { if (now - stageTime > 30.0) Finish("FAILED: no MapTraffic"); return; }
                    traffic.count = 12;
                    MapTraffic.JunctionsTaken = 0;
                    player = me;
                    fpsLog = new StringBuilder();
                    trafficSeen.Clear();
                    trafficOverlaps = trafficJumps = trafficOffRoad = trafficSamples = trafficWaits = trafficBlocks = 0;
                    trafficPitch = trafficTop = 0f;
                    dynStart = Time.time;
                    spot = 0;
                    stage = 30;
                    return;
                }
                if (me != null && EditorPrefs.GetString(ModeKey, "map") == "fps")
                {
                    // frame rate on foot: stand at a few busy spots of the map, a few seconds each
                    player = me;
                    stageTime = now;
                    startFrame = Time.frameCount;
                    startTime = Time.realtimeSinceStartup;
                    worstFrame = 0f;
                    fpsLog = new StringBuilder();
                    spot = -1;
                    stage = 10;
                    return;
                }
                if (car == null || me == null)
                {
                    if (now - stageTime > 30.0) Finish("FAILED: no car or no local player after 30 s");
                    return;
                }
                me.Teleport(car.transform.position - car.transform.right * 0.9f + Vector3.up * 0.2f);
                asked = false;
                smoothFrames = 0;
                stageTime = now;
                stage = 3;
                return;
            case 3:
                if (now - stageTime < 1.2) return; // the car looks for the player twice a second
                // getting in is a request that rides the network input: ask once, then give it a few ticks
                if (!asked) { car.TryToggle(); asked = true; }
                if (!car.IsDriven)
                {
                    if (now - stageTime > 4.0) Finish("FAILED: could not get into the car");
                    return;
                }
                // the map streams its tiles in after the start, each a second's hitch in the editor: a timed
                // programme run through that never gets where it is going
                smoothFrames = Time.unscaledDeltaTime < 0.05f ? smoothFrames + 1 : 0;
                if (smoothFrames < 60 && now - stageTime < 90.0) return;
                dynState = 0;
                dynStart = -1f;
                zeroTo100 = brakeFrom = circleG = circleRoll = circleSlip = settleBounce = 0f;
                dynLog.Clear();
                yawTrace.Clear();
                manState = manLever = manShifts = manStallsAtStart = 0;
                manSince = manGas = manClutch = manLastSpeed = manLastT = manTo50 = manWorstJerk = manWorstDip = manCreep = manPedalT = 0f;
                manKey = false;
                manLog.Clear();
                if (EditorPrefs.GetString(ModeKey, "map") is "track" or "dyn" or "manual")
                {
                    var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pad.name = "CarTestTrack_col";
                    pad.transform.position = new Vector3(0f, -501f, 0f);
                    pad.transform.localScale = new Vector3(3000f, 2f, 3000f);
                    var rbody = car.GetComponent<Rigidbody>();
                    rbody.position = new Vector3(0f, -499.7f, -1000f);
                    rbody.rotation = Quaternion.identity;
                    rbody.linearVelocity = rbody.angularVelocity = Vector3.zero;
                    car.transform.SetPositionAndRotation(rbody.position, rbody.rotation);
                }
                runOver = shotDead = onMap = null;
                rammed = null;
                if (EditorPrefs.GetString(ModeKey, "map") == "ped")
                {
                    // the pad under the car, a passer-by 10 units ahead to run down and one aside to shoot in the head
                    var o = car.transform.position;
                    var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pad.name = "CarTestTrack_col";
                    pad.transform.position = new Vector3(o.x, -501f, o.z + 10f);
                    pad.transform.localScale = new Vector3(60f, 2f, 60f);
                    var rbody = car.GetComponent<Rigidbody>();
                    rbody.position = new Vector3(o.x, -499.7f, o.z);
                    rbody.rotation = Quaternion.identity;
                    rbody.linearVelocity = rbody.angularVelocity = Vector3.zero;
                    car.transform.SetPositionAndRotation(rbody.position, rbody.rotation);
                    runOverStart = new Vector3(o.x, -500f, o.z + 10f);
                    shotStart = new Vector3(o.x + 3f, -500f, o.z + 3f);
                    runOver = TestPerson(runOverStart);
                    shotDead = TestPerson(shotStart);
                    // one on the map's own ground, where the car was parked: does a body stay on top of it?
                    var mapGround = Physics.RaycastAll(o + Vector3.right * 1.5f + Vector3.up * 20f, Vector3.down, 60f)
                        .Where(h => h.collider.name.EndsWith("_col")).OrderByDescending(h => h.point.y).FirstOrDefault();
                    onMap = mapGround.collider != null ? TestPerson(mapGround.point) : null;
                    onMapStart = mapGround.point;
                    // and two plain balls dropped next to it: does anything rest on this ground at all?
                    foreach (float r in new[] { 0.03f, 0.15f })
                    {
                        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        ball.name = "TestBall" + r;
                        ball.transform.localScale = Vector3.one * r * 2f;
                        ball.transform.position = onMapStart + new Vector3(0.6f + r * 4f, 0.5f, 0f);
                        ball.AddComponent<Rigidbody>().collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                        balls.Add(ball);
                    }
                    if (runOver == null) { Finish("FAILED: no MapPedestrians models to test with"); return; }
                    // and a parked car further on, as MapParkedCars puts one down
                    var model = Resources.LoadAll<GameObject>("Cars").FirstOrDefault();
                    if (model != null)
                    {
                        var template = MapParkedCars.Template(model, null);
                        var parked = Object.Instantiate(template);
                        parked.transform.localScale = Vector3.one * CarSpecs.Get(model.name).length / 3f;
                        rammedStart = new Vector3(o.x + 0.2f, -500f, o.z + 18f);
                        parked.transform.SetPositionAndRotation(rammedStart, Quaternion.Euler(0f, 90f, 0f));
                        parked.SetActive(true);
                        rammed = parked.GetComponent<LooseCar>();
                    }
                }
                if (EditorPrefs.GetString(ModeKey, "map") == "kerb")
                {
                    // a flat pad with two pavements across it, kerbs as high as the map's (0.10 onto WalksCol) —
                    // straight under the car, so the map streamer has no new cells to load for it
                    var o = car.transform.position;
                    var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pad.name = "CarTestTrack_col";
                    pad.transform.position = new Vector3(o.x, -501f, o.z + 10f);
                    pad.transform.localScale = new Vector3(60f, 2f, 60f);
                    foreach (float z in new[] { 6f, 18f })
                    {
                        var walk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        walk.name = "CarTestWalk_col";
                        walk.transform.position = new Vector3(o.x, -500f + KerbHeight * 0.5f, o.z + z + 2f);
                        walk.transform.localScale = new Vector3(40f, KerbHeight, 4f);
                    }
                    var rbody = car.GetComponent<Rigidbody>();
                    rbody.position = new Vector3(o.x, -499.7f, o.z);
                    rbody.rotation = Quaternion.identity;
                    rbody.linearVelocity = rbody.angularVelocity = Vector3.zero;
                    car.transform.SetPositionAndRotation(rbody.position, rbody.rotation);
                }
                reached80 = reached120 = -1f;
                log = new StringBuilder("t;speed;wheels;lift;vy;up;x;z;y;under;state;gear\n");
                maxLift = maxVy = topSpeed = 0f;
                minUp = 1f;
                airborneSamples = shot = 0;
                stageTime = lastSample = now;
                startFrame = Time.frameCount;
                startTime = Time.realtimeSinceStartup;
                worstFrame = 0f;
                stage = 4;
                return;
            case 20:
                {
                    // once a second for a minute: stars, where the police are, our health; frames from above
                    float since = (float)(now - stageTime);
                    if (since < spot) return;
                    spot += 1;
                    var health = player.GetComponentInParent<PlayerHealth>();
                    var here = player.transform.position;
                    string status = Police.Instance.Status(here, out var nearest);
                    fpsLog.AppendLine($"{since:F0}s stars {Police.Instance.StarsOf(player.Object.InputAuthority)}, {status}, " +
                                      $"health {(health != null ? health.Health.ToString("F0") : "?")}");
                    if (spot % 10 == 0) LookShot(spot / 10, here, nearest);
                    if (since >= 60f) Finish("police run:\n" + fpsLog);
                    return;
                }
            case 30:
                {
                    float since = Time.time - dynStart;
                    if (since < spot * 0.5f) return;
                    spot++;
                    TrafficSample(spot);
                    if (since >= 150f)
                        Finish($"TRAFFIC {trafficSamples} car-samples over {since:F0} s, {trafficSeen.Count} cars seen: " +
                               $"overlapping pairs {trafficOverlaps}, jumps {trafficJumps}, off the road {trafficOffRoad}, " +
                               $"steepest pitch {trafficPitch:F1}°, fastest {trafficTop * 10.8f:F0} km/h, " +
                               $"junctions entered {MapTraffic.JunctionsTaken}, samples waiting at one {trafficWaits}, held up {trafficBlocks}\n{fpsLog}");
                    return;
                }
            case 10:
                {
                    // one spot after another, 4 s each, however long a hitch on arrival takes
                    if (spot < 0 || Time.realtimeSinceStartup - spotTime >= 8f)
                    {
                        if (spot >= 0)
                        {
                            float spotFps = (Time.frameCount - spotFrame) / Mathf.Max(0.01f, Time.realtimeSinceStartup - spotTime);
                            // what the map has there: the collider underfoot and the tiles loaded
                            var sp = FpsSpots[spot].pos;
                            string under = string.Join("+", Physics.RaycastAll(new Vector3(sp.x, 1000f, sp.y), Vector3.down, 3000f)
                                .Where(h => h.collider.name.EndsWith("_col")).Select(h => h.collider.name.Split('_')[0]).Distinct());
                            if (under == "") under = "NOTHING";
                            var tileRoot = Object.FindFirstObjectByType<MapTileStreamer>();
                            int loaded = tileRoot != null ? tileRoot.transform.Cast<Transform>().Count(c => c.name.StartsWith("Tile_")) : -1;
                            fpsLog.Append($"{FpsSpots[spot].name} {spotFps:F0} fps (worst {spotWorst * 1000f:F0} ms, under {under}, {loaded} tiles); ");
                        }
                        spot++;
                        spotWorst = 0f;
                        if (spot >= FpsSpots.Length)
                        {
                            float fps = (Time.frameCount - startFrame) / Mathf.Max(0.01f, Time.realtimeSinceStartup - startTime);
                            Finish($"FPS average {fps:F0}, worst frame {worstFrame * 1000f:F0} ms: {fpsLog}");
                            return;
                        }
                        var p = FpsSpots[spot].pos;
                        float ground = Physics.Raycast(new Vector3(p.x, 1000f, p.y), Vector3.down, out var gh, 3000f) ? gh.point.y : 0f;
                        player.Teleport(new Vector3(p.x, ground + 0.3f, p.y));
                        spotFrame = Time.frameCount;
                        spotTime = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - spotTime > 1f)
                    {
                        // the first second after a jump loads the new cells: counted apart from steady play
                        worstFrame = Mathf.Max(worstFrame, Time.unscaledDeltaTime);
                        spotWorst = Mathf.Max(spotWorst, Time.unscaledDeltaTime);
                    }
                    return;
                }
            case 4:
                float t = (float)(now - stageTime);
                if (EditorPrefs.GetString(ModeKey, "map") == "dyn" || EditorPrefs.GetString(ModeKey, "map") == "manual")
                {
                    if (EditorPrefs.GetString(ModeKey, "map") == "dyn") Dyn(t);
                    else Manual(t);
                    if (now - lastSample >= 0.1)
                    {
                        lastSample = now;
                        Sample(t);
                    }
                    return;
                }
                int step = 0;
                while (step < Programme.Length && t > Programme[step].until) step++;
                if (shotDead != null && !shotDead.Dead && t > 2f)
                {
                    var head = shotDead.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.Head);
                    if (head != null) shotDead.Shot(25f, head.position + Vector3.up * 0.03f, Vector3.forward, null);
                }
                if (onMap != null && !onMap.Dead && t > 1.5f && shot < 90)
                {
                    shot = 90;
                    PersonShot(onMapStart);
                    // and the cockpit, from the driver's seat
                    var tr = car.transform;
                    var b = new Bounds(tr.position, Vector3.zero);
                    foreach (var r in car.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                    var eye = b.center + Vector3.up * b.extents.y * 0.25f - tr.right * b.extents.x * 0.3f - tr.forward * b.extents.z * 0.1f;
                    ViewShot("cockpit.png", eye, eye + tr.forward + Vector3.down * 0.25f);
                }
                if (onMap != null && !onMap.Dead && t > 2f)
                {
                    onMap.Shot(100f, onMap.transform.position + Vector3.up * 0.3f, Vector3.forward, null);
                    // where each part starts, against the ground: one already in it gets pushed out the far side
                    var start = new StringBuilder("ragdoll at birth, bottom above ground: ");
                    foreach (var c in onMap.GetComponentsInChildren<Collider>())
                        if (c.enabled) start.Append($"{c.name.Replace("mixamorig:", "")} {c.bounds.min.y - onMapStart.y:F3}; ");
                    hits.AppendLine(start.ToString());
                }
                if (step >= Programme.Length)
                {
                    SimpleCar.ScriptedInput = null;
                    if (runOver != null || shotDead != null)
                    {
                        hits.AppendLine("run over: " + PersonState(runOver, runOverStart) + "; shot in the head: " + PersonState(shotDead, shotStart));
                        if (onMap != null)
                        {
                            // every body part against the highest map collider under it: below it is sunk in
                            float worst = float.MaxValue;
                            string which = "";
                            foreach (var rb in onMap.GetComponentsInChildren<Rigidbody>())
                            {
                                var p = rb.worldCenterOfMass;
                                // from just above the part: from high up the bus station's canopy counted as ground
                                var top = Physics.RaycastAll(p + Vector3.up * 0.3f, Vector3.down, 20f)
                                    .Where(h => h.collider.name.EndsWith("_col")).OrderByDescending(h => h.point.y).FirstOrDefault();
                                if (top.collider != null && p.y - top.point.y < worst)
                                {
                                    worst = p.y - top.point.y;
                                    var col = rb.GetComponent<Collider>();
                                    which = $"{rb.name} under {top.collider.name} (y {p.y:F2} vs {top.point.y:F2}, start {onMapStart.y:F2}); " +
                                            $"collider {(col != null ? col.GetType().Name + (col.enabled ? " on" : " OFF") + " bounds " + col.bounds.min.y.ToString("F3") + ".." + col.bounds.max.y.ToString("F3") : "none")}" +
                                            $"{(col is CapsuleCollider cc ? $" r {cc.radius:F3} h {cc.height:F3} dir {cc.direction}" : "")}, " +
                                            $"bone scale {rb.transform.lossyScale.ToString("F3")}, kinematic {rb.isKinematic}, sleeping {rb.IsSleeping()}, " +
                                            $"parent body {(rb.TryGetComponent<CharacterJoint>(out var cj) && cj.connectedBody != null ? cj.connectedBody.name : "-")}";
                                }
                            }
                            // which way the ground's triangles face there, and whether a ray from below meets them
                            var probe = onMapStart + Vector3.right * 0.2f;
                            bool down = Physics.Raycast(probe + Vector3.up * 1f, Vector3.down, out var fromAbove, 3f);
                            Physics.queriesHitBackfaces = true;
                            bool up = Physics.Raycast(probe - Vector3.up * 1f, Vector3.up, out var fromBelow, 3f);
                            Physics.queriesHitBackfaces = false;
                            hits.AppendLine($"ground probe: from above {(down ? fromAbove.collider.name + " normal " + fromAbove.normal : "nothing")}, " +
                                            $"from below {(up ? fromBelow.collider.name + " normal " + fromBelow.normal : "nothing")}; " +
                                            $"collider {(down ? fromAbove.collider.GetType().Name : "")}");
                            foreach (var ball in balls)
                                if (ball != null)
                                    hits.AppendLine($"{ball.name}: bottom at {ball.transform.position.y - ball.transform.localScale.y * 0.5f:F3}, ground {onMapStart.y:F3}");
                            balls.Clear();
                            hits.AppendLine($"dead on the map: lowest body part {worst:F3} above the ground, {which} " +
                                            $"(a limb is ~{0.05f * onMap.transform.lossyScale.y:F3} thick)");
                        }
                        if (rammed != null)
                            hits.AppendLine($"rammed car: loose {rammed.Body != null}, moved {(rammed.transform.position - rammedStart).ToString("F2")}, " +
                                            $"turned to {rammed.transform.eulerAngles.y:F0}° (was 90)");
                    }
                    float fps = (Time.frameCount - startFrame) / Mathf.Max(0.01f, Time.realtimeSinceStartup - startTime);
                    Finish($"OK top speed {topSpeed:F2} ({topSpeed * 10.8f:F0} km/h), 0-80 in {reached80:F1} s, " +
                           $"0-120 in {reached120:F1} s, max lift {maxLift:F3}, max vertical speed {maxVy:F2}, " +
                           $"lowest up {minUp:F2}, airborne samples {airborneSamples}, " +
                           $"average {fps:F0} fps, worst frame {worstFrame * 1000f:F0} ms");
                    return;
                }
                if (t > 1f) worstFrame = Mathf.Max(worstFrame, Time.unscaledDeltaTime);
                SimpleCar.ScriptedInput = Programme[step].input;
                if (now - lastSample >= 0.1)
                {
                    lastSample = now;
                    Sample(t);
                }
                if (t >= shot * 3.5f + 1f && shot < 5) Shot(shot++);
                return;
        }
    }

    // "dyn": a road test as a magazine does it, on the flat pad, against the brochure: 0-100, braking from 100, the
    // car on a steady circle at 60 with the wheel at the keyboard's full lock, then a lift-off and stop.
    private static int dynState;
    private static float dynSince, zeroTo100, brakeFrom, brakeStart, circleG, circleRoll, circleSlip, settleBounce;
    private static Vector3 brakeAt;
    private static readonly StringBuilder dynLog = new StringBuilder();
    private static readonly System.Collections.Generic.List<(float t, float yaw)> yawTrace = new System.Collections.Generic.List<(float t, float yaw)>();

    private static float dynStart = -1f;

    private static void Dyn(float wallTime)
    {
        // game time: the editor's hitches while the map streams in stop the physics clock, not the wall one
        if (dynStart < 0f) dynStart = Time.time;
        float t = Time.time - dynStart;
        var rb = car.GetComponent<Rigidbody>();
        var tr = car.transform;
        float kmh = car.Speed * 10.8f;
        float lateralG = Mathf.Abs(rb.linearVelocity.magnitude * 3f * rb.angularVelocity.y) / 9.81f;
        float roll = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, tr.forward), tr.up, tr.forward);
        float hold60 = Mathf.Clamp01((60f - kmh) * 0.15f + 0.25f);
        switch (dynState)
        {
            case 0: // standing start
                if (t < 1.5f)
                {
                    SimpleCar.ScriptedInput = Vector2.zero;
                    settleBounce = Mathf.Max(settleBounce, Mathf.Abs(rb.linearVelocity.y));
                    dynSince = t;
                    return;
                }
                SimpleCar.ScriptedInput = new Vector2(0f, 1f);
                if (kmh >= 100f) { zeroTo100 = t - dynSince; brakeAt = tr.position; brakeFrom = kmh; brakeStart = t; dynState = 1; }
                else if (t - dynSince > 40f) { zeroTo100 = -1f; dynState = 1; brakeAt = tr.position; brakeFrom = kmh; brakeStart = t; }
                return;
            case 1: // full brake
                SimpleCar.ScriptedInput = new Vector2(0f, -1f);
                if (kmh < 0.5f)
                {
                    var d = (tr.position - brakeAt) * 3f;
                    d.y = 0f;
                    dynLog.Append($"braking from {brakeFrom:F0} km/h: {d.magnitude:F1} m in {t - brakeStart:F1} s; ");
                    SimpleCar.ScriptedInput = Vector2.zero;
                    dynSince = t;
                    dynState = 2;
                }
                return;
            case 2: // up to 60, straight
                SimpleCar.ScriptedInput = new Vector2(0f, Mathf.Max(0.02f, kmh < 55f ? 1f : hold60));
                if (kmh > 57f && t - dynSince > 3f) { dynSince = t; dynState = 3; }
                return;
            case 3: // the circle: full lock as a keyboard gives it, the speed held
                SimpleCar.ScriptedInput = new Vector2(1f, Mathf.Max(0.02f, hold60));
                // how soon the car answers the wheel: the turn rate's rise after the key goes down
                yawTrace.Add((t - dynSince, Mathf.Abs(rb.angularVelocity.y)));
                if (t - dynSince > 4f)
                {
                    circleG = Mathf.Max(circleG, lateralG);
                    circleRoll = Mathf.Max(circleRoll, Mathf.Abs(roll));
                    var local = tr.InverseTransformDirection(rb.linearVelocity);
                    circleSlip = Mathf.Max(circleSlip, Mathf.Abs(Mathf.Atan2(local.x, Mathf.Abs(local.z)) * Mathf.Rad2Deg));
                }
                if (t - dynSince > 9f)
                {
                    float final = yawTrace.Where(y => y.t > 7f).Select(y => y.yaw).DefaultIfEmpty(0f).Average();
                    float t63 = yawTrace.Where(y => y.yaw >= 0.63f * final).Select(y => y.t).DefaultIfEmpty(-1f).First();
                    float t90 = yawTrace.Where(y => y.yaw >= 0.9f * final).Select(y => y.t).DefaultIfEmpty(-1f).First();
                    float peak = yawTrace.Select(y => y.yaw).DefaultIfEmpty(0f).Max();
                    dynLog.Append($"turn-in: 63 % in {t63:F2} s, 90 % in {t90:F2} s, overshoot {(final > 0f ? (peak / final - 1f) * 100f : 0f):F0} %; ");
                    dynLog.Append($"circle at {kmh:F0} km/h: {circleG:F2} g, roll {circleRoll:F1}°, body slip {circleSlip:F1}°, " +
                                  $"radius {(Mathf.Abs(rb.angularVelocity.y) > 0.01f ? rb.linearVelocity.magnitude * 3f / Mathf.Abs(rb.angularVelocity.y) : 0f):F0} m; ");
                    dynSince = t;
                    dynState = 4;
                }
                return;
            case 4: // straighten up and stop
                SimpleCar.ScriptedInput = new Vector2(0f, -1f);
                if (kmh < 0.5f || t - dynSince > 10f)
                {
                    Finish($"DYN 0-100 in {zeroTo100:F1} s; {dynLog}settling bounce {settleBounce:F2} u/s, upright {Vector3.Dot(tr.up, Vector3.up):F3}");
                }
                return;
        }
    }

    // "manual": a learner on the keyboard, pedals moving as SimpleCar.ReadDriver moves them — key with the clutch
    // down, first, clutch up with no gas (creep), then gas and changes up to third at 3000 rpm, then a stop.
    private static int manState, manLever, manShifts, manStallsAtStart;
    private static float manSince, manGas, manClutch, manLastSpeed, manLastT, manTo50, manWorstJerk, manWorstDip, manCreep;
    private static bool manKey, manMoved;
    private static float manPedalT;
    private static readonly StringBuilder manLog = new StringBuilder();

    private static void Manual(float wallTime)
    {
        if (dynStart < 0f) { dynStart = Time.time; manLastT = 0f; }
        float t = Time.time - dynStart;
        float dt = Mathf.Max(1e-3f, t - manLastT);
        var d = car.Drivetrain;
        float kmh = car.Speed * 10.8f;
        float accel = (car.Speed * 3f - manLastSpeed) / dt;   // m/s²
        bool fresh = t - manLastT > 0.05f;
        bool wantClutch = true, wantGas = false;
        switch (manState)
        {
            case 0: // key with the clutch down
                manKey = !d.Running && t > 1f;
                if (d.Running) { manKey = false; manLever = 1; manShifts++; manSince = t; manStallsAtStart = d.Stalls; manState = 1; }
                break;
            case 1: // first, clutch up by itself, no gas: a modern car creeps
                wantClutch = t - manSince < 0.5f;
                if (t - manSince > 4f)
                {
                    manCreep = kmh;
                    manLog.Append($"creep in first, no gas: {kmh:F1} km/h, {d.Rpm:F0} rpm, stalled {d.Stalls - manStallsAtStart}x; ");
                    if (!d.Running) { manState = 0; manLever = 0; manShifts++; break; }
                    manSince = t;
                    manState = 2;
                }
                break;
            case 2: // gas, up to 3000, then change up
                wantClutch = false;
                wantGas = true;
                if (fresh && t - manSince > 0.6f && Mathf.Abs(accel) > 0f) manWorstDip = Mathf.Min(manWorstDip, accel);
                if (manTo50 <= 0f && kmh >= 50f) manTo50 = t - manSince;
                if (d.Rpm > 3000f && manLever < 3) { manSince = t; manState = 3; manMoved = false; }
                if (manLever >= 3 && kmh >= 60f) { manSince = t; manState = 4; }
                break;
            case 3: // the change: off the gas, clutch down, lever, then back up as the keyboard lets it
                wantClutch = t - manSince < 0.45f;
                wantGas = t - manSince > 0.6f;
                if (t - manSince > 0.3f && manClutch > 0.9f && d.Gear == manLever && !manMoved) { manLever++; manShifts++; manMoved = true; }
                if (t - manSince > 0.6f && fresh) manWorstJerk = Mathf.Max(manWorstJerk, Mathf.Abs(accel));
                if (t - manSince > 1.6f) { manSince = t; manState = 2; }
                break;
            case 4: // brake, clutch down under 15
                wantClutch = kmh < 15f;
                SimpleCar.ScriptedInput = Vector2.zero;
                if (kmh < 0.3f || t - manSince > 15f)
                {
                    Finish($"MANUAL {manLog}0-50 in {manTo50:F1} s (gas from the creep), worst push after a change {manWorstJerk / 9.81f:F2} g, " +
                           $"worst dip under gas {manWorstDip / 9.81f:F2} g, gear {d.Gear}, running {d.Running}, " +
                           $"stalls {d.Stalls - manStallsAtStart}, grinds by count of shifts {manShifts}");
                    SimpleCar.ScriptedPedals = null;
                    return;
                }
                break;
        }
        // the keyboard's own pedal travel (SimpleCar.ReadDriver)
        dt = Mathf.Max(0f, t - manPedalT);
        manPedalT = t;
        manGas = Mathf.MoveTowards(manGas, wantGas ? 1f : 0f, dt * (wantGas ? 2.5f : 6f));
        float rate = wantClutch ? 6f : manClutch > 0.33f && manClutch < 0.67f ? 0.45f : 3f;
        manClutch = Mathf.MoveTowards(manClutch, wantClutch ? 1f : 0f, dt * rate);
        float brake = manState == 4 ? 1f : 0f;
        var sw = CarSwitch.Manual | (manKey ? CarSwitch.Key : 0);
        SimpleCar.ScriptedInput = Vector2.zero;
        SimpleCar.ScriptedPedals = new CarPedals
        {
            gas = (byte)(manGas * 255f), brake = (byte)(brake * 255f), clutch = (byte)(manClutch * 255f),
            switches = (byte)sw, lever = (byte)(manLever + 1), shifts = (byte)manShifts,
        };
        if (fresh)
        {
            manLastT = t;
            manLastSpeed = car.Speed * 3f;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<GameObject, (Vector3 pos, float time)> trafficSeen =
        new System.Collections.Generic.Dictionary<GameObject, (Vector3, float)>();
    private static int trafficOverlaps, trafficJumps, trafficOffRoad, trafficSamples, trafficWaits, trafficBlocks;
    private static float trafficPitch, trafficTop;

    /// <summary>
    /// Every half second: cars inside one another, cars that moved further than they drive (teleports), cars off the
    /// road surface or sunk in it, pitch; a frame from behind one of them every 15 s.
    /// </summary>
    private static void TrafficSample(int index)
    {
        var traffic = Object.FindFirstObjectByType<MapTraffic>();
        if (traffic == null) return;
        var list = traffic.Snapshot();
        float now = Time.time;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c.go.GetComponent<LooseCar>()?.Body != null) continue;
            trafficSamples++;
            trafficTop = Mathf.Max(trafficTop, c.speed);
            if (c.waiting) trafficWaits++;
            if (c.blocked) trafficBlocks++;
            for (int j = i + 1; j < list.Count; j++)
                if ((list[j].pos - c.pos).magnitude < (c.length + list[j].length) * 0.25f)
                {
                    trafficOverlaps++;
                    fpsLog.AppendLine($"{now - dynStart:F1}s overlap at {c.pos.x:F0} {c.pos.z:F0}");
                }
            if (trafficSeen.TryGetValue(c.go, out var last) && c.pos.y < 4000f && last.pos.y < 4000f)
            {
                float moved = Vector3.Distance(last.pos, c.pos), most = (c.speed + 1f) * (now - last.time) * 2f + 0.2f;
                if (moved > most)
                {
                    trafficJumps++;
                    fpsLog.AppendLine($"{now - dynStart:F1}s jump {moved:F1} u at {c.pos.x:F0} {c.pos.z:F0}");
                }
            }
            trafficSeen[c.go] = (c.pos, now);
            var ground = Physics.RaycastAll(c.pos + Vector3.up * 1.5f, Vector3.down, 4f)
                .Where(h => h.collider.name.EndsWith("_col")).Select(h => h.point.y).DefaultIfEmpty(float.NaN).Max();
            if (float.IsNaN(ground) || Mathf.Abs(ground - c.pos.y) > 0.12f) trafficOffRoad++;
            float pitch = Mathf.Abs(Mathf.DeltaAngle(0f, c.go.transform.eulerAngles.x));
            trafficPitch = Mathf.Max(trafficPitch, pitch);
            if (pitch > 20f) fpsLog.AppendLine($"{now - dynStart:F1}s pitch {pitch:F0}° at {c.pos.x:F0} {c.pos.y:F1} {c.pos.z:F0}");
        }
        if (index % 30 == 0 && list.Count > 0)
        {
            var me = player.transform.position;
            var c = list.OrderBy(x => (x.pos - me).sqrMagnitude).First();
            var back = c.go.transform.TransformPoint(new Vector3(0.6f, 1.2f, -2.2f));
            ViewShot($"traffic{index / 30}.png", back, c.pos + Vector3.up * 0.2f + c.go.transform.forward * c.length * 0.8f);
            fpsLog.AppendLine($"{Time.time - dynStart:F0}s: {list.Count} cars, shot of one at {c.speed * 10.8f:F0} km/h");
        }
    }

    private static void Sample(float t)
    {
        var tr = car.transform;
        var rb = car.GetComponent<Rigidbody>();
        float ground = Physics.Raycast(tr.position + Vector3.up * 2f, Vector3.down, out var hit, 10f, ~0,
                                       QueryTriggerInteraction.Ignore) && hit.collider.attachedRigidbody != rb
            ? hit.point.y : tr.position.y;
        // The ray from above can land on the car; measure the root against the terrain under it instead.
        foreach (var h in Physics.RaycastAll(tr.position + Vector3.up * 2f, Vector3.down, 10f))
            if (h.collider.attachedRigidbody != rb && h.collider.name.EndsWith("_col")) ground = Mathf.Max(ground, h.point.y);
        float lift = tr.position.y - ground;
        float up = Vector3.Dot(tr.up, Vector3.up);
        topSpeed = Mathf.Max(topSpeed, Mathf.Abs(car.Speed));
        if (reached80 < 0f && car.Speed * 10.8f >= 80f) reached80 = t;
        if (reached120 < 0f && car.Speed * 10.8f >= 119f) reached120 = t;
        maxLift = Mathf.Max(maxLift, lift);
        maxVy = Mathf.Max(maxVy, Mathf.Abs(rb.linearVelocity.y));
        minUp = Mathf.Min(minUp, up);
        if (car.WheelsOnGround == 0) airborneSamples++;
        string under = Physics.Raycast(tr.position + Vector3.up * 0.5f, Vector3.down, out var below, 50f, ~0,
                                       QueryTriggerInteraction.Ignore) ? $"{below.collider.name}@{below.distance:F2}" : "nothing";
        log.AppendLine(string.Join(";", new[] { t, car.Speed, car.WheelsOnGround, lift, rb.linearVelocity.y, up, tr.position.x, tr.position.z, tr.position.y }
            .Select(v => System.Convert.ToSingle(v).ToString("F3", CultureInfo.InvariantCulture))) + ";" + under +
            ";" + (rb.IsSleeping() ? "sleep" : "awake") + (rb.isKinematic ? " kinematic" : "") + ";" + car.Gear);
    }

    private static void Shot(int index)
    {
        var camGo = GameObject.Find("CarCamera");
        var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
        if (cam == null) return;
        var rt = new RenderTexture(1280, 720, 24);
        var prev = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        File.WriteAllBytes(Path.Combine(OutDir, $"drive{index}.png"), tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = prev;
        Object.DestroyImmediate(tex);
        rt.Release();
    }

    /// <summary>A frame from a camera above and behind the player, looking past them at the nearest police car.</summary>
    private static void LookShot(int index, Vector3 player, Vector3 at)
    {
        var dir = at - player;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
        dir.Normalize();
        var go = new GameObject("TestShotCamera");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 60f;
        go.transform.position = player - dir * 3f + Vector3.up * 1.6f;
        go.transform.LookAt(Vector3.Lerp(player, at, 0.5f) + Vector3.up * 0.3f);
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        File.WriteAllBytes(Path.Combine(OutDir, $"police{index}.png"), tex.EncodeToPNG());
        RenderTexture.active = prev;
        cam.targetTexture = null;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(go);
    }

    /// <summary>A frame of a person standing at `feet`, from the side at their height.</summary>
    private static void PersonShot(Vector3 feet) =>
        ViewShot("person.png", feet + new Vector3(-1.2f, 0.35f, 0f), feet + Vector3.up * 0.25f);

    private static void ViewShot(string file, Vector3 from, Vector3 at)
    {
        var go = new GameObject("TestShotCamera");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.01f;
        go.transform.position = from;
        go.transform.LookAt(at);
        var rt = new RenderTexture(960, 720, 24);
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(960, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 960, 720), 0, 0);
        File.WriteAllBytes(Path.Combine(OutDir, file), tex.EncodeToPNG());
        RenderTexture.active = prev;
        cam.targetTexture = null;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(go);
    }

    private static void Finish(string result)
    {
        SimpleCar.ScriptedInput = null;
        SimpleCar.ScriptedPedals = null;
        File.WriteAllText(Path.Combine(OutDir, "result.txt"), result + "\n" + hits);
        hits.Clear();
        if (log != null) File.WriteAllText(Path.Combine(OutDir, "samples.csv"), log.ToString());
        EditorPrefs.SetBool(RunningKey, false);
        stage = 0;
        EditorApplication.isPlaying = false;
    }
}
