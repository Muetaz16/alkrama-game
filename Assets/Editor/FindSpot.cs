// Work out where a screenshot was taken from (by where the lighthouse and the roundabout fountain
// appear in it), then find what is under a circled screen point -> map coordinates.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod FindSpot.Run
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FindSpot
{
    const float W = 1917f, H = 1020f;

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        var player = GameObject.Find("Player"); player.SetActive(false);
        var cam = Object.FindFirstObjectByType<Camera>();
        cam.fieldOfView = 65f; cam.aspect = W / H;

        var lh = SceneBuilder.FindLighthouse();
        var rb = GameObject.Find("roundabout");
        Vector3 fountain = rb ? rb.GetComponentInChildren<Renderer>().bounds.center : lh;
        if (rb) { var b = new Bounds(rb.transform.position, Vector3.zero); foreach (var r in rb.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds); fountain = new Vector3(b.center.x, 1.5f, b.center.z); }
        Debug.Log($"[spot] lighthouse {lh} fountain {fountain}");

        // where things are in the screenshot (pixels): lighthouse middle of tower, top of tower, fountain column
        var shotLh = new Vector2(130, 260); var shotLhTop = new Vector2(100, 70); var shotFountain = new Vector2(705, 470);
        var lhMid = lh + Vector3.up * 11f; var lhTop = lh + Vector3.up * 23f;

        float best = float.MaxValue; Vector3 bestPos = default; float bestYaw = 0, bestPitch = 0;
        for (float x = lh.x - 150; x <= lh.x + 150; x += 3)
        for (float z = lh.z - 150; z <= lh.z + 150; z += 3)
        {
            var feet = new Vector3(x, 0, z);
            if (Physics.CheckCapsule(feet + Vector3.up * 0.5f, feet + Vector3.up * 1.6f, 0.3f)) continue;
            for (float yaw = 0; yaw < 360; yaw += 3)
            for (float pitch = -4; pitch <= 16; pitch += 4)
            {
                var rot = Quaternion.Euler(pitch, yaw, 0);
                var pivot = feet + Quaternion.Euler(0, yaw, 0) * new Vector3(0.55f, 1.6f, 0f);
                cam.transform.SetPositionAndRotation(pivot - rot * Vector3.forward * 3.2f, rot);
                float e = Err(cam, lhMid, shotLh) + Err(cam, lhTop, shotLhTop) + Err(cam, fountain, shotFountain);
                if (e < best) { best = e; bestPos = feet; bestYaw = yaw; bestPitch = pitch; }
            }
        }
        var r2 = Quaternion.Euler(bestPitch, bestYaw, 0);
        var pv = bestPos + Quaternion.Euler(0, bestYaw, 0) * new Vector3(0.55f, 1.6f, 0f);
        cam.transform.SetPositionAndRotation(pv - r2 * Vector3.forward * 3.2f, r2);
        Debug.Log($"[spot] best fit error {best:F0}px at {bestPos:F1} yaw {bestYaw} pitch {bestPitch}");
        player.SetActive(true); player.transform.SetPositionAndRotation(bestPos, Quaternion.Euler(0, bestYaw, 0));
        SceneBuilder.Shoot(cam, "findspot_view.png");
        foreach (var (px, py) in new[] { (930, 460), (930, 490), (915, 470), (950, 450), (930, 440) })
        {
            var ray = cam.ViewportPointToRay(new Vector3(px / W, 1f - py / H, 0));
            foreach (var h in Physics.RaycastAll(ray, 2000f))
            {
                if (h.collider.transform.IsChildOf(player.transform)) continue;
                Debug.Log($"[spot] pixel {px},{py} -> unity {h.point:F1} map {(-h.point.x):F1},{h.point.z:F1} dist {h.distance:F0} hit {h.collider.name}");
                break;
            }
        }
    }

    static float Err(Camera cam, Vector3 world, Vector2 px)
    {
        var v = cam.WorldToViewportPoint(world);
        if (v.z <= 0) return 1e5f;
        return Vector2.Distance(new Vector2(v.x * W, (1 - v.y) * H), px);
    }
}
