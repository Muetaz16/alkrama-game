using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Rifle + sniper for the third-person soldier.
// Left mouse / RT = fire, right mouse / LT = aim (sniper: scope), R / X = reload, 1-2 / Y = switch weapon.
// Shots go where the crosshair points; the sniper round is a real ballistic shot (850 m/s, gravity),
// so far targets need you to aim a little high. Recoil kicks the camera, tracers, flash, impact dust,
// bullet holes and a generated gunshot sound. Anything with a Damageable gets hit.
public class WeaponController : MonoBehaviour
{
    [System.Serializable]
    public class Weapon
    {
        public string name; public bool auto; public float rpm, damage, spread, aimSpread, recoil, range, muzzleVelocity;
        public int magSize, reserve; public float reloadTime, aimFov; public bool scope;
        [HideInInspector] public int mag;
    }

    public Weapon[] weapons =
    {
        new Weapon { name = "AK-47", auto = true, rpm = 600, damage = 34, spread = 1.6f, aimSpread = 0.35f, recoil = 1.1f, range = 400, muzzleVelocity = 0, magSize = 30, reserve = 180, reloadTime = 2.1f, aimFov = 45 },
        new Weapon { name = "Sniper", auto = false, rpm = 45, damage = 120, spread = 4f, aimSpread = 0.02f, recoil = 4.5f, range = 1200, muzzleVelocity = 850, magSize = 5, reserve = 30, reloadTime = 3f, aimFov = 11, scope = true },
    };
    public int current;
    public bool externalInput;                       // AutoTest drives these
    [HideInInspector] public bool fireHeld, aimHeld, reloadPressed; [HideInInspector] public int switchTo = -1;

    public bool Aiming { get; private set; }
    public Weapon W => weapons[current];

    PlayerController player; ThirdPersonCamera camRig; Camera cam; Animator anim;
    Transform rifle, sniper, rightHand, chest;
    float nextShot, reloadEnd, hitMarker, defaultFov, baseDistance;
    bool reloading;
    AudioSource audioSrc; AudioClip rifleClip, sniperClip, clickClip;
    LineRenderer tracer; float tracerOff; Light flash; float flashOff;
    ParticleSystem dust;
    readonly Queue<GameObject> holes = new();
    Material holeMat;
    float aimT;

    // like the web game: the gun lives in the right hand; its hold is measured once from the
    // "idle aiming" pose (pointing straight ahead, grip in the palm), so every animation carries it
    void CalibrateHold(Transform gun)
    {
        anim.SetFloat("VelX", 0); anim.SetFloat("VelZ", 0); anim.SetBool("Crouch", false);
        anim.Play("Locomotion", 0, 0f);
        anim.Update(0f);
        var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var palm = mid ? Vector3.Lerp(rightHand.position, mid.position, 0.6f) : rightHand.position;
        var rot = Quaternion.LookRotation(transform.forward, Vector3.up);
        gun.SetPositionAndRotation(palm - rot * new Vector3(0, -0.04f, 0), rot);   // grip block sits 4 cm under the gun origin
        gun.SetParent(rightHand, true);
    }

    void Start()
    {
        player = GetComponent<PlayerController>();
        camRig = FindFirstObjectByType<ThirdPersonCamera>();
        cam = camRig.GetComponent<Camera>();
        defaultFov = cam.fieldOfView; baseDistance = camRig.distance;
        anim = GetComponentInChildren<Animator>();
        rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        chest = anim.GetBoneTransform(HumanBodyBones.Chest) ?? anim.GetBoneTransform(HumanBodyBones.Spine);
        foreach (var w in weapons) w.mag = w.magSize;

        rifle = transform.GetComponentsInChildren<Transform>(true).FirstOrDefaultByName("Rifle");
        if (rifle && rightHand) { CalibrateHold(rifle); sniper = BuildSniper(rifle); }

        audioSrc = gameObject.AddComponent<AudioSource>(); audioSrc.spatialBlend = 0; audioSrc.volume = 0.8f;
        rifleClip = Gunshot(0.32f, 900f, 1); sniperClip = Gunshot(0.9f, 420f, 2); clickClip = Gunshot(0.03f, 3000f, 3);

        tracer = new GameObject("Tracer").AddComponent<LineRenderer>();
        tracer.material = new Material(Shader.Find("Sprites/Default"));
        tracer.startColor = new Color(1f, 0.85f, 0.5f, 0.9f); tracer.endColor = new Color(1f, 0.9f, 0.6f, 0.15f);
        tracer.startWidth = 0.025f; tracer.endWidth = 0.012f; tracer.positionCount = 2; tracer.enabled = false;
        flash = new GameObject("MuzzleFlash").AddComponent<Light>();
        flash.type = LightType.Point; flash.color = new Color(1f, 0.75f, 0.4f); flash.range = 6; flash.intensity = 0;
        dust = BuildDust();
        holeMat = new Material(Shader.Find("Universal Render Pipeline/Lit")); holeMat.SetColor("_BaseColor", new Color(0.05f, 0.045f, 0.04f)); holeMat.SetFloat("_Smoothness", 0);
        ShowWeapon();
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        if (switchTo >= 0 && switchTo != current && switchTo < weapons.Length) { current = switchTo; reloading = false; ShowWeapon(); }
        switchTo = -1;

        Aiming = aimHeld && !player.Sprinting;
        player.aiming = Aiming;
        // zoom in/out smoothly; while it runs the weapon owns the field of view
        aimT = Mathf.MoveTowards(aimT, Aiming ? 1f : 0f, Time.deltaTime / 0.18f);
        camRig.baseFov = defaultFov;
        camRig.aimingFov = aimT > 0.001f;
        if (camRig.aimingFov) cam.fieldOfView = Mathf.Lerp(defaultFov, W.aimFov, Mathf.SmoothStep(0, 1, aimT));
        camRig.distance = Mathf.Lerp(camRig.distance, Aiming ? (W.scope ? 0.6f : 1.9f) : baseDistance, 1 - Mathf.Exp(-12 * Time.deltaTime));

        if (reloadPressed && !reloading && W.mag < W.magSize && W.reserve > 0) { reloading = true; reloadEnd = Time.time + W.reloadTime; }
        reloadPressed = false;
        if (reloading && Time.time >= reloadEnd)
        {
            int need = W.magSize - W.mag, take = Mathf.Min(need, W.reserve);
            W.mag += take; W.reserve -= take; reloading = false;
        }

        if (fireHeld && !reloading && Time.time >= nextShot && !player.Sprinting && player.SprintLock <= 0)
        {
            if (W.mag <= 0) { audioSrc.PlayOneShot(clickClip, 0.5f); nextShot = Time.time + 0.3f; if (W.reserve > 0) { reloading = true; reloadEnd = Time.time + W.reloadTime; } }
            else { Fire(); nextShot = Time.time + 60f / W.rpm; }
            if (!W.auto) fireHeld = false;
        }

        if (Time.time > tracerOff) tracer.enabled = false;
        flash.intensity = Time.time < flashOff ? 6f : 0f;
        hitMarker -= Time.deltaTime;
    }

    void ReadInput()
    {
        var m = Mouse.current; var k = Keyboard.current; var g = Gamepad.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        fireHeld = (m != null && locked && (W.auto ? m.leftButton.isPressed : m.leftButton.wasPressedThisFrame)) || (g != null && (W.auto ? g.rightTrigger.isPressed : g.rightTrigger.wasPressedThisFrame));
        aimHeld = (m != null && m.rightButton.isPressed) || (g != null && g.leftTrigger.isPressed);
        reloadPressed = (k != null && k.rKey.wasPressedThisFrame) || (g != null && g.buttonWest.wasPressedThisFrame);
        if (k != null && k.digit1Key.wasPressedThisFrame) switchTo = 0;
        if (k != null && k.digit2Key.wasPressedThisFrame) switchTo = 1;
        if (g != null && g.buttonNorth.wasPressedThisFrame) switchTo = (current + 1) % weapons.Length;
        if (m != null && Mathf.Abs(m.scroll.ReadValue().y) > 0.1f) switchTo = (current + 1) % weapons.Length;
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
        W.mag--;
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
        tracer.enabled = true; tracerOff = Time.time + 0.04f;
        flash.transform.position = muzzle; flashOff = Time.time + 0.04f;
        audioSrc.pitch = Random.Range(0.93f, 1.05f);
        audioSrc.PlayOneShot(W.scope ? sniperClip : rifleClip);
        camRig.lookDelta += new Vector2(Random.Range(-0.25f, 0.25f) * W.recoil, W.recoil * (Aiming ? 0.6f : 1f));

        if (hit)
        {
            var d = h.collider.GetComponentInParent<Damageable>();
            if (d) { d.Hit(W.damage, h.point, dir); hitMarker = 0.25f; hits++; }
            else Hole(h);
            if (dust) dust.transform.SetPositionAndRotation(h.point + h.normal * 0.05f, Quaternion.LookRotation(h.normal));
            if (dust) dust.Emit(W.scope ? 26 : 12);
        }
        lastShot = hit ? $"{(d0(h) ? "TARGET " : "")}{h.distance:F0} m" : "miss";
    }
    string lastShot = "";
    static bool d0(RaycastHit h) => h.collider.GetComponentInParent<Damageable>() != null;

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

    // first hit that isn't the soldier himself (the camera sits behind his shoulder)
    bool RayNotSelf(Vector3 o, Vector3 d, float range, out RaycastHit best)
    {
        best = default; float bd = float.MaxValue; bool any = false;
        foreach (var h in Physics.RaycastAll(o, d, range, ~0, QueryTriggerInteraction.Ignore))
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

    Vector3 Muzzle() => (W.scope && sniper ? sniper : rifle) is Transform t && t ? t.position + t.forward * (W.scope ? 1.05f : 0.68f) + t.up * 0.07f : transform.position + Vector3.up * 1.4f;

    // hold the gun in the right hand, pointing at the crosshair; lean the chest with the camera pitch
    void LateUpdate()
    {
        if (chest)
        {
            float pitch = camRig.pitch;
            chest.rotation = Quaternion.AngleAxis(pitch * 0.55f, transform.right) * chest.rotation;
        }

    }

    void ShowWeapon()
    {
        if (rifle) rifle.gameObject.SetActive(!W.scope || !sniper);
        if (sniper) sniper.gameObject.SetActive(W.scope);
    }

    Transform BuildSniper(Transform ak)
    {
        var s = Instantiate(ak.gameObject, ak.parent).transform; s.name = "Sniper";
        s.SetLocalPositionAndRotation(ak.localPosition, ak.localRotation); s.localScale = ak.localScale;
        foreach (Transform p in s)
        {
            if (p.name == "Barrel") { p.localScale = new Vector3(0.022f, 0.022f, 0.75f); p.localPosition = new Vector3(0, 0.07f, 0.66f); }
            if (p.name == "Magazine") p.localScale = new Vector3(0.03f, 0.07f, 0.08f);
        }
        var scope = GameObject.CreatePrimitive(PrimitiveType.Cylinder); scope.name = "Scope";
        Destroy(scope.GetComponent<Collider>());
        scope.transform.SetParent(s, false); scope.transform.localPosition = new Vector3(0, 0.14f, 0.12f);
        scope.transform.localRotation = Quaternion.Euler(90, 0, 0); scope.transform.localScale = new Vector3(0.045f, 0.16f, 0.045f);
        scope.GetComponent<Renderer>().sharedMaterial = ak.GetComponentInChildren<Renderer>().sharedMaterial;
        s.gameObject.SetActive(false);
        return s;
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

    // a gunshot made from noise: sharp crack, then a decaying boom
    static AudioClip Gunshot(float seconds, float body, int seed)
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

    void OnGUI()
    {
        float w = Screen.width, h = Screen.height, cx = w / 2, cy = h / 2;
        var white = Texture2D.whiteTexture;
        if (Aiming && W.scope)
        {
            // scope: black outside a circle-ish window, thin cross-hairs
            float r = h * 0.46f;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, cx - r, h), white); GUI.DrawTexture(new Rect(cx + r, 0, w - cx - r, h), white);
            GUI.DrawTexture(new Rect(cx - r, 0, 2 * r, cy - r), white); GUI.DrawTexture(new Rect(cx - r, cy + r, 2 * r, h - cy - r), white);
            GUI.DrawTexture(new Rect(0, cy - 0.5f, w, 1), white); GUI.DrawTexture(new Rect(cx - 0.5f, 0, 1, h), white);
            for (int i = 1; i <= 4; i++) GUI.DrawTexture(new Rect(cx - 6, cy + i * h * 0.04f, 12, 1), white);   // drop marks
        }
        else
        {
            float gap = (Aiming ? 4 : 9) + (player ? player.moveInput.magnitude * 8 : 0);
            GUI.color = new Color(1, 1, 1, 0.9f);
            GUI.DrawTexture(new Rect(cx - 1, cy - gap - 8, 2, 8), white); GUI.DrawTexture(new Rect(cx - 1, cy + gap, 2, 8), white);
            GUI.DrawTexture(new Rect(cx - gap - 8, cy - 1, 8, 2), white); GUI.DrawTexture(new Rect(cx + gap, cy - 1, 8, 2), white);
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
        var small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.022f) };
        Shadowed(new Rect(w - 420, h - 110, 400, 90), $"{W.name}\n{(reloading ? "RELOADING" : $"{W.mag} / {W.reserve}")}", style);
        Shadowed(new Rect(20, h - 70, 1200, 60), "LMB fire · RMB aim · R reload · 1/2 weapon · Shift sprint · C crouch · Space jump · M mark spot" + (lastShot != "" ? $"   last shot: {lastShot}" : ""), small);
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
