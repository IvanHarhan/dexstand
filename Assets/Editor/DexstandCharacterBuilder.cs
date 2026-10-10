using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Turns the capsule stand-ins into animated characters: imports the clip
/// library, builds animator controllers for the patrolling targets and for the
/// player's own body, and assembles a target prefab.
/// </summary>
public static class DexstandCharacterBuilder
{
    // One file holds the mesh and all 30 clips on the rig they were authored
    // for. The earlier pairing — a character from the humanoid folder plus a
    // separate clip library — shared bone names but not bone parenting, and
    // local rotations cannot be carried across different hierarchies: the
    // character animated flat on its back no matter how the paths were fixed.
    private const string CharacterPath = "Assets/Models/Character/SwatFull.fbx";
    private const string AnimationsPath = "Assets/Models/Character/SwatFull.fbx";
    private const string TargetPrefabPath = "Assets/Prefabs/TargetCharacter.prefab";
    private const string TargetControllerPath = "Assets/Animations/TargetController.controller";

    // Names as they appear inside Animations.fbx.
    private const string ClipIdle = "Idle_Gun";
    private const string ClipWalk = "Walk";
    private const string ClipHit = "HitRecieve";
    private const string ClipDeath = "Death";
    private const string ClipShoot = "Idle_Gun_Shoot";

    [MenuItem("Dexstand/Собрать персонажей")]
    public static void BuildCharacters()
    {
        ConfigureImporters();

        var clips = LoadClips();
        if (clips.Count == 0)
        {
            Debug.LogError("[Characters] No clips found in the animation library.");
            return;
        }

        // The patrolling characters now wear the same model as the player, which
        // is a humanoid rig — so their controller is built from the humanoid
        // library instead of the Swat pack's own generic clips. The player's
        // body controller keeps the generic ones; nothing else uses them.
        var humanoid = LoadHumanoidClips();
        var targetController = BuildTargetController(humanoid.Count > 0 ? humanoid : clips);
        BuildTargetPrefab(targetController);

        AssetDatabase.SaveAssets();
        Debug.Log("[Characters] Done.");
    }

    private static void ConfigureImporters()
    {
        SetRig(CharacterPath, importAnimation: true);
    }

    /// <summary>
    /// Maps every bone name in the target model to its full transform path.
    /// The clips and this model share bone names but not hierarchy, so curves
    /// are re-pointed by name. Matching on paths fails, and Unity's humanoid
    /// retargeting cannot map this rig either — the avatar it generates comes
    /// back isHuman=false.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, string> BuildBoneMap()
    {
        var map = new System.Collections.Generic.Dictionary<string, string>();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
        if (model == null) return map;

        var instance = Object.Instantiate(model);
        try
        {
            var root = instance.transform;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                if (!map.ContainsKey(t.name)) map[t.name] = PathTo(root, t);
            }
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }

        Debug.Log($"[Characters] Bone map: {map.Count} transforms in the model.");
        return map;
    }

    private static string PathTo(Transform root, Transform target)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var t = target; t != null && t != root; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void SetRig(string path, bool importAnimation)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Characters] No importer at {path}");
            return;
        }

        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Generic)
        {
            importer.animationType = ModelImporterAnimationType.Generic;
            changed = true;
        }
        if (importer.importAnimation != importAnimation)
        {
            importer.importAnimation = importAnimation;
            changed = true;
        }

        // A Generic rig without an avatar has no bone hierarchy to drive, and
        // the Animator refuses to run: "Animator is not playing an
        // AnimatorController", once per frame per character.
        if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }

        if (changed) importer.SaveAndReimport();
    }

    private const string ClipFolder = "Assets/Animations/Clips";

    /// <summary>
    /// The model the patrolling characters wear, and the humanoid library that
    /// animates it. Both are the player's own: one rig, one set of clips, and a
    /// character that looks like it belongs in the same game as the hands in
    /// front of the lens — the Swat pack's blank silhouettes did not.
    /// </summary>
    private const string NpcModelPath = DexstandCharacterImport.ModelPath;
    private const string HumanoidLibraryPath = "Assets/Models/Character/Animations.fbx";

    /// <summary>
    /// The humanoid clips, straight out of the library FBX. No re-pointing and
    /// no copies: a humanoid clip is played through the avatar, so it drives any
    /// humanoid rig regardless of what its bones are called.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, AnimationClip> LoadHumanoidClips()
    {
        var map = new System.Collections.Generic.Dictionary<string, AnimationClip>();

        foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(HumanoidLibraryPath)
                                          .OfType<AnimationClip>()
                                          .Where(c => !c.name.StartsWith("__preview__")))
        {
            var bar = clip.name.LastIndexOf('|');
            var key = bar >= 0 ? clip.name.Substring(bar + 1) : clip.name;
            if (!map.ContainsKey(key)) map[key] = clip;
        }

        // Idle and walk come from the same Mixamo rifle clips the player's own
        // body uses. The library's Idle_Gun holds the weapon down by the thigh,
        // so a patrol posed from it carries its rifle somewhere near its knee —
        // and stands quite differently from the player it is shooting at.
        var rifleIdle = DexstandBodyAnimations.Downloaded("Idle");
        var rifleWalk = DexstandBodyAnimations.Downloaded("Walk");
        if (rifleIdle != null) map[ClipIdle] = rifleIdle;
        if (rifleWalk != null) map[ClipWalk] = rifleWalk;

        Debug.Log($"[Characters] {map.Count} humanoid clip(s) for the patrols; " +
                  $"idle is '{(map.TryGetValue(ClipIdle, out var shown) ? shown.name : "missing")}'.");
        return map;
    }

    private static System.Collections.Generic.Dictionary<string, AnimationClip> LoadClips()
    {
        var map = new System.Collections.Generic.Dictionary<string, AnimationClip>();

        if (!AssetDatabase.IsValidFolder("Assets/Animations"))
            AssetDatabase.CreateFolder("Assets", "Animations");
        if (!AssetDatabase.IsValidFolder(ClipFolder))
            AssetDatabase.CreateFolder("Assets/Animations", "Clips");

        var source = AssetDatabase.LoadAllAssetsAtPath(AnimationsPath)
                                  .OfType<AnimationClip>()
                                  .Where(c => !c.name.StartsWith("__preview__"))
                                  .ToArray();
        if (source.Length == 0) return map;

        // Clips and mesh now come from the same file, so the paths already
        // point at the right bones and must be left alone.
        System.Collections.Generic.Dictionary<string, string> bones = null;
        int stripped = 0, remapped = 0, dropped = 0;

        foreach (var clip in source)
        {
            // Clips are exported as "CharacterArmature|Walk".
            var bar = clip.name.LastIndexOf('|');
            var key = bar >= 0 ? clip.name.Substring(bar + 1) : clip.name;

            map[key] = RebuildClip(clip, $"{ClipFolder}/{key}.anim", bones,
                                   ref stripped, ref remapped, ref dropped);
        }

        Debug.Log($"[Characters] {map.Count} clips: {stripped} had scale curves, " +
                  $"{remapped} curves re-pointed, {dropped} dropped as unmatched.");
        return map;
    }

    /// <summary>
    /// Copies a clip out of the FBX with its m_LocalScale curves removed.
    /// These clips were exported from Blender with a unit conversion baked in,
    /// so they drive the root scale to 100 and blow the character up to a
    /// hundred times its size the instant they start playing.
    /// </summary>
    public static AnimationClip StripScaleCurves(AnimationClip source, string path, ref int counter)
    {
        int ignoredA = 0, ignoredB = 0;
        return RebuildClip(source, path, null, ref counter, ref ignoredA, ref ignoredB);
    }

    /// <summary>
    /// Writes a usable copy of an imported clip: scale curves dropped, and each
    /// remaining curve re-pointed at the bone of the same name in the target
    /// model. Pass a null map to keep the original paths.
    /// </summary>
    public static AnimationClip RebuildClip(AnimationClip source, string path,
                                            System.Collections.Generic.Dictionary<string, string> bones,
                                            ref int counter, ref int remapped, ref int dropped)
    {
        var copy = new AnimationClip
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path),
            frameRate = source.frameRate,
            wrapMode = source.wrapMode,
            legacy = false,
        };

        var settings = AnimationUtility.GetAnimationClipSettings(source);
        AnimationUtility.SetAnimationClipSettings(copy, settings);

        bool hadScale = false;

        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            if (binding.propertyName.Contains("m_LocalScale"))
            {
                hadScale = true;
                continue;
            }

            var curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null) continue;

            var moved = binding;

            if (bones != null && !string.IsNullOrEmpty(binding.path))
            {
                // Skip the topmost bone. Its curves hold an absolute rotation
                // from the source rig's rest pose, and this model's rest pose
                // differs by the usual Blender Z-up / Unity Y-up quarter turn,
                // so applying it lays the character on its back. Everything
                // below animates relative to it and is fine.
                if (!binding.path.Contains("/"))
                {
                    dropped++;
                    continue;
                }

                var slash = binding.path.LastIndexOf('/');
                var boneName = slash >= 0 ? binding.path.Substring(slash + 1) : binding.path;

                if (!bones.TryGetValue(boneName, out var resolved))
                {
                    dropped++;
                    continue;
                }

                if (resolved != binding.path) remapped++;
                moved.path = resolved;
            }

            AnimationUtility.SetEditorCurve(copy, moved, curve);
        }

        if (hadScale) counter++;

        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
            AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(copy, path);

        return copy;
    }

    private static AnimationClip Find(
        System.Collections.Generic.Dictionary<string, AnimationClip> clips, string name)
    {
        if (clips.TryGetValue(name, out var clip)) return clip;
        Debug.LogWarning($"[Characters] Clip '{name}' missing.");
        return null;
    }

    private static AnimatorController CreateController(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
            AssetDatabase.DeleteAsset(path);

        if (!AssetDatabase.IsValidFolder("Assets/Animations"))
            AssetDatabase.CreateFolder("Assets", "Animations");

        return AnimatorController.CreateAnimatorControllerAtPath(path);
    }

    /// <summary>Idle/Walk on Speed, plus a hit reaction and a death pose.</summary>
    private static AnimatorController BuildTargetController(
        System.Collections.Generic.Dictionary<string, AnimationClip> clips)
    {
        var c = CreateController(TargetControllerPath);
        c.AddParameter("Speed", AnimatorControllerParameterType.Float);
        c.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
        c.AddParameter("Dead", AnimatorControllerParameterType.Bool);
        c.AddParameter("Shoot", AnimatorControllerParameterType.Trigger);

        var sm = c.layers[0].stateMachine;

        var idle = sm.AddState("Idle");
        idle.motion = Find(clips, ClipIdle);
        sm.defaultState = idle;

        var walk = sm.AddState("Walk");
        walk.motion = Find(clips, ClipWalk);

        var hit = sm.AddState("Hit");
        hit.motion = Find(clips, ClipHit);

        // Firing back: the library's own shooting clip, which keeps the rifle up
        // rather than swinging the arms as the hit reaction does.
        var shoot = sm.AddState("Shoot");
        shoot.motion = Find(clips, ClipShoot) ?? Find(clips, ClipIdle);

        var death = sm.AddState("Death");
        death.motion = Find(clips, ClipDeath);

        var toWalk = idle.AddTransition(walk);
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        toWalk.hasExitTime = false;
        toWalk.duration = 0.15f;

        var toIdle = walk.AddTransition(idle);
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        toIdle.hasExitTime = false;
        toIdle.duration = 0.15f;

        var anyHit = sm.AddAnyStateTransition(hit);
        anyHit.AddCondition(AnimatorConditionMode.If, 0f, "Hit");
        anyHit.AddCondition(AnimatorConditionMode.IfNot, 0f, "Dead");
        anyHit.hasExitTime = false;
        anyHit.duration = 0.05f;
        anyHit.canTransitionToSelf = false;

        var hitBack = hit.AddTransition(idle);
        hitBack.hasExitTime = true;
        hitBack.exitTime = 0.8f;
        hitBack.duration = 0.15f;

        var anyShoot = sm.AddAnyStateTransition(shoot);
        anyShoot.AddCondition(AnimatorConditionMode.If, 0f, "Shoot");
        anyShoot.AddCondition(AnimatorConditionMode.IfNot, 0f, "Dead");
        anyShoot.hasExitTime = false;
        anyShoot.duration = 0.08f;
        anyShoot.canTransitionToSelf = false;

        var shootBack = shoot.AddTransition(idle);
        shootBack.hasExitTime = true;
        shootBack.exitTime = 0.7f;
        shootBack.duration = 0.2f;

        var anyDeath = sm.AddAnyStateTransition(death);
        anyDeath.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
        anyDeath.hasExitTime = false;
        anyDeath.duration = 0.1f;
        anyDeath.canTransitionToSelf = false;

        var revive = death.AddTransition(idle);
        revive.AddCondition(AnimatorConditionMode.IfNot, 0f, "Dead");
        revive.hasExitTime = false;
        revive.duration = 0.2f;

        AssetDatabase.SaveAssets();
        return c;
    }

    /// <summary>
    /// The player's body controller is not built here.
    ///
    /// It used to be, out of the Swat pack's generic clips, and it writes to the
    /// same asset path as the humanoid one DexstandBodyAnimations builds. So
    /// running "characters" quietly replaced the body's rifle idle with a clip
    /// authored for a different skeleton: the character fell back to its bind
    /// pose, arms down, and the gun hand could no longer reach the rest point in
    /// front of the lens. The weapon vanished off the bottom of the screen and
    /// nothing in the viewmodel code had changed.
    /// </summary>
    private static void BuildTargetPrefab(AnimatorController controller)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(NpcModelPath);
        if (model == null)
        {
            Debug.LogError($"[Characters] Character model missing at {NpcModelPath}");
            return;
        }

        var root = new GameObject("TargetCharacter");
        try
        {
            var instance = Object.Instantiate(model, root.transform);
            instance.name = "Model";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            float height = DexstandPlayerBuilder.PlayerHeight;
            var bounds = MeasureBounds(instance);
            if (bounds.size.y > 0.0001f)
                instance.transform.localScale = Vector3.one * (height / bounds.size.y);

            // Re-measure after scaling so the feet land on the prefab origin.
            bounds = MeasureBounds(instance);
            instance.transform.localPosition -= new Vector3(
                bounds.center.x, bounds.min.y, bounds.center.z);

            var animator = instance.GetComponent<Animator>();
            if (animator == null) animator = instance.AddComponent<Animator>();

            // Loaded by path, not taken from the caller: creating and deleting
            // the clip assets invalidates the reference handed back earlier,
            // and assigning it silently leaves the Animator with no controller.
            var fresh = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(TargetControllerPath);
            animator.runtimeAnimatorController = fresh != null ? fresh : controller;
            animator.applyRootMotion = false;
            animator.avatar = LoadAvatar(NpcModelPath);

            Debug.Log($"[Characters] Animator controller = " +
                      $"{(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "NONE")}, " +
                      $"avatar = {(animator.avatar != null ? animator.avatar.name : "NONE")}");

            ArmCharacter(instance, height);

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.height = height;
            capsule.radius = height * 0.18f;
            capsule.center = new Vector3(0f, height * 0.5f, 0f);

            // Patrol distances scale with the character, so a small player does
            // not face targets wandering half the map away.
            float ratio = height / 1.8f;
            var target = root.AddComponent<Target>();
            target.patrolRadius = 3.5f * ratio;
            target.patrolSpeed = 1.1f * ratio;

            // Everything they can do to the player is in metres too, so a 0.6 m
            // character does not open fire from across the map.
            // Six of them can see most of the yard between them, so one hit has
            // to be survivable: at 5 a point each with a beat between shots the
            // player has time to take cover, which is the fight being asked for.
            target.sightRange = 24f * ratio;
            target.damage = 5f;
            target.fireInterval = 1.5f;

            PrefabUtility.SaveAsPrefabAsset(root, TargetPrefabPath);
            Debug.Log($"[Characters] Target prefab saved, height {height:F2}m.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private const string NpcPack =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Art";

    private const string NpcRiflePath = NpcPack + "/Meshes/Weapons/ARs/SK_AR_01.fbx";

    private const string NpcRifleMagazinePath =
        NpcPack + "/Meshes/Weapons/ARs/SM_AR_01_Magazine_Default.fbx";

    /// <summary>
    /// Puts a rifle in the character's right hand.
    ///
    /// The clips already hold the arms as though there were one — that is what
    /// Idle_Gun is — so a patrol with empty hands reads as a mime. The weapon is
    /// hung off the hand bone with the magazine well in the fist, the same
    /// landmark the player's own loadout is built around, and aimed along the
    /// character's forward rather than a camera's: nobody is looking down these
    /// sights.
    /// </summary>
    private static void ArmCharacter(GameObject model, float height)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(NpcRiflePath);
        if (asset == null)
        {
            Debug.LogWarning($"[Characters] No rifle at {NpcRiflePath}; patrols go unarmed.");
            return;
        }

        var hand = model.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name == "mixamorig:RightHand");
        if (hand == null)
        {
            Debug.LogWarning("[Characters] No right hand bone; patrols go unarmed.");
            return;
        }

        // Posed first: measured in the bind pose the arms are out in a T and
        // every offset taken off them belongs to a stance the game never shows.
        var pose = LoadHumanoidClips().TryGetValue(ClipIdle, out var idle) ? idle : null;
        if (pose != null) pose.SampleAnimation(model, 0f);

        var weapon = Object.Instantiate(asset, hand);
        weapon.name = "Rifle";
        weapon.transform.localPosition = Vector3.zero;
        weapon.transform.localRotation = Quaternion.identity;

        var magazineAsset = AssetDatabase.LoadAssetAtPath<GameObject>(NpcRifleMagazinePath);
        var well = weapon.GetComponentsInChildren<Transform>(true)
                         .FirstOrDefault(t => t.name == MagazineSocket);
        if (magazineAsset != null && well != null)
        {
            var magazine = Object.Instantiate(magazineAsset, well);
            magazine.transform.localPosition = Vector3.zero;
            magazine.transform.localRotation = Quaternion.identity;
        }

        var muzzle = weapon.GetComponentsInChildren<Transform>(true)
                           .FirstOrDefault(t => t.name == "SOCKET_Muzzle");
        if (muzzle == null || well == null)
        {
            Debug.LogWarning("[Characters] The rifle has no muzzle/magazine pair to aim it by.");
            return;
        }

        // Sized by measurement, not by arithmetic: the weapon hangs off a hand
        // inside a mesh that was already scaled to this stature, so multiplying
        // by a ratio applies the reduction twice and leaves a rifle the size of
        // a pencil. A real assault rifle is about 0.49 of a man's height.
        var box = MeasureBounds(weapon);
        float longest = Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));
        if (longest > 0.0001f)
            weapon.transform.localScale *= height * 0.49f / longest;

        Vector3 bore = weapon.transform.InverseTransformPoint(muzzle.position).normalized;
        Vector3 down = weapon.transform.InverseTransformPoint(well.position);
        down -= bore * Vector3.Dot(down, bore);
        Vector3 up = down.sqrMagnitude > 1e-8f ? -down.normalized : Vector3.up;

        var body = model.transform;
        var offHand = model.GetComponentsInChildren<Transform>(true)
                           .FirstOrDefault(t => t.name == "mixamorig:LeftHand");

        // Aimed along the line between the two hands, the way the idle clip
        // holds them — the same rule the player's own weapon is placed by. Along
        // the character's forward instead, the rifle crosses both wrists and the
        // support hand ends up behind the receiver.
        Vector3 along = offHand != null ? offHand.position - hand.position : body.forward;
        if (along.sqrMagnitude < 1e-8f) along = body.forward;

        weapon.transform.rotation = Quaternion.LookRotation(along.normalized, body.up) *
                                    Quaternion.Inverse(Quaternion.LookRotation(bore, up));

        // Then slid so the magazine well lands in the firing hand and the
        // handguard under the support hand: split the difference, since one rig
        // and one rifle rarely agree to the millimetre.
        Vector3 handguard = Vector3.Lerp(well.position, muzzle.position, 0.35f);
        Vector3 wanted = offHand != null
            ? (hand.position + offHand.position) * 0.5f
            : hand.position;
        Vector3 have = offHand != null ? (well.position + handguard) * 0.5f : well.position;

        weapon.transform.position += wanted - have + body.up * (height * 0.010f);

        foreach (var r in weapon.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = NpcGunmetal();
            r.sharedMaterials = mats;
        }

        Debug.Log($"[Characters] Patrol armed with a rifle in '{hand.name}'.");
    }

    private static Material gunmetal;

    /// <summary>
    /// Plain steel for the patrols' weapons. The pack's own materials come in
    /// through an embedded-material import that this project deliberately does
    /// not use — left alone the rifle renders bright magenta.
    /// </summary>
    private static Material NpcGunmetal()
    {
        if (gunmetal != null) return gunmetal;

        const string path = "Assets/Materials/NpcGunmetal.mat";
        gunmetal = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (gunmetal != null) return gunmetal;

        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        gunmetal = new Material(shader);
        gunmetal.color = new Color(0.22f, 0.23f, 0.25f);
        if (gunmetal.HasProperty("_Metallic")) gunmetal.SetFloat("_Metallic", 0.5f);
        if (gunmetal.HasProperty("_Smoothness")) gunmetal.SetFloat("_Smoothness", 0.35f);

        AssetDatabase.CreateAsset(gunmetal, path);
        return gunmetal;
    }

    private const string MagazineSocket = "SOCKET_Magazine";

    /// <summary>The Avatar sub-asset the model importer generates for the rig.</summary>
    public static Avatar LoadAvatar(string modelPath)
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null)
            Debug.LogWarning($"[Characters] No avatar in {modelPath} — the Animator will not run.");
        return avatar;
    }

    private static Bounds MeasureBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
