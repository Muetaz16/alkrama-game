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
        player.externalInput = true; cam.externalInput = true; startYaw = player.yaw; startPos = player.transform.position;
        outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity_shots"));
        if (!Application.isEditor) outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../unity_shots"));
        Directory.CreateDirectory(outDir);
        log = new StreamWriter(Path.Combine(outDir, "autotest.log"));
        log.WriteLine($"loaded after {Time.realtimeSinceStartup:F1} s, {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}"); log.Flush();
        StartCoroutine(Run());
    }

    IEnumerator Step(string name, Vector2 move, float secs, bool sprint = false, bool walk = false, bool crouch = false, float turn = 0f)
    {
        player.moveInput = move; player.sprint = sprint; player.walk = walk; player.crouch = crouch;
        var start = player.transform.position; int frames = 0; float t0 = Time.realtimeSinceStartup, worst = 0;
        for (float t = 0; t < secs; t += Time.deltaTime)
        {
            frames++; worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            cam.lookDelta = new Vector2(turn * Time.deltaTime, 0);
            yield return null;
        }
        var moved = Vector3.Distance(start, player.transform.position);
        var anim = player.GetComponentInChildren<Animator>();
        var clip = anim.GetCurrentAnimatorClipInfo(0);
        log.WriteLine($"{name}: {frames / Mathf.Max(0.01f, Time.realtimeSinceStartup - t0):F0} fps (worst frame {worst * 1000:F0} ms), moved {moved:F2} m in {secs}s ({moved / secs:F2} m/s), grounded {player.GetComponent<CharacterController>().isGrounded}, pos {player.transform.position}, clip {(clip.Length > 0 ? clip[0].clip.name : "-")}");
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
        LookAt(near.transform.position + Vector3.up * 0.55f);     // torso centre (0.95 is the gap under the head)
        yield return new WaitForSeconds(0.3f);
        yield return Shot("fp_rifle_hip.png");
        gun.aimHeld = true;
        yield return new WaitForSeconds(0.6f);
        LookAt(near.transform.position + Vector3.up * 0.55f);
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
        // tactical reload (rounds left): fresh mag strips the old one, old one kept, one stays chambered
        int before2 = gun.W.mag;
        gun.reloadPressed = true;
        yield return new WaitForSeconds(0.45f);
        yield return Shot("fp_reload_pouch.png");
        yield return new WaitForSeconds(0.42f);
        yield return Shot("fp_reload_magout.png");
        log.WriteLine("reload debug: " + gun.View.ReloadDebug());
        yield return new WaitForSeconds(0.42f);
        yield return Shot("fp_reload_magin.png");
        yield return new WaitForSeconds(1.4f);
        log.WriteLine($"tactical reload: tactical {gun.LastReloadTactical}, mag {before2} -> {gun.W.mag}, reserve {gun.W.reserve}");
        // emergency reload (empty): old mag drops, fresh one in, charging handle racked
        gun.W.mag = 0; gun.reloadPressed = true;
        yield return new WaitForSeconds(0.85f);
        yield return Shot("fp_reload_drop.png");
        yield return new WaitForSeconds(0.85f);
        yield return Shot("fp_reload_rack.png");
        yield return new WaitForSeconds(0.9f);
        log.WriteLine($"emergency reload: tactical {gun.LastReloadTactical}, mag 0 -> {gun.W.mag}, dropped mags on the ground {GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length > 0 && GameObject.Find("Dropped mag") != null}");
        // leaning out right, then left
        player.leanInput = 1f; yield return new WaitForSeconds(0.5f);
        yield return Shot("fp_lean_right.png");
        var camT = Camera.main.transform; log.WriteLine($"lean right: head {Vector3.Dot(camT.position - player.transform.position, player.transform.right):F2} m to the side, roll {camT.eulerAngles.z:F0} deg");
        player.leanInput = -1f; yield return new WaitForSeconds(0.6f);
        yield return Shot("fp_lean_left.png");
        player.leanInput = 0; yield return new WaitForSeconds(0.4f);
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
        gun.externalInput = false;
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

    // walk into the sea: the breath bar should run out and the player drown
    IEnumerator Drown()
    {
        var hp = player.GetComponent<PlayerHealth>(); if (!hp) yield break;
        var cc = player.GetComponent<CharacterController>();
        Vector3 sea = Vector3.zero; bool ok = false;
        for (float rad = 20; rad < 260 && !ok; rad += 15)
            for (int k = 0; k < 16 && !ok; k++)
            {
                var p = player.transform.position + Quaternion.Euler(0, k * 22.5f, 0) * Vector3.forward * rad;
                if (Physics.Raycast(new Vector3(p.x, 6, p.z), Vector3.down, out var g, 30f) && g.point.y < -2.5f && g.point.y > -8f) { sea = g.point; ok = true; }
            }
        if (!ok) { log.WriteLine("drown: no sea found"); yield break; }
        cc.enabled = false; player.transform.position = sea + Vector3.up * 0.1f; cc.enabled = true;
        player.moveInput = Vector2.zero;
        hp.invulnerable = true;                     // only the water can kill him here
        float t0 = Time.time;
        yield return new WaitForSeconds(4f);
        yield return Shot("drown_breath.png");
        log.WriteLine($"in the sea at depth {sea.y:F1}: in water {hp.InWater}, breath {hp.Breath:F2}, health {hp.Health:F0} after 4 s");
        while (!hp.Dead && Time.time - t0 < 25f) yield return null;
        yield return new WaitForSeconds(0.5f);
        yield return Shot("drown_dead.png");
        log.WriteLine($"drowned {hp.Dead} ({hp.LastDeath}) after {Time.time - t0:F1} s in the water");
        yield return new WaitForSeconds(4.5f);
        log.WriteLine($"respawned: dead {hp.Dead}, health {hp.Health:F0}, pos {player.transform.position}");
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
        yield return Drown();
        log.WriteLine("done"); log.Close();
        Application.Quit();
    }
}
