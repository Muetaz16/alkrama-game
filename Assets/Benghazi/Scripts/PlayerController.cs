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
    [HideInInspector] public float combatUntil;  // the weapon keeps the body facing the crosshair until then
    public bool combat { get; private set; }
    public float Speed { get; private set; }
    public bool Sprinting { get; private set; }
    Transform model; float lean;

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
        model = anim ? anim.transform : null;
        yaw = transform.eulerAngles.y;
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        float dt = Time.deltaTime;

        // two ways of moving, like modern shooters:
        //  combat (aiming / just fired / crouched): body faces the crosshair, strafes and backpedals
        //  free-run: body turns to face where it runs, so it always uses the forward run/sprint cycles
        combat = combatUntil > Time.time || crouch;
        float speed = crouch ? CrouchSpeed : walk ? WalkSpeed : RunSpeed;
        var input = Vector2.ClampMagnitude(moveInput, 1f);
        bool sprinting = sprint && !crouch && (combat ? input.y > 0.5f : input.magnitude > 0.5f);
        if (sprinting) { speed = SprintSpeed; combatUntil = 0; combat = crouch; }

        var look = Quaternion.Euler(0, yaw, 0);
        var wish = look * new Vector3(input.x, 0, input.y) * speed;
        // quick to start, a little slower to stop and to change direction at speed (weight)
        float rate = wish.sqrMagnitude > velocity.sqrMagnitude ? 2.2f : 3.2f;
        if (Vector3.Dot(wish, velocity) < 0) rate = 4.5f;
        velocity = Vector3.MoveTowards(velocity, wish, rate * Mathf.Max(speed, RunSpeed) * dt);
        Speed = velocity.magnitude; Sprinting = sprinting && Speed > RunSpeed + 0.5f;

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

        // facing: crosshair in combat, run direction otherwise (stand still = keep facing)
        float before = transform.eulerAngles.y;
        if (combat) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0, yaw, 0), turnSpeed * dt);
        else if (velocity.sqrMagnitude > 0.3f)
        {
            var target = Quaternion.LookRotation(new Vector3(velocity.x, 0, velocity.z));
            float turnRate = Mathf.Lerp(720f, 380f, Mathf.InverseLerp(RunSpeed, SprintSpeed, Speed));   // wider turns at sprint
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnRate * dt);
        }
        // lean into turns
        float turn = Mathf.DeltaAngle(before, transform.eulerAngles.y) / Mathf.Max(dt, 1e-4f);
        lean = Mathf.Lerp(lean, Mathf.Clamp(-turn * 0.03f * Mathf.InverseLerp(1f, SprintSpeed, Speed), -12f, 12f), 1 - Mathf.Exp(-8 * dt));
        if (model) model.localRotation = Quaternion.Euler(0, 0, lean);

        if (anim)
        {
            var local = transform.InverseTransformDirection(velocity);
            anim.SetFloat(VelX, local.x, 0.12f, dt);
            anim.SetFloat(VelZ, local.z, 0.12f, dt);
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
