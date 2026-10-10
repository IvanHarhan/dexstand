using UnityEngine;

public interface IWeapon
{
    string WeaponName { get; }
    float FireRate { get; }
    bool CanFire { get; }
    bool TryFire(Ray aimRay);
    void Tick(float deltaTime);
}
