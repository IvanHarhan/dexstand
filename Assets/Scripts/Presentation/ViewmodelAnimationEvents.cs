using UnityEngine;

/// <summary>
/// Answers the animation events baked into the Low Poly Shooter Pack's clips.
///
/// The pack's fire, reload and inspect clips call back into Infima's own weapon
/// systems at the frame a casing flies, a magazine seats or an animation ends.
/// None of those systems are in this game, and Unity logs an error for every
/// call that nothing receives — "AnimationEvent 'OnEjectCasing' has no
/// receiver!", once per shot, which is several hundred a minute with an
/// automatic weapon. That noise buries real errors and costs frames on a phone.
///
/// The methods are deliberately empty. Reloading is timed by the weapon itself
/// rather than by these callbacks, since the server has to agree about when the
/// magazine is full and a client's animation is not evidence.
/// </summary>
public class ViewmodelAnimationEvents : MonoBehaviour
{
    /// <summary>A spent case leaves the ejection port. Somewhere to hang brass.</summary>
    public void OnEjectCasing() { }

    /// <summary>The fresh magazine is seated.</summary>
    public void OnAmmunitionFill() { }

    /// <summary>The reload animation has played out.</summary>
    public void OnAnimationEndedReload() { }

    /// <summary>The inspect animation has played out.</summary>
    public void OnAnimationEndedInspect() { }
}
