using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Picture quality for the HDRP build, switched while the game runs.
//   F8                 next level (LOW -> MEDIUM -> HIGH -> ULTRA)
//   -quality <name>    start at low | medium | high | ultra
//   -novsync           do not wait for the monitor (to measure the real frame rate)
//   -fpslog            write the frame rate to the player log every 5 seconds
// A level is: how many pixels are drawn before the temporal upscaler brings the picture back to the window's
// size, how sharp and how far the sun shadows go, and which of the screen-space effects HDRP adds over URP are
// on (reflections, bounce light, shadows at contact, fog you can see the sun shafts in). It is a global volume
// (priority 80) over the scene's, plus the sun's shadow map and the dynamic-resolution scale - nothing is
// rebuilt, so the change is instant. Starts itself; nothing in the scene refers to it.
public class GraphicsQuality : MonoBehaviour
{
    public struct Level
    {
        public string name; public float scale;              // % of the window drawn
        public int shadowLevel; public float shadowDistance;   // 0..3 = 1024 / 2048 / 2048 / 4096 for the sun
        public bool ssr, ssgi, contact, volumetric; public int effects;    // 0..2 quality of the screen-space effects
        public float aoIntensity; public bool motionBlur;
    }
    public static readonly Level[] Levels =
    {
        new Level { name = "LOW",    scale = 67,  shadowLevel = 0, shadowDistance = 80,  ssr = false, ssgi = false, contact = false, volumetric = false, effects = 0, aoIntensity = 1.2f, motionBlur = false },
        new Level { name = "MEDIUM", scale = 85,  shadowLevel = 1, shadowDistance = 120, ssr = true,  ssgi = false, contact = true,  volumetric = true,  effects = 0, aoIntensity = 1.4f, motionBlur = true },
        new Level { name = "HIGH",   scale = 100, shadowLevel = 2, shadowDistance = 150, ssr = true,  ssgi = true,  contact = true,  volumetric = true,  effects = 1, aoIntensity = 1.5f, motionBlur = true },
        new Level { name = "ULTRA",  scale = 100, shadowLevel = 3, shadowDistance = 180, ssr = true,  ssgi = true,  contact = true,  volumetric = true,  effects = 2, aoIntensity = 1.6f, motionBlur = true },
    };
    public const int DefaultLevel = 2;

    public static int Current { get; private set; } = DefaultLevel;
    public static string Name => Levels[Current].name;
    public static float LastFps { get; private set; }
    static GraphicsQuality inst; static float scale = 100;

    Volume volume; ScreenSpaceReflection ssr; GlobalIllumination gi; ContactShadows contact; Fog fog; HDShadowSettings shadows; ScreenSpaceAmbientOcclusion ao; MotionBlur blur;
    float labelUntil; GUIStyle style; bool fpsLog; float fpsT; int fpsN; float fpsWorst; readonly List<float> frames = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!FindFirstObjectByType<PlayerController>()) return;
        inst = new GameObject("Graphics quality").AddComponent<GraphicsQuality>();
    }

    void Start()
    {
        var vg = new GameObject("Quality volume"); vg.transform.SetParent(transform, false);
        volume = vg.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 80;
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        ssr = p.Add<ScreenSpaceReflection>(true); gi = p.Add<GlobalIllumination>(true); contact = p.Add<ContactShadows>(true);
        fog = p.Add<Fog>(false); fog.active = true; shadows = p.Add<HDShadowSettings>(false); shadows.active = true;
        ao = p.Add<ScreenSpaceAmbientOcclusion>(false); ao.active = true; blur = p.Add<MotionBlur>(false); blur.active = true;
        volume.profile = p;

        DynamicResolutionHandler.SetDynamicResScaler(() => scale, DynamicResScalePolicyType.ReturnsPercentage);
        var cam = Camera.main;
        if (cam && cam.TryGetComponent<HDAdditionalCameraData>(out var cd)) cd.allowDynamicResolution = true;

        int start = PlayerPrefs.GetInt("benghazi_quality", DefaultLevel); var a = System.Environment.GetCommandLineArgs();
        bool autotest = false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "-quality" && i + 1 < a.Length) { int k = Find(a[i + 1]); if (k >= 0) start = k; }
            if (a[i] == "-novsync") { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
            if (a[i] == "-fpslog") fpsLog = true;
            if (a[i] == "-autotest") autotest = true;
        }
        if (autotest && System.Array.IndexOf(a, "-quality") < 0) start = DefaultLevel;       // a test run does not depend on what was played before
        Set(start, false);
    }

    static int Find(string s)
    {
        s = s.ToLowerInvariant();
        for (int i = 0; i < Levels.Length; i++) if (Levels[i].name.ToLowerInvariant() == s) return i;
        return -1;
    }

    public static void Set(int index, bool label = true)
    {
        if (!inst) return;
        index = Mathf.Clamp(index, 0, Levels.Length - 1); Current = index; var l = Levels[index];
        scale = l.scale;
        var i = inst;
        i.ssr.enabled.Override(l.ssr); i.ssr.quality.Override(l.effects);
        i.gi.enable.Override(l.ssgi); i.gi.quality.Override(l.effects); i.gi.fullResolutionSS.Override(l.effects >= 2);
        i.contact.enable.Override(l.contact); i.contact.quality.Override(l.effects);
        i.fog.enableVolumetricFog.Override(l.volumetric);
        i.fog.fogControlMode = FogControl.Balance; i.fog.volumetricFogBudget = l.effects == 0 ? 0.22f : l.effects == 1 ? 0.33f : 0.5f; i.fog.resolutionDepthRatio = 0.6f;
        i.fog.depthExtent.Override(l.effects == 0 ? 64f : 96f);
        i.shadows.maxShadowDistance.Override(l.shadowDistance);
        i.ao.intensity.Override(l.aoIntensity); i.ao.quality.Override(l.effects);
        i.blur.intensity.Override(l.motionBlur ? 0.14f : 0f);
        Light sun = RenderSettings.sun;
        if (!sun) foreach (var x in FindObjectsByType<Light>(FindObjectsInactive.Exclude)) if (x.type == LightType.Directional) { sun = x; break; }
        if (sun && sun.TryGetComponent<HDAdditionalLightData>(out var hd)) { hd.SetShadowResolutionOverride(false); hd.SetShadowResolutionLevel(l.shadowLevel); }
        PlayerPrefs.SetInt("benghazi_quality", index);
        if (label) i.labelUntil = Time.unscaledTime + 3f;
        Debug.Log($"[quality] {l.name}: render scale {l.scale:F0} %, sun shadow level {l.shadowLevel} to {l.shadowDistance:F0} m, SSR {l.ssr}, SSGI {l.ssgi}, contact shadows {l.contact}, volumetric fog {l.volumetric}");
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k != null && k.f8Key.wasPressedThisFrame) Set((Current + 1) % Levels.Length);
        // frame rate: mean, and the mean of the slowest 1 % of frames
        float dt = Time.unscaledDeltaTime; fpsT += dt; fpsN++; frames.Add(dt);
        if (fpsT >= 5f)
        {
            LastFps = fpsN / fpsT;
            if (fpsLog)
            {
                frames.Sort(); int n = Mathf.Max(1, frames.Count / 100); float worst = 0; for (int j = frames.Count - n; j < frames.Count; j++) worst += frames[j];
                Debug.Log($"[fps] {Name} {Screen.width}x{Screen.height} time {TimeOfDay.Name}: mean {LastFps:F1}, 1% low {n / worst:F1}, frames {fpsN}");
            }
            fpsT = 0; fpsN = 0; frames.Clear();
        }
    }

    void OnGUI()
    {
        if (Time.unscaledTime > labelUntil || Event.current.type != EventType.Repaint) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        float a = Mathf.Clamp01(labelUntil - Time.unscaledTime); string s = $"GRAPHICS  ·  {Name}";
        var r = new Rect(0, Screen.height * 0.24f, Screen.width, 40); var keep = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.7f * a); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), s, style);
        GUI.color = new Color(1, 1, 1, a); GUI.Label(r, s, style);
        GUI.color = keep;
    }
}
