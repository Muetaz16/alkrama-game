using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Press M to mark the spot under the crosshair ("put the lighthouse here").
// Each mark is added to Desktop\tasks 3D\markers.txt with the map coordinates the map tool uses,
// and a flag pole is planted there so you can see it.
public class MarkerTool : MonoBehaviour
{
    Camera cam; string file; float shown; string msg = "";

    void Start()
    {
        cam = Camera.main;
        var root = Application.isEditor ? Path.Combine(Application.dataPath, "../..") : Path.Combine(Application.dataPath, "../../..");
        file = Path.GetFullPath(Path.Combine(root, "markers.txt"));
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k == null || !k.mKey.wasPressedThisFrame || !cam) return;
        var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
        foreach (var h in Physics.RaycastAll(ray, 2000f))
        {
            if (h.collider.transform.IsChildOf(transform)) continue;
            var p = h.point;
            // Unity x is the map's -x
            var line = $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}  map {(-p.x):F1},{p.z:F1}  unity {p.x:F1},{p.y:F1},{p.z:F1}  {Vector3.Distance(transform.position, p):F0} m away";
            File.AppendAllText(file, line + "\n");
            Flag(p);
            msg = $"Marked! {Vector3.Distance(transform.position, p):F0} m away  (saved to markers.txt)";
            shown = Time.time + 3f;
            return;
        }
    }

    static void Flag(Vector3 p)
    {
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(pole.GetComponent<Collider>());
        pole.transform.position = p + Vector3.up * 3f; pole.transform.localScale = new Vector3(0.12f, 3f, 0.12f);
        var flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(flag.GetComponent<Collider>());
        flag.transform.position = p + new Vector3(0.6f, 5.4f, 0); flag.transform.localScale = new Vector3(1.2f, 0.7f, 0.04f);
        var m = Rp.Lit("Marker", new Color(1f, 0.85f, 0f));
        pole.GetComponent<Renderer>().material = m; flag.GetComponent<Renderer>().material = m;
    }

    void OnGUI()
    {
        if (Time.time > shown) return;
        var st = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.035f), alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold };
        GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(new Rect(2, Screen.height * 0.12f + 2, Screen.width, 60), msg, st);
        GUI.color = new Color(1f, 0.85f, 0f); GUI.Label(new Rect(0, Screen.height * 0.12f, Screen.width, 60), msg, st);
    }
}
