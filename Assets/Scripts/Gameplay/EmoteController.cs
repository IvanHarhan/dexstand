using UnityEngine;

/// <summary>
/// Fortnite-style emotes: the camera swings out behind the player, the body
/// appears in place of the viewmodel, and a clip from the character's own set
/// plays. Everything returns to first person when it finishes.
///
/// The choice travels as network input and comes back through
/// <see cref="PlayerController.NetEmotes"/>, which every peer watches, so the
/// clip plays for everyone. Only the camera is the owner's alone.
/// </summary>
public class EmoteController : MonoBehaviour
{
    [SerializeField] private PlayerSetup setup;
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Animator bodyAnimator;

    [Tooltip("State names in the body controller, played in turn.")]
    public string[] emotes = { "Wave", "Roll", "Punch_Left", "Kick_Right" };

    [Tooltip("What the wheel calls each emote, in the same order.")]
    public string[] labels = { "Привет", "Кувырок", "Удар", "Пинок" };

    [System.Serializable]
    public struct DanceStep
    {
        public string state;
        public float speed;
        public float seconds;

        public DanceStep(string state, float speed, float seconds)
        {
            this.state = state;
            this.speed = speed;
            this.seconds = seconds;
        }
    }

    [Tooltip("The drunk dance: emote states strung together, each at its own speed.")]
    public DanceStep[] dance =
    {
        new DanceStep("Wave", 0.7f, 1.3f),
        new DanceStep("Roll", 1.4f, 0.8f),
        new DanceStep("Kick_Right", 0.8f, 1.2f),
        new DanceStep("Wave", 1.3f, 0.8f),
    };

    public float emoteSeconds = 2.6f;

    [Tooltip("Camera offset while emoting, in metres — scaled to the player.")]
    public Vector3 thirdPersonOffset = new Vector3(0.22f, 0.18f, -0.85f);

    [Tooltip("Downward tilt while emoting, so the whole body is framed.")]
    public float cameraPitch = 12f;

    [Tooltip("How wide the camera is treated as being when it looks for walls.")]
    public float cameraRadius = 0.06f;

    [Tooltip("Gap kept between the camera and whatever it backed into.")]
    public float wallPadding = 0.02f;

    public float blendSpeed = 7f;

    public bool IsEmoting { get; private set; }

    private Vector3 restCameraPosition;
    private float emoteUntil;
    private int next;
    private InputRouter router;

    /// <summary>One line per emote, not one per frame.</summary>
    private bool reportedClamp;

    private void Awake()
    {
        if (cameraTransform != null) restCameraPosition = cameraTransform.localPosition;
    }

    /// <summary>
    /// Asks for the next emote in the list, one per press. Nothing plays yet:
    /// <see cref="Play"/> runs when the choice comes back from the network.
    /// </summary>
    public void PlayNext()
    {
        if (emotes == null || emotes.Length == 0) return;

        if (router == null) router = FindFirstObjectByType<InputRouter>();
        if (router == null) return;

        router.PickEmote(next % emotes.Length);
        next++;
    }

    /// <summary>
    /// Plays an emote on this body; every peer calls it for every player. The
    /// camera and the view swap are for the owner only — on a remote copy
    /// SetEmoteView would switch the arms on afterwards, and the pack's arms on
    /// a remote copy bind themselves to the wrong player.
    /// </summary>
    public void Play(int index)
    {
        if (emotes == null || index < 0 || index >= emotes.Length || bodyAnimator == null) return;

        StopDance();

        // The view goes first: in first person the owner's body is switched
        // off, and an animator on an inactive object drops a Play.
        if (IsOwner)
        {
            emoteUntil = Time.time + emoteSeconds;
            reportedClamp = false;
            SetEmoting(true);
        }

        bodyAnimator.Play(emotes[index], 0, 0f);
    }

    public bool IsDancing => dancing != null;

    private Coroutine dancing;

    /// <summary>
    /// The beer's drunk dance. Every peer starts it on the same moment, worked
    /// out from the dose (see <see cref="PlayerEffects"/>); the owner watches
    /// it from the emote camera.
    /// </summary>
    public void PlayDance()
    {
        if (bodyAnimator == null || dance == null || dance.Length == 0) return;

        StopDance();
        dancing = StartCoroutine(Dance());
    }

    /// <summary>Breaks the dance off — moving or shooting does it.</summary>
    public void StopDance()
    {
        if (dancing == null) return;

        StopCoroutine(dancing);
        dancing = null;
        bodyAnimator.speed = 1f;
        if (IsOwner) SetEmoting(false);
    }

    private System.Collections.IEnumerator Dance()
    {
        float total = 0f;
        foreach (var step in dance) total += step.seconds;

        if (IsOwner)
        {
            emoteUntil = Time.time + total;
            reportedClamp = false;
            SetEmoting(true);
        }

        // The speed is the whole animator's, walking included, so it goes back
        // to 1 on every way out of here.
        foreach (var step in dance)
        {
            bodyAnimator.speed = step.speed;
            bodyAnimator.Play(step.state, 0, 0f);
            yield return new WaitForSeconds(step.seconds);
        }

        bodyAnimator.speed = 1f;
        dancing = null;
    }

    private void OnDisable()
    {
        if (bodyAnimator != null) bodyAnimator.speed = 1f;
        dancing = null;
    }

    private bool IsOwner => setup != null && setup.Object != null && setup.Object.HasInputAuthority;

    private void Update()
    {
        if (IsEmoting && Time.time >= emoteUntil) SetEmoting(false);
    }

    private void LateUpdate()
    {
        if (cameraTransform == null) return;

        var wanted = IsEmoting ? PullOutOfWalls(restCameraPosition + thirdPersonOffset)
                               : restCameraPosition;
        cameraTransform.localPosition = Vector3.Lerp(
            cameraTransform.localPosition, wanted, blendSpeed * Time.deltaTime);

        var wantedRotation = IsEmoting
            ? Quaternion.Euler(cameraPitch, 0f, 0f)
            : Quaternion.identity;
        cameraTransform.localRotation = Quaternion.Slerp(
            cameraTransform.localRotation, wantedRotation, blendSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Keeps the swung-out camera outside the scenery.
    ///
    /// The emote offset walks the camera a metre backwards with no regard for
    /// what is behind the player, and this map is a yard full of crates — stand
    /// next to one, emote, and the lens ends up inside it looking at the back
    /// faces of its planks. So the move is swept rather than teleported: a
    /// sphere the size of the lens is cast from the head to where the camera
    /// wants to be, and it stops short of the first thing it touches.
    ///
    /// The player's own colliders are skipped by hand instead of by layer —
    /// nothing in this project is layered yet, and the capsule sits right on
    /// top of the point the cast starts from, so it would swallow every sweep.
    /// </summary>
    private Vector3 PullOutOfWalls(Vector3 wantedLocal)
    {
        var parent = cameraTransform.parent;
        if (parent == null) return wantedLocal;

        Vector3 pivot = parent.TransformPoint(restCameraPosition);
        Vector3 target = parent.TransformPoint(wantedLocal);
        Vector3 travel = target - pivot;

        float distance = travel.magnitude;
        if (distance < 1e-4f) return wantedLocal;

        Vector3 direction = travel / distance;
        float reached = distance;

        var hits = Physics.SphereCastAll(pivot, cameraRadius, direction, distance,
                                         ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            // A sphere that already overlaps something at the start reports a
            // zero distance and a zero normal; taking it at its word would pin
            // the camera to the back of the player's head.
            if (hit.distance <= 0f) continue;
            if (hit.transform.IsChildOf(transform)) continue;
            if (hit.distance < reached) reached = hit.distance;
        }

        if (reached >= distance) return wantedLocal;

        if (!reportedClamp)
        {
            reportedClamp = true;
            Debug.Log($"[Emote] Camera held at {reached:F2}m of {distance:F2}m — something behind the player.");
        }

        float allowed = Mathf.Max(reached - wallPadding, 0f);
        return parent.InverseTransformPoint(pivot + direction * allowed);
    }

    /// <summary>
    /// Swaps which self the player sees. The body is normally hidden from its
    /// owner because it sits inside the camera; during an emote it is the whole
    /// point, and the arms would hang in mid-air.
    ///
    /// The head comes back too. It is collapsed for the owner so it does not fill
    /// the lens in first person, but the emote camera is a metre behind the
    /// player, and from there a headless body is the only thing you look at.
    /// </summary>
    private void SetEmoting(bool on)
    {
        IsEmoting = on;
        if (setup != null) setup.SetEmoteView(on);
    }
}
