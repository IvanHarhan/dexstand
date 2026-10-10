using InfimaGames.LowPolyShooterPack;
using UnityEngine;

/// <summary>
/// Makes the player's own body carry the weapon, which is what the first person
/// view is made of once the viewmodel is gone.
///
/// Three things the clips cannot do:
///
/// The downloaded rifle idle is a low ready — the fist rests at the waist, three
/// hand-widths below the bottom of a 60° frame at this stature, so nothing of
/// the player was in shot at all. The gun hand is therefore solved onto a rest
/// point carried in front of the lens instead, which is where every viewmodel in
/// this project was framed.
///
/// The clips never close the off hand on anything, so it is solved onto a grip
/// point the weapon carries.
///
/// And they were authored level with the horizon while the camera pitches on a
/// holder of its own, so the spine is bent by the difference — the arms are
/// pinned to the lens either way, but without this the shoulders stay square to
/// the ground and the arms pull out of the torso.
/// </summary>
[DefaultExecutionOrder(1000)]
public class BodyRig : MonoBehaviour
{
    [Header("Aim")]
    [Tooltip("The transform that carries the camera's pitch.")]
    public Transform cameraHolder;

    [Tooltip("Lowest spine bone first. Each takes its share of the pitch.")]
    public Transform[] spine;

    [Tooltip("How much of the pitch each spine bone takes, in the same order.")]
    public float[] spineShare;

    [Range(0f, 1f)]
    public float aimWeight = 1f;

    [Tooltip("Degrees the chest is turned away from the view, the way a shooter " +
             "stands: it brings the support shoulder forward, and the off arm is " +
             "barely long enough to reach the handguard without it.")]
    public float torsoTwist = 30f;

    [Tooltip("Degrees the chest leans into the shot. It carries both shoulders " +
             "forward, which is what lets the weapon be held far enough from the " +
             "lens to be seen whole rather than filling the frame.")]
    public float torsoLean = 15f;

    [Header("Hands")]
    [Tooltip("Infima's two-bone solver. Its right target is the rest point under " +
             "the camera; its left target is the proxy below.")]
    public CharacterKinematics kinematics;

    [Tooltip("Moved onto the held weapon's grip point every frame, so switching " +
             "weapons needs no rewiring.")]
    public Transform offHandTarget;

    public WeaponSwitcher switcher;

    [Tooltip("Finger bones of the support hand, which the clips leave open. The " +
             "firing hand is closed in the rifle idle itself and is left alone.")]
    public Transform[] offHandFingers;

    [Tooltip("The thumb, which closes over the handguard from the other side and " +
             "by less than the fingers do.")]
    public Transform[] offHandThumb;

    [Range(0f, 90f)]
    public float fingerCurl = 68f;

    [Range(0f, 90f)]
    public float thumbCurl = 30f;

    [Tooltip("Child of the weapon that says where the off hand belongs. A weapon " +
             "without one is held in a single hand and that arm is left alone.")]
    public string gripName = "LeftGrip";

    [Tooltip("Watched for the magazine change, which is the one thing the off " +
             "hand has to be let go of for.")]
    public WeaponController weapon;

    [Tooltip("Seconds to hand the arms over to a clip and to take them back.")]
    public float releaseSeconds = 0.12f;

    [Tooltip("Pull the firing hand onto the rest point in front of the lens. Off " +
             "when the clip itself holds the weapon up — then only the support " +
             "hand is solved, onto the handguard.")]
    public bool solveGunHand = true;

    /// <summary>
    /// How much of the hold the solver still has: 1 on the weapon, 0 left to
    /// whatever the animator is playing.
    /// </summary>
    private float armWeight = 1f;

    [Tooltip("While an emote plays the body is the thing being watched, and the " +
             "clip has to own every bone — a hand pinned to the gun cannot wave.")]
    public EmoteController emotes;


    private void LateUpdate()
    {
        if (emotes != null && emotes.IsEmoting) return;

        // A magazine change is an animation now, not a hand walked along a
        // curve by this script. The solver has to let go of both arms for it, or
        // the clip plays into wrists that are pinned in front of the lens and
        // none of it is visible — which is what made the old reload look broken.
        bool reloading = weapon != null && weapon.IsReloading;
        armWeight = Mathf.MoveTowards(armWeight, reloading ? 0f : 1f,
                                      Time.deltaTime / Mathf.Max(0.01f, releaseSeconds));

        Aim();

        if (kinematics == null || armWeight <= 0.001f) return;

        // The gun hand first. The off hand is solved onto the weapon and the
        // weapon hangs off the gun hand, so the other way round it reaches for a
        // grip that has not moved yet and trails a frame behind.
        if (solveGunHand) kinematics.Compute(0f, armWeight);

        HoldForegrip();
        CloseOffHand();
    }

    /// <summary>
    /// Curls the support hand round the handguard.
    ///
    /// The downloaded rifle idle poses the firing hand and leaves the other one
    /// flat — palm along the barrel, fingers straight out — so it is bent here
    /// instead. Applied on top of whatever the animator wrote this frame, which
    /// is what keeps a reload or an emote from being overwritten mid-motion:
    /// those states own the bones and this runs after them, once.
    /// </summary>
    public void CloseOffHand()
    {
        Curl(offHandFingers, fingerCurl);
        Curl(offHandThumb, thumbCurl);
    }

    private static void Curl(Transform[] bones, float degrees)
    {
        if (bones == null || Mathf.Abs(degrees) < 0.01f) return;

        var bend = Quaternion.Euler(degrees, 0f, 0f);
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] != null) bones[i].localRotation *= bend;
    }

    /// <summary>
    /// Bends the spine by the camera's pitch, parent bone first so each one's
    /// share adds to what it inherited.
    ///
    /// The angle is read off the holder's transform rather than off
    /// PlayerController, so the body matches the picture on screen even when the
    /// network's render pass has not run yet this frame.
    /// </summary>
    private void Aim()
    {
        if (cameraHolder == null || spine == null || spine.Length == 0) return;

        float pitch = Mathf.DeltaAngle(0f, cameraHolder.localEulerAngles.x) * aimWeight;

        Bend(spine, spineShare, pitch + torsoLean, transform.right);
        Bend(spine, spineShare, torsoTwist, transform.up);
    }

    /// <summary>
    /// Spreads one turn down a chain of bones, parent first, so each one's share
    /// adds to what it inherited.
    ///
    /// Shared with the builder, which has to pose the body exactly as the game
    /// will before it measures anything off it: a weapon placed against an
    /// untwisted chest is a weapon the twisted one no longer holds.
    /// </summary>
    public static void Bend(Transform[] spine, float[] share, float degrees, Vector3 axis)
    {
        if (spine == null || Mathf.Abs(degrees) < 0.01f) return;

        for (int i = 0; i < spine.Length; i++)
        {
            if (spine[i] == null) continue;

            float part = share != null && i < share.Length ? share[i] : 1f / spine.Length;
            spine[i].rotation = Quaternion.AngleAxis(degrees * part, axis) * spine[i].rotation;
        }
    }

    private void HoldForegrip()
    {
        if (offHandTarget == null) return;

        var grip = CurrentGrip();
        if (grip == null) return;

        offHandTarget.SetPositionAndRotation(grip.position, grip.rotation);
        kinematics.Compute(armWeight, 0f);
    }

    /// <summary>
    /// The grip point of whichever weapon is out. Resolved each time rather than
    /// cached: the switcher swaps models at runtime, and a cached transform would
    /// belong to the gun that was put away.
    /// </summary>
    private Transform CurrentGrip()
    {
        var model = CurrentModel();
        return model != null ? model.transform.Find(gripName) : null;
    }

    private GameObject CurrentModel()
    {
        if (switcher == null) return null;

        int index = switcher.CurrentIndex;
        if (index < 0 || index >= switcher.slots.Length) return null;

        return switcher.slots[index].model;
    }
}
