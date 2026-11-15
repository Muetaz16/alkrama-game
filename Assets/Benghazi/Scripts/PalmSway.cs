using UnityEngine;

// Palm crown moving in the sea breeze: a slow lean with gusts, each palm a little out of step.
public class PalmSway : MonoBehaviour
{
    public float amount = 1.6f, speed = 0.55f;
    Quaternion rest; float phase;

    void Start() { rest = transform.localRotation; phase = Random.value * 50f; }

    void Update()
    {
        float t = Time.time * speed + phase;
        float gust = 0.6f + 0.4f * Mathf.PerlinNoise(t * 0.15f, phase);
        float x = (Mathf.Sin(t) * 0.7f + Mathf.Sin(t * 2.3f + 1.3f) * 0.3f) * amount * gust;
        float z = (Mathf.Sin(t * 0.8f + 2f) * 0.6f + Mathf.Sin(t * 1.9f) * 0.25f) * amount * gust;
        transform.localRotation = rest * Quaternion.Euler(x, 0, z);
    }
}
