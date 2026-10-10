using UnityEngine;

/// <summary>
/// An engine you can hear, made up on the fly (no recordings to license): a four-cylinder firing note and its harmonics
/// plus intake/exhaust noise growing with load. The drivable SimpleCar tells it its real revs, starter and load
/// (<see cref="Drive"/>), and asks for the key click, gear grinding and the indicator's tick (<see cref="Play"/>);
/// traffic and police cars are guessed from their movement through a pretend five-speed box. 3D, paused when far.
///
/// The source plays a clip of ones and the synth multiplies into it, so Unity's distance fade and panning — applied
/// to the clip before this filter — carry over; writing the samples outright made every car in town as loud as the
/// one you sit in.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class CarEngineSound : MonoBehaviour
{
    public enum Cue { Key, Grind, Tick, Tock }

    public float volume = 0.55f;
    public float idleRpm = 850f, maxRpm = 6200f;
    // km/h where each gear tops out; one scene unit is 3 m, so km/h = units/s * 10.8
    private static readonly float[] GearTop = { 30f, 55f, 85f, 120f, 165f, 260f };

    private AudioSource src;
    private Vector3 lastPos;
    private float kmh, load;
    private int guessGear;
    private float drivenUntil;
    private float drivenRpm, drivenLoad;
    private bool drivenRunning, drivenCranking;

    // read on the audio thread
    private volatile float rpmA, ampA, noiseA, crankA;
    private volatile int keyCue, grindCue, tickCue, tockCue;
    private int keyDone, grindDone, tickDone, tockDone;
    private float rpmS, ampS, noiseS, crankS;
    private float keyT = 1f, grindT = 1f, tickT = 1f;
    private bool tock;
    private double phase, crankPhase;
    private float rate = 48000f;
    private uint seed = 22222u;
    private float lp, bp1, bp2;
    private static AudioListener ear;

    private void Awake()
    {
        src = GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = true;
        src.spatialBlend = 1f;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = 1.5f;
        src.maxDistance = 35f;   // ~100 m
        src.dopplerLevel = 0.3f;
        rate = AudioSettings.outputSampleRate;
        seed ^= (uint)GetInstanceID();
        lastPos = transform.position;
        int n = (int)rate;
        var ones = new float[n];
        for (int i = 0; i < n; i++) ones[i] = 1f;
        src.clip = AudioClip.Create("EngineCarrier", n, 1, n, false);
        src.clip.SetData(ones, 0);
        src.Play();
    }

    /// <summary>The real engine, every frame by the car that has one. Silent while it is off.</summary>
    public void Drive(float rpm, float engineLoad, bool running, bool cranking)
    {
        drivenUntil = Time.time + 0.5f;
        drivenRpm = rpm;
        drivenLoad = engineLoad;
        drivenRunning = running;
        drivenCranking = cranking;
    }

    public void Play(Cue cue)
    {
        switch (cue)
        {
            case Cue.Key: keyCue++; break;
            case Cue.Grind: grindCue++; break;
            case Cue.Tick: tickCue++; break;
            case Cue.Tock: tockCue++; break;
        }
    }

    private void Update()
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float rpm, ld;
        bool firing = true;
        float crank = 0f;
        if (Time.time < drivenUntil)
        {
            rpm = drivenRpm;
            ld = drivenLoad;
            firing = drivenRunning;
            crank = drivenCranking ? 1f : 0f;
            lastPos = transform.position;
        }
        else
        {
            float v = (transform.position - lastPos).magnitude / dt * 10.8f;
            lastPos = transform.position;
            if (v > 400f) v = kmh;                                   // a teleport, not a speed
            float accel = (v - kmh) / dt;
            kmh = Mathf.Lerp(kmh, v, 1f - Mathf.Exp(-dt * 4f));
            load = Mathf.Lerp(load, Mathf.Clamp01(0.25f + accel * 0.04f), 1f - Mathf.Exp(-dt * 3f));
            ld = load;

            // up a gear past its top, down only well below the one under it: no hunting between two
            while (guessGear < GearTop.Length - 1 && kmh > GearTop[guessGear] * 0.97f) guessGear++;
            while (guessGear > 0 && kmh < GearTop[guessGear - 1] * 0.8f) guessGear--;
            float lo = guessGear == 0 ? 0f : GearTop[guessGear - 1] * 0.6f;
            float t = Mathf.Clamp01((kmh - lo) / (GearTop[guessGear] - lo));
            rpm = kmh < 3f ? idleRpm : Mathf.Lerp(idleRpm + 600f, maxRpm, t);
        }
        rpmA = rpm;
        float revShare = Mathf.Clamp01(rpm / maxRpm);
        ampA = firing ? volume * Mathf.Lerp(0.45f, 1f, ld) * Mathf.Lerp(0.7f, 1f, revShare)
                      : volume * 0.35f * Mathf.Clamp01(rpm / 900f);   // turning over without firing: a hollow whoosh
        noiseA = firing ? 0.15f + 0.35f * ld * revShare : 0.25f;
        crankA = crank * volume;

        // nobody near: stop the synth (the audio thread costs something per car)
        if (ear == null || !ear.isActiveAndEnabled) ear = FindAnyObjectByType<AudioListener>();
        var listener = ear != null ? ear.transform.position : transform.position;
        float d2 = (listener - transform.position).sqrMagnitude, far = src.maxDistance * src.maxDistance;
        if (d2 < far * 1.1f && !src.isPlaying) src.Play();
        else if (d2 > far * 1.3f && src.isPlaying) src.Pause();
    }

    private float Rand()
    {
        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
        return (seed & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (keyCue != keyDone) { keyDone = keyCue; keyT = 0f; }
        if (grindCue != grindDone) { grindDone = grindCue; grindT = 0f; }
        if (tickCue != tickDone) { tickDone = tickCue; tickT = 0f; tock = false; }
        if (tockCue != tockDone) { tockDone = tockCue; tickT = 0f; tock = true; }

        float rpmTarget = rpmA, ampTarget = ampA, noiseTarget = noiseA, crankTarget = crankA;
        float step = 1f / rate;
        // glide to the new values over a few milliseconds instead of jumping once per block (that clicked)
        float glide = 1f - Mathf.Exp(-step * 60f);
        for (int i = 0; i < data.Length; i += channels)
        {
            rpmS += (rpmTarget - rpmS) * glide;
            ampS += (ampTarget - ampS) * glide;
            noiseS += (noiseTarget - noiseS) * glide;
            crankS += (crankTarget - crankS) * glide;

            phase += rpmS / 30.0 / rate;         // four cylinders: two firings per turn
            if (phase > 1.0) phase -= 1.0;
            float p = (float)(phase * 2.0 * Mathf.PI);
            float s = Mathf.Sin(p) * 0.6f + Mathf.Sin(2 * p) * 0.3f + Mathf.Sin(3 * p) * 0.18f + Mathf.Sin(5 * p) * 0.08f;
            lp += (Rand() - lp) * 0.08f;          // low-passed noise: intake and exhaust roar
            float x = (s * 0.35f + lp * noiseS) * ampS;

            // starter: an electric whine, chugging with each compression stroke
            if (crankS > 0.001f)
            {
                crankPhase += 190.0 / rate;
                if (crankPhase > 1.0) crankPhase -= 1.0;
                float whine = Mathf.Sin((float)(crankPhase * 2.0 * Mathf.PI)) + 0.4f * Mathf.Sin((float)(crankPhase * 4.0 * Mathf.PI));
                float chug = 0.55f + 0.45f * Mathf.Sin(p);
                x += (whine * 0.25f + lp * 0.6f) * chug * crankS;
            }
            // the key: a dry click
            if (keyT < 0.03f)
            {
                x += Rand() * (1f - keyT / 0.03f) * 0.5f * volume;
                keyT += step;
            }
            // gears grinding: a harsh rasp, buzzing at the speed difference
            if (grindT < 0.4f)
            {
                float buzz = Mathf.Sign(Mathf.Sin(grindT * 2f * Mathf.PI * 70f));
                bp1 += (Rand() - bp1) * 0.6f;
                bp2 += (bp1 - bp2) * 0.6f;
                x += (bp1 - bp2) * (0.6f + 0.4f * buzz) * (1f - grindT / 0.4f) * 1.4f * volume;
                grindT += step;
            }
            // the indicator relay
            if (tickT < 0.012f)
            {
                float e = 1f - tickT / 0.012f;
                x += Mathf.Sin(tickT * 2f * Mathf.PI * (tock ? 1500f : 2100f)) * e * e * 0.5f * volume;
                tickT += step;
            }
            for (int c = 0; c < channels; c++) data[i + c] *= x;
        }
    }
}
