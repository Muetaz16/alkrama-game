// Landmark ruins made in Tripo from the owner's photos (Tools/blender/prep_hero.py shrinks them to ~45k triangles,
// stands them on the ground and turns the photographed front to +z). Each one replaces a few web-city buildings
// near the play area: the box is switched off and the model is set with its front on the street line, scaled to
// the length of that wall. Copies go to different streets, at least HeroSpacing apart, each facing its own street.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class CityDetail
{
    const string HeroDir = Root + "/Models/Hero";
    const float HeroSpacing = 55f;
    const string HiddenTag = "[hero-hidden]";
    // file, real width of the photographed front in metres, copies
    static readonly (string file, float width, int copies)[] Heroes =
    {
        ("hero_tripo3", 26f, 2), ("hero_ruins", 30f, 3), ("hero_city", 22f, 3),     // hero_ruined left out: its base is missing, it reads as a slab
    };
    static readonly List<GameObject> heroPlaced = new();
    static readonly List<List<Vector3>> cutPolys = new();
    const string CutAsset = Root + "/Detail/CityCut.asset";

    // The web city is merged per facade material into a few meshes that span the whole map, so a building can't be
    // switched off: its walls and roof are cut out of those meshes instead (triangles above the ground whose centre
    // lies inside the footprint), and the cut copies are saved in CityCut.asset.
    static void RestoreCut(GameObject city)
    {
        cutPolys.Clear();
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh && !mf.sharedMesh.name.EndsWith("_cut")) continue;   // a cut copy, or a lost one (CityCut.asset was remade)
            var src = PrefabUtility.GetCorrespondingObjectFromSource(mf);
            if (!src || !src.sharedMesh) continue;
            mf.sharedMesh = src.sharedMesh;
            if (mf.TryGetComponent<MeshCollider>(out var mc)) mc.sharedMesh = src.sharedMesh;
            PrefabUtility.RevertObjectOverride(mf, InteractionMode.AutomatedAction);
        }
    }

    static bool InCut(Vector3 p)
    {
        foreach (var poly in cutPolys)
        {
            if (Inside(poly, p)) return true;
            for (int i = 0; i < poly.Count; i++)                                       // walls sit on the outline: 0.6 m margin
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count]; var ab = new Vector2(b.x - a.x, b.z - a.z); var ap = new Vector2(p.x - a.x, p.z - a.z);
                float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
                if ((ap - ab * t).sqrMagnitude < (p.y > 2.5f ? 9f : 0.36f)) return true;   // up high also the balconies and shop awnings that stuck out (3 m)
            }
        }
        return false;
    }

    static bool InCutPoly(Vector3 p) => cutPolys.Any(poly => Inside(poly, p));
    static bool NearPoly(List<Vector3> poly, Vector3 p, float m)
    {
        if (Inside(poly, p)) return true;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count]; var ab = new Vector2(b.x - a.x, b.z - a.z); var ap = new Vector2(p.x - a.x, p.z - a.z);
            float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
            if ((ap - ab * t).sqrMagnitude < m * m) return true;
        }
        return false;
    }

    // the Corniche must run free between the city and the sea: buildings on the sea side of it, or standing in a
    // road, are taken out (cut from the city meshes like a landmark lot)
    static void ClearSeaside()
    {
        var corniche = roads.Where(r => r.name == "كورنيش بنغازي").ToList();
        int sea = 0, onRoad = 0;
        foreach (var b in blds.ToList())
        {
            bool remove = StreetEdge(b.c, null, out _) < -0.5f;
            if (remove) onRoad++;
            else if (corniche.Count > 0 && coastPts.Count > 0)
            {
                Vector3 q = default; float best = float.MaxValue;
                foreach (var r in corniche)
                    for (int k = 0; k < r.pts.Count - 1; k++)
                    {
                        var a = r.pts[k]; var d = r.pts[k + 1] - a; d.y = 0; float L2 = Mathf.Max(d.sqrMagnitude, 1e-4f);
                        float t = Mathf.Clamp01(Vector3.Dot(new Vector3(b.c.x - a.x, 0, b.c.z - a.z), d) / L2);
                        var p = a + d * t; float dd = (new Vector3(b.c.x, 0, b.c.z) - new Vector3(p.x, 0, p.z)).sqrMagnitude;
                        if (dd < best) { best = dd; q = new Vector3(p.x, 0, p.z); }
                    }
                if (best < 150f * 150f)
                {
                    var cp = coastPts.OrderBy(p => (p - q).sqrMagnitude).First();
                    var toB = new Vector3(b.c.x, 0, b.c.z) - q; var toSea = cp - q;
                    if (Vector3.Dot(toB, toSea) > 0 && toB.magnitude < toSea.magnitude + 10f) { remove = true; sea++; }
                }
            }
            if (!remove) continue;
            cutPolys.Add(b.poly); blds.Remove(b);
        }
        Debug.Log($"[hero] seaside cleared: {sea} buildings between the Corniche and the sea, {onRoad} standing in a road");
    }

    static void CutCity(GameObject city)
    {
        AssetDatabase.DeleteAsset(CutAsset);
        if (cutPolys.Count == 0) return;
        var holder = new Mesh { name = "CityCut" }; AssetDatabase.CreateAsset(holder, CutAsset);
        int meshes = 0, tris = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>(true))
        {
            var src = mf.sharedMesh; if (!src) continue;
            var M = mf.transform.localToWorldMatrix;
            var wb = new Bounds(M.MultiplyPoint3x4(src.bounds.center), Vector3.zero);
            for (int i = 0; i < 8; i++) wb.Encapsulate(M.MultiplyPoint3x4(src.bounds.center + Vector3.Scale(src.bounds.extents, new Vector3(i % 2 * 2 - 1, i / 2 % 2 * 2 - 1, i / 4 * 2 - 1))));
            if (wb.max.y < 1f || !cutPolys.Any(poly => poly.Any(p => wb.Contains(new Vector3(p.x, wb.center.y, p.z))))) continue;
            var v = src.vertices; var subs = new List<int[]>(); int removed = 0;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var t = src.GetTriangles(s); var keep = new List<int>(t.Length);
                for (int k = 0; k < t.Length; k += 3)
                {
                    var p0 = M.MultiplyPoint3x4(v[t[k]]); var p1 = M.MultiplyPoint3x4(v[t[k + 1]]); var p2 = M.MultiplyPoint3x4(v[t[k + 2]]);
                    var c = (p0 + p1 + p2) / 3f;
                    if (c.y > 0.3f)
                    {
                        var nt = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                        bool cutIt;
                        if (Mathf.Abs(nt.y) > 0.7f)
                            // roofs and floors: big triangles, so by their corners - all three on the lot (the lot's own
                            // roof), or the centre inside it; the next building's roof keeps every triangle
                            cutIt = InCutPoly(c) || cutPolys.Any(poly => NearPoly(poly, p0, 0.4f) && NearPoly(poly, p1, 0.4f) && NearPoly(poly, p2, 0.4f));
                        else
                        {
                            // walls, parapets, awnings: by the centre with the margin, but the next building's wall that
                            // faces into the lot and reaches the ground stays
                            bool neighbourWall = Mathf.Abs(nt.y) < 0.3f && Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) < 1.5f && InCutPoly(c + nt * 0.8f) && !InCutPoly(c - nt * 0.8f);
                            cutIt = InCut(c) && !neighbourWall;
                        }
                        if (cutIt) { removed++; continue; }
                    }
                    keep.Add(t[k]); keep.Add(t[k + 1]); keep.Add(t[k + 2]);
                }
                subs.Add(keep.ToArray());
            }
            if (removed == 0) continue;
            var cut = Object.Instantiate(src); cut.name = src.name + "_cut";
            for (int s = 0; s < subs.Count; s++) cut.SetTriangles(subs[s], s);
            AssetDatabase.AddObjectToAsset(cut, holder);
            mf.sharedMesh = cut;
            if (mf.TryGetComponent<MeshCollider>(out var mc)) mc.sharedMesh = cut;
            meshes++; tris += removed;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[hero] cut {tris} triangles of the old buildings out of {meshes} city meshes");
    }

    static Material HeroMat(Material src, string file)
    {
        if (!src) return src;
        var path = $"{Mats}/Hero_{file}_{src.name.Replace(' ', '_').Replace('/', '_')}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Hero_" + file };
        Texture T(string p) => src.HasProperty(p) ? src.GetTexture(p) : null;
        var baseTex = T("baseColorTexture") ?? T("_BaseMap") ?? T("_MainTex");
        if (baseTex) m.SetTexture("_BaseMap", baseTex);
        var c = src.HasProperty("baseColorFactor") ? src.GetColor("baseColorFactor") : Color.white; c.a = 1;
        m.SetColor("_BaseColor", c * new Color(1.02f, 0.97f, 0.9f));           // a touch warmer, like the ochre streets
        var nrm = T("normalTexture"); if (nrm) { m.SetTexture("_BumpMap", nrm); m.EnableKeyword("_NORMALMAP"); }
        m.SetFloat("_Smoothness", 0.08f); m.SetFloat("_Metallic", 0f);
        m.SetFloat("_Cull", 0); m.doubleSidedGI = true;                       // scanned shells are open at the back
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static void HeroBuildings()
    {
        heroPlaced.Clear();
        var city = GameObject.Find("Benghazi Corniche"); if (!city) return;
        // a re-run: bring back what the last run switched off, the spots may differ now
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
            if (r.gameObject.name == HiddenTag) { r.enabled = true; if (r.TryGetComponent<Collider>(out var c)) c.enabled = true; r.gameObject.name = ""; }
        RestoreCut(city);
        ClearSeaside();
        Physics.SyncTransforms();
        var lh = SceneBuilder.FindLighthouse();

        // street fronts that could take a landmark
        var spots = new List<(Bld b, Vector3 mid, Vector3 n, float L, float depth, float inset, Road road)>();
        foreach (var b in blds)
        {
            int nP = b.poly.Count; if (nP < 3 || b.h < 6f) continue;
            float dp = Vector2.Distance(new Vector2(b.c.x, b.c.z), new Vector2(player.x, player.z));
            if (dp < 35f || dp > 280f || Vector3.Distance(new Vector3(b.c.x, 0, b.c.z), lh) < 50f) continue;
            if (keepOut.Any(k => Vector3.Distance(new Vector3(b.c.x, 0, b.c.z), k.c) < k.r + 10)) continue;
            for (int i = 0; i < nP; i++)
            {
                var a = b.poly[i]; var c = b.poly[(i + 1) % nP]; var d = c - a; d.y = 0; float L = d.magnitude; if (L < 12f) continue;
                var f = d / L; var mid = (a + c) / 2;
                var n = new Vector3(-f.z, 0, f.x); if (Vector3.Dot(n, mid - b.c) < 0) n = -n;
                if (!(StreetEdge(mid + n * 3f, null, out var near) < 5f && near != null)) continue;
                if (!Ray(mid + n * 1.6f + Vector3.up * 2f, -n, 2.6f, out var wh) || Vector3.Dot(wh.normal, n) < 0.7f) continue;
                float depth = b.poly.Max(p => Vector3.Dot(mid - p, n));
                spots.Add((b, mid, n, L, depth, Vector3.Dot(wh.point - mid, n), near));
            }
        }

        Debug.Log($"[hero] {spots.Count} street fronts >= 12 m near the play area: " + string.Join(" ", spots.Select(x => x.L.ToString("F0"))));
        int placed = 0; var taken = new List<Vector3>();
        foreach (var (file, width, copies) in Heroes)
        {
            var path = $"{HeroDir}/{file}.glb";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!asset) { Debug.LogWarning("[hero] missing " + path); continue; }
            var mb = new Bounds(); bool first = true;
            foreach (var mf in asset.GetComponentsInChildren<MeshFilter>())
                foreach (var v in mf.sharedMesh.vertices) { var w = mf.transform.localToWorldMatrix.MultiplyPoint3x4(v); if (first) { mb = new Bounds(w, Vector3.zero); first = false; } else mb.Encapsulate(w); }
            for (int k = 0; k < copies; k++)
            {
                // the closest fit in length, ruined blocks first, never next to another landmark
                // the honeycomb ruin stands on the main road, where it is seen from the Corniche
                bool mainOnly = file == "hero_tripo3" && k == 0;
                var best = spots.Where(s => s.L > width * 0.6f && s.L < width * 1.35f && !taken.Any(t => Vector3.Distance(t, s.mid) < HeroSpacing)
                                         && (!mainOnly || s.road.name == "كورنيش بنغازي" || Main(s.road)))
                                .OrderBy(s => Mathf.Abs(s.L - width) / width + (s.b.ruined ? 0f : 0.25f) + R(0, 0.3f)).FirstOrDefault();
                if (best.b == null) { Debug.LogWarning($"[hero] no street front left for {file} copy {k + 1}"); break; }
                float s = best.L / mb.size.x;                                   // exactly the length of the wall: nothing pokes into the next block
                // switch off the web building(s) standing on this footprint
                int off = 0;
                foreach (var r in city.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!r.enabled) continue; var bb = r.bounds;
                    if (bb.max.y < 1.5f || bb.size.x > 60f || bb.size.z > 60f) continue;
                    if (!Inside(best.b.poly, bb.center) && !(Mathf.Max(bb.size.x, bb.size.z) < 15f && bb.min.y > 1.8f && NearPoly(best.b.poly, bb.center, 3.5f))) continue;
                    r.enabled = false; if (r.TryGetComponent<Collider>(out var c)) c.enabled = false; r.gameObject.name = HiddenTag; off++;
                }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                go.name = $"Landmark {file} {k + 1}";
                var wallLine = best.mid + best.n * best.inset; wallLine.y = -0.05f;
                go.transform.SetPositionAndRotation(wallLine - best.n * (mb.max.z * s), Quaternion.LookRotation(best.n));
                go.transform.localScale = Vector3.one * s;
                // the glTF importer's shader graph is not in the player build: give each model a URP Lit copy of its surface
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                    r.sharedMaterials = r.sharedMaterials.Select(m => HeroMat(m, file)).ToArray();
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>()) { mf.gameObject.isStatic = true; if (!mf.GetComponent<Collider>()) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh; }
                go.isStatic = true;
                heroPlaced.Add(go); taken.Add(best.mid); blds.Remove(best.b); cutPolys.Add(best.b.poly); spots.RemoveAll(x => x.b == best.b);
                float hh = mb.max.y * s;
                ReviewViews.Add((wallLine + best.n * (12f + hh * 0.6f) + Vector3.up * 1.7f, wallLine + Vector3.up * hh * 0.45f, $"hero_{file}_{k + 1}.png"));
                Debug.Log($"[hero] {file} copy {k + 1}: front {best.L:F1} m (model {width} m), scale {s:F1}, {mb.size.x * s:F0} x {hh:F0} m high at {wallLine}, {off} web parts off{(best.b.ruined ? ", was a ruined block" : "")}");
                placed++;
            }
        }
        CutCity(city);
        Physics.SyncTransforms();
        Debug.Log($"[hero] {placed} landmarks placed");
    }
}
