using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
        new Weapon { name = "AK-47", auto = true, rpm = 600, damage = 34, spread = 1.6f, aimSpread = 0.3f, recoil = 0.9f, range = 400, magSize = 30, reserve = 180, reloadTime = 2.2f, aimFov = 50 },
        new Weapon { name = "SNIPER", auto = false, rpm = 45, damage = 120, spread = 4f, aimSpread = 0.02f, recoil = 4f, range = 1200, muzzleVelocity = 850, magSize = 5, reserve = 30, reloadTime = 3f, aimFov = 11, scope = true },
        new Weapon { name = "KNIFE", melee = true, rpm = 140, damage = 100, range = 2.3f },
    };
    public int current;
    public bool externalInput;                       // AutoTest drives these
    [HideInInspector] public bool frozen;            // dead: no shooting
    public static float LastShotTime = -99;          // enemies hear this
    [HideInInspector] public bool fireHeld, aimHeld, reloadPressed, quickKnife, toggleView, holdBreath; [HideInInspector] public int switchTo = -1;

    public bool Aiming { get; private set; }
    public bool FirstPerson => camRig && camRig.firstPerson;
    public Weapon W => weapons[current];
    public ViewModel View => vm;

    PlayerController player; ThirdPersonCamera camRig; Camera cam; Animator anim; ViewModel vm; CharacterController cc;
    Transform rifle, sniper, knife, chest;
    readonly Transform[] handGun = new Transform[3];
    float nextShot, reloadEnd, hitMarker, defaultFov, baseDistance;
    bool reloading; int quickFrom = -1; float quickBack;
    AudioSource audioSrc; AudioClip rifleClip, sniperClip, clickClip, swishClip, thudClip, magOutClip, magInClip, boltClip;
    LineRenderer tracer; float tracerOff; Light flash; float flashOff;
    ParticleSystem dust;
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
        vm.onSound = OnVmSound; vm.onKnifeHit = MeleeHit; vm.onEject = () => Shell(1);

        audioSrc = gameObject.AddComponent<AudioSource>(); audioSrc.spatialBlend = 0; audioSrc.volume = 0.8f;
        rifleClip = Gunshot(0.32f, 900f, 1); sniperClip = Gunshot(0.9f, 420f, 2); clickClip = Gunshot(0.03f, 3000f, 3);
        magOutClip = Click(0.07f, 2200, 11); magInClip = Click(0.09f, 1500, 12); boltClip = Click(0.12f, 2600, 13);
        swishClip = Swish(0.28f); thudClip = Gunshot(0.12f, 300f, 14);

        tracer = new GameObject("Tracer").AddComponent<LineRenderer>();
        tracer.material = new Material(Shader.Find("Sprites/Default"));
        tracer.startColor = new Color(1f, 0.85f, 0.5f, 0.9f); tracer.endColor = new Color(1f, 0.9f, 0.6f, 0.15f);
        tracer.startWidth = 0.018f; tracer.endWidth = 0.01f; tracer.positionCount = 2; tracer.enabled = false;
        flash = new GameObject("MuzzleFlash").AddComponent<Light>();
        flash.type = LightType.Point; flash.color = new Color(1f, 0.75f, 0.4f); flash.range = 6; flash.intensity = 0;
        dust = BuildDust();
        holeMat = new Material(Shader.Find("Universal Render Pipeline/Lit")); holeMat.SetColor("_BaseColor", new Color(0.05f, 0.045f, 0.04f)); holeMat.SetFloat("_Smoothness", 0);
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
        if (frozen) { fireHeld = aimHeld = reloadPressed = quickKnife = false; switchTo = -1; }
        if (toggleView) { SetView(!camRig.firstPerson); toggleView = false; }
        // quick knife: out, one slash, back to the gun
        if (quickKnife && !W.melee && !vm.Busy) { quickFrom = current; switchTo = 2; quickBack = Time.time + 0.75f; }
        quickKnife = false;
        if (switchTo >= 0 && switchTo != current && switchTo < weapons.Length)
        {
            current = switchTo; reloading = false; vm.SwitchTo(current, quickFrom >= 0); ShowWeapon();
            if (quickFrom >= 0 && current == 2) { nextShot = Time.time + 0.12f; pendingSlash = true; }
        }
        switchTo = -1;
        if (pendingSlash && Time.time >= nextShot) { pendingSlash = false; Knife(false); }
        if (quickFrom >= 0 && current == 2 && Time.time > quickBack && !vm.KnifeBusy) { switchTo = quickFrom; quickFrom = -1; }

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
            float amp = holdBreath ? 0.03f : 0.32f; float t0 = breath, t1 = breath + Time.deltaTime; breath = t1;
            camRig.lookDelta += new Vector2((Mathf.Sin(t1 * 0.9f) - Mathf.Sin(t0 * 0.9f)) * amp, (Mathf.Sin(t1 * 1.4f + 1) - Mathf.Sin(t0 * 1.4f + 1)) * amp * 0.8f);
        }

        if (reloadPressed && !W.melee && !reloading && !vm.Busy && W.mag < W.magSize && W.reserve > 0) StartReload();
        reloadPressed = false;
        if (reloading && Time.time >= reloadEnd)
        {
            int need = W.magSize - W.mag, take = Mathf.Min(need, W.reserve);
            W.mag += take; W.reserve -= take; reloading = false;
        }

        bool ready = !reloading && !vm.Busy && Time.time >= nextShot && !player.Sprinting && player.SprintLock <= 0;
        if (W.melee)
        {
            if ((fireHeld || aimHeld) && !vm.KnifeBusy && Time.time >= nextShot && quickFrom < 0) Knife(aimHeld && !fireHeld);
            fireHeld = false;
        }
        else if (fireHeld && ready)
        {
            if (W.mag <= 0) { audioSrc.PlayOneShot(clickClip, 0.5f); nextShot = Time.time + 0.3f; if (W.reserve > 0) StartReload(); }
            else { Fire(); nextShot = Time.time + 60f / W.rpm; }
            if (!W.auto) fireHeld = false;
        }

        AimFocus();
        if (Time.time > tracerOff) tracer.enabled = false;
        flash.intensity = Time.time < flashOff ? 6f : 0f;
        hitMarker -= Time.deltaTime;
    }
    bool pendingSlash;

    // aiming down the sights: focus on what the cross-hair is on, the gun right in front of your eye goes soft
    DepthOfField dof; float focusDist = 30f; bool dofSearched;
    void AimFocus()
    {
        if (!dofSearched) { dofSearched = true; var vol = FindFirstObjectByType<Volume>(); if (vol && vol.profile) vol.profile.TryGet(out dof); }
        if (dof == null) return;
        bool on = camRig.firstPerson && aimT > 0.5f && !W.melee && !(W.scope && aimT > 0.88f);
        if (on)
        {
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            float target = RayNotSelf(ray.origin, ray.direction, 300f, out var h) ? h.distance : 300f;
            focusDist = Mathf.Lerp(focusDist, target, 1 - Mathf.Exp(-8 * Time.deltaTime));
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(Mathf.Max(1f, focusDist));
        }
        else if (dof.mode.value != DepthOfFieldMode.Off) dof.mode.Override(DepthOfFieldMode.Off);
    }

    void StartReload() { reloading = true; reloadEnd = Time.time + W.reloadTime; vm.Reload(current, W.reloadTime); }

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
        var origin = cam.transform.position;
        var aim = AimPoint(out _);
        var dir = (aim - origin).normalized;
        // spread: tighter when aiming, wider when moving
        float moving = new Vector2(player.moveInput.x, player.moveInput.y).magnitude;
        float spread = (Aiming ? W.aimSpread : W.spread) * (1 + moving * 0.8f);
        dir = Quaternion.AngleAxis(Random.Range(-spread, spread), cam.transform.up) * Quaternion.AngleAxis(Random.Range(-spread, spread), cam.transform.right) * dir;

        bool hit; RaycastHit h;
        if (W.muzzleVelocity > 0) hit = Ballistic(origin, dir * W.muzzleVelocity, out h);
        else hit = RayNotSelf(origin, dir, W.range, out h);

        var muzzle = Muzzle();
        tracer.SetPosition(0, muzzle); tracer.SetPosition(1, hit ? h.point : origin + dir * 200);
        tracer.enabled = !(W.scope && Aiming); tracerOff = Time.time + 0.04f;
        flash.transform.position = muzzle; flashOff = Time.time + 0.04f;
        audioSrc.pitch = Random.Range(0.93f, 1.05f);
        audioSrc.PlayOneShot(W.scope ? sniperClip : rifleClip);
        camRig.lookDelta += new Vector2(Random.Range(-0.25f, 0.25f) * W.recoil, W.recoil * (Aiming ? 0.55f : 0.9f));
        vm.Fire(current, Aiming);
        if (current == 0) Shell(0);

        if (hit)
        {
            var d = h.collider.GetComponentInParent<Damageable>();
            if (d) { d.Hit(W.damage, h.point, dir); hitMarker = 0.25f; hits++; }
            else Hole(h);
            if (dust) dust.transform.SetPositionAndRotation(h.point + h.normal * 0.05f, Quaternion.LookRotation(h.normal));
            if (dust) dust.Emit(W.scope ? 26 : 12);
        }
        lastShot = hit ? $"{(h.collider.GetComponentInParent<Damageable>() ? "TARGET " : "")}{h.distance:F0} m" : "miss";
    }
    string lastShot = "";

    // ---------- knife ----------
    void Knife(bool heavy)
    {
        vm.KnifeAttack(heavy);
        nextShot = Time.time + (heavy ? 0.66f : 0.44f);
    }

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
        if (d) { d.Hit(weapons[2].damage * (heavy ? 1.5f : 1f), best.point, f); hitMarker = 0.3f; hits++; knifeHits++; audioSrc.PlayOneShot(thudClip, 0.9f); lastShot = "knife: TARGET"; }
        else { audioSrc.PlayOneShot(magInClip, 0.7f); if (dust) { dust.transform.SetPositionAndRotation(best.point + best.normal * 0.05f, Quaternion.LookRotation(best.normal)); dust.Emit(6); } lastShot = "knife: wall"; }
        camRig.lookDelta += new Vector2(heavy ? 0 : 0.6f * (Random.value - 0.5f), 0.5f);
    }
    public int knifeHits;

    void OnVmSound(string s)
    {
        audioSrc.pitch = Random.Range(0.95f, 1.05f);
        switch (s)
        {
            case "magout": audioSrc.PlayOneShot(magOutClip, 0.6f); break;
            case "magin": audioSrc.PlayOneShot(magInClip, 0.8f); break;
            case "boltback": audioSrc.PlayOneShot(boltClip, 0.6f); break;
            case "boltfwd": audioSrc.PlayOneShot(magInClip, 0.7f); break;
            case "swish": audioSrc.PlayOneShot(swishClip, 0.7f); break;
        }
    }

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
        var q = holes.Count >= 120 ? holes.Dequeue() : GameObject.CreatePrimitive(PrimitiveType.Quad);
        if (q.TryGetComponent<Collider>(out var c)) Destroy(c);
        q.GetComponent<Renderer>().sharedMaterial = holeMat;
        q.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        q.transform.SetPositionAndRotation(h.point + h.normal * 0.01f, Quaternion.LookRotation(-h.normal) * Quaternion.Euler(0, 0, Random.Range(0, 360)));
        q.transform.localScale = Vector3.one * Random.Range(0.06f, 0.11f) * (W.scope ? 1.6f : 1f);
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
    void LateUpdate()
    {
        if (chest && !camRig.firstPerson)
            chest.rotation = Quaternion.AngleAxis(camRig.pitch * 0.55f, transform.right) * chest.rotation;
    }

    void ShowWeapon()
    {
        if (rifle) rifle.gameObject.SetActive(current == 0);
        if (sniper) sniper.gameObject.SetActive(current == 1);
        if (knife) knife.gameObject.SetActive(current == 2);
    }

    ParticleSystem BuildDust()
    {
        var go = new GameObject("ImpactDust");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = false; main.playOnAwake = false; main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 1.1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 4f); main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.35f);
        main.startColor = new Color(0.62f, 0.56f, 0.46f, 0.8f); main.gravityModifier = 0.6f; main.maxParticles = 400;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 35; sh.radius = 0.02f;
        var col = ps.colorOverLifetime; col.enabled = true;
        var gr = new Gradient(); gr.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0.8f, 0), new GradientAlphaKey(0, 1) });
        col.color = gr;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = new Material(Shader.Find("Sprites/Default"));   // always in the build
        return ps;
    }

    // ---------- generated sounds ----------
    // a gunshot made from noise: sharp crack, then a decaying boom
    public static AudioClip Gunshot(float seconds, float body, int seed)
    {
        int rate = 44100, n = (int)(rate * seconds);
        var data = new float[n]; var rnd = new System.Random(seed); float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float noise = (float)(rnd.NextDouble() * 2 - 1);
            lp += (noise - lp) * Mathf.Clamp01(body / rate * 6.28f);
            float crack = noise * Mathf.Exp(-t * 90f);
            float boom = lp * Mathf.Exp(-t * (6f / seconds)) * 2.2f;
            data[i] = Mathf.Clamp((crack * 0.7f + boom) * 0.9f, -1, 1);
        }
        var clip = AudioClip.Create("shot", n, 1, rate, false); clip.SetData(data, 0); return clip;
    }

    // metal click: two short ringing ticks
    static AudioClip Click(float seconds, float freq, int seed)
    {
        int rate = 44100, n = (int)(rate * seconds); var data = new float[n]; var rnd = new System.Random(seed);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, t2 = Mathf.Max(0, t - seconds * 0.45f);
            float ring = Mathf.Sin(t * freq * 6.28f) * Mathf.Exp(-t * 70) + Mathf.Sin(t2 * freq * 1.3f * 6.28f) * Mathf.Exp(-t2 * 80) * (t2 > 0 ? 0.8f : 0);
            float tick = (float)(rnd.NextDouble() * 2 - 1) * (Mathf.Exp(-t * 400) + (t2 > 0 ? Mathf.Exp(-t2 * 400) : 0));
            data[i] = Mathf.Clamp(ring * 0.5f + tick * 0.6f, -1, 1);
        }
        var clip = AudioClip.Create("click", n, 1, rate, false); clip.SetData(data, 0); return clip;
    }

    // knife swish: band of noise sweeping up then down
    static AudioClip Swish(float seconds)
    {
        int rate = 44100, n = (int)(rate * seconds); var data = new float[n]; var rnd = new System.Random(21); float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n, env = Mathf.Sin(u * Mathf.PI) * Mathf.Sin(u * Mathf.PI);
            float cut = Mathf.Lerp(0.02f, 0.25f, Mathf.Sin(u * Mathf.PI));
            float noise = (float)(rnd.NextDouble() * 2 - 1);
            lp += (noise - lp) * cut; lp2 += (lp - lp2) * cut;
            data[i] = (lp - lp2) * env * 3f;
        }
        var clip = AudioClip.Create("swish", n, 1, rate, false); clip.SetData(data, 0); return clip;
    }

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
        else if (Aiming && !W.melee && camRig.firstPerson && aimT > 0.85f)
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
        Shadowed(new Rect(20, h - 70, 1500, 60), "LMB fire · RMB aim · R reload · 1/2/3 weapon · F knife · V view · Shift sprint / hold breath · C crouch · Space jump" + (lastShot != "" ? $"   last: {lastShot}" : ""), small);
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
