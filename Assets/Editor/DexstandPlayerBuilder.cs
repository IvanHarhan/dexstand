using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Fills the Player prefab in: eye height, viewmodel arms with an animator,
/// and a third-person body. The prefab shipped with empty Arms/Body holders
/// and the camera parked at foot level.
/// </summary>
public static class DexstandPlayerBuilder
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string ArmsModelPath = "Assets/Models/FPSArms/rifle.fbx";
    // Whichever character DexstandCharacterImport is pointed at, so the body and
    // the import step cannot drift apart. Currently the MPFB character built in
    // Blender by blender_tools/make_character.py: it carries the mixamo_unity
    // skeleton, bone for bone the one Mixamo hands out, so Mixamo clips drop
    // onto it unchanged — unlike the Quaternius clips still in
    // DexstandCharacterBuilder, whose curves name Quaternius bones.
    private const string BodyModelPath = DexstandCharacterImport.ModelPath;
    private const string ControllerPath = "Assets/Animations/ArmsController.controller";

    /// <summary>
    /// Player stature in metres — the one number to change. Capsule, eye height,
    /// walk speed, jump and viewmodel size are all derived from it, so the
    /// character stays in proportion at any size.
    /// </summary>
    public const float PlayerHeight = 0.6f;

    private const float ReferenceHeight = 1.8f;
    private static float Ratio => PlayerHeight / ReferenceHeight;

    private static float EyeHeight => PlayerHeight * 0.92f;
    private const string ModelChildName = "Model";

    // Both source models sit several units off their own origin, so everything
    // below is placed by measured bounds rather than by transform position.
    // The yaw is computed from the mesh; this is only the fallback.
    private static float TargetArmsLength => 0.5f * Ratio;
    private const float ArmsYawDegrees = 180f;

    // Raised and pulled in so the hands clear the bottom of the frame instead
    // of sitting just under it.
    private static Vector3 ArmsViewOffset => new Vector3(0.20f, -0.17f, 0.48f) * Ratio;

    /// <summary>Player then scene, for a single headless -executeMethod run.</summary>
    public static void BuildAll()
    {
        BuildPlayer();
        DexstandSceneBuilder.BuildScene();
    }

    [MenuItem("Dexstand/Собрать игрока")]
    public static void BuildPlayer()
    {
        ConfigureModelImporters();
        var controller = BuildArmsController();

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            // --- Eye height ---------------------------------------------------
            // PlayerRoot's origin is the soles of the feet (CharacterController
            // center 0.9, height 1.8), so a CameraHolder at y=0 puts the camera
            // on the floor.
            var cameraHolder = FindChild(root, "CameraHolder");
            if (cameraHolder != null)
            {
                cameraHolder.localPosition = new Vector3(0f, EyeHeight, 0f);
                Debug.Log($"[Dexstand] CameraHolder raised to eye height {EyeHeight:F2}m.");
            }

            // --- Networked movement -------------------------------------------
            // PlayerRoot is what actually moves, so it needs its own transform
            // sync. The one on the prefab root only ever reports the spawn
            // point, which would freeze every remote player in place.
            // Fusion's CharacterController flavour of it: a plain NetworkTransform
            // restores the transform for the owner's rollbacks while the enabled
            // CharacterController keeps its own position and moves on from that,
            // so the second player ran ahead of the host and was snapped back.
            var playerRootT = FindChild(root, "PlayerRoot");
            if (playerRootT != null)
            {
                var oldSync = playerRootT.GetComponent<Fusion.NetworkTransform>();
                if (oldSync != null) Object.DestroyImmediate(oldSync, true);
                if (playerRootT.GetComponent<Fusion.NetworkCharacterController>() == null)
                    playerRootT.gameObject.AddComponent<Fusion.NetworkCharacterController>();
                Debug.Log("[Dexstand] PlayerRoot synced by NetworkCharacterController.");
            }

            // --- Stature ------------------------------------------------------
            var cc = root.GetComponentInChildren<CharacterController>(true);
            if (cc != null)
            {
                cc.height = PlayerHeight;
                cc.radius = PlayerHeight * 0.17f;
                cc.center = new Vector3(0f, PlayerHeight * 0.5f, 0f);
                cc.stepOffset = PlayerHeight * 0.28f;
                cc.skinWidth = cc.radius * 0.15f;
                EditorUtility.SetDirty(cc);
            }

            var pc = root.GetComponentInChildren<PlayerController>(true);
            if (pc != null)
            {
                // Linear for speed keeps body-lengths per second constant; jump
                // goes as the square root so it clears the same relative height.
                // 3 m/s scaled by stature reads as a crawl at this size — a
                // small character needs to cover more body-lengths per second
                // to feel like it is walking rather than shuffling.
                pc.walkSpeed = 5.4f * Ratio;
                pc.jumpForce = 6f * Mathf.Sqrt(Ratio);
                EditorUtility.SetDirty(pc);
                Debug.Log($"[Dexstand] Stature {PlayerHeight:F2}m — walk {pc.walkSpeed:F2}, " +
                          $"jump {pc.jumpForce:F2}, capsule r={cc?.radius:F2}.");
            }

            // Viewmodel arms sit closer than the default 0.3 near plane clips.
            var cam = root.GetComponentInChildren<Camera>(true);
            if (cam != null) cam.nearClipPlane = 0.01f;

            // --- Arms ---------------------------------------------------------
            // Only when the player is looking at a viewmodel. With their own body
            // on screen this is a second pair of arms nobody ever sees, and the
            // weapons belong in the body's own fist — DexstandViewmodelBuilder
            // decides that, at the bottom of this method.
            var playerSetup = root.GetComponentInChildren<PlayerSetup>(true);
            bool ownBody = playerSetup == null || playerSetup.trueFirstPerson;

            var arms = FindChild(root, "Arms");
            var armsModel = ownBody ? null : AttachModel(arms, ArmsModelPath);
            if (armsModel != null && cameraHolder != null)
            {
                FitViewmodel(armsModel, cameraHolder);

                var animator = armsModel.GetComponent<Animator>();
                if (animator == null) animator = armsModel.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;

                var driver = root.GetComponent<ArmsAnimationDriver>();
                if (driver == null) driver = root.AddComponent<ArmsAnimationDriver>();
                driver.animator = animator;

                var playerController = root.GetComponentInChildren<PlayerController>(true);
                if (playerController != null)
                {
                    playerController.armsDriver = driver;
                    EditorUtility.SetDirty(playerController);
                }

                // The arms pack has no walk clip, so footsteps are faked by bob.
                var bob = armsModel.GetComponent<WeaponBob>();
                if (bob == null) bob = armsModel.AddComponent<WeaponBob>();
                bob.player = playerController;
                // A long stride on purpose. At this stature the character covers
                // three body-lengths a second, and a realistic short stride
                // would shake the view eight times a second.
                bob.strideLength = PlayerHeight * 2.1f;
                bob.horizontalAmount = 0.008f;
                bob.verticalAmount = 0.011f;

                BuildLoadout(root, armsModel);
            }

            // --- Body ---------------------------------------------------------
            var body = FindChild(root, "Body");

            // Movement happens on PlayerRoot, so a body parented to the prefab
            // root would stay behind at the spawn point while the player walks.
            var playerRoot = FindChild(root, "PlayerRoot");
            if (body != null && playerRoot != null && body.parent != playerRoot)
            {
                body.SetParent(playerRoot, false);
                body.localPosition = Vector3.zero;
                body.localRotation = Quaternion.identity;
                Debug.Log("[Dexstand] Body reparented under PlayerRoot so it follows movement.");
            }

            var bodyModel = AttachModel(body, BodyModelPath);
            if (bodyModel != null)
            {
                FitBody(bodyModel, root.transform);

                var bodyAnimator = bodyModel.GetComponent<Animator>();
                if (bodyAnimator == null) bodyAnimator = bodyModel.AddComponent<Animator>();

                var bodyController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    "Assets/Animations/BodyController.controller");
                if (bodyController != null)
                {
                    bodyAnimator.runtimeAnimatorController = bodyController;
                    bodyAnimator.applyRootMotion = false;
                    bodyAnimator.avatar = DexstandCharacterBuilder.LoadAvatar(BodyModelPath);
                }
                else
                {
                    Debug.LogWarning("[Dexstand] BodyController missing — run Собрать персонажей first.");
                }

                // Emotes: third-person camera plus the body, driven by name.
                var emotes = root.GetComponent<EmoteController>();
                if (emotes == null) emotes = root.AddComponent<EmoteController>();
                emotes.emoteSeconds = 2.6f;
                // Written in human metres, then scaled: about two metres back
                // and half a metre up for a 1.8m character. Closer than this
                // and the back of the head fills the frame.
                emotes.thirdPersonOffset = new Vector3(0.4f, 1.0f, -3.4f) * (PlayerHeight / 1.8f);
                // The lens needs a size before it can be kept out of the crates
                // it used to reverse into, and both are stature-relative.
                emotes.cameraRadius = PlayerHeight * 0.08f;
                emotes.wallPadding = PlayerHeight * 0.03f;
                Link(emotes, "setup", root.GetComponentInChildren<PlayerSetup>(true));
                Link(emotes, "cameraTransform", cam != null ? cam.transform : null);
                Link(emotes, "bodyAnimator", bodyAnimator);

                var setup = root.GetComponentInChildren<PlayerSetup>(true);
                if (setup != null)
                {
                    setup.bodyAnimator = bodyAnimator;

                    // On since the MPFB character replaced the old body. Without
                    // it you are a floating pair of hands: looking down shows
                    // the ground, and the emote camera swings out behind a
                    // player that is not drawn. The head bone is shrunk away by
                    // PlayerSetup.HideHead so the camera does not sit inside it.
                    setup.showBodyToSelf = true;
                    EditorUtility.SetDirty(setup);
                }
            }

            BuildHealthAndHitboxes(root, playerRoot);

            // Last, and the one place that decides which pair of hands carries
            // the weapons. Running it from here is what stops the two builders
            // overwriting each other's work depending on which was invoked last.
            DexstandViewmodelBuilder.BuildHands(root);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[Dexstand] Player prefab rebuilt.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Health on the prefab root, and the hitboxes the server shoots at.
    ///
    /// The hitboxes are plain offsets under PlayerRoot rather than components on
    /// the animated bones. Bone-accurate boxes would be more faithful, but the
    /// local player shrinks its own head bone to see past it (PlayerSetup.HideHead)
    /// and a hitbox parented to that bone would shrink with it. Fixed offsets are
    /// the same for everyone, which is what an authoritative hit needs to be.
    ///
    /// HitboxRoot goes on PlayerRoot, not on the prefab root: its broad-phase
    /// sphere is placed relative to its own transform, and the prefab root never
    /// leaves the spawn point.
    /// </summary>
    private static void BuildHealthAndHitboxes(GameObject root, Transform playerRoot)
    {
        var health = root.GetComponent<PlayerHealth>();
        if (health == null) health = root.AddComponent<PlayerHealth>();
        health.maxHealth = 100f;
        health.headshotMultiplier = 2f;
        health.regenDelay = 5f;
        health.regenPerSecond = 25f;
        health.respawnSeconds = 3f;
        EditorUtility.SetDirty(health);

        if (playerRoot == null)
        {
            Debug.LogWarning("[Dexstand] No PlayerRoot — hitboxes skipped, players cannot be shot.");
            return;
        }

        var hitboxRoot = playerRoot.GetComponent<Fusion.HitboxRoot>();
        if (hitboxRoot == null) hitboxRoot = playerRoot.gameObject.AddComponent<Fusion.HitboxRoot>();
        hitboxRoot.Offset = new Vector3(0f, PlayerHeight * 0.5f, 0f);
        hitboxRoot.BroadRadius = PlayerHeight * 0.75f;

        float radius = PlayerHeight * 0.17f;

        // Torso and legs as one box: from the ankles to the shoulders.
        var torso = MakeHitbox(playerRoot, "HitboxBody", PlayerHeight * 0.45f, health, false);
        torso.Type = Fusion.HitboxTypes.Box;
        torso.BoxExtents = new Vector3(radius, PlayerHeight * 0.38f, radius * 0.7f);

        // The head sits above it, and is worth double.
        var head = MakeHitbox(playerRoot, "HitboxHead", PlayerHeight * 0.91f, health, true);
        head.Type = Fusion.HitboxTypes.Sphere;
        head.SphereRadius = PlayerHeight * 0.12f;

        hitboxRoot.InitHitboxes();
        EditorUtility.SetDirty(hitboxRoot);

        Debug.Log($"[Dexstand] Hitboxes built: torso at {PlayerHeight * 0.45f:F2}m, " +
                  $"head at {PlayerHeight * 0.91f:F2}m, broad radius {hitboxRoot.BroadRadius:F2}m.");
    }

    private static Fusion.Hitbox MakeHitbox(
        Transform parent, string name, float height, PlayerHealth health, bool isHead)
    {
        var existing = FindChild(parent.gameObject, name);
        var go = existing != null ? existing.gameObject : new GameObject(name);
        if (existing == null) go.transform.SetParent(parent, false);

        go.transform.localPosition = new Vector3(0f, height, 0f);
        go.transform.localRotation = Quaternion.identity;

        var hitbox = go.GetComponent<Fusion.Hitbox>();
        if (hitbox == null) hitbox = go.AddComponent<Fusion.Hitbox>();
        hitbox.Offset = Vector3.zero;

        var marker = go.GetComponent<PlayerHitbox>();
        if (marker == null) marker = go.AddComponent<PlayerHitbox>();
        marker.isHead = isHead;
        marker.health = health;
        EditorUtility.SetDirty(marker);

        return hitbox;
    }

    private static void ConfigureModelImporters()
    {
        SetGenericRig(ArmsModelPath);
        EnsureHumanoidRig(BodyModelPath);
    }

    /// <summary>
    /// Keeps the body on a Humanoid rig.
    ///
    /// It used to be set Generic here along with the arms, which quietly undid
    /// "humanoid" on every player rebuild. The body's clips come off Mixamo as
    /// humanoid motion and retarget through the avatar; against a Generic rig
    /// there is nothing for them to resolve and the character stands in its
    /// T-pose, holding a weapon nobody put in its hand.
    /// </summary>
    private static void EnsureHumanoidRig(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Dexstand] No model importer at {path}");
            return;
        }

        if (importer.animationType == ModelImporterAnimationType.Human) return;

        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.SaveAndReimport();

        var avatar = DexstandCharacterBuilder.LoadAvatar(path);
        if (avatar == null || !avatar.isHuman)
            Debug.LogError($"[Dexstand] {path} would not map to Humanoid — the body's " +
                           "clips will not play.");
        else
            Debug.Log($"[Dexstand] Body rig set to Humanoid ({avatar.name}).");
    }

    private static void SetGenericRig(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Dexstand] No model importer at {path}");
            return;
        }

        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Generic)
        {
            importer.animationType = ModelImporterAnimationType.Generic;
            changed = true;
        }
        if (!importer.importAnimation)
        {
            importer.importAnimation = true;
            changed = true;
        }
        if (changed) importer.SaveAndReimport();
    }

    /// <summary>Replaces the holder's "Model" child with a fresh copy of the asset.</summary>
    private static GameObject AttachModel(Transform holder, string assetPath)
    {
        if (holder == null)
        {
            Debug.LogWarning($"[Dexstand] Holder missing for {assetPath}");
            return null;
        }

        var existing = holder.Find(ModelChildName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (asset == null)
        {
            Debug.LogWarning($"[Dexstand] Model not found at {assetPath}");
            return null;
        }

        var instance = Object.Instantiate(asset, holder);
        instance.name = ModelChildName;
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        var b = LocalBounds(instance);
        Debug.Log($"[Dexstand] {assetPath} attached — bounds center {b.center}, size {b.size}");
        return instance;
    }

    /// <summary>
    /// Scales the viewmodel down and places its visible centre in front of the
    /// camera. Placing by bounds, not by transform, because the mesh sits far
    /// off its own origin.
    /// </summary>
    private static void FitViewmodel(GameObject model, Transform cameraHolder)
    {
        model.transform.localScale = Vector3.one;
        model.transform.localRotation = Quaternion.Euler(0f, ComputeMuzzleYaw(model), 0f);

        var raw = LocalBounds(model);
        float longest = Mathf.Max(raw.size.x, Mathf.Max(raw.size.y, raw.size.z));
        if (longest > 0.0001f)
            model.transform.localScale = Vector3.one * (TargetArmsLength / longest);

        var scaled = LocalBounds(model);
        var desired = cameraHolder.position + ArmsViewOffset;
        model.transform.position += desired - scaled.center;

        var final = LocalBounds(model);
        Debug.Log($"[Dexstand] Arms fitted — centre {final.center}, size {final.size}, " +
                  $"camera at {cameraHolder.position}");
    }

    /// <summary>
    /// Works out which way the barrel points from the mesh itself: the hands sit
    /// on the grip, so the gun's bulk lies toward the muzzle. Returns the yaw
    /// that turns that direction into +Z, where the camera looks.
    /// </summary>
    private static float ComputeMuzzleYaw(GameObject model)
    {
        model.transform.localRotation = Quaternion.identity;

        Bounds hands = default, gun = default;
        bool hasHands = false, hasGun = false;

        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            bool isHand = r.name.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (isHand)
            {
                hands = hasHands ? Encapsulated(hands, r.bounds) : r.bounds;
                hasHands = true;
            }
            else
            {
                gun = hasGun ? Encapsulated(gun, r.bounds) : r.bounds;
                hasGun = true;
            }
        }

        if (!hasHands || !hasGun)
        {
            Debug.LogWarning($"[Dexstand] Could not tell hands from gun — falling back to {ArmsYawDegrees}°.");
            return ArmsYawDegrees;
        }

        var barrel = gun.center - hands.center;
        barrel.y = 0f;
        if (barrel.sqrMagnitude < 1e-6f) return ArmsYawDegrees;

        float yaw = Vector3.SignedAngle(barrel.normalized, Vector3.forward, Vector3.up);
        Debug.Log($"[Dexstand] hands at {hands.center}, gun at {gun.center}, " +
                  $"barrel dir {barrel.normalized} -> yaw {yaw:F1}°");
        return yaw;
    }

    private static Bounds Encapsulated(Bounds a, Bounds b)
    {
        a.Encapsulate(b);
        return a;
    }

    private const string RiflePath = "Assets/Models/Weapons/AK.fbx";
    private const string RifleChildName = "AK";

    /// <summary>The model points muzzle-back once aligned, so it gets turned round.</summary>
    private const float RifleExtraYaw = 180f;

    /// <summary>The placeholder was a stubby block; a real AK reads longer.</summary>
    private const float RifleSizeBoost = 1.7f;

    /// <summary>
    /// Replaces the blocky placeholder gun with the AK, matched to the old
    /// gun's measured length and centre so the posed hands still line up. The
    /// original mesh is hidden rather than deleted — it belongs to the skinned
    /// arms and removing it would break the rig.
    /// </summary>
    private class WeaponSpec
    {
        public string Name;
        public string Path;
        public float SizeBoost;
        public float FireRate;
        public float Damage;
        public float Range;
        public bool Automatic;
        public bool Melee;
        public float RecoilPitch;
        public float RecoilYaw;
        public float Spread;

        public Vector2[] Spray;

        /// <summary>Per-model turn, on top of the long-axis alignment.</summary>
        public Vector3 ExtraEuler;

        /// <summary>
        /// Where the barrel sits vertically, as a fraction of the model's
        /// height from its centre. A pistol's bore runs well above centre
        /// because the grip hangs below it.
        /// </summary>
        public float MuzzleHeight;
    }

    /// <summary>
    /// Kick per shot as (yaw, pitch), shaped like an AK in CS: a steep climb
    /// for the first third, then a pull left, then a swing right. Learnable and
    /// controllable, unlike random scatter.
    /// </summary>
    /// <summary>Shared with the viewmodel builder, which rebuilds the loadout
    /// on the imported rig and needs the same feel.</summary>
    public static Vector2[] RifleSprayPattern => RifleSpray;
    public static Vector2[] PistolSprayPattern => PistolSpray;

    private static readonly Vector2[] RifleSpray =
    {
        new Vector2( 0.00f, 0.55f), new Vector2( 0.02f, 0.60f),
        new Vector2( 0.05f, 0.62f), new Vector2( 0.04f, 0.58f),
        new Vector2(-0.10f, 0.50f), new Vector2(-0.26f, 0.42f),
        new Vector2(-0.40f, 0.34f), new Vector2(-0.45f, 0.26f),
        new Vector2(-0.34f, 0.20f), new Vector2(-0.10f, 0.16f),
        new Vector2( 0.26f, 0.14f), new Vector2( 0.45f, 0.12f),
        new Vector2( 0.50f, 0.10f), new Vector2( 0.40f, 0.10f),
        new Vector2( 0.16f, 0.10f), new Vector2(-0.16f, 0.10f),
        new Vector2(-0.35f, 0.10f), new Vector2(-0.30f, 0.10f),
        new Vector2( 0.00f, 0.10f), new Vector2( 0.30f, 0.10f),
    };

    /// <summary>Semi-auto, so the pattern is short and mostly vertical.</summary>
    private static readonly Vector2[] PistolSpray =
    {
        new Vector2( 0.00f, 0.85f), new Vector2( 0.12f, 0.80f),
        new Vector2(-0.14f, 0.75f), new Vector2( 0.16f, 0.70f),
        new Vector2(-0.10f, 0.65f),
    };

    private static readonly WeaponSpec[] Loadout =
    {
        new WeaponSpec { Name = "AK", Path = "Assets/Models/Weapons/AK.fbx",
            SizeBoost = 2.0f, FireRate = 0.1f, Damage = 25f, Range = 500f,
            // 0.9 per shot climbed to the sky within a two-second burst.
            Automatic = true, RecoilPitch = 0.32f, RecoilYaw = 0.18f, Spread = 1f,
            ExtraEuler = new Vector3(0f, 180f, 0f), MuzzleHeight = 0.18f,
            Spray = RifleSpray },

        new WeaponSpec { Name = "Pistol", Path = "Assets/Models/Weapons/Pistol.fbx",
            SizeBoost = 0.9f, FireRate = 0.18f, Damage = 34f, Range = 300f,
            Automatic = false, RecoilPitch = 1.4f, RecoilYaw = 0.2f, Spread = 0.6f,
            ExtraEuler = new Vector3(0f, 180f, 0f), MuzzleHeight = 0.34f,
            Spray = PistolSpray },

        // Blade angled across the view, the way a knife is actually carried.
        // Range is arm's length at this stature, not a fixed two metres —
        // at 0.6m tall that was three body lengths and felt like a gun.
        new WeaponSpec { Name = "Knife", Path = "Assets/Models/Weapons/Knife.fbx",
            SizeBoost = 0.7f, FireRate = 0.45f, Damage = 60f, Range = PlayerHeight * 0.9f,
            Automatic = false, Melee = true, RecoilPitch = 0.2f, RecoilYaw = 0.1f, Spread = 0f,
            ExtraEuler = new Vector3(0f, 0f, -35f) },
    };

    /// <summary>
    /// Builds every weapon into the hands, each matched to the placeholder gun
    /// the arms were posed around, and wires the switcher. Only one is visible
    /// at a time.
    /// </summary>
    private static void BuildLoadout(GameObject root, GameObject armsModel)
    {
        var oldGun = armsModel.GetComponentsInChildren<Renderer>(true)
            .FirstOrDefault(r => r.name.IndexOf("gun", System.StringComparison.OrdinalIgnoreCase) >= 0
                              || r.name.IndexOf("rifle", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (oldGun == null)
        {
            Debug.LogWarning("[Dexstand] Placeholder gun mesh not found — no loadout built.");
            return;
        }

        var reference = oldGun.bounds;

        // Hang the weapons off the bone nearest the grip, not off the model
        // root: the fire clip animates the arm bones, so anything parented to
        // the root stays frozen while the hands recoil around it.
        var parent = FindGripBone(armsModel, reference.center);
        var metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/GunMetal.mat");

        var switcher = root.GetComponent<WeaponSwitcher>();
        if (switcher == null) switcher = root.AddComponent<WeaponSwitcher>();

        var built = new System.Collections.Generic.List<WeaponSwitcher.Slot>();

        foreach (var spec in Loadout)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(spec.Path);
            if (asset == null)
            {
                Debug.LogWarning($"[Dexstand] Missing weapon model {spec.Path}");
                continue;
            }

            var existing = parent.Find(spec.Name);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var go = Object.Instantiate(asset, parent);
            go.name = spec.Name;
            go.transform.localScale = Vector3.one;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localPosition = Vector3.zero;

            // Line the long axis up with the placeholder's, then turn it round:
            // these models all point muzzle-back once aligned.
            var raw = LocalBounds(go);
            go.transform.localRotation =
                Quaternion.FromToRotation(LongestAxis(raw.size), LongestAxis(reference.size)) *
                Quaternion.Euler(spec.ExtraEuler);

            var rotated = LocalBounds(go);
            float from = Mathf.Max(rotated.size.x, Mathf.Max(rotated.size.y, rotated.size.z));
            float to = Mathf.Max(reference.size.x, Mathf.Max(reference.size.y, reference.size.z));
            if (from > 0.0001f)
                go.transform.localScale = Vector3.one * (to / from) * spec.SizeBoost;

            var scaled = LocalBounds(go);
            go.transform.position += reference.center - scaled.center;

            if (metal != null)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = metal;
                    r.sharedMaterials = mats;
                }
            }

            // Muzzle at the forward tip, so tracers and flash start at the barrel.
            var final = LocalBounds(go);
            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(go.transform, false);
            muzzle.position = new Vector3(
                final.center.x,
                final.center.y + final.size.y * spec.MuzzleHeight,
                final.max.z);

            built.Add(new WeaponSwitcher.Slot
            {
                displayName = spec.Name,
                model = go,
                fireRate = spec.FireRate,
                damage = spec.Damage,
                range = spec.Range,
                automatic = spec.Automatic,
                melee = spec.Melee,
                recoilPitch = spec.RecoilPitch,
                recoilYaw = spec.RecoilYaw,
                baseSpread = spec.Spread,
                meleeRadius = PlayerHeight * 0.2f,
                sprayPattern = spec.Spray,
            });

            Debug.Log($"[Dexstand] {spec.Name} fitted — size {final.size}");
        }

        switcher.slots = built.ToArray();

        var gun = root.GetComponentInChildren<WeaponController>(true);
        Link(switcher, "weapon", gun);

        if (gun != null)
        {
            // What counts as "touching" has to follow the player's size like
            // everything else here: the default is written for a 1.8m world and
            // would let a shot at the sky catch a bystander at this stature.
            gun.pointBlankRadius = PlayerHeight * 0.2f;
            EditorUtility.SetDirty(gun);
        }

        // Only the first is visible in the saved prefab. The switcher does this
        // at runtime too, but a prefab holding three overlapping guns is
        // confusing to look at in the editor.
        for (int i = 0; i < built.Count; i++)
        {
            if (built[i].model != null) built[i].model.SetActive(i == 0);
        }

        oldGun.enabled = false;
        Debug.Log($"[Dexstand] Loadout: {built.Count} weapons.");
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

    /// <summary>
    /// The bone closest to where the placeholder gun sat — effectively the
    /// hand. Bones carry no renderer, which is how they are told apart from
    /// the mesh objects.
    /// </summary>
    private static Transform FindGripBone(GameObject armsModel, Vector3 gripPoint)
    {
        Transform best = null;
        float bestDistance = float.MaxValue;

        foreach (var t in armsModel.GetComponentsInChildren<Transform>(true))
        {
            if (t == armsModel.transform) continue;
            if (t.GetComponent<Renderer>() != null) continue;

            float d = Vector3.Distance(t.position, gripPoint);
            if (d >= bestDistance) continue;

            bestDistance = d;
            best = t;
        }

        if (best == null)
        {
            Debug.LogWarning("[Dexstand] No bone found near the grip — weapons will not recoil.");
            return armsModel.transform;
        }

        Debug.Log($"[Dexstand] Weapons parented to bone '{best.name}' " +
                  $"({bestDistance:F3}m from the grip).");
        return best;
    }

    /// <summary>Unit vector along whichever axis of a bounds size is longest.</summary>
    private static Vector3 LongestAxis(Vector3 size)
    {
        if (size.x >= size.y && size.x >= size.z) return Vector3.right;
        if (size.y >= size.x && size.y >= size.z) return Vector3.up;
        return Vector3.forward;
    }

    /// <summary>
    /// Scales the body to the player's stature and stands it on the origin with
    /// feet at y=0 — a 1.86m model inside a 1.15m capsule reads as a giant.
    /// </summary>
    private static void FitBody(GameObject model, Transform root)
    {
        model.transform.localScale = Vector3.one;
        model.transform.localRotation = Quaternion.identity;

        var raw = LocalBounds(model);
        if (raw.size.y > 0.0001f)
        {
            float scale = PlayerHeight / raw.size.y;
            model.transform.localScale = Vector3.one * scale;
            Debug.Log($"[Dexstand] Body scaled {scale:F2}x to match {PlayerHeight:F2}m stature.");
        }

        var b = LocalBounds(model);
        model.transform.position += new Vector3(
            root.position.x - b.center.x,
            root.position.y - b.min.y,
            root.position.z - b.center.z);

        var final = LocalBounds(model);
        Debug.Log($"[Dexstand] Body fitted — centre {final.center}, size {final.size}");
    }

    private static Bounds LocalBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    private static Transform FindChild(GameObject root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true)
                   .FirstOrDefault(t => t.name == name);
    }

    /// <summary>
    /// Builds the controller ArmsAnimationDriver expects: a Speed float and an
    /// IsShooting bool driving Idle and Shoot states.
    /// </summary>
    private static AnimatorController BuildArmsController()
    {
        const string dir = "Assets/Animations";
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets", "Animations");

        var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (existing != null) AssetDatabase.DeleteAsset(ControllerPath);

        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("IsShooting", AnimatorControllerParameterType.Bool);

        var clips = AssetDatabase.LoadAllAssetsAtPath(ArmsModelPath)
                                .OfType<AnimationClip>()
                                .Where(c => !c.name.StartsWith("__preview__"))
                                .ToArray();

        // Same Blender unit-conversion problem as the character clips: a baked
        // scale curve would blow the viewmodel up the moment it fires.
        int stripped = 0;
        var shootClip = clips.Length > 0
            ? DexstandCharacterBuilder.StripScaleCurves(
                clips[0], "Assets/Animations/Clips/ArmsFire.anim", ref stripped)
            : null;

        Debug.Log($"[Dexstand] Clips in arms model: {clips.Length} " +
                  $"({string.Join(", ", clips.Select(c => c.name))}), " +
                  $"scale curves removed from {stripped}");

        var sm = controller.layers[0].stateMachine;

        var idle = sm.AddState("Idle");
        var shoot = sm.AddState("Shoot");
        shoot.motion = shootClip;
        sm.defaultState = idle;

        var toShoot = idle.AddTransition(shoot);
        toShoot.AddCondition(AnimatorConditionMode.If, 0f, "IsShooting");
        toShoot.hasExitTime = false;
        toShoot.duration = 0.05f;

        var toIdle = shoot.AddTransition(idle);
        toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsShooting");
        toIdle.hasExitTime = false;
        toIdle.duration = 0.1f;

        AssetDatabase.SaveAssets();
        return controller;
    }
}
