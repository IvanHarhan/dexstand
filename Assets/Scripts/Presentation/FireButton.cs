using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Hold to fire. Sends state to MobileInputService.
/// </summary>
public class FireButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
{
    [SerializeField] private MobileInputService inputService;

    [Tooltip("Sliding off the button keeps firing and steers the view, so one " +
             "thumb can shoot and aim at once.")]
    [SerializeField] private bool dragAims = true;

    private Vector2 lastDragPosition;

    public void OnPointerDown(PointerEventData e)
    {
        lastDragPosition = e.position;
        if (inputService != null) inputService.OnFireDown();
    }

    public void OnDrag(PointerEventData e)
    {
        if (!dragAims || inputService == null) return;

        var delta = e.position - lastDragPosition;
        lastDragPosition = e.position;
        inputService.OnLookDelta(delta);
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (inputService != null) inputService.OnFireUp();
    }
}
