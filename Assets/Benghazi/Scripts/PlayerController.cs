using UnityEngine;
using UnityEngine.InputSystem;

// Third-person shooter movement, tuned to feel like the web prototype (main.js):
// the soldier always faces where the camera looks (strafe / backpedal with the 8-way mocap),
// jog 4.2 m/s, Shift = sprint 7.5 (forward only), double-tap Shift = tactical sprint 9.5,
// aiming 2.4, crouch 2.3, prone 1.0 (Z). Momentum eases toward the wanted speed (accel 11, decel 14).
// Above the sprint clip's own speed the animator plays faster so the feet stay planted.
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    // clip speeds (m/s) the blend tree is laid out with
    public const float WalkSpeed = 1.92f, RunSpeed = 4.8f, SprintSpeed = 7.2f, CrouchSpeed = 2.04f;
    // movement speeds (from the web game)
    public const float JogSpeed = 4.2f, SprintMove = 7.5f, TacticalMove = 9.5f, AimMove = 2.4f, CrouchMove = 2.04f, CrouchAimMove = 1.6f, ProneMove = 1.0f, ProneAimMove = 0.45f;
    public float accel = 11f, decel = 14f, airControl = 1.5f, jumpHeight = 1.1f, gravity = -22f, turnSpeed = 900f;

    [HideInInspector] public float yaw;          // look direction, set by the camera
    [HideInInspector] public Vector2 moveInput;  // -1..1, from keyboard/stick (or AutoTest)
    [HideInInspector] public bool sprint, walk, crouch, prone, jumpPressed, aiming;
    // how much the stance steadies the rifle: standing 1, kneeling 0.7, prone 0.4 (sway, recoil and spread scale with it)
    public float Unsteady => prone ? 0.4f : crouch ? 0.7f : 1f;
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
    public float Twist { get; private set; }       // degrees the eyes are turned away from the feet (the torso takes it up)
    float bodyYaw; bool stepping;
    // "-legacygait" on the command line: the old leg animation (blend by raw speed, feet turn with every
    // glance), kept so the automated test can measure old against new in the same build
    public static readonly bool LegacyGait = System.Environment.CommandLine.Contains("-legacygait");
    // "-calgait": park the leg blend on one clip set and play it at the measured pace (Gait below). Off by
    // default: the foot-slip measure could confirm it for walking only, not for running, so until someone
    // has judged it by eye the legs blend the way they always did
    public static readonly bool CalibratedGait = System.Environment.CommandLine.Contains("-calgait");
    // "-gaitscale 0.9": multiplies the playback rate, for the test to find the rate at which the feet hold
    public static readonly float GaitScale = ArgFloat("-gaitscale", 1f);
    static float ArgFloat(string name, float fallback)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i] == name && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        return fallback;
    }

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
        yaw = bodyYaw = transform.eulerAngles.y;
    }

    // The blend tree has a full set of clips laid out at walking pace, at running pace and (forwards) at
    // sprinting pace. Two things made the feet skate: half way between two sets the blended stride fits
    // neither, and the clips do not cover the ground their place in the tree says - measured on the rig
    // (AutoTest "clip pace": how fast the planted toe travels back under the body) a walk covers 1.82 m/s
    // where the tree has it at 1.92, a forward run 4.16 where the tree has 4.8, a sprint 6.37 for 7.2.
    // So the blend is parked on the nearest set and that set is played faster or slower by the ratio of the
    // body's real speed to the pace the clip really has: the planted foot then stays where it is put.
    // Returns the pace to feed the blend tree; rate = playback speed.
    static float Gait(float speed, Vector2 dir, bool crouched, out float rate)
    {
        rate = 1f;
        if (crouched) return speed < 0.5f ? speed : Mathf.Lerp(speed, CrouchSpeed, Mathf.InverseLerp(0.5f, 1.1f, speed));     // that state has no playback-speed control
        // measured paces, by direction of travel (dir is a unit vector in the body's frame: x right, y forward)
        float f = Mathf.Max(0, dir.y), bk = Mathf.Max(0, -dir.y); f *= f; bk *= bk; float sd = dir.x * dir.x;
        float walk = 1.82f * f + 1.73f * bk + 1.78f * sd, run = 4.16f * f + 4.28f * bk + 4.23f * sd, sprint = 6.37f;
        float g, real;
        if (speed < 0.6f) { g = speed; real = speed; }
        else if (speed < 1.2f) { float k = Mathf.InverseLerp(0.6f, 1.2f, speed); g = Mathf.Lerp(speed, WalkSpeed, k); real = Mathf.Lerp(speed, walk, k); }
        else if (speed < 2.6f) { g = WalkSpeed; real = walk; }
        else if (speed < 3.2f) { float k = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(2.6f, 3.2f, speed)); g = Mathf.Lerp(WalkSpeed, RunSpeed, k); real = Mathf.Lerp(walk, run, k); }
        else if (speed < 5.6f || dir.y < 0.5f) { g = RunSpeed; real = run; }                 // the sprint clips only exist going forwards
        else if (speed < 6.2f) { float k = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(5.6f, 6.2f, speed)); g = Mathf.Lerp(RunSpeed, SprintSpeed, k); real = Mathf.Lerp(run, sprint, k); }
        else { g = SprintSpeed; real = sprint; }
        rate = real < 0.3f ? 1f : Mathf.Clamp(speed / real * GaitScale, 0.5f, 1.7f);
        return g;
    }

    void Update()
    {
        if (!externalInput) ReadInput();
        if (frozen) { moveInput = Vector2.zero; sprint = false; jumpPressed = false; }
        // prone: a jump or a sprint gets you up first (to a knee, then standing), you cannot lean lying down
        if (prone && jumpPressed) { prone = false; crouch = true; jumpPressed = false; }
        if (prone && sprint && moveInput.y > 0.3f && !aiming) { prone = false; crouch = false; }
        if (prone) crouch = false;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        var input = Vector2.ClampMagnitude(moveInput, 1f);

        // sprint: forward only, not while aiming; it stands you up from a crouch
        if (sprint && input.y > 0.3f && crouch && !aiming) crouch = false;
        bool sprinting = sprint && !aiming && input.y > 0.3f && !crouch && !prone;
        // tactical sprint: double-tap Shift while sprinting
        if (sprint && !sprintHeldBefore) { if (Time.time - lastTap < 0.3f && sprinting) tacT = 6f; lastTap = Time.time; }
        sprintHeldBefore = sprint;
        if (!sprinting) tacT = 0; else tacT -= dt;
        bool tactical = tacT > 0;
        SprintLock = sprinting && Speed > 1 ? 0.22f : Mathf.Max(0, SprintLock - dt);
        if (sprinting) combatUntil = 0;

        // lean out from cover: not while sprinting; slower feet while leaning
        Lean = Mathf.MoveTowards(Lean, sprinting || prone ? 0 : Mathf.Clamp(leanInput, -1, 1), dt / 0.18f);
        float top = prone ? (aiming ? ProneAimMove : ProneMove) : crouch ? (aiming ? CrouchAimMove : CrouchMove) : aiming ? AimMove : tactical ? TacticalMove : sprinting ? SprintMove : walk ? WalkSpeed : JogSpeed;
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

        // Facing. On the move the feet follow the eyes (the 8-way mocap does the strafing / backpedal). Standing
        // still a man does not spin on the spot with every glance: the head and torso turn first, and only
        // past about 50 degrees do the feet step round to square up again.
        float offYaw = Mathf.DeltaAngle(bodyYaw, yaw);
        bool onTheMove = input.sqrMagnitude > 0.01f || velocity.sqrMagnitude > 0.25f || !cc.isGrounded || frozen;
        if (LegacyGait) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, yaw, turnSpeed * dt);
        else if (onTheMove) { stepping = false; bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, yaw, Mathf.Max(240f, Mathf.Abs(offYaw) * 14f) * dt); }
        else
        {
            if (Mathf.Abs(offYaw) > 50f) stepping = true;
            if (stepping) { bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, yaw, Mathf.Max(200f, Mathf.Abs(offYaw) * 6f) * dt); if (Mathf.Abs(Mathf.DeltaAngle(bodyYaw, yaw)) < 3f) stepping = false; }
        }
        Twist = Mathf.DeltaAngle(bodyYaw, yaw);
        transform.rotation = Quaternion.Euler(0, bodyYaw, 0);

        Speed = new Vector2(velocity.x, velocity.z).magnitude;
        Sprinting = sprinting && Speed > JogSpeed + 0.5f;
        Tactical = tactical && Sprinting;
        var local = transform.InverseTransformDirection(velocity);                    // for the legs: relative to the feet
        var view = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)) * velocity;          // for the camera and the rifle: relative to the eyes
        AccelForward = Mathf.Lerp(AccelForward, (view.z - prevFwd) / dt, 1 - Mathf.Exp(-12 * dt));
        prevFwd = view.z;
        Strafe = view.x / SprintMove;

        if (anim)
        {
            // the blend tree tops out at the sprint clip's speed; beyond that play it faster
            float ground = new Vector2(local.x, local.z).magnitude;
            var dir = ground > 0.05f ? new Vector2(local.x, local.z) / ground : Vector2.zero;
            float pace = Gait(ground, dir, crouch || prone, out float animRate);
            if (LegacyGait || !CalibratedGait) { pace = Mathf.Min(ground, SprintSpeed); animRate = local.z > SprintSpeed ? local.z / SprintSpeed : 1f; }
            anim.SetFloat(VelX, dir.x * pace, 0.08f, dt);
            anim.SetFloat(VelZ, dir.y * pace, 0.08f, dt);
            anim.SetFloat(AnimSpeed, animRate, 0.1f, dt);
            anim.SetBool(Grounded, cc.isGrounded);
            anim.SetBool(Crouch, crouch || prone);        // no prone clips yet: the body (and its shadow) stays down on a knee
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
            if (k.cKey.wasPressedThisFrame) { if (prone) { prone = false; crouch = true; } else crouch = !crouch; }
            if (k.zKey.wasPressedThisFrame) { prone = !prone; if (prone) crouch = false; }
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
