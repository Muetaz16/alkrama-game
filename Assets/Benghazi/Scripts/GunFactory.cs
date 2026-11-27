using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

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
        return Rp.Lit(name, c, metal, smooth, tex);            // HDRP/Lit from the build's template (Rp.cs)
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
        // colours are sRGB: gun steel is a dark grey that still reflects, not black (0.16 sRGB is 2% reflectance)
        steel = Lit(new Color(0.31f, 0.32f, 0.35f), 0.85f, 0.46f, noise, "Steel");
        steelWorn = Lit(new Color(0.56f, 0.56f, 0.57f), 0.95f, 0.6f, noise, "SteelWorn");
        black = Lit(new Color(0.02f, 0.02f, 0.02f), 0.2f, 0.2f, null, "Black");
        wood = Lit(new Color(0.45f, 0.26f, 0.16f), 0f, 0.5f, Grain(512, 128, 9), "Wood");
        bakelite = Lit(new Color(0.34f, 0.16f, 0.09f), 0f, 0.5f, noise, "Bakelite");
        polymer = Lit(new Color(0.12f, 0.12f, 0.13f), 0f, 0.32f, noise, "Polymer");
        olive = Lit(new Color(0.30f, 0.30f, 0.21f), 0f, 0.22f, Noise(128, 0.12f, 5), "Olive");
        rubber = Lit(new Color(0.05f, 0.05f, 0.05f), 0f, 0.12f, noise, "Rubber");
        brass = Lit(Color.white, 1f, 0.64f, BrassTex(), "Brass");
        glass = Lit(new Color(0.05f, 0.09f, 0.12f), 0.6f, 0.97f, null, "Glass");
        blade = Lit(new Color(0.52f, 0.53f, 0.55f), 1f, 0.62f, noise, "Blade");
        edge = Lit(new Color(0.85f, 0.86f, 0.88f), 1f, 0.85f, null, "Edge");
        dot = new Material(Rp.Fx) { name = "RedDot", mainTexture = Dot(64) };
    }

    // =====================================================================================================
    // The realistic look of the real models, all made in code (no extra assets):
    //  - PBR texture sets (albedo, a normal map from a height field, metallic in R + smoothness in A), made
    //    once and shared by every gun in the scene
    //  - every model mesh rebuilt once and cached per source mesh: area-weighted smooth normals with a crease
    //    angle, box-projected UVs in metres (the models ship UVs but no textures), tangents, a few pieces given
    //    their own finish, and thin worn-bright strips laid along the sharp convex edges of the steel
    // Only two URP/Lit variants are used and material assets in the project already use both, so they are in
    // a player build: _NORMALMAP + _METALLICSPECGLOSSMAP (Asphalt.mat ...) and _ALPHATEST_ON (Detail_Crack.mat ...).
    // =====================================================================================================
    internal sealed class TexData { public int w, h; public Color32[] albedo, normal, ms; }
    sealed class TexSet { public Texture2D albedo, normal, ms; }

    static float SS(float a, float b, float x) { x = (x - a) / (b - a); x = x < 0 ? 0 : x > 1 ? 1 : x; return x * x * (3 - 2 * x); }
    static byte B8(float v) => (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v);

    // one octave of tileable value noise (cx by cy random cells, smooth interpolation) added into o
    static void AddLayer(float[] o, int w, int h, int cx, int cy, float amp, System.Random rnd)
    {
        var g = new float[cx * cy]; for (int i = 0; i < g.Length; i++) g[i] = (float)rnd.NextDouble() * amp;
        var x0 = new int[w]; var x1 = new int[w]; var fx = new float[w];
        for (int x = 0; x < w; x++) { float p = x * (float)cx / w; int i = (int)p; float f = p - i; x0[x] = i % cx; x1[x] = (i + 1) % cx; fx[x] = f * f * (3 - 2 * f); }
        int k = 0;
        for (int y = 0; y < h; y++)
        {
            float p = y * (float)cy / h; int j = (int)p; float f = p - j; f = f * f * (3 - 2 * f);
            int r0 = (j % cy) * cx, r1 = ((j + 1) % cy) * cx;
            for (int x = 0; x < w; x++)
            {
                float a = g[r0 + x0[x]], b = g[r1 + x0[x]];
                a += (g[r0 + x1[x]] - a) * fx[x]; b += (g[r1 + x1[x]] - b) * fx[x];
                o[k++] += a + (b - a) * f;
            }
        }
    }

    // tileable fractal noise, 0..1 with the middle stretched by "contrast"
    static float[] Fbm(int w, int h, int cx, int cy, int oct, float gain, float contrast, System.Random rnd)
    {
        var o = new float[w * h]; float amp = 1, sum = 0;
        for (int k = 0; k < oct; k++) { AddLayer(o, w, h, Mathf.Min(cx, w), Mathf.Min(cy, h), amp, rnd); sum += amp; amp *= gain; cx *= 2; cy *= 2; }
        float inv = contrast / sum, off = 0.5f - 0.5f * contrast;
        for (int i = 0; i < o.Length; i++) { float v = o[i] * inv + off; o[i] = v < 0 ? 0 : v > 1 ? 1 : v; }
        return o;
    }

    // thin lines: scratches on steel, pores in wood. "along" = share that runs along u, within +-spread/2 radians
    static void Scratches(float[] b, int w, int h, int count, int minLen, int maxLen, float along, float spread, System.Random rnd)
    {
        for (int s = 0; s < count; s++)
        {
            float x = (float)rnd.NextDouble() * w, y = (float)rnd.NextDouble() * h;
            float a = rnd.NextDouble() < along ? ((float)rnd.NextDouble() - 0.5f) * spread : (float)rnd.NextDouble() * 6.283f;
            float dx = Mathf.Cos(a), dy = Mathf.Sin(a), k = 0.3f + 0.7f * (float)rnd.NextDouble(); k *= k;
            int len = rnd.Next(minLen, maxLen);
            for (int i = 0; i < len; i++)
            {
                float v = k * Mathf.Min(1f, Mathf.Min(i + 1, len - i) / 3f);            // tapered ends
                float px = x + dx * i, py = y + dy * i; int ix = Mathf.FloorToInt(px), iy = Mathf.FloorToInt(py); float fy = py - iy;
                ix = ((ix % w) + w) % w; int y0 = ((iy % h) + h) % h, y1 = (y0 + 1) % h;
                int i0 = y0 * w + ix, i1 = y1 * w + ix;
                if (b[i0] < v * (1 - fy)) b[i0] = v * (1 - fy);
                if (b[i1] < v * fy) b[i1] = v * fy;
            }
        }
    }

    static void Dots(float[] b, int w, int h, int count, float maxRad, System.Random rnd)
    {
        for (int s = 0; s < count; s++)
        {
            int cx = rnd.Next(w), cy = rnd.Next(h); float rad = 0.7f + (maxRad - 0.7f) * (float)rnd.NextDouble(); int ir = (int)rad + 1;
            for (int dy = -ir; dy <= ir; dy++) for (int dx = -ir; dx <= ir; dx++)
            {
                float v = 1 - Mathf.Sqrt(dx * dx + dy * dy) / rad; if (v <= 0) continue;
                int i = ((cy + dy + h) % h) * w + (cx + dx + w) % w; if (b[i] < v) b[i] = v;
            }
        }
    }

    // tangent-space normal map (x in R, y in G, z in B: what URP unpacks from an uncompressed RGBA texture)
    static Color32[] NormalMap(float[] hgt, int w, int h, float strength)
    {
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            int yc = y * w, yu = ((y + 1) % h) * w, yd = ((y + h - 1) % h) * w;
            for (int x = 0; x < w; x++)
            {
                float dx = (hgt[yc + (x + 1) % w] - hgt[yc + (x + w - 1) % w]) * strength, dy = (hgt[yu + x] - hgt[yd + x]) * strength;
                float inv = 1f / Mathf.Sqrt(dx * dx + dy * dy + 1f);
                px[yc + x] = new Color32((byte)((0.5f - 0.5f * dx * inv) * 255f), (byte)((0.5f - 0.5f * dy * inv) * 255f), (byte)((0.5f + 0.5f * inv) * 255f), 255);
            }
        }
        return px;
    }

    // blued / parkerised steel: a dark blue-grey oxide finish with a fine grain, thinned to bare metal in patches
    // and along handling scratches, a little brown where the finish is breaking up, a few pits.
    // wearAmt 0.15 = a looked-after receiver, 0.4 = a magazine, 0.95 = bare steel with stains (bolt handle)
    internal static TexData SteelData(int n, float wearAmt, int seed, Color32 finish)
    {
        var rnd = new System.Random(seed);
        var micro = Fbm(n, n, n / 2, n / 2, 2, 0.6f, 1.5f, rnd);
        var low = Fbm(n, n, 6, 6, 5, 0.62f, 1.8f, rnd);
        var oil = Fbm(n, n, 3, 3, 3, 0.5f, 1.8f, rnd);
        var streak = Fbm(n, n, 2, 40, 3, 0.5f, 1.8f, rnd);
        var scr = new float[n * n]; Scratches(scr, n, n, (int)(n * 0.2f * (0.4f + Mathf.Min(wearAmt, 1f))), 6, n / 4, 0.8f, 0.5f, rnd);
        var pit = new float[n * n]; Dots(pit, n, n, n / 6, 2.2f, rnd);
        float th = 0.80f - 0.52f * wearAmt;
        var d = new TexData { w = n, h = n, albedo = new Color32[n * n], ms = new Color32[n * n] }; var hgt = new float[n * n];
        for (int i = 0; i < hgt.Length; i++)
        {
            float m = micro[i], s = scr[i], p = pit[i];
            float wear = SS(th, th + 0.24f, low[i] + (streak[i] - 0.5f) * 0.3f + (m - 0.5f) * 0.12f);          // soft: the finish thins before it is gone
            float bare = wear * wear * 0.8f + s; if (bare > 1) bare = 1;
            float fin = 0.84f + 0.32f * oil[i] - 0.14f * (m - 0.5f), br = 0.90f + 0.16f * m, dark = 1 - p * 0.55f;
            float rust = p * 0.5f + 4f * wear * (1 - wear) * 0.3f * oil[i];
            float r = (finish.r * fin + (172f * br - finish.r * fin) * bare + 34f * rust) * dark;
            float g = (finish.g * fin + (171f * br - finish.g * fin) * bare + 10f * rust) * dark;
            float b = (finish.b * fin + (173f * br - finish.b * fin) * bare - 8f * rust) * dark;
            d.albedo[i] = new Color32(B8(r), B8(g), B8(b), 255);
            float a0 = 0.42f + 0.20f * oil[i], sm = a0 + (0.66f + 0.16f * streak[i] - a0) * bare - p * 0.25f - rust * 0.15f;
            d.ms[i] = new Color32(B8((0.70f + 0.30f * bare - rust * 0.3f) * 255f), 255, 0, B8(sm * 255f));
            hgt[i] = (m - 0.5f) * (1 - 0.7f * bare) - s * 0.8f - wear * 0.2f - p;
        }
        d.normal = NormalMap(hgt, n, n, 0.9f);
        return d;
    }

    // laminated birch furniture: plies with dark glue lines, long wavy grain along u, pores, a red-brown stain
    // under a semi-gloss lacquer that is worn pale and matt in patches
    internal static TexData WoodData(int w, int h, int seed)
    {
        var rnd = new System.Random(seed);
        var bands = Fbm(w, h, 2, 18, 3, 0.5f, 2.0f, rnd);
        var fiber = Fbm(w, h, 8, h / 3, 2, 0.5f, 1.6f, rnd);
        var curl = Fbm(w, h, 3, 3, 2, 0.5f, 1.8f, rnd);
        var lacq = Fbm(w, h, 5, 7, 5, 0.62f, 1.8f, rnd);
        var pore = new float[w * h]; Scratches(pore, w, h, w * h / 110, 4, 22, 1f, 0.06f, rnd);
        const int plies = 20; var tone = new float[plies]; for (int k = 0; k < plies; k++) tone[k] = 0.86f + 0.26f * (float)rnd.NextDouble();
        var d = new TexData { w = w, h = h, albedo = new Color32[w * h], ms = new Color32[w * h] }; var hgt = new float[w * h];
        for (int y = 0, i = 0; y < h; y++) for (int x = 0; x < w; x++, i++)
        {
            float yy = y + (curl[i] - 0.5f) * 0.08f * h; int j = Mathf.FloorToInt(yy); float f = yy - j;
            int j0 = ((j % h) + h) % h * w + x, j1 = ((j + 1) % h + h) % h * w + x;
            float bnd = bands[j0] + (bands[j1] - bands[j0]) * f, fib = fiber[j0] + (fiber[j1] - fiber[j0]) * f;
            float pl = yy / h * plies, pfl = Mathf.Floor(pl), pf = pl - pfl; int pk = (((int)pfl % plies) + plies) % plies;
            float glue = 1 - SS(0.0f, 0.09f, pf < 0.5f ? pf : 1 - pf);
            float t = bnd * 0.7f + fib * 0.3f, lw = SS(0.52f, 0.86f, lacq[i] + (fib - 0.5f) * 0.25f), po = pore[i];
            float k = tone[pk] * (1 - 0.32f * glue) * (1 - 0.5f * po);
            float r = (70f + 60f * t) * k, g = (40f + 40f * t) * k, b = (29f + 27f * t) * k;
            float wk = lw * 0.42f, pale = 0.72f + 0.4f * t;                                     // worn through the lacquer: paler, greyer
            r += (150f * pale * (1 - 0.3f * po) - r) * wk; g += (116f * pale * (1 - 0.3f * po) - g) * wk; b += (88f * pale * (1 - 0.3f * po) - b) * wk;
            d.albedo[i] = new Color32(B8(r), B8(g), B8(b), 255);
            float sm = 0.62f + (0.24f - 0.62f) * lw - po * 0.25f - glue * 0.04f + (bnd - 0.5f) * 0.06f;
            d.ms[i] = new Color32(0, 255, 0, B8(sm * 255f));
            hgt[i] = ((fib - 0.5f) * 0.9f - po * 0.9f - glue * 0.3f) * (0.4f + 0.6f * lw);
        }
        d.normal = NormalMap(hgt, w, h, 1.5f);
        return d;
    }

    // moulded black polymer: fine pebble grain, polished shiny where hands rub it
    internal static TexData PolymerData(int n, int seed)
    {
        var rnd = new System.Random(seed);
        var peb = Fbm(n, n, n / 3, n / 3, 2, 0.5f, 1.6f, rnd);
        var low = Fbm(n, n, 4, 4, 4, 0.55f, 1.8f, rnd);
        var scr = new float[n * n]; Scratches(scr, n, n, n / 6, 5, n / 5, 0.5f, 0.6f, rnd);
        var d = new TexData { w = n, h = n, albedo = new Color32[n * n], ms = new Color32[n * n] }; var hgt = new float[n * n];
        for (int i = 0; i < hgt.Length; i++)
        {
            float p = peb[i], rub = SS(0.55f, 0.75f, low[i]), s = scr[i];
            float v = (38f + 12f * p + 10f * low[i]) * (1 + 0.5f * s);
            d.albedo[i] = new Color32(B8(v), B8(v * 1.01f), B8(v * 1.05f), 255);
            d.ms[i] = new Color32(0, 255, 0, B8((0.30f + 0.26f * rub - 0.08f * p - 0.1f * s) * 255f));
            hgt[i] = p * (1 - 0.65f * rub) - s * 0.6f;
        }
        d.normal = NormalMap(hgt, n, n, 1.8f);
        return d;
    }

    // the strip laid along sharp steel edges: bare metal, cut away (alpha) in runs so the wear is patchy.
    // u runs along the edge, v = 0 on the edge and 1 at the inner side of the strip
    internal static Color32[] EdgeData(int w, int h)
    {
        var rnd = new System.Random(21);
        var run = Fbm(w, 1, 8, 1, 4, 0.6f, 2.4f, rnd);
        var rag = Fbm(w, h, 64, 8, 2, 0.5f, 1.6f, rnd);
        var px = new Color32[w * h];
        for (int y = 0, i = 0; y < h; y++) for (int x = 0; x < w; x++, i++)
        {
            float v = y / (h - 1f), a = 0.5f + (run[x] - 0.54f - v * 0.5f + (rag[i] - 0.5f) * 0.22f) * 5f;
            float g = 150f + 60f * rag[i];
            px[i] = new Color32(B8(g), B8(g), B8(g + 2), B8(a * 255f));
        }
        return px;
    }

    // fired case: brass with a little tarnish; the top rows (v > 0.85) are the sooty inside of the case mouth
    static Texture2D BrassTex()
    {
        const int n = 64; var rnd = new System.Random(31);
        var tar = Fbm(n, n, 6, 6, 3, 0.55f, 1.8f, rnd); var px = new Color32[n * n];
        for (int y = 0, i = 0; y < n; y++) for (int x = 0; x < n; x++, i++)
        {
            float v = y / (n - 1f), t = tar[i], heat = SS(0.62f, 0.8f, v) * 0.35f, k = (0.80f + 0.24f * t) * (1 - heat);
            float soot = SS(0.82f, 0.88f, v);
            float r = 204f * k, g = (160f - 16f * (1 - t)) * k, b = (88f - 22f * (1 - t)) * k;
            px[i] = new Color32(B8(r + (34f - r) * soot), B8(g + (30f - g) * soot), B8(b + (26f - b) * soot), 255);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Brass", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
        tex.SetPixels32(px); tex.Apply(true, true); return tex;
    }

    static Texture2D Upload(Color32[] px, int w, int h, bool linear, string name)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
        t.SetPixels32(px); t.Apply(true, true);        // mipmaps, then free the CPU copy
        return t;
    }
    static TexSet Upload(TexData d, string name) => new TexSet { albedo = Upload(d.albedo, d.w, d.h, false, name + "_Albedo"), normal = Upload(d.normal, d.w, d.h, true, name + "_Normal"), ms = Upload(d.ms, d.w, d.h, true, name + "_MetalSmooth") };

    static Material Pbr(string name, TexSet t, Color tint, float smoothness, float bump)
    {
        return Rp.Pbr(name, t.albedo, t.normal, t.ms, tint, smoothness, bump);     // HDRP mask map: R metal, A smoothness
    }

    static Material mSteel, mCover, mMag, mBare, mScope, mWood, mGrip, mPoly, mEdge, mBore, kBlade, kGuard, kHandle;
    internal static MeshPlan pAkSteel, pAkWood, pAkGrip, pAkMag, pAkHandle, pVssSteel, pVssStock, pVssScope, pVssMag, pSteel, pWood, pPoly;
    static bool pbrReady;

    static void InitPbr()
    {
        if (pbrReady) return; pbrReady = true;
        // the gun is seen at grazing angles all the time: let its textures use anisotropic filtering
        if (QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable) QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
        var tSteel = Upload(SteelData(512, 0.16f, 11, new Color32(82, 85, 92, 255)), "GunSteel");
        var tWorn = Upload(SteelData(256, 0.32f, 12, new Color32(80, 83, 90, 255)), "GunSteelWorn");
        var tBare = Upload(SteelData(256, 1.25f, 13, new Color32(66, 66, 70, 255)), "GunSteelBare");
        var tWood = Upload(WoodData(512, 512, 14), "GunWood");
        var tPoly = Upload(PolymerData(256, 15), "GunPolymer");
        mSteel = Pbr("GunSteel", tSteel, Color.white, 0.86f, 0.32f);                                     // parkerised receiver, barrel, sights
        mCover = Pbr("GunSteelCover", tSteel, new Color(0.82f, 0.83f, 0.88f), 1f, 0.25f);              // blued sheet-steel dust cover: darker, glossier
        mScope = Pbr("GunScope", tSteel, new Color(0.60f, 0.61f, 0.63f), 0.95f, 0.55f);               // black-anodised scope body
        mMag = Pbr("GunMagazine", tWorn, Color.white, 0.9f, 0.45f);                                      // stamped steel magazine, well worn
        mBare = Pbr("GunBolt", tBare, Color.white, 0.92f, 0.35f);                                      // charging handle, selector: steel in the white
        mWood = Pbr("GunWood", tWood, Color.white, 1f, 1f);                                           // laminated stock and handguards
        mGrip = Pbr("GunGrip", tWood, new Color(0.74f, 0.66f, 0.64f), 0.85f, 1f);                     // pistol grip: same laminate, darker with hand oil
        mPoly = Pbr("GunPolymer", tPoly, Color.white, 1f, 1f);
        mEdge = null;
        var et = new Texture2D(256, 32, TextureFormat.RGBA32, true) { name = "GunEdgeWear", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        et.SetPixels32(EdgeData(256, 32)); et.Apply(true, true);
        mEdge = Rp.Cutout("GunEdgeWear", new Color(0.86f, 0.86f, 0.87f), 1f, 0.6f, et, 0.5f);
        mBore = Lit(new Color(0.012f, 0.012f, 0.012f), 0f, 0.05f, null, "GunBore");

        MakePlans();
        pAkSteel.mats = new[] { mSteel, mCover, mMag, mWood }; pAkWood.mats = new[] { mWood, mSteel }; pAkGrip.mats = new[] { mGrip }; pAkMag.mats = new[] { mMag }; pAkHandle.mats = new[] { mBare };
        pVssSteel.mats = new[] { mSteel }; pVssStock.mats = new[] { mPoly }; pVssScope.mats = new[] { mScope }; pVssMag.mats = new[] { mPoly };
        pSteel.mats = new[] { mSteel }; pWood.mats = new[] { mWood }; pPoly.mats = new[] { mPoly };
    }

    // the geometry side of each plan (no Unity objects: the materials are filled in by InitPbr)
    internal static void MakePlans()
    {
        // AK steel: the dust cover and the selector lever are separate pieces of the mesh, recognised by their bounds
        // (x is tested mirrored too). The model's steel / wood split follows big triangles, not the parts, and
        // leaves zig-zags: the strip along the top of the upper handguard goes back to wood; the gas-tube collars,
        // the handguard ferrules and slivers of the barrel that came as "wood" go back to steel; and behind
        // z = -0.053 the surface is cut on a clean plane, wood behind it and steel in front
        pAkSteel = new MeshPlan
        {
            mats = new Material[4], su = new[] { 0.16f, 0.16f, 0.14f, 0.30f }, sv = new[] { 0.16f, 0.16f, 0.14f, 0.11f }, grain = new[] { 2, 2, 2, 2 },
            metal = new[] { true, true, true, false }, clipZ = -0.053f, clipLimit = -0.03f, behind = 3,
            pieceMin = new[] { new Vector3(-0.0215f, 0.072f, -0.037f), new Vector3(0.020f, 0.051f, 0.013f), new Vector3(-0.012f, 0.111f, 0.299f) },
            pieceMax = new[] { new Vector3(0.0215f, 0.113f, 0.220f), new Vector3(0.024f, 0.089f, 0.136f), new Vector3(0.012f, 0.117f, 0.405f) }, pieceSlot = new[] { 1, 2, 3 },
        };
        pAkWood = new MeshPlan
        {
            mats = new Material[2], su = new[] { 0.30f, 0.16f }, sv = new[] { 0.11f, 0.16f }, grain = new[] { 2, 2 }, metal = new[] { false, false }, clipZ = -0.053f, clipLimit = -0.03f, ahead = 1,
            // steel inside the "wood" mesh: gas-tube collars and barrel slivers, the rear handguard ferrule, the butt-plate screws, the tang screw
            boxMin = new[] { new Vector3(-0.03f, 0.04f, 0.4015f), new Vector3(-0.03f, 0.07f, 0.2865f), new Vector3(-0.01f, -0.04f, -0.26f), new Vector3(-0.01f, 0.06f, -0.08f) },
            boxMax = new[] { new Vector3(0.03f, 0.13f, 0.50f), new Vector3(0.03f, 0.13f, 0.3005f), new Vector3(0.01f, 0.05f, -0.225f), new Vector3(0.01f, 0.072f, -0.06f) }, boxSlot = new[] { 1, 1, 1, 1 },
        };
        pAkGrip = One(0.22f, 0.09f, 1, false);
        pAkMag = One(0.11f, 0.11f, 2, true);
        pAkHandle = One(0.10f, 0.10f, 2, false);
        pVssSteel = One(0.16f, 0.16f, 2, true);
        pVssStock = One(0.09f, 0.09f, 2, false);
        pVssScope = One(0.16f, 0.16f, 2, true);
        pVssMag = One(0.09f, 0.09f, 2, false);
        pSteel = One(0.16f, 0.16f, 2, true); pWood = One(0.30f, 0.11f, 2, false); pPoly = One(0.09f, 0.09f, 2, false);
    }
    static MeshPlan One(float su, float sv, int grainAxis, bool metal) => new MeshPlan { mats = new Material[1], su = new[] { su }, sv = new[] { sv }, grain = new[] { grainAxis }, metal = new[] { metal } };

    // how one mesh of a model is rebuilt: a material slot per submesh (+ the edge-wear strips as the last one)
    internal sealed class MeshPlan
    {
        public Material[] mats;                     // one per slot
        public float[] su, sv;                      // metres per texture tile along / across the grain, per slot
        public int[] grain;                         // the axis the texture's u runs along (0 x, 1 y, 2 z), per slot
        public bool[] metal;                        // slots whose sharp edges get worn bright
        public float clipZ = float.NaN, clipLimit = float.MaxValue; public int behind = -1, ahead = -1;   // cut triangles lying below clipLimit on the plane z = clipZ
        public Vector3[] pieceMin, pieceMax; public int[] pieceSlot;                                      // connected pieces recognised by their bounds
        public Vector3[] boxMin, boxMax; public int[] boxSlot;                                            // connected pieces lying wholly inside a box
        public float crease = 50f, edgeMin = 52f, edgeWidth = 0.0007f;
        Material[] full, plain;
        public Material[] Materials(bool edges)
        {
            if (plain == null) { plain = mats; full = mats; if (System.Array.IndexOf(metal, true) >= 0) { full = new Material[mats.Length + 1]; mats.CopyTo(full, 0); full[mats.Length] = mEdge; } }
            return edges ? full : plain;
        }
    }
    internal sealed class Built { public Vector3[] pos, nrm; public Vector4[] tan; public Vector2[] uv; public int[][] sub; }

    static void ClipTri(Vector3 a, Vector3 b, Vector3 c, MeshPlan R, List<Vector3> P, List<int> slot)
    {
        const float eps = 2e-5f; float cz = R.clipZ;
        int sBack = R.behind >= 0 ? R.behind : 0, sFront = R.ahead >= 0 ? R.ahead : 0;
        if (a.z >= R.clipLimit || b.z >= R.clipLimit || c.z >= R.clipLimit) { P.Add(a); P.Add(b); P.Add(c); slot.Add(0); return; }
        float da = a.z - cz, db = b.z - cz, dc = c.z - cz;
        bool anyF = da > eps || db > eps || dc > eps, anyB = da < -eps || db < -eps || dc < -eps;
        if (!anyF || !anyB) { P.Add(a); P.Add(b); P.Add(c); slot.Add(anyB ? sBack : sFront); return; }
        var f = new List<Vector3>(4); var k = new List<Vector3>(4);
        void Edge(Vector3 p, float dp, Vector3 q, float dq)
        {
            if (dp >= 0) f.Add(p); if (dp <= 0) k.Add(p);
            if ((dp > 0 && dq < 0) || (dp < 0 && dq > 0)) { var m = p + (q - p) * (dp / (dp - dq)); m.z = cz; f.Add(m); k.Add(m); }
        }
        Edge(a, da, b, db); Edge(b, db, c, dc); Edge(c, dc, a, da);
        for (int i = 2; i < f.Count; i++) { P.Add(f[0]); P.Add(f[i - 1]); P.Add(f[i]); slot.Add(sFront); }
        for (int i = 2; i < k.Count; i++) { P.Add(k[0]); P.Add(k[i - 1]); P.Add(k[i]); slot.Add(sBack); }
    }

    // pure geometry (no Unity objects), so it can be checked outside the engine
    internal static Built Rebuild(Vector3[] srcV, int[] srcI, MeshPlan R)
    {
        // 1. one vertex per triangle corner; the optional plane cut
        int T0 = srcI.Length / 3; bool clip = !float.IsNaN(R.clipZ);
        var pl = new List<Vector3>(srcI.Length + 96); var sl = new List<int>(T0 + 32);
        for (int t = 0; t < T0; t++)
        {
            Vector3 a = srcV[srcI[3 * t]], b = srcV[srcI[3 * t + 1]], c = srcV[srcI[3 * t + 2]];
            if (clip) ClipTri(a, b, c, R, pl, sl); else { pl.Add(a); pl.Add(b); pl.Add(c); sl.Add(0); }
        }
        var P = pl.ToArray(); var slot = sl.ToArray();
        int C = P.Length, T = C / 3;

        // 2. weld by position (0.05 mm grid) to know which corners meet
        var vid = new int[C]; int nv = 0; var weld = new Dictionary<long, int>(C);
        for (int i = 0; i < C; i++)
        {
            var p = P[i];
            long key = ((long)(Mathf.RoundToInt(p.x * 20000f) + 1048576) & 0x1FFFFF) | (((long)(Mathf.RoundToInt(p.y * 20000f) + 1048576) & 0x1FFFFF) << 21) | (((long)(Mathf.RoundToInt(p.z * 20000f) + 1048576) & 0x1FFFFF) << 42);
            if (!weld.TryGetValue(key, out int id)) { id = nv++; weld[key] = id; }
            vid[i] = id;
        }

        // 2b. connected pieces; the ones that are inside out get turned. (Several closed pieces of the AK - the
        //     magazine, the lower handguard, the front trunnion, the magazine well - were mirrored when the model
        //     was made and kept their winding, so they were drawn back faces only, lit from the wrong side.)
        var par = new int[nv]; for (int i = 0; i < nv; i++) par[i] = i;
        int Root(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
        for (int t = 0; t < T; t++) { int ra = Root(vid[3 * t]), rb = Root(vid[3 * t + 1]); if (rb != ra) par[rb] = ra; int rc = Root(vid[3 * t + 2]); if (rc != ra) par[rc] = ra; }
        {
            var cen = new Vector3[nv]; var num = new int[nv];
            for (int i = 0; i < C; i++) { int r = Root(vid[i]); cen[r] += P[i]; num[r]++; }
            var vol = new float[nv]; var surf = new float[nv]; var edges = new int[nv]; var openEdges = new int[nv];
            var count = new Dictionary<long, int>(C);
            for (int i = 0; i < C; i++)
            {
                int va = vid[i], vb = vid[3 * (i / 3) + (i + 1) % 3];
                long key = EdgeKey(va, vb);
                count.TryGetValue(key, out int n); count[key] = n + 1;
            }
            for (int t = 0; t < T; t++)
            {
                int r = Root(vid[3 * t]); var c = cen[r] / num[r];
                Vector3 a = P[3 * t] - c, b = P[3 * t + 1] - c, d = P[3 * t + 2] - c;
                vol[r] += Vector3.Dot(a, Vector3.Cross(b, d)) / 6f; surf[r] += Vector3.Cross(b - a, d - a).magnitude * 0.5f;
                for (int k = 0; k < 3; k++)
                {
                    int va = vid[3 * t + k], vb = vid[3 * t + (k + 1) % 3];
                    long key = EdgeKey(va, vb);
                    edges[r]++; if (count[key] != 2) openEdges[r]++;
                }
            }
            for (int t = 0; t < T; t++)
            {
                int r = Root(vid[3 * t]);
                if (openEdges[r] * 4 >= edges[r] || vol[r] >= -0.004f * Mathf.Pow(surf[r], 1.5f)) continue;      // only closed pieces that are clearly inverted
                var q = P[3 * t + 1]; P[3 * t + 1] = P[3 * t + 2]; P[3 * t + 2] = q;
                int w = vid[3 * t + 1]; vid[3 * t + 1] = vid[3 * t + 2]; vid[3 * t + 2] = w;
            }
        }

        // 3. face normals, areas, corner angles
        var fn = new Vector3[T]; var area = new float[T]; var ang = new float[C];
        for (int t = 0; t < T; t++)
        {
            Vector3 a = P[3 * t], b = P[3 * t + 1], c = P[3 * t + 2];
            var n = Vector3.Cross(b - a, c - a); float l = n.magnitude;
            area[t] = l * 0.5f; fn[t] = l > 1e-12f ? n / l : Vector3.zero;
            ang[3 * t] = CornerAngle(a, b, c); ang[3 * t + 1] = CornerAngle(b, c, a); ang[3 * t + 2] = CornerAngle(c, a, b);
        }

        // 4. corners around each welded vertex
        var start = new int[nv + 1]; for (int i = 0; i < C; i++) start[vid[i] + 1]++;
        for (int i = 0; i < nv; i++) start[i + 1] += start[i];
        var fill = new int[nv]; var around = new int[C];
        for (int i = 0; i < C; i++) { int v = vid[i]; around[start[v] + fill[v]++] = i; }

        // 5. some pieces are recognised by their bounds and get their own finish
        if (R.pieceSlot != null || R.boxSlot != null)
        {
            var mn = new Vector3[nv]; var mx = new Vector3[nv]; var seen = new bool[nv];
            for (int i = 0; i < C; i++)
            {
                int r = Root(vid[i]); var p = P[i];
                if (!seen[r]) { seen[r] = true; mn[r] = mx[r] = p; } else { mn[r] = Vector3.Min(mn[r], p); mx[r] = Vector3.Max(mx[r], p); }
            }
            var pieceOf = new int[nv];
            for (int r = 0; r < nv; r++)
            {
                pieceOf[r] = -1; if (!seen[r]) continue;
                for (int k = 0; R.boxSlot != null && k < R.boxSlot.Length; k++)
                {
                    Vector3 a = R.boxMin[k], b = R.boxMax[k];
                    if (mn[r].x >= a.x && mn[r].y >= a.y && mn[r].z >= a.z && mx[r].x <= b.x && mx[r].y <= b.y && mx[r].z <= b.z) { pieceOf[r] = R.boxSlot[k]; break; }
                }
                for (int k = 0; R.pieceSlot != null && pieceOf[r] < 0 && k < R.pieceSlot.Length; k++)
                {
                    Vector3 a = R.pieceMin[k], b = R.pieceMax[k]; const float tol = 0.0015f;
                    bool yz = Mathf.Abs(mn[r].y - a.y) < tol && Mathf.Abs(mx[r].y - b.y) < tol && Mathf.Abs(mn[r].z - a.z) < tol && Mathf.Abs(mx[r].z - b.z) < tol;
                    bool x = (Mathf.Abs(mn[r].x - a.x) < tol && Mathf.Abs(mx[r].x - b.x) < tol) || (Mathf.Abs(mn[r].x + b.x) < tol && Mathf.Abs(mx[r].x + a.x) < tol);
                    if (yz && x) { pieceOf[r] = R.pieceSlot[k]; break; }
                }
            }
            for (int t = 0; t < T; t++) { int s = pieceOf[Root(vid[3 * t])]; if (s >= 0 && slot[t] == 0) slot[t] = s; }
        }

        // 6. smooth normals: faces within the crease angle, weighted by area and corner angle, so big flat
        //    faces stay flat and round parts (barrel, gas tube) shade round
        float cosCrease = Mathf.Cos(R.crease * Mathf.Deg2Rad);
        var nrm = new Vector3[C];
        for (int i = 0; i < C; i++)
        {
            int t = i / 3, v = vid[i]; var f = fn[t]; var acc = Vector3.zero;
            for (int k = start[v]; k < start[v + 1]; k++)
            {
                int j = around[k], tj = j / 3;
                if (tj == t || Vector3.Dot(f, fn[tj]) >= cosCrease) acc += fn[tj] * (area[tj] * ang[j]);
            }
            float l = acc.magnitude; nrm[i] = l > 1e-20f ? acc / l : f;
        }

        // 7. box-projected UVs in metres with the grain along the part, and the tangent that goes with them
        var uv = new Vector2[C]; var tan = new Vector4[C];
        for (int t = 0; t < T; t++)
        {
            int s = slot[t], g = R.grain[s]; var f = fn[t];
            float ax = Mathf.Abs(f.x), ay = Mathf.Abs(f.y), az = Mathf.Abs(f.z);
            int d = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
            int ua = d != g ? g : (g + 1) % 3, va = 3 - d - ua; if (d == g) va = (g + 2) % 3;
            var tu = Vector3.zero; tu[ua] = 1; var tv = Vector3.zero; tv[va] = 1;
            for (int k = 0; k < 3; k++)
            {
                int i = 3 * t + k; var p = P[i]; var n = nrm[i];
                uv[i] = new Vector2(p[ua] / R.su[s] + d * 0.37f, p[va] / R.sv[s] + d * 0.61f);
                var tg = tu - n * Vector3.Dot(n, tu); float l = tg.magnitude;
                if (l < 1e-4f) { tg = Vector3.Cross(n, tv); l = tg.magnitude; }
                tg /= l;
                tan[i] = new Vector4(tg.x, tg.y, tg.z, Vector3.Dot(Vector3.Cross(n, tg), tv) >= 0 ? 1f : -1f);
            }
        }

        // 8. worn edges: on each sharp convex edge between two steel faces, a narrow strip hugging each face,
        //    a hair above it; its normals lean from the edge direction to the face so the edge also catches
        //    light like a rounded one
        var eP = new List<Vector3>(); var eN = new List<Vector3>(); var eT = new List<Vector4>(); var eU = new List<Vector2>(); var eI = new List<int>();
        if (System.Array.IndexOf(R.metal, true) >= 0)
        {
            float cosMin = Mathf.Cos(R.edgeMin * Mathf.Deg2Rad), cosMax = Mathf.Cos(150f * Mathf.Deg2Rad), W = R.edgeWidth; const float lift = 0.00006f;
            var open = new Dictionary<long, int>(C);
            for (int i = 0; i < C; i++)
            {
                int t = i / 3, i2 = 3 * t + (i + 1) % 3; int va = vid[i], vb = vid[i2]; if (va == vb || area[t] < 1e-12f) continue;
                long key = EdgeKey(va, vb);
                if (!open.TryGetValue(key, out int j)) { open[key] = i; continue; }
                open.Remove(key);
                int tj = j / 3; if (!R.metal[slot[t]] || !R.metal[slot[tj]]) continue;
                Vector3 n1 = fn[t], n2 = fn[tj]; float dt = Vector3.Dot(n1, n2); if (dt > cosMin || dt < cosMax) continue;
                Vector3 A = P[i], Bp = P[i2], c1 = P[3 * t + (i + 2) % 3], c2 = P[3 * tj + (j + 2) % 3];
                if (Vector3.Dot(n1, c2 - A) > -1e-6f) continue;                       // concave: dirt collects there, not wear
                var e = Bp - A; float len = e.magnitude; if (len < 0.0008f) continue; e /= len;
                var nE = (n1 + n2).normalized;
                float uA = (A.z + A.y * 0.73f + A.x * 0.41f) / 0.09f, uB = (Bp.z + Bp.y * 0.73f + Bp.x * 0.41f) / 0.09f;
                for (int side = 0; side < 2; side++)
                {
                    var n = side == 0 ? n1 : n2; var far = side == 0 ? c1 : c2;
                    var inw = Vector3.Cross(n, e); float hgt = Vector3.Dot(inw, far - A); if (hgt < 0) { inw = -inw; hgt = -hgt; }
                    float w = Mathf.Min(W, hgt * 0.9f); if (w < 0.00012f) continue;
                    int b0 = C + eP.Count; var up = nE * lift; var off = inw * w + n * lift;
                    eP.Add(A + up); eP.Add(Bp + up); eP.Add(Bp + off); eP.Add(A + off);
                    eN.Add(nE); eN.Add(nE); eN.Add(n); eN.Add(n);
                    var tg = new Vector4(e.x, e.y, e.z, 1f); eT.Add(tg); eT.Add(tg); eT.Add(tg); eT.Add(tg);
                    float vIn = w / W; eU.Add(new Vector2(uA, 0)); eU.Add(new Vector2(uB, 0)); eU.Add(new Vector2(uB, vIn)); eU.Add(new Vector2(uA, vIn));
                    if (Vector3.Dot(Vector3.Cross(e, inw), n) >= 0) { eI.Add(b0); eI.Add(b0 + 1); eI.Add(b0 + 2); eI.Add(b0); eI.Add(b0 + 2); eI.Add(b0 + 3); }
                    else { eI.Add(b0); eI.Add(b0 + 2); eI.Add(b0 + 1); eI.Add(b0); eI.Add(b0 + 3); eI.Add(b0 + 2); }
                }
            }
        }

        // 9. pack: a submesh per material slot, the edge strips last
        int S = R.mats.Length; var cnt = new int[S]; for (int t = 0; t < T; t++) cnt[slot[t]] += 3;
        var sub = new int[S + 1][]; for (int s = 0; s < S; s++) { sub[s] = new int[cnt[s]]; cnt[s] = 0; }
        for (int t = 0; t < T; t++) { int s = slot[t]; var a = sub[s]; int k = cnt[s]; a[k] = 3 * t; a[k + 1] = 3 * t + 1; a[k + 2] = 3 * t + 2; cnt[s] = k + 3; }
        sub[S] = eI.ToArray();
        int E = eP.Count;
        var o = new Built { pos = new Vector3[C + E], nrm = new Vector3[C + E], tan = new Vector4[C + E], uv = new Vector2[C + E], sub = sub };
        System.Array.Copy(P, o.pos, C); System.Array.Copy(nrm, o.nrm, C); System.Array.Copy(tan, o.tan, C); System.Array.Copy(uv, o.uv, C);
        eP.CopyTo(o.pos, C); eN.CopyTo(o.nrm, C); eT.CopyTo(o.tan, C); eU.CopyTo(o.uv, C);
        return o;
    }
    // the two welded vertex ids of an edge as one dictionary key. The lower id is scrambled first: a long hashes
    // as (high ^ low), and neighbouring ids would otherwise pile into the same buckets
    static long EdgeKey(int a, int b) { if (a > b) { int t = a; a = b; b = t; } return ((long)(uint)(a * -1640531535) << 32) | (uint)b; }
    static float CornerAngle(Vector3 p, Vector3 q, Vector3 r)
    {
        var a = q - p; var b = r - p; float l = a.magnitude * b.magnitude;
        return l < 1e-20f ? 0f : Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b) / l, -1f, 1f));
    }

    // the detailed copy of a model mesh: built once, shared by every instance
    static readonly Dictionary<Mesh, Mesh> detailed = new Dictionary<Mesh, Mesh>();
    static Mesh Detail(Mesh src, MeshPlan R)
    {
        if (detailed.TryGetValue(src, out var done) && (done || !src.isReadable)) return done;
        Mesh m = null;
        if (src.isReadable)
        {
            var b = Rebuild(src.vertices, src.triangles, R);
            m = new Mesh { name = src.name + " detailed" };
            if (b.pos.Length > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(b.pos); m.SetNormals(b.nrm); m.SetTangents(b.tan); m.SetUVs(0, b.uv);
            m.subMeshCount = b.sub.Length;
            for (int i = 0; i < b.sub.Length; i++) m.SetTriangles(b.sub[i], i, false);
            m.bounds = src.bounds;                       // the reload measures the magazine by its mesh bounds: keep them exact
            m.UploadMeshData(false);
        }
        detailed[src] = m; return m;
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
    // edges = with the worn-edge strips (first person and the player's own rifle; left off the rifles of the enemies)
    static Transform Real(Transform parent, string res, string name, bool ak, bool edges)
    {
        var src = Resources.Load<GameObject>(res); if (!src) return null;
        var inst = Object.Instantiate(src, parent, false); inst.name = name;
        inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity; inst.transform.localScale = Vector3.one;
        InitPbr();
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = mf.GetComponent<MeshRenderer>(); if (!r) continue;
            string node = mf.name.ToLower(), mat = r.sharedMaterial ? r.sharedMaterial.name.ToLower() : "";
            MeshPlan part;
            if (ak) part = node == "steel" ? pAkSteel : node == "wood" ? pAkWood : node == "bakelite" ? pAkGrip : node == "mag" ? pAkMag : node == "charging handle" ? pAkHandle : null;
            else part = node == "steel" ? pVssSteel : node == "wood" ? pVssStock : node == "black" ? pVssScope : node == "mag" ? pVssMag : null;
            if (part == null) part = mat.StartsWith("wood") ? pWood : mat.StartsWith("black") || mat.StartsWith("bakelite") ? pPoly : pSteel;
            var mesh = mf.sharedMesh ? Detail(mf.sharedMesh, part) : null;
            if (mesh) { mf.sharedMesh = mesh; r.sharedMaterials = part.Materials(edges); }
            else r.sharedMaterial = mat.StartsWith("wood") ? wood : mat.StartsWith("bakelite") ? bakelite : mat.StartsWith("black") ? polymer : steel;   // mesh not readable: plain finishes
            r.shadowCastingMode = ShadowCastingMode.Off;
        }
        return inst.transform;
    }

    public static Transform AK(Transform parent, bool redDot = true)
    {
        Init();
        var real = Real(parent, "Guns/gun_ak", "AK-47", true, redDot);       // the enemies ask for no red dot: they get the cheaper rifle
        if (real)
        {
            RealAK = true;
            Part(real, cyl, mBore, new Vector3(0f, 0.0717f, 0.6265f), new Vector3(0.0092f, 0.0002f, 0.0092f), new Vector3(90, 0, 0), "bore");   // black down the barrel
            return real;
        }
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
        var real = Real(parent, "Guns/gun_vss", "Sniper", false, true);
        if (real)
        {
            Part(real, cyl, mBore, new Vector3(0.002f, 0.029f, 0.6982f), new Vector3(0.0096f, 0.0002f, 0.0096f), new Vector3(90, 0, 0), "bore");   // the exit hole of the suppressor
            return real;
        }
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
        // the skin is one grey bake; its UV islands tell the parts apart (blade in the middle third, the stacked
        // handle on the right, guard and pommel on the left), so each gets its own surface
        if (!kBlade)
        {
            var tex = Resources.Load<Texture2D>("Guns/knife_skin");
            kBlade = Lit(new Color(0.93f, 0.94f, 0.96f), 1f, 0.66f, tex, "KnifeBlade");            // satin steel
            kGuard = Lit(new Color(0.50f, 0.51f, 0.54f), 0.9f, 0.48f, tex, "KnifeGuard");          // parkerised guard and pommel
            kHandle = Lit(new Color(1.45f, 0.98f, 0.66f), 0f, 0.3f, tex, "KnifeHandle");           // oiled leather washers
        }
        foreach (var f in inst.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = f.GetComponent<MeshRenderer>(); if (!r || !f.sharedMesh) continue;
            f.sharedMesh = KnifeMesh(f.sharedMesh);
            r.sharedMaterials = f.sharedMesh.subMeshCount == 3 ? new[] { kBlade, kHandle, kGuard } : new[] { kBlade };
            r.shadowCastingMode = ShadowCastingMode.Off;
        }
        Node(holder, "Muzzle", new Vector3(0, 0, 0.23f));
        return holder;
    }
    static System.Collections.Generic.IEnumerable<int> Enumerable3(Vector3 _) { yield return 0; yield return 1; yield return 2; }

    static readonly Dictionary<Mesh, Mesh> knifeMeshes = new Dictionary<Mesh, Mesh>();
    static Mesh KnifeMesh(Mesh src)
    {
        if (knifeMeshes.TryGetValue(src, out var done) && done) return done;
        var m = src; var uv = src.isReadable ? src.uv : null;
        if (uv != null && uv.Length == src.vertexCount && uv.Length > 0)
        {
            var tri = src.triangles; var sub = new[] { new List<int>(), new List<int>(), new List<int>() };
            for (int i = 0; i < tri.Length; i += 3)
            {
                var c = (uv[tri[i]] + uv[tri[i + 1]] + uv[tri[i + 2]]) / 3f;
                int s = c.y < 0.14f ? 0 : c.x > 0.562f ? 1 : c.x < 0.19f ? 2 : 0;
                sub[s].Add(tri[i]); sub[s].Add(tri[i + 1]); sub[s].Add(tri[i + 2]);
            }
            m = Object.Instantiate(src); m.name = src.name + " parts"; m.subMeshCount = 3;
            for (int s = 0; s < 3; s++) m.SetTriangles(sub[s], s, false);
            m.bounds = src.bounds;
        }
        knifeMeshes[src] = m; return m;
    }

    // a fired case for the ejection: a real bottle-necked case (rim, extractor groove, tapered body, shoulder,
    // neck, hollow sooty mouth) lying along z, in tarnished brass
    static Mesh caseSmall, caseBig;
    public static GameObject Shell(bool big)
    {
        Init();
        if (!caseSmall) { caseSmall = CaseMesh(0.039f, 0.0056f); caseBig = CaseMesh(0.064f, 0.006f); }
        var g = new GameObject("Shell");
        var body = Part(g.transform, big ? caseBig : caseSmall, brass, Vector3.zero, Vector3.one, default, "case");
        body.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.On;
        return g;
    }

    static Mesh CaseMesh(float len, float rad)
    {
        const int seg = 14; float z0 = -len / 2;
        // profile: z as a share of the length, r as a share of the body radius
        float[] pz = { 0f, 0.008f, 0.03f, 0.036f, 0.062f, 0.07f, 0.79f, 0.86f, 1f }, pr = { 0.93f, 1f, 1f, 0.84f, 0.84f, 1f, 0.9f, 0.76f, 0.76f };
        float inner = rad * 0.68f, depth = len * 0.22f;
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        void Band(float za, float ra, float zb, float rb, float va, float vb, bool outward)
        {
            int b = v.Count; float slope = (rb - ra) / Mathf.Max(zb - za, 1e-6f);
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                var nn = (outward ? 1 : -1) * new Vector3(d.x, d.y, -slope).normalized;
                v.Add(d * ra + Vector3.forward * za); n.Add(nn); uv.Add(new Vector2(i * 2f / seg, va));
                v.Add(d * rb + Vector3.forward * zb); n.Add(nn); uv.Add(new Vector2(i * 2f / seg, vb));
            }
            for (int i = 0; i < seg; i++)
            {
                int a0 = b + i * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                if (outward) tri.AddRange(new[] { a0, b0, a1, a1, b0, b1 }); else tri.AddRange(new[] { a0, a1, b0, a1, b1, b0 });
            }
        }
        void Disc(float z, float ro, float ri, bool front, float vv)
        {
            int b = v.Count; var nn = front ? Vector3.forward : Vector3.back;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                v.Add(d * ro + Vector3.forward * z); n.Add(nn); uv.Add(new Vector2(i * 2f / seg, vv));
                v.Add(d * ri + Vector3.forward * z); n.Add(nn); uv.Add(new Vector2(i * 2f / seg, vv));
            }
            for (int i = 0; i < seg; i++)
            {
                int o0 = b + i * 2, i0 = o0 + 1, o1 = o0 + 2, i1 = o0 + 3;
                if (front) tri.AddRange(new[] { o0, o1, i1, o0, i1, i0 }); else tri.AddRange(new[] { o0, i1, o1, o0, i0, i1 });
            }
        }
        for (int k = 0; k + 1 < pz.Length; k++)
            Band(z0 + pz[k] * len, pr[k] * rad, z0 + pz[k + 1] * len, pr[k + 1] * rad, 0.05f + 0.73f * pz[k], 0.05f + 0.73f * pz[k + 1], true);
        Disc(z0, pr[0] * rad, rad * 0.36f, false, 0.3f); Disc(z0, rad * 0.36f, 0, false, 0.66f);        // head with the darker primer
        Disc(z0 + len, pr[pr.Length - 1] * rad, inner, true, 0.74f);                                    // mouth rim
        Band(z0 + len - depth, inner, z0 + len, inner, 0.97f, 0.97f, false);                            // the sooty inside
        Disc(z0 + len - depth, inner, 0, true, 0.97f);
        var m = new Mesh { name = "Case" }; m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    public static Transform Find(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }
}
