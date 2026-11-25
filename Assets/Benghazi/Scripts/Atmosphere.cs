using UnityEngine;

// The air of the city after the fighting, all made in code at start (no textures or effects in the project):
//   dust      fine motes hanging in the light round the player, drifting with the wind
//   gusts     now and then a low cloud of sand rolls along the street and thins out
//   smoke     slow black columns over burning ruins across the city (positions from CityDetail), bending with
//             the wind off the sea, with a dull glow of fire at the foot of each one
// Cheap: three particle systems and one per column, a few hundred particles in all, no lights.
public class Atmosphere : MonoBehaviour
{
    public Vector3[] smoke = new Vector3[0];
    [Range(0, 2)] public float dust = 1f;
    public Vector3 wind = new Vector3(0.9f, 0f, 0.3f);           // m/s, the same drift as CombatFx's smoke, a little stronger up high

    ParticleSystem motes, gusts;
    Transform cam;
    float nextGust;
    Material softMat, smokeMat, glowMat;

    void Start()
    {
        var shader = Shader.Find("Sprites/Default");
        if (!shader) { enabled = false; return; }
        var soft = Soft(64); var puff = Puff(128);
        softMat = new Material(shader) { mainTexture = soft };
        smokeMat = new Material(shader) { mainTexture = puff };
        glowMat = new Material(shader) { mainTexture = soft }; glowMat.SetColor("_Color", new Color(3.2f, 1.5f, 0.5f, 1f));

        // dust motes: tiny, pale, in a box that follows the camera
        motes = Make("Dust motes", softMat, 700);
        var m = motes.main; m.startLifetime = new ParticleSystem.MinMaxCurve(6f, 11f); m.startSpeed = 0.05f;
        m.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.035f); m.startColor = new Color(1f, 0.93f, 0.8f, 0.55f);
        var em = motes.emission; em.enabled = true; em.rateOverTime = 70f * dust;
        var sh = motes.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(22f, 7f, 22f);
        var vel = motes.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
        vel.x = wind.x * 0.4f; vel.y = 0.02f; vel.z = wind.z * 0.4f;
        var nz = motes.noise; nz.enabled = true; nz.strength = 0.15f; nz.frequency = 0.4f; nz.scrollSpeed = 0.2f;
        var col = motes.colorOverLifetime; col.enabled = true; col.color = Fade(new Color(1f, 0.95f, 0.85f), 0.15f, 0.85f);
        motes.Play();

        // sand gusts: emitted in bursts from Update
        gusts = Make("Sand gusts", softMat, 160);
        var g = gusts.main; g.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
        var gs = gusts.sizeOverLifetime; gs.enabled = true; gs.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.6f, 1, 1.6f));
        var gc = gusts.colorOverLifetime; gc.enabled = true; gc.color = Fade(new Color(0.8f, 0.7f, 0.54f), 0.25f, 0.6f);
        gusts.Play();

        // smoke columns over the burning ruins
        if (smoke != null)
            foreach (var s in smoke) Column(s);
        nextGust = Time.time + 2f;
    }

    void LateUpdate()
    {
        if (!cam) { var c = Camera.main; if (!c) return; cam = c.transform; }
        motes.transform.position = cam.position;
        if (Time.time >= nextGust && dust > 0)
        {
            nextGust = Time.time + Random.Range(2.5f, 7f) / dust;
            var flat = Random.insideUnitCircle.normalized * Random.Range(8f, 32f);
            var at = cam.position + new Vector3(flat.x, 0, flat.y);
            if (Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore) && hit.point.y < 1f && hit.point.y > -0.15f)
            {
                var ep = new ParticleSystem.EmitParams();
                var w = wind.normalized * Random.Range(2.5f, 4.5f);
                for (int i = 0, n = Random.Range(6, 11); i < n; i++)
                {
                    ep.position = hit.point + new Vector3(Random.Range(-2.5f, 2.5f), Random.Range(0.2f, 0.9f), Random.Range(-2.5f, 2.5f));
                    ep.velocity = w + new Vector3(Random.Range(-0.4f, 0.4f), Random.Range(0.05f, 0.35f), Random.Range(-0.4f, 0.4f));
                    ep.startSize = Random.Range(2f, 5.5f);
                    ep.startColor = new Color(1, 1, 1, Random.Range(0.12f, 0.22f));
                    ep.rotation = Random.Range(0f, 360f);
                    gusts.Emit(ep, 1);
                }
            }
        }
    }

    void Column(Vector3 top)
    {
        var ps = Make("Smoke column", smokeMat, 140);
        ps.transform.position = top;
        var m = ps.main; m.startLifetime = new ParticleSystem.MinMaxCurve(18f, 26f); m.startSpeed = 0f;
        m.startSize = new ParticleSystem.MinMaxCurve(4f, 7f); m.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2);
        m.startColor = new ParticleSystem.MinMaxGradient(new Color(0.16f, 0.15f, 0.14f, 1f), new Color(0.26f, 0.24f, 0.22f, 1f));
        var em = ps.emission; em.enabled = true; em.rateOverTime = 4.5f;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 2.5f;
        var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
        vel.x = new ParticleSystem.MinMaxCurve(wind.x * 1.6f, AnimationCurve.Linear(0, 0.3f, 1, 1f));
        vel.y = new ParticleSystem.MinMaxCurve(2.4f, AnimationCurve.Linear(0, 1f, 1, 0.45f));
        vel.z = new ParticleSystem.MinMaxCurve(wind.z * 1.6f, AnimationCurve.Linear(0, 0.3f, 1, 1f));
        var size = ps.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0, 0.7f, 1, 4.2f));
        var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
        var col = ps.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(new[] { new GradientColorKey(new Color(0.55f, 0.5f, 0.45f), 0f), new GradientColorKey(Color.white, 0.25f), new GradientColorKey(new Color(1.5f, 1.45f, 1.4f), 1f) },
                     new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.06f), new GradientAlphaKey(0.55f, 0.45f), new GradientAlphaKey(0f, 1f) });
        col.color = grad;
        var r = ps.GetComponent<ParticleSystemRenderer>(); r.maxParticleSize = 4f; r.sortMode = ParticleSystemSortMode.YoungestInFront;
        ps.Simulate(20f, true, true);                    // already burning when the game starts
        ps.Play();

        // the fire at its foot: a few hot, short-lived blobs
        var fire = Make("Fire glow", glowMat, 30);
        fire.transform.position = top + Vector3.down * 0.5f;
        var fm = fire.main; fm.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f); fm.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
        fm.startSize = new ParticleSystem.MinMaxCurve(1.2f, 3f);
        var fe = fire.emission; fe.enabled = true; fe.rateOverTime = 12f;
        var fs = fire.shape; fs.enabled = true; fs.shapeType = ParticleSystemShapeType.Hemisphere; fs.radius = 1.8f; fs.rotation = new Vector3(-90, 0, 0);
        var fc = fire.colorOverLifetime; fc.enabled = true; fc.color = Fade(Color.white, 0.2f, 0.5f);
        fire.Play();
    }

    ParticleSystem Make(string name, Material mat, int max)
    {
        var go = new GameObject(name); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var m = ps.main; m.loop = true; m.playOnAwake = false; m.maxParticles = max; m.simulationSpace = ParticleSystemSimulationSpace.World;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return ps;
    }

    static ParticleSystem.MinMaxGradient Fade(Color c, float inAt, float outFrom)
    {
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, inAt), new GradientAlphaKey(1f, outFrom), new GradientAlphaKey(0f, 1f) });
        return g;
    }

    static Texture2D Soft(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - r); a = a * a * (3f - 2f * a);
            px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        t.SetPixels32(px); t.Apply(true, true); return t;
    }

    // a billowing puff: several octaves of noise inside a soft circle, lit a little from above
    static Texture2D Puff(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n, v = (y + 0.5f) / n, dx = u * 2 - 1, dy = v * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
            float fbm = 0, amp = 0.5f, fr = 3f;
            for (int o = 0; o < 4; o++) { fbm += amp * Mathf.PerlinNoise(u * fr + 11.3f * o, v * fr + 7.1f * o); amp *= 0.5f; fr *= 2.1f; }
            float a = Mathf.Clamp01((1f - r) * 1.6f) * Mathf.Clamp01(fbm * 1.9f - 0.35f);
            float shade = Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(v * 0.8f + fbm * 0.4f));
            byte c = (byte)(shade * 255f);
            px[y * n + x] = new Color32(c, c, c, (byte)(Mathf.Clamp01(a) * 255f));
        }
        t.SetPixels32(px); t.Apply(true, true); return t;
    }
}
