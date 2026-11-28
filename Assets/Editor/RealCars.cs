// The downloaded cars (an old white sedan and a big SUV, AI-generated, re-baked by tools/rebake-car.mjs)
// in many versions: six body colours plus the original, each clean or shot up (dust, rust, bullet holes),
// and a burnt-out shell. The body paint is found in the texture by its colour and repainted.
// CityDetail decides where cars go; this queues them and makes the GameObjects at the end.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class RealCars
{
    const string Dir = "Assets/Benghazi/Models/Cars", TexDir = "Assets/Benghazi/Textures/Cars", MatDir = "Assets/Benghazi/Materials/Cars";
    public static readonly Color[] Paints =
    {
        new(0.88f, 0.88f, 0.86f), new(0.62f, 0.63f, 0.64f), new(0.06f, 0.06f, 0.065f),
        new(0.45f, 0.07f, 0.06f), new(0.10f, 0.18f, 0.36f), new(0.74f, 0.67f, 0.52f),
    };
    public const int Colors = 7;                          // the six paints + the original

    class Model
    {
        public string name; public Mesh mesh; public float scale; public Bounds b;
        public Material[,] mats;                          // [state 0/1, colour]; burnt is separate
        public Material burnt;
        public Model lod;                                 // low-detail version shown from a distance
    }
    static Model[] models;
    public static bool Ready => models != null && models.Length > 0;
    static readonly List<(int m, Vector3 at, Quaternion rot, int c, int s, Quaternion tilt)> queue = new();

    public static void Init()
    {
        queue.Clear(); models = null;
        Directory.CreateDirectory(TexDir); Directory.CreateDirectory(MatDir);
        var list = new List<Model>();
        // sedan: white / silver body; SUV: green body
        var specs = new (string name, float scale, System.Func<float, float, float, bool> paint)[]
        {
            ("sedan", 4.2f, (h, s, v) => s < 0.2f && v > 0.45f),
            ("suv", 4.2f, (h, s, v) => h > 0.14f && h < 0.52f && s > 0.05f && v > 0.04f),
        };
        foreach (var sp in specs)
        {
          Model Build(string nm)
          {
            var glb = $"{Dir}/{nm}.glb";
            var mesh = AssetDatabase.LoadAllAssetsAtPath(glb).OfType<Mesh>().FirstOrDefault();
            var atlasPath = $"{Dir}/{nm}_atlas.png";
            var ti = AssetImporter.GetAtPath(atlasPath) as TextureImporter;
            if (!mesh || !ti) { Debug.LogWarning($"[cars] missing {nm}"); return null; }
            ti.isReadable = true; ti.mipmapEnabled = false; ti.maxTextureSize = 2048; ti.textureCompression = TextureImporterCompression.Uncompressed; ti.SaveAndReimport();
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            var m = new Model { name = nm, mesh = Smooth(mesh, nm), scale = sp.scale, b = mesh.bounds, mats = new Material[2, Colors] };
            // texture rows may run either way against the UVs: keep the mask that lines up with the paint
            var half = src.GetPixels32(); int SW = src.width, HW = src.width / 2, HH = src.height / 2;
            float Score(bool[] al) { int hit = 0, tot = 0; for (int y = 0; y < HH; y++) for (int x = 0; x < HW; x++) { if (!al[y * HW + x]) continue; tot++; Color.RGBToHSV(half[(2 * y) * SW + 2 * x], out var h, out var sa, out var v); if (sp.paint(h, sa, v)) hit++; } return tot > 0 ? hit / (float)tot : 0; }
            var maskA = PaintableArea(mesh, HW, HH, false); var maskB = PaintableArea(mesh, HW, HH, true);
            float sa1 = Score(maskA), sb1 = Score(maskB);
            Debug.Log($"[cars] {nm} paint match: uv {sa1:F2}, flipped {sb1:F2}");
            // only the white sedan needs the shape rules (its rims and glass are as light as the paint); the SUV's paint is green
            Variants(m, src, sp.paint, sp.name == "sedan" ? (sb1 > sa1 ? maskB : maskA) : Enumerable.Repeat(true, HW * HH).ToArray());
            Debug.Log($"[cars] {nm}: {mesh.triangles.Length / 3} triangles, size {mesh.bounds.size * sp.scale}");
            return m;
          }
          var full = Build(sp.name); if (full == null) continue;
          full.lod = Build(sp.name + "_lod");
          list.Add(full);
        }
        models = list.ToArray();
    }

    // the re-baked car has one flat normal per triangle (faceted look): average the normals of triangles that
    // meet at a point, unless they meet at a sharp edge (panel gaps, window frames stay crisp)
    static Mesh Smooth(Mesh src, string name)
    {
        var m = Object.Instantiate(src); m.name = name + "_smooth";
        var v = m.vertices; var t = m.triangles; var old = m.normals; var face = new Vector3[v.Length];
        var acc = new Dictionary<Vector3Int, Vector3>();
        Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 3000), Mathf.RoundToInt(p.y * 3000), Mathf.RoundToInt(p.z * 3000));
        for (int k = 0; k < t.Length; k += 3)
        {
            var fn = Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]);
            if (old.Length > 0 && Vector3.Dot(fn, old[t[k]]) < 0) fn = -fn;
            for (int c = 0; c < 3; c++)
            {
                int i = t[k + c]; face[i] = fn.normalized; var key = Key(v[i]);
                acc[key] = acc.TryGetValue(key, out var a) ? a + fn : fn;
            }
        }
        var n = new Vector3[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var avg = acc.TryGetValue(Key(v[i]), out var a) ? a.normalized : face[i];
            n[i] = Vector3.Angle(avg, face[i]) < 50f ? avg : face[i];
        }
        m.normals = n;
        var path = $"{Dir}/{name}_smooth.asset";
        AssetDatabase.DeleteAsset(path); AssetDatabase.CreateAsset(m, path);
        return m;
    }

    // ---------- texture variants ----------
    // which texture pixels belong to the body (not tyres / rims, windows, underside): decided per triangle from
    // where it sits on the car, then marked in the atlas
    static bool[] PaintableArea(Mesh mesh, int W, int H, bool flipV)
    {
        var allow = new bool[W * H];
        var v = mesh.vertices; var uv = mesh.uv; var t = mesh.triangles; var b = mesh.bounds;
        for (int k = 0; k < t.Length; k += 3)
        {
            Vector3 p0 = v[t[k]], p1 = v[t[k + 1]], p2 = v[t[k + 2]], c = (p0 + p1 + p2) / 3, n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            float hy = (c.y - b.min.y) / b.size.y, ax = Mathf.Abs(c.x - b.center.x) / (b.size.x / 2), az = Mathf.Abs(c.z - b.center.z) / (b.size.z / 2);
            bool wheel = hy < 0.38f && ax > 0.5f && az > 0.38f && az < 0.85f;
            bool glass = hy > 0.64f && Mathf.Abs(n.y) < 0.7f;
            bool under = hy < 0.1f;
            if (wheel || glass || under) continue;
            Vector2 U(Vector2 q) => new Vector2(q.x * W, (flipV ? 1 - q.y : q.y) * H);
            Vector2 a = U(uv[t[k]]), bb = U(uv[t[k + 1]]), cc = U(uv[t[k + 2]]);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, bb.x, cc.x) - 1)), x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(a.x, bb.x, cc.x) + 1));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, bb.y, cc.y) - 1)), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(a.y, bb.y, cc.y) + 1));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) allow[y * W + x] = true;
        }
        return allow;
    }

    static void Variants(Model m, Texture2D src, System.Func<float, float, float, bool> isPaint, bool[] allow)
    {
        int W = src.width / 2, H = src.height / 2;              // 1024: plenty for a car, no mip-maps (atlas cells)
        var s = src.GetPixels32(); int SW = src.width;
        var baseC = new Color[W * H]; var hsv = new Vector3[W * H]; var mask = new bool[W * H];
        var vals = new List<float>();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            Color c = (Color)s[(2 * y) * SW + 2 * x] + (Color)s[(2 * y) * SW + 2 * x + 1] + (Color)s[(2 * y + 1) * SW + 2 * x] + (Color)s[(2 * y + 1) * SW + 2 * x + 1];
            c /= 4; c.a = 1; int i = y * W + x; baseC[i] = c;
            Color.RGBToHSV(c, out var h, out var sa, out var v); hsv[i] = new Vector3(h, sa, v);
            if (allow[i] && isPaint(h, sa, v)) { mask[i] = true; vals.Add(v); }
        }
        vals.Sort(); float refV = vals.Count > 0 ? vals[vals.Count / 2] : 0.7f;
        var rnd = new System.Random(m.name.Length * 31);
        for (int state = 0; state < 2; state++)
            for (int c = 0; c < Colors; c++)
            {
                var px = new Color[W * H];
                for (int i = 0; i < px.Length; i++)
                {
                    var o = baseC[i];
                    if (c < Paints.Length && mask[i]) { float k = Mathf.Clamp(hsv[i].z / refV, 0.3f, 1.25f); o = Paints[c] * k; o.a = 1; }
                    if (state == 1)
                    {
                        int x = i % W, y = i / W;
                        float n1 = Noise(x, y, 28, 1 + c), n2 = Noise(x, y, 14, 7 + c);
                        o = Color.Lerp(o, new Color(0.52f, 0.46f, 0.36f), 0.12f + 0.3f * n1 * n1);           // dust
                        if (mask[i] && n2 > 0.7f) o = Color.Lerp(o, new Color(0.36f, 0.17f, 0.07f), Mathf.Clamp01((n2 - 0.7f) * 4));   // rust where paint chipped
                    }
                    px[i] = o;
                }
                if (state == 1) Holes(px, W, H, rnd, 140);
                m.mats[state, c] = Save($"{m.name}_{(state == 0 ? "clean" : "shot")}_{c}", px, W, H, state == 0 ? 0.62f : 0.3f, state == 0 ? 0.25f : 0.1f);
            }
        // burnt out: black soot and rust, grey ash, a little of the original shading
        var b = new Color[W * H];
        for (int i = 0; i < b.Length; i++)
        {
            int x = i % W, y = i / W; float n1 = Noise(x, y, 22, 40), n2 = Noise(x, y, 9, 41);
            // A car that has burnt out is not brown all over (that reads as mud): the paint is gone and the steel is
            // charred black-grey; where the fire was hottest the bare steel has rusted in sharp-edged orange patches
            // with a blue-grey temper line round them; pale ash lies in fine speckles on it.
            float n3 = Noise(x, y, 4, 42), n4 = Noise(x, y, 58, 43);
            var charred = Color.Lerp(new Color(0.022f, 0.021f, 0.022f), new Color(0.085f, 0.082f, 0.08f), n3);
            var rusted = Color.Lerp(new Color(0.21f, 0.085f, 0.035f), new Color(0.36f, 0.18f, 0.08f), n2);
            float heat = Mathf.SmoothStep(0.56f, 0.66f, n1 * 0.7f + n4 * 0.3f);              // about a third of the shell, in patches with a firm edge
            var c = Color.Lerp(charred, rusted, heat * Mathf.Lerp(0.55f, 1f, n3));
            float rim = 1f - Mathf.Abs(Mathf.InverseLerp(0.5f, 0.62f, n1 * 0.7f + n4 * 0.3f) - 0.5f) * 2f;
            if (rim > 0) c = Color.Lerp(c, new Color(0.09f, 0.1f, 0.13f), rim * 0.55f);          // heat-blued steel at the edge of the rust
            float ash = n2 * n3; if (ash > 0.5f) c = Color.Lerp(c, new Color(0.46f, 0.45f, 0.43f), Mathf.Clamp01((ash - 0.5f) * 5f) * 0.55f);
            c *= 0.75f + 0.35f * hsv[i].z; c.a = 1; b[i] = c;
        }
        Holes(b, W, H, rnd, 60);
        m.burnt = Save($"{m.name}_burnt", b, W, H, 0.03f, 0f);          // dead matt: soot and scale do not shine
    }

    // bullet holes: dark core, bright bare-metal ring
    static void Holes(Color[] px, int W, int H, System.Random rnd, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int cx = rnd.Next(W), cy = rnd.Next(H); float r = 1.2f + (float)rnd.NextDouble() * 1.4f;
            for (int y = cy - 4; y <= cy + 4; y++) for (int x = cx - 4; x <= cx + 4; x++)
            {
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < r) px[y * W + x] = new Color(0.02f, 0.02f, 0.02f, 1);
                else if (d < r + 1.3f) px[y * W + x] = Color.Lerp(px[y * W + x], new Color(0.62f, 0.62f, 0.6f, 1), 0.7f);
            }
        }
    }

    static Material Save(string name, Color[] px, int W, int H, float smooth, float metal)
    {
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false); tex.SetPixels(px); tex.Apply();
        var path = $"{TexDir}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.mipmapEnabled = false; ti.wrapMode = TextureWrapMode.Clamp; ti.maxTextureSize = 1024; ti.anisoLevel = 1;
        ti.SaveAndReimport();
        var mpath = $"{MatDir}/{name}.mat";
        AssetDatabase.DeleteAsset(mpath);
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        m.SetColor("_BaseColor", Color.white); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
        m.enableInstancing = true;
        m.SetFloat("_Cull", 0); m.doubleSidedGI = true;         // a few simplified triangles face inwards
        AssetDatabase.CreateAsset(m, mpath);
        return m;
    }

    static float Noise(int x, int y, int cell, int seed)
    {
        float fx = x / (float)cell, fy = y / (float)cell; int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
        float Hh(int a, int b) { uint h = (uint)(a * 374761393 + b * 668265263 + seed * 2246822519); h = (h ^ (h >> 13)) * 1274126177; return (h & 0xffff) / 65535f; }
        float sx = fx - ix, sy = fy - iy; sx = sx * sx * (3 - 2 * sx); sy = sy * sy * (3 - 2 * sy);
        return Mathf.Lerp(Mathf.Lerp(Hh(ix, iy), Hh(ix + 1, iy), sx), Mathf.Lerp(Hh(ix, iy + 1), Hh(ix + 1, iy + 1), sx), sy);
    }

    // ---------- placing ----------
    public static int ModelCount => models?.Length ?? 0;

    public static void Queue(int model, Vector3 at, Quaternion rot, int state, System.Random rnd)
    {
        float r1 = (float)rnd.NextDouble(), r2 = (float)rnd.NextDouble();
        var tilt = state == 2 ? Quaternion.Euler((r1 - 0.5f) * 5, 0, (r2 - 0.5f) * 6)          // burnt: sitting on the rims
                 : state == 1 && r1 < 0.5f ? Quaternion.Euler(r2 < 0.5f ? 1.5f : -1.5f, 0, r1 < 0.25f ? -2.5f : 2.5f)   // flat tyre
                 : Quaternion.identity;
        queue.Add((model, at, rot, rnd.Next(Colors), state, tilt));
    }

    public static void Spawn(Transform root)
    {
        if (!Ready || queue.Count == 0) return;
        var holder = new GameObject("Cars").transform; holder.SetParent(root, false);
        foreach (var q in queue)
        {
            var m = models[q.m];
            var go = new GameObject($"{m.name} {(q.s == 2 ? "burnt" : q.s == 1 ? "shot" : "")}");
            go.transform.SetParent(holder, false);
            float sink = q.s == 2 ? 0.14f : 0f;
            go.transform.SetPositionAndRotation(q.at + Vector3.down * sink, q.rot * q.tilt);
            go.transform.localScale = Vector3.one * m.scale * (q.s == 2 ? 0.985f : 1f);
            // full detail only when you are close; a 2 000-triangle version further off; nothing when tiny on screen
            Renderer Part(Model mm, string nm2)
            {
                var c = new GameObject(nm2); c.transform.SetParent(go.transform, false);
                c.AddComponent<MeshFilter>().sharedMesh = mm.mesh;
                var mr = c.AddComponent<MeshRenderer>(); mr.sharedMaterial = q.s == 2 ? mm.burnt : mm.mats[q.s, q.c];
                return mr;
            }
            var near = Part(m, "LOD0");
            var lg = go.AddComponent<LODGroup>();
            if (m.lod != null)
            {
                var far = Part(m.lod, "LOD1");
                lg.SetLODs(new[] { new LOD(0.11f, new[] { near }), new LOD(0.014f, new[] { far }) });
            }
            else lg.SetLODs(new[] { new LOD(0.014f, new[] { near }) });
            lg.RecalculateBounds();
            var bc = go.AddComponent<BoxCollider>(); bc.center = m.b.center; bc.size = m.b.size * 0.96f;
        }
        Debug.Log($"[cars] placed {queue.Count} real cars");
    }
}
