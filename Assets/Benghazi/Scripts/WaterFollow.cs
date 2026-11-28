using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// The HDRP ocean draws its foam (the surf along the shore: water decals written by the editor's CinematicLook)
// inside a square region. This keeps that square centred on the camera, so wherever the player walks along the
// seafront the surf near him is drawn. Added to the "Ocean" object by CinematicLook.
[RequireComponent(typeof(WaterSurface))]
public class WaterFollow : MonoBehaviour
{
    Transform anchor; Camera cam; WaterSurface water;

    void Start()
    {
        water = GetComponent<WaterSurface>();
        anchor = new GameObject("Foam region").transform;
        water.decalRegionAnchor = anchor;
    }

    void LateUpdate()
    {
        if (!cam) cam = Camera.main;
        if (!cam || !anchor) return;
        // in 16 m steps: the foam texture is not re-laid every frame the camera moves
        var p = cam.transform.position;
        anchor.position = new Vector3(Mathf.Round(p.x / 16f) * 16f, transform.position.y, Mathf.Round(p.z / 16f) * 16f);
    }
}
