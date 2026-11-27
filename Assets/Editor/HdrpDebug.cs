// Quick HDRP look checks from the editor, without building a player.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod HdrpDebug.Shots
// Renders the promenade view a few times into unity_shots_hdrp/dbg_*.png, each with one thing changed, to see
// what a setting does to the picture.
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class HdrpDebug
{
    public static void Shots()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        ShaderUtil.allowAsyncCompilation = false;
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).First(c => c.CompareTag("MainCamera"));
        var player = GameObject.Find("Player"); var at = player ? player.transform.position : Vector3.zero; var fwd = player ? player.transform.forward : Vector3.forward;
        if (player) player.SetActive(false);
        cam.fieldOfView = 60f; cam.aspect = 16f / 9f;
        cam.transform.position = at + Vector3.up * 1.62f; cam.transform.rotation = Quaternion.LookRotation(fwd);
        var vols = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include);
        var sky = vols.First(v => v.name == "Sky and Fog"); var post = vols.First(v => v.name == "Post FX");
        var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Include).First(l => l.type == LightType.Directional);
        Debug.Log($"[dbg] sun intensity {sun.intensity} unit {sun.lightUnit} colour {sun.color} shadows {sun.shadows}; volumes: {string.Join(", ", vols.Select(v => v.name + " p" + v.priority))}");
        sky.sharedProfile.TryGet<Exposure>(out var ex); sky.sharedProfile.TryGet<Fog>(out var fog); sky.sharedProfile.TryGet<HDRISky>(out var hdri);
        Debug.Log($"[dbg] exposure mode {ex.mode.value} fixed {ex.fixedExposure.value} comp {ex.compensation.value}; sky mult {hdri.multiplier.value} mode {hdri.skyIntensityMode.value}; fog mfp {fog.meanFreePath.value}");

        SceneBuilder.Shoot(cam, "dbg_0_fwd.png");
        cam.transform.rotation = Quaternion.LookRotation(-fwd); SceneBuilder.Shoot(cam, "dbg_1_back.png");
        cam.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0, -75f, 0) * fwd) * Quaternion.Euler(8, 0, 0); cam.transform.position += Vector3.up * 0.6f; SceneBuilder.Shoot(cam, "dbg_2_sea.png");
        cam.transform.rotation = Quaternion.LookRotation(-sun.transform.forward); SceneBuilder.Shoot(cam, "dbg_3_sun.png");
        var lh = SceneBuilder.FindLighthouse(); var seaDir = CityDetail.SeaDirection();
        cam.transform.position = lh + seaDir * 48f + Vector3.Cross(Vector3.up, seaDir) * 14f + Vector3.up * 1.7f; cam.transform.LookAt(lh + Vector3.up * 9f); SceneBuilder.Shoot(cam, "dbg_4_lighthouse.png");
        if (CityDetail.ReviewViews.Count > 0) { var v = CityDetail.ReviewViews[0]; cam.transform.position = v.eye; cam.transform.LookAt(v.at); SceneBuilder.Shoot(cam, "dbg_5_street.png"); }
        else { cam.transform.position = at + Vector3.up * 1.62f - fwd * 2f + Vector3.Cross(Vector3.up, fwd) * 60f; cam.transform.rotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, fwd)); SceneBuilder.Shoot(cam, "dbg_5_street.png"); }
        Debug.Log("[dbg] done");
    }

    public static void Sun()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        ShaderUtil.allowAsyncCompilation = false;
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).First(c => c.CompareTag("MainCamera"));
        var player = GameObject.Find("Player"); var at = player.transform.position; var fwd = player.transform.forward; player.SetActive(false);
        cam.fieldOfView = 60f; cam.aspect = 16f / 9f; cam.transform.position = at + Vector3.up * 1.62f; cam.transform.rotation = Quaternion.LookRotation(-fwd);
        var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Include).First(l => l.type == LightType.Directional);
        var hd = sun.GetComponent<HDAdditionalLightData>();
        Debug.Log($"[dbg] sun enabled {sun.enabled} active {sun.gameObject.activeInHierarchy} mask {sun.cullingMask} layers {sun.renderingLayerMask} dimmer {hd.lightDimmer} affectDiffuse {hd.affectDiffuse} bake {sun.lightmapBakeType}");
        float keep = sun.intensity; sun.intensity = keep * 6;
        var vols = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include);
        var sky = vols.First(v => v.name == "Sky and Fog"); var post = vols.First(v => v.name == "Post FX");
        SceneBuilder.Shoot(cam, "sun_0_x6.png");
        post.enabled = false; SceneBuilder.Shoot(cam, "sun_1_nopostvol.png"); post.enabled = true;
        sky.sharedProfile.TryGet<Fog>(out var fog); fog.active = false; SceneBuilder.Shoot(cam, "sun_2_nofog.png"); fog.active = true;
        sky.sharedProfile.TryGet<IndirectLightingController>(out var ilc); ilc.active = false; SceneBuilder.Shoot(cam, "sun_3_noilc.png"); ilc.active = true;
        var g2 = new GameObject("Sun2"); var l2 = g2.AddComponent<Light>(); l2.type = LightType.Directional; g2.AddComponent<HDAdditionalLightData>(); l2.lightUnit = LightUnit.Lux; l2.intensity = 30; l2.color = Color.red;
        g2.transform.rotation = Quaternion.Euler(50, 30, 0); SceneBuilder.Shoot(cam, "sun_4_secondredlight.png"); Object.DestroyImmediate(g2);
        var cd = cam.GetComponent<HDAdditionalCameraData>(); cd.customRenderingSettings = true;
        cd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSGI] = true; cd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.SSGI, false);
        SceneBuilder.Shoot(cam, "sun_5_nossgi.png");
        Debug.Log("[dbg] sun done");
    }

    public static void Expo()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        ShaderUtil.allowAsyncCompilation = false;
        var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).First(c => c.CompareTag("MainCamera"));
        var player = GameObject.Find("Player"); var at = player.transform.position; var fwd = player.transform.forward; player.SetActive(false);
        cam.fieldOfView = 60f; cam.aspect = 16f / 9f; cam.transform.position = at + Vector3.up * 1.62f;
        var sky = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include).First(v => v.name == "Sky and Fog");
        sky.sharedProfile.TryGet<Exposure>(out var ex); float keep = ex.fixedExposure.value;
        foreach (var d in new[] { 1.0f, 1.4f, 1.8f })
        {
            ex.fixedExposure.value = keep - d;
            cam.transform.rotation = Quaternion.LookRotation(fwd); SceneBuilder.Shoot(cam, $"expo_{d:F1}_fwd.png");
            cam.transform.rotation = Quaternion.LookRotation(-fwd); SceneBuilder.Shoot(cam, $"expo_{d:F1}_back.png");
        }
        ex.fixedExposure.value = keep;
    }

    public static void Mats()
    {
        foreach (var path in new[] { "Assets/Benghazi/Player.prefab", "Assets/Resources/Guns/gun_ak.glb" })
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (var m in go.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m).Distinct())
            {
                string s = $"[dbg] {path} :: {m.name} shader {m.shader.name} asset {AssetDatabase.GetAssetPath(m)} kw [{string.Join(" ", m.shaderKeywords)}]";
                foreach (var prop in new[] { "_BaseColor", "_EmissiveColor", "_EmissiveColorLDR", "baseColorFactor", "emissiveFactor" }) if (m.HasProperty(prop)) s += $" {prop}={m.GetColor(prop)}";
                foreach (var prop in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap", "_EmissiveColorMap", "baseColorTexture" }) if (m.HasProperty(prop)) s += $" {prop}={(m.GetTexture(prop) ? m.GetTexture(prop).name : "-")}";
                foreach (var prop in new[] { "_Metallic", "_Smoothness", "_SurfaceType", "_AlphaCutoffEnable", "_EmissiveIntensity", "_UseEmissiveIntensity", "_MaterialID" }) if (m.HasProperty(prop)) s += $" {prop}={m.GetFloat(prop)}";
                Debug.Log(s);
            }
        }
    }
}
