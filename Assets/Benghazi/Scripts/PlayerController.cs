using UnityEngine;
using UnityEngine.InputSystem;

// Third-person shooter movement, same feel as the web prototype:
// WASD / left stick move relative to the camera, the soldier always faces where the camera looks,
// Shift = sprint (forward), Ctrl/Alt = walk, C = crouch, Space = jump.
// Speeds match the mocap clips (m/s) so the feet don't slide; the animator blends by local velocity.
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public const float WalkSpeed = 1.92f, RunSpeed = 4.8f, SprintSpeed = 7.2f, CrouchSpeed = 2.04f;
    public float acceleration = 14f, jumpHeight = 1.1f, gravity = -22f, turnSpeed = 720f;

    [HideInInspector] public float yaw;          // look direction, set by the camera
    [HideInInspector] public Vector2 moveInput;  // -1..1, from keyboard/stick (or AutoTest)
    [HideInInspector] public bool sprint, walk, crouch, jumpPressed;
    public bool externalInput;                   // AutoTest / touch controls drive the inputs

    CharacterController cc;
    Animator anim;
    Vector3 velocity;        // horizontal, world space
    float vy;
    bool wasGrounded = true;
    static readonly int VelX = Animator.StringToHash("VelX"), VelZ = Animator.StringToHash("VelZ"),
        Grounded = Animator.StringToHash("Grounded"), Crouch = Animator.StringToHash("Crouch"), Jump = Animator.StringToHash("Jump");

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        anim = GetComponentInChildren<Animator>();
        yaw = transform.eulerAngles.y;
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        float dt = Time.deltaTime;

        // target speed for this gait
        float speed = crouch ? CrouchSpeed : walk ? WalkSpeed : RunSpeed;
        var input = Vector2.ClampMagnitude(moveInput, 1f);
        bool sprinting = sprint && !crouch && input.y > 0.5f;
        if (sprinting) speed = SprintSpeed;

        var look = Quaternion.Euler(0, yaw, 0);
        var wish = look * new Vector3(input.x, 0, input.y) * speed;
        velocity = Vector3.MoveTowards(velocity, wish, acceleration * speed * 0.35f * dt);

        // gravity and jumping
        if (cc.isGrounded && vy < 0) vy = -2f;
        if (jumpPressed && cc.isGrounded && !crouch)
        {
            vy = Mathf.Sqrt(2f * -gravity * jumpHeight);
            anim?.SetTrigger(Jump);
        }
        jumpPressed = false;
        vy += gravity * dt;
        cc.Move((velocity + Vector3.up * vy) * dt);

        // face the camera direction (strafe shooter); sprinting turns into the run direction a bit
        var face = yaw;
        if (sprinting && velocity.sqrMagnitude > 1f) face = Mathf.LerpAngle(yaw, Quaternion.LookRotation(velocity).eulerAngles.y, 0.35f);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0, face, 0), turnSpeed * dt);

        if (anim)
        {
            var local = transform.InverseTransformDirection(velocity);
            anim.SetFloat(VelX, local.x, 0.08f, dt);
            anim.SetFloat(VelZ, local.z, 0.08f, dt);
            anim.SetBool(Grounded, cc.isGrounded);
            anim.SetBool(Crouch, crouch);
        }
        wasGrounded = cc.isGrounded;
    }

    void ReadInput()
    {
        var k = Keyboard.current; var g = Gamepad.current;
        var m = Vector2.zero;
        if (k != null)
        {
            if (k.wKey.isPressed) m.y += 1; if (k.sKey.isPressed) m.y -= 1;
            if (k.dKey.isPressed) m.x += 1; if (k.aKey.isPressed) m.x -= 1;
            sprint = k.leftShiftKey.isPressed;
            walk = k.leftCtrlKey.isPressed || k.leftAltKey.isPressed;
            if (k.cKey.wasPressedThisFrame) crouch = !crouch;
            if (k.spaceKey.wasPressedThisFrame) { jumpPressed = true; crouch = false; }
        }
        if (g != null)
        {
            var s = g.leftStick.ReadValue(); if (s.sqrMagnitude > m.sqrMagnitude) m = s;
            sprint |= g.leftStickButton.isPressed;
            if (g.buttonEast.wasPressedThisFrame) crouch = !crouch;
            if (g.buttonSouth.wasPressedThisFrame) { jumpPressed = true; crouch = false; }
        }
        moveInput = m;
    }
}
