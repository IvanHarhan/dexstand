using System.IO;
using System.Linq;
using System.Text;
using InfimaGames.LowPolyShooterPack;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Works on the one thing a screenshot keeps showing wrong: the hands are beside
/// the weapon instead of on it, fingers spread. Renders the first person view
/// from the editor — no play session — so a grip can be judged and corrected in
/// seconds rather than a minute a guess.
///
/// Driven by its own trigger file so it never races DexstandAutoRun, which the
/// other session edits.
/// </summary>
[InitializeOnLoad]
public static class DexstandGrip
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static readonly string TriggerPath = Path.Combine(Root, "GripTrigger.txt");
    private static readonly string ResultPath = Path.Combine(Root, "GripResult.txt");

    private const string OutDir =
        @"C:\screenshots\dexstand\grip";

    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";

    private const int Width = 1280;
    private const int Height = 720;

    private const double PollSeconds = 1.0;
    private static double nextPoll;

    static DexstandGrip()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + PollSeconds;

        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!File.Exists(TriggerPath)) return;

        string command;
        try
        {
            command = File.ReadAllText(TriggerPath).Trim().ToLowerInvariant();
            File.Delete(TriggerPath);
        }
        catch (IOException)
        {
            return;
        }

        var log = new StringBuilder();
        try
        {
            switch (command)
            {
                case "probe":
                    Probe(log);
                    break;
                case "shot":
                    Shot(log);
                    break;
                case "npc":
                    NpcShot(log);
                    break;
                case "naked":
                    Strip(log, null);
                    break;
                case "view":
                    FrameRifle(log);
                    break;
                default:
                    // "anim idle", "anim fire" — one clip at a time, which a
                    // fixed switch cannot express.
                    // The trigger text arrives lower-cased; the roles are not.
                    if (command.StartsWith("anim "))
                    {
                        string asked = command.Substring(5).Trim();
                        string role = new[] { "Idle", "Walk", "Run", "Reload", "Fire", "Aim", "WalkBack" }
                            .FirstOrDefault(r => string.Equals(r, asked, System.StringComparison.OrdinalIgnoreCase));
                        Strip(log, role ?? asked);
                    }
                    else
                        log.AppendLine($"UNKNOWN: {command}");
                    break;
            }
        }
        catch (System.Exception e)
        {
            log.AppendLine($"FAILED {command}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }

        File.WriteAllText(ResultPath, log.ToString());
    }

    /// <summary>
    /// Answers the one question the pictures cannot: whether the fingers are
    /// posed at all. A humanoid clip only carries fingers if both it and the
    /// avatar map them, and an unmapped hand is left in the bind pose — which is
    /// the flat, spread palm under the rifle.
    /// </summary>
    private static void Probe(StringBuilder log)
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(DexstandCharacterImport.ModelPath)
                                  .OfType<Avatar>().FirstOrDefault();
        if (avatar == null) log.AppendLine("BODY AVATAR: none");
        else
        {
            var human = avatar.humanDescription.human;
            var fingers = human.Where(h => h.humanName.Contains("Index") ||
                                           h.humanName.Contains("Thumb") ||
                                           h.humanName.Contains("Middle") ||
                                           h.humanName.Contains("Ring") ||
                                           h.humanName.Contains("Little")).ToArray();
            log.AppendLine($"BODY AVATAR: isHuman={avatar.isHuman}, bones={human.Length}, " +
                           $"finger bones mapped={fingers.Length}");
            if (fingers.Length > 0)
                log.AppendLine("  e.g. " + string.Join(", ",
                    fingers.Take(4).Select(f => $"{f.humanName}<-{f.boneName}")));
        }

        foreach (var name in new[] { "RifleIdle", "RifleWalk", "Reloading" })
        {
            string path = $"Assets/Models/Character/MixamoClips/{name}.fbx";
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null) { log.AppendLine($"CLIP {name}: missing"); continue; }

            var bindings = AnimationUtility.GetCurveBindings(clip);
            int fingerCurves = bindings.Count(b =>
                b.propertyName.Contains("Index") || b.propertyName.Contains("Thumb") ||
                b.propertyName.Contains("Middle") || b.propertyName.Contains("Ring") ||
                b.propertyName.Contains("Little"));
            log.AppendLine($"CLIP {name}: humanMotion={clip.humanMotion}, curves={bindings.Length}, " +
                           $"finger curves={fingerCurves}, length={clip.length:F2}s");
        }

        PoseReport = log;
        var player = Spawn(out var camera, out var model, out var weapon, log);
        PoseReport = null;
        if (player == null) return;

        try
        {
            var hands = new[] { "mixamorig:RightHand", "mixamorig:LeftHand" };
            foreach (var h in hands)
            {
                var bone = Find(model, h);
                if (bone == null) { log.AppendLine($"BONE {h}: missing"); continue; }

                var local = camera.InverseTransformPoint(bone.position);
                log.AppendLine($"{h}: camera-space right {local.x:F3} up {local.y:F3} " +
                               $"fwd {local.z:F3}");

                for (int f = 1; f <= 3; f++)
                {
                    var finger = Find(model, h + (h.Contains("Right") ? "" : "") + "Index" + f);
                    if (finger == null) finger = Find(model, h + "Index" + f);
                    if (finger == null) { log.AppendLine($"  Index{f}: missing"); continue; }
                    log.AppendLine($"  Index{f} local euler {finger.localEulerAngles}");
                }
            }

            if (weapon != null)
            {
                var b = WorldBounds(weapon);
                var c = camera.InverseTransformPoint(b.center);
                log.AppendLine($"WEAPON {weapon.name}: size {b.size}, centre camera-space " +
                               $"right {c.x:F3} up {c.y:F3} fwd {c.z:F3}");

                var rightHand = Find(model, "mixamorig:RightHand");
                foreach (var t in weapon.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SOCKET") && t.name != "LeftGrip") continue;
                    var p = camera.InverseTransformPoint(t.position);
                    string fromFist = rightHand != null
                        ? $", {Vector3.Distance(t.position, rightHand.position):F3} from the fist"
                        : "";
                    log.AppendLine($"  {t.name}: right {p.x:F3} up {p.y:F3} fwd {p.z:F3}{fromFist}");
                }

                // Whether the off hand can physically get there: a two bone solver
                // cannot stretch past the sum of its two bones, and everything
                // beyond that reads as a hand left hanging in the air.
                var shoulder = Find(model, "mixamorig:LeftArm");
                var elbow = Find(model, "mixamorig:LeftForeArm");
                var wrist = Find(model, "mixamorig:LeftHand");
                var leftGrip = Find(weapon, "LeftGrip");
                if (shoulder != null && elbow != null && wrist != null && leftGrip != null)
                {
                    float reach = Vector3.Distance(shoulder.position, elbow.position) +
                                  Vector3.Distance(elbow.position, wrist.position);
                    float need = Vector3.Distance(shoulder.position, leftGrip.position);
                    log.AppendLine($"OFF ARM: reach {reach:F3}, needs {need:F3} " +
                                   $"({(need > reach ? "SHORT by " + (need - reach).ToString("F3") : "reaches")})");
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }

    /// <summary>
    /// Renders what the player sees, and the same pose from outside.
    ///
    /// The first person frame says whether the picture reads; it cannot say why
    /// it does not. An arm reaching past the weapon and an arm buried in the
    /// chest look the same down the barrel and completely different from the
    /// side, so both are taken every time.
    /// </summary>
    private static void Shot(StringBuilder log)
    {
        var player = Spawn(out var camera, out var model, out _, log);
        if (player == null) return;

        try
        {
            Directory.CreateDirectory(OutDir);

            // PlayerSetup shrinks the head away for whoever is behind the lens;
            // without it the inside of the gas mask fills half the frame. Put
            // back before the outside views, which are of a whole character.
            var head = model.GetComponentsInChildren<Transform>(true)
                            .FirstOrDefault(t => t.name.EndsWith("Head", System.StringComparison.OrdinalIgnoreCase));
            Vector3 headScale = head != null ? head.localScale : Vector3.one;
            if (head != null) head.localScale = Vector3.one * 0.001f;

            var path = Path.Combine(OutDir, "grip.png");
            Capture(camera.GetComponent<Camera>(), path);
            log.AppendLine($"SHOT -> {path}");

            ShootEachWeapon(player, model, camera.GetComponent<Camera>(), log);

            if (head != null) head.localScale = headScale;

            var rig = new GameObject("GripSideCam");
            var side = rig.AddComponent<Camera>();
            side.nearClipPlane = 0.005f;
            side.fieldOfView = 45f;

            var chest = Find(model, "mixamorig:Spine2");
            Vector3 focus = chest != null ? chest.position : WorldBounds(model).center;

            foreach (var angle in new[] { 90f, 35f })
            {
                var dir = Quaternion.Euler(-15f, angle, 0f) * Vector3.forward;
                rig.transform.position = focus + dir * (DexstandPlayerBuilder.PlayerHeight * 0.9f);
                rig.transform.rotation = Quaternion.LookRotation(focus - rig.transform.position);

                var sidePath = Path.Combine(OutDir, $"grip_side{(int)angle}.png");
                Capture(side, sidePath);
                log.AppendLine($"SHOT -> {sidePath}");
            }

            Object.DestroyImmediate(rig);
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }

    /// <summary>
    /// Puts a player in the scene posed the way the game will show it: the rifle
    /// idle sampled onto the body, then the same two solver passes BodyRig runs
    /// every frame. Anything measured off an unsolved prefab belongs to a pose
    /// nobody sees.
    /// </summary>
    private static GameObject Spawn(out Transform camera, out GameObject model,
                                    out GameObject weapon, StringBuilder log)
    {
        camera = null;
        model = null;
        weapon = null;

        if (EditorSceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { log.AppendLine("No player prefab."); return null; }

        var player = Object.Instantiate(prefab);
        player.name = "GripProbe";

        // Above the yard rather than in it: the map's crates stand right where
        // the origin is, and a photograph of a cardboard box says nothing about
        // how a rifle is held.
        player.transform.position = new Vector3(0f, 3f, 0f);
        player.transform.rotation = Quaternion.identity;

        var cam = player.GetComponentInChildren<Camera>(true);
        if (cam == null) { Object.DestroyImmediate(player); log.AppendLine("No camera."); return null; }
        cam.gameObject.SetActive(true);
        camera = cam.transform;

        var body = Find(player, "Body");
        var arms = Find(player, "Arms");
        if (body != null) body.gameObject.SetActive(true);
        if (arms != null) arms.gameObject.SetActive(false);

        var modelT = body != null ? body.Find("Model") : null;
        if (modelT == null) { Object.DestroyImmediate(player); log.AppendLine("No Body/Model."); return null; }
        model = modelT.gameObject;

        var pose = DexstandBodyAnimations.Downloaded("Idle");
        if (pose != null) pose.SampleAnimation(model, 0f);
        else log.AppendLine("No idle clip to sample — showing the bind pose.");

        var rig = model.GetComponent<BodyRig>();
        var kinematics = model.GetComponent<CharacterKinematics>();
        weapon = ActiveWeapon(player);

        Stage("clip pose", camera, model);

        // The same stance BodyRig strikes every frame. Left out, the picture is
        // of a body square to the camera and the hands land somewhere the game
        // never puts them.
        if (rig != null)
        {
            BodyRig.Bend(rig.spine, rig.spineShare, rig.torsoLean, player.transform.right);
            BodyRig.Bend(rig.spine, rig.spineShare, rig.torsoTwist, player.transform.up);
            Stage("after torso twist", camera, model);
        }

        if (kinematics != null)
        {
            kinematics.Compute(0f, 1f);
            Stage("after gun-hand IK", camera, model);

            if (rig != null && rig.offHandTarget != null && weapon != null)
            {
                var grip = Find(weapon, rig.gripName);
                if (grip != null)
                {
                    rig.offHandTarget.SetPositionAndRotation(grip.position, grip.rotation);
                    kinematics.Compute(1f, 0f);
                    rig.CloseOffHand();
                    Stage("after off-hand IK", camera, model);
                }
                else log.AppendLine($"No '{rig.gripName}' on {weapon.name} — off hand unsolved.");
            }
        }
        else log.AppendLine("No CharacterKinematics on the body.");

        return player;
    }

    private const string TargetPrefabPath = "Assets/Prefabs/TargetCharacter.prefab";
    private const string SoloControllerPath = "Assets/Animations/Solo.controller";

    /// <summary>
    /// Takes everything off the player's body but one thing at a time.
    ///
    /// Asked for after a run of "it is still crooked": with a dozen systems
    /// writing to the same bones — a blend tree, a masked upper-body layer, two
    /// IK chains, a spine twist, a finger curl and a bob — no screenshot can say
    /// which one is wrong. "naked" leaves the character in its bind pose holding
    /// the weapon and nothing else running; "anim &lt;role&gt;" plays exactly one
    /// clip on top of that, still with no IK.
    /// </summary>
    private static void Strip(StringBuilder log, string role)
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var model = root.GetComponentsInChildren<Transform>(true)
                            .FirstOrDefault(t => t.name == "Model" && t.parent != null &&
                                                 t.parent.name == "Body");
            if (model == null) { log.AppendLine("No Body/Model in the player prefab."); return; }

            var animator = model.GetComponent<Animator>();
            var rig = model.GetComponent<BodyRig>();
            var bob = root.GetComponentInChildren<WeaponBob>(true);

            // The solver, the twist, the finger curl and the bob all edit bones
            // after the animator has written them, so they go off together.
            if (rig != null) rig.enabled = false;
            if (bob != null) bob.enabled = false;

            if (animator != null)
            {
                animator.runtimeAnimatorController = string.IsNullOrEmpty(role)
                    ? null
                    : SoloController(role, log);
            }

            // The rifle is re-seated against the clip's own hands, so it sits
            // where that animation holds it rather than where the old IK did.
            var clip = string.IsNullOrEmpty(role) ? null : DexstandBodyAnimations.Downloaded(role);
            if (clip != null)
            {
                FaceWeapon(model.gameObject, clip, log);
                SeatRifle(model.gameObject, clip, log);

                // Only the support hand is solved, and only onto the point the
                // clip itself put it. No twist, no lean, no curl: the clip owns
                // the stance and the fingers.
                if (rig != null)
                {
                    rig.enabled = true;
                    rig.solveGunHand = false;
                    rig.torsoTwist = 0f;
                    rig.torsoLean = 0f;
                    rig.fingerCurl = 0f;
                    rig.thumbCurl = 0f;
                    EditorUtility.SetDirty(rig);
                }
            }

            log.AppendLine(ResetPose(model.gameObject));

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();

            log.AppendLine(string.IsNullOrEmpty(role)
                ? "STRIPPED: no animator controller, BodyRig and WeaponBob disabled. " +
                  "The body stands in its bind pose with the weapon in its hand."
                : $"SOLO '{role}': that clip only, no IK, no bob, no twist.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// Puts every bone back where the imported model has it.
    ///
    /// The builder poses the character before it measures anything — it samples
    /// the rifle idle, turns the chest and runs the IK — and those bone
    /// rotations are saved into the prefab along with everything else. So a
    /// character with no animator at all still stood there holding a rifle at an
    /// angle nobody chose, and every clip played on top of a body that was
    /// already bent. This copies the rest pose back out of the FBX.
    /// </summary>
    private static string ResetPose(GameObject model)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(DexstandCharacterImport.ModelPath);
        if (source == null) return "No source model to take a rest pose from.";

        var rest = new System.Collections.Generic.Dictionary<string, Transform>();
        foreach (var t in source.GetComponentsInChildren<Transform>(true))
            if (!rest.ContainsKey(t.name)) rest[t.name] = t;

        int moved = 0;
        foreach (var bone in model.GetComponentsInChildren<Transform>(true))
        {
            if (!rest.TryGetValue(bone.name, out var original)) continue;
            if (bone.transform == model.transform) continue;

            bone.localPosition = original.localPosition;
            bone.localRotation = original.localRotation;
            moved++;
        }

        return $"REST POSE: {moved} bone(s) put back where the FBX has them.";
    }

    /// <summary>
    /// Puts the rifle where the clip's hands say it is.
    ///
    /// A Mixamo clip carries no weapon, only two hands shaped round one. So the
    /// clip's first frame is sampled, and the rifle is turned onto the line from
    /// the firing hand to the support hand and slid until its magazine well and
    /// handguard sit between them — the same rule that put rifles correctly in
    /// the patrols' hands. Stored as an offset from the hand bone, so it rides
    /// along with every frame afterwards.
    /// </summary>
    private static void SeatRifle(GameObject model, AnimationClip clip, StringBuilder log)
    {
        var hand = Find(model, "mixamorig:RightHand");
        var offHand = Find(model, "mixamorig:LeftHand");
        var rifle = hand != null
            ? hand.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "AK")
            : null;
        if (hand == null || offHand == null || rifle == null)
        {
            log.AppendLine("No hands or no AK to seat.");
            return;
        }

        var well = Find(rifle.gameObject, "SOCKET_Magazine");
        var muzzle = Find(rifle.gameObject, "SOCKET_Muzzle");
        if (well == null || muzzle == null)
        {
            log.AppendLine("The AK has no magazine/muzzle sockets to seat it by.");
            return;
        }

        clip.SampleAnimation(model, 0f);

        Vector3 bore = rifle.InverseTransformPoint(muzzle.position).normalized;
        Vector3 down = rifle.InverseTransformPoint(well.position);
        down -= bore * Vector3.Dot(down, bore);
        Vector3 up = down.sqrMagnitude > 1e-8f ? -down.normalized : Vector3.up;

        Vector3 along = offHand.position - hand.position;
        if (along.sqrMagnitude < 1e-8f) along = model.transform.forward;

        rifle.rotation = Quaternion.LookRotation(along.normalized, model.transform.up) *
                         Quaternion.Inverse(Quaternion.LookRotation(bore, up));

        Vector3 handguard = Vector3.Lerp(well.position, muzzle.position, 0.35f);
        rifle.position += (hand.position + offHand.position) * 0.5f -
                          (well.position + handguard) * 0.5f;

        // Where the clip's own left wrist is, recorded on the rifle. The solver
        // pulls the support hand to exactly this point every frame, so the hand
        // is on the handguard the way the animation put it — even when the
        // runtime pose drifts from the sampled one, which is what left it
        // hanging in the air beside the weapon.
        var grip = rifle.Find("LeftGrip");
        if (grip == null)
        {
            grip = new GameObject("LeftGrip").transform;
            grip.SetParent(rifle, false);
        }
        grip.SetPositionAndRotation(offHand.position, offHand.rotation);

        log.AppendLine($"AK seated between the hands of '{clip.name}' " +
                       $"({Vector3.Distance(hand.position, offHand.position):F3} m apart).");
    }

    /// <summary>
    /// Turns the whole body so the clip's weapon points where the camera does.
    ///
    /// Mixamo's aiming poses are a shooter's stance — hips and chest turned side
    /// on, the rifle along a line well off the character's own forward. The
    /// camera looks down that forward, so left alone the rifle crossed the view
    /// at an angle. The yaw is measured off the line between the two hands in
    /// the clip's first frame and taken back off the model.
    /// </summary>
    private static void FaceWeapon(GameObject model, AnimationClip clip, StringBuilder log)
    {
        var hand = Find(model, "mixamorig:RightHand");
        var offHand = Find(model, "mixamorig:LeftHand");
        var body = model.transform.parent;
        if (hand == null || offHand == null || body == null) return;

        clip.SampleAnimation(model, 0f);

        Vector3 along = body.InverseTransformDirection(offHand.position - hand.position);
        along.y = 0f;
        if (along.sqrMagnitude < 1e-8f) return;

        float yaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
        model.transform.localRotation = Quaternion.Euler(0f, -yaw, 0f) * model.transform.localRotation;

        log.AppendLine($"Body turned {-yaw:F1}° so the rifle points down the view.");
    }

    /// <summary>
    /// The rifle framed by the camera, the hands brought to it.
    ///
    /// Asked for after the clip-driven hold kept landing the stock off the
    /// shoulder: a Mixamo clip carries no weapon, and no rule for where to put
    /// one inside a pair of hands survived contact with the next clip. So the
    /// order is reversed. The rifle hangs off the rest point in front of the lens
    /// and sits exactly where it is framed; the clip only lends the shape of the
    /// grip — where each wrist sits on the weapon and how it is turned — and IK
    /// carries both arms there. The firing hand is off the bottom of the frame;
    /// the support hand is on the handguard.
    /// </summary>
    private static void FrameRifle(StringBuilder log)
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            float h = DexstandPlayerBuilder.PlayerHeight;

            var model = root.GetComponentsInChildren<Transform>(true)
                            .FirstOrDefault(t => t.name == "Model" && t.parent != null &&
                                                 t.parent.name == "Body");
            var camera = root.GetComponentInChildren<Camera>(true);
            var gunRest = root.GetComponentsInChildren<Transform>(true)
                              .FirstOrDefault(t => t.name == "GunRest");
            if (model == null || camera == null || gunRest == null)
            {
                log.AppendLine("Missing Body/Model, camera or GunRest.");
                return;
            }

            var animator = model.GetComponent<Animator>();
            var rig = model.GetComponent<BodyRig>();
            var kinematics = model.GetComponent<CharacterKinematics>();
            var hand = Find(model.gameObject, "mixamorig:RightHand");
            var offHand = Find(model.gameObject, "mixamorig:LeftHand");
            // Under the hand the first time, under the rest point on every run
            // after that.
            var holdPoint = (hand != null ? hand.Find("WeaponHold") : null) ?? gunRest.Find("WeaponHold");
            var rifle = holdPoint != null ? holdPoint.Find("AK") : null;
            var clip = DexstandBodyAnimations.Downloaded("Idle");
            if (animator == null || rig == null || kinematics == null || hand == null ||
                offHand == null || holdPoint == null || rifle == null || clip == null)
            {
                log.AppendLine("Missing animator, rig, hands, WeaponHold/AK or the idle clip.");
                return;
            }

            var well = Find(rifle.gameObject, "SOCKET_Magazine");
            var muzzle = Find(rifle.gameObject, "SOCKET_Muzzle");
            if (well == null || muzzle == null) { log.AppendLine("AK has no sockets."); return; }

            // The weapon's own axes: along the bore, and up away from the
            // magazine. Taken in its local space, so they hold wherever it moves.
            Vector3 boreLocal = rifle.InverseTransformPoint(muzzle.position).normalized;
            Vector3 downLocal = rifle.InverseTransformPoint(well.position);
            downLocal -= boreLocal * Vector3.Dot(downLocal, boreLocal);
            Vector3 upLocal = downLocal.sqrMagnitude > 1e-8f ? -downLocal.normalized : Vector3.up;

            animator.runtimeAnimatorController = SoloController("Idle", log);
            model.localRotation = Quaternion.identity;
            ResetPose(model.gameObject);

            // 1. The grip's shape: seat the rifle between the clip's hands and
            //    record each wrist on it.
            if (holdPoint.parent != hand) holdPoint.SetParent(hand, true);
            clip.SampleAnimation(model.gameObject, 0f);
            SeatRifle(model.gameObject, clip, log);

            var rightGrip = rifle.Find("RightGrip");
            if (rightGrip == null)
            {
                rightGrip = new GameObject("RightGrip").transform;
                rightGrip.SetParent(rifle, false);
            }
            rightGrip.SetPositionAndRotation(hand.position, hand.rotation);

            // The support wrist keeps the clip's angle but is put on the
            // handguard itself, a little under the bore, rather than wherever the
            // clip's wrist happened to be.
            var leftGrip = rifle.Find("LeftGrip");
            Vector3 under = -rifle.TransformDirection(upLocal).normalized;
            leftGrip.position = Vector3.Lerp(well.position, muzzle.position, 0.45f) + under * (h * 0.022f);

            // 2. The rifle moves to the camera and stays there.
            holdPoint.SetParent(gunRest, true);

            var cam = camera.transform;
            rifle.rotation =
                Quaternion.LookRotation(cam.TransformDirection(DexstandViewmodelBuilder.BarrelDirection.normalized), cam.up) *
                Quaternion.Inverse(Quaternion.LookRotation(boreLocal, upLocal));

            Vector3 wellAt = cam.TransformPoint((DexstandViewmodelBuilder.BodyWeaponRest +
                                                 DexstandViewmodelBuilder.FistToMagazine) * h);
            rifle.position += wellAt - well.position;
            Vector3 framed = rifle.position;

            // 3. Both arms solved onto the rifle.
            var so = new SerializedObject(kinematics);
            so.FindProperty("armRightTarget").objectReferenceValue = rightGrip;
            so.FindProperty("armRightWeightPosition").floatValue = 1f;
            so.FindProperty("armRightWeightRotation").floatValue = 1f;
            so.FindProperty("armLeftWeightPosition").floatValue = 1f;
            so.FindProperty("armLeftWeightRotation").floatValue = 1f;
            so.ApplyModifiedPropertiesWithoutUndo();

            rig.enabled = true;
            rig.solveGunHand = true;
            rig.fingerCurl = 35f;
            rig.thumbCurl = 15f;

            // 4. The least chest turn that lets both arms reach, found by trying
            //    rather than typed in: the arm is 15 cm long at this stature.
            //    If no stance reaches, the rifle is drawn back towards the chest
            //    a step at a time — a weapon the hands cannot get to is worse
            //    than one a little closer to the lens.
            foreach (var pull in new[] { 0f, 0.02f, 0.04f, 0.06f, 0.08f, 0.10f })
            {
                rifle.position = framed - cam.forward * (pull * h);
                if (PickStance(model.gameObject, rig, clip, leftGrip, rightGrip, log))
                {
                    log.AppendLine($"Rifle drawn back {pull * h * 100f:F1} cm from its framed spot.");
                    break;
                }
            }

            ResetPose(model.gameObject);
            EditorUtility.SetDirty(rig);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            log.AppendLine("VIEW: rifle framed by the camera, both hands solved onto it.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static bool PickStance(GameObject model, BodyRig rig, AnimationClip clip,
                                   Transform leftGrip, Transform rightGrip, StringBuilder log)
    {
        var lShoulder = Find(model, "mixamorig:LeftArm");
        var lElbow = Find(model, "mixamorig:LeftForeArm");
        var lWrist = Find(model, "mixamorig:LeftHand");
        var rShoulder = Find(model, "mixamorig:RightArm");

        float leftReach = 0f;
        float bestTwist = 0f, bestLean = 0f, bestSlack = float.MinValue;
        float bestLeftNeed = 0f, bestRightNeed = 0f;

        foreach (var lean in new[] { 0f, 8f, 15f, 22f })
        {
            foreach (var twist in new[] { 0f, 10f, 20f, 30f, 40f, 50f })
            {
                ResetPose(model);
                clip.SampleAnimation(model, 0f);
                BodyRig.Bend(rig.spine, rig.spineShare, lean, model.transform.right);
                BodyRig.Bend(rig.spine, rig.spineShare, twist, model.transform.up);

                leftReach = Vector3.Distance(lShoulder.position, lElbow.position) +
                            Vector3.Distance(lElbow.position, lWrist.position);
                float leftNeed = Vector3.Distance(lShoulder.position, leftGrip.position);
                float rightNeed = Vector3.Distance(rShoulder.position, rightGrip.position);

                // The worse of the two arms decides: a stance that frees the
                // left arm by stretching the right one past its length is no
                // better than the one before it.
                float slack = Mathf.Min(leftReach - leftNeed, leftReach - rightNeed);
                if (slack > 0.004f)
                {
                    rig.torsoTwist = twist;
                    rig.torsoLean = lean;
                    log.AppendLine($"STANCE: twist {twist}°, lean {lean}° — both arms reach " +
                                   $"({slack:F3} m to spare).");
                    return true;
                }

                if (slack > bestSlack)
                {
                    bestSlack = slack; bestTwist = twist; bestLean = lean;
                    bestLeftNeed = leftNeed; bestRightNeed = rightNeed;
                }
            }
        }

        rig.torsoTwist = bestTwist;
        rig.torsoLean = bestLean;
        log.AppendLine($"STANCE: nothing reaches cleanly; best is twist {bestTwist}°, lean " +
                       $"{bestLean}°, short by {-bestSlack:F3} m of a {leftReach:F3} m arm " +
                       $"(left needs {bestLeftNeed:F3}, right needs {bestRightNeed:F3}).");
        return false;
    }

    /// <summary>A throwaway controller with one clip looping in one state.</summary>
    private static RuntimeAnimatorController SoloController(string role, StringBuilder log)
    {
        var clip = DexstandBodyAnimations.Downloaded(role);
        if (clip == null)
        {
            log.AppendLine($"No downloaded clip for '{role}'. Known roles: Idle, Walk, Run, " +
                           "Reload, Fire, Aim, WalkBack.");
            return null;
        }

        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(SoloControllerPath) != null)
            AssetDatabase.DeleteAsset(SoloControllerPath);

        var controller = AnimatorController.CreateAnimatorControllerAtPath(SoloControllerPath);
        var state = controller.layers[0].stateMachine.AddState(role);
        state.motion = clip;
        controller.layers[0].stateMachine.defaultState = state;

        log.AppendLine($"Playing '{clip.name}' ({clip.length:F2}s) as '{role}'.");
        return controller;
    }

    /// <summary>
    /// Photographs a patrolling character from three sides, posed as the game
    /// poses it. They are 20 m away and behind the player's own weapon in every
    /// playtest frame, so nothing about them can be judged from one.
    /// </summary>
    private static void NpcShot(StringBuilder log)
    {
        if (EditorSceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPrefabPath);
        if (prefab == null) { log.AppendLine("No target prefab."); return; }

        var npc = Object.Instantiate(prefab);
        npc.name = "NpcProbe";
        npc.transform.position = new Vector3(0f, 3f, 0f);

        var camGo = new GameObject("NpcCam");
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.005f;
        cam.fieldOfView = 40f;

        try
        {
            Directory.CreateDirectory(OutDir);

            var model = npc.transform.childCount > 0 ? npc.transform.GetChild(0).gameObject : npc;
            // The clip the patrol's controller actually idles in, which is the
            // same rifle idle the player stands in.
            var clip = DexstandBodyAnimations.Downloaded("Idle");
            if (clip != null) clip.SampleAnimation(model, 0f);
            else log.AppendLine("No rifle idle clip; showing the bind pose.");

            int vertices = 0, meshes = 0;
            foreach (var r in npc.GetComponentsInChildren<Renderer>(true))
            {
                meshes++;
                if (r is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    vertices += skinned.sharedMesh.vertexCount;
                else if (r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    vertices += filter.sharedMesh.vertexCount;
            }

            // Worth knowing before six of them are on a phone screen at once.
            log.AppendLine($"NPC COST: {meshes} renderer(s), {vertices} vertices each, " +
                           $"{vertices * 6} for a full yard.");

            var bounds = WorldBounds(npc);
            Vector3 focus = bounds.center;

            foreach (var angle in new[] { 0f, 55f, 140f })
            {
                var dir = Quaternion.Euler(-8f, angle, 0f) * Vector3.forward;
                camGo.transform.position = focus + dir * (bounds.size.y * 1.7f);
                camGo.transform.rotation = Quaternion.LookRotation(focus - camGo.transform.position);

                var path = Path.Combine(OutDir, $"npc_{(int)angle}.png");
                Capture(cam, path);
                log.AppendLine($"SHOT -> {path}");
            }
        }
        finally
        {
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(npc);
        }
    }

    /// <summary>
    /// One frame per weapon in the loadout, each carried where its own slot asks
    /// for it.
    ///
    /// At runtime the switcher moves the hold point and the bob carries the
    /// hands after it; out of play mode nothing runs, so the same shift is
    /// applied here by hand and the arms are re-solved onto it.
    /// </summary>
    private static void ShootEachWeapon(GameObject player, GameObject model, Camera camera,
                                        StringBuilder log)
    {
        var switcher = player.GetComponent<WeaponSwitcher>();
        var rig = model.GetComponent<BodyRig>();
        var kinematics = model.GetComponent<CharacterKinematics>();
        var gunRest = player.GetComponentsInChildren<Transform>(true)
                            .FirstOrDefault(t => t.name == "GunRest");
        if (switcher == null || rig == null || kinematics == null || gunRest == null) return;

        Vector3 rest = gunRest.localPosition;

        for (int i = 0; i < switcher.slots.Length; i++)
        {
            var slot = switcher.slots[i];
            if (slot.model == null) continue;

            for (int j = 0; j < switcher.slots.Length; j++)
                if (switcher.slots[j].model != null)
                    switcher.slots[j].model.SetActive(j == i);

            gunRest.localPosition = rest + slot.holdOffset;
            kinematics.Compute(0f, 1f);

            var grip = slot.model.transform.Find(rig.gripName);
            if (grip != null && rig.offHandTarget != null)
            {
                rig.offHandTarget.SetPositionAndRotation(grip.position, grip.rotation);
                kinematics.Compute(1f, 0f);
                rig.CloseOffHand();
            }

            var path = Path.Combine(OutDir, $"grip_{i}_{slot.displayName}.png");
            Capture(camera, path);
            log.AppendLine($"SHOT -> {path}");
        }

        gunRest.localPosition = rest;
    }

    /// <summary>
    /// Set while a probe is spawning, so the pose can be reported at each step of
    /// the solve. The two IK passes are where a grip taken from the clip is lost,
    /// and one line per stage says which pass lost it.
    /// </summary>
    private static StringBuilder PoseReport;

    private static void Stage(string label, Transform camera, GameObject model)
    {
        if (PoseReport == null || camera == null) return;

        var right = Find(model, "mixamorig:RightHand");
        var left = Find(model, "mixamorig:LeftHand");
        if (right == null || left == null) return;

        Vector3 r = camera.InverseTransformPoint(right.position);
        Vector3 l = camera.InverseTransformPoint(left.position);

        PoseReport.AppendLine(
            $"[{label}] right hand (r {r.x:F3} u {r.y:F3} f {r.z:F3}), " +
            $"left hand (r {l.x:F3} u {l.y:F3} f {l.z:F3}), " +
            $"hands {Vector3.Distance(right.position, left.position):F3} apart");
    }

    /// <summary>
    /// The weapon the switcher has out. Its slots are what the game switches
    /// between; the prefab leaves them all under the hold, only one enabled.
    /// </summary>
    private static GameObject ActiveWeapon(GameObject player)
    {
        var switcher = player.GetComponent<WeaponSwitcher>();
        if (switcher != null && switcher.slots != null)
        {
            var slot = switcher.slots.FirstOrDefault(s => s.model != null && s.model.activeSelf);
            if (slot.model != null) return slot.model;
        }

        var hold = player.GetComponentsInChildren<Transform>(true)
                         .FirstOrDefault(t => t.name == "WeaponHold");
        if (hold == null) return null;

        for (int i = 0; i < hold.childCount; i++)
            if (hold.GetChild(i).gameObject.activeSelf) return hold.GetChild(i).gameObject;

        return hold.childCount > 0 ? hold.GetChild(0).gameObject : null;
    }

    private static Transform Find(GameObject root, string name) =>
        root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

    private static Bounds WorldBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    private static void Capture(Camera cam, string path)
    {
        var rt = new RenderTexture(Width, Height, 24);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;

        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();

        File.WriteAllBytes(path, tex.EncodeToPNG());

        cam.targetTexture = prevTarget;
        RenderTexture.active = prevActive;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }
}
