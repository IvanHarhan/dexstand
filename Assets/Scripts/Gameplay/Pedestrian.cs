using UnityEngine;

/// <summary>
/// One of MapPedestrians' walkers, as something that can die: shot by any weapon or run over. The body then goes
/// limp — a ragdoll put together on the spot from the humanoid bones — and is cleared away after a while. A shot to
/// the head takes the head off.
///
/// Walkers are each client's own scenery, not networked: every peer decides the deaths of its own people (the
/// shooter's client for a shot, whoever sees a car run into one for that).
/// </summary>
public class Pedestrian : MonoBehaviour
{
    public float health = 60f;
    public float corpseSeconds = 25f;

    public bool Dead { get; private set; }

    private Animator anim;
    private float scale;  // world size of a metre of this person

    private void Awake()
    {
        anim = GetComponentInChildren<Animator>();
        scale = transform.lossyScale.y;
    }

    /// <summary>A bullet or a blade. A head shot kills outright; a body already down just takes the knock.</summary>
    public void Shot(float damage, Vector3 point, Vector3 direction, Rigidbody part)
    {
        if (Dead)
        {
            if (part != null) part.AddForceAtPosition(direction * 2f * scale, point, ForceMode.VelocityChange);
            return;
        }
        bool head = IsHead(point);
        health -= head ? health : damage;
        if (health > 0f) return;
        Die(point, direction * 4f * scale, head);
    }

    /// <summary>A car at speed: thrown along its velocity (world units per second).</summary>
    public void RunOver(Vector3 point, Vector3 velocity)
    {
        if (Dead) return;
        Die(point, velocity * 0.9f + Vector3.up * velocity.magnitude * 0.35f, false);
    }

    private bool IsHead(Vector3 point)
    {
        var head = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
        if (head != null) return Vector3.Distance(point, head.position + Vector3.up * 0.08f * scale) < 0.16f * scale;
        return point.y - transform.position.y > 1.5f * scale;
    }

    private void Die(Vector3 point, Vector3 velocity, bool head)
    {
        Dead = true;
        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;

        Rigidbody nearest = null;
        if (anim != null && anim.isHuman) nearest = BuildRagdoll(anim, scale, point, velocity, head);
        if (anim != null) anim.enabled = false;
        if (nearest == null)
        {
            // not a humanoid: the whole body tips over as one piece
            var cap = gameObject.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 0.9f, 0f);
            cap.height = 1.75f;
            cap.radius = 0.25f;
            nearest = gameObject.AddComponent<Rigidbody>();
            nearest.mass = 70f;
            nearest.linearVelocity = velocity;
            nearest.AddForceAtPosition(velocity.normalized * 5f, point, ForceMode.VelocityChange);
        }
        Destroy(gameObject, corpseSeconds);
    }

    /// <summary>
    /// Rigid bodies on the hips, head and limbs, joined like a body; the spine bones between stay stiff. Returns the
    /// body nearest to where the hit landed, which takes the blow. Also what a police officer falls as (Police).
    /// scale: world size of a metre of this person.
    /// </summary>
    public static Rigidbody BuildRagdoll(Animator anim, float scale, Vector3 point, Vector3 velocity, bool headOff)
    {
        Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
        var hips = B(HumanBodyBones.Hips);
        var chest = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest) ?? B(HumanBodyBones.Spine);
        var head = B(HumanBodyBones.Head);
        if (hips == null || chest == null || head == null) return null;

        var parts = new System.Collections.Generic.List<Rigidbody>();
        Rigidbody Part(Transform bone, Transform toward, float radiusMetres, float mass, Rigidbody parent)
        {
            if (bone == null) return null;
            var rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            // A third-size person's forearm is under 2 cm across in scene units: it slipped through the map's
            // ground mesh and bodies lay half sunk into the road. Thicker, and swept every step.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.solverIterations = 12;
            rb.maxDepenetrationVelocity = 1f;
            // by the bone's size, not its sign: the rig's right side is mirrored, and a negative radius collides
            // with nothing — the right leg alone dropped through the ground
            var ls = bone.lossyScale;
            float boneScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z), 1e-6f);
            float radius = Mathf.Max(radiusMetres * scale, 0.035f) / boneScale;
            if (toward != null)
            {
                // a capsule from this bone to the next one down the limb
                var v = bone.InverseTransformPoint(toward.position);
                var cap = bone.gameObject.AddComponent<CapsuleCollider>();
                int axis = Mathf.Abs(v.x) > Mathf.Abs(v.y) ? (Mathf.Abs(v.x) > Mathf.Abs(v.z) ? 0 : 2) : (Mathf.Abs(v.y) > Mathf.Abs(v.z) ? 1 : 2);
                cap.direction = axis;
                cap.center = v * 0.5f;
                cap.height = v.magnitude + radius;
                cap.radius = radius;
                cap.contactOffset = 0.03f;
            }
            else
            {
                var sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = radius;
                sphere.center = bone.InverseTransformPoint(bone.position + Vector3.up * 0.08f * scale);
            }
            if (parent != null)
            {
                var joint = bone.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                // projection snaps a stretched limb back past any collider: legs went through the road with it
                joint.enableProjection = false;
                joint.enablePreprocessing = false;
                joint.lowTwistLimit = new SoftJointLimit { limit = -40f };
                joint.highTwistLimit = new SoftJointLimit { limit = 40f };
                joint.swing1Limit = new SoftJointLimit { limit = 50f };
                joint.swing2Limit = new SoftJointLimit { limit = 30f };
            }
            parts.Add(rb);
            return rb;
        }

        var pelvis = Part(hips, chest, 0.15f, 25f, null);
        if (!headOff) Part(head, null, 0.11f, 5f, pelvis);
        else head.localScale = Vector3.zero;  // gone: the neck is all that is left
        foreach (var side in new[] { (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                                     (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                                     (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                                     (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot) })
        {
            bool leg = side.Item1 == HumanBodyBones.LeftUpperLeg || side.Item1 == HumanBodyBones.RightUpperLeg;
            var upper = Part(B(side.Item1), B(side.Item2), leg ? 0.08f : 0.05f, leg ? 8f : 3f, pelvis);
            Part(B(side.Item2), B(side.Item3), leg ? 0.06f : 0.04f, leg ? 4f : 2f, upper ?? pelvis);
        }

        // neighbouring capsules overlap at the joints and would shove each other apart
        var colliders = new System.Collections.Generic.List<Collider>();
        foreach (var rb in parts) colliders.Add(rb.GetComponent<Collider>());
        for (int i = 0; i < colliders.Count; i++)
            for (int j = i + 1; j < colliders.Count; j++)
                Physics.IgnoreCollision(colliders[i], colliders[j]);

        Rigidbody nearest = pelvis;
        float best = float.MaxValue;
        foreach (var rb in parts)
        {
            rb.linearVelocity = velocity * 0.6f;
            float d = (rb.worldCenterOfMass - point).sqrMagnitude;
            if (d < best) { best = d; nearest = rb; }
        }
        nearest.AddForceAtPosition(velocity * 0.4f, point, ForceMode.VelocityChange);
        return nearest;
    }
}
