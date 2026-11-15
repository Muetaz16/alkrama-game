using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Enemy soldier, moving on the city's navigation map (baked by GameplaySetup), so he finds his way round
// buildings and never walks into the sea.
//   Patrol      walks between random points near where he appeared, stops and looks around
//   Investigate heard a shot / lost sight of you: runs to where you were, searches, gives up after a while
//   Cover       sees you: sprints to a spot where something solid is between him and you (car, wall,
//               barrier, sandbags), crouches; pops up to fire a burst or two, ducks again; moves to new
//               cover every 8-14 s or straight away if you flank him and the cover stops covering
//   Flank       now and then one of them circles round you to come at you from the side
//   Open        no cover nearby: side-steps while firing, keeps 9-28 m away
// Hit: flinches and breaks for cover. Walk / run / sprint / crouch animations follow his real speed.
// Aim gets worse with distance and when you move or crouch. 100 health, head shots x2.5.
public class EnemyAI : Damageable
{
    public static readonly List<EnemyAI> All = new();
    const float sight = 75f, hearing = 45f, damage = 6f;
    const float WalkSpd = 1.6f, RunSpd = 4.6f, SprintSpd = 6.2f;

    public enum State { Patrol, Investigate, Cover, Flank, Open }
    public State Mode { get; private set; } = State.Patrol;
    public bool Crouched { get; private set; }
    public int CoverMoves { get; private set; }

    NavMeshAgent agent; CharacterController cc; Animator anim; Transform player; PlayerHealth ph; PlayerController pc;
    Vector3 home, lastKnown, cover, faceDir;
    float nextLook, seenAt = -99, firstSeen = -99, nextBurst, nextShot, stateUntil, peekUntil, duckUntil, staggerUntil, pauseUntil, nextCoverCheck;
    int burstLeft; bool sees, dead, peeking, atCover; float distToPlayer = 999;
    Transform muzzle; LineRenderer tracer; float tracerOff; AudioSource audioSrc; static AudioClip shotClip;
    static readonly int VelX = Animator.StringToHash("VelX"), VelZ = Animator.StringToHash("VelZ"),
        CrouchP = Animator.StringToHash("Crouch"), AnimSpeed = Animator.StringToHash("AnimSpeed");
    static int flankers;
    float lookSeed;

    void Start()
    {
        health = 100; fallsOver = false; lookSeed = Random.Range(0f, 6.28f);
        cc = GetComponent<CharacterController>(); anim = GetComponentInChildren<Animator>();
        pc = FindFirstObjectByType<PlayerController>();
        if (pc) { player = pc.transform; ph = pc.GetComponent<PlayerHealth>(); }
        All.Add(this);
        // desert-tan kit so he reads differently from your grey one
        foreach (var r in GetComponentsInChildren<Renderer>())
            foreach (var m in r.materials) if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.GetColor("_BaseColor") * new Color(0.95f, 0.72f, 0.46f));
        SetupGun();
        if (!shotClip) shotClip = WeaponController.Gunshot(0.3f, 800f, 5);
        audioSrc = gameObject.AddComponent<AudioSource>(); audioSrc.spatialBlend = 1; audioSrc.minDistance = 6; audioSrc.maxDistance = 220; audioSrc.rolloffMode = AudioRolloffMode.Linear; audioSrc.volume = 0.7f;
        tracer = new GameObject("EnemyTracer").AddComponent<LineRenderer>(); tracer.transform.SetParent(transform, false);
        tracer.useWorldSpace = true; tracer.material = new Material(Shader.Find("Sprites/Default"));
        tracer.startColor = new Color(1f, 0.8f, 0.45f, 0.85f); tracer.endColor = new Color(1f, 0.85f, 0.5f, 0.1f);
        tracer.startWidth = 0.02f; tracer.endWidth = 0.01f; tracer.positionCount = 2; tracer.enabled = false;

        // walking on the navigation map; the CharacterController stays only as his body for bullets
        agent = GetComponent<NavMeshAgent>(); if (!agent) agent = gameObject.AddComponent<NavMeshAgent>();
        agent.radius = 0.35f; agent.height = 1.8f; agent.baseOffset = 0;
        agent.acceleration = 14f; agent.angularSpeed = 0; agent.updateRotation = false; agent.autoBraking = true;
        agent.stoppingDistance = 0.3f; agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
        agent.avoidancePriority = Random.Range(30, 70);
        if (NavMesh.SamplePosition(transform.position, out var nh, 4f, NavMesh.AllAreas)) agent.Warp(nh.position);
        home = transform.position; faceDir = transform.forward;
        nextLook = Random.value * 0.3f;
        SetPatrolGoal();
    }

    void OnDestroy() { All.Remove(this); if (Mode == State.Flank) flankers--; }

    // the rifle goes in the right hand the same way as the player's (measured once from the idle pose)
    void SetupGun()
    {
        var hand = anim ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null; if (!hand) return;
        Transform rifle = null;
        foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Rifle") rifle = t;
        if (!rifle) { rifle = new GameObject("Rifle").transform; rifle.SetParent(hand, false); }
        anim.Update(0f);
        var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var palm = mid ? Vector3.Lerp(hand.position, mid.position, 0.6f) : hand.position;
        var rot = Quaternion.LookRotation(transform.forward, Vector3.up);
        rifle.SetPositionAndRotation(palm - rot * new Vector3(0, -0.04f, 0), rot);
        rifle.SetParent(hand, true);
        foreach (Transform c in rifle) Destroy(c.gameObject);
        var gun = GunFactory.AK(rifle, false);
        foreach (var r in gun.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        muzzle = GunFactory.Find(gun, "Muzzle");
    }

    Vector3 Eye => transform.position + Vector3.up * (Crouched ? 1.05f : 1.6f);
    Vector3 PlayerChest => player.position + Vector3.up * (pc && pc.crouch ? 0.9f : 1.3f);

    // the first thing on the line from a to b, ignoring himself (and the player if asked)
    bool Blocked(Vector3 a, Vector3 b, out RaycastHit first, bool ignorePlayer)
    {
        first = default; float best = float.MaxValue; bool any = false;
        var d = b - a; float len = d.magnitude; if (len < 0.01f) return false;
        foreach (var h in Physics.RaycastAll(a, d / len, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (h.collider.transform.IsChildOf(transform)) continue;
            if (ignorePlayer && player && h.collider.transform.IsChildOf(player)) continue;
            if (h.collider.GetComponentInParent<EnemyAI>()) continue;          // his mates don't count as cover
            if (h.distance < best) { best = h.distance; first = h; any = true; }
        }
        return any;
    }

    bool CanSee()
    {
        if (!player || (ph && ph.Dead)) return false;
        var to = PlayerChest - Eye; distToPlayer = to.magnitude;
        bool alerted = Time.time - seenAt < 6f;
        if (distToPlayer > sight) return false;
        if (!alerted && Vector3.Angle(transform.forward, to) > 70f && distToPlayer > 6f) return false;      // looking the other way
        return !Blocked(Eye, PlayerChest, out _, true);
    }

    // ---------- choosing where to go ----------
    void Go(Vector3 p, float speed)
    {
        if (!agent.isOnNavMesh) return;
        agent.speed = speed;
        if (NavMesh.SamplePosition(p, out var h, 3f, NavMesh.AllAreas)) agent.SetDestination(h.position);
    }

    void SetPatrolGoal()
    {
        for (int i = 0; i < 8; i++)
        {
            var r = Random.insideUnitCircle * 18f;
            if (NavMesh.SamplePosition(home + new Vector3(r.x, 0, r.y), out var h, 2f, NavMesh.AllAreas) && h.position.y > -0.3f)
            { Go(h.position, WalkSpd); stateUntil = Time.time + Random.Range(10f, 16f); return; }
        }
    }

    // a spot 3-16 m away where something solid stands between it and the player, at a sensible range
    bool FindCover(out Vector3 best)
    {
        best = default; float bestScore = float.MaxValue; bool found = false;
        var pChest = PlayerChest;
        for (int i = 0; i < 22; i++)
        {
            var r = Random.insideUnitCircle * 16f;
            var c = transform.position + new Vector3(r.x, 0, r.y);
            if (!NavMesh.SamplePosition(c, out var h, 1.5f, NavMesh.AllAreas) || h.position.y < -0.3f) continue;
            var p = h.position; float dp = Vector3.Distance(p, player.position);
            if (dp < 8f || dp > 45f) continue;
            // crouched head height must be hidden by something within 2.5 m of the spot
            var head = p + Vector3.up * 1.0f;
            if (!Blocked(head, pChest, out var hit, true) || hit.distance > 2.5f) continue;
            // ... but standing up he must be able to shoot (or step half a metre to the side and shoot)
            var stand = p + Vector3.up * 1.6f; var side = Vector3.Cross(Vector3.up, (pChest - p).normalized) * 0.7f;
            bool canPeek = !Blocked(stand, pChest, out _, true) || !Blocked(stand + side, pChest, out _, true) || !Blocked(stand - side, pChest, out _, true);
            if (!canPeek) continue;
            float score = Vector3.Distance(transform.position, p) + Mathf.Abs(dp - 22f) * 0.35f;
            if (score < bestScore) { bestScore = score; best = p; found = true; }
        }
        return found;
    }

    void TakeCover()
    {
        if (player && FindCover(out var c))
        {
            cover = c; atCover = false; peeking = false; Crouched = false;
            Go(c, Vector3.Distance(transform.position, c) > 5f ? SprintSpd : RunSpd);
            SetMode(State.Cover); stateUntil = Time.time + Random.Range(8f, 14f); CoverMoves++;
        }
        else { SetMode(State.Open); stateUntil = Time.time + Random.Range(3f, 5f); }
    }

    void SetMode(State m)
    {
        if (Mode == State.Flank && m != State.Flank) flankers--;
        if (m == State.Flank && Mode != State.Flank) flankers++;
        Mode = m;
    }

    // ---------- every frame ----------
    void Update()
    {
        if (dead) return;
        float dt = Time.deltaTime;
        if (Time.time >= nextLook)
        {
            nextLook = Time.time + 0.2f;
            bool was = sees; sees = CanSee();
            if (sees) { if (!was && Time.time - seenAt > 6f) firstSeen = Time.time; seenAt = Time.time; lastKnown = player.position; }
            else if (player && Time.time - WeaponController.LastShotTime < 0.5f && Vector3.Distance(player.position, transform.position) < hearing)
            { if (Mode == State.Patrol) { SetMode(State.Investigate); Go(player.position, RunSpd); stateUntil = Time.time + 12f; } seenAt = Mathf.Max(seenAt, Time.time - 3f); lastKnown = player.position; }
        }
        bool combat = Time.time - seenAt < 7f && player && !(ph && ph.Dead);
        if (combat && (Mode == State.Patrol || Mode == State.Investigate) && sees) TakeCover();
        if (!combat && (Mode == State.Cover || Mode == State.Open || Mode == State.Flank))
        { SetMode(State.Investigate); Crouched = false; Go(lastKnown, RunSpd); stateUntil = Time.time + 12f; }

        faceDir = agent.desiredVelocity.sqrMagnitude > 0.2f ? agent.desiredVelocity : faceDir;
        switch (Mode)
        {
            case State.Patrol:
                if (!agent.pathPending && agent.remainingDistance < 0.6f)
                {
                    if (pauseUntil < Time.time - 1f) pauseUntil = Time.time + Random.Range(2f, 4.5f);       // stop and look round
                    else if (Time.time > pauseUntil) SetPatrolGoal();
                    faceDir = Quaternion.Euler(0, Mathf.Sin(Time.time * 0.8f + lookSeed) * 70f, 0) * faceDir;
                }
                break;
            case State.Investigate:
                if (!agent.pathPending && agent.remainingDistance < 1f)
                    faceDir = Quaternion.Euler(0, Mathf.Sin(Time.time * 1.1f) * 90f, 0) * (lastKnown - home).normalized;
                if (Time.time > stateUntil) { SetMode(State.Patrol); home = transform.position; SetPatrolGoal(); }
                break;
            case State.Cover:
                CoverUpdate();
                break;
            case State.Flank:
                if (sees && Vector3.Distance(transform.position, player.position) < 30f) faceDir = lastKnown - transform.position;
                if (sees && agent.remainingDistance < 6f || Time.time > stateUntil) TakeCover();
                break;
            case State.Open:
                faceDir = lastKnown - transform.position;
                if (Time.time > pauseUntil)
                {
                    // side-step, keeping 9-28 m away
                    var to = (lastKnown - transform.position); to.y = 0; float d = to.magnitude; to /= Mathf.Max(d, 0.1f);
                    var step = Vector3.Cross(Vector3.up, to) * Random.Range(-5f, 5f) + to * (d > 28f ? 6f : d < 9f ? -5f : 0f);
                    Go(transform.position + step, RunSpd * 0.75f); pauseUntil = Time.time + Random.Range(1.4f, 2.6f);
                }
                if (sees) Shoot();
                if (Time.time > stateUntil) TakeCover();
                break;
        }
        // a few seconds into a fight, one of them may go round your side
        if (Mode == State.Cover && atCover && flankers < 1 && Time.time - firstSeen > 10f && Random.value < 0.002f && player)
        {
            var to = (transform.position - player.position); to.y = 0; to.Normalize();
            var side = Quaternion.Euler(0, Random.value < 0.5f ? 80f : -80f, 0) * to * 20f;
            if (NavMesh.SamplePosition(player.position + side, out var fh, 4f, NavMesh.AllAreas) && fh.position.y > -0.3f)
            { SetMode(State.Flank); Crouched = false; Go(fh.position, SprintSpd); stateUntil = Time.time + 14f; }
        }

        // hit: stop for a moment
        agent.isStopped = Time.time < staggerUntil;

        // face: the player when fighting, otherwise the way he walks; turning is smooth
        if (combat && sees && Mode != State.Flank) faceDir = lastKnown - transform.position;
        faceDir.y = 0;
        if (faceDir.sqrMagnitude > 0.01f)
        {
            var want = Quaternion.LookRotation(faceDir);
            float turn = Quaternion.Angle(transform.rotation, want);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, Mathf.Clamp(turn * 6f, 90f, 540f) * dt);
        }
        Animate(dt);
        if (Time.time > tracerOff) tracer.enabled = false;
    }

    void CoverUpdate()
    {
        if (!atCover)
        {
            // still running there: shoot only if he's already slowed down and sees you
            if (!agent.pathPending && agent.remainingDistance < 0.5f) { atCover = true; Crouched = true; duckUntil = Time.time + Random.Range(0.8f, 2f); }
            else if (sees && agent.velocity.magnitude < 2f) Shoot();
            return;
        }
        faceDir = lastKnown - transform.position;
        if (Time.time > nextCoverCheck && player)
        {
            nextCoverCheck = Time.time + 0.6f;
            // flanked: crouched head now in plain view -> move
            if (!Blocked(cover + Vector3.up * 1.0f, PlayerChest, out var h, true) || h.distance > 2.8f) { TakeCover(); return; }
        }
        if (!peeking && Time.time > duckUntil) { peeking = true; Crouched = false; peekUntil = Time.time + Random.Range(1.6f, 3.2f); burstLeft = 0; nextBurst = Time.time + 0.35f; }
        else if (peeking)
        {
            if (sees) Shoot();
            if (Time.time > peekUntil) { peeking = false; Crouched = true; duckUntil = Time.time + Random.Range(1.5f, 3.5f); }
        }
        if (Time.time > stateUntil) TakeCover();       // don't sit in one place all fight
    }

    void Animate(float dt)
    {
        if (!anim) return;
        var v = agent.velocity; v.y = 0;
        var local = transform.InverseTransformDirection(v);
        // blend tree tops out at the sprint clip; play faster beyond it so the feet don't skate
        anim.SetFloat(VelX, local.x, 0.12f, dt);
        anim.SetFloat(VelZ, Mathf.Min(local.z, PlayerController.SprintSpeed), 0.12f, dt);
        anim.SetFloat(AnimSpeed, local.z > PlayerController.SprintSpeed ? local.z / PlayerController.SprintSpeed : 1f);
        anim.SetBool(CrouchP, Crouched && v.magnitude < 1.5f);
    }

    void Shoot()
    {
        if (Time.time - firstSeen < 0.7f) return;                       // reaction time
        if (Vector3.Angle(transform.forward, lastKnown - transform.position) > 25f) return;
        if (burstLeft <= 0)
        {
            if (Time.time < nextBurst) return;
            burstLeft = Random.Range(3, 6); nextShot = Time.time;
        }
        if (Time.time < nextShot) return;
        nextShot = Time.time + 0.13f; burstLeft--;
        if (burstLeft <= 0) nextBurst = Time.time + Random.Range(1.1f, 2.3f);

        float chance = Mathf.Clamp(0.4f - distToPlayer / 190f, 0.05f, 0.34f);
        if (pc) { if (pc.Speed > 3f) chance *= 0.6f; if (pc.crouch) chance *= 0.7f; }
        if (agent.velocity.magnitude > 2f) chance *= 0.5f;             // firing on the move
        bool hit = Random.value < chance;
        var from = muzzle ? muzzle.position : Eye;
        var aim = player.position + Vector3.up * 1.2f + (hit ? Vector3.zero : Random.onUnitSphere * Random.Range(0.6f, 1.6f));
        tracer.SetPosition(0, from); tracer.SetPosition(1, from + (aim - from).normalized * (distToPlayer + 4f));
        tracer.enabled = true; tracerOff = Time.time + 0.045f;
        audioSrc.pitch = Random.Range(0.9f, 1.05f); audioSrc.PlayOneShot(shotClip);
        if (hit && ph) ph.Damage(damage, from);
    }

    public override void Hit(float dmg, Vector3 point, Vector3 dir)
    {
        if (dead) return;
        bool head = point.y > transform.position.y + (Crouched ? 0.95f : 1.5f);
        health -= dmg * (head ? 2.5f : 1f);
        onHit?.Invoke(this);
        if (health <= 0) { Die(dir, head); return; }
        staggerUntil = Time.time + 0.3f;
        if (anim) anim.transform.localRotation = Quaternion.Euler(Random.Range(-8f, -4f), Random.Range(-10f, 10f), 0);   // flinch
        if (player)
        {
            seenAt = Time.time; lastKnown = player.position;
            if (firstSeen < Time.time - 6f) firstSeen = Time.time - 0.4f;
            if (Mode != State.Cover || !atCover || !Crouched) TakeCover();     // caught in the open: run for it
        }
    }

    void LateUpdate()
    {
        if (anim && !dead) anim.transform.localRotation = Quaternion.Slerp(anim.transform.localRotation, Quaternion.identity, 1 - Mathf.Exp(-10 * Time.deltaTime));
    }

    void Die(Vector3 dir, bool head)
    {
        dead = true; All.Remove(this); if (Mode == State.Flank) flankers--; Mode = State.Patrol;
        if (agent) agent.enabled = false;
        cc.enabled = false; tracer.enabled = false;
        if (anim) { anim.SetFloat(VelX, 0); anim.SetFloat(VelZ, 0); anim.SetBool(CrouchP, false); anim.CrossFade(Vector3.Dot(dir, transform.forward) > 0 ? "Death Back" : "Death Front", 0.1f); }
        if (ph) ph.AddKill(head);
        Destroy(gameObject, 14f);
    }

    // told where the player is (a mate radioed it in / the test): go and look
    public void Alert(Vector3 at)
    {
        lastKnown = at; seenAt = Mathf.Max(seenAt, Time.time - 3f);
        if (Mode == State.Patrol) { SetMode(State.Investigate); Go(at, RunSpd); stateUntil = Time.time + 20f; }
    }
    public float PathLeft => agent && agent.isOnNavMesh ? agent.remainingDistance : -1;

    public bool IsDead => dead;
    public bool SeesPlayer => sees;
}
