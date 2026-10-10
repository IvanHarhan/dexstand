using Fusion;
using UnityEngine;

/// <summary>
/// Movement + camera rotation for the player.
/// Sits on PlayerRoot which has CharacterController.
///
/// Hierarchy:
///   Player (NetworkObject, PlayerSetup)
///     PlayerRoot (this + CharacterController) — rotates Y
///       CameraHolder — rotates X (pitch)
///         Camera
///         Arms (FPS model, Generic rig)
///     Body (3rd person model, visible to others)
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 4.5f;
    public float gravity = 20f;
    public float jumpForce = 6f;

    [Header("Camera")]
    public float lookSensitivity = 1f;
    public float minPitch = -75f;
    public float maxPitch = 75f;

    [Header("Sway")]
    [Tooltip("How far the aim wanders on its own, in degrees. Nobody holds a rifle " +
             "perfectly still, and long single shots are where it shows.")]
    public float swayDegrees = 0.5f;

    [Tooltip("Seconds for the aim to trace its figure of eight once.")]
    public float swayPeriod = 4f;

    [Header("Bodycam")]
    [Tooltip("Seconds the owner's camera trails a turn with the bodycam slider at " +
             "100% — the stand-in for motion blur, which a phone cannot afford. " +
             "Only the picture trails; shots follow the aim.")]
    public float bodycamLagSeconds = 0.08f;

    [Header("References")]
    public Transform cameraHolder;
    public ArmsAnimationDriver armsDriver;

    [Networked] public float NetYaw { get; set; }
    [Networked] public float NetPitch { get; set; }
    [Networked] public NetworkBool NetIsMoving { get; set; }
    [Networked] public float NetSpeed { get; set; }
    [Networked] public NetworkBool NetIsFiring { get; set; }

    /// <summary>
    /// How many times this player has jumped. A counter rather than a flag: the
    /// animator wants a trigger, and a networked bool cannot deliver one —
    /// proxies only see state at tick boundaries, so a flag raised and lowered
    /// inside one tick is simply never observed. A number that only grows is
    /// observed by anyone who compares it with what they saw last.
    /// </summary>
    [Networked] public int NetJumps { get; set; }

    /// <summary>
    /// Emotes played so far, and the last one. A counter like
    /// <see cref="NetJumps"/> and for the same reason: every peer that sees it
    /// grow plays the clip once, so the wave is seen by everyone.
    /// </summary>
    [Networked] public int NetEmotes { get; set; }
    [Networked] public byte NetEmote { get; set; }

    /// <summary>Behind the wheel of the car: out of sight, out of the fight.</summary>
    [Networked] public NetworkBool NetInCar { get; set; }

    /// <summary>
    /// Networked, not a field: the owner's client re-runs past ticks whenever the host's state arrives, and a
    /// plain field carried the fall speed of the last re-run into the next one.
    /// </summary>
    [Networked] private float VerticalVelocity { get; set; }

    private CharacterController cc;
    private NetworkCharacterController netCC;
    private PlayerHealth health;
    private PlayerEffects effects;
    private float renderYaw;
    private float renderPitch;

    // Networked through netCC: a CharacterController re-enabled for a rollback reports not grounded for a tick,
    // which ate jumps pressed on that tick.
    public bool IsGrounded => netCC != null && Object != null && Object.IsValid ? netCC.Grounded : cc.isGrounded;

    /// <summary>
    /// Moves the player without the CharacterController fighting it. Move() is
    /// the only sanctioned way in, and it sweeps — teleporting across the map
    /// with it would collide with everything on the way. Disabling the
    /// controller for one assignment is the standard way around that.
    /// </summary>
    public void Teleport(Vector3 position)
    {
        if (Object != null && Object.IsValid) VerticalVelocity = 0f;

        if (netCC != null && Object != null && Object.IsValid)
        {
            netCC.Teleport(position);
            return;
        }

        bool wasEnabled = cc.enabled;
        cc.enabled = false;
        transform.position = position;
        cc.enabled = wasEnabled;

        var netTransform = GetComponent<NetworkTransform>();
        if (netTransform != null) netTransform.Teleport(position);
    }

    /// <summary>
    /// Kicks the aim up and sideways. Applied to the networked look angles so
    /// the shot really climbs and the player has to pull back down, rather than
    /// being a camera effect that does not affect where bullets go.
    /// </summary>
    public void ApplyRecoil(float pitchKick, float yawKick)
    {
        NetPitch = Mathf.Clamp(NetPitch - pitchKick, minPitch, maxPitch);
        NetYaw += yawKick;
    }

    /// <summary>
    /// How far the aim has wandered at a moment of the match, as (yaw, pitch)
    /// in degrees. It depends on the clock alone, so the host and the shooter's
    /// client work out the same drift for the same tick without a byte of it on
    /// the network.
    /// </summary>
    public Vector2 Sway(float time)
    {
        float phase = time * 2f * Mathf.PI / Mathf.Max(0.01f, swayPeriod);
        var drift = new Vector2(Mathf.Sin(phase), 0.5f * Mathf.Sin(2f * phase)) * swayDegrees;
        if (effects == null) return drift;

        // A shaking hand on top of the drift: fast, and uneven enough not to
        // settle into a pattern that could be timed.
        var shake = new Vector2(Mathf.Sin(time * 37f) + 0.5f * Mathf.Sin(time * 71f),
                                Mathf.Sin(time * 43f)) * effects.Tremor;
        return drift * effects.SwayMultiplier + shake;
    }

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        netCC = GetComponent<NetworkCharacterController>();
        health =GetComponentInParent<PlayerHealth>();
        effects = GetComponentInParent<PlayerEffects>();
    }

    /// <summary>The nickname the player typed in the menu, shown over their head to everyone else (NameTag).</summary>
    [Networked] public NetworkString<_32> Nick { get; set; }

    public override void Spawned()
    {
        NetYaw = transform.eulerAngles.y;
        renderYaw = NetYaw;
        renderPitch = 0f;
        if (Object.HasInputAuthority) RPC_SetNick(NickName.Current);
        else NameTag.Create(this);
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void RPC_SetNick(NetworkString<_32> nick) => Nick = nick;

    public override void FixedUpdateNetwork()
    {
        if (!GetInput(out NetworkInputData data)) return;

        // A corpse still falls, it just does not steer. Skipping the whole
        // method instead would leave the body hanging wherever it died.
        if (health != null && health.IsDead)
        {
            data.moveInput = Vector2.zero;
            data.lookInput = Vector2.zero;
            data.jump = false;
            data.fire = false;
            data.emote = 0;
        }

        if (data.emote > 0)
        {
            NetEmote = (byte)(data.emote - 1);
            NetEmotes++;
        }

        if (Drive(data)) return;

        // Consumables scale these on top; the fields themselves stay as built.
        float look = lookSensitivity * (effects != null ? effects.LookMultiplier : 1f);
        float speed = walkSpeed * (effects != null ? effects.SpeedMultiplier : 1f);
        float jump = jumpForce * (effects != null ? effects.JumpMultiplier : 1f);

        // --- Look ---
        NetYaw += data.lookInput.x * look;
        NetPitch -= data.lookInput.y * look;
        NetPitch = Mathf.Clamp(NetPitch, minPitch, maxPitch);

        // --- Movement (relative to look direction) ---
        Vector3 forward = Quaternion.Euler(0f, NetYaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, NetYaw, 0f) * Vector3.right;
        Vector3 move = forward * data.moveInput.y + right * data.moveInput.x;
        move *= speed;

        // Gravity + jump
        if (IsGrounded)
        {
            VerticalVelocity = -1f;
            if (data.jump)
            {
                VerticalVelocity = jump;
                NetJumps++;
            }
        }
        else
        {
            VerticalVelocity -= gravity * Runner.DeltaTime;
        }

        move.y = VerticalVelocity;
        // The yaw goes into the synced rotation too; left to Render alone, every rollback restored a stale one.
        transform.rotation = Quaternion.Euler(0f, NetYaw, 0f);
        cc.Move(move * Runner.DeltaTime);
        if (netCC != null) netCC.Grounded = cc.isGrounded;

        // --- State ---
        // Walking only while the stick or keys ask for it: standing on the map's slopes the controller slides and
        // snaps by a hair every tick, and that speed alone kept a standing player's legs walking to everyone else.
        Vector3 hVel = new Vector3(cc.velocity.x, 0f, cc.velocity.z);
        bool asked = data.moveInput.sqrMagnitude > 0.01f;
        NetIsMoving = asked && hVel.magnitude > 0.1f;
        NetSpeed = asked ? hVel.magnitude : 0f;
        NetIsFiring = data.fire;
    }

    /// <summary>
    /// The car: the host seats or lets out whoever pressed its button, and a
    /// player who is driving rides along above it, out of sight and out of its
    /// way — the move input steers the car instead of the legs.
    /// </summary>
    private bool Drive(NetworkInputData data)
    {
        var car = SimpleCar.Current;
        if (car == null) return false;

        // at the wheel or riding along: either way the legs stay out of it
        var me = Object.InputAuthority;
        bool was = car.IsInside(me);
        if (HasStateAuthority && (data.car || (was && health != null && health.IsDead)))
            car.Toggle(me, transform.position);

        bool inside = car.IsInside(me);
        if (was && !inside && HasStateAuthority) Teleport(car.ExitPoint);

        NetInCar = inside;
        if (!inside) return false;

        Teleport(car.transform.position + Vector3.up * 2f);
        NetIsMoving = false;
        NetSpeed = 0f;
        NetIsFiring = false;
        return true;
    }

    public override void Render()
    {
        // The owner's camera trails a turn by as much as the bodycam slider
        // asks; everyone else's copy only smooths out the ticks.
        float lag = Object.HasInputAuthority
            ? Mathf.Lerp(1f / 25f, bodycamLagSeconds, BodycamIntensity.Weight)
            : 1f / 25f;
        float t = Time.deltaTime / lag;
        renderYaw = Mathf.LerpAngle(renderYaw, NetYaw, t);
        renderPitch = Mathf.Lerp(renderPitch, NetPitch, t);

        // PlayerRoot rotates Y
        transform.rotation = Quaternion.Euler(0f, renderYaw, 0f);

        // CameraHolder rotates X (pitch). Arms are child of CameraHolder
        // so they follow automatically — no bone rotation needed. The sway is
        // drawn here as well, or the crosshair would sit still while the shots
        // wander off it.
        if (cameraHolder != null)
        {
            var sway = Sway(Runner.LocalRenderTime);
            float nod = effects != null ? effects.ViewNod : 0f;
            cameraHolder.localRotation = Quaternion.Euler(renderPitch + sway.y + nod, sway.x, 0f);
        }

        // Drive arms animation
        if (armsDriver != null)
            armsDriver.UpdateState(NetSpeed, NetIsFiring);
    }
}
