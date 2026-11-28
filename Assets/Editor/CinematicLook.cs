// Cinematic rendering pass: configuration only. Nothing in the scene is deleted or regenerated, no new assets.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod CinematicLook.Run
//
// What it touches (all found, never created, except post-FX overrides inside the existing profile):
//   Assets/Settings/URP_PC.asset        shadows, MSAA, HDR grading, render scale (URP_Mobile is left alone)
//   Assets/Settings/URP_Renderer.asset  the existing SSAO feature's quality
//   the global Volume's profile         tonemapping, grade, bloom, vignette, grain, motion blur ...
//   Sun, RenderSettings, reflection probe, Main Camera, QualitySettings (levels that use URP_PC)
//
// IMPORTANT, found while writing this: Assets/Benghazi/PostFX.asset is saved with ten EMPTY component slots
// ("- {fileID: 0}"). SceneBuilder/LookUpgrade add the overrides with profile.Add<T>() but never store them inside
// the asset (AssetDatabase.AddObjectToAsset), so they only live in the editor session that created them. A player
// built by a later Unity process has NO post-processing at all - no tonemapping, no grade, no bloom, and
// WeaponController.AimFocus never finds a DepthOfField. PostFx() below repairs that; it is the biggest single
// change in this file.
//
// Safe to run again: every value is an absolute constant, nothing is relative to what was there before.
// To keep the look after a full rebuild, call CinematicLook.Apply() at the end of LookUpgrade.All (see Apply).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class CinematicLook
{
    // ---------------------------------------------------------------- tunables
    const string ScenePath = "Assets/Scenes/Corniche.unity";
    const string PcAssetPath = "Assets/Settings/URP_PC.asset";
    const string RendererPath = "Assets/Settings/URP_Renderer.asset";

    // Supersampling. 1.0 = the game renders at the display resolution (it does NOT render "8K").
    // 1.25 means 56 % more pixels to shade on top of 4x MSAA: only raise it on a GPU with headroom.
    const float SupersampleRenderScale = 1.0f;

    // Anti-aliasing: 4x MSAA (geometry edges, and alpha-to-coverage on the palm fronds) + SMAA High (the rest).
    // TAA is left out on purpose - see the report / the comment in Camera().
    const int MsaaSamples = 4;
    const bool UseTaaInstead = false;      // true: MSAA off, camera TAA. Not validated with the view model.

    // Sun shadows for a ~300 m street scene: 4 cascades in a 4096 atlas (2048 each).
    // Cascade ends at 9 / 27 / 67 / 150 m -> about 1 / 3 / 7 / 15 cm per shadow texel.
    const float ShadowDistance = 150f;
    static readonly Vector3 CascadeSplits = new Vector3(0.06f, 0.18f, 0.45f);
    const float CascadeBorder = 0.15f;
    const float ShadowDepthBias = 1.0f, ShadowNormalBias = 0.8f;

    // Sun. The direction is kept: LookPro points it at the sun in the photographed sky (48 degrees up),
    // and moving it would put the shadows at odds with the visible sun. Set > 0 to force an elevation anyway
    // (the compass direction is kept).
    static readonly float SunElevationOverrideDeg = 0f;
    const float SunIntensity = 1.45f;
    static readonly Color SunColor = new Color(1f, 0.74f, 0.5f);         // sunset: low and orange (the sky photo is a sunset too)
    const float SunShadowStrength = 0.93f;
    const float AmbientIntensity = 1.25f;                                  // sky fill: ACES darkens the shade a lot, it needs the light

    // Haze: sky-horizon blue pulled 30 % toward dust, slightly denser (100 m: 10 %, 200 m: 34 %, 300 m: 60 %).
    // dust in the air of a city at war: warm grey haze (100 m: 15 %, 200 m: 50 %)
    const float FogDensity = 0.0046f;
    static readonly Color FogColor = new Color(0.8f, 0.66f, 0.52f);        // golden dust against the low sun

    // Exposure. URP has no auto exposure; this is a fixed offset in stops, applied before ACES.
    // Worked out for: sunlit pale wall ~0.86, sunlit white ~0.90, wall in shade ~0.55 (display values).
    const float PostExposure = 0.55f;      // ACES sits about a stop under the untonemapped picture; first seen at -0.3 the shade went black

    // ---------------------------------------------------------------- entry points
    public static void Run()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!Apply()) { Fail("nothing was saved"); return; }
            if (!EditorSceneManager.SaveScene(scene)) { Fail("could not save " + ScenePath); return; }
            AssetDatabase.SaveAssets();
            Debug.Log("[cinematic] done - scene and assets saved");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Fail("exception: " + e.Message);
        }
    }

    // Applies the look to the scene that is open (it must be Corniche) without opening or saving the scene.
    // Hook for a full rebuild - in LookUpgrade.All, after GameplaySetup.Run() and before SaveScene(scene):
    //     CinematicLook.Apply();
    public static bool Apply()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) return Missing($"the open scene is '{scene.path}', expected {ScenePath}");

        // find everything first, so a missing piece stops the run before anything is changed
        var pc = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PcAssetPath);
        if (!pc) return Missing("URP asset " + PcAssetPath);
        var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (!rendererData) return Missing("renderer data " + RendererPath);
        var ssao = rendererData.rendererFeatures.FirstOrDefault(f => f && f.GetType().Name == "ScreenSpaceAmbientOcclusion");
        if (!ssao) return Missing("the SSAO renderer feature on " + RendererPath + " (LookUpgrade.Run adds it)");
        var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include).FirstOrDefault(v => v.isGlobal);
        if (!volume) return Missing("a global Volume in the scene ('Post FX')");
        var profile = volume.sharedProfile;
        if (!profile) return Missing("the Volume profile on '" + volume.name + "'");
        if (!AssetDatabase.Contains(profile)) return Missing("the Volume profile as an asset (it is not saved in the project)");
        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        var sun = RenderSettings.sun && RenderSettings.sun.type == LightType.Directional ? RenderSettings.sun : lights.FirstOrDefault(l => l.type == LightType.Directional);
        if (!sun) return Missing("a directional light (the Sun)");
        var probe = Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include).FirstOrDefault();
        if (!probe) return Missing("the reflection probe ('Environment')");
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).FirstOrDefault(c => c.CompareTag("MainCamera"));
        if (!cam) return Missing("a camera tagged MainCamera");
        if (!RenderSettings.skybox) return Missing("a skybox material in the scene's lighting settings");
        var pcLevels = Enumerable.Range(0, QualitySettings.names.Length).Where(i => QualitySettings.GetRenderPipelineAssetAt(i) == pc).ToList();
        if (pcLevels.Count == 0) return Missing("a quality level that uses " + PcAssetPath);

        Pipeline(pc);
        Ssao(rendererData, ssao);
        PostFx(volume, profile);
        Lighting(sun, lights);
        Probe(probe);
        MainCamera(cam);
        Quality(pcLevels);

        EditorSceneManager.MarkSceneDirty(scene);
        return true;
    }

    static bool Missing(string what)
    {
        Debug.LogError("[cinematic] MISSING: " + what + " - nothing was changed");
        return false;
    }

    static void Fail(string why)
    {
        Debug.LogError("[cinematic] FAILED: " + why);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }

    // "name old -> new" for the log
    static string D(string name, object was, object now) => Equals(was, now) || was.ToString() == now.ToString() ? $"{name} {now} (same)" : $"{name} {was} -> {now}";
    static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- URP asset (PC)
    static void Pipeline(UniversalRenderPipelineAsset pc)
    {
        var log = new List<string>();
        int msaa = UseTaaInstead ? 1 : MsaaSamples;

        log.Add(D("HDR", pc.supportsHDR, true)); pc.supportsHDR = true;
        // 32-bit (R11G11B10) stays: the 64-bit buffer doubles the bandwidth of a 4x MSAA target for no visible gain here
        log.Add(D("HDR precision", pc.hdrColorBufferPrecision, HDRColorBufferPrecision._32Bits)); pc.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
        log.Add(D("MSAA", pc.msaaSampleCount, msaa)); pc.msaaSampleCount = msaa;
        log.Add(D("render scale", F(pc.renderScale), F(SupersampleRenderScale))); pc.renderScale = SupersampleRenderScale;
        pc.upscalingFilter = UpscalingFilterSelection.Auto;
        // grading in HDR, before the tonemapper: highlights roll off instead of clipping
        log.Add(D("grading", pc.colorGradingMode, ColorGradingMode.HighDynamicRange)); pc.colorGradingMode = ColorGradingMode.HighDynamicRange;
        log.Add(D("LUT", pc.colorGradingLutSize, 64)); pc.colorGradingLutSize = 64;       // finer grade steps: no banding in skies and in the night
        pc.supportsCameraDepthTexture = true;      // the water shader and SSAO read it
        // no shader here reads _CameraOpaqueTexture (Benghazi/Water does not refract), so no extra full-screen copy
        log.Add(D("opaque texture", pc.supportsCameraOpaqueTexture, false)); pc.supportsCameraOpaqueTexture = false;
        Debug.Log("[cinematic] URP_PC quality: " + string.Join(", ", log));

        log.Clear();
        log.Add(D("atlas", pc.mainLightShadowmapResolution, 4096)); pc.mainLightShadowmapResolution = 4096;
        log.Add(D("distance", F(pc.shadowDistance), F(ShadowDistance))); pc.shadowDistance = ShadowDistance;
        log.Add(D("cascades", pc.shadowCascadeCount, 4)); pc.shadowCascadeCount = 4;
        log.Add(D("splits", pc.cascade4Split.ToString("F3"), CascadeSplits.ToString("F3"))); pc.cascade4Split = CascadeSplits;
        log.Add(D("border", F(pc.cascadeBorder), F(CascadeBorder))); pc.cascadeBorder = CascadeBorder;
        log.Add(D("depth bias", F(pc.shadowDepthBias), F(ShadowDepthBias))); pc.shadowDepthBias = ShadowDepthBias;
        log.Add(D("normal bias", F(pc.shadowNormalBias), F(ShadowNormalBias))); pc.shadowNormalBias = ShadowNormalBias;
        pc.conservativeEnclosingSphere = true;
        log.Add(D("extra-light atlas", pc.additionalLightsShadowmapResolution, 2048)); pc.additionalLightsShadowmapResolution = 2048;
        EditorUtility.SetDirty(pc);

        // these four have no public setter in URP 17.6 (names checked against UniversalRenderPipelineAsset.cs)
        var so = new SerializedObject(pc);
        var sq = Find(so, "m_SoftShadowQuality");                                           // enum SoftShadowQuality: 1 Low, 2 Medium, 3 High
        log.Add(D("soft quality", (SoftShadowQuality)sq.intValue, SoftShadowQuality.High)); sq.intValue = (int)SoftShadowQuality.High;
        log.Add(SetBool(so, "m_SoftShadowsSupported", true, "soft shadows"));              // was OFF: the hard, blocky shadow edges
        log.Add(SetBool(so, "m_AdditionalLightShadowsSupported", true, "extra-light shadows"));
        log.Add(SetBool(so, "m_SupportsLightCookies", false, "light cookies"));           // unused: fewer shader variants
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pc);
        Debug.Log("[cinematic] URP_PC shadows: " + string.Join(", ", log));
    }

    static string SetBool(SerializedObject so, string prop, bool value, string label)
    {
        var p = so.FindProperty(prop);
        if (p == null || p.propertyType != SerializedPropertyType.Boolean)
            throw new InvalidOperationException($"serialized bool '{prop}' not found on {so.targetObject.name} ({so.targetObject.GetType().Name})");
        var s = D(label, p.boolValue, value); p.boolValue = value; return s;
    }

    static SerializedProperty Find(SerializedObject so, string prop)
    {
        var p = so.FindProperty(prop);
        if (p == null) throw new InvalidOperationException($"serialized property '{prop}' not found on {so.targetObject.name} ({so.targetObject.GetType().Name})");
        return p;
    }

    // ---------------------------------------------------------------- SSAO (existing feature, settings only)
    // field names checked against ScreenSpaceAmbientOcclusionSettings in URP 17.6
    static void Ssao(UniversalRendererData data, ScriptableRendererFeature ssao)
    {
        var log = new List<string>();
        var so = new SerializedObject(ssao);
        void Int(string n, int v) { var p = Find(so, "m_Settings." + n); log.Add(D(n, p.intValue, v)); p.intValue = v; }
        void Flt(string n, float v) { var p = Find(so, "m_Settings." + n); log.Add(D(n, F(p.floatValue), F(v))); p.floatValue = v; }
        void Bool(string n, bool v) { var p = Find(so, "m_Settings." + n); log.Add(D(n, p.boolValue, v)); p.boolValue = v; }
        Bool("Downsample", false);
        Bool("AfterOpaque", false);
        Int("Source", 1);                 // DepthNormals: real normals, no faceting on the boxy buildings
        Int("Samples", 0);                // High = 12 samples (was Medium, grainy on the white steps)
        Int("BlurQuality", 0);            // High = bilateral
        Flt("Intensity", 1.5f);
        Flt("Radius", 0.7f);
        Flt("DirectLightingStrength", 0.35f);   // a little contact shadow in direct sun too
        Flt("Falloff", 120f);
        so.ApplyModifiedPropertiesWithoutUndo();
        ssao.SetActive(true);
        EditorUtility.SetDirty(ssao); EditorUtility.SetDirty(data);
        Debug.Log("[cinematic] SSAO: " + string.Join(", ", log));
        // The renderer was saved without its PostProcessData (shaders and textures of the post stack): with
        // that reference empty URP skips post-processing altogether, whatever the Volume profile says.
        if (!data.postProcessData)
        {
            const string ppPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";
            var pp = AssetDatabase.LoadAssetAtPath<PostProcessData>(ppPath);
            if (!pp) throw new System.InvalidOperationException("post-process data not found at " + ppPath);
            data.postProcessData = pp; EditorUtility.SetDirty(data);
            Debug.Log("[cinematic] renderer: post-process data was missing (post-processing never ran) -> assigned the URP default");
        }
        else Debug.Log("[cinematic] renderer: post-process data present");
    }

    // ---------------------------------------------------------------- post-processing
    static void PostFx(Volume volume, VolumeProfile profile)
    {
        // 1. repair: drop the empty slots, and store every override inside the asset so a build gets them
        int empty = profile.components.RemoveAll(c => c == null);
        int stored = 0;
        foreach (var c in profile.components) if (Store(c, profile)) stored++;
        int before = profile.components.Count;

        var tone = Get<Tonemapping>(profile);
        // ACES: filmic shoulder and toe; it is what turns the flat, clipped picture into footage
        tone.mode.Override(TonemappingMode.ACES);

        // warm Mediterranean midday, restrained: slightly under, a little contrast (ACES already adds some),
        // saturation pulled back so sky and sand stop looking like poster paint
        var ca = Get<ColorAdjustments>(profile);
        ca.postExposure.Override(PostExposure);
        ca.contrast.Override(4f);               // a little more bite than plain ACES: film, not video
        ca.saturation.Override(-10f);
        ca.hueShift.Override(0f);
        ca.colorFilter.Override(new Color(1f, 0.99f, 0.97f));

        var wb = Get<WhiteBalance>(profile);
        wb.temperature.Override(5f);          // warm, short of the old +8 that made the walls too yellow
        wb.tint.Override(0f);

        // shade takes a trace of the sky, sun keeps a trace of warmth: what daylight does, well short of teal/orange
        var smh = Get<ShadowsMidtonesHighlights>(profile);
        // the cinema split: shade a little cool, sunlight a little warm
        smh.shadows.Override(new Vector4(0.95f, 0.99f, 1.05f, -0.02f));
        smh.midtones.Override(new Vector4(1f, 1f, 1f, 0f));
        smh.highlights.Override(new Vector4(1.05f, 1.0f, 0.95f, 0f));
        smh.shadowsStart.Override(0f); smh.shadowsEnd.Override(0.3f);
        smh.highlightsStart.Override(0.55f); smh.highlightsEnd.Override(1f);

        // blacks a touch deeper, mids held; neutral in colour
        var lgg = Get<LiftGammaGain>(profile);
        lgg.lift.Override(new Vector4(1f, 1f, 1f, 0.012f));        // keep detail in the blacks (gun, gloves, arches)
        lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0.06f));
        lgg.gain.Override(new Vector4(1f, 1f, 1f, 0f));

        // hue-vs-saturation: take ~15 % out of sand-yellow (walls, paving) and ~10 % out of sky blue.
        // 0.5 = unchanged. Skin and wood (hue below 0.07) are outside the dip.
        var curves = Get<ColorCurves>(profile);
        curves.hueVsSat.Override(new TextureCurve(new[]
        {
            new Keyframe(0.06f, 0.5f), new Keyframe(0.125f, 0.47f), new Keyframe(0.20f, 0.5f),
            new Keyframe(0.50f, 0.5f), new Keyframe(0.60f, 0.45f), new Keyframe(0.70f, 0.5f),
        }, 0.5f, true, new Vector2(0f, 1f)));

        // bloom only from what is really hot (sky, glints, muzzle flash, white plaster in full sun)
        var bloom = Get<Bloom>(profile);
        bloom.threshold.Override(1.3f);       // gamma space: about 1.8 in linear light
        bloom.intensity.Override(0.28f);
        bloom.scatter.Override(0.6f);
        bloom.tint.Override(Color.white);
        bloom.highQualityFiltering.Override(true);
        bloom.downscale.Override(BloomDownscaleMode.Half);
        bloom.maxIterations.Override(6);

        var vig = Get<Vignette>(profile);
        vig.color.Override(Color.black);
        vig.center.Override(new Vector2(0.5f, 0.5f));
        vig.intensity.Override(0.28f);
        vig.smoothness.Override(0.4f);
        vig.rounded.Override(false);

        var grain = Get<FilmGrain>(profile);
        grain.type.Override(FilmGrainLookup.Thin1);
        grain.intensity.Override(0.22f);
        grain.response.Override(0.8f);        // grain mostly in shade and midtones, like film

        // light that floods the lens when the sun (or a muzzle flash, a fire) is in the picture: a soft
        // ghost and a short streak, kept low - it should be felt, not noticed
        var flare = Get<ScreenSpaceLensFlare>(profile);
        flare.intensity.Override(0.55f); flare.tintColor.Override(new Color(1f, 0.93f, 0.82f));
        flare.firstFlareIntensity.Override(0.6f); flare.secondaryFlareIntensity.Override(0.35f); flare.warpedFlareIntensity.Override(0.3f);
        flare.streaksIntensity.Override(0.6f); flare.streaksLength.Override(0.45f); flare.streaksThreshold.Override(0.6f);
        flare.vignetteEffect.Override(0.8f); flare.chromaticAbberationIntensity.Override(0.5f);
        var chroma = Get<ChromaticAberration>(profile);
        chroma.intensity.Override(0.04f);     // barely there: only the frame corners

        // CameraAndObjects: the gun and arms get their own motion vectors, and as they move with the camera
        // their screen velocity is ~0, so they stay sharp while the world streaks a little on fast turns.
        // (CameraOnly blurs the weapon as if it were part of the street.)
        var mb = Get<MotionBlur>(profile);
        mb.mode.Override(MotionBlurMode.CameraAndObjects);
        mb.quality.Override(MotionBlurQuality.Medium);
        mb.intensity.Override(0.14f);
        mb.clamp.Override(0.03f);

        // depth of field belongs to WeaponController.AimFocus (it switches mode and focus distance at run time).
        // Only make sure the override exists for it to find; an existing one is not touched.
        bool dofAdded = false;
        if (!profile.TryGet<DepthOfField>(out var dof))
        {
            dof = Get<DepthOfField>(profile); dofAdded = true;
            dof.mode.Override(DepthOfFieldMode.Off);
            dof.focalLength.Override(55f); dof.aperture.Override(4f); dof.bladeCount.Override(6);   // LookUpgrade's values
        }
        Store(dof, profile);

        volume.isGlobal = true; volume.weight = 1f; volume.enabled = true;
        foreach (var c in profile.components) EditorUtility.SetDirty(c);
        EditorUtility.SetDirty(profile); EditorUtility.SetDirty(volume);

        Debug.Log($"[cinematic] post FX profile '{AssetDatabase.GetAssetPath(profile)}': {empty} empty slots removed, {stored} unsaved overrides stored in the asset, " +
                  $"{profile.components.Count - before} added, {profile.components.Count} total ({string.Join(", ", profile.components.Select(c => c.GetType().Name))})");
        Debug.Log($"[cinematic] post FX values: ACES, exposure {F(PostExposure)} EV, contrast +4, saturation -10, white balance +5, split cool shade / warm sun, hue-vs-sat dip on sand yellow and sky blue, " +
                  "bloom 0.28 @ 1.3, vignette 0.28, grain Thin1 0.22, chromatic 0.04, motion blur 0.14 (camera+objects, clamp 0.03), " +
                  "depth of field " + (dofAdded ? "added (Off, 55 mm f/4) for WeaponController" : "left as it was for WeaponController"));
    }

    static T Get<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (!profile.TryGet<T>(out var c)) c = profile.Add<T>(false);
        c.active = true;
        Store(c, profile);
        return c;
    }

    // an override made with profile.Add<T>() only exists in memory until it is a sub-asset of the profile
    static bool Store(VolumeComponent c, VolumeProfile profile)
    {
        if (AssetDatabase.Contains(c)) return false;
        c.name = c.GetType().Name;
        c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(c, profile);
        return true;
    }

    // ---------------------------------------------------------------- sun, sky light, haze
    static void Lighting(Light sun, Light[] all)
    {
        var log = new List<string>();
        var fwd = sun.transform.forward;
        float elevation = Mathf.Asin(Mathf.Clamp(-fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
        if (SunElevationOverrideDeg > 0f)
        {
            var flat = new Vector3(fwd.x, 0, fwd.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            float e = SunElevationOverrideDeg * Mathf.Deg2Rad;
            sun.transform.rotation = Quaternion.LookRotation(flat.normalized * Mathf.Cos(e) + Vector3.down * Mathf.Sin(e));
            log.Add(D("elevation", F(elevation), F(SunElevationOverrideDeg)));
        }
        else log.Add($"elevation {elevation:F1} deg kept (matches the sun in the sky photo)");
        log.Add(D("intensity", F(sun.intensity), F(SunIntensity))); sun.intensity = SunIntensity;
        log.Add(D("colour", sun.color.ToString("F3"), SunColor.ToString("F3"))); sun.color = SunColor;
        sun.useColorTemperature = false;
        log.Add(D("shadows", sun.shadows, LightShadows.Soft)); sun.shadows = LightShadows.Soft;
        log.Add(D("strength", F(sun.shadowStrength), F(SunShadowStrength))); sun.shadowStrength = SunShadowStrength;
        sun.cookie = null;
        // bias and softness come from the URP asset, so there is one place to tune them
        var ld = sun.GetUniversalAdditionalLightData();
        ld.usePipelineSettings = true;
        ld.softShadowQuality = SoftShadowQuality.UsePipelineSettings;
        RenderSettings.sun = sun;
        EditorUtility.SetDirty(sun); EditorUtility.SetDirty(ld); EditorUtility.SetDirty(sun.transform);
        Debug.Log($"[cinematic] sun '{sun.name}': {string.Join(", ", log)}; {all.Length - 1} other light(s) in the scene left alone");

        log.Clear();
        RenderSettings.ambientMode = AmbientMode.Skybox;
        log.Add(D("ambient", F(RenderSettings.ambientIntensity), F(AmbientIntensity))); RenderSettings.ambientIntensity = AmbientIntensity;
        log.Add(D("reflections", F(RenderSettings.reflectionIntensity), F(1f))); RenderSettings.reflectionIntensity = 1f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        log.Add(D("sky reflection size", RenderSettings.defaultReflectionResolution, 256)); RenderSettings.defaultReflectionResolution = 256;
        // the sunset photo, warmed and a little darker so the sky glows gold instead of washing out white
        if (RenderSettings.skybox.HasProperty("_Tint")) RenderSettings.skybox.SetColor("_Tint", new Color(0.6f, 0.49f, 0.4f));
        if (RenderSettings.skybox.HasProperty("_Exposure")) RenderSettings.skybox.SetFloat("_Exposure", 0.9f);
        EditorUtility.SetDirty(RenderSettings.skybox);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        log.Add(D("fog density", F(RenderSettings.fogDensity), F(FogDensity))); RenderSettings.fogDensity = FogDensity;
        log.Add(D("fog colour", RenderSettings.fogColor.ToString("F3"), FogColor.ToString("F3"))); RenderSettings.fogColor = FogColor;
        DynamicGI.UpdateEnvironment();
        Debug.Log($"[cinematic] environment (sky '{RenderSettings.skybox.name}' kept): " + string.Join(", ", log));
    }

    // ---------------------------------------------------------------- reflection probe
    static void Probe(ReflectionProbe probe)
    {
        var log = new List<string>();
        log.Add(D("resolution", probe.resolution, 512)); probe.resolution = 512;      // rendered once at start (EnvironmentInit): free at run time
        log.Add(D("HDR", probe.hdr, true)); probe.hdr = true;
        log.Add(D("intensity", F(probe.intensity), F(1f))); probe.intensity = 1f;
        EditorUtility.SetDirty(probe);
        Debug.Log($"[cinematic] reflection probe '{probe.name}': " + string.Join(", ", log));
    }

    // ---------------------------------------------------------------- camera
    // Why not TAA: it needs per-object motion vectors for everything that moves. The muzzle flash, tracers and
    // red dot use Sprites/Default and Benghazi/Water has only a forward pass - none of them write motion vectors,
    // so they would ghost or shimmer; the view model's recoil and reload moves are fast and centimetres from the
    // lens; and the sniper's jump to an 11 degree field of view leaves stale history for a few frames. TAA also
    // switches MSAA off. MSAA + SMAA has none of these problems and no ghosting. The scope ring and HUD are drawn
    // with OnGUI after the camera, so neither method touches them.
    static void MainCamera(Camera cam)
    {
        var log = new List<string>();
        var data = cam.GetUniversalAdditionalCameraData();
        var mode = UseTaaInstead ? AntialiasingMode.TemporalAntiAliasing : AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        log.Add(D("post-processing", data.renderPostProcessing, true)); data.renderPostProcessing = true;
        log.Add(D("AA", data.antialiasing, mode)); data.antialiasing = mode;
        log.Add(D("AA quality", data.antialiasingQuality, AntialiasingQuality.High)); data.antialiasingQuality = AntialiasingQuality.High;
        log.Add(D("dithering", data.dithering, true)); data.dithering = true;       // hides banding in the sky gradient
        log.Add(D("stop NaN", data.stopNaN, true)); data.stopNaN = true;            // a bad pixel cannot bloom into a black square
        log.Add(D("allow HDR", cam.allowHDR, true)); cam.allowHDR = true;
        log.Add(D("allow MSAA", cam.allowMSAA, true)); cam.allowMSAA = true;
        EditorUtility.SetDirty(cam); EditorUtility.SetDirty(data);
        Debug.Log($"[cinematic] camera '{cam.name}': " + string.Join(", ", log));
    }

    // ---------------------------------------------------------------- QualitySettings (levels on URP_PC only)
    static void Quality(List<int> levels)
    {
        int keep = QualitySettings.GetQualityLevel();
        int msaa = UseTaaInstead ? 0 : MsaaSamples;
        var names = QualitySettings.names;
        foreach (int i in levels)
        {
            QualitySettings.SetQualityLevel(i, false);
            var log = new List<string>();
            log.Add(D("anisotropic", QualitySettings.anisotropicFiltering, AnisotropicFiltering.ForceEnable)); QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            log.Add(D("MSAA", QualitySettings.antiAliasing, msaa)); QualitySettings.antiAliasing = msaa;      // URP overwrites this from the asset at run time; kept equal
            log.Add(D("mip limit", QualitySettings.globalTextureMipmapLimit, 0)); QualitySettings.globalTextureMipmapLimit = 0;
            log.Add(D("LOD bias", F(QualitySettings.lodBias), F(2f))); QualitySettings.lodBias = 2f;
            QualitySettings.maximumLODLevel = 0;
            log.Add(D("vSync", QualitySettings.vSyncCount, 1)); QualitySettings.vSyncCount = 1;
            log.Add(D("soft particles", QualitySettings.softParticles, true)); QualitySettings.softParticles = true;
            log.Add(D("realtime probes", QualitySettings.realtimeReflectionProbes, true)); QualitySettings.realtimeReflectionProbes = true;
            Debug.Log($"[cinematic] quality level {i} '{names[i]}': " + string.Join(", ", log));
        }
        QualitySettings.SetQualityLevel(keep, false);
    }
}
