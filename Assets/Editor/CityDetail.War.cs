// Second half of CityDetail: the war on the buildings of Sabri, the lighthouse grounds, and the air of the city.
// Same placement rules (ray casts against the real city) and the same merged batches as CityDetail.cs.
//
//   facades   concrete balconies with railings in stacks on the window columns (the facade tiles are 4 m x 3.2 m,
//             so the window columns are known), split AC units, drain pipes, wires across the streets;
//             war damage: bullet sprays, shell holes showing the block wall, soot over burnt windows, Arabic
//             graffiti at street level (plain words only); on ruined blocks floor slabs hanging off the walls
//             with their rebar, and heaps of rubble against the walls
//   streets   sand blown over the side streets, bricks, chunks and paper along the walls
//   lighthouse  the tall square podium of the web city is replaced by a low weathered stone plinth with steps
//             to the sea, the tower keeps its old scanned surface and colour; the Berenice digs around it are rebuilt
//             as walls of laid stone blocks with dry grass, and the Sidi Khrebish zawiya with its minaret stands nearby
//   air       an Atmosphere object (dust in the sunlight, sand gusts, smoke columns over burning ruins)
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static partial class CityDetail
{
    const string PH2 = Root + "/Textures/PolyHaven2k";
    const float FloorH = 3.2f, TileW = 4f;            // web facade tiles: one window per 4 m, one floor per 3.2 m

    // The tower stands this far back from the map spot, away from the sea: open ground between it and the
    // coast road, as in the photos. 0 = exactly on the OSM spot.
    public const float LighthouseSetBack = 10f;
    const float PlinthH = 0.9f, PlinthSize = 25f;

    static readonly HashSet<string> FarVisible = new();       // merged meshes that stay drawn at any distance
    static readonly List<List<Vector3>> ruinsPolys = new();
    static readonly List<Vector3> smokeSpots = new();
    public static readonly List<(Vector3 eye, Vector3 at, string name)> ReviewViews = new();   // street views for the review shots
    static readonly List<(Vector3 c, float r)> keepOut = new();   // no street detail here (Ground() checks it)
    static Vector3 lhAxis = Vector3.forward;

    // ---------------------------------------------------------------- lighthouse spot (also used by SceneBuilder)
    static Vector3? seaCache, axisCache;
    static void ReadLighthouse()
    {
        if (seaCache.HasValue) return;
        var map = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        Vector3 sea = Vector3.forward, axis = Vector3.forward;
        foreach (Dictionary<string, object> l in (List<object>)map["landmarks"])
        {
            if ((string)l["type"] != "lighthouse") continue;
            if (l.TryGetValue("seaDir", out var sd)) { var s = (List<object>)sd; sea = new Vector3(-System.Convert.ToSingle(s[0]), 0, System.Convert.ToSingle(s[1])).normalized; }
            if (l.TryGetValue("poly", out var po))
            {
                var p = (List<object>)po; var a = (List<object>)p[0]; var b = (List<object>)p[1];
                axis = new Vector3(-(System.Convert.ToSingle(b[0]) - System.Convert.ToSingle(a[0])), 0, System.Convert.ToSingle(b[1]) - System.Convert.ToSingle(a[1])).normalized;
            }
        }
        seaCache = sea; axisCache = axis;
    }
    public static Vector3 SeaDirection() { ReadLighthouse(); return seaCache.Value; }
    public static Vector3 LighthouseShift() => -SeaDirection() * LighthouseSetBack;

    static readonly List<Vector3> coastPts = new();
    static void LoadWarData(Dictionary<string, object> map)
    {
        ruinsPolys.Clear(); coastPts.Clear();
        if (map.TryGetValue("coast", out var co))
            foreach (var run in (List<object>)co)
                foreach (var q in (List<object>)run)
                { var v = (List<object>)q; var p = new Vector3(-System.Convert.ToSingle(v[0]), 0, System.Convert.ToSingle(v[1])); if (p.magnitude < 1500f) coastPts.Add(p); }
        foreach (Dictionary<string, object> l in (List<object>)map["landmarks"])
            if ((string)l["type"] == "ruins" && l.TryGetValue("poly", out var po))
                ruinsPolys.Add(((List<object>)po).Select(q => { var v = (List<object>)q; return new Vector3(-System.Convert.ToSingle(v[0]), 0, System.Convert.ToSingle(v[1])); }).ToList());
        ReadLighthouse(); lhAxis = axisCache.Value;
    }

    static bool Inside(List<Vector3> poly, Vector3 p)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z + 1e-6f) + a.x) c = !c;
        }
        return c;
    }
    static bool InRuins(Vector3 p) => ruinsPolys.Any(r => Inside(r, p));

    // ---------------------------------------------------------------- materials
    static Material Mat2(string name, string ph2, Color c, float smooth, float tile = 1f)
    {
        var m = Mat(name, c: c, smooth: smooth);
        var d = AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH2}/{ph2}_diff_2k.jpg");
        var n = AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH2}/{ph2}_nor_gl_2k.jpg");
        if (d) m.SetTexture("_BaseMap", d);
        if (n) { m.SetTexture("_BumpMap", n); m.EnableKeyword("_NORMALMAP"); }
        m.SetTextureScale("_BaseMap", Vector2.one * tile);
        EditorUtility.SetDirty(m);
        return m;
    }
    static Material TwoSided(Material m) { m.SetFloat("_Cull", 0); m.doubleSidedGI = true; EditorUtility.SetDirty(m); return m; }

    static void WarTextures()
    {
        var t = new System.Random(2011);
        float N() => (float)t.NextDouble();

        // shell hole: black inside, a ring of broken block wall, chipped plaster round it
        Write("ShellHole", 256, 256, (x, y) =>
        {
            float dx = (x - 127.5f) / 128f, dy = (y - 127.5f) / 128f, r = Mathf.Sqrt(dx * dx + dy * dy), an = Mathf.Atan2(dy, dx);
            float wob = 0.05f * Mathf.Sin(an * 3 + 1.3f) + 0.03f * Mathf.Sin(an * 7 + 0.4f) + (Noise(x, y, 14, 40) - 0.5f) * 0.14f + (Noise(x, y, 4, 39) - 0.5f) * 0.05f;
            float rr = r + wob;
            if (rr < 0.3f) { float g = 0.03f + 0.05f * Noise(x, y, 6, 41) + 0.06f * Mathf.Clamp01((rr - 0.2f) * 10); return new Color(g, g * 0.95f, g * 0.9f, 1); }
            if (rr < 0.58f)
            {
                bool joint = (y % 22) < 3 || ((x + ((y / 22) % 2) * 20) % 40) < 3;               // block courses
                bool brick = Noise(x, y, 30, 42) > 0.55f;
                var c = joint ? new Color(0.36f, 0.33f, 0.29f) : brick ? new Color(0.58f, 0.34f, 0.24f) : new Color(0.55f, 0.53f, 0.49f);
                c *= 0.75f + 0.35f * Noise(x, y, 5, 43) - (0.6f - rr) * 0.6f;
                return new Color(c.r, c.g, c.b, 1);
            }
            if (rr < 0.92f)
            {
                float chip = Noise(x, y, 7, 44); float a = chip > (rr - 0.58f) * 2.8f ? 1 : 0;
                float g = 0.5f + 0.18f * Noise(x, y, 4, 45) - (0.92f - rr) * 0.15f;                // broken plaster edge, a shade darker than the wall
                return new Color(g * 1.02f, g * 0.96f, g * 0.86f, a);
            }
            return new Color(0, 0, 0, 0);
        }, true);

        // bullet spray: pits with a pale chipped ring, dense in the middle of the burst
        var pocks = new Color[256, 256];
        for (int i = 0; i < 140; i++)
        {
            float a = N() * 6.283f, d = Mathf.Pow(N(), 0.6f) * 120;
            int cx = (int)(128 + Mathf.Cos(a) * d), cy = (int)(128 + Mathf.Sin(a) * d);
            float pit = 1.2f + N() * 1.8f, chip = pit + 1 + N() * 3.5f;
            for (int y = -8; y <= 8; y++) for (int x = -8; x <= 8; x++)
            {
                int px = cx + x, py = cy + y; if (px < 0 || py < 0 || px > 255 || py > 255) continue;
                float rr = Mathf.Sqrt(x * x + y * y) + (Noise(px, py, 2, 46) - 0.5f) * 3.5f;     // ragged, not round
                if (rr < pit) pocks[px, py] = new Color(0.07f, 0.065f, 0.06f, 0.95f);
                else if (rr < chip && pocks[px, py].a < 1) { float g = 0.42f + 0.16f * Noise(px, py, 2, 47); pocks[px, py] = new Color(g * 0.8f, g * 0.76f, g * 0.68f, 0.45f); }   // bare concrete under the plaster, half blended into the wall
            }
        }
        Write("Pocks", 256, 256, (x, y) => pocks[x, y], true);

        // soot above a burnt window: dense at the bottom, rising and spreading
        Write("Soot", 128, 256, (x, y) =>
        {
            float v = y / 255f, u = (x - 63.5f) / 64f;
            float width = 0.35f + v * 0.65f + (Noise(x, y, 16, 48) - 0.5f) * 0.4f;
            float a = Mathf.Clamp01(1 - Mathf.Abs(u) / width) * Mathf.Pow(1 - v, 0.8f) * (0.55f + 0.45f * Noise(x, y, 12, 49));
            return new Color(0.05f, 0.045f, 0.04f, Mathf.Clamp01(a * 1.3f));
        }, false);

        // balcony railing: top and bottom rails, square bars, a little rust
        Write("Railing", 256, 64, (x, y) =>
        {
            bool rail = y > 56 || y < 5 || (y > 27 && y < 31);
            bool bar = (x % 16) < 3;
            bool frame = x < 4 || x > 251;
            if (!(rail || bar || frame)) return new Color(0, 0, 0, 0);
            float rust = Noise(x, y, 8, 50);
            var c = Color.Lerp(new Color(0.16f, 0.17f, 0.17f), new Color(0.36f, 0.2f, 0.12f), Mathf.Clamp01(rust * 1.6f - 0.6f));
            return new Color(c.r, c.g, c.b, 1);
        }, true, wrap: true);

        // dry grass tuft
        var grass = new Color[128, 128];
        for (int b = 0; b < 70; b++)
        {
            float x0 = 20 + N() * 88, lean = (N() - 0.5f) * 0.9f, hgt = 50 + N() * 75; var col = Color.Lerp(new Color(0.62f, 0.55f, 0.36f), new Color(0.45f, 0.4f, 0.25f), N());
            for (int s = 0; s < hgt; s++)
            {
                float f = s / hgt; int px = (int)(x0 + lean * s + Mathf.Sin(f * 3) * 2), py = s; if (px < 1 || px > 126 || py > 127) break;
                grass[px, py] = new Color(col.r * (0.8f + 0.3f * f), col.g * (0.8f + 0.3f * f), col.b, 1);
                if (f < 0.6f) grass[px + 1, py] = grass[px, py];
            }
        }
        Write("DryGrass", 128, 128, (x, y) => grass[x, y], true);

        // rubble: broken concrete, block and brick in a tumble
        var pts = Enumerable.Range(0, 260).Select(_ => new Vector2(N() * 256, N() * 256)).ToArray();
        var cols = pts.Select(_ => { float k = N(); return k < 0.15f ? new Color(0.46f, 0.3f, 0.22f) : k < 0.55f ? new Color(0.5f, 0.48f, 0.44f) : new Color(0.6f, 0.56f, 0.48f); }).ToArray();
        Write("Rubble", 256, 256, (x, y) =>
        {
            float b1 = 1e9f, b2 = 1e9f; int bi = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                float dx = Mathf.Abs(x - pts[i].x); dx = Mathf.Min(dx, 256 - dx); float dy = Mathf.Abs(y - pts[i].y); dy = Mathf.Min(dy, 256 - dy);
                float d = dx * dx + dy * dy; if (d < b1) { b2 = b1; b1 = d; bi = i; } else if (d < b2) b2 = d;
            }
            float edge = Mathf.Sqrt(b2) - Mathf.Sqrt(b1);
            var c = cols[bi] * (0.75f + 0.35f * Noise(x, y, 6, 51));
            c *= 0.8f + 0.25f * Mathf.Clamp01(edge / 6f);                                 // pieces a little rounded
            if (edge < 1.6f) c *= 0.3f;                                                   // the gaps between pieces
            c = Color.Lerp(c, new Color(0.62f, 0.56f, 0.45f), Noise(x, y, 40, 52) * 0.45f);   // dust over everything
            return new Color(c.r, c.g, c.b, 1);
        }, false, wrap: true);

        // blown sand over asphalt: soft, uneven edge
        Write("SandSheet", 256, 256, (x, y) =>
        {
            float dx = (x - 127.5f) / 128f, dy = (y - 127.5f) / 128f, r = Mathf.Sqrt(dx * dx + dy * dy) + (Noise(x, y, 40, 53) - 0.5f) * 0.6f;
            float a = Mathf.Clamp01((0.95f - r) * 2.2f) * (0.45f + 0.55f * Noise(x, y, 14, 54));
            float g = 0.85f + 0.15f * Noise(x, y, 5, 55);
            return new Color(0.66f * g, 0.57f * g, 0.42f * g, a * 0.75f);
        }, false);

        // split AC unit front: fan grille on the right, louvres on the left
        Write("AcFront", 128, 64, (x, y) =>
        {
            float g = 0.82f + 0.08f * Noise(x, y, 10, 56) - (y < 10 ? 0.15f * Noise(x, 0, 6, 57) : 0);   // dirt streaks at the bottom
            float dx = x - 88, dy = y - 32, r = Mathf.Sqrt(dx * dx + dy * dy);
            if (r < 24) g = r > 22 ? 0.35f : ((int)r % 4 < 1 || Mathf.Abs(dx) < 1.2f || Mathf.Abs(dy) < 1.2f) ? 0.5f : 0.12f;
            else if (x < 52 && x > 8 && y > 10 && y < 54 && y % 6 < 2) g *= 0.7f;
            return new Color(g, g, g * 0.97f, 1);
        }, false);
        AssetDatabase.Refresh();
        var gi = (TextureImporter)AssetImporter.GetAtPath(Gen + "/Graffiti.png");
        if (gi && !gi.alphaIsTransparency) { gi.alphaIsTransparency = true; gi.wrapMode = TextureWrapMode.Clamp; gi.SaveAndReimport(); }
    }

    // ---------------------------------------------------------------- meshes made here
    static Mesh heapMesh, tuftMesh; static Mesh[] graffitiQuads; static Mesh octMesh, octCone;
    static void WarMeshes()
    {
        // a heap: a noisy dome 1 x 1 x 1 standing on y = 0
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        const int n = 20;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n * 2 - 1, w = j / (float)n * 2 - 1, r = Mathf.Sqrt(u * u + w * w);
            float h = Mathf.Clamp01(1 - r * r); h = Mathf.Sqrt(h) * Mathf.Clamp01((1 - r) * 3f);        // rounded top, slopes out to the edge
            float lump = (Mathf.PerlinNoise(i * 0.7f + 3, j * 0.7f + 7) - 0.5f) * 0.3f * h + (Mathf.PerlinNoise(i * 1.9f, j * 1.9f) - 0.5f) * 0.1f * h;
            v.Add(new Vector3(u * 0.5f, Mathf.Max(0, h + lump) - 0.05f, w * 0.5f)); uv.Add(new Vector2(u * 2.5f, w * 2.5f));
        }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            tri.AddRange(new[] { a, c, b, b, c, d });
        }
        heapMesh = new Mesh { name = "Heap" }; heapMesh.SetVertices(v); heapMesh.SetUVs(0, uv); heapMesh.SetTriangles(tri, 0); heapMesh.RecalculateNormals(); heapMesh.RecalculateBounds();

        graffitiQuads = new Mesh[8];
        for (int k = 0; k < 8; k++)
        {
            float u0 = (k % 4) / 4f, u1 = u0 + 0.25f, v1 = 1 - (k / 4) * 0.5f, v0 = v1 - 0.5f;
            var m = new Mesh { name = "Graffiti" + k };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            m.uv = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) };
            m.triangles = new[] { 0, 3, 1, 3, 0, 2 }; m.RecalculateNormals(); m.RecalculateBounds();
            graffitiQuads[k] = m;
        }
        // a tuft: two crossed quads 1 x 1 standing on y = 0, normals up so the sun lights both sides
        tuftMesh = new Mesh { name = "Tuft" };
        var tv = new List<Vector3>(); var tu = new List<Vector2>(); var tt = new List<int>();
        foreach (var dir in new[] { Vector3.right, Vector3.forward })
        {
            int b0 = tv.Count;
            tv.Add(-dir * 0.5f); tv.Add(dir * 0.5f); tv.Add(-dir * 0.5f + Vector3.up); tv.Add(dir * 0.5f + Vector3.up);
            tu.Add(new Vector2(0, 0)); tu.Add(new Vector2(1, 0)); tu.Add(new Vector2(0, 1)); tu.Add(new Vector2(1, 1));
            tt.AddRange(new[] { b0, b0 + 2, b0 + 1, b0 + 1, b0 + 2, b0 + 3 });
        }
        tuftMesh.SetVertices(tv); tuftMesh.SetUVs(0, tu); tuftMesh.SetTriangles(tt, 0);
        tuftMesh.SetNormals(Enumerable.Repeat(Vector3.up, tv.Count).ToList()); tuftMesh.RecalculateBounds();

        // an octagonal prism and cone, 1 m tall, radius 1, standing on y = 0 (GunFactory.Lathe runs along z)
        var stand = Matrix4x4.Rotate(Quaternion.Euler(-90, 0, 0));
        Mesh Up(Mesh src) { var m = Object.Instantiate(src); m.vertices = src.vertices.Select(p => stand.MultiplyPoint3x4(p)).ToArray(); m.normals = src.normals.Select(p => stand.MultiplyVector(p)).ToArray(); m.RecalculateBounds(); return m; }
        octMesh = Up(GunFactory.Lathe(0, 1, 1, 1, 0, 8));
        octCone = Up(GunFactory.Lathe(0, 1, 1, 0.02f, 0, 8));
    }

    // ---------------------------------------------------------------- the lighthouse grounds
    static void LighthouseGrounds()
    {
        var city = GameObject.Find("Benghazi Corniche");
        if (!city) { Debug.LogWarning("[war] no city in the scene - lighthouse grounds skipped"); return; }
        var map = SceneBuilder.FindLighthouseMap(); var at = map + LighthouseShift(); at.y = 0;
        var sea = SeaDirection();

        // 1. what the web city put there: the 44 m podium, its parapet ring and the white steps -> off
        //    (renderer and collider only: the tower may hang under the same node)
        int off = 0; var tower = new List<Transform>(); var tb = new Bounds(); bool any = false;
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>())
        {
            if (!r.enabled) continue;
            var b = r.bounds; var c = b.center;
            float dMap = new Vector2(c.x - map.x, c.z - map.z).magnitude, dAt = new Vector2(c.x - at.x, c.z - at.z).magnitude;
            if (b.size.y > 14f && (dMap < 12f || dAt < 12f)) { tower.Add(r.transform); if (!any) { tb = b; any = true; } else tb.Encapsulate(b); continue; }
            bool podium = dMap < 5f && b.size.x > 36f && b.size.z > 36f && b.size.y < 3f;
            bool steps = dMap < 24f && b.max.y < 2.3f && b.min.y > -0.15f && Mathf.Max(b.size.x, b.size.z) > 6f && b.size.y > 0.2f;
            if (!podium && !steps) continue;
            r.enabled = false; if (r.TryGetComponent<Collider>(out var col)) col.enabled = false; off++;
        }
        // 2. the tower (its own scanned surface and colour are kept): onto the new plinth, set back from the road
        if (any)
        {
            var delta = new Vector3(at.x - tb.center.x, PlinthH - tb.min.y, at.z - tb.center.z);
            foreach (var t in tower) if (!tower.Any(o => o != t && t.IsChildOf(o))) t.position += delta;
            Debug.Log($"[war] lighthouse tower: {tower.Count} parts moved by {delta} (was {tb.size.x:F1} x {tb.size.y:F1} x {tb.size.z:F1} m at {tb.center})");
        }
        else Debug.LogWarning("[war] lighthouse tower not found near " + map);
        Physics.SyncTransforms();

        // 3. the plinth: weathered stone blocks, a cornice, paving on top, a broken low parapet, steps to the sea
        var q = Quaternion.LookRotation(lhAxis);
        var toSea = q * Vector3.forward; foreach (var dir in new[] { q * Vector3.back, q * Vector3.right, q * Vector3.left }) if (Vector3.Dot(dir, sea) > Vector3.Dot(toSea, sea)) toSea = dir;
        var qs = Quaternion.LookRotation(toSea);                                           // forward = towards the sea
        keepOut.Add((at, PlinthSize * 0.72f + 1.5f));
        var mBlock = Mat("LighthouseBase_Stone", ph: "rock_boulder_dry", c: new Color(1f, 0.9f, 0.74f), smooth: 0.05f, tile: 0.6f);
        var mPave = Mat2("LighthouseBase_Paving", "precast_stone_paving", new Color(0.82f, 0.77f, 0.68f), 0.08f, 6f);
        var mStone = Mat("RuinStone", ph: "rock_boulder_dry", c: new Color(1f, 0.9f, 0.72f), smooth: 0.05f, tile: 0.5f);
        FarVisible.Add(mBlock.name); FarVisible.Add(mPave.name); FarVisible.Add(mStone.name);
        float S = PlinthSize, h = PlinthH;
        // walls of the plinth in courses of 0.45 m with slightly uneven blocks, so it reads as laid stone
        for (int side = 0; side < 4; side++)
        {
            var qq = qs * Quaternion.Euler(0, side * 90, 0); var o = at + qq * Vector3.forward * (S / 2 - 0.3f);
            for (int course = 0; course < 2; course++)
                for (float s = -S / 2; s < S / 2 - 0.1f;)
                {
                    float bl = Mathf.Min(R(0.8f, 1.4f), S / 2 - s); var p = o + qq * Vector3.right * (s + bl / 2) + Vector3.up * (course * 0.45f + 0.225f);
                    if (!(side == 0 && Mathf.Abs(s + bl / 2) < 3.6f))                         // the steps go there
                        Box(p + qq * Vector3.forward * R(-0.02f, 0.03f), new Vector3(bl - 0.025f, 0.43f, 0.6f), qq * Quaternion.Euler(0, R(-1, 1), 0), mBlock, true);
                    s += bl;
                }
        }
        Box(at + Vector3.up * (h / 2 - 0.02f), new Vector3(S - 1.0f, h - 0.04f, S - 1.0f), qs, mBlock, true);                  // core
        Box(at + Vector3.up * (h - 0.03f), new Vector3(S - 0.2f, 0.06f, S - 0.2f), qs, mPave, true);                          // paving
        for (int side = 0; side < 4; side++)                                                                                // cornice
        {
            var qq = qs * Quaternion.Euler(0, side * 90, 0);
            if (side == 0) { foreach (float sgn in new[] { -1f, 1f }) Box(at + qq * new Vector3(sgn * (S / 4 + 1.8f), h + 0.02f, S / 2 - 0.15f), new Vector3(S / 2 - 3.6f, 0.14f, 0.5f), qq, mBlock); }
            else Box(at + qq * new Vector3(0, h + 0.02f, S / 2 - 0.15f), new Vector3(S + 0.1f, 0.14f, 0.5f), qq, mBlock);
        }
        // low parapet, broken: some stretches gone, some knocked lower, blocks lying where they fell
        for (int side = 1; side < 4; side++)
        {
            var qq = qs * Quaternion.Euler(0, side * 90, 0);
            for (float s = -S / 2 + 0.3f; s < S / 2 - 0.3f; s += 1.2f)
            {
                if (R() < 0.18f) { if (R() < 0.6f) Box(at + qq * new Vector3(s + R(-0.5f, 0.5f), 0.15f, S / 2 + R(0.6f, 2f)), new Vector3(1.1f, 0.3f, 0.32f), Quaternion.Euler(R(-20, 20), R(0, 360), R(-10, 10)), mBlock, true); continue; }
                float ph = R() < 0.2f ? R(0.2f, 0.4f) : 0.55f;
                Box(at + qq * new Vector3(s + 0.6f, h + ph / 2 + 0.08f, S / 2 - 0.2f), new Vector3(1.17f, ph, 0.32f), qq, mBlock, true);
            }
        }
        // steps from the sea side: 5 risers of 0.18 m, the lowest worn and chipped
        for (int k = 0; k < 5; k++)
        {
            float top = h - k * 0.18f; float depth = 0.36f * (k + 1);
            Box(at + qs * new Vector3(0, top / 2, S / 2 - 0.6f + depth / 2 + 0.3f), new Vector3(7f - (k == 4 ? R(0, 0.6f) : 0), top, 0.38f), qs, mBlock, true);
        }

        // 4. the digs of ancient Berenice: the web city drew them as plain boxes; rebuild every wall in laid blocks
        int walls = 0, cols = 0;
        // (bounds from the mesh itself: on a second run these renderers are already switched off)
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = mf.GetComponent<MeshRenderer>(); if (!r || !mf.sharedMesh || !mf.gameObject.activeInHierarchy) continue;
            var b = WorldBounds(mf); if (b.size.y > 2.8f || Mathf.Max(b.size.x, b.size.z) > 12f || b.min.y < -0.2f || b.min.y > 0.2f) continue;
            if (!InRuins(b.center)) continue;
            var mb = mf.sharedMesh.bounds; var T = mf.transform; var sc = T.lossyScale;
            var size = Vector3.Scale(mb.size, new Vector3(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z))); var c = T.TransformPoint(mb.center);
            r.enabled = false; if (mf.TryGetComponent<Collider>(out var col)) col.enabled = false;
            if (Mathf.Abs(size.x - size.z) < 0.2f && size.x < 1f) { Column(new Vector3(c.x, 0, c.z), size.y, mStone); cols++; continue; }
            var along = T.TransformDirection(size.x >= size.z ? Vector3.right : Vector3.forward); along.y = 0; along.Normalize();
            var wallRot = Quaternion.LookRotation(Vector3.Cross(along, Vector3.up));            // its right = along the wall
            StoneWall(new Vector3(c.x, 0, c.z), wallRot, Mathf.Max(size.x, size.z), size.y, Mathf.Min(size.x, size.z), wallRot, mStone); walls++;
        }
        // 5. the open ground round the tower: a few more wall stubs and fallen blocks inland, dry grass everywhere
        var mGrass = TwoSided(Mat("DryGrass", tex: "DryGrass", c: new Color(1.1f, 1.05f, 0.95f), smooth: 0.05f, cutout: 0.4f));
        int tufts = 0, stubs = 0;
        for (int i = 0; i < 1400; i++)
        {
            var p = at + new Vector3(R(-75, 75), 0, R(-75, 75));
            if (!Ground(p, out var gy)) continue;
            if (Mathf.Abs(Vector3.Dot(p - at, qs * Vector3.right)) < S / 2 + 1 && Mathf.Abs(Vector3.Dot(p - at, toSea)) < S / 2 + 3) continue;   // not on the plinth
            if (StreetEdge(p, null, out _) < 1.5f) continue;
            if (Down(p, out _, out var m) && (m.StartsWith("Asphalt") || m.StartsWith("Detail_"))) continue;
            float sz = R(0.45f, 1.0f); float yaw = R(0, 180);
            Add(tuftMesh, Matrix4x4.TRS(new Vector3(p.x, gy - 0.02f, p.z), Quaternion.Euler(0, yaw, 0), new Vector3(sz, sz * 0.85f, sz)), mGrass, false);
            tufts++;
            bool inland = Vector3.Dot(p - at, sea) < -S / 2;
            if (inland && R() < 0.045f && !InRuins(p)) { StoneWall(new Vector3(p.x, gy, p.z), Quaternion.Euler(0, R(0, 360), 0), R(1.5f, 4.5f), R(0.4f, 1.1f), 0.6f, qs * Quaternion.Euler(0, R() < 0.5f ? 0 : 90, 0), mStone); stubs++; }
        }
        Debug.Log($"[war] lighthouse grounds: {off} web parts off (podium, parapet, steps), plinth {S} m at {at}, berenice walls {walls}, columns {cols}, new wall stubs {stubs}, grass tufts {tufts}");

        Zawiya(at, sea, qs);
    }

    static Bounds WorldBounds(MeshFilter mf)
    {
        var mb = mf.sharedMesh.bounds; var T = mf.transform; var b = new Bounds(T.TransformPoint(mb.center), Vector3.zero);
        for (int i = 0; i < 8; i++) b.Encapsulate(T.TransformPoint(mb.center + Vector3.Scale(mb.extents, new Vector3(i % 2 * 2 - 1, i / 2 % 2 * 2 - 1, i / 4 * 2 - 1))));
        return b;
    }

    // a wall of laid stone blocks, broken down from the top, with blocks fallen beside it
    static void StoneWall(Vector3 c, Quaternion unused, float len, float hgt, float thick, Quaternion rot, Material m)
    {
        int courses = Mathf.Max(1, Mathf.RoundToInt(hgt / 0.48f));
        var along = rot * Vector3.right; var across = rot * Vector3.forward;
        thick = Mathf.Clamp(thick, 0.45f, 0.75f);
        for (int k = 0; k < courses; k++)
        {
            float ch = hgt / courses, s = -len / 2 + (k % 2) * R(0.2f, 0.5f) - (k % 2) * 0.3f;
            float stopAt = k == courses - 1 ? len * R(0.3f, 1f) - len / 2 : len / 2;       // the top course is only partly there
            while (s < Mathf.Min(len / 2, stopAt))
            {
                float bl = R(0.6f, 1.25f), e = Mathf.Min(s + bl, len / 2); float mid = (Mathf.Max(s, -len / 2) + e) / 2, w = e - Mathf.Max(s, -len / 2);
                if (w > 0.15f && R() > 0.06f + 0.1f * k)
                    Box(c + along * mid + across * R(-0.03f, 0.03f) + Vector3.up * (k * ch + ch / 2), new Vector3(w - 0.03f, ch - 0.025f, thick * R(0.92f, 1.04f)), rot * Quaternion.Euler(R(-1.5f, 1.5f), R(-2, 2), R(-1.5f, 1.5f)), m, true);
                s = e;
            }
        }
        int fallen = R() < 0.6f ? 1 + rnd.Next(3) : 0;
        for (int i = 0; i < fallen; i++)
        {
            var p = c + along * R(-len / 2, len / 2) + across * (R() < 0.5f ? -1 : 1) * R(0.7f, 1.8f);
            Box(p + Vector3.up * 0.2f, new Vector3(R(0.6f, 1.1f), 0.42f, thick), Quaternion.Euler(R(-12, 12), R(0, 360), R(-25, 25)), m, true);
        }
    }

    // a column of stacked drums on a square base, the top drums gone
    static void Column(Vector3 c, float hgt, Material m)
    {
        Box(c + Vector3.up * 0.14f, new Vector3(0.95f, 0.28f, 0.95f), Quaternion.Euler(0, R(0, 90), 0), m, true);
        float y = 0.28f;
        while (y < hgt + 0.1f)
        {
            float dh = R(0.45f, 0.7f); var off = new Vector3(R(-0.03f, 0.03f), 0, R(-0.03f, 0.03f));
            Cyl(c + off + Vector3.up * (y + dh / 2), 0.36f, dh - 0.02f, Quaternion.Euler(R(-1, 1), R(0, 360), R(-1, 1)), m, true);
            y += dh;
        }
        if (R() < 0.5f) Cyl(c + new Vector3(R(-1.5f, 1.5f), 0.36f, R(-1.5f, 1.5f)), 0.36f, R(0.5f, 0.9f), Quaternion.Euler(90, R(0, 360), 0), m, true);   // a drum on the ground
    }

    // the Sidi Khrebish zawiya beside the lighthouse (the photos show a minaret there): prayer hall, dome, minaret
    static void Zawiya(Vector3 lh, Vector3 sea, Quaternion qs)
    {
        var side = qs * Vector3.right;
        Vector3 best = Vector3.zero; float bestScore = float.MaxValue;
        for (int i = 0; i < 400; i++)
        {
            float along = R(-70, 70), back = R(5, 60);
            var p = lh + side * along - sea * back;
            if (Mathf.Abs(along) < 26) continue;
            bool ok = true;
            for (int gx = -1; gx <= 1 && ok; gx++) for (int gz = -1; gz <= 1 && ok; gz++)
            {
                var s = p + qs * new Vector3(gx * 6.5f, 0, gz * 6.5f);
                if (!Ground(s, out _) || StreetEdge(s, null, out _) < 2f || InRuins(s)) ok = false;
                else if (Down(s, out _, out var m) && m.StartsWith("Asphalt")) ok = false;
            }
            if (!ok) continue;
            float score = Mathf.Abs(Mathf.Abs(along) - 38) + back * 0.4f;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (bestScore == float.MaxValue) { Debug.LogWarning("[war] no open ground for the zawiya near the lighthouse"); return; }
        Down(best, out var gh, out _); var at = new Vector3(best.x, gh.point.y, best.z);
        keepOut.Add((at, 9.5f));
        var q = Quaternion.LookRotation(lh - at - Vector3.Project(lh - at, Vector3.up)) ;
        var mWall = Mat("Zawiya_Plaster", ph: "plastered_wall_02", c: new Color(0.93f, 0.89f, 0.8f), smooth: 0.08f, tile: 0.6f);
        var mTrim = Mat("Zawiya_Trim", c: new Color(0.74f, 0.66f, 0.55f), smooth: 0.1f);
        var mDark = Mat("Zawiya_Dark", c: new Color(0.05f, 0.05f, 0.05f), smooth: 0.1f);
        var mDome = Mat("Zawiya_Dome", ph: "concrete_wall_008", c: new Color(0.9f, 0.88f, 0.82f), smooth: 0.15f);
        foreach (var m in new[] { mWall, mTrim, mDark, mDome }) FarVisible.Add(m.name);
        // hall 9 x 9 x 5, a cornice, door and arched windows on each side
        Box(at + Vector3.up * 2.5f, new Vector3(9, 5, 9), q, mWall, true);
        Box(at + Vector3.up * 5.1f, new Vector3(9.4f, 0.25f, 9.4f), q, mTrim);
        for (int s = 0; s < 4; s++)
        {
            var qq = q * Quaternion.Euler(0, s * 90, 0);
            if (s == 0) Box(at + qq * new Vector3(0, 1.3f, 4.51f), new Vector3(1.6f, 2.6f, 0.05f), qq, mDark);
            foreach (float x in s == 0 ? new[] { -2.8f, 2.8f } : new[] { -2.6f, 0f, 2.6f })
            {
                Box(at + qq * new Vector3(x, 2.6f, 4.51f), new Vector3(0.8f, 1.5f, 0.05f), qq, mDark);
                Cyl(at + qq * new Vector3(x, 3.35f, 4.51f), 0.4f, 0.05f, qq * Quaternion.Euler(90, 0, 0), mDark);
            }
        }
        // drum and dome
        var ring = at + Vector3.up * 5.2f;
        Add(octMesh, Matrix4x4.TRS(ring, q, new Vector3(2.6f, 1.2f, 2.6f)), mWall, false);
        Meshes(); Add(sphereMesh, Matrix4x4.TRS(ring + Vector3.up * 1.2f, q, new Vector3(5.0f, 4.4f, 5.0f)), mDome, false);
        Cyl(ring + Vector3.up * 3.55f, 0.06f, 0.8f, Quaternion.identity, mTrim);
        // minaret at the corner nearest the road: square base, octagonal shaft, balcony with posts, top shaft, cone
        var mc = at + q * new Vector3(4.6f, 0, 4.6f);
        Box(mc + Vector3.up * 3.6f, new Vector3(2.6f, 7.2f, 2.6f), q, mWall, true);
        Box(mc + Vector3.up * 7.25f, new Vector3(2.9f, 0.2f, 2.9f), q, mTrim);
        Add(octMesh, Matrix4x4.TRS(mc + Vector3.up * 7.3f, q, new Vector3(1.05f, 7.2f, 1.05f)), mWall, false);
        Add(octMesh, Matrix4x4.TRS(mc + Vector3.up * 14.5f, q, new Vector3(1.75f, 0.3f, 1.75f)), mTrim, false);
        for (int k = 0; k < 12; k++)
        {
            var a = Quaternion.Euler(0, k * 30, 0);
            Box(mc + Vector3.up * 15.2f + a * Vector3.forward * 1.6f, new Vector3(0.07f, 0.9f, 0.07f), a, mTrim);
            Box(mc + Vector3.up * 15.65f + a * Quaternion.Euler(0, 15, 0) * Vector3.forward * 1.56f, new Vector3(0.86f, 0.08f, 0.08f), a * Quaternion.Euler(0, 15, 0), mTrim);
        }
        Add(octMesh, Matrix4x4.TRS(mc + Vector3.up * 14.8f, q, new Vector3(0.85f, 2.9f, 0.85f)), mWall, false);
        for (int k = 0; k < 4; k++) Box(mc + Vector3.up * 16.4f + q * Quaternion.Euler(0, k * 90, 0) * Vector3.forward * 0.86f, new Vector3(0.3f, 0.8f, 0.05f), q * Quaternion.Euler(0, k * 90, 0), mDark);
        Add(octMesh, Matrix4x4.TRS(mc + Vector3.up * 17.7f, q, new Vector3(1.0f, 0.18f, 1.0f)), mTrim, false);
        Add(octCone, Matrix4x4.TRS(mc + Vector3.up * 17.88f, q, new Vector3(0.95f, 2.2f, 0.95f)), mDome, false);
        Meshes(); Add(sphereMesh, Matrix4x4.TRS(mc + Vector3.up * 20.15f, Quaternion.identity, Vector3.one * 0.22f), mTrim, false);
        // the war on it: bullet sprays and one shell hole on the minaret base
        var mPock = Mat("Pocks", tex: "Pocks", c: Color.white, smooth: 0.1f, transparent: true);
        var mHole = Mat("ShellHole", tex: "ShellHole", c: Color.white, smooth: 0.05f, cutout: 0.5f);
        for (int s = 0; s < 4; s++) { var qq = q * Quaternion.Euler(0, s * 90, 0); Vertical(at + qq * new Vector3(R(-3, 3), R(1.5f, 4f), 4.53f), R(1.6f, 2.6f), R(1.6f, 2.6f), qq * Quaternion.Euler(0, 180, 0), mPock); }
        Vertical(mc + q * new Vector3(0, 5.0f, 1.33f), 1.4f, 1.4f, q * Quaternion.Euler(0, 180, R(0, 360)), mHole);
        Debug.Log($"[war] zawiya and minaret at {at}, {Vector3.Distance(at, lh):F0} m from the lighthouse");
    }

    // ---------------------------------------------------------------- the war on the buildings
    static void WarFacades()
    {
        var mSlab = Mat("BalconySlab", ph: "concrete_wall_008", c: new Color(0.72f, 0.68f, 0.6f), smooth: 0.06f);
        var mRail = TwoSided(Mat("Railing", tex: "Railing", c: Color.white, smooth: 0.3f, metal: 0.4f, cutout: 0.5f));
        var mParapet = Mat("BalconyParapet", ph: "plastered_wall_02", c: new Color(0.76f, 0.7f, 0.6f), smooth: 0.06f);
        var mAc = Mat("AcUnit", tex: "AcFront", c: new Color(0.95f, 0.94f, 0.9f), smooth: 0.35f);
        var mAcBody = Mat("AcBody", c: new Color(0.78f, 0.77f, 0.73f), smooth: 0.3f);
        var mPipe = Mat("DrainPipe", c: new Color(0.55f, 0.55f, 0.53f), smooth: 0.35f);
        var mPock = Mat("Pocks", tex: "Pocks", c: Color.white, smooth: 0.1f, transparent: true);
        var mHole = Mat("ShellHole", tex: "ShellHole", c: Color.white, smooth: 0.05f, cutout: 0.5f);
        var mSoot = Mat("Soot", tex: "Soot", c: Color.white, smooth: 0.05f, transparent: true);
        var mGraf = Mat("Graffiti", tex: "Graffiti", c: new Color(1, 1, 1, 0.92f), smooth: 0.1f, transparent: true);
        var mGrime = Mat("WallGrime", tex: "Grime", c: Color.white, smooth: 0.05f, transparent: true);
        var mRebar = Mat("Rebar", tex: "Rust", c: new Color(0.7f, 0.6f, 0.55f), smooth: 0.2f, metal: 0.7f);
        var mHeap = Mat("RubbleHeap", tex: "Rubble", c: new Color(0.62f, 0.58f, 0.52f), smooth: 0.05f, tile: 1f);
        var mConc = Mat("RubbleChunk", ph: "concrete_wall_008", c: new Color(0.78f, 0.73f, 0.64f), smooth: 0.08f);
        var mBrick = Mat("Brick", c: new Color(0.62f, 0.36f, 0.25f), smooth: 0.08f, tex: "NoiseGrey");
        var mWire = Mat("Wire", c: new Color(0.03f, 0.03f, 0.03f), smooth: 0.3f);
        foreach (var m in new[] { mSlab, mParapet, mHeap, mPipe }) FarVisible.Add(m.name);
        NoShadow.Add(mSoot.name); NoShadow.Add(mGraf.name); NoShadow.Add(mPock.name);

        int intact = 0, ruinedViews = 0;
        int balconies = 0, broken = 0, acs = 0, pipes = 0, holes = 0, sprays = 0, soot = 0, graffiti = 0, slabs = 0, heaps = 0;
        foreach (var b in blds)
        {
            int nP = b.poly.Count; if (nP < 3 || b.h < 2.5f) continue;
            int floors = Mathf.FloorToInt(b.h / FloorH + 0.05f);
            // how hard the war hit this block: ruined ones worst, most others shot at, a few untouched
            float hit = b.ruined ? 1f : R() < 0.75f ? R(0.25f, 0.7f) : 0.05f;
            float u = 0;
            for (int i = 0; i < nP; i++)
            {
                var a = b.poly[i]; var c = b.poly[(i + 1) % nP]; var d = c - a; d.y = 0; float L = d.magnitude;
                float u0 = u; u += L;
                if (L < 3f) continue;
                var f = d / L; var mid = (a + c) / 2;
                var n = new Vector3(-f.z, 0, f.x); if (Vector3.Dot(n, mid - b.c) < 0) n = -n;
                if (!Ray(mid + n * 1.6f + Vector3.up * 4.5f, -n, 2.6f, out var wh) || Vector3.Dot(wh.normal, n) < 0.7f) continue;
                float inset = Vector3.Dot(wh.point - mid, n);
                float ko = b.kit ? 0.24f : 0f;                                     // in front of the kit facade
                Vector3 W(float s, float y, float out_) { var p = a + f * s + n * (inset + out_ + ko); return new Vector3(p.x, y, p.z); }
                var face = Quaternion.LookRotation(-n);
                bool street = StreetEdge(mid + n * 2f, null, out var near) < 4f && near != null;
                float fromPlayer = Vector2.Distance(new Vector2(mid.x, mid.z), new Vector2(player.x, player.z));
                if (street && L > 10 && b.h > 9 && fromPlayer < 220 && !Physics.Raycast(W(L / 2, 1.7f, 0.5f), n, 13f) && ((b.ruined && ruinedViews < 3) || (!b.ruined && hit > 0.4f && intact < 3)))
                {
                    ReviewViews.Add((W(L / 2 + R(-4, 4), 1.7f, 13f), W(L / 2, Mathf.Min(b.h * 0.45f, 7f), 0), b.ruined ? $"war_ruined_{ruinedViews}.png" : $"war_street_{intact}.png"));
                    if (b.ruined) ruinedViews++; else intact++;
                }

                // window columns of this wall: the facade tile repeats every 4 m along the perimeter, window in the middle
                float first = Mathf.Repeat(2f - u0, TileW);
                for (float s = first; s < L - 0.8f; s += TileW)
                {
                    if (s < 1.2f) continue;
                    int kind = b.kit ? 99 : rnd.Next(100);   // kit buildings have their own balconies                    // the whole column: < 22 balconies, < 34 AC units, else bare
                    bool parapet = R() < 0.25f;
                    for (int fl = 1; fl < floors; fl++)
                    {
                        float y = fl * FloorH;
                        if (y > b.h - 0.6f) break;
                        if (kind < 22)
                        {
                            bool down = hit > 0.5f && R() < 0.25f * hit;                       // the slab broke off at the wall and hangs
                            if (b.ruined && R() < 0.3f) continue;                              // gone altogether
                            float bw = 2.9f, dep = R(0.9f, 1.2f);
                            if (!down)
                            {
                                Box(W(s, y - 0.08f, dep / 2), new Vector3(bw, 0.16f, dep), face, mSlab);
                                if (parapet) { Box(W(s, y + 0.45f, dep - 0.05f), new Vector3(bw, 0.9f, 0.1f), face, mParapet); foreach (float sx in new[] { -1f, 1f }) Box(W(s + sx * (bw / 2 - 0.05f), y + 0.45f, dep / 2), new Vector3(0.1f, 0.9f, dep), face, mParapet); }
                                else
                                {
                                    Vertical(W(s, y + 0.5f, dep - 0.03f), bw, 1.0f, face, mRail);
                                    foreach (float sx in new[] { -1f, 1f }) Vertical(W(s + sx * (bw / 2 - 0.02f), y + 0.5f, dep / 2), dep, 1.0f, Quaternion.LookRotation(f), mRail);
                                }
                                balconies++;
                            }
                            else
                            {
                                float ang = R(25, 65);
                                var outDir = (n * Mathf.Cos(ang * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(ang * Mathf.Deg2Rad)).normalized;
                                var pivot = W(s, y - 0.08f, 0.02f);
                                Box(pivot + outDir * dep / 2, new Vector3(bw, 0.16f, dep), Quaternion.LookRotation(outDir, Vector3.Cross(outDir, f)) , mSlab);
                                for (int k = 0; k < 5; k++)
                                {
                                    var top = pivot + f * R(-bw / 2, bw / 2) + outDir * R(0, dep * 0.4f); float len = R(0.4f, 1.3f);
                                    var dir = Quaternion.Euler(R(-15, 15), R(0, 360), R(-15, 15)) * Vector3.down;
                                    Cyl(top + dir * len / 2, 0.008f, len, Quaternion.FromToRotation(Vector3.up, dir), mRebar);
                                }
                                broken++;
                            }
                        }
                        else if (kind < 34 && R() < 0.55f && !b.ruined)
                        {
                            float side = R() < 0.5f ? -1.15f : 1.15f;
                            var p = W(s + side, y + R(0.2f, 0.6f), 0.17f);
                            Box(p, new Vector3(0.84f, 0.56f, 0.3f), face, mAcBody);
                            Vertical(p + n * 0.151f, 0.84f, 0.56f, face, mAc);
                            Box(p + Vector3.down * 0.3f, new Vector3(0.7f, 0.04f, 0.34f), face, mPipe);                 // bracket
                            Vertical(W(s + side, y - 0.55f, 0.025f), 0.7f, 1.2f, face, mGrime);                            // drip stain
                            acs++;
                        }
                        // soot over a window that burnt
                        if (R() < 0.05f + 0.25f * hit) { Vertical(W(s, y + 2.3f + 1.3f, 0.03f), R(2.2f, 3.2f), R(2.4f, 3.4f), face, mSoot); soot++; }
                    }
                }
                // bullet sprays and shell holes over the whole wall
                int nSpray = Mathf.RoundToInt(L / 10f * (0.5f + 2.5f * hit) * R(0.5f, 1.5f) * (b.kit ? 0.5f : 1f));
                for (int k = 0; k < nSpray; k++)
                {
                    float sz = R(1.4f, 3.2f);
                    Vertical(W(R(sz / 2, L - sz / 2), R(0.8f, Mathf.Max(1.2f, b.h - 1.5f)), 0.028f + k * 0.002f), sz, sz, face * Quaternion.Euler(0, 0, R(0, 360)), mPock); sprays++;
                }
                int nHole = b.kit ? 0 : Mathf.RoundToInt(L / 18f * hit * hit * R(0.3f, 2.2f) + (b.ruined ? R(0, 2) : 0));   // the kit has real holes
                for (int k = 0; k < nHole; k++)
                {
                    float sz = R(1.1f, 2.6f);
                    Vertical(W(R(sz / 2, L - sz / 2), R(1.5f, Mathf.Max(2f, b.h - 1.2f)), 0.034f + k * 0.002f), sz, sz * R(0.8f, 1.1f), face * Quaternion.Euler(0, 0, R(0, 360)), mHole); holes++;
                }
                // graffiti at street level (plain words)
                if (street && R() < 0.22f + 0.2f * hit && L > 4)
                {
                    float gw = R(1.8f, 2.8f);
                    Add(graffitiQuads[rnd.Next(8)], Matrix4x4.TRS(W(R(gw / 2, L - gw / 2), R(1.2f, 1.9f), 0.04f), face * Quaternion.Euler(0, 0, R(-4, 4)), new Vector3(gw, gw, 1)), mGraf, false);
                    graffiti++;
                }
                // ruined blocks: floor slabs hanging off the wall top, rebar, and the heap of what fell
                if (b.ruined && L > 5)
                {
                    int hang = 0;   // hanging slabs off: from a distance they read as floating pieces (owner)
                    for (int k = 0; k < hang; k++)
                    {
                        float sw = Mathf.Min(L - 1, R(3, 6)), s = R(sw / 2, L - sw / 2), y = Mathf.Max(FloorH, (floors - rnd.Next(2)) * FloorH - 0.1f);
                        if (y > b.h) y = b.h - 0.1f;
                        float ang = R(30, 75), dep = R(2f, 3.5f);
                        var outDir = (n * Mathf.Cos(ang * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(ang * Mathf.Deg2Rad)).normalized;
                        var pivot = W(s, y, -0.2f);
                        // only off a wall that is really there just below (not a cut lot, not a gap in the old box)
                        var wallBelow = W(s, y - 1.2f, 0f);
                        if (cutPolys.Any(cp => NearPoly(cp, pivot, 3f)) || !Ray(wallBelow + n * 1.2f, -n, 1.8f, out var wb2) || Vector3.Dot(wb2.normal, n) < 0.6f) continue;
                        Box(pivot + outDir * dep / 2, new Vector3(sw, 0.22f, dep), Quaternion.LookRotation(outDir, Vector3.Cross(outDir, f)), mConc);
                        for (int r = 0; r < 9; r++)
                        {
                            var top = pivot + f * R(-sw / 2, sw / 2) + outDir * dep * R(0.85f, 1f); float len = R(0.5f, 1.8f);
                            var dir = Vector3.Slerp(outDir, Vector3.down, R(0.3f, 0.9f));
                            Cyl(top + dir * len / 2, 0.009f, len, Quaternion.FromToRotation(Vector3.up, dir), mRebar);
                        }
                        slabs++;
                    }
                    int nHeap = Mathf.Max(1, Mathf.RoundToInt(L / 9f));
                    for (int k = 0; k < nHeap; k++)
                    {
                        if (R() < 0.35f) continue;
                        float hw = R(4f, 9f), hd = R(3f, 5.5f), hh = R(0.7f, 1.8f);
                        var p = W(R(1, L - 1), 0, hd * 0.3f);
                        if (!Ground(p, out var gy, true)) continue;
                        Add(heapMesh, Matrix4x4.TRS(new Vector3(p.x, gy, p.z), face * Quaternion.Euler(0, R(-15, 15), 0), new Vector3(hw, hh, hd)), mHeap, true);
                        for (int ch = 0; ch < 14; ch++)
                        {
                            var o = p + f * R(-hw / 2, hw / 2) + n * R(-hd / 3, hd / 2);
                            float rr = new Vector2(Vector3.Dot(o - p, f) / (hw / 2), Vector3.Dot(o - p, n) / (hd / 2)).magnitude;
                            float top = Mathf.Sqrt(Mathf.Clamp01(1 - rr * rr)) * Mathf.Clamp01((1 - rr) * 3f) * hh;
                            float sz = R(0.12f, 0.5f);
                            Box(new Vector3(o.x, gy + top + sz * 0.1f, o.z), new Vector3(sz, sz * R(0.3f, 0.6f), sz * R(0.6f, 1.4f)), RandRot(), R() < 0.25f ? mBrick : mConc);
                        }
                        heaps++;
                    }
                }
            }
            // drain pipes down a couple of corners that face a street
            if (!b.ruined && !b.kit && R() < 0.35f)
            {
                var corner = b.poly[rnd.Next(nP)]; var inward = (b.c - corner); inward.y = 0; inward.Normalize();
                var p = corner - inward * 0.14f;
                if (StreetEdge(p, null, out _) < 6f && Ground(p - inward * 0.5f, out var gy, true))
                { Cyl(new Vector3(p.x, gy + b.h / 2, p.z), 0.05f, b.h, Quaternion.identity, mPipe); pipes++; }
            }
        }
        Debug.Log($"[war] balconies {balconies} (+{broken} hanging), AC units {acs}, drain pipes {pipes}, bullet sprays {sprays}, shell holes {holes}, soot {soot}, graffiti {graffiti}, hanging slabs {slabs}, rubble heaps {heaps}");

        // wires strung across the side streets from wall to wall
        int wires = 0;
        foreach (var r in roads.Where(x => x.kind == "residential" || x.kind == "unclassified" || x.kind == "service" || x.kind == "living_street"))
        {
            float along = R(3, 12);
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 1) continue;
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (; along < L; along += R(9, 18))
                {
                    var p = a + f * along; float y = R(4.5f, 7.5f);
                    if (!Ray(new Vector3(p.x, y, p.z), nn, 16, out var h1) || !Ray(new Vector3(p.x, y, p.z), -nn, 16, out var h2)) continue;
                    if (h1.distance + h2.distance > 24 || Mathf.Abs(h1.normal.y) > 0.3f || Mathf.Abs(h2.normal.y) > 0.3f) continue;
                    var e1 = h1.point - nn * 0.05f; var e2 = h2.point + nn * 0.05f + f * R(-2, 2) + Vector3.up * R(-0.6f, 0.6f);
                    int count = 1 + rnd.Next(3);
                    for (int w = 0; w < count; w++) { Cable(e1 + Vector3.up * w * 0.25f, e2 + Vector3.up * w * 0.2f, R(0.4f, 1.3f), mWire); wires++; }
                }
                along -= L;
            }
        }
        Debug.Log($"[war] wires across streets {wires}");
    }

    // ---------------------------------------------------------------- the streets after the fighting
    static void StreetDebris()
    {
        var mSand = Mat("SandSheet", tex: "SandSheet", c: Color.white, smooth: 0.05f, transparent: true);
        var mConc = Mat("RubbleChunk", ph: "concrete_wall_008", c: new Color(0.78f, 0.73f, 0.64f), smooth: 0.08f);
        var mBrick = Mat("Brick", c: new Color(0.62f, 0.36f, 0.25f), smooth: 0.08f, tex: "NoiseGrey");
        var mPaper = TwoSided(Mat("Paper", c: new Color(0.86f, 0.85f, 0.8f), smooth: 0.1f));
        var mPlastic = Mat("Plastic", c: new Color(0.25f, 0.42f, 0.55f), smooth: 0.6f);
        NoShadow.Add(mSand.name); NoShadow.Add(mPaper.name);
        int sand = 0, bits = 0;
        foreach (var r in roads)
        {
            bool side = !Main(r);
            float along = R(2, 8);
            for (int k = 0; k < r.pts.Count - 1; k++)
            {
                var a = r.pts[k]; var b = r.pts[k + 1]; var d = b - a; d.y = 0; float L = d.magnitude; if (L < 1) continue;
                var f = d / L; var nn = new Vector3(-f.z, 0, f.x);
                for (; along < L; along += side ? R(2.5f, 6) : R(6, 14))
                {
                    if (NearSpecial(a + f * along, 2)) continue;
                    // sand blown over the road, thicker towards the kerb
                    float off = (R() < 0.5f ? -1 : 1) * R(0, r.half * (side ? 0.9f : 0.75f));
                    var p = a + f * along + nn * off;
                    if (OnRoad(p, out var ry)) { Flat(p, ry + 0.012f + R(0, 0.004f), R(2.5f, side ? 7f : 4.5f), R(2f, side ? 6f : 4f), Quaternion.LookRotation(f) * Quaternion.Euler(0, R(-30, 30), 0), mSand); sand++; }
                    // bits along the kerb and walls: bricks, concrete, paper, a bottle
                    int count = side ? rnd.Next(4) : rnd.Next(2);
                    for (int i = 0; i < count; i++)
                    {
                        var q = a + f * (along + R(-3, 3)) + nn * (R() < 0.5f ? -1 : 1) * (r.half + R(-0.8f, 2.5f));
                        if (!Ground(q, out var gy, true)) continue;
                        float roll = R();
                        if (roll < 0.35f) Box(new Vector3(q.x, gy + 0.04f, q.z), new Vector3(0.22f, 0.07f, 0.11f), Quaternion.Euler(R(-8, 8), R(0, 360), R(-8, 8)), mBrick);
                        else if (roll < 0.7f) { float s = R(0.12f, 0.45f); Box(new Vector3(q.x, gy + s * 0.2f, q.z), new Vector3(s, s * 0.45f, s * R(0.6f, 1.3f)), RandRot(), mConc); }
                        else if (roll < 0.93f) Flat(q, gy + 0.015f, R(0.18f, 0.35f), R(0.2f, 0.4f), Quaternion.Euler(0, R(0, 360), 0), mPaper);
                        else Cyl(new Vector3(q.x, gy + 0.04f, q.z), 0.04f, 0.26f, Quaternion.Euler(90, R(0, 360), 0), mPlastic);
                        bits++;
                    }
                }
                along -= L;
            }
        }
        Debug.Log($"[war] sand over the streets {sand}, debris {bits}");
    }

    // ---------------------------------------------------------------- the air
    static void AtmosphereObject(Transform root)
    {
        smokeSpots.Clear();
        foreach (var b in blds.Where(x => x.ruined && x.h > 6).OrderBy(_ => R()))
        {
            float d = Vector2.Distance(new Vector2(b.c.x, b.c.z), new Vector2(player.x, player.z));
            if (d < 90 || d > 320) continue;
            if (smokeSpots.Any(s => Vector2.Distance(new Vector2(s.x, s.z), new Vector2(b.c.x, b.c.z)) < 90)) continue;
            smokeSpots.Add(new Vector3(b.c.x, b.h, b.c.z));
            if (smokeSpots.Count == 4) break;
        }
        var go = new GameObject("Atmosphere"); go.transform.SetParent(root, false);
        var atm = go.AddComponent<Atmosphere>();
        atm.smoke = smokeSpots.ToArray();
        Debug.Log($"[war] atmosphere: dust, sand gusts, {smokeSpots.Count} smoke columns at {string.Join(", ", smokeSpots.Select(s => s.ToString("F0")))}");
    }
}
