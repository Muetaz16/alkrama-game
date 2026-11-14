// Soldier: Mixamo character + Pro Rifle Pack -> Humanoid, animator, player prefab, placed in the scene.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SoldierSetup.Run
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SoldierSetup.BuildWindows
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public static class SoldierSetup
{
    const string Dir = "Assets/Benghazi/Characters/Soldier";
    const string Model = Dir + "/Soldier.fbx";
    const string Anims = Dir + "/Animations";
    const string ControllerPath = Dir + "/Soldier.controller";
    public const string PrefabPath = "Assets/Benghazi/Player.prefab";
    const string ScenePath = "Assets/Scenes/Corniche.unity";

    public static void Run()
    {
        ImportModel();
        ImportClips();
        var controller = BuildController();
        var prefab = BuildPrefab(controller);
        BuildEnemyPrefab(controller);
        PlaceInScene(prefab);
        AssetDatabase.SaveAssets();
        Debug.Log("[soldier] setup done");
    }

    static void ImportModel()
    {
        var mi = (ModelImporter)AssetImporter.GetAtPath(Model);
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = false;
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        mi.SaveAndReimport();
        // the textures are packed inside the FBX: pull them out so the materials can use them
        if (!Directory.Exists(Dir + "/Textures")) { mi.ExtractTextures(Dir + "/Textures"); AssetDatabase.Refresh(); mi.SaveAndReimport(); }
        var avatar = AssetDatabase.LoadAllAssetsAtPath(Model).OfType<Avatar>().First();
        Debug.Log($"[soldier] avatar valid {avatar.isValid} human {avatar.isHuman}");
    }

    static void ImportClips()
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(Model).OfType<Avatar>().First();
        foreach (var path in Directory.GetFiles(Anims, "*.fbx"))
        {
            var p = path.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(p);
            var mi = (ModelImporter)AssetImporter.GetAtPath(p);
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            mi.sourceAvatar = avatar;
            mi.importAnimation = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            var clips = mi.defaultClipAnimations;
            bool loop = !name.StartsWith("death") && name != "jump up" && name != "jump down";
            foreach (var c in clips)
            {
                c.name = name;
                c.loopTime = loop; c.loopPose = loop;
                c.lockRootRotation = true; c.keepOriginalOrientation = true;
                c.lockRootHeightY = true; c.keepOriginalPositionY = true;
                c.lockRootPositionXZ = true; c.keepOriginalPositionXZ = false;
            }
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }
    }

    static AnimationClip Clip(string name) =>
        AssetDatabase.LoadAllAssetsAtPath($"{Anims}/{name}.fbx").OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));

    // eight directions; Mixamo "left" moves to the soldier's left (local -x)
    static readonly (string suffix, Vector2 dir)[] Dirs =
    {
        ("forward", new Vector2(0, 1)), ("forward left", new Vector2(-0.7071f, 0.7071f)), ("forward right", new Vector2(0.7071f, 0.7071f)),
        ("left", new Vector2(-1, 0)), ("right", new Vector2(1, 0)),
        ("backward", new Vector2(0, -1)), ("backward left", new Vector2(-0.7071f, -0.7071f)), ("backward right", new Vector2(0.7071f, -0.7071f)),
    };

    static AnimatorController BuildController()
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        var ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ac.AddParameter("VelX", AnimatorControllerParameterType.Float);
        ac.AddParameter("VelZ", AnimatorControllerParameterType.Float);
        ac.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        ac.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
        ac.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
        ac.AddParameter(new AnimatorControllerParameter { name = "AnimSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        ac.parameters.First(p => p.name == "Grounded").defaultBool = true;
        var sm = ac.layers[0].stateMachine;

        // standing: idle -> walk (1.9 m/s) -> run (4.8) -> sprint (7.2)
        var loco = ac.CreateBlendTreeInController("Locomotion", out var tree, 0);
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "VelX"; tree.blendParameterY = "VelZ";
        tree.AddChild(Clip("idle aiming"), Vector2.zero);
        foreach (var (s, d) in Dirs) tree.AddChild(Clip("walk " + s), d * PlayerController.WalkSpeed);
        foreach (var (s, d) in Dirs) tree.AddChild(Clip("run " + s), d * PlayerController.RunSpeed);
        foreach (var (s, d) in Dirs.Take(3)) tree.AddChild(Clip("sprint " + s), d * PlayerController.SprintSpeed);
        loco.speedParameterActive = true; loco.speedParameter = "AnimSpeed";   // faster than the clip at full/tactical sprint

        var crouch = ac.CreateBlendTreeInController("Crouch", out var ctree, 0);
        ctree.blendType = BlendTreeType.FreeformDirectional2D;
        ctree.blendParameter = "VelX"; ctree.blendParameterY = "VelZ";
        ctree.AddChild(Clip("idle crouching"), Vector2.zero);
        foreach (var (s, d) in Dirs) ctree.AddChild(Clip("walk crouching " + s), d * PlayerController.CrouchSpeed);

        var up = sm.AddState("Jump Up"); up.motion = Clip("jump up");
        var air = sm.AddState("In Air"); air.motion = Clip("jump loop");
        var down = sm.AddState("Land"); down.motion = Clip("jump down");
        sm.defaultState = loco;
        // falling when killed (enemies): played directly with CrossFade, no way back
        sm.AddState("Death Front").motion = Clip("death from the front");
        sm.AddState("Death Back").motion = Clip("death from the back");

        T(loco, crouch, 0.2f).AddCondition(AnimatorConditionMode.If, 0, "Crouch");
        T(crouch, loco, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch");
        var j = sm.AddAnyStateTransition(up); j.duration = 0.08f; j.canTransitionToSelf = false; j.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        var u = T(up, air, 0.1f); u.hasExitTime = true; u.exitTime = 0.85f;
        T(air, down, 0.06f).AddCondition(AnimatorConditionMode.If, 0, "Grounded");
        var l = T(down, loco, 0.15f); l.hasExitTime = true; l.exitTime = 0.45f;
        // walking off a ledge
        T(loco, air, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
        AssetDatabase.SaveAssets();
        return ac;
    }

    static AnimatorStateTransition T(AnimatorState a, AnimatorState b, float dur)
    {
        var t = a.AddTransition(b); t.hasExitTime = false; t.duration = dur; return t;
    }

    static GameObject BuildPrefab(AnimatorController ac)
    {
        var root = new GameObject("Player");
        var cc = root.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0, 0.92f, 0); cc.stepOffset = 0.4f; cc.slopeLimit = 50f;
        root.AddComponent<PlayerController>();

        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Model));
        model.transform.SetParent(root.transform, false);
        var anim = model.GetComponent<Animator>();
        anim.runtimeAnimatorController = ac;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        // scale the model to 1.8 m tall
        var b = new Bounds(model.transform.position, Vector3.zero);
        foreach (var r in model.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        if (b.size.y > 0.1f) model.transform.localScale *= 1.8f / b.size.y;
        Debug.Log($"[soldier] model height {b.size.y:F2} -> 1.8");

        AddRifle(anim);
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    public const string EnemyPrefabPath = "Assets/Benghazi/Enemy.prefab";
    // the same soldier with the enemy brain instead of the player's controls (tinted desert-tan when he spawns)
    static void BuildEnemyPrefab(AnimatorController ac)
    {
        var root = new GameObject("Enemy");
        var cc = root.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0, 0.92f, 0); cc.stepOffset = 0.4f; cc.slopeLimit = 50f;
        root.AddComponent<EnemyAI>();
        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Model));
        model.transform.SetParent(root.transform, false);
        var anim = model.GetComponent<Animator>();
        anim.runtimeAnimatorController = ac; anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        var b = new Bounds(model.transform.position, Vector3.zero);
        foreach (var r in model.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        if (b.size.y > 0.1f) model.transform.localScale *= 1.8f / b.size.y;
        PrefabUtility.SaveAsPrefabAsset(root, EnemyPrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log("[soldier] enemy prefab saved");
    }

    // simple rifle (placeholder until a real model): receiver, barrel, magazine, stock, held in the right hand
    static void AddRifle(Animator anim)
    {
        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (!hand) return;
        var dark = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Rifle" };
        dark.SetColor("_BaseColor", new Color(0.12f, 0.12f, 0.11f)); dark.SetFloat("_Smoothness", 0.45f); dark.SetFloat("_Metallic", 0.6f);
        var wood = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "RifleWood" };
        wood.SetColor("_BaseColor", new Color(0.36f, 0.2f, 0.1f)); wood.SetFloat("_Smoothness", 0.3f);
        AssetDatabase.CreateAsset(dark, Dir + "/Rifle.mat"); AssetDatabase.CreateAsset(wood, Dir + "/RifleWood.mat");
        var rifle = new GameObject("Rifle");
        void Part(string n, Vector3 pos, Vector3 size, Material m)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube); p.name = n;
            Object.DestroyImmediate(p.GetComponent<Collider>());
            p.transform.SetParent(rifle.transform, false); p.transform.localPosition = pos; p.transform.localScale = size;
            p.GetComponent<Renderer>().sharedMaterial = m;
        }
        // built along +z (muzzle forward), grip at the origin
        Part("Receiver", new Vector3(0, 0.05f, 0.12f), new Vector3(0.05f, 0.08f, 0.38f), dark);
        Part("Barrel", new Vector3(0, 0.07f, 0.48f), new Vector3(0.025f, 0.025f, 0.38f), dark);
        Part("Handguard", new Vector3(0, 0.055f, 0.36f), new Vector3(0.055f, 0.06f, 0.2f), wood);
        Part("Magazine", new Vector3(0, -0.06f, 0.2f), new Vector3(0.035f, 0.16f, 0.07f), dark);
        Part("Grip", new Vector3(0, -0.04f, 0f), new Vector3(0.035f, 0.1f, 0.04f), dark);
        Part("Stock", new Vector3(0, 0.03f, -0.2f), new Vector3(0.045f, 0.09f, 0.26f), wood);
        rifle.transform.SetParent(hand, false);
        // Mixamo right hand: fingers along +y, palm faces +z-ish; point the muzzle along the fingers
        rifle.transform.localRotation = Quaternion.Euler(-90f, 0f, 90f);
        rifle.transform.localPosition = new Vector3(-0.02f, 0.07f, 0.03f);
        // undo the model scale so the rifle keeps real size
        var s = hand.lossyScale.x; if (s > 0) rifle.transform.localScale = Vector3.one / s;
    }

    public static void PlaceInScene(GameObject prefab)
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        foreach (var name in new[] { "Player", "AutoTest" })
        { var old = GameObject.Find(name); if (old) Object.DestroyImmediate(old); }

        // spawn from the web game's data: web (x, z) -> Unity (-x, z); web yaw faces (-sin, -cos)
        var data = JsonUtility.FromJson<Spawns>(File.ReadAllText("Assets/Benghazi/Data/benghazi_corniche_spawns.json"));
        var pos = new Vector3(-data.playerSpawn[0], 0.2f, data.playerSpawn[2]);
        float wx = -Mathf.Sin(data.spawnYaw), wz = -Mathf.Cos(data.spawnYaw);
        float yaw = Mathf.Atan2(-wx, wz) * Mathf.Rad2Deg;
        if (Physics.Raycast(pos + Vector3.up * 50, Vector3.down, out var hit, 100f)) pos.y = hit.point.y + 0.05f;

        var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        player.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));

        var cam = Object.FindFirstObjectByType<Camera>();
        var follow = cam.GetComponent<ThirdPersonCamera>() ?? cam.gameObject.AddComponent<ThirdPersonCamera>();
        follow.target = player.GetComponent<PlayerController>();
        cam.nearClipPlane = 0.1f;
        cam.fieldOfView = 65f;
        var probeCam = cam.transform;
        probeCam.position = pos + Quaternion.Euler(0, yaw, 0) * new Vector3(0.55f, 1.8f, -3.2f);
        probeCam.rotation = Quaternion.Euler(8f, yaw, 0);

        new GameObject("AutoTest").AddComponent<AutoTest>();
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[soldier] player at {pos} yaw {yaw:F0}");
    }

    [System.Serializable] class Spawns { public float[] playerSpawn; public float spawnYaw; }

    public static void BuildWindows()
    {
        var outPath = Path.GetFullPath("../builds/win/Benghazi.exe");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath }, locationPathName = outPath,
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.None,
        });
        Debug.Log($"[soldier] build {report.summary.result} {report.summary.totalSize / 1e6:F0} MB -> {outPath}");
        EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
