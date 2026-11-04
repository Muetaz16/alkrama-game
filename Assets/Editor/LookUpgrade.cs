// Makes the Corniche look real: scanned textures (Poly Haven, CC0) on ground, roads, walls and rocks,
// a sea floor that slopes away from the shore, the Benghazi/Water shader, better palm fronds,
// ambient occlusion and sky reflections.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod LookUpgrade.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class LookUpgrade
{
    // whole scene from scratch: city -> soldier -> look
    public static void All()
    {
        SceneBuilder.Build();
        SoldierSetup.PlaceInScene(AssetDatabase.LoadAssetAtPath<GameObject>(SoldierSetup.PrefabPath));
        Run();
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        GameplaySetup.Run();
        EditorSceneManager.SaveScene(scene);
    }

    const string Root = "Assets/Benghazi";
    const string PH = Root + "/Textures/PolyHaven";
    const string Gen = Root + "/Textures/Generated";
    const string Mats = Root + "/Materials";

    public static void Run()
    {
        Directory.CreateDirectory(Gen); Directory.CreateDirectory(Mats);
        foreach (var f in Directory.GetFiles(Mats, "Mesh_*.asset")) AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
        SetupTextures();
        SetupRenderer();
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        var city = GameObject.Find("Benghazi Corniche");

        SeaFloor(city);
        Materials(city);
        Water();
        Environment();

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Shots();
        Debug.Log("[look] done");
    }

    // ---------- textures ----------
    static void SetupTextures()
    {
        foreach (var f in Directory.GetFiles(PH, "*.jpg"))
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(f.Replace('\\', '/'));
            bool normal = f.Contains("_nor_");
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = !normal;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.anisoLevel = 8;
            ti.maxTextureSize = 1024;
            ti.SaveAndReimport();
        }
        WritePng(Gen + "/WaveNormal.png", WaveNormal(512), true);
        WritePng(Gen + "/FrondGreen.png", Frond(256, 1024, new Color(0.20f, 0.32f, 0.10f), new Color(0.38f, 0.50f, 0.18f), 0.05f, 1), false);
        WritePng(Gen + "/FrondDry.png", Frond(256, 1024, new Color(0.30f, 0.22f, 0.12f), new Color(0.46f, 0.35f, 0.19f), 0.3f, 2), false);
        WritePng(Gen + "/FrondBurnt.png", Frond(256, 1024, new Color(0.13f, 0.10f, 0.07f), new Color(0.30f, 0.23f, 0.14f), 0.4f, 3), false);
    }

    static void WritePng(string path, Texture2D tex, bool normal)
    {
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.alphaIsTransparency = !normal;
        ti.wrapMode = normal ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        ti.mipmapEnabled = true;
        ti.anisoLevel = 4;
        ti.SaveAndReimport();
    }

    // tileable wave height (sum of waves with whole-number frequencies) -> normal map
    static Texture2D WaveNormal(int n)
    {
        var rnd = new System.Random(7);
        var waves = Enumerable.Range(0, 28).Select(k =>
        {
            int fx = rnd.Next(-9, 10), fy = rnd.Next(1, 10) * (rnd.Next(2) * 2 - 1);
            float f = Mathf.Sqrt(fx * fx + fy * fy);
            return (fx, fy, amp: 1f / Mathf.Pow(f, 1.4f), ph: (float)rnd.NextDouble() * 6.283f);
        }).ToArray();
        var h = new float[n, n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float s = 0, u = x / (float)n * 6.2832f, v = y / (float)n * 6.2832f;
            foreach (var w in waves) { float a = Mathf.Sin(u * w.fx + v * w.fy + w.ph); s += w.amp * (1 - Mathf.Abs(a)) * 1.3f - w.amp * 0.5f * a; }
            h[x, y] = s;
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = h[(x + 1) % n, y] - h[(x + n - 1) % n, y], dy = h[x, (y + 1) % n] - h[x, (y + n - 1) % n];
            var nn = new Vector3(-dx * 2.2f, -dy * 2.2f, 1).normalized;
            tex.SetPixel(x, y, new Color(nn.x * 0.5f + 0.5f, nn.y * 0.5f + 0.5f, nn.z * 0.5f + 0.5f, 1));
        }
        tex.Apply();
        return tex;
    }

    // palm frond: midrib down the middle (v = along the frond), leaflets angled out to both sides,
    // transparent between them; ragged/ missing leaflets for dry and burnt fronds
    static Texture2D Frond(int w, int h, Color dark, Color light, float ragged, int seed)
    {
        var rnd = new System.Random(seed);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color(dark.r, dark.g, dark.b, 0);
        int count = 36;
        for (int k = 0; k < count; k++)
        {
            float v0 = 0.04f + k / (float)count * 0.92f;
            float lenScale = Mathf.Clamp01(Mathf.Sin(Mathf.Clamp01(v0 * 1.25f) * Mathf.PI) * 1.1f) * 0.85f + 0.12f;   // longest a third of the way up, short at the tip
            foreach (int side in new[] { -1, 1 })
            {
                if (rnd.NextDouble() < ragged * 0.6) continue;                         // missing leaflet
                float len = (0.47f * lenScale) * (1 - (float)rnd.NextDouble() * ragged * 0.6f);
                var shade = Color.Lerp(dark, light, (float)rnd.NextDouble());
                for (float s = 0; s < len; s += 0.6f / w)
                {
                    float u = 0.5f + side * s;
                    float v = v0 + s * 0.45f;                                             // leaflets sweep toward the tip
                    float width = 0.006f * (1 - s / len * 0.75f);
                    int cx = Mathf.RoundToInt(u * w), cy = Mathf.RoundToInt(v * h);
                    int r = Mathf.Max(1, Mathf.RoundToInt(width * h));
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int yy = cy + dy, xx = cx; if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                        float edge = 1 - Mathf.Abs(dy) / (float)(r + 1);
                        var c = Color.Lerp(shade * 0.8f, shade, edge); c.a = 1;
                        px[yy * w + xx] = c;
                        if (xx + 1 < w) px[yy * w + xx + 1] = c;
                    }
                }
            }
        }
        // midrib
        for (int y = 0; y < h; y++) for (int x = w / 2 - 2; x <= w / 2 + 2; x++) px[y * w + x] = Color.Lerp(light, dark, 0.3f) * new Color(1.1f, 1.05f, 0.9f, 1);
        tex.SetPixels(px); tex.Apply();
        return tex;
    }

    // ---------- renderer: depth texture for the water, SSAO ----------
    static void SetupRenderer()
    {
        foreach (var path in new[] { "Assets/Settings/URP_PC.asset", "Assets/Settings/URP_Mobile.asset" })
        {
            var a = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            a.supportsCameraDepthTexture = true;
            EditorUtility.SetDirty(a);
        }
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP_Renderer.asset");
        var ssaoType = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
        if (ssaoType != null)
        {
            foreach (var f in data.rendererFeatures.Where(f => !f || f.GetType() == ssaoType).ToList())
            { data.rendererFeatures.Remove(f); if (f) Object.DestroyImmediate(f, true); }
            var ssao = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
            ssao.name = "SSAO";
            AssetDatabase.AddObjectToAsset(ssao, data);
            data.rendererFeatures.Add(ssao);
            // stronger, wider occlusion than the default
            var so = new SerializedObject(ssao);
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.name == "Intensity" && it.propertyType == SerializedPropertyType.Float) it.floatValue = 1.4f;
                if (it.name == "Radius" && it.propertyType == SerializedPropertyType.Float) it.floatValue = 0.6f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
            Debug.Log("[look] SSAO added");
        }
        AssetDatabase.SaveAssets();
    }

    // ---------- sea floor: land at 0, under water it slopes down with distance from the shore ----------
    static void SeaFloor(GameObject city)
    {
        var map = JsonUtility.FromJson<MapInfo>(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        float half = map.half, cell = map.seaCell;
        int n = Mathf.RoundToInt(2 * half / cell);
        var sea = new bool[n, n];
        foreach (var s in SceneBuilder.ParseSea())
        {
            int j = Mathf.Clamp(Mathf.FloorToInt((s[2] + half) / cell + 0.01f), 0, n - 1);
            int i0 = Mathf.Clamp(Mathf.RoundToInt((s[0] + half) / cell), 0, n), i1 = Mathf.Clamp(Mathf.RoundToInt((s[1] + half) / cell), 0, n);
            for (int i = i0; i < i1; i++) sea[i, j] = true;
        }
        // distance (cells) from land, breadth-first
        var dist = new int[n, n];
        var q = new Queue<(int, int)>();
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { dist[i, j] = sea[i, j] ? int.MaxValue : 0; if (!sea[i, j]) q.Enqueue((i, j)); }
        while (q.Count > 0)
        {
            var (i, j) = q.Dequeue();
            foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= n || b >= n || dist[a, b] <= dist[i, j] + 1) continue;
                dist[a, b] = dist[i, j] + 1; q.Enqueue((a, b));
            }
        }
        float Depth(int i, int j)
        {
            if (i < 0 || j < 0 || i >= n || j >= n) return -12f;
            if (!sea[i, j]) return 0f;
            float d = dist[i, j] * cell;
            return -Mathf.Min(0.6f + d * 0.09f + d * d * 0.0012f, 12f);
        }
        // vertices at cell corners: average of the 4 touching cells (land wins so the shore stays sharp)
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
        {
            float y = 0; bool land = false;
            foreach (var (a, b) in new[] { (i - 1, j - 1), (i, j - 1), (i - 1, j), (i, j) })
            { if (a >= 0 && b >= 0 && a < n && b < n && !sea[a, b]) land = true; y += Depth(a, b); }
            y = land ? -0.04f : y / 4;    // just under the paving and roads so they always show
            float wx = -half + i * cell, wz = -half + j * cell;     // web coordinates
            verts.Add(new Vector3(-wx, y, wz));                      // web x -> Unity -x
            uvs.Add(new Vector2(-wx, wz) / 4f);
        }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            tris.AddRange(new[] { a, c, b, b, c, d });
        }
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "SeaFloorGround" };
        mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        // make sure it faces up
        var nrm = Vector3.Cross(verts[n + 1] - verts[0], verts[1] - verts[0]);
        if (nrm.y < 0) { var t = mesh.triangles; for (int k = 0; k < t.Length; k += 3) (t[k + 1], t[k + 2]) = (t[k + 2], t[k + 1]); mesh.triangles = t; }
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
        AssetDatabase.DeleteAsset(Root + "/SeaFloor.asset");
        AssetDatabase.CreateAsset(mesh, Root + "/SeaFloor.asset");

        var old = GameObject.Find("Ground (sea floor)"); if (old) Object.DestroyImmediate(old);
        var go = new GameObject("Ground (sea floor)");
        go.isStatic = true;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = Lit("Sand", "coast_sand_01", Color.white * 0.97f, 0.1f, 1f);
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        // hide the old flat ground (the 460 m plane)
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>())
            if (r.bounds.size.x > 400 && r.bounds.size.y < 0.5f && r.bounds.size.x < 500 && !r.sharedMaterial.GetTexture("baseColorTexture"))
            { r.gameObject.SetActive(false); Debug.Log("[look] old ground hidden " + r.name); }
        Debug.Log($"[look] sea floor {n}x{n} cells");
    }
    [System.Serializable] class MapInfo { public float half; public float seaCell; }

    // ---------- materials ----------
    static readonly Dictionary<string, Material> cache = new();

    static Material Lit(string name, string ph, Color tint, float smooth, float bump, Texture baseOverride = null)
    {
        var key = name;
        if (cache.TryGetValue(key, out var m)) return m;
        var path = $"{Mats}/{name}.mat";
        m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        var diff = baseOverride ? baseOverride : (ph != null ? AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_diff_1k.jpg") : null);
        var nor = ph != null ? AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/{ph}_nor_gl_1k.jpg") : null;
        m.SetTexture("_BaseMap", diff); m.SetColor("_BaseColor", tint);
        m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", 0);
        if (nor) { m.SetTexture("_BumpMap", nor); m.SetFloat("_BumpScale", bump); m.EnableKeyword("_NORMALMAP"); }
        EditorUtility.SetDirty(m);
        cache[key] = m;
        return m;
    }

    static Material Frond(string name, string tex, Color tint)
    {
        var m = Lit(name, null, tint, 0.15f, 0, AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{tex}.png"));
        m.SetFloat("_AlphaClip", 1); m.SetFloat("_Cutoff", 0.4f); m.EnableKeyword("_ALPHATEST_ON");
        m.SetFloat("_Cull", 0); m.doubleSidedGI = true;
        m.renderQueue = (int)RenderQueue.AlphaTest;
        return m;
    }

    // facade: keep the window texture, add real plaster grain on top (URP detail maps)
    static Material Facade(Material src)
    {
        var tex = src.GetTexture("baseColorTexture");
        var key = "Facade_" + tex.name;
        if (cache.TryGetValue(key, out var m)) return m;
        var path = $"{Mats}/{key}.mat";
        m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        m.SetTexture("_BaseMap", tex);
        var st = src.GetVector("baseColorTexture_ST");
        m.SetTextureScale("_BaseMap", new Vector2(st.x, st.y)); m.SetTextureOffset("_BaseMap", new Vector2(st.z, st.w));
        m.SetColor("_BaseColor", (Color)src.GetColor("baseColorFactor") * new Color(0.93f, 0.85f, 0.72f));   // Benghazi sandstone, not white
        m.SetFloat("_Smoothness", 0.12f);
        m.SetTexture("_DetailAlbedoMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/plastered_wall_02_diff_1k.jpg"));
        m.SetTexture("_DetailNormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/plastered_wall_02_nor_gl_1k.jpg"));
        m.SetFloat("_DetailAlbedoMapScale", 0.8f); m.SetFloat("_DetailNormalMapScale", 0.9f);
        m.SetTextureScale("_DetailAlbedoMap", new Vector2(1.5f, 1.5f));
        m.EnableKeyword("_DETAIL_MULX2");
        EditorUtility.SetDirty(m);
        cache[key] = m;
        return m;
    }

    static bool Near(Color a, float r, float g, float b) => Mathf.Abs(a.r - r) < 0.02f && Mathf.Abs(a.g - g) < 0.02f && Mathf.Abs(a.b - b) < 0.02f;

    static void Materials(GameObject city)
    {
        int changed = 0;
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            var size = r.bounds.size;
            for (int k = 0; k < mats.Length; k++)
            {
                var src = mats[k]; if (!src || !src.HasProperty("baseColorFactor")) continue;
                var tex = src.GetTexture("baseColorTexture");
                var tn = tex ? tex.name : "";
                var c = src.GetColor("baseColorFactor");
                var path = NamePath(r.transform);
                Material dst = null;
                if (path.Contains("palm_green")) dst = Frond("FrondGreen", "FrondGreen", Color.white);
                else if (path.Contains("palm_dry")) dst = Frond("FrondDry", "FrondDry", Color.white);
                else if (path.Contains("palm_burnt")) dst = Frond("FrondBurnt", "FrondBurnt", Color.white);
                else if (path.Contains("palm_trunk")) continue;
                else if (path.Contains("roads") || (path.Contains("roundabout") && !tex && Near(c, 0.23f, 0.23f, 0.23f))) { dst = Lit("Asphalt", "asphalt_02", new Color(0.82f, 0.8f, 0.78f), 0.18f, 1f); PlanarUV(r, 0.18f); }
                else if (!tex && size.x > 1000 && Near(c, 0.33f, 0.32f, 0.29f)) { dst = Lit("Asphalt", "asphalt_02", new Color(0.82f, 0.8f, 0.78f), 0.18f, 1f); PlanarUV(r, 0.18f); }
                else if (tn == "image_24" || (!tex && size.x > 100 && size.z > 200 && Near(c, 0.89f, 0.87f, 0.82f))) { dst = Lit("SeaWall", "plastered_wall_02", new Color(1.04f, 1.03f, 1.0f), 0.25f, 0.5f); PlanarUV(r, 0.25f); }
                else if (tn == "image_25") { dst = Lit("Promenade", "large_grey_tiles", new Color(1.35f, 1.32f, 1.25f), 0.22f, 0.8f); PlanarUV(r, 0.22f); }
                else if (tex && size.x > 300 && (tex.width == 128 || tex.width == 64 || tex.width == 512) && tn != "image_25") dst = Facade(src);
                else if (false && tn == "image_27") dst = Frond("FrondGreen", "FrondGreen", Color.white);
                else if (false && tn == "image_28") dst = Frond("FrondDry", "FrondDry", Color.white);
                else if (false && tn == "image_29") dst = Frond("FrondBurnt", "FrondBurnt", Color.white);
                else if (!tex && (Near(c, 0.31f, 0.29f, 0.27f) || Near(c, 0.56f, 0.54f, 0.51f))) dst = Lit("Rock", "rock_boulder_dry", new Color(0.85f, 0.82f, 0.78f), 0.15f, 1.2f);
                else if (!tex && Near(c, 0.60f, 0.55f, 0.45f)) dst = Lit("Rubble", "concrete_wall_008", new Color(0.86f, 0.8f, 0.68f), 0.08f, 1f);
                else if (!tex && size.x > 30 && size.y < 3 && Near(c, 0.91f, 0.89f, 0.85f)) { dst = Lit("LighthouseBase", "concrete_wall_008", new Color(0.98f, 0.96f, 0.92f), 0.1f, 0.8f); PlanarUV(r, 0.35f); }
                if (dst)
                {
                    // keep double-sided surfaces double-sided (the web export's floors face down)
                    if (src.HasProperty("_Cull") && src.GetFloat("_Cull") == 0 && dst.GetFloat("_Cull") != 0) { dst.SetFloat("_Cull", 0); dst.doubleSidedGI = true; }
                    mats[k] = dst; changed++;
                }
            }
            r.sharedMaterials = mats;
        }
        Debug.Log($"[look] materials replaced {changed}");
    }

    static string NamePath(Transform t) { var s = ""; for (; t; t = t.parent) s += t.name + "/"; return s; }

    // new UVs in metres from world position, picked per vertex by the normal (floors use x/z, walls use the wall plane)
    static void PlanarUV(Renderer r, float scale)
    {
        var mf = r.GetComponent<MeshFilter>(); if (!mf || !mf.sharedMesh) return;
        if (AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(Mats)) return;   // already done
        var m = Object.Instantiate(mf.sharedMesh);
        var v = m.vertices; var nrm = m.normals; var uv = new Vector2[v.Length];
        var t = r.transform;
        for (int i = 0; i < v.Length; i++)
        {
            var p = t.TransformPoint(v[i]); var n = nrm.Length > 0 ? t.TransformDirection(nrm[i]) : Vector3.up;
            var a = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
            uv[i] = a.y >= a.x && a.y >= a.z ? new Vector2(p.x, p.z) : a.x >= a.z ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
            uv[i] *= scale;
        }
        m.uv = uv;
        if (m.bounds.size.y < 0.2f) m.normals = Enumerable.Repeat(Vector3.up, v.Length).ToArray();   // flat floors: all normals up
        m.RecalculateTangents();
        AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{Mats}/Mesh_{r.name}.asset"));
        mf.sharedMesh = m;
        var mc = r.GetComponent<MeshCollider>(); if (mc) mc.sharedMesh = m;
    }

    // ---------- sea ----------
    static void Water()
    {
        var sea = GameObject.Find("Sea");
        var m = new Material(Shader.Find("Benghazi/Water"));
        m.SetTexture("_WaveNormal", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/WaveNormal.png"));
        AssetDatabase.DeleteAsset($"{Mats}/Water.mat");
        AssetDatabase.CreateAsset(m, $"{Mats}/Water.mat");
        sea.GetComponent<MeshRenderer>().sharedMaterial = m;
        sea.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        var p = sea.transform.position; p.y = -0.22f; sea.transform.position = p;   // below the promenade edge so it never shows on the land side
        Debug.Log("[look] water shader " + (m.shader.isSupported ? "ok" : "NOT SUPPORTED"));
    }

    // ---------- light, sky, reflections ----------
    static void Environment()
    {
        var sun = Object.FindFirstObjectByType<Light>();
        sun.intensity = 1.35f; sun.color = new Color(1f, 0.92f, 0.78f);
        sun.transform.rotation = Quaternion.Euler(32f, -40f, 0f);
        sun.shadowStrength = 0.85f;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.reflectionIntensity = 1f;
        RenderSettings.fogDensity = 0.0022f;
        RenderSettings.fogColor = new Color(0.78f, 0.80f, 0.80f);
        var sky = RenderSettings.skybox;
        sky.SetFloat("_SunSize", 0.035f); sky.SetFloat("_AtmosphereThickness", 0.95f);
        sky.SetColor("_SkyTint", new Color(0.5f, 0.62f, 0.8f)); sky.SetFloat("_Exposure", 1.15f);

        var old = GameObject.Find("Environment"); if (old) Object.DestroyImmediate(old);
        var env = new GameObject("Environment");
        var probe = env.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.size = new Vector3(2000, 400, 2000);
        probe.resolution = 256;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.cullingMask = 0;    // sky only: cheap, and it is what the sea mostly reflects
        env.AddComponent<EnvironmentInit>().probe = probe;
        DynamicGI.UpdateEnvironment();

        var vol = Object.FindFirstObjectByType<Volume>();
        var prof = vol.sharedProfile;
        if (prof.TryGet<ColorAdjustments>(out var ca)) { ca.postExposure.Override(-0.1f); ca.contrast.Override(14f); ca.saturation.Override(4f); }
        if (prof.TryGet<Bloom>(out var bl)) { bl.intensity.Override(0.5f); bl.threshold.Override(0.95f); }
        if (!prof.Has<ShadowsMidtonesHighlights>()) { var smh = prof.Add<ShadowsMidtonesHighlights>(true); smh.shadows.Override(new Vector4(0.95f, 0.98f, 1.06f, 0)); smh.highlights.Override(new Vector4(1.05f, 1.0f, 0.94f, 0)); }
        EditorUtility.SetDirty(prof);
    }

    // ---------- comparison shots ----------
    static void Shots()
    {
        var cam = Object.FindFirstObjectByType<Camera>();
        var keep = (cam.transform.position, cam.transform.rotation);
        var lh = SceneBuilder.FindLighthouse();
        var player = GameObject.Find("Player");
        // 1. behind the soldier on the Corniche
        if (player)
        {
            var t = player.transform;
            cam.transform.position = t.position + t.rotation * new Vector3(0.55f, 1.75f, -3.2f);
            cam.transform.rotation = t.rotation * Quaternion.Euler(8, 0, 0);
            SceneBuilder.Shoot(cam, "look_corniche.png");
        }
        // 2. the lighthouse from the water's edge
        var map = JsonUtility.FromJson<LhMap>(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        var sd = map.landmarks.First(l => l.type == "lighthouse").seaDir;
        var seaDir = new Vector3(-sd[0], 0, sd[1]).normalized;
        cam.transform.position = lh + seaDir * 48f + Vector3.Cross(Vector3.up, seaDir) * 18f + Vector3.up * 3f;
        cam.transform.LookAt(lh + Vector3.up * 10f);
        SceneBuilder.Shoot(cam, "look_lighthouse.png");
        // 3. looking out over the sea from the promenade
        if (player)
        {
            var t = player.transform;
            cam.transform.position = t.position + Vector3.up * 2.2f;
            var side = Quaternion.Euler(0, -75f, 0) * t.forward;
            cam.transform.rotation = Quaternion.LookRotation(side) * Quaternion.Euler(6, 0, 0);
            SceneBuilder.Shoot(cam, "look_sea.png");
        }
        // 4. a palm close up
        var palm = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).FirstOrDefault(x => x.sharedMaterial && x.sharedMaterial.name == "FrondGreen" && player && Vector3.Distance(x.bounds.center, player.transform.position) < 60);
        if (palm)
        {
            var c = palm.bounds.center;
            cam.transform.position = c + new Vector3(6f, -3.5f, 6f);
            cam.transform.LookAt(c + Vector3.down * 1.5f);
            SceneBuilder.Shoot(cam, "look_palm.png");
        }
        cam.transform.SetPositionAndRotation(keep.Item1, keep.Item2);
    }
    [System.Serializable] class LhLandmark { public string type; public float[] seaDir; }
    [System.Serializable] class LhMap { public LhLandmark[] landmarks; }
}
