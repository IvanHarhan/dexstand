using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Rebuilds the first-person viewmodel on the Infima character: a textured,
/// rigged body with real clips for idle, walk, run, fire, reload and inspect,
/// and weapons carried on the rig's own gun socket.
///
/// The file is called SK_FP_CH but the skeleton inside it is a full third
/// person body — legs, spine, head and all. So the model cannot simply be
/// dropped at the camera: the camera ends up at its feet that way. It is
/// placed by its head bone instead, and the head is hidden so it does not
/// fill the screen.
///
/// Mesh and clips come from the same pack on purpose. Mixing a mesh from one
/// source with animations from another is what left the earlier characters
/// animating flat on their backs.
/// </summary>
public static class DexstandViewmodelBuilder
{
    private const string Pack = "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Art";
    // The player's own arms, not the pack's. Same skeleton bone for bone — the
    // mesh was transplanted onto it in Blender, so every clip in the pack still
    // binds — but the geometry and the UVs are DexGuy's, which is what finally
    // allows a real skin texture on the hands. See blender_tools/transplant_arms.py.
    private const string ArmsPath = "Assets/Models/Viewmodel/DexArms.fbx";
    private const string RiflePath = Pack + "/Meshes/Weapons/ARs/SK_AR_01.fbx";
    private const string PistolPath = Pack + "/Meshes/Weapons/Handguns/SK_Handgun_03.fbx";
    private const string KnifePath = "Assets/Models/Weapons/Knife.fbx";

    /// <summary>
    /// The pack's weapons are modular: the magazine is a separate mesh hung on
    /// a socket by Infima's attachment system at runtime. Instantiating the
    /// weapon FBX on its own gives a rifle with an empty magazine well.
    /// </summary>
    private const string MagazineSocket = "SOCKET_Magazine";
    private const string RifleMagPath = Pack + "/Meshes/Weapons/ARs/SM_AR_01_Magazine_Default.fbx";
    private const string PistolMagPath =
        Pack + "/Meshes/Weapons/Handguns/SM_Handgun_03_Magazine_Default.fbx";

    // Plain gunmetal, built here rather than taken from the pack: every pack
    // material is a painted skin — camo, carbon fibre, bright orange — and this
    // game wants weapons that look issued, not customised.
    private const string SteelPath = "Assets/Materials/WeaponSteel.mat";
    private const string PolymerPath = "Assets/Materials/WeaponPolymer.mat";
    private const string BrassPath = "Assets/Materials/WeaponBrass.mat";
    private const string UniformPath = "Assets/Materials/ArmsUniform.mat";
    private const string SkinPath =
        "Assets/Models/Character/DexGuy/Materials/dexskin.mat";
    private const string GloveTexturePath =
        Pack + "/Textures/Character/T_FP_CH_Camo_001_001_D.png";
    private const string ClipsCommon = Pack + "/Animations/Character/_Common";
    private const string ClipsRifle = Pack + "/Animations/Character/ARs";

    /// <summary>
    /// The pack's own assembled character. Not used in the game — its scripts
    /// need Infima's whole systems — but it is the only place that records
    /// where the camera belongs, which is what every FP clip was authored
    /// around.
    /// </summary>
    private const string PackCharacterPath =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Prefabs/P_LPSP_FP_CH.prefab";

    private const string PackRoot = "Assets/Infima Games/Low Poly Shooter Pack - Free Sample";

    private const string CameraSocket = "SOCKET_Camera";

    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string ControllerPath = "Assets/Animations/ViewmodelController.controller";
    private const string ArmMaskPath = "Assets/Animations/ViewmodelArms.mask";

    /// <summary>The socket Unreal-style rigs use to carry the weapon.</summary>
    private const string WeaponSocket = "ik_hand_gun";
    private const string HeadBone = "head";
    private const string RightHand = "hand_r";

    /// <summary>
    /// The same three things on the player's own body, which is a Mixamo rig and
    /// names everything differently.
    /// </summary>
    private const string BodyRightHand = "mixamorig:RightHand";
    private const string BodyLeftHand = "mixamorig:LeftHand";

    /// <summary>
    /// Sits between the fist and the weapon. The recoil is a local offset, so
    /// its axes have to be the weapon's own — pushed along the hand bone's
    /// instead it shoves the gun sideways.
    /// </summary>
    private const string HoldName = "WeaponHold";

    /// <summary>Where the off hand is solved to. Named on the weapon itself.</summary>
    private const string LeftGripName = "LeftGrip";

    /// <summary>The gun hand's rest point, under the camera holder.</summary>
    private const string GunRestName = "GunRest";

    /// <summary>The off hand's target, moved onto the weapon's grip each frame.</summary>
    private const string OffHandTargetName = "OffHandTarget";

    /// <summary>The stature the pack is authored for.</summary>
    private const float PackHeight = 1.8f;

    /// <summary>
    /// Where the gun hand rests in the camera's own axes — right, up, forward —
    /// as a fraction of the player's stature, so it survives a change of scale.
    /// Held to the right and a little low, far enough out that the whole weapon
    /// is inside the frustum.
    /// </summary>
    private static readonly Vector3 WeaponRest = new Vector3(0.108f, -0.105f, 0.175f);

    /// <summary>
    /// Where a weapon rests and how it is angled, judged in the camera's own
    /// axes rather than in the hand's.
    ///
    /// Parenting a weapon to the hand bone and leaving it at the bone's origin
    /// puts the model's *pivot* in the fist — and every pivot in this pack sits
    /// mid-receiver, so the gun hung down and to the right with half of it past
    /// the bottom edge. The hand cannot be trusted to carry the weapon either:
    /// the pack's clips never close the fingers on anything (see the IK note in
    /// BuildLoadout), so the placement is ours to choose.
    ///
    /// <c>rest</c> is where the anchor lands — right, up, forward, as a fraction
    /// of the player's stature. It is measured from the hand bone when the
    /// weapon has an anchor, so the numbers read as "the magazine sits this far
    /// in front of the fist", and from the lens when it has none.
    /// <c>tilt</c> turns the weapon off the camera's own axis afterwards, in
    /// degrees around those axes: pitch, yaw, roll. Some yaw is what makes a
    /// rifle read as a rifle — aimed dead down the view axis it is seen end-on
    /// and shows as a dark lump, muzzle hidden behind its own receiver.
    /// </summary>
    private struct Placement
    {
        /// <summary>
        /// The landmark on the weapon that is put in the fist. A magazine well
        /// is the one landmark every gun in this pack carries that says where
        /// the firing hand belongs — the grip is immediately behind it. Left
        /// null, the weapon is placed by the centre of what the camera sees of
        /// it, which is all a blade offers.
        /// </summary>
        public string anchor;

        public Vector3 rest;
        public Vector3 tilt;
        public float scale;
    }

    /// <summary>
    /// The rifle: turned muzzle-inward so the right side of the receiver faces
    /// the camera, and drawn large enough to have presence — 0.62 of life size
    /// left it reading as a toy at this stature.
    /// </summary>
    /// <summary>
    /// Set by the sweep to try a rifle size and reach without editing the file
    /// between attempts. Judging a viewmodel needs a picture, and a picture per
    /// guess costs a rebuild and a play session; the sweep renders half a dozen
    /// in one run and the winning numbers are then written in below.
    /// </summary>
    public static bool SweepRifle;
    public static float SweepScale;
    public static Vector3 SweepRest;

    /// <summary>
    /// Moves the fist without moving the weapon.
    ///
    /// The weapon hangs off the hand bone, so shifting the hand drags the rifle
    /// along with it and the two never change places — which is no use when the
    /// complaint is that the fist is not on the grip. The delta is therefore
    /// added to where the hand is put and taken back off the weapon's own offset,
    /// leaving the rifle exactly where it was and the hand somewhere new on it.
    /// </summary>
    public static bool SweepHand;
    public static Vector3 SweepHandDelta;

    private static readonly Placement RiflePlacement = new Placement
    {
        anchor = MagazineSocket,
        // The magazine well just ahead of the fist and a little below it, which
        // puts the hand on the grip. Placed by its centre instead, the rifle sat
        // with the fist out on the handguard — held by nothing, which is what
        // "криво лежит в руках" was.
        // Chosen off the sweep contact sheet, then corrected: carrying the rifle
        // further out left the fist hanging behind the stock, so the arms were
        // moved forward with it (see WeaponRest) and what is left here is only
        // the gap from the fist to the magazine well — the pistol grip sits in
        // that gap, which is where a right hand belongs.
        rest = new Vector3(-0.030f, -0.010f, 0.105f),
        // Muzzle up and well to the left, so the rifle lies across the lower
        // right corner and its whole side is readable — the pose every shooter
        // since Half-Life has used, and the one the user asked for by handing
        // over a Counter-Strike screenshot. Aimed flat down the view axis it was
        // foreshortened into a lump.
        tilt = new Vector3(-9f, -16f, 6f),
        scale = 0.68f,
    };

    /// <summary>
    /// The handgun is small enough to be held closer to the middle of the view
    /// and higher, the way a pistol is actually presented; left where the rifle
    /// sits it disappeared behind the bottom edge.
    /// </summary>
    private static readonly Placement PistolPlacement = new Placement
    {
        anchor = MagazineSocket,
        // A handgun's magazine is inside the grip, but the fist is a fat low-poly
        // lump at this size: sat exactly on the hand the whole pistol disappeared
        // inside it, so it is carried a little forward of the knuckles and above
        // them, where the slide clears the fingers.
        // Offset by the same amount the fist was moved back and out, so the
        // handgun stays where it was framed rather than riding along with it.
        rest = new Vector3(-0.022f, 0.015f, 0.110f),
        tilt = new Vector3(-7f, -14f, 5f),
        scale = 0.95f,
    };

    /// <summary>
    /// A knife is carried blade-forward and canted across the view, not swung
    /// out sideways — which is where the old flat -35° roll put it.
    /// </summary>
    private static readonly Placement KnifePlacement = new Placement
    {
        rest = new Vector3(0.075f, -0.075f, 0.195f),
        // Negative pitch is nose-up in Unity's axes, and a knife is carried with
        // the point above the fist: +18 had it aimed at the player's own feet.
        // The roll turns the blade off its flat: face-on it read as a plank.
        tilt = new Vector3(-12f, -14f, 70f),
        // Drawn at the same three quarters of life size as the guns, or a blade
        // as long as the forearm crosses the whole lower half of the screen.
        scale = 0.75f,
    };

    /// <summary>
    /// The pack's assembled weapons. The raw FBX files carry only placeholder
    /// material slots — the real camo and steel are assigned in these prefabs,
    /// so the meshes render flat white without them.
    /// </summary>
    private const string PackPrefabs =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Prefabs";

    private static float Ratio => DexstandPlayerBuilder.PlayerHeight / PackHeight;

    [MenuItem("Dexstand/Собрать руки")]
    public static void BuildViewmodel()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            BuildHands(root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Puts the weapons in whichever pair of hands the player is going to see,
    /// and leaves the other holder empty.
    ///
    /// One entry point on purpose, called by the player builder as its last
    /// step. The two builders used to fill the same "Arms" holder from different
    /// models — the old placeholder rifle from one, the Infima rig from the
    /// other — so whichever ran last decided what the player was holding.
    /// </summary>
    public static void BuildHands(GameObject root)
    {
        // Settled 2026-09-23: the first-person view uses the pack's own arms,
        // weapons and animations, driven by PackViewmodel. Solving the body's
        // own 15 cm arms onto a weapon framed in front of the lens never looked
        // right, whatever the clips.
        BuildOnPack(root);
    }

    /// <summary>
    /// The pack's first-person character, set on our camera with its own player
    /// stripped out.
    ///
    /// Kept: the arms, the animator, the inventory with the pack's rifle and
    /// handgun, the IK component and the animation-event receiver — everything
    /// the pack needs to put its hands on its weapons. Removed: its input,
    /// movement, mouse look, cameras and UI, which our player already has.
    /// PackViewmodel then does Character's job, taking orders from our weapon.
    /// </summary>
    private static void BuildOnPack(GameObject root)
    {
        ConvertPackMaterials();

        var setup = root.GetComponentInChildren<PlayerSetup>(true);
        if (setup != null)
        {
            setup.trueFirstPerson = false;
            setup.showBodyToSelf = false;
            EditorUtility.SetDirty(setup);
        }

        var arms = FindChild(root, "Arms");
        var camera = root.GetComponentInChildren<Camera>(true);
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PackCharacterPath);
        if (arms == null || camera == null || asset == null)
        {
            Debug.LogError("[Pack] Need the Arms holder, the camera and the pack character.");
            return;
        }

        ClearHolder(arms);
        ClearBodyWeapons(root);

        var pack = Object.Instantiate(asset, arms);
        pack.name = "PackArms";
        pack.transform.localPosition = Vector3.zero;
        pack.transform.localRotation = Quaternion.identity;
        pack.transform.localScale = Vector3.one * Ratio;

        // The pack's own player. Scripts first: Movement requires the body it
        // moves, and Unity refuses to remove a component something depends on.
        foreach (var name in new[] { "Character", "Movement", "CameraLook", "TimeHandler",
                                     "CanvasSpawner", "PlayerInput" })
        {
            foreach (var c in pack.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == name) Object.DestroyImmediate(c);
        }
        foreach (var c in pack.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(c);
        foreach (var c in pack.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var c in pack.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(c);

        var animator = pack.GetComponentsInChildren<Animator>(true)
                           .FirstOrDefault(a => a.GetComponent<InfimaGames.LowPolyShooterPack.CharacterAnimationEventHandler>() != null);
        var inventory = pack.GetComponentInChildren<InfimaGames.LowPolyShooterPack.InventoryBehaviour>(true);
        if (animator == null || inventory == null)
        {
            Debug.LogError("[Pack] The pack character has no animator or no inventory.");
            return;
        }

        // Lined up by the pack's own weapon camera: where it looks from, in the
        // idle pose, is where every clip in the pack was framed for. It renders
        // at 60°, the same as ours, so the picture is the pack's own.
        var idle = LoadClip(ClipsCommon, "A_FP_PCH_Idle");
        if (idle != null) idle.SampleAnimation(animator.gameObject, 0f);

        var packCamera = pack.GetComponentsInChildren<Camera>(true)
                             .OrderByDescending(c => c.depth).FirstOrDefault();
        if (packCamera != null)
        {
            Quaternion rel = Quaternion.Inverse(pack.transform.rotation) * packCamera.transform.rotation;
            pack.transform.rotation = camera.transform.rotation * Quaternion.Inverse(rel);
            pack.transform.position += camera.transform.position - packCamera.transform.position;
        }
        else Debug.LogWarning("[Pack] No pack camera to line up with; arms left at the lens.");

        // One camera sits inside the other, so the inner one is already gone by
        // the time the loop reaches it.
        foreach (var go in pack.GetComponentsInChildren<Camera>(true).Select(c => c.gameObject).ToArray())
            if (go != null) Object.DestroyImmediate(go);

        // Nothing to see from inside a skull.
        var head = FindChild(pack, HeadBone);
        if (head != null) head.localScale = Vector3.zero;

        // One flat colour for the sleeves and gloves: the pack's camo was the
        // loudest thing on a phone screen, and a texture breaks into blocks on
        // this mesh's UVs.
        var cloth = EnsureMaterial(UniformPath, new Color(0.13f, 0.13f, 0.14f), 0.05f, 0.30f);
        foreach (var skin in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin.GetComponentInParent<InfimaGames.LowPolyShooterPack.WeaponBehaviour>() != null) continue;
            var mats = skin.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = cloth;
            skin.sharedMaterials = mats;
        }

        KeepOnScreen(pack);
        LightViewmodel(arms);

        // Stats for our slots, without meshes of our own: the pack draws them.
        var rifleReload = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Reload");
        BuildLoadout(root, arms, arms, rifleReload != null ? rifleReload.length : 2.2f);

        var switcher = root.GetComponent<WeaponSwitcher>();
        if (switcher != null)
        {
            for (int i = 0; i < switcher.slots.Length; i++)
            {
                if (switcher.slots[i].model != null) Object.DestroyImmediate(switcher.slots[i].model);
                switcher.slots[i].model = null;
            }

            // The pack plays its own holster and draw; ours would only fight it.
            switcher.viewmodel = null;
            EditorUtility.SetDirty(switcher);
        }

        var weapon = root.GetComponentInChildren<WeaponController>(true);
        if (weapon != null)
        {
            weapon.viewmodel = null;

            // The pack's muzzle brings its own flash.
            weapon.drawMuzzleFlash = false;
            EditorUtility.SetDirty(weapon);
        }

        // Bullets striking the world, with the pack's own impact sounds.
        var effects = root.GetComponent<ImpactEffects>();
        if (effects != null)
        {
            effects.impactSounds = AssetDatabase.FindAssets("t:AudioClip", new[] { PackRoot + "/Audio" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => System.IO.Path.GetFileName(p).Contains("Impact_Bullet"))
                .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
                .Where(c => c != null)
                .ToArray();
            EditorUtility.SetDirty(effects);
        }

        var driver = pack.AddComponent<PackViewmodel>();
        driver.casingRifle = AssetDatabase.LoadAssetAtPath<GameObject>(
            PackRoot + "/Prefabs/Casings/P_LPSP_Casing_Big.prefab");
        driver.casingHandgun = AssetDatabase.LoadAssetAtPath<GameObject>(
            PackRoot + "/Prefabs/Casings/P_LPSP_Casing_Small.prefab");

        // Our own footsteps, heard flat rather than from a point in the world.
        driver.stepsWalking = AssetDatabase.LoadAssetAtPath<AudioClip>(
            PackRoot + "/Audio/SFX/Character/Movement/S_CH_Loop_Walking.wav");
        var steps = pack.AddComponent<AudioSource>();
        steps.playOnAwake = false;
        steps.loop = true;
        steps.spatialBlend = 0f;
        steps.volume = 0.5f;
        driver.stepsSource = steps;
        var so = new SerializedObject(driver);
        so.FindProperty("inventory").objectReferenceValue = inventory;
        so.FindProperty("characterAnimator").objectReferenceValue = animator;
        so.FindProperty("cameraWorld").objectReferenceValue = camera;
        so.ApplyModifiedPropertiesWithoutUndo();
        driver.weapon = weapon;
        driver.switcher = switcher;
        driver.player = root.GetComponentInChildren<PlayerController>(true);

        BuildBodyGuns(root, inventory, switcher, camera.transform);

        // Off in the prefab: PlayerSetup turns the arms on for the local player
        // only. Left on, a remote player's copy would wake first and the pack's
        // weapons would take it for "the player".
        arms.gameObject.SetActive(false);

        Debug.Log($"[Pack] Pack arms on the camera at scale {Ratio:F3}, driven by PackViewmodel.");
    }

    private const string BodyGunsName = "BodyGuns";

    /// <summary>
    /// A bare copy of each pack weapon — meshes only, no scripts, sounds or effects — in the body's right fist,
    /// for other players to see. The pack draws the owner's gun only for the owner, so everyone else saw a
    /// body firing and reloading empty hands. PlayerSetup shows the one the networked weapon index names.
    ///
    /// Placed by geometry: the pack's weapons, posed in its idle, point down our camera's forward, which says
    /// which of their axes is the barrel; on the body, in its rifle idle, the barrel runs from the right fist
    /// to the left hand, and the grip sits a little behind and below the weapon's middle.
    /// </summary>
    private static void BuildBodyGuns(GameObject root, InfimaGames.LowPolyShooterPack.InventoryBehaviour inventory,
                                      WeaponSwitcher switcher, Transform camera)
    {
        var setup = root.GetComponentInChildren<PlayerSetup>(true);
        var body = FindChild(root, "Body");
        var model = body != null ? body.Find("Model") : null;
        var hand = model != null ? FindChild(model.gameObject, BodyRightHand) : null;
        var offHand = model != null ? FindChild(model.gameObject, BodyLeftHand) : null;
        if (setup == null || switcher == null || hand == null || offHand == null)
        {
            Debug.LogError("[BodyGuns] Need PlayerSetup, WeaponSwitcher and the body's two hand bones.");
            return;
        }

        var old = hand.Find(BodyGunsName);
        if (old != null) Object.DestroyImmediate(old.gameObject);

        // Measured in the pose the animator stands in, then the bones are put back: the prefab keeps its rest pose.
        var bones = model.GetComponentsInChildren<Transform>(true);
        var saved = bones.Select(t => (t.localPosition, t.localRotation, t.localScale)).ToArray();
        var idle = DexstandBodyAnimations.Downloaded("Idle");
        if (idle != null) idle.SampleAnimation(model.gameObject, 0f);
        else Debug.LogWarning("[BodyGuns] No body idle clip — placing off the bind pose.");

        var forward = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up).normalized;
        var toOff = offHand.position - hand.position;
        var dir = toOff.magnitude > 0.05f * DexstandPlayerBuilder.PlayerHeight && Vector3.Angle(toOff, forward) < 70f
            ? toOff.normalized : forward;
        var up = Vector3.ProjectOnPlane(Vector3.up, dir).normalized;

        // The palm, not the wrist the bone sits on.
        var middle = FindChild(model.gameObject, "mixamorig:RightHandMiddle1");
        var palm = middle != null ? Vector3.Lerp(hand.position, middle.position, 0.5f) : hand.position;

        var holder = new GameObject(BodyGunsName).transform;
        holder.SetParent(hand, false);

        var packWeapons = inventory.GetComponentsInChildren<InfimaGames.LowPolyShooterPack.WeaponBehaviour>(true);
        var guns = new GameObject[switcher.slots.Length];
        for (int i = 0; i < guns.Length; i++)
        {
            // the same match as PackViewmodel.PackIndex
            string wanted = switcher.slots[i].displayName.ToLowerInvariant();
            string needle = wanted.Contains("pistol") ? "handgun"
                          : wanted.Contains("ak") || wanted.Contains("rifle") ? "_ar_" : null;
            var source = needle == null ? null
                       : packWeapons.FirstOrDefault(w => w.name.ToLowerInvariant().Contains(needle));
            if (source == null) continue;

            var src = source.transform;
            Vector3 barrelLocal = SnapAxis(src.InverseTransformDirection(camera.forward));
            Vector3 upLocal = SnapAxis(Vector3.ProjectOnPlane(src.InverseTransformDirection(camera.up), barrelLocal));

            var gun = Object.Instantiate(source.gameObject, holder, false);
            gun.name = "Body_" + source.name;
            ChooseAttachments(gun);
            StripToMeshes(gun);
            SetLayer(gun, model.gameObject.layer);

            // At the origin, unturned and at its world size, the bounds are the weapon's own.
            var t = gun.transform;
            t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            t.localScale = Vector3.one * (src.lossyScale.x / holder.lossyScale.x);
            var renderers = gun.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Object.DestroyImmediate(gun); continue; }
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            float length = Mathf.Abs(Vector3.Dot(b.size, Abs(barrelLocal)));
            float height = Mathf.Abs(Vector3.Dot(b.size, Abs(upLocal)));
            Vector3 grip = b.center - barrelLocal * length * 0.1f - upLocal * height * 0.25f;

            var rotation = Quaternion.LookRotation(dir, up) * Quaternion.Inverse(Quaternion.LookRotation(barrelLocal, upLocal));
            t.SetPositionAndRotation(palm - rotation * grip, rotation);
            gun.SetActive(i == 0);
            guns[i] = gun;
            Debug.Log($"[BodyGuns] Slot {i} '{switcher.slots[i].displayName}' ← {source.name}, " +
                      $"{length:F3} long, barrel along local {barrelLocal}.");
        }

        for (int i = 0; i < bones.Length; i++)
        {
            bones[i].localPosition = saved[i].localPosition;
            bones[i].localRotation = saved[i].localRotation;
            bones[i].localScale = saved[i].localScale;
        }

        setup.bodyGuns = guns;
        EditorUtility.SetDirty(setup);
    }

    /// <summary>The nearest of the six local axes: the pack holds its weapons turned a few degrees off the view.</summary>
    private static Vector3 SnapAxis(Vector3 v)
    {
        var a = Abs(v);
        if (a.x >= a.y && a.x >= a.z) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
        if (a.y >= a.z) return new Vector3(0f, Mathf.Sign(v.y), 0f);
        return new Vector3(0f, 0f, Mathf.Sign(v.z));
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    /// <summary>
    /// What the pack's WeaponAttachmentManager does in Awake — one muzzle, one magazine, the iron sights — done
    /// here, since the copy loses its scripts and would otherwise show every attachment the prefab carries.
    /// </summary>
    private static void ChooseAttachments(GameObject gun)
    {
        foreach (var mb in gun.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetType().Name != "WeaponAttachmentManager") continue;
            var so = new SerializedObject(mb);
            var scope = so.FindProperty("scopeDefaultBehaviour")?.objectReferenceValue as Component;
            var show = so.FindProperty("scopeDefaultShow");
            if (scope != null && show != null) scope.gameObject.SetActive(show.boolValue);
            foreach (var kind in new[] { "muzzle", "magazine" })
            {
                var array = so.FindProperty(kind + "Array");
                var index = so.FindProperty(kind + "Index");
                if (array == null || index == null || !array.isArray) continue;
                for (int i = 0; i < array.arraySize; i++)
                    if (array.GetArrayElementAtIndex(i).objectReferenceValue is Component c)
                        c.gameObject.SetActive(i == index.intValue);
            }
        }
    }

    /// <summary>
    /// Everything but transforms, meshes and their renderers. Scripts first and each object's components last
    /// to first: what requires a component was added after it, and Unity refuses to remove a dependency.
    /// </summary>
    private static void StripToMeshes(GameObject gun)
    {
        var objects = gun.GetComponentsInChildren<Transform>(true);
        foreach (var t in objects)
        {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            var scripts = t.GetComponents<MonoBehaviour>();
            for (int i = scripts.Length - 1; i >= 0; i--) Object.DestroyImmediate(scripts[i]);
        }
        foreach (var c in gun.GetComponentsInChildren<ParticleSystem>(true)) Object.DestroyImmediate(c);
        foreach (var t in objects)
        {
            var comps = t.GetComponents<Component>();
            for (int i = comps.Length - 1; i >= 0; i--)
            {
                var c = comps[i];
                if (c == null || c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer) continue;
                Object.DestroyImmediate(c);
            }
        }
    }

    private static void SetLayer(GameObject go, int layer)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    /// <summary>
    /// What the body-carried weapon left behind: the hold points, the weapons
    /// hung off the hand or the rest point, and the solver that pulled the
    /// body's arms up to them. With the pack's arms in front of the lens the
    /// body is only ever seen by other players, and it plays its clips as they
    /// are.
    /// </summary>
    private static void ClearBodyWeapons(GameObject root)
    {
        foreach (var hold in root.GetComponentsInChildren<Transform>(true)
                                 .Where(t => t.name == HoldName).ToArray())
            Object.DestroyImmediate(hold.gameObject);

        var body = FindChild(root, "Body");
        var model = body != null ? body.Find("Model") : null;
        if (model == null) return;

        var rig = model.GetComponent<BodyRig>();
        if (rig != null)
        {
            rig.enabled = false;
            EditorUtility.SetDirty(rig);
        }

        var animator = model.GetComponent<Animator>();
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
            "Assets/Animations/BodyController.controller");
        if (animator != null && controller != null) animator.runtimeAnimatorController = controller;

        foreach (var bob in root.GetComponentsInChildren<WeaponBob>(true))
        {
            bob.enabled = false;
            EditorUtility.SetDirty(bob);
        }
    }

    /// <summary>
    /// The weapon in the body's own right hand.
    ///
    /// Everything the player sees of themselves is then one model: the arms in
    /// front of the lens are the arms everyone else sees, and a gesture has to
    /// be animated once instead of twice in step.
    /// </summary>
    private static void BuildOnBody(GameObject root)
    {
        ConvertPackMaterials();

        var body = FindChild(root, "Body");
        var model = body != null ? body.Find("Model") : null;
        if (model == null)
        {
            Debug.LogError("[Hands] No Body/Model — the player builder has to run first.");
            return;
        }

        var camera = root.GetComponentInChildren<Camera>(true);
        if (camera == null)
        {
            Debug.LogError("[Hands] No camera in the player prefab.");
            return;
        }

        var cameraHolder = FindChild(root, "CameraHolder");
        if (cameraHolder == null)
        {
            Debug.LogError("[Hands] No CameraHolder to hang the gun rest from.");
            return;
        }

        // Nothing is drawn from the viewmodel holder in this mode, and a second
        // pair of arms left sitting in the prefab is what kept coming back after
        // every rebuild.
        ClearHolder(FindChild(root, "Arms"));

        // The fist has to be where the animator will put it. Measured in bind
        // pose the arms are out sideways in a T, and every offset taken off them
        // then belongs to a pose the game never shows.
        var pose = DexstandBodyAnimations.Downloaded("Idle");
        if (pose != null) pose.SampleAnimation(model.gameObject, 0f);
        else Debug.LogWarning("[Hands] No rifle idle clip for the body — measuring the " +
                              "bind pose, which will leave the weapon out to one side.");

        var hand = FindChild(model.gameObject, BodyRightHand);
        var offHand = FindChild(model.gameObject, BodyLeftHand);
        if (hand == null || offHand == null)
        {
            Debug.LogError($"[Hands] The body has no '{BodyRightHand}'/'{BodyLeftHand}' bone.");
            return;
        }

        Vector3 wasHeld = camera.transform.InverseTransformPoint(hand.position);

        // The clip's own grip, before anything is solved: how the two wrists are
        // turned relative to each other is the one thing the rifle idle gets
        // right, and both targets below are built out of it.
        var grip = ReadGrip(camera.transform, hand, offHand);

        var gunRest = MakeGunRest(cameraHolder, camera.transform, grip);
        var kinematics = BuildKinematics(model.gameObject, gunRest);

        // The stance the rig will hold at runtime, struck before anything is
        // measured: BodyRig turns the chest every frame, and a weapon fitted to
        // a square-shouldered body sits somewhere else once it does.
        var spine = Spine(model.gameObject);
        BodyRig.Bend(spine, SpineShare(spine), TorsoLean, root.transform.right);
        BodyRig.Bend(spine, SpineShare(spine), TorsoTwist, root.transform.up);

        // Solved here as well as at runtime. Every offset below is measured off
        // the fist, so the fist has to already be standing where the game will
        // put it — measured where the clip leaves it, the whole loadout is framed
        // around a hand that is off the bottom of the screen.
        kinematics.Compute(0f, 1f);

        Vector3 nowHeld = camera.transform.InverseTransformPoint(hand.position);
        Debug.Log($"[Hands] Gun hand moved from (right {wasHeld.x:F3}, up {wasHeld.y:F3}, " +
                  $"fwd {wasHeld.z:F3}) to (right {nowHeld.x:F3}, up {nowHeld.y:F3}, " +
                  $"fwd {nowHeld.z:F3}); the frame is {nowHeld.z * FrameHalfHeight:F3} " +
                  $"tall either side of centre at that distance.");

        var existingHold = hand.Find(HoldName);
        if (existingHold != null) Object.DestroyImmediate(existingHold.gameObject);

        var hold = new GameObject(HoldName).transform;
        hold.SetParent(hand, false);
        hold.SetPositionAndRotation(hand.position, camera.transform.rotation);

        // The same lamp the viewmodel needed, for the same reason: the hands are
        // a few centimetres from the lens and in the player's own shadow, and
        // against a sunlit yard they collapse into one black silhouette. On the
        // camera holder rather than the arms holder, which is empty in this mode.
        LightViewmodel(cameraHolder);

        float reload = DexstandBodyAnimations.ReloadSeconds();
        BuildLoadout(root, hold, hand, reload > 0.1f ? reload : 2.2f, grip);
        WireBody(root, model.gameObject, gunRest, kinematics);

        // The off hand can only be solved once the weapon it reaches for is in
        // place, so it is done here rather than in BuildKinematics — and the
        // reach is reported, since an arm that cannot get there is left pointing
        // at the grip from a distance and reads as a hand hanging in mid air.
        SolveOffHand(root, model.gameObject, kinematics, camera.transform);

        Debug.Log($"[Hands] Weapons in the body's own fist on '{hand.name}', reload {reload:F2}s.");
    }

    /// <summary>
    /// Half the height of the frame one metre out, for the 60° camera this game
    /// uses. Anything further from the middle than this times its distance is off
    /// the screen, which is the test every placement here has to pass.
    /// </summary>
    private const float FrameHalfHeight = 0.5774f;

    /// <summary>
    /// Where the gun hand is carried, in the camera's own axes — right, up,
    /// forward — as a fraction of the player's stature, so it survives a change
    /// of scale. Just inside the bottom right of the frame, which is where a
    /// first person weapon has lived since Doom.
    /// </summary>
    internal static readonly Vector3 BodyWeaponRest = new Vector3(0.058f, -0.112f, 0.265f);

    /// <summary>
    /// Where the barrel points, in the camera's own axes: forward, a little left
    /// and a little up, which is the angle every first person shooter carries a
    /// rifle at. The whole grip is turned onto this line, so the two hands keep
    /// the spacing and the wrist angles the clip gave them.
    /// </summary>
    internal static readonly Vector3 BarrelDirection = new Vector3(-0.26f, 0.10f, 0.96f);

    /// <summary>
    /// How the idle clip holds a rifle, lifted out of the pose before any of it
    /// is solved away.
    ///
    /// Both wrist angles are turned by the same rotation — the one that takes the
    /// clip's grip line, running from the firing hand to the support hand, onto
    /// the line the weapon is carried along. Turned as one piece the hands keep
    /// their distance and their angles to each other, which is the hold; turned
    /// separately, or not at all, they come apart.
    /// </summary>
    private struct Grip
    {
        public Quaternion right;
        public Quaternion left;
        public float span;
    }

    private static Grip ReadGrip(Transform camera, Transform hand, Transform offHand)
    {
        Vector3 along = offHand.position - hand.position;
        float span = along.magnitude;

        // A pose with both fists in the same place says nothing about direction;
        // carry on with no turn rather than with a NaN rotation.
        Quaternion turn = Quaternion.identity;
        if (span > 1e-4f)
        {
            turn = Quaternion.LookRotation(camera.TransformDirection(BarrelDirection.normalized),
                                           camera.up) *
                   Quaternion.Inverse(Quaternion.LookRotation(along / span, Vector3.up));
        }

        return new Grip
        {
            right = turn * hand.rotation,
            left = turn * offHand.rotation,
            span = span,
        };
    }

    /// <summary>
    /// The rest point the gun hand is solved onto, hung off the camera holder so
    /// it pitches and turns with the view — which is what makes the weapon point
    /// where the player is looking without any of it being animated.
    ///
    /// The wrist angle is the clip's own, turned onto the carry line: solved onto
    /// a target that faces somewhere else the hand rolls off the grip, which is
    /// what left a flat open palm under the rifle.
    /// </summary>
    private static Transform MakeGunRest(Transform cameraHolder, Transform camera, Grip grip)
    {
        var existing = cameraHolder.Find(GunRestName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var rest = new GameObject(GunRestName).transform;
        rest.SetParent(cameraHolder, false);
        rest.SetPositionAndRotation(
            camera.TransformPoint(BodyWeaponRest * DexstandPlayerBuilder.PlayerHeight),
            grip.right);

        return rest;
    }

    /// <summary>
    /// Sets Infima's two-bone solver up on the body: the gun hand onto the rest
    /// point in front of the lens, the off hand onto whatever grip the weapon
    /// offers. The fields are private and serialized, which is why they are
    /// written through a SerializedObject rather than assigned.
    /// </summary>
    private static InfimaGames.LowPolyShooterPack.CharacterKinematics BuildKinematics(
        GameObject model, Transform gunRest)
    {
        var kinematics = model.GetComponent<InfimaGames.LowPolyShooterPack.CharacterKinematics>();
        if (kinematics == null)
            kinematics = model.AddComponent<InfimaGames.LowPolyShooterPack.CharacterKinematics>();

        var offHand = model.transform.Find(OffHandTargetName);
        if (offHand == null)
        {
            offHand = new GameObject(OffHandTargetName).transform;
            offHand.SetParent(model.transform, false);
        }

        var so = new SerializedObject(kinematics);
        SetChain(so, "armRightHierarchy", model,
                 "mixamorig:RightArm", "mixamorig:RightForeArm", "mixamorig:RightHand");
        SetChain(so, "armLeftHierarchy", model,
                 "mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand");

        so.FindProperty("armRightTarget").objectReferenceValue = gunRest;
        so.FindProperty("armRightWeightPosition").floatValue = 1f;

        // Rotation as well as position: the weapon hangs off this wrist, and a
        // wrist left to the walk clip swings the muzzle across the screen.
        so.FindProperty("armRightWeightRotation").floatValue = 1f;

        so.FindProperty("armLeftTarget").objectReferenceValue = offHand;
        so.FindProperty("armLeftWeightPosition").floatValue = 1f;

        // Both wrists are solved now. The grip point on the weapon carries the
        // clip's own off-hand angle (see ReadGrip), so following it keeps the
        // palm on the handguard instead of leaving it facing wherever the low
        // ready pose had it — a flat hand under the barrel.
        so.FindProperty("armLeftWeightRotation").floatValue = 1f;
        so.FindProperty("weightHint").floatValue = 0f;
        so.ApplyModifiedPropertiesWithoutUndo();

        return kinematics;
    }

    private static void ClearHolder(Transform holder)
    {
        if (holder == null) return;

        for (int i = holder.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(holder.GetChild(i).gameObject);

        holder.localPosition = Vector3.zero;
        holder.localRotation = Quaternion.identity;
        holder.localScale = Vector3.one;
    }

    /// <summary>
    /// Points the recoil, the firing code and the body rig at what was just
    /// built.
    /// </summary>
    private static void WireBody(GameObject root, GameObject model, Transform gunRest,
                                 InfimaGames.LowPolyShooterPack.CharacterKinematics kinematics)
    {
        var player = root.GetComponentInChildren<PlayerController>(true);

        // The kick and the sway are put on the rest point, not on the weapon.
        // Everything downstream is solved onto this one transform — the fist
        // reaches for it, the gun hangs off the fist, the off hand goes onto the
        // gun — so moving it moves the whole assembly together. Applied to the
        // weapon alone it would have slid out of a hand that stayed still.
        var bob = gunRest.GetComponent<WeaponBob>();
        if (bob == null) bob = gunRest.gameObject.AddComponent<WeaponBob>();
        bob.player = player;

        // The body animator is driven from PlayerSetup alone — two writers on
        // one Speed parameter is one too many.
        bob.animator = null;

        // The rest point hangs off the camera holder, which is not scaled, so
        // these are plain metres.
        bob.strideLength = DexstandPlayerBuilder.PlayerHeight * 2.1f;
        bob.horizontalAmount = DexstandPlayerBuilder.PlayerHeight * 0.013f;
        bob.verticalAmount = DexstandPlayerBuilder.PlayerHeight * 0.018f;
        bob.kickBack = DexstandPlayerBuilder.PlayerHeight * 0.035f;
        bob.kickUp = DexstandPlayerBuilder.PlayerHeight * 0.018f;
        bob.maxKick = DexstandPlayerBuilder.PlayerHeight * 0.10f;

        // The magazine change is a downloaded clip now, played on the body's
        // upper-body layer while the solver lets go of both arms — there is
        // nothing left here to move the hold point by.

        // The lag behind a turn of the head. In metres, like the bob, so it
        // scales with stature rather than reading as a twitch at 0.6 m.
        bob.swayAmount = DexstandPlayerBuilder.PlayerHeight * 0.10f;
        bob.maxSway = DexstandPlayerBuilder.PlayerHeight * 0.045f;
        bob.swayTilt = 6f;
        bob.holsterDrop = DexstandPlayerBuilder.PlayerHeight * 0.13f;
        bob.landingDip = DexstandPlayerBuilder.PlayerHeight * 0.06f;

        var weapon = root.GetComponentInChildren<WeaponController>(true);
        if (weapon != null)
        {
            weapon.viewmodel = bob;
            EditorUtility.SetDirty(weapon);
        }

        // The switcher shifts that same hold point per weapon, so it needs the
        // bob too — a handgun is not carried where a rifle is.
        var switcher = root.GetComponent<WeaponSwitcher>();
        if (switcher != null)
        {
            switcher.viewmodel = bob;
            EditorUtility.SetDirty(switcher);
        }

        // The old driver pushes an "IsShooting" bool at a controller with no such
        // parameter, and warns about it once a frame while it does.
        var driver = root.GetComponent<ArmsAnimationDriver>();
        if (driver != null)
        {
            driver.animator = null;
            EditorUtility.SetDirty(driver);
        }

        BuildBodyRig(root, model, kinematics);
    }

    /// <summary>
    /// The runtime half of the same arrangement: the solver run every frame, and
    /// the spine bent by the camera's pitch so the shoulders follow the arms.
    /// </summary>
    private static void BuildBodyRig(GameObject root, GameObject model,
                                     InfimaGames.LowPolyShooterPack.CharacterKinematics kinematics)
    {
        var rig = model.GetComponent<BodyRig>();
        if (rig == null) rig = model.AddComponent<BodyRig>();

        rig.cameraHolder = FindChild(root, "CameraHolder");
        rig.switcher = root.GetComponent<WeaponSwitcher>();
        rig.weapon = root.GetComponentInChildren<WeaponController>(true);
        rig.gripName = LeftGripName;
        rig.kinematics = kinematics;
        rig.offHandTarget = model.transform.Find(OffHandTargetName);
        rig.emotes = root.GetComponent<EmoteController>();

        var spine = Spine(model);
        rig.spine = spine;
        rig.spineShare = SpineShare(spine);
        rig.torsoTwist = TorsoTwist;
        rig.torsoLean = TorsoLean;

        var wrist = FindChild(model, BodyLeftHand);
        if (wrist != null)
        {
            var bones = wrist.GetComponentsInChildren<Transform>(true)
                             .Where(t => t != wrist && !t.name.EndsWith("End"))
                             .ToArray();

            rig.offHandThumb = bones.Where(t => t.name.Contains("Thumb")).ToArray();
            rig.offHandFingers = bones.Except(rig.offHandThumb).ToArray();
            rig.fingerCurl = FingerCurl;
            rig.thumbCurl = ThumbCurl;

            Debug.Log($"[Hands] Off hand closes with {rig.offHandFingers.Length} finger bone(s) " +
                      $"and {rig.offHandThumb.Length} thumb bone(s).");
        }

        EditorUtility.SetDirty(rig);
        Debug.Log($"[Hands] Body rig: {spine.Length} spine bone(s) take the pitch, " +
                  $"off hand solved onto '{LeftGripName}'.");
    }

    /// <summary>
    /// How far the chest is turned out of the view. A shooter stands with the
    /// support shoulder forward, and at this stature the off arm is 15 cm long —
    /// square to the camera it cannot reach the handguard at all.
    /// </summary>
    private const float TorsoTwist = 38f;

    /// <summary>
    /// And how far it leans into the shot. Both shoulders come forward with it,
    /// which buys the centimetres that let the weapon be carried out where the
    /// lens can see all of it instead of pressed against the glass.
    /// </summary>
    private const float TorsoLean = 18f;

    /// <summary>
    /// How far the support fingers are closed round the handguard, in degrees per
    /// joint. The rifle idle leaves that hand flat — see BodyRig.CloseOffHand —
    /// and a palm laid along a barrel is what the screenshots kept showing.
    /// </summary>
    private const float FingerCurl = 68f;
    private const float ThumbCurl = 30f;

    /// <summary>
    /// How much closer to the middle a handgun is carried than the rifle, as a
    /// fraction of stature. A pistol is presented in front of the face in both
    /// hands, not held out in the corner of the eye where a rifle's receiver
    /// lives — see the reference the user handed over.
    /// </summary>
    private static readonly Vector3 PistolHold = new Vector3(-0.055f, 0.030f, 0.010f);

    private static Transform[] Spine(GameObject model) =>
        new[] { "mixamorig:Spine", "mixamorig:Spine1", "mixamorig:Spine2" }
            .Select(n => FindChild(model, n))
            .Where(t => t != null)
            .ToArray();

    /// <summary>
    /// Weighted towards the chest. Taking the pitch from the waist alone folds
    /// the character double, and the camera is sitting on its head.
    /// </summary>
    private static float[] SpineShare(Transform[] spine) =>
        spine.Length == 3
            ? new[] { 0.2f, 0.3f, 0.5f }
            : Enumerable.Repeat(1f / Mathf.Max(1, spine.Length), spine.Length).ToArray();

    /// <summary>
    /// Puts the support hand on the weapon that is out and says whether the arm
    /// is long enough to get there.
    ///
    /// A two bone solver cannot stretch past the sum of its bones: asked for a
    /// grip further away than that it straightens the arm and points at it, which
    /// is a hand held in the air beside the handguard. At 0.6 m of stature the
    /// arm is about 15 cm, so this is a real limit and not a rounding error.
    /// </summary>
    private static void SolveOffHand(GameObject root, GameObject model,
                                     InfimaGames.LowPolyShooterPack.CharacterKinematics kinematics,
                                     Transform camera)
    {
        var target = model.transform.Find(OffHandTargetName);
        var switcher = root.GetComponent<WeaponSwitcher>();
        if (target == null || switcher == null || switcher.slots.Length == 0) return;

        var weapon = switcher.slots[0].model;
        var grip = weapon != null ? weapon.transform.Find(LeftGripName) : null;
        if (grip == null)
        {
            Debug.LogWarning("[Hands] The first weapon carries no grip point; the off hand " +
                             "is left where the clip put it.");
            return;
        }

        target.SetPositionAndRotation(grip.position, grip.rotation);
        kinematics.Compute(1f, 0f);

        var shoulder = FindChild(model, "mixamorig:LeftArm");
        var elbow = FindChild(model, "mixamorig:LeftForeArm");
        var wrist = FindChild(model, BodyLeftHand);
        if (shoulder == null || elbow == null || wrist == null) return;

        float reach = Vector3.Distance(shoulder.position, elbow.position) +
                      Vector3.Distance(elbow.position, wrist.position);
        float need = Vector3.Distance(shoulder.position, grip.position);
        Vector3 seen = camera.InverseTransformPoint(wrist.position);

        Debug.Log($"[Hands] Off hand at right {seen.x:F3}, up {seen.y:F3}, fwd {seen.z:F3}; " +
                  $"arm reaches {reach:F3} and the grip is {need:F3} away — " +
                  (need > reach ? $"SHORT BY {need - reach:F3}, pull the weapon in."
                                : "it closes."));
    }

    private static void SetChain(SerializedObject so, string field, GameObject model,
                                 params string[] bones)
    {
        var prop = so.FindProperty(field);
        prop.arraySize = bones.Length;

        for (int i = 0; i < bones.Length; i++)
        {
            var bone = FindChild(model, bones[i]);
            if (bone == null) Debug.LogWarning($"[Hands] {field}: no bone '{bones[i]}'.");
            prop.GetArrayElementAtIndex(i).objectReferenceValue = bone;
        }
    }

    /// <summary>
    /// Marks where the off hand belongs, as a fraction of the way from the
    /// magazine well to the muzzle: 0 leaves it on the well, where both hands
    /// meet on a handgun, and 0.45 puts it up the handguard where a support hand
    /// actually sits. A weapon without both sockets gets nothing and its off arm
    /// is left to the animation.
    /// </summary>
    private static void AddForegrip(GameObject weapon, float alongBarrel, Grip? hold)
    {
        var magazine = FindChild(weapon, MagazineSocket);
        var muzzle = FindChild(weapon, "SOCKET_Muzzle");
        if (magazine == null || muzzle == null)
        {
            Debug.LogWarning($"[Hands] {weapon.name}: no magazine/muzzle pair to measure a " +
                             "grip from — the off hand stays where the clip puts it.");
            return;
        }

        var existing = weapon.transform.Find(LeftGripName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var grip = new GameObject(LeftGripName).transform;
        grip.SetParent(weapon.transform, false);
        grip.position = Vector3.Lerp(magazine.position, muzzle.position, alongBarrel) +
                        Below(weapon, magazine, muzzle) *
                        (DexstandPlayerBuilder.PlayerHeight * 0.022f);

        // The wrist angle the idle clip holds a handguard at, turned onto the
        // carry line with the rest of the hold. The weapon's own rotation stood
        // in for it before and put the back of the hand on the barrel.
        grip.rotation = hold.HasValue ? hold.Value.left : weapon.transform.rotation;
    }

    /// <summary>
    /// Which way is down on a weapon, across the bore.
    ///
    /// A support hand wraps a handguard from underneath; placed on the bore line
    /// itself the palm lies along the top of the barrel, back of the hand to the
    /// sky. The scope socket says where the top is — and the magazine well is the
    /// fallback, since a magazine hangs below the bore on everything.
    /// </summary>
    private static Vector3 Below(GameObject weapon, Transform magazine, Transform muzzle)
    {
        Vector3 bore = (muzzle.position - magazine.position).normalized;

        var scope = FindChild(weapon, "SOCKET_Scope");
        Vector3 across = scope != null
            ? magazine.position - scope.position
            : magazine.position - WorldBounds(weapon).center;

        across -= bore * Vector3.Dot(across, bore);
        return across.sqrMagnitude > 1e-8f ? across.normalized : Vector3.down;
    }

    /// <summary>
    /// The viewmodel proper: a second pair of arms hung off the camera, seen by
    /// nobody but their owner. Only reached with trueFirstPerson off.
    /// </summary>
    private static void BuildOnViewmodel(GameObject root)
    {
        ConvertPackMaterials();

        var controller = BuildController();
        if (controller == null) return;

        var arms = FindChild(root, "Arms");
        if (arms == null)
        {
            Debug.LogError("[Viewmodel] No Arms holder in the player prefab.");
            return;
        }

        var camera = root.GetComponentInChildren<Camera>(true);
        if (camera == null)
        {
            Debug.LogError("[Viewmodel] No camera in the player prefab.");
            return;
        }

        // The holder may still carry the old builder's fitting offsets.
        arms.localPosition = Vector3.zero;
        arms.localRotation = Quaternion.identity;
        arms.localScale = Vector3.one;

        // Clear whatever the previous builder left behind.
        for (int i = arms.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(arms.GetChild(i).gameObject);

        var armsAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPath);
        if (armsAsset == null)
        {
            Debug.LogError("[Viewmodel] Character model missing from the pack.");
            return;
        }

        var model = Object.Instantiate(armsAsset, arms);
        model.name = "Model";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one * Ratio;
        ApplyUniform(model);

        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;

        // Animation events go to the object holding the animator, so the
        // receiver for the pack's callbacks belongs here — without it every
        // shot logs "AnimationEvent 'OnEjectCasing' has no receiver!".
        if (model.GetComponent<ViewmodelAnimationEvents>() == null)
            model.AddComponent<ViewmodelAnimationEvents>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // Bones sit in bind pose until something poses them, and the pack's
        // idle is nothing like bind pose. Measuring the head before sampling
        // put the camera inside the character's chest.
        // Same order the animator uses at runtime: the body idle first,
        // then the weapon grip over the arms. The gun hand is what the
        // placement measures, so it has to come from the grip pose.
        var idlePose = LoadClip(ClipsCommon, "A_FP_PCH_Idle");
        if (idlePose != null) idlePose.SampleAnimation(model, 0f);

        var gripPose = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Idle_Pose");
        if (gripPose != null) gripPose.SampleAnimation(model, 0f);

        PlaceViewmodel(model, camera.transform);

        var socket = FindChild(model, WeaponSocket);
        if (socket == null)
        {
            Debug.LogWarning($"[Viewmodel] No '{WeaponSocket}' bone — weapons on the model root.");
            socket = model.transform;
        }

        // Weapons hang off the hand bone, not the rig's ik_hand_gun socket. The
        // pack drives the hands onto the weapon with IK effectors at runtime —
        // its clips carry no grip at all — so anything parented to the gun
        // socket floats beside a fist that never closes on it.
        var hand = FindChild(model, RightHand) ?? socket;

        LightViewmodel(arms);

        var reload = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Reload");
        BuildLoadout(root, hand, hand, reload != null ? reload.length : 2.2f);
        WireViewmodel(root, model);
        KeepOnScreen(model);

        Debug.Log($"[Viewmodel] Built on the Infima rig — scale {Ratio:F3}, " +
                  $"weapons on '{hand.name}'.");
    }

    /// <summary>
    /// Positions the body by the weapon rather than by the head.
    ///
    /// The pack's camera socket turns out to sit exactly on the head bone, and
    /// lining the camera up with it leaves the gun below the bottom of the
    /// screen — the clips were authored for a different field of view and a
    /// full-size character. So the gun is put where a viewmodel belongs and the
    /// body follows it; the head is hidden, so nobody can tell it moved.
    /// </summary>
    private static void PlaceViewmodel(GameObject model, Transform camera)
    {
        // Kept for the clearance report: it is the one landmark the pack itself
        // documents. Lining the camera up with it leaves the gun at the bottom
        // edge of the screen, so the gun is what actually sets the position.
        CopyCameraSocket(model);

        // The hand carries the weapon, so the hand is what has to land in front
        // of the camera.
        var gun = FindChild(model, RightHand) ?? FindChild(model, WeaponSocket);
        if (gun == null)
        {
            Debug.LogWarning($"[Viewmodel] No '{RightHand}' — body left at the camera origin.");
            return;
        }

        Vector3 rest = WeaponRest + (SweepHand ? SweepHandDelta : Vector3.zero);

        model.transform.position +=
            camera.TransformPoint(rest * DexstandPlayerBuilder.PlayerHeight) - gun.position;

        // Nothing to see from inside a skull, and at this stature the near
        // plane will not clip it away.
        var head = FindChild(model, HeadBone);
        if (head != null) head.localScale = Vector3.zero;

        Debug.Log($"[Viewmodel] Body placed by the gun hand — model at {model.transform.localPosition}.");
        ReportClearance(model, camera);
    }

    /// <summary>
    /// Recreates the pack's camera socket on our own copy of the rig: same
    /// parent bone, same local placement. Returns null when the pack prefab
    /// cannot be read.
    /// </summary>
    private static Transform CopyCameraSocket(GameObject model)
    {
        var existing = FindChild(model, CameraSocket);
        if (existing != null) return existing;

        var pack = AssetDatabase.LoadAssetAtPath<GameObject>(PackCharacterPath);
        var source = pack != null ? FindChild(pack, CameraSocket) : null;
        if (source == null || source.parent == null)
        {
            Debug.LogWarning($"[Viewmodel] '{CameraSocket}' not found in {PackCharacterPath}.");
            return null;
        }

        var bone = FindChild(model, source.parent.name);
        if (bone == null)
        {
            Debug.LogWarning($"[Viewmodel] Socket's bone '{source.parent.name}' is not on our rig.");
            return null;
        }

        var anchor = new GameObject(CameraSocket).transform;
        anchor.SetParent(bone, false);
        anchor.localPosition = source.localPosition;
        anchor.localRotation = source.localRotation;
        anchor.localScale = source.localScale;

        Debug.Log($"[Viewmodel] Camera socket copied onto '{bone.name}' at {source.localPosition}.");
        return anchor;
    }

    /// <summary>
    /// Everything in this pack is a skinned mesh, and a skinned mesh is culled
    /// against bounds derived from its root bone in the imported pose — which
    /// for a viewmodel sitting 3 cm from the lens lands nowhere near the
    /// camera. The whole viewmodel simply vanished. Recomputing the bounds
    /// every frame costs nothing for a handful of meshes and makes it show up.
    /// </summary>
    private static void KeepOnScreen(GameObject model)
    {
        int count = 0;
        foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            skin.updateWhenOffscreen = true;
            count++;
        }
        Debug.Log($"[Viewmodel] {count} skinned meshes set to recompute bounds.");
    }

    /// <summary>
    /// Prints where the body's landmarks end up in front of the camera, in the
    /// camera's own axes: right, up, forward. Guessing this from screenshots
    /// costs a play session each time; the numbers say it in one line.
    /// </summary>
    private static void ReportClearance(GameObject model, Transform camera)
    {
        foreach (var bone in new[] { CameraSocket, HeadBone, "spine_03", RightHand, WeaponSocket })
        {
            var t = FindChild(model, bone);
            if (t == null) continue;
            var local = camera.InverseTransformPoint(t.position);
            Debug.Log($"[Viewmodel]   {bone}: right {local.x:F3}, up {local.y:F3}, fwd {local.z:F3}");
        }

        var body = WorldBounds(model);
        var c = camera.InverseTransformPoint(body.center);
        Debug.Log($"[Viewmodel]   mesh centre: right {c.x:F3}, up {c.y:F3}, fwd {c.z:F3}, " +
                  $"size {body.size}");
    }

    /// <summary>
    /// Rebuilds every weapon on the new rig and re-points the switcher at them.
    /// The old loadout hung off a bone of the placeholder arms, so replacing the
    /// arms left every slot holding a destroyed object — which is why switching
    /// stopped working.
    /// </summary>
    /// <param name="parent">Where the weapon objects are hung.</param>
    /// <param name="hand">The fist the placement offsets are measured from. The
    /// same transform as <paramref name="parent"/> on the viewmodel, a bone
    /// above it on the body — where a holder sits between the two so the recoil
    /// can push the weapon back without dragging the arm with it.</param>
    /// <param name="reloadSeconds">Length of the clip that shows the magazine
    /// change. Guessed at instead, the weapon either fires again with the
    /// magazine still in mid-air or sits dead after the hands are done.</param>
    private static void BuildLoadout(GameObject root, Transform parent, Transform hand,
                                     float reloadSeconds, Grip? hold = null)
    {
        var switcher = root.GetComponent<WeaponSwitcher>();
        if (switcher == null) switcher = root.AddComponent<WeaponSwitcher>();

        var built = new System.Collections.Generic.List<WeaponSwitcher.Slot>();

        var camera = root.GetComponentInChildren<Camera>(true).transform;

        Debug.Log($"[Hands] Reload takes {reloadSeconds:F2}s — from the clip itself.");

        var rifle = Spawn(RiflePath, parent, "AK");
        if (rifle != null)
        {
            AttachPart(rifle, MagazineSocket, RifleMagPath);
            ApplyGunmetal(rifle);
            AlignBarrel(rifle, FindChild(rifle, "SOCKET_Muzzle"), camera);

            var riflePlacement = RiflePlacement;
            if (SweepRifle)
            {
                riflePlacement.scale = SweepScale;
                riflePlacement.rest = SweepRest;
            }
            if (SweepHand) riflePlacement.rest -= SweepHandDelta;

            ReportSockets(rifle, camera, hand);
            if (hold.HasValue) GripWeapon(rifle, camera, hand, hold.Value, riflePlacement.scale);
            else PlaceWeapon(rifle, camera, hand, riflePlacement);
            ReportWeapon(rifle, camera);
            AddMuzzle(rifle, FindChild(rifle, "SOCKET_Muzzle"));

            // Far enough up the handguard to look like a hold, near enough that
            // a 0.6 m character's arm still reaches it — measured at build time
            // and reported by the grip probe.
            AddForegrip(rifle, 0.40f, hold);
            MeasureAim(rifle, camera, BodyWeaponRest * DexstandPlayerBuilder.PlayerHeight,
                       out var rifleAim, out var rifleTilt);
            built.Add(new WeaponSwitcher.Slot
            {
                displayName = "AK", model = rifle,
                aimOffset = rifleAim, aimTilt = rifleTilt,
                fireRate = 0.1f, damage = 25f, range = 500f, automatic = true,
                recoilPitch = 0.32f, recoilYaw = 0.18f, baseSpread = 1f,
                sprayPattern = DexstandPlayerBuilder.RifleSprayPattern,
                magazineSize = 30, reloadSeconds = reloadSeconds,
            });
        }

        var pistol = Spawn(PistolPath, parent, "Pistol");
        if (pistol != null)
        {
            AttachPart(pistol, MagazineSocket, PistolMagPath);
            ApplyGunmetal(pistol);
            AlignBarrel(pistol, FindChild(pistol, "SOCKET_Muzzle"), camera);
            if (hold.HasValue) GripWeapon(pistol, camera, hand, hold.Value, PistolPlacement.scale);
            else PlaceWeapon(pistol, camera, hand, PistolPlacement);
            ReportWeapon(pistol, camera);
            AddMuzzle(pistol, FindChild(pistol, "SOCKET_Muzzle"));

            // Both hands wrap the same grip on a handgun, so the off hand is
            // sent to the magazine well itself rather than up the barrel.
            AddForegrip(pistol, 0f, hold);
            MeasureAim(pistol, camera,
                       (BodyWeaponRest + PistolHold) * DexstandPlayerBuilder.PlayerHeight,
                       out var pistolAim, out var pistolTilt);
            built.Add(new WeaponSwitcher.Slot
            {
                displayName = "Pistol", model = pistol,
                holdOffset = PistolHold * DexstandPlayerBuilder.PlayerHeight,
                aimOffset = pistolAim,
                aimTilt = pistolTilt,
                fireRate = 0.18f, damage = 34f, range = 300f, automatic = false,
                recoilPitch = 1.4f, recoilYaw = 0.2f, baseSpread = 0.6f,
                sprayPattern = DexstandPlayerBuilder.PistolSprayPattern,
                magazineSize = 12, reloadSeconds = reloadSeconds * 0.8f,
            });
        }

        // The pack ships no blade, so the old knife stays — sized to the hand
        // and angled across the view the way a knife is actually carried.
        var knife = Spawn(KnifePath, parent, "Knife");
        if (knife != null)
        {
            ApplyGunmetal(knife);

            // A combat knife is about a quarter of a man's height overall. Drawn
            // at 0.30 of the pack's reference it read as a scalpel, tip barely
            // clearing the fist.
            FitToLength(knife, 0.45f * Ratio);

            // The blade has no muzzle socket to point with, so its own longest
            // side stands in for one: aimed down the view axis first, then
            // canted by the placement like the guns are.
            Vector3 edge = PointLongestAxis(knife, camera);
            if (hold.HasValue) GripKnife(knife, camera, hand, edge);
            else PlaceWeapon(knife, camera, hand, KnifePlacement);
            ReportWeapon(knife, camera);

            // Where the melee ray starts is the point of the blade, which is the
            // far end along that same edge — not the far corner of a world-axis
            // box, which is what this used to take and which moved every time
            // the player turned around.
            Vector3 point = knife.transform.TransformDirection(edge).normalized;
            var blade = WorldBounds(knife);
            AddMuzzleAt(knife, blade.center + point * Vector3.Dot(
                blade.extents, new Vector3(Mathf.Abs(point.x), Mathf.Abs(point.y), Mathf.Abs(point.z))));

            Debug.Log($"[Viewmodel] Knife edge points right {camera.InverseTransformDirection(point).x:F2}, " +
                      $"up {camera.InverseTransformDirection(point).y:F2}, " +
                      $"fwd {camera.InverseTransformDirection(point).z:F2}.");

            built.Add(new WeaponSwitcher.Slot
            {
                displayName = "Knife", model = knife,
                fireRate = 0.45f, damage = 60f,
                range = DexstandPlayerBuilder.PlayerHeight * 0.9f,
                automatic = false, melee = true,
                meleeRadius = DexstandPlayerBuilder.PlayerHeight * 0.2f,
                recoilPitch = 0.2f, recoilYaw = 0.1f, baseSpread = 0f,
                magazineSize = 0,
            });
        }

        switcher.slots = built.ToArray();
        Link(switcher, "weapon", root.GetComponentInChildren<WeaponController>(true));

        for (int i = 0; i < built.Count; i++)
            if (built[i].model != null) built[i].model.SetActive(i == 0);

        EditorUtility.SetDirty(switcher);
        Debug.Log($"[Viewmodel] Loadout: {string.Join(", ", built.Select(b => b.displayName))}");
    }

    /// <summary>
    /// A lamp at the lens so the hands and weapon are readable.
    ///
    /// The viewmodel hangs under the camera, in the player's own shadow, and
    /// against a sunlit yard it collapses into one black silhouette — which is
    /// why a magazine that was fitted and rendering looked missing. A short
    /// range is what makes this safe: at this stature the world is metres away
    /// and the weapon is centimetres, so the light reaches nothing else.
    /// </summary>
    private static void LightViewmodel(Transform arms)
    {
        var existing = arms.Find("ViewmodelLight");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var go = new GameObject("ViewmodelLight");
        go.transform.SetParent(arms, false);

        // Set back behind the lens rather than on it. At the lens the lamp was
        // one hand's width from the weapon, and inverse-square makes that a
        // knife edge: the reload lifts the rifle a few centimetres closer and it
        // burns out white. From further back the same few centimetres barely
        // change the exposure.
        go.transform.localPosition = new Vector3(0f, 0f, -DexstandPlayerBuilder.PlayerHeight * 0.25f);

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = DexstandPlayerBuilder.PlayerHeight * 1.6f;

        // Brightness falls with the square of the distance, and the weapon is
        // barely a hand's width away: an ordinary intensity of 1.5 burned the
        // whole viewmodel to white. This is tuned for ~0.3m, not for a room.
        light.intensity = 0.065f;
        light.color = new Color(1f, 0.97f, 0.92f);
        light.shadows = LightShadows.None;
        light.renderMode = LightRenderMode.ForcePixel;

        Debug.Log($"[Viewmodel] Lamp at the lens — range {light.range:F2}m.");
    }

    /// <summary>
    /// Hangs a detachable part — the magazine — on its socket. Without this the
    /// rifle renders with an empty magazine well, because the pack keeps every
    /// attachment in its own mesh and fits it at runtime.
    /// </summary>
    private static void AttachPart(GameObject weapon, string socketName, string path)
    {
        var socket = FindChild(weapon, socketName);
        if (socket == null)
        {
            Debug.LogWarning($"[Viewmodel] {weapon.name}: no '{socketName}' socket.");
            return;
        }

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
        {
            Debug.LogWarning($"[Viewmodel] Missing part {path}");
            return;
        }

        var part = Object.Instantiate(asset, socket);
        part.name = System.IO.Path.GetFileNameWithoutExtension(path);
        part.transform.localPosition = Vector3.zero;
        part.transform.localRotation = Quaternion.identity;
        part.transform.localScale = Vector3.one;

        var b = WorldBounds(part);
        Debug.Log($"[Viewmodel] {weapon.name}: {part.name} fitted to {socketName}, " +
                  $"{part.GetComponentsInChildren<Renderer>(true).Length} renderer(s), " +
                  $"size {b.size}.");
    }

    /// <summary>
    /// Paints a weapon in issued gunmetal: steel receiver, matte polymer
    /// furniture, brass rounds. The slot names the FBX carries — Body, Bolt,
    /// Grip, Stock, Bullets — say which is which, so one rule covers every
    /// weapon in the pack and anything imported later that names its slots the
    /// same way.
    /// </summary>
    private static void ApplyGunmetal(GameObject weapon)
    {
        // Light enough to read. The first pass was near black, and against a
        // bright yard the whole weapon collapsed into one silhouette — the
        // magazine was fitted and rendering, but nobody could see it.
        var steel = EnsureMaterial(SteelPath, new Color(0.34f, 0.35f, 0.37f), 0.80f, 0.40f);
        var polymer = EnsureMaterial(PolymerPath, new Color(0.16f, 0.16f, 0.17f), 0.0f, 0.25f);
        var brass = EnsureMaterial(BrassPath, new Color(0.62f, 0.47f, 0.18f), 0.9f, 0.62f);
        if (steel == null) return;

        int slots = 0;
        foreach (var r in weapon.GetComponentsInChildren<Renderer>(true))
        {
            var current = r.sharedMaterials;
            var next = new Material[current.Length];

            for (int i = 0; i < current.Length; i++)
            {
                string slot = current[i] != null ? current[i].name.ToLowerInvariant() : string.Empty;

                if (slot.Contains("bullet") || slot.Contains("casing")) next[i] = brass;
                else if (slot.Contains("grip") || slot.Contains("stock") ||
                         slot.Contains("handle")) next[i] = polymer;
                else next[i] = steel;

                slots++;
            }

            r.sharedMaterials = next;
        }

        Debug.Log($"[Viewmodel] {weapon.name}: {slots} material slots painted gunmetal.");
    }

    /// <summary>
    /// Dresses the arms in issued kit: the pack's own glove texture, dimmed.
    ///
    /// The pack ships one skin for this character, `M_FP_Camo_001_001`, and at
    /// full brightness it is a yellow-and-blue camo that made the player's own
    /// hands the loudest thing on the phone screen. Painting them a flat colour
    /// instead killed the yellow but also every seam and knuckle with it, and
    /// the hands went back to reading as two smooth lumps — the texture is what
    /// makes them look like thin gloved fingers rather than mittens. So the map
    /// stays and a dark olive tint multiplies it down: the pattern survives, the
    /// yellow does not.
    /// </summary>
    private static void ApplyUniform(GameObject model)
    {
        // The character's own skin, so the hands in front of the lens are the
        // same hands other players see. This only became possible with the
        // transplanted arms: the pack's "Cubic" mesh dropped each facet on a
        // single point of the map instead of unwrapping across it, so any
        // texture on it broke into flat blocks — that is where the yellow and
        // blue hands came from. The olive stays as the fallback for when the
        // character has not been imported yet.
        var cloth = AssetDatabase.LoadAssetAtPath<Material>(SkinPath)
                    ?? EnsureMaterial(UniformPath, new Color(0.13f, 0.13f, 0.14f), 0.05f, 0.30f);
        if (cloth == null) return;

        int slots = 0;
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            var next = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < next.Length; i++)
            {
                next[i] = cloth;
                slots++;
            }
            r.sharedMaterials = next;
        }

        Debug.Log($"[Viewmodel] Arms: {slots} material slots dressed in olive.");
    }

    /// <summary>Creates or refreshes a plain URP Lit material.</summary>
    private static Material EnsureMaterial(string path, Color colour, float metallic, float smoothness)
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[Viewmodel] URP Lit shader not found.");
            return null;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(lit);
            AssetDatabase.CreateAsset(mat, path);
        }

        mat.shader = lit;
        mat.SetColor("_BaseColor", colour);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Smoothness", smoothness);

        // A texture left over from an earlier build would tint the colour.
        mat.SetTexture("_BaseMap", null);
        mat.SetTexture("_MainTex", null);

        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// Turns the weapon so its barrel points where the player is looking.
    ///
    /// The pack's weapon meshes are modelled barrel-up: left as imported they
    /// aim at the sky. This sets the rotation once at build time, which the
    /// hand bone then carries as a fixed local offset — so the gun still moves
    /// with the grip through every animation.
    /// </summary>
    private static void AlignBarrel(GameObject weapon, Transform muzzle, Transform camera)
    {
        if (muzzle == null)
        {
            Debug.LogWarning($"[Viewmodel] {weapon.name}: no muzzle socket, rotation left as imported.");
            return;
        }

        Vector3 barrel = weapon.transform.InverseTransformPoint(muzzle.position);
        if (barrel.sqrMagnitude < 1e-8f) return;
        barrel.Normalize();

        // Which way is up on the weapon itself. World up is meaningless in the
        // mesh's own axes, and the scope socket turned out to lie near enough
        // to the barrel to give a useless direction — both guns came out
        // upside down, magazine in the air. The magazine socket cannot: a
        // magazine hangs below the bore on every weapon there is.
        Vector3 up = Vector3.up;
        var reference = FindChild(weapon, MagazineSocket) ?? FindChild(weapon, "SOCKET_Grip");
        if (reference != null)
        {
            Vector3 down = weapon.transform.InverseTransformPoint(reference.position);
            down -= barrel * Vector3.Dot(down, barrel);
            if (down.sqrMagnitude > 1e-8f) up = -down.normalized;
        }
        else
        {
            Debug.LogWarning($"[Viewmodel] {weapon.name}: nothing to tell up from; roll is a guess.");
        }

        weapon.transform.rotation =
            Quaternion.LookRotation(camera.forward, camera.up) *
            Quaternion.Inverse(Quaternion.LookRotation(barrel, up));

        Vector3 aim = camera.InverseTransformDirection(
            (muzzle.position - weapon.transform.position).normalized);

        // The check that catches an upside-down gun without anyone squinting at
        // a screenshot: the magazine has to sit below the bore.
        string roll = "no magazine to check";
        var mag = FindChild(weapon, MagazineSocket);
        if (mag != null)
        {
            float lift = camera.InverseTransformPoint(mag.position).y -
                         camera.InverseTransformPoint(muzzle.position).y;
            roll = lift < 0f ? $"magazine {(-lift):F3} below the bore — upright"
                             : $"MAGAZINE {lift:F3} ABOVE THE BORE — UPSIDE DOWN";
        }

        Debug.Log($"[Viewmodel] {weapon.name}: barrel was {barrel} locally, now points " +
                  $"right {aim.x:F2}, up {aim.y:F2}, fwd {aim.z:F2}; {roll}.");
    }

    private static GameObject Spawn(string path, Transform parent, string name)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
        {
            Debug.LogWarning($"[Viewmodel] Missing weapon model {path}");
            return null;
        }

        var go = Object.Instantiate(asset, parent);
        go.name = name;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        return go;
    }

    /// <summary>
    /// The switcher looks for a direct child called "Muzzle", so the pack's own
    /// socket — buried somewhere in the weapon hierarchy — gets one placed on it.
    /// </summary>
    private static void AddMuzzle(GameObject weapon, Transform socket)
    {
        if (socket == null)
        {
            Debug.LogWarning($"[Viewmodel] {weapon.name}: no SOCKET_Muzzle, using the bounds tip.");
            var b = WorldBounds(weapon);
            AddMuzzleAt(weapon, new Vector3(b.center.x, b.center.y, b.max.z));
            return;
        }
        AddMuzzleAt(weapon, socket.position);
    }

    /// <summary>
    /// Sizes, angles and positions a weapon in front of the lens.
    ///
    /// Order matters: scaling moves the mesh relative to its own pivot and
    /// turning it moves it relative to the hand, so both have to happen before
    /// anything is measured. The weapon is then shifted bodily until the centre
    /// of what the camera can see of it lands on the rest point — measuring the
    /// rendered bounds rather than the pivot is the whole trick, since the pivot
    /// of every mesh in this pack sits somewhere arbitrary inside the receiver.
    ///
    /// The weapon is a child of the hand bone, so this world-space nudge is
    /// stored as a local offset and rides along with every animation afterwards.
    /// </summary>
    private static void PlaceWeapon(GameObject weapon, Transform camera, Transform hand,
                                    Placement placement)
    {
        weapon.transform.localScale *= placement.scale;

        // Around the camera's axes, not the weapon's: "turn it eight degrees to
        // the left of where I am looking" is the thing being described.
        weapon.transform.rotation =
            Quaternion.AngleAxis(placement.tilt.y, camera.up) *
            Quaternion.AngleAxis(placement.tilt.x, camera.right) *
            Quaternion.AngleAxis(placement.tilt.z, camera.forward) *
            weapon.transform.rotation;

        var anchor = placement.anchor != null ? FindChild(weapon, placement.anchor) : null;
        if (placement.anchor != null && anchor == null)
        {
            Debug.LogWarning($"[Viewmodel] {weapon.name}: no '{placement.anchor}' to hold it by; " +
                             $"falling back to its centre.");
        }

        Vector3 held = anchor != null ? anchor.position : WorldBounds(weapon).center;
        Vector3 offset = placement.rest * DexstandPlayerBuilder.PlayerHeight;

        // With a landmark to hold it by, the weapon is hung off the hand and the
        // offset is measured from there. Without one there is nothing to hold,
        // so the offset is measured from the lens and only composition matters.
        Vector3 target = anchor != null && hand != null
            ? hand.position + camera.TransformVector(offset)
            : camera.TransformPoint(offset);

        weapon.transform.position += target - held;
    }

    /// <summary>
    /// Where the magazine well sits relative to the fist, in the camera's axes
    /// and as a fraction of stature. A pistol grip is immediately behind the
    /// well, so a hand closed on the grip has the well just in front of it and a
    /// few millimetres below — carried 10 cm out instead, which is what the old
    /// placement did, the fist ends up somewhere behind the stock holding air.
    /// </summary>
    internal static readonly Vector3 FistToMagazine = new Vector3(0.004f, 0.020f, 0.022f);

    /// <summary>
    /// Puts a weapon in a fist that is already closed on a grip.
    ///
    /// The hands are no longer arranged around the weapon; the weapon is placed
    /// into the hold the clip provides. AlignBarrel has already aimed the bore
    /// down the view axis, so all that is left is to turn it onto the same carry
    /// line both wrists were turned onto, and to slide it until its magazine well
    /// is in the firing hand.
    /// </summary>
    private static void GripWeapon(GameObject weapon, Transform camera, Transform hand,
                                   Grip hold, float scale)
    {
        weapon.transform.localScale *= scale;

        Quaternion carry =
            Quaternion.LookRotation(camera.TransformDirection(BarrelDirection.normalized), camera.up) *
            Quaternion.Inverse(Quaternion.LookRotation(camera.forward, camera.up));
        weapon.transform.rotation = carry * weapon.transform.rotation;

        var anchor = FindChild(weapon, MagazineSocket);
        Vector3 held = anchor != null ? anchor.position : WorldBounds(weapon).center;
        Vector3 target = hand.position +
                         camera.TransformVector(FistToMagazine * DexstandPlayerBuilder.PlayerHeight);

        weapon.transform.position += target - held;

        Vector3 seen = camera.InverseTransformPoint(WorldBounds(weapon).center);
        Debug.Log($"[Hands] {weapon.name} gripped: centre right {seen.x:F3}, up {seen.y:F3}, " +
                  $"fwd {seen.z:F3}; hands {hold.span:F3} apart in the clip.");
    }

    /// <summary>
    /// Where the hold point has to go, and how far it has to turn, for this
    /// weapon's sights to sit on the middle of the screen.
    ///
    /// Both are measured off the weapon as built rather than typed in per gun:
    /// the turn is whatever takes the carry angle back onto the view axis, and
    /// the shift is wherever the sight line ends up once it has. A little is
    /// left on the low side, because sights that land exactly on the crosshair
    /// hide the thing being shot at.
    /// </summary>
    private static void MeasureAim(GameObject weapon, Transform camera, Vector3 hold,
                                   out Vector3 offset, out Vector3 tilt)
    {
        Vector3 carry = BarrelDirection.normalized;
        tilt = new Vector3(Mathf.Asin(carry.y) * Mathf.Rad2Deg,
                           -Mathf.Atan2(carry.x, carry.z) * Mathf.Rad2Deg,
                           0f);

        // The muzzle, not the scope rail: once the weapon is turned onto the view
        // axis the bore runs parallel to it, so putting the muzzle on the middle
        // of the screen puts the whole line of the shot there. The pack's scope
        // socket sits off to one side of the receiver and pulled the rifle with
        // it.
        var sight = FindChild(weapon, "SOCKET_Muzzle") ??
                    FindChild(weapon, "SOCKET_Scope") ??
                    FindChild(weapon, MagazineSocket);
        if (sight == null)
        {
            offset = Vector3.zero;
            return;
        }

        // Where the sight ends up once the hold point has turned. The assembly
        // pivots about the hold point, not about the lens, so the turn is
        // applied to the sight's offset from the hold point — rotating it about
        // the camera instead leaves the weapon pointing off to one side, which
        // is what the first attempt did.
        Vector3 local = camera.InverseTransformPoint(sight.position);
        Vector3 turned = hold + Quaternion.Euler(tilt) * (local - hold);

        // Pushed out as well as across. Brought straight back to the eye the
        // receiver fills half the frame and the sight line is lost inside it.
        offset = new Vector3(-turned.x,
                             -turned.y - DexstandPlayerBuilder.PlayerHeight * 0.006f,
                             DexstandPlayerBuilder.PlayerHeight * 0.09f);

        Debug.Log($"[Aim] {weapon.name}: sight at (r {local.x:F3} u {local.y:F3} f {local.z:F3}), " +
                  $"turned to (r {turned.x:F3} u {turned.y:F3} f {turned.z:F3}) by " +
                  $"pitch {tilt.x:F1} yaw {tilt.y:F1}; hold shifts by " +
                  $"(r {offset.x:F3} u {offset.y:F3} f {offset.z:F3}).");
    }

    /// <summary>
    /// Puts the knife's handle in the fist.
    ///
    /// A blade carries no sockets at all, so its own longest axis stands in for
    /// a barrel and the quarter of it nearest the body stands in for a grip.
    /// Placed by its centre against the lens, as the guns used to be, it floated
    /// beside an open hand with nothing in it.
    /// </summary>
    private static void GripKnife(GameObject knife, Transform camera, Transform hand, Vector3 edge)
    {
        Vector3 point = knife.transform.TransformDirection(edge).normalized;

        // Carried point-forward along the same line the rifle is aimed down, and
        // rolled so the flat of the blade faces the player rather than the
        // ground — face-on it reads as a plank.
        Quaternion carry =
            Quaternion.LookRotation(camera.TransformDirection(BarrelDirection.normalized), camera.up) *
            Quaternion.Inverse(Quaternion.LookRotation(point, camera.up));
        knife.transform.rotation = Quaternion.AngleAxis(95f, camera.TransformDirection(BarrelDirection.normalized)) *
                                   carry * knife.transform.rotation;

        var blade = WorldBounds(knife);
        Vector3 along = knife.transform.TransformDirection(edge).normalized;
        float half = Vector3.Dot(blade.extents,
            new Vector3(Mathf.Abs(along.x), Mathf.Abs(along.y), Mathf.Abs(along.z)));

        // The butt end, a quarter of the way in — where a hand actually closes.
        Vector3 handle = blade.center - along * (half * 0.55f);
        knife.transform.position += hand.position - handle;
    }

    /// <summary>
    /// Lists every socket the weapon carries, measured from the hand along the
    /// camera's axes, before the weapon is moved.
    ///
    /// Which landmark to hold a gun by is the whole question — the magazine well
    /// and the pistol grip are a few centimetres apart and the fist has to be on
    /// one of them — and the pack's socket names do not say where they sit.
    /// </summary>
    private static void ReportSockets(GameObject weapon, Transform camera, Transform hand)
    {
        if (hand == null) return;

        foreach (var t in weapon.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("SOCKET_")) continue;

            Vector3 fromHand = camera.InverseTransformVector(t.position - hand.position);
            Debug.Log($"[Viewmodel] {weapon.name}.{t.name}: right {fromHand.x:F3}, " +
                      $"up {fromHand.y:F3}, fwd {fromHand.z:F3} from the fist.");
        }
    }

    /// <summary>
    /// Turns a model so that its longest side points where the camera looks,
    /// and hands back that side as a direction in the model's own space — the
    /// only direction information a mesh without sockets carries. For a blade
    /// it is the edge, which is both where it is aimed and where its tip is.
    ///
    /// Which end of that axis is the tip has to be decided as well: the mesh
    /// bounds are centred on the pivot and a knife's pivot is at the handle, so
    /// whichever end is further from the pivot is the pointed one. Aim the
    /// wrong end forward and the knife is held backwards, blade in the fist.
    /// </summary>
    private static Vector3 PointLongestAxis(GameObject go, Transform camera)
    {
        var filter = go.GetComponentInChildren<MeshFilter>(true);
        if (filter == null || filter.sharedMesh == null)
        {
            Debug.LogWarning($"[Viewmodel] {go.name}: no mesh to take a direction from.");
            return Vector3.forward;
        }

        var mesh = filter.sharedMesh.bounds;
        Vector3 size = mesh.size;
        Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right
                     : size.y >= size.z ? Vector3.up
                     : Vector3.forward;

        if (Vector3.Dot(mesh.center, axis) < 0f) axis = -axis;

        // The mesh may hang off a child transform of its own.
        Vector3 local = go.transform.InverseTransformDirection(
            filter.transform.TransformDirection(axis));
        if (local.sqrMagnitude < 1e-8f) return Vector3.forward;
        local.Normalize();

        go.transform.rotation =
            Quaternion.FromToRotation(go.transform.TransformDirection(local), camera.forward) *
            go.transform.rotation;

        return local;
    }

    /// <summary>
    /// Prints the weapon's extent in the camera's own axes. Everything about a
    /// viewmodel is judged by what lands inside the frustum, and the pivot sits
    /// mid-weapon — so the stock hangs behind the lens and moving the gun
    /// forward makes it look bigger, not smaller. The numbers make that legible.
    /// </summary>
    private static void ReportWeapon(GameObject weapon, Transform camera)
    {
        var b = WorldBounds(weapon);
        Vector3 min = camera.InverseTransformPoint(b.min);
        Vector3 max = camera.InverseTransformPoint(b.max);

        Vector3 centre = camera.InverseTransformPoint(b.center);

        Debug.Log($"[Viewmodel] {weapon.name} in view: centre ({centre.x:F3}, {centre.y:F3}, " +
                  $"{centre.z:F3}), x {Mathf.Min(min.x, max.x):F3}..{Mathf.Max(min.x, max.x):F3}, " +
                  $"y {Mathf.Min(min.y, max.y):F3}..{Mathf.Max(min.y, max.y):F3}, " +
                  $"z {Mathf.Min(min.z, max.z):F3}..{Mathf.Max(min.z, max.z):F3}");

        // Printed in the same form the playtest prints at runtime, so the pose
        // the builder sampled can be compared with the pose the animator settles
        // into. They have to agree or nothing placed here survives play mode.
        Vector3 euler = (Quaternion.Inverse(camera.rotation) * weapon.transform.rotation).eulerAngles;
        Debug.Log($"[Viewmodel] {weapon.name} turned relative to the lens: " +
                  $"pitch {Mathf.DeltaAngle(0f, euler.x):F1}, yaw {Mathf.DeltaAngle(0f, euler.y):F1}, " +
                  $"roll {Mathf.DeltaAngle(0f, euler.z):F1}");
    }

    private static void AddMuzzleAt(GameObject weapon, Vector3 worldPosition)
    {
        var muzzle = new GameObject("Muzzle").transform;
        muzzle.SetParent(weapon.transform, false);
        muzzle.position = worldPosition;
        muzzle.rotation = weapon.transform.rotation;
    }

    /// <summary>Scales a model so its longest side matches a world length.</summary>
    private static void FitToLength(GameObject go, float metres)
    {
        var b = WorldBounds(go);
        float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (longest < 1e-5f) return;
        go.transform.localScale *= metres / longest;
    }

    private static Bounds WorldBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);

        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    /// <summary>
    /// Points the bob, the animator and the firing code at the new model.
    /// </summary>
    private static void WireViewmodel(GameObject root, GameObject model)
    {
        var bob = model.GetComponent<WeaponBob>();
        if (bob == null) bob = model.AddComponent<WeaponBob>();
        bob.player = root.GetComponentInChildren<PlayerController>(true);
        bob.animator = model.GetComponent<Animator>();

        // Real walk and run clips now carry the footstep motion, so the
        // procedural bob only has to add recoil and a little sway.
        bob.horizontalAmount = 0f;
        bob.verticalAmount = 0f;
        bob.strideLength = DexstandPlayerBuilder.PlayerHeight * 2.1f;

        var weapon = root.GetComponentInChildren<WeaponController>(true);
        if (weapon != null)
        {
            weapon.viewmodel = bob;
            EditorUtility.SetDirty(weapon);
        }

        // The old driver pushed an "IsShooting" bool the new controller has no
        // parameter for; leaving it wired logs a warning every frame.
        var driver = root.GetComponent<ArmsAnimationDriver>();
        if (driver != null)
        {
            driver.animator = null;
            EditorUtility.SetDirty(driver);
        }
    }

    /// <summary>
    /// Idle, walk and run blended by speed, with fire, reload and inspect
    /// fired as one-shots on top.
    /// </summary>
    private static AnimatorController BuildController()
    {
        // The pack splits the two apart: the _Common clips move an empty-handed
        // body, and the weapon-specific poses put the hands on the gun. Playing
        // only the common idle left the fingers splayed with the rifle floating
        // beside them, so the grip goes on its own masked layer.
        var idle = LoadClip(ClipsCommon, "A_FP_PCH_Idle");
        var grip = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Idle_Pose");
        var walk = LoadClip(ClipsCommon, "A_FP_PCH_Walk_F");
        var run = LoadClip(ClipsCommon, "A_FP_PCH_Run_01");
        var fire = LoadClip(ClipsRifle, "A_FP_PCH_AR_Fire");
        var reload = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Reload");
        var inspect = LoadClip(ClipsRifle, "A_FP_PCH_AR_01_Inspect");

        if (idle == null)
        {
            Debug.LogError("[Viewmodel] Idle clip missing — controller not built.");
            return null;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Animations"))
            AssetDatabase.CreateFolder("Assets", "Animations");
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
            AssetDatabase.DeleteAsset(ControllerPath);

        var c = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        c.AddParameter("Speed", AnimatorControllerParameterType.Float);
        c.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
        c.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        c.AddParameter("Inspect", AnimatorControllerParameterType.Trigger);

        var sm = c.layers[0].stateMachine;

        var move = sm.AddState("Move");
        var blend = new BlendTree
        {
            name = "Move",
            blendParameter = "Speed",
            blendType = BlendTreeType.Simple1D,
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(blend, c);

        // Thresholds in this world's metres per second, not the pack's: the
        // player is a third of the authored stature and walks proportionally.
        // Standing still means holding the weapon, so the grip pose is the base
        // idle too — not only an overlay. The empty-handed common idle is only
        // ever seen blended into walking.
        blend.AddChild(grip != null ? grip : idle, 0f);
        if (walk != null) blend.AddChild(walk, DexstandPlayerBuilder.PlayerHeight * 3f);
        if (run != null) blend.AddChild(run, DexstandPlayerBuilder.PlayerHeight * 6f);

        move.motion = blend;
        sm.defaultState = move;

        // Everything the hands do rides above the locomotion, on a layer masked
        // to the arms. Fire, reload and inspect belong there too — on the base
        // layer the arm layer would simply overwrite them.
        BuildArmsLayer(c, grip, fire, reload, inspect);

        AssetDatabase.SaveAssets();
        Debug.Log($"[Viewmodel] Controller built — idle:{idle != null} grip:{grip != null} " +
                  $"walk:{walk != null} run:{run != null} fire:{fire != null} " +
                  $"reload:{reload != null} inspect:{inspect != null}");
        return c;
    }

    /// <summary>
    /// Adds the masked arm layer: a held pose that overrides whatever the legs
    /// are doing, with the weapon one-shots on top of it.
    /// </summary>
    private static void BuildArmsLayer(AnimatorController c, AnimationClip grip,
                                       AnimationClip fire, AnimationClip reload,
                                       AnimationClip inspect)
    {
        if (grip == null)
        {
            Debug.LogWarning("[Viewmodel] No weapon grip pose — arms left on the locomotion clips.");
            return;
        }

        var machine = new AnimatorStateMachine
        {
            name = "Arms",
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(machine, c);

        var hold = machine.AddState("Hold");
        hold.motion = grip;
        machine.defaultState = hold;

        AddOneShot(machine, hold, "Fire", fire, "Fire", 0.02f, 0.75f);
        AddOneShot(machine, hold, "Reload", reload, "Reload", 0.1f, 0.9f);
        AddOneShot(machine, hold, "Inspect", inspect, "Inspect", 0.15f, 0.9f);

        c.AddLayer(new AnimatorControllerLayer
        {
            name = "Arms",
            stateMachine = machine,
            defaultWeight = 1f,
            blendingMode = AnimatorLayerBlendingMode.Override,
            avatarMask = BuildArmMask(),
        });
    }

    /// <summary>
    /// A mask covering both arms and the weapon sockets, by transform path —
    /// the rig is Generic, so there are no humanoid body parts to tick.
    /// </summary>
    private static AvatarMask BuildArmMask()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPath);
        if (asset == null) return null;

        if (AssetDatabase.LoadAssetAtPath<AvatarMask>(ArmMaskPath) != null)
            AssetDatabase.DeleteAsset(ArmMaskPath);

        var bones = asset.GetComponentsInChildren<Transform>(true)
                         .Where(t => t != asset.transform)
                         .ToArray();

        var mask = new AvatarMask { transformCount = bones.Length };
        int active = 0;

        for (int i = 0; i < bones.Length; i++)
        {
            mask.SetTransformPath(i, AnimationUtility.CalculateTransformPath(bones[i], asset.transform));

            bool arm = IsUnderArm(bones[i], asset.transform);
            mask.SetTransformActive(i, arm);
            if (arm) active++;
        }

        AssetDatabase.CreateAsset(mask, ArmMaskPath);
        Debug.Log($"[Viewmodel] Arm mask: {active} of {bones.Length} bones driven by the weapon layer.");
        return mask;
    }

    /// <summary>True for the clavicles, everything below them, and the IK hands.</summary>
    private static bool IsUnderArm(Transform bone, Transform root)
    {
        for (var t = bone; t != null && t != root; t = t.parent)
        {
            var n = t.name;
            if (n.StartsWith("clavicle") || n.StartsWith("ik_hand")) return true;
        }
        return false;
    }

    private static void AddOneShot(AnimatorStateMachine sm, AnimatorState back, string name,
                                   AnimationClip clip, string trigger,
                                   float inDuration, float exitTime)
    {
        if (clip == null)
        {
            Debug.LogWarning($"[Viewmodel] Clip for '{name}' missing — state skipped.");
            return;
        }

        var state = sm.AddState(name);
        state.motion = clip;

        var enter = sm.AddAnyStateTransition(state);
        enter.AddCondition(AnimatorConditionMode.If, 0f, trigger);
        enter.hasExitTime = false;
        enter.duration = inDuration;
        enter.canTransitionToSelf = false;

        var leave = state.AddTransition(back);
        leave.hasExitTime = true;
        leave.exitTime = exitTime;
        leave.duration = 0.15f;
    }

    private static AnimationClip LoadClip(string folder, string file)
    {
        var path = $"{folder}/{file}.fbx";
        var clip = AssetDatabase.LoadAllAssetsAtPath(path)
                                .OfType<AnimationClip>()
                                .FirstOrDefault(x => !x.name.StartsWith("__preview__"));
        if (clip == null)
        {
            Debug.LogWarning($"[Viewmodel] No clip inside {path}");
            return null;
        }

        // The pack's clips call back into Infima's own systems at the moment a
        // casing flies or a magazine drops. Nothing on our rig answers to those
        // names, and Unity logs an error for every unanswered call — once per
        // shot, which is hundreds a minute. Listing them here is what makes it
        // possible to write the receiver.
        var events = AnimationUtility.GetAnimationEvents(clip);
        if (events.Length > 0)
        {
            Debug.Log($"[Viewmodel] {file} calls: " +
                      string.Join(", ", events.Select(e => e.functionName).Distinct()));
        }

        return clip;
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
    /// Pulls materials out of the FBX files that own them. A material embedded
    /// in a model is read-only: the shader can be set on it in memory and the
    /// change is reported as done, but nothing is written and the object keeps
    /// rendering magenta. Extracting them first makes the conversion real.
    /// </summary>
    private static void ExtractEmbeddedMaterials()
    {
        var guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets" });
        int changed = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);

            // The map is huge and already textured; leave it alone.
            if (path.StartsWith("Assets/map/")) continue;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;
            if (importer.materialLocation == ModelImporterMaterialLocation.External) continue;

            importer.materialLocation = ModelImporterMaterialLocation.External;
            importer.SaveAndReimport();
            changed++;
        }

        Debug.Log($"[Viewmodel] Materials extracted from {changed} models.");
    }

    /// <summary>
    /// Moves Built-in pipeline materials onto URP shaders. Anything left on a
    /// Built-in shader renders as flat magenta in this project.
    /// </summary>
    private static void ConvertPackMaterials()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[Viewmodel] URP Lit shader not found.");
            return;
        }

        // Whole project, not just the pack: the imported character and weapon
        // FBXs bring their own Built-in materials too, and any of them left
        // behind shows up as a magenta object in URP.
        ExtractEmbeddedMaterials();

        var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        int converted = 0, untouched = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            var shaderName = mat.shader != null ? mat.shader.name : string.Empty;
            bool broken = mat.shader == null || shaderName.Contains("InternalErrorShader");
            // "Autodesk Interactive" is what Unity assigns to FBX materials on
            // import; it is a Built-in shader and renders magenta under URP.
            bool builtin = shaderName == "Standard" ||
                           shaderName == "Standard (Specular setup)" ||
                           shaderName == "Autodesk Interactive" ||
                           shaderName.StartsWith("Legacy Shaders/") ||
                           shaderName.StartsWith("Mobile/") ||
                           shaderName.StartsWith("Unlit/");

            if (!broken && !builtin)
            {
                untouched++;
                continue;
            }

            // Grab the albedo before the shader swap drops unknown properties.
            Texture albedo = null;
            if (mat.HasProperty("_MainTex")) albedo = mat.GetTexture("_MainTex");
            if (albedo == null && mat.HasProperty("_BaseMap")) albedo = mat.GetTexture("_BaseMap");

            Color colour = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;

            mat.shader = lit;
            if (albedo != null)
            {
                mat.SetTexture("_BaseMap", albedo);
                mat.SetTexture("_MainTex", albedo);
            }
            mat.SetColor("_BaseColor", colour);

            EditorUtility.SetDirty(mat);
            converted++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Viewmodel] Materials: {converted} moved to URP, {untouched} already fine.");
    }

    private static Transform FindChild(GameObject root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true)
                   .FirstOrDefault(t => t.name == name);
    }
}
