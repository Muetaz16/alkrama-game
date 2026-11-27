// A real photographed sky for each time of day (Poly Haven HDRIs, CC0), for TimeOfDay to switch between.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SkySet.Run
// HDRP: each .hdr is imported as a cubemap and wrapped in a small volume profile with an HDRI Sky
// (Resources/Sky/SkyVol_<TIME>.asset) that the game loads by name. For each one it also finds the sun (or moon)
// in the picture so the light in the game comes from where it is seen in the sky, and takes the horizon colour.
// The findings go to Resources/Sky/sky_info.txt.
//
// Where a pixel of the lat-long picture ends up on the cubemap is the texture importer's business, so it is
// measured, not assumed: the four side faces of the imported cubemap are compared with the picture's horizon
// at four bearings, and the turn that fits best is used for the light direction (Calibrate).
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class SkySet
{
    const string Dir = "Assets/Benghazi/Sky", Out = "Assets/Resources/Sky";
    static readonly (string time, string file)[] Skies =
    {
        ("DAWN", "kloofendal_misty_morning_puresky_4k.hdr"),
        ("NOON", "kloofendal_43d_clear_puresky_4k.hdr"),
        ("AFTERNOON", "kloofendal_48d_partly_cloudy_puresky_4k.hdr"),
        ("SUNSET", "belfast_sunset_puresky_4k.hdr"),
        ("MIDNIGHT", "kloppenheim_02_puresky_4k.hdr"),
    };

    public static void Run()
    {
        if (Build() != Skies.Length) EditorApplication.Exit(1);
    }

    public static int Build()
    {
        Directory.CreateDirectory(Out); AssetDatabase.Refresh();
        var sb = new StringBuilder(); int made = 0;
        int turn = 1, hand = 1; bool calibrated = false;          // HDRP's own lat-long convention until measured
        // calibrate on the sunset first: the horizon differs most from one bearing to the next
        var order = new System.Collections.Generic.List<(string time, string file)>(Skies);
        order.Sort((a, b) => (a.time == "SUNSET" ? 0 : 1).CompareTo(b.time == "SUNSET" ? 0 : 1));
        var lines = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var (time, file) in order)
        {
            string path = $"{Dir}/{file}";
            if (!File.Exists(path)) { Debug.LogError($"[sky] missing {path}"); continue; }
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureShape = TextureImporterShape.TextureCube; ti.generateCubemap = TextureImporterGenerateCubemap.Cylindrical;
            ti.sRGBTexture = false; ti.mipmapEnabled = true; ti.isReadable = true;
            ti.wrapMode = TextureWrapMode.Clamp; ti.maxTextureSize = 4096;          // 4096 x 2048 picture -> 1024 faces
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (!cube) { Debug.LogError($"[sky] {path} did not import as a cubemap"); continue; }

            string vp = $"{Out}/SkyVol_{time}.asset"; AssetDatabase.DeleteAsset(vp);
            var prof = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(prof, vp);
            var sky = prof.Add<HDRISky>(true); sky.hdriSky.Override(cube);
            sky.name = "HDRISky"; sky.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy; AssetDatabase.AddObjectToAsset(sky, prof);
            EditorUtility.SetDirty(prof);
            AssetDatabase.DeleteAsset($"{Out}/Sky_{time}.mat");           // the URP build's Skybox/Panoramic material

            var (W, H, px) = LookPro.ReadHdr(path, 4);
            if (!calibrated) calibrated = Calibrate(cube, px, W, H, time, ref turn, ref hand);
            // the sun / moon: the brightest spot, taken as the centre of everything nearly as bright
            float bv = 0; for (int i = 0; i < px.Length; i++) bv = Mathf.Max(bv, px[i].r + px[i].g + px[i].b);
            Vector3 dir = Vector3.zero; float wsum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = px[i].r + px[i].g + px[i].b; if (l < bv * 0.5f) continue;
                int r = i / W, c = i % W;
                dir += ToDir((c + 0.5f) / W, (r + 0.5f) / H, turn, hand) * l; wsum += l;
            }
            dir = wsum > 0 ? dir.normalized : Vector3.up;
            // horizon colour and how bright the sky is overall (upper half)
            Color hz = Color.black; int n = 0;
            for (int x = 0; x < W; x += 2) { hz += px[(int)(H * 0.47f) * W + x]; n++; }
            hz /= Mathf.Max(1, n);
            double lum = 0; int ln = 0;
            for (int y = 0; y < H / 2; y += 2) for (int x = 0; x < W; x += 2) { var p = px[y * W + x]; lum += Mathf.Min(4f, p.r * 0.3f + p.g * 0.59f + p.b * 0.11f); ln++; }
            float avg = (float)(lum / Mathf.Max(1, ln));
            string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
            lines[time] = $"{time};{F(dir.x)};{F(dir.y)};{F(dir.z)};{F(hz.r)};{F(hz.g)};{F(hz.b)};{F(avg)}";
            Debug.Log($"[sky] {time}: {file}, cubemap {cube.width} px faces, light {Mathf.Asin(Mathf.Clamp(dir.y, -1, 1)) * Mathf.Rad2Deg:F0} deg above the horizon, bearing {Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg:F0} deg, horizon ({hz.r:F2}, {hz.g:F2}, {hz.b:F2}), sky brightness {avg:F3}");
            made++;
        }
        foreach (var (time, _) in Skies) if (lines.TryGetValue(time, out var l)) sb.AppendLine(l);
        File.WriteAllText($"{Out}/sky_info.txt", sb.ToString());
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log($"[sky] {made} of {Skies.Length} skies ready in {Out} (lat-long -> cubemap: turn {turn * 90} deg, hand {hand}, {(calibrated ? "measured" : "NOT measured - HDRP's convention assumed")})");
        return made;
    }

    // direction of a lat-long pixel. bearing angle a runs from +X towards +Z; (turn, hand) = (0, 1) is the
    // Skybox/Panoramic mapping (picture centre on +X), (1, 1) puts the picture centre on +Z.
    static Vector3 ToDir(float u, float v, int turn, int hand)
    {
        float lat = v * Mathf.PI, a = hand * (0.5f - u) * 2 * Mathf.PI + turn * Mathf.PI * 0.5f;
        return new Vector3(Mathf.Sin(lat) * Mathf.Cos(a), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(a));
    }

    // which way round did the importer wrap the picture? compare the horizon on the four side faces with the picture
    static bool Calibrate(Cubemap cube, Color[] px, int W, int H, string time, ref int turn, ref int hand)
    {
        try
        {
            var faces = new[] { CubemapFace.PositiveX, CubemapFace.PositiveZ, CubemapFace.NegativeX, CubemapFace.NegativeZ };      // bearings 0, 90, 180, 270
            var f = new float[4]; int s = cube.width, q = Mathf.Max(1, s / 12);
            for (int k = 0; k < 4; k++)
            {
                double sum = 0; int n = 0;
                for (int y = s / 2 - q; y < s / 2 + q; y += 2) for (int x = s / 2 - q; x < s / 2 + q; x += 2) { var c = cube.GetPixel(faces[k], x, y); sum += Mathf.Log(1e-4f + c.r * 0.3f + c.g * 0.59f + c.b * 0.11f); n++; }
                f[k] = (float)(sum / n);
            }
            float Pic(float u)
            {
                double sum = 0; int n = 0; int cx = Mathf.RoundToInt(u * W), hw = Mathf.Max(1, W / 75), hh = Mathf.Max(1, H / 38);
                for (int y = H / 2 - hh; y < H / 2 + hh; y++) for (int x = cx - hw; x < cx + hw; x++) { var c = px[y * W + ((x % W) + W) % W]; sum += Mathf.Log(1e-4f + c.r * 0.3f + c.g * 0.59f + c.b * 0.11f); n++; }
                return (float)(sum / n);
            }
            var us = new[] { 0.5f, 0.25f, 0f, 0.75f }; var pic = new float[4]; for (int i = 0; i < 4; i++) pic[i] = Pic(us[i]);
            float best = float.MaxValue, second = float.MaxValue; int bt = turn, bh = hand; var log = new StringBuilder();
            foreach (int h in new[] { 1, -1 }) for (int t = 0; t < 4; t++)
            {
                float err = 0;
                for (int i = 0; i < 4; i++) { int steps = h * i + t; int face = ((steps % 4) + 4) % 4; err += Mathf.Abs(pic[i] - f[face]); }     // u = 0.5 - i/4  ->  bearing hand * i * 90 + turn * 90
                log.Append($" [turn {t * 90} hand {h}: {err:F2}]");
                if (err < best) { second = best; best = err; bt = t; bh = h; } else if (err < second) second = err;
            }
            bool sure = second > best * 1.5f + 0.05f;
            Debug.Log($"[sky] lat-long -> cubemap, measured on {time}:{log} -> turn {bt * 90} deg, hand {bh}{(sure ? "" : " (not clear-cut: keeping HDRP's convention)")}");
            if (sure) { turn = bt; hand = bh; }
            return sure;
        }
        catch (System.Exception e) { Debug.LogWarning("[sky] could not read the cubemap to measure its layout: " + e.Message); return false; }
    }
}
