using UnityEngine;

// The sky light and reflections come from the skybox; update them once when the scene starts
// (no baked lighting yet), then render the reflection probe for the sea and windows.
public class EnvironmentInit : MonoBehaviour
{
    public ReflectionProbe probe;

    void Start()
    {
        DynamicGI.UpdateEnvironment();
        if (probe) probe.RenderProbe();
    }
}
