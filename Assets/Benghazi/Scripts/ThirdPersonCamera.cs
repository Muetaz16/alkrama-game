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

        var rot = Quaternion.Euler(pitch, yaw, 0);
        var pivot = target.transform.position + Quaternion.Euler(0, yaw, 0) * shoulder;
        var want = pivot - rot * Vector3.forward * distance;
        // keep the camera out of walls
        if (Physics.SphereCast(pivot, 0.2f, want - pivot, out var hit, distance, collideWith, QueryTriggerInteraction.Ignore)
            && !hit.collider.transform.IsChildOf(target.transform))
            want = pivot + (want - pivot).normalized * Mathf.Max(0.3f, hit.distance - 0.1f);
        transform.SetPositionAndRotation(want, rot);
    }
}
