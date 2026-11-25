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
    readonly Vector3[] sight = new Vector3[3], magHome = new Vector3[3], boltHome = new Vector3[3], handleAt = new Vector3[3];
    Renderer[][] renderers = new Renderer[3][];

    // hip poses (camera space) and how far in front of the eye the sight sits when aiming
    // Shouldered ready: the fighter stands square to the threat and the stock sits in the shoulder pocket
    // under the cheek, so the rifle runs almost straight out from under the eye - not canted across the
    // body with the butt out past the shoulder.
    static readonly Vector3[] HipPos = { new(0.078f, -0.15f, 0.32f), new(0.08f, -0.158f, 0.32f), new(0.15f, -0.17f, 0.36f) };
    static readonly Vector3[] HipRot = { new(0.5f, -0.8f, 1f), new(0.5f, -0.8f, 1f), new(-24f, -12f, -18f) };
    static readonly float[] AdsDist = { 0.27f, 0.05f, 0f };
    float AdsDistance(int w) => w == AK && GunFactory.RealAK ? 0.21f : AdsDist[w];      // iron sights sit closer to the eye than a red dot
    Vector3 Hip(int w) => w == AK && GunFactory.RealAK ? new Vector3(0.076f, -0.152f, 0.34f) : HipPos[w];   // the real AK's stock is long: hold it further out

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
    float sprintT, crouchT, proneT, airT, block, lastYaw, lastPitch, time, scopeHide, lowT, closeT;
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
            if (bolt[i])
            {
                boltHome[i] = bolt[i].localPosition;
                var hmf = bolt[i].GetComponentInChildren<MeshFilter>();
                handleAt[i] = hmf && hmf.sharedMesh ? bolt[i].InverseTransformPoint(hmf.transform.TransformPoint(hmf.sharedMesh.bounds.center)) : Vector3.zero;
            }
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
        lastFireT = Time.time;
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
    bool tacReload, dropped, rackJolt; int lhA, lhB; float lhK, rackLag;
    Vector3 spOff, oldOff; float spAng, oldAng;
    // the magazine pouches on the chest rig, in camera space: low on the support side, out of sight - you
    // watch the hand go down for the magazine and come back up with it. s = +1 the left hand is the working hand
    static Vector3 Pouch(float s) => new(-0.13f * s, -0.44f, 0.13f);
    float WorkSide => shoulderT >= 0 ? 1 : -1;
    static float rockSign;
    public bool TacticalReload => tacReload;
    // reload timeline (fractions of the whole reload); the sounds in Update() use the same numbers
    const float RHandOff = 0.02f, RAtPouch = 0.13f, RMagOut = 0.15f, RBehind = 0.32f, RStrip = 0.38f, RLug = 0.45f, RSeated = 0.52f,
        RUnder = 0.6f, ROnHandle = 0.67f, RPull = 0.69f, RBack = 0.755f, RHome = 0.78f, RHandBack = 0.8f, RUnderBack = 0.88f;
    public float BoltBack(int w) => bolt[w] ? (boltHome[w] - bolt[w].localPosition).z : 0;          // how far the charging handle is drawn, metres
    public string HandleDebug() { int w = Shown; return bolt[w] ? $"charging handle in gun space {model[w].InverseTransformPoint(bolt[w].TransformPoint(handleAt[w])):F3}, grip {gripR[w]:F3}" : "no bolt"; }
    public string ReloadDebug()
    {
        int w = Shown; var ct = cam.transform;
        string P(Transform t) => t ? $"{t.name} on {t.gameObject.activeInHierarchy} cam-local {ct.InverseTransformPoint(t.position):F2} centre {ct.InverseTransformPoint(t.TransformPoint(magCentre[w] - magHome[w])):F2} parent {(t.parent ? t.parent.name : "-")}" : "none";
        return $"u {(Playing(reloadAnim) ? U(reloadAnim) : -1):F2} home {magHome[w]:F3} centre {magCentre[w]:F3} front {magFront[w]:F3} size {magSize[w]:F3} | mag {P(mag[w])} | spare {P(spare[w])}";
    }
    public void Reload(int w, float seconds, bool tactical) { tacReload = tactical; dropped = false; rackJolt = false; rackLag = 0; Play(reloadAnim, w, seconds); }

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
    // a hand never moves in a straight line: out of the pouch and round to the rifle on an arc
    static Vector3 Arc(Vector3 a, Vector3 c, Vector3 b, float t) => (1 - t) * (1 - t) * a + 2 * t * (1 - t) * c + t * t * b;

    void ReloadPose(int w, float u, float a, ref Vector3 pos, ref Vector3 rot)
    {
        bool tac = tacReload; float side = WorkSide;
        // The firing hand never leaves the grip and the rifle stays in the shoulder, on the target: only a
        // small lift and roll of the magwell towards the working hand while the magazines change.
        float work = Sm(u, 0f, 0.1f) * (1 - Sm(u, tac ? 0.66f : RSeated + 0.02f, tac ? 0.86f : RUnder + 0.04f));
        pos += new Vector3(-0.012f, 0.012f, -0.03f) * work * (1 - a * 0.5f); rot += new Vector3(-3f, 2.5f, -9f) * work;
        float back = 0;
        if (!tac)
        {
            // Then the working hand goes UNDER the rifle to the charging handle on the far side, while the
            // rifle is canted about twenty degrees outboard (top away from the working hand) so the handle
            // comes down to meet it; one full pull to the rear and let it go (never ride it forward), and
            // the hand goes back the way it came, under the rifle, to the handguard. The rifle rocks with the pull.
            float rk = Sm(u, RSeated + 0.02f, ROnHandle) * (1 - Sm(u, RHandBack, RUnderBack + 0.04f)) * Mathf.Clamp01(shoulderT * 2f);
            rot += new Vector3(-1.5f, 1.5f, -20f) * rk; pos += new Vector3(-0.006f, 0.008f, -0.006f) * rk;
            float pull = Mathf.InverseLerp(RPull, RBack, u); pull = pull * pull * (3 - 2 * pull);
            back = pull * (1 - Mathf.InverseLerp(RBack, RHome, u));
            pos += new Vector3(0, 0, -0.014f) * back; rot += new Vector3(-1.5f, 0, 0) * back;
            rackLag = Sm(u, RBack, RHome);                                   // the hand lets go: it stays back while the bolt flies home
            if (u >= RHome && !rackJolt) { rackJolt = true; recPV += new Vector3(0, -0.05f, 0.5f); recRV += new Vector3(40f, 0, 0); }   // bolt slams home
        }
        if (!mag[w] || !spare[w]) return;
        var pouchL = mag[w].parent.InverseTransformPoint(cam.transform.TransformPoint(Pouch(side)));
        Vector3 pouchOff = pouchL - magCentre[w];
        Vector3 behind = new(0, -0.055f, -0.04f), onPaddle = new(0, -0.04f, 0.025f), lugIn = new(0, -0.012f, 0);
        var round = (pouchOff + behind) * 0.5f + new Vector3(-0.09f * side, -0.05f, -0.05f);      // out on the working side, under the rifle
        // fresh mag: out of the pouch -> round behind the old one -> sweep forward on the paddle -> front lug in -> rocked home
        spare[w].gameObject.SetActive(u >= RMagOut);
        if (u < RBehind) { float t = Sm(u, RMagOut, RBehind); spOff = Arc(pouchOff, round, behind, t); spAng = Mathf.Lerp(-22f, 0, t); }
        else if (u < RStrip) { float t = Sm(u, RBehind, RStrip); spOff = Vector3.Lerp(behind, onPaddle, t); spAng = 0; }
        else if (u < RLug) { float t = Sm(u, RStrip, RLug); spOff = Vector3.Lerp(onPaddle, lugIn, t); spAng = 24f * t; }
        else if (u < RSeated) { float t = Sm(u, RLug, RSeated); spOff = Vector3.Lerp(lugIn, Vector3.zero, t); spAng = 24f * (1 - t); }
        else { spOff = Vector3.zero; spAng = 0; }
        PoseMag(spare[w], w, spAng, spOff);
        // old mag: stripped out by the fresh one; dropped (empty) or kept in the hand and stowed (tactical)
        if (u < RBehind) { oldOff = Vector3.zero; oldAng = 0; }
        else if (u < RStrip) { float t = Sm(u, RBehind, RStrip); oldOff = new Vector3(0, -0.03f, 0.01f) * t; oldAng = 24f * t; }
        else if (!tac)
        {
            if (!dropped) { dropped = true; DropMag(w); }
            mag[w].gameObject.SetActive(false);
        }
        else if (u < RSeated) { oldOff = spOff + new Vector3(0, -0.04f, -0.07f); oldAng = spAng; }
        else if (u < 0.72f) { float t = Sm(u, RSeated, 0.72f); oldOff = Arc(new Vector3(0, -0.04f, -0.07f), round, pouchOff, t); oldAng = -22f * t; }
        else mag[w].gameObject.SetActive(false);
        if (mag[w].gameObject.activeSelf) PoseMag(mag[w], w, oldAng, oldOff);
        if (bolt[w]) bolt[w].localPosition = boltHome[w] + new Vector3(0, 0, -0.085f) * back;
        // what the working hand is doing (see Support): 0 handguard, 1 pouch, 2 fresh mag, 3 old mag, 4 charging handle
        if (u < RAtPouch) { lhA = 0; lhB = 1; lhK = Sm(u, RHandOff, RAtPouch); }
        else if (u < 0.2f) { lhA = 1; lhB = 2; lhK = Sm(u, RMagOut, 0.2f); }
        else if (u < RSeated) { lhA = lhB = 2; lhK = 0; }
        else if (tac)
        {
            if (u < 0.72f) { lhA = lhB = 3; lhK = 0; }
            else { lhA = 1; lhB = 0; lhK = Sm(u, 0.72f, 0.86f); }
        }
        else if (side < 0)
        {
            // Rifle in the left shoulder: the working hand is the right one and the charging handle is on its
            // own side of the rifle. No going underneath - the hand that seated the magazine goes straight up
            // to the handle, racks it and returns to the handguard. An AK does not hold its bolt open on an
            // empty magazine, so it will not fire until this is done.
            if (u < ROnHandle) { lhA = 2; lhB = 4; lhK = Sm(u, RSeated, ROnHandle - 0.03f); }
            else if (u < RHandBack) { lhA = lhB = 4; lhK = 0; }
            else { lhA = 4; lhB = 0; lhK = Sm(u, RHandBack, 0.95f); }
        }
        else
        {
            if (u < RUnder) { lhA = 2; lhB = 5; lhK = Sm(u, RSeated, RUnder); }                 // lets go of the seated mag, drops under the rifle
            else if (u < ROnHandle) { lhA = 5; lhB = 4; lhK = Sm(u, RUnder, ROnHandle); }        // up the far side onto the handle
            else if (u < RHandBack) { lhA = lhB = 4; lhK = 0; }
            else if (u < RUnderBack) { lhA = 4; lhB = 5; lhK = Sm(u, RHandBack, RUnderBack); }   // back the way it came
            else { lhA = 5; lhB = 0; lhK = Sm(u, RUnderBack, 0.97f); }
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

    // Knife work. Two grips:
    //   forward (hammer) grip - blade out of the thumb side: horizontal cuts, a downward diagonal cut, a straight thrust
    //   reverse (ice-pick) grip - blade out of the little-finger side, edge out: the overhand stab downwards
    //   and a backhand rip. The knife is turned in the hand during the wind-up and turned back afterwards.
    // The weapon picks the attack from what is in front (see WeaponController.Knife).
    public const int CutAcross = 0, CutDown = 1, Thrust = 2, StabDown = 3, RipBack = 4;
    static readonly float[] KnifeLen = { 0.42f, 0.5f, 0.6f, 0.72f, 0.6f }, KnifeHitAt = { 0.4f, 0.46f, 0.48f, 0.52f, 0.5f };
    static bool ReverseGrip(int kind) => kind >= StabDown;
    public static bool Heavy(int kind) => kind >= Thrust;
    public static float KnifeTime(int kind) => KnifeLen[kind];
    public int LastKnife => knifeKind;
    int knifeKind; float reverseT;
    public void KnifeAttack(int kind)
    {
        knifeKind = Mathf.Clamp(kind, 0, 4);
        knifeAnim.side = knifeKind == CutAcross ? -knifeAnim.side : 1;
        if (knifeAnim.side == 0) knifeAnim.side = 1;
        heavyKnife = Heavy(knifeKind);
        Play(knifeAnim, Knife, KnifeLen[knifeKind]);
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
    // downward diagonal cut: up by the shoulder, down across the body
    static readonly Key[] SlashDown = { Zero0, K(0.3f, 0.03f, 0.13f, -0.06f, -42, 14, 34, 1), K(0.5f, -0.13f, -0.1f, 0.09f, 34, -38, -30, 3), K(0.63f, -0.14f, -0.11f, 0.07f, 37, -41, -33, 1), Zero1 };
    // reverse grip, overhand: fist up beside the head, driven down and in
    static readonly Key[] StabDownKeys = { Zero0, K(0.36f, -0.03f, 0.19f, -0.1f, -58, -4, 12, 1), K(0.52f, -0.07f, -0.08f, 0.17f, 46, 6, 4, 3), K(0.66f, -0.07f, -0.09f, 0.16f, 48, 6, 4), Zero1 };
    // reverse grip, backhand rip: across from the support side
    static readonly Key[] RipBackKeys = { Zero0, K(0.34f, -0.2f, 0.06f, -0.03f, -10, -46, 20, 1), K(0.5f, 0.05f, 0.02f, 0.12f, 4, 34, -18, 3), K(0.63f, 0.06f, 0.02f, 0.11f, 5, 37, -20, 1), Zero1 };
    static Key[] KnifeKeys(int kind) => kind == CutDown ? SlashDown : kind == Thrust ? Stab : kind == StabDown ? StabDownKeys : kind == RipBack ? RipBackKeys : Slash;
    static readonly Key[] Lower = { Zero0, K(1, 0, -0.22f, -0.05f, 40, 0, -10, 2) };
    static readonly Key[] Raise = { K(0, 0, -0.22f, -0.05f, 40, 0, -10), K(0.75f, 0, 0.005f, 0.002f, -3, 0, 1, 1), Zero1 };

    void Update()
    {
        if (!root) return;
        // events in the animations (sounds, the knife hit, the case out of the sniper)
        if (Playing(reloadAnim))
        {
            float u = U(reloadAnim);
            var ev = tacReload ? EvTac : EvEmpty;
            var nm = tacReload ? NmTac : NmEmpty;
            while (reloadAnim.fired < ev.Length && u >= ev[reloadAnim.fired]) onSound?.Invoke(nm[reloadAnim.fired++]);
        }
        if (Playing(boltAnim))
        {
            float u = U(boltAnim);
            if (boltAnim.fired == 0 && u > 0.18f) { boltAnim.fired = 1; onSound?.Invoke("boltback"); }
            if (boltAnim.fired == 1 && u > 0.42f) { boltAnim.fired = 2; onEject?.Invoke(); }
            if (boltAnim.fired == 2 && u > 0.62f) { boltAnim.fired = 3; onSound?.Invoke("boltfwd"); }
        }
        if (Playing(knifeAnim) && knifeAnim.fired == 0 && U(knifeAnim) > KnifeHitAt[knifeKind]) { knifeAnim.fired = 1; onKnifeHit?.Invoke(heavyKnife); }
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

    static readonly float[] EvTac = { RBehind + 0.02f, RSeated - 0.01f, 0.7f }, EvEmpty = { RBehind + 0.02f, RSeated - 0.01f, RPull + 0.01f, RHome };
    public const float ReloadRackAt = RBack - 0.01f, ReloadUnderAt = RUnder, ReloadStripAt = RStrip - 0.01f;      // for the test's screenshots
    static readonly string[] NmTac = { "magout", "magin", "boltfwd" }, NmEmpty = { "magout", "magin", "boltback", "boltfwd" };

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
        pos += new Vector3(Mathf.Sin(ph) * 0.008f, Mathf.Sin(ph * 2) * 0.0055f - 0.004f * k, -0.006f * k) * bobK;
        rot += new Vector3(Mathf.Sin(ph * 2) * 0.7f, Mathf.Sin(ph) * 0.55f, Mathf.Sin(ph) * 1.2f) * bobK;
        // Sprint across open ground: the rifle is carried by the firing hand alone, muzzle up, the stock
        // against the side of the face with its heel on the shoulder; the other hand is off the rifle, down
        // by the body where the spare magazines are, pumping with the stride. (Knife: pumped low.)
        if (w == Knife) { pos += new Vector3(-0.01f, -0.03f, -0.02f) * sp; rot += new Vector3(22, 0, 8) * sp; }
        else { pos += new Vector3(0.15f, -0.075f, -0.1f) * sp; rot += new Vector3(-54f, 7f, 12f) * sp; rot += new Vector3(Mathf.Sin(ph * 2) * 2.5f, 0, Mathf.Sin(ph) * 3f) * sp; }

        // ready posture: low ready when nothing is happening (muzzle just under the line of sight), straight
        // up to the shoulder the moment there is a threat, a shot or the aim button
        bool calm = Time.time - combatAt > 2.5f && !sprint && w != Knife && !Playing(reloadAnim);
        lowT = Mathf.MoveTowards(lowT, calm ? 1 : 0, dt / (calm ? 0.7f : 0.12f));
        float low = Mathf.SmoothStep(0, 1, lowT) * free;
        pos += new Vector3(0.008f, -0.022f, -0.01f) * low; rot += new Vector3(7.5f, -3f, 3f) * low;

        // strafe tilt, crouch cant, jump lift, landing dip
        rot.z += player.Strafe * 6f * free;
        crouchT = Mathf.Lerp(crouchT, player.crouch && !player.prone ? 1 : 0, 1 - Mathf.Exp(-8 * dt));
        // prone: elbows on the ground, the rifle level and close under the eye, hardly any movement in it
        proneT = Mathf.Lerp(proneT, player.prone ? 1 : 0, 1 - Mathf.Exp(-5 * dt));
        pos += new Vector3(-0.02f, 0.03f, -0.02f) * proneT * free; rot += new Vector3(-1f, 1.5f, -2f) * proneT * free;
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
            Eval(KnifeKeys(knifeKind), U(knifeAnim), out p, out r);
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

        // shoulder transition: the rifle crosses to the other shoulder (support-side corner). Everything above
        // is mirrored, so the stance, the reload and the ready postures all work from either side; on the
        // way across the muzzle dips under the line of sight and the hands change jobs (see Arms)
        shoulderT = Mathf.MoveTowards(shoulderT, leftShoulder && w != Knife ? -1 : 1, dt / 0.15f);
        if (w != Knife && shoulderT < 0.999f)
        {
            float mir = Mathf.SmoothStep(0, 1, (1 - shoulderT) * 0.5f), across = Mathf.Sin(mir * Mathf.PI);
            pos = Vector3.Lerp(pos, new Vector3(-pos.x, pos.y, pos.z), mir) + new Vector3(0, -0.03f, -0.035f) * across * free;
            rot = Vector3.Lerp(rot, new Vector3(rot.x, -rot.y, -rot.z), mir) + new Vector3(8f, 0, 0) * across * free;
        }

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
    // the torso stays below the view. Each frame both arms are bent (two-bone IK) onto the weapon and every
    // finger is curled for what that hand is doing:
    //   firing hand   high on the pistol grip, three fingers and the thumb wrapped round it, the index
    //                 finger straight along the receiver until the rifle is on a target (trigger discipline)
    //   support hand  wrapped round the handguard from underneath, thumb forward along its side; during a
    //                 reload it is the working hand: open at the pouch, closed round a magazine, hooked
    //                 over the charging handle
    // Either hand can do either job: on the support-side shoulder (left corner) the roles swap.
    Transform arms;
    readonly Transform[][] arm = { new Transform[3], new Transform[3] };          // [0 right, 1 left][upper, lower, hand]
    readonly Quaternion[][] armRest = { new Quaternion[3], new Quaternion[3] };
    readonly Transform[] midH = new Transform[2], idxH = new Transform[2], litH = new Transform[2];
    readonly Transform[][][] fing = new Transform[2][][];                          // [hand][index, middle, ring, little, thumb][3 joints]
    readonly Quaternion[][][] fingRest = new Quaternion[2][][];
    readonly Vector3[] gripR = new Vector3[3], gripL = new Vector3[3];
    const float ArmScale = 1.34f, HandScale = 0.88f;       // viewmodel arms are a little long, as in every shooter; the gloves are not
    [HideInInspector] public bool leftShoulder;            // rifle on the support-side shoulder (set by the weapon: left lean / X)
    float shoulderT = 1, trigT, lastFireT = -9;            // +1 strong side .. -1 support side
    public float ShoulderSide => shoulderT;

    struct Grip
    {
        public Vector3 wrist, pole; public Quaternion rot; public float i, m, r, l, t;
        public Vector3 pad; public float padW;          // where the pad of the index finger goes (trigger / along the frame), and how much
        public static Grip Mix(Grip a, Grip b, float k) => new Grip
        {
            wrist = Vector3.Lerp(a.wrist, b.wrist, k), pole = Vector3.Lerp(a.pole, b.pole, k), rot = Quaternion.Slerp(a.rot, b.rot, k),
            i = Mathf.Lerp(a.i, b.i, k), m = Mathf.Lerp(a.m, b.m, k), r = Mathf.Lerp(a.r, b.r, k), l = Mathf.Lerp(a.l, b.l, k), t = Mathf.Lerp(a.t, b.t, k),
            pad = a.padW > 0 && b.padW > 0 ? Vector3.Lerp(a.pad, b.pad, k) : a.padW > 0 ? a.pad : b.pad, padW = Mathf.Lerp(a.padW, b.padW, k)
        };
    }
    // a hand's attitude from two things you can see on it: where the fingers point and the knuckle line (little -> index)
    static Quaternion Basis(Vector3 fingers, Vector3 across) { var f = fingers.normalized; return Quaternion.LookRotation(f, Vector3.Cross(f, across.normalized)); }

    public void BuildArms(Animator src)
    {
        if (!src || !root) return;
        var go = Instantiate(src.gameObject, root, true); go.name = "FP Arms";
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
            if (t && (t.name == "Rifle" || t.name == "Sniper" || t.name == "Knife")) { t.gameObject.SetActive(false); Destroy(t.gameObject); }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var a = go.GetComponent<Animator>(); if (!a) { Destroy(go); return; }
        Transform B(HumanBodyBones b) => a.GetBoneTransform(b);
        arm[0][0] = B(HumanBodyBones.RightUpperArm); arm[0][1] = B(HumanBodyBones.RightLowerArm); arm[0][2] = B(HumanBodyBones.RightHand);
        arm[1][0] = B(HumanBodyBones.LeftUpperArm); arm[1][1] = B(HumanBodyBones.LeftLowerArm); arm[1][2] = B(HumanBodyBones.LeftHand);
        midH[0] = B(HumanBodyBones.RightMiddleProximal); idxH[0] = B(HumanBodyBones.RightIndexProximal); litH[0] = B(HumanBodyBones.RightLittleProximal);
        midH[1] = B(HumanBodyBones.LeftMiddleProximal); idxH[1] = B(HumanBodyBones.LeftIndexProximal); litH[1] = B(HumanBodyBones.LeftLittleProximal);
        if (!arm[0][0] || !arm[0][2] || !arm[1][0] || !arm[1][2]) { Destroy(go); return; }
        // fold away what the camera must not see
        foreach (var b in new[] { HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
        { var t = B(b); if (t) t.localScale = Vector3.one * 0.001f; }
        var fb = new[] {
            new[] { new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
                    new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
                    new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
                    new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
                    new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal } },
            new[] { new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
                    new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
                    new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
                    new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
                    new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal } } };
        for (int h = 0; h < 2; h++)
        {
            fing[h] = new Transform[5][]; fingRest[h] = new Quaternion[5][];
            for (int i = 0; i < 5; i++)
            {
                fing[h][i] = System.Array.ConvertAll(fb[h][i], B); fingRest[h][i] = new Quaternion[3];
                for (int j = 0; j < 3; j++) if (fing[h][i][j]) fingRest[h][i][j] = fing[h][i][j].localRotation;
            }
        }
        a.enabled = false;
        for (int h = 0; h < 2; h++) for (int i = 0; i < 3; i++) armRest[h][i] = arm[h][i].localRotation;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = true;
            if (r is SkinnedMeshRenderer sk) sk.updateWhenOffscreen = true;
        }
        go.transform.localScale *= ArmScale;
        arm[0][2].localScale = arm[1][2].localScale = Vector3.one * HandScale;
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

    // the firing hand: s = +1 it is the right hand (rifle in the right shoulder), -1 the left
    Grip Firing(int w, float s)
    {
        var gm = model[w]; var ct = cam.transform; Vector3 f = gm.forward, u = gm.up, rs = gm.right * s;
        Vector3 fingers = f * 0.85f - rs * 0.35f, across = u * 0.9f + f * 0.25f;
        // Trigger finger. Off the trigger it lies straight along the frame above the trigger guard. On it,
        // only the pad of the first joint (the fingertip pad, between the tip and the first crease) touches
        // the trigger - never the middle of the finger: that is what lets the trigger come straight back
        // without pushing the muzzle sideways. Both are points on the rifle the fingertip is taken to.
        var g0 = gm.TransformPoint(gripR[w]);
        var onFrame = g0 + f * 0.088f + u * 0.076f + rs * 0.019f;         // on the receiver, above the trigger guard
        var onTrigger = g0 + f * 0.07f + u * 0.06f - rs * 0.003f;         // the face of the trigger blade, inside the guard
        return new Grip
        {
            // a high grip: the web of the hand up under the receiver, far enough back that the index finger
            // reaches the trigger with its pad instead of wrapping past it
            wrist = g0 - fingers.normalized * 0.062f + rs * 0.028f + u * 0.032f - f * 0.022f, rot = Basis(fingers, across),
            pole = -ct.up + ct.right * 0.7f * s - ct.forward * 0.2f,
            i = 0.05f, m = 0.9f, r = 0.95f, l = 1f, t = 0.55f,
            pad = Vector3.Lerp(onFrame, onTrigger, Mathf.SmoothStep(0, 1, trigT)), padW = 1
        };
    }

    // the support hand, and during a reload the working hand (kind: 0 handguard, 1 pouch, 2 fresh mag, 3 old mag, 4 charging handle, 5 passing under the rifle)
    Grip Support(int w, float s, int kind)
    {
        var gm = model[w]; var ct = cam.transform; Vector3 f = gm.forward, u = gm.up, rs = gm.right * s;
        var g = new Grip { pole = -ct.up - ct.right * 0.6f * s - ct.forward * 0.2f };
        var held = kind == 2 ? spare[w] : kind == 3 ? mag[w] : null;
        if (kind == 1)
        {
            // reaching into the mag pouch on the chest rig: hand turned to take the magazine, fingers opening
            g.wrist = ct.TransformPoint(Pouch(s)) - ct.forward * 0.05f - ct.right * 0.03f * s + ct.up * 0.02f;
            g.rot = Basis(ct.forward * 0.7f - ct.up * 0.6f + ct.right * 0.25f * s, ct.up + ct.forward * 0.5f);
            g.i = g.m = g.r = g.l = 0.35f; g.t = 0.15f;
        }
        else if (held && held.gameObject.activeSelf)
        {
            // a magazine in the fist: gripped low on its body from the support side, index finger up its front
            var c = held.TransformPoint(magCentre[w] - magHome[w] + new Vector3(0, -0.06f, 0));
            Vector3 hf = held.forward, hu = held.up;
            g.wrist = c - hf * 0.055f - rs * 0.03f - hu * 0.01f; g.rot = Basis(hf * 0.95f + rs * 0.15f, hu);
            g.i = 0.7f; g.m = 0.85f; g.r = 0.9f; g.l = 0.9f; g.t = 0.5f;
        }
        else if (kind == 4)
        {
            // come up from under the rifle on the handle's side: hand upright beside the receiver, palm to
            // the rifle, the first two fingers hooked over the charging handle from in front
            var hd = (handle[w] ? handle[w].position : bolt[w] ? bolt[w].TransformPoint(handleAt[w]) : gm.position) + gm.right * 0.012f - f * 0.085f * rackLag - u * 0.03f * rackLag;
            Vector3 fingers = u * 0.9f - gm.right * 0.25f + f * 0.1f;
            g.wrist = hd - fingers.normalized * 0.08f + gm.right * 0.03f + f * 0.012f; g.rot = Basis(fingers, f * s);
            g.i = 0.75f; g.m = 0.75f; g.r = 0.6f; g.l = 0.55f; g.t = 0.25f;
        }
        else if (kind == 6)
        {
            // sprinting: off the rifle, down beside the body at the spare magazines, swinging with the stride
            float sw = Mathf.Sin(player.StepPhase);
            g.wrist = ct.TransformPoint(Pouch(s) + new Vector3(-0.05f * s, 0.03f, 0.05f)) + ct.forward * sw * 0.08f + ct.up * Mathf.Abs(sw) * 0.02f;
            g.rot = Basis(ct.forward * 0.8f - ct.up * 0.55f + ct.right * 0.1f * s, ct.up * 0.9f + ct.forward * 0.4f);
            g.i = g.m = g.r = g.l = 0.55f; g.t = 0.3f;
        }
        else if (kind == 5)
        {
            // on the way under the rifle: below the magazine, hand open, turning palm-up
            Vector3 fingers = gm.right * 0.75f * s + f * 0.35f + u * 0.2f;
            g.wrist = gm.TransformPoint(magCentre[w]) - u * 0.2f - rs * 0.05f + f * 0.02f; g.rot = Basis(fingers, f * 0.8f - u * 0.3f);
            g.i = g.m = g.r = g.l = 0.3f; g.t = 0.15f;
        }
        else
        {
            // wrapped round the handguard from underneath, thumb forward along its side
            Vector3 fingers = rs * 0.72f + u * 0.5f;
            g.wrist = gm.TransformPoint(gripL[w]) - fingers.normalized * 0.058f - u * 0.032f - rs * 0.012f; g.rot = Basis(fingers, f);
            g.i = 0.88f; g.m = 0.96f; g.r = 0.96f; g.l = 0.92f; g.t = 0.45f;
        }
        return g;
    }

    void Arms(int w, bool scoped)
    {
        if (!arms) return;
        arms.gameObject.SetActive(!scoped);
        if (scoped) return;
        var ct = cam.transform; float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        for (int h = 0; h < 2; h++)
        {
            for (int i = 0; i < 3; i++) arm[h][i].localRotation = armRest[h][i];
            for (int i = 0; i < 5; i++) for (int j = 0; j < 3; j++) if (fing[h][i][j]) fing[h][i][j].localRotation = fingRest[h][i][j];
        }
        // shoulders just under and in front of the eye, turned with the view. The body copy stands bladed the
        // way a right-handed shooter does (support shoulder forward); on the support-side shoulder the
        // stance is the mirror of that, or the new support arm could not reach the handguard
        float k = Mathf.SmoothStep(0, 1, (shoulderT + 1) * 0.5f);       // 1 the right hand fires, 0 the left one does
        arms.rotation = ct.rotation;
        var line = ct.InverseTransformVector(arm[1][0].position - arm[0][0].position);
        float blade = Mathf.Atan2(line.z, -line.x) * Mathf.Rad2Deg;
        // the mocap pose is bladed (support shoulder forward): turn the body so both shoulders are square to
        // the target, the same from either shoulder
        arms.rotation = ct.rotation * Quaternion.Euler(0, -blade, 0);
        var mid = (arm[0][0].position + arm[1][0].position) * 0.5f;
        arms.position += ct.position + ct.rotation * new Vector3(0, -0.34f, 0.105f) - mid;  // body well under the view

        // finger on the trigger only when the rifle is on a target or firing
        bool onTarget = aimT > 0.6f || Time.time - lastFireT < 0.45f;
        trigT = Mathf.MoveTowards(trigT, onTarget && !Playing(reloadAnim) && !switching ? 1 : 0, dt / 0.09f);

        var gm = model[w];
        arm[1][0].localScale = w == Knife ? Vector3.one * 0.001f : Vector3.one;      // the knife is a one-hand job
        if (w == Knife)
        {
            // the knife: a closed fist placed where the knife animation puts the knife; then the knife is put
            // into the fist (handle through it, blade out of the index side)
            var hf = holder[w]; Vector3 hfF = hf.forward, hfU = hf.up, hfR = hf.right;
            Vector3 kf = hfF * 0.85f - hfR * 0.35f, ka = hfU * 0.9f + hfF * 0.25f;
            // which grip: the knife is turned in the hand during the wind-up of a reverse-grip attack and
            // turned back as the arm recovers
            bool attacking = Playing(knifeAnim); float ku = attacking ? U(knifeAnim) : 1;
            float wantRev = attacking && ReverseGrip(knifeKind) && ku < 0.8f ? 1 : 0;
            reverseT = Mathf.MoveTowards(reverseT, wantRev, dt / 0.13f);
            float rv = Mathf.SmoothStep(0, 1, reverseT), turning = Mathf.Sin(rv * Mathf.PI);
            // a full, hard fist: the handle is locked by the last three fingers, the index closes round it under
            // the guard; forward grip the thumb lies along the spine side, reverse grip it caps the pommel.
            // While the knife turns the fingers open just enough to let it through.
            float open = turning * 0.35f, sq = attacking ? 1f : 0.94f;
            Place(0, new Grip { wrist = hf.position - kf.normalized * 0.07f, rot = Basis(kf, ka), pole = -ct.up + ct.right * 0.7f - ct.forward * 0.2f,
                i = (0.96f - open) * sq, m = (1f - open) * sq, r = 1f * sq - open * 0.5f, l = 1f * sq - open * 0.3f, t = Mathf.Lerp(0.72f, 1f, rv) - open });
            if (midH[0] && idxH[0] && litH[0])
            {
                var h = arm[0][2]; Vector3 aF = (midH[0].position - h.position).normalized, aA = (idxH[0].position - litH[0].position).normalized;
                var palm = Vector3.Cross(aF, aA).normalized;
                var centre = midH[0].position + palm * 0.03f - aF * 0.012f;
                // blade out of the thumb side (forward grip); half a turn about the palm puts it out of the little-finger side
                gm.SetPositionAndRotation(centre, Quaternion.AngleAxis(180f * rv, palm) * Quaternion.LookRotation(aA, -palm));
            }
            return;
        }
        // which hand does what: k = 1 the right hand fires, 0 the left one does (support-side shoulder)
        bool reloading = Playing(reloadAnim) && reloadAnim.weapon == w;
        float free = Mathf.SmoothStep(0, 1, sprintT);
        Grip Sup(float s) => reloading ? Grip.Mix(Support(w, s, lhA), Support(w, s, lhB), lhK) : free > 0.001f ? Grip.Mix(Support(w, s, 0), Support(w, s, 6), free) : Support(w, s, 0);
        Place(0, k > 0.999f ? Firing(w, 1) : k < 0.001f ? Sup(-1) : Grip.Mix(Sup(-1), Firing(w, 1), k));
        Place(1, k > 0.999f ? Sup(1) : k < 0.001f ? Firing(w, -1) : Grip.Mix(Firing(w, -1), Sup(1), k));
    }

    // how far each wrist ended up from where its grip wants it (0 = the arm reached)
    public string ArmsDebug() => $"wrist miss right {wristMiss[0] * 100:F1} cm, left {wristMiss[1] * 100:F1} cm";
    readonly float[] wristMiss = new float[2];

    // put a hand on its grip: arm bent onto the wrist point, hand turned, fingers curled
    void Place(int h, Grip g)
    {
        var a = arm[h];
        TwoBone(a[0], a[1], a[2], g.wrist, g.pole);
        wristMiss[h] = Vector3.Distance(a[2].position, g.wrist);
        if (!midH[h] || !idxH[h] || !litH[h]) return;
        var hand = a[2];
        Vector3 cF = (midH[h].position - hand.position).normalized, cA = (idxH[h].position - litH[h].position).normalized;
        hand.rotation = g.rot * Quaternion.Inverse(Quaternion.LookRotation(cF, Vector3.Cross(cF, cA))) * hand.rotation;
        Curl(h, g.i, g.m, g.r, g.l, g.t);
        padMiss[h] = -1;
        if (g.padW > 0.01f) IndexPad(h, g.pad, g.padW);
    }

    // Take the pad of the index finger to a point: the finger is a two-bone chain (first and second
    // phalanx) bent the way a finger bends - middle knuckle out towards the back of the hand - with the
    // last phalanx left almost straight so it is the flat of the pad that arrives, not the tip or a crease.
    readonly float[] padMiss = { -1, -1 };
    public string TriggerDebug() => $"index pad to its point: right {(padMiss[0] < 0 ? "-" : (padMiss[0] * 1000).ToString("F0") + " mm")}, left {(padMiss[1] < 0 ? "-" : (padMiss[1] * 1000).ToString("F0") + " mm")}, on trigger {trigT:F2}";
    void IndexPad(int h, Vector3 target, float weight)
    {
        var f = fing[h][0]; if (!f[0] || !f[1] || !f[2]) return;
        var hand = arm[h][2];
        Vector3 aF = (midH[h].position - hand.position).normalized, axis = (idxH[h].position - litH[h].position).normalized;
        var palm = Vector3.Cross(aF, axis).normalized * (h == 0 ? 1 : -1);
        float l3 = (f[2].position - f[1].position).magnitude * 0.78f;                       // last phalanx is a little shorter than the one before it
        // the last joint sits behind the pad: half the last phalanx back along the finger, a little off the surface
        var toward = (target - f[0].position).normalized;
        var joint = target - toward * (l3 * 0.5f) - palm * 0.004f;
        joint = Vector3.Lerp(f[2].position, joint, weight);
        TwoBone(f[0], f[1], f[2], joint, -palm);
        // last phalanx: laid along the line to the pad, with the slight natural curl
        var want = (target - f[2].position); var cur = f[2].position - f[1].position;
        if (want.sqrMagnitude > 1e-8f) f[2].rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, want), 0.6f * weight) * f[2].rotation;
        padMiss[h] = Vector3.Distance(f[2].position + (f[2].position - f[1].position).normalized * (l3 * 0.5f), target);
    }

    // curl the fingers of a hand towards its palm (0 open .. 1 a closed fist); the thumb folds across them
    static readonly float[] Bend = { 62f, 78f, 55f };
    void Curl(int h, float ci, float cm, float cr, float cl, float thumb)
    {
        var hand = arm[h][2]; var fs = fing[h];
        Vector3 aF = (midH[h].position - hand.position).normalized, axis = (idxH[h].position - litH[h].position).normalized;
        var palm = Vector3.Cross(aF, axis).normalized * (h == 0 ? 1 : -1);          // from the back of the hand to the palm
        for (int i = 0; i < 4; i++)
        {
            float c = i == 0 ? ci : i == 1 ? cm : i == 2 ? cr : cl;
            var f = fs[i]; if (!f[0] || c < 0.01f) continue;
            var tipT = f[2] ? f[2] : f[1] ? f[1] : f[0];
            // which way round bends towards the palm
            var before = f[0].rotation; f[0].rotation = Quaternion.AngleAxis(30f, axis) * before;
            float plus = Vector3.Dot(tipT.position - f[0].position, palm); f[0].rotation = Quaternion.AngleAxis(-30f, axis) * before;
            float minus = Vector3.Dot(tipT.position - f[0].position, palm); f[0].rotation = before;
            float sgn = plus >= minus ? 1 : -1;
            for (int j = 0; j < 3; j++) if (f[j]) f[j].rotation = Quaternion.AngleAxis(Bend[j] * c * sgn, axis) * f[j].rotation;
        }
        var th = fs[4];
        if (th[0] && th[1] && thumb > 0.01f)
        {
            var tip = th[2] ? th[2] : th[1];
            var before = th[1].rotation; th[1].rotation = Quaternion.AngleAxis(25f, aF) * before;
            float plus = Vector3.Dot(tip.position - th[0].position, palm); th[1].rotation = Quaternion.AngleAxis(-25f, aF) * before;
            float minus = Vector3.Dot(tip.position - th[0].position, palm); th[1].rotation = before;
            float sgn = plus >= minus ? 1 : -1;
            th[1].rotation = Quaternion.AngleAxis(35f * thumb * sgn, aF) * th[1].rotation;
            if (th[2]) th[2].rotation = Quaternion.AngleAxis(30f * thumb * sgn, aF) * th[2].rotation;
        }
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
