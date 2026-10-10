using UnityEngine;

/// <summary>
/// Marks a Fusion <see cref="Fusion.Hitbox"/> as belonging to a player, and says
/// which part of them it is.
///
/// A lag compensated hit hands back the hitbox that was struck, not the player.
/// Walking up the transform hierarchy would find the health component anyway,
/// but it would not say whether the shot landed on the head — and that is the
/// difference between 25 and 50 damage. One component answers both questions.
/// </summary>
public class PlayerHitbox : MonoBehaviour
{
    [Tooltip("Head hitboxes multiply incoming damage.")]
    public bool isHead;

    [Tooltip("The health this hitbox reports damage to. Wired by the builder.")]
    public PlayerHealth health;

    private void Reset()
    {
        health = GetComponentInParent<PlayerHealth>();
    }
}
