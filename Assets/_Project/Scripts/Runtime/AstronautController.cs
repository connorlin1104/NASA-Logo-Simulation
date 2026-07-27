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
        [Tooltip("Grace period after stepping off a ledge/stair during which a jump still fires, so " +
                 "leaving an edge doesn't 'eat' the jump. Seconds.")]
        [Min(0f)] public float coyoteTime = 0.12f;
        [Tooltip("Press Space up to this long BEFORE landing and the jump fires the instant you touch " +
                 "down, instead of being dropped. Seconds.")]
        [Min(0f)] public float jumpBufferTime = 0.12f;
        [Tooltip("Release Space while still rising to cut the hop short - tap for a low hop, hold for the " +
                 "full floaty arc.")]
        public bool variableJumpHeight = true;
        [Tooltip("Rising speed is multiplied by this the instant Space is released mid-jump. Lower = a " +
                 "bigger gap between a tap and a full hold.")]
        [Range(0.05f, 1f)] public float jumpCutMultiplier = 0.5f;

        [Header("Air control")]
        [Tooltip("How much WASD steers the astronaut mid-jump: 0 = none (commit to the arc), 1 = full " +
                 "ground control. Below 1 gives a heavier, more committed lunar hop.")]
        [Range(0f, 1f)] public float airControl = 0.55f;

        [Header("Input")]
        public bool enableInput = true;

        [Header("Character Controller tuning")]
        [Tooltip("Copy the cc* values below onto the CharacterController in Awake. Turn this off to " +
                 "hand-tune the CharacterController directly in the Inspector instead.")]
        public bool applyTuningOnAwake = true;
        [Tooltip("Must exceed the steepest walkable ramp (the stair ramps are ~42°).")]
        public float ccSlopeLimit = 50f;
        [Tooltip("Tallest ledge mounted without jumping, in the root's LOCAL units. Unity multiplies the " +
                 "whole capsule (this included) by the transform scale — keep the astronaut root at scale 1 " +
                 "(Tools > NASA Sim > Setup > Normalize Astronaut Scale) so these numbers mean world metres.")]
        public float ccStepOffset = 0.3f;
        public float ccSkinWidth = 0.02f;
        public float ccRadius = 0.3f;
        public float ccHeight = 1.8f;
        public Vector3 ccCenter = new Vector3(0f, 0.9f, 0f);

        CharacterController _cc;
        float _velocityY;
        Vector3 _horizontalVelocity;
        float _coyoteTimer;
        float _jumpBufferTimer;
        bool _jumpCutArmed;

        /// <summary>Current horizontal ground speed in m/s. Read by <see cref="AstronautLocomotionVisual"/>.</summary>
        public float CurrentSpeed => _horizontalVelocity.magnitude;
        public bool IsMoving => CurrentSpeed > 0.05f;
        public bool IsGrounded => _cc != null && _cc.isGrounded;
        /// <summary>Signed vertical velocity (m/s); positive = rising. Read by the visual for the jump pose.</summary>
        public float VerticalVelocity => _velocityY;
        /// <summary>Downward speed (m/s) at the instant of the latest touchdown, otherwise 0. Read for the landing squash.</summary>
        public float LandingImpact { get; private set; }

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (applyTuningOnAwake) ApplyControllerTuning();
        }

        /// <summary>
        /// Copy the serialized cc* dimensions onto the CharacterController. Step Offset must exceed the
        /// tallest riser the astronaut should mount, and Slope Limit must exceed the steepest stair ramp
        /// (~42°) or the controller refuses to climb it. The values live in serialized fields — not
        /// constants — so the setup tools and the scene own them; safe to call from editor tools too.
        /// </summary>
        public void ApplyControllerTuning()
        {
            if (_cc == null) _cc = GetComponent<CharacterController>();
            if (_cc == null) return;
            _cc.slopeLimit = ccSlopeLimit;
            _cc.stepOffset = ccStepOffset;
            _cc.skinWidth = ccSkinWidth;
            _cc.radius = ccRadius;
            _cc.height = ccHeight;
            _cc.center = ccCenter;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;   // see class summary
            if (dt <= 0f) return;

            // A disabled CharacterController means something else owns the body this frame — sitting in a
            // chair (AstronautSitting) or a Teleport mid-flight. Walking and gravity must stay out of it,
            // or they would drag the astronaut off the seat.
            if (_cc == null || !_cc.enabled) return;

            LandingImpact = 0f;

            Vector2 input = ReadMoveInput();
            bool running = ReadRunInput();
            bool jumpPressed = ReadJumpInput();
            bool jumpReleased = ReadJumpReleased();
            bool grounded = _cc.isGrounded;

            // ---- Desired horizontal velocity ----
            // First person is the only walking view: the mouse yaws the body (AstronautCameraRig), so
            // WASD is always relative to the body itself.
            Vector3 wish = Vector3.zero;
            if (input.sqrMagnitude > 1e-4f)
            {
                wish = (transform.forward * input.y + transform.right * input.x);
                if (wish.sqrMagnitude > 1f) wish.Normalize();               // no diagonal speed bonus
                wish *= running ? runSpeed : walkSpeed;
            }

            // Reduced steering in the air (airControl < 1) makes a jump feel committed rather than
            // twitchy, without touching the responsive feel on the ground.
            float accel = acceleration * (grounded ? 1f : Mathf.Clamp01(airControl));
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, wish, accel * dt);

            // ---- Gravity + jump (forgiving input) ----
            // Coyote time: keep the jump alive for a moment after the ground drops away.
            if (grounded) _coyoteTimer = coyoteTime;
            else _coyoteTimer = Mathf.Max(0f, _coyoteTimer - dt);

            // Jump buffer: remember a press briefly so one made just before landing still fires.
            if (jumpPressed) _jumpBufferTimer = jumpBufferTime;
            else _jumpBufferTimer = Mathf.Max(0f, _jumpBufferTimer - dt);

            if (grounded && _velocityY < 0f)
                _velocityY = groundedStick;                 // stick to the ground so isGrounded stays true

            if (enableJump && _jumpBufferTimer > 0f && _coyoteTimer > 0f)
            {
                _velocityY = jumpSpeed;                     // launch (overrides the stick this frame)
                _jumpBufferTimer = 0f;                      // consume the buffered press...
                _coyoteTimer = 0f;                          // ...and the coyote window, so it can't double-fire
                _jumpCutArmed = variableJumpHeight;         // this jump may be shortened by releasing Space
            }

            // Variable height: releasing Space while still rising ends the upward push early.
            if (_jumpCutArmed && jumpReleased && _velocityY > 0f)
            {
                _velocityY *= jumpCutMultiplier;
                _jumpCutArmed = false;
            }
            if (_velocityY <= 0f) _jumpCutArmed = false;    // past the apex there is nothing left to cut

            if (!grounded || _velocityY > 0f)
                _velocityY += gravity * dt;                 // integrate the arc while rising or airborne

            Vector3 motion = _horizontalVelocity;
            motion.y = _velocityY;

            float descentSpeed = _velocityY < 0f ? -_velocityY : 0f;
            _cc.Move(motion * dt);

            // Touchdown: report the impact for the landing squash, then zero the fall so the next step
            // isn't launched. 'grounded' is this frame's START state, so '!grounded && now grounded' is
            // exactly the landing frame; the >1 m/s floor ignores gentle slope contact.
            if (_cc.isGrounded)
            {
                if (!grounded && descentSpeed > 1f) LandingImpact = descentSpeed;
                if (_velocityY < groundedStick) _velocityY = groundedStick;
            }
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

        bool ReadJumpReleased()
        {
            if (!enableInput) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.spaceKey.wasReleasedThisFrame;
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
            _coyoteTimer = 0f;
            _jumpBufferTimer = 0f;
            _jumpCutArmed = false;
        }
    }
}
