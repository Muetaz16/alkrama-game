// Weapons on the player + a practice range of steel targets along the Corniche (20-160 m)
// so shooting and sniping can be tried before the enemy soldiers come in.
using UnityEditor;
using UnityEngine;

public static class GameplaySetup
{
    public static void Run()
    {
        var player = GameObject.Find("Player");
        if (!player.GetComponent<WeaponController>()) player.AddComponent<WeaponController>();
        if (!player.GetComponent<MarkerTool>()) player.AddComponent<MarkerTool>();
        if (!player.GetComponent<PlayerHealth>()) player.AddComponent<PlayerHealth>();
        ClearToyProps();
        BakeNavMesh();
        Enemies(player.transform.position);

        var oldMark = GameObject.Find("LighthouseMarker"); if (oldMark) Object.DestroyImmediate(oldMark);
        new GameObject("LighthouseMarker").transform.position = SceneBuilder.FindLighthouse();
        var old = GameObject.Find("Practice Targets"); if (old) Object.DestroyImmediate(old);
        var root = new GameObject("Practice Targets");
        root.AddComponent<TestOnly>();          // the orange steel figures are for the automated test only: not in a normal game
        var steel = new Material(Shader.Find("Universal Render Pipeline/Lit")); steel.SetColor("_BaseColor", new Color(0.85f, 0.32f, 0.12f)); steel.SetFloat("_Smoothness", 0.35f); steel.SetFloat("_Metallic", 0.4f);
        var post = new Material(Shader.Find("Universal Render Pipeline/Lit")); post.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.2f));
        AssetDatabase.CreateAsset(steel, "Assets/Benghazi/Materials/TargetSteel.mat");
        AssetDatabase.CreateAsset(post, "Assets/Benghazi/Materials/TargetPost.mat");

        var t = player.transform;
        // targets go along the way to the lighthouse (on land), not out over the sea
        var lh = SceneBuilder.FindLighthouse();
        var toLh = Vector3.ProjectOnPlane(lh - t.position, Vector3.up).normalized;
        var right = Vector3.Cross(Vector3.up, toLh);
        int placed = 0;
        var eye = t.position + Vector3.up * 1.6f;
        var spots = new System.Collections.Generic.List<Vector3>();
        // fan out from the soldier towards the lighthouse side; keep only spots he can actually see
        foreach (var dist in new[] { 20f, 30f, 45f, 60f, 80f, 100f, 130f, 160f })
        foreach (var turn in new[] { 0f, -18f, 18f, -35f, 35f, -55f, 55f })
        {
            var dir = Quaternion.Euler(0, turn, 0) * toLh;
            var pos = t.position + dir * dist;
            if (!Physics.Raycast(pos + Vector3.up * 40, Vector3.down, out var hit, 80f) || hit.point.y < -0.1f) continue;   // water / sea floor
            pos = hit.point;
            if (Physics.CheckBox(pos + Vector3.up * 1.1f, new Vector3(0.5f, 0.9f, 0.5f))) continue;
            if (Physics.Linecast(eye, pos + Vector3.up * 1.0f)) continue;                                   // something in the way
            if (spots.Exists(o => Vector3.Distance(o, pos) < 12f)) continue;
            spots.Add(pos);
            break;
        }
        foreach (var pos in spots)
        {
            float dist = Vector3.Distance(pos, t.position);
            var tg = new GameObject($"Target {dist:F0}m");
            tg.transform.SetParent(root.transform);
            tg.transform.position = pos;
            tg.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(t.position - pos, Vector3.up));
            var swing = new GameObject("Plate").transform; swing.SetParent(tg.transform, false); swing.localPosition = new Vector3(0, 0.35f, 0);
            Part(swing, PrimitiveType.Cube, new Vector3(0, 0.55f, 0), new Vector3(0.5f, 0.75f, 0.03f), steel);    // torso
            Part(swing, PrimitiveType.Sphere, new Vector3(0, 1.1f, 0), new Vector3(0.24f, 0.26f, 0.04f), steel);  // head
            Part(tg.transform, PrimitiveType.Cylinder, new Vector3(0, 0.2f, 0.04f), new Vector3(0.05f, 0.2f, 0.05f), post, false);
            var d = swing.gameObject.AddComponent<Damageable>(); d.health = 100;
            placed++;
        }
        Debug.Log($"[gameplay] weapons on player, {placed} practice targets");
    }

    // re-bake only the walkable map (and the spawn spots that depend on it)
    // The web city's street props include olive-green oil barrels (flat colour 0.27 / 0.34 / 0.18, 70 cm across,
    // 95 cm tall, each under a node of its own). The owner asked for them to go: the barrel's node is switched off,
    // with its collider. Found by that colour and size. Safe to run again.
    static void ClearToyProps()
    {
        var city = GameObject.Find("Benghazi Corniche"); if (!city) return;
        int n = 0, back = 0;
        foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
        {
            var m = r.sharedMaterial; if (!m || !m.HasProperty("baseColorFactor")) continue;
            var c = m.GetColor("baseColorFactor");
            // an earlier version of this switched off the small bright-green props instead: those come back
            bool bright = Mathf.Abs(c.r - 0.15f) < 0.05f && Mathf.Abs(c.g - 0.68f) < 0.06f && Mathf.Abs(c.b - 0.38f) < 0.06f;
            if (bright && !r.gameObject.activeSelf) { r.gameObject.SetActive(true); back++; continue; }
            bool olive = Mathf.Abs(c.r - 0.27f) < 0.03f && Mathf.Abs(c.g - 0.34f) < 0.03f && Mathf.Abs(c.b - 0.18f) < 0.03f;
            if (!olive || !r.gameObject.activeInHierarchy) continue;
            var s = r.bounds.size; if (s.y < 0.7f || s.y > 1.3f || Mathf.Max(s.x, s.z) > 1.1f) continue;
            // the barrel's own node, if that holds nothing but the barrel
            var node = r.transform.parent && r.transform.parent != city.transform && r.transform.parent.childCount <= 4 ? r.transform.parent.gameObject : r.gameObject;
            node.SetActive(false); n++;
        }
        Debug.Log($"[gameplay] {n} olive-green barrels of the web city switched off ({back} small props restored)");
    }

    // only the prop clean-up, on the saved scene:  -executeMethod GameplaySetup.PropsOnly
    public static void PropsOnly()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        ClearToyProps();
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
    }

    public static void NavOnly()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        var player = GameObject.Find("Player");
        BakeNavMesh(); Enemies(player.transform.position);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
    }

    // walkable map for the enemies: everything with a collider, the sea marked unwalkable (one box per water strip)
    static void BakeNavMesh()
    {
        var old = GameObject.Find("NavMesh"); if (old) Object.DestroyImmediate(old);
        var go = new GameObject("NavMesh");
        foreach (var st in SceneBuilder.ParseSea())
        {
            var v = new GameObject("sea").AddComponent<Unity.AI.Navigation.NavMeshModifierVolume>();
            v.transform.SetParent(go.transform, false);
            v.center = new Vector3(-(st[0] + st[1]) / 2, -9f, (st[2] + st[3]) / 2);   // web x -> Unity -x; down to the deepest sea floor
            v.size = new Vector3(Mathf.Abs(st[1] - st[0]) + 0.6f, 19f, Mathf.Abs(st[3] - st[2]) + 0.6f);
            v.area = 1;                                                             // Not Walkable
        }
        var surf = go.AddComponent<Unity.AI.Navigation.NavMeshSurface>();
        surf.collectObjects = Unity.AI.Navigation.CollectObjects.All;
        surf.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
        surf.layerMask = ~((1 << 2) | (1 << 21));                                   // not "ignore raycast" (cases) or flat road marks
        surf.overrideVoxelSize = true; surf.voxelSize = 0.17f;
        surf.overrideTileSize = true; surf.tileSize = 128;
        surf.minRegionArea = 6f;
        var player = GameObject.Find("Player"); if (player) player.SetActive(false);   // don't carve a hole where he stands
        var t = System.Diagnostics.Stopwatch.StartNew();
        surf.BuildNavMesh();
        if (player) player.SetActive(true);
        const string path = "Assets/Benghazi/NavMesh.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(surf.navMeshData, path);
        surf.navMeshData = AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>(path);
        var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
        Debug.Log($"[gameplay] navmesh baked in {t.Elapsed.TotalSeconds:F0} s: {tri.indices.Length / 3} triangles, sea volumes {go.transform.childCount}");
    }

    // random enemy soldiers: the street spots come from the map; the spawner picks among them while you play
    static void Enemies(Vector3 playerPos)
    {
        var old = GameObject.Find("Enemy Spawner"); if (old) Object.DestroyImmediate(old);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierSetup.EnemyPrefabPath);
        if (!prefab) { Debug.LogWarning("[gameplay] no enemy prefab - run SoldierSetup.Run first"); return; }
        var data = (System.Collections.Generic.Dictionary<string, object>)MiniJson.Parse(System.IO.File.ReadAllText("Assets/Benghazi/Data/benghazi_corniche_spawns.json"));
        var spots = new System.Collections.Generic.List<Vector3>();
        Physics.SyncTransforms();
        foreach (System.Collections.Generic.List<object> s in (System.Collections.Generic.List<object>)data["enemySpawns"])
        {
            var p = new Vector3(-System.Convert.ToSingle(s[0]), 0, System.Convert.ToSingle(s[1]));     // web x -> Unity -x
            if (!Physics.Raycast(p + Vector3.up * 40, Vector3.down, out var hit, 80f) || hit.point.y < -0.05f || hit.point.y > 0.5f) continue;   // street level only
            if (Physics.CheckCapsule(hit.point + Vector3.up * 0.6f, hit.point + Vector3.up * 1.6f, 0.45f)) continue;                             // room to stand
            if (Vector3.Distance(hit.point, playerPos) < 30f) continue;
            if (!UnityEngine.AI.NavMesh.SamplePosition(hit.point, out var nh, 1.2f, UnityEngine.AI.NavMesh.AllAreas)) continue;   // on the walkable map
            hit.point = nh.position;
            spots.Add(hit.point);
        }
        var sp = new GameObject("Enemy Spawner").AddComponent<EnemySpawner>();
        sp.enemyPrefab = prefab; sp.spots = spots.ToArray();
        Debug.Log($"[gameplay] enemy spawner with {spots.Count} street spots");
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material m, bool keepCollider = true)
    {
        var p = GameObject.CreatePrimitive(type);
        if (!keepCollider) Object.DestroyImmediate(p.GetComponent<Collider>());
        p.transform.SetParent(parent, false); p.transform.localPosition = pos; p.transform.localScale = scale;
        p.GetComponent<Renderer>().sharedMaterial = m;
    }
}
