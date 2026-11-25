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
        Seafront.Build();
        CityDetail.Build();
        LookPro.Build();
        GameplaySetup.Run();
        CinematicLook.Apply();          // post-processing stored in the profile asset, soft shadows, grade (see CinematicLook.cs)
        EditorSceneManager.SaveScene(scene);
    }

    // Only the passes the war detail / lighthouse grounds / look touch, then review shots (../unity_shots/war_*.png):
    //   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod LookUpgrade.War
    public static void War()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Seafront.Build();
        CityDetail.Build();
        LookPro.Build();
        GameplaySetup.Run();
        CinematicLook.Apply();
        RetintFacades();
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindFirstObjectByType<Camera>();
        var player = GameObject.Find("Player"); bool was = player && player.activeSelf; if (player) player.SetActive(false);
        cam.fieldOfView = 60f; cam.aspect = 16f / 9f;
        ShaderUtil.allowAsyncCompilation = false;            // no cyan stand-in shaders in the first shot
        var lh = SceneBuilder.FindLighthouse(); var sea = CityDetail.SeaDirection(); var side = Vector3.Cross(Vector3.up, sea);
        void Shot(Vector3 eye, Vector3 at, string file) { cam.transform.position = eye; cam.transform.LookAt(at); SceneBuilder.Shoot(cam, file); }
        Shot(lh + sea * 48f + side * 14f + Vector3.up * 1.7f, lh + Vector3.up * 9f, "war_lighthouse_road.png");
        Shot(lh - sea * 45f - side * 25f + Vector3.up * 1.7f, lh + Vector3.up * 8f, "war_lighthouse_ruins.png");
        Shot(lh + sea * 70f - side * 60f + Vector3.up * 40f, lh, "war_lighthouse_aerial.png");
        foreach (var v in CityDetail.ReviewViews) Shot(v.eye, v.at, v.name);
        if (player) player.SetActive(was);
        Debug.Log($"[war] review shots: 3 lighthouse + {CityDetail.ReviewViews.Count} streets");
    }

    // the web-city facade materials take the tint of their variant (Facade_<tex>[_v<n>][_plain]): absolute, safe to repeat
    static void RetintFacades()
    {
        int n = 0;
        foreach (var guid in AssetDatabase.FindAssets("Facade_ t:Material", new[] { Mats }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (!m || !m.HasProperty("_BaseColor")) continue;
            var mt = System.Text.RegularExpressions.Regex.Match(m.name, @"_v(\d+)");
            int v = mt.Success ? int.Parse(mt.Groups[1].Value) : 0;
            m.SetColor("_BaseColor", FacadeTints[v % FacadeTints.Length]); EditorUtility.SetDirty(m); n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[war] {n} web facade materials retinted to the ochre palette");
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
        WritePng(Gen + "/FrondGreen.png", Frond(512, 2048, new Color(0.17f, 0.29f, 0.09f), new Color(0.40f, 0.52f, 0.20f), 0.04f, 1), false);
        WritePng(Gen + "/FrondDry.png", Frond(512, 2048, new Color(0.32f, 0.24f, 0.13f), new Color(0.55f, 0.43f, 0.24f), 0.3f, 2), false);
        WritePng(Gen + "/FrondBurnt.png", Frond(512, 2048, new Color(0.13f, 0.10f, 0.07f), new Color(0.30f, 0.23f, 0.14f), 0.4f, 3), false);
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
        int count = 68;
        for (int k = 0; k < count; k++)
        {
            float v0 = 0.04f + k / (float)count * 0.92f;
            float lenScale = Mathf.Clamp01(Mathf.Sin(Mathf.Clamp01(v0 * 1.25f) * Mathf.PI) * 1.1f) * 0.85f + 0.12f;   // longest a third of the way up, short at the tip
            foreach (int side in new[] { -1, 1 })
            {
                if (rnd.NextDouble() < ragged * 0.6) continue;                         // missing leaflet
                float len = (0.48f * lenScale) * (1 - (float)rnd.NextDouble() * ragged * 0.6f);
                var shade = Color.Lerp(dark, light, (float)rnd.NextDouble());
                if (rnd.NextDouble() < 0.12) shade = Color.Lerp(shade, new Color(0.62f, 0.55f, 0.3f), 0.5f);   // the odd yellowing leaflet
                for (float s = 0; s < len; s += 0.6f / w)
                {
                    float u = 0.5f + side * s;
                    float v = v0 + s * 0.38f;                                             // leaflets sweep toward the tip
                    float width = 0.0042f * (1 - s / len * 0.8f);
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
        for (int y = 0; y < h; y++) for (int x = w / 2 - 3; x <= w / 2 + 3; x++) px[y * w + x] = Color.Lerp(light, new Color(0.62f, 0.55f, 0.35f), 0.55f);
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
    // Benghazi walls are not one colour: sandstone, grey concrete, ochre, dusty white, pale beige
    // ochre, tan and sand like the streets of Sabri (the owner's reference photo)
    public static readonly Color[] FacadeTints = { new(0.84f, 0.68f, 0.48f), new(0.74f, 0.62f, 0.5f), new(0.8f, 0.62f, 0.42f), new(0.88f, 0.78f, 0.62f), new(0.78f, 0.64f, 0.52f) };
    static Material Facade(Material src, int variant, bool maps = true)
    {
        var tex = src.GetTexture("baseColorTexture");
        var key = "Facade_" + tex.name + (variant > 0 ? "_v" + variant : "") + (maps ? "" : "_plain");
        if (cache.TryGetValue(key, out var m)) return m;
        var path = $"{Mats}/{key}.mat";
        m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        m.SetTexture("_BaseMap", tex);
        var st = src.GetVector("baseColorTexture_ST");
        m.SetTextureScale("_BaseMap", new Vector2(st.x, st.y)); m.SetTextureOffset("_BaseMap", new Vector2(st.z, st.w));
        m.SetColor("_BaseColor", (Color)src.GetColor("baseColorFactor") * FacadeTints[variant % FacadeTints.Length]);
        m.SetFloat("_Smoothness", 0.12f);
        // depth and glass, worked out from the painted facade: windows and doors sit back in the wall,
        // frames and cornices stand proud, glass reflects the sky
        var (nrm, mask) = maps ? FacadeMaps(tex) : (null, null);   // the scanned lighthouse keeps its own surface
        if (nrm) { m.SetTexture("_BumpMap", nrm); m.SetFloat("_BumpScale", 1.15f); m.EnableKeyword("_NORMALMAP"); }
        if (mask) { m.SetTexture("_MetallicGlossMap", mask); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessTextureChannel", 0); }
        m.SetTexture("_DetailAlbedoMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/plastered_wall_02_diff_1k.jpg"));
        m.SetTexture("_DetailNormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{PH}/plastered_wall_02_nor_gl_1k.jpg"));
        m.SetFloat("_DetailAlbedoMapScale", 0.3f);   // the x2 detail brightens; keep it subtle m.SetFloat("_DetailNormalMapScale", 0.9f);
        m.SetTextureScale("_DetailAlbedoMap", new Vector2(1.5f, 1.5f));
        m.EnableKeyword("_DETAIL_MULX2");
        EditorUtility.SetDirty(m);
        cache[key] = m;
        return m;
    }

    static readonly Dictionary<string, (Texture2D, Texture2D)> facadeMaps = new();
    static (Texture2D normal, Texture2D mask) FacadeMaps(Texture src)
    {
        if (facadeMaps.TryGetValue(src.name, out var done)) return done;
        // read the imported texture through the GPU (sub-assets of the .glb are not readable)
        int W = Mathf.Min(src.width, 1024), H = Mathf.Max(1, Mathf.RoundToInt(src.height * (W / (float)src.width)));
        var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var read = new Texture2D(W, H, TextureFormat.RGBA32, false); read.ReadPixels(new Rect(0, 0, W, H), 0, 0); read.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        var px = read.GetPixels32(); Object.DestroyImmediate(read);
        var hgt = new float[W * H]; var gloss = new float[W * H];
        for (int i = 0; i < px.Length; i++)
        {
            Color c = px[i]; float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            Color.RGBToHSV(c, out _, out var sat, out _);
            bool opening = lum < 0.3f;                                   // glass, doorways, shell holes: set back
            hgt[i] = opening ? 0f : 0.55f + 0.45f * lum;                 // light trim stands a little proud of the wall
            gloss[i] = lum > 0.07f && lum < 0.3f && sat < 0.45f ? 1f : 0f;   // dark neutral = glass (not black holes, not shutters)
        }
        float At(float[] a, int x, int y) => a[((y % H + H) % H) * W + ((x % W + W) % W)];
        // soften the height a little so edges read as bevels, not razor lines
        var blur = new float[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            blur[y * W + x] = (At(hgt, x, y) * 4 + At(hgt, x - 1, y) + At(hgt, x + 1, y) + At(hgt, x, y - 1) + At(hgt, x, y + 1)) / 8f;
        var n = new Texture2D(W, H, TextureFormat.RGBA32, false, true); var mk = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
        var np = new Color[W * H]; var mp = new Color[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            float dx = At(blur, x + 1, y) - At(blur, x - 1, y), dy = At(blur, x, y + 1) - At(blur, x, y - 1);
            var v = new Vector3(-dx * 2.6f, -dy * 2.6f, 1).normalized;
            np[y * W + x] = new Color(v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, 1);
            float g = (At(gloss, x, y) * 2 + At(gloss, x - 1, y) + At(gloss, x + 1, y) + At(gloss, x, y - 1) + At(gloss, x, y + 1)) / 6f;
            mp[y * W + x] = new Color(g * 0.55f, 0, 0, Mathf.Lerp(0.1f, 0.92f, g));     // R metallic, A smoothness
        }
        n.SetPixels(np); n.Apply(); mk.SetPixels(mp); mk.Apply();
        Texture2D Save(Texture2D t, string suffix, bool normal)
        {
            var path = $"{Gen}/Facade_{src.name}_{suffix}.png";
            File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = false; ti.alphaIsTransparency = false; ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true; ti.anisoLevel = 4;
            ti.isReadable = !normal;         // the game reads the glass mask to know a window from a wall (CarDamage.BuildingGlassAt)
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        var res = (Save(n, "nrm", true), Save(mk, "mask", false));
        facadeMaps[src.name] = res;
        return res;
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
                else if ((tex && tex.width == 64 && tex.height == 64 && size.y > 0.5f && size.y < 3f && Mathf.Max(size.x, size.z) > 100) || (!tex && size.x > 100 && size.z > 200 && Near(c, 0.89f, 0.87f, 0.82f))) { dst = Lit("SeaWall", "plastered_wall_02", new Color(1.04f, 1.03f, 1.0f), 0.25f, 0.5f); PlanarUV(r, 0.25f); }
                else if (tex && tex.width == 256 && tex.height == 256 && size.y < 0.5f && size.x > 50) { dst = Lit("Promenade", "large_grey_tiles", new Color(1.18f, 1.14f, 1.06f), 0.3f, 1f); PlanarUV(r, 0.55f); }
                else if (tex && size.y > 2f && (tex.width == 128 || tex.width == 512 || tex.width == 1024 || tex.width == 2048 || (tex.width == 64 && tex.height == 64)) && !path.Contains("palm")) dst = Facade(src, Mathf.Abs((r.name.GetHashCode() ^ (int)(r.bounds.center.x * 7) ^ (int)(r.bounds.center.z * 13)) % FacadeTints.Length), !path.Contains("model"));   // walls: houses, mosques, ruins
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
        // what is still on the imported glTF look (to catch white / untreated buildings)
        foreach (var gr in city.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r => r.sharedMaterials.Where(m => m && m.HasProperty("baseColorFactor")).Select(m => (r, m)))
            .Where(x => x.m.GetTexture("baseColorTexture")).GroupBy(x => x.m.GetTexture("baseColorTexture").name).OrderByDescending(g => g.Sum(x => x.r.bounds.size.magnitude)).Take(12))
        {
            var t = gr.First().m.GetTexture("baseColorTexture");
            Debug.Log($"[look] untreated {gr.Key} {t.width}x{t.height} x{gr.Count()} eg {NamePath(gr.First().r.transform)} size {gr.First().r.bounds.size:F0}");
        }
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
        sun.intensity = 1.55f; sun.color = new Color(1f, 0.9f, 0.74f);
        sun.transform.rotation = Quaternion.Euler(27f, -40f, 0f);     // late afternoon: long shadows across the streets
        sun.shadowStrength = 0.92f;
        RenderSettings.ambientIntensity = 1.0f;
        RenderSettings.reflectionIntensity = 1f;
        RenderSettings.fogDensity = 0.0034f;                 // dusty coastal haze gives depth
        RenderSettings.fogColor = new Color(0.83f, 0.80f, 0.74f);
        var sky = RenderSettings.skybox;
        sky.SetFloat("_SunSize", 0.035f); sky.SetFloat("_AtmosphereThickness", 1.02f);
        sky.SetColor("_SkyTint", new Color(0.62f, 0.64f, 0.68f)); sky.SetFloat("_Exposure", 1.05f);

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
        if (prof.TryGet<ColorAdjustments>(out var ca)) { ca.postExposure.Override(-0.05f); ca.contrast.Override(20f); ca.saturation.Override(-12f); }
        if (prof.TryGet<Bloom>(out var bl)) { bl.intensity.Override(0.65f); bl.threshold.Override(0.9f); }
        // film look: fine grain, a touch of lens fringing, light motion blur; depth of field is switched on by
        // the weapon while aiming down the sights
        if (!prof.TryGet<FilmGrain>(out var fg)) fg = prof.Add<FilmGrain>(true);
        fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.22f); fg.response.Override(0.75f);
        if (!prof.TryGet<ChromaticAberration>(out var chr)) chr = prof.Add<ChromaticAberration>(true);
        chr.intensity.Override(0.07f);
        if (!prof.TryGet<MotionBlur>(out var mb)) mb = prof.Add<MotionBlur>(true);
        mb.intensity.Override(0.16f); mb.quality.Override(MotionBlurQuality.Low);
        if (!prof.TryGet<DepthOfField>(out var dof)) dof = prof.Add<DepthOfField>(true);
        dof.mode.Override(DepthOfFieldMode.Off); dof.focalLength.Override(55f); dof.aperture.Override(4f); dof.bladeCount.Override(6);
        if (prof.TryGet<Vignette>(out var vg)) { vg.intensity.Override(0.24f); vg.smoothness.Override(0.45f); }
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
