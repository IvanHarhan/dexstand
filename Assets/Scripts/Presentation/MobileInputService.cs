using UnityEngine;

/// <summary>
/// Mobile input: virtual joystick (left) + look zone (right) + fire/jump buttons.
/// Implements IInputService for platform-agnostic input reading.
/// </summary>
public class MobileInputService : MonoBehaviour, IInputService
{
    [Header("Joystick")]
    [SerializeField] private DynamicJoystick moveJoystick;

    [Header("Look")]
    [SerializeField] private float lookSensitivity = 0.2f;

    /// <summary>
    /// The stick wins when it is being dragged; otherwise the direction pads
    /// drive movement, so both control styles can sit on screen at once.
    /// </summary>
    public Vector2 MoveInput
    {
        get
        {
            var stick = moveJoystick != null ? moveJoystick.inputVector : Vector2.zero;
            if (stick.sqrMagnitude > 0.0001f) return stick;
            return Vector2.ClampMagnitude(dpad, 1f);
        }
    }
    [Header("Look pad")]
    [Tooltip("Degrees of turn per network tick while a look arrow is held.")]
    [SerializeField] private float lookPadStep = 1.5f;

    /// <summary>Drag on the look zone plus whatever the look arrows are adding.</summary>
    public Vector2 LookInput => pendingLook + lookPad * lookPadStep;
    public bool FireHeld => fireHeld;
    public bool AimHeld => aimHeld;
    /// <summary>
    /// A tap waits until it has actually been read.
    ///
    /// The simulation only looks at the input when it runs a tick, and a heavy
    /// frame — the playtest writing a 4 MB screenshot, say — can leave a quarter
    /// of a second between one look and the next. A flag cleared on a timer, or
    /// at the end of the frame it was pressed in, falls into that gap and the
    /// jump simply never happens.
    /// </summary>
    public bool JumpPressed
    {
        get
        {
            if (jumpQueued) jumpRead = true;
            return jumpQueued;
        }
    }

    public bool ReloadPressed
    {
        get
        {
            if (reloadQueued) reloadRead = true;
            return reloadQueued;
        }
    }

    public int WheelHeld => wheelHeld;
    public Vector2 WheelDrag => wheelDrag;

    private int wheelHeld;
    private Vector2 wheelDrag;

    private Vector2 pendingLook;
    private Vector2 dpad;
    private Vector2 lookPad;
    private bool fireHeld;
    private bool aimHeld;
    private bool jumpQueued, jumpRead;
    private bool reloadQueued, reloadRead;

    // Called by the direction pads. Held directions add up, so pressing
    // forward and right together walks diagonally.
    public void OnDirection(Vector2 direction, bool pressed)
    {
        dpad += pressed ? direction : -direction;

        // Guard against a button losing its release event, e.g. when a finger
        // slides off it — otherwise the player walks forever.
        if (Mathf.Abs(dpad.x) > 2f || Mathf.Abs(dpad.y) > 2f) dpad = Vector2.zero;
    }

    // Called by the look arrows. Held like the movement pad, but feeds the
    // camera instead of the legs.
    public void OnLookDirection(Vector2 direction, bool pressed)
    {
        lookPad += pressed ? direction : -direction;
        if (Mathf.Abs(lookPad.x) > 2f || Mathf.Abs(lookPad.y) > 2f) lookPad = Vector2.zero;
    }

    public void ClearDirections()
    {
        dpad = Vector2.zero;
        lookPad = Vector2.zero;
    }

    // Called by LookZone UI
    public void OnLookDelta(Vector2 delta)
    {
        pendingLook = delta * lookSensitivity;
    }

    // Called by Fire button
    public void OnFireDown() => fireHeld = true;
    public void OnFireUp() => fireHeld = false;

    // Called by the Aim button, which is held rather than tapped.
    public void OnAimDown() => aimHeld = true;
    public void OnAimUp() => aimHeld = false;

    // Called by Jump button
    public void OnJump() => jumpQueued = true;

    // Called by the Reload button
    public void OnReload() => reloadQueued = true;

    // Called by the wheel buttons: held, dragged, let go.
    public void OnWheelDown(int wheel)
    {
        wheelHeld = wheel;
        wheelDrag = Vector2.zero;
    }

    public void OnWheelDrag(Vector2 travel) => wheelDrag = travel;

    public void OnWheelUp(int wheel)
    {
        if (wheelHeld == wheel) wheelHeld = 0;
    }

    /// <summary>
    /// Clears the look delta, and a tap only once it has been read at least
    /// once — see <see cref="JumpPressed"/>.
    /// </summary>
    public void ResetOneShots()
    {
        pendingLook = Vector2.zero;

        if (jumpRead) { jumpQueued = false; jumpRead = false; }
        if (reloadRead) { reloadQueued = false; reloadRead = false; }
    }
}
