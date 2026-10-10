using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Tap to change the magazine. Sends one-shot to MobileInputService, the same
/// way the jump button does — the reload has to travel as network input, so it
/// cannot call the weapon directly.
/// </summary>
public class ReloadButton : MonoBehaviour, IPointerDownHandler
{
    [SerializeField] private MobileInputService inputService;

    public void OnPointerDown(PointerEventData e)
    {
        if (inputService != null) inputService.OnReload();
    }
}
