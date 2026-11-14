using UnityEngine;

// Anything that can be shot: practice targets now, enemy soldiers next.
public class Damageable : MonoBehaviour
{
    public float health = 100;
    public bool fallsOver = true;          // steel target: knocks back and stands up again
    public float resetAfter = 3f;
    public System.Action<Damageable> onHit;

    float maxHealth, downUntil = -1;
    Quaternion upright;

    void Awake() { maxHealth = health; upright = transform.localRotation; }

    public virtual void Hit(float damage, Vector3 point, Vector3 dir)
    {
        health -= damage;
        onHit?.Invoke(this);
        if (health <= 0 && fallsOver && downUntil < 0)
        {
            downUntil = Time.time + resetAfter;
            var away = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
            var axis = Vector3.Cross(Vector3.up, away);
            transform.rotation = Quaternion.AngleAxis(80, axis) * transform.rotation;
        }
    }

    void Update()
    {
        if (downUntil > 0 && Time.time > downUntil) { downUntil = -1; health = maxHealth; transform.localRotation = upright; }
    }
}
