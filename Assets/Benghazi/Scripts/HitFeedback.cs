using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// What being hit, and dying, looks and sounds like - for the player and for the men he fights.
//   player hit    a grunt, blood on the edges of the view that runs and fades, on top of the red flash and
//                 hit-direction marker PlayerHealth already draws
//   badly hurt    the picture drains of colour and closes in, a heartbeat that quickens as health falls
//   player dead   a last breath, he goes down: the view drops to the ground and rolls over, colour goes,
//                 the picture fades out until the respawn
//   enemy hit     a grunt from where he stands, blood thrown onto the wall or ground behind him
//   enemy dead    a cry (none on a head shot), and blood spreads under the body
// The player's part is driven by watching PlayerHealth, so nothing there had to change. Sounds are made in
// code like every other sound in the game. Starts itself; nothing in the scene refers to it.
[DefaultExecutionOrder(500)]            // after the camera rig has placed the view
public class HitFeedback : MonoBehaviour
{
    public static int PlayerHits { get; private set; }
    public static int EnemyGrunts { get; private set; }
    public static int BloodMarks { get; private set; }
    public static float DeathView { get; private set; }       // 0 standing .. 1 down on the ground

    static HitFeedback inst;
    PlayerHealth hp; Camera cam; float lastHealth, deadAt = -1, nextBeat, lowT, hurtT; bool wasDead;
    Volume volume; ColorAdjustments grade; Vignette vig;
    AudioSource self; AudioSource[] world; int worldIx;
    AudioClip[] grunts, cries; AudioClip beat, lastBreath;
    Texture2D splatTex, blackTex;
    struct Splat { public Rect r; public float born, life, rot; }
    readonly List<Splat> splats = new();
    // blood in the world
    const int MaxMarks = 48;
    readonly Queue<Transform> marks = new(); Material bloodMat, bloodDecal; Mesh quad;
    class Pool { public Transform t; public float born, size; }
    readonly List<Pool> pools = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { inst = null; PlayerHits = EnemyGrunts = BloodMarks = 0; DeathView = 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!FindFirstObjectByType<PlayerHealth>()) return;
        inst = new GameObject("Hit feedback").AddComponent<HitFeedback>();
    }

    void Start()
    {
        hp = FindFirstObjectByType<PlayerHealth>(); cam = Camera.main;
        lastHealth = hp ? hp.Health : 100;
        try
        {
            self = gameObject.AddComponent<AudioSource>(); self.spatialBlend = 0; self.playOnAwake = false;
            world = new AudioSource[5];
            for (int i = 0; i < world.Length; i++)
            {
                var g = new GameObject("voice"); g.transform.SetParent(transform, false);
                var a = g.AddComponent<AudioSource>(); a.playOnAwake = false; a.spatialBlend = 1; a.rolloffMode = AudioRolloffMode.Logarithmic; a.minDistance = 3; a.maxDistance = 70; a.dopplerLevel = 0;
                world[i] = a;
            }
            grunts = new[] { Voice(3, 0.22f, 150, 105, 700, 1150, 0.9f), Voice(7, 0.27f, 135, 95, 620, 1050, 1f), Voice(12, 0.19f, 165, 120, 760, 1250, 0.8f), Voice(19, 0.3f, 125, 88, 580, 980, 1f) };
            cries = new[] { Voice(31, 0.75f, 170, 78, 720, 1180, 1f), Voice(37, 0.9f, 150, 70, 640, 1060, 1f), Voice(43, 0.6f, 185, 92, 780, 1300, 0.9f) };
            lastBreath = Voice(51, 1.3f, 120, 60, 560, 940, 0.75f, 0.55f);
            beat = Heartbeat();
        }
        catch (System.Exception e) { Debug.LogWarning("[hit] sound off: " + e.Message); self = null; world = null; }

        // the wounded picture: its own global volume on top of the scene's, weighted by how bad it is
        var vg = new GameObject("Hurt volume"); vg.transform.SetParent(transform, false);
        volume = vg.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 60; volume.weight = 0;
        var prof = ScriptableObject.CreateInstance<VolumeProfile>();
        grade = prof.Add<ColorAdjustments>(true); grade.saturation.Override(-70f); grade.contrast.Override(18f); grade.colorFilter.Override(new Color(1f, 0.86f, 0.84f));
        vig = prof.Add<Vignette>(true); vig.intensity.Override(0.52f); vig.smoothness.Override(0.5f); vig.color.Override(new Color(0.25f, 0.01f, 0.01f));
        volume.profile = prof;

        splatTex = Blot(256, 5, new Color(0.26f, 0.004f, 0.004f), 0.2f);     // soft and dark: blood in the eye, not a painted flower blackTex = Texture2D.whiteTexture;
        var prim = GameObject.CreatePrimitive(PrimitiveType.Quad); quad = prim.GetComponent<MeshFilter>().sharedMesh; Destroy(prim);
        var blot = Blot(256, 9, new Color(0.2f, 0.005f, 0.005f));
        bloodMat = new Material(Rp.Fx) { name = "Blood", mainTexture = blot };
        bloodDecal = Rp.Decal("Blood decal", blot, new Color(1.6f, 1f, 1f), 0.55f);         // wet: it catches the light
    }

    void Update()
    {
        if (!hp) return;
        float dt = Time.deltaTime;
        // the player's health, watched
        if (hp.Dead && !wasDead) Died();
        else if (!hp.Dead && wasDead) { deadAt = -1; DeathView = 0; splats.Clear(); }
        else if (!hp.Dead && hp.Health < lastHealth - 0.01f)
        {
            bool drowning = hp.InWater && hp.Breath <= 0.02f;
            if (!drowning) Struck(lastHealth - hp.Health);
        }
        wasDead = hp.Dead; lastHealth = hp.Health;

        // badly hurt: colour drains, the heart pounds
        float low = hp.Dead ? 1 : Mathf.Clamp01(1 - hp.Health / 45f);
        lowT = Mathf.Lerp(lowT, low, 1 - Mathf.Exp(-(low > lowT ? 6 : 1.2f) * dt));
        hurtT = Mathf.Max(0, hurtT - dt * 1.4f);
        if (volume) volume.weight = Mathf.Clamp01(Mathf.Max(lowT, hurtT * 0.45f));
        if (!hp.Dead && low > 0.15f && self && beat && Time.time >= nextBeat)
        {
            self.PlayOneShot(beat, Mathf.Lerp(0.35f, 0.9f, low));
            nextBeat = Time.time + Mathf.Lerp(0.95f, 0.5f, low);
        }
        // blood pools spread, slowly
        for (int i = pools.Count - 1; i >= 0; i--)
        {
            var p = pools[i]; if (!p.t) { pools.RemoveAt(i); continue; }
            float k = Mathf.Clamp01((Time.time - p.born) / 7f); k = 1 - (1 - k) * (1 - k);
            { float sz = Mathf.Lerp(0.12f, p.size, k); p.t.localScale = bloodDecal ? new Vector3(sz, sz, 1f) : Vector3.one * sz; }
            if (k >= 1) pools.RemoveAt(i);
        }
    }

    // down he goes: after the camera rig has put the view at eye height, take it to the ground and over
    void LateUpdate()
    {
        if (!hp || deadAt < 0 || !cam) return;
        float t = Time.time - deadAt, k = Mathf.Clamp01(t / 0.85f); k = k * k * (3 - 2 * k);
        DeathView = k;
        var ct = cam.transform; float ground = hp.transform.position.y + 0.2f;
        var p = ct.position; p.y = Mathf.Lerp(p.y, ground, k); p += hp.transform.right * 0.25f * k;
        ct.SetPositionAndRotation(p, ct.rotation * Quaternion.Euler(-14f * k, 0, 72f * k));
    }

    void Struck(float amount)
    {
        PlayerHits++; hurtT = 1;
        if (self && grunts != null) { self.pitch = Random.Range(0.94f, 1.06f); self.PlayOneShot(grunts[Random.Range(0, grunts.Length)], Mathf.Clamp(0.55f + amount * 0.02f, 0.55f, 1f)); }
        // blood on the edges of the view: more of it for a harder hit
        int n = amount > 18 ? 2 : 1;
        for (int i = 0; i < n; i++)
        {
            float w = Screen.width, h = Screen.height, size = h * Random.Range(0.3f, 0.48f);
            bool side = Random.value < 0.6f;
            float x = side ? (Random.value < 0.5f ? -size * 0.35f : w - size * 0.65f) : Random.Range(0, w - size);
            float y = side ? Random.Range(-size * 0.2f, h - size * 0.8f) : (Random.value < 0.5f ? -size * 0.35f : h - size * 0.65f);
            if (splats.Count > 10) splats.RemoveAt(0);
            splats.Add(new Splat { r = new Rect(x, y, size, size), born = Time.time, life = Random.Range(3f, 4.5f), rot = Random.Range(0f, 360f) });
        }
    }

    void Died()
    {
        deadAt = Time.time;
        if (self && lastBreath) { self.pitch = 1; self.PlayOneShot(lastBreath, 0.9f); }
        CombatFx.Shake(2.2f, 0.35f);
    }

    void OnGUI()
    {
        if (!hp || Event.current.type != EventType.Repaint) return;
        var keep = GUI.color; var keepM = GUI.matrix;
        for (int i = splats.Count - 1; i >= 0; i--)
        {
            var s = splats[i]; float age = Time.time - s.born;
            if (age > s.life) { splats.RemoveAt(i); continue; }
            float a = Mathf.Clamp01(age / 0.06f) * Mathf.Clamp01((s.life - age) / (s.life * 0.6f)) * 0.62f;
            GUI.color = new Color(1, 1, 1, a);
            var r = s.r; r.y += age * 9f;                               // it runs down a little
            GUIUtility.RotateAroundPivot(s.rot, r.center);
            GUI.DrawTexture(r, splatTex);
            GUI.matrix = keepM;
        }
        if (deadAt >= 0)
        {
            float f = Mathf.Clamp01((Time.time - deadAt - 0.9f) / 1.8f);
            if (f > 0) { GUI.color = new Color(0, 0, 0, f * 0.96f); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), blackTex); }
        }
        GUI.color = keep;
    }

    // ---------- the men he fights ----------
    public static void EnemyHit(Vector3 point, Vector3 dir, bool head, bool fatal)
    {
        if (!inst) return;
        inst.Spatter(point, dir, head ? 1.5f : 1f);
        if (!fatal && inst.world != null && Random.value < 0.75f) { inst.Say(inst.grunts, point, 0.85f); EnemyGrunts++; }
    }

    public static void EnemyDied(Vector3 at, Vector3 dir, bool head)
    {
        if (!inst) return;
        if (!head && inst.world != null) inst.Say(inst.cries, at + Vector3.up * 1.4f, 1f);
        // blood under the body, spreading
        if (Physics.Raycast(at + Vector3.up * 0.6f, Vector3.down, out var g, 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var t = inst.Mark(g.point + g.normal * 0.012f, g.normal, 0.12f);
            inst.pools.Add(new Pool { t = t, born = Time.time + 1.1f, size = Random.Range(0.75f, 1.15f) });
        }
    }

    void Say(AudioClip[] clips, Vector3 at, float vol)
    {
        if (clips == null || world == null) return;
        var a = world[worldIx = (worldIx + 1) % world.Length]; if (!a) return;
        a.transform.position = at; a.pitch = Random.Range(0.9f, 1.1f); a.PlayOneShot(clips[Random.Range(0, clips.Length)], vol);
    }

    // blood thrown on by the round: onto the wall behind him if there is one within a few metres, else the ground beyond him
    void Spatter(Vector3 point, Vector3 dir, float size)
    {
        var from = point + dir * 0.45f;
        if (Physics.Raycast(from, dir, out var w, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !w.collider.GetComponentInParent<EnemyAI>())
            Mark(w.point + w.normal * 0.012f, w.normal, Random.Range(0.3f, 0.55f) * size);
        else if (Physics.Raycast(from + dir * Random.Range(0.3f, 1.1f), Vector3.down, out var g, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !g.collider.GetComponentInParent<EnemyAI>())
            Mark(g.point + g.normal * 0.012f, g.normal, Random.Range(0.25f, 0.45f) * size);
    }

    Transform Mark(Vector3 p, Vector3 n, float size)
    {
        Transform t = null;
        while (marks.Count >= MaxMarks) { t = marks.Dequeue(); if (t) break; }
        if (!t)
        {
            var go = new GameObject("Blood"); go.layer = 2;
            if (bloodDecal) { var dp = go.AddComponent<DecalProjector>(); dp.material = bloodDecal; dp.size = new Vector3(1, 1, 0.3f); dp.pivot = Vector3.zero; dp.drawDistance = 90f; dp.fadeScale = 0.85f; }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = bloodMat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            }
            t = go.transform;
        }
        marks.Enqueue(t);
        // HDRP decal: laid into the surface, lit and shadowed with it (the box is 1 x 1 x 0.3 m, scaled)
        t.SetPositionAndRotation(p, Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)));
        t.localScale = bloodDecal ? new Vector3(size, size, 1f) : Vector3.one * size; t.gameObject.SetActive(true);
        BloodMarks++;
        return t;
    }

    // ---------- made in code ----------
    // a blot with ragged lobes and a few flung drops
    static Texture2D Blot(int n, int seed, Color col, float soft = 0.06f)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
        var rnd = new System.Random(seed); var ph = new float[9]; for (int i = 0; i < ph.Length; i++) ph[i] = (float)rnd.NextDouble() * 6.2832f;
        var drops = new Vector3[14];
        for (int i = 0; i < drops.Length; i++) { float a = (float)rnd.NextDouble() * 6.2832f, d = 0.5f + (float)rnd.NextDouble() * 0.4f; drops[i] = new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.025f + (float)rnd.NextDouble() * 0.06f); }
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
            float edge = 0.42f; for (int i = 0; i < ph.Length; i++) edge += Mathf.Sin(a * (i + 2) + ph[i]) * (soft > 0.1f ? 0.035f : 0.075f) / (1 + i * 0.35f);
            float al = Mathf.Clamp01((edge - r) / soft); if (soft > 0.1f) al *= al;
            foreach (var d in drops) al = Mathf.Max(al, Mathf.Clamp01((d.z * (soft > 0.1f ? 0.6f : 1f) - Vector2.Distance(new Vector2(dx, dy), d)) / (soft > 0.1f ? 0.03f : 0.02f)) * (soft > 0.1f ? 0.7f : 1f));
            float dark = 0.75f + 0.25f * Mathf.Clamp01(r / Mathf.Max(0.01f, edge));        // thicker and darker towards the middle
            px[y * n + x] = new Color(col.r * dark, col.g, col.b, al * 0.92f);
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    // A voice without words: a pulse train (the vocal cords) falling in pitch through two resonances (an
    // open "ah/uh"), with breath noise on top. Short and hard = a grunt, long and falling = a cry.
    static AudioClip Voice(int seed, float len, float f0a, float f0b, float form1, float form2, float gain, float breath = 0.22f)
    {
        const int rate = 44100; int n = (int)(rate * len); var d = new float[n]; var rnd = new System.Random(seed);
        float phase = 0, y1a = 0, y2a = 0, y1b = 0, y2b = 0, lp = 0;
        float ra = 0.992f, rb = 0.988f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, u = t / len;
            float f0 = Mathf.Lerp(f0a, f0b, u * u * 0.4f + u * 0.6f) * (1f + 0.02f * Mathf.Sin(t * 37f));
            phase += f0 / rate; if (phase >= 1) phase -= 1;
            float pulse = (phase < 0.12f ? 1f - phase / 0.12f : 0f) - 0.06f;                     // a sharp glottal pulse
            float w = (float)rnd.NextDouble() * 2 - 1; lp += (w - lp) * 0.3f;
            float src = pulse + (w - lp) * breath;
            float wa = 6.2832f * form1 * (1 - 0.15f * u) / rate, wb = 6.2832f * form2 / rate;
            float ya = src + 2 * ra * Mathf.Cos(wa) * y1a - ra * ra * y2a; y2a = y1a; y1a = ya;
            float yb = src + 2 * rb * Mathf.Cos(wb) * y1b - rb * rb * y2b; y2b = y1b; y1b = yb;
            float env = Mathf.Min(1f, t / 0.012f) * Mathf.Pow(1 - u, len > 0.5f ? 1.3f : 0.7f);
            d[i] = (ya * 0.6f + yb * 0.3f) * env;
        }
        float peak = 1e-6f; for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(d[i]));
        for (int i = 0; i < n; i++) d[i] = d[i] / peak * 0.8f * gain;
        var clip = AudioClip.Create("voice " + seed, n, 1, rate, false); clip.SetData(d, 0); return clip;
    }

    static AudioClip Heartbeat()
    {
        const int rate = 44100; int n = (int)(rate * 0.42f); var d = new float[n];
        void Thump(float at, float amp, float f)
        {
            int s0 = (int)(at * rate);
            for (int i = s0; i < n; i++) { float t = (i - s0) / (float)rate; d[i] += Mathf.Sin(6.2832f * (f - 18f * t * 8f) * t) * Mathf.Exp(-t * 34f) * amp * Mathf.Min(1, t * 400f); }
        }
        Thump(0f, 1f, 62f); Thump(0.15f, 0.7f, 54f);
        for (int i = 0; i < n; i++) d[i] *= 0.85f * Mathf.Min(1f, (n - i) / (rate * 0.03f));
        var clip = AudioClip.Create("heartbeat", n, 1, rate, false); clip.SetData(d, 0); return clip;
    }
}
