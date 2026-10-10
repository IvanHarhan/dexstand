using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// The bodycam slider in the pause menu. It is the weight of the volume that
/// carries the bodycam effects — the colour grade sits on a volume of its own
/// and does not change — and the choice is remembered between runs.
/// </summary>
[RequireComponent(typeof(Volume))]
public class BodycamIntensity : MonoBehaviour
{
    /// <summary>The weight the profile is tuned for: the intended look at this slider position.</summary>
    public const float DefaultWeight = 0.6f;

    /// <summary>Where a fresh install starts: off, the player turns it on in the pause menu (the user, 2026-09-27).</summary>
    public const float StartWeight = 0f;

    // a new key so installs that saved the old 60% start clean too
    private const string Key = "Dexstand.Bodycam2";

    /// <summary>The slider as it stands, for the camera lag that goes with the look.</summary>
    public static float Weight { get; private set; } = StartWeight;

    [SerializeField] private Slider slider;
    [SerializeField] private Text label;

    private Volume volume;

    private void Start()
    {
        volume = GetComponent<Volume>();

        float weight = PlayerPrefs.GetFloat(Key, StartWeight);
        Apply(weight);

        if (slider != null)
        {
            slider.SetValueWithoutNotify(weight);
            slider.onValueChanged.AddListener(Set);
        }
    }

    public void Set(float weight)
    {
        PlayerPrefs.SetFloat(Key, weight);
        Apply(weight);
    }

    private void Apply(float weight)
    {
        volume.weight = Mathf.Clamp01(weight);
        Weight = volume.weight;
        if (label != null) label.text = $"Бодикам: {Mathf.RoundToInt(volume.weight * 100f)}%";
    }
}
