using UnityEngine;

/// <summary>
/// A shootable character target: patrols slowly, reacts to hits, drops when
/// killed and gets back up. Damage is applied by whoever fired, which is right
/// for single player; networked health comes later.
/// </summary>
public class Target : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;
    public float respawnSeconds = 5f;

    [Header("Patrol")]
    public float patrolRadius = 3.5f;
    public float patrolSpeed = 1.1f;
    public float turnSpeed = 6f;
    public float waitSeconds = 1.5f;

    [Header("Combat")]
    [Tooltip("How far they can see a player, in metres.")]
    public float sightRange = 8f;

    [Tooltip("How wide their attention is, in degrees across.")]
    public float fieldOfView = 140f;

    [Tooltip("Seconds between shots; each one is jittered around this.")]
    public float fireInterval = 1.1f;

    [Tooltip("Damage per shot that lands.")]
    public float damage = 7f;

    [Tooltip("Half-angle of their aim cone. They are not meant to be snipers.")]
    public float spreadDegrees = 4f;

    [Tooltip("Where their tracer and flash come from; the rifle's muzzle when " +
             "the character builder armed them.")]
    public Transform muzzle;

    [Tooltip("Shared with the player's own weapon, for the flash and tracers.")]
    public ImpactEffects effects;

    private float nextShot;
    private static float nextReport;
    private static float nextHitReport;

    [Header("Hit flash")]
    public Color hitTint = new Color(1f, 0.25f, 0.25f);
    public float flashSeconds = 0.1f;

    [Header("Headshots")]
    public bool popHeadOnDeath = true;
    public float headshotMultiplier = 3f;
    public float headPopForce = 3.5f;
    public float headDebrisSeconds = 6f;

    public float Health { get; private set; }
    public bool IsDown => Health <= 0f;

    private static readonly int HashSpeed = Animator.StringToHash("Speed");
    private static readonly int HashHit = Animator.StringToHash("Hit");
    private static readonly int HashDead = Animator.StringToHash("Dead");
    private static readonly int HashShoot = Animator.StringToHash("Shoot");
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    [Tooltip("Multiplies this one's kit colour, so a squad built from one model " +
             "is not a row of identical men. Set by the scene builder.")]
    public Color uniform = Color.white;

    private Animator animator;
    private Renderer[] renderers;
    private Color[] originalColors;
    private MaterialPropertyBlock block;

    private Vector3 home;
    private Vector3 destination;
    private float waitUntil;
    private float flashUntil;
    private float respawnAt;

    private float bodyRadius = 0.2f;
    private float bodyHeight = 0.6f;

    private Transform headBone;
    private Vector3 headBoneScale = Vector3.one;

    [Tooltip("How long the body takes to hit the ground.")]
    public float toppleSeconds = 0.55f;

    private Quaternion toppleFrom = Quaternion.identity;
    private Quaternion toppleTo = Quaternion.identity;
    private float toppleStarted;

    private void Awake()
    {
        // An Animator with no controller warns on every single SetFloat, and
        // with a handful of NPCs that is thousands of log lines a second — enough
        // to stall the editor. Take the one that can actually play something, and
        // otherwise drive nothing.
        animator = null;
        foreach (var candidate in GetComponentsInChildren<Animator>(true))
        {
            if (candidate.runtimeAnimatorController == null) continue;
            animator = candidate;
            break;
        }
        if (animator == null)
            Debug.LogWarning($"[Target] {name}: no animator with a controller — standing still.", this);

        renderers = GetComponentsInChildren<Renderer>(true);
        block = new MaterialPropertyBlock();

        originalColors = new Color[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var mat = renderers[i] != null ? renderers[i].sharedMaterial : null;
            var own = mat != null && mat.HasProperty(BaseColor)
                ? mat.GetColor(BaseColor)
                : Color.white;

            originalColors[i] = new Color(own.r * uniform.r, own.g * uniform.g,
                                          own.b * uniform.b, own.a);
        }

        // Applied once so the shade is there before anything is shot at: the
        // patrols share one model, and a yard full of the same man reads as a
        // placeholder.
        Tint(Color.white);

        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.Equals("Head", System.StringComparison.OrdinalIgnoreCase)) continue;
            headBone = t;
            headBoneScale = t.localScale;
            break;
        }

        var capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            bodyRadius = capsule.radius * 0.9f;
            bodyHeight = capsule.height;
        }

        // The muzzle of whatever they were handed, and the same effects pool the
        // player's own weapon uses — found here so the scene builder does not
        // have to wire six characters by hand.
        if (muzzle == null)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "SOCKET_Muzzle") continue;
                muzzle = t;
                break;
            }
        }

        if (effects == null) effects = FindFirstObjectByType<ImpactEffects>();

        Health = maxHealth;
        home = transform.position;
        PickDestination();
    }

    private void Update()
    {
        if (flashUntil > 0f && Time.time >= flashUntil)
        {
            flashUntil = 0f;
            Tint(Color.white);
        }

        if (IsDown)
        {
            float fall = Mathf.Clamp01((Time.time - toppleStarted) / toppleSeconds);
            transform.rotation = Quaternion.Slerp(toppleFrom, toppleTo, fall * fall);

            if (Time.time >= respawnAt) Respawn();
            return;
        }

        if (Engage()) return;

        Patrol();
    }

    /// <summary>
    /// Turns on whoever is in front of them and shoots.
    ///
    /// Returns true while there is someone to shoot at, so the patrol route is
    /// left alone until they are gone — a character that keeps strolling to its
    /// next waypoint mid-firefight reads as scenery, which is what these were.
    /// </summary>
    private bool Engage()
    {
        var quarry = Quarry();
        if (quarry == null) return false;

        Vector3 flat = quarry.position - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(flat.normalized), turnSpeed * Time.deltaTime);
        }

        if (animator != null) animator.SetFloat(HashSpeed, 0f, 0.1f, Time.deltaTime);

        if (Time.time < nextShot) return true;
        nextShot = Time.time + fireInterval * Random.Range(0.75f, 1.35f);

        if (Time.time > nextReport)
        {
            nextReport = Time.time + 2f;
            Debug.Log($"[NPC] {name} engaging at " +
                      $"{Vector3.Distance(transform.position, quarry.position):F1}m");
        }

        Shoot(quarry);
        if (animator != null) animator.SetTrigger(HashShoot);
        return true;
    }

    /// <summary>
    /// The nearest living player in sight: close enough, roughly in front, and
    /// with nothing solid in between. Re-found rather than cached, since the
    /// player respawns and the one it was watching may no longer exist.
    /// </summary>
    private Transform Quarry()
    {
        Transform best = null;
        float bestDistance = sightRange;

        foreach (var candidate in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (candidate == null || candidate.IsDead) continue;

            var body = Body(candidate);
            Vector3 to = body.position - transform.position;
            float distance = to.magnitude;
            if (distance > bestDistance) continue;

            // Behind them does not count; nor does through a shipping container.
            if (Vector3.Angle(transform.forward, to) > fieldOfView * 0.5f) continue;
            if (!CanSee(body)) continue;

            best = body;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// The part of a player that actually moves.
    ///
    /// PlayerHealth sits on the network object's root, which stays at the spawn
    /// point — the controller moves a child. Aiming at the root sent every shot
    /// into the scenery behind wherever the player first appeared, while the
    /// range check happily reported them two metres away.
    /// </summary>
    private static Transform Body(PlayerHealth player)
    {
        var controller = player.GetComponentInChildren<CharacterController>();
        return controller != null ? controller.transform : player.transform;
    }

    private bool CanSee(Transform who)
    {
        Vector3 eye = transform.position + Vector3.up * (bodyHeight * 0.85f);
        Vector3 at = who.position + Vector3.up * (bodyHeight * 0.7f);
        Vector3 to = at - eye;

        if (!Physics.Raycast(eye, to.normalized, out var hit, to.magnitude)) return true;

        // Our own capsule, and the target's, both count as clear line of sight.
        return hit.collider.transform.root == who.root ||
               hit.collider.transform.root == transform.root;
    }

    /// <summary>
    /// One shot at the player: a spread cone, a hit only if the ray lands on
    /// them, and the damage applied by whoever owns the simulation.
    /// </summary>
    private void Shoot(Transform at)
    {
        Vector3 eye = transform.position + Vector3.up * (bodyHeight * 0.85f);
        Vector3 aim = (at.position + Vector3.up * (bodyHeight * 0.7f)) - eye;

        // A cone in degrees, so distance makes them worse shots by itself.
        Quaternion wobble = Quaternion.Euler(
            Random.Range(-spreadDegrees, spreadDegrees),
            Random.Range(-spreadDegrees, spreadDegrees), 0f);
        Vector3 direction = wobble * aim.normalized;

        if (effects != null) effects.PlayMuzzleFlash(muzzle != null ? muzzle : transform);

        // Their own capsule surrounds the muzzle, so a plain raycast comes back
        // holding it and the shot goes nowhere. Take the nearest hit that is not
        // theirs instead.
        var hits = Physics.RaycastAll(eye, direction, sightRange * 1.5f);
        RaycastHit landed = default;
        float nearest = float.MaxValue;

        foreach (var candidate in hits)
        {
            if (candidate.collider.transform.root == transform.root) continue;
            if (candidate.distance >= nearest) continue;
            nearest = candidate.distance;
            landed = candidate;
        }

        if (nearest == float.MaxValue)
        {
            if (effects != null) effects.PlayTracer(eye, eye + direction * sightRange);
            return;
        }

        if (effects != null) effects.PlayTracer(eye, landed.point);

        var health = landed.collider.GetComponentInParent<PlayerHealth>();
        if (Time.time > nextHitReport)
        {
            nextHitReport = Time.time + 2f;
            Debug.Log($"[NPC] {name} shot landed on '{landed.collider.name}' at {nearest:F2}m, " +
                      $"player={(health != null ? "yes" : "no")}");
        }

        if (health == null || health.IsDead) return;

        var hitbox = landed.collider.GetComponent<PlayerHitbox>();
        health.ApplyDamage(damage, hitbox != null && hitbox.isHead, default);
    }

    private void Patrol()
    {
        if (Time.time < waitUntil)
        {
            if (animator != null) animator.SetFloat(HashSpeed, 0f, 0.1f, Time.deltaTime);
            return;
        }

        var flat = destination - transform.position;
        flat.y = 0f;

        if (flat.magnitude < 0.25f)
        {
            waitUntil = Time.time + waitSeconds;
            PickDestination();
            return;
        }

        var dir = flat.normalized;
        float step = patrolSpeed * Time.deltaTime;

        if (Blocked(dir, step))
        {
            // Turn away instead of walking into the wall.
            waitUntil = Time.time + waitSeconds * 0.5f;
            PickDestination();
            if (animator != null) animator.SetFloat(HashSpeed, 0f, 0.1f, Time.deltaTime);
            return;
        }

        transform.position += dir * step;
        transform.rotation = Quaternion.Slerp(
            transform.rotation, Quaternion.LookRotation(dir), turnSpeed * Time.deltaTime);

        if (animator != null) animator.SetFloat(HashSpeed, patrolSpeed, 0.1f, Time.deltaTime);
    }

    /// <summary>
    /// Sweeps a sphere at chest height along the next step. Walking the
    /// transform straight into geometry is what let these characters stroll
    /// through walls — nothing was ever checking.
    /// </summary>
    private bool Blocked(Vector3 direction, float step)
    {
        float radius = bodyRadius;
        var origin = transform.position + Vector3.up * (bodyHeight * 0.5f);

        var hits = Physics.SphereCastAll(origin, radius, direction,
                                         step + radius * 0.5f, ~0,
                                         QueryTriggerInteraction.Ignore);

        foreach (var hit in hits)
        {
            if (hit.collider == null) continue;
            if (hit.collider.transform.IsChildOf(transform)) continue;
            if (hit.collider.gameObject.name == "SafetyFloor") continue;

            // Other characters do not count. They patrol a few metres apart, so
            // treating each other as walls left every one of them standing
            // still — and a target that never moves never plays its walk.
            if (hit.collider.GetComponentInParent<Target>() != null) continue;

            return true;
        }
        return false;
    }

    private void PickDestination()
    {
        // Try a few spots and keep the first with a clear line from here.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var offset = Random.insideUnitCircle * patrolRadius;
            var candidate = home + new Vector3(offset.x, 0f, offset.y);

            var toward = candidate - transform.position;
            toward.y = 0f;
            if (toward.sqrMagnitude < 0.01f) continue;

            if (!Blocked(toward.normalized, toward.magnitude))
            {
                destination = candidate;
                return;
            }
        }

        // Everything is walled off — stay put this round.
        destination = transform.position;
    }

    public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection)
    {
        if (IsDown) return;

        // Anything landing in the top slice of the capsule counts as a head
        // hit — cheaper and steadier than a second collider on the head bone.
        bool headshot = hitPoint.y >= transform.position.y + bodyHeight * 0.8f;
        if (headshot) amount *= headshotMultiplier;

        Health -= amount;
        flashUntil = Time.time + flashSeconds;
        Tint(hitTint);

        if (Health <= 0f) Down(hitDirection, headshot);
        else if (animator != null) animator.SetTrigger(HashHit);
    }

    private void Down(Vector3 hitDirection, bool headshot)
    {
        Health = 0f;
        respawnAt = Time.time + respawnSeconds;
        Tint(new Color(0.45f, 0.45f, 0.45f));

        // Only a head hit takes the head off. A body kill just drops them.
        if (popHeadOnDeath && headshot) PopHead(hitDirection);

        // The fall is scripted rather than animated. The Death clip could not be
        // confirmed to move the skeleton, and a corpse left standing upright is
        // the one thing that has to read correctly every time.
        toppleFrom = transform.rotation;
        var away = hitDirection;
        away.y = 0f;
        if (away.sqrMagnitude < 0.001f) away = transform.forward;
        toppleTo = Quaternion.AngleAxis(82f, Vector3.Cross(Vector3.up, away.normalized)) * toppleFrom;
        toppleStarted = Time.time;

        if (animator != null)
        {
            animator.SetBool(HashDead, true);
            animator.SetFloat(HashSpeed, 0f);
        }
    }

    /// <summary>
    /// Takes the head off: the bone is scaled away so it vanishes from the
    /// skinned body, and a loose chunk is thrown in the direction of the shot.
    /// Scaling rather than deleting keeps the skeleton intact for the respawn.
    /// </summary>
    private void PopHead(Vector3 hitDirection)
    {
        if (headBone == null) return;

        var chunk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        chunk.name = "HeadDebris";
        chunk.transform.position = headBone.position;
        chunk.transform.localScale = Vector3.one * (bodyHeight * 0.17f);

        var renderer = chunk.GetComponent<Renderer>();
        if (renderers != null && renderers.Length > 0 && renderers[0] != null)
            renderer.sharedMaterial = renderers[0].sharedMaterial;

        var body = chunk.AddComponent<Rigidbody>();
        var push = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : transform.forward;
        body.linearVelocity = (push + Vector3.up * 1.2f) * headPopForce;
        body.angularVelocity = Random.insideUnitSphere * 12f;

        Destroy(chunk, headDebrisSeconds);

        headBone.localScale = Vector3.one * 0.001f;
    }

    private void Respawn()
    {
        Health = maxHealth;
        transform.position = home;
        waitUntil = 0f;
        PickDestination();

        if (headBone != null) headBone.localScale = headBoneScale;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        Tint(Color.white);

        if (animator != null) animator.SetBool(HashDead, false);
    }

    /// <summary>
    /// Multiplies each mesh's own colour rather than overwriting it. The model
    /// is built from several materials with different colours, so flashing
    /// them all to a flat white leaves the character permanently white once
    /// the flash ends.
    /// </summary>
    private void Tint(Color multiplier)
    {
        if (renderers == null || originalColors == null) return;

        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;

            var c = originalColors[i];
            block.SetColor(BaseColor, new Color(
                c.r * multiplier.r, c.g * multiplier.g, c.b * multiplier.b, c.a));
            r.SetPropertyBlock(block);
        }
    }
}
