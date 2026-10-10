using UnityEngine;

/// <summary>
/// Merges several IInputService sources into one, so a single build reads both
/// keyboard/mouse and the on-screen controls without knowing the platform.
/// Whichever source is actually being touched wins.
/// </summary>
public class InputRouter : MonoBehaviour, IInputService
{
    [Tooltip("Components implementing IInputService — desktop and mobile.")]
    [SerializeField] private MonoBehaviour[] sources;

    private IInputService[] services;

    private void Awake()
    {
        Rebuild();
    }

    private void Rebuild()
    {
        if (sources == null)
        {
            services = new IInputService[0];
            return;
        }

        var list = new System.Collections.Generic.List<IInputService>();
        foreach (var s in sources)
        {
            if (s is IInputService svc && !ReferenceEquals(svc, this))
                list.Add(svc);
        }
        services = list.ToArray();
    }

    /// <summary>The car's pedals while this device drives: they stand in for the stick.</summary>
    public Vector2 Drive { get; set; }

    public Vector2 MoveInput
    {
        get
        {
            if (Drive.sqrMagnitude > 0.0001f) return Drive;
            if (services == null) return Vector2.zero;
            foreach (var s in services)
            {
                var v = s.MoveInput;
                if (v.sqrMagnitude > 0.0001f) return v;
            }
            return Vector2.zero;
        }
    }

    public Vector2 LookInput
    {
        get
        {
            var sum = Vector2.zero;
            if (services == null) return sum;
            foreach (var s in services) sum += s.LookInput;
            return sum;
        }
    }

    public bool FireHeld
    {
        get
        {
            if (services == null) return false;
            foreach (var s in services)
            {
                if (s.FireHeld) return true;
            }
            return false;
        }
    }

    public bool AimHeld
    {
        get
        {
            if (services == null) return false;
            foreach (var s in services)
            {
                if (s.AimHeld) return true;
            }
            return false;
        }
    }

    public bool JumpPressed
    {
        get
        {
            if (services == null) return false;
            foreach (var s in services)
            {
                if (s.JumpPressed) return true;
            }
            return false;
        }
    }

    public bool ReloadPressed
    {
        get
        {
            if (services == null) return false;
            foreach (var s in services)
            {
                if (s.ReloadPressed) return true;
            }
            return false;
        }
    }

    public int WheelHeld
    {
        get
        {
            if (services == null) return 0;
            foreach (var s in services)
            {
                if (s.WheelHeld != 0) return s.WheelHeld;
            }
            return 0;
        }
    }

    /// <summary>The drag of whichever source is holding a wheel open.</summary>
    public Vector2 WheelDrag
    {
        get
        {
            if (services == null) return Vector2.zero;
            foreach (var s in services)
            {
                if (s.WheelHeld != 0) return s.WheelDrag;
            }
            return Vector2.zero;
        }
    }

    /// <summary>
    /// A weapon picked on screen, as its slot plus one; zero when none was.
    ///
    /// A menu is no input device, so the router keeps these picks itself. Each
    /// is held until the network has read it, like a jump — see
    /// <see cref="MobileInputService.JumpPressed"/>.
    /// </summary>
    public int WeaponPick
    {
        get
        {
            if (weaponPick != 0) weaponPickRead = true;
            return weaponPick;
        }
    }

    /// <summary>An emote picked on screen, as its index plus one; zero when none was.</summary>
    public int EmotePick
    {
        get
        {
            if (emotePick != 0) emotePickRead = true;
            return emotePick;
        }
    }

    /// <summary>A consumable picked on screen; <see cref="Consumable.None"/> when none was.</summary>
    public Consumable ItemPick
    {
        get
        {
            if (itemPick != Consumable.None) itemPickRead = true;
            return itemPick;
        }
    }

    /// <summary>The car button was pressed: get in, or out.</summary>
    public bool CarPick
    {
        get
        {
            if (carPick) carPickRead = true;
            return carPick;
        }
    }

    private int weaponPick, emotePick;
    private Consumable itemPick;
    private bool carPick;
    private bool weaponPickRead, emotePickRead, itemPickRead, carPickRead;

    public void PickCar() => carPick = true;

    public void PickWeapon(int slot) => weaponPick = slot + 1;
    public void PickEmote(int index) => emotePick = index + 1;
    public void PickItem(Consumable item) => itemPick = item;

    public void ResetOneShots()
    {
        if (weaponPickRead) { weaponPick = 0; weaponPickRead = false; }
        if (emotePickRead) { emotePick = 0; emotePickRead = false; }
        if (itemPickRead) { itemPick = Consumable.None; itemPickRead = false; }
        if (carPickRead) { carPick = false; carPickRead = false; }

        if (services == null) return;
        foreach (var s in services) s.ResetOneShots();
    }
}
