using UnityEngine;

public interface IInputService
{
    Vector2 MoveInput { get; }
    Vector2 LookInput { get; }
    bool FireHeld { get; }

    /// <summary>Held while the sights are up.</summary>
    bool AimHeld { get; }

    bool JumpPressed { get; }

    /// <summary>One shot, like the jump: true for the tick the key was hit.</summary>
    bool ReloadPressed { get; }

    /// <summary>Which radial wheel is being held open (see RadialWheel.Kind); 0 when none.</summary>
    int WheelHeld { get; }

    /// <summary>
    /// How far the pointer has travelled since the wheel opened, in pixels —
    /// the finger's drag on a phone, the mouse's summed movement on a PC.
    /// </summary>
    Vector2 WheelDrag { get; }

    void ResetOneShots();
}
