using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// AK-47, bolt-action sniper and combat knife.
// Left mouse / RT = fire (knife: slash), right mouse / LT = aim (sniper: scope; knife: heavy stab),
// R / X = reload, 1-2-3 / Y / wheel = switch weapon, F / right stick = quick knife, V = first / third person,
// Shift while scoped = hold breath.
// First person (default) shows only the gun (ViewModel); third person shows the soldier holding it.
// Shots go where the crosshair points; the sniper round is a real ballistic shot (850 m/s, gravity).
// Recoil kicks the camera and the gun, tracers, muzzle flash, cases fly out, impact dust, bullet holes,
// generated gunshot / reload / knife sounds. Anything with a Damageable gets hit.
public class WeaponController : MonoBehaviour
{
    [System.Serializable]
    public class Weapon
    {
        public string name; public bool auto, melee; public float rpm, damage, spread, aimSpread, recoil, range, muzzleVelocity;
        public int magSize, reserve; public float reloadTime, aimFov; public bool scope;
        [HideInInspector] public int mag;
    }

    public Weapon[] weapons =
    {
        new Weapon { name = "AK-47", auto = true, rpm = 600, damage = 34, spread = 1.1f, aimSpread = 0.06f, recoil = 0.9f, range = 450, muzzleVelocity = 715, magSize = 30, reserve = 180, reloadTime = 2.4f, aimFov = 50 },
        new Weapon { name = "SNIPER", auto = false, rpm = 45, damage = 120, spread = 4f, aimSpread = 0.012f, recoil = 4f, range = 1200, muzzleVelocity = 850, magSize = 5, reserve = 30, reloadTime = 2.7f, aimFov = 11, scope = true },
        new Weapon { name = "KNIFE", melee = true, rpm = 140, damage = 100, range = 1.05f },          // a knife works at contact distance: an arm and a blade, no more
    };
    public int current;
    public bool externalInput;                       // AutoTest drives these
    [HideInInspector] public bool frozen;            // dead: no shooting
    public static float LastShotTime = -99;          // enemies hear this
    [HideInInspector] public bool fireHeld, aimHeld, reloadPressed, quickKnife, toggleView, holdBreath, supportShoulder; [HideInInspector] public int switchTo = -1;

    public bool Aiming { get; private set; }
    public bool FirstPerson => camRig && camRig.firstPerson;
    public Weapon W => weapons[current];
    public Weapon Get(int i) => weapons[i];
    public ViewModel View => vm;

    PlayerController player; ThirdPersonCamera camRig; Camera cam; Animator anim; ViewModel vm; CharacterController cc;
    Transform rifle, sniper, knife, chest;
    readonly Transform[] handGun = new Transform[3];
    float nextShot, reloadEnd, hitMarker, defaultFov, baseDistance;
    bool reloading; int quickFrom = -1; float quickBack;
    // sound is CombatAudio, flash / tracers / impacts / smoke are CombatFx
    readonly Queue<GameObject> holes = new();
    readonly Queue<Rigidbody> shells = new();
    Material holeMat; Texture2D scopeTex, dotTex;
    float aimT, breath;

    // the gun lives in the right hand (third person); its hold is measured once from the "idle aiming"
    // pose (pointing straight ahead, grip in the palm), so every animation carries it
    void CalibrateHold(Transform gun)
    {
        anim.SetFloat("VelX", 0); anim.SetFloat("VelZ", 0); anim.SetBool("Crouch", false);
        anim.Play("Locomotion", 0, 0f);
        anim.Update(0f);
        var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var palm = mid ? Vector3.Lerp(rightHand.position, mid.position, 0.6f) : rightHand.position;
        var rot = Quaternion.LookRotation(transform.forward, Vector3.up);
        gun.SetPositionAndRotation(palm - rot * new Vector3(0, -0.04f, 0), rot);   // grip sits 4 cm under the gun origin
        gun.SetParent(rightHand, true);
    }
    Transform rightHand;

    void Start()
    {
        player = GetComponent<PlayerController>(); cc = GetComponent<CharacterController>();
        camRig = FindFirstObjectByType<ThirdPersonCamera>();
        cam = camRig.GetComponent<Camera>();
        baseDistance = camRig.distance;
        anim = GetComponentInChildren<Animator>();
        rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        chest = anim.GetBoneTransform(HumanBodyBones.Chest) ?? anim.GetBoneTransform(HumanBodyBones.Spine);
        foreach (var w in weapons) w.mag = w.magSize;
        weapons[2].range = 1.05f;        // the weapon table is stored in the scene: contact distance for the knife, whatever is saved there

        // third-person guns in the hand: the new models replace the old block rifle
        rifle = transform.GetComponentsInChildren<Transform>(true).FirstOrDefaultByName("Rifle");
        if (rifle && rightHand)
        {
            CalibrateHold(rifle);
            foreach (Transform c in rifle) Destroy(c.gameObject);
            handGun[0] = GunFactory.AK(rifle);
            sniper = Sibling("Sniper"); handGun[1] = GunFactory.Sniper(sniper);
            knife = Sibling("Knife"); handGun[2] = GunFactory.Knife(knife); handGun[2].localPosition = new Vector3(0, -0.04f, 0.0f);
        }

        // first-person gun
        vm = cam.gameObject.AddComponent<ViewModel>();
        vm.Init(player, camRig, cam);
        vm.BuildArms(anim);                              // the soldier's own arms hold the gun in first person
        vm.onSound = OnVmSound; vm.onKnifeHit = MeleeHit; vm.onEject = () => Shell(1);

        holeMat = Rp.Decal("Bullet hole", HoleTexture(64), Color.white, 0.02f);      // HDRP decal: sinks into the wall's own surface
        scopeTex = ScopeTexture(1024); dotTex = DotTexture(32);
        SetView(camRig.firstPerson);
        ShowWeapon();
    }

    Transform Sibling(string name)
    {
        var t = new GameObject(name).transform; t.SetParent(rifle.parent, false);
        t.SetLocalPositionAndRotation(rifle.localPosition, rifle.localRotation); t.localScale = rifle.localScale;
        return t;
    }

    // first person: hide the body (it still casts its shadow); third person: show it
    public void SetView(bool fp)
    {
        camRig.firstPerson = fp;
        defaultFov = fp ? 68f : 62f;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.shadowCastingMode = fp ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        vm.visible = fp;
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        if (frozen || player.Swimming) { fireHeld = aimHeld = reloadPressed = quickKnife = false; switchTo = -1; }      // both hands are busy in the water
        if (toggleView) { SetView(!camRig.firstPerson); toggleView = false; }
        // quick knife: out, one slash, back to the gun
        if (quickKnife && !W.melee && !vm.Busy) { quickFrom = current; switchTo = 2; quickBack = Time.time + 0.75f; }
        quickKnife = false;
        if (switchTo >= 0 && switchTo != current && switchTo < weapons.Length)
        {
            current = switchTo; reloading = false; burstCount = 0; vm.SwitchTo(current, quickFrom >= 0); ShowWeapon();
            if (quickFrom >= 0 && current == 2) { nextShot = Time.time + 0.12f; pendingSlash = true; }
        }
        switchTo = -1;
        if (pendingSlash && Time.time >= nextShot) { pendingSlash = false; Knife(false); }
        if (quickFrom >= 0 && current == 2 && Time.time > quickBack && !vm.KnifeBusy) { switchTo = quickFrom; quickFrom = -1; }

        // which shoulder: a corner on the left is taken from the left shoulder, so only the muzzle and one
        // eye show round it; leaning sets it, X keeps it there (or brings it back) when you are not leaning
        vm.leftShoulder = !W.melee && (player.leanInput < -0.5f || (player.leanInput < 0.5f && supportShoulder));
        Aiming = aimHeld && !player.Sprinting && !W.melee;
        player.aiming = Aiming;
        // zoom in/out smoothly; while it runs the weapon owns the field of view
        aimT = Mathf.MoveTowards(aimT, Aiming ? 1f : 0f, Time.deltaTime / (W.scope ? 0.24f : 0.16f));
        vm.aimT = aimT;
        camRig.baseFov = defaultFov;
        camRig.aimingFov = aimT > 0.001f;
        float zoom = W.scope ? (aimT > 0.88f ? 1 : aimT * 0.25f) : Mathf.SmoothStep(0, 1, aimT);
        if (camRig.aimingFov) cam.fieldOfView = Mathf.Lerp(defaultFov, W.melee ? defaultFov : W.aimFov, zoom);
        if (!camRig.firstPerson) camRig.distance = Mathf.Lerp(camRig.distance, Aiming ? (W.scope ? 0.6f : 1.9f) : baseDistance, 1 - Mathf.Exp(-12 * Time.deltaTime));

        // scope sway; Shift holds the breath
        if (Aiming && W.scope && aimT > 0.88f)
        {
            float amp = (holdBreath ? 0.03f : 0.32f) * (1f + fatigue * 1.5f) * player.Unsteady; float t0 = breath, t1 = breath + Time.deltaTime; breath = t1;
            camRig.lookDelta += new Vector2((Mathf.Sin(t1 * 0.9f) - Mathf.Sin(t0 * 0.9f)) * amp, (Mathf.Sin(t1 * 1.4f + 1) - Mathf.Sin(t0 * 1.4f + 1)) * amp * 0.8f);
        }

        if (reloadPressed && !W.melee && !reloading && !vm.Busy && W.mag <= W.magSize && W.reserve > 0) StartReload();
        reloadPressed = false;
        if (reloading && Time.time >= reloadEnd)
        {
            if (tacticalReload)
            {
                // one stays in the chamber, the part-used mag goes back in the pouch
                int keep = Mathf.Max(0, W.mag - 1); W.reserve += keep;
                int take = Mathf.Min(W.magSize, W.reserve); W.reserve -= take; W.mag = take + 1;
            }
            else { int take = Mathf.Min(W.magSize, W.reserve); W.reserve -= take; W.mag = take; }
            reloading = false;
        }
        Fatigue();
        RecoilRecovery();
        AimSway();
        if (Aiming || fireHeld || Time.time - LastShotTime < 0.2f || reloading || EnemyAI.All.Exists(e => e && e.SeesPlayer)) vm.combatAt = Time.time;

        bool ready = !reloading && !vm.Busy && Time.time >= nextShot && !player.Sprinting && player.SprintLock <= 0;
        if (W.melee)
        {
            if ((fireHeld || aimHeld) && !vm.KnifeBusy && Time.time >= nextShot && quickFrom < 0) Knife(aimHeld && !fireHeld);
            fireHeld = false;
        }
        else if (fireHeld && ready)
        {
            if (W.mag <= 0) { CombatAudio.DryFire(); nextShot = Time.time + 0.3f; if (W.reserve > 0) StartReload(); }
            else { Fire(); nextShot = Time.time + 60f / W.rpm; }
            if (!W.auto) fireHeld = false;
        }

        AimFocus();
        hitMarker -= Time.deltaTime;
    }
    bool pendingSlash;

    // aiming down the sights: focus on what the cross-hair is on, the gun right in front of your eye goes soft
    DepthOfField dof; float focusDist = 30f; bool dofSearched;
    void AimFocus()
    {
        if (!dofSearched)
        {
            // its own small global volume: the scene's profile asset is never written to at run time
            dofSearched = true;
            var vg = new GameObject("Aim focus volume"); vg.transform.SetParent(transform, false);
            var vol = vg.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 30;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>(); dof = prof.Add<DepthOfField>(true); vol.profile = prof;
            dof.focusMode.Override(DepthOfFieldMode.Off);
        }
        if (dof == null) return;
        bool on = camRig.firstPerson && aimT > 0.5f && !W.melee && !(W.scope && aimT > 0.88f);
        if (on)
        {
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            float target = RayNotSelf(ray.origin, ray.direction, 300f, out var h) ? h.distance : 300f;
            focusDist = Mathf.Lerp(focusDist, target, 1 - Mathf.Exp(-8 * Time.deltaTime));
            // HDRP manual ranges: the gun right in front of the eye goes soft the way it does to the eye (it must not
            // melt into a blob), what the cross-hair is on stays sharp, the far distance softens a little
            dof.focusMode.Override(DepthOfFieldMode.Manual);
            dof.nearFocusStart.Override(0.12f); dof.nearFocusEnd.Override(Mathf.Min(0.9f, Mathf.Max(0.45f, focusDist * 0.5f)));
            dof.farFocusStart.Override(Mathf.Max(8f, focusDist * 3f)); dof.farFocusEnd.Override(Mathf.Max(30f, focusDist * 12f));
            dof.nearMaxBlur = 3.5f; dof.farMaxBlur = 2.5f;
        }
        else if (dof.focusMode.value != DepthOfFieldMode.Off) dof.focusMode.Override(DepthOfFieldMode.Off);
    }

    bool tacticalReload;
    public bool LastReloadTactical => tacticalReload;
    void StartReload()
    {
        tacticalReload = W.mag > 0;                                 // rounds left: tactical; empty: emergency
        float t = W.reloadTime * (tacticalReload ? 1.05f : 0.95f);
        reloading = true; reloadEnd = Time.time + t; vm.Reload(current, t, tacticalReload);
    }

    // ---------- the shooter's body ----------
    // fatigue builds while sprinting and fades when you stop: a racing heart shakes the sights
    float fatigue, swayT;
    public float FatigueLevel => fatigue;
    void Fatigue() => fatigue = Mathf.Clamp01(fatigue + (player.Sprinting ? Time.deltaTime / 6f : -Time.deltaTime / 9f));

    // sights drift with breathing and heartbeat when aiming (scope sway is handled separately)
    void AimSway()
    {
        if (!Aiming || W.melee || W.scope || aimT < 0.5f) return;
        float moving = Mathf.Clamp01(player.Speed / PlayerController.JogSpeed);
        float amp = (0.05f + fatigue * 0.3f + moving * 0.12f) * player.Unsteady;
        float t0 = swayT, t1 = swayT + Time.deltaTime; swayT = t1;
        float beat(float t) => Mathf.Pow(Mathf.Max(0, Mathf.Sin(t * (5f + fatigue * 5f))), 8) * fatigue * 0.6f;
        float nx(float t) => Mathf.Sin(t * 0.7f) * 0.6f + Mathf.Sin(t * 1.9f + 1.3f) * 0.4f;
        float ny(float t) => Mathf.Sin(t * 1.1f + 0.4f) * 0.7f + beat(t);
        camRig.lookDelta += new Vector2(nx(t1) - nx(t0), ny(t1) - ny(t0)) * amp;
    }

    // ---------- recoil: the AK climbs, pulls right and wanders; the shooter brings it back down ----------
    int burstCount; float lastShotT; Vector2 recoilDebt;
    void Kick()
    {
        if (Time.time - lastShotT > 0.35f) burstCount = 0;
        burstCount++; lastShotT = Time.time;
        float mult = (Aiming ? 0.6f : 1f) * Mathf.Lerp(1f, player.Unsteady, 0.8f) * (1f + fatigue * 0.25f);
        Vector2 k;
        if (W.scope) k = new Vector2(Random.Range(-0.3f, 0.3f), 2.4f);
        else
        {
            float v = burstCount == 1 ? 0.32f : 0.5f + Mathf.Min(burstCount, 8) * 0.03f;
            float h = burstCount < 5 ? 0.12f : Mathf.Sin(burstCount * 0.9f) * 0.35f;
            k = new Vector2(h + Random.Range(-0.1f, 0.1f), v);
        }
        k *= mult;
        camRig.lookDelta += k; recoilDebt += k * 0.7f;               // 70% of it is pulled back down
    }
    void RecoilRecovery()
    {
        if (Time.time - lastShotT < 0.09f || recoilDebt.sqrMagnitude < 1e-5f) return;
        var rec = recoilDebt * (1 - Mathf.Exp(-9f * Time.deltaTime));
        camRig.lookDelta -= rec; recoilDebt -= rec;
    }

    void ReadInput()
    {
        var m = Mouse.current; var k = Keyboard.current; var g = Gamepad.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool autoFire = W.auto;
        fireHeld = (m != null && locked && (autoFire ? m.leftButton.isPressed : m.leftButton.wasPressedThisFrame)) || (g != null && (autoFire ? g.rightTrigger.isPressed : g.rightTrigger.wasPressedThisFrame));
        aimHeld = W.melee ? (m != null && locked && m.rightButton.wasPressedThisFrame) || (g != null && g.leftTrigger.wasPressedThisFrame)
                          : (m != null && m.rightButton.isPressed) || (g != null && g.leftTrigger.isPressed);
        reloadPressed = (k != null && k.rKey.wasPressedThisFrame) || (g != null && g.buttonWest.wasPressedThisFrame);
        if (k != null && k.digit1Key.wasPressedThisFrame) switchTo = 0;
        if (k != null && k.digit2Key.wasPressedThisFrame) switchTo = 1;
        if (k != null && k.digit3Key.wasPressedThisFrame) switchTo = 2;
        if (g != null && g.buttonNorth.wasPressedThisFrame) switchTo = (current + 1) % weapons.Length;
        if (m != null && Mathf.Abs(m.scroll.ReadValue().y) > 0.1f) switchTo = (current + (m.scroll.ReadValue().y < 0 ? 1 : weapons.Length - 1)) % weapons.Length;
        quickKnife = (k != null && k.fKey.wasPressedThisFrame) || (g != null && g.rightStickButton.wasPressedThisFrame);
        toggleView = k != null && k.vKey.wasPressedThisFrame;
        holdBreath = k != null && k.leftShiftKey.isPressed;
        if (k != null && k.xKey.wasPressedThisFrame) supportShoulder = !supportShoulder;
    }

    // where the crosshair points, from the camera
    Vector3 AimPoint(out bool hitSomething)
    {
        var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
        hitSomething = RayNotSelf(ray.origin, ray.direction, W.range, out var h);
        return hitSomething ? h.point : ray.origin + ray.direction * W.range;
    }

    void Fire()
    {
        W.mag--; LastShotTime = Time.time;
        var origin = cam.transform.position; var ct = cam.transform;
        var aimDir = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f)).direction;
        // the bore is 6.5 cm under the sight line and the rifle is zeroed (AK 100 m, sniper 200 m): close up the
        // round hits a little low, at the zero right on, beyond it drops
        float v = W.muzzleVelocity > 0 ? W.muzzleVelocity : 715f, zero = W.scope ? 200f : 100f;
        var start = origin - ct.up * 0.065f;
        var dir = ((origin + aimDir * zero) - start).normalized;
        float tz = zero / v; float lift = Mathf.Atan2(0.5f * 9.81f * tz * tz, zero) * Mathf.Rad2Deg;
        dir = Quaternion.AngleAxis(-lift, ct.right) * dir;
        // spread: a still, aimed first round goes where the sights are; moving, firing long bursts, a racing
        // heart and hip firing open it up
        if (Time.time - lastShotT > 0.35f) burstCount = 0;          // a new burst: the last one's spread is gone
        float moving = Mathf.Clamp(player.Speed / PlayerController.JogSpeed, 0, 2);
        float spread = Aiming ? W.aimSpread * (1 + moving * 3f) : W.spread * (1 + moving * 0.6f);
        spread *= (1 + fatigue * 0.8f) * player.Unsteady;
        spread += Mathf.Min(1.2f, Mathf.Max(0, burstCount) * (Aiming ? 0.05f : 0.12f));
        if (burstCount == 0 && moving < 0.1f) spread *= 0.4f;
        dir = Quaternion.AngleAxis(Random.Range(-spread, spread), ct.up) * Quaternion.AngleAxis(Random.Range(-spread, spread), ct.right) * dir;

        bool hit = Ballistic(start, dir * v, out var h);

        var muzzle = Muzzle();
        if (!(W.scope && Aiming)) CombatFx.Tracer(muzzle, hit ? h.point : origin + dir * 200, true, W.scope);
        CombatFx.Muzzle(muzzle, dir, W.scope, true);
        CombatAudio.Shot(W.scope ? 1 : 0, muzzle, true);
        Kick();
        vm.Fire(current, Aiming);
        if (current == 0) Shell(0);

        if (hit)
        {
            var d = h.collider.GetComponentInParent<Damageable>();
            int car = CarDamage.None;
            if (d) { d.Hit(W.damage, h.point, dir); hitMarker = 0.25f; hits++; }
            else if ((car = CarDamage.Hit(h, dir, W.scope)) == CarDamage.None) Hole(h);        // a car marks its own hole or broken pane
            if (!(d is EnemyAI) && car != CarDamage.Glass) CombatFx.Impact(h, dir, W.scope);   // a man shows his own hit (EnemyAI.Hit); glass breaks, it does not spark
        }
        lastShot = hit ? $"{(h.collider.GetComponentInParent<Damageable>() ? "TARGET " : "")}{h.distance:F0} m" : "miss";
    }
    string lastShot = "";

    // ---------- knife ----------
    // Which attack: decided by what is in front and where the eyes are, with chance in it so two attacks in
    // a row are not the same. Quick (fire): cuts. Committed (aim button): thrusts and reverse-grip stabs.
    //   a man standing close in front       cut across, or a downward diagonal cut; committed: straight thrust
    //   looking down / a man kneeling, low  downward cut; committed: reverse grip, overhand stab down
    //   someone at the side of the view     backhand rip in the reverse grip
    int lastKnife = -1;
    void Knife(bool heavy)
    {
        var ct = cam.transform; bool low = camRig.pitch > 18f, side = false;
        EnemyAI near = null; float nd = 3f;
        foreach (var e in EnemyAI.All)
        {
            if (!e || e.IsDead) continue;
            var to = e.transform.position + Vector3.up - ct.position; float d = to.magnitude;
            if (d < nd && Vector3.Dot(to / d, ct.forward) > 0.2f) { nd = d; near = e; }
        }
        if (near)
        {
            low |= near.Crouched;
            var to = Vector3.ProjectOnPlane(near.transform.position - ct.position, Vector3.up).normalized;
            side = Vector3.Dot(to, ct.right) < -0.45f;                  // off to the support side: the backhand reaches him
        }
        float r = Random.value; int kind;
        if (heavy) kind = low ? (r < 0.8f ? ViewModel.StabDown : ViewModel.Thrust) : side ? (r < 0.6f ? ViewModel.RipBack : ViewModel.Thrust) : (r < 0.6f ? ViewModel.Thrust : r < 0.85f ? ViewModel.StabDown : ViewModel.RipBack);
        else kind = low ? (r < 0.7f ? ViewModel.CutDown : ViewModel.CutAcross) : (r < 0.6f ? ViewModel.CutAcross : ViewModel.CutDown);
        if (kind == lastKnife && kind != ViewModel.CutAcross && Random.value < 0.6f) kind = heavy ? (kind == ViewModel.Thrust ? ViewModel.StabDown : ViewModel.Thrust) : ViewModel.CutAcross;
        if (forceKnife >= 0) { kind = forceKnife; forceKnife = -1; }
        lastKnife = kind;
        vm.KnifeAttack(kind);
        nextShot = Time.time + ViewModel.KnifeTime(kind) + 0.04f;
    }
    [HideInInspector] public int forceKnife = -1;          // the test asks for one attack by name

    void MeleeHit(bool heavy)
    {
        var o = cam.transform.position; var f = cam.transform.forward;
        RaycastHit best = default; float bd = float.MaxValue; bool any = false;
        foreach (var h in Physics.SphereCastAll(o, 0.28f, f, weapons[2].range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (h.collider.transform.IsChildOf(transform) || h.distance <= 0) continue;
            if (h.distance < bd) { bd = h.distance; best = h; any = true; }
        }
        if (!any) { lastShot = "knife: miss"; return; }
        var d = best.collider.GetComponentInParent<Damageable>();
        if (d) { d.Hit(weapons[2].damage * (heavy ? 1.5f : 1f), best.point, f); hitMarker = 0.3f; hits++; knifeHits++; CombatAudio.KnifeHit(d is EnemyAI, best.point); lastShot = "knife: TARGET"; }
        else { CombatAudio.KnifeHit(false, best.point); CombatFx.Impact(best, f, false, true); lastShot = "knife: wall"; }
        camRig.lookDelta += new Vector2(heavy ? 0 : 0.6f * (Random.value - 0.5f), 0.5f);
    }
    public int knifeHits;

    void OnVmSound(string s) => CombatAudio.Reload(s);     // "magout", "magin", "boltback", "boltfwd", "swish"

    // a spent case flies out of the ejection port to the right
    void Shell(int w)
    {
        Rigidbody rb;
        if (shells.Count >= 24) rb = shells.Dequeue();
        else
        {
            var g = GunFactory.Shell(w == 1);
            g.layer = 2;                                        // Ignore Raycast: bullets go through flying cases
            foreach (Transform c in g.transform) c.gameObject.layer = 2;
            var col = g.AddComponent<CapsuleCollider>(); col.direction = 2; col.radius = 0.006f; col.height = w == 1 ? 0.064f : 0.039f;
            Physics.IgnoreCollision(col, cc);
            rb = g.AddComponent<Rigidbody>(); rb.mass = 0.01f; rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
        shells.Enqueue(rb);
        var ct = cam.transform;
        var at = camRig.firstPerson ? vm.EjectPos(w) : (handGun[w] ? (GunFactory.Find(handGun[w], "Eject")?.position ?? Muzzle()) : Muzzle());
        rb.gameObject.SetActive(true);
        rb.transform.SetPositionAndRotation(at, ct.rotation * Quaternion.Euler(0, 90 + Random.Range(-20, 20), 0));
        rb.linearVelocity = cc.velocity + ct.right * Random.Range(1.6f, 2.6f) + ct.up * Random.Range(1.1f, 1.8f) - ct.forward * Random.Range(0.1f, 0.5f);
        rb.angularVelocity = Random.insideUnitSphere * 25f;
        CombatFx.ShellSmoke(rb.transform);
        CombatAudio.ShellDrop(at, transform.position.y, w == 1);
    }

    // sniper round: step through the flight with gravity
    bool Ballistic(Vector3 pos, Vector3 vel, out RaycastHit hit)
    {
        const float dt = 0.01f; float travelled = 0;
        while (travelled < W.range)
        {
            var next = pos + vel * dt; vel += Physics.gravity * dt;
            var seg = next - pos;
            if (RayNotSelf(pos, seg.normalized, seg.magnitude, out hit))
            { hit.distance += travelled; return true; }
            travelled += seg.magnitude; pos = next;
        }
        hit = default; return false;
    }

    // first hit that isn't the soldier himself
    bool RayNotSelf(Vector3 o, Vector3 d, float range, out RaycastHit best)
    {
        best = default; float bd = float.MaxValue; bool any = false;
        foreach (var h in Physics.RaycastAll(o, d, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (h.distance < bd && !h.collider.transform.IsChildOf(transform)) { bd = h.distance; best = h; any = true; }
        return any;
    }

    public int hits;

    void Hole(RaycastHit h)
    {
        GameObject q;
        if (holes.Count >= 120) q = holes.Dequeue();
        else { q = new GameObject("Bullet hole") { layer = 2 }; var dp = q.AddComponent<DecalProjector>(); dp.material = holeMat; dp.size = new Vector3(1, 1, 0.12f); dp.pivot = Vector3.zero; dp.drawDistance = 80f; dp.fadeScale = 0.85f; }
        q.transform.SetPositionAndRotation(h.point, Quaternion.LookRotation(-h.normal) * Quaternion.Euler(0, 0, Random.Range(0, 360)));
        float hs = Random.Range(0.06f, 0.11f) * (W.scope ? 1.6f : 1f) * 1.5f;          // the texture has a soft rim: a little larger than the old quad
        q.transform.localScale = new Vector3(hs, hs, 1f);
        q.transform.SetParent(h.collider.transform, true);
        holes.Enqueue(q);
    }

    Vector3 Muzzle()
    {
        if (camRig.firstPerson) return vm.MuzzlePos;
        var g = handGun[current]; var m = g ? GunFactory.Find(g, "Muzzle") : null;
        return m ? m.position : transform.position + Vector3.up * 1.4f;
    }

    // third person: lean the chest with the camera pitch
    // Swimming: the rifle is swung round onto his back on its sling, muzzle up over the left shoulder, and
    // comes back into his hands when he finds his feet.
    bool slung; Transform slingParent; Vector3 slingPos; Quaternion slingRot;
    public bool RifleSlung => slung;
    void Sling(bool on)
    {
        var g = current == 0 ? rifle : current == 1 ? sniper : knife;
        if (on == slung || !g || !chest) return;
        if (on)
        {
            slingParent = g.parent; slingPos = g.localPosition; slingRot = g.localRotation; slingGun = g;
            var up = transform.up * 0.92f - transform.right * 0.38f;
            g.SetPositionAndRotation(chest.position - transform.forward * 0.19f + transform.up * 0.02f + transform.right * 0.06f - up.normalized * 0.28f, Quaternion.LookRotation(up, -transform.forward));
            g.SetParent(chest, true);
        }
        else if (slingGun) { slingGun.SetParent(slingParent, false); slingGun.localPosition = slingPos; slingGun.localRotation = slingRot; }
        slung = on;
    }
    Transform slingGun;

    void LateUpdate()
    {
        if (player.Swimming != slung) Sling(player.Swimming);
        if (chest && !camRig.firstPerson)
        {
            chest.rotation = Quaternion.AngleAxis(camRig.pitch * 0.55f, transform.right) * chest.rotation;
            chest.rotation = Quaternion.AngleAxis(-player.Lean * 18f, transform.forward) * chest.rotation;
        }
    }

    void ShowWeapon()
    {
        if (rifle) rifle.gameObject.SetActive(current == 0);
        if (sniper) sniper.gameObject.SetActive(current == 1);
        if (knife) knife.gameObject.SetActive(current == 2);
    }

    // (sounds: CombatAudio.cs; dust, tracers, flash: CombatFx.cs)

    // scope picture: clear circle, black outside with a soft dark rim
    static Texture2D ScopeTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
            float a = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.86f, 0.995f, d));   // rim darkening
            px[y * n + x] = new Color(0, 0, 0, Mathf.Max(a, d > 0.995f ? 1 : 0));
        }
        t.SetPixels(px); t.Apply(); return t;
    }

    // a round's hole: a dark pit with a chipped, paler rim, clear outside it (used as an HDRP decal)
    static Texture2D HoleTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "BulletHole" };
        var rnd = new System.Random(4); var ph = new float[6]; for (int i = 0; i < ph.Length; i++) ph[i] = (float)rnd.NextDouble() * 6.2832f;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
            float edge = 0.6f; for (int i = 0; i < ph.Length; i++) edge += Mathf.Sin(a * (i + 3) + ph[i]) * 0.045f;
            float pit = Mathf.Clamp01((edge * 0.55f - r) / 0.1f), rim = Mathf.Clamp01((edge - r) / 0.16f);
            var c = Color.Lerp(new Color(0.42f, 0.4f, 0.37f), new Color(0.03f, 0.028f, 0.025f), pit);
            px[y * n + x] = new Color(c.r, c.g, c.b, Mathf.Max(pit, rim * 0.75f));
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    static Texture2D DotTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f); t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1 - d) * 3))); }
        t.Apply(); return t;
    }

    void OnGUI()
    {
        float w = Screen.width, h = Screen.height, cx = w / 2, cy = h / 2;
        var white = Texture2D.whiteTexture;
        if (Aiming && W.scope && aimT > 0.88f)
        {
            // scope: round view, black around it, duplex cross-hair with mil dots
            float r = h * 0.47f;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, cx - r + 1, h), white); GUI.DrawTexture(new Rect(cx + r - 1, 0, w - cx - r + 1, h), white);
            GUI.color = Color.white; GUI.DrawTexture(new Rect(cx - r, cy - r, 2 * r, 2 * r), scopeTex);
            GUI.color = new Color(0, 0, 0, 0.95f);
            float thick = Mathf.Max(3, h * 0.006f), inner = r * 0.32f;
            GUI.DrawTexture(new Rect(cx - r, cy - thick / 2, r - inner, thick), white); GUI.DrawTexture(new Rect(cx + inner, cy - thick / 2, r - inner, thick), white);
            GUI.DrawTexture(new Rect(cx - thick / 2, cy + inner, thick, r - inner), white); GUI.DrawTexture(new Rect(cx - thick / 2, cy - r, thick, r - inner), white);
            GUI.DrawTexture(new Rect(cx - inner, cy - 0.5f, 2 * inner, 1.2f), white); GUI.DrawTexture(new Rect(cx - 0.5f, cy - inner, 1.2f, 2 * inner), white);
            for (int i = -4; i <= 4; i++) if (i != 0)
            {
                GUI.DrawTexture(new Rect(cx + i * inner / 5 - 2, cy - 2, 4, 4), white);
                GUI.DrawTexture(new Rect(cx - 2, cy + i * inner / 5 - 2, 4, 4), white);
            }
            GUI.color = new Color(1, 0.2f, 0.15f, 0.9f); GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3, 3), white);
            GUI.color = Color.black; GUI.DrawTexture(new Rect(0, 0, w, cy - r + 2), white); GUI.DrawTexture(new Rect(0, cy + r - 2, w, h - cy - r + 2), white);
        }
        else if (Aiming && !W.melee && camRig.firstPerson && aimT > 0.85f && !(current == 0 && GunFactory.RealAK))
        {
            // the red dot, projected at infinity in the middle of the sight
            GUI.color = new Color(1f, 0.15f, 0.1f, 0.35f); GUI.DrawTexture(new Rect(cx - 4, cy - 4, 8, 8), dotTex);
            GUI.color = new Color(1f, 0.25f, 0.18f, 1f); GUI.DrawTexture(new Rect(cx - 2.5f, cy - 2.5f, 5, 5), dotTex);
        }
        else if (!(Aiming && !W.melee && camRig.firstPerson))
        {
            // hip-fire cross-hair (none while aiming down the red dot)
            float gap = W.melee ? 3 : (Aiming ? 4 : 9) + (player ? player.moveInput.magnitude * 10 : 0);
            GUI.color = new Color(1, 1, 1, 0.9f);
            if (W.melee) GUI.DrawTexture(new Rect(cx - 2, cy - 2, 4, 4), white);
            else
            {
                GUI.DrawTexture(new Rect(cx - 1, cy - gap - 8, 2, 8), white); GUI.DrawTexture(new Rect(cx - 1, cy + gap, 2, 8), white);
                GUI.DrawTexture(new Rect(cx - gap - 8, cy - 1, 8, 2), white); GUI.DrawTexture(new Rect(cx + gap, cy - 1, 8, 2), white);
            }
        }
        if (hitMarker > 0)
        {
            GUI.color = new Color(1, 0.25f, 0.2f, hitMarker * 4);
            var m = GUI.matrix; GUIUtility.RotateAroundPivot(45, new Vector2(cx, cy));
            GUI.DrawTexture(new Rect(cx - 12, cy - 1, 24, 2), white); GUI.DrawTexture(new Rect(cx - 1, cy - 12, 2, 24), white);
            GUI.matrix = m;
        }
        GUI.color = Color.white;
        var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.032f), alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold };
        var small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.021f) };
        string ammo = W.melee ? "" : reloading ? "RELOADING" : $"{W.mag} / {W.reserve}";
        Shadowed(new Rect(w - 420, h - 110, 400, 90), $"{W.name}\n{ammo}", style);
        Shadowed(new Rect(20, h - 70, 1500, 60), "LMB fire · RMB aim · R reload · Q/E lean · X shoulder · 1/2/3 weapon · F knife · V view · Shift sprint / hold breath · C crouch · Z prone · Space jump · T time of day · F11 window" + (lastShot != "" ? $"   last: {lastShot}" : ""), small);
    }

    // white text with a dark shadow so it reads on bright stone and sky
    static void Shadowed(Rect r, string text, GUIStyle style)
    {
        GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
        GUI.color = Color.white; GUI.Label(r, text, style);
    }
}

static class TransformFind
{
    public static Transform FirstOrDefaultByName(this Transform[] list, string name)
    { foreach (var t in list) if (t.name == name) return t; return null; }
}
