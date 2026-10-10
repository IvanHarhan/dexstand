using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The cars of Resources/Cars as they are in life: size, weight, engine, gearbox and drive, for how SimpleCar drives
/// them (CarDrivetrain) and how big they are on the map. All in real units — metres, kg, N·m, kW. The Moskvich goes
/// like a Bugatti Chiron — the user's wish. The Passat has 300 000 km on the clock: tired and slow. Unknown models get
/// an ordinary 1.6 petrol hatchback.
/// </summary>
public static class CarSpecs
{
    public enum Drive { Front, Rear, All }

    public class Spec
    {
        public float length = 4.3f;         // metres
        public float topKmh = 190f;         // limiter or what drag allows, whichever comes first
        public float zeroTo100 = 11f;       // seconds, the brochure figure the test stand checks against
        public float mass = 1250f;          // kg, with a driver
        public Drive drive = Drive.Front;
        public float peakTorque = 155f;     // N·m
        public float torqueFrom = 3800f, torqueTo = 4200f; // rpm of the torque plateau
        public float powerKw = 85f;
        public float powerRpm = 5800f;
        public float idleRpm = 800f;
        public float redline = 6300f;
        public bool diesel;
        public float[] gears = { 3.45f, 1.94f, 1.29f, 0.97f, 0.80f };
        public float reverse = 3.6f;
        public float finalDrive = 4.2f;
        public float grip = 1.0f;           // tyre friction coefficient on dry asphalt
        public float comHeight = 0.52f;     // m, centre of mass above the road
        public float dragArea = 0.68f;      // Cd·A, m²
        public float engineInertia = 0.16f; // kg·m², flywheel included
    }

    private static readonly Dictionary<string, Spec> Table = new Dictionary<string, Spec>
    {
        // TT 3.2 VR6 quattro (8N): 250 PS, 320 N·m, 6-speed
        ["audi-tt"] = new Spec
        {
            length = 4.04f, topKmh = 250f, zeroTo100 = 6.4f, mass = 1520f, drive = Drive.All,
            peakTorque = 320f, torqueFrom = 2800f, torqueTo = 3200f, powerKw = 184f, powerRpm = 6300f,
            idleRpm = 750f, redline = 6800f, gears = new[] { 3.36f, 2.09f, 1.47f, 1.10f, 0.92f, 0.79f },
            reverse = 3.99f, finalDrive = 3.94f, grip = 1.05f, comHeight = 0.48f, dragArea = 0.62f, engineInertia = 0.17f,
        },
        // B8 2.0 TDI on its 300 000th km: 150 PS when new, about 120 left; 6-speed
        ["vw-passat"] = new Spec
        {
            length = 4.77f, topKmh = 200f, zeroTo100 = 11.5f, mass = 1600f, drive = Drive.Front, diesel = true,
            peakTorque = 280f, torqueFrom = 1750f, torqueTo = 2500f, powerKw = 88f, powerRpm = 3600f,
            idleRpm = 800f, redline = 4600f, gears = new[] { 3.77f, 2.12f, 1.27f, 0.92f, 0.76f, 0.62f },
            reverse = 3.6f, finalDrive = 3.45f, grip = 0.95f, comHeight = 0.55f, dragArea = 0.66f, engineInertia = 0.24f,
        },
        // Octavia RS 245: 2.0 TSI, 370 N·m, 6-speed
        ["skoda-octavia"] = new Spec
        {
            length = 4.69f, topKmh = 250f, zeroTo100 = 6.7f, mass = 1470f, drive = Drive.Front,
            peakTorque = 370f, torqueFrom = 1600f, torqueTo = 4300f, powerKw = 180f, powerRpm = 6000f,
            idleRpm = 750f, redline = 6500f, gears = new[] { 3.77f, 2.12f, 1.36f, 1.03f, 0.81f, 0.68f },
            reverse = 3.6f, finalDrive = 3.94f, grip = 1.02f, comHeight = 0.52f, dragArea = 0.64f, engineInertia = 0.15f,
        },
        // E46 330i: 231 PS straight six, 300 N·m, 6-speed, rear-wheel drive
        ["bmw-e46"] = new Spec
        {
            length = 4.47f, topKmh = 250f, zeroTo100 = 6.5f, mass = 1480f, drive = Drive.Rear,
            peakTorque = 300f, torqueFrom = 3500f, torqueTo = 3900f, powerKw = 170f, powerRpm = 5900f,
            idleRpm = 700f, redline = 6500f, gears = new[] { 4.21f, 2.49f, 1.66f, 1.24f, 1.00f, 0.83f },
            reverse = 3.85f, finalDrive = 2.93f, grip = 1.02f, comHeight = 0.50f, dragArea = 0.63f, engineInertia = 0.18f,
        },
        // G 63 AMG: 585 PS, 850 N·m, heavy and tall
        ["mercedes-g"] = new Spec
        {
            length = 4.6f, topKmh = 220f, zeroTo100 = 4.5f, mass = 2560f, drive = Drive.All,
            peakTorque = 850f, torqueFrom = 2500f, torqueTo = 3500f, powerKw = 430f, powerRpm = 6000f,
            idleRpm = 650f, redline = 6500f, gears = new[] { 4.38f, 2.86f, 1.92f, 1.37f, 1.00f, 0.82f },
            reverse = 3.42f, finalDrive = 3.27f, grip = 0.98f, comHeight = 0.78f, dragArea = 1.45f, engineInertia = 0.25f,
        },
        // a Chiron in disguise: 1500 PS, 1600 N·m, 7-speed
        ["moskvich-408"] = new Spec
        {
            length = 4.09f, topKmh = 420f, zeroTo100 = 2.4f, mass = 1995f, drive = Drive.All,
            peakTorque = 1600f, torqueFrom = 2000f, torqueTo = 6000f, powerKw = 1103f, powerRpm = 6700f,
            idleRpm = 800f, redline = 6900f, gears = new[] { 3.30f, 2.20f, 1.65f, 1.30f, 1.05f, 0.86f, 0.71f },
            reverse = 3.3f, finalDrive = 3.2f, grip = 1.25f, comHeight = 0.45f, dragArea = 0.58f, engineInertia = 0.3f,
        },
    };

    private static readonly Spec Ordinary = new Spec();

    public static Spec Get(string model) =>
        model != null && Table.TryGetValue(model, out var s) ? s : Ordinary;

    /// <summary>Full-throttle torque at an rpm: rising to the plateau, flat, then falling off along the power curve.</summary>
    public static float FullTorque(Spec s, float rpm)
    {
        if (rpm <= 0f) return 0f;
        float low = s.diesel ? 0.45f : 0.6f;
        if (rpm < s.torqueFrom)
        {
            float t = Mathf.Clamp01((rpm - s.idleRpm * 0.6f) / (s.torqueFrom - s.idleRpm * 0.6f));
            return s.peakTorque * Mathf.Lerp(low, 1f, t * t * (3f - 2f * t));
        }
        if (rpm <= s.torqueTo) return s.peakTorque;
        float atPower = Mathf.Min(s.peakTorque, s.powerKw * 1000f / (s.powerRpm * Mathf.PI / 30f));
        if (rpm <= s.powerRpm)
            return Mathf.Lerp(s.peakTorque, atPower, (rpm - s.torqueTo) / Mathf.Max(1f, s.powerRpm - s.torqueTo));
        return atPower * Mathf.Lerp(1f, 0.8f, Mathf.Clamp01((rpm - s.powerRpm) / Mathf.Max(1f, s.redline - s.powerRpm)));
    }
}
