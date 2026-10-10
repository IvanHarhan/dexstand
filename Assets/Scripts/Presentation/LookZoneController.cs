using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Right side of screen: drag to look around.
/// Sends delta to MobileInputService.
/// </summary>
public class LookZoneController : MonoBehaviour, IDragHandler, IPointerDownHandler, IPointerUpHandler
{
    [SerializeField] private MobileInputService inputService;

    private int touchId = -1;
    private Vector2 lastPos;

    public void OnPointerDown(PointerEventData e)
    {
        touchId = e.pointerId;
        lastPos = e.position;
    }

    public void OnDrag(PointerEventData e)
    {
        if (e.pointerId != touchId) return;

        Vector2 delta = e.position - lastPos;
        lastPos = e.position;

        if (inputService != null)
            inputService.OnLookDelta(delta);
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (e.pointerId == touchId)
            touchId = -1;
    }
}
