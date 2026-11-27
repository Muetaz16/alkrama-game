// Top-down picture of the enemies' walkable map over the city, to check walls and the sea are excluded.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod NavDebug.Run
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

public static class NavDebug
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Corniche.unity");
        Physics.SyncTransforms();
        var tri = NavMesh.CalculateTriangulation();
        Debug.Log($"[nav] triangles {tri.indices.Length / 3}");
        const int N = 1400; const float half = 240f;
        var px = new Color32[N * N];
        // city underneath: height of the first hit from above (darker = higher = building)
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float wx = -half + (x + 0.5f) / N * 2 * half, wz = -half + (y + 0.5f) / N * 2 * half;
            byte v = 40;
            if (Physics.Raycast(new Vector3(wx, 80, wz), Vector3.down, out var h, 120f))
                v = h.point.y < -0.3f ? (byte)25 : h.point.y > 1.5f ? (byte)70 : (byte)150;
            px[y * N + x] = new Color32(v, v, v, 255);
        }
        // walkable triangles in green
        for (int i = 0; i < tri.indices.Length; i += 3)
        {
            Vector2 P(Vector3 q) => new Vector2((q.x + half) / (2 * half) * N, (q.z + half) / (2 * half) * N);
            Vector2 a = P(tri.vertices[tri.indices[i]]), b = P(tri.vertices[tri.indices[i + 1]]), c = P(tri.vertices[tri.indices[i + 2]]);
            int x0 = Mathf.Max(0, (int)Mathf.Min(a.x, b.x, c.x)), x1 = Mathf.Min(N - 1, (int)Mathf.Max(a.x, b.x, c.x) + 1);
            int y0 = Mathf.Max(0, (int)Mathf.Min(a.y, b.y, c.y)), y1 = Mathf.Min(N - 1, (int)Mathf.Max(a.y, b.y, c.y) + 1);
            float yAvg = (tri.vertices[tri.indices[i]].y + tri.vertices[tri.indices[i + 1]].y + tri.vertices[tri.indices[i + 2]].y) / 3;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y), d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y), d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
                if ((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)) continue;
                var o = px[y * N + x];
                px[y * N + x] = yAvg > 1.5f ? new Color32(220, 60, 60, 255) : new Color32((byte)(o.r / 3), (byte)Mathf.Min(255, o.g / 2 + 120), (byte)(o.b / 3), 255);   // red = walkable up on a roof
            }
        }
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false); tex.SetPixels32(px); tex.Apply();
        var outDir = HdrpSetup.ShotsDir();
        File.WriteAllBytes(Path.Combine(outDir, "navmesh_top.png"), tex.EncodeToPNG());
        Debug.Log("[nav] saved navmesh_top.png");
    }
}
