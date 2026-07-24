using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// WASD walking for the astronaut, on a <see cref="CharacterController"/> so stairs, slopes and
    /// ledges are handled by Unity's collide-and-slide rather than by physics we would have to tune.
    ///
    /// IMPORTANT — everything here runs on <c>Time.unscaledDeltaTime</c>. <see cref="SimulationManager"/>
    /// drives <c>Time.timeScale</c> up to 16x with the 1-5 speed keys to fast-forward the mowing run; if
    /// the astronaut used the scaled delta it would rocket across the biodome whenever you sped the
    /// tractor up. Walking must stay at a constant real-world pace no matter the sim speed.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class AstronautController : MonoBehaviour
    {
        // NOTE: AstronautSetup ("Add Astronaut & Balcony To Scene") is the source of truth for the
        // moon-walk feel — it (re)applies these values on the scene's components each run. The defaults
        // here match, so a freshly-added component behaves the same before the menu is ever run.

        [Header("Speed")]
        [Tooltip("A relaxed lunar stroll.")]
        [Min(0f)] public float walkSpeed = 1.9f;
        [Tooltip("Hold Left Shift. A brisk 'speed walk on the moon', not a sprint.")]
        [Min(0f)] public float runSpeed = 3.4f;
        [Tooltip("How fast the body turns to face the direction of travel (third-person only).")]
        [Min(0f)] public float rotationSpeedDeg = 720f;
        [Tooltip("Acceleration/damping on the ground speed, so starts and stops aren't instant.")]
        [Min(0.01f)] public float acceleration = 12f;

        [Header("Gravity (lunar)")]
        [Tooltip("Low, moon-like gravity: slow, floaty falls and a long hang time on jumps. Earth is ~-9.8, " +
                 "the Moon ~-1.6; -3.5 keeps it playable while still reading as low-g.")]
        public float gravity = -3.5f;
        [Tooltip("Downward velocity held while grounded. A small negative keeps the controller pressed " +
                 "into the ground so isGrounded stays true on stair treads and slopes.")]
        public float groundedStick = -2f;

        [Header("Jump")]
        [Tooltip("Press Space. With the low lunar gravity above this is a slow, floaty hop.")]
        public bool enableJump = true;
        [Tooltip("Launch velocity (m/s). Apex height ~= jumpSpeed^2 / (2 * |gravity|).")]
        [Min(0f)] public float jumpSpeed = 3.4f;

        [Header("Camera relationship")]
        [Tooltip("Optional. When set, WASD is interpreted relative to where the camera is looking " +
                 "(third-person). Leave empty for purely body-relative movement.")]
        public Transform cameraTransform;
        [Tooltip("Set by AstronautCameraRig. In first person the body is yawed by the mouse, so WASD " +
                 "is body-relative and the body must NOT auto-turn toward the travel direction.")]
        public bool bodyRelative = false;

        [Header("Input")]
        public bool enableInput = true;

        CharacterController _cc;
        float _velocityY;
        Vector3 _horizontalVelocity;

        /// <summary>Current horizontal ground speed in m/s. Read by <see cref="AstronautLocomotionVisual"/>.</summary>
        public float CurrentSpeed => _horizontalVelocity.magnitude;
        public bool IsMoving => CurrentSpeed > 0.05f;
        public bool IsGrounded => _cc != null && _cc.isGrounded;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            ApplyControllerTuning();
        }

        /// <summary>
        /// Dimensions that make stair climbing work. Step Offset must exceed the tallest riser the
        /// astronaut should mount, and Slope Limit must exceed the staircase ramp angle (~42 degrees
        /// for the placeholder flight) or the controller refuses to climb it.
        /// </summary>
        void ApplyControllerTuning()
        {
            _cc.slopeLimit = 50f;
            _cc.stepOffset = 0.35f;
            _cc.skinWidth = 0.02f;
            _cc.radius = 0.3f;
            _cc.height = 1.8f;
            _cc.center = new Vector3(0f, 0.9f, 0f);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;   // see class summary
            if (dt <= 0f) return;

            Vector2 input = ReadMoveInput();
            bool running = ReadRunInput();
            bool jumpPressed = ReadJumpInput();

            // ---- Desired horizontal velocity ----
            Vector3 wish = Vector3.zero;
            if (input.sqrMagnitude > 1e-4f)
            {
                Vector3 forward, right;
                if (!bodyRelative && cameraTransform != null)
                {
                    // Third person: WASD pushes the astronaut in screen-space directions.
                    forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
                    if (forward.sqrMagnitude < 1e-6f)                       // camera looking straight down
                        forward = Vector3.ProjectOnPlane(cameraTransform.up, Vector3.up);
                    forward.Normalize();
                    right = Vector3.Cross(Vector3.up, forward);
                }
                else
                {
                    // First person: the mouse yaws the body, so WASD is relative to the body itself.
                    forward = transform.forward;
                    right = transform.right;
                }

                wish = (forward * input.y + right * input.x);
                if (wish.sqrMagnitude > 1f) wish.Normalize();               // no diagonal speed bonus
                wish *= running ? runSpeed : walkSpeed;
            }

            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, wish, acceleration * dt);

            // ---- Face travel direction (third person only) ----
            if (!bodyRelative && _horizontalVelocity.sqrMagnitude > 1e-4f)
            {
                Quaternion look = Quaternion.LookRotation(_horizontalVelocity.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, rotationSpeedDeg * dt);
            }

            // ---- Gravity + jump ----
            bool grounded = _cc.isGrounded;
            if (grounded && _velocityY < 0f)
                _velocityY = groundedStick;                 // stick to the ground so isGrounded stays true
            if (enableJump && grounded && jumpPressed)
                _velocityY = jumpSpeed;                     // launch (overrides the stick this frame)
            if (!grounded || _velocityY > 0f)
                _velocityY += gravity * dt;                 // integrate the arc while rising or airborne

            Vector3 motion = _horizontalVelocity;
            motion.y = _velocityY;
            _cc.Move(motion * dt);

            // Landing: zero the accumulated fall speed so the next step isn't launched.
            if (_cc.isGrounded && _velocityY < groundedStick)
                _velocityY = groundedStick;
        }

        Vector2 ReadMoveInput()
        {
            if (!enableInput) return Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return Vector2.zero;
            float x = 0f, y = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            return new Vector2(x, y);
#else
            return Vector2.zero;
#endif
        }

        bool ReadRunInput()
        {
            if (!enableInput) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.leftShiftKey.isPressed;
#else
            return false;
#endif
        }

        bool ReadJumpInput()
        {
            if (!enableInput) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.spaceKey.wasPressedThisFrame;
#else
            return false;
#endif
        }

        /// <summary>Drop the astronaut at a position without fighting the CharacterController's own sweep.</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (_cc == null) _cc = GetComponent<CharacterController>();
            bool wasEnabled = _cc.enabled;
            _cc.enabled = false;                 // required: Move() and transform writes conflict otherwise
            transform.SetPositionAndRotation(position, rotation);
            _cc.enabled = wasEnabled;
            _velocityY = 0f;
            _horizontalVelocity = Vector3.zero;
        }
    }
}
