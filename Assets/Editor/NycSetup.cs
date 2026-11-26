// The (HDRP) NYC-like City Buildings Set (Marcin's Assets, Asset Store) in this URP project:
// its HDRP/Lit materials are rebuilt as URP Lit (colour, normal, roughness/metallic from the HDRP mask map),
// and every model and stackable part is measured into the log.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod NycSetup.Run
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class NycSetup
{
    public const string Root = "Assets/ThirdParty/NYCBuildings";

    public static void Run()
    {
        AssetDatabase.Refresh();
        int n = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path); if (!m) continue;
            if (m.shader && m.shader.name.StartsWith("Universal Render Pipeline")) continue;
            // read the HDRP values straight from the saved properties before the shader changes
            var so = new SerializedObject(m);
            Texture Tex(string name) { var p = so.FindProperty("m_SavedProperties.m_TexEnvs"); for (int i = 0; i < p.arraySize; i++) { var e = p.GetArrayElementAtIndex(i); if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture; } return null; }
            Vector2 Scale(string name) { var p = so.FindProperty("m_SavedProperties.m_TexEnvs"); for (int i = 0; i < p.arraySize; i++) { var e = p.GetArrayElementAtIndex(i); if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second.m_Scale").vector2Value; } return Vector2.one; }
            float Flt(string name, float d) { var p = so.FindProperty("m_SavedProperties.m_Floats"); for (int i = 0; i < p.arraySize; i++) { var e = p.GetArrayElementAtIndex(i); if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second").floatValue; } return d; }
            Color Col(string name, Color d) { var p = so.FindProperty("m_SavedProperties.m_Colors"); for (int i = 0; i < p.arraySize; i++) { var e = p.GetArrayElementAtIndex(i); if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second").colorValue; } return d; }
            var baseTex = Tex("_BaseColorMap"); var nrm = Tex("_NormalMap"); var mask = Tex("_MaskMap");
            var col = Col("_BaseColor", Color.white); var tile = Scale("_BaseColorMap");
            float smooth = Flt("_Smoothness", 0.3f), metal = Flt("_Metallic", 0f);
            m.shader = Shader.Find("Universal Render Pipeline/Lit");
            if (baseTex) m.SetTexture("_BaseMap", baseTex);
            m.SetTextureScale("_BaseMap", tile);
            m.SetColor("_BaseColor", col);
            if (nrm) { m.SetTexture("_BumpMap", nrm); m.EnableKeyword("_NORMALMAP"); }
            m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
            if (mask) { m.SetTexture("_OcclusionMap", mask); }                 // HDRP mask: G = occlusion
            EditorUtility.SetDirty(m); n++;
            Debug.Log($"[nyc] material {m.name}: base {(baseTex ? baseTex.name : "-")} tile {tile} colour {col} normal {(nrm ? "yes" : "no")} smooth {smooth:F2} metal {metal:F2}");
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[nyc] {n} materials converted to URP Lit");

        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { Root + "/Models", Root + "/Prefabs" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var rs = go.GetComponentsInChildren<Renderer>(true); if (rs.Length == 0) continue;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            int tris = go.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh).Sum(f => (int)(f.sharedMesh.triangles.Length / 3));
            var mats = string.Join(",", rs.SelectMany(r => r.sharedMaterials).Where(x => x).Select(x => x.name).Distinct());
            Debug.Log($"[nyc] {System.IO.Path.GetFileName(path)}: size {b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m, min y {b.min.y:F1}, centre {b.center}, {rs.Length} renderers, {tris} tris, mats {mats}");
        }
    }
}
