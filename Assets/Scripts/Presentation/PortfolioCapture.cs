using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Editor-only screenshot run for the GitHub page (the "portfolio" trigger of DexstandAutoRun): starts a
/// single-player match and renders 1920x1080 frames straight from cameras into a RenderTexture — the first-person
/// view, the car, street level in towns along the map and the A4, Köln, sunset and night — then leaves play mode.
/// </summary>
public class PortfolioCapture : MonoBehaviour
{
    public const string Key = "DexstandPortfolio";
    private const int W = 1920, H = 1080;

    // scene units at the map's usual third of real size (as MapTeleport's), view headings to try
    private static readonly (string name, Vector2 xz)[] Places =
    {
        ("wiehl", new Vector2(2f, 3f)), ("forum", new Vector2(-374f, -2812f)), ("dieringhausen", new Vector2(145f, -1398f)),
        ("kaufland", new Vector2(480.2f, -1332.8f)), ("bielstein", new Vector2(1054.7f, -570.9f)),
        ("a4_engelskirchen", new Vector2(4434.3f, -715.1f)), ("a4_overath", new Vector2(6933f, -14.5f)),
        ("koeln_dom", new Vector2(13831.1f, 129.6f)), ("koeln_neumarkt", new Vector2(14066.7f, 343f)),
    };

    private string output;
    private RenderTexture rt;

#if UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (!UnityEditor.EditorPrefs.GetBool(Key, false)) return;
        UnityEditor.EditorPrefs.SetBool(Key, false);
        new GameObject("PortfolioCapture").AddComponent<PortfolioCapture>();
    }
#endif

    private IEnumerator Start()
    {
        output = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", "screenshots"));
        System.IO.Directory.CreateDirectory(output);
        rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        yield return new WaitForSeconds(1.5f);
        var menu = FindFirstObjectByType<GameMenu>();
        if (menu != null) menu.StartSinglePlayer();
        yield return new WaitForSeconds(6f);

        var dn = FindFirstObjectByType<MapDayNight>();
        if (dn != null) dn.offset += 14f - MapDayNight.Hour;

        // the player's own view, weapon in hand, then turned a little for a second frame
        var player = Camera.main;
        if (player != null)
        {
            Shot(player, "fp_0");
            yield return new WaitForSeconds(0.5f);
        }

        var cam = new GameObject("PortfolioCamera").AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 65f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 1500f;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        var map = GameObject.Find("Map");
        float k = map != null ? map.transform.localScale.x * 3f : 1f;

        // the car, from the front quarter and from behind
        var car = SimpleCar.Current;
        if (car != null)
        {
            // PlayerController sits on the prefab root, which stays at spawn; the camera moves with the body
            if (player != null) car.RPC_Summon(player.transform.position, player.transform.forward);
            yield return new WaitForSeconds(3f);
            var t = car.transform;
            Debug.Log($"[Portfolio] car {car.name} at {t.position}");
            cam.transform.position = t.position + t.forward * 2.2f + t.right * 1.6f + Vector3.up * 0.7f;
            cam.transform.LookAt(t.position + Vector3.up * 0.3f);
            Shot(cam, "car_front");
            cam.transform.position = t.position - t.forward * 2.6f - t.right * 0.9f + Vector3.up * 0.9f;
            cam.transform.LookAt(t.position + t.forward * 3f + Vector3.up * 0.3f);
            Shot(cam, "car_back");
        }

        foreach (var (name, xz0) in Places)
        {
            var xz = xz0 * k;
            cam.transform.position = new Vector3(xz.x, Ground(xz) + 0.55f, xz.y);
            yield return new WaitForSeconds(9f);  // the tile under the camera streams in, people and cars gather
            float y = Ground(xz);
            cam.transform.position = new Vector3(xz.x, y + 0.55f, xz.y);
            yield return new WaitForSeconds(1f);
            for (int a = 0; a < 4; a++)
            {
                cam.transform.rotation = Quaternion.Euler(3f, a * 90f, 0f);
                yield return null;
                Shot(cam, $"{name}_{a}");
            }
            cam.transform.position = new Vector3(xz.x, y + 25f, xz.y - 35f);
            cam.transform.rotation = Quaternion.Euler(28f, 0f, 0f);
            yield return new WaitForSeconds(2f);
            Shot(cam, $"{name}_above");
        }

        if (dn != null)
        {
            var xz = Places[0].xz * k;
            cam.transform.position = new Vector3(xz.x, Ground(xz) + 0.55f, xz.y);
            yield return new WaitForSeconds(3f);
            foreach (var (hour, tag) in new[] { (19.5f, "sunset"), (23f, "night") })
            {
                dn.offset += hour - MapDayNight.Hour;
                yield return new WaitForSeconds(2f);
                for (int a = 0; a < 4; a++)
                {
                    cam.transform.rotation = Quaternion.Euler(3f, a * 90f, 0f);
                    yield return null;
                    Shot(cam, $"wiehl_{tag}_{a}");
                }
            }
        }

        Debug.Log($"[Portfolio] Done: {output}");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private static float Ground(Vector2 xz)
    {
        float best = float.MinValue;
        foreach (var h in Physics.RaycastAll(new Vector3(xz.x, 5000f, xz.y), Vector3.down, 10000f, ~0, QueryTriggerInteraction.Ignore))
            if (h.collider.name.EndsWith("_col")) best = Mathf.Max(best, h.point.y);
        return best == float.MinValue ? 0f : best;
    }

    private void Shot(Camera cam, string name)
    {
        var was = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = was;
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output, name + ".png"), tex.EncodeToPNG());
        Destroy(tex);
    }
}
