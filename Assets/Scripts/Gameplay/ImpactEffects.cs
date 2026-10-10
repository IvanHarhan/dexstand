using UnityEngine;

/// <summary>
/// Visible feedback for a shot: a tracer through the air and a bullet hole on
/// whatever was hit. Objects are built in code so no prefab wiring is needed;
/// the editor builder only has to hand over two materials.
/// </summary>
public class ImpactEffects : MonoBehaviour
{
    [Header("Materials")]
    public Material tracerMaterial;
    public Material decalMaterial;
    public Material flashMaterial;

    [Header("Tracer")]
    public float tracerSeconds = 0.05f;
    public float tracerWidth = 0.02f;

    [Header("Decal")]
    public float decalSize = 0.15f;
    public float decalSeconds = 20f;
    public int maxDecals = 60;

    private readonly System.Collections.Generic.Queue<GameObject> decals =
        new System.Collections.Generic.Queue<GameObject>();

    [Header("Muzzle flash")]
    public float flashSeconds = 0.045f;

    [Tooltip("Metres. Must stay small: the muzzle sits a hand's length from the lens.")]
    public float flashSize = 0.03f;

    public float flashLightRange = 0.9f;
    public float flashLightIntensity = 1.2f;

    /// <summary>
    /// A brief glow and light burst at the barrel. Short-lived on purpose: a
    /// flash that outlives the shot reads as a lamp, not a gunshot.
    /// </summary>
    public void PlayMuzzleFlash(Transform muzzle)
    {
        var material = flashMaterial != null ? flashMaterial : tracerMaterial;
        if (muzzle == null || material == null) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "MuzzleFlash";
        Destroy(go.GetComponent<Collider>());

        go.transform.SetParent(muzzle, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * flashSize * Random.Range(0.8f, 1.25f);
        go.transform.Rotate(0f, 0f, Random.Range(0f, 360f), Space.Self);

        var r = go.GetComponent<Renderer>();
        r.material = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.85f, 0.5f);
        light.range = flashLightRange;
        light.intensity = flashLightIntensity;

        Destroy(go, flashSeconds);
    }

    [Header("Blood")]
    public Color bloodColor = new Color(0.45f, 0.03f, 0.03f);
    public int bloodParticles = 14;
    public float bloodSpeed = 2.2f;
    public float bloodSize = 0.03f;

    private ParticleSystem blood;

    /// <summary>
    /// A short spray where a body was hit. Built once and re-emitted rather
    /// than spawned per shot — a burst every 0.1s would otherwise litter the
    /// scene with objects.
    /// </summary>
    public void PlayBlood(Vector3 point, Vector3 direction)
    {
        if (blood == null) blood = BuildBloodSystem();

        blood.transform.position = point;
        blood.transform.rotation = Quaternion.LookRotation(
            direction.sqrMagnitude > 0.001f ? -direction.normalized : Vector3.up);
        blood.Emit(bloodParticles);
    }

    private ParticleSystem BuildBloodSystem()
    {
        var go = new GameObject("BloodBurst");
        var system = go.AddComponent<ParticleSystem>();
        system.Stop();

        var main = system.main;
        main.startColor = bloodColor;
        main.startSize = bloodSize;
        main.startSpeed = bloodSpeed;
        main.startLifetime = 0.7f;
        main.gravityModifier = 1.6f;
        main.playOnAwake = false;
        main.maxParticles = 200;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = system.emission;
        emission.enabled = false;

        var shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 28f;
        shape.radius = 0.02f;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.material = tracerMaterial != null
            ? new Material(tracerMaterial) { color = bloodColor }
            : renderer.material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        return system;
    }

    public void PlayTracer(Vector3 from, Vector3 to)
    {
        if (tracerMaterial == null) return;

        var go = new GameObject("Tracer");
        var line = go.AddComponent<LineRenderer>();
        line.material = tracerMaterial;
        line.widthMultiplier = tracerWidth;
        line.positionCount = 2;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.useWorldSpace = true;

        Destroy(go, tracerSeconds);
    }

    [Header("Impact sound")]
    [Tooltip("Played where a bullet strikes the world. One is picked at random.")]
    public AudioClip[] impactSounds;
    public float impactVolume = 0.6f;

    public void PlayImpact(Vector3 point, Vector3 normal, Transform surface)
    {
        if (impactSounds != null && impactSounds.Length > 0)
        {
            var clip = impactSounds[Random.Range(0, impactSounds.Length)];
            if (clip != null) AudioSource.PlayClipAtPoint(clip, point, impactVolume);
        }

        if (decalMaterial == null) return;
        Decal("BulletHole", point, normal, surface, decalMaterial, decalSize);
    }

    [Header("Spit")]
    public Color spitColor = new Color(0.24f, 0.16f, 0.07f, 0.85f);
    public float spitSize = 0.06f;

    private Material spitMaterial;

    /// <summary>A brown splat where a pouch of snus was spat out.</summary>
    public void PlaySpit(Vector3 point, Vector3 normal)
    {
        if (decalMaterial == null) return;
        if (spitMaterial == null)
        {
            spitMaterial = new Material(decalMaterial);
            spitMaterial.SetColor("_BaseColor", spitColor);
        }
        Decal("Spit", point, normal, null, spitMaterial, spitSize);
    }

    private void Decal(string name, Vector3 point, Vector3 normal, Transform surface,
                       Material material, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Destroy(go.GetComponent<Collider>());

        go.transform.position = point + normal * 0.01f;
        go.transform.rotation = Quaternion.LookRotation(-normal);
        go.transform.localScale = Vector3.one * size;

        // Stick to whatever was hit so holes ride moving surfaces.
        if (surface != null) go.transform.SetParent(surface, true);

        var r = go.GetComponent<Renderer>();
        r.material = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        decals.Enqueue(go);
        while (decals.Count > maxDecals)
        {
            var old = decals.Dequeue();
            if (old != null) Destroy(old);
        }

        Destroy(go, decalSeconds);
    }
}
