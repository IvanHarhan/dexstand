using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Day and night on the region map. The hour comes from the real UTC clock (a game day lasts dayMinutes), so every
/// player sees the same sky without anything sent over the network. It turns the sun, tints and dims it (orange at
/// dawn and dusk, pale blue moonlight at night), sets the ambient light and fog, and feeds the global _Night the
/// map's shaders read: lit windows on the facades (Dexstand/Facade), stars (Dexstand/CloudSky). Street lamp heads
/// glow at night. Sits on the map root; the scene builder adds it.
/// </summary>
public class MapDayNight : MonoBehaviour
{
    public float dayMinutes = 24f;
    [Tooltip("Shifts the clock, hours")] public float offset = 8f;
    public float sunIntensity = 1.1f;

    private Light sun;
    private Material sky;
    private readonly System.Collections.Generic.List<Material> lampHeads = new System.Collections.Generic.List<Material>();
    private static readonly int NightId = Shader.PropertyToID("_Night");

    public static float Hour { get; private set; }

    private void Start()
    {
        sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
        sky = RenderSettings.skybox;
        foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name.StartsWith("LampHead") && !lampHeads.Contains(m)) lampHeads.Add(m);
        RenderSettings.ambientMode = AmbientMode.Custom;
    }

    private void Update()
    {
        double t = System.DateTime.UtcNow.TimeOfDay.TotalSeconds / (dayMinutes * 60.0) * 24.0 + offset;
        float hour = (float)(t % 24.0);
        Hour = hour;

        // sun from east (06:00) over the south (12:00) to west (18:00), 50° high at noon; the moon opposite at night
        float dayArc = (hour - 6f) / 12f;                // 0..1 while the sun is up
        float elev = Mathf.Sin(dayArc * Mathf.PI) * 50f;
        float night = Mathf.Clamp01((-elev + 2f) / 10f);  // 0 by day, 1 once the sun is 8° below
        float dusk = Mathf.Clamp01(1f - Mathf.Abs(elev - 4f) / 14f) * (1f - night);
        if (sun != null)
        {
            float az = 90f + dayArc * 180f;
            bool moon = night > 0.5f;
            sun.transform.rotation = Quaternion.Euler(moon ? 35f : Mathf.Max(elev, 2f), moon ? az + 180f : az, 0f);
            var dayCol = Color.Lerp(new Color(1f, 0.95f, 0.87f), new Color(1f, 0.55f, 0.28f), dusk);
            sun.color = Color.Lerp(dayCol, new Color(0.55f, 0.65f, 0.9f), night);
            sun.intensity = Mathf.Lerp(sunIntensity * Mathf.Clamp01(elev / 12f + 0.15f), 0.22f, night);
        }

        var skyTop = Color.Lerp(Color.Lerp(new Color(0.13f, 0.3f, 0.68f), new Color(0.2f, 0.18f, 0.35f), dusk),
                                new Color(0.01f, 0.015f, 0.04f), night);
        var horizon = Color.Lerp(Color.Lerp(new Color(0.72f, 0.78f, 0.84f), new Color(0.9f, 0.55f, 0.35f), dusk),
                                 new Color(0.03f, 0.04f, 0.07f), night);
        RenderSettings.fogColor = horizon;
        if (sky != null)
        {
            if (sky.HasProperty("_ZenithColor")) sky.SetColor("_ZenithColor", skyTop);
            if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", horizon);
        }
        var probe = new SphericalHarmonicsL2();
        probe.AddAmbientLight(Color.Lerp(Color.Lerp(skyTop, horizon, 0.5f) * 1.1f, new Color(0.08f, 0.09f, 0.14f), night));
        RenderSettings.ambientProbe = probe;

        Shader.SetGlobalFloat(NightId, night);
        var glow = new Color(1f, 0.78f, 0.45f) * (night * 2.5f);
        foreach (var m in lampHeads)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", glow);
        }
    }

    private void OnDisable() => Shader.SetGlobalFloat(NightId, 0f);
}
