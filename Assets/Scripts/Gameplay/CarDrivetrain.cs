using UnityEngine;

/// <summary>
/// Everything between the pedals and the road for SimpleCar, in real units (m, kg, N, N·m, rad/s): an engine with a
/// torque curve, idle governor, starter and stalling; a dry clutch with a bite point; a manual gearbox (or an automatic
/// one for phones and tests); open differentials; brush-style tyres with a friction circle, so wheels spin, lock and
/// slide; ABS, traction and stability control that can be switched off. The car hands it wheel loads and the body's
/// motion and applies the forces it returns. It steps in substeps: a tyre is far stiffer than one physics tick.
/// </summary>
public class CarDrivetrain
{
    public class Wheel
    {
        // set by the car every tick
        public bool front, driven, grounded;
        public float radius;     // m
        public float load;       // N onto the road
        public float steer;      // rad, + to the right
        public Vector2 pos;      // m from the centre of mass: x right, y forward
        // kept between ticks
        public float spin;       // rad/s
        // results of the tick, averaged, in the car's frame (x right, y forward)
        public Vector2 force;
        public float slipRatio, slipAngle;
        public bool sliding;
        internal float vx, vy;
    }

    public struct Controls
    {
        public float gas, brake, handbrake;
        public float clutch;     // pedal: 1 = pressed down (disengaged)
        public bool crank;       // key turned to start
    }

    private const float WheelInertia = 1.1f;          // kg·m², a wheel and tyre
    private const float PeakSlip = 0.1f;               // slip ratio where a tyre grips best
    private const float PeakAngle = 0.14f;             // slip angle (tan) where it grips best, ~8°
    private const int Substeps = 10;

    // A tyre's grip over its combined slip (1 = the peak), Pacejka-shaped: steep from the start — a real tyre's
    // cornering stiffness, about 20 per radian of its load — rounding over the top, then easing off to about 85 %
    // when it slides. A straight line up to the peak was a third as stiff: the car leaned on its tyres like on grease
    // before it turned.
    private const float CurveB = 2.07f, CurveC = 1.4f;
    private static float Curve(float rho) => Mathf.Sin(CurveC * Mathf.Atan(CurveB * rho));

    public CarSpecs.Spec spec = CarSpecs.Get(null);
    public bool automatic;
    public bool abs = true, esc = true;

    public float EngineSpin { get; private set; }      // rad/s
    public float Rpm => EngineSpin * 30f / Mathf.PI;
    public bool Running { get; private set; }
    public bool Cranking { get; private set; }
    public int Gear { get; private set; }               // -1 reverse, 0 neutral, 1.. forward
    public int GearCount => spec.gears.Length;
    public float Throttle { get; private set; }         // what the engine got, governor and traction control included
    public float Engagement { get; private set; }       // clutch, 0 open .. 1 closed
    public bool AbsWorking { get; private set; }
    public bool EscWorking { get; private set; }
    public bool Shifting => shiftTimer > 0f;

    /// <summary>Counted up every time it happens, for the car to notice and say or play.</summary>
    public int Stalls { get; private set; }
    public int Starts { get; private set; }

    private bool clutchLocked;
    private float stallTimer, crankTimer, shiftTimer;
    private float tcs = 1f;

    private float Ratio(int gear) =>
        gear > 0 ? spec.gears[Mathf.Min(gear, spec.gears.Length) - 1] * spec.finalDrive
        : gear < 0 ? -spec.reverse * spec.finalDrive : 0f;

    public void Reset()
    {
        EngineSpin = 0f;
        Running = Cranking = false;
        Gear = 0;
        clutchLocked = false;
        stallTimer = crankTimer = shiftTimer = 0f;
    }

    public void StopEngine() => Running = false;

    /// <summary>
    /// The lever into a gear. A real box will not go in with the clutch up (it grinds), nor into reverse while
    /// rolling forward; neutral always goes. False = it ground.
    /// </summary>
    public bool TryShift(int target, float forwardSpeed)
    {
        target = Mathf.Clamp(target, -1, GearCount);
        if (target == Gear) return true;
        if (target != 0 && Engagement > 0.15f) return false;
        if (target < 0 && forwardSpeed > 0.8f) return false;
        if (target > 0 && Gear >= 0 && forwardSpeed < -0.8f) return false;
        Gear = target;
        clutchLocked = false;
        return true;
    }

    /// <summary>Automatic box: the car picks forward or reverse; this picks the gear and works the clutch.</summary>
    public void SetDirection(int direction)
    {
        if (direction < 0 && Gear >= 0) { Gear = -1; clutchLocked = false; }
        else if (direction > 0 && Gear <= 0) { Gear = 1; clutchLocked = false; }
    }

    private float Friction(float rpm) => spec.peakTorque * (0.05f + 0.11f * rpm / spec.redline) * (spec.diesel ? 1.2f : 1f);

    /// <summary>The engine's own torque at its shaft: combustion at this throttle less friction, plus the starter.</summary>
    private float EngineTorque(float throttle)
    {
        float rpm = Rpm;
        float friction = rpm > 1f ? Friction(rpm) : 0f;
        float torque = -friction;
        if (Running)
        {
            // The idle governor holds it at idle and, as a modern car's anti-stall does, opens up hard when the
            // clutch drags the revs down: pulling away on the clutch alone works if the pedal comes up gently.
            float idleFriction = Friction(spec.idleRpm);
            float hold = idleFriction / (CarSpecs.FullTorque(spec, spec.idleRpm) + idleFriction);
            float governor = Mathf.Clamp(hold + (spec.idleRpm - rpm) * 0.008f, 0f, 1f);
            float th = Mathf.Max(throttle, governor);
            if (rpm > spec.redline) th = 0f;   // the limiter cuts the fuel
            Throttle = th;
            torque = th * (CarSpecs.FullTorque(spec, rpm) + friction) - friction;
        }
        else Throttle = 0f;
        if (Cranking && !Running) torque += spec.peakTorque * 0.45f * Mathf.Clamp01(1f - rpm / 300f);
        return torque;
    }

    /// <summary>
    /// One physics tick. velocity (x right, y forward, m/s) and yawRate (rad/s, + to the right) are the body's now;
    /// they are carried through the substeps as a prediction, the rigidbody integrates the forces for real.
    /// </summary>
    public void Step(float dt, Wheel[] wheels, Controls c, float mass, float yawInertia, Vector2 velocity, float yawRate)
    {
        Cranking = c.crank && !Running;
        if (!Cranking) crankTimer = 0f;
        float forward = velocity.y;

        // ---- the clutch: the pedal, or the automatic's own hand
        float engagement = 1f - Mathf.Clamp01((c.clutch - 0.35f) / 0.3f);
        engagement = engagement * engagement * (3f - 2f * engagement);
        int driven = 0;
        float drivenSpin = 0f;
        foreach (var w in wheels)
            if (w.driven) { driven++; drivenSpin += w.spin; }
        driven = Mathf.Max(driven, 1);
        drivenSpin /= driven;
        if (automatic) engagement = AutoClutch(dt, c.gas, drivenSpin);
        Engagement = engagement;
        float capacity = engagement * spec.peakTorque * 1.7f;

        // ---- traction control: less throttle while a driven wheel spins
        float throttle = c.gas;
        bool spinning = false;
        foreach (var w in wheels)
            if (w.driven && w.grounded && w.slipRatio > 0.18f && Mathf.Abs(forward) < 60f) spinning = true;
        tcs = Mathf.MoveTowards(tcs, esc && spinning ? 0.25f : 1f, dt * (spinning ? 8f : 2f));
        throttle *= tcs;
        // ...and ahead of any spin, no more torque than the driven tyres can put down. Without it the Chiron's
        // 1600 N·m lit up all four wheels at once in the low gears and the car went sideways.
        float gearing = Mathf.Abs(Ratio(Gear)), fullTorque = CarSpecs.FullTorque(spec, Rpm);
        if (esc && gearing > 0f && fullTorque > 1f)
        {
            float traction = 0f;
            foreach (var w in wheels)
                if (w.driven && w.grounded) traction += spec.grip * w.load * w.radius;
            throttle = Mathf.Min(throttle, Mathf.Max(0.05f, traction * 1.05f / gearing / fullTorque));
        }

        // ---- stability control: brake one wheel when the car turns more (or less) than the steering asks
        float frontSteer = 0f, wheelbase = 0f;
        float frontY = float.MinValue, rearY = float.MaxValue;
        foreach (var w in wheels)
        {
            if (w.front) frontSteer = w.steer;
            frontY = Mathf.Max(frontY, w.pos.y);
            rearY = Mathf.Min(rearY, w.pos.y);
        }
        wheelbase = Mathf.Max(0.5f, frontY - rearY);
        float escFront = 0f, escRear = 0f;     // brake torque on the outer front / inner rear
        EscWorking = false;
        if (esc && forward > 5f)
        {
            float wanted = forward * Mathf.Tan(frontSteer) / wheelbase;
            float most = spec.grip * 9.81f / forward;
            wanted = Mathf.Clamp(wanted, -most, most);
            float over = Mathf.Abs(yawRate) - Mathf.Abs(wanted);
            if (over > 0.08f || (Mathf.Abs(yawRate) > 0.15f && yawRate * wanted < 0f))
            {
                escFront = Mathf.Min(2500f, over * 4000f) * Mathf.Sign(yawRate);
                throttle *= 0.3f;
                EscWorking = true;
            }
            else if (over < -0.15f && Mathf.Abs(frontSteer) > 0.03f)
            {
                escRear = Mathf.Min(1500f, -over * 3000f) * Mathf.Sign(wanted);
                throttle *= 0.6f;
                EscWorking = true;
            }
        }
        if (tcs < 0.95f) EscWorking = true;

        // ---- brakes: 66 % on the front axle, the handbrake on the rear wheels only
        float brakeAll = mass * 9.81f * 1.25f * c.brake;
        AbsWorking = false;

        float h = dt / Substeps;
        foreach (var w in wheels) { w.force = Vector2.zero; w.sliding = false; }
        float ratio = Ratio(Gear);
        bool engaged = ratio != 0f && capacity > 1f;
        if (!engaged) clutchLocked = false;
        float inertia = spec.engineInertia;

        for (int s = 0; s < Substeps; s++)
        {
            foreach (var w in wheels)
            {
                float right = velocity.x + yawRate * w.pos.y;
                float ahead = velocity.y - yawRate * w.pos.x;
                float cs = Mathf.Cos(w.steer), sn = Mathf.Sin(w.steer);
                w.vx = right * sn + ahead * cs;
                w.vy = right * cs - ahead * sn;
            }

            float gearboxSpin = 0f;
            foreach (var w in wheels) if (w.driven) gearboxSpin += w.spin;
            gearboxSpin = gearboxSpin / driven * ratio;

            float before = EngineSpin;
            float engineTorque = EngineTorque(throttle);
            float extraInertia = 0f, wheelTorque = 0f;
            if (engaged && clutchLocked)
            {
                // clutch closed: the engine turns with the wheels, its flywheel weighing on them through the gears
                extraInertia = inertia * ratio * ratio / driven;
                wheelTorque = engineTorque * ratio / driven;
            }
            else
            {
                float slip = EngineSpin - gearboxSpin;
                float clutchTorque = engaged ? capacity * Mathf.Clamp(slip / 4f, -1f, 1f) : 0f;
                EngineSpin = Mathf.Max(0f, EngineSpin + h * (engineTorque - clutchTorque) / inertia);
                wheelTorque = clutchTorque * ratio / driven;
            }

            float sumRight = 0f, sumAhead = 0f, turn = 0f;
            foreach (var w in wheels)
            {
                float serviceBrake = brakeAll * (w.front ? 0.33f : 0.17f) * w.radius;
                if (w.front && escFront != 0f && (escFront > 0f ? w.pos.x < 0f : w.pos.x > 0f)) serviceBrake += Mathf.Abs(escFront);
                if (!w.front && escRear != 0f && (escRear > 0f ? w.pos.x > 0f : w.pos.x < 0f)) serviceBrake += Mathf.Abs(escRear);
                float fixedBrake = 0.013f * w.load * w.radius;
                if (!w.front) fixedBrake += c.handbrake * mass * 9.81f * 0.45f * w.radius;
                float I = WheelInertia + (w.driven ? extraInertia : 0f);
                Tyre(w, I, w.driven ? wheelTorque : 0f, serviceBrake, fixedBrake, h, out float fx, out float fy);

                float cs = Mathf.Cos(w.steer), sn = Mathf.Sin(w.steer);
                float fr = fx * sn + fy * cs, fa = fx * cs - fy * sn;
                w.force += new Vector2(fr, fa) / Substeps;
                sumRight += fr;
                sumAhead += fa;
                turn += w.pos.y * fr - w.pos.x * fa;
            }

            float gearboxAfter = 0f;
            foreach (var w in wheels) if (w.driven) gearboxAfter += w.spin;
            gearboxAfter = gearboxAfter / driven * ratio;
            if (engaged && clutchLocked)
            {
                EngineSpin = gearboxAfter;
                float carried = engineTorque - inertia * (EngineSpin - before) / h;
                if (Mathf.Abs(carried) > capacity || EngineSpin < 0f) clutchLocked = false;
                EngineSpin = Mathf.Max(0f, EngineSpin);
            }
            else if (engaged)
            {
                float d0 = before - gearboxSpin, d1 = EngineSpin - gearboxAfter;
                if ((d0 * d1 <= 0f || Mathf.Abs(d1) < 0.5f) && gearboxAfter >= 0f) clutchLocked = true;
            }
            EngineSpin = Mathf.Min(EngineSpin, spec.redline * 1.4f * Mathf.PI / 30f);

            // starting and stalling
            float rpm = Rpm;
            if (Cranking)
            {
                crankTimer += h;
                if (crankTimer > (spec.diesel ? 0.9f : 0.55f) && rpm > 150f)
                {
                    Running = true;
                    Cranking = false;
                    Starts++;
                    EngineSpin = Mathf.Max(EngineSpin, spec.idleRpm * 1.3f * Mathf.PI / 30f);
                }
            }
            // only a real bog-down kills it: revs held far under idle for half a second
            if (Running && rpm < spec.idleRpm * 0.28f)
            {
                stallTimer += h;
                if (stallTimer > 0.5f) { Running = false; Stalls++; stallTimer = 0f; }
            }
            else stallTimer = 0f;

            // the body, as a flat rigid body: tyres, air, and the turning frame's own push
            float speed = velocity.y;
            sumAhead -= Mathf.Sign(speed) * 0.5f * 1.2f * spec.dragArea * speed * speed;
            var v = velocity;
            velocity.x += h * (sumRight / mass - yawRate * v.y);
            velocity.y += h * (sumAhead / mass + yawRate * v.x);
            yawRate += h * turn / yawInertia;
        }
    }

    /// <summary>
    /// One wheel for one substep. The spin is solved implicitly against the tyre's grip (stiff), then the brake takes
    /// it towards zero without passing it — a wheel that reaches zero stays locked.
    /// </summary>
    private void Tyre(Wheel w, float I, float drive, float serviceBrake, float fixedBrake, float h, out float fx, out float fy)
    {
        fx = fy = 0f;
        float spin = w.spin;
        if (!w.grounded || w.load <= 0f)
        {
            spin += h * drive / I;
        }
        else
        {
            float mu = spec.grip, load = w.load, r = w.radius;
            float vref = Mathf.Max(Mathf.Abs(w.vx), 1f);
            float cx = mu * load / PeakSlip;
            float ty = w.vy / vref, ny = ty / PeakAngle;
            // the spin against the road's grip, implicitly: first at the curve's steepest, then at the slope of the
            // line to where that put it
            float stiffness = CurveB * CurveC;
            float sx = 0f, nx = 0f, rho = 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                float k = h * r * cx * stiffness / vref;
                spin = (I * w.spin + h * drive + k * w.vx) / (I + k * r);
                sx = (spin * r - w.vx) / vref;
                nx = sx / PeakSlip;
                rho = Mathf.Sqrt(nx * nx + ny * ny);
                stiffness = rho > 1e-4f ? Curve(rho) / rho : CurveB * CurveC;
            }
            // the friction circle: slip either way shares one grip
            float f = mu * load * Curve(rho);
            if (rho > 1e-4f)
            {
                fx = f * nx / rho;
                fy = -f * ny / rho;
            }
            if (rho > 1f)
            {
                // past the peak the curve falls: the wheel's own spin, from what the road gives back
                spin = w.spin + h * (drive - fx * r) / I;
                w.sliding = true;
            }
            w.slipRatio = sx;
            w.slipAngle = Mathf.Atan(ty);

            // ABS: a wheel past its best slip gets no more brake than its tyre can take, so it rolls back to the
            // peak and stays there; a valve's work is coarser than ideal, hence a little less
            if (abs && serviceBrake > 0f && w.vx > 1.5f && sx < -PeakSlip * 0.8f)
            {
                serviceBrake = Mathf.Min(serviceBrake, 0.93f * mu * load * r);
                AbsWorking = true;
            }
        }
        float stop = h * (serviceBrake + fixedBrake) / I;
        spin = Mathf.Abs(spin) <= stop ? 0f : spin - Mathf.Sign(spin) * stop;
        w.spin = spin;
    }

    /// <summary>
    /// Automatic: changes up and down by revs and pedal, and slips the clutch to pull away and to stop without stalling.
    /// </summary>
    private float AutoClutch(float dt, float gas, float drivenSpin)
    {
        if (shiftTimer > 0f)
        {
            shiftTimer -= dt;
            return 0f;
        }
        float ratio = Ratio(Gear);
        if (ratio == 0f) return 0f;
        float wheelRpm = Mathf.Abs(drivenSpin * ratio) * 30f / Mathf.PI;
        if (Gear > 0)
        {
            float up = Mathf.Lerp(0.45f, 0.92f, gas) * spec.redline;
            float down = Mathf.Lerp(0.25f, 0.45f, gas) * spec.redline;
            if (wheelRpm > up && Gear < GearCount)
            {
                Gear++;
                shiftTimer = 0.25f;
                clutchLocked = false;
                return 0f;
            }
            if (Gear > 1 && wheelRpm < down)
            {
                float after = wheelRpm * Ratio(Gear - 1) / ratio;
                if (after < spec.redline * 0.85f)
                {
                    Gear--;
                    shiftTimer = 0.2f;
                    clutchLocked = false;
                    return 0f;
                }
            }
        }
        float idle = spec.idleRpm;
        if (clutchLocked) return wheelRpm < idle * 0.9f && Rpm < idle * 0.95f ? 0f : 1f;
        float t = Mathf.Clamp01((Rpm - idle * 1.05f) / (idle * 0.9f));
        return t * t * (3f - 2f * t);
    }
}
