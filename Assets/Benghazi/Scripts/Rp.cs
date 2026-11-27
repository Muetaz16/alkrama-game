using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Render-pipeline helper for materials made in code while the game runs (HDRP).
// A player only contains the shader variants some material in the build uses, so every material made at run
// time is a copy of a template in Resources/Hdrp (written by the editor's HdrpSetup.Templates): the variant is
// there for certain, and no keyword has to be worked out in the player.
//   Lit        plain colour / colour texture
//   LitPbr     colour + normal map + mask map (R metal, G occlusion, B detail, A smoothness)
//   LitCutout  colour texture cut by its alpha, two-sided
//   Decal      HDRP decal projected onto whatever is behind it (bullet holes, cracks, blood)
// Flashes, tracers, smoke and other unlit effects stay on "Sprites/Default": HDRP draws it as an unlit pass and
// it is not multiplied by the camera exposure, which is what a flash or a puff of dust wants.
public static class Rp
{
    static Material tLit, tPbr, tCut, tDecal, tFx; static Shader fx;
    static readonly int FxGainId = Shader.PropertyToID("_BenghaziFxGain");

    /// <summary>The unlit effect shader (Benghazi/Fx: Sprites/Default that follows the camera exposure).</summary>
    public static Shader Fx
    {
        get
        {
            if (!fx) { var t = T(ref tFx, "Fx"); fx = t ? t.shader : null; }
            if (!fx) fx = Shader.Find("Benghazi/Fx");
            if (!fx) fx = Shader.Find("Sprites/Default");
            return fx;
        }
    }
    /// <summary>Called when the exposure changes (TimeOfDay): fixed exposure in EV100.</summary>
    public static void FxExposure(float ev100) => Shader.SetGlobalFloat(FxGainId, 1f / (1.2f * Mathf.Pow(2f, ev100)));
    static Material T(ref Material slot, string name)
    {
        if (!slot) slot = Resources.Load<Material>("Hdrp/" + name);
        if (!slot) Debug.LogError("[rp] missing template Resources/Hdrp/" + name + " - run HdrpSetup.Run");
        return slot;
    }

    public static Material Lit(string name, Color c, float metal = 0f, float smooth = 0.3f, Texture tex = null)
    {
        var t = T(ref tLit, "Lit"); var m = t ? new Material(t) : new Material(Shader.Find("HDRP/Lit"));
        m.name = name; m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
        if (tex) m.SetTexture("_BaseColorMap", tex);
        return m;
    }

    /// <summary>ms: R = metal, A = smoothness (G and B are not read: occlusion is held at 1).</summary>
    public static Material Pbr(string name, Texture albedo, Texture normal, Texture ms, Color tint, float smoothness, float bump)
    {
        var t = T(ref tPbr, "LitPbr"); var m = t ? new Material(t) : new Material(Shader.Find("HDRP/Lit"));
        m.name = name; m.SetColor("_BaseColor", tint); m.SetTexture("_BaseColorMap", albedo);
        m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalScale", bump);
        m.SetTexture("_MaskMap", ms);
        m.SetFloat("_MetallicRemapMin", 0); m.SetFloat("_MetallicRemapMax", 1);
        m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", Mathf.Clamp01(smoothness));
        m.SetFloat("_AORemapMin", 1); m.SetFloat("_AORemapMax", 1);
        return m;
    }

    public static Material Cutout(string name, Color c, float metal, float smooth, Texture tex, float cutoff)
    {
        var t = T(ref tCut, "LitCutout"); var m = t ? new Material(t) : new Material(Shader.Find("HDRP/Lit"));
        m.name = name; m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
        m.SetTexture("_BaseColorMap", tex); m.SetFloat("_AlphaCutoff", cutoff); m.SetFloat("_Cutoff", cutoff);
        return m;
    }

    public static void Color(Material m, Color c) { if (m) m.SetColor("_BaseColor", c); }

    // ---------------------------------------------------------------- decals
    public static bool DecalsReady => T(ref tDecal, "Decal");

    /// <summary>A decal material: the texture's colour laid over the surface by its alpha.</summary>
    public static Material Decal(string name, Texture tex, Color tint, float smoothness = 0.1f)
    {
        var t = T(ref tDecal, "Decal"); if (!t) return null;
        var m = new Material(t) { name = name };
        m.SetTexture("_BaseColorMap", tex); m.SetColor("_BaseColor", tint); m.SetFloat("_DecalBlend", 1f);
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    /// <summary>Lay a decal on a surface: at the point, facing into it, size in metres. Reuses <paramref name="reuse"/> if given.</summary>
    public static DecalProjector Project(Material m, Vector3 point, Vector3 normal, float size, float depth = 0.25f, float spin = 0f, Transform parent = null, DecalProjector reuse = null)
    {
        var dp = reuse;
        if (!dp)
        {
            var go = new GameObject("Decal") { layer = 2 };
            dp = go.AddComponent<DecalProjector>();
        }
        dp.material = m;
        var t = dp.transform; t.SetParent(parent, true);
        t.SetPositionAndRotation(point + normal * (depth * 0.5f), Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, spin));
        dp.size = new Vector3(size, size, depth); dp.pivot = new Vector3(0, 0, depth * 0.5f);
        dp.fadeFactor = 1f; dp.drawDistance = 120f; dp.fadeScale = 0.85f;
        dp.gameObject.SetActive(true);
        return dp;
    }
}
