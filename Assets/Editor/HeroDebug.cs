// Where the landmark ruins ended up and what stands in front of them: for each one, a ray from the street spot the
// auto-test uses towards the landmark, logging every renderer it passes through (enabled or not).
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod HeroDebug.Run
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HeroDebug
{
    static string PathOf(Transform t) { var s = t.name; while (t.parent) { t = t.parent; s = t.name + "/" + s; } return s; }

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Physics.SyncTransforms();
        foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include).Where(t => t.name.StartsWith("Landmark ")).Select(t => t.gameObject))
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            var fwd = go.transform.forward;
            var mats = string.Join(",", rs.SelectMany(r => r.sharedMaterials).Select(m => m ? m.name + "/" + m.shader.name : "null"));
            Debug.Log($"[herodbg] {PathOf(go.transform)} active {go.activeInHierarchy} renderers {rs.Length} (enabled {rs.Count(r => r.enabled)}) mats {mats} colliders {go.GetComponentsInChildren<Collider>().Length} bounds {b.center} {b.size} fwd {fwd}");
            var eye = b.center + fwd * (b.extents.magnitude * 0.35f + 7f); eye.y = 1.7f;
            var to = b.center - eye;
            foreach (var h in Physics.RaycastAll(eye, to.normalized, to.magnitude + 5).OrderBy(h => h.distance))
            {
                var r = h.collider.GetComponent<Renderer>();
                Debug.Log($"[herodbg]    hit at {h.distance:F1} m: {PathOf(h.collider.transform)} renderer {(r ? (r.enabled ? "ON " + (r.sharedMaterial ? r.sharedMaterial.name : "") : "off") : "none")}");
            }
            // every enabled renderer of the web city whose bounds contain the landmark's front centre
            var front = b.center + fwd * b.extents.z * 0.8f;
            var city = GameObject.Find("Benghazi Corniche");
            foreach (var r in city.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.bounds.Contains(new Vector3(front.x, 3f, front.z))).Take(8))
                Debug.Log($"[herodbg]    web renderer still drawn over the front: {PathOf(r.transform)} mat {r.sharedMaterial?.name} bounds {r.bounds.center} {r.bounds.size}");
        }
    }
}
