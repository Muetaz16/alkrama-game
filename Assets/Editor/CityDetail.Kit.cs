// Third part of CityDetail: real facades from the Sabri building kit (Tools/blender/sabri_kit.py -> Models/Kit/sabri_kit.glb).
// Every wall of a building near the play area gets a row of kit bays per floor, laid over the web city's painted box:
// windows with stone frames and shutters, balconies, shops and doors on the streets, arches on the Italian blocks,
// shell holes and collapsed tops on the ruined ones, floor bands, a cornice and parapet on the roof line, corner stones.
// Bays are stretched a little (0.75-1.3 x) to fit each wall exactly, floors to fit the building height.
// Far away the kit is not drawn (layer 22, see ThirdPersonCamera) and the painted box underneath shows instead.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class CityDetail
{
    const string KitPath = Root + "/Models/Kit/sabri_kit.glb";
    public const float KitRadius = 240f;                 // buildings this close to the player's start get the kit
    public const int KitLayer = 22;
    static readonly HashSet<string> KitMats = new();

    class KitPiece { public List<(Mesh mesh, int sub, Matrix4x4 m, string mat)> parts = new(); }
    static Dictionary<string, KitPiece> kit;
    static float kitOut = 1f;                            // which local z the pieces stick out along (+1 / -1)

    static bool LoadKit()
    {
        AssetDatabase.ImportAsset(KitPath, ImportAssetOptions.ForceUpdate);
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(KitPath);
        if (!go) { Debug.LogWarning("[kit] no kit at " + KitPath + " - run Tools/blender/sabri_kit.py"); return false; }
        kit = new();
        var rootInv = go.transform.worldToLocalMatrix;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            var mr = mf.GetComponent<MeshRenderer>(); if (!mr || !mf.sharedMesh) continue;
            var name = mf.gameObject.name.Split('.')[0];
            var piece = new KitPiece(); var m = rootInv * mf.transform.localToWorldMatrix;
            for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
            {
                var mat = s < mr.sharedMaterials.Length && mr.sharedMaterials[s] ? mr.sharedMaterials[s].name : "Kit_Plaster";
                piece.parts.Add((mf.sharedMesh, s, m, mat.Replace(" (Instance)", "")));
            }
            kit[name] = piece;
        }
        // the pieces stick out from their back (on the wall) towards +z or -z after the glTF import: measure it
        if (kit.TryGetValue("plain", out var plain))
        {
            var (mesh, _, m, _) = plain.parts[0];
            float zc = mesh.vertices.Select(v => m.MultiplyPoint3x4(v).z).Average();
            kitOut = zc >= 0 ? 1f : -1f;
        }
        Debug.Log($"[kit] {kit.Count} pieces ({string.Join(", ", kit.Keys)}), out along {(kitOut > 0 ? "+" : "-")}z");
        return kit.Count > 0;
    }

    static void AddSub(Mesh mesh, int sub, Matrix4x4 m, Material mat, bool collide)
    {
        var p = m.GetColumn(3);
        var key = (mat, Mathf.FloorToInt(p.x / 64), Mathf.FloorToInt(p.z / 64), collide);
        if (!batches.TryGetValue(key, out var b)) batches[key] = b = new Batch { mat = mat, collide = collide };
        b.parts.Add(new CombineInstance { mesh = mesh, transform = m, subMeshIndex = sub });
        counts++;
    }

    static int kitTris;
    static void Place(string piece, Vector3 at, Vector3 outward, Vector3 scale, Dictionary<string, Material> mats, bool collide = false)
    {
        if (!kit.TryGetValue(piece, out var kp)) return;
        var M = Matrix4x4.TRS(at, Quaternion.LookRotation(outward * kitOut, Vector3.up), scale);
        foreach (var (mesh, sub, m, mat) in kp.parts)
        {
            if (!mats.TryGetValue(mat, out var um)) continue;
            AddSub(mesh, sub, M * m, um, collide);
            kitTris += (int)mesh.GetIndexCount(sub) / 3;
        }
    }

    static void KitFacades()
    {
        if (!LoadKit()) return;
        kitTris = 0; KitMats.Clear();
        Material K(Material m) { KitMats.Add(m.name); return m; }
        // ochre, tan and sand plaster like the streets of Sabri, about half of it peeling (damaged_plaster)
        var ochres = new[] { new Color(0.82f, 0.64f, 0.42f), new Color(0.76f, 0.6f, 0.42f), new Color(0.86f, 0.72f, 0.52f), new Color(0.72f, 0.58f, 0.44f), new Color(0.8f, 0.68f, 0.54f), new Color(0.88f, 0.8f, 0.68f) };
        var plasters = ochres.Select((c, i) => K(Mat2("KitPlaster" + i, i % 2 == 0 ? "damaged_plaster" : "painted_plaster_wall", c, 0.06f))).ToArray();
        var ruinedPlaster = K(Mat2("KitPlasterRuined", "damaged_plaster", new Color(0.78f, 0.62f, 0.44f), 0.05f));
        var shutters = new[] { new Color(0.24f, 0.38f, 0.28f), new Color(0.36f, 0.25f, 0.17f), new Color(0.24f, 0.32f, 0.42f), new Color(0.45f, 0.45f, 0.42f) }
            .Select((c, i) => K(Mat("KitShutter" + i, tex: "NoiseGrey", c: c, smooth: 0.25f))).ToArray();
        var trim = K(Mat("KitTrim", ph: "concrete_wall_008", c: new Color(0.8f, 0.7f, 0.56f), smooth: 0.06f, tile: 1f));
        var glass = K(Mat("KitGlass", c: new Color(0.06f, 0.07f, 0.08f), smooth: 0.88f, metal: 0.2f));
        var frame = K(Mat("KitFrame", c: new Color(0.74f, 0.74f, 0.72f), smooth: 0.45f, metal: 0.3f));
        var metal = K(Mat("KitMetal", tex: "Rust", c: new Color(0.45f, 0.42f, 0.4f), smooth: 0.3f, metal: 0.6f));
        var dark = K(Mat("KitDark", c: new Color(0.015f, 0.014f, 0.013f), smooth: 0f));
        var block = K(Mat2("KitBlock", "broken_wall", new Color(0.85f, 0.8f, 0.74f), 0.05f, 1.5f));
        var shopShutter = K(Mat("KitShopShutter", tex: "Shutter", c: Color.white, smooth: 0.35f, metal: 0.5f));
        var door = K(Mat("KitDoor", tex: "Rust", c: new Color(0.75f, 0.6f, 0.5f), smooth: 0.3f, metal: 0.4f));
        var rebar = K(Mat("Rebar", tex: "Rust", c: new Color(0.7f, 0.6f, 0.55f), smooth: 0.2f, metal: 0.7f));
        var stone = K(Mat("KitStone", ph: "concrete_wall_008", c: new Color(0.58f, 0.55f, 0.5f), smooth: 0.06f));
        var signs = new[] { new Color(0.62f, 0.14f, 0.1f), new Color(0.1f, 0.38f, 0.22f), new Color(0.12f, 0.24f, 0.5f), new Color(0.82f, 0.62f, 0.15f), new Color(0.85f, 0.84f, 0.8f) }
            .Select((c, i) => K(Mat("Sign" + i, tex: "Sign", c: c, smooth: 0.4f))).ToArray();

        int nB = 0, nBays = 0;
        foreach (var b in blds)
        {
            b.kit = false;
            int nP = b.poly.Count; if (nP < 3 || b.h < 3f) continue;
            if (Vector2.Distance(new Vector2(b.c.x, b.c.z), new Vector2(player.x, player.z)) > KitRadius) continue;
            int style = rnd.Next(100) < 45 ? 0 : rnd.Next(100) < 64 ? 1 : 2;           // 0 Italian, 1 modern, 2 old shuttered
            var mats = new Dictionary<string, Material>
            {
                ["Kit_Plaster"] = b.ruined ? ruinedPlaster : plasters[rnd.Next(plasters.Length)], ["Kit_Trim"] = trim, ["Kit_Glass"] = glass,
                ["Kit_Frame"] = frame, ["Kit_Shutter"] = shutters[rnd.Next(shutters.Length)], ["Kit_Metal"] = metal, ["Kit_Dark"] = dark,
                ["Kit_Block"] = block, ["Kit_ShopShutter"] = shopShutter, ["Kit_Sign"] = signs[rnd.Next(signs.Length)], ["Kit_Door"] = door,
                ["Kit_Rebar"] = rebar, ["Kit_Stone"] = stone,
            };
            float hit = b.ruined ? 1f : R() < 0.7f ? R(0.2f, 0.7f) : 0.05f;
            int floors = Mathf.Max(1, Mathf.RoundToInt(b.h / FloorH)); float sy = b.h / (floors * FloorH);
            bool any = false;
            for (int i = 0; i < nP; i++)
            {
                var a = b.poly[i]; var c = b.poly[(i + 1) % nP]; var d = c - a; d.y = 0; float L = d.magnitude;
                if (L < 2.6f) continue;
                var f = d / L; var mid = (a + c) / 2;
                var n = new Vector3(-f.z, 0, f.x); if (Vector3.Dot(n, mid - b.c) < 0) n = -n;
                if (!Ray(mid + n * 1.6f + Vector3.up * 1.5f, -n, 2.6f, out var wh) || Vector3.Dot(wh.normal, n) < 0.7f) continue;
                float inset = Vector3.Dot(wh.point - mid, n) + 0.01f;
                bool street = StreetEdge(mid + n * 2f, null, out var near) < 4f && near != null;
                int bays = Mathf.Max(1, Mathf.RoundToInt(L / TileW)); float bw = L / bays, sx = bw / TileW;
                int doorAt = street && bays >= 3 ? rnd.Next(bays) : -1;
                for (int j = 0; j < bays; j++)
                {
                    var o = a + f * (bw * (j + 0.5f)) + n * inset; o.y = 0;
                    // one kind per window column, top to bottom
                    bool balc = R() < (style == 1 ? 0.3f : 0.26f);
                    string win = style == 0 ? (R() < 0.72f ? "win_italian" : "win_closed") : style == 1 ? "win_modern" : (R() < 0.6f ? "win_closed" : "win_italian");
                    string bal = style == 1 ? (R() < 0.6f ? "balcony_solid" : "balcony_rail") : "balcony_rail";
                    bool topGone = b.ruined && floors > 1 && R() < 0.45f;
                    for (int fl = 0; fl < floors; fl++)
                    {
                        string pc;
                        if (topGone && fl == floors - 1) pc = "dmg_top" + rnd.Next(2);
                        else if (fl == 0)
                        {
                            if (street) pc = j == doorAt ? "door" : style == 0 && R() < 0.3f ? "arch" : R() < 0.62f ? (R() < 0.6f ? "shop" : "shop_open") : R() < 0.5f ? win : "plain";
                            else pc = R() < 0.35f ? "plain" : win;
                        }
                        else pc = balc ? bal : win;
                        // the war: blown holes, broken windows
                        if (!pc.StartsWith("dmg") && pc != "door" && R() < (b.ruined ? 0.28f : 0.06f * hit)) pc = "dmg_hole" + rnd.Next(3);
                        else if (pc.StartsWith("win") && R() < (b.ruined ? 0.5f : 0.22f * hit)) pc = "win_broken";
                        var at = o + Vector3.up * (fl * FloorH * sy);
                        Place(pc, at, n, new Vector3(sx, sy, 1), mats);
                        if (fl > 0 && !pc.StartsWith("dmg")) Place("band", at, n, new Vector3(sx, 1, 1), mats);
                        if (fl == 0 && (pc.StartsWith("win") || pc == "plain")) Place("base", at, n, new Vector3(sx, 1, 1), mats);
                        nBays++;
                    }
                    if (!topGone) Place("parapet", o + Vector3.up * b.h, n, new Vector3(sx, 1, 1), mats);
                }
                any = true;
            }
            if (!any) continue;
            // corner stones: hide the seams where two walls meet
            for (int i = 0; i < nP; i++)
            {
                var p = b.poly[i]; var e = b.poly[(i + 1) % nP] - p; e.y = 0; if (e.sqrMagnitude < 0.01f) continue;
                var outw = (p - b.c); outw.y = 0;
                Place("corner", new Vector3(p.x, 0, p.z) + outw.normalized * 0.12f, e.normalized, new Vector3(1, (b.h + 1.0f) / FloorH, 1), mats);
            }
            b.kit = true; nB++;
        }
        foreach (var m in KitMats) FarVisible.Remove(m);
        Debug.Log($"[kit] {nB} buildings within {KitRadius} m clad with {nBays} bays, {kitTris / 1000}k triangles");
    }
}
