using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Health bar, hit flash and respawn countdown for whichever player is local.
///
/// Builds its own canvas at runtime instead of being wired into a prefab. The
/// local player does not exist until Fusion spawns it, so anything authored in
/// the scene would have to hunt for its target anyway — and this way the HUD
/// survives a rebuild of the player prefab without being re-linked.
/// </summary>
public class HealthHud : MonoBehaviour
{
    [Tooltip("Seconds the red edge flash takes to fade after a hit.")]
    public float flashSeconds = 0.45f;

    private PlayerHealth health;
    private WeaponController weapon;
    private WeaponSwitcher switcher;
    private PlayerEffects effects;

    private Image barFill;
    private Image flash;
    private Text label;
    private Text respawnLabel;
    private Text ammoLabel;
    private Text effectLabel;
    private GameObject taking;
    private Image takingFill;
    private Text takingLabel;

    private float lastSeenHealth = -1f;
    private float flashUntil;

    private void Awake()
    {
        Build();
    }

    private void Update()
    {
        if (health == null || health.Object == null || !health.Object.IsValid)
        {
            health = FindLocalHealth();
            if (health == null)
            {
                SetVisible(false);
                return;
            }
            lastSeenHealth = health.Health;

            weapon = health.GetComponentInChildren<WeaponController>(true);
            switcher = health.GetComponent<WeaponSwitcher>();
            effects = health.GetComponent<PlayerEffects>();
        }

        SetVisible(true);

        float current = health.Health;

        // Only a drop is a hit. Regeneration climbs past the same numbers and
        // would otherwise flash the screen red while you are healing.
        if (lastSeenHealth >= 0f && current < lastSeenHealth - 0.01f)
            flashUntil = Time.time + flashSeconds;
        lastSeenHealth = current;

        barFill.fillAmount = Mathf.Lerp(barFill.fillAmount, health.Fraction, 12f * Time.deltaTime);
        barFill.color = health.Fraction > 0.35f
            ? new Color(0.85f, 0.87f, 0.85f, 0.92f)
            : new Color(0.80f, 0.22f, 0.18f, 0.95f);

        label.text = Mathf.CeilToInt(Mathf.Max(0f, current)).ToString();

        float remaining = flashUntil - Time.time;
        var flashColour = flash.color;
        flashColour.a = remaining > 0f ? Mathf.Clamp01(remaining / flashSeconds) * 0.55f : 0f;
        flash.color = flashColour;

        respawnLabel.enabled = health.IsDead;
        if (health.IsDead) respawnLabel.text = "ЛЁГ";

        ammoLabel.text = Ammo();
        effectLabel.text = EffectLine();

        bool busy = effects != null && effects.Taking != 0;
        if (taking.activeSelf != busy) taking.SetActive(busy);
        if (busy)
        {
            takingLabel.text = PlayerEffects.Names[Mathf.Clamp(effects.Taking, 0, PlayerEffects.Names.Length - 1)] + "…";
            takingFill.fillAmount = effects.TakeProgress;
        }
    }

    /// <summary>The weapon's name and what is left in the magazine; a knife says only its name.</summary>
    private string Ammo()
    {
        if (switcher == null) return "";
        if (weapon == null || weapon.magazineSize <= 0) return switcher.CurrentName;
        if (weapon.IsReloading) return $"{switcher.CurrentName}  перезарядка";
        return $"{switcher.CurrentName}  {weapon.Ammo}/{weapon.magazineSize}";
    }

    /// <summary>Charges left, and what is running with its seconds.</summary>
    private string EffectLine()
    {
        if (effects == null) return "";

        string line = $"Предметы: {effects.Charges}";
        var current = effects.Current;
        if (current == Consumable.None) return line;

        if (effects.InWithdrawal) return $"{line}   Ломка {Mathf.CeilToInt(effects.Left)}с";

        float left = current == Consumable.Stim ? effects.Left - effects.withdrawalSeconds : effects.Left;
        return $"{line}   {PlayerEffects.Names[(int)current]} {Mathf.CeilToInt(left)}с";
    }

    private PlayerHealth FindLocalHealth()
    {
        foreach (var candidate in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (candidate.Object != null && candidate.Object.HasInputAuthority) return candidate;
        }
        return null;
    }

    private void SetVisible(bool on)
    {
        if (barFill != null && barFill.transform.parent.gameObject.activeSelf != on)
            barFill.transform.parent.gameObject.SetActive(on);
    }

    private void Build()
    {
        var canvasGo = new GameObject("HealthHudCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Below the menus, above the world. The menus claim 100 and up.
        canvas.sortingOrder = 10;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        // Half and half, so the bar keeps its size in both phone portrait-ish
        // and desktop widescreen rather than tracking one axis.
        scaler.matchWidthOrHeight = 0.5f;

        canvasGo.AddComponent<GraphicRaycaster>();

        // Full-screen red wash on damage. Not a raycast target: it covers the
        // whole screen and would otherwise eat every touch on mobile.
        flash = NewImage(canvasGo.transform, "DamageFlash");
        Stretch(flash.rectTransform);
        flash.color = new Color(0.65f, 0.05f, 0.05f, 0f);
        flash.raycastTarget = false;

        var bar = new GameObject("HealthBar").AddComponent<RectTransform>();
        bar.SetParent(canvasGo.transform, false);
        bar.anchorMin = new Vector2(0f, 0f);
        bar.anchorMax = new Vector2(0f, 0f);
        bar.pivot = new Vector2(0f, 0f);
        bar.anchoredPosition = new Vector2(48f, 48f);
        bar.sizeDelta = new Vector2(360f, 26f);

        var backing = NewImage(bar, "Backing");
        Stretch(backing.rectTransform);
        backing.color = new Color(0f, 0f, 0f, 0.45f);
        backing.raycastTarget = false;

        barFill = NewImage(bar, "Fill");
        Stretch(barFill.rectTransform);
        barFill.type = Image.Type.Filled;
        barFill.fillMethod = Image.FillMethod.Horizontal;
        barFill.fillAmount = 1f;
        barFill.raycastTarget = false;

        label = NewText(bar, "Value", 26, TextAnchor.MiddleLeft);
        label.rectTransform.anchorMin = new Vector2(1f, 0f);
        label.rectTransform.anchorMax = new Vector2(1f, 1f);
        label.rectTransform.pivot = new Vector2(0f, 0.5f);
        label.rectTransform.anchoredPosition = new Vector2(14f, 0f);
        label.rectTransform.sizeDelta = new Vector2(120f, 0f);

        respawnLabel = NewText(canvasGo.transform, "Respawn", 64, TextAnchor.MiddleCenter);
        Stretch(respawnLabel.rectTransform);
        respawnLabel.color = new Color(0.85f, 0.2f, 0.16f, 0.9f);
        respawnLabel.enabled = false;

        // Above the bar: the weapon and its magazine, then the consumables.
        ammoLabel = NewText(bar, "Ammo", 30, TextAnchor.LowerLeft);
        PlaceAbove(ammoLabel.rectTransform, 36f);
        effectLabel = NewText(bar, "Effect", 26, TextAnchor.LowerLeft);
        PlaceAbove(effectLabel.rectTransform, 76f);

        // Taking a consumable: its name and a bar, low in the middle, where the
        // lowered weapon used to be.
        var take = new GameObject("Taking").AddComponent<RectTransform>();
        take.SetParent(canvasGo.transform, false);
        take.anchorMin = take.anchorMax = new Vector2(0.5f, 0f);
        take.pivot = new Vector2(0.5f, 0f);
        take.anchoredPosition = new Vector2(0f, 230f);
        take.sizeDelta = new Vector2(420f, 16f);
        taking = take.gameObject;

        var takeBacking = NewImage(take, "Backing");
        Stretch(takeBacking.rectTransform);
        takeBacking.color = new Color(0f, 0f, 0f, 0.45f);
        takeBacking.raycastTarget = false;

        takingFill = NewImage(take, "Fill");
        Stretch(takingFill.rectTransform);
        takingFill.type = Image.Type.Filled;
        takingFill.fillMethod = Image.FillMethod.Horizontal;
        takingFill.color = new Color(0.62f, 0.7f, 0.5f, 0.95f);
        takingFill.raycastTarget = false;

        takingLabel = NewText(take, "Name", 40, TextAnchor.LowerCenter);
        PlaceAbove(takingLabel.rectTransform, 22f);
        taking.SetActive(false);
    }

    /// <summary>A line of text resting this far above the top of its parent.</summary>
    private static void PlaceAbove(RectTransform rect, float gap)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, gap - 10f);
        rect.sizeDelta = new Vector2(240f, 40f);
    }

    private static Sprite white;

    private static Image NewImage(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        // A Filled image with no sprite ignores fillAmount and draws whole: the health bar stayed full and
        // only its colour said anything.
        if (white == null)
            white = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
        image.sprite = white;
        return image;
    }

    private static Text NewText(Transform parent, string name, int size, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = anchor;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
