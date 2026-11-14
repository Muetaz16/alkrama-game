using UnityEngine;

// Keeps the streets dangerous: a handful of enemies at the start, and a new one every few seconds while
// fewer than "maxAlive" are left. Each appears at a random street spot 45-150 m from you, preferably
// where you cannot see it happen. The spots come from the map (GameplaySetup fills them in).
public class EnemySpawner : MonoBehaviour
{
    public GameObject enemyPrefab;
    public Vector3[] spots = new Vector3[0];
    public int startCount = 6, maxAlive = 8;
    public float interval = 6f, minDist = 45f, maxDist = 150f;
    public int Spawned { get; private set; }

    Transform player; float next;

    void Start()
    {
        var pc = FindFirstObjectByType<PlayerController>(); if (pc) player = pc.transform;
        for (int i = 0; i < startCount; i++) SpawnOne();
        next = Time.time + interval;
    }

    void Update()
    {
        if (Time.time < next) return;
        next = Time.time + interval;
        if (EnemyAI.All.Count < maxAlive) SpawnOne();
    }

    public EnemyAI SpawnOne()
    {
        if (!enemyPrefab || spots.Length == 0 || !player) return null;
        Vector3 pick = Vector3.zero; bool found = false, hidden = false;
        var eye = player.position + Vector3.up * 1.6f;
        for (int tries = 0; tries < 40 && !hidden; tries++)
        {
            var s = spots[Random.Range(0, spots.Length)];
            float d = Vector3.Distance(s, player.position);
            if (d < minDist || d > maxDist) continue;
            bool crowded = false;
            foreach (var e in EnemyAI.All) if (e && Vector3.Distance(e.transform.position, s) < 18f) { crowded = true; break; }
            if (crowded) continue;
            bool seen = !Physics.Linecast(eye, s + Vector3.up * 1.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (!found || !seen) { pick = s; found = true; hidden = !seen; }
        }
        if (!found) return null;
        var look = player.position - pick; look.y = 0;
        var go = Instantiate(enemyPrefab, pick + Vector3.up * 0.05f, Quaternion.LookRotation(Quaternion.Euler(0, Random.Range(-140f, 140f), 0) * (look.sqrMagnitude > 0.01f ? look.normalized : Vector3.forward)));
        go.name = "Enemy";
        Spawned++;
        return go.GetComponent<EnemyAI>();
    }
}
