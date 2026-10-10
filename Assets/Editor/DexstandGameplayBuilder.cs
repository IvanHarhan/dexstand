using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

/// <summary>
/// Builds the playable layer on top of the assembled scene: materials for the
/// untextured models, shootable targets, the touch control canvas, a crosshair,
/// and the weapon wiring that makes hits register.
/// </summary>
public static class DexstandGameplayBuilder
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string MaterialDir = "Assets/Materials";
    private const string DecalTexturePath = "Assets/Textures/BulletHole.png";

    private static readonly Vector2[] TargetSpots =
    {
        new Vector2(  0f,  8f), new Vector2(  6f,  5f), new Vector2( -6f,  5f),
        new Vector2( 10f, -2f), new Vector2(-10f, -2f), new Vector2(  0f, -8f),
    };

    [MenuItem("Dexstand/Собрать геймплей")]
    public static void BuildGameplay()
    {
        var mats = BuildMaterials();
        BuildPlayerWiring(mats);
        BuildConsumables();
        BuildScene(mats);
        AssetDatabase.SaveAssets();
        Debug.Log("[Gameplay] Done.");
    }

    // ------------------------------------------------------------------ materials

    private class Mats
    {
        public Material Gun, Skin, Target, Tracer, Decal, Flash;
    }

    private const string GlowTexturePath = "Assets/Textures/MuzzleGlow.png";

    /// <summary>
    /// A soft radial glow, generated rather than shipped. Drawn on a quad with
    /// a flat colour the flash reads as a yellow square; it needs an alpha that
    /// falls off from the centre and a few spikes to look like fire.
    /// </summary>
    private static Texture2D BuildGlowTexture()
    {
        const int size = 128;

        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(GlowTexturePath);
        if (existing != null) return existing;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var centre = new Vector2(size * 0.5f, size * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x, y) - centre;
                float distance = p.magnitude / (size * 0.5f);

                // Round core plus a four-pointed star, both fading out.
                float core = Mathf.Clamp01(1f - distance);
                core = core * core * core;

                float angle = Mathf.Atan2(p.y, p.x);
                float spikes = Mathf.Pow(Mathf.Abs(Mathf.Cos(angle * 2f)), 8f);
                float star = Mathf.Clamp01(1f - distance * 0.8f) * spikes * 0.6f;

                float alpha = Mathf.Clamp01(core + star);
                tex.SetPixel(x, y, new Color(1f, 0.95f, 0.75f, alpha));
            }
        }

        tex.Apply();

        if (!AssetDatabase.IsValidFolder("Assets/Textures"))
            AssetDatabase.CreateFolder("Assets", "Textures");

        System.IO.File.WriteAllBytes(GlowTexturePath, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(GlowTexturePath);

        var importer = AssetImporter.GetAtPath(GlowTexturePath) as TextureImporter;
        if (importer != null)
        {
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        Object.DestroyImmediate(tex);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(GlowTexturePath);
    }

    private static Mats BuildMaterials()
    {
        if (!AssetDatabase.IsValidFolder(MaterialDir))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var m = new Mats
        {
            Gun = Lit("GunMetal", new Color(0.13f, 0.14f, 0.16f), 0.7f, 0.45f),
            Skin = Lit("Skin", new Color(0.83f, 0.66f, 0.52f), 0f, 0.25f),
            Target = Lit("TargetBody", new Color(0.75f, 0.2f, 0.2f), 0f, 0.3f),
            Tracer = Unlit("Tracer", new Color(1f, 0.85f, 0.35f, 0.9f), null),
            Flash = Unlit("MuzzleFlash", new Color(1f, 0.92f, 0.6f, 1f), BuildGlowTexture()),
            Decal = Unlit("BulletHole", Color.white,
                          AssetDatabase.LoadAssetAtPath<Texture2D>(DecalTexturePath)),
        };
        return m;
    }

    private static Material Lit(string name, Color color, float metallic, float smoothness)
    {
        var path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }

        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Material Unlit(string name, Color color, Texture2D tex)
    {
        var path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(mat, path);
        }

        mat.SetColor("_BaseColor", color);
        if (tex != null) mat.SetTexture("_BaseMap", tex);

        // URP needs the surface type spelled out when set from script.
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;

        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ------------------------------------------------------------------ player

    private static void BuildPlayerWiring(Mats mats)
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var all = root.GetComponentsInChildren<Transform>(true);

            // Paint the imported model: it ships with no textures at all.
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool isHand = r.name.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isGun = r.name.IndexOf("gun", System.StringComparison.OrdinalIgnoreCase) >= 0
                           || r.name.IndexOf("rifle", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (isHand) r.sharedMaterial = mats.Skin;
                else if (isGun) r.sharedMaterial = mats.Gun;
            }

            var effects = root.GetComponent<ImpactEffects>();
            if (effects == null) effects = root.AddComponent<ImpactEffects>();
            effects.tracerMaterial = mats.Tracer;
            effects.decalMaterial = mats.Decal;
            effects.flashMaterial = mats.Flash;

            // Every effect size is in metres, so they have to follow the
            // player's stature. At 0.6m tall a default-sized muzzle flash is
            // wider than the field of view and whites out the screen.
            float ratio = DexstandPlayerBuilder.PlayerHeight / 1.8f;
            effects.flashSize = 0.055f * ratio;
            effects.flashLightRange = 2.5f * ratio;
            effects.tracerWidth = 0.02f * ratio;
            effects.decalSize = 0.15f * ratio;

            var cam = root.GetComponentInChildren<Camera>(true);
            EnablePostProcessing(cam);

            var weapon = root.GetComponentInChildren<WeaponController>(true);
            if (weapon != null)
            {
                weapon.effects = effects;
                weapon.shootPoint = BuildMuzzle(all, cam);
                weapon.viewmodel = root.GetComponentInChildren<WeaponBob>(true);
                EditorUtility.SetDirty(weapon);
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[Gameplay] Player wiring saved.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>Puts a muzzle transform at the front tip of the gun mesh.</summary>
    private static Transform BuildMuzzle(Transform[] all, Camera cam)
    {
        // The loadout already built a muzzle per weapon; prefer the equipped
        // one, otherwise tracers leave from the placeholder and look like they
        // come out of the player's face.
        var fromLoadout = all.FirstOrDefault(t => t.name == "Muzzle");
        if (fromLoadout != null) return fromLoadout;

        var arms = all.FirstOrDefault(t => t.name == "Arms");
        if (arms == null) return cam != null ? cam.transform : null;

        var gunRenderer = arms.GetComponentsInChildren<Renderer>(true)
            .FirstOrDefault(r => r.name.IndexOf("gun", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (gunRenderer == null) return cam != null ? cam.transform : null;

        var existing = arms.Find("Muzzle");
        var muzzle = existing != null ? existing : new GameObject("Muzzle").transform;
        muzzle.SetParent(arms, false);
        muzzle.name = "Muzzle";

        var b = gunRenderer.bounds;
        muzzle.position = new Vector3(b.center.x, b.center.y, b.max.z);
        return muzzle;
    }

    // ------------------------------------------------------------------ scene

    private static void BuildScene(Mats mats)
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        BuildTargets(mats);
        BuildLook();
        BuildUI();
        WireInput();
        PlaceCar();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Gameplay] Scene saved.");
    }

    private const int WantedTargets = 6;

    private const string PackTargetPath =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Prefabs/Damageables/P_LPSP_DMG_Target.prefab";

    // Distances are expressed in player heights, not metres, so the arena keeps
    // the same feel whatever stature the character is built at.
    private const float StatureReference = 1.8f;
    private static float StatureRatio => DexstandPlayerBuilder.PlayerHeight / StatureReference;
    private static float MinTargetSpacing => 4f * StatureRatio;

    /// <summary>
    /// Puts the URP asset back on the graphics and quality settings. Importing
    /// a package that ships its own ProjectSettings replaced ours, which left
    /// every quality level without a render pipeline: URP materials then render
    /// magenta and the lighting goes flat and blown out.
    /// </summary>
    public static void RepairRenderPipeline()
    {
        var mobile = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(
            "Assets/Settings/Mobile_RPAsset.asset");
        var pc = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(
            "Assets/Settings/PC_RPAsset.asset");

        var chosen = mobile != null ? mobile : pc;
        if (chosen == null)
        {
            Debug.LogError("[Repair] No URP asset found in Assets/Settings.");
            return;
        }

        UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = chosen;

        int levels = QualitySettings.names.Length;
        int current = QualitySettings.GetQualityLevel();
        for (int i = 0; i < levels; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = chosen;
        }
        QualitySettings.SetQualityLevel(current, false);

        // The same stray package import that wiped the quality levels also took
        // the bundle id with it, which would have shipped the next APK under
        // someone else's name.
        PlayerSettings.SetApplicationIdentifier(
            UnityEditor.Build.NamedBuildTarget.Android, "com.dexstand.shooter");

        AssetDatabase.SaveAssets();
        Debug.Log($"[Repair] URP asset '{chosen.name}' assigned as default and to " +
                  $"all {levels} quality levels; bundle id restored.");
    }

    private const string ProfilePath = "Assets/Settings/BodycamProfile.asset";
    private const string GradeProfilePath = "Assets/Settings/ColourProfile.asset";

    /// <summary>
    /// The picture, on two volumes. The colour grade is the game's own look and
    /// stays as it is. The bodycam — vignette, grain, fringing, the wide-lens
    /// bulge — has a volume of its own, whose weight is the slider in the pause
    /// menu. All four are drawn inside URP's one uber pass, which is what keeps
    /// them nearly free on a phone. Bloom is not — it blurs in passes of its
    /// own — so it is kept faint (check its cost on the phone). Bloom and motion
    /// blur are switches in the pause menu (<see cref="GraphicsOptions"/>).
    /// </summary>
    private static void BuildLook()
    {
        var grade = NewProfile(GradeProfilePath);
        var colour = AddOverride<ColorAdjustments>(grade);
        colour.saturation.Override(10f);
        colour.contrast.Override(16f);
        colour.postExposure.Override(0.3f);
        // a filmic curve and a slightly warm white instead of the flat, over-saturated look the region map had
        // (the user, 2026-09-25: "видно, что это просто 3D-модель"). ACES was tried 2026-09-27: its hue skew
        // turned the orthophoto's pale meadows khaki, so the curve stays Neutral and the contrast does the work
        AddOverride<Tonemapping>(grade).mode.Override(TonemappingMode.Neutral);
        var white = AddOverride<WhiteBalance>(grade);
        white.temperature.Override(4f);
        var smh = AddOverride<ShadowsMidtonesHighlights>(grade);
        smh.shadows.Override(new Vector4(0.97f, 0.99f, 1.05f, 0f));
        smh.highlights.Override(new Vector4(1.03f, 1.01f, 0.97f, 0f));
        var bloom = AddOverride<Bloom>(grade);
        bloom.threshold.Override(1.05f);
        bloom.intensity.Override(0.25f);
        bloom.scatter.Override(0.6f);
        // off until the player switches it on; objects too, so the viewmodel turning with the camera stays sharp
        var blur = AddOverride<MotionBlur>(grade);
        blur.mode.Override(MotionBlurMode.CameraAndObjects);
        blur.quality.Override(MotionBlurQuality.Low);
        blur.intensity.Override(0.35f);
        blur.clamp.Override(0.05f);
        blur.active = false;

        var bodycam = NewProfile(ProfilePath);

        var vignette = AddOverride<Vignette>(bodycam);
        vignette.intensity.Override(AtFullWeight(0.34f, 0f));
        vignette.smoothness.Override(AtFullWeight(0.45f, 0.2f));

        var grain = AddOverride<FilmGrain>(bodycam);
        grain.type.Override(FilmGrainLookup.Medium1);
        grain.intensity.Override(AtFullWeight(0.35f, 0f));
        grain.response.Override(AtFullWeight(0.75f, 0.8f));

        var fringing = AddOverride<ChromaticAberration>(bodycam);
        fringing.intensity.Override(AtFullWeight(0.18f, 0f));

        var lens = AddOverride<LensDistortion>(bodycam);
        lens.intensity.Override(AtFullWeight(-0.18f, 0f));
        lens.scale.Override(AtFullWeight(1.05f, 1f));

        EditorUtility.SetDirty(grade);
        EditorUtility.SetDirty(bodycam);
        AssetDatabase.SaveAssets();

        var gradeVolume = GlobalVolume("Global Volume", grade);
        if (gradeVolume.GetComponent<GraphicsOptions>() == null) gradeVolume.gameObject.AddComponent<GraphicsOptions>();

        var bodycamVolume = GlobalVolume("Bodycam Volume", bodycam);
        bodycamVolume.weight = BodycamIntensity.StartWeight;
        if (bodycamVolume.GetComponent<BodycamIntensity>() == null)
            bodycamVolume.gameObject.AddComponent<BodycamIntensity>();

        Debug.Log("[Gameplay] Colour grade and bodycam applied, on separate volumes.");
    }

    /// <summary>
    /// A bodycam value written for the slider at 100%. A volume at partial
    /// weight blends each value from the effect's neutral one, and the look was
    /// tuned at <see cref="BodycamIntensity.DefaultWeight"/> — so this is the
    /// value that lands exactly on it there.
    /// </summary>
    private static float AtFullWeight(float tuned, float neutral) =>
        neutral + (tuned - neutral) / BodycamIntensity.DefaultWeight;

    /// <summary>A profile started from scratch, replacing whatever was at the path.</summary>
    private static VolumeProfile NewProfile(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(path) != null) AssetDatabase.DeleteAsset(path);

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);
        return profile;
    }

    private static Volume GlobalVolume(string name, VolumeProfile profile)
    {
        var go = GameObject.Find(name);
        if (go == null) go = new GameObject(name);

        var volume = go.GetComponent<Volume>();
        if (volume == null) volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;
        return volume;
    }

    /// <summary>
    /// Only the picture and its slider. A full gameplay build also rebuilds the
    /// targets, the whole HUD and the player's wiring, which this has no reason
    /// to touch.
    /// </summary>
    public static void BuildBodycam()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        BuildLook();

        var pause = GameObject.Find("GameUI")?.transform.Find("PausePanel");
        if (pause != null) { BuildBodycamSlider(pause); BuildGraphicsToggles(pause); }
        else Debug.LogWarning("[Gameplay] No pause panel — run gameplay to build the menus.");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    /// <summary>
    /// Adds an effect to the profile AND writes it into the asset file.
    /// VolumeProfile.Add only builds the object in memory: the profile saves a
    /// list of null references, so the look survives until the next domain
    /// reload and then silently disappears.
    /// </summary>
    private static T AddOverride<T>(VolumeProfile profile) where T : VolumeComponent
    {
        var component = ScriptableObject.CreateInstance<T>();
        component.name = typeof(T).Name;
        component.hideFlags = HideFlags.HideInHierarchy;
        component.active = true;

        profile.components.Add(component);
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    /// <summary>Post-processing is off per-camera in URP unless asked for.</summary>
    private static void EnablePostProcessing(Camera cam)
    {
        if (cam == null) return;
        var data = cam.GetUniversalAdditionalCameraData();
        if (data != null) data.renderPostProcessing = true;
    }

    private static void BuildTargets(Mats mats)
    {
        var parent = GameObject.Find("Targets");
        if (parent != null) Object.DestroyImmediate(parent);
        parent = new GameObject("Targets");

        Physics.SyncTransforms();

        if (!GroundAt(0f, 0f, out float spawnFloor))
        {
            Debug.LogWarning("[Gameplay] No ground under the spawn — skipping targets.");
            return;
        }

        var chosen = new System.Collections.Generic.List<Vector3>();
        int probed = 0, rejectedHeight = 0, rejectedHeadroom = 0, rejectedSpacing = 0;

        float minRadius = 5f * StatureRatio;
        float maxRadius = 14f * StatureRatio;
        float step = 2.5f * StatureRatio;

        for (float radius = minRadius; radius <= maxRadius && chosen.Count < WantedTargets; radius += step)
        {
            for (int i = 0; i < 12 && chosen.Count < WantedTargets; i++)
            {
                float angle = i * Mathf.PI * 2f / 12f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                probed++;

                if (!GroundNear(x, z, spawnFloor, out float y)) continue;

                // Courtyard level only. A tolerance near the player's height lets
                // rooftops and crate stacks through, and targets end up perched
                // above the fight instead of in it.
                if (Mathf.Abs(y - spawnFloor) > 0.75f)
                {
                    rejectedHeight++;
                    continue;
                }

                // Room to stand.
                if (Physics.Raycast(new Vector3(x, y + 0.05f, z), Vector3.up,
                                    DexstandPlayerBuilder.PlayerHeight * 1.2f))
                {
                    rejectedHeadroom++;
                    continue;
                }

                var spot = new Vector3(x, y, z);
                if (chosen.Any(c => Vector3.Distance(c, spot) < MinTargetSpacing))
                {
                    rejectedSpacing++;
                    continue;
                }

                chosen.Add(spot);
            }
        }

        // The pack's pop-up targets, not armed patrols: decided 2026-09-23. They
        // drop when hit and stand back up on their own, with the pack's sounds.
        var targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PackTargetPath);
        if (targetPrefab == null)
        {
            Debug.LogError($"[Gameplay] No pack target at {PackTargetPath}.");
            return;
        }

        // The pack is built for a 1.8 m world.
        float ratio = DexstandPlayerBuilder.PlayerHeight / 1.8f;

        for (int i = 0; i < chosen.Count; i++)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(targetPrefab);
            go.transform.SetParent(parent.transform);
            go.transform.position = chosen[i];
            go.transform.localScale = Vector3.one * ratio;

            go.name = $"Target_{i}";

            // Face the middle of the yard, where the player starts.
            go.transform.rotation = Quaternion.LookRotation(
                new Vector3(-chosen[i].x, 0f, -chosen[i].z).normalized, Vector3.up);

            Debug.Log($"[Gameplay] Target_{i} at ({chosen[i].x:F1}, {chosen[i].y:F2}, {chosen[i].z:F1}).");
        }

        Debug.Log($"[Gameplay] {chosen.Count} targets placed (floor y={spawnFloor:F2}, " +
                  $"probed {probed}, rejected: height {rejectedHeight}, " +
                  $"headroom {rejectedHeadroom}, spacing {rejectedSpacing}).");
    }

    /// <summary>Topmost non-safety-net surface under a point — used for the spawn probe.</summary>
    private static bool GroundAt(float x, float z, out float y)
    {
        y = 0f;
        var hits = Physics.RaycastAll(new Vector3(x, 200f, z), Vector3.down, 400f);
        float best = float.MinValue;
        foreach (var h in hits)
        {
            if (h.collider.gameObject.name == "SafetyFloor") continue;
            if (h.point.y > best) best = h.point.y;
        }
        if (best == float.MinValue) return false;
        y = best;
        return true;
    }

    /// <summary>
    /// The surface nearest the player's own floor level. Picking the topmost hit
    /// lands targets in tree canopies; picking the lowest puts them under the
    /// map. The storey the player walks on is the one closest to their feet.
    /// </summary>
    private static bool GroundNear(float x, float z, float referenceY, out float y)
    {
        y = 0f;
        var hits = Physics.RaycastAll(new Vector3(x, 200f, z), Vector3.down, 400f);

        float bestDistance = float.MaxValue;
        bool found = false;
        foreach (var h in hits)
        {
            if (h.collider.gameObject.name == "SafetyFloor") continue;
            float d = Mathf.Abs(h.point.y - referenceY);
            if (d < bestDistance)
            {
                bestDistance = d;
                y = h.point.y;
                found = true;
            }
        }
        return found;
    }

    private static void BuildUI()
    {
        var old = GameObject.Find("GameUI");
        if (old != null) Object.DestroyImmediate(old);

        if (Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }

        var canvasGo = new GameObject("GameUI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // Everything the player sees while actually playing lives under one
        // object, so a menu can hide the lot with a single SetActive.
        var gameplayRoot = new GameObject("GameplayUI", typeof(RectTransform));
        gameplayRoot.transform.SetParent(canvasGo.transform, false);
        Stretch(gameplayRoot.GetComponent<RectTransform>());

        AddCrosshair(gameplayRoot.transform);

        var touch = new GameObject("TouchControls", typeof(RectTransform));
        touch.transform.SetParent(gameplayRoot.transform, false);
        Stretch(touch.GetComponent<RectTransform>());

        var mobile = touch.AddComponent<MobileInputService>();

        var joystick = AddZone(touch.transform, "JoystickArea",
            new Vector2(0f, 0f), new Vector2(0.5f, 1f), new Color(1f, 1f, 1f, 0.02f));
        var dyn = joystick.gameObject.AddComponent<DynamicJoystick>();
        dyn.background = AddCircle(joystick, "JoystickBG", 260f, new Color(1f, 1f, 1f, 0.18f));
        dyn.handle = AddCircle(dyn.background, "JoystickHandle", 110f, new Color(1f, 1f, 1f, 0.45f));

        // Stick on the left half, drag to look on the right — the one scheme.
        // The arrow pads and the switch between schemes are gone.
        var look = AddZone(touch.transform, "LookZone",
            new Vector2(0.5f, 0f), new Vector2(1f, 1f), new Color(1f, 1f, 1f, 0.02f));
        look.gameObject.AddComponent<LookZoneController>();

        var fire = AddButton(touch.transform, "FireButton",
            new Vector2(-220f, 180f), 200f, new Color(0.9f, 0.3f, 0.25f, 0.55f));
        fire.gameObject.AddComponent<FireButton>();

        var jump = AddButton(touch.transform, "JumpButton",
            new Vector2(-430f, 120f), 150f, new Color(0.35f, 0.65f, 0.95f, 0.55f));
        jump.gameObject.AddComponent<JumpButton>();

        // Above the fire button, where the thumb already is — an empty magazine
        // is the one thing a player needs to fix without looking.
        var reload = AddButton(touch.transform, "ReloadButton",
            new Vector2(-235f, 400f), 150f, new Color(0.95f, 0.78f, 0.25f, 0.55f));
        reload.gameObject.AddComponent<ReloadButton>();

        // Left of the fire button: held with the same hand that shoots, so the
        // sights come up without letting go of anything.
        var aim = AddButton(touch.transform, "AimButton",
            new Vector2(-430f, 330f), 150f, new Color(0.85f, 0.85f, 0.85f, 0.45f));
        aim.gameObject.AddComponent<AimButton>();

        // Private [SerializeField] links have to go through SerializedObject.
        Link(mobile, "moveJoystick", dyn);
        Link(look.gameObject.GetComponent<LookZoneController>(), "inputService", mobile);
        Link(fire.gameObject.GetComponent<FireButton>(), "inputService", mobile);
        Link(jump.gameObject.GetComponent<JumpButton>(), "inputService", mobile);
        Link(reload.gameObject.GetComponent<ReloadButton>(), "inputService", mobile);
        Link(aim.gameObject.GetComponent<AimButton>(), "inputService", mobile);

        var visibility = canvasGo.AddComponent<MobileUIVisibility>();
        Link(visibility, "touchControls", touch);

        // Touch on phones, keyboard and mouse on a computer — WASD, mouse to
        // look, left button to fire, right to aim, R to reload, space to jump.
        visibility.mode = MobileUIVisibility.ControlMode.Auto;

        BuildMenus(canvasGo, gameplayRoot);
    }

    // ------------------------------------------------------------------ menus

    private static void BuildMenus(GameObject canvas, GameObject gameplayRoot)
    {
        var menu = canvas.AddComponent<GameMenu>();

        var main = AddPanel(canvas.transform, "MainPanel", new Color(0.05f, 0.06f, 0.08f, 0.94f));
        AddLabel(main, "DEXSTAND", 110, new Vector2(0f, 300f), new Vector2(1200f, 160f));
        AddLabel(main, "военный шутер", 42, new Vector2(0f, 200f), new Vector2(1200f, 80f));

        var single = AddMenuButton(main, "Одиночная игра", new Vector2(0f, 40f));
        var multi = AddMenuButton(main, "Мультиплеер", new Vector2(0f, -70f));
        var controlsMain = AddMenuButton(main, "Управление", new Vector2(0f, -180f));
        var quit = AddMenuButton(main, "Выход", new Vector2(0f, -290f));

        var pause = AddPanel(canvas.transform, "PausePanel", new Color(0.05f, 0.06f, 0.08f, 0.82f));
        AddLabel(pause, "ПАУЗА", 90, new Vector2(0f, 220f), new Vector2(1000f, 140f));
        var resume = AddMenuButton(pause, "Продолжить", new Vector2(0f, 80f));
        var controlsPause = AddMenuButton(pause, "Управление", new Vector2(0f, -30f));
        var toMenu = AddMenuButton(pause, "В главное меню", new Vector2(0f, -140f));
        BuildBodycamSlider(pause);
        BuildGraphicsToggles(pause);

        // PC or phone controls, switchable from both menus: the stick and the
        // on-screen buttons for touch, keyboard and mouse for a computer.
        var visibility = canvas.GetComponent<MobileUIVisibility>();
        if (visibility != null)
        {
            Bind(controlsMain, visibility, nameof(MobileUIVisibility.Toggle));
            Bind(controlsPause, visibility, nameof(MobileUIVisibility.Toggle));

            var so = new SerializedObject(visibility);
            var labels = so.FindProperty("labels");
            labels.arraySize = 2;
            labels.GetArrayElementAtIndex(0).objectReferenceValue = controlsMain.GetComponentInChildren<Text>();
            labels.GetArrayElementAtIndex(1).objectReferenceValue = controlsPause.GetComponentInChildren<Text>();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Pause control for touch, where there is no Esc key.
        var pauseBtn = AddMenuButton(gameplayRoot.transform, "II", new Vector2(0f, 0f), 120f, 90f);
        var pauseRt = pauseBtn.GetComponent<RectTransform>();
        pauseRt.anchorMin = pauseRt.anchorMax = new Vector2(1f, 1f);
        pauseRt.pivot = new Vector2(1f, 1f);
        pauseRt.anchoredPosition = new Vector2(-40f, -40f);

        Bind(single, menu, nameof(GameMenu.StartSinglePlayer));
        Bind(multi, menu, nameof(GameMenu.StartMultiplayer));
        Bind(quit, menu, nameof(GameMenu.Quit));
        Bind(resume, menu, nameof(GameMenu.Resume));
        Bind(toMenu, menu, nameof(GameMenu.LeaveToMenu));
        Bind(pauseBtn, menu, nameof(GameMenu.Pause));

        var launcher = GameObject.Find("NetworkManager")?.GetComponent<NetworkLauncher>();
        if (launcher != null) launcher.startOnPlay = false;

        BuildWheelUI(gameplayRoot.transform);

        if (canvas.GetComponent<PlaytestCapture>() == null)
            canvas.AddComponent<PlaytestCapture>();

        Link(menu, "mainPanel", main.gameObject);
        Link(menu, "pausePanel", pause.gameObject);
        Link(menu, "gameplayUI", gameplayRoot);
        Link(menu, "launcher", launcher);

        Debug.Log("[Gameplay] Menus built (main, pause).");
    }

    // ------------------------------------------------------------------ car

    /// <summary>Just the car, for when the rest of the scene should be left alone.</summary>
    public static void BuildCar()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        PlaceCar();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    /// <summary>
    /// The car as a networked scene object, so Fusion brings it up for every
    /// player with the match. It builds its own body when the game runs, and the
    /// host parks it somewhere open.
    /// </summary>
    private static void PlaceCar()
    {
        var go = GameObject.Find("TestCar");
        if (go == null) go = new GameObject("TestCar");
        go.transform.SetPositionAndRotation(new Vector3(0f, 2f, 6f), Quaternion.identity);

        if (go.GetComponent<Fusion.NetworkObject>() == null) go.AddComponent<Fusion.NetworkObject>();
        if (go.GetComponent<Fusion.NetworkTransform>() == null) go.AddComponent<Fusion.NetworkTransform>();
        // Replaced rather than kept: the scene saves the component's numbers, and
        // an old copy went on driving at the old top speed and steering after
        // the code's own values had changed.
        var old = go.GetComponent<SimpleCar>();
        if (old != null) Object.DestroyImmediate(old);
        go.AddComponent<SimpleCar>();

        Debug.Log("[Gameplay] Car placed as a networked scene object.");
    }

    /// <summary>The police (wanted stars, patrol cars, officers) as a networked scene object, like the car.</summary>
    public static void BuildPolice()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var go = GameObject.Find("Police");
        if (go == null) go = new GameObject("Police");
        if (go.GetComponent<Fusion.NetworkObject>() == null) go.AddComponent<Fusion.NetworkObject>();
        // replaced rather than kept, for the same reason as the car's
        var old = go.GetComponent<Police>();
        if (old != null) Object.DestroyImmediate(old);
        go.AddComponent<Police>();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Gameplay] Police placed as a networked scene object.");
    }

    // ------------------------------------------------------------------ wheels

    private const string XRayShaderPath = "Assets/Shaders/XRay.shader";

    /// <summary>
    /// The radial wheels and what they need: the consumables on the player, and
    /// the wheels with their buttons on the HUD in place of the old weapon menu.
    /// Leaves the rest of the scene and the player alone.
    /// </summary>
    public static void BuildWheels()
    {
        BuildConsumables();

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var gameplayRoot = GameObject.Find("GameUI")?.transform.Find("GameplayUI");
        if (gameplayRoot == null)
        {
            Debug.LogWarning("[Gameplay] No GameplayUI — run gameplay to build the HUD.");
            return;
        }

        BuildWheelUI(gameplayRoot);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    /// <summary>The consumables component on the player, with its see-through material.</summary>
    private static void BuildConsumables()
    {
        Material xray = null;
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(XRayShaderPath);
        if (shader != null)
        {
            var path = $"{MaterialDir}/XRay.mat";
            xray = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (xray == null)
            {
                xray = new Material(shader);
                AssetDatabase.CreateAsset(xray, path);
            }
            xray.shader = shader;
            xray.SetColor("_BaseColor", new Color(1f, 0.45f, 0.1f, 0.6f));
            EditorUtility.SetDirty(xray);
        }
        else
        {
            Debug.LogWarning($"[Gameplay] No shader at {XRayShaderPath} — enemies will not show through walls.");
        }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var effects = root.GetComponent<PlayerEffects>();
            if (effects == null) effects = root.AddComponent<PlayerEffects>();
            effects.xrayMaterial = xray;
            EditorUtility.SetDirty(effects);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[Gameplay] Consumables on the player.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// The three wheels, where every control scheme can reach them, and on
    /// touch their buttons in a column above the fire cluster. A PC holds
    /// Q, E and B instead.
    /// </summary>
    private static void BuildWheelUI(Transform gameplayRoot)
    {
        foreach (var name in new[] { "WeaponMenu", "Wheels" })
        {
            var old = gameplayRoot.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
        }

        var wheels = new GameObject("Wheels", typeof(RectTransform));
        wheels.transform.SetParent(gameplayRoot, false);
        Stretch(wheels.GetComponent<RectTransform>());

        var touch = gameplayRoot.Find("TouchControls");
        var mobile = touch != null ? touch.GetComponent<MobileInputService>() : null;

        var layout = new (RadialWheel.Kind Kind, string Label, float Height)[]
        {
            (RadialWheel.Kind.Weapons, "ОРУЖИЕ", 560f),
            (RadialWheel.Kind.Items, "ПРЕДМЕТЫ", 720f),
            (RadialWheel.Kind.Emotes, "ЭМОЦИИ", 880f),
        };

        foreach (var entry in layout)
        {
            var go = new GameObject($"Wheel{entry.Kind}", typeof(RectTransform));
            go.transform.SetParent(wheels.transform, false);
            Stretch(go.GetComponent<RectTransform>());
            go.AddComponent<RadialWheel>().kind = entry.Kind;

            if (touch == null) continue;

            string buttonName = $"{entry.Kind}WheelButton";
            var oldButton = touch.Find(buttonName);
            if (oldButton != null) Object.DestroyImmediate(oldButton.gameObject);

            var button = AddButton(touch, buttonName, new Vector2(-110f, entry.Height), 130f,
                                   new Color(0.22f, 0.26f, 0.32f, 0.75f));
            AddLabel(button, entry.Label, 22, Vector2.zero, new Vector2(130f, 130f));

            var hold = button.gameObject.AddComponent<WheelButton>();
            hold.wheel = entry.Kind;
            Link(hold, "inputService", mobile);
        }

        Debug.Log($"[Gameplay] Wheels built{(touch != null ? ", with touch buttons" : "")}.");
    }

    private static RectTransform AddPanel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Stretch(go.GetComponent<RectTransform>());

        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;

        return go.GetComponent<RectTransform>();
    }

    /// <summary>
    /// The bodycam strength, under the pause buttons: a caption and a slider in
    /// one holder, so a rebuild can replace both. <see cref="BodycamIntensity"/>
    /// on the volume does the work.
    /// </summary>
    private static void BuildBodycamSlider(Transform pause)
    {
        var old = pause.Find("Bodycam");
        if (old != null) Object.DestroyImmediate(old.gameObject);

        var holder = new GameObject("Bodycam", typeof(RectTransform));
        holder.transform.SetParent(pause, false);
        var holderRt = holder.GetComponent<RectTransform>();
        holderRt.anchorMin = holderRt.anchorMax = new Vector2(0.5f, 0.5f);
        holderRt.sizeDelta = new Vector2(620f, 130f);
        holderRt.anchoredPosition = new Vector2(0f, -290f);

        var caption = AddLabel(holder.transform, "Бодикам", 36, new Vector2(0f, 35f), new Vector2(620f, 50f));

        // Plain images like every other control here, sized for a thumb.
        var go = DefaultControls.CreateSlider(new DefaultControls.Resources());
        go.name = "Slider";
        go.transform.SetParent(holder.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(620f, 36f);
        rt.anchoredPosition = new Vector2(0f, -25f);

        go.transform.Find("Background").GetComponent<Image>().color = new Color(0.22f, 0.26f, 0.32f, 0.95f);
        go.transform.Find("Fill Area/Fill").GetComponent<Image>().color = new Color(0.62f, 0.7f, 0.5f, 1f);

        var slideArea = go.transform.Find("Handle Slide Area").GetComponent<RectTransform>();
        slideArea.offsetMin = new Vector2(28f, 0f);
        slideArea.offsetMax = new Vector2(-28f, 0f);
        var handle = go.transform.Find("Handle Slide Area/Handle").GetComponent<RectTransform>();
        handle.sizeDelta = new Vector2(56f, 28f);

        var slider = go.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = BodycamIntensity.StartWeight;

        var intensity = Object.FindFirstObjectByType<BodycamIntensity>();
        if (intensity == null) Debug.LogWarning("[Gameplay] No BodycamIntensity — the slider does nothing.");
        Link(intensity, "slider", slider);
        Link(intensity, "label", caption);
    }

    /// <summary>Bloom and motion blur switches, side by side under the bodycam slider; <see cref="GraphicsOptions"/> wires them.</summary>
    private static void BuildGraphicsToggles(Transform pause)
    {
        foreach (var name in new[] { "Button_Bloom", "Button_Blur" })
        {
            var old = pause.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
        }
        var bloom = AddMenuButton(pause, "Блум", new Vector2(-160f, -420f), 300f, 80f);
        var blur = AddMenuButton(pause, "Размытие", new Vector2(160f, -420f), 300f, 80f);
        bloom.name = "Button_Bloom";
        blur.name = "Button_Blur";

        var options = Object.FindFirstObjectByType<GraphicsOptions>();
        if (options == null) Debug.LogWarning("[Gameplay] No GraphicsOptions — the graphics switches do nothing.");
        Link(options, "bloomButton", bloom);
        Link(options, "blurButton", blur);
    }

    private static Text AddLabel(Transform parent, string text, int size,
                                 Vector2 offset, Vector2 area)
    {
        var go = new GameObject($"Label_{text}", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = area;
        rt.anchoredPosition = offset;

        var label = go.AddComponent<Text>();
        label.text = text;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = size;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.raycastTarget = false;
        return label;
    }

    private static Button AddMenuButton(Transform parent, string text, Vector2 offset,
                                        float width = 620f, float height = 96f)
    {
        var go = new GameObject($"Button_{text}", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = offset;

        var img = go.AddComponent<Image>();
        img.color = new Color(0.22f, 0.26f, 0.32f, 0.95f);

        var button = go.AddComponent<Button>();
        button.targetGraphic = img;

        AddLabel(go.transform, text, 40, Vector2.zero, new Vector2(width, height));
        return button;
    }

    /// <summary>
    /// Wires a click through UnityEventTools so the hook survives in the saved
    /// scene — a runtime AddListener would not be serialised.
    /// </summary>
    private static void Bind(Button button, GameMenu menu, string methodName)
    {
        var action = System.Delegate.CreateDelegate(
            typeof(UnityEngine.Events.UnityAction), menu, methodName)
            as UnityEngine.Events.UnityAction;

        UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick, action);
    }

    private static void Bind(Button button, MobileUIVisibility visibility, string methodName)
    {
        var action = System.Delegate.CreateDelegate(
            typeof(UnityEngine.Events.UnityAction), visibility, methodName)
            as UnityEngine.Events.UnityAction;

        UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick, action);
    }

    private static void AddDPad(Transform parent, MobileInputService mobile,
                                string name, Vector2 direction, Vector2 offset,
                                DPadButton.PadTarget target)
    {
        var go = new GameObject($"Pad{name}", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(110f, 110f);
        rt.anchoredPosition = offset;

        var img = go.AddComponent<Image>();
        img.color = target == DPadButton.PadTarget.Look
            ? new Color(0.55f, 0.8f, 1f, 0.3f)
            : new Color(1f, 1f, 1f, 0.28f);
        img.raycastTarget = true;

        var pad = go.AddComponent<DPadButton>();
        pad.direction = direction;
        pad.target = target;
        Link(pad, "inputService", mobile);
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void AddCrosshair(Transform parent)
    {
        var go = new GameObject("Crosshair", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(8f, 8f);

        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.85f);
        img.raycastTarget = false;
    }

    private static RectTransform AddZone(Transform parent, string name,
                                         Vector2 anchorMin, Vector2 anchorMax, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        return rt;
    }

    private static RectTransform AddCircle(Transform parent, string name, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = Vector2.zero;

        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return rt;
    }

    private static RectTransform AddButton(Transform parent, string name,
                                           Vector2 offset, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = offset;

        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        return rt;
    }

    /// <summary>
    /// Feeds both input sources into one router and hands that to the launcher,
    /// so the same build answers to keyboard and to touch.
    /// </summary>
    private static void WireInput()
    {
        var manager = GameObject.Find("NetworkManager");
        if (manager == null)
        {
            Debug.LogWarning("[Gameplay] No NetworkManager — run Собрать сцену first.");
            return;
        }

        var desktop = manager.GetComponent<DesktopInputService>();
        if (desktop == null) desktop = manager.AddComponent<DesktopInputService>();

        var mobile = Object.FindFirstObjectByType<MobileInputService>();

        var router = manager.GetComponent<InputRouter>();
        if (router == null) router = manager.AddComponent<InputRouter>();

        var sources = new SerializedObject(router).FindProperty("sources");
        sources.arraySize = mobile != null ? 2 : 1;
        sources.GetArrayElementAtIndex(0).objectReferenceValue = desktop;
        if (mobile != null) sources.GetArrayElementAtIndex(1).objectReferenceValue = mobile;
        sources.serializedObject.ApplyModifiedPropertiesWithoutUndo();

        var launcher = manager.GetComponent<NetworkLauncher>();
        if (launcher != null) Link(launcher, "inputServiceObject", router);

        // The mode switcher has to be able to turn desktop input off.
        var visibility = Object.FindFirstObjectByType<MobileUIVisibility>();
        if (visibility != null) Link(visibility, "desktopInput", desktop);

        Debug.Log($"[Gameplay] Input routed (desktop{(mobile != null ? " + mobile" : "")}), " +
                  $"mode {(visibility != null ? visibility.mode.ToString() : "none")}.");
    }

    private static void Link(Object target, string field, Object value)
    {
        if (target == null) return;
        var so = new SerializedObject(target);
        var prop = so.FindProperty(field);
        if (prop == null) return;
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
