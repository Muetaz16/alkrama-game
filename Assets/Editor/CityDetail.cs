// Street-level realism for the Corniche, placed from the real OSM streets and building footprints
// (benghazi_corniche.json) and checked against the actual city with ray casts:
//   streets: kerbs (black/white on the main roads), raised sidewalks, worn lane markings, cracks,
//            asphalt patches, manholes, shell craters, sand drifting against the kerbs
//   furniture: street lamps (some bent or snapped), utility poles with sagging cables, parked and burnt
//            cars, a checkpoint by the roundabout (concrete barriers, sandbags, drums)
//   buildings: dirt along the base of the walls, rolled-down shop shutters and signs on streets,
//            stair rooms, rebar stubs and satellite dishes on the roofs, rubble along ruined walls
// Everything is merged per material in 64 m cells so it stays cheap to draw.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod CityDetail.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class CityDetail
{
    const string Root = "Assets/Benghazi", Gen = Root + "/Textures/Generated", Mats = Root + "/Materials", PH = Root + "/Textures/PolyHaven";
    const string MeshAsset = Root + "/Detail/CityDetail.asset";
    static System.Random rnd;
    static float R() => (float)rnd.NextDouble();
    static float R(float a, float b) => a + (b - a) * R();

    class Road { public List<Vector3> pts = new(); public float w, half; public string kind; public int id; }
    class Bld { public List<Vector3> poly = new(); public bool ruined; public Vector3 c; public float area; }
    static List<Road> roads; static List<Bld> blds;
    static Vector3 roundabout; static Vector3 player;
    static int counts;

    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Build();
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    // scene must be open
    public static void Build()
    {
        rnd = new System.Random(1911);
        batches.Clear(); matCache.Clear(); counts = 0; carMarks.Clear();
        var old = GameObject.Find("City Detail"); if (old) Object.DestroyImmediate(old);
        Physics.SyncTransforms();
        bool back = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;   // the exported road surface faces down
        Load();
        Textures();
        var p = GameObject.Find("Player"); player = p ? p.transform.position : new Vector3(9999, 0, 9999);

        RealCars.Init();
        HideWebCars();
        Streets();
        Lamps();
        Poles();
        Cars();
        Checkpoint();
        Buildings();

        Physics.queriesHitBackfaces = back;
        var root = new GameObject("City Detail");
        Flush(root);
        RealCars.Spawn(root.transform);
        CarColliders.Apply();           // the cars' real shape for bullets (holes on the bodywork, glass that breaks)
        foreach (var kv in carMarks)
        {
            var mk = new GameObject("CarMarker" + kv.Key).transform; mk.SetParent(root.transform, false);
            mk.position = kv.Value.at; mk.rotation = Quaternion.LookRotation(kv.Value.kerb);
        }
        Physics.SyncTransforms();
        Debug.Log($"[detail] placed {counts} parts in {root.transform.childCount} merged meshes");
    }

    // the web city brings its own block cars (body, cabin, roof, 4 wheels); the real ones replace them
    static void HideWebCars()
    {
        var city = GameObject.Find("Benghazi Corniche"); if (!city) return;
        int n = 0;
        foreach (Transform t in city.GetComponentsInChildren<Transform>(true))
        {
            if (t.childCount != 7) continue;
            var rs = t.GetComponentsInChildren<MeshRenderer>(true); if (rs.Length != 7) continue;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            float horiz = Mathf.Max(b.size.x, b.size.z);
            if (horiz < 3.6f || horiz > 5.2f || b.size.y < 1.0f || b.size.y > 2.1f) continue;
            t.gameObject.SetActive(false); n++;
        }
        // ... and its sandbag walls, which are plain boxes (6 x 2 x 1 segments = 82 vertices, 1 m tall)
        int bags = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.sharedMesh || mf.sharedMesh.vertexCount != 82) continue;
            var r = mf.GetComponent<MeshRenderer>(); if (!r) continue;
            if (Mathf.Abs(r.bounds.size.y - 1f) > 0.15f || Mathf.Max(r.bounds.size.x, r.bounds.size.z) > 6f) continue;
            mf.gameObject.SetActive(false); bags++;
        }
        Debug.Log($"[detail] web block cars hidden: {n}, box sandbags hidden: {bags}");
    }

    // ---------- map ----------
    static void Load()
    {
        var map = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        Vector3 U(object o) { var l = (List<object>)o; return new Vector3(-System.Convert.ToSingle(l[0]), 0, System.Convert.ToSingle(l[1])); }
        roads = new(); blds = new();
        int id = 0;
        foreach (Dictionary<string, object> r in (List<object>)map["roads"])
        {
            var rd = new Road { w = System.Convert.ToSingle(r["w"]), kind = (string)r["kind"], id = id++ };
            rd.half = rd.w / 2 + 1.6f;                       // the web game draws roads this wide
            foreach (var q in (List<object>)r["pts"]) rd.pts.Add(U(q));
            roads.Add(rd);
        }
        foreach (Dictionary<string, object> b in (List<object>)map["buildings"])
        {
            var bd = new Bld { ruined = b.TryGetValue("ruined", out var ru) && ru is bool rb && rb };
            foreach (var q in (List<object>)b["poly"]) bd.poly.Add(U(q));
            bd.c = bd.poly.Aggregate(Vector3.zero, (s, v) => s + v) / bd.poly.Count;
            float a = 0; for (int i = 0; i < bd.poly.Count; i++) { var u = bd.poly[i]; var v = bd.poly[(i + 1) % bd.poly.Count]; a += u.x * v.z - v.x * u.z; }
            bd.area = Mathf.Abs(a) / 2;
            blds.Add(bd);
        }
        roundabout = new Vector3(9999, 0, 9999);
        foreach (Dictionary<string, object> l in (List<object>)map["landmarks"])
            if ((string)l["type"] == "lighthouse" && l.TryGetValue("roundabout", out var rbo)) roundabout = U(rbo);
        // segment grid for "is this point on another street"
        grid.Clear();
        foreach (var r in roads)
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; float m = r.half + 3;
                for (int gx = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - m) / 16); gx <= Mathf.FloorToInt((Mathf.Max(a.x, b.x) + m) / 16); gx++)
                    for (int gz = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - m) / 16); gz <= Mathf.FloorToInt((Mathf.Max(a.z, b.z) + m) / 16); gz++)
                    {
                        if (!grid.TryGetValue((gx, gz), out var list)) grid[(gx, gz)] = list = new();
                        list.Add((a, b, r));
                    }
            }
        Debug.Log($"[detail] {roads.Count} streets, {blds.Count} buildings, roundabout {roundabout}");
    }
    static readonly Dictionary<(int, int), List<(Vector3 a, Vector3 b, Road r)>> grid = new();

    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var d = b - a; d.y = 0; float L = d.sqrMagnitude; if (L < 1e-6f) return Vector2.Distance(new Vector2(p.x, p.z), new Vector2(a.x, a.z));
        float t = Mathf.Clamp01(((p.x - a.x) * d.x + (p.z - a.z) * d.z) / L);
        return Vector2.Distance(new Vector2(p.x, p.z), new Vector2(a.x + d.x * t, a.z + d.z * t));
    }
    // distance past the edge of the nearest street (negative = on it), ignoring one street
    static float StreetEdge(Vector3 p, Road skip, out Road nearest)
    {
        nearest = null; float best = 99;
        if (!grid.TryGetValue((Mathf.FloorToInt(p.x / 16), Mathf.FloorToInt(p.z / 16)), out var list)) return best;
        foreach (var s in list)
        {
            if (s.r == skip) continue;
            float d = SegDist(p, s.a, s.b) - s.r.half;
            if (d < best) { best = d; nearest = s.r; }
        }
        return best;
    }
    static bool Walkable(Road r) => r.kind != "footway" && r.kind != "path" && r.kind != "steps" && r.kind != "pedestrian";
    static bool Main(Road r) => r.kind == "primary" || r.kind == "secondary" || r.kind == "trunk" || r.kind == "tertiary" || r.kind == "primary_link";

    // ---------- what's under / in front of a point ----------
    static bool IsPalm(Transform t) { for (; t; t = t.parent) if (t.name.Contains("palm")) return true; return false; }
    static string MatName(RaycastHit h)
    {
        var r = h.collider.GetComponent<Renderer>(); if (!r) return "";
        var mats = r.sharedMaterials; if (mats.Length == 0) return "";
        if (mats.Length > 1 && h.collider is MeshCollider mc && mc.sharedMesh && h.triangleIndex >= 0)
        {
            int idx = h.triangleIndex * 3; var mesh = mc.sharedMesh;
            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++) { var sd = mesh.GetSubMesh(s); if (idx >= sd.indexStart && idx < sd.indexStart + sd.indexCount) return mats[s] ? mats[s].name : ""; }
        }
        return mats[0] ? mats[0].name : "";
    }
    // top surface at x, z (palms ignored)
    static bool Down(Vector3 p, out RaycastHit hit, out string mat)
    {
        hit = default; mat = "";
        var hits = Physics.RaycastAll(new Vector3(p.x, 80, p.z), Vector3.down, 120, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits) { if (IsPalm(h.collider.transform) || h.collider.GetComponentInParent<PlayerController>()) continue; hit = h; mat = MatName(h); return true; }
        return false;
    }
    static bool Ray(Vector3 o, Vector3 d, float len, out RaycastHit hit)
    {
        hit = default; float best = float.MaxValue; bool any = false;
        foreach (var h in Physics.RaycastAll(o, d, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (!IsPalm(h.collider.transform) && h.distance < best) { best = h.distance; hit = h; any = true; }
        return any;
    }
    // open ground at street level (not a roof, not the sea, not on the landmark squares)
    static bool Ground(Vector3 p, out float y, bool allowPromenade = false)
    {
        y = 0;
        if (!Down(p, out var h, out var m)) return false;
        y = h.point.y;
        if (y < -0.06f || y > 0.45f) return false;
        if (!allowPromenade && (m.StartsWith("Promenade") || m.StartsWith("LighthouseBase") || m.StartsWith("SeaWall"))) return false;
        return true;
    }
    static bool OnRoad(Vector3 p, out float y)
    {
        y = 0;
        if (!Down(p, out var h, out var m)) return false;
        y = h.point.y; return m.StartsWith("Asphalt") && y > -0.05f && y < 0.3f;
    }
    static bool NearSpecial(Vector3 p, float r) => Vector3.Distance(new Vector3(p.x, 0, p.z), roundabout) < 26 + r || Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(player.x, 0, player.z)) < r + 2;

    // ---------- streets ----------
    static void Streets()
    {
        var mKerbW = Mat("KerbWhite", ph: "concrete_wall_008", c: new Color(0.92f, 0.91f, 0.87f), smooth: 0.2f);
        var mKerbB = Mat("KerbBlack", ph: "concrete_wall_008", c: new Color(0.11f, 0.11f, 0.11f), smooth: 0.2f);
        var mKerb = Mat("Kerb", ph: "concrete_wall_008", c: new Color(0.68f, 0.66f, 0.62f), smooth: 0.15f);
        var mWalk = Mat("Sidewalk", ph: "large_grey_tiles", c: new Color(0.93f, 0.88f, 0.8f), smooth: 0.18f, tile: 1.4f);
        var mPaint = Mat("RoadPaint", tex: "WornPaint", c: new Color(0.92f, 0.91f, 0.86f), smooth: 0.35f, cutout: 0.45f);
        var mCrack = Mat("Crack", tex: "Crack", c: Color.white, smooth: 0.1f, cutout: 0.4f);
        var mPatch = Mat("Patch", tex: "Patch", c: Color.white, smooth: 0.15f, cutout: 0.4f);
        var mSand = Mat("SandDrift", tex: "Sand", c: Color.white, smooth: 0.05f, transparent: true);
        var mHole = Mat("Manhole", tex: "Manhole", c: Color.white, smooth: 0.45f, cutout: 0.4f, metal: 0.5f);
        var mScorch = Mat("Crater", tex: "Crater", c: Color.white, smooth: 0.05f, transparent: true);
        var mRubble = Mat("RubbleChunk", ph: "concrete_wall_008", c: new Color(0.78f, 0.73f, 0.64f), smooth: 0.08f);
        int kerbs = 0, walks = 0, marks = 0, decals = 0;
        foreach (var r in roads)
        {
            if (!Walkable(r)) continue;
            bool bw = r.kind == "primary" || r.kind == "primary_link" || r.kind == "secondary";
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 0.5f) continue;
                var f = d / L; var n = new Vector3(-f.z, 0, f.x); var rot = Quaternion.LookRotation(f);
                // kerbs and sidewalks, 1 m pieces, both sides
                for (float s = 0.5f; s < L; s += 1f)
                {
                    var c = a + f * s;
                    foreach (var side in new[] { -1f, 1f })
                    {
                        var kp = c + n * side * (r.half + 0.14f);
                        if (NearSpecial(kp, 0) || StreetEdge(kp, r, out _) < 0.6f) continue;
                        if (!Ground(kp, out var gy)) continue;
                        int piece = Mathf.FloorToInt((s + k * 1000) / 1f);
                        Box(new Vector3(kp.x, gy + 0.07f, kp.z), new Vector3(0.26f, 0.2f, 1.0f), rot, bw ? (piece % 2 == 0 ? mKerbW : mKerbB) : mKerb, true); kerbs++;
                        // sidewalk behind the kerb, only where it's open ground all the way
                        var w0 = c + n * side * (r.half + 0.27f); var w1 = c + n * side * (r.half + 2.5f);
                        if (StreetEdge(w1, r, out _) < 0.3f) continue;
                        if (!Ground(w1, out var gy1) || !Ground((w0 + w1) / 2, out _)) continue;
                        var wc = (w0 + w1) / 2;
                        Box(new Vector3(wc.x, Mathf.Max(gy, gy1) + 0.06f, wc.z), new Vector3(2.23f, 0.18f, 1.0f), rot, mWalk, true); walks++;
                    }
                }
                // lane markings on the main roads: dashed centre line, solid edge lines on the wide ones
                if (Main(r) && r.w >= 8)
                    for (float s = 1f; s < L - 1f; s += 7f)
                    {
                        var c = a + f * Mathf.Min(s + 1.5f, L - 0.5f);
                        if (StreetEdge(c, r, out _) < 1.5f || NearSpecial(c, 0) || !OnRoad(c, out var ry)) continue;
                        Flat(c, ry + 0.012f, 0.13f, 3f, rot, mPaint); marks++;
                        if (r.w >= 10)
                            foreach (var side in new[] { -1f, 1f })
                            {
                                var e = c + n * side * (r.half - 0.5f);
                                if (StreetEdge(e, r, out _) < 1.5f || !OnRoad(e, out var ey)) continue;
                                Flat(e, ey + 0.012f, 0.15f, 7f, rot, mPaint); marks++;
                            }
                    }
                // wear: cracks, patches, manholes, a few shell craters, sand against the kerbs
                for (float s = R(2, 9); s < L; s += R(7, 14))
                {
                    var c = a + f * s + n * R(-r.half + 1, r.half - 1);
                    if (!OnRoad(c, out var ry) || NearSpecial(c, 0)) continue;
                    float pick = R(); var yaw = Quaternion.Euler(0, R(0, 360), 0);
                    if (pick < 0.42f) { Flat(c, ry + 0.010f, R(1.5f, 3.2f), R(1.5f, 3.2f), yaw, mCrack); decals++; }
                    else if (pick < 0.66f) { Flat(c, ry + 0.008f, R(1.2f, 3f), R(1.0f, 2.2f), yaw, mPatch); decals++; }
                    else if (pick < 0.74f && Main(r)) { var m = a + f * s; if (OnRoad(m, out var my)) { Flat(m, my + 0.011f, 0.75f, 0.75f, yaw, mHole); decals++; } }
                    else if (pick < 0.79f)
                    {
                        Flat(c, ry + 0.014f, R(3f, 4.5f), R(3f, 4.5f), yaw, mScorch); decals++;
                        for (int i = 0; i < 9; i++)
                        {
                            float ang = R(0, 6.28f), rad = R(1.0f, 2.2f), sz = R(0.12f, 0.35f);
                            Box(c + new Vector3(Mathf.Cos(ang) * rad, ry + sz * 0.3f, Mathf.Sin(ang) * rad), new Vector3(sz, sz * 0.6f, sz * R(0.8f, 1.4f)), RandRot(), mRubble);
                        }
                    }
                }
                for (float s = 0; s < L - 1; s += 4f)
                    foreach (var side in new[] { -1f, 1f })
                    {
                        if (R() < 0.35f) continue;
                        var c = a + f * (s + 2f) + n * side * (r.half - 0.45f);
                        if (StreetEdge(c, r, out _) < 0.8f || NearSpecial(c, 0) || !OnRoad(c, out var ry)) continue;
                        var q = side > 0 ? rot : rot * Quaternion.Euler(0, 180, 0);
                        Flat(c, ry + 0.009f, 1.0f, 4.2f, q, mSand); decals++;
                    }
            }
        }
        Debug.Log($"[detail] kerbs {kerbs}, sidewalk {walks}, markings {marks}, road wear {decals}");
    }

    // ---------- street lamps ----------
    static void Lamps()
    {
        var mPole = Mat("LampPole", c: new Color(0.36f, 0.37f, 0.38f), smooth: 0.4f, metal: 0.7f, tex: "NoiseGrey");
        var mHead = Mat("LampHead", c: new Color(0.25f, 0.26f, 0.27f), smooth: 0.5f, metal: 0.6f);
        var mGlass = Mat("LampGlass", c: new Color(0.85f, 0.85f, 0.78f), smooth: 0.9f);
        int n = 0;
        foreach (var r in roads.Where(Main))
        {
            float along = R(3, 12); int side = 1;
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 1) continue;
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (; along < L; along += 28f, side = -side)
                {
                    var p = a + f * along + nn * side * (r.half + 0.75f);
                    if (NearSpecial(p, 2) || StreetEdge(p, r, out _) < 1.2f || !Ground(p, out var gy, true)) continue;
                    var toRoad = -nn * side;
                    float lean = R() < 0.15f ? R(6, 16) : 0; bool snapped = R() < 0.08f;
                    var tilt = Quaternion.AngleAxis(lean, Vector3.Cross(Vector3.up, toRoad).normalized * (R() < 0.5f ? 1 : -1));
                    var basePos = new Vector3(p.x, gy, p.z);
                    Cyl(basePos + Vector3.up * 0.4f, 0.13f, 0.8f, Quaternion.identity, mPole);
                    if (snapped)
                    {
                        Cyl(basePos + tilt * Vector3.up * 1.6f, 0.075f, 3.2f, tilt, mPole);
                        var fall = Quaternion.LookRotation(toRoad) * Quaternion.Euler(86, 0, 0);
                        Cyl(basePos + toRoad * 2.6f + Vector3.up * 0.12f, 0.07f, 4.6f, fall, mPole);
                    }
                    else
                    {
                        var top = basePos + tilt * Vector3.up * 8f;
                        Cyl(basePos + tilt * Vector3.up * 4f, 0.07f, 8f, tilt, mPole);
                        var armRot = Quaternion.LookRotation(toRoad) * Quaternion.Euler(-8, 0, 0);
                        Box(top + toRoad * 0.8f + Vector3.up * 0.05f, new Vector3(0.07f, 0.07f, 1.7f), armRot, mPole);
                        var head = top + toRoad * 1.65f + Vector3.up * 0.12f;
                        Box(head, new Vector3(0.34f, 0.13f, 0.62f), Quaternion.LookRotation(toRoad), mHead);
                        Box(head - Vector3.up * 0.07f, new Vector3(0.26f, 0.02f, 0.48f), Quaternion.LookRotation(toRoad), mGlass);
                    }
                    n++;
                }
                along -= L;
            }
        }
        Debug.Log($"[detail] street lamps {n}");
    }

    // ---------- utility poles with sagging cables ----------
    static void Poles()
    {
        var mWood = Mat("PoleWood", c: new Color(0.32f, 0.25f, 0.18f), smooth: 0.1f, tex: "NoiseGrey");
        var mWire = Mat("Wire", c: new Color(0.03f, 0.03f, 0.03f), smooth: 0.3f);
        int n = 0, wires = 0;
        foreach (var r in roads.Where(x => x.kind == "residential" || x.kind == "unclassified" || x.kind == "tertiary"))
        {
            int side = r.id % 2 == 0 ? 1 : -1;
            Vector3? prev = null;
            float along = R(4, 14);
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 1) { continue; }
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (; along < L; along += 30f)
                {
                    var p = a + f * along + nn * side * (r.half + 0.6f);
                    if (NearSpecial(p, 3) || StreetEdge(p, r, out _) < 1f || !Ground(p, out var gy, true)) { prev = null; continue; }
                    float lean = R(-4, 4);
                    var tilt = Quaternion.AngleAxis(lean, f);
                    var basePos = new Vector3(p.x, gy, p.z);
                    Cyl(basePos + tilt * Vector3.up * 3.9f, 0.12f, 7.8f, tilt, mWood);
                    var top = basePos + tilt * Vector3.up * 7.4f;
                    Box(top, new Vector3(1.5f, 0.1f, 0.1f), Quaternion.LookRotation(f) * Quaternion.AngleAxis(lean, Vector3.forward), mWood);
                    if (prev.HasValue && Vector3.Distance(prev.Value, top) < 45)
                    {
                        var right = Vector3.Cross(Vector3.up, (top - prev.Value).normalized);
                        foreach (var o in new[] { -0.65f, 0f, 0.65f }) { Cable(prev.Value + right * o, top + right * o, 0.7f + R(0, 0.5f), mWire); wires++; }
                    }
                    prev = top; n++;
                }
                along -= L;
            }
        }
        Debug.Log($"[detail] utility poles {n}, cables {wires}");
    }

    static void Cable(Vector3 a, Vector3 b, float sag, Material m)
    {
        const int segs = 10; Vector3 last = a;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs; var p = Vector3.Lerp(a, b, t) + Vector3.down * sag * 4 * t * (1 - t);
            var d = p - last; Box((p + last) / 2, new Vector3(0.018f, 0.018f, d.magnitude + 0.01f), Quaternion.LookRotation(d), m); last = p;
        }
    }

    // ---------- cars: the everyday Benghazi mix - small sedans, hatchbacks, SUVs, pickups - some shot up or burnt ----------
    // Generic shapes of the cars you see there (Hyundai / Kia sedans, Toyota SUVs, Hilux-style pickups), no badges.
    class CarType
    {
        public string name; public Mesh core, shell, glass, roof; public float W, wheelR, wheelF, wheelB, track, bPillar; public bool pickup;
        public Vector2[] g; public float frontZ, backZ, bumperY;
    }
    static CarType[] carTypes;
    static readonly Dictionary<int, (Vector3 at, Vector3 kerb)> carMarks = new();
    static Mesh jersey, dish;

    static CarType MakeType(string name, Vector2[] body, Vector2[] glass, float W, float wheelR, float wf, float wb, float track, float bPillar, bool pickup = false)
    {
        // rounded look: a full-width core, and a slightly smaller full-profile shell standing proud at the sides
        var c = body.Aggregate(Vector2.zero, (s, v) => s + v) / body.Length;
        var inner = body.Select(v => c + (v - c) * 0.965f).ToArray();
        var t = new CarType { name = name, W = W, wheelR = wheelR, wheelF = wf, wheelB = wb, track = track, bPillar = bPillar, pickup = pickup, g = glass };
        t.core = GunFactory.Extrude(body, W - 0.1f, 0.25f);
        t.shell = GunFactory.Extrude(inner, W, 0.25f);
        t.glass = GunFactory.Extrude(glass, W * 0.82f, 0.25f);
        var g1 = glass[1]; var g2 = glass[2];
        t.roof = GunFactory.Extrude(new[] { g1 + new Vector2(0.04f, -0.01f), g2 + new Vector2(-0.04f, -0.01f), g2 + new Vector2(-0.06f, 0.06f), g1 + new Vector2(0.06f, 0.06f) }, W * 0.86f, 0.25f);
        t.frontZ = body.Max(v => v.x); t.backZ = body.Min(v => v.x); t.bumperY = body.Min(v => v.y) + 0.1f;
        return t;
    }

    static void Cars()
    {
        Meshes();
        carTypes = new[]
        {
            MakeType("sedan", P(2.25f, 0.33f, 2.3f, 0.5f, 2.27f, 0.68f, 2.1f, 0.8f, 1.0f, 0.96f, -1.55f, 1.0f, -2.15f, 0.98f, -2.27f, 0.82f, -2.3f, 0.5f, -2.25f, 0.33f),
                     P(0.98f, 0.95f, 0.25f, 1.40f, -0.95f, 1.43f, -1.6f, 0.99f), 1.8f, 0.31f, 1.35f, -1.32f, 0.76f, -0.35f),
            MakeType("hatch", P(1.95f, 0.33f, 2.0f, 0.5f, 1.97f, 0.7f, 1.8f, 0.82f, 0.85f, 0.97f, -1.8f, 1.05f, -1.98f, 0.95f, -2.0f, 0.5f, -1.95f, 0.33f),
                     P(0.83f, 0.96f, 0.15f, 1.45f, -1.55f, 1.45f, -1.85f, 1.04f), 1.72f, 0.3f, 1.25f, -1.22f, 0.74f, -0.45f),
            MakeType("suv", P(2.3f, 0.45f, 2.35f, 0.65f, 2.3f, 0.95f, 2.1f, 1.05f, 1.3f, 1.12f, -2.25f, 1.15f, -2.33f, 1.0f, -2.35f, 0.55f, -2.3f, 0.45f),
                     P(1.28f, 1.11f, 0.75f, 1.72f, -2.05f, 1.76f, -2.25f, 1.15f), 1.88f, 0.37f, 1.45f, -1.4f, 0.8f, -0.4f),
            MakeType("pickup", P(2.6f, 0.5f, 2.65f, 0.72f, 2.6f, 1.0f, 2.4f, 1.08f, 1.55f, 1.14f, -0.55f, 1.14f, -0.55f, 0.95f, -2.6f, 0.95f, -2.65f, 0.7f, -2.6f, 0.5f),
                     P(1.5f, 1.13f, 0.95f, 1.72f, -0.45f, 1.74f, -0.55f, 1.14f), 1.82f, 0.38f, 1.6f, -1.45f, 0.78f, 99f, true),
        };
        // white and silver dominate the streets there
        var paintCols = new[] { new Color(0.86f, 0.86f, 0.84f), new Color(0.86f, 0.86f, 0.84f), new Color(0.6f, 0.61f, 0.62f), new Color(0.6f, 0.61f, 0.62f), new Color(0.07f, 0.07f, 0.08f),
                                new Color(0.42f, 0.08f, 0.07f), new Color(0.1f, 0.18f, 0.34f), new Color(0.72f, 0.66f, 0.52f), new Color(0.3f, 0.32f, 0.3f) };
        var paints = paintCols.Select((c, i) => Mat("CarPaint" + i, c: c, smooth: 0.78f, metal: 0.35f)).ToArray();
        var dirty = paintCols.Select((c, i) => Mat("CarPaintDirty" + i, tex: "NoiseGrey", c: c * 0.78f, smooth: 0.35f, metal: 0.2f)).ToArray();
        var m = new CarMats
        {
            glass = Mat("CarGlass", c: new Color(0.05f, 0.07f, 0.08f), smooth: 0.95f, metal: 0.2f),
            shattered = Mat("CarGlassShot", tex: "Shattered", c: Color.white, smooth: 0.6f),
            tyre = Mat("Tyre", c: new Color(0.05f, 0.05f, 0.05f), smooth: 0.15f),
            rim = Mat("Rim", c: new Color(0.62f, 0.63f, 0.64f), smooth: 0.7f, metal: 0.9f),
            plastic = Mat("CarTrim", c: new Color(0.06f, 0.06f, 0.065f), smooth: 0.3f),
            chrome = Mat("Chrome", c: new Color(0.75f, 0.76f, 0.78f), smooth: 0.9f, metal: 1f),
            lamp = Mat("CarLight", c: new Color(0.9f, 0.9f, 0.86f), smooth: 0.95f, metal: 0.3f),
            tail = Mat("CarTail", c: new Color(0.6f, 0.05f, 0.04f), smooth: 0.85f),
            amber = Mat("CarAmber", c: new Color(0.9f, 0.5f, 0.08f), smooth: 0.85f),
            plate = Mat("Plate", c: new Color(0.88f, 0.88f, 0.85f), smooth: 0.4f),
            rust = Mat("Rust", tex: "Rust", c: Color.white, smooth: 0.12f, metal: 0.4f),
            burnt = Mat("Burnt", tex: "NoiseGrey", c: new Color(0.07f, 0.065f, 0.06f), smooth: 0.08f),
            hole = Mat("BulletHole", c: new Color(0.02f, 0.02f, 0.02f), smooth: 0.1f),
            well = Mat("WheelWell", c: new Color(0.025f, 0.025f, 0.025f), smooth: 0.05f),
        };
        int n = 0, broken = 0, burnt = 0;
        foreach (var r in roads.Where(x => Walkable(x) && x.kind != "service"))
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 6) continue;
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (float s = R(3, 10); s < L - 3; s += R(11, 24))
                {
                    if (R() > 0.5f) continue;
                    bool abandoned = R() < 0.25f;                        // left in the lane at an angle
                    float side = R() < 0.5f ? -1 : 1;
                    var p = a + f * s + nn * (abandoned ? R(-r.half + 1.6f, r.half - 1.6f) : side * (r.half - 1.15f));
                    if (NearSpecial(p, 5) || StreetEdge(p, r, out _) < 2f || !OnRoad(p, out var y)) continue;
                    if (!OnRoad(p + f * 2.4f, out _) || !OnRoad(p - f * 2.4f, out _)) continue;
                    float roll = R();
                    int state = roll < 0.45f ? 0 : roll < 0.78f ? 1 : 2;      // 0 fine, 1 shot up / broken, 2 burnt out
                    var rot = Quaternion.LookRotation(f * (R() < 0.5f ? 1 : -1)) * Quaternion.Euler(0, abandoned ? R(-40, 40) : R(-4, 4), 0);
                    float pick = R();
                    if (RealCars.Ready)                                        // the downloaded sedan and SUV
                    {
                        int model = pick < 0.5f || RealCars.ModelCount < 2 ? 0 : 1;
                        RealCars.Queue(model, new Vector3(p.x, y, p.z), rot, state, rnd);
                        int mk = model * 10 + state;
                        if (!carMarks.ContainsKey(mk) && !abandoned && Vector3.Distance(p, player) < 260) carMarks[mk] = (new Vector3(p.x, y, p.z), nn * side);
                    }
                    else
                    {
                        int pc = rnd.Next(paints.Length);                      // Hilux-style pickup built here
                        BuildCar(carTypes[3], new Vector3(p.x, y, p.z), rot, state == 1 ? dirty[pc] : paints[pc], state, m);
                    }
                    n++; if (state == 1) broken++; if (state == 2) burnt++;
                }
            }
        Debug.Log($"[detail] cars {n} ({broken} broken, {burnt} burnt)");
    }

    class CarMats { public Material glass, shattered, tyre, rim, plastic, chrome, lamp, tail, amber, plate, rust, burnt, hole, well; }

    static void BuildCar(CarType t, Vector3 at, Quaternion rot, Material paint, int state, CarMats m)
    {
        bool burnt = state == 2, broken = state == 1;
        var body = burnt ? (R() < 0.5f ? m.burnt : m.rust) : paint;
        // flat tyre / sitting on the rims
        int flat = broken && R() < 0.6f ? rnd.Next(4) : -1;
        float sink = burnt ? 0.16f : 0f;
        var tilt = burnt ? Quaternion.Euler(R(-2.5f, 2.5f), 0, R(-3, 3)) : flat >= 0 ? Quaternion.Euler(flat < 2 ? 1.5f : -1.5f, 0, flat % 2 == 0 ? -2.2f : 2.2f) : Quaternion.identity;
        var q = rot * tilt;
        var o = at + Vector3.down * sink;
        Matrix4x4 M(Vector3 lp) => Matrix4x4.TRS(o + q * lp, q, Vector3.one);
        Vector3 W(float x, float y, float z) => o + q * new Vector3(x, y, z);

        Add(t.core, M(Vector3.zero), body, true);
        Add(t.shell, M(Vector3.zero), body, false);
        // cabin: glass, roof, pillars
        Material glassM = burnt ? null : broken ? (R() < 0.6f ? m.shattered : m.glass) : m.glass;
        if (glassM) Add(t.glass, M(Vector3.zero), glassM, false);
        float roofDrop = burnt ? R(0.04f, 0.18f) : 0;
        Add(t.roof, Matrix4x4.TRS(o + q * new Vector3(0, -roofDrop, 0), q * Quaternion.Euler(burnt ? R(-3, 3) : 0, 0, burnt ? R(-4, 4) : 0), Vector3.one), body, false);
        var g = t.g; float px = t.W * 0.415f;
        foreach (var sx in new[] { -1f, 1f })
        {
            Strut(W(sx * px, g[0].y, g[0].x), W(sx * px, g[1].y - roofDrop, g[1].x), 0.07f, body);       // A pillar
            Strut(W(sx * px, g[3].y, g[3].x), W(sx * px, g[2].y - roofDrop, g[2].x), t.name == "suv" ? 0.16f : 0.11f, body);   // C pillar
            if (t.bPillar < 50)
            {
                float yb = g[3].y + (g[0].y - g[3].y) * Mathf.InverseLerp(g[3].x, g[0].x, t.bPillar);
                float yt = g[2].y + (g[1].y - g[2].y) * Mathf.InverseLerp(g[2].x, g[1].x, t.bPillar);
                Strut(W(sx * px, yb, t.bPillar), W(sx * px, yt - roofDrop, t.bPillar), 0.08f, body);
            }
            // door gaps, handles, mirror
            float half = t.W / 2;
            Box(W(sx * (half + 0.003f), 0.68f, g[0].x + 0.05f), new Vector3(0.006f, 0.5f, 0.012f), q, m.well);
            if (t.bPillar < 50) Box(W(sx * (half + 0.003f), 0.68f, t.bPillar), new Vector3(0.006f, 0.5f, 0.012f), q, m.well);
            Box(W(sx * (half + 0.012f), 0.86f, g[0].x - 0.35f), new Vector3(0.025f, 0.03f, 0.14f), q, burnt ? body : m.chrome);
            if (!burnt) Box(W(sx * (half + 0.09f), g[0].y + 0.06f, g[0].x - 0.08f), new Vector3(0.14f, 0.11f, 0.06f), q, paint);
            // wheel wells
            foreach (var wz in new[] { t.wheelF, t.wheelB })
                Cyl(W(sx * (half - 0.03f), t.wheelR, wz), t.wheelR + 0.06f, 0.08f, q * Quaternion.Euler(0, 0, 90), m.well);
        }
        // wheels: tyre, alloy rim, hub
        int wi = 0;
        foreach (var wz in new[] { t.wheelF, t.wheelB })
            foreach (var sx in new[] { -1f, 1f })
            {
                bool isFlat = wi++ == flat;
                float r = t.wheelR, cy = burnt ? 0.2f + sink : isFlat ? r - 0.07f : r;
                var wp = W(sx * t.track, cy - sink, wz); var wr = q * Quaternion.Euler(0, 0, 90);
                if (!burnt) Add(cylMesh, Matrix4x4.TRS(wp, wr, new Vector3(2 * r, 0.11f, 2 * r * (isFlat ? 0.82f : 1f))), m.tyre, false);
                Cyl(wp + q * new Vector3(sx * (burnt ? 0.02f : 0.112f), 0, 0), r * 0.64f, 0.02f, wr, burnt ? m.rust : m.rim);
                if (!burnt) Cyl(wp + q * new Vector3(sx * 0.124f, 0, 0), r * 0.2f, 0.02f, wr, m.plastic);
            }
        // front: bumper, grille, headlights, plate; back: bumper, tail lights, plate
        float fz = t.frontZ, bz = t.backZ, by = t.bumperY;
        bool lostBumper = broken && R() < 0.3f;
        if (!lostBumper) Box(W(0, by, fz - 0.02f), new Vector3(t.W - 0.04f, 0.2f, 0.12f), q, burnt ? body : (t.pickup ? m.chrome : paint));
        else Box(o + q * new Vector3(R(-0.6f, 0.6f), 0.06f, fz + R(0.4f, 1.2f)), new Vector3(t.W - 0.1f, 0.18f, 0.1f), q * Quaternion.Euler(R(-10, 10), R(-30, 30), 80), burnt ? body : paint);
        Box(W(0, by + 0.24f, fz + 0.005f), new Vector3(t.W * (t.pickup ? 0.5f : 0.36f), t.pickup ? 0.22f : 0.12f, 0.04f), q, t.pickup && !burnt ? m.chrome : m.plastic);
        foreach (var sx in new[] { -1f, 1f })
        {
            Box(W(sx * t.W * 0.33f, by + 0.27f, fz - 0.04f), new Vector3(t.W * 0.24f, 0.11f, 0.08f), q * Quaternion.Euler(0, sx * 12, 0), burnt ? m.plastic : m.lamp);
            Box(W(sx * t.W * 0.45f, by + 0.27f, fz - 0.14f), new Vector3(0.04f, 0.06f, 0.1f), q, burnt ? m.plastic : m.amber);
            Box(W(sx * t.W * 0.38f, by + (t.pickup ? 0.45f : 0.42f), bz + 0.03f), new Vector3(t.W * 0.18f, t.pickup ? 0.3f : 0.13f, 0.06f), q, burnt ? m.plastic : m.tail);
        }
        Box(W(0, by, bz + 0.02f), new Vector3(t.W - 0.04f, 0.2f, 0.12f), q, burnt ? body : m.plastic);
        if (!burnt) { Box(W(0, by + 0.05f, fz + 0.045f), new Vector3(0.5f, 0.11f, 0.01f), q, m.plate); Box(W(0, by + 0.2f, bz - 0.015f), new Vector3(0.5f, 0.11f, 0.01f), q, m.plate); }
        // pickup bed: sides, tailgate, floor
        if (t.pickup)
        {
            foreach (var sx in new[] { -1f, 1f }) Box(W(sx * (t.W / 2 - 0.04f), 1.2f, -1.58f), new Vector3(0.07f, 0.5f, 2.05f), q, body, true);
            Box(W(0, 1.2f, bz + 0.05f), new Vector3(t.W - 0.04f, 0.5f, 0.07f), q, body, true);
            Box(W(0, 0.96f, -1.58f), new Vector3(t.W - 0.1f, 0.04f, 2.05f), q, burnt ? body : m.plastic);
        }
        // shot up: bullet holes along the doors
        if (broken || (burnt && R() < 0.5f))
            for (int i = 0, nh = rnd.Next(6, 16); i < nh; i++)
            {
                float sx = R() < 0.5f ? -1 : 1;
                Box(W(sx * (t.W / 2 + 0.004f), R(0.5f, 0.92f), R(bz + 0.5f, fz - 0.5f)), new Vector3(0.01f, 0.035f, 0.035f), q, m.hole);
            }
    }

    static void Strut(Vector3 a, Vector3 b, float thick, Material m)
    {
        var d = b - a; Box((a + b) / 2, new Vector3(0.06f, thick, d.magnitude + 0.02f), Quaternion.LookRotation(d, Vector3.up), m);
    }

    // ---------- checkpoint by the roundabout ----------
    static void Checkpoint()
    {
        if (roundabout.x > 9000) return;
        jersey = GunFactory.Extrude(P(-0.31f, 0, 0.31f, 0, 0.29f, 0.08f, 0.1f, 0.34f, 0.08f, 0.82f, -0.08f, 0.82f, -0.1f, 0.34f, -0.29f, 0.08f), 3.0f, 0.4f);
        var mConc = Mat("Barrier", ph: "concrete_wall_008", c: new Color(0.8f, 0.78f, 0.72f), smooth: 0.12f);
        var mBag = Mat("Sandbag", tex: "NoiseGrey", c: new Color(0.63f, 0.56f, 0.41f), smooth: 0.05f);
        var drums = new[] { Mat("DrumBlue", tex: "Rust", c: new Color(0.45f, 0.6f, 0.95f), smooth: 0.3f, metal: 0.5f), Mat("DrumRed", tex: "Rust", c: new Color(1f, 0.55f, 0.5f), smooth: 0.3f, metal: 0.5f) };
        var mTyre = Mat("Tyre", c: new Color(0.05f, 0.05f, 0.05f), smooth: 0.15f);
        int placed = 0; var done = new HashSet<Road>();
        foreach (var r in roads.Where(Walkable))
            for (int k = 0; k < r.pts.Count - 1 && !done.Contains(r); k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 1) continue;
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (float s = 0; s < L; s += 2)
                {
                    var c = a + f * s; float dist = Vector3.Distance(new Vector3(c.x, 0, c.z), roundabout);
                    if (dist < 38 || dist > 41) continue;
                    // chicane: two staggered barrier pairs, sandbag post on the side, drums and tyres
                    var away = (c - roundabout); away.y = 0; away.Normalize(); var across = Quaternion.LookRotation(f);
                    foreach (var (off, side) in new[] { (0f, -1f), (5f, 1f) })
                    {
                        var bp = c + away * off + nn * side * (r.half * 0.45f);
                        if (!OnRoad(bp, out var y)) continue;
                        Add(jersey, Matrix4x4.TRS(new Vector3(bp.x, y, bp.z), across * Quaternion.Euler(0, R(-6, 6), 0), Vector3.one), mConc, true);
                    }
                    var post = c + away * 2.5f + nn * (r.half + 1.8f);
                    if (Ground(post, out var gy, true))
                    {
                        var face = Quaternion.LookRotation(-nn);
                        for (int row = 0; row < 3; row++)
                            for (int i = -3; i <= 3; i++)
                                Add(sphereMesh, Matrix4x4.TRS(new Vector3(post.x, gy + 0.12f + row * 0.19f, post.z) + face * new Vector3(i * 0.52f + (row % 2) * 0.26f, 0, 0), face * Quaternion.Euler(0, R(-8, 8), 0), new Vector3(0.58f, 0.2f, 0.34f)), mBag, true);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        var dp = c + away * R(-1, 7) + nn * R(-r.half, r.half) * 0.8f;
                        if (!OnRoad(dp, out var y)) continue;
                        if (R() < 0.6f) Cyl(new Vector3(dp.x, y + 0.44f, dp.z), 0.29f, 0.88f, Quaternion.Euler(0, R(0, 360), 0), drums[rnd.Next(2)], true);
                        else Cyl(new Vector3(dp.x, y + 0.11f, dp.z), 0.36f, 0.22f, Quaternion.Euler(R(-5, 5), 0, R(-5, 5)), mTyre);
                    }
                    placed++; done.Add(r); break;
                }
            }
        Debug.Log($"[detail] checkpoints {placed}");
    }

    // ---------- buildings ----------
    static void Buildings()
    {
        var mGrime = Mat("WallGrime", tex: "Grime", c: Color.white, smooth: 0.05f, transparent: true);
        var mShutter = Mat("Shutter", tex: "Shutter", c: Color.white, smooth: 0.35f, metal: 0.5f);
        var mBoxM = Mat("ShutterBox", c: new Color(0.42f, 0.43f, 0.44f), smooth: 0.35f, metal: 0.5f);
        var signs = new[] { new Color(0.62f, 0.14f, 0.1f), new Color(0.1f, 0.38f, 0.22f), new Color(0.12f, 0.24f, 0.5f), new Color(0.82f, 0.62f, 0.15f), new Color(0.85f, 0.84f, 0.8f) }
            .Select((c, i) => Mat("Sign" + i, tex: "Sign", c: c, smooth: 0.4f)).ToArray();
        var mPlaster = Mat("RoofRoom", ph: "plastered_wall_02", c: new Color(0.82f, 0.76f, 0.66f), smooth: 0.1f);
        var mDoor = Mat("MetalDoor", tex: "Rust", c: new Color(0.55f, 0.6f, 0.55f), smooth: 0.3f, metal: 0.5f);
        var mConc = Mat("RubbleChunk", ph: "concrete_wall_008", c: new Color(0.78f, 0.73f, 0.64f), smooth: 0.08f);
        var mRebar = Mat("Rebar", tex: "Rust", c: new Color(0.7f, 0.6f, 0.55f), smooth: 0.2f, metal: 0.7f);
        var mDish = Mat("Dish", c: new Color(0.86f, 0.86f, 0.84f), smooth: 0.45f);
        var mGrey = Mat("DishArm", c: new Color(0.3f, 0.3f, 0.31f), smooth: 0.4f, metal: 0.6f);
        dish = GunFactory.Lathe(0, 0.14f, 0.05f, 0.45f, 0, 24);
        int grime = 0, shops = 0, rooms = 0, rebar = 0, dishes = 0, rubble = 0;
        foreach (var b in blds)
        {
            int nP = b.poly.Count; if (nP < 3) continue;
            // roof height at the middle
            float roofY = -1;
            if (Down(b.c, out var rh, out _) && rh.normal.y > 0.85f && rh.point.y > 2.4f) roofY = rh.point.y;
            for (int i = 0; i < nP; i++)
            {
                var a = b.poly[i]; var c = b.poly[(i + 1) % nP]; var d = c - a; d.y = 0; float L = d.magnitude; if (L < 2.5f) continue;
                var f = d / L; var mid = (a + c) / 2;
                var n = new Vector3(-f.z, 0, f.x); if (Vector3.Dot(n, mid - b.c) < 0) n = -n;
                // find the real wall surface
                if (!Ray(mid + n * 1.6f + Vector3.up * 1.2f, -n, 2.6f, out var wh) || Vector3.Dot(wh.normal, n) < 0.7f) continue;
                float inset = Vector3.Dot(wh.point - mid, n);
                var wallMid = new Vector3(mid.x, 0, mid.z) + n * inset;
                var face = Quaternion.LookRotation(-n);              // looking at the wall
                // dirt along the base
                Vertical(wallMid + n * 0.03f + Vector3.up * 0.8f, L + 0.05f, 1.6f, face, mGrime); grime++;
                // ground floor on a street: rolled-down shop shutters with signs above
                bool street = StreetEdge(mid + n * 2f, null, out var near) < 4f && near != null && Walkable(near);
                if (street && !b.ruined && L > 4)
                    for (float s = 1.8f; s < L - 1.6f; s += 3.6f)
                    {
                        if (R() > 0.45f) continue;
                        var p = a + f * s + n * inset; p.y = 0;
                        if (!Ray(p + n * 1.2f + Vector3.up * 1.3f, -n, 1.6f, out var sh) || Vector3.Dot(sh.normal, n) < 0.7f) continue;
                        var at = sh.point + n * 0.04f; at.y = 0;
                        Box(at + Vector3.up * 1.25f, new Vector3(2.6f, 2.5f, 0.06f), face, mShutter);
                        Box(at + Vector3.up * 2.62f + n * 0.08f, new Vector3(2.75f, 0.3f, 0.24f), face, mBoxM);
                        if (R() < 0.6f) Box(at + Vector3.up * 3.15f + n * 0.05f, new Vector3(2.8f, 0.62f, 0.08f), face, signs[rnd.Next(signs.Length)]);
                        shops++;
                    }
                // ruined: rubble spilled along the wall
                if (b.ruined)
                    for (float s = 0.5f; s < L; s += 1.3f)
                    {
                        if (R() > 0.5f) continue;
                        var p = a + f * s + n * (inset + R(0.3f, 2.4f));
                        if (!Ground(p, out var gy, true)) continue;
                        float sz = R(0.2f, 0.75f);
                        Box(new Vector3(p.x, gy + sz * 0.25f, p.z), new Vector3(sz, sz * R(0.4f, 0.8f), sz * R(0.7f, 1.5f)), RandRot(), mConc); rubble++;
                    }
            }
            if (roofY < 0 || b.area < 25) continue;
            // roof: stair room, rebar stubs at the corners (unfinished next floor), extra dishes
            bool RoofAt(Vector3 p, out float y) { y = 0; if (!Down(p, out var h2, out _) || h2.normal.y < 0.85f || Mathf.Abs(h2.point.y - roofY) > 0.25f) return false; y = h2.point.y; return true; }
            if (!b.ruined && R() < 0.28f)
            {
                var p = Vector3.Lerp(b.c, b.poly[rnd.Next(nP)], 0.35f);
                var e = b.poly[1] - b.poly[0]; var q = Quaternion.LookRotation(new Vector3(e.x, 0, e.z));
                if (RoofAt(p, out var y) && RoofAt(p + q * new Vector3(1.3f, 0, 1.3f), out _) && RoofAt(p - q * new Vector3(1.3f, 0, 1.3f), out _))
                {
                    Box(new Vector3(p.x, y + 1.3f, p.z), new Vector3(2.5f, 2.6f, 2.7f), q, mPlaster, true);
                    Box(new Vector3(p.x, y + 2.65f, p.z), new Vector3(2.7f, 0.12f, 2.9f), q, mPlaster);
                    Box(new Vector3(p.x, y + 1.05f, p.z) + q * new Vector3(0, 0, 1.36f), new Vector3(0.9f, 2.0f, 0.04f), q, mDoor);
                    rooms++;
                }
            }
            if (R() < (b.ruined ? 0.6f : 0.3f))
                foreach (var corner in b.poly)
                {
                    if (R() < 0.3f) continue;
                    var p = Vector3.MoveTowards(corner, b.c, 0.45f);
                    if (!RoofAt(p, out var y)) continue;
                    Box(new Vector3(p.x, y + 0.2f, p.z), new Vector3(0.3f, 0.4f, 0.3f), Quaternion.Euler(0, R(0, 90), 0), mConc);
                    for (int i = 0; i < 4; i++)
                    {
                        var o = new Vector3((i % 2 - 0.5f) * 0.18f, 0, (i / 2 - 0.5f) * 0.18f);
                        var t = Quaternion.Euler(R(-12, 12), 0, R(-12, 12)); float h = R(0.6f, 1.1f);
                        Cyl(new Vector3(p.x, y + 0.4f, p.z) + o + t * Vector3.up * h / 2, 0.008f, h, t, mRebar);
                    }
                    rebar++;
                }
            if (!b.ruined && R() < 0.35f)
            {
                var p = Vector3.Lerp(b.c, b.poly[rnd.Next(nP)], R(0.4f, 0.7f));
                if (RoofAt(p, out var y))
                {
                    var face = Quaternion.Euler(-35, R(150, 210), 0);       // towards the satellites in the south
                    Cyl(new Vector3(p.x, y + 0.45f, p.z), 0.025f, 0.9f, Quaternion.identity, mGrey);
                    Add(dish, Matrix4x4.TRS(new Vector3(p.x, y + 0.95f, p.z), face * Quaternion.Euler(180, 0, 0), Vector3.one), mDish, false);
                    Box(new Vector3(p.x, y + 0.95f, p.z) + face * new Vector3(0, 0, -0.35f), new Vector3(0.02f, 0.02f, 0.5f), face, mGrey);
                    dishes++;
                }
            }
        }
        Debug.Log($"[detail] wall grime {grime}, shop fronts {shops}, roof rooms {rooms}, rebar stubs {rebar}, dishes {dishes}, rubble {rubble}");
    }

    // ---------- geometry helpers ----------
    static Mesh cubeMesh, cylMesh, sphereMesh, quadMesh;
    static void Meshes()
    {
        if (cubeMesh) return;
        Mesh Prim(PrimitiveType p) { var g = GameObject.CreatePrimitive(p); var m = g.GetComponent<MeshFilter>().sharedMesh; Object.DestroyImmediate(g); return m; }
        cubeMesh = Prim(PrimitiveType.Cube); cylMesh = Prim(PrimitiveType.Cylinder); sphereMesh = Prim(PrimitiveType.Sphere); quadMesh = Prim(PrimitiveType.Quad);
    }
    static Quaternion RandRot() => Quaternion.Euler(R(0, 360), R(0, 360), R(0, 360));
    static void Box(Vector3 c, Vector3 size, Quaternion rot, Material m, bool collide = false) { Meshes(); Add(cubeMesh, Matrix4x4.TRS(c, rot, size), m, collide); }
    static void Cyl(Vector3 c, float r, float h, Quaternion rot, Material m, bool collide = false) { Meshes(); Add(cylMesh, Matrix4x4.TRS(c, rot, new Vector3(2 * r, h / 2, 2 * r)), m, collide); }
    // flat decal on the ground: width across, length along rot's forward
    static void Flat(Vector3 c, float y, float width, float length, Quaternion rot, Material m)
    { Meshes(); Add(quadMesh, Matrix4x4.TRS(new Vector3(c.x, y, c.z), rot * Quaternion.Euler(90, 0, 0), new Vector3(width, length, 1)), m, false); }
    // vertical quad facing back along rot's forward
    static void Vertical(Vector3 c, float width, float height, Quaternion rot, Material m)
    { Meshes(); Add(quadMesh, Matrix4x4.TRS(c, rot, new Vector3(width, height, 1)), m, false); }
    static Vector2[] P(params float[] zy) { var r = new Vector2[zy.Length / 2]; for (int i = 0; i < r.Length; i++) r[i] = new Vector2(zy[2 * i], zy[2 * i + 1]); return r; }

    // ---------- merging ----------
    class Batch { public Material mat; public bool collide; public List<CombineInstance> parts = new(); }
    static readonly Dictionary<(Material, int, int, bool), Batch> batches = new();
    static readonly HashSet<string> NoShadow = new() { "Detail_RoadPaint", "Detail_Crack", "Detail_Patch", "Detail_Manhole", "Detail_SandDrift", "Detail_Crater", "Detail_WallGrime", "Detail_Wire" };
    static void Add(Mesh mesh, Matrix4x4 m, Material mat, bool collide)
    {
        var p = m.GetColumn(3);
        var key = (mat, Mathf.FloorToInt(p.x / 64), Mathf.FloorToInt(p.z / 64), collide);
        if (!batches.TryGetValue(key, out var b)) batches[key] = b = new Batch { mat = mat, collide = collide };
        b.parts.Add(new CombineInstance { mesh = mesh, transform = m, subMeshIndex = 0 });
        counts++;
    }
    static void Flush(GameObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MeshAsset));
        AssetDatabase.DeleteAsset(MeshAsset);
        var holder = new Mesh { name = "CityDetail" };
        AssetDatabase.CreateAsset(holder, MeshAsset);
        int i = 0;
        foreach (var kv in batches)
        {
            var b = kv.Value;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = $"{b.mat.name}_{kv.Key.Item2}_{kv.Key.Item3}{(b.collide ? "_c" : "")}" };
            mesh.CombineMeshes(b.parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            AssetDatabase.AddObjectToAsset(mesh, holder);
            var go = new GameObject(mesh.name); go.transform.SetParent(root.transform, false);
            go.isStatic = true;
            go.layer = NoShadow.Contains(b.mat.name) ? 21 : 20;      // the camera stops drawing these at a distance (ThirdPersonCamera)
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = b.mat;
            if (b.mat.renderQueue >= (int)RenderQueue.Transparent - 50) mr.shadowCastingMode = ShadowCastingMode.Off;
            if (NoShadow.Contains(b.mat.name)) mr.shadowCastingMode = ShadowCastingMode.Off;
            if (b.collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            i++;
        }
        AssetDatabase.SaveAssets();
    }

    // ---------- materials ----------
    static readonly Dictionary<string, Material> matCache = new();
    static Material Mat(string name, string ph = null, string tex = null, Color? c = null, float smooth = 0.3f, float metal = 0f, float tile = 1f, float cutout = 0f, bool transparent = false)
    {
        if (matCache.TryGetValue(name, out var m)) return m;
        var path = $"{Mats}/Detail_{name}.mat";
        AssetDatabase.DeleteAsset(path);
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Detail_" + name };
        if (ph != null)
        {
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_diff_1k.jpg"));
            var nor = AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_nor_gl_1k.jpg");
            if (nor) { m.SetTexture("_BumpMap", nor); m.EnableKeyword("_NORMALMAP"); }
            m.SetTextureScale("_BaseMap", Vector2.one * tile);
        }
        if (tex != null) m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{tex}.png"));
        m.SetColor("_BaseColor", c ?? Color.white); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
        if (cutout > 0) { m.SetFloat("_AlphaClip", 1); m.SetFloat("_Cutoff", cutout); m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue = (int)RenderQueue.AlphaTest; m.SetOverrideTag("RenderType", "TransparentCutout"); }
        if (transparent)
        {
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 0); m.SetFloat("_ZWrite", 0);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One); m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent - 20;
            m.SetShaderPassEnabled("DepthOnly", false); m.SetShaderPassEnabled("ShadowCaster", false);
        }
        AssetDatabase.CreateAsset(m, path);
        matCache[name] = m;
        return m;
    }

    // ---------- generated textures ----------
    static void Textures()
    {
        Directory.CreateDirectory(Gen);
        var t = new System.Random(77);
        float N() => (float)t.NextDouble();
        Write("WornPaint", 256, 256, (x, y) => { float v = Noise(x, y, 18, 3) * 0.7f + Noise(x, y, 5, 9) * 0.3f; return new Color(1, 1, 1, v > 0.32f ? 1 : 0); }, true);
        // cracks: random branching walks
        var crack = new float[512, 512];
        for (int c = 0; c < 6; c++)
        {
            float x = 256 + (N() - 0.5f) * 200, y = 256 + (N() - 0.5f) * 200, ang = N() * 6.28f;
            for (int s = 0; s < 220; s++)
            {
                ang += (N() - 0.5f) * 0.7f; x += Mathf.Cos(ang) * 1.5f; y += Mathf.Sin(ang) * 1.5f;
                if (x < 2 || y < 2 || x > 509 || y > 509) break;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) crack[(int)x + dx, (int)y + dy] = 1;
                if (N() < 0.02f) { float bx = x, by = y, ba = ang + (N() < 0.5f ? 1 : -1) * 0.9f; for (int k = 0; k < 60; k++) { ba += (N() - 0.5f) * 0.6f; bx += Mathf.Cos(ba) * 1.4f; by += Mathf.Sin(ba) * 1.4f; if (bx < 1 || by < 1 || bx > 510 || by > 510) break; crack[(int)bx, (int)by] = 1; } }
            }
        }
        Write("Crack", 512, 512, (x, y) => new Color(0.06f, 0.055f, 0.05f, crack[x, y]), true);
        Write("Patch", 256, 256, (x, y) =>
        {
            float dx = (x - 128) / 128f, dy = (y - 128) / 128f; float r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) + (Noise(x, y, 12, 4) - 0.5f) * 0.25f;
            float g = 0.16f + Noise(x, y, 40, 5) * 0.06f; return new Color(g, g * 0.98f, g * 0.95f, r < 0.85f ? 1 : 0);
        }, false);
        Write("Sand", 64, 256, (x, y) =>
        {
            float u = x / 63f; float a = Mathf.Pow(Mathf.Clamp01(1 - u * 1.1f), 1.5f) * (0.55f + 0.45f * Noise(x, y, 10, 6));
            float g = 0.85f + Noise(x, y, 30, 7) * 0.15f; return new Color(0.74f * g, 0.64f * g, 0.47f * g, a * 0.85f);
        }, false);
        Write("Grime", 128, 128, (x, y) =>
        {
            float v = y / 127f; float streak = 0.6f + 0.4f * Noise(x, 0, 24, 8);
            float a = Mathf.Pow(1 - v, 1.7f) * 0.72f * streak + (v < 0.08f ? 0.15f : 0);
            return new Color(0.2f, 0.16f, 0.12f, Mathf.Clamp01(a));
        }, false);
        Write("Rust", 256, 256, (x, y) =>
        {
            float n1 = Noise(x, y, 16, 10), n2 = Noise(x, y, 64, 11);
            var c = Color.Lerp(new Color(0.24f, 0.12f, 0.06f), new Color(0.55f, 0.28f, 0.12f), n1);
            return Color.Lerp(c, new Color(0.06f, 0.05f, 0.05f), Mathf.Clamp01(n2 * 1.4f - 0.5f));
        }, false, wrap: true);
        Write("Shattered", 256, 256, (x, y) =>
        {
            // web of white cracks over dark glass around two impacts
            float best = 0;
            foreach (var (ix, iy) in new[] { (90, 150), (180, 70) })
            {
                float dx = x - ix, dy = y - iy, rr = Mathf.Sqrt(dx * dx + dy * dy), ang = Mathf.Atan2(dy, dx);
                float rays = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 7 + Noise(x, y, 40, 30) * 2)), 60) * Mathf.Clamp01(1 - rr / 130f);
                float rings = (Mathf.Abs(Mathf.Sin(rr * 0.22f + Noise(x, y, 20, 31) * 3)) > 0.97f ? 1 : 0) * Mathf.Clamp01(1 - rr / 70f);
                best = Mathf.Max(best, Mathf.Max(rays, rings) + (rr < 4 ? 1 : 0));
            }
            var c = Color.Lerp(new Color(0.08f, 0.1f, 0.11f), new Color(0.85f, 0.88f, 0.9f), Mathf.Clamp01(best));
            return new Color(c.r, c.g, c.b, 1);
        }, false);
        Write("NoiseGrey", 128, 128, (x, y) => { float v = 0.85f + Noise(x, y, 32, 12) * 0.25f; return new Color(v, v, v, 1); }, false, wrap: true);
        Write("Shutter", 128, 128, (x, y) =>
        {
            float rib = (y % 8) < 2 ? 0.6f : (y % 8) < 3 ? 1.1f : 0.92f;
            float g = 0.55f * rib * (0.9f + Noise(x, y, 20, 13) * 0.15f);
            float rust = Mathf.Clamp01(Noise(x, y / 4, 10, 14) * 1.6f - 1.0f) * (1 - y / 128f);
            return Color.Lerp(new Color(g, g, g * 1.02f), new Color(0.42f, 0.22f, 0.1f), rust);
        }, false, wrap: true);
        Write("Manhole", 128, 128, (x, y) =>
        {
            float dx = x - 63.5f, dy = y - 63.5f, r = Mathf.Sqrt(dx * dx + dy * dy) / 64f;
            float grid = ((x / 10 + y / 10) % 2 == 0) ? 0.22f : 0.17f; float rim = r > 0.88f ? 0.28f : grid;
            return new Color(rim, rim, rim * 1.05f, r < 0.98f ? 1 : 0);
        }, false);
        Write("Crater", 256, 256, (x, y) =>
        {
            float dx = (x - 128) / 128f, dy = (y - 128) / 128f; float r = Mathf.Sqrt(dx * dx + dy * dy) + (Noise(x, y, 8, 15) - 0.5f) * 0.3f;
            float a = Mathf.Clamp01(1.05f - r) * 0.95f; float g = r < 0.35f ? 0.03f : 0.08f;
            return new Color(g, g * 0.9f, g * 0.8f, a);
        }, false);
        Write("Sign", 256, 64, (x, y) =>
        {
            bool border = x < 6 || x > 249 || y < 5 || y > 58;
            float writing = (y > 18 && y < 46 && x > 30 && x < 226 && Noise(x / 2, y, 6, (x / 40) + 20) > 0.55f) ? 1 : 0;
            float v = border ? 0.75f : writing > 0 ? 1.6f : 1f; return new Color(v, v, v, 1);
        }, false);
        AssetDatabase.Refresh();
    }

    static float Noise(int x, int y, int cell, int seed)
    {
        float fx = x / (float)cell, fy = y / (float)cell; int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
        float H(int a, int b) { uint h = (uint)(a * 374761393 + b * 668265263 + seed * 2246822519); h = (h ^ (h >> 13)) * 1274126177; return (h & 0xffff) / 65535f; }
        float sx = fx - ix, sy = fy - iy; sx = sx * sx * (3 - 2 * sx); sy = sy * sy * (3 - 2 * sy);
        return Mathf.Lerp(Mathf.Lerp(H(ix, iy), H(ix + 1, iy), sx), Mathf.Lerp(H(ix, iy + 1), H(ix + 1, iy + 1), sx), sy);
    }

    static void Write(string name, int w, int h, System.Func<int, int, Color> f, bool cutoutMips, bool wrap = false)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) tex.SetPixel(x, y, f(x, y));
        tex.Apply();
        var path = $"{Gen}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true; ti.wrapMode = wrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        ti.mipmapEnabled = true; ti.mipMapsPreserveCoverage = cutoutMips; ti.alphaTestReferenceValue = 0.45f; ti.anisoLevel = 4;
        ti.SaveAndReimport();
    }
}

// tiny JSON reader: objects -> Dictionary, arrays -> List, numbers -> double
public static class MiniJson
{
    public static object Parse(string s) { int i = 0; return Val(s, ref i); }
    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    static object Val(string s, ref int i)
    {
        Ws(s, ref i);
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); i++;
            while (true) { Ws(s, ref i); if (s[i] == '}') { i++; return d; } var k = Str(s, ref i); Ws(s, ref i); i++; d[k] = Val(s, ref i); Ws(s, ref i); if (s[i] == ',') i++; }
        }
        if (c == '[')
        {
            var l = new List<object>(); i++;
            while (true) { Ws(s, ref i); if (s[i] == ']') { i++; return l; } l.Add(Val(s, ref i)); Ws(s, ref i); if (s[i] == ',') i++; }
        }
        if (c == '"') return Str(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int st = i; while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        return double.Parse(s.Substring(st, i - st), System.Globalization.CultureInfo.InvariantCulture);
    }
    static string Str(string s, ref int i)
    {
        var sb = new System.Text.StringBuilder(); i++;
        while (s[i] != '"')
        {
            if (s[i] == '\\') { i++; char e = s[i]; if (e == 'u') { sb.Append((char)System.Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; } else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e); }
            else sb.Append(s[i]);
            i++;
        }
        i++; return sb.ToString();
    }
}
