// Rays across the view of a cleared lot: what is the thing floating there? (log only)
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod FloatDebug.Run -- x z ax az
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class FloatDebug
{
    static string PathOf(Transform t) { var s = t.name; while (t.parent) { t = t.parent; s = t.name + "/" + s; } return s; }
    public static void Run()
    {
        var a = System.Environment.GetCommandLineArgs(); int i = System.Array.IndexOf(a, "--");
        float x = float.Parse(a[i + 1]), z = float.Parse(a[i + 2]), ax = float.Parse(a[i + 3]), az = float.Parse(a[i + 4]);
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity"); Physics.SyncTransforms();
        var away = new Vector3(ax, 0, az); var eye = new Vector3(x, 1.7f, z) - away * 25f; var at = new Vector3(x, 6f, z);
        var rot = Quaternion.LookRotation(at - eye);
        var seen = new System.Collections.Generic.Dictionary<string, int>();
        for (int u = -30; u <= 30; u += 2) for (int v = 0; v <= 30; v += 2)
        {
            var d = rot * Quaternion.Euler(-v, u, 0) * Vector3.forward;
            if (!Physics.Raycast(eye, d, out var h, 120f)) continue;
            if (h.point.y < 3.5f) continue;
            var r = h.collider.GetComponent<Renderer>();
            var k = PathOf(h.collider.transform) + " | " + (r && r.sharedMaterial ? r.sharedMaterial.name : "-") + $" | hit y {h.point.y:F0}";
            k = k.Substring(0, System.Math.Min(160, k.Length));
            seen[k] = seen.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        foreach (var kv in seen.OrderByDescending(k => k.Value)) Debug.Log($"[float] {kv.Value,3} {kv.Key}");
        // and everything with a renderer whose bounds sit in the air over the lot
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var b = r.bounds; if (b.size.magnitude > 40f || b.min.y < 3f) continue;
            if (new Vector2(b.center.x - x, b.center.z - z).magnitude > 20f) continue;
            Debug.Log($"[float-air] {PathOf(r.transform)} {(r.sharedMaterial ? r.sharedMaterial.name : "")} bounds {b.center} {b.size}");
        }
    }
}
