using System.Collections.Generic;
using UnityEngine;

// What a round does to a parked car.
//   body panel  a clean punched hole: dark through-hole, a ring of bare bright steel where the paint is
//               knocked off, chipped paint round it. It sits on the real body surface (the cars carry a
//               mesh collider for bullets - see CarColliders in the editor scripts), and stays on the car.
//   glass       holed first, broken after: the first round punches a hole with a tight web of cracks round
//               it; the second crazes the pane right across; the third brings it down - the pane falls out
//               in a shower of fragments and leaves a jagged opening that later rounds simply pass through.
//               Every stage is heard.
// It makes no difference whose round it is: the player's and the enemies' go through the same code.
// Which is which: the same rule the car textures were painted with (RealCars) - the upper part of the body
// that is not roof is glass. Burnt-out cars have no glass left.
// Everything here is built in code and pooled; nothing in the scene refers to it.
public static class CarDamage
{
    public const int None = 0, Body = 1, Glass = 2, Tyre = 3;
    public static int TyresBurst { get; private set; }
    public static int BuildingGlassHits { get; private set; }
    public static int BodyHoles { get; private set; }
    public static int GlassHits { get; private set; }
    public static int PanesBroken { get; private set; }      // panes that have fallen out altogether

    const int MaxHoles = 220, MaxCracks = 60;
    static readonly Queue<Transform> holes = new(), cracks = new();
    static Material holeMat, crackMat, shardMat, brokenMat; static Mesh quad;
    static ParticleSystem shards; static AudioClip[] glassClips; static AudioClip tyreClip; static AudioSource[] voices; static int voice;
    static readonly Dictionary<Collider, Info> cars = new();
    internal class Pane { public Vector3 at, normal; public int hits; public bool gone; public readonly List<Transform> marks = new(); }
    internal class Info
    {
        public bool car, burnt; public Bounds local; public readonly List<Pane> panes = new();
        public readonly bool[] tyres = new bool[4]; public bool posed; public Transform carT; public Vector3 basePos; public Quaternion baseRot;
        public float roll, pitch, sink, curRoll, curPitch, curSink;
    }
    const float PaneReach = 0.5f;          // hits this close together on the same face are on one pane

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { holes.Clear(); cracks.Clear(); cars.Clear(); holeMat = crackMat = shardMat = null; quad = null; shards = null; glassClips = null; voices = null; BodyHoles = GlassHits = PanesBroken = TyresBurst = BuildingGlassHits = 0; settling.Clear(); tyreClip = null; brokenMat = null; }

    static Info Look(Collider c)
    {
        if (cars.TryGetValue(c, out var i)) return i;
        i = new Info();
        var t = c.transform; var holder = t.parent ? t.parent.parent : null;
        if (c is MeshCollider mc && mc.sharedMesh && c.name == "Body collider" && holder && holder.name == "Cars")
        {
            i.car = true; i.local = mc.sharedMesh.bounds; i.burnt = t.parent.name.Contains("burnt");
        }
        cars[c] = i; return i;
    }

    /// <summary>Is this collider a car, and which part of it is this point on?</summary>
    public static int Classify(RaycastHit h)
    {
        var i = Look(h.collider); if (!i.car) return None;
        var t = h.collider.transform;
        var lp = t.InverseTransformPoint(h.point); var ln = t.InverseTransformDirection(h.normal);
        float hy = Mathf.InverseLerp(i.local.min.y, i.local.max.y, lp.y);
        // the same shape rules the car textures were painted with (RealCars.PaintableArea)
        float ax = Mathf.Abs(lp.x - i.local.center.x) / (i.local.size.x * 0.5f), az = Mathf.Abs(lp.z - i.local.center.z) / (i.local.size.z * 0.5f);
        if (hy < 0.38f && ax > 0.5f && az > 0.38f && az < 0.85f) return Tyre;
        return !i.burnt && hy > 0.64f && Mathf.Abs(ln.y) < 0.7f ? Glass : Body;
    }

    // ---------- building windows ----------
    // The windows of the buildings are painted on the facades. LookUpgrade worked a glass mask out of each
    // facade picture (red channel of the material's metallic map); GlassSetup makes those masks readable.
    // A round that lands where the mask says glass breaks the pane like any other.
    public static string LastBuildingProbe = "";
    public static bool BuildingGlassAt(RaycastHit h)
    {
        if (!(h.collider is MeshCollider mc) || !mc.sharedMesh) { LastBuildingProbe = "not a mesh collider"; return false; }
        var r = h.collider.GetComponent<MeshRenderer>(); if (!r) { LastBuildingProbe = "no renderer"; return false; }
        var mesh = mc.sharedMesh; int sub = 0, idx = h.triangleIndex * 3;
        for (int s = 0; s < mesh.subMeshCount; s++) { var sm = mesh.GetSubMesh(s); if (idx >= sm.indexStart && idx < sm.indexStart + sm.indexCount) { sub = s; break; } }
        tmpMats.Clear(); r.GetSharedMaterials(tmpMats);
        var mat = sub < tmpMats.Count ? tmpMats[sub] : null; tmpMats.Clear();
        if (!mat || !mat.name.StartsWith("Facade") || !mat.HasProperty("_MetallicGlossMap")) { LastBuildingProbe = "not a facade"; return false; }
        var mask = mat.GetTexture("_MetallicGlossMap") as Texture2D;
        if (!mask) { LastBuildingProbe = "facade without a glass mask"; return false; }
        if (!mask.isReadable) { LastBuildingProbe = "glass mask not readable (run GlassSetup.Run)"; return false; }
        if (!mesh.isReadable) { LastBuildingProbe = "building mesh not readable"; return false; }
        var uv = Vector2.Scale(h.textureCoord, mat.GetTextureScale("_BaseMap")) + mat.GetTextureOffset("_BaseMap");
        bool glass = mask.GetPixelBilinear(uv.x, uv.y).r > 0.3f;
        LastBuildingProbe = glass ? "glass" : "wall";
        return glass;
    }
    static readonly List<Material> tmpMats = new();

    /// <summary>A round hit something: a car is holed / its glass or tyre goes, a building window breaks. Returns None / Body / Glass / Tyre.</summary>
    public static int Hit(RaycastHit h, Vector3 dir, bool heavy)
    {
        int kind = Classify(h);
        if (kind == None)
        {
            if (!BuildingGlassAt(h)) return None;
            Build(); BuildingGlassHits++;
            GlassHit(h, dir, heavy, 1.7f);
            return Glass;
        }
        Build();
        var car = h.collider.transform; var n = h.normal;
        if (kind == Body)
        {
            var q = Take(holes, MaxHoles, holeMat, "Bullet hole");
            q.SetParent(null, false);
            q.SetPositionAndRotation(h.point + n * 0.004f, Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)));
            q.localScale = Vector3.one * Random.Range(0.085f, 0.105f) * (heavy ? 1.4f : 1f);     // a touch larger than life so it reads from across a street
            q.SetParent(car, true); q.gameObject.SetActive(true);
            BodyHoles++;
        }
        else if (kind == Tyre) TyreHit(h, dir);
        else GlassHit(h, dir, heavy, 1f);
        return kind;
    }

    // ---------- tyres ----------
    // A round through a tyre: it goes with a bang and a rush of air, dust jumps from the road, and that
    // corner of the car drops onto its rim over a third of a second. Each tyre goes once.
    static void TyreHit(RaycastHit h, Vector3 dir)
    {
        var i = Look(h.collider); var body = h.collider.transform; var car = body.parent;
        var lp = body.InverseTransformPoint(h.point);
        int right = lp.x > i.local.center.x ? 1 : 0, front = lp.z > i.local.center.z ? 1 : 0, idx = right + front * 2;
        if (i.tyres[idx]) return;                       // already flat: the round just goes into rubber
        i.tyres[idx] = true; TyresBurst++;
        if (!i.posed) { i.posed = true; i.basePos = car.localPosition; i.baseRot = car.localRotation; }
        if (!i.burnt)
        {
            i.roll += right == 1 ? -2.3f : 2.3f; i.pitch += front == 1 ? 1.4f : -1.4f; i.sink += 0.028f;
            if (!settling.Contains(i)) { i.carT = car; settling.Add(i); }
            Runner();
        }
        // dust blown out from under the wheel
        if (shards)
        {
            var ep = new ParticleSystem.EmitParams { startColor = new Color(0.5f, 0.47f, 0.42f, 0.7f) };
            for (int k = 0; k < 26; k++)
            {
                ep.position = h.point + Vector3.down * Random.Range(0.05f, 0.25f) + Random.insideUnitSphere * 0.12f;
                ep.velocity = (h.normal + Random.insideUnitSphere * 0.9f) * Random.Range(1.5f, 4.5f) + Vector3.up * Random.Range(0.2f, 1.2f);
                ep.startSize = Random.Range(0.04f, 0.12f); ep.startLifetime = Random.Range(0.4f, 0.9f);
                shards.Emit(ep, 1);
            }
        }
        if (voices != null && tyreClip) { var v = voices[voice = (voice + 1) % voices.Length]; if (v) { v.transform.position = h.point; v.pitch = Random.Range(0.92f, 1.08f); v.PlayOneShot(tyreClip, 1f); } }
    }

    static readonly List<Info> settling = new(); static CarDamageRunner runner;
    static void Runner() { if (!runner) { var go = new GameObject("Car damage"); Object.DontDestroyOnLoad(go); runner = go.AddComponent<CarDamageRunner>(); } }
    internal static void Settle(float dt)
    {
        for (int k = settling.Count - 1; k >= 0; k--)
        {
            var i = settling[k]; if (!i.carT) { settling.RemoveAt(k); continue; }
            float e = 1 - Mathf.Exp(-9f * dt);
            i.curRoll = Mathf.Lerp(i.curRoll, i.roll, e); i.curPitch = Mathf.Lerp(i.curPitch, i.pitch, e); i.curSink = Mathf.Lerp(i.curSink, i.sink, e);
            i.carT.localRotation = i.baseRot * Quaternion.Euler(i.curPitch, 0, i.curRoll);
            i.carT.localPosition = i.basePos + Vector3.down * i.curSink;
            if (Mathf.Abs(i.curRoll - i.roll) < 0.01f && Mathf.Abs(i.curPitch - i.pitch) < 0.01f && Mathf.Abs(i.curSink - i.sink) < 0.0005f) settling.RemoveAt(k);
        }
    }

    // ---------- glass: holed first, broken after ----------
    static void GlassHit(RaycastHit h, Vector3 dir, bool heavy, float size)
    {
        var owner = h.collider.transform; var n = h.normal;
        var i = Look(h.collider);
        Vector3 lp = owner.InverseTransformPoint(h.point), ln = owner.InverseTransformDirection(n).normalized;
        float reach = PaneReach * size / Mathf.Max(0.05f, owner.lossyScale.x);
        Pane pane = null;
        foreach (var pn in i.panes) if (Vector3.Dot(pn.normal, ln) > 0.6f && Vector3.Distance(pn.at, lp) < reach) { pane = pn; break; }
        if (pane == null) { pane = new Pane { at = lp, normal = ln }; i.panes.Add(pane); }
        if (pane.gone) return;                             // nothing left in that opening: the round goes through it
        pane.hits++; GlassHits++;
        if (pane.hits < 3)
        {
            // 1: a hole and a tight web of cracks.  2: the first web runs right across the pane, a second hole beside it
            foreach (var m in pane.marks) if (m && m.gameObject.activeSelf) m.localScale *= 1.7f;
            var q = Take(cracks, MaxCracks, crackMat, "Glass crack");
            q.GetComponent<MeshRenderer>().sharedMaterial = crackMat;
            q.SetParent(null, false);
            q.SetPositionAndRotation(h.point + n * 0.006f, Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)));
            q.localScale = Vector3.one * (pane.hits == 1 ? Random.Range(0.3f, 0.36f) : Random.Range(0.42f, 0.52f)) * (heavy ? 1.3f : 1f) * size;
            q.SetParent(owner, true); q.gameObject.SetActive(true);
            pane.marks.Add(q);
            Shards(h.point, n, dir, pane.hits == 1 ? 12 : 24, 1f);
            Sound(h.point, pane.hits == 1 ? 0.55f : 0.8f, 1f);
            if (heavy) pane.hits = 2;                      // a heavy round does two rounds' work
        }
        else
        {
            // 3: the pane lets go
            pane.gone = true; PanesBroken++;
            Vector3 centre = Vector3.zero; int cnt = 0;
            foreach (var m in pane.marks) if (m) { if (m.gameObject.activeSelf) { centre += m.position; cnt++; } m.gameObject.SetActive(false); }
            centre = cnt > 0 ? (centre + h.point) / (cnt + 1) : h.point;
            pane.marks.Clear();
            var q = Take(cracks, MaxCracks, brokenMat, "Broken pane");
            q.GetComponent<MeshRenderer>().sharedMaterial = brokenMat;
            q.SetParent(null, false);
            q.SetPositionAndRotation(centre + n * 0.007f, Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)));
            q.localScale = Vector3.one * Random.Range(0.46f, 0.54f) * size;        // kept inside the opening: a flat mark cannot follow the frame
            q.SetParent(owner, true); q.gameObject.SetActive(true);
            Shards(centre, n, dir, size > 1.2f ? 140 : 95, 2.2f * size);
            Sound(centre, 1f, size > 1.2f ? 0.78f : 0.86f);
        }
    }

    static Transform Take(Queue<Transform> pool, int max, Material m, string name)
    {
        Transform t = null;
        while (pool.Count >= max) { t = pool.Dequeue(); if (t) break; }
        if (!t)
        {
            var go = new GameObject(name); go.layer = 2;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            t = go.transform;
        }
        pool.Enqueue(t); return t;
    }

    static void Sound(Vector3 at, float volume, float pitch)
    {
        if (voices == null || glassClips == null) return;
        var v = voices[voice = (voice + 1) % voices.Length]; if (!v) return;
        v.transform.position = at; v.pitch = pitch * Random.Range(0.94f, 1.08f);
        v.PlayOneShot(glassClips[Random.Range(0, glassClips.Length)], volume);
    }

    // fragments: most go on with the round out of the far side, some spill back out of the near side
    static void Shards(Vector3 at, Vector3 n, Vector3 dir, int count, float spread)
    {
        if (!shards) return;
        var ep = new ParticleSystem.EmitParams();
        for (int k = 0; k < count; k++)
        {
            bool through = k % 3 != 0;
            ep.position = at + n * (through ? -0.03f : 0.02f) + Random.insideUnitSphere * 0.07f * spread * (spread > 1.5f ? 2.2f : 1f);
            ep.velocity = (through ? dir * Random.Range(1.5f, 5f) : n * Random.Range(0.6f, 2.2f)) + Random.insideUnitSphere * 1.4f + Vector3.up * Random.Range(0f, 1f);
            ep.startSize = Random.Range(0.012f, 0.04f) * (spread > 1.5f ? 1.3f : 1f); ep.startLifetime = Random.Range(0.7f, 1.6f);
            ep.rotation3D = Random.insideUnitSphere * 180f; ep.angularVelocity3D = Random.insideUnitSphere * 720f;
            shards.Emit(ep, 1);
        }
    }

    // ---------- built once ----------
    static void Build()
    {
        if (quad) return;
        var prim = GameObject.CreatePrimitive(PrimitiveType.Quad); quad = prim.GetComponent<MeshFilter>().sharedMesh; Object.Destroy(prim);
        var sh = Shader.Find("Sprites/Default");
        holeMat = new Material(sh) { name = "Car bullet hole", mainTexture = HoleTex(128) };
        crackMat = new Material(sh) { name = "Car glass crack", mainTexture = CrackTex(512) };
        brokenMat = new Material(sh) { name = "Car pane broken out", mainTexture = BrokenTex(256) };
        shardMat = new Material(sh) { name = "Glass shard", mainTexture = Texture2D.whiteTexture, color = new Color(0.82f, 0.93f, 0.95f, 0.85f) };

        var go = new GameObject("Glass shards"); Object.DontDestroyOnLoad(go);
        shards = go.AddComponent<ParticleSystem>();
        shards.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = shards.main; main.loop = false; main.playOnAwake = false; main.maxParticles = 400; main.gravityModifier = 1.6f;
        main.simulationSpace = ParticleSystemSimulationSpace.World; main.startRotation3D = true; main.startColor = new Color(0.82f, 0.93f, 0.95f, 0.9f);
        var em = shards.emission; em.enabled = false;
        var shp = shards.shape; shp.enabled = false;
        var col = shards.collision; col.enabled = true; col.type = ParticleSystemCollisionType.World; col.bounce = 0.25f; col.dampen = 0.5f; col.lifetimeLoss = 0.25f; col.quality = ParticleSystemCollisionQuality.Low;
        var pr = go.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = shardMat; pr.renderMode = ParticleSystemRenderMode.Mesh; pr.mesh = quad;
        pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.alignment = ParticleSystemRenderSpace.World;

        try
        {
            glassClips = new[] { GlassClip(11), GlassClip(23), GlassClip(37) }; tyreClip = TyreClip();
            var vg = new GameObject("Glass sounds"); Object.DontDestroyOnLoad(vg);
            voices = new AudioSource[4];
            for (int k = 0; k < voices.Length; k++)
            {
                var c = new GameObject("voice"); c.transform.SetParent(vg.transform, false);
                var a = c.AddComponent<AudioSource>(); a.playOnAwake = false; a.spatialBlend = 1f; a.rolloffMode = AudioRolloffMode.Logarithmic; a.minDistance = 4f; a.maxDistance = 90f; a.dopplerLevel = 0;
                voices[k] = a;
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[car] glass sound off: " + e.Message); voices = null; }
    }

    // a punched hole in painted sheet steel: through-hole, bare bright rim, chipped paint, a few flakes
    static Texture2D HoleTex(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        var px = new Color32[n * n]; var rnd = new System.Random(5);
        var lobes = new float[9]; for (int i = 0; i < lobes.Length; i++) lobes[i] = (float)rnd.NextDouble();
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
            float wob = 0; for (int i = 0; i < lobes.Length; i++) wob += Mathf.Sin(a * (i + 2) + lobes[i] * 6.28f) * 0.018f / (1 + i * 0.3f);
            float hole = 0.24f + wob, rim = 0.38f + wob * 1.6f, chip = 0.62f + wob * 4f, soot = 0.95f;
            Color c; float al;
            if (r < hole) { c = new Color(0.015f, 0.015f, 0.018f); al = 1; }
            else if (r < rim) { float k = Mathf.InverseLerp(hole, rim, r); c = Color.Lerp(new Color(0.42f, 0.42f, 0.44f), new Color(0.86f, 0.86f, 0.84f), Mathf.Sin(k * Mathf.PI)); al = 1; }      // bare steel, bright where it is bent inwards
            else if (r < chip) { float k = Mathf.InverseLerp(rim, chip, r); c = Color.Lerp(new Color(0.55f, 0.55f, 0.54f), new Color(0.3f, 0.29f, 0.27f), k); al = (1 - k) * (0.75f + 0.25f * Mathf.Sin(a * 13 + lobes[0] * 9)); }   // primer and chipped paint
            else if (r < soot) { float k = Mathf.InverseLerp(chip, soot, r); c = new Color(0.1f, 0.09f, 0.08f); al = (1 - k) * (1 - k) * 0.55f; }      // scorched, dirty halo: it is what makes the hole show on pale paint
            else { c = Color.black; al = 0; }
            px[y * n + x] = new Color(c.r, c.g, c.b, Mathf.Clamp01(al));
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    // toughened glass round a bullet hole: a small hole, radial cracks, rings of concentric cracks, crazing near the centre
    static Texture2D CrackTex(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        var al = new float[n * n]; var rnd = new System.Random(9);
        float R() => (float)rnd.NextDouble();
        void Line(Vector2 a, Vector2 b, float w, float s)
        {
            int steps = Mathf.CeilToInt(Vector2.Distance(a, b) * n * 1.5f);
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)Mathf.Max(1, steps)); int cx = (int)((p.x * 0.5f + 0.5f) * n), cy = (int)((p.y * 0.5f + 0.5f) * n), rr = Mathf.CeilToInt(w);
                for (int oy = -rr; oy <= rr; oy++) for (int ox = -rr; ox <= rr; ox++)
                {
                    int x = cx + ox, y = cy + oy; if (x < 0 || y < 0 || x >= n || y >= n) continue;
                    float d = Mathf.Sqrt(ox * ox + oy * oy); float v = Mathf.Clamp01(1 - d / w) * s;
                    if (v > al[y * n + x]) al[y * n + x] = v;
                }
            }
        }
        int rays = 19; var ang = new float[rays]; var len = new float[rays];
        for (int i = 0; i < rays; i++) { ang[i] = (i + R() * 0.7f) / rays * 6.2832f; len[i] = 0.45f + R() * 0.5f; }
        for (int i = 0; i < rays; i++)
        {
            // a radial crack wanders a little on its way out, and thins
            Vector2 p = new Vector2(Mathf.Cos(ang[i]), Mathf.Sin(ang[i])) * 0.05f; float a = ang[i];
            int segs = 7;
            for (int k = 0; k < segs; k++)
            {
                a += (R() - 0.5f) * 0.22f; float step = len[i] / segs;
                var q = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * step;
                Line(p, q, Mathf.Lerp(4.6f, 2.4f, k / (float)segs), Mathf.Lerp(1f, 0.7f, k / (float)segs)); p = q;
            }
        }
        // concentric cracks: short chords between neighbouring radials, denser near the hole
        foreach (float rad in new[] { 0.1f, 0.16f, 0.24f, 0.34f, 0.47f, 0.62f })
            for (int i = 0; i < rays; i++)
            {
                int j = (i + 1) % rays; if (R() < 0.18f + rad * 0.5f || rad > len[i] || rad > len[j]) continue;
                float r1 = rad * (0.94f + R() * 0.12f), r2 = rad * (0.94f + R() * 0.12f);
                Line(new Vector2(Mathf.Cos(ang[i]), Mathf.Sin(ang[i])) * r1, new Vector2(Mathf.Cos(ang[j]), Mathf.Sin(ang[j])) * r2, 3.2f, 0.85f);
            }
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = al[y * n + x];
            float frost = Mathf.Clamp01(1 - r / 0.3f) * 0.7f;                   // crushed white round the hole
            float edge = Mathf.Clamp01((1f - r) * 6f);                             // fade out before the quad's edge
            Color c = new Color(0.93f, 0.96f, 0.97f); float outA = Mathf.Max(a, frost) * edge;
            if (r < 0.045f) { c = new Color(0.02f, 0.02f, 0.025f); outA = 1; }
            px[y * n + x] = new Color(c.r, c.g, c.b, Mathf.Clamp01(outA));
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    // the pane gone: a dark ragged opening, with teeth of glass still standing round its edge
    static Texture2D BrokenTex(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        var px = new Color32[n * n]; var rnd = new System.Random(21);
        var ph = new float[11]; for (int i = 0; i < ph.Length; i++) ph[i] = (float)rnd.NextDouble() * 6.2832f;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
            float edge = 0.78f; for (int i = 0; i < ph.Length; i++) edge += Mathf.Sin(a * (i + 2) + ph[i]) * 0.035f / (1 + i * 0.25f);
            float teeth = Mathf.Abs(Mathf.Sin(a * 17 + ph[0])) * Mathf.Abs(Mathf.Sin(a * 7 + ph[1])) * 0.1f;       // shards left standing in the frame
            Color c; float al;
            if (r < edge - teeth) { c = new Color(0.05f, 0.055f, 0.06f); al = 0.86f; }
            else if (r < edge) { c = new Color(0.86f, 0.92f, 0.93f); al = 0.85f; }
            else if (r < edge + 0.06f) { c = new Color(0.9f, 0.95f, 0.96f); al = (1 - (r - edge) / 0.06f) * 0.6f; }
            else { c = Color.black; al = 0; }
            px[y * n + x] = new Color(c.r, c.g, c.b, al);
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    // a tyre going: a hard thump, then the air rushing out and dying away
    static AudioClip TyreClip()
    {
        const int rate = 44100; int n = (int)(rate * 1.1f); var d = new float[n]; var rnd = new System.Random(77);
        float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, w = (float)rnd.NextDouble() * 2 - 1;
            float thump = Mathf.Sin(6.2832f * (150f - 80f * Mathf.Min(1, t * 9f)) * t) * Mathf.Exp(-t * 26f) * 1.1f + w * Mathf.Exp(-t * 60f) * 0.7f;
            lp += (w - lp) * 0.55f; lp2 += (lp - lp2) * 0.12f;
            float hiss = (lp - lp2) * Mathf.Exp(-t * 4.2f) * Mathf.Min(1f, t * 40f) * 0.55f;
            d[i] = thump + hiss;
        }
        float peak = 0; for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(d[i]));
        for (int i = 0; i < n; i++) d[i] = d[i] / peak * 0.9f * Mathf.Min(1f, (n - i) / (rate * 0.06f));
        var clip = AudioClip.Create("tyre", n, 1, rate, false); clip.SetData(d, 0); return clip;
    }

    // breaking glass: the sharp crack of the pane, then a shower of fragments ringing as they land
    static AudioClip GlassClip(int seed)
    {
        const int rate = 44100; int n = (int)(rate * 0.95f); var d = new float[n]; var rnd = new System.Random(seed);
        float R() => (float)rnd.NextDouble();
        float lp = 0;
        for (int i = 0; i < n; i++)                                               // the break: bright noise, almost instant
        {
            float tt = i / (float)rate, w = (R() * 2 - 1); lp += (w - lp) * 0.35f;
            d[i] = (w - lp) * Mathf.Exp(-tt * 55f) * 0.9f;
        }
        int pieces = 46;
        for (int p = 0; p < pieces; p++)                                          // fragments: short high rings, thinning out over most of a second
        {
            float at = Mathf.Pow(R(), 1.7f) * 0.7f, f = 2600f + R() * 7200f, dec = 22f + R() * 55f, amp = (0.05f + R() * 0.12f) * (1f - at * 0.9f), ph = R() * 6.28f;
            int s0 = (int)(at * rate), s1 = Mathf.Min(n, s0 + (int)(rate * 0.22f));
            for (int i = s0; i < s1; i++) { float tt = (i - s0) / (float)rate; d[i] += Mathf.Sin(tt * f * 6.2832f + ph) * Mathf.Exp(-tt * dec) * amp * Mathf.Min(1f, tt * 900f); }
        }
        float peak = 0; for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(d[i]));
        if (peak > 0) for (int i = 0; i < n; i++) d[i] = d[i] / peak * 0.85f * Mathf.Min(1f, (n - i) / (rate * 0.05f));
        var clip = AudioClip.Create("glass " + seed, n, 1, rate, false); clip.SetData(d, 0); return clip;
    }
}

public class CarDamageRunner : MonoBehaviour
{
    void Update() => CarDamage.Settle(Time.deltaTime);
}
