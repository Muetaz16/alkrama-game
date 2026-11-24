using System.Collections.Generic;
using System.Threading;
using UnityEngine;

// Every sound of the fight, made in code (there are no audio files in the project).
//
//   weapons    layered the way weapon sound design is done: supersonic crack + low body thump + the action
//              cycling + early reflections off the buildings + a filtered noise tail. Several pre-rendered
//              takes per weapon, a small pool of voices so a 600 rpm burst overlaps its own tails.
//   distance   enemy fire is delayed by the speed of sound, near = crack and body, far = a dull thump and
//              the echo; rounds that pass close crack and zip on the side they went by.
//   foley      footsteps on the player's own step phase, by surface; kit rattle, jump / land, reload parts,
//              dry fire, brass on the ground, knife, bullet impacts by surface, grenades.
//   ambience   wind and surf loops, far-off small-arms fire and the odd distant explosion.
//
// All clips are synthesised on background threads while the scene loads and handed to Unity a few per
// frame, so start-up costs the main thread next to nothing; a sound whose clip is not ready yet is skipped.
// Voices are pooled and capped; nothing here allocates once the clips exist. With no audio device (the
// unattended test) every call is a quiet no-op.
public static class CombatAudio
{
    // ---------- tunables ----------
    public static float MasterVolume = 0.9f;        // AudioListener.volume (ducked for a moment by a close blast)
    public static float WeaponLevel = 0.85f;        // the player's own gun
    public static float EnemyFireLevel = 0.9f;      // enemy gunfire, before distance
    public static float FoleyLevel = 0.55f;         // steps, kit, reload
    public static float ImpactLevel = 0.8f;         // rounds striking things
    public static float ExplosionLevel = 1.0f;
    public static float AmbienceLevel = 0.5f;       // wind + surf beds and the distant battle (0 = off)
    public static float FarBattleMin = 5f, FarBattleMax = 16f;   // seconds between far-off bursts
    public static float FarBoomMin = 40f, FarBoomMax = 110f;     // seconds between far-off explosions
    public static float BlastDuckRadius = 14f;      // a blast nearer than this muffles your hearing for ~3 s
    public const int WorldVoices = 24, LocalVoices = 10;
    const float SpeedOfSound = 343f;

    enum Id
    {
        AkP, SnP, DryFire, MagOut, MagIn, BoltBack, BoltFwd, StepConcrete, StepAsphalt, StepSand, StepWater, Gear,
        ImpConcrete, ImpMetal, ImpSand, ImpWater, ImpFlesh, ImpWood, AkE, AkFar, Whiz, Rico, Shell, Swish, KnifeHard,
        Jump, Land, BodyFall, NadeBounce, Explosion, Ring, Wind, Surf, FarBurst, FarBoom, Count
    }

    sealed class Job { public Id id; public int v, rate; public System.Func<float[]> gen; public float[] data; }
    struct Pending { public float at; public int kind; public Vector3 pos; public float a; }

    static readonly AudioClip[][] bank = new AudioClip[(int)Id.Count][];
    static readonly int[] lastPick = new int[(int)Id.Count];
    static Queue<Job> done = new();
    static bool started, failed;
    static int clipsLeft;
    static CombatAudioRunner runner;
    static AudioSource[] world, local; static double[] worldBusy, localBusy;
    static AudioSource windSrc, surfSrc;
    static Transform listener; static AudioLowPassFilter lpf; static Vector3 lpos; static float listenerCheck;
    static readonly Pending[] pend = new Pending[32]; static int pendCount;
    static float duck, duckApplied, duckPeak, duckAt, nextFar, nextBoom, lastWhiz, lastImpactT;
    // the player's feet
    static PlayerController pc; static CharacterController pcc; static float pcCheck, stepDist, airT, lastVy; static int lastHalf; static bool wasGrounded = true;

    public static bool ClipsReady => started && clipsLeft <= 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        started = failed = false; runner = null; world = local = null; windSrc = surfSrc = null; listener = null; lpf = null;
        done = new Queue<Job>(); pendCount = 0; duck = duckApplied = duckPeak = 0; pc = null; pcc = null; wasGrounded = true;
        for (int i = 0; i < bank.Length; i++) bank[i] = null;
    }

    // the synthesis starts before the scene loads, on other cores
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { try { StartWorkers(); } catch (System.Exception e) { failed = true; Debug.LogWarning("CombatAudio off: " + e.Message); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AfterLoad() { Ready(); }

    static bool Ready()
    {
        if (runner) return true;
        if (failed || !Application.isPlaying) return false;
        try { Build(); }
        catch (System.Exception e) { failed = true; Debug.LogWarning("CombatAudio off: " + e.Message); }
        return runner;
    }

    static void Build()
    {
        StartWorkers();
        var go = new GameObject("CombatAudio"); Object.DontDestroyOnLoad(go);
        var flat = AnimationCurve.Constant(0, 1, 1);          // distance is applied by hand (see Atten), Unity only pans
        world = new AudioSource[WorldVoices]; worldBusy = new double[WorldVoices];
        for (int i = 0; i < WorldVoices; i++)
        {
            var v = new GameObject("voice " + i); v.transform.SetParent(go.transform, false);
            var s = v.AddComponent<AudioSource>(); s.playOnAwake = false; s.spatialBlend = 1; s.dopplerLevel = 0; s.spread = 25; s.reverbZoneMix = 0;
            s.rolloffMode = AudioRolloffMode.Custom; s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, flat); s.minDistance = 1; s.maxDistance = 2000;
            world[i] = s;
        }
        local = new AudioSource[LocalVoices]; localBusy = new double[LocalVoices];
        for (int i = 0; i < LocalVoices; i++) { var s = go.AddComponent<AudioSource>(); s.playOnAwake = false; s.spatialBlend = 0; s.reverbZoneMix = 0; local[i] = s; }
        windSrc = go.AddComponent<AudioSource>(); surfSrc = go.AddComponent<AudioSource>();
        foreach (var s in new[] { windSrc, surfSrc }) { s.playOnAwake = false; s.spatialBlend = 0; s.loop = true; s.priority = 200; s.volume = 0; }
        nextFar = Time.time + Random.Range(4f, 9f); nextBoom = Time.time + Random.Range(20f, 60f);
        runner = go.AddComponent<CombatAudioRunner>();
    }

    // ============================================================ public API ============================================================

    /// <summary>A round fired. weapon 0 = AK, 1 = sniper. The player's own gun is 2D; enemy fire is placed in the
    /// world, delayed by the speed of sound and turned into a dull thump + echo with distance (or behind a wall).</summary>
    public static void Shot(int weapon, Vector3 pos, bool isPlayer, bool occluded = false)
    {
        if (!Ready()) return;
        if (isPlayer)
        {
            PlayLocal(Pick(weapon == 1 ? Id.SnP : Id.AkP), WeaponLevel * (weapon == 1 ? 1.15f : 1f), Random.Range(0.965f, 1.035f), 0, 0);
            return;
        }
        float d = Vector3.Distance(lpos, pos), delay = d / SpeedOfSound, g = EnemyFireLevel * Atten(d, 28f, 600f);
        float far = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(25f, 85f, d)), near = 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(40f, 100f, d));
        if (occluded) { near *= 0.4f; far = Mathf.Max(far, 0.6f); g *= 0.8f; }
        float pitch = Random.Range(0.94f, 1.05f);
        if (near > 0.06f) PlayWorld(Pick(Id.AkE), pos, g * near, pitch, delay, 40);
        if (far > 0.06f) PlayWorld(Pick(Id.AkFar), pos, Mathf.Min(1f, g * far * 1.5f), pitch, delay, 70);
    }

    /// <summary>A round flew from a to b without hitting the player: if it passed close to the listener it cracks
    /// and zips on that side.</summary>
    public static void BulletPass(Vector3 a, Vector3 b)
    {
        if (!Ready() || Time.time - lastWhiz < 0.05f) return;
        var ab = b - a; float len2 = ab.sqrMagnitude; if (len2 < 9f) return;
        float t = Vector3.Dot(lpos - a, ab) / len2; if (t < 0.02f || t > 0.995f) return;
        var p = a + ab * t; var off = p - lpos; float miss = off.magnitude; if (miss > 4.5f) return;
        lastWhiz = Time.time;
        if (miss < 0.7f) p = lpos + (miss > 0.01f ? off / miss : Vector3.right) * 0.7f;
        float k = 1f - miss / 4.5f;
        PlayWorld(Pick(Id.Whiz), p, Mathf.Lerp(0.3f, 0.95f, k), Random.Range(0.9f, 1.15f), 0, 20);
        CombatFx.Shake(0.22f * k, 0.12f);
    }

    /// <summary>A round striking something.</summary>
    public static void Impact(Surface s, Vector3 p, bool heavy = false)
    {
        if (!Ready()) return;
        float d = Vector3.Distance(lpos, p); if (d > 110f) return;
        float g = ImpactLevel * Atten(d, 7f, 110f) * (heavy ? 1f : 0.8f), delay = d > 15f ? d / SpeedOfSound : 0f;
        Id id = s == Surface.Metal ? Id.ImpMetal : s == Surface.Sand ? Id.ImpSand : s == Surface.Water ? Id.ImpWater : s == Surface.Flesh ? Id.ImpFlesh : s == Surface.Wood ? Id.ImpWood : Id.ImpConcrete;
        PlayWorld(Pick(id), p, g, Random.Range(0.9f, 1.12f) * (heavy ? 0.88f : 1f), delay, 90);
        float rico = s == Surface.Metal ? 0.3f : s == Surface.Concrete || s == Surface.Asphalt ? 0.1f : 0f;
        if (rico > 0 && Random.value < rico && Time.time - lastImpactT > 0.15f) { lastImpactT = Time.time; PlayWorld(Pick(Id.Rico), p, g * 0.45f, Random.Range(0.8f, 1.3f), delay + 0.015f, 110); }
    }

    /// <summary>An enemy's (or anyone's) footstep in the world. The player's own steps are handled here by themselves.</summary>
    public static void Footstep(Vector3 pos, float speed, bool crouched)
    {
        if (!Ready()) return;
        float d = Vector3.Distance(lpos, pos); if (d > 32f) return;
        float g = FoleyLevel * Atten(d, 3.5f, 32f) * (speed > 3.5f ? 1f : 0.6f) * (crouched ? 0.5f : 1f);
        PlayWorld(Pick(StepId(CombatSurface.Under(pos))), pos, g, Random.Range(0.88f, 1.12f), 0, 140);
        if (speed > 3.5f && d < 18f) PlayWorld(Pick(Id.Gear), pos + Vector3.up, g * 0.5f, Random.Range(0.9f, 1.15f), 0.03f, 160);
    }

    /// <summary>Grenade going off: crack, a real low boom, debris; close by it rings in your ears and everything
    /// else is muffled for a few seconds.</summary>
    public static void Explosion(Vector3 p)
    {
        if (!Ready()) return;
        float d = Vector3.Distance(lpos, p), delay = d / SpeedOfSound;
        PlayWorld(Pick(Id.Explosion), p, Mathf.Min(1f, ExplosionLevel * Atten(d, 40f, 900f) * 1.2f), Random.Range(0.92f, 1.05f), delay, 5);
        if (d < BlastDuckRadius)
        {
            float k = 1f - d / BlastDuckRadius;
            duckPeak = Mathf.Max(duckPeak, Mathf.Lerp(0.35f, 1f, k)); duckAt = Time.time + delay + 0.14f;   // the blast itself is heard at full level
            PlayLocal(Pick(Id.Ring), 0.3f * k, 1f, delay + 0.1f, 30);
        }
    }

    /// <summary>Reload choreography events from the view model: "magout", "magin", "boltback", "boltfwd", "swish".</summary>
    public static void Reload(string ev)
    {
        if (!Ready()) return;
        float p = Random.Range(0.96f, 1.05f);
        switch (ev)
        {
            case "magout": PlayLocal(Pick(Id.MagOut), FoleyLevel * 1.1f, p, 0, 50); break;
            case "magin": PlayLocal(Pick(Id.MagIn), FoleyLevel * 1.3f, p, 0, 50); break;
            case "boltback": PlayLocal(Pick(Id.BoltBack), FoleyLevel * 1.15f, p, 0, 50); break;
            case "boltfwd": PlayLocal(Pick(Id.BoltFwd), FoleyLevel * 1.4f, p, 0, 50); break;
            case "swish": PlayLocal(Pick(Id.Swish), FoleyLevel * 1.1f, Random.Range(0.9f, 1.15f), 0, 50); break;
        }
    }

    public static void DryFire() { if (Ready()) PlayLocal(Pick(Id.DryFire), FoleyLevel, Random.Range(0.97f, 1.04f), 0, 40); }

    /// <summary>The knife landing: on a body or on something hard.</summary>
    public static void KnifeHit(bool flesh, Vector3 point)
    {
        if (!Ready()) return;
        PlayLocal(Pick(flesh ? Id.ImpFlesh : Id.KnifeHard), flesh ? 0.9f : 0.6f, Random.Range(0.85f, 1f), 0, 30);
    }

    /// <summary>A spent case: it tinkles on the ground a moment after it leaves the port.</summary>
    public static void ShellDrop(Vector3 ejectedAt, float groundY, bool big)
    {
        if (!Ready()) return;
        float fall = Mathf.Sqrt(Mathf.Max(0.1f, 2f * (ejectedAt.y - groundY + 0.35f) / 9.81f)) + 0.12f;
        ejectedAt.y = groundY + 0.05f;
        Later(0, Time.time + fall + Random.Range(0f, 0.12f), ejectedAt, big ? 0.78f : 1f);
    }

    /// <summary>A man going down: kit and body on the ground half a second later.</summary>
    public static void BodyFall(Vector3 pos) { if (Ready()) Later(1, Time.time + 0.55f, pos, 1); }

    public static void GrenadeBounce(Vector3 pos, float strength)
    {
        if (!Ready()) return;
        float d = Vector3.Distance(lpos, pos);
        PlayWorld(Pick(Id.NadeBounce), pos, FoleyLevel * 1.5f * Atten(d, 5f, 45f) * Mathf.Clamp01(strength), Random.Range(0.9f, 1.1f), 0, 60);
    }

    /// <summary>The player takes a round.</summary>
    public static void PlayerHurt() { if (Ready()) PlayLocal(Pick(Id.ImpFlesh), 0.75f, Random.Range(0.78f, 0.9f), 0, 10); }

    // ============================================================ playback ============================================================

    static float Atten(float d, float refD, float maxD) => refD / (refD + d) * (1f - Mathf.Clamp01((d - maxD * 0.6f) / (maxD * 0.4f)));

    static AudioClip Pick(Id id)
    {
        var a = bank[(int)id]; if (a == null) return null;
        int n = a.Length, k = Random.Range(0, n); if (n > 1 && k == lastPick[(int)id]) k = (k + 1) % n;
        for (int i = 0; i < n; i++) { int j = (k + i) % n; if (a[j]) { lastPick[(int)id] = j; return a[j]; } }
        return null;
    }

    // a free voice, or the one that is closest to finishing (its tail is the quietest thing playing)
    static int Free(double[] busy, double now)
    {
        int best = 0; double bt = double.MaxValue;
        for (int i = 0; i < busy.Length; i++) { if (busy[i] <= now) return i; if (busy[i] < bt) { bt = busy[i]; best = i; } }
        return best;
    }

    static void PlayWorld(AudioClip c, Vector3 pos, float vol, float pitch, float delay, int priority)
    {
        if (!c || vol < 0.004f) return;
        double now = AudioSettings.dspTime; int i = Free(worldBusy, now); var s = world[i];
        s.Stop(); s.clip = c; s.transform.position = pos; s.volume = Mathf.Clamp01(vol); s.pitch = pitch; s.priority = priority;
        if (delay > 0.005f) s.PlayScheduled(now + delay); else s.Play();
        worldBusy[i] = now + delay + c.length / pitch;
    }

    static void PlayLocal(AudioClip c, float vol, float pitch, float delay, int priority)
    {
        if (!c || vol < 0.004f) return;
        double now = AudioSettings.dspTime; int i = Free(localBusy, now); var s = local[i];
        s.Stop(); s.clip = c; s.volume = Mathf.Clamp01(vol); s.pitch = pitch; s.priority = priority;
        if (delay > 0.005f) s.PlayScheduled(now + delay); else s.Play();
        localBusy[i] = now + delay + c.length / pitch;
    }

    static void Later(int kind, float at, Vector3 pos, float a)
    {
        if (pendCount >= pend.Length) return;
        pend[pendCount++] = new Pending { kind = kind, at = at, pos = pos, a = a };
    }

    static Id StepId(Surface s) => s == Surface.Sand ? Id.StepSand : s == Surface.Water ? Id.StepWater : s == Surface.Asphalt ? Id.StepAsphalt : Id.StepConcrete;

    // ============================================================ every frame ============================================================

    internal static void Tick()
    {
        float now = Time.time, dt = Time.deltaTime;
        FinishClips();

        // who is listening
        if (!listener && now >= listenerCheck)
        {
            listenerCheck = now + 1f;
            var al = Object.FindAnyObjectByType<AudioListener>();
            if (!al && Camera.main) al = Camera.main.gameObject.AddComponent<AudioListener>();
            if (al)
            {
                listener = al.transform;
                lpf = al.GetComponent<AudioLowPassFilter>(); if (!lpf) lpf = al.gameObject.AddComponent<AudioLowPassFilter>();
                lpf.cutoffFrequency = 22000; lpf.enabled = false;
            }
        }
        if (listener) lpos = listener.position;

        // delayed one-shots: brass on the ground, a body falling
        for (int i = pendCount - 1; i >= 0; i--)
        {
            if (now < pend[i].at) continue;
            var e = pend[i]; pend[i] = pend[--pendCount];
            float d = Vector3.Distance(lpos, e.pos);
            if (e.kind == 0)
            {
                var s = CombatSurface.Under(e.pos + Vector3.up * 0.4f);
                if (s != Surface.Water && d < 18f) PlayWorld(Pick(Id.Shell), e.pos, FoleyLevel * Atten(d, 2.5f, 18f) * (s == Surface.Sand ? 0.25f : 0.8f), e.a * Random.Range(0.92f, 1.1f), 0, 170);
            }
            else if (d < 45f) PlayWorld(Pick(Id.BodyFall), e.pos, FoleyLevel * 1.6f * Atten(d, 5f, 45f), Random.Range(0.9f, 1.05f), 0, 100);
        }

        PlayerFoley(now, dt);
        Ambience(now);

        // a close blast: full level for the bang itself, then muffled and quiet, clearing over about three seconds
        if (duckPeak > 0 && now >= duckAt) { duck = Mathf.Max(duck, duckPeak); duckPeak = 0; }
        duck = Mathf.MoveTowards(duck, 0, dt / 3.2f);
        duckApplied = Mathf.MoveTowards(duckApplied, duck, dt * 7f);
        AudioListener.volume = MasterVolume * (1f - 0.55f * duckApplied);
        if (lpf)
        {
            bool on = duckApplied > 0.01f;
            if (on) lpf.cutoffFrequency = 22000f * Mathf.Pow(650f / 22000f, Mathf.Sqrt(duckApplied));
            if (lpf.enabled != on) lpf.enabled = on;
        }
    }

    static void FinishClips()
    {
        for (int k = 0; k < 4; k++)
        {
            Job j;
            lock (done) { if (done.Count == 0) return; j = done.Dequeue(); }
            clipsLeft--;
            if (j.data == null || failed) continue;
            try
            {
                var c = AudioClip.Create(j.id.ToString(), j.data.Length, 1, j.rate, false);
                if (c && c.SetData(j.data, 0)) { var a = bank[(int)j.id]; if (a != null && j.v < a.Length) a[j.v] = c; }
            }
            catch (System.Exception e) { failed = true; Debug.LogWarning("CombatAudio off: " + e.Message); }
            j.data = null;
        }
    }

    // the player's feet follow his own step phase (two foot plants per locomotion cycle), with the distance
    // walked as a second opinion; the surface is one short ray per step
    static void PlayerFoley(float now, float dt)
    {
        if (!pc)
        {
            if (now < pcCheck) return;
            pcCheck = now + 1f; pc = Object.FindAnyObjectByType<PlayerController>();
            if (!pc) return;
            pcc = pc.GetComponent<CharacterController>(); wasGrounded = true; airT = 0;
            if (!pcc) { pc = null; return; }
        }
        bool g = pcc.isGrounded; float sp = pc.Speed;
        if (g && !wasGrounded && airT > 0.22f && lastVy < -2.5f)
        {
            float k = Mathf.InverseLerp(-2.5f, -9f, lastVy);
            PlayLocal(Pick(Id.Land), FoleyLevel * Mathf.Lerp(0.7f, 1.6f, k), Random.Range(0.92f, 1.05f), 0, 60);
            PlayLocal(Pick(StepId(CombatSurface.Under(pc.transform.position))), FoleyLevel * 1.2f, 0.85f, 0, 80);
            stepDist = 0;
        }
        if (!g && wasGrounded && pcc.velocity.y > 2.5f) PlayLocal(Pick(Id.Jump), FoleyLevel * 0.8f, Random.Range(0.95f, 1.08f), 0, 80);
        airT = g ? 0 : airT + dt; lastVy = pcc.velocity.y; wasGrounded = g;

        int half = (int)(pc.StepPhase / Mathf.PI);
        if (!g || sp < 0.7f || pc.frozen) { lastHalf = half; stepDist = Mathf.Min(stepDist, 0.4f); return; }
        stepDist += sp * dt;
        float stride = pc.prone ? 0.8f : sp > 6f ? 2.0f : sp > 3f ? 1.45f : 0.95f;     // metres between foot plants
        bool step = (half != lastHalf && stepDist > stride * 0.5f) || stepDist > stride * 1.5f;
        lastHalf = half;
        if (!step) return;
        stepDist = 0;
        var surf = CombatSurface.Under(pc.transform.position);
        float run = Mathf.InverseLerp(1.5f, PlayerController.SprintMove, sp);
        float vol = FoleyLevel * Mathf.Lerp(0.55f, 1.25f, run) * (pc.prone ? 0.45f : pc.crouch ? 0.55f : 1f);
        PlayLocal(Pick(StepId(surf)), vol, Random.Range(0.9f, 1.1f) * (pc.prone ? 0.8f : 1f), 0, 120);
        if (sp > 3.4f && !pc.crouch) PlayLocal(Pick(Id.Gear), FoleyLevel * Mathf.Lerp(0.25f, 0.75f, run), Random.Range(0.9f, 1.15f), 0.035f, 150);
    }

    static void Ambience(float now)
    {
        float lvl = Mathf.Max(0, AmbienceLevel);
        Loop(windSrc, Id.Wind, lvl * (0.2f + 0.12f * Mathf.PerlinNoise(now * 0.07f, 3.3f)));
        Loop(surfSrc, Id.Surf, lvl * 0.26f);
        if (lvl <= 0.001f) return;
        if (now >= nextFar)
        {
            nextFar = now + Random.Range(FarBattleMin, FarBattleMax);
            int bursts = Random.value < 0.3f ? 2 : 1; float delay = 0;
            for (int i = 0; i < bursts; i++)                      // sometimes someone answers from another street
            {
                float az = Random.value * 6.2832f;
                PlayWorld(Pick(Id.FarBurst), lpos + new Vector3(Mathf.Cos(az), 0.1f, Mathf.Sin(az)) * 60f, lvl * Random.Range(0.14f, 0.36f), Random.Range(0.9f, 1.1f), delay, 180);
                delay += Random.Range(0.7f, 1.8f);
            }
        }
        if (now >= nextBoom)
        {
            nextBoom = now + Random.Range(FarBoomMin, FarBoomMax);
            float az = Random.value * 6.2832f;
            PlayWorld(Pick(Id.FarBoom), lpos + new Vector3(Mathf.Cos(az), 0.1f, Mathf.Sin(az)) * 60f, lvl * Random.Range(0.3f, 0.55f), Random.Range(0.9f, 1.08f), 0, 170);
        }
    }

    static void Loop(AudioSource s, Id id, float vol)
    {
        if (!s) return;
        if (!s.clip) { var a = bank[(int)id]; if (a == null || !a[0]) return; s.clip = a[0]; s.time = Random.value * a[0].length * 0.9f; }
        s.volume = vol;
        if (vol > 0.001f) { if (!s.isPlaying) s.Play(); }
        else if (s.isPlaying) s.Pause();
    }

    // ============================================================ the clip list ============================================================

    static void StartWorkers()
    {
        if (started) return; started = true;
        var list = new List<Job>(100);
        // in the order they are needed: the player's own gun and feet first, the far-off battle last
        Def(list, Id.AkP, 5, 44100, v => GenAk(101 + v * 7, false));
        Def(list, Id.DryFire, 1, 44100, v => GenDryFire(40));
        Def(list, Id.StepConcrete, 4, 44100, v => GenStep(200 + v, 0));
        Def(list, Id.StepAsphalt, 4, 44100, v => GenStep(220 + v, 1));
        Def(list, Id.Gear, 4, 44100, v => GenGear(300 + v));
        Def(list, Id.ImpConcrete, 4, 44100, v => GenImpConcrete(400 + v));
        Def(list, Id.SnP, 3, 44100, v => GenSniper(151 + v * 5));
        Def(list, Id.MagOut, 2, 44100, v => GenMagOut(50 + v));
        Def(list, Id.MagIn, 2, 44100, v => GenMagIn(54 + v));
        Def(list, Id.BoltBack, 2, 44100, v => GenBoltBack(58 + v));
        Def(list, Id.BoltFwd, 2, 44100, v => GenBoltFwd(62 + v));
        Def(list, Id.Shell, 4, 44100, v => GenShell(70 + v));
        Def(list, Id.ImpMetal, 4, 44100, v => GenImpMetal(420 + v));
        Def(list, Id.ImpFlesh, 3, 44100, v => GenImpFlesh(440 + v));
        Def(list, Id.AkE, 4, 44100, v => GenAk(171 + v * 3, true));
        Def(list, Id.AkFar, 3, 44100, v => GenFar(191 + v));
        Def(list, Id.Whiz, 4, 44100, v => GenWhiz(500 + v));
        Def(list, Id.Swish, 2, 44100, v => GenSwish(80 + v));
        Def(list, Id.KnifeHard, 2, 44100, v => GenKnifeHard(84 + v));
        Def(list, Id.StepSand, 4, 44100, v => GenStep(240 + v, 2));
        Def(list, Id.StepWater, 3, 44100, v => GenStep(260 + v, 3));
        Def(list, Id.ImpSand, 3, 44100, v => GenImpSand(460 + v));
        Def(list, Id.ImpWater, 3, 44100, v => GenImpWater(470 + v));
        Def(list, Id.ImpWood, 2, 44100, v => GenImpWood(480 + v));
        Def(list, Id.Rico, 3, 44100, v => GenRico(520 + v));
        Def(list, Id.Jump, 1, 44100, v => GenJump(90));
        Def(list, Id.Land, 2, 44100, v => GenLand(92 + v, false));
        Def(list, Id.BodyFall, 2, 44100, v => GenLand(96 + v, true));
        Def(list, Id.NadeBounce, 2, 44100, v => GenNadeBounce(98 + v));
        Def(list, Id.Explosion, 2, 44100, v => GenExplosion(600 + v));
        Def(list, Id.Ring, 1, 44100, v => GenRing());
        Def(list, Id.Wind, 1, 22050, v => GenWind(700));
        Def(list, Id.Surf, 1, 22050, v => GenSurf(710));
        Def(list, Id.FarBurst, 5, 22050, v => GenFarBurst(720 + v, v));
        Def(list, Id.FarBoom, 2, 22050, v => GenFarBoom(740 + v));

        var arr = list.ToArray(); var q = done; int[] next = { -1 };
        clipsLeft = arr.Length;
        ThreadStart body = () =>
        {
            while (true)
            {
                int i = Interlocked.Increment(ref next[0]); if (i >= arr.Length) break;
                var j = arr[i];
                try { sr = j.rate; j.data = j.gen(); } catch { j.data = null; }
                lock (q) q.Enqueue(j);
            }
        };
        int threads = Mathf.Clamp(System.Environment.ProcessorCount - 2, 1, 3), ok = 0;
        for (int t = 0; t < threads; t++)
        {
            try { new Thread(body) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "CombatAudio synth" }.Start(); ok++; }
            catch { break; }
        }
        if (ok == 0) body();          // no threads on this platform: do it here
    }

    static void Def(List<Job> list, Id id, int count, int rate, System.Func<int, float[]> gen)
    {
        bank[(int)id] = new AudioClip[count];
        for (int v = 0; v < count; v++) { int vv = v; list.Add(new Job { id = id, v = vv, rate = rate, gen = () => gen(vv) }); }
    }

    // ============================================================ synthesis (worker threads: no Unity objects, no UnityEngine.Random) ============================================================

    [System.ThreadStatic] static int sr;

    struct Rng
    {
        uint s;
        public Rng(int seed) { s = (uint)seed * 747796405u + 2891336453u; if (s == 0) s = 1; Next(); Next(); }
        public float Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 8388608f - 1f; }   // -1..1
        public float U() => (Next() + 1f) * 0.5f;                                                                // 0..1
        public float R(float a, float b) => a + (b - a) * U();
    }

    static int N(float seconds) => Mathf.Max(16, (int)(seconds * sr));
    static float Coef(float hz) => 1f - Mathf.Exp(-6.2832f * Mathf.Min(hz, sr * 0.45f) / sr);

    static void Norm(float[] d, float peak)
    {
        float m = 1e-6f; for (int i = 0; i < d.Length; i++) { float a = d[i] < 0 ? -d[i] : d[i]; if (a > m) m = a; }
        float g = peak / m; for (int i = 0; i < d.Length; i++) d[i] *= g;
    }

    static void FadeOut(float[] d, float seconds)
    {
        int f = Mathf.Min(d.Length, N(seconds));
        for (int k = 0; k < f; k++) d[d.Length - 1 - k] *= k / (float)f;
    }

    static void Add(float[] d, float[] s, float at, float gain)
    {
        int o = (int)(at * sr); if (o < 0) o = 0;
        int n = Mathf.Min(s.Length, d.Length - o);
        for (int i = 0; i < n; i++) d[o + i] += s[i] * gain;
    }

    static void LP(float[] d, float hz, int passes)
    {
        float a = Coef(hz);
        for (int p = 0; p < passes; p++) { float l = 0; for (int i = 0; i < d.Length; i++) { l += (d[i] - l) * a; d[i] = l; } }
    }

    static float[] Filtered(float[] s, float lpHz) { var c = (float[])s.Clone(); LP(c, lpHz, 2); return c; }

    // loud things are recorded squashed: normalise, then a soft clip
    static void Drive(float[] d, float amount)
    {
        Norm(d, 1f);
        for (int i = 0; i < d.Length; i++) d[i] = (float)System.Math.Tanh(d[i] * amount);
    }

    // band of noise with an attack and an exponential decay, peak 1
    static float[] Burst(ref Rng r, float seconds, float decay, float lp, float hp, float attack = 0f)
    {
        int n = N(seconds); var d = new float[n];
        float a = Coef(lp), b = hp > 0 ? Coef(hp) : 0, l1 = 0, l2 = 0, h = 0;
        float env = 1f, envMul = Mathf.Exp(-decay / sr), atk = attack > 0 ? 1f : 0f, atkMul = attack > 0 ? Mathf.Exp(-1f / (attack * sr)) : 0f;
        for (int i = 0; i < n; i++)
        {
            float x = r.Next();
            l1 += (x - l1) * a; l2 += (l1 - l2) * a;
            float y = l2;
            if (b > 0) { h += (y - h) * b; y -= h; }
            d[i] = y * env * (1f - atk); env *= envMul; atk *= atkMul;
        }
        Norm(d, 1f); FadeOut(d, Mathf.Min(seconds * 0.25f, 0.004f));
        return d;
    }

    // a sine whose pitch falls from f0 to f1 (the low thump of a shot, a boom, a plop), peak 1
    static float[] Sweep(float seconds, float f0, float f1, float rate, float decay)
    {
        int n = N(seconds); var d = new float[n]; double ph = 0; float dtS = 1f / sr;
        float env = 1f, envMul = Mathf.Exp(-decay / sr), k = 1f, kMul = Mathf.Exp(-rate / sr);
        for (int i = 0; i < n; i++)
        {
            ph += 6.283185307 * (f1 + (f0 - f1) * k) * dtS;
            d[i] = (float)System.Math.Sin(ph) * env; env *= envMul; k *= kMul;
        }
        FadeOut(d, Mathf.Min(seconds * 0.25f, 0.006f));
        return d;
    }

    static readonly float[] Modes = { 1f, 1.593f, 2.312f, 3.176f, 4.12f, 5.43f };

    // struck metal: a few inharmonic partials ringing down, with a tick of noise on the front, peak 1
    static float[] Ping(ref Rng r, float seconds, float f, float decay, int partials, float tick)
    {
        int n = N(seconds); var d = new float[n]; float dtS = 1f / sr;
        for (int p = 0; p < partials && p < Modes.Length; p++)
        {
            float fp = f * Modes[p] * (1f + r.Next() * 0.012f); if (fp > sr * 0.45f) break;
            float amp = 1f / (1f + p * 0.7f), env = 1f, envMul = Mathf.Exp(-decay * (1f + p * 0.45f) / sr);
            double ph = r.U() * 6.28, w = 6.283185307 * fp * dtS;
            for (int i = 0; i < n; i++) { d[i] += (float)System.Math.Sin(ph) * amp * env; ph += w; env *= envMul; }
        }
        Norm(d, 1f);
        int tn = Mathf.Min(n, N(0.0015f));
        for (int i = 0; i < tn; i++) d[i] += r.Next() * tick * (1f - i / (float)tn);
        Norm(d, 1f); FadeOut(d, Mathf.Min(seconds * 0.25f, 0.004f));
        return d;
    }

    // noise through a resonant band whose centre slides from f0 to f1 (a whiz, a swish, steel sliding on steel), peak 1
    static float[] BandSweep(ref Rng r, float seconds, float f0, float f1, float damp, float attackFrac, float curve)
    {
        int n = N(seconds); var d = new float[n]; float low = 0, band = 0, ratio = f1 / f0;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n, fc = Mathf.Min(f0 * Mathf.Pow(ratio, u), sr * 0.15f);
            float f = 2f * Mathf.Sin(3.14159f * fc / sr);
            low += f * band; float high = r.Next() - low - damp * band; band += f * high;
            float env = u < attackFrac ? u / attackFrac : Mathf.Pow(1f - (u - attackFrac) / (1f - attackFrac), curve);
            d[i] = band * env;
        }
        Norm(d, 1f);
        return d;
    }

    // bits coming down afterwards: a scatter of tiny clicks, thinning out
    static void Patter(float[] d, ref Rng r, float t0, float t1, int count, float g0, float g1, float lp, float hp)
    {
        for (int k = 0; k < count; k++)
        {
            float u = r.U(); u *= Mathf.Sqrt(u);
            Add(d, Burst(ref r, 0.004f + r.U() * 0.012f, 500f, lp * r.R(0.6f, 1.2f), hp), t0 + (t1 - t0) * u, Mathf.Lerp(g0, g1, u) * r.R(0.5f, 1.3f));
        }
    }

    // a seamless loop out of a buffer that runs `extra` samples past the loop point
    static float[] MakeLoop(float[] ext, int n)
    {
        int x = ext.Length - n; var d = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i < x) { float w = i / (float)x * 1.5708f; d[i] = ext[i] * Mathf.Sin(w) + ext[n + i] * Mathf.Cos(w); }
            else d[i] = ext[i];
        }
        return d;
    }

    // ---------- weapons ----------

    // 7.62x39 from a stamped-steel rifle: a hard crack, a harsh mid bark, the thump in the chest, the carrier
    // slamming back and home, then the street answering - a few slaps off the facades and a wash of noise
    static float[] GenAk(int seed, bool enemy)
    {
        var r = new Rng(seed);
        var dry = new float[N(0.42f)];
        Add(dry, Burst(ref r, 0.03f, r.R(360f, 500f), 11000f, 1600f), 0f, 1f);
        Add(dry, Burst(ref r, 0.14f, r.R(48f, 62f), r.R(2400f, 2900f), 450f), 0.0004f, 0.8f);
        Add(dry, Sweep(0.28f, r.R(185f, 215f), 58f, 34f, 24f), 0f, 0.95f);
        Add(dry, Burst(ref r, 0.26f, 26f, 380f, 55f), 0.001f, 0.7f);
        Add(dry, Ping(ref r, 0.05f, r.R(2100f, 2600f), 170f, 3, 0.5f), 0.024f, enemy ? 0.05f : 0.11f);
        Add(dry, Ping(ref r, 0.06f, r.R(2900f, 3400f), 150f, 3, 0.6f), r.R(0.058f, 0.066f), enemy ? 0.05f : 0.10f);
        Drive(dry, 2.6f);
        float len = enemy ? 1.0f : 1.4f; var d = new float[N(len)];
        Add(d, dry, 0, 1f);
        float t = r.R(0.055f, 0.085f), g = 0.24f;
        for (int k = 0; k < 4; k++)
        {
            Add(d, Filtered(dry, 2800f / (1f + k * 0.7f)), t, g * r.R(0.8f, 1.2f));
            t += r.R(0.05f, 0.11f) + k * 0.03f; g *= 0.68f;
        }
        Add(d, Burst(ref r, len - 0.05f, enemy ? 6.5f : 4.6f, 1500f, 160f, 0.025f), 0.03f, 0.15f);
        Norm(d, 0.95f); FadeOut(d, 0.1f);
        return d;
    }

    // the heavy rifle: more of everything, lower, and a long roll down the street
    static float[] GenSniper(int seed)
    {
        var r = new Rng(seed);
        var dry = new float[N(0.7f)];
        Add(dry, Burst(ref r, 0.04f, r.R(230f, 300f), 12000f, 1200f), 0f, 1f);
        Add(dry, Burst(ref r, 0.2f, r.R(30f, 38f), 2000f, 300f), 0.0004f, 0.85f);
        Add(dry, Sweep(0.6f, r.R(140f, 160f), 40f, 20f, 11f), 0f, 1.1f);
        Add(dry, Burst(ref r, 0.5f, 13f, 300f, 35f), 0.001f, 0.95f);
        Drive(dry, 3.3f);
        const float len = 2.5f; var d = new float[N(len)];
        Add(d, dry, 0, 1f);
        float t = r.R(0.08f, 0.11f), g = 0.3f;
        for (int k = 0; k < 6; k++)
        {
            Add(d, Filtered(dry, 2400f / (1f + k * 0.6f)), t, g * r.R(0.8f, 1.2f));
            t += r.R(0.07f, 0.14f) + k * 0.04f; g *= 0.7f;
        }
        Add(d, Burst(ref r, len - 0.05f, 2.5f, 1300f, 110f, 0.03f), 0.03f, 0.22f);
        Add(d, Burst(ref r, 1.6f, 3.2f, 260f, 40f, 0.05f), 0.05f, 0.16f);
        Norm(d, 0.97f); FadeOut(d, 0.15f);
        return d;
    }

    // the same rifle from a couple of streets away: the air has taken the top off, what arrives is thump and echo
    static float[] GenFar(int seed)
    {
        var r = new Rng(seed);
        var dry = new float[N(0.5f)];
        Add(dry, Sweep(0.3f, r.R(115f, 135f), 50f, 25f, 16f), 0f, 1f);
        Add(dry, Burst(ref r, 0.35f, 15f, 520f, 60f, 0.003f), 0f, 0.9f);
        Add(dry, Burst(ref r, 0.05f, 120f, 1800f, 400f, 0.002f), 0f, 0.25f);
        Drive(dry, 1.8f);
        var d = new float[N(1.5f)];
        Add(d, dry, 0, 1f);
        float t = r.R(0.1f, 0.15f), g = 0.42f;
        for (int k = 0; k < 4; k++) { Add(d, Filtered(dry, 700f / (1f + k * 0.4f)), t, g); t += r.R(0.09f, 0.18f); g *= 0.7f; }
        Add(d, Burst(ref r, 1.4f, 3.6f, 650f, 90f, 0.04f), 0.03f, 0.38f);
        LP(d, 1500f, 1);
        Norm(d, 0.9f); FadeOut(d, 0.12f);
        return d;
    }

    static float[] GenDryFire(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.14f)];
        Add(d, Burst(ref r, 0.006f, 900f, 9000f, 2000f), 0f, 0.8f);
        Add(d, Ping(ref r, 0.1f, 3400f, 260f, 3, 0.4f), 0f, 0.55f);
        Add(d, Burst(ref r, 0.03f, 140f, 1400f, 300f), 0.001f, 0.4f);
        Norm(d, 0.5f); FadeOut(d, 0.02f); return d;
    }

    // magazine catch pressed, the magazine rocks out of the well
    static float[] GenMagOut(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.42f)];
        Add(d, Ping(ref r, 0.06f, r.R(2500f, 2800f), 220f, 3, 0.7f), 0f, 0.5f);
        Add(d, BandSweep(ref r, 0.15f, 2200f, 1300f, 0.5f, 0.3f, 1.2f), 0.05f, 0.22f);
        float t = r.R(0.18f, 0.2f);
        Add(d, Ping(ref r, 0.12f, r.R(850f, 1000f), 110f, 4, 0.5f), t, 0.6f);
        Add(d, Burst(ref r, 0.03f, 260f, 3200f, 300f), t, 0.5f);
        Add(d, Burst(ref r, 0.08f, 50f, 600f, 90f), t, 0.35f);
        Norm(d, 0.7f); FadeOut(d, 0.03f); return d;
    }

    // fresh magazine: front lug in, rocked back until the catch snaps over it
    static float[] GenMagIn(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.45f)];
        Add(d, BandSweep(ref r, 0.12f, 1400f, 2400f, 0.5f, 0.4f, 1f), 0f, 0.2f);
        float t = 0.12f;
        Add(d, Burst(ref r, 0.05f, 110f, 1300f, 140f), t, 0.85f);
        Add(d, Ping(ref r, 0.1f, r.R(1150f, 1300f), 120f, 4, 0.4f), t, 0.5f);
        t = r.R(0.2f, 0.22f);
        Add(d, Ping(ref r, 0.08f, r.R(2900f, 3200f), 240f, 4, 0.9f), t, 0.75f);
        Add(d, Burst(ref r, 0.02f, 400f, 6000f, 800f), t, 0.5f);
        Add(d, Burst(ref r, 0.06f, 70f, 500f, 80f), t, 0.4f);
        Norm(d, 0.8f); FadeOut(d, 0.03f); return d;
    }

    // charging handle hauled back against the spring
    static float[] GenBoltBack(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.3f)];
        Add(d, BandSweep(ref r, 0.14f, 1500f, 3200f, 0.35f, 0.25f, 0.8f), 0f, 0.35f);
        float t = 0.14f;
        Add(d, Ping(ref r, 0.1f, r.R(1650f, 1850f), 150f, 4, 0.8f), t, 0.8f);
        Add(d, Burst(ref r, 0.025f, 300f, 5000f, 500f), t, 0.6f);
        Norm(d, 0.72f); FadeOut(d, 0.03f); return d;
    }

    // ... and let go: the carrier runs home and the bolt locks
    static float[] GenBoltFwd(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.32f)];
        Add(d, BandSweep(ref r, 0.045f, 3000f, 1800f, 0.3f, 0.2f, 1f), 0f, 0.2f);
        float t = 0.045f;
        Add(d, Burst(ref r, 0.035f, 180f, 4500f, 400f), t, 1f);
        Add(d, Ping(ref r, 0.14f, r.R(1250f, 1400f), 100f, 5, 0.7f), t, 0.7f);
        Add(d, Sweep(0.06f, 260f, 150f, 30f, 60f), t, 0.5f);
        Add(d, Ping(ref r, 0.2f, r.R(520f, 600f), 28f, 2, 0f), t + 0.004f, 0.08f);   // the recoil spring singing
        Norm(d, 0.85f); FadeOut(d, 0.03f); return d;
    }

    // a brass / steel case: two or three bounces, each a little bell
    static float[] GenShell(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.55f)];
        float f = r.R(3600f, 5200f), t = 0, g = 1f;
        int bounces = 2 + (r.U() > 0.4f ? 1 : 0) + (r.U() > 0.7f ? 1 : 0);
        for (int k = 0; k < bounces; k++)
        {
            Add(d, Ping(ref r, 0.12f, f * r.R(0.93f, 1.08f), r.R(45f, 80f), 4, 0.5f), t, g);
            t += r.R(0.07f, 0.15f) * (1f - k * 0.2f); g *= r.R(0.45f, 0.65f);
        }
        Norm(d, 0.6f); FadeOut(d, 0.04f); return d;
    }

    static float[] GenSwish(int seed)
    {
        var r = new Rng(seed); float len = r.R(0.22f, 0.28f);
        var d = new float[N(len + 0.02f)];
        Add(d, BandSweep(ref r, len, r.R(500f, 700f), r.R(3200f, 4200f), 0.6f, 0.55f, 1.6f), 0f, 1f);
        Add(d, BandSweep(ref r, len, 300f, 1200f, 0.9f, 0.5f, 1.4f), 0f, 0.4f);
        Norm(d, 0.6f); FadeOut(d, 0.03f); return d;
    }

    static float[] GenKnifeHard(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.35f)];
        Add(d, Ping(ref r, 0.3f, r.R(2700f, 3300f), 55f, 5, 0.9f), 0f, 0.8f);
        Add(d, Burst(ref r, 0.03f, 250f, 5000f, 600f), 0f, 0.7f);
        Add(d, BandSweep(ref r, 0.09f, 3500f, 2200f, 0.4f, 0.1f, 1.5f), 0.01f, 0.3f);
        Norm(d, 0.7f); FadeOut(d, 0.03f); return d;
    }

    // ---------- rounds going past / striking ----------

    // a supersonic round a metre from your head: the snap of its shock wave, then the falling zip as it goes away
    static float[] GenWhiz(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.34f)];
        Add(d, Burst(ref r, 0.006f, 900f, 14000f, 2500f), 0f, 1f);
        Add(d, Burst(ref r, 0.03f, 200f, 6500f, 900f), 0.0003f, 0.4f);
        Add(d, BandSweep(ref r, r.R(0.18f, 0.26f), r.R(5200f, 6500f), r.R(900f, 1300f), 0.25f, 0.06f, 1.6f), 0.004f, r.R(0.35f, 0.5f));
        Add(d, Burst(ref r, 0.25f, 16f, 2500f, 500f, 0.01f), 0.03f, 0.09f);
        Norm(d, 0.9f); FadeOut(d, 0.03f); return d;
    }

    // the round whining away off something hard
    static float[] GenRico(int seed)
    {
        var r = new Rng(seed); int n = N(0.6f); var d = new float[n]; double ph = 0; float dtS = 1f / sr;
        float f0 = r.R(3400f, 4600f), f1 = r.R(1100f, 1700f), vib = r.R(28f, 45f), fall = r.R(5f, 8f);
        for (int i = 0; i < n; i++)
        {
            float t = i * dtS, f = (f1 + (f0 - f1) * Mathf.Exp(-t * fall)) * (1f + 0.05f * Mathf.Sin(t * vib * 6.2832f));
            ph += 6.283185307 * f * dtS;
            float env = (1f - Mathf.Exp(-t * 900f)) * Mathf.Exp(-t * 7f);
            d[i] = ((float)System.Math.Sin(ph) + 0.3f * (float)System.Math.Sin(ph * 1.5)) * env;
        }
        Add(d, Burst(ref r, 0.3f, 12f, 5000f, 1500f, 0.005f), 0f, 0.15f);
        Norm(d, 0.6f); FadeOut(d, 0.05f); return d;
    }

    static float[] GenImpConcrete(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.55f)];
        Add(d, Burst(ref r, 0.02f, r.R(350f, 480f), 9000f, 1200f), 0f, 1f);
        Add(d, Burst(ref r, 0.08f, r.R(55f, 75f), r.R(650f, 900f), 220f), 0f, 0.85f);
        Add(d, Burst(ref r, 0.15f, 30f, 3500f, 900f, 0.004f), 0.004f, 0.25f);
        Patter(d, ref r, 0.05f, 0.45f, 14, 0.3f, 0.04f, 5000f, 1200f);
        Norm(d, 0.85f); FadeOut(d, 0.03f); return d;
    }

    static float[] GenImpMetal(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.65f)];
        Add(d, Burst(ref r, 0.012f, 600f, 10000f, 2000f), 0f, 0.9f);
        Add(d, Ping(ref r, 0.6f, r.R(1500f, 2700f), r.R(11f, 18f), 6, 0.6f), 0f, 0.8f);
        Add(d, Sweep(0.09f, r.R(300f, 380f), 190f, 20f, 40f), 0f, 0.55f);        // the panel itself
        Add(d, Burst(ref r, 0.06f, 80f, 1500f, 250f), 0f, 0.4f);
        Norm(d, 0.85f); FadeOut(d, 0.05f); return d;
    }

    static float[] GenImpSand(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.3f)];
        Add(d, Burst(ref r, 0.16f, r.R(30f, 40f), 520f, 60f), 0f, 1f);
        Add(d, Burst(ref r, 0.22f, 20f, 5000f, 1800f, 0.012f), 0.002f, 0.2f);
        Norm(d, 0.7f); FadeOut(d, 0.03f); return d;
    }

    static float[] GenImpWater(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.5f)];
        Add(d, Sweep(0.12f, r.R(520f, 680f), 170f, 30f, 28f), 0f, 0.6f);
        Add(d, Burst(ref r, 0.4f, 11f, 4500f, 900f, 0.015f), 0.004f, 0.75f);
        for (int k = 0; k < 4; k++) Add(d, Sweep(0.04f, r.R(500f, 900f), r.R(1100f, 1800f), 60f, 70f), r.R(0.05f, 0.3f), r.R(0.06f, 0.14f));   // bubbles
        Norm(d, 0.75f); FadeOut(d, 0.04f); return d;
    }

    static float[] GenImpFlesh(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.22f)];
        Add(d, Burst(ref r, 0.1f, r.R(50f, 70f), 380f, 70f), 0f, 1f);
        Add(d, Sweep(0.09f, r.R(130f, 160f), 75f, 30f, 45f), 0f, 0.6f);
        Add(d, Burst(ref r, 0.035f, 190f, 2400f, 700f), 0.001f, 0.35f);
        Add(d, Burst(ref r, 0.12f, 30f, 1200f, 300f, 0.01f), 0.01f, 0.15f);      // cloth and webbing
        Norm(d, 0.85f); FadeOut(d, 0.03f); return d;
    }

    static float[] GenImpWood(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.3f)];
        Add(d, Ping(ref r, 0.2f, r.R(480f, 640f), 75f, 3, 0.5f), 0f, 0.9f);
        Add(d, Burst(ref r, 0.02f, 450f, 6000f, 800f), 0f, 0.7f);
        Add(d, Burst(ref r, 0.1f, 40f, 900f, 150f), 0f, 0.5f);
        Patter(d, ref r, 0.04f, 0.25f, 5, 0.15f, 0.04f, 3500f, 900f);
        Norm(d, 0.75f); FadeOut(d, 0.03f); return d;
    }

    // ---------- feet and kit ----------

    // one foot plant, heel then ball. kind: 0 paving stone, 1 asphalt (gritty), 2 sand, 3 shallow water
    static float[] GenStep(int seed, int kind)
    {
        var r = new Rng(seed); var d = new float[N(kind == 3 ? 0.45f : 0.3f)];
        float toe = r.R(0.045f, 0.075f);
        for (int k = 0; k < 2; k++)
        {
            float t = k * toe, g = k == 0 ? 1f : r.R(0.5f, 0.7f);
            if (kind == 0)
            {
                Add(d, Burst(ref r, 0.07f, r.R(80f, 100f), r.R(800f, 1000f), 110f), t, g);
                Add(d, Burst(ref r, 0.008f, 700f, 5000f, 1500f), t, g * 0.3f);
                Add(d, Burst(ref r, 0.07f, 40f, 6000f, 2500f, 0.01f), t, g * 0.1f);
            }
            else if (kind == 1)
            {
                Add(d, Burst(ref r, 0.07f, r.R(75f, 95f), r.R(560f, 720f), 90f), t, g);
                Add(d, Burst(ref r, 0.1f, 30f, 6500f, 2200f, 0.008f), t, g * 0.24f);
                Patter(d, ref r, t + 0.01f, t + 0.09f, 4, 0.12f * g, 0.05f * g, 6000f, 2000f);
            }
            else if (kind == 2)
            {
                Add(d, Burst(ref r, 0.15f, r.R(24f, 32f), 700f, 90f, 0.012f), t, g);
                Add(d, Burst(ref r, 0.17f, 20f, 4500f, 1500f, 0.02f), t, g * 0.32f);
            }
            else
            {
                Add(d, Burst(ref r, 0.3f, r.R(10f, 13f), 3200f, 500f, 0.02f), t, g);
                Add(d, Burst(ref r, 0.1f, 40f, 500f, 80f), t, g * 0.5f);
                Add(d, Sweep(0.05f, r.R(350f, 500f), r.R(700f, 1000f), 50f, 60f), t + r.R(0.05f, 0.15f), g * 0.15f);
            }
        }
        Norm(d, kind == 2 ? 0.55f : 0.7f); FadeOut(d, 0.03f); return d;
    }

    // webbing, sling swivels, magazines in their pouches
    static float[] GenGear(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.26f)];
        Add(d, Burst(ref r, 0.22f, 13f, 2600f, 600f, 0.04f), 0f, 0.5f);
        int n = 4 + (int)(r.U() * 4);
        for (int k = 0; k < n; k++)
        {
            float t = r.R(0f, 0.16f);
            if (r.U() < 0.5f) Add(d, Ping(ref r, 0.03f, r.R(1800f, 4200f), 260f, 2, 0.6f), t, r.R(0.15f, 0.45f));
            else Add(d, Burst(ref r, 0.012f, 400f, 4000f, 900f), t, r.R(0.2f, 0.5f));
        }
        Add(d, Burst(ref r, 0.05f, 60f, 500f, 120f), r.R(0f, 0.05f), 0.35f);
        Norm(d, 0.45f); FadeOut(d, 0.03f); return d;
    }

    static float[] GenJump(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.3f)];
        Add(d, Burst(ref r, 0.07f, 70f, 900f, 120f), 0f, 0.8f);                       // push off
        Add(d, Burst(ref r, 0.06f, 50f, 6000f, 2200f, 0.008f), 0f, 0.2f);
        Add(d, Burst(ref r, 0.26f, 10f, 2200f, 500f, 0.06f), 0.01f, 0.5f);            // cloth
        Add(d, GenGear(seed + 1), 0.03f, 0.6f);
        Norm(d, 0.5f); FadeOut(d, 0.04f); return d;
    }

    // both boots and thirty kilos of kit arriving (or a man going down)
    static float[] GenLand(int seed, bool body)
    {
        var r = new Rng(seed); var d = new float[N(body ? 0.75f : 0.45f)];
        Add(d, Sweep(0.16f, r.R(100f, 120f), 52f, 25f, body ? 22f : 30f), 0f, 1f);
        Add(d, Burst(ref r, 0.13f, 42f, 520f, 60f), 0f, 0.9f);
        Add(d, Burst(ref r, 0.07f, 60f, 5000f, 1800f, 0.006f), 0f, 0.2f);
        Add(d, GenGear(seed + 3), 0.025f, 0.8f);
        if (body)
        {
            Add(d, Burst(ref r, 0.2f, 22f, 420f, 55f, 0.01f), r.R(0.16f, 0.22f), 0.8f);   // the rest of him
            Add(d, GenGear(seed + 5), 0.2f, 0.7f);
            Add(d, Ping(ref r, 0.15f, r.R(900f, 1200f), 60f, 4, 0.8f), r.R(0.28f, 0.36f), 0.3f);   // the rifle on the ground
        }
        Norm(d, 0.75f); FadeOut(d, 0.05f); return d;
    }

    static float[] GenNadeBounce(int seed)
    {
        var r = new Rng(seed); var d = new float[N(0.3f)];
        Add(d, Ping(ref r, 0.25f, r.R(620f, 760f), 48f, 4, 0.8f), 0f, 0.9f);
        Add(d, Burst(ref r, 0.02f, 400f, 5000f, 600f), 0f, 0.6f);
        Add(d, Burst(ref r, 0.07f, 55f, 600f, 90f), 0f, 0.6f);
        Norm(d, 0.7f); FadeOut(d, 0.03f); return d;
    }

    // ---------- explosions ----------

    static float[] GenExplosion(int seed)
    {
        var r = new Rng(seed);
        var dry = new float[N(1.1f)];
        Add(dry, Burst(ref r, 0.05f, 190f, 8000f, 700f), 0f, 1f);                      // the crack of the charge
        Add(dry, Burst(ref r, 0.45f, 13f, 1700f, 140f), 0.0005f, 0.8f);                // blast
        Add(dry, Sweep(1.0f, r.R(100f, 120f), 30f, 9f, 4.2f), 0f, 1.1f);               // the boom you feel
        Add(dry, Sweep(0.8f, 68f, 27f, 6f, 5.5f), 0.02f, 0.7f);
        Add(dry, Burst(ref r, 1.0f, 4.5f, 210f, 25f), 0.001f, 0.9f);                   // rumble
        Drive(dry, 3.2f);
        const float len = 3.4f; var d = new float[N(len)];
        Add(d, dry, 0, 1f);
        float t = r.R(0.1f, 0.14f), g = 0.3f;
        for (int k = 0; k < 6; k++) { Add(d, Filtered(dry, 1800f / (1f + k * 0.6f)), t, g * r.R(0.8f, 1.2f)); t += r.R(0.09f, 0.2f) + k * 0.04f; g *= 0.72f; }
        Add(d, Burst(ref r, len - 0.1f, 1.8f, 850f, 55f, 0.05f), 0.04f, 0.3f);
        Patter(d, ref r, 0.3f, 1.9f, 46, 0.11f, 0.02f, 5500f, 1300f);                  // earth and stone coming back down
        Patter(d, ref r, 0.25f, 1.2f, 10, 0.1f, 0.03f, 900f, 150f);                    // the heavier lumps
        Norm(d, 0.98f); FadeOut(d, 0.25f);
        return d;
    }

    // ears ringing after a close one
    static float[] GenRing()
    {
        int n = N(3.2f); var d = new float[n]; float dtS = 1f / sr;
        for (int i = 0; i < n; i++)
        {
            float t = i * dtS, env = (1f - Mathf.Exp(-t * 14f)) * Mathf.Exp(-t * 1.25f);
            d[i] = (Mathf.Sin(t * 3150f * 6.2832f) * 0.6f + Mathf.Sin(t * 4020f * 6.2832f) * 0.3f + Mathf.Sin(t * 6100f * 6.2832f) * 0.1f) * env;
        }
        Norm(d, 0.5f); FadeOut(d, 0.3f); return d;
    }

    // ---------- the place ----------

    // wind off the sea along the corniche: gusts that open and close a band of noise, a faint whistle on top
    static float[] GenWind(int seed)
    {
        var r = new Rng(seed); const float loop = 12f; int n = N(loop), x = N(1.5f); var ext = new float[n + x];
        float l1 = 0, l2 = 0, low = 0, band = 0, dtS = 1f / sr;
        for (int i = 0; i < n + x; i++)
        {
            float u = i * dtS / loop * 6.2832f;                                        // whole cycles per loop, so the gusts join up
            float m = Mathf.Clamp01(0.5f + 0.25f * Mathf.Sin(u * 2f + 1f) + 0.16f * Mathf.Sin(u * 3f + 2.2f) + 0.1f * Mathf.Sin(u * 7f));
            float a = 6.2832f * (170f + 650f * m) / sr, s = r.Next();
            l1 += (s - l1) * a; l2 += (l1 - l2) * a;
            float f = 2f * Mathf.Sin(3.14159f * (480f + 220f * m) / sr);
            low += f * band; float high = s - low - 0.06f * band; band += f * high;
            ext[i] = l2 / Mathf.Sqrt(a) * (0.3f + 0.7f * m) + band * 0.012f * m * m;
        }
        var d = MakeLoop(ext, n); Norm(d, 0.8f); return d;
    }

    // surf on the beach and the breakwater: swells that build, break and hiss away, never two the same
    static float[] GenSurf(int seed)
    {
        var r = new Rng(seed); const float loop = 14f; int n = N(loop), x = N(1.5f); var ext = new float[n + x];
        float a1 = 0, a2 = 0, b1 = 0, b2 = 0, c1 = 0, dtS = 1f / sr;
        for (int i = 0; i < n + x; i++)
        {
            float t = i * dtS;
            float e1 = Wave(t / (loop / 2f)), e2 = Wave(t / (loop / 3f) + 0.37f) * 0.6f;
            float s1 = r.Next(), s2 = r.Next();
            float ka = 6.2832f * (260f + 3000f * e1) / sr; a1 += (s1 - a1) * ka; a2 += (a1 - a2) * ka;
            float kb = 6.2832f * (220f + 2400f * e2) / sr; b1 += (s2 - b1) * kb; b2 += (b1 - b2) * kb;
            c1 += (s1 - c1) * 0.02f;                                                   // the sea itself, low
            ext[i] = a2 / Mathf.Sqrt(ka) * (0.06f + e1) + b2 / Mathf.Sqrt(kb) * (0.04f + e2) + c1 * 1.2f;
        }
        var d = MakeLoop(ext, n); Norm(d, 0.8f); return d;
    }

    // one wave over p = 0..1: draws up slowly, breaks, washes out
    static float Wave(float p)
    {
        p -= Mathf.Floor(p);
        if (p < 0.38f) { float u = p / 0.38f; return 0.45f * u * u; }
        if (p < 0.44f) return Mathf.Lerp(0.45f, 1f, (p - 0.38f) / 0.06f);
        return Mathf.Exp(-(p - 0.44f) * 6.5f);
    }

    // somebody else's fight, a kilometre off: a burst (or two guns answering each other) with the city's echo on it
    static float[] GenFarBurst(int seed, int kind)
    {
        var r = new Rng(seed); var d = new float[N(3.4f)];
        var shot = new float[N(0.7f)];
        Add(shot, Burst(ref r, 0.3f, 14f, 500f, 60f, 0.004f), 0f, 1f);
        Add(shot, Sweep(0.25f, 110f, 48f, 22f, 16f), 0f, 0.7f);
        Add(shot, Burst(ref r, 0.65f, 6f, 700f, 120f, 0.02f), 0.01f, 0.5f);
        int groups = kind == 3 ? 2 : 1; float t = 0.02f;
        for (int gI = 0; gI < groups; gI++)
        {
            int shots = kind == 4 ? 1 + (int)(r.U() * 2) : 3 + (int)(r.U() * 5);
            float gap = kind == 4 ? r.R(0.35f, 0.6f) : r.R(0.092f, 0.11f), gg = gI == 0 ? 1f : r.R(0.4f, 0.7f);
            for (int k = 0; k < shots && t < 2.2f; k++) { Add(d, shot, t, gg * r.R(0.75f, 1f)); t += gap * r.R(0.93f, 1.07f); }
            t += r.R(0.25f, 0.5f);
        }
        var echo = Filtered(d, 380f);
        Add(d, echo, r.R(0.3f, 0.45f), 0.4f); Add(d, echo, r.R(0.6f, 0.8f), 0.2f);
        Add(d, Burst(ref r, 3.2f, 1.7f, 450f, 80f, 0.35f), 0.05f, 0.2f);
        LP(d, kind == 2 ? 500f : 850f, 1);
        Norm(d, 0.9f); FadeOut(d, 0.4f);
        return d;
    }

    // artillery or a car bomb somewhere across the city: no crack left, only the ground-roll
    static float[] GenFarBoom(int seed)
    {
        var r = new Rng(seed); var d = new float[N(4.6f)];
        Add(d, Sweep(1.6f, r.R(55f, 65f), 27f, 3f, 2.2f), 0f, 1f);
        Add(d, Burst(ref r, 2.6f, 1.8f, 180f, 20f, 0.06f), 0f, 1f);
        Add(d, Burst(ref r, 4.3f, 1.1f, 300f, 40f, 0.4f), 0.1f, 0.4f);
        Add(d, Filtered(d, 200f), r.R(0.5f, 0.8f), 0.35f);
        LP(d, 420f, 1);
        Norm(d, 0.9f); FadeOut(d, 0.6f);
        return d;
    }
}

// drives CombatAudio once a frame (created by it, never placed in a scene)
public class CombatAudioRunner : MonoBehaviour
{
    void Update() { CombatAudio.Tick(); }
}
