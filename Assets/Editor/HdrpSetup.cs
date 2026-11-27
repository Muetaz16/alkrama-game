// High Definition Render Pipeline for the Corniche: the pipeline asset, the run-time material templates, and
// the pass that turns the generators' materials into HDRP ones.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod HdrpSetup.Run       pipeline + templates + convert what is there
//
// HOW THE GENERATORS FIT IN. SceneBuilder, LookUpgrade, Seafront, CityDetail*, LookPro, RealCars ... still write
// "Universal Render Pipeline/Lit" materials with URP property names, exactly as on the URP branch (URP stays
// installed, as a package only - nothing renders with it). Every generator entry point brackets its work:
//     HdrpSetup.BeginUrp();     // materials converted by an earlier run go back to URP/Lit (their URP values are still saved in them)
//     ... generator code, unchanged ...
//     HdrpSetup.ConvertAll();   // every URP/Lit material in the project, the open scene and the prefabs becomes HDRP/Lit
// So generator work from the URP branch merges without touching a line of it, and the differences between the
// two pipelines live in Convert() below: mask-map packing (R metal, G occlusion, B detail, A smoothness), the
// packed detail map, alpha clip, two-sided, transparency.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

public static class HdrpSetup
{
    public const string AssetPath = "Assets/Settings/HDRP_PC.asset";
    const string Dir = "Assets/Benghazi/HDRP", Packed = Dir + "/Packed", Templates_ = "Assets/Resources/Hdrp";
    const string UrpLit = "Universal Render Pipeline/Lit", HdLit = "HDRP/Lit";

    public static void Run()
    {
        Pipeline(); Templates();
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        int n = ConvertAll();
        EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log($"[hdrp] setup done, {n} materials converted");
    }

    // ---------------------------------------------------------------- where this branch writes
    // Beside the repository, in folders of its own, so the URP build (builds/win) and its reference shots
    // (unity_shots) are never touched - also when the project is a git worktree under <repo>/.claude/worktrees.
    static string Beside()
    {
        string root = Path.GetFullPath(".").Replace('\\', '/'); int i = root.IndexOf("/.claude/worktrees/");
        return Path.GetDirectoryName(i > 0 ? root.Substring(0, i) : root);
    }
    public static string BuildDir() => Path.Combine(Beside(), "builds", "win-hdrp");
    public static string ShotsDir() => Path.Combine(Beside(), "unity_shots_hdrp");

    /// <summary>Two-sided, whichever pipeline's shader the material is on (the glTF import under HDRP has no _Cull).</summary>
    public static bool TwoSided(Material m) =>
        (m.HasProperty("_Cull") && m.GetFloat("_Cull") == 0) || (m.HasProperty("_DoubleSidedEnable") && m.GetFloat("_DoubleSidedEnable") > 0.5f) || (m.HasProperty("_CullMode") && m.GetFloat("_CullMode") == 0);

    // models import their materials for the pipeline that is active at that moment
    public static void ReimportModels()
    {
        int n = 0;
        foreach (var ext in new[] { "*.glb", "*.gltf", "*.fbx" })
            foreach (var f in Directory.GetFiles("Assets", ext, SearchOption.AllDirectories)) { AssetDatabase.ImportAsset(f.Replace('\\', '/'), ImportAssetOptions.ForceUpdate); n++; }
        Debug.Log($"[hdrp] {n} models re-imported with HDRP materials");
    }

    // ---------------------------------------------------------------- pipeline asset
    public static HDRenderPipelineAsset Pipeline()
    {
        Directory.CreateDirectory("Assets/Settings"); Directory.CreateDirectory(Dir);
        var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(AssetPath);
        if (!asset) { asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>(); AssetDatabase.CreateAsset(asset, AssetPath); }
        var s = asset.currentPlatformRenderPipelineSettings;
        // deferred only: hundreds of opaque buildings and one sun - cheapest lighting, half the shader variants.
        // (No MSAA in deferred; anti-aliasing is temporal, see CinematicLook.MainCamera.)
        s.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.DeferredOnly;
        s.colorBufferFormat = RenderPipelineSettings.ColorBufferFormat.R11G11B10;
        s.supportSSR = true; s.supportSSRTransparent = false;
        s.supportSSAO = true; s.supportSSGI = true;
        s.supportVolumetrics = true; s.supportVolumetricClouds = false;
        s.supportWater = true; s.supportWaterDecals = false; s.supportWaterExclusion = false;
        s.waterSimulationResolution = WaterSimulationResolution.Medium128;
        s.supportDecals = true; s.supportDecalLayers = false;
        s.supportMotionVectors = true; s.supportScreenSpaceLensFlare = true; s.supportDataDrivenLensFlare = false;
        s.supportDistortion = false; s.supportSubsurfaceScattering = false;
        s.supportTransparentBackface = false; s.supportTransparentDepthPrepass = true; s.supportTransparentDepthPostpass = false;
        s.supportRayTracing = false;                       // off: not affordable at 60 fps on a laptop RTX 4050
        s.supportCustomPass = false; s.supportRuntimeAOVAPI = false; s.supportTerrainHole = false;
        s.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.LegacyLightProbes;
        // sun shadows: the light picks Low..Ultra at run time (GraphicsQuality)
        var sh = s.hdShadowInitParams;
        sh.shadowResolutionDirectional = new IntScalableSetting(new[] { 1024, 2048, 2048, 4096 }, ScalableSettingSchemaId.With4Levels);
        sh.maxDirectionalShadowMapResolution = 4096;
        sh.directionalShadowFilteringQuality = HDShadowFilteringQuality.Medium;
        sh.supportScreenSpaceShadows = false;
        s.hdShadowInitParams = sh;
        // the picture can be drawn smaller and scaled up with the temporal upscaler (GraphicsQuality sets the percentage)
        var dr = s.dynamicResolutionSettings;
        dr.enabled = true; dr.dynResType = DynamicResolutionType.Software;     // hardware scaling on D3D12 is a known source of device hangs dr.upsampleFilter = DynamicResUpscaleFilter.TAAU;
        dr.minPercentage = 50; dr.maxPercentage = 100; dr.forceResolution = false; dr.useMipBias = true;
        s.dynamicResolutionSettings = dr;
        asset.currentPlatformRenderPipelineSettings = s;
        EditorUtility.SetDirty(asset);

        bool switched = GraphicsSettings.defaultRenderPipeline != asset;
        GraphicsSettings.defaultRenderPipeline = asset;
        int keep = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = asset; }
        QualitySettings.SetQualityLevel(keep, false);

        // what a camera draws by default: switch on what the scene uses and HDRP leaves off
        var fsType = typeof(HDRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.HighDefinition.RenderingPathFrameSettings");     // internal class
        var getter = typeof(GraphicsSettings).GetMethods().FirstOrDefault(x => x.Name == "GetRenderPipelineSettings" && x.IsGenericMethod && x.GetParameters().Length == 0);
        object fsHolder = fsType != null && getter != null ? getter.MakeGenericMethod(fsType).Invoke(null, null) : null;
        var field = fsType?.GetField("m_Camera", BindingFlags.Instance | BindingFlags.NonPublic);
        if (fsHolder != null && field != null)
        {
            var fs = (FrameSettings)field.GetValue(fsHolder);
            foreach (var f in new[] { FrameSettingsField.SSGI, FrameSettingsField.Water, FrameSettingsField.SSR, FrameSettingsField.SSAO, FrameSettingsField.ContactShadows, FrameSettingsField.Volumetrics,
                         FrameSettingsField.ReprojectionForVolumetrics, FrameSettingsField.Decals, FrameSettingsField.MotionVectors, FrameSettingsField.ObjectMotionVectors })
                fs.SetEnabled(f, true);
            fs.litShaderMode = LitShaderMode.Deferred;
            field.SetValue(fsHolder, fs);
            var gs = AssetDatabase.LoadAssetAtPath<Object>("Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset");
            if (gs) EditorUtility.SetDirty(gs);
            Debug.Log("[hdrp] default frame settings: SSGI, water, SSR, SSAO, contact shadows, volumetrics, decals, motion vectors on; deferred");
        }
        else Debug.LogWarning("[hdrp] could not reach the default frame settings (SSGI and water stay as HDRP's defaults)");
        AssetDatabase.SaveAssets();
        Debug.Log($"[hdrp] pipeline asset {AssetPath} active on all {QualitySettings.names.Length} quality levels");
        if (switched) ReimportModels();
        return asset;
    }

    // ---------------------------------------------------------------- templates for run-time materials (Rp.cs)
    public static void Templates()
    {
        Directory.CreateDirectory(Dir); Directory.CreateDirectory(Templates_);
        var white = Tex(Dir + "/white.png", new Color(1, 1, 1, 1), false);
        var flat = Tex(Dir + "/flat_normal.png", new Color(0.5f, 0.5f, 1f, 1), true);
        var mask = Tex(Dir + "/mask.png", new Color(0, 1, 1, 0.5f), false, linear: true);

        Material Make(string name, string shader, System.Action<Material> set)
        {
            string path = $"{Templates_}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find(shader); set(m); if (shader.StartsWith("HDRP/")) HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m);
            return m;
        }
        Make("Lit", HdLit, m => { m.SetTexture("_BaseColorMap", white); });
        Make("LitPbr", HdLit, m => { m.SetTexture("_BaseColorMap", white); m.SetTexture("_NormalMap", flat); m.SetTexture("_MaskMap", mask); });
        Make("LitCutout", HdLit, m => { m.SetTexture("_BaseColorMap", white); m.SetFloat("_AlphaCutoffEnable", 1); m.SetFloat("_AlphaCutoff", 0.5f); m.SetFloat("_DoubleSidedEnable", 1); m.SetFloat("_DoubleSidedNormalMode", 2); });
        Make("Fx", "Benghazi/Fx", m => { });
        Make("Decal", "HDRP/Decal", m =>
        {
            m.SetTexture("_BaseColorMap", white); m.SetFloat("_AffectAlbedo", 1); m.SetFloat("_AffectNormal", 0); m.SetFloat("_AffectMetal", 0);
            m.SetFloat("_AffectSmoothness", 1); m.SetFloat("_AffectAO", 0); m.SetFloat("_AffectEmission", 0); m.SetFloat("_Smoothness", 0.1f); m.SetFloat("_DecalBlend", 1f);
            m.enableInstancing = true;
        });
        AssetDatabase.SaveAssets();
        Debug.Log("[hdrp] run-time material templates in " + Templates_);
    }

    static Texture2D Tex(string path, Color c, bool normal, bool linear = false)
    {
        if (!File.Exists(path))
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true); t.SetPixels(Enumerable.Repeat(c, 16).ToArray()); t.Apply();
            File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default; ti.sRGBTexture = !normal && !linear;
            ti.alphaIsTransparency = false; ti.mipmapEnabled = false; ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------------------------------------------------------------- URP <-> HDRP materials
    static IEnumerable<string> MatFiles() => Directory.GetFiles("Assets", "*.mat", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).Where(p => !p.StartsWith(Templates_));

    /// <summary>Before a generator runs: materials an earlier run converted go back to URP/Lit, so the generator
    /// reads and writes the properties it knows. (A material keeps the values of properties its shader does not
    /// have, so nothing is lost either way.)</summary>
    public static int BeginUrp()
    {
        var urp = Shader.Find(UrpLit); if (!urp) { Debug.LogError("[hdrp] URP/Lit not found: the URP package must stay installed for the generators"); return 0; }
        int n = 0;
        foreach (var path in MatFiles())
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m || !m.shader || m.shader.name != HdLit) continue;
            m.shader = urp; m.renderQueue = -1; n++;
        }
        if (n > 0) Debug.Log($"[hdrp] {n} materials handed back to the generators as URP/Lit");
        return n;
    }

    /// <summary>After a generator: every URP/Lit material (assets, the open scene, prefabs) becomes HDRP/Lit.</summary>
    public static int ConvertAll()
    {
        var hd = Shader.Find(HdLit); if (!hd) { Debug.LogError("[hdrp] HDRP/Lit not found"); return 0; }
        Directory.CreateDirectory(Packed);
        var seen = new HashSet<Material>(); int n = 0;
        void Do(Material m) { if (m && seen.Add(m) && Convert(m, hd)) n++; }
        foreach (var path in MatFiles()) Do(AssetDatabase.LoadAssetAtPath<Material>(path));
        for (int i = 0; i < SceneManager.sceneCount; i++)
            foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) Do(m);
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)); if (!go) continue;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) Do(m);
        }
        AssetDatabase.SaveAssets();
        // what is still not HDRP in the scene (would draw pink or not at all)
        var odd = new Dictionary<string, int>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
            foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials)
                    if (m && m.shader) { string s = m.shader.name; odd[s] = odd.TryGetValue(s, out var c) ? c + 1 : 1; }
        Debug.Log($"[hdrp] {n} materials converted to HDRP/Lit; shaders in the scene: " + string.Join(", ", odd.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
        return n;
    }

    static bool Convert(Material m, Shader hd)
    {
        if (!m.shader || m.shader.name != UrpLit) return false;
        if (AssetDatabase.GetAssetPath(m).EndsWith(".glb") || AssetDatabase.GetAssetPath(m).EndsWith(".fbx")) return false;   // importer-owned
        // ---- read the URP side
        Texture baseMap = m.GetTexture("_BaseMap"); Vector2 bs = m.GetTextureScale("_BaseMap"), bo = m.GetTextureOffset("_BaseMap");
        Color baseColor = m.GetColor("_BaseColor");
        Texture bump = m.GetTexture("_BumpMap"); float bumpScale = m.GetFloat("_BumpScale");
        Texture mgm = m.IsKeywordEnabled("_METALLICSPECGLOSSMAP") ? m.GetTexture("_MetallicGlossMap") : null;
        float smooth = m.GetFloat("_Smoothness"), metal = m.GetFloat("_Metallic");
        bool clip = m.GetFloat("_AlphaClip") > 0.5f; float cutoff = m.GetFloat("_Cutoff");
        bool twoSided = m.GetFloat("_Cull") < 0.5f, transparent = m.GetFloat("_Surface") > 0.5f;
        Texture detA = m.GetTexture("_DetailAlbedoMap"), detN = m.GetTexture("_DetailNormalMap");
        Vector2 ds = m.GetTextureScale("_DetailAlbedoMap"), dofs = m.GetTextureOffset("_DetailAlbedoMap");
        float detAScale = m.GetFloat("_DetailAlbedoMapScale"), detNScale = m.GetFloat("_DetailNormalMapScale");
        if (!m.IsKeywordEnabled("_NORMALMAP")) bump = null;
        if (!m.IsKeywordEnabled("_DETAIL_MULX2") && !m.IsKeywordEnabled("_DETAIL_SCALED")) { detA = null; detN = null; }

        // ---- write the HDRP side
        m.shader = hd; m.renderQueue = -1;
        m.SetTexture("_BaseColorMap", baseMap); m.SetTextureScale("_BaseColorMap", bs); m.SetTextureOffset("_BaseColorMap", bo);
        m.SetColor("_BaseColor", baseColor);
        m.SetTexture("_NormalMap", bump); m.SetFloat("_NormalScale", bump ? bumpScale : 1f); m.SetFloat("_NormalMapSpace", 0);
        if (mgm)
        {
            // URP: metal = R, smoothness = A x _Smoothness. HDRP mask: G is occlusion and B the detail mask, so the
            // texture is repacked with both at 1 (the URP maps leave them black, which would switch both off).
            m.SetTexture("_MaskMap", Mask(mgm));
            m.SetFloat("_MetallicRemapMin", 0); m.SetFloat("_MetallicRemapMax", 1);
            m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", Mathf.Clamp01(smooth));
            m.SetFloat("_AORemapMin", 0); m.SetFloat("_AORemapMax", 1);
        }
        else { m.SetTexture("_MaskMap", null); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth); }
        if (detA || detN)
        {
            m.SetTexture("_DetailMap", Detail(detA, detN)); m.SetTextureScale("_DetailMap", ds); m.SetTextureOffset("_DetailMap", dofs);
            m.SetFloat("_DetailAlbedoScale", Mathf.Clamp(detAScale, 0f, 2f)); m.SetFloat("_DetailNormalScale", detN ? Mathf.Clamp(detNScale, 0f, 2f) : 0f);
            m.SetFloat("_DetailSmoothnessScale", 0f); m.SetFloat("_LinkDetailsWithBase", 0f);
        }
        else m.SetTexture("_DetailMap", null);
        m.SetFloat("_AlphaCutoffEnable", clip ? 1 : 0); m.SetFloat("_AlphaCutoff", cutoff);
        m.SetFloat("_DoubleSidedEnable", twoSided ? 1 : 0); m.SetFloat("_DoubleSidedNormalMode", 2);
        // ^ None: a back face is lit with the normal the mesh has, as URP does. The web export's floors are wound
        //   facing down with their normals up - flipped or mirrored they would get no sun at all.
        m.SetFloat("_SurfaceType", transparent ? 1 : 0); m.SetFloat("_BlendMode", 0);
        if (transparent) { m.SetFloat("_TransparentZWrite", 0); m.SetFloat("_EnableFogOnTransparent", 1); }
        m.SetFloat("_SupportDecals", 1); m.SetFloat("_ReceivesSSR", 1);
        m.SetFloat("_EnergyConservingSpecularColor", 1);
        HDMaterial.ValidateMaterial(m);
        EditorUtility.SetDirty(m);
        return true;
    }

    // ---- texture repacking (through the GPU: glb sub-assets and compressed textures are not readable)
    static Color[] Read(Texture src, int w, int h, bool linear)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false, linear); t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        var px = t.GetPixels(); Object.DestroyImmediate(t); return px;
    }

    static string Key(Texture t) { var p = AssetDatabase.GetAssetPath(t); var g = AssetDatabase.AssetPathToGUID(p); return (string.IsNullOrEmpty(g) ? "x" : g.Substring(0, 8)) + "_" + t.name; }
    static bool Fresh(string outPath, params Texture[] srcs)
    {
        if (!File.Exists(outPath)) return false;
        var when = File.GetLastWriteTimeUtc(outPath);
        foreach (var s in srcs) { if (!s) continue; var p = AssetDatabase.GetAssetPath(s); if (File.Exists(p) && File.GetLastWriteTimeUtc(p) > when) return false; }
        return true;
    }

    static readonly Dictionary<string, Texture2D> packed = new();
    static Texture2D Mask(Texture src)
    {
        string path = $"{Packed}/{Key(src)}_mask.png";
        if (packed.TryGetValue(path, out var done) && done) return done;
        if (!Fresh(path, src))
        {
            int w = Mathf.Min(src.width, 2048), h = Mathf.Min(src.height, 2048);
            var px = Read(src, w, h, true);
            for (int i = 0; i < px.Length; i++) px[i] = new Color(px[i].r, 1f, 1f, px[i].a);
            Save(path, px, w, h, readable: true);            // the game reads the facade glass mask (red) - CarDamage.BuildingGlassAt
        }
        return packed[path] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // HDRP detail map: R = grey albedo change (0.5 none), G = normal y, B = smoothness change, A = normal x
    static Texture2D Detail(Texture albedo, Texture normal)
    {
        string path = $"{Packed}/{(albedo ? Key(albedo) : "none")}__{(normal ? Key(normal) : "none")}_detail.png";
        if (packed.TryGetValue(path, out var done) && done) return done;
        if (!Fresh(path, albedo, normal))
        {
            var a = albedo ? albedo : normal; int w = Mathf.Min(a.width, 1024), h = Mathf.Min(a.height, 1024);
            var pa = albedo ? Read(albedo, w, h, false) : null;
            // a normal-map texture comes through the blit as it is stored: x in A (or R) and y in G
            var pn = normal ? Read(normal, w, h, true) : null;
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                float g = 0.5f;
                if (pa != null) { var c = pa[i].linear; g = Mathf.Clamp01(c.r * 0.3f + c.g * 0.59f + c.b * 0.11f); g = Mathf.LinearToGammaSpace(g); }   // URP x2 detail: sRGB mid grey = no change
                float nx = 0.5f, ny = 0.5f;
                if (pn != null) { ny = pn[i].g; nx = pn[i].a < 0.999f ? pn[i].a : pn[i].r; }
                px[i] = new Color(g, ny, 0.5f, nx);
            }
            Save(path, px, w, h, readable: false);
        }
        return packed[path] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void Save(string path, Color[] px, int w, int h, bool readable)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true); t.SetPixels(px); t.Apply();
        File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = TextureImporterType.Default; ti.sRGBTexture = false; ti.alphaIsTransparency = false; ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true; ti.anisoLevel = 4; ti.isReadable = readable;
        ti.SaveAndReimport();
    }
}

// Models imported "via material description" (the Mixamo soldier) get whatever the file says, and the soldier's
// second body material comes in with a white emissive colour: under HDRP that is a glowing white sleeve.
// Nothing in this game's models is meant to glow, so imported model materials never emit.
class HdrpModelMaterials : AssetPostprocessor
{
    public override uint GetVersion() => 3;
    public override int GetPostprocessOrder() => 1000;          // after HDRP's own material-description importer
    // the whole model is done here: every material the import made is final
    void OnPostprocessModel(GameObject root)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
            {
                if (!m || !m.shader || m.shader.name != "HDRP/Lit") continue;
                m.SetColor("_EmissiveColor", Color.black); m.SetColor("_EmissiveColorLDR", Color.black); m.SetColor("_EmissiveColorHDR", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                HDMaterial.ValidateMaterial(m);
            }
    }
}
