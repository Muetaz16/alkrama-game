using UnityEngine;

// First-person weapon view (Call of Duty style): only the gun is drawn, held in front of the camera.
// Everything is animated in code from key frames and springs:
//   idle breathing, look sway (the gun lags behind the mouse), walk / run bob in step with the feet,
//   sprint pose (gun swung across the chest), crouch cant, jump lift and landing dip, strafe tilt,
//   aim down sights (red dot / scope lined up with the screen centre), recoil kick with a spring,
//   magazine reload with the mag dropping out and a new one rocked in, charging handle / bolt cycling,
//   weapon switch (lower, swap, raise), knife slash (alternating sides) and heavy stab,
//   gun pulled back when you stand against a wall.
public class ViewModel : MonoBehaviour
{
    public const int AK = 0, Sniper = 1, Knife = 2;

    PlayerController player; ThirdPersonCamera rig; Camera cam;
    Transform root;
    readonly Transform[] holder = new Transform[3], model = new Transform[3], mag = new Transform[3], bolt = new Transform[3],
        handle = new Transform[3], muzzle = new Transform[3], eject = new Transform[3], flash = new Transform[3];
    readonly Vector3[] sight = new Vector3[3], magHome = new Vector3[3], boltHome = new Vector3[3];
    Renderer[][] renderers = new Renderer[3][];

    // hip poses (camera space) and how far in front of the eye the sight sits when aiming
    static readonly Vector3[] HipPos = { new(0.13f, -0.19f, 0.34f), new(0.14f, -0.195f, 0.33f), new(0.135f, -0.135f, 0.27f) };
    static readonly Vector3[] HipRot = { new(1f, -1.5f, 2f), new(1f, -1.5f, 2f), new(-28f, -14f, -22f) };
    static readonly float[] AdsDist = { 0.27f, 0.05f, 0f };
    float AdsDistance(int w) => w == AK && GunFactory.RealAK ? 0.21f : AdsDist[w];      // iron sights sit closer to the eye than a red dot
    Vector3 Hip(int w) => w == AK && GunFactory.RealAK ? new Vector3(0.14f, -0.2f, 0.47f) : HipPos[w];   // the real AK's stock is long: hold it further out

    public int Shown { get; private set; }
    public float aimT;                       // 0 hip .. 1 aiming, set by the weapon
    public bool visible = true;
    public bool Busy => switching || Playing(knifeAnim) || Playing(reloadAnim);
    public bool KnifeBusy => Playing(knifeAnim);
    public System.Action<string> onSound;    // "magout", "magin", "boltback", "boltfwd", "swish"
    public System.Action<bool> onKnifeHit;   // heavy?
    public System.Action onEject;            // the sniper's bolt throws the case out

    // springs and smoothed states
    Vector3 recP, recPV, recR, recRV, swayR, swayP;
    float sprintT, crouchT, airT, block, lastYaw, lastPitch, time, scopeHide;
    bool switching, raising; float switchStart; int switchTo;

    // key-frame animation: offsets of the gun (p, r) or of a part, over u = 0..1
    struct Key { public float u; public Vector3 p, r; public int e; public Key(float u, Vector3 p, Vector3 r, int e = 0) { this.u = u; this.p = p; this.r = r; this.e = e; } }
    class Anim { public Key[] gun, part1, part2; public float start = -1, len; public int weapon; public float side = 1; public float[] events; public string[] eventNames; public int fired; }
    Anim reloadAnim = new(), knifeAnim = new(), boltAnim = new();

    static Key K(float u, float px, float py, float pz, float rx, float ry, float rz, int e = 0) => new(u, new Vector3(px, py, pz), new Vector3(rx, ry, rz), e);
    static readonly Key Zero0 = K(0, 0, 0, 0, 0, 0, 0), Zero1 = K(1, 0, 0, 0, 0, 0, 0);

    public void Init(PlayerController p, ThirdPersonCamera r, Camera c)
    {
        player = p; rig = r; cam = c;
        root = new GameObject("ViewModel").transform;
        root.SetParent(cam.transform, false);
        for (int i = 0; i < 3; i++)
        {
            holder[i] = new GameObject("Hold " + i).transform; holder[i].SetParent(root, false);
            model[i] = i == AK ? GunFactory.AK(holder[i]) : i == Sniper ? GunFactory.Sniper(holder[i]) : GunFactory.Knife(holder[i]);
            mag[i] = GunFactory.Find(model[i], "Magazine"); bolt[i] = GunFactory.Find(model[i], "Bolt");
            handle[i] = bolt[i] ? GunFactory.Find(bolt[i], "handle") : null;
            muzzle[i] = GunFactory.Find(model[i], "Muzzle"); eject[i] = GunFactory.Find(model[i], "Eject");
            var s = GunFactory.Find(model[i], "Sight"); sight[i] = s ? s.localPosition : Vector3.zero;
            if (mag[i]) magHome[i] = mag[i].localPosition;
            if (bolt[i]) boltHome[i] = bolt[i].localPosition;
            if (i != Knife) flash[i] = BuildFlash(muzzle[i]);
            renderers[i] = model[i].GetComponentsInChildren<Renderer>(true);
            holder[i].gameObject.SetActive(i == AK);
        }
        lastYaw = rig.yaw; lastPitch = rig.pitch;
    }

    public Vector3 MuzzlePos => muzzle[Shown] ? muzzle[Shown].position : cam.transform.position;
    public Vector3 EjectPos(int w) => eject[w] ? eject[w].position : cam.transform.position;

    // ---------- triggers from the weapon ----------
    public void Fire(int w, bool aiming)
    {
        float s = aiming ? 0.55f : 1f, rs = aiming ? 0.35f : 1f;
        if (w == AK) { recPV += new Vector3(Random.Range(-0.08f, 0.08f), 0.12f, -1.25f) * s; recRV += new Vector3(-95f, Random.Range(-35f, 35f), Random.Range(-45f, 45f)) * rs; }
        else { recPV += new Vector3(0, 0.35f, -3.2f) * s; recRV += new Vector3(-320f, Random.Range(-40f, 40f), Random.Range(-60f, 90f)) * rs; }
        if (flash[w])
        {
            flash[w].gameObject.SetActive(true); flashOff = Time.time + 0.035f;
            flash[w].localRotation = Quaternion.Euler(0, 0, Random.Range(0, 360f)); flash[w].localScale = Vector3.one * Random.Range(0.8f, 1.25f) * (w == Sniper ? 1.6f : 1f);
        }
        if (w == Sniper) Play(boltAnim, Sniper, 0.85f, 0.14f);
    }
    float flashOff;

    public void Reload(int w, float seconds) => Play(reloadAnim, w, seconds);

    public void KnifeAttack(bool heavy)
    {
        knifeAnim.side = heavy ? 1 : -knifeAnim.side;
        heavyKnife = heavy;
        Play(knifeAnim, Knife, heavy ? 0.62f : 0.42f);
        onSound?.Invoke("swish");
    }
    bool heavyKnife;

    public void SwitchTo(int w, bool fast = false)
    {
        if (w == Shown && !switching) return;
        switchTo = w; switching = true; raising = false; switchStart = Time.time; fastSwitch = fast;
        reloadAnim.start = -1; boltAnim.start = boltAnim.start >= 0 && boltAnim.weapon == w ? boltAnim.start : -1;
    }
    bool fastSwitch;

    void Play(Anim a, int w, float len, float delay = 0) { a.start = Time.time + delay; a.len = len; a.weapon = w; a.fired = 0; }
    static bool Playing(Anim a) => a.start >= 0 && Time.time < a.start + a.len;
    static float U(Anim a) => Mathf.Clamp01((Time.time - a.start) / a.len);

    // ---------- key frames ----------
    static void Eval(Key[] k, float u, out Vector3 p, out Vector3 r)
    {
        p = k[0].p; r = k[0].r;
        if (u <= k[0].u) return;
        for (int i = 0; i < k.Length - 1; i++)
        {
            if (u > k[i + 1].u) continue;
            float s = (u - k[i].u) / Mathf.Max(1e-4f, k[i + 1].u - k[i].u);
            int e = k[i + 1].e;
            s = e == 1 ? 1 - (1 - s) * (1 - s) : e == 2 ? s * s : e == 3 ? s : s * s * (3 - 2 * s);
            p = Vector3.LerpUnclamped(k[i].p, k[i + 1].p, s); r = Vector3.LerpUnclamped(k[i].r, k[i + 1].r, s);
            return;
        }
        p = k[^1].p; r = k[^1].r;
    }

    // rifle reload: tilt the gun, drop the mag, rock a new one in, rack the charging handle
    static readonly Key[] AkReloadGun = { Zero0, K(0.13f, -0.03f, -0.03f, -0.02f, -6, 12, 30), K(0.3f, -0.032f, -0.032f, -0.02f, -7, 13, 31),
        K(0.58f, -0.035f, -0.036f, -0.02f, -6, 14, 32), K(0.63f, -0.03f, -0.026f, -0.02f, -10, 12, 29, 1), K(0.74f, -0.012f, -0.02f, -0.01f, 0, 6, -14),
        K(0.82f, -0.006f, -0.022f, -0.028f, 3, 7, -16, 1), K(0.88f, -0.012f, -0.02f, -0.01f, -2, 6, -13), Zero1 };
    static readonly Key[] AkReloadMag = { Zero0, K(0.18f, 0, 0, 0, 0, 0, 0), K(0.24f, 0, -0.02f, 0.008f, -8, 0, 0, 1), K(0.36f, 0, -0.42f, 0.05f, -40, 0, 0, 2),
        K(0.44f, 0, -0.42f, -0.06f, 25, 0, 0), K(0.56f, 0, -0.035f, 0.014f, 16, 0, 0, 1), K(0.63f, 0, 0, 0, 0, 0, 0, 1), Zero1 };
    static readonly Key[] AkReloadBolt = { Zero0, K(0.78f, 0, 0, 0, 0, 0, 0), K(0.83f, 0, 0, -0.085f, 0, 0, 0, 1), K(0.87f, 0, 0, 0, 0, 0, 0, 2), Zero1 };
    // sniper reload: open the bolt, swap the box mag, close the bolt
    static readonly Key[] SnReloadGun = { Zero0, K(0.1f, -0.02f, -0.02f, -0.02f, -4, 10, 22), K(0.68f, -0.024f, -0.026f, -0.02f, -5, 12, 24), K(0.72f, -0.02f, -0.02f, -0.02f, -8, 10, 21, 1), K(0.9f, -0.01f, -0.01f, -0.01f, -2, 6, 8), Zero1 };
    static readonly Key[] SnReloadMag = { Zero0, K(0.24f, 0, 0, 0, 0, 0, 0), K(0.38f, 0, -0.42f, 0.02f, -25, 0, 0, 2), K(0.48f, 0, -0.42f, -0.02f, 15, 0, 0), K(0.62f, 0, -0.02f, 0, 6, 0, 0, 1), K(0.68f, 0, 0, 0, 0, 0, 0, 1), Zero1 };
    static readonly Key[] SnReloadBolt = { Zero0, K(0.08f, 0, 0, 0, 0, 0, 0), K(0.14f, 0, 0, 0, 0, 0, 0), K(0.2f, 0, 0, -0.075f, 0, 0, 0, 1), K(0.78f, 0, 0, -0.075f, 0, 0, 0), K(0.84f, 0, 0, 0, 0, 0, 0, 2), Zero1 };
    static readonly Key[] SnReloadHandle = { Zero0, K(0.08f, 0, 0, 0, 0, 0, 0), K(0.14f, 0, 0, 0, 0, 0, -70, 1), K(0.84f, 0, 0, 0, 0, 0, -70), K(0.9f, 0, 0, 0, 0, 0, 0, 2), Zero1 };
    // sniper bolt cycle after each shot
    static readonly Key[] BoltGun = { Zero0, K(0.15f, -0.01f, 0.004f, -0.012f, -2, 5, 14), K(0.72f, -0.01f, 0.004f, -0.012f, -2, 5, 14), Zero1 };
    static readonly Key[] BoltSlide = { Zero0, K(0.2f, 0, 0, 0, 0, 0, 0), K(0.42f, 0, 0, -0.075f, 0, 0, 0, 1), K(0.62f, 0, 0, 0, 0, 0, 0, 2), Zero1 };
    static readonly Key[] BoltHandle = { Zero0, K(0.18f, 0, 0, 0, 0, 0, -70, 1), K(0.64f, 0, 0, 0, 0, 0, -70), K(0.8f, 0, 0, 0, 0, 0, 0, 2), Zero1 };
    // knife
    static readonly Key[] Slash = { Zero0, K(0.28f, 0.05f, 0.07f, -0.04f, -30, 22, 48, 1), K(0.52f, -0.17f, -0.07f, 0.08f, 22, -58, -38, 3), K(0.64f, -0.18f, -0.08f, 0.06f, 26, -62, -42, 1), Zero1 };
    static readonly Key[] Stab = { Zero0, K(0.35f, 0.0f, 0.03f, -0.09f, -6, 8, -75, 1), K(0.5f, -0.07f, 0.035f, 0.23f, -2, 12, -80, 3), K(0.64f, -0.07f, 0.035f, 0.22f, -2, 12, -80), Zero1 };
    static readonly Key[] Lower = { Zero0, K(1, 0, -0.22f, -0.05f, 40, 0, -10, 2) };
    static readonly Key[] Raise = { K(0, 0, -0.22f, -0.05f, 40, 0, -10), K(0.75f, 0, 0.005f, 0.002f, -3, 0, 1, 1), Zero1 };

    void Update()
    {
        if (!root) return;
        // events in the animations (sounds, the knife hit, the case out of the sniper)
        if (Playing(reloadAnim))
        {
            float u = U(reloadAnim);
            var ev = reloadAnim.weapon == AK ? new[] { 0.2f, 0.62f, 0.82f, 0.87f } : new[] { 0.13f, 0.27f, 0.66f, 0.85f };
            var nm = reloadAnim.weapon == AK ? new[] { "magout", "magin", "boltback", "boltfwd" } : new[] { "boltback", "magout", "magin", "boltfwd" };
            while (reloadAnim.fired < ev.Length && u >= ev[reloadAnim.fired]) onSound?.Invoke(nm[reloadAnim.fired++]);
        }
        if (Playing(boltAnim))
        {
            float u = U(boltAnim);
            if (boltAnim.fired == 0 && u > 0.18f) { boltAnim.fired = 1; onSound?.Invoke("boltback"); }
            if (boltAnim.fired == 1 && u > 0.42f) { boltAnim.fired = 2; onEject?.Invoke(); }
            if (boltAnim.fired == 2 && u > 0.62f) { boltAnim.fired = 3; onSound?.Invoke("boltfwd"); }
        }
        if (Playing(knifeAnim) && knifeAnim.fired == 0 && U(knifeAnim) > (heavyKnife ? 0.48f : 0.4f)) { knifeAnim.fired = 1; onKnifeHit?.Invoke(heavyKnife); }
        // weapon switch: lower, swap, raise
        if (switching)
        {
            float lowerT = fastSwitch ? 0.08f : 0.17f, raiseT = fastSwitch ? 0.14f : 0.3f;
            if (!raising && Time.time > switchStart + lowerT)
            {
                holder[Shown].gameObject.SetActive(false); Shown = switchTo; holder[Shown].gameObject.SetActive(visible);
                raising = true; switchStart = Time.time;
            }
            else if (raising && Time.time > switchStart + raiseT) switching = false;
        }
        if (flash[Shown] && Time.time > flashOff) flash[Shown].gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        if (!root) return;
        root.gameObject.SetActive(visible);
        if (!visible) { lastYaw = rig.yaw; lastPitch = rig.pitch; return; }
        float dt = Mathf.Max(Time.deltaTime, 1e-4f); time += dt;
        int w = Shown;
        float a = Mathf.SmoothStep(0, 1, aimT);
        float free = 1 - a;

        // hip <-> aim down sights
        var adsPos = w == Knife ? HipPos[w] : new Vector3(0, 0, AdsDistance(w)) - sight[w];
        var pos = Vector3.Lerp(Hip(w), adsPos, a);
        var rot = Vector3.Lerp(HipRot[w], Vector3.zero, a);

        // breathing
        pos += new Vector3(Mathf.Sin(time * 0.8f) * 0.0009f, Mathf.Sin(time * 1.6f) * 0.0014f, 0) * (free + 0.25f);
        rot += new Vector3(Mathf.Sin(time * 1.6f + 1f) * 0.35f, Mathf.Sin(time * 0.8f) * 0.2f, 0) * (free + 0.2f);

        // look sway: the gun trails the mouse a little
        float yawRate = Mathf.DeltaAngle(lastYaw, rig.yaw) / dt, pitchRate = (rig.pitch - lastPitch) / dt;
        lastYaw = rig.yaw; lastPitch = rig.pitch;
        float swayK = Mathf.Lerp(1f, 0.25f, a);
        var swayTarget = new Vector3(Mathf.Clamp(-pitchRate * 0.012f, -5, 5), Mathf.Clamp(-yawRate * 0.012f, -6, 6), Mathf.Clamp(yawRate * 0.008f, -5, 5)) * swayK;
        swayR = Vector3.Lerp(swayR, swayTarget, 1 - Mathf.Exp(-9 * dt));
        swayP = Vector3.Lerp(swayP, new Vector3(Mathf.Clamp(-yawRate * 0.00012f, -0.012f, 0.012f), Mathf.Clamp(pitchRate * 0.0001f, -0.01f, 0.01f), 0) * swayK, 1 - Mathf.Exp(-9 * dt));
        rot += swayR; pos += swayP;

        // walk / run bob, in step with the animation
        float speed = player.Speed, k = Mathf.Clamp01(speed / PlayerController.JogSpeed);
        bool sprint = player.Sprinting && !switching;
        sprintT = Mathf.MoveTowards(sprintT, sprint ? 1 : 0, dt / (sprint ? 0.2f : 0.16f));
        float sp = Mathf.SmoothStep(0, 1, sprintT);
        float ph = player.StepPhase, bobK = k * Mathf.Lerp(1f, 0.18f, a) * (1 + sp * 1.4f);
        pos += new Vector3(Mathf.Sin(ph) * 0.011f, Mathf.Sin(ph * 2) * 0.007f - 0.004f * k, 0) * bobK;
        rot += new Vector3(Mathf.Sin(ph * 2) * 0.9f, Mathf.Sin(ph) * 0.8f, Mathf.Sin(ph) * 1.8f) * bobK;
        // sprint: rifle swung across the chest, knife pumped low
        if (w == Knife) { pos += new Vector3(-0.01f, -0.03f, -0.02f) * sp; rot += new Vector3(22, 0, 8) * sp; }
        else { pos += new Vector3(-0.05f, 0.01f, -0.06f) * sp; rot += new Vector3(12, -38, 24) * sp; rot += new Vector3(Mathf.Sin(ph * 2) * 3f, 0, Mathf.Sin(ph) * 4f) * sp; }

        // strafe tilt, crouch cant, jump lift, landing dip
        rot.z += player.Strafe * 6f * free;
        crouchT = Mathf.Lerp(crouchT, player.crouch ? 1 : 0, 1 - Mathf.Exp(-8 * dt));
        pos += new Vector3(-0.012f, 0.012f, 0) * crouchT * free; rot.z -= 7f * crouchT * free;
        bool air = !player.GetComponent<CharacterController>().isGrounded;
        airT = Mathf.Lerp(airT, air ? 1 : 0, 1 - Mathf.Exp(-(air ? 6 : 14) * dt));
        pos.y += 0.012f * airT; rot.x -= 4f * airT;
        pos.y += player.LandDip * 0.07f; rot.x -= player.LandDip * 9f;

        // recoil springs
        Spring(ref recP, ref recPV, 420f, 0.62f, dt); Spring(ref recR, ref recRV, 380f, 0.6f, dt);
        pos += recP; rot += recR;

        // key-frame animations
        Vector3 p, r;
        if (Playing(reloadAnim) && reloadAnim.weapon == w)
        {
            float u = U(reloadAnim);
            Eval(w == AK ? AkReloadGun : SnReloadGun, u, out p, out r); pos += p * (1 - a * 0.5f); rot += r;
            if (mag[w]) { Eval(w == AK ? AkReloadMag : SnReloadMag, u, out p, out r); mag[w].localPosition = magHome[w] + p; mag[w].localEulerAngles = r; }
            if (bolt[w]) { Eval(w == AK ? AkReloadBolt : SnReloadBolt, u, out p, out r); bolt[w].localPosition = boltHome[w] + p; }
            if (handle[w] && w == Sniper) { Eval(SnReloadHandle, u, out p, out r); handle[w].localEulerAngles = r; }
        }
        else if (Playing(boltAnim) && boltAnim.weapon == w)
        {
            float u = U(boltAnim);
            Eval(BoltGun, u, out p, out r); pos += p * free; rot += r * free;
            Eval(BoltSlide, u, out p, out r); bolt[w].localPosition = boltHome[w] + p;
            Eval(BoltHandle, u, out p, out r); handle[w].localEulerAngles = r;
        }
        else
        {
            if (mag[w]) { mag[w].localPosition = magHome[w]; mag[w].localEulerAngles = Vector3.zero; }
            if (bolt[w]) bolt[w].localPosition = boltHome[w];
            if (handle[w]) handle[w].localEulerAngles = Vector3.zero;
        }
        if (Playing(knifeAnim) && w == Knife)
        {
            Eval(heavyKnife ? Stab : Slash, U(knifeAnim), out p, out r);
            float s = knifeAnim.side;
            pos += new Vector3(p.x * s, p.y, p.z); rot += new Vector3(r.x, r.y * s, r.z * s);
        }
        if (switching)
        {
            float t = (Time.time - switchStart) / (raising ? (fastSwitch ? 0.14f : 0.3f) : (fastSwitch ? 0.08f : 0.17f));
            Eval(raising ? Raise : Lower, Mathf.Clamp01(t), out p, out r); pos += p; rot += r;
        }

        // against a wall: pull the gun back and tip the muzzle up
        float reach = w == Sniper ? 1.05f : w == AK ? 0.9f : 0.45f, d = reach;
        foreach (var h in Physics.SphereCastAll(cam.transform.position, 0.07f, cam.transform.forward, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (!h.collider.transform.IsChildOf(player.transform) && h.distance < d) d = h.distance;
        block = Mathf.Lerp(block, Mathf.Clamp01((reach - d) / (reach * 0.55f)), 1 - Mathf.Exp(-12 * dt));
        pos += new Vector3(-0.02f, -0.035f, -0.14f) * block; rot += new Vector3(-28, -10, 12) * block;

        holder[w].localPosition = pos;
        holder[w].localRotation = Quaternion.Euler(rot);

        // looking through the scope: the overlay takes over, hide the rifle
        bool scoped = w == Sniper && aimT > 0.88f && !Playing(boltAnim);
        foreach (var rr in renderers[w]) rr.enabled = !scoped;
        scopeHide = scoped ? 1 : 0;
    }
    public bool Scoped => scopeHide > 0.5f;

    static void Spring(ref Vector3 x, ref Vector3 v, float k, float damp, float dt)
    {
        float c = 2 * Mathf.Sqrt(k) * damp;
        // a few small steps so it stays stable at low frame rates
        int n = Mathf.CeilToInt(dt / 0.005f); float h = dt / n;
        for (int i = 0; i < n; i++) { v += (-k * x - c * v) * h; x += v * h; }
    }

    // muzzle flash: a burst of crossed star quads
    Transform BuildFlash(Transform at)
    {
        var g = new GameObject("Flash").transform; g.SetParent(at, false);
        var mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = FlashTex(128) };
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad); var qm = quad.GetComponent<MeshFilter>().sharedMesh; Destroy(quad);
        void Q(Vector3 p, Vector3 e, Vector3 s)
        {
            var q = new GameObject("q"); q.transform.SetParent(g, false); q.transform.localPosition = p; q.transform.localEulerAngles = e; q.transform.localScale = s;
            q.AddComponent<MeshFilter>().sharedMesh = qm; var r = q.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        Q(new Vector3(0, 0, 0.01f), Vector3.zero, Vector3.one * 0.11f);
        Q(new Vector3(0, 0, 0.06f), new Vector3(0, 90, 0), new Vector3(0.16f, 0.07f, 1));
        Q(new Vector3(0, 0, 0.06f), new Vector3(0, 90, 90), new Vector3(0.16f, 0.07f, 1));
        g.gameObject.SetActive(false);
        return g;
    }

    static Texture2D FlashTex(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var rnd = new System.Random(4); var spikes = new float[7]; for (int i = 0; i < 7; i++) spikes[i] = (float)rnd.NextDouble() * 6.28f;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            var d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f) / (n / 2f); float rr = d.magnitude, ang = Mathf.Atan2(d.y, d.x);
            float star = 0; foreach (var s in spikes) star = Mathf.Max(star, Mathf.Pow(Mathf.Max(0, Mathf.Cos(ang - s)), 40));
            float a = Mathf.Clamp01(1 - rr * 1.6f) + star * Mathf.Clamp01(1 - rr);
            var c = Color.Lerp(new Color(1f, 0.55f, 0.15f), new Color(1f, 0.95f, 0.75f), Mathf.Clamp01(1 - rr * 2.2f));
            c.a = Mathf.Clamp01(a); t.SetPixel(x, y, c);
        }
        t.Apply(); return t;
    }
}
