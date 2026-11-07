using System.Collections;
using System.IO;
using UnityEngine;

// Scripted test run so the game can be checked without a person at the keyboard.
// Only active when the player is started with -autotest; it walks, runs, sprints, strafes, crouches
// and jumps, saves screenshots to ../unity_shots/ and a log of positions, then quits.
public class AutoTest : MonoBehaviour
{
    PlayerController player;
    ThirdPersonCamera cam;
    StreamWriter log;
    string outDir;

    void Start()
    {
        if (!System.Environment.CommandLine.Contains("-autotest")) { enabled = false; return; }
        player = FindFirstObjectByType<PlayerController>();
        cam = FindFirstObjectByType<ThirdPersonCamera>();
        player.externalInput = true; cam.externalInput = true;
        outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity_shots"));
        if (!Application.isEditor) outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../unity_shots"));
        Directory.CreateDirectory(outDir);
        log = new StreamWriter(Path.Combine(outDir, "autotest.log"));
        StartCoroutine(Run());
    }

    IEnumerator Step(string name, Vector2 move, float secs, bool sprint = false, bool walk = false, bool crouch = false, float turn = 0f)
    {
        player.moveInput = move; player.sprint = sprint; player.walk = walk; player.crouch = crouch;
        var start = player.transform.position;
        for (float t = 0; t < secs; t += Time.deltaTime)
        {
            cam.lookDelta = new Vector2(turn * Time.deltaTime, 0);
            yield return null;
        }
        var moved = Vector3.Distance(start, player.transform.position);
        var anim = player.GetComponentInChildren<Animator>();
        var clip = anim.GetCurrentAnimatorClipInfo(0);
        log.WriteLine($"{name}: moved {moved:F2} m in {secs}s ({moved / secs:F2} m/s), grounded {player.GetComponent<CharacterController>().isGrounded}, pos {player.transform.position}, clip {(clip.Length > 0 ? clip[0].clip.name : "-")}");
        log.Flush();
    }

    IEnumerator Shot(string file)
    {
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, file));
        yield return null;
    }

    // point the camera at a world position
    void LookAt(Vector3 p)
    {
        var pivot = cam.firstPerson ? player.transform.position + Vector3.up * cam.eyeHeight : player.transform.position + Quaternion.Euler(0, cam.yaw, 0) * cam.shoulder;
        var d = p - pivot;
        cam.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        cam.pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
    }

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
        LookAt(near.transform.position + Vector3.up * 0.95f);
        yield return new WaitForSeconds(0.3f);
        yield return Shot("fp_rifle_hip.png");
        gun.aimHeld = true;
        yield return new WaitForSeconds(0.6f);
        LookAt(near.transform.position + Vector3.up * 0.95f);
        yield return null;
        yield return Shot("fp_rifle_ads.png");
        int before = gun.hits;
        gun.fireHeld = true;
        yield return new WaitForSeconds(0.25f);
        yield return Shot("fp_rifle_fire.png");
        yield return new WaitForSeconds(0.35f);
        gun.fireHeld = false; gun.aimHeld = false;
        log.WriteLine($"rifle burst at {Vector3.Distance(near.transform.position, player.transform.position):F0} m: {gun.hits - before} hits, mag {gun.W.mag}");
        yield return new WaitForSeconds(0.4f);
        // reload: the magazine comes out and a new one goes in
        gun.reloadPressed = true;
        yield return new WaitForSeconds(0.62f);
        yield return Shot("fp_reload_magout.png");
        yield return new WaitForSeconds(0.5f);
        yield return Shot("fp_reload_magin.png");
        yield return new WaitForSeconds(1.4f);
        log.WriteLine($"reloaded: mag {gun.W.mag}");
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
        // knife: walk up to the near target and slash it
        var cc = player.GetComponent<CharacterController>();
        var tp = near.transform.position; var toMe = Vector3.ProjectOnPlane(player.transform.position - tp, Vector3.up).normalized;
        cc.enabled = false; player.transform.position = new Vector3(tp.x, player.transform.position.y, tp.z) + toMe * 1.4f; cc.enabled = true;
        gun.switchTo = 2;
        yield return new WaitForSeconds(0.6f);
        LookAt(near.transform.position + Vector3.up * 0.55f);
        yield return null;
        yield return Shot("fp_knife.png");
        before = gun.knifeHits;
        gun.fireHeld = true;
        yield return new WaitForSeconds(0.17f);
        yield return Shot("fp_knife_slash.png");
        yield return new WaitForSeconds(0.6f);
        log.WriteLine($"knife slash: {gun.knifeHits - before} hits");
        yield return new WaitForSeconds(3.2f);                 // let the target stand up again
        before = gun.knifeHits;
        LookAt(near.transform.position + Vector3.up * 0.55f);
        gun.aimHeld = true; yield return null; gun.aimHeld = false;
        yield return new WaitForSeconds(0.3f);
        yield return Shot("fp_knife_stab.png");
        yield return new WaitForSeconds(0.5f);
        log.WriteLine($"knife stab: {gun.knifeHits - before} hits");
        gun.switchTo = 0;
        yield return new WaitForSeconds(0.6f);
        gun.externalInput = false;
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

    IEnumerator Run()
    {
        yield return new WaitForSeconds(1.5f);
        yield return Shooting();
        yield return Shot("play_start.png");
        yield return Step("idle", Vector2.zero, 1f);
        yield return Step("walk", Vector2.up, 2f, walk: true);
        yield return Shot("play_walk.png");
        yield return Step("run", Vector2.up, 2f);
        yield return Shot("play_run.png");
        yield return Step("sprint", Vector2.up, 2f, sprint: true);
        yield return Shot("fp_sprint.png");
        yield return Step("strafe left", Vector2.left, 1.5f);
        yield return Shot("play_strafe.png");
        yield return Step("back", Vector2.down, 1.5f);
        yield return Step("crouch walk", Vector2.up, 2f, crouch: true);
        yield return Shot("play_crouch.png");
        yield return Step("turn", Vector2.zero, 1f, turn: 90f);
        player.jumpPressed = true;
        yield return Step("jump", Vector2.up, 0.35f);
        yield return Shot("play_jump.png");
        yield return Step("land", Vector2.zero, 1f);
        // third person once, to show the soldier with the new rifle
        var wc = player.GetComponent<WeaponController>();
        if (wc) { wc.SetView(false); yield return new WaitForSeconds(0.5f); yield return Shot("tp_view.png"); wc.SetView(true); }
        yield return Showcase();
        log.WriteLine("done"); log.Close();
        Application.Quit();
    }
}
