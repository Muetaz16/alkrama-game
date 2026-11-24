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
        if (!armL || !foreL || !handL || !FindGuns()) return;
        Transform gun = null, model = null;
        for (int i = 0; i < guns.Length; i++) if (guns[i] && guns[i].gameObject.activeInHierarchy) { gun = guns[i]; model = models[i]; break; }
        bool hold = gun && model && !player.Sprinting && !player.frozen;
        ikW = Mathf.MoveTowards(ikW, hold ? 1 : 0, dt * 5f);
        if (ikW < 0.01f || !model) return;
        var gl = GunFactory.Find(model, "GripL");
        var grip = gl ? gl.position : model.TransformPoint(new Vector3(0, 0.02f, 0.3f));
        // wrist just under the handguard, on the body's side of it
        var want = grip - model.up * 0.045f - model.right * 0.02f - model.forward * 0.03f;
        var target = Vector3.Lerp(handL.position, want, Mathf.SmoothStep(0, 1, ikW));
        TwoBone(armL, foreL, handL, target, -transform.right * 0.6f - Vector3.up - transform.forward * 0.2f);
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
