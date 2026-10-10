using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Measures the map so scale decisions come from the geometry instead of guesses.
/// </summary>
public static class DexstandDiagnostics
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    /// <summary>Fires the same downward probes the target placer uses and reports each one.</summary>
    public static void ProbeGround()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Physics.SyncTransforms();

        var map = GameObject.Find("Map");
        if (map != null)
        {
            var b = new Bounds(map.transform.position, Vector3.zero);
            foreach (var r in map.GetComponentsInChildren<Renderer>(true)) b.Encapsulate(r.bounds);
            Debug.Log($"[Probe] Map scale {map.transform.localScale.x:F2}, " +
                      $"bounds size {b.size}, y {b.min.y:F2}..{b.max.y:F2}");
            Debug.Log($"[Probe] Mesh colliders: {map.GetComponentsInChildren<MeshCollider>(true).Length}");
        }

        foreach (var radius in new[] { 0f, 5f, 7.5f, 10f, 12.5f, 20f, 30f })
        {
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI * 0.5f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;

                var hits = Physics.RaycastAll(new Vector3(x, 50f, z), Vector3.down, 100f)
                                  .Where(h => h.collider.gameObject.name != "SafetyFloor")
                                  .OrderBy(h => h.point.y)
                                  .ToArray();

                string what = hits.Length == 0
                    ? "MISS"
                    : $"{hits.Length} hits, lowest {hits[0].point.y:F2} on {hits[0].collider.name}";
                Debug.Log($"[Probe] r={radius,5:F1} ({x,6:F1},{z,6:F1}) -> {what}");

                if (radius == 0f) break;
            }
        }
    }

    /// <summary>
    /// Looks for scale curves in the animation clips. A Generic clip that
    /// animates m_LocalScale overrides whatever size the prefab was built at,
    /// the moment the clip starts playing.
    /// </summary>
    public static void InspectClips()
    {
        const string animationsPath = "Assets/Models/Character/Animations.fbx";

        var clips = AssetDatabase.LoadAllAssetsAtPath(animationsPath)
                                 .OfType<AnimationClip>()
                                 .Where(c => !c.name.StartsWith("__preview__"))
                                 .ToArray();

        Debug.Log($"[Clips] {clips.Length} clips in {animationsPath}");

        foreach (var clip in clips.Take(6))
        {
            var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
            var scaleBindings = bindings.Where(b => b.propertyName.Contains("m_LocalScale")).ToArray();
            var positionBindings = bindings.Where(b => b.propertyName.Contains("m_LocalPosition")).ToArray();

            Debug.Log($"[Clips] {clip.name}: {bindings.Length} curves, " +
                      $"{scaleBindings.Length} scale, {positionBindings.Length} position");

            foreach (var b in scaleBindings.Take(3))
            {
                var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, b);
                float first = curve.length > 0 ? curve.keys[0].value : float.NaN;
                Debug.Log($"[Clips]    scale on '{b.path}' {b.propertyName} = {first:F3}");
            }

            foreach (var b in positionBindings.Take(2))
            {
                var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, b);
                float first = curve.length > 0 ? curve.keys[0].value : float.NaN;
                Debug.Log($"[Clips]    pos on '{b.path}' {b.propertyName} = {first:F3}");
            }
        }
    }

    /// <summary>
    /// Checks that the clip curve paths actually exist on the character. A
    /// clip whose paths do not resolve plays silently and nothing moves.
    /// </summary>
    public static void VerifyRig()
    {
        // The player's body runs on its own Mixamo clips, in a different folder
        // and against a different model, so it is checked first and separately —
        // the rest of this method only ever looks at the Quaternius targets.
        DexstandBodyAnimations.Report();

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Clips/Walk.anim");
        if (clip == null)
        {
            Debug.LogError("[Rig] Walk.anim not found — run Собрать персонажей first.");
            return;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/TargetCharacter.prefab");
        if (prefab == null)
        {
            Debug.LogError("[Rig] TargetCharacter prefab not found.");
            return;
        }

        var animator = prefab.GetComponentInChildren<Animator>(true);
        if (animator == null)
        {
            Debug.LogError("[Rig] No Animator on the target prefab.");
            return;
        }

        Debug.Log($"[Rig] Animator on '{animator.gameObject.name}', " +
                  $"controller '{(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "NONE")}'");

        var avatar = animator.avatar;
        Debug.Log(avatar == null
            ? "[Rig] avatar: NONE — the Animator will not run"
            : $"[Rig] avatar '{avatar.name}': valid={avatar.isValid}, human={avatar.isHuman}");

        if (avatar != null && avatar.isHuman)
        {
            // Humanoid retargets through the avatar, so clip paths are irrelevant.
            Debug.Log("[Rig] Humanoid rig — clip paths do not need to match.");
            return;
        }

        var animatorRoot = animator.transform;
        var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);

        int resolved = 0, missing = 0;
        var missingPaths = new System.Collections.Generic.List<string>();

        foreach (var path in bindings.Select(b => b.path).Distinct())
        {
            var found = string.IsNullOrEmpty(path) ? animatorRoot : animatorRoot.Find(path);
            if (found != null) resolved++;
            else
            {
                missing++;
                if (missingPaths.Count < 8) missingPaths.Add(path);
            }
        }

        Debug.Log($"[Rig] Walk.anim paths: {resolved} resolved, {missing} missing");
        foreach (var p in missingPaths) Debug.Log($"[Rig]   missing: '{p}'");

        Debug.Log("[Rig] model children: " + string.Join(", ",
            animatorRoot.Cast<Transform>().Select(t => t.name)));
    }

    /// <summary>
    /// Samples the walk clip onto the target and measures it. A character that
    /// stands correctly at rest but tips over once animated shows up here as a
    /// collapsed height, without needing to enter play mode.
    /// </summary>
    public static void SampleWalk()
    {
        DexstandBodyAnimations.Report();
        SampleClip("Walk");
        SampleClip("Death");
    }

    private static void SampleClip(string clipName)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/TargetCharacter.prefab");
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
            $"Assets/Animations/Clips/{clipName}.anim");
        if (prefab == null || clip == null)
        {
            Debug.LogError($"[Sample] Target prefab or {clipName}.anim missing.");
            return;
        }

        var instance = Object.Instantiate(prefab);
        instance.transform.position = Vector3.zero;
        instance.transform.rotation = Quaternion.identity;

        try
        {
            var rest = Measure(instance);
            Debug.Log($"[Sample] {clipName} rest: height {rest.size.y:F2}, " +
                      $"footprint {rest.size.x:F2} x {rest.size.z:F2}");

            foreach (var t in new[] { 0f, 0.5f, 0.99f })
            {
                clip.SampleAnimation(instance, t * clip.length);
                var b = Measure(instance);
                Debug.Log($"[Sample] {clipName} t={t:F2}: height {b.size.y:F2}, " +
                          $"footprint {b.size.x:F2} x {b.size.z:F2}, centre y {b.center.y:F2}");
            }

            var hips = instance.GetComponentsInChildren<Transform>(true)
                               .FirstOrDefault(x => x.name == "Hips");
            if (hips != null)
                Debug.Log($"[Sample] Hips world euler {hips.eulerAngles}");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static Bounds Measure(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    /// <summary>Reports the real world size of every target and of the player.</summary>
    public static void MeasureTargets()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var parent = GameObject.Find("Targets");
        if (parent == null)
        {
            Debug.LogError("[Targets] No Targets object in scene.");
            return;
        }

        foreach (Transform child in parent.transform)
        {
            var renderers = child.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.Log($"[Targets] {child.name}: no renderers");
                continue;
            }

            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

            Debug.Log($"[Targets] {child.name}: height {b.size.y:F2}m, " +
                      $"width {b.size.x:F2}m, feet y={b.min.y:F2}, " +
                      $"lossyScale {child.lossyScale.y:F4}");
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        if (prefab != null)
        {
            var cc = prefab.GetComponentInChildren<CharacterController>(true);
            if (cc != null)
                Debug.Log($"[Targets] Player capsule height {cc.height:F2}m, radius {cc.radius:F2}m");
        }
    }

    /// <summary>Dumps what a model actually contains: hierarchy, meshes, materials, clips.</summary>
    public static void InspectModels()
    {
        const string infima = "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Art";

        string[] paths =
        {
            infima + "/Meshes/Character/SK_FP_CH_Default_Cubic.fbx",
            infima + "/Meshes/Weapons/ARs/SK_AR_01.fbx",
            infima + "/Animations/Character/_Common/A_FP_PCH_Idle.fbx",
            infima + "/Animations/Character/ARs/A_FP_PCH_AR_01_Inspect.fbx",
        };

        foreach (var path in paths)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                Debug.Log($"[Model] {path}: MISSING");
                continue;
            }

            var instance = Object.Instantiate(asset);
            try
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds();
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview__")).Select(c => c.name);

                var mats = renderers.SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Select(m => $"{m.name}<{(m.mainTexture != null ? m.mainTexture.name : "no-tex")}>")
                    .Distinct();

                var topBones = instance.GetComponentsInChildren<Transform>(true)
                    .Where(t => t != instance.transform)
                    .Take(12)
                    .Select(t => t.name);

                // Do the clip curves address bones that exist on this model?
                string clipCheck = "n/a";
                var firstClip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (firstClip != null)
                {
                    var bindings = UnityEditor.AnimationUtility.GetCurveBindings(firstClip);
                    clipCheck = $"{bindings.Select(x => x.path).Distinct().Count()} paths, " +
                                $"first '{bindings.Select(x => x.path).FirstOrDefault(p => !string.IsNullOrEmpty(p))}'";
                }

                Debug.Log($"[Model] {path}\n" +
                          $"  size {b.size}\n" +
                          $"  renderers: {string.Join(", ", renderers.Select(r => r.name))}\n" +
                          $"  skinned: {instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length}\n" +
                          $"  transforms: {instance.GetComponentsInChildren<Transform>(true).Length}\n" +
                          $"  first bones: {string.Join(", ", topBones)}\n" +
                          $"  materials: {string.Join(", ", mats)}\n" +
                          $"  clips: {string.Join(", ", clips)}\n" +
                          $"  curves: {clipCheck}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }

    /// <summary>Every material in the project whose shader is missing or not URP.</summary>
    public static void ShaderAudit()
    {
        var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        int total = 0, bad = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;
            total++;

            var name = mat.shader == null ? "NULL" : mat.shader.name;
            bool ok = name.StartsWith("Universal Render Pipeline/") ||
                      name.StartsWith("Shader Graphs/") ||
                      name.StartsWith("Cesium/") ||
                      name.StartsWith("TextMeshPro/") ||
                      name.StartsWith("UI/");

            if (ok) continue;

            bad++;
            if (bad <= 20) Debug.Log($"[Audit] '{mat.name}' shader '{name}' at {path}");
        }

        Debug.Log($"[Audit] {bad} of {total} materials are not on a URP shader.");
    }

    /// <summary>Lists every renderer on the player prefab with its material and shader.</summary>
    public static void ShaderCheck()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        if (prefab == null)
        {
            Debug.LogError("[Shaders] Player prefab missing.");
            return;
        }

        foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var m in r.sharedMaterials)
            {
                var shader = m == null ? "NO MATERIAL"
                                       : (m.shader == null ? "NO SHADER" : m.shader.name);
                Debug.Log($"[Shaders] {r.name}: material '{(m != null ? m.name : "-")}' " +
                          $"shader '{shader}'");
            }
        }
    }

    /// <summary>
    /// Builds the rifle at several sizes and reaches and photographs each one.
    ///
    /// "A bit bigger and a bit further out" cannot be answered by arithmetic —
    /// it is a look — and every guess otherwise costs a rebuild plus a play
    /// session to see it. This renders the whole grid in one run, named by the
    /// numbers that made it, so the winner can simply be read off the file name
    /// and written into the builder.
    /// </summary>
    public static void SweepRifle()
    {
        string dir = @"C:\Users\harha\AppData\Local\Temp\claude\" +
                     @"C--Users-harha-Desktop\6571ad3b-9134-45a0-80d9-daeb5aad259a\scratchpad\sweep";

        // Scale is the weapon's size; reach is how far in front of the fist its
        // magazine sits, in fractions of the player's stature.
        float[] scales = { 0.85f, 1.0f, 1.15f };
        float[] reaches = { 0.035f, 0.075f, 0.115f };

        try
        {
            DexstandViewmodelBuilder.SweepRifle = true;

            foreach (float scale in scales)
            {
                foreach (float reach in reaches)
                {
                    DexstandViewmodelBuilder.SweepScale = scale;
                    DexstandViewmodelBuilder.SweepRest = new Vector3(0f, -0.01f, reach);

                    DexstandViewmodelBuilder.BuildViewmodel();

                    string name = $"ak_s{scale:0.00}_z{reach:0.000}.png".Replace(',', '.');
                    DexstandPreview.RenderTo(System.IO.Path.Combine(dir, name));
                    Debug.Log($"[Sweep] scale {scale:F2}, reach {reach:F3} -> {name}");
                }
            }
        }
        finally
        {
            // Left on, every later build would use the last combination tried.
            DexstandViewmodelBuilder.SweepRifle = false;
        }

        Debug.Log($"[Sweep] Done — {scales.Length * reaches.Length} frames in {dir}.");
    }

    /// <summary>
    /// Photographs the fist at several places along the rifle while the rifle
    /// itself stands still, so the one where the hand closes on the pistol grip
    /// can be picked out by eye.
    /// </summary>
    public static void SweepGrip()
    {
        string dir = @"C:\Users\harha\AppData\Local\Temp\claude\" +
                     @"C--Users-harha-Desktop\6571ad3b-9134-45a0-80d9-daeb5aad259a\scratchpad\grip";

        // Back towards the body, and out to the right — the two directions that
        // walk the fist down the weapon towards its grip.
        float[] backs = { 0f, -0.03f, -0.06f };
        float[] rights = { 0f, 0.03f, 0.06f };

        try
        {
            DexstandViewmodelBuilder.SweepHand = true;

            foreach (float back in backs)
            {
                foreach (float right in rights)
                {
                    DexstandViewmodelBuilder.SweepHandDelta = new Vector3(right, 0f, back);
                    DexstandViewmodelBuilder.BuildViewmodel();

                    string name = $"hand_b{-back:0.00}_r{right:0.00}.png".Replace(',', '.');
                    DexstandPreview.RenderTo(System.IO.Path.Combine(dir, name));
                    Debug.Log($"[Grip] back {back:F2}, right {right:F2} -> {name}");
                }
            }
        }
        finally
        {
            DexstandViewmodelBuilder.SweepHand = false;
        }

        Debug.Log($"[Grip] Done — {backs.Length * rights.Length} frames in {dir}.");
    }

    public static void MeasureMap()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var map = GameObject.Find("Map");
        if (map == null)
        {
            Debug.LogError("[Measure] No Map in scene.");
            return;
        }

        var parts = map.GetComponentsInChildren<Renderer>(true)
            .Select(r => new { r.name, Size = r.bounds.size, Min = r.bounds.min.y, Max = r.bounds.max.y })
            .OrderBy(p => p.Size.y)
            .ToArray();

        Debug.Log($"[Measure] Map has {parts.Length} renderers.");

        foreach (var p in parts)
        {
            Debug.Log($"[Measure] {p.name}: size ({p.Size.x:F2}, {p.Size.y:F2}, {p.Size.z:F2}) " +
                      $"y from {p.Min:F2} to {p.Max:F2}");
        }

        // A doorway is the most reliable human-scale reference in a level; short
        // of that, report the shortest solid piece as a crate candidate.
        var shortest = parts.FirstOrDefault(p => p.Size.y > 0.05f);
        if (shortest != null)
            Debug.Log($"[Measure] Shortest solid piece: {shortest.name} at {shortest.Size.y:F2} tall.");

        var b = new Bounds(map.transform.position, Vector3.zero);
        foreach (var r in map.GetComponentsInChildren<Renderer>(true)) b.Encapsulate(r.bounds);
        Debug.Log($"[Measure] Whole map: size {b.size}, y from {b.min.y:F2} to {b.max.y:F2}");
    }

    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";

    /// <summary>
    /// Where the first person view actually stands: what PlayerSetup switches
    /// off, which holder the weapons hang from, and how far the body's own gun
    /// hand is from the lens once a rifle pose is on it.
    ///
    /// The pose matters. In bind pose the arms are out sideways in a T, so any
    /// offset measured there says nothing about where a held weapon belongs.
    /// </summary>
    public static void InspectFirstPerson()
    {
        var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            var setup = root.GetComponentInChildren<PlayerSetup>(true);
            var camera = root.GetComponentInChildren<Camera>(true);

            Debug.Log($"[FP] trueFirstPerson={setup?.trueFirstPerson}, showBodyToSelf={setup?.showBodyToSelf}, " +
                      $"arms='{PathOf(setup?.arms, root)}', body='{PathOf(setup?.body, root)}', " +
                      $"cameraObject='{PathOf(setup?.cameraObject, root)}', " +
                      $"bodyAnimator='{PathOf(setup?.bodyAnimator?.gameObject, root)}'");

            var switcher = root.GetComponent<WeaponSwitcher>();
            if (switcher == null) Debug.LogWarning("[FP] No WeaponSwitcher on the prefab root.");
            else
                foreach (var slot in switcher.slots)
                    Debug.Log($"[FP] slot '{slot.displayName}' -> {PathOf(slot.model, root)}");

            var arms = FindDeep(root, "Arms");
            if (arms != null)
                foreach (Transform child in arms)
                    Debug.Log($"[FP] Arms child: {child.name} " +
                              $"({child.GetComponentsInChildren<Renderer>(true).Length} renderers)");

            var body = FindDeep(root, "Body");
            var model = body != null ? body.Find("Model") : null;
            if (model == null)
            {
                Debug.LogError("[FP] No Body/Model — nothing to measure.");
                return;
            }

            var animator = model.GetComponent<Animator>();
            Debug.Log($"[FP] body model scale {model.localScale.x:F3}, " +
                      $"avatar={(animator?.avatar == null ? "NONE" : animator.avatar.name)}, " +
                      $"human={(animator?.avatar != null && animator.avatar.isHuman)}, " +
                      $"controller={(animator?.runtimeAnimatorController == null ? "NONE" : animator.runtimeAnimatorController.name)}");

            var pose = LoadFirstClip("Assets/Models/Character/MixamoClips/RifleIdle.fbx");
            if (pose == null) Debug.LogWarning("[FP] No RifleIdle clip — measuring the bind pose instead.");
            else
            {
                pose.SampleAnimation(model.gameObject, 0f);
                Debug.Log($"[FP] posed by {pose.name} (human={pose.isHumanMotion}, {pose.length:F2}s).");
            }

            var hips = FindDeep(root, "mixamorig:Hips");
            var head = FindDeep(root, "mixamorig:Head");
            var rightHand = FindDeep(root, "mixamorig:RightHand");
            var leftHand = FindDeep(root, "mixamorig:LeftHand");

            if (camera != null)
            {
                foreach (var pair in new[]
                {
                    ("hips", hips), ("head", head),
                    ("rightHand", rightHand), ("leftHand", leftHand),
                })
                {
                    if (pair.Item2 == null) { Debug.LogWarning($"[FP] bone '{pair.Item1}' missing."); continue; }
                    var local = camera.transform.InverseTransformPoint(pair.Item2.position);
                    Debug.Log($"[FP]   {pair.Item1}: right {local.x:F3}, up {local.y:F3}, fwd {local.z:F3}");
                }
            }

            if (rightHand != null && leftHand != null)
            {
                var span = leftHand.position - rightHand.position;
                Debug.Log($"[FP] hands {span.magnitude:F3}m apart; direction in the right hand's own axes: " +
                          $"{rightHand.InverseTransformDirection(span.normalized)}, " +
                          $"world up in those axes: {rightHand.InverseTransformDirection(Vector3.up)}");
            }

            // The chain the aim has to pitch, so a gun in the fist follows the lens.
            for (var t = head; t != null && t != model; t = t.parent)
                Debug.Log($"[FP] chain: {t.name}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static AnimationClip LoadFirstClip(string assetPath)
    {
        return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                            .OfType<AnimationClip>()
                            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
    }

    private static string PathOf(GameObject go, GameObject root)
    {
        if (go == null) return "NULL";
        return AnimationUtility.CalculateTransformPath(go.transform, root.transform);
    }

    private static Transform FindDeep(GameObject root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    }
}
