using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Desktop input: WASD + mouse look + left click to fire + space to jump.
/// Uses new Input System.
/// </summary>
public class DesktopInputService : MonoBehaviour, IInputService
{
    [Header("Mouse")]
    public float mouseSensitivity = 0.3f;

    public Vector2 MoveInput => moveInput;
    public Vector2 LookInput => lookDelta;
    public bool FireHeld => fireHeld;
    public bool AimHeld => aimHeld;
    /// <summary>
    /// Held until read — see MobileInputService for why a one-frame flag is not
    /// enough.
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

    [Tooltip("Mouse travel kept while a wheel is open, in pixels. Capped so that " +
             "turning to another slice never means first undoing a long sweep.")]
    public float wheelReach = 150f;

    private int wheelHeld;
    private Vector2 wheelDrag;

    private Vector2 moveInput;
    private Vector2 lookDelta;
    private bool fireHeld;
    private bool aimHeld;
    private bool jumpQueued, jumpRead;
    private bool reloadQueued, reloadRead;

    private Keyboard kb;
    private Mouse mouse;
    private GameMenu menu;

    /// <summary>
    /// A match is running and no menu is up. The cursor is only ever captured
    /// then: capturing it on every click, as this used to, hid it the moment the
    /// player clicked anything in the main menu or on the pause screen.
    /// </summary>
    private bool InGame => (menu == null || (!menu.InMenu && !menu.IsPaused)) && !MapMinimap.FullMapOpen;

    private void Start()
    {
        menu = FindFirstObjectByType<GameMenu>();
        if (InGame) Capture(true);
    }

    /// <summary>
    /// Switched off when the player picks phone controls. The router keeps
    /// reading this component regardless, so whatever it last held — a W key, the
    /// trigger — would otherwise stay pressed forever.
    /// </summary>
    private void OnDisable()
    {
        moveInput = Vector2.zero;
        lookDelta = Vector2.zero;
        fireHeld = false;
        aimHeld = false;
        jumpQueued = jumpRead = false;
        reloadQueued = reloadRead = false;
        wheelHeld = 0;
        wheelDrag = Vector2.zero;
    }

    private static void Capture(bool captured)
    {
        Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !captured;
    }

    private void Update()
    {
        kb = Keyboard.current;
        mouse = Mouse.current;
        if (kb == null || mouse == null) return;
        if (MapMinimap.FullMapOpen)
        {
            // the full map has the mouse: no walking, looking or shooting under it
            moveInput = lookDelta = Vector2.zero;
            fireHeld = aimHeld = false;
            return;
        }

        // WASD
        float x = 0f, y = 0f;
        if (kb.wKey.isPressed) y += 1f;
        if (kb.sKey.isPressed) y -= 1f;
        if (kb.dKey.isPressed) x += 1f;
        if (kb.aKey.isPressed) x -= 1f;
        moveInput = new Vector2(x, y).normalized;

        // Mouse look
        Vector2 mouseDelta = mouse.delta.ReadValue();
        lookDelta = mouseDelta * mouseSensitivity;

        // Radial wheels, held like the thumb on a phone: Q weapons, E
        // consumables, B emotes. While one is open the mouse picks a slice
        // and the camera stays put.
        // At the wheel Q and E are the indicators (SimpleCar).
        int held = SimpleCar.DrivingHere ? 0
                 : kb.qKey.isPressed ? (int)RadialWheel.Kind.Weapons
                 : kb.eKey.isPressed ? (int)RadialWheel.Kind.Items
                 : kb.bKey.isPressed ? (int)RadialWheel.Kind.Emotes
                 : 0;
        if (held != wheelHeld)
        {
            wheelHeld = held;
            wheelDrag = Vector2.zero;
        }
        if (wheelHeld != 0)
        {
            wheelDrag = Vector2.ClampMagnitude(wheelDrag + mouseDelta, wheelReach);
            lookDelta = Vector2.zero;
        }

        // Fire (LMB), sights (RMB) — where every shooter on a mouse puts them.
        fireHeld = mouse.leftButton.isPressed;
        aimHeld = mouse.rightButton.isPressed;

        // Jump
        if (kb.spaceKey.wasPressedThisFrame) jumpQueued = true;

        // Reload — R, where every shooter puts it.
        if (kb.rKey.wasPressedThisFrame) reloadQueued = true;

        // Menus and pause own the cursor; GameMenu frees it when they open.
        if (menu == null) menu = FindFirstObjectByType<GameMenu>();
        if (!InGame) return;

        // Escape frees the cursor (and opens the pause screen); a click in the
        // running game takes it back.
        if (kb.escapeKey.wasPressedThisFrame) Capture(false);
        else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            Capture(true);
    }

    public void ResetOneShots()
    {
        lookDelta = Vector2.zero;

        if (jumpRead) { jumpQueued = false; jumpRead = false; }
        if (reloadRead) { reloadQueued = false; reloadRead = false; }
    }
}
