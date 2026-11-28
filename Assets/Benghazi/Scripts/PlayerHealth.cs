using UnityEngine;

// The player's life: enemy fire hurts, staying out of fire heals (after 5 s), 0 = dead and back to the start
// after a few seconds. The sea drowns you: chest-deep water starts a breath bar (8 s), then health drains.
// Also keeps the score: kills this life, and the best run ever (saved on the PC between sessions).
public class PlayerHealth : MonoBehaviour
{
    public float maxHealth = 100, regenDelay = 5f, regenRate = 14f;
    public float waterLevel = -0.22f, breathSeconds = 8f, drownDamage = 22f;
    public bool invulnerable;                       // the automatic test turns enemy damage off

    public float Health { get; private set; }
    public bool Dead { get; private set; }
    public float Breath { get; private set; }      // 1 = full .. 0 = drowning
    public bool InWater { get; private set; }
    public int Kills { get; private set; }
    public int Best { get; private set; }
    public int Deaths { get; private set; }
    public string LastDeath { get; private set; } = "";

    PlayerController pc; WeaponController wc; CharacterController cc; Camera cam;
    Vector3 spawnPos; Quaternion spawnRot;
    float lastHurt = -99, hurtFlash, respawnAt, feedUntil; string feed = ""; Vector3 hurtFrom;
    Texture2D vignette;

    void Start()
    {
        pc = GetComponent<PlayerController>(); wc = GetComponent<WeaponController>(); cc = GetComponent<CharacterController>();
        cam = Camera.main;
        Health = maxHealth; Breath = 1;
        spawnPos = transform.position; spawnRot = transform.rotation;
        Best = PlayerPrefs.GetInt("benghazi_best_kills", 0);
        vignette = Vignette(256);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Dead)
        {
            if (Time.time >= respawnAt) Respawn();
            return;
        }
        // the sea: water up to the chest (or deeper) uses up your breath, then you drown
        float depth = waterLevel - transform.position.y;
        InWater = depth > 0.95f;
        // he swims: breath is only used with the head under (pulled down, or dead weight); on the surface it comes back
        bool under = InWater && Camera.main && Camera.main.transform.position.y < waterLevel - 0.02f && !(pc && pc.Swimming);
        if (under) Breath = Mathf.Max(0, Breath - dt / breathSeconds);
        else Breath = Mathf.Min(1, Breath + dt / 3f);
        if (InWater && Breath <= 0) Hurt(drownDamage * dt, transform.position, "drowned", true);

        if (Time.time - lastHurt > regenDelay && Health < maxHealth) Health = Mathf.Min(maxHealth, Health + regenRate * dt);
        hurtFlash = Mathf.Max(0, hurtFlash - dt * 1.6f);
    }

    public void Damage(float amount, Vector3 from) => Hurt(amount, from, "shot", false);

    void Hurt(float amount, Vector3 from, string cause, bool always)
    {
        if (Dead || (invulnerable && !always)) return;
        Health -= amount; lastHurt = Time.time; hurtFrom = from;
        if (cause != "drowned") hurtFlash = Mathf.Min(1, hurtFlash + 0.45f);
        if (Health <= 0) Die(cause);
    }

    void Die(string cause)
    {
        Dead = true; Health = 0; Deaths++; LastDeath = cause;
        respawnAt = Time.time + 4f;
        if (Kills > Best) { Best = Kills; PlayerPrefs.SetInt("benghazi_best_kills", Best); PlayerPrefs.Save(); }
        if (pc) pc.frozen = true; if (wc) wc.frozen = true;
    }

    void Respawn()
    {
        cc.enabled = false; transform.SetPositionAndRotation(spawnPos, spawnRot); cc.enabled = true;
        Health = maxHealth; Breath = 1; Dead = false; Kills = 0; hurtFlash = 0; lastHurt = -99;
        if (pc) pc.frozen = false; if (wc) wc.frozen = false;
    }

    public void AddKill(bool headshot)
    {
        Kills++;
        feed = headshot ? "HEADSHOT  +1" : "ENEMY DOWN  +1"; feedUntil = Time.time + 2.2f;
        if (Kills > Best) { Best = Kills; PlayerPrefs.SetInt("benghazi_best_kills", Best); PlayerPrefs.Save(); feed += "   NEW RECORD"; }
    }

    static Texture2D Vignette(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
            t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Pow(Mathf.Clamp01((d - 0.62f) * 1.6f), 1.6f)));   // edges and corners only
        }
        t.Apply(); return t;
    }

    void OnGUI()
    {
        float w = Screen.width, h = Screen.height; var white = Texture2D.whiteTexture;
        // under water: blue wash
        if (cam && cam.transform.position.y < waterLevel) { GUI.color = new Color(0.05f, 0.3f, 0.42f, 0.55f); GUI.DrawTexture(new Rect(0, 0, w, h), white); }
        // hurt: red edges, stronger when badly wounded
        float low = Mathf.Clamp01(1 - Health / (maxHealth * 0.5f)) * 0.7f;
        float red = Mathf.Max(hurtFlash * 0.8f, Dead ? 0.9f : low);
        if (red > 0.01f) { GUI.color = new Color(0.7f, 0.02f, 0.02f, red); GUI.DrawTexture(new Rect(0, 0, w, h), vignette); }

        // where the shots come from: a red mark round the cross-hair, pointing at the shooter
        if (hurtFlash > 0.05f && !Dead && cam && (hurtFrom - transform.position).sqrMagnitude > 1f)
        {
            var to = hurtFrom - cam.transform.position; to.y = 0; var fw = cam.transform.forward; fw.y = 0;
            float ang = Vector3.SignedAngle(fw, to, Vector3.up);
            var mtx = GUI.matrix; GUIUtility.RotateAroundPivot(ang, new Vector2(w / 2, h / 2));
            GUI.color = new Color(1f, 0.15f, 0.1f, Mathf.Clamp01(hurtFlash * 1.6f));
            GUI.DrawTexture(new Rect(w / 2 - 45, h / 2 - h * 0.2f, 90, 9), white);
            GUI.DrawTexture(new Rect(w / 2 - 22, h / 2 - h * 0.2f - 7, 44, 7), white);
            GUI.matrix = mtx;
        }
        var big = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.03f), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
        var mid = new GUIStyle(big) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(h * 0.034f) };
        var small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.02f), fontStyle = FontStyle.Bold };
        // score, top right
        Label(new Rect(w - 520, 16, 500, 50), $"KILLS  {Kills}      BEST  {Best}", big, Color.white);
        // health bar, bottom left
        float bw = w * 0.2f, bx = 22, by = h - 110;
        GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(new Rect(bx - 2, by - 2, bw + 4, 18), white);
        GUI.color = Color.Lerp(new Color(0.85f, 0.12f, 0.1f), new Color(0.3f, 0.8f, 0.3f), Health / maxHealth);
        GUI.DrawTexture(new Rect(bx, by, bw * Mathf.Clamp01(Health / maxHealth), 14), white);
        Label(new Rect(bx + bw + 10, by - 6, 200, 30), $"{Mathf.CeilToInt(Health)}", small, Color.white);
        // breath bar while in the water
        if (Breath < 0.999f)
        {
            float cw = w * 0.24f, cx = (w - cw) / 2, cy = h * 0.74f;
            GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(new Rect(cx - 2, cy - 2, cw + 4, 16), white);
            GUI.color = Breath > 0.3f ? new Color(0.35f, 0.7f, 1f) : new Color(1f, 0.35f, 0.25f);
            GUI.DrawTexture(new Rect(cx, cy, cw * Breath, 12), white);
            Label(new Rect(cx, cy - 34, cw, 30), Breath > 0 ? "BREATH" : "DROWNING", new GUIStyle(small) { alignment = TextAnchor.MiddleCenter }, Color.white);
        }
        if (Time.time < feedUntil) Label(new Rect(0, h * 0.3f, w, 50), feed, mid, new Color(1f, 0.85f, 0.3f));
        if (Dead)
        {
            Label(new Rect(0, h * 0.4f, w, 60), LastDeath == "drowned" ? "YOU DROWNED" : "YOU WERE KILLED", new GUIStyle(mid) { fontSize = Mathf.RoundToInt(h * 0.06f) }, Color.white);
            Label(new Rect(0, h * 0.4f + h * 0.08f, w, 50), $"best run: {Best} kills    back in {Mathf.Max(0, respawnAt - Time.time):F0}", mid, Color.white);
        }
        GUI.color = Color.white;
    }

    static void Label(Rect r, string text, GUIStyle style, Color c)
    {
        GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
        GUI.color = c; GUI.Label(r, text, style);
    }
}
