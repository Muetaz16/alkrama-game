using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The hour the fight happens at: dawn, noon, afternoon, sunset, midnight.
//   T            next time of day (it changes over a couple of seconds)
//   -time <name> start at dawn | noon | afternoon | sunset | night
//   F11          full screen <-> window
// Each time has its own photographed sky (Resources/Sky, made by the editor's SkySet from Poly Haven HDRIs)
// and sets the sun or the moon to where it stands in that photograph, with its colour and strength, the
// sky light, the haze, and the exposure and tint of the picture. Changing time dims the sky, swaps it and
// brings it back up. Starts itself; nothing in the scene refers to it.
// The game also opens full screen at the monitor's own resolution unless a -screen-... option says otherwise.
//
// HDRP: the sky is an HDRI Sky in a global volume of its own (priority 40), with the fog, the fixed exposure and
// the sky-light multipliers beside it; the sky's light and reflections follow the sky by themselves.
// The numbers in the presets are the URP ones and mean the same: the exposure is fixed so that a camera
// exposure of 0 leaves scene light at 1:1 (ExposureBase), the sun's lux is its old strength x pi (a white
// wall then returns what it did), and a sky "exposure" is a plain multiplier on the photograph.
public class TimeOfDay : MonoBehaviour
{
    public struct Preset
    {
        public string name, clock; public float elevation, bearing;          // sun above the horizon (degrees), bearing added to the scene's own
        public Color sun; public float sunPower, shadow;
        public Color sky; public float skyExposure, skyLight;
        public Color haze; public float hazeDensity;
        public float exposure, saturation; public Color filter;
    }
    public static readonly Preset[] Presets =
    {
        new Preset { name = "DAWN", clock = "05:40", elevation = 7, bearing = 180, sun = new Color(1f, 0.74f, 0.58f), sunPower = 0.75f, shadow = 0.8f,
            sky = new Color(0.5f, 0.47f, 0.56f), skyExposure = 0.55f, skyLight = 0.75f, haze = new Color(0.6f, 0.58f, 0.66f), hazeDensity = 0.0046f, exposure = 0.3f, saturation = -12, filter = new Color(0.96f, 0.96f, 1f) },
        new Preset { name = "NOON", clock = "12:30", elevation = 70, bearing = 90, sun = new Color(1f, 0.975f, 0.93f), sunPower = 1.5f, shadow = 0.93f,
            sky = new Color(0.5f, 0.5f, 0.5f), skyExposure = 0.82f, skyLight = 1.0f, haze = new Color(0.7f, 0.73f, 0.8f), hazeDensity = 0.0028f, exposure = 0.05f, saturation = -4, filter = Color.white },
        new Preset { name = "AFTERNOON", clock = "16:00", elevation = 34, bearing = 0, sun = new Color(1f, 0.91f, 0.78f), sunPower = 1.35f, shadow = 0.92f,
            sky = new Color(0.53f, 0.5f, 0.46f), skyExposure = 0.85f, skyLight = 0.95f, haze = new Color(0.73f, 0.7f, 0.66f), hazeDensity = 0.0032f, exposure = 0.15f, saturation = -6, filter = new Color(1f, 0.99f, 0.97f) },
        new Preset { name = "SUNSET", clock = "18:50", elevation = 5, bearing = 0, sun = new Color(1f, 0.56f, 0.3f), sunPower = 0.95f, shadow = 0.82f,
            sky = new Color(0.6f, 0.43f, 0.33f), skyExposure = 0.72f, skyLight = 0.72f, haze = new Color(0.76f, 0.52f, 0.4f), hazeDensity = 0.0042f, exposure = 0.3f, saturation = -8, filter = new Color(1f, 0.95f, 0.9f) },
        new Preset { name = "MIDNIGHT", clock = "00:10", elevation = 42, bearing = 130, sun = new Color(0.56f, 0.66f, 0.92f), sunPower = 0.42f, shadow = 0.75f,
            sky = new Color(0.07f, 0.09f, 0.16f), skyExposure = 0.6f, skyLight = 2.2f, haze = new Color(0.04f, 0.055f, 0.09f), hazeDensity = 0.0038f, exposure = 0.9f, saturation = -30, filter = new Color(0.8f, 0.88f, 1f) },
    };
    // how bright each photographed sky is shown (the "night" photograph is a moonlit one, far brighter than the game's night: taken well down)
    public static readonly float[] RealSkyExposure = { 0.8f, 0.85f, 0.9f, 1.0f, 0.13f };

    // ---- HDRP scale (see the header)
    public const float ExposureBase = -1.05f;         // EV100 for a preset exposure of 0. Set by eye against the URP build's pictures on the promenade
                                                      // (HdrpDebug.Expo); -0.26, where HDRP's exposure multiplier is exactly 1, came out 1.3 stops under.
    public const float SunLux = Mathf.PI;             // lux per unit of the old directional-light strength
    public const float FogTop = 90f;                  // the haze thins out above the roofs (m)
    // The photograph is shown darker than it lights: at the exposure the ground needs, the sky as it is would be
    // washed out. Its light and reflections are multiplied back, so the scene is lit exactly as before.
    public const float SkyShown = 0.62f;
    // HDRP wraps a lat-long photograph onto its cubemap a quarter turn round from where URP's panoramic skybox
    // showed it. The sky is turned back, so every hour has its sun over the same part of the city as in the URP
    // build (the sunset over the sea). SkyYaw turns the light with it; SkyRotation is the HDRI Sky's own setting.
    public const float SkyYaw = 90f, SkyRotation = 270f;
    public static Vector3 SkyDir(Vector3 inCubemap) => Quaternion.Euler(0, SkyYaw, 0) * inCubemap;
    // The picture's identity: war documentary, shot on location. Every hour is taken a little under, with less
    // colour and more dust in the air than the plain presets (the rest of it is in the editor's CinematicLook).
    public const float DocSaturation = -12f, DocExposure = -0.03f, DocHaze = 1.25f;

    public static int Current { get; private set; } = 2;
    public static string Name => Presets[Current].name;
    static TimeOfDay inst;

    /// <summary>The components one time of day is written into (a volume profile holds them).</summary>
    public class Rig
    {
        public VisualEnvironment env; public HDRISky hdri; public Fog fog; public Exposure expo; public IndirectLightingController ilc; public ColorAdjustments grade;
        public static Rig In(VolumeProfile p)
        {
            // overrides off: only what Write() sets is taken over, the rest (the grade's contrast, say) stays the scene's
            T Get<T>() where T : VolumeComponent { if (!p.TryGet<T>(out var c)) c = p.Add<T>(false); c.active = true; return c; }
            return new Rig { env = Get<VisualEnvironment>(), hdri = Get<HDRISky>(), fog = Get<Fog>(), expo = Get<Exposure>(), ilc = Get<IndirectLightingController>(), grade = Get<ColorAdjustments>() };
        }
    }

    /// <summary>Writes a time of day into a rig. cube = that time's sky (null keeps whatever sky the rig has), dip = 0..1 sky brightness during a change.</summary>
    public static void Write(Rig r, Preset p, Cubemap cube, float skyMul, float dip)
    {
        r.env.skyType.Override((int)SkyType.HDRI); r.env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
        if (cube) r.hdri.hdriSky.Override(cube);
        r.hdri.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
        r.hdri.multiplier.Override(Mathf.Max(0.0001f, skyMul * dip * SkyShown)); r.hdri.exposure.Override(0f); r.hdri.rotation.Override(SkyRotation);
        r.hdri.updateMode.Override(EnvironmentUpdateMode.OnChanged);

        // haze. URP's fog was exp(-(d z)^2); HDRP's is exp(-z / meanFreePath). They agree at 150 m with meanFreePath = 1 / (d^2 * 150).
        r.fog.enabled.Override(true);
        // the distance fades into the colour of the sky behind it (URP left the sky clear of fog; a constant haze
        // colour here would lie over the sky as well and wash it out), warmed a little towards the hour's dust
        float hz = Mathf.Max(p.haze.r, p.haze.g, p.haze.b, 0.001f);
        r.fog.colorMode.Override(FogColorMode.SkyColor); r.fog.color.Override(p.haze);
        r.fog.tint.Override(Color.Lerp(Color.white, new Color(p.haze.r / hz, p.haze.g / hz, p.haze.b / hz), 0.45f));
        r.fog.mipFogNear.Override(0f); r.fog.mipFogFar.Override(600f); r.fog.mipFogMaxMip.Override(0.6f);
        // (matched at 300 m in fact, x 1.6: an exponential haze is far heavier close by than URP's squared one, and
        // close by is where the fight is - the street must stay crisp, the far blocks fade)
        float dens = p.hazeDensity * DocHaze;
        r.fog.meanFreePath.Override(Mathf.Clamp(1.6f / (dens * dens * 300f), 60f, 6000f));
        r.fog.baseHeight.Override(-2f); r.fog.maximumHeight.Override(FogTop); r.fog.maxFogDistance.Override(700f);
        float hm = Mathf.Max(p.haze.r, p.haze.g, p.haze.b, 0.001f);
        r.fog.albedo.Override(new Color(p.haze.r / hm, p.haze.g / hm, p.haze.b / hm) * 0.9f);       // what the sun shafts are made of (volumetric fog)
        r.fog.anisotropy.Override(0.6f); r.fog.globalLightProbeDimmer.Override(0.35f);       // sun shafts, not a milky veil of sky light

        r.expo.mode.Override(ExposureMode.Fixed); r.expo.fixedExposure.Override(ExposureBase - p.exposure - DocExposure);
        Rp.FxExposure(ExposureBase - p.exposure - DocExposure);          // the unlit effects follow the exposure (Benghazi/Fx)
        r.ilc.indirectDiffuseLightingMultiplier.Override(p.skyLight / SkyShown); r.ilc.reflectionLightingMultiplier.Override(Mathf.Clamp01(p.skyLight) / SkyShown * 0.45f);     // steel mirrors the sky: at this exposure a full reflection turns the rifle silver
        r.grade.saturation.Override(p.saturation + DocSaturation); r.grade.colorFilter.Override(p.filter);
    }

    /// <summary>Sun (or moon) for a time of day.</summary>
    public static void WriteSun(Light sun, Preset p)
    {
        sun.color = p.sun; sun.intensity = p.sunPower * SunLux;
        if (sun.TryGetComponent<HDAdditionalLightData>(out var hd)) hd.shadowDimmer = p.shadow;
    }

    Light sun; float baseBearing; Volume volume; Rig rig;
    Preset from, now, to; float blend = 1, labelUntil; GUIStyle style;
    readonly Cubemap[] skies = new Cubemap[5]; readonly Vector3[] lightDir = new Vector3[5]; readonly bool[] hasDir = new bool[5];
    Vector3 dirFrom, dirTo; int skyShown = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void FullScreen()
    {
        string cl = System.Environment.CommandLine;
        if (Application.isEditor || cl.Contains("-screen-") || cl.Contains("-autotest") || cl.Contains("-batchmode")) return;
        // a window size left behind by an earlier windowed run would otherwise be used again
        Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!FindFirstObjectByType<PlayerController>()) return;
        inst = new GameObject("Time of day").AddComponent<TimeOfDay>();
    }

    void Start()
    {
        sun = RenderSettings.sun;
        if (!sun) foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude)) if (l.type == LightType.Directional) { sun = l; break; }
        if (!sun) { enabled = false; return; }
        var f = sun.transform.forward; baseBearing = Mathf.Atan2(-f.x, -f.z) * Mathf.Rad2Deg;      // where the scene's own sun stands
        var vg = new GameObject("Time volume"); vg.transform.SetParent(transform, false);
        volume = vg.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 40;
        var prof = ScriptableObject.CreateInstance<VolumeProfile>(); rig = Rig.In(prof); volume.profile = prof;

        // the photographed skies, and where the light is in each
        var info = Resources.Load<TextAsset>("Sky/sky_info");
        for (int i = 0; i < Presets.Length; i++)
        {
            var sp = Resources.Load<VolumeProfile>("Sky/SkyVol_" + Presets[i].name);
            if (sp && sp.TryGet<HDRISky>(out var hs)) skies[i] = hs.hdriSky.value as Cubemap;
        }
        if (info)
            foreach (var line in info.text.Split('\n'))
            {
                var c = line.Trim().Split(';'); if (c.Length < 4) continue;
                int k = Find(c[0]); if (k < 0) continue;
                float P(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                var d = new Vector3(P(c[1]), P(c[2]), P(c[3]));
                if (d.y < 0.1f) { d.y = 0.1f; d.Normalize(); }                 // never from below the roofs: shadows would cover everything
                lightDir[k] = SkyDir(d.normalized); hasDir[k] = true;
            }
        int start = 2; var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == "-time") { int k = Find(a[i + 1]); if (k >= 0) start = k; }
        Current = start; now = to = from = Presets[start]; dirFrom = dirTo = Dir(start); skyShown = start; Apply(now);
    }

    static int Find(string s)
    {
        s = s.ToLowerInvariant(); if (s == "night") s = "midnight";
        for (int i = 0; i < Presets.Length; i++) if (Presets[i].name.ToLowerInvariant() == s) return i;
        return -1;
    }

    /// <summary>Go to a time of day (0 dawn .. 4 midnight).</summary>
    public static void Set(int index, bool instant = false)
    {
        if (!inst || !inst.enabled) return;
        index = ((index % Presets.Length) + Presets.Length) % Presets.Length;
        inst.dirFrom = inst.CurrentDir(); inst.dirTo = inst.Dir(index);
        Current = index; inst.from = inst.now; inst.to = Presets[index]; inst.blend = instant ? 1 : 0; inst.labelUntil = Time.time + 3f;
        if (instant) { inst.now = inst.to; inst.skyShown = index; inst.Apply(inst.now); }
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k != null && k.f11Key.wasPressedThisFrame)
        {
            if (Screen.fullScreen) Screen.SetResolution(1600, 900, FullScreenMode.Windowed);
            else Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow);
        }
        var wc = FindPlayerWeapon();
        if (k != null && k.tKey.wasPressedThisFrame && !(wc && wc.externalInput)) Set(Current + 1);
        if (blend < 1)
        {
            blend = Mathf.Min(1, blend + Time.deltaTime / 2.5f);
            if (blend >= 0.5f && skyShown != Current) skyShown = Current;
            now = Mix(from, to, Mathf.SmoothStep(0, 1, blend)); Apply(now);
        }
    }
    WeaponController weapon;
    WeaponController FindPlayerWeapon() { if (!weapon) weapon = FindFirstObjectByType<WeaponController>(); return weapon; }

    static Preset Mix(Preset a, Preset b, float t) => new Preset
    {
        name = b.name, clock = b.clock, elevation = Mathf.Lerp(a.elevation, b.elevation, t), bearing = Mathf.LerpAngle(a.bearing, b.bearing, t),
        sun = Color.Lerp(a.sun, b.sun, t), sunPower = Mathf.Lerp(a.sunPower, b.sunPower, t), shadow = Mathf.Lerp(a.shadow, b.shadow, t),
        sky = Color.Lerp(a.sky, b.sky, t), skyExposure = Mathf.Lerp(a.skyExposure, b.skyExposure, t), skyLight = Mathf.Lerp(a.skyLight, b.skyLight, t),
        haze = Color.Lerp(a.haze, b.haze, t), hazeDensity = Mathf.Lerp(a.hazeDensity, b.hazeDensity, t),
        exposure = Mathf.Lerp(a.exposure, b.exposure, t), saturation = Mathf.Lerp(a.saturation, b.saturation, t), filter = Color.Lerp(a.filter, b.filter, t)
    };

    // where the light comes from at a time of day: where it is in that time's photograph, or the preset's angles
    Vector3 Dir(int i)
    {
        if (hasDir[i] && skies[i] && i != 0) return lightDir[i];        // the dawn sky is mist with no sun in it: its brightest patch is overhead, so dawn keeps its own low sun
        float e = Presets[i].elevation * Mathf.Deg2Rad, b = (baseBearing + Presets[i].bearing) * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(b) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(b) * Mathf.Cos(e));
    }
    Vector3 CurrentDir() => sun ? -sun.transform.forward : Vector3.up;

    void Apply(Preset p)
    {
        var toSun = Vector3.Slerp(dirFrom, dirTo, Mathf.SmoothStep(0, 1, blend)).normalized;
        sun.transform.rotation = Quaternion.LookRotation(-toSun);
        WriteSun(sun, p);
        float dip = blend < 1 ? Mathf.Lerp(0.12f, 1f, Mathf.Abs(2 * blend - 1)) : 1f;        // down, swap, up
        var cube = skyShown >= 0 ? skies[skyShown] : null;
        Write(rig, p, cube, cube ? RealSkyExposure[skyShown] : p.skyExposure, dip);
    }

    void OnGUI()
    {
        if (Time.time > labelUntil || Event.current.type != EventType.Repaint) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        var p = Presets[Current]; float a = Mathf.Clamp01(labelUntil - Time.time);
        var r = new Rect(0, Screen.height * 0.16f, Screen.width, 50); var keep = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.7f * a); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), $"{p.name}  ·  {p.clock}", style);
        GUI.color = new Color(1, 1, 1, a); GUI.Label(r, $"{p.name}  ·  {p.clock}", style);
        GUI.color = keep;
    }
}
