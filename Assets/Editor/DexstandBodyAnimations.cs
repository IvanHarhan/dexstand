using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds the player body's animator out of Mixamo clips downloaded for the
/// body character itself.
///
/// The body is a Mixamo character on a mixamorig skeleton, but BodyController
/// was built by DexstandCharacterBuilder out of the Quaternius library, whose
/// curves name Quaternius bones. Nothing resolves, so the body stands in its
/// rest T-pose no matter what Speed says. Clips pulled from Mixamo for this
/// same character carry the identical bone names and parenting, so their paths
/// resolve as they are and no re-pointing is needed — the re-pointing in
/// DexstandCharacterBuilder exists only to bridge two different rigs.
///
/// Drop the clip FBXs (FBX Binary, Without Skin, and In Place for anything that
/// travels) into the Animations folder beside the character and run "bodyanim".
/// </summary>
public static class DexstandBodyAnimations
{
    /// <summary>Beside the character model, so it follows whichever one is set.</summary>
    public static string SourceFolder =>
        Path.GetDirectoryName(DexstandCharacterImport.ModelPath).Replace('\\', '/') + "/Animations";

    // Separate from Assets/Animations/Clips: that folder holds the Quaternius
    // clips the targets still run on, and the names collide.
    private const string ClipFolder = "Assets/Animations/Body";
    private const string ControllerPath = "Assets/Animations/BodyController.controller";

    private const string StateLocomotion = "Locomotion";
    private const string StateJump = "Jump";
    private const string StateReload = "Reload";

    /// <summary>
    /// EmoteController plays these by name. They have to exist as states or
    /// every press logs "state could not be found"; when no Mixamo clip matches,
    /// the state gets the idle clip so the animator stays quiet.
    /// </summary>
    private static readonly string[] EmoteStates = { "Wave", "Roll", "Punch_Left", "Kick_Right", "Interact" };

    /// <summary>
    /// Which downloaded file fills which role. Mixamo names its downloads after
    /// the animation ("Walking.fbx", "Ch35@Running.fbx"), so the match is on the
    /// file name; each file is claimed once, and the choice is logged so a wrong
    /// pairing can be fixed by renaming the file.
    /// </summary>
    private static readonly (string Role, string[] Needles)[] Roles =
    {
        ("Jump", new[] { "jump" }),
        ("Run",  new[] { "run", "sprint", "jog" }),
        ("Walk", new[] { "walk" }),
        ("Idle", new[] { "idle", "breathing" }),
    };

    private static readonly HashSet<string> Looping =
        new HashSet<string> { "Idle", "Walk", "Run", "Aim", "WalkBack", "StrafeLeft", "StrafeRight" };

    [MenuItem("Dexstand/Собрать анимации тела")]
    public static void BuildBodyAnimations()
    {
        if (!AssetDatabase.IsValidFolder(SourceFolder))
        {
            Debug.LogError($"[Body] No clip folder at {SourceFolder}. " +
                           "Download Idle/Walk/Run/Jump from Mixamo for this character " +
                           "(FBX Binary, Without Skin) and drop the files there.");
            return;
        }

        var sources = MatchRoles();
        if (!sources.ContainsKey("Idle"))
        {
            Debug.LogError("[Body] No idle clip — it is the default state and the fallback " +
                           "for the emote states, so nothing can be built without it.");
            return;
        }

        ConfigureImporters(sources.Values);
        var clips = ExtractClips(sources);
        BuildController(clips);

        AssetDatabase.SaveAssets();
        Report(clips);
    }

    /// <summary>Pairs each role with a downloaded FBX, first match wins.</summary>
    private static Dictionary<string, string> MatchRoles()
    {
        var files = AssetDatabase.FindAssets("t:Model", new[] { SourceFolder })
                                 .Select(AssetDatabase.GUIDToAssetPath)
                                 .Distinct()
                                 .OrderBy(p => p)
                                 .ToList();

        var taken = new HashSet<string>();
        var result = new Dictionary<string, string>();

        foreach (var (role, needles) in Roles)
        {
            foreach (var path in files)
            {
                if (taken.Contains(path)) continue;

                var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (!needles.Any(n => name.Contains(n))) continue;

                result[role] = path;
                taken.Add(path);
                Debug.Log($"[Body] {role} <- {Path.GetFileName(path)}");
                break;
            }

            if (!result.ContainsKey(role))
                Debug.LogWarning($"[Body] No file matched {role} " +
                                 $"(looked for {string.Join(", ", needles)}).");
        }

        foreach (var path in files.Where(p => !taken.Contains(p)))
            Debug.Log($"[Body] unused: {Path.GetFileName(path)}");

        return result;
    }

    /// <summary>
    /// A clip FBX arrives as a plain model: animation import off, and no loop
    /// flag. Loop has to be set here rather than on the copy, because the copy
    /// inherits its settings from the imported clip.
    /// </summary>
    private static void ConfigureImporters(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = true;

            // Skeleton-only files carry no geometry worth keeping, and the
            // default material import leaves empty materials behind.
            importer.materialImportMode = ModelImporterMaterialImportMode.None;

            string role = RoleOf(path);
            var takes = importer.defaultClipAnimations;
            for (int i = 0; i < takes.Length; i++)
            {
                takes[i].loopTime = Looping.Contains(role);
                takes[i].name = role;
            }
            if (takes.Length > 0) importer.clipAnimations = takes;

            importer.SaveAndReimport();
        }
    }

    private static string RoleOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var (role, needles) in Roles)
            if (needles.Any(n => name.Contains(n))) return role;
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Copies each take out of its FBX into an editable .anim with the scale
    /// curves dropped. Paths are kept as they are: source and character share
    /// the mixamorig skeleton.
    /// </summary>
    private static Dictionary<string, AnimationClip> ExtractClips(Dictionary<string, string> sources)
    {
        EnsureFolder(ClipFolder);

        var result = new Dictionary<string, AnimationClip>();
        int scaled = 0, remapped = 0, dropped = 0;

        foreach (var pair in sources)
        {
            var take = AssetDatabase.LoadAllAssetsAtPath(pair.Value)
                                    .OfType<AnimationClip>()
                                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (take == null)
            {
                Debug.LogWarning($"[Body] {Path.GetFileName(pair.Value)} carries no clip — " +
                                 "it was probably downloaded without the animation.");
                continue;
            }

            result[pair.Key] = DexstandCharacterBuilder.RebuildClip(
                take, $"{ClipFolder}/{pair.Key}.anim", null, ref scaled, ref remapped, ref dropped);
        }

        Debug.Log($"[Body] {result.Count} clip(s) extracted, {scaled} had scale curves.");
        return result;
    }

    /// <summary>
    /// Idle/Walk/Run on one blend tree, plus a jump state and the emote states.
    ///
    /// Speed is the raw horizontal speed in m/s that PlayerSetup.Render copies
    /// out of PlayerController.NetSpeed, so the thresholds are real speeds, not
    /// a 0..1 blend. The top one is read off the built player rather than
    /// hardcoded — the character is 0.6 m tall and its walk speed is scaled to
    /// match, so a hardcoded 5 would never reach the run clip.
    /// </summary>
    private static void BuildController(Dictionary<string, AnimationClip> clips)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
            AssetDatabase.DeleteAsset(ControllerPath);

        var c = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        c.AddParameter("Speed", AnimatorControllerParameterType.Float);
        c.AddParameter(StateJump, AnimatorControllerParameterType.Trigger);

        // Added whether or not a clip turned up: PlayerSetup fires this trigger
        // on every magazine change, and a trigger with no parameter behind it
        // logs a warning each time.
        c.AddParameter(StateReload, AnimatorControllerParameterType.Trigger);

        var sm = c.layers[0].stateMachine;

        float top = TopSpeed();
        var locomotion = c.CreateBlendTreeInController(StateLocomotion, out var tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;

        tree.AddChild(clips["Idle"], 0f);
        if (clips.TryGetValue("Walk", out var walk)) tree.AddChild(walk, top * 0.5f);
        if (clips.TryGetValue("Run", out var run)) tree.AddChild(run, top);

        sm.defaultState = locomotion;
        Debug.Log($"[Body] Blend tree thresholds: idle 0, walk {top * 0.5f:F2}, run {top:F2} m/s.");

        if (clips.TryGetValue("Jump", out var jump))
        {
            var state = sm.AddState(StateJump);
            state.motion = jump;

            var into = sm.AddAnyStateTransition(state);
            into.AddCondition(AnimatorConditionMode.If, 0f, StateJump);
            into.hasExitTime = false;
            into.duration = 0.05f;
            into.canTransitionToSelf = false;

            var back = state.AddTransition(locomotion);
            back.hasExitTime = true;
            back.exitTime = 0.9f;
            back.duration = 0.15f;
        }

        BuildUpperBody(c, clips);

        // Emotes have no Mixamo clips yet, so they land on idle: the state
        // resolves, the animator stays silent, and dropping a matching file in
        // the folder later is all that is needed to make them move.
        foreach (var name in EmoteStates)
        {
            var state = sm.AddState(name);
            state.motion = clips.TryGetValue(name, out var own) ? own : clips["Idle"];

            var back = state.AddTransition(locomotion);
            back.hasExitTime = true;
            back.exitTime = 0.95f;
            back.duration = 0.2f;
        }

        AssetDatabase.SaveAssets();
    }

    private const string MaskPath = "Assets/Animations/UpperBody.mask";
    private const string LayerUpper = "Upper";
    private const string StateHold = "Hold";
    private const string StateFire = "Fire";
    private const string StateAim = "Aim";

    /// <summary>
    /// A second layer carrying everything the arms do while the legs walk:
    /// firing, the magazine change, and the sights.
    ///
    /// On the base layer a reload stopped the legs dead for two seconds and a
    /// shot could not play at all. Masked to the chest and arms it blends over
    /// whatever the locomotion tree is doing, which is what every shooter does.
    /// </summary>
    private static void BuildUpperBody(AnimatorController c, Dictionary<string, AnimationClip> clips)
    {
        bool has = clips.ContainsKey(StateFire) || clips.ContainsKey(StateReload) ||
                   clips.ContainsKey(StateAim);
        if (!has)
        {
            Debug.LogWarning("[Body] No fire/reload/aim clips — the arms will have nothing " +
                             "to play over the walk. Download them from Mixamo.");
            return;
        }

        c.AddParameter(StateFire, AnimatorControllerParameterType.Trigger);
        c.AddParameter("Aiming", AnimatorControllerParameterType.Bool);

        c.AddLayer(LayerUpper);
        var layers = c.layers;
        var upper = layers[layers.Length - 1];
        upper.defaultWeight = 1f;
        upper.avatarMask = UpperBodyMask();
        upper.blendingMode = AnimatorLayerBlendingMode.Override;
        layers[layers.Length - 1] = upper;
        c.layers = layers;

        var sm = upper.stateMachine;

        // An empty state, not the idle clip: on a masked override layer an empty
        // state lets the base layer through, while an idle clip would pin the
        // arms to a standing pose through the whole walk cycle.
        var hold = sm.AddState(StateHold);
        sm.defaultState = hold;

        if (clips.TryGetValue(StateAim, out var aim))
        {
            var state = sm.AddState(StateAim);
            state.motion = aim;

            var into = hold.AddTransition(state);
            into.AddCondition(AnimatorConditionMode.If, 0f, "Aiming");
            into.hasExitTime = false;
            into.duration = 0.15f;

            var back = state.AddTransition(hold);
            back.AddCondition(AnimatorConditionMode.IfNot, 0f, "Aiming");
            back.hasExitTime = false;
            back.duration = 0.15f;
        }

        if (clips.TryGetValue(StateFire, out var fire))
        {
            var state = sm.AddState(StateFire);
            state.motion = fire;

            var into = sm.AddAnyStateTransition(state);
            into.AddCondition(AnimatorConditionMode.If, 0f, StateFire);
            into.hasExitTime = false;
            into.duration = 0.02f;
            into.canTransitionToSelf = true;

            var back = state.AddTransition(hold);
            back.hasExitTime = true;
            back.exitTime = 0.8f;
            back.duration = 0.1f;
        }

        if (clips.TryGetValue(StateReload, out var reload))
        {
            var state = sm.AddState(StateReload);
            state.motion = reload;

            var into = sm.AddAnyStateTransition(state);
            into.AddCondition(AnimatorConditionMode.If, 0f, StateReload);
            into.hasExitTime = false;
            into.duration = 0.1f;
            into.canTransitionToSelf = false;

            var back = state.AddTransition(hold);
            back.hasExitTime = true;
            back.exitTime = 0.95f;
            back.duration = 0.2f;

            Debug.Log($"[Body] Reload on the upper layer, {reload.length:F2}s.");
        }

        Debug.Log($"[Body] Upper body layer: " +
                  $"{(clips.ContainsKey(StateFire) ? "fire " : "")}" +
                  $"{(clips.ContainsKey(StateReload) ? "reload " : "")}" +
                  $"{(clips.ContainsKey(StateAim) ? "aim" : "")}".Trim());
    }

    /// <summary>
    /// Chest, head and both arms. Built through the humanoid body parts rather
    /// than transform paths, which is the only form that survives a change of
    /// character.
    /// </summary>
    private static AvatarMask UpperBodyMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask == null)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, MaskPath);
        }

        foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
        {
            if (part == AvatarMaskBodyPart.LastBodyPart) continue;

            bool arms = part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm ||
                        part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers ||
                        part == AvatarMaskBodyPart.Body || part == AvatarMaskBodyPart.Head;
            mask.SetHumanoidBodyPartActive(part, arms);
        }

        EditorUtility.SetDirty(mask);
        return mask;
    }

    /// <summary>
    /// The speed the player actually reaches, taken off the built prefab so the
    /// thresholds cannot drift away from PlayerController.
    /// </summary>
    private static float TopSpeed()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        var pc = prefab != null ? prefab.GetComponentInChildren<PlayerController>(true) : null;
        if (pc != null && pc.walkSpeed > 0.01f) return pc.walkSpeed;

        Debug.LogWarning("[Body] No player prefab to read walkSpeed from — " +
                         "falling back to the stature formula.");
        return 5.4f * (DexstandPlayerBuilder.PlayerHeight / 1.8f);
    }

    /// <summary>
    /// The Quaternius clip library. Only DexstandDiagnostics.InspectClips reads
    /// this file, so its import settings can be changed without disturbing the
    /// targets, which run off SwatFull.fbx.
    /// </summary>
    private const string LibraryPath = "Assets/Models/Character/Animations.fbx";

    /// <summary>
    /// Single-clip FBX files downloaded from Mixamo, one animation each.
    ///
    /// They take priority over the library: these were made for a rifle, and the
    /// library's idle and walk are empty-handed, so a character holding a gun
    /// walked around as if he were not. Inside such a file the clip is called
    /// "mixamo.com" no matter what was downloaded, so the role comes from the
    /// file name instead.
    /// </summary>
    private const string ClipSourceFolder = "Assets/Models/Character/MixamoClips";

    /// <summary>
    /// Builds the body controller out of the Quaternius library by retargeting
    /// instead of downloading anything.
    ///
    /// This only became possible once the body mapped to Humanoid. Humanoid
    /// clips are stored in muscle space rather than as per-bone curves, so
    /// Unity can replay them on any humanoid avatar whatever its bone names —
    /// which is exactly the mismatch that left the body in a T-pose. The clips
    /// are used straight from the FBX: rebuilding them the way the Generic path
    /// does would strip them back to curves and undo the retargeting.
    /// </summary>
    public static void BuildFromLibrary()
    {
        var importer = AssetImporter.GetAtPath(LibraryPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[Body] No clip library at {LibraryPath}.");
            return;
        }

        var wasType = importer.animationType;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;

        // Locomotion has to loop or the character freezes at the end of the
        // first stride; the library ships every clip as a one-shot.
        var takes = importer.defaultClipAnimations;
        for (int i = 0; i < takes.Length; i++)
        {
            var bar = takes[i].name.LastIndexOf('|');
            var key = bar >= 0 ? takes[i].name.Substring(bar + 1) : takes[i].name;
            takes[i].loopTime = Looping.Contains(Canonical(key));
        }
        if (takes.Length > 0) importer.clipAnimations = takes;
        importer.SaveAndReimport();

        var avatar = AssetDatabase.LoadAllAssetsAtPath(LibraryPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isHuman)
        {
            Debug.LogWarning($"[Body] The library did not map to Humanoid " +
                             $"(avatar={(avatar == null ? "NONE" : avatar.name)}, " +
                             $"human={(avatar != null && avatar.isHuman)}). " +
                             "Reverting; clips will have to come from elsewhere.");
            importer.animationType = wasType;
            importer.SaveAndReimport();
            return;
        }

        Debug.Log($"[Body] Library mapped to Humanoid as '{avatar.name}'. Retargeting.");

        var clips = new Dictionary<string, AnimationClip>();
        CollectDownloaded(clips);

        foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(LibraryPath).OfType<AnimationClip>())
        {
            if (clip.name.StartsWith("__preview__")) continue;

            var bar = clip.name.LastIndexOf('|');
            var key = Canonical(bar >= 0 ? clip.name.Substring(bar + 1) : clip.name);
            if (key != null && !clips.ContainsKey(key)) clips[key] = clip;
        }

        Debug.Log($"[Body] Matched roles: {string.Join(", ", clips.Keys.OrderBy(k => k))}");

        if (!clips.ContainsKey("Idle"))
        {
            Debug.LogError("[Body] No idle clip in the library.");
            return;
        }

        BuildController(clips);
        AssetDatabase.SaveAssets();
        Report(clips);
    }

    /// <summary>
    /// Maps a library clip name onto the role the controller wants. The library
    /// names clips for the weapon they were authored with ("Idle_Gun"), so an
    /// exact match finds nothing.
    /// </summary>
    /// <summary>
    /// Picks up the single-clip FBX files from the downloads folder.
    ///
    /// Each one is switched to Humanoid first: without that Unity imports it as
    /// Generic, and a Generic clip only drives the exact skeleton it was authored
    /// on. Looping is set here too — a walk that stops dead at the last frame is
    /// the usual sign it was missed.
    /// </summary>
    private static void CollectDownloaded(Dictionary<string, AnimationClip> clips)
    {
        if (!AssetDatabase.IsValidFolder(ClipSourceFolder)) return;

        int taken = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ClipSourceFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string role = Canonical(System.IO.Path.GetFileNameWithoutExtension(path));
            if (role == null || clips.ContainsKey(role)) continue;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = true;
            }

            var takes = importer.defaultClipAnimations;
            for (int i = 0; i < takes.Length; i++) takes[i].loopTime = Looping.Contains(role);
            if (takes.Length > 0) importer.clipAnimations = takes;
            importer.SaveAndReimport();

            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null) continue;

            clips[role] = clip;
            taken++;
            Debug.Log($"[Body] {role} taken from {System.IO.Path.GetFileName(path)}.");
        }

        if (taken > 0) Debug.Log($"[Body] {taken} clip(s) came from {ClipSourceFolder}.");
    }

    /// <summary>
    /// The downloaded clip that fills a role, straight from its FBX. The folder
    /// and the role naming live here, so nothing else has to know either.
    /// </summary>
    public static AnimationClip Downloaded(string role)
    {
        if (!AssetDatabase.IsValidFolder(ClipSourceFolder)) return null;

        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ClipSourceFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Canonical(Path.GetFileNameWithoutExtension(path)) != role) continue;

            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip != null) return clip;
        }

        return null;
    }

    /// <summary>
    /// How long the body takes to change a magazine, read off the clip that
    /// shows it. The weapon's own timer is set from this; guessed at instead,
    /// the gun fires again while the hands are still working. Zero when no
    /// reload was downloaded.
    /// </summary>
    public static float ReloadSeconds()
    {
        var clip = Downloaded(StateReload);
        return clip != null ? clip.length : 0f;
    }

    private static string Canonical(string name)
    {
        var n = name.ToLowerInvariant();
        // Substring first, for the downloaded files: they are named for the
        // weapon as well as the move ("RifleIdle"), and an exact match or a
        // prefix finds neither.
        //
        // Order matters, because Mixamo's own names overlap: "Rifle Aiming
        // Idle" is an aim, not an idle, and "Walking Backwards" is a backpedal,
        // not a walk. The more specific words are therefore tested first.
        if (n.Contains("reload")) return "Reload";
        if (n.Contains("fire") || n.Contains("firing") || n.Contains("shoot")) return "Fire";
        if (n.Contains("aim")) return "Aim";
        if (n.Contains("backward") || n.Contains("back")) return "WalkBack";
        if (n.Contains("strafe") && n.Contains("left")) return "StrafeLeft";
        if (n.Contains("strafe") && n.Contains("right")) return "StrafeRight";
        if (n.Contains("draw") || n.Contains("equip")) return "Draw";
        if (n.Contains("idle")) return "Idle";
        if (n.Contains("walk")) return "Walk";
        if (n.Contains("run") || n.Contains("sprint")) return "Run";
        if (n == "walk") return "Walk";
        if (n == "run") return "Run";
        if (n.Contains("jump")) return "Jump";
        foreach (var emote in EmoteStates)
            if (string.Equals(name, emote, System.StringComparison.OrdinalIgnoreCase)) return emote;
        return null;
    }

    /// <summary>
    /// Asks Unity to map the body's skeleton onto its Humanoid rig and reports
    /// whether it took.
    ///
    /// It matters because a humanoid avatar retargets: any humanoid clip from
    /// anywhere drives the character, and the clip's own bone names stop being
    /// relevant. Generic is the fallback and needs clips authored on this exact
    /// skeleton. The import is left Generic if the mapping fails, which is what
    /// happened to the Quaternius rig this project started with.
    /// </summary>
    public static void TryHumanoid()
    {
        string path = DexstandCharacterImport.ModelPath;
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[Humanoid] No model at {path}.");
            return;
        }

        var wasType = importer.animationType;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.SaveAndReimport();

        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        bool ok = avatar != null && avatar.isValid && avatar.isHuman;

        Debug.Log($"[Humanoid] {System.IO.Path.GetFileName(path)}: " +
                  $"avatar={(avatar == null ? "NONE" : avatar.name)}, " +
                  $"valid={(avatar != null && avatar.isValid)}, human={(avatar != null && avatar.isHuman)}");

        if (ok)
        {
            Debug.Log("[Humanoid] Mapped. Any humanoid clip will retarget onto this body — " +
                      "clip bone names no longer have to match.");
            return;
        }

        Debug.LogWarning($"[Humanoid] Mapping failed — reverting to {wasType}. " +
                         "Clips will have to be authored on this exact skeleton.");
        importer.animationType = wasType;
        importer.SaveAndReimport();
    }

    /// <summary>
    /// Says whether the clips will actually drive this body: every curve path
    /// has to resolve against the model, and the model has to keep its height
    /// through the clip. A clip built for another rig resolves nothing and the
    /// character stands still; one with a rest-pose mismatch collapses.
    /// </summary>
    public static void Report(Dictionary<string, AnimationClip> clips = null)
    {
        clips ??= LoadBuilt();
        if (clips.Count == 0)
        {
            Debug.LogWarning($"[Body] No clips in {ClipFolder} — run \"bodyanim\" first.");
            return;
        }

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(DexstandCharacterImport.ModelPath);
        if (model == null)
        {
            Debug.LogError($"[Body] No body model at {DexstandCharacterImport.ModelPath}.");
            return;
        }

        var instance = Object.Instantiate(model);
        try
        {
            var root = instance.transform;

            foreach (var pair in clips.OrderBy(p => p.Key))
            {
                // A humanoid clip carries muscle curves, not bone paths, and
                // Unity retargets it through the avatar — there is nothing to
                // resolve and sampling it without an Animator shows nothing.
                if (pair.Value.isHumanMotion)
                {
                    Debug.Log($"[Body] {pair.Key}: humanoid clip, {pair.Value.length:F2}s, " +
                              $"loop={AnimationUtility.GetAnimationClipSettings(pair.Value).loopTime} " +
                              "— retargets through the avatar, no paths to check");
                    continue;
                }

                var paths = AnimationUtility.GetCurveBindings(pair.Value)
                                            .Select(b => b.path)
                                            .Distinct()
                                            .ToArray();

                var missing = paths.Where(p => !string.IsNullOrEmpty(p) && root.Find(p) == null).ToArray();

                Debug.Log($"[Body] {pair.Key}: {paths.Length - missing.Length}/{paths.Length} paths resolve, " +
                          $"{pair.Value.length:F2}s, loop={AnimationUtility.GetAnimationClipSettings(pair.Value).loopTime}");

                foreach (var p in missing.Take(5))
                    Debug.LogError($"[Body]   unresolved: '{p}'");

                foreach (var t in new[] { 0f, 0.5f, 0.99f })
                {
                    pair.Value.SampleAnimation(instance, t * pair.Value.length);
                    var b = Measure(instance);
                    Debug.Log($"[Body]   t={t:F2}: height {b.size.y:F2}, centre y {b.center.y:F2}");
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static Dictionary<string, AnimationClip> LoadBuilt()
    {
        var map = new Dictionary<string, AnimationClip>();
        if (!AssetDatabase.IsValidFolder(ClipFolder)) return map;

        foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null) map[Path.GetFileNameWithoutExtension(path)] = clip;
        }
        return map;
    }

    private static Bounds Measure(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;

        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
