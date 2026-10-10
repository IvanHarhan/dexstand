using Fusion;
using UnityEngine;

/// <summary>
/// Player health, death and respawn.
///
/// Lives on the prefab root next to <see cref="PlayerSetup"/>, because the thing
/// that dies is the player, not the body: the body gets hidden and put back,
/// the object itself survives the whole match.
///
/// Every number here is owned by the state authority. Damage is only ever
/// applied under it and clients read the networked value, so two clients can
/// never disagree about who is still alive. Regeneration is server side for the
/// same reason — a locally predicted heal would fight the authoritative value
/// and make the bar jitter.
/// </summary>
public class PlayerHealth : NetworkBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;

    [Tooltip("Damage multiplier for a hit on a head hitbox.")]
    public float headshotMultiplier = 2f;

    [Header("Regeneration")]
    [Tooltip("Quiet seconds after the last hit before health starts coming back.")]
    public float regenDelay = 5f;

    [Tooltip("Health per second once regeneration starts.")]
    public float regenPerSecond = 25f;

    [Header("Death")]
    public float respawnSeconds = 3f;

    [Networked] public float Health { get; set; }
    [Networked] public NetworkBool IsDead { get; set; }

    /// <summary>
    /// Counts up on every death. Remote clients watch it rather than
    /// <see cref="IsDead"/> so two deaths in a row still read as two events.
    /// </summary>
    [Networked] public int DeathCount { get; set; }

    [Networked] private TickTimer RegenBlocked { get; set; }
    [Networked] private TickTimer RespawnAt { get; set; }

    /// <summary>
    /// Scales incoming damage. This is the hook the consumables hang off — the
    /// shot itself does not know the target is on a stim, and should not.
    /// </summary>
    [HideInInspector] public float incomingDamageMultiplier = 1f;

    public float Fraction => maxHealth <= 0f ? 0f : Mathf.Clamp01(Health / maxHealth);

    private PlayerController controller;
    private PlayerSetup setup;

    public override void Spawned()
    {
        controller = GetComponentInChildren<PlayerController>(true);
        setup = GetComponentInChildren<PlayerSetup>(true);

        if (HasStateAuthority)
        {
            Health = maxHealth;
            IsDead = false;
        }
    }

    /// <summary>
    /// Applies damage from a shot. Called on the state authority only — the
    /// shooter's client predicts the tracer and the hole, never the kill.
    /// </summary>
    public void ApplyDamage(float amount, bool headshot, PlayerRef source)
    {
        if (!HasStateAuthority || IsDead || amount <= 0f) return;

        // Shooting yourself is not a mechanic. It happens anyway, because the
        // muzzle sits inside your own hitbox for the first few centimetres.
        if (source == Object.InputAuthority) return;

        if (headshot) amount *= headshotMultiplier;
        amount *= incomingDamageMultiplier;

        Health -= amount;
        RegenBlocked = TickTimer.CreateFromSeconds(Runner, regenDelay);

        if (Health <= 0f) Die();
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        if (IsDead)
        {
            if (RespawnAt.Expired(Runner)) Respawn();
            return;
        }

        if (Health < maxHealth && RegenBlocked.ExpiredOrNotRunning(Runner))
            Health = Mathf.Min(maxHealth, Health + regenPerSecond * Runner.DeltaTime);
    }

    private void Die()
    {
        Health = 0f;
        IsDead = true;
        DeathCount++;
        RespawnAt = TickTimer.CreateFromSeconds(Runner, respawnSeconds);
    }

    private void Respawn()
    {
        Health = maxHealth;
        IsDead = false;
        RegenBlocked = TickTimer.None;
        RespawnAt = TickTimer.None;

        if (controller != null) controller.Teleport(FindSpawnPosition());
    }

    /// <summary>
    /// Reuses the launcher's floor probe so a respawn lands exactly where a
    /// fresh join would. Falling back to the current position is better than
    /// dropping the player through the map when the launcher is gone.
    /// </summary>
    private Vector3 FindSpawnPosition()
    {
        var launcher = FindFirstObjectByType<NetworkLauncher>();
        if (launcher != null) return launcher.GetSpawnPosition();

        return controller != null ? controller.transform.position : transform.position;
    }

    /// <summary>
    /// Death and respawn as the player sees them. Driven off the networked flag
    /// rather than the death event, so a client that joins mid-death still shows
    /// the right thing.
    /// </summary>
    public override void Render()
    {
        bool inCar = controller != null && controller.NetInCar;
        bool shouldShowBody = !IsDead && !inCar &&
                              (!Object.HasInputAuthority || (setup != null && setup.OwnBodyShown));

        if (setup != null && setup.body != null && setup.body.activeSelf != shouldShowBody)
            setup.body.SetActive(shouldShowBody);

        // The arms are the local player's own view of themselves — they go away
        // while dead, and the camera is left looking at the world.
        if (setup != null && setup.arms != null && Object.HasInputAuthority)
        {
            bool shouldShowArms = !IsDead && !inCar && setup.OwnArmsShown;
            if (setup.arms.activeSelf != shouldShowArms) setup.arms.SetActive(shouldShowArms);
        }
    }
}
