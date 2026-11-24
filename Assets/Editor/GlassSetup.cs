// Building windows that break.
// The game tells a window from a wall by reading the glass mask LookUpgrade made for each facade picture
// (Textures/Generated/Facade_*_mask.png, red channel). For that the masks must be readable at run time,
// and the hit must report a texture coordinate, which needs the building meshes to be readable too.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod GlassSetup.Run
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class GlassSetup
{
    public static void Run()
    {
        int masks = 0, changed = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Benghazi/Textures/Generated" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!Path.GetFileName(path).StartsWith("Facade_") || !path.EndsWith("_mask.png")) continue;
            masks++;
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            if (ti && !ti.isReadable) { ti.isReadable = true; ti.SaveAndReimport(); changed++; }
        }
        // what the windows sit on: the city's mesh colliders
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        int facadeColliders = 0, readable = 0, withMask = 0;
        foreach (var mc in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include))
        {
            var r = mc.GetComponent<MeshRenderer>(); if (!r || !mc.sharedMesh) continue;
            bool facade = false, mask = false;
            foreach (var m in r.sharedMaterials) if (m && m.name.StartsWith("Facade")) { facade = true; if (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap")) mask = true; }
            if (!facade) continue;
            facadeColliders++; if (mc.sharedMesh.isReadable) readable++; if (mask) withMask++;
        }
        Debug.Log($"[glass] facade masks: {masks} found, {changed} made readable; facade colliders in the scene: {facadeColliders}, with a readable mesh {readable}, with a glass mask {withMask}");
        if (masks == 0 || facadeColliders == 0) EditorApplication.Exit(1);
    }
}
