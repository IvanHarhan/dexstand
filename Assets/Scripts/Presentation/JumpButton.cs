using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Tap to jump. Sends one-shot to MobileInputService.
/// </summary>
public class JumpButton : MonoBehaviour, IPointerDownHandler
{
    [SerializeField] private MobileInputService inputService;

    public void OnPointerDown(PointerEventData e)
    {
        if (inputService != null) inputService.OnJump();
    }
}
