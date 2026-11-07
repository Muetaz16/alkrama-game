// Weapons on the player + a practice range of steel targets along the Corniche (20-160 m)
// so shooting and sniping can be tried before the enemy soldiers come in.
using UnityEditor;
using UnityEngine;

public static class GameplaySetup
{
    public static void Run()
    {
        var player = GameObject.Find("Player");
        if (!player.GetComponent<WeaponController>()) player.AddComponent<WeaponController>();
        if (!player.GetComponent<MarkerTool>()) player.AddComponent<MarkerTool>();

        var oldMark = GameObject.Find("LighthouseMarker"); if (oldMark) Object.DestroyImmediate(oldMark);
        new GameObject("LighthouseMarker").transform.position = SceneBuilder.FindLighthouse();
        var old = GameObject.Find("Practice Targets"); if (old) Object.DestroyImmediate(old);
        var root = new GameObject("Practice Targets");
        var steel = new Material(Shader.Find("Universal Render Pipeline/Lit")); steel.SetColor("_BaseColor", new Color(0.85f, 0.32f, 0.12f)); steel.SetFloat("_Smoothness", 0.35f); steel.SetFloat("_Metallic", 0.4f);
        var post = new Material(Shader.Find("Universal Render Pipeline/Lit")); post.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.2f));
        AssetDatabase.CreateAsset(steel, "Assets/Benghazi/Materials/TargetSteel.mat");
        AssetDatabase.CreateAsset(post, "Assets/Benghazi/Materials/TargetPost.mat");

        var t = player.transform;
        // targets go along the way to the lighthouse (on land), not out over the sea
        var lh = SceneBuilder.FindLighthouse();
        var toLh = Vector3.ProjectOnPlane(lh - t.position, Vector3.up).normalized;
        var right = Vector3.Cross(Vector3.up, toLh);
        int placed = 0;
        var eye = t.position + Vector3.up * 1.6f;
        var spots = new System.Collections.Generic.List<Vector3>();
        // fan out from the soldier towards the lighthouse side; keep only spots he can actually see
        foreach (var dist in new[] { 20f, 30f, 45f, 60f, 80f, 100f, 130f, 160f })
        foreach (var turn in new[] { 0f, -18f, 18f, -35f, 35f, -55f, 55f })
        {
            var dir = Quaternion.Euler(0, turn, 0) * toLh;
            var pos = t.position + dir * dist;
            if (!Physics.Raycast(pos + Vector3.up * 40, Vector3.down, out var hit, 80f) || hit.point.y < -0.1f) continue;   // water / sea floor
            pos = hit.point;
            if (Physics.CheckBox(pos + Vector3.up * 1.1f, new Vector3(0.5f, 0.9f, 0.5f))) continue;
            if (Physics.Linecast(eye, pos + Vector3.up * 1.0f)) continue;                                   // something in the way
            if (spots.Exists(o => Vector3.Distance(o, pos) < 12f)) continue;
            spots.Add(pos);
            break;
        }
        foreach (var pos in spots)
        {
            float dist = Vector3.Distance(pos, t.position);
            var tg = new GameObject($"Target {dist:F0}m");
            tg.transform.SetParent(root.transform);
            tg.transform.position = pos;
            tg.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(t.position - pos, Vector3.up));
            var swing = new GameObject("Plate").transform; swing.SetParent(tg.transform, false); swing.localPosition = new Vector3(0, 0.35f, 0);
            Part(swing, PrimitiveType.Cube, new Vector3(0, 0.55f, 0), new Vector3(0.5f, 0.75f, 0.03f), steel);    // torso
            Part(swing, PrimitiveType.Sphere, new Vector3(0, 1.1f, 0), new Vector3(0.24f, 0.26f, 0.04f), steel);  // head
            Part(tg.transform, PrimitiveType.Cylinder, new Vector3(0, 0.2f, 0.04f), new Vector3(0.05f, 0.2f, 0.05f), post, false);
            var d = swing.gameObject.AddComponent<Damageable>(); d.health = 100;
            placed++;
        }
        Debug.Log($"[gameplay] weapons on player, {placed} practice targets");
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material m, bool keepCollider = true)
    {
        var p = GameObject.CreatePrimitive(type);
        if (!keepCollider) Object.DestroyImmediate(p.GetComponent<Collider>());
        p.transform.SetParent(parent, false); p.transform.localPosition = pos; p.transform.localScale = scale;
        p.GetComponent<Renderer>().sharedMaterial = m;
    }
}
