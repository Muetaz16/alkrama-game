// A real photographed sky for each time of day (Poly Haven HDRIs, CC0), for TimeOfDay to switch between.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SkySet.Run
// For each one: imports the .hdr as a lat-long panorama, makes Resources/Sky/Sky_<TIME>.mat, finds the sun
// (or moon) in the picture so the light in the game comes from where it is seen in the sky, and takes the
// horizon colour for the haze. The findings go to Resources/Sky/sky_info.txt.
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

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
        Directory.CreateDirectory(Out); AssetDatabase.Refresh();
        var shader = Shader.Find("Skybox/Panoramic");
        var sb = new StringBuilder(); int made = 0;
        foreach (var (time, file) in Skies)
        {
            string path = $"{Dir}/{file}";
            if (!File.Exists(path)) { Debug.LogError($"[sky] missing {path}"); continue; }
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureShape = TextureImporterShape.Texture2D; ti.sRGBTexture = false; ti.mipmapEnabled = false;
            ti.wrapModeU = TextureWrapMode.Repeat; ti.wrapModeV = TextureWrapMode.Clamp; ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var mat = new Material(shader) { name = "Sky_" + time };
            mat.SetTexture("_MainTex", tex); mat.SetFloat("_Mapping", 1); mat.SetFloat("_ImageType", 0); mat.SetFloat("_Exposure", 1f); mat.SetFloat("_Rotation", 0);
            mat.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f));
            mat.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
            string mp = $"{Out}/Sky_{time}.mat"; AssetDatabase.DeleteAsset(mp); AssetDatabase.CreateAsset(mat, mp);

            var (W, H, px) = LookPro.ReadHdr(path, 4);
            // the sun / moon: the brightest spot, taken as the centre of everything nearly as bright
            float bv = 0; for (int i = 0; i < px.Length; i++) bv = Mathf.Max(bv, px[i].r + px[i].g + px[i].b);
            Vector3 dir = Vector3.zero; float wsum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = px[i].r + px[i].g + px[i].b; if (l < bv * 0.5f) continue;
                int r = i / W, c = i % W;
                float lat = (r + 0.5f) / H * Mathf.PI, phi = (0.5f - (c + 0.5f) / W) * 2 * Mathf.PI;      // as Skybox/Panoramic maps it
                dir += new Vector3(Mathf.Sin(lat) * Mathf.Cos(phi), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(phi)) * l; wsum += l;
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
            sb.AppendLine($"{time};{F(dir.x)};{F(dir.y)};{F(dir.z)};{F(hz.r)};{F(hz.g)};{F(hz.b)};{F(avg)}");
            Debug.Log($"[sky] {time}: {file}, light {Mathf.Asin(Mathf.Clamp(dir.y, -1, 1)) * Mathf.Rad2Deg:F0} deg above the horizon, horizon ({hz.r:F2}, {hz.g:F2}, {hz.b:F2}), sky brightness {avg:F3}");
            made++;
        }
        File.WriteAllText($"{Out}/sky_info.txt", sb.ToString());
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log($"[sky] {made} of {Skies.Length} skies ready in {Out}");
        if (made != Skies.Length) EditorApplication.Exit(1);
    }
}
