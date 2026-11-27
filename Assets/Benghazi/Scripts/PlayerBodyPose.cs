using UnityEngine;

// The soldier's own body (what you see in third person, and his shadow in first person), layered on top of
// the motion capture after the animator has run:
//   - the support hand is put on the rifle's handguard (the mocap was made with another rifle, so the left
//     hand hung in the air beside this one), released while he sprints and for the knife
//   - head and neck follow where he is looking instead of staring straight ahead
//   - standing still the torso turns with the eyes while the feet stay put (PlayerController.Twist)
//   - the body leans into a start, back into a stop and into a sidestep, and breathes when it stands
// Starts itself on the player; nothing in the scene refers to it.
[DefaultExecutionOrder(200)]            // after WeaponController has leaned the chest
public class PlayerBodyPose : MonoBehaviour
{
    PlayerController player; ThirdPersonCamera rig; Animator anim;
    Transform armL, foreL, handL, neck, head;
    Transform[] spine; float twist, leanF, leanS, breath;
    Transform armR, foreR, legL, legR, shinL, shinR, hips; Vector3 modelPos; Quaternion modelRot; bool modelKept;
    Transform[] guns; Transform[] models;
    float ikW, lookPitch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var p = FindFirstObjectByType<PlayerController>();
        if (p && !p.GetComponent<PlayerBodyPose>()) p.gameObject.AddComponent<PlayerBodyPose>();
    }

    void Start()
    {
        player = GetComponent<PlayerController>(); rig = FindFirstObjectByType<ThirdPersonCamera>();
        anim = GetComponentInChildren<Animator>();
        if (!anim) { enabled = false; return; }
        armL = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); foreL = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm); handL = anim.GetBoneTransform(HumanBodyBones.LeftHand);
        neck = anim.GetBoneTransform(HumanBodyBones.Neck); head = anim.GetBoneTransform(HumanBodyBones.Head);
        armR = anim.GetBoneTransform(HumanBodyBones.RightUpperArm); foreR = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
        legL = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg); legR = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        shinL = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg); shinR = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        modelPos = anim.transform.localPosition; modelRot = anim.transform.localRotation; modelKept = true;
        var list = new System.Collections.Generic.List<Transform>();
        foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest }) { var t = anim.GetBoneTransform(b); if (t) list.Add(t); }
        spine = list.ToArray();
    }

    // the gun objects are made by WeaponController.Start: look for them once they exist
    bool FindGuns()
    {
        if (guns != null) return guns.Length > 0;
        Transform rifle = null;
        foreach (var t in anim.GetComponentsInChildren<Transform>(true)) if (t.name == "Rifle") { rifle = t; break; }
        if (!rifle || rifle.childCount == 0) return false;
        var list = new System.Collections.Generic.List<Transform>(); var ms = new System.Collections.Generic.List<Transform>();
        foreach (Transform s in rifle.parent)
            if ((s.name == "Rifle" || s.name == "Sniper") && s.childCount > 0) { list.Add(s); ms.Add(s.GetChild(0)); }
        guns = list.ToArray(); models = ms.ToArray();
        return guns.Length > 0;
    }

    void LateUpdate()
    {
        if (!anim || !player) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        if (Swim(dt)) return;
        if (spine != null && spine.Length > 0)
        {
            float still = 1 - Mathf.Clamp01(player.Speed / 1.5f);
            twist = Mathf.Lerp(twist, player.Twist, 1 - Mathf.Exp(-14 * dt));
            leanF = Mathf.Lerp(leanF, Mathf.Clamp(player.AccelForward * 0.45f, -5f, 6f), 1 - Mathf.Exp(-6 * dt));
            leanS = Mathf.Lerp(leanS, Mathf.Clamp(player.Strafe * 9f, -5f, 5f), 1 - Mathf.Exp(-6 * dt));
            breath += dt * (1.5f + 2.5f * Mathf.Clamp01(player.Speed / PlayerController.SprintMove));
            float chestUp = Mathf.Sin(breath) * 0.7f * still;
            float n = spine.Length;
            foreach (var b in spine)
            {
                b.rotation = Quaternion.AngleAxis(twist / n, Vector3.up) * b.rotation;
                b.rotation = Quaternion.AngleAxis((leanF + chestUp) / n, transform.right) * b.rotation;
                b.rotation = Quaternion.AngleAxis(-leanS / n, transform.forward) * b.rotation;
            }
        }
        // head follows the eyes: the chest already takes about half of the pitch, the neck and head the rest
        if (rig && head)
        {
            lookPitch = Mathf.Lerp(lookPitch, rig.pitch * 0.4f, 1 - Mathf.Exp(-10 * dt));
            var q = Quaternion.AngleAxis(lookPitch * 0.5f, transform.right);
            if (neck) neck.rotation = q * neck.rotation;
            head.rotation = q * head.rotation;
        }
        if (!armL || !foreL || !handL || !armR || !foreR || !FindGuns()) return;
        Shoulder(dt);
    }

    // The rifle, shouldered the way he is taught to shoulder it - not the way the motion capture happened to
    // carry another rifle. Each frame the rifle is put where it belongs and the arms are bent onto it:
    //   ready / firing   butt plate in the pocket of the right shoulder, the rifle along the line of sight
    //                    (muzzle a little low when he is not aiming), cheek down on the stock, right hand on
    //                    the grip, left hand under the handguard, elbows in
    //   sprinting        carried by the right hand alone: muzzle up, the stock by the side of his face with
    //                    its heel on the shoulder; the left arm free to run with
    // Dead, swimming or with the knife out, it is left to what is already there.
    Transform handR; Vector3 gunLocalPos; Quaternion gunLocalRot; Transform poseGun; float carryT, buttZ, shoulderW;
    public static float LastButtGap { get; private set; } = -1;
    void Shoulder(float dt)
    {
        Transform gun = null, model = null;
        for (int i = 0; i < guns.Length; i++) if (guns[i] && guns[i].gameObject.activeInHierarchy) { gun = guns[i]; model = models[i]; break; }
        if (!handR) handR = anim.GetBoneTransform(HumanBodyBones.RightHand);
        bool on = gun && model && handR && !player.frozen && !player.Swimming && gun.parent == handR;
        shoulderW = Mathf.MoveTowards(shoulderW, on ? 1 : 0, dt * 5f);
        if (!on || shoulderW < 0.01f) { LastButtGap = -1; return; }
        if (poseGun != gun)
        {
            // how the rifle sits in the hand (WeaponController measured it once), and where its butt plate is
            poseGun = gun; gunLocalPos = gun.localPosition; gunLocalRot = gun.localRotation;
            float minZ = 0;
            foreach (var mf in gun.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue; var mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++) minZ = Mathf.Min(minZ, gun.InverseTransformPoint(mf.transform.TransformPoint(new Vector3((c & 1) != 0 ? mb.max.x : mb.min.x, (c & 2) != 0 ? mb.max.y : mb.min.y, (c & 4) != 0 ? mb.max.z : mb.min.z))).z);
            }
            buttZ = minZ + 0.012f;
        }
        carryT = Mathf.MoveTowards(carryT, player.Sprinting ? 1 : 0, dt / 0.22f);
        float carry = Mathf.SmoothStep(0, 1, carryT), w = Mathf.SmoothStep(0, 1, shoulderW);
        Vector3 fwd = transform.forward, right = transform.right, up = Vector3.up;
        // where he is looking (the body may still be turning to it)
        var look = rig ? Quaternion.Euler(rig.pitch, rig.yaw, 0) : transform.rotation;
        var aim = Quaternion.AngleAxis(player.aiming ? 0f : 9f, look * Vector3.right) * (look * Vector3.forward);       // low ready when not on the sights
        var carryDir = (up * 0.7f + fwd * 0.7f + right * 0.08f).normalized;        // muzzle up and forward, not straight overhead
        var dir = Vector3.Slerp(aim, carryDir, carry).normalized;
        var rot = Quaternion.LookRotation(dir, Vector3.Slerp(up, -fwd * 0.6f + up * 0.6f, carry)) * Quaternion.Euler(0, 0, Mathf.Lerp(-4f, -18f, carry));
        // the pocket of the right shoulder; for the carry the heel of the stock rides on top of it
        var pocket = armR.position - right * 0.055f + fwd * 0.07f - up * 0.015f;
        pocket = Vector3.Lerp(pocket, armR.position - right * 0.03f + fwd * 0.05f + up * 0.05f, carry);
        var plateLocal = new Vector3(0, 0, buttZ);
        var gunPos = pocket - rot * Vector3.Scale(plateLocal, gun.lossyScale);
        // the right hand goes where it has to be for the rifle to lie like that
        var handRot = rot * Quaternion.Inverse(gunLocalRot);
        var handPos = gunPos - handRot * Vector3.Scale(gunLocalPos, handR.lossyScale);
        TwoBone(armR, foreR, handR, Vector3.Lerp(handR.position, handPos, w), right * 0.75f - up * 0.9f - fwd * 0.15f);
        handR.rotation = Quaternion.Slerp(handR.rotation, handRot, w);
        LastButtGap = Vector3.Distance(gun.TransformPoint(plateLocal), pocket);
        // cheek to the stock: the head comes down and over to the rifle
        if (head) head.rotation = Quaternion.AngleAxis(7f * w * (1 - carry), fwd) * Quaternion.AngleAxis(5f * w * (1 - carry), right) * head.rotation;
        // support hand under the handguard (free on the run)
        ikW = Mathf.MoveTowards(ikW, player.Sprinting ? 0 : 1, dt * 5f);
        if (ikW > 0.01f)
        {
            var gl = GunFactory.Find(model, "GripL");
            var grip = gl ? gl.position : model.TransformPoint(new Vector3(0, 0.02f, 0.3f));
            var want = grip - model.up * 0.045f - model.right * 0.02f - model.forward * 0.03f;
            TwoBone(armL, foreL, handL, Vector3.Lerp(handL.position, want, Mathf.SmoothStep(0, 1, ikW) * w), -right * 0.55f - up - fwd * 0.2f);
        }
    }

    // Front crawl, built on the body each frame (no swimming was ever motion-captured for him), the way it is
    // coached:
    //   body     long and flat at the surface, hips up; it ROLLS about its long axis with every stroke - the
    //            shoulder of the recovering arm comes clear of the water, the other drives down and forward
    //   arms     half a cycle apart. Entry fingertips first in front of the shoulder, reach, catch with a high
    //            elbow, pull back under the body in a shallow S to a finish past the hip; recovery over the
    //            water with the elbow leading and the hand relaxed below it
    //   legs     six-beat flutter from the hips, knees almost straight, toes pointed
    //   head     in line with the spine, face in the water; every second cycle it turns with the body roll to
    //            breathe on the side of the recovering arm, one goggle still in the water
    //   still    treading water: upright, hands sculling, legs working under him
    // with a little white water where the hands go in and where the feet kick.
    Transform footL, footR; float swimMove; ParticleSystem splash; readonly float[] lastT = { 0.5f, 0.5f }; float lastKick;
    bool Swim(float dt)
    {
        var model = anim.transform; float k = Mathf.SmoothStep(0, 1, player.SwimBlend);
        if (k <= 0.001f) { if (modelKept) { model.localPosition = modelPos; model.localRotation = modelRot; } swimMove = 0; return false; }
        if (!hips || !armL || !armR || !handL) return false;
        if (!handR) handR = anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (!footL) { footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot); footR = anim.GetBoneTransform(HumanBodyBones.RightFoot); }
        Vector3 fwd = transform.forward, right = transform.right, up = Vector3.up;
        swimMove = Mathf.Lerp(swimMove, Mathf.Clamp01(player.Speed / (PlayerController.SwimSpeed * 0.75f)), 1 - Mathf.Exp(-3.5f * dt));
        float mv = Mathf.SmoothStep(0, 1, swimMove), ph = player.SwimPhase, cycle = ph / (2 * Mathf.PI);
        float tR = Mathf.Repeat(cycle, 1f), tL = Mathf.Repeat(cycle + 0.5f, 1f);

        // flat on the water when he swims, upright when he treads
        model.localRotation = modelRot; model.localPosition = modelPos;
        model.RotateAround(hips.position, right, Mathf.Lerp(24f, 85f, mv) * k);
        float roll = -Mathf.Sin(ph) * 30f * mv * k;                                        // right shoulder up while the right arm recovers
        model.RotateAround(hips.position, fwd, roll * 0.45f);
        float wantY = PlayerController.WaterLevel - Mathf.Lerp(0.46f, 0.13f, mv) + Mathf.Sin(ph * 2f) * 0.012f;
        model.position += up * ((wantY - hips.position.y) * k) + fwd * (0.32f * k * mv);
        if (spine != null) foreach (var b in spine) b.rotation = Quaternion.AngleAxis(roll * 0.55f / spine.Length, fwd) * b.rotation;

        // head: in line and face down; turned to breathe on the right every second cycle
        float breathe = 0;
        if (Mathf.FloorToInt(cycle) % 2 == 0 && tR > 0.48f && tR < 0.98f) breathe = Mathf.Sin((tR - 0.48f) / 0.5f * Mathf.PI);
        float lift = Mathf.Lerp(-22f, -9f, mv) * k;                                         // treading: chin up out of the water
        if (neck) neck.rotation = Quaternion.AngleAxis(lift * 0.5f, right) * Quaternion.AngleAxis(breathe * 30f * mv * k, fwd) * neck.rotation;
        if (head) head.rotation = Quaternion.AngleAxis(lift * 0.5f, right) * Quaternion.AngleAxis(breathe * 48f * mv * k, fwd) * head.rotation;

        // arms
        CrawlArm(armR, foreR, handR, tR, 1, fwd, right, up, k, mv, ph);
        CrawlArm(armL, foreL, handL, tL, -1, fwd, right, up, k, mv, ph);

        // legs: six beats to the cycle from the hips, loose knees, pointed toes; treading, a slow scissor under him
        float beat = ph * 3f, kick = Mathf.Sin(beat) * Mathf.Lerp(20f, 13f, mv) * k, rate = Mathf.Lerp(0.5f, 1f, mv);
        kick = Mathf.Lerp(Mathf.Sin(ph * 1.5f) * 22f * k, kick, mv);
        if (legL) legL.rotation = Quaternion.AngleAxis(kick, right) * legL.rotation;
        if (legR) legR.rotation = Quaternion.AngleAxis(-kick, right) * legR.rotation;
        if (shinL) shinL.rotation = Quaternion.AngleAxis(Mathf.Max(0, -kick) * 0.9f + 4f * k, right) * shinL.rotation;
        if (shinR) shinR.rotation = Quaternion.AngleAxis(Mathf.Max(0, kick) * 0.9f + 4f * k, right) * shinR.rotation;
        if (footL) footL.rotation = Quaternion.AngleAxis(48f * k * mv, right) * footL.rotation;
        if (footR) footR.rotation = Quaternion.AngleAxis(48f * k * mv, right) * footR.rotation;

        // white water: where each hand goes in, and at the feet on every beat
        if (mv > 0.4f && k > 0.9f)
        {
            if (lastT[0] > 0.8f && tR < 0.2f && handR) Splash(handR.position, fwd, 9);
            if (lastT[1] > 0.8f && tL < 0.2f) Splash(handL.position, fwd, 9);
            float kb = Mathf.Floor(beat / Mathf.PI);
            if (kb != lastKick && footL && footR) { lastKick = kb; Splash((footL.position + footR.position) * 0.5f, -fwd, 4); }
        }
        lastT[0] = tR; lastT[1] = tL;
        return true;
    }

    // one arm of the crawl: the hand is taken round its path and the arm bent onto it (two-bone IK), so the
    // elbow is where a swimmer's is - high and outside the hand in the catch, leading the hand in the recovery
    void CrawlArm(Transform upper, Transform fore, Transform hand, float t, float side, Vector3 fwd, Vector3 right, Vector3 up, float k, float mv, float ph)
    {
        if (!upper || !fore || !hand) return;
        var S = upper.position;
        Vector3 L(float f, float u, float r) => S + fwd * f + up * u + right * (side * r);
        Vector3 p, pole;
        if (t < 0.52f)
        {
            // under water: entry -> reach -> catch -> through under the chest -> finish past the hip
            float e = t / 0.52f; e = e * e * (3 - 2 * e);
            p = Bezier(L(0.6f, -0.05f, 0.02f), L(0.5f, -0.3f, 0.06f), L(-0.02f, -0.44f, -0.12f), L(-0.5f, -0.1f, 0.1f), e);
            pole = right * (side * 0.9f) + up * 0.7f - fwd * 0.1f;
        }
        else
        {
            // over the water: elbow leads up and forward, hand hanging under it, then reaches for the entry
            float e = (t - 0.52f) / 0.48f; e = e * e * (3 - 2 * e);
            p = Bezier(L(-0.5f, -0.1f, 0.1f), L(-0.34f, 0.2f, 0.24f), L(0.22f, 0.2f, 0.2f), L(0.6f, -0.05f, 0.02f), e);
            pole = up * 1f + right * (side * 0.75f) - fwd * 0.15f;
        }
        // treading water: wide figure-of-eight sculls just under the surface
        var scull = L(0.24f + Mathf.Sin(ph * 1.5f + (side > 0 ? 0 : Mathf.PI)) * 0.05f, -0.2f, 0.36f + Mathf.Cos(ph * 1.5f) * 0.1f);
        p = Vector3.Lerp(scull, p, mv); pole = Vector3.Slerp(right * side - up * 0.4f, pole, mv);
        TwoBone(upper, fore, hand, Vector3.Lerp(hand.position, p, k), pole);
        // the hand: flat, in line with the forearm, palm to the water behind it
        hand.rotation = Quaternion.Slerp(hand.rotation, fore.rotation, 0.75f * k);
    }

    static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        float u = 1 - t;
        return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
    }

    void Splash(Vector3 at, Vector3 dir, int count)
    {
        if (!splash)
        {
            var sh = Shader.Find("Sprites/Default"); if (!sh) return;
            var go = new GameObject("Swim splash"); splash = go.AddComponent<ParticleSystem>();
            splash.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = splash.main; main.loop = false; main.playOnAwake = false; main.maxParticles = 160; main.gravityModifier = 1.3f; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = splash.emission; em.enabled = false; var shp = splash.shape; shp.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = new Material(sh) { mainTexture = Dot(32) }; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        var ep = new ParticleSystem.EmitParams { startColor = new Color(0.93f, 0.97f, 1f, 0.75f) };
        at.y = PlayerController.WaterLevel + 0.02f;
        for (int i = 0; i < count; i++)
        {
            ep.position = at + Random.insideUnitSphere * 0.06f; ep.velocity = dir * Random.Range(0.3f, 1.2f) + Vector3.up * Random.Range(0.7f, 1.9f) + Random.insideUnitSphere * 0.5f;
            ep.startSize = Random.Range(0.025f, 0.07f); ep.startLifetime = Random.Range(0.3f, 0.55f);
            splash.Emit(ep, 1);
        }
    }

    static Texture2D Dot(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), Vector2.one * n * 0.5f) / (n * 0.5f); px[y * n + x] = new Color(1, 1, 1, Mathf.Clamp01(1 - d * d)); }
        t.SetPixels32(px); t.Apply(); return t;
    }

    static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole)
    {
        Vector3 pa = a.position, pb = b.position;
        float l1 = (pb - pa).magnitude, l2 = (c.position - pb).magnitude;
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
}
