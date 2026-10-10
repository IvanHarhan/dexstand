using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// One arrow of the on-screen direction pad. Holding it walks that way; it is
/// the simpler alternative to dragging the analogue stick.
/// </summary>
public class DPadButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public enum PadTarget
    {
        /// <summary>Walks the player.</summary>
        Move,
        /// <summary>Turns the camera.</summary>
        Look,
    }

    [Tooltip("(0,1) forward, (0,-1) back, (-1,0) left, (1,0) right.")]
    public Vector2 direction = Vector2.up;

    public PadTarget target = PadTarget.Move;

    [SerializeField] private MobileInputService inputService;

    private bool held;

    public void OnPointerDown(PointerEventData e)
    {
        if (inputService == null || held) return;
        held = true;
        Send(true);
    }

    public void OnPointerUp(PointerEventData e) => Release();

    // A finger that slides off the button counts as letting go.
    public void OnPointerExit(PointerEventData e) => Release();

    private void OnDisable() => Release();

    private void Release()
    {
        if (inputService == null || !held) return;
        held = false;
        Send(false);
    }

    private void Send(bool pressed)
    {
        if (target == PadTarget.Look) inputService.OnLookDirection(direction, pressed);
        else inputService.OnDirection(direction, pressed);
    }
}
