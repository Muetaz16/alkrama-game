using UnityEngine;
using UnityEngine.InputSystem;

// Third-person shooter movement, tuned to feel like the web prototype (main.js):
// the soldier always faces where the camera looks (strafe / backpedal with the 8-way mocap),
// jog 4.2 m/s, Shift = sprint 7.5 (forward only), double-tap Shift = tactical sprint 9.5,
// aiming 2.4, crouch 2.3. Momentum eases toward the wanted speed (accel 11, decel 14).
// Above the sprint clip's own speed the animator plays faster so the feet stay planted.
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    // clip speeds (m/s) the blend tree is laid out with
    public const float WalkSpeed = 1.92f, RunSpeed = 4.8f, SprintSpeed = 7.2f, CrouchSpeed = 2.04f;
    // movement speeds (from the web game)
    public const float JogSpeed = 4.2f, SprintMove = 7.5f, TacticalMove = 9.5f, AimMove = 2.4f, CrouchMove = 2.3f, CrouchAimMove = 1.6f;
    public float accel = 11f, decel = 14f, airControl = 1.5f, jumpHeight = 1.1f, gravity = -22f, turnSpeed = 900f;

    [HideInInspector] public float yaw;          // look direction, set by the camera
    [HideInInspector] public Vector2 moveInput;  // -1..1, from keyboard/stick (or AutoTest)
    [HideInInspector] public bool sprint, walk, crouch, jumpPressed, aiming;
    [HideInInspector] public float leanInput;      // -1 left (Q) .. +1 right (E): peek round a corner
    public float Lean { get; private set; }
    public bool externalInput;                   // AutoTest / touch controls drive the inputs
    [HideInInspector] public bool frozen;        // dead: no moving until the respawn
    [HideInInspector] public float combatUntil;  // kept for the weapon (sprint-to-fire)

    public float Speed { get; private set; }
    public bool Sprinting { get; private set; }
    public bool Tactical { get; private set; }
    public float SprintLock { get; private set; }  // seconds until the gun is up after a sprint
    public float AccelForward { get; private set; }// for the start/stop camera lean
    public float Strafe { get; private set; }      // -1..1 sideways speed, for the camera roll
    public float LandDip { get; private set; }     // camera dip after a landing
    public float StepPhase { get; private set; }   // radians, two steps per locomotion cycle

    CharacterController cc;
    Animator anim;
    Vector3 velocity;        // horizontal, world space
    float vy, prevFwd, tacT, lastTap = -1, landVel;
    bool sprintHeldBefore, wasGrounded = true;
    static readonly int VelX = Animator.StringToHash("VelX"), VelZ = Animator.StringToHash("VelZ"),
        Grounded = Animator.StringToHash("Grounded"), Crouch = Animator.StringToHash("Crouch"), Jump = Animator.StringToHash("Jump"),
        AnimSpeed = Animator.StringToHash("AnimSpeed");

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        cc.minMoveDistance = 0f;   // at high frame rates each step is under a millimetre; never drop it
        anim = GetComponentInChildren<Animator>();
        yaw = transform.eulerAngles.y;
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        if (frozen) { moveInput = Vector2.zero; sprint = false; jumpPressed = false; }
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        var input = Vector2.ClampMagnitude(moveInput, 1f);

        // sprint: forward only, not while aiming; it stands you up from a crouch
        if (sprint && input.y > 0.3f && crouch && !aiming) crouch = false;
        bool sprinting = sprint && !aiming && input.y > 0.3f && !crouch;
        // tactical sprint: double-tap Shift while sprinting
        if (sprint && !sprintHeldBefore) { if (Time.time - lastTap < 0.3f && sprinting) tacT = 6f; lastTap = Time.time; }
        sprintHeldBefore = sprint;
        if (!sprinting) tacT = 0; else tacT -= dt;
        bool tactical = tacT > 0;
        SprintLock = sprinting && Speed > 1 ? 0.22f : Mathf.Max(0, SprintLock - dt);
        if (sprinting) combatUntil = 0;

        // lean out from cover: not while sprinting; slower feet while leaning
        Lean = Mathf.MoveTowards(Lean, sprinting ? 0 : Mathf.Clamp(leanInput, -1, 1), dt / 0.18f);
        float top = crouch ? (aiming ? CrouchAimMove : CrouchMove) : aiming ? AimMove : tactical ? TacticalMove : sprinting ? SprintMove : walk ? WalkSpeed : JogSpeed;
        top *= 1f - 0.35f * Mathf.Abs(Lean);
        var wish = Quaternion.Euler(0, yaw, 0) * new Vector3(input.x, 0, input.y);
        if (wish.sqrMagnitude > 1e-4f) wish = wish.normalized * top * input.magnitude;
        float rate = !cc.isGrounded ? airControl : input.sqrMagnitude > 1e-4f ? accel : decel;
        velocity = Vector3.Lerp(velocity, wish, 1 - Mathf.Exp(-rate * dt));

        // gravity and jumping
        if (cc.isGrounded && vy < 0) vy = -2f;
        if (jumpPressed && cc.isGrounded && !crouch) { vy = Mathf.Sqrt(2f * -gravity * jumpHeight); anim?.SetTrigger(Jump); }
        jumpPressed = false;
        vy += gravity * dt;
        var before = transform.position;
        cc.Move((velocity + Vector3.up * vy) * dt);
        // keep only the speed we really got (slide along walls, no stored momentum)
        var moved = (transform.position - before) / dt; moved.y = 0;
        if (moved.magnitude < velocity.magnitude * 0.7f) velocity = Vector3.Lerp(velocity, moved, 0.5f);   // ran into a wall

        // landing: dip the camera by how hard we hit the ground
        if (cc.isGrounded && !wasGrounded && landVel < -3f) LandDip = Mathf.Max(-0.35f, landVel * 0.025f);
        LandDip = Mathf.Lerp(LandDip, 0, 1 - Mathf.Exp(-7 * dt));
        landVel = cc.velocity.y;
        wasGrounded = cc.isGrounded;

        // always face the camera direction (the 8-way mocap does the strafing / backpedal)
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0, yaw, 0), turnSpeed * dt);

        Speed = new Vector2(velocity.x, velocity.z).magnitude;
        Sprinting = sprinting && Speed > JogSpeed + 0.5f;
        Tactical = tactical && Sprinting;
        var local = transform.InverseTransformDirection(velocity);
        AccelForward = Mathf.Lerp(AccelForward, (local.z - prevFwd) / dt, 1 - Mathf.Exp(-12 * dt));
        prevFwd = local.z;
        Strafe = local.x / SprintMove;

        if (anim)
        {
            // the blend tree tops out at the sprint clip's speed; beyond that play it faster
            float clipZ = Mathf.Min(local.z, SprintSpeed);
            anim.SetFloat(VelX, local.x, 0.08f, dt);
            anim.SetFloat(VelZ, clipZ, 0.08f, dt);
            anim.SetFloat(AnimSpeed, local.z > SprintSpeed ? local.z / SprintSpeed : 1f);
            anim.SetBool(Grounded, cc.isGrounded);
            anim.SetBool(Crouch, crouch);
            var st = anim.GetCurrentAnimatorStateInfo(0);
            StepPhase = (st.normalizedTime % 1f) * Mathf.PI * 2f;
        }
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
            leanInput = (k.eKey.isPressed ? 1 : 0) - (k.qKey.isPressed ? 1 : 0);
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
