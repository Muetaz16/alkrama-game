// Bullet collision for the parked cars.
// RealCars gives each car one BoxCollider: fine for walking into, useless for a bullet hole - the box is
// not where the bodywork is, so a hole decal floats in the air off a wing or sits inside the bonnet, and
// there is no telling a window from a door. Each car therefore also carries a MeshCollider of its own
// body ("Body collider", a child), and the box moves to the Ignore Raycast layer: people still collide
// with the simple box, rounds and lines of sight meet the real shape.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod CarColliders.Run
// (swaps it in the scene as it is; CityDetail's full rebuild calls Apply so it survives a regeneration)
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class CarColliders
{
    public const string ChildName = "Body collider";

    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        int n = Apply();
        if (n < 0) { EditorApplication.Exit(1); return; }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    /// <summary>Returns the number of cars set up, or -1 if the cars could not be found.</summary>
    public static int Apply()
    {
        Transform holder = null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)) if (t.name == "Cars" && t.childCount > 0 && t.GetChild(0).GetComponent<LODGroup>()) { holder = t; break; }
        if (!holder) { Debug.LogError("[cars] no 'Cars' object with LOD groups in the scene"); return -1; }
        int done = 0, tris = 0;
        foreach (Transform car in holder)
        {
            var lod0 = car.Find("LOD0"); var mf = lod0 ? lod0.GetComponent<MeshFilter>() : null;
            if (!mf || !mf.sharedMesh) { Debug.LogWarning($"[cars] {car.name}: no LOD0 mesh, left as it was"); continue; }
            var old = car.Find(ChildName); if (old) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(ChildName); go.transform.SetParent(car, false); go.isStatic = true; go.layer = 0;
            go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            car.gameObject.layer = 2;                       // Ignore Raycast: the box is for bodies, not bullets
            done++; tris += mf.sharedMesh.triangles.Length / 3;
        }
        Debug.Log($"[cars] bullet colliders: {done} cars, {tris} triangles in all; walking still uses the boxes");
        return done;
    }
}
