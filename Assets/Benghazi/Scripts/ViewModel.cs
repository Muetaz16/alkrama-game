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
    static readonly Vector3[] HipPos = { new(0.13f, -0.19f, 0.34f), new(0.14f, -0.195f, 0.33f), new(0.15f, -0.17f, 0.36f) };
    static readonly Vector3[] HipRot = { new(1f, -1.5f, 2f), new(1f, -1.5f, 2f), new(-28f, -14f, -22f) };
    static readonly float[] AdsDist = { 0.27f, 0.05f, 0f };
    float AdsDistance(int w) => w == AK && GunFactory.RealAK ? 0.21f : AdsDist[w];      // iron sights sit closer to the eye than a red dot
    Vector3 Hip(int w) => w == AK && GunFactory.RealAK ? new Vector3(0.13f, -0.2f, 0.37f) : HipPos[w];   // the real AK's stock is long: hold it further out

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
    float sprintT, crouchT, airT, block, lastYaw, lastPitch, time, scopeHide, lowT, closeT;
    [HideInInspector] public float combatAt;          // last time there was a threat / shot / aim (set by the weapon)
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
            if (i != Knife && mag[i])
            {
                var b = MagBox(mag[i]);
                magCentre[i] = b.center; magSize[i] = b.size;
                magFront[i] = new Vector3(b.center.x, b.max.y, b.max.z); magRear[i] = new Vector3(b.center.x, b.max.y, b.min.z);
                if (i == AK && GunFactory.RealAK) { magFront[i] = new Vector3(0, 0.032f, 0.205f); magRear[i] = new Vector3(0, 0.032f, 0.15f); magCentre[i] = new Vector3(0, -0.07f, 0.215f); }
                spare[i] = Instantiate(mag[i].gameObject, mag[i].parent, false).transform; spare[i].name = "Spare mag";
                spare[i].localPosition = magHome[i]; spare[i].localRotation = Quaternion.identity; spare[i].gameObject.SetActive(false);
            }
            renderers[i] = model[i].GetComponentsInChildren<Renderer>(true);
            holder[i].gameObject.SetActive(i == AK);
        }
        lastYaw = rig.yaw; lastPitch = rig.pitch;
        rockSign = (Quaternion.AngleAxis(10f, Vector3.right) * Vector3.down).z > 0 ? 1f : -1f;
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

    // ---------- magazine changes, the way special forces do them on an AK ----------
    // Both: rifle into the "workspace" (high, close, rolled so the magwell faces the support hand; eyes
    // stay up on the threat). The support hand brings the fresh mag from the pouch and uses it to push the
    // paddle release, stripping the old mag out; the fresh mag goes in front lug first and is rocked back.
    //   emergency (empty): the old mag drops to the ground; rifle rolled left, support hand racks the handle
    //   tactical (rounds left): a round stays chambered; the old mag is kept in the hand and stowed
    readonly Transform[] spare = new Transform[3];
    readonly Vector3[] magFront = new Vector3[3], magRear = new Vector3[3], magCentre = new Vector3[3], magSize = new Vector3[3];
    bool tacReload, dropped; int lhA, lhB; float lhK;
    Vector3 spOff, oldOff; float spAng, oldAng;
    static readonly Vector3 PouchCam = new(-0.12f, -0.46f, 0.12f);
    static float rockSign;
    public bool TacticalReload => tacReload;
    public string ReloadDebug()
    {
        int w = Shown; var ct = cam.transform;
        string P(Transform t) => t ? $"{t.name} on {t.gameObject.activeInHierarchy} cam-local {ct.InverseTransformPoint(t.position):F2} centre {ct.InverseTransformPoint(t.TransformPoint(magCentre[w] - magHome[w])):F2} parent {(t.parent ? t.parent.name : "-")}" : "none";
        return $"u {(Playing(reloadAnim) ? U(reloadAnim) : -1):F2} home {magHome[w]:F3} centre {magCentre[w]:F3} front {magFront[w]:F3} size {magSize[w]:F3} | mag {P(mag[w])} | spare {P(spare[w])}";
    }
    public void Reload(int w, float seconds, bool tactical) { tacReload = tactical; dropped = false; Play(reloadAnim, w, seconds); }

    static Bounds MagBox(Transform m)
    {
        var par = m.parent; bool any = false; var b = new Bounds();
        foreach (var mf in m.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.sharedMesh) continue;
            var mb = mf.sharedMesh.bounds; var M = par.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            for (int c = 0; c < 8; c++)
            {
                var p = M.MultiplyPoint3x4(new Vector3((c & 1) != 0 ? mb.max.x : mb.min.x, (c & 2) != 0 ? mb.max.y : mb.min.y, (c & 4) != 0 ? mb.max.z : mb.min.z));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        return b;
    }

    // a mag's pose: rotated about the magwell's front lug by ang (bottom swinging forward), then offset
    void PoseMag(Transform t, int w, float ang, Vector3 off)
    {
        var R = Quaternion.AngleAxis(ang * rockSign, Vector3.right);
        var P = magFront[w];
        t.localRotation = R; t.localPosition = P + R * (magHome[w] - P) + off;
    }

    static float Sm(float x, float a, float b) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(a, b, x));

    void ReloadPose(int w, float u, float a, ref Vector3 pos, ref Vector3 rot)
    {
        bool tac = tacReload;
        // rifle into the workspace
        float work = Sm(u, 0f, 0.1f) * (1 - Sm(u, tac ? 0.66f : 0.82f, tac ? 0.86f : 0.98f));
        pos += new Vector3(-0.04f, 0.035f, -0.09f) * work * (1 - a * 0.5f); rot += new Vector3(-8f, 6f, -24f) * work;     // magwell turned to the support hand
        if (!tac)
        {
            // charging handle up for the support hand
            float rk = Sm(u, 0.58f, 0.66f) * (1 - Sm(u, 0.8f, 0.9f));
            rot += new Vector3(0, -10f, 58f) * rk; pos += new Vector3(0.02f, 0.01f, 0) * rk;      // rolled over: charging handle up
        }
        if (!mag[w] || !spare[w]) return;
        var pouchL = mag[w].parent.InverseTransformPoint(cam.transform.TransformPoint(PouchCam));
        Vector3 pouchOff = pouchL - magCentre[w];
        // fresh mag: pouch -> behind the old one -> sweep forward on the paddle -> front lug in -> rocked home
        spare[w].gameObject.SetActive(u >= 0.12f);
        if (u < 0.3f) { float t = Sm(u, 0.12f, 0.3f); spOff = Vector3.Lerp(pouchOff, new Vector3(0, -0.055f, -0.04f), t); spAng = Mathf.Lerp(-15f, 0, t); }
        else if (u < 0.38f) { float t = Sm(u, 0.3f, 0.38f); spOff = Vector3.Lerp(new Vector3(0, -0.055f, -0.04f), new Vector3(0, -0.04f, 0.025f), t); spAng = 0; }
        else if (u < 0.46f) { float t = Sm(u, 0.38f, 0.46f); spOff = Vector3.Lerp(new Vector3(0, -0.04f, 0.025f), new Vector3(0, -0.012f, 0), t); spAng = 24f * t; }
        else if (u < 0.54f) { float t = Sm(u, 0.46f, 0.54f); spOff = Vector3.Lerp(new Vector3(0, -0.012f, 0), Vector3.zero, t); spAng = 24f * (1 - t); }
        else { spOff = Vector3.zero; spAng = 0; }
        PoseMag(spare[w], w, spAng, spOff);
        // old mag: stripped out by the fresh one; dropped (empty) or kept in the hand and stowed (tactical)
        if (u < 0.3f) { oldOff = Vector3.zero; oldAng = 0; }
        else if (u < 0.38f) { float t = Sm(u, 0.3f, 0.38f); oldOff = new Vector3(0, -0.03f, 0.01f) * t; oldAng = 24f * t; }
        else if (!tac)
        {
            if (!dropped) { dropped = true; DropMag(w); }
            mag[w].gameObject.SetActive(false);
        }
        else if (u < 0.54f) { oldOff = spOff + new Vector3(0, -0.04f, -0.07f); oldAng = spAng; }
        else if (u < 0.72f) { float t = Sm(u, 0.54f, 0.72f); oldOff = Vector3.Lerp(new Vector3(0, -0.04f, -0.07f), pouchOff, t); oldAng = 0; }
        else mag[w].gameObject.SetActive(false);
        if (mag[w].gameObject.activeSelf) PoseMag(mag[w], w, oldAng, oldOff);
        // charging handle (emergency)
        if (bolt[w])
        {
            float back = tac ? 0 : Sm(u, 0.7f, 0.75f) * (1 - Sm(u, 0.76f, 0.8f));
            bolt[w].localPosition = boltHome[w] + new Vector3(0, 0, -0.085f) * back;
        }
        // where the support hand is
        if (u < 0.12f) { lhA = 0; lhB = 1; lhK = Sm(u, 0f, 0.12f); }
        else if (u < 0.54f) { lhA = lhB = 2; lhK = 0; }
        else if (tac)
        {
            if (u < 0.72f) { lhA = lhB = 3; lhK = 0; }
            else { lhA = 1; lhB = 0; lhK = Sm(u, 0.72f, 0.86f); }
        }
        else
        {
            if (u < 0.66f) { lhA = 2; lhB = 4; lhK = Sm(u, 0.54f, 0.66f); }
            else if (u < 0.8f) { lhA = lhB = 4; lhK = 0; }
            else { lhA = 4; lhB = 0; lhK = Sm(u, 0.8f, 0.9f); }
        }
    }

    Vector3 LeftTarget(int w, int kind, Vector3 grip)
    {
        switch (kind)
        {
            case 1: return cam.transform.TransformPoint(PouchCam);
            // the hand holds a mag by its lower half, so the mag stands out of the fist
            case 2: return spare[w] && spare[w].gameObject.activeSelf ? spare[w].TransformPoint(magCentre[w] - magHome[w] + new Vector3(0, -0.075f, 0)) : grip;
            case 3: return mag[w] && mag[w].gameObject.activeSelf ? mag[w].TransformPoint(magCentre[w] - magHome[w] + new Vector3(0, -0.075f, 0)) : grip;
            case 4: return (handle[w] ? handle[w] : bolt[w] ? bolt[w] : model[w]).position + model[w].right * 0.03f;
            default: return grip;
        }
    }

    // the empty mag falls to the ground and stays there a while
    void DropMag(int w)
    {
        var m = mag[w];
        var go = Instantiate(m.gameObject); go.name = "Dropped mag";
        go.transform.SetPositionAndRotation(m.position, m.rotation); go.transform.localScale = m.lossyScale;
        go.layer = 2;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        var bc = go.AddComponent<BoxCollider>(); bc.center = magCentre[w] - magHome[w]; bc.size = Vector3.Max(magSize[w], Vector3.one * 0.02f);
        var cc = player.GetComponent<CharacterController>(); if (cc) Physics.IgnoreCollision(bc, cc);
        var rb = go.AddComponent<Rigidbody>(); rb.mass = 0.45f; rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.linearVelocity = cc ? cc.velocity * 0.8f : Vector3.zero; rb.linearVelocity += -cam.transform.up * 0.6f + cam.transform.forward * 0.2f;
        rb.angularVelocity = Random.insideUnitSphere * 4f;
        Destroy(go, 25f);
    }

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
            var ev = tacReload ? new[] { 0.33f, 0.53f, 0.7f } : new[] { 0.33f, 0.53f, 0.72f, 0.78f };
            var nm = tacReload ? new[] { "magout", "magin", "boltfwd" } : new[] { "magout", "magin", "boltback", "boltfwd" };
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
        // eyes, muzzle and target move together: the muzzle leads a fast turn a touch and the rifle rolls
        // into it with the body, instead of dragging behind like a loose prop
        var swayTarget = new Vector3(Mathf.Clamp(-pitchRate * 0.008f, -4, 4), Mathf.Clamp(yawRate * 0.005f, -3.5f, 3.5f), Mathf.Clamp(yawRate * 0.014f, -7, 7)) * swayK;
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

        // ready posture: low ready when nothing is happening (muzzle just under the line of sight), straight
        // up to the shoulder the moment there is a threat, a shot or the aim button
        bool calm = Time.time - combatAt > 2.5f && !sprint && w != Knife && !Playing(reloadAnim);
        lowT = Mathf.MoveTowards(lowT, calm ? 1 : 0, dt / (calm ? 0.7f : 0.12f));
        float low = Mathf.SmoothStep(0, 1, lowT) * free;
        pos += new Vector3(0.01f, -0.035f, -0.015f) * low; rot += new Vector3(13f, -4f, 4f) * low;

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
        bool reloadingW = Playing(reloadAnim) && reloadAnim.weapon == w;
        if (!reloadingW) { if (spare[w]) spare[w].gameObject.SetActive(false); if (mag[w] && !mag[w].gameObject.activeSelf) mag[w].gameObject.SetActive(true); }
        if (reloadingW) ReloadPose(w, U(reloadAnim), a, ref pos, ref rot);
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

        // close quarters: near a wall, a doorway or an enemy close in front, the rifle is pulled back into a
        // compressed ready (stock high in the shoulder, muzzle still level on the threat); only right against
        // the wall does the muzzle come up to high port
        float reach = w == Sniper ? 1.05f : w == AK ? 0.9f : 0.45f, d = reach;
        foreach (var h in Physics.SphereCastAll(cam.transform.position, 0.07f, cam.transform.forward, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (!h.collider.transform.IsChildOf(player.transform) && h.distance < d) d = h.distance;
        float near = 0;
        foreach (var e in EnemyAI.All)
        {
            if (!e || e.IsDead) continue;
            var to = e.transform.position - player.transform.position; float dd = to.magnitude;
            if (dd < 6f && dd > 0.1f && Vector3.Dot(to / dd, cam.transform.forward) > 0.3f) near = Mathf.Max(near, 1 - dd / 6f);
        }
        closeT = Mathf.Lerp(closeT, near, 1 - Mathf.Exp(-8 * dt));
        block = Mathf.Lerp(block, Mathf.Max(Mathf.Clamp01((reach - d) / (reach * 0.55f)), closeT * 0.55f), 1 - Mathf.Exp(-12 * dt));
        float highPort = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.8f, 1f, block));
        pos += new Vector3(0, 0, -0.17f) * block + new Vector3(-0.015f, 0.025f, 0) * block * free + new Vector3(0, -0.02f, 0.02f) * highPort;
        rot += new Vector3(-3f, -4f, 4f) * block * free + new Vector3(-30f, -8f, 10f) * highPort;

        holder[w].localPosition = pos;
        holder[w].localRotation = Quaternion.Euler(rot);

        // looking through the scope: the overlay takes over, hide the rifle
        bool scoped = w == Sniper && aimT > 0.88f && !Playing(boltAnim);
        foreach (var rr in renderers[w]) rr.enabled = !scoped;
        scopeHide = scoped ? 1 : 0;
        Arms(w, scoped);
    }

    // ---------- arms: the soldier's own sleeves and gloves, holding the gun ----------
    // A copy of the player's body sits just under the camera, turned with it; head and legs are folded away,
    // the torso stays below the view. Each frame both arms are bent (two-bone IK) so the right hand is on
    // the pistol grip and the left on the handguard (or the magazine during a reload), and each hand is
    // turned so it really wraps the grip.
    Transform arms, midR, idxR, litR, midL, idxL, litL;
    readonly Transform[] armR = new Transform[3], armL = new Transform[3];
    readonly Quaternion[] restR = new Quaternion[3], restL = new Quaternion[3];
    readonly Vector3[] gripR = new Vector3[3], gripL = new Vector3[3];
    Transform[][] fingR; Quaternion[][] fingRest;      // right hand: index, middle, ring, little, thumb x (proximal, intermediate, distal)

    public void BuildArms(Animator src)
    {
        if (!src || !root) return;
        var go = Instantiate(src.gameObject, root, true); go.name = "FP Arms";
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
            if (t && (t.name == "Rifle" || t.name == "Sniper" || t.name == "Knife")) { t.gameObject.SetActive(false); Destroy(t.gameObject); }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var a = go.GetComponent<Animator>(); if (!a) { Destroy(go); return; }
        Transform B(HumanBodyBones b) => a.GetBoneTransform(b);
        armR[0] = B(HumanBodyBones.RightUpperArm); armR[1] = B(HumanBodyBones.RightLowerArm); armR[2] = B(HumanBodyBones.RightHand);
        armL[0] = B(HumanBodyBones.LeftUpperArm); armL[1] = B(HumanBodyBones.LeftLowerArm); armL[2] = B(HumanBodyBones.LeftHand);
        midR = B(HumanBodyBones.RightMiddleProximal); idxR = B(HumanBodyBones.RightIndexProximal); litR = B(HumanBodyBones.RightLittleProximal);
        midL = B(HumanBodyBones.LeftMiddleProximal); idxL = B(HumanBodyBones.LeftIndexProximal); litL = B(HumanBodyBones.LeftLittleProximal);
        if (!armR[0] || !armR[2] || !armL[0] || !armL[2]) { Destroy(go); return; }
        // fold away what the camera must not see
        foreach (var b in new[] { HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
        { var t = B(b); if (t) t.localScale = Vector3.one * 0.001f; }
        var fb = new[] {
            new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
            new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal } };
        fingR = new Transform[5][]; fingRest = new Quaternion[5][];
        for (int i = 0; i < 5; i++)
        {
            fingR[i] = System.Array.ConvertAll(fb[i], B); fingRest[i] = new Quaternion[3];
            for (int j = 0; j < 3; j++) if (fingR[i][j]) fingRest[i][j] = fingR[i][j].localRotation;
        }
        a.enabled = false;
        for (int i = 0; i < 3; i++) { restR[i] = armR[i].localRotation; restL[i] = armL[i].localRotation; }
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = true;
            if (r is SkinnedMeshRenderer sk) sk.updateWhenOffscreen = true;
        }
        go.transform.localScale *= 1.34f;
        Debug.Log("[arms] renderers: " + string.Join(", ", System.Array.ConvertAll(go.GetComponentsInChildren<Renderer>(true), x => x.name)));                  // viewmodel arms are a little long, as in every shooter
        arms = go.transform;
        // where the hands go on each weapon (gun space)
        bool realAK = GunFactory.RealAK, realSn = GunFactory.Find(model[Sniper], "steel") != null;
        gripR[AK] = realAK ? new Vector3(0, -0.045f, -0.01f) : new Vector3(0, -0.05f, -0.09f);
        gripL[AK] = realAK ? new Vector3(0, 0.035f, 0.31f) : new Vector3(0, 0.01f, 0.3f);
        gripR[Sniper] = realSn ? new Vector3(0, -0.05f, 0.0f) : new Vector3(0, -0.06f, -0.1f);
        var gl = GunFactory.Find(model[Sniper], "GripL");
        gripL[Sniper] = gl ? model[Sniper].InverseTransformPoint(gl.position) : new Vector3(0, -0.01f, 0.25f);
        gripR[Knife] = new Vector3(0, 0, -0.005f);
    }

    void Arms(int w, bool scoped)
    {
        if (!arms) return;
        arms.gameObject.SetActive(!scoped);
        if (scoped) return;
        var ct = cam.transform;
        for (int i = 0; i < 3; i++) { armR[i].localRotation = restR[i]; armL[i].localRotation = restL[i]; }
        if (fingR != null) for (int i = 0; i < 5; i++) for (int j = 0; j < 3; j++) if (fingR[i][j]) fingR[i][j].localRotation = fingRest[i][j];
        // shoulders just under and in front of the eye, turned with the view
        arms.rotation = ct.rotation;
        var mid = (armR[0].position + armL[0].position) * 0.5f;
        arms.position += ct.position + ct.rotation * new Vector3(0, -0.34f, 0.03f) - mid;   // body well under the view

        var gm = model[w];
        Vector3 f = gm.forward, u = gm.up, r = gm.right;
        // right hand: on the pistol grip, index on the trigger
        if (w == Knife)
        {
            // the knife: the same closed fist as on the rifle grip, placed where the knife animation puts the
            // knife; then the knife is put into the fist (handle through it, blade out of the index side)
            var hf = holder[w]; Vector3 hfF = hf.forward, hfU = hf.up, hfR = hf.right;
            Vector3 kf = hfF * 0.85f - hfR * 0.35f, ka = hfU * 0.9f + hfF * 0.25f;
            Hand(armR, hf.position - kf.normalized * 0.07f, kf, ka, -ct.up + ct.right * 0.7f - ct.forward * 0.2f, midR, idxR, litR);
            if (midR && idxR && litR)
            {
                var h = armR[2]; Vector3 aF = (midR.position - h.position).normalized, aA = (idxR.position - litR.position).normalized;
                var palm = MakeFist(aF, aA);
                var centre = midR.position + palm * 0.03f - aF * 0.012f;
                gm.SetPositionAndRotation(centre, Quaternion.LookRotation(aA, -palm));
            }
        }
        else
        {
            Vector3 fingers = f * 0.85f - r * 0.35f, across = u * 0.9f + f * 0.25f;
            var gR = gm.TransformPoint(gripR[w]);
            var wristR = gR - fingers.normalized * 0.07f + r * 0.025f + u * 0.015f;
            Hand(armR, wristR, fingers, across, -ct.up + ct.right * 0.7f - ct.forward * 0.2f, midR, idxR, litR);
        }
        // left hand: under the handguard, palm up (none on the knife)
        armL[0].localScale = w == Knife ? Vector3.one * 0.001f : Vector3.one;
        if (w != Knife)
        {
            var gL = gm.TransformPoint(gripL[w]);
            if (Playing(reloadAnim) && reloadAnim.weapon == w)
                gL = Vector3.Lerp(LeftTarget(w, lhA, gL), LeftTarget(w, lhB, gL), lhK);
            var fingersL = r * 0.7f + u * 0.5f;
            var wristL = gL - fingersL.normalized * 0.065f - u * 0.03f - r * 0.01f;
            Hand(armL, wristL, fingersL, f, -ct.up - ct.right * 0.6f - ct.forward * 0.2f, midL, idxL, litL);
        }
    }

    // close the right hand into a fist (fingers bend about the knuckle line, towards the palm); returns the
    // direction from the back of the hand to the palm
    Vector3 MakeFist(Vector3 aF, Vector3 aA)
    {
        if (fingR == null || !fingR[1][0] || !fingR[1][2]) return Vector3.zero;
        var tip0 = fingR[1][2].position - fingR[1][0].position;          // the grip pose already bends the fingers a little
        var palm = Vector3.ProjectOnPlane(tip0, aF); palm = palm.sqrMagnitude > 1e-6f ? palm.normalized : Vector3.Cross(aA, aF).normalized;
        var axis = aA.normalized;
        float[] bend = { 62f, 78f, 55f };
        for (int i = 0; i < 4; i++)
        {
            var f = fingR[i]; if (!f[0]) continue;
            var tipT = f[2] ? f[2] : f[1] ? f[1] : f[0];
            // which way round bends towards the palm
            var before = f[0].rotation; f[0].rotation = Quaternion.AngleAxis(30f, axis) * before;
            float plus = Vector3.Dot(tipT.position - f[0].position, palm); f[0].rotation = Quaternion.AngleAxis(-30f, axis) * before;
            float minus = Vector3.Dot(tipT.position - f[0].position, palm); f[0].rotation = before;
            float sgn = plus >= minus ? 1 : -1;
            for (int j = 0; j < 3; j++) if (f[j]) f[j].rotation = Quaternion.AngleAxis(bend[j] * sgn, axis) * f[j].rotation;
        }
        // thumb wraps over the fingers
        var th = fingR[4];
        if (th[0] && th[1])
        {
            var before = th[1].rotation; th[1].rotation = Quaternion.AngleAxis(25f, aF) * before;
            float plus = Vector3.Dot((th[2] ? th[2] : th[1]).position - th[0].position, palm); th[1].rotation = Quaternion.AngleAxis(-25f, aF) * before;
            float minus = Vector3.Dot((th[2] ? th[2] : th[1]).position - th[0].position, palm); th[1].rotation = before;
            float sgn = plus >= minus ? 1 : -1;
            th[1].rotation = Quaternion.AngleAxis(35f * sgn, aF) * th[1].rotation;
            if (th[2]) th[2].rotation = Quaternion.AngleAxis(30f * sgn, aF) * th[2].rotation;
        }
        return palm;
    }

    void Hand(Transform[] a, Vector3 wrist, Vector3 fingers, Vector3 across, Vector3 pole, Transform mid, Transform idx, Transform lit)
    {
        TwoBone(a[0], a[1], a[2], wrist, pole);
        if (!mid || !idx || !lit) return;
        var h = a[2];
        Vector3 cF = (mid.position - h.position).normalized, cA = (idx.position - lit.position).normalized;
        Vector3 dF = fingers.normalized, dA = across.normalized;
        var cur = Quaternion.LookRotation(cF, Vector3.Cross(cF, cA));
        var want = Quaternion.LookRotation(dF, Vector3.Cross(dF, dA));
        h.rotation = want * Quaternion.Inverse(cur) * h.rotation;
    }

    static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole)
    {
        Vector3 pa = a.position, pb = b.position, pc = c.position;
        float l1 = (pb - pa).magnitude, l2 = (pc - pb).magnitude;
        var toT = target - pa;
        float dist = Mathf.Clamp(toT.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
        var dir = toT.normalized;
        float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist), -1, 1);
        var perp = pole - dir * Vector3.Dot(pole, dir); if (perp.sqrMagnitude < 1e-6f) perp = Vector3.down; perp.Normalize();
        var elbow = pa + dir * (l1 * cosA) + perp * (l1 * Mathf.Sqrt(1 - cosA * cosA));
        var hand = pa + dir * dist;
        a.rotation = Quaternion.FromToRotation(pb - pa, elbow - pa) * a.rotation;
        b.rotation = Quaternion.FromToRotation(c.position - b.position, hand - elbow) * b.rotation;
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
