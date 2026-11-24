using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// A dead fighter's rifle falls out of his hands and can be taken (G): what is in it is whatever he had
// left - a part-used magazine and a random number of spare ones. The player already carries an AK, so the
// rifle itself is stripped: the rounds go into the reserve.
// Nothing in the scene refers to this: it starts itself and watches EnemyAI.All for men who have died.
public class WeaponPickups : MonoBehaviour
{
    public const float Reach = 2.4f, Lifetime = 90f;
    public const int MaxReserve = 360;

    public class Drop { public Transform gun; public int magRounds, spareMags, magSize; public float until; public int Rounds => magRounds + spareMags * magSize; }
    public static readonly List<Drop> Drops = new();
    public static int Taken { get; private set; }          // rifles picked up so far
    public static int LastRounds { get; private set; }

    static WeaponPickups inst;
    readonly List<EnemyAI> known = new();
    PlayerController player; WeaponController gun; CharacterController cc;
    float nextScan, toastUntil; string toast = "";
    GUIStyle style, small;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Drops.Clear(); Taken = 0; LastRounds = 0;
        if (!FindFirstObjectByType<PlayerController>()) return;
        inst = new GameObject("Weapon pickups").AddComponent<WeaponPickups>();
    }

    void Start()
    {
        player = FindFirstObjectByType<PlayerController>();
        if (player) { gun = player.GetComponent<WeaponController>(); cc = player.GetComponent<CharacterController>(); }
    }

    void Update()
    {
        if (!player || !gun) return;
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.15f;
            // a man we knew who is dead now: his rifle becomes a pickup
            for (int i = known.Count - 1; i >= 0; i--)
            {
                var e = known[i];
                if (!e) { known.RemoveAt(i); continue; }
                if (e.IsDead) { known.RemoveAt(i); DropRifle(e); }
            }
            foreach (var e in EnemyAI.All) if (e && !e.IsDead && !known.Contains(e)) known.Add(e);
            for (int i = Drops.Count - 1; i >= 0; i--)
                if (!Drops[i].gun || Time.time > Drops[i].until) { if (Drops[i].gun) Destroy(Drops[i].gun.gameObject); Drops.RemoveAt(i); }
        }
        var k = Keyboard.current; var g = Gamepad.current;
        bool press = (k != null && k.gKey.wasPressedThisFrame) || (g != null && g.dpad.down.wasPressedThisFrame);
        if (press && !gun.externalInput) TakeNearest();
    }

    void DropRifle(EnemyAI e)
    {
        Transform rifle = null;
        foreach (var t in e.GetComponentsInChildren<Transform>(true)) if (t.name == "Rifle") { rifle = t; break; }
        if (!rifle || !rifle.gameObject.activeInHierarchy) return;
        var rends = rifle.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return;
        rifle.SetParent(null, true);
        // the muzzle flash and anything else switched off stays off; the rifle is only a thing on the ground now
        var b = new Bounds(rifle.InverseTransformPoint(rends[0].bounds.center), Vector3.zero);
        foreach (var r in rends)
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            var c = r.bounds.center; var x = r.bounds.extents;
            for (int i = 0; i < 8; i++) b.Encapsulate(rifle.InverseTransformPoint(c + new Vector3((i & 1) != 0 ? x.x : -x.x, (i & 2) != 0 ? x.y : -x.y, (i & 4) != 0 ? x.z : -x.z)));
        }
        foreach (var t in rifle.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;       // bullets go through it
        var col = rifle.gameObject.AddComponent<BoxCollider>(); col.center = b.center;
        col.size = new Vector3(Mathf.Clamp(b.size.x, 0.03f, 0.12f), Mathf.Clamp(b.size.y, 0.05f, 0.3f), Mathf.Clamp(b.size.z, 0.3f, 1.1f));
        if (cc) Physics.IgnoreCollision(col, cc);
        var rb = rifle.gameObject.AddComponent<Rigidbody>(); rb.mass = 3.5f; rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.linearVelocity = e.transform.forward * Random.Range(0.3f, 1.1f) + Vector3.up * Random.Range(0.4f, 1.2f);
        rb.angularVelocity = Random.insideUnitSphere * 3f;
        int size = gun.Get(0).magSize;
        Drops.Add(new Drop { gun = rifle, magSize = size, magRounds = Random.Range(3, size + 1), spareMags = Random.Range(0, 4), until = Time.time + Lifetime });
    }

    static Drop Nearest(Vector3 from, float reach)
    {
        Drop best = null; float bd = reach;
        foreach (var d in Drops) { if (!d.gun) continue; float dist = Vector3.Distance(d.gun.position, from); if (dist < bd) { bd = dist; best = d; } }
        return best;
    }

    // take the nearest rifle within reach: its rounds go into the AK's reserve. Returns the rounds gained (0 = nothing there)
    public static int TakeNearest()
    {
        if (!inst || !inst.gun) return 0;
        var d = Nearest(inst.player.transform.position + Vector3.up * 0.8f, Reach);
        if (d == null) return 0;
        var ak = inst.gun.Get(0);
        int room = Mathf.Max(0, MaxReserve - ak.reserve), got = Mathf.Min(room, d.Rounds);
        ak.reserve += got; LastRounds = got; Taken++;
        inst.toast = got > 0 ? $"+{got} rounds  ({d.spareMags} mag{(d.spareMags == 1 ? "" : "s")} + {d.magRounds} in the rifle)" : "ammo full";
        inst.toastUntil = Time.time + 2.5f;
        Drops.Remove(d); Destroy(d.gun.gameObject);
        return got;
    }

    void OnGUI()
    {
        if (!player) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            small = new GUIStyle(style) { fontSize = 20 };
        }
        float w = Screen.width, h = Screen.height;
        var d = Nearest(player.transform.position + Vector3.up * 0.8f, Reach);
        if (d != null) Label(new Rect(w / 2 - 400, h * 0.62f, 800, 40), $"G  ·  pick up AK-47  ({d.spareMags} mag{(d.spareMags == 1 ? "" : "s")} + {d.magRounds} rounds)", style, Color.white);
        if (Time.time < toastUntil) Label(new Rect(w / 2 - 400, h * 0.68f, 800, 36), toast, small, new Color(1f, 0.9f, 0.55f));
    }

    static void Label(Rect r, string text, GUIStyle s, Color c)
    {
        var o = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.75f); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, s);
        GUI.color = c; GUI.Label(r, text, s);
        GUI.color = o;
    }
}
