using UnityEngine;

/// <summary>
/// Holds the loadout and swaps between weapons: one model visible at a time,
/// with its stats pushed into the shared <see cref="WeaponController"/>.
///
/// Which weapon is held is networked (<see cref="WeaponController.WeaponIndex"/>).
/// A pick only asks for one; the model follows the networked index, on the
/// holder's screen and on everyone else's.
/// </summary>
public class WeaponSwitcher : MonoBehaviour
{
    [System.Serializable]
    public class Slot
    {
        public string displayName = "Weapon";
        public GameObject model;

        [Header("Behaviour")]
        public float fireRate = 0.1f;
        public float damage = 25f;
        public float range = 500f;
        public bool automatic = true;
        public bool melee;

        [Tooltip("Sweep thickness for melee, in metres.")]
        public float meleeRadius = 0.12f;

        [Header("Magazine")]
        [Tooltip("Rounds per magazine; 0 for a weapon that never runs out.")]
        public int magazineSize = 30;

        [Tooltip("Seconds to change the magazine.")]
        public float reloadSeconds = 2.2f;

        [Tooltip("Where this weapon is carried, as a shift of the hold point in " +
                 "the camera's own axes. A handgun is presented higher and closer " +
                 "to the middle than a rifle; left on the rifle's point it hangs " +
                 "off in the corner, held out at arm's length by one hand.")]
        public Vector3 holdOffset;

        [Tooltip("Where the hold point goes while aiming: measured at build time " +
                 "so this weapon's own sight line lands on the middle of the " +
                 "screen, rather than guessed at per weapon.")]
        public Vector3 aimOffset;

        [Tooltip("The turn that takes it off the carry angle and onto the view " +
                 "axis, in degrees.")]
        public Vector3 aimTilt;

        [Header("Feel")]
        public float recoilPitch = 0.9f;
        public float recoilYaw = 0.35f;
        public float baseSpread = 1f;

        [Tooltip("Per-shot (yaw, pitch) kick. Empty means a random kick.")]
        public Vector2[] sprayPattern;
    }

    public Slot[] slots = new Slot[0];

    [SerializeField] private WeaponController weapon;

    [Tooltip("The hold point the hands are solved onto — the bob and the recoil " +
             "live on it. Each weapon shifts it by its own offset.")]
    public WeaponBob viewmodel;

    [Tooltip("Seconds to lower the old weapon and to raise the new one.")]
    public float holsterSeconds = 0.14f;
    public float drawSeconds = 0.22f;

    private Coroutine swapping;
    private InputRouter router;

    /// <summary>The slot on its way up, or already up.</summary>
    private int shown = -1;

    public int CurrentIndex { get; private set; } = -1;

    public string CurrentName =>
        CurrentIndex >= 0 && CurrentIndex < slots.Length ? slots[CurrentIndex].displayName : "-";

    private void Start()
    {
        if (slots.Length > 0) Show(0);
    }

    /// <summary>
    /// Asks for a weapon. Nothing changes yet: the pick goes out as input, the
    /// simulation moves the networked index, and <see cref="Update"/> brings
    /// the model up once it has.
    /// </summary>
    public void Select(int index)
    {
        if (slots.Length == 0) return;

        if (router == null) router = FindFirstObjectByType<InputRouter>();
        if (router != null) router.PickWeapon(Mathf.Clamp(index, 0, slots.Length - 1));
    }

    private void Update()
    {
        // Networked state is unreadable until the player has been spawned.
        if (weapon == null || weapon.Object == null || !weapon.Object.IsValid) return;

        int held = weapon.WeaponIndex;
        if (held != shown && held >= 0 && held < slots.Length) Show(held);
    }

    private void Show(int index)
    {
        shown = index;

        // The first pick, and anything outside play mode, happens instantly:
        // there is nothing on screen yet to lower.
        if (viewmodel == null || CurrentIndex < 0 || !Application.isPlaying)
        {
            Equip(index);
            return;
        }

        if (swapping != null) StopCoroutine(swapping);
        swapping = StartCoroutine(Swap(index));
    }

    /// <summary>
    /// Puts one weapon away and brings the next one up, rather than swapping the
    /// model between two frames with the hands never moving.
    /// </summary>
    private System.Collections.IEnumerator Swap(int index)
    {
        viewmodel.Holster(holsterSeconds);
        yield return new WaitForSeconds(holsterSeconds);

        Equip(index);

        viewmodel.Draw(drawSeconds);
        swapping = null;
    }

    private void Equip(int index)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].model != null) slots[i].model.SetActive(i == index);
        }

        CurrentIndex = index;
        Apply(slots[index]);
    }

    public void Next()
    {
        if (slots.Length == 0) return;
        Select((CurrentIndex + 1) % slots.Length);
    }

    /// <summary>
    /// Re-points the weapon at this model's muzzle too — each gun has its own
    /// barrel, and tracers from the wrong one look like they miss the gun.
    /// </summary>
    private void Apply(Slot slot)
    {
        // The hold point moves before anything else: the hands are solved onto
        // it, and the weapon hangs off the hand, so this carries the whole
        // assembly. It is the one part that works without a WeaponController.
        if (viewmodel != null)
        {
            viewmodel.holdOffset = slot.holdOffset;
            viewmodel.aimOffset = slot.aimOffset;
            viewmodel.aimTilt = slot.aimTilt;
        }

        if (weapon == null || slot.model == null) return;

        var muzzle = slot.model.transform.Find("Muzzle");
        if (muzzle != null) weapon.shootPoint = muzzle;
    }

    /// <summary>
    /// Loads a slot's numbers into the weapon. Called by the weapon from the
    /// simulation, on the tick the slot changes, rather than from here when
    /// the model comes up: the model is only a picture, and the host has to
    /// shoot with the same numbers as the client that pulled the trigger.
    /// </summary>
    public void ApplyStats(int index)
    {
        if (weapon == null || index < 0 || index >= slots.Length) return;
        var slot = slots[index];

        weapon.fireRate = slot.fireRate;
        weapon.damage = slot.damage;
        weapon.maxDistance = slot.range;
        weapon.automatic = slot.automatic;
        weapon.melee = slot.melee;
        weapon.recoilPitch = slot.recoilPitch;
        weapon.recoilYaw = slot.recoilYaw;
        weapon.baseSpread = slot.baseSpread;
        weapon.meleeRadius = slot.meleeRadius;
        weapon.sprayPattern = slot.sprayPattern;
        weapon.magazineSize = slot.magazineSize;
        weapon.reloadSeconds = slot.reloadSeconds;
    }
}
