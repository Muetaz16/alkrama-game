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
        // the full ACES transform (RRT + ODT), not the fitted curve: a very bright light loses its colour on the way
        // to white the way it does on film, instead of clipping to a flat saturated patch
        var tone = Add<Tonemapping>(); tone.mode.Override(TonemappingMode.ACES); tone.useFullACES.Override(true);
        tone.acesPreset.Override(HDRACESPreset.ACES1000Nits); tone.detectPaperWhite.Override(true); tone.detectBrightnessLimits.Override(true);     // only used on an HDR display (-hdr)

        // the URP grade, value for value (exposure, saturation and filter belong to the hour: TimeOfDay)
        var ca = Add<ColorAdjustments>();
        ca.contrast.Override(17f);                // documentary stock: firm, but the shade keeps its detail (22 crushed it to black) ca.hueShift.Override(0f);
        var wb = Add<WhiteBalance>(); wb.temperature.Override(-1f);        // URP had +5; with bounce light off the sand the picture is warm enough wb.tint.Override(0f);
        var smh = Add<ShadowsMidtonesHighlights>();                                   // shade a little cool, sunlight a little warm
        // the war-documentary split: shade goes cold and a little green (steel, concrete, smoke), sunlight stays warm (dust, sand)
        smh.shadows.Override(new Vector4(0.9f, 1.0f, 1.06f, -0.03f)); smh.midtones.Override(new Vector4(1f, 0.995f, 0.98f, 0f)); smh.highlights.Override(new Vector4(1.07f, 1.0f, 0.9f, 0f));
        smh.shadowsStart.Override(0f); smh.shadowsEnd.Override(0.3f); smh.highlightsStart.Override(0.55f); smh.highlightsEnd.Override(1f);
        var lgg = Add<LiftGammaGain>();
        lgg.lift.Override(new Vector4(0.98f, 1f, 1.03f, 0.035f));       // blacks lifted a touch and cool: nothing crushed, like a print lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0.06f)); lgg.gain.Override(new Vector4(1f, 1f, 1f, 0f));
        var curves = Add<ColorCurves>();                                                // ~15 % out of sand-yellow, ~10 % out of sky blue
        curves.hueVsSat.Override(new TextureCurve(new[]
        {
            new Keyframe(0.06f, 0.5f), new Keyframe(0.125f, 0.47f), new Keyframe(0.20f, 0.5f),
            new Keyframe(0.50f, 0.5f), new Keyframe(0.60f, 0.45f), new Keyframe(0.70f, 0.5f),
        }, 0.5f, true, new Vector2(0f, 1f)));

        var bloom = Add<Bloom>();
        bloom.threshold.Override(1.0f); bloom.intensity.Override(0.2f); bloom.scatter.Override(0.72f); bloom.tint.Override(new Color(1f, 0.96f, 0.9f));     // light flares softly through dusty glass
        var vig = Add<Vignette>();
        vig.mode.Override(VignetteMode.Procedural); vig.color.Override(Color.black); vig.intensity.Override(0.26f); vig.smoothness.Override(0.4f); vig.rounded.Override(false);
        var grain = Add<FilmGrain>(); grain.type.Override(FilmGrainLookup.Medium1); grain.intensity.Override(0.2f); grain.response.Override(0.75f);     // 16 mm news stock, mostly in the shade
        var flare = Add<ScreenSpaceLensFlare>();
        flare.intensity.Override(0.55f); flare.tintColor.Override(new Color(1f, 0.93f, 0.82f));
        flare.firstFlareIntensity.Override(0.6f); flare.secondaryFlareIntensity.Override(0.35f); flare.warpedFlareIntensity.Override(0.3f);
        flare.streaksIntensity.Override(0.6f); flare.streaksLength.Override(0.45f); flare.streaksThreshold.Override(0.6f);
        flare.vignetteEffect.Override(0.8f); flare.chromaticAbberationIntensity.Override(0.5f);
        Add<ChromaticAberration>().intensity.Override(0.03f);
        // per-object motion vectors: the gun and arms move with the camera, so they stay sharp while the street streaks a little
        var mb = Add<MotionBlur>();
        mb.intensity.Override(0.22f);         // a hand-held camera's shutter mb.maximumVelocity.Override(60f); mb.minimumVelocity.Override(2f);
        mb.cameraRotationVelocityClamp.Override(0.03f);

        // ---- what HDRP adds over URP (GraphicsQuality switches these per level at run time)
        var ao = Add<ScreenSpaceAmbientOcclusion>();
        ao.intensity.Override(1.5f); ao.radius.Override(1.2f); ao.directLightingStrength.Override(0.3f); ao.temporalAccumulation.Override(true);
        var ssr = Add<ScreenSpaceReflection>();
        ssr.enabled.Override(true); ssr.reflectSky.Override(true);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);     // rough surfaces get a properly blurred reflection, not a mirror one faded out
        ssr.accumulationFactor.Override(0.8f);
        var gi = Add<GlobalIllumination>();                 // screen-space bounce light: sunlit ground lights the shaded wall
        gi.enable.Override(true);
        var cs = Add<ContactShadows>();
        cs.enable.Override(true); cs.length.Override(0.12f); cs.maxDistance.Override(60f); cs.fadeDistance.Override(8f); cs.opacity.Override(0.9f);
        Add<MicroShadowing>().enable.Override(true);
        var sh = Add<HDShadowSettings>();
        sh.maxShadowDistance.Override(ShadowDistance); sh.cascadeShadowSplitCount.Override(4);
        sh.cascadeShadowSplit0.Override(CascadeSplits.x); sh.cascadeShadowSplit1.Override(CascadeSplits.y); sh.cascadeShadowSplit2.Override(CascadeSplits.z);
        var water = Add<WaterRendering>(); water.enable.Override(true);
        // ray tracing (RT quality level only): the scene kept for tracing is what lies within 180 m of the camera
        var rts = Add<RayTracingSettings>();
        rts.buildMode.Override(RTASBuildMode.Automatic); rts.cullingMode.Override(RTASCullingMode.Sphere); rts.cullingDistance.Override(180f);
        rts.rayBias.Override(0.004f); rts.distantRayBias.Override(0.01f);

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
                var d = TimeOfDay.SkyDir(new Vector3(P(c[1]), P(c[2]), P(c[3]))); if (d.y < 0.1f) { d.y = 0.1f; }
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
        hd.angularDiameter = 0.8f;                         // the real sun is 0.53 degrees; a little more so the penumbra reads at street distances
        hd.dirLightPCSSMaxPenumbraSize = 0.7f; hd.dirLightPCSSBlockerSampleCount = 24; hd.dirLightPCSSFilterSampleCount = 16;
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
        w.refractionColor = new Color(0.1f, 0.6f, 0.62f); w.maxRefractionDistance = 0.6f; w.absorptionDistance = 9f;
        w.scatteringColor = new Color(0.02f, 0.36f, 0.46f); w.ambientScattering = 0.6f; w.heightScattering = 0.2f; w.displacementScattering = 0.25f;
        w.directLightTipScattering = 0.5f; w.directLightBodyScattering = 0.4f;
        w.startSmoothness = 0.95f; w.endSmoothness = 0.85f; w.smoothnessFadeStart = 100f; w.smoothnessFadeDistance = 500f;
        w.tessellation = true; w.maxTessellationFactor = 2f;
        w.foam = true; w.simulationFoamAmount = 0.25f; w.foamTextureTiling = 0.35f; w.foamPersistenceMultiplier = 0.6f; w.foamSmoothness = 0.2f;
        w.foamResolution = WaterSurface.WaterDecalRegionResolution.Resolution1024; w.decalRegionSize = new Vector2(240, 240);
        ShoreFoam(go.transform);
        w.caustics = true; w.causticsIntensity = 0.4f; w.causticsPlaneBlendDistance = 2f; w.virtualPlaneDistance = 3f;
        w.underWater = true; w.volumeBounds = null; w.volumeDepth = 40f;
        EditorUtility.SetDirty(w);
        Debug.Log($"[cinematic] sea: HDRP ocean at y {WaterLevel}, the old water mesh is switched off");
    }

    // Surf: where the sea meets the land, a line of foam patches (HDRP water decals), laid along the shore from
    // the map's water cells. Only the shore within reach of the fight: the foam is drawn in a 240 m square that
    // follows the camera (WaterFollow), and the pipeline takes at most 128 decals.
    [Serializable] class MapInfo { public float half; public float seaCell; }
    static void ShoreFoam(Transform ocean)
    {
        var old = ocean.Find("Shore foam"); if (old) Object.DestroyImmediate(old.gameObject);
        var holder = new GameObject("Shore foam").transform; holder.SetParent(ocean, false);
        var map = JsonUtility.FromJson<MapInfo>(File.ReadAllText("Assets/Benghazi/Data/benghazi_corniche.json"));
        float half = map.half, cell = map.seaCell; int n = Mathf.RoundToInt(2 * half / cell);
        var sea = new bool[n, n];
        foreach (var s in SceneBuilder.ParseSea())
        {
            int j = Mathf.Clamp(Mathf.FloorToInt((s[2] + half) / cell + 0.01f), 0, n - 1);
            int i0 = Mathf.Clamp(Mathf.RoundToInt((s[0] + half) / cell), 0, n), i1 = Mathf.Clamp(Mathf.RoundToInt((s[1] + half) / cell), 0, n);
            for (int i = i0; i < i1; i++) sea[i, j] = true;
        }
        var player = GameObject.Find("Player"); var centre = player ? player.transform.position : Vector3.zero;
        var placed = new List<Vector3>(); const float step = 9f, reach = 420f; int made = 0;
        // HDRP's default decal material does not make foam. This is the material its own foam generator used:
        // a soft-edged rectangle that only writes foam (no deformation of the surface).
        const string foamPath = "Assets/Benghazi/HDRP/ShoreFoam.mat";
        const string FoamShaderPath = "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/ShaderGraph/Water Decal Sample.shadergraph";
        var foamShader = AssetDatabase.LoadAssetAtPath<Shader>(FoamShaderPath);
        var foamMat = AssetDatabase.LoadAssetAtPath<Material>(foamPath);
        if (!foamMat) { foamMat = new Material(foamShader) { name = "ShoreFoam" }; AssetDatabase.CreateAsset(foamMat, foamPath); }
        foamMat.shader = foamShader;
        foamMat.SetFloat("_AffectDeformation", 0f); foamMat.SetFloat("_AffectFoam", 1f); foamMat.SetFloat("_TYPE", 1f);
        foamMat.SetVector("_Blend_Distance", new Vector4(0.2f, 0.45f, 0, 0));
        MaterialEditor.ApplyMaterialPropertyDrawers(foamMat); HDMaterial.ValidateMaterial(foamMat); EditorUtility.SetDirty(foamMat);
        Debug.Log($"[cinematic] shore foam material: shader {foamShader.name}, keywords [{string.Join(" ", foamMat.shaderKeywords)}]");
        var cand = new List<(Vector3 p, Vector3 toSea)>();
        for (int j = 1; j < n - 1; j++) for (int i = 1; i < n - 1; i++)
        {
            if (!sea[i, j]) continue;
            Vector3 toLand = Vector3.zero;
            foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) if (!sea[i + di, j + dj]) toLand += new Vector3(-di, 0, dj);     // web x -> Unity -x
            if (toLand == Vector3.zero) continue;
            float wx = -half + (i + 0.5f) * cell, wz = -half + (j + 0.5f) * cell;
            var p = new Vector3(-wx, 0, wz) + toLand.normalized * (cell * 0.35f);
            if ((p - centre).sqrMagnitude < reach * reach) cand.Add((p, -toLand.normalized));
        }
        foreach (var (p, toSea) in cand.OrderBy(c => (c.p - centre).sqrMagnitude))
        {
            if (made >= 120) break;
            bool near = false; foreach (var q in placed) if ((q - p).sqrMagnitude < step * step) { near = true; break; }
            if (near) continue;
            placed.Add(p);
            var g = new GameObject("foam"); g.transform.SetParent(holder, false);
            g.transform.SetPositionAndRotation(new Vector3(p.x, WaterLevel, p.z) + toSea * 1.2f, Quaternion.LookRotation(toSea));
            var d = g.AddComponent<WaterDecal>();
            d.material = foamMat; d.resolution = new Vector2Int(64, 64);
            d.regionSize = new Vector2(step * 1.5f, 5.5f); d.amplitude = 0f; d.surfaceFoamDimmer = 0.75f; d.deepFoamDimmer = 0.6f;
            made++;
        }
        if (!ocean.GetComponent<WaterFollow>()) ocean.gameObject.AddComponent<WaterFollow>();
        Debug.Log($"[cinematic] shore foam: {made} patches along {cand.Count} shore cells within {reach:F0} m (cell {cell} m)");
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
