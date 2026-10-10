using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The on-screen button that holds a radial wheel open: press, drag, let go.
/// It reports to MobileInputService like every other touch control, so the
/// wheel itself only ever reads the InputRouter.
/// </summary>
public class WheelButton : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] private MobileInputService inputService;

    public RadialWheel.Kind wheel = RadialWheel.Kind.Weapons;

    private Vector2 pressedAt;
    private bool pressed;

    public void OnPointerDown(PointerEventData e)
    {
        if (inputService == null) return;

        pressed = true;
        pressedAt = e.position;
        inputService.OnWheelDown((int)wheel);
    }

    public void OnDrag(PointerEventData e)
    {
        if (pressed && inputService != null) inputService.OnWheelDrag(e.position - pressedAt);
    }

    public void OnPointerUp(PointerEventData e) => Release();

    // Hidden mid-press — a menu opening — must not leave the wheel held.
    private void OnDisable() => Release();

    private void Release()
    {
        if (!pressed) return;

        pressed = false;
        if (inputService != null) inputService.OnWheelUp((int)wheel);
    }
}
