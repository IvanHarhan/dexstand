using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// The bloom and motion blur switches in the pause menu. They turn the effects on the colour-grade volume on and
/// off (bloom starts on, motion blur off) and the choice is remembered between runs.
/// </summary>
[RequireComponent(typeof(Volume))]
public class GraphicsOptions : MonoBehaviour
{
    private const string BloomKey = "Dexstand.Bloom", BlurKey = "Dexstand.MotionBlur";

    [SerializeField] private Button bloomButton;
    [SerializeField] private Button blurButton;

    private Bloom bloom;
    private MotionBlur blur;

    private void Start()
    {
        var profile = GetComponent<Volume>().profile;  // the volume's own copy: the asset stays as built
        profile.TryGet(out bloom);
        profile.TryGet(out blur);
        if (bloomButton != null) bloomButton.onClick.AddListener(() => Flip(BloomKey, 1));
        if (blurButton != null) blurButton.onClick.AddListener(() => Flip(BlurKey, 0));
        Apply();
    }

    private void Flip(string key, int start)
    {
        PlayerPrefs.SetInt(key, 1 - PlayerPrefs.GetInt(key, start));
        Apply();
    }

    private void Apply()
    {
        bool bloomOn = PlayerPrefs.GetInt(BloomKey, 1) == 1, blurOn = PlayerPrefs.GetInt(BlurKey, 0) == 1;
        if (bloom != null) bloom.active = bloomOn;
        if (blur != null) blur.active = blurOn;
        Label(bloomButton, "Блум", bloomOn);
        Label(blurButton, "Размытие", blurOn);
    }

    private static void Label(Button button, string name, bool on)
    {
        var text = button != null ? button.GetComponentInChildren<Text>() : null;
        if (text != null) text.text = $"{name}: {(on ? "вкл" : "выкл")}";
    }
}
