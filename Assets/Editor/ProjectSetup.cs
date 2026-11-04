// One-time project setup, run from the command line:
//   Unity.exe -batchmode -projectPath <proj> -executeMethod ProjectSetup.AddPackages
// Adds the packages the game needs (Unity picks the version that fits this editor).
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class ProjectSetup
{
    static readonly string[] Packages =
    {
        "com.unity.render-pipelines.universal",  // URP: one renderer, PC "High" and phone "Mobile" quality
        "com.unity.cloud.gltfast",                // import .glb (the city export, Tripo models)
        "com.unity.inputsystem",                  // touch, keyboard/mouse and gamepad
        "com.unity.cinemachine",                  // third-person and cutscene cameras
        "com.unity.ai.navigation",                // NavMesh for the AI soldiers
    };

    static AddAndRemoveRequest request;

    public static void AddPackages()
    {
        request = Client.AddAndRemove(Packages, null);
        EditorApplication.update += Wait;
    }

    static void Wait()
    {
        if (!request.IsCompleted) return;
        EditorApplication.update -= Wait;
        if (request.Status == StatusCode.Success)
        {
            foreach (var p in request.Result) Debug.Log($"[setup] package {p.name} {p.version}");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError($"[setup] package install failed: {request.Error?.message}");
            EditorApplication.Exit(1);
        }
    }
}
