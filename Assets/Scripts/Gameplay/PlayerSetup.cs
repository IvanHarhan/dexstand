using Fusion;
using UnityEngine;

/// <summary>
/// Local vs Remote player visibility.
///
/// Local player:  Arms ON, Camera ON, Body OFF
/// Remote player: Arms OFF, Camera OFF, Body ON
///
/// This is the KEY architectural difference from the old project:
/// - No bone rotation code
/// - Arms are a separate model (Generic rig) parented to CameraHolder
/// - They follow camera automatically via transform hierarchy
/// - Body is a separate model visible only to other players
/// </summary>
public class PlayerSetup : NetworkBehaviour
{
    [Header("First Person (Local)")]
    public GameObject arms;
    public GameObject cameraObject;

    [Header("Third Person (Remote)")]
    public GameObject body;
    public Animator bodyAnimator;

    [Tooltip("Mesh-only copies of the weapons in the body's right hand, one per WeaponSwitcher slot (null for " +
             "none). The pack draws the gun only for its owner; these are what everyone else sees.")]
    public GameObject[] bodyGuns = new GameObject[0];
    private int shownGun = -1;

    [Header("First Person Body")]
    [Tooltip("Keep your own body visible so you see legs and torso looking down.")]
    public bool showBodyToSelf = true;

    [Tooltip("Own body instead of the viewmodel: the arms you see are the arms " +
             "everyone else sees.")]
    public bool trueFirstPerson = true;

    [Tooltip("Bone shrunk away for the local player — the head sits inside the camera.")]
    public string headBoneName = "Head";

    private static readonly int HashSpeed = Animator.StringToHash("Speed");
    private static readonly int HashJump = Animator.StringToHash("Jump");
    private static readonly int HashReload = Animator.StringToHash("Reload");
    private static readonly int HashFire = Animator.StringToHash("Fire");
    private static readonly int HashAiming = Animator.StringToHash("Aiming");
    private static readonly int HashInteract = Animator.StringToHash("Interact");

    /// <summary>Jump count last seen, so each jump fires the trigger once.</summary>
    private int seenJumps = -1;
    private bool seenReloading;

    /// <summary>
    /// Rounds last seen. The shot animation is fired off the count dropping,
    /// which is the one signal that survives the network — the trigger the
    /// weapon pulls is local to whoever pulled it.
    /// </summary>
    private int seenAmmo = -1;

    /// <summary>Emote count last seen, so each emote is played once.</summary>
    private int seenEmotes = -1;

    private bool seenTaking;

    private PlayerController playerController;
    private WeaponController weapon;
    private EmoteController emotes;
    private PlayerEffects effects;

    // Resolved once and kept, so an emote can put the head back and the original
    // scale is not lost to the 0.001 we overwrite it with.
    private Transform headBone;
    private Vector3 headBoneScale = Vector3.one;

    /// <summary>
    /// True only for the owner of a body that hides its own head. Remote players
    /// keep their heads, and nothing here may take them away.
    /// </summary>
    private bool headIsOurs;

    private bool emoteView;

    /// <summary>
    /// What the owner sees of themselves while alive. One rule for both the
    /// emote view here and <see cref="PlayerHealth"/>, which redraws the pair
    /// every frame for death — with a rule of its own it put the arms back and
    /// took the body away in the middle of every emote.
    /// </summary>
    public bool OwnBodyShown => emoteView || showBodyToSelf || trueFirstPerson;
    public bool OwnArmsShown => !emoteView && !trueFirstPerson;

    public override void Spawned()
    {
        playerController = GetComponentInChildren<PlayerController>();
        weapon = GetComponentInChildren<WeaponController>(true);
        emotes = GetComponentInChildren<EmoteController>(true);
        effects = GetComponentInChildren<PlayerEffects>(true);

        if (Object.HasInputAuthority)
        {
            // LOCAL: first person view, optionally with your own body below you
            //
            // With trueFirstPerson the viewmodel is gone entirely: the camera
            // already sits at this body's head, so the arms in front of the lens
            // are the real ones. That is what makes a gesture visible to the
            // player and to everyone else at once — there is only one pair of
            // hands to animate, not two kept in step.
            SetActive(arms, !trueFirstPerson);
            SetActive(cameraObject, true);
            SetActive(body, showBodyToSelf || trueFirstPerson);

            if (showBodyToSelf || trueFirstPerson)
            {
                headIsOurs = true;
                SetHeadHidden(true);
            }

            Debug.Log($"[PlayerSetup] Local: trueFirstPerson={trueFirstPerson}, " +
                      $"arms='{(arms == null ? "NULL" : arms.name)}' active={arms != null && arms.activeSelf}, " +
                      $"body='{(body == null ? "NULL" : body.name)}' active={body != null && body.activeSelf}.");
        }
        else
        {
            // REMOTE: third person view
            SetActive(arms, false);
            SetActive(cameraObject, false);
            SetActive(body, true);
        }
    }

    public override void Render()
    {
        // Animate body for remote players (walk/idle)
        if (bodyAnimator != null && body != null && body.activeSelf && playerController != null)
        {
            bodyAnimator.SetFloat(HashSpeed, playerController.NetSpeed);

            // The first frame only records where the counter stands; firing the
            // trigger here would make every player jump the moment they spawn.
            if (seenJumps < 0) seenJumps = playerController.NetJumps;
            else if (playerController.NetJumps != seenJumps)
            {
                seenJumps = playerController.NetJumps;
                bodyAnimator.SetTrigger(HashJump);
            }
        }

        // The magazine change, on the same edge-detect principle as the jump but
        // off networked state that lasts whole seconds rather than a flag raised
        // inside one tick. Proxies never run the weapon's own reload code — they
        // get no input — so watching the replicated timer is the only way a
        // remote player is seen to reload at all.
        if (bodyAnimator != null && body != null && body.activeSelf && weapon != null)
        {
            bool reloading = weapon.IsReloading;
            if (reloading && !seenReloading) bodyAnimator.SetTrigger(HashReload);
            seenReloading = reloading;

            // The arms' own layer: a shot each time the ammunition drops, and
            // the sights held for as long as the player holds the button.
            bodyAnimator.SetBool(HashAiming, weapon.IsAiming);

            if (seenAmmo >= 0 && weapon.Ammo < seenAmmo) bodyAnimator.SetTrigger(HashFire);
            seenAmmo = weapon.Ammo;

            int held = weapon.WeaponIndex;
            if (held != shownGun)
            {
                shownGun = held;
                for (int i = 0; i < bodyGuns.Length; i++)
                    if (bodyGuns[i] != null) bodyGuns[i].SetActive(i == held);
            }
        }

        // Taking a consumable has no clip of its own; onlookers see the body's
        // reach-and-use gesture instead.
        if (bodyAnimator != null && body != null && body.activeSelf && effects != null)
        {
            bool taking = effects.Taking != 0;
            if (taking && !seenTaking) bodyAnimator.Play(HashInteract, 0, 0f);
            seenTaking = taking;
        }

        // Emotes, off a counter like the jump. Outside the checks above on
        // purpose: the owner's body may be hidden, and the owner still needs
        // the camera to swing out.
        if (playerController != null && emotes != null)
        {
            if (seenEmotes < 0) seenEmotes = playerController.NetEmotes;
            else if (playerController.NetEmotes != seenEmotes)
            {
                seenEmotes = playerController.NetEmotes;
                emotes.Play(playerController.NetEmote);
            }
        }
    }

    /// <summary>
    /// Swaps the local player between their own eyes and the emote camera.
    ///
    /// The rules live here rather than in EmoteController because there is more
    /// than one of them now. With trueFirstPerson the body never goes away and
    /// the viewmodel never comes back, and an emote that simply swapped the two
    /// left the player looking at an empty holder once the wave was over.
    /// </summary>
    public void SetEmoteView(bool emoting)
    {
        emoteView = emoting;
        SetActive(arms, OwnArmsShown);
        SetActive(body, OwnBodyShown);
        SetHeadHidden(!emoting);
    }

    /// <summary>
    /// Takes the local player's head away, or gives it back.
    ///
    /// Shrinks the head bone instead of disabling a renderer: the head shares a
    /// mesh with the rest of the body, so it cannot be hidden on its own. Scaling
    /// the bone collapses those vertices and leaves the rest intact — and it takes
    /// the hat with it, since that is skinned to the same bone.
    ///
    /// Emotes call this with false. The camera swings out behind the player for
    /// them, and until this existed the body waved back at you headless: the head
    /// was collapsed once at spawn and never restored.
    /// </summary>
    public void SetHeadHidden(bool hidden)
    {
        if (!headIsOurs || !ResolveHeadBone()) return;
        headBone.localScale = hidden ? Vector3.one * 0.001f : headBoneScale;
    }

    /// <summary>Finds the head bone once and remembers the scale it came with.</summary>
    private bool ResolveHeadBone()
    {
        if (headBone != null) return true;
        if (body == null || string.IsNullOrEmpty(headBoneName)) return false;

        foreach (var t in body.GetComponentsInChildren<Transform>(true))
        {
            // EndsWith, not Equals: Mixamo prefixes every bone with "mixamorig:",
            // so an exact match found nothing and the head stayed in front of
            // the camera. "HeadTop_End" is not caught by this, which is right.
            if (!t.name.EndsWith(headBoneName, System.StringComparison.OrdinalIgnoreCase)) continue;
            headBone = t;
            headBoneScale = t.localScale;
            return true;
        }

        Debug.LogWarning($"[PlayerSetup] No bone ending in '{headBoneName}' under {body.name} — " +
                         "the head will sit in front of the camera.");
        return false;
    }

    private void SetActive(GameObject go, bool active)
    {
        if (go != null) go.SetActive(active);
    }
}
