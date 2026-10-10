using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Dynamic joystick that appears at touch position.
/// Left side of screen for movement.
/// </summary>
public class DynamicJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [Header("Settings")]
    public float handleRange = 1f;

    [Header("UI References")]
    public RectTransform background;
    public RectTransform handle;

    [HideInInspector]
    public Vector2 inputVector = Vector2.zero;

    private Vector2 origin;
    private Canvas canvas;
    private Camera cam;

    private void Start()
    {
        canvas = GetComponentInParent<Canvas>();
        cam = canvas.renderMode == RenderMode.ScreenSpaceCamera ? canvas.worldCamera : null;

        background.gameObject.SetActive(false);
    }

    /// <summary>
    /// The rect anchoredPosition is measured against. Converting touches in
    /// canvas space while positioning inside a half-screen parent offsets the
    /// stick by half the screen.
    /// </summary>
    private RectTransform Area => background != null ? background.parent as RectTransform : null;

    public void OnPointerDown(PointerEventData e)
    {
        var area = Area;
        if (area == null) return;

        background.gameObject.SetActive(true);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            area, e.position, cam, out origin);
        background.anchoredPosition = origin;
        handle.anchoredPosition = Vector2.zero;
    }

    public void OnDrag(PointerEventData e)
    {
        var area = Area;
        if (area == null) return;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            area, e.position, cam, out Vector2 pos);

        Vector2 delta = pos - origin;
        float radius = background.sizeDelta.x * 0.5f;

        delta = Vector2.ClampMagnitude(delta, radius * handleRange);
        handle.anchoredPosition = delta;

        inputVector = delta / (radius * handleRange);
    }

    public void OnPointerUp(PointerEventData e)
    {
        inputVector = Vector2.zero;
        handle.anchoredPosition = Vector2.zero;
        background.gameObject.SetActive(false);
    }
}
