using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// An on-screen pedal or steering button for the test car: held while a finger
/// is on it, released when the finger lifts or slides off.
/// </summary>
public class CarHoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public bool Held { get; private set; }

    public void OnPointerDown(PointerEventData eventData) => Held = true;
    public void OnPointerUp(PointerEventData eventData) => Held = false;
    public void OnPointerExit(PointerEventData eventData) => Held = false;

    private void OnDisable() => Held = false;
}
