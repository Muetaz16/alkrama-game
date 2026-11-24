using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Enemy soldier - the behaviour of the web game, on the city's navigation map:
//   calm         strolls round where he appeared
//   sees you     tells the others close by, runs for real cover (a car, a barrier, a wall corner) and fires
//                on the move; behind cover he crouches, pops up (low cover) or leans out (cars, walls) for a
//                burst, ducks again; reloads behind cover; if you can see him while he hides he moves
//   flanker      one in three swings wide round your side before joining in
//   no cover     strafes left / right while firing, keeps 10-24 m away
//   you hide     lobs a grenade at your last spot; everyone runs from a grenade near him
//   lost you     after 6-11 s goes hunting where you were last seen
// Rifle and upper body aim at you; recoil, muzzle flash, tracers, impacts, footsteps, bullets whizzing past.
//
// He is meant to look drilled, not scripted:
//   contact      whoever sees you first drops to a knee and returns fire while his mates run for cover
//   movement     cover to cover in short bounds: sprints the open stretch without firing, backs into the last
//                metres facing you; never more than one or two up at once - the others put rounds down
//   fire         a reaction time, sights must settle, short bursts with a pause to find you again; worse when
//                he moves, at range, late in a burst, when your rounds crack past him or he has been hit
//   cover        leans out either side (whichever has a view) or pops up, never the same rhythm twice, shifts to
//                another position after a few peeks; reloads crouched; ducks at once when shot at
//   search       runs most of the way to where you were, walks the last metres rifle up, stops, looks round,
//                checks two or three more places before he calms down
//   motion       turns, speeds, torso twist, aim and the hand on the rifle are all eased, nothing snaps
public class EnemyAI : Damageable
{
    public static readonly List<EnemyAI> All = new();
    public static int ShotsFired, GrenadesThrown;
    const float Range = 60f, Hearing = 45f;
    const int Mag = 30;
    // paces chosen to sit on the clips (walk 1.92, run 4.8, sprint 7.2, crouch-walk 2.04 m/s) so the feet stay planted
    const float StrollS = 1.6f, TacticalS = 2.1f, ApproachS = 4f, RunS = 4.8f, SprintS = 6.6f, CrouchS = 1.9f;
    const float ReloadTime = 2.4f, ContactRange = 25f, BoundLen = 14f;
    const float AccNear = 0.30f, AccFar = 0.06f;            // chance per round at 10 m / 60 m, standing still, sights settled
    const float SettleTime = 0.5f, SuppRecover = 0.22f;     // s to steady the sights; suppression lost per second
    const float TwistMax = 40f;                             // how far the torso turns to you before the feet follow
    public enum State { Patrol, Hunt, Cover, Flank, Open, Evade }
    public State Mode { get; private set; } = State.Patrol;
    public bool Crouched => crouchT > 0.5f;
    public int CoverMoves { get; private set; }
    public bool SeesPlayer => sees;
    public bool IsDead => dead;
    public float PathLeft => agent && agent.isOnNavMesh ? agent.remainingDistance : -1;

    struct CoverSpot { public Vector3 pos, side; public float top; }

    NavMeshAgent agent; CharacterController cc; Animator anim; Transform player; PlayerHealth ph; PlayerController pc;
    Transform rifle, muzzle, flash, gripL, armL, foreL, handL; Transform[] spine; Quaternion rifleLocalRot; Vector3 rifleLocalPos;
    float raiseW, hitP, hitR, hitVP, hitVR, curSpeed; bool sprinting, wantCrouchNow;
    bool alerted, sees, dead, flanker, flankDone, flankSet, hasCover, peeking, raised;
    int flankSide, ammo = Mag, burst, grenades = 1, strafeDir = 1;
    float seenT = 99, firstSeen = -99, shootT, reloadT, coverCheckT, phaseT, exposedT, stuckT, wanderT, nadeCool,
          crouchT, aimW, recoil, flashOff, tracerOff, staggerUntil, nextLook, distToPlayer = 999, stepDist, lookSeed;
    Vector3? lastSeen; Vector3 wander, home, flankPos, goalSet = new Vector3(1e6f, 0, 1e6f);
    CoverSpot cover;
    // discipline: suppression (0-1), sight settle (0-1), reaction, timers that differ per man so they never move in step
    float supp, settle, react = 0.45f, tempo = 1, huntAfter = 7, contactUntil, duckUntil, flinchUntil, hurtUntil, coverRetryAt,
          heardShot, scanT, idleYaw, openT, yawVel, twist, aimPitch, aimErr = 180, ikW = 1;
    bool bounding, support, scanning, searching, dashing, mateMoving, hasAim;
    int peeksLeft, peekSide, burstShot, searchLeft, matesPeek, matesCover;
    Vector3 peekPos, dashPos, aimPoint, avoidPos = new Vector3(1e6f, 0, 1e6f);
    static readonly RaycastHit[] rayBuf = new RaycastHit[64];
    static readonly Collider[] colBuf = new Collider[384];
    static int spawnCount;
    static Texture2D flashTex;      // sounds are CombatAudio, tracers / impacts / blood are CombatFx
    static readonly int VelX = Animator.StringToHash("VelX"), VelZ = Animator.StringToHash("VelZ"),
        CrouchP = Animator.StringToHash("Crouch"), AnimSpeed = Animator.StringToHash("AnimSpeed"), RaiseP = Animator.StringToHash("Raise");

    static float R => Random.value;
    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

    void Start()
    {
        health = 100; fallsOver = false;
        lookSeed = Random.Range(0f, 6.28f);
        cc = GetComponent<CharacterController>(); anim = GetComponentInChildren<Animator>();
        pc = FindFirstObjectByType<PlayerController>();
        if (pc) { player = pc.transform; ph = pc.GetComponent<PlayerHealth>(); }
        All.Add(this);
        flanker = spawnCount++ % 3 == 2; flankSide = R < 0.5f ? -1 : 1;
        nadeCool = 6 + R * 6;
        tempo = 0.85f + R * 0.4f; huntAfter = 6 + R * 5; nextLook = Time.time + R * 0.1f;      // nobody shares a clock
        heardShot = WeaponController.LastShotTime; idleYaw = transform.eulerAngles.y;
        // desert-tan kit so he reads differently from your grey one
        foreach (var r in GetComponentsInChildren<Renderer>())
            foreach (var m in r.materials) if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.GetColor("_BaseColor") * new Color(0.95f, 0.72f, 0.46f));
        if (anim)
        {
            var list = new List<Transform>();
            foreach (var bone in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest }) { var t = anim.GetBoneTransform(bone); if (t) list.Add(t); }
            spine = list.ToArray();
            armL = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); foreL = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm); handL = anim.GetBoneTransform(HumanBodyBones.LeftHand);
        }
        SetupGun();
        SetupFx();

        agent = GetComponent<NavMeshAgent>(); if (!agent) agent = gameObject.AddComponent<NavMeshAgent>();
        agent.radius = 0.35f; agent.height = 1.8f; agent.baseOffset = 0;
        agent.acceleration = 11f; agent.angularSpeed = 0;      // a man takes half a second to get up to a run agent.updateRotation = false; agent.autoBraking = true;
        agent.stoppingDistance = 0.25f; agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
        agent.avoidancePriority = Random.Range(30, 70);
        if (NavMesh.SamplePosition(transform.position, out var nh, 4f, NavMesh.AllAreas)) agent.Warp(nh.position);
        home = wander = transform.position;
    }

    void OnDestroy() { All.Remove(this); }

    // ---------- setup ----------
    void SetupGun()
    {
        var hand = anim ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null; if (!hand) return;
        foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Rifle") rifle = t;
        if (!rifle) { rifle = new GameObject("Rifle").transform; rifle.SetParent(hand, false); }
        // measure the hold in the "idle aiming" pose (rifle pointing straight ahead), like the web game
        var culling = anim.cullingMode; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // off-screen it would skip the pose
        anim.SetFloat(VelX, 0); anim.SetFloat(VelZ, 0); anim.SetFloat(RaiseP, 1f); anim.SetBool(CrouchP, false);
        anim.Play("Locomotion", 0, 0f);
        anim.Update(0f); anim.Update(0f);
        var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var palm = mid ? Vector3.Lerp(hand.position, mid.position, 0.6f) : hand.position;
        var rot = Quaternion.LookRotation(transform.forward, Vector3.up);
        rifle.SetPositionAndRotation(palm - rot * new Vector3(0, -0.04f, 0), rot);
        rifle.SetParent(hand, true);
        foreach (Transform c in rifle) Destroy(c.gameObject);
        var gun = GunFactory.AK(rifle, false);
        foreach (var r in gun.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        muzzle = GunFactory.Find(gun, "Muzzle"); gripL = GunFactory.Find(gun, "GripL");
        rifleLocalPos = rifle.localPosition; rifleLocalRot = rifle.localRotation;
        anim.cullingMode = culling;
    }

    void SetupFx()
    {
        if (!flashTex) flashTex = FlashTexture(64);
        if (muzzle)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(q.GetComponent<Collider>());
            q.name = "Flash"; flash = q.transform; flash.SetParent(muzzle, false); flash.localScale = Vector3.one * 0.32f;
            var mr = q.GetComponent<MeshRenderer>(); mr.material = new Material(Shader.Find("Sprites/Default")) { mainTexture = flashTex };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var fl = q.AddComponent<Light>(); fl.type = LightType.Point; fl.range = 5; fl.intensity = 4; fl.color = new Color(1f, 0.75f, 0.4f);
            q.SetActive(false);
        }
    }

    // ---------- senses ----------
    Vector3 Eye => transform.position + Vector3.up * (1.6f - 0.55f * crouchT);
    Vector3 PlayerTarget => player.position + Vector3.up * (pc && pc.crouch ? 0.95f : 1.4f) + (pc ? player.right * pc.Lean * 0.3f : Vector3.zero);

    bool FirstHit(Vector3 a, Vector3 b, out RaycastHit first, bool skipPlayer)
    {
        first = default; float best = float.MaxValue; bool any = false;
        var d = b - a; float len = d.magnitude; if (len < 0.01f) return false;
        int n = Physics.RaycastNonAlloc(a, d / len, rayBuf, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);   // shared buffer: no garbage
        for (int i = 0; i < n; i++)
        {
            var h = rayBuf[i];
            if (h.collider.transform.IsChildOf(transform)) continue;
            if (skipPlayer && player && h.collider.transform.IsChildOf(player)) continue;
            if (h.collider.GetComponentInParent<EnemyAI>()) continue;
            if (h.distance < best) { best = h.distance; first = h; any = true; }
        }
        return any;
    }

    void Look()
    {
        if (!player || (ph && ph.Dead)) { sees = false; return; }
        var to = PlayerTarget - Eye; distToPlayer = to.magnitude;
        bool inView = alerted || distToPlayer < 8f || Vector3.Angle(transform.forward, to) < 75f;
        bool was = sees;
        sees = distToPlayer < Range && inView && !FirstHit(Eye, PlayerTarget, out _, true);
        // what the mates near him are doing (read by the cover logic: who is up and moving, who is peeking)
        int near = 0, holders = 0; mateMoving = false; matesPeek = matesCover = 0;
        foreach (var o in All)
        {
            if (o == this || o.dead || Flat(o.transform.position, transform.position) > 40f) continue;
            near++; if (o.bounding) mateMoving = true;
            if (o.support && Time.time < o.contactUntil) holders++;
            if (o.hasCover) { matesCover++; if (o.peeking) matesPeek++; }
        }
        if (sees)
        {
            if (!was && seenT > 3f)
            {
                firstSeen = Time.time; react = 0.3f + R * 0.25f + (alerted ? 0 : 0.2f);      // takes a moment to react, longer if surprised
                settle = 0; contactUntil = 0;
                if (distToPlayer < ContactRange) flankDone = true;                        // too close to go round you: fight
                if (distToPlayer < ContactRange && !AtCover)
                {
                    // contact drill: the first to see you holds and returns fire, the rest get one burst off and move
                    support = near == 0 || holders * 2 < near;
                    contactUntil = Time.time + react + (near == 0 ? 1.1f + R * 0.6f : support ? 2.2f + R * 1.2f : 0.5f + R * 0.4f);
                }
            }
            alerted = true; Known(player.position);
            foreach (var o in All)                                       // shouts it to his mates
                if (o != this && !o.dead && Flat(o.transform.position, transform.position) < 35f) o.Alert(player.position);
        }
        else if (Time.time - WeaponController.LastShotTime < 0.3f && distToPlayer < Hearing) { alerted = true; Known(player.position); }
    }

    bool AtCover => hasCover && Flat(transform.position, cover.pos) < 0.9f;

    // fresh word of where you are: drop any search he was on
    void Known(Vector3 at) { lastSeen = at; searching = scanning = false; searchLeft = 2 + (lookSeed > 3.14f ? 1 : 0); }

    // told where the player is: he joins the fight
    public void Alert(Vector3 at)
    {
        if (dead) return;
        alerted = true; if (!sees) Known(at);
    }

    // your shot: did it crack past him? Then he ducks, loses his aim for a while and knows roughly where you are
    void Incoming()
    {
        var cam = Camera.main; if (!cam || !player) return;
        Vector3 f = cam.transform.forward, o = cam.transform.position;
        o += f * Mathf.Max(0, Vector3.Dot(player.position + Vector3.up * 1.5f - o, f) + 0.6f);        // start past your own body (third person)
        var c = transform.position + Vector3.up * 1.1f - o;
        float along = Vector3.Dot(c, f); if (along < 1f || along > 160f) return;
        float miss = (c - f * along).magnitude; if (miss > 2.2f) return;
        if (along > 3f && FirstHit(o, o + f * (along - 2.5f), out _, true)) return;                    // stopped by a wall well short of him
        supp = Mathf.Min(1, supp + Mathf.Lerp(0.45f, 0.2f, miss / 2.2f));
        duckUntil = Time.time + 0.5f + R * 0.6f; flinchUntil = Time.time + 0.3f + R * 0.2f; hitVP += 3.5f;
        alerted = true; if (!sees) Known(player.position);
        if (peeking) { peeking = false; phaseT = (1f + R * 1.5f) * tempo; }                          // back down behind it
        if (!hasCover) coverRetryAt = 0;
    }

    // ---------- cover ----------
    // real cover: the far side (from you) of a car / barrier / drum / sandbag wall, or a building corner
    bool PickCover(Vector3 threat, out CoverSpot best)
    {
        CoverSpot bestSpot = default; float bestScore = float.MaxValue; bool found = false;
        var tChest = threat + Vector3.up * 1.3f;
        void Try(Vector3 spot, float top, Vector3 side)
        {
            if (!NavMesh.SamplePosition(spot, out var h, 0.9f, NavMesh.AllAreas) || h.position.y < -0.3f || h.position.y > 0.8f) return;
            var p = h.position; float dP = Flat(p, threat);
            if (dP < 7f || dP > 45f) return;
            foreach (var o in All) if (o != this && o.hasCover && Flat(o.cover.pos, p) < 2.5f) return;     // don't bunch up
            // crouched he must be hidden: three rays at 0.95 m (left, middle, right) all stopped close to him
            var perp = Vector3.Cross(Vector3.up, (tChest - p).normalized) * 0.3f;
            for (int k = -1; k <= 1; k++)
                if (!FirstHit(p + Vector3.up * 0.95f + perp * k, tChest + perp * k, out var hit, true) || hit.distance > 2.2f) return;
            // nearest first, about 18 m from you; a long run in the open costs extra (short bounds), and so does
            // the position he is leaving (after a few peeks you know where he is)
            float dS = Flat(transform.position, p);
            float score = dS + Mathf.Max(0, dS - BoundLen) + Mathf.Abs(dP - 18f) * 0.35f + (Flat(p, avoidPos) < 2.5f ? 14f : 0);
            if (score < bestScore) { bestScore = score; bestSpot = new CoverSpot { pos = p, top = top, side = side }; found = true; }
        }
        // objects: cars, barriers, sandbags, drums - anything 0.8-3 m tall and up to 7 m long standing on the street
        int nCol = Physics.OverlapSphereNonAlloc(transform.position, 22f, colBuf, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int ci = 0; ci < nCol; ci++)
        {
            var c = colBuf[ci];
            if (c.transform.IsChildOf(transform) || (player && c.transform.IsChildOf(player)) || c.GetComponentInParent<EnemyAI>()) continue;
            var b = c.bounds;
            float len = Mathf.Max(b.size.x, b.size.z);
            if (b.min.y > 0.6f || b.size.y < 0.8f || b.size.y > 3.2f || len > 7f || len < 0.5f) continue;
            var away = b.center - threat; away.y = 0; if (away.sqrMagnitude < 1e-3f) continue; away.Normalize();
            float ext = b.extents.x * Mathf.Abs(away.x) + b.extents.z * Mathf.Abs(away.z);
            Try(new Vector3(b.center.x, 0, b.center.z) + away * (ext + 0.55f), b.max.y, new Vector3(-away.z, 0, away.x));
        }
        // walls and corners: points round him, kept only if a wall really shields them
        for (int i = 0; i < 14; i++)
        {
            var r = Random.insideUnitCircle * 15f;
            var p = transform.position + new Vector3(r.x, 0, r.y);
            var away = p - threat; away.y = 0; away.Normalize();
            Try(p, 3f, new Vector3(-away.z, 0, away.x));
        }
        best = bestSpot;
        return found;
    }

    // ---------- every frame ----------
    void Update()
    {
        if (dead) return;
        float dt = Time.deltaTime, now = Time.time;
        if (reloadT > 0) { reloadT -= dt; if (reloadT <= 0) ammo = Mag; }
        if (now >= nextLook) { nextLook = now + 0.15f; Look(); }
        if (sees) seenT = 0; else seenT += dt;
        supp = Mathf.MoveTowards(supp, 0, dt * SuppRecover);
        if (WeaponController.LastShotTime != heardShot) { heardShot = WeaponController.LastShotTime; Incoming(); }

        Vector3? goal = null, face = null; float speed = 0; bool wantCrouch = false, canShoot = false, weaponUp = false;
        bounding = false;
        var pos = transform.position;
        var nade = EnemyGrenade.Near(pos, EnemyGrenade.Radius + 1.5f);
        if (nade)
        {
            // grenade! run away from it
            var away = pos - nade.position; away.y = 0;
            goal = pos + away.normalized * 8f; speed = SprintS; hasCover = false; coverRetryAt = 0; peeking = dashing = false; Mode = State.Evade;
        }
        else if (!alerted)
        {
            Mode = State.Patrol;
            wanderT -= dt;
            if (wanderT <= 0)
            {
                wanderT = 5 + R * 6;
                var r = Random.insideUnitCircle * 7f; wander = home + new Vector3(r.x, 0, r.y);
            }
            if (Flat(pos, wander) > 0.6f) { goal = wander; speed = StrollS; idleYaw = transform.eulerAngles.y; }
            // stands and looks up and down the street (round the heading he arrived with - not round himself, that made him spin)
            else face = pos + Quaternion.Euler(0, idleYaw + Mathf.Sin(now * 0.45f + lookSeed) * 55f, 0) * Vector3.forward;
        }
        else if (!sees && seenT > huntAfter && lastSeen.HasValue)
        {
            // lost you for a while: go and look where you were last seen - quickly while it is far, then slow,
            // rifle up; stop there and look round, then check a couple of places near by before standing down
            Mode = State.Hunt; hasCover = false; peeking = dashing = false;
            float d = Flat(pos, lastSeen.Value);
            if (d > 1.8f && !scanning)
            {
                bool close = d < 14f;
                goal = lastSeen; speed = close ? TacticalS : RunS; weaponUp = close; idleYaw = transform.eulerAngles.y;
            }
            else
            {
                if (!scanning) { scanning = true; scanT = 1.2f + R * 1.4f; }
                scanT -= dt; weaponUp = true;
                face = pos + Quaternion.Euler(0, idleYaw + Mathf.Sin(now * 1.3f + lookSeed) * 75f, 0) * Vector3.forward;
                if (scanT <= 0)
                {
                    scanning = false;
                    if (searchLeft-- > 0 && SearchPoint(out var next)) { lastSeen = next; searching = true; }
                    else { lastSeen = null; alerted = searching = false; home = wander = pos; wanderT = 2f; }
                }
            }
        }
        else if (flanker && !flankDone && lastSeen.HasValue)
        {
            // swing wide round your side before engaging: head down and run, shoot only if he bumps into you
            Mode = State.Flank; bounding = true;
            if (!flankSet)
            {
                var toP = lastSeen.Value - pos; toP.y = 0; toP.Normalize();
                flankPos = lastSeen.Value + new Vector3(-toP.z, 0, toP.x) * 14f * flankSide - toP * 4f; flankSet = true;
            }
            goal = flankPos; speed = RunS;
            if (sees && distToPlayer < 14f) { canShoot = true; face = player.position; }
            if (Flat(pos, flankPos) < 2f) flankDone = true;
        }
        else
        {
            coverCheckT -= dt;
            var threat = lastSeen ?? (player ? player.position : pos);
            bool at = AtCover;
            if (now < contactUntil && sees && !at)
            {
                // contact: down on a knee where he stands and return fire (see Look) - then he moves
                Mode = State.Open; face = threat; canShoot = true; wantCrouch = distToPlayer > 8f; dashing = false;
            }
            else
            {
                bool tooClose = hasCover && Flat(cover.pos, threat) < 6f;
                bool need = hasCover ? coverCheckT <= 0 || tooClose : now >= coverRetryAt;
                // one moves, the others cover him: a planned change of position waits while a mate is up and running
                if (need && hasCover && !tooClose && at && mateMoving) { coverCheckT = 0.8f + R * 1.2f; need = false; }
                if (need)
                {
                    bool had = hasCover && !tooClose; var old = cover;
                    if (at) avoidPos = cover.pos;                                     // leaving a position: don't come straight back to it
                    hasCover = PickCover(threat, out cover);
                    if (hasCover) CoverMoves++;
                    else if (had) { cover = old; hasCover = true; }                   // nowhere better: stay put
                    // searching for cover costs a few dozen rays: when there is none, look again in 2 s, not every frame
                    coverCheckT = 10 + R * 6; coverRetryAt = now + 1.5f + R; peeking = false; exposedT = 0;
                    phaseT = (0.6f + R * 0.9f) * tempo; peeksLeft = 2 + Random.Range(0, 3);
                }
                if (hasCover)
                {
                    Mode = State.Cover; dashing = false;
                    bool tall = cover.top > 1.3f; float dc = Flat(pos, cover.pos);
                    if (dc > 0.7f && !peeking)
                    {
                        goal = cover.pos;
                        if (dc > 5f)
                        {
                            // a bound: up, run, down. Flat out if you can see him or he is under fire; no hip fire on the way
                            speed = sees || seenT < 2f || now < hurtUntil || supp > 0.3f ? SprintS : RunS; bounding = true;
                        }
                        else if (dc > 1.8f)
                        {
                            // the last metres he backs in facing you, snap-shooting if you're in view
                            speed = ApproachS; bounding = true;
                            if (seenT < 1.5f) { face = threat; canShoot = sees && distToPlayer < 30f; }
                        }
                        else { speed = CrouchS; wantCrouch = true; face = threat; }           // settles in low
                    }
                    else
                    {
                        phaseT -= dt * (peeking ? 1f : 1f / (1f + supp * 1.5f));          // rounds coming in: stays down longer
                        face = threat;
                        if (!peeking)
                        {
                            wantCrouch = true;
                            if (ammo < 10 && reloadT <= 0) reloadT = ReloadTime;
                            // a mate is up and moving: get rounds down now, don't wait out the pause
                            if (mateMoving && reloadT <= 0 && supp < 0.5f && phaseT > 0.35f) phaseT = 0.35f;
                            if (phaseT <= 0 && reloadT <= 0)
                            {
                                // not everyone up at the same moment: with half the others already firing he waits his turn
                                if (!mateMoving && matesPeek * 2 > matesCover) phaseT = 0.3f + R * 0.5f;
                                else StartPeek(threat, tall);
                            }
                            // spotted while hiding? shoot back from the knee, and this cover is no good: find another
                            if (sees) { exposedT += dt; canShoot = exposedT > 0.35f; if (exposedT > 1.2f) { avoidPos = cover.pos; hasCover = false; coverRetryAt = 0; exposedT = 0; } }
                            else exposedT = 0;
                        }
                        else
                        {
                            // peek: lean / step out to the side that has a view, or stand up over low cover
                            if (peekSide != 0) { goal = peekPos; speed = tall ? TacticalS : CrouchS; wantCrouch = !tall; }
                            bool pin = !sees && seenT < 4f && lastSeen.HasValue;             // lost you a moment ago: keeps your head down
                            canShoot = sees || pin;
                            if (phaseT <= 0 || ammo <= 0 || reloadT > 0 || (!sees && !pin && phaseT < 0.7f))
                            {
                                peeking = false; phaseT = (1.2f + R * 2.2f) * tempo;
                                if (peeksLeft <= 0) coverCheckT = 0;                         // you know this spot by now: shift
                            }
                        }
                    }
                }
                else
                {
                    // no cover around: kneel and fire, then a short dash to one side and down again - a moving
                    // target between bursts, not a dancer; keeps a fighting distance
                    Mode = State.Open;
                    var toP = threat - pos; toP.y = 0; float dP = toP.magnitude; toP /= Mathf.Max(dP, 0.01f);
                    openT -= dt * (dP < 7f ? 3f : 1f);
                    if (dashing)
                    {
                        goal = dashPos; speed = RunS; bounding = true;
                        if (Flat(pos, dashPos) < 0.8f || openT <= 0) { dashing = false; openT = (2f + R * 2.2f) * tempo; }
                    }
                    else
                    {
                        face = threat; canShoot = sees; wantCrouch = dP > 8f;
                        if (ammo < 8 && !sees && reloadT <= 0) reloadT = ReloadTime;
                        if (openT <= 0 && reloadT <= 0 && !(mateMoving && dP > 9f))
                        {
                            if (R < 0.4f) strafeDir = -strafeDir;
                            var c = pos + new Vector3(-toP.z, 0, toP.x) * strafeDir * (4f + R * 3f) + toP * (dP > 26f ? 5f : dP < 12f ? -5f : (R - 0.5f) * 3f);
                            if (NavMesh.SamplePosition(c, out var dh, 1.5f, NavMesh.AllAreas) && !NavMesh.Raycast(pos, dh.position, out _, NavMesh.AllAreas))
                            { dashPos = dh.position; dashing = true; openT = 2.5f; }
                            else { strafeDir = -strafeDir; openT = 0.4f; }
                        }
                    }
                }
            }
            // you're hiding: flush you out with a grenade (not onto his own mates, and facing the way he throws)
            nadeCool -= dt;
            if (grenades > 0 && nadeCool <= 0 && !sees && seenT > 2.5f && seenT < 12f && lastSeen.HasValue)
            {
                float d = Flat(pos, lastSeen.Value);
                bool clear = Vector3.Angle(transform.forward, Vector3.ProjectOnPlane(lastSeen.Value - pos, Vector3.up)) < 50f;
                foreach (var o in All) if (o != this && Flat(o.transform.position, lastSeen.Value) < EnemyGrenade.Radius) clear = false;
                if (!clear) nadeCool = 0.5f;
                else if (d > 7f && d < 26f) { EnemyGrenade.Throw(Eye + transform.forward * 0.4f + Vector3.up * 0.15f, lastSeen.Value, cc); grenades--; nadeCool = 14f; GrenadesThrown++; }
            }
        }

        // stance: a round just cracked past - he gets small, unless he is running for it
        if (now < duckUntil && alerted && speed <= TacticalS + 0.1f) wantCrouch = true;
        if (wantCrouch && speed > CrouchS) speed = CrouchS;
        crouchT = Mathf.MoveTowards(crouchT, wantCrouch ? 1 : 0, dt * 4f);

        // movement on the navigation map
        if (agent.isOnNavMesh)
        {
            agent.isStopped = Time.time < staggerUntil;
            if (goal.HasValue)
            {
                agent.speed = speed;
                if ((goal.Value - goalSet).sqrMagnitude > 0.6f)
                {
                    goalSet = goal.Value;
                    if (NavMesh.SamplePosition(goal.Value, out var h, 2.5f, NavMesh.AllAreas)) agent.SetDestination(h.position);
                    else stuckT = 2f;
                }
                // stuck against something? give up on that goal
                bool stalled = !agent.pathPending && agent.remainingDistance > 1f && agent.velocity.magnitude < speed * 0.25f;
                stuckT = stalled ? stuckT + dt : Mathf.Max(0, stuckT - dt);
                if (stuckT > 1.2f)
                {
                    if (hasCover) { avoidPos = cover.pos; coverRetryAt = 0; }
                    hasCover = false; peeking = dashing = false; strafeDir *= -1; wanderT = 0; if (flankSet) flankDone = true;
                    if (searching && lastSeen.HasValue) lastSeen = pos;                  // can't get to that corner: look round from here
                    stuckT = 0; goalSet = new Vector3(1e6f, 0, 1e6f);
                }
            }
            else if (agent.hasPath) { agent.ResetPath(); goalSet = new Vector3(1e6f, 0, 1e6f); }
        }

        // facing: you when he's fighting, otherwise where he's going. Eased - quick in a fight, lazy on patrol -
        // because a snap turn is the first thing that gives a puppet away
        Vector3 dir = face.HasValue ? face.Value - pos : agent.desiredVelocity.sqrMagnitude > 0.1f ? agent.desiredVelocity : transform.forward;
        dir.y = 0;
        float yaw = transform.eulerAngles.y;
        if (dir.sqrMagnitude > 0.01f && now >= staggerUntil)
        {
            bool fight = alerted && face.HasValue;
            yaw = Mathf.SmoothDampAngle(yaw, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, ref yawVel, fight ? 0.11f : alerted ? 0.16f : goal.HasValue ? 0.2f : 0.4f, fight ? 540f : alerted ? 400f : goal.HasValue ? 300f : 120f, dt);
            transform.rotation = Quaternion.Euler(0, yaw, 0);
        }
        else yawVel = 0;

        // where the rifle points: at you if he sees you, else at the last place he knew
        hasAim = alerted && player && (sees || lastSeen.HasValue) && Mode != State.Evade;
        if (hasAim) { aimPoint = sees ? PlayerTarget : lastSeen.Value + Vector3.up * 1.3f; hasAim = Flat(pos, aimPoint) > 2f; }
        raised = alerted && (canShoot || peeking || sees || weaponUp);
        aimErr = 180f;
        if (hasAim) { var to = aimPoint - pos; aimErr = Mathf.Abs(Mathf.DeltaAngle(yaw + twist, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg)); }
        curSpeed = goal.HasValue ? speed : 0; sprinting = speed > 5.5f && goal.HasValue; wantCrouchNow = wantCrouch;
        Animate(dt, wantCrouch);
        Footsteps(dt);
        Shooting(dt, canShoot && now >= flinchUntil);
        if (flash && Time.time > flashOff && flash.gameObject.activeSelf) flash.gameObject.SetActive(false);
    }

    // a place to look next, a few metres from where he stands (the route there may go round a corner)
    bool SearchPoint(out Vector3 p)
    {
        for (int i = 0; i < 4; i++)
        {
            var r = Random.insideUnitCircle.normalized * (6f + R * 6f);
            if (NavMesh.SamplePosition(transform.position + new Vector3(r.x, 0, r.y), out var h, 2f, NavMesh.AllAreas) && Mathf.Abs(h.position.y - transform.position.y) < 1f) { p = h.position; return true; }
        }
        p = default; return false;
    }

    // out from behind cover. Tall cover (car, wall): step out past it - on the side that really has a view of you,
    // not always the same one. Low cover: mostly pop up over it, sometimes lean out round the end, still kneeling
    void StartPeek(Vector3 threat, bool tall)
    {
        peeking = true; peeksLeft--; phaseT = (1.1f + R * 1.3f) * tempo; peekSide = 0; settle = 0;
        if (!tall && R > 0.4f) return;
        int s = R < 0.5f ? -1 : 1; float reach = tall ? 1.2f : 0.9f;
        for (int k = 0; k < 2; k++, s = -s)
        {
            if (!NavMesh.SamplePosition(cover.pos + cover.side * reach * s, out var h, 0.4f, NavMesh.AllAreas) || NavMesh.Raycast(cover.pos, h.position, out _, NavMesh.AllAreas)) continue;
            if (FirstHit(h.position + Vector3.up * (tall ? 1.55f : 1.05f), threat + Vector3.up * 1.3f, out _, true)) continue;      // no view from there
            peekPos = h.position; peekSide = s; return;
        }
        if (tall) { peekPos = cover.pos + cover.side * reach * flankSide; peekSide = flankSide; }      // neither side is clear: try his usual one
    }

    // like the web game: pick ONE movement set (crouch-walk / walk / run / sprint) and play it at exactly the speed
    // he moves, so the feet stay planted; the 8 directions blend inside the set. The set follows what he MEANS to
    // do, not the speed of the moment, so getting going and pulling up never flicker walk-run-walk; below half a
    // clip's pace the blend slides towards the idle (shorter steps) instead of the feet skating.
    void Animate(float dt, bool wantCrouch)
    {
        if (!anim) return;
        var v = agent.velocity; v.y = 0; float sp = v.magnitude;
        raiseW = Mathf.Lerp(raiseW, raised && !sprinting ? 1 : 0, 1 - Mathf.Exp(-7 * dt));
        anim.SetFloat(RaiseP, raiseW);
        anim.SetBool(CrouchP, wantCrouch);
        float want = Mathf.Max(curSpeed, sp);
        float clip = wantCrouch ? PlayerController.CrouchSpeed
                   : sprinting && sp > 4.2f ? PlayerController.SprintSpeed
                   : want > 3f ? PlayerController.RunSpeed : PlayerController.WalkSpeed;
        var local = transform.InverseTransformDirection(v);
        var dir2 = new Vector2(local.x, local.z); if (sp > 1e-3f) dir2 /= sp;
        var feed = dir2 * (clip * Mathf.Clamp01(sp / (clip * 0.5f)));
        anim.SetFloat(VelX, feed.x, 0.14f, dt);
        anim.SetFloat(VelZ, feed.y, 0.14f, dt);
        anim.SetFloat(AnimSpeed, sp > 0.2f ? Mathf.Clamp(sp / clip, 0.5f, 1.35f) : 1f, 0.1f, dt);
    }

    void Footsteps(float dt)
    {
        float sp = agent.velocity.magnitude; if (sp < 0.5f) return;
        stepDist += sp * dt;
        float stride = sp > 3.5f ? 1.5f : sp > 2f ? 1.1f : 0.75f;
        if (stepDist < stride) return;
        stepDist = 0;
        CombatAudio.Footstep(transform.position, sp, Crouched);
    }

    // fire discipline: react, bring the rifle on, let the sights settle, a short burst (single aimed shots at
    // range, longer bursts close in), then a pause to find you again before the next
    void Shooting(float dt, bool canShoot)
    {
        shootT -= dt;
        bool moving = agent.velocity.sqrMagnitude > 0.25f;
        bool onTarget = canShoot && hasAim && aimErr < (moving ? 20f : 9f);
        settle = onTarget ? Mathf.Min(moving ? 0.6f : 1f, settle + dt / SettleTime) : Mathf.Max(0, settle - dt * 3f);
        if (!canShoot) burst = 0;                                                          // lost the picture: the next burst starts afresh
        if (!onTarget || ammo <= 0 || reloadT > 0 || shootT > 0 || !player) return;
        if (Time.time - firstSeen < react) return;                                        // reaction time
        if (burst <= 0)
        {
            if (settle < 0.45f) return;                                                    // sights not on yet
            burst = distToPlayer > 38f ? 1 + Random.Range(0, 2) : distToPlayer > 20f ? 2 + Random.Range(0, 2) : 3 + Random.Range(0, 3);
            burstShot = 0;
        }
        burst--; burstShot++;
        if (burst > 0) shootT = 0.1f;
        else
        {
            // re-acquire: quicker close in, slower at range and when he is being shot at
            shootT = (0.55f + R * 0.9f) * tempo * Mathf.Lerp(0.65f, 1.3f, Mathf.InverseLerp(10f, 40f, distToPlayer)) * (1f + supp * 0.8f);
            settle *= 0.5f;
        }
        if (--ammo <= 0) { reloadT = ReloadTime; burst = 0; }
        Fire();
    }

    // chance that a round fired now hits you
    float HitChance()
    {
        float c = Mathf.Lerp(AccNear, AccFar, Mathf.InverseLerp(10f, 60f, distToPlayer));
        c *= Mathf.Lerp(0.55f, 1f, settle);                                                 // snap shot vs. aimed shot
        c *= 1f - 0.1f * Mathf.Min(burstShot - 1, 4);                                       // the muzzle climbs through the burst
        if (agent.velocity.sqrMagnitude > 0.25f) c *= 0.45f; else if (crouchT > 0.5f) c *= 1.15f;   // on the move / steady on a knee
        c *= 1f - 0.6f * supp;                                                              // your rounds are cracking past him
        if (Time.time < hurtUntil) c *= 0.7f;                                               // he has just been hit
        if (pc)
        {
            if (pc.Speed > 0.5f) c *= pc.Sprinting ? 0.5f : 0.7f;
            var pcc = player.GetComponent<CharacterController>();
            if (pcc && !pcc.isGrounded) c *= 0.6f;
            if (pc.crouch) c *= 0.65f;
        }
        return c;
    }

    void Fire()
    {
        ShotsFired++;
        var from = muzzle ? muzzle.position : Eye;
        // he only hits what he can see; without a view he is putting rounds into the place he last saw you
        var target = sees ? PlayerTarget : aimPoint;
        bool hit = sees && R < HitChance();
        float wide = (sees ? 1f : 1.5f) * Mathf.Lerp(0.6f, 1.4f, distToPlayer / 50f);        // misses spread with range
        var end = hit ? target : target + new Vector3((R - 0.5f) * 2.5f, (R - 0.3f) * 1.5f, (R - 0.5f) * 2.5f) * wide;
        var dir = (end - from).normalized;
        // the round flies on until it meets something
        var stop = from + dir * 200f;
        if (FirstHit(from, from + dir * 200f, out var h, false))
        {
            stop = h.point;
            // a round that ends in a car holes the panel or breaks the pane, exactly as the player's does
            if (!hit && !(player && h.collider.transform.IsChildOf(player)) && CarDamage.Hit(h, dir, false) != CarDamage.Glass) CombatFx.Impact(h, dir);
        }
        CombatFx.Tracer(from, stop, false);
        CombatFx.Muzzle(from, dir, false, false);
        if (flash) { flash.gameObject.SetActive(true); flashOff = Time.time + 0.045f; }
        recoil = 1f; hitVP -= 0.6f;
        CombatAudio.Shot(0, from, false, !sees);                    // delayed by distance, a dull thump from far off or behind a wall
        if (hit && ph) { ph.Damage(6f + R * 4f, transform.position); if (!ph.invulnerable && !ph.Dead) CombatFx.PlayerHit(transform.position); }
        else if (player) CombatAudio.BulletPass(from, stop);        // a near miss cracks past on the side it went
    }

    // ---------- layered on top of the motion capture (as in the web game) ----------
    // the spine bends to look up / down at you (more when the rifle is up), flinches when hit, kicks with
    // each shot; the rifle stays in the right hand as the clip holds it; the left hand is put on the handguard
    void LateUpdate()
    {
        if (dead || !rifle) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        hitVP += (-140f * hitP - 13f * hitVP) * dt; hitP += hitVP * dt;
        hitVR += (-140f * hitR - 13f * hitVR) * dt; hitR += hitVR * dt;
        recoil = Mathf.Max(0, recoil - dt * 10f);
        // aim pitch (radians, up = positive) and torso twist (degrees): both eased, so the rifle swings on to you
        // instead of jumping, and the shoulders turn to you before the feet catch up
        float wantPitch = 0, wantTwist = 0;
        if (hasAim)
        {
            var d = aimPoint - Eye; float hd = new Vector2(d.x, d.z).magnitude;
            wantPitch = Mathf.Atan2(d.y, Mathf.Max(hd, 0.1f)) * (0.35f + 0.65f * raiseW);
            float off = Mathf.DeltaAngle(transform.eulerAngles.y, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            if (!sprinting && raiseW > 0.3f && Mathf.Abs(off) < 110f) wantTwist = Mathf.Clamp(off, -TwistMax, TwistMax) * raiseW;   // behind him: don't wring the spine
        }
        float ease = 1 - Mathf.Exp(-11 * dt);
        aimPitch = Mathf.Lerp(aimPitch, wantPitch, ease); twist = Mathf.Lerp(twist, wantTwist, ease);
        if (spine != null && spine.Length > 0)
        {
            float down = (-aimPitch + hitP * 0.5f - recoil * 0.04f) / spine.Length * Mathf.Rad2Deg;
            float roll = (-hitR * 0.5f) / spine.Length * Mathf.Rad2Deg, turn = twist / spine.Length;
            foreach (var b in spine)
            {
                b.rotation = Quaternion.AngleAxis(turn, Vector3.up) * b.rotation;
                b.rotation = Quaternion.AngleAxis(down, transform.right) * b.rotation;
                b.rotation = Quaternion.AngleAxis(roll, transform.forward) * b.rotation;
            }
        }
        // rifle: the calibrated hold, tilted while reloading, pushed back by recoil
        rifle.localPosition = rifleLocalPos; rifle.localRotation = rifleLocalRot;
        float rl = reloadT > 0 ? 1f - reloadT / ReloadTime : -1f;
        float tilt = rl >= 0 ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0f, 0.15f, rl)) * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.85f, 1f, rl))) : 0;
        if (tilt > 0) rifle.rotation = Quaternion.AngleAxis(34f * tilt, rifle.forward) * Quaternion.AngleAxis(-14f * tilt, rifle.right) * rifle.rotation;
        if (recoil > 0) rifle.position -= rifle.forward * 0.05f * recoil;
        // left hand: on the handguard, down to the vest pouch mid-reload; free while sprinting (it lets go and
        // takes hold again over a fifth of a second, it doesn't teleport)
        ikW = Mathf.MoveTowards(ikW, sprinting ? 0 : 1, dt * 5f);
        if (gripL && armL && foreL && handL && ikW > 0.01f)
        {
            var target = Vector3.Lerp(handL.position, gripL.position, Mathf.SmoothStep(0, 1, ikW));
            if (rl >= 0 && spine != null && spine.Length > 0)
            {
                var pouch = spine[spine.Length - 1].position - transform.right * 0.1f - transform.up * 0.15f + transform.forward * 0.15f;
                float away = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.2f, 0.4f, rl)) * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.7f, 0.9f, rl)));
                target = Vector3.Lerp(target, pouch, away);
            }
            TwoBoneIK(armL, foreL, handL, target, -transform.right * 0.6f - Vector3.up - transform.forward * 0.2f);
        }
        if (flash && flash.gameObject.activeSelf && Camera.main) flash.rotation = Quaternion.LookRotation(flash.position - Camera.main.transform.position) * Quaternion.Euler(0, 0, R * 360f);
    }

    static void TwoBoneIK(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole)
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

    // ---------- getting hit / dying ----------
    public override void Hit(float dmg, Vector3 point, Vector3 dir)
    {
        if (dead) return;
        bool head = point.y > transform.position.y + (Crouched ? 0.95f : 1.5f);
        health -= dmg * (head ? 2.5f : 1f);
        onHit?.Invoke(this);
        CombatFx.Impact(Surface.Flesh, point, -dir, dir, dmg > 100f);     // red mist and the thud of the hit
        HitFeedback.EnemyHit(point, dir, head, health <= 0);              // his voice, blood on what is behind him
        if (health <= 0) { Die(dir, head); return; }
        float now = Time.time;
        staggerUntil = now + 0.35f; flinchUntil = now + 0.55f; hurtUntil = now + 4f; supp = Mathf.Min(1, supp + 0.5f);
        var ld = transform.InverseTransformDirection(new Vector3(dir.x, 0, dir.z).normalized);           // flinch away from the shot
        hitVP += ld.z * 7f; hitVR += ld.x * 7f;
        if (player) { alerted = true; if (!sees) Known(player.position); if (seenT > 3f) { firstSeen = now; react = 0.35f; } }
        if (peeking) { peeking = false; phaseT = (1.2f + R * 1.5f) * tempo; }                         // hit while up: straight back down
        else if (AtCover) { avoidPos = cover.pos; hasCover = false; coverRetryAt = 0; }               // hit while hiding: this cover doesn't work
        else { coverCheckT = 0; coverRetryAt = 0; contactUntil = 0; }                                 // caught in the open: find cover now
    }

    void Die(Vector3 dir, bool head)
    {
        dead = true; All.Remove(this);
        if (agent) agent.enabled = false;
        cc.enabled = false;
        CombatAudio.BodyFall(transform.position);
        HitFeedback.EnemyDied(transform.position, dir, head);
        if (flash) flash.gameObject.SetActive(false);
        if (rifle) { rifle.localPosition = rifleLocalPos; rifle.localRotation = rifleLocalRot; }
        if (anim) { anim.SetFloat(VelX, 0); anim.SetFloat(VelZ, 0); anim.CrossFade(DeathClip(dir, head), 0.12f); }
        if (ph) ph.AddKill(head);
        Destroy(gameObject, 14f);
    }

    string DeathClip(Vector3 dir, bool head)
    {
        if (Crouched) return "death crouching headshot front";
        var ld = transform.InverseTransformDirection(new Vector3(dir.x, 0, dir.z));
        if (Mathf.Abs(ld.x) > Mathf.Abs(ld.z)) return "death from right";
        if (ld.z > 0) return head ? "death from back headshot" : "death from the back";      // shot from behind
        return head ? "death from front headshot" : "death from the front";
    }

    // ---------- generated sounds and effects ----------
    static Texture2D FlashTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            var d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f) / (n / 2f); float r = d.magnitude, a = Mathf.Atan2(d.y, d.x);
            float star = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 2.5f)), 12) * Mathf.Clamp01(1 - r);
            float al = Mathf.Clamp01(Mathf.Clamp01(1 - r * 1.8f) + star);
            t.SetPixel(x, y, new Color(1f, Mathf.Lerp(0.55f, 0.95f, Mathf.Clamp01(1 - r * 2)), Mathf.Lerp(0.2f, 0.7f, Mathf.Clamp01(1 - r * 2.5f)), al));
        }
        t.Apply(); return t;
    }
}

// Hand grenade thrown by an enemy: lobbed at where he last saw you, bounces, goes off after 2.4-3 s.
// Hurts you if you are within 7 m and nothing solid is between you; shakes the camera from further away.
public class EnemyGrenade : MonoBehaviour
{
    public const float Radius = 7f;
    static readonly List<EnemyGrenade> live = new();
    float fuse, lastBounce;

    public static Transform Near(Vector3 p, float r)
    {
        foreach (var g in live) if (g && Vector3.Distance(g.transform.position, p) < r) return g.transform;
        return null;
    }

    public static void Throw(Vector3 from, Vector3 to, Collider thrower)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "Grenade"; go.layer = 2;
        go.transform.position = from; go.transform.localScale = Vector3.one * 0.12f;
        var mr = go.GetComponent<MeshRenderer>(); mr.material = new Material(Shader.Find("Universal Render Pipeline/Lit")); mr.material.SetColor("_BaseColor", new Color(0.24f, 0.27f, 0.17f));
        if (thrower) Physics.IgnoreCollision(go.GetComponent<Collider>(), thrower);
        var rb = go.AddComponent<Rigidbody>(); rb.mass = 0.4f; rb.linearDamping = 0.05f; rb.angularDamping = 0.5f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        float d = Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(to.x, 0, to.z));
        float t = Mathf.Clamp(d / 11f, 0.7f, 1.7f);
        var target = to + Vector3.up * 0.1f;
        rb.linearVelocity = (target - from) / t - 0.5f * Physics.gravity * t;
        rb.angularVelocity = Random.insideUnitSphere * 10f;
        var g = go.AddComponent<EnemyGrenade>(); g.fuse = 2.4f + Random.value * 0.6f;
        live.Add(g);
    }

    void Update()
    {
        fuse -= Time.deltaTime;
        if (fuse <= 0) Explode();
    }

    // the clink of it landing and rolling: the warning you get
    void OnCollisionEnter(Collision c)
    {
        if (Time.time - lastBounce < 0.12f || c.relativeVelocity.magnitude < 0.8f) return;
        lastBounce = Time.time;
        CombatAudio.GrenadeBounce(transform.position, c.relativeVelocity.magnitude / 6f);
    }

    void Explode()
    {
        live.Remove(this);
        var p = transform.position;
        CombatAudio.Explosion(p);       // crack, boom and debris; rings in your ears if it is close
        CombatFx.Explosion(p);          // flash, fireball, smoke, earth, dust ring, and the camera shake by distance
        var pc = FindFirstObjectByType<PlayerController>();
        if (pc)
        {
            var chest = pc.transform.position + Vector3.up * 1.1f; float d = Vector3.Distance(p, chest);
            bool shielded = false;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.3f, (chest - p).normalized, d, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(pc.transform) && !h.collider.GetComponentInParent<EnemyAI>()) { shielded = true; break; }
            var hp = pc.GetComponent<PlayerHealth>();
            if (d < Radius && !shielded && hp) hp.Damage(95f * Mathf.Pow(1 - d / Radius, 1.3f), p);
        }
        Destroy(gameObject);
    }
}
