using System.Collections;
using InfimaGames.LowPolyShooterPack;
using UnityEngine;

/// <summary>
/// The Low Poly Shooter Pack's own first-person arms, driven by this game.
///
/// The pack's arms, weapons and clips were made together, and its hands only
/// sit on a weapon because its Character script runs the IK every frame and
/// answers the animation events. Taking the clips without that script is what
/// left hands floating beside the gun before. This stands in for Character: it
/// keeps the pack's animator, inventory and IK exactly as the pack uses them,
/// and takes its orders from our WeaponController and WeaponSwitcher instead of
/// the pack's own input and movement.
///
/// Shooting, damage and ammunition stay ours. The pack only animates.
/// </summary>
public class PackViewmodel : CharacterBehaviour
{
    [Header("Pack")]
    [SerializeField] private InventoryBehaviour inventory;
    [SerializeField] private Animator characterAnimator;
    [SerializeField] private Camera cameraWorld;

    [Header("Game")]
    public WeaponController weapon;
    public WeaponSwitcher switcher;
    public PlayerController player;

    [Tooltip("Smoothing on the walk blend and on the sights, as in the pack.")]
    public float dampTimeLocomotion = 0.15f;
    public float dampTimeAiming = 0.3f;

    [Tooltip("Longest wait for the holster clip to report back before the next " +
             "weapon comes up anyway.")]
    public float holsterTimeout = 1f;

    [Header("Effects")]
    [Tooltip("The pack's casings, rifle and handgun. Spawned at the weapon's own " +
             "scale, since the pack's prefabs are sized for a 1.8 m world.")]
    public GameObject casingRifle;
    public GameObject casingHandgun;

    [Tooltip("Footstep loops from the pack, played while the player moves.")]
    public AudioClip stepsWalking;
    public AudioSource stepsSource;

    /// <summary>The drinking clip's layer and state, added to the pack's controller by DexstandConsumeBuilder.</summary>
    public const string ConsumeLayer = "Layer Consume";
    public const string ConsumeBeerState = "Consume Beer";
    private const string ConsumeSource = "Consume/A_FP_PCH_Consume_Beer";
    private const float ConsumeClipSeconds = 3.7f;
    private const float ConsumeFade = 0.2f;

    private static readonly int HashMovement = Animator.StringToHash("Movement");
    private static readonly int HashAimingAlpha = Animator.StringToHash("Aiming");

    private CharacterKinematics kinematics;
    private WeaponBehaviour equipped;
    private MagazineBehaviour magazine;

    private int layerHolster, layerActions, layerOverlay, layerConsume = -1;
    private float consumeWeight;
    private bool drinking;
    private GameObject bottle;
    private Renderer[] hiddenWeapon;

    private bool holstered, holstering, aiming, inspecting, lowered;
    private PlayerEffects effects;
    private MuzzleBehaviour muzzle;
    private readonly System.Collections.Generic.HashSet<WeaponBehaviour> scaledEffects =
        new System.Collections.Generic.HashSet<WeaponBehaviour>();
    private int shownSlot = -2;
    private int seenAmmo = -1;
    private bool seenReloading;

    protected override void Awake()
    {
        kinematics = GetComponent<CharacterKinematics>();
        effects = GetComponentInParent<PlayerEffects>();
        inventory.Init();
        Refresh();
    }

    protected override void Start()
    {
        layerHolster = characterAnimator.GetLayerIndex("Layer Holster");
        layerActions = characterAnimator.GetLayerIndex("Layer Actions");
        layerOverlay = characterAnimator.GetLayerIndex("Layer Overlay");
        layerConsume = characterAnimator.GetLayerIndex(ConsumeLayer);
    }

    protected override void Update()
    {
        // Networked state is unreadable until the player has been spawned.
        bool live = weapon != null && weapon.Object != null && weapon.Object.IsValid;

        if (switcher != null && switcher.CurrentIndex != shownSlot)
        {
            bool first = shownSlot == -2;
            shownSlot = switcher.CurrentIndex;
            StopAllCoroutines();
            StartCoroutine(Show(shownSlot, first));
        }

        if (live)
        {
            aiming = weapon.IsAiming && !holstered;

            if (!weapon.melee && seenAmmo >= 0 && weapon.Ammo < seenAmmo) Fire();
            seenAmmo = weapon.Ammo;

            if (weapon.IsReloading && !seenReloading) Reload();
            seenReloading = weapon.IsReloading;

            // The weapon goes down out of the way while an item is taken; the
            // beer then has a clip of its own on top.
            bool busy = effects != null && effects.Taking != 0;
            if (busy != lowered) Lower(busy);
            Drink();
        }
        else if (drinking) StopDrinking();

        consumeWeight = Mathf.MoveTowards(consumeWeight, drinking ? 1f : 0f, Time.deltaTime / ConsumeFade);
        if (layerConsume >= 0) characterAnimator.SetLayerWeight(layerConsume, consumeWeight);

        // Unreadable once the player is gone too — leaving a match despawns it
        // a frame before these arms go.
        float speed = live && player != null ? player.NetSpeed / Mathf.Max(0.01f, player.walkSpeed) : 0f;
        characterAnimator.SetFloat(HashMovement, Mathf.Clamp01(speed), dampTimeLocomotion, Time.deltaTime);
        Footsteps(speed);
        characterAnimator.SetFloat(HashAimingAlpha, aiming ? 1f : 0f, 0.25f * dampTimeAiming, Time.deltaTime);
        characterAnimator.SetBool("Aim", aiming);
        characterAnimator.SetBool("Running", false);
    }

    protected override void LateUpdate()
    {
        // The pack's hands are put on the weapon here, after the animator, every
        // frame. This is the part that was missing when only its clips were used.
        // Not while drinking: it would pull the hand off the bottle and back onto the gun.
        if (equipped != null && kinematics != null && consumeWeight <= 0f) kinematics.Compute();
    }

    /// <summary>
    /// The drinking clip, timed to end with the take: it starts once the weapon
    /// has had the rest of the time to go down. Cancelling (moving, shooting)
    /// clears Taking, and the arms fade back into the Unholster.
    /// </summary>
    private void Drink()
    {
        if (effects == null) return;
        bool beer = effects.Taking == (byte)Consumable.Beer && layerConsume >= 0;
        bool due = beer && effects.TakeProgress >= 1f - ConsumeClipSeconds / Mathf.Max(ConsumeClipSeconds, effects.beerTakeSeconds);
        if (due && !drinking) StartDrinking();
        else if (!beer && drinking) StopDrinking();
    }

    private void StartDrinking()
    {
        drinking = true;
        characterAnimator.Play(ConsumeBeerState, layerConsume, 0f);

        // The gun rides the right hand, which is about to carry the bottle.
        hiddenWeapon = equipped != null
            ? System.Array.FindAll(equipped.GetComponentsInChildren<Renderer>(), r => r.enabled)
            : new Renderer[0];
        foreach (var r in hiddenWeapon) r.enabled = false;

        bottle = SpawnBottle();
    }

    private void StopDrinking()
    {
        drinking = false;
        if (bottle != null) Destroy(bottle);
        bottle = null;
        if (hiddenWeapon != null)
            foreach (var r in hiddenWeapon) if (r != null) r.enabled = true;
        hiddenWeapon = null;
    }

    /// <summary>
    /// The beer in the right hand, where it was put in Blender: the exported
    /// clip carries the bottle under its hand_r, and the item model's own bottle
    /// is lined up onto that.
    /// </summary>
    private GameObject SpawnBottle()
    {
        var hand = Bone(transform, "hand_r");
        var source = Resources.Load<GameObject>(ConsumeSource);
        var prefab = Resources.Load<GameObject>("Items/beer/beer");
        if (hand == null || source == null || prefab == null) return null;

        var sourceHand = Bone(source.transform, "hand_r");
        var sourceBottle = Bone(source.transform, "GARRAFA");
        var sourceArm = Bone(source.transform, "lowerarm_r");
        var arm = Bone(transform, "lowerarm_r");
        if (sourceHand == null || sourceBottle == null) return null;

        var go = Instantiate(prefab, hand, false);
        var modelBottle = Bone(go.transform, "GARRAFA");
        if (modelBottle == null)
        {
            Destroy(go);
            return null;
        }

        // Blender's units against the pack's, read off a bone's length.
        float unit = sourceArm != null && arm != null && sourceArm.localPosition.sqrMagnitude > 1e-12f
            ? arm.localPosition.magnitude / sourceArm.localPosition.magnitude : 1f;
        Matrix4x4 inHand = Matrix4x4.Scale(Vector3.one * unit) * sourceHand.worldToLocalMatrix * sourceBottle.localToWorldMatrix;
        Matrix4x4 inModel = go.transform.worldToLocalMatrix * modelBottle.localToWorldMatrix;
        Matrix4x4 local = inHand * inModel.inverse;
        go.transform.localPosition = local.GetColumn(3);
        go.transform.localRotation = local.rotation;
        go.transform.localScale = local.lossyScale;

        // The model's own green comes out acid under the grade; bottle glass is darker.
        PlayerEffects.PrepareHeld(go, "beer", new Color(0.04f, 0.14f, 0.05f), true);
        return go;
    }

    private static Transform Bone(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private void Fire()
    {
        // Firing cuts an inspect short, and the clip that would have reported
        // its end never gets there.
        inspecting = false;
        characterAnimator.CrossFade("Fire", 0.05f, layerOverlay, 0);

        var gun = equipped != null ? equipped.GetAnimator() : null;
        if (gun != null) gun.Play("Fire", 0, 0f);

        // The pack's own flash — particles and a light at the muzzle.
        if (muzzle != null) muzzle.Effect();
    }

    /// <summary>
    /// Turns the weapon over for a look, with the pack's authored clip. Ignored
    /// while the arms are busy: an inspect started mid-reload fights the reload
    /// for the same layer and both come out broken.
    /// </summary>
    public void Inspect()
    {
        if (holstered || holstering || inspecting) return;
        if (weapon != null && weapon.Object != null && weapon.Object.IsValid && weapon.IsReloading) return;

        inspecting = true;
        characterAnimator.CrossFade("Inspect", 0f, layerActions, 0);
    }

    private void Footsteps(float speed)
    {
        if (stepsSource == null || stepsWalking == null) return;

        bool walking = speed > 0.1f && (player == null || player.IsGrounded) && !holstered;
        if (walking && !stepsSource.isPlaying)
        {
            stepsSource.clip = stepsWalking;
            stepsSource.loop = true;
            stepsSource.Play();
        }
        else if (!walking && stepsSource.isPlaying)
        {
            stepsSource.Stop();
        }
    }

    private void Reload()
    {
        inspecting = false;
        string state = weapon.Ammo > 0 ? "Reload" : "Reload Empty";
        characterAnimator.Play(state, layerActions, 0f);

        var gun = equipped != null ? equipped.GetAnimator() : null;
        if (gun != null) gun.Play(state, 0, 0f);
    }

    /// <summary>
    /// Puts the current weapon away and brings up the pack weapon matching the
    /// slot, the way the pack's own Character does it. A slot the pack has no
    /// weapon for — the knife — leaves the arms holstered.
    /// </summary>
    private IEnumerator Show(int slot, bool first)
    {
        int index = PackIndex(slot);
        inspecting = false;

        if (!first && !holstered)
        {
            SetHolstered(holstering = true);
            float until = Time.time + holsterTimeout;
            yield return new WaitUntil(() => !holstering || Time.time > until);
            holstering = false;
        }

        if (index < 0)
        {
            SetHolstered(true);
            yield break;
        }

        SetHolstered(false);
        if (!first) characterAnimator.Play("Unholster", layerHolster, 0f);

        inventory.Equip(index);
        Refresh();
    }

    /// <summary>
    /// Which of the pack's weapons stands in for a slot of ours, matched on the
    /// name: the pack's inventory holds an assault rifle and a handgun.
    /// </summary>
    private int PackIndex(int slot)
    {
        if (switcher == null || slot < 0 || slot >= switcher.slots.Length) return -1;

        string wanted = switcher.slots[slot].displayName.ToLowerInvariant();
        string needle = wanted.Contains("pistol") ? "handgun"
                      : wanted.Contains("ak") || wanted.Contains("rifle") ? "_ar_"
                      : null;
        if (needle == null) return -1;

        var weapons = inventory.GetComponentsInChildren<WeaponBehaviour>(true);
        for (int i = 0; i < weapons.Length; i++)
            if (weapons[i].name.ToLowerInvariant().Contains(needle)) return i;

        return -1;
    }

    private void Refresh()
    {
        equipped = inventory.GetEquipped();
        if (equipped == null) return;

        characterAnimator.runtimeAnimatorController = equipped.GetAnimatorController();

        var attachments = equipped.GetAttachmentManager();
        magazine = attachments != null ? attachments.GetEquippedMagazine() : null;

        // Tracers leave from the barrel the player is looking at, not from the
        // hidden body's hand.
        muzzle = attachments != null ? attachments.GetEquippedMuzzle() : null;
        if (weapon != null && muzzle != null) weapon.shootPoint = muzzle.GetSocket();

        ScaleEffects(equipped);
    }

    /// <summary>
    /// Shrinks the muzzle flash to the arms' size. The pack builds it when the
    /// weapon wakes, as particles and a light, both sized for a 1.8 m world: the
    /// particles ignore the scale of what they hang off unless told otherwise,
    /// and a light's range never follows scale at all, so at this stature the
    /// flash filled the screen and lit the yard.
    /// </summary>
    private void ScaleEffects(WeaponBehaviour gun)
    {
        if (gun == null || !scaledEffects.Add(gun)) return;

        float scale = gun.transform.lossyScale.x;

        foreach (var ps in gun.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }

        foreach (var light in gun.GetComponentsInChildren<Light>(true))
            light.range *= scale;
    }

    /// <summary>
    /// Puts the weapon down for a consumable and brings it back. A slot the
    /// pack has no weapon for is down already, and a swap under way sets the
    /// pose itself when it lands.
    /// </summary>
    private void Lower(bool value)
    {
        lowered = value;
        if (PackIndex(shownSlot) < 0 || holstering) return;

        inspecting = false;
        SetHolstered(value);
        if (!value) characterAnimator.Play("Unholster", layerHolster, 0f);
    }

    private void SetHolstered(bool value)
    {
        holstered = value;
        characterAnimator.SetBool("Holstered", value);
    }

    // --- What the pack's weapons, sounds and animation events ask of a character.

    public override Camera GetCameraWorld() => cameraWorld;
    public override InventoryBehaviour GetInventory() => inventory;
    public override bool IsCrosshairVisible() => !aiming && !holstered;
    public override bool IsRunning() => false;
    public override bool IsAiming() => aiming;
    public override bool IsCursorLocked() => Cursor.lockState == CursorLockMode.Locked;
    public override bool IsTutorialTextVisible() => false;
    public override Vector2 GetInputMovement() => Vector2.zero;
    public override Vector2 GetInputLook() => Vector2.zero;

    /// <summary>
    /// Throws a casing from the ejection port. The pack's weapon would spawn it
    /// at the pack's full size, three times too big here, so it is spawned from
    /// this side at the weapon's own scale — and made heavier by the same
    /// factor, since the casing kicks itself out with a fixed force on waking
    /// and at a third of the size it would otherwise fly three times as far.
    /// </summary>
    public override void EjectCasing()
    {
        if (equipped == null) return;

        bool handgun = equipped.name.ToLowerInvariant().Contains("handgun");
        var prefab = handgun ? casingHandgun : casingRifle;
        if (prefab == null) return;

        Transform port = null;
        foreach (var t in equipped.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "SOCKET_Eject") continue;
            port = t;
            break;
        }
        if (port == null) return;

        float scale = equipped.transform.lossyScale.x;
        var casing = Instantiate(prefab, port.position, port.rotation);
        casing.transform.localScale *= scale;

        var body = casing.GetComponent<Rigidbody>();
        if (body != null && scale > 0.0001f) body.mass /= scale;
    }

    // The magazine count is ours; the pack's weapon keeps its own and is never
    // fired, so there is nothing to refill.
    public override void FillAmmunition(int amount) { }

    public override void SetActiveMagazine(int active)
    {
        if (magazine != null) magazine.gameObject.SetActive(active != 0);
    }

    public override void AnimationEndedReload() { }
    public override void AnimationEndedInspect() => inspecting = false;
    public override void AnimationEndedHolster() => holstering = false;
}
