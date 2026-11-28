using System.Collections;
using System.IO;
using UnityEngine;

// Scripted test run so the game can be checked without a person at the keyboard.
// Only active when the player is started with -autotest; it walks, runs, sprints, strafes, crouches
// and jumps, saves screenshots to ../unity_shots/ and a log of positions, then quits.
public class AutoTest : MonoBehaviour
{
    PlayerController player; float startYaw; Vector3 startPos;
    ThirdPersonCamera cam;
    StreamWriter log;
    string outDir;

    void Start()
    {
        if (!System.Environment.CommandLine.Contains("-autotest")) { enabled = false; return; }
        player = FindFirstObjectByType<PlayerController>();
        cam = FindFirstObjectByType<ThirdPersonCamera>();
        player.externalInput = true; cam.externalInput = true;
        // nobody at the mouse or keyboard may steer or fire during the run (a click in the window used to empty half a magazine)
        var wc0 = player.GetComponent<WeaponController>(); if (wc0) wc0.externalInput = true; startYaw = player.yaw; startPos = player.transform.position;
        outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity_shots_hdrp"));
        if (!Application.isEditor) outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../unity_shots_hdrp"));
        Directory.CreateDirectory(outDir);
        log = new StreamWriter(Path.Combine(outDir, "autotest.log"));
        log.WriteLine($"loaded after {Time.realtimeSinceStartup:F1} s, {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}{(PlayerController.LegacyGait ? ", LEGACY GAIT" : "")}{(PlayerController.GaitScale != 1f ? $", gait scale {PlayerController.GaitScale}" : "")}"); log.Flush();
        StartCoroutine(Run());
    }

    IEnumerator Step(string name, Vector2 move, float secs, bool sprint = false, bool walk = false, bool crouch = false, float turn = 0f)
    {
        player.moveInput = move; player.sprint = sprint; player.walk = walk; player.crouch = crouch;
        var start = player.transform.position; int frames = 0; float t0 = Time.realtimeSinceStartup, worst = 0;
        var anim = player.GetComponentInChildren<Animator>();
        FindFeet(anim);
        slipDist = slipTime = strideDist = 0; slipFrom = Time.time + Mathf.Min(0.5f, secs * 0.4f); slipOn = true;      // not while he is still getting up to speed
        travel = new Vector3(move.x, 0, move.y);
        for (float t = 0; t < secs; t += Time.deltaTime)
        {
            frames++; worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            cam.lookDelta = new Vector2(turn * Time.deltaTime, 0);
            yield return null;
        }
        slipOn = false;
        var moved = Vector3.Distance(start, player.transform.position);
        var clip = anim.GetCurrentAnimatorClipInfo(0);
        log.WriteLine($"{name}: {frames / Mathf.Max(0.01f, Time.realtimeSinceStartup - t0):F0} fps (worst frame {worst * 1000:F0} ms), moved {moved:F2} m in {secs}s ({moved / secs:F2} m/s), foot slip {(slipTime > 0 ? slipDist / slipTime : 0):F2} m/s, grounded {player.GetComponent<CharacterController>().isGrounded}, pos {player.transform.position}, clip {(clip.Length > 0 ? clip[0].clip.name : "-")}");
        log.Flush();
    }

    // Foot slip: how fast a foot that is on the ground slides over it (0 = planted, as a real foot is).
    // Measured after the animator has posed the body, on the toe joint: the ball of the foot stays where it
    // is put from foot-flat to toe-off, while the ankle travels as the foot rolls over it. A foot is on the
    // ground while that joint is within 2 cm of where it sits standing still AND it is travelling backwards
    // relative to the body (the swinging foot also passes low, but going forwards).
    void FindFeet(Animator anim)
    {
        if (footL) return;
        footL = anim.GetBoneTransform(HumanBodyBones.LeftToes); footR = anim.GetBoneTransform(HumanBodyBones.RightToes);
        if (!footL || !footR) { footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot); footR = anim.GetBoneTransform(HumanBodyBones.RightFoot); }
    }
    // The same samples give the stride speed: how fast the planted foot goes back under the body, which is
    // the ground speed the clips were made for.
    Transform footL, footR; Vector3 prevL, prevR, prevLl, prevRl, travel; float slipDist, slipTime, strideDist, slipFrom, ankleRest = -1; bool slipOn, slipPrev;
    void LateUpdate()
    {
        if (!footL || !footR) return;
        var pt = player.transform; Vector3 l = footL.position, r = footR.position, ll = pt.InverseTransformPoint(l), rl = pt.InverseTransformPoint(r);
        float ground = pt.position.y, dt = Time.deltaTime;
        if (ankleRest < 0 && player.Speed < 0.01f && Time.timeSinceLevelLoad > 1f) ankleRest = Mathf.Min(l.y, r.y) - ground;
        if (slipOn && slipPrev && Time.time >= slipFrom && ankleRest >= 0 && dt > 0)
        {
            float lim = ground + ankleRest + 0.02f;
            var dirL = travel.sqrMagnitude > 0.01f ? travel.normalized : Vector3.zero;       // body-space direction of travel
            void Foot(Vector3 w, Vector3 pw, Vector3 loc, Vector3 ploc)
            {
                if (w.y > lim) return;
                var dl = loc - ploc; dl.y = 0;
                if (dirL != Vector3.zero && Vector3.Dot(dl, dirL) >= 0) return;               // going forwards: it is swinging, not planted
                var d = w - pw; d.y = 0;
                slipDist += d.magnitude; strideDist += dirL != Vector3.zero ? -Vector3.Dot(dl, dirL) : 0; slipTime += dt;
            }
            Foot(l, prevL, ll, prevLl); Foot(r, prevR, rl, prevRl);
        }
        prevL = l; prevR = r; prevLl = ll; prevRl = rl; slipPrev = slipOn;
    }

    // The clips' own ground speeds: hold the body still, drive the animator by hand to each clip set and
    // watch how fast the planted foot travels back under the body.
    IEnumerator Calibrate()
    {
        var anim = player.GetComponentInChildren<Animator>();
        FindFeet(anim);
        player.moveInput = Vector2.zero; yield return new WaitForSeconds(0.5f);
        player.enabled = false;
        var sets = new (string name, Vector2 v, bool crouch)[] {
            ("walk fwd", new Vector2(0, PlayerController.WalkSpeed), false), ("run fwd", new Vector2(0, PlayerController.RunSpeed), false), ("sprint fwd", new Vector2(0, PlayerController.SprintSpeed), false),
            ("walk back", new Vector2(0, -PlayerController.WalkSpeed), false), ("run back", new Vector2(0, -PlayerController.RunSpeed), false),
            ("walk left", new Vector2(-PlayerController.WalkSpeed, 0), false), ("run left", new Vector2(-PlayerController.RunSpeed, 0), false),
            ("crouch fwd", new Vector2(0, PlayerController.CrouchSpeed), true), ("crouch left", new Vector2(-PlayerController.CrouchSpeed, 0), true) };
        foreach (var (name, v, crouch) in sets)
        {
            anim.SetBool("Crouch", crouch); anim.SetBool("Grounded", true); anim.SetFloat("AnimSpeed", 1f);
            for (float t = 0; t < 0.6f; t += Time.deltaTime) { anim.SetFloat("VelX", v.x); anim.SetFloat("VelZ", v.y); yield return null; }
            travel = new Vector3(v.x, 0, v.y); slipDist = slipTime = strideDist = 0; slipFrom = 0; slipOn = true;
            for (float t = 0; t < 2.4f; t += Time.deltaTime) { anim.SetFloat("VelX", v.x); anim.SetFloat("VelZ", v.y); yield return null; }
            slipOn = false;
            log.WriteLine($"clip pace {name}: laid out at {v.magnitude:F2} m/s, planted foot travels {(slipTime > 0 ? strideDist / slipTime : 0):F2} m/s ({slipTime:F1} s of ground contact)");
        }
        anim.SetBool("Crouch", false); anim.SetFloat("VelX", 0); anim.SetFloat("VelZ", 0);
        player.enabled = true; log.Flush();
        yield return new WaitForSeconds(0.5f);
    }

    IEnumerator Shot(string file)
    {
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, file));
        yield return null;
    }

    // A second camera a hand's breadth to the side of the weapon, for a close look at the hands (trigger
    // finger, knife grip, the hand on the charging handle) that the player's own view hides behind the gun.
    // from / at are in the player camera's space.
    static readonly Vector3 GripFrom = new(0.4f, -0.1f, 0.3f), GripAt = new(0.08f, -0.2f, 0.36f);
    static readonly Vector3 KnifeFrom = new(0.46f, -0.04f, 0.42f), KnifeAt = new(0.14f, -0.17f, 0.38f);
    IEnumerator Inspect(string file, Vector3 from, Vector3 at)
    {
        yield return new WaitForEndOfFrame();
        GameObject go = null; RenderTexture rt = null; Texture2D tex = null;
        try
        {
            var main = Camera.main; var mt = main.transform;
            go = new GameObject("Inspect camera"); var c = go.AddComponent<Camera>();
            c.CopyFrom(main); c.fieldOfView = 36f; c.nearClipPlane = 0.02f; c.enabled = false;
            go.transform.position = mt.TransformPoint(from);
            go.transform.rotation = Quaternion.LookRotation(mt.TransformPoint(at) - go.transform.position, mt.up);
            rt = new RenderTexture(1280, 720, 24); c.targetTexture = rt; c.Render();
            RenderTexture.active = rt; tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
            c.targetTexture = null;
        }
        catch (System.Exception e) { log.WriteLine($"inspect {file}: failed ({e.Message})"); }
        if (rt) Destroy(rt); if (tex) Destroy(tex); if (go) Destroy(go);
        yield return null;
    }

    // the same, looking at the soldier's body from outside: from / at are in the player's own space (x right, y up, z ahead)
    IEnumerator InspectBody(string file, Vector3 from, Vector3 at)
    {
        var mt = Camera.main.transform; var pt = player.transform;
        yield return Inspect(file, mt.InverseTransformPoint(pt.TransformPoint(from)), mt.InverseTransformPoint(pt.TransformPoint(at)));
    }

    // point the camera at a world position
    void LookAt(Vector3 p)
    {
        var pivot = cam.firstPerson ? player.transform.position + Vector3.up * cam.eyeHeight : player.transform.position + Quaternion.Euler(0, cam.yaw, 0) * cam.shoulder;
        var d = p - pivot;
        cam.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        cam.pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
    }

    bool rackShot;
    IEnumerator Shooting()
    {
        var gun = player.GetComponent<WeaponController>();
        if (!gun) yield break;
        gun.externalInput = true;
        var targets = FindObjectsByType<Damageable>(FindObjectsSortMode.None);
        System.Array.Sort(targets, (a, b) => Vector3.Distance(a.transform.position, player.transform.position).CompareTo(Vector3.Distance(b.transform.position, player.transform.position)));
        log.WriteLine($"targets: {targets.Length}");
        if (targets.Length == 0) yield break;
        // rifle: aim at a near target, short burst
        var near = targets[Mathf.Min(1, targets.Length - 1)];
        log.WriteLine("target distances: " + string.Join(", ", System.Array.ConvertAll(targets, x => Vector3.Distance(x.transform.position, player.transform.position).ToString("F0"))));
        LookAt(near.transform.position + Vector3.up * 0.55f);     // torso centre (0.95 is the gap under the head)
        yield return new WaitForSeconds(0.3f);
        yield return Shot("fp_rifle_hip.png");
        log.WriteLine("right shoulder: " + gun.View.ArmsDebug());
        yield return Inspect("inspect_grip_ready.png", GripFrom, GripAt);
        log.WriteLine("ready, " + gun.View.TriggerDebug());
        log.WriteLine("ready, " + gun.View.ShoulderDebug());
        yield return Inspect("inspect_shoulder.png", new Vector3(0.5f, 0.12f, -0.25f), new Vector3(0.06f, -0.2f, 0.12f));
        log.WriteLine(gun.View.HandleDebug());
        gun.aimHeld = true;
        yield return new WaitForSeconds(0.6f);
        LookAt(near.transform.position + Vector3.up * 0.55f);
        yield return null;
        yield return Shot("fp_rifle_ads.png");
        yield return Inspect("inspect_grip_trigger.png", new Vector3(0.36f, -0.1f, 0.2f), new Vector3(0.0f, -0.135f, 0.24f));
        log.WriteLine("aiming, " + gun.View.TriggerDebug());
        int before = gun.hits;
        gun.fireHeld = true;
        yield return new WaitForSeconds(0.25f);
        yield return Shot("fp_rifle_fire.png");
        yield return new WaitForSeconds(0.35f);
        gun.fireHeld = false; gun.aimHeld = false;
        // straight after the burst the rifle is back at the ready with the finger still on the trigger: the side
        // view shows which part of the finger is on it
        yield return new WaitForSeconds(0.22f);
        yield return Inspect("inspect_finger_on_trigger.png", GripFrom, GripAt);
        log.WriteLine("after the burst, " + gun.View.TriggerDebug());
        log.WriteLine($"rifle burst at {Vector3.Distance(near.transform.position, player.transform.position):F0} m: {gun.hits - before} hits, mag {gun.W.mag}");
        yield return new WaitForSeconds(0.4f);
        // reload: the magazine comes out and a new one goes in
        // tactical reload (rounds left): fresh mag strips the old one, old one kept, one stays chambered
        int before2 = gun.W.mag;
        gun.reloadPressed = true;
        float t0 = Time.time, tlen = gun.ReloadLen(true);
        IEnumerator UntilT(float u) { while (Time.time - t0 < tlen * u) yield return null; }
        yield return UntilT(0.13f);
        yield return Shot("fp_reload_pouch.png");
        yield return UntilT(0.34f);
        yield return Shot("fp_reload_magout.png");              // the fresh magazine beside the one in the rifle, both in the hand
        yield return Inspect("inspect_tac_two_mags.png", new Vector3(-0.5f, -0.05f, 0.3f), new Vector3(0f, -0.14f, 0.3f));
        log.WriteLine("reload debug: " + gun.View.ReloadDebug());
        yield return UntilT(0.49f);
        yield return Shot("fp_reload_magin.png");
        yield return UntilT(1.1f);
        log.WriteLine($"tactical reload: tactical {gun.LastReloadTactical}, mag {before2} -> {gun.W.mag}, reserve {gun.W.reserve}");
        // emergency reload (empty): old mag drops, fresh one in, charging handle racked
        gun.W.mag = 0; gun.reloadPressed = true;
        float r0 = Time.time, rlen = gun.ReloadLen(false);
        IEnumerator Until(float u) { while (Time.time - r0 < rlen * u) yield return null; }
        yield return Until(0.22f);
        yield return Shot("fp_reload_draw.png");                // the fresh mag coming up from the chest rig
        yield return Inspect("inspect_reload_up.png", new Vector3(-0.75f, 0.02f, 0.2f), new Vector3(0f, -0.08f, 0.3f));
        yield return Until(ViewModel.ReloadStripAt);
        yield return Shot("fp_reload_drop.png");                // the fresh mag knocks the empty one out
        yield return Until(ViewModel.ReloadUnderAt);
        yield return Shot("fp_reload_under.png");               // hand passing under the rifle
        yield return Until(ViewModel.ReloadRackAt - 0.05f);
        yield return Shot("fp_reload_handle.png");              // hand on the charging handle, from underneath
        yield return Inspect("inspect_rack.png", new Vector3(0.5f, -0.02f, 0.34f), new Vector3(0.07f, -0.13f, 0.4f));
        yield return Until(ViewModel.ReloadRackAt);
        yield return Shot("fp_reload_rack.png");                // handle to the rear
        yield return Until(1.12f);
        log.WriteLine($"emergency reload: tactical {gun.LastReloadTactical}, mag 0 -> {gun.W.mag}, dropped mags on the ground {GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length > 0 && GameObject.Find("Dropped mag") != null}");
        // leaning out right, then left
        player.leanInput = 1f; yield return new WaitForSeconds(0.5f);
        yield return Shot("fp_lean_right.png");
        var camT = Camera.main.transform; log.WriteLine($"lean right: head {Vector3.Dot(camT.position - player.transform.position, player.transform.right):F2} m to the side, roll {camT.eulerAngles.z:F0} deg");
        player.leanInput = -1f; yield return new WaitForSeconds(0.6f);
        yield return Shot("fp_lean_left.png");
        log.WriteLine($"lean left: shoulder side {gun.View.ShoulderSide:F2} (-1 = rifle in the left shoulder)");
        player.leanInput = 0; yield return new WaitForSeconds(0.5f);
        log.WriteLine($"lean released: shoulder side {gun.View.ShoulderSide:F2}");
        // support-side shoulder held with X: ready, aimed, a burst and a reload from that side
        gun.supportShoulder = true; yield return new WaitForSeconds(0.16f);
        yield return Shot("fp_shoulder_switch.png");            // half way across
        yield return new WaitForSeconds(0.45f);
        yield return Shot("fp_left_ready.png");
        log.WriteLine("left shoulder: " + gun.View.ArmsDebug());
        gun.aimHeld = true; yield return new WaitForSeconds(0.5f);
        LookAt(near.transform.position + Vector3.up * 0.55f); yield return null;
        yield return Shot("fp_left_ads.png");
        int hitsL = gun.hits; gun.fireHeld = true; yield return new WaitForSeconds(0.3f); gun.fireHeld = false; gun.aimHeld = false;
        log.WriteLine($"left shoulder burst at {Vector3.Distance(near.transform.position, player.transform.position):F0} m: {gun.hits - hitsL} hits, shoulder side {gun.View.ShoulderSide:F2}");
        yield return new WaitForSeconds(0.5f);
        gun.reloadPressed = true; yield return new WaitForSeconds(gun.ReloadLen(true) * 0.36f);
        yield return Shot("fp_left_reload.png");
        yield return new WaitForSeconds(gun.ReloadLen(true) * 0.75f);
        // and with the magazine empty: from the left shoulder the right hand changes the magazine and then
        // racks the handle itself, straight away - the rifle must not be ready before the bolt has been drawn
        gun.W.mag = 0; gun.reloadPressed = true;
        bool upShot = false; float l0 = Time.time, llen = gun.ReloadLen(false), drawn = 0; int roundsAtRack = -1;
        while (Time.time - l0 < llen * 1.1f)
        {
            float u = (Time.time - l0) / llen, bb = gun.View.BoltBack(0);
            if (bb > drawn) { drawn = bb; roundsAtRack = gun.W.mag; }
            if (u > 0.3f && !upShot) { upShot = true; yield return Shot("fp_left_reload_up.png"); }
            if (u > ViewModel.ReloadRackAt - 0.03f && u < ViewModel.ReloadRackAt && !rackShot) { rackShot = true; yield return Shot("fp_left_rack.png"); yield return Inspect("inspect_left_rack.png", new Vector3(0.5f, -0.02f, 0.34f), new Vector3(-0.07f, -0.13f, 0.4f)); }
            yield return null;
        }
        log.WriteLine($"left shoulder, empty reload: charging handle drawn {drawn * 1000:F0} mm by the working (right) hand, magazine read {roundsAtRack} at that moment, {gun.W.mag} after; shoulder side {gun.View.ShoulderSide:F2}");
        gun.supportShoulder = false; yield return new WaitForSeconds(0.6f);
        // kneeling and prone
        player.crouch = true; yield return new WaitForSeconds(0.7f);
        yield return Shot("fp_kneel.png");
        player.crouch = false; player.prone = true; yield return new WaitForSeconds(1.2f);
        LookAt(near.transform.position + Vector3.up * 0.55f); yield return null;
        yield return Shot("fp_prone.png");
        gun.aimHeld = true; yield return new WaitForSeconds(0.5f);
        LookAt(near.transform.position + Vector3.up * 0.55f); yield return null;
        yield return Shot("fp_prone_ads.png");
        int hitsP = gun.hits; gun.fireHeld = true; yield return new WaitForSeconds(0.3f); gun.fireHeld = false; gun.aimHeld = false;
        log.WriteLine($"prone burst: {gun.hits - hitsP} hits, eye {Camera.main.transform.position.y - player.transform.position.y:F2} m above the ground");
        player.prone = false; yield return new WaitForSeconds(1.0f);
        // sprint across open ground: rifle up in the firing hand, the other hand off it
        var keepPos = player.transform.position; cam.yaw = startYaw + 180f; cam.pitch = 4f;
        player.moveInput = Vector2.up; player.sprint = true;
        yield return new WaitForSeconds(1.3f);
        yield return Shot("fp_sprint_carry.png");
        yield return Inspect("inspect_sprint_carry.png", new Vector3(0.55f, 0.05f, 0.5f), new Vector3(0.04f, -0.12f, 0.2f));
        log.WriteLine($"sprint carry: speed {player.Speed:F1} m/s, sprinting {player.Sprinting}");
        player.moveInput = Vector2.zero; player.sprint = false;
        yield return new WaitForSeconds(0.5f);
        var ccS = player.GetComponent<CharacterController>(); ccS.enabled = false; player.transform.position = keepPos; ccS.enabled = true;
        LookAt(near.transform.position + Vector3.up * 0.55f);
        yield return new WaitForSeconds(0.4f);
        // low ready after a quiet moment
        yield return new WaitForSeconds(3.2f);
        yield return Shot("fp_low_ready.png");
        // sniper: farthest target, scope in, one shot, bolt cycle
        var far = targets[targets.Length - 1];
        gun.switchTo = 1;
        yield return new WaitForSeconds(0.6f);
        LookAt(far.transform.position + Vector3.up * 0.55f);
        yield return null;
        yield return Shot("fp_sniper_hip.png");
        gun.aimHeld = true;
        yield return new WaitForSeconds(0.9f);
        LookAt(far.transform.position + Vector3.up * 0.6f);
        yield return null;
        yield return Shot("fp_scope.png");
        before = gun.hits;
        gun.fireHeld = true; yield return null; yield return null;
        gun.aimHeld = false;
        yield return new WaitForSeconds(0.55f);
        yield return Shot("fp_sniper_bolt.png");
        log.WriteLine($"sniper at {Vector3.Distance(far.transform.position, player.transform.position):F0} m: {gun.hits - before} hits");
        gun.fireHeld = false;
        yield return new WaitForSeconds(0.8f);
        // knife: contact distance only. First from two metres (must not reach), then in close: each attack
        // in turn, both grips, with a side view of the hand at the moment of contact
        var cc = player.GetComponent<CharacterController>();
        var tp = near.transform.position; var toMe = Vector3.ProjectOnPlane(player.transform.position - tp, Vector3.up).normalized;
        void StandAt(float d) { cc.enabled = false; player.transform.position = new Vector3(tp.x, player.transform.position.y, tp.z) + toMe * d; cc.enabled = true; }
        StandAt(2.0f);
        gun.switchTo = 2;
        yield return new WaitForSeconds(0.7f);
        LookAt(near.transform.position + Vector3.up * 0.55f);
        yield return null;
        yield return Shot("fp_knife.png");
        yield return Inspect("inspect_knife_grip.png", KnifeFrom, KnifeAt);
        before = gun.knifeHits; gun.forceKnife = ViewModel.CutAcross; gun.fireHeld = true;
        yield return new WaitForSeconds(0.8f);
        log.WriteLine($"knife from 2.0 m: {gun.knifeHits - before} hits (must be 0: a knife needs contact distance)");
        StandAt(0.9f);
        yield return new WaitForSeconds(0.3f);
        var names = new[] { "cut across", "cut down", "thrust", "stab down (reverse grip)", "rip back (reverse grip)" };
        var files = new[] { "fp_knife_slash.png", "fp_knife_cutdown.png", "fp_knife_stab.png", "fp_knife_stabdown.png", "fp_knife_rip.png" };
        int landed = 0;
        for (int kind = 0; kind < 5; kind++)
        {
            LookAt(near.transform.position + Vector3.up * 0.55f);
            before = gun.knifeHits; gun.forceKnife = kind;
            if (ViewModel.Heavy(kind)) { gun.aimHeld = true; yield return null; gun.aimHeld = false; } else gun.fireHeld = true;
            float len = ViewModel.KnifeTime(kind, 1f);
            yield return new WaitForSeconds(len * 0.3f);
            if (kind >= ViewModel.StabDown) yield return Inspect($"inspect_knife_reverse_{kind}.png", KnifeFrom + new Vector3(0, 0.1f, 0), KnifeAt + new Vector3(-0.03f, 0.12f, -0.04f));
            yield return new WaitForSeconds(len * 0.16f);
            yield return Shot(files[kind]);
            yield return new WaitForSeconds(len * 0.6f);
            int got = gun.knifeHits - before; landed += got > 0 ? 1 : 0;
            log.WriteLine($"knife {names[kind]}: {got} hits (attack played: {gun.View.LastKnife})");
            if (kind == 0) log.WriteLine($"knife slash: {got} hits");
            if (kind == 2) log.WriteLine($"knife stab: {got} hits");
            yield return new WaitForSeconds(3.3f);             // let the target stand up again
        }
        log.WriteLine($"knife: {landed} of 5 attacks landed at 0.9 m");
        // the quick combinations as the player gets them: six presses in a row, then a pause and three more
        string seq = ""; int comboHits = 0;
        for (int i = 0; i < 9; i++)
        {
            if (i == 6) yield return new WaitForSeconds(1.3f);
            LookAt(near.transform.position + Vector3.up * 0.55f);
            before = gun.knifeHits; gun.fireHeld = true;
            yield return new WaitForSeconds(0.12f);
            float tl = ViewModel.KnifeTime(gun.View.LastKnife, gun.LastKnifeSize);
            if (i == 0) { yield return new WaitForSeconds(tl * 0.3f); yield return Inspect("inspect_knife_cut.png", KnifeFrom, KnifeAt); }
            yield return new WaitForSeconds(tl + 0.02f);
            seq += $"{(gun.View.LastKnife == ViewModel.Thrust ? "thrust" : "cut")}{(gun.LastKnifeSide > 0 ? "+" : "-")}{(gun.LastKnifeSize < 0.8f ? "s" : "L")} "; comboHits += gun.knifeHits - before;
        }
        log.WriteLine($"knife combinations (+ forehand, - backhand, L large, s short): {seq}; hits {comboHits}");
        gun.switchTo = 0;
        yield return new WaitForSeconds(0.6f);
        log.Flush();
    }

    // a parked car that is not burnt out: rounds into a door must leave holes on the bodywork, rounds into
    // a window must break the glass
    IEnumerator Cars()
    {
        var gun = player.GetComponent<WeaponController>(); var cc = player.GetComponent<CharacterController>();
        Transform holder = null; foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (t.name == "Cars" && t.childCount > 0) { holder = t; break; }
        if (!holder || !gun) { log.WriteLine("cars: none in the scene"); yield break; }
        Collider best = null; Vector3 stand = Vector3.zero; float bd = 1e9f;
        foreach (Transform car in holder)
        {
            if (car.name.Contains("burnt")) continue;
            var body = car.Find("Body collider"); var col = body ? body.GetComponent<Collider>() : null; if (!col) continue;
            foreach (float side in new[] { 1f, -1f })
            {
                var from = col.bounds.center + car.right * side * 5.5f;
                if (!Physics.Raycast(from + Vector3.up * 20, Vector3.down, out var g, 30f) || g.point.y < -0.1f || g.point.y > 0.6f) continue;
                if (Physics.Linecast(g.point + Vector3.up * 1.5f, col.bounds.center, out var blk) && blk.collider != col) continue;      // something in the way
                float d = Vector3.Distance(g.point, player.transform.position);
                if (d < bd) { bd = d; best = col; stand = g.point; }
            }
        }
        if (!best) { log.WriteLine($"cars: {holder.childCount} cars, none with a body collider and a clear side to stand at"); yield break; }
        cc.enabled = false; player.transform.position = stand + Vector3.up * 0.1f; cc.enabled = true;
        player.moveInput = Vector2.zero; gun.switchTo = 0; gun.Get(0).mag = gun.Get(0).magSize;       // a full magazine for the two bursts
        var b = best.bounds; Vector3 door = new(b.center.x, b.min.y + b.size.y * 0.42f, b.center.z), pane = new(b.center.x, b.min.y + b.size.y * 0.8f, b.center.z);
        yield return new WaitForSeconds(0.6f);
        gun.aimHeld = true; LookAt(door); yield return new WaitForSeconds(0.6f); LookAt(door); yield return null;
        int h0 = CarDamage.BodyHoles, g0 = CarDamage.GlassHits;
        gun.fireHeld = true; yield return new WaitForSeconds(0.32f); gun.fireHeld = false; gun.aimHeld = false;
        yield return new WaitForSeconds(0.5f);
        yield return Shot("car_holes.png");
        log.WriteLine($"car body ({best.transform.parent.name}): {CarDamage.BodyHoles - h0} holes, {CarDamage.GlassHits - g0} glass hits from rounds aimed at the door");
        gun.aimHeld = true; LookAt(pane); yield return new WaitForSeconds(0.6f); LookAt(pane); yield return null;
        h0 = CarDamage.BodyHoles; g0 = CarDamage.GlassHits;
        gun.fireHeld = true; yield return new WaitForSeconds(0.12f);
        yield return Shot("car_glass_breaking.png");
        yield return new WaitForSeconds(0.2f); gun.fireHeld = false; gun.aimHeld = false;
        yield return new WaitForSeconds(0.6f);
        yield return Shot("car_glass.png");
        log.WriteLine($"car glass: {CarDamage.GlassHits - g0} hits on glass, {CarDamage.PanesBroken} pane(s) broken out, {CarDamage.BodyHoles - h0} holes from rounds aimed at the window");
        // a tyre: the car must drop on that corner
        var bt = best.transform; var lb = ((MeshCollider)best).sharedMesh.bounds; var pl = bt.InverseTransformPoint(player.transform.position);
        float sx = pl.x > lb.center.x ? 1 : -1;
        var wheel = bt.TransformPoint(new Vector3(lb.center.x + sx * lb.size.x * 0.47f, lb.min.y + lb.size.y * 0.17f, lb.center.z + lb.size.z * 0.5f * 0.62f));
        var carRoot = bt.parent; var rot0 = carRoot.rotation; int t0 = CarDamage.TyresBurst;
        gun.aimHeld = true; LookAt(wheel); yield return new WaitForSeconds(0.6f); LookAt(wheel); yield return null;
        gun.fireHeld = true; yield return new WaitForSeconds(0.3f); gun.fireHeld = false; gun.aimHeld = false;
        yield return new WaitForSeconds(0.9f);
        yield return Shot("car_tyre.png");
        log.WriteLine($"car tyre: {CarDamage.TyresBurst - t0} tyre(s) burst, the car tilted {Quaternion.Angle(rot0, carRoot.rotation):F1} degrees");
        // and from close up
        var closer = stand + Vector3.ProjectOnPlane(b.center - stand, Vector3.up).normalized * 3.1f;
        cc.enabled = false; player.transform.position = closer + Vector3.up * 0.1f; cc.enabled = true;
        yield return new WaitForSeconds(0.4f);
        LookAt(Vector3.Lerp(door, pane, 0.55f)); yield return new WaitForSeconds(0.5f);
        yield return Shot("car_damage_close.png");
        // an enemy on the far side of the car fires at the player through it: his rounds must mark it too
        var spawner = FindFirstObjectByType<EnemySpawner>(); var hp = player.GetComponent<PlayerHealth>();
        if (spawner && hp)
        {
            hp.invulnerable = true;
            foreach (var o in EnemyAI.All.ToArray()) if (o) Destroy(o.gameObject);
            yield return null;
            var far = b.center + Vector3.ProjectOnPlane(b.center - stand, Vector3.up).normalized * 7f;
            if (Physics.Raycast(far + Vector3.up * 20, Vector3.down, out var fg, 30f)) far = fg.point;
            cc.enabled = false; player.transform.position = stand + Vector3.up * 0.1f; cc.enabled = true;
            player.crouch = true;                              // down behind the car, so his rounds meet the car first
            var foe = Instantiate(spawner.enemyPrefab, far + Vector3.up * 0.05f, Quaternion.LookRotation(Vector3.ProjectOnPlane(stand - far, Vector3.up))).GetComponent<EnemyAI>();
            yield return null;
            foe.Alert(player.transform.position);
            int eh = CarDamage.BodyHoles, eg = CarDamage.GlassHits, ep = CarDamage.PanesBroken, es = EnemyAI.ShotsFired;
            yield return new WaitForSeconds(7f);
            log.WriteLine($"enemy fire on the car: {EnemyAI.ShotsFired - es} rounds fired, {CarDamage.BodyHoles - eh} holes in the body, {CarDamage.GlassHits - eg} hits on glass, {CarDamage.PanesBroken - ep} pane(s) broken out");
            player.crouch = false; if (foe) Destroy(foe.gameObject);
            cc.enabled = false; player.transform.position = closer + Vector3.up * 0.1f; cc.enabled = true;
            yield return new WaitForSeconds(0.4f);
            LookAt(Vector3.Lerp(door, pane, 0.55f)); yield return new WaitForSeconds(0.5f);
            yield return Shot("car_after_enemy_fire.png");
        }
        log.Flush();
    }

    // a window of a building: found by looking round for a point the glass mask calls glass, then shot
    IEnumerator BuildingGlass()
    {
        var gun = player.GetComponent<WeaponController>(); if (!gun) yield break;
        var eye = player.transform.position + Vector3.up * 1.62f; bool found = false; Vector3 at = Vector3.zero; int probes = 0, walls = 0; string why = "";
        for (float pitch = 2; pitch <= 26 && !found; pitch += 3)
            for (float yaw = 0; yaw < 360 && !found; yaw += 4)
            {
                var d = Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward;
                if (!Physics.Raycast(eye, d, out var h, 70f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || h.distance < 8f) continue;
                probes++;
                if (CarDamage.BuildingGlassAt(h)) { found = true; at = h.point; }
                else { if (CarDamage.LastBuildingProbe == "wall") walls++; else if (CarDamage.LastBuildingProbe != "not a facade" && CarDamage.LastBuildingProbe != "not a mesh collider" && CarDamage.LastBuildingProbe != "no renderer") why = CarDamage.LastBuildingProbe; }
            }
        if (!found) { log.WriteLine($"building glass: none found in {probes} looks ({walls} facade walls){(why != "" ? ", " + why : "")}"); yield break; }
        gun.switchTo = 0; gun.Get(0).mag = gun.Get(0).magSize;
        int g0 = CarDamage.BuildingGlassHits, p0 = CarDamage.PanesBroken;
        gun.aimHeld = true; LookAt(at); yield return new WaitForSeconds(0.7f); LookAt(at); yield return null;
        gun.fireHeld = true; yield return new WaitForSeconds(0.14f);
        yield return Shot("building_glass_breaking.png");
        yield return new WaitForSeconds(0.45f); gun.fireHeld = false;
        yield return new WaitForSeconds(0.7f);
        yield return Shot("building_glass.png");
        gun.aimHeld = false;
        log.WriteLine($"building glass at {Vector3.Distance(eye, at):F0} m: {CarDamage.BuildingGlassHits - g0} hits on window glass, {CarDamage.PanesBroken - p0} pane(s) broken out");
        log.Flush();
    }

    // being hit, badly hurt, and dying: what the player sees
    IEnumerator Wounds()
    {
        var hp = player.GetComponent<PlayerHealth>(); if (!hp) yield break;
        hp.invulnerable = false; int h0 = HitFeedback.PlayerHits;
        var from = player.transform.position + player.transform.forward * 10f + player.transform.right * 6f;
        hp.Damage(22f, from); yield return new WaitForSeconds(0.25f);
        yield return Shot("hurt_hit.png");
        hp.Damage(22f, from); yield return new WaitForSeconds(0.2f); hp.Damage(26f, from);
        yield return new WaitForSeconds(0.9f);
        yield return Shot("hurt_low_health.png");
        log.WriteLine($"player hit: {HitFeedback.PlayerHits - h0} hits felt, health {hp.Health:F0}");
        hp.Damage(200f, from);
        yield return new WaitForSeconds(0.7f);
        yield return Shot("death_falling.png");
        var gunD = player.GetComponent<WeaponController>();
        yield return new WaitForSeconds(0.25f);
        yield return Shot("death_support_hand_gone.png");            // about 1 s in: the support hand has dropped, the rifle sags in one hand
        log.WriteLine($"player death, {gunD.View.DeadT:F1} s in: support hand off, firing hand still on");
        yield return new WaitForSeconds(0.75f);
        yield return Shot("death_rifle_released.png");               // about 1.7 s in: the firing hand has let go
        log.WriteLine($"player death: dead {hp.Dead}, view {HitFeedback.DeathView:F2} of the way down, eye {Camera.main.transform.position.y - player.transform.position.y:F2} m above the ground, roll {Camera.main.transform.eulerAngles.z:F0} deg");
        yield return new WaitForSeconds(1.6f);
        yield return Shot("death_fade.png");
        yield return new WaitForSeconds(2.6f);
        log.WriteLine($"player respawned: dead {hp.Dead}, health {hp.Health:F0}, view back up {HitFeedback.DeathView:F2}");
        hp.invulnerable = true;
        log.Flush();
    }

    // walk-up views of the landmarks, taken in the real game for presenting the prototype
    IEnumerator Showcase()
    {
        var palace = GameObject.Find("palace");
        if (!palace) yield break;
        var b = new Bounds(palace.transform.position, Vector3.zero);
        foreach (var r in palace.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        var cc = player.GetComponent<CharacterController>();
        void Teleport(Vector3 p) { cc.enabled = false; player.transform.position = p; cc.enabled = true; }
        var front = palace.transform.forward; var side = palace.transform.right;
        Teleport(new Vector3(b.center.x, 0.2f, b.center.z) + front * 24f + side * 10f);
        player.moveInput = Vector2.zero;
        yield return new WaitForSeconds(0.3f);
        LookAt(b.center + Vector3.up * 1.5f - side * 4f);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("showcase_palace.png");
        Teleport(new Vector3(b.center.x, 0.2f, b.center.z) + front * 6.5f - side * 14f);
        yield return new WaitForSeconds(0.3f);
        LookAt(b.center + front * 6.5f + side * 6f + Vector3.up * 1.4f);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("showcase_arcade.png");
        // the roundabout and the lighthouse from the Corniche side
        var rb = GameObject.Find("roundabout");
        if (rb)
        {
            Teleport(rb.transform.position + (rb.transform.position - b.center).normalized * -6f + Vector3.Cross(Vector3.up, (rb.transform.position - b.center).normalized) * 26f + Vector3.up * 0.2f);
            yield return new WaitForSeconds(0.3f);
            LookAt(rb.transform.position + Vector3.up * 3f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot("showcase_roundabout.png");
        }
        // cars: fine, shot up, burnt out - seen from the middle of the road
        foreach (var s in new[] { 0, 1, 2, 10, 11, 12 })
        {
            var mk = GameObject.Find("CarMarker" + s); if (!mk) continue;
            var look = mk.transform.position; var from = look - mk.transform.forward * 6.5f + Vector3.Cross(Vector3.up, mk.transform.forward) * 3f;
            if (!Physics.Raycast(from + Vector3.up * 30, Vector3.down, out var g, 40) || g.point.y < -0.1f || g.point.y > 0.6f) continue;   // stand on the street
            Teleport(g.point + Vector3.up * 0.1f);
            yield return new WaitForSeconds(0.3f);
            LookAt(look + Vector3.up * 0.6f);
            yield return new WaitForSeconds(0.5f);
            yield return Shot($"showcase_car{s}.png");
        }
        // the lighthouse at its real spot, seen from the street
        var lh = FindLighthouseInScene();
        if (lh != Vector3.zero)
        {
            var away = Vector3.ProjectOnPlane(player.transform.position - lh, Vector3.up).normalized;
            for (int tries = 0; tries < 8; tries++)
            {
                var spot = lh + Quaternion.Euler(0, tries * 45f, 0) * away * 32f;
                if (Physics.Raycast(spot + Vector3.up * 60, Vector3.down, out var hit, 80f) && hit.point.y < 1f && hit.point.y > -0.1f
                    && (!Physics.Linecast(hit.point + Vector3.up * 1.6f, lh + Vector3.up * 12f, out var block) || Vector2.Distance(new Vector2(block.point.x, block.point.z), new Vector2(lh.x, lh.z)) < 9f))
                { Teleport(hit.point + Vector3.up * 0.1f); break; }
            }
            yield return new WaitForSeconds(0.3f);
            LookAt(lh + Vector3.up * 9f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot("showcase_lighthouse.png");
            log.WriteLine($"lighthouse at {lh}, viewed from {player.transform.position}");
        }
        // the Tripo landmark ruins, each from the street in front of it
        int li = 0;
        foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!t.name.StartsWith("Landmark ")) continue;
            var rs = t.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) continue;
            var lb = rs[0].bounds; foreach (var r in rs) lb.Encapsulate(r.bounds);
            var fwd = t.forward;
            foreach (float back in new[] { 16f, 11f, 7f })
            {
                var spot = lb.center + fwd * (lb.extents.magnitude * 0.35f + back);
                if (Physics.Raycast(spot + Vector3.up * 60, Vector3.down, out var hit, 80f) && hit.point.y < 1f && hit.point.y > -0.1f) { Teleport(hit.point + Vector3.up * 0.1f); break; }
            }
            yield return new WaitForSeconds(0.3f);
            LookAt(lb.center + Vector3.up * lb.extents.y * 0.1f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot($"landmark_{li++}_{t.name.Replace(' ', '_')}.png");
            log.WriteLine($"{t.name}: {lb.size.x:F0} x {lb.size.y:F0} x {lb.size.z:F0} m, viewed from {player.transform.position}");
        }
    }

    static Vector3 FindLighthouseInScene()
    {
        var marker = GameObject.Find("LighthouseMarker");
        if (marker) return marker.transform.position;
        foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            if (t.name.ToLower().Contains("lighthouse") && t.GetComponentInChildren<Renderer>())
            {
                var b = new Bounds(t.position, Vector3.zero);
                foreach (var r in t.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                if (b.size.y > 10) return new Vector3(b.center.x, 0, b.center.z);
            }
        return Vector3.zero;
    }

    // an enemy 14 m in front: he should turn and fire; a rifle burst should drop him and count a kill
    IEnumerator Combat()
    {
        var gun = player.GetComponent<WeaponController>(); var hp = player.GetComponent<PlayerHealth>();
        var spawner = FindFirstObjectByType<EnemySpawner>();
        if (!gun || !hp || !spawner) { log.WriteLine("combat: no spawner / health"); yield break; }
        log.WriteLine($"enemies: {EnemyAI.All.Count} alive at start, {spawner.spots.Length} spawn spots");
        // open ground in front of the player
        Vector3 at = Vector3.zero; bool ok = false;
        for (int k = 0; k < 12 && !ok; k++)
        {
            var dir = Quaternion.Euler(0, k * 30f, 0) * player.transform.forward; var p = player.transform.position + dir * 14f;
            if (Physics.Raycast(p + Vector3.up * 20, Vector3.down, out var g, 30f) && g.point.y > -0.05f && g.point.y < 0.5f
                && !Physics.Linecast(player.transform.position + Vector3.up * 1.6f, g.point + Vector3.up * 1.3f)) { at = g.point; ok = true; }
        }
        if (!ok) { log.WriteLine("combat: no open spot"); yield break; }
        var e = Instantiate(spawner.enemyPrefab, at + Vector3.up * 0.05f, Quaternion.LookRotation(Vector3.ProjectOnPlane(player.transform.position - at, Vector3.up))).GetComponent<EnemyAI>();
        hp.invulnerable = false; float before = hp.Health;
        gun.externalInput = true; gun.switchTo = 0;
        yield return new WaitForSeconds(0.4f);
        LookAt(e.transform.position + Vector3.up * 1.2f);
        yield return new WaitForSeconds(2.2f);
        yield return Shot("enemy_firing.png");
        log.WriteLine($"enemy mode {e.Mode}, crouched {e.Crouched}, cover moves {e.CoverMoves}, path left {e.PathLeft:F1}");
        log.WriteLine($"enemy sees player {e.SeesPlayer}; player health {before:F0} -> {hp.Health:F0} after 2.6 s under fire");
        hp.invulnerable = true;
        int kills = hp.Kills;
        LookAt(e.transform.position + Vector3.up * 1.2f);
        gun.aimHeld = true; yield return new WaitForSeconds(0.35f);
        for (int i = 0; i < 10 && !e.IsDead; i++) { LookAt(e.transform.position + Vector3.up * (e.Crouched ? 0.9f : 1.2f)); gun.fireHeld = true; yield return new WaitForSeconds(0.25f); }
        gun.fireHeld = false; gun.aimHeld = false;
        log.WriteLine($"enemy after the burst: mode {e.Mode}, crouched {e.Crouched}, cover moves {e.CoverMoves}, dead {e.IsDead}");
        if (!e.IsDead) e.Hit(500, e.transform.position + Vector3.up, player.transform.forward);   // he got behind cover: finish him to test the kill record
        yield return new WaitForSeconds(1.0f);
        yield return Shot("enemy_down.png");
        log.WriteLine($"enemy dead {e.IsDead}; kills {kills} -> {hp.Kills}, best {hp.Best}");
        log.WriteLine($"enemy hit feedback: {HitFeedback.EnemyGrunts} grunts, {HitFeedback.BloodMarks} blood marks so far");
        // his rifle is on the ground now: walk over and take what was left in it
        yield return new WaitForSeconds(1.2f);
        var drops = WeaponPickups.Drops; var keep = player.transform.position;
        if (drops.Count > 0 && drops[0].gun)
        {
            var d0 = drops[0]; var cc0 = player.GetComponent<CharacterController>();
            var gp = d0.gun.position; var back = Vector3.ProjectOnPlane(keep - gp, Vector3.up).normalized;
            cc0.enabled = false; player.transform.position = new Vector3(gp.x, keep.y, gp.z) + back * 1.5f + Vector3.up * 0.05f; cc0.enabled = true;
            yield return new WaitForSeconds(0.4f);
            LookAt(d0.gun.position);
            yield return new WaitForSeconds(0.4f);
            yield return Shot("pickup_prompt.png");
            int before0 = gun.Get(0).reserve; int got = WeaponPickups.TakeNearest();
            int sniperBefore = gun.Get(1).reserve;
            log.WriteLine($"pickup: rifle on the ground {d0.gun.position.y:F2} m up, {d0.spareMags} mags + {d0.magRounds} rounds of {d0.calibre} in it; took {got}, reserve {before0} -> {gun.Get(0).reserve}, rifles left {drops.Count}");
            log.WriteLine($"pickup ammunition: {d0.calibre} went to weapon {WeaponPickups.LastWeapon} ({gun.Get(Mathf.Max(0, WeaponPickups.LastWeapon)).name}); sniper reserve unchanged {gun.Get(1).reserve == sniperBefore}; a 5.45x39 rifle would feed weapon {WeaponPickups.WeaponFor("5.45x39")} (-1 = none)");
            yield return new WaitForSeconds(0.3f);
            yield return Shot("pickup_taken.png");
            cc0.enabled = false; player.transform.position = keep; cc0.enabled = true;
            yield return new WaitForSeconds(0.3f);
        }
        else log.WriteLine($"pickup: no rifle dropped ({drops.Count})");
        log.Flush();
    }

    // an enemy out of sight, 35-70 m away by road: told where the player is, he must find a way round the
    // buildings, then take cover and fight from it
    IEnumerator EnemyMovement()
    {
        var hp = player.GetComponent<PlayerHealth>(); var spawner = FindFirstObjectByType<EnemySpawner>();
        if (!hp || !spawner) yield break;
        hp.invulnerable = true;
        foreach (var o in EnemyAI.All.ToArray()) if (o) Destroy(o.gameObject);                 // a clean street for the test
        yield return null;
        var eye = player.transform.position + Vector3.up * 1.6f;
        var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
        bool pOn = UnityEngine.AI.NavMesh.SamplePosition(player.transform.position, out var pn, 2f, UnityEngine.AI.NavMesh.AllAreas);
        log.WriteLine($"movement: navmesh {tri.indices.Length / 3} triangles at runtime; player on it {pOn} ({(pOn ? Vector3.Distance(pn.position, player.transform.position) : -1):F1} m)");
        int nRange = 0, nHidden = 0, nPath = 0, nRound = 0;
        EnemyAI e = null; float straight = 0, path = 0;
        foreach (var s in spawner.spots)
        {
            straight = Vector3.Distance(s, player.transform.position);
            if (straight < 30f || straight > 90f) continue; nRange++;
            if (!Physics.Linecast(eye, s + Vector3.up * 1.4f) || !Physics.Linecast(eye + Vector3.up * 3f, s + Vector3.up * 4f)) continue; nHidden++;   // hidden by a building, not a palm
            var np = new UnityEngine.AI.NavMeshPath();
            if (!UnityEngine.AI.NavMesh.CalculatePath(s, pOn ? pn.position : player.transform.position, UnityEngine.AI.NavMesh.AllAreas, np) || np.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) continue; nPath++;
            path = 0; for (int i = 1; i < np.corners.Length; i++) path += Vector3.Distance(np.corners[i - 1], np.corners[i]);
            nRound++;                                                       // want a way round, not a straight line
            e = Instantiate(spawner.enemyPrefab, s, Quaternion.identity).GetComponent<EnemyAI>(); break;
        }
        log.WriteLine($"  spots in range {nRange}, hidden {nHidden}, with a full path {nPath}, path goes round {nRound}");
        if (!e) { log.WriteLine("movement: no hidden spot with a path round"); yield break; }
        yield return null;
        e.Alert(player.transform.position);
        log.WriteLine($"movement: enemy {straight:F0} m away in a straight line, {path:F0} m by road");
        float t0 = Time.time; var start = e.transform.position; bool saw = false;
        while (Time.time - t0 < 25f && !e.IsDead)
        {
            if (e.SeesPlayer && !saw) { saw = true; log.WriteLine($"  sees the player after {Time.time - t0:F1} s, {Vector3.Distance(e.transform.position, player.transform.position):F0} m away (walked {Vector3.Distance(start, e.transform.position):F0} m)"); }
            if (saw && e.Mode == EnemyAI.State.Cover && e.Crouched) break;
            yield return null;
        }
        log.WriteLine($"  after {Time.time - t0:F1} s: mode {e.Mode}, crouched {e.Crouched}, cover moves {e.CoverMoves}, {Vector3.Distance(e.transform.position, player.transform.position):F0} m from the player");
        LookAt(e.transform.position + Vector3.up * 1.0f);
        yield return new WaitForSeconds(0.4f);
        yield return Shot("enemy_cover.png");
        // watch him peek and duck for a few seconds
        int peeks = 0; bool wasCrouched = e.Crouched; float t1 = Time.time;
        while (Time.time - t1 < 8f && !e.IsDead) { if (wasCrouched && !e.Crouched) peeks++; wasCrouched = e.Crouched; yield return null; }
        log.WriteLine($"  in 8 s: popped up {peeks} times, mode {e.Mode}, cover moves {e.CoverMoves}; all enemies so far fired {EnemyAI.ShotsFired} rounds, threw {EnemyAI.GrenadesThrown} grenades");
        Destroy(e.gameObject);
        log.Flush();
    }

    // three alerted enemies 14-24 m away: screenshots every 1.5 s of how they run, take cover, peek and shoot
    IEnumerator EnemyWatch()
    {
        var hp = player.GetComponent<PlayerHealth>(); var spawner = FindFirstObjectByType<EnemySpawner>();
        if (!hp || !spawner) yield break;
        hp.invulnerable = true;
        foreach (var o in EnemyAI.All.ToArray()) if (o) Destroy(o.gameObject);
        yield return null;
        var list = new System.Collections.Generic.List<EnemyAI>();
        var eye = player.transform.position + Vector3.up * 1.6f;
        for (int k = 0; k < 24 && list.Count < 3; k++)
        {
            var dir = Quaternion.Euler(0, k * 15f, 0) * player.transform.forward; var p = player.transform.position + dir * (14f + (k % 3) * 5f);
            if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var nh, 2f, UnityEngine.AI.NavMesh.AllAreas) || nh.position.y < -0.2f || nh.position.y > 0.6f) continue;
            if (Physics.Linecast(eye, nh.position + Vector3.up * 1.3f)) continue;
            bool near = false; foreach (var o in list) if (Vector3.Distance(o.transform.position, nh.position) < 5f) near = true; if (near) continue;
            var e = Instantiate(spawner.enemyPrefab, nh.position, Quaternion.LookRotation(Vector3.ProjectOnPlane(player.transform.position - nh.position, Vector3.up))).GetComponent<EnemyAI>();
            list.Add(e);
        }
        yield return null;
        foreach (var e in list) e.Alert(player.transform.position);
        log.WriteLine($"watch: {list.Count} enemies 14-24 m away");
        for (int i = 0; i < 6; i++)
        {
            yield return new WaitForSeconds(1.5f);
            EnemyAI look = null; float best = 1e9f;
            foreach (var e in list) if (e && !e.IsDead) { float d = Vector3.Distance(e.transform.position, player.transform.position); if (d < best) { best = d; look = e; } }
            if (look) LookAt(look.transform.position + Vector3.up * 1.0f);
            yield return null;
            yield return Shot($"watch_{i}.png");
            log.WriteLine($"  {i * 1.5f + 1.5f:F1} s: " + string.Join("; ", list.ConvertAll(e => e ? $"{e.Mode} crouched {e.Crouched} sees {e.SeesPlayer} {Vector3.Distance(e.transform.position, player.transform.position):F0} m" : "gone")));
        }
        log.WriteLine($"  rounds fired {EnemyAI.ShotsFired}, grenades {EnemyAI.GrenadesThrown}");
        foreach (var e in list) if (e) Destroy(e.gameObject);
        log.Flush();
    }

    // into the sea on his own feet: wade until the water is at his chest, then swim - rifle on his back,
    // head above water, no drowning - out to sea, back, and out onto the sand again
    IEnumerator Swim()
    {
        var hp = player.GetComponent<PlayerHealth>(); var gun = player.GetComponent<WeaponController>(); if (!hp || !gun) yield break;
        var cc = player.GetComponent<CharacterController>();
        // a spot on the dry sand, and the way to the water from it (the beach meshes the seafront generator made)
        Vector3 sand = Vector3.zero, toSea = Vector3.zero; bool ok = false;
        var dry = GameObject.Find("Beach dry"); var wet = GameObject.Find("Beach wet");
        if (dry && wet)
        {
            float sd = 1e9f;
            foreach (var v in dry.GetComponent<MeshFilter>().sharedMesh.vertices) { var w = dry.transform.TransformPoint(v); float d = Vector3.Distance(w, startPos); if (w.y > 0.15f && w.y < 0.55f && d < sd) { sd = d; sand = w; } }      // low on the beach, past the sea wall and its rail
            Vector3 edge = sand; float ed = 1e9f;
            foreach (var v in wet.GetComponent<MeshFilter>().sharedMesh.vertices) { var w = wet.transform.TransformPoint(v); float d = Vector3.Distance(w, sand); if (d < ed) { ed = d; edge = w; } }
            toSea = Vector3.ProjectOnPlane(edge - sand, Vector3.up).normalized; ok = sd < 1e8f && toSea != Vector3.zero;
            // make sure that way really gets deep
            if (ok) { var q = sand + toSea * (ed + 25f); ok = !Physics.Raycast(new Vector3(q.x, 8, q.z), Vector3.down, out var g2, 40f) || g2.point.y < -1.8f; if (!ok) log.WriteLine($"swim: 25 m past the water's edge the bottom is at {g2.point.y:F1}"); }
        }
        if (!ok) { log.WriteLine("swim: no beach found"); yield break; }
        hp.invulnerable = true;
        // stand clear of the sea wall the sand runs up to: a few metres down the beach, on whatever is underfoot there
        var down = sand + toSea * 5f;
        if (Physics.Raycast(new Vector3(down.x, 6, down.z), Vector3.down, out var gs, 12f) && gs.point.y > PlayerController.WaterLevel) sand = gs.point;
        log.WriteLine($"swim: starting on the sand at {sand}, the sea towards {toSea}");
        cc.enabled = false; player.transform.position = sand + Vector3.up * 0.1f; cc.enabled = true;
        cam.yaw = Mathf.Atan2(toSea.x, toSea.z) * Mathf.Rad2Deg; cam.pitch = 6f;
        gun.switchTo = 0; yield return new WaitForSeconds(0.6f);
        player.moveInput = Vector2.up; float t0 = Time.time, wadeDepth = 0, swamAt = -1; bool shotWade = false;
        while (Time.time - t0 < 30f && !player.Swimming)
        {
            wadeDepth = player.WaterDepth;
            if (!shotWade && wadeDepth > 0.7f) { shotWade = true; yield return Shot("swim_wading.png"); }
            yield return null;
        }
        swamAt = Time.time - t0;
        log.WriteLine($"swim: walked in for {swamAt:F1} s, water {wadeDepth:F2} m up him when he started to swim (swimming {player.Swimming})");
        yield return new WaitForSeconds(1.2f);
        var a0 = player.transform.position; float sw0 = Time.time;
        yield return new WaitForSeconds(2.5f);
        yield return Shot("swim_fp.png");
        float v1 = Vector3.Distance(a0, player.transform.position) / (Time.time - sw0);
        player.sprint = true; var a1 = player.transform.position; sw0 = Time.time;
        yield return new WaitForSeconds(3f);
        float v2 = Vector3.Distance(a1, player.transform.position) / (Time.time - sw0);
        yield return Shot("swim_fp_fast.png");
        float eyeAbove = Camera.main.transform.position.y - PlayerController.WaterLevel;
        log.WriteLine($"swim: {v1:F2} m/s, sprinting {v2:F2} m/s; eyes {eyeAbove:F2} m above the water, rifle slung {gun.RifleSlung}, breath {hp.Breath:F2}, health {hp.Health:F0}, dead {hp.Dead}");
        gun.fireHeld = true; int mag0 = gun.W.mag; yield return new WaitForSeconds(0.4f); gun.fireHeld = false;
        log.WriteLine($"swim: tried to fire in the water: rounds used {mag0 - gun.W.mag} (must be 0)");
        player.sprint = false;
        gun.SetView(false); yield return new WaitForSeconds(1.2f);
        yield return Shot("swim_tp.png");
        for (int i = 0; i < 4; i++) { yield return InspectBody($"swim_side_{i}.png", new Vector3(2.6f, 2.0f, 0.2f), new Vector3(0f, 1.25f, -0.2f)); yield return new WaitForSeconds(0.3f); }
        gun.SetView(true);
        // a long float: he must not drown
        player.moveInput = Vector2.zero; yield return new WaitForSeconds(9f);
        log.WriteLine($"swim: after 9 s treading water: breath {hp.Breath:F2}, health {hp.Health:F0}, dead {hp.Dead}");
        yield return Shot("swim_treading.png");
        // back to the beach
        cam.yaw += 180f; player.moveInput = Vector2.up; player.sprint = true; t0 = Time.time;
        while (Time.time - t0 < 40f && (player.Swimming || player.WaterDepth > 0.05f)) yield return null;
        player.sprint = false; yield return new WaitForSeconds(0.8f); player.moveInput = Vector2.zero;
        yield return new WaitForSeconds(0.6f);
        yield return Shot("swim_ashore.png");
        log.WriteLine($"swim: back on the sand after {Time.time - t0:F1} s: swimming {player.Swimming}, rifle slung {gun.RifleSlung}, water depth {player.WaterDepth:F2}, health {hp.Health:F0}");
        log.Flush();
    }

    // the seafront from where you start: the sea and beach, along the promenade, the palm median
    IEnumerator SeafrontShots()
    {
        var wet = GameObject.Find("Beach wet");
        if (wet)
        {
            var mf = wet.GetComponent<MeshFilter>(); Vector3 best = Vector3.zero; float bd = 1e9f;
            foreach (var v in mf.sharedMesh.vertices) { var w = wet.transform.TransformPoint(v); float d = Vector3.Distance(w, player.transform.position); if (d < bd) { bd = d; best = w; } }
            LookAt(best + Vector3.down * 0.5f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot("seafront_sea.png");
            var to = best - player.transform.position; to.y = 0;
            LookAt(player.transform.position + Quaternion.Euler(0, 70f, 0) * to.normalized * 40f + Vector3.up * 1.0f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot("seafront_along.png");
            LookAt(player.transform.position + Quaternion.Euler(0, -70f, 0) * to.normalized * 40f + Vector3.up * 1.0f);
            yield return new WaitForSeconds(0.6f);
            yield return Shot("seafront_along2.png");
            log.WriteLine($"seafront: beach water edge {bd:F0} m from the start");
            // down on the sand, looking along the shore
            var dry = GameObject.Find("Beach dry");
            if (dry)
            {
                Vector3 sand = Vector3.zero; float sd = 1e9f;
                foreach (var v in dry.GetComponent<MeshFilter>().sharedMesh.vertices) { var w = dry.transform.TransformPoint(v); float d = Vector3.Distance(w, best); if (d < sd && w.y > 0.15f) { sd = d; sand = w; } }
                var keep = player.transform.position; var cc = player.GetComponent<CharacterController>();
                cc.enabled = false; player.transform.position = sand + Vector3.up * 0.1f; cc.enabled = true;
                yield return new WaitForSeconds(0.4f);
                var along = Vector3.Cross(Vector3.up, (best - sand).normalized);
                LookAt(sand + along * 30f + (best - sand).normalized * 6f);
                yield return new WaitForSeconds(0.6f);
                yield return Shot("seafront_beach.png");
                cc.enabled = false; player.transform.position = keep; cc.enabled = true;
                yield return new WaitForSeconds(0.3f);
            }
        }
        MeshRenderer median = null; float md = 1e9f;
        foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (r.sharedMaterial && r.sharedMaterial.name.StartsWith("Seafront_MedianGrass")) { float d = Vector3.Distance(r.bounds.center, player.transform.position); if (d < md) { md = d; median = r; } }
        var crowns = GameObject.Find("Palms");
        if (crowns && crowns.transform.childCount > 0)
        {
            Transform near = null; float nd = 1e9f;
            foreach (Transform c in crowns.transform) { float d = Vector3.Distance(c.position, player.transform.position); if (d > 6 && d < nd) { nd = d; near = c; } }
            if (near) { LookAt(near.position - Vector3.up * 2f); yield return new WaitForSeconds(0.6f); yield return Shot("seafront_palm.png"); log.WriteLine($"palms {crowns.transform.childCount}, nearest {nd:F0} m"); }
        }
        if (median) log.WriteLine($"median nearest {md:F0} m");
        log.Flush();
    }

    IEnumerator Run()
    {
        yield return new WaitForSeconds(1.5f);
        var health = player.GetComponent<PlayerHealth>(); if (health) health.invulnerable = true;
        if (System.Environment.CommandLine.Contains("-swimonly"))
        {
            foreach (var en in EnemyAI.All.ToArray()) if (en) Destroy(en.gameObject);
            var sp1 = FindFirstObjectByType<EnemySpawner>(); if (sp1) sp1.enabled = false;
            var w1 = player.GetComponent<WeaponController>(); if (w1) w1.externalInput = true;
            yield return Swim();
            log.WriteLine("done"); log.Close();
            Application.Quit(); yield break;
        }
        if (System.Environment.CommandLine.Contains("-timeshots"))
        {
            // the same two views at each time of day
            foreach (var en in EnemyAI.All.ToArray()) if (en) Destroy(en.gameObject);
            var sp0 = FindFirstObjectByType<EnemySpawner>(); if (sp0) sp0.enabled = false;
            for (int i = 0; i < TimeOfDay.Presets.Length; i++)
            {
                TimeOfDay.Set(i, true);
                cam.yaw = startYaw; cam.pitch = 2f; yield return new WaitForSeconds(1.2f);
                yield return Shot($"time_{i}_{TimeOfDay.Presets[i].name.ToLowerInvariant()}.png");
                cam.yaw = startYaw + 150f; yield return new WaitForSeconds(0.8f);
                yield return Shot($"time_{i}_{TimeOfDay.Presets[i].name.ToLowerInvariant()}_b.png");
                log.WriteLine($"time of day {TimeOfDay.Name}: sun {RenderSettings.sun.intensity:F2} at {Vector3.Angle(-RenderSettings.sun.transform.forward, Vector3.ProjectOnPlane(-RenderSettings.sun.transform.forward, Vector3.up)):F0} deg up, sky light {RenderSettings.ambientIntensity:F2}, {1f / Time.smoothDeltaTime:F0} fps");
            }
            log.WriteLine("done"); log.Close();
            Application.Quit(); yield break;
        }
        if (System.Environment.CommandLine.Contains("-gaitonly"))
        {
            // just the legs: third person, the clips' own paces, then each way of moving with the foot-slip measure
            foreach (var en in EnemyAI.All.ToArray()) if (en) Destroy(en.gameObject);
            var sp = FindFirstObjectByType<EnemySpawner>(); if (sp) sp.enabled = false;
            var w0 = player.GetComponent<WeaponController>(); if (w0) w0.SetView(false);
            cam.pitch = 8f; yield return new WaitForSeconds(0.6f);
            yield return Calibrate();
            yield return Step("idle", Vector2.zero, 1f);
            yield return Step("walk", Vector2.up, 3f, walk: true);
            yield return Step("run", Vector2.up, 3f);
            yield return Step("sprint", Vector2.up, 2.5f, sprint: true);
            yield return Step("back", Vector2.down, 3f);
            yield return Step("strafe left", Vector2.left, 3f);
            yield return Step("strafe right", Vector2.right, 3f);
            yield return Step("walk back", Vector2.down, 3f, walk: true);
            yield return Step("crouch walk", Vector2.up, 3f, crouch: true);
            yield return Step("look aside", Vector2.zero, 0.4f, turn: -80f);
            log.WriteLine("done"); log.Close();
            Application.Quit(); yield break;
        }
        yield return SeafrontShots();
        yield return Shooting();
        yield return Combat();
        yield return EnemyMovement();
        yield return EnemyWatch();
        cam.yaw = startYaw; cam.pitch = 8f;                                  // face the way we started
        // from the knife target this heading runs into a median palm: sprint into it, then back off -
        // the player must come free again (he used to stay wedged in the trunk)
        yield return Step("bump walk", Vector2.up, 2f, walk: true);
        yield return Step("bump run", Vector2.up, 2f);
        yield return Step("bump sprint", Vector2.up, 2f, sprint: true);
        yield return Shot("bump_palm.png");
        yield return Step("bump back off", Vector2.down, 1.5f);
        yield return Step("bump strafe", Vector2.left, 1.5f);
        // the movement steps run from the start point, on open ground
        var pcc = player.GetComponent<CharacterController>();
        pcc.enabled = false; player.transform.position = startPos; pcc.enabled = true;
        player.moveInput = Vector2.zero;
        yield return new WaitForSeconds(0.3f);
        var wcv = player.GetComponent<WeaponController>(); if (wcv) wcv.SetView(false);
        yield return new WaitForSeconds(0.4f);
        yield return Shot("play_start.png");
        yield return Calibrate();
        yield return Step("idle", Vector2.zero, 1f);
        yield return Step("walk", Vector2.up, 2f, walk: true);
        yield return Shot("play_walk.png");
        log.WriteLine($"third person, ready: butt plate {PlayerBodyPose.LastButtGap * 100:F1} cm from the shoulder pocket");
        yield return InspectBody("tp_stance_front.png", new Vector3(0.7f, 1.5f, 2.3f), new Vector3(0.05f, 1.3f, 0f));
        yield return InspectBody("tp_stance_side.png", new Vector3(2.2f, 1.45f, 0.5f), new Vector3(0f, 1.3f, 0.3f));
        player.aiming = true; var wA = player.GetComponent<WeaponController>(); if (wA) wA.aimHeld = true;
        yield return new WaitForSeconds(0.5f);
        yield return InspectBody("tp_stance_aiming.png", new Vector3(1.6f, 1.6f, 1.6f), new Vector3(0.05f, 1.35f, 0.2f));
        if (wA) wA.aimHeld = false;
        yield return Step("run", Vector2.up, 2f);
        yield return Shot("play_run.png");
        yield return Step("sprint", Vector2.up, 2f, sprint: true);
        yield return Shot("fp_sprint.png");
        yield return InspectBody("tp_sprint_carry.png", new Vector3(2.0f, 1.5f, 1.8f), new Vector3(0f, 1.3f, 0.2f));
        log.WriteLine($"third person, sprint: rifle carried up, butt plate {PlayerBodyPose.LastButtGap * 100:F1} cm from the top of the shoulder");
        yield return Step("strafe left", Vector2.left, 1.5f);
        yield return Shot("play_strafe.png");
        yield return Step("back", Vector2.down, 1.5f);
        yield return Step("crouch walk", Vector2.up, 2f, crouch: true);
        yield return Shot("play_crouch.png");
        yield return Step("turn", Vector2.zero, 1f, turn: 90f);
        yield return Shot("play_turn.png");
        yield return Step("look aside", Vector2.zero, 0.4f, turn: -80f);
        yield return Shot("play_look_aside.png");                             // standing, looking 30 degrees off: the torso should turn, not the feet
        yield return Step("turn settle", Vector2.zero, 0.8f);
        player.jumpPressed = true;
        yield return Step("jump", Vector2.up, 0.35f);
        yield return Shot("play_jump.png");
        yield return Step("land", Vector2.zero, 1f);
        // third person once, to show the soldier with the new rifle
        var wc = player.GetComponent<WeaponController>();
        if (wc) { wc.SetView(false); yield return new WaitForSeconds(0.5f); yield return Shot("tp_view.png"); wc.SetView(true); yield return new WaitForSeconds(0.3f); }
        yield return Cars();
        yield return BuildingGlass();
        yield return Showcase();
        yield return Wounds();
        yield return Swim();
        log.WriteLine("done"); log.Close();
        Application.Quit();
    }
}
