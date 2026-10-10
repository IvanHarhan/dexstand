using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Editor check of the region map (the "tour" trigger of DexstandAutoRun): starts a single-player match, puts a
/// camera of its own at street level in a few towns (it draws last, so grass, trees, people and traffic gather
/// round it), saves a turn of shots and one from above, then one at night, and leaves play mode.
/// </summary>
public class MapTourCapture : MonoBehaviour
{
    public const string TourKey = "DexstandTour";
    private const string Output = @"C:\Users\harha\AppData\Local\Temp\claude\C--Users-harha-Desktop\18bb1fb2-8816-4b97-b782-2ccdb5b95406\scratchpad\tour";

    // scene units at the map's usual third of real size (as MapTeleport's)
    private static readonly (string name, Vector2 xz)[] Places =
    {
        ("wiehl", new Vector2(2f, 3f)), ("forum", new Vector2(-374f, -2812f)), ("dieringhausen", new Vector2(145f, -1398f)),
        ("a4", new Vector2(1980.7f, -1006.1f)),  // the A4 by Bielstein, first corridor chunk
    };

#if UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (!UnityEditor.EditorPrefs.GetBool(TourKey, false)) return;
        UnityEditor.EditorPrefs.SetBool(TourKey, false);
        new GameObject("MapTourCapture").AddComponent<MapTourCapture>();
    }
#endif

    private IEnumerator Start()
    {
        System.IO.Directory.CreateDirectory(Output);
        yield return new WaitForSeconds(1.5f);
        var menu = FindFirstObjectByType<GameMenu>();
        if (menu != null) menu.StartSinglePlayer();
        yield return new WaitForSeconds(4f);

        var cam = new GameObject("TourCamera").AddComponent<Camera>();
        cam.depth = 100f;
        cam.fieldOfView = 70f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 600f;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        var map = GameObject.Find("Map");
        float k = map != null ? map.transform.localScale.x * 3f : 1f;
        var dn = FindFirstObjectByType<MapDayNight>();
        if (dn != null) dn.offset += 13f - MapDayNight.Hour;  // the day shots at one o'clock
        yield return null;

        foreach (var (name, xz0) in Places)
        {
            var xz = xz0 * k;
            float y = Ground(xz);
            cam.transform.position = new Vector3(xz.x, y + 0.55f, xz.y);
            yield return new WaitForSeconds(5f);  // grass chunks, people and cars come round the camera
            // far from the start the tile under the camera streams in during that wait: the first ground probe
            // found nothing and left the camera under the terrain (the Forum and A4 shots)
            y = Ground(xz);
            cam.transform.position = new Vector3(xz.x, y + 0.55f, xz.y);
            var hits = Physics.RaycastAll(new Vector3(xz.x, 5000f, xz.y), Vector3.down, 10000f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Debug.Log($"[Tour] {name} ray: " + string.Join(", ", System.Array.ConvertAll(hits, h => $"{h.collider.name}@{h.point.y:F1}")));
            var people = FindFirstObjectByType<MapPedestrians>();
            var grass = FindFirstObjectByType<MapGrass>();
            Debug.Log($"[Tour] {name}: people {(people != null ? people.Count : -1)} (enabled {people != null && people.enabled}, " +
                      $"tries {(people != null ? people.Tries : -1)}, last ground {(people != null ? people.LastGround : "")}), " +
                      $"grass clumps {(grass != null ? grass.Clumps : -1)} (enabled {grass != null && grass.enabled})");
            for (int a = 0; a < 3; a++)
            {
                cam.transform.rotation = Quaternion.Euler(4f, a * 120f, 0f);
                yield return new WaitForSeconds(1f);
                yield return Shot($"{name}_{a}");
            }
            if (name == "wiehl")
            {
                // what the new picture costs, standing still once the tiles are in: all on, then one thing off at a time
                cam.transform.rotation = Quaternion.Euler(4f, 120f, 0f);
                yield return new WaitForSeconds(3f);
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                var refl = FindFirstObjectByType<MapReflections>();
                string costs = "";
                var plain = new System.Collections.Generic.Dictionary<Material, Material>();
                var swapped = new System.Collections.Generic.List<(Renderer r, Material[] was)>();
                for (int step = 0; step < 4; step++)
                {
                    if (step == 3)
                    {
                        // the ground as it was drawn before OrthoDetail reached the region: the photo in URP Lit
                        var lit = Shader.Find("Universal Render Pipeline/Lit");
                        foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                        {
                            var mats = r.sharedMaterials;
                            bool any = false;
                            for (int m = 0; m < mats.Length; m++)
                            {
                                if (mats[m] == null || mats[m].shader.name != "Dexstand/OrthoDetail") continue;
                                if (!plain.TryGetValue(mats[m], out var p))
                                {
                                    p = new Material(lit);
                                    p.SetTexture("_BaseMap", mats[m].GetTexture("_BaseMap"));
                                    plain[mats[m]] = p;
                                }
                                mats[m] = p;
                                any = true;
                            }
                            if (any) { swapped.Add((r, r.sharedMaterials)); r.sharedMaterials = mats; }
                        }
                    }
                    if (step == 1 && refl != null)
                    {
                        refl.enabled = false;
                        foreach (var p in refl.GetComponentsInChildren<ReflectionProbe>()) p.enabled = false;
                    }
                    if (step == 2 && urp != null) urp.shadowCascadeCount = 2;
                    yield return new WaitForSeconds(1f);
                    int f0 = Time.frameCount;
                    float t0 = Time.realtimeSinceStartup;
                    yield return new WaitForSeconds(4f);
                    costs += $"{(step == 0 ? "all" : step == 1 ? "no probes" : step == 2 ? "no probes+2 cascades" : "+plain ground")} " +
                             $"{(Time.frameCount - f0) / (Time.realtimeSinceStartup - t0):F0} fps; ";
                }
                foreach (var (r, was) in swapped) if (r != null) r.sharedMaterials = was;
                if (urp != null) urp.shadowCascadeCount = 4;
                if (refl != null)
                {
                    refl.enabled = true;
                    foreach (var p in refl.GetComponentsInChildren<ReflectionProbe>()) p.enabled = true;
                }
                Debug.Log("[Tour] cost at wiehl: " + costs);
                // the white house east of the bus station through a long lens: facade detail at its real distance
                cam.transform.rotation = Quaternion.Euler(-1f, 137f, 0f);
                cam.fieldOfView = 12f;
                yield return new WaitForSeconds(0.5f);
                yield return Shot("wiehl_zoom");
                cam.fieldOfView = 70f;
            }
            // the nearest walker, from a few metres
            var root = GameObject.Find("Pedestrians");
            Transform person = null;
            if (root != null)
                foreach (Transform c in root.transform)
                    if (c.gameObject.activeSelf && c.name != "PedestrianTemplate" &&
                        (person == null || (c.position - cam.transform.position).sqrMagnitude <
                                           (person.position - cam.transform.position).sqrMagnitude)) person = c;
            if (person != null)
            {
                var r = person.GetComponentInChildren<Renderer>();
                Debug.Log($"[Tour] {name} person {person.name} at {person.position}, scale {person.lossyScale.x:F3}, " +
                          $"renderer {(r != null ? r.bounds.size.ToString() : "none")}");
                cam.transform.position = person.position + person.forward * 1.2f + Vector3.up * 0.45f;
                cam.transform.LookAt(person.position + Vector3.up * 0.35f);
                yield return new WaitForSeconds(0.5f);
                yield return Shot($"{name}_person");
            }
            cam.transform.position = new Vector3(xz.x, y + 18f, xz.y - 25f);
            cam.transform.rotation = Quaternion.Euler(30f, 0f, 0f);
            yield return new WaitForSeconds(2f);
            yield return Shot($"{name}_above");
        }

        // night in Wiehl: shift the clock so it reads 23:00
        if (dn != null)
        {
            dn.offset += 23f - MapDayNight.Hour;
            var xz = Places[0].xz * k;
            cam.transform.position = new Vector3(xz.x, Ground(xz) + 0.55f, xz.y);
            cam.transform.rotation = Quaternion.Euler(4f, 120f, 0f);
            yield return new WaitForSeconds(2f);
            yield return Shot("wiehl_night");
        }
        Debug.Log($"[Tour] Done: {Output}");
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

    private static IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        var t = ScreenCapture.CaptureScreenshotAsTexture();
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(Output, name + ".png"), t.EncodeToPNG());
        Destroy(t);
    }
}
