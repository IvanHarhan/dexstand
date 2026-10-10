using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// A car parked near the spawn. Walk up to it, press "В машину" (F on a
/// keyboard) and drive; the button then reads "Выйти". On a phone the normal
/// touch controls make way for gas, brake, left and right buttons while driving;
/// on a computer WASD drives.
///
/// Networked: a scene object whose physics only the host runs. Getting in and
/// out is a request in the input, the driver's steering rides in their ordinary
/// move input and the pedals, lever and switches beside it (CarPedals), and
/// everyone else sees the car through its NetworkTransform.
///
/// Each wheel is a spring-damper sphere cast and the body collider rides high enough to clear a kerb; what the
/// tyres do — engine, clutch, gearbox, grip, slides — is a real car's (CarDrivetrain, CarSpecs). On a computer it is
/// driven like one: key, three pedals, a lever; phones and tests get an automatic. Sizes and speeds here are in
/// scene units; the world is at a third of real size.
/// </summary>
public class SimpleCar : NetworkBehaviour
{
    public float maxSpeed = 23.1f;      // the model's top speed: one scene unit is 3 m, so km/h = units/s * 10.8

    [Header("Steering")]
    public float maxSteer = 30f;        // wheel angle at low speed, degrees

    // GTA-style: the wheel winds on gradually and comes back quicker, so a tap
    // on a phone button is a nudge rather than a jerk.
    public float steerSpeed = 1.6f;     // full lock in about 0.6 s
    public float steerReturn = 3.5f;    // back to straight in about 0.3 s

    [Header("Chassis")]
    public float carLength = 1.35f;     // 4.04 m, a TT
    public float suspensionTravel = 0.16f;
    public float bodyClearance = 0.25f; // underside of the body collider; the map's kerbs are 0.10 off the road

    public float enterDistance = 1.8f;

    /// <summary>Automated tests drive through this instead of the controls (x = steer, y = throttle), on the automatic.</summary>
    public static Vector2? ScriptedInput;

    /// <summary>Tests of the manual box: the pedals, lever and key instead of the driver's (with ScriptedInput for the wheel).</summary>
    public static CarPedals? ScriptedPedals;

    /// <summary>This device's pedals, lever and switches while it drives; NetworkLauncher sends them.</summary>
    public static CarPedals LocalPedals;

    /// <summary>This device is at the wheel: keys that mean something else on foot (Q, E) belong to the car.</summary>
    public static bool DrivingHere { get; private set; }

    private class Wheel
    {
        public Transform visual;
        public Quaternion restRotation;  // relative to the car
        public Vector3 center;           // at rest, car space
        public float radius;
        public bool front;
        public float compression;
        public bool grounded;
        public float spin;
        public Vector3 contact;          // world, where the tyre meets the road this tick
        public Vector3 mount;            // world, where the spring pushes on the body this tick
        public float load;               // the spring and damper's push, before the anti-roll bar
        public readonly CarDrivetrain.Wheel tyre = new CarDrivetrain.Wheel();
    }

    private readonly CarDrivetrain drivetrain = new CarDrivetrain();
    private CarDrivetrain.Wheel[] tyres = new CarDrivetrain.Wheel[0];
    private CarEngineSound engineSound;

    // host: what the driver sent
    private CarPedals hostPedals;
    private byte lastShifts;
    private bool lastKey;
    private PlayerRef lastDriver;
    private float reverseHold;

    // this device's own controls while it drives
    private float gasPedal, brakePedal, clutchPedal;
    private bool manual = true, leftOn, rightOn, hazardOn, lightsOn, assistsOff;
    private sbyte lever;
    private byte shifts;
    private bool cancelArmed;
    private float lookYaw, lookPitch, lookIdle;
    private int seenGrinds, seenStalls, seenKeys, seenNoClutch;
    private Text message, help;
    private static bool helpShown;
    private float messageUntil;
    private bool blinkWas;
    private AudioListener carEar, playerEar;

    // lamps on the body
    private GameObject lampLeft, lampRight, lampBrake, lampReverse, lampTail, lampHead;
    private Light cabinLight;

    // phone: the manual's clutch, handbrake, key and a sequential lever, and the switch between the boxes
    private GameObject manualPad, boxButton;
    private CarHoldButton clutchButton, handbrakeButton, keyButton;
    private Text boxLabel;
    private const int LampLeft = 1, LampRight = 2, LampHazard = 4, LampHead = 8, LampBrake = 16, LampReverse = 32,
                      LampRunning = 64, LampCranking = 128, LampHandbrake = 256, LampAssistsOff = 512,
                      LampAbs = 1024, LampEsc = 2048, LampManual = 4096;

    private readonly List<Wheel> wheels = new List<Wheel>();
    private static readonly RaycastHit[] WheelHits = new RaycastHit[8];
    private Rigidbody body;
    private Camera chaseCam;
    private InputRouter router;
    private bool seated;
    private Camera driverCam;
    private PlayerController nearest;
    private float nextLookup;
    private GameObject buttonGo;
    private Text buttonLabel;
    private GameObject pedals;
    private CarHoldButton gas, brake, left, right;
    private GameObject touchControls;
    private bool touchDriving;
    private float steering;
    private float flippedFor;
    private Vector3 camVelocity;

    [Header("Cockpit")]
    [Tooltip("How far the steering wheel turns at full lock, in degrees.")]
    public float steeringWheelTurn = 200f;

    private Transform steeringWheel, steeringAxis;
    private Quaternion steeringRest;
    private bool insideView;
    private Text viewLabel;
    private Bounds bodyBounds;  // the model, car space: where the seats are for the view from inside

    // any parked car of the map can be taken: this car moves to its spot and wears its model (RPC_Take)
    private string builtModel = "";
    private bool parkedInReach;
    private int parkedSpot;
    private Vector3 parkedPos;
    private Quaternion parkedRot;
    private string parkedModel;

    public bool IsDriven => Live && Driver != PlayerRef.None;

    // A remote copy's body is kinematic and knows no speed of its own.
    public float Speed => Live && !HasStateAuthority ? NetSpeed
                        : body != null ? Vector3.Dot(body.linearVelocity, transform.forward) : 0f;
    public int WheelsOnGround { get; private set; }
    /// <summary>The host's engine and box, for the test drives.</summary>
    public CarDrivetrain Drivetrain => drivetrain;
    /// <summary>-1 reverse, 0 neutral, 1.. forward.</summary>
    public int Gear => Live && !HasStateAuthority ? NetGear : drivetrain.Gear;

    private Text dash;


    /// <summary>
    /// A clear spot a few metres from the spawn, facing whichever way is open the
    /// longest — the map changes, so no fixed coordinates. Box casts at car size,
    /// lifted off the ground so the road itself is not an obstacle.
    /// </summary>
    private static void FindParking(out Vector3 position, out float yaw)
    {
        // A door's width of room on every side, so the car does not end up
        // wedged against a shelter or a fence.
        var half = new Vector3(0.75f, 0.18f, 1.1f);
        position = GroundAt(new Vector3(2.2f, 0f, 1.2f), null);
        yaw = 90f;
        float best = -1f;
        // Not right at the spawn: the bus stop stands there.
        foreach (float radius in new[] { 6f, 9f, 12f })
        {
            for (int a = 0; a < 360; a += 20)
            {
                var ground = GroundAt(Quaternion.Euler(0f, a, 0f) * Vector3.forward * radius, null);

                // Open sky only: a roof overhead — the bus stop — is no parking spot.
                if (Physics.Raycast(ground + Vector3.up * 0.1f, Vector3.up, 6f, ~0, QueryTriggerInteraction.Ignore))
                    continue;

                var centre = ground + Vector3.up * 0.45f;
                for (int y = 0; y < 360; y += 30)
                {
                    var rot = Quaternion.Euler(0f, y, 0f);
                    if (Physics.CheckBox(centre, half, rot, ~0, QueryTriggerInteraction.Ignore)) continue;
                    float free = Physics.BoxCast(centre, half, rot * Vector3.forward, out var hit, rot, 20f,
                                                 ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 20f;
                    if (free <= best) continue;
                    best = free;
                    position = ground;
                    yaw = y;
                }
            }
        }
    }

    /// <summary>The highest real surface under x, z — the safety net and the car itself do not count.</summary>
    private static Vector3 GroundAt(Vector3 p, Rigidbody ignore)
    {
        float best = float.MinValue;
        foreach (var hit in Physics.RaycastAll(new Vector3(p.x, 500f, p.z), Vector3.down, 1000f,
                                               ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.gameObject.name == "SafetyFloor") continue;
            if (ignore != null && hit.collider.attachedRigidbody == ignore) continue;
            best = Mathf.Max(best, hit.point.y);
        }
        return new Vector3(p.x, best == float.MinValue ? 2f : best, p.z);
    }

    // ------------------------------------------------------------------ build

    private void Awake()
    {
        var model = Resources.Load<GameObject>("Cars/audi-tt");
        if (model != null) { ApplySpec(model.name); builtModel = model.name; }
        Bounds bounds = model != null ? BuildFromModel(model) : BuildFromPrimitives();
        bodyBounds = bounds;

        // The body collider starts above the kerbs; the wheels are sphere casts, not colliders. It leaves the mirrors out.
        var box = gameObject.AddComponent<BoxCollider>();
        float top = bounds.max.y;
        box.center = new Vector3(bounds.center.x, (bodyClearance + top) * 0.5f, bounds.center.z);
        box.size = new Vector3(bounds.size.x * 0.84f, top - bodyClearance, bounds.size.z * 0.97f);
        box.material = new PhysicsMaterial("CarBody")
        {
            dynamicFriction = 0.05f, staticFriction = 0.05f, bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Minimum
        };

        body = gameObject.AddComponent<Rigidbody>();
        engineSound = GetComponent<CarEngineSound>();
        if (engineSound == null) engineSound = gameObject.AddComponent<CarEngineSound>();
        body.mass = drivetrain.spec.mass;
        body.linearDamping = 0f; // rolling and air drag are modelled per wheel
        body.angularDamping = 0.05f; // the tyres hold the yaw; more would brake a slide no real car brakes
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.useGravity = false; // its own, real one: see Gravity
        body.centerOfMass = new Vector3(0f, drivetrain.spec.comHeight / 3f, 0f);

        chaseCam = new GameObject("CarCamera").AddComponent<Camera>();
        chaseCam.depth = 50f;
        chaseCam.fieldOfView = 65f;
        chaseCam.nearClipPlane = 0.05f;
        chaseCam.farClipPlane = 3000f;
        chaseCam.enabled = false;

        // Rendered the way the player's own camera is, post-processing and all:
        // left at the defaults it drew the world and no HUD, and the pedals
        // could not be seen.
        chaseCam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        // the ears go where the eyes are while seated: the player's own ride two metres over the roof
        carEar = chaseCam.gameObject.AddComponent<AudioListener>();
        carEar.enabled = false;

        BuildLamps(bounds);
        BuildUi();
    }

    /// <summary>The Kenney sports sedan, turned to face +Z, scaled to <see cref="carLength"/>, wheels on y = 0.</summary>
    private Bounds BuildFromModel(GameObject prefab)
    {
        var model = Instantiate(prefab, transform);
        model.name = "Model";
        model.transform.localPosition = Vector3.zero;
        foreach (var c in model.GetComponentsInChildren<Collider>()) Destroy(c);

        // Without the wing it reads as an ordinary saloon rather than a race car.
        foreach (var t in model.GetComponentsInChildren<Transform>())
            if (t.name.StartsWith("spoiler")) t.gameObject.SetActive(false);

        var wheelTransforms = new List<Transform>();
        foreach (var t in model.GetComponentsInChildren<Transform>())
            if (t.name.StartsWith("wheel-")) wheelTransforms.Add(t);

        Vector3 Avg(bool front)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var w in wheelTransforms)
            {
                if (w.name.Contains("front") != front) continue;
                sum += transform.InverseTransformPoint(RendererBounds(w.gameObject).center);
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        var ahead = Vector3.ProjectOnPlane(Avg(true) - Avg(false), Vector3.up);
        if (ahead.sqrMagnitude > 1e-6f)
            model.transform.localRotation = Quaternion.FromToRotation(ahead.normalized, Vector3.forward) * model.transform.localRotation;

        var local = LocalBounds(model);
        model.transform.localScale *= carLength / Mathf.Max(local.size.z, 1e-3f);
        local = LocalBounds(model);
        model.transform.localPosition -= new Vector3(local.center.x, local.min.y, local.center.z);

        foreach (var w in wheelTransforms)
        {
            var wb = RendererBounds(w.gameObject);
            var center = transform.InverseTransformPoint(wb.center);
            wheels.Add(new Wheel
            {
                visual = w,
                restRotation = Quaternion.Inverse(transform.rotation) * w.rotation,
                center = center,
                radius = Mathf.Max(0.05f, center.y),
                front = center.z > 0f,
            });
        }
        if (wheels.Count != 4) Debug.LogWarning($"[SimpleCar] model has {wheels.Count} wheels");

        // The steering wheel turns about its own column, which leans; the model
        // carries a marker ahead of the hub to say which way the column runs.
        foreach (var t in model.GetComponentsInChildren<Transform>())
        {
            if (t.name == "steering-wheel") steeringWheel = t;
            if (t.name == "steering-axis") steeringAxis = t;
        }
        var bounds = LocalBounds(model);
        if (steeringWheel == null) AddCockpit(model.transform, bounds);
        if (steeringWheel != null) steeringRest = steeringWheel.localRotation;

        return bounds;
    }

    private static Mesh cockpitMesh;
    private static Material[] cockpitMaterials;

    /// <summary>
    /// The low-poly town cars have no cabin: they get the Audi's — its dashboard, seats and steering wheel, cut out of
    /// its body by material — widened or narrowed to the car and sat on its floor.
    /// </summary>
    private void AddCockpit(Transform model, Bounds car)
    {
        var prefab = Resources.Load<GameObject>("Cars/audi-tt");
        if (prefab == null) return;
        var audi = Instantiate(prefab);
        audi.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        foreach (var c in audi.GetComponentsInChildren<Collider>()) Destroy(c);

        // turned and sized as when the Audi is the car itself (BuildFromModel)
        Vector3 frontSum = Vector3.zero, backSum = Vector3.zero;
        var wheelParts = new List<GameObject>();
        MeshFilter shell = null;
        Transform wheel = null, axis = null;
        foreach (var t in audi.GetComponentsInChildren<Transform>())
        {
            if (t.name.StartsWith("wheel-"))
            {
                if (t.name.Contains("front")) frontSum += RendererBounds(t.gameObject).center;
                else backSum += RendererBounds(t.gameObject).center;
                wheelParts.Add(t.gameObject);
            }
            if (t.name == "body") shell = t.GetComponent<MeshFilter>();
            if (t.name == "steering-wheel") wheel = t;
            if (t.name == "steering-axis") axis = t;
        }
        var ahead = Vector3.ProjectOnPlane(frontSum - backSum, Vector3.up);
        if (ahead.sqrMagnitude > 1e-8f) audi.transform.rotation = Quaternion.FromToRotation(ahead.normalized, Vector3.forward);
        var whole = RendererBounds(audi);
        audi.transform.localScale *= CarSpecs.Get("audi-tt").length / 3f / Mathf.Max(whole.size.z, 1e-4f);
        whole = RendererBounds(audi);
        foreach (var part in wheelParts) DestroyImmediate(part);

        if (shell == null || wheel == null || (cockpitMesh == null && !shell.sharedMesh.isReadable))
        {
            Destroy(audi);
            return;
        }
        var shellRenderer = shell.GetComponent<MeshRenderer>();
        if (cockpitMesh == null)
        {
            var source = shell.sharedMesh;
            var mats = shellRenderer.sharedMaterials;
            var keep = new List<int>();
            for (int i = 0; i < mats.Length && i < source.subMeshCount; i++)
                if (mats[i] != null && (mats[i].name.StartsWith("internal") || mats[i].name.StartsWith("plastic_S"))) keep.Add(i);
            cockpitMesh = new Mesh { name = "Cockpit", indexFormat = source.indexFormat };
            cockpitMesh.SetVertices(source.vertices);
            cockpitMesh.SetNormals(source.normals);
            cockpitMesh.SetUVs(0, source.uv);
            cockpitMesh.subMeshCount = keep.Count;
            cockpitMaterials = new Material[keep.Count];
            for (int i = 0; i < keep.Count; i++)
            {
                cockpitMesh.SetTriangles(source.GetTriangles(keep[i]), i);
                cockpitMaterials[i] = mats[keep[i]];
            }
            cockpitMesh.RecalculateBounds();
        }
        shell.sharedMesh = cockpitMesh;
        shellRenderer.sharedMaterials = cockpitMaterials;

        // into this car: as wide as its cabin, on its floor, taller bodies lift it to their seat height
        float s = Mathf.Clamp(car.size.x / Mathf.Max(whole.size.x, 1e-4f), 0.85f, 1.25f);
        audi.transform.localScale *= s;
        float lift = Mathf.Max(0f, car.size.y - whole.size.y * s) * 0.5f;
        audi.transform.SetParent(transform, false);
        audi.transform.localPosition = new Vector3(car.center.x, car.min.y + lift, car.center.z)
                                       - new Vector3(whole.center.x, whole.min.y, whole.center.z) * s;
        audi.transform.SetParent(model, true);
        audi.name = "Cockpit";
        steeringWheel = wheel;
        steeringAxis = axis;
    }

    /// <summary>Swaps the body for another model of Resources/Cars; the wheels, cockpit and collider follow it.</summary>
    private void ApplyModel(string name)
    {
        builtModel = name;
        var prefab = Resources.Load<GameObject>("Cars/" + name);
        if (prefab == null) return;
        var old = transform.Find("Model");
        if (old != null)
        {
            old.name = "OldModel";  // Destroy waits for the frame's end
            old.gameObject.SetActive(false);
            Destroy(old.gameObject);
        }
        wheels.Clear();
        steeringWheel = steeringAxis = null;
        insideView = false;
        ApplySpec(name);
        var b = BuildFromModel(prefab);
        bodyBounds = b;
        if (wheels.Count == 0)
        {
            // one-piece models: the four rays go where wheels of a car that size would be
            float r = 0.105f;
            foreach (var x in new[] { -1f, 1f })
                foreach (var z in new[] { -1f, 1f })
                {
                    var c = new Vector3(b.center.x + x * b.extents.x * 0.78f, r, b.center.z + z * b.extents.z * 0.64f);
                    wheels.Add(new Wheel { visual = null, restRotation = Quaternion.identity, center = c, radius = r, front = z > 0f });
                }
        }
        BuildLamps(b);
        var box = GetComponent<BoxCollider>();
        if (box != null)
        {
            float top = b.max.y;
            box.center = new Vector3(b.center.x, (bodyClearance + top) * 0.5f, b.center.z);
            box.size = new Vector3(b.size.x * 0.84f, top - bodyClearance, b.size.z * 0.97f);
        }
    }

    /// <summary>The model's real size, weight, engine and gearbox (CarSpecs); lengths in scene units: a unit is 3 m.</summary>
    private void ApplySpec(string name)
    {
        var s = CarSpecs.Get(name);
        carLength = s.length / 3f;
        maxSpeed = s.topKmh / 10.8f;
        drivetrain.spec = s;
        drivetrain.Reset();    // another car: its engine is off, its lever in neutral
        if (body != null)
        {
            body.mass = s.mass;
            body.centerOfMass = new Vector3(0f, s.comHeight / 3f, 0f);
        }
    }

    /// <summary>A plain red box car, if the model is missing from Resources.</summary>
    private Bounds BuildFromPrimitives()
    {
        var paint = Paint(new Color(0.72f, 0.06f, 0.05f), 0.6f);
        Part(PrimitiveType.Cube, new Vector3(0f, 0.3f, 0f), new Vector3(0.62f, 0.2f, carLength), Vector3.zero, paint);
        Part(PrimitiveType.Cube, new Vector3(0f, 0.47f, -0.1f), new Vector3(0.56f, 0.16f, 0.75f), Vector3.zero,
             Paint(new Color(0.07f, 0.09f, 0.11f), 0.9f));
        var tyre = Paint(new Color(0.04f, 0.04f, 0.04f), 0.2f);
        foreach (var x in new[] { -0.28f, 0.28f })
        {
            foreach (var z in new[] { -0.48f, 0.5f })
            {
                var w = Part(PrimitiveType.Cylinder, new Vector3(x, 0.14f, z), new Vector3(0.28f, 0.05f, 0.28f),
                             new Vector3(0f, 0f, 90f), tyre);
                wheels.Add(new Wheel { visual = w, restRotation = w.localRotation, center = new Vector3(x, 0.14f, z), radius = 0.14f, front = z > 0f });
            }
        }
        return new Bounds(new Vector3(0f, 0.3f, 0f), new Vector3(0.62f, 0.6f, carLength));
    }

    private static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    /// <summary>Bounds of the active renderers under go, in this car's local space.</summary>
    private Bounds LocalBounds(GameObject go)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) * 2 - 1, (i >> 1 & 1) * 2 - 1, (i >> 2 & 1) * 2 - 1));
                var p = transform.InverseTransformPoint(corner);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    private static Material Paint(Color colour, float smoothness)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", colour);
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    private Transform Part(PrimitiveType type, Vector3 position, Vector3 scale, Vector3 euler, Material material)
    {
        var go = GameObject.CreatePrimitive(type);
        DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
        go.transform.localEulerAngles = euler;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go.transform;
    }

    private static Material Glow(Color colour)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        var m = new Material(shader);
        m.SetColor("_BaseColor", colour);
        return m;
    }

    /// <summary>
    /// Indicators, brake, reverse, tail and head lamps as small glowing dots at the body's corners (the models have
    /// no separate lamps), switched by <see cref="ShowLamps"/>. Head lamps light the road.
    /// </summary>
    private void BuildLamps(Bounds b)
    {
        var old = transform.Find("Lamps");
        if (old != null) Destroy(old.gameObject);
        var root = new GameObject("Lamps").transform;
        root.SetParent(transform, false);

        float y = b.min.y + b.size.y * 0.42f, x = b.extents.x * 0.74f;
        float front = b.max.z + 0.004f, back = b.min.z - 0.004f;
        GameObject Dot(string name, Vector3 at, Color colour, float size, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = new Vector3(size, size * 0.7f, size * 0.4f);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = Glow(colour);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }
        GameObject Group(string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(root, false);
            g.SetActive(false);
            return g;
        }

        var amber = new Color(1f, 0.55f, 0.05f);
        lampLeft = Group("IndicatorLeft");
        lampRight = Group("IndicatorRight");
        Dot("FL", new Vector3(b.center.x - x - 0.01f, y, front - 0.01f), amber, 0.035f, lampLeft.transform);
        Dot("RL", new Vector3(b.center.x - x - 0.01f, y, back + 0.01f), amber, 0.035f, lampLeft.transform);
        Dot("FR", new Vector3(b.center.x + x + 0.01f, y, front - 0.01f), amber, 0.035f, lampRight.transform);
        Dot("RR", new Vector3(b.center.x + x + 0.01f, y, back + 0.01f), amber, 0.035f, lampRight.transform);

        lampBrake = Group("Brake");
        Dot("L", new Vector3(b.center.x - x * 0.85f, y + 0.02f, back), new Color(1f, 0.02f, 0.02f), 0.05f, lampBrake.transform);
        Dot("R", new Vector3(b.center.x + x * 0.85f, y + 0.02f, back), new Color(1f, 0.02f, 0.02f), 0.05f, lampBrake.transform);
        lampTail = Group("Tail");
        Dot("L", new Vector3(b.center.x - x * 0.85f, y + 0.02f, back), new Color(0.45f, 0f, 0f), 0.04f, lampTail.transform);
        Dot("R", new Vector3(b.center.x + x * 0.85f, y + 0.02f, back), new Color(0.45f, 0f, 0f), 0.04f, lampTail.transform);
        lampReverse = Group("Reverse");
        Dot("C", new Vector3(b.center.x - x * 0.5f, y, back), Color.white, 0.035f, lampReverse.transform);

        // the cabin, lit for whoever looks from inside (this screen only): a warm dome light over the front seats
        cabinLight = new GameObject("CabinLight").AddComponent<Light>();
        cabinLight.transform.SetParent(root, false);
        cabinLight.transform.localPosition = new Vector3(b.center.x, b.min.y + b.size.y * 0.75f, b.center.z + b.extents.z * 0.1f);
        cabinLight.type = LightType.Point;
        cabinLight.range = b.size.z * 0.5f;
        cabinLight.intensity = 2.5f;
        cabinLight.color = new Color(1f, 0.9f, 0.75f);
        cabinLight.shadows = LightShadows.None;
        cabinLight.enabled = false;

        lampHead = Group("Head");
        foreach (float side in new[] { -1f, 1f })
        {
            var at = new Vector3(b.center.x + side * x * 0.85f, y + 0.02f, front);
            Dot(side < 0 ? "L" : "R", at, new Color(1f, 0.97f, 0.85f), 0.05f, lampHead.transform);
            var beam = new GameObject("Beam").AddComponent<Light>();
            beam.transform.SetParent(lampHead.transform, false);
            beam.transform.localPosition = at + Vector3.forward * 0.03f;
            beam.transform.localRotation = Quaternion.Euler(6f, 0f, 0f);
            beam.type = LightType.Spot;
            beam.range = 30f;
            beam.spotAngle = 70f;
            beam.innerSpotAngle = 35f;
            beam.intensity = 6f;
            beam.color = new Color(1f, 0.96f, 0.86f);
            beam.shadows = LightShadows.None;
        }
    }

    /// <summary>What the lamps show, on every screen, from what the host says (NetLamps).</summary>
    private void ShowLamps()
    {
        if (lampHead == null || !Live) return;
        int l = NetLamps;
        bool blink = Time.time % 0.75f < 0.4f;   // about 80 flashes a minute, as the law asks
        bool hazard = (l & LampHazard) != 0;
        bool left = blink && (hazard || (l & LampLeft) != 0);
        bool right = blink && (hazard || (l & LampRight) != 0);
        if (lampLeft.activeSelf != left) lampLeft.SetActive(left);
        if (lampRight.activeSelf != right) lampRight.SetActive(right);
        bool brakeLit = (l & LampBrake) != 0, reverse = (l & LampReverse) != 0, head = (l & LampHead) != 0;
        if (lampBrake.activeSelf != brakeLit) lampBrake.SetActive(brakeLit);
        if (lampReverse.activeSelf != reverse) lampReverse.SetActive(reverse);
        if (lampHead.activeSelf != head) lampHead.SetActive(head);
        if (lampTail.activeSelf != head) lampTail.SetActive(head);

        // the relay ticks on and tocks off, heard in and near the car
        bool any = hazard || (l & (LampLeft | LampRight)) != 0;
        if (any && blink != blinkWas) engineSound.Play(blink ? CarEngineSound.Cue.Tick : CarEngineSound.Cue.Tock);
        blinkWas = blink;
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        var canvasGo = new GameObject("CarUI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

        buttonGo = Panel(canvasGo.transform, "CarButton", new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(400f, 110f));
        buttonGo.AddComponent<Button>().onClick.AddListener(TryToggle);
        buttonLabel = Label(buttonGo.transform, "", 44);
        buttonGo.SetActive(false);

        // Driving buttons for phones: steering bottom-left, pedals bottom-right.
        pedals = new GameObject("CarPedals", typeof(RectTransform));
        pedals.transform.SetParent(canvasGo.transform, false);
        var prt = (RectTransform)pedals.transform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = prt.offsetMax = Vector2.zero;
        left = Hold(pedals.transform, "Left", "<", new Vector2(0f, 0f), new Vector2(170f, 150f));
        right = Hold(pedals.transform, "Right", ">", new Vector2(0f, 0f), new Vector2(400f, 150f));
        brake = Hold(pedals.transform, "Brake", "ТОРМОЗ", new Vector2(1f, 0f), new Vector2(-400f, 130f));
        gas = Hold(pedals.transform, "Gas", "ГАЗ", new Vector2(1f, 0f), new Vector2(-170f, 180f), new Vector2(210f, 260f));
        // Beside the exit button, and only there while driving: behind the car or behind the wheel.
        var view = Panel(pedals.transform, "View", new Vector2(0.5f, 1f), new Vector2(340f, -40f), new Vector2(240f, 110f));
        view.AddComponent<Button>().onClick.AddListener(ToggleView);
        viewLabel = Label(view.transform, "Салон (C)", 40);

        // Phone only: the automatic by default; the switch on the other side of the exit button gives the manual
        // its clutch, handbrake, key and a sequential lever (+ / −) above the pedals.
        boxButton = Panel(pedals.transform, "Box", new Vector2(0.5f, 1f), new Vector2(-340f, -40f), new Vector2(240f, 110f));
        boxButton.AddComponent<Button>().onClick.AddListener(() => manual = !manual);
        boxLabel = Label(boxButton.transform, "АКПП", 40);
        manualPad = new GameObject("ManualPad", typeof(RectTransform));
        manualPad.transform.SetParent(pedals.transform, false);
        var mrt = (RectTransform)manualPad.transform;
        mrt.anchorMin = Vector2.zero;
        mrt.anchorMax = Vector2.one;
        mrt.offsetMin = mrt.offsetMax = Vector2.zero;
        clutchButton = Hold(manualPad.transform, "Clutch", "СЦЕП", new Vector2(1f, 0f), new Vector2(-620f, 130f));
        handbrakeButton = Hold(manualPad.transform, "Handbrake", "РУЧН", new Vector2(1f, 0f), new Vector2(-620f, 330f), new Vector2(200f, 140f));
        keyButton = Hold(manualPad.transform, "Key", "КЛЮЧ", new Vector2(1f, 0f), new Vector2(-170f, 560f), new Vector2(200f, 140f));
        var up = Panel(manualPad.transform, "GearUp", new Vector2(1f, 0f), new Vector2(-170f, 400f), new Vector2(200f, 140f));
        up.AddComponent<Button>().onClick.AddListener(() => StepLever(1));
        Label(up.transform, "ПЕР +", 44);
        var down = Panel(manualPad.transform, "GearDown", new Vector2(1f, 0f), new Vector2(-400f, 330f), new Vector2(200f, 140f));
        down.AddComponent<Button>().onClick.AddListener(() => StepLever(-1));
        Label(down.transform, "ПЕР −", 44);
        boxButton.SetActive(false);
        manualPad.SetActive(false);

        pedals.SetActive(false);

        var dashGo = Panel(canvasGo.transform, "Dash", new Vector2(0.5f, 0f), new Vector2(0f, 95f), new Vector2(820f, 150f));
        dash = Label(dashGo.transform, "", 40);
        dash.supportRichText = true;
        dashGo.SetActive(false);

        var messageGo = Panel(canvasGo.transform, "CarMessage", new Vector2(0.5f, 0f), new Vector2(0f, 220f), new Vector2(1000f, 70f));
        message = Label(messageGo.transform, "", 36);
        message.color = new Color(1f, 0.85f, 0.3f);
        messageGo.SetActive(false);

        var helpGo = Panel(canvasGo.transform, "CarHelp", new Vector2(0f, 0.5f), new Vector2(250f, 0f), new Vector2(460f, 560f));
        help = Label(helpGo.transform, "", 26);
        help.alignment = TextAnchor.MiddleLeft;
        help.text = "  K — зажигание (держать)\n  W — газ   S — тормоз\n  Shift — сцепление\n  A / D — руль\n" +
                    "  Пробел — ручник\n  1–7 — передачи\n  0 — нейтраль\n  R — задняя\n" +
                    "  Q / E — поворотники\n  X — аварийка   L — фары\n  G — механика / автомат\n" +
                    "  F3 — ABS и ESP вкл/выкл\n  Мышь — осмотреться\n  Средняя кнопка — взгляд прямо\n  C — салон / сзади\n  H — скрыть подсказку";
        helpGo.SetActive(false);

        if (FindFirstObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }
    }

    private static GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, anchor.y > 0.5f ? 1f : 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        go.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
        return go;
    }

    private static Text Label(Transform parent, string text, int size)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.text = text;
        t.raycastTarget = false;
        return t;
    }

    private static CarHoldButton Hold(Transform parent, string name, string text, Vector2 anchor, Vector2 position,
                                      Vector2? size = null)
    {
        var go = Panel(parent, name, anchor, position, size ?? new Vector2(200f, 200f));
        Label(go.transform, text, 48);
        return go.AddComponent<CarHoldButton>();
    }

    // ------------------------------------------------------------------ enter / exit

    private static PlayerController LocalPlayer()
    {
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            if (pc.Object != null && pc.Object.IsValid && pc.Object.HasInputAuthority) return pc;
        }
        return null;
    }

    private bool Live => Object != null && Object.IsValid;

    private bool InReach => nearest != null && Live && !IsInside(Runner.LocalPlayer) &&
                            (Driver == PlayerRef.None || FreeSeat() >= 0) &&
                            Vector3.Distance(nearest.transform.position, transform.position) < enterDistance;

    /// <summary>The car on this device's screen is the one this player is driving.</summary>
    private bool DrivenFromHere => Live && Driver != PlayerRef.None && Driver == Runner.LocalPlayer;

    /// <summary>This device's player rides along in another's car.</summary>
    private bool RiddenFromHere => Live && Driver != Runner.LocalPlayer && IsInside(Runner.LocalPlayer);

    /// <summary>At the wheel or on one of the other seats.</summary>
    public bool IsInside(PlayerRef p)
    {
        if (!Live || p == PlayerRef.None) return false;
        if (Driver == p) return true;
        for (int i = 0; i < Passengers.Length; i++)
            if (Passengers[i] == p) return true;
        return false;
    }

    private int FreeSeat()
    {
        for (int i = 0; i < Passengers.Length; i++)
            if (Passengers[i] == PlayerRef.None) return i;
        return -1;
    }

    /// <summary>Nobody in it: only then may it be summoned or swapped for a parked car.</summary>
    private bool Empty
    {
        get
        {
            if (Driver != PlayerRef.None) return false;
            for (int i = 0; i < Passengers.Length; i++)
                if (Passengers[i] != PlayerRef.None) return false;
            return true;
        }
    }

    private void Update()
    {
        if (router == null) router = FindFirstObjectByType<InputRouter>();
        if (Time.time >= nextLookup)
        {
            nearest = LocalPlayer();
            nextLookup = Time.time + 0.5f;
        }

        bool here = DrivenFromHere;
        bool inside = here || RiddenFromHere;
        if (inside != seated)
        {
            if (inside) Seat(here);
            else LeaveSeat();
        }

        // Pedals travel like the stick does: as this device's move input.
        if (router != null) router.Drive = here ? Pedals() : Vector2.zero;
        DrivingHere = here;
        LocalPedals = here ? ReadDriver() : default;
        if (inside) Look();
        if (Live)
        {
            if (NetKeys != seenKeys) { seenKeys = NetKeys; engineSound.Play(CarEngineSound.Cue.Key); }
            if (NetGrinds != seenGrinds)
            {
                seenGrinds = NetGrinds;
                engineSound.Play(CarEngineSound.Cue.Grind);
                if (here) Say("Скрежет! Передачу — только с выжатым сцеплением (Shift)");
            }
            if (NetStalls != seenStalls)
            {
                seenStalls = NetStalls;
                if (here) Say("Заглох. Выжми сцепление и заведи (K)");
            }
            if (NetNoClutch != seenNoClutch)
            {
                seenNoClutch = NetNoClutch;
                if (here) Say("Чтобы завести: выжми сцепление (Shift) или поставь нейтраль (0)");
            }
            int l = NetLamps;
            engineSound.Drive(NetRpm, NetLoad, (l & LampRunning) != 0, (l & LampCranking) != 0);
            ShowLamps();
        }
        bool cabin = inside && insideView;
        if (cabinLight != null && cabinLight.enabled != cabin) cabinLight.enabled = cabin;
        bool pad = here && touchDriving;
        if (boxButton.activeSelf != pad) boxButton.SetActive(pad);
        if (manualPad.activeSelf != (pad && manual)) manualPad.SetActive(pad && manual);
        boxLabel.text = manual ? "МКПП" : "АКПП";

        var messageGo = message.transform.parent.gameObject;
        bool showMessage = inside && Time.time < messageUntil;
        if (messageGo.activeSelf != showMessage) messageGo.SetActive(showMessage);

        // every peer wears the model the host picked, and hides the parked car it stands in for
        if (Live)
        {
            string want = ModelName.ToString();
            if (want.Length > 0 && want != builtModel) ApplyModel(want);
            if (MapParkedCars.Instance != null) MapParkedCars.Instance.SetTaken(TakenSpot - 1);
        }
        parkedInReach = !inside && !InReach && Live && Empty && nearest != null &&
                        MapParkedCars.Instance != null &&
                        MapParkedCars.Instance.Nearest(nearest.transform.position, enterDistance * 1.3f,
                                                       out parkedSpot, out parkedPos, out parkedRot, out parkedModel);

        bool show = inside || InReach || parkedInReach;
        if (buttonGo.activeSelf != show) buttonGo.SetActive(show);
        buttonLabel.text = inside ? "Выйти (F)"
                         : InReach && Driver != PlayerRef.None ? "Пассажиром (F)" : "В машину (F)";

        var dashGo = dash.transform.parent.gameObject;
        if (dashGo.activeSelf != inside) dashGo.SetActive(inside);
        if (inside) dash.text = Dashboard();

        var kb = Keyboard.current;
        if (show && kb != null && kb.fKey.wasPressedThisFrame) TryToggle();
        if (inside && kb != null && kb.cKey.wasPressedThisFrame) ToggleView();
    }

    private void Say(string text)
    {
        message.text = text;
        messageUntil = Time.time + 4f;
    }

    private static readonly System.Text.StringBuilder DashText = new System.Text.StringBuilder();

    /// <summary>Speed, gear, rev counter and the warning lights, from what the host says.</summary>
    private string Dashboard()
    {
        int l = NetLamps, g = NetGear;
        bool blink = Time.time % 0.75f < 0.4f;
        string gear = g < 0 ? "R" : g == 0 ? "N" : ((l & LampManual) != 0 ? "" : "D") + g;
        float red = drivetrain.spec.redline;
        var b = DashText;
        b.Clear();
        b.Append("<size=60><b>").Append(Mathf.RoundToInt(Mathf.Abs(Speed) * 10.8f)).Append("</b></size> км/ч     <size=60><b>")
         .Append(gear).Append("</b></size>     ").Append(Mathf.RoundToInt(NetRpm / 50f) * 50).Append(" об/мин\n");

        // the rev counter: a bar to the redline and a little past it
        const int Bars = 28;
        int lit = Mathf.Clamp(Mathf.RoundToInt(NetRpm / red * 24f), 0, Bars);
        for (int i = 0; i < Bars; i++)
        {
            string colour = i >= lit ? "#444444" : i >= 21 ? "#ff3030" : i >= 17 ? "#ffb020" : "#ffffff";
            b.Append("<color=").Append(colour).Append(">|</color>");
        }
        b.Append('\n');

        string Lamp(bool on, string text, string colour) => $"<color={(on ? colour : "#3a3a3a")}>{text}</color>  ";
        bool hazard = (l & LampHazard) != 0;
        b.Append(Lamp(blink && (hazard || (l & LampLeft) != 0), "<<", "#30ff40"));
        b.Append(Lamp((l & LampHandbrake) != 0, "(P)", "#ff3030"));
        b.Append(Lamp((l & LampAssistsOff) != 0 || (blink && (l & LampAbs) != 0), "ABS", "#ffb020"));
        b.Append(Lamp((l & LampAssistsOff) != 0 || (blink && (l & LampEsc) != 0), "ESP", "#ffb020"));
        b.Append(Lamp((l & LampHead) != 0, "ФАРЫ", "#40a0ff"));
        b.Append(Lamp(hazard && blink, "/!\\", "#ff3030"));
        b.Append(Lamp((l & LampRunning) == 0, (l & LampCranking) != 0 ? "СТАРТЕР" : "ДВИГ.", "#ff3030"));
        b.Append(Lamp(blink && (hazard || (l & LampRight) != 0), ">>", "#30ff40"));
        return b.ToString();
    }

    /// <summary>
    /// The keyboard as a car's controls. Pedals travel rather than snap — a key cannot be eased like a foot, so the
    /// clutch comes up slowly through the bite by itself. The lever, indicators and switches stay as set.
    /// </summary>
    private CarPedals ReadDriver()
    {
        float dt = Time.deltaTime;
        var kb = Keyboard.current;
        bool keys = kb != null && !touchDriving && !MapMinimap.FullMapOpen;
        bool touch = touchDriving && manual;
        {
            bool w = (keys && kb.wKey.isPressed) || (touch && gas.Held);
            bool s = (keys && kb.sKey.isPressed) || (touch && brake.Held);
            gasPedal = Mathf.MoveTowards(gasPedal, w ? 1f : 0f, dt * (w ? 2.5f : 6f));
            brakePedal = Mathf.MoveTowards(brakePedal, s ? 1f : 0f, dt * (s ? 4f : 8f));
            bool down = (keys && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)) || (touch && clutchButton.Held);
            float rate = down ? 6f : clutchPedal > 0.33f && clutchPedal < 0.67f ? 0.45f : 3f;
            clutchPedal = Mathf.MoveTowards(clutchPedal, down ? 1f : 0f, dt * rate);
        }
        if (keys)
        {
            // the top row for laptops, the number pad as well: 1–9 gears, 0 neutral, R (or Num .) reverse
            for (int i = 0; i < 9; i++)
                if (kb[Key.Numpad1 + i].wasPressedThisFrame || kb[Key.Digit1 + i].wasPressedThisFrame) MoveLever((sbyte)(i + 1));
            if (kb.numpad0Key.wasPressedThisFrame || kb.digit0Key.wasPressedThisFrame) MoveLever(0);
            if (kb.numpadPeriodKey.wasPressedThisFrame || kb.rKey.wasPressedThisFrame) MoveLever(-1);

            if (kb.qKey.wasPressedThisFrame) { leftOn = !leftOn; rightOn = false; cancelArmed = false; }
            if (kb.eKey.wasPressedThisFrame) { rightOn = !rightOn; leftOn = false; cancelArmed = false; }
            if (kb.xKey.wasPressedThisFrame) hazardOn = !hazardOn;
            if (kb.lKey.wasPressedThisFrame) lightsOn = !lightsOn;
            if (kb.gKey.wasPressedThisFrame)
            {
                manual = !manual;
                Say(manual ? "Механика: сцепление — Shift, передачи — 1–7, нейтраль 0, задняя R"
                           : "Автомат: W — газ, S — тормоз, стоя на S — назад");
            }
            if (kb.f3Key.wasPressedThisFrame)
            {
                assistsOff = !assistsOff;
                Say(assistsOff ? "ABS и ESP выключены — колёса блокируются, машину может развернуть" : "ABS и ESP включены");
            }
            if (kb.hKey.wasPressedThisFrame) help.transform.parent.gameObject.SetActive(!help.transform.parent.gameObject.activeSelf);
        }

        // the indicator switches itself off once the wheel comes back from the turn
        if (leftOn || rightOn)
        {
            float into = leftOn ? -NetSteer : NetSteer;
            if (into > 0.35f) cancelArmed = true;
            else if (cancelArmed && into < 0.08f) leftOn = rightOn = cancelArmed = false;
        }

        var sw = (CarSwitch)0;
        if (manual) sw |= CarSwitch.Manual;
        if ((keys && kb.spaceKey.isPressed) || (touch && handbrakeButton.Held)) sw |= CarSwitch.Handbrake;
        if ((keys && kb.kKey.isPressed) || (touch && keyButton.Held)) sw |= CarSwitch.Key;
        if (leftOn) sw |= CarSwitch.Left;
        if (rightOn) sw |= CarSwitch.Right;
        if (hazardOn) sw |= CarSwitch.Hazard;
        if (lightsOn) sw |= CarSwitch.Lights;
        if (assistsOff) sw |= CarSwitch.NoAssists;
        return new CarPedals
        {
            gas = (byte)(gasPedal * 255f), brake = (byte)(brakePedal * 255f), clutch = (byte)(clutchPedal * 255f),
            switches = (byte)sw, lever = (byte)(lever + 1), shifts = shifts,
        };
    }

    private void MoveLever(sbyte gear)
    {
        lever = gear;
        shifts++;
    }

    /// <summary>The phone's sequential lever: one gear up or down from the one engaged (R, N, 1, 2 …).</summary>
    private void StepLever(int step) =>
        MoveLever((sbyte)Mathf.Clamp(NetGear + step, -1, drivetrain.spec.gears.Length));

    /// <summary>The mouse turns the head: inside freely (the middle button looks ahead again), behind the car it swings round and drifts back.</summary>
    private void Look()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        var delta = Cursor.lockState == CursorLockMode.Locked ? mouse.delta.ReadValue() * 0.15f : Vector2.zero;
        if (delta.sqrMagnitude > 0.0001f)
        {
            lookYaw = Mathf.Clamp(lookYaw + delta.x, -170f, 170f);
            lookPitch = Mathf.Clamp(lookPitch - delta.y, -55f, 35f);
            lookIdle = 0f;
        }
        else lookIdle += Time.deltaTime;
        if (mouse.middleButton.wasPressedThisFrame) lookYaw = lookPitch = 0f;
        if (!insideView && lookIdle > 1.5f && Mathf.Abs(Speed) > 1f)
        {
            lookYaw = Mathf.MoveTowards(lookYaw, 0f, 120f * Time.deltaTime);
            lookPitch = Mathf.MoveTowards(lookPitch, 0f, 60f * Time.deltaTime);
        }
    }

    /// <summary>Behind the car, or in the driver's seat — this screen's choice alone.</summary>
    public void ToggleView()
    {
        insideView = !insideView;
        chaseCam.nearClipPlane = insideView ? 0.01f : 0.05f;
        chaseCam.fieldOfView = insideView ? 72f : 65f;
        viewLabel.text = insideView ? "Сзади (C)" : "Салон (C)";
    }

    /// <summary>
    /// Asks to get in or out. It goes out as network input; the host decides
    /// (<see cref="Toggle"/>), and this screen follows <see cref="Driver"/>.
    /// </summary>
    public void TryToggle()
    {
        if (parkedInReach && Live)
        {
            RPC_Take(parkedPos, parkedRot, parkedModel, parkedSpot, Runner.LocalPlayer);
            return;
        }
        if (router == null) router = FindFirstObjectByType<InputRouter>();
        if (router != null) router.PickCar();
    }

    /// <summary>
    /// Host: the empty car takes the place of a parked one — its spot, its model — with the asking player at the
    /// wheel. Everyone then builds that model and hides the parked copy (see Update).
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Take(Vector3 pos, Quaternion rot, NetworkString<_32> model, int spot, PlayerRef who)
    {
        if (!Empty) return;
        if (MapParkedCars.Instance != null) MapParkedCars.Instance.SetTaken(spot);
        var pose = GroundAt(pos, body) + Vector3.up * 0.25f;
        transform.SetPositionAndRotation(pose, rot);
        body.position = pose;
        body.rotation = rot;
        body.linearVelocity = body.angularVelocity = Vector3.zero;
        var net = GetComponent<NetworkTransform>();
        if (net != null && net.enabled) net.Teleport(pose, rot);
        ModelName = model;
        TakenSpot = spot + 1;
        Driver = who;
    }

    /// <summary>
    /// Host only: lets whoever asked out, or seats them — at the wheel if it is free, else on a passenger seat.
    /// </summary>
    public void Toggle(PlayerRef player, Vector3 from)
    {
        if (!HasStateAuthority) return;

        if (Driver == player) { Driver = PlayerRef.None; return; }
        for (int i = 0; i < Passengers.Length; i++)
            if (Passengers[i] == player) { Passengers.Set(i, PlayerRef.None); return; }
        if (Vector3.Distance(from, transform.position) >= enterDistance) return;
        if (Driver == PlayerRef.None) Driver = player;
        else
        {
            int seat = FreeSeat();
            if (seat >= 0) Passengers.Set(seat, player);
        }
    }

    /// <summary>
    /// Brings the empty car to a player: parked a couple of metres to their right,
    /// on the ground, facing where they look. Any peer may ask; the host moves it.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_Summon(Vector3 near, Vector3 forward)
    {
        if (!Empty) return;
        var flat = Vector3.ProjectOnPlane(forward, Vector3.up);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        // 2 m (two thirds of a unit) of room between the player and the car's side
        var spot = near + Vector3.Cross(Vector3.up, flat) * 1.0f;
        var pose = GroundAt(spot, body) + Vector3.up * 0.25f;
        var rotation = Quaternion.LookRotation(flat, Vector3.up);
        transform.SetPositionAndRotation(pose, rotation);
        body.position = pose;
        body.rotation = rotation;
        body.linearVelocity = body.angularVelocity = Vector3.zero;
        var net = GetComponent<NetworkTransform>();
        if (net != null && net.enabled) net.Teleport(pose, rotation);
    }

    /// <summary>Where a driver gets out: beside the door, on the ground.</summary>
    public Vector3 ExitPoint => GroundAt(transform.position - transform.right * 0.8f, body) + Vector3.up * 0.1f;

    /// <param name="driving">at the wheel: pedals; a passenger only gets the camera</param>
    private void Seat(bool driving)
    {
        seated = true;
        var local = LocalPlayer();
        driverCam = local != null ? local.GetComponentInChildren<Camera>() : null;
        if (driverCam != null) driverCam.enabled = false;

        // Phone: swap the stick and fire buttons for pedals while driving.
        var mobile = FindFirstObjectByType<MobileInputService>();
        touchControls = mobile != null ? mobile.gameObject : null;
        touchDriving = touchControls != null && touchControls.activeInHierarchy;
        if (touchDriving) touchControls.SetActive(false);

        // Shown in every mode: on a PC they take a mouse click as well as the keys.
        pedals.SetActive(driving);

        playerEar = null;
        foreach (var ear in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            if (ear != carEar && ear.isActiveAndEnabled) playerEar = ear;
        if (playerEar != null) playerEar.enabled = false;
        carEar.enabled = true;

        // a fresh seat: feet off the pedals, head straight, switches off; a keyboard drives a manual
        gasPedal = brakePedal = clutchPedal = 0f;
        leftOn = rightOn = hazardOn = cancelArmed = false;
        lookYaw = lookPitch = 0f;
        manual = !touchDriving;
        if (driving && manual && !helpShown)
        {
            helpShown = true;
            help.transform.parent.gameObject.SetActive(true);
            Say("Механика: выжми сцепление (Shift) и заведи (K). H — подсказка по кнопкам");
        }

        var flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        chaseCam.transform.position = transform.position - flat * 2.6f + Vector3.up * 1.1f;
        chaseCam.transform.LookAt(transform.position + Vector3.up * 0.35f);
        chaseCam.enabled = true;
    }

    private void LeaveSeat()
    {
        seated = false;
        chaseCam.enabled = false;
        if (driverCam != null) driverCam.enabled = true;
        carEar.enabled = false;
        if (playerEar != null) playerEar.enabled = true;
        help.transform.parent.gameObject.SetActive(false);
        pedals.SetActive(false);
        if (touchDriving && touchControls != null) touchControls.SetActive(true);
        touchDriving = false;
    }

    private Vector2 Pedals()
    {
        float throttle = gas.Held ? 1f : brake.Held ? -1f : 0f;
        float steer = (right.Held ? 1f : 0f) - (left.Held ? 1f : 0f);
        // the keys as they are: the walking stick is normalised, which took a third off the lock with gas held
        var kb = Keyboard.current;
        if (!touchDriving && kb != null && throttle == 0f && steer == 0f && !MapMinimap.FullMapOpen)
        {
            steer = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            throttle = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        }
        return new Vector2(steer, throttle);
    }

    // ------------------------------------------------------------------ network

    [Networked] public PlayerRef Driver { get; set; }
    /// <summary>Up to three riders besides the driver (PlayerRef.None where a seat is free).</summary>
    [Networked, Capacity(3)] public NetworkArray<PlayerRef> Passengers => default;
    [Networked] private Vector2 Control { get; set; }
    [Networked] private float NetSpeed { get; set; }
    [Networked] private float NetSteer { get; set; }
    [Networked] private int NetGear { get; set; }
    [Networked] private float NetRpm { get; set; }
    [Networked] private float NetLoad { get; set; }
    /// <summary>Lamps and dashboard lights (the Lamp* bits).</summary>
    [Networked] private int NetLamps { get; set; }
    // counted up each time, so every screen hears it and the driver's says it
    [Networked] private int NetGrinds { get; set; }
    [Networked] private int NetStalls { get; set; }
    [Networked] private int NetKeys { get; set; }
    [Networked] private int NetNoClutch { get; set; }
    /// <summary>The Resources/Cars model the car wears ("" = the one it was built with).</summary>
    [Networked] public NetworkString<_32> ModelName { get; set; }
    /// <summary>The parked-car spot it has taken the place of, plus one (0 = none).</summary>
    [Networked] public int TakenSpot { get; set; }

    /// <summary>The one car, for the players' simulation to ask about.</summary>
    public static SimpleCar Current { get; private set; }

    public override void Spawned()
    {
        Current = this;
        // what happened before this screen joined is not news
        seenGrinds = NetGrinds;
        seenStalls = NetStalls;
        seenKeys = NetKeys;
        seenNoClutch = NetNoClutch;

        // The host drives the physics; everyone else is shown where it is.
        body.isKinematic = !HasStateAuthority;
        if (!HasStateAuthority) body.interpolation = RigidbodyInterpolation.None;
        if (!HasStateAuthority) return;

        var box = GetComponent<BoxCollider>();
        box.enabled = false;
        FindParking(out var position, out float yaw);
        box.enabled = true;

        var pose = position + Vector3.up * 0.25f;
        var rotation = Quaternion.Euler(0f, yaw, 0f);
        transform.SetPositionAndRotation(pose, rotation);
        body.position = pose;
        body.rotation = rotation;
        var net = GetComponent<NetworkTransform>();
        if (net != null) net.Teleport(pose, rotation);
        Debug.Log($"[Car] Parked at {position}, facing {yaw:F0}°.");

        // Alone there is nobody to tell where the car is, and the transform sync
        // setting the pose every frame on top of the rigidbody's own smoothing
        // made the car judder. Solo, the physics alone moves it.
        if (net != null && Runner.GameMode == GameMode.Single) net.enabled = false;
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Current == this) Current = null;
        if (seated) LeaveSeat();
        if (router != null) router.Drive = Vector2.zero;
        DrivingHere = false;
        LocalPedals = default;
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        // A driver who left the match leaves the car.
        bool present = false;
        foreach (var p in Runner.ActivePlayers)
        {
            if (p == Driver) present = true;
        }
        if (!present) Driver = PlayerRef.None;
        // and a rider who left frees their seat
        for (int i = 0; i < Passengers.Length; i++)
        {
            if (Passengers[i] == PlayerRef.None) continue;
            bool here = false;
            foreach (var p in Runner.ActivePlayers) if (p == Passengers[i]) here = true;
            if (!here) Passengers.Set(i, PlayerRef.None);
        }

        NetworkInputData data = default;
        bool got = Driver != PlayerRef.None && Runner.TryGetInputForPlayer(Driver, out data);
        Control = got ? data.moveInput : Vector2.zero;
        hostPedals = got ? data.pedals : default;

        NetSpeed = Vector3.Dot(body.linearVelocity, transform.forward);
        NetSteer = steering;
        NetGear = drivetrain.Gear;
        NetRpm = drivetrain.Rpm;
        NetLoad = drivetrain.Throttle;
        if (drivetrain.Stalls != stallsSeenByHost)
        {
            stallsSeenByHost = drivetrain.Stalls;
            NetStalls++;
        }

        var sw = Driver != PlayerRef.None ? (CarSwitch)hostPedals.switches : (CarSwitch)0;
        int lamps = 0;
        if ((sw & CarSwitch.Left) != 0) lamps |= LampLeft;
        if ((sw & CarSwitch.Right) != 0) lamps |= LampRight;
        if ((sw & CarSwitch.Hazard) != 0) lamps |= LampHazard;
        if ((sw & CarSwitch.Lights) != 0) lamps |= LampHead;
        if ((sw & CarSwitch.Manual) != 0) lamps |= LampManual;
        if ((sw & CarSwitch.NoAssists) != 0) lamps |= LampAssistsOff;
        if (hostControls.brake > 0.05f) lamps |= LampBrake;
        if (hostControls.handbrake > 0.5f) lamps |= LampHandbrake;
        if (drivetrain.Gear < 0) lamps |= LampReverse;
        if (drivetrain.Running) lamps |= LampRunning;
        if (drivetrain.Cranking) lamps |= LampCranking;
        if (drivetrain.AbsWorking) lamps |= LampAbs;
        if (drivetrain.EscWorking) lamps |= LampEsc;
        NetLamps = lamps;
    }

    private int stallsSeenByHost;

    // ------------------------------------------------------------------ physics

    /// <summary>
    /// A real car crumples rather than bounces: a knock keeps the heading but loses most of its spin and any
    /// hop, where the bare rigidbody flew off like a wooden box.
    /// </summary>
    private void OnCollisionEnter(Collision c)
    {
        if (!Live || !HasStateAuthority) return;
        // Where the car runs into the map: the height on the car says a kerb (low) from a wall (high),
        // the position finds a collider wider than what is drawn.
        if (c.rigidbody == null && c.relativeVelocity.magnitude > 1f && c.contactCount > 0)
        {
            var p = c.GetContact(0);
            Debug.Log($"[CarHit] {c.collider.name} at {p.point.x:F1} {p.point.y:F2} {p.point.z:F1}, " +
                      $"{transform.InverseTransformPoint(p.point).y:F2} up the car, normal {p.normal}, " +
                      $"{c.relativeVelocity.magnitude * 10.8f:F0} km/h");
        }
        // Something light (a body, a limb of one) is not a crash either: a person's ten ragdoll parts each took
        // their share and a run-over stopped the car dead.
        if (c.rigidbody != null && c.rigidbody.mass < body.mass * 0.2f) return;
        // Scraping the belly over a kerb or a crest is not a crash: only a knock from the side takes the speed.
        bool knock = false;
        for (int i = 0; i < c.contactCount; i++)
            knock |= Vector3.Dot(c.GetContact(i).normal, Vector3.up) < 0.6f;
        if (!knock) return;
        body.angularVelocity *= 0.35f;
        var v = body.linearVelocity;
        body.linearVelocity = new Vector3(v.x, Mathf.Min(v.y, 0.3f), v.z) * 0.85f;
    }

    private static readonly Collider[] Struck = new Collider[16];

    /// <summary>
    /// What the car runs into of the map's own scenery: people go down under it, parked and passing cars are shoved
    /// off (LooseCar). Every peer checks its own (they are not networked), by the speed the host reports, before
    /// they could stop the car like a post.
    /// </summary>
    private void Ram()
    {
        float speed = Speed;
        if (Mathf.Abs(speed) < 0.3f) return;
        var box = GetComponent<BoxCollider>();
        if (box == null) return;
        var velocity = transform.forward * speed;
        // the body box reaches down to the road here: the bumper meets people at the knees
        // three steps ahead: the car's continuous collision meets things sooner than one step would
        var centre = transform.TransformPoint(box.center - Vector3.up * bodyClearance * 0.5f) + velocity * Time.fixedDeltaTime * 3f;
        var half = Vector3.Scale(box.size + Vector3.up * bodyClearance, transform.lossyScale) * 0.5f + Vector3.one * 0.05f;
        int n = Physics.OverlapBoxNonAlloc(centre, half, Struck, transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = Struck[i];
            var person = c.GetComponentInParent<Pedestrian>();
            // about 10 km/h: slower than that people step aside
            if (person != null && !person.Dead && Mathf.Abs(speed) >= 1f)
            {
                person.RunOver(c.ClosestPoint(centre), velocity);
                // the driver's own screen reports it: the people there are the ones the driver saw go down
                if (Driver != PlayerRef.None && Driver == Runner.LocalPlayer) Police.Report(Driver, true);
            }
            // two cars of a weight: the one hit takes about half the speed, from the point of the blow
            var other = c.GetComponent<LooseCar>();
            if (other != null && other.Body == null) other.Knock(velocity * 0.55f, c.ClosestPoint(centre));
        }
    }

    /// <summary>
    /// The car falls by real gravity, not the game's 17 (which is for people's jumps): the world is a third of real
    /// size, and with lengths and gravity scaled alike the car moves in real time — its springs, the weight on its
    /// tyres, hills, kerbs and jumps all as a real car's, with no corrections on top.
    /// </summary>
    public const float Gravity = 9.81f / 3f;

    private const float SpringHz = 1.5f;        // body bounce, an ordinary saloon's
    private const float DampingRatio = 0.4f;
    private const float RestCompression = 0.45f; // of the travel, where the springs sit under the car's weight
    private const float AntiRollFront = 0.6f, AntiRollRear = 0.3f; // of a spring's rate: stiffer in front, it understeers at the limit

    private void FixedUpdate()
    {
        if (body != null && !body.isKinematic) body.AddForce(Vector3.down * Gravity, ForceMode.Acceleration);
        if (Live) Ram();
        // Physics is the host's; before a match starts nothing drives it.
        if (!Live || !HasStateAuthority) return;

        float dt = Time.fixedDeltaTime;
        var up = transform.up;
        float forwardSpeed = Vector3.Dot(body.linearVelocity, transform.forward);
        var controls = HostControls(dt, forwardSpeed, out float wanted);
        bool easing = Mathf.Abs(wanted) < Mathf.Abs(steering) || wanted * steering < 0f;
        steering = Mathf.MoveTowards(steering, wanted, (easing ? steerReturn : steerSpeed) * dt);

        // A keyboard only knows full lock; at speed that much would just slide the front tyres. The wheel turns no
        // further than the tyres can use (the bend the grip allows, plus their best slip angle) — what a
        // driver's hands would do.
        float realSpeed = Mathf.Abs(forwardSpeed) * 3f;
        float usable = realSpeed < 1f ? maxSteer
            : Mathf.Atan(2.6f * drivetrain.spec.grip * 9.81f * 1.1f / (realSpeed * realSpeed)) * Mathf.Rad2Deg + 6f;
        float steerAngle = steering * Mathf.Clamp(usable, 4f, maxSteer);

        // Each corner carries its share of the weight at RestCompression and bounces at SpringHz; in the last tenth of
        // the travel a bump stop takes over.
        float perWheel = body.mass / Mathf.Max(wheels.Count, 1);
        float omega = 2f * Mathf.PI * SpringHz;
        float stiffness = perWheel * omega * omega;
        float damping = 2f * DampingRatio * Mathf.Sqrt(stiffness * perWheel);
        float rest = RestCompression * suspensionTravel;

        WheelsOnGround = 0;
        foreach (var w in wheels)
        {
            // The whole tyre is swept down, not a ray through its middle: a kerb ahead meets the front of the
            // tyre first and lifts the wheel onto it, where a ray found it only under the axle, all at once.
            // The sweep starts inside the car's own box, so the nearest hit that is not the car is the road.
            var mount = transform.TransformPoint(w.center + Vector3.up * suspensionTravel);
            int count = Physics.SphereCastNonAlloc(mount, w.radius, -up, WheelHits, suspensionTravel + 0.1f, ~0,
                                                   QueryTriggerInteraction.Ignore);
            RaycastHit hit = default;
            w.grounded = false;
            for (int i = 0; i < count; i++)
            {
                var h = WheelHits[i];
                if (h.collider.attachedRigidbody == body || h.distance > suspensionTravel) continue;
                // Touching already where the sweep starts (a hard landing): measure straight down instead —
                // and if it is not under the wheel, it is a wall beside it, not road.
                if (h.distance <= 0f)
                {
                    var c = h.collider;
                    if (!c.Raycast(new Ray(mount, -up), out h, suspensionTravel + w.radius)) continue;
                    h.distance = Mathf.Max(0f, h.distance - w.radius);
                }
                if (!w.grounded || h.distance < hit.distance) hit = h;
                w.grounded = true;
            }
            var tyre = w.tyre;
            tyre.front = w.front;
            tyre.driven = drivetrain.spec.drive == CarSpecs.Drive.All ||
                          (w.front ? drivetrain.spec.drive == CarSpecs.Drive.Front : drivetrain.spec.drive == CarSpecs.Drive.Rear);
            tyre.radius = w.radius * 3f;
            tyre.steer = w.front ? steerAngle * Mathf.Deg2Rad : 0f;
            var fromCentre = (w.center - body.centerOfMass) * 3f;
            tyre.pos = new Vector2(fromCentre.x, fromCentre.z);
            tyre.grounded = w.grounded;
            tyre.load = 0f;
            if (!w.grounded)
            {
                w.compression = 0f;
                continue;
            }
            WheelsOnGround++;

            float compression = Mathf.Min(suspensionTravel, suspensionTravel - hit.distance);
            // touching down from the air the spring was at full droop: a fall's speed, not a jump across the gap
            float compressionSpeed = Mathf.Clamp((compression - w.compression) / dt, -3f, 3f);
            w.compression = compression;
            float load = perWheel * Gravity + stiffness * (compression - rest) + damping * compressionSpeed;
            float stop = compression - 0.9f * suspensionTravel;
            if (stop > 0f) load += stiffness * 12f * stop;
            w.load = load;
            w.mount = mount;
            w.contact = hit.point;
        }

        // Anti-roll bars: what one side of an axle sinks more than the other, the bar pushes up on it and lifts
        // the other side by — the body leans less, and the weight goes to the outer tyre of that axle.
        foreach (var w in wheels)
        {
            if (!w.grounded) continue;
            float other = 0f;
            foreach (var o in wheels)
                if (o != w && o.front == w.front && o.center.x * w.center.x < 0f) other = o.grounded ? o.compression : 0f;
            float bar = stiffness * (w.front ? AntiRollFront : AntiRollRear) * (w.compression - other);
            float load = Mathf.Max(0f, w.load + bar);
            body.AddForceAtPosition(up * load, w.mount);
            w.tyre.load = load * 3f;  // a newton here is three real ones
        }

        Tyres(dt, up, controls);

        // Beached on its belly (across a ditch, on a kerb stone) with no wheel on the ground: games let
        // the car crawl off rather than strand the player, so the pedal still pushes it slowly.
        float push = drivetrain.Running && drivetrain.Gear != 0 && drivetrain.Engagement > 0.5f
            ? controls.gas * Mathf.Sign(drivetrain.Gear) : 0f;
        if (WheelsOnGround <= 2 && Mathf.Abs(body.linearVelocity.y) < 0.3f && Mathf.Abs(push) > 0.05f
            && Mathf.Abs(forwardSpeed) < 1.5f)
        {
            body.AddForce(transform.forward * push * 3f * body.mass);
            body.AddTorque(Vector3.up * steering * body.mass * 0.6f);
        }
        // On its roof or side for a second: back on its wheels where it lies.
        flippedFor = Vector3.Dot(up, Vector3.up) < 0.3f ? flippedFor + dt : 0f;
        if (flippedFor > 1f)
        {
            body.MovePosition(body.position + Vector3.up * 0.4f);
            body.MoveRotation(Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            flippedFor = 0f;
        }
    }

    /// <summary>
    /// The drivetrain's tyre forces for this tick, onto the body where each tyre meets the road. Speeds and forces
    /// cross over between scene units and real ones here: a unit is 3 m, so a real newton is a third of one here.
    /// </summary>
    private void Tyres(float dt, Vector3 up, CarDrivetrain.Controls controls)
    {
        if (tyres.Length != wheels.Count)
        {
            tyres = new CarDrivetrain.Wheel[wheels.Count];
            for (int i = 0; i < wheels.Count; i++) tyres[i] = wheels[i].tyre;
        }
        var right = transform.right;
        var fwd = transform.forward;
        var v = body.linearVelocity;
        var velocity = new Vector2(Vector3.Dot(v, right), Vector3.Dot(v, fwd)) * 3f;
        float yawRate = Vector3.Dot(body.angularVelocity, up);

        // Weight goes to the back under power, to the front under braking, to the outside in a bend by itself now:
        // the tyres push at the road, below the centre of mass, and the springs and bars carry what that leans.
        float mass = body.mass;
        var tensor = body.inertiaTensor;
        var axis = Quaternion.Inverse(body.inertiaTensorRotation) * Vector3.up;
        float yawInertia = (tensor.x * axis.x * axis.x + tensor.y * axis.y * axis.y + tensor.z * axis.z * axis.z) * 9f;

        drivetrain.Step(dt, tyres, controls, mass, Mathf.Max(yawInertia, 100f), velocity, yawRate);

        foreach (var w in wheels)
        {
            var f = w.tyre.force;
            if (w.grounded) body.AddForceAtPosition((right * f.x + fwd * f.y) / 3f, w.contact);
            w.spin += w.tyre.spin * Mathf.Rad2Deg * dt;
        }
        float ahead = velocity.y;
        body.AddForce(-fwd * Mathf.Sign(ahead) * 0.5f * 1.2f * drivetrain.spec.dragArea * ahead * ahead / 3f);
        hostControls = controls;
    }

    private CarDrivetrain.Controls hostControls;
    private bool crankAllowed, syncShifts;

    /// <summary>
    /// Host: what the car is driven with this tick — the driver's key, pedals and lever (manual); the stick or a
    /// test's input (automatic); or nobody, and the handbrake is on.
    /// </summary>
    private CarDrivetrain.Controls HostControls(float dt, float forwardSpeed, out float steer)
    {
        var c = new CarDrivetrain.Controls();
        steer = 0f;
        float speed = forwardSpeed * 3f;
        if (Driver != lastDriver)
        {
            // got out: the engine goes off with the key; got in: nothing held from before counts
            if (Driver == PlayerRef.None) drivetrain.StopEngine();
            lastDriver = Driver;
            syncShifts = true;
            lastKey = true;
            crankAllowed = false;
        }
        bool scripted = ScriptedInput.HasValue;
        if (!scripted && Driver == PlayerRef.None)
        {
            c.handbrake = 1f;
            return c;
        }

        var p = ScriptedPedals ?? hostPedals;
        var sw = (CarSwitch)p.switches;
        var move = scripted ? ScriptedInput.Value : Control;
        steer = Mathf.Clamp(move.x, -1f, 1f);
        bool manualBox = (!scripted || ScriptedPedals.HasValue) && (sw & CarSwitch.Manual) != 0;
        drivetrain.automatic = !manualBox;
        drivetrain.abs = drivetrain.esc = scripted || (sw & CarSwitch.NoAssists) == 0;
        c.handbrake = (sw & CarSwitch.Handbrake) != 0 ? 1f : 0f;

        if (manualBox)
        {
            c.gas = p.gas / 255f;
            c.brake = p.brake / 255f;
            c.clutch = p.clutch / 255f;
            bool clutchDown = drivetrain.Gear == 0 || c.clutch >= 0.65f;
            bool key = (sw & CarSwitch.Key) != 0;
            if (key && !lastKey)
            {
                crankAllowed = false;
                if (drivetrain.Running) drivetrain.StopEngine();
                else if (!clutchDown) NetNoClutch++;
                else { crankAllowed = true; NetKeys++; }
            }
            if (!key) crankAllowed = false;
            lastKey = key;
            // the starter turns while the key is held, and only with nothing that would drive the car off on it
            c.crank = crankAllowed && !drivetrain.Running && clutchDown;

            // the lever count starts where this driver's is: moves made in another car are not made here
            if (syncShifts)
            {
                lastShifts = p.shifts;
                syncShifts = false;
            }
            if (p.shifts != lastShifts)
            {
                lastShifts = p.shifts;
                if (!drivetrain.TryShift(p.lever - 1, speed)) NetGrinds++;
            }
            return c;
        }

        // Automatic: the pedal drives forward; held on the brake once stopped, it backs up instead.
        float y = Mathf.Clamp(move.y, -1f, 1f);
        if (!drivetrain.Running && !drivetrain.Cranking && (Mathf.Abs(y) > 0.05f || scripted)) NetKeys++;
        c.crank = !drivetrain.Running && (Mathf.Abs(y) > 0.05f || scripted);
        if (drivetrain.Gear >= 0)
        {
            if (y > 0.05f)
            {
                drivetrain.SetDirection(1);
                c.gas = y;
                reverseHold = 0f;
            }
            else if (y < -0.05f)
            {
                c.brake = -y;
                reverseHold = speed > 0.5f ? 0f : reverseHold + dt;
                if (reverseHold > 0.3f) drivetrain.SetDirection(-1);
            }
            else reverseHold = 0f;
        }
        else if (y < -0.05f) c.gas = -y;
        else if (y > 0.05f)
        {
            if (speed < -0.5f) c.brake = y;
            else drivetrain.SetDirection(1);
        }
        // let go at a standstill: it holds itself, on a hill too
        if (Mathf.Abs(y) < 0.05f && Mathf.Abs(speed) < 0.3f) c.handbrake = 1f;
        return c;
    }

    private void LateUpdate()
    {
        // A remote copy has no suspension of its own; it steers and rolls by what the host says.
        if (Live && !HasStateAuthority)
        {
            steering = NetSteer;
            foreach (var w in wheels) w.spin += NetSpeed / w.radius * Mathf.Rad2Deg * Time.deltaTime;
        }

        // Wheels: follow the suspension, spin with the road, front ones steer.
        float steerAngle = steering * maxSteer;
        foreach (var w in wheels)
        {
            if (w.visual == null) continue;
            w.visual.rotation = transform.rotation * Quaternion.Euler(0f, w.front ? steerAngle : 0f, 0f)
                                * Quaternion.AngleAxis(w.spin, Vector3.right) * w.restRotation;
            // The wheel centre rides up by however much its spring is compressed.
            var centre = transform.TransformPoint(w.center + Vector3.up * (w.grounded ? w.compression : 0f));
            w.visual.position += centre - RendererBounds(w.visual.gameObject).center;
        }

        // The steering wheel follows the front wheels, many times over.
        if (steeringWheel != null && steeringAxis != null)
        {
            var hub = steeringWheel.position;
            var column = (hub - steeringAxis.position).normalized;
            steeringWheel.localRotation = steeringRest;
            steeringWheel.RotateAround(hub, column, steering * steeringWheelTurn);
        }

        if (!chaseCam.enabled) return;

        bool atWheel = Live && Driver == Runner.LocalPlayer;
        if (insideView && (!atWheel || steeringWheel == null || steeringAxis == null))
        {
            // Any other seat, or a model without a wheel of its own: eye height on that seat, from the body's size.
            // Driver front left, then front right, rear left, rear right.
            int seat = 0;
            if (!atWheel)
                for (int i = 0; i < Passengers.Length; i++)
                    if (Passengers[i] == Runner.LocalPlayer) seat = i + 1;
            float side = seat % 2 == 0 ? -1f : 1f, row = seat < 2 ? 0.05f : -0.35f;
            var eye = new Vector3(bodyBounds.center.x + side * bodyBounds.extents.x * 0.38f,
                                  bodyBounds.min.y + bodyBounds.size.y * 0.78f,
                                  bodyBounds.center.z + row * bodyBounds.extents.z);
            chaseCam.transform.position = transform.TransformPoint(eye);
            chaseCam.transform.rotation = transform.rotation * Quaternion.Euler(0f, lookYaw, 0f) * Quaternion.Euler(6f + lookPitch, 0f, 0f);
            camVelocity = Vector3.zero;
            return;
        }
        if (insideView)
        {
            // In the driver's seat: behind the wheel and a little above it, looking down the road.
            var towardDriver = (steeringWheel.position - steeringAxis.position).normalized;
            var flatForward = Vector3.ProjectOnPlane(transform.forward, transform.up).normalized;
            towardDriver = Vector3.ProjectOnPlane(towardDriver, transform.up).normalized;
            chaseCam.transform.position = steeringWheel.position + towardDriver * 0.15f + transform.up * 0.08f;
            // the head turns about the neck, a little behind the eyes: looking over a shoulder moves them aside
            var head = Quaternion.LookRotation(flatForward, transform.up) * Quaternion.Euler(0f, lookYaw, 0f);
            var neck = steeringWheel.position + towardDriver * 0.18f + transform.up * 0.08f;
            chaseCam.transform.position = neck + head * Vector3.forward * 0.03f;
            chaseCam.transform.rotation = head * Quaternion.Euler(6f + lookPitch, 0f, 0f);
            camVelocity = Vector3.zero;
            return;
        }

        // Hangs back further the faster it goes and trails the turns, like a racing game's chase view; the mouse
        // swings it round the car.
        var flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        flat = Quaternion.AngleAxis(lookYaw, Vector3.up) * flat;
        float back = 2.1f + 0.6f * Mathf.Clamp01(Mathf.Abs(Speed) / 25f);
        float rise = 0.85f + Mathf.Clamp(-lookPitch, -20f, 35f) * 0.02f;
        var target = transform.position - flat * back + Vector3.up * rise;
        chaseCam.transform.position = Vector3.SmoothDamp(chaseCam.transform.position, target, ref camVelocity, 0.08f);
        chaseCam.transform.LookAt(transform.position + Vector3.up * 0.35f + flat * 0.8f);
    }
}
