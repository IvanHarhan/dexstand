using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// Weapon system: raycast from screen center, spread, debug visualization.
/// Extensible via IWeapon interface for weapon switching.
/// </summary>
public class WeaponController : NetworkBehaviour
{
    [Header("Weapon Settings")]
    [Tooltip("Seconds between shots while the trigger is held — 0.1 is ~600 rpm.")]
    public float fireRate = 0.1f;
    public float maxDistance = 500f;

    [Header("Spread (degrees)")]
    public float baseSpread = 1f;
    public float movingSpread = 3f;
    public float jumpingSpread = 6f;

    [Header("Damage")]
    public float damage = 25f;

    [Header("Fire mode")]
    [Tooltip("Held trigger keeps firing. Off means one shot per press.")]
    public bool automatic = true;

    [Header("Magazine")]
    [Tooltip("Rounds per magazine. Zero means the weapon never runs dry — a knife.")]
    public int magazineSize = 30;

    [Tooltip("How long the reload takes. Match it to the clip or the gun is " +
             "usable again before the hands have finished putting it back together.")]
    public float reloadSeconds = 2.2f;

    /// <summary>
    /// Rounds left. Networked because the server decides what a shot costs: a
    /// client that kept its own count could fire a magazine that is already
    /// empty on everyone else's screen.
    /// </summary>
    [Networked] public int Ammo { get; set; }

    [Networked] private TickTimer ReloadTimer { get; set; }

    /// <summary>
    /// The switcher slot in hand. Networked because the server resolves every
    /// shot with the numbers of this weapon: while the pick stayed on the
    /// client, the host shot everybody with the rifle they had spawned with.
    /// </summary>
    [Networked] public int WeaponIndex { get; set; }

    /// <summary>Slot whose numbers are loaded into the fields below.</summary>
    private int appliedIndex = -1;

    /// <summary>For the HUD: true while the magazine is being changed.</summary>
    public bool IsReloading => !ReloadTimer.ExpiredOrNotRunning(Runner);

    /// <summary>
    /// How far through the magazine change we are, 0 to 1, and 1 when none is
    /// running. The hands are moved along this rather than along a clock of
    /// their own, so a reload that is cut short by a weapon swap does not leave
    /// an arm halfway to the pouch.
    /// </summary>
    public float ReloadProgress
    {
        get
        {
            float? left = ReloadTimer.RemainingTime(Runner);
            if (left == null || reloadSeconds <= 0.01f) return 1f;
            return Mathf.Clamp01(1f - left.Value / reloadSeconds);
        }
    }

    [Tooltip("Melee skips the tracer and muzzle flash and reaches only a step.")]
    public bool melee;

    [Tooltip("Sweep thickness for melee, in metres.")]
    public float meleeRadius = 0.12f;

    [Tooltip("How close counts as point blank. A ray that starts inside a body " +
             "passes straight through it, so anything this near is resolved by " +
             "an overlap instead.")]
    public float pointBlankRadius = 0.25f;

    [Tooltip("Draw our own muzzle flash. Off when the weapon in view brings its " +
             "own, or every shot flashes twice.")]
    public bool drawMuzzleFlash = true;

    [Tooltip("What is left of the spread while aiming down the sights.")]
    [Range(0f, 1f)] public float aimingSpreadShare = 0.25f;

    /// <summary>Whether the sights are up, as the simulation sees it.</summary>
    [Networked] public NetworkBool IsAiming { get; set; }

    [Header("Recoil")]
    public float recoilPitch = 0.9f;
    public float recoilYaw = 0.35f;

    [Tooltip("Per-shot kick as (yaw, pitch) in degrees. A fixed sequence, like " +
             "CS: the same burst always walks the same way, so it can be learned " +
             "and pulled against. Empty falls back to the random kick above.")]
    public Vector2[] sprayPattern;

    [Tooltip("Idle time after which the spray starts from the first shot again.")]
    public float patternResetSeconds = 0.35f;

    private int patternIndex;
    private float lastShotTime = -99f;

    [Header("References")]
    public Transform shootPoint;
    public ImpactEffects effects;
    public WeaponBob viewmodel;

    [Header("Debug")]
    public bool drawDebugRay = true;

    private TickTimer fireTimer;
    private PlayerController playerController;
    private HitboxRoot ownHitboxes;
    private WeaponSwitcher switcher;
    private PlayerEffects consumables;

    // Reused so a shot does not allocate. Melee is the only path that needs it.
    private static readonly List<LagCompensatedHit> SweepHits = new List<LagCompensatedHit>();

    public override void Spawned()
    {
        playerController = GetComponentInParent<PlayerController>();
        ownHitboxes = GetComponentInParent<HitboxRoot>();
        switcher = GetComponentInParent<WeaponSwitcher>();
        consumables = GetComponentInParent<PlayerEffects>();

        ApplyWeapon();
        if (HasStateAuthority) Ammo = magazineSize;
    }

    public override void FixedUpdateNetwork()
    {
        if (!GetInput(out NetworkInputData data)) return;

        if (data.weapon > 0) TakeWeapon(data.weapon - 1);

        // A resimulated tick rolls the index back behind this component's
        // back, and the shot below has to use the numbers of that tick's gun.
        if (WeaponIndex != appliedIndex) ApplyWeapon();

        // Semi-automatic weapons only fire on the press, not while held.
        bool triggered = automatic ? data.fire : (data.fire && !firePressedLastTick);
        bool askedToReload = data.reload && !reloadPressedLastTick;

        firePressedLastTick = data.fire;
        reloadPressedLastTick = data.reload;

        // The sights follow the same input the shot is resolved from, so what
        // the player sees and what the spread does cannot disagree.
        IsAiming = data.aim;
        if (viewmodel != null && Runner.IsForward) viewmodel.SetAiming(data.aim);

        FinishReload();

        // Hands busy with a consumable: no shot and no reload. The press that
        // cancels it is honoured on the next tick.
        if (consumables != null && consumables.Taking != 0) return;
        if (playerController != null && playerController.NetInCar) return;

        // Pulling the trigger on an empty gun starts the reload by itself. On a
        // phone there is no spare thumb for the button, and every mobile shooter
        // does this.
        if (askedToReload || (triggered && UsesAmmo && Ammo <= 0)) BeginReload();

        if (IsReloading) return;

        if (triggered && fireTimer.ExpiredOrNotRunning(Runner))
        {
            if (UsesAmmo && Ammo <= 0) return;

            fireTimer = TickTimer.CreateFromSeconds(Runner, fireRate);
            if (UsesAmmo) Ammo--;
            Fire();
        }
    }

    private bool firePressedLastTick;
    private bool reloadPressedLastTick;

    private bool UsesAmmo => magazineSize > 0 && !melee;

    /// <summary>
    /// Puts another slot in hand, on the tick the pick arrives: the new gun
    /// comes up loaded and whatever the old one was doing stops.
    /// </summary>
    private void TakeWeapon(int index)
    {
        if (switcher == null || switcher.slots.Length == 0) return;

        index = Mathf.Clamp(index, 0, switcher.slots.Length - 1);
        if (index == WeaponIndex) return;

        WeaponIndex = index;
        ApplyWeapon();
        ReloadTimer = default;
        Ammo = magazineSize;
    }

    /// <summary>Loads the held slot's numbers: fire rate, damage, magazine.</summary>
    private void ApplyWeapon()
    {
        appliedIndex = WeaponIndex;
        if (switcher != null) switcher.ApplyStats(WeaponIndex);
    }

    /// <summary>Puts the melee slot in hand — the beer's doing, not the player's.</summary>
    public void DrawMelee()
    {
        if (switcher == null) return;

        for (int i = 0; i < switcher.slots.Length; i++)
        {
            if (!switcher.slots[i].melee) continue;
            TakeWeapon(i);
            return;
        }
    }

    /// <summary>
    /// Starts a magazine change, if there is anything to change: a full gun or a
    /// knife has nothing to do, and asking twice must not restart the timer or
    /// the animation stutters while the button is spammed.
    /// </summary>
    private void BeginReload()
    {
        if (!UsesAmmo || IsReloading || Ammo >= magazineSize) return;

        ReloadTimer = TickTimer.CreateFromSeconds(Runner, reloadSeconds);

        // The hands are local to whoever is looking through them, so the clip is
        // played on the forward pass only — a resimulated tick would restart it
        // several times a second.
        if (Runner.IsForward && viewmodel != null) viewmodel.TriggerReload(reloadSeconds);
    }

    /// <summary>
    /// Fills the magazine the tick the timer runs out. Reserve ammunition does
    /// not exist yet, so the pouch is bottomless; when it does, this is where it
    /// is deducted.
    /// </summary>
    private void FinishReload()
    {
        // A timer that was never started reports "not expired", so this needs no
        // flag of its own to tell "reloading" from "idle".
        if (!ReloadTimer.Expired(Runner)) return;

        ReloadTimer = default;
        Ammo = magazineSize;
    }

    private void Fire()
    {
        if (playerController == null || playerController.cameraHolder == null) return;

        Ray ray = AimRay();

        // Spread
        float spread = GetCurrentSpread();
        if (spread > 0f)
        {
            float yaw = Random.Range(-spread, spread);
            float pitch = Random.Range(-spread, spread);
            ray.direction = Quaternion.Euler(pitch, yaw, 0f) * ray.direction;
        }

        if (drawDebugRay)
            Debug.DrawRay(ray.origin, ray.direction * maxDistance, Color.red, 0.3f);

        // Effects only on the authoritative forward pass — resimulated ticks
        // would otherwise stack duplicate tracers, holes and kicks.
        bool forward = Runner.IsForward;
        bool showEffects = effects != null && forward && !melee;

        if (forward)
        {
            if (playerController != null)
            {
                var kick = NextRecoil();
                if (consumables != null) kick *= consumables.RecoilMultiplier;
                playerController.ApplyRecoil(kick.y, kick.x);
            }
            if (viewmodel != null)
            {
                if (melee)
                {
                    viewmodel.Slash();
                }
                else
                {
                    viewmodel.Kick();
                    viewmodel.TriggerFire();
                }
            }
            if (effects != null && !melee && drawMuzzleFlash) effects.PlayMuzzleFlash(shootPoint);
        }
        Vector3 muzzle = shootPoint != null ? shootPoint.position : ray.origin;

        bool connected = Query(ray, out LagCompensatedHit hit);

        if (connected)
        {
            if (drawDebugRay)
                Debug.DrawLine(ray.origin, hit.Point, Color.green, 0.3f);

            var playerHit = hit.Hitbox != null ? hit.Hitbox.GetComponent<PlayerHitbox>() : null;
            var target = hit.Collider != null ? hit.Collider.GetComponentInParent<Target>() : null;

            // The pack's pop-up targets listen for its projectiles hitting them.
            // Ours are hitscan, so the target is told directly; it drops and
            // stands itself back up.
            var popUp = hit.Collider != null ? hit.Collider.GetComponentInParent<TargetScript>() : null;
            if (popUp != null) popUp.isHit = true;

            // Passers-by are each client's own scenery: the shooter's client is the one that has this one.
            var passerBy = hit.Collider != null ? hit.Collider.GetComponentInParent<Pedestrian>() : null;
            if (passerBy != null && forward && HasInputAuthority)
            {
                bool alive = !passerBy.Dead;
                passerBy.Shot(damage, hit.Point, ray.direction, hit.Collider.attachedRigidbody);
                if (alive) Police.Report(Object.InputAuthority, passerBy.Dead);  // a crime; a kill is a worse one
            }
            var cop = hit.Collider != null ? hit.Collider.GetComponentInParent<PoliceCop>() : null;


            // Only the state authority decides who loses health. The shooter's
            // client already drew the tracer and the blood; it does not get to
            // decide the kill, or two clients would disagree about it.
            if (forward && HasStateAuthority)
            {
                if (playerHit != null && playerHit.health != null)
                    playerHit.health.ApplyDamage(damage, playerHit.isHead, Object.InputAuthority);
                else if (target != null)
                    target.TakeDamage(damage, hit.Point, ray.direction);
                else if (cop != null && Police.Instance != null)
                    Police.Instance.HitCop(cop.index, damage, Object.InputAuthority);
            }

            if (showEffects)
            {
                effects.PlayTracer(muzzle, hit.Point);

                // Flesh sprays; walls take a hole.
                bool flesh = playerHit != null || target != null || passerBy != null || cop != null;
                if (flesh) effects.PlayBlood(hit.Point, ray.direction);
                else if (hit.Collider != null)
                    effects.PlayImpact(hit.Point, hit.Normal, hit.Collider.transform);
            }
        }
        else if (showEffects)
        {
            effects.PlayTracer(muzzle, ray.origin + ray.direction * maxDistance);
        }
    }

    /// <summary>
    /// The line a shot leaves along: from the eyes, down the networked aim and
    /// its sway.
    ///
    /// Not the camera's own transform. That one is moved in Render — trailing
    /// the aim to smooth it, swung out behind the player for an emote — and a
    /// resimulated tick does not put it back, so the host and the shooter's
    /// client each cast from wherever their camera happened to be drawn.
    /// </summary>
    private Ray AimRay()
    {
        var sway = playerController.Sway(Runner.Tick * Runner.DeltaTime);
        var aim = Quaternion.Euler(playerController.NetPitch + sway.y,
                                   playerController.NetYaw + sway.x, 0f);
        return new Ray(playerController.cameraHolder.position, aim * Vector3.forward);
    }

    /// <summary>
    /// Resolves the shot against the world as the shooter saw it, not as the
    /// server sees it now. Without this a player on a phone at 120 ms would have
    /// to lead every moving target by most of a body width, which nobody does —
    /// they would simply conclude the game does not register hits.
    ///
    /// <c>IncludePhysX</c> makes the one query cover both the map (ordinary
    /// colliders) and players (Fusion hitboxes), so a wall in between still
    /// stops the bullet.
    /// </summary>
    private bool Query(Ray ray, out LagCompensatedHit hit)
    {
        // The muzzle sits inside the shooter's own hitboxes, so every shot would
        // otherwise land on themselves at zero distance.
        bool restore = false;
        if (ownHitboxes != null && ownHitboxes.HitboxRootActive)
        {
            ownHitboxes.HitboxRootActive = false;
            restore = true;
        }

        try
        {
            // Lag compensation is unavailable until the runner has a hitbox
            // manager. Without this guard a shot fired a tick too early throws
            // inside Fusion, and the exception is logged with a full stack every
            // tick the trigger is held — enough log traffic to stall the editor.
            if (Runner == null || Runner.LagCompensation == null)
                return PhysicsFallback(ray, out hit);

            bool connected = Runner.LagCompensation.Raycast(
                ray.origin, ray.direction, maxDistance, Object.InputAuthority,
                out hit, -1, HitOptions.IncludePhysX | HitOptions.SubtickAccuracy);

            // Hitboxes are not the only thing of ours in the way: the shot
            // starts inside our own character capsule, and pressed up against
            // somebody the query returns that capsule two centimetres out.
            // Every round of a burst then hit the shooter's own PlayerRoot and
            // the enemy took nothing.
            if (connected && IsOwn(hit.Collider)) connected = false;

            // A blade sweeps, it does not shoot. When the thin ray misses, a fat
            // overlap at arm's length forgives the aim and reads as a slash.
            //
            // A bullet needs the same escape for a different reason: pressed up
            // against somebody, the muzzle — and the camera the shot is cast
            // from — is inside their collider, and a ray that starts inside a
            // shape never reports hitting it. Whole magazines went into an enemy
            // at 0.2m without taking a point of health off them.
            //
            // The gun's version is deliberately short and thin: it fires only
            // when a ray found nothing at all for half a kilometre, which also
            // happens when shooting at the sky, and a generous sphere would
            // then award hits to whoever happened to be standing beside the
            // player.
            if (!connected)
            {
                connected = melee
                    ? Sweep(ray, meleeRadius, meleeRadius * 4f, out hit)
                    : Sweep(ray, pointBlankRadius, pointBlankRadius, out hit);
            }

            return connected;
        }
        finally
        {
            if (restore) ownHitboxes.HitboxRootActive = true;
        }
    }

    /// <summary>
    /// Plain physics, used when lag compensation is not up yet. Hits the map
    /// and the NPCs; networked players are missed, which is the right trade for
    /// a case that only lasts a few ticks.
    /// </summary>
    private bool PhysicsFallback(Ray ray, out LagCompensatedHit hit)
    {
        hit = default;

        bool connected = melee
            ? Physics.SphereCast(ray, meleeRadius, out RaycastHit raw, maxDistance)
            : Physics.Raycast(ray, out raw, maxDistance);

        if (connected && IsOwn(raw.collider)) connected = false;

        // Same point-blank hole as the lag-compensated path, same patch: sweep a
        // short fat cast when the thin one reports nothing.
        if (!connected && !melee)
            connected = Physics.SphereCast(ray, pointBlankRadius, out raw, pointBlankRadius * 2f);

        if (!connected) return false;

        hit.Point = raw.point;
        hit.Normal = raw.normal;
        hit.Distance = raw.distance;
        hit.Collider = raw.collider;
        return true;
    }

    private bool Sweep(Ray ray, float radius, float reach, out LagCompensatedHit hit)
    {
        hit = default;
        SweepHits.Clear();

        Vector3 centre = ray.origin + ray.direction * Mathf.Min(maxDistance, reach);
        int found = Runner.LagCompensation.OverlapSphere(
            centre, radius, Object.InputAuthority, SweepHits, -1,
            HitOptions.IncludePhysX);

        if (found <= 0) return false;

        // Nearest wins, so a wall right in front is not skipped for the player
        // standing behind it.
        float best = float.MaxValue;
        foreach (var candidate in SweepHits)
        {
            if (IsOwn(candidate.Collider)) continue;

            float d = (candidate.Point - ray.origin).sqrMagnitude;
            if (d >= best) continue;
            best = d;
            hit = candidate;
        }

        return best < float.MaxValue;
    }

    /// <summary>Whether a collider belongs to the player taking the shot.</summary>
    private bool IsOwn(Collider collider) =>
        collider != null && collider.transform.root == transform.root;

    /// <summary>
    /// Walks the spray pattern one step. Holding the trigger climbs and then
    /// drifts the same way every time; letting go long enough starts over.
    /// </summary>
    private Vector2 NextRecoil()
    {
        if (Time.time - lastShotTime > patternResetSeconds) patternIndex = 0;
        lastShotTime = Time.time;

        if (sprayPattern == null || sprayPattern.Length == 0)
            return new Vector2(Random.Range(-recoilYaw, recoilYaw), recoilPitch);

        var kick = sprayPattern[Mathf.Min(patternIndex, sprayPattern.Length - 1)];
        patternIndex++;
        return kick;
    }

    private float GetCurrentSpread()
    {
        float multiplier = consumables != null ? consumables.SpreadMultiplier : 1f;
        if (playerController == null) return baseSpread * multiplier;

        float spread = baseSpread;
        if (!playerController.IsGrounded) spread = jumpingSpread;
        else if (playerController.NetIsMoving) spread = movingSpread;

        // Down the sights the shot goes where it is pointed. Not to zero: a
        // rifle held by a standing man is never quite still, and a weapon with
        // no cone at all makes every other weapon feel broken.
        return (IsAiming ? spread * aimingSpreadShare : spread) * multiplier;
    }
}
