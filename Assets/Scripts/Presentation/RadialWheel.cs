using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A radial picker: hold its button — or its key on a PC — drag towards a
/// slice, let go to take it. One component, three instances: weapons,
/// consumables, emotes. It listens only to the InputRouter, so a finger and a
/// mouse drive it the same way, and what it picks goes out as network input
/// like every other command.
///
/// The circle is drawn in the middle of the screen whatever opened it. Only the
/// direction of the drag counts, so a thumb never has to cross half the screen.
/// </summary>
public class RadialWheel : MonoBehaviour
{
    public enum Kind { Weapons = 1, Items = 2, Emotes = 3 }

    public Kind kind = Kind.Weapons;

    [Tooltip("Pointer travel, as a share of screen height, before a slice is chosen. " +
             "Letting go short of it picks nothing.")]
    public float deadZone = 0.035f;

    [Tooltip("A press this short that goes nowhere is a tap: the weapons wheel " +
             "inspects the weapon, the others do nothing.")]
    public float tapSeconds = 0.25f;

    public float radius = 220f;

    private static readonly Color Accent = new Color(0.62f, 0.7f, 0.5f, 0.9f);
    private static readonly Color Muted = new Color(0.45f, 0.45f, 0.45f, 0.6f);

    private InputRouter router;
    private PlayerHealth local;
    private PlayerEffects effects;

    private RectTransform ring;
    private Image highlight;
    private Text caption;
    private readonly List<Text> slices = new List<Text>();
    private readonly List<string> names = new List<string>();

    private bool open;
    private float openedAt;
    private int hovered = -1;

    private void Awake() => Build();

    private void Update()
    {
        if (router == null) router = FindFirstObjectByType<InputRouter>();
        if (router == null) return;

        bool held = router.WheelHeld == (int)kind;
        if (held && !open) Open();
        if (!open) return;

        // Once let go the drag reads zero, so the slice is the last one pointed at.
        if (!held)
        {
            Close();
            return;
        }

        Vector2 drag = router.WheelDrag;
        hovered = drag.magnitude < deadZone * Screen.height ? -1 : SliceAt(drag);

        // Drawn once it is clearly a hold, so a tap does not flash a wheel.
        bool shown = hovered >= 0 || Time.unscaledTime - openedAt > tapSeconds * 0.5f;
        if (ring.gameObject.activeSelf != shown) ring.gameObject.SetActive(shown);
        if (shown) Draw();
    }

    // Hidden while held — a menu opening — closes without picking.
    private void OnDisable()
    {
        open = false;
        if (ring != null) ring.gameObject.SetActive(false);
    }

    private void Open()
    {
        open = true;
        openedAt = Time.unscaledTime;
        hovered = -1;
        Fill();
    }

    private void Close()
    {
        open = false;
        ring.gameObject.SetActive(false);

        if (hovered >= 0 && hovered < names.Count) Pick(hovered);
        else if (Time.unscaledTime - openedAt < tapSeconds) Tap();
    }

    private void Pick(int index)
    {
        switch (kind)
        {
            case Kind.Weapons: router.PickWeapon(index); break;
            case Kind.Items: router.PickItem((Consumable)(index + 1)); break;
            case Kind.Emotes: router.PickEmote(index); break;
        }
    }

    /// <summary>A tap on the weapons wheel turns the weapon over for a look.</summary>
    private void Tap()
    {
        if (kind != Kind.Weapons || !FindLocal()) return;

        // The pack's arms have an authored inspect for each weapon; the scripted
        // twirl is only for the body-carried setup.
        var pack = local.GetComponentInChildren<PackViewmodel>(true);
        if (pack != null && pack.isActiveAndEnabled)
        {
            pack.Inspect();
            return;
        }

        var bob = local.GetComponentInChildren<WeaponBob>(true);
        if (bob != null) bob.Inspect();
    }

    /// <summary>What the slices say, read off the local player each time the wheel opens.</summary>
    private void Fill()
    {
        names.Clear();

        if (FindLocal())
        {
            switch (kind)
            {
                case Kind.Weapons:
                    var switcher = local.GetComponent<WeaponSwitcher>();
                    if (switcher != null)
                        foreach (var slot in switcher.slots) names.Add(slot.displayName);
                    break;

                case Kind.Items:
                    for (int i = 1; i < PlayerEffects.Names.Length; i++) names.Add(PlayerEffects.Names[i]);
                    break;

                case Kind.Emotes:
                    var emotes = local.GetComponent<EmoteController>();
                    if (emotes != null)
                    {
                        for (int i = 0; i < emotes.emotes.Length; i++)
                            names.Add(i < emotes.labels.Length ? emotes.labels[i] : emotes.emotes[i]);
                    }
                    break;
            }
        }

        while (slices.Count < names.Count) slices.Add(NewText(ring, "Slice", 34, new Vector2(260f, 70f)));

        for (int i = 0; i < slices.Count; i++)
        {
            bool used = i < names.Count;
            slices[i].gameObject.SetActive(used);
            if (!used) continue;

            slices[i].text = names[i];
            slices[i].rectTransform.anchoredPosition = Direction(i) * radius;
        }
    }

    private void Draw()
    {
        // Consumables can be refused — none left, or one already running.
        bool usable = kind != Kind.Items || (effects != null && effects.Object != null &&
                                             effects.Object.IsValid && effects.CanTake);

        for (int i = 0; i < names.Count; i++) slices[i].color = usable ? Color.white : Muted;

        highlight.gameObject.SetActive(hovered >= 0);
        if (hovered >= 0)
        {
            highlight.rectTransform.anchoredPosition = Direction(hovered) * radius;
            highlight.color = usable ? Accent : Muted;
        }

        caption.text = hovered >= 0 ? names[hovered] : Title();
    }

    private string Title()
    {
        switch (kind)
        {
            case Kind.Weapons: return "ОРУЖИЕ";
            case Kind.Emotes: return "ЭМОЦИИ";
        }

        if (effects == null || effects.Object == null || !effects.Object.IsValid) return "ПРЕДМЕТЫ";
        return $"ПРЕДМЕТЫ ×{effects.Charges}";
    }

    /// <summary>Slice under a drag: the first at the top, then clockwise.</summary>
    private int SliceAt(Vector2 drag)
    {
        if (names.Count == 0) return -1;

        float angle = Mathf.Atan2(drag.x, drag.y);
        if (angle < 0f) angle += 2f * Mathf.PI;
        return Mathf.RoundToInt(angle / (2f * Mathf.PI / names.Count)) % names.Count;
    }

    private Vector2 Direction(int index)
    {
        float angle = index * 2f * Mathf.PI / Mathf.Max(1, names.Count);
        return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
    }

    /// <summary>
    /// The player on this device. Fusion spawns it after the HUD exists, so it
    /// is looked up rather than wired — once per opening, not every frame.
    /// </summary>
    private bool FindLocal()
    {
        if (local != null && local.Object != null && local.Object.IsValid) return true;

        local = null;
        foreach (var candidate in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (candidate.Object == null || !candidate.Object.HasInputAuthority) continue;
            local = candidate;
            break;
        }

        effects = local != null ? local.GetComponent<PlayerEffects>() : null;
        return local != null;
    }

    private void Build()
    {
        var go = new GameObject("Ring", typeof(RectTransform));
        ring = go.GetComponent<RectTransform>();
        ring.SetParent(transform, false);
        ring.anchorMin = ring.anchorMax = new Vector2(0.5f, 0.5f);
        ring.sizeDelta = Vector2.one * (radius * 2f + 200f);

        var back = go.AddComponent<Image>();
        back.sprite = Disc();
        back.color = new Color(0.05f, 0.06f, 0.08f, 0.6f);
        back.raycastTarget = false;

        var spot = new GameObject("Highlight", typeof(RectTransform));
        spot.transform.SetParent(ring, false);
        highlight = spot.AddComponent<Image>();
        highlight.sprite = Disc();
        highlight.raycastTarget = false;
        highlight.rectTransform.sizeDelta = new Vector2(170f, 170f);

        caption = NewText(ring, "Caption", 40, new Vector2(340f, 90f));

        go.SetActive(false);
    }

    private static Text NewText(Transform parent, string name, int size, Vector2 area)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.rectTransform.sizeDelta = area;
        return text;
    }

    private static Sprite disc;

    /// <summary>A soft-edged white circle, made once: the project ships no UI sprites.</summary>
    private static Sprite Disc()
    {
        if (disc != null) return disc;

        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        float r = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d) * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        disc = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return disc;
    }
}
