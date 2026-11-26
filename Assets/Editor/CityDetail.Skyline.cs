// The city goes on past the edge of the map: a ring of big apartment blocks on the land side, from the
// (HDRP) NYC-like City Buildings Set (Marcin's Assets, converted to URP by NycSetup). Their red brick is
// re-dressed as Benghazi's dusty beige plaster with the same weathering as the kit, so they read as the
// 6-12 storey blocks further out from Sabri. Only the four smaller blocks are used (22-38k triangles each).
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class CityDetail
{
    static readonly string[] SkylineModels = { "building_1", "building_6", "building_7", "building_2" };
    const float SkylineFrom = 40f, SkylineTo = 150f;      // metres past the edge of the map

    static Mesh TiledBox(Vector3 size, float metres)
    {
        var src = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); var m = Object.Instantiate(src); m.name = "OutskirtsGround";
        var v = m.vertices; var n = m.normals; var uv = new Vector2[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var p = Vector3.Scale(v[i], size);
            uv[i] = Mathf.Abs(n[i].y) > 0.5f ? new Vector2(p.x, p.z) / metres : Mathf.Abs(n[i].x) > 0.5f ? new Vector2(p.z, p.y) / metres : new Vector2(p.x, p.y) / metres;
        }
        m.uv = uv;
        var path = $"{Root}/Detail/OutskirtsGround_{(int)size.x}x{(int)size.z}.asset"; AssetDatabase.DeleteAsset(path); AssetDatabase.CreateAsset(m, path);   // saved, or the scene loses it
        return m;
    }

    static void Skyline(Transform root)
    {
        var models = SkylineModels.Select(n => AssetDatabase.LoadAssetAtPath<GameObject>($"{NycSetup.Root}/Models/{n}.fbx")).Where(g => g).ToArray();
        if (models.Length == 0) { Debug.Log("[skyline] NYC building set not in the project - skipped"); return; }
        var map = (Dictionary<string, object>)MiniJson.Parse(System.IO.File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        float half = System.Convert.ToSingle(map["half"]);
        var sea = SeaDirection();

        // their brick and painted concrete become our plaster; trims a light concrete; the rest keeps its own look
        var plasters = new[] { new Color(0.86f, 0.76f, 0.62f), new Color(0.8f, 0.72f, 0.62f), new Color(0.9f, 0.84f, 0.74f), new Color(0.76f, 0.7f, 0.62f) }
            .Select((c, i) => Mat2("SkylinePlaster" + i, i % 2 == 0 ? "damaged_plaster" : "painted_plaster_wall", c, 0.04f, 0.25f)).ToArray();
        var trim = Mat2("SkylineTrim", "concrete_floor_worn_001", new Color(0.82f, 0.78f, 0.7f), 0.04f, 0.3f);
        foreach (var m in plasters.Append(trim)) FarVisible.Add(m.name);

        var holder = new GameObject("Skyline").transform; holder.SetParent(root, false);
        // past the map edge there is no ground: a dusty apron on every land side of the square map, the city's
        // outskirts, out to well beyond the blocks (the sea sides stay open)
        var groundMat = Mat2("OutskirtsGround", "coast_sand_02", new Color(0.92f, 0.84f, 0.72f), 0.02f, 1f);
        FarVisible.Add(groundMat.name);
        const float Apron = SkylineTo + 120f;
        int aprons = 0;
        foreach (var side in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
        {
            if (Vector3.Dot(side, sea) > -0.15f) continue;
            var across = Vector3.Cross(Vector3.up, side);
            var c = side * (half + Apron / 2);
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube); g.name = "Outskirts ground";
            g.transform.SetParent(holder, false);
            g.transform.SetPositionAndRotation(new Vector3(c.x, -0.16f, c.z), Quaternion.LookRotation(side));
            g.transform.localScale = new Vector3(2 * (half + Apron), 0.3f, Apron + 2f);
            g.GetComponent<MeshRenderer>().sharedMaterial = groundMat;
            g.GetComponent<MeshFilter>().sharedMesh = TiledBox(g.transform.localScale, 10f);   // the texture repeats every 10 m instead of stretching over the whole apron
            g.isStatic = true; aprons++;
        }
        Physics.SyncTransforms();
        int placed = 0, tris = 0; var spots = new List<Vector3>();
        for (int i = 0; i < 160 && placed < 16; i++)
        {
            float ang = R(0, 360); var dir = Quaternion.Euler(0, ang, 0) * Vector3.forward;
            if (Vector3.Dot(dir, sea) > -0.15f) continue;                           // the sea side stays open
            // out from the square map's edge in that direction
            float toEdge = half / Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.z));
            var at = dir * (toEdge + R(SkylineFrom, SkylineTo)); at.y = 0;
            if (spots.Any(s => Vector3.Distance(s, at) < 75f)) continue;
            // only on the apron: all four corners of a 50 m footprint must be on the outskirts ground
            bool OnApron(Vector3 q) => Physics.Raycast(new Vector3(q.x, 200, q.z), Vector3.down, out var ah, 400f) && ah.collider.name == "Outskirts ground";
            if (!new[] { new Vector3(-25, 0, -25), new Vector3(25, 0, -25), new Vector3(-25, 0, 25), new Vector3(25, 0, 25) }.All(o => OnApron(at + o))) continue;
            var src = models[rnd.Next(models.Length)];
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src, holder);
            go.name = "Skyline " + src.name;
            // centre the block on its spot, facing the city, sitting on the ground
            var rs = go.GetComponentsInChildren<Renderer>(); var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            go.transform.rotation = Quaternion.LookRotation(-dir) * Quaternion.Euler(0, rnd.Next(4) * 90, 0);
            go.transform.position = Vector3.zero;
            b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            float gy = Physics.Raycast(new Vector3(at.x, 200, at.z), Vector3.down, out var gh, 400f) ? gh.point.y : 0f;
            go.transform.position = new Vector3(at.x - b.center.x, gy - b.min.y - 0.1f, at.z - b.center.z);
            var plaster = plasters[rnd.Next(plasters.Length)];
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterials = r.sharedMaterials.Select(m => !m ? m : m.name.StartsWith("brick") || m.name.StartsWith("concrate_painter") ? plaster : m.name.StartsWith("concrate") ? trim : m).ToArray();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.gameObject.isStatic = true;
            }
            go.isStatic = true;
            tris += go.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh).Sum(f => (int)(f.sharedMesh.triangles.Length / 3));
            spots.Add(at); placed++;
            if (placed <= 3) { var eye = at - dir * (SkylineFrom + 60f); eye.y = 1.7f; ReviewViews.Add((eye, at + Vector3.up * 12f, $"skyline_{placed}.png")); }
        }
        Debug.Log($"[skyline] {aprons} ground aprons, {placed} blocks on the land side, {SkylineFrom}-{SkylineTo} m past the map edge, {tris / 1000}k triangles");
    }
}
