using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Owns the on-screen controls and whether they show at all. On desktop the
/// touch look-zone fights the real mouse, so the two input styles are never on
/// together. The player can switch between them from the menu; the choice is
/// remembered.
/// </summary>
public class MobileUIVisibility : MonoBehaviour
{
    public enum ControlMode
    {
        /// <summary>Touch on phones, keyboard and mouse everywhere else.</summary>
        Auto,
        /// <summary>On-screen stick and buttons, drivable by mouse in the editor.</summary>
        ForceTouch,
        /// <summary>Keyboard and mouse only, controls hidden.</summary>
        ForceDesktop,
    }

    private const string ModeKey = "Dexstand.ControlMode";

    [Tooltip("Auto = touch on phone, keys on PC, until the player picks one in the menu.")]
    public ControlMode mode = ControlMode.Auto;

    [SerializeField] private GameObject touchControls;
    [SerializeField] private DesktopInputService desktopInput;

    [Tooltip("Captions of the menu buttons that switch the controls.")]
    [SerializeField] private Text[] labels;

    private bool useTouch;

    private void Start()
    {
        mode = (ControlMode)PlayerPrefs.GetInt(ModeKey, (int)mode);
        Apply();
    }

    /// <summary>Switches between the on-screen controls and keyboard and mouse.</summary>
    public void Toggle()
    {
        mode = useTouch ? ControlMode.ForceDesktop : ControlMode.ForceTouch;
        PlayerPrefs.SetInt(ModeKey, (int)mode);
        Apply();
    }

    private void Apply()
    {
        switch (mode)
        {
            case ControlMode.ForceTouch: useTouch = true; break;
            case ControlMode.ForceDesktop: useTouch = false; break;

            // The platform, not Input.touchSupported: a Windows laptop with a
            // touch screen reports touch support and would lose its keyboard.
            default: useTouch = Application.isMobilePlatform; break;
        }

        if (touchControls != null) touchControls.SetActive(useTouch);

        if (desktopInput != null)
        {
            desktopInput.enabled = !useTouch;

            // Nothing grabs the cursor in touch mode, so hand it back.
            if (useTouch)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        if (labels != null)
        {
            foreach (var label in labels)
                if (label != null) label.text = useTouch ? "Управление: телефон" : "Управление: ПК";
        }
    }
}
