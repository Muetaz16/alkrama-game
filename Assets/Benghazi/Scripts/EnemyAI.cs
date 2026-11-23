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
//   lost you     after 7 s goes hunting where you were last seen
// Rifle and upper body aim at you; recoil, muzzle flash, tracers, impacts, footsteps, bullets whizzing past.
public class EnemyAI : Damageable
{
    public static readonly List<EnemyAI> All = new();
    public static int ShotsFired, GrenadesThrown;
    const float Range = 60f, Hearing = 45f;
    const int Mag = 30;
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
    AudioSource gunAudio, stepAudio; LineRenderer tracer;
    bool alerted, sees, dead, flanker, flankDone, flankSet, hasCover, peeking, raised;
    int flankSide, ammo = Mag, burst, grenades = 1, strafeDir = 1;
    float seenT = 99, firstSeen = -99, shootT, reloadT, coverCheckT, phaseT, exposedT, strafeT, stuckT, wanderT, nadeCool,
          crouchT, aimW, recoil, flashOff, tracerOff, staggerUntil, nextLook, distToPlayer = 999, stepDist, lookSeed;
    Vector3? lastSeen; Vector3 wander, home, flankPos, goalSet = new Vector3(1e6f, 0, 1e6f);
    CoverSpot cover;
    static int spawnCount;
    static AudioClip shotClip, stepClip, whizClip;
    static Texture2D flashTex;
    static ParticleSystem dust;
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
        nadeCool = 6 + R * 6; shootT = 0.4f + R * 0.8f;
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
        agent.acceleration = 24f; agent.angularSpeed = 0; agent.updateRotation = false; agent.autoBraking = true;
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
        if (!shotClip) shotClip = WeaponController.Gunshot(0.3f, 800f, 5);
        if (!stepClip) stepClip = Step();
        if (!whizClip) whizClip = Whiz();
        if (!flashTex) flashTex = FlashTexture(64);
        if (!dust) dust = Dust();
        gunAudio = gameObject.AddComponent<AudioSource>(); gunAudio.spatialBlend = 1; gunAudio.minDistance = 8; gunAudio.maxDistance = 240; gunAudio.rolloffMode = AudioRolloffMode.Linear; gunAudio.volume = 0.75f;
        stepAudio = gameObject.AddComponent<AudioSource>(); stepAudio.spatialBlend = 1; stepAudio.minDistance = 2; stepAudio.maxDistance = 28; stepAudio.rolloffMode = AudioRolloffMode.Linear; stepAudio.volume = 0.6f;
        tracer = new GameObject("EnemyTracer").AddComponent<LineRenderer>(); tracer.transform.SetParent(transform, false);
        tracer.useWorldSpace = true; tracer.material = new Material(Shader.Find("Sprites/Default"));
        tracer.startColor = new Color(1f, 0.6f, 0.35f, 0.95f); tracer.endColor = new Color(1f, 0.75f, 0.45f, 0.15f);
        tracer.startWidth = 0.025f; tracer.endWidth = 0.012f; tracer.positionCount = 2; tracer.enabled = false;
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
        foreach (var h in Physics.RaycastAll(a, d / len, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
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
        if (sees)
        {
            if (!was && seenT > 3f) firstSeen = Time.time;              // takes a moment to react
            alerted = true; lastSeen = player.position;
            foreach (var o in All)                                       // shouts it to his mates
                if (o != this && !o.dead && Flat(o.transform.position, transform.position) < 35f) o.Alert(player.position);
        }
        else if (Time.time - WeaponController.LastShotTime < 0.3f && distToPlayer < Hearing) { alerted = true; lastSeen = player.position; }
    }

    // told where the player is: he joins the fight
    public void Alert(Vector3 at)
    {
        if (dead) return;
        alerted = true; if (!sees) lastSeen = at;
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
            foreach (var o in All) if (o != this && o.hasCover && Flat(o.cover.pos, p) < 1.6f) return;
            // crouched he must be hidden: three rays at 0.95 m (left, middle, right) all stopped close to him
            var perp = Vector3.Cross(Vector3.up, (tChest - p).normalized) * 0.3f;
            foreach (var off in new[] { -perp, Vector3.zero, perp })
                if (!FirstHit(p + Vector3.up * 0.95f + off, tChest + off, out var hit, true) || hit.distance > 2.2f) return;
            float score = Flat(transform.position, p) + Mathf.Abs(dP - 18f) * 0.35f;
            if (score < bestScore) { bestScore = score; bestSpot = new CoverSpot { pos = p, top = top, side = side }; found = true; }
        }
        // objects: cars, barriers, sandbags, drums - anything 0.8-3 m tall and up to 7 m long standing on the street
        foreach (var c in Physics.OverlapSphere(transform.position, 22f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
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
        float dt = Time.deltaTime;
        if (reloadT > 0) { reloadT -= dt; if (reloadT <= 0) ammo = Mag; }
        if (Time.time >= nextLook) { nextLook = Time.time + 0.15f; Look(); }
        if (sees) seenT = 0; else seenT += dt;

        Vector3? goal = null, face = null; float speed = 0; bool wantCrouch = false, canShoot = false;
        var nade = EnemyGrenade.Near(transform.position, EnemyGrenade.Radius + 1.5f);
        if (nade)
        {
            // grenade! run away from it
            var away = transform.position - nade.position; away.y = 0;
            goal = transform.position + away.normalized * 8f; speed = 5.8f; hasCover = false; Mode = State.Evade;
        }
        else if (!alerted)
        {
            Mode = State.Patrol;
            wanderT -= dt;
            if (wanderT <= 0)
            {
                wanderT = 3 + R * 4;
                var r = Random.insideUnitCircle * 7f; wander = home + new Vector3(r.x, 0, r.y);
            }
            if (Flat(transform.position, wander) > 0.6f) { goal = wander; speed = 1.3f; }
            else face = transform.position + Quaternion.Euler(0, Mathf.Sin(Time.time * 0.7f + lookSeed) * 80f, 0) * transform.forward;
        }
        else if (!sees && seenT > 7f && lastSeen.HasValue)
        {
            // lost you for a while: go hunting where you were last seen
            Mode = State.Hunt; goal = lastSeen; speed = 3.4f; hasCover = false;
            if (Flat(transform.position, lastSeen.Value) < 1.8f) { lastSeen = null; alerted = seenT < 20f; home = wander = transform.position; }
        }
        else if (flanker && !flankDone && lastSeen.HasValue)
        {
            // swing wide round your side before engaging
            Mode = State.Flank;
            if (!flankSet)
            {
                var toP = lastSeen.Value - transform.position; toP.y = 0; toP.Normalize();
                flankPos = lastSeen.Value + new Vector3(-toP.z, 0, toP.x) * 14f * flankSide - toP * 4f; flankSet = true;
            }
            goal = flankPos; speed = 4.6f; canShoot = sees && distToPlayer < 25f; face = sees ? player.position : (Vector3?)null;
            if (Flat(transform.position, flankPos) < 2f || stuckT > 1.5f) flankDone = true;
        }
        else
        {
            coverCheckT -= dt;
            var threat = lastSeen ?? (player ? player.position : transform.position);
            if (!hasCover || coverCheckT <= 0 || Flat(cover.pos, threat) < 6f)
            {
                hasCover = PickCover(threat, out cover);
                coverCheckT = 7 + R * 5; peeking = false; phaseT = 0.8f + R;
                if (hasCover) CoverMoves++;
            }
            if (hasCover)
            {
                Mode = State.Cover;
                bool tall = cover.top > 1.3f;
                if (Flat(transform.position, cover.pos) > 0.7f && !(peeking && tall))
                {
                    // run to cover, snap-shooting if you're in view
                    goal = cover.pos; speed = 4.6f; canShoot = sees && distToPlayer < 30f; face = sees ? player.position : (Vector3?)null;
                }
                else
                {
                    phaseT -= dt;
                    face = threat;
                    if (!peeking)
                    {
                        wantCrouch = true;
                        if (ammo < 12 && reloadT <= 0) reloadT = 2.4f;
                        if (phaseT <= 0 && reloadT <= 0) { peeking = true; phaseT = 1.3f + R * 1.4f; }
                        // spotted while hiding? this cover is no good, find another
                        if (sees) { exposedT += dt; if (exposedT > 0.9f) { hasCover = false; exposedT = 0; } } else exposedT = 0;
                    }
                    else
                    {
                        // peek: stand up over low cover, or step out past a car / wall
                        if (tall) { goal = cover.pos + cover.side * 1.2f * flankSide; speed = 2f; }
                        canShoot = sees;
                        if (phaseT <= 0 || (!sees && phaseT < 0.7f)) { peeking = false; phaseT = 1.4f + R * 2f; goal = cover.pos; speed = 2.5f; }
                    }
                }
            }
            else
            {
                // no cover around: strafe sideways while shooting, keep a fighting distance
                Mode = State.Open;
                strafeT -= dt;
                if (strafeT <= 0) { strafeDir *= -1; strafeT = 1.2f + R * 1.5f; }
                var toP = threat - transform.position; toP.y = 0; float dP = toP.magnitude; toP /= Mathf.Max(dP, 0.01f);
                var move = new Vector3(-toP.z, 0, toP.x) * strafeDir + toP * (dP > 24f ? 1f : dP < 10f ? -1f : 0f);
                goal = transform.position + move.normalized * 2.5f; speed = 2.6f; canShoot = sees; face = threat;
            }
            // you're hiding: flush you out with a grenade
            nadeCool -= dt;
            if (grenades > 0 && nadeCool <= 0 && !sees && seenT > 2.5f && seenT < 12f && lastSeen.HasValue)
            {
                float d = Flat(transform.position, lastSeen.Value);
                if (d > 7f && d < 26f) { EnemyGrenade.Throw(Eye + transform.forward * 0.4f + Vector3.up * 0.15f, lastSeen.Value, cc); grenades--; nadeCool = 14f; GrenadesThrown++; }
            }
        }

        // stance
        crouchT = Mathf.MoveTowards(crouchT, wantCrouch ? 1 : 0, dt * 4f);

        // movement on the navigation map
        if (agent.isOnNavMesh)
        {
            agent.isStopped = Time.time < staggerUntil;
            if (goal.HasValue)
            {
                agent.speed = speed * (wantCrouch ? 0.5f : 1f);
                if ((goal.Value - goalSet).sqrMagnitude > 0.6f)
                {
                    goalSet = goal.Value;
                    if (NavMesh.SamplePosition(goal.Value, out var h, 2.5f, NavMesh.AllAreas)) agent.SetDestination(h.position);
                    else stuckT = 2f;
                }
                // stuck against something? give up on that goal
                bool stalled = !agent.pathPending && agent.remainingDistance > 1f && agent.velocity.magnitude < speed * 0.25f;
                stuckT = stalled ? stuckT + dt : Mathf.Max(0, stuckT - dt);
                if (stuckT > 1.2f) { hasCover = false; strafeDir *= -1; wanderT = 0; if (flankSet) flankDone = true; stuckT = 0; goalSet = new Vector3(1e6f, 0, 1e6f); }
            }
            else if (agent.hasPath) { agent.ResetPath(); goalSet = new Vector3(1e6f, 0, 1e6f); }
        }

        // facing: you when he's fighting, otherwise where he's going
        Vector3 dir = face.HasValue ? face.Value - transform.position : agent.desiredVelocity.sqrMagnitude > 0.1f ? agent.desiredVelocity : transform.forward;
        dir.y = 0;
        if (dir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir), 900f * dt);

        raised = alerted && (canShoot || peeking || sees);
        curSpeed = speed; sprinting = speed > 5f && goal.HasValue; wantCrouchNow = wantCrouch;
        Animate(dt, wantCrouch);
        Footsteps(dt);
        Shooting(dt, canShoot);
        if (Time.time > tracerOff) tracer.enabled = false;
        if (flash && Time.time > flashOff && flash.gameObject.activeSelf) flash.gameObject.SetActive(false);
    }

    // like the web game: pick ONE movement set (crouch-walk / walk / run / sprint) from what he is doing and play
    // it at exactly the speed he moves, so the feet stay planted; the 8 directions blend inside the set
    void Animate(float dt, bool wantCrouch)
    {
        if (!anim) return;
        var v = agent.velocity; v.y = 0; float sp = v.magnitude;
        raiseW = Mathf.Lerp(raiseW, raised && !sprinting ? 1 : 0, 1 - Mathf.Exp(-9 * dt));
        anim.SetFloat(RaiseP, raiseW);
        anim.SetBool(CrouchP, wantCrouch);
        bool moving = sp > 0.25f;
        float clip = wantCrouch ? PlayerController.CrouchSpeed
                   : sprinting ? PlayerController.SprintSpeed
                   : (raised && sp < 3.2f) || sp < 3f ? PlayerController.WalkSpeed : PlayerController.RunSpeed;
        var local = transform.InverseTransformDirection(v);
        var dir2 = new Vector2(local.x, local.z); if (dir2.sqrMagnitude > 1e-4f) dir2.Normalize();
        var feed = moving ? dir2 * clip : Vector2.zero;
        anim.SetFloat(VelX, feed.x, 0.12f, dt);
        anim.SetFloat(VelZ, feed.y, 0.12f, dt);
        anim.SetFloat(AnimSpeed, moving ? Mathf.Clamp(sp / clip, 0.5f, 1.6f) : 1f);
    }

    void Footsteps(float dt)
    {
        float sp = agent.velocity.magnitude; if (sp < 0.5f) return;
        stepDist += sp * dt;
        float stride = sp > 3.5f ? 1.5f : sp > 2f ? 1.1f : 0.75f;
        if (stepDist < stride) return;
        stepDist = 0;
        stepAudio.pitch = Random.Range(0.85f, 1.15f);
        stepAudio.PlayOneShot(stepClip, (sp > 3.5f ? 1f : 0.6f) * (Crouched ? 0.5f : 1f));
    }

    // rifles fire short bursts from a 30-round magazine
    void Shooting(float dt, bool canShoot)
    {
        shootT -= dt;
        if (!canShoot || ammo <= 0 || reloadT > 0 || shootT > 0 || !player) return;
        if (Time.time - firstSeen < 0.6f) return;                                       // reaction time
        var toP = player.position - transform.position; toP.y = 0;
        if (Vector3.Angle(transform.forward, toP) > 25f) return;                          // not facing you yet
        if (--burst > 0) shootT = 0.12f; else { burst = 3 + Random.Range(0, 3); shootT = 0.7f + R * 0.8f; }
        if (--ammo <= 0) reloadT = 2.4f;
        Fire();
    }

    void Fire()
    {
        ShotsFired++;
        var from = muzzle ? muzzle.position : Eye;
        var target = PlayerTarget;
        float chance = Mathf.Clamp(0.3f - distToPlayer * 0.007f, 0.06f, 0.28f);
        if (agent.velocity.magnitude > 0.5f) chance *= 0.6f;
        if (pc)
        {
            if (pc.Speed > 0.5f) chance *= pc.Sprinting ? 0.5f : 0.7f;
            var pcc = player.GetComponent<CharacterController>();
            if (pcc && !pcc.isGrounded) chance *= 0.6f;
            if (pc.crouch) chance *= 0.65f;
        }
        bool hit = R < chance;
        var end = hit ? target : target + new Vector3((R - 0.5f) * 2.5f, (R - 0.3f) * 1.5f, (R - 0.5f) * 2.5f);
        var dir = (end - from).normalized;
        // the round flies on until it meets something
        var stop = from + dir * 200f;
        if (FirstHit(from, from + dir * 200f, out var h, false))
        {
            stop = h.point;
            if (!hit && !(player && h.collider.transform.IsChildOf(player)) && dust)
            { dust.transform.SetPositionAndRotation(h.point + h.normal * 0.05f, Quaternion.LookRotation(h.normal)); dust.Emit(8); }
        }
        tracer.SetPosition(0, from); tracer.SetPosition(1, stop); tracer.enabled = true; tracerOff = Time.time + 0.05f;
        if (flash) { flash.gameObject.SetActive(true); flashOff = Time.time + 0.045f; }
        recoil = 1f; hitVP -= 0.6f;
        gunAudio.pitch = Random.Range(0.92f, 1.05f); gunAudio.PlayOneShot(shotClip);
        if (hit && ph) ph.Damage(6f + R * 4f, transform.position);
        else if (player && distToPlayer < 40f && R < 0.35f)
        {
            // crack of a near miss, on the side it went past
            var cam = Camera.main;
            if (cam) AudioSource.PlayClipAtPoint(whizClip, cam.transform.position + (end - target).normalized * 1.5f, 0.55f);
        }
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
        // aim pitch (radians, up = positive)
        Vector3 tgt = player && (raised || lastSeen.HasValue) ? PlayerTarget : Eye + transform.forward * 10f;
        var d = tgt - Eye; float hd = new Vector2(d.x, d.z).magnitude;
        float pitch = Mathf.Atan2(d.y, Mathf.Max(hd, 0.1f)) * (0.35f + 0.65f * raiseW);
        if (spine != null && spine.Length > 0)
        {
            float down = (-pitch + hitP * 0.5f - recoil * 0.04f) / spine.Length * Mathf.Rad2Deg;
            float roll = (-hitR * 0.5f) / spine.Length * Mathf.Rad2Deg;
            foreach (var b in spine)
            {
                b.rotation = Quaternion.AngleAxis(down, transform.right) * b.rotation;
                b.rotation = Quaternion.AngleAxis(roll, transform.forward) * b.rotation;
            }
        }
        // rifle: the calibrated hold, tilted while reloading, pushed back by recoil
        rifle.localPosition = rifleLocalPos; rifle.localRotation = rifleLocalRot;
        float rl = reloadT > 0 ? 1f - reloadT / 2.4f : -1f;
        float tilt = rl >= 0 ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0f, 0.15f, rl)) * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.85f, 1f, rl))) : 0;
        if (tilt > 0) rifle.rotation = Quaternion.AngleAxis(34f * tilt, rifle.forward) * Quaternion.AngleAxis(-14f * tilt, rifle.right) * rifle.rotation;
        if (recoil > 0) rifle.position -= rifle.forward * 0.05f * recoil;
        // left hand: on the handguard, down to the vest pouch mid-reload; free while sprinting
        if (gripL && armL && foreL && handL && !sprinting)
        {
            var target = gripL.position;
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
        if (health <= 0) { Die(dir, head); return; }
        staggerUntil = Time.time + 0.25f;
        var ld = transform.InverseTransformDirection(new Vector3(dir.x, 0, dir.z).normalized);           // flinch away from the shot
        hitVP += ld.z * 7f; hitVR += ld.x * 7f;
        if (player) { alerted = true; lastSeen = player.position; if (seenT > 3f) firstSeen = Time.time - 0.3f; }
        if (!hasCover || Flat(transform.position, cover.pos) > 1f) coverCheckT = 0;        // caught in the open: find cover now
    }

    void Die(Vector3 dir, bool head)
    {
        dead = true; All.Remove(this);
        if (agent) agent.enabled = false;
        cc.enabled = false; tracer.enabled = false;
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
    static AudioClip Step()
    {
        int rate = 44100, n = (int)(rate * 0.09f); var d = new float[n]; var rnd = new System.Random(31); float lp = 0;
        for (int i = 0; i < n; i++) { float t = i / (float)rate; float no = (float)(rnd.NextDouble() * 2 - 1); lp += (no - lp) * 0.08f; d[i] = (lp * 3f + no * 0.25f * Mathf.Exp(-t * 120)) * Mathf.Exp(-t * 45); }
        var c = AudioClip.Create("step", n, 1, rate, false); c.SetData(d, 0); return c;
    }
    static AudioClip Whiz()
    {
        int rate = 44100, n = (int)(rate * 0.22f); var d = new float[n]; var rnd = new System.Random(33); float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n; float no = (float)(rnd.NextDouble() * 2 - 1); lp += (no - lp) * Mathf.Lerp(0.6f, 0.1f, u);
            d[i] = (no - lp) * Mathf.Sin(u * Mathf.PI) * Mathf.Exp(-u * 3f) * 1.4f + (i < 300 ? no * 0.6f : 0);
        }
        var c = AudioClip.Create("whiz", n, 1, rate, false); c.SetData(d, 0); return c;
    }
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
    static ParticleSystem Dust()
    {
        var go = new GameObject("EnemyImpactDust"); var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = false; main.playOnAwake = false; main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3.5f); main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.28f);
        main.startColor = new Color(0.62f, 0.56f, 0.46f, 0.8f); main.gravityModifier = 0.6f; main.maxParticles = 300; main.simulationSpace = ParticleSystemSimulationSpace.World;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 35; sh.radius = 0.02f;
        go.GetComponent<ParticleSystemRenderer>().material = new Material(Shader.Find("Sprites/Default"));
        return ps;
    }
}

// Hand grenade thrown by an enemy: lobbed at where he last saw you, bounces, goes off after 2.4-3 s.
// Hurts you if you are within 7 m and nothing solid is between you; shakes the camera from further away.
public class EnemyGrenade : MonoBehaviour
{
    public const float Radius = 7f;
    static readonly List<EnemyGrenade> live = new();
    static AudioClip boom; static ParticleSystem smoke;
    float fuse;

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

    void Explode()
    {
        live.Remove(this);
        var p = transform.position;
        if (!boom) boom = WeaponController.Gunshot(1.5f, 150f, 9);
        AudioSource.PlayClipAtPoint(boom, p, 1f);
        if (!smoke) smoke = Smoke();
        smoke.transform.position = p; smoke.Emit(70);
        var lg = new GameObject("Blast").AddComponent<Light>(); lg.transform.position = p + Vector3.up * 0.5f; lg.type = LightType.Point; lg.range = 14; lg.intensity = 12; lg.color = new Color(1f, 0.65f, 0.3f);
        Destroy(lg.gameObject, 0.09f);
        var pc = FindFirstObjectByType<PlayerController>();
        if (pc)
        {
            var chest = pc.transform.position + Vector3.up * 1.1f; float d = Vector3.Distance(p, chest);
            bool shielded = false;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.3f, (chest - p).normalized, d, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(pc.transform) && !h.collider.GetComponentInParent<EnemyAI>()) { shielded = true; break; }
            var hp = pc.GetComponent<PlayerHealth>();
            if (d < Radius && !shielded && hp) hp.Damage(95f * Mathf.Pow(1 - d / Radius, 1.3f), p);
            var cam = FindFirstObjectByType<ThirdPersonCamera>();
            if (cam && d < 25f) cam.lookDelta += Random.insideUnitCircle * 4f * (1 - d / 25f);
        }
        Destroy(gameObject);
    }

    static ParticleSystem Smoke()
    {
        var go = new GameObject("GrenadeSmoke"); var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = false; main.playOnAwake = false; main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 3f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 9f); main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 2.2f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.35f, 0.32f, 0.28f, 0.85f), new Color(0.62f, 0.56f, 0.46f, 0.75f));
        main.gravityModifier = 0.15f; main.maxParticles = 400; main.simulationSpace = ParticleSystemSimulationSpace.World;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Hemisphere; sh.radius = 0.4f;
        var col = ps.colorOverLifetime; col.enabled = true;
        var gr = new Gradient(); gr.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0.9f, 0), new GradientAlphaKey(0, 1) });
        col.color = gr;
        var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.6f, 1, 1.8f));
        var lim = ps.limitVelocityOverLifetime; lim.enabled = true; lim.limit = 1.5f; lim.dampen = 0.15f;
        go.GetComponent<ParticleSystemRenderer>().material = new Material(Shader.Find("Sprites/Default"));
        return ps;
    }
}
