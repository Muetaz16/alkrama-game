// The Corniche seafront, modelled on photos of Benghazi's Corniche:
//   beach     dry sand from the sea wall down past the breakwater rocks, wet dark sand sloping under the
//             water (the water shader draws the surf line where they meet)
//   railing   white balustrade on the sea wall (turned balusters, square posts, top rail), with gaps
//             every ~45 m where you can climb over to the beach
//   median    a planted strip with palms and shrubs down the middle of the Corniche road
//   palms     every palm rebuilt: curved ringed trunk, ~22 arching fronds, a skirt of dead fronds on
//             some, crowns that sway in the breeze (replaces the flat web-export palms)
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Seafront.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class Seafront
{
    const string Root = "Assets/Benghazi", PH = Root + "/Textures/PolyHaven", Gen = Root + "/Textures/Generated", Mats = Root + "/Materials";
    const string MeshAsset = Root + "/Detail/Seafront.asset";
    static System.Random rnd;
    static float R() => (float)rnd.NextDouble();
    static float R(float a, float b) => a + (b - a) * R();

    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Build();
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    public static void Build()
    {
        rnd = new System.Random(2011);
        batches.Clear(); mats.Clear();
        var old = GameObject.Find("Seafront"); if (old) Object.DestroyImmediate(old);
        var root = new GameObject("Seafront");
        Textures();
        var map = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        float half = System.Convert.ToSingle(map["half"]);
        Physics.SyncTransforms();

        int beach = Beach(map, half, root.transform);
        int balusters = Railing(map, half);
        var palmSpots = OldPalms();
        int median = Median(map, palmSpots);
        palmSpots.Clear();                       // the owner wants no palms at all (the web palms stay hidden by OldPalms)
        int palms = Palms(palmSpots, root.transform);
        Flush(root);
        Physics.SyncTransforms();
        Debug.Log($"[seafront] beach segments {beach}, balusters {balusters}, median pieces {median}, palms {palms}");
    }

    static Vector3 U(List<object> p) => new Vector3(-System.Convert.ToSingle(p[0]), 0, System.Convert.ToSingle(p[1]));   // web -> Unity

    // coastline runs inside the map, points thinned to >= 1.5 m apart (same as the web game)
    static List<List<Vector3>> CoastRuns(Dictionary<string, object> map, float half)
    {
        var runs = new List<List<Vector3>>();
        if (!map.TryGetValue("coast", out var co)) return runs;
        foreach (List<object> line in (List<object>)co)
        {
            var run = new List<Vector3>();
            foreach (List<object> p in line)
            {
                var v = U(p);
                if (Mathf.Abs(v.x) < half - 2 && Mathf.Abs(v.z) < half - 2) { if (run.Count == 0 || Vector3.Distance(run[^1], v) > 1.5f) run.Add(v); }
                else { if (run.Count > 1) runs.Add(run); run = new List<Vector3>(); }
            }
            if (run.Count > 1) runs.Add(run);
        }
        return runs;
    }

    // inland normal at each point of a run (land is on the left of an OSM coastline; x is mirrored here)
    static Vector3[] Inland(List<Vector3> run)
    {
        var n = new Vector3[run.Count];
        for (int i = 0; i < run.Count; i++)
        {
            var a = run[Mathf.Max(0, i - 1)]; var b = run[Mathf.Min(run.Count - 1, i + 1)];
            var d = (b - a); d.y = 0; d.Normalize();
            n[i] = new Vector3(d.z, 0, -d.x);                       // right of the direction in Unity = left in the web map
        }
        // pick the side that really is land (sample: is there sea a few metres out on the other side?)
        int votes = 0;
        for (int i = 0; i < run.Count; i += Mathf.Max(1, run.Count / 12))
        {
            float yIn = GroundY(run[i] + n[i] * 6f), yOut = GroundY(run[i] - n[i] * 14f);
            if (yIn > yOut + 0.3f) votes++; else if (yOut > yIn + 0.3f) votes--;
        }
        if (votes < 0) for (int i = 0; i < n.Length; i++) n[i] = -n[i];
        return n;
    }

    static float GroundY(Vector3 p)
    {
        float best = -99;
        foreach (var h in Physics.RaycastAll(new Vector3(p.x, 60, p.z), Vector3.down, 90))
            if (!h.collider.name.Contains("palm") && h.point.y > best) best = h.point.y;
        return best;
    }

    // ---------- beach ----------
    static readonly float[] DryOff = { 0.1f, -3f, -8f, -13f, -17f }, DryY = { 0.38f, 0.3f, 0.22f, 0.12f, 0.04f };
    static readonly float[] WetOff = { -17f, -21f, -26f, -32f, -40f }, WetY = { 0.04f, -0.14f, -0.42f, -0.85f, -1.6f };

    static int Beach(Dictionary<string, object> map, float half, Transform root)
    {
        var dry = Mat("Sand", ph: "coast_sand_01", c: new Color(1.02f, 0.94f, 0.80f), smooth: 0.08f);
        var wet = Mat("WetSand", ph: "coast_sand_01", c: new Color(0.62f, 0.55f, 0.44f), smooth: 0.55f);
        int segs = 0;
        foreach (var run in CoastRuns(map, half))
        {
            var n = Inland(run);
            foreach (var (offs, ys, mat, name) in new[] { (DryOff, DryY, dry, "Beach dry"), (WetOff, WetY, wet, "Beach wet") })
            {
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
                int rows = offs.Length;
                for (int i = 0; i < run.Count; i++)
                    for (int k = 0; k < rows; k++)
                    {
                        // the beach is wider in places and narrower in others (rows past the wall foot stretch)
                        float along = i * 2f;
                        float width = 0.75f + 0.55f * Mathf.PerlinNoise(along * 0.018f + 3.1f, 0.5f);
                        float o = offs[k] < -0.5f ? -0.5f + (offs[k] + 0.5f) * width : offs[k];
                        var p = run[i] + n[i] * o;
                        float dune = Mathf.PerlinNoise(p.x * 0.21f, p.z * 0.21f) * 0.12f - 0.06f;
                        p.y = ys[k] + (k == 0 && mat == dry ? 0 : dune);
                        v.Add(p); uv.Add(new Vector2(p.x, p.z) * 0.22f);
                    }
                for (int i = 0; i < run.Count - 1; i++)
                    for (int k = 0; k < rows - 1; k++)
                    {
                        int a = i * rows + k, b = a + 1, c = a + rows, d = c + 1;
                        tri.AddRange(new[] { a, c, b, b, c, d });
                    }
                var m = new Mesh { indexFormat = IndexFormat.UInt32, name = name };
                m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
                m.RecalculateNormals();
                if (m.normals.Length > 0 && m.normals.Average(x => x.y) < 0) { var t = m.triangles; for (int q = 0; q < t.Length; q += 3) (t[q + 1], t[q + 2]) = (t[q + 2], t[q + 1]); m.triangles = t; m.RecalculateNormals(); }
                m.RecalculateTangents(); m.RecalculateBounds();
                Save(m);
                var go = new GameObject(name); go.transform.SetParent(root, false); go.isStatic = true;
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
                go.AddComponent<MeshCollider>().sharedMesh = m;
                segs += run.Count - 1;
            }
        }
        return segs;
    }

    // ---------- railing ----------
    static int Railing(Dictionary<string, object> map, float half)
    {
        var white = Mat("Balustrade", ph: "plastered_wall_02", c: new Color(1.05f, 1.03f, 0.98f), smooth: 0.22f, tile: 0.6f);
        int count = 0;
        foreach (var run in CoastRuns(map, half))
        {
            var n = Inland(run);
            float along = 0; float nextPost = 0;
            for (int i = 0; i < run.Count - 1; i++)
            {
                var a = run[i]; var b = run[i + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 0.2f) continue;
                var f = d / L; var rot = Quaternion.LookRotation(f);
                var na = n[i]; var nb = n[i + 1];
                // the web wall stands 0.3 m inland of the coastline, cap top at 0.95 m
                Vector3 W(float s) { float t = s / L; return Vector3.Lerp(a + na * 0.3f, b + nb * 0.3f, t); }
                for (float s = 0; s < L; s += 0.34f)
                {
                    float g = along + s;
                    if (g % 45f > 41f) continue;                                        // gap to climb down to the beach
                    var p = W(s); if (GroundY(p) > 1.3f) continue;                      // something built over it
                    // turned baluster: base, belly, neck
                    Add(LatheMesh(), Matrix4x4.TRS(p + Vector3.up * 0.95f, Quaternion.Euler(-90, 0, 0), new Vector3(1, 1, 1)), white, false);
                    count++;
                    if (g >= nextPost) { nextPost = g + 4.5f; Box(p + Vector3.up * 1.3f, new Vector3(0.26f, 0.7f, 0.26f), rot, white, true); Box(p + Vector3.up * 1.67f, new Vector3(0.32f, 0.06f, 0.32f), rot, white); }
                }
                // top rail and plinth along the segment, broken at the gaps
                float s0 = 0;
                while (s0 < L)
                {
                    float g0 = along + s0;
                    float gapStart = Mathf.Floor(g0 / 45f) * 45f + 41f;
                    if (g0 % 45f > 41f) { s0 = Mathf.Min(L, s0 + (45f - g0 % 45f)); continue; }
                    float s1 = Mathf.Min(L, s0 + (gapStart - g0));
                    if (s1 - s0 > 0.1f)
                    {
                        var c = (W(s0) + W(s1)) / 2;
                        if (GroundY(c) < 1.3f)
                        {
                            Box(c + Vector3.up * 1.53f, new Vector3(0.22f, 0.1f, s1 - s0 + 0.02f), rot, white, true);
                            Box(c + Vector3.up * 1.0f, new Vector3(0.2f, 0.08f, s1 - s0 + 0.02f), rot, white);
                        }
                    }
                    s0 = s1 + 0.001f;
                }
                along += L;
            }
        }
        return count;
    }

    static Mesh latheMesh;
    static Mesh LatheMesh()
    {
        if (latheMesh) return latheMesh;
        // profile of a classic baluster (radius by height), turned round
        var prof = new (float y, float r)[] { (0, 0.07f), (0.06f, 0.07f), (0.08f, 0.045f), (0.16f, 0.06f), (0.28f, 0.075f), (0.38f, 0.05f), (0.44f, 0.035f), (0.48f, 0.055f), (0.53f, 0.06f), (0.53f, 0f) };
        const int seg = 10;
        var v = new List<Vector3>(); var t = new List<int>(); var uv = new List<Vector2>();
        for (int k = 0; k < prof.Length; k++)
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2;
                v.Add(new Vector3(Mathf.Cos(a) * prof[k].r, Mathf.Sin(a) * prof[k].r, prof[k].y)); uv.Add(new Vector2(i / (float)seg, prof[k].y * 2));
            }
        for (int k = 0; k < prof.Length - 1; k++)
            for (int i = 0; i < seg; i++)
            {
                int a = k * (seg + 1) + i, b = a + 1, c = a + seg + 1, d = c + 1;
                t.AddRange(new[] { a, c, b, b, c, d });
            }
        latheMesh = new Mesh { name = "Baluster" }; latheMesh.SetVertices(v); latheMesh.SetUVs(0, uv); latheMesh.SetTriangles(t, 0);
        latheMesh.RecalculateNormals(); latheMesh.RecalculateBounds();
        return latheMesh;
    }

    // ---------- median on the Corniche road ----------
    static int Median(Dictionary<string, object> map, List<(Vector3 pos, float h, int kind)> palmSpots)
    {
        var kerb = Mat("MedianKerb", ph: "concrete_wall_008", c: new Color(0.9f, 0.89f, 0.85f), smooth: 0.2f);
        var soil = Mat("MedianGrass", tex: "Grass", c: Color.white, smooth: 0.05f);
        var shrub = FrondMat("ShrubLeaves", "FrondGreen");
        var bush = Bush(); Save(bush);
        Vector3 rb = new Vector3(1e6f, 0, 1e6f);
        foreach (Dictionary<string, object> l in (List<object>)map["landmarks"])
            if ((string)l["type"] == "lighthouse" && l.TryGetValue("roundabout", out var o)) rb = U((List<object>)o);
        var roads = new List<(List<Vector3> pts, float half, string name)>();
        foreach (Dictionary<string, object> r in (List<object>)map["roads"])
            roads.Add((((List<object>)r["pts"]).Select(p => U((List<object>)p)).ToList(), System.Convert.ToSingle(r["w"]) / 2 + 1.6f, (string)r["name"]));
        int pieces = 0;
        foreach (var road in roads.Where(x => x.name == "كورنيش بنغازي"))
        {
            float along = 0;
            for (int k = 0; k < road.pts.Count - 1; k++)
            {
                var a = road.pts[k]; var b = road.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 3) { along += L; continue; }
                var f = d / L; var rot = Quaternion.LookRotation(f);
                for (float s = 1.5f; s < L - 1.5f; s += 3f)
                {
                    var c = a + f * s; float g = along + s;
                    if (Vector3.Distance(c, rb) < 34f) continue;                              // the roundabout
                    bool crossing = false;                                                     // another street joins here
                    foreach (var o in roads)
                    {
                        if (o.pts == road.pts) continue;
                        for (int q = 0; q < o.pts.Count - 1 && !crossing; q++) if (SegDist(c, o.pts[q], o.pts[q + 1]) < o.half + 4f) crossing = true;
                        if (crossing) break;
                    }
                    if (crossing) continue;
                    float y = GroundY(c); if (y > 0.5f || y < -0.1f) continue;
                    Box(c + Vector3.up * (y + 0.12f), new Vector3(2.4f, 0.24f, 3.02f), rot, kerb, true);
                    Box(c + Vector3.up * (y + 0.245f), new Vector3(2.05f, 0.02f, 3.02f), rot, soil);
                    for (int i = 0; i < 2; i++)
                    {
                        float sz = R(0.7f, 1.15f);
                        var sp = c + f * R(-1.1f, 1.1f) + Vector3.Cross(Vector3.up, f) * R(-0.5f, 0.5f);
                        Add(bush, Matrix4x4.TRS(sp + Vector3.up * (y + 0.24f), Quaternion.Euler(0, R(0, 360), 0), Vector3.one * sz), shrub, false);
                    }
                    if (Mathf.Repeat(g, 12f) < 3f) palmSpots.Add((c + Vector3.up * (y + 0.24f), R(7f, 10f), 0));
                    pieces++;
                }
                along += L;
            }
        }
        return pieces;
    }

    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var d = b - a; d.y = 0; float L = d.sqrMagnitude; if (L < 1e-6f) return Vector2.Distance(new Vector2(p.x, p.z), new Vector2(a.x, a.z));
        float t = Mathf.Clamp01(((p.x - a.x) * d.x + (p.z - a.z) * d.z) / L);
        return Vector2.Distance(new Vector2(p.x, p.z), new Vector2(a.x + d.x * t, a.z + d.z * t));
    }

    // ---------- palms ----------
    // the web-export palms: remember where they stand (and how tall), then hide them
    static List<(Vector3 pos, float h, int kind)> OldPalms()
    {
        var spots = new List<(Vector3, float, int)>();
        var city = GameObject.Find("Benghazi Corniche"); if (!city) return spots;
        var crowns = new List<(Vector3 c, int kind)>();
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
        {
            string path = ""; for (var t = r.transform; t; t = t.parent) path += t.name + "/";
            if (!path.Contains("palm_")) continue;
            if (path.Contains("palm_trunk")) spots.Add((new Vector3(r.bounds.center.x, r.bounds.min.y, r.bounds.center.z), r.bounds.size.y, -1));
            else crowns.Add((r.bounds.center, path.Contains("palm_burnt") ? 2 : path.Contains("palm_dry") ? 1 : 0));
            r.enabled = false;
            var col = r.GetComponent<Collider>(); if (col) col.enabled = false;
        }
        // which crown sat on which trunk: keeps burnt palms burnt
        for (int i = 0; i < spots.Count; i++)
        {
            int kind = 0; float best = 3f;
            foreach (var c in crowns) { float d = Vector2.Distance(new Vector2(c.c.x, c.c.z), new Vector2(spots[i].Item1.x, spots[i].Item1.z)); if (d < best) { best = d; kind = c.kind; } }
            spots[i] = (spots[i].Item1, Mathf.Max(5.5f, spots[i].Item2), kind);
        }
        return spots;
    }

    static Mesh[][] crownMeshes;    // [kind][variant]
    static int Palms(List<(Vector3 pos, float h, int kind)> spots, Transform root)
    {
        var bark = Mat("PalmBark", tex: "Bark", c: Color.white, smooth: 0.08f);
        var barkN = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/BarkNormal.png");
        if (barkN) { bark.SetTexture("_BumpMap", barkN); bark.SetFloat("_BumpScale", 1.4f); bark.EnableKeyword("_NORMALMAP"); }
        var dates = Mat("Dates", c: new Color(0.85f, 0.45f, 0.1f), smooth: 0.45f);
        var stalk = Mat("DateStalk", c: new Color(0.75f, 0.55f, 0.2f), smooth: 0.2f);
        var collar = Collar(); Save(collar);
        var frond = new[] { FrondMat("PalmFrondGreen", "FrondGreen"), FrondMat("PalmFrondDry", "FrondDry"), FrondMat("PalmFrondBurnt", "FrondBurnt") };
        var boot = Mat("PalmBoot", tex: "Bark", c: new Color(0.55f, 0.42f, 0.3f), smooth: 0.05f);
        crownMeshes = new Mesh[3][];
        for (int k = 0; k < 3; k++) { crownMeshes[k] = new Mesh[3]; for (int v = 0; v < 3; v++) { crownMeshes[k][v] = Crown(k, v); Save(crownMeshes[k][v]); } }
        var skirt = Skirt(); Save(skirt);
        var holder = new GameObject("Palms").transform; holder.SetParent(root, false);
        foreach (var (pos, h, kind) in spots)
        {
            // trunk: tapered, gently curved, ringed bark
            float lean = R(0, 7f), dir = R(0, 360f);
            var trunk = Trunk(h, lean);
            var m = Matrix4x4.TRS(pos, Quaternion.Euler(0, dir, 0), Vector3.one);
            Add(trunk, m, bark, false);
            var top = pos + Quaternion.Euler(0, dir, 0) * TopOffset(h, lean);
            TrunkCapsules(TrunkColliderRoot(root), pos, top);
            Sphere(top + Vector3.down * 0.15f, new Vector3(0.62f, 0.8f, 0.62f), Quaternion.identity, boot);
            Add(collar, Matrix4x4.TRS(top + Vector3.down * 0.25f, Quaternion.Euler(0, R(0, 360), 0), Vector3.one), frond[1], false);
            // hanging bunches of dates on some of the green palms
            if (kind == 0 && R() < 0.4f)
                for (int d = 0, nd = rnd.Next(2, 5); d < nd; d++)
                {
                    var dir2 = Quaternion.Euler(0, R(0, 360), 0) * Vector3.forward;
                    var at = top + dir2 * 0.45f + Vector3.down * 0.55f;
                    Box(at + Vector3.up * 0.25f, new Vector3(0.04f, 0.6f, 0.04f), Quaternion.LookRotation(dir2) * Quaternion.Euler(25, 0, 0), stalk);
                    for (int k = 0; k < 7; k++) Sphere(at + new Vector3(R(-0.16f, 0.16f), R(-0.35f, 0.05f), R(-0.16f, 0.16f)), Vector3.one * R(0.13f, 0.2f), Quaternion.identity, dates);
                }
            // crown: own object so it can sway
            var cg = new GameObject("Palm crown"); cg.transform.SetParent(holder, false);
            cg.transform.SetPositionAndRotation(top, Quaternion.Euler(R(-4, 4), R(0, 360), R(-4, 4)));
            cg.transform.localScale = Vector3.one * R(0.9f, 1.15f);
            cg.AddComponent<MeshFilter>().sharedMesh = crownMeshes[kind][rnd.Next(3)];
            cg.AddComponent<MeshRenderer>().sharedMaterial = frond[kind];
            cg.AddComponent<PalmSway>().amount = kind == 2 ? 0.6f : R(1.2f, 2f);
            if (kind != 2 && R() < 0.45f)
            {
                var sk = new GameObject("Dead fronds"); sk.transform.SetParent(cg.transform, false);
                sk.AddComponent<MeshFilter>().sharedMesh = skirt; sk.AddComponent<MeshRenderer>().sharedMaterial = frond[1];
            }
        }
        return spots.Count;
    }

    // Trunk collision: a few capsules along the curve. The trunk mesh itself is a thin open tube - as a
    // MeshCollider the player's capsule could end up overlapping it (sprinting up the median kerb into a
    // palm) and then not move in any direction.
    const string TrunkColliders = "Palm trunk colliders";
    static Transform TrunkColliderRoot(Transform root)
    {
        var t = root.Find(TrunkColliders);
        if (!t) { t = new GameObject(TrunkColliders).transform; t.SetParent(root, false); }
        return t;
    }
    static void TrunkCapsules(Transform parent, Vector3 pos, Vector3 top)
    {
        const int parts = 4;
        float h = top.y - pos.y; var lean = new Vector3(top.x - pos.x, 0, top.z - pos.z);   // same curve as Trunk(): offset grows with u squared
        Vector3 At(float u) => pos + Vector3.up * (u * h) + lean * (u * u);
        for (int k = 0; k < parts; k++)
        {
            Vector3 a = At(k / (float)parts), b = At((k + 1) / (float)parts);
            float r = Mathf.Lerp(0.3f, 0.22f, (k + 0.5f) / parts);
            var go = new GameObject("Trunk"); go.transform.SetParent(parent, false); go.isStatic = true; go.layer = 20;
            go.transform.SetPositionAndRotation((a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, b - a));
            var c = go.AddComponent<CapsuleCollider>(); c.direction = 1; c.radius = r; c.height = (b - a).magnitude + 2 * r;
        }
    }

    // Swap the trunk collision in the scene as it is, without regenerating the seafront (which would also
    // reset the materials LookPro dresses afterwards):
    //   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Seafront.PalmCollidersOnly
    public static void PalmCollidersOnly()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        var root = GameObject.Find("Seafront"); var holder = root ? root.transform.Find("Palms") : null;
        if (!holder) { Debug.LogError("[seafront] no Seafront/Palms in the scene"); EditorApplication.Exit(1); return; }
        // the trunks are read back from the combined bark meshes: each trunk is (rings + 1) rings of (seg + 1)
        // vertices (see Trunk()), the first ring is its foot and the last its top
        const int ring = 11, perTrunk = 13 * ring;
        var trunks = new List<(Vector3 foot, Vector3 top)>(); var bark = new List<Transform>();
        foreach (Transform t in root.transform)
        {
            if (!t.name.Contains("PalmBark") || !t.TryGetComponent<MeshFilter>(out var mf) || !mf.sharedMesh) continue;
            var v = mf.sharedMesh.vertices;
            if (v.Length == 0 || v.Length % perTrunk != 0) { Debug.LogError($"[seafront] {t.name}: {v.Length} vertices is not whole trunks - run Seafront.Run instead"); EditorApplication.Exit(1); return; }
            Vector3 Centre(int first) { var c = Vector3.zero; for (int i = 0; i < ring - 1; i++) c += v[first + i]; return t.TransformPoint(c / (ring - 1)); }
            for (int o = 0; o < v.Length; o += perTrunk) trunks.Add((Centre(o), Centre(o + perTrunk - ring)));
            bark.Add(t);
        }
        // every trunk must end under a crown, or the meshes are not what this expects
        foreach (var (foot, top) in trunks)
        {
            bool crown = false; foreach (Transform c in holder) if (Vector3.Distance(c.position, top) < 0.05f) { crown = true; break; }
            if (!crown || top.y - foot.y < 5f) { Debug.LogError($"[seafront] trunk at {foot} has no crown on top - run Seafront.Run instead"); EditorApplication.Exit(1); return; }
        }
        if (trunks.Count != holder.childCount) { Debug.LogError($"[seafront] {trunks.Count} trunks but {holder.childCount} crowns - run Seafront.Run instead"); EditorApplication.Exit(1); return; }
        var oldC = root.transform.Find(TrunkColliders); if (oldC) Object.DestroyImmediate(oldC.gameObject);
        int meshColliders = 0;
        foreach (var t in bark) if (t.TryGetComponent<MeshCollider>(out var mc)) { Object.DestroyImmediate(mc); meshColliders++; }
        var parent = TrunkColliderRoot(root.transform);
        foreach (var (foot, top) in trunks) TrunkCapsules(parent, foot, top);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[seafront] trunk colliders: {trunks.Count} palms, {parent.childCount} capsules, {meshColliders} mesh colliders removed");
    }

    static Vector3 TopOffset(float h, float lean) => new Vector3(0, h, 0) + Vector3.forward * Mathf.Tan(lean * Mathf.Deg2Rad) * h * 0.7f;

    static Mesh Trunk(float h, float lean)
    {
        const int rings = 12, seg = 10;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
        for (int k = 0; k <= rings; k++)
        {
            float u = k / (float)rings;
            float r = Mathf.Lerp(0.3f, 0.22f, u) + (u < 0.06f ? (0.06f - u) * 2.4f : 0) + (u > 0.93f ? (u - 0.93f) * 1.6f : 0);   // flared foot, crown shaft
            var c = new Vector3(0, u * h, 0) + Vector3.forward * Mathf.Tan(lean * Mathf.Deg2Rad) * h * 0.7f * u * u;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2;
                v.Add(c + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r)); uv.Add(new Vector2(i / (float)seg, u * h * 1.6f));
            }
        }
        for (int k = 0; k < rings; k++)
            for (int i = 0; i < seg; i++)
            {
                int a = k * (seg + 1) + i, b = a + 1, c = a + seg + 1, d = c + 1;
                t.AddRange(new[] { a, b, c, b, d, c });
            }
        var m = new Mesh { name = "PalmTrunk" }; m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0);
        m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
        return m;
    }

    // one frond: a strip along a spine that rises then droops, folded in a shallow V along the midrib
    static void Frond(List<Vector3> v, List<Vector2> uv, List<Vector3> nrm, List<int> t, float yaw, float elev, float len, float width, float droop)
    {
        const int seg = 10;
        var dirH = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        var side = Quaternion.Euler(0, yaw, 0) * Vector3.right;
        Vector3 p = Vector3.zero; var tan = Quaternion.AngleAxis(-elev, side) * dirH;
        int b = v.Count;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg;
            float wd = width * Mathf.Sin(Mathf.Min(1, u * 1.15f + 0.05f) * Mathf.PI) * (1 - u * 0.35f);
            var up = Vector3.Cross(side, tan).normalized;
            var sd = Quaternion.AngleAxis(u * 28f, tan) * side;                      // the frond twists as it goes out
            v.Add(p - sd * wd + up * wd * 0.42f); uv.Add(new Vector2(0, u));
            v.Add(p + up * 0.02f); uv.Add(new Vector2(0.5f, u));
            v.Add(p + sd * wd + up * wd * 0.42f); uv.Add(new Vector2(1, u));
            nrm.Add(up); nrm.Add(up); nrm.Add(up);
            // gravity bends the frond down more and more towards the tip
            tan = Quaternion.AngleAxis(droop * (0.4f + u) * 1.1f, side) * tan;
            p += tan * (len / seg);
        }
        for (int i = 0; i < seg; i++)
            for (int j = 0; j < 2; j++)
            {
                int a = b + i * 3 + j, c = a + 3;
                t.AddRange(new[] { a, c, a + 1, a + 1, c, c + 1 });
            }
    }

    static Mesh Crown(int kind, int variant)
    {
        var r2 = new System.Random(100 + kind * 10 + variant);
        float Rr(float a, float b) => a + (b - a) * (float)r2.NextDouble();
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var t = new List<int>();
        int count = kind == 2 ? 12 : kind == 1 ? 22 : 34;
        for (int i = 0; i < count; i++)
        {
            // phyllotaxis spiral; four tiers from young upright spears to old fronds hanging below the horizon
            float tier = (i % 4) / 3f;
            float elev = Mathf.Lerp(74f, -22f, tier) + Rr(-8, 8);
            float len = Rr(3.4f, 4.6f) * (kind == 2 ? 0.65f : 1f) * (1 - tier * 0.12f) * (tier < 0.1f ? 0.8f : 1f);
            float droop = Rr(5f, 8f) + tier * 9f;
            Frond(v, uv, n, t, i * 137.5f + Rr(-8, 8), elev, len, Rr(0.5f, 0.72f) * (tier < 0.1f ? 0.6f : 1f), droop);
        }
        var m = new Mesh { name = $"PalmCrown_{kind}_{variant}" };
        m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    // fibrous collar of old leaf stubs right under the crown
    static Mesh Collar()
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var t = new List<int>();
        for (int i = 0; i < 18; i++) Frond(v, uv, n, t, i * 20f, (i % 2 == 0 ? 25f : 50f), 0.55f, 0.11f, 2f);
        var m = new Mesh { name = "PalmCollar" }; m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    // low leafy plant for the planters: short fronds fanning up and out from the soil
    static Mesh Bush()
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var t = new List<int>();
        var r2 = new System.Random(77);
        for (int i = 0; i < 16; i++)
            Frond(v, uv, n, t, i * 137.5f + (float)r2.NextDouble() * 20f, 35f + (float)r2.NextDouble() * 40f, 0.8f + (float)r2.NextDouble() * 0.5f, 0.22f, 9f);
        var m = new Mesh { name = "Bush" }; m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    // dead fronds hanging down against the trunk under the crown
    static Mesh Skirt()
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var t = new List<int>();
        for (int i = 0; i < 9; i++) Frond(v, uv, n, t, i * 40f + (float)rnd.NextDouble() * 15f, -62f, 2.4f, 0.3f, 1.5f);
        var m = new Mesh { name = "PalmSkirt" }; m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    static Material FrondMat(string name, string tex)
    {
        var m = Mat(name, tex: tex, c: Color.white, smooth: 0.18f);
        m.SetFloat("_AlphaClip", 1); m.SetFloat("_Cutoff", 0.42f); m.EnableKeyword("_ALPHATEST_ON");
        m.SetFloat("_Cull", 0); m.doubleSidedGI = true; m.renderQueue = (int)RenderQueue.AlphaTest; m.SetOverrideTag("RenderType", "TransparentCutout");
        return m;
    }

    // ---------- merging (per material, per 64 m cell) ----------
    static Mesh cube, sphere;
    static void Prims()
    {
        if (cube) return;
        Mesh P(PrimitiveType p) { var g = GameObject.CreatePrimitive(p); var m = g.GetComponent<MeshFilter>().sharedMesh; Object.DestroyImmediate(g); return m; }
        cube = P(PrimitiveType.Cube); sphere = P(PrimitiveType.Sphere);
    }
    static void Box(Vector3 c, Vector3 size, Quaternion rot, Material m, bool collide = false) { Prims(); Add(cube, Matrix4x4.TRS(c, rot, size), m, collide); }
    static void Sphere(Vector3 c, Vector3 size, Quaternion rot, Material m) { Prims(); Add(sphere, Matrix4x4.TRS(c, rot, size), m, false); }
    class Batch { public Material mat; public bool collide; public List<CombineInstance> parts = new(); }
    static readonly Dictionary<(Material, int, int, bool), Batch> batches = new();
    static void Add(Mesh mesh, Matrix4x4 m, Material mat, bool collide)
    {
        var p = m.GetColumn(3);
        var key = (mat, Mathf.FloorToInt(p.x / 64), Mathf.FloorToInt(p.z / 64), collide);
        if (!batches.TryGetValue(key, out var b)) batches[key] = b = new Batch { mat = mat, collide = collide };
        b.parts.Add(new CombineInstance { mesh = mesh, transform = m });
    }
    static Mesh holderMesh;
    static void Save(Mesh m)
    {
        if (!holderMesh)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MeshAsset));
            AssetDatabase.DeleteAsset(MeshAsset);
            holderMesh = new Mesh { name = "Seafront" };
            AssetDatabase.CreateAsset(holderMesh, MeshAsset);
        }
        AssetDatabase.AddObjectToAsset(m, holderMesh);
    }
    static void Flush(GameObject root)
    {
        foreach (var kv in batches)
        {
            var b = kv.Value;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = $"{b.mat.name}_{kv.Key.Item2}_{kv.Key.Item3}{(b.collide ? "_c" : "")}" };
            mesh.CombineMeshes(b.parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            Save(mesh);
            var go = new GameObject(mesh.name); go.transform.SetParent(root.transform, false); go.isStatic = true; go.layer = 20;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = b.mat;
            if (b.collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        holderMesh = null;
        AssetDatabase.SaveAssets();
    }

    // ---------- materials and textures ----------
    static readonly Dictionary<string, Material> mats = new();
    static Material Mat(string name, string ph = null, string tex = null, Color? c = null, float smooth = 0.3f, float tile = 1f)
    {
        if (mats.TryGetValue(name, out var m)) return m;
        var path = $"{Mats}/Seafront_{name}.mat";
        AssetDatabase.DeleteAsset(path);
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Seafront_" + name };
        if (ph != null)
        {
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_diff_1k.jpg"));
            var nor = AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_nor_gl_1k.jpg");
            if (nor) { m.SetTexture("_BumpMap", nor); m.EnableKeyword("_NORMALMAP"); }
            m.SetTextureScale("_BaseMap", Vector2.one * tile);
        }
        if (tex != null) m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{tex}.png"));
        m.SetColor("_BaseColor", c ?? Color.white); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", 0);
        AssetDatabase.CreateAsset(m, path);
        mats[name] = m;
        return m;
    }

    static void Textures()
    {
        // overlapping leaf-base scars: each a rounded knob, rows offset, darker fibrous grooves between
        float BarkH(int x, int y)
        {
            float u = x / 256f * 6f, v = y / 512f * 12f;
            float row = Mathf.Floor(v); float uu = u + (row % 2) * 0.5f;
            float fx = Mathf.Repeat(uu, 1f) - 0.5f, fy = Mathf.Repeat(v, 1f) - 0.5f;
            float knob = Mathf.Clamp01(1 - (fx * fx * 3.2f + fy * fy * 5f) * 2.6f);
            return knob * 0.8f + Noise(x, y, 5, 3) * 0.12f + Noise(x, y, 2, 13) * 0.08f;
        }
        Write("Bark", 256, 512, (x, y) =>
        {
            float hgt = BarkH(x, y);
            float n = Noise(x, y, 14, 4) * 0.2f;
            return Color.Lerp(new Color(0.2f, 0.16f, 0.12f), new Color(0.6f, 0.52f, 0.42f), Mathf.Clamp01(hgt * 0.85f + n));
        }, true);
        WriteNormal("BarkNormal", 256, 512, BarkH, 3.5f);
        Write("Grass", 128, 128, (x, y) =>
        {
            float n = Noise(x, y, 8, 5) * 0.6f + Noise(x, y, 2, 6) * 0.4f;
            var c = Color.Lerp(new Color(0.24f, 0.3f, 0.12f), new Color(0.48f, 0.5f, 0.24f), n);
            if (Noise(x, y, 14, 7) > 0.72f) c = Color.Lerp(c, new Color(0.55f, 0.46f, 0.32f), 0.6f);   // bare dry patches
            return c;
        }, true);
        Write("Shrub", 128, 128, (x, y) =>
        {
            float n = Noise(x, y, 4, 8) * 0.7f + Noise(x, y, 1, 9) * 0.3f;
            return Color.Lerp(new Color(0.1f, 0.2f, 0.07f), new Color(0.32f, 0.44f, 0.16f), n);
        }, true);
    }

    static float Noise(int x, int y, int cell, int seed)
    {
        float fx = x / (float)cell, fy = y / (float)cell; int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
        float H(int a, int b) { uint h = (uint)(a * 374761393 + b * 668265263 + seed * 2246822519); h = (h ^ (h >> 13)) * 1274126177; return (h & 0xffff) / 65535f; }
        float sx = fx - ix, sy = fy - iy; sx = sx * sx * (3 - 2 * sx); sy = sy * sy * (3 - 2 * sy);
        return Mathf.Lerp(Mathf.Lerp(H(ix, iy), H(ix + 1, iy), sx), Mathf.Lerp(H(ix, iy + 1), H(ix + 1, iy + 1), sx), sy);
    }

    static void WriteNormal(string name, int w, int h, System.Func<int, int, float> height, float strength)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float dx = height((x + 1) % w, y) - height((x + w - 1) % w, y), dy = height(x, (y + 1) % h) - height(x, (y + h - 1) % h);
            var nn = new Vector3(-dx * strength, -dy * strength, 1).normalized;
            tex.SetPixel(x, y, new Color(nn.x * 0.5f + 0.5f, nn.y * 0.5f + 0.5f, nn.z * 0.5f + 0.5f, 1));
        }
        tex.Apply();
        var path = $"{Gen}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = TextureImporterType.NormalMap; ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true; ti.SaveAndReimport();
    }

    static void Write(string name, int w, int h, System.Func<int, int, Color> f, bool wrap)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var c = f(x, y); c.a = 1; tex.SetPixel(x, y, c); }
        tex.Apply();
        var path = $"{Gen}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.wrapMode = wrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp; ti.mipmapEnabled = true; ti.anisoLevel = 4;
        ti.SaveAndReimport();
    }
}
