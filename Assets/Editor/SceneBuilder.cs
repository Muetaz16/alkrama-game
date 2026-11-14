// Builds the Corniche scene from the city exported by the web prototype.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SceneBuilder.Build
// 1. URP with PC ("High") and phone ("Mobile") quality, 2. the city .glb with colliders,
// 3. sun, sky, sea, fog, post-processing, 4. a test camera shot saved to ../unity_shots/
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SceneBuilder
{
    const string Root = "Assets/Benghazi";
    const string CityGlb = Root + "/Models/benghazi_corniche_city.glb";
    const string ScenePath = "Assets/Scenes/Corniche.unity";

    public static void Build()
    {
        var urp = SetupUrp();
        // re-import the models now URP is active, so their materials use URP shaders
        AssetDatabase.ImportAsset(CityGlb, ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(Root + "/Models/lighthouse.glb", ImportAssetOptions.ForceUpdate);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // sun: warm late-afternoon light over the sea, soft shadows
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.93f, 0.8f);
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
        RenderSettings.sun = sun;

        var sky = new Material(Shader.Find("Skybox/Procedural"));
        sky.SetFloat("_AtmosphereThickness", 1.1f);
        sky.SetColor("_SkyTint", new Color(0.62f, 0.7f, 0.82f));
        sky.SetFloat("_Exposure", 1.25f);
        AssetDatabase.CreateAsset(sky, Root + "/Sky.mat");
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 0.75f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0028f;
        RenderSettings.fogColor = new Color(0.8f, 0.78f, 0.72f);

        // the city: every building, the Corniche, lighthouse square, roundabout, palms
        var cityAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CityGlb);
        var city = (GameObject)PrefabUtility.InstantiatePrefab(cityAsset);
        city.name = "Benghazi Corniche";
        city.isStatic = true;
        int colliders = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.isStatic = true;
            if (mf.sharedMesh && mf.sharedMesh.vertexCount > 3) { mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh; colliders++; }
        }
        var bounds = new Bounds(city.transform.position, Vector3.zero);
        foreach (var r in city.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

        // sea: built from the map's water strips (so it only covers real water), plus open sea beyond
        var map = JsonUtility.FromJson<MapData>(File.ReadAllText(Root + "/Data/benghazi_corniche.json"));
        var seaMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        seaMat.SetColor("_BaseColor", new Color(0.05f, 0.3f, 0.4f));
        seaMat.SetFloat("_Smoothness", 0.94f);
        seaMat.SetFloat("_Metallic", 0.1f);
        AssetDatabase.CreateAsset(seaMat, Root + "/Sea.mat");
        var seaMesh = SeaMesh(map, 0.06f);
        AssetDatabase.CreateAsset(seaMesh, Root + "/SeaMesh.asset");
        var sea = new GameObject("Sea");
        sea.AddComponent<MeshFilter>().sharedMesh = seaMesh;
        sea.AddComponent<MeshRenderer>().sharedMaterial = seaMat;

        // post-processing: filmic colours, a little bloom, warm Benghazi grade
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var tone = profile.Add<Tonemapping>(true); tone.mode.Override(TonemappingMode.ACES);
        var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.35f); bloom.threshold.Override(1.1f);
        var grade = profile.Add<ColorAdjustments>(true); grade.postExposure.Override(-0.35f); grade.contrast.Override(12f); grade.saturation.Override(6f);
        var wb = profile.Add<WhiteBalance>(true); wb.temperature.Override(8f);
        var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.18f);
        AssetDatabase.CreateAsset(profile, Root + "/PostFX.asset");
        var volume = new GameObject("Post FX").AddComponent<Volume>();
        volume.isGlobal = true; volume.sharedProfile = profile;

        // camera looking at the lighthouse from the seafront
        var lighthouse = FindLighthouse();
        var shaders = city.GetComponentsInChildren<Renderer>().SelectMany(x => x.sharedMaterials).Where(m => m)
            .GroupBy(m => m.shader ? m.shader.name : "null").Select(gr => $"{gr.Key} x{gr.Count()}");
        Debug.Log("[build] shaders: " + string.Join(", ", shaders));
        var cam = new GameObject("Main Camera").AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.farClipPlane = 2000f;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        cam.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        var seaDir = SeaDir(map);
        PlaceShotCamera(cam, lighthouse, seaDir);

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log($"[build] city bounds {bounds}, colliders {colliders}, lighthouse at {lighthouse}, urp {urp.name}");

        Shoot(cam, "corniche_lighthouse.png");
        cam.transform.position = lighthouse + new Vector3(0, 120, 0);
        cam.transform.rotation = Quaternion.Euler(90, 0, 0);
        Shoot(cam, "corniche_top.png");
        PlaceShotCamera(cam, lighthouse, seaDir);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
    }

    // URP asset + renderer, used by both quality levels; phone level is lighter
    static UniversalRenderPipelineAsset SetupUrp()
    {
        const string dir = "Assets/Settings";
        Directory.CreateDirectory(dir);
        var data = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(data, dir + "/URP_Renderer.asset");
        var pc = UniversalRenderPipelineAsset.Create(data);
        pc.shadowDistance = 95f; pc.shadowCascadeCount = 3; pc.msaaSampleCount = 2;   // shadows past ~100 m are lost in the haze anyway
        pc.supportsHDR = true;
        AssetDatabase.CreateAsset(pc, dir + "/URP_PC.asset");
        var phone = UniversalRenderPipelineAsset.Create(data);
        phone.shadowDistance = 60f; phone.shadowCascadeCount = 1; phone.msaaSampleCount = 1;
        phone.renderScale = 0.85f; phone.supportsHDR = false;
        AssetDatabase.CreateAsset(phone, dir + "/URP_Mobile.asset");
        GraphicsSettings.defaultRenderPipeline = pc;
        var names = QualitySettings.names;
        for (int i = 0; i < names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = i < names.Length / 2 ? phone : pc;
        }
        QualitySettings.SetQualityLevel(names.Length - 1, false);
        AssetDatabase.SaveAssets();
        return pc;
    }

    // lighthouse spot from the exported game data; the web game uses x east / z south and
    // glTFast mirrors x on import, so a web point (x, z) is (-x, z) here
    public static Vector3 FindLighthouse()
    {
        var json = File.ReadAllText(Root + "/Data/benghazi_corniche_spawns.json");
        var data = JsonUtility.FromJson<Spawns>(json);
        foreach (var l in data.landmarks) if (l.type == "lighthouse") return new Vector3(-l.x, 0, l.z);
        return Vector3.zero;
    }
    [System.Serializable] class Landmark { public string type; public string name; public float x; public float z; }
    [System.Serializable] class Spawns { public Landmark[] landmarks; }

    static void PlaceShotCamera(Camera cam, Vector3 lighthouse, Vector3 seaDir)
    {
        // from the Corniche, between the lighthouse and the sea, a little to the side
        var side = Vector3.Cross(Vector3.up, seaDir);
        cam.transform.position = lighthouse + seaDir * 55f + side * 22f + Vector3.up * 3f;
        cam.transform.LookAt(lighthouse + Vector3.up * 12f);
    }

    static Vector3 SeaDir(MapData map)
    {
        foreach (var l in map.landmarks)
            if (l.type == "lighthouse" && l.seaDir != null && l.seaDir.Length == 2) return new Vector3(-l.seaDir[0], 0, l.seaDir[1]).normalized;
        return Vector3.forward;
    }

    // map "sea" = rows of [x0, x1, z0, z1] water strips (web coordinates; web x is Unity -x);
    // one quad each, plus big quads of open sea on the map sides that touch water
    static Mesh SeaMesh(MapData map, float y)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        void Quad(float x0, float x1, float z0, float z1)
        {
            int i = v.Count;
            v.Add(new Vector3(-x0, y, z0)); v.Add(new Vector3(-x1, y, z0)); v.Add(new Vector3(-x1, y, z1)); v.Add(new Vector3(-x0, y, z1));
            // pick the winding that faces up
            var up = Vector3.Cross(v[i + 1] - v[i], v[i + 2] - v[i]).y > 0;
            t.AddRange(up ? new[] { i, i + 1, i + 2, i, i + 2, i + 3 } : new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
        }
        var strips = ParseSea();
        foreach (var s in strips) Quad(s[0], s[1], s[2], s[3]);
        float h = map.half, far = 4000f;
        bool west = false, east = false, north = false, south = false;   // web map edges
        foreach (var s in strips)
        {
            if (s[0] <= -h + 1) west = true;
            if (s[1] >= h - 1) east = true;
            if (s[2] <= -h + 1) north = true;
            if (s[3] >= h - 1) south = true;
        }
        if (west) Quad(-far, -h, -far, far);
        if (east) Quad(h, far, -far, far);
        if (north) Quad(-h, h, -far, -h);
        if (south) Quad(-h, h, h, far);
        var m = new Mesh { indexFormat = IndexFormat.UInt32 };
        m.SetVertices(v); m.SetTriangles(t, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        Debug.Log($"[build] sea strips {strips.Count}, open sea W{west} E{east} N{north} S{south}");
        return m;
    }

    // JsonUtility can't read nested arrays, so pull "sea": [[x0,x1,z0,z1], ...] out by hand
    public static List<float[]> ParseSea()
    {
        var json = File.ReadAllText(Root + "/Data/benghazi_corniche.json");
        int a = json.IndexOf("\"sea\":"); a = json.IndexOf('[', a);
        int depth = 0, b = a;
        for (; b < json.Length; b++) { if (json[b] == '[') depth++; else if (json[b] == ']' && --depth == 0) break; }
        var list = new List<float[]>();
        foreach (var part in json.Substring(a + 1, b - a - 1).Split(new[] { "]," }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var nums = part.Trim().Trim('[', ']').Split(',');
            if (nums.Length == 4) list.Add(System.Array.ConvertAll(nums, n => float.Parse(n.Trim(), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return list;
    }

    [System.Serializable] class MapLandmark { public string type; public float[] seaDir; }
    [System.Serializable] class MapData { public float half; public float seaCell; public MapLandmark[] landmarks; }

    public static void Shoot(Camera cam, string file)
    {
        const int w = 1600, h = 900;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        cam.targetTexture = null; RenderTexture.active = null;
        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity_shots"));
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
        Object.DestroyImmediate(rt);
        Debug.Log($"[build] shot saved {file}");
    }
}
