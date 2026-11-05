// Presentation screenshots of the landmarks (for showing the prototype).
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ShowcaseShots.Run
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ShowcaseShots
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        var cam = Object.FindFirstObjectByType<Camera>();
        cam.fieldOfView = 60f; cam.aspect = 16f / 9f;
        GameObject.Find("Player")?.SetActive(false);
        var palace = GameObject.Find("palace");
        if (palace)
        {
            var b = new Bounds(palace.transform.position, Vector3.zero);
            foreach (var r in palace.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            var front = palace.transform.forward;              // the arcade side
            cam.transform.position = b.center + front * 30f + palace.transform.right * 12f + Vector3.up * -3f;
            cam.transform.position = new Vector3(cam.transform.position.x, 2.2f, cam.transform.position.z);
            cam.transform.LookAt(b.center + Vector3.up * 1f);
            SceneBuilder.Shoot(cam, "showcase_palace.png");
            cam.transform.position = b.center + front * 6f - palace.transform.right * 8f;
            cam.transform.position = new Vector3(cam.transform.position.x, 1.7f, cam.transform.position.z);
            cam.transform.LookAt(b.center + front * 6f + palace.transform.right * 10f + Vector3.up * 1.6f);
            SceneBuilder.Shoot(cam, "showcase_arcade.png");
        }
        var lh = SceneBuilder.FindLighthouse();
        cam.transform.position = lh + new Vector3(-70f, 25f, -45f);
        cam.transform.LookAt(lh + Vector3.up * 5f);
        SceneBuilder.Shoot(cam, "showcase_aerial.png");
    }
}
