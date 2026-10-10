using System.Collections;
using UnityEngine;

/// <summary>
/// Drives a short unattended play session and photographs it: starts a single
/// player match, fires a burst and saves frames, then leaves play mode. Lets
/// the running game be inspected without anyone watching it happen.
///
/// Dormant unless the editor asks for a run, so it never affects normal play.
/// </summary>
public class PlaytestCapture : MonoBehaviour
{
    public const string RequestKey = "Dexstand.PlaytestRequested";

    /// <summary>Asks for the walkabout instead of the fixed photo sequence.</summary>
    public const string WalkKey = "Dexstand.WalkaboutRequested";

    /// <summary>Asks for the car driven with the phone controls.</summary>
    public const string CarKey = "Dexstand.CarTouchRequested";

    /// <summary>
    /// Phone controls, into the car, the gas pedal held: says whether the car
    /// moved and photographs what the screen shows.
    /// </summary>
    private IEnumerator CarOnTouch()
    {
        yield return new WaitForSeconds(startDelay);
        var menu = FindFirstObjectByType<GameMenu>();
        if (menu != null) menu.StartSinglePlayer();
        yield return new WaitForSeconds(settleAfterSpawn + 1f);

        var visibility = FindFirstObjectByType<MobileUIVisibility>();
        var mobile = FindFirstObjectByType<MobileInputService>(FindObjectsInactive.Include);
        // Toggle remembers the choice: switched for the test, it is switched back at the end (a PC editor had
        // been left in touch mode)
        bool switched = visibility != null && (mobile == null || !mobile.gameObject.activeInHierarchy);
        if (switched) visibility.Toggle();
        try
        {

        var car = FindFirstObjectByType<SimpleCar>();
        var player = FindFirstObjectByType<PlayerController>();
        if (car == null || player == null)
        {
            Debug.Log($"[CarTouch] car {(car != null)}, player {(player != null)} — stopped.");
            yield break;
        }

        player.Teleport(car.transform.position - car.transform.right * 0.8f + Vector3.up * 0.1f);
        yield return new WaitForSeconds(1f);
        car.TryToggle();
        yield return new WaitForSeconds(0.5f);
        yield return Shot(Path("car_touch_seated"));

        var gas = GameObject.Find("CarUI/CarPedals/Gas");
        Debug.Log($"[CarTouch] touch on {(mobile != null && mobile.gameObject.activeInHierarchy)}, " +
                  $"gas {(gas != null ? (gas.activeInHierarchy ? "shown" : "hidden") : "missing")}");

        Vector3 from = car.transform.position;
        var hold = gas != null ? gas.GetComponent<CarHoldButton>() : null;
        if (hold != null) hold.OnPointerDown(null);
        yield return new WaitForSeconds(2f);
        if (hold != null) hold.OnPointerUp(null);

        Debug.Log($"[CarTouch] moved {Vector3.Distance(from, car.transform.position):F2}m with the gas held.");
        yield return Shot(Path("car_touch_driving"));

        // The driver's seat, with the wheel turned.
        car.ToggleView();
        var steer = GameObject.Find("CarUI/CarPedals/Left")?.GetComponent<CarHoldButton>();
        if (steer != null) steer.OnPointerDown(null);
        yield return new WaitForSeconds(0.8f);
        yield return Shot(Path("car_inside"));
        if (steer != null) steer.OnPointerUp(null);
        }
        finally
        {
            if (switched) visibility.Toggle();
        }
    }

    [SerializeField] private string outputDirectory =
        @"C:\screenshots\dexstand\playtest";

    [Tooltip("The consumable the playtest takes from the wheel at the end.")]
    [SerializeField] private Consumable testItem = Consumable.Beer;
    [SerializeField] private float startDelay = 1.5f;
    [SerializeField] private float settleAfterSpawn = 1.5f;
    [SerializeField] private int frames = 8;
    [SerializeField] private float frameInterval = 0.12f;

    private IEnumerator Start()
    {
#if UNITY_EDITOR
        if (UnityEditor.EditorPrefs.GetBool(WalkKey, false))
        {
            UnityEditor.EditorPrefs.SetBool(WalkKey, false);
            yield return Walkabout();
            UnityEditor.EditorApplication.isPlaying = false;
            yield break;
        }

        if (UnityEditor.EditorPrefs.GetBool(CarKey, false))
        {
            UnityEditor.EditorPrefs.SetBool(CarKey, false);
            yield return CarOnTouch();
            UnityEditor.EditorApplication.isPlaying = false;
            yield break;
        }

        if (!UnityEditor.EditorPrefs.GetBool(RequestKey, false)) yield break;
        UnityEditor.EditorPrefs.SetBool(RequestKey, false);
#else
        yield break;
#endif

#pragma warning disable CS0162 // unreachable outside the editor, by design
        System.IO.Directory.CreateDirectory(outputDirectory);
        Debug.Log($"[Playtest] Recording to {outputDirectory}");

        yield return new WaitForSeconds(startDelay);

        var menu = FindFirstObjectByType<GameMenu>();
        if (menu != null)
        {
            menu.StartSinglePlayer();
            Debug.Log("[Playtest] Single player started.");
        }

        yield return new WaitForSeconds(settleAfterSpawn);

        // Including inactive: on a PC the touch controls are hidden, and the
        // router still reads this service all the same.
        var input = FindFirstObjectByType<MobileInputService>(FindObjectsInactive.Include);
        if (input != null) input.OnFireDown();

        for (int i = 0; i < frames; i++)
        {
            yield return new WaitForSeconds(frameInterval);
            var path = System.IO.Path.Combine(outputDirectory, $"frame{i:00}.png");
            yield return Shot(path);
            Debug.Log($"[Playtest] frame {i} -> {path}");
        }

        if (input != null) input.OnFireUp();

        // The magazine is now short a few rounds, so this is the moment to see
        // the reload: the count going back up is the part that cannot be judged
        // from a screenshot, so it is logged either side of it.
        // Through the player, like everything else here: a plain search skips
        // components that hang off inactive objects, and half this player does.
        var player = FindFirstObjectByType<WeaponSwitcher>();
        var gun = player != null
            ? player.transform.root.GetComponentInChildren<WeaponController>(true)
            : null;
        if (gun == null) Debug.Log("[Playtest] No WeaponController found — reload not exercised.");

        if (input != null && gun != null)
        {
            Debug.Log($"[Playtest] Ammo before the reload: {gun.Ammo}/{gun.magazineSize}");
            input.OnReload();

            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForSeconds(0.55f);
                var path = System.IO.Path.Combine(outputDirectory, $"reload{i:00}.png");
                yield return Shot(path);
                Debug.Log($"[Playtest] reload {i} ({gun.Ammo} rounds) -> {path}");
            }

            yield return new WaitForSeconds(1.2f);
            Debug.Log($"[Playtest] Ammo after the reload: {gun.Ammo}/{gun.magazineSize}");
        }

        // Then an emote, so the third-person swing-out gets photographed too.
        // Reached through the player: a plain search comes back empty, since
        // the component does not sit on an object that an active-objects-only
        // search can see.
        var owner = FindFirstObjectByType<WeaponSwitcher>();
        var emote = owner != null
            ? owner.transform.root.GetComponentInChildren<EmoteController>(true)
            : null;
        if (emote == null)
        {
            Debug.Log($"[Playtest] No EmoteController under " +
                      $"{(owner != null ? owner.transform.root.name : "<no player>")} — " +
                      $"the emote shots were skipped.");
        }

        if (emote != null)
        {
            emote.PlayNext();
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForSeconds(0.4f);
                var path = System.IO.Path.Combine(outputDirectory, $"emote{i:00}.png");
                yield return Shot(path);
                Debug.Log($"[Playtest] emote {i} -> {path}");
            }
        }

        // Wait out the emote, then photograph the weapon inspect.
        yield return new WaitForSeconds(2.5f);

        // An emote with something solid behind the player. Where the player
        // happens to spawn decides whether the ordinary swing-out has anything
        // to back into at all, so the camera is sent much further than usual on
        // purpose: it has to stop at the first wall instead of ending up inside
        // the next building, and it says so in the log when it does.
        if (emote != null)
        {
            var ordinary = emote.thirdPersonOffset;
            emote.thirdPersonOffset = ordinary * 6f;

            emote.PlayNext();
            for (int i = 0; i < 2; i++)
            {
                yield return new WaitForSeconds(0.6f);
                var path = System.IO.Path.Combine(outputDirectory, $"emotewall{i:00}.png");
                yield return Shot(path);
                Debug.Log($"[Playtest] emote against a wall {i} -> {path}");
            }

            emote.thirdPersonOffset = ordinary;
            yield return new WaitForSeconds(2f);
        }

        var pack = owner != null ? owner.GetComponentInChildren<PackViewmodel>(true) : null;
        if (pack != null)
        {
            pack.Inspect();
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForSeconds(0.32f);
                var path = System.IO.Path.Combine(outputDirectory, $"inspect{i:00}.png");
                yield return Shot(path);
                Debug.Log($"[Playtest] inspect {i} -> {path}");
            }
        }

        // Every weapon in turn. Firing only ever photographs the first slot, so
        // a badly placed pistol or knife would go unnoticed until someone
        // switched to it by hand.
        var switcher = FindFirstObjectByType<WeaponSwitcher>();
        if (switcher != null)
        {
            // The inspect animation is still turning the weapon over; shooting
            // now photographs a gun halfway across the screen and says nothing
            // about where it rests.
            yield return new WaitForSeconds(2.5f);

            for (int i = 0; i < switcher.slots.Length; i++)
            {
                switcher.Select(i);

                yield return new WaitForSeconds(0.5f);

                var path = System.IO.Path.Combine(
                    outputDirectory, $"weapon{i:00}_{switcher.CurrentName}.png");
                yield return Shot(path);
                Debug.Log($"[Playtest] weapon {switcher.CurrentName} -> {path}");
                ReportPlacement(switcher.slots[i].model);
                ReportHands(switcher.slots[i].model);

                // And the same weapon with the sights up, which is the pose the
                // player spends every aimed shot looking at.
                if (input != null)
                {
                    input.OnAimDown();
                    yield return new WaitForSeconds(0.9f);

                    var aimed = System.IO.Path.Combine(
                        outputDirectory, $"aim{i:00}_{switcher.CurrentName}.png");
                    yield return Shot(aimed);
                    Debug.Log($"[Playtest] aiming {switcher.CurrentName} at weight " +
                              $"{(switcher.viewmodel != null ? switcher.viewmodel.AimWeight : -1f):F2}, " +
                              $"offset {(switcher.viewmodel != null ? switcher.viewmodel.aimOffset.ToString("F3") : "none")} " +
                              $"-> {aimed}");

                    input.OnAimUp();
                    yield return new WaitForSeconds(0.3f);
                }
            }

            switcher.Select(0);
        }

        // The wheels, driven the way a thumb drives them: hold, drag towards a
        // slice, let go. The weapons wheel first — the second slice sits down
        // and to the right.
        if (input != null && switcher != null)
        {
            yield return new WaitForSeconds(0.6f);
            yield return Wheel(input, RadialWheel.Kind.Weapons, new Vector2(0.87f, -0.5f), "wheel_weapons");
            yield return new WaitForSeconds(0.6f);
            Debug.Log($"[Playtest] weapons wheel picked {switcher.CurrentName}");

            switcher.Select(0);
            yield return new WaitForSeconds(0.6f);
        }

        // Then a consumable, taken standing still. The wheel's slices run
        // clockwise from the top in Consumable order.
        var effects = switcher != null ? switcher.GetComponent<PlayerEffects>() : null;
        if (input != null && effects != null)
        {
            int charges = effects.Charges;
            float slice = ((int)testItem - 1) * 2f * Mathf.PI / 5f;
            yield return Wheel(input, RadialWheel.Kind.Items, new Vector2(Mathf.Sin(slice), Mathf.Cos(slice)), "wheel_items");

            // Frames through the whole take: the weapon going down, the item up.
            float use = effects.UseSeconds((byte)testItem);
            for (int i = 1; i <= 6; i++)
            {
                yield return new WaitForSeconds(use / 7f);
                yield return Shot(System.IO.Path.Combine(outputDirectory, $"taking_{i}.png"));
                Debug.Log($"[Playtest] taking {(Consumable)effects.Taking}, {effects.TakeProgress:P0} done");
            }

            yield return new WaitForSeconds(use);
            yield return Shot(System.IO.Path.Combine(outputDirectory, "effect.png"));
            Debug.Log($"[Playtest] consumable: charges {charges} -> {effects.Charges}, " +
                      $"{effects.Current} with {effects.Left:F0}s left, " +
                      $"spread x{effects.SpreadMultiplier:F2}, recoil x{effects.RecoilMultiplier:F2}");
        }

        Debug.Log("[Playtest] Done.");

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
#pragma warning restore CS0162
    }

    /// <summary>
    /// Plays the game for a minute and writes down what went wrong.
    ///
    /// Not a photo shoot: it walks, turns, jumps, aims at a patrolling target
    /// and shoots it, changes weapons, reloads and stabs — checking after each
    /// one that the thing it asked for actually happened. Positions, health and
    /// ammunition are read back rather than assumed, because the failures worth
    /// finding are exactly the ones that look fine in a screenshot: a shot that
    /// takes no health, a walk that does not move, a floor that is not there.
    ///
    /// Everything the console logs during the run is collected as well, since an
    /// exception every tick is invisible in a picture and fatal in a game.
    /// </summary>
    private IEnumerator Walkabout()
    {
        System.IO.Directory.CreateDirectory(outputDirectory);
        problems.Clear();
        faults.Clear();
        Application.logMessageReceived += OnLog;

        Debug.Log("[Walk] Starting.");
        yield return new WaitForSeconds(startDelay);

        var menu = FindFirstObjectByType<GameMenu>();
        if (menu != null) menu.StartSinglePlayer();
        yield return new WaitForSeconds(settleAfterSpawn + 1f);

        var input = FindFirstObjectByType<MobileInputService>(FindObjectsInactive.Include);
        var switcher = FindFirstObjectByType<WeaponSwitcher>();
        var root = switcher != null ? switcher.transform.root : null;
        var body = root != null ? root.GetComponentInChildren<PlayerController>(true) : null;
        var gun = root != null ? root.GetComponentInChildren<WeaponController>(true) : null;

        if (input == null || body == null || gun == null)
        {
            Note($"Could not take control: input {(input != null)}, player {(body != null)}, " +
                 $"weapon {(gun != null)}.");
            Application.logMessageReceived -= OnLog;
            Report();
            yield break;
        }

        Vector3 spawn = body.transform.position;
        Debug.Log($"[Walk] Spawned at {spawn}.");

        // --- Walking ------------------------------------------------------
        foreach (var leg in new[]
                 {
                     ("forward", Vector2.up), ("right", Vector2.right),
                     ("back", Vector2.down), ("left", Vector2.left),
                 })
        {
            Vector3 from = body.transform.position;
            input.OnDirection(leg.Item2, true);
            yield return new WaitForSeconds(1.4f);
            input.OnDirection(leg.Item2, false);
            yield return new WaitForSeconds(0.3f);

            Vector3 moved = body.transform.position - from;
            Debug.Log($"[Walk] {leg.Item1}: moved {moved.magnitude:F2}m.");

            if (moved.magnitude < 0.05f) Note($"Walking {leg.Item1} moved the player {moved.magnitude:F2}m — stuck.");
            if (body.transform.position.y < spawn.y - 1f)
                Note($"Fell to y={body.transform.position.y:F2} while walking {leg.Item1}.");
        }

        yield return Shot(Path("walk_after_moving"));

        // --- Jumping ------------------------------------------------------
        float beforeJump = body.transform.position.y;
        input.OnJump();
        yield return new WaitForSeconds(0.25f);
        float peak = body.transform.position.y;
        yield return new WaitForSeconds(1.5f);
        float landed = body.transform.position.y;

        Debug.Log($"[Walk] Jump: {beforeJump:F2} -> peak {peak:F2} -> landed {landed:F2}.");
        if (peak - beforeJump < 0.05f) Note("Jump did not leave the ground.");
        if (Mathf.Abs(landed - beforeJump) > 0.5f)
            Note($"Did not land back at ground level: {beforeJump:F2} -> {landed:F2}.");

        // --- Shooting a target --------------------------------------------
        var mark = NearestTarget(body.transform.position);
        if (mark == null)
        {
            Note("No Target in the scene to shoot at.");
        }
        else
        {
            Debug.Log($"[Walk] Nearest target {mark.name} at " +
                      $"{Vector3.Distance(body.transform.position, mark.transform.position):F1}m, " +
                      $"health {mark.Health:F0}.");

            // Twice, at two ranges. Whether a shot lands is the one thing a
            // screenshot cannot show, and the interesting failure is the one
            // that only happens at arm's length — the ray starting inside what
            // it is aimed at.
            yield return Backpedal(input, body, mark.transform, 3f);
            yield return Fired(input, body, gun, mark, "from a few paces");

            var second = NearestTarget(body.transform.position);
            if (second != null && !second.IsDown)
            {
                yield return Approach(input, body, second.transform, 0.6f);
                yield return Fired(input, body, gun, second, "point blank");
            }
        }

        // --- Taking fire ----------------------------------------------------
        //
        // The patrols shoot back now, so the thing worth checking is that being
        // shot does something and that dying is survivable: health has to fall
        // while standing in the open, and the player has to come back.
        var health = body.GetComponentInParent<PlayerHealth>();
        if (health != null)
        {
            float startedAt = health.Health;
            float watchUntil = Time.time + 9f;
            bool died = false;

            while (Time.time < watchUntil)
            {
                if (health.IsDead) died = true;
                yield return null;
            }

            Debug.Log($"[Walk] Under fire for 9s: health {startedAt:F0} -> {health.Health:F0}" +
                      $"{(died ? ", died at least once" : "")}.");

            if (Mathf.Approximately(startedAt, health.Health) && !died)
                Note("Standing in front of armed patrols for 9s cost no health.");

            if (died)
            {
                yield return new WaitForSeconds(6f);
                if (health.IsDead) Note("Died and never respawned.");
                else Debug.Log($"[Walk] Respawned with {health.Health:F0} health.");
            }
        }

        // --- Reloading ------------------------------------------------------
        int before = gun.Ammo;
        input.OnReload();
        yield return new WaitForSeconds(gun.reloadSeconds + 0.6f);
        Debug.Log($"[Walk] Reload: {before} -> {gun.Ammo}.");
        if (gun.Ammo <= before) Note($"Reload did not refill the magazine ({before} -> {gun.Ammo}).");

        // --- The other weapons ----------------------------------------------
        for (int i = 1; i < switcher.slots.Length; i++)
        {
            switcher.Select(i);
            yield return new WaitForSeconds(0.8f);

            string name = switcher.CurrentName;
            var active = switcher.slots[i].model;
            if (active == null || !active.activeInHierarchy) Note($"{name} did not appear when selected.");

            int ammo = gun.Ammo;
            input.OnFireDown();
            yield return new WaitForSeconds(0.9f);
            input.OnFireUp();
            yield return new WaitForSeconds(0.3f);

            Debug.Log($"[Walk] {name}: ammo {ammo} -> {gun.Ammo}, magazine {gun.magazineSize}.");
            if (gun.magazineSize > 0 && gun.Ammo >= ammo)
                Note($"{name} fired without using ammunition ({ammo} -> {gun.Ammo}).");

            yield return Shot(Path($"walk_{name}"));
        }

        switcher.Select(0);
        yield return new WaitForSeconds(0.6f);

        Application.logMessageReceived -= OnLog;
        Report();
    }

    /// <summary>Holds a wheel open, drags towards a slice, photographs it and lets go.</summary>
    private IEnumerator Wheel(MobileInputService input, RadialWheel.Kind wheel, Vector2 towards, string shot)
    {
        input.OnWheelDown((int)wheel);
        input.OnWheelDrag(towards.normalized * Screen.height * 0.2f);
        yield return new WaitForSeconds(0.4f);

        yield return Shot(System.IO.Path.Combine(outputDirectory, shot + ".png"));
        input.OnWheelUp((int)wheel);
    }

    /// <summary>Aims, empties a burst into it, and says whether it landed.</summary>
    private IEnumerator Fired(MobileInputService input, PlayerController body,
                              WeaponController gun, Target mark, string what)
    {
        yield return Aim(input, body, mark.transform);

        float range = Vector3.Distance(body.transform.position, mark.transform.position);
        float healthBefore = mark.Health;
        int ammoBefore = gun.Ammo;

        yield return Shot(Path($"walk_aim_{what.Replace(' ', '_')}"));

        input.OnFireDown();
        yield return new WaitForSeconds(1.2f);
        input.OnFireUp();
        yield return new WaitForSeconds(0.4f);

        Debug.Log($"[Walk] Burst {what} at {range:F1}m: health {healthBefore:F0} -> " +
                  $"{mark.Health:F0}, ammo {ammoBefore} -> {gun.Ammo}.");
        yield return Shot(Path($"walk_shot_{what.Replace(' ', '_')}"));

        if (gun.Ammo >= ammoBefore)
            Note($"Firing {what} did not use ammunition ({ammoBefore} -> {gun.Ammo}).");

        if (mark.Health >= healthBefore && !mark.IsDown)
            Note($"A burst {what} ({range:F1}m) took no health off {mark.name}.");
    }

    /// <summary>Walks backwards until the target is at least this far away.</summary>
    private IEnumerator Backpedal(MobileInputService input, PlayerController body,
                                  Transform from, float wanted)
    {
        float until = Time.time + 5f;
        while (Vector3.Distance(body.transform.position, from.position) < wanted &&
               Time.time < until)
        {
            input.OnDirection(Vector2.down, true);
            yield return null;
        }

        input.OnDirection(Vector2.down, false);
        yield return new WaitForSeconds(0.3f);
    }

    /// <summary>Walks up to the target until it is within arm's reach.</summary>
    private IEnumerator Approach(MobileInputService input, PlayerController body,
                                 Transform to, float wanted)
    {
        float until = Time.time + 6f;
        while (Vector3.Distance(body.transform.position, to.position) > wanted &&
               Time.time < until)
        {
            yield return Aim(input, body, to);
            input.OnDirection(Vector2.up, true);
            yield return new WaitForSeconds(0.2f);
        }

        input.OnDirection(Vector2.up, false);
        yield return new WaitForSeconds(0.3f);
    }

    private readonly System.Collections.Generic.List<string> problems =
        new System.Collections.Generic.List<string>();

    private readonly System.Collections.Generic.List<string> faults =
        new System.Collections.Generic.List<string>();

    private void Note(string problem)
    {
        problems.Add(problem);
        Debug.Log($"[Walk] PROBLEM: {problem}");
    }

    /// <summary>
    /// Keeps the console's own errors, deduplicated — the same exception thrown
    /// every tick is one fault, not four hundred.
    /// </summary>
    private void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;

        string line = message.Length > 160 ? message.Substring(0, 160) : message;
        foreach (var seen in faults)
        {
            if (seen.EndsWith(line)) return;
        }
        faults.Add($"{type}: {line}");
    }

    private void Report()
    {
        Debug.Log($"[Walk] Finished — {problems.Count} problem(s), {faults.Count} console fault(s).");
        foreach (var p in problems) Debug.Log($"[Walk] * {p}");
        foreach (var f in faults) Debug.Log($"[Walk] ! {f}");
    }

    private string Path(string name) =>
        System.IO.Path.Combine(outputDirectory, name + ".png");

    private Target NearestTarget(Vector3 from)
    {
        Target best = null;
        float bestDistance = float.MaxValue;

        foreach (var candidate in FindObjectsByType<Target>(FindObjectsSortMode.None))
        {
            if (candidate.IsDown) continue;
            float d = (candidate.transform.position - from).sqrMagnitude;
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Turns towards something by feeding look input, the way a player's thumb
    /// would. How many degrees a unit of look is worth belongs to the player
    /// controller, so this closes the loop on the angle that is left instead of
    /// computing the input outright.
    /// </summary>
    private IEnumerator Aim(MobileInputService input, PlayerController body, Transform at)
    {
        var lens = body.transform.root.GetComponentInChildren<Camera>(true);
        if (lens == null)
        {
            Note("No camera on the player to aim with.");
            yield break;
        }

        // The middle of what can be seen of the target, not its transform: the
        // root of these characters is between their boots, and a shot aimed
        // there at close range goes into the dirt.
        Bounds mass = new Bounds(at.position, Vector3.zero);
        foreach (var r in at.GetComponentsInChildren<Renderer>(true)) mass.Encapsulate(r.bounds);

        // Two sensitivities sit between this and the player's yaw — the touch
        // service's and the controller's — and both are small, so a gentle
        // nudge turns a fraction of a degree a tick. The loop is long and the
        // step generous for that reason.
        for (int i = 0; i < 400; i++)
        {
            foreach (var r in at.GetComponentsInChildren<Renderer>(true)) mass.Encapsulate(r.bounds);

            Vector3 to = mass.center - lens.transform.position;
            if (to.sqrMagnitude < 0.0001f) yield break;
            to.Normalize();

            Vector3 flat = new Vector3(to.x, 0f, to.z);
            if (flat.sqrMagnitude < 0.0001f) yield break;

            float yawError = Mathf.DeltaAngle(
                body.transform.eulerAngles.y, Quaternion.LookRotation(flat).eulerAngles.y);

            // Pitch as the controller keeps it: positive is nose down, and
            // pushing look up is what makes it smaller.
            float pitchNow = -Mathf.Asin(Mathf.Clamp(lens.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float pitchWanted = -Mathf.Asin(Mathf.Clamp(to.y, -1f, 1f)) * Mathf.Rad2Deg;
            float pitchError = pitchWanted - pitchNow;

            if (Mathf.Abs(yawError) < 1.5f && Mathf.Abs(pitchError) < 1.5f)
            {
                input.OnLookDelta(Vector2.zero);
                Debug.Log($"[Walk] Aimed to within {yawError:F1}° across, {pitchError:F1}° up " +
                          $"after {i} frames.");
                yield break;
            }

            input.OnLookDelta(new Vector2(Mathf.Clamp(yawError * 2f, -60f, 60f),
                                          Mathf.Clamp(-pitchError * 2f, -60f, 60f)));
            yield return null;
        }

        input.OnLookDelta(Vector2.zero);
        Note("Could not turn to face the target — look input may not be reaching the player.");
    }

    /// <summary>
    /// Takes one photograph, and does not return until it is on disk.
    ///
    /// ScreenCapture.CaptureScreenshot writes its file somewhere later than it
    /// is called — late enough that a run photographing three weapons in turn
    /// produced three images each holding the weapon from the step after it,
    /// every one of them saved under the wrong name. Reading the frame into a
    /// texture at the end of the frame and writing the PNG here leaves no room
    /// for that: what the image holds is what was on screen when it was asked
    /// for.
    /// </summary>
    private IEnumerator Shot(string path)
    {
        yield return new WaitForEndOfFrame();

        var texture = ScreenCapture.CaptureScreenshotAsTexture();
        System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
        Destroy(texture);
    }

    /// <summary>
    /// Says where a weapon actually ends up in front of the lens once the
    /// animator is running, in the camera's own axes — right, up, forward.
    ///
    /// The builder prints the same three numbers for the pose it sampled at
    /// build time. When they disagree it is the runtime pose that is real, and
    /// the difference is exactly what the placement has to be moved by; reading
    /// it off a screenshot instead costs a play session per guess.
    /// </summary>
    private static void ReportPlacement(GameObject weapon)
    {
        if (weapon == null) return;

        // The player's own camera, not Camera.main: the menu leaves a camera of
        // its own in the scene and it is the one tagged MainCamera, which put
        // the weapon a reported seven metres away.
        var camera = weapon.transform.root.GetComponentInChildren<Camera>(true);
        if (camera == null) return;

        var renderers = weapon.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

        Vector3 min = camera.transform.InverseTransformPoint(bounds.min);
        Vector3 max = camera.transform.InverseTransformPoint(bounds.max);
        Vector3 centre = camera.transform.InverseTransformPoint(bounds.center);

        Debug.Log($"[Playtest] {weapon.name} in view: centre ({centre.x:F3}, {centre.y:F3}, " +
                  $"{centre.z:F3}), x {Mathf.Min(min.x, max.x):F3}..{Mathf.Max(min.x, max.x):F3}, " +
                  $"y {Mathf.Min(min.y, max.y):F3}..{Mathf.Max(min.y, max.y):F3}, " +
                  $"z {Mathf.Min(min.z, max.z):F3}..{Mathf.Max(min.z, max.z):F3}");

        // How far the animated hand has turned the weapon away from the pose the
        // builder placed it in. The builder works on a sampled pose in the
        // prefab; if the animator rests somewhere else, every build-time angle
        // is wrong by this much and no amount of nudging the rest point fixes it.
        Quaternion relative = Quaternion.Inverse(camera.transform.rotation) * weapon.transform.rotation;
        Vector3 euler = relative.eulerAngles;
        euler = new Vector3(Mathf.DeltaAngle(0f, euler.x),
                            Mathf.DeltaAngle(0f, euler.y),
                            Mathf.DeltaAngle(0f, euler.z));
        Debug.Log($"[Playtest] {weapon.name} turned relative to the lens: " +
                  $"pitch {euler.x:F1}, yaw {euler.y:F1}, roll {euler.z:F1}");
    }

    /// <summary>
    /// Where the body's own hands end up once the solver has run, against the
    /// edge of the frame they have to stay inside.
    ///
    /// "The hands are not in shot" is the one complaint a screenshot answers
    /// slowly and three numbers answer at once: a point is on screen only while
    /// its height off the view axis is less than its distance times the tangent
    /// of half the field of view, and that is the whole test.
    /// </summary>
    private static void ReportHands(GameObject weapon)
    {
        if (weapon == null) return;

        var root = weapon.transform.root;
        var camera = root.GetComponentInChildren<Camera>(true);
        if (camera == null) return;

        var lens = camera.transform;
        float spread = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

        foreach (var name in new[]
                 { "mixamorig:RightHand", "mixamorig:LeftHand", "GunRest", "LeftGrip" })
        {
            // The grip lives on the weapon, and every weapon carries one — so the
            // held one is searched first, or the report describes a holstered gun.
            var found = FindByName(weapon.transform, name) ?? FindByName(root, name);
            if (found == null)
            {
                Debug.Log($"[Playtest] hands: '{name}' not found.");
                continue;
            }

            Vector3 local = lens.InverseTransformPoint(found.position);
            float edge = Mathf.Max(0.0001f, local.z) * spread;
            bool onScreen = local.z > camera.nearClipPlane && Mathf.Abs(local.y) <= edge;

            Debug.Log($"[Playtest] {name}: right {local.x:F3}, up {local.y:F3}, " +
                      $"fwd {local.z:F3} — frame reaches {edge:F3} up and down at that " +
                      $"distance, so {(onScreen ? "IN SHOT" : "OFF SCREEN")}.");
        }
    }

    private static Transform FindByName(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
}
