using System.Collections.Generic;
using UnityEngine;

// Enemy soldier. Patrols round the spot he appeared at; when he sees you (within 75 m, in front of him),
// hears your shots (45 m) or gets hit, he turns on you: closes in to about 25 m, side-steps, and fires
// bursts. His aim gets worse with distance and when you move or crouch. 100 health, head shots x2.5.
// No nav-mesh: he steers round walls with a forward probe and never walks into the sea.
public class EnemyAI : Damageable
{
    public static readonly List<EnemyAI> All = new();
    const float sight = 75f, hearing = 45f, damage = 6f;

    CharacterController cc; Animator anim; Transform player; PlayerHealth ph; PlayerController pc;
    Vector3 home, goal, lastKnown, vel;
    float nextLook, seenAt = -99, firstSeen = -99, nextBurst, nextShot, strafe, strafeUntil, goalUntil, avoidSide = 1, avoidUntil;
    int burstLeft; bool sees, dead; float distToPlayer = 999;
    Transform muzzle; LineRenderer tracer; float tracerOff; AudioSource audioSrc; static AudioClip shotClip;
    static readonly int VelX = Animator.StringToHash("VelX"), VelZ = Animator.StringToHash("VelZ");

    void Start()
    {
        health = 100; fallsOver = false;
        cc = GetComponent<CharacterController>(); anim = GetComponentInChildren<Animator>();
        pc = FindFirstObjectByType<PlayerController>();
        if (pc) { player = pc.transform; ph = pc.GetComponent<PlayerHealth>(); }
        home = goal = transform.position;
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
        nextLook = Random.value * 0.3f;
    }

    void OnDestroy() { All.Remove(this); }

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

    Vector3 Eye => transform.position + Vector3.up * 1.6f;

    bool CanSee()
    {
        if (!player || (ph && ph.Dead)) return false;
        var target = player.position + Vector3.up * (pc && pc.crouch ? 0.9f : 1.3f);
        var to = target - Eye; distToPlayer = to.magnitude;
        bool alerted = Time.time - seenAt < 6f;
        if (distToPlayer > sight) return false;
        if (!alerted && Vector3.Angle(transform.forward, to) > 70f && distToPlayer > 6f) return false;      // looking the other way
        // first thing on the line must be the player
        float best = float.MaxValue; Transform hit = null;
        foreach (var h in Physics.RaycastAll(Eye, to.normalized, distToPlayer + 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (!h.collider.transform.IsChildOf(transform) && h.distance < best) { best = h.distance; hit = h.collider.transform; }
        return hit && hit.IsChildOf(player);
    }

    void Update()
    {
        if (dead) return;
        float dt = Time.deltaTime;
        if (Time.time >= nextLook)
        {
            nextLook = Time.time + 0.2f;
            bool was = sees; sees = CanSee();
            if (sees) { if (!was && Time.time - seenAt > 6f) firstSeen = Time.time; seenAt = Time.time; lastKnown = player.position; }
            else if (player && distToPlayer < hearing && Time.time - WeaponController.LastShotTime < 0.5f) { seenAt = Time.time; lastKnown = player.position; }   // heard you
        }
        bool combat = Time.time - seenAt < 6f;
        Vector3 wish = Vector3.zero, face = transform.forward;
        if (combat)
        {
            var toP = lastKnown - transform.position; toP.y = 0; float d = toP.magnitude; var dir = d > 0.1f ? toP / d : transform.forward;
            face = dir;
            if (sees)
            {
                if (Time.time > strafeUntil) { strafe = Random.Range(-1, 2); strafeUntil = Time.time + Random.Range(1.2f, 2.8f); }
                if (d > 28f) wish = dir * 3.4f; else if (d < 9f) wish = -dir * 2.2f;
                wish += Vector3.Cross(Vector3.up, dir) * strafe * 1.9f;
                Shoot();
            }
            else if (d > 2.5f) wish = dir * 3.6f;                      // go and look where he last saw you
        }
        else
        {
            // patrol: stroll to a random point near home, wait, pick another
            var toG = goal - transform.position; toG.y = 0;
            if (toG.magnitude < 1.2f || Time.time > goalUntil)
            {
                var r = Random.insideUnitCircle * 14f; goal = home + new Vector3(r.x, 0, r.y); goalUntil = Time.time + Random.Range(5f, 10f);
            }
            else if (Time.time > goalUntil - 3f) wish = Vector3.zero;   // pause at the end of each leg
            else { wish = toG.normalized * 1.7f; face = wish; }
        }
        // steer: round walls, and never into the sea
        if (wish.sqrMagnitude > 0.01f)
        {
            var wd = wish.normalized; var chest = transform.position + Vector3.up * 0.9f;
            bool blocked = false;
            foreach (var h in Physics.SphereCastAll(chest, 0.3f, wd, 1.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && !(player && h.collider.transform.IsChildOf(player)) && h.point.y > transform.position.y + 0.45f) { blocked = true; break; }
            if (blocked)
            {
                if (Time.time > avoidUntil) { avoidSide = Random.value < 0.5f ? -1 : 1; avoidUntil = Time.time + 1.5f; }
                wish = Quaternion.Euler(0, 75f * avoidSide, 0) * wish;
                if (!combat) goalUntil = Mathf.Min(goalUntil, Time.time + 1f);
            }
            var ahead = transform.position + wish.normalized * 1.6f + Vector3.up * 2f;
            if (!Physics.Raycast(ahead, Vector3.down, out var g, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || g.point.y < -0.3f) wish = Vector3.zero;
        }
        vel = Vector3.Lerp(vel, wish, 1 - Mathf.Exp(-8 * dt));
        if (face.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(face), 420f * dt);
        if (cc.enabled) cc.SimpleMove(vel);
        if (anim)
        {
            var local = transform.InverseTransformDirection(vel);
            anim.SetFloat(VelX, local.x, 0.1f, dt); anim.SetFloat(VelZ, local.z, 0.1f, dt);
        }
        if (Time.time > tracerOff) tracer.enabled = false;
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
        bool head = point.y > transform.position.y + 1.5f;
        health -= dmg * (head ? 2.5f : 1f);
        onHit?.Invoke(this);
        if (player) { seenAt = Time.time; lastKnown = player.position; if (firstSeen < Time.time - 6f) firstSeen = Time.time - 0.4f; }   // he knows where it came from
        if (health <= 0) Die(dir, head);
    }

    void Die(Vector3 dir, bool head)
    {
        dead = true; All.Remove(this);
        cc.enabled = false; tracer.enabled = false;
        if (anim) { anim.SetFloat(VelX, 0); anim.SetFloat(VelZ, 0); anim.CrossFade(Vector3.Dot(dir, transform.forward) > 0 ? "Death Back" : "Death Front", 0.1f); }
        if (ph) ph.AddKill(head);
        Destroy(gameObject, 14f);
    }

    public bool IsDead => dead;
    public bool SeesPlayer => sees;
}
