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

    IEnumerator Run()
    {
        yield return new WaitForSeconds(1.5f);
        yield return Shot("play_start.png");
        yield return Step("idle", Vector2.zero, 1f);
        yield return Step("walk", Vector2.up, 2f, walk: true);
        yield return Shot("play_walk.png");
        yield return Step("run", Vector2.up, 2f);
        yield return Shot("play_run.png");
        yield return Step("sprint", Vector2.up, 2f, sprint: true);
        yield return Shot("play_sprint.png");
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
        log.WriteLine("done"); log.Close();
        Application.Quit();
    }
}
