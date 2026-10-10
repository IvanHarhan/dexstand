using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Hold to bring the sights up. A hold rather than a toggle, so it matches the
/// right mouse button on a desktop and the thumb never has to remember what
/// state it left the weapon in.
/// </summary>
public class AimButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [SerializeField] private MobileInputService inputService;

    public void OnPointerDown(PointerEventData e)
    {
        if (inputService != null) inputService.OnAimDown();
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (inputService != null) inputService.OnAimUp();
    }
}
