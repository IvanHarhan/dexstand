using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>What a player can take. The number is what travels in the input.</summary>
public enum Consumable : byte { None, Snus, Cigarette, Beer, Weed, Stim }

/// <summary>
/// Consumables: two per life, handed out on respawn, one effect at a time.
///
/// Only a handful of numbers travel — what is being taken and when that is
/// done, which effect runs and the tick it ends. Every consequence is worked
/// out from those and the tick by whoever needs it: the multipliers the weapon
/// and the legs read, the tremor, the cough, the beer's knife and dance, the
/// spit. The host and every client arrive at the same answer without being
/// told it.
///
/// The multipliers are read, never written into WeaponController or
/// PlayerController: the weapon switcher overwrites those fields on every swap,
/// and an effect written into them would quietly disappear.
/// </summary>
public class PlayerEffects : NetworkBehaviour
{
    public static readonly string[] Names = { "", "Снюс", "Сигарета", "Пиво", "Шмаль", "Укол" };

    [Header("Taking")]
    public int chargesPerLife = 2;

    [Tooltip("Seconds with the item at the mouth. Moving or shooting before the end " +
             "cancels it, and nothing is spent.")]
    public float useSeconds = 1.5f;

    [Tooltip("Beer is drunk by an authored clip: the weapon going down, then 3.7 s of drinking.")]
    public float beerTakeSeconds = 4.3f;

    public float UseSeconds(byte item) => item == (byte)Consumable.Beer ? beerTakeSeconds : useSeconds;

    [Tooltip("Stick travel that counts as walking off.")]
    public float moveCancel = 0.2f;

    [Header("Snus")]
    public float snusSeconds = 60f;

    [Tooltip("Share of recoil and spread left while it works.")]
    public float snusSteady = 0.65f;

    [Tooltip("The hand starts shaking this many seconds before the end.")]
    public float tremorSeconds = 12f;
    public float tremorDegrees = 0.6f;

    [Header("Cigarette")]
    public float cigaretteSeconds = 45f;

    [Tooltip("Degrees each cough throws the aim up.")]
    public float coughKick = 2.5f;

    [Header("Beer")]
    public float beerSeconds = 60f;
    public float beerSpeed = 1.2f;

    [Tooltip("About this often the hand goes for the knife on its own.")]
    public float knifeEvery = 15f;

    [Tooltip("A dance may start this often; it does on half of them.")]
    public float danceEvery = 20f;

    [Header("Weed")]
    public float weedSeconds = 30f;
    public float wallhackSeconds = 3f;

    [Tooltip("Jump height multiplier; take-off speed grows by its square root.")]
    public float weedJumpHeight = 5f;
    public float weedLook = 0.8f;
    public float weedFov = 0.8f;
    public float weedMuffleHz = 900f;

    [Header("Stim")]
    public float stimSeconds = 15f;
    public float withdrawalSeconds = 10f;
    public float stimHeal = 50f;
    public float stimDamageTaken = 0.5f;
    public float withdrawalSpeed = 0.7f;
    public float withdrawalSpread = 1.5f;

    [Header("Looks")]
    [Tooltip("Enemies are drawn with this through walls, on the smoker's screen only.")]
    public Material xrayMaterial;

    [Tooltip("How far the camera dips when spitting, in degrees.")]
    public float nodDegrees = 9f;
    public float nodSeconds = 0.6f;

    [Tooltip("Optional. Played where the player spits and coughs.")]
    public AudioClip spitSound;
    public AudioClip coughSound;

    [Networked] public byte Charges { get; set; }

    /// <summary>The item on its way to the mouth, or zero.</summary>
    [Networked] public byte Taking { get; set; }

    [Networked] private TickTimer TakeTimer { get; set; }

    /// <summary>The last effect taken. It runs until <see cref="EffectEnd"/>.</summary>
    [Networked] public byte Effect { get; set; }
    [Networked] public int EffectEnd { get; set; }

    /// <summary>The life the charges were handed out for, by death count.</summary>
    [Networked] private int ChargedLife { get; set; }

    private PlayerHealth health;
    private PlayerSetup setup;
    private PlayerController controller;
    private WeaponController weapon;
    private EmoteController emotes;
    private ImpactEffects impact;

    /// <summary>The effect running on this tick, withdrawal included.</summary>
    public Consumable Current =>
        Object != null && Object.IsValid && Runner.Tick.Raw < EffectEnd ? (Consumable)Effect : Consumable.None;

    /// <summary>Seconds until the current effect is over.</summary>
    public float Left => Current == Consumable.None ? 0f : (EffectEnd - Runner.Tick.Raw) * Runner.DeltaTime;

    /// <summary>Seconds since the current effect began.</summary>
    public float Elapsed => Current == Consumable.None ? 0f : Duration(Current) - Left;

    public bool InWithdrawal => Current == Consumable.Stim && Left <= withdrawalSeconds;

    /// <summary>A charge left, nothing in the hand or the blood, and alive.</summary>
    public bool CanTake =>
        Charges > 0 && Taking == 0 && Current == Consumable.None && (health == null || !health.IsDead);

    /// <summary>How far through taking the item the hand is, 0 to 1.</summary>
    public float TakeProgress
    {
        get
        {
            float? left = TakeTimer.RemainingTime(Runner);
            return Taking == 0 || left == null ? 0f : Mathf.Clamp01(1f - left.Value / UseSeconds(Taking));
        }
    }

    public float SpreadMultiplier =>
        Current == Consumable.Snus ? snusSteady : InWithdrawal ? withdrawalSpread : 1f;

    public float RecoilMultiplier => Current == Consumable.Snus ? snusSteady : 1f;

    public float SwayMultiplier => Current == Consumable.Cigarette ? 0f : 1f;

    /// <summary>Degrees of shake in the hand, growing over the last seconds of snus.</summary>
    public float Tremor =>
        Current == Consumable.Snus ? tremorDegrees * Mathf.Clamp01(1f - Left / tremorSeconds) : 0f;

    public float SpeedMultiplier =>
        Current == Consumable.Beer ? beerSpeed : InWithdrawal ? withdrawalSpeed : 1f;

    public float JumpMultiplier => Current == Consumable.Weed ? Mathf.Sqrt(weedJumpHeight) : 1f;

    public float LookMultiplier => Current == Consumable.Weed ? weedLook : 1f;

    private float DamageTaken => Current == Consumable.Stim && !InWithdrawal ? stimDamageTaken : 1f;

    public float Duration(Consumable item)
    {
        switch (item)
        {
            case Consumable.Snus: return snusSeconds;
            case Consumable.Cigarette: return cigaretteSeconds;
            case Consumable.Beer: return beerSeconds;
            case Consumable.Weed: return weedSeconds;
            case Consumable.Stim: return stimSeconds + withdrawalSeconds;
            default: return 0f;
        }
    }

    public override void Spawned()
    {
        health = GetComponent<PlayerHealth>();
        setup = GetComponent<PlayerSetup>();
        controller = GetComponentInChildren<PlayerController>(true);
        weapon = GetComponentInChildren<WeaponController>(true);
        emotes = GetComponent<EmoteController>();
        impact = GetComponent<ImpactEffects>();

        if (HasStateAuthority && health != null)
        {
            Charges = (byte)chargesPerLife;
            ChargedLife = health.DeathCount;
        }

        if (Object.HasInputAuthority) PrepareSenses();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        SetXRay(false);
        if (muffle != null) Destroy(muffle);
        if (held != null) Destroy(held);
    }

    public override void FixedUpdateNetwork()
    {
        if (health == null) return;

        if (HasStateAuthority)
        {
            // Death ends whatever was running; the next life starts with fresh
            // charges and a clear head.
            if (health.IsDead)
            {
                if (Current != Consumable.None) EffectEnd = Runner.Tick.Raw;
            }
            else if (ChargedLife != health.DeathCount)
            {
                ChargedLife = health.DeathCount;
                Charges = (byte)chargesPerLife;
            }

            health.incomingDamageMultiplier = DamageTaken;
        }

        if (!GetInput(out NetworkInputData data)) return;

        if (health.IsDead)
        {
            Taking = 0;
            return;
        }

        Take(data);
        Ongoing();
    }

    private void Take(NetworkInputData data)
    {
        bool moving = data.moveInput.sqrMagnitude > moveCancel * moveCancel || data.jump;
        bool wantsGun = data.fire || data.weapon > 0;

        if (Taking != 0)
        {
            // Walking off or going for the gun puts it away unused.
            if (moving || wantsGun)
            {
                Taking = 0;
                TakeTimer = default;
            }
            else if (TakeTimer.Expired(Runner))
            {
                Begin((Consumable)Taking);
            }
            return;
        }

        if (data.item > 0 && data.item <= (byte)Consumable.Stim && CanTake && !moving && !wantsGun)
        {
            Taking = data.item;
            TakeTimer = TickTimer.CreateFromSeconds(Runner, UseSeconds(data.item));
        }
    }

    private void Begin(Consumable item)
    {
        Taking = 0;
        TakeTimer = default;
        Charges--;
        Effect = (byte)item;
        EffectEnd = Runner.Tick.Raw + Ticks(Duration(item));

        // Health belongs to the server; a client healing itself would only be
        // corrected a moment later.
        if (item == Consumable.Stim && HasStateAuthority)
            health.Health = Mathf.Min(health.maxHealth, health.Health + stimHeal);
    }

    /// <summary>The moments of an effect that change the simulation.</summary>
    private void Ongoing()
    {
        var current = Current;

        // The beer goes for the knife every so often. It is the simulation's
        // own pick, so it lands the same on the host and the client.
        if (current == Consumable.Beer && weapon != null && Recurs(knifeEvery, 1))
            weapon.DrawMelee();

        // The cigarette ends in a coughing fit: two kicks the aim has to be
        // pulled back from.
        if (current == Consumable.Cigarette && controller != null)
        {
            int left = EffectEnd - Runner.Tick.Raw;
            if (left == Ticks(1.2f) || left == Ticks(0.6f))
                controller.ApplyRecoil(coughKick, (Hash(left, 3) - 0.5f) * coughKick);
        }
    }

    /// <summary>
    /// Whether this tick is one of an effect's recurring moments: roughly every
    /// period, each nudged by a hash of this player and this dose. Everyone
    /// computes the same moments from the same two networked numbers.
    /// </summary>
    private bool Recurs(float period, int salt)
    {
        int every = Ticks(period);
        if (every <= 0) return false;

        int since = Runner.Tick.Raw - DoseStart;
        int n = since / every;
        for (int k = Mathf.Max(1, n); k <= n + 1; k++)
        {
            if (since == k * every + Mathf.RoundToInt((Hash(k, salt) - 0.5f) * every * 0.4f)) return true;
        }
        return false;
    }

    /// <summary>Which of the beer's dances is due to start now, or -1.</summary>
    private int DanceDue()
    {
        if (Current != Consumable.Beer) return -1;

        int every = Ticks(danceEvery);
        int since = Runner.Tick.Raw - DoseStart;
        int k = every > 0 ? since / every : 0;

        if (k < 1 || Hash(k, 2) < 0.5f) return -1;
        return since - k * every < Ticks(0.5f) ? k : -1;
    }

    private int DoseStart => EffectEnd - Ticks(Duration((Consumable)Effect));

    private float Hash(int a, int b)
    {
        unchecked
        {
            uint h = Object.Id.Raw * 2654435761u ^ (uint)DoseStart * 40503u ^
                     (uint)a * 2246822519u ^ (uint)b * 3266489917u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private int Ticks(float seconds) => Mathf.RoundToInt(seconds / Runner.DeltaTime);

    // ------------------------------------------------------------------ looks

    private int dancedFor = -1;
    private int spatFor;
    private int coughedFor;
    private float nodUntil;

    /// <summary>The dip the camera takes when spitting, in degrees; the owner's view only.</summary>
    public float ViewNod
    {
        get
        {
            float left = nodUntil - Time.time;
            if (left <= 0f || nodSeconds <= 0f) return 0f;
            return Mathf.Sin((1f - left / nodSeconds) * Mathf.PI) * nodDegrees;
        }
    }

    public override void Render()
    {
        Dance();
        Spit();
        Cough();
        if (Object.HasInputAuthority)
        {
            Senses();
            Held();
        }
    }

    // ------------------------------------------------------------------ held item

    private GameObject held;
    private byte heldFor;

    /// <summary>
    /// The item on its way to the mouth, in front of the owner's camera while the
    /// weapon is down. There is no raise-to-mouth clip, so the item itself rises.
    /// Models live in Resources/Items; the weed borrows the cigarette, the stim
    /// has none, and the beer is held by the arms themselves (PackViewmodel).
    /// </summary>
    private void Held()
    {
        if (Taking != heldFor)
        {
            if (held != null) Destroy(held);
            held = null;
            heldFor = Taking;
            if (Taking != 0 && eye != null) held = SpawnHeld((Consumable)Taking);
        }
        if (held == null) return;

        // Up from below the frame to just under the lens in the first half, held there after.
        float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(TakeProgress * 2f));
        held.transform.localPosition = Vector3.Lerp(new Vector3(0.015f, -0.06f, 0.07f),
                                                    new Vector3(0.006f, -0.018f, 0.06f), t);
    }

    private GameObject SpawnHeld(Consumable item)
    {
        string name; float length; Vector3 tilt; Color tint;
        switch (item)
        {
            case Consumable.Snus: name = "snus"; length = 0.02f; tilt = new Vector3(-40f, 15f, 0f); tint = new Color(0.05f, 0.08f, 0.22f); break;
            case Consumable.Cigarette: name = "cigarette"; length = 0.028f; tilt = new Vector3(0f, 90f, 0f); tint = new Color(0.85f, 0.83f, 0.78f); break;
            case Consumable.Weed: name = "cigarette"; length = 0.034f; tilt = new Vector3(0f, 90f, 0f); tint = new Color(0.85f, 0.83f, 0.78f); break;
            default: return null;
        }

        var prefab = Resources.Load<GameObject>($"Items/{name}/{name}");
        if (prefab == null) return null;

        var go = Instantiate(prefab, eye.transform);
        PrepareHeld(go, name, tint);

        // Sized by its longest side, and centred on its own middle, whatever the source's units and pivot.
        go.transform.localRotation = Quaternion.Euler(tilt);
        // The snus model is as tall as it is wide; a real tin is a third of that.
        if (item == Consumable.Snus) go.transform.localScale = Vector3.Scale(go.transform.localScale, new Vector3(1f, 0.35f, 1f));
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (longest > 1e-6f) go.transform.localScale *= length / longest;

            bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            var offset = go.transform.position - bounds.center;
            var pivot = new GameObject("Held");
            pivot.transform.SetParent(eye.transform, false);
            go.transform.SetParent(pivot.transform, true);
            go.transform.position = pivot.transform.position + offset;
            return pivot;
        }
        return go;
    }

    /// <summary>
    /// Readies an item model for the owner's view: no physics, no shadows, and
    /// a colour where the source has none. Some sources bring a camera and a
    /// light of their own, which go too.
    /// </summary>
    public static void PrepareHeld(GameObject go, string name, Color tint, bool recolour = false)
    {
        go.name = "Held_" + name;
        foreach (var c in go.GetComponentsInChildren<Camera>(true)) Destroy(c.gameObject);
        foreach (var l in go.GetComponentsInChildren<Light>(true)) Destroy(l.gameObject);
        foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // The sources ship colourless: an untextured material renders as blown-out white.
            if (recolour || r.sharedMaterial == null || r.sharedMaterial.mainTexture == null)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = Tinted(name, tint);
                r.sharedMaterials = mats;
            }
        }
    }

    private static readonly Dictionary<string, Material> tinted = new Dictionary<string, Material>();

    private static Material Tinted(string name, Color color)
    {
        if (tinted.TryGetValue(name, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Held_" + name, color = color };
        m.SetFloat("_Smoothness", 0.35f);
        tinted[name] = m;
        return m;
    }

    /// <summary>
    /// The beer's dance, on every screen at once: the moment is worked out from
    /// the dose, and moving or shooting breaks it off, as seen by each peer.
    /// </summary>
    private void Dance()
    {
        if (emotes == null || controller == null) return;

        bool busy = controller.NetIsMoving || controller.NetIsFiring || (health != null && health.IsDead);
        int due = DanceDue();

        if (due > 0 && due != dancedFor && !busy)
        {
            dancedFor = due;
            emotes.PlayDance();
        }
        else if (emotes.IsDancing && busy)
        {
            emotes.StopDance();
        }
    }

    /// <summary>The end of a pouch of snus, seen by everyone: a splat by the feet.</summary>
    private void Spit()
    {
        if (Effect != (byte)Consumable.Snus || EffectEnd == spatFor || Runner.Tick.Raw < EffectEnd) return;
        spatFor = EffectEnd;

        // Died with it in, or long over by the time this peer looked.
        if (health != null && health.IsDead) return;
        if (Runner.Tick.Raw - EffectEnd > Ticks(1f) || controller == null) return;

        var feet = controller.transform;
        var from = feet.position + feet.forward * 0.25f + Vector3.up * 0.3f;
        if (Physics.Raycast(from, Vector3.down, out var hit, 1.5f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (impact != null) impact.PlaySpit(hit.point, hit.normal);
            if (spitSound != null) AudioSource.PlayClipAtPoint(spitSound, hit.point);
        }

        if (Object.HasInputAuthority) nodUntil = Time.time + nodSeconds;
    }

    /// <summary>The cough's sound, for everyone near; the kick itself is simulated.</summary>
    private void Cough()
    {
        if (coughSound == null || Effect != (byte)Consumable.Cigarette || EffectEnd == coughedFor) return;
        if (Runner.Tick.Raw < EffectEnd - Ticks(1.2f) || Runner.Tick.Raw >= EffectEnd) return;

        coughedFor = EffectEnd;
        if (controller != null) AudioSource.PlayClipAtPoint(coughSound, controller.transform.position);
    }

    // What the smoker alone sees and hears.

    private Camera eye;
    private float baseFov;
    private AudioLowPassFilter muffle;
    private bool seeingThrough;
    private readonly List<GameObject> xrays = new List<GameObject>();

    private void PrepareSenses()
    {
        eye = setup != null && setup.cameraObject != null ? setup.cameraObject.GetComponent<Camera>() : null;
        if (eye != null) baseFov = eye.fieldOfView;

        var listener = eye != null ? eye.GetComponent<AudioListener>() : null;
        if (listener != null)
        {
            muffle = listener.gameObject.AddComponent<AudioLowPassFilter>();
            muffle.cutoffFrequency = weedMuffleHz;
            muffle.enabled = false;
        }
    }

    private void Senses()
    {
        bool stoned = Current == Consumable.Weed;

        if (eye != null)
        {
            float wanted = baseFov * (stoned ? weedFov : 1f);
            eye.fieldOfView = Mathf.Lerp(eye.fieldOfView, wanted, 4f * Time.deltaTime);
        }

        if (muffle != null) muffle.enabled = stoned;

        bool xray = stoned && Elapsed < wallhackSeconds;
        if (xray != seeingThrough) SetXRay(xray);
    }

    /// <summary>
    /// Every other player drawn again through the walls, on this screen only:
    /// a copy of each body's skinned mesh on the same bones, in a material that
    /// draws only where something stands in front of it.
    /// </summary>
    private void SetXRay(bool on)
    {
        seeingThrough = on;

        foreach (var go in xrays)
        {
            if (go != null) Destroy(go);
        }
        xrays.Clear();

        if (!on || xrayMaterial == null) return;

        foreach (var other in FindObjectsByType<PlayerSetup>(FindObjectsSortMode.None))
        {
            if (other == setup || other.body == null) continue;

            foreach (var skin in other.body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.sharedMesh == null) continue;

                var go = new GameObject("XRay");
                go.transform.SetParent(skin.transform, false);

                var copy = go.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = skin.sharedMesh;
                copy.bones = skin.bones;
                copy.rootBone = skin.rootBone;
                copy.updateWhenOffscreen = true;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                copy.receiveShadows = false;

                var materials = new Material[skin.sharedMesh.subMeshCount];
                for (int i = 0; i < materials.Length; i++) materials[i] = xrayMaterial;
                copy.sharedMaterials = materials;

                xrays.Add(go);
            }
        }
    }
}
