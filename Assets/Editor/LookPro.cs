// Real-world assets (Poly Haven, CC0) on top of the city:
//   sky       photographed sky (kloofendal 48d partly cloudy) as the skybox and ambient light; the sun light
//             is turned to where the sun is in the photo, fog takes the colour of its horizon
//   surfaces  2K scanned textures with roughness on asphalt, promenade, sea wall, beach, kerbs, sidewalks,
//             rubble; chipped plaster as the close-up detail on every building wall
//   rocks     the angular breakwater blocks replaced by a scanned boulder (2 detail levels)
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod LookPro.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class LookPro
{
    const string Root = "Assets/Benghazi", PH2 = Root + "/Textures/PolyHaven2k", Gen = Root + "/Textures/Generated", Mats = Root + "/Materials";
    const string SkyFile = Root + "/Sky/kloofendal_48d_partly_cloudy_puresky_4k.hdr";

    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Build();
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    public static void Build()
    {
        // the knife's mesh is read at run time to turn it the right way round
        if (AssetImporter.GetAtPath("Assets/Resources/Guns/knife.fbx") is ModelImporter mi && !mi.isReadable) { mi.isReadable = true; mi.SaveAndReimport(); }
        Sky();
        Surfaces();
        Rocks();
        AssetDatabase.SaveAssets();
    }

    // ---------- sky ----------
    static void Sky()
    {
        if (!File.Exists(SkyFile)) { Debug.LogWarning("[pro] no sky file"); return; }
        var ti = (TextureImporter)AssetImporter.GetAtPath(SkyFile);
        ti.textureShape = TextureImporterShape.Texture2D; ti.sRGBTexture = false; ti.mipmapEnabled = false;
        ti.wrapModeU = TextureWrapMode.Repeat; ti.wrapModeV = TextureWrapMode.Clamp; ti.maxTextureSize = 4096;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
        ti.SaveAndReimport();
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyFile);
        var mat = new Material(Shader.Find("Skybox/Panoramic")) { name = "SkyPhoto" };
        mat.SetTexture("_MainTex", tex); mat.SetFloat("_Mapping", 1); mat.SetFloat("_ImageType", 0); mat.SetFloat("_Exposure", 1.0f); mat.SetFloat("_Rotation", 0);
        mat.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
        AssetDatabase.DeleteAsset(Root + "/SkyPhoto.mat"); AssetDatabase.CreateAsset(mat, Root + "/SkyPhoto.mat");
        RenderSettings.skybox = mat;
        RenderSettings.ambientMode = AmbientMode.Skybox; RenderSettings.ambientIntensity = 1.0f;

        // where is the sun in the photo? (brightest spot of the lat-long image)
        var (W, H, px) = ReadHdr(SkyFile, 4);
        int bi = 0; float bv = 0;
        for (int i = 0; i < px.Length; i++) { float l = px[i].r + px[i].g + px[i].b; if (l > bv) { bv = l; bi = i; } }
        int r = bi / W, c = bi % W;
        // same mapping as Unity's Skybox/Panoramic shader: u = 0.5 - atan2(z, x) / 2pi, row 0 = straight up
        float lat = (r + 0.5f) / H * Mathf.PI, phi = (0.5f - (c + 0.5f) / W) * 2 * Mathf.PI;
        var sunDir = new Vector3(Mathf.Sin(lat) * Mathf.Cos(phi), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(phi)).normalized;
        var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
        if (sun)
        {
            sun.transform.rotation = Quaternion.LookRotation(-sunDir);
            sun.intensity = 1.7f; sun.color = new Color(1f, 0.95f, 0.86f); sun.shadowStrength = 0.9f;
        }
        // haze the colour of the photo's horizon
        Color hz = Color.black; int n = 0;
        for (int x = 0; x < W; x += 4) { var p = px[(int)(H * 0.47f) * W + x]; hz += p; n++; }
        hz /= Mathf.Max(1, n);
        float m = Mathf.Max(hz.r, Mathf.Max(hz.g, hz.b));
        RenderSettings.fogColor = m > 0 ? new Color(hz.r / m, hz.g / m, hz.b / m) * 0.86f : RenderSettings.fogColor;
        RenderSettings.fogDensity = 0.0028f;
        DynamicGI.UpdateEnvironment();
        Debug.Log($"[pro] sky: sun at elevation {90 - lat * Mathf.Rad2Deg:F0} deg, direction {sunDir}, fog {RenderSettings.fogColor}");
    }

    // minimal Radiance .hdr reader (new-style RLE), every 'step'-th pixel
    static (int w, int h, Color[] px) ReadHdr(string path, int step)
    {
        var b = File.ReadAllBytes(path); int p = 0;
        string Line() { var s = new System.Text.StringBuilder(); while (p < b.Length && b[p] != '\n') s.Append((char)b[p++]); p++; return s.ToString(); }
        string l; do { l = Line(); } while (l.Length > 0);
        var dims = Line().Split(' '); int H = int.Parse(dims[1]), W = int.Parse(dims[3]);
        int w = W / step, h = H / step; var outp = new Color[w * h];
        var scan = new byte[W * 4];
        for (int y = 0; y < H; y++)
        {
            if (b[p] == 2 && b[p + 1] == 2)
            {
                p += 4;
                for (int ch = 0; ch < 4; ch++)
                    for (int x = 0; x < W;)
                    {
                        int count = b[p++];
                        if (count > 128) { count -= 128; byte v = b[p++]; for (int k = 0; k < count; k++) scan[(x++) * 4 + ch] = v; }
                        else for (int k = 0; k < count; k++) scan[(x++) * 4 + ch] = b[p++];
                    }
            }
            else { System.Array.Copy(b, p, scan, 0, W * 4); p += W * 4; }
            if (y % step != 0 || y / step >= h) continue;
            for (int x = 0; x < w; x++)
            {
                int i = x * step * 4; int e = scan[i + 3];
                float f = e == 0 ? 0 : Mathf.Pow(2, e - 136);
                outp[(y / step) * w + x] = new Color(scan[i] * f, scan[i + 1] * f, scan[i + 2] * f);
            }
        }
        return (w, h, outp);
    }

    // ---------- scanned surfaces ----------
    static void Surfaces()
    {
        int n = 0;
        n += Apply("Asphalt", "worn_asphalt", new Color(0.44f, 0.43f, 0.42f));         // dark as the road was, but old: worn, cracked, faded
        n += Apply("Promenade", "precast_stone_paving", new Color(1.05f, 1.02f, 0.97f));
        n += Apply("SeaWall", "painted_plaster_wall", new Color(1.08f, 1.06f, 1.02f));
        n += Apply("Sand", "coast_sand_02", new Color(1.02f, 0.96f, 0.86f));
        n += Apply("Seafront_Sand", "coast_sand_02", new Color(1.05f, 0.97f, 0.85f));
        n += Apply("Seafront_WetSand", "damp_beach_sand", new Color(0.86f, 0.8f, 0.72f));
        n += Apply("Seafront_Balustrade", "painted_plaster_wall", new Color(1.1f, 1.08f, 1.04f));
        n += Apply("Seafront_MedianKerb", "painted_plaster_wall", new Color(1.0f, 0.99f, 0.95f));
        n += Apply("Detail_Sidewalk", "concrete_floor_worn_001", new Color(1.0f, 0.97f, 0.92f));
        n += Apply("Detail_RubbleChunk", "broken_wall", new Color(0.95f, 0.9f, 0.82f));
        n += Apply("Detail_Kerb", "concrete_floor_worn_001", new Color(0.95f, 0.94f, 0.9f));
        n += Apply("Detail_Barrier", "concrete_floor_worn_001", new Color(0.92f, 0.9f, 0.86f));
        n += Apply("Detail_RoofRoom", "damaged_plaster", new Color(0.9f, 0.84f, 0.74f));
        // every building wall: chipped, patched plaster close up
        var detA = Tex($"{PH2}/damaged_plaster_diff_2k.jpg", false); var detN = Tex($"{PH2}/damaged_plaster_nor_gl_2k.jpg", true);
        int walls = 0;
        foreach (var path in Directory.GetFiles(Mats, "Facade_*.mat"))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\', '/')); if (!m) continue;
            m.SetTexture("_DetailAlbedoMap", detA); m.SetTexture("_DetailNormalMap", detN);
            m.SetFloat("_DetailAlbedoMapScale", 0.45f); m.SetFloat("_DetailNormalMapScale", 1.1f);
            m.SetTextureScale("_DetailAlbedoMap", new Vector2(1.2f, 1.2f));
            m.EnableKeyword("_DETAIL_MULX2");
            EditorUtility.SetDirty(m); walls++;
        }
        Debug.Log($"[pro] scanned surfaces on {n} materials, plaster detail on {walls} wall materials");
    }

    static readonly Dictionary<string, Texture2D> texCache = new();
    static Texture2D Tex(string path, bool normal)
    {
        if (texCache.TryGetValue(path, out var t)) return t;
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        if (ti)
        {
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = !normal; ti.wrapMode = TextureWrapMode.Repeat; ti.anisoLevel = 8; ti.maxTextureSize = 2048; ti.mipmapEnabled = true;
            ti.SaveAndReimport();
        }
        t = AssetDatabase.LoadAssetAtPath<Texture2D>(path); texCache[path] = t; return t;
    }

    // roughness -> URP's metallic/smoothness map (smoothness in alpha)
    static Texture2D Gloss(string id)
    {
        var outPath = $"{Gen}/{id}_gloss.png";
        if (!File.Exists(outPath))
        {
            var src = new Texture2D(2, 2); src.LoadImage(File.ReadAllBytes($"{PH2}/{id}_rough_2k.jpg"));
            int w = src.width / 2, h = src.height / 2; var o = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { float r = src.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r; px[y * w + x] = new Color(0, 0, 0, 1 - r); }
            o.SetPixels(px); o.Apply();
            File.WriteAllBytes(outPath, o.EncodeToPNG());
            Object.DestroyImmediate(src); Object.DestroyImmediate(o);
            AssetDatabase.ImportAsset(outPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath); ti.sRGBTexture = false; ti.alphaIsTransparency = false; ti.wrapMode = TextureWrapMode.Repeat; ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
    }

    static int Apply(string matName, string id, Color tint)
    {
        var path = $"{Mats}/{matName}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m || !File.Exists($"{PH2}/{id}_diff_2k.jpg")) return 0;
        var scale = m.GetTextureScale("_BaseMap");
        m.SetTexture("_BaseMap", Tex($"{PH2}/{id}_diff_2k.jpg", false));
        m.SetTextureScale("_BaseMap", scale);
        m.SetTexture("_BumpMap", Tex($"{PH2}/{id}_nor_gl_2k.jpg", true)); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP");
        if (File.Exists($"{PH2}/{id}_rough_2k.jpg"))
        {
            m.SetTexture("_MetallicGlossMap", Gloss(id)); m.EnableKeyword("_METALLICSPECGLOSSMAP");
            m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessTextureChannel", 0); m.SetFloat("_Metallic", 0);
        }
        m.SetColor("_BaseColor", tint);
        EditorUtility.SetDirty(m);
        return 1;
    }

    // ---------- breakwater: scanned boulders ----------
    static void Rocks()
    {
        var near = AssetDatabase.LoadAllAssetsAtPath(Root + "/Models/Rocks/boulder.glb").OfType<Mesh>().FirstOrDefault();
        var far = AssetDatabase.LoadAllAssetsAtPath(Root + "/Models/Rocks/boulder_lod.glb").OfType<Mesh>().FirstOrDefault();
        var srcMat = AssetDatabase.LoadAllAssetsAtPath(Root + "/Models/Rocks/boulder.glb").OfType<Material>().FirstOrDefault();
        if (!near || !srcMat) { Debug.LogWarning("[pro] no boulder model"); return; }
        // URP copy of the scanned material
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Boulder" };
        var baseTex = srcMat.HasProperty("baseColorTexture") ? srcMat.GetTexture("baseColorTexture") : srcMat.mainTexture;
        var nrm = srcMat.HasProperty("normalTexture") ? srcMat.GetTexture("normalTexture") : null;
        mat.SetTexture("_BaseMap", baseTex); mat.SetColor("_BaseColor", new Color(0.74f, 0.75f, 0.78f)); mat.SetFloat("_Smoothness", 0.15f);
        if (nrm) { mat.SetTexture("_BumpMap", nrm); mat.EnableKeyword("_NORMALMAP"); }
        mat.enableInstancing = true;
        AssetDatabase.DeleteAsset($"{Mats}/Boulder.mat"); AssetDatabase.CreateAsset(mat, $"{Mats}/Boulder.mat");

        var old = GameObject.Find("Breakwater"); if (old) Object.DestroyImmediate(old);
        var root = new GameObject("Breakwater");
        var city = GameObject.Find("Benghazi Corniche");
        var rnd = new System.Random(5);
        int count = 0;
        var size = near.bounds.size;
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!r.sharedMaterial || r.sharedMaterial.name != "Rock") continue;          // (already hidden on a re-run)
            var b = r.bounds; if (b.size.x > 6 || b.size.z > 6 || b.size.y > 4) continue;
            r.enabled = false; var col = r.GetComponent<Collider>(); if (col) col.enabled = false;
            float s = Mathf.Max(b.size.x, b.size.z) / Mathf.Max(size.x, size.z) * 1.15f;
            var go = new GameObject("Boulder"); go.transform.SetParent(root.transform, false); go.isStatic = true;
            go.transform.SetPositionAndRotation(new Vector3(b.center.x, b.min.y - 0.15f * s, b.center.z), Quaternion.Euler((float)rnd.NextDouble() * 20 - 10, (float)rnd.NextDouble() * 360, (float)rnd.NextDouble() * 20 - 10));
            go.transform.localScale = new Vector3(s, s * (0.75f + (float)rnd.NextDouble() * 0.4f), s);
            Renderer Part(Mesh m2, string nm)
            {
                var c = new GameObject(nm); c.transform.SetParent(go.transform, false); c.isStatic = true;
                c.AddComponent<MeshFilter>().sharedMesh = m2; var mr = c.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; return mr;
            }
            var lg = go.AddComponent<LODGroup>();
            var r0 = Part(near, "LOD0");
            if (far) lg.SetLODs(new[] { new LOD(0.08f, new[] { r0 }), new LOD(0.012f, new[] { Part(far, "LOD1") }) });
            else lg.SetLODs(new[] { new LOD(0.012f, new[] { r0 }) });
            lg.RecalculateBounds();
            var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = far ? far : near;
            count++;
        }
        Debug.Log($"[pro] breakwater: {count} scanned boulders");
    }
}
