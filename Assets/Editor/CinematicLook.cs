// Cinematic rendering pass for the HDRP build: configuration only. Nothing in the scene is regenerated.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod CinematicLook.Run
//
// What it sets up (all absolute values, safe to run again):
//   Assets/Settings/HDRP_PC.asset         the pipeline (HdrpSetup.Pipeline) and the run-time material templates
//   every generator material              URP/Lit -> HDRP/Lit (HdrpSetup.ConvertAll)
//   Assets/Benghazi/PostFX.asset          tonemapping, grade, bloom, vignette, grain, motion blur, lens flare,
//                                          ambient occlusion, screen-space reflections and bounce light, contact
//                                          shadows, shadow cascades            -> the scene's "Post FX" volume
//   Assets/Benghazi/HDRP/SkyAndFog.asset  the afternoon sky, fog, fixed exposure (TimeOfDay.Write, the same code
//                                          the game uses when the hour changes)  -> the scene's "Sky and Fog" volume
//   Sun                                   HDRP directional light: lux, soft shadows, contact shadows, sun shafts
//   Main Camera                           temporal anti-aliasing, dithering, dynamic resolution allowed
//   Sea                                   the URP water mesh is switched off; an HDRP Water System ocean takes its place
//
// The look itself (grade, haze, sun colours per hour) is the one tuned on the URP branch; see TimeOfDay.cs for
// how its numbers carry over to HDRP's units.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class CinematicLook
{
    const string ScenePath = "Assets/Scenes/Corniche.unity";
    const string PostPath = "Assets/Benghazi/PostFX.asset", SkyPath = "Assets/Benghazi/HDRP/SkyAndFog.asset";
    const int EditorTime = 2;                       // the hour the saved scene (and the editor's review shots) shows: AFTERNOON

    // Sun shadows for a ~300 m street scene: 4 cascades, ends at 9 / 27 / 67 / 150 m
    const float ShadowDistance = 150f;
    static readonly Vector3 CascadeSplits = new Vector3(0.06f, 0.18f, 0.45f);
    public const float WaterLevel = -0.22f;         // PlayerController.WaterLevel

    public static void Run()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            HdrpSetup.BeginUrp();          // so a change to the conversion rules reaches the materials already converted
            if (!Apply()) { Fail("nothing was saved"); return; }
            if (!EditorSceneManager.SaveScene(scene)) { Fail("could not save " + ScenePath); return; }
            AssetDatabase.SaveAssets();
            Debug.Log("[cinematic] done - scene and assets saved");
        }
        catch (Exception e) { Debug.LogException(e); Fail("exception: " + e.Message); }
    }

    // Applies the look to the scene that is open (it must be Corniche) without opening or saving the scene.
    // Called at the end of LookUpgrade.All / War.
    public static bool Apply()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) return Missing($"the open scene is '{scene.path}', expected {ScenePath}");
        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        var sun = RenderSettings.sun && RenderSettings.sun.type == LightType.Directional ? RenderSettings.sun : lights.FirstOrDefault(l => l.type == LightType.Directional);
        if (!sun) return Missing("a directional light (the Sun)");
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).FirstOrDefault(c => c.CompareTag("MainCamera"));
        if (!cam) return Missing("a camera tagged MainCamera");

        HdrpSetup.Pipeline();
        HdrpSetup.Templates();
        if (!AssetDatabase.LoadAssetAtPath<VolumeProfile>($"Assets/Resources/Sky/SkyVol_{TimeOfDay.Presets[EditorTime].name}.asset")) SkySet.Build();
        HdrpSetup.ConvertAll();

        PostFx();
        SkyAndFog(sun);
        Sun(sun);
        MainCamera(cam);
        Water();
        Legacy();
        Quality();

        EditorSceneManager.MarkSceneDirty(scene);
        return true;
    }

    static bool Missing(string what) { Debug.LogError("[cinematic] MISSING: " + what + " - nothing was changed"); return false; }
    static void Fail(string why) { Debug.LogError("[cinematic] FAILED: " + why); if (Application.isBatchMode) EditorApplication.Exit(1); }

    // ---------------------------------------------------------------- volumes
    static Volume GlobalVolume(string name, float priority, VolumeProfile profile)
    {
        var go = GameObject.Find(name) ?? new GameObject(name);
        var v = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
        v.isGlobal = true; v.weight = 1f; v.priority = priority; v.sharedProfile = profile; v.enabled = true;
        EditorUtility.SetDirty(v);
        return v;
    }

    // a fresh profile asset: the old one may hold another pipeline's overrides
    static VolumeProfile FreshProfile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var old = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (old)
        {
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path)) if (sub && sub != old) Object.DestroyImmediate(sub, true);
            old.components.Clear(); EditorUtility.SetDirty(old);
            return old;                                    // same asset, same GUID: the scene's reference stays valid
        }
        var p = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(p, path); return p;
    }

    // an override made with profile.Add<T>() only exists in memory until it is a sub-asset of the profile
    static void Store(VolumeProfile profile)
    {
        foreach (var c in profile.components)
        {
            if (!c || AssetDatabase.Contains(c)) continue;
            c.name = c.GetType().Name; c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(c, profile);
        }
        foreach (var c in profile.components) if (c) EditorUtility.SetDirty(c);
        EditorUtility.SetDirty(profile);
    }

    // ---------------------------------------------------------------- post-processing and screen-space lighting
    static void PostFx()
    {
        var p = FreshProfile(PostPath);
        T Add<T>() where T : VolumeComponent { var c = p.Add<T>(false); c.active = true; return c; }

        // ACES: filmic shoulder and toe
        Add<Tonemapping>().mode.Override(TonemappingMode.ACES);

        // the URP grade, value for value (exposure, saturation and filter belong to the hour: TimeOfDay)
        var ca = Add<ColorAdjustments>();
        ca.contrast.Override(10f); ca.hueShift.Override(0f);
        var wb = Add<WhiteBalance>(); wb.temperature.Override(5f); wb.tint.Override(0f);
        var smh = Add<ShadowsMidtonesHighlights>();                                   // shade a little cool, sunlight a little warm
        smh.shadows.Override(new Vector4(0.95f, 0.99f, 1.05f, -0.02f)); smh.midtones.Override(new Vector4(1f, 1f, 1f, 0f)); smh.highlights.Override(new Vector4(1.05f, 1.0f, 0.95f, 0f));
        smh.shadowsStart.Override(0f); smh.shadowsEnd.Override(0.3f); smh.highlightsStart.Override(0.55f); smh.highlightsEnd.Override(1f);
        var lgg = Add<LiftGammaGain>();
        lgg.lift.Override(new Vector4(1f, 1f, 1f, 0.012f)); lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0.06f)); lgg.gain.Override(new Vector4(1f, 1f, 1f, 0f));
        var curves = Add<ColorCurves>();                                                // ~15 % out of sand-yellow, ~10 % out of sky blue
        curves.hueVsSat.Override(new TextureCurve(new[]
        {
            new Keyframe(0.06f, 0.5f), new Keyframe(0.125f, 0.47f), new Keyframe(0.20f, 0.5f),
            new Keyframe(0.50f, 0.5f), new Keyframe(0.60f, 0.45f), new Keyframe(0.70f, 0.5f),
        }, 0.5f, true, new Vector2(0f, 1f)));

        var bloom = Add<Bloom>();
        bloom.threshold.Override(1.1f); bloom.intensity.Override(0.16f); bloom.scatter.Override(0.6f); bloom.tint.Override(Color.white);
        var vig = Add<Vignette>();
        vig.mode.Override(VignetteMode.Procedural); vig.color.Override(Color.black); vig.intensity.Override(0.28f); vig.smoothness.Override(0.4f); vig.rounded.Override(false);
        var grain = Add<FilmGrain>(); grain.type.Override(FilmGrainLookup.Thin1); grain.intensity.Override(0.22f); grain.response.Override(0.8f);
        var flare = Add<ScreenSpaceLensFlare>();
        flare.intensity.Override(0.55f); flare.tintColor.Override(new Color(1f, 0.93f, 0.82f));
        flare.firstFlareIntensity.Override(0.6f); flare.secondaryFlareIntensity.Override(0.35f); flare.warpedFlareIntensity.Override(0.3f);
        flare.streaksIntensity.Override(0.6f); flare.streaksLength.Override(0.45f); flare.streaksThreshold.Override(0.6f);
        flare.vignetteEffect.Override(0.8f); flare.chromaticAbberationIntensity.Override(0.5f);
        Add<ChromaticAberration>().intensity.Override(0.04f);
        // per-object motion vectors: the gun and arms move with the camera, so they stay sharp while the street streaks a little
        var mb = Add<MotionBlur>();
        mb.intensity.Override(0.14f); mb.maximumVelocity.Override(60f); mb.minimumVelocity.Override(2f);
        mb.cameraRotationVelocityClamp.Override(0.03f);

        // ---- what HDRP adds over URP (GraphicsQuality switches these per level at run time)
        var ao = Add<ScreenSpaceAmbientOcclusion>();
        ao.intensity.Override(1.5f); ao.radius.Override(1.2f); ao.directLightingStrength.Override(0.3f); ao.temporalAccumulation.Override(true);
        var ssr = Add<ScreenSpaceReflection>();
        ssr.enabled.Override(true); ssr.reflectSky.Override(true);
        var gi = Add<GlobalIllumination>();                 // screen-space bounce light: sunlit ground lights the shaded wall
        gi.enable.Override(true);
        var cs = Add<ContactShadows>();
        cs.enable.Override(true); cs.length.Override(0.12f); cs.maxDistance.Override(60f); cs.fadeDistance.Override(8f); cs.opacity.Override(0.9f);
        Add<MicroShadowing>().enable.Override(true);
        var sh = Add<HDShadowSettings>();
        sh.maxShadowDistance.Override(ShadowDistance); sh.cascadeShadowSplitCount.Override(4);
        sh.cascadeShadowSplit0.Override(CascadeSplits.x); sh.cascadeShadowSplit1.Override(CascadeSplits.y); sh.cascadeShadowSplit2.Override(CascadeSplits.z);
        var water = Add<WaterRendering>(); water.enable.Override(true);

        Store(p);
        GlobalVolume("Post FX", 0, p);
        Debug.Log($"[cinematic] post FX profile {PostPath}: {string.Join(", ", p.components.Select(c => c.GetType().Name))}");
    }

    // ---------------------------------------------------------------- sky, fog, exposure: the afternoon, as the game sets it
    static void SkyAndFog(Light sun)
    {
        var preset = TimeOfDay.Presets[EditorTime];
        var p = FreshProfile(SkyPath);
        var rig = TimeOfDay.Rig.In(p);
        Cubemap cube = null;
        var sp = AssetDatabase.LoadAssetAtPath<VolumeProfile>($"Assets/Resources/Sky/SkyVol_{preset.name}.asset");
        if (sp && sp.TryGet<HDRISky>(out var hs)) cube = hs.hdriSky.value as Cubemap;
        if (!cube) Debug.LogWarning("[cinematic] no sky cubemap for " + preset.name + " (run SkySet.Run): the sky will be black in the editor");
        TimeOfDay.Write(rig, preset, cube, cube ? TimeOfDay.RealSkyExposure[EditorTime] : preset.skyExposure, 1f);
        rig.fog.enableVolumetricFog.Override(true);
        Store(p);
        GlobalVolume("Sky and Fog", 1, p);

        // the sun stands where it is in that sky's photograph
        var info = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/Sky/sky_info.txt");
        if (info)
            foreach (var line in info.text.Split('\n'))
            {
                var c = line.Trim().Split(';'); if (c.Length < 4 || c[0] != preset.name) continue;
                float P(string v) => float.Parse(v, CultureInfo.InvariantCulture);
                var d = new Vector3(P(c[1]), P(c[2]), P(c[3])); if (d.y < 0.1f) { d.y = 0.1f; }
                sun.transform.rotation = Quaternion.LookRotation(-d.normalized); EditorUtility.SetDirty(sun.transform);
            }
        TimeOfDay.WriteSun(sun, preset);
        // the built-in pipeline's sky and fog are not used by HDRP
        RenderSettings.fog = false; RenderSettings.skybox = null; RenderSettings.sun = sun;
        Debug.Log($"[cinematic] sky and fog: {preset.name}, sky {(cube ? cube.name : "none")}, sun {sun.intensity:F2} lux at elevation {Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1, 1)) * Mathf.Rad2Deg:F0} deg, fixed exposure {rig.expo.fixedExposure.value:F2} EV");
    }

    static void Sun(Light sun)
    {
        var hd = sun.GetComponent<HDAdditionalLightData>() ?? sun.gameObject.AddComponent<HDAdditionalLightData>();
        sun.lightUnit = LightUnit.Lux; sun.useColorTemperature = false; sun.shadows = LightShadows.Soft; sun.cookie = null;
        TimeOfDay.WriteSun(sun, TimeOfDay.Presets[EditorTime]);
        hd.angularDiameter = 1.2f;                         // a little larger than the real sun: softer shadow edges with distance
        hd.SetShadowResolutionOverride(false); hd.SetShadowResolutionLevel(2);
        hd.useContactShadow.useOverride = true; hd.useContactShadow.@override = true;
        hd.affectsVolumetric = true; hd.volumetricDimmer = 1f; hd.volumetricShadowDimmer = 1f;
        hd.interactsWithSky = false;                       // the sky is a photograph, the sun is already in it
        EditorUtility.SetDirty(sun); EditorUtility.SetDirty(hd);
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            if (l != sun && !l.GetComponent<HDAdditionalLightData>()) l.gameObject.AddComponent<HDAdditionalLightData>();
        Debug.Log($"[cinematic] sun '{sun.name}': HDRP light, lux, soft shadows + contact shadows, sun shafts in the fog");
    }

    // ---------------------------------------------------------------- camera
    // Temporal anti-aliasing: HDRP writes motion vectors for every lit object (the gun and arms included), so the
    // objections that kept TAA out of the URP build do not apply; it is also what the upscaler and the
    // screen-space effects need to be stable.
    static void MainCamera(Camera cam)
    {
        var data = cam.GetComponent<HDAdditionalCameraData>() ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
        data.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
        data.TAAQuality = HDAdditionalCameraData.TAAQualityLevel.High;
        data.taaSharpenStrength = 0.55f; data.taaAntiFlicker = 0.6f; data.taaHistorySharpening = 0.4f;
        data.dithering = true; data.stopNaNs = true; data.allowDynamicResolution = true;
        data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
        cam.allowHDR = true;
        EditorUtility.SetDirty(cam); EditorUtility.SetDirty(data);
        Debug.Log($"[cinematic] camera '{cam.name}': TAA high, dithering, stop NaN, dynamic resolution allowed");
    }

    // ---------------------------------------------------------------- the sea: HDRP Water System
    // An ocean surface at the game's water level. It lies under all the land (the ground is above it), so only
    // real water shows. Calm Mediterranean: a low swell and wind ripples - the player wades and swims at a fixed
    // level (PlayerController.WaterLevel), so the swell is kept to a few centimetres.
    static void Water()
    {
        var sea = GameObject.Find("Sea");
        if (sea && sea.TryGetComponent<MeshRenderer>(out var mr)) { mr.enabled = false; mr.sharedMaterial = null; EditorUtility.SetDirty(mr); }      // no reference left to the URP water shader
        var go = GameObject.Find("Ocean") ?? new GameObject("Ocean");
        go.transform.SetPositionAndRotation(new Vector3(0, WaterLevel, 0), Quaternion.identity); go.transform.localScale = Vector3.one;
        var w = go.GetComponent<WaterSurface>() ?? go.AddComponent<WaterSurface>();
        w.surfaceType = WaterSurfaceType.OceanSeaLake; w.geometryType = WaterGeometryType.Infinite;
        w.timeMultiplier = 1f; w.scriptInteractions = false;
        // swell + ripples
        w.repetitionSize = 120f; w.largeWindSpeed = 9f; w.largeChaos = 0.85f; w.largeOrientationValue = 200f;
        w.largeBand0Multiplier = 0.55f; w.largeBand1Multiplier = 0.8f;
        w.ripples = true; w.ripplesWindSpeed = 7f; w.ripplesChaos = 0.9f;
        // colour: clear turquoise in the shallows going to deep blue (refraction with absorption, scattering in the body)
        w.refractionColor = new Color(0.12f, 0.55f, 0.6f); w.maxRefractionDistance = 0.6f; w.absorptionDistance = 7f;
        w.scatteringColor = new Color(0.0f, 0.3f, 0.42f); w.ambientScattering = 0.45f; w.heightScattering = 0.2f; w.displacementScattering = 0.25f;
        w.directLightTipScattering = 0.5f; w.directLightBodyScattering = 0.4f;
        w.startSmoothness = 0.95f; w.endSmoothness = 0.85f; w.smoothnessFadeStart = 100f; w.smoothnessFadeDistance = 500f;
        w.tessellation = true; w.maxTessellationFactor = 2f;
        w.foam = true; w.simulationFoamAmount = 0.25f; w.foamTextureTiling = 0.2f;
        w.caustics = true; w.causticsIntensity = 0.4f; w.causticsPlaneBlendDistance = 2f; w.virtualPlaneDistance = 3f;
        w.underWater = true; w.volumeBounds = null; w.volumeDepth = 40f;
        EditorUtility.SetDirty(w);
        Debug.Log($"[cinematic] sea: HDRP ocean at y {WaterLevel}, the old water mesh is switched off");
    }

    // things only the built-in / URP renderers used
    static void Legacy()
    {
        var env = GameObject.Find("Environment");
        if (env) { Object.DestroyImmediate(env); Debug.Log("[cinematic] the sky-only reflection probe is gone: HDRP reflects the sky by itself"); }
    }

    static void Quality()
    {
        int keep = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable; QualitySettings.globalTextureMipmapLimit = 0;
            QualitySettings.lodBias = 2f; QualitySettings.maximumLODLevel = 0; QualitySettings.vSyncCount = 1;
        }
        QualitySettings.SetQualityLevel(keep, false);
    }
}
