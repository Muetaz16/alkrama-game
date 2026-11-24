using System.Collections.Generic;
using UnityEngine;

// What a round or a grenade lands on. Asphalt and Concrete look alike but sound different under a boot.
public enum Surface { Concrete, Asphalt, Sand, Water, Metal, Wood, Flesh }

// Works out the surface from what is there already: the material and object names the generators gave things
// ("Seafront_Sand", "Detail_CarPaint", "roads" ...), the sub-mesh that was hit, and the water line.
// The answer is kept per collider, so after the first look-up it costs a dictionary read.
public static class CombatSurface
{
    public const float WaterLevel = -0.22f;                 // same as PlayerHealth.waterLevel
    static readonly Dictionary<Collider, Surface[]> cache = new();
    static readonly List<Material> mats = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cache.Clear(); }

    public static Surface Of(RaycastHit h) => Of(h.collider, h.triangleIndex, h.point);

    public static Surface Of(Collider c, int triangle, Vector3 point)
    {
        if (!c) return Surface.Concrete;
        if (!cache.TryGetValue(c, out var arr)) { arr = Build(c); cache[c] = arr; }
        if (arr[0] == Surface.Flesh) return Surface.Flesh;
        if (point.y < WaterLevel - 0.03f) return Surface.Water;         // the sea bed: the round went in through the surface
        if (arr.Length == 1 || triangle < 0) return arr[0];
        var mesh = c is MeshCollider mc ? mc.sharedMesh : null;
        if (!mesh || mesh.subMeshCount != arr.Length) return arr[0];
        int idx = triangle * 3;
        for (int s = 0; s < arr.Length; s++) { var sm = mesh.GetSubMesh(s); if (idx >= sm.indexStart && idx < sm.indexStart + sm.indexCount) return arr[s]; }
        return arr[0];
    }

    /// <summary>What is under a pair of boots standing at pos (one short ray).</summary>
    public static Surface Under(Vector3 pos)
    {
        if (pos.y < WaterLevel - 0.05f) return Surface.Water;
        if (Physics.Raycast(pos + Vector3.up * 0.35f, Vector3.down, out var h, 1.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var s = Of(h);
            return s == Surface.Flesh ? Surface.Concrete : s;
        }
        return Surface.Concrete;
    }

    static Surface[] Build(Collider c)
    {
        if (c.GetComponentInParent<EnemyAI>() || c.GetComponentInParent<PlayerController>()) return new[] { Surface.Flesh };
        var r = c.GetComponent<Renderer>(); if (!r) r = c.GetComponentInChildren<Renderer>(); if (!r) r = c.GetComponentInParent<Renderer>();
        string obj = c.name + " " + (c.transform.parent ? c.transform.parent.name : "");
        var byName = Classify(obj, out bool sure);
        mats.Clear(); if (r) r.GetSharedMaterials(mats);
        if (mats.Count == 0) return new[] { byName };
        var arr = new Surface[c is MeshCollider ? mats.Count : 1];
        for (int i = 0; i < arr.Length; i++)
        {
            var m = mats[i]; var s = m ? Classify(m.name, out bool mSure) : Classify("", out mSure);
            arr[i] = mSure ? s : byName;
        }
        mats.Clear();
        return arr;
    }

    static bool Has(string s, string key) => s.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0;

    static Surface Classify(string n, out bool sure)
    {
        sure = true;
        if (Has(n, "water")) return Surface.Water;
        if (Has(n, "sand") || Has(n, "beach") || Has(n, "sea floor") || Has(n, "grass")) return Surface.Sand;      // sandbags too
        if (Has(n, "palm") || Has(n, "frond") || Has(n, "bark") || Has(n, "wood") || Has(n, "shrub") || Has(n, "trunk") || Has(n, "dates") || Has(n, "stalk") || Has(n, "tyre")) return Surface.Wood;
        if (Has(n, "car") || Has(n, "steel") || Has(n, "metal") || Has(n, "rust") || Has(n, "chrome") || Has(n, "_rim") || Has(n, "drum") || Has(n, "shutter") || Has(n, "lamp")
            || Has(n, "dish") || Has(n, "rebar") || Has(n, "manhole") || Has(n, "plate") || Has(n, "target") || Has(n, "sign") || Has(n, "wire") || Has(n, "burnt") || Has(n, "pole")) return Surface.Metal;
        if (Has(n, "road") || Has(n, "asphalt") || Has(n, "tarmac") || Has(n, "patch") || Has(n, "crater")) return Surface.Asphalt;
        if (Has(n, "facade") || Has(n, "kerb") || Has(n, "sidewalk") || Has(n, "promenade") || Has(n, "wall") || Has(n, "rubble") || Has(n, "rock") || Has(n, "barrier") || Has(n, "balustrade") || Has(n, "lighthouse")) return Surface.Concrete;
        sure = false; return Surface.Concrete;
    }
}

// Everything you see when weapons are used, built in code: muzzle flash, light and smoke, tracers that fly as
// streaks, impacts by surface (dust and falling chips on stone, sparks on steel, sand, water, a red mist on a
// man), smoke off the spent cases, grenade explosions (flash, fireball, black smoke, earth, dust ring, a
// smoke column, a scorch mark) and the camera shake that goes with all of it.
//
// Five shared particle systems, a fixed pool of tracers and lights; nothing is created or destroyed while
// playing. Only the "Sprites/Default" shader is used (always in the build); if no usable shader is found the
// effects switch themselves off rather than draw magenta.
public static class CombatFx
{
    // ---------- tunables ----------
    public static int PlayerTracerEvery = 3;        // one visible tracer in this many rounds (sniper: every round, unless scoped)
    public static int EnemyTracerEvery = 2;
    public static float TracerSpeed = 420f;         // m/s on screen (slower than the round so the eye catches it)
    public static float TracerLength = 11f;         // metres of streak
    public static float ShakeScale = 1f;            // 0 = no camera shake at all
    public static float FireShakeAk = 0.10f, FireShakeSniper = 0.38f;   // degrees, on top of the recoil the weapon already applies
    public static float HitShake = 0.9f;            // degrees when the player takes a round
    public static float BlastShake = 3.4f, BlastShakeRange = 45f;       // degrees right next to a grenade; no shake beyond the range
    public static float MuzzleSmoke = 1f;           // 0 = none
    public static float FxRange = 160f;             // impacts further than this from the camera are not drawn
    const int MaxTracers = 20, MaxLights = 3, MaxWisps = 8, MaxColumns = 4, MaxScorch = 6;

    static readonly Vector3 Wind = new Vector3(0.35f, 0.05f, 0.12f);
    static readonly Color32 StoneDust = new(196, 182, 158, 150), AsphaltDust = new(118, 114, 108, 150), SandDust = new(218, 196, 148, 170),
        WoodDust = new(128, 104, 74, 150), Mist = new(232, 240, 246, 150), GunSmoke = new(170, 170, 170, 46), DarkSmoke = new(44, 41, 38, 205),
        BloodMist = new(98, 7, 7, 165), Earth = new(74, 62, 48, 255), White = new(255, 255, 255, 255);

    static CombatFxRunner runner; static bool failed;
    static ParticleSystem soft, chunk, spark, flash, fire;
    static ParticleSystem.EmitParams ep;
    static Material softMat, tracerMat;
    // tracers
    static LineRenderer[] trLine; static readonly Vector3[] trStart = new Vector3[MaxTracers], trDir = new Vector3[MaxTracers];
    static readonly float[] trLen = new float[MaxTracers], trHead = new float[MaxTracers]; static readonly bool[] trOn = new bool[MaxTracers]; static int trNext, playerShots, enemyShots;
    // lights (0 = the player's muzzle, 1-2 = blasts)
    static Light[] lights; static readonly float[] ltPeak = new float[MaxLights], ltLife = new float[MaxLights], ltT = new float[MaxLights]; static int blastLight;
    // smoke off spent cases, smoke columns after a blast, scorch marks
    static readonly Transform[] wisp = new Transform[MaxWisps]; static readonly float[] wispUntil = new float[MaxWisps], wispNext = new float[MaxWisps]; static int wispI;
    static readonly Vector3[] colPos = new Vector3[MaxColumns]; static readonly float[] colStart = new float[MaxColumns], colNext = new float[MaxColumns]; static int colI;
    static Transform[] scorch; static int scorchI;
    // shake
    static ThirdPersonCamera rig; static float rigCheck, shakeAmp, shakeRate, heat; static Vector2 shakePrev;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        runner = null; failed = false; soft = chunk = spark = flash = fire = null; trLine = null; lights = null; scorch = null; rig = null;
        shakeAmp = 0; shakePrev = Vector2.zero; heat = 0; playerShots = enemyShots = 0;
        for (int i = 0; i < MaxTracers; i++) trOn[i] = false;
        for (int i = 0; i < MaxWisps; i++) { wisp[i] = null; wispUntil[i] = 0; }
        for (int i = 0; i < MaxColumns; i++) colStart[i] = -99;
    }

    static bool Ready()
    {
        if (runner) return true;
        if (failed || !Application.isPlaying) return false;
        try { Build(); }
        catch (System.Exception e) { failed = true; Debug.LogWarning("CombatFx off: " + e.Message); }
        return runner;
    }

    // ============================================================ public API ============================================================

    /// <summary>Muzzle blast: a hot core and a few sparks, a brief light (player only), a wisp of smoke that
    /// builds up while the trigger is held. The view model / enemy keep their own flash quads; this adds to them.</summary>
    public static void Muzzle(Vector3 pos, Vector3 dir, bool heavy, bool isPlayer)
    {
        if (!Ready()) return;
        float k = heavy ? 1.6f : 1f;
        if (!isPlayer)
        {
            var cam = Camera.main; if (cam && (cam.transform.position - pos).sqrMagnitude > 110f * 110f) return;
            Emit(flash, pos + dir * 0.08f, dir * 2f, Random.Range(0.3f, 0.5f), Random.Range(0.03f, 0.05f), White);
            if (Random.value < 0.5f) Emit(soft, pos + dir * 0.15f, dir * Random.Range(0.5f, 1.4f) + Wind, Random.Range(0.14f, 0.24f), Random.Range(0.8f, 1.5f), GunSmoke);
            return;
        }
        Emit(flash, pos + dir * 0.06f, dir * 3f, Random.Range(0.1f, 0.2f) * k, Random.Range(0.025f, 0.045f), White);
        if (Random.value < 0.4f) Emit(flash, pos + dir * Random.Range(0.12f, 0.22f), dir * 5f, Random.Range(0.06f, 0.12f) * k, 0.03f, new Color32(255, 210, 150, 255));
        int sparks = Random.value < (heavy ? 0.9f : 0.35f) ? Random.Range(2, heavy ? 7 : 4) : 0;
        for (int i = 0; i < sparks; i++) Emit(spark, pos, Cone(dir, 0.35f) * Random.Range(6f, 16f), Random.Range(0.008f, 0.016f), Random.Range(0.05f, 0.14f), White);
        LightUp(0, pos + dir * 0.2f, (heavy ? 9f : 5.5f) * Random.Range(0.8f, 1.25f), heavy ? 9f : 6.5f, heavy ? 0.075f : 0.05f, new Color(1f, Random.Range(0.68f, 0.8f), Random.Range(0.36f, 0.48f)));
        // smoke: a little each round, more as the barrel heats up in a long burst
        heat = Mathf.Min(14f, heat + (heavy ? 4f : 1f));
        int puffs = MuzzleSmoke <= 0 ? 0 : heavy ? 3 : heat > 9f ? 2 : heat > 3f || Random.value < 0.5f ? 1 : 0;
        for (int i = 0; i < puffs; i++)
        {
            var c = GunSmoke; c.a = (byte)Mathf.Clamp(c.a * MuzzleSmoke * Mathf.Lerp(0.7f, 1.5f, heat / 14f), 0, 255);
            Emit(soft, pos + dir * Random.Range(0.1f, 0.35f), dir * Random.Range(0.6f, 2.2f) + Vector3.up * Random.Range(0.1f, 0.4f) + Wind, Random.Range(0.07f, 0.15f) * k, Random.Range(0.9f, 1.9f), c);
        }
        Shake(heavy ? FireShakeSniper : FireShakeAk, heavy ? 0.16f : 0.08f);
    }

    /// <summary>A tracer from the muzzle to where the round stopped: a short bright streak that flies there.
    /// Only some rounds carry one (PlayerTracerEvery / EnemyTracerEvery); pass force to show this one regardless.</summary>
    public static void Tracer(Vector3 from, Vector3 to, bool isPlayer, bool force = false)
    {
        if (!Ready() || trLine == null) return;
        int every = Mathf.Max(1, isPlayer ? PlayerTracerEvery : EnemyTracerEvery);
        int n = isPlayer ? playerShots++ : enemyShots++;
        if (!force && n % every != 0) return;
        var d = to - from; float len = d.magnitude; float skip = isPlayer ? 1.5f : 0.4f;
        if (len < skip + 3f) return;
        int i = trNext; for (int k = 0; k < MaxTracers; k++) { int j = (trNext + k) % MaxTracers; if (!trOn[j]) { i = j; break; } }
        trNext = (i + 1) % MaxTracers;
        d /= len; trStart[i] = from + d * skip; trDir[i] = d; trLen[i] = len - skip; trHead[i] = Random.Range(0f, 3f); trOn[i] = true;
        var lr = trLine[i];
        lr.startColor = isPlayer ? new Color(1f, 0.55f, 0.25f, 0f) : new Color(1f, 0.45f, 0.2f, 0f);
        lr.endColor = isPlayer ? new Color(1f, 0.85f, 0.6f, 1f) : new Color(1f, 0.72f, 0.45f, 1f);
        MoveTracer(i, 0f);
        lr.enabled = true;
    }

    /// <summary>A round striking something: looks up the surface, draws the right burst and plays the right sound.
    /// dir is the way the round was flying.</summary>
    public static Surface Impact(RaycastHit h, Vector3 dir, bool heavy = false, bool silent = false)
    {
        var s = CombatSurface.Of(h);
        Vector3 p = h.point, n = h.normal;
        if (s == Surface.Water)
        {
            // it went in through the surface: the splash is where it crossed the water line
            if (dir.y < -0.02f) p -= dir * ((p.y - CombatSurface.WaterLevel) / dir.y); else p.y = CombatSurface.WaterLevel;
            n = Vector3.up;
        }
        Impact(s, p, n, dir, heavy, silent);
        return s;
    }

    public static void Impact(Surface s, Vector3 p, Vector3 n, Vector3 dir, bool heavy = false, bool silent = false)
    {
        if (!silent) CombatAudio.Impact(s, p, heavy);
        if (!Ready()) return;
        var cam = Camera.main; if (cam && (cam.transform.position - p).sqrMagnitude > FxRange * FxRange) return;
        float k = heavy ? 1.8f : 1f; p += n * 0.03f;
        var bounce = Vector3.Reflect(dir, n); var spray = (n * 1.4f + bounce * 0.6f).normalized;
        switch (s)
        {
            case Surface.Flesh: BloodPuff(p, dir, k); break;
            case Surface.Metal:
                for (int i = 0, c = (int)(Random.Range(9, 16) * k); i < c; i++) Emit(spark, p, Cone(spray, 0.75f) * Random.Range(3f, 13f), Random.Range(0.01f, 0.022f), Random.Range(0.08f, 0.32f), White);
                Emit(flash, p, Vector3.zero, Random.Range(0.1f, 0.18f) * k, 0.035f, new Color32(255, 225, 170, 255));
                for (int i = 0; i < 3; i++) Emit(soft, p, Cone(n, 0.6f) * Random.Range(0.4f, 1.6f), Random.Range(0.08f, 0.2f), Random.Range(0.4f, 0.9f), new Color32(120, 118, 114, 90));
                break;
            case Surface.Water:
                for (int i = 0, c = (int)(Random.Range(12, 18) * k); i < c; i++) Emit(chunk, p, Cone(Vector3.up, 0.35f) * Random.Range(2f, 5.5f) * k, Random.Range(0.02f, 0.05f), Random.Range(0.4f, 0.8f), Mist);
                for (int i = 0; i < 5; i++) Emit(soft, p + Vector3.up * 0.1f, Cone(Vector3.up, 0.5f) * Random.Range(0.8f, 2.4f), Random.Range(0.18f, 0.4f) * k, Random.Range(0.35f, 0.7f), Mist);
                break;
            case Surface.Sand:
                for (int i = 0, c = (int)(Random.Range(8, 12) * k); i < c; i++) Emit(soft, p, Cone(spray, 0.5f) * Random.Range(1.5f, 5f), Random.Range(0.1f, 0.3f) * k, Random.Range(0.5f, 1.3f), SandDust);
                for (int i = 0, c = (int)(Random.Range(10, 16) * k); i < c; i++) Emit(chunk, p, Cone(spray, 0.6f) * Random.Range(2f, 6f), Random.Range(0.008f, 0.02f), Random.Range(0.4f, 0.9f), new Color32(205, 182, 135, 255));
                Emit(soft, p, n * 0.4f + Wind, Random.Range(0.35f, 0.6f) * k, Random.Range(1.4f, 2.4f), new Color32(218, 196, 148, 70));
                break;
            case Surface.Wood:
                for (int i = 0; i < 4; i++) Emit(soft, p, Cone(spray, 0.5f) * Random.Range(0.8f, 2.5f), Random.Range(0.08f, 0.2f) * k, Random.Range(0.4f, 0.9f), WoodDust);
                for (int i = 0, c = (int)(Random.Range(5, 9) * k); i < c; i++) Emit(chunk, p, Cone(spray, 0.7f) * Random.Range(1.5f, 5f), Random.Range(0.012f, 0.035f), Random.Range(0.4f, 0.9f), new Color32(150, 120, 82, 255));
                break;
            default:
                var dust = s == Surface.Asphalt ? AsphaltDust : StoneDust;
                for (int i = 0, c = (int)(Random.Range(6, 10) * k); i < c; i++) Emit(soft, p, Cone(spray, 0.45f) * Random.Range(1f, 4.5f), Random.Range(0.1f, 0.32f) * k, Random.Range(0.45f, 1.2f), dust);
                var chip = s == Surface.Asphalt ? new Color32(70, 68, 66, 255) : new Color32(168, 156, 136, 255);
                for (int i = 0, c = (int)(Random.Range(8, 14) * k); i < c; i++) Emit(chunk, p, Cone(spray, 0.75f) * Random.Range(1.5f, 6.5f), Random.Range(0.012f, 0.045f) * k, Random.Range(0.5f, 1.1f), chip);
                dust.a = 70;                                                   // the puff that hangs there for a couple of seconds
                Emit(soft, p + n * 0.1f, n * 0.5f + Wind, Random.Range(0.4f, 0.65f) * k, Random.Range(1.5f, 2.6f), dust);
                if (Random.value < 0.2f) for (int i = 0; i < 3; i++) Emit(spark, p, Cone(spray, 0.6f) * Random.Range(3f, 9f), 0.012f, Random.Range(0.06f, 0.18f), White);
                break;
        }
    }

    /// <summary>A man hit: a brief dark red mist where the round went in and out. Nothing more than that.</summary>
    public static void Blood(Vector3 point, Vector3 dir)
    {
        if (!Ready()) return;
        var cam = Camera.main; if (cam && (cam.transform.position - point).sqrMagnitude > FxRange * FxRange) return;
        BloodPuff(point, dir, 1f);
    }

    static void BloodPuff(Vector3 p, Vector3 dir, float k)
    {
        for (int i = 0; i < 4; i++) Emit(soft, p, Cone(-dir, 0.7f) * Random.Range(0.4f, 1.4f), Random.Range(0.07f, 0.16f) * k, Random.Range(0.22f, 0.45f), BloodMist);
        for (int i = 0; i < 3; i++) Emit(soft, p + dir * 0.25f, Cone(dir, 0.45f) * Random.Range(1f, 3f), Random.Range(0.08f, 0.2f) * k, Random.Range(0.25f, 0.5f), BloodMist);
        for (int i = 0; i < 5; i++) Emit(chunk, p + dir * 0.2f, Cone(dir, 0.5f) * Random.Range(1.5f, 4f), Random.Range(0.008f, 0.018f), Random.Range(0.3f, 0.6f), new Color32(80, 4, 4, 255));
        for (int i = 0; i < 2; i++) Emit(soft, p, Cone(-dir, 0.8f) * Random.Range(0.3f, 1f), Random.Range(0.06f, 0.12f), Random.Range(0.3f, 0.6f), new Color32(150, 140, 120, 80));   // dust off the kit
    }

    /// <summary>A faint trail of smoke off a spent case for the first third of a second of its flight.</summary>
    public static void ShellSmoke(Transform shell)
    {
        if (!Ready() || !shell) return;
        wisp[wispI] = shell; wispUntil[wispI] = Time.time + 0.32f; wispNext[wispI] = Time.time + 0.02f; wispI = (wispI + 1) % MaxWisps;
    }

    /// <summary>A grenade going off (the sound is CombatAudio.Explosion).</summary>
    public static void Explosion(Vector3 p)
    {
        if (!Ready()) return;
        var cam = Camera.main; float camD = cam ? Vector3.Distance(cam.transform.position, p) : 0f;
        Shake(camD < BlastShakeRange ? BlastShake * Mathf.Pow(1f - camD / BlastShakeRange, 2f) : 0f, 0.55f);
        if (camD > 300f) return;
        Vector3 g = p, n = Vector3.up; bool grounded = false;
        if (Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out var h, 2.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) { g = h.point; n = h.normal; grounded = true; }
        bool water = g.y < CombatSurface.WaterLevel;
        if (water) g.y = CombatSurface.WaterLevel;

        Emit(fire, p + Vector3.up * 0.4f, Vector3.zero, 6f, 0.09f, White);                                        // the flash
        LightUp(1 + blastLight, p + Vector3.up * 0.7f, 26f, 20f, 0.28f, new Color(1f, 0.66f, 0.32f)); blastLight = (blastLight + 1) % (MaxLights - 1);
        for (int i = 0; i < 14; i++)                                                                               // fireball
            Emit(fire, p + Random.insideUnitSphere * 0.5f + Vector3.up * 0.5f, Cone(Vector3.up, 0.9f) * Random.Range(1.5f, 7f), Random.Range(1.2f, 2.6f), Random.Range(0.28f, 0.6f), new Color32(255, (byte)Random.Range(200, 255), 170, 255));
        for (int i = 0; i < 22; i++)                                                                               // ... that turns to black smoke
            Emit(soft, p + Random.insideUnitSphere * 0.8f + Vector3.up * 0.7f, (Random.insideUnitSphere + Vector3.up * 1.2f) * Random.Range(1.5f, 5f), Random.Range(1.6f, 3.2f), Random.Range(2.2f, 4.6f), DarkSmoke);
        var ringCol = water ? Mist : new Color32(190, 174, 148, 135);
        for (int i = 0; i < 26; i++)                                                                               // dust rolling out along the ground
        {
            float a = (i + Random.value) / 26f * 6.2832f; var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            Emit(soft, g + dir * 0.4f + Vector3.up * 0.15f, dir * Random.Range(6f, 11f) + Vector3.up * 0.4f, Random.Range(1f, 1.8f), Random.Range(1.2f, 2.3f), ringCol);
        }
        var lump = water ? Mist : Earth;
        for (int i = 0; i < 70; i++)                                                                               // earth and stones thrown up, falling back
            Emit(chunk, g + Vector3.up * 0.2f, Cone(Vector3.up, 0.65f) * Random.Range(4f, 13f), Random.Range(0.03f, 0.11f), Random.Range(0.9f, 1.9f), lump);
        for (int i = 0; i < 24; i++) Emit(spark, p + Vector3.up * 0.3f, Cone(Vector3.up, 1.1f) * Random.Range(8f, 22f), Random.Range(0.02f, 0.04f), Random.Range(0.2f, 0.5f), White);
        colPos[colI] = g; colStart[colI] = Time.time; colNext[colI] = Time.time + 0.15f; colI = (colI + 1) % MaxColumns;
        if (grounded && !water && scorch != null)
        {
            var q = scorch[scorchI]; scorchI = (scorchI + 1) % MaxScorch;
            q.SetPositionAndRotation(g + n * 0.02f, Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)));
            q.localScale = Vector3.one * Random.Range(3f, 4.2f); q.gameObject.SetActive(true);
        }
    }

    /// <summary>The player takes a round: the view jolts and he hears it land.</summary>
    public static void PlayerHit(Vector3 from)
    {
        Shake(HitShake, 0.22f);
        CombatAudio.PlayerHurt();
    }

    /// <summary>Shake the view: degrees at the peak, dying away over about `seconds`. Zero-mean (the view comes
    /// back to where it was). Not applied while the camera is driven by a script (the automated test).</summary>
    public static void Shake(float degrees, float seconds)
    {
        degrees *= ShakeScale;
        if (degrees <= 0.001f || degrees < shakeAmp || !Ready()) return;
        shakeAmp = degrees; shakeRate = 3f / Mathf.Max(0.03f, seconds);
    }

    // ============================================================ every frame ============================================================

    internal static void Tick()
    {
        float now = Time.time, dt = Time.deltaTime;
        heat = Mathf.Max(0, heat - dt * 2.5f);

        for (int i = 0; i < MaxLights; i++)
        {
            if (ltT[i] >= ltLife[i]) continue;
            ltT[i] += dt; float u = ltT[i] / ltLife[i];
            if (u >= 1f) { lights[i].enabled = false; continue; }
            lights[i].intensity = ltPeak[i] * (1f - u) * (1f - u);
        }

        for (int i = 0; i < MaxTracers; i++) if (trOn[i]) MoveTracer(i, dt);

        for (int i = 0; i < MaxWisps; i++)
        {
            if (now > wispUntil[i] || now < wispNext[i]) continue;
            if (!wisp[i] || !wisp[i].gameObject.activeInHierarchy) { wispUntil[i] = 0; continue; }
            wispNext[i] = now + 0.035f;
            Emit(soft, wisp[i].position, Vector3.up * 0.15f + Wind * 0.5f, Random.Range(0.025f, 0.05f), Random.Range(0.45f, 0.9f), new Color32(180, 180, 180, 38));
        }

        for (int i = 0; i < MaxColumns; i++)
        {
            float age = now - colStart[i]; if (age > 3f || now < colNext[i]) continue;
            colNext[i] = now + 0.075f; float k = age / 3f;
            byte grey = (byte)Mathf.Lerp(46, 120, k);
            Emit(soft, colPos[i] + Random.insideUnitSphere * 0.5f + Vector3.up * 0.4f, Vector3.up * Random.Range(1.4f, 2.8f) + Wind * 2f, Random.Range(1.2f, 2.2f), Random.Range(2.6f, 4.2f), new Color32(grey, grey, grey, (byte)Mathf.Lerp(170, 60, k)));
        }

        // shake: a fast wobble added to the look angles as a difference, so it sums to nothing
        if (shakeAmp > 0 || shakePrev != Vector2.zero)
        {
            if (!rig && now >= rigCheck) { rigCheck = now + 1f; rig = Object.FindAnyObjectByType<ThirdPersonCamera>(); }
            shakeAmp *= Mathf.Exp(-shakeRate * dt); if (shakeAmp < 0.004f) shakeAmp = 0;
            var off = shakeAmp * new Vector2(Mathf.Sin(now * 131f) * 0.6f + Mathf.Sin(now * 83f + 1.3f) * 0.4f, Mathf.Sin(now * 149f + 0.7f) * 0.6f + Mathf.Sin(now * 97f + 2.1f) * 0.4f);
            if (rig && !rig.externalInput) rig.lookDelta += off - shakePrev;
            shakePrev = off;
        }
    }

    static void MoveTracer(int i, float dt)
    {
        trHead[i] += TracerSpeed * dt;
        float a = Mathf.Max(0, trHead[i] - TracerLength), b = Mathf.Min(trHead[i], trLen[i]);
        var lr = trLine[i];
        if (a >= trLen[i]) { trOn[i] = false; lr.enabled = false; return; }
        Vector3 tail = trStart[i] + trDir[i] * a, head = trStart[i] + trDir[i] * Mathf.Max(b, a + 0.05f);
        lr.SetPosition(0, tail); lr.SetPosition(1, head);
        // wide enough to stay a pixel or two across at any range, never a rod up close
        var cam = Camera.main; float d = cam ? Vector3.Distance(cam.transform.position, head) : 30f;
        float w = Mathf.Clamp(d * 0.0022f, 0.02f, 0.3f);
        lr.startWidth = w * 0.35f; lr.endWidth = w;
    }

    static void LightUp(int i, Vector3 pos, float intensity, float range, float life, Color c)
    {
        if (lights == null) return;
        var l = lights[i]; l.transform.position = pos; l.color = c; l.range = range; l.intensity = intensity; l.enabled = true;
        ltPeak[i] = intensity; ltLife[i] = life; ltT[i] = 0;
    }

    static Vector3 Cone(Vector3 axis, float spread) => (axis + Random.insideUnitSphere * spread).normalized;

    static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color32 c)
    {
        if (!ps) return;
        ep.position = pos; ep.velocity = vel; ep.startSize = size; ep.startLifetime = life; ep.startColor = c;
        ep.rotation = Random.value * 360f; ep.angularVelocity = Random.Range(-70f, 70f);
        ps.Emit(ep, 1);
    }

    // ============================================================ construction ============================================================

    static void Build()
    {
        var shader = Shader.Find("Sprites/Default");                // in "always included shaders": safe in the player
        bool sprites = shader;
        if (!shader) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (!shader) { failed = true; Debug.LogWarning("CombatFx off: no particle shader in this build"); return; }

        var go = new GameObject("CombatFx"); Object.DontDestroyOnLoad(go);
        var root = go.transform;
        var softTex = SoftTexture(64); var chunkTex = ChunkTexture(16); var starTex = StarTexture(64);
        softMat = Mat(shader, sprites, softTex, Color.white);
        var chunkMat = Mat(shader, sprites, chunkTex, Color.white);
        var sparkMat = Mat(shader, sprites, softTex, new Color(4.5f, 2.8f, 1.2f, 1f));          // over 1: the bloom picks it up
        var flashMat = Mat(shader, sprites, starTex, new Color(4.2f, 3.0f, 1.5f, 1f));
        var fireMat = Mat(shader, sprites, softTex, new Color(3.2f, 1.7f, 0.6f, 1f));
        tracerMat = Mat(shader, sprites, softTex, new Color(3.2f, 2.2f, 1.2f, 1f));

        // soft: dust, smoke, mist. Swells, fades in and out, loses its speed quickly and drifts up a little
        soft = Make(root, "Fx soft", softMat, 900, -0.012f);
        {
            var col = soft.colorOverLifetime; col.enabled = true; col.color = Grad(Color.white, Color.white, 0f, 1f, 0.12f, 0.75f, 0.55f, 0f);
            var sz = soft.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.55f, 0, 2.2f), new Keyframe(0.35f, 1.2f, 0.9f, 0.9f), new Keyframe(1, 1.75f, 0.5f, 0)));
            var lim = soft.limitVelocityOverLifetime; lim.enabled = true; lim.limit = 0.6f; lim.dampen = 0.14f;
        }
        // chunk: chips, earth, drops. Fly and fall
        chunk = Make(root, "Fx chunk", chunkMat, 700, 1f);
        {
            var col = chunk.colorOverLifetime; col.enabled = true; col.color = Grad(Color.white, Color.white, 1f, 1f, 0.1f, 1f, 0.8f, 0f);
            var rot = chunk.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-9f, 9f);
        }
        // spark: hot streaks along their own velocity
        spark = Make(root, "Fx spark", sparkMat, 300, 0.7f);
        {
            var col = spark.colorOverLifetime; col.enabled = true; col.color = Grad(new Color(1f, 0.95f, 0.8f), new Color(1f, 0.45f, 0.12f), 1f, 1f, 0.1f, 1f, 0.7f, 0f);
            var r = spark.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.03f; r.lengthScale = 1.5f;
        }
        // flash: the star at a muzzle or where steel is struck, gone in two frames
        flash = Make(root, "Fx flash", flashMat, 64, 0f);
        {
            var col = flash.colorOverLifetime; col.enabled = true; col.color = Grad(Color.white, new Color(1f, 0.6f, 0.3f), 1f, 1f, 0.3f, 1f, 0.6f, 0f);
            var sz = flash.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.7f, 1, 1.25f));
        }
        // fire: white-hot, then orange, then a dull red as it becomes smoke
        fire = Make(root, "Fx fire", fireMat, 160, -0.08f);
        {
            var col = fire.colorOverLifetime; col.enabled = true;
            var gr = new Gradient();
            gr.SetKeys(new[] { new GradientColorKey(new Color(1f, 1f, 0.85f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.2f), 0.3f), new GradientColorKey(new Color(0.45f, 0.12f, 0.04f), 0.75f), new GradientColorKey(new Color(0.1f, 0.06f, 0.05f), 1f) },
                       new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = gr;
            var sz = fire.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.45f, 0, 3f), new Keyframe(0.3f, 1.05f), new Keyframe(1, 1.5f)));
            var lim = fire.limitVelocityOverLifetime; lim.enabled = true; lim.limit = 1.2f; lim.dampen = 0.18f;
        }
        foreach (var ps in new[] { soft, chunk, spark, flash, fire }) ps.Play();        // nothing comes out by itself (emission is off)

        trLine = new LineRenderer[MaxTracers];
        for (int i = 0; i < MaxTracers; i++)
        {
            var lr = new GameObject("Tracer " + i).AddComponent<LineRenderer>(); lr.transform.SetParent(root, false);
            lr.sharedMaterial = tracerMat; lr.useWorldSpace = true; lr.positionCount = 2; lr.textureMode = LineTextureMode.Stretch; lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false; lr.enabled = false;
            trLine[i] = lr; trOn[i] = false;
        }

        lights = new Light[MaxLights];
        for (int i = 0; i < MaxLights; i++)
        {
            var l = new GameObject(i == 0 ? "Muzzle light" : "Blast light").AddComponent<Light>(); l.transform.SetParent(root, false);
            l.type = LightType.Point; l.shadows = LightShadows.None; l.intensity = 0; l.enabled = false; lights[i] = l; ltLife[i] = 0; ltT[i] = 0;
        }

        var scorchMat = Mat(shader, sprites, softTex, new Color(0.03f, 0.028f, 0.025f, 0.8f));
        scorch = new Transform[MaxScorch];
        for (int i = 0; i < MaxScorch; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.name = "Scorch"; q.layer = 2;
            if (q.TryGetComponent<Collider>(out var c)) Object.Destroy(c);
            var mr = q.GetComponent<MeshRenderer>(); mr.sharedMaterial = scorchMat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            q.transform.SetParent(root, false); q.SetActive(false); scorch[i] = q.transform;
        }
        for (int i = 0; i < MaxColumns; i++) colStart[i] = -99;
        runner = go.AddComponent<CombatFxRunner>();
    }

    static ParticleSystem Make(Transform root, string name, Material m, int max, float gravity)
    {
        var go = new GameObject(name); go.transform.SetParent(root, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = true; main.playOnAwake = false; main.maxParticles = max; main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World; main.scalingMode = ParticleSystemScalingMode.Local;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return ps;
    }

    static Material Mat(Shader shader, bool sprites, Texture2D tex, Color c)
    {
        var m = new Material(shader);
        if (sprites) { m.mainTexture = tex; m.SetColor("_Color", c); }
        else
        {
            // fall-back: URP particle shader, set up as an alpha-blended transparent
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.renderQueue = 3000;
        }
        return m;
    }

    // colour a -> b, alpha a0 at the start, a1 from t1 to t2 ... down to aEnd at the end
    static ParticleSystem.MinMaxGradient Grad(Color a, Color b, float a0, float a1, float t1, float a2, float t2, float aEnd)
    {
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) },
                  new[] { new GradientAlphaKey(a0, 0f), new GradientAlphaKey(a1, t1), new GradientAlphaKey(a2, t2), new GradientAlphaKey(aEnd, 1f) });
        return g;
    }

    // a lumpy soft blob
    static Texture2D SoftTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
            float lump = 0.72f + 0.28f * Mathf.PerlinNoise(x * 0.11f + 3.7f, y * 0.11f + 9.1f);
            float a = Mathf.Clamp01(1f - r * (2f - lump)); a = a * a * (3f - 2f * a);
            px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        t.SetPixels32(px); t.Apply(true, true); return t;
    }

    // a chip: hard-edged, not round
    static Texture2D ChunkTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), an = Mathf.Atan2(dy, dx);
            float edge = 0.62f + 0.2f * Mathf.Sin(an * 3f + 1f) + 0.1f * Mathf.Sin(an * 5f + 2.4f);
            float a = Mathf.Clamp01((edge - r) * 5f);
            byte v = (byte)(215 + 40 * Mathf.Clamp01(dy * 0.5f + 0.5f));          // a little lighter on top
            px[y * n + x] = new Color32(v, v, v, (byte)(a * 255f));
        }
        t.SetPixels32(px); t.Apply(false, true); return t;
    }

    // an uneven star: hot middle, spikes of different lengths
    static Texture2D StarTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), an = Mathf.Atan2(dy, dx);
            float reach = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(an * 1.5f + 0.6f));
            float spikes = Mathf.Pow(Mathf.Abs(Mathf.Cos(an * 2.5f + 0.3f)), 10f) * Mathf.Clamp01(1f - r / reach);
            float core = Mathf.Clamp01(1f - r * 2.2f); core *= core;
            float a = Mathf.Clamp01(core * 1.6f + spikes * 0.9f) * Mathf.Clamp01((1f - r) * 6f);
            px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        t.SetPixels32(px); t.Apply(true, true); return t;
    }
}

// drives CombatFx once a frame (created by it, never placed in a scene)
public class CombatFxRunner : MonoBehaviour
{
    void Update() { CombatFx.Tick(); }
}
