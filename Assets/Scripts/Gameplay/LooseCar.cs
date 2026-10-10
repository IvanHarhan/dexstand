using UnityEngine;

/// <summary>
/// A parked or passing car of the map (MapParkedCars, MapTraffic) that a crash sets loose. Until then it is a plain
/// box collider that costs no physics; once rammed it becomes a car-weight rigid body that is shoved, turned and
/// slides to a stop where it ends up. Like the map's people these cars are each client's own, so every peer looses
/// its own copy (SimpleCar.Ram).
/// </summary>
public class LooseCar : MonoBehaviour
{
    public Rigidbody Body { get; private set; }
    private float knockedAt;
    private static PhysicsMaterial skid;
    private static PhysicsMaterial Skid => skid != null ? skid : skid = new PhysicsMaterial("LooseCar")
    {
        dynamicFriction = 0.6f, staticFriction = 0.7f, bounciness = 0.05f,
        frictionCombine = PhysicsMaterialCombine.Minimum
    };

    /// <summary>First blow: from here on physics has it. velocity: what it is sent off with, world units/s.</summary>
    public void Knock(Vector3 velocity, Vector3 point)
    {
        if (Body != null) return;
        knockedAt = transform.position.y;
        Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = 1000f;
        Body.linearDamping = 0.4f;
        Body.angularDamping = 2f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        // real gravity, as the driven car has (SimpleCar.Gravity), and tyres skidding sideways on it
        Body.useGravity = false;
        var box = GetComponent<Collider>();
        if (box != null) box.sharedMaterial = Skid;
        Body.AddForceAtPosition(velocity, point, ForceMode.VelocityChange);
    }

    /// <summary>Parked again (reused from the pool): back to a still collider.</summary>
    public void Settle()
    {
        if (Body == null) return;
        Body.isKinematic = true;  // Destroy waits for the frame's end; it must not move before then
        Destroy(Body);
        var box = GetComponent<Collider>();
        if (box != null) box.sharedMaterial = null;
        Body = null;
    }

    private void FixedUpdate()
    {
        if (Body != null && !Body.isKinematic) Body.AddForce(Vector3.down * SimpleCar.Gravity, ForceMode.Acceleration);
        // shoved off the map, or the ground under it streamed away: gone rather than falling for ever
        if (Body != null && Body.position.y < knockedAt - 50f) gameObject.SetActive(false);
    }
}
