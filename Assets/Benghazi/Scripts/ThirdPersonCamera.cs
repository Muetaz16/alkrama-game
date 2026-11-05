using UnityEngine;
using UnityEngine.InputSystem;

// Over-the-shoulder camera: mouse / right stick turn it, the player faces where it looks.
// Pulls in when a wall is between the camera and the soldier.
public class ThirdPersonCamera : MonoBehaviour
{
    public PlayerController target;
    public Vector3 shoulder = new Vector3(0.55f, 1.6f, 0f);
    public float distance = 3.2f, mouseSensitivity = 0.12f, stickSensitivity = 160f;
    public float pitch = 8f, minPitch = -60f, maxPitch = 70f;
    public LayerMask collideWith = ~0;
    public bool externalInput;
    [HideInInspector] public Vector2 lookDelta;   // degrees, for AutoTest / touch

    [HideInInspector] public float yaw;
    [HideInInspector] public bool aimingFov;      // the weapon owns the field of view while aiming
    [HideInInspector] public float baseFov = 65f;
    float sprintFov, roll, startLean;

    void Start()
    {
        if (target) yaw = target.yaw;
        if (!externalInput && Application.isFocused) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    }

    void LateUpdate()
    {
        if (!target) return;
        var d = lookDelta; lookDelta = Vector2.zero;
        if (!externalInput)
        {
            if (Mouse.current != null) d += Mouse.current.delta.ReadValue() * mouseSensitivity;
            if (Gamepad.current != null) d += Gamepad.current.rightStick.ReadValue() * stickSensitivity * Time.deltaTime;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            // click in the window to grab the mouse again
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            if (Cursor.lockState != CursorLockMode.Locked && Mouse.current != null) d -= Mouse.current.delta.ReadValue() * mouseSensitivity;   // don't spin while the mouse is free
        }
        yaw += d.x;
        pitch = Mathf.Clamp(pitch - d.y, minPitch, maxPitch);
        target.yaw = yaw;

        // same camera feel as the web game: head bob synced to the steps, sprint widens the view
        // (+8, tactical +14), a little roll into strafes, a lean on start/stop and a dip on landing
        float dt = Time.deltaTime;
        float sp = target.Speed, runK = Mathf.Clamp01(sp / PlayerController.SprintMove);
        float bobK = Mathf.Clamp01(sp / PlayerController.JogSpeed) * (aimingFov ? 0.15f : 1f);
        float ph = target.StepPhase * 2f;                                  // two steps per locomotion cycle
        var bob = new Vector3(Mathf.Sin(ph) * (0.025f + 0.02f * runK), -Mathf.Abs(Mathf.Sin(ph)) * (0.03f + 0.05f * runK), 0) * bobK;
        sprintFov = Mathf.Lerp(sprintFov, target.Sprinting ? (target.Tactical ? 14f : 8f) : 0f, 1 - Mathf.Exp(-6 * dt));
        var cam = GetComponent<Camera>();
        if (cam && !aimingFov) cam.fieldOfView = baseFov + sprintFov;
        roll = Mathf.Lerp(roll, -target.Strafe * 2.2f, 1 - Mathf.Exp(-6 * dt));
        startLean = Mathf.Lerp(startLean, Mathf.Clamp(target.AccelForward * 0.12f, -2.5f, 2.5f), 1 - Mathf.Exp(-8 * dt));
        var rot = Quaternion.Euler(pitch + startLean, yaw, roll);
        var pivot = target.transform.position + Vector3.up * target.LandDip + Quaternion.Euler(0, yaw, 0) * (shoulder + bob);
        var want = pivot - rot * Vector3.forward * distance;
        // keep the camera out of walls
        if (Physics.SphereCast(pivot, 0.2f, want - pivot, out var hit, distance, collideWith, QueryTriggerInteraction.Ignore)
            && !hit.collider.transform.IsChildOf(target.transform))
            want = pivot + (want - pivot).normalized * Mathf.Max(0.3f, hit.distance - 0.1f);
        transform.SetPositionAndRotation(want, rot);
    }
}
