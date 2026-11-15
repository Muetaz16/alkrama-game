using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Detailed weapon models built from code (no downloads): AK-47 with a red-dot sight, a bolt-action
// sniper rifle with a scope, and a combat knife. Units are metres; +z is the muzzle, +y up, +x right.
// Gun origin = bottom of the receiver right above the pistol grip (where the hand holds it).
// Named children the animations use: "Muzzle", "Magazine", "Bolt", "Eject", "Sight" (aim point).
public static class GunFactory
{
    static Material steel, steelWorn, wood, bakelite, polymer, olive, rubber, black, brass, glass, blade, edge, dot;
    static Mesh cube, cyl, sphere;

    // ---------- materials and textures ----------
    static Material Lit(Color c, float metal, float smooth, Texture tex = null, string name = "gun")
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
        if (tex) m.SetTexture("_BaseMap", tex);
        return m;
    }

    // fine grey noise: parkerised steel, polymer
    static Texture2D Noise(int n, float amount, int seed)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "noise" };
        var rnd = new System.Random(seed); var px = new Color[n * n];
        for (int i = 0; i < px.Length; i++) { float v = 1 - amount + (float)rnd.NextDouble() * amount * 2; px[i] = new Color(v, v, v, 1); }
        // a few soft scratches
        for (int s = 0; s < 40; s++)
        {
            int x = rnd.Next(n), y = rnd.Next(n), len = rnd.Next(8, 40); float dx = (float)rnd.NextDouble() * 2 - 1, dy = (float)rnd.NextDouble() * 0.4f - 0.2f;
            for (int k = 0; k < len; k++) { int xx = ((int)(x + dx * k) % n + n) % n, yy = ((int)(y + dy * k) % n + n) % n; px[yy * n + xx] *= 1.35f; }
        }
        t.SetPixels(px); t.Apply(); return t;
    }

    // laminated wood grain running along u
    static Texture2D Grain(int w, int h, int seed)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "wood" };
        var rnd = new System.Random(seed); var px = new Color[w * h];
        var ph = new float[6]; for (int k = 0; k < 6; k++) ph[k] = (float)rnd.NextDouble() * 6.28f;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float u = x / (float)w, v = y / (float)h;
            float warp = Mathf.Sin(u * 6.28f * 2 + ph[0]) * 0.03f + Mathf.Sin(u * 6.28f * 5 + ph[1]) * 0.01f;
            float ring = Mathf.Sin((v + warp) * 6.28f * 18 + Mathf.Sin(u * 6.28f + ph[2]) * 2f);
            float g = 0.82f + 0.12f * ring + 0.06f * Mathf.Sin((v + warp * 2) * 6.28f * 63 + ph[3]);
            g *= 0.94f + (float)rnd.NextDouble() * 0.08f;
            px[y * w + x] = new Color(g, g * 0.97f, g * 0.94f, 1);
        }
        t.SetPixels(px); t.Apply(); return t;
    }

    static Texture2D Dot(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "reddot" };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
            float a = Mathf.Clamp01(1 - d * 3.2f) + Mathf.Clamp01(1 - d) * 0.25f;
            t.SetPixel(x, y, new Color(1f, 0.25f + 0.6f * Mathf.Clamp01(1 - d * 5), 0.2f, Mathf.Clamp01(a)));
        }
        t.Apply(); return t;
    }

    static void Init()
    {
        if (cube) return;
        Mesh Prim(PrimitiveType p) { var g = GameObject.CreatePrimitive(p); var m = g.GetComponent<MeshFilter>().sharedMesh; Object.Destroy(g); return m; }
        cube = Prim(PrimitiveType.Cube); cyl = Prim(PrimitiveType.Cylinder); sphere = Prim(PrimitiveType.Sphere);
        var noise = Noise(128, 0.08f, 3);
        steel = Lit(new Color(0.16f, 0.16f, 0.17f), 0.85f, 0.42f, noise, "Steel");
        steelWorn = Lit(new Color(0.42f, 0.42f, 0.43f), 0.95f, 0.6f, noise, "SteelWorn");
        black = Lit(new Color(0.02f, 0.02f, 0.02f), 0.2f, 0.2f, null, "Black");
        wood = Lit(new Color(0.62f, 0.30f, 0.14f), 0f, 0.55f, Grain(512, 128, 9), "Wood");
        bakelite = Lit(new Color(0.42f, 0.16f, 0.06f), 0f, 0.5f, noise, "Bakelite");
        polymer = Lit(new Color(0.075f, 0.075f, 0.08f), 0f, 0.28f, noise, "Polymer");
        olive = Lit(new Color(0.30f, 0.30f, 0.21f), 0f, 0.22f, Noise(128, 0.12f, 5), "Olive");
        rubber = Lit(new Color(0.05f, 0.05f, 0.05f), 0f, 0.12f, noise, "Rubber");
        brass = Lit(new Color(0.80f, 0.60f, 0.28f), 1f, 0.72f, null, "Brass");
        glass = Lit(new Color(0.05f, 0.09f, 0.12f), 0.6f, 0.97f, null, "Glass");
        blade = Lit(new Color(0.52f, 0.53f, 0.55f), 1f, 0.62f, noise, "Blade");
        edge = Lit(new Color(0.85f, 0.86f, 0.88f), 1f, 0.85f, null, "Edge");
        dot = new Material(Shader.Find("Sprites/Default")) { name = "RedDot", mainTexture = Dot(64) };
    }

    // ---------- parts ----------
    static Transform Node(Transform parent, string name, Vector3 pos, Vector3 euler = default)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = pos; t.localEulerAngles = euler; return t;
    }

    static Transform Part(Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 scale, Vector3 euler = default, string name = "part")
    {
        var g = new GameObject(name); var t = g.transform;
        t.SetParent(parent, false); t.localPosition = pos; t.localEulerAngles = euler; t.localScale = scale;
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return t;
    }

    static Transform Box(Transform p, Material m, Vector3 center, Vector3 size, Vector3 euler = default, string name = "box") => Part(p, cube, m, center, size, euler, name);

    // cylinder along z from z0 to z1
    static Transform Tube(Transform p, Material m, float x, float y, float z0, float z1, float r, string name = "tube", float squashX = 1)
        => Part(p, cyl, m, new Vector3(x, y, (z0 + z1) / 2), new Vector3(2 * r * squashX, (z1 - z0) / 2, 2 * r), new Vector3(90, 0, 0), name);

    // side-profile shape (points in z, y) extruded across x
    static Transform Prism(Transform p, Material m, Vector2[] outline, float width, float x = 0, string name = "prism", float uvScale = 6f)
    {
        var g = new GameObject(name); var t = g.transform; t.SetParent(p, false); t.localPosition = new Vector3(x, 0, 0);
        g.AddComponent<MeshFilter>().sharedMesh = Extrude(outline, width, uvScale);
        var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return t;
    }

    // frustum (cone section) along z, r0 at z0 to r1 at z1, optionally hollow
    static Transform Cone(Transform p, Material m, float y, float z0, float z1, float r0, float r1, float inner = 0, string name = "cone")
    {
        var g = new GameObject(name); var t = g.transform; t.SetParent(p, false); t.localPosition = new Vector3(0, y, 0);
        g.AddComponent<MeshFilter>().sharedMesh = Lathe(z0, z1, r0, r1, inner, 28);
        var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return t;
    }

    public static Mesh Lathe(float z0, float z1, float r0, float r1, float inner, int seg)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        void Ring(float ra, float rb, bool outward)
        {
            int b = v.Count;
            float slope = (rb - ra) / (z1 - z0);
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                var nn = (outward ? 1 : -1) * new Vector3(d.x, d.y, -slope).normalized;
                v.Add(d * ra + Vector3.forward * z0); n.Add(nn); uv.Add(new Vector2(i / (float)seg, 0));
                v.Add(d * rb + Vector3.forward * z1); n.Add(nn); uv.Add(new Vector2(i / (float)seg, 1));
            }
            for (int i = 0; i < seg; i++)
            {
                int a0 = b + i * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                if (outward) tri.AddRange(new[] { a0, b0, a1, a1, b0, b1 }); else tri.AddRange(new[] { a0, a1, b0, a1, b1, b0 });
            }
        }
        void Cap(float z, float ro, float ri, bool front)
        {
            int b = v.Count; var nn = front ? Vector3.forward : Vector3.back;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                v.Add(d * ro + Vector3.forward * z); n.Add(nn); uv.Add(new Vector2(d.x, d.y) * 0.5f);
                v.Add(d * ri + Vector3.forward * z); n.Add(nn); uv.Add(new Vector2(d.x, d.y) * 0.5f * (ri / Mathf.Max(ro, 1e-4f)));
            }
            for (int i = 0; i < seg; i++)
            {
                int o0 = b + i * 2, i0 = o0 + 1, o1 = o0 + 2, i1 = o0 + 3;
                if (front) tri.AddRange(new[] { o0, o1, i1, o0, i1, i0 }); else tri.AddRange(new[] { o0, i1, o1, o0, i0, i1 });
            }
        }
        Ring(r0, r1, true);
        if (inner > 0) Ring(inner, inner, false);
        Cap(z0, r0, inner, false); Cap(z1, r1, inner, true);
        var m = new Mesh(); m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    // extrude a simple polygon (z, y) across x, flat-shaded walls, ear-clipped caps
    public static Mesh Extrude(Vector2[] o, float width, float uvScale)
    {
        // make it counter-clockwise
        float area = 0; for (int i = 0; i < o.Length; i++) { var a = o[i]; var b = o[(i + 1) % o.Length]; area += a.x * b.y - b.x * a.y; }
        if (area < 0) System.Array.Reverse(o = (Vector2[])o.Clone());
        var caps = EarClip(o);
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        float hx = width / 2;
        foreach (var side in new[] { -1, 1 })
        {
            int b = v.Count;
            foreach (var p in o) { v.Add(new Vector3(side * hx, p.y, p.x)); n.Add(new Vector3(side, 0, 0)); uv.Add(new Vector2(p.x, p.y) * uvScale); }
            for (int i = 0; i < caps.Count; i += 3)
                if (side > 0) tri.AddRange(new[] { b + caps[i], b + caps[i + 2], b + caps[i + 1] });
                else tri.AddRange(new[] { b + caps[i], b + caps[i + 1], b + caps[i + 2] });
        }
        float run = 0;
        for (int i = 0; i < o.Length; i++)
        {
            var a = o[i]; var c = o[(i + 1) % o.Length]; var d = c - a; float len = d.magnitude; if (len < 1e-6f) continue;
            var nn = new Vector3(0, -d.x, d.y).normalized;   // outward for a CCW outline in (z, y)
            int b = v.Count;
            v.Add(new Vector3(-hx, a.y, a.x)); v.Add(new Vector3(hx, a.y, a.x)); v.Add(new Vector3(hx, c.y, c.x)); v.Add(new Vector3(-hx, c.y, c.x));
            for (int k = 0; k < 4; k++) n.Add(nn);
            uv.Add(new Vector2(run, 0) * uvScale); uv.Add(new Vector2(run, width) * uvScale); uv.Add(new Vector2(run + len, width) * uvScale); uv.Add(new Vector2(run + len, 0) * uvScale);
            run += len;
            tri.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        var m = new Mesh(); m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
        m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    static List<int> EarClip(Vector2[] p)
    {
        var idx = new List<int>(); for (int i = 0; i < p.Length; i++) idx.Add(i);
        var tris = new List<int>(); int guard = 0;
        while (idx.Count > 3 && guard++ < 1000)
        {
            bool cut = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                Vector2 a = p[ia], b = p[ib], c = p[ic];
                if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0) continue;   // reflex corner
                bool inside = false;
                foreach (var j in idx) { if (j == ia || j == ib || j == ic) continue; if (InTri(p[j], a, b, c)) { inside = true; break; } }
                if (inside) continue;
                tris.AddRange(new[] { ia, ib, ic }); idx.RemoveAt(i); cut = true; break;
            }
            if (!cut) break;
        }
        if (idx.Count == 3) tris.AddRange(idx);
        return tris;
    }
    static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y), d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y), d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    static Vector2[] P(params float[] zy) { var r = new Vector2[zy.Length / 2]; for (int i = 0; i < r.Length; i++) r[i] = new Vector2(zy[2 * i], zy[2 * i + 1]); return r; }

    // ---------- AK-47 (AKM) with a side-mounted red dot ----------
    // ---------- real models (Resources/Guns, CC0: AK-47 by Lamoot, VSS by OpenGameArt, KA-BAR knife by gamekorp) ----------
    public static bool RealAK { get; private set; }
    static Material bakeliteMag;
    static Transform Real(Transform parent, string res, string name)
    {
        var src = Resources.Load<GameObject>(res); if (!src) return null;
        var inst = Object.Instantiate(src, parent, false); inst.name = name;
        inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity; inst.transform.localScale = Vector3.one;
        if (!bakeliteMag) bakeliteMag = Lit(new Color(0.55f, 0.2f, 0.07f), 0f, 0.42f, Noise(128, 0.08f, 6), "BakeliteMag");
        foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string n = mats[i] ? mats[i].name.ToLower() : "";
                mats[i] = n.StartsWith("wood") ? wood : n.StartsWith("bakelite_mag") ? bakeliteMag : n.StartsWith("bakelite") ? bakelite : n.StartsWith("black") ? polymer : steel;
            }
            r.sharedMaterials = mats; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        return inst.transform;
    }

    public static Transform AK(Transform parent, bool redDot = true)
    {
        Init();
        var real = Real(parent, "Guns/gun_ak", "AK-47");
        if (real) { RealAK = true; return real; }
        var g = Node(parent, "AK-47", Vector3.zero);
        // receiver with the rounded dust cover, trunnions, rivets
        Box(g, steel, new Vector3(0, 0.031f, 0.035f), new Vector3(0.046f, 0.062f, 0.33f), default, "receiver");
        Tube(g, steel, 0, 0.058f, -0.125f, 0.185f, 0.0225f, "dust cover");
        Box(g, steelWorn, new Vector3(0, 0.081f, -0.13f), new Vector3(0.012f, 0.008f, 0.012f), default, "cover latch");
        Box(g, steel, new Vector3(0, 0.032f, 0.205f), new Vector3(0.048f, 0.064f, 0.04f), default, "front trunnion");
        for (int i = 0; i < 4; i++) foreach (var sx in new[] { -1, 1 })
            Part(g, sphere, steelWorn, new Vector3(sx * 0.0235f, 0.012f + (i % 2) * 0.03f, 0.19f + (i / 2) * 0.022f), Vector3.one * 0.005f, default, "rivet");
        Box(g, black, new Vector3(0.0234f, 0.046f, 0.075f), new Vector3(0.002f, 0.02f, 0.075f), default, "ejection port");
        Node(g, "Eject", new Vector3(0.03f, 0.05f, 0.075f));
        // selector lever and the charging handle (moves in the reload)
        Box(g, steelWorn, new Vector3(0.0245f, 0.047f, 0.02f), new Vector3(0.003f, 0.012f, 0.11f), new Vector3(-4, 0, 0), "selector");
        var bolt = Node(g, "Bolt", new Vector3(0.026f, 0.05f, 0.155f));
        Box(bolt, steelWorn, new Vector3(0.006f, 0, 0), new Vector3(0.014f, 0.008f, 0.01f), default, "handle");
        Part(bolt, sphere, steelWorn, new Vector3(0.014f, 0, 0), new Vector3(0.012f, 0.012f, 0.014f), default, "knob");
        // rear sight block + leaf
        Box(g, steel, new Vector3(0, 0.074f, 0.222f), new Vector3(0.034f, 0.026f, 0.05f), default, "rear sight block");
        Box(g, steel, new Vector3(0, 0.09f, 0.236f), new Vector3(0.028f, 0.006f, 0.07f), new Vector3(-3, 0, 0), "rear sight leaf");
        // barrel, gas tube, handguards
        Tube(g, steel, 0, 0.04f, 0.2f, 0.635f, 0.0115f, "barrel");
        Prism(g, wood, P(0.238f, -0.002f, 0.43f, 0.004f, 0.43f, 0.052f, 0.238f, 0.054f), 0.054f, 0, "lower handguard");
        for (int i = 0; i < 2; i++) Box(g, wood, new Vector3(0, 0.058f, 0.27f + i * 0.12f), new Vector3(0.052f, 0.004f, 0.018f), default, "handguard cap");
        Tube(g, wood, 0, 0.077f, 0.245f, 0.405f, 0.0175f, "upper handguard");
        Node(g, "GripL", new Vector3(-0.035f, -0.03f, 0.29f));                   // where the left wrist sits holding the handguard
        Tube(g, steel, 0, 0.077f, 0.405f, 0.47f, 0.0105f, "gas tube");
        Box(g, steel, new Vector3(0, 0.06f, 0.482f), new Vector3(0.026f, 0.05f, 0.03f), default, "gas block");
        Box(g, steel, new Vector3(0, 0.03f, 0.43f), new Vector3(0.05f, 0.014f, 0.012f), default, "handguard retainer");
        // front sight with ears, muzzle brake
        Box(g, steel, new Vector3(0, 0.064f, 0.595f), new Vector3(0.024f, 0.05f, 0.028f), default, "front sight base");
        foreach (var sx in new[] { -1, 1 }) Box(g, steel, new Vector3(sx * 0.0095f, 0.1f, 0.595f), new Vector3(0.004f, 0.026f, 0.012f), default, "sight ear");
        Box(g, steel, new Vector3(0, 0.097f, 0.595f), new Vector3(0.0025f, 0.02f, 0.004f), default, "sight post");
        Box(g, steel, new Vector3(0, 0.026f, 0.59f), new Vector3(0.012f, 0.014f, 0.02f), default, "bayonet lug");
        Cone(g, steel, 0.04f, 0.632f, 0.672f, 0.0145f, 0.0135f, 0.007f, "muzzle brake");
        Node(g, "Muzzle", new Vector3(0, 0.04f, 0.68f));
        // pistol grip, trigger, guard
        Prism(g, bakelite, P(-0.052f, 0.002f, -0.082f, -0.105f, -0.128f, -0.112f, -0.127f, -0.098f, -0.104f, -0.004f), 0.032f, 0, "pistol grip");
        Box(g, steel, new Vector3(0, -0.034f, -0.018f), new Vector3(0.012f, 0.004f, 0.078f), default, "trigger guard");
        Box(g, steel, new Vector3(0, -0.016f, 0.019f), new Vector3(0.012f, 0.036f, 0.004f), default, "guard front");
        Box(g, steelWorn, new Vector3(0, -0.014f, -0.018f), new Vector3(0.006f, 0.026f, 0.006f), new Vector3(-12, 0, 0), "trigger");
        // curved 30-round magazine (bakelite orange), four segments bending forward
        var mag = Node(g, "Magazine", new Vector3(0, 0.004f, 0.075f));
        var at = Vector3.zero; float ang = 0;
        for (int k = 0; k < 4; k++)
        {
            float h = 0.058f; var rot = Quaternion.Euler(-ang, 0, 0);
            var c = at + rot * new Vector3(0, -h / 2, 0);
            var seg = Box(mag, bakelite, c, new Vector3(0.03f, h + 0.004f, 0.068f + k * 0.003f), new Vector3(-ang, 0, 0), "mag");
            foreach (var sx in new[] { -1, 1 }) Box(mag, bakelite, c + rot * new Vector3(sx * 0.0155f, 0, 0.012f), new Vector3(0.002f, h * 0.8f, 0.006f), new Vector3(-ang, 0, 0), "mag rib");
            at += rot * new Vector3(0, -h, 0); ang += 9.5f;
        }
        Box(mag, steel, at + Quaternion.Euler(-ang + 9.5f, 0, 0) * new Vector3(0, -0.004f, 0.004f), new Vector3(0.034f, 0.008f, 0.08f), new Vector3(-ang + 9.5f, 0, 0), "mag floor");
        // wooden stock and butt plate
        Prism(g, wood, P(-0.125f, 0.058f, -0.125f, 0.002f, -0.2f, -0.016f, -0.535f, -0.104f, -0.535f, 0.04f, -0.36f, 0.047f, -0.2f, 0.054f), 0.04f, 0, "stock");
        Prism(g, steel, P(-0.535f, -0.106f, -0.535f, 0.042f, -0.545f, 0.042f, -0.545f, -0.106f), 0.042f, 0, "butt plate");
        Box(g, steel, new Vector3(0, 0.03f, -0.13f), new Vector3(0.05f, 0.064f, 0.012f), default, "rear tang");
        // modern side rail + red dot (hollow tube you look through)
        if (redDot)
        {
            Box(g, steel, new Vector3(0, 0.087f, 0.045f), new Vector3(0.022f, 0.012f, 0.1f), default, "dot mount");
            for (int i = 0; i < 6; i++) Box(g, black, new Vector3(0, 0.0935f, 0.004f + i * 0.016f), new Vector3(0.024f, 0.002f, 0.006f), default, "rail slot");
            Cone(g, polymer, 0.122f, 0.012f, 0.082f, 0.0188f, 0.0196f, 0.0172f, "dot tube");
            Box(g, polymer, new Vector3(0, 0.1f, 0.047f), new Vector3(0.018f, 0.012f, 0.05f), default, "dot base");
            Tube(g, polymer, 0.022f, 0.122f, 0.04f, 0.056f, 0.006f, "dot knob");
            var d = Part(g, cube, dot, new Vector3(0, 0.122f, 0.081f), new Vector3(0.0045f, 0.0045f, 0.0001f), default, "dot");
            d.GetComponent<MeshRenderer>().sharedMaterial = dot;
            Node(g, "Sight", new Vector3(0, 0.122f, 0.012f));
        }
        else Node(g, "Sight", new Vector3(0, 0.094f, 0.24f));
        return g;
    }

    // ---------- bolt-action sniper rifle with a scope ----------
    public static Transform Sniper(Transform parent)
    {
        Init();
        var real = Real(parent, "Guns/gun_vss", "Sniper");
        if (real) return real;
        var g = Node(parent, "Sniper", Vector3.zero);
        // synthetic stock with pistol grip, cheek rest and recoil pad
        Prism(g, olive, P(0.44f, 0.024f, 0.1f, 0.02f, -0.1f, 0.02f, -0.17f, 0.034f, -0.26f, 0.052f, -0.58f, 0.05f, -0.635f, 0.047f,
                         -0.635f, -0.094f, -0.57f, -0.09f, -0.17f, -0.036f, -0.1f, -0.036f, -0.118f, -0.132f, -0.078f, -0.138f,
                         -0.042f, -0.04f, 0.0f, -0.036f, 0.12f, -0.032f, 0.38f, -0.013f, 0.44f, 0.0f), 0.046f, 0, "stock");
        Prism(g, olive, P(-0.27f, 0.05f, -0.52f, 0.05f, -0.52f, 0.072f, -0.3f, 0.07f), 0.03f, 0, "cheek rest");
        Prism(g, rubber, P(-0.635f, -0.096f, -0.635f, 0.05f, -0.66f, 0.05f, -0.66f, -0.096f), 0.05f, 0, "recoil pad");
        for (int i = 0; i < 5; i++) Box(g, olive, new Vector3(0, -0.006f, 0.2f + i * 0.04f), new Vector3(0.047f, 0.012f, 0.012f), default, "forend grip");
        // round receiver, barrel with a taper, muzzle brake
        Tube(g, steel, 0, 0.035f, -0.11f, 0.16f, 0.0175f, "receiver");
        Tube(g, steel, 0, 0.035f, 0.16f, 0.46f, 0.0125f, "barrel");
        Cone(g, steel, 0.035f, 0.46f, 0.75f, 0.0125f, 0.0095f, 0, "barrel taper");
        Cone(g, steel, 0.035f, 0.75f, 0.815f, 0.0155f, 0.0155f, 0.006f, "muzzle brake");
        for (int i = 0; i < 3; i++) foreach (var sx in new[] { -1, 1 }) Box(g, black, new Vector3(sx * 0.0145f, 0.035f, 0.765f + i * 0.018f), new Vector3(0.004f, 0.012f, 0.008f), default, "brake port");
        Node(g, "Muzzle", new Vector3(0, 0.035f, 0.82f));
        Box(g, black, new Vector3(0.0168f, 0.043f, 0.04f), new Vector3(0.002f, 0.016f, 0.06f), default, "ejection port");
        Node(g, "Eject", new Vector3(0.025f, 0.045f, 0.04f));
        // bolt: handle that turns up and slides back
        var bolt = Node(g, "Bolt", new Vector3(0.0f, 0.035f, -0.075f));
        Tube(bolt, steelWorn, 0, 0, -0.06f, 0.0f, 0.0105f, "bolt shroud");
        var handle = Node(bolt, "handle", Vector3.zero);
        Box(handle, steelWorn, new Vector3(0.03f, -0.012f, 0.0f), new Vector3(0.05f, 0.008f, 0.008f), new Vector3(0, 0, -25), "bolt arm");
        Part(handle, sphere, polymer, new Vector3(0.055f, -0.024f, 0.0f), Vector3.one * 0.019f, default, "bolt knob");
        // trigger and guard, detachable box magazine
        Box(g, steel, new Vector3(0, -0.044f, -0.02f), new Vector3(0.012f, 0.004f, 0.08f), default, "trigger guard");
        Box(g, steel, new Vector3(0, -0.025f, 0.018f), new Vector3(0.012f, 0.036f, 0.004f), default, "guard front");
        Box(g, steelWorn, new Vector3(0, -0.022f, -0.02f), new Vector3(0.006f, 0.024f, 0.006f), new Vector3(-12, 0, 0), "trigger");
        var mag = Node(g, "Magazine", new Vector3(0, -0.02f, 0.07f));
        Box(mag, steel, new Vector3(0, -0.025f, 0), new Vector3(0.032f, 0.06f, 0.088f), default, "mag");
        Box(mag, polymer, new Vector3(0, -0.058f, 0), new Vector3(0.036f, 0.008f, 0.092f), default, "mag floor");
        // scope: rings on a rail, main tube, turrets, objective bell, eyepiece, lenses
        Box(g, steel, new Vector3(0, 0.058f, 0.04f), new Vector3(0.02f, 0.01f, 0.24f), default, "rail");
        foreach (var z in new[] { -0.02f, 0.12f })
        {
            Box(g, steel, new Vector3(0, 0.074f, z), new Vector3(0.026f, 0.026f, 0.018f), default, "ring base");
            Cone(g, steel, 0.104f, z - 0.009f, z + 0.009f, 0.0155f, 0.0155f, 0, "ring");
        }
        Tube(g, black, 0, 0.104f, -0.07f, 0.17f, 0.0128f, "scope tube");
        Cone(g, black, 0.104f, 0.17f, 0.225f, 0.0128f, 0.0245f, 0, "objective bell");
        Cone(g, black, 0.104f, 0.225f, 0.262f, 0.0245f, 0.0245f, 0.021f, "objective");
        Cone(g, black, 0.104f, -0.13f, -0.07f, 0.0185f, 0.0135f, 0, "eyepiece");
        Cone(g, rubber, 0.104f, -0.15f, -0.13f, 0.0195f, 0.0185f, 0.015f, "eye cup");
        Part(g, cyl, glass, new Vector3(0, 0.104f, 0.252f), new Vector3(0.042f, 0.001f, 0.042f), new Vector3(90, 0, 0), "front lens");
        Part(g, cyl, glass, new Vector3(0, 0.104f, -0.14f), new Vector3(0.03f, 0.001f, 0.03f), new Vector3(90, 0, 0), "rear lens");
        Part(g, cyl, black, new Vector3(0, 0.124f, 0.05f), new Vector3(0.024f, 0.012f, 0.024f), default, "elevation turret");
        Part(g, cyl, black, new Vector3(0.02f, 0.104f, 0.05f), new Vector3(0.022f, 0.011f, 0.022f), new Vector3(0, 0, 90), "windage turret");
        Part(g, cyl, black, new Vector3(-0.018f, 0.104f, 0.05f), new Vector3(0.02f, 0.008f, 0.02f), new Vector3(0, 0, 90), "parallax");
        // folded bipod
        foreach (var sx in new[] { -1, 1 }) Tube(g, steel, sx * 0.009f, -0.022f, 0.24f, 0.42f, 0.0045f, "bipod leg");
        Box(g, steel, new Vector3(0, -0.016f, 0.42f), new Vector3(0.03f, 0.012f, 0.02f), default, "bipod mount");
        Node(g, "Sight", new Vector3(0, 0.104f, -0.15f));
        return g;
    }

    // ---------- combat knife (origin = middle of the grip, blade along +z, edge down) ----------
    public static Transform Knife(Transform parent)
    {
        Init();
        var real = RealKnife(parent);
        if (real) return real;
        var g = Node(parent, "Knife", Vector3.zero);
        // blade: satin body, bright sharpened edge, dark fuller, clip point
        Prism(g, blade, P(0.058f, 0.013f, 0.17f, 0.013f, 0.2f, 0.006f, 0.232f, -0.002f, 0.205f, -0.009f, 0.17f, -0.0125f, 0.058f, -0.0125f), 0.0045f, 0, "blade", 20f);
        Prism(g, edge, P(0.06f, -0.0085f, 0.17f, -0.0085f, 0.204f, -0.0055f, 0.229f, -0.0015f, 0.205f, -0.0095f, 0.17f, -0.0128f, 0.06f, -0.0128f), 0.0052f, 0, "edge", 20f);
        Prism(g, edge, P(0.172f, 0.0135f, 0.2f, 0.0065f, 0.226f, -0.0005f, 0.2f, 0.0055f, 0.172f, 0.0118f), 0.0035f, 0, "clip edge", 20f);
        foreach (var sx in new[] { -1, 1 }) Box(g, black, new Vector3(sx * 0.0023f, 0.005f, 0.12f), new Vector3(0.0006f, 0.0032f, 0.1f), default, "fuller");
        // guard, handle with grip rings, pommel
        Box(g, steelWorn, new Vector3(0, -0.002f, 0.053f), new Vector3(0.012f, 0.05f, 0.007f), default, "guard");
        Tube(g, rubber, 0, 0, -0.06f, 0.05f, 0.0145f, "handle", 0.78f);
        for (int i = 0; i < 6; i++) Tube(g, black, 0, 0, -0.05f + i * 0.017f, -0.046f + i * 0.017f, 0.0152f, "grip ring", 0.8f);
        Cone(g, steelWorn, 0, -0.075f, -0.06f, 0.012f, 0.0155f, 0, "pommel");
        Node(g, "Muzzle", new Vector3(0, 0, 0.23f));
        return g;
    }

    // the KA-BAR model: turned so the blade points +z with the edge down, 30 cm long, grip in the middle of the handle
    static Transform RealKnife(Transform parent)
    {
        var src = Resources.Load<GameObject>("Guns/knife"); if (!src) return null;
        var mf = src.GetComponentInChildren<MeshFilter>(); if (!mf || !mf.sharedMesh) return null;
        var m = mf.sharedMesh; var v = m.vertices;
        var meshToSrc = src.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
        for (int i = 0; i < v.Length; i++) v[i] = meshToSrc.MultiplyPoint3x4(v[i]);
        var b = new Bounds(v[0], Vector3.zero); foreach (var p in v) b.Encapsulate(p);
        // axes: longest = along the knife, smallest = blade thickness, the other = blade height
        var size = b.size; int L = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        int T = Enumerable3(size).Where(i => i != L).OrderBy(i => size[i]).First(); int Hh = 3 - L - T;
        // which end is the blade: the thinner one
        float lo = b.min[L], hi = b.max[L], len = hi - lo;
        float ThickNear(float a, float c) { float mn = 1e9f, mx = -1e9f; foreach (var p in v) if (p[L] >= a && p[L] <= c) { mn = Mathf.Min(mn, p[T]); mx = Mathf.Max(mx, p[T]); } return mx - mn; }
        bool bladeHigh = ThickNear(hi - len * 0.25f, hi) < ThickNear(lo, lo + len * 0.25f);
        // handle axis vs blade: the blade sits on the edge side of the handle line
        float handleMid = 0, bladeMid = 0; int hn = 0, bn = 0;
        foreach (var p in v)
        {
            float u = (p[L] - lo) / len; if (bladeHigh) u = 1 - u;          // u = 0 at the tip end
            if (u > 0.8f) { handleMid += p[Hh]; hn++; } else if (u > 0.3f && u < 0.6f) { bladeMid += p[Hh]; bn++; }
        }
        handleMid /= Mathf.Max(1, hn); bladeMid /= Mathf.Max(1, bn);
        bool edgeHigh = bladeMid > handleMid;                                // edge on the +H side
        // rotation that maps (blade dir, edge-down dir, thickness) onto (+z, -y, x)
        var fwd = Vector3.zero; fwd[L] = bladeHigh ? 1 : -1;
        var down = Vector3.zero; down[Hh] = edgeHigh ? 1 : -1;
        var rot = Quaternion.Inverse(Quaternion.LookRotation(fwd, -down));
        float scale = 0.3f / len;
        var gripCentre = b.center; gripCentre[L] = bladeHigh ? lo + len * 0.17f : hi - len * 0.17f; gripCentre[Hh] = handleMid;
        var holder = new GameObject("Knife").transform; holder.SetParent(parent, false);
        var inst = Object.Instantiate(src, holder, false); inst.name = "model";
        inst.transform.localRotation = rot; inst.transform.localScale = Vector3.one * scale;
        inst.transform.localPosition = -(rot * (gripCentre * scale));
        var tex = Resources.Load<Texture2D>("Guns/knife_skin");
        var mat = Lit(Color.white, 0.6f, 0.55f, tex, "KnifeSkin");
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) { r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
        Node(holder, "Muzzle", new Vector3(0, 0, 0.23f));
        return holder;
    }
    static System.Collections.Generic.IEnumerable<int> Enumerable3(Vector3 _) { yield return 0; yield return 1; yield return 2; }

    // a fired case for the ejection
    public static GameObject Shell(bool big)
    {
        Init();
        var g = new GameObject("Shell");
        var body = Part(g.transform, cyl, brass, Vector3.zero, big ? new Vector3(0.012f, 0.032f, 0.012f) : new Vector3(0.011f, 0.0195f, 0.011f), new Vector3(90, 0, 0), "case");
        body.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return g;
    }

    public static Transform Find(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }
}
