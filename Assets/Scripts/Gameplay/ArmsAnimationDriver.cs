using UnityEngine;

/// <summary>
/// Drives Arms Animator (Generic rig).
/// Arms are a child of CameraHolder — they rotate with the camera automatically.
/// No bone rotation code needed.
///
/// Animator parameters:
///   Speed (float)        — controls Idle/Walk blend
///   IsShooting (bool)    — triggers shoot state
///
/// States: Idle, Walk, Shoot
/// </summary>
public class ArmsAnimationDriver : MonoBehaviour
{
    public Animator animator;

    [Header("Settings")]
    public float speedDampTime = 0.1f;

    private static readonly int HashSpeed = Animator.StringToHash("Speed");
    private static readonly int HashIsShooting = Animator.StringToHash("IsShooting");

    public void UpdateState(float speed, bool isFiring)
    {
        if (animator == null) return;

        animator.SetFloat(HashSpeed, speed, speedDampTime, Time.deltaTime);
        animator.SetBool(HashIsShooting, isFiring);
    }
}
