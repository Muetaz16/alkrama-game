// Review shots of the skyline blocks (../unity_shots/skyline_view_*.png), from the city side.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SkylineShots.Run
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SkylineShots
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        ShaderUtil.allowAsyncCompilation = false;
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).First(c => c.CompareTag("MainCamera"));
        GameObject.Find("Player")?.SetActive(false);
        cam.fieldOfView = 55f; cam.aspect = 16f / 9f;
        int i = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include).Where(t => t.name.StartsWith("Skyline ")).Take(4))
        {
            var rs = t.GetComponentsInChildren<Renderer>(); var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            foreach (var h in Physics.RaycastAll(new Vector3(b.center.x + b.extents.x + 5, 300, b.center.z), Vector3.down, 600f).OrderBy(h => h.distance)) Debug.Log($"[skydbg] {t.name} bottom {b.min.y:F2}: ground beside it hit {h.collider.name} at y {h.point.y:F2}");
            Debug.Log($"[skydbg] {t.name} at {b.center} size {b.size}");
            var inward = -new Vector3(b.center.x, 0, b.center.z).normalized;
            cam.transform.position = b.center + inward * (b.extents.magnitude * 1.6f + 20f) + Vector3.up * 6f;
            cam.transform.LookAt(b.center);
            SceneBuilder.Shoot(cam, $"skyline_view_{i++}.png");
        }
        // and the horizon from the middle of the city, looking inland
        var player = new Vector3(116.7f, 1.7f, -112.6f);
        var lh = SceneBuilder.FindLighthouse();
        var sea = CityDetail.SeaDirection();
        cam.transform.position = player + Vector3.up * 18f; cam.transform.rotation = Quaternion.LookRotation(-sea + Vector3.down * 0.05f);
        SceneBuilder.Shoot(cam, "skyline_view_city.png");
    }
}
