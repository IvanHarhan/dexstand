using UnityEngine;

/// <summary>
/// Sways the viewmodel while walking. The arms pack ships only a fire clip, so
/// the sense of footsteps is generated here instead of played back.
/// </summary>
public class WeaponBob : MonoBehaviour
{
    [Header("Source")]
    public PlayerController player;

    [Tooltip("Viewmodel animator. When set, real clips drive idle/walk/run, " +
             "fire, reload and inspect, and the procedural bob steps aside.")]
    public Animator animator;

    private static readonly int HashSpeed = Animator.StringToHash("Speed");
    private static readonly int HashFire = Animator.StringToHash("Fire");
    private static readonly int HashReload = Animator.StringToHash("Reload");
    private static readonly int HashInspect = Animator.StringToHash("Inspect");

    public void TriggerFire()
    {
        if (animator != null) animator.SetTrigger(HashFire);
    }

    /// <summary>
    /// Plays the magazine change: the authored clip where there is one, and
    /// otherwise the scripted dip below.
    ///
    /// With the weapon in the body's own hands there is no viewmodel animator at
    /// all, and the body's reload clip only moves the arms — the weapon itself
    /// hangs off the wrist and would go through the whole change without once
    /// being tipped towards the lens, which is where the magazine actually goes
    /// in. So the hold point does that part.
    /// </summary>
    public void TriggerReload(float seconds)
    {
        if (animator != null) animator.SetTrigger(HashReload);
    }

    [Header("Bob")]
    [Tooltip("Metres per step. The bob advances with distance walked, not with " +
             "time, so it can never cycle fast while the player creeps along.")]
    public float strideLength = 0.5f;

    public float horizontalAmount = 0.012f;
    public float verticalAmount = 0.018f;

    [Header("Response")]
    public float smoothing = 12f;

    [Header("Recoil")]
    public float kickBack = 0.04f;
    public float kickUp = 0.02f;
    public float kickRecovery = 9f;
    public float maxKick = 0.12f;

    private Vector3 restPosition;

    /// <summary>
    /// Where the weapon that is out wants to be carried, relative to the rest
    /// point the rifle was framed at. Set by <see cref="WeaponSwitcher"/> on
    /// every swap; the bob, the kick and the inspect all ride on top of it.
    /// </summary>
    [HideInInspector] public Vector3 holdOffset;

    /// <summary>
    /// Where the hold point goes while the sights are up, set per weapon by the
    /// switcher. Measured at build time from the weapon's own sight line.
    /// </summary>
    [HideInInspector] public Vector3 aimOffset;

    /// <summary>
    /// The turn that takes the weapon off its carry angle and straight down the
    /// view axis, so the sights line up rather than merely coming closer.
    /// </summary>
    [HideInInspector] public Vector3 aimTilt;

    [Header("Aim")]
    [Tooltip("Seconds to bring the sights up and to drop them again.")]
    public float aimSeconds = 0.16f;

    /// <summary>How far up the sights are, 0 to 1. Read by the HUD and the spread.</summary>
    public float AimWeight { get; private set; }

    private bool aiming;

    /// <summary>Held by the player's own input; the body is not networked for it yet.</summary>
    public void SetAiming(bool value) => aiming = value;
    private Vector3 recoil;
    private float phase;

    [Header("Melee swing")]
    public float swingDegrees = 70f;
    public float swingRecovery = 5f;

    [Header("Inspect")]
    public float inspectSeconds = 1.6f;

    private float swing;
    private Quaternion restRotation;
    private float inspectUntil;
    private float inspectStarted;
    public bool IsInspecting => Time.time < inspectUntil;

    /// <summary>
    /// Turns the weapon over in the hands for a look. Scripted rather than
    /// animated: the arms pack ships one clip and it is the fire clip.
    /// </summary>
    public void Inspect()
    {
        // Prefer the authored clip; the scripted twirl is the fallback for
        // weapons that ship without one.
        if (animator != null)
        {
            animator.SetTrigger(HashInspect);
            return;
        }

        inspectStarted = Time.time;
        inspectUntil = Time.time + inspectSeconds;
    }

    /// <summary>The pose partway through an inspect, as position and rotation offsets.</summary>
    private void SampleInspect(out Vector3 offset, out Quaternion rotation)
    {
        float t = Mathf.Clamp01((Time.time - inspectStarted) / Mathf.Max(0.01f, inspectSeconds));

        // Ease in and out so it starts and ends at rest.
        float envelope = Mathf.Sin(t * Mathf.PI);

        offset = new Vector3(-0.03f, 0.02f, -0.05f) * envelope;
        rotation = Quaternion.Euler(
            -18f * envelope,
            42f * Mathf.Sin(t * Mathf.PI * 1.5f),
            25f * envelope);
    }

    [Header("Landing")]
    [Tooltip("Metres the weapon dips when the player lands, and how fast it " +
             "comes back up.")]
    public float landingDip = 0.05f;
    public float landingRecovery = 6f;

    private float landDrop;
    private bool wasAirborne;

    /// <summary>
    /// The dip that takes the weight of a landing. Driven off the controller's
    /// own grounded flag rather than off a jump trigger, so a step off a crate
    /// lands the same way a jump does.
    /// </summary>
    private Vector3 Landing()
    {
        bool airborne = player != null && !player.IsGrounded;
        if (wasAirborne && !airborne) landDrop = landingDip;
        wasAirborne = airborne;

        landDrop = Mathf.Lerp(landDrop, 0f, landingRecovery * Time.deltaTime);
        return new Vector3(0f, -landDrop, 0f);
    }

    [Header("Swap")]
    [Tooltip("Metres the weapon drops out of frame while it is being put away.")]
    public float holsterDrop = 0.06f;

    [Tooltip("Degrees it tips muzzle-down as it goes.")]
    public float holsterTilt = 35f;

    private float swapStarted = -99f;
    private float swapLength;
    private bool swapDrawing;

    /// <summary>
    /// Lowers the weapon out of shot. The switcher calls this, waits, changes
    /// the model and then calls <see cref="Draw"/> — without it a rifle becomes
    /// a knife between two frames with the hands never moving.
    /// </summary>
    public void Holster(float seconds)
    {
        swapStarted = Time.time;
        swapLength = Mathf.Max(0.05f, seconds);
        swapDrawing = false;
    }

    /// <summary>Brings the new weapon back up into shot.</summary>
    public void Draw(float seconds)
    {
        swapStarted = Time.time;
        swapLength = Mathf.Max(0.05f, seconds);
        swapDrawing = true;
    }

    private bool IsSwapping => Time.time < swapStarted + swapLength;

    /// <summary>
    /// How far out of frame the weapon is right now: all the way down at the end
    /// of a holster, all the way up at the end of a draw.
    /// </summary>
    private void SampleSwap(out Vector3 offset, out Quaternion rotation)
    {
        float t = Mathf.Clamp01((Time.time - swapStarted) / Mathf.Max(0.01f, swapLength));
        float away = swapDrawing ? 1f - t : t;
        away = Mathf.SmoothStep(0f, 1f, away);

        offset = new Vector3(0f, -holsterDrop * away, 0f);
        rotation = Quaternion.Euler(holsterTilt * away, 0f, 0f);
    }

    /// <summary>Punches the viewmodel back and up; called on every shot.</summary>
    public void Kick()
    {
        recoil += new Vector3(0f, kickUp, -kickBack);
        recoil = Vector3.ClampMagnitude(recoil, maxKick);
    }

    /// <summary>
    /// A diagonal slash for the knife. Firearms jolt straight back, a blade
    /// travels across the view, so melee gets its own motion.
    /// </summary>
    public void Slash()
    {
        swing = swingDegrees;
        recoil += new Vector3(-kickBack * 0.5f, 0f, kickBack);
        recoil = Vector3.ClampMagnitude(recoil, maxKick);
    }

    [Header("Sway")]
    [Tooltip("How far the weapon lags behind a turn of the head, in metres per " +
             "radian per second. Pinned rigidly to the view it reads as painted " +
             "on the lens; this is what makes it feel carried.")]
    public float swayAmount = 0.06f;

    [Tooltip("How far it tips into the turn, in degrees on the same scale.")]
    public float swayTilt = 5f;

    [Tooltip("Metres and degrees the sway is allowed to reach at full whip.")]
    public float maxSway = 0.03f;

    public float swaySmoothing = 10f;

    private Vector3 sway;
    private Vector3 lastLook;

    private void Awake()
    {
        restPosition = transform.localPosition;
        restRotation = transform.localRotation;
        lastLook = Look();
    }

    /// <summary>
    /// Where the head is pointing, as a direction in the player's own space.
    /// Taken off the transform rather than off the input, so it follows whatever
    /// moved the view — mouse, touch drag or the network's own interpolation.
    /// </summary>
    private Vector3 Look()
    {
        var holder = transform.parent;
        return holder != null ? holder.forward : transform.forward;
    }

    /// <summary>
    /// The offset and tip the weapon lags behind the view by, eased back to zero
    /// once the turn stops.
    /// </summary>
    private void Sway(ref Vector3 target, ref Quaternion rotation)
    {
        Vector3 look = Look();
        var holder = transform.parent;

        Vector3 delta = holder != null
            ? holder.InverseTransformDirection(look - lastLook)
            : look - lastLook;
        lastLook = look;

        float dt = Mathf.Max(0.0001f, Time.deltaTime);
        Vector3 wanted = new Vector3(-delta.x, -delta.y, 0f) * (swayAmount / dt);
        wanted = Vector3.ClampMagnitude(wanted, maxSway);

        sway = Vector3.Lerp(sway, wanted, swaySmoothing * dt);
        target += sway;

        // Rolled into the turn as well as dragged: a carried weapon pivots about
        // the grip, it does not slide sideways on rails.
        float scale = maxSway > 0.0001f ? swayTilt / maxSway : 0f;
        rotation *= Quaternion.Euler(-sway.y * scale, sway.x * scale, sway.x * scale * 1.5f);
    }

    private void LateUpdate()
    {
        float speed = player != null ? player.NetSpeed : 0f;

        // Feed the blend tree so idle, walk and run pick themselves.
        if (animator != null) animator.SetFloat(HashSpeed, speed, 0.12f, Time.deltaTime);

        float reference = player != null ? Mathf.Max(0.01f, player.walkSpeed) : 1f;

        // Down the sights the weapon is braced: the walk bob all but stops, or
        // the shot the player is lining up wanders off the target by itself.
        float intensity = Mathf.Clamp01(speed / reference) * (1f - AimWeight * 0.8f);

        // The sights come up and go down on their own clock, so nothing else has
        // to know whether the button is still held.
        AimWeight = Mathf.MoveTowards(AimWeight, aiming ? 1f : 0f,
                                      Time.deltaTime / Mathf.Max(0.01f, aimSeconds));

        Vector3 target = restPosition + holdOffset + aimOffset * AimWeight;

        if (intensity > 0.01f)
        {
            // One full bob per stride: phase follows distance covered.
            phase += speed * Time.deltaTime / Mathf.Max(0.05f, strideLength) * Mathf.PI;

            // Vertical runs at double rate: one dip per footfall, two per stride.
            target += new Vector3(
                Mathf.Sin(phase) * horizontalAmount * intensity,
                -Mathf.Abs(Mathf.Sin(phase * 2f)) * verticalAmount * intensity,
                0f);
        }
        else
        {
            phase = 0f;

            // Standing still is not standing frozen: a slow, shallow drift, a
            // twelfth of the walking bob, which reads as breathing rather than
            // as a paused game.
            float breath = Time.time * 1.1f;
            target += new Vector3(Mathf.Sin(breath * 0.7f), Mathf.Sin(breath), 0f) *
                      (verticalAmount * 0.12f);
        }

        target += Landing();

        recoil = Vector3.Lerp(recoil, Vector3.zero, kickRecovery * Time.deltaTime);
        target += recoil;

        var extraRotation = Quaternion.identity;
        if (IsSwapping)
        {
            SampleSwap(out var swapOffset, out extraRotation);
            target += swapOffset;
        }
        else if (IsInspecting)
        {
            SampleInspect(out var inspectOffset, out extraRotation);
            target += inspectOffset;
        }

        Sway(ref target, ref extraRotation);

        if (AimWeight > 0.001f)
        {
            extraRotation = Quaternion.Slerp(Quaternion.identity,
                                             Quaternion.Euler(aimTilt), AimWeight) * extraRotation;
        }

        transform.localPosition = Vector3.Lerp(
            transform.localPosition, target, smoothing * Time.deltaTime);

        swing = Mathf.Lerp(swing, 0f, swingRecovery * Time.deltaTime);
        var swingRotation = Quaternion.Euler(swing, 0f, -swing * 0.6f);

        transform.localRotation = Quaternion.Slerp(
            transform.localRotation,
            restRotation * swingRotation * extraRotation,
            smoothing * Time.deltaTime);
    }
}
